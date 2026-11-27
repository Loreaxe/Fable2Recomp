namespace Refii.Tests.Achievements;

using System;
using System.IO;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Text;

/// <summary>
/// Fable 2's achievements page (screen 1013) speaks the game's language: every line it adds is one of our
/// own words in all thirteen cultures, and no English literal is left on its face.
/// </summary>
[TestSuite]
public class AchievementPageWordsTests
{
	private static string? RepoRoot()
	{
		string? at = AppContext.BaseDirectory;
		for (int i = 0; i < 12 && at != null; i++)
		{
			if (File.Exists(Path.Combine(at, "Godot", "src", "Engine", "ui", "Engine_UiAchievementPage.cs")))
			{
				return at;
			}

			at = Path.GetDirectoryName(at);
		}

		return null;
	}

	[TestCase]
	public void ThePagesLinesAreOurWordsInEveryCulture()
	{
		foreach (string key in new[]
			{
				"REFII_ACHIEVEMENTS", "REFII_ACH_NOT_READY", "REFII_ACH_UNLOCKED", "REFII_ACH_UNLOCKED_ON",
				"REFII_ACH_LOCKED", "REFII_ACH_NUMBERED",
			})
		{
			foreach (string culture in Engine_OwnWords.Cultures)
			{
				AssertThat(Engine_OwnWords.Find(key, culture)).OverrideFailureMessage($"{key} in {culture}").IsNotNull();
			}
		}

		AssertThat(Engine_OwnWords.Find("REFII_ACH_LOCKED", "de-de")).IsEqual("Gesperrt");
	}

	[TestCase]
	public void AnotherPlayersListOpensOnlyWhenTheirMachineStatedIt()
	{
		AssertThat(Refii.Ui.Engine_UiAchievementPage.ViewPeer("no-such-peer", "Them")).IsFalse();
	}

	[TestCase]
	public void NoEnglishLiteralIsLeftOnThePage()
	{
		string? root = RepoRoot();
		if (root == null)
		{
			return;
		}

		string page = File.ReadAllText(Path.Combine(root, "Godot", "src", "Engine", "ui", "Engine_UiAchievementPage.cs"));
		foreach (string literal in new[] { "\"Unlocked", "\"Locked\"", "\"The title's", "$\"Achievement {", "values[Title] = \"Achievements\"" })
		{
			AssertThat(page.Contains(literal, StringComparison.Ordinal)).OverrideFailureMessage(literal).IsFalse();
		}
	}
}
