using System.Collections.Generic;
using Godot;
using static Refii.Camera.Engine_CameraStatements;

namespace Refii.Camera;

/// <summary>
/// THE CAMERA, REBUILT FROM WHAT THE GUEST STATES. Every camera component's state is followed
/// from its setters' arguments and its easing step's inputs, by the guest's own law, so the block
/// every guest reader sees (position, up, forward, horizontal FOV, cut) is known without reading it.
/// </summary>
/// <remarks>
/// <para>THE LAW (both titles; Fable 2 offsets, Fable 3's in camera_statements.cpp). A component
/// holds a target position (+0x90), target and current orientation (+0xA0, +0xB0), target and
/// current roll, FOV, the factor and the interpolation counters. Its entity holds the drawn
/// position and orientation. The easing step, with factor f as it starts:
/// position = entity + (target − entity)·f, plus the shake's random offset (stated) or the repulsion's
/// kept point (stated); roll lerps; while a repulsion radius stands the orientation target is
/// re-aimed at its point and f is taken four times; current orientation = f ≥ 1 ? target :
/// slerp(current, target, f); its up and forward rows (turned by the shake, stated) make the entity's
/// orientation with the roll about forward applied; FOV lerps; then n counts down and f becomes
/// (total − n) / total, or 1 (and the radius 0) when n is 0.</para>
/// <para>THE SETTERS. MoveTo sets the target position, MoveToCut the entity's too; SetDirection
/// and SetTarget (towards the point from the entity) aim the target against world Z, their Cut forms
/// the current orientation and the entity's too; SetAngles builds the target from degrees and turns
/// it about forward by the current roll × 2π (sic, the binary's `fmuls f1, roll, 6.2831855`);
/// SetAnglesCut builds it without that turn; SetFOV / SetRoll set the target (Cut: both);
/// InterpolateFor sets the counters (f 0, or 1 for no ticks); SetCutThisFrame the cut byte.</para>
/// <para>WHICH CAMERA A MANAGER SHOWS: the component its update eased with the active flag. The
/// statements of one update come in order on the guest's thread and end with its push.</para>
/// </remarks>
public sealed class Engine_CameraRig
{
	/// <summary>Degrees to radians as the guest's setters multiply (`flt_82100ED4`).</summary>
	public const float DegreesToRadians = 0.017453292f;

	/// <summary>The component constructor's FOV, 70° (`flt_82100EFC`).</summary>
	public const float DefaultFov = 1.2217305f;

	/// <summary>One camera component as the statements leave it.</summary>
	public sealed class Component
	{
		public uint Address;
		public uint Entity;
		public uint Manager;
		public Vector3 Target;
		public Vector3 Position;
		public Engine_CameraQuat TargetOrientation = Engine_CameraQuat.Identity;
		public Engine_CameraQuat Current = Engine_CameraQuat.Identity;
		public Engine_CameraQuat Orientation = Engine_CameraQuat.Identity;
		public float RollTarget;
		public float Roll;
		public float FovTarget = DefaultFov;
		public float Fov = DefaultFov;
		public float Factor;
		public int Remaining;
		public int Total;
		public float Shake;
		public float LastShake;
		public bool Cut;
		public Vector3 RepulsionPoint;
		public float RepulsionRadius;

		/// <summary>The position is known: a MoveToCut, or a stated target eased at a factor of 1.</summary>
		public bool PositionKnown;

		/// <summary>The orientation is known: a stated direction or angles taken by a cut or a full step.</summary>
		public bool OrientationKnown;

		/// <summary>A target position / orientation has been stated since the component was first seen.</summary>
		public bool TargetStated;
		public bool TargetOrientationStated;

		/// <summary>Steps whose stated factor or cut byte differed from the followed one.</summary>
		public int FactorDisagreements;
		public int CutDisagreements;

		public Block Block => new(Position, Orientation.Up, Orientation.Forward, Fov, Cut, LastShake,
			PositionKnown && OrientationKnown);
	}

	/// <summary>The camera block, as the guest's getter builds it.</summary>
	public readonly record struct Block(
		Vector3 Position, Vector3 Up, Vector3 Forward, float HorizontalFov, bool Cut, float Shake, bool Known);

	/// <summary>One camera manager: the component it shows and the blocks of its last two pushes.</summary>
	public sealed class Manager
	{
		public uint Address;
		public Component? Active;
		public byte Player;
		public bool Drawn;
		public uint Pushes;
		public uint LastPushFrame;
		public uint Activation;
		public Block? Live;
		public Block? Previous;
		public Block? LastGuestBlock;
	}

	/// <summary>A guest block stated against the rebuilt state at that point in the guest's order.</summary>
	public readonly record struct Observation(
		uint Sequence, uint GuestFrame, uint Manager, uint Entity, uint Activation,
		Block Guest, Block? Rebuilt, bool AtPush);

	private readonly Dictionary<uint, Component> _components = new();
	private readonly Dictionary<uint, Manager> _managers = new();
	private readonly List<(Component Component, bool Active)> _eased = new();
	private uint _snapManager;
	private Statement _previous;
	private uint _lastSequence;

	private Component? _stepComponent;
	private Statement _stepBegin;
	private readonly List<Statement> _stepNested = new();

	/// <summary>Records lost to the ring (a sequence gap), as a count of gaps.</summary>
	public int Gaps { get; private set; }

	public uint LastSequence => _lastSequence;

	public IReadOnlyDictionary<uint, Manager> Managers => _managers;

	public IReadOnlyDictionary<uint, Component> Components => _components;

	/// <summary>Called for every stated guest block, and for every push, with the rebuild beside it.</summary>
	public System.Action<Observation>? Observed;

	/// <summary>Called after a manager's push has rebuilt its block.</summary>
	public System.Action<Manager>? Pushed;

	public void Apply(in Statement statement)
	{
		if (_lastSequence != 0 && statement.Sequence > _lastSequence + 1)
		{
			Forget();
		}

		_lastSequence = statement.Sequence;

		if (_stepComponent is not null && statement.Depth > _stepBegin.Depth)
		{
			_stepNested.Add(statement);
			_previous = statement;
			return;
		}

		switch (statement.Call)
		{
			case Call.EaseBegin:
				_stepComponent = ComponentAt(statement.Component);
				_stepComponent.Entity = statement.Entity;
				_stepBegin = statement;
				_stepNested.Clear();
				break;
			case Call.EaseEnd:
				if (_stepComponent is { } stepped && stepped.Address == statement.Component)
				{
					Step(stepped, _stepBegin, _stepNested);
					Note(stepped, (_stepBegin.Word & 0xFFu) != 0u);
				}

				_stepComponent = null;
				_stepNested.Clear();
				break;
			case Call.Push:
				Push(statement);
				break;
			case Call.SnapBegin:
				_snapManager = statement.Manager;
				break;
			case Call.SnapEnd:
				_snapManager = 0;
				break;
			case Call.Activate:
				ManagerAt(statement.Manager).Activation = statement.Word;
				break;
			case Call.GuestBlock:
				Observe(statement);
				break;
			default:
				if (!Covered(statement, _previous))
				{
					Set(ComponentAt(statement.Component), statement);
				}

				break;
		}

		_previous = statement;
	}

	/// <summary>A setter one level out from a setter it called on the same component is that call's
	/// wrapper (SetTarget around the direction body): the inner, exact, statement stands.</summary>
	private static bool Covered(in Statement statement, in Statement previous) =>
		previous.Depth == statement.Depth + 1 && previous.Component == statement.Component &&
		previous.Call is Call.SetDirection or Call.SetDirectionCut &&
		statement.Call is Call.SetTarget or Call.SetTargetCut;

	/// <summary>After a gap every followed value may be stale until restated.</summary>
	private void Forget()
	{
		++Gaps;
		foreach (Component component in _components.Values)
		{
			component.PositionKnown = false;
			component.OrientationKnown = false;
			component.TargetStated = false;
			component.TargetOrientationStated = false;
		}

		_eased.Clear();
		_stepComponent = null;
		_stepNested.Clear();
	}

	private Component ComponentAt(uint address)
	{
		if (!_components.TryGetValue(address, out Component? component))
		{
			component = new Component { Address = address };
			_components[address] = component;
		}

		return component;
	}

	private Manager ManagerAt(uint address)
	{
		if (!_managers.TryGetValue(address, out Manager? manager))
		{
			manager = new Manager { Address = address };
			_managers[address] = manager;
		}

		return manager;
	}

	private static void Set(Component c, in Statement s)
	{
		switch (s.Call)
		{
			case Call.MoveTo:
				c.Target = s.Vector(0);
				c.TargetStated = true;
				break;
			case Call.MoveToCut:
				c.Target = s.Vector(0);
				c.TargetStated = true;
				c.Position = c.Target;
				c.PositionKnown = true;
				break;
			case Call.SetDirection:
				Aim(c, Engine_CameraQuat.FromDirection(s.Vector(0)));
				break;
			case Call.SetDirectionCut:
				Aim(c, Engine_CameraQuat.FromDirection(s.Vector(0)));
				CutOrientation(c);
				break;
			case Call.SetTarget:
				Aim(c, Engine_CameraQuat.FromDirection(s.Vector(0) - c.Position));
				break;
			case Call.SetTargetCut:
				Aim(c, Engine_CameraQuat.FromDirection(s.Vector(0) - c.Position));
				CutOrientation(c);
				break;
			case Call.SetAngles:
			{
				(Vector3 up, Vector3 forward) = Engine_CameraQuat.FromAngles(
					s.Values[0] * DegreesToRadians, s.Values[1] * DegreesToRadians, s.Values[2] * DegreesToRadians);
				Engine_CameraQuat turn = Engine_CameraQuat.AxisAngle(forward, c.Roll * 6.2831855f);
				Aim(c, Engine_CameraQuat.FromUpForward(turn.Rotate(up), turn.Rotate(forward)));
				break;
			}
			case Call.SetAnglesCut:
			{
				(Vector3 up, Vector3 forward) = Engine_CameraQuat.FromAngles(
					s.Values[0] * DegreesToRadians, s.Values[1] * DegreesToRadians, s.Values[2] * DegreesToRadians);
				Aim(c, Engine_CameraQuat.FromUpForward(up, forward));
				CutOrientation(c);
				break;
			}
			case Call.SetFov:
				c.FovTarget = s.Values[0] * DegreesToRadians;
				break;
			case Call.SetFovCut:
				c.FovTarget = s.Values[0] * DegreesToRadians;
				c.Fov = c.FovTarget;
				break;
			case Call.SetRoll:
				c.RollTarget = s.Values[0] * DegreesToRadians;
				break;
			case Call.SetRollCut:
				c.RollTarget = s.Values[0] * DegreesToRadians;
				c.Roll = c.RollTarget;
				break;
			case Call.AddShake:
				c.Shake += s.Values[0];
				break;
			case Call.SetShake:
				c.Shake = s.Values[0];
				break;
			case Call.StopShake:
				c.Shake = 0f;
				break;
			case Call.InterpolateFor:
			{
				int ticks = unchecked((int)s.Word);
				c.Remaining = ticks;
				c.Total = ticks;
				c.Factor = ticks <= 0 ? 1f : 0f;
				break;
			}
			case Call.SetCutThisFrame:
				c.Cut = (s.Word & 0xFFu) != 0u;
				break;
			case Call.SetRepulsion:
				c.RepulsionPoint = s.Vector(0);
				c.RepulsionRadius = s.Values[3];
				break;
		}
	}

	private static void Aim(Component c, Engine_CameraQuat target)
	{
		c.TargetOrientation = target;
		c.TargetOrientationStated = true;
	}

	/// <summary>A Cut form: current = target, and the entity takes it as it stands (no roll; the step
	/// that follows applies it).</summary>
	private static void CutOrientation(Component c)
	{
		c.Current = c.TargetOrientation;
		c.Orientation = c.Current;
		c.OrientationKnown = c.TargetOrientationStated;
	}

	/// <summary>The easing step, by the law, on the inputs it stated.</summary>
	private static void Step(Component c, in Statement begin, List<Statement> nested)
	{
		float f = begin.Values[0];
		float shake = begin.Values[1];
		float radius = begin.Values[2];
		bool cut = begin.Values[3] != 0f;
		if (System.MathF.Abs(f - c.Factor) > 1e-6f)
		{
			++c.FactorDisagreements;
		}

		if (cut != c.Cut)
		{
			++c.CutDisagreements;
		}

		c.Factor = f;
		c.Cut = cut;
		c.RepulsionRadius = radius;

		Vector3 position = new(
			(c.Target.X - c.Position.X) * f + c.Position.X,
			(c.Target.Y - c.Position.Y) * f + c.Position.Y,
			(c.Target.Z - c.Position.Z) * f + c.Position.Z);
		foreach (Statement n in nested)
		{
			if (n.Call == Call.ShakePosition)
			{
				position += n.Vector(0);
			}
			else if (n.Call == Call.Repulsion)
			{
				position = n.Vector(3);
			}
		}

		c.Position = position;
		c.PositionKnown |= f >= 1f && c.TargetStated;

		c.Roll = f >= 1f ? c.RollTarget : (c.RollTarget - c.Roll) * f + c.Roll;

		Statement previous = default;
		foreach (Statement n in nested)
		{
			if (n.Call is >= Call.MoveTo and <= Call.SetRepulsion && !Covered(n, previous))
			{
				Set(c, n);
			}

			previous = n;
		}

		float turnFactor = radius != 0f ? f * 4f : f;
		c.Current = turnFactor >= 1f
			? c.TargetOrientation
			: Engine_CameraQuat.Slerp(c.Current, c.TargetOrientation, turnFactor);
		c.OrientationKnown |= turnFactor >= 1f && c.TargetOrientationStated;

		Vector3 up = c.Current.Up;
		Vector3 forward = c.Current.Forward;
		foreach (Statement n in nested)
		{
			if (n.Call == Call.ShakeRotation)
			{
				up = n.Vector(6);
				forward = n.Vector(9);
			}
		}

		c.Orientation = Engine_CameraQuat.FromStepRows(up, forward) *
			Engine_CameraQuat.AxisAngle(Engine_CameraQuat.AxisY, c.Roll);
		c.Fov = (c.FovTarget - c.Fov) * f + c.Fov;
		c.LastShake = shake;
		c.Shake = 0f;

		if (c.Remaining != 0)
		{
			--c.Remaining;
			c.Factor = c.Total == 0 ? 1f : (float)(c.Total - c.Remaining) / c.Total;
		}
		else
		{
			c.Factor = 1f;
			c.RepulsionRadius = 0f;
		}
	}

	private void Note(Component component, bool active)
	{
		if (_snapManager != 0)
		{
			component.Manager = _snapManager;
			if (active)
			{
				ManagerAt(_snapManager).Active = component;
			}

			return;
		}

		_eased.Add((component, active));
	}

	private void Push(in Statement s)
	{
		Manager manager = ManagerAt(s.Manager);
		foreach ((Component component, bool active) in _eased)
		{
			component.Manager = manager.Address;
			if (active)
			{
				manager.Active = component;
			}
		}

		_eased.Clear();
		manager.Player = s.Player;
		manager.Drawn = (s.Flags & FlagDrawn) != 0;
		manager.Previous = manager.Live;
		manager.Live = manager.Active?.Block;
		manager.LastPushFrame = s.GuestFrame;
		++manager.Pushes;

		if (manager.LastGuestBlock is { } readers)
		{
			Observed?.Invoke(new Observation(s.Sequence, s.GuestFrame, manager.Address,
				manager.Active?.Entity ?? 0u, manager.Activation, readers, manager.Live, AtPush: true));
		}

		Pushed?.Invoke(manager);
	}

	private void Observe(in Statement s)
	{
		var guest = new Block(s.Vector(0), s.Vector(3), s.Vector(6), s.Values[9], (s.Word & 0xFFu) != 0u,
			s.Values[10], true);
		Manager manager = ManagerAt(s.Manager);
		manager.LastGuestBlock = guest;

		Block? rebuilt = null;
		foreach (Component component in _components.Values)
		{
			if (component.Entity != 0u && component.Entity == s.Entity)
			{
				rebuilt = component.Block;
				break;
			}
		}

		Observed?.Invoke(new Observation(s.Sequence, s.GuestFrame, s.Manager, s.Entity, manager.Activation,
			guest, rebuilt, AtPush: false));
	}
}
