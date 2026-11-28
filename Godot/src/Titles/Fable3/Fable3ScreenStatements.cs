namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Godot;
using Refii.Ui;

/// <summary>
/// What the guest STATED for one box-manager screen, by its box: its opener's and its Show's
/// arguments (`@Screen.Opened`, `@Screen.Online`, `@Screen.Friends`, `@Screen.Bed`, `@Screen.Divorce`,
/// …) and the rows its writers were called with (quest summary, king's schedule, co-op partners). The
/// screen models (<see cref="Fable3ScreenModel"/>, <see cref="Fable3BoxScreenModel"/>) draw the
/// highlight and the ghosting from this, never from the elements' slides. Every box screen's pad is
/// Godot's (<see cref="Fable3BoxAnswers.OwnsScreen"/>), so the record also holds Godot's own working
/// values — the cursor, the wedding tier, the chat channel's mute, the bed's two options — which live
/// and die with the box.
/// </summary>
public sealed class Fable3ScreenState
{
	public static readonly Fable3ScreenState Empty = new();

	/// <summary>The opener's / ctor's arguments: bed (offered, group, cycle), wedding (three affordable bytes).</summary>
	public int[] Opened { get; set; } = System.Array.Empty<int>();

	/// <summary>The partners' window rows by row path: sub_82B5D9E0's record (married, online, joinable).</summary>
	public Dictionary<string, (bool Married, bool Online, bool Joinable)> PartnerRows { get; } = new(System.StringComparer.Ordinal);

	/// <summary>The king's schedule rows 1..4 as its Show stated them: the tick slide (4 = no task, 2 = stamped) and the task's tag.</summary>
	public SortedDictionary<int, (int Tick, uint Tag)> ScheduleRows { get; } = new();

	/// <summary>The king's schedule's title tag, day line tag and day (-1 = no day line) as its Show stated them, null before it.</summary>
	public (uint TitleTag, uint DayTag, int Day)? Schedule { get; set; }

	/// <summary>The quest summary's window rows 0..6: sub_82973FB0's quest, selected and last.</summary>
	public SortedDictionary<int, (uint Quest, bool Selected, bool Last)> QuestRows { get; } = new();

	/// <summary>
	/// The quest summary's row texts by window slot, as the row writer built them before its `Pres`
	/// (owned_screen_texts.cpp `@Screen.QuestRowText`): the quest's name (a text NAME, resolved as
	/// `@Ui.TextName` is), its location and its guild seals (texts, "" for none).
	/// </summary>
	public SortedDictionary<int, (string Name, string Location, string Seals)> QuestRowTexts { get; } = new();

	/// <summary>The stats page as sub_8296EC88 was asked to move it (2 categories, 3 statistics, 4 friends), null before.</summary>
	public int? StatsPage { get; set; }

	/// <summary>The stats pages' windows by page (2 categories, 3 statistics, 4 friends): the first row, the list's count and the rows drawn.</summary>
	public Dictionary<int, (int Start, int Count, int Rows)> StatsWindows { get; } = new();

	/// <summary>
	/// The stats pages' lists as their writers stated them (stats_screen_stubs.cpp `@Screen.StatsList`), by
	/// page (2 categories, 3 statistics, 4 friends): the count, the guest's window rows and first row, the
	/// statistics page's online test, the title's tag and the A caption's tag (0: no A on that page).
	/// </summary>
	public Dictionary<int, StatsList> StatsLists { get; } = new();

	/// <summary>The stats pages' rows (`@Screen.StatsRow`) by page, then by the row's index in the whole list.</summary>
	public Dictionary<int, SortedDictionary<int, StatsRow>> StatsRows { get; } = new();

	/// <summary>The board around the player for the statistic under the cursor (`@Screen.StatsBoard`), lines 1..3.</summary>
	public SortedDictionary<int, StatsBoardLine> StatsBoard { get; } = new();

	/// <summary>
	/// The player the box belongs to as its Show cached it (`@Screen.Owner`: the stats box's +0x40, the
	/// value its own handler tests a press's player against); null before it.
	/// </summary>
	public int? Owner { get; set; }

	/// <summary>The promise ctor's tag and accepted argument (sub_827DA738 / disc sub_827A3290, r5 and r6), null before it.</summary>
	public (uint Tag, bool Accepted)? Promise { get; set; }

	/// <summary>
	/// The chat channel's Show as stated: the saved mute, its tag, and the two descriptions' tags
	/// (muted, audible); null before it.
	/// </summary>
	public (bool Saved, uint SavedTag, uint MutedTag, uint AudibleTag)? Chat { get; set; }

	/// <summary>
	/// The list row Godot's cursor is on, as the row lever stated it stored (`@Screen.Cursor`: quest
	/// summary, stats); null before the first, which is row 0 (the ctors' and the page setter's 0).
	/// </summary>
	public int? Cursor { get; set; }

	/// <summary>
	/// GODOT'S: on the stats box's categories page, the cursor is on the host's Achievements category, past the
	/// guest's last (<see cref="Fable3ScreenModel"/> draws it; A opens <see cref="Fable3AchievementsPage"/>). The
	/// guest's own cursor stays on its last category meanwhile: the row is not the guest's.
	/// </summary>
	public bool StatsAchievements { get; set; }

	/// <summary>GODOT'S: the wedding picker's tier 0..2 (the ctor starts the guest's on 0, sub_82B90948).</summary>
	public int Tier { get; set; }

	/// <summary>GODOT'S: the chat channel's working mute, null until the player changes it (until then the Show's stated saved mute).</summary>
	public bool? Muted { get; set; }

	/// <summary>
	/// The join / visibility options' Show as stated (session_boxes.cpp): the type (18 join, 19
	/// visibility), the row 1..3 of the setting it starts on and that setting's tag; null before it.
	/// </summary>
	public (int Type, int Row, uint Tag)? Online { get; set; }

	/// <summary>The friends channel's Show as stated: whether its invite row takes A (TU3: not in a live game); null before it.</summary>
	public bool? InviteAllowed { get; set; }

	/// <summary>
	/// The bed menu's Show as stated (family_boxes.cpp): the Sleep and Sex titles' tags, the three sleep
	/// options' and descriptions' tags, the two sex options' and descriptions' tags; null before it.
	/// </summary>
	public BedTags? Bed { get; set; }

	/// <summary>The divorce box's ctor as stated: its pad, page mode 0..5, starting reason and fee; null before it.</summary>
	public (int Pad, int Mode, int Reason, int Fee)? Divorce { get; set; }

	/// <summary>GODOT'S: the bed menu's sleep option 0..2 and sex option 0..1 (the opener starts both on 0, sub_82623738).</summary>
	public int BedSleep { get; set; }

	public int BedSex { get; set; }

	/// <summary>The screen root's slide the screen's own element write last stated (the treasury's page), set by <see cref="Fable3StatedScreen"/>.</summary>
	public int? RootState { get; set; }

	/// <summary>The save menu (no box): its row 1 Save, 2 Save and Quit, 3 Quit, 4 Character Management, and its `allowed`.</summary>
	public int SaveRow { get; set; }

	public bool SaveAllowed { get; set; } = true;

	public bool OpenedFlag(int index, bool fallback) => index < Opened.Length ? Opened[index] != 0 : fallback;

	/// <summary>One stats page's list as its writer stated it.</summary>
	public sealed record StatsList(int Count, int Rows, int Start, bool Online, uint TitleTag, uint AcceptTag);

	/// <summary>
	/// One stats row: its label by tag (categories, statistics) or literally (a friend's gamertag), its
	/// value text, its rank text ("" for none) and the offline rank's tag (0 for none).
	/// </summary>
	public sealed record StatsRow(uint LabelTag, string Label, string Value, string Rank, uint RankTag);

	/// <summary>One line of the board around the player: whether it is the player's own, its gamertag, score and rank.</summary>
	public sealed record StatsBoardLine(bool Mine, string GamerTag, string Score, string Rank);

	/// <summary>The bed menu's stated tags (sub_82B8DB48's statics, by row and option).</summary>
	public sealed record BedTags(uint SleepTitle, uint SexTitle, uint[] Sleep, uint[] SleepAbout, uint[] Sex, uint[] SexAbout);
}

/// <summary>
/// The box-manager screens' statements (Fable3/src/hooks/src/screen_stated_hooks.cpp), held per
/// box until the box closes: `@Screen.Opened(box, kind, args…)`, `@Screen.PartnerRow(box, path, married, online, joinable)`,
/// `@Screen.ScheduleRow(box, row, tick, tag)`, `@Screen.QuestRow(box, row, quest, selected, last)`,
/// `@Screen.StatsPage(box, page)`, `@Screen.StatsWindow(box, page, start, count, rows)`,
/// `@Screen.StatsList(box, page, count, rows, start, online, title tag, A tag)`, `@Screen.StatsRow(box, page,
/// index, label tag, label, value, rank, rank tag)`, `@Screen.StatsBoard(box, line, mine, gamertag, score,
/// rank)` and `@Screen.Owner(box, player)` (stats_screen_stubs.cpp),
/// `@Screen.Promise(box, tag, accepted)`, `@Screen.Cursor(box, row)` (a row lever's stored row,
/// display_box_hooks.cpp), `@Screen.Schedule(box, title tag, day tag, day)` (the king's
/// schedule's Show) and `@Screen.Chat(box, saved, saved tag, muted tag, audible tag)` (the chat
/// channel's Show; both display_box_stubs.cpp), `@Screen.Online(box, type, row, tag)` and
/// `@Screen.Friends(box, invite)` (session_boxes.cpp), `@Screen.Bed(box, tags…)` and
/// `@Screen.Divorce(box, pad, mode, reason, fee)` (family_boxes.cpp).
/// </summary>
public static class Fable3ScreenStatements
{
	private static readonly Dictionary<uint, Fable3ScreenState> Boxes = new();

	private static readonly Dictionary<string, System.Action<Engine_UiModel.GuiNativeCall>> Routes = BuildRoutes();

	/// <summary>Bumped on every statement here.</summary>
	public static ulong Generation { get; private set; }

	/// <summary>A working value of Godot's own changed with no lever behind it: the screen composes again.</summary>
	public static void Touch() => ++Generation;

	public static System.Action<string> Log { get; set; } = message => GD.Print(message);

	/// <summary>The stated record of a box, or <see cref="Fable3ScreenState.Empty"/> when nothing was stated for it.</summary>
	public static Fable3ScreenState Of(uint box) =>
		Boxes.TryGetValue(box, out Fable3ScreenState? state) ? state : Fable3ScreenState.Empty;

	/// <summary>A box closed or was dropped: its address is the next box's.</summary>
	public static void Forget(uint box)
	{
		if (Boxes.Remove(box))
		{
			++Generation;
		}
	}

	public static void Clear()
	{
		Boxes.Clear();
		++Generation;
	}

	public static void Register(Engine_UiGuiNatives router)
	{
		foreach (KeyValuePair<string, System.Action<Engine_UiModel.GuiNativeCall>> route in Routes)
		{
			System.Action<Engine_UiModel.GuiNativeCall> handler = route.Value;
			router.Register(route.Key, call => handler(call));
		}
	}

	/// <summary>Takes one statement as the router would (the tests' entry); false when it is not this model's.</summary>
	public static bool Take(Engine_UiModel.GuiNativeCall call)
	{
		if (!Routes.TryGetValue(call.Name, out System.Action<Engine_UiModel.GuiNativeCall>? handler))
		{
			return false;
		}

		handler(call);
		return true;
	}

	/// <summary>The record of <paramref name="box"/> for Godot's own working values, made when nothing was stated for it yet.</summary>
	public static Fable3ScreenState Working(uint box) => At(box);

	/// <summary>
	/// The screen root's slide the screen's own element write stated (<see cref="Fable3StatedScreen"/>,
	/// for the screens whose presentation is still the guest's: the treasury's page); a change only.
	/// </summary>
	public static void NoteRootState(uint box, int? state)
	{
		if (box != 0u && Of(box).RootState != state)
		{
			At(box).RootState = state;
		}
	}

	private static Fable3ScreenState At(Engine_UiModel.GuiNativeCall call) => At(Fable3Elements.Parse(call.Argument(0).Text));

	private static Fable3ScreenState At(uint box)
	{
		if (!Boxes.TryGetValue(box, out Fable3ScreenState? state))
		{
			if (Boxes.Count > 256)
			{
				Boxes.Clear();
			}

			Boxes[box] = state = new Fable3ScreenState();
		}

		++Generation;
		return state;
	}

	private static int[] Numbers(Engine_UiModel.GuiNativeCall call, int from)
	{
		int count = call.Arguments is { } arguments ? System.Math.Max(0, arguments.Count - from) : 0;
		var values = new int[count];
		for (int i = 0; i < count; ++i)
		{
			values[i] = (int)call.Argument(from + i).Number;
		}

		return values;
	}

	private static Dictionary<string, System.Action<Engine_UiModel.GuiNativeCall>> BuildRoutes()
	{
		var routes = new Dictionary<string, System.Action<Engine_UiModel.GuiNativeCall>>(System.StringComparer.Ordinal);
		void Add(string name, System.Action<Engine_UiModel.GuiNativeCall> handler) => routes[name] = handler;

		Add("@Screen.Online", call =>
		{
			Fable3ScreenState state = At(call);
			state.Online = ((int)call.Argument(1).Number, (int)call.Argument(2).Number, Fable3Elements.Parse(call.Argument(3).Text));
			state.Cursor = null;
			Log($"[fable3-screens] {(state.Online.Value.Type == 19 ? "visibility" : "join")} box {call.Argument(0).Text} starts on row {state.Online.Value.Row}");
		});
		Add("@Screen.Friends", call => At(call).InviteAllowed = call.Argument(1).Boolean);
		Add("@Screen.Bed", call =>
		{
			uint Tag(int index) => Fable3Elements.Parse(call.Argument(index).Text);
			At(call).Bed = new Fable3ScreenState.BedTags(Tag(1), Tag(2), new[] { Tag(3), Tag(4), Tag(5) },
				new[] { Tag(6), Tag(7), Tag(8) }, new[] { Tag(9), Tag(10) }, new[] { Tag(11), Tag(12) });
		});
		Add("@Screen.Divorce", call =>
		{
			Fable3ScreenState state = At(call);
			state.Divorce = ((int)call.Argument(1).Number, (int)call.Argument(2).Number, (int)call.Argument(3).Number,
				(int)call.Argument(4).Number);
			Log($"[fable3-screens] divorce box {call.Argument(0).Text}: mode {state.Divorce.Value.Mode}, pad {state.Divorce.Value.Pad}, fee {state.Divorce.Value.Fee}");
		});
		Add("@Screen.Opened", call =>
		{
			Fable3ScreenState state = At(call);
			state.Opened = Numbers(call, 2);
			Log($"[fable3-screens] {call.Argument(1).Text} box {call.Argument(0).Text} opened with {string.Join(' ', state.Opened)}");
		});
		Add("@Screen.PartnerRow", call => At(call).PartnerRows[call.Argument(1).Text] =
			(call.Argument(2).Boolean, call.Argument(3).Boolean, call.Argument(4).Boolean));
		Add("@Screen.ScheduleRow", call => At(call).ScheduleRows[(int)call.Argument(1).Number] =
			((int)call.Argument(2).Number, Fable3Elements.Parse(call.Argument(3).Text)));
		Add("@Screen.QuestRow", call => At(call).QuestRows[(int)call.Argument(1).Number] =
			(Fable3Elements.Parse(call.Argument(2).Text), call.Argument(3).Boolean, call.Argument(4).Boolean));
		Add("@Screen.QuestRowText", call => At(call).QuestRowTexts[(int)call.Argument(1).Number] =
			(call.Argument(2).Text, call.Argument(3).Text, call.Argument(4).Text));
		Add("@Screen.StatsPage", call =>
		{
			// The setter is stated as asked; it returns at once on the page it is on, and on a new one
			// puts its cursor on row 0 (`stw r11,0x44(r31)` TU3 0x829B73F8).
			Fable3ScreenState state = At(call);
			int page = (int)call.Argument(1).Number;
			if ((state.StatsPage ?? 2) != page)
			{
				state.Cursor = 0;
				Log($"[fable3-stated] stats page {page} (box {call.Argument(0).Text})");
			}

			state.StatsPage = page;
		});
		Add("@Screen.Cursor", call => At(call).Cursor = (int)call.Argument(1).Number);
		Add("@Screen.Chat", call => At(call).Chat = (call.Argument(1).Boolean, Fable3Elements.Parse(call.Argument(2).Text),
			Fable3Elements.Parse(call.Argument(3).Text), Fable3Elements.Parse(call.Argument(4).Text)));
		Add("@Screen.Schedule", call => At(call).Schedule =
			(Fable3Elements.Parse(call.Argument(1).Text), Fable3Elements.Parse(call.Argument(2).Text), (int)call.Argument(3).Number));
		Add("@Screen.StatsList", call =>
		{
			Fable3ScreenState state = At(call);
			int page = (int)call.Argument(1).Number;
			state.StatsLists[page] = new Fable3ScreenState.StatsList((int)call.Argument(2).Number, (int)call.Argument(3).Number,
				(int)call.Argument(4).Number, call.Argument(5).Boolean, Fable3Elements.Parse(call.Argument(6).Text),
				Fable3Elements.Parse(call.Argument(7).Text));
			state.StatsRows[page] = new SortedDictionary<int, Fable3ScreenState.StatsRow>();
			if (page == 3)
			{
				state.StatsBoard.Clear();
			}
		});
		Add("@Screen.StatsRow", call =>
		{
			Fable3ScreenState state = At(call);
			int page = (int)call.Argument(1).Number;
			if (!state.StatsRows.TryGetValue(page, out SortedDictionary<int, Fable3ScreenState.StatsRow>? rows))
			{
				state.StatsRows[page] = rows = new SortedDictionary<int, Fable3ScreenState.StatsRow>();
			}

			rows[(int)call.Argument(2).Number] = new Fable3ScreenState.StatsRow(Fable3Elements.Parse(call.Argument(3).Text),
				call.Argument(4).Text, call.Argument(5).Text, call.Argument(6).Text, Fable3Elements.Parse(call.Argument(7).Text));
		});
		Add("@Screen.StatsBoard", call =>
		{
			int line = (int)call.Argument(1).Number;
			At(call).StatsBoard[line] = new Fable3ScreenState.StatsBoardLine(line == (int)call.Argument(2).Number,
				call.Argument(3).Text, call.Argument(4).Text, call.Argument(5).Text);
		});
		Add("@Screen.Owner", call => At(call).Owner = (int)call.Argument(1).Number);
		Add("@Screen.StatsWindow", call => At(call).StatsWindows[(int)call.Argument(1).Number] =
			((int)call.Argument(2).Number, (int)call.Argument(3).Number, (int)call.Argument(4).Number));
		Add("@Screen.Promise", call =>
		{
			At(call).Promise = (Fable3Elements.Parse(call.Argument(1).Text), call.Argument(2).Boolean);
			Log($"[fable3-stated] promise tag {call.Argument(1).Text} accepted {call.Argument(2).Boolean}");
		});

		return routes;
	}
}
