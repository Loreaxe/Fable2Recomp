namespace Refii.Tests.Titles;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GdUnit4;
using Godot;
using Refii.Core;
using Refii.Mods;
using Refii.Titles.Fable2;
using Refii.Ui;
using static GdUnit4.Assertions;

/// <summary>
/// What each title answers through the hooks Fable 2's HUD table, display boxes, save files and
/// hard-mod guide cards moved behind, pinned to the values the shell held before the move: Fable 2
/// keeps every answer, and Fable 3 answers what the shared code gave it.
/// </summary>
[TestSuite]
public class TitleLiftPinTests
{
	private static Engine_HudLayout.Piece Piece(string id, string art = "") =>
		new(id, art, Engine_HudLayout.Anchor.TopLeft, Vector2.Zero);

	private static Engine_HudTitleLayout Fable3Hud => Engine_TitleGame.For(Engine_Title.Fable3).HudLayout;

	[TestCase]
	public void Fable2sHudGroupTableIsTheOneTheShellHeld()
	{
		var expected = new (string Name, string Label, Engine_HudLayout.Anchor? Anchor, float Depth)[]
		{
			("wheelmenu", "ExpressionWheel", null, 0.5f),
			("TopBox", "QuestBox", Engine_HudLayout.Anchor.TopCentre, 0f),
			("ExpressionMeter", "ExpressionMeter", null, 0.5f),
			("JobMultiplier", "JobMultiplier", null, 0.5f),
			("PlayerHealth", "PlayerHealth", Engine_HudLayout.Anchor.TopLeft, 0f),
			("CoopHealth", "CoopHealth", Engine_HudLayout.Anchor.TopRight, 0f),
			("TargetedQuery", "TargetedQuery", Engine_HudLayout.Anchor.TopCentre, 0f),
			("SpellCharging", "SpellCharging", null, 0f),
			("SpellCharging2", "SpellCharging2", null, 0f),
			("LookAt", "LookAt", null, 0f),
		};

		IReadOnlyList<Engine_HudLayout.GroupSubtree> groups = Fable2HudLayout.Instance.Groups;
		AssertThat(groups.Count).IsEqual(expected.Length);
		for (int i = 0; i < expected.Length; i++)
		{
			AssertThat(groups[i]).IsEqual(new Engine_HudLayout.GroupSubtree(
				expected[i].Name, expected[i].Label, expected[i].Anchor, expected[i].Depth));
		}

		IReadOnlyDictionary<string, (Engine_HudLayout.Anchor Anchor, Vector2 Offset)> placed = Fable2HudLayout.Instance.Arrangement;
		AssertThat(placed.Count).IsEqual(2);
		AssertThat(placed["group.PlayerHealth.PlayerHealth"]).IsEqual((Engine_HudLayout.Anchor.TopLeft, new Vector2(69f, 65f)));
		AssertThat(placed["group.CoopHealth.CoopHealth"]).IsEqual((Engine_HudLayout.Anchor.TopRight, new Vector2(-29f, 77f)));
		AssertThat(Fable2HudLayout.Instance.AddsWidgets).IsTrue();
		AssertThat(Engine_TitleGame.For(Engine_Title.Fable2).HudLayout).IsSame(Fable2HudLayout.Instance);
	}

	[TestCase]
	public void TheViewsDefaultLayoutIsFable2sFourPieces()
	{
		Engine_HudLayout shipped = Engine_HudLayout.Shipped();
		AssertThat(shipped.Pieces.Select(p => p.Id).ToArray())
			.ContainsExactly("health.bar", "health.cap", "quest.title", "quest.objective");
		AssertThat(shipped.Pieces[0]).IsEqual(new Engine_HudLayout.Piece("health.bar", "PlayerHealth",
			Engine_HudLayout.Anchor.TopLeft, new Vector2(96f, 64f), "PlayerHealth", Engine_HudLayout.Drive.Bar));
		AssertThat(shipped.Pieces[1]).IsEqual(new Engine_HudLayout.Piece("health.cap", "PlayerHealthMax",
			Engine_HudLayout.Anchor.TopLeft, new Vector2(96f, 64f), "PlayerHealthMax", Engine_HudLayout.Drive.Bar));
		AssertThat(shipped.Pieces[2]).IsEqual(new Engine_HudLayout.Piece("quest.title", "TitleText",
			Engine_HudLayout.Anchor.TopRight, new Vector2(-320f, 72f), "QuestTitle", Engine_HudLayout.Drive.Text));
		AssertThat(shipped.Pieces[3]).IsEqual(new Engine_HudLayout.Piece("quest.objective", "Text",
			Engine_HudLayout.Anchor.TopRight, new Vector2(-320f, 104f), "QuestObjective", Engine_HudLayout.Drive.Text));
	}

	[TestCase]
	public void Fable2sSeatRulesAreTheOnesTheShellHeld()
	{
		Fable2HudLayout f2 = Fable2HudLayout.Instance;
		AssertThat(f2.OwnerOf(Piece("group.PlayerHealth.PlayerHealthBar"))).IsEqual(Engine_UiSeats.Seat.PlayerOne);
		AssertThat(f2.OwnerOf(Piece("group.SpellCharging.SpellCharging"))).IsEqual(Engine_UiSeats.Seat.PlayerOne);
		AssertThat(f2.OwnerOf(Piece("group.CoopHealth.CoopHealth"))).IsEqual(Engine_UiSeats.Seat.PlayerTwo);
		AssertThat(f2.OwnerOf(Piece("group.SpellCharging2.SpellCharging2"))).IsEqual(Engine_UiSeats.Seat.PlayerTwo);
		AssertThat(f2.OwnerOf(Piece("group.QuestBox.TopBox"))).IsEqual(Engine_UiSeats.Seat.Whole);

		foreach (string each in new[]
		{
			"slidefader2.ExpAmounts.ExperienceAmounts", "meter.TopBoxY.ExperienceAmounts",
			"text.GenericExpText.GeneralExperience", "text.StrengthExpText.StrengthExperience",
			"text.SkillExpText.SkillExperience", "text.WillExpText.WillExperience",
			"group.TargetedQuery.TargetedQuery", "slidetoggle.DPadUp.up", "meter.SubtitlesY.dpad",
			"meter.SubtitlesY.dpadmenu", "slidetoggle.Subtitles.dpad",
		})
		{
			AssertThat(f2.EachPlayerOnSplit(Piece(each))).OverrideFailureMessage(each).IsTrue();
		}

		AssertThat(f2.IsDpad(Piece("slidetoggle.DPadLeft.left"))).IsTrue();
		AssertThat(f2.IsDpad(Piece("group.TargetedQuery.TargetedQuery"))).IsFalse();
		AssertThat(f2.SharedOnSplit(Piece("slidefader2.Subtitles.Subtitles"))).IsTrue();
		AssertThat(f2.IsExpressionWheel(Piece("anything", "wheelmenu"))).IsTrue();
		AssertThat(f2.IsExpressionMeter(Piece("group.ExpressionMeter.ExpressionMeter"))).IsTrue();
		AssertThat(f2.IsExpressionMeter(Piece("anything", "ExpressionMeter"))).IsTrue();
		AssertThat(f2.PlayerTwoSourceId("group.SpellCharging.SpellChargingBar")).IsEqual("group.SpellCharging2.SpellCharging2Bar");
		AssertThat(f2.PlayerOneIdOf("group.CoopHealth.CoopHealthBar")).IsEqual("group.PlayerHealth.PlayerHealthBar");
	}

	/// <summary>
	/// THE FABLE 3 HUD READS THE SAME ANSWERS ON THE WHOLE WINDOW. The seat rules answer per title;
	/// Fable 3 states its own only for its couch pairs (Fable3HudLayout: the d-pad, money alert and
	/// followers, each player's), so Fable 2's rules gave none of Fable 3's pieces an owner or a
	/// player-two source, Fable 3's own give one only to the pairs, and the only Fable 3 code that stands
	/// a view in a seat is the HUD document's split (Fable3HudDocument, camera C2f).
	/// </summary>
	[TestCase]
	public void Fable3sHudPiecesGetTheAnswersTheSharedRulesGaveThem()
	{
		AssertThat(Fable3Hud).IsSame(Refii.Titles.Fable3.Fable3HudLayout.Instance);
		AssertThat(Fable3Hud.Groups.Count).IsEqual(0);
		AssertThat(Fable3Hud.Arrangement.Count).IsEqual(0);

		string titles = Path.Combine(RepoRoot()!, "Godot", "src", "Titles", "Fable3");
		var group = new Regex(@"new(?: Engine_HudLayout\.GroupSubtree)?\(\s*""([^""]+)"",\s*""([^""]+)""", RegexOptions.Compiled);
		var ids = new List<string>();
		var seated = new List<string>();
		foreach (string file in Directory.EnumerateFiles(titles, "*.cs", SearchOption.AllDirectories))
		{
			string text = File.ReadAllText(file);
			if (text.Contains("GroupSubtree", StringComparison.Ordinal))
			{
				foreach (Match match in group.Matches(text))
				{
					ids.Add($"group.{match.Groups[2].Value}.{match.Groups[1].Value}");
				}
			}

			if (Regex.IsMatch(text, @"\b(Seat|LocalPlayerTwo|HidesPartner|ExpressionPlayers|WheelPlayer)\s*=(?!=)"))
			{
				seated.Add(Path.GetFileName(file));
			}
		}

		// Every box screen's frame document is labelled "Frame_<document>" (Fable3ScreenFrames).
		ids.Add("group.Frame_bedmenubox.BlackBox");
		AssertThat(ids.Count > 30).OverrideFailureMessage($"only {ids.Count} Fable 3 group ids found").IsTrue();
		foreach (string id in ids)
		{
			Engine_HudLayout.Piece piece = Piece(id);
			AssertThat(Fable2HudLayout.Instance.OwnerOf(piece)).OverrideFailureMessage(id).IsEqual(Engine_UiSeats.Seat.Whole);
			AssertThat(Fable2HudLayout.Instance.PlayerTwoSourceId(id)).OverrideFailureMessage(id).IsNull();
			AssertThat(Fable2HudLayout.Instance.PlayerOneIdOf(id)).OverrideFailureMessage(id).IsNull();
			bool paired = Array.Exists(Refii.Titles.Fable3.Fable3HudLayout.Pairs, pair => pair.One == id || pair.Two == id);
			if (!paired)
			{
				AssertThat(Fable3Hud.OwnerOf(piece)).OverrideFailureMessage(id).IsEqual(Engine_UiSeats.Seat.Whole);
				AssertThat(Fable3Hud.PlayerTwoSourceId(id)).OverrideFailureMessage(id).IsNull();
			}
		}

		AssertThat(seated).OverrideFailureMessage("Fable 3 seats a HUD view: " + string.Join(", ", seated))
			.ContainsExactly("Fable3HudDocument.cs");
	}

	[TestCase]
	public void ABoxIsStatedByFable2AndNeverByFable3()
	{
		AssertThat(Engine_TitleGame.For(Engine_Title.Fable2).StatedBoxes).IsSame(Engine_UiDisplayBoxes.Statements);
		AssertThat(Engine_TitleGame.For(Engine_Title.Fable3).StatedBoxes).IsSame(Engine_UiStatedBoxes.None);

		// The store only Fable 2 writes: no shared or Fable 3 file opens a box in it.
		string src = Path.Combine(RepoRoot()!, "Godot", "src");
		string fable2 = Path.Combine(src, "Titles", "Fable2") + Path.DirectorySeparatorChar;
		var writers = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
			.Concat(Directory.EnumerateFiles(Path.Combine(RepoRoot()!, "Godot", "addons"), "*.cs", SearchOption.AllDirectories))
			.Where(f => !f.StartsWith(fable2, StringComparison.Ordinal))
			.Where(f => Regex.IsMatch(File.ReadAllText(f), @"Engine_UiDisplayBoxes\.(Opened|MenuOpened)\("))
			.Select(Path.GetFileName).ToList();
		AssertThat(writers.Count).OverrideFailureMessage("boxes opened outside Fable 2: " + string.Join(", ", writers)).IsEqual(0);
	}

	[TestCase]
	public void ASavesGuestFilesAreFable2sListAndFable3NamesNone()
	{
		AssertThat(Engine_TitleGame.For(Engine_Title.Fable2).SaveGuestFiles.ToArray()).ContainsExactly(
			"mainsave.bin", "herosave.bin", "chaptersave.bin", "saveuid.bin", "texturemorphs.bin",
			"failquestsave.bin", "Fable2PubInfo.xml");
		AssertThat(Engine_TitleGame.For(Engine_Title.Fable3).SaveGuestFiles.Count).IsEqual(0);

		// The copy that reads the list is reached from Fable 2's front end alone.
		string src = Path.Combine(RepoRoot()!, "Godot", "src");
		string fable2 = Path.Combine(src, "Titles", "Fable2") + Path.DirectorySeparatorChar;
		var callers = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
			.Concat(Directory.EnumerateFiles(Path.Combine(RepoRoot()!, "Godot", "addons"), "*.cs", SearchOption.AllDirectories))
			.Where(f => !f.StartsWith(fable2, StringComparison.Ordinal))
			.Where(f => File.ReadAllText(f).Contains("ExportForConsole(", StringComparison.Ordinal) ||
				File.ReadAllText(f).Contains("SaveExport.Copy(", StringComparison.Ordinal))
			.Select(Path.GetFileName).ToList();
		AssertThat(callers.ToArray()).ContainsExactly("Engine_ModSaveGate.cs");
	}

	[TestCase]
	public void TheGuidesHardModCardsAreFable2sWhicheverTitleIsActive()
	{
		// Fable 3 builds hard mods too (the native bus, core/refii_mod_bus.h) but states no guide cards of its own.
		AssertThat(Engine_Title.Fable2.HasHardMods).IsTrue();
		AssertThat(Engine_Title.Fable3.HasHardMods).IsTrue();
		AssertThat(Engine_TitleGame.For(Engine_Title.Fable3).ModGuideCards(ModGuidePages.ScriptApiKey, false).Count).IsEqual(0);

		ModGuidePage scriptApi = ModGuidePages.Find(ModGuidePages.ScriptApiKey)!;
		AssertThat(scriptApi.Cards.Select(c => c.Title).ToArray()).ContainsExactly(
			"🧩 RefiiModApi — everything a mod is, as calls", "📜 Lua, text and images",
			"🖼️ HUD, abilities and menus", "🖼️ The UI tab — HUD, menus, modals and the settings page",
			"⚙ Hard mods — native code", "🗡️ Worked example: Souls Combat");
		AssertThat(scriptApi.Closing.Count).IsEqual(0);

		ModGuidePage events = ModGuidePages.Find(ModGuidePages.EventsKey)!;
		AssertThat(events.Cards.Count).IsEqual(2);
		AssertThat(events.Closing.Select(c => c.Title).ToArray())
			.ContainsExactly("🗡️ Worked examples: Souls Combat and Spell Weaving");
		AssertThat(events.Closing[0].Body).Contains("Fable2/mods-src/spell_weaving");
	}

	[TestCase]
	public void Fable2sOwnSourceReadsAreAbsentOffTheLiveGuest()
	{
		if (Engine_UiSource.Current is Fable2UiSource)
		{
			return;
		}

		Fable2UiSource reads = Fable2UiSource.Active;
		AssertThat(reads.GetFrontEndDialog().HasValue).IsFalse();
		AssertThat(reads.GetSpellWheel(1)).IsEqual(default(Engine_UiModel.SpellWheelState));
		AssertThat(reads.GetSpellBlade()).IsEqual(default(Engine_UiModel.SpellWheelState));
		AssertThat(reads.GetSpellAssign()).IsEqual(default(Engine_UiModel.SpellAssignState));
		AssertThat(reads.TakeMenuChunks().Count).IsEqual(0);
	}

	[TestCase]
	public void BothFable2AndFable3GuestsOwnStandingLevelEffects()
	{
		AssertThat(Engine_TitleGame.For(Engine_Title.Fable2).GuestStatesLevelEffects).IsTrue();
		AssertThat(Engine_TitleGame.For(Engine_Title.Fable3).GuestStatesLevelEffects).IsTrue();
	}

	private static string? RepoRoot()
	{
		string? at = AppContext.BaseDirectory;
		for (int i = 0; i < 12 && at != null; i++)
		{
			if (File.Exists(Path.Combine(at, "Godot", "project.godot")) &&
				Directory.Exists(Path.Combine(at, "Fable2")))
			{
				return at;
			}

			at = Path.GetDirectoryName(at);
		}

		return null;
	}
}
