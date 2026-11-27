namespace Refii.Tests.Titles;

using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Text;
using Refii.Titles.Fable3;

/// <summary>
/// Fable 3's host options driver (Fable3HostOptions), its pure rules: which click is A on a row,
/// and the prompts each kind of page offers.
/// </summary>
[TestSuite]
public class Fable3HostOptionsTests
{
	[TestCase]
	public void AnOpenerOpensOnTheFirstClick()
	{
		AssertThat(Fable3HostOptions.ClickAccepts(rowsTakeAccept: true, onCursor: false, choices: 1)).IsTrue();
		AssertThat(Fable3HostOptions.ClickAccepts(rowsTakeAccept: true, onCursor: true, choices: 1)).IsTrue();
	}

	[TestCase]
	public void ARebindRowTakesTheFirstClickAsThePickAndTheSecondAsA()
	{
		AssertThat(Fable3HostOptions.ClickAccepts(rowsTakeAccept: true, onCursor: false, choices: 5)).IsFalse();
		AssertThat(Fable3HostOptions.ClickAccepts(rowsTakeAccept: true, onCursor: true, choices: 5)).IsTrue();
	}

	/// <summary>A value page (the Display rows, the weather, a mod's settings) keeps A as its save: no click is A.</summary>
	[TestCase]
	public void AValuePagesClickNeverAccepts()
	{
		AssertThat(Fable3HostOptions.ClickAccepts(rowsTakeAccept: false, onCursor: true, choices: 5)).IsFalse();
		AssertThat(Fable3HostOptions.ClickAccepts(rowsTakeAccept: false, onCursor: false, choices: 1)).IsFalse();
		AssertThat(Fable3HostOptions.ClickAccepts(rowsTakeAccept: false, onCursor: true, choices: 1)).IsFalse();
	}

	[TestCase]
	public void ThePromptsFollowThePagesKind()
	{
		AssertThat(Spell(Fable3HostOptions.Prompts(rowsTakeAccept: true, commits: false))).IsEqual("A REFII_OPEN, B REFII_BACK");
		AssertThat(Spell(Fable3HostOptions.Prompts(rowsTakeAccept: true, commits: true))).IsEqual("A REFII_CHANGE, X REFII_SAVE, B REFII_BACK");
		AssertThat(Spell(Fable3HostOptions.Prompts(rowsTakeAccept: false, commits: true))).IsEqual("A REFII_SAVE, B REFII_BACK");
		AssertThat(Fable3HostOptions.CommitButton).IsEqual("X");
	}

	/// <summary>Every prompt word is one of ours, stated in every shipped language.</summary>
	[TestCase]
	public void EveryPromptWordIsStatedInEveryLanguage()
	{
		foreach (bool rows in new[] { false, true })
		{
			foreach (bool commits in new[] { false, true })
			{
				foreach ((string _, string word) in Fable3HostOptions.Prompts(rows, commits))
				{
					foreach (string culture in Engine_OwnWords.Cultures)
					{
						AssertThat(string.IsNullOrWhiteSpace(Engine_OwnWords.Shared.Get(word, culture))).OverrideFailureMessage(word + " [" + culture + "]").IsFalse();
					}
				}
			}
		}

		AssertThat(Engine_OwnWords.Shared.Get("REFII_CHANGE", "en-uk")).IsEqual("Change");
		AssertThat(Engine_OwnWords.Shared.Get("REFII_CHANGE", "de-de")).IsEqual("Ändern");
		AssertThat(Engine_OwnWords.Shared.Get("REFII_CHANGE", "sk-sk")).IsEqual("Změnit");
	}

	private static string Spell(IReadOnlyList<(string Button, string Word)> prompts)
	{
		var parts = new List<string>();
		foreach ((string button, string word) in prompts)
		{
			parts.Add(button + " " + word);
		}

		return string.Join(", ", parts);
	}
}
