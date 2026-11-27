namespace Refii.Tests.Titles;

using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable3;

/// <summary>
/// Fable 3's Options in the PC layout, Godot's side: the guest's row of each setting per page
/// (sub_825F55B8's switches), the setters' steps, the four Game Settings tabs, the list pages from
/// the stated gates, and Godot's own state following the guest's statements.
/// </summary>
[TestSuite]
public class Fable3OptionsModelTests
{
	private static Fable3OptionsState.State State(int page, int row = 1, uint multiplayer = 0u, bool canSave = true, bool canLoad = true, uint box = 0x12345678u) =>
		new(box, page, row, true, false, canSave, multiplayer, 0u, canLoad, 2, 4, false, false);

	private static string? NoStated(string path) => null;

	private static string Resolve(string tag) => "<" + tag + ">";

	[TestCase]
	public void EachSettingIsTheGuestsRowOnItsPage()
	{
		AssertThat(Fable3OptionsModel.GuestRow(3, Fable3OptionSetting.Invert)).IsEqual(1);
		AssertThat(Fable3OptionsModel.GuestRow(3, Fable3OptionSetting.Gamma)).IsEqual(4);
		AssertThat(Fable3OptionsModel.GuestRow(3, Fable3OptionSetting.Breadcrumb)).IsEqual(5);
		AssertThat(Fable3OptionsModel.GuestRow(3, Fable3OptionSetting.Speaker)).IsEqual(9);
		AssertThat(Fable3OptionsModel.GuestRow(3, Fable3OptionSetting.Language)).IsEqual(0);
		AssertThat(Fable3OptionsModel.GuestRow(4, Fable3OptionSetting.Language)).IsEqual(1);
		AssertThat(Fable3OptionsModel.GuestRow(4, Fable3OptionSetting.Gamma)).IsEqual(3);
		AssertThat(Fable3OptionsModel.GuestRow(4, Fable3OptionSetting.Speaker)).IsEqual(7);
		AssertThat(Fable3OptionsModel.GuestRow(4, Fable3OptionSetting.Invert)).IsEqual(0);
		AssertThat(Fable3OptionsModel.GuestRow(1, Fable3OptionSetting.Music)).IsEqual(0);
	}

	[TestCase]
	public void TheStepsAreTheSetters()
	{
		// Toggles go to 1 on right and 0 on left, whatever they were.
		AssertThat(Fable3OptionsModel.Step(Fable3OptionSetting.Subtitles, 1f, 1, 0)).IsEqual(1f);
		AssertThat(Fable3OptionsModel.Step(Fable3OptionSetting.Subtitles, 1f, -1, 0)).IsEqual(0f);
		// Bars move 0.025 on the grid and stop at the ends.
		AssertThat(Fable3OptionsModel.Step(Fable3OptionSetting.Music, 0.9f, 1, 0)).IsEqualApprox(0.925f, 0.0001f);
		AssertThat(Fable3OptionsModel.Step(Fable3OptionSetting.Music, 1f, 1, 0)).IsEqual(1f);
		AssertThat(Fable3OptionsModel.Step(Fable3OptionSetting.Gamma, 0f, -1, 0)).IsEqual(0f);
		// Counts move one within their range: breadcrumb 0..4, speaker 0..2, language 0..count-1.
		AssertThat(Fable3OptionsModel.Step(Fable3OptionSetting.Breadcrumb, 4f, 1, 0)).IsEqual(4f);
		AssertThat(Fable3OptionsModel.Step(Fable3OptionSetting.Speaker, 0f, -1, 0)).IsEqual(0f);
		AssertThat(Fable3OptionsModel.Step(Fable3OptionSetting.Language, 2f, 1, 4)).IsEqual(3f);
		AssertThat(Fable3OptionsModel.Step(Fable3OptionSetting.Language, 3f, 1, 4)).IsEqual(3f);
	}

	[TestCase]
	public void ADragIsTheValueSteppingWouldReach()
	{
		AssertThat(Fable3OptionsModel.FromFraction(0.5f)).IsEqual(0.5f);
		AssertThat(Fable3OptionsModel.FromFraction(0.51f)).IsEqualApprox(0.5f, 0.0001f);
		AssertThat(Fable3OptionsModel.FromFraction(0.52f)).IsEqualApprox(0.525f, 0.0001f);
		AssertThat(Fable3OptionsModel.FromFraction(1.5f)).IsEqual(1f);
		AssertThat(Fable3ListScreen.SliderFraction(40f)).IsEqual(0f);
		AssertThat(Fable3ListScreen.SliderFraction(360f)).IsEqual(1f);
		AssertThat(Fable3ListScreen.SliderFraction(200f)).IsEqual(0.5f);
		AssertThat(Fable3ListScreen.SliderFraction(-500f)).IsEqual(0f);
		// Stepping 0.9 to 0.5 lands where the drag does.
		float value = 0.9f;
		for (int i = 0; i < 16; ++i)
		{
			value = Fable3OptionsModel.Step(Fable3OptionSetting.Voice, value, -1, 0);
		}

		AssertThat(value).IsEqualApprox(Fable3OptionsModel.FromFraction(0.5f), 0.0001f);
	}

	[TestCase]
	public void AClickFlipsAToggleAndStepsACountOn()
	{
		AssertThat(Fable3OptionsModel.Clicked(Fable3OptionSetting.Tutorial, 1f, 0)).IsEqual(0f);
		AssertThat(Fable3OptionsModel.Clicked(Fable3OptionSetting.Tutorial, 0f, 0)).IsEqual(1f);
		AssertThat(Fable3OptionsModel.Clicked(Fable3OptionSetting.Speaker, 2f, 0)).IsEqual(0f);
		AssertThat(Fable3OptionsModel.Clicked(Fable3OptionSetting.Breadcrumb, 1f, 0)).IsEqual(2f);
		AssertThat(Fable3OptionsModel.Clicked(Fable3OptionSetting.Music, 0.3f, 0)).IsEqual(0.3f);
	}

	[TestCase]
	public void GameSettingsIsThePcsFourTabs()
	{
		AssertThat(Fable3OptionsModel.TabWords.Length).IsEqual(4);
		IReadOnlyList<Fable3OptionsModel.Entry> display = Fable3OptionsModel.TabEntries(3, Fable3OptionsModel.TabDisplay);
		AssertThat(display.Count).IsEqual(1 + Refii.Ui.Engine_UiDisplayOptions.RowCount);
		AssertThat(display[0].Setting).IsEqual(Fable3OptionSetting.Gamma);
		AssertThat(display[1].Display).IsEqual(0);
		AssertThat(display[4].Display).IsEqual(3);
		IReadOnlyList<Fable3OptionsModel.Entry> controller = Fable3OptionsModel.TabEntries(3, Fable3OptionsModel.TabController);
		AssertThat(controller[0].Setting).IsEqual(Fable3OptionSetting.Invert);
		AssertThat(controller[1].Opens).IsEqual(Refii.Ui.Engine_UiControlsPage.OpenCommand);
		IReadOnlyList<Fable3OptionsModel.Entry> audio = Fable3OptionsModel.TabEntries(3, Fable3OptionsModel.TabAudio);
		AssertThat(audio.Count).IsEqual(4);
		AssertThat(audio[3].Setting).IsEqual(Fable3OptionSetting.Speaker);
		IReadOnlyList<Fable3OptionsModel.Entry> misc = Fable3OptionsModel.TabEntries(3, Fable3OptionsModel.TabMisc);
		AssertThat(misc.Count).IsEqual(3);
		AssertThat(misc[0].Setting).IsEqual(Fable3OptionSetting.Subtitles);
		// The front end's page: no invert, tutorial or breadcrumb; the language under Misc.
		AssertThat(Fable3OptionsModel.TabEntries(4, Fable3OptionsModel.TabController).Count).IsEqual(1);
		IReadOnlyList<Fable3OptionsModel.Entry> liteMisc = Fable3OptionsModel.TabEntries(4, Fable3OptionsModel.TabMisc);
		AssertThat(liteMisc.Count).IsEqual(2);
		AssertThat(liteMisc[0].Setting).IsEqual(Fable3OptionSetting.Language);
		// Every setting of each page is on exactly one tab.
		foreach (int page in new[] { 3, 4 })
		{
			int count = 0;
			for (int tab = 0; tab < 4; ++tab)
			{
				foreach (Fable3OptionsModel.Entry entry in Fable3OptionsModel.TabEntries(page, tab))
				{
					count += entry.Setting is not null ? 1 : 0;
				}
			}

			AssertThat(count).IsEqual(page == 3 ? 9 : 7);
		}
	}

	[TestCase]
	public void TabRowsCarryTheValueOrTheBar()
	{
		var values = new float[Fable3OptionsModel.Settings];
		values[(int)Fable3OptionSetting.Music] = 0.75f;
		values[(int)Fable3OptionSetting.Speaker] = 2f;
		List<Fable3OptionsModel.Row> audio = Fable3OptionsModel.TabRows(3, Fable3OptionsModel.TabAudio, values, Resolve, new List<string>());
		AssertThat(audio[0].Label).IsEqual("<GUI_SCREEN_OPTIONS_MUSIC>");
		AssertThat(audio[0].Bar).IsEqual(0.75f);
		AssertThat(audio[0].Value).IsEqual(string.Empty);
		AssertThat(audio[3].Value).IsEqual("<GUI_SCREEN_OPTIONS_AUDIO_SURROUND>");
		AssertThat(audio[3].Bar).IsNull();
		AssertThat(audio[3].Description).IsEqual("GUI_SCREEN_OPTIONS_AUDIO_SPEAKERS_DESC");
		values[(int)Fable3OptionSetting.Language] = 1f;
		List<Fable3OptionsModel.Row> misc = Fable3OptionsModel.TabRows(4, Fable3OptionsModel.TabMisc, values, Resolve, new List<string> { "English", "Français" });
		AssertThat(misc[0].Value).IsEqual("Français");
		AssertThat(misc[1].Value).IsEqual("<TEXT_GUI_OPTIONS_OFF>");

		// The Display tab's launcher rows take their label and caption from the Display page.
		List<Fable3OptionsModel.Row> display = Fable3OptionsModel.TabRows(3, Fable3OptionsModel.TabDisplay, values, Resolve, new List<string>(),
			row => ("Row" + row, "Value" + row));
		AssertThat(display.Count).IsEqual(1 + Refii.Ui.Engine_UiDisplayOptions.RowCount);
		AssertThat(display[2].Label).IsEqual("Row1");
		AssertThat(display[2].Value).IsEqual("Value1");
		AssertThat(display[2].Bar).IsNull();
	}

	[TestCase]
	public void GodotsStateFollowsTheGuestsStatements()
	{
		var work = new Fable3OptionsWork();
		// The list page: the cursor where the guest put its own.
		AssertThat(work.Follow(State(1, row: 2), null)).IsTrue();
		AssertThat(work.Cursor).IsEqual(1);
		work.Cursor = 0;
		AssertThat(work.Follow(State(1, row: 2), null)).IsFalse();
		AssertThat(work.Cursor).IsEqual(0);

		// Game Settings opens: first tab, first row; the fill's values are the working values.
		var filled = new float[Fable3OptionsModel.Settings];
		filled[(int)Fable3OptionSetting.Voice] = 0.89999998f;
		AssertThat(work.Follow(State(3), new Fable3OptionsState.Values(0x12345678u, 1, 3, filled))).IsTrue();
		AssertThat(work.Tab).IsEqual(Fable3OptionsModel.TabDisplay);
		AssertThat(work.Values[(int)Fable3OptionSetting.Voice]).IsEqualApprox(0.9f, 0.0001f);

		// Godot's own change stands until the guest states values again.
		AssertThat(work.Set(Fable3OptionSetting.Voice, 0.5f)).IsTrue();
		AssertThat(work.Set(Fable3OptionSetting.Voice, 0.5f)).IsFalse();
		work.SetTab(-1);
		AssertThat(work.Tab).IsEqual(Fable3OptionsModel.TabMisc);
		AssertThat(work.Follow(State(3), new Fable3OptionsState.Values(0x12345678u, 1, 3, filled))).IsFalse();
		AssertThat(work.Values[(int)Fable3OptionSetting.Voice]).IsEqual(0.5f);
		AssertThat(work.Tab).IsEqual(Fable3OptionsModel.TabMisc);

		// The defaults: a new epoch replaces them.
		var defaults = new float[Fable3OptionsModel.Settings];
		defaults[(int)Fable3OptionSetting.Voice] = 0.9f;
		AssertThat(work.Follow(State(3), new Fable3OptionsState.Values(0x12345678u, 2, 3, defaults))).IsTrue();
		AssertThat(work.Values[(int)Fable3OptionSetting.Voice]).IsEqualApprox(0.9f, 0.0001f);

		// Another box's values are not this one's.
		AssertThat(work.Follow(State(3), new Fable3OptionsState.Values(0x99999999u, 3, 3, filled))).IsFalse();
	}

	[TestCase]
	public void OptionsPageListsTheHandlersRows()
	{
		List<Fable3OptionsModel.Row> rows = Fable3OptionsModel.ListRows(State(Fable3OptionsModel.PageOptions, 2), Resolve, NoStated);
		AssertThat(rows.Count).IsEqual(3);
		AssertThat(rows[0].Name).IsEqual("SaveManagement");
		AssertThat(rows[0].Label).IsEqual("<GUI_SAVE_MANAGEMENT>");
		AssertThat(rows[1].Name).IsEqual("GameSettings");
		AssertThat(rows[2].Name).IsEqual("NewGame");
		AssertThat(rows[2].Description).IsEqual("GUI_NEW_GAME_DESC");
		AssertThat(Fable3OptionsModel.ListRowCount(1, 0u)).IsEqual(3);
		AssertThat(Fable3OptionsModel.ListRowCount(5, 1u)).IsEqual(4);
		AssertThat(Fable3OptionsModel.ListRowCount(2, 0u)).IsEqual(2);

		// A stated label wins over the tag; the multiplayer page carries the Kick/Leave row.
		List<Fable3OptionsModel.Row> stated = Fable3OptionsModel.ListRows(State(Fable3OptionsModel.PageMultiplayer, 4, multiplayer: 1u), Resolve,
			path => path == "Options.Sections.Multiplayer.SelectionText.Kick/Leave" ? "Kick" : null);
		AssertThat(stated.Count).IsEqual(4);
		AssertThat(stated[3].Label).IsEqual("Kick");
	}

	/// <summary>The host's Achievements row follows the handler's rows on Options and Multiplayer only; the handler's rows are unchanged.</summary>
	[TestCase]
	public void TheAchievementsRowIsTheHostsOnOptionsAndMultiplayer()
	{
		List<Fable3OptionsModel.Row> options = Fable3OptionsModel.HostRows(Fable3OptionsModel.PageOptions, Resolve);
		AssertThat(options.Count).IsEqual(1);
		AssertThat(options[0].Name).IsEqual(Fable3OptionsModel.AchievementsRow);
		AssertThat(options[0].Label).IsEqual("<REFII_ACHIEVEMENTS>");
		AssertThat(Fable3OptionsModel.HostRows(Fable3OptionsModel.PageMultiplayer, Resolve).Count).IsEqual(1);
		AssertThat(Fable3OptionsModel.HostRows(Fable3OptionsModel.PageSaveManagement, Resolve).Count).IsEqual(0);
		AssertThat(Fable3OptionsModel.HostRows(Fable3OptionsModel.PageGameSettings, Resolve).Count).IsEqual(0);
		AssertThat(Fable3OptionsModel.HostRows(Fable3OptionsModel.PageGameSettingsLite, Resolve).Count).IsEqual(0);
		AssertThat(Fable3OptionsModel.ListRows(State(Fable3OptionsModel.PageOptions, 2), Resolve, NoStated).Count).IsEqual(3);
	}

	[TestCase]
	public void SaveManagementGhostsWhatTheGuestRefuses()
	{
		// Load is ghosted by the stated canLoad (TU3 +192, the disc +112 == 0).
		List<Fable3OptionsModel.Row> rows = Fable3OptionsModel.ListRows(State(Fable3OptionsModel.PageSaveManagement, canSave: false, canLoad: false), Resolve, NoStated);
		AssertThat(rows.Count).IsEqual(2);
		AssertThat(rows[0].Name).IsEqual("Save&Continue");
		AssertThat(rows[0].Ghosted).IsTrue();
		AssertThat(rows[1].Name).IsEqual("LoadMangement");
		AssertThat(rows[1].Ghosted).IsTrue();
		AssertThat(Fable3OptionsModel.ListRows(State(Fable3OptionsModel.PageSaveManagement), Resolve, NoStated)[0].Ghosted).IsFalse();
	}

	/// <summary>No row of any settings tab or list page spells its value with art characters: a bar is Row.Bar, drawn by the template.</summary>
	[TestCase]
	public void NoRowSpellsArtWithCharacters()
	{
		var values = new float[Fable3OptionsModel.Settings];
		for (int i = 0; i < values.Length; ++i)
		{
			values[i] = 0.5f;
		}

		var lines = new List<string>();
		foreach (int page in new[] { Fable3OptionsModel.PageGameSettings, Fable3OptionsModel.PageGameSettingsLite })
		{
			for (int tab = 0; tab < Fable3OptionsModel.TabWords.Length; ++tab)
			{
				foreach (Fable3OptionsModel.Row row in Fable3OptionsModel.TabRows(page, tab, values, Resolve, new List<string> { "English" }))
				{
					lines.Add(Fable3OptionsModel.Line(row));
				}
			}
		}

		foreach (int page in new[] { Fable3OptionsModel.PageOptions, Fable3OptionsModel.PageSaveManagement, Fable3OptionsModel.PageMultiplayer })
		{
			foreach (Fable3OptionsModel.Row row in Fable3OptionsModel.ListRows(State(page), Resolve, NoStated))
			{
				lines.Add(Fable3OptionsModel.Line(row));
			}
		}

		AssertThat(lines.Count > 10).IsTrue();
		foreach (string line in lines)
		{
			foreach (char c in line)
			{
				AssertThat(c >= '\u2500' && c <= '\u27BF').OverrideFailureMessage($"'{line}' spells art with U+{(int)c:X4}").IsFalse();
			}
		}
	}

	[TestCase]
	public void StatedPagesListTheSectionsRowTexts()
	{
		var texts = new List<KeyValuePair<string, string>>
		{
			new("Options.Sections.Options.SaveManagement", "Save Management"),
			new("Options.Sections.Options.GameSettings", "Game Settings"),
			new("Options.Sections.Options.GameSettings.Description", "nested, not a row"),
			new("Options.Sections.GameSettings.Gamma", "another page"),
			new("HeroStatus.Section1.Values.Strength", "another screen"),
		};
		List<Fable3OptionsModel.Row> rows = Fable3OptionsModel.StatedRows(Fable3OptionsModel.PageOptions, texts);
		AssertThat(rows.Count).IsEqual(2);
		AssertThat(rows[0].Label).IsEqual("Save Management");
		AssertThat(rows[1].Name).IsEqual("GameSettings");
	}

	[TestCase]
	public void SectionPathsAreTheGuestsElementNames()
	{
		AssertThat(Fable3OptionsModel.SectionPath(Fable3OptionsModel.PageGameSettings)).IsEqual("Options.Sections.GameSettings");
		AssertThat(Fable3OptionsModel.SectionPath(Fable3OptionsModel.PageMultiplayer)).IsEqual("Options.Sections.Multiplayer");
		AssertThat(Fable3OptionsModel.Line(new Fable3OptionsModel.Row("Gamma", "Gamma", "50%", string.Empty))).IsEqual("Gamma    50%");
		AssertThat(Fable3OptionsModel.Line(new Fable3OptionsModel.Row("Save", "Save", string.Empty, string.Empty))).IsEqual("Save");
	}
}
