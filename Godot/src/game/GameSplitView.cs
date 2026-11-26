using Godot;
using Refii.Core;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// Local co-op on split screen: each local player's view of the one world, side by side or
/// stacked, as the player chooses (<see cref="OrientationKey"/>).
/// </summary>
/// <remarks>
/// <para>THE GUEST STATES BOTH CAMERAS. With the host's <c>coop_split_screen</c> switch on, local
/// player two's Player gets a camera manager of their own, ticked straight after player one's and
/// driven by player two's pad (Fable2/src/hooks/src/coop_hero_hooks.cpp); each manager's camera is
/// rebuilt from its statements (<see cref="Refii.Camera.Engine_CameraFeed"/>), player two's the one its
/// push keys 2. Player one's view follows <see cref="GameWorld"/>'s
/// camera, which stays the window's camera and the one every single-camera system reads; player
/// two's follows their own stated camera on the bodies' clock and rule, as player one's does.</para>
/// <para>TWO SUBVIEWPORTS OVER THE ONE WORLD. Godot renders a viewport through exactly one camera,
/// so each view is a <see cref="SubViewport"/> sharing the window's <see cref="World3D"/>, with its
/// own exposure and tone compositor. While split, the window's own 3D pass is disabled so the world
/// is drawn twice, not three times.</para>
/// <para>EACH HALF KEEPS THE GUEST'S 4:3 FRAME IN VIEW. The guest states a horizontal fov for a
/// 4:3 screen. The full window keeps the 4:3-derived vertical angle and widens with the window; a
/// half narrower than 4:3 (side by side on a widescreen) keeps the stated horizontal angle
/// instead (<see cref="Camera3D.KeepAspectEnum.Width"/>) and grows taller; a half wider than 4:3
/// (stacked) keeps the vertical angle, as the window does.</para>
/// <para>SIDE BY SIDE OR STACKED. Player one left and player two right, or player one top and
/// player two bottom. The setting is read every frame, so a change re-lays the open split out in
/// place, and the layout is stated to <see cref="Refii.Core.Engine_Views.Stacked"/> for every half
/// of the UI.</para>
/// <para>THE GUEST IS TOLD ONLY WHAT IS DRAWN. <see cref="CoopSession"/> states split screen to the
/// guest (<c>CoopSplitScreen:1</c>) only when this can draw it — a second guest camera with one
/// drawn view would leave player two walking off the screen. Both renderers draw a view per player:
/// the classic one keeps a register block per view and player two's camera selects its block by
/// <see cref="Refii.Models.Engine_ClassicViews.SecondViewLayer"/> (Engine_ClassicViews keeps the
/// split cameras' cull masks).</para>
/// </remarks>
public partial class GameSplitView : CanvasLayer
{
	/// <summary>The player's choice between split and shared screen for local co-op.</summary>
	public const string SwitchKey = "coop_split_screen";

	/// <summary>Split screen, unless the player has chosen a shared screen.</summary>
	public static bool Wanted => Refii.Core.Engine_Switches.Flag(SwitchKey, true);

	/// <summary>The player's choice of split: <c>vertical</c> (side by side) or <c>horizontal</c> (stacked).</summary>
	public const string OrientationKey = "coop_split_screen_orientation";

	/// <summary>Stacked — player one top, player two bottom — when the player has chosen a horizontal split.</summary>
	public static bool WantsStacked =>
		Refii.Core.Engine_Switches.String(OrientationKey, "vertical").Equals("horizontal", System.StringComparison.OrdinalIgnoreCase);

	/// <summary>Whether the screen is split right now.</summary>
	public static bool Active { get; private set; }

	private readonly SubViewport?[] _views = new SubViewport?[2];
	private readonly Camera3D?[] _cameras = new Camera3D?[2];
	private Control? _frame;
	private readonly Refii.Core.Engine_TickTimeline _secondLine = new();
	private bool _reported;

	public override void _Ready()
	{
		// Over the window's world and under the UI (its CanvasLayer is 5).
		Layer = 1;
	}

	public override void _ExitTree()
	{
		Collapse();
	}

	/// <summary>
	/// Called by <see cref="GameWorld"/> once its own camera is placed for the frame: splits or
	/// rejoins the screen, and places both views.
	/// </summary>
	public void Follow(Camera3D first)
	{
		HostBridge.GuestCamera? second = Engine_TitleGame.Active.LocalCoop && Wanted
			? Refii.Camera.Engine_CameraFeed.AsGuestCamera(Refii.Camera.Engine_CameraFeed.ForPlayer(2)?.Live)
			: null;
		if (second is not { IsDefault: false } stated ||
			!GameWorld.TryStatedCamera(stated, out Transform3D secondStated, out _))
		{
			Collapse();
			return;
		}

		if (!Active)
		{
			Open();
		}

		bool stacked = WantsStacked;
		if (_frame is BoxContainer box && box.Vertical != stacked)
		{
			box.Vertical = stacked;
			_reported = false;
		}

		Refii.Core.Engine_Views.SetStacked(stacked);

		Vector2 size = GetViewport().GetVisibleRect().Size;
		var half = stacked
			? new Vector2I(Mathf.Max(1, (int)size.X), Mathf.Max(1, (int)size.Y / 2))
			: new Vector2I(Mathf.Max(1, (int)size.X / 2), Mathf.Max(1, (int)size.Y));
		foreach (SubViewport? view in _views)
		{
			if (view is not null && view.Size != half)
			{
				view.Size = half;
			}
		}

		bool narrow = half.X * 3 < half.Y * 4;

		// Player one: the window camera's placement, at the stated angle.
		if (_cameras[0] is { } one)
		{
			one.GlobalTransform = first.GlobalTransform;
			Aim(one, HorizontalFromVertical(first.Fov), narrow);
		}

		// Player two: their own stated camera, on the same clock and rule as player one's.
		if (_cameras[1] is { } two)
		{
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

			two.GlobalTransform = drawn;
			if (float.IsFinite(drawnFov) && drawnFov >= 1f && drawnFov <= 179f)
			{
				Aim(two, drawnFov, narrow);
			}
		}

		if (!_reported)
		{
			_reported = true;
			GD.Print($"[split-view] split {(stacked ? "top and bottom" : "side by side")}: " +
				$"{half.X}x{half.Y} per player, {(narrow ? "horizontal" : "vertical")} angle kept");
		}
	}

	/// <summary>
	/// Points a view at the stated horizontal angle: kept as stated in a half narrower than 4:3,
	/// else as the 4:3-derived vertical angle the window keeps.
	/// </summary>
	private static void Aim(Camera3D camera, float horizontalDegrees, bool narrow)
	{
		Camera3D.KeepAspectEnum keep = narrow ? Camera3D.KeepAspectEnum.Width : Camera3D.KeepAspectEnum.Height;
		if (camera.KeepAspect != keep)
		{
			camera.KeepAspect = keep;
		}

		camera.Fov = narrow ? horizontalDegrees : VerticalFromHorizontal(horizontalDegrees);
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

	private void Open()
	{
		Viewport window = GetViewport();
		World3D world = window.World3D;

		var box = new BoxContainer
		{
			Name = "SplitViews",
			Vertical = WantsStacked,
			MouseFilter = Control.MouseFilterEnum.Ignore,
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
				RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
				Msaa3D = window.Msaa3D,
				ScreenSpaceAA = window.ScreenSpaceAA,
				UseTaa = window.UseTaa,
				UseDebanding = window.UseDebanding,
				Scaling3DMode = window.Scaling3DMode,
				Scaling3DScale = window.Scaling3DScale,
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

			var container = new SubViewportContainer
			{
				Name = player == 0 ? "PlayerOne" : "PlayerTwo",
				Stretch = true,
				SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
				SizeFlagsVertical = Control.SizeFlags.ExpandFill,
				MouseFilter = Control.MouseFilterEnum.Ignore,
			};
			container.AddChild(view);
			_frame.AddChild(container);

			_views[player] = view;
			_cameras[player] = camera;
		}

		window.Disable3D = true;
		Active = true;
		Refii.Core.Engine_Views.SetStacked(box.Vertical);
		Refii.Core.Engine_Views.SetSplit(_views[0], _views[1], _cameras[1]);
		_reported = false;
		GD.Print("[split-view] local co-op split screen: a view per player");
	}

	private void Collapse()
	{
		if (!Active && _frame is null)
		{
			return;
		}

		if (IsInsideTree())
		{
			GetViewport().Disable3D = false;
		}

		if (Active)
		{
			Refii.Core.Engine_Views.SetSplit(null, null, null);
		}

		_frame?.QueueFree();
		_frame = null;
		for (int player = 0; player < 2; ++player)
		{
			Refii.Lighting.Engine_LevelLighting.ReleaseCompositor(_cameras[player], releaseStaticFallbacks: false);
			_views[player] = null;
			_cameras[player] = null;
		}

		if (Active)
		{
			GD.Print("[split-view] shared screen again");
		}

		Active = false;
	}
}
