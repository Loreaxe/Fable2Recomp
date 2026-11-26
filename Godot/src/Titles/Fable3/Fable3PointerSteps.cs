namespace Refii.Titles.Fable3;

/// <summary>
/// The mouse on a box-manager screen: the row the pointer picked, reached by the guest's own cursor.
/// </summary>
/// <remarks>
/// THE BOX MANAGER'S SCREENS TAKE NO ROW FROM OUTSIDE (Options and the save menu are stated a row
/// and reach it natively: <see cref="Fable3BoxAnswers.OptionsRow"/>, <see cref="Fable3BoxAnswers.SaveMenuRow"/>).
/// Each moves its cursor inside its own pad handler, behind its own repeat gate — the quest
/// summary eight updates (0x82974800), the stats pages four ticks (0x8296e5ec), the menu
/// box 0.3 s between any two inputs (+36, 0x825fa118) — and three of them read only the LEFT STICK
/// (the quest summary 0x829745f8, the stats pages 0x8296dc3c, the interaction list 0x829644e4), while
/// every one of them takes it: sub_82B3F6F0 turns a stick past 0.6 into GUI up/down (29/30, y first
/// tested after x, 0x82b3f6f4..0x82b3f7c8), the menu box past 0.95 (0x82b13314). So a pick is walked:
/// one short full-deflection stick step at a time, a step only after the last has had time to pass
/// the slowest gate, each checked against the row the guest then STATES — closed loop, so a step a
/// gate swallowed is simply taken again. A click on the row reached is the guest's A (74, and 72 for
/// the screens that select on it), sent after the same pause, since the menu box swallows an accept
/// within 0.3 s of the step before it.
/// </remarks>
public static class Fable3PointerSteps
{
	/// <summary>The pause after a step or before an accept: past every screen's repeat gate.</summary>
	private const double Pace = 0.32;

	/// <summary>Frames a step or an accept is held: one guest input read at the pump's 30 Hz.</summary>
	private const int HoldFrames = 3;

	/// <summary>How long a pick is walked before it is given up (a gate that never opens).</summary>
	private const double GiveUp = 4.0;

	private static int _target = -1;
	private static bool _accept;
	private static double _wait;
	private static double _age;
	private static int _holdFrames;
	private static int _holdStick;
	private static bool _holdAccept;

	/// <summary>Walks the guest's cursor to <paramref name="row"/>; <paramref name="accept"/> presses A there.</summary>
	public static void Toward(int row, bool accept)
	{
		if (row != _target)
		{
			_age = 0.0;
		}

		_target = row;
		_accept |= accept;
	}

	/// <summary>Drops a pick (the screen closed).</summary>
	public static void Cancel()
	{
		_target = -1;
		_accept = false;
	}

	/// <summary>One frame against the row the guest states now (-1 for none).</summary>
	public static void Tick(int selected, double delta)
	{
		_wait -= delta;
		if (_target < 0 || selected < 0 || _holdFrames > 0 || _wait > 0.0)
		{
			return;
		}

		if ((_age += Pace) > GiveUp)
		{
			Cancel();
			return;
		}

		if (selected != _target)
		{
			// Up the list is the stick up: positive Y, as XInput states it.
			_holdStick = selected > _target ? 1 : -1;
			_holdFrames = HoldFrames;
			_wait = Pace;
			return;
		}

		if (_accept)
		{
			_holdAccept = true;
			_holdFrames = HoldFrames;
			_wait = Pace;
		}

		Cancel();
	}

	/// <summary>What the walk holds on the guest's pad 0 this frame: the stick's Y and whether A is down.</summary>
	public static (int StickY, bool Accept) Held()
	{
		if (_holdFrames <= 0)
		{
			return (0, false);
		}

		--_holdFrames;
		(int, bool) held = (_holdStick * 32767, _holdAccept);
		if (_holdFrames == 0)
		{
			_holdStick = 0;
			_holdAccept = false;
		}

		return held;
	}
}
