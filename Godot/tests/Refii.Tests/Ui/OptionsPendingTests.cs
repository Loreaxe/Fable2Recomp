namespace Refii.Tests.Ui;

using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Host;
using Refii.Ui;
using Refii.Titles.Fable2;

/// <summary>
/// The options page level. A GUEST page's cursor and values are the guest's, as its row widgets
/// state them, and never stepped by the host; a HOST page's record
/// is the host's. The pad's codes to a guest page are held through the page's quiet half second
/// and each 28..31 goes out with its release. Pure model code — no Godot type is built.
/// </summary>
[TestSuite]
public class OptionsPendingTests
{
	/// <summary>Records what the level posts to the guest.</summary>
	private sealed class RecordingSource : Engine_UiSource
	{
		public readonly List<uint> Codes = new();

		public override uint ScreenAction(uint code)
		{
			Codes.Add(code);
			return 1;
		}
	}

	/// <summary>A two-row host page with three choices per row.</summary>
	private sealed class TwoRowPage : Engine_UiHostOptionsPage
	{
		public override string Name => "TwoRows";

		public override string Title => "Two rows";

		public override int Count => 2;

		public override string RowTitle(int row) => $"row{row}";

		public override int Choices(int row) => 3;

		public override string Caption(int row, int index) => $"{row}:{index}";

		public override int[] Read() => new[] { 0, 0 };
	}

	private const string Pages = "Foreground.Book.Pages.";

	private static void ClearStated()
	{
		Engine_UiPickBoxes.Clear();
		Engine_UiRowWidgets.Clear();
	}

	[TestCase]
	public void GuestPageTakesItsCursorAndIndicesFromTheGuestsStatementsAndTrackStepsNothing()
	{
		Engine_UiOptionsPending.Leave();
		ClearStated();
		try
		{
			Engine_UiOptionsPending.Enter("Game");
			AssertThat(Engine_UiOptionsPending.Open).IsTrue();
			// Nothing stated yet: no caption is painted from a guess, and the cursor reads as row 0.
			AssertThat(Engine_UiOptionsPending.IndexOf(0)).IsEqual(-1);
			AssertThat(Engine_UiOptionsPending.Row).IsEqual(0);

			const string options = Pages + "Page1.Contents.Options.";
			Engine_UiRowWidgets.StateSelected(options + "Subtitles_pickbox", true);
			Engine_UiPickBoxes.State(options + "BreadcrumbSize_pickbox.grp.text", 4, "Very high");
			Engine_UiPickBoxes.State(options + "MultiplayerOrbs_pickbox.grp.text", 2, "All");
			// The row selector clears the old row's widget and sets the new one's.
			Engine_UiRowWidgets.StateSelected(options + "Subtitles_pickbox", false);
			Engine_UiRowWidgets.StateSelected(options + "MultiplayerOrbs_pickbox", true);
			AssertThat(Engine_UiOptionsPending.Row).IsEqual(3);
			AssertThat(Engine_UiOptionsPending.IndexOf(1)).IsEqual(4);
			AssertThat(Engine_UiOptionsPending.IndexOf(3)).IsEqual(2);
			AssertThat(Engine_UiOptionsPending.SliderValue(0)).IsNull();

			// The guest's record is the only one; a code sent to it moves nothing here.
			Engine_UiOptionsPending.Track(Engine_UiScreenInput.Left);
			Engine_UiOptionsPending.Track(Engine_UiScreenInput.Down);
			Engine_UiOptionsPending.Track(Engine_UiScreenInput.Select);
			AssertThat(Engine_UiOptionsPending.Row).IsEqual(3);
			AssertThat(Engine_UiOptionsPending.IndexOf(3)).IsEqual(2);
			AssertThat(Engine_UiOptionsPending.Open).IsTrue();

			// A re-populated page builds its widgets again: a new widget is unselected.
			Engine_UiRowWidgets.StateInit(options + "MultiplayerOrbs_pickbox");
			AssertThat(Engine_UiOptionsPending.Row).IsEqual(0);
		}
		finally
		{
			Engine_UiOptionsPending.Leave();
			ClearStated();
		}
	}

	[TestCase]
	public void AudioPageTakesTheSliderWidgetsStatedValuesOverTheCommittedLevels()
	{
		Engine_UiOptionsPending.Leave();
		ClearStated();
		try
		{
			Engine_UiOptionsPending.Enter("Audio");
			const string contents = Pages + "Page4.Contents.";
			Engine_UiRowWidgets.StateValue(contents + "slider1", 0.25f);
			Engine_UiRowWidgets.StateValue(contents + "slider2", 0.5f);
			Engine_UiRowWidgets.StateValue(contents + "slider3", 0.75f);
			Engine_UiPickBoxes.State(contents + "pickbox1.grp.text", 2, "Surround");
			Engine_UiRowWidgets.StateSelected(contents + "slider2", true);
			AssertThat(Engine_UiOptionsPending.Row).IsEqual(1);
			AssertThat(Engine_UiOptionsPending.SliderValue(0)).IsEqual(0.25f);
			AssertThat(Engine_UiOptionsPending.SliderValue(3)).IsNull();
			AssertThat(Engine_UiOptionsPending.IndexOf(0)).IsEqual(-1);
			AssertThat(Engine_UiOptionsPending.IndexOf(3)).IsEqual(2);

			var committed = new HostBridge.OptionSliders(true, 0.4f, 1f, 1f, 1f, 0);
			HostBridge.OptionSliders live = Engine_UiOptionsPending.Pending(committed);
			AssertThat(live.Sfx).IsEqual(0.25f);
			AssertThat(live.Music).IsEqual(0.5f);
			AssertThat(live.Speech).IsEqual(0.75f);
			AssertThat(live.Gamma).IsEqual(0.4f);
		}
		finally
		{
			Engine_UiOptionsPending.Leave();
			ClearStated();
		}
	}

	[TestCase]
	public void AnEmptyPageNameIsRefused()
	{
		Engine_UiOptionsPending.Leave();
		try
		{
			Engine_UiOptionsPending.Enter(string.Empty);
			AssertThat(Engine_UiOptionsPending.Open).IsFalse();
			Engine_UiOptionsPending.Enter("Game");
			Engine_UiOptionsPending.Enter(string.Empty);
			AssertThat(Engine_UiOptionsPending.Page).IsEqual("Game");
		}
		finally
		{
			Engine_UiOptionsPending.Leave();
		}
	}

	[TestCase]
	public void HostPageStillStepsItsOwnRecord()
	{
		Engine_UiOptionsPending.Leave();
		try
		{
			Engine_UiOptionsPending.EnterHost(new TwoRowPage());
			Engine_UiOptionsPending.Track(Engine_UiScreenInput.Right);
			AssertThat(Engine_UiOptionsPending.IndexOf(0)).IsEqual(1);
			Engine_UiOptionsPending.Track(Engine_UiScreenInput.Down);
			AssertThat(Engine_UiOptionsPending.Row).IsEqual(1);
			Engine_UiOptionsPending.Track(Engine_UiScreenInput.Left);
			AssertThat(Engine_UiOptionsPending.IndexOf(1)).IsEqual(0);
			Engine_UiOptionsPending.Track(Engine_UiScreenInput.Select);
			AssertThat(Engine_UiOptionsPending.Open).IsFalse();
		}
		finally
		{
			Engine_UiOptionsPending.Leave();
		}
	}

	[TestCase]
	public void PadCodesAreHeldThroughTheQuietWindowThenSentWithTheirReleases()
	{
		Engine_UiSource before = Engine_UiSource.Current;
		var source = new RecordingSource();
		Engine_UiSource.Current = source;
		Engine_UiOptionsPending.Leave();
		try
		{
			Engine_UiOptionsPending.Enter("Game");
			var level = new Engine_UiScreenLevel();

			// Down at 0.1 s and A at 0.2 s: both inside the guest's half second, neither posted.
			level.Drive(new Engine_UiSurfaceInput { Direction = 1, Delta = 0.1 });
			level.Drive(new Engine_UiSurfaceInput { Accept = true, Delta = 0.1 });
			AssertThat(source.Codes).IsEmpty();

			// The window ends: the held codes go out in order, the press with its release.
			level.Drive(new Engine_UiSurfaceInput { Delta = 0.5 });
			AssertThat(source.Codes).ContainsExactly(
				Engine_UiScreenInput.Down, Engine_UiScreenInput.DownReleased, Engine_UiScreenInput.Select);

			// Past the window a tap is posted at once, still paired with its release.
			source.Codes.Clear();
			level.Drive(new Engine_UiSurfaceInput { Horizontal = -1, Delta = 0.1 });
			AssertThat(source.Codes).ContainsExactly(
				Engine_UiScreenInput.Left, Engine_UiScreenInput.LeftReleased);
		}
		finally
		{
			Engine_UiSource.Current = before;
			Engine_UiOptionsPending.Leave();
		}
	}
}
