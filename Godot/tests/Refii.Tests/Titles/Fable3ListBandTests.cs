namespace Refii.Tests.Titles;

using GdUnit4;
using static GdUnit4.Assertions;
using Godot;
using Refii.Titles.Fable3;
using Refii.Ui;

/// <summary>
/// The band Fable 3's list template states to the shared view (Fable3ListScreen.ListBand): the
/// carousel's rows stand clear of the tab strip above and the description panel below, the cursor
/// row always inside. Slot heights are the shared slot table's, on the view's canvas anchor.
/// </summary>
[TestSuite]
public class Fable3ListBandTests
{
	/// <summary>Engine_FrontEndMenuView.CanvasAnchor's Y (the view class itself is not loaded here: it holds Godot statics).</summary>
	private const float AnchorY = 185f;

	private const float LineHeight = 24f;

	/// <summary>The carousel's cursor slot (Engine_UiExpandableMenu.HighlightSlot).</summary>
	private const int Cursor = Engine_UiExpandableMenu.HighlightSlot;

	/// <summary>The canvas Y of 1-based carousel slot <paramref name="slot"/>.</summary>
	private static float Slot(int slot) => Engine_UiMenuConstants.Slots[slot - 1].Position.Y + AnchorY;

	private static bool Inside(Rect2? band, float y) => band is not { } b || (y >= b.Position.Y && y <= b.End.Y);

	[TestCase]
	public void NoTabsNoDescriptionIsNoBand()
	{
		AssertThat(Fable3ListScreen.ListBand(false, 0, LineHeight, Slot(Cursor)).HasValue).IsFalse();
	}

	[TestCase]
	public void ADescriptionEndsTheListAboveItsPanel()
	{
		Rect2? one = Fable3ListScreen.ListBand(false, 1, LineHeight, Slot(Cursor));
		AssertThat(one.HasValue).IsTrue();
		AssertThat(Inside(one, Slot(9))).IsTrue();
		AssertThat(Inside(one, Slot(10))).IsFalse();
		AssertThat(Inside(one, Slot(11))).IsFalse();

		Rect2? three = Fable3ListScreen.ListBand(false, 3, LineHeight, Slot(Cursor));
		AssertThat(Inside(three, Slot(8))).IsTrue();
		AssertThat(Inside(three, Slot(9))).IsFalse();

		// Nothing above is cut without a tab strip.
		AssertThat(Inside(one, Slot(1))).IsTrue();
	}

	[TestCase]
	public void TabsEndTheListUnderTheStrip()
	{
		Rect2? band = Fable3ListScreen.ListBand(true, 0, LineHeight, Slot(Cursor));
		AssertThat(band.HasValue).IsTrue();
		AssertThat(Inside(band, Slot(1))).IsFalse();
		AssertThat(Inside(band, Slot(2))).IsFalse();
		AssertThat(Inside(band, Slot(3))).IsTrue();
		AssertThat(Inside(band, Slot(11))).IsTrue();
	}

	[TestCase]
	public void TheCursorSlotIsAlwaysInside()
	{
		for (int lines = 0; lines <= 30; ++lines)
		{
			AssertThat(Inside(Fable3ListScreen.ListBand(false, lines, LineHeight, Slot(Cursor)), Slot(4))).IsTrue();
			AssertThat(Inside(Fable3ListScreen.ListBand(true, lines, LineHeight, Slot(Cursor)), Slot(4))).IsTrue();
		}
	}

	[TestCase]
	public void ATallerDescriptionCutsMore()
	{
		float last = float.NegativeInfinity;
		for (int lines = 1; lines <= 6; ++lines)
		{
			// Up to the cursor's row, which it never cuts.
			float bottom = Fable3ListScreen.ListBand(false, lines, LineHeight, Slot(Cursor))!.Value.Position.Y;
			AssertThat(bottom > last).IsTrue();
			last = bottom;
		}
	}
}
