namespace Refii.Tests.Ui;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Core;
using Refii.Ui;

/// <summary>
/// The GUI detail-map rule, one for both titles: mode 4 alone asks (Fable 2 TU1 0x8286E4B0, Fable 3
/// TU3 0x82B7804C, disc 0x82B42E2C), and the slot picks from the theme table's row 0, built with
/// the same names on both (Fable 2 sub_826DB1D8, Fable 3 TU3 sub_825EDA28, disc sub_825BFC40).
/// </summary>
[TestSuite]
public class EngineUiDetailMapsTests
{
	[TestCase]
	public void OnlyModeFourAsks()
	{
		AssertThat(Engine_UiDetailMaps.Asks(4)).IsTrue();
		foreach (int mode in new[] { 0, 1, 2, 3, 5, -1 })
		{
			AssertThat(Engine_UiDetailMaps.Asks(mode)).IsFalse();
		}
	}

	/// <summary>Slot 1 word 1, slot 2 word 2, slot 3 by name, any other slot word 0.</summary>
	[TestCase]
	public void TheSlotPicksFromRowZero()
	{
		AssertThat(Engine_UiDetailMaps.PathFor(1)).IsEqual(@"art\gui\frames\buttondetailmap.tex");
		AssertThat(Engine_UiDetailMaps.PathFor(2)).IsEqual(@"art\gui\frames\frames_01.tex");
		AssertThat(Engine_UiDetailMaps.PathFor(3)).IsEqual(@"art\gui\persistant\frame_tiled_background.tex");
		foreach (int slot in new[] { 0, 4, 7, -1 })
		{
			AssertThat(Engine_UiDetailMaps.PathFor(slot)).IsEqual(@"art\gui\frames\framedetailmap.tex");
		}
	}

	/// <summary>
	/// The names are already lower-case resource paths, so they hash as the banks list them (the
	/// texture table keys on Engine_GameHash.ResourcePath of the lower-cased path).
	/// </summary>
	[TestCase]
	public void ThePathsAreTheBanksOwnNames()
	{
		foreach (int slot in new[] { 0, 1, 2, 3 })
		{
			string path = Engine_UiDetailMaps.PathFor(slot);
			AssertThat(path).IsEqual(path.ToLowerInvariant());
			AssertThat(Engine_GameHash.ResourcePath(path)).IsEqual(Engine_GameHash.ResourcePath(path.ToUpperInvariant()));
		}
	}

	/// <summary>
	/// The hash Fable 3's `interactionmode.fac` gives all four `RulerInfoBox.Background.Selection`
	/// quads (0xA1DC859D) is Fable 2's frame atlas, and the box documents' bar pieces hash to the
	/// bar's own art: why the list cursor takes its art from a box (Fable3ListCursor.Document).
	/// </summary>
	[TestCase]
	public void TheInteractionSelectionNamesTheFrameAtlas()
	{
		AssertThat(Engine_GameHash.ResourcePath(@"art\gui\frames\frames_04.tex")).IsEqual(0xA1DC859Du);
		AssertThat(Engine_GameHash.ResourcePath(
			@"art\gui_memmount\hudassests\version4\middleelements\options\selectionbar_background.tex"))
			.IsNotEqual(0xA1DC859Du);
	}
}
