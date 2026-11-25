namespace Refii.Tests.Ui;

using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Host;
using Refii.Ui;
using Refii.Titles.Fable2;

/// <summary>
/// The options page level: Godot's record, seeded from what the guest's page readers state, stepped
/// with the guest widgets' arithmetic, and committed as `Options:` levers that run the guest's
/// setters. Pure model code — no Godot type is built.
/// </summary>
[TestSuite]
public class OptionsPendingTests
{
	/// <summary>Records what the page posts to the guest.</summary>
	private sealed class RecordingSource : Engine_UiSource
	{
		public readonly List<uint> Codes = new();

		public readonly List<string> Levers = new();

		public override uint ScreenAction(uint code)
		{
			Codes.Add(code);
			return 1;
		}

		public override uint PostFrontEndAction(string name)
		{
			Levers.Add(name);
			return 1;
		}
	}

	private static readonly HostBridge.OptionSliders Committed = new(true, 0.55f, 1f, 1f, 0.9f, 2);

	/// <summary>Runs <paramref name="body"/> with a recording source and a clean record.</summary>
	private static void WithSource(System.Action<RecordingSource> body)
	{
		Engine_UiSource before = Engine_UiSource.Current;
		var source = new RecordingSource();
		Engine_UiSource.Current = source;
		Engine_UiOptionsPending.Leave();
		ClearStated();
		try
		{
			body(source);
		}
		finally
		{
			Engine_UiSource.Current = before;
			Engine_UiOptionsPending.Leave();
			ClearStated();
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
	public void GamePageSeedsFromTheStatedIndicesAndCommitsEveryRow()
	{
		WithSource(source =>
		{
			const string options = Pages + "Page1.Contents.Options.";
			Engine_UiPickBoxes.State(options + "Subtitles_pickbox.grp.text", 0, "Off");
			Engine_UiPickBoxes.State(options + "BreadcrumbSize_pickbox.grp.text", 4, "Very high");
			Engine_UiPickBoxes.State(options + "TutorialBoxes_pickbox.grp.text", 1, "On");
			Engine_UiPickBoxes.State(options + "MultiplayerOrbs_pickbox.grp.text", 2, "All");
			Engine_UiPickBoxes.State(options + "Auto_joinable_pickbox.grp.text", 1, "On");

			AssertThat(Engine_UiOptionsPending.Enter("Game", Committed)).IsTrue();
			AssertThat(Engine_UiOptionsPending.IndexOf(1)).IsEqual(4);
			AssertThat(Engine_UiOptionsPending.SliderValue(0)).IsNull();

			// The breadcrumb row does not wrap; the orbs row does. Nothing is posted until A.
			Engine_UiOptionsPending.Track(Engine_UiScreenInput.Down);
			Engine_UiOptionsPending.Track(Engine_UiScreenInput.Right);
			AssertThat(Engine_UiOptionsPending.IndexOf(1)).IsEqual(4);
			Engine_UiOptionsPending.Track(Engine_UiScreenInput.Down);
			Engine_UiOptionsPending.Track(Engine_UiScreenInput.Down);
			Engine_UiOptionsPending.Track(Engine_UiScreenInput.Right);
			AssertThat(Engine_UiOptionsPending.IndexOf(3)).IsEqual(0);
			for (int i = 0; i < 4; ++i)
			{
				Engine_UiOptionsPending.Track(Engine_UiScreenInput.Down);
			}

			AssertThat(Engine_UiOptionsPending.Row).IsEqual(4);
			AssertThat(source.Levers).IsEmpty();

			Engine_UiOptionsPending.Track(Engine_UiScreenInput.Select);
			AssertThat(source.Levers).ContainsExactly(
				"Options:1:subtitles:0", "Options:1:breadcrumbs:4", "Options:1:tutorials:1",
				"Options:1:orbs:0", "Options:1:autojoin:1");
			AssertThat(Engine_UiOptionsPending.Open).IsFalse();
		});
	}

	[TestCase]
	public void PlayerTwosGamePageIsTheSubtitleRowAlone()
	{
		WithSource(source =>
		{
			Engine_UiPickBoxes.State(Pages + "Page1.Contents.Options.Subtitles_pickbox.grp.text", 1, "On");
			Engine_UiOptionsPending.Enter("Game", Committed, playerTwo: true);
			Engine_UiOptionsPending.Track(Engine_UiScreenInput.Down);
			AssertThat(Engine_UiOptionsPending.Row).IsEqual(0);
			Engine_UiOptionsPending.Track(Engine_UiScreenInput.Right);
			Engine_UiOptionsPending.Track(Engine_UiScreenInput.Select);
			AssertThat(source.Levers).ContainsExactly("Options:1:subtitles:0");
		});
	}

	[TestCase]
	public void AudioPageAppliesLiveCommitsTheProfileAndLeavesLevelsOnCancel()
	{
		WithSource(source =>
		{
			const string contents = Pages + "Page4.Contents.";
			Engine_UiRowWidgets.StateValue(contents + "slider1", 1f);
			Engine_UiRowWidgets.StateValue(contents + "slider2", 0.5f);
			Engine_UiRowWidgets.StateValue(contents + "slider3", 0.9f);
			Engine_UiPickBoxes.State(contents + "pickbox1.grp.text", 2, "Surround");

			Engine_UiOptionsPending.Enter("Audio", Committed);
			AssertThat(Engine_UiOptionsPending.SliderValue(1)).IsEqual(0.5f);

			// The slider clamps at 1: no step, no lever.
			Engine_UiOptionsPending.Track(Engine_UiScreenInput.Right);
			AssertThat(source.Levers).IsEmpty();
			Engine_UiOptionsPending.Track(Engine_UiScreenInput.Left);
			AssertThat(source.Levers).ContainsExactly("Options:1:sfx:0.95");

			// The speaker row wraps 2 -> 0 and applies at once.
			for (int i = 0; i < 3; ++i)
			{
				Engine_UiOptionsPending.Track(Engine_UiScreenInput.Down);
			}

			Engine_UiOptionsPending.Track(Engine_UiScreenInput.Right);
			AssertThat(Engine_UiOptionsPending.IndexOf(3)).IsEqual(0);
			AssertThat(Engine_UiOptionsPending.Pending(Committed).Speaker).IsEqual(0);
			AssertThat(source.Levers[^1]).IsEqual("Options:1:speaker:0");

			Engine_UiOptionsPending.Track(Engine_UiScreenInput.Select);
			AssertThat(source.Levers[^1]).IsEqual("Options:1:audio:0.95:0.5:0.9:0");

			// B posts nothing: the live levels stay where the last step put them (0x829C16FC).
			Engine_UiOptionsPending.Enter("Audio", Committed);
			int posted = source.Levers.Count;
			Engine_UiOptionsPending.Track(Engine_UiScreenInput.Back);
			AssertThat(source.Levers.Count).IsEqual(posted);
			AssertThat(Engine_UiOptionsPending.Open).IsFalse();
		});
	}

	[TestCase]
	public void VideoPagePreviewsGammaAndPutsItBackOnCancel()
	{
		WithSource(source =>
		{
			Engine_UiOptionsPending.Enter("Video", Committed);
			AssertThat(Engine_UiOptionsPending.SliderValue(0)).IsEqual(0.55f);
			Engine_UiOptionsPending.Track(Engine_UiScreenInput.Right);
			Engine_UiOptionsPending.Track(Engine_UiScreenInput.Right);
			AssertThat(source.Levers).ContainsExactly("Options:1:gammalive:0.6", "Options:1:gammalive:0.65");
			AssertThat(Engine_UiOptionsPending.Pending(Committed).Gamma).IsEqualApprox(0.65f, 1e-5f);
			Engine_UiOptionsPending.Track(Engine_UiScreenInput.Back);
			AssertThat(source.Levers[^1]).IsEqual("Options:1:gammalive:0.55");

			Engine_UiOptionsPending.Enter("Video", Committed);
			Engine_UiOptionsPending.Track(Engine_UiScreenInput.Left);
			Engine_UiOptionsPending.Track(Engine_UiScreenInput.Select);
			AssertThat(source.Levers[^1]).IsEqual("Options:1:gamma:0.5");
		});
	}

	[TestCase]
	public void AnUnknownPageNameIsRefused()
	{
		WithSource(_ =>
		{
			AssertThat(Engine_UiOptionsPending.Enter(string.Empty, Committed)).IsFalse();
			AssertThat(Engine_UiOptionsPending.Open).IsFalse();
			AssertThat(Engine_UiOptionsPending.Enter("Mods", Committed)).IsFalse();
			AssertThat(Engine_UiOptionsPending.Enter("Controls", Committed)).IsTrue();
			AssertThat(Engine_UiOptionsPending.Page).IsEqual("Controls");
		});
	}

	[TestCase]
	public void TheListWidgetsStepWrapsOnlyWhereTheWidgetWraps()
	{
		AssertThat(Engine_UiOptionsPending.StepIndex(0, -1, 3, true)).IsEqual(2);
		AssertThat(Engine_UiOptionsPending.StepIndex(2, 1, 3, true)).IsEqual(0);
		AssertThat(Engine_UiOptionsPending.StepIndex(0, -1, 5, false)).IsEqual(0);
		AssertThat(Engine_UiOptionsPending.StepIndex(4, 1, 5, false)).IsEqual(4);
		AssertThat(Engine_UiOptionsPending.StepIndex(1, 1, 5, false)).IsEqual(2);
	}

	[TestCase]
	public void ThePadStepsTheRecordAtOnceAndSendsNoScreenCode()
	{
		WithSource(source =>
		{
			Engine_UiOptionsPending.Enter("Game", Committed);
			var level = new Engine_UiScreenLevel();
			level.Drive(new Engine_UiSurfaceInput { Direction = 1, Delta = 0.1 });
			AssertThat(Engine_UiOptionsPending.Row).IsEqual(1);
			level.Drive(new Engine_UiSurfaceInput { Back = true, Delta = 0.1 });
			AssertThat(Engine_UiOptionsPending.Open).IsFalse();
			AssertThat(source.Codes).IsEmpty();
		});
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
}
