namespace Refii.Tests.Save;

using System;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Save;

/// <summary>
/// The joining player's choice of the save their hero comes from: the question as the bridge states it,
/// anything else reads as none; the rows are the saves in the guest's order then the new hero; a row
/// answers its save's index or the new hero; the new hero is saved only while the profile has a slot.
/// </summary>
[TestSuite]
public class HeroSaveChoiceTests
{
	private const char Sep = '\x1F';

	private static string Record(params string[] fields) => string.Join(Sep, fields);

	[TestCase]
	public void TheQuestionReadsAsStated()
	{
		Engine_HeroSaveChoice.Question q = Engine_HeroSaveChoice.Parse(Record("7", "1", "4", "2", "Hero003", "Hero001"))!;
		AssertThat(q.Id).IsEqual(7u);
		AssertThat(q.User).IsEqual(1);
		AssertThat(q.Listed).IsEqual(4);
		AssertThat(string.Join(",", q.Saves)).IsEqual("Hero003,Hero001");
	}

	[TestCase]
	public void AnythingElseIsNoQuestion()
	{
		AssertThat(Engine_HeroSaveChoice.Parse(null)).IsNull();
		AssertThat(Engine_HeroSaveChoice.Parse(string.Empty)).IsNull();
		AssertThat(Engine_HeroSaveChoice.Parse(Record("7", "1", "4"))).IsNull();
		AssertThat(Engine_HeroSaveChoice.Parse(Record("7", "1", "4", "0"))).IsNull();
		AssertThat(Engine_HeroSaveChoice.Parse(Record("7", "1", "4", "2", "Hero003"))).IsNull();
		AssertThat(Engine_HeroSaveChoice.Parse(Record("7", "1", "4", "1", ""))).IsNull();
		AssertThat(Engine_HeroSaveChoice.Parse(Record("x", "1", "4", "1", "Hero003"))).IsNull();
		AssertThat(Engine_HeroSaveChoice.Parse(Record("7", "-1", "4", "1", "Hero003"))).IsNull();
	}

	[TestCase]
	public void TheRowsAreTheSavesInTheGuestsOrderThenTheNewHero()
	{
		Engine_HeroSaveChoice.Question q = Engine_HeroSaveChoice.Parse(Record("1", "1", "3", "2", "Hero003", "Hero001"))!;
		var written = new DateTime(2026, 11, 20, 18, 5, 0);
		var rows = Engine_HeroSaveChoice.Rows(q,
			save => save == "Hero003" ? new Engine_HeroSaveChoice.SaveFacts("Ella", written) : new Engine_HeroSaveChoice.SaveFacts(string.Empty, null),
			word => "<" + word + ">");
		AssertThat(rows.Count).IsEqual(3);
		AssertThat(rows[0].Save).IsEqual("Hero003");
		AssertThat(rows[0].Label).IsEqual("Ella");
		AssertThat(rows[0].Value).IsEqual("2026-11-20 18:05");
		AssertThat(rows[1].Label).IsEqual("Hero001");
		AssertThat(rows[1].Value).IsEqual(string.Empty);
		AssertThat(rows[2].Save).IsNull();
		AssertThat(rows[2].Label).IsEqual("<REFII_NEW_HERO>");
		AssertThat(rows[2].Description).IsEqual("<REFII_NEW_HERO_SAVED>");
	}

	[TestCase]
	public void ARowAnswersItsSaveOrTheNewHero()
	{
		Engine_HeroSaveChoice.Question q = Engine_HeroSaveChoice.Parse(Record("1", "1", "3", "2", "Hero003", "Hero001"))!;
		AssertThat(Engine_HeroSaveChoice.Answer(q, 0)).IsEqual(0);
		AssertThat(Engine_HeroSaveChoice.Answer(q, 1)).IsEqual(1);
		AssertThat(Engine_HeroSaveChoice.Answer(q, 2)).IsEqual(Engine_HeroSaveChoice.NewHero);
		AssertThat(Engine_HeroSaveChoice.Answer(q, 3)).IsNull();
		AssertThat(Engine_HeroSaveChoice.Answer(q, -1)).IsNull();
	}

	[TestCase]
	public void TheNewHeroIsSavedOnlyWhileTheProfileHasASlot()
	{
		AssertThat(Engine_HeroSaveChoice.NewHeroIsSaved(Engine_HeroSaveChoice.Parse(Record("1", "1", "4", "1", "A"))!)).IsTrue();
		AssertThat(Engine_HeroSaveChoice.NewHeroIsSaved(Engine_HeroSaveChoice.Parse(Record("1", "1", "5", "1", "A"))!)).IsFalse();
		var rows = Engine_HeroSaveChoice.Rows(Engine_HeroSaveChoice.Parse(Record("1", "1", "5", "1", "A"))!,
			_ => new Engine_HeroSaveChoice.SaveFacts(string.Empty, null), word => word);
		AssertThat(rows[1].Description).IsEqual("REFII_NEW_HERO_UNSAVED");
	}

	[TestCase]
	public void CancelIsTheNativeAnswerNoRowGives()
	{
		// native hero_save_choice.h: REFII_HERO_CHOICE_NEW_HERO -1, _NO_ANSWER -2, _CANCEL -3.
		AssertThat(Engine_HeroSaveChoice.Cancel).IsEqual(-3);
		Engine_HeroSaveChoice.Question q = Engine_HeroSaveChoice.Parse(Record("1", "1", "3", "2", "Hero003", "Hero001"))!;
		for (int row = -2; row <= 4; ++row)
		{
			AssertThat(Engine_HeroSaveChoice.Answer(q, row) == Engine_HeroSaveChoice.Cancel).IsFalse();
		}
	}

	[TestCase]
	public void TheHeadersOwnNameLabelsASaveWithNoSidecarName()
	{
		// Fable 3's Hero003.header: device 1, type 1, then "Hero 4" UTF-16 big-endian.
		var header = new byte[328];
		header[3] = 1;
		header[7] = 1;
		byte[] name = System.Text.Encoding.BigEndianUnicode.GetBytes("Hero 4");
		System.Array.Copy(name, 0, header, 8, name.Length);
		AssertThat(Engine_SaveHeader.DisplayName(header)).IsEqual("Hero 4");
		AssertThat(Engine_SaveHeader.DisplayName(new byte[8])).IsNull();
		AssertThat(Engine_SaveHeader.DisplayName(new byte[328])).IsNull();

		Engine_HeroSaveChoice.Question q = Engine_HeroSaveChoice.Parse(Record("1", "1", "3", "2", "Hero003", "Hero001"))!;
		var rows = Engine_HeroSaveChoice.Rows(q,
			save => save == "Hero003"
				? new Engine_HeroSaveChoice.SaveFacts(string.Empty, null, "Hero 4")
				: new Engine_HeroSaveChoice.SaveFacts("Ella", null, "Hero 2"),
			word => word);
		AssertThat(rows[0].Label).IsEqual("Hero 4");
		AssertThat(rows[0].Description).IsEqual("Hero003");
		AssertThat(rows[1].Label).IsEqual("Ella");
		AssertThat(rows[1].Description).IsEqual("Hero 2 (Hero001)");
	}
}
