using Godot;

namespace Refii.Core;

/// <summary>
/// A transform the guest states on its game tick — a body, the camera, a live effect — drawn on
/// Godot's clock: moved every frame by the motion the guest states, at the guest's own pace
/// (Engine_DrawAdvance), and steered toward the guest's own position at the world's draw point by
/// a bounded correction. The same rule
/// <see cref="Refii.Animations.Engine_ClipPlayhead"/> applies to clip time, so a body, the legs
/// walking it and the camera following it are one motion.
/// </summary>
/// <remarks>
/// THE GUEST'S POSITION IS A REFERENCE, NOT A SAMPLE. The guest draws `blend(prev, live, alpha)`
/// (`sub_822112D8`, a real slerp for the rotation); evaluated at
/// <see cref="Refii.Entities.Engine_DrawnFrame.DrawTick"/> that is where the thing should be.
/// Drawn directly, every read defect reached the screen: a read that took the next step under
/// this frame's stamp, a tick the walk missed. Drawn on a per-subject playhead anchored to when
/// the walk noticed a change, each body ran on its own approximation of the clock, a frame either
/// side of its limbs: feet skating on a walk. Measured in res://debug/anim_timing_lab.
///
/// Only a jump past <see cref="SnapMetres"/> or <see cref="SnapRadians"/> snaps; a teleport is
/// the caller's <see cref="Reset"/>.
/// </remarks>
public sealed partial class Engine_TickTimeline
{
	/// <summary>Game ticks per second (`dbl_83319660`).</summary>
	private const double TicksPerSecond = Engine_TickClock.GameTicksPerSecond;

	/// <summary>
	/// The step, in metres, past which a sample is a teleport rather than a movement — the
	/// timeline restarts on it instead of gliding across.
	/// </summary>
	/// <remarks>
	/// ONE THRESHOLD, BECAUSE THINGS THAT MOVE TOGETHER MUST RESTART TOGETHER. A body used 4 m
	/// and the camera following it used 8. A hero moved 5 m in a tick — a door, a cut, a
	/// scripted placement — and the two rules disagreed about what had just happened: the body
	/// restarted and was drawn at its new position at once, while the camera treated the same
	/// jump as travel and glided across it over the next tick and a half. For that tenth of a
	/// second the hero was somewhere the camera had not reached, which is the camera letting go
	/// of the player. 4 m in a 15 Hz tick is already 60 m/s and nothing runs there.
	/// </remarks>
	public const float TeleportMetres = 4f;

	/// <summary>How far past its newest stamp the reference runs on before it holds, in ticks.</summary>
	public const double Lead = Engine_PairPlayhead.Ceiling - 1.0;

	/// <summary>How fast an error is closed: the fraction left after one second is e^-Gain.</summary>
	private const double Gain = 14.0;

	/// <summary>The most the drawn motion may differ from the stated one while closing an error, as a fraction of it.</summary>
	private const double MaxCorrection = 1.0;

	/// <summary>Floors under that fraction, so something standing still still settles on its place.</summary>
	private const double MinSpeed = 1.5;

	private const double MinTurn = 1.5;

	private const double MinScalarRate = 1.0;

	/// <summary>Errors this large are a re-placement, taken at once.</summary>
	public const float SnapMetres = 1f;

	public const float SnapRadians = 0.7f;

	private const float SnapScalar = 10f;

	private bool _started;
	private readonly bool _stillWhenUnchanged;

	/// <param name="stillWhenUnchanged">
	/// True for a placement the guest writes on every tick it moves — a body, the camera: a read
	/// that finds it unchanged is the thing standing still. False for one written only on some
	/// ticks — a live effect, whose positioners (`sub_8322E4D0`, `sub_83230880`, the setter
	/// `sub_832325A8`) need not run each tick: there an unchanged read is no statement at all,
	/// the motion is taken between the last two changes, and the thing stops only once its next
	/// change is overdue. Read as "still", an effect riding a running hero stopped on every
	/// unwritten tick and fell behind him.
	/// </param>
	public Engine_TickTimeline(bool stillWhenUnchanged = true)
	{
		_stillWhenUnchanged = stillWhenUnchanged;
	}
	private Transform3D _previous;
	private float _previousScalar;
	private double _previousStamp;
	private double _stamp;
	private Quaternion _previousRotation;
	private Quaternion _newestRotation = Quaternion.Identity;
	private Vector3 _spinAxis = Vector3.Up;
	private float _spinAngle;
	private Vector3 _velocity;
	private double _spinRate;
	private double _scalarRate;
	private Vector3 _position;
	private Quaternion _rotation = Quaternion.Identity;
	private float _drawnScalar;
	private ulong _lastUsec;

	/// <summary>The newest stated transform.</summary>
	public Transform3D Newest { get; private set; }

	/// <summary>The newest stated companion value (the camera's field of view).</summary>
	public float NewestScalar { get; private set; }

	/// <summary>The companion value as last drawn.</summary>
	public float DrawnScalar => _drawnScalar;

	public bool Empty => !_started;

	public double NewestTick => _started ? _stamp : 0.0;

	/// <summary>Re-placements taken past the snap bounds; run total, for the debug channel.</summary>
	public static long Snaps;

	/// <summary>Frames on which a correction ran at its bound; run total.</summary>
	public static long Saturated;

	/// <summary>
	/// Where <paramref name="drawTick"/> falls between the last two stated samples: 0 at the
	/// previous, 1 at the newest and past it; 1 with nothing stated or no draw point.
	/// </summary>
	public double Phase(double drawTick)
	{
		if (!_started || double.IsNaN(drawTick))
		{
			return 1.0;
		}

		double span = _stamp - _previousStamp;
		return span > 0.0 ? System.Math.Clamp((drawTick - _previousStamp) / span, 0.0, 1.0) : 1.0;
	}

	/// <summary>Starts over on a sample — a teleport, a load, a stated snap, the first sight of a thing.</summary>
	public void Reset(double tick, Transform3D sample, float scalar = 0f)
	{
		_started = true;
		_previous = sample;
		_previousScalar = scalar;
		_previousStamp = tick - 1.0;
		_stamp = tick;
		Newest = sample;
		NewestScalar = scalar;
		_newestRotation = sample.Basis.GetRotationQuaternion();
		_previousRotation = _newestRotation;
		_spinAxis = Vector3.Up;
		_spinAngle = 0f;
		_velocity = Vector3.Zero;
		_spinRate = 0.0;
		_scalarRate = 0.0;
		_position = sample.Origin;
		_rotation = _previousRotation;
		_drawnScalar = scalar;
		_lastUsec = 0UL;
		ReleaseArrivals();
	}

	/// <summary>Resets this timeline to the empty state so reused timelines drop stale history.</summary>
	public void Clear()
	{
		_started = false;
		_lastUsec = 0UL;
		ReleaseArrivals();
	}

	/// <summary>
	/// Restates the whole history — both stated samples, the drawn transform and its motion — as
	/// turned rightward by <paramref name="rightDegrees"/> about the vertical through
	/// <paramref name="pivot"/>. See <see cref="TurnAbout(Vector3, Basis)"/>.
	/// </summary>
	public void TurnAbout(Vector3 pivot, double rightDegrees)
	{
		if (rightDegrees == 0.0 || !double.IsFinite(rightDegrees))
		{
			return;
		}

		TurnAbout(pivot, new Basis(Vector3.Up, -Mathf.DegToRad((float)rightDegrees)));
	}

	/// <summary>
	/// Restates the whole history — both stated samples, the drawn transform and its motion — as
	/// turned by <paramref name="turn"/> about <paramref name="pivot"/>. For a turn the newest
	/// statement carries that the history was drawn with by other means
	/// (<see cref="Engine_MouseLook"/>): the timeline then blends only the motion the source states
	/// of its own. The spin is held in the previous sample's own frame, so it is unchanged by a turn
	/// of the whole.
	/// </summary>
	public void TurnAbout(Vector3 pivot, Basis turn)
	{
		if (!_started || !pivot.IsFinite() || turn.IsEqualApprox(Basis.Identity) ||
			!turn.X.IsFinite() || !turn.Y.IsFinite() || !turn.Z.IsFinite())
		{
			return;
		}

		Quaternion rotation = turn.GetRotationQuaternion().Normalized();
		Transform3D Turned(Transform3D t) => new(turn * t.Basis, pivot + (turn * (t.Origin - pivot)));

		_previous = Turned(_previous);
		Newest = Turned(Newest);
		_previousRotation = (rotation * _previousRotation).Normalized();
		_newestRotation = (rotation * _newestRotation).Normalized();
		_position = pivot + (turn * (_position - pivot));
		_rotation = (rotation * _rotation).Normalized();
		_velocity = turn * _velocity;
		_arrivals?.TurnAbout(pivot, turn, rotation);
	}

	/// <summary>
	/// What the guest states on <paramref name="tick"/>, changed or not: an unchanged value is a
	/// thing standing still. The same tick again replaces the newest; an earlier one is a new world.
	/// </summary>
	public void Observe(double tick, Transform3D sample, float scalar = 0f)
	{
		if (!_started || tick < _stamp)
		{
			Reset(tick, sample, scalar);
			return;
		}

		if (tick == _stamp && sample.IsEqualApprox(Newest) && Mathf.IsEqualApprox(scalar, NewestScalar))
		{
			return;
		}

		if (!_stillWhenUnchanged && tick > _stamp && sample.IsEqualApprox(Newest) && scalar == NewestScalar)
		{
			return;  // not rewritten this tick: no statement
		}

		if (ArrivalFit && _stillWhenUnchanged) ObserveArrival(sample, scalar);  // Engine_TickTimeline.ArrivalFit.cs

		if (tick > _stamp)
		{
			// A move after ticks nobody read starts at the tick before it: the thing was where it
			// last was until then (the guest latches a placement only when it changes, and the
			// walk skips what it found unchanged). For a source written only on some ticks, the
			// gap is judged against its own last interval.
			double gapLimit = _stillWhenUnchanged ? 4.0 : (2.0 * System.Math.Max(_stamp - _previousStamp, 1.0)) + 1.0;
			_previousStamp = tick - _stamp > gapLimit ? tick - 1.0 : _stamp;
			_previous = Newest;
			_previousScalar = NewestScalar;
			_stamp = tick;
		}

		Newest = sample;
		NewestScalar = scalar;
		_newestRotation = sample.Basis.GetRotationQuaternion();
		_previousRotation = _previous.Basis.GetRotationQuaternion();
		Quaternion delta = (_previousRotation.Inverse() * _newestRotation).Normalized();
		if (delta.W < 0f)
		{
			delta = -delta;
		}

		double seconds = (_stamp - _previousStamp) / TicksPerSecond;
		_spinAngle = delta.GetAngle();
		_spinAxis = _spinAngle > 1e-6f && !delta.GetAxis().IsZeroApprox()
			? delta.GetAxis().Normalized()
			: Vector3.Up;
		_velocity = (sample.Origin - _previous.Origin) / (float)seconds;
		_spinRate = _spinAngle / seconds;
		_scalarRate = (scalar - _previousScalar) / seconds;
	}

	/// <summary>Constructs an angle-axis quaternion safely without throwing when unnormalized or near-zero.</summary>
	private static Quaternion SafeAngleAxis(Vector3 axis, float angle)
	{
		if (Mathf.Abs(angle) < 1e-6f || axis.IsZeroApprox())
		{
			return Quaternion.Identity;
		}

		return new Quaternion(axis.Normalized(), angle);
	}

	/// <summary>Whether the drawn transform has come to rest on the newest stated one.</summary>
	public bool Settled =>
		_velocity == Vector3.Zero && _spinAngle <= 1e-6f && _scalarRate == 0.0 &&
		_position == Newest.Origin && _rotation.AngleTo(_newestRotation) < 1e-3f &&
		_drawnScalar == NewestScalar;

	/// <summary>Moves the drawn transform to this frame and answers it.</summary>
	public Transform3D Advance(ulong nowUsec, double drawTick)
	{
		if (!_started)
		{
			return Newest;
		}

		if (_lastUsec != 0UL && nowUsec == _lastUsec)
		{
			return new Transform3D(new Basis(_rotation) * Basis.FromScale(Newest.Basis.Scale), _position);
		}

		if (TryAdvanceArrival(nowUsec, out Transform3D fitted)) return fitted;  // Engine_TickTimeline.ArrivalFit.cs

		double seconds = _lastUsec == 0UL || nowUsec <= _lastUsec
			? 0.0
			: System.Math.Min((nowUsec - _lastUsec) * 1e-6, 0.25);
		_lastUsec = nowUsec;

		// Moved by the frame's time at the guest's own pace — slower than real time under load —
		// and the stated motion (Engine_DrawAdvance).
		double span = _stamp - _previousStamp;
		double maxSpan = System.Math.Max(span, 1.0);

		// A source written only on some ticks has stopped once its next change is overdue: past
		// half its own interval beyond where that change was due, plus the draw latency's tick.
		bool overdue = !double.IsNaN(drawTick) &&
			drawTick - _stamp > (_stillWhenUnchanged ? maxSpan + Lead : (1.5 * maxSpan) + 1.0);
		if (overdue)
		{
			_previous = Newest;
			_previousRotation = _newestRotation;
			_previousScalar = NewestScalar;
			_velocity = Vector3.Zero;
			_spinAngle = 0f;
			_spinRate = 0.0;
			_scalarRate = 0.0;
		}
		double stated = overdue ? 0.0 : Engine_DrawAdvance.Ticks(seconds, nowUsec, drawTick) / TicksPerSecond;
		_position += _velocity * (float)stated;
		if (_spinRate > 0.0)
		{
			_rotation = (_rotation * SafeAngleAxis(_spinAxis, (float)(_spinRate * stated))).Normalized();
		}

		_drawnScalar += (float)(_scalarRate * stated);

		// The reference: the guest's blend of its pair at the draw point — run back along the
		// segment by up to a tick when the shared playhead's floor holds the draw point before it,
		// and forward by up to Lead (0.5) so a 2-tick (7.5 Hz) mover reaches alpha=1.5 on the
		// intermediate tick before seamlessly handing off to alpha=0.5 on the next 2-tick pair.
		double alpha = double.IsNaN(drawTick) || overdue
			? 1.0
			: System.Math.Clamp((drawTick - _previousStamp) / span, -1.0 / span, 1.0 + Lead);
		Vector3 target = _previous.Origin.Lerp(Newest.Origin, (float)alpha);
		Quaternion turn = _spinAngle > 1e-6f
			? _previousRotation * SafeAngleAxis(_spinAxis, (float)(_spinAngle * alpha))
			: _newestRotation;
		float scalarTarget = Mathf.Lerp(_previousScalar, NewestScalar, (float)alpha);

		double pull = 1.0 - System.Math.Exp(-Gain * seconds);
		Vector3 error = target - _position;
		if (error.Length() > SnapMetres)
		{
			++Snaps;
			_position = target;
		}
		else
		{
			Vector3 step = error * (float)pull;
			float limit = (float)(MaxCorrection * System.Math.Max(_velocity.Length(), MinSpeed) * seconds);
			if (step.Length() > limit)
			{
				step = step.Normalized() * limit;
				++Saturated;
			}

			_position += step;
		}

		float off = _rotation.AngleTo(turn);
		if (off > SnapRadians)
		{
			++Snaps;
			_rotation = turn;
		}
		else if (off > 1e-6f)
		{
			double limit = MaxCorrection * System.Math.Max(_spinRate, MinTurn) * seconds;
			double t = off * pull > limit ? limit / off : pull;
			_rotation = _rotation.Slerp(turn, (float)t).Normalized();
		}

		float scalarError = scalarTarget - _drawnScalar;
		if (Mathf.Abs(scalarError) > SnapScalar)
		{
			_drawnScalar = scalarTarget;
		}
		else
		{
			float step = scalarError * (float)pull;
			float limit = (float)(MaxCorrection * System.Math.Max(System.Math.Abs(_scalarRate), MinScalarRate) * seconds);
			_drawnScalar += Mathf.Clamp(step, -limit, limit);
		}

		// Settle exactly on a thing that stopped: no motion stated, and within a hair of its place.
		// The corrections approach their target and never reach it, so without this a body that
		// once moved would be re-placed every frame for ever.
		if (_velocity == Vector3.Zero && (Newest.Origin - _position).LengthSquared() < 1e-6f)
		{
			_position = Newest.Origin;
		}

		if (_spinAngle <= 1e-6f && _rotation.AngleTo(_newestRotation) < 1e-3f)
		{
			_rotation = _newestRotation;
		}

		if (_scalarRate == 0.0 && Mathf.Abs(scalarTarget - _drawnScalar) < 1e-4f)
		{
			_drawnScalar = scalarTarget;
		}

		return new Transform3D(new Basis(_rotation) * Basis.FromScale(Newest.Basis.Scale), _position);
	}
}
