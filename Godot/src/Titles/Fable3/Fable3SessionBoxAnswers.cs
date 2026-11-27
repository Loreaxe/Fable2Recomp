namespace Refii.Titles.Fable3;

/// <summary>
/// What a press or a pointer pick means on the session boxes (join / visibility options, the friends
/// channel, co-op partners) and the family boxes (the bed menu, the divorce message, the treasury
/// statement, the marriage proposal, the food query), whose pad Godot owns: the `box:` lever their
/// own handlers answer (Fable3/src/hooks/src/session_boxes.cpp, family_boxes.cpp) and Godot's working
/// values after it. Pure logic, so the tests run it.
/// </summary>
/// <remarks>
/// The levers, and the action each box's handler then takes (TU3 / disc handlers in the native
/// files' headers): join / visibility `choose:&lt;1..3&gt;` (74: the apply and the close) and `close`
/// (75); the friends channel `row:&lt;1..3&gt;` (74: the row's system UI and the close) and `close`;
/// co-op partners `row:&lt;i&gt;` (the cursor stored, the window rewritten, `@Screen.Cursor`),
/// `join:&lt;i&gt;` (74), `card:&lt;i&gt;` (77), `divorce:&lt;i&gt;` (76), `close` (75); the bed
/// `sleep:&lt;0..2&gt;` / `sex:&lt;0..1&gt;` (41 with the row and option stored) and `cancel` (42); the
/// divorce `accept` (74), `cancel` (75), `reason:prev` / `reason:next` (31 / 32); the treasury `accept`
/// (74), `back` (75), `flip` (76), `less` / `more` (31 / 32, its amount widget's own steps); the
/// proposal `accept` (41), `decline` (42), `leave` (76: decline and ask to leave the game), `card` (77);
/// the food query `yes` (41) and `no` (42). Each handler keeps its own player test, so a lever it
/// refuses does nothing.
/// </remarks>
public static class Fable3SessionBoxAnswers
{
	/// <summary>The screens (by element root) this class answers.</summary>
	public static bool Owns(string? root) => root is Fable3BoxScreenModel.JoinOptions or Fable3BoxScreenModel.Visibility or
		Fable3BoxScreenModel.FriendsChannel or Fable3BoxScreenModel.Partners or Fable3BoxScreenModel.BedMenu or
		Fable3BoxScreenModel.Divorce or Fable3BoxScreenModel.Treasury or Fable3BoxScreenModel.Proposal or
		Fable3BoxScreenModel.FoodQuery;

	/// <summary>
	/// Godot's cursor (0-based) on a screen: the player's, else where the guest's Show starts it — join
	/// / visibility on the stated setting's row (`@Screen.Online`), the friends channel on its first row
	/// (`+36 = 1`), the bed on Sleep (the opener's `+44 = 0`), partners on the first record.
	/// </summary>
	public static int CursorOf(string root, Fable3ScreenState screen) => screen.Cursor ?? root switch
	{
		Fable3BoxScreenModel.JoinOptions or Fable3BoxScreenModel.Visibility => System.Math.Clamp((screen.Online?.Row ?? 1) - 1, 0, 2),
		_ => 0,
	};

	/// <summary>
	/// The partners' cursor row in the guest's eight-row window: the lever stores `+56 = min(i, 7)` and
	/// the scroll `+60 = i - +56` (the stick's own steps keep that window, TU3 0x82B91850..0x82B91880).
	/// </summary>
	public static int PartnerWindowRow(Fable3ScreenState screen) => System.Math.Min(screen.Cursor ?? 0, 7);

	public static Fable3BoxAnswers.ScreenStep ForScreen(string root, uint address, Fable3BoxAnswers.Press press,
		Fable3ScreenState screen, int player = Fable3ActingPlayer.One)
	{
		string box = Lever(address, player);
		int cursor = CursorOf(root, screen);
		switch (root)
		{
			case Fable3BoxScreenModel.JoinOptions or Fable3BoxScreenModel.Visibility:
				return Choices(press, cursor, 3, box + "choose:", box + "move:", box + "close");
			case Fable3BoxScreenModel.FriendsChannel:
				return Choices(press, cursor, 3, box + "row:", box + "move:", box + "close");
			case Fable3BoxScreenModel.Partners:
				if (press.Up || press.Down)
				{
					// The guest bounds the record against its list; one it has not is refused there.
					int row = cursor + (press.Down ? 1 : -1);
					return row >= 0 ? new Fable3BoxAnswers.ScreenStep(box + "row:" + Int(row)) : default;
				}

				return new Fable3BoxAnswers.ScreenStep(press.A ? box + "join:" + Int(cursor) : press.Y ? box + "card:" + Int(cursor) :
					press.X ? box + "divorce:" + Int(cursor) : press.B ? box + "close" : null);
			case Fable3BoxScreenModel.BedMenu:
				return Bed(press, cursor, screen, box);
			case Fable3BoxScreenModel.Divorce:
				return new Fable3BoxAnswers.ScreenStep(press.A ? box + "accept" : press.B ? box + "cancel" : press.Left ? box + "reason:prev" :
					press.Right ? box + "reason:next" : null);
			case Fable3BoxScreenModel.Treasury:
				return new Fable3BoxAnswers.ScreenStep(press.A ? box + "accept" : press.B ? box + "back" : press.X ? box + "flip" : press.Left ? box + "less" :
					press.Right ? box + "more" : null);
			case Fable3BoxScreenModel.Proposal:
				return new Fable3BoxAnswers.ScreenStep(press.A ? box + "accept" : press.B ? box + "decline" : press.X ? box + "leave" : press.Y ? box + "card" : null);
			case Fable3BoxScreenModel.FoodQuery:
				return new Fable3BoxAnswers.ScreenStep(press.A ? box + "yes" : press.B ? box + "no" : null);
			default:
				return default;
		}
	}

	/// <summary>
	/// The step for the pointer on the drawn row named <paramref name="rowName"/>: resting on it moves
	/// Godot's cursor there, a click picks it.
	/// </summary>
	public static Fable3BoxAnswers.ScreenStep PointScreen(string root, uint address, string rowName, bool clicked,
		Fable3ScreenState screen, int player = Fable3ActingPlayer.One)
	{
		string box = Lever(address, player);
		int cursor = CursorOf(root, screen);
		int index = RowIndex(root, rowName);
		if (index < 0)
		{
			return default;
		}

		switch (root)
		{
			case Fable3BoxScreenModel.JoinOptions or Fable3BoxScreenModel.Visibility:
				return clicked
					? new Fable3BoxAnswers.ScreenStep(box + "choose:" + Int(index + 1), Cursor: index)
					: Move(index, cursor, box + "move:" + Int(index + 1));
			case Fable3BoxScreenModel.FriendsChannel:
				return clicked
					? new Fable3BoxAnswers.ScreenStep(box + "row:" + Int(index + 1), Cursor: index)
					: Move(index, cursor, box + "move:" + Int(index + 1));
			case Fable3BoxScreenModel.BedMenu:
				if (index == 1 && !screen.OpenedFlag(0, true))
				{
					return default;
				}

				return clicked
					? new Fable3BoxAnswers.ScreenStep(BedAccept(index, screen, box), Cursor: index)
					: Move(index, cursor, box + "row:" + Int(index));
			case Fable3BoxScreenModel.Partners:
			{
				int record = cursor - PartnerWindowRow(screen) + index;
				if (clicked)
				{
					return new Fable3BoxAnswers.ScreenStep(box + "join:" + Int(record));
				}

				return record == cursor ? default : new Fable3BoxAnswers.ScreenStep(box + "row:" + Int(record));
			}

			default:
				return default;
		}
	}

	/// <summary>The row a model's row name stands for on <paramref name="root"/>, or -1.</summary>
	public static int RowIndex(string root, string rowName) => root switch
	{
		Fable3BoxScreenModel.JoinOptions => rowName switch { "Any1" => 0, "Invite" => 1, "Off" => 2, _ => -1 },
		Fable3BoxScreenModel.Visibility => rowName switch { "Everyone" => 0, "Friends" => 1, "None" => 2, _ => -1 },
		Fable3BoxScreenModel.FriendsChannel => rowName switch { "Friends" => 0, "Join" => 1, "Invite" => 2, _ => -1 },
		Fable3BoxScreenModel.BedMenu => rowName switch { "Sleep" => 0, "Sex" => 1, _ => -1 },
		Fable3BoxScreenModel.Partners when rowName.StartsWith("Partner", System.StringComparison.Ordinal) &&
			int.TryParse(rowName.Substring(7), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
				out int row) => row,
		_ => -1,
	};

	/// <summary>
	/// The bed menu, as its handler moves it (TU3 sub_82B8DFA0's switch on action - 29): up to Sleep,
	/// down to Sex only when offered (0x82B8E458); on Sleep left / right step 0..2 wrapping (32 at 2 to 0,
	/// 31 at 0 to 2: 0x82B8E23C..0x82B8E264 / 0x82B8E354..0x82B8E380); on Sex 0..1 wrapping only when the
	/// opener lets it cycle (0x82B8E280 / 0x82B8E39C). A hands the row's option over, B cancels.
	/// </summary>
	private static Fable3BoxAnswers.ScreenStep Bed(Fable3BoxAnswers.Press press, int row, Fable3ScreenState screen, string box)
	{
		bool offered = screen.OpenedFlag(0, true);
		bool cycles = screen.OpenedFlag(2, false);
		if (press.Up || press.Down)
		{
			// Every press plays the step cue, moved or not (`row`).
			int to = press.Up ? 0 : offered ? 1 : row;
			return new Fable3BoxAnswers.ScreenStep(box + "row:" + Int(to), Cursor: to != row ? to : (int?)null);
		}

		if (press.Left || press.Right)
		{
			// Every press plays its side's cue (`option`); Sex moves only when the opener lets it cycle.
			int step = press.Right ? 1 : -1;
			string side = box + "option:" + (press.Right ? "right:" : "left:");
			if (row == 0)
			{
				int sleep = (screen.BedSleep + step + 3) % 3;
				return new Fable3BoxAnswers.ScreenStep(side + Int(sleep), Sleep: sleep);
			}

			int sex = cycles ? (screen.BedSex + step + 2) % 2 : screen.BedSex;
			return new Fable3BoxAnswers.ScreenStep(side + Int(sex), Sex: cycles ? sex : (int?)null);
		}

		return new Fable3BoxAnswers.ScreenStep(press.A ? BedAccept(row, screen, box) : press.B ? box + "cancel" : null);
	}

	private static string BedAccept(int row, Fable3ScreenState screen, string box) =>
		row == 1 ? box + "sex:" + Int(screen.BedSex) : box + "sleep:" + Int(screen.BedSleep);

	/// <summary>
	/// A three-row choice screen: up / down move Godot's cursor (clamped; the move stated by `move` for
	/// the mover's cue, which the guest plays only when the row changes), A picks its 1-based row, B closes.
	/// </summary>
	private static Fable3BoxAnswers.ScreenStep Choices(Fable3BoxAnswers.Press press, int cursor, int rows, string pick, string move,
		string close)
	{
		if (press.Up || press.Down)
		{
			int row = System.Math.Clamp(cursor + (press.Down ? 1 : -1), 0, rows - 1);
			return Move(row, cursor, move + Int(row + 1));
		}

		return new Fable3BoxAnswers.ScreenStep(press.A ? pick + Int(cursor + 1) : press.B ? close : null);
	}

	private static Fable3BoxAnswers.ScreenStep Move(int row, int cursor, string lever) =>
		row != cursor ? new Fable3BoxAnswers.ScreenStep(lever, Cursor: row) : default;

	private static string Lever(uint address, int player) =>
		"box:" + Fable3ActingPlayer.Field(player) + ":" + address.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ":";

	private static string Int(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
