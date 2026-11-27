using Godot;
using Refii.Core;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// The world, rendered by GODOT through the guest's camera.
///
/// This is the plan's phase 1 in its smallest form: the guest simulates and tells us where
/// it is looking; Godot draws. Proving the camera first is deliberate — it is nearly free,
/// it needs no entity sync, and nothing else in the world feed is visible until something
/// is looking at it.
/// </summary>
public partial class GameWorld : Node3D
{
	/// <summary>Furthest the camera may sit from the origin before its depths can overflow.</summary>
	private const float MaxCameraReach = 1_000_000f;

	/// <summary>Camera snapshots refused as not finite. Run total; a torn read is expected at some rate.</summary>
	public static int NonFiniteCameras;

	/// <summary>Frames the camera was blended between consecutive stated cameras.</summary>
	public static long BlendedCameras;

	/// <summary>Frames it stood on the live camera — a stated snap, or no pair to blend.</summary>
	public static long LiveOnlyCameras;

	/// <summary>The mouse's turn drawn ahead of the guest's camera last frame (posted, not yet taken), degrees rightward.</summary>
	public static double MouseLookOutstanding { get; private set; }

	/// <summary>The pitch drawn ahead last frame (the ranged aim only), degrees upward, after the elevation bound.</summary>
	public static double MouseLookOutstandingPitch { get; private set; }

	/// <summary>How the guest's camera turns for the mouse (an Engine_MouseLook mode), and its pivot as stated.</summary>
	public static int MouseLookMode { get; private set; }

	public static Vector3 MouseLookPivot { get; private set; }

	/// <summary>Everything the guest's camera has turned and pitched for the mouse, degrees. Run totals.</summary>
	public static double MouseLookAbsorbed { get; private set; }

	public static double MouseLookAbsorbedPitch { get; private set; }

	/// <summary>
	/// The ranged aim whose reticule turns with the view (the aim mode), and this frame's turn drawn
	/// ahead of the guest's camera: its pivot (Godot space) and rotation. The reticule's anchor is
	/// turned by it (<see cref="TurnAimAhead"/>), as the guest's next update will turn the aim.
	/// </summary>
	public static uint MouseLookAim { get; private set; }

	/// <summary>True while the host owns ranged aiming orientation at display rate.</summary>
	public static bool HostAimActive { get; private set; }

	/// <summary>Guest ranged aim object currently driven by host aim, or 0 when inactive.</summary>
	public static uint HostAimObject { get; private set; }

	/// <summary>Current vertical FOV during ranged aiming (0 outside aim mode), used for ADS zoom scaling.</summary>
	public static float ActiveAimFov { get; private set; }

	/// <summary>Latest whole read of the guest's mouse-look account.</summary>
	public static HostBridge.MouseLookState? LastMouseLook { get; private set; }

	/// <summary>True when host aim owns <paramref name="aimObject"/> this frame.</summary>
	public static bool IsHostAiming(uint aimObject) =>
		HostAimActive && aimObject != 0u && aimObject == HostAimObject;

	/// <summary>The sequence of the mouse account the camera was last drawn from (a whole read).</summary>
	public static uint MouseLookSequence { get; private set; }

	private static Vector3 _aheadPivot;
	private static Basis _aheadTurn = Basis.Identity;

	/// <summary>A guest-space point of <see cref="MouseLookAim"/> turned by the turn drawn ahead this frame.</summary>
	public static Vector3 TurnAimAhead(Vector3 guest)
	{
		if (MouseLookAim == 0u || _aheadTurn.IsEqualApprox(Basis.Identity))
		{
			return guest;
		}

		var godot = new Vector3(-guest.X, guest.Z, guest.Y);
		Vector3 turned = _aheadPivot + (_aheadTurn * (godot - _aheadPivot));
		return new Vector3(-turned.X, turned.Z, turned.Y);
	}

	/// <summary>Target switches the mouse handed to a target lock, as last read. Run total.</summary>
	public static uint MouseLookFlicks { get; private set; }

	/// <summary>Frames whose read of the guest's mouse account straddled a camera update. Run total.</summary>
	public static long MouseLookTornReads;

	private readonly Refii.Core.Engine_MouseLook _mouseLook = new();
	private readonly Engine_HostAimCamera _hostAimCamera = new();
	private Viewport? _cachedRootViewport;
	private bool _cameraDirty = true;
	private Transform3D _lastCameraTransform;
	private float _lastCameraFov;
	private Camera3D.KeepAspectEnum _lastKeepAspect = (Camera3D.KeepAspectEnum)(-1);

	/// <summary>The camera timeline's own drawn transform last frame, before the mouse's outstanding turn.</summary>
	private Transform3D _lineDrawn = Transform3D.Identity;

	/// <summary>True when a 3D world region is mounted and active for rendering.</summary>
	public static bool HasActiveWorld { get; private set; }

	/// <summary>True once world streaming and queued worker GPU uploads have finished and settle/warmup frames may render.</summary>
	public static bool ReadyForWarmupAndSettle { get; private set; }

	private static int _activeWorldFrames;
	private static int _settleFrames;

	/// <summary>Frames elapsed since the active 3D world region mounted, read safely from the render thread.</summary>
	public static int ActiveWorldFrames => System.Threading.Volatile.Read(ref _activeWorldFrames);

	/// <summary>Frames elapsed since <see cref="ReadyForWarmupAndSettle"/> became true, read safely from the render thread.</summary>
	public static int SettleFrames => System.Threading.Volatile.Read(ref _settleFrames);

	/// <summary>True when the root 3D viewport should render the 3D world (including behind the loading cover so scene PSOs warm incrementally during streaming).</summary>
	public static bool ShouldEnableSceneRendering(bool hasActiveWorld, bool coverHeld, bool readyForWarmupAndSettle) =>
		hasActiveWorld;

	/// <summary>True when root 3D quality upscaling/AA (FSR2, FXAA, MSAA) should be active for the world.</summary>
	public static bool ShouldEnableWorldQualityAndEffects(bool hasActiveWorld, bool coverHeld, bool readyForWarmupAndSettle) =>
		hasActiveWorld && (!coverHeld || readyForWarmupAndSettle);

	/// <summary>
	/// Decides whether the guest's entities for a newly built destination are activated and safe to mount against.
	/// On ABI &gt;= 50 (<paramref name="worldActiveSupported"/>), <paramref name="worldActive"/> (<c>World+0xA8</c>) is
	/// authoritative because <paramref name="worldReady"/> (<c>game+0x105</c>) remains stale-true throughout State 2
	/// and State 3's blocking load before clearing at the end of State 3.
	/// </summary>
	public static bool AreGuestEntitiesReadyForMount(
		bool worldActive,
		bool worldReady,
		bool worldActiveSupported,
		bool hasFullLoadInFlight,
		int inFlightState,
		int waitFrames,
		int waitLimit)
	{
		if (waitFrames >= waitLimit)
		{
			return true;
		}

		if (hasFullLoadInFlight && inFlightState < 5)
		{
			return false;
		}

		if (worldActiveSupported)
		{
			return worldActive || (hasFullLoadInFlight && inFlightState >= 5);
		}

		return worldActive || worldReady || (hasFullLoadInFlight && inFlightState >= 5);
	}

	/// <summary>
	/// Bitmask for <c>dx12_skip_3d</c> / <c>REFII_DX12_SKIP_3D</c> isolation bisection
	/// (comma-separated: <c>terrain</c>, <c>props</c>, <c>entities</c>, <c>hero</c>, <c>fx</c>, <c>environment</c>,
	/// <c>water</c>, <c>reflections</c>, <c>ssao</c>, <c>sunshadows</c>, <c>lights</c>,
	/// <c>depthprepass</c>, <c>hud3d</c>, <c>skydome</c>, <c>clouds</c>, <c>all</c>).
	/// </summary>
	[System.Flags]
	public enum Dx12Skip3dMask
	{
		None = 0,
		Terrain = 1 << 0,
		Props = 1 << 1,
		Entities = 1 << 2,
		Fx = 1 << 3,
		Environment = 1 << 4,
		Water = 1 << 5,
		Reflections = 1 << 6,
		Ssao = 1 << 7,
		SunShadows = 1 << 8,
		Lights = 1 << 9,
		DepthPrepass = 1 << 10,
		Hud3d = 1 << 11,
		SkyDome = 1 << 12,
		Clouds = 1 << 13,
		Hero = 1 << 14,
		All = Terrain | Props | Entities | Fx | Environment | Water | Reflections |
		      Ssao | SunShadows | Lights | DepthPrepass | Hud3d | SkyDome | Clouds | Hero,
	}

	private static Dx12Skip3dMask _appliedSkip3dMask;

	/// <summary>Active <c>dx12_skip_3d</c> mask applied on the most recent mount.</summary>
	public static Dx12Skip3dMask ActiveDx12Skip3dMask => _appliedSkip3dMask;

	/// <summary>Parses a comma/whitespace-separated <c>dx12_skip_3d</c> subsystem specification.</summary>
	public static Dx12Skip3dMask ParseDx12Skip3d(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return Dx12Skip3dMask.None;
		}

		Dx12Skip3dMask mask = Dx12Skip3dMask.None;
		string[] tokens = raw.Split(new[] { ',', ';', '|', ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
		foreach (string token in tokens)
		{
			switch (token.Trim().ToLowerInvariant())
			{
				case "terrain":
					mask |= Dx12Skip3dMask.Terrain;
					break;
				case "props":
					mask |= Dx12Skip3dMask.Props;
					break;
				case "entities":
					mask |= Dx12Skip3dMask.Entities;
					break;
				case "hero":
					mask |= Dx12Skip3dMask.Hero;
					break;
				case "fx":
					mask |= Dx12Skip3dMask.Fx;
					break;
				case "environment":
				case "env":
				case "sky":
					mask |= Dx12Skip3dMask.Environment;
					break;
				case "skydome":
					mask |= Dx12Skip3dMask.SkyDome;
					break;
				case "clouds":
					mask |= Dx12Skip3dMask.Clouds;
					break;
				case "water":
					mask |= Dx12Skip3dMask.Water;
					break;
				case "reflections":
				case "reflection":
					mask |= Dx12Skip3dMask.Reflections;
					break;
				case "ssao":
					mask |= Dx12Skip3dMask.Ssao;
					break;
				case "sunshadows":
				case "sun_shadows":
				case "sunshadow":
					mask |= Dx12Skip3dMask.SunShadows;
					break;
				case "lights":
				case "placedlights":
				case "placed_lights":
					mask |= Dx12Skip3dMask.Lights;
					break;
				case "depthprepass":
				case "depth_prepass":
				case "prepass":
					mask |= Dx12Skip3dMask.DepthPrepass;
					break;
				case "hud3d":
				case "hud":
					mask |= Dx12Skip3dMask.Hud3d;
					break;
				case "all":
					mask |= Dx12Skip3dMask.All;
					break;
			}
		}

		return mask;
	}

	private static Dx12Skip3dMask ResolveDx12Skip3d()
	{
		string? env = System.Environment.GetEnvironmentVariable("REFII_DX12_SKIP_3D");
		if (!string.IsNullOrWhiteSpace(env))
		{
			return ParseDx12Skip3d(env);
		}

		System.Collections.Generic.IReadOnlyDictionary<string, string> cfg = Refii.Core.Engine_RefiiConfig.Read(out _);
		string configured = Refii.Core.Engine_RefiiConfig.String(cfg, "dx12_skip_3d", string.Empty);
		return ParseDx12Skip3d(configured);
	}

	private void ApplyDx12Skip3d(Node3D region, Dx12Skip3dMask mask)
	{
		Dx12Skip3dMask previous = _appliedSkip3dMask;
		_appliedSkip3dMask = mask;

		if ((mask & Dx12Skip3dMask.Ssao) != 0)
		{
			Refii.Lighting.Engine_LevelLighting.SsaoOverride = false;
		}
		else if ((previous & Dx12Skip3dMask.Ssao) != 0)
		{
			Refii.Lighting.Engine_LevelLighting.SsaoOverride = null;
		}

		if ((mask & Dx12Skip3dMask.SunShadows) != 0)
		{
			Refii.Lighting.Engine_LevelLighting.SunShadowOverride = false;
		}
		else if ((previous & Dx12Skip3dMask.SunShadows) != 0)
		{
			Refii.Lighting.Engine_LevelLighting.SunShadowOverride = null;
		}

		if ((mask & Dx12Skip3dMask.Lights) != 0)
		{
			Refii.Lighting.Engine_LightShadowBudget.LightsEnabled = false;
		}
		else if ((previous & Dx12Skip3dMask.Lights) != 0)
		{
			Refii.Lighting.Engine_LightShadowBudget.LightsEnabled = true;
		}

		if ((mask & Dx12Skip3dMask.DepthPrepass) != 0)
		{
			Refii.Lighting.Engine_DepthPrepassMarker.Enabled = false;
		}
		else if ((previous & Dx12Skip3dMask.DepthPrepass) != 0)
		{
			Refii.Lighting.Engine_DepthPrepassMarker.Enabled = true;
		}

		if ((mask & Dx12Skip3dMask.Hud3d) != 0)
		{
			Refii.Ui.Engine_HudView.Enabled = false;
			Refii.Ui.Engine_HudWorldSprites.Suppressed = true;
		}
		else if ((previous & Dx12Skip3dMask.Hud3d) != 0)
		{
			Refii.Ui.Engine_HudView.Enabled = true;
			Refii.Ui.Engine_HudWorldSprites.Suppressed = false;
		}

		if ((mask & Dx12Skip3dMask.Reflections) != 0 && _reflections is not null)
		{
			_reflections.DisableAll();
		}

		GameEntities.SkipHeroRendering = (mask & Dx12Skip3dMask.Hero) != 0;
		if ((mask & Dx12Skip3dMask.Fx) != 0)
		{
			Refii.Fx.Engine_FxDisplacement.Current?.SetEnabled(false);
		}
		else if ((previous & Dx12Skip3dMask.Fx) != 0)
		{
			Refii.Fx.Engine_FxDisplacement.Current?.SetEnabled(true);
		}
		if (_entities is not null)
		{
			_entities.Visible = (mask & Dx12Skip3dMask.Entities) == 0;
		}
		if (_fx is not null)
		{
			_fx.Visible = (mask & Dx12Skip3dMask.Fx) == 0;
		}

		if (mask == Dx12Skip3dMask.None)
		{
			return;
		}

		ApplySkipToSubtree(region, mask);
		GD.Print($"[game-world] dx12_skip_3d active: {mask}");
	}

	private static void ApplySkipToSubtree(Node root, Dx12Skip3dMask mask)
	{
		foreach (Node child in root.GetChildren())
		{
			string name = child.Name.ToString();
			bool skipSky = (mask & (Dx12Skip3dMask.Environment | Dx12Skip3dMask.SkyDome)) != 0;
			bool skipClouds = (mask & (Dx12Skip3dMask.Environment | Dx12Skip3dMask.Clouds)) != 0;
			bool hide =
				((mask & Dx12Skip3dMask.Terrain) != 0 &&
					(name == Refii.Level.Engine_LevelScene.TerrainNodeName ||
					 name.StartsWith(Refii.Level.Engine_LevelScene.TerrainNodeName + "_", System.StringComparison.Ordinal) ||
					 name.StartsWith(Refii.Level.Engine_LevelScene.AdjacentNodeName, System.StringComparison.Ordinal))) ||
				((mask & Dx12Skip3dMask.Props) != 0 && name == Refii.Level.Engine_LevelProps.NodeName) ||
				((mask & Dx12Skip3dMask.Entities) != 0 && name == Refii.Entities.Engine_LevelEntities.NodeName) ||
				((mask & Dx12Skip3dMask.Water) != 0 && name == Refii.Level.Engine_LevelScene.WaterNodeName) ||
				((mask & Dx12Skip3dMask.Fx) != 0 && name == "Mist") ||
				((mask & Dx12Skip3dMask.SunShadows) != 0 && child is DirectionalLight3D) ||
				((mask & Dx12Skip3dMask.Lights) != 0 && child is OmniLight3D or SpotLight3D) ||
				(skipSky &&
					(name == Refii.Level.Engine_SkyEnvironment.NodeName ||
					 name == Refii.Level.Engine_CelestialElements.NodeName)) ||
				(skipClouds && name == "Clouds") ||
				((mask & Dx12Skip3dMask.Environment) != 0 &&
					(child is WorldEnvironment ||
					 child is DirectionalLight3D ||
					 name == Refii.Level.Engine_DynamicPrecipitation.NodeName ||
					 name == Refii.Level.Engine_DynamicCloudShadows.NodeName ||
					 name == "Weather"));

			if (hide && child is DirectionalLight3D dirLight && (mask & Dx12Skip3dMask.Environment) == 0)
			{
				dirLight.ShadowEnabled = false;
			}
			else if (hide && child is Node3D n3d)
			{
				n3d.Visible = false;
			}
			else if (hide && child is WorldEnvironment envNode)
			{
				envNode.Environment = null;
			}

			if (child.GetChildCount() > 0 &&
			    (mask & (Dx12Skip3dMask.Lights | Dx12Skip3dMask.SunShadows | Dx12Skip3dMask.SkyDome | Dx12Skip3dMask.Clouds)) != 0)
			{
				ApplySkipToSubtree(child, mask);
			}
		}
	}

	/// <summary>True when render-thread compositor effects (DepthCapture / PostFX) should execute this frame.</summary>
	public static bool ShouldRunRenderEffects(
		bool hasActiveWorld, bool coverHeld, bool readyForWarmupAndSettle, int settleFrames, int requiredSettleFrames) =>
		hasActiveWorld && (!coverHeld || (readyForWarmupAndSettle && settleFrames >= requiredSettleFrames));

	private Camera3D _camera = null!;
	private GameSplitView _split = null!;
	private System.Func<string>? _preDrawDetailProvider;
	private bool _reported;
	public override void _Ready()
	{
		HasActiveWorld = false;
		ReadyForWarmupAndSettle = false;
		System.Threading.Interlocked.Exchange(ref _activeWorldFrames, 0);
		System.Threading.Interlocked.Exchange(ref _settleFrames, 0);
		_camera = new Camera3D
		{
			Name = "GuestCamera",
			Near = 0.05f,
			Far = 20_000f,

			// The guest auto-exposes; a constant cannot follow a scene it was not measured on.
			Attributes = Refii.Lighting.Engine_LevelLighting.BuildCameraAttributes(),

			// The guest's tone curve, measured per frame. Godot's tonemapper is Linear
			// wherever this runs — see Engine_LevelLighting.ConfigureEnvironment.
			Compositor = Refii.Lighting.Engine_LevelLighting.BuildCompositor(),
		};

		AddChild(_camera);
		Refii.Lighting.Engine_DepthPrepassMarker.Attach(_camera);

		// After the add, and explicitly. Godot renders a viewport through exactly one
		// current camera, and this is the window's — the front end renders into a viewport
		// of its own precisely so that it cannot take this one away, which it used to do
		// every time a widget changed state.
		_camera.MakeCurrent();

		// Local co-op on split screen: a view per player over this world (GameSplitView).
		_split = new GameSplitView { Name = "SplitView" };
		AddChild(_split);

		_preDrawDetailProvider = DescribePreDrawState;
		Refii.Core.Engine_FramePhases.PreDrawDetailProvider = _preDrawDetailProvider;
		Refii.Core.Engine_RdHealth.RegisterDeviceLostHandler(OnDeviceLost);
	}

	private void OnDeviceLost()
	{
		if (_reflections is not null && GodotObject.IsInstanceValid(_reflections))
		{
			_reflections.DisableAll();
		}
	}

	private string DescribePreDrawState()
	{
		Viewport? vp = GetViewport();
		WorldEnvironment? worldEnv = _region?.GetNodeOrNull<WorldEnvironment>(Refii.Level.Engine_SkyEnvironment.NodeName);
		bool ssao = worldEnv?.Environment?.SsaoEnabled ?? false;
		bool sdfgi = worldEnv?.Environment?.SdfgiEnabled ?? false;
		bool sunShadow = false;
		if (_region is not null)
		{
			foreach (Node child in _region.GetChildren())
			{
				if (child is DirectionalLight3D dir)
				{
					sunShadow = dir.ShadowEnabled;
					break;
				}
			}
		}
		int posAtlas = vp is not null && GodotObject.IsInstanceValid(vp) ? vp.PositionalShadowAtlasSize : -1;
		int reflBodies = _reflections is not null && GodotObject.IsInstanceValid(_reflections) ? _reflections.BodyCount : 0;
		Vector3 cam = _camera is not null && GodotObject.IsInstanceValid(_camera) ? _camera.GlobalPosition : Vector3.Zero;
		return $"driver={RenderingServer.GetCurrentRenderingDriverName()} coverHeld={CoverHeld} worldReady={ReadyForWarmupAndSettle} " +
			$"skip3d={_appliedSkip3dMask} sunShadow={sunShadow} ssao={ssao} sdfgi={sdfgi} posAtlas={posAtlas} " +
			$"depthMarker={Refii.Lighting.Engine_DepthPrepassMarker.EffectiveVisible} refl={reflBodies} " +
			$"placedLights={(Refii.Lighting.Engine_LightShadowBudget.LightsEnabled ? "on" : "off")} " +
			$"cam=({cam.X:0.0},{cam.Y:0.0},{cam.Z:0.0})";
	}

	public override void _ExitTree()
	{
		EndHostAim(forwardResidualDelta: false);
		_cameraDirty = true;
		_cachedRootViewport = null;
		if (_preDrawDetailProvider is not null &&
		    ReferenceEquals(Refii.Core.Engine_FramePhases.PreDrawDetailProvider, _preDrawDetailProvider))
		{
			Refii.Core.Engine_FramePhases.PreDrawDetailProvider = null;
		}
		_preDrawDetailProvider = null;
		Refii.Core.Engine_RdHealth.RegisterDeviceLostHandler(null);
		Refii.Lighting.Engine_LevelLighting.ReleaseCompositor(_camera, releaseStaticFallbacks: true);
		if (GetViewport() is { } rootViewport)
		{
			rootViewport.Disable3D = false;
		}
	}

	private void ApplyCameraToGodot(Transform3D transform, float fov)
	{
		// A mod's display offset (camera_offset) moves the picture only: everything above keeps the
		// stated camera, and the guest is never told.
		float[]? offset = HostBridge.GetCameraOffset(1);
		transform = Refii.Camera.Engine_CameraViews.Offset(transform, offset);
		fov = Refii.Camera.Engine_CameraViews.OffsetFov(fov, offset);
		if (_cameraDirty || !_lastCameraTransform.IsEqualApprox(transform))
		{
			_camera.GlobalTransform = transform;
			_lastCameraTransform = transform;
		}

		if (float.IsFinite(fov) && fov >= 1f && fov <= 179f &&
			(_cameraDirty || !Mathf.IsEqualApprox(_lastCameraFov, fov)))
		{
			_camera.Fov = fov;
			_lastCameraFov = fov;
		}

		if (_cameraDirty || _lastKeepAspect != Camera3D.KeepAspectEnum.Height)
		{
			_camera.KeepAspect = Camera3D.KeepAspectEnum.Height;
			_lastKeepAspect = Camera3D.KeepAspectEnum.Height;
		}

		_cameraDirty = false;
	}

	private void EndHostAim(bool forwardResidualDelta)
	{
		bool wasActive = _hostAimCamera.Active || HostAimActive;
		_hostAimCamera.End();
		HostBridge.SetHostAim(false, Vector3.Zero);
		HostAimActive = false;
		HostAimObject = 0u;
		ActiveAimFov = 0f;

		Vector2 residual = GameInput.TakeHostAimDelta();
		if (forwardResidualDelta && wasActive && residual != Vector2.Zero)
		{
			HostBridge.PostMouseLook(residual.X, residual.Y);
		}
	}

	private void EvaluateAndApplyHostAim(double delta, uint cameraTick, bool hasPair)
	{
		Vector2 hostDelta = GameInput.TakeHostAimDelta();
		_hostAimCamera.ApplyMouseDelta(hostDelta.X, hostDelta.Y);
		Transform3D aimDrawn = _hostAimCamera.Evaluate(
			Refii.Entities.Engine_DrawnFrame.NowUsec,
			Refii.Entities.Engine_DrawnFrame.DrawTick,
			delta,
			out float aimFov,
			out Vector3 guestForward);

		ActiveAimFov = aimFov;
		HostBridge.SetHostAim(true, guestForward);
		_cameraLine.Reset(cameraTick, aimDrawn, aimFov);
		_displayPair = false;
		_lineDrawn = aimDrawn;
		_aheadTurn = Basis.Identity;
		++BlendedCameras;

		ApplyCameraToGodot(aimDrawn, aimFov);
		SampleCameraTrace(aimDrawn.Origin, CameraObserved, cameraTick);
		_split.Follow(_camera);

		if (!_reported)
		{
			_reported = true;
			GD.Print($"[game-world] driving Godot's camera from host aim: " +
				$"pos={aimDrawn.Origin} fov={_camera.Fov:F1}deg " +
				$"pair={(hasPair ? "yes" : "no")}");
			Refii.Core.Engine_FramePhases.ArmDrawTrace("camera-drive", 5);
		}
	}

	public override void _Process(double delta)
	{
		using (Refii.Core.Engine_FramePhases.Measure("world_process"))
		{
			world_processBody(delta);
		}
	}

	/// <summary>The mouse's account read just before and after this frame's camera statements were taken.</summary>
	private HostBridge.MouseLookState? _cameraLookBefore;
	private HostBridge.MouseLookState? _cameraLookAfter;

	private void world_processBody(double delta)
	{
		if (HostBridge.TryGetAbiVersion() is null)
		{
			return;
		}

		// The camera statements are taken every frame, world up or not, so the rebuild never
		// misses an update (Engine_CameraFeed). The mouse's account is read on both sides of the
		// take: the camera's read is the take, and a turn in flight across it is a torn read below.
		_cameraLookBefore = HostBridge.GetMouseLook();
		Refii.Camera.Engine_CameraFeed.Pump();
		_cameraLookAfter = HostBridge.GetMouseLook();
		if (Refii.Camera.Engine_CameraFeed.AsGuestCamera(Refii.Camera.Engine_CameraFeed.Drawn?.Live) is { } drawnCamera)
		{
			HostBridge.StateCamera(drawnCamera);
		}

		if (!PumpRegionRetirement()) return;

		FollowGuestIntoTheWorld();
		if (HasActiveWorld)
		{
			System.Threading.Interlocked.Increment(ref _activeWorldFrames);
		}
		ReleaseWhenPropsHaveStreamed();
		if (_cachedRootViewport is null || !GodotObject.IsInstanceValid(_cachedRootViewport))
		{
			_cachedRootViewport = GetViewport();
		}
		if (_cachedRootViewport is { } rootViewport)
		{
			bool disable3D = !ShouldEnableSceneRendering(HasActiveWorld, CoverHeld, ReadyForWarmupAndSettle);
			if (rootViewport.Disable3D != disable3D)
			{
				rootViewport.Disable3D = disable3D;
			}
		}
		UpdateGuestLoadFreeze();
		Refii.Core.Engine_ServerSyncs.LoadCoverActive = CoverHeld;
		PumpDeferredCompaction();
		PumpLohHighWatermark();
		TraceEntities();
		if (Engine_TitleGame.Active.ApplyCamera(_camera))
		{
			_cameraDirty = true;
			EndHostAim(forwardResidualDelta: false);
			return;
		}

		// Photo mode owns the camera: it places it first each frame and restores the guest's
		// transform when it closes, so the statement below is read again from then on.
		if (GamePhotoMode.Active)
		{
			_cameraDirty = true;
			EndHostAim(forwardResidualDelta: false);
			return;
		}

		HostBridge.MouseLookState? lookBefore = _cameraLookBefore;
		Refii.Camera.Engine_CameraRig.Manager? drawnManager = Refii.Camera.Engine_CameraFeed.Drawn;
		if (Refii.Camera.Engine_CameraFeed.AsGuestCamera(drawnManager?.Live) is not { } camera)
		{
			return;
		}

		bool pairStated = drawnManager!.Previous is { Known: true };
		HostBridge.MouseLookState? lookAfter = _cameraLookAfter;

		if (camera.IsDefault)
		{
			return;
		}

		if (!TryStatedCamera(camera, out Transform3D stated, out float fov))
		{
			return;
		}

		uint cameraTick = Refii.Core.Engine_TickClock.GuestGameTick;
		bool torn = lookBefore is { } first && lookAfter is { } look &&
			(first.Sequence != look.Sequence || (look.Sequence & 1u) != 0u);
		if (torn)
		{
			// The camera may or may not carry the update that was in flight: it is not stated
			// this frame, and the turn posted since the last whole read is drawn against the
			// camera already drawn (or integrated directly when host aim is active).
			++MouseLookTornReads;
			if (_hostAimCamera.Active)
			{
				EvaluateAndApplyHostAim(delta, cameraTick, pairStated);
				return;
			}

			if (lookAfter is { } partial)
			{
				_mouseLook.ObservePosted(partial.Posted, partial.PostedPitch);
			}

			if (!_cameraLine.Empty)
			{
				_lineDrawn = AdvanceCameraLine(out float tornFov);
				Transform3D drawnTorn = DrawMouseLook(_lineDrawn);
				ApplyCameraToGodot(drawnTorn, tornFov);
				SampleCameraTrace(drawnTorn.Origin, CameraCarried, cameraTick);
				_split.Follow(_camera);
			}

			return;
		}

		if (lookBefore is not null && lookAfter is not null)
		{
			LastMouseLook = lookAfter;
		}

		// A cut is the stated cut byte on ANY push since the last statement taken, not only the
		// newest: a frame that takes two pushes still lands the first one's cut (Manager.Cuts).
		bool stepComplete = Refii.Core.Engine_GuestFrame.Changed;
		bool snap = camera.Flag || drawnManager!.Cuts != _cameraCutsSeen;
		if (stepComplete)
		{
			_cameraCutsSeen = drawnManager!.Cuts;
		}

		bool wantsHostAim = lookBefore is not null && lookAfter is { } wholeAim &&
			Refii.Ui.Engine_UiInputMode.KeyboardMouse &&
			wholeAim.Mode == Refii.Core.Engine_MouseLook.ModeAim &&
			wholeAim.Aim != 0u &&
			wholeAim.HasHead &&
			wholeAim.HeadGuest.IsFinite();

		if (wantsHostAim)
		{
			HostBridge.MouseLookState whole = lookAfter!.Value;
			ObserveMouseLook(whole, allowTurnAbout: false);
			MouseLookOutstanding = 0.0;
			MouseLookOutstandingPitch = 0.0;

			Vector3 headGodot = ToGodot(whole.HeadGuest);
			bool starting = !_hostAimCamera.Active || HostAimObject != whole.Aim;
			if (starting)
			{
				Transform3D seed = _cameraLine.Empty ? stated : DrawMouseLook(_lineDrawn);
				_hostAimCamera.Begin(
					seed, stated, headGodot, fov,
					whole.PitchLimit, whole.Factor, whole.LookInverted, cameraTick);
				HostAimActive = true;
				HostAimObject = whole.Aim;
				_ = GameInput.TakeHostAimDelta();
			}
			else if (stepComplete)
			{
				_hostAimCamera.ObserveGuest(
					cameraTick, stated, headGodot, fov,
					whole.PitchLimit, whole.Factor, whole.LookInverted, snap);
			}

			if (stepComplete)
			{
				Refii.Entities.Engine_DrawnFrame.NoteRestated();
			}

			EvaluateAndApplyHostAim(delta, cameraTick, pairStated);
			return;
		}

		if (_hostAimCamera.Active || HostAimActive)
		{
			EndHostAim(forwardResidualDelta: true);
		}

		ActiveAimFov = (lookBefore is not null && lookAfter is { Mode: Refii.Core.Engine_MouseLook.ModeAim })
			? fov
			: 0f;

		if (lookBefore is not null && lookAfter is { } wholeNonHost)
		{
			// Only advance MouseLookSequence and apply TurnAbout on a completed guest step (or initial
			// seed) so deferred in-flight frames never desync GameWorld.MouseLookSequence from GameUi.
			if (stepComplete || _cameraLine.Empty)
			{
				ObserveMouseLook(wholeNonHost, allowTurnAbout: true);
			}
			else
			{
				_mouseLook.ObservePosted(wholeNonHost.Posted, wholeNonHost.PostedPitch);
			}
		}

		bool changed = _cameraLine.Empty ||
			!stated.Origin.IsEqualApprox(_cameraLine.Newest.Origin) ||
			!stated.Basis.IsEqualApprox(_cameraLine.Newest.Basis) ||
			!Mathf.IsEqualApprox(fov, _cameraLine.NewestScalar);
		if (changed && stepComplete)
		{
			Refii.Entities.Engine_DrawnFrame.NoteRestated();
		}

		Transform3D drawn = stated;
		float drawnFov = fov;
		int how;
		if (!stepComplete && !_cameraLine.Empty)
		{
			drawn = AdvanceCameraLine(out drawnFov);
			++CarriedCameras;
			how = CameraCarried;
		}
		else if (snap || _cameraLine.Empty ||
			_cameraLine.Newest.Origin.DistanceSquaredTo(stated.Origin) >
				Refii.Core.Engine_TickTimeline.TeleportMetres * Refii.Core.Engine_TickTimeline.TeleportMetres)
		{
			// A stated snap (`live+0x34`) restarts the history rather than gliding across it, and
			// so does a jump the bodies treat as a teleport — the two restart together.
			_cameraLine.Reset(cameraTick, stated, fov);
			TakeDisplayStep(drawnManager!, restart: true);
			++LiveOnlyCameras;
			drawn = stated;
			how = CameraReset;
		}
		else
		{
			// On the bodies' clock and rule (Engine_TickTimeline): moved by the stated motion
			// every frame, steered toward the guest's blend at the world's draw point.
			_cameraLine.Observe(cameraTick, stated, fov);
			TakeDisplayStep(drawnManager!, restart: false);
			drawn = AdvanceCameraLine(out drawnFov);
			++BlendedCameras;
			how = CameraObserved;
		}

		_lineDrawn = drawn;
		drawn = DrawMouseLook(drawn);
		ApplyCameraToGodot(drawn, drawnFov);
		SampleCameraTrace(drawn.Origin, how, cameraTick);
		_split.Follow(_camera);

		if (!_reported)
		{
			_reported = true;
			GD.Print($"[game-world] driving Godot's camera from the guest: " +
				$"pos={drawn.Origin} fov={_camera.Fov:F1}deg " +
				$"pair={(pairStated ? "yes" : "no")}");
			Refii.Core.Engine_FramePhases.ArmDrawTrace("camera-drive", 5);
		}
	}


	/// <summary>The drawn manager's cut count at the last statement taken (Engine_CameraRig.Manager.Cuts).</summary>
	private uint _cameraCutsSeen;

	/// <summary>The two stated steps the camera line is drawing between, and whether they are
	/// consecutive pushes of the drawn manager (Engine_CameraDisplay shapes only such a pair).</summary>
	private Refii.Camera.Engine_CameraDisplay.Step _displayFrom;
	private Refii.Camera.Engine_CameraDisplay.Step _displayTo;
	private uint _displayPush;
	private bool _displayPair;

	/// <summary>
	/// The step the camera line just took, kept beside it: the drawn manager's live block and how
	/// its step made it, with the push before it as the step it is drawn from. A restart, a
	/// manager whose previous push is unknown, or pushes taken more than one at a time leave no pair.
	/// </summary>
	private void TakeDisplayStep(Refii.Camera.Engine_CameraRig.Manager manager, bool restart)
	{
		if (manager.Live is not { } live)
		{
			_displayPair = false;
			return;
		}

		if (!restart && manager.Pushes == _displayPush)
		{
			return;
		}

		_displayPair = !restart && manager.Pushes == _displayPush + 1u && manager.Previous is { Known: true };
		_displayPush = manager.Pushes;
		_displayTo = new Refii.Camera.Engine_CameraDisplay.Step(live, manager.LiveEase);
		_displayFrom = _displayPair
			? new Refii.Camera.Engine_CameraDisplay.Step(manager.Previous!.Value, manager.PreviousEase)
			: _displayTo;
	}

	/// <summary>
	/// THE DRAWN CAMERA BETWEEN TWO STATED STEPS. The camera line moves the camera on the bodies'
	/// clock and rule (Engine_TickTimeline: the straight blend of the stated pair at the shared draw
	/// point). Where the easing law gives the step a shape of its own (a blend under
	/// Camera.InterpolateFor, a rolled camera), the drawn camera is moved by the difference between
	/// the law's pose and the straight blend at the line's own phase (Engine_CameraDisplay), in the
	/// camera's frame, so a turn of the whole history (the mouse's) leaves it unchanged. The
	/// difference is nothing at each stated step and for every step the law does not shape, where
	/// the picture is the line's alone.
	/// </summary>
	private Transform3D AdvanceCameraLine(out float fov)
	{
		Transform3D drawn = _cameraLine.Advance(Refii.Entities.Engine_DrawnFrame.NowUsec,
			Refii.Entities.Engine_DrawnFrame.DrawTick);
		fov = _cameraLine.DrawnScalar;
		if (!_displayPair || !Refii.Camera.Engine_CameraDisplay.Shaped(_displayFrom, _displayTo))
		{
			return drawn;
		}

		float phase = (float)_cameraLine.Phase(Refii.Entities.Engine_DrawnFrame.DrawTick);
		if (!(phase > 0f) || phase >= 1f)
		{
			return drawn;
		}

		if (Refii.Camera.Engine_CameraFeed.AsGuestCamera(
				Refii.Camera.Engine_CameraDisplay.Evaluate(_displayFrom, _displayTo, phase)) is not { } law ||
			Refii.Camera.Engine_CameraFeed.AsGuestCamera(
				Refii.Camera.Engine_CameraDisplay.Blend(_displayFrom.Block, _displayTo.Block, phase)) is not { } straight ||
			!TryStatedCamera(law, out Transform3D lawDrawn, out float lawFov) ||
			!TryStatedCamera(straight, out Transform3D straightDrawn, out float straightFov))
		{
			return drawn;
		}

		++ShapedCameras;
		fov += lawFov - straightFov;
		return drawn * (straightDrawn.AffineInverse() * lawDrawn);
	}

	/// <summary>Frames whose drawn camera took the law's shape between two stated steps; run total.</summary>
	public static long ShapedCameras;

	/// <summary>
	/// A whole read of the mouse's account, paired with the camera read inside it. A turn the
	/// guest's camera absorbed since the last whole read is carried by this camera and was drawn
	/// ahead of the last one, so the drawn history is restated by it, about the point it was drawn
	/// turning about (<see cref="Refii.Core.Engine_MouseLook"/>): the view does not move, and the
	/// timeline goes on blending only the camera's own motion. A turn absorbed while none was drawn
	/// ahead (the camera had refused the mouse until this update) is left to the timeline to glide.
	/// </summary>
	private void ObserveMouseLook(HostBridge.MouseLookState look, bool allowTurnAbout = true)
	{
		int drawnMode = _mouseLook.Mode;
		Vector3 drawnPivot = _mouseLook.PivotOffsets;
		(double right, double up) = _mouseLook.Observe(look.Posted, look.Taken, look.Absorbed,
			look.PostedPitch, look.TakenPitch, look.AbsorbedPitch, look.Factor, look.Pivot, look.Mode,
			look.PitchLimit, look.Aim);
		if (allowTurnAbout && (right != 0.0 || up != 0.0) &&
			drawnMode != Refii.Core.Engine_MouseLook.ModeNone && !_cameraLine.Empty)
		{
			Transform3D newest = _cameraLine.Newest;
			_cameraLine.TurnAbout(Refii.Core.Engine_MouseLook.PivotFor(newest, drawnMode, drawnPivot),
				Refii.Core.Engine_MouseLook.Rotation(newest, drawnMode, right, up));
		}

		MouseLookMode = _mouseLook.Mode;
		MouseLookPivot = _mouseLook.PivotOffsets;
		MouseLookAbsorbed = _mouseLook.Absorbed;
		MouseLookAbsorbedPitch = _mouseLook.AbsorbedPitch;
		MouseLookFlicks = look.Flicks;
		MouseLookSequence = look.Sequence;
	}

	/// <summary>
	/// THE MOUSE'S TURN AT THE DISPLAY RATE: the camera the timeline draws, turned by the part of
	/// the mouse's turn no guest camera update has taken yet, the way the guest's camera turns —
	/// about its focus, in place, or with the ranged aim about the hero's head. Nothing is predicted:
	/// the guest turns exactly this much in its next update, and the statement that carries it
	/// restates the history by the same.
	/// </summary>
	private Transform3D DrawMouseLook(Transform3D drawn)
	{
		MouseLookOutstanding = _mouseLook.Outstanding;
		(Vector3 pivot, Basis turn) = _mouseLook.Ahead(drawn);
		MouseLookOutstandingPitch = _mouseLook.Mode == Refii.Core.Engine_MouseLook.ModeAim
			? Refii.Core.Engine_MouseLook.BoundPitch(drawn, _mouseLook.OutstandingPitch, _mouseLook.PitchLimit)
			: 0.0;
		MouseLookAim = _mouseLook.Aim;
		_aheadPivot = pivot;
		_aheadTurn = turn;
		return _mouseLook.DrawAhead(drawn);
	}

	/// <summary>
	/// One stated camera as a Godot transform and a vertical fov, or false when it cannot be
	/// drawn from. Applied to BOTH halves of the guest's pair, so a torn or half-built previous
	/// camera can never reach the blend.
	/// </summary>
	/// <remarks>
	/// A non-finite camera makes every depth NaN: Godot sorts its render list with std::sort,
	/// whose unguarded final pass walks off the array on NaN comparisons and faults forever.
	/// `IsZeroApprox()` admits NaN, and an infinite forward becomes NaN in `Normalized()`, hence
	/// the explicit finiteness tests. The blocks this reads are unsynchronised, so a torn read
	/// is expected; the caller holds the last good camera.
	///
	/// The fov needs its own check: `Camera3D.set_fov` tests `fov &lt; 1 || fov > 179`, which
	/// stores a NaN. `VerticalFovDegrees` is `Atan(Tan(h * 0.5))`, so an infinite horizontal fov
	/// arrives as NaN.
	/// </remarks>
	internal static bool TryStatedCamera(
		HostBridge.GuestCamera camera, out Transform3D stated, out float verticalFov)
	{
		stated = Transform3D.Identity;
		verticalFov = 0f;

		Vector3 position = ToGodot(camera.Position);
		Vector3 forward = ToGodot(camera.Forward).Normalized();
		Vector3 up = ToGodot(camera.Up);

		if (!position.IsFinite() || !forward.IsFinite() || !up.IsFinite() ||
			position.Length() > MaxCameraReach || forward.IsZeroApprox())
		{
			++NonFiniteCameras;
			return false;
		}

		// Re-orthonormalise up against forward. This matches the guest rather than fighting
		// it — it does the same Gram-Schmidt itself in sub_822B52F8, correcting UP and
		// holding FORWARD fixed.
		Vector3 basisZ = -forward;
		Vector3 basisY = (up - basisZ * up.Dot(basisZ)).Normalized();

		// Gram-Schmidt can hand back NaN from finite inputs when up is parallel to forward:
		// the subtraction cancels to zero and Normalized() divides by it. Same reason as
		// above, IsZeroApprox alone would not see it.
		if (!basisY.IsFinite() || basisY.IsZeroApprox())
		{
			++NonFiniteCameras;
			return false;
		}

		float fov = camera.VerticalFovDegrees;
		if (!float.IsFinite(fov) || fov < 1f || fov > 179f)
		{
			++NonFiniteCameras;
			return false;
		}

		stated = new Transform3D(new Basis(basisY.Cross(basisZ), basisY, basisZ), position);
		verticalFov = fov;
		return true;
	}

	/// <summary>
	/// Lets the guest go once the mounted region has actually finished paging in.
	///
	/// THE MOUNT IS NOT THE FINISH LINE. `Engine_PropStreamer` streams props over frames after
	/// the region enters the tree — 25 ms of work a frame by default — so a world released at
	/// AddChild is bare terrain under an empty sky that fills in while the player walks through
	/// it. That reads as a failed build and is not one: it is a build the guest was let into
	/// too early.
	///
	/// Runs every frame rather than hooking `ProgressChanged`, because the streamer publishes
	/// progress from its own tick and a missed final event would strand the hold forever. A
	/// poll cannot miss an edge.
	/// </summary>
	private void ReleaseWhenPropsHaveStreamed()
	{
		if (!_loaded || !_holdingForProps)
		{
			return;
		}

		// COUNTED UNDER THE STANDING REGION ONLY, not across every live streamer.
		//
		// `Engine_PropStreamer.TryGetProgress` sums the whole static `Live` list, which spans
		// regions. One leftover streamer that never drains — a region torn down mid-stream, a
		// node whose `_ExitTree` has not run yet — holds the total above the count for ever, and
		// the guest with it. Asking only for those beneath the region we actually mounted cannot be
		// poisoned that way.
		//
		// No streamer at all is DONE, not pending: a region with nothing to page in must not
		// hold the guest.
		int done = 0;
		int total = 0;
		int settling = 0;
		if (_region is not null)
		{
			Refii.Level.Engine_PropStreamer.SumProgress(_region, ref done, ref total, ref settling);
		}

		// Placed is not finished: a prop's vertex bake and its ambient variant land after its
		// step, and either one left for play is a main-thread apply in the middle of it.
		bool propsPending = (total > 0 && done < total) || settling > 0;

		// AND THE ENTITIES, AND THEN A FEW FRAMES. Props streamed is not "the world is there":
		// entity meshes build off-thread, the static copies reconcile against them, and the
		// first draw of every material compiles its pipeline. All of it happens under the
		// loading screen now, so the frame that hands the player control is the frame they keep.
		if (_entities is { } holding)
		{
			holding.LoadHold = true;
		}

		bool entitiesPending = Engine_TitleGame.Active.StatesEntities &&
			_entities is { } feed && !feed.ReadyForRelease;
		// THE CLASSIC SHADERS, last: every composition compiled and every light-group shape the
		// placed fixtures can produce built, so play composes and compiles nothing.
		string classicWait = Refii.Models.Engine_ClassicLoad.Pump(!propsPending && !entitiesPending);
		// THEN THE LIGHT-COUNT PIPELINES (`android_pipeline_warmup`, Mobile only): every surface
		// the level holds drawn beside the camera under each light count play can give it.
		bool uploadsPending = Refii.Core.Engine_ServerSyncs.PendingUploadCount > 0;
		bool lightingBakePending = Refii.Lighting.Engine_LevelShadowPrebuild.IsLoadBakePending;
		bool worldReady = !propsPending && !entitiesPending && !uploadsPending && !lightingBakePending && classicWait.Length == 0;
		if (worldReady)
		{
			if (!ReadyForWarmupAndSettle)
			{
				GD.Print($"[dx12-trace] worldReady reached for {_standing} after {_propHoldFrames} frames (props={done}/{total}, uploads=0)");
				Refii.Core.Engine_FramePhases.ArmDrawTrace("warmup-ready", 2);
			}
			ReadyForWarmupAndSettle = true;
			System.Threading.Interlocked.Increment(ref _settleFrames);
		}
		else
		{
			ReadyForWarmupAndSettle = false;
			System.Threading.Interlocked.Exchange(ref _settleFrames, 0);
		}
		string pipelineWait = Refii.Models.Engine_PipelineWarmup.Pump(this, worldReady);
		bool pending = !worldReady || pipelineWait.Length != 0;
		Refii.Lighting.Engine_GiRefresh.HoldForWorldLoad = pending;
		// A warm-up frame draws the whole world too, so it counts towards the settle.
		_readyFrames = worldReady ? _readyFrames + 1 : 0;
		if (!pending && _readyFrames < ReleaseSettleFrames)
		{
			pending = true;
		}

		if (!propsPending)
		{
			Refii.Core.Engine_LoadTimeline.Mark("props");
		}

		if (!entitiesPending)
		{
			Refii.Core.Engine_LoadTimeline.Mark("entities");
		}

		if (!lightingBakePending)
		{
			Refii.Core.Engine_LoadTimeline.Mark("lighting-bake");
		}

		if (worldReady)
		{
			Refii.Core.Engine_LoadTimeline.Mark("world");
		}

		if (worldReady && pipelineWait.Length == 0)
		{
			Refii.Core.Engine_LoadTimeline.Mark("warmed");
		}

		++_propHoldFrames;

		if (pending)
		{
			// A HOLD THAT NEVER RELEASES IS A HUNG GAME, so it reports itself and then gives up.
			// A wait with neither is indistinguishable from a crash, and the counts are what say
			// whether the streamer is progressing slowly or not progressing at all.
			if (_propHoldFrames % PropHoldReportFrames == 0)
			{
				string bakeDiag = lightingBakePending
					? $"lighting bake ({Refii.Lighting.Engine_LevelShadowPrebuild.LoadBakeStatus}), "
					: string.Empty;
				GD.Print($"[game-world] still holding the guest for {_standing}: " +
					$"{done}/{total} prop(s) streamed ({settling} bake(s) or variant(s) to apply), " +
					bakeDiag +
					$"classic waiting on {(classicWait.Length != 0 ? classicWait : "nothing")}, " +
					(pipelineWait.Length != 0 ? $"pipeline warm-up waiting on {pipelineWait}, " : "") + "entities waiting on " +
					$"{(_entities is { } e ? e.ReleaseWait : "no feed")}, {_readyFrames} settle frame(s) " +
					$"after {_propHoldFrames} frame(s)");
			}

			if (_propHoldFrames < PropHoldFrameLimit)
			{
				return;
			}

			GD.PrintErr($"[game-world] {_standing} did not finish loading " +
				$"({done}/{total} props, {settling} bake(s) or variant(s), classic waiting on " +
				$"{(classicWait.Length != 0 ? classicWait : "nothing")}) after {_propHoldFrames} frame(s) — releasing the guest " +
				"anyway. A world that fills in late is worse than one the player cannot leave.");
		}

		_holdingForProps = false;
		Refii.Lighting.Engine_GiRefresh.HoldForWorldLoad = false;
		Refii.Core.Engine_LoadTimeline.Released();
		Refii.Models.Engine_ClassicLoad.End();
		Refii.Models.Engine_PipelineWarmup.End();
		_entities?.ReportLoadBuilds();
		Refii.Core.Engine_FramePhases.ArmDrawTrace("cover-release", 3);
		// The title clears its tone-map history for as long as a level streams
		// (`sub_82181B68`), so the first frame after the load snaps the statistics; a cave
		// theme with EXPOSURE_SPEED 0 then holds that snap.
		Refii.Lighting.Engine_PostFxEffect.ResetHistory();
		SetWorldBuildHeld(false);
		if (_entities is { } released)
		{
			released.LoadHold = false;
		}

		if (_region is not null)
		{
			SetStreamBudget(_region, Refii.Level.Engine_PropStreamer.DefaultMsPerFrame);
		}

		if (!pending)
		{
			double heldSeconds = (System.Diagnostics.Stopwatch.GetTimestamp() - _heldSince) /
				(double)System.Diagnostics.Stopwatch.Frequency;
			GD.Print($"[game-world] {_standing} loaded in {heldSeconds:0.00} s — finished streaming its props and their bakes, compiled its classic shaders, built every entity " +
				$"mesh, reconciled its copies and drew {ReleaseSettleFrames} settled frame(s) after " +
				$"{_propHoldFrames} frame(s) — releasing the guest; " +
				(_entities?.DescribeHero() ?? "no entity feed"));
		}

		CompactLargeObjectHeap();
		Refii.Core.Engine_FramePhases.ResetMetrics();
	}

	/// <summary>
	/// True while the world build holds the guest's level change. The loading screen reads it:
	/// the guest can take its own screen down (resource preload, state 4) while the change is
	/// still parked behind the hold.
	/// </summary>
	public static bool WorldBuildHeld { get; private set; }

	/// <summary>The loading cover stays up: the build holds, or classic settles the stated lights' shapes.</summary>
	public static bool CoverHeld => WorldBuildHeld || Refii.Models.Engine_ClassicLoad.Settling;

	/// <summary>
	/// True while Godot's world build or prop/entity streaming holds the loading cover and the guest
	/// has already activated the level's entities and settled their initial bone poses, halting
	/// guest simulation steps so NPCs, income notices and HUD transitions do not run behind the cover.
	/// </summary>
	public static bool FreezeGuestForLoad { get; private set; }

	private static int _activeHoldGuestSteps;

	private void UpdateGuestLoadFreeze()
	{
		// Note: `WorldIsReady()` (`game+0x105`) is cleared in state 3 and stays false until state 9
		// (after our hold releases), so gating on it would leave the guest running throughout the load.
		// `WorldIsActive()` (`World+0xA8`) is set by entity activation in state 5 / save-load state 2.
		if (!WorldBuildHeld || !HostBridge.WorldIsActive())
		{
			_activeHoldGuestSteps = 0;
			FreezeGuestForLoad = false;
			return;
		}

		if (_loaded && !_holdingForProps)
		{
			FreezeGuestForLoad = false;
			return;
		}

		if (Refii.Core.Engine_GuestFrame.Changed)
		{
			++_activeHoldGuestSteps;
		}

		// Allow at most 2 guest HF frames across the entire hold window after WorldIsActive() so initial
		// bone poses settle (including save-loads where WorldIsReady() activates early), without ever
		// dropping the freeze when `_loaded` flips true at mount (`GameEntities` pumps its initial walk
		// directly via `LoadHold && !ReadyForRelease` without requiring guest frames to advance).
		FreezeGuestForLoad = _activeHoldGuestSteps >= 2 || (_entities is not null && _entities.HasInitialWalk);
	}

	/// <summary>When the current hold began, for the load's wall time on the release line.</summary>
	private static long _heldSince;

	private static void SetWorldBuildHeld(bool held)
	{
		if (held && !WorldBuildHeld)
		{
			_heldSince = System.Diagnostics.Stopwatch.GetTimestamp();
			_activeHoldGuestSteps = 0;
			Refii.Lighting.Engine_GiRefresh.HoldForWorldLoad = true;
			// A compaction still owed by the last release runs under this loading screen, before
			// the next build's buffers land on top of the old gaps.
			RunDeferredCompaction("the next load");
		}

		if (!held)
		{
			_activeHoldGuestSteps = 0;
			FreezeGuestForLoad = false;
			Refii.Lighting.Engine_GiRefresh.HoldForWorldLoad = false;
			HostBridge.SetTimeBaseFrozen(false);
		}

		WorldBuildHeld = held;
		Refii.Core.Engine_ServerSyncs.LoadCoverActive = CoverHeld;
		HostBridge.SetWorldBuildPending(held);
	}

	/// <summary>
	/// Compacts the large-object heap and runs Godot <see cref="RefCounted"/> finalizers so
	/// unreferenced GPU textures, shadow map atlases, meshes and materials are released from VRAM.
	/// </summary>
	/// <remarks>
	/// Bank containers, mesh arrays and texture buffers exceed the 85 KB threshold, so a region
	/// load churns the LOH, which .NET sweeps but never compacts unless asked; the gaps stay
	/// committed. Furthermore, Godot's C# <see cref="RefCounted"/> wrappers (<see cref="Texture2D"/>,
	/// <see cref="Texture2DArray"/>, <see cref="ArrayMesh"/>, <see cref="ShaderMaterial"/>) only
	/// decrement their native <see cref="RenderingServer"/> refcounts when their finalizers run,
	/// so <see cref="GC.WaitForPendingFinalizers"/> followed by a second collection is required
	/// to reclaim VRAM across level transitions.
	/// </remarks>
	private static void CompactLargeObjectHeap()
	{
		long before = System.GC.GetTotalMemory(false);
		// Android compacts only under `android_loh_compaction` (GameWorld.Android.cs).
		if (Refii.Core.Engine_Platform.IsAndroid && CollectWithoutCompaction(before))
		{
			return;
		}

		int deferSeconds = DeferSeconds();
		if (deferSeconds > 0)
		{
			_compactionDue = System.Diagnostics.Stopwatch.GetTimestamp() +
				deferSeconds * System.Diagnostics.Stopwatch.Frequency;
			_compactionPauseFrames = 0;
			GD.Print($"[game-world] large-object heap compaction deferred: at the first pause menu, " +
				$"the next load, or in {deferSeconds} s ({before / 1048576.0:0} MB live)");
			return;
		}

		Compact(before);
	}

	/// <summary>
	/// `loh_compaction_defer_seconds` (default 30; 0 runs compaction at release): when above 0,
	/// the release's compaction waits for a moment the player is not watching the world move —
	/// the pause menu standing for <see cref="CompactionPauseFrames"/> frames (so it has been
	/// drawn and the freeze lands on a still screen), or the next load's hold — and runs after
	/// that many seconds of play at the latest, so the memory always comes back.
	/// </summary>
	private static void CompactImmediateOnTeardown()
	{
		long before = System.GC.GetTotalMemory(false);
		_compactionDue = 0;
		Compact(before);
	}

	private static int DeferSeconds()
	{
		try
		{
			return System.Math.Max(0, Refii.Core.Engine_RefiiConfig.Int(
				Refii.Core.Engine_RefiiConfig.Read(out _), DeferSecondsKey, 30));
		}
		catch (System.Exception)
		{
			return 30;
		}
	}

	public const string DeferSecondsKey = "loh_compaction_defer_seconds";

	/// <summary>Frames the pause root stands before the owed compaction runs under it.</summary>
	private const int CompactionPauseFrames = 3;

	/// <summary>When the owed compaction runs at the latest, as a Stopwatch timestamp; 0 for none owed.</summary>
	private static long _compactionDue;

	private static int _compactionPauseFrames;

	/// <summary>One frame of play: the owed compaction, if the pause menu stands or its time is up.</summary>
	private static void PumpDeferredCompaction()
	{
		if (_compactionDue == 0 || WorldBuildHeld)
		{
			return;
		}

		_compactionPauseFrames = HostBridge.GetPauseState() is { RootActive: true } ? _compactionPauseFrames + 1 : 0;
		if (_compactionPauseFrames >= CompactionPauseFrames)
		{
			RunDeferredCompaction("the pause menu");
		}
		else if (System.Diagnostics.Stopwatch.GetTimestamp() >= _compactionDue)
		{
			RunDeferredCompaction("its deadline");
		}
	}

	private static void RunDeferredCompaction(string when)
	{
		if (_compactionDue == 0)
		{
			return;
		}

		_compactionDue = 0;
		GD.Print($"[game-world] running the deferred large-object heap compaction at {when}");
		Compact(System.GC.GetTotalMemory(false));
	}

	private static void Compact(long before)
	{
		long started = System.Diagnostics.Stopwatch.GetTimestamp();
		Refii.Hero.Engine_HeroTextureMorph.TrimDecodedSources();
		Refii.Level.Engine_TerrainSplatMaterial.ClearSharedCache();
		// The first pass only finds what the finalizers can let go of; the second compacts all of it,
		// so moving everything in the first as well is a pause with no result of its own.
		System.GC.Collect(2, System.GCCollectionMode.Forced, blocking: true, compacting: false);
		System.GC.WaitForPendingFinalizers();
		System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
			System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
		System.GC.Collect(2, System.GCCollectionMode.Aggressive, blocking: true, compacting: true);
		System.GC.WaitForPendingFinalizers();

		System.GCMemoryInfo info = System.GC.GetGCMemoryInfo();
		long lohAfter = ReadLohLiveBytes(info);
		if (lohAfter >= 0)
		{
			_lohBaselineBytes = lohAfter;
		}
		GD.Print($"[game-world] compacted the large-object heap: " +
			$"{before / 1048576.0:0} MB -> {System.GC.GetTotalMemory(false) / 1048576.0:0} MB live, " +
			$"{info.TotalCommittedBytes / 1048576.0:0} MB committed, " +
			$"{System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms");
	}

	private const int LohCheckIntervalFrames = 300;
	private const long LohGrowthThresholdBytes = 1024L * 1024L * 1024L;
	private static long _lohBaselineBytes = -1L;
	private static int _lohCheckFrameCounter;

	private static long ReadLohLiveBytes(System.GCMemoryInfo info)
	{
		// GenerationInfo[3] is LOH in .NET (0=Gen0, 1=Gen1, 2=Gen2, 3=LOH, 4=POH).
		return info.GenerationInfo.Length > 3
			? info.GenerationInfo[3].SizeAfterBytes
			: -1L;
	}

	/// <summary>
	/// Pure policy helper deciding whether .NET ServerGC LOH growth over the post-compaction baseline
	/// requires a proactive full compacting GC pass to bound uncollected LOH bloat.
	/// </summary>
	public static bool ShouldTriggerLohHighWatermark(
		long currentLohBytes,
		long baselineLohBytes,
		bool worldBuildHeld,
		bool deviceLost,
		long growthThresholdBytes = LohGrowthThresholdBytes) =>
		!worldBuildHeld &&
		!deviceLost &&
		baselineLohBytes >= 0 &&
		currentLohBytes - baselineLohBytes >= growthThresholdBytes;

	private static void PumpLohHighWatermark()
	{
		if (WorldBuildHeld || Refii.Core.Engine_RdHealth.IsDeviceLost)
		{
			return;
		}

		if (++_lohCheckFrameCounter < LohCheckIntervalFrames)
		{
			return;
		}

		_lohCheckFrameCounter = 0;
		System.GCMemoryInfo info = System.GC.GetGCMemoryInfo();
		long currentLoh = ReadLohLiveBytes(info);
		if (currentLoh < 0)
		{
			return;
		}

		if (_lohBaselineBytes < 0)
		{
			_lohBaselineBytes = currentLoh;
			return;
		}

		if (!ShouldTriggerLohHighWatermark(currentLoh, _lohBaselineBytes, WorldBuildHeld, Refii.Core.Engine_RdHealth.IsDeviceLost))
		{
			return;
		}

		GD.Print($"[game-world] LOH high-watermark reached (+{(currentLoh - _lohBaselineBytes) / 1048576.0:0} MB over {_lohBaselineBytes / 1048576.0:0} MB baseline); compacting");
		Compact(System.GC.GetTotalMemory(false));
	}

	/// <summary>The last two cameras the guest stated; the playhead is shared.</summary>
	/// <summary>The camera's stated history, on the bodies' clock and rule.</summary>
	private readonly Refii.Core.Engine_TickTimeline _cameraLine = new();

	private int _cameraTraceRemaining;

	/// <summary>How the frame's camera was produced; `how` in the trace rows.</summary>
	public const int CameraCarried = 0;

	public const int CameraObserved = 1;

	public const int CameraReset = 2;

	/// <summary>Frames drawn from the timeline alone because the guest's step was not complete at frame start.</summary>
	public static long CarriedCameras;

	/// <summary>One drawn frame of the camera trace: the drawn position beside the clock terms that placed it.</summary>
	private readonly record struct CameraTraceRow(
		ulong Usec, ulong Now, uint Tick, uint Stamp, uint Frame, int How, double Playhead,
		double Newest, double Alpha, double Margin);

	private readonly System.Collections.Generic.List<CameraTraceRow> _cameraTrace = new();
	private readonly System.Collections.Generic.List<Vector3> _cameraTracePositions = new();

	/// <summary>Records the guest camera per frame, for the debug channel's stutter analysis.</summary>
	public void BeginCameraTrace(int frames)
	{
		_cameraTraceRemaining = frames;
		_cameraTrace.Clear();
		_cameraTracePositions.Clear();
	}

	/// <summary>
	/// The trace rows. `usec` is the sample's own instant, `now` the frame's draw instant every
	/// draw point used; `tick` is `dword_83319650` beside the sample, `stamp` the game tick read at
	/// frame start that stamped it, `frame` the guest's HF-pass counter; `how` is
	/// <see cref="CameraCarried"/>, <see cref="CameraObserved"/> or <see cref="CameraReset"/>;
	/// `playhead` the draw point, `newest` the camera line's newest stamp, `alpha` the shared
	/// pair's raw alpha (past 1 extrapolating, at 1.5 held), `margin` the latency taken off it.
	/// </summary>
	public System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>> CameraTraceReport()
	{
		var rows = new System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>();
		for (int i = 0; i < _cameraTrace.Count; ++i)
		{
			CameraTraceRow r = _cameraTrace[i];
			Vector3 p = _cameraTracePositions[i];
			rows.Add(new System.Collections.Generic.Dictionary<string, object?>
			{
				["usec"] = r.Usec, ["now"] = r.Now, ["tick"] = r.Tick, ["stamp"] = r.Stamp,
				["frame"] = r.Frame, ["how"] = r.How, ["playhead"] = r.Playhead, ["newest"] = r.Newest,
				["alpha"] = r.Alpha, ["margin"] = r.Margin,
				["extrapolated"] = r.Alpha > 1.0, ["held"] = r.Alpha >= Refii.Core.Engine_PairPlayhead.Ceiling,
				["pos"] = new[] { p.X, p.Y, p.Z },
			});
		}

		return rows;
	}

	private void SampleCameraTrace(Vector3 position, int how, uint stamp)
	{
		if (_cameraTraceRemaining <= 0)
		{
			return;
		}

		--_cameraTraceRemaining;
		ulong now = Refii.Entities.Engine_DrawnFrame.NowUsec;
		Refii.Core.Engine_PairPlayhead pair = Refii.Entities.Engine_DrawnFrame.Playhead;
		_cameraTrace.Add(new CameraTraceRow(
			Godot.Time.GetTicksUsec(), now, HostBridge.SimTick, stamp, Refii.Core.Engine_GuestFrame.Frame, how,
			Refii.Entities.Engine_DrawnFrame.DrawTick, _cameraLine.NewestTick,
			pair.Ready ? pair.Alpha(now) : double.NaN, pair.MarginTicks));
		_cameraTracePositions.Add(position);
	}

	private const double HeldStreamMsPerFrame = 14.0;

	private static void SetStreamBudget(Node node, double milliseconds) =>
		Refii.Level.Engine_PropStreamer.SetBudget(node, milliseconds);

	/// <summary>True between mounting a region and its props finishing streaming.</summary>
	private bool _holdingForProps;

	private int _propHoldFrames;

	/// <summary>Frames in a row with the world in place; the release waits for ReleaseSettleFrames of them.</summary>
	private int _readyFrames;

	/// <summary>Frames drawn with everything in place before the guest is released.</summary>
	private const int ReleaseSettleFrames = 10;

	private int _entityWaitFrames;

	/// <summary>Every ~5s at 60fps, so a stall keeps saying what it is waiting for.</summary>
	private const int EntityWaitReportFrames = 300;

	/// <summary>
	/// About 30s. The guest's own load is blocking and finishes long before this; anything
	/// longer means we are misreading it, and a late mount beats a world nobody can enter.
	/// </summary>
	private const int EntityWaitFrameLimit = 1800;

	/// <summary>Roughly every 5s at 60fps, so a stall says what it is waiting for.</summary>
	private const int PropHoldReportFrames = 300;

	/// <summary>
	/// About a minute. Long enough for a big region to page in honestly, short enough that a
	/// hold which will never release does not read as a hang with no explanation.
	/// </summary>
	private const int PropHoldFrameLimit = 3600;

	/// <summary>
	/// The region build, running off the main thread. Null until one has been started.
	/// </summary>
	private System.Threading.Tasks.Task? _build;

	/// <summary>
	/// What the worker produced. Written by the worker BEFORE the task completes and read by
	/// the main thread only AFTER <see cref="System.Threading.Tasks.Task.IsCompleted"/>, which
	/// is the ordering that makes these safe without a lock of their own — completing a task
	/// publishes everything the task wrote.
	/// </summary>
	private Node3D? _builtRegion;

	private string _buildError = string.Empty;

	/// <summary>"World/Level" the in-flight build was started for, so its completion cannot be
	/// reported against a level the guest moved to in the meantime.</summary>
	private string _buildingLevel = string.Empty;

	private System.Diagnostics.Stopwatch? _buildClock;

	/// <summary>
	/// Kicks off the region build on a worker thread.
	///
	/// The scenario is resolved by the CALLER, on the main thread, before this is reached —
	/// it is read from the guest and written to shared state on the assets singleton, and
	/// doing that from the worker would race the very first thing the worker reads.
	/// </summary>
	private void StartBuild(Refii.Assets.Engine_GameAssets assets, HostBridge.StartLevel start)
	{
		if (RetirementBlocksBuild) return;
		string world = start.World;
		string level = start.Level;
		_buildingLevel = $"{world}/{level}";

		_buildClock = System.Diagnostics.Stopwatch.StartNew();

		// HOLD THE GUEST, using its own question. State 7 of the level-change command polls
		// "IsEnginePreLoading" and waits while the answer is yes; Godot is the engine. Set
		// before the worker starts so there is no frame where the guest could be let through
		// between the decision to build and the build beginning.
		HostEntityWalk.Discard();
		SetWorldBuildHeld(true);

		GD.Print($"[game-world] building {world}/{level} on a worker thread; " +
			"the guest waits in its own loading screen");

		Refii.Core.Engine_LoadTimeline.Open($"build {world}/{level}");
		_build = System.Threading.Tasks.Task.Run(() =>
		{
			long buildFrom = System.Diagnostics.Stopwatch.GetTimestamp();
			try
			{
				_builtRegion = assets.BuildLevelNode(world, level, out string error);
				_buildError = error;
				Refii.Core.Engine_LoadTimeline.Add(Refii.Core.Engine_LoadTimeline.Category.RegionBuild,
					System.Diagnostics.Stopwatch.GetTimestamp() - buildFrom);
			}
			catch (System.Exception e)
			{
				// CAUGHT AND CARRIED BACK, never left to the task. An exception on a worker
				// does not stop the game — it is stored in the Task and silently discarded when
				// nothing observes it, so the world would simply never appear with nothing at
				// all in the log to say why.
				_builtRegion = null;
				_buildError = $"{e.GetType().Name}: {e.Message}";
			}
		});
	}

	/// <summary>
	/// Takes the finished region, or null when the build produced nothing.
	///
	/// <paramref name="failed"/> separates the two nulls: a build that ERRORED is worth saying
	/// once, while a build that simply found no banks mounted yet is the ordinary early state
	/// and is retried in silence.
	/// </summary>
	private Node3D? TakeBuiltRegion(HostBridge.StartLevel start, out bool failed)
	{
		Node3D? region = _builtRegion;
		_builtRegion = null;
		failed = region is null && _buildError.Length != 0;

		// NAMED BY WHAT WAS BUILT, not by what the guest happens to say now.
		//
		// These lines used to print the CURRENT frame's level, so a build started for BWSMarket
		// and finished while the guest had moved on reported "BowerLake built" and "mounted
		// BowerLake" over BWSMarket's geometry. The log asserted a level change that had not
		// happened, which is worse than saying nothing — it is the reason this took a run to
		// spot. `_buildingLevel` is captured when the worker starts and cannot drift.
		if (region is not null && _buildClock is not null)
		{
			GD.Print($"[game-world] {_buildingLevel} built in " +
				$"{_buildClock.ElapsedMilliseconds} ms of WALL time off the main thread");
			_buildClock = null;
		}

		if (region is not null &&
			!_buildingLevel.Equals($"{start.World}/{start.Level}",
				System.StringComparison.OrdinalIgnoreCase))
		{
			// The guest moved while we were building. Throwing the region away and rebuilding is
			// the only honest outcome — mounting it would put the wrong world under the camera
			// and call it the right one.
			GD.Print($"[game-world] built {_buildingLevel} but the guest is now in " +
				$"{start.World}/{start.Level} — discarding it and rebuilding");
			region.QueueFree();
			return null;
		}

		return region;
	}

	/// <summary>
	/// Builds the region the guest named. `sub_82379628` writes the destination before it
	/// starts the game, so Godot is told which region to build rather than inferring one.
	/// </summary>
	private void FollowGuestIntoTheWorld()
	{
		// THE LEVEL THE GUEST IS STANDING IN, not the one a New Game asked for.
		//
		// This used to read `GetStartLevel`, and that only ever worked for a new game. Every
		// writer of those globals is a game-START path; a door (`sub_8283C7F8`), a teleporter,
		// fast travel (`sub_82519B08`) and `World.LoadLevel` (`sub_82350D20`) all put their
		// destination on the CmdLevelChange command object and never touch a global. The
		// version that appeared to work did so because the DEV DEBUG MENU happens to rewrite
		// those globals before it issues `Debug.LoadLevel` — right answer, wrong reason, and it
		// would have done nothing the first time anyone walked through a door.
		//
		// Null is ordinary: before the first load, and during the window when the old Level is
		// released and the new one not yet constructed.
		//
		// OUT OF THE GAME IS THE OTHER UNLOAD EDGE. `byte_83496A5E` is set once by
		// Game_InitDatabases (0x82311648) and cleared only by the game mode's shutdown — a level
		// change never touches it — so its falling edge is the guest stating that it has left
		// the game: Quit to Title, with or without a save. The guest's own shutdown frees its
		// Level and every entity; a region left standing past that point drew a world the guest
		// no longer had, and the front end came up over it.
		//
		// `_torndownKey` is NOT kept across this edge. It exists to stop a level change
		// rebuilding the level being LEFT while the guest still names it; a quit leaves no level
		// named, and the next game the player starts may well be the same level — Continue after
		// Quit to Title — which the guard would otherwise refuse to build until the guest named a
		// different one.
		if (!HostBridge.IsInGame)
		{
			if (Refii.Core.Engine_Platform.IsAndroid) ReleaseRetiredFence();
			if (_loaded || _build is not null)
			{
				GD.Print($"[game-world] the guest has left the game — taking " +
					$"{(_loaded ? _standing : _buildingLevel)} down");
				TearDownRegion();
				_torndownKey = 0u;
			}

			return;
		}

		// THE UNLOAD EDGE IS STATE 3: the tick that leaves state 2 is the one that tears down.
		//
		// State 2 (`CmdLevelChange_Update` 0x823D5AA8, jump-table case 1) only waits for the
		// screen fader the command started (`sub_82180F28` at 0x823D5BA4; 1.5 s from
		// `flt_8209CBBC` at 0x82350CF0, for doors, teleporters and fast travel alike). When the
		// fade is done, ONE tick shows the loading screen (0x823D5C30), destroys the entities
		// (`sub_823D6798` at 0x823D5F9C) and stores state 3 (0x823D6054). So all through state 2
		// the old region is alive and fading out under the guest's fader. Freeing it at state 2
		// played that whole fade over an empty frame, which read as a stall after every fast
		// travel and door.
		//
		// `game+0x105` is no use here: it is not cleared until state 3's blocking load returns.
		//
		// FULLLOAD DECIDES WHETHER THERE IS ANYTHING TO TAKE DOWN AT ALL. With `cmd+0x32` clear
		// the guest tears nothing down — state 3 takes the player-manager branch instead of the
		// load, and the teardown is not reachable from it. That is a reposition inside the
		// resident level, so the region must stay standing; freeing it would rebuild a world the
		// guest never unloaded.
		HostBridge.LevelChange? inFlight = HostBridge.GetLevelChange();

		// ONLY IF WHAT IS STANDING IS NOT WHERE THE GUEST IS GOING.
		//
		// Testing the state alone tears down the region we have just built. The command stays in
		// the queue until state 9 retires it, so the frame after mounting the destination still
		// sees "a full load at state >= 3" and frees it again — a full rebuild of a region that
		// was already correct, repeating for as long as the command lives. The destination is
		// right there on the command; compare it.
		if (_loaded && inFlight is { } change && change.FullLoad && change.State >= 3)
		{
			string destination = $"{change.World}/{change.Level}";
			if (!destination.Equals(_standing, System.StringComparison.OrdinalIgnoreCase))
			{
				GD.Print($"[game-world] the guest is changing level (state {change.State}, " +
					$"travel {change.TravelType}) to {destination} — taking {_standing} down");
				TearDownRegion();
				return;
			}
		}

		// AND DO NOT BUILD FROM A DESTINATION THAT DOES NOT EXIST YET.
		//
		// The new Level object is not constructed until state 3, so before that
		// `GetCurrentLevel` still names the level being LEFT — and building on it rebuilds the
		// region the player is walking away from. Worse, the completion lines name the CURRENT
		// level, so the log reports the destination over the old level's geometry: a level
		// change that silently rebuilds where you came from is indistinguishable from one that
		// never happened.
		if (inFlight is { FullLoad: true, State: < 3 })
		{
			return;
		}

		bool ready = HostBridge.WorldIsReady();

		// START BUILDING AS SOON AS THE DESTINATION EXISTS — MOUNT ONLY WHEN THE GUEST IS READY.
		//
		// These are two different moments and treating them as one wasted the whole overlap. Our
		// region build measured 18115 ms; gating its START on `ready` put all of that AFTER the
		// guest had finished loading, so the player waited for the guest and then waited for us.
		//
		// The new Level object is constructed in state 3, so from state 3 onward
		// `GetCurrentLevel` already names the destination — and states 4 through 8 (net ack,
		// entrance placement, fade, render fence, peer acks) all run while we build. That is the
		// preload: our work overlaps the guest's instead of following it.
		//
		// NOT EARLIER THAN STATE 3, deliberately. Before it the live Level is still the OLD one,
		// so `GetActiveScenario` — which resolves through the world's {level key -> scenario} map
		// — would hand us the previous level's scenario, and the scenario is what SELECTS THE
		// HEIGHTFIELD. That is the failure that put a childhood entrance in chapter 2's
		// Bowerstone: coherent, plausible, and the wrong map.
		if (HostBridge.GetCurrentLevel() is not { } here || here.Level.Length == 0)
		{
			return;
		}

		// THE SAME RULE WITHOUT THE COMMAND, because the command is not always findable.
		//
		// The state-3 gate above needs a CmdLevelChange in the queue, and the queue dump proved
		// we cannot count on that: a load-from-save carries CmdLoadSaveGame and no level-change
		// command at all, and even during a door transition the command is only in the queue for
		// part of it. So that guard went missing on exactly the frames that mattered, and the
		// build started against the level being LEFT.
		//
		// Remembering the key we tore down needs nothing from the queue. Whatever mechanism
		// moved the player, rebuilding the level we just left is never right — so wait until the
		// guest names a DIFFERENT one. Cleared as soon as it does.
		if (_torndownKey != 0u && here.Key == _torndownKey)
		{
			return;
		}

		_torndownKey = 0u;

		// KEYED ON THE GUEST'S OWN KEY. `here.Key` is Level+0xB4, the guest's FNV-1 of lowercase
		// "world/level", stamped by the Level constructor from `World_ComputeWorldLevelFnv1Key`.
		// Comparing that rather than a formatted name means the edge does not depend on how
		// either side spells a level, and it is one dword instead of two string builds a frame.
		//
		// Still checked even though the unload above normally fires first: a transition that
		// never clears the ready byte would otherwise leave the wrong region standing forever,
		// and this is the cheaper of the two things to be wrong about.
		if (_loaded)
		{
			if (here.Key == _standingKey)
			{
				return;
			}

			GD.Print($"[game-world] the guest is in {here.World}/{here.Level} but {_standing} " +
				$"is standing (key 0x{_standingKey:X8} -> 0x{here.Key:X8}) — taking it down");
			TearDownRegion();
			// Android builds the destination once the old region has retired (GameWorld.Android.cs).
			if (Refii.Core.Engine_Platform.IsAndroid) return;
		}

		var start = new HostBridge.StartLevel(here.World, here.Level, here.Entrance);

		if (!_announced)
		{
			_announced = true;
			GD.Print($"[game-world] the guest is going to {start.World}/{start.Level} " +
				$"(entrance '{start.Entrance}')");
		}

		// The editor's own construction path, then mount it under the live camera.
		//
		// NOTHING BELOW LATCHES _loaded UNTIL IT HAS ACTUALLY BUILT. The guest can cross into
		// the game before Engine_GameAssets has finished mounting — the mount is asynchronous
		// and TryBuildLevel answers "banks are not mounted" until it completes — so latching on
		// the attempt turns a few frames of bad luck into a permanently empty world that no
		// later frame ever retries.
		if (Refii.Assets.Engine_GameAssets.Instance is not { } assets)
		{
			return;
		}

		// THE SCENARIO, BEFORE THE BUILD. It is what selects the heightfield — a level ships one
		// per story state, and picking by name order put a childhood entrance in chapter 2's
		// Bowerstone. Taking it from the guest rather than inferring it is the whole point: the
		// guest states which map it is standing in.
		// The LIVE scenario wins; the start value is only the fallback before a level is up.
		//
		// ONLY WHILE NO BUILD IS IN FLIGHT. This runs every frame, and the worker reads the
		// scenario off the same singleton to pick the heightfield — so continuing to rewrite it
		// during the build is a genuine race, and one whose symptom would be the wrong map
		// loading intermittently rather than anything that looks like a threading fault.
		if (_build is null &&
			(HostBridge.GetActiveScenario() ?? HostBridge.GetStartScenario())
				is { Length: > 0 } scenario)
		{
			assets.CurrentScenario = scenario.ToLowerInvariant();
			if (!_scenarioReported)
			{
				_scenarioReported = true;
				uint[] layers = HostBridge.GetActiveLayers();
				GD.Print($"[game-world] the guest's scenario is '{assets.CurrentScenario}', " +
					$"{layers.Length} layer(s) active");
			}
		}


		// STRAIGHT TO A NODE, not through a `.tscn` on disk.
		//
		// Building the region, saving it to `res://levels/...` and loading that back gets the
		// same construction through a round trip that cannot survive an export: `res://` is a
		// read-only pack in an exported build, so the save fails and the game gets no world at
		// all. The editor still saves, because a scene you can open IS the editor's point.
		//
		// AND ON A WORKER THREAD, because it measured 14312 ms in ONE frame — terrain 9357,
		// props 3634. Every millisecond of that stopped Godot's main loop: nothing drew, no
		// loading screen was possible, and the guest — which paces itself against the wall
		// clock in `sub_82276D10` and RESYNCS by dropping ticks when it falls behind — lost
		// fourteen seconds of simulation in one go.
		//
		// Only the construction moves. Godot's scene tree is not thread-safe, so the finished
		// root is handed back and mounted from `_Process` below. That split is safe because a
		// node is only tree-bound once something adds it: `AddChild` and `Owner` on a detached
		// subtree are plain object writes, which is what lets the whole region be assembled
		// away from the main thread and attached in one step.
		if (_build is null)
		{
			// The back-off from a failed attempt. Costs nothing when there has not been one.
			if (_retryDelay > 0)
			{
				--_retryDelay;
				return;
			}

			StartBuild(assets, start);
			return;
		}

		if (!_build.IsCompleted)
		{
			return;
		}

		// THE MOUNT GATE — and it must not be `ready` alone, or it deadlocks against the hold.
		//
		// `ready` is state 9. We hold the guest at state 7. So waiting for state 9 to mount, and
		// holding until we mount, is a circular wait: the run stalled at exactly this line with
		// "built and waiting for the guest to finish its own load" as its last word, forever.
		//
		// STATE 5 IS THE REAL ANSWER, and the contract already said so. Every entity a region
		// contributes is constructed in the blocking load in state 3 and ACTIVATED IN STATE 5;
		// there is no budgeted or deferred spawn path. States 6, 7 and 8 are a screen fade, this
		// render fence and co-op peer acknowledgement — not one of them touches an entity. So a
		// command that has reached state 5 has a complete entity set, and `ready` is simply a
		// later, coarser signal for the same fact.
		//
		// `ready` is still honoured for the case with no command in flight — the command retires
		// itself in state 9, so anything that arrives after that has only the byte to go on.
		//
		// (Entities can still appear later, but only on demand: the deferred store at
		// entityMgr+160 instantiates one at a time by uid when a quest or AI asks. GameEntities
		// polls every frame, so those arrive on their own.)
		// AND A CEILING ON IT. Any wait whose release depends on the host correctly reading guest
		// memory can be wrong, and when it is wrong the player gets a frozen loading screen with
		// no recourse. Mounting a few frames early is a cosmetic risk; never mounting is not.
		// THE GUEST'S OWN "the world is activated" BIT, and it is the answer for both paths.
		//
		// `World+0xA8` is set by the entity activator `sub_823640E0` and cleared by the world
		// teardown. Travel reaches the activator from level-change state 5; a load-from-save
		// reaches the SAME function from its own state 2. So this one poll replaces both of the
		// mechanism-specific gates below, and needs nothing walked or matched.
		//
		// The other two stay as corroboration, and the frame limit stays as a backstop — but if
		// this works they should never be what decides, and the log will say which one did.
		bool active = HostBridge.WorldIsActive();
		bool activeSupported = (HostBridge.TryGetAbiVersion() ?? 0) >= 50;
		bool hasFullLoadInFlight = inFlight is { FullLoad: true };
		int inFlightState = (int)(inFlight?.State ?? 0u);
		bool guestEntitiesReady = AreGuestEntitiesReadyForMount(
			active, ready, activeSupported, hasFullLoadInFlight, inFlightState, waitFrames: 0, waitLimit: 1);
		bool entitiesComplete = guestEntitiesReady || _entityWaitFrames >= EntityWaitFrameLimit;

		if (!entitiesComplete)
		{
			++_entityWaitFrames;
			// NAMES WHAT IT IS WAITING FOR, and keeps saying so. "Waiting for the guest", said
			// once, is true and useless: with three signals feeding this gate the only useful
			// line is the one that prints all of them.
			if (_entityWaitFrames % EntityWaitReportFrames == 0)
			{
				_waitingForGuest = true;
				GD.Print($"[game-world] {start.World}/{start.Level} is built; waiting for the " +
					"guest to activate its entities (needs level-change state 5, currently " +
					$"{(inFlight is { } s ? s.State.ToString() : "no command in flight")}, " +
					$"worldReady={ready}, worldActive={active})" +
					// The queue itself when we cannot find the command in it. `worldReady` false
					// with no command found is a contradiction — the byte is only clear between
					// state 3 and state 9 — so one of the two reads is wrong, and this says which.
					(inFlight is null
						? $"\n    gameflow queue: {HostBridge.DescribeGameFlowQueue()}"
						: string.Empty));
			}

			return;
		}

		if (_entityWaitFrames >= EntityWaitFrameLimit && !guestEntitiesReady)
		{
			GD.PrintErr($"[game-world] the guest never reported its entities ready for " +
				$"{start.World}/{start.Level} after {_entityWaitFrames} frame(s) " +
				$"(worldActive={active}, worldReady={ready}, level change " +
				$"{(inFlight is { } c ? $"state {c.State}" : "not in the queue")}) — " +
				"mounting anyway rather than leaving the world unreachable.");
		}

		_waitingForGuest = false;
		_entityWaitFrames = 0;

		Node3D? region = TakeBuiltRegion(start, out bool failed);
		if (region is null)
		{
			// A FAILED BUILD MUST NOT LATCH — but it must not spin either.
			//
			// `_loaded` stays false and `_build` clears so a later frame retries, because the
			// guest can reach the game before the banks finish mounting and treating those few
			// frames as fatal left a permanently empty world.
			//
			// But a build that failed for a REASON (rather than for "not mounted yet") will fail
			// again on the next frame with the same inputs, so retrying at frame rate cannot
			// help — it only spawns a task per frame and buries the real error under its own
			// repetition. Back off, and say so once per destination: a failure on a different
			// level is news, the same one again is not.
			// RELEASE ON FAILURE TOO. A hold that outlives its build parks the guest at state 7
			// forever, loading music playing, which is indistinguishable from a hung load — and
			// far worse than the empty world the failure would otherwise leave.
			SetWorldBuildHeld(false);
			_build = null;
			_retryDelay = RetryFrames;

			if (failed)
			{
				string destination = $"{start.World}/{start.Level}";
				if (destination != _reportedFailure)
				{
					_reportedFailure = destination;
					GD.PrintErr($"[game-world] could not build {destination}: {_buildError}");
				}
			}

			return;
		}

		_loaded = true;
		ReadyForWarmupAndSettle = false;
		System.Threading.Interlocked.Exchange(ref _activeWorldFrames, 0);
		System.Threading.Interlocked.Exchange(ref _settleFrames, 0);
		HasActiveWorld = true;
		_standing = $"{start.World}/{start.Level}";
		_standingKey = here.Key;
		_region = region;
		_holdingForProps = true;
		_propHoldFrames = 0;
		_readyFrames = 0;
		long mountFrom = System.Diagnostics.Stopwatch.GetTimestamp();
		System.TimeSpan mountPause = System.GC.GetTotalPauseDuration();
		Dx12Skip3dMask skip3d = ResolveDx12Skip3d();
		ApplyDx12Skip3d(region, skip3d);
		AddChild(region);
		Refii.Core.Engine_FramePhases.ArmDrawTrace("mount", 2);
		double mountTreeMs = System.Diagnostics.Stopwatch.GetElapsedTime(mountFrom).TotalMilliseconds;
		Refii.Models.Engine_ClassicLoad.Begin(region);
		Refii.Models.Engine_PipelineWarmup.Begin();

		SetStreamBudget(region, HeldStreamMsPerFrame);

		if ((skip3d & (Dx12Skip3dMask.Water | Dx12Skip3dMask.Reflections)) == 0)
		{
			MountWaterReflections(region);
		}

		// The sun's far cascades exist for the landscape; this stops every crate in 300 metres
		// riding along in them. Godot's sun casts nothing under classic (the guest's cascades
		// take the casters, each with its own stated reach), so there it has nothing to do.
		if (!Refii.Models.Engine_ClassicMaterial.Active)
		{
			region.AddChild(new Refii.Lighting.Engine_ShadowReach
			{
				Name = Refii.Lighting.Engine_ShadowReach.NodeName,
			});
		}

		// NOT RELEASED HERE. Mounting is not the same as being ready to look at.
		//
		// `Engine_PropStreamer` pages props in over frames AFTER the mount, at 25 ms a frame, so
		// the region enters the tree as terrain and fills in afterwards. Releasing on AddChild
		// let the guest resume into exactly that: a screenshot of bare ground under a white sky
		// with the hero walking through it. The hold now runs until the streamer has drained —
		// see `ReleaseWhenPropsHaveStreamed`.

		// THE REGION'S OWN COPY OF EVERY ENTITY THE GUEST PLACES, indexed so it can be retired
		// one record at a time as the guest claims it.
		//
		// Both halves of the region are built from the level's `.save` map and so is the guest,
		// so a door is drawn twice — a ghost you cannot interact with standing inside the one
		// you can. The obvious cure is to delete the static half outright and it is wrong twice
		// over: the props container also holds foliage, engine_level blocks and companion parts
		// that no `.save` record names and the guest never states, and of the guest's 1437 live
		// entities only 1030 name a model at all, so the rest have no live copy to replace the
		// static one with. Freeing the whole container emptied the street.
		//
		// The container is handed to GameEntities alive. Freeing it here and passing the freed
		// node on the next statement leaves nothing for the one-at-a-time retirement to do —
		// `IsInstanceValid` is already false when the first entity arrives.
		Refii.Core.Engine_LoadTimeline.Mark("mounted");
		long twinsFrom = System.Diagnostics.Stopwatch.GetTimestamp();
		var twins = Refii.Entities.Engine_StaticTwins.Build(region);
		long twinsTicks = System.Diagnostics.Stopwatch.GetTimestamp() - twinsFrom;
		Refii.Core.Engine_LoadTimeline.Add(Refii.Core.Engine_LoadTimeline.Category.StaticTwins,
			twinsTicks, twins.Instances);
		GD.Print($"[game-world] the guest owns entities here; {twins.Instances} static " +
			$"drawable(s) across {twins.Records} `.save` record(s) are waiting to be retired");

		// Alongside the region, not inside it: the live entities carry the whole guest-to-Godot
		// map themselves, exactly as the camera does.
		Refii.Level.Engine_LevelScene.TryGetLevelGdbView(region, out Refii.Gdb.GdbView? levelGdbView);
		Refii.Entities.Engine_CorpsePolicy.SetActiveLevelGdb(levelGdbView);
		if ((skip3d & Dx12Skip3dMask.Entities) == 0)
		{
			_entities = new GameEntities
			{
				Name = "GameEntities",
				Twins = twins,
			};
			AddChild(_entities);
		}

		// The effects the guest plays for itself, beside the entities and for the same reason:
		// the region's own FX are the ones the level STATES, and every effect that starts because
		// something happened — a spell, an impact, a fire catching — exists only in the guest.
		// It censuses by default and draws only under REFII_FX_LIVE=1; see GameFx.
		if ((skip3d & Dx12Skip3dMask.Fx) == 0)
		{
			_fx = new GameFx { Name = "GameFx" };
			AddChild(_fx);
		}

		// Zero-energy lights paired with every mesh, so Mobile's per-draw pipeline stops
		// changing with the lamps in reach (`stable_light_counts`, off unless configured).
		Refii.Lighting.Engine_StableLightCounts.Configure(Refii.Core.Engine_RefiiConfig.Read(out _));
		Refii.Lighting.Engine_StableLightCounts.Mount(this);
		GD.Print($"[game-world] built and mounted {start.World}/{start.Level} in " +
			$"{System.Diagnostics.Stopwatch.GetElapsedTime(mountFrom).TotalMilliseconds:0} ms (tree {mountTreeMs:0}, " +
			$"static twins {twinsTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0}, " +
			$"gc pause {(System.GC.GetTotalPauseDuration() - mountPause).TotalMilliseconds:0})");
	}

	/// <summary>
	/// Frees the standing region so the next one can be built.
	///
	/// BOTH NODES, and the entity feed FIRST. `GameEntities` holds the region's static-twin
	/// map and one node per live entity keyed on a guest address; leaving it up across a level
	/// change would have it reconciling this region's records against the next region's
	/// entities, which is not a wrong picture so much as two worlds mixed together.
	///
	/// `QueueFree` rather than `Free`: this runs from `_Process`, which is inside the very
	/// tree walk that would be mutated, and freeing a node out from under that is the one way
	/// to crash a Godot frame from managed code. The nodes are detached immediately so nothing
	/// finds them again in the meantime, and reclaimed at the end of the frame.
	///
	/// THE TEARDOWN POLICY: desktop releases the region's caches and build hold here, in
	/// <see cref="ReleaseRegionAtOnce"/>; Android retires the region over the next frames
	/// instead, from <see cref="BeginRegionRetirement"/> (GameWorld.Android.cs).
	/// </summary>
	private void TearDownRegion()
	{
		Refii.Level.Engine_PropStreamer.StopAll();

		bool android = Refii.Core.Engine_Platform.IsAndroid;
		if (android) BeginRegionRetirement();
		if (_entities is not null)
		{
			RemoveChild(_entities);
			_entities.QueueFree();
			_entities = null;
		}
		Refii.Entities.Engine_CorpsePolicy.SetActiveLevelGdb(null);

		// Discard any pre-teardown or in-flight entity walk and clear guest word-diff state so
		// entities captured from the outgoing region cannot feed into the incoming region.
		HostEntityWalk.Discard();
		HostBridge.ResetEntityWalkState();

		// Alongside the entity feed, and for its reason: this holds one node per LIVE guest
		// effect keyed on a guest pointer, and the guest reuses those addresses. Left standing
		// across a level change it would match the next region's effects against this region's
		// nodes — not a wrong picture so much as two worlds mixed together.
		if (_fx is not null)
		{
			RemoveChild(_fx);
			_fx.QueueFree();
			_fx = null;
		}

		if (_reflections is not null)
		{
			// BEFORE the region, not after. These viewports hold a texture the region's water
			// materials point at, and a level change frees the region out from under them.
			RemoveChild(_reflections);
			_reflections.QueueFree();
			_reflections = null;
		}

		if (_region is not null)
		{
			RemoveChild(_region);
			_region.QueueFree();
			_region = null;
		}

		// QueueFree has not completed here. Android measures a separate retirement boundary;
		// desktop retains its established timing. Dictionary clears do not guarantee GPU release.
		if (!android) ReleaseRegionAtOnce();

		// REMEMBERED BEFORE IT IS CLEARED. Everything below forgets what was standing, and the
		// one thing the next build must not do is recreate it.
		_torndownKey = _standingKey;

		EndHostAim(forwardResidualDelta: false);
		_cameraDirty = true;
		LastMouseLook = null;

		_loaded = false;
		HasActiveWorld = false;
		ReadyForWarmupAndSettle = false;
		System.Threading.Interlocked.Exchange(ref _activeWorldFrames, 0);
		System.Threading.Interlocked.Exchange(ref _settleFrames, 0);
		_standing = string.Empty;
		_standingKey = 0u;
		_waitingForGuest = false;
		_holdingForProps = false;
		Refii.Lighting.Engine_GiRefresh.HoldForWorldLoad = false;
		Refii.Models.Engine_ClassicLoad.End();
		Refii.Models.Engine_PipelineWarmup.End();
		Refii.Models.Engine_ClassicLoad.Abandon();
		Refii.Models.Engine_ClassicVertexBakes.Clear();
		Refii.Models.Engine_ClassicPrtRows.Clear();
		if (_entities is { } tornDown)
		{
			tornDown.LoadHold = false;
		}

		_entityWaitFrames = 0;
		_build = null;
		_buildError = string.Empty;

		// The once-only reports are once PER REGION, not once per run. Left latched, the second
		// level would build in silence and the log would read as though nothing had happened —
		// and the scenario line matters most exactly here, since a level change is when the
		// guest is most likely to name a different one.
		_announced = false;
		_scenarioReported = false;

		// AND THE CAPPED ONES TOO, for the same reason and a sharper one. `TraceEntities` stops
		// after ten reports, and those ten were being spent on the first two regions of a run —
		// so Ravenscar, the region that actually had an entity problem, produced no
		// `[guest-entities]` line at all and the live count could not be read off the log. A
		// rate limit that never resets measures whichever level happened to load first.
		_entityReports = 0;
		_entityTicks = 0;

		// Per region for the same reason: a run total cannot say whether the level now standing
		// is the one whose models are not arriving.
		Refii.Models.Engine_BnkMeshLoader.ResetEmptyMeshCounts();
	}

	/// <summary>
	/// Desktop's teardown, in one place: the region's caches, its VRAM, and the build hold.
	/// </summary>
	private void ReleaseRegionAtOnce()
	{
		Refii.Core.Engine_LevelCaches.ReleaseRegion();

		// QueueFree() destroys the native nodes at the end of the frame. Running a deferred GC +
		// finalizer pass right after the delete queue flushes ensures the outgoing region's
		// Texture2D, Texture2DArray, ArrayMesh and ShaderMaterial RefCounted wrappers release
		// their VRAM before the incoming region mounts and streams.
		Callable.From(CompactImmediateOnTeardown).CallDeferred();

		// A teardown can land while a build is in flight — the guest changing its mind mid-load.
		// The next StartBuild re-asserts it, so clearing here cannot let the guest through early
		// but does guarantee no hold survives a region that no longer exists.
		SetWorldBuildHeld(false);
		_builtRegion = null;
	}

	/// <summary>
	/// Reports the guest's live entities — the plan's phase 1, proved before anything is drawn
	/// from it.
	///
	/// Three numbers decide whether the feed is usable, and none of them needs a mesh:
	/// how many entities came back, how many were SKIPPED for an unrecognised placement vtable
	/// (the set of physics delegate classes could not be proven closed, so those are counted
	/// rather than guessed), and how many model hashes resolve through the prop index.
	///
	/// That last one settles an open question by measurement instead of argument. The guest
	/// stores a resource-name hash for ModelFile and our index keys on FNV-1 of the lowercased
	/// path; whether those are the same family was NOT established from the binary, so the
	/// honest test is to hash-match real entities and count the hits.
	/// </summary>
	private void TraceEntities()
	{
		if (!_loaded || ++_entityTicks % EntityReportFrames != 0 || _entityReports >= EntityReportLimit)
		{
			return;
		}

		System.Collections.Generic.List<HostBridge.GuestEntity> entities =
			HostBridge.PeekEntities(out uint unknown, out HostBridge.UnknownPlacement[] unknownVtables);
		if (entities.Count == 0 && unknown == 0)
		{
			return;
		}

		++_entityReports;

		// Three buckets, not two. "685 of 1049 resolved" reads as a 35% index miss and is
		// not one: an entity carrying NO model hash is a trigger or a marker and was never
		// going to draw. The miss rate that matters is against the entities that do name a
		// model, and conflating the two is the same wrong-denominator mistake that sent a
		// whole turn after a parser defect that did not exist.
		int withModel = 0;
		int resolved = 0;

		// WHAT THE MODELLESS ONES ACTUALLY ARE, rather than the assumption above.
		//
		// The comment says a modelless entity "is a trigger or a marker and was never going to
		// draw", and that has never been checked. It matters more than it reads: those entities
		// are the ENTIRE justification for building a second, static copy of the region from
		// the `.save`, which is what Engine_StaticTwins then spends the whole session hiding —
		// 1310 of 1674 copies in the measured region — and what leaves a ghost standing when
		// the guest destroys its live one. If they are markers, the static layer is carrying
		// nothing and can go. If they carry a graphic component we do not read, they are the
		// naked-hero bug again: geometry the guest states through a route we have not learned.
		//
		// Tallied by COMPONENT ID because that is what the guest states and what the class
		// registry names. Presence only — the pointer half is deliberately not followed.
		var modellessComponents = new System.Collections.Generic.Dictionary<byte, int>();
		int modellessSampled = 0;

		// Fable 3 only: the sampled modelless entities that are neither a light (component 8,
		// CECGraphicAppearanceLight, whose render part is never a mesh) nor a composite
		// character (component 20, CECGraphicAppearanceMorph), listed by address with their
		// whole component signature — the ones still to be explained.
		bool listRemainder = Engine_Title.Active == Engine_Title.Fable3;
		var modellessRemainder = new System.Text.StringBuilder();
		int modellessRemainderCount = 0;

		foreach (HostBridge.GuestEntity entity in entities)
		{
			if (entity.ModelHash == 0)
			{
				// Capped: this is one bridge call per entity and the answer saturates long
				// before the population does.
				if (modellessSampled < 512)
				{
					++modellessSampled;
					byte[] ids = HostBridge.GetEntityComponents(entity.Address);
					foreach (byte id in ids)
					{
						modellessComponents.TryGetValue(id, out int seen);
						modellessComponents[id] = seen + 1;
					}

					if (listRemainder && System.Array.IndexOf(ids, (byte)8) < 0 &&
						System.Array.IndexOf(ids, (byte)20) < 0)
					{
						++modellessRemainderCount;
						modellessRemainder.Append($"\n    0x{entity.Address:X8} components=[{string.Join(",", ids)}]");
					}
				}

				continue;
			}

			++withModel;
			if (Refii.Level.Engine_PropModelIndex.Resolve(
					entity.ModelHash, System.Array.Empty<string>()) is not null)
			{
				++resolved;
			}
		}

		// Descending by tally: the first address on the line is the one worth decompiling.
		System.Array.Sort(unknownVtables, (a, b) => b.Entities.CompareTo(a.Entities));
		string vtables = unknownVtables.Length == 0
			? string.Empty
			: "  unknown vtables: " +
			  string.Join(", ", System.Array.ConvertAll(
				  unknownVtables, v => $"0x{v.Vtable:X8}x{v.Entities}"));

		GD.Print($"[guest-entities] live={entities.Count} unknownPlacement={unknown} " +
			$"withModel={withModel} (noModel={entities.Count - withModel}) " +
			$"resolved={resolved}/{withModel}{vtables}");

		if (modellessSampled != 0)
		{
			var byCount = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<byte, int>>(
				modellessComponents);
			byCount.Sort((a, b) => b.Value.CompareTo(a.Value));

			var top = new System.Collections.Generic.List<string>();
			for (int i = 0; i < byCount.Count && i < 16; i++)
			{
				// A component on EVERY sampled entity is structural and says nothing; one on a
				// subset is what separates the classes.
				top.Add($"{byCount[i].Key}x{byCount[i].Value}");
			}

			GD.Print($"[guest-entities] modelless sample {modellessSampled} entity(s), " +
				$"{modellessComponents.Count} distinct component id(s) — most common: " +
				string.Join(" ", top));
			if (listRemainder)
			{
				GD.Print($"[guest-modelless] {modellessRemainderCount} of {modellessSampled} sampled " +
					$"modelless entity(s) are neither a light (component 8) nor a composite " +
					$"(component 20):{modellessRemainder}");
			}
		}

		TracePoses(entities);

		// Concrete rows, because a count alone cannot say whether the transforms are sane —
		// an entity at the origin with an identity rotation is what a wrong offset looks like.
		//
		// UNRESOLVED ones by preference. The first few entities in list order were all
		// resolving already, so the rows confirmed what was working and said nothing about
		// the 364 that were not; these are the hashes to go and look up.
		int shown = 0;
		foreach (HostBridge.GuestEntity entity in entities)
		{
			if (shown >= 4 || entity.ModelHash == 0 ||
				Refii.Level.Engine_PropModelIndex.Resolve(
					entity.ModelHash, System.Array.Empty<string>()) is not null)
			{
				continue;
			}

			++shown;
			GD.Print($"    unresolved 0x{entity.Address:X8} pos={entity.Position} " +
				$"rot={entity.Rotation} model=0x{entity.ModelHash:X8} " +
				$"record=0x{entity.RecordHash:X8}");
		}
	}

	/// <summary>
	/// Reports which live entities are CHARACTERS, and whether their bones are readable.
	///
	/// A character is an entity carrying an animated mesh, and the pose the guest's animation
	/// system evaluated is left in that component. Proving the feed before drawing anything
	/// from it is the same discipline the placement read got: a rig posed from a wrong offset
	/// does not look like a bad offset, it looks like a broken character.
	///
	/// The space is reported because the guest states it. The bones are written LOCAL and
	/// converted to model space in place, so a pose caught mid-update is local — and only the
	/// guest can say which one we are holding.
	/// </summary>
	private void TracePoses(System.Collections.Generic.List<HostBridge.GuestEntity> entities)
	{
		int characters = 0;
		int modelSpace = 0;
		int localSpace = 0;
		int unposed = 0;
		int bones = 0;
		int scaled = 0;
		float scaleLow = float.PositiveInfinity;
		float scaleHigh = float.NegativeInfinity;
		string sample = string.Empty;
		string chainSample = string.Empty;

		// Characters the draw path cannot reach: a live skeleton and no model hash.
		int modelless = 0;
		int modellessWithMorph = 0;
		byte morphComponent = Engine_Title.Active == Engine_Title.Fable3 ? (byte)20 : (byte)17;
		string modellessSample = string.Empty;

		foreach (HostBridge.GuestEntity entity in entities)
		{
			if (HostBridge.GetEntityPose(entity.Address) is not { } pose)
			{
				continue;
			}

			++characters;
			bones += pose.BoneCount;

			// THREE buckets. "modelSpace=12 of 138" invited the reading that the other 126 were
			// local, and a local pose is usable — you compose it. A pose the animation system
			// has never written is not, and it was being composed anyway.
			switch (pose.Space)
			{
				case 2: ++modelSpace; break;
				case 1: ++localSpace; break;
				default: ++unposed; break;
			}

			// THE SCALE LANES, PROVEN RATHER THAN ASSUMED. The 48-byte element is read as
			// quaternion / translation / scale, and the third of those is the only field
			// nothing has ever printed — it went straight into the bone basis on the strength
			// of the layout alone. If these are the guest's scales they are 1 almost
			// everywhere; if the lanes are actually something else the extremes say so
			// immediately, and a wrong triple multiplied into every bone is another way to
			// tear a rig apart that looks nothing like a lane mix-up.
			for (int b = 0; b < pose.BoneCount; ++b)
			{
				Vector3 bone = pose.Scale(b);
				float low = Mathf.Min(bone.X, Mathf.Min(bone.Y, bone.Z));
				float high = Mathf.Max(bone.X, Mathf.Max(bone.Y, bone.Z));
				scaleLow = Mathf.Min(scaleLow, low);
				scaleHigh = Mathf.Max(scaleHigh, high);
				if (Mathf.Abs(bone.X - 1f) > 0.01f || Mathf.Abs(bone.Y - 1f) > 0.01f ||
					Mathf.Abs(bone.Z - 1f) > 0.01f)
				{
					++scaled;
				}
			}

			// The characters with no model hash, counted against the route that can still
			// draw them.
			//
			// An entity with a live skeleton and no model hash is one component 6 cannot
			// reach: `sub_8257E0C0` returns 0 for a zero hash with no fallback. Their geometry
			// is the part LIST on CECGraphicAppearanceMorph, component id 17 (Fable 3: 20, its
			// built list at +0x38/+0x3C), so whether they carry that id at all decides whether
			// anything can draw them — and that is a live-memory fact no amount of reading the
			// binary answers.
			//
			// Only the ids are read here; the parts themselves come from
			// RefiiHostGetEntityMorphParts, which knows the component's layout.
			if (entity.ModelHash == 0)
			{
				++modelless;

				byte[] ids = HostBridge.GetEntityComponents(entity.Address);
				bool hasMorph = System.Array.IndexOf(ids, morphComponent) >= 0;
				if (hasMorph)
				{
					++modellessWithMorph;
				}

				if (modelless <= 3)
				{
					modellessSample += $"\n    modelless character 0x{entity.Address:X8} " +
						$"bones={pose.BoneCount} components=[{string.Join(",", ids)}] " +
						$"morph{morphComponent}={(hasMorph ? "PRESENT" : "absent")}";
				}
			}

			// IS A "LOCAL" POSE ACTUALLY LOCAL? The one measurement that decides the explosion.
			//
			// Every rig that explodes does so by compounding down the chain, and each link adds
			// roughly the bone's REST offset rather than a bone-length delta — which is what
			// composing already-absolute transforms looks like. But that is an inference from
			// bounds, and the two possibilities need opposite fixes, so here is the direct test:
			// walk a parent chain and print each bone's stated translation.
			//
			// If they are LOCAL they are bone lengths and stay small and roughly constant with
			// depth. If they are already MODEL space they grow monotonically along the chain,
			// because each one is an absolute position further up the body. No arithmetic, no
			// theory — the numbers say which.
			if (chainSample.Length == 0 && pose.Space == 1 && pose.BoneCount > 24)
			{
				var chain = new System.Collections.Generic.List<int>();
				for (int b = pose.BoneCount - 1; b >= 0 && chain.Count < 6; b = pose.Parents[b])
				{
					chain.Add(b);
					if (pose.Parents[b] < 0 || pose.Parents[b] >= b)
					{
						break;
					}
				}

				chain.Reverse();
				var text = new System.Text.StringBuilder();
				foreach (int b in chain)
				{
					Vector3 t = pose.Translation(b);
					text.Append($"\n        bone[{b}] parent={pose.Parents[b]} " +
						$"t={t} |t|={t.Length():0.###}");
				}

				chainSample = $"\n    LOCAL-space chain on 0x{entity.Address:X8} " +
					"(|t| roughly constant with depth = genuinely local; growing = already " +
					$"model space and we are double-composing):{text}";
			}

			if (sample.Length == 0)
			{
				// Bone 0 and one mid-rig bone: a rig whose every bone sits at the origin is
				// what an array that was sized but never filled looks like, and it counts the
				// same as a good one until someone prints a position.
				int mid = pose.BoneCount / 2;
				sample = $"\n    0x{entity.Address:X8} bones={pose.BoneCount} " +
					$"space={pose.Space} root={pose.Translation(0)} " +
					$"bone{mid}={pose.Translation(mid)} rot{mid}={pose.Rotation(mid)} " +
					$"scale{mid}={pose.Scale(mid)} parent{mid}={pose.Parents[mid]}";
			}
		}

		// IS THE FACING EVEN COMING THROUGH? The measurement behind "everyone faces one way".
		//
		// A character's orientation reaches Godot through exactly one value — the quaternion in
		// the placement read — and several of the EC-2 layouts do not load one directly: the
		// hybrids delegate and fall back to IDENTITY when the chain yields nothing. If that is
		// what is happening, every affected character gets the same rotation forever, which is
		// precisely the symptom. Counted against NON-characters as the control: if props are
		// mostly oriented and characters are mostly identity, the fault is specific to the
		// class characters use and not to the read as a whole.
		int characterIdentity = 0;
		int characterOriented = 0;
		int otherIdentity = 0;
		int otherOriented = 0;

		foreach (HostBridge.GuestEntity entity in entities)
		{
			bool isCharacter = HostBridge.GetEntityPose(entity.Address) is not null;
			bool identity = Mathf.Abs(entity.Rotation.X) < 1e-5f &&
				Mathf.Abs(entity.Rotation.Y) < 1e-5f &&
				Mathf.Abs(entity.Rotation.Z) < 1e-5f;

			if (isCharacter)
			{
				if (identity) { ++characterIdentity; } else { ++characterOriented; }
			}
			else
			{
				if (identity) { ++otherIdentity; } else { ++otherOriented; }
			}
		}

		GD.Print($"[guest-facing] characters: {characterIdentity} IDENTITY rotation, " +
			$"{characterOriented} oriented | everything else: {otherIdentity} identity, " +
			$"{otherOriented} oriented — characters nearly all identity means the facing never " +
			"reaches us and no amount of bone work will turn them round");

		GD.Print($"[guest-poses] characters={characters} model={modelSpace} local={localSpace} " +
			$"NEVER-POSED={unposed} bones={bones} " +
			$"| scale lanes: {scaled} of {bones} bones are not unit, range " +
			$"{(bones == 0 ? 0f : scaleLow)}..{(bones == 0 ? 0f : scaleHigh)} " +
			$"| left in bind: " +
			$"noPose={Refii.Entities.Engine_EntitySkeleton.NoPose} " +
			$"countMismatch={Refii.Entities.Engine_EntitySkeleton.CountMismatch}" +
			$"{Refii.Entities.Engine_EntitySkeleton.LastMismatch} " +
			$"neverPosed={Refii.Entities.Engine_EntitySkeleton.NeverPosed}{sample}{chainSample}");

		// Reported separately from the pose counts above, because it says where a character
		// with no model hash gets its geometry: the morph component is the composite route
		// (drawn from its part records, by design), and only a modelless character WITHOUT it
		// is unreachable by either path.
		int unreachable = modelless - modellessWithMorph;
		GD.Print($"[guest-modelless] {modelless} of {characters} character(s) have a live " +
			$"skeleton and no model hash; {modellessWithMorph} carry component {morphComponent} " +
			$"(CECGraphicAppearanceMorph) and draw as composites, {unreachable} name no mesh " +
			$"by any route{modellessSample}");
	}

	private int _entityTicks;
	private int _entityReports;

	private bool _loaded;

	/// <summary>
	/// "World/Level" of the region currently standing, empty when none is. Compared against
	/// what the guest names so a level change is noticed rather than assumed never to happen.
	/// </summary>
	private string _standing = string.Empty;

	/// <summary>
	/// The guest's own key for the standing region — Level+0xB4, its FNV-1 of lowercase
	/// "world/level". This is what the level-change edge compares; <see cref="_standing"/> is
	/// carried alongside it only so the log can name what moved.
	/// </summary>
	private uint _standingKey;

	/// <summary>
	/// The key of the region most recently torn down, held until the guest names a different
	/// one. Zero when nothing is pending. This is what stops a transition from rebuilding the
	/// level it just left, and unlike the level-change command it is always available.
	/// </summary>
	private uint _torndownKey;

	/// <summary>The standing region and its entity feed, held so they can be taken down.</summary>
	private Node3D? _region;

	private Refii.Level.Engine_WaterReflection? _reflections;

	/// <summary>
	/// Gives every water body in the region a mirrored view of the world to reflect.
	///
	/// Mounted HERE rather than in <c>Engine_LevelScene</c> because a planar reflection needs the
	/// camera it is a reflection of, and the scene builder has none — it runs on a worker and
	/// produces a detached node. GameWorld owns the camera and the region both, so it is the one
	/// place where the two meet.
	/// </summary>
	private void MountWaterReflections(Node3D region)
	{
		if (region.GetNodeOrNull<Node3D>(Refii.Level.Engine_LevelScene.WaterNodeName)
			is not Node3D water)
		{
			return;
		}

		_reflections = new Refii.Level.Engine_WaterReflection
		{
			Name = Refii.Level.Engine_WaterReflection.NodeName,
		};

		// Added before Mount so the SubViewports are inside the tree when they are asked for their
		// textures — a detached SubViewport has no render target and hands back nothing.
		AddChild(_reflections);
		_reflections.Mount(water, _camera);
	}

	private GameEntities? _entities;

	/// <summary>The live entity feed, for a sibling that needs one entity's node (the co-op mannequin).</summary>
	public GameEntities? Entities => _entities;

	private GameFx? _fx;

	/// <summary>The destination has been reported. Separate from <see cref="_loaded"/>, which
	/// only becomes true once the region is actually standing.</summary>
	private bool _announced;


	/// <summary>Frames to wait before retrying a failed build. Zero means retry at once.</summary>
	private int _retryDelay;

	/// <summary>Said once per transition, so "we are ahead of the guest" is visible without
	/// writing it every frame — which is the mistake that made the last log unreadable.</summary>
	private bool _waitingForGuest;

	/// <summary>Roughly two seconds at 60 fps — often enough to catch a late bank mount, rare
	/// enough that an unresolvable level does not flood the log or the thread pool.</summary>
	private const int RetryFrames = 120;

	/// <summary>The destination whose failure has already been reported, so the same one is not
	/// said twice while a different one still is.</summary>
	private string _reportedFailure = string.Empty;

	private bool _scenarioReported;

	/// <summary>
	/// Fable world space to Godot world space.
	///
	/// THE NET MAP THE WORLD IS BUILT WITH, which takes TWO steps to read off and is wrong
	/// if you stop after the first.
	///
	/// Step one is per placement: <see cref="Refii.Level.Engine_LevelProps.BuildTransform"/>
	/// puts a prop at <c>(X, Z, Y)</c>, which on its own is a MIRROR — determinant -1. Step two is the level root:
	/// <c>Engine_LevelScene.TryBuild</c> gives it <c>Scale = (-1, 1, 1)</c>, a second mirror.
	/// The two compose to <c>(x, y, z) -> (-x, z, y)</c>, determinant +1 — a proper
	/// rotation. THE WORLD IS NOT MIRRORED; it just gets there in two moves.
	///
	/// This camera is a SIBLING of the mounted region, not a child of it, so it does not
	/// inherit that root scale and has to carry the whole net map itself. That is the trap:
	/// matching only the per-placement half puts the camera in the region's INTERNAL space,
	/// which is the level reflected through X, and it is off by a mirror even though the
	/// props and the camera now "agree".
	///
	/// The guest side is settled and verified from the binary: Fable's world is right-handed
	/// with X right, Y FORWARD, Z UP — sub_821B0068 builds [cross(fwd,up), fwd, up] as a
	/// proper rotation, and sub_821DD3B0's no-camera defaults are forward = (0,1,0) at
	/// snapshot+0x20 and up = (0,0,1) at +0x10, masked out of the lane constants at
	/// 0x82099140 = (0,ffffffff,0,0) and 0x82099150 = (0,0,ffffffff,0).
	///
	/// Because the net map is a rotation and not a mirror, rebuilding the basis below as
	/// X = Y x Z lands on the correct right-handed camera rather than a reflected one.
	/// </summary>
	private static Vector3 ToGodot(Vector3 fable) => GuestToGodot(fable);

	/// <summary>
	/// THE ONE WORLD CONVERSION: a guest world point (Z up, X mirrored against Godot) to this
	/// world's space — the same map the entities, the camera and the sun take. Every host path
	/// that turns a guest world position into a Godot one goes through here; the world sprites
	/// used the MODEL handedness `(x, y, -z)` instead and sent the guest's up-lift into depth,
	/// which is why a prompt floated off its target as the camera moved.
	/// </summary>
	public static Vector3 GuestToGodot(Vector3 fable) => new(-fable.X, fable.Z, fable.Y);
}
