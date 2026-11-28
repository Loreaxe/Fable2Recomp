using System.Collections.Generic;
using Godot;

namespace Refii.Core;

/// <summary>
/// Every camera the world is drawn through this frame: the window's, and on split screen the
/// second player's view.
/// </summary>
/// <remarks>
/// <para>ONE WORLD, ONE OR TWO VIEWS. Local co-op on split screen draws the window's world through
/// two cameras (Refii.Game.GameSplitView): player one's sits exactly where the window camera is —
/// it copies it every frame — and player two's is <see cref="Second"/>. So "the window's camera
/// plus <see cref="Second"/>" is the whole set, and a system that culls, ranks or budgets by what
/// is in view asks for every view rather than <c>GetViewport().GetCamera3D()</c>: a thing in
/// either player's view is in view.</para>
/// <para>OPEN, AND DRAWN. The two views are built once (<see cref="Open"/>) and kept for the whole
/// local co-op game; the screen splits and rejoins by drawing them or the window (<see cref="Split"/>),
/// with nothing built or freed. Hybrid's swap is one of those, so a system that keeps something per
/// view builds it while the views are open and draws it while they are drawn.</para>
/// <para>A system that MUST draw per view (an overlay that mirrors a camera into its own
/// viewport, a per-view shader constant) is told through <see cref="ViewsChanged"/>, raised when
/// the views open or close and when the screen splits or rejoins.</para>
/// </remarks>
public static class Engine_Views
{
	/// <summary>Player two's camera while the views are open, else null.</summary>
	public static Camera3D? Second { get; private set; }

	/// <summary>Player two's view while the views are open, else null.</summary>
	public static SubViewport? SecondViewport { get; private set; }

	/// <summary>Player one's view while the views are open, else null.</summary>
	public static SubViewport? FirstViewport { get; private set; }

	/// <summary>Whether the two views exist (they may not be drawn: see <see cref="Split"/>).</summary>
	public static bool Open => Second is { } camera && GodotObject.IsInstanceValid(camera);

	/// <summary>Whether the world is drawn through the two views right now (else the window draws it).</summary>
	public static bool Split => _drawn && Open;

	/// <summary>
	/// TWO INDEPENDENT CAMERAS. Whether a per-view effect may take its view's slot (the AO, depth and exposure
	/// globals): a split camera's only while the halves are drawn, the window camera's only while they are not. So
	/// view 0 is player one's split camera's alone while split, and the window's alone otherwise; a claim one of
	/// them queued on its last frame before a split or a merge is dropped, not applied over the other's first frame.
	/// Pure.
	/// </summary>
	public static bool MayClaimViewSlot(bool splitCamera, bool splitDrawn) => splitCamera == splitDrawn;

	/// <summary><see cref="MayClaimViewSlot"/> now. Main thread (the effects' deferred claims).</summary>
	public static bool MayClaim(bool splitCamera) => MayClaimViewSlot(splitCamera, Split);

	/// <summary>
	/// How the split view divides the window: false side by side (player one left, player two
	/// right), true stacked (player one top, player two bottom). Stated by the split view as it
	/// lays the views out; every half of the UI (Refii.Ui.Engine_UiSeats) follows it.
	/// </summary>
	public static bool Stacked { get; private set; }

	/// <summary>Raised when the views open or close, and when the screen splits or rejoins.</summary>
	public static event System.Action? ViewsChanged;

	private static readonly List<Camera3D> Scratch = new(2);
	private static bool _drawn;

	/// <summary>Set by the split view when it builds its views (all non-null) or frees them (nulls).</summary>
	public static void SetSplit(SubViewport? first, SubViewport? second, Camera3D? secondCamera)
	{
		FirstViewport = first;
		SecondViewport = second;
		Second = secondCamera;
		if (secondCamera is null)
		{
			_drawn = false;
			ShownRects[0] = null;
			ShownRects[1] = null;
		}

		ViewsChanged?.Invoke();
	}

	/// <summary>Set by the split view when it starts or stops drawing its open views.</summary>
	public static void SetDrawn(bool drawn)
	{
		if (drawn == _drawn)
		{
			return;
		}

		_drawn = drawn;
		ViewsChanged?.Invoke();
	}

	/// <summary>Set by the split view when it lays its views out side by side or stacked.</summary>
	public static void SetStacked(bool stacked) => Stacked = stacked;

	private static readonly Rect2I?[] ShownRects = new Rect2I?[2];

	/// <summary>
	/// The part of view <paramref name="view"/>'s picture (0 player one's, 1 player two's) its half shows, in
	/// that view's own pixels: the whole view at rest; during hybrid's swap the view draws a picture twice the
	/// half along the divider's axis and the half shows a part of it (Refii.Camera.Engine_CameraSwap.Shown).
	/// Something projected through the view's camera lands in the half at its pixel minus this rect's position.
	/// </summary>
	public static Rect2I Shown(int view, Vector2I viewSize) =>
		view is 0 or 1 && ShownRects[view] is { } rect ? rect : new Rect2I(Vector2I.Zero, viewSize);

	/// <summary>Set by the split view each frame it draws: the part of each view its half shows (null: all of it).</summary>
	public static void SetShown(int view, Rect2I? shown)
	{
		if (view is 0 or 1)
		{
			ShownRects[view] = shown;
		}
	}

	/// <summary>
	/// The cameras drawing the world: <paramref name="primary"/> (the window's), or on split
	/// screen player one's view camera in its place followed by player two's. The list is reused;
	/// read it before the next call.
	/// </summary>
	/// <remarks>
	/// While split, the window camera is NOT what player one is drawn through: player one's view
	/// shares its eye but renders a half of the window at the split view's angle rule, so its
	/// frustum differs. A frustum test against the window camera would call
	/// things on player one's screen out of view.
	/// </remarks>
	public static IReadOnlyList<Camera3D> Cameras(Camera3D? primary)
	{
		Scratch.Clear();
		if (Split)
		{
			if (FirstViewport?.GetCamera3D() is { } first && GodotObject.IsInstanceValid(first))
			{
				Scratch.Add(first);
			}
			else if (primary is not null && GodotObject.IsInstanceValid(primary))
			{
				Scratch.Add(primary);
			}

			Scratch.Add(Second!);
			return Scratch;
		}

		if (primary is not null && GodotObject.IsInstanceValid(primary))
		{
			Scratch.Add(primary);
		}

		return Scratch;
	}

	/// <summary>
	/// The camera player one is drawn through: their view's camera while split (same eye as the
	/// window camera, but a half of the window at the split view's angle rule), else the window's.
	/// Anything that culls by a frustum or projects through player one's camera uses this.
	/// </summary>
	public static Camera3D? Primary(Camera3D? window) =>
		Split && FirstViewport?.GetCamera3D() is { } first && GodotObject.IsInstanceValid(first) ? first : window;

	/// <summary>The squared distance from <paramref name="point"/> to the nearest view's eye.</summary>
	public static float NearestEyeDistanceSquared(Camera3D? primary, Vector3 point)
	{
		float best = float.MaxValue;
		foreach (Camera3D camera in Cameras(primary))
		{
			float d = camera.GlobalPosition.DistanceSquaredTo(point);
			if (d < best)
			{
				best = d;
			}
		}

		return best;
	}

	/// <summary>Whether a sphere is inside any view's frustum.</summary>
	public static bool SphereInAnyView(Camera3D? primary, Vector3 centre, float radius)
	{
		foreach (Camera3D camera in Cameras(primary))
		{
			if (SphereInView(camera, centre, radius))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>Whether a sphere is inside one camera's frustum (its planes face outward).</summary>
	public static bool SphereInView(Camera3D camera, Vector3 centre, float radius)
	{
		foreach (Plane plane in camera.GetFrustum())
		{
			if (plane.DistanceTo(centre) > radius)
			{
				return false;
			}
		}

		return true;
	}
}
