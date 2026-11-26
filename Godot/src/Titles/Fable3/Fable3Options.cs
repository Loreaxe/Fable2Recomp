namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Godot;
using Refii.Game;
using Refii.Host;
using Refii.Text;
using Refii.Ui;

/// <summary>
/// The rows of Fable 3's Options screen, from the screen's state as its handler states it
/// (`@Options.State`, Fable3/src/hooks/src/options_hooks.cpp) and the levels the game's appliers
/// were given (RefiiHostGetOptionsSliders, the stated options table). Pure logic, so the tests can
/// run it against a snapshot.
/// </summary>
/// <remarks>
/// THE ROW ORDER AND VALUES ARE THE GUEST'S. The row writer sub_825CC938 (page 3, GameSettings)
/// writes rows 1..9 as `*(obj+92)` selects them: Controller +31, Subtitles +32, Tutorial +33,
/// Gamma +128, Breadcrumb +104, Music +116, SoundEffects +120, Voice +124, SpeakerType +100
/// (0x825cc978..0x825ccb84); sub_825CCB90 (page 4, GameSettingsLite) rows 1..7: Language +64,
/// Subtitles, Gamma, Music, SoundEffects, Voice, SpeakerType (0x825ccbc8..0x825ccd78). The value
/// tags are the tables its static initialiser fills: ON/OFF `dword_83558A64` =
/// TEXT_GUI_OPTIONS_OFF / TEXT_GUI_OPTIONS_ON (0x831b4be0 / 0x831b4c20), breadcrumb
/// `dword_83558A6C` = GUI_OPTIONS_BCRUMB_OFF/LOW/MEDIUM/HIGH/VERYHIGH (0x831b4c70..0x831b4d58),
/// speaker `dword_83558A80` = GUI_SCREEN_OPTIONS_AUDIO_TV/HIFI/SURROUND (0x831b4d98..0x831b4e18).
/// The row labels are the tags options.bgf gives its row elements (GUI_SCREEN_OPTIONS_*, the
/// `.Controller` row's being INVERT_AIM: "Adjust your controllers invert aim").
///
/// THE LIST PAGES are the A-press handler sub_825CB588's rows: pages 1 and 5 (`+84`, 0x825cb73c)
/// row 1 Save Management (page 2, 0x825cb794), row 2 Game Settings (page 3, or the lite page when
/// sub_82347CE8 refuses, 0x825cb7f8/0x825cb830), row 3 New Game (the confirm query, 0x825cb8ac/c0),
/// row 4 Kick/Leave (multiplayer, 0x825cba94 — page 5's section carries it, 0x825c9a98); page 2
/// (`+88`, 0x825cb5b4) row 1 Save and Continue (ghosted by the Show when the save is not allowed,
/// 0x825c99f4..0x825c9a3c), row 2 Load Management (ghosted when the guest does not offer it: TU3's
/// +192, the disc's +112 != 0 — stated as canLoad, options_hooks.cpp). Labels are the
/// rows' tags in options.bgf; the texts the guest writes into the section's `SelectionText` /
/// `SelectionTitle` / `SelectionDesription` elements win when stated. A page the handler does not
/// list falls back to every text stated under its section.
/// </remarks>
public static class Fable3OptionsModel
{
	/// <summary>One row: a stable name, its label (resolved), its value (resolved), its description tag and whether the guest ghosts it.</summary>
	public readonly record struct Row(string Name, string Label, string Value, string Description, bool Ghosted = false);

	/// <summary>The Options screen's state: the statement's first 15 words and the 4 stated levels.</summary>
	public readonly record struct Snapshot(
		uint Box, int Page, int SelectedRow, bool Controller, bool Subtitles, bool Tutorial, int Breadcrumb,
		int Speaker, int LanguageIndex, int LanguageCount, uint Flag108, uint Flag112, bool FromPause, bool Lite,
		float Gamma, float Music, float Sfx, float Voice, bool CanSave = true, bool CanLoad = true);

	public const int Words = 15;
	public const int Values = 4;

	public const int PageOptions = 1;
	public const int PageSaveManagement = 2;
	public const int PageGameSettings = 3;
	public const int PageGameSettingsLite = 4;
	public const int PageMultiplayer = 5;

	/// <summary>The value tags, as the guest's tables order them.</summary>
	public static readonly string[] OnOff = { "TEXT_GUI_OPTIONS_OFF", "TEXT_GUI_OPTIONS_ON" };

	public static readonly string[] Breadcrumbs =
		{ "GUI_OPTIONS_BCRUMB_OFF", "GUI_OPTIONS_BCRUMB_LOW", "GUI_OPTIONS_BCRUMB_MEDIUM", "GUI_OPTIONS_BCRUMB_HIGH", "GUI_OPTIONS_BCRUMB_VERYHIGH" };

	public static readonly string[] Speakers =
		{ "GUI_SCREEN_OPTIONS_AUDIO_TV", "GUI_SCREEN_OPTIONS_AUDIO_HIFI", "GUI_SCREEN_OPTIONS_AUDIO_SURROUND" };

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

	public static Snapshot Parse(uint[] words, float[] values) => new(
		words[0], (int)words[1], (int)words[2], words[3] != 0u, words[4] != 0u, words[5] != 0u, (int)words[6],
		(int)words[7], (int)words[8], (int)words[9], words[10], words[11], words[12] != 0u, words[13] != 0u,
		values[0], values[1], values[2], values[3], words.Length < 15 || words[14] != 0u);

	/// <summary>
	/// A slider as text: the guest's bar is `value * 1000` on a 0..1000 meter (sub_825CDBD0,
	/// 0x825cdc38); twenty pips here, a step of 0.025 being half a pip, with the percentage.
	/// </summary>
	public static string Slider(float value)
	{
		float clamped = value < 0f ? 0f : value > 1f ? 1f : value;
		int filled = (int)System.MathF.Round(clamped * 20f);
		return new string('■', filled) + new string('□', 20 - filled) + "  " + (int)System.MathF.Round(clamped * 100f) + "%";
	}

	/// <summary>
	/// The rows of the settings pages (3 and 4), labels and values resolved through
	/// <paramref name="resolve"/>; <paramref name="languages"/> names the language row's value.
	/// Empty for the pages whose rows are element writes.
	/// </summary>
	public static List<Row> SettingsRows(Snapshot s, System.Func<string, string> resolve, IReadOnlyList<string> languages)
	{
		var rows = new List<Row>();
		string Pick(string[] tags, int index) => resolve(index >= 0 && index < tags.Length ? tags[index] : tags[0]);
		Row Toggle(string name, string tag, bool on) => new(name, resolve(tag), Pick(OnOff, on ? 1 : 0), tag + "_DESC");
		Row Bar(string name, string tag, float value) => new(name, resolve(tag), Slider(value), tag + "_DESC");

		switch (s.Page)
		{
			case PageGameSettings:
				rows.Add(Toggle("Controller", "GUI_SCREEN_OPTIONS_INVERT_AIM", s.Controller));
				rows.Add(Toggle("Subtitles", "GUI_SCREEN_OPTIONS_SUBTITLES", s.Subtitles));
				rows.Add(Toggle("Tutorial", "GUI_SCREEN_OPTIONS_TUTORIAL_BOXES", s.Tutorial));
				rows.Add(Bar("Gamma", "GUI_SCREEN_OPTIONS_GAMMA", s.Gamma));
				rows.Add(new Row("Breadcrumb", resolve("GUI_SCREEN_OPTIONS_BREADCRUMB_SIZE"), Pick(Breadcrumbs, s.Breadcrumb), "GUI_SCREEN_OPTIONS_BREADCRUMB_SIZE_DESC"));
				rows.Add(Bar("Music", "GUI_SCREEN_OPTIONS_MUSIC", s.Music));
				rows.Add(Bar("SoundEffects", "GUI_SCREEN_OPTIONS_SOUNDS", s.Sfx));
				rows.Add(Bar("Voice", "GUI_SCREEN_OPTIONS_VOICE", s.Voice));
				rows.Add(new Row("SpeakerType", resolve("GUI_SCREEN_OPTIONS_AUDIO_SPEAKERS"), Pick(Speakers, s.Speaker), "GUI_SCREEN_OPTIONS_AUDIO_SPEAKERS_DESC"));
				break;
			case PageGameSettingsLite:
				string language = s.LanguageIndex >= 0 && s.LanguageIndex < languages.Count ? languages[s.LanguageIndex] : string.Empty;
				rows.Add(new Row("Language", resolve("GUI_SCREEN_OPTIONS_LANGUAGE"), language, "GUI_SCREEN_OPTIONS_LANGUAGE_DESC"));
				rows.Add(Toggle("Subtitles", "GUI_SCREEN_OPTIONS_SUBTITLES", s.Subtitles));
				rows.Add(Bar("Gamma", "GUI_SCREEN_OPTIONS_GAMMA", s.Gamma));
				rows.Add(Bar("Music", "GUI_SCREEN_OPTIONS_MUSIC", s.Music));
				rows.Add(Bar("SoundEffects", "GUI_SCREEN_OPTIONS_SOUNDS", s.Sfx));
				rows.Add(Bar("Voice", "GUI_SCREEN_OPTIONS_VOICE", s.Voice));
				rows.Add(new Row("SpeakerType", resolve("GUI_SCREEN_OPTIONS_AUDIO_SPEAKERS"), Pick(Speakers, s.Speaker), "GUI_SCREEN_OPTIONS_AUDIO_SPEAKERS_DESC"));
				break;
		}

		return rows;
	}

	/// <summary>
	/// The rows of the list pages (1, 2 and 5), as the handler orders them. <paramref name="stated"/>
	/// answers an element path under the page's section with the text the guest wrote, or null;
	/// a stated label or description wins over the tag.
	/// </summary>
	public static List<Row> ListRows(Snapshot s, System.Func<string, string> resolve, System.Func<string, string?> stated)
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
				if (multiplayer || s.Flag108 != 0u)
				{
					rows.Add(Make("Kick/Leave", s.Flag108 == 2u ? "GUI_NET_PAUSED_DISCONNECT_FROM_HOST" : "GUI_ORB_INTERACT_KICK", string.Empty,
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

	/// <summary>Every page's rows: the settings pages' values, the list pages' entries.</summary>
	public static List<Row> Rows(Snapshot s, System.Func<string, string> resolve, IReadOnlyList<string> languages, System.Func<string, string?> stated)
	{
		List<Row> rows = SettingsRows(s, resolve, languages);
		return rows.Count > 0 ? rows : ListRows(s, resolve, stated);
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
/// The Options screen's state as its own handler and Show left it (`@Options.State(box, page,
/// row, invert, subtitles, tutorial, breadcrumb, speaker, language, languages, flag108, flag112,
/// fromPause, frontEnd, canSave, query, changed, canLoad)`, options_hooks.cpp), held while the box manager
/// shows the screen. Pure, so the tests can run it.
/// </summary>
public static class Fable3OptionsState
{
	/// <summary>The statement's words: the box, then its 17 fields.</summary>
	public const int Words = 18;

	public const int QueryWord = 15;

	/// <summary>Whether the guest offers Load Management (TU3 +192, the disc +112 == 0).</summary>
	public const int CanLoadWord = 17;

	private static uint[]? _words;

	/// <summary>The words while the screen shows, else null.</summary>
	public static uint[]? Current => _words;

	public static ulong Generation { get; private set; }

	/// <summary>The statement's words, or null when it is short.</summary>
	public static uint[]? Parse(Refii.Ui.Engine_UiModel.GuiNativeCall call)
	{
		if (call.Arguments is not { Count: >= Words } arguments ||
			!uint.TryParse(arguments[0].Text, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint box))
		{
			return null;
		}

		var words = new uint[Words];
		words[0] = box;
		for (int i = 1; i < Words; ++i)
		{
			words[i] = unchecked((uint)(int)arguments[i].Number);
		}

		return words;
	}

	public static void Stated(Refii.Ui.Engine_UiModel.GuiNativeCall call)
	{
		if (Parse(call) is { } words)
		{
			_words = words;
			++Generation;
		}
	}

	/// <summary>The box manager dropped <paramref name="box"/>: its state goes with it.</summary>
	public static void Closed(uint box)
	{
		if (_words is { } words && words[0] == box)
		{
			_words = null;
			++Generation;
		}
	}

	/// <summary>
	/// The model's snapshot: the state's first 15 words with the stated speaker mode, and the
	/// stated levels (gamma, music, sfx, voice). The speaker and the levels are the appliers'.
	/// </summary>
	public static Fable3OptionsModel.Snapshot Snapshot(uint[] words, float gamma, float music, float sfx, float voice, int speaker)
	{
		var head = new uint[Fable3OptionsModel.Words];
		System.Array.Copy(words, head, Fable3OptionsModel.Words);
		if (speaker is >= 0 and <= 2)
		{
			head[7] = (uint)speaker;
		}

		return Fable3OptionsModel.Parse(head, new[] { gamma, music, sfx, voice }) with
		{
			CanLoad = words.Length <= CanLoadWord || words[CanLoadWord] != 0u,
		};
	}
}

/// <summary>
/// Fable 3's Options screen, drawn on the shared menu template (<see cref="Fable3ListScreen"/>)
/// from its stated state (<see cref="Fable3OptionsState"/>) and the stated levels: the rows are
/// the page's (<see cref="Fable3OptionsModel"/>), the highlight the screen's selected row, the title
/// and the highlighted row's description over it. Godot owns its pad: each press is one game
/// action into the screen's own handler (`options:<p>:<action>`, options_hooks.cpp; the handler
/// takes its own hero's player alone) — up/down move, left/right step its sliders and toggles
/// (sub_825CAA98 / sub_825CB028, applying them live), A enters a page or commits one, B goes back,
/// Y restores the defaults. The pointer states the row itself (`options-row:<p>:<row>[:accept]`):
/// the guest's own row setter takes it at once, and the two settings pages, which have none, are
/// stepped to it natively at the guest's own rate; there a click places the cursor and the wheel
/// steps the value (the row's own left and right setters).
/// </summary>
public partial class Fable3Options : Fable3ListScreen
{
	private const double RepeatDelay = 0.35;
	private const double RepeatEvery = 0.2;
	private const int StickDeadZone = 16000;

	private readonly List<string> _languages = new();
	private double _languagesAge = double.MaxValue;
	private int _page;
	private int _selected;
	private uint _wasDown;
	private int _held;
	private double _repeat;

	protected override string LogTag => "fable3-options";

	protected override string LogContext => $"page {_page} ";

	/// <summary>The Options sections author their arrows; nothing writes them (<see cref="Fable3ListCursor.OptionsArrows"/>).</summary>
	protected override (bool Top, bool Bottom) Arrows(int highlight, int total) => Fable3ListCursor.OptionsArrows(_page);

	/// <summary>Options (1), Save Management (2) and Multiplayer (5) centre their choices; Game Settings (3, 4) keeps its rows left.</summary>
	protected override bool RowsOnCentreLine => Fable3ListCursor.RowsCentred(_page);

	/// <summary>
	/// Whether the guest's Game Settings page is up with nothing over it, so X opens the host's
	/// Controls page (<see cref="Fable3HostOptions"/>; the screen's handler ignores X).
	/// </summary>
	public static bool OffersControls { get; private set; }

	protected override Fable3ScreenPage? Compose(double delta)
	{
		OffersControls = false;
		if (!HostBridge.IsGuestUp || Fable3OptionsState.Current is not { } words || Fable3HostOptions.IsOpen)
		{
			return null;
		}

		HostBridge.OptionSliders levels = HostBridge.GetOptionSliders();
		Fable3OptionsModel.Snapshot snapshot = levels.Known
			? Fable3OptionsState.Snapshot(words, levels.Gamma, levels.Music, levels.Sfx, levels.Speech, levels.Speaker)
			: Fable3OptionsState.Snapshot(words, 0.5f, 1.0f, 1.0f, 0.9f, -1);
		_page = snapshot.Page;
		_selected = snapshot.SelectedRow - 1;

		// The language names are the guest's table; asked when the lite page shows, every second.
		if (snapshot.Page == Fable3OptionsModel.PageGameSettingsLite && (_languagesAge += delta) > 1.0)
		{
			_languagesAge = 0.0;
			_languages.Clear();
			foreach (Engine_UiModel.Language language in Engine_UiSource.Current.GetLanguages())
			{
				_languages.Add(language.Name);
			}
		}

		List<Fable3OptionsModel.Row> rows = Fable3OptionsModel.Rows(snapshot, Resolve, _languages, Stated);
		if (rows.Count == 0)
		{
			rows = Fable3OptionsModel.StatedRows(snapshot.Page, StatedTexts());
		}

		var lines = new List<Fable3ScreenPage.Row>(rows.Count);
		foreach (Fable3OptionsModel.Row row in rows)
		{
			lines.Add(new Fable3ScreenPage.Row(row.Name, Fable3OptionsModel.Line(row), row.Ghosted, row.Description));
		}

		Fable3ScreenPage.QueryBox? query = words[Fable3OptionsState.QueryWord] != 0u ? Query(words[0]) : null;
		OffersControls = query is null &&
			snapshot.Page is Fable3OptionsModel.PageGameSettings or Fable3OptionsModel.PageGameSettingsLite;

		Drive(delta);

		Fable3ScreenPage.Prompt[] prompts = OffersControls
			? new[] { new Fable3ScreenPage.Prompt("X", Fable3SettingsHub.Page.Title) }
			: System.Array.Empty<Fable3ScreenPage.Prompt>();
		return new Fable3ScreenPage(Resolve(Fable3OptionsModel.PageTitle(snapshot.Page)), lines, _selected,
			string.Empty, prompts, true, query);
	}

	/// <summary>The pad, as game actions into the screen's handler; held directions repeat.</summary>
	private void Drive(double delta)
	{
		uint down = GameInput.Pad0.Buttons;
		uint pressed = down & ~_wasDown;
		_wasDown = down;
		GameInput.PadState pad = GameInput.Pad0;

		var press = new Fable3BoxAnswers.Press(
			(pressed & GameInput.ButtonA) != 0, (pressed & GameInput.ButtonB) != 0, false, (pressed & GameInput.ButtonY) != 0,
			false, false, false, false);
		if (Fable3BoxAnswers.OptionsAction(press) is { } button)
		{
			Send(button);
			Refii.Core.Engine_TitleGame.Active.Input.HoldUntilReleased();
			return;
		}

		int held = (down & GameInput.DpadUp) != 0 || pad.LeftY > StickDeadZone ? Fable3BoxAnswers.ActionUp :
			(down & GameInput.DpadDown) != 0 || pad.LeftY < -StickDeadZone ? Fable3BoxAnswers.ActionDown :
			(down & GameInput.DpadLeft) != 0 || pad.LeftX < -StickDeadZone ? Fable3BoxAnswers.ActionLeft :
			(down & GameInput.DpadRight) != 0 || pad.LeftX > StickDeadZone ? Fable3BoxAnswers.ActionRight : 0;
		if (held != _held)
		{
			_held = held;
			_repeat = RepeatDelay;
			if (held != 0)
			{
				Send(held);
			}
		}
		else if (held != 0 && (_repeat -= delta) <= 0.0)
		{
			_repeat = RepeatEvery;
			Send(held);
		}
	}

	private static void Send(int action) =>
		HostBridge.PostFrontEndAction(Fable3BoxAnswers.Options(action, Fable3ActingPlayer.OfPad(0)));

	/// <summary>
	/// The cursor is the pointer's to state: a click puts the screen's cursor on the row and, on a
	/// list page, accepts it in one action (<see cref="Fable3BoxAnswers.OptionsRow"/>); on a settings
	/// page a click only places the cursor (A there commits the page), and the wheel steps the
	/// cursor row's value (<see cref="PointWheel"/>). The mouse is player one's.
	/// </summary>
	protected override void PointAt(int row, bool clicked)
	{
		bool accept = clicked && Fable3BoxAnswers.OptionsClickAccepts(_page);
		if ((accept || row != _selected) &&
			Fable3BoxAnswers.OptionsRow(row, accept, Fable3ActingPlayer.One) is { } action)
		{
			HostBridge.PostFrontEndAction(action);
		}
	}

	/// <summary>
	/// The wheel over a settings page's cursor row steps its value through its own setters, as the
	/// pad's left and right do (<see cref="Fable3BoxAnswers.OptionsWheelSteps"/>); anywhere else it
	/// moves the cursor, as on every list.
	/// </summary>
	protected override bool PointWheel(int steps, int row)
	{
		if (row != _selected || _page is not (Fable3OptionsModel.PageGameSettings or Fable3OptionsModel.PageGameSettingsLite))
		{
			return false;
		}

		foreach (int action in Fable3BoxAnswers.OptionsWheelSteps(steps))
		{
			HostBridge.PostFrontEndAction(Fable3BoxAnswers.Options(action, Fable3ActingPlayer.One));
		}

		return true;
	}

	private const string QueryRoot = "Options.Sections.QueryBox";

	/// <summary>
	/// THE QUERY IS THE SCREEN'S. sub_825CBEC0(box, tag) — called by the A handler sub_825CB588 for
	/// New Game, Save and Continue, Load and Kick/Leave (0x825cb60c, 0x825cb8ac, 0x825cb8c0,
	/// 0x825cb8e4, 0x825cba94) and the B handler sub_825CBAA0 (0x825cbb80, 0x825cbc44) — sets the
	/// `Options` root to state 6 (0x825cbef4), writes the question by tag into
	/// `Options.Sections.QueryBox.Group.QueryBox.Text` (sub_82819328, 0x825cbf08) and raises box+30
	/// (0x825cbf10), which the screen's state carries; its handler answers it on 74 / 75
	/// (0x825CA5AC..0x825CA664). All of that is behind the box's presentation (0x825CBEE0), so the
	/// question is the call's tag (`@Options.Query`, screen_stated_hooks.cpp), resolved through the
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
