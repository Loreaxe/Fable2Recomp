namespace Refii.Titles.Fable2;

/// <summary>
/// The save fans' pages: every save stays on the fan, five to a page, each page laid out as the
/// guest lays out that many saves. Shared by the Load Game fan and the co-op save picker
/// (<see cref="Engine_FrontEndSaveCards"/>). Pure arithmetic over indexes.
/// </summary>
public static class Engine_FrontEndSavePages
{
	/// <summary>Saves on one page — the user's choice (2026-11-24), one short of the six authored cards.</summary>
	public const int PageSize = 5;

	/// <summary>Pages for <paramref name="saves"/> saves; one even when empty.</summary>
	public static int PageCount(int saves) => saves <= PageSize ? 1 : (saves + PageSize - 1) / PageSize;

	/// <summary>The page a save is on.</summary>
	public static int PageOf(int save) => save <= 0 ? 0 : save / PageSize;

	/// <summary>The first save on <paramref name="page"/>.</summary>
	public static int FirstOn(int page) => System.Math.Max(0, page) * PageSize;

	/// <summary>How many of <paramref name="saves"/> saves stand on <paramref name="page"/>.</summary>
	public static int CountOn(int saves, int page) => System.Math.Clamp(saves - FirstOn(page), 0, PageSize);

	/// <summary>
	/// The card the first of <paramref name="shown"/> saves stands on, 1-based — the guest's own rule.
	/// The load fan (TU1 0x826E0670..0x826E0698, disc 0x826C8AA0..0x826C8AC8): two saves card 3, three
	/// or four card 2, more card 1. The co-op picker (`sub_8243B070` 0x8243B1FC..0x8243B224): one or two
	/// card 3, the rest the same. One save never opens the load fan (it commits at 0x826E0470), so a page
	/// of one takes the picker's card 3.
	/// </summary>
	public static int BaseSlot(int shown) => shown <= 2 ? 3 : shown <= 4 ? 2 : 1;

	/// <summary>
	/// The save a sideways step from <paramref name="cursor"/> lands on, carrying over a page edge
	/// onto the neighbouring page; -1 at either end of the list.
	/// </summary>
	public static int Step(int cursor, int direction, int saves)
	{
		int next = cursor + System.Math.Sign(direction);
		return direction == 0 || next < 0 || next >= saves ? -1 : next;
	}

	/// <summary>
	/// The save a page turn from <paramref name="cursor"/> lands on: the first card of the next page,
	/// or the last card of the previous one; -1 when there is no such page.
	/// </summary>
	public static int Turn(int cursor, int direction, int saves)
	{
		int page = PageOf(cursor) + System.Math.Sign(direction);
		if (direction == 0 || page < 0 || page >= PageCount(saves) || saves <= 0)
		{
			return -1;
		}

		return direction > 0 ? FirstOn(page) : FirstOn(page) + CountOn(saves, page) - 1;
	}

	/// <summary>A trigger counts as pressed above this, of 255 — the XInput convention the spells menu's RT uses.</summary>
	public const uint TriggerThreshold = 30;

	/// <summary>
	/// The page buttons, edge-detected: RB or RT turns to the next page, LB or LT to the previous one.
	/// A held button is one press; both buttons of a pair together are still one.
	/// </summary>
	public struct PageButtons
	{
		private bool _nextDown;
		private bool _previousDown;

		/// <summary>This frame's presses from the shoulders and the triggers' 0..255.</summary>
		public (bool Next, bool Previous) Feed(bool lb, bool rb, uint lt, uint rt)
		{
			bool next = rb || rt > TriggerThreshold;
			bool previous = lb || lt > TriggerThreshold;
			(bool, bool) edges = (next && !_nextDown, previous && !_previousDown);
			_nextDown = next;
			_previousDown = previous;
			return edges;
		}

		/// <summary>Takes whatever is down now as held, so a new source of input presses nothing.</summary>
		public void Hold(bool lb, bool rb, uint lt, uint rt)
		{
			_nextDown = rb || rt > TriggerThreshold;
			_previousDown = lb || lt > TriggerThreshold;
		}
	}

	/// <summary>"Page 1 / 2" as the indicator shows it, page 1-based; empty for a single page.</summary>
	public static string Indicator(string template, int page, int pages) =>
		pages <= 1 ? string.Empty : string.Format(System.Globalization.CultureInfo.InvariantCulture, template, page + 1, pages);
}
