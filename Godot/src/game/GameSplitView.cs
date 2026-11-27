using Godot;
using Refii.Core;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// Local co-op's screen: one shared view, a view per player side by side or stacked, or hybrid — shared
/// until the heroes pass the leash's distance, then a view each (Refii.Camera.Engine_CameraScreens).
/// </summary>
/// <remarks>
/// <para>THE GUEST STATES BOTH CAMERAS. Local player two's Player gets a camera manager of their own in
/// every local co-op game, ticked straight after player one's and driven by player two's pad
/// (Fable2/src/hooks/src/coop_hero_hooks.cpp); each manager's camera is rebuilt from its statements
/// (<see cref="Refii.Camera.Engine_CameraFeed"/>), player two's the one its push keys 2. Player one's view
/// follows <see cref="GameWorld"/>'s camera, which stays the window's camera and the one every
/// single-camera system reads; player two's follows their own stated camera on the bodies' clock and
/// rule, as player one's does.</para>
/// <para>TWO SUBVIEWPORTS OVER THE ONE WORLD, BUILT ONCE. Godot renders a viewport through exactly one
/// camera, so each view is a <see cref="SubViewport"/> sharing the window's <see cref="World3D"/>, with
/// its own exposure and tone compositor. They are built the first time split or hybrid is chosen in a
/// local co-op game and kept until it ends; the screen splits by drawing them (the window's own 3D pass
/// off, so the world is drawn twice, not three times) and rejoins by drawing the window again
/// (<see cref="Refii.Core.Engine_Views.SetDrawn"/>). Neither builds or frees anything.</para>
/// <para>EACH HALF KEEPS THE GUEST'S 4:3 FRAME IN VIEW. The guest states a horizontal fov for a
/// 4:3 screen. The full window keeps the 4:3-derived vertical angle and widens with the window; a
/// half narrower than 4:3 (side by side on a widescreen) keeps the stated horizontal angle
/// instead (<see cref="Camera3D.KeepAspectEnum.Width"/>) and grows taller; a half wider than 4:3
/// (stacked) keeps the vertical angle, as the window does.</para>
/// <para>HYBRID'S SWAP. At the split each half starts as its part of the shared picture and moves to its
/// own player's over <see cref="Refii.Camera.Engine_CameraScreens.SwapSeconds"/>; the rejoin runs it back
/// and then draws the window (<see cref="Refii.Camera.Engine_CameraSwap"/>). The dedicated modes cut.
/// Through the swap each view draws the centred picture that holds its half's frustum, twice the half
/// along the divider's axis, and its half shows the frustum's part of it
/// (<see cref="Refii.Core.Engine_Views.Shown"/>). No camera is ever off-axis: Godot's volumetric fog and
/// sun cascades, and every view mirror of ours, assume a centred frustum and would draw one over the
/// picture.</para>
/// <para>THE GUEST IS TOLD THE SPLIT. <see cref="GuestSplit"/> is what the title's co-op session states
/// (<c>CoopSplitScreen:</c>): split for the whole game in split mode, hybrid's state in hybrid, never in
/// shared. Both renderers draw a view per player: the classic one keeps a register block per view and
/// player two's camera selects its block by <see cref="Refii.Models.Engine_ClassicViews.SecondViewLayer"/>
/// (Engine_ClassicViews keeps the split cameras' cull masks).</para>
/// </remarks>
public partial class GameSplitView : CanvasLayer
{
	/// <summary>The players' choice of layout for split and hybrid: <c>vertical</c> (side by side) or <c>horizontal</c> (stacked).</summary>
	public const string OrientationKey = Refii.Camera.Engine_CameraScreens.OrientationKey;

	/// <summary>Stacked — player one top, player two bottom — when the player has chosen a horizontal split.</summary>
	public static bool WantsStacked => Refii.Camera.Engine_CameraScreens.Stacked;

	/// <summary>Whether the screen is split right now (two views drawn).</summary>
	public static bool Active { get; private set; }

	/// <summary>Whether the guest is to give each player their own camera now: split mode, or hybrid while split.</summary>
	public static bool GuestSplit { get; private set; }

	/// <summary>Hybrid's decision at its last tick: split (a view each) or not. False outside hybrid.</summary>
	public static bool HybridSplit { get; private set; }

	/// <summary>The swap's progress, 0 (the shared picture) .. 1 (a picture each).</summary>
	public static float SwapProgress { get; private set; }

	/// <summary>Hybrid's last split or rejoin: which, the two stated numbers it compared, and when (ms since start).</summary>
	public readonly record struct HybridDecision(bool Split, float Distance, float Separation, ulong AtMsec);

	/// <summary>Hybrid's last decision, or null before one this session.</summary>
	public static HybridDecision? LastDecision { get; private set; }

	private readonly SubViewport?[] _views = new SubViewport?[2];
	private readonly Camera3D?[] _cameras = new Camera3D?[2];
	private readonly SubViewportContainer?[] _containers = new SubViewportContainer?[2];
	private readonly Refii.Camera.Engine_CameraSwap.Frustum?[] _swapFrustums =
		new Refii.Camera.Engine_CameraSwap.Frustum?[2];
	private Control? _frame;
	private Camera3D? _window;
	private readonly bool[] _resized = new bool[2];
	private readonly Refii.Core.Engine_TickTimeline _secondLine = new();
	private bool _reported;
	private Refii.Camera.Engine_CameraScreens.Mode? _modeWas;
	private bool _hybridSplit;
	private float _progress;

	public override void _Ready()
	{
		// Over the window's world and under the UI (its CanvasLayer is 5).
		Layer = 1;
	}

	public override void _ExitTree()
	{
		Close();
	}

	/// <summary>
	/// Called by <see cref="GameWorld"/> once its own camera is placed for the frame: splits or
	/// rejoins the screen, and places both views.
	/// </summary>
	public void Follow(Camera3D first)
	{
		_window = first;
		Refii.Lighting.Engine_TemporalHistoryEffect.Quiet();
		if (!Engine_TitleGame.Active.LocalCoop)
		{
			Close();
			GuestSplit = false;
			_modeWas = null;
			_hybridSplit = false;
			_progress = 0f;
			HybridSplit = false;
			SwapProgress = 0f;
			LastDecision = null;
			Engine_TitleGame.Active.StateCoopSplit(false);
			StateViews(first, null, sharedWithTwo: false, stacked: false);
			return;
		}

		Refii.Camera.Engine_CameraScreens.Mode mode = Refii.Camera.Engine_CameraScreens.Chosen;
		bool stacked = WantsStacked;
		if (mode != Refii.Camera.Engine_CameraScreens.Mode.Shared && _frame is null)
		{
			Open(stacked);
		}

		MatchWindow();
		_resized[0] = _resized[1] = false;

		bool hybrid = mode == Refii.Camera.Engine_CameraScreens.Mode.Hybrid;
		if (hybrid)
		{
			if (_modeWas != mode)
			{
				// Entering hybrid keeps what is drawn until the stated distance says otherwise.
				_hybridSplit = _progress >= 1f;
			}

			if (TetherStated(out float distance, out float separation))
			{
				bool was = _hybridSplit;
				_hybridSplit = Refii.Camera.Engine_CameraScreens.HybridSplits(_hybridSplit, distance, separation);
				if (_hybridSplit != was)
				{
					LastDecision = new HybridDecision(_hybridSplit, distance, separation, Time.GetTicksMsec());
					GD.Print(Refii.Camera.Engine_CameraScreens.Decision(_hybridSplit, distance, separation));
				}
			}
		}

		_modeWas = mode;
		HybridSplit = hybrid && _hybridSplit;
		GuestSplit = mode == Refii.Camera.Engine_CameraScreens.Mode.Split || hybrid && _hybridSplit;
		Engine_TitleGame.Active.StateCoopSplit(GuestSplit);

		// Player two's stated camera, on the same clock and rule as player one's.
		bool ready = SecondPose(out Transform3D twoPose, out float twoFov);
		bool apart = GuestSplit && ready;
		_progress = hybrid
			? Refii.Camera.Engine_CameraScreens.Step(_progress, apart, (float)GetProcessDeltaTime())
			: apart ? 1f : 0f;
		SwapProgress = _progress;

		if (!ready || _progress <= 0f || _frame is null)
		{
			Draw(false, mode);
			StateViews(first, null, sharedWithTwo: true, stacked);
			return;
		}

		if (_frame is BoxContainer box && box.Vertical != stacked)
		{
			box.Vertical = stacked;
			_reported = false;
		}

		Refii.Core.Engine_Views.SetStacked(stacked);

		Vector2 size = GetViewport().GetVisibleRect().Size;
		Vector2I half = HalfOf(size, stacked);
		bool narrow = Refii.Camera.Engine_CameraSwap.Narrow(half);
		float halfAspect = half.Y > 0 ? (float)half.X / half.Y : 1f;
		float windowAspect = size.Y > 0f ? size.X / size.Y : 1f;
		float weight = Refii.Camera.Engine_CameraScreens.Ease(_progress);

		// At rest each view draws its half; through the swap, the centred picture that holds its frustum.
		Vector2I drawn = weight >= 1f ? half : Refii.Camera.Engine_CameraSwap.Full(half, stacked);
		for (int player = 0; player < 2; ++player)
		{
			if (_views[player] is { } view && view.Size != drawn)
			{
				view.Size = drawn;
				_resized[player] = true;
			}
		}

		// Player one: the window camera's placement, at the stated angle; during hybrid's swap, framed
		// between its part of the window's picture and its own.
		if (_cameras[0] is { } one)
		{
			one.GlobalTransform = first.GlobalTransform;
			float horizontal = HorizontalFromVertical(first.Fov);
			if (weight >= 1f)
			{
				Aim(one, horizontal, narrow);
				Show(0, null, half, drawn, stacked);
			}
			else
			{
				Show(0, Refii.Camera.Engine_CameraSwap.Blend(
					Refii.Camera.Engine_CameraSwap.Crop(first.Fov, windowAspect, one.Near, stacked, 0),
					Refii.Camera.Engine_CameraSwap.Own(horizontal, halfAspect, one.Near, narrow), weight),
					half, drawn, stacked);
			}
		}

		// Player two: their own camera; during hybrid's swap, between player one's (the shared picture,
		// its other part) and their own.
		if (_cameras[1] is { } two)
		{
			two.GlobalTransform = Refii.Camera.Engine_CameraSwap.Pose(first.GlobalTransform, twoPose, weight);
			if (weight >= 1f)
			{
				if (float.IsFinite(twoFov) && twoFov >= 1f && twoFov <= 179f)
				{
					Aim(two, twoFov, narrow);
				}

				Show(1, null, half, drawn, stacked);
			}
			else
			{
				Show(1, Refii.Camera.Engine_CameraSwap.Blend(
					Refii.Camera.Engine_CameraSwap.Crop(first.Fov, windowAspect, two.Near, stacked, 1),
					Refii.Camera.Engine_CameraSwap.Own(twoFov, halfAspect, two.Near, narrow), weight),
					half, drawn, stacked);
			}
		}

		Draw(true, mode);
		StateViews(_cameras[0] ?? first, _cameras[1], sharedWithTwo: false, stacked);

		if (!_reported)
		{
			_reported = true;
			GD.Print($"[split-view] split {(stacked ? "top and bottom" : "side by side")}: " +
				$"{half.X}x{half.Y} per player, {(narrow ? "horizontal" : "vertical")} angle kept");
		}
	}

	/// <summary>
	/// Player two's drawn pose and horizontal angle this frame: their stated camera on its timeline, with
	/// any display offset a mod stated. False while player two has no stated camera.
	/// </summary>
	private bool SecondPose(out Transform3D pose, out float horizontalDegrees)
	{
		pose = Transform3D.Identity;
		horizontalDegrees = 0f;
		HostBridge.GuestCamera? second =
			Refii.Camera.Engine_CameraFeed.AsGuestCamera(Refii.Camera.Engine_CameraFeed.ForPlayer(2)?.Live);
		if (second is not { IsDefault: false } stated ||
			!GameWorld.TryStatedCamera(stated, out Transform3D secondStated, out _))
		{
			return false;
		}

		float horizontal = Mathf.RadToDeg(stated.HorizontalFov);
		uint tick = Refii.Core.Engine_TickClock.GuestGameTick;
		Transform3D drawn = secondStated;
		float drawnFov = horizontal;
		float teleport = Refii.Core.Engine_TickTimeline.TeleportMetres;
		// A statement only on a frame whose guest step is complete (Engine_GuestFrame.Changed),
		// for the reason GameWorld gives for player one's camera: read later in the frame than
		// its stamp, a camera from a step finishing in between is the next tick's under this
		// tick's stamp. Otherwise the timeline runs on by the motion already stated.
		if (!Refii.Core.Engine_GuestFrame.Changed && !_secondLine.Empty)
		{
			drawn = _secondLine.Advance(Refii.Entities.Engine_DrawnFrame.NowUsec,
				Refii.Entities.Engine_DrawnFrame.DrawTick);
			drawnFov = _secondLine.DrawnScalar;
		}
		else if (stated.Flag || _secondLine.Empty ||
			_secondLine.Newest.Origin.DistanceSquaredTo(secondStated.Origin) > teleport * teleport)
		{
			_secondLine.Reset(tick, secondStated, horizontal);
		}
		else
		{
			_secondLine.Observe(tick, secondStated, horizontal);
			drawn = _secondLine.Advance(Refii.Entities.Engine_DrawnFrame.NowUsec,
				Refii.Entities.Engine_DrawnFrame.DrawTick);
			drawnFov = _secondLine.DrawnScalar;
		}

		float[]? offset = HostBridge.GetCameraOffset(2);
		pose = Refii.Camera.Engine_CameraViews.Offset(drawn, offset);
		horizontalDegrees = Refii.Camera.Engine_CameraViews.OffsetFov(drawnFov, offset);
		return true;
	}

	/// <summary>
	/// The leash's distance and the heroes' head-to-head separation the title's core camera chunk last
	/// stated (Refii._coop_tether_stated: the shell's stated values 21..23). False until one is stated.
	/// </summary>
	/// <summary>
	/// The co-op screen for the debug channel's <c>camera</c> op: the mode, what is drawn, what the guest
	/// is told, hybrid's state and swap, the tether as stated now (values 21..23) with the thresholds it
	/// gives, and hybrid's last decision.
	/// </summary>
	public static System.Collections.Generic.Dictionary<string, object?> Report()
	{
		bool stated = TetherStated(out float distance, out float separation);
		return new System.Collections.Generic.Dictionary<string, object?>
		{
			["localCoop"] = Engine_TitleGame.Active.LocalCoop,
			["mode"] = Refii.Camera.Engine_CameraScreens.Name(Refii.Camera.Engine_CameraScreens.Chosen),
			["stacked"] = WantsStacked,
			["drawnSplit"] = Active,
			["guestSplit"] = GuestSplit,
			["hybridSplit"] = HybridSplit,
			["swapProgress"] = SwapProgress,
			["tetherSerial"] = HostBridge.Stated(HostBridge.StatedValue.CoopTetherSerial),
			["tetherDistance"] = stated ? distance : null,
			["tetherSeparation"] = stated ? separation : null,
			["splitAbove"] = stated ? distance : null,
			["rejoinBelow"] = stated ? Refii.Camera.Engine_CameraScreens.RejoinBelow(distance) : null,
			["lastDecision"] = LastDecision is { } last
				? new System.Collections.Generic.Dictionary<string, object?>
				{
					["split"] = last.Split,
					["distance"] = last.Distance,
					["separation"] = last.Separation,
					["msecAgo"] = Time.GetTicksMsec() - last.AtMsec,
				}
				: null,
		};
	}

	private static bool TetherStated(out float distance, out float separation)
	{
		distance = 0f;
		separation = 0f;
		if (HostBridge.Stated(HostBridge.StatedValue.CoopTetherSerial) == 0u)
		{
			return false;
		}

		distance = System.BitConverter.UInt32BitsToSingle(HostBridge.Stated(HostBridge.StatedValue.CoopTetherDistance));
		separation = System.BitConverter.UInt32BitsToSingle(HostBridge.Stated(HostBridge.StatedValue.CoopTetherSeparation));
		return true;
	}

	private static Vector2I HalfOf(Vector2 size, bool stacked) => stacked
		? new Vector2I(Mathf.Max(1, (int)size.X), Mathf.Max(1, (int)size.Y / 2))
		: new Vector2I(Mathf.Max(1, (int)size.X / 2), Mathf.Max(1, (int)size.Y));

	/// <summary>
	/// States this frame's views for the camera mod API (<see cref="Refii.Camera.Engine_CameraViews"/>):
	/// split, each player's camera over their half; shared, player one's camera over the window, and
	/// player two the same picture while they share it.
	/// </summary>
	private void StateViews(Camera3D one, Camera3D? two, bool sharedWithTwo, bool stacked)
	{
		Vector2 size = GetViewport().GetVisibleRect().Size;
		int mode = Engine_TitleGame.Active.LocalCoop
			? Refii.Camera.Engine_CameraScreens.Screen(Refii.Camera.Engine_CameraScreens.Chosen, WantsStacked)
			: Refii.Camera.Engine_CameraViews.ScreenShared;
		Refii.Camera.Engine_CameraViews.Screen(mode, two is not null);
		bool cut = Refii.Camera.Engine_CameraFeed.Drawn?.Live is { Cut: true };
		if (two is null)
		{
			Refii.Camera.Engine_CameraViews.View whole = ViewOf(one, size, new Rect2(0f, 0f, 1f, 1f), cut);
			Refii.Camera.Engine_CameraViews.Draw(1, whole);
			Refii.Camera.Engine_CameraViews.Draw(2, sharedWithTwo ? whole : null);
		}
		else
		{
			Rect2 first = stacked ? new Rect2(0f, 0f, 1f, 0.5f) : new Rect2(0f, 0f, 0.5f, 1f);
			Rect2 second = stacked ? new Rect2(0f, 0.5f, 1f, 0.5f) : new Rect2(0.5f, 0f, 0.5f, 1f);
			Vector2 half = new(size.X * first.Size.X, size.Y * first.Size.Y);
			Refii.Camera.Engine_CameraViews.Draw(1, ViewOf(one, half, first, cut, _swapFrustums[0]));
			Refii.Camera.Engine_CameraViews.Draw(2, ViewOf(two, half, second,
				Refii.Camera.Engine_CameraFeed.ForPlayer(2)?.Live is { Cut: true }, _swapFrustums[1]));
		}

		HostBridge.StateCameraViews(Refii.Camera.Engine_CameraViews.Words());
	}

	private static Refii.Camera.Engine_CameraViews.View ViewOf(Camera3D camera, Vector2 pixels, Rect2 rect, bool cut,
		Refii.Camera.Engine_CameraSwap.Frustum? swap = null)
	{
		float aspect = pixels.Y > 0f ? pixels.X / pixels.Y : 1f;
		if (swap is { } frustum)
		{
			// Hybrid's swap: the half's frustum's own extent (Engine_CameraSwap.VerticalDegrees), height kept, and
			// its centre off the camera's axis (the frustum's offset on the near plane, over the near distance).
			float vertical = Refii.Camera.Engine_CameraSwap.VerticalDegrees(frustum, camera.Near);
			Vector2 centre = camera.Near > 0f ? frustum.Offset / camera.Near : Vector2.Zero;
			return Refii.Camera.Engine_CameraViews.FromCamera(camera.GlobalTransform, vertical, true, aspect, rect, cut, centre);
		}

		return Refii.Camera.Engine_CameraViews.FromCamera(camera.GlobalTransform, camera.Fov,
			camera.KeepAspect == Camera3D.KeepAspectEnum.Height, aspect, rect, cut);
	}

	/// <summary>
	/// Points a view at the stated horizontal angle: kept as stated in a half narrower than 4:3,
	/// else as the 4:3-derived vertical angle the window keeps.
	/// </summary>
	private static void Aim(Camera3D camera, float horizontalDegrees, bool narrow)
	{
		if (camera.Projection != Camera3D.ProjectionType.Perspective)
		{
			camera.Projection = Camera3D.ProjectionType.Perspective;
		}

		Camera3D.KeepAspectEnum keep = narrow ? Camera3D.KeepAspectEnum.Width : Camera3D.KeepAspectEnum.Height;
		if (camera.KeepAspect != keep)
		{
			camera.KeepAspect = keep;
		}

		camera.Fov = narrow ? horizontalDegrees : VerticalFromHorizontal(horizontalDegrees);
	}

	/// <summary>
	/// Shows view <paramref name="view"/>'s half. At rest (<paramref name="frustum"/> null) the half is the whole
	/// view. Through hybrid's swap the view's camera draws, at <paramref name="drawn"/>, the centred picture that
	/// holds <paramref name="frustum"/> (height kept), and the half shows the frustum's part of it
	/// (Engine_CameraSwap.Shown).
	/// </summary>
	private void Show(int view, Refii.Camera.Engine_CameraSwap.Frustum? frustum, Vector2I half, Vector2I drawn, bool stacked)
	{
		Rect2I shown = new(Vector2I.Zero, drawn);
		bool swapping = frustum is { } f && float.IsFinite(f.Size) && f.Size > 0f;
		if (swapping && _cameras[view] is { } camera)
		{
			Refii.Camera.Engine_CameraSwap.Frustum picture = frustum!.Value;
			if (camera.Projection != Camera3D.ProjectionType.Perspective)
			{
				camera.Projection = Camera3D.ProjectionType.Perspective;
			}

			if (camera.KeepAspect != Camera3D.KeepAspectEnum.Height)
			{
				camera.KeepAspect = Camera3D.KeepAspectEnum.Height;
			}

			camera.Fov = Refii.Camera.Engine_CameraSwap.CoverVerticalDegrees(picture, camera.Near, stacked);
			shown = Refii.Camera.Engine_CameraSwap.Shown(picture, half, stacked);
			_swapFrustums[view] = picture;
		}
		else
		{
			swapping = false;
			_swapFrustums[view] = null;
		}

		if (_containers[view] is { } container)
		{
			Vector2 at = -(Vector2)shown.Position;
			if (container.Position != at)
			{
				container.Position = at;
			}

			if (container.Size != (Vector2)drawn)
			{
				container.Size = drawn;
			}
		}

		Refii.Core.Engine_Views.SetShown(view, swapping ? shown : null);
	}

	/// <summary>The window camera's vertical angle for a stated horizontal one: <c>2·atan(0.75·tan(h/2))</c>.</summary>
	private static float VerticalFromHorizontal(float horizontalDegrees) =>
		Mathf.RadToDeg(2f * Mathf.Atan(0.75f * Mathf.Tan(Mathf.DegToRad(horizontalDegrees) * 0.5f)));

	/// <summary>
	/// The horizontal angle the guest stated, from the vertical one the window camera carries:
	/// <c>VerticalFovDegrees</c> is <c>2·atan(0.75·tan(h/2))</c>.
	/// </summary>
	private static float HorizontalFromVertical(float verticalDegrees) =>
		Mathf.RadToDeg(2f * Mathf.Atan(Mathf.Tan(Mathf.DegToRad(verticalDegrees) * 0.5f) / 0.75f));

	/// <summary>
	/// Builds the two views over the window's world, not drawn yet: kept until local co-op ends, so every
	/// later split and rejoin only switches which picture is drawn.
	/// </summary>
	private void Open(bool stacked)
	{
		Viewport window = GetViewport();
		World3D world = window.World3D;
		Vector2I half = HalfOf(window.GetVisibleRect().Size, stacked);

		var box = new BoxContainer
		{
			Name = "SplitViews",
			Vertical = stacked,
			MouseFilter = Control.MouseFilterEnum.Ignore,
			Visible = false,
		};
		box.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		box.AddThemeConstantOverride("separation", 0);
		_frame = box;
		AddChild(_frame);

		for (int player = 0; player < 2; ++player)
		{
			var view = new SubViewport
			{
				Name = player == 0 ? "PlayerOneView" : "PlayerTwoView",
				World3D = world,
				OwnWorld3D = false,
				TransparentBg = false,
				HandleInputLocally = false,
				RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
				Size = half,
				Msaa3D = window.Msaa3D,
				ScreenSpaceAA = window.ScreenSpaceAA,
				UseTaa = window.UseTaa,
				UseDebanding = window.UseDebanding,
				Scaling3DMode = window.Scaling3DMode,
				Scaling3DScale = window.Scaling3DScale,
				FsrSharpness = window.FsrSharpness,
				PositionalShadowAtlasSize = window.PositionalShadowAtlasSize,
				PositionalShadowAtlas16Bits = window.PositionalShadowAtlas16Bits,
				MeshLodThreshold = window.MeshLodThreshold,
			};
			for (int quadrant = 0; quadrant < 4; ++quadrant)
			{
				view.SetPositionalShadowAtlasQuadrantSubdiv(quadrant, window.GetPositionalShadowAtlasQuadrantSubdiv(quadrant));
			}

			var camera = new Camera3D
			{
				Name = player == 0 ? "PlayerOneCamera" : "PlayerTwoCamera",
				Near = 0.05f,
				Far = 20_000f,
				KeepAspect = Camera3D.KeepAspectEnum.Width,
				Attributes = Refii.Lighting.Engine_LevelLighting.BuildCameraAttributes(),
				Compositor = Refii.Lighting.Engine_LevelLighting.BuildCompositor(player),
			};
			camera.Current = true;
			view.AddChild(camera);
			Refii.Lighting.Engine_DepthPrepassMarker.Attach(camera);

			// The half clips its view: at rest the view is the half; through hybrid's swap it is twice the half
			// along the divider's axis and slides under it (Show).
			var seat = new Control
			{
				Name = player == 0 ? "PlayerOne" : "PlayerTwo",
				ClipContents = true,
				SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
				SizeFlagsVertical = Control.SizeFlags.ExpandFill,
				MouseFilter = Control.MouseFilterEnum.Ignore,
			};
			var container = new SubViewportContainer
			{
				Name = "View",
				Stretch = false,
				MouseFilter = Control.MouseFilterEnum.Ignore,
				Size = half,
			};
			container.AddChild(view);
			seat.AddChild(container);
			_frame.AddChild(seat);

			_views[player] = view;
			_cameras[player] = camera;
			_containers[player] = container;
		}

		Active = false;
		Refii.Core.Engine_Views.SetStacked(box.Vertical);
		Refii.Core.Engine_Views.SetSplit(_views[0], _views[1], _cameras[1]);
		_reported = false;
		GD.Print("[split-view] views open: a view per player, kept for this local co-op game");
	}

	/// <summary>
	/// Draws the two open views (the window's own 3D pass off) or the window again; switches only what is
	/// drawn. <paramref name="mode"/> names the screen in the log.
	/// </summary>
	private void Draw(bool on, Refii.Camera.Engine_CameraScreens.Mode mode)
	{
		if (_frame is null)
		{
			Active = false;
			return;
		}

		if (on == Active)
		{
			return;
		}

		_frame.Visible = on;
		foreach (SubViewport? view in _views)
		{
			if (view is not null)
			{
				view.RenderTargetUpdateMode = on ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
			}
		}

		GetViewport().Disable3D = on;
		Active = on;
		if (on)
		{
			for (int player = 0; player < 2; ++player)
			{
				Resume(_cameras[player], _window, _resized[player]);
			}
		}
		else
		{
			Resume(_window, _cameras[0], buffersFresh: false);
		}
		if (!on)
		{
			Refii.Core.Engine_Views.SetShown(0, null);
			Refii.Core.Engine_Views.SetShown(1, null);
		}

		Refii.Core.Engine_Views.SetDrawn(on);
		GD.Print(on
			? $"[split-view] drawn: a view per player ({Refii.Camera.Engine_CameraScreens.Name(mode)})"
			: $"[split-view] drawn: one shared view ({Refii.Camera.Engine_CameraScreens.Name(mode)})");
	}

	/// <summary>
	/// <paramref name="camera"/>'s view draws again this frame, continuing <paramref name="from"/>'s picture:
	/// its temporal history starts over (Engine_TemporalHistoryEffect) and its tone history is
	/// <paramref name="from"/>'s (Engine_PostFxEffect.Continue). Only this class asks, so a single-player
	/// camera never does.
	/// </summary>
	private static void Resume(Camera3D? camera, Camera3D? from, bool buffersFresh)
	{
		Refii.Lighting.Engine_TemporalHistoryEffect.Drop(camera, buffersFresh);
		Refii.Lighting.Engine_PostFxEffect.Continue(from, camera);
	}

	/// <summary>
	/// The views draw at the window's 3D quality, as the player sets it now: a change in the settings while
	/// they exist reaches them. Each value is written only when it differs (Viewport.set_scaling_3d_scale
	/// and set_mesh_lod_threshold pass every write on to the server).
	/// </summary>
	private void MatchWindow()
	{
		if (_frame is null)
		{
			return;
		}

		Viewport window = GetViewport();
		foreach (SubViewport? view in _views)
		{
			if (view is null)
			{
				continue;
			}

			if (view.Msaa3D != window.Msaa3D)
			{
				view.Msaa3D = window.Msaa3D;
			}

			if (view.ScreenSpaceAA != window.ScreenSpaceAA)
			{
				view.ScreenSpaceAA = window.ScreenSpaceAA;
			}

			if (view.UseTaa != window.UseTaa)
			{
				view.UseTaa = window.UseTaa;
			}

			if (view.UseDebanding != window.UseDebanding)
			{
				view.UseDebanding = window.UseDebanding;
			}

			if (view.Scaling3DMode != window.Scaling3DMode)
			{
				view.Scaling3DMode = window.Scaling3DMode;
			}

			if (view.Scaling3DScale != window.Scaling3DScale)
			{
				view.Scaling3DScale = window.Scaling3DScale;
			}

			if (view.FsrSharpness != window.FsrSharpness)
			{
				view.FsrSharpness = window.FsrSharpness;
			}

			if (view.MeshLodThreshold != window.MeshLodThreshold)
			{
				view.MeshLodThreshold = window.MeshLodThreshold;
			}
		}
	}

	/// <summary>Frees the two views (local co-op ended, or the world goes).</summary>
	private void Close()
	{
		if (_frame is null)
		{
			Active = false;
			return;
		}

		if (IsInsideTree())
		{
			GetViewport().Disable3D = false;
		}

		if (Active)
		{
			// Ahead of the halves' release: the window takes player one's tone history before it is freed.
			Resume(_window, _cameras[0], buffersFresh: false);
		}

		Refii.Core.Engine_Views.SetSplit(null, null, null);
		_frame.QueueFree();
		_frame = null;
		for (int player = 0; player < 2; ++player)
		{
			Refii.Lighting.Engine_LevelLighting.ReleaseCompositor(_cameras[player], releaseStaticFallbacks: false);
			_views[player] = null;
			_cameras[player] = null;
			_containers[player] = null;
			_swapFrustums[player] = null;
		}

		Active = false;
		_secondLine.Clear();
		GD.Print("[split-view] views closed");
	}
}
