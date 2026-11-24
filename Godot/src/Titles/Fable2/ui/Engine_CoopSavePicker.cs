namespace Refii.Titles.Fable2;

/// <summary>
/// Player two's save picker on the co-op screen past the sixth save. Pure arithmetic over indexes.
/// </summary>
/// <remarks>
/// <para>THE GUEST'S CURSOR STOPS AT SIX. The picker's cursor setter (TU1 `sub_82439738`, disc
/// `sub_8247A930`) takes only <c>0 &lt;= N &lt; min(count, 6)</c> (0x82439764..0x82439778), so on the
/// first six saves the guest keeps its own cursor, left/right and A, and from the seventh this owns
/// the cursor and commits by index (the pump's <c>CoopPickSave:N</c>). Left and right step over
/// the save the opener names (`sub_82439630`, looped past at 0x82438e04 / 0x82438e78). A on an
/// unloadable save (its header's +24 byte 0) commits nothing (TU1 0x82438db4).</para>
/// <para>Of player two's pad, only what the guest must not act on is withheld: right while the
/// guest's next step would leave its six, and left, right and A while this owns the cursor. The
/// page buttons never reach the picker (its switch takes codes 30..37 only, 0x82438d58), and B is
/// always the guest's close.</para>
/// </remarks>
public static class Engine_CoopSavePicker
{
	/// <summary>The saves the guest's own cursor reaches: <c>min(count, 6)</c>.</summary>
	public const int GuestCards = 6;

	/// <summary>The picker as stated, and the save this owns the cursor on (null: the guest's).</summary>
	public readonly record struct Picker(int Saves, int GuestCursor, int OwnSave, uint Valid, int? Owned)
	{
		/// <summary>The save the cursor is on, whoever holds it.</summary>
		public int Cursor => Owned ?? GuestCursor;

		/// <summary>Whether save <paramref name="save"/>'s header parsed.</summary>
		public bool IsValid(int save) => save is >= 0 and < 32 && (Valid & (1u << save)) != 0u;
	}

	/// <summary>Player two's presses the picker answers.</summary>
	[System.Flags]
	public enum Press
	{
		None = 0,
		Left = 1,
		Right = 2,
		Accept = 4,
		PageNext = 8,
		PagePrevious = 16,
	}

	/// <summary>
	/// What one press did: whether the guest must not see it, the save this owns the cursor on
	/// after it, the save to put the guest's own cursor on (-1 none) and the save to commit (-1 none).
	/// </summary>
	public readonly record struct Outcome(bool Taken, int? Owned, int GuestCursor, int Commit)
	{
		public static Outcome Guest(int? owned) => new(false, owned, -1, -1);
	}

	/// <summary>
	/// The save a sideways step from <paramref name="cursor"/> lands on, stepping over
	/// <paramref name="ownSave"/> as the guest's loop does; -1 past either end.
	/// </summary>
	public static int Step(int cursor, int direction, int saves, int ownSave)
	{
		int dir = System.Math.Sign(direction);
		if (dir == 0)
		{
			return -1;
		}

		int next = cursor + dir;
		if (next == ownSave)
		{
			next += dir;
		}

		return next >= 0 && next < saves ? next : -1;
	}

	/// <summary>
	/// The save a page turn lands on (<see cref="Engine_FrontEndSavePages.Turn"/>), moved off
	/// <paramref name="ownSave"/> onward in the turn's direction, or back when that is the end.
	/// </summary>
	public static int Turn(int cursor, int direction, int saves, int ownSave)
	{
		int to = Engine_FrontEndSavePages.Turn(cursor, direction, saves);
		if (to < 0 || to != ownSave)
		{
			return to;
		}

		int onward = Step(to, direction, saves, ownSave);
		int back = Step(to, -direction, saves, ownSave);
		int landed = onward >= 0 ? onward : back;
		return landed == cursor ? -1 : landed;
	}

	/// <summary>The presses withheld from the guest in this state, before any is pressed.</summary>
	public static Press Withheld(Picker picker)
	{
		if (picker.Owned is not null)
		{
			return Press.Left | Press.Right | Press.Accept;
		}

		return Step(picker.GuestCursor, 1, picker.Saves, picker.OwnSave) >= GuestCards ? Press.Right : Press.None;
	}

	/// <summary>One press of player two's on the picker.</summary>
	public static Outcome Apply(Picker picker, Press press)
	{
		bool owned = picker.Owned is not null;
		int cursor = picker.Cursor;
		switch (press)
		{
			case Press.Right:
			case Press.Left:
			{
				if ((Withheld(picker) & press) == 0)
				{
					return Outcome.Guest(picker.Owned);
				}

				int to = Step(cursor, press == Press.Right ? 1 : -1, picker.Saves, picker.OwnSave);
				return to < 0 ? new Outcome(true, picker.Owned, -1, -1) : MoveTo(picker, to);
			}

			case Press.PageNext:
			case Press.PagePrevious:
			{
				int to = Turn(cursor, press == Press.PageNext ? 1 : -1, picker.Saves, picker.OwnSave);
				return to < 0 ? new Outcome(true, picker.Owned, -1, -1) : MoveTo(picker, to);
			}

			case Press.Accept:
				return !owned ? Outcome.Guest(null)
					: new Outcome(true, picker.Owned, -1, picker.IsValid(cursor) ? cursor : -1);

			default:
				return Outcome.Guest(picker.Owned);
		}
	}

	/// <summary>
	/// The pointer on save <paramref name="save"/>, clicked or only pointed: the cursor goes there
	/// and a click on a loadable save commits it. A save the guest's cursor reaches while the guest
	/// holds it is the caller's to walk with the guest's own presses (Taken false); the save the
	/// cursor steps over is not reached at all.
	/// </summary>
	public static Outcome Point(Picker picker, int save, bool click)
	{
		if (save < 0 || save >= picker.Saves || save == picker.OwnSave)
		{
			return new Outcome(true, picker.Owned, -1, -1);
		}

		if (picker.Owned is null && save < GuestCards)
		{
			return Outcome.Guest(null);
		}

		Outcome moved = save == picker.Cursor ? new Outcome(true, picker.Owned, -1, -1) : MoveTo(picker, save);
		return click && picker.IsValid(save) ? moved with { Commit = save } : moved;
	}

	/// <summary>
	/// The left stick counts as a direction from this far out, of 1 — `sub_826E39C0` compares the
	/// dead-zoned X against ±0.75 and emits code 30 / 31 (0x826E3B3C / 0x826E3B98).
	/// </summary>
	public const double StickPush = 0.75;

	/// <summary>And stops counting below this — the shipped `THUMBSTICK_RELEASE_THRESHOLD`.</summary>
	public const double StickRelease = 0.65;

	/// <summary>Player two's presses on the picker, edge-detected off their own pad.</summary>
	public struct PadEdges
	{
		private Press _down;
		private bool _stickLeft;
		private bool _stickRight;
		private Engine_FrontEndSavePages.PageButtons _pages;

		/// <summary>This frame's new presses from the D-pad, the stick's X (signed, of 32767), A and the page buttons.</summary>
		public Press Feed(bool left, bool right, bool accept, bool lb, bool rb, uint lt, uint rt, int leftX)
		{
			double x = leftX / 32767.0;
			_stickLeft = _stickLeft ? -x > StickRelease : -x >= StickPush;
			_stickRight = _stickRight ? x > StickRelease : x >= StickPush;
			Press down = (left || _stickLeft ? Press.Left : Press.None) |
				(right || _stickRight ? Press.Right : Press.None) |
				(accept ? Press.Accept : Press.None);
			Press edges = down & ~_down;
			_down = down;
			(bool next, bool previous) = _pages.Feed(lb, rb, lt, rt);
			return edges | (next ? Press.PageNext : Press.None) | (previous ? Press.PagePrevious : Press.None);
		}
	}

	/// <summary>
	/// The guest's own sideways presses that walk its cursor from <paramref name="cursor"/> to
	/// <paramref name="save"/>, stepping over <paramref name="ownSave"/> as it does: positive right,
	/// negative left, 0 when it cannot get there.
	/// </summary>
	public static int Presses(int cursor, int save, int saves, int ownSave)
	{
		int direction = System.Math.Sign(save - cursor);
		int presses = 0;
		for (int at = cursor; at != save; ++presses)
		{
			at = Step(at, direction, saves, ownSave);
			if (at < 0 || System.Math.Sign(save - at) == -direction)
			{
				return 0;
			}
		}

		return presses * direction;
	}

	/// <summary>
	/// The cursor onto <paramref name="save"/>: held here past the guest's six, else handed back on
	/// the guest's own cursor, set by its setter when it is elsewhere.
	/// </summary>
	private static Outcome MoveTo(Picker picker, int save) =>
		save >= GuestCards
			? new Outcome(true, save, -1, -1)
			: new Outcome(true, null, save == picker.GuestCursor ? -1 : save, -1);
}
