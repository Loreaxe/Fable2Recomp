namespace Refii.Titles.Fable3;

using System;

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
/// (`+13 = 1`, the close; 0x825FA0F0). The Options screen, whose cursor and working values are
/// Godot's: A, B, Y or START into its own handler (TU3 sub_825F4F30 / disc sub_825CA410), a list
/// page's row through the row setters its own steps call (sub_825F68A0 / sub_825CBC68, sub_825F69B8 /
/// sub_825CBD80) then A, and a settings row's working value, which its own left / right setters bring
/// the box to (options_hooks.cpp). The save menu: a row its own
/// two steps are called toward (disc sub_8296CC38 / sub_8296CCF8, 0.3 s a row), accepted through
/// its input sub_8296C918 with 72. THE BOX SCREENS Godot owns (<see cref="OwnsScreen"/>; TU3 /
/// disc handlers in display_box_hooks.cpp's header): hero status B (75) closes; a promise A (74) signs
/// while unsigned and B (75) closes once signed, for the pad of player record 1; the king's schedule A
/// (74) from player one closes; the quest summary's cursor is Godot's, stated as a list row (`row`),
/// picked with 72 (`select`, message event 156) or left with 73; the wedding picker's tier is Godot's,
/// handed with 41 (`accept`, event 69, only an affordable tier) or 42 (`decline`, event 70), each move
/// of it stated (`tier`) so the guest still plays the step's cue; the chat channel's mute is Godot's,
/// each change stated (`mute`, its cue), saved with 72 (`chat`) or left with 73; the stats pages' cursor is
/// Godot's (`stats-row`), A is 72 (`stats-open`), B 73 (`stats-back`), Y on the friends' board 77
/// (`stats-card`, the gamer card). Each handler keeps its own player test, so a lever it refuses does
/// nothing and Godot follows only what the guest then states (`@Box.Close`, `@Screen.Cursor`,
/// `@Screen.StatsPage`, the promise's slide).
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

	/// <summary>The box-manager screens (by element root) whose pad, cursor and working value Godot owns.</summary>
	public static bool OwnsScreen(string? root) => root is Fable3ScreenModel.HeroStatus or Fable3ScreenModel.Promises or
		Fable3ScreenModel.Schedule or Fable3ScreenModel.Quests or Fable3ScreenModel.Stats or
		Fable3BoxScreenModel.WeddingPicker or Fable3BoxScreenModel.ChatChannel || Fable3SessionBoxAnswers.Owns(root);

	/// <summary>
	/// What a press or a pointer pick means on an owned box screen: the tick action to post (or none)
	/// and Godot's own working values after it (null = unchanged).
	/// </summary>
	public readonly record struct ScreenStep(string? Lever, int? Tier = null, bool? Muted = null, int? Cursor = null,
		int? Sleep = null, int? Sex = null, bool? StatsAchievements = null, bool OpenAchievements = false)
	{
		public bool Any => Lever is not null || Tier is not null || Muted is not null || Cursor is not null || Sleep is not null ||
			Sex is not null || StatsAchievements is not null || OpenAchievements;
	}

	/// <summary>
	/// The step for <paramref name="press"/> on the owned screen <paramref name="root"/> whose box is
	/// <paramref name="address"/>, for <paramref name="player"/>. <paramref name="screen"/> is the box's
	/// record (the stated cursor, page and windows, Godot's tier); <paramref name="muted"/> the chat
	/// channel's working value now.
	/// </summary>
	public static ScreenStep ForScreen(string root, uint address, Press press, Fable3ScreenState screen, bool muted = false,
		int player = Fable3ActingPlayer.One)
	{
		string box = "box:" + Fable3ActingPlayer.Field(player) + ":" +
			address.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ":";
		int cursor = screen.Cursor ?? 0;
		switch (root)
		{
			case Fable3ScreenModel.HeroStatus:
				return new(press.B ? box + "close" : null);
			case Fable3ScreenModel.Promises:
				return new(press.A ? box + "accept" : press.B ? box + "close" : null);
			case Fable3ScreenModel.Schedule:
				return new(press.A ? box + "accept" : null);
			case Fable3ScreenModel.Quests:
				if (press.Up || press.Down)
				{
					// The guest bounds the row against its list; a row it has not is refused there.
					int row = cursor + (press.Down ? 1 : -1);
					return new ScreenStep(row >= 0 ? box + "row:" + Int(row) : null);
				}

				return new(press.A ? box + "select:" + Int(cursor) : press.B ? box + "close" : null);
			case Fable3BoxScreenModel.WeddingPicker:
				if (press.Up || press.Down)
				{
					// The guest's own clamp: up to 0, down to 2 (TU3 0x82B90F80 / 0x82B90FEC). `tier` states
					// the move, for the step's cue.
					int tier = System.Math.Clamp(screen.Tier + (press.Down ? 1 : -1), 0, 2);
					return tier != screen.Tier ? new ScreenStep(box + "tier:" + Int(tier), tier) : default;
				}

				return new(press.A ? box + "accept:" + Int(screen.Tier) : press.B ? box + "decline:" + Int(screen.Tier) : null);
			case Fable3BoxScreenModel.ChatChannel:
				if (press.Left || press.Right)
				{
					// The guest's rule (TU3 sub_829BB608): left mutes only from audible, right unmutes only
					// from muted. `mute` states the change, for its cue.
					bool to = press.Left;
					return to != muted ? new ScreenStep(box + "mute:" + (to ? "1" : "0"), null, to) : default;
				}

				return new(press.A ? box + "chat:" + (muted ? "1" : "0") : press.B ? box + "close" : null);
			case Fable3ScreenModel.Stats:
			{
				int page = screen.StatsPage is 3 or 4 ? screen.StatsPage.Value : 2;
				bool known = screen.StatsWindows.TryGetValue(page, out (int Start, int Count, int Rows) window);
				// THE ACHIEVEMENTS CATEGORY, the host's, past the guest's last category (Fable3ScreenModel's
				// StatsPage): reached by Down from the last, left by Up; A opens the achievements page.
				if (page == 2 && screen.StatsAchievements)
				{
					return press.Up ? new ScreenStep(null, StatsAchievements: false)
						: press.A ? new ScreenStep(null, StatsAchievements: false, OpenAchievements: true)
						: press.B ? new ScreenStep(box + "stats-back", StatsAchievements: false)
						: default;
				}

				if (press.Up || press.Down)
				{
					int row = cursor + (press.Down ? 1 : -1);
					if (page == 2 && press.Down && known && row == window.Count)
					{
						return new ScreenStep(null, StatsAchievements: true);
					}

					return new(row >= 0 && (!known || row < window.Count) ? box + "stats-row:" + Int(row) : null);
				}

				if (press.A && page != 4)
				{
					return new(box + "stats-open:" + Int(cursor));
				}

				if (press.Y && page == 4)
				{
					return new(box + "stats-card:" + Int(cursor));
				}

				return new(press.B ? box + "stats-back" : null);
			}

			default:
				return Fable3SessionBoxAnswers.ForScreen(root, address, press, screen, player);
		}
	}

	/// <summary>
	/// The step for the pointer on the drawn row named <paramref name="rowName"/> of an owned screen
	/// (the models' row names end in the row's index in the stated window): resting on it moves Godot's
	/// cursor there, a click picks it. <paramref name="clicked"/> is a left click on the row.
	/// </summary>
	public static ScreenStep PointScreen(string root, uint address, string rowName, bool clicked, Fable3ScreenState screen,
		bool muted = false, int player = Fable3ActingPlayer.One)
	{
		string box = "box:" + Fable3ActingPlayer.Field(player) + ":" +
			address.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ":";
		int digits = rowName.Length;
		while (digits > 0 && rowName[digits - 1] is >= '0' and <= '9')
		{
			--digits;
		}

		int index = digits < rowName.Length
			? int.Parse(rowName.AsSpan(digits), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture)
			: -1;
		int cursor = screen.Cursor ?? 0;
		switch (root)
		{
			case Fable3ScreenModel.Quests when index >= 0:
			{
				// The window's selected slot is the cursor's row (sub_829BCA18's `selected`, `@Screen.QuestRow`).
				int selectedSlot = 0;
				foreach ((int slot, (uint _, bool selected, bool _)) in screen.QuestRows)
				{
					if (selected)
					{
						selectedSlot = slot;
						break;
					}
				}

				int row = cursor - selectedSlot + index;
				return row < 0 ? default : new ScreenStep(box + (clicked ? "select:" : "row:") + Int(row));
			}

			case Fable3ScreenModel.Stats when rowName == Fable3ScreenModel.StatsAchievementsRow:
				return clicked ? new ScreenStep(null, StatsAchievements: false, OpenAchievements: true)
					: screen.StatsAchievements ? default : new ScreenStep(null, StatsAchievements: true);
			case Fable3ScreenModel.Stats when index >= 0:
			{
				int page = screen.StatsPage is 3 or 4 ? screen.StatsPage.Value : 2;
				int row = (screen.StatsWindows.TryGetValue(page, out (int Start, int Count, int Rows) window) ? window.Start : 0) + index;
				if (screen.StatsAchievements && !clicked)
				{
					return new ScreenStep(box + "stats-row:" + Int(row), StatsAchievements: false);
				}

				if (!clicked)
				{
					return row == cursor ? default : new ScreenStep(box + "stats-row:" + Int(row));
				}

				return page == 4 ? default : new ScreenStep(box + "stats-open:" + Int(row));
			}

			case Fable3BoxScreenModel.WeddingPicker when index is >= 0 and <= 2:
				return clicked ? new ScreenStep(box + "accept:" + Int(index), index) :
					index != screen.Tier ? new ScreenStep(box + "tier:" + Int(index), index) : default;
			case Fable3BoxScreenModel.ChatChannel:
				return clicked ? new ScreenStep(box + "mute:" + (muted ? "0" : "1"), null, !muted) : default;
			default:
				return Fable3SessionBoxAnswers.PointScreen(root, address, rowName, clicked, screen, player);
		}
	}

	private static string Int(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

	public const int ActionStart = 14;
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
	/// Godot's hold fill of the TopBox's A or Y, in thousandths, for <paramref name="player"/>:
	/// `topbox:&lt;p&gt;:&lt;a|y&gt;:fill:&lt;0..1000&gt;` (topbox_answer.cpp's replaced hold step takes it as the progress).
	/// </summary>
	public static string TopBoxFill(bool a, int thousandths, int player = Fable3ActingPlayer.One) =>
		"topbox:" + Fable3ActingPlayer.Field(player) + ":" + (a ? "a" : "y") + ":fill:" +
		System.Math.Clamp(thousandths, 0, 1000).ToString(System.Globalization.CultureInfo.InvariantCulture);

	/// <summary>
	/// An emotion choice's button for <paramref name="player"/>, as the game's action
	/// <paramref name="code"/> (<see cref="Fable3GameActions.EmotionPressed"/> going down,
	/// <see cref="Fable3GameActions.EmotionReleased"/> coming up): `emotion:&lt;p&gt;:&lt;code&gt;`
	/// (emotion_choice_answer.cpp runs the choice UI's own input with it).
	/// </summary>
	public static string Emotion(int code, int player = Fable3ActingPlayer.One) =>
		"emotion:" + Fable3ActingPlayer.Field(player) + ":" + code.ToString(System.Globalization.CultureInfo.InvariantCulture);

	/// <summary>
	/// The world icon's hold button (A) going down or coming up for <paramref name="player"/>:
	/// `worldicon:&lt;p&gt;:hold:&lt;down|up&gt;` (world_icon_hold.cpp runs the icon registry's own input).
	/// </summary>
	public static string WorldIconHold(bool down, int player = Fable3ActingPlayer.One) =>
		"worldicon:" + Fable3ActingPlayer.Field(player) + ":hold:" + (down ? "down" : "up");

	/// <summary>
	/// A HUD d-pad direction for <paramref name="player"/>, as the game's action <paramref name="code"/>
	/// (<see cref="Fable3GameActions.GuiUp"/>.. going down, <see cref="Fable3GameActions.GuiUpReleased"/>..
	/// coming up): `dpad:&lt;p&gt;:&lt;code&gt;` (hud_dpad_use.cpp runs the d-pad widget's own input).
	/// </summary>
	public static string Dpad(int code, int player = Fable3ActingPlayer.One) =>
		"dpad:" + Fable3ActingPlayer.Field(player) + ":" + code.ToString(System.Globalization.CultureInfo.InvariantCulture);

	/// <summary>
	/// A game action for <paramref name="player"/> into the interaction mode up, as its
	/// <paramref name="code"/> (<see cref="Fable3GameActions"/>): `interaction:&lt;p&gt;:&lt;code&gt;`
	/// (interaction_mode_input.cpp runs the interaction manager's own input with it).
	/// </summary>
	public static string Interaction(int code, int player = Fable3ActingPlayer.One) =>
		"interaction:" + Fable3ActingPlayer.Field(player) + ":" + code.ToString(System.Globalization.CultureInfo.InvariantCulture);

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

	/// <summary>
	/// The tick action for an Options game action by <paramref name="player"/>: `options:&lt;p&gt;:&lt;action&gt;`
	/// with 74 A, 75 B, 77 Y or 14 START. The screen's handler takes its own hero's player alone
	/// (options_hooks.cpp).
	/// </summary>
	public static string Options(int action, int player = Fable3ActingPlayer.One) =>
		"options:" + Fable3ActingPlayer.Field(player) + ":" + action.ToString(System.Globalization.CultureInfo.InvariantCulture);

	/// <summary>
	/// The tick action that states Godot's cursor on the Options screen's 0-based <paramref name="row"/>
	/// (the screen's own rows are 1-based) and, with <paramref name="accept"/>, has A pressed on it,
	/// for <paramref name="player"/>: `options-row:&lt;p&gt;:&lt;row&gt;[:accept]` (options_hooks.cpp). On a
	/// settings page it is the guest row the screen's own cursor is walked to. Null for no row.
	/// </summary>
	public static string? OptionsRow(int row, bool accept, int player = Fable3ActingPlayer.One) =>
		row < 0 ? null :
		"options-row:" + Fable3ActingPlayer.Field(player) + ":" +
		(row + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + (accept ? ":accept" : string.Empty);

	/// <summary>
	/// The tick action that hands the guest Godot's working <paramref name="value"/> of Options settings
	/// page <paramref name="page"/>'s 1-based <paramref name="guestRow"/> (a bar 0..1 on the 0.025 grid, a
	/// toggle 0 / 1, a count), for <paramref name="player"/>:
	/// `options-value:&lt;p&gt;:&lt;page&gt;:&lt;row&gt;:&lt;value&gt;` (options_hooks.cpp; dropped there on any
	/// other page). Null for no row or a value below 0.
	/// </summary>
	public static string? OptionsValue(int page, int guestRow, float value, int player = Fable3ActingPlayer.One) =>
		guestRow < 1 || value < 0f || float.IsNaN(value) ? null :
		"options-value:" + Fable3ActingPlayer.Field(player) + ":" +
		page.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" +
		guestRow.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" +
		value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

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
