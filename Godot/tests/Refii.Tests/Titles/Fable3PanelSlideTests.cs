namespace Refii.Tests.Titles;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Ui;

/// <summary>
/// A panel is read where its page's slide stands it (Engine_UiTextBoxFit.SlideStated under
/// Fable3HudDocument.Authored / Fable3ScreenFrames.PanelOf): the slide's `position.x` / `position.y`
/// commands replace the file's on the elements they name, and the chain rule sums the rest as before.
/// Built on treasureaccounts.bgf's shape: `TreasureAccounts` → `Personal` (0, 0) → `Personal Balance`
/// (−334, 180); its "TransferFounds" slide writes `Personal` position (0, −90).
/// </summary>
[TestSuite]
public class Fable3PanelSlideTests
{
	private static BgfAttribute Float(string name, float value) =>
		new(Refii.Core.Engine_GameHash.AttributeId(name), BgfValueType.Float, System.BitConverter.SingleToUInt32Bits(value));

	private static (Engine_BgfDocument Doc, int Group, int Row) Treasury()
	{
		Engine_BgfDocument doc = Engine_BgfEdit.NewDocument("Scene", 1280, 720, true);
		int layer = Engine_BgfEdit.AddChild(doc, 0, "Layer");
		int root = Engine_BgfEdit.AddChild(doc, layer, "TreasureAccounts", 0f, 0f);
		int group = Engine_BgfEdit.AddChild(doc, root, "Personal", 0f, 0f);
		int row = Engine_BgfEdit.AddChild(doc, group, "Personal Balance", -334f, 180f);
		return (doc, group, row);
	}

	private static BgfSlide Moving(int element, params BgfAttribute[] commands) => new()
	{
		Name = "TransferFounds",
		Entries = new[] { new BgfSlideEntry { ElementOrdinal = element, Active = true, Commands = commands } },
	};

	[TestCase]
	public void ASlidesPositionWritesMoveTheRowsUnderIt()
	{
		(Engine_BgfDocument doc, int group, int row) = Treasury();
		AssertThat(Engine_UiTextBoxFit.ChainPosition(doc, row) == (-334f, 180f)).IsTrue();

		var stated = Engine_UiTextBoxFit.SlideStated(Moving(group, Float("position.x", 0f), Float("position.y", -90f)), null);
		AssertThat(Engine_UiTextBoxFit.ChainPosition(doc, row, stated) == (-334f, 90f)).IsTrue();
	}

	[TestCase]
	public void AWriteOfOneAxisKeepsTheOtherAndWhatIsStatedBeneath()
	{
		(Engine_BgfDocument doc, int group, int row) = Treasury();
		// A widescreen adapter states the row's x; the slide writes the group's y alone.
		System.Func<int, (float? X, float? Y)> wide = ordinal => ordinal == row ? (-300f, null) : default;
		var stated = Engine_UiTextBoxFit.SlideStated(Moving(group, Float("position.y", -90f)), wide);
		AssertThat(stated(group) == ((float?)null, (float?)-90f)).IsTrue();
		AssertThat(stated(row) == ((float?)-300f, (float?)null)).IsTrue();
		AssertThat(Engine_UiTextBoxFit.ChainPosition(doc, row, stated) == (-300f, 90f)).IsTrue();
	}

	[TestCase]
	public void NoSlideStatesNothingNew()
	{
		(Engine_BgfDocument doc, _, int row) = Treasury();
		AssertThat(Engine_UiTextBoxFit.ChainPosition(doc, row, Engine_UiTextBoxFit.SlideStated(null, null)) == (-334f, 180f)).IsTrue();
	}
}
