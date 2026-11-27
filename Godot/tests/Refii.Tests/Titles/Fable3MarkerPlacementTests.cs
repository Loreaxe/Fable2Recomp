namespace Refii.Tests.Titles;

using GdUnit4;
using Godot;
using static GdUnit4.Assertions;
using Refii.Titles.Fable3;

/// <summary>
/// The co-op markers' screen rules (types 52/53): sub_82B519C8's edge clamp and the corner
/// sub_82B529E8 measures the on-screen lift from. Pure arithmetic on Godot's value types; no
/// Godot object is built.
/// </summary>
[TestSuite]
public class Fable3MarkerPlacementTests
{
	/// <summary>
	/// At 1280x720 the clamp's rect is x in [0.1w + 10, w - 0.09w - 10], y in [0.1h + 40, h - 0.09h]:
	/// [138, 1154.8] x [112, 655.2]. A point inside stays put.
	/// </summary>
	[TestCase]
	public void ASplitHalfPlacesItsMarkersInsideItself()
	{
		// Side by side at 2560x1440: player two's half stands at (1280, 0), 1280x1440. Its camera's centre lands
		// at the half's centre, not on the divider; a viewport rendered at another size is scaled onto the half.
		var half = new Vector2(1280f, 1440f);
		Vector2 centre = Fable3MarkerPlacement.OnWindow(new Vector2(640f, 720f), half, new Vector2(1280f, 0f), half);
		AssertThat(centre.X).IsEqualApprox(1920f, 0.001f);
		AssertThat(centre.Y).IsEqualApprox(720f, 0.001f);
		Vector2 scaled = Fable3MarkerPlacement.OnWindow(new Vector2(320f, 360f), new Vector2(640f, 720f), new Vector2(1280f, 0f), half);
		AssertThat(scaled.X).IsEqualApprox(1920f, 0.001f);
		AssertThat(scaled.Y).IsEqualApprox(720f, 0.001f);
		// One screen: the window, unchanged.
		var window = new Vector2(2560f, 1440f);
		Vector2 same = Fable3MarkerPlacement.OnWindow(new Vector2(1280f, 550f), window, Vector2.Zero, window);
		AssertThat(same.X).IsEqualApprox(1280f, 0.001f);
		AssertThat(same.Y).IsEqualApprox(550f, 0.001f);
	}

	[TestCase]
	public void TheClampHoldsTheGuestsMarginsAt720()
	{
		var window = new Vector2(1280f, 720f);
		Vector2 low = Fable3MarkerPlacement.Clamp(new Vector2(-500f, -500f), window);
		Vector2 high = Fable3MarkerPlacement.Clamp(new Vector2(5000f, 5000f), window);
		AssertThat(low.X).IsEqualApprox(138f, 0.001f);
		AssertThat(low.Y).IsEqualApprox(112f, 0.001f);
		AssertThat(high.X).IsEqualApprox(1154.8f, 0.001f);
		AssertThat(high.Y).IsEqualApprox(655.2f, 0.001f);
		Vector2 inside = Fable3MarkerPlacement.Clamp(new Vector2(640f, 360f), window);
		AssertThat(inside.X).IsEqual(640f);
		AssertThat(inside.Y).IsEqual(360f);
	}

	/// <summary>The 10 and 40 pixels are the guest's 720 lines: doubled at 1440, the fractions unchanged.</summary>
	[TestCase]
	public void TheClampsFixedPixelsScaleWithTheLines()
	{
		var window = new Vector2(2560f, 1440f);
		Vector2 low = Fable3MarkerPlacement.Clamp(new Vector2(0f, 0f), window);
		Vector2 high = Fable3MarkerPlacement.Clamp(window, window);
		AssertThat(low.X).IsEqualApprox(276f, 0.001f);
		AssertThat(low.Y).IsEqualApprox(224f, 0.001f);
		AssertThat(high.X).IsEqualApprox(2309.6f, 0.01f);
		AssertThat(high.Y).IsEqualApprox(1310.4f, 0.01f);
	}

	/// <summary>
	/// The lift is measured from the content's (right, bottom): `Contents.Icon.ImageModel`, 37.5
	/// square about y +25, so +18.75 right and 6.25 above the root (y down on screen).
	/// </summary>
	[TestCase]
	public void TheLiftCornerIsTheIconsBottomRight()
	{
		Vector2 corner = Fable3MarkerPlacement.LiftCorner(new Vector2(640f, 360f), 2f);
		AssertThat(corner.X).IsEqualApprox(640f + 37.5f, 0.001f);
		AssertThat(corner.Y).IsEqualApprox(360f - 12.5f, 0.001f);
	}
}
