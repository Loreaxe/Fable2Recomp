using System;
using Godot;

namespace Refii.Fx;

/// <summary>
/// The displacement pass: FX surfaces write screen-space offsets into this layer's own viewport,
/// and a full-view canvas rect at layer -100 redraws the view's 3D image through them.
/// </summary>
/// <remarks>
/// ONE INSTANCE PER VIEW (<see cref="Refii.Core.Engine_Views"/>). View 0 mirrors the window's
/// camera and draws over the window, or over player one's view while the screen is split; view 1
/// exists only while split and mirrors and draws over player two's. Both draw the one layer world
/// — the FX instances live in view 0's scenario — each at its own view's size; the apply rect is
/// a canvas layer of that view's viewport, so its screen texture is that view's image.
/// </remarks>
public partial class Engine_FxDisplacement : Node
{
    public const int Layer = 20;
    public const uint LayerMask = 1u << (Layer - 1);
    public const string ApplyShaderPath = "res://src/Engine/fx/fx_displacement_apply.gdshader";

    private static Engine_FxDisplacement? _current;

    private SubViewport _viewport = null!;
    private Camera3D _camera = null!;
    private CanvasLayer _layer = null!;
    private ColorRect _rect = null!;
    private ShaderMaterial? _apply;
    private Viewport? _host;

    /// <summary>The viewport Ensure was called from; view 0's host whenever the screen is whole.</summary>
    private Viewport? _windowHost;

    /// <summary>Which view this instance draws for: 0 the window or player one's, 1 player two's.</summary>
    private int _view;

    /// <summary>View 1's layer world: view 0's, so both views draw the same instances.</summary>
    private World3D? _world;

    /// <summary>View 1's instance while the screen is split; held by view 0's.</summary>
    private Engine_FxDisplacement? _second;

    private bool _enabled = true;

    /// <summary>True only when the host window is BOTH assigned and still alive.</summary>
    /// <remarks>
    /// A FREED GODOT OBJECT IS NOT NULL. The C# wrapper outlives the native object, so
    /// `_host is null` is false for a window that has already been torn down and the next
    /// call throws ObjectDisposedException — 90 times in one run here, every frame after a
    /// level change took the window away. `IsInstanceValid` is the only test that sees it,
    /// and it is what the entity code already uses on its own nodes.
    /// </remarks>
    private bool HostAlive => _host is not null && GodotObject.IsInstanceValid(_host);
    private Vector2I _size;
    private Vector2I _dispTargetSize;
    private static readonly bool[] _viewActive = new bool[2];

    /// <summary>Whether <paramref name="view"/>'s displacement SubViewport is actively rendering this frame.</summary>
    public static bool IsViewActive(int view) =>
        (uint)view < 2u && System.Threading.Volatile.Read(ref _viewActive[view]);

    public static float Strength = 1f;

    public static Engine_FxDisplacement? Current => _current;

    /// <summary>View 1's instance (player two's half) while the screen is split, else null.</summary>
    public static Engine_FxDisplacement? SecondView =>
        _current?._second is { } second && GodotObject.IsInstanceValid(second) ? second : null;

    /// <summary>The layer's own viewport, for 2D drawn into it (Engine_ModernDrunk); null until ready.</summary>
    public SubViewport? Target => _viewport;

    /// <summary>The layer world's scenario for its instances; invalid until the layer is up.</summary>
    public static Rid Scenario =>
        _current is not null && GodotObject.IsInstanceValid(_current) && _current._viewport is not null
            ? _current._viewport.World3D.Scenario
            : default;

    /// <summary>Live lever: stop or resume the displacement pass, in every view.</summary>
    public void SetEnabled(bool on)
    {
        _enabled = on;
        if (_viewport is not null) _viewport.RenderTargetUpdateMode = on ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
        if (_layer is not null) _layer.Visible = on;
        if (_second is not null && GodotObject.IsInstanceValid(_second)) _second.SetEnabled(on);
    }

    private static ulong _activeFrame;

    public static void NotifyActive() => _activeFrame = Engine.GetProcessFrames();

    /// <summary>The frame a shown static displacement mesh was last pumped (<see cref="NotifyStaticShown"/>).</summary>
    private static ulong _staticFrame;

    /// <summary>
    /// The frame this view woke for a static displacement mesh alone, so <see cref="SleepUnseen"/>
    /// may put it back to sleep; any other frame, never.
    /// </summary>
    private ulong _staticOnlyFrame = ulong.MaxValue;

    /// <summary>
    /// A static displacement mesh is shown (Engine_DisplacementMeshes.Pump): wakes the layer next
    /// frame exactly as <see cref="NotifyActive"/> does, except that the wake can be taken back in
    /// that frame by <see cref="SleepUnseen"/>.
    /// </summary>
    public static void NotifyStaticShown() => _staticFrame = Engine.GetProcessFrames();

    /// <summary>
    /// Puts back to sleep, for this frame, each view woken only by a static displacement mesh when
    /// nothing else stated the layer active this frame and <paramref name="seen"/> says no static
    /// mesh is inside the frustum of the camera that view copied this frame — the camera it draws
    /// with. Main thread, after every view's _Process and before the frame draws.
    /// </summary>
    public static void SleepUnseen(Func<Camera3D, bool> seen)
    {
        if (_current is not { } first || !GodotObject.IsInstanceValid(first)) return;
        ulong frame = Engine.GetProcessFrames();
        if (_activeFrame == frame) return;
        first.SleepIfUnseen(frame, seen);
        if (first._second is { } second && GodotObject.IsInstanceValid(second)) second.SleepIfUnseen(frame, seen);
    }

    private void SleepIfUnseen(ulong frame, Func<Camera3D, bool> seen)
    {
        if (_staticOnlyFrame != frame || _rect is null || _camera is null || seen(_camera)) return;
        _staticOnlyFrame = ulong.MaxValue;
        if ((uint)_view < 2u) System.Threading.Volatile.Write(ref _viewActive[_view], false);
        _rect.Visible = false;
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
    }

    public Image? CaptureMap() => _viewport?.GetTexture()?.GetImage();

    public static void Ensure(Node context)
    {
        if (_current is not null && GodotObject.IsInstanceValid(_current)) return;
        SceneTree? tree = context.GetTree();
        if (tree is null) return;
        Viewport host = context.GetViewport() ?? tree.Root;
        var node = new Engine_FxDisplacement { Name = "FxDisplacement", _host = host, _windowHost = host };
        _current = node;
        tree.Root.CallDeferred(Node.MethodName.AddChild, node);
    }

    public override void _Ready()
    {
        _host ??= GetViewport();
        _windowHost ??= _host;
        _viewport = new SubViewport
        {
            Name = "DispViewport",
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            RenderTargetClearMode = SubViewport.ClearMode.Always,
            Msaa3D = Viewport.Msaa.Disabled,
            UseDebanding = false,
            UseHdr2D = false,
            PositionalShadowAtlasSize = 0,
            // ITS OWN WORLD. The layer's surfaces are rendering-server instances in this world
            // (Engine_FxLayerInstance), so the main scene's index never holds them and this
            // viewport culls only them; the scene depth arrives through the depth-capture global.
            // View 1 draws view 0's world.
            World3D = _world ?? new World3D(),
        };
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0f, 0f, 0f, 0f),
            TonemapMode = Godot.Environment.ToneMapper.Linear,
            TonemapExposure = 1f,
            AmbientLightSource = Godot.Environment.AmbientSource.Disabled,
            GlowEnabled = false,
        };
        _camera = new Camera3D { Name = "DispCamera", CullMask = LayerMask | (_view == 1 ? Refii.Models.Engine_ClassicViews.SecondViewLayer : 0u), Environment = env };
        _viewport.AddChild(_camera);
        AddChild(_viewport);
        _camera.Current = true;

        _apply = new ShaderMaterial { Shader = GD.Load<Shader>(ApplyShaderPath) };
        _apply.SetShaderParameter("disp_tex", _viewport.GetTexture());
        // The rect is sized to the host in Resize rather than anchored: a Control under a
        // CanvasLayer anchors to the viewport its NODE is in (the window), not the layer's
        // custom viewport, which is one player's half while split.
        _layer = new CanvasLayer { Name = "DispApply", Layer = -100, Visible = _enabled };
        _rect = new ColorRect { Name = "DispRect", Material = _apply, MouseFilter = Control.MouseFilterEnum.Ignore };
        _layer.AddChild(_rect);
        AddChild(_layer);
        _layer.CustomViewport = _host;
        ProcessPriority = 1000;
        Resize();

        if (_view == 0)
        {
            Refii.Core.Engine_Views.ViewsChanged += FollowViews;
            FollowViews();
        }
    }

    public override void _ExitTree()
    {
        if (_view == 0)
        {
            Refii.Core.Engine_Views.ViewsChanged -= FollowViews;
            System.Threading.Volatile.Write(ref _viewActive[1], false);
        }

        if ((uint)_view < 2u)
        {
            System.Threading.Volatile.Write(ref _viewActive[_view], false);
        }
    }

    /// <summary>
    /// View 0's: builds view 1's while the two views are open (Engine_Views.Open) and frees it when they
    /// close; draws over player one's view while they are drawn and over the window while it draws, so a
    /// split or rejoin re-hosts and builds nothing. The re-host happens before the split view frees its
    /// viewports, so the apply layer never points at a freed one.
    /// </summary>
    private void FollowViews()
    {
        if (!GodotObject.IsInstanceValid(this) || !IsInsideTree())
        {
            return;
        }

        if (Refii.Core.Engine_Views.Open &&
            Refii.Core.Engine_Views.FirstViewport is { } first && GodotObject.IsInstanceValid(first) &&
            Refii.Core.Engine_Views.SecondViewport is { } second && GodotObject.IsInstanceValid(second))
        {
            if (Refii.Core.Engine_Views.Split || _windowHost is null || !GodotObject.IsInstanceValid(_windowHost))
            {
                Host(first);
            }
            else
            {
                Host(_windowHost);
            }

            if (_second is null || !GodotObject.IsInstanceValid(_second))
            {
                _second = new Engine_FxDisplacement
                {
                    Name = "FxDisplacementSecondView",
                    _view = 1,
                    _host = second,
                    _world = _viewport.World3D,
                    _enabled = _enabled,
                };
                AddChild(_second);
            }
            else
            {
                _second.Host(second);
            }

            return;
        }

        if (_windowHost is not null && GodotObject.IsInstanceValid(_windowHost))
        {
            Host(_windowHost);
        }

        if (_second is not null && GodotObject.IsInstanceValid(_second))
        {
            _second.QueueFree();
        }

        _second = null;
    }

    /// <summary>Draws over <paramref name="host"/> from now on, at its size.</summary>
    private void Host(Viewport host)
    {
        _host = host;
        if (_layer is not null && _layer.CustomViewport != host)
        {
            _layer.CustomViewport = host;
        }

        Resize();
    }

    private void Resize()
    {
        if (!HostAlive) return;
        Vector2 v = _host!.GetVisibleRect().Size;
        var size = new Vector2I((int)MathF.Ceiling(v.X), (int)MathF.Ceiling(v.Y));
        if (size.X <= 0 || size.Y <= 0) return;
        if (_rect is not null && !Refii.AndroidRuntime.AndroidDisplacementRect.Owns(_rect) && _rect.Size != v)
        {
            _rect.Position = Vector2.Zero;
            _rect.Size = v;
        }

        float scale3D = _host.Scaling3DScale > 0f ? _host.Scaling3DScale : 1f;
        var dispSize = new Vector2I(
            Math.Max(1, (int)MathF.Ceiling(v.X * scale3D * 0.5f)),
            Math.Max(1, (int)MathF.Ceiling(v.Y * scale3D * 0.5f)));
        if (dispSize != _dispTargetSize)
        {
            _dispTargetSize = dispSize;
            _viewport.Size = dispSize;
        }

        if (size == _size) return;
        _size = size;
        float s = 0.5f * ((size.X / 1280f) + (size.Y / 720f)) * 255f * Strength;
        _apply?.SetShaderParameter("disp_pixel_scale", new Vector2(s, s));
    }

    public override void _Process(double delta)
    {
    	using var _framePhase = Refii.Core.Engine_FramePhases.Measure("fxdisplacement");
        if (!HostAlive || _apply is null) return;
        Resize();
        Camera3D? main = _host!.GetCamera3D();
        if (main is null)
        {
            _rect.Visible = false;
            if ((uint)_view < 2u) System.Threading.Volatile.Write(ref _viewActive[_view], false);
            return;
        }

        if ((main.CullMask & LayerMask) != 0) main.CullMask &= ~LayerMask;

        ulong frame = Engine.GetProcessFrames();
        bool stated = frame - _activeFrame <= 1;
        bool active = _enabled && (stated || frame - _staticFrame <= 1);
        // Awake for a static displacement mesh alone: the mesh pump tests it against the camera
        // copied below, in this frame, and may put the view back to sleep (SleepUnseen).
        _staticOnlyFrame = active && !stated ? frame : ulong.MaxValue;
        if ((uint)_view < 2u) System.Threading.Volatile.Write(ref _viewActive[_view], active);
        _rect.Visible = active;
        _viewport.RenderTargetUpdateMode = active ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
        if (!active) return;
        _camera.GlobalTransform = main.GlobalTransform;
        _camera.Projection = main.Projection;
        _camera.Fov = main.Fov;
        _camera.Size = main.Size;
        _camera.Near = main.Near;
        _camera.Far = main.Far;
        _camera.KeepAspect = main.KeepAspect;
        _camera.HOffset = main.HOffset;
        _camera.VOffset = main.VOffset;
    }
}
