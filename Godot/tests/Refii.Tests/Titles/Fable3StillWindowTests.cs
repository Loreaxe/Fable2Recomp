namespace Refii.Tests.Titles;

using GdUnit4;
using static GdUnit4.Assertions;
using Godot;
using Refii.Titles.Fable3;
using Refii.Ui;

/// <summary>
/// A still list's window fitted to its band (Engine_UiExpandableMenu.StillSlots on
/// Fable3ListScreen.ListBand): the window starts at the first slot under a tab strip and holds only
/// the slots above the description or the prompts, so no row of it, and never the cursor, stands
/// under what the template draws over the list. Slot heights are the shared slot table's on the
/// view's canvas anchor (int7d D5: Game Settings' first row stood in slot 2, under the tab strip).
/// </summary>
[TestSuite]
public class Fable3StillWindowTests
{
	/// <summary>Engine_FrontEndMenuView.CanvasAnchor's Y (the view class holds Godot statics; not loaded here).</summary>
	private const float AnchorY = 185f;

	private const float LineHeight = 24f;

	private static readonly int SlotCount = Engine_UiMenuConstants.Slots.Length;

	private static float Slot(int slot) => Engine_UiMenuConstants.Slots[slot - 1].Position.Y + AnchorY;

	private static (int TopSlot, int Rows) Fit(Rect2? band) =>
		Engine_UiExpandableMenu.StillSlots(Slot, SlotCount, band?.Position.Y, band?.End.Y);

	private static Rect2? Band(bool tabs, int lines, bool prompts) =>
		Fable3ListScreen.ListBand(tabs, lines, LineHeight, Slot(Engine_UiExpandableMenu.HighlightSlot), null, prompts);

	private static bool Inside(Rect2? band, float y) => band is not { } b || (y >= b.Position.Y && y <= b.End.Y);

	[TestCase]
	public void NoBandKeepsTenRowsFromTheTopSlot()
	{
		AssertThat(Fit(null) == (Engine_UiExpandableMenu.StillTopSlot, Engine_UiExpandableMenu.NumVisible)).IsTrue();
	}

	[TestCase]
	public void ATabbedPagesFirstRowStandsUnderTheStrip()
	{
		// Options > Game Settings: tabs, a one-line description, the prompts.
		Rect2? band = Band(true, 1, true);
		(int top, int rows) = Fit(band);
		AssertThat(top).IsEqual(3);
		for (int slot = top; slot < top + rows; ++slot)
		{
			AssertThat(Inside(band, Slot(slot))).IsTrue();
		}

		AssertThat(Inside(band, Slot(top - 1))).IsFalse();
		AssertThat(Inside(band, Slot(top + rows))).IsFalse();
		// Its five rows all stand in the window, the first in the top slot.
		AssertThat(rows >= 5).IsTrue();
	}

	[TestCase]
	public void ThePromptsEndAListWithNoDescription()
	{
		Rect2? band = Band(false, 0, true);
		AssertThat(band.HasValue).IsTrue();
		AssertThat(Inside(band, Slot(11))).IsTrue();
		AssertThat(Inside(band, Slot(12))).IsFalse();
		// The template's column keeps K07's ten rows from slot 2.
		AssertThat(Fit(band) == (2, 10)).IsTrue();
		// No prompts, no strip, no description: no band, as before.
		AssertThat(Band(false, 0, false).HasValue).IsFalse();
	}

	[TestCase]
	public void APitchedPanelHoldsEveryRowThatFitsAboveThePrompts()
	{
		// The treasury: rows 30 apart from canvas y 180 (its file's Personal / Treasury balances).
		const float pitch = 30f;
		float SlotAt(int slot) => 180f - ((slot - Engine_UiExpandableMenu.StillTopSlot) * pitch);
		Rect2? band = Fable3ListScreen.ListBand(false, 0, LineHeight, SlotAt(Engine_UiExpandableMenu.HighlightSlot), pitch, true);
		(int top, int rows) = Engine_UiExpandableMenu.StillSlots(SlotAt, SlotCount, band?.Position.Y, band?.End.Y);
		AssertThat(top).IsEqual(2);
		// Slots 2..12: eleven rows, the last at y −120, above the prompts.
		AssertThat(rows).IsEqual(11);
	}

	[TestCase]
	public void TheWindowMovesOnlyToTakeTheCursorInAtItsOwnSize()
	{
		AssertThat(Engine_UiExpandableMenu.StillTop(1, 7, 20, 7)).IsEqual(1);
		AssertThat(Engine_UiExpandableMenu.StillTop(1, 8, 20, 7)).IsEqual(2);
		AssertThat(Engine_UiExpandableMenu.StillTop(5, 3, 20, 7)).IsEqual(3);
		AssertThat(Engine_UiExpandableMenu.StillTop(15, 20, 20, 7)).IsEqual(14);
		// The ten-row form is the same rule.
		AssertThat(Engine_UiExpandableMenu.StillTop(1, 11, 20)).IsEqual(Engine_UiExpandableMenu.StillTop(1, 11, 20, 10));
		// The slot rule centred on StillCentre puts the window's top row in its top slot.
		const int top = 4;
		const int topSlot = 3;
		AssertThat(Engine_UiMenuFormatter.SlotFor(top, Engine_UiExpandableMenu.StillCentre(top, topSlot))).IsEqual(topSlot);
	}

	[TestCase]
	public void ABandNoSlotStandsInKeepsOneRow()
	{
		AssertThat(Engine_UiExpandableMenu.StillSlots(_ => 0f, SlotCount, 10f, 20f).Rows).IsEqual(1);
	}
}
