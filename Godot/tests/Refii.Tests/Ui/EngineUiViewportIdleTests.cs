namespace Refii.Tests.Ui;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Ui;

/// <summary>
/// When an idling UI viewport renders (Engine_UiViewportIdle): an idle viewport keeps its last
/// picture, so the frame after the content went must still render.
/// </summary>
[TestSuite]
public class EngineUiViewportIdleTests
{
	/// <summary>Runs frames of (held, content) through one viewport's rule; a letter a frame: R rendered, - idle.</summary>
	private static string Run(params (bool Held, bool Content)[] frames)
	{
		var idle = new Engine_UiViewportIdle();
		var rendered = new System.Text.StringBuilder();
		foreach ((bool held, bool content) in frames)
		{
			rendered.Append(idle.Renders(held, content) ? 'R' : '-');
		}

		return rendered.ToString();
	}

	[TestCase]
	public void ContentRendersEveryFrame()
	{
		AssertThat(Run((false, true), (false, true))).IsEqual("RR");
	}

	[TestCase]
	public void TheFrameAfterTheContentWentStillRendersThenTheViewportIdles()
	{
		AssertThat(Run((false, true), (false, false), (false, false), (false, false))).IsEqual("RR--");
	}

	[TestCase]
	public void NothingEverDrawnNeverRenders()
	{
		AssertThat(Run((false, false), (false, false))).IsEqual("--");
	}

	[TestCase]
	public void AHoldKeepsTheViewportIdleAndTheEmptyFrameIsOwedAfterIt()
	{
		// Content, then a level change under the loading cover empties it: the first frame after
		// the cover renders the emptiness, the next idles.
		AssertThat(Run((false, true), (true, false), (true, false), (false, false), (false, false))).IsEqual("R--R-");
	}

	[TestCase]
	public void ContentUnderAHoldDoesNotRender()
	{
		AssertThat(Run((true, true), (true, true), (false, true))).IsEqual("--R");
	}

	[TestCase]
	public void ContentComingBackRendersAtOnce()
	{
		AssertThat(Run((false, true), (false, false), (false, false), (false, true))).IsEqual("RR-R");
	}
}
