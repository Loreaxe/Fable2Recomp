namespace Refii.Tests.Titles;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable3;
using Refii.Ui;

/// <summary>
/// The panel rule of Fable 3's one list template (Fable3ListPanel): a box screen's rows, marks and
/// description stand on the panel its own .bgf authors, and a list with no panel keeps the
/// template's own column (the Options settings page). The panels below are the files' own numbers
/// (Fable3ScreenFrames.Frames' remarks): hero status and the king's schedule.
/// </summary>
[TestSuite]
public class Fable3ListPanelTests
{
	/// <summary>herostatus.bgf: frame x 275 (the widescreen adapter's), Strength at (5, 160) halign 0, Skill 30 below, the meters' zero at 257.</summary>
	private static readonly Fable3ListPanel HeroStatus = new(275f, 5f, 0, 160f, 30f, 257f);

	/// <summary>kingsschedule.bgf: paper x 0, Task1 at (−140, 189) halign 0, Task2 102 below, the tick boxes at −190.</summary>
	private static readonly Fable3ListPanel Schedule = new(0f, -140f, 0, 189f, 102f, -190f);

	/// <summary>joinoptions.bgf: `SelectionTitles.*` halign 1 at x 78, box x 0.</summary>
	private static readonly Fable3ListPanel Choices = new(0f, 78f, 1, 42f);

	/// <summary>The carousel's cursor slot height, from the shared slot table (the view class holds Godot statics).</summary>
	private static float HighlightSlotY => Engine_UiMenuConstants.Slots[Engine_UiExpandableMenu.HighlightSlot - 1].Position.Y;

	/// <summary>The view's slot height rule under a stated pitch (Engine_FrontEndMenuView.SlotY), restated over the table.</summary>
	private static float SlotY(int slot, float pitch) => HighlightSlotY - ((slot - Engine_UiExpandableMenu.HighlightSlot) * pitch);

	[TestCase]
	public void LeftRowsMirrorTheirInsetForTheRightEdge()
	{
		AssertThat(HeroStatus.Centred).IsFalse();
		AssertThat(HeroStatus.Right!.Value).IsEqual(545f);
		AssertThat(HeroStatus.InnerWidth!.Value).IsEqual(540f);
		AssertThat(Schedule.Right!.Value).IsEqual(140f);
		AssertThat(Choices.Centred).IsTrue();
		AssertThat(Choices.Right.HasValue).IsFalse();
	}

	[TestCase]
	public void BarsStartAtTheValueColumnAndEndInsideThePanel()
	{
		(float left, float right) = Fable3ListPanel.Slider(HeroStatus);
		AssertThat(left).IsEqual(257f);
		AssertThat(right).IsEqual(545f - Fable3ListPanel.PercentRoom);

		// No panel: the template's own span, as Options' settings page has always drawn it.
		AssertThat(Fable3ListPanel.Slider(null) == (Fable3ListPanel.DefaultSliderLeft, Fable3ListPanel.DefaultSliderRight)).IsTrue();
		AssertThat(Fable3ListScreen.SliderFraction(Fable3ListPanel.DefaultSliderLeft)).IsEqual(0f);
		AssertThat(Fable3ListPanel.Fraction(377f, left, right)).IsEqualApprox(0.5f, 0.001f);
		AssertThat(Fable3ListPanel.Fraction(0f, left, right)).IsEqual(0f);
	}

	[TestCase]
	public void PipsStandBeforeTheLabelWhereTheFileDoesElsePastIt()
	{
		// The schedule's tick, authored left of its task text, stays there.
		AssertThat(Fable3ListPanel.PipLeft(Schedule, -140f, -80f, 12f, 12f)).IsEqual(-190f);

		// Hero status: the value column, or past a label that runs into it.
		AssertThat(Fable3ListPanel.PipLeft(HeroStatus, 5f, 120f, 12f, 96f)).IsEqual(257f);
		AssertThat(Fable3ListPanel.PipLeft(HeroStatus, 5f, 300f, 12f, 96f)).IsEqual(312f);

		// A centred choice states no value column: the pip follows its label.
		AssertThat(Fable3ListPanel.PipLeft(Choices, 33f, 123f, 12f, 12f)).IsEqual(135f);

		// No panel: the template's rule unchanged (its column, or past the label).
		AssertThat(Fable3ListPanel.PipLeft(null, -390f, -250f, 12f, 96f)).IsEqual(Fable3ListPanel.DefaultSliderLeft);
		AssertThat(Fable3ListPanel.PipLeft(null, -390f, 100f, 12f, 96f)).IsEqual(112f);
	}

	[TestCase]
	public void APanelsRowsStandWhereTheFileDoesCursorOrNot()
	{
		// The window's top row rests on the still list's top slot: the view moves it onto the file's
		// first row, and row n (slot StillTopSlot + n − 1) stands n − 1 authored pitches below it.
		int top = Engine_UiExpandableMenu.StillTopSlot;
		(float ViewY, float? Pitch)? rows = Fable3ListPanel.Rows(HeroStatus, pitch => SlotY(top, pitch ?? 0f));
		AssertThat(rows.HasValue).IsTrue();
		float step = rows!.Value.Pitch!.Value;
		AssertThat(rows.Value.ViewY + SlotY(top, step)).IsEqualApprox(160f, 0.001f);
		AssertThat(rows.Value.ViewY + SlotY(top + 1, step)).IsEqualApprox(130f, 0.001f);
		AssertThat(rows.Value.ViewY + SlotY(top + 7, step)).IsEqualApprox(-50f, 0.001f);

		(float ViewY, float? Pitch)? schedule = Fable3ListPanel.Rows(Schedule, pitch => SlotY(top, pitch ?? 0f));
		AssertThat(schedule!.Value.ViewY + SlotY(top, schedule.Value.Pitch!.Value)).IsEqualApprox(189f, 0.001f);
		AssertThat(schedule.Value.ViewY + SlotY(top + 3, schedule.Value.Pitch!.Value)).IsEqualApprox(-117f, 0.001f);

		// A first row with no pitch keeps the table's spacing; no first row, or no panel, keeps the column.
		(float ViewY, float? Pitch)? choices = Fable3ListPanel.Rows(Choices, pitch => pitch is null ? 100f : 0f);
		AssertThat(choices!.Value.Pitch.HasValue).IsFalse();
		AssertThat(choices.Value.ViewY).IsEqualApprox(42f - 100f, 0.001f);
		AssertThat(Fable3ListPanel.Rows(new Fable3ListPanel(0f, 0f, 0), _ => 0f).HasValue).IsFalse();
		AssertThat(Fable3ListPanel.Rows(null, _ => 0f).HasValue).IsFalse();
	}

	[TestCase]
	public void AStillListsWindowMovesOnlyToTakeTheCursorIn()
	{
		int n = Engine_UiExpandableMenu.NumVisible;
		// A list that fits never moves, wherever the cursor goes or comes back to.
		AssertThat(Engine_UiExpandableMenu.StillTop(1, 1, 6)).IsEqual(1);
		AssertThat(Engine_UiExpandableMenu.StillTop(1, 6, 6)).IsEqual(1);
		AssertThat(Engine_UiExpandableMenu.StillTop(1, 3, 6)).IsEqual(1);
		// A long list: inside the window nothing moves; past its foot it moves one row; above its head, to the cursor.
		AssertThat(Engine_UiExpandableMenu.StillTop(1, n, 20)).IsEqual(1);
		AssertThat(Engine_UiExpandableMenu.StillTop(1, n + 1, 20)).IsEqual(2);
		AssertThat(Engine_UiExpandableMenu.StillTop(5, 7, 20)).IsEqual(5);
		AssertThat(Engine_UiExpandableMenu.StillTop(5, 3, 20)).IsEqual(3);
		// Never past the last row (the list shrank under it).
		AssertThat(Engine_UiExpandableMenu.StillTop(15, 12, 12)).IsEqual(System.Math.Max(1, 12 - n + 1));
		// The top row stands in the top slot: the slot rule centred on StillCentre puts it there.
		int top = 4;
		// (the view's slot rule, Engine_UiMenuFormatter.SlotFor: display − centre + HighlightSlot).
		AssertThat(top - Engine_UiExpandableMenu.StillCentre(top) + Engine_UiExpandableMenu.HighlightSlot)
			.IsEqual(Engine_UiExpandableMenu.StillTopSlot);
		(int first, int last) = Engine_UiExpandableMenu.Window(Engine_UiExpandableMenu.StillCentre(top), 20);
		AssertThat(first).IsEqual(top);
		AssertThat(last).IsEqual(top + n - 1);
	}

	[TestCase]
	public void TheDescriptionStandsInsideThePanel()
	{
		AssertThat(Fable3ListPanel.Description(Schedule, 720f) == (0f, 280f)).IsTrue();
		AssertThat(Fable3ListPanel.Description(HeroStatus, 720f) == (275f, 540f)).IsTrue();
		AssertThat(Fable3ListPanel.Description(Choices, 720f) == (0f, 720f)).IsTrue();
		AssertThat(Fable3ListPanel.Description(null, 720f) == (0f, 720f)).IsTrue();
	}

	/// <summary>
	/// Every frame that states rows names its own document's elements under its own root, and the two
	/// pages without a cursor that F6 found off their panels state their pitch and value column.
	/// </summary>
	[TestCase]
	public void FramesStateTheirRowsUnderTheirOwnRoot()
	{
		foreach (Fable3ScreenFrames.Frame frame in Fable3ScreenFrames.Frames)
		{
			if (frame.Rows is not { } rows)
			{
				continue;
			}

			AssertThat(rows.First.StartsWith(frame.Root + ".", System.StringComparison.Ordinal)).IsTrue();
			AssertThat(rows.Second is null || rows.Second.StartsWith(frame.Root + ".", System.StringComparison.Ordinal)).IsTrue();
			AssertThat(rows.Values is null || rows.Values.StartsWith(frame.Root + ".", System.StringComparison.Ordinal)).IsTrue();
		}

		foreach (string root in new[] { Fable3ScreenModel.HeroStatus, Fable3ScreenModel.Schedule })
		{
			Fable3ScreenFrames.Column rows = Fable3ScreenFrames.FrameOf(root)!.Value.Rows!.Value;
			AssertThat(rows.Second).IsNotNull();
			AssertThat(rows.Values).IsNotNull();
		}

		// Stats' pages stand their rows in different columns: it keeps the template's.
		AssertThat(Fable3ScreenFrames.FrameOf(Fable3ScreenModel.Stats)!.Value.Rows.HasValue).IsFalse();
	}
}
