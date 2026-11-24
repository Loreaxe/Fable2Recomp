using System.Collections.Generic;
using Refii.Core;

namespace Refii.Ui;

/// <summary>
/// THE SLIDES PLAY: a screen's components run their slides' animation tracks over real time, as
/// the guest's presentation does, so a book that stands on its file moves the way the console's
/// screen moves. Everything here is the guest's contract (TU1):
///
/// <list type="bullet">
/// <item>ENTRY (`sub_82BB5760` → `sub_82BB6408`): the master's entries, then the slide's — each
/// entry's commands are attribute writes and its kind-0 switches turn tracks on or OFF
/// (`sub_82BB6E00`, no bulk clear). Time restarts at 0; the record's flags byte gives the pass
/// count (bits 0-1, 0 = forever), paused (bit 2), ping-pong (bit 3) and go-next (bit 4)
/// (`sub_82BB3778`, `sub_82BB5C20`).</item>
/// <item>LIVENESS (`sub_822849A0` 0x82284ae4..b38): a component that comes live — its subtree
/// drawn again, its time window open — enters its slide again from t = 0. The walk itself
/// enters WITHOUT play, so an authored-paused slide waits at 0 until the screen's code plays it
/// (`sub_82426CA8`, `COMMAND_PLAY`); whether an entry plays is the caller's statement.</item>
/// <item>THE CLOCK (`sub_822BE810`, `sub_82284DF8`): milliseconds of real time since entry,
/// frozen while paused. At the end: N passes then HOLD at the slide's length; ping-pong bounces
/// and holds at the length on an odd count, at 0 on an even one; 0 passes loops. A completed
/// go-next slide hands over to the next one (`sub_82284D50` → `COMMAND_GOTONEXTSLIDE`,
/// `sub_82BB59F0`, clamped to the last).</item>
/// <item>TRACKS (`sub_822846B0`): every active track writes its attribute every frame at its
/// target's INHERITED time — the clock of the nearest component strictly above the target (the
/// root's own tracks run on the presentation's clock). The value persists after the track
/// stops, until a slide command writes that attribute.</item>
/// <item>WINDOWS (0x82284a5c, 0x82284a9c): an element whose inherited time is outside its
/// `timeoffset`..`endtime` is not live, and nor is its subtree.</item>
/// </list>
///
/// Not reproduced: the live-value rebase of bit-30 tracks (`sub_82BB7038`) — no shipped file
/// sets the bit. Event actions (kind ≠ 0 switches) are the presentation's logic, not tracks.
/// </summary>
public sealed class Engine_UiSlidePlayback
{
	public enum Channel
	{
		PositionX, PositionY, PositionZ, ScaleX, ScaleY, ScaleZ,
		RotationX, RotationY, RotationZ, PivotX, PivotY, PivotZ, Opacity,
	}

	public const int Channels = 13;

	private static readonly Dictionary<uint, Channel> ChannelById = new()
	{
		[Engine_GameHash.AttributeId("position.x")] = Channel.PositionX,
		[Engine_GameHash.AttributeId("position.y")] = Channel.PositionY,
		[Engine_GameHash.AttributeId("position.z")] = Channel.PositionZ,
		[Engine_GameHash.AttributeId("scale.x")] = Channel.ScaleX,
		[Engine_GameHash.AttributeId("scale.y")] = Channel.ScaleY,
		[Engine_GameHash.AttributeId("scale.z")] = Channel.ScaleZ,
		[Engine_GameHash.AttributeId("rotation.x")] = Channel.RotationX,
		[Engine_GameHash.AttributeId("rotation.y")] = Channel.RotationY,
		[Engine_GameHash.AttributeId("rotation.z")] = Channel.RotationZ,
		[Engine_GameHash.AttributeId("pivot.x")] = Channel.PivotX,
		[Engine_GameHash.AttributeId("pivot.y")] = Channel.PivotY,
		[Engine_GameHash.AttributeId("pivot.z")] = Channel.PivotZ,
		[Engine_GameHash.AttributeId("opacity")] = Channel.Opacity,
	};

	/// <summary>The attribute id a channel writes (the inverse of the playback's channel map).</summary>
	public static uint AttributeOf(Channel channel)
	{
		foreach ((uint id, Channel c) in ChannelById)
		{
			if (c == channel)
			{
				return id;
			}
		}

		return 0u;
	}

	private static readonly uint TimeOffsetId = Engine_GameHash.AttributeId("timeoffset");
	private static readonly uint EndTimeId = Engine_GameHash.AttributeId("endtime");

	/// <summary>One component's time block.</summary>
	private sealed class Clock
	{
		/// <summary>The slide playing, in the guest's numbering (1 past the master); 0 for none.</summary>
		public int Slide;

		/// <summary>The slide last stated, so a restatement is not a new entry.</summary>
		public int Stated;

		public bool Live;
		public long Start;
		public int Frozen;
		public bool Paused;
		public int Duration;
		public int Passes;
		public bool PingPong;
		public bool Next;

		/// <summary>This frame's time.</summary>
		public int Time;

		/// <summary>The statement sequence when the component last came live: anything older the walk's entry superseded.</summary>
		public ulong LiveSince;

		/// <summary>The last told entry and play state applied (Engine_UiSlideStatements).</summary>
		public ulong Told;

		public ulong ToldPause;

		public int Raw(long now) => Paused ? Frozen : (int)System.Math.Min(now - Start, int.MaxValue);

		public void SetPaused(bool paused, long now)
		{
			if (paused == Paused)
			{
				return;
			}

			if (paused)
			{
				Frozen = Raw(now);
			}
			else
			{
				Start = now - Frozen;
			}

			Paused = paused;
		}
	}

	private readonly Engine_UiScreen _screen;
	private readonly BgfAnimation[] _tracks;
	private readonly Channel?[] _trackChannel;
	private readonly bool[] _trackOn;

	/// <summary>Per element: its non-master slides in file order, or null where it owns none.</summary>
	private readonly List<BgfSlide>?[] _owned;

	private readonly BgfSlide?[] _master;
	private readonly Clock?[] _clocks;

	/// <summary>Per element: the nearest slide owner strictly above it, -1 for the presentation's clock.</summary>
	private readonly int[] _clockOf;

	/// <summary>Elements that carry a time window in their own attributes or in any slide's commands.</summary>
	private readonly int[] _windowed;

	private readonly float[] _values;
	private readonly bool[] _closed;
	private readonly int[] _slides;
	private long _opened = long.MinValue;

	/// <summary>The statement sequence at the last step (or the close): a component that comes live now does so after it.</summary>
	private ulong _stepped;

	/// <summary>False for a playback kept only for its windows (the constructor's <c>windowsOf</c>).</summary>
	private readonly bool _evaluatesTracks;

	/// <summary>
	/// <paramref name="windowsOf"/>, when given, keeps only the clocks and the time windows of
	/// those elements: no track is evaluated and <see cref="Closed"/> is answered for them alone —
	/// a screen whose look the guest's sample still states, timed by the same clocks.
	/// </summary>
	public Engine_UiSlidePlayback(Engine_UiScreen screen, IReadOnlyCollection<int>? windowsOf = null)
	{
		_screen = screen;
		_evaluatesTracks = windowsOf is null;
		int count = screen.Nodes.Length;
		_tracks = screen.Document.Animations;
		_trackChannel = new Channel?[_tracks.Length];
		_trackOn = new bool[_tracks.Length];
		for (int i = 0; i < _tracks.Length; ++i)
		{
			BgfAnimation track = _tracks[i];
			if (track.Keys.Length != 0 && track.ElementOrdinal >= 0 && track.ElementOrdinal < count &&
				ChannelById.TryGetValue(track.AttributeId, out Channel channel))
			{
				_trackChannel[i] = channel;
			}
		}

		_owned = new List<BgfSlide>?[count];
		_master = new BgfSlide?[count];
		var windowed = new HashSet<int>();
		foreach (BgfSlide slide in screen.Document.Slides)
		{
			int owner = slide.OwnerElement;
			if (owner < 0 || owner >= count)
			{
				continue;
			}

			if (slide.IsMaster)
			{
				_master[owner] ??= slide;
			}
			else
			{
				(_owned[owner] ??= new List<BgfSlide>()).Add(slide);
			}

			foreach (BgfSlideEntry entry in slide.Entries)
			{
				foreach (BgfAttribute command in entry.Commands ?? System.Array.Empty<BgfAttribute>())
				{
					if ((command.Id == TimeOffsetId || command.Id == EndTimeId) &&
						entry.ElementOrdinal >= 0 && entry.ElementOrdinal < count)
					{
						windowed.Add(entry.ElementOrdinal);
					}
				}
			}
		}

		_clocks = new Clock?[count];
		_clockOf = new int[count];
		for (int i = 0; i < count; ++i)
		{
			if (_owned[i] is not null)
			{
				_clocks[i] = new Clock();
			}

			_clockOf[i] = -1;
			for (UiNode? up = screen.Nodes[i].Parent; up is not null; up = up.Parent)
			{
				if (up.Ordinal >= 0 && up.Ordinal < count && _owned[up.Ordinal] is not null)
				{
					_clockOf[i] = up.Ordinal;
					break;
				}
			}

			BgfElement element = screen.Nodes[i].Element;
			if (element is not null &&
				(element.TryGetAttribute(TimeOffsetId, out _) || element.TryGetAttribute(EndTimeId, out _)))
			{
				windowed.Add(i);
			}
		}

		if (windowsOf is not null)
		{
			windowed.IntersectWith(windowsOf);
		}

		_windowed = new int[windowed.Count];
		windowed.CopyTo(_windowed);
		_values = new float[count * Channels];
		_closed = new bool[count];
		_told = new bool?[count];
		_activeAt = new ulong[count];
		_writtenAt = new ulong[count * Channels];
		_toldActiveAt = new ulong[count];
		_toldWrittenAt = new ulong[count * Channels];
		_slides = new int[count];
		Reset();
	}

	private readonly bool?[] _told;

	/// <summary>
	/// Per element: `active` as the guest last told it since its last slide entry, null where
	/// it told nothing — a later `setActive` over the entry's own bit.
	/// </summary>
	public bool?[] ToldActive => _told;

	/// <summary>
	/// A told attribute write (`sub_82284798` outside a track or a slide entry): it stands in the
	/// same store the tracks write — until a slide command writes that attribute, and under any
	/// track active on it.
	/// </summary>
	public void Tell(int ordinal, uint attribute, uint value, ulong sequence = ulong.MaxValue)
	{
		if (ordinal >= 0 && ordinal < _closed.Length && ChannelById.TryGetValue(attribute, out Channel channel) &&
			sequence > _writtenAt[(ordinal * Channels) + (int)channel])
		{
			_values[(ordinal * Channels) + (int)channel] = System.BitConverter.UInt32BitsToSingle(value);
		}
	}

	/// <summary>A told `active` (`sub_82284BE8`, mask 0x2000).</summary>
	public void TellActive(int ordinal, bool on, ulong sequence = ulong.MaxValue)
	{
		if (ordinal >= 0 && ordinal < _told.Length && sequence > _activeAt[ordinal])
		{
			_told[ordinal] = on;
		}
	}

	/// <summary>
	/// The statement sequence at which a slide entry last wrote each element's `active` and each
	/// attribute channel: a told write OLDER than that entry was overwritten by it in the guest —
	/// the guest's own order, which a frame's batch of statements does not keep by itself.
	/// </summary>
	private readonly ulong[] _activeAt;

	private readonly ulong[] _writtenAt;

	/// <summary>
	/// As <see cref="_activeAt"/> and <see cref="_writtenAt"/>, for TOLD entries only (the guest's own
	/// slide call, whose GoTo applies the slide's entries in the call — Fable 3 TU3 0x82267224 /
	/// 0x82267250 -> `sub_821CA6D8` -> the store 0x821CA7A4): the walk's and a go-next's entries are not
	/// the guest's calls.
	/// </summary>
	private readonly ulong[] _toldActiveAt;

	private readonly ulong[] _toldWrittenAt;

	/// <summary>The sequence the entry being applied stands at (Enter).</summary>
	private ulong _entering;

	/// <summary>Whether the entry being applied is a told one (Enter).</summary>
	private bool _enteringTold;

	/// <summary>The statement sequence of the latest told entry that wrote the element's channel, 0 for none.</summary>
	public ulong ToldEntryWrote(int ordinal, Channel channel) =>
		ordinal >= 0 && ordinal < _closed.Length ? _toldWrittenAt[(ordinal * Channels) + (int)channel] : 0;

	/// <summary>The statement sequence of the latest told entry that wrote the element's `active`, 0 for none.</summary>
	public ulong ToldEntryActive(int ordinal) => ordinal >= 0 && ordinal < _closed.Length ? _toldActiveAt[ordinal] : 0;

	/// <summary>This frame's track values, NaN where no track wrote (see <see cref="Engine_UiElementResolve.Animated"/>).</summary>
	public float[] Values => _values;

	/// <summary>Per element: outside its time window this frame.</summary>
	public bool[] Closed => _closed;

	/// <summary>
	/// The clock of the component at or above <paramref name="ordinal"/> as the last
	/// <see cref="Step"/> left it, with that component's Master slide end (the deserialiser's
	/// timeline +36, which the scroll gate TU3 `sub_82878358` compares the time with); null when
	/// no component owns it or its clock is not live.
	/// </summary>
	public (int Time, int MasterEnd)? OwnerClock(int ordinal)
	{
		if (ordinal < 0 || ordinal >= _clocks.Length)
		{
			return null;
		}

		int owner = _clocks[ordinal] is not null ? ordinal : _clockOf[ordinal];
		return owner >= 0 && _clocks[owner] is { Live: true } clock
			? (clock.Time, _master[owner]?.EndTime ?? 0)
			: null;
	}

	/// <summary>
	/// The presentation closed: every component leaves, every track returns to its file state,
	/// and the next <see cref="Step"/> enters everything afresh. <paramref name="sequence"/> is the
	/// statement sequence at the close — what was told before it belongs to the screen that closed.
	/// </summary>
	public void Reset(ulong sequence = 0)
	{
		_stepped = sequence;
		for (int i = 0; i < _tracks.Length; ++i)
		{
			_trackOn[i] = _tracks[i].ActiveOnLoad;
		}

		System.Array.Fill(_values, float.NaN);
		System.Array.Clear(_closed);
		System.Array.Clear(_told);
		System.Array.Clear(_activeAt);
		System.Array.Clear(_writtenAt);
		System.Array.Clear(_toldActiveAt);
		System.Array.Clear(_toldWrittenAt);
		foreach (Clock? clock in _clocks)
		{
			if (clock is not null)
			{
				clock.Live = false;
				clock.Slide = 0;
				clock.Stated = 0;
			}
		}

		_opened = long.MinValue;
	}

	/// <summary>
	/// `COMMAND_PLAY` on a component: its slide runs from wherever it stands. The guest's
	/// `sub_82426CA8` is a slide entry followed by this.
	/// </summary>
	public void Play(int ordinal, long now) => ClockFor(ordinal)?.SetPaused(false, now);

	/// <summary>
	/// `COMMAND_GOTOTIME` (`sub_82BB5B60`) on the component at or above <paramref name="ordinal"/>:
	/// the meter's path, `t = trunc(pct × 10)` (`sub_823FDFF8`).
	/// </summary>
	public void GoToTime(int ordinal, int time, long now)
	{
		if (ClockFor(ordinal) is not { } clock)
		{
			return;
		}

		if (clock.Paused)
		{
			clock.Frozen = time;
		}
		else
		{
			clock.Start = now - time;
		}

		clock.Time = TimeAt(clock.Raw(now), clock.Duration, clock.Passes, clock.PingPong, out _);
	}

	private Clock? ClockFor(int ordinal)
	{
		if (ordinal < 0 || ordinal >= _clocks.Length)
		{
			return null;
		}

		return _clocks[ordinal] ?? (_clockOf[ordinal] >= 0 ? _clocks[_clockOf[ordinal]] : null);
	}

	/// <summary>
	/// One frame. <paramref name="stated"/> is the slide each element stands on (0 = master);
	/// <paramref name="drawn"/> is last frame's draw gate (null before the first). Returns the slide
	/// each element PLAYS — the stated one, moved on by a completed go-next slide. An entry whose
	/// owner <paramref name="played"/> answers true for plays, as `sub_82426CA8` does; any other
	/// keeps the record's pause, as the walk's own entry and `sub_82426BA8` do — an authored-paused
	/// slide waits at t = 0 until something plays it.
	/// <paramref name="timed"/> runs after the entries and before the tracks read their clocks —
	/// this tick's <see cref="GoToTime"/> statements.
	///
	/// <paramref name="told"/> answers what the guest told an element (Engine_UiSlideStatements),
	/// null where Godot states the slide itself; <paramref name="sequence"/> is the statement
	/// sequence now. A told entry made since the component came live enters that slide with the
	/// record's pause, and a later told play or pause applies where it stands.
	/// </summary>
	public int[] Step(IReadOnlyList<int> stated, IReadOnlyList<bool>? drawn, long now,
		System.Func<int, bool>? played = null, System.Action<long>? timed = null,
		System.Func<int, Engine_UiSlideStatements.Told?>? told = null, ulong sequence = 0)
	{
		if (_opened == long.MinValue)
		{
			_opened = now;
		}

		for (int i = 0; i < _clocks.Length; ++i)
		{
			int wanted = i < stated.Count ? stated[i] : 0;
			_slides[i] = wanted;
			if (_clocks[i] is not { } clock)
			{
				continue;
			}

			bool live = wanted > 0 && (drawn is null || i >= drawn.Count || drawn[i]) && !_closed[i];
			if (!live)
			{
				clock.Live = false;
				clock.Stated = wanted;
				continue;
			}

			if (!clock.Live)
			{
				clock.LiveSince = _stepped;
				clock.Told = 0;
				clock.ToldPause = 0;
			}

			if (!clock.Live || wanted != clock.Stated)
			{
				// The walk's own entry of a component coming live runs on the GUI walk AFTER the
				// tick's code (`sub_822849A0`), so it is later than every statement made so far:
				// a menu's setup switching its pieces on is overruled by the first slide's entry
				// switching the unused ones off again.
				Enter(i, clock, wanted, now, play: played?.Invoke(i) ?? false, sequence);
			}

			if (told?.Invoke(i) is { } said)
			{
				if (said.Sequence > clock.LiveSince && said.Sequence != clock.Told && said.Slide > 0)
				{
					Enter(i, clock, said.Slide, now, play: false, said.Sequence, told: true);
					clock.Told = said.Sequence;
				}

				if (said.Paused is { } paused && said.PauseSequence != clock.ToldPause &&
					said.PauseSequence > System.Math.Max(clock.LiveSince, clock.Told))
				{
					clock.SetPaused(paused, now);
					clock.ToldPause = said.PauseSequence;
				}
			}

			clock.Live = true;
			clock.Stated = wanted;
			clock.Time = TimeAt(clock.Raw(now), clock.Duration, clock.Passes, clock.PingPong, out bool done);
			if (done && clock.Next && _owned[i] is { } slides && clock.Slide < slides.Count)
			{
				Enter(i, clock, clock.Slide + 1, now, play: false, sequence);
				clock.Time = 0;
			}

			_slides[i] = clock.Slide;
		}

		_stepped = sequence;
		timed?.Invoke(now);
		int presentation = (int)System.Math.Min(now - _opened, int.MaxValue);
		for (int i = 0; _evaluatesTracks && i < _tracks.Length; ++i)
		{
			if (!_trackOn[i] || _trackChannel[i] is not { } channel)
			{
				continue;
			}

			BgfAnimation track = _tracks[i];
			if (InheritedTime(track.ElementOrdinal, presentation) is not { } t)
			{
				continue;
			}

			_values[(track.ElementOrdinal * Channels) + (int)channel] = track.Evaluate(t);
		}

		foreach (int i in _windowed)
		{
			_closed[i] = InheritedTime(i, presentation) is { } t && !Window(i).IsOpen(t);
		}

		return _slides;
	}

	/// <summary>The time an element's tracks and window read, or null while its clock is not live.</summary>
	private int? InheritedTime(int ordinal, int presentation)
	{
		int owner = _clockOf[ordinal];
		if (owner < 0)
		{
			return presentation;
		}

		Clock clock = _clocks[owner]!;
		return clock.Live ? clock.Time : null;
	}

	/// <summary>The element's window: its own attributes, overridden by its clock's playing slide.</summary>
	private Engine_UiTimeline.Window Window(int ordinal)
	{
		BgfElement element = _screen.Nodes[ordinal].Element;
		int start = element.TryGetAttribute(TimeOffsetId, out BgfAttribute offset) ? offset.AsInt() : int.MinValue;
		int end = element.TryGetAttribute(EndTimeId, out BgfAttribute until) ? until.AsInt() : int.MaxValue;
		int owner = _clockOf[ordinal];
		if (owner >= 0 && _clocks[owner] is { Slide: > 0 } clock && _owned[owner] is { } slides &&
			clock.Slide <= slides.Count)
		{
			WindowFrom(_master[owner], ordinal, ref start, ref end);
			WindowFrom(slides[clock.Slide - 1], ordinal, ref start, ref end);
		}

		return new Engine_UiTimeline.Window(ordinal, start, end);
	}

	/// <summary>What one slide's entries state of an element's window, over what stood before.</summary>
	private static void WindowFrom(BgfSlide? slide, int ordinal, ref int start, ref int end)
	{
		if (slide?.Entries is not { } entries)
		{
			return;
		}

		for (int i = 0; i < entries.Length; ++i)
		{
			BgfSlideEntry entry = entries[i];
			if (entry.ElementOrdinal != ordinal || entry.Commands is not { } commands)
			{
				continue;
			}

			for (int j = 0; j < commands.Length; ++j)
			{
				BgfAttribute command = commands[j];
				if (command.Id == TimeOffsetId)
				{
					start = command.AsInt();
				}
				else if (command.Id == EndTimeId)
				{
					end = command.AsInt();
				}
			}
		}
	}

	/// <summary>GotoSlide: the master's entries, then the slide's; time to 0; the record's mode.</summary>
	private void Enter(int owner, Clock clock, int slide, long now, bool play, ulong sequence, bool told = false)
	{
		_entering = sequence;
		_enteringTold = told;
		List<BgfSlide> slides = _owned[owner]!;
		slide = System.Math.Clamp(slide, 1, slides.Count);
		BgfSlide record = slides[slide - 1];
		Apply(_master[owner]);
		Apply(record);

		clock.Slide = slide;
		clock.Duration = record.EndTime - record.StartTime;
		clock.Passes = record.Flags & 3;
		clock.Paused = !play && (record.Flags & 4) != 0;
		clock.PingPong = (record.Flags & 8) != 0;
		clock.Next = (record.Flags & 0x10) != 0;
		clock.Start = now;
		clock.Frozen = 0;
		clock.Time = 0;
	}

	/// <summary>One slide's entries: commands end a held track value; switches turn tracks on or off.</summary>
	private void Apply(BgfSlide? slide)
	{
		if (slide is null)
		{
			return;
		}

		foreach (BgfSlideEntry entry in slide.Entries)
		{
			int ordinal = entry.ElementOrdinal;
			if (ordinal >= 0 && ordinal < _closed.Length)
			{
				// The entry writes the element's own active bit (`goToSlide`), over any told one.
				_told[ordinal] = null;
				_activeAt[ordinal] = System.Math.Max(_activeAt[ordinal], _entering);
				if (_enteringTold)
				{
					_toldActiveAt[ordinal] = System.Math.Max(_toldActiveAt[ordinal], _entering);
				}

				foreach (BgfAttribute command in entry.Commands ?? System.Array.Empty<BgfAttribute>())
				{
					if (ChannelById.TryGetValue(command.Id, out Channel channel))
					{
						int at = (ordinal * Channels) + (int)channel;
						_values[at] = float.NaN;
						_writtenAt[at] = System.Math.Max(_writtenAt[at], _entering);
						if (_enteringTold)
						{
							_toldWrittenAt[at] = System.Math.Max(_toldWrittenAt[at], _entering);
						}
					}
				}
			}

			foreach (BgfAnimationEvent change in entry.AnimationEvents ?? System.Array.Empty<BgfAnimationEvent>())
			{
				if (change.IsTrack && change.Index < _trackOn.Length)
				{
					_trackOn[change.Index] = change.On;
				}
			}
		}
	}

	/// <summary>
	/// The time block's per-tick answer (`sub_82284DF8`) for <paramref name="raw"/> milliseconds
	/// since entry: <paramref name="completed"/> when a limited, non-ping-pong slide has run its passes.
	/// </summary>
	public static int TimeAt(int raw, int duration, int passes, bool pingPong, out bool completed)
	{
		completed = false;
		if (raw < 0)
		{
			return 0;
		}

		bool limited = passes != 0 && duration != 0;
		if (limited && raw > passes * duration)
		{
			raw = passes * duration;
		}

		if (duration == 0)
		{
			return raw;
		}

		if (!pingPong && limited && raw == passes * duration)
		{
			completed = true;
			return duration;
		}

		if (pingPong && (raw / duration) % 2 >= 1)
		{
			return duration - (raw % duration);
		}

		return raw % duration;
	}
}
