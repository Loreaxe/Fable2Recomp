namespace Refii.Tests.Camera;

using GdUnit4;
using Godot;
using Refii.Camera;
using static GdUnit4.Assertions;

/// <summary>
/// Hybrid's swap picture (Engine_CameraSwap): at its start each half is exactly its part of the window's
/// picture, at its end the half's own framing, the split view's angle rule. Pure math.
/// </summary>
[TestSuite]
public class CameraSwapTests
{
	private const float NearPlane = 0.05f;

	private static void Near(float a, float b, float within = 1e-6f) =>
		AssertThat(Mathf.Abs(a - b) < within).OverrideFailureMessage($"{a} is not {b}").IsTrue();

	/// <summary>The near-plane rectangle of a frustum (height kept) on a view of this aspect: left, right, bottom, top.</summary>
	private static (float L, float R, float B, float T) Edges(Engine_CameraSwap.Frustum f, float aspect) =>
		(f.Offset.X - f.Size * aspect * 0.5f, f.Offset.X + f.Size * aspect * 0.5f,
			f.Offset.Y - f.Size * 0.5f, f.Offset.Y + f.Size * 0.5f);

	[TestCase]
	public void SideBySideTheHalvesAreTheWindowCutDownTheMiddle()
	{
		const float vertical = 59f;
		const float window = 16f / 9f;
		float t = NearPlane * Mathf.Tan(Mathf.DegToRad(vertical) * 0.5f);
		var left = Edges(Engine_CameraSwap.Crop(vertical, window, NearPlane, stacked: false, view: 0), window * 0.5f);
		var right = Edges(Engine_CameraSwap.Crop(vertical, window, NearPlane, stacked: false, view: 1), window * 0.5f);
		Near(left.L, -window * t);
		Near(left.R, 0f);
		Near(right.L, 0f);
		Near(right.R, window * t);
		Near(left.B, -t);
		Near(left.T, t);
		Near(right.B, -t);
		Near(right.T, t);
	}

	[TestCase]
	public void StackedTheHalvesAreTheWindowCutAcross()
	{
		const float vertical = 59f;
		const float window = 16f / 9f;
		float t = NearPlane * Mathf.Tan(Mathf.DegToRad(vertical) * 0.5f);
		var top = Edges(Engine_CameraSwap.Crop(vertical, window, NearPlane, stacked: true, view: 0), window * 2f);
		var bottom = Edges(Engine_CameraSwap.Crop(vertical, window, NearPlane, stacked: true, view: 1), window * 2f);
		Near(top.B, 0f);
		Near(top.T, t);
		Near(bottom.B, -t);
		Near(bottom.T, 0f);
		Near(top.L, -window * t);
		Near(top.R, window * t);
	}

	[TestCase]
	public void TheOwnFramingIsTheSplitViewsAngleRule()
	{
		const float horizontal = 75f;
		float th = Mathf.Tan(Mathf.DegToRad(horizontal) * 0.5f);
		// A half narrower than 4:3 keeps the stated horizontal angle across its width.
		const float narrowAspect = 8f / 9f;
		Engine_CameraSwap.Frustum narrow = Engine_CameraSwap.Own(horizontal, narrowAspect, NearPlane, narrow: true);
		var n = Edges(narrow, narrowAspect);
		Near(n.R, NearPlane * th);
		Near(n.L, -NearPlane * th);
		// A wider half keeps 2·atan(0.75·tan(h/2)) up its height.
		Engine_CameraSwap.Frustum wide = Engine_CameraSwap.Own(horizontal, 32f / 9f, NearPlane, narrow: false);
		Near(wide.Size, 2f * NearPlane * 0.75f * th);
		float vertical = Mathf.RadToDeg(2f * Mathf.Atan(0.75f * th));
		Near(Engine_CameraSwap.VerticalDegrees(wide, NearPlane), vertical, 1e-3f);
		AssertThat(narrow.Offset).IsEqual(Vector2.Zero);
	}

	[TestCase]
	public void TheNarrowRuleIsFourByThree()
	{
		AssertThat(Engine_CameraSwap.Narrow(new Vector2I(1280, 1440))).IsTrue();
		AssertThat(Engine_CameraSwap.Narrow(new Vector2I(1920, 1440))).IsFalse();
		AssertThat(Engine_CameraSwap.Narrow(new Vector2I(2560, 720))).IsFalse();
	}

	[TestCase]
	public void TheBlendRunsFromTheCropToTheOwnFraming()
	{
		Engine_CameraSwap.Frustum crop = Engine_CameraSwap.Crop(59f, 16f / 9f, NearPlane, stacked: false, view: 1);
		Engine_CameraSwap.Frustum own = Engine_CameraSwap.Own(75f, 8f / 9f, NearPlane, narrow: true);
		AssertThat(Engine_CameraSwap.Blend(crop, own, 0f)).IsEqual(crop);
		AssertThat(Engine_CameraSwap.Blend(crop, own, 1f)).IsEqual(own);
		Engine_CameraSwap.Frustum half = Engine_CameraSwap.Blend(crop, own, 0.5f);
		Near(half.Size, (crop.Size + own.Size) * 0.5f);
		Near(half.Offset.X, crop.Offset.X * 0.5f);
	}

	/// <summary>
	/// The near-plane rectangle the part <see cref="Engine_CameraSwap.Shown"/> picks of the centred picture
	/// (<see cref="Engine_CameraSwap.Full"/>, <see cref="Engine_CameraSwap.CoverVerticalDegrees"/>, height kept)
	/// spans: left, right, bottom, top.
	/// </summary>
	private static (float L, float R, float B, float T) ShownEdges(Engine_CameraSwap.Frustum f, Vector2I half, bool stacked)
	{
		Vector2I full = Engine_CameraSwap.Full(half, stacked);
		float height = 2f * NearPlane * Mathf.Tan(Mathf.DegToRad(Engine_CameraSwap.CoverVerticalDegrees(f, NearPlane, stacked)) * 0.5f);
		float width = height * full.X / full.Y;
		Rect2I shown = Engine_CameraSwap.Shown(f, half, stacked);
		AssertThat(shown.Size).IsEqual(half);
		float X(int px) => ((float)px / full.X - 0.5f) * width;
		float Y(int py) => (0.5f - (float)py / full.Y) * height;
		return (X(shown.Position.X), X(shown.End.X), Y(shown.End.Y), Y(shown.Position.Y));
	}

	private static void Same((float L, float R, float B, float T) a, (float L, float R, float B, float T) b, float pixel)
	{
		Near(a.L, b.L, pixel);
		Near(a.R, b.R, pixel);
		Near(a.B, b.B, pixel);
		Near(a.T, b.T, pixel);
	}

	[TestCase]
	public void TheCentredPictureShowsExactlyTheSwapsFrustumSideBySide()
	{
		var half = new Vector2I(1280, 1440);
		const float window = 2560f / 1440f;
		float aspect = (float)half.X / half.Y;
		Engine_CameraSwap.Frustum own = Engine_CameraSwap.Own(75f, aspect, NearPlane, narrow: true);
		for (int view = 0; view < 2; view++)
		{
			Engine_CameraSwap.Frustum crop = Engine_CameraSwap.Crop(59f, window, NearPlane, stacked: false, view);
			foreach (float w in new[] { 0f, 0.25f, 0.5f, 0.9f, 0.999f })
			{
				Engine_CameraSwap.Frustum f = Engine_CameraSwap.Blend(crop, own, w);
				// One pixel of the half on the near plane, the rounding Shown allows.
				float pixel = f.Size / half.Y;
				Same(ShownEdges(f, half, stacked: false), Edges(f, aspect), pixel);
			}
		}

		// At the swap's start the halves are the window's picture cut down the middle.
		AssertThat(Engine_CameraSwap.Shown(Engine_CameraSwap.Crop(59f, window, NearPlane, false, 0), half, false).Position)
			.IsEqual(Vector2I.Zero);
		AssertThat(Engine_CameraSwap.Shown(Engine_CameraSwap.Crop(59f, window, NearPlane, false, 1), half, false).Position)
			.IsEqual(new Vector2I(half.X, 0));
		AssertThat(Engine_CameraSwap.Shown(own, half, false).Position).IsEqual(new Vector2I(half.X / 2, 0));
	}

	[TestCase]
	public void TheCentredPictureShowsExactlyTheSwapsFrustumStacked()
	{
		var half = new Vector2I(2560, 720);
		const float window = 2560f / 1440f;
		float aspect = (float)half.X / half.Y;
		Engine_CameraSwap.Frustum own = Engine_CameraSwap.Own(75f, aspect, NearPlane, narrow: false);
		for (int view = 0; view < 2; view++)
		{
			Engine_CameraSwap.Frustum crop = Engine_CameraSwap.Crop(59f, window, NearPlane, stacked: true, view);
			foreach (float w in new[] { 0f, 0.3f, 0.6f, 0.999f })
			{
				Engine_CameraSwap.Frustum f = Engine_CameraSwap.Blend(crop, own, w);
				float pixel = f.Size / half.Y;
				Same(ShownEdges(f, half, stacked: true), Edges(f, aspect), pixel);
			}
		}

		AssertThat(Engine_CameraSwap.Shown(Engine_CameraSwap.Crop(59f, window, NearPlane, true, 0), half, true).Position)
			.IsEqual(Vector2I.Zero);
		AssertThat(Engine_CameraSwap.Shown(Engine_CameraSwap.Crop(59f, window, NearPlane, true, 1), half, true).Position)
			.IsEqual(new Vector2I(0, half.Y));
	}

	[TestCase]
	public void TheCentredPictureIsTwiceTheHalfAlongTheDivider()
	{
		AssertThat(Engine_CameraSwap.Full(new Vector2I(1280, 1440), stacked: false)).IsEqual(new Vector2I(2560, 1440));
		AssertThat(Engine_CameraSwap.Full(new Vector2I(2560, 720), stacked: true)).IsEqual(new Vector2I(2560, 1440));
	}

	[TestCase]
	public void PlayerTwosPoseStartsAtTheSharedCamera()
	{
		var shared = new Transform3D(new Basis(Vector3.Up, 0.3f), new Vector3(1f, 2f, 3f));
		var own = new Transform3D(new Basis(Vector3.Up, -1.1f), new Vector3(-20f, 2.5f, 9f));
		AssertThat(Engine_CameraSwap.Pose(shared, own, 0f)).IsEqual(shared);
		AssertThat(Engine_CameraSwap.Pose(shared, own, 1f)).IsEqual(own);
		Transform3D mid = Engine_CameraSwap.Pose(shared, own, 0.5f);
		AssertThat(mid.Origin.DistanceTo((shared.Origin + own.Origin) * 0.5f) < 1e-4f).IsTrue();
	}
}
