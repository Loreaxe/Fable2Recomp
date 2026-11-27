using System;
using Godot;
using Refii.Assets;
using Refii.Host;
using Refii.Core;
using Refii.Text;
using Refii.Textures;
using Refii.Ui;

namespace Refii.Game;

/// <summary>
/// One running game: mounts the assets, boots the guest, and steps it from <see cref="_Process"/>.
/// The guest simulates and never draws; Godot owns the window and the frame loop. Opened by the
/// launcher on Play, never before.
/// </summary>
public partial class GameSession : Node
{
	/// <summary>Emitted once the guest is up and being driven.</summary>
	[Signal] public delegate void StartedEventHandler();

	/// <summary>Emitted when the guest stops, with the reason.</summary>
	[Signal] public delegate void StoppedEventHandler(string reason);

	/// <summary>Game directory override. Empty resolves by the shared convention.</summary>
	[Export(PropertyHint.GlobalDir)] public string GameDirectory { get; set; } = string.Empty;

	/// <summary>Boot the guest as well as mounting assets; off gives the world half alone.</summary>
	[Export] public bool StartGuest { get; set; } = true;

	private bool _driving;
	private long _steps;

	/// <summary>High-frequency steps granted to the guest — the host's own simulation position.</summary>
	private long _granted;

	public bool IsDriving => _driving;

	/// <summary>
	/// The running session, so a host surface can reach the simulation clock (static: the UI is
	/// mounted under the window, not the session). Null before start and after leaving the tree.
	/// </summary>
	public static GameSession? Current { get; private set; }

	private bool _booted;
	private string _bootDirectory = string.Empty;

	public override void _Ready()
	{
		Current = this;

		// Frame lifecycle boundaries: FrameStart (priority int.MinValue) samples GPU passes,
		// clocks, and advances FramePhases.FrameIndex; FrameEnd (priority int.MaxValue) marks frame end.
		// These must run every frame unconditionally in all configurations so guest liveness caching,
		// entity bone rigging, animation playheads, and UI carousels operate properly.
		AddChild(new Refii.Core.Engine_FrameStartNode { Name = "FrameStart" });
		AddChild(new Refii.Core.Engine_FrameEndNode { Name = "FrameEnd" });

		// The hang watchdog's feed, before anything slow: the bridge's own thread writes a
		// report when the frame stamp above stops for 8 s (GameHangWatchdog, hang_watchdog.cpp).
		GameHangWatchdog.Install();

		// THE GRANT GOES LAST IN THE FRAME, after every reader. A granted step starts on the
		// guest's own thread at once, so granted at the top of the frame it ran under the camera
		// read, the walk and the rig reads, and a read could take the next step's state under
		// this frame's stamp (Engine_TickClock.FrameStamp, read at frame start). The guest
		// finishes a step with the HF pass `sub_821A61B0` and then only publishes the render
		// (`sub_82278DD0`) and spins in SwitchToThread until the next grant (sub_82276D10,
		// 0x822772a4 onward), so a frame that saw a new guest frame now reads a parked guest,
		// and the step computes while Godot renders. Before Engine_FrameEndNode, which closes
		// the frame's measurements.
		ProcessPriority = int.MaxValue - 1;

		// The classic renderer's shadow cascades, up before any level builds its materials
		// so every classic material can bind the maps as it is made.
		Refii.Models.Engine_ClassicPath.Ensure(this);

		// THE GAME'S OWN PROFILER, when `tracy` is on (restart only; see GameTracy). Tracy runs
		// whole inside the process — client and analysis connected in memory, no socket — and a
		// capture is started from the dev menu. Until then the client collects nothing
		// (`TRACY_ON_DEMAND`) and sampling is off.
		//
		// EVERY FRAME PHASE BECOMES A TRACY ZONE, installed once here: the profiler carries the
		// same 28 phases the engine measures for its own report, so a zone cannot drift from the
		// phase it names. Godot's pass timestamps go onto the same timeline as GPU zones while
		// the engine captures them (`gpu_profile = true` passes `--gpu-profile`).
		GameTracy.Start(Refii.Core.Engine_RefiiConfig.Read(out _));
		if (GameTracy.Enabled)
		{
			GD.Print("[game] Tracy is up — capture from the dev menu" +
				(GameTracy.Advanced ? " (advanced data on)" : string.Empty));
			Refii.Core.Engine_FramePhases.ZoneBegin = Refii.Host.HostBridge.TraceZoneBegin;
			Refii.Core.Engine_FramePhases.ZoneEnd = Refii.Host.HostBridge.TraceZoneEnd;
			Refii.Core.Engine_FramePhases.Plot = Refii.Host.HostBridge.TracePlot;
			Refii.Core.Engine_GpuPasses.Sink = Refii.Host.HostBridge.TraceGpuPasses;
			AddChild(new GameTracyFeed { Name = "GameTracyFeed" });
		}

		// RenderDoc, when the launcher armed its Vulkan layer for this run (`renderdoc = true`):
		// takes the in-application API so the debug channel can trigger captures into user://captures.
		if (Refii.Core.Engine_RenderDoc.Install(
				ProjectSettings.GlobalizePath("user://captures"), out string renderDocNote))
		{
			GD.Print("[renderdoc] " + renderDocNote);
		}
		else if (renderDocNote.Length != 0)
		{
			GD.Print("[renderdoc] " + renderDocNote);
		}

		// THE DEBUG CHANNEL COMES UP FIRST, BEFORE ANYTHING SLOW.
		//
		// It was attached after a successful guest boot, which is precisely when it is least
		// needed: everything before that point — the texture sweep, the shader warm-up, the
		// asset mount, the guest init itself — is a long silent phase with no diagnostic but a
		// CPU counter. On the run that proved it, the game spent fifteen minutes rebuilding an
		// invalidated texture cache while I measured thread CPU and reasoned about whether it
		// had hung; the screen said "Enhancing textures" the whole time and nothing could ask
		// it. A channel that only works once everything works is not a debug channel.
		//
		// It needs no guest: `shot` photographs the window whatever is on it, and `state`
		// reports the bridge as down rather than failing.
		if (GameDebugServer.Wanted)
		{
			AddChild(new GameDebugServer { Name = "GameDebugServer" });
		}

		string directory = Directory(GameDirectory);
		if (directory.Length == 0)
		{
			Fail("no game data found; install the game or set REFII_GAME_DATA.");
			return;
		}

		GD.Print($"[game] assets: {directory}");

		// Before the mount: a backend registered later does not enhance what already resolved.
		// Off unless REFII_TEX_UPSCALE says otherwise.
		Engine_TexEnhance.RegisterDefault();

		// Stale epochs and the size budget, on a worker; overlaps the bank walk.
		Engine_TexCache.Maintain();

		Engine_GameAssets? assets = Engine_GameAssets.Instance;
		if (assets is null)
		{
			Fail("Engine_GameAssets autoload is not present.");
			return;
		}

		if (!assets.IsMounted && !assets.IsMounting)
		{
			assets.Mount(directory);
		}

		if (!StartGuest)
		{
			GD.Print("[game] StartGuest is off — assets only, no guest.");
			return;
		}

		// THE GUEST WAITS FOR THE SHADER WARM-UP. Pipeline compilation happens on the main
		// thread the first time a material draws, and the step pump lives on that thread —
		// letting the storm land while the guest is mid-boot is how a shader edit became five
		// straight failed launches. The warm-up screen renders every shader first, overlapping
		// the asset mount's worker; _Process starts the boot once both are done.
		_bootDirectory = directory;
		Refii.Models.Engine_ShaderWarmup.Begin(this, directory);
	}

	private void Boot()
	{
		string directory = _bootDirectory;
		Engine_GameAssets? assets = Engine_GameAssets.Instance;
		if (assets is null)
		{
			return;
		}

		// The mount must finish before the guest starts, or its first asset lookups miss.
		// `Mount`'s worker publishes under the mount lock, so blocking here without pumping a
		// frame is safe.
		if (assets.IsMounting)
		{
			GD.Print("[game] waiting for the asset mount before starting the guest...");
			var waited = System.Diagnostics.Stopwatch.StartNew();
			while (assets.IsMounting && waited.Elapsed < System.TimeSpan.FromSeconds(120))
			{
				System.Threading.Thread.Sleep(10);
			}

			GD.Print(assets.IsMounted
				? $"[game] assets mounted in {waited.ElapsedMilliseconds} ms — starting the guest"
				: $"[game] the mount did not finish in {waited.ElapsedMilliseconds} ms; " +
				  "starting the guest anyway, and its early asset lookups will miss");
		}

		int? abi = HostBridge.TryGetRawAbiVersion();
		if (abi != HostBridge.ExpectedAbiVersion)
		{
			Fail($"host bridge ABI {abi?.ToString() ?? "unavailable"}, " +
				$"expected {HostBridge.ExpectedAbiVersion} — rebuild refii_hostbridge.");
			return;
		}

		// Synchronous (loads the XEX, runs guest init) and on the main thread: SDL requires it.
		GD.Print("[game] initialising guest...");
		HostBridge.ProbeResult init = HostBridge.Initialize(directory);
		GD.Print($"[game] {init.Message}");

		if (!init.Success)
		{
			Fail(init.Message);
			return;
		}

		// Input starts only once the guest is running.
		AddChild(new GameInput { Name = "GameInput" });
		AddChild(new GamePhotoMode { Name = "PhotoMode" });

		// Only after a successful boot: a failed init leaves the title's UI on its authored
		// stand-in.
		Engine_TitleGame.Active.AddSessionNodes(this);

		_driving = true;
		EmitSignal(SignalName.Started);
	}

	/// <summary>
	/// The guest's simulation rate in steps per second: bone poses at 30 Hz, game tick at 15.
	/// Independent of the display rate.
	/// </summary>
	public const double StepsPerSecond = Refii.Core.Engine_GuestPacer.StepsPerSecond;

	/// <summary>How long the pace is averaged over before it is judged.</summary>
	private const double PaceWindowSeconds = 5.0;

	private double _paceSeconds;

	private int _paceSteps;

	private int _paceFrames;

	/// <summary>
	/// Halts the simulation without stopping it: the UI still draws and the bridge still reads.
	/// </summary>
	public bool Paused { get; set; }

	private Refii.Core.Engine_GuestPacer _guestPacer;

	/// <summary>Seconds since the display's refresh rate was last read; the first frame reads it.</summary>
	private double _refreshRateAge = double.MaxValue;

	private const double RefreshRateSeconds = 1.0;

	/// <summary>Set once the ORM sweep's worker has been started.</summary>
	private bool _ormStarted;

	/// <summary>Written by the sweep's worker thread when it finishes; read from _Process.</summary>
	private volatile bool _ormDone;

	/// <summary>The sweep's latest status line. Reference assignment, so no lock.</summary>
	private volatile string _ormStatus = "Preparing materials...";

	public override void _Process(double delta)
	{
		using (Refii.Core.Engine_FramePhases.Measure("session_process"))
		{
			session_processBody(delta);
		}
	}

	private double _classicStatusTimer;

	private void session_processBody(double delta)
	{
		GameCrashContext.Tick(this, delta);

		// The mods' settings, abilities and errors, and their godot/*.gd scripts under this node.
		Refii.Mods.ModFrame.Pump(this, Refii.Host.HostBridge.IsGuestUp, _driving);

		// A system message box the guest waits on (XamShowMessageBoxUI), asked of the player.
		Refii.Ui.Engine_UiSystemBoxes.Pump(Refii.Host.HostBridge.IsGuestUp);

		// The views drawn this frame (split screen's two cameras), for the feeds that follow a camera.
		Refii.Models.Engine_ClassicViews.Begin(GetViewport()?.GetCamera3D());

		// The modern renderer's decals: the guest's stated ring as Godot decals.
		if (!Refii.Models.Engine_ClassicMaterial.Active && Refii.Host.HostBridge.IsGuestUp)
		{
			using (Refii.Core.Engine_FramePhases.Measure("session.modern"))
			{
				bool guestFrame = Refii.Core.Engine_GuestFrame.Changed;
				Refii.Fx.Engine_ModernDecals.Ensure(this).Pump();
				Refii.Level.Engine_ModernMist.Ensure(this).Pump();
				Refii.Lighting.Engine_LightZones.Pump();
				if (guestFrame)
				{
					// The guest's live theme block: its texture slots give the modern dome its overlays.
					Refii.Models.Engine_ClassicTheme.Pump();
					Refii.Level.Engine_SkyOverlay.Pump();
				}
				if (GetViewport()?.GetCamera3D() is { } modernEye)
				{
					// The roofs around every view's eye: a drop under any of them is dry in either view.
					Refii.Level.Engine_RoofCut.Pump(modernEye.GlobalPosition,
						Refii.Models.Engine_ClassicViews.Count > 1 ? (Vector3?)Refii.Models.Engine_ClassicViews.Eye(1) : null);
					if (guestFrame)
					{
						Refii.Level.Engine_SnowPushes.Pump();
					}
					// The guest's FX mesh twins, drawn on the modern materials; FableFx's simulated copies stand down.
					if (Refii.Fx.Engine_FxMeshTwins.Instance is null)
					{
						AddChild(new Refii.Fx.Engine_FxMeshTwins());
					}
					Refii.Fx.Engine_FxMeshTwins.Instance?.Pump(modernEye);
					Refii.Fx.FableFx.GuestMeshTwins = Refii.Fx.Engine_FxMeshTwins.FeedLive;
					// SEPARATION OF CONCERNS BETWEEN MODERN AND CLASSIC PARTICLE SYSTEMS:
					// - Modern mode (!Engine_ClassicMaterial.Active) uses FableFx + Engine_FxSystem exclusively
					//   across the entire 0..200m range (simulated at 30 Hz with GPU sub-step interpolation and
					//   unclamped linear HDR colours).
					// - Classic mode (Engine_ClassicMaterial.Active) uses Engine_ClassicParticles exclusively,
					//   pumped below via Engine_ClassicPath.Pump.
					// Keeping the two runtime systems separate by renderer mode avoids split-brain 50m handoffs
					// while sharing pure static math/table helpers on Engine_FxSystem.
					Refii.Models.Engine_ModernFur.Pump(modernEye);
					if (guestFrame)
					{
						Refii.Fx.Engine_ModernDrunk.Pump(this);
					}
					else if (Refii.Fx.Engine_ModernDrunk.Drawn > 0)
					{
						Refii.AndroidRuntime.AndroidDisplacementGate.WakeDrunk();
					}
					Refii.Fx.Engine_DisplacementMeshes.Pump();
					Refii.AndroidRuntime.AndroidDisplacementGate.AfterPump();
				}
			}
		}

		// The classic renderer's frame constants: registered once, pushed when they changed.
		if (Refii.Models.Engine_ClassicMaterial.Active)
		{
			Camera3D? camera = GetViewport()?.GetCamera3D();
			Refii.Models.Engine_ClassicPath.Pump(this, camera, GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920f, 1080f));

			// The path's liveness, every ten seconds: which feeds ran and what they drew.
			_classicStatusTimer += delta;
			if (_classicStatusTimer >= 10.0)
			{
				_classicStatusTimer = 0.0;
				GD.Print(Refii.Models.Engine_ClassicPath.Status());
			}
		}

		if (!_booted)
		{
			if (_bootDirectory.Length == 0 || !Refii.Models.Engine_ShaderWarmup.Done)
			{
				return;
			}

			// THE ORM SWEEP RUNS BEHIND THE WARM-UP SCREEN, NOT DURING PLAY. Built on demand,
			// an ORM is a Poisson solve on a streaming worker, and a cache-version bump makes a
			// region's first load pay hundreds of them in one burst — which looks like the game
			// freezing. Behind this screen the same cost is a progress line; on a warm cache the
			// sweep is enumerate-and-hit and adds seconds.
			if (!_ormStarted)
			{
				_ormStarted = true;
				string directory = _bootDirectory;
				var worker = new System.Threading.Thread(() =>
				{
					// One mount for the sweeps, opened by the first that has work.
					Refii.Bnk.BnkFileSystem? sharedBanks = null;
					var mount = new Lazy<Refii.Bnk.BnkFileSystem>(() =>
					{
						sharedBanks = Engine_GameAssets.Instance?.WaitForArchives();
						return sharedBanks ?? Refii.Bnk.BnkFileSystem.Mount(directory);
					});
					try
					{
						Refii.Textures.Engine_TexPrebuild.Progress result =
							Refii.Textures.Engine_TexPrebuild.RunOrm(directory, progress =>
								_ormStatus = progress.Total == 0
									? "Preparing materials..."
									: $"Preparing materials... {progress.Done:N0} of " +
									  $"{progress.Total:N0}", mount: mount);
						GD.Print($"[game] orm sweep: {result.Built} built, " +
							$"{result.Failed} failed of {result.Total}");

						// Static shadow maps (.shadows) and cropped ground contact AO (.ao) now bake
						// on-demand on the GPU via Engine_LevelShadowPrebuild.EnsureScenarioBuilt when
						// entering a level, eliminating the multi-minute upfront 78-scenario sweep.

						// THE UPSCALE SWEEPS, when `texture_upscaling_when` says boot: every
						// material texture, then every terrain layer, into the shared cache
						// behind this same screen — so play never pays an enhancement and the
						// terrain's synchronous build is enumerate-and-hit. "play" defers both
						// to their live paths; "off" makes both no-ops.
						if (Refii.Textures.Engine_TexSettings.UpscaleWhen == "boot")
						{
							Refii.Textures.Engine_TexPrebuild.Progress sharp =
								Refii.Textures.Engine_TexPrebuild.RunTextures(directory, progress =>
									_ormStatus = progress.Total == 0
										? "Enhancing textures..."
										: $"Enhancing textures... {progress.Done:N0} of " +
										  $"{progress.Total:N0}", mount: mount);
							GD.Print($"[game] texture sweep: {sharp.Built} prepared, " +
								$"{sharp.Failed} failed of {sharp.Total}");

							Refii.Level.Engine_TerrainPrebuild.Progress ground =
								Refii.Level.Engine_TerrainPrebuild.Run(directory, progress =>
									_ormStatus = progress.Total == 0
										? "Preparing terrain..."
										: $"Preparing terrain... {progress.Done:N0} of " +
										  $"{progress.Total:N0}", mount: mount);
							GD.Print($"[game] terrain sweep: {ground.Built} enhanced, " +
								$"{ground.Failed} unreadable of {ground.Total}");
						}
					}
					catch (Exception e)
					{
						// The sweep is an optimisation; a failure means the live path pays
						// per-texture instead, never that the boot stops.
						GD.Print($"[game] orm sweep failed: {e.Message}");
					}
					finally
					{
						if (mount.IsValueCreated && !ReferenceEquals(mount.Value, sharedBanks))
						{
							mount.Value.Dispose();
						}

						System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
							System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
						GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
						_ormDone = true;
					}
				})
				{
					IsBackground = true,
					Name = "orm-prebuild",
				};
				worker.Start();
				return;
			}

			if (!_ormDone)
			{
				Refii.Models.Engine_ShaderWarmup.SetStatus(_ormStatus);
				return;
			}

			_booted = true;
			Refii.Models.Engine_ShaderWarmup.SetStatus("Preparing Albion...");
			Boot();
			// Only now: the guest is initialised (or Fail said why), so the videos that follow
			// have something real behind them instead of a grey gap.
			Refii.Models.Engine_ShaderWarmup.ReleaseScreen();
			System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
				System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
			GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
			return;
		}

		if (!_driving)
			return;

		long lap = System.Diagnostics.Stopwatch.GetTimestamp();

		// Hold guest simulation steps while the world build cover or classic light-shape settle freezes the guest,
		// while still draining enhanced texture completions so workers never pile up behind a load hold.
		bool freezeClock = Paused || GameWorld.FreezeGuestForLoad || Refii.Models.Engine_ClassicLoad.FreezeGuest;
		HostBridge.SetTimeBaseFrozen(freezeClock);
		if (freezeClock)
		{
			_guestPacer.ClearAccumulated();
			if (GameWorld.WorldBuildHeld)
			{
				HostBridge.SetWorldBuildPending(true);
			}
			Engine_TexLibrary.Pump();
			Refii.Core.Engine_FramePhases.Lap("session.tex", ref lap);
			return;
		}

		// Godot's pacing is the guest's clock: the guest's loop takes at most one step per
		// frame and never banks multi-step catch-up bursts (matching 0x82276FA4 in sub_82276D10).
		//
		// Closed loop: backpressure reads the native driven time-base's authoritative unconsumed
		// HF step depth (`pending + in_flight`) right here at grant time rather than subtracting
		// drifting cumulative counters against a start-of-frame `Engine_GuestFrame` latch.
		//
		// `guestTick` (`game+0xFC`, incremented once per game tick at 0x821C9044 with no gate) is
		// used for counter-pair drift monitoring and stall detection (`GuestStalled`).
		uint guestTick = HostBridge.SimClock() is { } clock ? clock.Tick : 0u;

		// `dword_83319650` is gated (`821d46e8` branches past its own increment) and has a
		// second increment path (`823d9904`); `guestTick` is the ungated once-per-game-tick
		// counter. Both are read in the same instant here, so their drift is observed exactly.
		if (guestTick != 0u)
		{
			Refii.Core.Engine_TickClock.ObserveCounterPair(HostBridge.SimTick, guestTick);

			// The draw point's terms are the display's: `sub_82388380` sizes its draw time from
			// the refresh rate and present interval (`XGetVideoMode`), never a measured frame
			// time. Re-read once a second, because the window can move to another screen — not
			// per frame: on X11 the query is XRRGetScreenResourcesCurrent plus a CRTC query per
			// output, round trips to the X server on the main thread.
			_refreshRateAge += delta;
			if (_refreshRateAge >= RefreshRateSeconds)
			{
				_refreshRateAge = 0.0;
				float hz = Godot.DisplayServer.ScreenGetRefreshRate(
					Godot.DisplayServer.WindowGetCurrentScreen((int)Godot.DisplayServer.MainWindowId));
				if (hz > 1.0f)
				{
					Refii.Core.Engine_TickClock.RefreshHz = hz;
				}
			}
		}

		ulong unconsumedSteps = guestTick != 0u && HostBridge.TryGetUnconsumedTimeBaseSteps(out ulong liveUnconsumed)
			? liveUnconsumed
			: 0UL;
		int steps = _guestPacer.Advance(delta, unconsumedSteps, out double lead, out bool withholdGrant);
		if (guestTick != 0u)
		{
			Refii.Core.Engine_TickClock.HostGrantLead = lead;
			Refii.Core.Engine_TickClock.HostGrantWithheld = withholdGrant;
		}

		Refii.Core.Engine_FramePhases.Lap("session.clock", ref lap);
		if (steps > 0)
		{
			HostBridge.AdvanceTimeBase((uint)steps);
			_granted += steps;
		}
		else
		{
			HostBridge.EndTimeBaseReadWindow();
		}

		// The same accumulator is the draw clock: `_guestPacer.Phase` is how far this frame
		// lies between two guest steps, so the playback rate is 1.0 by construction.
		Refii.Core.Engine_TickClock.PublishHostTicks(_granted, _guestPacer.Phase);

		// One Tracy frame boundary and perf-counter flip per host frame. The renderer that
		// used to own Profiler::Flip() is compiled out, so without this the timeline has no
		// frames and the per-frame counter snapshots never rotate.
		Refii.Host.HostBridge.FrameMark();

		// TWO INSTRUMENTS ON THE SAME LOOP, and they answer different questions. The Tracy zone
		// is for a profiler attached to the native side, where the guest's own zones say what a
		// step is doing; the frame phase is ours and rides the debug channel, so `refii_perf`
		// answers "what does a step COST" with nothing attached. The pace line above says
		// whether we are keeping up; this says why not.
		using (Refii.Core.Engine_FramePhases.Measure("guest-step"))
		using (new Refii.Host.HostBridge.TraceZone("guest-steps"))
		{
			for (int i = 0; i < steps; ++i)
			{
				if (!StepGuest())
				{
					return;
				}
			}

			// THE HOST'S CHORES RUN EVERY FRAME, granted time or not. `RefiiHostStep` does not
			// advance the guest (`AdvanceTimeBase` above grants time); it services it — frame
			// completions, stream retirement, the world-build hold, and the in-game loading
			// screen's take-down event (`FinishStuckInGameLoadingScreenFromHost`). A guest whose
			// game thread is blocked on one of those never advances its tick, so the lead cap
			// above withheld every step from it for good: a co-op joiner sat on "Loading
			// Bowerstone Market" with its game thread in `sub_82361458`'s infinite wait and the
			// pace line at 0.0 steps/s (2026-10-19). Only for a STALLED guest — no time granted and
			// its tick still for half a second — so ordinary play keeps one service per step.
			if (steps == 0 && GuestStalled(guestTick, delta) && !StepGuest())
			{
				return;
			}
		}

		// SWAP IN ENHANCED TEXTURES BEFORE THE STEP GATE, NOT AFTER IT. Each queued completion
		// captures a whole mip chain — megabytes of native Image data sitting in the anonymous
		// heap until it is applied — and the queue is bounded only by the number of jobs. Behind
		// the gate it drained only while the guest was stepping, which is precisely NOT the case
		// during a loading screen or a level transition: the moment four workers are producing
		// fastest was the moment nothing consumed them. Budgeted still, so a warm cache cannot
		// trade a background cost for a visible hitch.
		Refii.Core.Engine_FramePhases.Lap("session.steps", ref lap);
		Engine_TexLibrary.Pump();
		Refii.Core.Engine_FramePhases.Lap("session.tex", ref lap);

		// THE PACE IT ACTUALLY GOT. The accumulator ASKS for StepsPerSecond; whether it gets it
		// is a fact about how long a step costs, and nothing above can assert it. A guest that
		// advances slower than real time moves the hero and his animation in slow motion while
		// the camera still runs at frame rate — which reads as the model detaching and then
		// snapping back, not as a frame-rate problem.
		//
		// Printed ONLY when it is behind, so a healthy run stays silent.
		_paceSeconds += delta;
		_paceSteps += steps;
		++_paceFrames;
		if (_paceSeconds >= PaceWindowSeconds)
		{
			double perSecond = _paceSteps / _paceSeconds;
			if (perSecond < StepsPerSecond * 0.9)
			{
				GD.Print($"[game-pace] the guest advanced {perSecond:0.0} step(s)/s against a " +
					$"{StepsPerSecond:0} Hz target while Godot drew " +
					$"{_paceFrames / _paceSeconds:0.0} frame(s)/s — the world is running at " +
					$"{perSecond / StepsPerSecond:P0} of real time");
			}

			_paceSeconds = 0.0;
			_paceSteps = 0;
			_paceFrames = 0;
		}

		if (steps == 0)
		{
			return;
		}

		// Periodic report; prints only when the counters have moved.
		if (_steps % 600 == 0)
		{
			Engine_TexLibrary.ReportIfChanged();
			Refii.Models.Engine_MdlMaterial.ReportIfChanged();
		}

		// After the step: what the title reads is written by this frame's guest tick.
		Engine_TitleGame.Active.Step();

		TraceCamera();
	}

	/// <summary>
	/// Whether the guest's tick has not moved for <see cref="StallSeconds"/> — a game thread
	/// blocked, not merely between ticks (it ticks at 15 Hz). Fed once per frame.
	/// </summary>
	private bool GuestStalled(uint guestTick, double delta)
	{
		if (guestTick != _stallTick)
		{
			_stallTick = guestTick;
			_stallSeconds = 0.0;
			return false;
		}

		_stallSeconds += delta;
		return _stallSeconds >= StallSeconds;
	}

	private const double StallSeconds = 0.5;
	private uint _stallTick;
	private double _stallSeconds;

	/// <summary>One slice of the guest's loop. False once it has stopped.</summary>
	private bool StepGuest()
	{
		if (!HostBridge.Step())
		{
			_driving = false;
			string reason = HostBridge.HostedStatus;
			GD.Print($"[game] guest stopped after {_steps:N0} steps: {reason}");

			// Latch after the status is read: it makes the bridge refuse, which is what stops
			// every polling node other than `GameWindow`.
			HostBridge.MarkUnusable($"the guest stopped: {reason}");
			EmitSignal(SignalName.Stopped, reason);
			return false;
		}

		_steps++;
		return true;
	}

	/// <summary>
	/// Logs the drawn camera when it moves: the block rebuilt from the guest's camera statements for
	/// the manager this machine draws (Refii.Camera.Engine_CameraFeed).
	/// </summary>
	private void TraceCamera()
	{
		if (Refii.Camera.Engine_CameraFeed.AsGuestCamera(Refii.Camera.Engine_CameraFeed.Drawn?.Live) is not { } camera)
		{
			return;
		}

		bool moved = _lastCamera is not { } previous ||
			previous.Position.DistanceTo(camera.Position) > 0.01f ||
			previous.Forward.DistanceTo(camera.Forward) > 0.001f;
		if (!moved)
		{
			return;
		}

		_lastCamera = camera;
		if (++_cameraReports <= 20)
		{
			GD.Print($"[guest-camera] pos={camera.Position} fwd={camera.Forward} " +
				$"up={camera.Up} vfov={camera.VerticalFovDegrees:F1}deg " +
				$"{(camera.IsDefault ? "DEFAULT (no live camera)" : "live")}");
		}
	}

	private HostBridge.GuestCamera? _lastCamera;
	private int _cameraReports;

	public override void _ExitTree()
	{
		if (ReferenceEquals(Current, this))
		{
			Current = null;
		}

		Engine_TexLibrary.Report();
		Refii.Models.Engine_MdlMaterial.ReportIfChanged();

		// The mod scripts and their singleton go before the guest does.
		Refii.Mods.ModFrame.Stop();

		// Guest threads left live at process exit abort the process. Raw version, not
		// `TryGetAbiVersion`: that one reports the refusal latch, set exactly when the guest
		// stopped. Only a missing or wrong-version library excuses skipping teardown.
		if (HostBridge.TryGetRawAbiVersion() == HostBridge.ExpectedAbiVersion)
		{
			GD.Print($"[game] shutdown: {HostBridge.Shutdown().Message}");
		}
	}

	/// <summary>The export wins; otherwise the one shared convention.</summary>
	private static string Directory(string configured) =>
		System.IO.Directory.Exists(configured) ? configured : Engine_GameData.Resolve();

	/// <summary>
	/// Abandons the boot. `Stopped` only reaches `GameWindow`; latching the bridge is what stops
	/// every other node polling the guest.
	/// </summary>
	private void Fail(string reason)
	{
		GD.PrintErr($"[game] {reason}");
		HostBridge.MarkUnusable(reason);
		EmitSignal(SignalName.Stopped, reason);
	}
}
