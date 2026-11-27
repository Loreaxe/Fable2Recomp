namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Godot;
using Refii.Game;
using Refii.Host;
using Refii.Text;
using Refii.Ui;

/// <summary>One of the Options screen's settings: the box field each settings page's row holds.</summary>
public enum Fable3OptionSetting
{
	Invert,
	Subtitles,
	Tutorial,
	Gamma,
	Breadcrumb,
	Music,
	Sfx,
	Voice,
	Speaker,
	Language,
}

/// <summary>
/// Fable 3's Options screen in the PC layout, as Godot owns it: the settings, the guest's row of
/// each, the four Game Settings tabs, the steps, the captions. Pure logic, so the tests can run it.
/// </summary>
/// <remarks>
/// <para>THE ROWS AND THEIR STEPS ARE THE GUEST'S (options_hooks.cpp, both builds the same): page 3
/// (Game Settings) rows 1..9 are invert aim +31, subtitles +32, tutorial boxes +33, gamma +128,
/// breadcrumb +104, music +116, sound effects +120, voice +124, speaker +100; page 4 (the front
/// end's) rows 1..7 language +64, subtitles, gamma, music, sound effects, voice, speaker — the order
/// of the right setter sub_825F55B8's two switches. Its steps: a toggle goes to 1 on right and 0 on
/// left; a bar moves 0.025 and stops at 0 and 1; breadcrumb 0..4, speaker 0..2 and the language
/// 0..count-1 move one. The value tags are the tables the row writer reads: ON/OFF
/// TEXT_GUI_OPTIONS_OFF / _ON, breadcrumb GUI_OPTIONS_BCRUMB_OFF .. _VERYHIGH, speaker
/// GUI_SCREEN_OPTIONS_AUDIO_TV / _HIFI / _SURROUND; the labels the row elements' tags in
/// options.bgf (GUI_SCREEN_OPTIONS_*).</para>
/// <para>THE TABS ARE THE PC BUILD'S (Options › Game Settings: Display, Controller, Audio, Misc;
/// research f3_pc §F): the 360's settings grouped under them, plus the host's own rows — Controller
/// opens the shared Controls page (rebinding, sensitivity, vibration, prompts). One commit still
/// takes the whole page: A is the guest's commit on every tab, B its back, Y its defaults.</para>
/// <para>THE LIST PAGES are the A arm sub_825F60A8's rows: pages 1 and 5 row 1 Save Management, row
/// 2 Game Settings, row 3 New Game, row 4 Kick / Leave (multiplayer); page 2 row 1 Save and
/// Continue (ghosted while the save is not allowed, +36), row 2 Load (ghosted while the guest does
/// not offer it: TU3 +192, the disc's +112 != 0). Labels are the rows' tags in options.bgf; the texts
/// the guest writes into the section's elements win when stated.</para>
/// </remarks>
public static class Fable3OptionsModel
{
	/// <summary>One row: a stable name, its label (resolved), its value (resolved), its description tag, whether it is ghosted, its bar value.</summary>
	public readonly record struct Row(string Name, string Label, string Value, string Description, bool Ghosted = false, float? Bar = null);

	public const int PageOptions = 1;
	public const int PageSaveManagement = 2;
	public const int PageGameSettings = 3;
	public const int PageGameSettingsLite = 4;
	public const int PageMultiplayer = 5;

	public const int Settings = 10;

	/// <summary>The setters' bar step.</summary>
	public const float BarStep = 0.025f;

	/// <summary>The value tags, as the guest's tables order them.</summary>
	public static readonly string[] OnOff = { "TEXT_GUI_OPTIONS_OFF", "TEXT_GUI_OPTIONS_ON" };

	public static readonly string[] Breadcrumbs =
		{ "GUI_OPTIONS_BCRUMB_OFF", "GUI_OPTIONS_BCRUMB_LOW", "GUI_OPTIONS_BCRUMB_MEDIUM", "GUI_OPTIONS_BCRUMB_HIGH", "GUI_OPTIONS_BCRUMB_VERYHIGH" };

	public static readonly string[] Speakers =
		{ "GUI_SCREEN_OPTIONS_AUDIO_TV", "GUI_SCREEN_OPTIONS_AUDIO_HIFI", "GUI_SCREEN_OPTIONS_AUDIO_SURROUND" };

	/// <summary>The PC layout's Game Settings tabs, in its order (our words).</summary>
	public static readonly string[] TabWords = { "REFII_F3_TAB_DISPLAY", "REFII_F3_TAB_CONTROLLER", "REFII_F3_TAB_AUDIO", "REFII_F3_TAB_MISC" };

	public const int TabDisplay = 0;
	public const int TabController = 1;
	public const int TabAudio = 2;
	public const int TabMisc = 3;

	public static bool IsSettingsPage(int page) => page is PageGameSettings or PageGameSettingsLite;

	/// <summary>The guest's 1-based row of <paramref name="setting"/> on <paramref name="page"/>, 0 when the page has none.</summary>
	public static int GuestRow(int page, Fable3OptionSetting setting) => page switch
	{
		PageGameSettings => setting switch
		{
			Fable3OptionSetting.Invert => 1,
			Fable3OptionSetting.Subtitles => 2,
			Fable3OptionSetting.Tutorial => 3,
			Fable3OptionSetting.Gamma => 4,
			Fable3OptionSetting.Breadcrumb => 5,
			Fable3OptionSetting.Music => 6,
			Fable3OptionSetting.Sfx => 7,
			Fable3OptionSetting.Voice => 8,
			Fable3OptionSetting.Speaker => 9,
			_ => 0,
		},
		PageGameSettingsLite => setting switch
		{
			Fable3OptionSetting.Language => 1,
			Fable3OptionSetting.Subtitles => 2,
			Fable3OptionSetting.Gamma => 3,
			Fable3OptionSetting.Music => 4,
			Fable3OptionSetting.Sfx => 5,
			Fable3OptionSetting.Voice => 6,
			Fable3OptionSetting.Speaker => 7,
			_ => 0,
		},
		_ => 0,
	};

	public static bool IsBar(Fable3OptionSetting setting) =>
		setting is Fable3OptionSetting.Gamma or Fable3OptionSetting.Music or Fable3OptionSetting.Sfx or Fable3OptionSetting.Voice;

	public static bool IsToggle(Fable3OptionSetting setting) =>
		setting is Fable3OptionSetting.Invert or Fable3OptionSetting.Subtitles or Fable3OptionSetting.Tutorial;

	/// <summary>A count's top: breadcrumb 4, speaker 2, the language the count less one.</summary>
	public static int Top(Fable3OptionSetting setting, int languages) => setting switch
	{
		Fable3OptionSetting.Breadcrumb => 4,
		Fable3OptionSetting.Speaker => 2,
		Fable3OptionSetting.Language => System.Math.Max(0, languages - 1),
		_ => 1,
	};

	/// <summary>A bar value on the setters' 0.025 grid, within 0..1.</summary>
	public static float OnGrid(float value) =>
		System.Math.Clamp(System.MathF.Round(value / BarStep) * BarStep, 0f, 1f);

	/// <summary>
	/// <paramref name="value"/> stepped once in <paramref name="direction"/> (-1 left, +1 right) as the
	/// guest's setters step it: a toggle to 0 / 1, a bar by 0.025 within 0..1, a count by one within
	/// its range.
	/// </summary>
	public static float Step(Fable3OptionSetting setting, float value, int direction, int languages)
	{
		if (IsToggle(setting))
		{
			return direction > 0 ? 1f : 0f;
		}

		if (IsBar(setting))
		{
			return OnGrid(value + (direction * BarStep));
		}

		return System.Math.Clamp(System.MathF.Round(value) + System.Math.Sign(direction), 0f, Top(setting, languages));
	}

	/// <summary>A pointer's 0..1 on a bar's slider as the bar's value: the nearest grid step.</summary>
	public static float FromFraction(float fraction) => OnGrid(fraction);

	/// <summary>A click on a row's value: a toggle flips, a count steps on and wraps to its first, a bar is left to the slider.</summary>
	public static float Clicked(Fable3OptionSetting setting, float value, int languages)
	{
		if (IsToggle(setting))
		{
			return value >= 0.5f ? 0f : 1f;
		}

		if (IsBar(setting))
		{
			return value;
		}

		int next = (int)System.MathF.Round(value) + 1;
		return next > Top(setting, languages) ? 0f : next;
	}

	/// <summary>The row label's tag.</summary>
	public static string LabelTag(Fable3OptionSetting setting) => setting switch
	{
		Fable3OptionSetting.Invert => "GUI_SCREEN_OPTIONS_INVERT_AIM",
		Fable3OptionSetting.Subtitles => "GUI_SCREEN_OPTIONS_SUBTITLES",
		Fable3OptionSetting.Tutorial => "GUI_SCREEN_OPTIONS_TUTORIAL_BOXES",
		Fable3OptionSetting.Gamma => "GUI_SCREEN_OPTIONS_GAMMA",
		Fable3OptionSetting.Breadcrumb => "GUI_SCREEN_OPTIONS_BREADCRUMB_SIZE",
		Fable3OptionSetting.Music => "GUI_SCREEN_OPTIONS_MUSIC",
		Fable3OptionSetting.Sfx => "GUI_SCREEN_OPTIONS_SOUNDS",
		Fable3OptionSetting.Voice => "GUI_SCREEN_OPTIONS_VOICE",
		Fable3OptionSetting.Speaker => "GUI_SCREEN_OPTIONS_AUDIO_SPEAKERS",
		_ => "GUI_SCREEN_OPTIONS_LANGUAGE",
	};

	/// <summary>The value as the row shows it; a bar's is its slider, so its text is empty.</summary>
	public static string Caption(Fable3OptionSetting setting, float value, System.Func<string, string> resolve, IReadOnlyList<string> languages)
	{
		string Pick(string[] tags) => resolve(tags[System.Math.Clamp((int)System.MathF.Round(value), 0, tags.Length - 1)]);
		return setting switch
		{
			_ when IsToggle(setting) => Pick(OnOff),
			_ when IsBar(setting) => string.Empty,
			Fable3OptionSetting.Breadcrumb => Pick(Breadcrumbs),
			Fable3OptionSetting.Speaker => Pick(Speakers),
			_ => (int)System.MathF.Round(value) is int index && index >= 0 && index < languages.Count ? languages[index] : string.Empty,
		};
	}

	/// <summary>
	/// One entry of a settings tab: a guest setting, a host row that opens a host page, one of the
	/// launcher's Display rows (<see cref="Engine_UiDisplayOptions"/>, its index), or the local co-op
	/// screen row (<see cref="CoopScreen"/>).
	/// </summary>
	public readonly record struct Entry(string Name, Fable3OptionSetting? Setting, string? Opens = null, int Display = -1, bool CoopScreen = false);

	/// <summary>
	/// THE LOCAL CO-OP SCREEN ROW, ours (the guest has no such setting; its couch game is the shared screen):
	/// the remaster's <c>coop_screen_mode</c> and its orientation as the five values Fable 2's co-op screen
	/// steps through (Refii.Camera.Engine_CameraScreens.Row order: shared, split side by side / top and
	/// bottom, split when apart side by side / top and bottom). It stands on the Game Settings page's Misc
	/// tab — the PC build's home of the gameplay rows (subtitles, tutorials, breadcrumbs), as the launcher
	/// files it under Gameplay › Co-op — only in a couch game (the front end's page has no co-op), and it
	/// applies as it steps: the split follows on the next frame (GameSplitView).
	/// </summary>
	public const string CoopScreenName = "CoopScreen";

	/// <summary>The row's label (our words).</summary>
	public const string CoopScreenWord = "REFII_F3_COOP_SCREEN";

	/// <summary>The row's five values, in Engine_CameraScreens.Row order (our words).</summary>
	public static readonly string[] CoopScreenWords =
	{
		"REFII_F3_COOP_MODE_SHARED", "REFII_F3_COOP_MODE_SIDE", "REFII_F3_COOP_MODE_STACKED",
		"REFII_F3_COOP_MODE_HYBRID_SIDE", "REFII_F3_COOP_MODE_HYBRID_STACKED",
	};

	/// <summary>
	/// The co-op screen row <paramref name="row"/> moved by <paramref name="steps"/>: within the five values
	/// for the stick and the wheel (no wrap, as the page's other steps), on past the last to the first for a click.
	/// </summary>
	public static int StepCoopScreen(int row, int steps, bool wrap = false)
	{
		int count = CoopScreenWords.Length;
		return wrap
			? (((row + steps) % count) + count) % count
			: System.Math.Clamp(row + steps, 0, count - 1);
	}

	/// <summary>The PC layout's tabs of settings page <paramref name="page"/>, each its entries; <paramref name="localCoop"/> adds the co-op screen row to the in-game page's Misc tab.</summary>
	public static IReadOnlyList<Entry> TabEntries(int page, int tab, bool localCoop = false)
	{
		var entries = new List<Entry>();
		void Add(Fable3OptionSetting setting)
		{
			if (GuestRow(page, setting) > 0)
			{
				entries.Add(new Entry(setting.ToString(), setting));
			}
		}

		switch (tab)
		{
			case TabDisplay:
				Add(Fable3OptionSetting.Gamma);
				for (int row = 0; row < Engine_UiDisplayOptions.RowCount; ++row)
				{
					entries.Add(new Entry("Display" + row, null, null, row));
				}

				break;
			case TabController:
				Add(Fable3OptionSetting.Invert);
				entries.Add(new Entry("Controls", null, Engine_UiControlsPage.OpenCommand));
				break;
			case TabAudio:
				Add(Fable3OptionSetting.Music);
				Add(Fable3OptionSetting.Sfx);
				Add(Fable3OptionSetting.Voice);
				Add(Fable3OptionSetting.Speaker);
				break;
			default:
				Add(Fable3OptionSetting.Language);
				Add(Fable3OptionSetting.Subtitles);
				Add(Fable3OptionSetting.Tutorial);
				Add(Fable3OptionSetting.Breadcrumb);
				if (localCoop && page == PageGameSettings)
				{
					entries.Add(new Entry(CoopScreenName, null, CoopScreen: true));
				}

				break;
		}

		return entries;
	}

	/// <summary>
	/// The rows of a settings tab: each entry's label, value and bar from the working values; a
	/// Display row's label and caption from <paramref name="display"/> (its index), none when null.
	/// </summary>
	public static List<Row> TabRows(int page, int tab, IReadOnlyList<float> values, System.Func<string, string> resolve, IReadOnlyList<string> languages,
		System.Func<int, (string Label, string Value)>? display = null, int? coopScreen = null)
	{
		var rows = new List<Row>();
		foreach (Entry entry in TabEntries(page, tab, coopScreen is not null))
		{
			if (entry.CoopScreen)
			{
				int at = System.Math.Clamp(coopScreen ?? 0, 0, CoopScreenWords.Length - 1);
				rows.Add(new Row(entry.Name, Engine_OwnWords.Say(CoopScreenWord), Engine_OwnWords.Say(CoopScreenWords[at]), string.Empty));
				continue;
			}

			if (entry.Setting is { } setting)
			{
				float value = values[(int)setting];
				string tag = LabelTag(setting);
				rows.Add(new Row(entry.Name, resolve(tag), Caption(setting, value, resolve, languages), tag + "_DESC", false,
					IsBar(setting) ? value : null));
			}
			else if (entry.Display >= 0)
			{
				(string label, string value) = display?.Invoke(entry.Display) ?? (entry.Name, string.Empty);
				rows.Add(new Row(entry.Name, label, value, string.Empty));
			}
			else
			{
				rows.Add(new Row(entry.Name, Engine_UiControlsPage.PageTitle, string.Empty, string.Empty));
			}
		}

		return rows;
	}

	/// <summary>The section element each page writes under (`Options` + the suffix, 0x831b3590..0x831b3748).</summary>
	public static string SectionPath(int page) => page switch
	{
		PageOptions => "Options.Sections.Options",
		PageSaveManagement => "Options.Sections.SaveManagement",
		PageGameSettings => "Options.Sections.GameSettings",
		PageGameSettingsLite => "Options.Sections.GameSettingsLite",
		PageMultiplayer => "Options.Sections.Multiplayer",
		_ => "Options",
	};

	/// <summary>The page's title tag (options.bgf): the root carries the screen title.</summary>
	public static string PageTitle(int page) => page switch
	{
		PageSaveManagement => "GUI_SAVE_MANAGEMENT",
		PageGameSettings or PageGameSettingsLite => "GUI_GAME_SETTINGS",
		_ => "GUI_OPTIONS_TITLE",
	};

	/// <summary>The rows a list page has (1 and 5: three, four with the multiplayer row; 2: two).</summary>
	public static int ListRowCount(int page, uint multiplayer) =>
		page == PageSaveManagement ? 2 : page is PageOptions or PageMultiplayer ? (page == PageMultiplayer || multiplayer != 0u ? 4 : 3) : 0;

	/// <summary>
	/// The rows of the list pages (1, 2 and 5), as the handler orders them. <paramref name="stated"/>
	/// answers an element path under the page's section with the text the guest wrote, or null;
	/// a stated label or description wins over the tag.
	/// </summary>
	public static List<Row> ListRows(Fable3OptionsState.State s, System.Func<string, string> resolve, System.Func<string, string?> stated)
	{
		var rows = new List<Row>();
		string section = SectionPath(s.Page);
		Row Make(string name, string labelTag, string descriptionTag, string? labelPath, string? descriptionPath, bool ghosted = false)
		{
			string label = (labelPath is not null ? stated(section + "." + labelPath) : null) ?? resolve(labelTag);
			string description = (descriptionPath is not null ? stated(section + "." + descriptionPath) : null) ?? descriptionTag;
			return new Row(name, label, string.Empty, description, ghosted);
		}

		switch (s.Page)
		{
			case PageOptions:
			case PageMultiplayer:
				bool multiplayer = s.Page == PageMultiplayer;
				rows.Add(Make("SaveManagement", "GUI_SAVE_MANAGEMENT", "GUI_SAVE_MANAGEMENT_DESC",
					multiplayer ? "SelectionText.SaveManagement" : "SelectionTitle.SaveManagment", null));
				rows.Add(Make("GameSettings", "GUI_GAME_SETTINGS", "GUI_GAME_SETTINGS_DESC", null, null));
				rows.Add(Make("NewGame", "GUI_NEW_GAME", "GUI_NEW_GAME_DESC",
					multiplayer ? "SelectionText.NewGame" : "SelectionTitle.NewGame", null));
				if (multiplayer || s.Multiplayer != 0u)
				{
					rows.Add(Make("Kick/Leave", s.Multiplayer == 2u ? "GUI_NET_PAUSED_DISCONNECT_FROM_HOST" : "GUI_ORB_INTERACT_KICK", string.Empty,
						"SelectionText.Kick/Leave", "SelectionDesription.Kick/LeaveDescription"));
				}
				break;
			case PageSaveManagement:
				rows.Add(Make("Save&Continue", "GUI_SCREEN_PAUSE_SAVE_AND_CONTINUE", "GUI_SCREEN_PAUSE_SAVE_AND_CONTINUE_DESC",
					"SelectionText.Save&Continue", "SelectionDesription.SaveAndContDescription", !s.CanSave));
				rows.Add(Make("LoadMangement", "GUI_SCREEN_LOAD_MANGEMENT", "GUI_SCREEN_LOAD_MANGEMENT_DESC",
					"SelectionText.LoadMangement", "SelectionDesription.LoadDescription", !s.CanLoad));
				break;
		}

		return rows;
	}

	/// <summary>The host's own Achievements row (<see cref="Fable3AchievementsPage"/>).</summary>
	public const string AchievementsRow = "RefiiAchievements";

	/// <summary>
	/// The host's rows after the handler's on a list page: Achievements on Options (1) and Multiplayer (5),
	/// never on Save Management (2) or a settings page. The handler knows nothing of them: A on one opens the
	/// host's page and posts nothing to the guest, whose cursor stays where it was.
	/// </summary>
	public static List<Row> HostRows(int page, System.Func<string, string> say)
	{
		var rows = new List<Row>(1);
		if (page is PageOptions or PageMultiplayer)
		{
			rows.Add(new Row(AchievementsRow, say("REFII_ACHIEVEMENTS"), string.Empty, string.Empty));
		}

		return rows;
	}

	/// <summary>
	/// The rows of a page the guest states as element texts: every text written directly under
	/// the page's section (one path segment below it, the row elements), in path order.
	/// </summary>
	public static List<Row> StatedRows(int page, IEnumerable<KeyValuePair<string, string>> statedTexts)
	{
		string prefix = SectionPath(page) + ".";
		var rows = new List<Row>();
		foreach ((string path, string text) in statedTexts)
		{
			if (!path.StartsWith(prefix, System.StringComparison.Ordinal) || text.Length == 0)
			{
				continue;
			}

			string tail = path[prefix.Length..];
			if (tail.IndexOf('.') >= 0)
			{
				continue;
			}

			rows.Add(new Row(tail, text, string.Empty, string.Empty));
		}

		return rows;
	}

	/// <summary>The one line a row shows in the list: label, then its value when it has one.</summary>
	public static string Line(Row row) => row.Value.Length == 0 ? row.Label : row.Label + "    " + row.Value;
}

/// <summary>
/// What the guest states of its Options screen (options_hooks.cpp): `@Options.State(box, page, row,
/// fromPause, frontEnd, canSave, multiplayer, loadBlocked, canLoad, saves, languages, query,
/// pending, owner)` — the page and the gates as the guest computed them, and the player whose Options
/// these are (its handler's own gate) — and `@Options.Values(box,
/// epoch, page, invert, subtitles, tutorial, gamma, breadcrumb, music, sfx, voice, speaker,
/// language)`, the settings the guest itself put in the box (a page's fill, the defaults), which
/// become Godot's working values. Held while the box manager shows the screen. Pure.
/// </summary>
public static class Fable3OptionsState
{
	public readonly record struct State(
		uint Box, int Page, int Row, bool FromPause, bool FrontEnd, bool CanSave, uint Multiplayer, uint LoadBlocked,
		bool CanLoad, int Saves, int Languages, bool Query, bool Pending, int Owner = 0);

	public sealed record Values(uint Box, int Epoch, int Page, float[] Settings);

	/// <summary>The state statement's arguments: the box, then 13 words.</summary>
	public const int StateWords = 14;

	/// <summary>The values statement's arguments: the box, the epoch, the page, then the ten settings.</summary>
	public const int ValueWords = 13;

	public static State? Current { get; private set; }

	public static Values? CurrentValues { get; private set; }

	public static ulong Generation { get; private set; }

	private static uint? Box(Refii.Ui.Engine_UiModel.GuiNativeCall call, int words) =>
		call.Arguments is { Count: var count } arguments && count >= words &&
		uint.TryParse(arguments[0].Text, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint box)
			? box
			: null;

	public static State? Parse(Refii.Ui.Engine_UiModel.GuiNativeCall call)
	{
		if (Box(call, StateWords) is not { } box)
		{
			return null;
		}

		int Word(int i) => (int)call.Arguments![i].Number;
		return new State(box, Word(1), Word(2), Word(3) != 0, Word(4) != 0, Word(5) != 0, (uint)Word(6), (uint)Word(7),
			Word(8) != 0, Word(9), Word(10), Word(11) != 0, Word(12) != 0, Word(13));
	}

	public static Values? ParseValues(Refii.Ui.Engine_UiModel.GuiNativeCall call)
	{
		if (Box(call, ValueWords) is not { } box)
		{
			return null;
		}

		var settings = new float[Fable3OptionsModel.Settings];
		for (int i = 0; i < settings.Length; ++i)
		{
			settings[i] = call.Arguments![3 + i].Number;
		}

		return new Values(box, (int)call.Arguments![1].Number, (int)call.Arguments[2].Number, settings);
	}

	public static void Stated(Refii.Ui.Engine_UiModel.GuiNativeCall call)
	{
		if (Parse(call) is { } state)
		{
			Current = state;
			++Generation;
		}
	}

	public static void StatedValues(Refii.Ui.Engine_UiModel.GuiNativeCall call)
	{
		if (ParseValues(call) is { } values)
		{
			CurrentValues = values;
			++Generation;
		}
	}

	/// <summary>The box manager dropped <paramref name="box"/>: its state goes with it.</summary>
	public static void Closed(uint box)
	{
		if (Current is { } state && state.Box == box)
		{
			Current = null;
			CurrentValues = null;
			++Generation;
		}
	}
}

/// <summary>
/// GODOT'S OWN STATE OF THE OPTIONS SCREEN: the cursor of each page, the Game Settings tab, and the
/// working value of every setting. The guest's statements reset it where the guest decides (a new
/// box or page puts the cursor where the guest put its own; a values epoch — a fill, the defaults —
/// replaces the working values); every change Godot makes is handed to the guest as the working
/// value the box's own setters reach (`options-value`). Pure, so the tests can run it.
/// </summary>
public sealed class Fable3OptionsWork
{
	public uint Box { get; private set; }

	public int Page { get; private set; }

	public int Tab { get; private set; }

	/// <summary>The 0-based cursor on the open page (a list page's row, a tab's entry).</summary>
	public int Cursor { get; set; }

	public int Epoch { get; private set; } = -1;

	public float[] Values { get; } = new float[Fable3OptionsModel.Settings];

	/// <summary>Follows the guest's statements (the class remarks). True when the page or the values changed under Godot.</summary>
	public bool Follow(Fable3OptionsState.State state, Fable3OptionsState.Values? values)
	{
		bool changed = false;
		if (state.Box != Box)
		{
			Box = state.Box;
			Epoch = -1;
			Page = 0;
		}

		if (state.Page != Page)
		{
			Page = state.Page;
			Tab = Fable3OptionsModel.TabDisplay;
			Cursor = Fable3OptionsModel.IsSettingsPage(Page) ? 0 : System.Math.Max(0, state.Row - 1);
			changed = true;
		}

		if (values is { } v && v.Box == Box && v.Epoch != Epoch)
		{
			Epoch = v.Epoch;
			System.Array.Copy(v.Settings, Values, Values.Length);
			for (int i = 0; i < Values.Length; ++i)
			{
				if (Fable3OptionsModel.IsBar((Fable3OptionSetting)i))
				{
					Values[i] = Fable3OptionsModel.OnGrid(Values[i]);
				}
			}

			changed = true;
		}

		return changed;
	}

	/// <summary>Opens tab <paramref name="tab"/> (wrapping over the four), the cursor on its first entry.</summary>
	public void SetTab(int tab)
	{
		int count = Fable3OptionsModel.TabWords.Length;
		Tab = ((tab % count) + count) % count;
		Cursor = 0;
	}

	/// <summary>Sets a working value; true when it moved.</summary>
	public bool Set(Fable3OptionSetting setting, float value)
	{
		if (Values[(int)setting] == value)
		{
			return false;
		}

		Values[(int)setting] = value;
		return true;
	}
}

/// <summary>
/// Fable 3's Options screen in the PC layout on the shared menu template (<see cref="Fable3ListScreen"/>).
/// Godot owns its cursor, its Game Settings tabs and its working values (<see cref="Fable3OptionsWork"/>),
/// both devices live at once: up / down and the wheel move the cursor, left / right, a click on the
/// value, the wheel over the cursor row and a drag on a bar's slider change the working value (each
/// handed to the guest, whose own setters bring the box to it), LT / RT (LB / RB, `[` `]`) or a click
/// change the tab. Every decision is the guest's, through its own handler (`options:<p>:<action>`,
/// options_hooks.cpp): A enters a list row (with the row, `options-row:<p>:<row>:accept`) or commits a
/// settings page, B goes back (asking when the page changed), Y restores the defaults, START closes;
/// the guest's query (`@Options.Query`) is drawn here and answered by A / B or a click on its prompts.
/// </summary>
public partial class Fable3Options : Fable3ListScreen
{
	private const double RepeatDelay = 0.35;
	private const double RepeatRows = 0.2;
	private const double RepeatValues = 0.06;
	private const int StickDeadZone = 16000;
	private const uint TriggerDown = 128u;

	private readonly Fable3OptionsWork _work = new();
	private readonly List<string> _languages = new();
	private double _languagesAge = double.MaxValue;
	private Engine_UiPressEdges _edges;
	private bool _wasLeftTrigger;
	private bool _wasRightTrigger;
	private int _heldVertical;
	private int _heldHorizontal;
	private double _repeat;
	private IReadOnlyList<Fable3OptionsModel.Entry> _entries = System.Array.Empty<Fable3OptionsModel.Entry>();
	private int _listRows;
	private readonly List<string> _rowNames = new();

	/// <summary>The Display rows' chosen indices while a settings page is open: read on entry, saved by A, left by B.</summary>
	private int[] _display = System.Array.Empty<int>();

	protected override string LogTag => "fable3-options";

	protected override string LogContext => $"page {_work.Page} tab {_work.Tab} ";

	/// <summary>The Options sections author their arrows; nothing writes them (<see cref="Fable3ListCursor.OptionsArrows"/>).</summary>
	protected override (bool Top, bool Bottom) Arrows(int highlight, int total) => Fable3ListCursor.OptionsArrows(_work.Page);

	/// <summary>Options (1), Save Management (2) and Multiplayer (5) centre their choices; Game Settings (3, 4) keeps its rows left.</summary>
	protected override bool RowsOnCentreLine => Fable3ListCursor.RowsCentred(_work.Page);

	/// <summary>
	/// Whether a settings page is up with nothing over it, so X opens the host's settings hub
	/// (<see cref="Fable3HostOptions"/>; the screen's handler ignores X).
	/// </summary>
	public static bool OffersControls { get; private set; }

	/// <summary>
	/// The player whose Options these are, as the guest's handler gates them (`@Options.State`'s owner): player
	/// two's when their pad opened them, so their levers carry 2 and their Achievements row opens the page on
	/// their own pad; player one's otherwise (and for a hero that is no player's, which the gate lets through).
	/// </summary>
	private int Player => Fable3OptionsState.Current is { Owner: 2 } ? Fable3ActingPlayer.Two : Fable3ActingPlayer.One;

	/// <summary>The pad of <see cref="Player"/>: pad 0 (with the keyboard) for player one, player two's own pad for two.</summary>
	private GameInput.PadState OwnersPad =>
		Player == Fable3ActingPlayer.Two ? GameInput.Seen(Fable3ActingPlayer.PadOfTwo) : GameInput.Pad0;

	protected override Fable3ScreenPage? Compose(double delta)
	{
		OffersControls = false;
		if (!HostBridge.IsGuestUp || Fable3OptionsState.Current is not { } state || Fable3HostOptions.IsOpen)
		{
			// The screen does not have the pad: what is held when it gets it (the press that opened
			// it, the press that left the host page over it) is not a press on it.
			_edges.Hold();
			_wasLeftTrigger = true;
			_wasRightTrigger = true;
			_heldVertical = 0;
			_heldHorizontal = 0;
			return null;
		}

		int was = _work.Page;
		_work.Follow(state, Fable3OptionsState.CurrentValues);
		int page = _work.Page;
		bool settings = Fable3OptionsModel.IsSettingsPage(page);
		if (settings && (page != was || _display.Length != Engine_UiDisplayOptions.RowCount))
		{
			_display = Engine_UiDisplayOptions.Page.Read();
		}

		// The language names are the guest's table; asked when the front end's page shows, every second.
		if (page == Fable3OptionsModel.PageGameSettingsLite && (_languagesAge += delta) > 1.0)
		{
			_languagesAge = 0.0;
			_languages.Clear();
			foreach (Engine_UiModel.Language language in Engine_UiSource.Current.GetLanguages())
			{
				_languages.Add(language.Name);
			}
		}

		List<Fable3OptionsModel.Row> rows;
		IReadOnlyList<string>? tabs = null;
		if (settings)
		{
			bool coop = Refii.Core.Engine_TitleGame.Active.LocalCoop;
			_entries = Fable3OptionsModel.TabEntries(page, _work.Tab, coop);
			rows = Fable3OptionsModel.TabRows(page, _work.Tab, _work.Values, Resolve, _languages, DisplayRow,
				coop ? CoopScreenRow() : null);
			var words = new string[Fable3OptionsModel.TabWords.Length];
			for (int i = 0; i < words.Length; ++i)
			{
				words[i] = Engine_OwnWords.Say(Fable3OptionsModel.TabWords[i]);
			}

			tabs = words;
		}
		else
		{
			_entries = System.Array.Empty<Fable3OptionsModel.Entry>();
			rows = Fable3OptionsModel.ListRows(state, Resolve, Stated);
			if (rows.Count == 0)
			{
				rows = Fable3OptionsModel.StatedRows(page, StatedTexts());
			}

			rows.AddRange(Fable3OptionsModel.HostRows(page, Engine_OwnWords.Say));
		}

		_listRows = rows.Count;
		_rowNames.Clear();
		foreach (Fable3OptionsModel.Row row in rows)
		{
			_rowNames.Add(row.Name);
		}

		_work.Cursor = System.Math.Clamp(_work.Cursor, 0, System.Math.Max(0, rows.Count - 1));

		var lines = new List<Fable3ScreenPage.Row>(rows.Count);
		foreach (Fable3OptionsModel.Row row in rows)
		{
			lines.Add(new Fable3ScreenPage.Row(row.Name, Fable3OptionsModel.Line(row), row.Ghosted, row.Description, row.Bar));
		}

		Fable3ScreenPage.QueryBox? query = state.Query ? Query(state.Box) : null;
		OffersControls = query is null && settings;

		Drive(delta, state, query is not null);

		var prompts = new List<Fable3ScreenPage.Prompt>(4);
		if (settings && query is null)
		{
			prompts.Add(new Fable3ScreenPage.Prompt("A", Engine_OwnWords.Say("REFII_SAVE")));
			prompts.Add(new Fable3ScreenPage.Prompt("B", Engine_OwnWords.Say("REFII_BACK")));
			prompts.Add(new Fable3ScreenPage.Prompt("Y", Engine_OwnWords.Say("REFII_F3_DEFAULTS")));
			prompts.Add(new Fable3ScreenPage.Prompt("X", Fable3SettingsHub.Page.Title));
		}

		return new Fable3ScreenPage(Resolve(Fable3OptionsModel.PageTitle(page)), lines, _work.Cursor,
			string.Empty, prompts, true, query, tabs, settings ? _work.Tab : -1);
	}

	/// <summary>The pad (and the keys on it): Godot's cursor, tab and values, and the guest's actions.</summary>
	private void Drive(double delta, Fable3OptionsState.State state, bool query)
	{
		GameInput.PadState pad = OwnersPad;
		uint down = pad.Buttons;
		uint pressed = _edges.Take(down);
		bool leftTrigger = pad.LeftTrigger >= TriggerDown;
		bool rightTrigger = pad.RightTrigger >= TriggerDown;
		bool tabBack = (leftTrigger && !_wasLeftTrigger) || (pressed & GameInput.LeftShoulder) != 0;
		bool tabOn = (rightTrigger && !_wasRightTrigger) || (pressed & GameInput.RightShoulder) != 0;
		_wasLeftTrigger = leftTrigger;
		_wasRightTrigger = rightTrigger;
		bool settings = Fable3OptionsModel.IsSettingsPage(_work.Page);

		if (query)
		{
			// The guest's query: A its yes, B its no.
			if ((pressed & GameInput.ButtonA) != 0)
			{
				Act(Fable3BoxAnswers.ActionA);
			}
			else if ((pressed & GameInput.ButtonB) != 0)
			{
				Act(Fable3BoxAnswers.ActionB);
			}

			return;
		}

		if ((pressed & GameInput.Start) != 0)
		{
			Act(Fable3BoxAnswers.ActionStart);
			return;
		}

		if ((pressed & GameInput.ButtonA) != 0)
		{
			Accept();
			return;
		}

		if ((pressed & GameInput.ButtonB) != 0)
		{
			Act(Fable3BoxAnswers.ActionB);
			return;
		}

		if (settings && (pressed & GameInput.ButtonY) != 0)
		{
			Act(Fable3BoxAnswers.ActionY);
			return;
		}

		if (settings && (tabBack || tabOn))
		{
			_work.SetTab(_work.Tab + (tabOn ? 1 : -1));
			HintCursor();
			return;
		}

		int vertical = (down & GameInput.DpadUp) != 0 || pad.LeftY > StickDeadZone ? -1 :
			(down & GameInput.DpadDown) != 0 || pad.LeftY < -StickDeadZone ? 1 : 0;
		int horizontal = (down & GameInput.DpadLeft) != 0 || pad.LeftX < -StickDeadZone ? -1 :
			(down & GameInput.DpadRight) != 0 || pad.LeftX > StickDeadZone ? 1 : 0;
		if (vertical != 0)
		{
			horizontal = 0;
		}

		if (vertical != _heldVertical || horizontal != _heldHorizontal)
		{
			_heldVertical = vertical;
			_heldHorizontal = horizontal;
			_repeat = RepeatDelay;
		}
		else if ((vertical == 0 && horizontal == 0) || (_repeat -= delta) > 0.0)
		{
			return;
		}
		else
		{
			_repeat = vertical != 0 || !CursorOnBar() ? RepeatRows : RepeatValues;
		}

		if (vertical != 0)
		{
			MoveCursor(_work.Cursor + vertical);
		}
		else if (horizontal != 0 && settings)
		{
			StepValue(horizontal);
		}
	}

	/// <summary>The host's row a list page's <paramref name="row"/> is, by name, else null (the handler's).</summary>
	private string? HostRowAt(int row) =>
		!Fable3OptionsModel.IsSettingsPage(_work.Page) && row >= 0 && row < _rowNames.Count &&
		_rowNames[row] == Fable3OptionsModel.AchievementsRow
			? _rowNames[row]
			: null;

	/// <summary>A host row accepted: the Achievements row opens the page for the player whose Options these are.</summary>
	private void AcceptHostRow(int player)
	{
		Fable3AchievementsPage.Open(player);
		Refii.Core.Engine_TitleGame.Active.Input.HoldUntilReleased();
	}

	private (string Label, string Value) DisplayRow(int row) =>
		(Engine_UiDisplayOptions.Page.RowTitle(row),
			Engine_UiDisplayOptions.Page.Caption(row, row < _display.Length ? _display[row] : 0));

	/// <summary>A Display row's chosen index moved by <paramref name="steps"/> (no wrap) or, <paramref name="wrap"/>, on by one past its last to its first.</summary>
	private void StepDisplay(int row, int steps, bool wrap = false)
	{
		if (row < 0 || row >= _display.Length)
		{
			return;
		}

		int choices = System.Math.Max(1, Engine_UiDisplayOptions.Page.Choices(row));
		_display[row] = wrap ? (_display[row] + 1) % choices : System.Math.Clamp(_display[row] + steps, 0, choices - 1);
	}

	private bool CursorOnBar() =>
		_work.Cursor >= 0 && _work.Cursor < _entries.Count && _entries[_work.Cursor].Setting is { } setting &&
		Fable3OptionsModel.IsBar(setting);

	/// <summary>Godot's cursor to 0-based <paramref name="row"/>, within the page's rows (no wrap, as the guest's steps).</summary>
	private void MoveCursor(int row)
	{
		int clamped = System.Math.Clamp(row, 0, System.Math.Max(0, _listRows - 1));
		if (clamped == _work.Cursor)
		{
			return;
		}

		_work.Cursor = clamped;
		HintCursor();
	}

	/// <summary>On a settings page the guest's cursor is walked toward Godot's while no value waits.</summary>
	private void HintCursor()
	{
		if (Fable3OptionsModel.IsSettingsPage(_work.Page) && _work.Cursor < _entries.Count &&
			_entries[_work.Cursor].Setting is { } setting &&
			Fable3BoxAnswers.OptionsRow(Fable3OptionsModel.GuestRow(_work.Page, setting) - 1, false, Player) is { } action)
		{
			HostBridge.PostFrontEndAction(action);
		}
	}

	/// <summary>A: a list page's row by the guest's A with that row; a settings page's host row opens its page; otherwise the guest's commit.</summary>
	private void Accept()
	{
		if (!Fable3OptionsModel.IsSettingsPage(_work.Page))
		{
			if (HostRowAt(_work.Cursor) is not null)
			{
				AcceptHostRow(Player);
			}
			else if (Fable3BoxAnswers.OptionsRow(_work.Cursor, true, Player) is { } action)
			{
				HostBridge.PostFrontEndAction(action);
				Refii.Core.Engine_TitleGame.Active.Input.HoldUntilReleased();
			}

			return;
		}

		if (_work.Cursor < _entries.Count && _entries[_work.Cursor].Opens is { } command &&
			Engine_UiHostOptionsPage.For(command) is { } hostPage)
		{
			Fable3HostOptions.Open(hostPage);
			return;
		}

		// One A takes the whole page, as the PC build's: the launcher's Display rows, then the guest's commit.
		int[] display = _display;
		Engine_UiDisplayOptions.Page.Commit(row => row < display.Length ? display[row] : 0);
		Act(Fable3BoxAnswers.ActionA);
	}

	private void Act(int action)
	{
		HostBridge.PostFrontEndAction(Fable3BoxAnswers.Options(action, Player));
		Refii.Core.Engine_TitleGame.Active.Input.HoldUntilReleased();
	}

	/// <summary>The cursor entry's working value (or Display row) one step in <paramref name="direction"/>.</summary>
	private void StepValue(int direction)
	{
		if (_work.Cursor < _entries.Count && _entries[_work.Cursor].Setting is { } setting)
		{
			SetValue(setting, Fable3OptionsModel.Step(setting, _work.Values[(int)setting], direction, Languages));
		}
		else if (_work.Cursor < _entries.Count && _entries[_work.Cursor].Display >= 0)
		{
			StepDisplay(_entries[_work.Cursor].Display, direction);
		}
		else if (_work.Cursor < _entries.Count && _entries[_work.Cursor].CoopScreen)
		{
			StepCoopScreen(direction);
		}
	}

	/// <summary>The co-op screen row's value: the screen mode and orientation settings (Engine_CameraScreens.Row).</summary>
	private static int CoopScreenRow() =>
		Refii.Camera.Engine_CameraScreens.Row(Refii.Camera.Engine_CameraScreens.Chosen, Refii.Camera.Engine_CameraScreens.Stacked);

	/// <summary>
	/// The co-op screen row stepped, written at once (LIVE: GameSplitView follows the settings each frame):
	/// the mode and the orientation, and the old switch removed, as Fable 2's co-op screen row writes them.
	/// </summary>
	private static void StepCoopScreen(int steps, bool wrap = false)
	{
		int was = CoopScreenRow();
		int row = Fable3OptionsModel.StepCoopScreen(was, steps, wrap);
		if (row == was)
		{
			return;
		}

		(Refii.Camera.Engine_CameraScreens.Mode mode, bool stacked) =
			Refii.Camera.Engine_CameraScreens.FromRow(row, Refii.Camera.Engine_CameraScreens.Stacked);
		var settings = new Dictionary<string, string>
		{
			[Refii.Camera.Engine_CameraScreens.ModeKey] = Refii.Camera.Engine_CameraScreens.Name(mode),
			[Refii.Camera.Engine_CameraScreens.OrientationKey] = stacked ? "horizontal" : "vertical",
		};
		bool written = Engine_UiScreenActions.WriteSettings(settings, new[] { Refii.Camera.Engine_CameraScreens.LegacySplitKey });
		GD.Print($"[fable3-options] co-op screen row: {Fable3OptionsModel.CoopScreenWords[row]}" +
			(written ? string.Empty : " (the setting could not be written)"));
	}

	private int Languages => Fable3OptionsState.Current is { } state ? state.Languages : 0;

	/// <summary>A working value Godot set, handed to the guest's setters (options_hooks.cpp).</summary>
	private void SetValue(Fable3OptionSetting setting, float value)
	{
		if (_work.Set(setting, value) &&
			Fable3BoxAnswers.OptionsValue(_work.Page, Fable3OptionsModel.GuestRow(_work.Page, setting), value, Player) is { } action)
		{
			HostBridge.PostFrontEndAction(action);
		}
	}

	/// <summary>
	/// A click: on a list page it accepts the row (the guest's A with it); on a settings page a click
	/// on a row that opens a host page opens it, a click on another row places the cursor, a click
	/// on the cursor row changes its value (a toggle flips, a count steps on). The mouse is player one's.
	/// </summary>
	protected override void PointAt(int row, bool clicked)
	{
		if (!Fable3OptionsModel.IsSettingsPage(_work.Page))
		{
			if (clicked && HostRowAt(row) is not null)
			{
				_work.Cursor = row;
				AcceptHostRow(Fable3ActingPlayer.Pointer);
			}
			else if (clicked && Fable3BoxAnswers.OptionsRow(row, true, Fable3ActingPlayer.One) is { } action)
			{
				_work.Cursor = row;
				HostBridge.PostFrontEndAction(action);
			}
			else if (!clicked)
			{
				MoveCursor(row);
			}

			return;
		}

		// A row that opens a host page opens on the first click, as a list page's row does.
		if (clicked && row < _entries.Count && _entries[row].Opens is not null)
		{
			_work.Cursor = row;
			Accept();
			return;
		}

		if (row != _work.Cursor || !clicked)
		{
			MoveCursor(row);
			return;
		}

		if (row < _entries.Count && _entries[row].Setting is { } setting)
		{
			SetValue(setting, Fable3OptionsModel.Clicked(setting, _work.Values[(int)setting], Languages));
		}
		else if (row < _entries.Count && _entries[row].Display >= 0)
		{
			StepDisplay(_entries[row].Display, 1, wrap: true);
		}
		else if (row < _entries.Count && _entries[row].CoopScreen)
		{
			StepCoopScreen(1, wrap: true);
		}
	}

	/// <summary>The wheel over a settings page's cursor row steps its value (up raises it); elsewhere it moves the cursor.</summary>
	protected override bool PointWheel(int steps, int row)
	{
		if (row != _work.Cursor || !Fable3OptionsModel.IsSettingsPage(_work.Page) || row >= _entries.Count)
		{
			return false;
		}

		if (_entries[row].Display >= 0)
		{
			StepDisplay(_entries[row].Display, -steps);
			return true;
		}

		if (_entries[row].CoopScreen)
		{
			StepCoopScreen(-steps);
			return true;
		}

		if (_entries[row].Setting is not { } setting)
		{
			return false;
		}

		float value = _work.Values[(int)setting];
		for (int i = 0; i < System.Math.Abs(steps); ++i)
		{
			value = Fable3OptionsModel.Step(setting, value, steps < 0 ? 1 : -1, Languages);
		}

		SetValue(setting, value);
		return true;
	}

	/// <summary>A press or drag on a bar's slider sets its working value (the nearest 0.025 step).</summary>
	protected override void PointBar(int row, float fraction)
	{
		if (row < _entries.Count && _entries[row].Setting is { } setting && Fable3OptionsModel.IsBar(setting))
		{
			if (row != _work.Cursor)
			{
				_work.Cursor = row;
				HintCursor();
			}

			SetValue(setting, Fable3OptionsModel.FromFraction(fraction));
		}
	}

	protected override void PointTab(int tab)
	{
		if (Fable3OptionsModel.IsSettingsPage(_work.Page))
		{
			_work.SetTab(tab);
			HintCursor();
		}
	}

	private const string QueryRoot = "Options.Sections.QueryBox";

	/// <summary>
	/// THE QUESTION IS THE GUEST'S, ITS ANSWER GODOT'S. sub_825CBEC0(box, tag) — called by the A arm
	/// sub_825CB588 for New Game, Save and Continue, Load and Kick/Leave (0x825cb60c, 0x825cb8ac,
	/// 0x825cb8c0, 0x825cb8e4, 0x825cba94) and the B arm sub_825CBAA0 (0x825cbb80, 0x825cbc44) — is
	/// re-expressed (options_hooks.cpp): it states the tag (`@Options.Query`) and raises box+30
	/// (0x825cbf10), which the screen's state carries; its element writes (root state 6, the text)
	/// are dropped. Godot draws it and answers it through the handler's own query arms, 74 yes /
	/// 75 no (0x825CA5AC..0x825CA664). So the question is the call's tag, resolved through the
	/// book; the element's text stands only for the kick query, whose henchman-formatted text is
	/// written inline (sub_82819070 at 0x825CBA5C) without the call. THE QUERY HAS NO TITLE: the
	/// binary names no `.Group.QueryBox.Title` path (only `.Text` and the two button paths). Its
	/// button labels are constants per case written inline behind the presentation (sub_82819328 on
	/// dword_835589D8 / 835589DC: 0x825cb770 / 0x825cb788, 0x825cb878 / 0x825cb890, 0x825cbb58 /
	/// 0x825cbb70, 0x825cbc1c / 0x825cbc34) — the elements' stated tags. THE BUTTONS ARE NEVER HIDDEN:
	/// no code resolves `QueryBox.Buttons…` for anything but those two tag writes (the only other
	/// Options opacity writers, sub_828194F8 at 0x825c9b50 / 0x825c9b64 and sub_82B59BB8 at
	/// 0x825c9a0c…0x825c9a58, touch the Options / Multiplayer sections' B button and rows), and every
	/// query enters the root's slide 6 (0x825cbef4, 0x825cb9e8), whose Options.bgf `Buttons` Slide1
	/// activates A_Button and B_Button. A query that writes no label (the overwrite warning
	/// 0x825cb60c, the disconnect 0x825cb8e4 / 0x825cba94, the kick 0x825cb9e8) shows what the
	/// element last held, the file's own `GUI_YES` / `GUI_NO` before any write.
	/// </summary>
	private static Fable3ScreenPage.QueryBox? Query(uint box)
	{
		string text = Fable3UiStatements.OptionsQuery is { } asked && asked.Box == box &&
			Fable3Elements.TagText(asked.Tag) is { } question
			? question
			: Fable3Elements.Latest(QueryRoot + ".Group.QueryBox.Text")?.StatedTextValue ?? string.Empty;
		const string title = "";
		var prompts = new List<Fable3ScreenPage.Prompt>(2);
		foreach ((string button, string authored) in new[] { ("A", "GUI_YES"), ("B", "GUI_NO") })
		{
			string? label = Fable3Elements.Latest(QueryRoot + ".Buttons.Buttons." + button + "_Button.small.Text")?.StatedTextValue ??
				Fable3Elements.TagNameText(authored);
			if (label is { Length: > 0 })
			{
				prompts.Add(new Fable3ScreenPage.Prompt(button, label));
			}
		}

		return new Fable3ScreenPage.QueryBox(title, text, prompts);
	}

	/// <summary>The text the guest wrote into an Options element, by path, or null.</summary>
	private static string? Stated(string path)
	{
		foreach (KeyValuePair<(string Document, string Path), Fable3Elements.Element> entry in Fable3Elements.All)
		{
			if (entry.Key.Path == path && entry.Value.StatedTextValue is { Length: > 0 } text)
			{
				return text;
			}
		}

		return null;
	}

	private static IEnumerable<KeyValuePair<string, string>> StatedTexts()
	{
		foreach (KeyValuePair<(string Document, string Path), Fable3Elements.Element> entry in Fable3Elements.All)
		{
			if (entry.Value.StatedTextValue is { Length: > 0 } text)
			{
				yield return new KeyValuePair<string, string>(entry.Key.Path, text);
			}
		}
	}
}
