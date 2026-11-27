namespace Refii.Tests.Camera;

using GdUnit4;
using Godot;
using Refii.Camera;
using static GdUnit4.Assertions;

/// <summary>
/// The views the shell states for the camera mod API's reads (Engine_CameraViews): the axes, the drawn
/// angles, the ray through a view point, the display offset and the stated layout. Pure math.
/// </summary>
[TestSuite]
public class CameraViewsTests
{
	private static void Near(Vector3 a, Vector3 b, float within = 1e-5f) =>
		AssertThat(a.DistanceTo(b) < within).OverrideFailureMessage($"{a} is not {b}").IsTrue();

	private static void Near(float a, float b, float within = 1e-5f) =>
		AssertThat(Mathf.Abs(a - b) < within).OverrideFailureMessage($"{a} is not {b}").IsTrue();

	[TestCase]
	public void TheAxesAreTheGuestsAndARotation()
	{
		var g = new Vector3(1f, 2f, 3f);
		Near(Engine_CameraViews.ToGuest(g), new Vector3(-1f, 3f, 2f));
		Near(Engine_CameraViews.ToGodot(Engine_CameraViews.ToGuest(g)), g);
		// A rotation: cross products map to cross products, so right = forward × up in either frame.
		var a = new Vector3(0.3f, -1f, 0.2f);
		var b = new Vector3(2f, 0.5f, -0.4f);
		Near(Engine_CameraViews.ToGuest(a.Cross(b)), Engine_CameraViews.ToGuest(a).Cross(Engine_CameraViews.ToGuest(b)));
	}

	[TestCase]
	public void TheDrawnAnglesFollowTheKeptSide()
	{
		(float h, float v) = Engine_CameraViews.Tangents(60f, keepsHeight: true, aspect: 2f);
		Near(v, Mathf.Tan(Mathf.DegToRad(30f)));
		Near(h, v * 2f);
		(h, v) = Engine_CameraViews.Tangents(60f, keepsHeight: false, aspect: 0.5f);
		Near(h, Mathf.Tan(Mathf.DegToRad(30f)));
		Near(v, h / 0.5f);
	}

	[TestCase]
	public void AWindowCameraIsStatedInGuestAxes()
	{
		// Godot's camera at (1, 2, 3) looking along Godot +Z (back of its basis is -Z), up +Y.
		var basis = new Basis(Vector3.Left, Vector3.Up, Vector3.Back * -1f);
		var view = Engine_CameraViews.FromCamera(new Transform3D(basis, new Vector3(1f, 2f, 3f)), 60f, true, 16f / 9f,
			new Rect2(0f, 0f, 1f, 1f), cut: false);
		Near(view.Position, new Vector3(-1f, 3f, 2f));
		Near(view.Forward, Engine_CameraViews.ToGuest(Vector3.Back));
		Near(view.Up, new Vector3(0f, 0f, 1f));
		Near(view.VerticalFov, Mathf.DegToRad(60f));
		Near(Mathf.Tan(view.HorizontalFov * 0.5f), Mathf.Tan(Mathf.DegToRad(30f)) * 16f / 9f);
	}

	[TestCase]
	public void TheRayGoesThroughThePointOfTheView()
	{
		var view = new Engine_CameraViews.View(new Vector3(5f, 6f, 7f), new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f),
			Mathf.DegToRad(90f), Mathf.DegToRad(90f), false, new Rect2(0f, 0f, 1f, 1f));
		(Vector3 origin, Vector3 centre) = Engine_CameraViews.Ray(view, 0.5f, 0.5f);
		Near(origin, view.Position);
		Near(centre, view.Forward);
		// Right of a camera facing +y with z up is +x (forward × up); the view's top is +z.
		(_, Vector3 right) = Engine_CameraViews.Ray(view, 1f, 0.5f);
		Near(right, new Vector3(1f, 1f, 0f).Normalized());
		(_, Vector3 top) = Engine_CameraViews.Ray(view, 0.5f, 0f);
		Near(top, new Vector3(0f, 1f, 1f).Normalized());
		(_, Vector3 corner) = Engine_CameraViews.Ray(view, 0f, 1f);
		Near(corner, new Vector3(-1f, 1f, -1f).Normalized());
	}

	[TestCase]
	public void TheOffsetMovesThePictureAlongItsOwnAxes()
	{
		var drawn = new Transform3D(Basis.Identity, new Vector3(1f, 1f, 1f));
		Near(Engine_CameraViews.Offset(drawn, null).Origin, drawn.Origin);
		Transform3D moved = Engine_CameraViews.Offset(drawn, new[] { 0.5f, 0.25f, 2f, 0f, 0f, 0f, 0f });
		Near(moved.Origin, new Vector3(1.5f, 1.25f, 3f));
		Transform3D turned = Engine_CameraViews.Offset(drawn, new[] { 0f, 0f, 0f, 90f, 0f, 0f, 0f });
		Near(-turned.Basis.Z, Vector3.Left);
		Near(Engine_CameraViews.OffsetFov(70f, new[] { 0f, 0f, 0f, 0f, 0f, 0f, 5f }), 75f);
		Near(Engine_CameraViews.OffsetFov(178f, new[] { 0f, 0f, 0f, 0f, 0f, 0f, 5f }), 179f);
	}

	[TestCase]
	public void TheLayoutIsTheScreenThenEachPlayer()
	{
		Engine_CameraViews.Clear();
		Engine_CameraViews.Screen(Engine_CameraViews.ScreenSplitHorizontal, split: true);
		var two = new Engine_CameraViews.View(new Vector3(1f, 2f, 3f), new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f),
			1f, 0.5f, true, new Rect2(0f, 0.5f, 1f, 0.5f));
		Engine_CameraViews.Draw(2, two);
		float[] w = Engine_CameraViews.Words();
		AssertThat(w.Length).IsEqual(Engine_CameraViews.ViewsWords);
		AssertThat(w.Length).IsEqual(42);
		Near(w[0], 2f);
		Near(w[1], 1f);
		Near(w[2 + 16], 0f);  // player one: not drawn
		int at = 2 + Engine_CameraViews.ViewWords;
		Near(w[at + 0], 1f);
		Near(w[at + 4], 1f);
		Near(w[at + 8], 1f);
		Near(w[at + 9], 1f);
		Near(w[at + 10], 0.5f);
		Near(w[at + 11], 1f);
		Near(w[at + 13], 0.5f);
		Near(w[at + 15], 0.5f);
		Near(w[at + 16], 1f);
		AssertThat(Engine_CameraViews.For(1) is null).IsTrue();
		Engine_CameraViews.Clear();
		AssertThat(Engine_CameraViews.For(2) is null).IsTrue();
	}

	/// <summary>
	/// Through hybrid's swap a half shows an off-centre part of its camera's picture: at the swap's start the
	/// window's own picture cut at the divider. The half's rays are the window's through the same pixels.
	/// </summary>
	[TestCase]
	public void ASwappingHalfsRaysAreThePicturesItShows()
	{
		const float near = 0.05f;
		const float vertical = 59f;
		const float window = 16f / 9f;
		var basis = new Basis(Vector3.Left, Vector3.Up, Vector3.Back * -1f).Rotated(Vector3.Up, 0.4f);
		var pose = new Transform3D(basis, new Vector3(1f, 2f, 3f));
		var whole = Engine_CameraViews.FromCamera(pose, vertical, true, window, new Rect2(0f, 0f, 1f, 1f), cut: false);
		foreach (bool stacked in new[] { false, true })
		{
			for (int view = 0; view < 2; ++view)
			{
				Engine_CameraSwap.Frustum crop = Engine_CameraSwap.Crop(vertical, window, near, stacked, view);
				float halfAspect = stacked ? window * 2f : window * 0.5f;
				Rect2 rect = stacked ? new Rect2(0f, view * 0.5f, 1f, 0.5f) : new Rect2(view * 0.5f, 0f, 0.5f, 1f);
				var half = Engine_CameraViews.FromCamera(pose, Engine_CameraSwap.VerticalDegrees(crop, near), true, halfAspect,
					rect, cut: false, crop.Offset / near);
				foreach ((float x, float y) in new[] { (0.5f, 0.5f), (0f, 0f), (1f, 1f), (0.25f, 0.75f) })
				{
					float wx = rect.Position.X + x * rect.Size.X;
					float wy = rect.Position.Y + y * rect.Size.Y;
					Near(Engine_CameraViews.Ray(half, x, y).Direction, Engine_CameraViews.Ray(whole, wx, wy).Direction, 1e-4f);
				}
			}
		}

		// The centre is stated in words 17..18 for the native ray (core/camera_views.h).
		Engine_CameraViews.Clear();
		Engine_CameraViews.Draw(1, whole with { Centre = new Vector2(0.25f, -0.5f) });
		float[] w = Engine_CameraViews.Words();
		Near(w[2 + 17], 0.25f);
		Near(w[2 + 18], -0.5f);
		Engine_CameraViews.Clear();
	}
}
