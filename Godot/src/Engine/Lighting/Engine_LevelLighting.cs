using System;
using Godot;
using Refii.Level;

namespace Refii.Lighting;

public static class Engine_LevelLighting
{
	public const string SunNodeName = "Sun";

	/// <summary>
	/// The sun cascades' reach. An occluder outside every cascade casts nothing, so too short a
	/// reach lets a low sun or moon light terrain straight through the hills beyond it.
	/// </summary>
	/// <remarks>`sun_shadow_distance`: how far the sun's cascades reach by day, in metres; 0 turns
	/// the sun's dynamic shadow off altogether, whatever the theme states. Reread on
	/// <see cref="ReloadConfig"/>, so a changed setting (a dev-menu cvar, a graphics preset switched
	/// in game) shows at once; <see cref="UpdateSun"/> applies it to the light.</remarks>
	public static float SunFarDepth => _sunFarDepth ??= ReadSunFarDepth();

	private static float? _sunFarDepth;

	/// <summary>The cascades' depth for building the light: the stated reach, or the default when it is off.</summary>
	private static float CascadeDepth => SunFarDepth > 0f ? SunFarDepth : 200f;

	private static float ReadSunFarDepth()
	{
		try
		{
			float depth = Refii.Core.Engine_RefiiConfig.Float(
				Refii.Core.Engine_RefiiConfig.Read(out _), "sun_shadow_distance", 200f);
			return depth <= 0f ? 0f : Mathf.Clamp(depth, 50f, 600f);
		}
		catch (System.Exception)
		{
			return 200f;
		}
	}

	public const float CascadeSplitNear = 35.0f;

	public const float CascadeSplitFour1 = 12.0f;

	public const float CascadeSplitFour2 = 34.0f;

	public const float CascadeSplitFour3 = 78.0f;

	public const float CascadeSplitMid = 110.0f;

	public const float CascadeSplitFar = 240.0f;

	public const float NearSlopeBias = 2.0f;

	public const float FourSplitSlopeBias = 1.30f;

	/// <summary>
	/// Computes normalized directional shadow cascade split ratios (<c>Split1, Split2, Split3</c>)
	/// for <paramref name="splits"/> and <paramref name="distance"/>. Pure helper for headless tests.
	/// </summary>
	public static (float Split1, float Split2, float Split3) ComputeCascadeSplits(int splits, float distance)
	{
		if (distance <= 0.0f)
		{
			return (0.1f, 0.3f, 0.6f);
		}

		float nearMetres = splits > 2 ? CascadeSplitFour1 : CascadeSplitNear;
		float split1 = Mathf.Clamp(nearMetres / distance, 0.05f, 0.5f);
		if (splits <= 2)
		{
			return (split1, 0.5f, 0.8f);
		}

		float split2 = Mathf.Clamp(CascadeSplitFour2 / distance, split1 + 0.05f, 0.8f);
		float split3 = Mathf.Clamp(Mathf.Min(CascadeSplitFour3, distance * 0.8f) / distance, split2 + 0.05f, 0.95f);
		return (split1, split2, split3);
	}

	/// <summary>`sun_shadow_splits`: cascades by day, 2 or 4 (defaults to 4 on desktop, 2 on mobile).</summary>
	public static int SunSplits => _sunSplits ??= ReadSunSplits();

	private static int? _sunSplits;

	private static int ReadSunSplits()
	{
		int defaultSplits = Refii.AndroidRuntime.AndroidBakedShadows.KeepsOneBake ? 2 : 4;
		try
		{
			return Refii.Core.Engine_RefiiConfig.Int(
				Refii.Core.Engine_RefiiConfig.Read(out _), "sun_shadow_splits", defaultSplits) >= 4 ? 4 : 2;
		}
		catch (System.Exception)
		{
			return defaultSplits;
		}
	}

	/// <summary>`sun_shadow_map`: the directional shadow atlas size; the biases scale with its texel.</summary>
	public static int SunShadowMap { get; set; } = ReadSunShadowMap();

	/// <summary>The sun's constant bias for a <paramref name="map"/>-texel shadow map: it only grows, under 4096.</summary>
	public static float SunBiasFor(int map) => 0.03f * Mathf.Max(1f, 4096f / map);

	private static int ReadSunShadowMap()
	{
		try
		{
			return Mathf.Clamp(Refii.Core.Engine_RefiiConfig.Int(
				Refii.Core.Engine_RefiiConfig.Read(out _), "sun_shadow_map", 4096), 512, 16384);
		}
		catch (System.Exception)
		{
			return 4096;
		}
	}

	/// <summary>A live lever: null follows the day set, true/false forces the sun's shadow.</summary>
	public static bool? SunShadowOverride;

	/// <summary>A live lever: null follows <c>baked_ao_enabled</c>, true/false forces screen-space AO.</summary>
	public static bool? SsaoOverride;

	/// <summary>Live lever: 1 (one orthogonal map), 2 or 4 cascades regardless of the sun's energy; null follows it.</summary>
	public static int? SunSplitsOverride;

	/// <summary>Live lever: whether the cascades blend across their boundaries; null keeps them blending.</summary>
	/// <remarks>Blending starts each farther cascade at the camera rather than at the split, so
	/// everything nearer than the first split is drawn into every cascade.</remarks>
	public static bool? SunBlendOverride;

	/// <summary>Below this energy the light is the moon and gets the night cascades.</summary>
	public const float NightEnergy = 0.35f;

	/// <summary>Shadow depth for the night cascades.</summary>
	public const float NightFarDepth = 150.0f;

	public static DirectionalLight3D BuildSun(LightingFrame frame)
	{
		var light = new DirectionalLight3D
		{
			Name = SunNodeName,
			DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits,
			DirectionalShadowMaxDistance = CascadeDepth,
			DirectionalShadowSplit1 = CascadeSplitNear / CascadeDepth,
			DirectionalShadowSplit2 = CascadeSplitMid / CascadeDepth,
			DirectionalShadowSplit3 = Mathf.Min(CascadeSplitFar, CascadeDepth * 0.8f) / CascadeDepth,

			// Blending starts each farther cascade at the camera rather than at the split,
			// which forces every near caster into every cascade. Disabled by default; overridable
			// via SunBlendOverride.
			DirectionalShadowBlendSplits = false,
			DirectionalShadowFadeStart = 0.85f,

			// NO CASCADES IN A REFLECTION: the reflected bit never selects a caster, so a mirror
			// camera, which sees only that bit, finds no casters (Engine_ReflectionLayer).
			ShadowCasterMask = Refii.Level.Engine_ReflectionLayer.CasterMask(~0u),

			// The normal bias is already in texels (Godot scales it by the shadow texel), so it
			// holds for any map or reach. The constant bias is depth in world units: it only
			// grows, for a map under 4096; scaling it down stripes sunlit ground at a grazing sun.
			ShadowNormalBias = NearSlopeBias,
			ShadowBias = SunBiasFor(SunShadowMap),
		};

		UpdateSun(light, frame);
		return light;
	}

	private static float? _sunScale;

	public static void ReloadConfig()
	{
		_sunFarDepth = null;
		_sunSplits = null;
		_sunScale = null;
		_giHalfResolution = null;
		_sdfgiEnabled = null;
		SunShadowMap = ReadSunShadowMap();
	}

	public static void ApplyWeatherSun(DirectionalLight3D light, LightingFrame frame)
	{
		float energy = (Refii.Level.Engine_DynamicSkyLighting.OverridesSun
			? Refii.Level.Engine_DynamicSkyLighting.SunEnergy
			: frame.SunEnergy * Refii.Level.Engine_DynamicSkyLighting.SunTransmission) * SunScale;
		if (light.LightEnergy != energy) light.LightEnergy = energy;
		if (Refii.Level.Engine_DynamicSkyLighting.OverridesSun)
		{
			Vector3 colour = Refii.Level.Engine_DynamicSkyLighting.SunColour;
			var linear = new Color(colour.X, colour.Y, colour.Z);
			if (light.LightColor != Engine_LightColour.ForGodot(linear)) Engine_LightColour.Apply(light, linear);
		}
	}

	/// <summary>
	/// The remaster's `sun_light_scale`. The shipped balance keeps the direct sun mild against
	/// the bakes — people and ground barely react to it — and the user asked for the opposite.
	/// 1.0 is the shipped energy exactly.
	/// </summary>
	private static float SunScale
	{
		get
		{
			if (_sunScale is null)
			{
				try
				{
					_sunScale = Math.Clamp(
						Refii.Core.Engine_RefiiConfig.Float(
							Refii.Core.Engine_RefiiConfig.Read(out _), "sun_light_scale", 1.35f),
						0.0f, 4.0f);
				}
				catch (Exception)
				{
					_sunScale = 1.35f;
				}
			}

			return _sunScale.Value;
		}
	}

	/// <summary>
	/// Whether Godot's sun casts: the debug override, else the frame's. Never under classic: every
	/// classic material is unshaded, and its sun shadow is the title's own cascades
	/// (Engine_ClassicShadows), so Godot's maps would be drawn and read by nothing.
	/// </summary>
	public static bool SunCasts(bool frameShadow) =>
		SunShadowOverride ?? (frameShadow && SunFarDepth > 0f && !Refii.Models.Engine_ClassicMaterial.Active);

	public static void UpdateSun(DirectionalLight3D light, LightingFrame frame)
	{
		Refii.AndroidRuntime.AndroidInteriorSun.Apply(light, frame.HasSky);
		if (!Refii.Level.Engine_DynamicSkyLighting.OverridesSun)
		{
			// Linear (the theme's value through ToLinearInput), so it goes in encoded like every
			// other guest light colour: Godot decodes LightColor before it shades.
			var linear = new Color(frame.SunColour[0], frame.SunColour[1], frame.SunColour[2]);
			if (light.LightColor != Engine_LightColour.ForGodot(linear)) Engine_LightColour.Apply(light, linear);
		}
		ApplyWeatherSun(light, frame);
		bool shadows = SunCasts(frame.SunShadow);
		if (shadows)
		{
			// Before the light casts: lite shaders built without shadow sampling go back (lite_shadowless).
			shadows = Refii.Models.Engine_ShaderSpecialisation.ShadowCast("the sun");
		}
		if (light.ShadowEnabled != shadows) light.ShadowEnabled = shadows;

		float sunDayFactor = Math.Clamp(frame.SunDirection[1] / 0.20f, 0.0f, 1.0f);
		float desiredVolumetricEnergy = frame.HasSky ? Mathf.Lerp(0.85f, 4.0f, sunDayFactor) : 0.0f;
		if (MathF.Abs(light.LightVolumetricFogEnergy - desiredVolumetricEnergy) > 0.001f)
		{
			light.LightVolumetricFogEnergy = desiredVolumetricEnergy;
		}

		// Exclude town building interiors (IndoorMask / IndoorLampHostMask) from the outdoor
		// directional sun/moon light so sunlight never leaks through walls or roofs onto indoor
		// geometry when sun shadows are disabled or beyond cascade depth.
		uint desiredCullMask = (Engine_LightZones.Active && frame.HasSky)
			? ~(Engine_LightZones.IndoorMask | Engine_LightZones.IndoorLampHostMask | Refii.Models.Engine_ClassicViews.IndoorViewLayers | Refii.Fx.Engine_ModernDecals.StaticDecalReceiverLayerMask)
			: uint.MaxValue;
		if (light.LightCullMask != desiredCullMask)
		{
			light.LightCullMask = desiredCullMask;
		}

		// CASCADES BY STRENGTH. The moon at energy 0.2 cast the same four cascades over the
		// whole market as the noon sun: 5,000 draw calls of shadow for a light nobody can see
		// the shadows of. Two splits over a shorter depth at night; the day keeps four.
		// Every cascade redraws every caster in its slice. `sun_shadow_splits` picks two
		// (the near split to 35 m at half the map, the rest to the reach) or four.
		float activeEnergy = Refii.Level.Engine_DynamicSkyLighting.OverridesSun
			? Refii.Level.Engine_DynamicSkyLighting.SunEnergy : frame.SunEnergy;
		bool isNight = activeEnergy < NightEnergy;
		int splits = SunSplitsOverride ?? (isNight ? 2 : SunSplits);
		var mode = splits <= 1 ? DirectionalLight3D.ShadowMode.Orthogonal
			: splits == 2 ? DirectionalLight3D.ShadowMode.Parallel2Splits
			: DirectionalLight3D.ShadowMode.Parallel4Splits;
		bool blend = SunBlendOverride ?? (splits >= 4);
		if (light.DirectionalShadowBlendSplits != blend) light.DirectionalShadowBlendSplits = blend;
		float distance = isNight ? Mathf.Min(NightFarDepth, CascadeDepth * 0.6f) : CascadeDepth;
		if (light.DirectionalShadowMode != mode) light.DirectionalShadowMode = mode;
		if (light.DirectionalShadowMaxDistance != distance) light.DirectionalShadowMaxDistance = distance;
		if (light.DirectionalShadowFadeStart != 0.85f) light.DirectionalShadowFadeStart = 0.85f;

		float desiredNormalBias = splits >= 4 ? FourSplitSlopeBias : NearSlopeBias;
		if (MathF.Abs(light.ShadowNormalBias - desiredNormalBias) > 0.01f)
		{
			light.ShadowNormalBias = desiredNormalBias;
		}

		if (distance > 0f)
		{
			(float split1, float split2, float split3) = ComputeCascadeSplits(splits, distance);
			if (Mathf.Abs(light.DirectionalShadowSplit1 - split1) > 0.001f)
			{
				light.DirectionalShadowSplit1 = split1;
			}
			if (splits > 2)
			{
				if (Mathf.Abs(light.DirectionalShadowSplit2 - split2) > 0.001f) light.DirectionalShadowSplit2 = split2;
				if (Mathf.Abs(light.DirectionalShadowSplit3 - split3) > 0.001f) light.DirectionalShadowSplit3 = split3;
			}
		}

		// IN WORLD SPACE, because the node sits under the level root's Scale(-1, 1, 1). A local
		// transform inherits that mirror, and the sun's world aim came out with its X flipped —
		// the agreement diagnostic printed rigDot = -0.888 where -1.0 is correct, travel
		// negating Y and Z but not X — so the light and its shadows arrived from the wrong
		// east/west side of everything the frame stated. Assigning the GLOBAL transform
		// composes the parent chain away, mirror included; before the node enters the tree the
		// local assignment stands in (its parent applies the same mirror to the region, so the
		// first configured frame corrects it).
		Vector3 toward = Refii.Level.Engine_DynamicSkyLighting.SunToward;
		Basis basis = Refii.Level.Engine_DynamicSkyLighting.OverridesSun
			? Basis.LookingAt(-toward, MathF.Abs(toward.Y) > 0.99f ? Vector3.Forward : Vector3.Up)
			: SunBasis(frame.LightDirection);
		var aimed = new Transform3D(basis, Vector3.Zero);
		bool inTree = light.IsInsideTree();
		Transform3D currentTransform = inTree ? light.GlobalTransform : light.Transform;
		// Smooth display-rate sun rotation driven by SunClockInterpolator (zero 0.085 deg step jumps;
		// exact 1e-12 epsilon skips redundant server updates while the clock is frozen).
		if ((currentTransform.Basis.Z - aimed.Basis.Z).LengthSquared() > 1e-12f ||
		    (currentTransform.Basis.Y - aimed.Basis.Y).LengthSquared() > 1e-12f)
		{
			Refii.Core.Engine_InstanceUpdates.Note(Refii.Core.Engine_InstanceUpdates.Source.Light);
			if (inTree) light.GlobalTransform = aimed;
			else light.Transform = aimed;
		}

		ReportSun(frame);
	}

	private static float _reportedEnergy = float.NaN;
	private static bool _reportedShadow;
	private static float _reportedAmbient = float.NaN;

	/// <summary>
	/// Prints the directional light and the ambient floor whenever either moves materially.
	/// Self-limiting rather than periodic: the interesting question is what the values ARE, and
	/// they only change as the day does. `direct` and `ambient` are the two halves of the guest's
	/// `albedo * (direct + bake)` — if a street reads black, this says which half is missing.
	/// </summary>
	private static void ReportSun(LightingFrame frame)
	{
		float ambient = frame.HasAmbient ? frame.AmbientEnergy : 0f;
		bool moved = float.IsNaN(_reportedEnergy)
			|| MathF.Abs(frame.SunEnergy - _reportedEnergy) > 0.05f
			|| MathF.Abs(ambient - _reportedAmbient) > 0.02f
			|| frame.SunShadow != _reportedShadow;
		if (!moved)
		{
			return;
		}

		_reportedEnergy = frame.SunEnergy;
		_reportedAmbient = ambient;
		_reportedShadow = frame.SunShadow;
		GD.Print(
			$"[lighting] tod={frame.TimeOfDay:0.00}h sun=({frame.SunColour[0]:0.000}, " +
			$"{frame.SunColour[1]:0.000}, {frame.SunColour[2]:0.000}) energy={frame.SunEnergy:0.000} " +
			$"shadow={frame.SunShadow} dir=({frame.LightDirection[0]:0.00}, " +
			$"{frame.LightDirection[1]:0.00}, {frame.LightDirection[2]:0.00})  " +
			$"ambient={(frame.HasAmbient ? $"({frame.AmbientColour[0]:0.000}, {frame.AmbientColour[1]:0.000}, {frame.AmbientColour[2]:0.000}) x{frame.AmbientEnergy:0.000}" : "NONE")}");
	}

	/// <summary>
	/// The luminance that tone mapping maps to white. A STAND-IN — see below.
	///
	/// The guest's curve is a 512-entry 1D LUT that <c>sub_8217E428</c> rebuilds on the CPU
	/// every frame over the input domain [0, 4] — the entries are stored as a ratio against
	/// <c>((i + 0.5) / 512) * 4.0</c> at 0x8217E72C, so the LUT holds a GAIN rather than an
	/// output colour, which is exactly how the tone-map pixel shader uses it:
	///
	///     r0.x   = tex1D(g_ToneMapSampler, luminance * g_RecipMaxLuminance.x).x
	///     colour = saturate(brightness * r0.x * colour)
	///
	/// and <c>g_RecipMaxLuminance</c> is the literal 0.25, so the domain really is [0, 4].
	///
	/// FOUR IS THE DOMAIN BOUND, NOT THE WHITE POINT. The white point is not authored anywhere
	/// — the GDB has no such field — it is derived per frame from the adapted luminance triple:
	///
	///     v3  = *(a1+0x41A0) - *(a1+0x4198)        adaptedMax - adaptedMin
	///     v18 = 1.0 / (v3 * v3)                    1/W^2, at 0x8217E530
	///
	/// Reproducing it therefore needs the triple, which comes from a CONVERTTOLUMINANCE resolve
	/// of the previous frame binned into a CPU histogram (<c>sub_822AA9A0</c>). We do not render
	/// the guest's frame, so there is no triple to read and this stays a constant.
	/// </summary>
	public const float TonemapWhite = 4.0f;

	/// <summary>
	/// A STATIC STAND-IN FOR THE GUEST'S AUTO-EXPOSURE, which Godot 4 cannot reproduce.
	///
	/// The guest's post-process is two stages and the curve is the second one. First
	/// <c>sub_822AA9A0</c> locks the previous frame's luminance surface, bins 1024 histogram
	/// buckets on the CPU and takes percentile thresholds; then exposure walks toward putting
	/// that average on MIDDLE_GRAY before the Reinhard curve sees anything. Shipping the curve
	/// without the exposure is worse than shipping neither — it is designed to receive a signal
	/// that has already been normalised, so everything lands on the toe and the region reads as
	/// dusk at two in the afternoon.
	///
	/// The value comes from two stated defaults rather than from taste. MIDDLE_GRAY is 0.45 and
	/// AVG_LUMINANCE_LOWER_BOUND is 0.15 (both from the theme's default constructor
	/// <c>sub_82A45DD0</c>), and the measured median luminance of this region at midday is
	/// 0.026 — well under that floor, so the guest's own clamp is what would apply.
	///
	/// THE FORM IS NOT QUITE THE GUEST'S, and it is worth being honest about which part is
	/// approximated. The key the curve actually multiplies by is a DIFFERENCE, not a ratio
	/// against the average alone (0x8217E524):
	///
	///     v15 = *(a1+0x2C) / (*(a1+0x419C) - *(a1+0x4198))    middleGray / (avg - min)
	///
	/// so the denominator is the adapted average above the adapted MINIMUM. Both of those are
	/// per-frame histogram outputs, and a static stand-in cannot honour a difference between
	/// two values it does not have — picking floors for them would be inventing the numerator
	/// and denominator both. The ratio below is kept because it produces a working image on the
	/// region it was measured against; it is not the guest's arithmetic.
	///
	/// Also unmodelled: the curve is not pure Reinhard. Each entry is lerped against a contrast
	/// term by <c>*(a1+0x40)</c>, where the contrast is <c>pow(x, *(a1+0x44))</c> with a
	/// squaring fast path when that exponent is 2.0 (0x8217E51C). Do not confuse that squaring
	/// with the colour-texture decode — they are unrelated.
	///
	/// Nothing reads this field. Live exposure is the guest's own loop (`sub_82191C18`), run per
	/// frame against a luminance histogram in `Engine_PostFxEffect` -> `fable_postfx_reduce.glsl`.
	/// Kept only to record the value the Godot Environment would want beside its derivation.
	/// </summary>
	public const float TonemapExposure = 1.0f;

	/// <summary>The middle grey the guest drives the adapted average toward.</summary>
	/// <remarks>
	/// The theme's MIDDLE_GRAY, default 0.45, and the numerator of the curve's key at
	/// 0x8217E524. Stated by the game rather than chosen.
	/// </remarks>
	public const float AutoExposureTargetGrey = 0.45f;

	/// <summary>
	/// How fast exposure follows the scene, as a rate per second.
	/// </summary>
	/// <remarks>
	/// Derived from the guest's own smoother rather than picked. It blends by
	/// <c>1 - (1 - ExposureSpeed)^(dt * 30)</c> with ExposureSpeed 0.01 (0x82276810), so over one
	/// second it closes <c>1 - 0.99^30 = 0.260</c> of the gap. An exponential approach with rate
	/// k closes <c>1 - e^-k</c>, so k = -ln(1 - 0.260) = 0.30.
	/// </remarks>
	public const float AutoExposureSpeed = 0.30f;

	/// <summary>
	/// The guest's auto-exposure, as close as Godot's own control loop gets to it.
	/// </summary>
	/// <remarks>
	/// Godot's own auto-exposure is off deliberately: the guest's loop runs instead, in
	/// `fable_postfx_reduce.glsl`, and two control loops would fight over the same frame.
	/// That loop follows `sub_82191C18`:
	///
	///   - curve key `middleGray / (adaptedAvg - adaptedMin)`, computed as
	///     `knee / max(SmMid - SmBlack, 1e-4)`;
	///   - exposure scalar clamped to [1, 16], twice, in the guest's units;
	///   - all 512 curve entries smoothed, not just the scalar.
	/// </remarks>
	public static CameraAttributesPractical BuildCameraAttributes() => new()
	{
		AutoExposureEnabled = false,
	};

	/// <summary>
	/// The compositor that runs the guest's tone curve, or null where compute is unavailable.
	/// </summary>
	/// <remarks>
	/// One per camera. The effect owns GPU resources keyed to a rendering device, so it is built
	/// here rather than shared as a singleton — a level change disposes the camera and the effect
	/// frees its pipelines with it.
	/// </remarks>
	/// <param name="view">The view the chain draws (<see cref="Engine_PostFxEffect.View"/>): 0 the
	/// window's or player one's, 1 player two's on split screen.</param>
	/// <param name="splitCamera">Built for a split camera (GameSplitView): its per-view effects may hold their view's
	/// slot while split; the window camera's may not (Refii.Core.Engine_Views.MayClaimViewSlot).</param>
	public static Compositor? BuildCompositor(int view = 0, bool splitCamera = false)
	{
		if (RenderingServer.GetRenderingDevice() is null)
		{
			GD.Print("[lighting] no rendering device; tone curve disabled for this renderer");
			return null;
		}

		var effects = new Godot.Collections.Array<CompositorEffect>
		{
			new Refii.Fx.Engine_FxDepthCaptureEffect { View = view, SplitCamera = splitCamera },
		};
		if (!Refii.AndroidRuntime.AndroidBakedShadows.KeepsOneBake &&
			string.Equals(RenderingServer.GetCurrentRenderingMethod(), "forward_plus", StringComparison.Ordinal))
		{
			effects.Add(new Engine_ScreenSpaceAoEffect { View = view, SplitCamera = splitCamera });
		}
		if (Refii.Models.Engine_ClassicMaterial.Active)
		{
			// The classic look's screen-space sun shadow mask, between the depth prepass and the
			// opaque colour pass, as the title draws it.
			effects.Add(new Refii.Models.Engine_ClassicScreenShadow());
		}

		if (Engine_PostFxParams.ChainRequested)
		{
			effects.Add(new Engine_PostFxEffect { View = view, SplitCamera = splitCamera });
		}
		else
		{
			GD.Print($"[lighting] {Engine_PostFxParams.ModeEnvironment}=off; the post-process chain is not attached");
		}

		// Disabled until a co-op view asks it to start the temporal history over (GameSplitView).
		effects.Add(new Engine_TemporalHistoryEffect());

		var compositor = new Compositor { CompositorEffects = effects };
		GC.KeepAlive(effects);
		return compositor;
	}

	/// <summary>
	/// Detaches <paramref name="camera"/>'s compositor and deterministically frees its RD resources
	/// while the render thread is still active, avoiding shutdown RID leaks from late Resource finalization.
	/// </summary>
	public static void ReleaseCompositor(Camera3D? camera, bool releaseStaticFallbacks = false)
	{
		if (camera is null || !GodotObject.IsInstanceValid(camera))
		{
			return;
		}

		Compositor? compositor = camera.Compositor;
		camera.Compositor = null;
		if (compositor is null || !GodotObject.IsInstanceValid(compositor))
		{
			return;
		}

		if (compositor.CompositorEffects is { Count: > 0 } effects)
		{
			for (int i = 0; i < effects.Count; i++)
			{
				switch (effects[i])
				{
					case Engine_PostFxEffect postFx:
						postFx.ReleaseNow(releaseStaticFallbacks);
						break;
					case Refii.Fx.Engine_FxDepthCaptureEffect depthCapture:
						depthCapture.ReleaseNow(releaseStaticFallbacks);
						break;
					case Engine_ScreenSpaceAoEffect ssaoEffect:
						ssaoEffect.ReleaseNow(releaseStaticFallbacks);
						break;
					case Refii.Models.Engine_ClassicScreenShadow classicShadow:
						classicShadow.ReleaseNow(releaseStaticFallbacks);
						break;
				}
			}

			effects.Clear();
		}

		if (!Refii.Core.Engine_RdHealth.IsTestHost && RenderingServer.GetRenderingDevice() is not null)
		{
			Refii.Core.Engine_RdRetire.OnRenderThread(() =>
				Refii.Core.Engine_RdRetire.FlushAll(RenderingServer.GetRenderingDevice()));
			if (!RenderingServer.IsOnRenderThread())
			{
				RenderingServer.ForceSync();
			}
		}
	}

	/// <summary>
	/// Godot's dynamic GI, on for the modern model. Static geometry is voxelised as it streams in —
	/// every mesh and MultiMesh at the default static GI mode (renderer_scene_cull.cpp takes any
	/// geometry flagged USES_BAKED_LIGHT) — and guest-driven entities receive it as dynamic objects.
	/// Placed lights enter it at <see cref="Light3D.BakeMode.Static"/>: baked into a cascade when
	/// that cascade refreshes rather than re-injected every update, which is what 124 fixtures in a
	/// town need, and it keeps a torch's flicker out of the bounce.
	/// </summary>
	private static bool? _giHalfResolution;

	private static bool _giHalfApplied;

	private static bool _giHalfEverApplied;

	/// <summary>
	/// `gi_half_resolution`, ON by default: the per-pixel gather of the GI runs at half the frame's
	/// resolution and is upsampled against depth and normals. The cascades, the lights in them and
	/// the bounce are untouched — only how finely the result is resolved on screen. Measured
	/// 2026-09-21 on Bowerstone Market stills: 0.75 % of pixels move perceptibly by day and 0.03 % by
	/// night, all of them on silhouettes; GI as a whole was 2.5 ms of a 9.5 ms GPU frame at 1440p.
	/// Settable live for the comparison.
	/// </summary>
	public static bool GiHalfResolution
	{
		get
		{
			if (_giHalfResolution is null)
			{
				try
				{
					_giHalfResolution = Refii.Core.Engine_RefiiConfig.Bool(
						Refii.Core.Engine_RefiiConfig.Read(out _), "gi_half_resolution", true);
				}
				catch (System.Exception)
				{
					_giHalfResolution = true;
				}
			}

			return _giHalfResolution.Value;
		}
		set => _giHalfResolution = value;
	}

	private static bool? _sdfgiEnabled;

	/// <summary>
	/// Whether real-time bounced sky and surface global illumination (<c>sdfgi_enabled</c>) is active
	/// in Modern mode. Defaults to true; disabling keeps Modern direct lighting, sky-tethered ambient
	/// fill, and ambient occlusion while shedding SDFGI probe/cascade GPU cost on integrated GPUs.
	/// </summary>
	public static bool SdfgiEnabled
	{
		get
		{
			if (_sdfgiEnabled is null)
			{
				try
				{
					_sdfgiEnabled = Refii.Core.Engine_RefiiConfig.Bool(
						Refii.Core.Engine_RefiiConfig.Read(out _), "sdfgi_enabled", true);
				}
				catch (System.Exception)
				{
					_sdfgiEnabled = true;
				}
			}

			return _sdfgiEnabled.Value;
		}
		set => _sdfgiEnabled = value;
	}

	public const int DefaultSdfgiCascades = 4;

	private static void ConfigureGlobalIllumination(Godot.Environment environment, bool unified, bool hasSky)
	{
		// A renderer-wide switch, not the environment's; written only when it changes.
		bool half = GiHalfResolution;
		if (!_giHalfEverApplied || half != _giHalfApplied)
		{
			RenderingServer.GISetUseHalfResolution(half);
			_giHalfApplied = half;
			_giHalfEverApplied = true;
		}

		// A rebuild in progress holds it off for a frame or two; see Engine_GiRefresh.
		bool enableGi = unified && SdfgiEnabled;
		bool sdfgiOn = enableGi &&
			!Engine_GiRefresh.HoldingOff &&
			!Engine_GiRefresh.ForcedOff &&
			!Engine_GiRefresh.HoldForWorldLoad &&
			!Engine_GiRefresh.SettlingAfterWorldLoad;
		if (environment.SdfgiEnabled != sdfgiOn) environment.SdfgiEnabled = sdfgiOn;
		if (!enableGi)
		{
			return;
		}

		if (Refii.AndroidRuntime.AndroidEnvironmentWrites.Unchanged(environment, 0, hasSky ? 1f : 2f)) return;
		// Cascade 0 is 0.2 m cells over 25.6 m and each of the four doubles it (covering 204.8 m,
		// matching SunFarDepth = 200 m) while avoiding culling the entire region two extra times
		// into RENDER_LIST_SECONDARY for 409.6 m / 819.2 m cascades on Direct3D 12.
		if (environment.SdfgiCascades != DefaultSdfgiCascades) environment.SdfgiCascades = DefaultSdfgiCascades;
		if (environment.SdfgiMinCellSize != 0.2f) environment.SdfgiMinCellSize = 0.2f;
		if (environment.SdfgiYScale != Godot.Environment.SdfgiyScale.Scale75Percent) environment.SdfgiYScale = Godot.Environment.SdfgiyScale.Scale75Percent;
		if (environment.SdfgiUseOcclusion != true) environment.SdfgiUseOcclusion = true;
		if (environment.SdfgiReadSkyLight != hasSky) environment.SdfgiReadSkyLight = hasSky;
		if (environment.SdfgiBounceFeedback != 0.5f) environment.SdfgiBounceFeedback = 0.5f;
		if (environment.SdfgiEnergy != 1.0f) environment.SdfgiEnergy = 1.0f;
	}

	private static int _lastSsaoQualityTier = -1;

	/// <summary>
	/// Whether the Forward+ <see cref="Engine_ScreenSpaceAoEffect"/> PreOpaque GTAO pass should run this frame.
	/// </summary>
	public static bool ShouldRunCustomScreenSpaceAo() =>
		!Refii.Core.Engine_AppRoot.IsTestHost &&
		!Refii.Models.Engine_ClassicMaterial.Active &&
		!Refii.AndroidRuntime.AndroidBakedShadows.KeepsOneBake &&
		string.Equals(RenderingServer.GetCurrentRenderingMethod(), "forward_plus", StringComparison.Ordinal) &&
		(SsaoOverride ?? (Engine_LightingFrame.ModernModel &&
			Engine_LevelAmbientOcclusion.BakedAoEnabled &&
			Engine_LevelAmbientOcclusion.BakedAoScale > 0.01f));

	private static void ConfigureScreenSpaceAo(Godot.Environment environment, bool unified)
	{
		// On Forward+, Engine_ScreenSpaceAoEffect runs unified dual-scale GTAO in PreOpaque and folds
		// directly into combined_ao (covering custom light(), EMISSION, depth_prepass_alpha cutouts,
		// and Debug View 11). Disable Godot's opaque-only Environment SSAO so GPU work is never duplicated.
		if (!Refii.AndroidRuntime.AndroidBakedShadows.KeepsOneBake &&
		    string.Equals(RenderingServer.GetCurrentRenderingMethod(), "forward_plus", StringComparison.Ordinal))
		{
			Engine_ScreenSpaceAoEffect.RegisterGlobals();
			if (environment.SsaoEnabled) environment.SsaoEnabled = false;
			return;
		}

		bool enableSsao = SsaoOverride ?? (unified &&
			Engine_LevelAmbientOcclusion.BakedAoEnabled &&
			Engine_LevelAmbientOcclusion.BakedAoScale > 0.01f &&
			!Refii.AndroidRuntime.AndroidBakedShadows.KeepsOneBake);
		if (environment.SsaoEnabled != enableSsao) environment.SsaoEnabled = enableSsao;
		if (!enableSsao)
		{
			return;
		}

		int aoRes = Engine_LevelAmbientOcclusion.TerrainAoResolution;
		int tier = aoRes <= 2048 ? 1 : (aoRes <= 4096 ? 2 : 3);
		if (_lastSsaoQualityTier != tier)
		{
			_lastSsaoQualityTier = tier;
			var quality = tier == 1 ? RenderingServer.EnvironmentSsaoQuality.Low
				: tier == 2 ? RenderingServer.EnvironmentSsaoQuality.Medium
				: RenderingServer.EnvironmentSsaoQuality.High;
			RenderingServer.EnvironmentSetSsaoQuality(
				quality,
				tier == 1,
				0.5f,
				2,
				35.0f,
				55.0f);
		}

		if (Refii.AndroidRuntime.AndroidEnvironmentWrites.Unchanged(environment, 1, Engine_LevelAmbientOcclusion.BakedAoScale)) return;
		if (environment.SsaoRadius != 0.52f) environment.SsaoRadius = 0.52f;
		float ssaoIntensity = 1.65f * Math.Clamp(Engine_LevelAmbientOcclusion.BakedAoScale, 0.0f, 2.0f);
		if (environment.SsaoIntensity != ssaoIntensity) environment.SsaoIntensity = ssaoIntensity;
		if (environment.SsaoPower != 1.35f) environment.SsaoPower = 1.35f;
		if (environment.SsaoDetail != 1.00f) environment.SsaoDetail = 1.00f;
		if (environment.SsaoHorizon != 0.08f) environment.SsaoHorizon = 0.08f;
		if (environment.SsaoSharpness != 0.85f) environment.SsaoSharpness = 0.85f;
		if (environment.SsaoLightAffect != 0.34f) environment.SsaoLightAffect = 0.34f;
	}

	public static void ConfigureEnvironment(Godot.Environment environment, LightingFrame frame)
	{
		if (environment is null)
		{
			return;
		}

		if (environment.TonemapMode != Godot.Environment.ToneMapper.Linear) environment.TonemapMode = Godot.Environment.ToneMapper.Linear;

		// ONE FILL FOR EVERY SURFACE, AND GODOT CARRIES IT. The lit shaders no longer switch
		// Godot's ambient off, so exactly one side may supply the fill or it is counted twice:
		//   modern — Godot: this ambient plus the GI below. `fable_unified` = 1 stops every
		//            per-instance guest fill (flat ambient, probe, vertex bake, lightmap) from
		//            emitting, so a character and the wall beside him are lit by the same data
		//            and both take the bounce of the lights that are actually there.
		//   fable  — the guest: the shaders emit the 360's own fills and Godot's ambient has no
		//            energy, which is what `ambient_light_disabled` used to guarantee.
		// In sky-less caves (`!frame.HasSky`), `unified` remains active in the modern model so
		// Godot's SDFGI (`SdfgiReadSkyLight = false`) traces and bounces the cave's dynamic
		// torches, braziers and emissive surfaces, while `fable_fill_floor` provides the cave
		// theme's `LightmapColour` ambient share.
		bool unified = Engine_LightingFrame.ModernModel;
		LastUnified.Set(FableUnifiedName, unified ? 1.0f : 0.0f);
		Engine_LevelAmbientOcclusion.PublishAoParameters();
		ConfigureGlobalIllumination(environment, unified, frame.HasSky);
		ConfigureScreenSpaceAo(environment, unified);

		bool sdfgiActive = environment.SdfgiEnabled;
		float floorShare = sdfgiActive ? Engine_LightingFrame.ModernFloorShare : 0.15f;
		bool routeThemeAmbient = unified && !sdfgiActive;
		Engine_LevelBakedLighting.PublishThemeLighting(
			frame,
			routeThemeAmbient ? floorShare : 1.0f,
			out Vector3 themeAmbient);

		// Sky reflections are not part of this: the materials carry their own environment map.
		if (environment.ReflectedLightSource != Godot.Environment.ReflectionSource.Disabled) environment.ReflectedLightSource = Godot.Environment.ReflectionSource.Disabled;

		// NO SKY, MODERN MODEL: interior dials apply where the theme states no main light.
		// A painted day set is not a roof: painted sets author mood over OUTDOOR cells and the
		// data has no interior flag (`Engine_PaintedDaySets`). Gating on SkylightWeight
		// (`hasSky ? 1 - PaintedWeight : 0`) flipped the whole screen when PaintedWeight clamped
		// to exactly 1, and the painted set's ambient is already folded in by its stated weight.
		// The gate is the theme stating no main light; the interior dials are remaster choices.
		bool interior = Engine_LightingFrame.ModernModel && !frame.HasSky;
		Engine_LightingFrame.CurrentInterior = interior;

		float ambientScale = Engine_LevelBakedLighting.AmbientScale;
		if (unified)
		{
			// GODOT'S AMBIENT IS THE GI'S SKY LIGHT, NOT A FLOOR: inside a cascade the engine replaces
			// it with the trace (`ambient_light = diffuse`), so it lands only where the sky is seen
			// — which is what the guest's BAKE did. When SDFGI is disabled, we also fold `(1 - floorShare)`
			// of the theme's `AmbientColour * AmbientEnergy` into Godot's AmbientLight alongside `skyBake`
			// (leaving `floorShare` in `fable_ambient_colour`), preserving exact energy conservation while
			// allowing Godot's per-pixel SSAO to occlude architectural crevices day and night.
			float skyBakeR = Engine_LightingFrame.MeanAuthoredBake * frame.LightmapColour[0];
			float skyBakeG = Engine_LightingFrame.MeanAuthoredBake * frame.LightmapColour[1];
			float skyBakeB = Engine_LightingFrame.MeanAuthoredBake * frame.LightmapColour[2];
			float[] godotAmbient =
			{
				skyBakeR + (routeThemeAmbient ? themeAmbient.X : 0f),
				skyBakeG + (routeThemeAmbient ? themeAmbient.Y : 0f),
				skyBakeB + (routeThemeAmbient ? themeAmbient.Z : 0f),
			};
			if (frame.HasSky || !sdfgiActive)
			{
				float peak = MathF.Max(godotAmbient[0], MathF.Max(godotAmbient[1], godotAmbient[2]));
				if (environment.AmbientLightSource != Godot.Environment.AmbientSource.Color) environment.AmbientLightSource = Godot.Environment.AmbientSource.Color;
				Color skyColour = peak > 0f
					? Engine_LightColour.ForGodot(new Color(godotAmbient[0] / peak, godotAmbient[1] / peak, godotAmbient[2] / peak))
					: Colors.Black;
				if (environment.AmbientLightColor != skyColour) environment.AmbientLightColor = skyColour;
				float traced = 1f - floorShare;
				float skyEnergy = peak * traced * ambientScale;
				if (environment.AmbientLightEnergy != skyEnergy) environment.AmbientLightEnergy = skyEnergy;
			}
			else
			{
				// NONE MEANS NONE, not "whatever was here". The Environment outlives the region that
				// last configured it, and a frame with no sky must not keep the previous region's
				// sky ambient (b175). SDFGI in caves traces the dynamic lights alone.
				if (environment.AmbientLightSource != Godot.Environment.AmbientSource.Disabled) environment.AmbientLightSource = Godot.Environment.AmbientSource.Disabled;
				if (environment.AmbientLightEnergy != 0f) environment.AmbientLightEnergy = 0f;
			}

			float floor = floorShare * ambientScale;
			LastFillFloor.Set(FableFillFloorName, new Vector3(skyBakeR * floor, skyBakeG * floor, skyBakeB * floor));
		}
		else
		{
			if (environment.AmbientLightSource != Godot.Environment.AmbientSource.Disabled) environment.AmbientLightSource = Godot.Environment.AmbientSource.Disabled;
			if (environment.AmbientLightEnergy != 0f) environment.AmbientLightEnergy = 0f;
			LastFillFloor.Set(FableFillFloorName, Vector3.Zero);
		}

		Refii.AndroidRuntime.AndroidModernAmbientFold.Publish(environment);

		// THE PROBE FIELD'S SKY TETHER. Published in ambient_light_scale space because the
		// probe L0 it mixes with carries the same scale. Every caller that moves the lighting
		// frame comes through here, so the global tracks the day exactly as the theme does.
		// Weight 0 (the fable model, or a theme with no ambient) leaves the probes authored.
		// And it lets go where there is no sky to tether to: a cave whose theme states no main
		// light, or a room painted with its own ambient (frame.SkylightWeight carries both).
		float tether = Engine_LightingFrame.ModernModel && frame.HasAmbient
			? Engine_LightingFrame.ModernProbeSkyBlend * Math.Clamp(frame.SkylightWeight, 0f, 1f)
			: 0f;
		Engine_LevelBakedLighting.InteriorNow = interior ? 1.0f : 0.0f;
		Engine_LevelBakedLighting.InteriorBakeNow =
			interior ? Engine_LightingFrame.InteriorBakeScale : 1.0f;
		LastInterior.Set(FableInteriorName, interior ? 1.0f : 0.0f);
		// The bent-normal reference: the rig's sun direction, dome convention like `atmos_sun`.
		LastAmbientRef.Set(FableAmbientRefName, Refii.Level.Engine_SkyFrame.ToDome(frame.AmbientReference));
		float interiorContrastFactor = interior
			? ComputeInteriorFogContrastFactor(
				Refii.Level.Engine_SkyEnvironment.LastAtmosphereState,
				frame)
			: 1.0f;
		float effectiveFogScale = interior
			? Engine_LightingFrame.InteriorFogScale * interiorContrastFactor
			: Engine_LightingFrame.OutdoorFogScale;

		LastInteriorFog.Set(FableInteriorFogName, effectiveFogScale);
		LastInteriorBake.Set(FableInteriorBakeName, interior ? Engine_LightingFrame.InteriorBakeScale : 1.0f);
		LastSkyAmbient.Set(FableSkyAmbientName, new Vector4(
			frame.AmbientColour[0] * frame.AmbientEnergy * ambientScale,
			frame.AmbientColour[1] * frame.AmbientEnergy * ambientScale,
			frame.AmbientColour[2] * frame.AmbientEnergy * ambientScale,
			tether));

		// OFF, EXPLICITLY, not merely left alone. Five of the saved level scenes carry
		// `fog_enabled = true` inside their Environment sub-resource from when this function set
		// it, with a fog colour of 5/255 — so simply not touching the property any more would
		// leave those regions running Godot's near-black depth fog UNDERNEATH the atmosphere
		// below, which reads as the world going dark with distance rather than as two fogs.
		if (environment.FogEnabled) environment.FogEnabled = false;

		// GODOT 3D VOLUMETRIC FOG (0..128m 32-bit float uniform medium) + FULL-RANGE FABLE ATMOSPHERE (0..2000m).
		// Note: Custom `FogVolume` shaders (`shader_type fog;`) are quantized into integer `R32I` (`* 1024`)
		// and `R11G11B10` (`EMISSION * DENSITY / 4 * 2047`) atomics in Godot 4's `volumetric_fog.glsl`,
		// which truncates physical distance fog densities (`~0.001 m^-1`) to 0.
		// By contrast, `Godot.Environment`'s built-in `VolumetricFogDensity`, `VolumetricFogAlbedo`, and
		// `VolumetricFogEmission` are passed as 32-bit float uniforms (`params.base_density`,
		// `params.base_scattering`, `params.base_emission`) directly into `volumetric_fog_process.glsl`
		// (`RGBA16F` float), completely bypassing integer truncation!
		// Meanwhile, `fable_atmosphere.gdshaderinc` runs in full 32-bit float per pixel across all distances
		// (`0..2000m`), dividing out `exp(-VolumetricFogDensity * min(view_z, 128m))` so near-field surfaces
		// are never double-fogged. The 128 m is ConfigureFogMedium's VolumetricFogEndMetres.
		bool useVolumetricFog = unified && Engine_LightingFrame.VolumetricFogEnabled;
		bool activeVolumetric = useVolumetricFog && effectiveFogScale > 0.0f;
		float volBaseDensity = 0.0f;
		Color volAlbedo = Colors.White;
		Color volLinearTint = Colors.White;
		Color volEmission = Colors.Black;
		float volEmissionEnergy = 0.0f;

		if (activeVolumetric &&
			Refii.Level.Engine_SkyEnvironment.LastAtmosphereState is { HasFog: true } atmosState)
		{
			volBaseDensity = ComputeVolumetricMediumParameters(
				atmosState,
				effectiveFogScale,
				out volAlbedo,
				out volLinearTint,
				out volEmission,
				out volEmissionEnergy);
		}

		bool hasMistVolumes = (Refii.Level.Engine_ModernMist.Instance?.Drawn ?? 0) > 0;
		if (volBaseDensity > 1e-5f)
		{
			float fogStart = Refii.Level.Engine_SkyEnvironment.LastAtmosphereState is { HasFog: true } fs
				? MathF.Max(fs.ScatteringMisc[0], 0.0f)
				: 0.0f;
			float startAtten = 1.0f / (1.0f + fogStart * 0.045f);
			if (!environment.VolumetricFogEnabled) environment.VolumetricFogEnabled = true;
			if (environment.VolumetricFogDensity != volBaseDensity) environment.VolumetricFogDensity = volBaseDensity;
			if (environment.VolumetricFogAlbedo != volAlbedo) environment.VolumetricFogAlbedo = volAlbedo;
			if (environment.VolumetricFogEmission != volEmission) environment.VolumetricFogEmission = volEmission;
			float emissionEnergy = interior ? volEmissionEnergy : (volEmissionEnergy * startAtten * 0.45f);
			if (environment.VolumetricFogEmissionEnergy != emissionEnergy) environment.VolumetricFogEmissionEnergy = emissionEnergy;
			ConfigureFogMedium(environment, interior);
			Engine_VolumetricLightShafts.SetMedium(MathF.Max(volBaseDensity, 0.0022f), volLinearTint, 0.65f, fogStart);
		}
		else if (activeVolumetric)
		{
			if (environment.VolumetricFogEnabled != hasMistVolumes) environment.VolumetricFogEnabled = hasMistVolumes;
			if (environment.VolumetricFogDensity != 0.0f) environment.VolumetricFogDensity = 0.0f;
			if (environment.VolumetricFogEmissionEnergy != 0.0f) environment.VolumetricFogEmissionEnergy = 0.0f;
			ConfigureFogMedium(environment, interior);
			Engine_VolumetricLightShafts.SetMedium(0.0022f, Colors.White, 0.65f, 0.0f);
		}
		else
		{
			if (environment.VolumetricFogEnabled) environment.VolumetricFogEnabled = false;
			if (environment.VolumetricFogDensity != 0.0f) environment.VolumetricFogDensity = 0.0f;
			Engine_VolumetricLightShafts.SetMedium(0.0f, Colors.Black, 0.65f);
		}

		LastVolumetricFogEnd.Set(FableVolumetricFogEndName, Refii.AndroidRuntime.AndroidNearFog.Published(volBaseDensity));
		Refii.AndroidRuntime.AndroidLeanShading.LevelFog(effectiveFogScale, Refii.AndroidRuntime.AndroidNearFog.Published(volBaseDensity));
	}

	/// <summary>
	/// The fixed part of the volumetric medium, common to both lit branches. Each property is
	/// written only when it differs: Godot's setters carry no equality check.
	/// </summary>
	private static void ConfigureFogMedium(Godot.Environment environment, bool interior)
	{
		const float VolumetricFogEndMetres = 128.0f;
		float ambientInject = interior ? 0.15f : 0.20f;
		float giInject = interior ? 0.85f : 0.30f;
		float skyAffect = interior ? 1.0f : 0.45f;
		if (environment.VolumetricFogLength != VolumetricFogEndMetres) environment.VolumetricFogLength = VolumetricFogEndMetres;
		if (environment.VolumetricFogDetailSpread != 2.0f) environment.VolumetricFogDetailSpread = 2.0f;
		if (environment.VolumetricFogAnisotropy != 0.65f) environment.VolumetricFogAnisotropy = 0.65f;
		if (environment.VolumetricFogAmbientInject != ambientInject) environment.VolumetricFogAmbientInject = ambientInject;
		if (environment.VolumetricFogGIInject != giInject) environment.VolumetricFogGIInject = giInject;
		if (environment.VolumetricFogSkyAffect != skyAffect) environment.VolumetricFogSkyAffect = skyAffect;
		if (!environment.VolumetricFogTemporalReprojectionEnabled) environment.VolumetricFogTemporalReprojectionEnabled = true;
		if (environment.VolumetricFogTemporalReprojectionAmount != 0.90f) environment.VolumetricFogTemporalReprojectionAmount = 0.90f;
	}

	/// <summary>
	/// Computes the interior cave fog contrast transfer factor (<c>kappa</c>) for sky-less caves
	/// in Modern mode. High-albedo / low-density caves like Howling Halls (<c>palacecave</c>,
	/// <c>CloseFogColour</c> luma <c>0.704</c> vs <c>AmbientColour</c> luma <c>0.068</c>, ratio <c>10.3x</c>)
	/// relied on near-black unlit cave walls in Classic Fable 2 for their fog contrast, whereas
	/// high-density / low-albedo caves like Guild Cave (<c>thagscave</c>, <c>CloseFogColour</c> luma
	/// <c>0.089</c> vs <c>AmbientColour</c> luma <c>0.050</c>, ratio <c>1.78x</c>) carry their visibility
	/// directly in <c>FarDensity = 4.0</c>.
	/// </summary>
	public static float ComputeInteriorFogContrastFactor(
		Refii.Level.AtmosphereState? state,
		LightingFrame frame)
	{
		if (state is null || !state.HasFog || state.ScatteringMisc[0] >= 500000.0f)
		{
			return 1.0f;
		}

		float closeLuma =
			(0.2126f * state.FogCloseColour[0]) +
			(0.7152f * state.FogCloseColour[1]) +
			(0.0722f * state.FogCloseColour[2]);
		float ambR = frame.AmbientColour[0] * frame.AmbientEnergy;
		float ambG = frame.AmbientColour[1] * frame.AmbientEnergy;
		float ambB = frame.AmbientColour[2] * frame.AmbientEnergy;
		float ambLuma = MathF.Max(
			(0.2126f * ambR) + (0.7152f * ambG) + (0.0722f * ambB),
			0.02f);

		float ratio = closeLuma / (2.5f * ambLuma);
		return Math.Clamp(ratio, 1.0f, 4.5f);
	}

	/// <summary>
	/// Derives Godot's 32-bit float <c>Environment.VolumetricFog*</c> uniform medium parameters
	/// from the solved <see cref="Refii.Level.AtmosphereState"/>.
	/// </summary>
	public static float ComputeVolumetricMediumParameters(
		Refii.Level.AtmosphereState state,
		float effectiveFogScale,
		out Color albedo,
		out Color emission,
		out float emissionEnergy) =>
		ComputeVolumetricMediumParameters(
			state,
			effectiveFogScale,
			out albedo,
			out _,
			out emission,
			out emissionEnergy);

	/// <summary>
	/// Derives Godot's 32-bit float <c>Environment.VolumetricFog*</c> uniform medium parameters
	/// and the unclamped linear scattering tint from the solved <see cref="Refii.Level.AtmosphereState"/>.
	/// </summary>
	public static float ComputeVolumetricMediumParameters(
		Refii.Level.AtmosphereState state,
		float effectiveFogScale,
		out Color albedo,
		out Color linearTint,
		out Color emission,
		out float emissionEnergy)
	{
		albedo = Colors.White;
		linearTint = Colors.White;
		emission = Colors.Black;
		emissionEnergy = 0.0f;

		if (!state.HasFog || effectiveFogScale <= 0.0f || state.ScatteringMisc[0] >= 500000.0f)
		{
			return 0.0f;
		}

		// Evaluate representative optical depth over both the near-camera span (20m) and mid-field
		// span (50m) past FoggingStart so concave/steep near-field fog curves (e.g. Hall of Light
		// `templeoflight`, Howling Halls `palacecave`, elevated Wraithmarsh `wraithmarsh`) retain
		// full close-range 3D volumetric presence while clear daytime towns stay light haze.
		const float nearSpan = 20.0f;
		const float refSpan = 50.0f;
		float tNear = MathF.Min(nearSpan, Refii.Level.Engine_SkyAtmosphere.MaxFogDistance) * state.FogRecipFarRange;
		float t = MathF.Min(refSpan, Refii.Level.Engine_SkyAtmosphere.MaxFogDistance) * state.FogRecipFarRange;
		if (t <= 0.0f || state.FogOpticalDepthAtFar <= 0.0f)
		{
			return 0.0f;
		}

		float opticalDepth20 = state.FogOpticalDepthAtFar * MathF.Pow(tNear, state.FogExponent) * effectiveFogScale;
		float opticalDepth50 = state.FogOpticalDepthAtFar * MathF.Pow(t, state.FogExponent) * effectiveFogScale;
		float betaR = state.BetaRayleigh[0] + state.BetaMie[0];
		float betaG = state.BetaRayleigh[1] + state.BetaMie[1];
		float betaB = state.BetaRayleigh[2] + state.BetaMie[2];
		float betaLuma = (0.2126f * betaR) + (0.7152f * betaG) + (0.0722f * betaB);

		// Attenuate uniform 0..128m froxel density when the level authors a positive FoggingStart
		// (e.g. Bowerstone Market FoggingStart ~ 20m) so the clear near-camera zone is respected,
		// while FoggingStart = 0..5m levels (Howling Halls, Wraithmarsh, Hall of Light) keep full close fog.
		float fogStart = MathF.Max(state.ScatteringMisc[0], 0.0f);
		float startFactor = 1.0f / (1.0f + fogStart * 0.045f);
		float opticalRate = MathF.Max(opticalDepth20 / nearSpan, opticalDepth50 / refSpan);
		float sigma = Math.Clamp((betaLuma * opticalRate) * 0.52f * startFactor, 0.0f, 0.028f);
		if (sigma <= 1e-6f)
		{
			return 0.0f;
		}

		float closeR = MathF.Max(state.FogCloseColour[0], 0.0f);
		float closeG = MathF.Max(state.FogCloseColour[1], 0.0f);
		float closeB = MathF.Max(state.FogCloseColour[2], 0.0f);

		// Blend CloseFogColour with the solved sky in-scatter LUT at representative near-field
		// distance (16m * 1/CloseFogMaxDistance) whenever the sky LUT is brighter than CloseFogColour,
		// so levels like Hall of Light (`templeoflight`) and Wraithmarsh retain their full near-field
		// in-scatter luminance in scene-referred HDR space without dimming caves like Howling Halls
		// where CloseFogColour carries the near-field fog brightness.
		Refii.Level.Engine_SkyAtmosphere.SampleInScatter(state, 0.50f, out float skyR, out float skyG, out float skyB);
		skyR = MathF.Max(skyR, 0.0f);
		skyG = MathF.Max(skyG, 0.0f);
		skyB = MathF.Max(skyB, 0.0f);
		float skyBlend = Math.Clamp(16.0f * state.ScatteringMisc[2], 0.0f, 1.0f);

		float r = MathF.Max(closeR, Mathf.Lerp(closeR, skyR, skyBlend));
		float g = MathF.Max(closeG, Mathf.Lerp(closeG, skyG, skyBlend));
		float b = MathF.Max(closeB, Mathf.Lerp(closeB, skyB, skyBlend));
		float peak = MathF.Max(r, MathF.Max(g, b));

		if (peak > 1e-4f)
		{
			linearTint = new Color(r / peak, g / peak, b / peak);
			// Blend 65% of the authored fog tint into the scattering albedo so dynamic
			// lights take on the atmosphere's hue while still allowing warm torches to tint cool fog.
			Color normTint = new(
				Mathf.Lerp(1.0f, linearTint.R, 0.65f),
				Mathf.Lerp(1.0f, linearTint.G, 0.65f),
				Mathf.Lerp(1.0f, linearTint.B, 0.65f));
			albedo = Engine_LightColour.ForGodot(normTint);
			emission = Engine_LightColour.ForGodot(linearTint);
			emissionEnergy = peak * Refii.Level.Engine_SkyAtmosphere.FogColourFactor;
		}

		return sigma;
	}

	/// <summary>
	/// Aims the sun so it agrees with the sun DISC in the sky.
	///
	/// <see cref="Engine_SkyFrame"/> builds SunDirection as (groundX, up, groundZ) — component
	/// [1] is the vertical — so it is already Y-up and needs only the X flip that carries it
	/// into the level root's mirrored space. Engine_CelestialElements does exactly that,
	/// <c>(-d0, d1, d2)</c>, to place the visible disc.
	///
	/// A DirectionalLight3D travels along its own -Z, and light travels FROM the sun, so the
	/// forward vector is the NEGATION of the direction to the disc: <c>(d0, -d1, -d2)</c>.
	///
	/// NOT all three: negating X as well puts the sun on one side of the sky and the light on
	/// the other, so shadows fall the wrong way horizontally — subtle enough to read as "the
	/// lighting needs work" rather than as a sign error, especially under a heavily overcast
	/// sky where the disc is hard to locate.
	/// </summary>
	private static Basis SunBasis(float[] direction)
	{
		// LOCAL, UNDER A MIRRORED ROOT. The light hangs off the region root, whose
		// Scale = (-1, 1, 1) mirrors X again on the way to world space. The world travel we want
		// is -ToGodot(d) = (d0, -d1, -d2); the root maps local (a, b, c) to world (-a, b, c);
		// so the local basis has to be (-d0, -d1, -d2). Taking (d0, -d1, -d2) here — the world
		// answer handed to a mirrored parent — put the light on the wrong side of the sky.
		var forward = -Refii.Level.Engine_SkyFrame.ToRegionLocal(direction);
		if (forward.LengthSquared() < 1e-8f)
		{
			forward = Vector3.Down;
		}

		forward = forward.Normalized();
		Vector3 up = MathF.Abs(forward.Y) > 0.99f ? Vector3.Forward : Vector3.Up;
		return Basis.LookingAt(forward, up);
	}

	// Written here and nowhere else, and written every frame: each sends only a changed value.
	private static readonly Engine_LastWritten<float> LastUnified = new();
	private static readonly Engine_LastWritten<Vector3> LastFillFloor = new();
	private static readonly Engine_LastWritten<float> LastInterior = new();
	private static readonly Engine_LastWritten<Vector3> LastAmbientRef = new();
	private static readonly Engine_LastWritten<float> LastInteriorFog = new();
	private static readonly Engine_LastWritten<float> LastInteriorBake = new();
	private static readonly Engine_LastWritten<Vector4> LastSkyAmbient = new();
	private static readonly Engine_LastWritten<float> LastVolumetricFogEnd = new();

	// Lazily initialized so touching Engine_LevelLighting in headless .NET testhost never invokes
	// native Godot StringName constructors.
	private static StringName? _fableUnifiedName;
	private static StringName? _fableFillFloorName;
	private static StringName? _fableInteriorName;
	private static StringName? _fableInteriorFogName;
	private static StringName? _fableInteriorBakeName;
	private static StringName? _fableSkyAmbientName;

	private static StringName FableUnifiedName => _fableUnifiedName ??= "fable_unified";
	private static StringName? _fableAmbientRefName;
	private static StringName FableAmbientRefName => _fableAmbientRefName ??= "fable_ambient_ref";
	private static StringName FableFillFloorName => _fableFillFloorName ??= "fable_fill_floor";
	private static StringName FableInteriorName => _fableInteriorName ??= "fable_interior";
	private static StringName FableInteriorFogName => _fableInteriorFogName ??= "fable_interior_fog";
	private static StringName FableInteriorBakeName => _fableInteriorBakeName ??= "fable_interior_bake";
	private static StringName FableSkyAmbientName => _fableSkyAmbientName ??= "fable_sky_ambient";
	private static StringName? _fableVolumetricFogEndName;
	private static StringName FableVolumetricFogEndName => _fableVolumetricFogEndName ??= "fable_volumetric_fog_end";
}
