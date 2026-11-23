using Godot;
using Refii.Assets;
using Refii.Audio;
using Refii.Core;
using Refii.Game;
using Refii.Host;
using Refii.Models;
using Refii.Textures;
using Refii.Ui;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Refii.AndroidRuntime;

/// <summary>Android entry point for the production UI, world, materials and audio, played by controller or touch.</summary>
/// <remarks>Native execution has one persistent owner; Godot presentation stays on its main
/// thread. GameSession is intentionally absent because it would initialize and step a second guest.
/// The APK contains no game data or generated game module. Supply physical paths through the
/// private configuration documented by AndroidRuntimeOptions before starting the app.</remarks>
public partial class AndroidGameHost : Node3D
{
    private AndroidRuntimeOptions? _options;
    private AndroidNativeSession? _guest;
    private AndroidGuestPlacement? _placement;
    private AndroidLoadPlacement? _loadPlacement;
    private Engine_GameAssets? _assets;
    private GameWorld? _world;
    private GameAudio? _audio;
    private GameInput? _input;
    private Node? _uiNode;
    private bool _frontEndAttached;
    private IGameTitleUi? _ui;
    private Control? _touchNode;
    private IAndroidTouchControls? _touch;
    private CanvasLayer? _uiLayer;
    private bool? _canvasLeanSetting;
    private Label? _status;
    private PanelContainer? _statusPanel;
    private AndroidStartup? _startup;

    /// <summary>The guest's front end as last followed, for the status file; null until the UI follows it.</summary>
    private FrontEndSample? _frontEnd;
    private bool _assetsReady, _focused = true;

    /// <summary>
    /// Seconds until the return's pause is pressed, once the window has its focus back: resume
    /// arrives before focus, and the pad is muted until focus, so a press then would be lost.
    /// Negative while no return waits.
    /// </summary>
    private double _pauseOnReturn = -1;

    /// <summary>How long the world runs again before the return's pause is pressed.</summary>
    private const double PauseOnReturnDelay = 0.3;
    private string _phase = "waiting for private Android configuration";
    private string _failure = string.Empty;
    private string _userDirectory = string.Empty;
    private string _runtimeConfig = string.Empty;
    private string _renderScaleError = string.Empty;
    private string _renderThreadError = string.Empty;
    private readonly AndroidRenderScale _renderScale = new();
    private readonly Engine_MeshLodPolicy _meshLod = new();
    private readonly AndroidSunShadows _sunShadows = new();
    private AndroidProfilingOptions? _profiling;
    private double _reportAge, _statusAge;
    private long _presentationFrames;

    /// <summary>The diagnostics node of the scene, found once.</summary>
    private AndroidDiagnosticButtons? _diagnostics;

    /// <summary>Writes android-status.json off the main thread; null until the user directory is known.</summary>
    private AndroidStatusWriter? _statusWriter;

    /// <summary>The phase and failure the status file last stated: a change is written within a second.</summary>
    private string _statusPhase = string.Empty, _statusFailure = string.Empty;

    /// <summary>refii.toml as last read for the live settings, and its stamp then: read again only when it moves.</summary>
    private System.Collections.Generic.SortedDictionary<string, string>? _liveValues;
    private GameWindowSettings? _liveSettings;
    private AndroidConfigStamp _liveStamp;

    /// <summary>What does not change while the process runs: Godot's version and the renderer.</summary>
    private string? _godotVersion, _renderingMethod;

    /// <summary>One options object: the serializer keeps its per-type metadata on it, so a new one each write built it all again.</summary>
    private static readonly JsonSerializerOptions StatusJson = new()
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    /// <summary>The guest's front end as <see cref="FollowFrontEnd"/> read it: raw fields, the status object built only when written.</summary>
    private readonly record struct FrontEndSample(bool Found, Engine_UiModel.FrontEndScreen Screen, uint State, bool CanPressA,
        uint Cursor, bool GuiUp, bool Reveal);

    [DllImport("libc", CallingConvention = CallingConvention.Cdecl)]
    private static extern int setenv(string name, string value, int overwrite);

    /// <summary>Sets writable paths, uncapped presentation and bounded renderer quality before mounting the supplied game.</summary>
    public override void _Ready()
    {
        AndroidNativeLog.StartPump();
        AndroidLastExit.RecordOnce();
        GD.Print($"[thread-pool] idle spin {AndroidPoolSpin.Outcome}");
        KeepInMemoryWhileAway();
        ProcessPriority = -100;
        Refii.Level.Engine_TreeSwayFar.Apply();
        AndroidPoseRate.Apply();
        AndroidOffscreenRigs.Apply();
        AndroidSkipIndexHolds.Apply();
        // The back gesture is the game's (AndroidSystemKeys), never Godot's quit.
        GetTree().QuitOnGoBack = false;
        _userDirectory = OS.GetUserDataDir();
        _statusWriter = new AndroidStatusWriter(Path.Combine(_userDirectory, "android-status.json"));
        BuildStatusOverlay();
        _touchNode = GetNodeOrNull<Control>("AndroidTouchLayer/AndroidTouchControls");
        _touch = _touchNode as IAndroidTouchControls;
        _diagnostics = GetNodeOrNull<AndroidDiagnosticButtons>("AndroidDiagnosticButtons");
        if (_touch != null && _diagnostics is { } diagnostics)
        {
            _touch.PassThrough = diagnostics.AddRects;
            _touch.OptionsPressed = diagnostics.Toggle;
            diagnostics.Touch = _touch;
            GameInput.HostUiRects = diagnostics.AddRects;
            diagnostics.ConfigPath = () => _runtimeConfig;
            // Just written here: read again whatever the file's stamp says.
            diagnostics.SettingsWritten = () => RefreshRenderScale(force: true);
        }
        if (_touch != null)
            AddChild(new AndroidCanvasLean { Name = "CanvasLean", Touch = _touchNode, Target = UiLayer });
        try
        {
            string configPath = Path.Combine(AndroidRuntimeOptions.TitleDirectory(_userDirectory), AndroidRuntimeOptions.FileName);
            if (!File.Exists(configPath))
                throw new FileNotFoundException("Supply private android-runtime.json before booting; see docs/android-runtime.md", configPath);
            _options = AndroidRuntimeOptions.Parse(File.ReadAllText(configPath), _userDirectory);
            string root = Path.Combine(_userDirectory, "runtime");
            SetEnvironment(Engine_AppRoot.EnvironmentVariable, root);
            Engine_AppRoot.Invalidate();
            Engine_AppRoot.EnsureLayout();
            SetEnvironment(Engine_GameData.EnvironmentVariable, _options.GameRoot);
            // These are the bounded, original-material settings used by the verified S25+
            // prototype. The production scene-referred HDR/post-processing code stays intact.
            SetEnvironment("REFII_MATERIALS_MODERNIZED", "0");
            SetEnvironment("REFII_TEX_UPSCALE", "0");
            SetEnvironment("REFII_TEX_ORM", "0");
            SetEnvironment("REFII_TEX_WORKERS", "1");
            SetEnvironment("REFII_TEX_CACHE_MB", "512");
            Refii.Textures.Engine_TexCache.Maintain();
            // `android_modern_lighting = true` keeps the config's own lighting_model (the lite
            // modern builds exist) while the two models are compared on phones.
            if (!Refii.Core.Engine_RefiiConfig.Bool(Refii.Core.Engine_RefiiConfig.Read(out _), "android_modern_lighting", false))
                SetEnvironment("REFII_LIGHTING_MODEL", "fable");
            // Entity batches (modern only) save a few draws but are culled and LOD-picked as one
            // 64 m box: on an S10e they drew 190K more triangles and cost 6-7 ms of GPU. So lean
            // modern leaves them off unless android_entity_batches asks (AndroidEntityProps).
            AndroidEntityProps.ApplyBatching();
            Refii.Lighting.Engine_GiRefresh.ForcedOff = true;
            Engine_TickClock.HostOwnsClock = false;
            Engine_TickClock.UseStatedClock = true;
            GetViewport().Msaa3D = Viewport.Msaa.Disabled;
            GetViewport().UseTaa = false;
            Engine_RenderStats.Track(GetViewport());
            AndroidTextureUpload.InitializeOnMainThread();
            string runtimeConfig = Path.Combine(root, Engine_Title.Active.ConfigFileName);
            if (!File.Exists(runtimeConfig))
                File.WriteAllText(runtimeConfig, "sun_shadow_map = 1024\nsun_shadow_splits = 2\nshader_warmup_enabled = false\n");
            var seeded = Refii.Launcher.Core.RuntimeConfig.ReadValues(runtimeConfig);
            var unseeded = new System.Collections.Generic.Dictionary<string, string>(seeded, StringComparer.Ordinal);
            if (AndroidLauncherSettings.ApplyPresetOnce(seeded) | Refii.Launcher.Core.GraphicsPresets.Upgrade(seeded) &&
                Refii.Launcher.Core.RuntimeConfig.WriteValues(runtimeConfig, seeded, out _))
                AndroidLauncherSettings.ShareWithOtherTitles(runtimeConfig, unseeded, seeded);
            _runtimeConfig = runtimeConfig;
            // The native threads' waits, read as they first run, so before the guest boots.
            foreach ((string name, string value) in AndroidIdleWaits.Environment(seeded))
                SetEnvironment(name, value);
            AndroidClothLand.Apply(seeded);
            _sunShadows.Apply(seeded, GetTree().Root);
            AddChild(new AndroidIdlePower { Name = "IdlePower", PauseNow = () => _pauseOnReturn = 0,
                ScreenMinutes = Engine_RefiiConfig.Int(seeded, AndroidIdlePower.ScreenMinutesKey, AndroidIdlePower.DefaultScreenMinutes) });
            // The thread model is fixed at start: this states it for the next one.
            _renderThreadError = AndroidRenderThread.Apply(Refii.Launcher.Core.RuntimeConfig.ReadValues(runtimeConfig));
            Engine_PresentationPolicy.ApplyAndroid(
                Engine_RefiiConfig.Int(Refii.Launcher.Core.RuntimeConfig.ReadValues(runtimeConfig), "max_fps", 0),
                value => Engine.MaxFps = value,
                value => DisplayServer.WindowSetVsyncMode(value, (int)DisplayServer.MainWindowId));
            SetEnvironment(Refii.Lighting.Engine_PostFxParams.ModeEnvironment,
                Engine_PresentationPolicy.PostFxMode(Refii.Launcher.Core.RuntimeConfig.ReadValues(runtimeConfig)));
            ConfigureProfiling();
            // Avoid eager compilation of unused shader families: older Adreno drivers can
            // hang there. This does not replace any authored shader or skip world rendering.
            _assets = Engine_GameAssets.Instance ?? throw new InvalidOperationException("GameAssets autoload is missing");
            _assets.Mounted += OnMounted;
            _assets.MountFailed += OnMountFailed;
            _phase = "mounting privately supplied game assets";
            _assets.Mount(_options.GameRoot);
            Engine_ShaderWarmup.Begin(this);
            Engine_ShaderWarmup.BeginStartupScreen(this, "Loading Albion...");
            AddChild(_startup = new AndroidStartup { Name = "AndroidStartup", Finished = () => GameHasScreen,
                InWorld = () => _world != null, FrontEndPending = () => _guest != null && !_frontEndAttached });
        }
        catch (Exception error) { Fail(error); }
        RefreshStatusSettings();
        SaveStatus();
    }

    /// <summary><see cref="RefreshRenderScale"/> where the status write ran it: an unexpected error is the host's failure.</summary>
    private void RefreshStatusSettings()
    {
        try { RefreshRenderScale(); }
        catch (Exception error) { Fail(error); }
    }

    /// <summary>Pumps presentation and real controller UI while the owner thread runs the native guest.</summary>
    public override void _Process(double delta)
    {
        AndroidDebugIdleQuit.Tick();
        PauseOnReturn(delta);
        // Every frame, not only while a streamer is still loading: it stops processing once settled.
        Refii.Level.Engine_PropFarShading.Tick(this);
        Refii.AndroidRuntime.AndroidTreeCards.Tick(this);
        Refii.AndroidRuntime.AndroidCharacterLevers.Tick(this);
        Refii.AndroidRuntime.AndroidEntityProps.Tick();
        try
        {
            if (_failure.Length == 0 && _guest == null && _assetsReady && Engine_ShaderWarmup.Done &&
                _assets != null && _options != null)
            {
                // GameAudio is the sole mixed-audio consumer, including during initialization.
                _audio = new GameAudio { Name = "GameAudio" };
                AddChild(_audio);
                _guest = new AndroidNativeSession(_options, Engine_AppRoot.Path, _assets,
                    _runtimeConfig.Length != 0 &&
                    AndroidNativeSession.RealClockConfigured(Refii.Launcher.Core.RuntimeConfig.ReadValues(_runtimeConfig)));
                _guest.Start();
                // The guest's game thread on the fastest cores once it is named (android_guest_prime_core).
                if (_runtimeConfig.Length != 0)
                {
                    var values = Refii.Launcher.Core.RuntimeConfig.ReadValues(_runtimeConfig);
                    _placement = AndroidGuestPlacement.Start(values);
                    _loadPlacement = AndroidLoadPlacement.Start(values, static () => GameWorld.WorldBuildHeld);
                    AndroidGcStress.Start(values);
                }
                _phase = "booting native guest";
                Engine_ShaderWarmup.SetStatus("Preparing Albion...");
                _startup?.PlayVideos(_guest);
            }
            if (_guest?.Error.Length > 0 && _failure.Length == 0)
                Fail(new InvalidOperationException(_guest.Error));
            if (_failure.Length == 0 && _guest?.IsInitialized == true)
            {
                // Match desktop's early worker kick: UI/world work can overlap the read,
                // then GameEntities takes it in this frame rather than scanning synchronously.
                // Engine_FrameStartNode already sampled this frame's guest frame; sampling it
                // again would compare the frame with itself and clear Changed, starving the
                // entity walk. Like GameWindow, re-read only while no frame has been seen.
                AndroidEntityFeed.SampleAndKick(GameEntities.FeedEveryFrame,
                    static () => { if (Engine_GuestFrame.Frame == 0u) HostBridge.SampleGuestFrame(); },
                    static () => Engine_GuestFrame.Changed, HostEntityWalk.Kick);
                if (HostBridge.SimClock() is { } clock)
                {
                    Engine_TickClock.PublishGuestGameTick(clock.Tick);
                    Engine_TickClock.ObserveCounterPair(HostBridge.SimTick, clock.Tick);
                }
                Engine_TexLibrary.Pump();
                // GameSession's periodic report, which prints only when the counters have moved.
                if (Engine.GetProcessFrames() % 600 == 0) Engine_TexLibrary.ReportIfChanged();
                // Not while a startup video plays: attaching is one long frame (AndroidStartup.HoldsFrontEnd).
                if (_startup is not { HoldsFrontEnd: true }) AttachFrontEnd();
                FollowFrontEnd();
                // The guest steps on its own thread: its pub-game manager is read once a frame.
                Engine_TitleGame.Active.Step();
                if (_world == null && HostBridge.IsInGame)
                {
                    _world = new GameWorld { Name = "GameWorld" };
                    AddChild(_world);
                    _phase = "building the native guest world";
                }
                if (_world?.Entities is { ReadyForRelease: true } && !GameWorld.WorldBuildHeld)
                    _phase = "game world ready";
            }
            // The mods' settings, abilities, errors and godot/*.gd scripts, as GameSession pumps them.
            if (_guest != null)
                Refii.Mods.ModFrame.Pump(this, HostBridge.IsGuestUp, _failure.Length == 0 && _guest.IsInitialized);
        }
        catch (Exception error) { Fail(error); }
        _presentationFrames++;
        // A write that failed on the writer's thread is this thread's failure.
        if (_statusWriter?.TakeError() is { } writeError) Fail(writeError);
        _statusAge += delta;
        _reportAge += delta;
        bool refresh = _reportAge >= AndroidStatusCadence.RefreshSeconds;
        if (refresh)
        {
            _reportAge = 0;
            // The live frame cap and render scale: a stat each second, refii.toml read only when it moved.
            RefreshStatusSettings();
        }
        if (AndroidStatusCadence.StatusDue(_statusAge, !string.Equals(_phase, _statusPhase, StringComparison.Ordinal),
                !string.Equals(_failure, _statusFailure, StringComparison.Ordinal)))
            SaveStatus();
        if (!refresh) return;
        // ONLY A FAILURE IS SHOWN. The phase ("building the native guest world") stood in the corner
        // over the loading screen and the startup screen on every start and load; it stays in the
        // status file for tools, and the startup and loading screens say what is happening.
        if (_failure.Length != 0) ShowFailure();
    }

    /// <summary>The mod scripts and their singleton go before the guest does.</summary>
    public override void _ExitTree() => Refii.Mods.ModFrame.Stop();

    /// <summary>
    /// Starts RefiiPlayService while the game is in front (Android refuses the start from the background):
    /// its foreground-service rank keeps the low-memory killer off the game while another app is in front.
    /// Without it an 8 GB phone killed a 4.5 GB game 3 s after Home (adj 700).
    /// </summary>
    private static void KeepInMemoryWhileAway()
    {
        if (!Engine.HasSingleton("RefiiInstall")) return;
        string error = Engine.GetSingleton("RefiiInstall").Call("play", Engine_Title.Active.Name).AsString();
        if (error.Length > 0) GD.PushWarning("[android] the game is not kept in memory in the background: " + error);
    }

    /// <summary>
    /// Releases held input and holds the guest while the app is away, coming back to the pause
    /// menu; turns the back gesture into the pad's (AndroidSystemKeys). Teardown remains unsupported.
    /// </summary>
    public override void _Notification(int what)
    {
        if (what == NotificationApplicationFocusOut || what == NotificationApplicationPaused)
        {
            _focused = false;
            if (_input != null) _input.DevicesEnabled = false;
            _guest?.ReleaseController();
            if (_guest != null) _guest.Away = true;
            _pauseOnReturn = -1;
        }
        else if (what == NotificationApplicationFocusIn || what == NotificationApplicationResumed)
        {
            _focused = true;
            if (_input != null) _input.DevicesEnabled = true;
            // Focus and resume both arrive on a return: only the first, which finds the guest held, pauses.
            bool wasAway = _guest?.Away == true;
            if (_guest != null) _guest.Away = false;
            if (_ui != null && AndroidSystemKeys.PauseOnReturn(wasAway, HostPanelUp, GameInput.Playing, GameInput.InOverlay,
                    BoardUp, Engine_TitleGame.Active.InteractionDialogUp))
                _pauseOnReturn = PauseOnReturnDelay;
        }
        else if (what == NotificationWMGoBackRequest)
        {
            // Options and the menus it opens close first: a way out whatever a touch did.
            if (GetNodeOrNull<AndroidDiagnosticButtons>("AndroidDiagnosticButtons")?.CloseTop() == true) return;
            switch (AndroidSystemKeys.Back(_ui != null, HostPanelUp, GameInput.Playing, GameInput.InOverlay,
                BoardUp, Engine_TitleGame.Active.InteractionDialogUp))
            {
                case AndroidBack.Pause: GameInput.PressOnce(GameInput.PadStart); break;
                case AndroidBack.BackOut: GameInput.PressOnce(GameInput.PadB); break;
                case AndroidBack.ClosePanel:
                    if (GamePhotoMode.Active) GamePhotoMode.RequestBack();
                    else Engine_TitleGame.Active.RequestBack();
                    break;
            }
        }
    }

    /// <summary>Presses Start for a return once the window has its focus back and the world has run a moment.</summary>
    private void PauseOnReturn(double delta)
    {
        if (_pauseOnReturn < 0 || !_focused || !GetWindow().HasFocus()) return;
        _pauseOnReturn -= delta;
        if (_pauseOnReturn >= 0) return;
        if (AndroidSystemKeys.PauseOnReturn(true, HostPanelUp, GameInput.Playing, GameInput.InOverlay,
                BoardUp, Engine_TitleGame.Active.InteractionDialogUp))
            GameInput.PressOnce(GameInput.PadStart);
    }

    /// <summary>Whether photo mode or the mirror owns the screen.</summary>
    private static bool HostPanelUp => GamePhotoMode.Active || Engine_TitleGame.Active.InteractionOwnsDevices;

    /// <summary>Whether a title board (Fable 2's pub-game table) is on screen: a press once reaches it through its own pad.</summary>
    private static bool BoardUp => Engine_TitleGame.Active.InteractionBoardUp;

    /// <summary>Registers the shared mount only after production texture loaders have been installed.</summary>
    private void OnMounted(int count)
    {
        _assetsReady = count > 0;
        if (!_assetsReady) OnMountFailed("No files were mounted from game_root");
    }

    /// <summary>Surfaces mount failures without attempting native initialization.</summary>
    private void OnMountFailed(string reason) => Fail(new InvalidDataException(reason));

    /// <summary>Installs production menus and the input sink after one successful native initialization.</summary>
    private void AttachFrontEnd()
    {
        // Once. Not "until _ui is set": only Fable 2's UI is an IGameTitleUi, so for Fable 3 _ui stays
        // null and every frame built another whole UI, input node and photo mode.
        if (_frontEndAttached || _guest == null) return;
        _frontEndAttached = true;
        _input = new GameInput { Name = "GameInput", ControllerStateSink = _guest.PublishController,
            DevicesEnabled = _focused };
        AddChild(_input);
        // Preserve the production order: the menu consumes the same physical sample whose
        // filtered guest packet was published by GameInput earlier in this frame.
        _uiNode = Engine_TitleGame.Active.CreateUi();
        _ui = _uiNode as IGameTitleUi;
        if (_uiNode != null) AddChild(_uiNode);
        // Photo mode, which the desktop's GameSession adds: here it holds this host's own guest thread.
        AndroidNativeSession guest = _guest;
        AddChild(new GamePhotoMode { Name = "PhotoMode", HoldGuest = held => guest.Held = held });
        // The title's own session nodes, which the desktop's GameSession adds: its live UI source,
        // its interactions (a dialog of theirs holds this host's guest too) and, for Fable 2, the
        // pub games — unclaimed, the guest would play its own Scaleform movie unseen behind the
        // loading screen.
        Engine_TitleGame.Active.AddSessionNodes(this, held => guest.Held = held);
        // In the same long frame, over the startup screen: the main menu too, not over the title later.
        (_uiNode as IAndroidMenuPrebuild)?.PrebuildMenuNow();
    }

    /// <summary>Mirrors real guest UI state; navigation and Load Game come only from the player's input.</summary>
    private void FollowFrontEnd()
    {
        if (_ui == null) return;
        bool found = HostBridge.TryGetFrontEnd(out var screen, out uint state, out bool canPressA,
            out uint cursor);
        _ui.FollowGuest(screen);
        bool guiUp = found;
        bool reveal = InteractiveFrontEndPolicy.ShouldReveal(found, screen, guiUp, canPressA, _ui.HasInGameUi);
        if (reveal) _ui.Reveal();
        else if (!InteractiveFrontEndPolicy.StaysRevealed(_ui.IsRevealed, guiUp, _ui.HasInGameUi))
            _ui.Conceal("native guest has no visible interactive UI");
        _frontEnd = new FrontEndSample(found, screen, state, canPressA, cursor, guiUp, reveal);
        if (_world == null) _phase = InteractiveFrontEndPolicy.Status(found, screen, guiUp, canPressA);
    }

    /// <summary>Whether the front end, the title's own title screen (Fable 3 has no <see cref="IGameTitleUi"/>),
    /// the world or an error has the screen, which hides the status line and ends the startup screen.</summary>
    private bool GameHasScreen =>
        _ui?.IsRevealed == true || Engine_TitleGame.Active.Input.TitleScreenUp
        || _world?.Entities is { ReadyForRelease: true } || _failure.Length != 0;

    /// <summary>Shows boot/configuration errors without introducing game actions.</summary>
    private void BuildStatusOverlay()
    {
        var layer = new CanvasLayer { Name = "AndroidBootStatus", Layer = 100 };
        AddChild(layer);
        _statusPanel = new PanelContainer { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _statusPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center, Control.LayoutPresetMode.KeepSize);
        _statusPanel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0f, 0f, 0f, 0.85f), ContentMarginLeft = 28, ContentMarginRight = 28,
            ContentMarginTop = 20, ContentMarginBottom = 20, CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
        });
        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(760, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore };
        _status.AddThemeFontSizeOverride("font_size", 22);
        _statusPanel.AddChild(_status);
        layer.AddChild(_statusPanel);
    }

    /// <summary>The failure, as a player reads it: what happened and how to report it, then the error's first line.</summary>
    private void ShowFailure()
    {
        if (_status is null || _statusPanel is null) return;
        _status.Text = AndroidFailureText.For(_failure, Engine_Title.Active.Name);
        _statusPanel.Visible = true;
        // Centred on its own size once the text has laid it out.
        _statusPanel.Size = Vector2.Zero;
        Vector2 size = _statusPanel.GetCombinedMinimumSize();
        _statusPanel.Position = (GetViewport().GetVisibleRect().Size - size) / 2;
    }

    /// <summary>Records a bounded error once, preserving ordinary engine diagnostics.</summary>
    private void Fail(Exception error)
    {
        if (_failure.Length != 0) return;
        _failure = error.ToString();
        _phase = "Android startup/presentation failed";
        GD.PrintErr("[android] " + error);
    }

    /// <summary>Atomically writes generic status; counters are evidence of activity, not performance guarantees.</summary>
    /// <remarks>The snapshot is taken here, on the main thread, from detached values; serializing and
    /// writing it is the writer's (<see cref="AndroidStatusWriter"/>), off this thread.</remarks>
    private void SaveStatus()
    {
        _statusAge = 0;
        _statusPhase = _phase;
        _statusFailure = _failure;
        if (_statusWriter is not { } writer) return;
        try
        {
            Viewport viewport = GetViewport();
            Vector2 logicalSize = viewport.GetVisibleRect().Size;
            Vector2 viewportTextureSize = viewport.GetTexture().GetSize();
            float projectScale = ProjectSettings.GetSetting("rendering/scaling_3d/scale", 1.0f).AsSingle();
            Vector2 stretchScale = viewport.GetStretchTransform().Scale;
            Vector2 expectedSize = AndroidRenderScale.ExpectedSize(logicalSize, stretchScale, viewport.Scaling3DScale);
            var sink = _audio?.GetNodeOrNull<Engine_AudioOutput>("AudioOutput");
            var render = Engine_RenderStats.Of(viewport);
            var bankCache = _assets?.NestedBankCache;
            var report = new
            {
                schema = 1, utc = DateTime.UtcNow.ToString("O"), phase = _phase, error = _failure,
                godot = _godotVersion ??= Engine.GetVersionInfo()["string"].AsString(),
                rendering_method = _renderingMethod ??= RenderingServer.GetCurrentRenderingMethod(),
                postfx_mode = Refii.Lighting.Engine_PostFxParams.Mode,
                presentation_pacing = new
                {
                    max_fps = Engine.MaxFps,
                    vsync = DisplayServer.WindowGetVsyncMode((int)DisplayServer.MainWindowId).ToString(),
                    target_fps = Engine_PresentationPolicy.TargetFps(true),
                    target_ms = Engine_PresentationPolicy.BudgetMs(true),
                    scope = "App-level presentation settings; Android compositor/display may still pace output; target is diagnostic, not a cap"
                },
                render_resolution = new
                {
                    root_window_width = GetTree().Root.Size.X, root_window_height = GetTree().Root.Size.Y,
                    logical_width = logicalSize.X, logical_height = logicalSize.Y,
                    viewport_texture_width = viewportTextureSize.X, viewport_texture_height = viewportTextureSize.Y,
                    viewport_texture_scope = "Actual GetTexture().GetSize() 2D accessor; Window includes canvas stretch; not RenderingServer or internal 3D buffer size",
                    mesh_lod_threshold = viewport.MeshLodThreshold,
                    configured_mesh_lod_pixels = _meshLod.ConfiguredPixels,
                    configured_mesh_lod_threshold = _meshLod.ParityThreshold,
                    project_3d_scale = projectScale,
                    stretch_x = stretchScale.X, stretch_y = stretchScale.Y,
                    applied_3d_scale = viewport.Scaling3DScale, scaling_mode = viewport.Scaling3DMode.ToString(),
                    expected_3d_width = expectedSize.X, expected_3d_height = expectedSize.Y,
                    estimate_scope = "Expected dimensions before renderer rounding/alignment; not a GPU readback",
                    renderer_buffers = Refii.Lighting.Engine_PostFxEffect.RenderBuffers,
                    renderer_buffers_scope = "Actual active compositor RenderSceneBuffersRD internal/target sizes; detached scalar observation with capture UTC, not a GPU pixel readback",
                    config_path = _runtimeConfig, config_error = _renderScaleError
                },
                focused = _focused, presentation_frames = _presentationFrames,
                assets_ready = _assetsReady, shared_asset_count = _assets?.FileCount ?? 0,
                nested_bank_cache = bankCache,
                nested_bank_cache_scope = "Process-wide BnkFileSystem nested cache; the budget bounds inflated containers only, memory-mapped containers are counted separately",
                guest = _guest?.Describe(), guest_placement = _placement?.Report(), load_placement = _loadPlacement?.Report(), game_ui = _ui?.DescribeInteractiveUi(),
                frontend = DescribeFrontEnd(), controller = _input?.DescribeControllerInput(),
                world_attached = _world != null, world_held = GameWorld.WorldBuildHeld,
                region_retirement = _world?.RetirementStatus,
                entities_ready = _world?.Entities?.ReadyForRelease ?? false,
                hero = _world?.Entities?.DescribeHero(),
                render = AndroidRenderCounters.Capture(render),
                render_thread = RenderThreadStatus(),
                profiling = new
                {
                    debug_channel = _profiling?.DebugChannel ?? false,
                    handshake_path = _profiling?.HandshakePath ?? string.Empty,
                    gpu_profile_argument_visible = _profiling?.GpuProfileArgumentVisible ?? false,
                    gpu_passes = Engine_GpuPasses.Report(),
                    scope = "gpu_passes.available proves captured timestamps; managed arguments may omit packaged --gpu-profile; debug channel is startup opt-in"
                },
                audio = sink == null ? null : DescribeAudio(sink),
                texture_policy = AndroidTextureUpload.Policy,
                prepared_texture_images = AndroidTextureUpload.PreparedImages,
                texture_upload_payloads = AndroidTextureUpload.Uploads,
                texture_upload_scope = "Cumulative successful upload payload, including replacements; not live VRAM or driver allocation bytes",
                hero_texture_morph = Refii.Hero.Engine_HeroTextureMorph.CacheMetrics,
                hero_texture_morph_scope = "Current decoded dictionary top-level managed RGBA and composite texture references; excludes active callers, temporary Images and textures retained only by live materials; CompositesBuilt is cumulative",
                prepared_gpu_albedos = new { hits = AndroidGpuTextureCache.Hits,
                    misses = AndroidGpuTextureCache.Misses, invalid = AndroidGpuTextureCache.Invalid,
                    payload_bytes = AndroidGpuTextureCache.PayloadBytes },
                automatic_frontend_actions = false,
                touch_controls = new { enabled = _touch?.ControlsEnabled ?? false,
                    context = (_touch?.CurrentContext ?? TouchContext.Hidden).ToString(),
                    touch_mode = Engine_UiInputMode.Touch },
                diagnostic_buttons = new {
                    performance_open = GetNodeOrNull<Refii.Game.GamePerfOverlay>("GamePerfOverlay")?.Visible ?? false,
                    debug_open = GetNodeOrNull<Refii.Game.GameDevMenu>("GameDevMenu")?.IsOpen ?? false,
                    visible = Diagnostics()?.Visible ?? false,
                },
                scope = "Foreground controller and touch prototype; explicit movie skipping; guest teardown unsupported"
            };
            // The anonymous type as the serializer's type argument, as when this was written here.
            writer.Post(() => Refii.Core.Engine_Json.Serialize(report, StatusJson));
        }
        catch (Exception error) { Fail(error); }
    }

    /// <summary>The front end as the status file states it, from the raw fields <see cref="FollowFrontEnd"/> kept.</summary>
    private object? DescribeFrontEnd() => _frontEnd is not { } f ? null : new
    {
        found = f.Found, screen = f.Screen.ToString(), state = f.State, can_press_a = f.CanPressA,
        cursor = f.Cursor, gui_up = f.GuiUp, should_reveal = f.Reveal
    };

    /// <summary>The scene's diagnostics node, while it stands.</summary>
    private AndroidDiagnosticButtons? Diagnostics()
    {
        if (_diagnostics is not null && GodotObject.IsInstanceValid(_diagnostics)) return _diagnostics;
        return _diagnostics = GetNodeOrNull<AndroidDiagnosticButtons>("AndroidDiagnosticButtons");
    }

    /// <summary>The render thread this run uses and the one refii.toml asks for (AndroidRenderThread).</summary>
    /// <remarks>From the values the live settings last read (<see cref="LiveSettings"/>), refii.toml's
    /// stamp checked each second; read here only while none were.</remarks>
    private object? RenderThreadStatus()
    {
        if (_runtimeConfig.Length == 0 || !File.Exists(_runtimeConfig)) return null;
        return new
        {
            mode = AndroidRenderThread.Describe(_liveValues ?? Refii.Launcher.Core.RuntimeConfig.ReadValues(_runtimeConfig)),
            write_error = _renderThreadError,
        };
    }

    /// <summary>The guest's output effect: Godot's device layout and rate, and what the effect has pulled.</summary>
    /// <param name="sink">The output node GameAudio created; not ready when the extension failed to load.</param>
    private object DescribeAudio(Engine_AudioOutput sink)
    {
        if (!sink.IsReady)
            return new { ready = false };
        HostBridge.AudioOutputStats(out ulong pulled, out ulong underrunSteps, out uint channels);
        HostBridge.AudioStats(out uint queued, out ulong pushed, out _, out ulong dropped, out _);
        return new
        {
            ready = true, mix_rate = (int)AudioServer.GetMixRate(), speaker_pairs = sink.SpeakerPairs,
            channels, frames_pulled = pulled, frames_pushed = pushed, ring_queued = queued,
            dropped, underrun_steps = underrunSteps,
            guest_frames_per_second = _audio?.GuestFramesPerSecond ?? 0
        };
    }

    /// <summary>Installs the host's single frame-sampler pair and an optional private loopback debug channel.</summary>
    /// <remarks>Android owns sampling even if the listener cannot bind. Its debug server installs
    /// no samplers of its own, so clocks and draw-end callbacks are registered once. Neither path
    /// creates GameSession or initializes, steps or shuts down a native guest. The channel
    /// is read once at startup; an environment variable alone cannot opt Android into it.</remarks>
    private void ConfigureProfiling()
    {
        _profiling = AndroidProfilingOptions.Read(_userDirectory, OS.GetCmdlineArgs());
        _profiling.InstallFrameSampling((handshake, _) =>
        {
            // Do not inherit a desktop/public handshake destination or arbitrary debug port.
            // GameDebugServer binds only 127.0.0.1, scans from 8787 and publishes pid/boot identity.
            SetEnvironment("REFII_DEBUG_HANDSHAKE", handshake);
            SetEnvironment("REFII_DEBUG_PORT", "8787");
            AddChild(new GameDebugServer { Name = "GameDebugServer" });
        }, () =>
        {
            AddChild(new Engine_FrameStartNode { Name = "FrameStart" });
            AddChild(new Engine_FrameEndNode { Name = "FrameEnd" });
        });
    }

    /// <summary>Reloads world resolution and authored mesh LOD quality without changing canvas sizing or visibility ranges.</summary>
    /// <remarks>LOD uses the root Window.Size passed to RenderingServer in CanvasItems mode.
    /// The texture size accessor includes canvas stretch and is reported separately, without a GPU readback.
    /// Unchanged inputs preserve a temporary profiler threshold until it is explicitly restored;
    /// a real quality, extent or global project-scale change reapplies configured parity.</remarks>
    /// <param name="force">Read refii.toml again even if its stamp has not moved: the diagnostics just wrote it.</param>
    private void RefreshRenderScale(bool force = false)
    {
        if (_runtimeConfig.Length == 0) return;
        try
        {
            // Read the known app-private file directly: changing this diagnostic setting
            // must never select a different config, resize Android's window or start a guest.
            GameWindowSettings settings = LiveSettings(force);
            // The frame cap follows the same live file, so a changed cap needs no restart.
            int frameCap = AndroidIdlePower.FrameCap(settings.MaxFps);
            if (Engine.MaxFps != frameCap)
            {
                Engine.MaxFps = frameCap;
                GD.Print($"[android] frame cap {(frameCap == 0 ? "off" : frameCap + " fps")}");
            }
            // android_canvas_layout_size: the root's 2D at the layout size, the world's pixels kept.
            bool canvasLayout = AndroidCanvasResolution.Apply(GetTree().Root,
                Engine_RefiiConfig.Bool(Engine_RefiiConfig.Read(out _), AndroidCanvasResolution.Key, false),
                out float stretch, out Vector2I renderedSize);
            if (_renderScale.Apply(settings, scale =>
            {
                Viewport viewport = GetViewport();
                if (RenderingServer.GetCurrentRenderingMethod() == "mobile" &&
                    viewport.Scaling3DMode != Viewport.Scaling3DModeEnum.Bilinear)
                    viewport.Scaling3DMode = Viewport.Scaling3DModeEnum.Bilinear;
                viewport.Scaling3DScale = scale;
            }, scale => AndroidCanvasResolution.WorldScale(scale, stretch, canvasLayout)))
                GD.Print($"[android] root world render scale {_renderScale.Applied:P0}; canvas/UI viewports unchanged");
            ApplyTextureSampling(GetViewport(), settings);
            ApplyColourBuffer(GetViewport(), settings);
            AndroidOpaqueOrder.ApplyFrontToBack(Engine_RefiiConfig.Bool(Engine_RefiiConfig.Read(out _), AndroidOpaqueOrder.FrontToBackKey, false));
            AndroidEnginePatchSwitches.Apply(Engine_RefiiConfig.Read(out _));
            ApplyCanvasLean(settings);
            _sunShadows.Apply(Engine_RefiiConfig.Read(out _), GetTree().Root);
            Viewport lodViewport = GetViewport();
            Vector2I physicalSize = renderedSize;
            float projectScale = ProjectSettings.GetSetting("rendering/scaling_3d/scale", 1.0f).AsSingle();
            if (_meshLod.Apply(settings.MeshLodPixels, physicalSize.X, physicalSize.Y, projectScale,
                threshold => lodViewport.MeshLodThreshold = threshold))
                GD.Print($"[android] root mesh LOD threshold {_meshLod.ParityThreshold}; physical root {physicalSize}, project scale {projectScale}; visibility ranges unchanged");
            _renderScaleError = string.Empty;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // An interrupted or invalid external config update keeps the last successful values.
            // Distinct errors are logged once; one file-access problem cannot flood logcat.
            if (_renderScaleError != error.Message) GD.Print("[android] render scale config read failed: " + error.Message);
            _renderScaleError = error.Message;
        }
    }

    /// <summary>
    /// The live settings as <see cref="AndroidRenderScale.ReadSettings"/> reads them, parsed again only
    /// when refii.toml's stamp moved (or <paramref name="force"/>): otherwise the last read's, after one stat.
    /// </summary>
    /// <exception cref="FileNotFoundException">The live configuration is unavailable.</exception>
    private GameWindowSettings LiveSettings(bool force)
    {
        // Stamped before the read: a write landing during it moves the stamp again, and is read next time.
        AndroidConfigStamp stamp = AndroidConfigStamp.Of(_runtimeConfig);
        if (_liveSettings is { } kept && !AndroidConfigStamp.Reread(_liveStamp, stamp, force)) return kept;
        _liveSettings = null;
        _liveValues = null;
        if (!File.Exists(_runtimeConfig)) throw new FileNotFoundException("Live render-scale configuration is unavailable", _runtimeConfig);
        var values = Refii.Launcher.Core.RuntimeConfig.ReadValues(_runtimeConfig);
        GameWindowSettings settings = AndroidRenderScale.SettingsOf(values, _runtimeConfig);
        _liveValues = values;
        _liveStamp = stamp;
        _liveSettings = settings;
        return settings;
    }

    /// <summary>
    /// `canvas_lean`, applied when the setting changes, so the debug channel's live lever
    /// (`canvasLean`) holds until the file says otherwise. See Engine_CanvasLean.
    /// </summary>
    private void ApplyCanvasLean(GameWindowSettings settings)
    {
        if (_canvasLeanSetting == settings.CanvasLean) return;
        _canvasLeanSetting = settings.CanvasLean;
        Engine_CanvasLean.Enabled = settings.CanvasLean;
        GD.Print($"[android] canvas_lean {(settings.CanvasLean ? "on" : "off")}");
    }

    /// <summary>GameUi's UI layer, the canvas the touch controls may share; null until the UI stands.</summary>
    private CanvasLayer? UiLayer()
    {
        if (_uiLayer is not null && GodotObject.IsInstanceValid(_uiLayer)) return _uiLayer;
        return _uiLayer = _uiNode is not null && GodotObject.IsInstanceValid(_uiNode) ? _uiNode.GetNodeOrNull<CanvasLayer>("UiLayer") : null;
    }

    /// <summary>
    /// The world's texture sampling at the applied render scale: the mip bias Godot adds for
    /// it cancelled, and the anisotropic filtering setting applied.
    /// </summary>
    /// <remarks>
    /// GODOT SAMPLES A SCALED WORLD AS IF IT WERE FULL SIZE. It adds log2(scale) to every
    /// mipmapped sampler's bias (renderer_viewport.cpp), which pays off only when TAA or FSR 2
    /// rebuilds the detail. Android upscales bilinearly with neither, so at 35% the bias of
    /// -1.5 fetched about eight times the texels per pixel, for shimmer and memory traffic.
    /// The player's `texture_mipmap_bias` is added on top. `anisotropic_filtering` was never
    /// applied on Android (GameWindow is desktop-only), leaving Godot's 4x on every surface.
    /// </remarks>
    /// <summary>
    /// `hdr_2d`: whether the root viewport keeps Godot's RGBA16F targets or, off, the Mobile
    /// renderer's 10-bit 3D buffer and an RGBA8 2D target: half the colour bandwidth and tile
    /// memory, for highlights that clip a little sooner. On a Galaxy S10e (Adreno 640) off took 6% off the GPU's work in Bowerstone
    /// Market, the native-resolution canvas pass halving. A change rebuilds every pipeline once,
    /// a stall of some seconds, so it is a launch or preset change rather than a live lever.
    /// </summary>
    private static void ApplyColourBuffer(Viewport viewport, GameWindowSettings settings)
    {
        if (viewport.UseHdr2D != settings.Hdr2D)
        {
            viewport.UseHdr2D = settings.Hdr2D;
            GD.Print($"[android] world colour buffer {(settings.Hdr2D ? "RGBA16F" : "RGB10A2, canvas RGBA8")}");
        }
    }

    private static void ApplyTextureSampling(Viewport viewport, GameWindowSettings settings)
    {
        float bias = -MathF.Log2(MathF.Min(viewport.Scaling3DScale, 1f)) + settings.MipmapBias;
        if (MathF.Abs(viewport.TextureMipmapBias - bias) > 0.001f)
        {
            viewport.TextureMipmapBias = bias;
            GD.Print($"[android] texture mip bias {bias:0.00} at render scale {viewport.Scaling3DScale:P0}");
        }

        Viewport.AnisotropicFiltering aniso = settings.Anisotropic switch
        {
            >= 16 => Viewport.AnisotropicFiltering.Anisotropy16X,
            >= 8 => Viewport.AnisotropicFiltering.Anisotropy8X,
            >= 4 => Viewport.AnisotropicFiltering.Anisotropy4X,
            >= 2 => Viewport.AnisotropicFiltering.Anisotropy2X,
            _ => Viewport.AnisotropicFiltering.Disabled,
        };
        if (viewport.AnisotropicFilteringLevel != aniso)
        {
            viewport.AnisotropicFilteringLevel = aniso;
            GD.Print($"[android] anisotropic filtering {aniso}");
        }
    }

    /// <summary>Updates managed and libc environment views before either side caches its configuration.</summary>
    private static void SetEnvironment(string name, string value)
    {
        System.Environment.SetEnvironmentVariable(name, value);
        if (Refii.Core.Engine_Platform.IsAndroid && setenv(name, value, 1) != 0)
            throw new InvalidOperationException("Android setenv failed for " + name);
    }
}
