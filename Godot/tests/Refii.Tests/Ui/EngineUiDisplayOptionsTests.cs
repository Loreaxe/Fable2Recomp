namespace Refii.Tests.Ui;

using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Ui;

/// <summary>
/// The Display rows in game read and write the launcher's own keys: the window mode is the
/// launcher's composite of `fullscreen` and `borderless`, the frame limit its 0..240 by 5, a stored
/// value no list offers is kept, and a commit writes the keys the launcher reads back.
/// </summary>
[TestSuite]
public class EngineUiDisplayOptionsTests
{
	private static readonly List<(string, string)> Resolutions = new() { ("1920x1080", "1920x1080"), ("1280x720", "1280x720") };

	private static readonly List<(string, string)> Vsyncs = new() { ("mailbox", "Mailbox"), ("on", "On"), ("adaptive", "Adaptive"), ("off", "Off") };

	[TestCase]
	public void TheRowsAreTheLaunchersKeys()
	{
		var values = new Dictionary<string, string>
		{
			["fullscreen"] = "false", ["borderless"] = "true", ["display_resolution"] = "1280x720",
			["vsync_mode"] = "\"off\"", ["max_fps"] = "120",
		};
		List<Engine_UiDisplayOptions.Row> rows = Engine_UiDisplayOptions.Build(values, Resolutions, Vsyncs);
		AssertThat(rows.Count).IsEqual(Engine_UiDisplayOptions.RowCount);
		AssertThat(rows[0].Choices[rows[0].Index].Value).IsEqual("borderless");
		AssertThat(rows[1].Index).IsEqual(1);
		AssertThat(rows[2].Choices[rows[2].Index].Value).IsEqual("off");
		AssertThat(rows[3].Choices[rows[3].Index].Value).IsEqual("120");
		AssertThat(rows[3].Choices.Count).IsEqual(Engine_UiDisplayOptions.FrameLimitTop / Engine_UiDisplayOptions.FrameLimitStep + 1);

		// Fullscreen wins over borderless, as the launcher's GetWindowMode has it.
		values["fullscreen"] = "true";
		AssertThat(Engine_UiDisplayOptions.Build(values, Resolutions, Vsyncs)[0].Choices[0].Value).IsEqual("windowed");
		AssertThat(Engine_UiDisplayOptions.Build(values, Resolutions, Vsyncs)[0].Index).IsEqual(2);
	}

	[TestCase]
	public void AStoredValueNoListOffersIsKept()
	{
		var values = new Dictionary<string, string> { ["display_resolution"] = "3440x1440", ["max_fps"] = "144" };
		List<Engine_UiDisplayOptions.Row> rows = Engine_UiDisplayOptions.Build(values, Resolutions, Vsyncs);
		AssertThat(rows[1].Choices[rows[1].Index].Value).IsEqual("3440x1440");
		AssertThat(rows[1].Choices.Count).IsEqual(3);
		AssertThat(rows[3].Choices[rows[3].Index].Value).IsEqual("144");
	}

	[TestCase]
	public void ACommitWritesTheLaunchersKeys()
	{
		List<Engine_UiDisplayOptions.Row> rows = Engine_UiDisplayOptions.Build(new Dictionary<string, string>(), Resolutions, Vsyncs);
		var written = new Dictionary<string, string>();
		int[] chosen = { 2, 0, 1, 12 };
		Engine_UiDisplayOptions.Store(rows, row => chosen[row], written);
		AssertThat(written["fullscreen"]).IsEqual("true");
		AssertThat(written["borderless"]).IsEqual("false");
		AssertThat(written["display_resolution"]).IsEqual("1920x1080");
		AssertThat(written["vsync_mode"]).IsEqual("on");
		AssertThat(written["max_fps"]).IsEqual("60");
		AssertThat(written.ContainsKey("window_mode")).IsFalse();
	}
}
