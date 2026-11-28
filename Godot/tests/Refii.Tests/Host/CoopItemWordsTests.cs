namespace Refii.Tests;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Mods;
using Refii.Text;

/// <summary>
/// The co-op item notices (CoopHeroLua.cs C.ItemNotice): our own words in every shipped language, and the
/// lookup the core Lua's Refii.notice reaches (ModText: the book, the mods' tables, the engine's words, then
/// ours), so a <c>{REFII_*}</c> notice is said in the culture in force on the machine that shows it.
/// </summary>
[TestSuite]
public class CoopItemWordsTests
{
	private static readonly string[] Keys = { "REFII_F2_COOP_ITEM_TO_PLAYER_ONE", "REFII_F2_COOP_ITEM_OWN_COPY" };

	[TestCase]
	public void EveryNoticeIsSaidInEveryLanguage()
	{
		foreach (string key in Keys)
		{
			AssertThat(Engine_OwnWords.Fable2.Has(key)).OverrideFailureMessage(key).IsTrue();
			string english = Engine_OwnWords.Fable2.Get(key, "en-uk") ?? string.Empty;
			foreach (string culture in Engine_OwnWords.Cultures)
			{
				string? line = Engine_OwnWords.Fable2.Get(key, culture);
				AssertThat(string.IsNullOrWhiteSpace(line)).OverrideFailureMessage($"{key} {culture}").IsFalse();
				if (culture != "en-uk")
				{
					AssertThat(line).OverrideFailureMessage($"{key} {culture} is the English").IsNotEqual(english);
				}
			}
		}
	}

	[TestCase]
	public void TheModTextChainEndsInOurOwnWords()
	{
		AssertThat(ModText.Find("REFII_PLAYER_1", null, ModText.Tables.Empty, "fr-fr")).IsEqual("Joueur 1");
		AssertThat(ModText.Find("REFII_PLAYER_1", key => key == "REFII_PLAYER_1" ? "Book" : null, ModText.Tables.Empty,
			"fr-fr")).IsEqual("Book");
		AssertThat(ModText.Find("REFII_NOT_A_KEY", null, ModText.Tables.Empty, "fr-fr")).IsNull();
		AssertThat(ModText.Resolve("{REFII_PLAYER_1}", key => ModText.Find(key, null, ModText.Tables.Empty, "de-de")))
			.IsEqual("Spieler 1");
	}
}
