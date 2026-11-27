namespace Refii.Tests.Camera;

using System;
using System.Collections.Generic;
using System.IO;
using GdUnit4;
using Refii.Core;
using Refii.Text;
using Refii.Titles.Fable3;
using static GdUnit4.Assertions;

/// <summary>
/// Fable 3's local co-op screen row: on the in-game Game Settings page's Misc tab in a couch game only, the
/// same five values as Fable 2's co-op screen in 13 languages, stepped within them (a click on past the last),
/// and written as it steps. Pure; the live switch is the game lane's (NOTES.md).
/// </summary>
[TestSuite]
public class CouchScreenRowTests
{
	private static string Resolve(string tag) => "<" + tag + ">";

	[TestCase]
	public void TheRowStandsOnlyOnTheInGameMiscTabOfACouchGame()
	{
		AssertThat(Fable3OptionsModel.TabEntries(3, Fable3OptionsModel.TabMisc).Count).IsEqual(3);
		IReadOnlyList<Fable3OptionsModel.Entry> couch = Fable3OptionsModel.TabEntries(3, Fable3OptionsModel.TabMisc, localCoop: true);
		AssertThat(couch.Count).IsEqual(4);
		AssertThat(couch[3].CoopScreen).IsTrue();
		AssertThat(couch[3].Setting).IsNull();
		AssertThat(couch[3].Opens).IsNull();
		AssertThat(couch[3].Display).IsEqual(-1);
		// The front end's page has no co-op; no other tab takes it.
		AssertThat(Fable3OptionsModel.TabEntries(4, Fable3OptionsModel.TabMisc, localCoop: true).Count).IsEqual(2);
		for (int tab = 0; tab < 3; ++tab)
		{
			foreach (Fable3OptionsModel.Entry entry in Fable3OptionsModel.TabEntries(3, tab, localCoop: true))
			{
				AssertThat(entry.CoopScreen).IsFalse();
			}
		}
	}

	[TestCase]
	public void TheRowReadsTheScreenInOurWords()
	{
		Engine_Title was = Engine_Title.Active;
		try
		{
			Engine_Title.Active = Engine_Title.Fable3;
			var values = new float[Fable3OptionsModel.Settings];
			List<Fable3OptionsModel.Row> plain = Fable3OptionsModel.TabRows(3, Fable3OptionsModel.TabMisc, values, Resolve, new List<string>());
			AssertThat(plain.Count).IsEqual(3);
			List<Fable3OptionsModel.Row> rows = Fable3OptionsModel.TabRows(3, Fable3OptionsModel.TabMisc, values, Resolve, new List<string>(), coopScreen: 3);
			AssertThat(rows.Count).IsEqual(4);
			AssertThat(rows[3].Label).IsEqual(Engine_OwnWords.Say(Fable3OptionsModel.CoopScreenWord));
			AssertThat(rows[3].Value).IsEqual(Engine_OwnWords.Say("REFII_F3_COOP_MODE_HYBRID_SIDE"));
			AssertThat(rows[3].Bar).IsNull();
		}
		finally
		{
			Engine_Title.Active = was;
		}

		AssertThat(Fable3OptionsModel.CoopScreenWords.Length).IsEqual(Refii.Camera.Engine_CameraScreens.Rows);
		foreach (string key in new List<string>(Fable3OptionsModel.CoopScreenWords) { Fable3OptionsModel.CoopScreenWord })
		{
			AssertThat(Engine_OwnWords.Fable3.Has(key)).OverrideFailureMessage(key).IsTrue();
			foreach (string culture in Engine_OwnWords.Cultures)
			{
				AssertThat(string.IsNullOrEmpty(Engine_OwnWords.Fable3.Get(key, culture))).OverrideFailureMessage(key + " " + culture).IsFalse();
			}
		}

		// The same five values, in the same order, as Fable 2's co-op screen row says them (its Labels; named
		// here, not read off the row, which is a Godot node).
		string[] fable2 =
		{
			"REFII_F2_COOP_MODE_SHARED", "REFII_F2_COOP_MODE_SIDE", "REFII_F2_COOP_MODE_STACKED",
			"REFII_F2_COOP_MODE_HYBRID_SIDE", "REFII_F2_COOP_MODE_HYBRID_STACKED",
		};
		for (int i = 0; i < Fable3OptionsModel.CoopScreenWords.Length; ++i)
		{
			string f2 = fable2[i];
			foreach (string culture in Engine_OwnWords.Cultures)
			{
				AssertThat(Engine_OwnWords.Fable3.Get(Fable3OptionsModel.CoopScreenWords[i], culture))
					.IsEqual(Engine_OwnWords.Fable2.Get(f2, culture) ?? "(none)");
			}
		}
	}

	[TestCase]
	public void TheRowStepsWithinItsValuesAndAClickGoesOn()
	{
		AssertThat(Fable3OptionsModel.StepCoopScreen(0, -1)).IsEqual(0);
		AssertThat(Fable3OptionsModel.StepCoopScreen(0, 1)).IsEqual(1);
		AssertThat(Fable3OptionsModel.StepCoopScreen(4, 1)).IsEqual(4);
		AssertThat(Fable3OptionsModel.StepCoopScreen(2, -3)).IsEqual(0);
		AssertThat(Fable3OptionsModel.StepCoopScreen(4, 1, wrap: true)).IsEqual(0);
		AssertThat(Fable3OptionsModel.StepCoopScreen(0, -1, wrap: true)).IsEqual(4);
		for (int row = 0; row < Refii.Camera.Engine_CameraScreens.Rows; ++row)
		{
			(Refii.Camera.Engine_CameraScreens.Mode mode, bool stacked) = Refii.Camera.Engine_CameraScreens.FromRow(row, false);
			if (row != 0)
			{
				AssertThat(Refii.Camera.Engine_CameraScreens.Row(mode, stacked)).IsEqual(row);
			}
		}
	}

	[TestCase]
	public void TheRowWritesTheScreenAsItSteps()
	{
		string? at = AppContext.BaseDirectory;
		for (int i = 0; i < 12 && at != null && !File.Exists(Path.Combine(at, "Godot", "src", "Titles", "Fable3", "Fable3Options.cs")); i++)
		{
			at = Path.GetDirectoryName(at);
		}

		if (at is null)
		{
			return;
		}

		string options = File.ReadAllText(Path.Combine(at, "Godot", "src", "Titles", "Fable3", "Fable3Options.cs"));
		foreach (string line in new[]
		{
			"bool coop = Refii.Core.Engine_TitleGame.Active.LocalCoop;",
			"[Refii.Camera.Engine_CameraScreens.ModeKey] = Refii.Camera.Engine_CameraScreens.Name(mode),",
			"[Refii.Camera.Engine_CameraScreens.OrientationKey] = stacked ? \"horizontal\" : \"vertical\",",
			"Engine_UiScreenActions.WriteSettings(settings, new[] { Refii.Camera.Engine_CameraScreens.LegacySplitKey });",
			"StepCoopScreen(1, wrap: true);",
			"StepCoopScreen(-steps);",
			"StepCoopScreen(direction);",
		})
		{
			AssertThat(options.Contains(line, StringComparison.Ordinal)).OverrideFailureMessage(line).IsTrue();
		}
	}
}
