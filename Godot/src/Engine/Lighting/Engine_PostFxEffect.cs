using System;
using System.Collections.Generic;
using Godot;
using Godot.Collections;
using Refii.Core;

namespace Refii.Lighting;

/// <summary>Runs the compute post-effects chain only on compatible scene-color images.</summary>
[Tool]
public partial class Engine_PostFxEffect : CompositorEffect
{
    /// <summary>
    /// Which view this chain draws: 0 the window's (player one's on split screen), 1 player two's.
    /// The per-view layers (Engine_FxAdditiveLayer, Engine_EntityHighlightLayer) publish a
    /// snapshot per view, and this is how the render callback — which cannot tell two
    /// same-sized views apart — picks its own.
    /// </summary>
    public int View { get; set; }

    public const string ReduceShaderPath = "res://src/Engine/Lighting/fable_postfx_reduce.glsl";
    public const string HistogramShaderPath = "res://src/Engine/Lighting/fable_postfx_histogram.glsl";
    public const string Downscale4ShaderPath = "res://src/Engine/Lighting/fable_postfx_downscale4.glsl";
    public const string DofDownscaleShaderPath = "res://src/Engine/Lighting/fable_postfx_dof_downscale.glsl";
    public const string RadialShaderPath = "res://src/Engine/Lighting/fable_postfx_radial.glsl";
    public const string BrightcutShaderPath = "res://src/Engine/Lighting/fable_postfx_brightcut.glsl";
    public const string GaussShaderPath = "res://src/Engine/Lighting/fable_postfx_gauss.glsl";
    public const string CompositeShaderPath = "res://src/Engine/Lighting/fable_postfx_composite.glsl";
    public const string SunLayerShaderPath = "res://src/Engine/Lighting/fable_postfx_sunlayer.glsl";

    public const string AddLayerShaderPath = "res://src/Engine/Lighting/fable_postfx_addlayer.glsl";
    public const string HighlightBlurShaderPath = "res://src/Engine/Lighting/fable_postfx_highlightblur.glsl";
    public const string HighlightPrepShaderPath = "res://src/Engine/Lighting/fable_postfx_highlightprep.glsl";
    public const string HighlightCompositeShaderPath = "res://src/Engine/Lighting/fable_postfx_highlightcomposite.glsl";
    public const string HaloLayerShaderPath = "res://src/Engine/Lighting/fable_postfx_halolayer.glsl";
    public const string VolumetricShaftsShaderPath = "res://src/Engine/Lighting/fable_postfx_volumetric_shafts.glsl";
    public const string VolumetricUpsampleShaderPath = "res://src/Engine/Lighting/fable_postfx_volumetric_upsample.glsl";

    public const string GlobalExposureName = "fable_exposure_tex";

    /// <summary>
    /// Player two's exposure on split screen. Globals are one value per frame for every view, so
    /// each view's chain publishes its own and the readers pick by the second view's layer
    /// (<see cref="Refii.Models.Engine_ClassicViews.SecondViewLayer"/>); a single view never reads it.
    /// </summary>
    public const string GlobalExposureSecondName = "fable_exposure_tex_second";
    public const string GlobalExposureValidName = "fable_exposure_valid";
    public const float DisabledMaterialExposure = 1.0f;
    public const float ElementDepthBias = 0.000001f;
    public const int RequiredSettleFrames = 5;

    public const int HistogramBins = Engine_PostFxModel.Bins + 1;
    public const int HistogramBytes = HistogramBins * sizeof(uint);
    public const int StateBytes = Engine_PostFxState.Bytes;
    public const int ParamsFloats = 64;
    public const int ParamsBytes = ParamsFloats * sizeof(float);
    public const int SmallPushBytes = 16;
    public const int WidePushBytes = 32;
    public const int GaussPushBytes = 48;

    public const int HaloCapacity = 64;
    public const int HaloFloats = 12;
    public const int HaloBufferBytes = HaloCapacity * HaloFloats * sizeof(float);

    /// <summary>Three vec4s per highlight slot: colour, screen rect, nearest depth.</summary>
    public const int HighlightFloats = 12;
    public const int HighlightBufferBytes =
        Refii.Entities.Engine_EntityHighlightLayer.Slots * HighlightFloats * sizeof(float);

    public const int ShaftBufferVec4s = 458;
    public const int ShaftBufferFloats = ShaftBufferVec4s * 4;
    public const int ShaftBufferBytes = ShaftBufferFloats * sizeof(float);

    public const float BloomSigmaScale = 2.2f;
    public const float DofSigmaScale = 2.0f;
    public const float SigmaPerQuarterWidth = 0.003125f;
    public const float SigmaPerQuarterHeight = 0.0055556f;

    private static bool _globalsRegistered;
    private static ImageTexture? _placeholderTexture;
    private static readonly Engine_PostFxEffect?[] _publishers = new Engine_PostFxEffect?[2];

    // D3D12 / Intel Arc DXIL warning: never bind an uninitialized `new Texture2Drd()` (`TextureRdRid == default`)
    // to a global shader parameter. Shaders sample `fable_exposure_tex` before PostFX runs its first pass;
    // a null RID creates an unbound SRV descriptor on D3D12.
    private static ImageTexture GetOrCreateFallbackExposureTexture()
    {
        if (_placeholderTexture is null || !GodotObject.IsInstanceValid(_placeholderTexture))
        {
            Godot.Image image = Godot.Image.CreateEmpty(1, 1, false, Godot.Image.Format.Rgba8);
            image.Fill(Colors.White);
            _placeholderTexture = ImageTexture.CreateFromImage(image);
        }

        return _placeholderTexture;
    }

    /// <summary>The exposure global this chain's view reads.</summary>
    private string ExposureGlobal => View == 0 ? GlobalExposureName : GlobalExposureSecondName;
    private readonly Engine_RenderBufferTelemetry _renderBuffers = new();

    /// <summary>Player one's world renderer-reported extents; null until its compositor has rendered.</summary>
    internal static Engine_RenderBufferTelemetry.Sample? RenderBuffers => _publishers[0]?._renderBuffers.Latest;

    private sealed class KeyedSet
    {
        private Rid _a;
        private Rid _b;

        public Rid Set { get; private set; }

        public bool Matches(RenderingDevice rd, Rid a, Rid b) =>
            Set.IsValid && a == _a && b == _b && rd.UniformSetIsValid(Set);

        public void Replace(RenderingDevice rd, Rid a, Rid b, Rid shader, RDUniform[] uniforms)
        {
            Release(rd);
            Set = rd.UniformSet(uniforms, shader, 0);
            _a = a;
            _b = b;
        }

        public void Release(RenderingDevice rd)
        {
            if (Set.IsValid && rd.UniformSetIsValid(Set))
            {
                rd.FreeRid(Set);
            }

            Set = default;
            _a = default;
            _b = default;
        }
    }

    private readonly int _dumpPeriod;
    private long _frameIndex;

    // Flicker trap (`postfx_flicker_trap`): the last TrapRingDepth composited frames kept
    // at quarter resolution on the GPU, written out as PNGs when the reduce pass sees the
    // scene's mean luminance jump by more than the stated ratio between consecutive frames,
    // or when the debug channel asks.
    private const int TrapRingDepth = 48;
    private const int TrapPollFrames = 20;
    private static volatile bool _trapDumpRequested;
    private static volatile string _trapLastDump = string.Empty;
    private static volatile bool _trapArmed;
    private readonly float _trapRatio;
    private readonly Rid[] _trapRing = new Rid[TrapRingDepth];
    private readonly long[] _trapRingFrame = new long[TrapRingDepth];
    private readonly Rid[] _trapRingSets = new Rid[TrapRingDepth];
    private Rid _trapRingColour;
    private Rid _trapUnitState;
    private int _trapRingNext;
    private float _trapLastHitFrame;   // the seeded state holds 0, which is never a real frame

    /// <summary>Whether any post-fx instance was constructed with the flicker trap on.</summary>
    public static bool TrapArmed => _trapArmed;

    /// <summary>The directory the trap last wrote its ring to; empty until it has.</summary>
    public static string TrapLastDump => _trapLastDump;

    /// <summary>Asks the armed trap to write its ring on the next rendered frame.</summary>
    public static void RequestTrapDump() => _trapDumpRequested = true;

    private RenderingDevice? _rd;
    private Texture2Drd? _exposureTexture;

    private Rid _reduceShader;
    private Rid _reducePipeline;
    private Rid _histogramShader;
    private Rid _histogramPipeline;
    private Rid _downscale4Shader;
    private Rid _downscale4Pipeline;
    private Rid _radialShader;
    private Rid _radialPipeline;
    private Rid _sunlayerShader;
    private Rid _sunlayerPipeline;
    private Rid _addlayerShader;
    private Rid _addlayerPipeline;
    private Rid _highlightShader;
    private Rid _highlightPipeline;
    private Rid _highlightPrepShader;
    private Rid _highlightPrepPipeline;
    private Rid _highlightCompositeShader;
    private Rid _highlightCompositePipeline;
    private Rid _highlightBuffer;
    private Rid _halolayerShader;
    private Rid _halolayerPipeline;
    private Rid _volShaftsShader;
    private Rid _volShaftsPipeline;
    private Rid _volUpsampleShader;
    private Rid _volUpsamplePipeline;
    private Rid _brightcutShader;
    private Rid _brightcutPipeline;
    private Rid _gaussShader;
    private Rid _dofDownscaleShader;
    private Rid _dofDownscalePipeline;
    private Rid _gaussPipeline;
    private Rid _compositeShader;
    private Rid _compositePipeline;

    private Rid _histogramBuffer;
    private Rid _stateBuffer;
    private Rid _paramsBuffer;
    private Rid _haloBuffer;
    private Rid _haloFallbackArray;
    private Rid _shaftBuffer;
    private Rid _shadowFallbackArray;
    private Rid _linearSampler;
    private Rid _nearestSampler;
    private Rid _exposureRd;
    private Engine_PostFxSaturationMask? _satMask;

    /// <summary>This frame composites with the saturation mask: masked entities are up and the theme states a masked pair.</summary>
    private bool _maskWanted;

    private Rid _quarter;
    private Rid _occl;
    private readonly Rid[] _rays = new Rid[2];
    private readonly Rid[] _bloom = new Rid[2];
    private readonly Rid[] _dof = new Rid[2];
    private readonly Rid[] _shaftHalf = new Rid[2];
    private readonly Rid[] _shaftDepthHalf = new Rid[2];

    /// <summary>The highlight blur's ping-pong, at the layer's own half resolution: the guest
    /// counts its blur width in texels of that buffer, so blurring at any other size would
    /// change the stated width.</summary>
    private readonly Rid[] _glow = new Rid[2];

    /// <summary>
    /// The size the glow ping-pong was last allocated at: the LAYER's, not the internal half.
    ///
    /// The layer is half the window because that is what the guest allocates (half of the
    /// extent it was told), while the internal size is the window times the player's render
    /// scale. At native the two agree; under FSR or any render scale below 100 they do not,
    /// and the prep pass fetched the layer by pixel into a buffer of the other size — the
    /// silhouette landed scaled and displaced, so the halo stood away from the character.
    /// </summary>
    private Vector2I _glowSize;
    private Rid _glowLayer;

    private Vector2I _fullSize;
    private Vector2I _quarterSize;
    private Vector2I _halfSize;

    private Rid _reduceSet;
    private Rid _histogramSet;
    private Rid _brightcutSet;
    private readonly Rid[] _radialSets = new Rid[2];
    private Rid _gaussBloomHSet;
    private Rid _gaussBloomVSet;
    private Rid _gaussDofHSet;
    private Rid _gaussDofVSet;
    private readonly KeyedSet _downscale4Set = new();
    private readonly KeyedSet _dofDownscaleSet = new();
    private readonly KeyedSet _compositeSet = new();
    private readonly KeyedSet _sunlayerSet = new();
    private readonly KeyedSet _addlayerSet = new();
    private readonly KeyedSet _halolayerSet = new();
    private readonly KeyedSet _glowHSet = new();
    private readonly KeyedSet _glowVSet = new();
    private readonly KeyedSet _highlightPrepSet = new();
    private readonly KeyedSet _highlightCompositeSet = new();
    private readonly KeyedSet[] _volShaftsSets = { new(), new() };
    private readonly KeyedSet[] _volUpsampleSets = { new(), new() };
    private readonly float[] _highlightScratch = new float[Refii.Entities.Engine_EntityHighlightLayer.Slots * HighlightFloats];
    private readonly byte[] _highlightBytes = new byte[HighlightBufferBytes];
    private readonly Refii.Entities.Engine_EntityHighlightLayer.Target[] _highlightTargets =
        new Refii.Entities.Engine_EntityHighlightLayer.Target[Refii.Entities.Engine_EntityHighlightLayer.Slots];

    private readonly byte[] _pushSmall = new byte[SmallPushBytes];
    private readonly byte[] _pushWide = new byte[WidePushBytes];
    private readonly byte[] _pushGauss = new byte[GaussPushBytes];
    private readonly byte[] _pushSun = new byte[64];
    private readonly byte[] _pushHalo = new byte[48];
    private readonly byte[] _pushShafts = new byte[40];
    private readonly byte[] _pushShaftUpsample = new byte[48];
    private readonly float[] _shaftScratch = new float[ShaftBufferFloats];
    private readonly byte[] _shaftBytes = new byte[ShaftBufferBytes];
    private readonly Engine_VolumetricLightShafts.ShaftLightInput[] _shaftLightScratch =
        new Engine_VolumetricLightShafts.ShaftLightInput[Engine_VolumetricLightShafts.MaxShaftLights];
    private readonly Engine_VolumetricLightShafts.MistVolumeSnapshot[] _shaftMistScratch =
        new Engine_VolumetricLightShafts.MistVolumeSnapshot[Engine_VolumetricLightShafts.MaxMistVolumes];
    private readonly Engine_VolumetricLightShafts.VolumetricCapsule[] _shaftCapsuleScratch =
        new Engine_VolumetricLightShafts.VolumetricCapsule[Engine_VolumetricLightShafts.MaxCapsules];

    private int _shaftPingPong;
    private bool _shaftHistoryValid;

    /// <summary>
    /// The frame (Engine.GetFramesDrawn) this effect last marched its shafts, or -1. A view that was not
    /// drawn in between (the window under a split screen, a half under a shared one) has no previous frame:
    /// its history is the picture from before the gap, under the camera it had then.
    /// </summary>
    private long _shaftFrame = -1;
    private Vector3 _prevShaftCamOrigin;
    private Vector3 _prevShaftForward;
    private Vector3 _prevShaftRight;
    private Vector3 _prevShaftUp;
    private float _prevShaftPx;
    private float _prevShaftPy;

    private readonly float[] _bloomWeightsH = new float[Engine_PostFxModel.GaussianTaps];
    private readonly float[] _bloomWeightsV = new float[Engine_PostFxModel.GaussianTaps];
    private readonly float[] _dofWeightsH = new float[Engine_PostFxModel.GaussianTaps];
    private readonly float[] _dofWeightsV = new float[Engine_PostFxModel.GaussianTaps];
    private readonly float[] _paramsScratch = new float[ParamsFloats];
    private readonly byte[] _paramsBytes = new byte[ParamsBytes];
    private readonly float[] _haloScratch = new float[HaloCapacity * HaloFloats];
    private readonly byte[] _haloBytes = new byte[HaloBufferBytes];
    private readonly System.Collections.Generic.List<Refii.Fx.Engine_PostFxHalos.WorldHalo> _haloSnapshot = new();
    private readonly Engine_PostFxModel.Derived _derived = new();

    private volatile bool _ready;
    private bool _disposed;
    private bool _firstFrame = true;

    /// <summary>
    /// THE TITLE RESETS ITS TONE HISTORY WHILE A LEVEL LOADS. `sub_82181B68`, run every frame
    /// from the scene tick, clears T+0x4190 for as long as the scene is streaming, so the first
    /// frame drawn after the load snaps the smoothed min/avg/max and the CDF to that frame
    /// (`sub_822767E0`: blend 1 with no history). A theme stating EXPOSURE_SPEED 0, as the caves
    /// do, then holds them: the room is levelled to its own first picture, not to the region
    /// the player came from. The world calls this when its load hold releases; each effect
    /// snaps on its next frame.
    /// </summary>
    public static void ResetHistory() => System.Threading.Interlocked.Increment(ref _historyResetGeneration);

    private static int _historyResetGeneration;
    private int _historyResetSeen;

    /// <summary>
    /// ONE SCREEN'S TONE HISTORY ACROSS A CO-OP SPLIT OR MERGE. The title has one tone history for its one
    /// screen; each co-op view's chain keeps its own. A view that starts drawing again continues the picture
    /// of the view it replaces (the halves the window's at a split, the window player one's half at a merge
    /// or when player two leaves), so it takes that chain's state and histogram instead of its own stale
    /// ones or a snap to its first frame: under a theme holding its exposure (EXPOSURE_SPEED 0, the caves)
    /// every view keeps the level the room took at its load, and the brightness does not step.
    /// Main thread; the copy runs on the render thread ahead of the frame and of any release queued after
    /// it (GameSplitView asks before it frees the halves).
    /// </summary>
    public static void Continue(Camera3D? from, Camera3D? to)
    {
        if (Of(from) is not { } source || Of(to) is not { } target || ReferenceEquals(source, target))
        {
            return;
        }

        RenderingServer.CallOnRenderThread(Callable.From(() => target.TakeStateOf(source)));
    }

    private static Engine_PostFxEffect? Of(Camera3D? camera)
    {
        if (camera is null || !GodotObject.IsInstanceValid(camera) ||
            camera.Compositor is not { } compositor || !GodotObject.IsInstanceValid(compositor))
        {
            return null;
        }

        foreach (CompositorEffect effect in compositor.CompositorEffects)
        {
            if (effect is Engine_PostFxEffect postFx)
            {
                return postFx;
            }
        }

        return null;
    }

    /// <summary>Render thread: this chain's state and histogram become <paramref name="source"/>'s.</summary>
    private void TakeStateOf(Engine_PostFxEffect source)
    {
        if (_disposed || !_ready || _rd is null || !ReferenceEquals(_rd, source._rd) || source._firstFrame ||
            !_stateBuffer.IsValid || !source._stateBuffer.IsValid ||
            !_histogramBuffer.IsValid || !source._histogramBuffer.IsValid)
        {
            return;
        }

        _rd.BufferCopy(source._stateBuffer, _stateBuffer, 0u, 0u, (uint)Engine_PostFxState.ToneBytes);
        _rd.BufferCopy(source._histogramBuffer, _histogramBuffer, 0u, 0u, (uint)HistogramBytes);
        _firstFrame = false;
        _histogramValid = source._histogramValid;
        _historyResetSeen = source._historyResetSeen;
    }
    private bool _histogramValid;
    private bool _measuredAny;
    private bool _materialDisabledWritten;
    private double _lastFrameSeconds;
    private readonly ColourLayerContract _colourContract = new();
    private Engine_PostFxModel.ColourRoute _colourRoute = Engine_PostFxModel.ColourRoute.Unusable;

    /// <summary>Owned full-size storage image for MSAA resolve and post-effects copy-back.</summary>
    private Rid _staging;

    public static void RegisterGlobals()
    {
        if (_globalsRegistered)
        {
            return;
        }

        _globalsRegistered = true;
        ImageTexture fallback = GetOrCreateFallbackExposureTexture();
        RenderingServer.GlobalShaderParameterSet(GlobalExposureName, fallback);
        RenderingServer.GlobalShaderParameterSet(GlobalExposureSecondName, fallback);
        RenderingServer.GlobalShaderParameterSet(GlobalExposureValidName, 0.0f);
    }

    public Engine_PostFxEffect()
    {
        EffectCallbackType = EffectCallbackTypeEnum.PostTransparent;

        int period = 0;
        float trap = 0f;
        try
        {
            System.Collections.Generic.IReadOnlyDictionary<string, string> config =
                Refii.Core.Engine_RefiiConfig.Read(out _);
            period = Refii.Core.Engine_RefiiConfig.Int(config, "postfx_dump_period", 0);
            trap = Refii.Core.Engine_RefiiConfig.Float(config, "postfx_flicker_trap", 0f);
        }
        catch (System.Exception e)
        {
            GD.PrintErr($"[postfx] could not read the post-fx diagnostics settings: {e.Message}");
        }

        if (int.TryParse(OS.GetEnvironment("REFII_EXPOSURE_DUMP"), out int envPeriod) && envPeriod > 0)
        {
            period = envPeriod;
        }

        if (float.TryParse(OS.GetEnvironment("REFII_FLICKER_TRAP"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float envTrap) && envTrap > 0f)
        {
            trap = envTrap;
        }

        if (period > 0)
        {
            _dumpPeriod = period;
            GD.Print($"[postfx] dumping the histogram and state every {period} frame(s); " +
                "this reads back from the GPU and stalls the device");
        }

        // A ratio of 1 or less can never trip, so it is the same as off.
        _trapRatio = float.IsFinite(trap) && trap > 1f ? trap : 0f;
        if (_trapRatio > 0f)
        {
            _trapArmed = true;
            GD.Print($"[postfx] flicker trap armed at x{_trapRatio:0.##}: the last {TrapRingDepth} " +
                $"frames are kept and written under user://flicker on a hit or on `flicker`");
        }

        RegisterGlobals();
        RenderingServer.CallOnRenderThread(Callable.From(Initialise));
    }

    private void Initialise()
    {
        if (_disposed || Refii.Core.Engine_RdHealth.IsDeviceLost)
        {
            return;
        }

        _rd = RenderingServer.GetRenderingDevice();
        if (_rd is null)
        {
            return;
        }

        if (!TryBuild(ReduceShaderPath, out _reduceShader, out _reducePipeline) ||
            !TryBuild(HistogramShaderPath, out _histogramShader, out _histogramPipeline) ||
            !TryBuild(Downscale4ShaderPath, out _downscale4Shader, out _downscale4Pipeline) ||
            !TryBuild(DofDownscaleShaderPath, out _dofDownscaleShader, out _dofDownscalePipeline) ||
            !TryBuild(RadialShaderPath, out _radialShader, out _radialPipeline) ||
            !TryBuild(SunLayerShaderPath, out _sunlayerShader, out _sunlayerPipeline) ||
            !TryBuild(AddLayerShaderPath, out _addlayerShader, out _addlayerPipeline) ||
            !TryBuild(HighlightBlurShaderPath, out _highlightShader, out _highlightPipeline) ||
            !TryBuild(HighlightPrepShaderPath, out _highlightPrepShader, out _highlightPrepPipeline) ||
            !TryBuild(HighlightCompositeShaderPath, out _highlightCompositeShader, out _highlightCompositePipeline) ||
            !TryBuild(HaloLayerShaderPath, out _halolayerShader, out _halolayerPipeline) ||
            !TryBuild(BrightcutShaderPath, out _brightcutShader, out _brightcutPipeline) ||
            !TryBuild(GaussShaderPath, out _gaussShader, out _gaussPipeline) ||
            !TryBuild(CompositeShaderPath, out _compositeShader, out _compositePipeline))
        {
            return;
        }

        _ = TryBuild(VolumetricShaftsShaderPath, out _volShaftsShader, out _volShaftsPipeline);
        _ = TryBuild(VolumetricUpsampleShaderPath, out _volUpsampleShader, out _volUpsamplePipeline);

        _histogramBuffer = _rd.StorageBufferCreate(HistogramBytes, new byte[HistogramBytes]);
        _stateBuffer = _rd.StorageBufferCreate(StateBytes, Engine_PostFxState.SeedBytes());
        _paramsBuffer = _rd.UniformBufferCreate(ParamsBytes, new byte[ParamsBytes]);
        _haloBuffer = _rd.StorageBufferCreate(HaloBufferBytes, new byte[HaloBufferBytes]);
        _highlightBuffer = _rd.StorageBufferCreate(HighlightBufferBytes, new byte[HighlightBufferBytes]);
        _shaftBuffer = _rd.StorageBufferCreate(ShaftBufferBytes, new byte[ShaftBufferBytes]);

        byte[] shadowFallbackBytes = new byte[32];
        for (int i = 0; i < 16; i++)
        {
            shadowFallbackBytes[2 * i] = 0x00;
            shadowFallbackBytes[(2 * i) + 1] = 0x78; // 0x7800 = 32768.0f in IEEE-754 binary16
        }

        _shadowFallbackArray = _rd.TextureCreate(
            new RDTextureFormat
            {
                Width = 4,
                Height = 4,
                ArrayLayers = 1,
                Format = RenderingDevice.DataFormat.R16Sfloat,
                TextureType = RenderingDevice.TextureType.Type2DArray,
                UsageBits = RenderingDevice.TextureUsageBits.SamplingBit |
                    RenderingDevice.TextureUsageBits.CanUpdateBit,
            },
            new RDTextureView(),
            new Array<byte[]> { shadowFallbackBytes });

        // The sprite-array binding must always hold something valid; before the halo feed has
        // resolved any authored sprite it binds this single white texel (every published layer
        // is -1 then, so the shader never actually samples it).
        _haloFallbackArray = _rd.Texture(
            new RDTextureFormat
            {
                Width = 1,
                Height = 1,
                ArrayLayers = 1,
                Format = RenderingDevice.DataFormat.R8G8B8A8Unorm,
                TextureType = RenderingDevice.TextureType.Type2DArray,
                UsageBits = RenderingDevice.TextureUsageBits.SamplingBit |
                    RenderingDevice.TextureUsageBits.CanUpdateBit,
            },
            new RDTextureView(),
            new byte[] { 255, 255, 255, 255 });

        _linearSampler = _rd.Sampler(new RDSamplerState
        {
            MinFilter = RenderingDevice.SamplerFilter.Linear,
            MagFilter = RenderingDevice.SamplerFilter.Linear,
            MipFilter = RenderingDevice.SamplerFilter.Linear,
            RepeatU = RenderingDevice.SamplerRepeatMode.ClampToEdge,
            RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge,
        });
        _nearestSampler = _rd.Sampler(new RDSamplerState
        {
            MinFilter = RenderingDevice.SamplerFilter.Nearest,
            MagFilter = RenderingDevice.SamplerFilter.Nearest,
            MipFilter = RenderingDevice.SamplerFilter.Nearest,
            RepeatU = RenderingDevice.SamplerRepeatMode.ClampToEdge,
            RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge,
        });

        _exposureRd = CreateTexture(
            1, 1, RenderingDevice.DataFormat.R32Sfloat,
            BitConverter.GetBytes(DisabledMaterialExposure));

        if (_trapRatio > 0f)
        {
            // A state buffer whose applied exposure is exactly 1, so the ring's downscale
            // records the composited frame as it is rather than re-exposing it.
            var unit = new byte[Engine_PostFxState.Header * sizeof(float)];
            BitConverter.TryWriteBytes(unit.AsSpan(Engine_PostFxState.AppliedFrame * sizeof(float)), 1.0f);
            _trapUnitState = _rd.StorageBufferCreate((uint)unit.Length, unit);
        }

        // The saturation mask is the one pass the chain runs without: unbuilt, masked entities
        // take the plain saturation.
        _satMask = new Engine_PostFxSaturationMask(_rd);
        if (!_satMask.Initialise())
        {
            _satMask = null;
        }

        bool ready = _histogramBuffer.IsValid && _stateBuffer.IsValid && _paramsBuffer.IsValid &&
            _haloBuffer.IsValid && _highlightBuffer.IsValid && _haloFallbackArray.IsValid &&
            _shaftBuffer.IsValid && _shadowFallbackArray.IsValid && _linearSampler.IsValid &&
            _nearestSampler.IsValid && _exposureRd.IsValid;
        if (!ready)
        {
            _ = Refii.Core.Engine_RdHealth.TripDeviceLost("Engine_PostFxEffect.Initialise", "buffer, sampler or exposure texture creation failed");
            GD.PrintErr("[postfx] buffer, sampler or exposure texture creation failed; chain disabled");
            return;
        }

        _reduceSet = _rd.UniformSet(new RDUniform[]
        {
            Buffer(_histogramBuffer, 0),
            Buffer(_stateBuffer, 1),
            UniformBlock(_paramsBuffer, 2),
            Image(_exposureRd, 3),
        }, _reduceShader, 0);
        if (!Refii.Core.Engine_RdHealth.CheckRdValid(_reduceSet, "Engine_PostFxEffect.Initialise.ReduceSet"))
        {
            GD.PrintErr("[postfx] reduce uniform set creation failed; chain disabled");
            return;
        }

        _ready = true;
        ClaimExposure();
    }

    /// <summary>1 while a claim is queued for the main thread.</summary>
    private int _claimQueued;

    /// <summary>
    /// Points this view's exposure global at this chain's exposure, on the main thread: once it is
    /// built, and whenever it runs while another chain holds the global. Two cameras run a chain for
    /// view 0 — the window's and player one's split view (Refii.Game.GameSplitView) — and only the
    /// one drawing runs, so the one that runs holds the global; the split view's, released when local
    /// co-op ends, put the placeholder in and left it there.
    /// </summary>
    private void ClaimExposure()
    {
        if (System.Threading.Interlocked.Exchange(ref _claimQueued, 1) == 1)
        {
            return;
        }

        Rid exposureRid = _exposureRd;
        Callable.From(() =>
        {
            System.Threading.Volatile.Write(ref _claimQueued, 0);
            if (_disposed || !_ready || _exposureRd != exposureRid || !exposureRid.IsValid)
            {
                return;
            }

            _exposureTexture ??= new Texture2Drd();
            _exposureTexture.TextureRdRid = exposureRid;
            _publishers[View] = this;
            RenderingServer.GlobalShaderParameterSet(ExposureGlobal, _exposureTexture);
            if (View == 0)
            {
                RenderingServer.GlobalShaderParameterSet(GlobalExposureValidName, 1.0f);
            }
        }).CallDeferred();
    }

    private bool TryBuild(string path, out Rid shader, out Rid pipeline)
    {
        shader = default;
        pipeline = default;

        var file = GD.Load<RDShaderFile>(path);
        if (file is null)
        {
            GD.PrintErr($"[postfx] could not load {path}");
            return false;
        }

        RDShaderSpirV spirv = file.GetSpirV();
        if (spirv.CompileErrorCompute.Length != 0)
        {
            GD.PrintErr($"[postfx] {path} failed to compile: {spirv.CompileErrorCompute}");
            return false;
        }

        shader = _rd!.Shader(spirv);
        if (!shader.IsValid)
        {
            GD.PrintErr($"[postfx] {path}: shader creation failed; chain disabled");
            return false;
        }

        pipeline = _rd!.ComputePipeline(shader);
        if (!pipeline.IsValid)
        {
            GD.PrintErr($"[postfx] {path}: compute pipeline creation failed; chain disabled");
            return false;
        }

        return true;
    }

    private Rid CreateTexture(
        int width, int height, RenderingDevice.DataFormat format, byte[]? initial = null,
        bool readable = false)
    {
        if (Refii.Core.Engine_RdHealth.IsDeviceLost)
        {
            return default;
        }

        var textureFormat = new RDTextureFormat
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = format,
            TextureType = RenderingDevice.TextureType.Type2D,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit |
                RenderingDevice.TextureUsageBits.StorageBit |
                RenderingDevice.TextureUsageBits.CanUpdateBit |
                RenderingDevice.TextureUsageBits.CanCopyToBit |
                (readable ? RenderingDevice.TextureUsageBits.CanCopyFromBit : 0),
        };

        Rid rid = _rd!.Texture(textureFormat, new RDTextureView(), initial);
        if (!Refii.Core.Engine_RdHealth.CheckRdValid(rid, "Engine_PostFxEffect.CreateTexture"))
        {
            return default;
        }
        if (initial is null)
        {
            EndBatch();
            _rd!.TextureClear(rid, new Color(0.0f, 0.0f, 0.0f, 0.0f), 0, 1, 0, 1);
        }

        return rid;
    }

    private long _batchList = -1;
    private bool _batchHasDispatch;
    private bool _raysZeroCleared;
    private bool _occlZeroCleared;
    private bool _bloomZeroCleared;

    /// <summary>
    /// Multi-pass compute command list batching across UAV-to-SRV transitions is enabled on Vulkan
    /// and disabled on Direct3D 12, where RenderingDeviceDriverD3D12 resolves UAV/SRV resource state
    /// transitions at ComputeListBegin/ComputeListEnd boundaries.
    /// </summary>
    public static bool ShouldBatchComputeLists(bool isD3D12Driver) => !isD3D12Driver;

    private void BeginBatch()
    {
        if (_batchList >= 0 || ClassicPost || Refii.Core.Engine_RdHealth.IsDeviceLost ||
            !ShouldBatchComputeLists(Refii.Textures.Engine_TextureUpload.IsD3D12Driver))
        {
            return;
        }

        _batchList = _rd!.ComputeListBegin();
        _batchHasDispatch = false;
    }

    private void EndBatch()
    {
        if (_batchList < 0)
        {
            return;
        }

        _rd!.ComputeListEnd();
        _batchList = -1;
        _batchHasDispatch = false;
    }

    private void ClearRaysIfDirty()
    {
        if (_raysZeroCleared)
        {
            return;
        }

        Clear(_rays[0]);
        _raysZeroCleared = true;
    }

    private void ClearOcclIfDirty()
    {
        if (_occlZeroCleared)
        {
            return;
        }

        Clear(_occl);
        _occlZeroCleared = true;
    }

    private void ClearBloomIfDirty()
    {
        if (_bloomZeroCleared)
        {
            return;
        }

        Clear(_bloom[0]);
        _bloomZeroCleared = true;
    }

    private void EnsureTargets(Vector2I size)
    {
        if (Refii.Core.Engine_RdHealth.IsDeviceLost || _rd is null)
        {
            return;
        }

        if (_quarter.IsValid && size == _fullSize)
        {
            return;
        }

        FreeTargets();
        _fullSize = size;
        _quarterSize = new Vector2I(Math.Max(size.X / 4, 1), Math.Max(size.Y / 4, 1));
        int shaftDiv = Math.Max(size.X, size.Y) > 2560 ? 4 : 2;
        _halfSize = new Vector2I(Math.Max(size.X / shaftDiv, 1), Math.Max(size.Y / shaftDiv, 1));

        _quarter = CreateTexture(_quarterSize.X, _quarterSize.Y, RenderingDevice.DataFormat.R16G16B16A16Sfloat);
        _satMask?.Resize(size);
        _occl = CreateTexture(_quarterSize.X, _quarterSize.Y, RenderingDevice.DataFormat.R16G16B16A16Sfloat);
        for (int i = 0; i < 2; i++)
        {
            _rays[i] = CreateTexture(_quarterSize.X, _quarterSize.Y, RenderingDevice.DataFormat.R16G16B16A16Sfloat);
            _bloom[i] = CreateTexture(_quarterSize.X, _quarterSize.Y, RenderingDevice.DataFormat.R16G16B16A16Sfloat);
            _dof[i] = CreateTexture(_quarterSize.X, _quarterSize.Y, RenderingDevice.DataFormat.R16G16B16A16Sfloat);
        }

        bool targetsValid =
            Refii.Core.Engine_RdHealth.CheckRdValid(_quarter, "Engine_PostFxEffect.EnsureTargets.quarter") &&
            Refii.Core.Engine_RdHealth.CheckRdValid(_occl, "Engine_PostFxEffect.EnsureTargets.occl") &&
            Refii.Core.Engine_RdHealth.CheckRdValid(_rays[0], "Engine_PostFxEffect.EnsureTargets.rays0") &&
            Refii.Core.Engine_RdHealth.CheckRdValid(_rays[1], "Engine_PostFxEffect.EnsureTargets.rays1") &&
            Refii.Core.Engine_RdHealth.CheckRdValid(_bloom[0], "Engine_PostFxEffect.EnsureTargets.bloom0") &&
            Refii.Core.Engine_RdHealth.CheckRdValid(_bloom[1], "Engine_PostFxEffect.EnsureTargets.bloom1") &&
            Refii.Core.Engine_RdHealth.CheckRdValid(_dof[0], "Engine_PostFxEffect.EnsureTargets.dof0") &&
            Refii.Core.Engine_RdHealth.CheckRdValid(_dof[1], "Engine_PostFxEffect.EnsureTargets.dof1");
        if (!targetsValid || Refii.Core.Engine_RdHealth.IsDeviceLost)
        {
            FreeTargets();
            _fullSize = default;
            return;
        }

        _shaftHistoryValid = false;
        _raysZeroCleared = true;
        _occlZeroCleared = true;
        _bloomZeroCleared = true;

        if (_trapRatio > 0f)
        {
            for (int i = 0; i < TrapRingDepth; i++)
            {
                _trapRing[i] = CreateTexture(
                    _quarterSize.X, _quarterSize.Y, RenderingDevice.DataFormat.R16G16B16A16Sfloat,
                    readable: true);
                _trapRingFrame[i] = 0;
            }
        }

        Engine_PostFxModel.FixedGaussianWeights(
            _quarterSize.X * SigmaPerQuarterWidth * BloomSigmaScale, _bloomWeightsH);
        Engine_PostFxModel.FixedGaussianWeights(
            _quarterSize.Y * SigmaPerQuarterHeight * BloomSigmaScale, _bloomWeightsV);
        Engine_PostFxModel.FixedGaussianWeights(
            _quarterSize.X * SigmaPerQuarterWidth * DofSigmaScale, _dofWeightsH);
        Engine_PostFxModel.FixedGaussianWeights(
            _quarterSize.Y * SigmaPerQuarterHeight * DofSigmaScale, _dofWeightsV);

        _histogramSet = _rd!.UniformSet(new RDUniform[]
        {
            Image(_quarter, 0),
            Buffer(_histogramBuffer, 1),
        }, _histogramShader, 0);
        _brightcutSet = _rd!.UniformSet(new RDUniform[]
        {
            Image(_quarter, 0),
            Image(_rays[0], 1),
            Image(_occl, 2),
            Buffer(_stateBuffer, 3),
            UniformBlock(_paramsBuffer, 4),
            Image(_bloom[0], 5),
        }, _brightcutShader, 0);
        for (int i = 0; i < 2; i++)
        {
            _radialSets[i] = _rd!.UniformSet(new RDUniform[]
            {
                Sampler(_nearestSampler, _rays[i], 0),
                Image(_rays[1 - i], 1),
                UniformBlock(_paramsBuffer, 2),
            }, _radialShader, 0);
        }

        _gaussBloomHSet = GaussSet(_bloom[0], _bloom[1]);
        _gaussBloomVSet = GaussSet(_bloom[1], _bloom[0]);
        _gaussDofHSet = GaussSet(_dof[1], _dof[0]);
        _gaussDofVSet = GaussSet(_dof[0], _dof[1]);
    }

    private bool EnsureShaftTargets()
    {
        if (Refii.Core.Engine_RdHealth.IsDeviceLost || _rd is null)
        {
            return false;
        }

        if (_shaftHalf[0].IsValid && _shaftHalf[1].IsValid &&
            _shaftDepthHalf[0].IsValid && _shaftDepthHalf[1].IsValid)
        {
            return true;
        }

        for (int i = 0; i < 2; i++)
        {
            _volShaftsSets[i].Release(_rd);
            _volUpsampleSets[i].Release(_rd);
            FreeTexture(ref _shaftHalf[i]);
            FreeTexture(ref _shaftDepthHalf[i]);
            _shaftHalf[i] = CreateTexture(_halfSize.X, _halfSize.Y, RenderingDevice.DataFormat.R16G16B16A16Sfloat);
            _shaftDepthHalf[i] = CreateTexture(_halfSize.X, _halfSize.Y, RenderingDevice.DataFormat.R16Sfloat);
            if (!Refii.Core.Engine_RdHealth.CheckRdValid(_shaftHalf[i], "Engine_PostFxEffect.EnsureShaftTargets.half") ||
                !Refii.Core.Engine_RdHealth.CheckRdValid(_shaftDepthHalf[i], "Engine_PostFxEffect.EnsureShaftTargets.depth"))
            {
                _shaftHistoryValid = false;
                return false;
            }
        }

        _shaftHistoryValid = false;
        return _shaftHalf[0].IsValid && _shaftHalf[1].IsValid &&
            _shaftDepthHalf[0].IsValid && _shaftDepthHalf[1].IsValid;
    }

    private Rid GaussSet(Rid src, Rid dst) => _rd!.UniformSet(new RDUniform[]
    {
        Image(src, 0),
        Image(dst, 1),
    }, _gaussShader, 0);

    private void FreeTargets()
    {
        FreeClassicTargets();
        FreeSet(ref _histogramSet);
        FreeSet(ref _brightcutSet);
        for (int i = 0; i < 2; i++)
        {
            FreeSet(ref _radialSets[i]);
        }

        FreeSet(ref _gaussBloomHSet);
        FreeSet(ref _gaussBloomVSet);
        FreeSet(ref _gaussDofHSet);
        FreeSet(ref _gaussDofVSet);
        if (_rd is not null)
        {
            _downscale4Set.Release(_rd);
            _dofDownscaleSet.Release(_rd);
            _compositeSet.Release(_rd);
            _sunlayerSet.Release(_rd);
            _addlayerSet.Release(_rd);
            _halolayerSet.Release(_rd);
            _glowHSet.Release(_rd);
            _glowVSet.Release(_rd);
            _highlightPrepSet.Release(_rd);
            _highlightCompositeSet.Release(_rd);
            for (int i = 0; i < 2; i++)
            {
                _volShaftsSets[i].Release(_rd);
                _volUpsampleSets[i].Release(_rd);
            }
        }

        for (int i = 0; i < TrapRingDepth; i++)
        {
            FreeSet(ref _trapRingSets[i]);
            FreeTexture(ref _trapRing[i]);
        }

        _trapRingColour = default;
        FreeTexture(ref _staging);
        FreeTexture(ref _quarter);
        FreeTexture(ref _occl);
        for (int i = 0; i < 2; i++)
        {
            FreeTexture(ref _rays[i]);
            FreeTexture(ref _bloom[i]);
            FreeTexture(ref _dof[i]);
            FreeTexture(ref _glow[i]);
            FreeTexture(ref _shaftHalf[i]);
            FreeTexture(ref _shaftDepthHalf[i]);
        }

        _shaftHistoryValid = false;
        _raysZeroCleared = true;
        _occlZeroCleared = true;
        _bloomZeroCleared = true;

        _glowSize = default;
        _glowLayer = default;
    }

    private void FreeSet(ref Rid set)
    {
        if (set.IsValid && _rd is not null && _rd.UniformSetIsValid(set))
        {
            _rd.FreeRid(set);
        }

        set = default;
    }

    private void FreeTexture(ref Rid rid)
    {
        if (rid.IsValid && _rd is not null && _rd.TextureIsValid(rid))
        {
            _rd.FreeRid(rid);
        }

        rid = default;
    }

    private void Free(ref Rid rid)
    {
        if (rid.IsValid && _rd is not null)
        {
            _rd.FreeRid(rid);
        }

        rid = default;
    }

    public override void _Notification(int what)
    {
        if (what != NotificationPredelete)
        {
            return;
        }

        // Fallback if caller did not invoke ReleaseNow() before SceneTree shutdown; note that at
        // Quit() time NotificationPredelete can fire after the render thread stops draining CallOnRenderThread.
        ReleaseNow(releaseStaticFallbacks: false);
    }

    /// <summary>
    /// Detaches published globals and frees RD resources deterministically while the render thread is alive.
    /// </summary>
    public void ReleaseNow(bool releaseStaticFallbacks = false)
    {
        if (_disposed && !releaseStaticFallbacks)
        {
            return;
        }

        _disposed = true;
        _ready = false;
        Engine_PostFxRoute.Withdraw(this);

        if (View is >= 0 and < 2 && ReferenceEquals(_publishers[View], this))
        {
            _publishers[View] = null;
            if (View == 0)
            {
                RenderingServer.GlobalShaderParameterSet(GlobalExposureValidName, 0.0f);
            }

            if (!releaseStaticFallbacks && _placeholderTexture is not null && GodotObject.IsInstanceValid(_placeholderTexture))
            {
                RenderingServer.GlobalShaderParameterSet(ExposureGlobal, _placeholderTexture);
            }
            else
            {
                RenderingServer.GlobalShaderParameterSet(ExposureGlobal, default(Variant));
            }
        }

        if (releaseStaticFallbacks)
        {
            if (View == 0)
            {
                RenderingServer.GlobalShaderParameterSet(GlobalExposureValidName, 0.0f);
                RenderingServer.GlobalShaderParameterSet(GlobalExposureName, default(Variant));
                RenderingServer.GlobalShaderParameterSet(GlobalExposureSecondName, default(Variant));
            }
            if (_placeholderTexture is not null && GodotObject.IsInstanceValid(_placeholderTexture))
            {
                _placeholderTexture.Dispose();
            }
            _placeholderTexture = null;
            _globalsRegistered = false;
        }

        WithdrawClassicSky();

        if (_exposureTexture is not null)
        {
            Refii.Core.Engine_RdHealth.ClearTextureRdRidSafely(_exposureTexture);
            _exposureTexture = null;
        }

        // Schedule ReleaseDeviceObjects() on the render thread even if _rd still looks null on the
        // main thread (Initialise() may still be queued ahead of us in CommandQueueMT).
        if (RenderingServer.IsOnRenderThread())
        {
            ReleaseDeviceObjects();
            return;
        }

        Engine_PostFxEffect self = this;
        RenderingServer.CallOnRenderThread(Callable.From(() => self.ReleaseDeviceObjects()));
        if (!releaseStaticFallbacks)
        {
            RenderingServer.ForceSync();
        }
    }

    private void ReleaseDeviceObjects()
    {
        RenderingDevice? rd = RenderingServer.GetRenderingDevice();
        if (_rd is null || rd is null)
        {
            _rd = null;
            return;
        }

        FreeClassicPost();
        FreeTargets();
        FreeSet(ref _reduceSet);
        FreeTexture(ref _exposureRd);
        Free(ref _nearestSampler);
        Free(ref _linearSampler);
        FreeTexture(ref _haloFallbackArray);
        Free(ref _haloBuffer);
        FreeTexture(ref _shadowFallbackArray);
        Free(ref _shaftBuffer);
        Free(ref _highlightBuffer);
        Free(ref _paramsBuffer);
        Free(ref _stateBuffer);
        Free(ref _trapUnitState);
        Free(ref _histogramBuffer);
        Free(ref _compositePipeline);
        Free(ref _gaussPipeline);
        Free(ref _dofDownscalePipeline);
        Free(ref _brightcutPipeline);
        Free(ref _radialPipeline);
        Free(ref _sunlayerPipeline);
        Free(ref _addlayerPipeline);
        Free(ref _highlightPipeline);
        Free(ref _highlightPrepPipeline);
        Free(ref _highlightCompositePipeline);
        Free(ref _halolayerPipeline);
        Free(ref _volShaftsPipeline);
        Free(ref _volUpsamplePipeline);
        Free(ref _downscale4Pipeline);
        Free(ref _histogramPipeline);
        Free(ref _reducePipeline);
        Free(ref _compositeShader);
        _satMask?.Release();
        _satMask = null;
        Free(ref _gaussShader);
        Free(ref _dofDownscaleShader);
        Free(ref _brightcutShader);
        Free(ref _radialShader);
        Free(ref _sunlayerShader);
        Free(ref _addlayerShader);
        Free(ref _highlightShader);
        Free(ref _highlightPrepShader);
        Free(ref _highlightCompositeShader);
        Free(ref _halolayerShader);
        Free(ref _volShaftsShader);
        Free(ref _volUpsampleShader);
        Free(ref _downscale4Shader);
        Free(ref _histogramShader);
        Free(ref _reduceShader);
        _rd = null;
    }

    /// <summary>Captures actual buffer extents and runs compatible post effects on the render thread.</summary>
    /// <param name="effectCallbackType">Godot's compositor stage.</param>
    /// <param name="renderData">Ephemeral native data, never retained outside this callback.</param>
    public override void _RenderCallback(int effectCallbackType, RenderData renderData)
    {
        if (Refii.Core.Engine_RdHealth.IsDeviceLost || !_ready || _rd is null)
        {
            return;
        }

        if (renderData.GetRenderSceneBuffers() is not RenderSceneBuffersRD buffers)
        {
            return;
        }

        Vector2I size = buffers.GetInternalSize();
        Vector2I target = buffers.GetTargetSize();
        _renderBuffers.Publish(size.X, size.Y, target.X, target.Y, buffers.GetViewCount(), DateTime.UtcNow);
        if (size.X <= 0 || size.Y <= 0)
        {
            return;
        }

        if (View is >= 0 and < 2 && !ReferenceEquals(System.Threading.Volatile.Read(ref _publishers[View]), this))
        {
            ClaimExposure();
        }

        if (!Engine_PostFxParams.Enabled ||
            !Refii.Game.GameWorld.ShouldRunRenderEffects(
                Refii.Game.GameWorld.HasActiveWorld,
                Refii.Game.GameWorld.CoverHeld,
                Refii.Game.GameWorld.ReadyForWarmupAndSettle,
                Refii.Game.GameWorld.SettleFrames,
                requiredSettleFrames: RequiredSettleFrames))
        {
            WriteDisabledMaterialExposure();
            return;
        }

        uint views = buffers.GetViewCount();
        if (views == 0 || !ColourLayerUsable(buffers))
        {
            WriteDisabledMaterialExposure();
            return;
        }

        _materialDisabledWritten = false;
        ++_frameIndex;
        float dt = Elapsed();
        Engine_PostFxParams.Snapshot(_derived);
        EnsureTargets(size);
        if (Refii.Core.Engine_RdHealth.IsDeviceLost || !_quarter.IsValid)
        {
            WriteDisabledMaterialExposure();
            return;
        }
        Engine_PostFxSunSources.Snapshot(out Engine_PostFxSunSources.Sources sun);

        bool bloomOn = Engine_PostFxParams.Bloom;
        bool raysOn = Engine_PostFxParams.Rays;
        bool dofOn = Engine_PostFxParams.DofAllowed && (_derived.DofNearBlur > 0.0f || _derived.DofFarBlur > 0.0f);

        RenderSceneData? sceneData = renderData.GetRenderSceneData();
        Basis camBasis = Basis.Identity;
        Vector3 camOrigin = Vector3.Zero;
        Projection camProjection = Projection.Identity;
        if (sceneData is not null)
        {
            Transform3D camTransform = sceneData.GetCamTransform();
            camBasis = camTransform.Basis;
            camOrigin = camTransform.Origin;
            camProjection = sceneData.GetCamProjection();
        }

        // `Render_LitMaterialSubmit` takes the SaturationMask variant only when the theme states a
        // masked pair (|SaturationMasked| or |BrightnessMasked| over 0.01, 0x821e5e60).
        _maskWanted = _satMask is not null && Refii.Entities.Engine_EntityAppearance.MaskedCount > 0 &&
            Engine_PostFxParams.RequiresSaturationMask(_derived.SaturationMasked, _derived.BrightnessMasked);
        UpdateParams(dt, sun.SunDirection, camBasis, camProjection, bloomOn, raysOn, dofOn);
        _frameProjection = camProjection;
        _frameDof = dofOn;
        Reduce();
        _rd.BufferClear(_histogramBuffer, 0, HistogramBytes);

        bool staged = _colourRoute == Engine_PostFxModel.ColourRoute.Staged;
        if (staged && !_staging.IsValid)
        {
            _staging = CreateTexture(size.X, size.Y, RenderingDevice.DataFormat.R16G16B16A16Sfloat, readable: true);
            if (!_staging.IsValid)
            {
                WriteDisabledMaterialExposure();
                return;
            }
        }

        for (uint view = 0; view < views; view++)
        {
            Rid sceneColour = buffers.GetColorLayer(view);
            Rid colour = sceneColour;
            if (staged)
            {
                Rid msaaColour = buffers.GetColorLayer(view, true);
                if (!msaaColour.IsValid) continue;
                _rd.TextureResolveMultisample(msaaColour, _staging);
                colour = _staging;
            }

            Rid depth = buffers.GetDepthLayer(view);
            if (!colour.IsValid || !depth.IsValid) continue;
            Projection projection = sceneData is not null ? sceneData.GetViewProjection(view) : camProjection;
            bool sunOnScreen = TrySunScreenUv(
                sun.SunDirection, camBasis, projection, out Vector2 sunUv);

            // The targeting highlight joins first. `sub_821B6B70` is slot 4 of the "Outline
            // Layer" (vtable 0x82001F74, installed by the main-scene pass `sub_82A860B8` at
            // 0x82a86af4), so it composites into the HDR scene target itself — ahead of bloom's
            // downscale and the tonemap, and ahead of the additive layer, which the guest only
            // adds in those two reads. Its blend is not an add, so that order shows. The
            // guest's own sequence is the silhouette into a half-res buffer, the two bleed passes
            // (0x82197c98, 0x82197cac), then the outline composite in `sub_821B6B70`. Nothing
            // runs when nothing is targeted or every target is off screen, which is the guest
            // bailing on an empty list or an empty rect union at 0x82197ba8 / 0x82197bb4.
            if (Refii.Entities.Engine_EntityHighlightLayer.TrySnapshot(
                    View, _rd, out Rid layer, out float scale, _highlightTargets) &&
                HighlightLayerLive(layer) &&
                EnsureGlow(layer) &&
                HighlightTargets(camBasis, camOrigin, projection))
            {
                BeginBatch();
                HighlightPrep(layer);
                HighlightBlur(_glowHSet, _glow[0], _glow[1], new Vector2I(1, 0), scale);
                HighlightBlur(_glowVSet, _glow[1], _glow[0], new Vector2I(0, 1), scale);
                HighlightComposite(colour, depth, layer, size, projection);
            }

            // The additive effects layer joins here, before anything reads the colour: the
            // guest's bucket-5 surface (off_8331AA14) is added to the scene in the bloom
            // downscale (0162) and in the tonemap (0509) — one sum, consumed by both, which
            // adding it into the colour buffer once reproduces for the whole chain.
            if (Refii.Fx.Engine_FxAdditiveLayer.TrySnapshot(View, _rd, out Rid additive) &&
                AdditiveLayerLive(additive))
            {
                BeginBatch();
                AddLayer(colour, size, additive);
            }

            VolumetricShafts(colour, depth, size, camBasis, camOrigin, projection);

            BeginBatch();
            Downscale4(colour, size);

            // THE RAYS ARE A RADIAL BLUR OF THE SUN LAYER — not of the scene, and not of a
            // synthesized beams billboard. Decoded from the frame segment (sub_821CB750): the
            // guest draws the depth-tested sun/moon discs into their own half-res [0,1] UNORM
            // layer (off_8331A9A8), DOWNSCALE2Xes it and runs RADIALBLUR on it TWICE, and that
            // buffer feeds the bright pass. SunLayer is the layer's content with the guest's
            // own per-pixel Z test, so a hill in front of the sun kills the rays exactly as
            // far as it covers.
            // Under classic the layer holds what the title draws there, the SUN BEAMS (PS 0121,
            // `sub_821CB750` 0x821CB958), and the glow layer takes the moon bloom and the sun disc
            // (Engine_ClassicSkyLayers).
            bool classicSky = Refii.Models.Engine_ClassicMaterial.Active && Engine_ClassicSkyLayers.Snapshot(_skyFrame);
            if (classicSky)
            {
                EndBatch();
                ClearRaysIfDirty();
                if (raysOn && ClassicBeams(depth, camBasis, camOrigin, projection, out Vector2 beamsUv))
                {
                    _raysZeroCleared = false;
                    Radial(0, beamsUv);
                    Radial(1, beamsUv);
                }
            }
            else if (raysOn && sunOnScreen && sun.Disc.Active && sun.Disc.TextureRid.IsValid &&
                RenderingServer.TextureGetRdTexture(sun.Disc.TextureRid) is var discRd && discRd.IsValid)
            {
                _raysZeroCleared = false;
                BeginBatch();
                SunLayer(depth, sunUv, sun.Disc, discRd, camBasis, camOrigin, projection);
                Radial(0, sunUv);
                Radial(1, sunUv);
            }
            else
            {
                ClearRaysIfDirty();
            }

            // The GLOW LAYER: every stated halo, rasterised with the scene depth in hand. Its
            // only consumer is the brightcut (the guest's off_8331A9CC feeds nothing else), so
            // with bloom off it need not exist at all.
            if (!bloomOn || !HaloLayer(depth, camBasis, camOrigin, projection))
            {
                ClearOcclIfDirty();
            }
            if (Refii.Models.Engine_ClassicMaterial.Active)
            {
                EndBatch();
                ClassicDecals(depth, camBasis, camOrigin, projection);
                _occlZeroCleared = false;
            }
            if (classicSky)
            {
                // Drawn even with bloom off: the sun disc's and moon bloom's pixels are the
                // glares' occlusion queries.
                EndBatch();
                ClassicGlow(depth, camBasis, camOrigin, projection);
                _occlZeroCleared = false;
            }

            if (bloomOn)
            {
                _bloomZeroCleared = false;
                BeginBatch();
                Brightcut();
                Gauss(_gaussBloomHSet, _bloom[0], _bloom[1], new Vector2I(1, 0), _bloomWeightsH);
                Gauss(_gaussBloomVSet, _bloom[1], _bloom[0], new Vector2I(0, 1), _bloomWeightsV);
            }
            else
            {
                ClearBloomIfDirty();
            }

            if (dofOn)
            {
                BeginBatch();
                if (!ClassicPost)
                {
                    DofDownscale(colour, depth, size, projection.Inverse());
                }
                Gauss(_gaussDofHSet, _quarter, _dof[0], new Vector2I(1, 0), _dofWeightsH);
                Gauss(_gaussDofVSet, _dof[0], _dof[1], new Vector2I(0, 1), _dofWeightsV);
            }

            if (_maskWanted)
            {
                EndBatch();
                if (!_satMask!.Build(depth))
                {
                    Clear(_satMask.Texture);
                }
            }

            BeginBatch();
            Composite(colour, depth, size, projection.Inverse());
            Histogram();
            if (_trapRatio > 0f && view == 0)
            {
                TrapCapture(colour, size);
            }

            EndBatch();
            if (staged)
            {
                _rd.TextureCopy(
                    _staging, sceneColour, Vector3.Zero, Vector3.Zero,
                    new Vector3(size.X, size.Y, 1), 0, 0, 0, 0);
            }
        }

        _firstFrame = false;
        _historyResetSeen = _historyResetGeneration;
        _histogramValid = true;

        if (_trapRatio > 0f)
        {
            TrapPoll();
        }

        if (_dumpPeriod > 0 && _frameIndex % _dumpPeriod == 0)
        {
            Dump(size, dt);
        }
    }

    /// <summary>The route and bounded diagnostic for a newly inspected scene-color image.</summary>
    /// <param name="Route">Direct storage, MSAA staging, or neutral-exposure fallback.</param>
    /// <param name="ReportRefusal">This effect has not yet reported an incompatible image.</param>
    internal readonly record struct ColourLayerDecision(Engine_PostFxModel.ColourRoute Route, bool ReportRefusal)
    {
        /// <summary>Whether either direct dispatch or the MSAA staging path is permitted.</summary>
        internal bool Usable => Route != Engine_PostFxModel.ColourRoute.Unusable;
    }

    /// <summary>Caches actual image routes and bounds fallback logging across buffer replacements.</summary>
    /// <remarks>Both scene and MSAA RID identities participate in the cache. Mobile attachments
    /// without storage or a valid resolve/copy-back path keep neutral exposure. Missing images
    /// must never trigger native format queries or reuse a previously permitted route.</remarks>
    internal sealed class ColourLayerContract
    {
        private ulong _colourId;
        private ulong _msaaId;
        private bool _haveColour;
        private Engine_PostFxModel.ColourRoute _route = Engine_PostFxModel.ColourRoute.Unusable;
        private bool _reportedRefusal;

        /// <summary>Returns dispatch permission for a cached non-MSAA image.</summary>
        /// <param name="colourId">Renderer RID identity, or zero for no image.</param>
        /// <param name="usable">Whether the cached route allows dispatch.</param>
        /// <returns>True when inspection is unnecessary.</returns>
        internal bool TryGet(ulong colourId, out bool usable)
        {
            bool cached = TryGetRoute(colourId, 0, out var route);
            usable = route != Engine_PostFxModel.ColourRoute.Unusable;
            return cached;
        }

        /// <summary>Returns a cached route for both renderer images, explicitly refusing an absent scene image.</summary>
        /// <param name="colourId">Scene image identity, or zero when absent.</param>
        /// <param name="msaaId">Multisampled image identity, or zero when absent/disabled.</param>
        /// <param name="route">The cached route, or Unusable when absent or not cached.</param>
        /// <returns>True when no native inspection is needed.</returns>
        internal bool TryGetRoute(ulong colourId, ulong msaaId, out Engine_PostFxModel.ColourRoute route)
        {
            route = Engine_PostFxModel.ColourRoute.Unusable;
            if (colourId == 0) return true;
            if (!_haveColour || _colourId != colourId || _msaaId != msaaId) return false;
            route = _route;
            return true;
        }

        /// <summary>Records actual format/usage flags and requests at most one refusal diagnostic per lifetime.</summary>
        /// <param name="colourId">Scene image identity.</param>
        /// <param name="format">Actual scene image format.</param>
        /// <param name="usage">Actual scene image creation usage.</param>
        /// <param name="msaaUsage">Actual MSAA usage, or null without a valid source.</param>
        /// <param name="msaaId">MSAA source identity, or zero without a valid source.</param>
        /// <returns>The route and bounded diagnostic request.</returns>
        internal ColourLayerDecision Inspect(ulong colourId, RenderingDevice.DataFormat format,
            RenderingDevice.TextureUsageBits usage, RenderingDevice.TextureUsageBits? msaaUsage = null,
            ulong msaaId = 0)
        {
            if (colourId == 0) return new(Engine_PostFxModel.ColourRoute.Unusable, false);
            _colourId = colourId;
            _msaaId = msaaId;
            _haveColour = true;
            _route = Engine_PostFxModel.ChooseColourRoute(format, usage, msaaId != 0 ? msaaUsage : null);
            bool report = _route == Engine_PostFxModel.ColourRoute.Unusable && !_reportedRefusal;
            _reportedRefusal |= report;
            return new(_route, report);
        }
    }

    /// <summary>Checks direct/staged scene-color eligibility before allocating compute targets.</summary>
    /// <param name="buffers">Current scene and MSAA images.</param>
    /// <returns>Whether dispatch is permitted; otherwise the callback writes neutral exposure.</returns>
    private bool ColourLayerUsable(RenderSceneBuffersRD buffers)
    {
        Rid colour = buffers.GetColorLayer(0);
        Rid msaa = colour.IsValid && buffers.GetMsaa3D() != RenderingServer.ViewportMsaa.Disabled
            ? buffers.GetColorLayer(0, true) : default;
        if (_colourContract.TryGetRoute(colour.Id, msaa.Id, out var cached))
        {
            _colourRoute = cached;
            PublishRoute();
            return cached != Engine_PostFxModel.ColourRoute.Unusable;
        }

        RDTextureFormat image = _rd!.TextureGetFormat(colour);
        RenderingDevice.TextureUsageBits? msaaUsage = msaa.IsValid
            ? _rd.TextureGetFormat(msaa).UsageBits : null;
        ColourLayerDecision decision = _colourContract.Inspect(
            colour.Id, image.Format, image.UsageBits, msaaUsage, msaa.Id);
        _colourRoute = decision.Route;
        PublishRoute();
        // Buffer replacements can change dimensions; staging belongs only to those images.
        Free(ref _staging);
        if (decision.ReportRefusal)
            GD.Print($"[postfx] colour layer is {image.Format}, usage {image.UsageBits}; " +
                "no storage or MSAA resolve/copy-back route; using neutral exposure");
        return decision.Usable;
    }

    /// <summary>View 0's route, for the layers only this chain brings to the screen
    /// (<see cref="Engine_PostFxRoute"/>).</summary>
    private void PublishRoute()
    {
        if (View == 0)
        {
            Engine_PostFxRoute.Publish(this, _colourRoute);
        }
    }

    /// <summary>
    /// True only when the sun is IN FRONT of the camera. ProjectSun's failure value is the
    /// screen centre, so an unguarded caller paints a behind-the-camera sun mid-frame.
    /// </summary>
    private static bool TrySunScreenUv(
        Vector3 direction, Basis basis, Projection projection, out Vector2 uv)
    {
        float px = MathF.Abs(projection.X.X);
        float py = MathF.Abs(projection.Y.Y);
        float tanHalfX = px > 1e-8f ? 1.0f / px : 0.0f;
        float tanHalfY = py > 1e-8f ? 1.0f / py : 0.0f;
        return Engine_PostFxParams.ProjectSun(
            direction, -basis.Z, basis.X, basis.Y, tanHalfX, tanHalfY, out uv, out _);
    }

    private void WriteDisabledMaterialExposure()
    {
        if (_materialDisabledWritten)
        {
            return;
        }

        _materialDisabledWritten = true;
        _rd!.TextureUpdate(_exposureRd, 0, BitConverter.GetBytes(DisabledMaterialExposure));
    }

    private float Elapsed()
    {
        double now = Time.GetTicksUsec() / 1_000_000.0;
        float dt = _measuredAny ? (float)(now - _lastFrameSeconds) : 0.0f;
        _lastFrameSeconds = now;
        _measuredAny = true;
        return dt;
    }

    private void UpdateParams(
        float dt, Vector3 sunDirection, Basis camBasis, Projection camProjection,
        bool bloomOn, bool raysOn, bool dofOn)
    {
        Engine_PostFxModel.Derived d = _derived;
        float[] p = _paramsScratch;
        System.Array.Clear(p, 0, p.Length);

        float lockValue = Engine_PostFxParams.ExposureLock
            ? Math.Clamp(Engine_PostFxParams.ExposureLockValue, Engine_PostFxModel.ExposureMin, Engine_PostFxModel.ExposureMax)
            : 0.0f;
        bool snap = _firstFrame || _historyResetSeen != _historyResetGeneration;
        Set(p, 0, dt, snap ? 1.0f : 0.0f, _histogramValid ? 1.0f : 0.0f, lockValue);
        Set(p, 1, d.ExposureSpeed, d.BlackMin, d.MidMin, d.WhiteMin);
        Set(p, 2, d.Pct[0], d.Pct[1], d.Pct[2], d.Knee);
        Set(p, 3, d.ContrastEnhancement, d.ContrastExponent, d.ContrastMaxDeviation, d.WhiteFactor);
        // The classic look is the title's chain: its own operator (PSHADER_POSTPROCESSINGMATERIAL
        // _TONEMAP1) on a scene the materials already exposed, and none of the remaster's lift.
        bool classic = Refii.Models.Engine_ClassicMaterial.Active;
        // `bloom_scale` (BloomGain) rides the BRIGHT term alone, here in bloom.y, not the bloom
        // buffer in the composite: the glow layer (sun and moon discs, fixture halos) joins that
        // same buffer unscaled in brightcut, so at 0 the scene's bloom is gone and the sun still
        // glows. The composite's 2x is the title's rescale and nothing else.
        Set(p, 4,
            d.BloomThresholdAvg,
            classic ? d.BloomFactor : d.BloomFactor * Engine_PostFxParams.BloomGain,
            raysOn ? 6.0f * d.RadialFactor * Engine_PostFxParams.RayGain : 0.0f,
            d.TonemapEnabled ? 1.0f : 0.0f);
        TrySunScreenUv(sunDirection, camBasis, camProjection, out Vector2 sunUv);
        Set(p, 5, sunUv.X, sunUv.Y, d.RadialFalloff, d.RadialMagnification);
        Set(p, 6,
            1.0f / MathF.Max(d.DofNearFocal - d.DofNearPlane, 1e-4f),
            d.DofNearFocal,
            d.DofFarFocal,
            1.0f / MathF.Max(d.DofFarPlane - d.DofFarFocal, 1e-4f));
        bool fableStyle = !classic && Engine_PostFxParams.FableStyleTonemap;
        Set(p, 7, d.DofFarBlur, d.DofNearBlur, dofOn ? 1.0f : 0.0f, classic ? 1.0f : Engine_PostFxParams.DarkLift);
        Set(p, 13, Engine_PostFxParams.DarkLiftOnsetNow, Engine_PostFxParams.FilmicCurve ? 1.0f : 0.0f,
            Engine_PostFxParams.OutputHeadroom, fableStyle ? 1.0f : 0.0f);
        Set(p, 14, (float)_frameIndex, _trapRatio, classic ? 1.0f : 0.0f, _maskWanted ? 1.0f : 0.0f);
        Set(p, 8, d.Saturation, d.Brightness + 1.0f, d.SaturationMasked, d.BrightnessMasked);

        float gammaRatio = d.Gamma > 0.0f && MathF.Abs(d.Gamma - 1.0f) > 1e-4f ? d.Gamma : 0.0f;
        Set(p, 9,
            1.0f / MathF.Max(d.ColourSensitivity, 1e-4f),
            gammaRatio,
            Math.Clamp(1.0f - d.Darkening, 0.0f, 1.0f),
            1.0f / Engine_PostFxModel.Range);
        Set(p, 10,
            d.TonemapEnabled ? 1.0f : 0.0f,
            dofOn ? 1.0f : 0.0f,
            Engine_PostFxParams.Saturation ? 1.0f : 0.0f,
            bloomOn ? 1.0f : 0.0f);

        Projection inverse = camProjection.Inverse();
        Set(p, 11, inverse.Z.Z, inverse.W.Z, inverse.Z.W, inverse.W.W);
        // c31.x for the title's operator is 2 x TONE_MAPPING_BLOOM_FACTOR (`Render_LitMaterialSubmit_INFERRED`
        // 0x821E5FC0: lfs P+0x4C; fmuls by 2.0) to undo the 0.5 = 1/g_BloomRescaleFactor in brightcut.
        // Both looks: `bloom_scale` is already in the bright term (slot 4.y above).
        float bloomScale = 2.0f * d.BloomFactor;
        Set(p, 12,
            bloomScale,
            d.RadialFactor,
            Engine_PostFxParams.MaterialExposureFollowsApplied ? 1.0f : 0.0f,
            Engine_PostFxParams.BloomThresholdScale);

        System.Buffer.BlockCopy(p, 0, _paramsBytes, 0, ParamsBytes);
        _rd!.BufferUpdate(_paramsBuffer, 0, ParamsBytes, _paramsBytes);
    }

    private static void Set(float[] p, int slot, float x, float y, float z, float w)
    {
        int at = slot * 4;
        p[at] = x;
        p[at + 1] = y;
        p[at + 2] = z;
        p[at + 3] = w;
    }

    private void Reduce()
    {
        Dispatch(_reducePipeline, _reduceSet, null, 1, 1);
    }

    private void VolumetricShafts(
        Rid colour, Rid depth, Vector2I size, Basis camBasis, Vector3 camOrigin, Projection projection)
    {
        long frame = Engine.GetFramesDrawn();
        if (_shaftFrame < 0 || frame - _shaftFrame > 1)
        {
            _shaftHistoryValid = false;
        }

        _shaftFrame = frame;
        if (Engine_PostFxParams.VolumetricShaftScale <= 1e-4f ||
            !_volShaftsPipeline.IsValid || !_volUpsamplePipeline.IsValid ||
            !Engine_VolumetricLightShafts.TrySnapshot(
                _shaftLightScratch,
                out int lightCount,
                _shaftMistScratch,
                out int mistCount,
                _shaftCapsuleScratch,
                out int capsuleCount,
                out Vector3 medScattering,
                out float medExtinction,
                out float medAnisotropy,
                out float fogStart) ||
            !EnsureShaftTargets())
        {
            _shaftHistoryValid = false;
            return;
        }

        float px = MathF.Abs(projection.X.X);
        float py = MathF.Abs(projection.Y.Y);
        if (px <= 1e-8f || py <= 1e-8f)
        {
            _shaftHistoryValid = false;
            return;
        }

        RenderingDevice rd = _rd!;
        Rid shadowAtlas = Engine_LevelShadowMaps.ActiveAtlasRdRid;
        int atlasRes = Engine_LevelShadowMaps.ActiveAtlasSize;
        if (!shadowAtlas.IsValid || !rd.TextureIsValid(shadowAtlas))
        {
            shadowAtlas = _shadowFallbackArray;
            atlasRes = 4;
        }

        Vector3 forward = (-camBasis.Z).Normalized();
        Vector3 right = camBasis.X.Normalized();
        Vector3 up = camBasis.Y.Normalized();
        Projection inverse = projection.Inverse();
        float invAtlasSize = 1.0f / Math.Max(atlasRes, 1);

        float[] s = _shaftScratch;
        Set(s, 0, camOrigin.X, camOrigin.Y, camOrigin.Z, fogStart);
        Set(s, 1, forward.X, forward.Y, forward.Z, px);
        Set(s, 2, right.X, right.Y, right.Z, py);
        Set(s, 3, up.X, up.Y, up.Z, medExtinction);
        Set(s, 4, medScattering.X, medScattering.Y, medScattering.Z, medAnisotropy);
        Set(s, 5, _prevShaftCamOrigin.X, _prevShaftCamOrigin.Y, _prevShaftCamOrigin.Z, _shaftHistoryValid ? 1.0f : 0.0f);
        Set(s, 6, _prevShaftForward.X, _prevShaftForward.Y, _prevShaftForward.Z, _prevShaftPx);
        Set(s, 7, _prevShaftRight.X, _prevShaftRight.Y, _prevShaftRight.Z, _prevShaftPy);
        Set(s, 8, _prevShaftUp.X, _prevShaftUp.Y, _prevShaftUp.Z, invAtlasSize);
        Set(s, 9, inverse.Z.Z, inverse.W.Z, inverse.Z.W, inverse.W.W);

        const int baseLights = 10;
        for (int i = 0; i < lightCount; i++)
        {
            ref readonly Engine_VolumetricLightShafts.ShaftLightInput l = ref _shaftLightScratch[i];
            float fluxScale = l.Energy * l.FogEnergyScale;
            float nodeRange = MathF.Max(l.NodeRange, 0.01f);
            float statedRange = MathF.Max(l.StatedRange, 0.01f);
            float invStatedR2 = 1.0f / (statedRange * statedRange);
            int slot = baseLights + (i * 5);
            Set(s, slot + 0, l.Position.X, l.Position.Y, l.Position.Z, nodeRange);
            Set(s, slot + 1,
                l.LinearColor.X * fluxScale,
                l.LinearColor.Y * fluxScale,
                l.LinearColor.Z * fluxScale,
                l.SpotCosHalfAngle);
            Set(s, slot + 2, l.AtlasRect.X, l.AtlasRect.Y, l.AtlasRect.Z, l.AtlasRect.W);
            Set(s, slot + 3, l.SpotDir.X, l.SpotDir.Y, l.SpotDir.Z, MathF.Max(l.SoftCoreRadius, 0.0f));
            Set(s, slot + 4,
                l.IndoorFromOutside ? 1.0f : 0.0f,
                l.CasterMask & 0xFFFFu,
                l.CasterMask >> 16,
                invStatedR2);
        }

        const int baseMists = 330;
        for (int m = 0; m < mistCount; m++)
        {
            ref readonly Engine_VolumetricLightShafts.MistVolumeSnapshot mv = ref _shaftMistScratch[m];
            int slot = baseMists + (m * 4);
            Set(s, slot + 0, mv.Min.X, mv.Min.Y, mv.Min.Z, mv.Extinction);
            Set(s, slot + 1, mv.Max.X, mv.Max.Y, mv.Max.Z, mv.Flatten);
            Set(s, slot + 2, mv.LinearAlbedo.X, mv.LinearAlbedo.Y, mv.LinearAlbedo.Z, 0.0f);
            Set(s, slot + 3,
                mv.NoiseScaleOffset.X,
                mv.NoiseScaleOffset.Y,
                mv.NoiseScaleOffset.Z,
                mv.NoiseScaleOffset.W);
        }

        const int baseCapsules = 394;
        for (int c = 0; c < capsuleCount; c++)
        {
            ref readonly Engine_VolumetricLightShafts.VolumetricCapsule cap = ref _shaftCapsuleScratch[c];
            int slot = baseCapsules + (c * 2);
            Set(s, slot + 0, cap.SegA.X, cap.SegA.Y, cap.SegA.Z, cap.Radius);
            Set(s, slot + 1, cap.SegB.X, cap.SegB.Y, cap.SegB.Z, cap.Feather);
        }

        System.Buffer.BlockCopy(s, 0, _shaftBytes, 0, ShaftBufferBytes);
        EndBatch();
        rd.BufferUpdate(_shaftBuffer, 0, ShaftBufferBytes, _shaftBytes);
        BeginBatch();

        int currIdx = _shaftPingPong & 1;
        int prevIdx = 1 - currIdx;
        _shaftPingPong ^= 1;

        KeyedSet shaftSet = _volShaftsSets[currIdx];
        if (!shaftSet.Matches(rd, depth, shadowAtlas))
        {
            shaftSet.Replace(rd, depth, shadowAtlas, _volShaftsShader, new[]
            {
                Sampler(_nearestSampler, depth, 0),
                Sampler(_linearSampler, shadowAtlas, 1),
                Sampler(_linearSampler, _shaftHalf[prevIdx], 2),
                Sampler(_nearestSampler, _shaftDepthHalf[prevIdx], 3),
                Image(_shaftHalf[currIdx], 4),
                Image(_shaftDepthHalf[currIdx], 5),
                Buffer(_shaftBuffer, 6),
            });
        }

        Span<byte> pushShaft = _pushShafts;
        BitConverter.TryWriteBytes(pushShaft.Slice(0, 4), _halfSize.X);
        BitConverter.TryWriteBytes(pushShaft.Slice(4, 4), _halfSize.Y);
        BitConverter.TryWriteBytes(pushShaft.Slice(8, 4), size.X);
        BitConverter.TryWriteBytes(pushShaft.Slice(12, 4), size.Y);
        BitConverter.TryWriteBytes(pushShaft.Slice(16, 4), lightCount);
        BitConverter.TryWriteBytes(pushShaft.Slice(20, 4), mistCount);
        BitConverter.TryWriteBytes(pushShaft.Slice(24, 4), capsuleCount);
        BitConverter.TryWriteBytes(pushShaft.Slice(28, 4), (int)(_frameIndex & 0x7FFFFFFF));
        BitConverter.TryWriteBytes(pushShaft.Slice(32, 4), Engine_PostFxParams.VolumetricShaftScale);
        BitConverter.TryWriteBytes(pushShaft.Slice(36, 4), 0.0f);

        Dispatch(
            _volShaftsPipeline,
            shaftSet.Set,
            _pushShafts,
            (_halfSize.X + 7) / 8,
            (_halfSize.Y + 7) / 8);

        KeyedSet upsampleSet = _volUpsampleSets[currIdx];
        if (!upsampleSet.Matches(rd, colour, depth))
        {
            upsampleSet.Replace(rd, colour, depth, _volUpsampleShader, new[]
            {
                Image(colour, 0),
                Sampler(_nearestSampler, depth, 1),
                Sampler(_nearestSampler, _shaftHalf[currIdx], 2),
                Sampler(_nearestSampler, _shaftDepthHalf[currIdx], 3),
            });
        }

        Span<byte> pushUp = _pushShaftUpsample;
        BitConverter.TryWriteBytes(pushUp.Slice(0, 4), size.X);
        BitConverter.TryWriteBytes(pushUp.Slice(4, 4), size.Y);
        BitConverter.TryWriteBytes(pushUp.Slice(8, 4), _halfSize.X);
        BitConverter.TryWriteBytes(pushUp.Slice(12, 4), _halfSize.Y);
        BitConverter.TryWriteBytes(pushUp.Slice(16, 4), inverse.Z.Z);
        BitConverter.TryWriteBytes(pushUp.Slice(20, 4), inverse.W.Z);
        BitConverter.TryWriteBytes(pushUp.Slice(24, 4), inverse.Z.W);
        BitConverter.TryWriteBytes(pushUp.Slice(28, 4), inverse.W.W);
        BitConverter.TryWriteBytes(pushUp.Slice(32, 4), 1.0f / px);
        BitConverter.TryWriteBytes(pushUp.Slice(36, 4), 1.0f / py);
        BitConverter.TryWriteBytes(pushUp.Slice(40, 4), Engine_VolumetricLightShafts.MaxFogRangeMetres);
        BitConverter.TryWriteBytes(pushUp.Slice(44, 4), 0.0f);

        Dispatch(
            _volUpsamplePipeline,
            upsampleSet.Set,
            _pushShaftUpsample,
            (size.X + 7) / 8,
            (size.Y + 7) / 8);

        _prevShaftCamOrigin = camOrigin;
        _prevShaftForward = forward;
        _prevShaftRight = right;
        _prevShaftUp = up;
        _prevShaftPx = px;
        _prevShaftPy = py;
        _shaftHistoryValid = true;
    }

    /// <summary>
    /// colour += min(layer, 1) × gain, the layer sampled bilinearly from its half-res
    /// viewport. The clamp IS the guest's A8R8G8B8 target; the gain is 1.0 in the host's units,
    /// where a scene value is the guest's exposed value over the exposure — see
    /// <see cref="Refii.Fx.Engine_FxAdditiveLayer"/>. The classic look's scene is already
    /// exposed, so there the shader multiplies the gain by E as 0162 does (c31.x).
    /// </summary>
    /// <summary>The highlight layer's texture is a live device texture; a dead one is reported
    /// back so the layer re-reads it, and nothing is bound to it this frame.</summary>
    private bool HighlightLayerLive(Rid layer)
    {
        if (_rd!.TextureIsValid(layer)) return true;
        Refii.Entities.Engine_EntityHighlightLayer.MarkStale(View);
        return false;
    }

    /// <summary>The additive layer's texture is a live device texture; see <see cref="HighlightLayerLive"/>.</summary>
    private bool AdditiveLayerLive(Rid layer)
    {
        if (_rd!.TextureIsValid(layer)) return true;
        Refii.Fx.Engine_FxAdditiveLayer.MarkStale(View);
        return false;
    }

    private void AddLayer(Rid colour, Vector2I size, Rid layer) =>
        AddLayer(colour, size, layer, _addlayerSet, Refii.Fx.Engine_FxAdditiveLayer.Gain);

    /// <summary>
    /// Adds one half-res layer into the colour buffer. Each caller brings its OWN uniform set:
    /// the set is keyed on (colour, layer), so two layers sharing one set would rebuild it on
    /// every dispatch.
    /// </summary>
    private void AddLayer(Rid colour, Vector2I size, Rid layer, KeyedSet set, float gain)
    {
        float effectiveGain = gain * Math.Max(Engine_PostFxParams.EmissionBoost, 1.0f);
        if (effectiveGain <= 1e-6f)
        {
            return;
        }

        RenderingDevice rd = _rd!;
        if (!set.Matches(rd, colour, layer))
        {
            set.Replace(rd, colour, layer, _addlayerShader, new RDUniform[]
            {
                Image(colour, 0),
                Sampler(_linearSampler, layer, 1),
                Buffer(_stateBuffer, 2),
            });
        }

        byte[] push = _pushSmall;
        BitConverter.TryWriteBytes(push.AsSpan(0), size.X);
        BitConverter.TryWriteBytes(push.AsSpan(4), size.Y);
        BitConverter.TryWriteBytes(push.AsSpan(8), effectiveGain);
        BitConverter.TryWriteBytes(push.AsSpan(12), Refii.Models.Engine_ClassicMaterial.Active ? 1.0f : 0.0f);
        Dispatch(_addlayerPipeline, set.Set, push, (size.X + 7) / 8, (size.Y + 7) / 8);
    }

    /// <summary>
    /// Fills the highlight objects table from this frame's camera: each slot's stated colour,
    /// its padded screen rect and its nearest depth. False when no target has a rect on screen.
    /// </summary>
    /// <remarks>
    /// `sub_82A69D40` projects the eight corners of the
    /// owner's box (owner+80 min, +96 max) and keeps the NEAREST corner's depth; a camera inside
    /// the box (`sub_82A6C740`) makes the rect the whole screen. `sub_82A69BB0` snaps the rect to
    /// the half-res texel grid, floor on the low side and ceil on the high, and pads it by 16
    /// half-res texels a side (0x82a69cb4..0x82a69cf4). An empty rect adds nothing.
    /// </remarks>
    private bool HighlightTargets(Basis camBasis, Vector3 camOrigin, Projection projection)
    {
        System.Array.Clear(_highlightScratch);
        float px = MathF.Abs(projection.X.X);
        float py = MathF.Abs(projection.Y.Y);
        if (px <= 1e-8f || py <= 1e-8f)
        {
            return false;
        }

        Vector3 forward = -camBasis.Z;
        Vector3 right = camBasis.X;
        Vector3 up = camBasis.Y;
        // Rects are padded by 16 texels of the guest's half-res buffer, which is the layer's size.
        float hw = Math.Max(_glowSize.X, 1);
        float hh = Math.Max(_glowSize.Y, 1);
        bool any = false;

        for (int k = 0; k < _highlightTargets.Length; ++k)
        {
            Refii.Entities.Engine_EntityHighlightLayer.Target t = _highlightTargets[k];
            if (!t.Present)
            {
                continue;
            }

            float x0 = 1f, y0 = 1f, x1 = 0f, y1 = 0f, nearest = float.MaxValue;
            bool inside = false;
            for (int c = 0; c < 8; ++c)
            {
                Vector3 corner = new(
                    (c & 1) == 0 ? t.Min.X : t.Max.X,
                    (c & 2) == 0 ? t.Min.Y : t.Max.Y,
                    (c & 4) == 0 ? t.Min.Z : t.Max.Z);
                Vector3 offset = corner - camOrigin;
                float facing = offset.Dot(forward);
                if (facing <= 0.05f)
                {
                    inside = true;
                    break;
                }

                float x = ((offset.Dot(right) * px / facing) + 1.0f) * 0.5f;
                float y = (1.0f - (offset.Dot(up) * py / facing)) * 0.5f;
                x0 = MathF.Min(x0, x);
                x1 = MathF.Max(x1, x);
                y0 = MathF.Min(y0, y);
                y1 = MathF.Max(y1, y);
                nearest = MathF.Min(nearest, facing);
            }

            if (inside)
            {
                x0 = 0f;
                y0 = 0f;
                x1 = 1f;
                y1 = 1f;
                nearest = 0f;
            }
            else
            {
                x0 = MathF.Max(x0, 0f);
                y0 = MathF.Max(y0, 0f);
                x1 = MathF.Min(x1, 1f);
                y1 = MathF.Min(y1, 1f);
                if (x0 >= x1 || y0 >= y1)
                {
                    continue;
                }

                x0 = MathF.Floor(x0 * hw) / hw - 16f / hw;
                y0 = MathF.Floor(y0 * hh) / hh - 16f / hh;
                x1 = MathF.Ceiling(x1 * hw) / hw + 16f / hw;
                y1 = MathF.Ceiling(y1 * hh) / hh + 16f / hh;
            }

            int at = k * HighlightFloats;
            _highlightScratch[at] = t.Colour.X;
            _highlightScratch[at + 1] = t.Colour.Y;
            _highlightScratch[at + 2] = t.Colour.Z;
            _highlightScratch[at + 3] = t.Colour.W;
            _highlightScratch[at + 4] = x0;
            _highlightScratch[at + 5] = y0;
            _highlightScratch[at + 6] = x1;
            _highlightScratch[at + 7] = y1;
            _highlightScratch[at + 8] = nearest;

            // THE GUEST'S FIRST DRAW, reproduced by the target's class rather than per pixel.
            // `sub_82A6BBA0` draws each object's padded rect twice: once at its AABB CENTRE's
            // depth for pixels whose stencil holds 0x10, once at its nearest corner's depth for
            // the rest; `sub_82AA2A68` gives 0x10 to every renderable whose bound radius is
            // <= 3.0 (`if (v13 > 3.0) flags &= ~0x10`). So around a character the pixels of
            // anything small — the sword in its hand, the sign it carries, the next NPC — are
            // tested against the character's CENTRE, and a held item in front of that centre
            // occludes the halo. This chain has no per-pixel record of who drew a pixel, so a
            // // small target tests every pixel against its centre, a large one against its
            // // nearest corner.
            Vector3 centre = (t.Min + t.Max) * 0.5f;
            float centreDepth = inside ? 0f : MathF.Max((centre - camOrigin).Dot(forward), 0f);
            float radius = (t.Max - t.Min).Length() * 0.5f;
            _highlightScratch[at + 9] = centreDepth;
            _highlightScratch[at + 10] = radius <= 3f ? 1f : 0f;
            any = true;
        }

        if (!any)
        {
            return false;
        }

        System.Buffer.BlockCopy(_highlightScratch, 0, _highlightBytes, 0, HighlightBufferBytes);
        EndBatch();
        _rd!.BufferUpdate(_highlightBuffer, 0, HighlightBufferBytes, _highlightBytes);
        return true;
    }

    /// <summary>The layer's slots resolved to the guest's buffer contents, into _glow[0].</summary>
    /// <summary>
    /// Keeps the glow ping-pong at the layer's own size. Asked of the render device only when
    /// the layer's texture changes, which is a resize; every set that binds a glow texture is
    /// released with it, because the composite's set is keyed on (colour, layer) and would
    /// otherwise keep sampling the freed one.
    /// </summary>
    private bool EnsureGlow(Rid layer)
    {
        RenderingDevice rd = _rd!;
        if (layer == _glowLayer && _glow[0].IsValid)
        {
            return true;
        }

        _glowLayer = layer;
        RDTextureFormat format = rd.TextureGetFormat(layer);
        var size = new Vector2I(Math.Max((int)format.Width, 1), Math.Max((int)format.Height, 1));
        if (size == _glowSize && _glow[0].IsValid)
        {
            return true;
        }

        _highlightPrepSet.Release(rd);
        _highlightCompositeSet.Release(rd);
        _glowHSet.Release(rd);
        _glowVSet.Release(rd);
        for (int i = 0; i < 2; i++)
        {
            Free(ref _glow[i]);
            _glow[i] = CreateTexture(size.X, size.Y, RenderingDevice.DataFormat.R16G16B16A16Sfloat);
        }

        _glowSize = size;
        return _glow[0].IsValid && _glow[1].IsValid;
    }

    private void HighlightPrep(Rid layer)
    {
        RenderingDevice rd = _rd!;
        if (!_highlightPrepSet.Matches(rd, layer, _glow[0]))
        {
            _highlightPrepSet.Replace(rd, layer, _glow[0], _highlightPrepShader, new RDUniform[]
            {
                Sampler(_nearestSampler, layer, 0),
                Image(_glow[0], 1),
                Buffer(_highlightBuffer, 2),
            });
        }

        byte[] push = _pushSmall;
        BitConverter.TryWriteBytes(push.AsSpan(0), _glowSize.X);
        BitConverter.TryWriteBytes(push.AsSpan(4), _glowSize.Y);
        BitConverter.TryWriteBytes(push.AsSpan(8), Refii.Entities.Engine_EntityHighlightLayer.Slots);
        BitConverter.TryWriteBytes(push.AsSpan(12), 0.0f);
        Dispatch(_highlightPrepPipeline, _highlightPrepSet.Set, push, (_glowSize.X + 7) / 8, (_glowSize.Y + 7) / 8);
    }

    /// <summary>
    /// One axis of the guest's outline bleed, at the half resolution its buffer has; see
    /// <c>fable_postfx_highlightblur.glsl</c>. <paramref name="scale"/> is the stated width's gain.
    /// </summary>
    private void HighlightBlur(KeyedSet set, Rid src, Rid dst, Vector2I dir, float scale)
    {
        RenderingDevice rd = _rd!;
        if (!set.Matches(rd, src, dst))
        {
            set.Replace(rd, src, dst, _highlightShader, new RDUniform[]
            {
                Sampler(_nearestSampler, src, 0),
                Image(dst, 1),
            });
        }

        byte[] push = _pushWide;
        BitConverter.TryWriteBytes(push.AsSpan(0), _glowSize.X);
        BitConverter.TryWriteBytes(push.AsSpan(4), _glowSize.Y);
        BitConverter.TryWriteBytes(push.AsSpan(8), dir.X);
        BitConverter.TryWriteBytes(push.AsSpan(12), dir.Y);
        BitConverter.TryWriteBytes(push.AsSpan(16), scale);
        BitConverter.TryWriteBytes(push.AsSpan(20), 0.0f);
        BitConverter.TryWriteBytes(push.AsSpan(24), 0.0f);
        BitConverter.TryWriteBytes(push.AsSpan(28), 0.0f);
        Dispatch(_highlightPipeline, set.Set, push, (_glowSize.X + 7) / 8, (_glowSize.Y + 7) / 8);
    }

    /// <summary>
    /// The outline composite over the scene: gated off the highlighted silhouettes, depth-tested
    /// per object at its nearest corner, blended ONE / INVSRCALPHA. See
    /// <c>fable_postfx_highlightcomposite.glsl</c>.
    /// </summary>
    private void HighlightComposite(Rid colour, Rid depth, Rid layer, Vector2I size, Projection projection)
    {
        RenderingDevice rd = _rd!;
        if (!_highlightCompositeSet.Matches(rd, colour, layer))
        {
            _highlightCompositeSet.Replace(rd, colour, layer, _highlightCompositeShader, new RDUniform[]
            {
                Image(colour, 0),
                Sampler(_nearestSampler, depth, 1),
                Sampler(_linearSampler, _glow[0], 2),
                Sampler(_nearestSampler, layer, 3),
                Buffer(_highlightBuffer, 4),
            });
        }

        Projection inverse = projection.Inverse();
        byte[] push = _pushWide;
        BitConverter.TryWriteBytes(push.AsSpan(0), size.X);
        BitConverter.TryWriteBytes(push.AsSpan(4), size.Y);
        BitConverter.TryWriteBytes(push.AsSpan(8), Refii.Entities.Engine_EntityHighlightLayer.Slots);
        BitConverter.TryWriteBytes(push.AsSpan(12), 0.0f);
        BitConverter.TryWriteBytes(push.AsSpan(16), inverse.Z.Z);
        BitConverter.TryWriteBytes(push.AsSpan(20), inverse.W.Z);
        BitConverter.TryWriteBytes(push.AsSpan(24), inverse.Z.W);
        BitConverter.TryWriteBytes(push.AsSpan(28), inverse.W.W);
        Dispatch(_highlightCompositePipeline, _highlightCompositeSet.Set, push, (size.X + 7) / 8, (size.Y + 7) / 8);
    }

    private void Downscale4(Rid colour, Vector2I size)
    {
        if (ClassicPost)
        {
            ClassicDownscale4(colour);
            return;
        }
        RenderingDevice rd = _rd!;
        if (!_downscale4Set.Matches(rd, colour, default))
        {
            _downscale4Set.Replace(rd, colour, default, _downscale4Shader, new RDUniform[]
            {
                Image(colour, 0),
                Image(_quarter, 1),
                Buffer(_stateBuffer, 2),
            });
        }

        Dispatch(
            _downscale4Pipeline,
            _downscale4Set.Set,
            PushDownscale(size, _quarterSize, Refii.Models.Engine_ClassicMaterial.Active),
            (_quarterSize.X + 7) / 8,
            (_quarterSize.Y + 7) / 8);
    }

    /// <summary>
    /// Synthesizes the guest's SUN LAYER into _rays[0]: the disc's own texture and colour at
    /// its projected position, per-pixel depth-tested against the scene. The guest renders
    /// this into off_8331A9A8 (half-res 2_10_10_10 UNORM, additive, z-tested) and the rays
    /// chain reads NOTHING else — not the scene, not a beams billboard.
    /// </summary>
    private void SunLayer(
        Rid depth, Vector2 sunUv, in Engine_PostFxSunSources.Source disc, Rid discTexture,
        Basis camBasis, Vector3 camOrigin, Projection projection)
    {
        RenderingDevice rd = _rd!;
        if (!_sunlayerSet.Matches(rd, depth, discTexture))
        {
            _sunlayerSet.Replace(rd, depth, discTexture, _sunlayerShader, new RDUniform[]
            {
                Sampler(_nearestSampler, depth, 0),
                Sampler(_linearSampler, discTexture, 1),
                Image(_rays[0], 2),
            });
        }

        // World half-extent at the disc's stated distance, through the projection, into UV
        // units. The guest's draw is the same camera-facing quad; only the rasteriser differs.
        float px = MathF.Abs(projection.X.X);
        float py = MathF.Abs(projection.Y.Y);
        var halfUv = new Vector2(
            disc.Distance > 1e-3f ? disc.Half.X / disc.Distance * px * 0.5f : 0.0f,
            disc.Distance > 1e-3f ? disc.Half.Y / disc.Distance * py * 0.5f : 0.0f);

        // Reversed-Z: the far plane is where the scene wrote nothing. Same convention the
        // celestial overlay derives in-shader from PROJECTION[2][2].
        float farValue = -projection.Z.Z >= 0.5f ? 1.0f : 0.0f;
        float boost = Math.Max(Engine_PostFxParams.EmissionBoost, 1.0f);
        Engine_PostFxParams.GetVistaOcean(out bool oceanActive, out float oceanY);
        Vector4 waterPlane = Engine_PostFxParams.BuildWaterPlanePush(oceanActive, oceanY, camOrigin, camBasis, px, py);

        System.Span<byte> push = _pushSun;
        BitConverter.TryWriteBytes(push.Slice(0, 4), _quarterSize.X);
        BitConverter.TryWriteBytes(push.Slice(4, 4), _quarterSize.Y);
        BitConverter.TryWriteBytes(push.Slice(8, 4), farValue);
        BitConverter.TryWriteBytes(push.Slice(12, 4), 0.0f);
        BitConverter.TryWriteBytes(push.Slice(16, 4), sunUv.X);
        BitConverter.TryWriteBytes(push.Slice(20, 4), sunUv.Y);
        BitConverter.TryWriteBytes(push.Slice(24, 4), halfUv.X);
        BitConverter.TryWriteBytes(push.Slice(28, 4), halfUv.Y);
        BitConverter.TryWriteBytes(push.Slice(32, 4), disc.Colour.X * boost);
        BitConverter.TryWriteBytes(push.Slice(36, 4), disc.Colour.Y * boost);
        BitConverter.TryWriteBytes(push.Slice(40, 4), disc.Colour.Z * boost);
        BitConverter.TryWriteBytes(push.Slice(44, 4), 1.0f);
        BitConverter.TryWriteBytes(push.Slice(48, 4), waterPlane.X);
        BitConverter.TryWriteBytes(push.Slice(52, 4), waterPlane.Y);
        BitConverter.TryWriteBytes(push.Slice(56, 4), waterPlane.Z);
        BitConverter.TryWriteBytes(push.Slice(60, 4), waterPlane.W);

        Dispatch(
            _sunlayerPipeline,
            _sunlayerSet.Set,
            _pushSun,
            (_quarterSize.X + 7) / 8,
            (_quarterSize.Y + 7) / 8);
    }

    /// <summary>
    /// Rasterises the guest's GLOW LAYER into _occl: every registered halo as a depth-tested
    /// soft disc at its light's projected position. The guest draws these quads into
    /// off_8331A9CC (half-res 2_10_10_10 UNORM, additive, z-tested), whose only reader is the
    /// bright pass — a lamp's glow blooms but never brightens the scene draw, and a wall kills
    /// it per pixel. False = nothing visible; the caller clears the layer instead.
    /// </summary>
    private bool HaloLayer(Rid depth, Basis camBasis, Vector3 camOrigin, Projection projection)
    {
        Refii.Fx.Engine_PostFxHalos.Snapshot(_haloSnapshot, out Rid spriteArray);
        if (_haloSnapshot.Count == 0)
        {
            return false;
        }

        if (!spriteArray.IsValid)
        {
            spriteArray = _haloFallbackArray;
        }

        float px = MathF.Abs(projection.X.X);
        float py = MathF.Abs(projection.Y.Y);
        if (px <= 1e-8f || py <= 1e-8f)
        {
            return false;
        }

        Vector3 forward = -camBasis.Z;
        Vector3 right = camBasis.X;
        Vector3 up = camBasis.Y;

        float boost = Math.Max(Engine_PostFxParams.EmissionBoost, 1.0f);
        int count = 0;
        foreach (Refii.Fx.Engine_PostFxHalos.WorldHalo halo in _haloSnapshot)
        {
            // ProjectSun's maths on a POSITION: the ratios are scale-free, so the camera-space
            // offset needs no normalising, and `facing` doubles as the view distance the
            // per-pixel occlusion test compares against.
            Vector3 offset = halo.Position - camOrigin;
            float facing = offset.Dot(forward);
            if (facing <= 0.05f)
            {
                continue;
            }

            float x = ((offset.Dot(right) * px / facing) + 1.0f) * 0.5f;
            float y = (1.0f - (offset.Dot(up) * py / facing)) * 0.5f;

            // Smoothly shrink local fixture halos and attenuate their radiance with view distance
            // so distant lanterns/torches at 60m+ read as subtle warm pinpoints rather than
            // full-energy bloom orbs. Sky halos (facing >= 250m) keep 100% full extent and radiance.
            float sizeScale = 1.0f;
            float colourAtten = 1.0f;
            if (Engine_PostFxParams.HaloDistanceFade && facing < 250.0f)
            {
                float t = Math.Clamp((facing - 6.0f) / 29.0f, 0.0f, 1.0f);
                sizeScale = 1.0f + (0.15f - 1.0f) * (t * t * (3.0f - 2.0f * t));
                colourAtten = 1.0f / (1.0f + 0.008f * facing * facing);
            }

            float halfExtent = halo.WorldHalf * sizeScale;
            float hx = halfExtent / facing * px * 0.5f;
            float hy = halfExtent / facing * py * 0.5f;
            if (!float.IsFinite(x) || !float.IsFinite(y) ||
                x + hx < 0.0f || x - hx > 1.0f || y + hy < 0.0f || y - hy > 1.0f)
            {
                continue;
            }

            float effectiveBoost = boost * colourAtten;
            int at = count * HaloFloats;
            _haloScratch[at] = x;
            _haloScratch[at + 1] = y;
            _haloScratch[at + 2] = hx;
            _haloScratch[at + 3] = hy;
            _haloScratch[at + 4] = halo.Colour.X * effectiveBoost;
            _haloScratch[at + 5] = halo.Colour.Y * effectiveBoost;
            _haloScratch[at + 6] = halo.Colour.Z * effectiveBoost;
            _haloScratch[at + 7] = facing;
            _haloScratch[at + 8] = halo.Layer;
            _haloScratch[at + 9] = 0f;
            _haloScratch[at + 10] = 0f;
            _haloScratch[at + 11] = 0f;
            if (++count == HaloCapacity)
            {
                break;
            }
        }

        if (count == 0)
        {
            return false;
        }

        RenderingDevice rd = _rd!;
        uint bytes = (uint)(count * HaloFloats * sizeof(float));
        System.Buffer.BlockCopy(_haloScratch, 0, _haloBytes, 0, (int)bytes);
        EndBatch();
        rd.BufferUpdate(_haloBuffer, 0, bytes, _haloBytes);
        _occlZeroCleared = false;
        BeginBatch();

        if (!_halolayerSet.Matches(rd, depth, spriteArray))
        {
            _halolayerSet.Replace(rd, depth, spriteArray, _halolayerShader, new RDUniform[]
            {
                Sampler(_nearestSampler, depth, 0),
                Image(_occl, 1),
                Buffer(_haloBuffer, 2),
                Sampler(_linearSampler, spriteArray, 3),
            });
        }

        Projection inverse = projection.Inverse();
        Engine_PostFxParams.GetVistaOcean(out bool oceanActive, out float oceanY);
        Vector4 waterPlane = Engine_PostFxParams.BuildWaterPlanePush(oceanActive, oceanY, camOrigin, camBasis, px, py);
        byte[] push = _pushHalo;
        BitConverter.TryWriteBytes(push.AsSpan(0), _quarterSize.X);
        BitConverter.TryWriteBytes(push.AsSpan(4), _quarterSize.Y);
        BitConverter.TryWriteBytes(push.AsSpan(8), count);
        BitConverter.TryWriteBytes(push.AsSpan(12), 0.0f);
        BitConverter.TryWriteBytes(push.AsSpan(16), inverse.Z.Z);
        BitConverter.TryWriteBytes(push.AsSpan(20), inverse.W.Z);
        BitConverter.TryWriteBytes(push.AsSpan(24), inverse.Z.W);
        BitConverter.TryWriteBytes(push.AsSpan(28), inverse.W.W);
        BitConverter.TryWriteBytes(push.AsSpan(32), waterPlane.X);
        BitConverter.TryWriteBytes(push.AsSpan(36), waterPlane.Y);
        BitConverter.TryWriteBytes(push.AsSpan(40), waterPlane.Z);
        BitConverter.TryWriteBytes(push.AsSpan(44), waterPlane.W);

        Dispatch(
            _halolayerPipeline,
            _halolayerSet.Set,
            push,
            (_quarterSize.X + 7) / 8,
            (_quarterSize.Y + 7) / 8);
        return true;
    }

    private void Radial(int src, Vector2 centre)
    {
        if (ClassicPost)
        {
            ClassicRadial(src, centre);
            return;
        }
        Dispatch(
            _radialPipeline,
            _radialSets[src],
            PushSizeVector(_quarterSize, centre),
            (_quarterSize.X + 7) / 8,
            (_quarterSize.Y + 7) / 8);
    }

    private void Brightcut()
    {
        if (ClassicPost)
        {
            ClassicBrightcut();
            return;
        }
        Dispatch(
            _brightcutPipeline,
            _brightcutSet,
            PushSizeFloats(_quarterSize, 0.0f, 0.0f),
            (_quarterSize.X + 7) / 8,
            (_quarterSize.Y + 7) / 8);
    }

    private void DofDownscale(Rid colour, Rid depth, Vector2I size, Projection inverse)
    {
        RenderingDevice rd = _rd!;
        if (!_dofDownscaleSet.Matches(rd, colour, depth))
        {
            _dofDownscaleSet.Replace(rd, colour, depth, _dofDownscaleShader, new RDUniform[]
            {
                Image(colour, 0),
                Image(_dof[1], 1),
                Buffer(_stateBuffer, 2),
                Sampler(_nearestSampler, depth, 3),
            });
        }

        byte[] push = PushSizeDepth(size, inverse);
        BitConverter.TryWriteBytes(push.AsSpan(8), _quarterSize.X);
        BitConverter.TryWriteBytes(push.AsSpan(12), _quarterSize.Y);
        Dispatch(_dofDownscalePipeline, _dofDownscaleSet.Set, push,
            (_quarterSize.X + 7) / 8, (_quarterSize.Y + 7) / 8);
    }

    private void Gauss(Rid set, Vector2I direction, float[] weights)
    {
        Gauss(set, _quarter, _dof[0], direction, weights);
    }

    private void Gauss(Rid set, Rid src, Rid dst, Vector2I direction, float[] weights)
    {
        if (ClassicPost)
        {
            ClassicGauss(src, dst, direction, weights);
            return;
        }
        Dispatch(
            _gaussPipeline,
            set,
            PushGauss(_quarterSize, direction, weights),
            (_quarterSize.X + 7) / 8,
            (_quarterSize.Y + 7) / 8);
    }

    private void Composite(Rid colour, Rid depth, Vector2I size, Projection inverse)
    {
        if (ClassicPost)
        {
            ClassicComposite(colour, depth, size);
            return;
        }
        RenderingDevice rd = _rd!;
        if (!_compositeSet.Matches(rd, colour, depth))
        {
            _compositeSet.Replace(rd, colour, depth, _compositeShader, new RDUniform[]
            {
                Image(colour, 0),
                Buffer(_stateBuffer, 1),
                UniformBlock(_paramsBuffer, 2),
                Sampler(_linearSampler, _bloom[0], 3),
                Sampler(_linearSampler, _dof[1], 4),
                Sampler(_nearestSampler, depth, 5),
                Sampler(_nearestSampler, _satMask?.Texture ?? _exposureRd, 6),
            });
        }

        Dispatch(
            _compositePipeline,
            _compositeSet.Set,
            PushSizeDepth(size, inverse),
            (size.X + 7) / 8,
            (size.Y + 7) / 8);
    }

    private void Histogram()
    {
        if (ClassicPost)
        {
            ClassicHistogram();
            return;
        }
        Dispatch(
            _histogramPipeline,
            _histogramSet,
            PushSizeFloats(_quarterSize, 0.0f, 0.0f),
            (_quarterSize.X + 15) / 16,
            (_quarterSize.Y + 15) / 16);
    }

    private void Clear(Rid texture)
    {
        EndBatch();
        _rd!.TextureClear(texture, new Color(0.0f, 0.0f, 0.0f, 0.0f), 0, 1, 0, 1);
    }

    private void Dump(Vector2I size, float dt)
    {
        byte[] histogram = _rd!.BufferGetData(_histogramBuffer, 0, HistogramBytes);
        byte[] stateBytes = _rd.BufferGetData(_stateBuffer, 0, StateBytes);
        if (histogram.Length < HistogramBytes || stateBytes.Length < StateBytes)
        {
            GD.PrintErr($"[postfx] readback short: {histogram.Length}/{HistogramBytes} bytes of " +
                $"histogram, {stateBytes.Length}/{StateBytes} of state");
            return;
        }

        var bins = new uint[HistogramBins];
        System.Buffer.BlockCopy(histogram, 0, bins, 0, HistogramBytes);
        var state = new float[Engine_PostFxState.Floats];
        System.Buffer.BlockCopy(stateBytes, 0, state, 0, StateBytes);
        GD.Print(Engine_PostFxReport.Format(bins, state, size.X, size.Y, dt, _frameIndex));
    }

    /// <summary>
    /// Records this frame's composited colour into the ring, at quarter resolution and with
    /// the exposure already applied, so a dump shows what the player saw.
    /// </summary>
    private void TrapCapture(Rid colour, Vector2I size)
    {
        RenderingDevice rd = _rd!;
        if (!_trapUnitState.IsValid || !_trapRing[0].IsValid)
        {
            return;
        }

        if (colour != _trapRingColour)
        {
            for (int i = 0; i < TrapRingDepth; i++)
            {
                FreeSet(ref _trapRingSets[i]);
                _trapRingSets[i] = rd.UniformSet(new RDUniform[]
                {
                    Image(colour, 0),
                    Image(_trapRing[i], 1),
                    Buffer(_trapUnitState, 2),
                }, _downscale4Shader, 0);
            }

            _trapRingColour = colour;
        }

        int slot = _trapRingNext;
        _trapRingNext = (slot + 1) % TrapRingDepth;
        _trapRingFrame[slot] = _frameIndex;
        Dispatch(
            _downscale4Pipeline,
            _trapRingSets[slot],
            PushDownscale(size, _quarterSize, false),
            (_quarterSize.X + 7) / 8,
            (_quarterSize.Y + 7) / 8);
    }

    /// <summary>
    /// Reads the trap's four scalars back every TrapPollFrames frames (a device stall, which
    /// is why it is not every frame) and writes the ring on a new hit or on request.
    /// </summary>
    /// <remarks>
    /// A hit at reduce frame F compared the histogram of frame F-1 with that of F-2, so the
    /// frame that changed is F-1; a one-frame flicker trips twice (the drop and the recovery),
    /// and the recovery's hit is the one still recorded when the poll comes round. Both are in
    /// the ring either way; trap.txt says which frames to look at first.
    /// </remarks>
    private void TrapPoll()
    {
        bool manual = _trapDumpRequested;
        if (!manual && _frameIndex % TrapPollFrames != 0)
        {
            return;
        }

        _trapDumpRequested = false;
        byte[] bytes = _rd!.BufferGetData(
            _stateBuffer, (uint)(Engine_PostFxState.TrapPrevMean * sizeof(float)), 4 * sizeof(float));
        if (bytes.Length < 4 * sizeof(float))
        {
            return;
        }

        float hitFrame = BitConverter.ToSingle(bytes, 4);
        float hitMean = BitConverter.ToSingle(bytes, 8);
        float hitPrev = BitConverter.ToSingle(bytes, 12);
        bool hit = hitFrame > _trapLastHitFrame;
        if (!hit && !manual)
        {
            return;
        }

        _trapLastHitFrame = Math.Max(hitFrame, _trapLastHitFrame);
        TrapDump(hit ? (long)hitFrame : -1, hitMean, hitPrev, manual);
    }

    private void TrapDump(long hitFrame, float hitMean, float hitPrev, bool manual)
    {
        string dir = System.IO.Path.Combine(
            OS.GetUserDataDir(), "flicker", $"{DateTime.Now:yyyyMMdd-HHmmss}-f{_frameIndex}");
        try
        {
            System.IO.Directory.CreateDirectory(dir);
        }
        catch (Exception e)
        {
            GD.PrintErr($"[postfx] flicker trap: cannot create {dir}: {e.Message}");
            return;
        }

        // The texture reads must happen on this thread, inside the frame; the conversion, the
        // PNGs and the summary go to a worker, so an armed trap never holds the frame.
        var frames = new List<(long Frame, byte[] Data)>(TrapRingDepth);
        for (int k = 0; k < TrapRingDepth; k++)
        {
            int slot = (_trapRingNext + k) % TrapRingDepth;
            long frame = _trapRingFrame[slot];
            if (frame <= 0 || !_trapRing[slot].IsValid)
            {
                continue;
            }

            frames.Add((frame, _rd!.TextureGetData(_trapRing[slot], 0)));
        }

        Vector2I quarter = _quarterSize;
        Vector2I full = _fullSize;
        float ratio = _trapRatio;
        long dumpedAt = _frameIndex;
        System.Threading.Tasks.Task.Run(() =>
        {
            int written = 0;
            long first = long.MaxValue;
            long last = 0;
            foreach ((long frame, byte[] data) in frames)
            {
                var image = Godot.Image.CreateFromData(
                    quarter.X, quarter.Y, false, Godot.Image.Format.Rgbah, data);
                image.Convert(Godot.Image.Format.Rgba8);
                if (image.SavePng(System.IO.Path.Combine(dir, $"frame_{frame:D8}.png")) == Error.Ok)
                {
                    written++;
                    first = Math.Min(first, frame);
                    last = Math.Max(last, frame);
                }
            }

            string why = hitFrame >= 0
                ? $"hit at reduce frame {hitFrame}: mean {hitPrev:0.####} -> {hitMean:0.####} " +
                  $"(x{(hitPrev > 0f ? hitMean / hitPrev : 0f):0.##}); look at frames " +
                  $"{hitFrame - 2} and {hitFrame - 1} first"
                : "no hit recorded";
            string summary = $"flicker trap ratio x{ratio:0.##}, {(manual ? "on request" : "automatic")}; " +
                $"{why}; {written} frame(s) {first}..{last} at {quarter.X}x{quarter.Y}, " +
                $"internal {full.X}x{full.Y}, dumped at frame {dumpedAt}";
            try
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "trap.txt"), summary + "\n");
            }
            catch (Exception e)
            {
                GD.PrintErr($"[postfx] flicker trap: cannot write trap.txt: {e.Message}");
            }

            _trapLastDump = dir;
            GD.Print($"[postfx] {summary} -> {dir}");
        });
    }

    private void Dispatch(Rid pipeline, Rid uniformSet, byte[]? push, int groupsX, int groupsY)
    {
        if (Refii.Core.Engine_RdHealth.IsDeviceLost || !pipeline.IsValid || !uniformSet.IsValid)
        {
            return;
        }

        if (_batchList >= 0)
        {
            if (_batchHasDispatch)
            {
                _rd!.ComputeListAddBarrier(_batchList);
            }

            _rd!.ComputeListBindComputePipeline(_batchList, pipeline);
            _rd.ComputeListBindUniformSet(_batchList, uniformSet, 0);
            if (push is not null)
            {
                _rd.ComputeListSetPushConstant(_batchList, push, (uint)push.Length);
            }

            _rd.ComputeListDispatch(_batchList, (uint)groupsX, (uint)groupsY, 1);
            _batchHasDispatch = true;
            return;
        }

        long list = _rd!.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(list, pipeline);
        _rd.ComputeListBindUniformSet(list, uniformSet, 0);
        if (push is not null)
        {
            _rd.ComputeListSetPushConstant(list, push, (uint)push.Length);
        }

        _rd.ComputeListDispatch(list, (uint)groupsX, (uint)groupsY, 1);
        _rd.ComputeListEnd();
    }

    private static RDUniform Image(Rid texture, int binding)
    {
        var uniform = new RDUniform
        {
            UniformType = RenderingDevice.UniformType.Image,
            Binding = binding,
        };
        uniform.AddId(texture);
        return uniform;
    }

    private static RDUniform Buffer(Rid buffer, int binding)
    {
        var uniform = new RDUniform
        {
            UniformType = RenderingDevice.UniformType.StorageBuffer,
            Binding = binding,
        };
        uniform.AddId(buffer);
        return uniform;
    }

    private static RDUniform UniformBlock(Rid buffer, int binding)
    {
        var uniform = new RDUniform
        {
            UniformType = RenderingDevice.UniformType.UniformBuffer,
            Binding = binding,
        };
        uniform.AddId(buffer);
        return uniform;
    }

    private static RDUniform Sampler(Rid sampler, Rid texture, int binding)
    {
        var uniform = new RDUniform
        {
            UniformType = RenderingDevice.UniformType.SamplerWithTexture,
            Binding = binding,
        };
        uniform.AddId(sampler);
        uniform.AddId(texture);
        return uniform;
    }

    private byte[] PushSizeFloats(Vector2I size, float a, float b)
    {
        byte[] bytes = _pushSmall;
        BitConverter.TryWriteBytes(bytes.AsSpan(0), size.X);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), size.Y);
        BitConverter.TryWriteBytes(bytes.AsSpan(8), a);
        BitConverter.TryWriteBytes(bytes.AsSpan(12), b);
        return bytes;
    }

    private byte[] PushSizes(Vector2I a, Vector2I b)
    {
        byte[] bytes = _pushSmall;
        BitConverter.TryWriteBytes(bytes.AsSpan(0), a.X);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), a.Y);
        BitConverter.TryWriteBytes(bytes.AsSpan(8), b.X);
        BitConverter.TryWriteBytes(bytes.AsSpan(12), b.Y);
        return bytes;
    }

    /// <summary>The quarter pass's push: the two sizes, then whether the scene is already exposed.</summary>
    private byte[] PushDownscale(Vector2I full, Vector2I quarter, bool sceneExposed)
    {
        byte[] bytes = _pushWide;
        BitConverter.TryWriteBytes(bytes.AsSpan(0), full.X);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), full.Y);
        BitConverter.TryWriteBytes(bytes.AsSpan(8), quarter.X);
        BitConverter.TryWriteBytes(bytes.AsSpan(12), quarter.Y);
        BitConverter.TryWriteBytes(bytes.AsSpan(16), sceneExposed ? 1.0f : 0.0f);
        BitConverter.TryWriteBytes(bytes.AsSpan(20), 0.0f);
        BitConverter.TryWriteBytes(bytes.AsSpan(24), 0.0f);
        BitConverter.TryWriteBytes(bytes.AsSpan(28), 0.0f);
        return bytes;
    }

    private byte[] PushSizeVector(Vector2I size, Vector2 v)
    {
        byte[] bytes = _pushWide;
        BitConverter.TryWriteBytes(bytes.AsSpan(0), size.X);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), size.Y);
        BitConverter.TryWriteBytes(bytes.AsSpan(8), 0.0f);
        BitConverter.TryWriteBytes(bytes.AsSpan(12), 0.0f);
        BitConverter.TryWriteBytes(bytes.AsSpan(16), v.X);
        BitConverter.TryWriteBytes(bytes.AsSpan(20), v.Y);
        BitConverter.TryWriteBytes(bytes.AsSpan(24), 0.0f);
        BitConverter.TryWriteBytes(bytes.AsSpan(28), 0.0f);
        return bytes;
    }

    private byte[] PushSizeDepth(Vector2I size, Projection inverse)
    {
        byte[] bytes = _pushWide;
        BitConverter.TryWriteBytes(bytes.AsSpan(0), size.X);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), size.Y);
        BitConverter.TryWriteBytes(bytes.AsSpan(8), 0.0f);
        BitConverter.TryWriteBytes(bytes.AsSpan(12), 0.0f);
        BitConverter.TryWriteBytes(bytes.AsSpan(16), inverse.Z.Z);
        BitConverter.TryWriteBytes(bytes.AsSpan(20), inverse.W.Z);
        BitConverter.TryWriteBytes(bytes.AsSpan(24), inverse.Z.W);
        BitConverter.TryWriteBytes(bytes.AsSpan(28), inverse.W.W);
        return bytes;
    }

    private byte[] PushGauss(Vector2I size, Vector2I direction, float[] weights)
    {
        byte[] bytes = _pushGauss;
        BitConverter.TryWriteBytes(bytes.AsSpan(0), size.X);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), size.Y);
        BitConverter.TryWriteBytes(bytes.AsSpan(8), direction.X);
        BitConverter.TryWriteBytes(bytes.AsSpan(12), direction.Y);
        for (int i = 0; i < Engine_PostFxModel.GaussianTaps; i++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(16 + (i * 4)), weights[i]);
        }

        BitConverter.TryWriteBytes(bytes.AsSpan(44), 0.0f);
        return bytes;
    }
}
