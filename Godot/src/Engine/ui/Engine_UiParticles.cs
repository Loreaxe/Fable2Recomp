using System.Collections.Generic;
using Godot;
using Refii.Core;
using Refii.Fx;

namespace Refii.Ui;

/// <summary>
/// THE GUI GLINTS: every `ParticleEffect` element of a drawn document plays its effect on a GUI
/// stage of its own, at the element's drawn place, while the guest's rule wants it
/// (<see cref="Engine_UiParticleRule"/>) — and the one-shots a title's hooks state at an element
/// (<see cref="Burst"/>). One implementation for both titles: a view that draws a document
/// (<see cref="Engine_HudView"/>, <see cref="Engine_UiOwnedBook"/>) owns one and hands it the trees it
/// built; the title says which opacity channel its particle system has (<see cref="Channel"/>).
/// </summary>
/// <remarks>
/// <para>WHAT THE GUEST DOES. CHUDParticle (Fable 2 `CHUDParticleManager_p`, vtable TU1 0x820DA0B0 /
/// disc 0x820D6840, attribute name `"ParticleEffect"` vf3 TU1 0x827F0778 / disc 0x827CD0E8) binds one
/// component to every element carrying the attribute; each frame its update makes, places, fades and
/// kills a particle system in the GUI 3D world from the element's active bit, cumulative opacity,
/// cumulative scale.x and drawn point. In the remaster that update makes nothing (Fable 2
/// gui_element_hooks.cpp, Fable 3 stated_element_hooks.cpp, THE GLINTS): the element state it reads is
/// the document Godot draws, so the rule runs here, on the tree the view built, with the view's own
/// resolve — no guest read, no mirror of a guest instance.</para>
/// <para>THE CHANNELS. Fable 2's update writes the opacity with `sub_832323E0` (TU1; disc
/// `sub_8322BB68`), which stores it at handle +0x34 (`stfs f31,0x34(r28)` 0x832323F8 / 0x8322BB80) —
/// the instance intensity FableFx bakes into each particle at spawn (<see cref="FableFx.Intensity"/>).
/// Fable 3's writes it with `sub_831C15E8` (TU3), command 15 — the live alpha
/// (<see cref="FableFx.StatedColour"/>). Both write the scale with the size scale (F2
/// `CParticleSystemEntityInstance_SetScale`, F3 command 9 `sub_831C1828`).</para>
/// <para>THE STAGE. A transparent <see cref="SubViewport"/> with its own world under the
/// 10.2-degree camera registered to the visible canvas (the same angle as Engine_UiFigureStage),
/// drawn as one quad over the canvas just in front of the nearest live glint's element, so a page
/// standing in front of the HUD still covers the HUD's glints.</para>
/// <para>THE STAGE ONLY ADDS LIGHT. The quad adds the stage's (premultiplied) colour and writes no
/// alpha: `blend_premul_alpha` with ALPHA 0 is `dst.rgb + src.rgb` and keeps `dst.a`. The stage's
/// alpha is NOT a coverage to composite with: the additive particle shader writes a real alpha
/// (fx_particle_add.gdshader `ALPHA = mix(1.0, texel.a * COLOR.a * fade, fx_src_alpha)`) and an
/// additive draw accumulates it in the target, so a premultiplied composite that honoured it
/// multiplied the HUD under every additive glint by (1 − a) and added the glint's dim, unexposed
/// colour back (the mix and add shaders leave exposure to the world's post when
/// `fable_exposure_valid` is set) — the health orbs drew as dark discs. A glint is light on the
/// glass; the HUD at rest stays pixel-identical. Its target renders only
/// while a glint lives and is shrunk after <see cref="IdleSeconds"/> without one. The effects never
/// join the world's additive or displacement layers, which composite over the world view
/// (<see cref="FableFx.OnUiStage"/>), and step on the real clock: the world's pause does not hold
/// a box's glint.</para>
/// <para>THE KILL IS AT ONCE. The falling edge frees the effect: Fable 2's kill
/// `ParticleSystem_Kill_p` (TU1 0x83232000 / disc 0x8322B788) marks every node of the
/// instance (`sub_83228E88` / `sub_83222610`: `li r31,1 ; stb r31,0xC8(r11)` 0x83228EAC / 0x83222634),
/// and the manager's next walk takes a marked instance off its list and releases it whatever it still
/// holds — the same path as a finished one (TU1 `sub_83234338`: `lbz r9,0xC8(r31) ; cmplwi ; bne`
/// 0x83234458..0x83234460 to the erase `bl sub_82180088` 0x832344A8 and the release through +0xD0
/// vf +0x14 0x832344AC..0x832344BC; disc `sub_8322DAC0` 0x8322DBE0..0x8322DBE8, `bl sub_8217F978`
/// 0x8322DC30); its position writer skips a marked node (`lbz r11,0xC8(r31) ; bne` 0x83232604 /
/// 0x8322BD7C). No drain.</para>
/// <para>NO FRAME WAITS ON THE BANK. A driver that tracks a glint starts the shared particle bank's
/// load on a worker (<see cref="FableFx.WarmBank"/>) — the title screen shows its glints before any
/// world effect has loaded it, and the first glint used to read and parse it inside one frame (744 ms
/// in `uiglints`). Until the bank is ready (<see cref="FableFx.BankReady"/>) a wanted glint and a
/// stated one-shot wait; they start on the first frame after it.</para>
/// <para>SPLIT SCREEN. A seat's view is moved onto its half by its own transform, and every place here
/// is the element's CANVAS transform, so player two's HUD glints stand on player two's half at that
/// half's scale; each view carries its own stage.</para>
/// </remarks>
public sealed partial class Engine_UiParticles : Node3D
{
	/// <summary>Where a title's particle system takes the element's opacity.</summary>
	public enum OpacityChannel
	{
		/// <summary>Fable 2: the instance intensity (handle +0x34), baked at spawn.</summary>
		Intensity,

		/// <summary>Fable 3: the live alpha (command 15).</summary>
		Alpha,
	}

	/// <summary>The title's opacity channel.</summary>
	public OpacityChannel Channel { get; init; } = OpacityChannel.Alpha;

	/// <summary>What the log calls this driver: the view's name.</summary>
	public string Label { get; init; } = "ui";

	/// <summary>The host view's resolve for this frame (its draw gate and accumulated opacity), or null.</summary>
	public System.Func<Engine_UiElementResolve?>? Resolve { get; set; }

	/// <summary>Seconds without a live glint before the stage's target is shrunk.</summary>
	public const float IdleSeconds = 2f;

	/// <summary>Canvas units the stage's quad stands in front of the nearest live glint's element.</summary>
	public const float Clearance = 1f;

	private static readonly uint EffectAttribute = Engine_GameHash.AttributeId("ParticleEffect");
	private static readonly StringName OrdinalMeta = "ordinal";
	private static readonly StringName StageParameter = "stage";

	private const string QuadShaderCode = @"shader_type spatial;
render_mode unshaded, blend_premul_alpha, depth_draw_never, cull_disabled, shadows_disabled, fog_disabled;
uniform sampler2D stage : source_color, filter_linear, repeat_disable;
void fragment() {
	ALBEDO = texture(stage, UV).rgb;
	ALPHA = 0.0;
}
";

	/// <summary>One glint element's node in a tracked tree.</summary>
	private sealed class Anchor
	{
		public required Node3D Node { get; init; }
		public required int Ordinal { get; init; }

		/// <summary>The element whose state the node shows (a keyed piece's source), -1 for none.</summary>
		public required int Source { get; init; }

		public required string Effect { get; init; }

		/// <summary>`ordinal#occurrence`: the same element cloned twice is two glints.</summary>
		public required string Key { get; init; }
	}

	private sealed class Live
	{
		public required FableFx Fx { get; init; }
		public Vector3 At;
		public float Scale = float.NaN;
		public float Opacity = float.NaN;
	}

	private sealed class OneShot
	{
		public required FableFx Fx { get; init; }
		public float Seconds;
		public float CanvasZ;
	}

	private readonly List<(Node3D Root, System.Func<int, int>? StateOf)> _roots = new();
	private readonly List<Anchor> _anchors = new();
	private readonly Dictionary<string, Live> _alive = new(System.StringComparer.Ordinal);
	private readonly HashSet<string> _seen = new(System.StringComparer.Ordinal);
	private readonly List<string> _gone = new();
	private readonly List<OneShot> _bursts = new();
	private readonly HashSet<string> _said = new(System.StringComparer.Ordinal);

	/// <summary>One-shots stated before the bank was ready, played the first frame it is.</summary>
	private readonly List<(Vector3 At, string Effect, float Scale, string Where)> _waiting = new();

	private Engine_UiScreen? _effectsFor;
	private string?[] _effects = System.Array.Empty<string?>();

	private SubViewport? _view;
	private Camera3D? _camera;
	private MeshInstance3D? _quad;
	private float _idle;

	/// <summary>How many glint elements the tracked trees hold.</summary>
	public int Tracked => _anchors.Count;

	/// <summary>Glints and one-shots alive now.</summary>
	public int Alive => _alive.Count + _bursts.Count;

	/// <summary>
	/// The effect name each element of <paramref name="screen"/> carries in its `ParticleEffect`
	/// attribute, null where it carries none or an empty one (the component hashes "" to 0 and never
	/// creates: F2 TU1 0x8283FE24 `lwz r4,0x10(r27) ; beq`, F3 TU3 0x82BCF108 `lwz r11,0x14(r3) ; ble`).
	/// The element's own attribute: a slide entry rewrites the string to the same name, and the
	/// component's hash is fixed at its construction (F2 TU1 `sub_8283F9E8` +16, F3 TU3 0x82BCECE8 +20).
	/// </summary>
	public static string?[] EffectsOf(Engine_UiScreen screen)
	{
		Engine_BgfDocument file = screen.Document;
		var effects = new string?[screen.Nodes.Length];
		for (int i = 0; i < effects.Length && i < file.Elements.Length; ++i)
		{
			if (file.Elements[i].TryGetAttribute(EffectAttribute, out BgfAttribute attribute) &&
				attribute.Type == BgfValueType.String &&
				file.GetString(attribute.AsStringIndex()) is { Length: > 0 } name)
			{
				effects[i] = name;
			}
		}

		return effects;
	}

	/// <summary>
	/// The trees the host built for <paramref name="screen"/>, each with the map from an element to
	/// the one whose state it shows (null: its own). Called after every build; a glint whose element
	/// is in the new trees again keeps its instance — a rebuild is the host's, not the guest's.
	/// </summary>
	public void Track(Engine_UiScreen screen, IEnumerable<(Node3D Root, System.Func<int, int>? StateOf)> roots)
	{
		if (!ReferenceEquals(_effectsFor, screen))
		{
			_effectsFor = screen;
			_effects = EffectsOf(screen);
		}

		_roots.Clear();
		_anchors.Clear();
		var occurrences = new Dictionary<int, int>();
		foreach ((Node3D root, System.Func<int, int>? stateOf) in roots)
		{
			if (!GodotObject.IsInstanceValid(root))
			{
				continue;
			}

			_roots.Add((root, stateOf));
			Collect(root, stateOf, occurrences);
		}

		if (_anchors.Count > 0)
		{
			FableFx.WarmBank();
		}

		string said = $"{Label}:{_anchors.Count}";
		if (_anchors.Count > 0 && _said.Add(said))
		{
			GD.Print($"[ui-glints] {Label}: {_anchors.Count} glint element(s) in {_roots.Count} tree(s) of '{screen.Name}'");
		}
	}

	private void Collect(Node node, System.Func<int, int>? stateOf, Dictionary<int, int> occurrences)
	{
		if (node is Node3D spatial && node.HasMeta(OrdinalMeta))
		{
			int ordinal = (int)node.GetMeta(OrdinalMeta);
			if (ordinal >= 0 && ordinal < _effects.Length && _effects[ordinal] is { } effect)
			{
				int occurrence = occurrences.TryGetValue(ordinal, out int seen) ? seen + 1 : 0;
				occurrences[ordinal] = occurrence;
				_anchors.Add(new Anchor
				{
					Node = spatial,
					Ordinal = ordinal,
					Source = stateOf?.Invoke(ordinal) ?? ordinal,
					Effect = effect,
					Key = $"{ordinal}#{occurrence}",
				});
			}
		}

		for (int i = 0, count = node.GetChildCount(); i < count; ++i)
		{
			Collect(node.GetChild(i), stateOf, occurrences);
		}
	}

	/// <summary>
	/// A ONE-SHOT a title's hook stated at an element (Fable 3 `sub_823CE408`(name, element, scale),
	/// `@Ui.Particle`): <paramref name="effect"/> at the drawn place of the node that shows element
	/// <paramref name="ordinal"/>'s STATE — its own node, or a keyed piece standing for it (player two's
	/// view draws player one's widget keyed to player two's elements) — and only a drawn one, so each
	/// player's one-shot lands on the view that draws that player's widget. Size scale
	/// <paramref name="scale"/>, placed once and left to its own lifetime: the guest's `sub_823CE480`
	/// sets the position once and never kills it. False when no tracked tree draws the element.
	/// </summary>
	public bool Burst(int ordinal, string effect, float scale)
	{
		if (effect.Length == 0 || Find(ordinal) is not { } node)
		{
			return false;
		}

		return BurstAt(Engine_UiTheme.CanvasTransform(node).Origin, effect, scale, $"element #{ordinal}");
	}

	/// <summary>
	/// A ONE-SHOT a title's hook stated at a CANVAS point (Fable 3 `sub_823CE480`(name, x, −z, y,
	/// scale), `@Ui.Particle2d`): the whole canvas's coordinates, whatever seat this view stands in.
	/// </summary>
	public bool BurstAt(Vector3 canvasPoint, string effect, float scale, string at = "a stated point")
	{
		if (effect.Length == 0)
		{
			return false;
		}

		if (!FableFx.BankReady)
		{
			FableFx.WarmBank();
			_waiting.Add((canvasPoint, effect, scale, at));
			return true;
		}

		var canvas = new Transform3D(Basis.Identity, canvasPoint);
		EnsureStage();
		FableFx fx = Make(effect, $"Burst{_bursts.Count}");
		Place(fx, canvas.Origin);
		fx.SizeScale = scale;
		_bursts.Add(new OneShot { Fx = fx, CanvasZ = canvas.Origin.Z });
		if (_said.Add("burst:" + effect))
		{
			GD.Print($"[ui-glints] {Label}: one-shot '{effect}' at {at} scale {scale:0.###}");
		}

		return true;
	}

	/// <summary>The first DRAWN node in the tracked trees whose state source is <paramref name="ordinal"/>.</summary>
	private Node3D? Find(int ordinal)
	{
		foreach ((Node3D root, System.Func<int, int>? stateOf) in _roots)
		{
			if (GodotObject.IsInstanceValid(root) && root.IsInsideTree() && FindIn(root, ordinal, stateOf) is { } found)
			{
				return found;
			}
		}

		return null;
	}

	private static Node3D? FindIn(Node node, int ordinal, System.Func<int, int>? stateOf)
	{
		if (node is Node3D spatial && node.HasMeta(OrdinalMeta))
		{
			int own = (int)node.GetMeta(OrdinalMeta);
			if ((stateOf?.Invoke(own) ?? own) == ordinal && spatial.IsVisibleInTree())
			{
				return spatial;
			}
		}

		for (int i = 0, count = node.GetChildCount(); i < count; ++i)
		{
			if (FindIn(node.GetChild(i), ordinal, stateOf) is { } found)
			{
				return found;
			}
		}

		return null;
	}

	public override void _Process(double delta)
	{
		using var _framePhase = Engine_FramePhases.Measure("uiglints");
		Step((float)delta);
	}

	private void Step(float delta)
	{
		if (_waiting.Count > 0 && FableFx.BankReady)
		{
			var waited = new List<(Vector3 At, string Effect, float Scale, string Where)>(_waiting);
			_waiting.Clear();
			foreach ((Vector3 at, string effect, float scale, string where) in waited)
			{
				BurstAt(at, effect, scale, where);
			}
		}

		if (_anchors.Count == 0 && _alive.Count == 0 && _bursts.Count == 0)
		{
			Idle(delta);
			return;
		}

		Engine_UiElementResolve? resolve = Resolve?.Invoke();
		float halfWidth = Engine_UiSceneBuilder.VisibleHalfWidth;
		float halfHeight = 0.5f * Engine_UiSceneBuilder.CanvasHeight / Engine_UiSceneBuilder.PixelsPerUnit;
		float front = float.NegativeInfinity;
		_seen.Clear();

		foreach (Anchor anchor in _anchors)
		{
			bool valid = GodotObject.IsInstanceValid(anchor.Node) && anchor.Node.IsInsideTree();
			Transform3D canvas = valid ? Engine_UiTheme.CanvasTransform(anchor.Node) : Transform3D.Identity;
			int source = anchor.Source;
			bool drawn = valid && source >= 0 && anchor.Node.IsVisibleInTree() &&
				(resolve is null || source >= resolve.Drawn.Length || resolve.Drawn[source]);
			float opacity = resolve is not null && source >= 0 && source < resolve.Opacity.Length
				? resolve.Opacity[source]
				: 1f;
			float scale = valid ? canvas.Basis.X.Length() : 0f;
			bool onScreen = valid && Engine_UiParticleRule.OnCanvas(canvas.Origin.X, canvas.Origin.Y, halfWidth, halfHeight);
			bool wanted = Engine_UiParticleRule.Wanted(drawn, opacity, scale, onScreen);
			bool alive = _alive.TryGetValue(anchor.Key, out Live? live);

			switch (Engine_UiParticleRule.Step(alive, wanted))
			{
				case Engine_UiParticleRule.Edge.Create when !FableFx.BankReady:
					// Waits for the warm-up: wanted stays, alive stays false, the edge comes again.
					FableFx.WarmBank();
					break;
				case Engine_UiParticleRule.Edge.Create:
					EnsureStage();
					live = new Live { Fx = Make(anchor.Effect, "Glint" + anchor.Key.Replace('#', '_')) };
					_alive[anchor.Key] = live;
					if (_said.Add("glint:" + anchor.Effect))
					{
						GD.Print($"[ui-glints] {Label}: '{anchor.Effect}' up on element #{anchor.Ordinal} " +
							$"(opacity {Engine_UiParticleRule.Opacity01(opacity):0.##}, scale {scale:0.###})");
					}

					break;
				case Engine_UiParticleRule.Edge.Kill:
					Retire(live!.Fx);
					_alive.Remove(anchor.Key);
					live = null;
					break;
			}

			if (live is null)
			{
				continue;
			}

			_seen.Add(anchor.Key);
			front = Mathf.Max(front, canvas.Origin.Z);
			if (live.At != canvas.Origin || float.IsNaN(live.Scale))
			{
				live.At = canvas.Origin;
				Place(live.Fx, canvas.Origin);
			}

			if (live.Scale != scale)
			{
				live.Scale = scale;
				live.Fx.SizeScale = scale;
			}

			float shown = Engine_UiParticleRule.Opacity01(opacity);
			if (live.Opacity != shown)
			{
				live.Opacity = shown;
				if (Channel == OpacityChannel.Alpha)
				{
					live.Fx.StatedColour = new Color(1f, 1f, 1f, shown);
				}
				else
				{
					live.Fx.Intensity = shown;
				}
			}
		}

		// An instance whose element left the tracked trees (a rebuild without it, a document gone):
		// its component is gone with the element.
		_gone.Clear();
		foreach (string key in _alive.Keys)
		{
			if (!_seen.Contains(key))
			{
				_gone.Add(key);
			}
		}

		foreach (string key in _gone)
		{
			Retire(_alive[key].Fx);
			_alive.Remove(key);
		}

		for (int i = _bursts.Count - 1; i >= 0; --i)
		{
			OneShot burst = _bursts[i];
			burst.Seconds += delta;
			if (!GodotObject.IsInstanceValid(burst.Fx) ||
				Engine_UiParticleRule.BurstDone(burst.Seconds, burst.Fx.DebugSpawned, burst.Fx.DebugAlive))
			{
				Retire(burst.Fx);
				_bursts.RemoveAt(i);
				continue;
			}

			front = Mathf.Max(front, burst.CanvasZ);
		}

		if (_alive.Count == 0 && _bursts.Count == 0)
		{
			Idle(delta);
			return;
		}

		_idle = 0f;
		Frame(front + Clearance);
		FableFx.LapAt = System.Diagnostics.Stopwatch.GetTimestamp();
		foreach (Live live in _alive.Values)
		{
			live.Fx.Step(delta, _camera);
		}

		foreach (OneShot burst in _bursts)
		{
			burst.Fx.Step(delta, _camera);
		}
	}

	private FableFx Make(string effect, string name)
	{
		var fx = new FableFx
		{
			Name = name,
			EffectName = effect,
			OnUiStage = true,
			// The guest's GUI instances loop by their own emitter lifetimes, as every live effect
			// does (GameFx.LiveFxDefaultLoop), and light nothing.
			Loop = false,
			SuppressLights = true,
		};
		_view!.AddChild(fx);
		return fx;
	}

	private static void Place(FableFx fx, Vector3 canvas)
	{
		(float x, float y, float z) = Engine_UiParticleRule.StagePoint(canvas.X, canvas.Y);
		(float X, float Y, float Z)[] c = Engine_UiParticleRule.StageBasis;
		fx.Transform = new Transform3D(
			new Basis(new Vector3(c[0].X, c[0].Y, c[0].Z), new Vector3(c[1].X, c[1].Y, c[1].Z), new Vector3(c[2].X, c[2].Y, c[2].Z)),
			new Vector3(x, y, z));
	}

	private static void Retire(FableFx fx)
	{
		if (GodotObject.IsInstanceValid(fx))
		{
			fx.QueueFree();
		}
	}

	private void EnsureStage()
	{
		if (_view is not null)
		{
			return;
		}

		_view = new SubViewport
		{
			Name = "GlintView",
			OwnWorld3D = true,
			TransparentBg = true,
			RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
		};
		AddChild(_view);
		_camera = new Camera3D
		{
			Name = "GlintCamera",
			Projection = Camera3D.ProjectionType.Perspective,
			KeepAspect = Camera3D.KeepAspectEnum.Height,
			Near = 0.5f,
			Far = 60f,
			Current = true,
		};
		_view.AddChild(_camera);
		_view.AddChild(new WorldEnvironment
		{
			Environment = new Godot.Environment
			{
				BackgroundMode = Godot.Environment.BGMode.ClearColor,
				TonemapMode = Godot.Environment.ToneMapper.Linear,
			},
		});

		var material = new ShaderMaterial { Shader = new Shader { Code = QuadShaderCode } };
		material.SetShaderParameter(StageParameter, _view.GetTexture());
		_quad = new MeshInstance3D
		{
			Name = "GlintScreen",
			Mesh = new QuadMesh { Size = Vector2.One },
			MaterialOverride = material,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			Visible = false,
		};
		AddChild(_quad);
	}

	/// <summary>Covers the visible canvas at canvas depth <paramref name="z"/> and renders the stage.</summary>
	private void Frame(float z)
	{
		if (_view is null || _camera is null || _quad is null || GetParent() is not Node3D parent || GetViewport() is not { } viewport)
		{
			return;
		}

		float height = Engine_UiSceneBuilder.CanvasHeight / Engine_UiSceneBuilder.PixelsPerUnit;
		float halfWidth = Engine_UiSceneBuilder.VisibleHalfWidth;

		// In canvas space, whatever seat transform the host stands under: the stage covers the whole
		// visible canvas, and each glint stands at its element's canvas point.
		Transform = Engine_UiTheme.CanvasTransform(parent).AffineInverse() *
			new Transform3D(Basis.Identity, new Vector3(0f, 0f, z));
		_quad.Scale = new Vector3(2f * halfWidth, height, 1f);
		_quad.Visible = true;
		_camera.Fov = Engine_UiParticleRule.FieldOfViewDegrees(height);

		Vector2I pixels = (Vector2I)viewport.GetVisibleRect().Size;
		if (pixels.X > 0 && pixels.Y > 0 && _view.Size != pixels)
		{
			_view.Size = pixels;
		}

		_view.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
	}

	private void Idle(float delta)
	{
		if (_view is null)
		{
			return;
		}

		if (_quad is not null && _quad.Visible)
		{
			_quad.Visible = false;
			_view.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
		}

		_idle += delta;
		if (_idle >= IdleSeconds && _view.Size != new Vector2I(2, 2))
		{
			_view.Size = new Vector2I(2, 2);
		}
	}

	public override void _ExitTree()
	{
		foreach (Live live in _alive.Values)
		{
			Retire(live.Fx);
		}

		foreach (OneShot burst in _bursts)
		{
			Retire(burst.Fx);
		}

		_alive.Clear();
		_bursts.Clear();
	}
}
