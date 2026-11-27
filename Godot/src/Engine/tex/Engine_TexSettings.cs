using System;
using System.Collections.Generic;
using Godot;
using Refii.Core;
using Refii.Launcher.Core;

namespace Refii.Textures;

/// <summary>
/// The texture pipeline's settings: §23's <c>[texture_upscaling]</c> block, and the switch.
/// </summary>
/// <remarks>
/// IN <c>refii.toml</c>, because that is the one settings file. Getting there took two fixes,
/// both of which had to land before this was safe:
///
/// <c>RuntimeConfig.WriteValues</c> rebuilt the file from the definition list and wrote NOTHING
/// else, so any key without a definition was deleted the next time a player saved anything in the
/// settings panel. It now preserves unrecognised keys. And <c>RuntimeCVars</c> is GENERATED from
/// the native runtime's own cvar registry, so it cannot describe anything Godot owns — hence
/// <see cref="RuntimeCVarsGodot"/>, the hand-maintained half, merged through
/// <see cref="RuntimeCVarSet"/>. These keys are therefore real settings with a type, a default and
/// a description, and they appear in the launcher's panel like any other.
///
/// The environment still overrides the file for one session, and is never persisted back: it is
/// how a developer A/Bs a run without editing the player's settings.
/// </remarks>
public static class Engine_TexSettings
{
	private static readonly object Gate = new();

	private static bool _loaded;

	private static bool _upscaleEnabled;

	private static bool _cacheEnabled = true;

	private static int _workers = 1;

	private static long _budgetMegabytes = 4096;

	private static bool _ormEnabled;

	private static float _ormAoStrength = 0.5f;

	private static float _ormMetallicStrength = 1.0f;

	private static bool _ormHeight = true;
	private static string _upscaleWhen = "boot";

	private static bool _modernized = true;

	/// <summary>The master key. Named here so the report and the writer cannot disagree on it.</summary>
	public const string ModernizedKey = "materials_modernized";

	private static float _pubGameRasterScale = 1.0f;

	private static bool _pubGameVectorShapes = true;

	private static bool _pubGameReleaseOnClose;

	private static string _model = "resample";

	private static string _weights = "realesr-general-x4v3";

	private static string _source = "defaults";

	/// <summary>What "decide for me" would have chosen, kept so the report can say so.</summary>
	private static int _workersAuto;

	private static long _budgetAuto;

	private static bool _workersFromFile;

	private static bool _budgetFromFile;

	/// <summary>
	/// The remaster switch: the modernized materials, or the game exactly as it shipped.
	/// </summary>
	/// <remarks>
	/// THIS IS THE PRODUCT, not an experiment behind a flag. Refii is an Oblivion-Remastered-style
	/// remake — the modernized look is the one being built, and the switch exists so a player can
	/// ask for the original instead, the same way that remaster does. That is why it defaults ON
	/// and why the individual keys default FROM it: an earlier version of this file argued each
	/// derived channel should be off because it "departs from how the original looked", which is
	/// an argument against the point of the project rather than for caution.
	///
	/// OFF MEANS THE SHIPPED ASSETS, EXACTLY. No upscaling, no ORM, so no derived occlusion, no
	/// derived metallic, no derived height and no parallax — the material path falls back to the
	/// specular map and the shader's own roughness, which is what the guest does.
	///
	/// TWO OF THE THREE TAKE EFFECT AT DIFFERENT TIMES, and it is worth being honest about which.
	/// Upscaling is live: <see cref="Engine_TexEnhance.SetEnabled"/> swaps every texture in place
	/// and reverts them on the way back. ORM is bound into materials when they are built, so
	/// changing it applies to the next level load rather than to the frame after the click.
	/// </remarks>
	public static bool Modernized
	{
		get
		{
			Load();
			lock (Gate)
			{
				return _modernized;
			}
		}
	}

	/// <summary>
	/// Whether the player has asked for upscaling at all. `texture_upscaling_when` = "off" is
	/// the same answer through the other switch — one policy governs every texture.
	/// </summary>
	public static bool UpscaleEnabled
	{
		get
		{
			Load();
			lock (Gate)
			{
				return _upscaleEnabled && _upscaleWhen != "off";
			}
		}
	}

	/// <summary>
	/// WHEN textures are enhanced — ALL of them: props, UI and terrain alike. "boot" prebuilds
	/// the whole game behind the warm-up screen; "play" pays as textures are met (async swap
	/// for props and UI, a longer first level load for terrain); "off" disables enhancement
	/// everywhere. Anything unrecognised reads as "boot".
	/// </summary>
	public static string UpscaleWhen
	{
		get
		{
			Load();
			lock (Gate)
			{
				return _upscaleWhen is "off" or "play" ? _upscaleWhen : "boot";
			}
		}
	}

	/// <summary>Whether processed results are read from and written to disk.</summary>
	public static bool CacheEnabled
	{
		get
		{
			Load();
			lock (Gate)
			{
				return _cacheEnabled;
			}
		}
	}

	/// <summary>
	/// How many textures may be enhanced at once.
	/// </summary>
	/// <remarks>
	/// Sized against the machines that actually run this. §23 of the plan says "a GPU such as a
	/// GTX 650"; the July 2026 Steam survey says otherwise — the commonest card is an RTX 3060 at
	/// 3.71%, VRAM is 16 GB (25.9%) then 8 GB (25.3%), system RAM is 16 GB (41.0%) then 32 GB
	/// (36.9%), and CPUs are 8 cores (27.9%) then 6 (27.5%). A 2 GB 2012 card is not the baseline.
	///
	/// It is also NOT bounded by memory, which was the original assumption: the soak measured the
	/// managed working set returning to its starting value once the run settled, so a worker costs
	/// allocation churn and CPU rather than resident bytes.
	/// </remarks>
	public static int Workers
	{
		get
		{
			Load();
			lock (Gate)
			{
				return _workers;
			}
		}
	}

	/// <summary>How much disk the cache may hold.</summary>
	public static long BudgetMegabytes
	{
		get
		{
			Load();
			lock (Gate)
			{
				return _budgetMegabytes;
			}
		}
	}

	/// <summary>
	/// Whether materials get a packed ORM texture instead of deriving roughness per pixel.
	/// </summary>
	/// <remarks>
	/// ON with <see cref="Modernized"/>, because ORM is what the modernization IS. It was off while
	/// its channels were constants — Fable 2 ships no AO and no metallic map — but §25 phase 5 is
	/// complete and all four now carry derived data: occlusion from the normal field's divergence,
	/// metallic from the specular map read as the F0 map it already is, height from a Poisson solve
	/// over the normal gradient, and roughness from the guest's own exponent equation. See
	/// <see cref="Engine_TexOrm"/>.
	///
	/// Set it explicitly to take the modernized textures without the modernized materials, or the
	/// other way round.
	/// </remarks>
	public static bool OrmEnabled
	{
		get
		{
			Load();
			lock (Gate)
			{
				return _ormEnabled;
			}
		}
	}

	/// <summary>
	/// How strongly derived cavity occlusion is applied in the ORM's R channel. 0 disables it.
	/// </summary>
	/// <remarks>
	/// The guest applies no ambient occlusion at all, so this is one of the places the modernized
	/// look departs from the original — deliberately, which is what <see cref="Modernized"/> is
	/// for. 0.5 is a restrained default: enough to read as contact shading without carving the
	/// normal map's every scratch into the lighting. 0 gives the packing with a constant R, which
	/// is what the shipped data alone supports.
	/// </remarks>
	public static float OrmAoStrength
	{
		get
		{
			Load();
			lock (Gate)
			{
				return _ormAoStrength;
			}
		}
	}

	/// <summary>
	/// How strongly a metallic channel is derived from the specular map's colour. 0 disables it.
	/// </summary>
	/// <remarks>
	/// 1.0 rather than a cautious fraction, because the derivation already declines to commit on
	/// its own: it needs a texel to be both COLOURED and BRIGHT, and
	/// <c>src/tools/SpecularCensus.cs</c> measured the median specular texel at chroma 0.031 and
	/// luminance 0.144 — grey and dark, so most of the game is untouched by it whatever this says.
	/// Scaling the output down as well would only wash out the fifth of textures that genuinely do
	/// describe metal.
	///
	/// Only consulted when <see cref="OrmEnabled"/> is on, which follows the modernized
	/// default — ON.
	/// </remarks>
	public static float OrmMetallicStrength
	{
		get
		{
			Load();
			lock (Gate)
			{
				return _ormMetallicStrength;
			}
		}
	}

	/// <summary>
	/// Whether a height field is solved from the normal map into the ORM's alpha channel.
	/// </summary>
	/// <remarks>
	/// ON when ORM is on, because unlike the other two channels this one is nearly always present:
	/// <c>src/tools/NormalCensus.cs</c> found 89.8% of material records name a normal map and 346
	/// of 400 of those carry at least a quarter non-flat texels. A derived AO or metallic is a
	/// judgement about what the data means; a height field is the integral of a gradient the artist
	/// drew.
	///
	/// A SETTING BECAUSE IT COSTS THE BLOCK FORMAT. A constant alpha compresses as DXT1 and a
	/// varying one forces DXT5, so switching this off is how a player buys back half the ORM's
	/// bytes on a machine that is not going to use parallax anyway.
	/// </remarks>
	public static bool OrmHeight
	{
		get
		{
			Load();
			lock (Gate)
			{
				return _ormHeight;
			}
		}
	}


	/// <summary>
	/// Whether the pub games draw their Flash artwork as geometry rather than rasterizing it.
	/// </summary>
	/// <remarks>
	/// ON, because it is better on every axis that was measured — keystone's 234 placed shapes cost
	/// 28.8 MB and 1.65 s as triangles against 109 MB and 4.6 s as bitmaps, and the triangles are
	/// sharp at any window size where the bitmaps are authored at one size and stretched to the
	/// rest. It is also the reason the pub games were rebuilt in C#: an interpreter has to
	/// rasterize, and we do not.
	///
	/// A SETTING BECAUSE THE BITMAP PATH IS THE ONE THE ARTWORK WAS VERIFIED AGAINST. The
	/// triangulation reproduces its even-odd fill rule exactly — checked at 100.000% over 3.3
	/// million sample points across all three games — but the gradient and bitmap fills go through
	/// Godot's UV mapping rather than through the rasterizer's sampler, and a difference there is
	/// worth being able to switch between and look at rather than argue about.
	///
	/// <see cref="PubGameRasterScale"/> applies only when this is off.
	/// </remarks>
	/// <summary>
	/// Whether a pub-game board disposes its Flash timelines when it is freed instead of leaving
	/// them to the finalizer. On by default on Android, where a Keystone board's timelines
	/// outlived it; off on the desktop. See <see cref="Refii.Ui.Flash.SwfSceneUi.Release"/>.
	/// </summary>
	public static bool PubGameReleaseOnClose
	{
		get
		{
			Load();
			lock (Gate)
			{
				return _pubGameReleaseOnClose;
			}
		}
	}

	public static bool PubGameVectorShapes
	{
		get
		{
			Load();
			lock (Gate)
			{
				return _pubGameVectorShapes;
			}
		}
	}

	/// <summary>
	/// How far above their authored size the pub games rasterize their vector art.
	/// </summary>
	/// <remarks>
	/// A SETTING BECAUSE THE COST IS REAL AND MEASURED, and it is a different kind of cost from
	/// everything else in this file. The pub games are Flash: their board is not a texture, it is
	/// vector shapes rasterized on the CPU into canvases at load, and the stage is then magnified
	/// to fill the window — about 2.3x at 1080p from a 640x462 stage. At a scale of 1 that
	/// magnification is Godot stretching authored-size bitmaps, which is why the boards look soft
	/// and why an upscaled source bitmap has nowhere to put its extra detail. The shipped art makes
	/// the same point: the backgrounds are 1800x1300 against a 640-wide stage.
	///
	/// Measured on keystone, 234 distinct canvases:
	///
	///     1.0x  109 MB   4.6 s
	///     2.5x  348 MB  11.9 s
	///
	/// Quadratic in both, so this is not a knob to turn up casually — seven extra seconds in front
	/// of a pub minigame is a worse experience than a soft board. It defaults to 1, which is what
	/// the games did before there was a setting at all.
	///
	///     pubgame_raster_scale = 0     follow the window, capped by the host
	///     pubgame_raster_scale = 1     authored size (the default)
	///     pubgame_raster_scale = 2.5   fixed oversample
	/// </remarks>
	public static float PubGameRasterScale
	{
		get
		{
			Load();
			lock (Gate)
			{
				return _pubGameRasterScale;
			}
		}
	}

	/// <summary>Which backend to register. "resample" is the only one built so far.</summary>
	public static string Model
	{
		get
		{
			Load();
			lock (Gate)
			{
				return _model;
			}
		}
	}

	/// <summary>
	/// Which weight files the neural backend loads, by base name.
	/// </summary>
	/// <remarks>
	/// A setting rather than a constant because the weights are not in the tree — they are dropped
	/// beside the build — so which ones are present is the player's business, not ours. The name
	/// is also where the model's SCALE is read from, so it has to be the real file name.
	/// </remarks>
	public static string Weights
	{
		get
		{
			Load();
			lock (Gate)
			{
				return _weights;
			}
		}
	}

	/// <summary>Where the values came from, for the log line that explains them.</summary>
	public static string Source
	{
		get
		{
			Load();
			lock (Gate)
			{
				return _source;
			}
		}
	}

	/// <summary>
	/// Remembers the player's answer to the upscaling switch.
	/// </summary>
	/// <remarks>
	/// ONLY THIS KEY'S LINE CHANGES (<c>RuntimeConfig.EditValues</c>): every other line of the
	/// player's file — another title's keys and unknown keys included — stays as it stands.
	/// </remarks>
	public static void PersistUpscaleEnabled(bool enabled)
	{
		lock (Gate)
		{
			_upscaleEnabled = enabled;
		}

		string path = Engine_RefiiConfig.ResolvePath();
		if (path.Length == 0)
		{
			GD.PushWarning("[tex] no " + Refii.Core.Engine_Title.Active.ConfigFileName + " to write the upscaling setting to");
			return;
		}

		// ONE KEY'S LINE CHANGES (RuntimeConfig.EditValues): a whole-file write from the title's
		// table would drop every key of another title's and reorder the player's file.
		var set = new Dictionary<string, string> { ["texture_upscaling_enabled"] = enabled ? "true" : "false" };
		if (!RuntimeConfig.EditValues(path, set, System.Array.Empty<string>(), out string error))
		{
			GD.PushWarning($"[tex] could not save {path}: {error}");
		}
	}

	/// <summary>Forces a re-read.</summary>
	public static void Invalidate()
	{
		lock (Gate)
		{
			_loaded = false;
		}
	}

	private static void Load()
	{
		lock (Gate)
		{
			if (_loaded)
			{
				return;
			}

			_loaded = true;

			IReadOnlyDictionary<string, string> values = Engine_RefiiConfig.Read(out string path);
			_source = path.Length == 0 ? "defaults (no " + Refii.Core.Engine_Title.Active.ConfigFileName + " found)" : path;

			// THE ONE SWITCH: the original game, or the modernized one. Everything below defaults
			// FROM it rather than to a constant, so a player picks a look rather than assembling
			// one out of five keys — and an explicit key still wins, so anyone who wants the
			// modernized materials without the upscaler can say exactly that.
			_modernized = Engine_RefiiConfig.Bool(values, ModernizedKey, true);

			_upscaleEnabled = Engine_RefiiConfig.Bool(
				values, "texture_upscaling_enabled", _modernized);
			_cacheEnabled = Engine_RefiiConfig.Bool(values, "texture_cache_enabled", true);
			_model = Engine_RefiiConfig.String(values, "texture_upscaling_model", "resample");
			_ormEnabled = Engine_RefiiConfig.Bool(values, "texture_orm_enabled", _modernized);
			// NEVER UNDER THE CLASSIC LIGHTING MODEL, whatever the key says: the title's own
			// shaders read the shipped specular map as it is, and the ground's cavity and
			// parallax are modern-material terms. ORM is the modernization (user, 2026-10-12).
			if (Refii.Lighting.Engine_LightingFrame.IsClassic(
					Engine_RefiiConfig.String(values, "lighting_model", "modern")))
			{
				_ormEnabled = false;
			}
			_weights = Engine_RefiiConfig.String(
				values, "texture_upscaling_weights", "realesr-general-x4v3");
			_ormAoStrength =
				Engine_RefiiConfig.Int(values, "texture_orm_ao_strength_percent", 50) / 100.0f;

			// AS A PERCENT, following texture_orm_ao_strength_percent, because the config reader
			// parses integers and a second numeric convention in one file is a trap. 100 is
			// authored size; 0 means "follow the window".
			_ormMetallicStrength =
				Engine_RefiiConfig.Int(values, "texture_orm_metallic_strength_percent", 100) / 100.0f;
			_ormHeight = Engine_RefiiConfig.Bool(values, "texture_orm_height_enabled", true);
			_upscaleWhen = Engine_RefiiConfig.String(
				values, "texture_upscaling_when", "boot").Trim().Trim('"').ToLowerInvariant();
			_pubGameRasterScale =
				Engine_RefiiConfig.Int(values, "pubgame_raster_scale_percent", 100) / 100.0f;
			_pubGameVectorShapes =
				Engine_RefiiConfig.Bool(values, "pubgame_vector_shapes", true);
			_pubGameReleaseOnClose = Engine_RefiiConfig.Bool(
				values, "pubgame_release_on_close", !Engine_AppRoot.IsTestHost && Engine_Platform.IsAndroid);
			// 0 means "decide for me", and the decision depends on whether upscaling is on, so it
			// is resolved AFTER the environment overrides below rather than here.
			_budgetMegabytes = Engine_RefiiConfig.Int(values, "texture_cache_max_mb", 0);

			_workers = Engine_RefiiConfig.Int(values, "texture_upscaling_workers", 0);

			// The environment overrides the file ONE KEY AT A TIME. Overriding the worker count
			// must not also silently reset the enable flag to its default.
			// THE MASTER OVERRIDE IS APPLIED FIRST, and it re-derives the keys that followed it, so
			// flipping the whole look from the environment behaves the same as flipping it in the
			// file. A key the FILE stated explicitly still wins — the environment is overriding the
			// master, not overruling a deliberate choice.
			if (EnvFlag("REFII_MATERIALS_MODERNIZED") is bool modernized)
			{
				_modernized = modernized;
				_source += " +env";

				if (!values.ContainsKey("texture_upscaling_enabled"))
				{
					_upscaleEnabled = modernized;
				}

				if (!values.ContainsKey("texture_orm_enabled"))
				{
					_ormEnabled = modernized;
				}
			}

			if (EnvFlag("REFII_TEX_UPSCALE") is bool upscale)
			{
				_upscaleEnabled = upscale;
				_source += " +env";
			}

			if (System.Environment.GetEnvironmentVariable("REFII_TEX_MODEL") is
				{ Length: > 0 } model)
			{
				_model = model.Trim();
			}

			if (System.Environment.GetEnvironmentVariable("REFII_TEX_WEIGHTS") is
				{ Length: > 0 } weights)
			{
				_weights = weights.Trim();
			}

			if (EnvFlag("REFII_TEX_ORM") is bool orm)
			{
				_ormEnabled = orm;
			}

			if (EnvFlag("REFII_TEX_CACHE") is bool cache)
			{
				_cacheEnabled = cache;
			}

			if (EnvInt("REFII_TEX_WORKERS") is int workers)
			{
				_workers = workers;
			}

			if (EnvInt("REFII_TEX_CACHE_MB") is int budget)
			{
				_budgetMegabytes = budget;
			}

			if (EnvInt("REFII_PUBGAME_RASTER_PERCENT") is int raster)
			{
				_ormMetallicStrength =
				Engine_RefiiConfig.Int(values, "texture_orm_metallic_strength_percent", 100) / 100.0f;
			_pubGameRasterScale = raster / 100.0f;
			}

			if (EnvFlag("REFII_PUBGAME_VECTOR") is bool vector)
			{
				_pubGameVectorShapes = vector;
			}

			// RESOLVED LAST, because both defaults depend on settings the environment may have
			// just changed. Deriving the budget from the file's upscale flag and then letting
			// REFII_TEX_UPSCALE flip that flag would size the cache for the wrong mode.
			_workersFromFile = _workers > 0;
			_budgetFromFile = _budgetMegabytes > 0;

			// COMPUTED EVEN WHEN NOT USED, so the report can show what the file is overriding.
			_workersAuto = Math.Clamp(System.Environment.ProcessorCount / 2, 1, 4);
			_budgetAuto = _upscaleEnabled ? 12288 : 4096;

			if (_workers <= 0)
			{
				// HALF THE CORES, not a quarter. The quarter was picked against the plan's "should
				// stay usable on a GTX 650" and that is not who runs this: the July 2026 Steam
				// survey puts 8 cores at 27.9% and 6 at 27.5% (4 cores is 13.1%), with 16 GB of
				// system RAM at 41.0%. A quarter gave the two commonest machines one or two
				// workers.
				//
				// Memory is not what bounds this anyway — the soak measured the managed working
				// set returning to its baseline afterwards, so the cost of a worker is allocation
				// churn and CPU, not resident bytes.
				_workers = System.Environment.ProcessorCount / 2;
			}

			if (_budgetMegabytes <= 0)
			{
				// 9.2 GB is what the whole shipped set costs enhanced, computed per texture from
				// its own size and format (TextureCensus), on top of 2.0 GB of decodes. A 4 GB
				// budget with upscaling ON would therefore evict most of the work continuously and
				// look like a broken cache rather than a full one.
				_budgetMegabytes = _upscaleEnabled ? 12288 : 4096;
			}

			// CAPPED AT FOUR, and the cap is measured rather than cautious. On a 16-core machine
			// the soak ran 150 textures at 14.0/s with eight workers and 13.9/s with four — no
			// gain at all — while Godot's resident memory went from 447 MB to 801 MB. The path
			// does not scale past about four, so the extra workers buy nothing and cost most of a
			// gigabyte. The two commonest core counts in the survey (8 and 6) land on four and
			// three either way, so the cap only affects machines where it is free.
			_workers = Math.Clamp(_workers, 1, 4);
			_budgetMegabytes = Math.Max(64, _budgetMegabytes);
			_ormAoStrength = Math.Clamp(_ormAoStrength, 0.0f, 4.0f);
		}
	}

	/// <summary>Null when unset, so an override can be told apart from a default.</summary>
	private static bool? EnvFlag(string name)
	{
		string value = (System.Environment.GetEnvironmentVariable(name) ?? string.Empty)
			.Trim()
			.ToLowerInvariant();

		if (value.Length == 0)
		{
			return null;
		}

		return value is not ("0" or "off" or "false" or "no");
	}

	private static int? EnvInt(string name)
	{
		string value = System.Environment.GetEnvironmentVariable(name) ?? string.Empty;
		return int.TryParse(value.Trim(), out int parsed) ? parsed : null;
	}

	/// <summary>
	/// One line naming the file, what it said, and what it is overriding.
	/// </summary>
	/// <remarks>
	/// THE OVERRIDE NOTE IS THE POINT. A settings file only ever gains keys — EnsureDefaults fills
	/// what is MISSING and never rewrites what is there, which is the only safe rule for something
	/// a player edits. The consequence is that improving a default does nothing for anyone whose
	/// file already exists, silently: a real session ran the enhancement single-threaded because
	/// its refii.toml still carried <c>texture_upscaling_workers = 1</c> from when that was the
	/// definition's default, and nothing anywhere said that "decide for me" would now pick four.
	///
	/// So when the file states a value the current build would choose differently, this says both.
	/// It does not change the value — the player's file still wins — it just stops the
	/// disagreement being invisible.
	/// </remarks>
	public static void Report() => GD.Print(Describe());

	/// <summary>
	/// The reported line, as a string, so a test can read it.
	/// </summary>
	/// <remarks>
	/// SPLIT OUT BECAUSE THE PRINTING VERSION WAS UNTESTABLE, and it silently regressed twice: two
	/// edits to it did not apply, the build succeeded either way, and no check looked at what it
	/// said — so sessions ran for a day reporting neither the ORM flag nor the override note while
	/// appearing to work. A log line nothing asserts on is a log line that can quietly say the
	/// wrong thing, or nothing at all.
	/// </remarks>
	public static string Describe()
	{
		Load();

		string workers = _workersFromFile && Workers != _workersAuto
			? $"{Workers} (set; 0 would pick {_workersAuto})"
			: $"{Workers}";

		string budget = _budgetFromFile && BudgetMegabytes != _budgetAuto
			? $"{BudgetMegabytes}MB (set; 0 would pick {_budgetAuto}MB)"
			: $"{BudgetMegabytes}MB";

		// The raster scale only means anything on the bitmap path, so say which is in use rather
		// than printing a number that does not apply.
		string raster = PubGameVectorShapes
			? "vector"
			: PubGameRasterScale <= 0.0f
				? "window"
				: $"{PubGameRasterScale:F2}x";

		return $"[tex] settings: modernized={Modernized} upscale={UpscaleEnabled} cache={CacheEnabled} " +
			$"workers={workers} budget={budget} model={Model} weights={Weights} " +
			$"orm={OrmEnabled} ao={OrmAoStrength:F2} metal={OrmMetallicStrength:F2} " +
			$"height={OrmHeight} " +
			$"pubraster={raster} ({Source})";
	}
}
