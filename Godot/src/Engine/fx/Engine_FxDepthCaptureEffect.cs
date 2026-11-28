using System;
using Godot;
using Godot.Collections;
using Refii.Core;

namespace Refii.Fx;

[Tool]
[GlobalClass]
public partial class Engine_FxDepthCaptureEffect : CompositorEffect
{
    public const string ShaderPath = "res://src/Engine/fx/fx_depth_capture.glsl";
    public const string GlobalTextureName = "fx_scene_depth";
    public const string GlobalValidName = "fx_scene_depth_valid";
    public const int RequiredSettleFrames = 3;

    /// <summary>
    /// Player two's view's depth on split screen. A global is one value for every view, so each
    /// view's capture publishes its own and the readers pick by the second view's layer
    /// (<see cref="Refii.Models.Engine_ClassicViews.SecondViewLayer"/>).
    /// </summary>
    public const string GlobalTextureSecondName = "fx_scene_depth_second";

    private static bool _globalsRegistered;
    private static readonly Texture2Drd?[] _textures = new Texture2Drd?[2];
    private static ImageTexture? _fallbackDepthTexture;

    // D3D12 / Intel Arc DXIL warning: never bind an uninitialized `new Texture2Drd()` (`TextureRdRid == default`)
    // to a global shader parameter. Shaders sample `fx_scene_depth` even while DepthCapture is disabled;
    // an unbound/null RD RID creates an invalid SRV descriptor that can hang or reset D3D12 devices.
    private static ImageTexture GetOrCreateFallbackDepthTexture()
    {
        if (_fallbackDepthTexture is null || !GodotObject.IsInstanceValid(_fallbackDepthTexture))
        {
            Image image = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
            image.Fill(Colors.Black);
            _fallbackDepthTexture = ImageTexture.CreateFromImage(image);
        }

        return _fallbackDepthTexture;
    }

    /// <summary>Which view this capture runs in: 0 the window's (player one's on split screen), 1 player two's.</summary>
    public int View { get; set; }

    private static string TextureGlobal(int view) => view == 0 ? GlobalTextureName : GlobalTextureSecondName;

    /// <summary>
    /// The capture whose depth each view's global holds now. Two cameras capture for view 0 — the
    /// window's, and player one's split view (Refii.Game.GameSplitView) — and only the one drawing
    /// runs, so the one that runs claims the global and only the holder clears it on release.
    /// </summary>
    private static readonly Engine_FxDepthCaptureEffect?[] _publishers = new Engine_FxDepthCaptureEffect?[2];

    /// <summary>1 while a publish is queued for the main thread.</summary>
    private int _publishQueued;

    private RenderingDevice? _rd;
    private Rid _shader;
    private Rid _pipeline;
    private Rid _sampler;
    private Rid _target;
    private Vector2I _targetSize;
    private bool _ready;
    private readonly Refii.Core.Engine_UniformSetKey _set = new(3);
    private readonly byte[] _push = new byte[16];

    /// <summary>
    /// Publishes the depth capture's two globals.
    /// </summary>
    /// <remarks>
    /// THE NAMES ARE DECLARED IN project.godot's [shader_globals], and must be. Setting a global
    /// that was never declared does not create it — it fails, and every shader referencing the
    /// name then reports "was removed at some point. Material will not display correctly". That
    /// was happening 259 times a run, on both displacement shaders, in the editor and the game.
    ///
    /// NOT PROBED FIRST. Every reader — GlobalShaderParameterGetList, ...Get and ...GetType —
    /// is EDITOR-ONLY: outside the editor each one ERR_FAILs with "This function should never
    /// be used outside the editor, it can severely damage performance" and returns empty. A
    /// probe therefore reports "not declared" in every game build, however well the globals
    /// are working, and returning on it skipped the Sets below. Measured: the run warned here
    /// and logged that ERR once, while the 259 "was removed at some point" material warnings
    /// the declaration was added to fix stayed at zero — the globals were fine, the check was
    /// not. If a name really is missing, Godot says so accurately on the Set, as
    /// `Condition "!global_shader_uniforms.variables.has(p_name)" is true`.
    /// </remarks>
    public static void RegisterGlobals()
    {
        if (_globalsRegistered) return;
        _globalsRegistered = true;

        ImageTexture fallback = GetOrCreateFallbackDepthTexture();
        for (int view = 0; view < _textures.Length; ++view)
        {
            RenderingServer.GlobalShaderParameterSet(TextureGlobal(view), fallback);
        }

        RenderingServer.GlobalShaderParameterSet(GlobalValidName, 0.0f);
    }

    /// <remarks>
    /// THE DEPTH IS ASKED FOR RESOLVED. `GetDepthLayer(0)` is the single-sample depth; under MSAA
    /// Godot fills it only for an effect that sets <see cref="CompositorEffect.AccessResolvedDepth"/>
    /// (or an unrelated feature that happens to need it), so with the launcher's `msaa` on and none
    /// of those, every reader of `fx_scene_depth` — world markers, highlight, soft particles,
    /// displacement — tested against a buffer the frame never wrote. No cost with MSAA off.
    /// </remarks>
    public Engine_FxDepthCaptureEffect()
    {
        AccessResolvedDepth = true;
        EffectCallbackType = Refii.AndroidRuntime.AndroidDepthCapture.CallbackType(RenderingServer.GetCurrentRenderingMethod());
        RegisterGlobals();
        RenderingServer.CallOnRenderThread(Callable.From(Initialise));
    }

    private void Initialise()
    {
        _rd = RenderingServer.GetRenderingDevice();
        if (_rd is null) return;
        var file = GD.Load<RDShaderFile>(ShaderPath);
        if (file is null) return;
        RDShaderSpirV spirv = file.GetSpirV();
        _shader = _rd.Shader(spirv);
        if (!_shader.IsValid) return;
        _pipeline = _rd.ComputePipeline(_shader);
        var samplerState = new RDSamplerState { MinFilter = RenderingDevice.SamplerFilter.Nearest, MagFilter = RenderingDevice.SamplerFilter.Nearest, RepeatU = RenderingDevice.SamplerRepeatMode.ClampToEdge, RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge };
        _sampler = _rd.Sampler(samplerState);
        _ready = _pipeline.IsValid && _sampler.IsValid;
    }

    private void EnsureTarget(Vector2I size)
    {
        if (Refii.Core.Engine_RdHealth.IsDeviceLost || _rd is null)
        {
            return;
        }

        if (_target.IsValid && size == _targetSize) return;
        // The FX materials sample the old depth until the deferred re-point below lands.
        Refii.Core.Engine_RdRetire.Retire(_target);
        var format = new RDTextureFormat
        {
            Width = (uint)size.X,
            Height = (uint)size.Y,
            Format = RenderingDevice.DataFormat.R32Sfloat,
            TextureType = RenderingDevice.TextureType.Type2D,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit |
                RenderingDevice.TextureUsageBits.StorageBit |
                RenderingDevice.TextureUsageBits.CanUpdateBit |
                RenderingDevice.TextureUsageBits.CanCopyToBit,
        };
        _target = _rd.Texture(format, new RDTextureView());
        if (!Refii.Core.Engine_RdHealth.CheckRdValid(_target, "Engine_FxDepthCaptureEffect.EnsureTarget"))
        {
            _targetSize = default;
            return;
        }

        _rd.TextureClear(_target, new Color(0.0f, 0.0f, 0.0f, 0.0f), 0, 1, 0, 1);
        _targetSize = size;
        Publish();
    }

    /// <summary>
    /// Points this view's global at this capture's target, on the main thread: when the target is
    /// made, and whenever this capture runs while another holds the global (the split view's player
    /// one drew last, or released it).
    /// </summary>
    private void Publish()
    {
        if (System.Threading.Interlocked.Exchange(ref _publishQueued, 1) == 1)
        {
            return;
        }

        int view = View;
        Callable.From(() =>
        {
            System.Threading.Volatile.Write(ref _publishQueued, 0);
            Rid rid = _target;
            if (_released || !rid.IsValid || view is < 0 or >= 2)
            {
                return;
            }

            Texture2Drd texture = _textures[view] ??= new Texture2Drd();
            texture.TextureRdRid = rid;
            RenderingServer.GlobalShaderParameterSet(TextureGlobal(view), texture);
            if (view == 0)
            {
                RenderingServer.GlobalShaderParameterSet(GlobalValidName, 1.0f);
            }

            System.Threading.Volatile.Write(ref _publishers[view], this);
        }).CallDeferred();
    }

    /// <summary>
    /// Releases the rendering-device objects this effect owns, before the device goes.
    /// </summary>
    /// <remarks>
    /// NOTHING FREED THESE. The shader, pipeline, sampler and target were created once and left
    /// to the process, and the static <c>Texture2Drd</c> holding the target's RID outlives every
    /// node — it is published as a global shader parameter, so the rendering server keeps a
    /// reference to it too. At exit the device is torn down first and those references are
    /// dangling, which is the shape of the shutdown fault: `viewport_remove_canvas` reporting a
    /// null viewport and `_disconnect` reporting a connection that is already gone, then a
    /// segfault inside Godot's own teardown on the main thread with the tree already emptied.
    ///
    /// `NotificationPredelete` rather than `_ExitTree` because a CompositorEffect is a Resource
    /// and never enters the tree. The global parameter is cleared FIRST: dropping the texture
    /// while the server still points at it is the same bug one step later.
    /// </remarks>
    private bool _released;

    public override void _Notification(int what)
    {
        if (what != NotificationPredelete)
        {
            return;
        }

        ReleaseNow(releaseStaticFallbacks: false);
    }

    /// <summary>
    /// Restores or clears depth globals and schedules RD resource release while the render thread is alive.
    /// </summary>
    public void ReleaseNow(bool releaseStaticFallbacks = false)
    {
        if (_released && !releaseStaticFallbacks)
        {
            return;
        }
        _released = true;

        // Only the capture holding the global gives it up: player one's split view, freed when local
        // co-op ends, is not what the window draws through after it, and clearing the window's
        // depth left every FX layer, highlight and world marker untested against the scene.
        bool holds = View is >= 0 and < 2 && ReferenceEquals(_publishers[View], this);
        if (holds)
        {
            _publishers[View] = null;
        }

        if (_globalsRegistered && (holds || releaseStaticFallbacks))
        {
            if (View == 0)
            {
                RenderingServer.GlobalShaderParameterSet(GlobalValidName, 0.0f);
            }

            RenderingServer.GlobalShaderParameterSet(
                TextureGlobal(View),
                !releaseStaticFallbacks && _fallbackDepthTexture is not null && GodotObject.IsInstanceValid(_fallbackDepthTexture)
                    ? _fallbackDepthTexture
                    : default(Variant));
        }

        if (releaseStaticFallbacks)
        {
            if (View == 0)
            {
                RenderingServer.GlobalShaderParameterSet(GlobalValidName, 0.0f);
                RenderingServer.GlobalShaderParameterSet(GlobalTextureName, default(Variant));
                RenderingServer.GlobalShaderParameterSet(GlobalTextureSecondName, default(Variant));
            }
            if (_fallbackDepthTexture is not null && GodotObject.IsInstanceValid(_fallbackDepthTexture))
            {
                _fallbackDepthTexture.Dispose();
            }
            _fallbackDepthTexture = null;
            _globalsRegistered = false;
        }

        if ((holds || releaseStaticFallbacks) && View is >= 0 and < 2 && _textures[View] is { } texture)
        {
            texture.TextureRdRid = default;
            _textures[View] = null;
        }

        Engine_FxDepthCaptureEffect self = this;
        Refii.Core.Engine_RdRetire.OnRenderThread(() => self.ReleaseDeviceObjects());
    }

    /// <summary>Frees the device objects. Render thread (<see cref="Refii.Core.Engine_RdRetire.OnRenderThread"/>).</summary>
    private void ReleaseDeviceObjects()
    {
        _ready = false;
        RenderingDevice? rd = RenderingServer.GetRenderingDevice();
        if (_rd is null || rd is null)
        {
            _rd = null;
            return;
        }

        // Freed in creation order's reverse, and each guarded: a device that has already gone
        // leaves every RID invalid, and freeing an invalid one is its own error.
        if (_target.IsValid) rd.FreeRid(_target);
        if (_sampler.IsValid) rd.FreeRid(_sampler);
        if (_pipeline.IsValid) rd.FreeRid(_pipeline);
        if (_shader.IsValid) rd.FreeRid(_shader);

        _target = default;
        _sampler = default;
        _pipeline = default;
        _shader = default;
        _rd = null;
    }

    /// <summary>
    /// Whether any active effect or HUD marker in <paramref name="view"/> reads <c>fx_scene_depth</c> this frame.
    /// </summary>
    private static bool HasActiveDepthConsumer(int view) =>
        Engine_FxAdditiveLayer.IsViewActive(view) ||
        Engine_FxDisplacement.IsViewActive(view) ||
        Refii.Entities.Engine_EntityHighlightLayer.IsViewActive(view) ||
        Refii.Ui.Engine_HudWorldSprites.HasActiveSprites(view);

    public override void _RenderCallback(int effectCallbackType, RenderData renderData)
    {
        if (_rd is not null) Refii.Core.Engine_RdRetire.Pump(_rd);
        if (Refii.Core.Engine_RdHealth.IsDeviceLost || !_ready || _rd is null ||
            !Refii.Game.GameWorld.ShouldRunRenderEffects(
                Refii.Game.GameWorld.HasActiveWorld,
                Refii.Game.GameWorld.CoverHeld,
                Refii.Game.GameWorld.ReadyForWarmupAndSettle,
                Refii.Game.GameWorld.SettleFrames,
                requiredSettleFrames: RequiredSettleFrames))
        {
            return;
        }
        if (renderData.GetRenderSceneBuffers() is not RenderSceneBuffersRD buffers) return;
        Vector2I size = buffers.GetInternalSize();
        if (size.X <= 0 || size.Y <= 0) return;
        if (!HasActiveDepthConsumer(View)) return;
        EnsureTarget(size);
        Rid depth = buffers.GetDepthLayer(0);
        if (Refii.Core.Engine_RdHealth.IsDeviceLost || !_target.IsValid || !depth.IsValid) return;
        if (!ReferenceEquals(System.Threading.Volatile.Read(ref _publishers[View]), this))
        {
            Publish();
        }
        // The set is held by the resources it binds: the RDUniforms and their Array were three
        // finalizable wrappers per frame for a set the cache already had.
        Rid set = _set.For(_rd, stackalloc Rid[] { _sampler, depth, _target });
        if (!set.IsValid)
        {
            var depthUniform = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 0 };
            depthUniform.AddId(_sampler);
            depthUniform.AddId(depth);
            var outUniform = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 1 };
            outUniform.AddId(_target);
            set = _set.Hold(Engine_RdCalls.CachedUniformSet(_shader, 0, depthUniform, outUniform));
        }
        if (!set.IsValid || !_pipeline.IsValid) return;
        BitConverter.TryWriteBytes(_push.AsSpan(0), size.X);
        BitConverter.TryWriteBytes(_push.AsSpan(4), size.Y);
        long list = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(list, _pipeline);
        _rd.ComputeListBindUniformSet(list, set, 0);
        _rd.ComputeListSetPushConstant(list, (ReadOnlySpan<byte>)_push, (uint)_push.Length);
        _rd.ComputeListDispatch(list, (uint)((size.X + 7) / 8), (uint)((size.Y + 7) / 8), 1);
        _rd.ComputeListEnd();
    }
}
