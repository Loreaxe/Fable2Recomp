using Godot;

namespace Refii.Ui;

/// <summary>
/// One camera the world sprites are projected through, and the seat they land in: the window's
/// camera on the whole window, or one player's view camera and half on split screen. Each title's
/// world-sprite layers go through it: a layer is seated with <see cref="Frame"/>'s
/// <see cref="Engine_UiSeats.Frame.Transform"/> and placed by <see cref="Place"/>, so a half is a
/// screen of its own (<see cref="Engine_UiSeats"/>) seen through its own player's camera.
/// </summary>
public sealed class Engine_HudSpriteView
{
	public Camera3D? Camera;

	/// <summary>The camera's own viewport, in pixels — what <c>UnprojectPosition</c> answers in.</summary>
	public Vector2 Viewport;

	public Engine_UiSeats.Frame Frame;

	/// <summary>
	/// Whether the scene-depth line of sight applies: a view whose depth is captured (each
	/// view's own, Engine_FxDepthCaptureEffect.View).
	/// </summary>
	public bool TestSight = true;

	/// <summary>Cached world-to-viewport projector updated once per frame.</summary>
	public readonly Refii.Core.Engine_CameraProjector Projector = new();

	/// <summary>
	/// The part of the viewport the seat shows, in viewport pixels: all of it, except through hybrid's swap,
	/// when the view draws more than its half (Engine_Views.Shown).
	/// </summary>
	public Rect2 Shown;

	/// <summary>A pixel on the camera's viewport, in the seat's own pixels.</summary>
	public Vector2 SeatPixel(Vector2 pixel)
	{
		Rect2 shown = Shown.Size.X > 0f && Shown.Size.Y > 0f ? Shown : new Rect2(Vector2.Zero, Viewport);
		return shown.Position == Vector2.Zero && shown.Size == Frame.Window || shown.Size.X <= 0f || shown.Size.Y <= 0f
			? pixel
			: (pixel - shown.Position) * (Frame.Window / shown.Size);
	}

	/// <summary>A pixel on the camera's viewport, on the window (window pixels).</summary>
	public Vector2 WindowPixel(Vector2 pixel) => Frame.Origin + SeatPixel(pixel);

	/// <summary>Whether a viewport pixel lies inside the part of the view the seat shows.</summary>
	public bool Shows(Vector2 pixel)
	{
		Rect2 shown = Shown.Size.X > 0f && Shown.Size.Y > 0f ? Shown : new Rect2(Vector2.Zero, Viewport);
		return pixel.X >= shown.Position.X && pixel.Y >= shown.Position.Y &&
			pixel.X <= shown.End.X && pixel.Y <= shown.End.Y;
	}

	/// <summary>
	/// Points both views at this frame's cameras: on the whole window <paramref name="mine"/> is the
	/// window's camera over the whole window and <paramref name="theirs"/> has none; split,
	/// <paramref name="mine"/> is player one's view camera in player one's half and
	/// <paramref name="theirs"/> player two's in player two's (Engine_Views, as GameSplitView states
	/// them). <paramref name="testSight"/>: the title captures each view's depth. Returns whether the
	/// screen is split.
	/// </summary>
	public static bool Aim(Engine_HudSpriteView mine, Engine_HudSpriteView theirs, Camera3D? windowCamera, Vector2 window,
		bool testSight)
	{
		Camera3D? one = Refii.Core.Engine_Views.FirstViewport is { } first && GodotObject.IsInstanceValid(first)
			? first.GetCamera3D()
			: null;
		bool split = Refii.Core.Engine_Views.Split && one is not null &&
			Refii.Core.Engine_Views.SecondViewport is { } second && GodotObject.IsInstanceValid(second);

		mine.Camera = split ? one : windowCamera;
		mine.Viewport = split ? (Vector2)Refii.Core.Engine_Views.FirstViewport!.Size : window;
		mine.Shown = split ? Refii.Core.Engine_Views.Shown(0, Refii.Core.Engine_Views.FirstViewport!.Size) : new Rect2(Vector2.Zero, window);
		mine.Frame = Engine_UiSeats.Of(window, split ? Engine_UiSeats.Seat.PlayerOne : Engine_UiSeats.Seat.Whole);
		mine.TestSight = testSight;

		theirs.Camera = split ? Refii.Core.Engine_Views.Second : null;
		theirs.Viewport = split ? (Vector2)Refii.Core.Engine_Views.SecondViewport!.Size : Vector2.Zero;
		theirs.Shown = split ? Refii.Core.Engine_Views.Shown(1, Refii.Core.Engine_Views.SecondViewport!.Size) : default;
		theirs.Frame = Engine_UiSeats.Of(window, Engine_UiSeats.Seat.PlayerTwo);
		theirs.TestSight = split && testSight;
		return split;
	}

	/// <summary>Takes this frame's snapshot of the camera; false with no camera to project through.</summary>
	public bool Update() => Camera is { } camera && Projector.Update(camera, Viewport, Godot.Engine.GetProcessFrames());

	/// <summary>
	/// A Godot world point's place in this view's seat, in seat canvas units, and its line of sight
	/// (uv.xy and depth on the view, or x &lt; 0 untested); null when behind the camera. A half has a
	/// neighbour, so with <paramref name="clip"/> a point outside the half's view is not drawn rather
	/// than drawn over the other player's picture; the whole window keeps no such test.
	/// </summary>
	public Engine_HudWorldSprites.Placed? Place(Vector3 world, bool clip)
	{
		if (!Update() || !Projector.TryProject(world, out Vector2 pixel, out float depth))
		{
			return null;
		}

		if (clip && !Frame.IsWhole && !Shows(pixel))
		{
			return null;
		}

		return new Engine_HudWorldSprites.Placed(
			Frame.FromPixel(SeatPixel(pixel)),
			TestSight
				? new Vector3(pixel.X / Viewport.X, pixel.Y / Viewport.Y, depth)
				: new Vector3(-1f, 0f, 0f));
	}

	/// <summary>
	/// A Godot world point in this view's seat canvas units, or null when behind the camera. On a
	/// half, a point outside the view is parked far off the canvas.
	/// </summary>
	public Vector2? Project(Vector3 world)
	{
		if (!Update() || !Projector.TryProject(world, out Vector2 pixel, out _))
		{
			return null;
		}

		if (!Frame.IsWhole && !Shows(pixel))
		{
			return new Vector2(100000f, 100000f);
		}

		return Frame.FromPixel(SeatPixel(pixel));
	}

	/// <summary>
	/// The half (0 player one's, 1 player two's) an offscreen co-op marker is drawn in on split screen, both
	/// titles: the hero marker (type 52, this machine's player one's hero) in player two's half, the
	/// henchman / partner marker (53, player two's hero) in player one's — each half shows the other
	/// player's hero. The guest decides each record against the camera of the half it is drawn in and
	/// switches it by that half's camera's GUI.SetShouldDrawPlayerIcons (Fable2 / Fable3
	/// src/hooks/src/coop_player_icons.cpp), so a stated record is drawn as stated.
	/// </summary>
	public static int PartnerMarkerHalf(uint type) => type == 52u ? 1 : 0;

	/// <summary>
	/// Whether a sprite is drawn in a half (0 player one's, 1 player two's) by the owner its guest
	/// record states as a hero's uid: only in its owner's half when that is one of this machine's two
	/// local heroes (<paramref name="one"/>, <paramref name="two"/>); a sprite that states no owner
	/// (0), or an owner neither local hero is, in every half that sees it — the one shared screen
	/// showed it to both players.
	/// </summary>
	public static bool DrawnIn(int half, ulong owner, ulong one, ulong two) =>
		owner == 0ul ? true
		: owner == one ? half == 0
		: owner == two ? half == 1
		: true;
}
