namespace Refii.Titles.Fable3;

/// <summary>
/// What a press on Fable 3's boxes, menu box and Options screen means, as the guest tick action
/// that carries Godot's answer to the box's own answer function (Fable3/src/hooks/src/
/// display_box_hooks.cpp, options_hooks.cpp). Pure logic, so the tests can run it.
/// </summary>
/// <remarks>
/// THE ANSWERS ARE THE GUEST'S OWN CALLS, each carrying the player who pressed
/// (<see cref="Fable3ActingPlayer"/>) for the handler's own gates. A question and the natal query:
/// the handler with 41 / 42, whose answer function (TU3 sub_82879AD8 / sub_82B93DB0, disc
/// sub_8281BD88) posts message event 68 with it (disc 0x8281BE08). The numeric
/// adjuster: its value stored where its answer reads it (+112, 0x8281C688), then its handler with
/// 41 / 42 (sub_8281C638(box, accept) behind its gates, posting event 69 or 70). A message: its handler with 73
/// (GUI_CANCEL: the cue and the close, 0x8281B3D0..0x8281B404), or 76 on the download form (+33,
/// 0x8281B418). A quest box: its handler with 73 (event 67, 0x8281CBF0). A tutorial notice: its
/// handler with 41 (the close, 0x824B29F0). An info card: a press card's handler with 74 or 77
/// (PostButtonAnswer 1 or 4, event 64); a held card's A is Godot's hold (<see cref="CardHoldSeconds"/>),
/// whose full fill posts PostButtonAnswer(1) as the guest's own fill did. The button box: its handler
/// with the release code of the face button (79 A, 80 B, 78 X, 81 Y → event 64 with 1, 2, 3, 4).
/// The TopBox: its own input with the pressed, held and released codes of A or Y. The pause screen's message box: the screen input
/// sub_825CE380 with 72 (GUI_SELECT, 0x825CE3B4), or 76 on its download form (0x825CE3D8). The
/// menu box: its highlight sub_825F9F38(MB, row) then its select sub_825FA850(MB), or its cancel
/// (`+13 = 1`, the close; 0x825FA0F0). The Options screen: one game action into its handler
/// sub_825CA410 — 29/30 up/down, 31/32 left/right, 74 A, 75 B, 77 Y — or a row stated to the
/// row setters its own steps call (disc sub_825CBC68 / sub_825CBD80), or on pages 3 and 4, which
/// have none, stepped to through the handler at its own repeat rate. The save menu: a row its own
/// two steps are called toward (disc sub_8296CC38 / sub_8296CCF8, 0.3 s a row), accepted through
/// its input sub_8296C918 with 72.
/// </remarks>
public static class Fable3BoxAnswers
{
	/// <summary>The presses a box reads this frame: each true on the frame its button went down.</summary>
	public readonly record struct Press(bool A, bool B, bool X, bool Y, bool Up, bool Down, bool Left, bool Right)
	{
		public bool Any => A || B || X || Y || Up || Down || Left || Right;
	}

	/// <summary>The box kinds (display_box_hooks.cpp's `KindOf`) whose pad Godot owns.</summary>
	public static bool Owns(string kind) =>
		kind is "message" or "question" or "adjuster" or "quest" or "notice" or "modal" or "card" or "buttons";

	/// <summary>
	/// How long the info card's A takes to hold full. The guest's fill moves by `*(GUI+0xB0)` a GUI
	/// update (sub_82C3A130), which its update writes as the world ticks since the last update times
	/// 1 / 15.0 (TU3 0x8225CE6C..0x8225CE9C with dbl_832DF4D0, disc 0x8225DCE4..0x8225DD14 with
	/// dbl_8329EA48, both 15.0) — 15.0 being the game tick rate (game_tick_pump.cpp) — so empty to full
	/// is one second.
	/// </summary>
	public const double CardHoldSeconds = 1.0;

	public const int ActionUp = 29;
	public const int ActionDown = 30;
	public const int ActionLeft = 31;
	public const int ActionRight = 32;
	public const int ActionA = 74;
	public const int ActionB = 75;
	public const int ActionY = 77;

	/// <summary>
	/// The tick action that answers the box at <paramref name="address"/> of <paramref name="kind"/>
	/// for <paramref name="press"/>, or null when the press is not an answer.
	/// <paramref name="adjusterValue"/> is the value Godot's adjuster holds; <paramref name="download"/>
	/// whether the message is the download form, whose X is its own action; <paramref name="player"/>
	/// the local player who pressed (1 or 2); <paramref name="holdCard"/> whether an info card's A must
	/// be held. The action is `box:&lt;p&gt;:&lt;HEX&gt;:&lt;verb&gt;[:&lt;value&gt;]`.
	/// </summary>
	public static string? ForBox(string kind, uint address, Press press, int adjusterValue = 0, bool download = false,
		int player = Fable3ActingPlayer.One, bool holdCard = false)
	{
		string box = "box:" + Fable3ActingPlayer.Field(player) + ":" +
			address.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ":";
		switch (kind)
		{
			case "question":
				return press.A ? box + "yes" : press.B ? box + "no" : null;
			case "adjuster":
				return press.A ? box + "accept:" + adjusterValue.ToString(System.Globalization.CultureInfo.InvariantCulture) :
					press.B ? box + "cancel" : null;
			case "message":
				if (press.X && download)
				{
					return box + "action";
				}

				return press.A || press.B ? box + "dismiss" : null;
			case "modal":
				// sub_825CE380 takes only 72 (GUI_SELECT) and, on the download form, 76.
				if (press.X && download)
				{
					return box + "action";
				}

				return press.A ? box + "dismiss" : null;
			case "quest":
				return press.A || press.B ? box + "dismiss" : null;
			case "notice":
				return press.A ? box + "dismiss" : null;
			case "card":
				// A press card's A and a card's Y: their own handler (74, 77). A held card's A is its
				// hold's (CardHold), never a press.
				return press.A && !holdCard ? box + "accept" : press.Y ? box + "help" : null;
			case "buttons":
				// The button box answers every face button, on its release codes: 1 A, 2 B, 3 X, 4 Y.
				return press.A ? box + "button:1" : press.B ? box + "button:2" :
					press.X ? box + "button:3" : press.Y ? box + "button:4" : null;
			default:
				return null;
		}
	}

	/// <summary>The held info card's accept once Godot's hold is full: `box:&lt;p&gt;:&lt;HEX&gt;:accept`.</summary>
	public static string CardHold(uint address, int player = Fable3ActingPlayer.One) =>
		"box:" + Fable3ActingPlayer.Field(player) + ":" +
		address.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ":accept";

	/// <summary>
	/// The TopBox's A or Y going down or up for <paramref name="player"/>: `topbox:&lt;p&gt;:&lt;a|y&gt;:&lt;down|up&gt;`
	/// (topbox_answer.cpp hands it to the TopBox's own input).
	/// </summary>
	public static string TopBox(bool a, bool down, int player = Fable3ActingPlayer.One) =>
		"topbox:" + Fable3ActingPlayer.Field(player) + ":" + (a ? "a" : "y") + ":" + (down ? "down" : "up");

	/// <summary>
	/// Picks row <paramref name="row"/> (0-based) of the open menu box for <paramref name="player"/>, or
	/// null: the guest's mapper selects for player one alone (`cmpwi r4,1`, TU3 0x82B512D4).
	/// </summary>
	public static string? MenuPick(int row, int player = Fable3ActingPlayer.One) =>
		player == Fable3ActingPlayer.One
			? "menubox:1:" + row.ToString(System.Globalization.CultureInfo.InvariantCulture)
			: null;

	/// <summary>Cancels the open menu box for <paramref name="player"/> (the guest takes it only when the box is cancelable; either player's B cancels).</summary>
	public static string MenuCancel(int player = Fable3ActingPlayer.One) => "menubox-cancel:" + Fable3ActingPlayer.Field(player);

	/// <summary>The index of the first of <paramref name="rows"/> holding <paramref name="at"/> (a pointer on a drawn row), or -1.</summary>
	public static int RowAt(System.Collections.Generic.IReadOnlyList<Godot.Rect2> rows, Godot.Vector2 at)
	{
		for (int i = 0; i < rows.Count; ++i)
		{
			if (rows[i].HasPoint(at))
			{
				return i;
			}
		}

		return -1;
	}

	/// <summary>The menu box's highlight after an up or down, wrapping as the guest's handler does (0x825FA138..0x825FA14C).</summary>
	public static int MenuStep(int highlight, int rows, bool down)
	{
		if (rows <= 0)
		{
			return 0;
		}

		int next = highlight + (down ? 1 : -1);
		return next >= rows ? 0 : next < 0 ? rows - 1 : next;
	}

	/// <summary>The Options screen's game action for a press, or null; A wins over a direction pressed with it.</summary>
	public static int? OptionsAction(Press press) =>
		press.A ? ActionA :
		press.B ? ActionB :
		press.Y ? ActionY :
		press.Up ? ActionUp :
		press.Down ? ActionDown :
		press.Left ? ActionLeft :
		press.Right ? ActionRight :
		null;

	/// <summary>
	/// The tick action for an Options game action by <paramref name="player"/>: `options:&lt;p&gt;:&lt;action&gt;`.
	/// The screen's handler takes its own hero's player alone (options_hooks.cpp).
	/// </summary>
	public static string Options(int action, int player = Fable3ActingPlayer.One) =>
		"options:" + Fable3ActingPlayer.Field(player) + ":" + action.ToString(System.Globalization.CultureInfo.InvariantCulture);

	/// <summary>
	/// The tick action that states the Options screen's 0-based <paramref name="row"/> (the
	/// screen's own rows are 1-based) and, with <paramref name="accept"/>, has A pressed on it
	/// once the cursor is there, for <paramref name="player"/>: `options-row:&lt;p&gt;:&lt;row&gt;[:accept]`
	/// (options_hooks.cpp). Null for no row.
	/// </summary>
	public static string? OptionsRow(int row, bool accept, int player = Fable3ActingPlayer.One) =>
		row < 0 ? null :
		"options-row:" + Fable3ActingPlayer.Field(player) + ":" +
		(row + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + (accept ? ":accept" : string.Empty);

	/// <summary>
	/// Whether a click on a row of Options page <paramref name="page"/> also accepts it: on the list
	/// pages (1 Options, 2 Save Management, 5 Multiplayer) A enters or asks; on the settings pages
	/// (3, 4) A commits the whole page and leaves it (sub_825F70F8, 0x825F6144), so a click there only
	/// puts the cursor on the row.
	/// </summary>
	public static bool OptionsClickAccepts(int page) => page is 1 or 2 or 5;

	/// <summary>
	/// The game actions a wheel's <paramref name="steps"/> are on an Options settings page's cursor
	/// row: each notch up is a right step (32), each down a left step (31) — the row's own setters,
	/// applied live as the pad's left and right are.
	/// </summary>
	public static int[] OptionsWheelSteps(int steps)
	{
		var actions = new int[System.Math.Abs(steps)];
		System.Array.Fill(actions, steps > 0 ? ActionRight : ActionLeft);
		return actions;
	}

	/// <summary>The save menu's rows: 1 Save, 2 Save and Quit, 3 Quit, 4 Character Management.</summary>
	public const int SaveMenuRows = 4;

	/// <summary>
	/// The tick action that states the save menu's 0-based <paramref name="row"/> and, with
	/// <paramref name="accept"/>, has it selected for <paramref name="player"/> once the cursor is there:
	/// `savemenu-row:&lt;p&gt;:&lt;row&gt;[:accept]` (save_menu_row.cpp). Null for no row of the menu.
	/// </summary>
	public static string? SaveMenuRow(int row, bool accept, int player = Fable3ActingPlayer.One) =>
		row < 0 || row >= SaveMenuRows ? null :
		"savemenu-row:" + Fable3ActingPlayer.Field(player) + ":" +
		(row + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + (accept ? ":accept" : string.Empty);
}
