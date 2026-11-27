namespace Refii.Tests.Ui;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable2;
using Refii.Ui;

/// <summary>
/// The TopBox's rows (<see cref="Engine_UiTopBoxLayout"/>): title, body and button row stacked
/// top-down under the box's line origin for every channel, from the extents Godot draws — the
/// guest's box fit cannot place a headless element and measures in its own font. Canvas +Y up.
/// No Godot type is built.
/// </summary>
[TestSuite]
public class HudTopBoxLayoutTests
{
	private const float Leading = 5f;

	private static Engine_UiTopBoxLayout.Extent Moved(Engine_UiTopBoxLayout.Extent e,
		Engine_UiTopBoxLayout.Offset by) => e.Moved(by);

	/// <summary>
	/// The quest banner as reported: title and objective both drawn at the authored line origin,
	/// one over the other. Arranged, the title's top is the anchor and the body hangs below it.
	/// </summary>
	[TestCase]
	public void TheQuestBannerStacksTheObjectiveUnderTheTitle()
	{
		var title = new Engine_UiTopBoxLayout.Extent(0f, 254f, 120f, 22f);   // "Slave Rescue"
		var body = new Engine_UiTopBoxLayout.Extent(0f, 258f, 180f, 18f);    // "Talk to the guard."

		var arranged = Engine_UiTopBoxLayout.Arrange(title, body, null, 0f, 276f, Leading);
		var t = Moved(title, arranged.Title);
		var b = Moved(body, arranged.Body);

		AssertThat(t.Top).IsEqualApprox(276f, 0.001f);
		AssertThat(b.Top).IsEqualApprox(t.Bottom - Leading, 0.001f);
		AssertThat(b.Top < t.Bottom).IsTrue();
		AssertThat(t.CentreX).IsEqualApprox(0f, 0.001f);
		AssertThat(b.CentreX).IsEqualApprox(0f, 0.001f);
	}

	/// <summary>
	/// The job sign: the title row (name plus its inline stars, one mesh) over a two-line body,
	/// and the keycap row inside the box below the body, right-aligned to the text — not left
	/// wherever its authored, never-scrubbed TopBoxY pose put it.
	/// </summary>
	[TestCase]
	public void TheJobSignPutsTheButtonRowUnderTheBodyInsideTheBox()
	{
		var title = new Engine_UiTopBoxLayout.Extent(-10f, 254f, 160f, 22f);   // "Blacksmith ★★★"
		var body = new Engine_UiTopBoxLayout.Extent(0f, 240f, 260f, 40f);      // two lines
		var buttons = new Engine_UiTopBoxLayout.Extent(300f, 520f, 90f, 30f);  // floating above

		var arranged = Engine_UiTopBoxLayout.Arrange(title, body, buttons, 0f, 276f, Leading);
		var t = Moved(title, arranged.Title);
		var b = Moved(body, arranged.Body);
		var r = Moved(buttons, arranged.Buttons);

		AssertThat(t.Bottom >= b.Top).IsTrue();
		AssertThat(r.Top).IsEqualApprox(b.Bottom - Leading, 0.001f);
		AssertThat(r.Right).IsEqualApprox(System.Math.Max(t.Right, b.Right), 0.001f);

		// The box the panel fits holds every row.
		var bounds = arranged.Bounds!.Value;
		AssertThat(bounds.Top).IsEqualApprox(276f, 0.001f);
		AssertThat(bounds.Bottom).IsEqualApprox(r.Bottom, 0.001f);
		AssertThat(bounds.Left <= b.Left && bounds.Right >= r.Right).IsTrue();
	}

	/// <summary>Applied once, the arrangement is a fixed point: the next frame moves nothing.</summary>
	[TestCase]
	public void ArrangingTheArrangedRowsMovesNothing()
	{
		var title = new Engine_UiTopBoxLayout.Extent(-40f, 100f, 120f, 22f);
		var body = new Engine_UiTopBoxLayout.Extent(30f, -30f, 180f, 40f);
		var buttons = new Engine_UiTopBoxLayout.Extent(300f, 520f, 90f, 30f);
		var first = Engine_UiTopBoxLayout.Arrange(title, body, buttons, 12f, 276f, Leading);

		var again = Engine_UiTopBoxLayout.Arrange(
			Moved(title, first.Title), Moved(body, first.Body), Moved(buttons, first.Buttons),
			12f, 276f, Leading);

		foreach (var by in new[] { again.Title, again.Body, again.Buttons })
		{
			AssertThat(System.Math.Abs(by.X) < 0.001f && System.Math.Abs(by.Y) < 0.001f).IsTrue();
		}
	}

	/// <summary>A box with no title (a body-only notice) starts its body at the anchor.</summary>
	[TestCase]
	public void ABodyOnlyBoxStartsAtTheAnchor()
	{
		var body = new Engine_UiTopBoxLayout.Extent(5f, 0f, 200f, 18f);
		var arranged = Engine_UiTopBoxLayout.Arrange(null, body, null, 0f, 276f, Leading);
		var b = Moved(body, arranged.Body);
		AssertThat(b.Top).IsEqualApprox(276f, 0.001f);
		AssertThat(b.CentreX).IsEqualApprox(0f, 0.001f);
		AssertThat(arranged.Title).IsEqual(Engine_UiTopBoxLayout.Offset.None);
	}

	/// <summary>`ButtonOnly`: the row alone, centred under the anchor.</summary>
	[TestCase]
	public void AButtonOnlyBoxCentresTheRow()
	{
		var buttons = new Engine_UiTopBoxLayout.Extent(300f, 520f, 90f, 30f);
		var arranged = Engine_UiTopBoxLayout.Arrange(null, null, buttons, 0f, 276f, Leading);
		var r = Moved(buttons, arranged.Buttons);
		AssertThat(r.Top).IsEqualApprox(276f, 0.001f);
		AssertThat(r.CentreX).IsEqualApprox(0f, 0.001f);
	}

	[TestCase]
	public void NothingDrawnHasNoBounds()
	{
		var arranged = Engine_UiTopBoxLayout.Arrange(null, null, null, 0f, 276f, Leading);
		AssertThat(arranged.Bounds is null).IsTrue();
	}

	[TestCase]
	public void TheLeadingFollowsTheBodySize()
	{
		AssertThat(Engine_UiTopBoxLayout.Leading(18))
			.IsEqualApprox(18 * Engine_UiTopBoxLayout.LeadingOfBodySize, 0.001f);
		AssertThat(Engine_UiTopBoxLayout.Leading(0) > 0f).IsTrue();
	}

	private static float VisibleWidth(float x, float y) => Engine_HudLayout.CanvasExtent(new Godot.Vector2(x, y)).X;

	/// <summary>
	/// The quest banner on split screen (int7 shots/vc2.jpg): each half is a 960x1080 view whose canvas
	/// shows 952 units, and the stated 1076 ran the line across the other player's half. A half wraps at
	/// the stated width's share of its view, and the line fits inside it.
	/// </summary>
	[TestCase]
	public void ASplitScreenHalfWrapsTheBannerInsideItself()
	{
		float wide = VisibleWidth(1920f, 1080f);
		float half = VisibleWidth(960f, 1080f);
		float wrap = Engine_UiTopBoxLayout.WrapWidthIn(Engine_UiHudTopBox.WrapWidth, half, wide);
		AssertThat(wrap < half).IsTrue();
		AssertThat(wrap).IsEqualApprox(Engine_UiHudTopBox.WrapWidth * (half / wide), 0.01f);
		AssertThat(Engine_UiHudTopBox.WrapWidth > half).IsTrue();

		// Stacked halves are 1920x540: wider than 16:9, so the stated width stands.
		AssertThat(Engine_UiTopBoxLayout.WrapWidthIn(Engine_UiHudTopBox.WrapWidth, VisibleWidth(1920f, 540f), wide))
			.IsEqual(Engine_UiHudTopBox.WrapWidth);
	}

	/// <summary>The single-player picture at 16:9 and wider keeps the guest's stated width.</summary>
	[TestCase]
	public void A16By9OrWiderViewKeepsTheStatedWidth()
	{
		float wide = VisibleWidth(1920f, 1080f);
		AssertThat(Engine_UiTopBoxLayout.WrapWidthIn(Engine_UiHudTopBox.WrapWidth, wide, wide))
			.IsEqual(Engine_UiHudTopBox.WrapWidth);
		AssertThat(Engine_UiTopBoxLayout.WrapWidthIn(Engine_UiHudTopBox.WrapWidth, VisibleWidth(2560f, 1440f), wide))
			.IsEqual(Engine_UiHudTopBox.WrapWidth);
		AssertThat(Engine_UiTopBoxLayout.WrapWidthIn(Engine_UiHudTopBox.WrapWidth, VisibleWidth(3440f, 1440f), wide))
			.IsEqual(Engine_UiHudTopBox.WrapWidth);
		AssertThat(Engine_UiTopBoxLayout.WrapWidthIn(0f, VisibleWidth(960f, 1080f), wide)).IsEqual(0f);
	}

	/// <summary>
	/// The box fit's re-statement of the TopBox strings (wrapped to the guest's font) is not
	/// taken; every other TextName write is.
	/// </summary>
	[TestCase]
	public void TheBoxFitsWrappedTopBoxStringsAreNotTaken()
	{
		AssertThat(Engine_UiHudRegistryWrites.TakesText(Refii.Core.Engine_GameHash.Fnv1("TopTitle"))).IsFalse();
		AssertThat(Engine_UiHudRegistryWrites.TakesText(Refii.Core.Engine_GameHash.Fnv1("TopMain"))).IsFalse();
		AssertThat(Engine_UiHudRegistryWrites.TakesText(Refii.Core.Engine_GameHash.Fnv1("RightTopButton"))).IsTrue();
		AssertThat(Engine_UiHudRegistryWrites.TakesText(Refii.Core.Engine_GameHash.Fnv1("Subtitles"))).IsTrue();
	}
}
