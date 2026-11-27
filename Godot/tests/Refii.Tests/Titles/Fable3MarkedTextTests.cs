namespace Refii.Tests.Titles;

using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Ui;

/// <summary>
/// The shop page's texts are the guest's and carry the guest's markup: a price's detail is the number
/// with the guest's own `&lt;gold_img&gt;` appended (TU3 sub_8295B980 0x8295BF64..0x8295BFA4, disc
/// sub_82B2EA50 0x82B2F034..0x82B2F074; the string L"&lt;gold_img&gt;" at TU3 0x82098F58 / disc
/// 0x82095748), which Fable 3's markup splits into the number and the coin icon — drawn as the coin
/// (Engine_UiCanvasText.DrawMarked), never as its letters (int7d D7: "19&lt;gold_img&gt;").
/// </summary>
[TestSuite]
public class Fable3MarkedTextTests
{
	[TestCase]
	public void APricesGoldTagIsTheCoinIcon()
	{
		var runs = Engine_UiTextMarkup.Vocabulary.Fable3.Split("19<gold_img>");
		AssertThat(runs.Count).IsEqual(2);
		AssertThat(runs[0].Text).IsEqual("19");
		AssertThat(runs[0].Tag.HasValue).IsFalse();
		AssertThat(runs[1].Tag.HasValue).IsTrue();
		AssertThat(runs[1].Tag!.Value.Name).IsEqual("gold_img");
		AssertThat(runs[1].Tag!.Value.Kind).IsEqual(Engine_UiTextMarkup.Kind.Icon);
		AssertThat(runs[1].Tag!.Value.Texture.EndsWith("CurrentcySmall", System.StringComparison.Ordinal)).IsTrue();
		// No run carries the tag's letters.
		AssertThat(runs.Any(run => run.Text.Contains('<') || run.Text.Contains("gold", System.StringComparison.Ordinal))).IsFalse();
	}

	[TestCase]
	public void TheIconAdvancesByTheTitlesPenAdvance()
	{
		// Fable 3 advances an icon 26 and draws it 32 a side, 6 left of and above the pen.
		AssertThat(Engine_UiTextContract.Fable3.IconPenAdvance(false, 24)).IsEqual(26f);
		AssertThat(Engine_UiTextContract.Fable3.IconSize(false, 24) == (32f, 32f)).IsTrue();
		AssertThat(Engine_UiTextContract.Fable3.IconInset).IsEqual(6f);
	}
}
