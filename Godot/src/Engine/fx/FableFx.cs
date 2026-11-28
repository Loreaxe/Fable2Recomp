using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Refii.Models;
using Refii.Textures;

namespace Refii.Fx;

/// <summary>
/// The Modern renderer's particle effect scene node, driving <see cref="Engine_FxSystem"/>.
/// </summary>
/// <remarks>
/// SEPARATION OF CONCERNS BETWEEN RENDERER MODES.
/// - Modern mode (<c>Engine_ClassicMaterial.Active == false</c>) uses <see cref="FableFx"/> and
///   <see cref="Engine_FxSystem"/> exclusively across the full 0..200m range (for both live guest
///   effects via <c>GameFx</c> and placed level effects via <c>Engine_LevelFx</c>), with 30 Hz
///   simulation, GPU sub-step interpolation, and unclamped linear HDR colours.
/// - Classic mode (<c>Engine_ClassicMaterial.Active == true</c>) uses
///   <see cref="Refii.Models.Engine_ClassicParticles"/> exclusively.
/// - Both systems are kept strictly separate at runtime while sharing pure static math and
///   keyframe-table helpers on <see cref="Engine_FxSystem"/>.
/// </remarks>
[Tool]
[GlobalClass]
public partial class FableFx : Node3D
{
	/// <summary>Where the running segment of <see cref="FrameWork"/> began; see <see cref="Step"/>.</summary>
	internal static long LapAt;

	/// <summary>Effect segments summed over all systems once per driver frame.</summary>
	internal static readonly Refii.Core.Engine_FrameWork FrameWork = new("fx.build", "fx.cull", "fx.sim", "fx.light", "fx.geometry", "fx.submit", "fx.assemble", "fx.flush", "fx.pack", "fx.upload", "fx.sort", "fx.bounds");

	private static readonly StringName AlbedoTexName = "albedo_tex";
	private static readonly StringName FxAlphaRefName = "fx_alpha_ref";
	private static readonly StringName FxSoftInvName = "fx_soft_inv";
	private static readonly StringName FxSrcAlphaName = "fx_src_alpha";
	private static readonly StringName FxTexName = "fx_tex";

	public const string BankPath = "art/particles/particle_bank.bnk";

	public const string MixShaderPath = "res://src/Engine/fx/fx_particle.gdshader";

	public const string AddShaderPath = "res://src/Engine/fx/fx_particle_add.gdshader";

	public const string MixAlphaShaderPath = "res://src/Engine/fx/fx_particle_mixalpha.gdshader";

	public const string DarkenShaderPath = "res://src/Engine/fx/fx_particle_darken.gdshader";

	public const string SubShaderPath = "res://src/Engine/fx/fx_particle_sub.gdshader";

	public const string DispShaderPath = "res://src/Engine/fx/fx_particle_disp.gdshader";

	public const string AddLayerShaderPath = "res://src/Engine/fx/fx_particle_addone.gdshader";

	public const string TextureFolder = "art/fx/pfx_textures/";

	[Export]
	public float ExposureScale { get; set; } = 1.0f;

	static FableFx()
	{
		Engine_FxDepthCaptureEffect.RegisterGlobals();
	}

	private static FxBank? _bank;
	private static bool _bankTried;

	/// <summary>The shipped bank's bytes, kept so a changed mod set re-overlays without re-reading the archive.</summary>
	private static byte[]? _bankBytes;

	/// <summary>The <see cref="Engine_FxModEffects.Generation"/> the shared bank was overlaid at.</summary>
	private static int _bankOverlay = -1;

	/// <summary>A bank handed over whole (<see cref="UseBank"/>): drawn as given, no mod overlay.</summary>
	private static bool _referenceBank;

	/// <summary>
	/// A reference folder of loose <c>.tex</c> files tried before the mount for effect textures,
	/// and a particle bank taken over the mount's: the weave lab's way of drawing another
	/// title's effects (Fable 3's weave twisters) as the look to match. Unset in the game.
	/// </summary>
	public static string ReferenceTextureDirectory { get; set; } = string.Empty;

	/// <summary>Replaces the shared particle bank with <paramref name="bytes"/>; false when it does not parse.</summary>
	public static bool UseBank(byte[] bytes, out string error)
	{
		var bank = new FxBank();
		if (!Engine_FxBank.Parse(bytes, bank))
		{
			error = bank.Error;
			return false;
		}

		_bank = bank;
		_bankTried = true;
		_referenceBank = true;
		error = string.Empty;
		GD.Print($"[fx] reference bank loaded: {bank.Effects.Count} effects, {bank.Emitters.Count} emitters, {bank.Systems.Count} systems");
		if (bank.ExactModel is { } x)
		{
			GD.Print($"[fx] bank sections: materials 0x{x.Section2Offset:X} ({x.Materials.Count}), systems 0x{x.Section3Offset:X} ({x.Systems.Count}), entities 0x{x.Section4Offset:X} ({x.EntityOrder.Count}), section5 0x{x.Section5Offset:X}");
			for (int i = 0; i < Math.Min(4, x.EntityOrder.Count); i++)
			{
				Xex.XexEntityRef r = x.EntityOrder[i];
				Xex.XexEntityType? e = r.TypeId switch { 0 => x.StdEmitters[r.Index], 3 => x.Orbitals[r.Index], 4 => x.Attractors[r.Index], 5 => x.SplineEntities[r.Index], 6 => x.Roots[r.Index], 7 => x.PointLights[r.Index], 8 => x.SpotLights[r.Index], 9 => x.SplineEmitters[r.Index], _ => null };
				GD.Print($"[fx]   entity {i}: type {r.TypeId} sub {r.SubType} id 0x{r.EntityId:X8} bytes 0x{e?.FileBegin:X}..0x{e?.FileEnd:X}");
			}
		}
		return true;
	}

	private readonly Engine_FxSystem _system = new();
	private readonly Dictionary<string, VisualBatchMaterial> _materials = new();
	private readonly Dictionary<string, Engine_FxSystem.MeshInstanceData> _meshData = new();
	private readonly Dictionary<string, MultiMeshInstance3D> _meshNodes = new();

	/// <summary>How many of <see cref="_meshNodes"/> are displacement meshes: the only ones given a layer instance.</summary>
	private int _dispMeshNodes;

	private MeshInstance3D? _surface;
	private ArrayMesh? _mesh;

	/// <summary>The state the geometry was last emitted for: camera position (this node's space)
	/// and which layers it reached.</summary>
	private bool _emitted;
	private Vector3 _emitPos;
	private bool _emitAddNull = true;
	private float _emitRibbonIntensity = -1f;
	private bool _emitMainActive;
	private bool _emitDispActive;
	private bool _emitAddActive;
	private Engine_FxLayerInstance? _dispInstance;
	private ArrayMesh? _dispMesh;

	/// <summary>Set while the guest's FX mesh twins are drawn (Engine_FxMeshTwins, modern renderer).</summary>
	public static bool GuestMeshTwins;

	/// <summary>
	/// Set while the guest draws this effect's particle groups itself (Engine_ClassicParticles,
	/// modern renderer; GameFx joins them by owner): its quads, ribbons and additive batch stand
	/// down, its lights and displacement stay. An effect the guest is not drawing — every one
	/// past its own reach — keeps drawing here.
	/// </summary>
	public bool GuestDrawn { get; set; }
	private bool _emitGuest;

	/// <summary>
	/// Drawn on a GUI stage (Engine_UiParticles): in the stage's own world, stepped by the stage with
	/// its camera on the real clock. It joins no driver, freezes for no world view, samples no scene
	/// light and puts nothing on the additive or displacement layers, which composite over the world
	/// view. Set before the node enters the tree.
	/// </summary>
	public bool OnUiStage { get; set; }

	private Engine_FxLayerInstance? _addInstance;
	private ArrayMesh? _addMesh;

	/// <summary>Displacement mesh effects: instances in the displacement layer's world, by path;
	/// the node in <see cref="_meshNodes"/> is kept for its MultiMesh and never enters the tree.</summary>
	private readonly Dictionary<string, Engine_FxLayerInstance> _meshInstances = new();
	private bool _built;
	private string _builtName = string.Empty;
	private bool _hasDisplacement;
	private bool _hasMeshParticles;

	private struct FxLightCache
	{
		public bool Applied;
		public bool Visible;
		public Vector3 Position;
		public float OmniRange;
		public Color Colour;
		public float LightEnergy;
		public bool DistanceFadeEnabled;
		public float DistanceFadeBegin;
		public float DistanceFadeLength;
	}

	/// <summary>
	/// One <see cref="OmniLight3D"/> per <c>CPointLightEntityType</c> the effect carries.
	///
	/// These are why a torch now lights the wall behind it. 72 of the bank's entities are lights
	/// and every one was parsed and discarded before — the flame drew and lit nothing.
	/// </summary>
	private readonly List<OmniLight3D> _lights = new();
	private readonly List<FxLightCache> _lightCache = new();

	private string _effectName = string.Empty;

	[Export]
	public string EffectName
	{
		get => _effectName;
		set
		{
			_effectName = value ?? string.Empty;
			_built = false;
		}
	}

	[Export]
	public bool Loop { get; set; } = true;

	[Export]
	public bool Emitting { get; set; } = true;

	/// <summary>When true (e.g. retired nodes draining remaining particles), effect lights stay hidden without culling particle decay.</summary>
	public bool SuppressLights { get; set; }

	[Export]
	public bool Playing { get; set; } = true;

	[Export]
	public float EffectScale { get; set; } = 1f;

	/// <summary>
	/// The effect instance's intensity as the guest states it (<c>handle+52</c>, 1.0 untouched).
	/// Baked into each particle at spawn, as the guest does, so a change travels through the
	/// effect at the particles' own lifetime.
	/// </summary>
	public float Intensity { get; set; } = 1f;

	/// <summary>
	/// The instance's size scale as the guest states it (element+412 via `SetScale`, 1.0
	/// untouched): a multiplier on every keyframe's global scale, so it applies live.
	/// </summary>
	public float SizeScale { get; set; } = 1f;

	/// <summary>An FX recipe's dressing (<see cref="Engine_FxRecipes"/>): a colour multiplier
	/// on every particle, and multipliers on the stated intensity and size. White and 1 untouched.</summary>
	public Color Tint { get; set; } = Colors.White;

	public float RecipeIntensity { get; set; } = 1f;

	/// <summary>
	/// The effect's colour as the guest states it (Fable 3's SetColour / SetAlpha, inst+80), applied
	/// at every draw as the guest's own draw does: red, green, blue on the particle colour beside
	/// <see cref="Tint"/>, alpha on the particle alpha. White untouched.
	/// </summary>
	public Color StatedColour { get; set; } = Colors.White;

	/// <summary>
	/// Particle flushes the guest has stated for this effect (Fable 3's opcode 22): each new one
	/// kills every live particle of the standard emitters; emission goes on.
	/// </summary>
	public uint StatedFlushes
	{
		get => _statedFlushes;
		set
		{
			if (value != _statedFlushes)
			{
				_statedFlushes = value;
				if (_built)
				{
					_system.FlushParticles();
				}
			}
		}
	}

	private uint _statedFlushes;

	/// <summary>
	/// The guest turned the saturation mask on for this effect's standard emitters (Fable 3's
	/// opcode 10, hit impacts). Applied once, after the build.
	/// </summary>
	public bool StatedSaturationMask { get; set; }

	private bool _saturationApplied;

	public float RecipeSize { get; set; } = 1f;

	/// <summary>
	/// The kill fade, frames left over frames total (1.0 until a fade-out kill). The guest's FX
	/// light multiplies its intensity by it (`sub_8323FA50` 0x8323fb14 `bl sub_83228B48`,
	/// 0x8323fb20 `fmuls f7, f8, f1`, f1 = +0xC4 / +0xC0), and the kill `sub_83232048` →
	/// `sub_83228F00` writes that pair on EVERY node down the chain, lights included. The
	/// effect's alpha (`handle+52`) does not reach a light: SetAlpha writes type 0/9 only.
	/// </summary>
	public float LightFade { get; set; } = 1f;

	/// <summary>
	/// Overrides the control points of any Type 5 spline chain node (<c>CParticleSplineNode</c>)
	/// in this effect with runtime endpoints in this node's local Godot space (used by multi-dummy
	/// weapon effects such as <c>FX_64D9750B</c> and <c>FX_B0BF62DC</c>).
	/// </summary>
	public void SetSplineOverride(ReadOnlySpan<Vector3> nodeLocalPoints)
	{
		EnsureBuilt();
		_system.SetSplineOverride(nodeLocalPoints);
	}

	/// <summary>True when this effect contains at least one ribbon emitter (<c>Mode == 2</c> or <c>3</c>).</summary>
	public bool HasRibbon => _built && _system.HasRibbon;

	/// <summary>True when this effect contains a <c>Mode == 3</c> melee weapon double-trail emitter.</summary>
	public bool IsWeaponDoubleTrail => _built && _system.IsWeaponDoubleTrail;

	/// <summary>True when this effect contains at least one <c>Mode == 3</c> double-trail emitter.</summary>
	public bool HasDoubleTrail => _built && _system.HasDoubleTrail;

	/// <summary>
	/// When true, live <c>Mode == 3</c> double trails suppress spawning until <see cref="SetDoubleTrailSpan"/>
	/// supplies a valid endpoint pair rather than emitting an arbitrary vertical fallback quad.
	/// </summary>
	public bool RequireDoubleTrailSpan
	{
		get => _system.RequireDoubleTrailSpan;
		set => _system.RequireDoubleTrailSpan = value;
	}

	/// <summary>
	/// When true, guest-driven <c>Mode == 2</c> single trails suppress spawning until
	/// <see cref="SetSingleTrailHead"/> supplies a valid head point.
	/// </summary>
	public bool RequireSingleTrailHead
	{
		get => _system.RequireSingleTrailHead;
		set => _system.RequireSingleTrailHead = value;
	}

	/// <summary>
	/// When true, live <c>Type 9</c> beam/spline emitters suppress fallback 3m bars until a guest
	/// spline baseline is supplied via <see cref="SetBeamSplineForType"/> or <see cref="SetSplineOverride"/>.
	/// </summary>
	public bool RequireBeamSpline
	{
		get => _system.RequireBeamSpline;
		set => _system.RequireBeamSpline = value;
	}

	/// <summary>True when this effect contains at least one <c>Type 9</c> spline/beam emitter.</summary>
	public bool HasBeam
	{
		get
		{
			EnsureBuilt();
			return _system.HasBeam;
		}
	}

	/// <summary>Number of <c>Type 9</c> spline/beam emitters in this effect.</summary>
	public int BeamEmitterCount
	{
		get
		{
			EnsureBuilt();
			return _system.BeamEmitterCount;
		}
	}

	/// <summary>Sets or clears the runtime spline points (in node-local Godot space) for a <c>Type 9</c> beam emitter.</summary>
	public int SetBeamSplineForType(uint emitterEntityId, int occurrence, ReadOnlySpan<Vector3> nodeLocalPoints)
	{
		EnsureBuilt();
		return _system.SetBeamSplineForType(emitterEntityId, occurrence, nodeLocalPoints);
	}

	/// <summary>Sets or clears the runtime spline points (in node-local Godot space) by beam index.</summary>
	public void SetBeamSpline(int beamIndex, ReadOnlySpan<Vector3> nodeLocalPoints)
	{
		EnsureBuilt();
		_system.SetBeamSpline(beamIndex, nodeLocalPoints);
	}

	/// <summary>Clears all per-beam spline overrides on this effect.</summary>
	public void ClearBeamSplines()
	{
		if (_built)
		{
			_system.ClearBeamSplines();
		}
	}

	/// <summary>Updates per-chain (<c>part.Vector</c>) placement roots in node-local Godot space.</summary>
	public void SetChainRoots(ReadOnlySpan<Transform3D> nodeLocalRoots)
	{
		EnsureBuilt();
		_system.SetChainRoots(nodeLocalRoots);
	}

	/// <summary>
	/// Sets the two world-aligned blade/span endpoints in this node's local Godot space for
	/// host-driven <c>Mode == 3</c> double-trail spawning, optionally carrying guest ring cursor state.
	/// </summary>
	public void SetDoubleTrailSpan(
		Vector3 nodeLocalEdgeA,
		Vector3 nodeLocalEdgeB,
		bool hasGuestCursor = false,
		uint guestTail = 0u,
		uint guestCount = 0u)
	{
		EnsureBuilt();
		_system.SetDoubleTrailSpan(nodeLocalEdgeA, nodeLocalEdgeB, hasGuestCursor, guestTail, guestCount);
	}

	/// <summary>Clears any host-supplied double-trail blade span, reverting to emitter-basis fallback.</summary>
	public void ClearDoubleTrailSpan()
	{
		if (_built)
		{
			_system.ClearDoubleTrailSpan();
		}
	}

	/// <summary>
	/// Supplies the complete guest <c>Mode == 3</c> double-trail ring (oldest to newest, in this
	/// node's local Godot space) with exact per-slot <c>KeyTime</c> values.
	/// </summary>
	public void SetDoubleTrailRing(ReadOnlySpan<Engine_FxSystem.DoubleTrailRingPoint> nodeLocalPoints)
	{
		EnsureBuilt();
		_system.SetDoubleTrailRing(nodeLocalPoints);
	}

	/// <summary>Clears any direct guest <c>Mode == 3</c> double-trail ring override.</summary>
	public void ClearDoubleTrailRing()
	{
		if (_built)
		{
			_system.ClearDoubleTrailRing();
		}
	}

	/// <summary>
	/// Sets the guest-driven <c>Mode == 2</c> single-trail head point in this node's local Godot space.
	/// </summary>
	public void SetSingleTrailHead(
		Vector3 nodeLocalHead,
		bool hasGuestCursor = false,
		uint guestTail = 0u,
		uint guestCount = 0u)
	{
		EnsureBuilt();
		_system.SetSingleTrailHead(nodeLocalHead, hasGuestCursor, guestTail, guestCount);
	}

	/// <summary>Clears any guest-driven single-trail head point.</summary>
	public void ClearSingleTrailHead()
	{
		if (_built)
		{
			_system.ClearSingleTrailHead();
		}
	}

	/// <summary>Flushes active ribbon particles (<c>Mode == 2</c> and/or <c>Mode == 3</c>) on guest reset.</summary>
	public void ResetTrails(int modeFilter = 0)
	{
		if (_built)
		{
			_system.ResetTrails(modeFilter);
		}
	}

	/// <summary>
	/// This effect's lights for the debug channel: the simulation's stated energy and radius
	/// beside what the node was given.
	/// </summary>
	public IEnumerable<string> DescribeLights(Vector3 eye)
	{
		for (int i = 0; i < _lights.Count; i++)
		{
			OmniLight3D light = _lights[i];
			Engine_FxSystem.FxLightState state = _system.LightAt(i);
			Vector3 p = light.GlobalPosition;
			Color c = Refii.Lighting.Engine_LightColour.Linear(light);
			yield return
				$"{EffectName}#{i} d={eye.DistanceTo(p):0.0} at=({p.X:0.0},{p.Y:0.0},{p.Z:0.0}) " +
				$"stated energy={state.Energy:0.00} radius={state.Range:0.0} cutoff={state.Cutoff:0} ramp={state.Ramp:0} | " +
				$"node energy={light.LightEnergy:0.00} range={light.OmniRange:0.0} fade={LightFade:0.00} " +
				$"exposureScale={ExposureScale:0.00} colour=({c.R:0.00},{c.G:0.00},{c.B:0.00}) " +
				$"specular={light.LightSpecular:0.0} visible={light.Visible}";
		}
	}

	private static ulong _bankRetryAtMsec;

	/// <summary>
	/// THE particle bank every effect draws from: the shipped bank with the enabled mods' effects
	/// overlaid (<see cref="Engine_FxModEffects"/>). A mount that changes the mods' effects steps
	/// their generation and the next call overlays again on a fresh parse; nodes already built keep
	/// what they built.
	/// </summary>
	public static FxBank? SharedBank()
	{
		AdoptWarmed();
		if (_bank is not null &&
			(_referenceBank || _bankOverlay == Engine_FxModEffects.Generation || _bankBytes is null))
		{
			return _bank;
		}

		if (_bank is not null && _bankBytes is not null && Overlaid(_bankBytes) is { } fresh)
		{
			_bank = fresh;
			return _bank;
		}

		ulong now = Time.GetTicksMsec();
		if (_bankTried && now < _bankRetryAtMsec)
		{
			return null;
		}

		_bankTried = true;
		_bankRetryAtMsec = now + 2000;
		byte[]? bytes = ShippedBankBytes();
		if (bytes is null)
		{
			GD.Print($"[fx] particle bank not found at {BankPath}");
			return null;
		}

		FxBank? bank = Overlaid(bytes);
		if (bank is null)
		{
			return null;
		}

		ReportLoaded(bank);
		_bankBytes = bytes;
		_bank = bank;
		return bank;
	}

	/// <summary>
	/// The SHIPPED bank's bytes: with mods stating effects the mount serves the guest's composed bank
	/// under this name (Engine_FxModEffects.ComposeGuestBank), and the shared bank overlays them itself.
	/// Safe off the main thread — the texture prefetch's worker reads through the same resolver.
	/// </summary>
	private static byte[]? ShippedBankBytes()
	{
		byte[]? bytes = Engine_FxModEffects.ShippedBank ??
						Engine_BnkMeshLoader.ReadBytes(BankPath) ??
						Engine_BnkMeshLoader.ReadBytes("data/" + BankPath);
		if (bytes is null)
		{
			string root = Refii.Core.Engine_GameData.Resolve();
			if (root.Length != 0)
			{
				foreach (string candidate in new[]
						 {
							 System.IO.Path.Combine(
								 root, BankPath.Replace('/', System.IO.Path.DirectorySeparatorChar)),
							 System.IO.Path.Combine(
								 root, "data",
								 BankPath.Replace('/', System.IO.Path.DirectorySeparatorChar)),
						 })
				{
					if (System.IO.File.Exists(candidate))
					{
						bytes = System.IO.File.ReadAllBytes(candidate);
						break;
					}
				}
			}
		}

		return bytes;
	}

	private static void ReportLoaded(FxBank bank) =>
		GD.Print(
			$"[fx] bank loaded: {bank.Effects.Count} effects, " +
			$"{bank.Emitters.Count} emitters, {bank.Systems.Count} systems; " +
			$"{bank.Effects.Count(e => e.UpdateFrequency == 0)} authored in the 15 Hz group" +
			(bank.FixedThirtyHertz ? " (stepped at 30 Hz: this title has no such group)" : string.Empty));

	/// <summary>A warm-up running or finished and not yet adopted (<see cref="WarmBank"/>).</summary>
	private static System.Threading.Tasks.Task<(FxBank? Bank, byte[]? Bytes, int Generation)>? _warming;

	/// <summary>
	/// THE BANK, WARMED OFF THE FRAME. Reads and parses the shared bank on a worker — the same bytes,
	/// parse and mod overlay <see cref="SharedBank"/> runs — so a screen that shows effects before any
	/// world effect has loaded the bank (the title screen's glints) does not stall a frame on it; the
	/// next <see cref="SharedBank"/> adopts the result. Once; nothing when the bank is loaded already.
	/// </summary>
	public static void WarmBank()
	{
		if (_bank is not null || _referenceBank || _warming is not null)
		{
			return;
		}

		_warming = System.Threading.Tasks.Task.Run(() =>
		{
			byte[]? bytes = ShippedBankBytes();
			int generation = -1;
			FxBank? bank = bytes is null ? null : Composed(bytes, out generation);
			return (bank, bytes, generation);
		});
	}

	/// <summary>
	/// Whether <see cref="SharedBank"/> answers now without reading and parsing the bank on this
	/// thread: loaded, or warmed. A caller that must not stall the frame (Engine_UiParticles) waits
	/// for it, calling <see cref="WarmBank"/>.
	/// </summary>
	public static bool BankReady => _bank is not null || _referenceBank || _warming is { IsCompleted: true };

	/// <summary>
	/// Takes a warm-up's bank on the main thread. A caller that needs the bank before the worker is
	/// done waits for it — never longer than loading it here would take.
	/// </summary>
	private static void AdoptWarmed()
	{
		if (_warming is not { } warming)
		{
			return;
		}

		_warming = null;
		(FxBank? Bank, byte[]? Bytes, int Generation) warmed;
		try
		{
			warmed = warming.Result;
		}
		catch (System.AggregateException failed)
		{
			GD.PushWarning($"[fx] warming the particle bank failed: {failed.InnerException?.Message}");
			return;
		}

		if (_bank is not null || warmed.Bank is null)
		{
			return;
		}

		ReportLoaded(warmed.Bank);
		_bank = warmed.Bank;
		_bankBytes = warmed.Bytes;
		_bankOverlay = warmed.Generation;
		_bankTried = true;
	}

	/// <summary>The shipped bank parsed from <paramref name="bytes"/> with the mods' effects overlaid; null when it does not parse.</summary>
	private static FxBank? Overlaid(byte[] bytes)
	{
		FxBank? bank = Composed(bytes, out int generation);
		if (bank is not null)
		{
			_bankOverlay = generation;
		}

		return bank;
	}

	/// <summary><see cref="Overlaid"/>'s parse and overlay, touching no shared state: the mods' generation it overlaid.</summary>
	private static FxBank? Composed(byte[] bytes, out int generation)
	{
		generation = -1;
		var bank = new FxBank();
		if (!Engine_FxBank.Parse(bytes, bank))
		{
			GD.Print($"[fx] particle bank parse failed: {bank.Error}");
			return null;
		}

		generation = Engine_FxModEffects.Generation;
		IReadOnlyList<Engine_FxModEffects.Source> sources = Engine_FxModEffects.DrawnSources;
		if (sources.Count != 0)
		{
			Engine_FxModEffects.Result result = Engine_FxModEffects.Apply(bank, sources);
			GD.Print($"[fx] mod effects over the bank: {result}");
			foreach (Refii.Mods.RowConflict conflict in result.Conflicts)
			{
				GD.PushWarning($"[fx] {conflict.Key} is stated by {string.Join(", ", conflict.Losers)} and {conflict.Winner}; {conflict.Winner} wins (the later in the load order)");
			}
		}

		return bank;
	}

	/// <summary>
	/// A bank this node draws from instead of <see cref="SharedBank"/> — the FX Lab's preview of an
	/// effect being edited (<see cref="Engine_FxModEffects.PreviewBank"/>). Setting it rebuilds.
	/// </summary>
	public FxBank? BankOverride
	{
		get => _bankOverride;
		set
		{
			_bankOverride = value;
			_built = false;
		}
	}

	private FxBank? _bankOverride;

	public override void _Ready()
	{
		SetNotifyTransform(true);
		EnsureBuilt();

		// Godot turns processing on in NOTIFICATION_READY, after _EnterTree and before this
		// body; the driver's say is applied again here so it survives that.
		EnterDriver();
	}

	public override void _EnterTree() => EnterDriver();

	/// <summary>The driver steps every system but a GUI stage's, which its stage steps.</summary>
	private void EnterDriver()
	{
		if (OnUiStage)
		{
			SetProcess(false);
			return;
		}

		Engine_FxDriver.Register(this);
	}

	public override void _ExitTree()
	{
		Engine_FxDriver.Unregister(this);
		FreeLayerInstances();
	}

	private Transform3D _placed = Transform3D.Identity;

	/// <summary>
	/// The node's own transform as the owner that places it last wrote it, which is what
	/// <see cref="Node3D.Transform"/> reads back; hundreds of effects compare against it every
	/// guest frame and most have not moved, so it is kept here rather than read across.
	/// </summary>
	internal Transform3D Placed
	{
		get => _placed;
		set
		{
			_placed = value;
			Transform = value;
		}
	}

	private bool _transformDirty = true;
	private Transform3D _cachedGlobalTransform;
	private Transform3D _cachedGlobalInverse;

	private Transform3D CachedGlobalTransform
	{
		get
		{
			if (_transformDirty)
			{
				_cachedGlobalTransform = GlobalTransform;
				_cachedGlobalInverse = _cachedGlobalTransform.AffineInverse();
				_transformDirty = false;
			}
			return _cachedGlobalTransform;
		}
	}

	private Transform3D CachedGlobalInverse
	{
		get
		{
			if (_transformDirty)
			{
				_cachedGlobalTransform = GlobalTransform;
				_cachedGlobalInverse = _cachedGlobalTransform.AffineInverse();
				_transformDirty = false;
			}
			return _cachedGlobalInverse;
		}
	}

	/// <summary>
	/// The layer instances follow this node's world transform and visibility, which the scene
	/// tree no longer propagates to them.
	/// </summary>
	public override void _Notification(int what)
	{
		if (!IsInsideTree())
		{
			return;  // a node leaving or not yet in the tree has no world transform to give
		}

		if (what == NotificationTransformChanged)
		{
			_transformDirty = true;
			Transform3D world = CachedGlobalTransform;
			_addInstance?.SetTransform(world);
			_dispInstance?.SetTransform(world);
			foreach (Engine_FxLayerInstance instance in _meshInstances.Values)
			{
				instance.SetTransform(world);
			}
		}
		else if (what == NotificationVisibilityChanged)
		{
			bool visible = IsVisibleInTree();
			_addInstance?.SetVisible(visible);
			_dispInstance?.SetVisible(visible);
			foreach (Engine_FxLayerInstance instance in _meshInstances.Values)
			{
				instance.SetVisible(visible);
			}
		}
	}

	/// <summary>
	/// Creates the layer instances whose layer world exists now. The layer nodes are added
	/// deferred, so the first Step after the meshes are built may find no scenario yet and
	/// tries again next frame; an instance made here starts at this node's transform.
	/// </summary>
	private void EnsureLayerInstances()
	{
		bool needAdd = _addMesh is not null && _addInstance is null;
		bool needDisp = _dispMesh is not null && _dispInstance is null;
		// Against the displacement nodes, not all of them: the others never get an instance, so
		// counting them kept this asking the tree every step for the life of the effect.
		bool needMesh = _meshInstances.Count < _dispMeshNodes;
		if (!needAdd && !needDisp && !needMesh)
		{
			return;
		}

		if (!IsInsideTree())
		{
			return;
		}

		Transform3D world = CachedGlobalTransform;
		bool visible = IsVisibleInTree();
		if (needAdd &&
			Engine_FxLayerInstance.Create(_addMesh!.GetRid(), Engine_FxAdditiveLayer.Scenario,
				Engine_FxAdditiveLayer.LayerMask, 1f, world, visible) is { } add)
		{
			_addInstance = add;
		}

		if (needDisp &&
			Engine_FxLayerInstance.Create(_dispMesh!.GetRid(), Engine_FxDisplacement.Scenario,
				Engine_FxDisplacement.LayerMask, 1f, world, visible) is { } disp)
		{
			_dispInstance = disp;
		}

		if (needMesh)
		{
			foreach ((string path, MultiMeshInstance3D node) in _meshNodes)
			{
				if (node.Layers != Engine_FxDisplacement.LayerMask || _meshInstances.ContainsKey(path) ||
					node.Multimesh is not MultiMesh multi)
				{
					continue;
				}

				Engine_FxLayerInstance? instance = Engine_FxLayerInstance.Create(multi.GetRid(),
					Engine_FxDisplacement.Scenario, Engine_FxDisplacement.LayerMask, 1f, world, visible,
					node.MaterialOverride?.GetRid() ?? default);
				if (instance is not null)
				{
					_meshInstances[path] = instance;
				}
			}
		}
	}

	private void FreeLayerInstances()
	{
		_addInstance?.Free();
		_addInstance = null;
		_dispInstance?.Free();
		_dispInstance = null;
		foreach (Engine_FxLayerInstance instance in _meshInstances.Values)
		{
			instance.Free();
		}

		_meshInstances.Clear();
	}

	/// <summary>
	/// Self-stepping, taken only while no <see cref="Engine_FxDriver"/> is in the tree (the
	/// editor and the viewer tools); in the game the driver calls <see cref="Step"/> from one
	/// callback for every system.
	/// </summary>
	public override void _Process(double delta)
	{
		using var _framePhase = Refii.Core.Engine_FramePhases.Measure("fablefx");
		LapAt = System.Diagnostics.Stopwatch.GetTimestamp();
		Step(Refii.Core.Engine_DrawAdvance.Seconds(delta));
	}

	/// <summary>
	/// One frame of this system. Reads the viewport camera and this node's world transform,
	/// so it must run after both are placed for the frame; see <see cref="Engine_FxDriver"/>.
	/// </summary>
	/// <param name="sharedCamera">
	/// The frame's camera, resolved once by <see cref="Engine_FxDriver"/> for every system;
	/// null falls back to this node's viewport camera.
	/// </param>
	/// <param name="sharedCameraPos">Optional pre-fetched camera global position to avoid interop overhead.</param>
	/// <param name="sharedCameraTransform">Optional pre-fetched camera global transform to avoid interop overhead.</param>
	public void Step(
		double delta,
		Camera3D? sharedCamera = null,
		Vector3? sharedCameraPos = null,
		Transform3D? sharedCameraTransform = null)
	{
		if (!Playing || Refii.Core.Engine_RdHealth.IsDeviceLost)
		{
			return;
		}

		// The step's segments, summed over every system (FrameWork): build, cull, sim, light, the
		// geometry prelude, then the mesh-particle submit. EmitGeometry laps its own. One running
		// stamp, so a system that was built already and freezes reads no clock: its time falls in
		// the next system's cull lap. Hundreds freeze a frame, and a read is a syscall-sized cost
		// on a phone.
		ref long lapAt = ref LapAt;
		if (!_built || _builtName != _effectName)
		{
			FrameWork.Lap(1, ref lapAt);
			EnsureBuilt();
			FrameWork.Lap(0, ref lapAt);
		}

		if (!_built || _mesh is null || _system.IsEmpty)
		{
			return;
		}

		bool culledByFade = Engine_FxSystem.ShouldCullByFade(LightFade);
		bool lightsOnly = Engine_FxSystem.ShouldRunLightsOnly(GuestDrawn, _hasDisplacement, GuestMeshTwins, _hasMeshParticles, LightFade);
		if (lightsOnly)
		{
			if (!_emitGuest || _emitMainActive || _emitAddActive || _emitDispActive)
			{
				if (_emitMainActive && _mesh.GetSurfaceCount() > 0) _mesh.ClearSurfaces();
				if (_emitAddActive && _addMesh is not null && _addMesh.GetSurfaceCount() > 0) _addMesh.ClearSurfaces();
				if (_emitDispActive && _dispMesh is not null && _dispMesh.GetSurfaceCount() > 0) _dispMesh.ClearSurfaces();
				foreach (MultiMeshInstance3D node in _meshNodes.Values)
				{
					if (node.Multimesh is MultiMesh multi) multi.VisibleInstanceCount = 0;
				}
				_emitMainActive = false;
				_emitAddActive = false;
				_emitDispActive = false;
				_emitGuest = true;
			}
			bool hideLights = culledByFade || SuppressLights;
			if (_lights.Count == 0 || hideLights)
			{
				if (hideLights)
				{
					for (int i = 0; i < _lights.Count; i++)
					{
						FxLightCache cache = _lightCache[i];
						if (!cache.Applied || cache.Visible)
						{
							Refii.AndroidRuntime.AndroidFxLightLimit.SetShown(_lights[i], false);
							cache.Applied = true;
							cache.Visible = false;
							_lightCache[i] = cache;
						}
					}
				}
				return;
			}
		}

		// THE GUEST FREEZES WHAT THE CAMERA CANNOT SEE: `sub_83233EF0` tests each instance's
		// distance against its stated cull radius and the view cone, and both update branches
		// gate on the result — no simulation, no aging, lights left as they are (they are never
		// view-culled). 209 systems in the market cost ~20 ms a frame simulating and rebuilding
		// billboards for effects behind the camera. A frozen system keeps its last geometry.
		// Every view counts: on split screen an effect in either player's view simulates.
		Camera3D? cullCamera = sharedCamera ?? GetViewport()?.GetCamera3D();
		// A far effect in view freezes only once it has something to show: one that has never
		// been near would otherwise keep its empty first frame however long it is looked at.
		bool frozen = !OnUiStage && cullCamera is not null && !InAnyView(cullCamera, sharedCameraPos, out bool farOnly) && (!farOnly || _emitted || HasGeometry || lightsOnly);
		if (frozen)
		{
			++FrozenThisFrame;
			return;
		}

		FrameWork.Lap(1, ref lapAt);

		_system.SetLoop(Loop);
		_system.SetEmitting(Emitting);
		_system.SetIntensity(Intensity * RecipeIntensity);
		_system.SetSizeScale(SizeScale * RecipeSize);
		_system.SetTint(Tint.R * StatedColour.R, Tint.G * StatedColour.G, Tint.B * StatedColour.B);
		_system.SetLiveAlpha(StatedColour.A);
		if (StatedSaturationMask && !_saturationApplied)
		{
			_system.ForceSaturationMask();
			_saturationApplied = true;
		}
		Engine_FxSystem.FadeOut = !Refii.Lighting.Engine_LightingFrame.ModernModel || Refii.AndroidRuntime.AndroidLeanModern.Fx;
		if (!lightsOnly && !OnUiStage)
		{
			ApplyLit();
		}
		// The node's world transform goes in so the system can hold world-space particles still
		// when this effect moves — see AttachToRoot. A node bolted to a wall passes the same
		// transform every frame and the system does nothing with it.
		_system.Update((float)delta, lightsOnly ? null : CachedGlobalTransform, lightsOnly);
		long simFrom = lapAt;
		FrameWork.Lap(2, ref lapAt);
		Refii.Core.Engine_SpikeNotes.Sim(lapAt - simFrom);

		// THE LIGHTS FOLLOW THE SAME CLOCK AS THE PARTICLES, so a brazier's glow breathes with
		// its flame instead of sitting at a constant. The colour and the energy stay SEPARATE —
		// the guest premultiplies them, but Godot multiplies LightColor by LightEnergy on its way
		// into LIGHT_COLOR, so splitting them here reproduces the same product and lets the tint
		// reach our shader unchanged. This is the same split the placed level lights use.
		for (int i = 0; i < _lights.Count; i++)
		{
			Engine_FxSystem.FxLightState state = _system.LightAt(i);
			OmniLight3D light = _lights[i];
			FxLightCache cache = _lightCache[i];

			float range = state.Range;
			float energy = state.Energy;
			if (SuppressLights || !(range > 0f) || !float.IsFinite(range) || !float.IsFinite(energy))
			{
				if (!cache.Applied || cache.Visible)
				{
					Refii.AndroidRuntime.AndroidFxLightLimit.SetShown(light, false);
					cache.Applied = true;
					cache.Visible = false;
					_lightCache[i] = cache;
				}
				continue;
			}

			// The same dials as the placed fixtures. The guest packs an effect's light and a
			// placed light through one path (`sub_821F0A50`: the same range, cutoff and ramp
			// slots, the same falloff), so `placed_light_range` and `placed_light_scale` apply
			// to both. 1.0 on both is the shipped data; Modern mode applies the 1.5x base reach scale.
			// Hold `OmniRange` at the keyframe's `BaseRange` so the Godot node's AABB does not
			// re-pair in the BVH on every random-walk flicker step, and fold the subtle flicker
			// radius breathing into `lightEnergy`.
			float modernBaseReach = Refii.Lighting.Engine_LightingFrame.ModernModel
				? Refii.AndroidRuntime.AndroidLeanModern.LampReach(Refii.Lighting.Engine_LevelLights.ModernBaseReachScale)
				: 1.0f;
			float baseRange = state.BaseRange > 0f ? state.BaseRange : range;
			float omniRange = baseRange * modernBaseReach * Refii.Lighting.Engine_LevelLights.PlacedRange;
			float radiusFactor = baseRange > 1e-4f ? Math.Clamp(range / baseRange, 0.5f, 1.5f) : 1.0f;
			float lightEnergy = energy * radiusFactor * LightFade * ExposureScale * Refii.Lighting.Engine_LevelLights.PlacedScale;

			// THE GUEST REACHES ZERO AT Cutoff, ramping over the Ramp metres inside it —
			// intensity × clamp((min(Cutoff, 1000) − dist) / Ramp, 0, 1) in `sub_821F0A50`
			// (0x821f0d38) — the same window shape as the placed fixtures, so the same Godot
			// mapping: begin = cutoff − ramp, length = ramp. The modern model doubles the
			// reach because its live lights carry what the bakes used to.
			bool fadeEnabled = false;
			float fadeBegin = 0f;
			float fadeLength = 0f;
			if (state.Cutoff > 0f)
			{
				float cutoff = MathF.Min(state.Cutoff, 1000f);
				float ramp = MathF.Max(state.Ramp, 0.01f);
				if (Refii.Lighting.Engine_LightingFrame.ModernModel && !Refii.AndroidRuntime.AndroidLeanModern.Fx)
				{
					cutoff *= 2f;
					ramp = MathF.Max(ramp * 2f, cutoff * 0.5f);
				}

				fadeEnabled = true;
				fadeBegin = MathF.Max(cutoff - ramp, 0f);
				fadeLength = ramp;
			}

			bool changed = false;
			bool spatialChanged = false;
			if (!cache.Applied || !cache.Visible)
			{
				Refii.AndroidRuntime.AndroidFxLightLimit.SetShown(light, true);
				cache.Visible = true;
				changed = true;
				spatialChanged = true;
			}

			if (!cache.Applied || cache.Position.DistanceSquaredTo(state.Position) > 1e-4f)
			{
				light.Position = state.Position;
				cache.Position = state.Position;
				changed = true;
				spatialChanged = true;
			}

			if (!cache.Applied || MathF.Abs(cache.OmniRange - omniRange) > 1e-2f)
			{
				light.OmniRange = omniRange;
				cache.OmniRange = omniRange;
				changed = true;
				spatialChanged = true;
			}

			if (!cache.Applied ||
				MathF.Abs(cache.Colour.R - state.Colour.R) > 1e-3f ||
				MathF.Abs(cache.Colour.G - state.Colour.G) > 1e-3f ||
				MathF.Abs(cache.Colour.B - state.Colour.B) > 1e-3f)
			{
				// Encoded, because Godot decodes it (see Engine_LightColour).
				Refii.Lighting.Engine_LightColour.Apply(light, state.Colour);
				cache.Colour = state.Colour;
				changed = true;
			}

			if (!cache.Applied || MathF.Abs(cache.LightEnergy - lightEnergy) > 1e-3f)
			{
				Refii.AndroidRuntime.AndroidFxLightLimit.SetEnergy(light, lightEnergy);
				cache.LightEnergy = lightEnergy;
				changed = true;
			}

			if (!cache.Applied || cache.DistanceFadeEnabled != fadeEnabled)
			{
				light.DistanceFadeEnabled = fadeEnabled;
				cache.DistanceFadeEnabled = fadeEnabled;
				changed = true;
			}

			if (fadeEnabled)
			{
				if (!cache.Applied || MathF.Abs(cache.DistanceFadeBegin - fadeBegin) > 1e-4f)
				{
					light.DistanceFadeBegin = fadeBegin;
					cache.DistanceFadeBegin = fadeBegin;
					changed = true;
				}

				if (!cache.Applied || MathF.Abs(cache.DistanceFadeLength - fadeLength) > 1e-4f)
				{
					light.DistanceFadeLength = fadeLength;
					cache.DistanceFadeLength = fadeLength;
					changed = true;
				}
			}

			if (changed)
			{
				cache.Applied = true;
				_lightCache[i] = cache;
				if (spatialChanged)
				{
					Refii.Core.Engine_InstanceUpdates.Note(Refii.Core.Engine_InstanceUpdates.Source.Light);
				}
			}
		}

		FrameWork.Lap(3, ref lapAt);
		if (lightsOnly)
		{
			return;
		}

		Camera3D? camera = cullCamera ?? GetViewport()?.GetCamera3D();
#if TOOLS
		// EDITOR ONLY, and compiled out of an export rather than merely skipped.
		// EditorInterface does not exist in an export template build, so referencing it
		// unguarded fails the export even though IsEditorHint() is false there at runtime
		// and this branch could never be taken. Same guard the addons use.
		if (camera is null && Engine.IsEditorHint())
		{
			camera = EditorInterface.Singleton.GetEditorViewport3D(0).GetCamera3D();
		}
#endif

		if (camera is null)
		{
			return;
		}

		EnsureLayerInstances();
		Transform3D camTransform = sharedCameraTransform ?? camera.GlobalTransform;
		Transform3D local = CachedGlobalInverse * camTransform;
		Vector3 cameraPos = local.Origin;
		Vector3 cameraRight = local.Basis.X.Normalized();
		Vector3 cameraUp = local.Basis.Y.Normalized();

		// Facing is the shader's: `fx_billboard.gdshaderinc` expands each camera-facing quad from
		// its centre along the axes of the view it is drawn through, so a turn does not rebuild
		// geometry and each split-screen view faces its own camera. The axes here still build
		// what depends on the camera's position (ribbon sides, the back-to-front sort).
		// The fast paths publish after the emit instead, against what it left on each surface
		// (their steps are in FableFx.FastPaths.cs).
		bool fast = Engine_FxFastPaths.Enabled;
		if (!fast)
		{
			PublishStepAlpha();
		}

		// The additive layer takes the ONE/ONE batches only while it can reach the screen —
		// the post chain composites it. Otherwise they draw into the scene. When the guest draws
		// this effect's particles (`GuestDrawn`), only displacement stays here and `_addMesh`
		// must be cleared so no earlier fallback frame stays frozen on the additive layer.
		ArrayMesh? addMesh = Engine_FxSystem.ShouldUseAdditiveMesh(GuestDrawn, _addMesh is not null, Engine_FxAdditiveLayer.Present && !OnUiStage)
			? _addMesh
			: null;

		// Geometry only when it can differ: a particle frame is 1/30 s, so the last emit stands
		// until a simulation step, a detached-particle rebase (`LastRebased`, when an attached
		// weapon or character node moves between simulation steps), a camera move past half a
		// metre, or the additive layer coming or going. Rotation is not a trigger: facing is the
		// vertex shader's job, and what is still built against the camera (a ribbon's side vector
		// `tan.Cross(eye - pos)` and the back-to-front sort) reads only the camera's position.
		bool turned = !_emitted || cameraPos.DistanceSquaredTo(_emitPos) > 0.25f;
		float liveRibbonIntensity = Intensity * RecipeIntensity * StatedColour.A;
		bool ribbonDirty = (_system.HasRibbon &&
			(MathF.Abs(liveRibbonIntensity - _emitRibbonIntensity) > 1e-3f || _system.ConsumeLiveWeaponTipDirty())) ||
			_system.ConsumeBeamDirty();
		if (_system.LastSteps == 0 && !_system.LastRebased && !turned && !ribbonDirty && (addMesh is null) == _emitAddNull && GuestDrawn == _emitGuest)
		{
			if (_emitDispActive) Refii.AndroidRuntime.AndroidDisplacementGate.WakeEffect(this, CachedGlobalTransform, _dispMesh);
			if (_emitAddActive) Engine_FxAdditiveLayer.NotifyActive();
			if (fast) PublishStepAlphaFast();
			FrameWork.Lap(4, ref lapAt);
			PlaceMeshParticles(fast);
			FrameWork.Lap(5, ref lapAt);
			return;
		}

		_emitted = true;
		_emitPos = cameraPos;
		_emitAddNull = addMesh is null;
		_emitGuest = GuestDrawn;
		_emitRibbonIntensity = liveRibbonIntensity;
		if (GuestDrawn)
		{
			if (_emitMainActive && _mesh.GetSurfaceCount() > 0) _mesh.ClearSurfaces();
			if (_emitAddActive && _addMesh is not null && _addMesh.GetSurfaceCount() > 0) _addMesh.ClearSurfaces();
			_emitMainActive = false;
			_emitAddActive = false;
		}
		else if (addMesh is null && _emitAddActive && _addMesh is not null && _addMesh.GetSurfaceCount() > 0)
		{
			_addMesh.ClearSurfaces();
			_emitAddActive = false;
		}
		// The delegate is cached: a method group converted at the call site allocates a new Func
		// every call.
		_lookupMaterial ??= LookupMaterial;
		FrameWork.Lap(4, ref lapAt);
		Engine_FxGroundConform.GroundContext? groundConform = null;
		if (_system.GroundConform && !Refii.Models.Engine_ClassicMaterial.Active)
		{
			Engine_FxGroundConform.GroundHeightSampler? sampler = Engine_FxGroundConform.TryGetLiveGroundSampler();
			if (sampler is not null)
			{
				groundConform = new Engine_FxGroundConform.GroundContext(CachedGlobalTransform, sampler);
			}
		}
		_system.EmitGeometry(
			_mesh, _lookupMaterial, cameraPos, cameraRight, cameraUp, _meshData, _dispMesh, GuestDrawn ? null : addMesh, !GuestDrawn, groundConform);
		lapAt = System.Diagnostics.Stopwatch.GetTimestamp();
		_emitMainActive = !GuestDrawn && _mesh.GetSurfaceCount() > 0;
		_emitDispActive = _dispMesh is not null && _dispMesh.GetSurfaceCount() > 0;
		_emitAddActive = !GuestDrawn && addMesh is not null && addMesh.GetSurfaceCount() > 0;
		if (!fast) PublishStepAlpha();
		if (_emitDispActive) Refii.AndroidRuntime.AndroidDisplacementGate.WakeEffect(this, CachedGlobalTransform, _dispMesh);
		if (_emitAddActive) Engine_FxAdditiveLayer.NotifyActive();
		if (fast) PublishStepAlphaFast();

		foreach ((string path, Engine_FxSystem.MeshInstanceData data) in _meshData)
		{
			// The displacement layer is the world view's.
			if (data.Displacement && OnUiStage)
			{
				continue;
			}

			if (!_meshNodes.TryGetValue(path, out MultiMeshInstance3D? node))
			{
				node = BuildMeshNode(data.MeshPath, data.Displacement);
				if (data.Displacement) Engine_FxDisplacement.Ensure(this);
				if (node is null)
				{
					continue;
				}

				_meshNodes[path] = node;
				if (data.Displacement)
				{
					++_dispMeshNodes;
				}
				else
				{
					AddChild(node);
				}
			}

			if (node.Multimesh is not MultiMesh multi)
			{
				continue;
			}

			int count = data.Instances.Count;
			// The guest's own mesh twins are drawn (Engine_FxMeshTwins): these simulated copies stand
			// down, displacement meshes excepted (no modern displacement twin yet).
			if (GuestMeshTwins && !data.Displacement)
			{
				if (MeshBufferStandsDown(path, fast))
				{
					multi.VisibleInstanceCount = 0;
				}

				continue;
			}
			int capacity = multi.InstanceCount;
			if (capacity < count)
			{
				capacity = Math.Max(count, 16);
				multi.InstanceCount = capacity;
			}

			multi.VisibleInstanceCount = count;
			if (fast)
			{
				WriteMeshBuffer(multi, path, data, capacity, count);
				continue;
			}

			ForgetMeshBuffer(path);
			for (int i = 0; i < count; i++)
			{
				(Transform3D transform, Color colour, Vector4 motion) = data.Instances[i];
				Refii.Core.Engine_InstanceUpdates.Note(Refii.Core.Engine_InstanceUpdates.Source.Fx);
				multi.SetInstanceTransform(i, Between(transform, motion));
				multi.SetInstanceColor(i, colour);
			}
		}

		FrameWork.Lap(5, ref lapAt);
	}

	/// <summary>A mesh particle between its last two steps, as the billboard shader draws one.</summary>
	private Transform3D Between(Transform3D transform, Vector4 motion)
	{
		if (motion.X == 0f && motion.Y == 0f && motion.Z == 0f)
		{
			return transform;
		}

		float alpha = motion.W > 0.5f ? _system.StepAlphaHalf : _system.StepAlpha;
		transform.Origin -= new Vector3(motion.X, motion.Y, motion.Z) * (1f - alpha);
		return transform;
	}

	/// <summary>
	/// Mesh particles moved between steps on the frames that do not re-emit: their transforms are
	/// the MultiMesh's own, so the draw between steps is written here rather than in a shader.
	/// Only instances that moved on their last step are touched.
	/// </summary>
	/// <param name="fast">
	/// The fast paths: a buffer the last emit sent whole is updated in place and sent once,
	/// with no call for the drawn count; any other node is placed as before.
	/// </param>
	private void PlaceMeshParticles(bool fast = false)
	{
		foreach ((string path, Engine_FxSystem.MeshInstanceData data) in _meshData)
		{
			if (!_meshNodes.TryGetValue(path, out MultiMeshInstance3D? node) || node.Multimesh is not MultiMesh multi)
			{
				continue;
			}

			if (PlaceFromMeshBuffer(path, data, multi, fast))
			{
				continue;
			}

			int count = Math.Min(data.Instances.Count, multi.VisibleInstanceCount);
			for (int i = 0; i < count; i++)
			{
				(Transform3D transform, _, Vector4 motion) = data.Instances[i];
				if (motion.X == 0f && motion.Y == 0f && motion.Z == 0f)
				{
					continue;
				}

				Refii.Core.Engine_InstanceUpdates.Note(Refii.Core.Engine_InstanceUpdates.Source.Fx);
				multi.SetInstanceTransform(i, Between(transform, motion));
			}
		}
	}

	/// <summary>Systems frozen off-camera in the last frame; reset by the metrics reader.</summary>
	public static int FrozenThisFrame;

	/// <summary>Freeze reach: beyond this an effect is a few pixels and the guest would have
	/// culled it by its radius.</summary>
	public const float FreezeDistance = 50f;

	/// <summary>A generous bounding sphere for the frustum test, so a wide plume at the edge
	/// of the view keeps simulating.</summary>
	public const float FreezeRadius = 14f;

	/// <summary>Each view's frustum (a view is a slot of Engine_Views.Cameras), read once per frame.</summary>
	private static readonly ulong[] _frustumFrame = { ulong.MaxValue, ulong.MaxValue };
	private static readonly ulong[] _frustumCamera = new ulong[2];
	private static readonly Camera3D?[] _frustumCameraRef = new Camera3D?[2];
	private static readonly Plane[][] _frustum = { new Plane[6], new Plane[6] };
	private static readonly Vector3[] _frustumEye = new Vector3[2];
	private static int _frustumViewCount;
	private static ulong _driverFrame = ulong.MaxValue;

	private static void ExtractFrustumPlanes(Camera3D cam, Plane[] dst)
	{
		Projection proj = cam.GetCameraProjection();
		Transform3D xform = cam.GetCameraTransform();
		dst[0] = xform * proj.GetProjectionPlane(Projection.Planes.Near);
		dst[1] = xform * proj.GetProjectionPlane(Projection.Planes.Far);
		dst[2] = xform * proj.GetProjectionPlane(Projection.Planes.Left);
		dst[3] = xform * proj.GetProjectionPlane(Projection.Planes.Top);
		dst[4] = xform * proj.GetProjectionPlane(Projection.Planes.Right);
		dst[5] = xform * proj.GetProjectionPlane(Projection.Planes.Bottom);
	}

	/// <summary>Pre-fetches the frame's camera frustum planes once per frame from <see cref="Engine_FxDriver"/>.</summary>
	internal static void PrepareFrame(Camera3D? primary)
	{
		_driverFrame = Engine.GetProcessFrames();
		if (primary is null)
		{
			_frustumViewCount = 0;
			return;
		}

		IReadOnlyList<Camera3D> cameras = Refii.Core.Engine_Views.Cameras(primary);
		int views = Math.Min(cameras.Count, _frustum.Length);
		_frustumViewCount = views;
		for (int v = 0; v < views; v++)
		{
			Camera3D cam = cameras[v];
			_frustumEye[v] = cam.GlobalPosition;
			ulong id = cam.GetInstanceId();
			if (_driverFrame != _frustumFrame[v] || id != _frustumCamera[v])
			{
				ExtractFrustumPlanes(cam, _frustum[v]);
				_frustumFrame[v] = _driverFrame;
				_frustumCamera[v] = id;
				_frustumCameraRef[v] = cam;
			}
		}
	}

	/// <summary>One view's frustum planes, read once per frame for every system: extracted
	/// directly from projection + transform without allocating a wrapper array.</summary>
	private static Plane[] FrameFrustum(Camera3D camera, int view)
	{
		if (ReferenceEquals(camera, _frustumCameraRef[view]) && _frustumFrame[view] == _driverFrame)
		{
			return _frustum[view];
		}

		ulong frame = Engine.GetProcessFrames();
		ulong id = camera.GetInstanceId();
		if (frame != _frustumFrame[view] || id != _frustumCamera[view])
		{
			ExtractFrustumPlanes(camera, _frustum[view]);
			_frustumFrame[view] = frame;
			_frustumCamera[view] = id;
			_frustumCameraRef[view] = camera;
		}

		return _frustum[view];
	}

	/// <summary>
	/// <see cref="InView"/> for any view drawing the world (<paramref name="primary"/>, then player
	/// two's on split screen); <paramref name="farOnly"/> when no view sees it near and one has it
	/// in its frustum and only too far. <paramref name="primaryPos"/> is the primary's position
	/// when the caller already read it this frame.
	/// </summary>
	private bool InAnyView(Camera3D primary, Vector3? primaryPos, out bool farOnly)
	{
		farOnly = false;
		if (_frustumViewCount > 0 && ReferenceEquals(primary, _frustumCameraRef[0]))
		{
			for (int v = 0; v < _frustumViewCount; v++)
			{
				Vector3 eye = v == 0 && primaryPos.HasValue ? primaryPos.Value : _frustumEye[v];
				if (InPreparedView(v, eye, out bool far))
				{
					farOnly = false;
					return true;
				}

				farOnly |= far;
			}

			return false;
		}

		IReadOnlyList<Camera3D> cameras = Refii.Core.Engine_Views.Cameras(primary);
		int views = Math.Min(cameras.Count, _frustum.Length);
		for (int v = 0; v < views; v++)
		{
			if (InView(cameras[v], v, v == 0 ? primaryPos : null, out bool far))
			{
				farOnly = false;
				return true;
			}

			farOnly |= far;
		}

		return false;
	}

	private bool InPreparedView(int view, Vector3 camPos, out bool farOnly)
	{
		farOnly = false;
		Vector3 origin = CachedGlobalTransform.Origin;
		float cullRadius = MathF.Max(FreezeRadius, _system.ActiveBoundRadius);
		Plane[] planes = _frustum[view];
		for (int i = 0; i < planes.Length; i++)
		{
			if (planes[i].DistanceTo(origin) > cullRadius)
			{
				return false;
			}
		}

		float freezeReach = Engine_FxSystem.EffectiveFreezeDistance(Refii.Lighting.Engine_LightingFrame.ModernModel) +
			MathF.Max(0f, cullRadius - FreezeRadius);
		if (origin.DistanceSquaredTo(camPos) > freezeReach * freezeReach)
		{
			farOnly = true;
			return false;
		}

		return true;
	}

	/// <summary>In one view's frustum and within <see cref="FreezeDistance"/> of its eye;
	/// <paramref name="farOnly"/> when it is in the frustum and only too far.</summary>
	private bool InView(Camera3D camera, int view, Vector3? cameraPos, out bool farOnly)
	{
		farOnly = false;
		Vector3 origin = CachedGlobalTransform.Origin;
		float cullRadius = MathF.Max(FreezeRadius, _system.ActiveBoundRadius);
		Plane[] planes = FrameFrustum(camera, view);
		for (int i = 0; i < planes.Length; i++)
		{
			if (planes[i].DistanceTo(origin) > cullRadius)
			{
				return false;
			}
		}

		Vector3 camPos = cameraPos ?? camera.GlobalPosition;
		float freezeReach = Engine_FxSystem.EffectiveFreezeDistance(Refii.Lighting.Engine_LightingFrame.ModernModel) +
			MathF.Max(0f, cullRadius - FreezeRadius);
		if (origin.DistanceSquaredTo(camPos) > freezeReach * freezeReach)
		{
			farOnly = true;
			return false;
		}

		return true;
	}

	/// <summary>Whether the last emit left anything to draw, in the scene or the additive layer.</summary>
	private bool HasGeometry => _emitMainActive || _emitAddActive || _emitDispActive;

	/// <summary>Where the scene light was last sampled for a node without a bake probe.</summary>
	private Vector3 _litSampledAt = new(float.NaN, float.NaN, float.NaN);

	private ulong _litSampledFrame;

	/// <summary>
	/// The scene light the particles lerp from: the guest's vertex shader lights a particle
	/// as `lerp(g_GlobalAmbient + point lights, keyframeColour, selfIllum)` (blob 0038).
	///
	/// A node with a bake probe (level-spawned) composes the probe with the theme ambient. A
	/// node without one (every effect the guest starts at runtime: spells, impacts, auras)
	/// samples the theme ambient plus the registered lights at the node's position, re-sampled
	/// when the effect has moved a metre or 30 frames have passed.
	/// </summary>
	private void ApplyLit()
	{
		if (!_system.NeedsSceneLight)
		{
			return;
		}

		float r = 1f, g = 1f, b = 1f;

		// Asked every 30 frames while absent, not every step (each query allocates a wrapper);
		// once present on a level-spawned effect, the probe metadata stays for the life of the node.
		if (_hasProbe != true)
		{
			ulong askFrame = Engine.GetProcessFrames();
			if (_hasProbe is null || askFrame - _probeAskedFrame >= 30)
			{
				_hasProbe = HasMeta(Refii.Lighting.Engine_LevelBakedLighting.ProbeMeta);
				_probeAskedFrame = askFrame;
			}

			if (_hasProbe == false)
			{
				Vector3 at = CachedGlobalTransform.Origin;
				if (_litSampledAt.IsFinite() &&
					at.DistanceSquaredTo(_litSampledAt) < 1f &&
					askFrame - _litSampledFrame < 30)
				{
					return;
				}

				_litSampledAt = at;
				_litSampledFrame = askFrame;
				if (Engine_FxSystem.SceneLightAt(at, _litScratch))
				{
					_system.SetAmbient(_litScratch[0], _litScratch[1], _litScratch[2]);
				}

				return;
			}
		}

		// The meta is an Array behind a Variant: reading it builds wrappers every call, and
		// a static level FX node's baked probe SH coefficients do not change once stored.
		if (!_probeFolded)
		{
			using Variant probeMeta = GetMeta(Refii.Lighting.Engine_LevelBakedLighting.ProbeMeta);
			if (probeMeta.VariantType == Variant.Type.Array)
			{
				Godot.Collections.Array<Vector4> sample = probeMeta.AsGodotArray<Vector4>();
				if (sample.Count == 3)
				{
					var basis = new Vector4(0.2820948f, 0f, 0.4886025f, 0f);
					const float gamma = 1.3f;
					_probeBand[0] = MathF.Pow(MathF.Max(sample[0].Dot(basis), 0f), gamma);
					_probeBand[1] = MathF.Pow(MathF.Max(sample[1].Dot(basis), 0f), gamma);
					_probeBand[2] = MathF.Pow(MathF.Max(sample[2].Dot(basis), 0f), gamma);
					_probeFolded = true;
				}
			}
		}

		if (_probeFolded)
		{
			{
				float br = _probeBand[0], bg = _probeBand[1], bb = _probeBand[2];
				Refii.Lighting.LightingFrame? frame = Refii.Level.Engine_SkyEnvironmentNode.Current?.Lighting;
				float[] lm = frame?.LightmapColour ?? White;
				float ar = 0f, ag = 0f, ab = 0f;
				if (frame is not null && frame.HasAmbient)
				{
					ar = frame.AmbientColour[0] * frame.AmbientEnergy;
					ag = frame.AmbientColour[1] * frame.AmbientEnergy;
					ab = frame.AmbientColour[2] * frame.AmbientEnergy;
				}

				r = (br * lm[0]) + ar; g = (bg * lm[1]) + ag; b = (bb * lm[2]) + ab;
			}
		}

		_system.SetAmbient(r, g, b);
	}

	private static readonly StringName StepAlphaParam = new("fx_step_alpha");
	private static readonly StringName StepAlphaHalfParam = new("fx_step_alpha_half");

	private float _publishedAlpha = -1f;
	private float _publishedAlphaHalf = -1f;

	/// <summary>
	/// How far between its simulation steps this effect is drawn — the billboard shader draws
	/// each particle on the segment it last moved along (`fx_billboard.gdshaderinc`). Every
	/// frame, because it changes every frame; nothing is re-emitted for it.
	/// </summary>
	private void PublishStepAlpha()
	{
		if ((!_emitMainActive && !_emitAddActive && !_emitDispActive) || _system.AliveCount() == 0)
		{
			return;
		}

		float alpha = _system.StepAlpha;
		float half = _system.StepAlphaHalf;
		// What the fast paths recorded no longer holds once this path writes.
		ForgetStepSent();
		if (MathF.Abs(alpha - _publishedAlpha) < 1e-3f && MathF.Abs(half - _publishedAlphaHalf) < 1e-3f)
		{
			return;
		}

		_publishedAlpha = alpha;
		_publishedAlphaHalf = half;
		if (_emitMainActive && _surface is not null)
		{
			_surface.SetInstanceShaderParameter(StepAlphaParam, alpha);
			_surface.SetInstanceShaderParameter(StepAlphaHalfParam, half);
		}

		if (_emitAddActive && _addInstance is not null)
		{
			_addInstance.SetShaderParameter(StepAlphaParam, alpha);
			_addInstance.SetShaderParameter(StepAlphaHalfParam, half);
		}
		if (_emitDispActive && _dispInstance is not null)
		{
			_dispInstance.SetShaderParameter(StepAlphaParam, alpha);
			_dispInstance.SetShaderParameter(StepAlphaHalfParam, half);
		}
	}

	private readonly float[] _litScratch = new float[3];
	private System.Func<string, Refii.Fx.VisualBatchMaterial?>? _lookupMaterial;
	private readonly float[] _probeBand = new float[3];
	private bool? _hasProbe;
	private ulong _probeAskedFrame;
	private bool _probeFolded;
	private ulong _probeReadFrame;
	private static readonly float[] White = { 1f, 1f, 1f };

	public int DebugAlive => _system.AliveCount();

	public int DebugSpawned => _system.TotalSpawned;

	/// <summary>True when at least one emitter spawns detached world-space particles that must drain after stop.</summary>
	public bool HasDetachedParticles => _system.HasDetachedParticles;

	/// <summary>
	/// Surfaces on the main mesh only: an "addone" batch commits to `_addMesh` and a "disp"
	/// batch to `_dispMesh`, so a purely additive effect has zero surfaces here. Read the three
	/// together.
	/// </summary>
	public int DebugSurfaces => _mesh?.GetSurfaceCount() ?? -1;

	public int DebugAddSurfaces => _addMesh?.GetSurfaceCount() ?? -1;

	public int DebugDispSurfaces => _dispMesh?.GetSurfaceCount() ?? -1;

	/// <summary>Emitters, visuals and particles this effect actually holds — see Census.</summary>
	public (int Emitters, int Visuals, int TrailVisuals, int Particles) DebugCensus => _system.Census();

	/// <summary>Per emitter, its particles' height and radius spans, for the lab.</summary>
	public string DebugEmitterSpread() => _system.DebugEmitterSpread();

	/// <summary>What the surfaces hold this frame, for the lab: quads, their box, their colour.</summary>
	public string DebugSurfaceStats()
	{
		var parts = new System.Collections.Generic.List<string>();
		foreach ((string label, ArrayMesh? mesh) in new[] { ("main", _mesh), ("add", _addMesh), ("disp", _dispMesh) })
		{
			if (mesh is null || mesh.GetSurfaceCount() == 0)
			{
				parts.Add($"{label}: none");
				continue;
			}

			Godot.Collections.Array arrays = Refii.Core.Engine_ServerSyncs.SurfaceArrays(mesh, 0);
			var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
			var custom1 = arrays[(int)Mesh.ArrayType.Custom1].AsFloat32Array();
			if (vertices.Length == 0)
			{
				parts.Add($"{label}: 0 vertices");
				continue;
			}

			var box = new Aabb(vertices[0], Vector3.Zero);
			float alphaSum = 0f, alphaMax = 0f, lumaSum = 0f, lumaMax = 0f;
			foreach (Vector3 v in vertices)
			{
				box = box.Expand(v);
			}

			int colourCount = custom1.Length / 4;
			for (int i = 0; i < colourCount; i++)
			{
				float r = custom1[(i * 4) + 0];
				float g = custom1[(i * 4) + 1];
				float b = custom1[(i * 4) + 2];
				float a = custom1[(i * 4) + 3];
				alphaSum += a; alphaMax = Math.Max(alphaMax, a);
				float luma = (r + g + b) / 3f; lumaSum += luma; lumaMax = Math.Max(lumaMax, luma);
			}

			int n = Math.Max(1, colourCount);
			parts.Add($"{label}: {vertices.Length} vertices, box {box.Position} size {box.Size}, alpha mean {alphaSum / n:0.###} max {alphaMax:0.###}, luma mean {lumaSum / n:0.###} max {lumaMax:0.###}");
		}

		return string.Join(" | ", parts);
	}

	private readonly List<Node3D> _pieces = new();

	/// <summary>
	/// The effect's authored pieces (<see cref="FxEffect.Pieces"/>) as children, at the effect's
	/// scale: built with the effect, replaced when it is rebuilt.
	/// </summary>
	private void BuildPieces(FxBank bank, FxPlacement placement)
	{
		foreach (Node3D piece in _pieces)
		{
			if (IsInstanceValid(piece))
			{
				piece.QueueFree();
			}
		}

		_pieces.Clear();
		FxEffect? effect = placement.EffectHash != 0 ? bank.FindByHash(placement.EffectHash) : bank.FindByName(_effectName);
		if (effect is not { Pieces.Count: > 0 })
		{
			return;
		}

		float scale = EffectScale * (effect.Scale > 0.0001f ? effect.Scale : 1f);
		foreach (Engine_FxRecipePieces.Piece piece in effect.Pieces)
		{
			if (Engine_FxRecipePieces.Build(piece) is not { } made)
			{
				continue;
			}

			if (scale != 1f)
			{
				made.Scale *= scale;
			}

			AddChild(made);
			_pieces.Add(made);
		}
	}

	/// <summary>Whether Build resolved the effect at all; false means the bank had no such key.</summary>
	public bool DebugBuilt => _built;

	public void Restart()
	{
		_built = false;
		EnsureBuilt();
	}

	private void EnsureBuilt()
	{
		if (_built && _builtName == _effectName)
		{
			return;
		}

		FxBank? bank = _bankOverride ?? SharedBank();
		if (bank is null || _effectName.Length == 0)
		{
			return;
		}

		foreach (MultiMeshInstance3D node in _meshNodes.Values)
		{
			node.QueueFree();
		}

		foreach (Engine_FxLayerInstance instance in _meshInstances.Values)
		{
			instance.Free();
		}

		_meshInstances.Clear();
		_meshNodes.Clear();
		_dispMeshNodes = 0;
		_meshData.Clear();
		_meshBuffers.Clear();

		foreach (OmniLight3D light in _lights)
		{
			light.QueueFree();
		}

		_lights.Clear();
		_lightCache.Clear();

		var placement = new FxPlacement { Scale = EffectScale };
		if (Engine_BnkFxLoader.TryParseHashToken(_effectName, out uint hash))
		{
			placement.EffectHash = hash;
		}
		else
		{
			placement.EffectName = _effectName;
		}

		_system.Build(bank, new[] { placement });
		_system.GroundConform = placement.EffectHash != 0u && Engine_FxGroundConform.ShouldConformEffect(placement.EffectHash);
		BuildPieces(bank, placement);
		_hasDisplacement = _system.HasDisplacement;
		_hasMeshParticles = _system.HasMeshParticles;

		if (_surface is null)
		{
			_mesh = new ArrayMesh();
			_surface = new MeshInstance3D
			{
				Name = "FxSurface",
				Mesh = _mesh,
				CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
				// A metre of safety only: the FX system states each surface's real box every
				// frame (Engine_FxSystem.EndSurfaces), corners included; a wide margin puts every
				// FX leaf in every light's and every shadow pass's reach.
				ExtraCullMargin = 1f,
			};
			AddChild(_surface);
		}

		// The additive and displacement surfaces live in the layers' own worlds as server
		// instances (Engine_FxLayerInstance), created by EnsureLayerInstances once the layer
		// node — added deferred — has a world.
		if (_addMesh is null && _system.HasAdditive && !OnUiStage)
		{
			_addMesh = new ArrayMesh();
			Engine_FxAdditiveLayer.Ensure(this);
		}

		if (_dispMesh is null && _hasDisplacement && !OnUiStage)
		{
			_dispMesh = new ArrayMesh();
			Engine_FxDisplacement.Ensure(this);
		}

		// THE EFFECT'S LIGHTS, built once. Their colour, range and energy are all timelines and
		// move every frame, but how MANY there are is fixed by the bank.
		bool anyHalo = false;
		for (int i = 0; i < _system.LightCount; i++)
		{
			var light = new OmniLight3D
			{
				Name = $"FxLight_{i}",

				// Godot's native falloff (`OmniAttenuation = 1.0`), matching placed level lights.
				OmniAttenuation = 1.0f,
				LightVolumetricFogEnergy = 0.0f,
				ShadowEnabled = false,
			};
			_lights.Add(light);
			_lightCache.Add(default);
			AddChild(light);
			Refii.AndroidRuntime.AndroidFxLightLimit.Track(light);

			// THE GLOW LAYER. The guest gives every in-range, in-frustum, non-black light a
			// glow quad, gated on a per-light texture key the shipped FX data never states —
			// with a runtime-built default texture set as the holder's fallback. Fable mode
			// follows the data (no keys, no glows); the modern model runs the mechanism with
			// the procedural disc standing in for the default provider.
			uint halo = _system.HaloAt(i);
			if (halo != 0 || (Refii.Lighting.Engine_LightingFrame.ModernModel && !Refii.AndroidRuntime.AndroidLeanModern.Fx))
			{
				Engine_PostFxHalos.Register(light, halo);
				anyHalo = true;
			}
		}

		if (anyHalo)
		{
			Engine_PostFxHalos.Ensure(this);
		}

		_built = true;
		_builtName = _effectName;
		_saturationApplied = false;

		if (!placement.Resolved && _pieces.Count == 0)
		{
			GD.Print($"[fx] effect '{_effectName}' not found in bank");
		}
	}

	private static readonly Dictionary<string, Shader?> SharedShaders = new(StringComparer.Ordinal);
	private static readonly Dictionary<string, Texture2D> SharedTextures = new(StringComparer.OrdinalIgnoreCase);
	private static readonly HashSet<string> MissingTextures = new(StringComparer.OrdinalIgnoreCase);
	private static readonly Dictionary<string, VisualBatchMaterial> SharedMaterials = new(StringComparer.Ordinal);
	private static readonly Dictionary<string, ArrayMesh?> SharedMeshes = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Drops region-scoped FX mesh caches on level teardown.
	/// </summary>
	public static void ClearRegionCaches()
	{
		SharedMeshes.Clear();
	}

	private static Shader? LoadSharedShader(string shaderPath)
	{
		if (SharedShaders.TryGetValue(shaderPath, out Shader? shader) &&
			(shader is null || IsInstanceValid(shader)))
		{
			return shader;
		}

		shader = GD.Load<Shader>(shaderPath);
		SharedShaders[shaderPath] = shader;
		return shader;
	}

	private VisualBatchMaterial? LookupMaterial(string key)
	{
		if (Refii.Core.Engine_RdHealth.IsDeviceLost)
		{
			return null;
		}

		if (_materials.TryGetValue(key, out VisualBatchMaterial? cached))
		{
			return cached;
		}

		bool canShare = MathF.Abs(ExposureScale - 1.0f) < 1e-4f &&
						MathF.Abs(Engine_FxSystem.Exposure - (10f / 3f)) < 1e-4f;
		if (canShare && SharedMaterials.TryGetValue(key, out VisualBatchMaterial? shared) &&
			IsInstanceValid(shared.Material))
		{
			_materials[key] = shared;
			return shared;
		}

		long madeFrom = System.Diagnostics.Stopwatch.GetTimestamp();
		string[] segments = key.Split('|');
		string texture = segments[0];
		string mode = segments.Length > 1 ? segments[1] : string.Empty;
		bool satMask = false;
		float softDepth = 0f;
		for (int i = 2; i < segments.Length; i++)
		{
			if (segments[i] == "sat") satMask = true;
			else if (segments[i].StartsWith("soft", StringComparison.Ordinal))
			{
				float.TryParse(segments[i][4..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out softDepth);
			}
		}
		bool additive = mode is "add" or "addone" or "addlayer";
		string shaderPath = mode switch
		{
			"add" or "addone" => AddShaderPath,
			"addlayer" => AddLayerShaderPath,
			"mixalpha" => MixAlphaShaderPath,
			"darken" => DarkenShaderPath,
			"sub" => SubShaderPath,
			"disp" => DispShaderPath,
			_ => MixShaderPath,
		};

		Shader? shader = LoadSharedShader(shaderPath);
		if (shader is null)
		{
			return null;
		}

		var material = new ShaderMaterial { Shader = shader };
		material.SetShaderParameter(FxAlphaRefName, satMask ? 16.0f : 1.0f);
		material.SetShaderParameter(FxSoftInvName, softDepth > 0f ? 1.0f / softDepth : 0.0f);

		// No soft distance, no fade: the hard build, without the depth texture whose declaration
		// alone splits Mobile's render pass, when fx_no_depth_reads asks for it; on Android a soft
		// one too under android_fx_hard_all. The additive layer's and the displacement shaders
		// never declare it.
		if (Engine_FxDepthReads.Adoptable(softDepth, mode))
		{
			Engine_FxDepthReads.Adopt(material, shader, softDepth > 0f);
		}
		if (mode == "darken")
		{
			material.RenderPriority = -1;
		}
		else if (mode != "disp")
		{
			material.SetShaderParameter(
				"fx_exposure", Engine_FxSystem.Exposure * ExposureScale);
		}

		if (additive && mode != "addlayer")
		{
			material.SetShaderParameter(FxSrcAlphaName, mode == "add" ? 1.0f : 0.0f);
		}

		Texture2D? tex = LoadTexture(texture);
		if (tex is not null)
		{
			material.SetShaderParameter(FxTexName, tex);
		}

		var batch = new VisualBatchMaterial { Material = material };
		Refii.Core.Engine_SpikeNotes.Since(Refii.Core.Engine_SpikeNotes.Kind.FxMaterial, madeFrom);
		_materials[key] = batch;
		if (canShare)
		{
			SharedMaterials[key] = batch;
		}
		return batch;
	}

	private static Texture2D? LoadTexture(string name)
	{
		if (SharedTextures.TryGetValue(name, out Texture2D? cached) && IsInstanceValid(cached))
		{
			return cached;
		}

		if (MissingTextures.Contains(name))
		{
			return null;
		}

		long madeFrom = System.Diagnostics.Stopwatch.GetTimestamp();
		string leaf = TextureLeaf(name);
		if (ReferenceTextureDirectory.Length != 0)
		{
			string loose = System.IO.Path.Combine(ReferenceTextureDirectory, leaf.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) ? leaf : leaf + ".tex");
			if (System.IO.File.Exists(loose) && Engine_TexImage.TryCreateTexture(System.IO.File.ReadAllBytes(loose), out _) is { } reference)
			{
				return reference;
			}
		}

		// Decoded already by the level-load prefetch: the same bytes through the same decode,
		// only the texture object is made here.
		if (PrefetchedImages.TryTake(name, out Image taken) && TakePrefetched(name, taken) is { } prefetched)
		{
			try
			{
				var ready = Engine_TextureUpload.CreateTexture(prefetched);
				SharedTextures[name] = ready;
				Refii.Core.Engine_SpikeNotes.Since(Refii.Core.Engine_SpikeNotes.Kind.FxTexture, madeFrom);
				return ready;
			}
			finally
			{
				prefetched.Dispose();
			}
		}

		Texture2D? texture = ReadTextureBytes(name, leaf) is { } bytes ? Engine_TexImage.TryCreateTexture(bytes, out _) : null;
		PrefetchedImages.MarkLoaded(name);
		Refii.Core.Engine_SpikeNotes.Since(Refii.Core.Engine_SpikeNotes.Kind.FxTexture, madeFrom);
		if (texture is not null)
		{
			SharedTextures[name] = texture;
			return texture;
		}

		MissingTextures.Add(name);
		GD.Print($"[fx] texture '{name}' unresolved");
		return null;
	}

	private static string TextureLeaf(string name)
	{
		string leaf = name.Replace('\\', '/');
		int slash = leaf.LastIndexOf('/');
		return slash >= 0 ? leaf[(slash + 1)..] : leaf;
	}

	/// <summary>A bank texture's bytes as the mount serves them, by the folder leaf first, then the stated path.</summary>
	private static byte[]? ReadTextureBytes(string name, string leaf)
	{
		foreach (string candidate in new[] { TextureFolder + leaf, name.Replace('\\', '/') })
		{
			byte[]? bytes = Engine_BnkMeshLoader.ReadBytes(candidate) ??
							Engine_BnkMeshLoader.ReadBytes("data/" + candidate);
			if (bytes is not null)
			{
				return bytes;
			}
		}

		return null;
	}

	/// <summary>
	/// `fx_prefetch_prepare` (default off): the prefetch also does what the upload would do to each
	/// image on the main thread. On a GPU without BC (Adreno 6xx) that is the BC-to-ASTC
	/// transcode, per block, of every effect texture, on the frame an effect first emits.
	/// </summary>
	/// <remarks>The same bytes reach the texture: Engine_TextureUpload.PrepareAhead runs on the
	/// prefetch thread instead of the main one. A BC1 image waits at twice its size (ASTC 4x4 is
	/// 16 bytes a block to BC1's 8) until its first use.</remarks>
	private static readonly bool PrefetchPrepares = ReadPrefetchPrepares();

	private static bool ReadPrefetchPrepares()
	{
		try
		{
			return Refii.Core.Engine_RefiiConfig.Bool(
				Refii.Core.Engine_RefiiConfig.Read(out _), "fx_prefetch_prepare", false);
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>Effect textures decoded off the main thread, waiting for their first use.</summary>
	private static readonly Engine_FxPrefetchStore<Image> PrefetchedImages = new();

	private static int _prefetchStarted;

	/// <summary>
	/// Reads and decodes every texture the bank's visuals name on a background thread, so an
	/// effect's first build does not page the texture bank in and decode on the frame it
	/// appears. Once per process; the first level load calls it.
	/// </summary>
	/// <remarks>
	/// SAME BYTES, SAME DECODE. <see cref="LoadTexture"/> takes the decoded image and makes the
	/// texture object from it, exactly what the synchronous path makes from the same bytes; a
	/// name the main thread reaches first loads as before and the prefetched copy is dropped
	/// (Engine_FxPrefetchStore). A decoded image is held only until it is taken.
	/// </remarks>
	public static void PrefetchTextures()
	{
		// The lab's loose reference textures are not what the bank names; nothing to warm.
		if (ReferenceTextureDirectory.Length != 0 || SharedBank() is not { } bank)
		{
			return;
		}

		if (System.Threading.Interlocked.Exchange(ref _prefetchStarted, 1) == 1)
		{
			return;
		}

		List<string> names = Engine_FxTexturePrefetch.Names(bank);
		bool prepare = PrefetchPrepares;
		foreach (string name in names)
		{
			if (SharedTextures.ContainsKey(name) || MissingTextures.Contains(name))
			{
				PrefetchedImages.MarkLoaded(name);
			}
		}

		var thread = new System.Threading.Thread(() =>
		{
			long started = System.Diagnostics.Stopwatch.GetTimestamp();
			PurgeStalePrefetchFiles();
			int decoded = 0;
			int prepared = 0;
			int missing = 0;
			long bytesHeld = 0;
			foreach (string name in names)
			{
				if (!PrefetchedImages.Wanted(name))
				{
					continue;
				}

				try
				{
					byte[]? bytes = ReadTextureBytes(name, TextureLeaf(name));
					Image? image = bytes is null ? null : Engine_TexImage.TryCreateImage(bytes, out _);
					if (image is null || image.IsEmpty())
					{
						++missing;
						continue;
					}

					if (prepare)
					{
						// The low-memory tier's image waits in a file (FableFx.PrefetchFiles.cs), which disposes the source.
						Image? kept = KeepPrefetchOnDisk(name, bytes!, image, out _);
						Image ready = kept ?? Engine_TextureUpload.PrepareAhead(image);
						if (!ReferenceEquals(ready, image))
						{
							if (kept is null) image.Dispose();
							image = ready;
							++prepared;
						}
					}

					if (PrefetchedImages.Offer(name, image))
					{
						++decoded;
						bytesHeld += image.GetDataSize();
					}
				}
				catch (Exception e)
				{
					++missing;
					GD.PushWarning($"[fx] prefetch of texture '{name}' failed: {e.Message}");
				}
			}

			double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
			GD.Print($"[fx] prefetched {decoded} of {names.Count} effect texture(s) off-thread in {ms:0} ms, " +
				$"{bytesHeld / 1048576.0:0.0} MB decoded and waiting, {missing} unresolved" +
				(prepare ? $", {prepared} prepared for upload (fx_prefetch_prepare)" : string.Empty));
		})
		{
			IsBackground = true,
			Name = "refii-fx-prefetch",
			Priority = System.Threading.ThreadPriority.BelowNormal,
		};
		thread.Start();
	}

	public const string MeshDispShaderPath = "res://src/Engine/fx/fx_mesh_disp.gdshader";

	private static MultiMeshInstance3D? BuildMeshNode(string meshPath, bool displacement = false)
	{
		string virtualPath = meshPath.Replace('\\', '/');
		if (!SharedMeshes.TryGetValue(virtualPath, out ArrayMesh? mesh) || (mesh is not null && !IsInstanceValid(mesh)))
		{
			byte[]? bytes = Engine_BnkMeshLoader.ReadBytes(virtualPath) ??
							Engine_BnkMeshLoader.ReadBytes("data/" + virtualPath);
			if (bytes is null)
			{
				SharedMeshes[virtualPath] = null;
				GD.Print($"[fx] mesh '{meshPath}' unresolved");
				return null;
			}

			mesh = Engine_MdlMesh.TryCreateMesh(
				bytes, virtualPath, out string error, out _);
			SharedMeshes[virtualPath] = mesh;
			if (mesh is null)
			{
				GD.Print($"[fx] mesh '{meshPath}' failed: {error}");
				return null;
			}
		}

		if (mesh is null)
		{
			return null;
		}

		var multi = new MultiMesh
		{
			TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
			UseColors = true,
			Mesh = mesh,
			InstanceCount = 16,
			VisibleInstanceCount = 0,
		};

		var node = new MultiMeshInstance3D
		{
			Name = "FxMesh_" + System.IO.Path.GetFileNameWithoutExtension(virtualPath),
			Multimesh = multi,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			ExtraCullMargin = 1f,
		};
		if (displacement)
		{
			var dispMat = new ShaderMaterial { Shader = LoadSharedShader(MeshDispShaderPath) };
			if (mesh.GetSurfaceCount() > 0 && mesh.SurfaceGetMaterial(0) is ShaderMaterial src && (src.GetShaderParameter(AlbedoTexName).As<Texture2D>() ?? Refii.Models.Engine_ClassicMaterial.Albedo(src)) is Texture2D albedo)
			{
				dispMat.SetShaderParameter(AlbedoTexName, albedo);
			}
			node.MaterialOverride = dispMat;
			node.Layers = Engine_FxDisplacement.LayerMask;
		}

		return node;
	}
}
