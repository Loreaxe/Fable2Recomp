using System.Collections.Generic;
using Godot;

namespace Refii.Ui;

/// <summary>
/// The GUI's figure stage — the co-op screen's figures and every menu's item preview: the
/// original's separate GUI 3D world, rebuilt as a <see cref="SubViewport"/> with its own camera
/// and lights, drawn into the page as one quad.
/// </summary>
/// <remarks>
/// <para>WHAT THE ORIGINAL DOES (TU1). The figures are not canvas elements. They stand in the GUI's
/// own 3D world (`dword_8335EC18`, created in `sub_823894E0`) under a fixed perspective camera set
/// up by `sub_821A5F20`: vertical field of view 10.2°, near 0.5, far 60, at (0, −4000, 0) looking
/// along +Y with +Z up. `sub_826D6C90` places a figure from its `model` node: the node's canvas
/// translation becomes `(gx, −gz, gy) · 0.01 + (0, −3960, 0)` (`sub_822A89C0`, 0.01/0.99 at
/// 0x8209CB94/0x8209B840), so the canvas plane stands 40 m from the camera and one canvas unit
/// is a centimetre on it. The node's `rotation.y` is the figure's heading in raw radians, and its
/// `scale.x` — the only scale read — is a percentage (`· 0.01` into `comp3+108`).</para>
/// <para>REGISTERED TO THE CANVAS. The stage's camera covers exactly the canvas the UI camera
/// shows: its vertical angle is the one that puts the visible canvas height at the 40 m plane —
/// the guest's 10.2° to within a fraction of a degree at the remaster's canvas scale — so an
/// anchor's canvas point lands on the same pixel in both, and a figure stood forward of the plane
/// is foreshortened exactly as the original's was.</para>
/// <para>LIT BY THE STAGE, NOT THE WORLD. The original lights its stage with its own spots
/// (`sub_82435B98`: a white key and a cool rim at colour 13:18:20) over a 0.3 grey ambient
/// (`sub_826D5E48`). Their exact falloff parameters are not decoded, so the stage keeps the
/// arrangement — key from the front, cool rim from behind, that ambient — in Godot's terms.</para>
/// </remarks>
public partial class Engine_UiFigureStage : Node3D
{
	/// <summary>Camera to the canvas plane, in metres: (−3960) − (−4000).</summary>
	public const float CanvasPlaneDistance = 40f;

	/// <summary>Metres per canvas unit in the stage (`sub_822A89C0`'s 0.01).</summary>
	public const float MetresPerCanvasUnit = 0.01f;

	/// <summary>The authored figure size, percent (`model.scale.x` in coopscreen.bgf).</summary>
	public const float AuthoredScalePercent = 350f;

	private const float Near = 0.5f;

	private const float Far = 60f;

	private readonly SubViewport _view = new()
	{
		Name = "FigureView",
		OwnWorld3D = true,
		TransparentBg = true,
		Msaa3D = Viewport.Msaa.Msaa4X,
		RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
	};

	private readonly Camera3D _camera = new()
	{
		Name = "FigureCamera",
		Projection = Camera3D.ProjectionType.Perspective,
		KeepAspect = Camera3D.KeepAspectEnum.Height,
		Near = Near,
		Far = Far,
		Current = true,
	};

	private readonly MeshInstance3D _screen = new() { Name = "FigureScreen" };

	private readonly Dictionary<int, Figure> _figures = new();

	/// <summary>One figure: the body copy, the entity it shows and the rig it is bound to.</summary>
	private sealed class Figure
	{
		public required MeshInstance3D Body;
		public uint Key;
	}

	/// <summary>Said once per change of outcome, per slot — what the caller prints.</summary>
	public string LastReport { get; private set; } = string.Empty;

	public override void _Ready()
	{
		AddChild(_view);
		_view.AddChild(_camera);
		_view.AddChild(new WorldEnvironment
		{
			Environment = new Environment
			{
				BackgroundMode = Environment.BGMode.ClearColor,
				AmbientLightSource = Environment.AmbientSource.Color,
				AmbientLightColor = new Color(0.3f, 0.3f, 0.3f),
				AmbientLightEnergy = 1f,
				TonemapMode = Environment.ToneMapper.Linear,
			},
		});

		// Key: in front of the figures, above and to the right, white. Rim: behind them, low on the
		// left, at the original's 13:18:20.
		var key = new DirectionalLight3D { Name = "Key", LightEnergy = 1.1f, ShadowEnabled = false };
		key.LookAtFromPosition(new Vector3(1.5f, 2.5f, 2f), Vector3.Zero, Vector3.Up);
		_view.AddChild(key);
		var rim = new DirectionalLight3D
		{
			Name = "Rim",
			LightColor = new Color(13f / 20f, 18f / 20f, 1f),
			LightEnergy = 0.9f,
			ShadowEnabled = false,
		};
		rim.LookAtFromPosition(new Vector3(-1.5f, 0.5f, -2f), Vector3.Zero, Vector3.Up);
		_view.AddChild(rim);

		_screen.Mesh = new QuadMesh { Size = Vector2.One };
		_screen.MaterialOverride = new StandardMaterial3D
		{
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			CullMode = BaseMaterial3D.CullModeEnum.Disabled,
			AlbedoTexture = _view.GetTexture(),
		};
		AddChild(_screen);
		Visible = false;
	}

	/// <summary>
	/// Covers the visible canvas: <paramref name="halfWidth"/> by <paramref name="height"/> canvas
	/// units centred on this node, rendered at <paramref name="pixels"/>. Call once a frame while
	/// the stage is up.
	/// </summary>
	public void Frame(float halfWidth, float height, Vector2I pixels)
	{
		_screen.Scale = new Vector3(2f * halfWidth, height, 1f);
		if (pixels.X > 0 && pixels.Y > 0 && _view.Size != pixels)
		{
			_view.Size = pixels;
		}

		// The angle that puts `height` canvas units at the canvas plane across the full view.
		_camera.Fov = Engine_UiParticleRule.FieldOfViewDegrees(height);
	}

	/// <summary>
	/// Stands one figure. <paramref name="anchor"/> is the `model` node's canvas point in Godot's
	/// axes (z negated from the file); <paramref name="heading"/> its `rotation.y`, radians;
	/// <paramref name="scalePercent"/> its stated `scale.x`. The body is the entity feed's composed
	/// mesh bound to the feed's own skeleton, so the guest's pose comes through as the feed writes
	/// it; the feed keeps its node.
	/// </summary>
	public bool Show(
		int slot, uint key, Mesh? mesh, Skin? skin, Skeleton3D? rig, Vector3 anchor, float heading,
		float scalePercent)
	{
		if (!_figures.TryGetValue(slot, out Figure? figure))
		{
			if (mesh is null || key == 0u)
			{
				LastReport = $"slot {slot}: no body to show";
				return false;
			}

			figure = new Figure { Body = new MeshInstance3D { Name = $"Figure{slot}" } };
			_view.AddChild(figure.Body);
			_figures[slot] = figure;
		}

		// THE LAST BODY STANDS on a frame the feed has no mesh for the same entity — the preview
		// creatures drop out of the entity walk between samples and the feed retires the node.
		bool sameEntity = figure.Key == key && key != 0u;
		if (mesh is null && !sameEntity)
		{
			figure.Body.Visible = false;
			LastReport = $"slot {slot}: no body for entity 0x{key:X8}";
			return false;
		}

		if (mesh is not null && !ReferenceEquals(figure.Body.Mesh, mesh))
		{
			figure.Body.Mesh = mesh;
			int textured = Dress(figure.Body, mesh);
			LastReport = $"slot {slot}: entity 0x{key:X8} ({textured}/{mesh.GetSurfaceCount()} surface(s) textured)";
		}

		// The rig can be rebuilt a frame after the mesh, so the skin follows on its own. Without the
		// feed's Skin the copy binds by bone ORDER and a redressed body explodes into spikes.
		if (skin is not null && !ReferenceEquals(figure.Body.Skin, skin))
		{
			figure.Body.Skin = skin;
		}

		if (rig is not null && GodotObject.IsInstanceValid(rig) && rig.IsInsideTree() &&
			figure.Body.IsInsideTree())
		{
			NodePath path = figure.Body.GetPathTo(rig);
			if (figure.Body.Skeleton != path)
			{
				figure.Body.Skeleton = path;
			}
		}

		figure.Key = key;

		// The guest's placement: canvas (x, y, file z) → stage (x, y, −(40 − z·0.01)), feet at the
		// anchor, heading about the vertical, one uniform scale from `scale.x`.
		float fileZ = -anchor.Z;
		figure.Body.Position = new Vector3(
			anchor.X * MetresPerCanvasUnit,
			anchor.Y * MetresPerCanvasUnit,
			-(CanvasPlaneDistance - (fileZ * MetresPerCanvasUnit)));
		float scale = (scalePercent > 0f ? scalePercent : AuthoredScalePercent) * 0.01f;
		figure.Body.Basis = new Basis(Vector3.Up, heading).Scaled(Vector3.One * scale);
		figure.Body.Visible = true;
		Refresh();
		return true;
	}

	/// <summary>The body mesh a slot is drawing, or null.</summary>
	public Mesh? MeshOf(int slot) =>
		_figures.TryGetValue(slot, out Figure? figure) && figure.Body.Visible ? figure.Body.Mesh : null;

	/// <summary>Takes one figure down; the stage stops rendering when none stands.</summary>
	public void Hide(int slot)
	{
		if (_figures.TryGetValue(slot, out Figure? figure))
		{
			figure.Body.Visible = false;
		}

		Refresh();
	}

	/// <summary>Takes every figure down.</summary>
	public void HideAll()
	{
		foreach (Figure figure in _figures.Values)
		{
			figure.Body.Visible = false;
		}

		Refresh();
	}

	private void Refresh()
	{
		bool any = false;
		foreach (Figure figure in _figures.Values)
		{
			any |= figure.Body.Visible;
		}

		Visible = any;
		_view.RenderTargetUpdateMode = any
			? SubViewport.UpdateMode.Always
			: SubViewport.UpdateMode.Disabled;
	}

	/// <summary>Each surface of a figure dressed for the stage (Engine_UiStageMaterial).</summary>
	private static int Dress(MeshInstance3D body, Mesh mesh) => Engine_UiStageMaterial.Dress(body, mesh);

	/// <summary>
	/// A canvas transform (Godot's axes, canvas units — Engine_UiTheme.CanvasTransform) in the
	/// stage: `sub_822A89C0`'s 0.01 metres per unit, the canvas plane 40 m from the camera —
	/// exactly as <see cref="Show"/> stands a figure, rotation and scale carried whole.
	/// </summary>
	public static Transform3D StageFromCanvas(Transform3D canvas) =>
		new Transform3D(Basis.Identity.Scaled(Vector3.One * MetresPerCanvasUnit),
			new Vector3(0f, 0f, -CanvasPlaneDistance)) * canvas;

	/// <summary>
	/// Draws <paramref name="source"/>'s model on the stage in slot <paramref name="slot"/> at
	/// <paramref name="canvas"/> — where it stands, in canvas units measured from the point of the
	/// canvas plane this stage covers (<see cref="StageFromCanvas"/>). A menu's item preview keeps
	/// its own placement (the guest's offsets, axis swizzle and spin) on a hidden node and is drawn
	/// here, lit as the original lights its GUI world. The source's skin and skeleton come with it.
	/// Returns the dressing report when the mesh changed, else null.
	/// </summary>
	public string? Mirror(int slot, MeshInstance3D source, Transform3D canvas)
	{
		if (source.Mesh is not { } mesh)
		{
			Hide(slot);
			return null;
		}

		if (!_figures.TryGetValue(slot, out Figure? figure))
		{
			figure = new Figure { Body = new MeshInstance3D { Name = $"Model{slot}" } };
			_view.AddChild(figure.Body);
			_figures[slot] = figure;
		}

		string? report = null;
		if (!ReferenceEquals(figure.Body.Mesh, mesh))
		{
			figure.Body.Mesh = mesh;
			var notes = new List<string>();
			int textured = Engine_UiStageMaterial.Dress(figure.Body, mesh, notes);
			report = $"{textured}/{mesh.GetSurfaceCount()} surface(s) textured [{string.Join(" ", notes)}]";
		}

		if (!ReferenceEquals(figure.Body.Skin, source.Skin))
		{
			figure.Body.Skin = source.Skin;
		}

		if (!source.Skeleton.IsEmpty && source.GetNodeOrNull<Skeleton3D>(source.Skeleton) is { } rig &&
			rig.IsInsideTree() && figure.Body.IsInsideTree())
		{
			NodePath path = figure.Body.GetPathTo(rig);
			if (figure.Body.Skeleton != path)
			{
				figure.Body.Skeleton = path;
			}
		}

		figure.Body.Transform = StageFromCanvas(canvas);
		figure.Body.Visible = true;
		Refresh();
		return report;
	}
}
