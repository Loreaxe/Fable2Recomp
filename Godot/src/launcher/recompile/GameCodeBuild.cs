using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Refii.Launcher.Core;
using Refii.Mods;
using Refii.Recompile;

namespace Refii.Launcher.Recompile;

/// <summary>
/// Turns the player's own game executable into the native module the runtime loads.
/// </summary>
/// <remarks>
/// AN INSTALL STEP, and the one that cannot be skipped: the recompiled guest is derived
/// from the player's game image, so it is not redistributable and no release can contain
/// it. What we ship is the generator (refii_recompiler, a library in bin/), the runtime,
/// our own libraries and a bundled Zig under <c>recompile/</c>; this drives them.
///
/// Five phases, in order:
///   1. generate  — the generator reads assets/&lt;Title&gt;/game/default.xex and writes C++ to
///                  cache/&lt;Title&gt;/compile/generated, skipped on warm builds via <see cref="CodegenCache"/>
///   2. precompile — the generated PCH header, cached across builds via <see cref="CompileCache"/>
///   3. compile   — ~150k functions, <see cref="JobCount"/> translation units at a time,
///                  LPT largest-first and content-addressed via <see cref="CompileCache"/>
///   4. link      — one shared module, next to the title's instance native libraries
///   5. prune     — generational cache retention (last 3 builds, 512 MB cap) and scratch cleanup
/// </remarks>
public static class GameCodeBuild
{
	/// <summary>Maximum number of build generations retained in a title's object cache.</summary>
	public const int ObjectCacheMaxGenerations = 3;

	/// <summary>Byte budget applied to older-generation artifacts in a title's object cache (512 MiB).</summary>
	public const long ObjectCacheMaxBytes = 512L * 1024 * 1024;

	private const string ModuleInputsPrefix = "module-inputs:";

	/// <summary>
	/// Internal execution seam used by automated feature tests to run the full game-code build pipeline
	/// against synthetic generator/compiler callbacks and isolated temp directories without touching workspace outputs.
	/// </summary>
	internal sealed class Driver
	{
		/// <summary>Optional root directory overriding <c>cache/&lt;Title&gt;/compile</c> and launcher log location.</summary>
		public string? CacheRootOverride { get; init; }

		/// <summary>Optional callback overriding <see cref="GetGameExecutablePath(Refii.Core.Engine_Title)"/>.</summary>
		public Func<Refii.Core.Engine_Title, string>? GameExecutablePathOverride { get; init; }

		/// <summary>Optional callback overriding the native hard-mods workspace directory for a title.</summary>
		public Func<Refii.Core.Engine_Title, string>? ModsRootOverride { get; init; }

		/// <summary>When true, skips the editor-only Windows CMake bootstrap step.</summary>
		public bool SkipWindowsBootstrap { get; init; } = true;

		/// <summary>Optional callback overriding bundled Zig toolchain resolution.</summary>
		public Func<(bool Ok, Toolchain Toolchain, string Error)>? ResolveToolchain { get; init; }

		/// <summary>Optional callback overriding <c>zig version</c> queries.</summary>
		public Func<Toolchain, string>? ToolchainVersion { get; init; }

		/// <summary>Optional callback overriding <c>Recompiler.SdkVersion()</c>.</summary>
		public Func<string>? SdkVersion { get; init; }

		/// <summary>Optional callback overriding in-process <c>Recompiler.Generate</c>.</summary>
		public Func<string, BuildControl, Action<string>, Action<string>, Result>? GenerateCode { get; init; }

		/// <summary>Optional callback overriding child process execution (<c>zig c++</c> compile and link invocations).</summary>
		public Func<string, IReadOnlyList<string>, string, IReadOnlyDictionary<string, string>, BuildControl, CancellationToken, Task<(int Code, string Output)>>? RunProcessAsync { get; init; }

		/// <summary>Optional override for available physical memory in bytes.</summary>
		public Func<long>? AvailableMemoryBytesOverride { get; init; }

		/// <summary>Optional override for logical processor count.</summary>
		public int? ProcessorCountOverride { get; init; }
	}

	private static readonly AsyncLocal<Driver?> CurrentDriver = new();

	/// <summary>Gets the active test driver for the current asynchronous execution flow, if any.</summary>
	internal static Driver? ActiveDriver => CurrentDriver.Value;

	/// <summary>Installs <paramref name="driver"/> for the duration of the returned scope.</summary>
	/// <param name="driver">Driver configuration to activate.</param>
	/// <returns>Disposable scope that restores the previous driver on disposal.</returns>
	internal static IDisposable PushDriver(Driver driver)
	{
		Driver? previous = CurrentDriver.Value;
		CurrentDriver.Value = driver;
		return new DriverScope(previous);
	}

	private sealed class DriverScope : IDisposable
	{
		private readonly Driver? _previous;
		private bool _disposed;

		public DriverScope(Driver? previous) => _previous = previous;

		public void Dispose()
		{
			if (!_disposed)
			{
				_disposed = true;
				CurrentDriver.Value = _previous;
			}
		}
	}

	/// <summary>
	/// Where the native libraries live.
	/// </summary>
	/// <remarks>
	/// The build has TWO roots and they are not interchangeable. This one is where the
	/// shipped native closure sits, and the only thing that belongs here is the module
	/// itself plus what compiles it — <c>refii_payload</c> resolves the payload beside the
	/// library that loaded it, so a module written anywhere else is a module the runtime
	/// cannot find.
	///
	/// Everything derived from the player's game — the executable, the generated sources —
	/// lives with the INSTALL instead, and is resolved through <see cref="Installer"/>.
	/// </remarks>
	public static string BinDirectory { get; set; } = string.Empty;

	public static string RecompileDirectory => Path.Combine(BinDirectory, "recompile");

	/// <summary>
	/// The working directory every zig invocation runs in. zig names the headers of its own <c>lib/</c> relative to
	/// it in the dependency files it writes, so <see cref="CompileCache"/> resolves them against this same directory.
	/// </summary>
	private static string CompilerWorkingDirectory => RecompileDirectory;

	/// <summary>
	/// <c>bin/recompile/&lt;Folder&gt;</c> for <paramref name="title"/>: its own hook sources and list.
	/// </summary>
	public static string GetTitleRecompileDirectory(Refii.Core.Engine_Title title) =>
		Path.Combine(RecompileDirectory, title.Folder);

	/// <summary>
	/// <c>bin/recompile/&lt;Folder&gt;</c>: the active title's own build inputs — its hook sources
	/// (<c>hooks/</c>) and their list (<c>hook_sources.cmake</c>), staged per title so a build
	/// never compiles another title's.
	/// </summary>
	public static string TitleRecompileDirectory =>
		GetTitleRecompileDirectory(Refii.Core.Engine_Title.Active);

	/// <summary>Staged hook list for <paramref name="title"/>.</summary>
	public static string GetHookSourcesPath(Refii.Core.Engine_Title title) =>
		Path.Combine(GetTitleRecompileDirectory(title), "hook_sources.cmake");

	/// <summary>The active title's staged hook list, <c>bin/recompile/&lt;Folder&gt;/hook_sources.cmake</c>.</summary>
	public static string HookSourcesPath => GetHookSourcesPath(Refii.Core.Engine_Title.Active);

	/// <summary>Title update path beside <paramref name="title"/>'s installed executable.</summary>
	public static string GetGameTitleUpdatePath(Refii.Core.Engine_Title title) =>
		Path.Combine(Path.GetDirectoryName(GetGameExecutablePath(title)) ?? string.Empty, "default.xexp");

	/// <summary>The title update beside the installed executable, when the player installed one.</summary>
	public static string GameTitleUpdatePath =>
		GetGameTitleUpdatePath(Refii.Core.Engine_Title.Active);

	/// <summary>Version folder name for <paramref name="title"/>.</summary>
	public static string GetGameVersionName(Refii.Core.Engine_Title title) =>
		title.VersionFolder(File.Exists(GetGameTitleUpdatePath(title)));

	/// <summary>
	/// Which <c>versions/</c> folder describes the installed game: the patched executable or
	/// the disc one.
	/// </summary>
	/// <remarks>
	/// THE TWO ARE DIFFERENT PROGRAMS. The title update is a full relink, so no guest address
	/// is shared between them; each folder carries its own manifest and config, and the hooks
	/// bind through the function names the two configs have in common. The generator applies
	/// the patch whenever it sits beside the executable, so its presence IS the version. Each
	/// title names its own folders (<see cref="Refii.Core.Engine_Title.VersionFolder"/>).
	/// </remarks>
	public static string GameVersionName =>
		GetGameVersionName(Refii.Core.Engine_Title.Active);

	/// <summary>The ReXGlue manifest and config directory for <paramref name="title"/>'s installed version.</summary>
	public static string GetVersionDirectory(Refii.Core.Engine_Title title) =>
		Path.Combine(RecompileDirectory, "versions", GetGameVersionName(title));

	/// <summary>The ReXGlue manifest and config for the installed game's version.</summary>
	public static string VersionDirectory =>
		GetVersionDirectory(Refii.Core.Engine_Title.Active);

	/// <summary>
	/// A file of the version folder for <paramref name="title"/>, named by the title's key:
	/// <c>refii_config.toml</c> for Fable 2, <c>refiii_config.toml</c> for Fable 3.
	/// </summary>
	private static string GetVersionFile(Refii.Core.Engine_Title title, string kind) =>
		Path.Combine(GetVersionDirectory(title), title.Key + "_" + kind + ".toml");

	private static string GetVersionConfigPath(Refii.Core.Engine_Title title) => GetVersionFile(title, "config");

	private static string GetVersionManifestPath(Refii.Core.Engine_Title title) => GetVersionFile(title, "manifest");

	private static string VersionConfigPath => GetVersionConfigPath(Refii.Core.Engine_Title.Active);

	private static string VersionManifestPath => GetVersionManifestPath(Refii.Core.Engine_Title.Active);

	/// <summary>Persistent compile cache root (<c>cache/&lt;Folder&gt;/compile</c>) for <paramref name="title"/>.</summary>
	/// <param name="title">Target title.</param>
	/// <returns>Full path to <paramref name="title"/>'s compile cache root.</returns>
	public static string GetCompileCacheRoot(Refii.Core.Engine_Title title) =>
		ActiveDriver?.CacheRootOverride is { Length: > 0 } cacheOverride
			? Path.Combine(cacheOverride, title.Folder, "compile")
			: Installer.GetCompileCacheDirectoryPath(title);

	/// <summary>The generator's persistent output directory (<c>cache/&lt;Folder&gt;/compile/generated</c>) for <paramref name="title"/>.</summary>
	/// <param name="title">Target title.</param>
	/// <returns>Full path to <paramref name="title"/>'s generated C++ cache directory.</returns>
	public static string GetGeneratedDirectory(Refii.Core.Engine_Title title) =>
		Path.Combine(GetCompileCacheRoot(title), "generated");

	/// <summary>The generator's persistent output directory for the active title.</summary>
	public static string GeneratedDirectory => GetGeneratedDirectory(Refii.Core.Engine_Title.Active);

	/// <summary>Content-addressed <c>.o</c> and <c>.pch</c> cache directory for <paramref name="title"/>.</summary>
	/// <param name="title">Target title.</param>
	/// <returns>Full path to <paramref name="title"/>'s object cache directory.</returns>
	public static string GetObjectCacheDirectory(Refii.Core.Engine_Title title) =>
		Path.Combine(GetCompileCacheRoot(title), "objects");

	/// <summary>Persistent Zig runtime/CRT cache directory for <paramref name="title"/>.</summary>
	/// <param name="title">Target title.</param>
	/// <returns>Full path to <paramref name="title"/>'s Zig cache directory.</returns>
	public static string GetZigCacheDirectory(Refii.Core.Engine_Title title) =>
		Path.Combine(GetCompileCacheRoot(title), "zig-cache");

	/// <summary>Ephemeral per-build staging directory for extracted native hard-mod sources.</summary>
	private static string GetModsStagingDirectory(Refii.Core.Engine_Title title) =>
		Path.Combine(GetCompileCacheRoot(title), "mods");

	/// <summary>Ephemeral per-build scratch directory for fresh compiler/linker outputs before caching.</summary>
	private static string GetWorkDirectory(Refii.Core.Engine_Title title) =>
		Path.Combine(GetCompileCacheRoot(title), "work");

	/// <summary>The executable the installer wrote for <paramref name="title"/>.</summary>
	public static string GetGameExecutablePath(Refii.Core.Engine_Title title) =>
		ActiveDriver?.GameExecutablePathOverride is { } custom
			? custom(title)
			: Installer.GetGameExecutablePath(title);

	/// <summary>The executable the installer wrote, not a path of our own.</summary>
	public static string GameExecutablePath => GetGameExecutablePath(Refii.Core.Engine_Title.Active);

	/// <summary>ReXGlue instance bin directory for <paramref name="title"/> (<c>bin/&lt;Folder&gt;</c>).</summary>
	public static string GetTitleBinDirectory(Refii.Core.Engine_Title title) =>
		Path.Combine(BinDirectory, title.Folder);

	/// <summary>
	/// The active title's ReXGlue instance: <c>bin/&lt;Folder&gt;/</c>, its host bridge and vfs beside
	/// the module built here, the shared natives one directory up.
	/// </summary>
	public static string TitleBinDirectory => GetTitleBinDirectory(Refii.Core.Engine_Title.Active);

	/// <summary>Compiled guest payload module path for <paramref name="title"/>.</summary>
	public static string GetModulePath(Refii.Core.Engine_Title title) =>
		Path.Combine(GetTitleBinDirectory(title), Refii.Core.Engine_NativeLibraries.FileNameFor(title.ModuleName));

	/// <summary>
	/// The active title's module, in its instance folder where the payload loader looks for it,
	/// beside the host bridge that loads it: <c>refii_generated</c> for Fable 2,
	/// <c>refiii_generated</c> for Fable 3.
	/// </summary>
	public static string ModulePath =>
		GetModulePath(Refii.Core.Engine_Title.Active);

	/// <summary>Build metadata record path for <paramref name="title"/>.</summary>
	public static string GetBuildInfoPath(Refii.Core.Engine_Title title) =>
		Path.Combine(GetTitleBinDirectory(title), "build-info.json");

	/// <summary>What the active title's module was built from, beside it.</summary>
	public static string BuildInfoPath => GetBuildInfoPath(Refii.Core.Engine_Title.Active);

	/// <summary>Fine-grained tracked dependency closure path (<c>module-inputs.txt</c>) for <paramref name="title"/>.</summary>
	/// <param name="title">Target title.</param>
	/// <returns>Full path to <paramref name="title"/>'s <c>module-inputs.txt</c> beside its module.</returns>
	public static string GetModuleInputsPath(Refii.Core.Engine_Title title) =>
		Path.Combine(GetTitleBinDirectory(title), "module-inputs.txt");

	/// <summary>Fine-grained tracked dependency closure path for the active title.</summary>
	public static string ModuleInputsPath => GetModuleInputsPath(Refii.Core.Engine_Title.Active);

	private const string SettingsSection = "Recompile";
	private const string UseAllCoresKey = "BuildUseAllCores";

	/// <summary>
	/// Whether to compile with every logical thread rather than leaving two threads for desktop UI.
	/// </summary>
	/// <remarks>
	/// DEFAULTS OFF. By default <see cref="JobCount"/> runs <c>ProcessorCount - 2</c> workers at
	/// <c>BelowNormal</c> OS priority (bounded by free RAM) so the machine stays responsive while
	/// utilizing nearly the full CPU; turning this on targets all logical threads.
	/// </remarks>
	public static bool UseAllCores
	{
		get => Ini.GetBool(UseAllCoresKey, false, SettingsSection);
		set
		{
			Ini.SetBool(UseAllCoresKey, value, SettingsSection);
			Ini.Save();
		}
	}

	/// <summary>
	/// Compile jobs to run at once, bounded by logical core count, <see cref="UseAllCores"/>, and available RAM.
	/// </summary>
	/// <returns>Planned worker count of at least 1.</returns>
	public static int JobCount()
	{
		Driver? driver = ActiveDriver;
		int cores = driver?.ProcessorCountOverride ?? Environment.ProcessorCount;
		long availableMemory = driver?.AvailableMemoryBytesOverride != null
			? driver.AvailableMemoryBytesOverride()
			: CompileScheduler.AvailableMemoryBytes();
		return CompileScheduler.PlanDesktopJobs(cores, availableMemory, UseAllCores);
	}

	public readonly record struct Progress(string Status, float Fraction);

	/// <summary>The outcome. <c>Stopped</c> is the player's doing, not a failure.</summary>
	public readonly record struct Result(bool Ok, string Error, bool Stopped = false);

	private static Result StoppedResult => new(false, "stopped", Stopped: true);

	/// <summary>True when <paramref name="title"/>'s module exists on disk.</summary>
	public static bool IsBuilt(Refii.Core.Engine_Title title) => File.Exists(GetModulePath(title));

	/// <summary>True when a module exists at all.</summary>
	public static bool IsBuilt() => IsBuilt(Refii.Core.Engine_Title.Active);

	private const long StatusTtlMs = 2000;
	private static readonly Dictionary<string, (bool NeedsBuild, string Reason, long Stamp)> _statusCache =
		new(StringComparer.Ordinal);

	public static void InvalidateStatus() => _statusCache.Clear();

	/// <summary>
	/// Cached <see cref="NeedsBuild(out string)"/>, for the UI.
	/// </summary>
	/// <remarks>
	/// The uncached check hashes the executable and read inputs, and the panel that shows this
	/// refreshes continuously — caching per title keeps UI redraws completely free.
	/// </remarks>
	public static bool NeedsBuildCached(out string reason) =>
		NeedsBuildCached(Refii.Core.Engine_Title.Active, out reason);

	/// <summary>Cached <see cref="NeedsBuild(Refii.Core.Engine_Title, out string)"/> per title for dual-card UI refresh.</summary>
	public static bool NeedsBuildCached(Refii.Core.Engine_Title title, out string reason)
	{
		long now = Environment.TickCount64;
		if (_statusCache.TryGetValue(title.Key, out var cached) && now - cached.Stamp < StatusTtlMs)
		{
			reason = cached.Reason;
			return cached.NeedsBuild;
		}

		bool needs = NeedsBuild(title, out reason);
		_statusCache[title.Key] = (needs, reason, now);
		return needs;
	}

	/// <summary>
	/// Whether the module has to be produced again, and why.
	/// </summary>
	public static bool NeedsBuild(out string reason) =>
		NeedsBuild(Refii.Core.Engine_Title.Active, out reason);

	/// <summary>
	/// Whether <paramref name="title"/>'s module has to be produced again, and why.
	/// </summary>
	/// <remarks>
	/// The game hash is the load-bearing check: a module built from a different image than
	/// the one currently installed is not merely out of date, it is wrong — every function
	/// address in it belongs to another executable. Comparing the SHA-256 of default.xex
	/// against what the build recorded is what catches a player reinstalling from a
	/// different disc or applying a title update.
	/// </remarks>
	public static bool NeedsBuild(Refii.Core.Engine_Title title, out string reason)
	{
		string modulePath = GetModulePath(title);
		if (!File.Exists(modulePath))
		{
			reason = "The recompiled game module has not been built yet.";
			return true;
		}

		// DEV ONLY, and deliberately AFTER the module-exists test so it can never conjure a
		// module that is not there. Written by REFII_DEV_SKIP_GAMECODE_REBUILD=ON; packaging
		// excludes it and the export refuses to zip a tree containing it, so it cannot reach a
		// player. Announced every launch, because a silently-skipped rebuild is exactly the
		// failure the input fingerprint below exists to prevent.
		if (File.Exists(Path.Combine(RecompileDirectory, "dev-skip-rebuild")))
		{
			if (!Refii.Core.Engine_AppRoot.IsTestHost)
			{
				Godot.GD.Print(
					"[gamecode] dev-skip-rebuild is present: using the module already in bin/ " +
					"without checking it against the installed game or the shipped sources. " +
					"Turn REFII_DEV_SKIP_GAMECODE_REBUILD off to restore the real check.");
			}

			reason = string.Empty;
			return false;
		}

		string buildInfoPath = GetBuildInfoPath(title);
		if (!File.Exists(buildInfoPath))
		{
			reason = "The recompiled game module has no build record beside it.";
			return true;
		}

		string recordedHash;
		string recordedInputs;
		string recordedMods;
		try
		{
			using FileStream stream = File.OpenRead(buildInfoPath);
			using JsonDocument document = JsonDocument.Parse(stream);
			recordedHash = document.RootElement.TryGetProperty("game_hash", out JsonElement value)
				? value.GetString() ?? string.Empty
				: string.Empty;
			recordedInputs =
				document.RootElement.TryGetProperty("build_inputs", out JsonElement inputs)
					? inputs.GetString() ?? string.Empty
					: string.Empty;
			// ABSENT MEANS NONE: every record written before hard mods existed compiled no mod code,
			// which is exactly what an empty hash states.
			recordedMods = document.RootElement.TryGetProperty("mods_hash", out JsonElement mods)
				? mods.GetString() ?? string.Empty
				: string.Empty;
		}
		catch (Exception exception)
		{
			reason = "The build record could not be read: " + exception.Message;
			return true;
		}

		string currentHash = HashGameExecutable(title);
		if (currentHash.Length == 0)
		{
			reason = "The installed game executable could not be read.";
			return true;
		}

		if (!string.Equals(recordedHash, currentHash, StringComparison.OrdinalIgnoreCase))
		{
			reason = "The installed game executable has changed since the module was built.";
			return true;
		}

		// OUR OWN CODE, which the game hash cannot see. A record with no `build_inputs` at all
		// was written by a build from before this check existed, and there is no way to know
		// what it compiled — so it rebuilds rather than being assumed current.
		if (recordedInputs.Length == 0)
		{
			reason = "The build record does not say what sources the module was built from.";
			return true;
		}

		if (recordedInputs.StartsWith(ModuleInputsPrefix, StringComparison.Ordinal))
		{
			string expectedDigest = recordedInputs[ModuleInputsPrefix.Length..];
			if (!ModuleInputsHold(title, expectedDigest))
			{
				reason = "The shipped game code has changed since the module was built — " +
					"unpacking an update over an install is exactly this, and running the old " +
					"module would silently keep every fix in that update out of the game.";
				return true;
			}
		}
		else
		{
			string currentInputs = HashBuildInputs(title);
			if (currentInputs.Length == 0)
			{
				reason = "The build inputs beside the module could not be read.";
				return true;
			}

			if (!string.Equals(recordedInputs, currentInputs, StringComparison.OrdinalIgnoreCase))
			{
				reason = "The shipped game code has changed since the module was built — " +
					"unpacking an update over an install is exactly this, and running the old " +
					"module would silently keep every fix in that update out of the game.";
				return true;
			}
		}

		if (LinkedAgainstAnotherConfiguration(title, modulePath, out string strayLibrary))
		{
			reason = "The module was linked against " + strayLibrary + ", which is not the build "
				+ "of that library this install runs on. Two copies of the runtime in one game "
				+ "stop it at start-up.";
			return true;
		}

		// HARD MODS ARE COMPILED IN, so the module is only current for the native code it was
		// built with. Enabling, disabling, reordering or updating one changes this hash.
		string currentMods = HashNativeMods(title);
		if (!string.Equals(recordedMods, currentMods, StringComparison.OrdinalIgnoreCase))
		{
			reason = currentMods.Length == 0
				? "The mods with native code that the game was built with are no longer enabled."
				: "The enabled mods with native code have changed since the game code was built.";
			return true;
		}

		reason = string.Empty;
		return false;
	}

	private static string GetModsRoot(Refii.Core.Engine_Title title) =>
		ActiveDriver?.ModsRootOverride is { } custom
			? custom(title)
			: title == Refii.Core.Engine_Title.Active
				? Refii.Assets.Engine_GameAssets.ModsRoot
				: ModWorkspace.GetDefaultModsRoot(title.AssetsDirectory);

	/// <summary>
	/// The hard mods a build compiles: enabled, consented to, highest priority first.
	/// </summary>
	internal static List<ModNativeSources> NativeModsForBuild(out List<string> withheld) =>
		NativeModsForBuild(Refii.Core.Engine_Title.Active, out withheld);

	private static List<ModNativeSources> NativeModsForBuild(Refii.Core.Engine_Title title, out List<string> withheld)
	{
		if (!title.HasHardMods)
		{
			withheld = new List<string>();
			return new List<ModNativeSources>();
		}

		return NativeModConsent.ForBuild(GetModsRoot(title), out withheld);
	}

	/// <summary>
	/// The fingerprint of the native code mods add to this build; empty when they add none.
	/// </summary>
	public static string HashNativeMods() => HashNativeMods(Refii.Core.Engine_Title.Active);

	/// <summary>
	/// The fingerprint of the native code mods add to <paramref name="title"/>'s build; empty when none.
	/// </summary>
	public static string HashNativeMods(Refii.Core.Engine_Title title)
	{
		try
		{
			return ModNative.HashSet(NativeModsForBuild(title, out _));
		}
		catch (Exception exception)
		{
			return "unreadable: " + exception.Message;
		}
	}

	/// <summary>
	/// Extracts every enabled, consented hard mod's native sources into the ephemeral per-build mods
	/// staging directory and appends the compiled ones to <paramref name="sources"/>.
	/// </summary>
	/// <remarks>
	/// Four refusals, all before a single unit compiles, each naming the mod:
	/// <list type="bullet">
	/// <item>a native mod requires a mod that is missing, disabled or too old;</item>
	/// <item>a hook replaces a function's body while another owner hooks it beneath (two owners
	/// that both wrap are chained instead, see <see cref="ModNative"/>);</item>
	/// <item>a mod overrides a name the generator never declared — otherwise its hook defines a
	/// symbol nothing calls and the mod silently does nothing. The usual cause is a
	/// <c>sub_XXXXXXXX</c> the config has named, so the config's name is offered;</item>
	/// <item>an entry path that would leave the mod's folder.</item>
	/// </list>
	/// Runs after generation, so the declared set is this install's own.
	/// </remarks>
	private static Result AddNativeModSources(
		Refii.Core.Engine_Title title,
		List<string> sources,
		Dictionary<string, List<string>> arguments,
		IReadOnlyList<string> hookSources,
		out string modsHash)
	{
		modsHash = string.Empty;
		List<ModNativeSources> mods;
		List<string> withheld;
		try
		{
			mods = NativeModsForBuild(title, out withheld);
		}
		catch (Exception exception)
		{
			return new Result(false, "The mods folder could not be read: " + exception.Message);
		}

		return AddNativeModSources(mods, withheld, () => NativeModConsent.RefiiOverrides(title), GetModsRoot(title),
			GetGeneratedDirectory(title), GetVersionConfigPath(title), GetModsStagingDirectory(title),
			sources, arguments, hookSources, LogWrite, out modsHash);
	}

	/// <summary>
	/// <see cref="AddNativeModSources(Refii.Core.Engine_Title, List{string}, Dictionary{string, List{string}}, IReadOnlyList{string}, out string)"/>
	/// for any build's folders: the phone build passes its own attempt's generated code, version
	/// config, mods staging folder, hook sources and log. <paramref name="readRefiiOverrides"/> is called
	/// only once a mod with native code is there and its dependencies are met, where the per-title
	/// build always read it.
	/// </summary>
	internal static Result AddNativeModSources(
		List<ModNativeSources> mods, IReadOnlyList<string> withheld, Func<Dictionary<string, string>> readRefiiOverrides,
		string modsRoot, string generatedDirectory, string versionConfig, string modsStagingDirectory,
		List<string> sources, Dictionary<string, List<string>> arguments, IReadOnlyList<string> hookSources,
		Action<string> log, out string modsHash)
	{
		modsHash = string.Empty;
		foreach (string name in withheld)
		{
			log($"mod '{name}': its native code is NOT compiled - its current sources were " +
				"never enabled in the launcher's Mods panel");
		}

		if (mods.Count == 0)
		{
			return new Result(true, string.Empty);
		}

		List<ModDependencyProblem> unmet = ModDependencies.Check(
			ModLoadOrder.Read(modsRoot), ModWorkspace.DiscoverModInstalls(modsRoot),
			mods.Select(mod => mod.ModName));
		if (unmet.Count > 0)
		{
			return new Result(false,
				"A mod with native code needs another mod that is not there:\n" +
				string.Join("\n", unmet.Select(problem => $"  {problem.Mod}: {problem.Message}")) +
				"\nEnable or install what it needs in the launcher's Mods panel.");
		}

		Dictionary<string, string> refiiOverrides = readRefiiOverrides();
		List<ModNativeConflict> conflicts = ModNative.FindConflicts(mods, refiiOverrides);
		if (conflicts.Count > 0)
		{
			return new Result(false,
				"A mod replaces the body of a game function that another owner also hooks, so that " +
				"hook would never run:\n" +
				string.Join("\n", conflicts.Select(conflict =>
					$"  {conflict.Function}: {conflict.First} replaces it over {conflict.Second}")) +
				"\nDisable one of the mods named in the launcher's Mods panel.");
		}

		HashSet<string> declared = ReadDeclaredFunctions(generatedDirectory);
		if (declared.Count == 0)
		{
			return new Result(false,
				"The generated code declares no functions, so the mods' hooks cannot be checked.");
		}

		Dictionary<uint, string> configNames = ReadConfigFunctionNames(versionConfig);
		var unknown = new List<string>();
		foreach (ModNativeSources mod in mods)
		{
			foreach (string function in mod.Overrides)
			{
				if (declared.Contains(function))
				{
					continue;
				}

				string hint = function.StartsWith("sub_", StringComparison.Ordinal) &&
					uint.TryParse(function.AsSpan(4), System.Globalization.NumberStyles.HexNumber,
						null, out uint address) &&
					configNames.TryGetValue(address, out string? named)
						? $" (this build calls it {named})"
						: " (no such function in this build of the game)";
				unknown.Add($"  {mod.ModName}: {function}{hint}");
			}
		}

		if (unknown.Count > 0)
		{
			return new Result(false,
				"A mod replaces a game function that does not exist under that name, so its hook " +
				"would never run:\n" + string.Join("\n", unknown));
		}

		string extracted = modsStagingDirectory;
		var compiledBy = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		foreach (ModNativeSources mod in mods)
		{
			string root = Path.Combine(extracted, mod.ModName);
			List<string> compiled = compiledBy[mod.ModName] = new List<string>();
			foreach (ModNativeFile file in mod.Files)
			{
				string target = Path.GetFullPath(Path.Combine(root, file.Path));
				StringComparison pathComparison = OperatingSystem.IsWindows()
					? StringComparison.OrdinalIgnoreCase
					: StringComparison.Ordinal;
				if (!target.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, pathComparison))
				{
					return new Result(false, $"mod '{mod.ModName}': '{file.Path}' leaves the mod's folder.");
				}

				try
				{
					Directory.CreateDirectory(Path.GetDirectoryName(target)!);
					File.WriteAllBytes(target, file.Bytes);
				}
				catch (Exception exception)
				{
					return new Result(false,
						$"mod '{mod.ModName}': could not write '{file.Path}': {exception.Message}");
				}

				if (ModNative.IsCompiled(file.Path))
				{
					sources.Add(target);
					compiled.Add(target);
					// A hard mod's own headers, for its own sources only: two mods may each ship a
					// "combat.h" and neither may see the other's. REFII_MOD_NAME is what the event
					// bus logs the mod's handlers under (core/refii_mod_events.h).
					AddArguments(arguments, target, new[] { "-I" + root, $"-DREFII_MOD_NAME=\"{mod.ModName}\"" });
				}
			}

			log($"mod '{mod.ModName}': compiling {mod.CompiledFiles.Count()} native source(s), " +
				$"hooking {string.Join(", ", mod.Overrides)}");
		}

		// A REQUIRED MOD'S PUBLIC HEADERS. What a mod puts under native/include/ is its API to the
		// mods that require it; nothing else of it is visible to them.
		Dictionary<string, ModInstall> installs = ModWorkspace.DiscoverModInstalls(modsRoot)
			.ToDictionary(mod => mod.Name, StringComparer.OrdinalIgnoreCase);
		foreach (ModNativeSources mod in mods)
		{
			if (!installs.TryGetValue(mod.ModName, out ModInstall? install))
			{
				continue;
			}

			foreach (ModRequirement requirement in install.Metadata.Requires)
			{
				ModNativeSources? required = mods.FirstOrDefault(other =>
					string.Equals(other.ModName, requirement.Name, StringComparison.OrdinalIgnoreCase));
				if (required == null)
				{
					continue;
				}

				string include = Path.Combine(extracted, required.ModName, "include");
				foreach (string source in compiledBy[mod.ModName])
				{
					AddArguments(arguments, source, new[] { "-I" + include });
				}
			}
		}

		// CHAINS. A function hooked by more than one owner gets each owner's hook under its own
		// symbol and one generated entry; see ModNative.
		List<ModNativeChain> chains = ModNative.PlanChains(mods, refiiOverrides);
		if (chains.Count > 0)
		{
			foreach (ModNativeSources mod in mods)
			{
				List<string> defines = ModNative.ChainDefines(chains, mod.ModName);
				foreach (string source in compiledBy[mod.ModName])
				{
					AddArguments(arguments, source, defines);
				}
			}

			foreach (string hookSource in hookSources)
			{
				List<string> defined;
				try
				{
					defined = ModNative.DeriveOverrides(File.ReadAllText(hookSource)).ToList();
				}
				catch (Exception)
				{
					continue;
				}

				List<string> defines = ModNative.ChainDefines(
					chains.Where(chain => defined.Contains(chain.Function)).ToList(), ModNative.RefiiOwner);
				AddArguments(arguments, hookSource, defines);
			}

			string chainFile = Path.Combine(extracted, "refii_mod_chains.cpp");
			try
			{
				Directory.CreateDirectory(extracted);
				File.WriteAllText(chainFile, ModNative.ChainSource(chains));
			}
			catch (Exception exception)
			{
				return new Result(false, $"could not write '{chainFile}': {exception.Message}");
			}

			sources.Add(chainFile);
			foreach (ModNativeChain chain in chains)
			{
				log($"chain {chain.Function}: {string.Join(" -> ", chain.Owners)} -> game");
			}
		}

		modsHash = ModNative.HashSet(mods);
		return new Result(true, string.Empty);
	}

	private static void AddArguments(
		Dictionary<string, List<string>> arguments, string source, IEnumerable<string> extra)
	{
		if (!arguments.TryGetValue(source, out List<string>? list))
		{
			arguments[source] = list = new List<string>();
		}

		foreach (string argument in extra)
		{
			if (!list.Contains(argument, StringComparer.Ordinal))
			{
				list.Add(argument);
			}
		}
	}

	/// <summary>Every function the generator emitted into <paramref name="generatedDir"/>, from its <c>DECLARE_REX_FUNC(name)</c> lines.</summary>
	private static HashSet<string> ReadDeclaredFunctions(string generatedDir)
	{
		var declared = new HashSet<string>(StringComparer.Ordinal);
		const string marker = "DECLARE_REX_FUNC(";
		if (!Directory.Exists(generatedDir))
		{
			return declared;
		}

		foreach (string header in Directory.EnumerateFiles(generatedDir, "*.h"))
		{
			foreach (string line in File.ReadLines(header))
			{
				int at = line.IndexOf(marker, StringComparison.Ordinal);
				if (at < 0)
				{
					continue;
				}

				int start = at + marker.Length;
				int end = line.IndexOf(')', start);
				if (end > start)
				{
					declared.Add(line.Substring(start, end - start).Trim());
				}
			}
		}

		return declared;
	}

	/// <summary>The config's function names by address (<c>0x8272A270 = { name = "rex_…" }</c>) in the config at <paramref name="config"/>.</summary>
	private static Dictionary<uint, string> ReadConfigFunctionNames(string config)
	{
		var names = new Dictionary<uint, string>();
		if (!File.Exists(config))
		{
			return names;
		}

		foreach (string line in File.ReadLines(config))
		{
			string trimmed = line.TrimStart();
			int nameAt = trimmed.IndexOf("name = \"", StringComparison.Ordinal);
			if (!trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || nameAt < 0)
			{
				continue;
			}

			int equals = trimmed.IndexOf('=');
			if (!uint.TryParse(trimmed.AsSpan(2, equals - 2).Trim(),
				System.Globalization.NumberStyles.HexNumber, null, out uint address))
			{
				continue;
			}

			int start = nameAt + "name = \"".Length;
			int end = trimmed.IndexOf('"', start);
			if (end > start)
			{
				names[address] = trimmed.Substring(start, end - start);
			}
		}

		return names;
	}

	/// <summary>
	/// Removes obsolete files left behind if a player or developer extracted a new release zip
	/// or copied an updated tree over an existing installation without wiping bin/recompile first.
	/// </summary>
	public static void PurgeStaleRecompileArtifacts(string recompile)
	{
		try
		{
			if (string.IsNullOrEmpty(recompile) || !Directory.Exists(recompile))
			{
				return;
			}

			// 1. Pre-split flat hooks and manifest (replaced by bin/recompile/<Title>/...)
			string flatHookSources = Path.Combine(recompile, "hook_sources.cmake");
			if (File.Exists(flatHookSources))
			{
				File.Delete(flatHookSources);
			}

			string flatHooksDir = Path.Combine(recompile, "hooks");
			if (Directory.Exists(flatHooksDir))
			{
				Directory.Delete(flatHooksDir, recursive: true);
			}

			// 2. Pre-split headers under include/01 that were moved to include/02 (src/runtime)
			// during the multi-title refactor. If include/01/core/payload_bridge.h exists, clang
			// resolves it ahead of include/02/core/payload_bridge.h and collides with
			// payload_bridge_fable2.h, causing enumerator redefinition errors.
			string inc01 = Path.Combine(recompile, "include", "01");
			string staleBridge = Path.Combine(inc01, "core", "payload_bridge.h");
			if (File.Exists(staleBridge))
			{
				string[] staleFiles =
				{
					Path.Combine(inc01, "core", "payload_bridge.h"),
					Path.Combine(inc01, "core", "payload_bridge_string.h"),
					Path.Combine(inc01, "core", "rex_payload_bridge.h"),
					Path.Combine(inc01, "core", "audio_state.h"),
					Path.Combine(inc01, "core", "event_relay.h"),
					Path.Combine(inc01, "core", "guest_yield_backoff.h"),
					Path.Combine(inc01, "core", "host_asset_reader.h"),
					Path.Combine(inc01, "core", "mod_values.h"),
					Path.Combine(inc01, "core", "player_state.h"),
					Path.Combine(inc01, "core", "precise_sleep.h"),
					Path.Combine(inc01, "core", "traced_mutex.h"),
					Path.Combine(inc01, "babel", "babel_database.h"),
					Path.Combine(inc01, "gdb", "gdb_database.h"),
					Path.Combine(inc01, "gui", "gui_native_calls.h"),
					Path.Combine(inc01, "rex", "thread", "mutex.h"),
				};

				foreach (string file in staleFiles)
				{
					if (File.Exists(file))
					{
						File.Delete(file);
					}
				}

				foreach (string dir in new[]
				{
					Path.Combine(inc01, "babel"),
					Path.Combine(inc01, "gdb"),
					Path.Combine(inc01, "rex", "thread"),
					Path.Combine(inc01, "rex"),
				})
				{
					if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
					{
						Directory.Delete(dir);
					}
				}
			}

			// 3. Stale root VFS DLL if a title-specific copy exists under bin/<Title>/
			if (!string.IsNullOrEmpty(BinDirectory))
			{
				string rootVfs = Path.Combine(BinDirectory, "refii_vfs.dll");
				string fable2Vfs = Path.Combine(BinDirectory, "Fable2", "refii_vfs.dll");
				if (File.Exists(rootVfs) && File.Exists(fable2Vfs))
				{
					File.Delete(rootVfs);
				}

				string rootLuaLib = Path.Combine(BinDirectory, "refii_lua.lib");
				if (File.Exists(rootLuaLib))
				{
					File.Delete(rootLuaLib);
				}
			}
		}
		catch
		{
			// Best-effort cleanup: never block the build if a file is locked or cannot be deleted
		}
	}

	/// <summary>
	/// A fingerprint of everything the module is COMPILED FROM, computed off the disk.
	/// </summary>
	/// <remarks>
	/// THE GAME HASH ALONE CANNOT SEE OUR OWN UPDATES. It catches a different disc or a title
	/// update, and nothing else — so a player who unpacked a new build over an old install kept
	/// running the module the OLD sources produced, silently, with no version anywhere that
	/// disagreed. Every fix we ship that touches a hook or a header was simply absent for them.
	///
	/// MEASURED, NOT STAMPED. A version written at package time would only describe what
	/// packaging believed it wrote; this hashes what is actually present, so it is right however
	/// the install got there — a zip over the top, a partial copy, a hand-edited header in a dev
	/// tree. Content only, never timestamps: unpacking a zip rewrites every mtime and would
	/// otherwise force a rebuild on every update whether or not anything changed.
	///
	/// Measured at ~0.8 s over 2,825 files. Paid once per launch, and only when there is already
	/// a module to judge.
	/// </remarks>
	public static string HashBuildInputs() => HashBuildInputs(Refii.Core.Engine_Title.Active);

	/// <summary>Hashes the staged build inputs and compile flags for <paramref name="title"/>.</summary>
	public static string HashBuildInputs(Refii.Core.Engine_Title title)
	{
		try
		{
			PurgeStaleRecompileArtifacts(RecompileDirectory);

			var files = new List<string>();

			// The title's own hooks, never another title's.
			foreach (string directory in new[]
				{
					Path.Combine(GetTitleRecompileDirectory(title), "hooks"),
					Path.Combine(RecompileDirectory, "payload_core"),
					Path.Combine(RecompileDirectory, "include"),
					Path.Combine(RecompileDirectory, "stdcxx"),
				})
			{
				if (Directory.Exists(directory))
				{
					files.AddRange(Directory.EnumerateFiles(
						directory, "*", SearchOption.AllDirectories));
				}
			}

			foreach (string file in new[]
				{
					GetHookSourcesPath(title),
					Path.Combine(RecompileDirectory, "payload_core_sources.cmake"),
					Path.Combine(RecompileDirectory, "include_dirs.cmake"),
				})
			{
				if (File.Exists(file))
				{
					files.Add(file);
				}
			}

			// The manifest and config the generator reads for THIS install's version. They
			// decide which functions exist and what they are called, so a config change is a
			// code change even when no hook source moved.
			string versionDirectory = GetVersionDirectory(title);
			if (Directory.Exists(versionDirectory))
			{
				files.AddRange(Directory.EnumerateFiles(
					versionDirectory, "*", SearchOption.AllDirectories));
			}

			if (files.Count == 0)
			{
				return string.Empty;
			}

			// Sorted by the path RELATIVE to the recompile directory, and ordinally, so the
			// fingerprint does not move when the install does or when a different filesystem
			// hands the enumeration back in another order.
			files.Sort((a, b) => string.CompareOrdinal(
				Path.GetRelativePath(RecompileDirectory, a).Replace('\\', '/'),
				Path.GetRelativePath(RecompileDirectory, b).Replace('\\', '/')));

			using var sha = Refii.Core.Engine_Hash.CreateSha256();
			var buffer = new byte[64 * 1024];

			foreach (string file in files)
			{
				// The PATH is hashed as well as the bytes: renaming a header, or the include
				// renumbering that moved Fable2/src/vfs/include from slot 15 to 16, changes what the
				// compiler resolves without changing any file's contents.
				byte[] name = Encoding.UTF8.GetBytes(
					Path.GetRelativePath(RecompileDirectory, file).Replace('\\', '/') + "\n");
				sha.TransformBlock(name, 0, name.Length, null, 0);

				using FileStream stream = File.OpenRead(file);
				int read;
				while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
				{
					sha.TransformBlock(buffer, 0, read, null, 0);
				}
			}

			// The compile flags are an input too: a module built for instructions this CPU
			// lacks would otherwise stay until some source happened to change. Paths are left
			// out, as above, so moving the install does not move the fingerprint.
			List<string> compileFlags = CompileFlags(title, Array.Empty<string>());
			var options = new StringBuilder();
			for (int i = 0; i < compileFlags.Count; i++)
			{
				if (compileFlags[i] == "-include")
				{
					i++;
					continue;
				}
				if (compileFlags[i].StartsWith("-I", StringComparison.Ordinal))
				{
					continue;
				}
				options.Append(compileFlags[i]).Append('\n');
			}
			// -march=native ties the module to this CPU, so its feature bits are an input as
			// well: an install copied to a machine without, say, AVX2 rebuilds instead of
			// loading instructions it cannot run. Leaf 1 EBX is left out; it carries the APIC
			// id of whichever core this thread is on.
			if (X86Base.IsSupported)
			{
				(_, _, int ecx1, int edx1) = X86Base.CpuId(1, 0);
				(_, int ebx7, int ecx7, int edx7) = X86Base.CpuId(7, 0);
				(_, _, int ecxExt, int edxExt) = X86Base.CpuId(unchecked((int)0x80000001), 0);
				options.Append($"cpu {ecx1:X8} {edx1:X8} {ebx7:X8} {ecx7:X8} {edx7:X8} {ecxExt:X8} {edxExt:X8}\n");
			}
			byte[] optionBytes = Encoding.UTF8.GetBytes(options.ToString());
			sha.TransformBlock(optionBytes, 0, optionBytes.Length, null, 0);

			sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
			return Convert.ToHexString(sha.Hash ?? Array.Empty<byte>());
		}
		catch (Exception)
		{
			// Deliberately NOT a silent pass: an unreadable input tree returns empty, and the
			// caller treats empty as "cannot prove it is current", which rebuilds.
			return string.Empty;
		}
	}

	private static readonly ConcurrentDictionary<string, (long XexLen, long XexTicks, bool HasXexp, long XexpLen, long XexpTicks, string Hash)> GameExeHashCache =
		new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

	private static readonly ConcurrentDictionary<string, (long Length, long Ticks, string Version)> ToolchainVersionCache =
		new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

	public static string HashGameExecutable() => HashGameExecutable(Refii.Core.Engine_Title.Active);

	/// <summary>Hashes <paramref name="title"/>'s installed <c>default.xex</c> and optional <c>default.xexp</c>.</summary>
	public static string HashGameExecutable(Refii.Core.Engine_Title title)
	{
		try
		{
			string xexPath = Path.GetFullPath(GetGameExecutablePath(title));
			var xexInfo = new FileInfo(xexPath);
			if (!xexInfo.Exists)
			{
				return string.Empty;
			}

			string updatePath = Path.GetFullPath(GetGameTitleUpdatePath(title));
			var updateInfo = new FileInfo(updatePath);
			bool hasUpdate = updateInfo.Exists;
			long updateLen = hasUpdate ? updateInfo.Length : 0;
			long updateTicks = hasUpdate ? updateInfo.LastWriteTimeUtc.Ticks : 0;

			long nowTicks = DateTime.UtcNow.Ticks;
			bool safeTimestamps = nowTicks - xexInfo.LastWriteTimeUtc.Ticks > TimeSpan.TicksPerSecond * 2 &&
				(!hasUpdate || nowTicks - updateTicks > TimeSpan.TicksPerSecond * 2);
			if (safeTimestamps &&
				GameExeHashCache.TryGetValue(xexPath, out var cached) &&
				cached.XexLen == xexInfo.Length &&
				cached.XexTicks == xexInfo.LastWriteTimeUtc.Ticks &&
				cached.HasXexp == hasUpdate &&
				cached.XexpLen == updateLen &&
				cached.XexpTicks == updateTicks)
			{
				return cached.Hash;
			}

			// The executable AND its title update: the generator recompiles the patched image
			// when the patch is present, so adding or removing it changes the code produced
			// without changing a byte of default.xex.
			using var sha = Refii.Core.Engine_Hash.CreateSha256();
			byte[] buffer = new byte[64 * 1024];
			using (FileStream xexStream = File.OpenRead(xexPath))
			{
				int read;
				while ((read = xexStream.Read(buffer, 0, buffer.Length)) > 0)
				{
					sha.TransformBlock(buffer, 0, read, null, 0);
				}
			}

			if (hasUpdate)
			{
				using FileStream updateStream = File.OpenRead(updatePath);
				int read;
				while ((read = updateStream.Read(buffer, 0, buffer.Length)) > 0)
				{
					sha.TransformBlock(buffer, 0, read, null, 0);
				}
			}

			sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
			string digest = Convert.ToHexString(sha.Hash ?? Array.Empty<byte>());
			if (safeTimestamps)
			{
				GameExeHashCache[xexPath] = (xexInfo.Length, xexInfo.LastWriteTimeUtc.Ticks, hasUpdate, updateLen, updateTicks, digest);
			}
			else
			{
				GameExeHashCache.TryRemove(xexPath, out _);
			}

			return digest;
		}
		catch (Exception)
		{
			return string.Empty;
		}
	}

	private static string GetToolchainVersion(Toolchain zig)
	{
		if (ActiveDriver?.ToolchainVersion is { } custom)
		{
			return custom(zig);
		}

		try
		{
			string full = Path.GetFullPath(zig.Zig);
			var info = new FileInfo(full);
			if (info.Exists &&
				ToolchainVersionCache.TryGetValue(full, out var cached) &&
				cached.Length == info.Length &&
				cached.Ticks == info.LastWriteTimeUtc.Ticks)
			{
				return cached.Version;
			}

			string version = Toolchain.Version(zig);
			if (info.Exists && version.Length > 0)
			{
				ToolchainVersionCache[full] = (info.Length, info.LastWriteTimeUtc.Ticks, version);
			}

			return version;
		}
		catch (Exception)
		{
			return Toolchain.Version(zig);
		}
	}

	private static string GetSdkVersion() =>
		ActiveDriver?.SdkVersion is { } custom ? custom() : Recompiler.SdkVersion();

	private static string GeneratorIdentity()
	{
		string generator = Path.Combine(BinDirectory, Refii.Core.Engine_NativeLibraries.FileNameFor("refii_recompiler"));
		return CompileCache.HashFileMemoized(generator) ?? "missing";
	}

	private static string ToolchainBinaryIdentity(Toolchain? resolved = null)
	{
		if (ActiveDriver != null)
		{
			Toolchain tc;
			if (resolved.HasValue)
			{
				tc = resolved.Value;
			}
			else if (ActiveDriver.ResolveToolchain != null)
			{
				var res = ActiveDriver.ResolveToolchain();
				if (!res.Ok)
				{
					return "missing";
				}

				tc = res.Toolchain;
			}
			else if (Toolchain.TryResolve(out Toolchain bundled, out _))
			{
				tc = bundled;
			}
			else
			{
				return "missing";
			}

			string ver = GetToolchainVersion(tc);
			string binHash = !string.IsNullOrEmpty(tc.Zig) && File.Exists(tc.Zig)
				? CompileCache.HashFileMemoized(tc.Zig) ?? "unreadable"
				: "virtual";
			return ver + ":" + binHash;
		}

		string zigPath = resolved.HasValue
			? resolved.Value.Zig
			: Path.Combine(Toolchain.BundleDirectory, OperatingSystem.IsWindows() ? "zig.exe" : "zig");
		return CompileCache.HashFileMemoized(zigPath) ?? "missing";
	}

	private static string DesktopCompileRecipe(Refii.Core.Engine_Title title, string toolchainIdentity)
	{
		var text = new StringBuilder();
		text.Append("toolchain=").Append(toolchainIdentity).Append('\n');
		text.Append("rid=").Append(RuntimeInformation.RuntimeIdentifier).Append('\n');

		List<string> compileFlags = CompileFlags(title, Array.Empty<string>());
		for (int i = 0; i < compileFlags.Count; i++)
		{
			if (compileFlags[i] == "-include")
			{
				i++;
				text.Append("-include:src/libcxx_chrono_shim.h\n");
				continue;
			}

			if (compileFlags[i].StartsWith("-I", StringComparison.Ordinal))
			{
				continue;
			}

			text.Append("flag=").Append(compileFlags[i]).Append('\n');
		}

		// What each install folder is renamed to in __FILE__; the folders themselves are install-specific.
		foreach (string macroPrefix in MacroPrefixFlags(title))
		{
			text.Append("macro-prefix").Append(macroPrefix[macroPrefix.LastIndexOf('=')..]).Append('\n');
		}

		string orderPath = Path.Combine(StdCxxDirectory, "order.txt");
		if (File.Exists(orderPath))
		{
			text.Append("stdcxx=").Append(CompileCache.HashFileMemoized(orderPath) ?? "missing").Append('\n');
		}

		if (X86Base.IsSupported)
		{
			(_, _, int ecx1, int edx1) = X86Base.CpuId(1, 0);
			(_, int ebx7, int ecx7, int edx7) = X86Base.CpuId(7, 0);
			(_, _, int ecxExt, int edxExt) = X86Base.CpuId(unchecked((int)0x80000001), 0);
			text.Append($"cpu={ecx1:X8}:{edx1:X8}:{ebx7:X8}:{ecx7:X8}:{edx7:X8}:{ecxExt:X8}:{edxExt:X8}\n");
		}

		return CompileCache.HashText(text.ToString());
	}

	private static string DesktopEnvironmentRecipe(Refii.Core.Engine_Title title, Toolchain? resolved = null)
	{
		var text = new StringBuilder();
		text.Append("compile=").Append(DesktopCompileRecipe(title, ToolchainBinaryIdentity(resolved))).Append('\n');
		text.Append("generator=").Append(GeneratorIdentity()).Append('\n');
		text.Append("sdk=").Append(GetSdkVersion()).Append('\n');
		foreach (string targetFlag in TargetFlags())
		{
			text.Append("link=").Append(targetFlag).Append('\n');
		}

		if (!OperatingSystem.IsWindows())
		{
			if (UsesLargeCodeModel)
			{
				text.Append("link=-mcmodel=large\n");
			}

			text.Append("link=-Wl,-rpath,$ORIGIN:$ORIGIN/..:$ORIGIN/lib\n");
		}

		if (File.Exists(Path.Combine(StdCxxDirectory, "order.txt")))
		{
			text.Append("link=-nostdlib++\nlink=-lstdc++\n");
		}

		return CompileCache.HashText(text.ToString());
	}

	private static List<string> EnumerateRecompileRelativePaths()
	{
		string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RecompileDirectory));
		var results = new List<string>();
		if (!Directory.Exists(root))
		{
			return results;
		}

		string zigPrefix = Path.Combine(root, "zig") + Path.DirectorySeparatorChar;
		StringComparison comparison = OperatingSystem.IsWindows()
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;
		foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
		{
			if (file.StartsWith(zigPrefix, comparison))
			{
				continue;
			}

			results.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
		}

		results.Sort(StringComparer.Ordinal);
		return results;
	}

	private static List<string> GetRelativeIncludeDirectories(IReadOnlyList<string> includeDirectories)
	{
		string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RecompileDirectory));
		StringComparison comparison = OperatingSystem.IsWindows()
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;
		var relative = new List<string>();
		foreach (string dir in includeDirectories)
		{
			string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
			if (full.StartsWith(root + Path.DirectorySeparatorChar, comparison))
			{
				relative.Add(Path.GetRelativePath(root, full).Replace('\\', '/'));
			}
		}

		string order = Path.Combine(StdCxxDirectory, "order.txt");
		if (File.Exists(order))
		{
			foreach (string slot in File.ReadAllLines(order))
			{
				string trimmed = slot.Trim();
				if (trimmed.Length > 0)
				{
					string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(StdCxxDirectory, trimmed)));
					if (full.StartsWith(root + Path.DirectorySeparatorChar, comparison))
					{
						relative.Add(Path.GetRelativePath(root, full).Replace('\\', '/'));
					}
				}
			}
		}

		return relative;
	}

	private static IEnumerable<string> LinkedLibraryNames(Refii.Core.Engine_Title title) =>
		new[] { "rexruntime", "refii_core", "refii_runtime", "refii_vfs" }
			.Where(name => name != "refii_vfs" || title.Instance.Contains(name));

	private static string? WriteModuleInputs(
		Refii.Core.Engine_Title title,
		Toolchain resolved,
		CompileCache cache,
		IReadOnlyList<string> allRecompileRelativePaths,
		IReadOnlyList<string> relativeIncludeDirs)
	{
		if (!cache.Complete)
		{
			return null;
		}

		try
		{
			string recompileRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RecompileDirectory));
			string binRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(BinDirectory));
			var tracked = cache.TrackedInputs.OrderBy(path => path, StringComparer.Ordinal).ToList();
			var filesToHash = new SortedSet<string>(tracked, StringComparer.Ordinal);

			foreach (string orchestrationFile in new[]
			{
				GetHookSourcesPath(title),
				Path.Combine(RecompileDirectory, "payload_core_sources.cmake"),
				Path.Combine(RecompileDirectory, "include_dirs.cmake"),
				Path.Combine(StdCxxDirectory, "order.txt"),
			})
			{
				if (File.Exists(orchestrationFile))
				{
					filesToHash.Add(Path.GetRelativePath(recompileRoot, orchestrationFile).Replace('\\', '/'));
				}
			}

			string versionDir = GetVersionDirectory(title);
			if (Directory.Exists(versionDir))
			{
				foreach (string file in Directory.EnumerateFiles(versionDir, "*", SearchOption.AllDirectories))
				{
					filesToHash.Add(Path.GetRelativePath(recompileRoot, file).Replace('\\', '/'));
				}
			}

			var body = new StringBuilder();
			body.Append("env ").Append(DesktopEnvironmentRecipe(title, resolved)).Append('\n');
			body.Append("names ")
				.Append(CompileCache.SameNames(
					tracked,
					CompileCache.FileNameIndex(allRecompileRelativePaths),
					relativeIncludeDirs))
				.Append('\n');

			foreach (string rel in tracked)
			{
				body.Append("tracked ").Append(rel).Append('\n');
			}

			foreach (string rel in filesToHash)
			{
				string diskPath = Path.Combine(recompileRoot, rel.Replace('/', Path.DirectorySeparatorChar));
				string? hash = CompileCache.HashFileMemoized(diskPath);
				if (hash == null)
				{
					return null;
				}

				body.Append("file ").Append(hash).Append(' ').Append(rel).Append('\n');
			}

			foreach (string libName in LinkedLibraryNames(title))
			{
				string libPath = FindShippedLibrary(title, libName);
				string? libHash = libPath.Length == 0 ? null : CompileCache.HashFileMemoized(libPath);
				if (libHash == null)
				{
					return null;
				}

				string relLib = Path.GetRelativePath(binRoot, libPath).Replace('\\', '/');
				body.Append("lib ").Append(libHash).Append(' ').Append(libName).Append(' ').Append(relLib).Append('\n');
			}

			string content = body.ToString();
			string targetPath = GetModuleInputsPath(title);
			string tempPath = targetPath + ".tmp";
			File.WriteAllText(tempPath, content);
			File.Move(tempPath, targetPath, overwrite: true);
			return CompileCache.HashText(content);
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static bool ModuleInputsHold(Refii.Core.Engine_Title title, string expectedDigest)
	{
		try
		{
			PurgeStaleRecompileArtifacts(RecompileDirectory);
			string path = GetModuleInputsPath(title);
			if (!File.Exists(path))
			{
				return false;
			}

			string content = File.ReadAllText(path);
			if (!string.Equals(CompileCache.HashText(content), expectedDigest, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			string recompileRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RecompileDirectory));
			string binRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(BinDirectory));
			string expectedNames = string.Empty;
			var tracked = new List<string>();
			int fileCount = 0;

			foreach (string line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
			{
				if (line.StartsWith("env ", StringComparison.Ordinal))
				{
					if (line[4..] != DesktopEnvironmentRecipe(title))
					{
						return false;
					}
				}
				else if (line.StartsWith("names ", StringComparison.Ordinal))
				{
					expectedNames = line[6..];
				}
				else if (line.StartsWith("tracked ", StringComparison.Ordinal))
				{
					tracked.Add(line[8..]);
				}
				else if (line.StartsWith("file ", StringComparison.Ordinal))
				{
					int secondSpace = line.IndexOf(' ', 5);
					if (secondSpace <= 5)
					{
						return false;
					}

					string recordedHash = line[5..secondSpace];
					string relPath = line[(secondSpace + 1)..];
					string diskPath = Path.Combine(recompileRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
					if (CompileCache.HashFileMemoized(diskPath) != recordedHash)
					{
						return false;
					}

					fileCount++;
				}
				else if (line.StartsWith("lib ", StringComparison.Ordinal))
				{
					string[] parts = line.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
					if (parts.Length != 4)
					{
						return false;
					}

					string recordedHash = parts[1];
					string libName = parts[2];
					string expectedRelLib = parts[3];
					string currentLib = FindShippedLibrary(title, libName);
					if (currentLib.Length == 0)
					{
						return false;
					}

					string currentRelLib = Path.GetRelativePath(binRoot, currentLib).Replace('\\', '/');
					if (!string.Equals(currentRelLib, expectedRelLib, StringComparison.Ordinal) ||
						CompileCache.HashFileMemoized(currentLib) != recordedHash)
					{
						return false;
					}
				}
				else
				{
					return false;
				}
			}

			if (fileCount == 0 || expectedNames.Length == 0)
			{
				return false;
			}

			List<string> includeDirs = ReadCMakeList(
				Path.Combine(RecompileDirectory, "include_dirs.cmake"), "REFII_PAYLOAD_INCLUDE_DIRS");
			string currentNames = CompileCache.SameNames(
				tracked,
				CompileCache.FileNameIndex(EnumerateRecompileRelativePaths()),
				GetRelativeIncludeDirectories(includeDirs));
			return currentNames == expectedNames;
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static readonly SemaphoreSlim WindowsInstallInputsGate = new(1, 1);

	private readonly record struct WindowsBootstrapTools(
		string CMake, string Ninja, string CCompiler, string CxxCompiler,
		IReadOnlyList<string> SearchDirectories);

	private static bool WindowsInstallInputsMissing()
	{
		string recompile = RecompileDirectory;

		return !File.Exists(Path.Combine(TitleBinDirectory,
				Refii.Core.Engine_NativeLibraries.FileNameFor("refii_hostbridge"))) ||
			!File.Exists(Path.Combine(BinDirectory,
				Refii.Core.Engine_NativeLibraries.FileNameFor("refii_core"))) ||
			!File.Exists(Path.Combine(BinDirectory,
				Refii.Core.Engine_NativeLibraries.FileNameFor("refii_runtime"))) ||
			!File.Exists(Path.Combine(BinDirectory, "Fable2",
				Refii.Core.Engine_NativeLibraries.FileNameFor("refii_vfs"))) ||
			!File.Exists(Path.Combine(BinDirectory, "Fable3",
				Refii.Core.Engine_NativeLibraries.FileNameFor("refii_hostbridge"))) ||
			!File.Exists(Path.Combine(BinDirectory, "Fable3",
				Refii.Core.Engine_NativeLibraries.FileNameFor("refii_vfs"))) ||
			!File.Exists(Path.Combine(BinDirectory,
				Refii.Core.Engine_NativeLibraries.FileNameFor("refii_binkdecode"))) ||
			!File.Exists(Path.Combine(BinDirectory,
				Refii.Core.Engine_NativeLibraries.FileNameFor("refii_xmadecode"))) ||
			!AnyFileExists(BinDirectory,
				"rexruntime.dll", "rexruntimed.dll", "rexruntimerd.dll",
				"librexruntime.dll", "librexruntimed.dll", "librexruntimerd.dll") ||
			!File.Exists(Path.Combine(BinDirectory,
				Refii.Core.Engine_NativeLibraries.FileNameFor("refii_recompiler"))) ||
			FindShippedLibrary("rexruntime").Length == 0 ||
			FindShippedLibrary("refii_core").Length == 0 ||
			FindShippedLibrary("refii_runtime").Length == 0 ||
			FindLibrary(Path.Combine(BinDirectory, "Fable2"), "refii_vfs").Length == 0 ||
			FindLibrary(Path.Combine(BinDirectory, "Fable3"), "refii_vfs").Length == 0 ||
			!DirectoryHasFiles(Path.Combine(recompile, "Fable2", "hooks")) ||
			!DirectoryHasFiles(Path.Combine(recompile, "Fable3", "hooks")) ||
			!DirectoryHasFiles(Path.Combine(recompile, "include")) ||
			!File.Exists(Path.Combine(recompile, "include_dirs.cmake")) ||
			!File.Exists(Path.Combine(recompile, "Fable2", "hook_sources.cmake")) ||
			!File.Exists(Path.Combine(recompile, "Fable3", "hook_sources.cmake")) ||
			!File.Exists(Path.Combine(recompile, "payload_core_sources.cmake")) ||
			!File.Exists(VersionManifestPath) ||
			!File.Exists(VersionConfigPath) ||
			!File.Exists(Path.Combine(recompile, "src", "payload_export.cpp")) ||
			!File.Exists(Path.Combine(recompile, "zig", "zig.exe")) ||
			!Directory.Exists(Path.Combine(recompile, "zig", "lib"));
	}

	private static bool WindowsInstallInputsModeEnabled(string buildDirectory)
	{
		try
		{
			string cache = Path.Combine(buildDirectory, "CMakeCache.txt");
			foreach (string line in File.ReadLines(cache))
			{
				if (!line.StartsWith("REFII_WINDOWS_INSTALL_INPUTS_ONLY:",
						StringComparison.Ordinal))
				{
					continue;
				}

				int separator = line.IndexOf('=');
				return separator >= 0 && string.Equals(
					line[(separator + 1)..].Trim(), "ON", StringComparison.OrdinalIgnoreCase);
			}
		}
		catch (Exception)
		{
		}

		return false;
	}

	private static bool AnyFileExists(string directory, params string[] names)
	{
		foreach (string name in names)
		{
			if (File.Exists(Path.Combine(directory, name)))
			{
				return true;
			}
		}
		return false;
	}

	private static bool DirectoryHasFiles(string directory)
	{
		try
		{
			return Directory.Exists(directory) && Directory.EnumerateFiles(
				directory, "*", SearchOption.AllDirectories).Any();
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static async Task<Result> EnsureWindowsInstallInputsAsync(
		Action<Progress> progress, CancellationToken cancellation)
	{
		bool acquired = false;
		try
		{
			await WindowsInstallInputsGate.WaitAsync(cancellation).ConfigureAwait(false);
			acquired = true;
			if (!WindowsInstallInputsMissing())
			{
				return new Result(true, string.Empty);
			}

			if (!TryResolveWindowsSourceRoot(out string sourceRoot, out string sourceError))
			{
				return new Result(false, sourceError);
			}

			if (!TryResolveWindowsBootstrapTools(out WindowsBootstrapTools tools,
					out string toolsError))
			{
				return new Result(false, toolsError);
			}

			string arch = Refii.Core.Engine_AppRoot.ArchFolder;
			string buildDirectory = Path.Combine(
				sourceRoot, "out", "build", "windows-godot-install-inputs-" + arch);

			var configureArguments = new List<string>
			{
				"-S", sourceRoot,
				"-B", buildDirectory,
				"-G", "Ninja Multi-Config",
				"-DCMAKE_MAKE_PROGRAM=" + tools.Ninja,
				"-DCMAKE_C_COMPILER=" + tools.CCompiler,
				"-DCMAKE_CXX_COMPILER=" + tools.CxxCompiler,
				"-DCMAKE_CONFIGURATION_TYPES=RelWithDebInfo",
				"-DREFII_WINDOWS_INSTALL_INPUTS_ONLY=ON",
				"-DREFII_PACKAGE=OFF",
			};
			if (string.Equals(arch, "x86_64", StringComparison.OrdinalIgnoreCase))
			{
				// The same target as the shipped Windows libraries (windows-base in
				// CMakePresets.json): x86-64-v2, so a CPU without AVX2 or FMA3 runs them.
				configureArguments.Add("-DCMAKE_C_FLAGS=-march=x86-64-v2");
				configureArguments.Add("-DCMAKE_CXX_FLAGS=-march=x86-64-v2");
			}

			LogPhase("prepare-windows-install-inputs");
			progress(new Progress("Preparing the Windows game-code components...", 0.005f));
			Result configured = default;
			for (int attempt = 0; attempt < 2; attempt++)
			{
				configured = await RunStepAsync(
					tools.CMake,
					configureArguments,
					sourceRoot,
					line => ReportBootstrapLine(
						progress, "Configuring Windows components", line, 0.008f),
					cancellation,
					tools.SearchDirectories).ConfigureAwait(false);
				if (!configured.Ok)
				{
					return new Result(false,
						"Could not configure the Windows game-code components automatically.\n\n" +
						configured.Error);
				}

				if (WindowsInstallInputsModeEnabled(buildDirectory))
				{
					break;
				}

				progress(new Progress(
					"Finalizing the Windows game-code configuration...", 0.009f));
			}
			if (!WindowsInstallInputsModeEnabled(buildDirectory))
			{
				return new Result(false,
					"CMake did not retain the Windows install-input build mode in " +
					Path.Combine(buildDirectory, "CMakeCache.txt") + ".");
			}

			var buildArguments = new List<string>
			{
				"--build", buildDirectory,
				"--config", "RelWithDebInfo",
				"--target", "refii_windows_install_inputs",
				"--parallel", JobCount().ToString(),
			};
			progress(new Progress("Building the Windows game-code components...", 0.01f));
			Result built = await RunStepAsync(
				tools.CMake,
				buildArguments,
				sourceRoot,
				line => ReportBootstrapLine(progress, "Building Windows components", line, 0.015f),
				cancellation,
				tools.SearchDirectories).ConfigureAwait(false);
			if (!built.Ok)
			{
				return new Result(false,
					"Could not build the Windows game-code components automatically.\n\n" +
					built.Error);
			}

			if (WindowsInstallInputsMissing())
			{
				return new Result(false,
					"The Windows component build finished, but the install-time game-code " +
					"inputs were not completely staged at " + RecompileDirectory + ".");
			}

			return new Result(true, string.Empty);
		}
		catch (OperationCanceledException)
		{
			return new Result(false, "cancelled");
		}
		finally
		{
			if (acquired)
			{
				WindowsInstallInputsGate.Release();
			}
		}
	}

	private static void ReportBootstrapLine(
		Action<Progress> progress, string phase, string line, float fraction)
	{
		string status = line.Trim();
		if (status.Length == 0)
		{
			return;
		}
		if (status.Length > 180)
		{
			status = status[..177] + "...";
		}

		progress(new Progress(phase + "...\n" + status, fraction));
	}

	private static bool TryResolveWindowsSourceRoot(out string sourceRoot, out string error)
	{
		sourceRoot = string.Empty;
		error = string.Empty;

		try
		{
			string projectDirectory = Path.TrimEndingDirectorySeparator(
				Path.GetFullPath(Godot.ProjectSettings.GlobalizePath("res://")));
			string? parent = Directory.GetParent(projectDirectory)?.FullName;
			if (string.IsNullOrEmpty(parent))
			{
				error = "Could not resolve the source checkout above the Godot project.";
				return false;
			}

			string expectedBin = Path.Combine(
				projectDirectory, "build", Refii.Core.Engine_AppRoot.ArchFolder, "bin");
			if (!string.Equals(Path.GetFullPath(BinDirectory), Path.GetFullPath(expectedBin),
					StringComparison.OrdinalIgnoreCase))
			{
				error = "The Windows install inputs are missing, but this editor run is not " +
					"using the source checkout's Godot/build staging directory.";
				return false;
			}

			foreach (string required in new[]
			         {
				         "CMakeLists.txt", "CMakePresets.json",
				         Path.Combine("cmake", "refii_finalize_windows_install_inputs.cmake"),
				         Path.Combine("rexglue-net", "rexglue-sdk", "CMakeLists.txt"),
				         Path.Combine("src", "extractor", "thirdparty", "zlib", "CMakeLists.txt"),
			         })
			{
				if (!File.Exists(Path.Combine(parent!, required)))
				{
					error = "This clean Windows run needs the complete refii source checkout; " +
						"missing " + Path.Combine(parent!, required) + ".";
					return false;
				}
			}

			sourceRoot = parent!;
			return true;
		}
		catch (Exception exception)
		{
			error = "Could not resolve the Windows source checkout: " + exception.Message;
			return false;
		}
	}

	private static bool TryResolveWindowsBootstrapTools(
		out WindowsBootstrapTools tools, out string error)
	{
		tools = default;
		error = string.Empty;

		var visualStudioRoots = FindVisualStudioRoots();
		string cmake = FindOnPath("cmake.exe");
		if (cmake.Length == 0)
		{
			foreach (string root in visualStudioRoots)
			{
				string candidate = Path.Combine(root, "Common7", "IDE", "CommonExtensions",
					"Microsoft", "CMake", "CMake", "bin", "cmake.exe");
				if (File.Exists(candidate))
				{
					cmake = candidate;
					break;
				}
			}
		}

		string ninja = FindOnPath("ninja.exe");
		if (ninja.Length == 0 && cmake.Length != 0)
		{
			string? cmakeDirectory = Path.GetDirectoryName(cmake);
			if (!string.IsNullOrEmpty(cmakeDirectory))
			{
				string candidate = Path.GetFullPath(
					Path.Combine(cmakeDirectory!, "..", "..", "Ninja", "ninja.exe"));
				if (File.Exists(candidate))
				{
					ninja = candidate;
				}
			}
		}
		if (ninja.Length == 0)
		{
			foreach (string root in visualStudioRoots)
			{
				string candidate = Path.Combine(root, "Common7", "IDE", "CommonExtensions",
					"Microsoft", "CMake", "Ninja", "ninja.exe");
				if (File.Exists(candidate))
				{
					ninja = candidate;
					break;
				}
			}
		}

		var compilerDirectories = new List<string>();
		foreach (string root in visualStudioRoots)
		{
			compilerDirectories.Add(Path.Combine(root, "VC", "Tools", "Llvm", "x64", "bin"));
			compilerDirectories.Add(Path.Combine(root, "VC", "Tools", "Llvm", "bin"));
		}
		string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
		if (programFiles.Length != 0)
		{
			compilerDirectories.Add(Path.Combine(programFiles, "LLVM", "bin"));
		}

		string clang = string.Empty;
		string clangxx = string.Empty;
		foreach (string directory in compilerDirectories)
		{
			string candidateC = Path.Combine(directory, "clang.exe");
			string candidateCxx = Path.Combine(directory, "clang++.exe");
			if (File.Exists(candidateC) && File.Exists(candidateCxx))
			{
				clang = candidateC;
				clangxx = candidateCxx;
				break;
			}
		}
		if (clang.Length == 0)
		{
			string pathClang = FindOnPath("clang.exe");
			string pathClangxx = FindOnPath("clang++.exe");
			if (!pathClang.Contains("mingw", StringComparison.OrdinalIgnoreCase) &&
					!pathClangxx.Contains("mingw", StringComparison.OrdinalIgnoreCase))
			{
				clang = pathClang;
				clangxx = pathClangxx;
			}
		}

		var missing = new List<string>();
		if (cmake.Length == 0) missing.Add("CMake");
		if (ninja.Length == 0) missing.Add("Ninja");
		if (clang.Length == 0 || clangxx.Length == 0) missing.Add("MSVC-compatible Clang");
		if (missing.Count != 0)
		{
			error = "The clean Windows checkout needs " + string.Join(", ", missing) +
				" to prepare its native game-code components, and they could not be found. " +
				"Install the Visual Studio C++ CMake tools and LLVM, then run the build again.";
			return false;
		}

		var searchDirectories = new List<string>();
		foreach (string executable in new[] { cmake, ninja, clang, clangxx })
		{
			string? directory = Path.GetDirectoryName(executable);
			if (!string.IsNullOrEmpty(directory) &&
					!searchDirectories.Exists(item => string.Equals(
						item, directory, StringComparison.OrdinalIgnoreCase)))
			{
				searchDirectories.Add(directory!);
			}
		}

		tools = new WindowsBootstrapTools(cmake, ninja, clang, clangxx, searchDirectories);
		return true;
	}

	private static List<string> FindVisualStudioRoots()
	{
		var result = new List<string>();
		var programRoots = new List<string>
		{
			Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
			Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
		};

		foreach (string programRoot in programRoots)
		{
			if (programRoot.Length == 0)
			{
				continue;
			}
			string visualStudio = Path.Combine(programRoot, "Microsoft Visual Studio");
			foreach (string version in SafeEnumerateDirectories(visualStudio))
			{
				foreach (string edition in SafeEnumerateDirectories(version))
				{
					if (!result.Exists(item => string.Equals(
							item, edition, StringComparison.OrdinalIgnoreCase)))
					{
						result.Add(edition);
					}
				}
			}
		}

		return result;
	}

	private static IReadOnlyList<string> SafeEnumerateDirectories(string directory)
	{
		try
		{
			return Directory.Exists(directory)
				? Directory.GetDirectories(directory)
				: Array.Empty<string>();
		}
		catch (Exception)
		{
			return Array.Empty<string>();
		}
	}

	private static string FindOnPath(string executable)
	{
		string searchPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
		foreach (string rawDirectory in searchPath.Split(Path.PathSeparator))
		{
			string directory = rawDirectory.Trim().Trim('"');
			if (directory.Length == 0)
			{
				continue;
			}
			try
			{
				string candidate = Path.Combine(directory, executable);
				if (File.Exists(candidate))
				{
					return Path.GetFullPath(candidate);
				}
			}
			catch (Exception)
			{
			}
		}

		return string.Empty;
	}

	/// <summary>
	/// Runs the whole pipeline for the active title. Reports progress as a status line and a 0..1 fraction.
	/// </summary>
	/// <param name="progress">Progress callback receiving status text and completion fraction.</param>
	/// <param name="control">Pause/stop controller for the build.</param>
	/// <returns>Outcome of the build.</returns>
	public static Task<Result> RunAsync(Action<Progress> progress, BuildControl control) =>
		RunAsync(Refii.Core.Engine_Title.Active, progress, control);

	/// <summary>
	/// Runs the whole pipeline for <paramref name="title"/>. Reports progress as a status line and a 0..1 fraction.
	/// </summary>
	/// <remarks>
	/// <paramref name="control"/> is where a pause holds the build and a stop abandons it; see
	/// <see cref="BuildControl"/> for exactly where each takes effect. A stop comes back as a
	/// <see cref="Result"/> with <c>Stopped</c> set, never as an error: ephemeral scratch output is
	/// deleted here while cached codegen and already-compiled objects remain intact so the next build
	/// resumes cleanly from where this one stopped.
	/// </remarks>
	public static async Task<Result> RunAsync(
		Refii.Core.Engine_Title title, Action<Progress> progress, BuildControl control)
	{
		CancellationToken cancellation = control.StopToken;
		Result result;
		try
		{
			result = await RunPipelineAsync(title, progress, control, cancellation).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			result = StoppedResult;
		}

		// A step that fails because its process was killed reports the kill as its error; the
		// token says which it was.
		if (result.Ok || (!result.Stopped && !cancellation.IsCancellationRequested))
		{
			return result;
		}

		LogPhase("stopped");
		progress(new Progress("Game code build stopped.", 0.0f));

		// Remove only ephemeral per-build scratch directories (mods/ and work/), preserving
		// verified generated sources and cached objects so the next run picks up immediately.
		CleanScratchDirectoriesBestEffort(title);

		InvalidateStatus();
		return StoppedResult;
	}

	private static void CleanScratchDirectoriesBestEffort(Refii.Core.Engine_Title title)
	{
		foreach (string dir in new[] { GetModsStagingDirectory(title), GetWorkDirectory(title) })
		{
			try
			{
				if (Directory.Exists(dir))
				{
					Directory.Delete(dir, recursive: true);
				}
			}
			catch (Exception exception)
			{
				LogWrite("could not remove scratch directory " + dir + ": " + exception.Message);
			}
		}
	}

	private static async Task<Result> RunPipelineAsync(
		Refii.Core.Engine_Title title,
		Action<Progress> progress,
		BuildControl control,
		CancellationToken cancellation)
	{
		if (BinDirectory.Length == 0)
		{
			return new Result(false, "The native library directory has not been resolved.");
		}

		string gameExecutablePath = GetGameExecutablePath(title);
		if (!File.Exists(gameExecutablePath))
		{
			return new Result(false,
				"No game executable at " + gameExecutablePath +
				". Install the game before building its code.");
		}

		if (OperatingSystem.IsWindows() &&
			!Refii.Core.Engine_AppRoot.IsExported &&
			ActiveDriver?.SkipWindowsBootstrap != true)
		{
			Result prepared = await EnsureWindowsInstallInputsAsync(
				progress, cancellation).ConfigureAwait(false);
			if (!prepared.Ok)
			{
				return prepared;
			}
		}

		// EVERYTHING missing, in one message. Reported one at a time this takes a build
		// attempt per fault to discover, and each attempt looks like "it just doesn't work".
		string generator = Path.Combine(BinDirectory,
			Refii.Core.Engine_NativeLibraries.FileNameFor("refii_recompiler"));

		var missing = new List<string>();
		if (!Directory.Exists(RecompileDirectory))
		{
			missing.Add("the build inputs directory (" + RecompileDirectory + ")");
		}
		else
		{
			if (!File.Exists(generator))
			{
				missing.Add("the code generator (" + generator + ")");
			}

			if (FindShippedLibrary(title, "rexruntime").Length == 0)
			{
				missing.Add("the link libraries (" + BinDirectory + ")");
			}

			if (!File.Exists(Path.Combine(RecompileDirectory, "include_dirs.cmake")))
			{
				missing.Add("the staged headers (" + RecompileDirectory + "/include)");
			}
		}

		if (missing.Count > 0)
		{
			return new Result(false,
				"This install cannot build the game code because it is missing " +
				string.Join("; ", missing) +
				". These ship beside the app under bin/ — if you copied the " +
				"executable on its own, copy the whole exported folder instead.");
		}

		Toolchain resolved;
		string toolchainError;
		if (ActiveDriver?.ResolveToolchain is { } customToolchain)
		{
			var tcResult = customToolchain();
			if (!tcResult.Ok)
			{
				return new Result(false, tcResult.Error);
			}

			resolved = tcResult.Toolchain;
		}
		else if (!Toolchain.TryResolve(out resolved, out toolchainError))
		{
			return new Result(false, toolchainError);
		}

		using IDisposable logScope = BeginLogScope();

		// 0. PREPARE SCRATCH & REMOVE PREVIOUS OUTPUT MODULE.
		//    Persistent caches (generated/, objects/, zig-cache/) live under cache/<Folder>/compile
		//    and are verified by content SHA-256; only ephemeral per-build folders (mods/, work/)
		//    and any pre-cache legacy assets/<Folder>/generated directory are cleared up front.
		LogPhase("prepare");
		progress(new Progress("Preparing the game code build...", 0.02f));
		PurgeStaleRecompileArtifacts(RecompileDirectory);

		try
		{
			if (ActiveDriver == null)
			{
				string legacyGenerated = Installer.GetGeneratedCodeDirectoryPath(title);
				if (Directory.Exists(legacyGenerated))
				{
					Directory.Delete(legacyGenerated, recursive: true);
				}
			}
		}
		catch (Exception)
		{
		}

		string generatedDir = GetGeneratedDirectory(title);
		string objectCacheDir = GetObjectCacheDirectory(title);
		string zigCacheDir = GetZigCacheDirectory(title);
		string modsStagingDir = GetModsStagingDirectory(title);
		string workDir = GetWorkDirectory(title);
		string modulePath = GetModulePath(title);
		string moduleInputsPath = GetModuleInputsPath(title);

		foreach (string ephemeralDir in new[] { modsStagingDir, workDir })
		{
			try
			{
				if (Directory.Exists(ephemeralDir))
				{
					Directory.Delete(ephemeralDir, recursive: true);
				}
			}
			catch (Exception exception)
			{
				return new Result(false,
					"Could not clear temporary build directory at " + ephemeralDir +
					": " + exception.Message +
					"\nClose anything reading that folder and try again.");
			}
		}

		// The module itself is output too. A failed build must not leave the previous one in
		// place looking current, and IsBuilt()/NeedsBuild() key off exactly this file.
		try
		{
			if (File.Exists(modulePath))
			{
				File.Delete(modulePath);
			}

			if (File.Exists(moduleInputsPath))
			{
				File.Delete(moduleInputsPath);
			}
		}
		catch (Exception exception)
		{
			return new Result(false,
				"Could not remove the previous game module at " + modulePath + ": " + exception.Message);
		}

		InvalidateStatus();

		try
		{
			Directory.CreateDirectory(generatedDir);
			Directory.CreateDirectory(objectCacheDir);
			Directory.CreateDirectory(zigCacheDir);
			Directory.CreateDirectory(workDir);
		}
		catch (Exception exception)
		{
			return new Result(false, "Could not create build cache directories: " + exception.Message);
		}

		// 1. CODEGEN (skipped via CodegenCache when XEX, XEXP, manifest/configs, and generator match).
		string sourceManifest = GetVersionManifestPath(title);
		if (!File.Exists(sourceManifest))
		{
			return new Result(false,
				"The code generation manifest for " + GetGameVersionName(title) +
				" is missing from this install (" + sourceManifest + ").");
		}

		string gameHash = HashGameExecutable(title);
		if (gameHash.Length == 0)
		{
			return new Result(false, "The installed game executable could not be read.");
		}

		var tomlInputs = new List<(string Name, byte[] Bytes)>();
		try
		{
			string versionDir = GetVersionDirectory(title);
			if (Directory.Exists(versionDir))
			{
				foreach (string tomlFile in Directory.EnumerateFiles(versionDir, "*", SearchOption.AllDirectories))
				{
					string relName = Path.GetRelativePath(versionDir, tomlFile).Replace('\\', '/');
					tomlInputs.Add((relName, File.ReadAllBytes(tomlFile)));
				}
			}
		}
		catch (Exception exception)
		{
			return new Result(false, "Could not read version configuration files: " + exception.Message);
		}

		string effectiveManifestName = title.Key + "_manifest.effective.toml";
		var ignoredGeneratedFiles = new HashSet<string>(StringComparer.Ordinal) { effectiveManifestName };
		string codegenDigest = CodegenCache.ComputeInputDigest(
			gameHash, tomlInputs, GeneratorIdentity(), GetSdkVersion());

		LogPhase("generate");
		if (CodegenCache.TryReuse(generatedDir, codegenDigest, ignoredGeneratedFiles))
		{
			LogWrite("codegen cache hit (" + codegenDigest[..16] + ")");
			progress(new Progress("Reusing cached generated game code...", 0.30f));
		}
		else
		{
			try
			{
				if (Directory.Exists(generatedDir))
				{
					Directory.Delete(generatedDir, recursive: true);
				}

				Directory.CreateDirectory(generatedDir);
			}
			catch (Exception exception)
			{
				return new Result(false, "Could not reset " + generatedDir + ": " + exception.Message);
			}

			Result lastGenFailure = default;
			string manifestSetupError = string.Empty;
			string partitionSeedPath = Path.Combine(objectCacheDir, CodegenTransforms.PartitionFile);

			try
			{
				await CodegenTransforms.RunWithPartitionSeedAsync(
					objectCacheDir,
					generatedDir,
					async () =>
					{
						Directory.CreateDirectory(generatedDir);
						string manifest = WriteEffectiveManifest(title, out string manifestError);
						if (manifest.Length == 0)
						{
							manifestSetupError = manifestError;
							return;
						}

						bool seededRun = File.Exists(partitionSeedPath);
						Result generated = await Task.Run(
							() => ActiveDriver?.GenerateCode is { } customGen
								? customGen(
									manifest,
									control,
									line => progress(new Progress("Generating game code... " + line, 0.05f)),
									LogWrite)
								: Recompiler.Generate(
									manifest,
									control,
									line => progress(new Progress("Generating game code... " + line, 0.05f)),
									LogWrite),
							cancellation).ConfigureAwait(false);

						if (generated.Stopped || cancellation.IsCancellationRequested)
						{
							throw new OperationCanceledException(cancellation);
						}

						if (!generated.Ok)
						{
							lastGenFailure = generated;
							if (seededRun)
							{
								throw new InvalidDataException(generated.Error);
							}
						}
					},
					prepareBeforeRetry: () =>
					{
						lastGenFailure = default;
						Directory.CreateDirectory(generatedDir);
					}).ConfigureAwait(false);
			}
			catch (InvalidDataException)
			{
				// Clean retry also failed; lastGenFailure holds the underlying generator error.
			}

			if (manifestSetupError.Length > 0)
			{
				return new Result(false, manifestSetupError);
			}

			if (!lastGenFailure.Ok && lastGenFailure.Error != null && lastGenFailure.Error.Length > 0)
			{
				LogWrite("generate failed: " + lastGenFailure.Error);
				return new Result(false, "Code generation failed: " + lastGenFailure.Error);
			}

			if (!File.Exists(Path.Combine(generatedDir, "sources.cmake")))
			{
				return new Result(false,
					"The generator finished without producing sources.cmake in " + generatedDir + ".");
			}

			// Apply shared post-codegen transforms (thread-local interrupt lock depth + chunked SetFunction registration).
			ApplyPostCodegenTransforms(generatedDir);
			CodegenCache.Record(generatedDir, codegenDigest, ignoredGeneratedFiles);
		}

		string sourcesCmake = Path.Combine(generatedDir, "sources.cmake");
		if (!File.Exists(sourcesCmake))
		{
			return new Result(false,
				"The generator finished without producing sources.cmake in " + generatedDir + ".");
		}

		List<string> sources;
		try
		{
			sources = CodegenTransforms.ReadGeneratedSources(sourcesCmake, generatedDir).ToList();
		}
		catch (InvalidDataException)
		{
			sources = ReadCMakeList(sourcesCmake, "GENERATED_SOURCES");
		}

		if (sources.Count == 0)
		{
			return new Result(false,
				"The generator produced sources.cmake but it lists no translation units.");
		}

		// The host ABI entry point, the one hand-written TU in the module. Compiled here
		// exactly as our own build compiles it.
		string payloadExport = Path.Combine(RecompileDirectory, "src", "payload_export.cpp");
		if (!File.Exists(payloadExport))
		{
			return new Result(false,
				"The payload export entry point is missing (" + payloadExport + ").");
		}

		sources.Add(payloadExport);

		// THE PAYLOAD CORE, every title's: the rex:: bridge to the host runtime and the guest
		// yield backoff. Compiled like the hooks, by the player's compiler, into the module.
		List<string> coreSources = ReadCMakeList(
			Path.Combine(RecompileDirectory, "payload_core_sources.cmake"), "REFII_PAYLOAD_CORE_SOURCES");
		if (coreSources.Count == 0)
		{
			return new Result(false,
				"The payload core sources are missing from this install (" + RecompileDirectory +
				"/payload_core_sources.cmake). Copy the whole extracted folder, not just the executable.");
		}

		// THE HOOKS ARE COMPILED, NOT LINKED. Every title has its own, staged under bin/recompile/<Folder>.
		string hookSourcesPath = GetHookSourcesPath(title);
		List<string> hookSources = ReadCMakeList(hookSourcesPath, "REFII_HOOK_SOURCES");
		if (hookSources.Count == 0)
		{
			return new Result(false,
				"The hook sources are missing from this install (" + hookSourcesPath +
				"). Copy the whole extracted folder, not just the executable.");
		}

		// EVERY LISTED SOURCE MUST BE ON DISK.
		var absentSources = new List<string>();
		foreach (string source in coreSources.Concat(hookSources))
		{
			if (!File.Exists(source))
			{
				absentSources.Add(Path.GetRelativePath(RecompileDirectory, source).Replace('\\', '/'));
			}
		}

		if (absentSources.Count > 0)
		{
			return new Result(false,
				"This install is missing " + absentSources.Count + " of the game code's source files: " +
				string.Join(", ", absentSources) + ". The download was not fully extracted — extract " +
				"the whole zip again over this folder, or reinstall, then build again.");
		}

		sources.AddRange(coreSources);
		sources.AddRange(hookSources);

		// HARD MODS: their native sources compile in this same step, after refii's hooks, with the
		// same compiler and flags. The link takes every object by index, so it needs nothing more.
		var modArguments = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		Result modded = AddNativeModSources(title, sources, modArguments, hookSources, out string compiledModsHash);
		if (!modded.Ok)
		{
			return modded;
		}

		List<string> includeDirectories = ReadCMakeList(
			Path.Combine(RecompileDirectory, "include_dirs.cmake"), "REFII_PAYLOAD_INCLUDE_DIRS");
		if (includeDirectories.Count == 0)
		{
			return new Result(false,
				"The staged include list (" + RecompileDirectory + "/include_dirs.cmake) is empty.");
		}

		List<string> flags = CompileFlags(title, includeDirectories);
		flags.AddRange(StandardLibraryFlags(resolved));
		flags.AddRange(MacroPrefixFlags(title));

		// Build content-addressed CompileCache over ordered roots (nested toolchain/ before parent recompile/).
		var inputRoots = new List<CompileInputRoot>();
		if (!string.IsNullOrEmpty(resolved.Root) && Directory.Exists(resolved.Root))
		{
			inputRoots.Add(CompileInputRoot.ImmutableToolchain("toolchain/", resolved.Root));
		}

		// The host's own headers (zig's native glibc discovery) change with the system, not with zig, so they are
		// hashed by content: a distribution update to them is a miss, never a stale object.
		if (!OperatingSystem.IsWindows())
		{
			foreach (var (prefix, sysDir) in new[]
			{
				("sysusrlocal/", "/usr/local/include"),
				("sysusr/", "/usr/include"),
				("sysusrlib/", "/usr/lib"),
			})
			{
				if (Directory.Exists(sysDir))
				{
					inputRoots.Add(CompileInputRoot.DiskHashed(prefix, sysDir));
				}
			}
		}

		inputRoots.Add(CompileInputRoot.DiskHashed("recompile/", RecompileDirectory, trackInputsAndShadowing: true));
		inputRoots.Add(CompileInputRoot.DiskHashed("mods/", modsStagingDir, trackInputsAndShadowing: false));
		inputRoots.Add(CompileInputRoot.DiskHashed("generated/", generatedDir, trackInputsAndShadowing: false));

		List<string> allRecompileRelativePaths = EnumerateRecompileRelativePaths();
		List<string> relativeIncludeDirs = GetRelativeIncludeDirectories(includeDirectories);
		string compileRecipe = DesktopCompileRecipe(title, ToolchainBinaryIdentity(resolved));
		var cache = new CompileCache(
			objectCacheDir,
			inputRoots,
			allRecompileRelativePaths,
			relativeIncludeDirs,
			compileRecipe,
			CompilerWorkingDirectory);

		// Determine per-unit raw and normalized extra flags up front so we know which units miss the cache
		// and whether any cache-miss unit needs the precompiled header.
		var rawExtraFlags = new IReadOnlyList<string>[sources.Count];
		var normalizedExtraFlags = new IReadOnlyList<string>[sources.Count];
		for (int i = 0; i < sources.Count; i++)
		{
			List<string>? unitFlags = null;
			if (Path.GetFileName(sources[i]) == "rex_bridge_payload.cpp")
			{
				unitFlags = new List<string> { "-fvisibility=hidden" };
			}

			if (modArguments.TryGetValue(sources[i], out List<string>? extra) && extra.Count > 0)
			{
				unitFlags ??= new List<string>();
				unitFlags.AddRange(extra);
			}

			if (unitFlags == null || unitFlags.Count == 0)
			{
				rawExtraFlags[i] = Array.Empty<string>();
				normalizedExtraFlags[i] = Array.Empty<string>();
			}
			else
			{
				rawExtraFlags[i] = unitFlags;
				normalizedExtraFlags[i] = NormalizeExtraFlagsForCache(unitFlags, modsStagingDir);
			}
		}

		var objectPaths = new string[sources.Count];
		var missIndices = new List<int>();
		for (int i = 0; i < sources.Count; i++)
		{
			if (cache.Find(sources[i], normalizedExtraFlags[i]) is { } hit)
			{
				objectPaths[i] = hit;
			}
			else
			{
				missIndices.Add(i);
			}
		}

		// What this build reuses: the units the content-addressed cache already holds, and the ones it compiles.
		LogWrite($"compile cache: {sources.Count - missIndices.Count} of {sources.Count} units cached, {missIndices.Count} to compile");

		// 2. Precompile (or load cached) generated header only when at least one PCH-eligible unit missed cache.
		//    Units with per-unit extra flags (-fvisibility=hidden, mod -I/-D) skip -include-pch so Clang never
		//    rejects the PCH due to macro or visibility differences.
		string header = Path.Combine(generatedDir, "refii_pch.h");
		if (!File.Exists(header))
		{
			header = Path.Combine(generatedDir, "refii_init.h");
		}

		bool needsPch = File.Exists(header) && missIndices.Any(index => rawExtraFlags[index].Count == 0);
		string pchUsed = string.Empty;

		if (needsPch)
		{
			await control.WaitWhilePausedAsync(cancellation).ConfigureAwait(false);
			LogPhase("precompile");
			progress(new Progress("Preparing the game code build...", 0.36f));

			string pchPathSalt = Path.GetFullPath(generatedDir) + "\0" + Path.GetFullPath(RecompileDirectory);
			if (cache.FindPchArtifact(header, pchPathSalt) is { } cachedPch)
			{
				pchUsed = cachedPch;
				LogWrite("pch cache hit: " + Path.GetFileName(cachedPch));
			}
			else
			{
				string pchWorkPath = Path.Combine(workDir, Path.GetFileNameWithoutExtension(header) + ".pch");
				string pchDepPath = pchWorkPath + ".d";
				var pchArguments = new List<string>(flags)
				{
					"-MD", "-MF", pchDepPath,
					"-Xclang", "-fno-pch-timestamp",
					"-x", "c++-header", header, "-o", pchWorkPath,
				};

				(int pchCode, string pchOutput) = await RunCaptureAsync(
					title, resolved.Zig, Prefixed("c++", pchArguments), control, cancellation).ConfigureAwait(false);

				// NOT FATAL. A precompiled header is a speed optimisation, and the TUs include the
				// header themselves regardless — so if clang refuses it, the right outcome is a
				// slower build rather than a failed install.
				if (pchCode == 0 && File.Exists(pchWorkPath))
				{
					pchUsed = cache.StorePch(pchDepPath, pchWorkPath, header, pchPathSalt);
					if (cache.PchFailure is { } pchNotKept)
					{
						LogWrite("compile cache: precompiled header not recorded: " + pchNotKept);
					}
				}
				else
				{
					progress(new Progress(
						"Preparing the game code build... (no precompiled header; this will be slower)",
						0.36f));
					string warning = "Precompiled header unavailable, continuing without it: " + pchOutput;
					LogWrite(warning);
					if (!Refii.Core.Engine_AppRoot.IsTestHost)
					{
						Godot.GD.PushWarning(warning);
					}
				}
			}
		}

		// 3. Compile missed units, LPT largest-first, JobCount() at a time.
		Result compiled = await CompileAllAsync(
			title,
			resolved,
			sources,
			flags,
			rawExtraFlags,
			normalizedExtraFlags,
			missIndices,
			objectPaths,
			cache,
			pchUsed,
			progress,
			control,
			cancellation).ConfigureAwait(false);
		if (!compiled.Ok)
		{
			return new Result(false, "Compiling the game code failed: " + compiled.Error);
		}

		if (cache.StoreFailure is { } notKept)
		{
			LogWrite($"compile cache: {cache.NotKept} unit(s) not kept for the next build; first: {notKept}");
		}

		// 4. Link.
		await control.WaitWhilePausedAsync(cancellation).ConfigureAwait(false);
		LogPhase("link");
		progress(new Progress("Linking the game module...", 0.95f));
		Result linked = await LinkAsync(title, resolved, objectPaths, control, cancellation).ConfigureAwait(false);
		if (!linked.Ok)
		{
			return new Result(false, "Linking the game module failed: " + linked.Error);
		}

		if (!File.Exists(modulePath))
		{
			return new Result(false,
				"The build reported success but produced no module at " + modulePath + ".");
		}

		string? moduleInputsDigest = WriteModuleInputs(
			title, resolved, cache, allRecompileRelativePaths, relativeIncludeDirs);
		WriteBuildInfo(title, resolved, compiledModsHash, moduleInputsDigest);

		// 5. Generational cache prune (retain last 3 generations up to 512 MB) & clean ephemeral scratch.
		progress(new Progress("Finalizing game code cache...", 0.98f));
		try
		{
			cache.KeepOnlyUsed(maxGenerations: ObjectCacheMaxGenerations, maxBytes: ObjectCacheMaxBytes);
		}
		catch (Exception exception)
		{
			LogWrite("cache prune warning: " + exception.Message);
		}

		CleanScratchDirectoriesBestEffort(title);

		LogPhase("done");
		progress(new Progress("Game code ready.", 1.0f));
		return new Result(true, string.Empty);
	}

	private static void ApplyPostCodegenTransforms(string generatedDir)
	{
		string pchHeader = Path.Combine(generatedDir, "refii_pch.h");
		if (File.Exists(pchHeader))
		{
			string original = File.ReadAllText(pchHeader);
			string updated = CodegenTransforms.ThreadLocalLockDepth(original);
			if (!string.Equals(original, updated, StringComparison.Ordinal))
			{
				File.WriteAllText(pchHeader, updated);
			}
		}

		foreach (string regFile in Directory.EnumerateFiles(generatedDir, "*_register.cpp", SearchOption.TopDirectoryOnly))
		{
			string stem = Path.GetFileNameWithoutExtension(regFile);
			if (!stem.EndsWith("_register", StringComparison.Ordinal) || stem.Length <= "_register".Length)
			{
				continue;
			}

			string prefix = stem[..^"_register".Length];
			string functionName = prefix + "_RegisterFunctions";
			string initFile = Path.Combine(generatedDir, prefix + "_init.cpp");
			string? initSource = File.Exists(initFile) ? File.ReadAllText(initFile) : null;
			try
			{
				string original = File.ReadAllText(regFile);
				string optimized = CodegenTransforms.OptimizeRegistration(original, initSource, functionName);
				if (!string.Equals(original, optimized, StringComparison.Ordinal))
				{
					File.WriteAllText(regFile, optimized);
				}
			}
			catch (InvalidDataException exception)
			{
				LogWrite("skipping registration optimization for " + Path.GetFileName(regFile) + ": " + exception.Message);
			}
		}
	}

	private static IReadOnlyList<string> NormalizeExtraFlagsForCache(
		IReadOnlyList<string> extraFlags,
		string modsStagingDir)
	{
		string modsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modsStagingDir));
		StringComparison comparison = OperatingSystem.IsWindows()
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;
		var normalized = new List<string>(extraFlags.Count);
		foreach (string flag in extraFlags)
		{
			if (flag.StartsWith("-I", StringComparison.Ordinal) && flag.Length > 2)
			{
				string rawPath = flag[2..];
				string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rawPath));
				if (string.Equals(fullPath, modsRoot, comparison))
				{
					normalized.Add("-Imods");
					continue;
				}

				if (fullPath.StartsWith(modsRoot + Path.DirectorySeparatorChar, comparison))
				{
					string rel = Path.GetRelativePath(modsRoot, fullPath).Replace('\\', '/');
					normalized.Add("-Imods/" + rel);
					continue;
				}
			}

			normalized.Add(flag);
		}

		return normalized;
	}

	/// <summary>
	/// Writes the manifest the generator actually reads, with absolute paths.
	/// </summary>
	/// <remarks>
	/// The staged manifest is a verbatim copy of the repo's, whose paths are relative to
	/// the repository. Resolving those against an installed app would put the generated
	/// sources somewhere inside the application directory. Only the launcher knows where
	/// the player's game and the output belong, so it states both absolutely here.
	/// </remarks>
	private static string WriteEffectiveManifest(Refii.Core.Engine_Title title, out string error)
	{
		string source = GetVersionManifestPath(title);
		if (!File.Exists(source))
		{
			error = "The code generation manifest for " + GetGameVersionName(title) +
					" is missing from this install (" + source + ").";
			return string.Empty;
		}

		string generatedDir = GetGeneratedDirectory(title);
		string versionDir = GetVersionDirectory(title);
		string gameExe = GetGameExecutablePath(title);

		try
		{
			string[] lines = File.ReadAllLines(source);
			var rewritten = new StringBuilder();
			bool sawFilePath = false;
			bool sawOutDirectory = false;

			// `includes` is written as a MULTI-LINE array. Replacing just the line it starts
			// on leaves its entries and closing bracket behind as stray top-level text, and
			// the generator rejects the whole manifest with a parse error.
			bool insideIncludes = false;
			var includeNames = new List<string>();

			foreach (string line in lines)
			{
				if (insideIncludes)
				{
					includeNames.AddRange(QuotedValues(line));
					if (line.Contains(']'))
					{
						insideIncludes = false;
						AppendIncludes(rewritten, versionDir, includeNames);
					}

					continue;
				}

				string trimmed = line.TrimStart();

				if (trimmed.StartsWith("file_path", StringComparison.Ordinal))
				{
					rewritten.AppendLine("file_path = \"" + Escape(gameExe) + "\"");
					sawFilePath = true;
					continue;
				}

				if (trimmed.StartsWith("out_directory_path", StringComparison.Ordinal))
				{
					rewritten.AppendLine("out_directory_path = \"" + Escape(generatedDir) + "\"");
					sawOutDirectory = true;
					continue;
				}

				if (trimmed.StartsWith("includes", StringComparison.Ordinal))
				{
					// Every declared include is repointed at its staged copy, rather than
					// one hardcoded name: the manifest owns which files codegen reads, and
					// silently dropping one it added would change the generated code
					// without changing anything the launcher can see.
					includeNames.AddRange(QuotedValues(line));
					insideIncludes = line.Contains('[') && !line.Contains(']');
					if (!insideIncludes)
					{
						AppendIncludes(rewritten, versionDir, includeNames);
					}

					continue;
				}

				rewritten.AppendLine(line);
			}

			if (!sawFilePath || !sawOutDirectory)
			{
				error = "The code generation manifest does not declare the entrypoint paths " +
						"this launcher knows how to repoint.";
				return string.Empty;
			}

			string destination = Path.Combine(
				generatedDir, title.Key + "_manifest.effective.toml");
			File.WriteAllText(destination, rewritten.ToString());
			error = string.Empty;
			return destination;
		}
		catch (Exception exception)
		{
			error = "Could not prepare the code generation manifest: " + exception.Message;
			return string.Empty;
		}
	}

	/// <summary>Every double-quoted value on a line, in order.</summary>
	private static List<string> QuotedValues(string line)
	{
		var values = new List<string>();
		int index = 0;

		while (true)
		{
			int open = line.IndexOf('"', index);
			if (open < 0)
			{
				break;
			}

			int close = line.IndexOf('"', open + 1);
			if (close < 0)
			{
				break;
			}

			string value = line[(open + 1)..close];
			if (value.Length > 0)
			{
				values.Add(value);
			}

			index = close + 1;
		}

		return values;
	}

	/// <summary>
	/// Writes the includes array, each entry repointed at its staged copy.
	/// </summary>
	private static void AppendIncludes(StringBuilder rewritten, string versionDirectory, List<string> names)
	{
		var absolute = new List<string>();
		foreach (string name in names)
		{
			// By file NAME, beside the manifest that names it: an include sits in its
			// version's folder, and the repo path it is written as does not exist on a
			// player's machine.
			absolute.Add("\"" +
				Escape(Path.Combine(versionDirectory, Path.GetFileName(name))) + "\"");
		}

		rewritten.AppendLine("includes = [" + string.Join(", ", absolute) + "]");
	}

	private static string Escape(string path) => path.Replace("\\", "\\\\");

	/// <summary>
	/// Reads a <c>set(VARIABLE a b c)</c> list out of one of the staged .cmake files.
	/// </summary>
	/// <remarks>
	/// The generator writes sources.cmake and packaging writes include_dirs.cmake, both in
	/// CMake's list syntax, and both remain the authority on what to compile and in which
	/// include order. Nothing here configures CMake — this only reads two lists out of a
	/// format we do not control.
	///
	/// TOKENS ARE SPLIT BEFORE <c>${CMAKE_CURRENT_LIST_DIR}</c> IS EXPANDED. The install
	/// directory is "Fable 2 Return to Albion", so expanding first and splitting after would
	/// tear every path in half at its spaces.
	/// </remarks>
	internal static List<string> ReadCMakeList(string path, string variable)
	{
		var values = new List<string>();
		if (!File.Exists(path))
		{
			return values;
		}

		string directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
		bool inside = false;

		foreach (string rawLine in File.ReadAllLines(path))
		{
			string line = rawLine;

			int comment = line.IndexOf('#');
			if (comment >= 0)
			{
				line = line[..comment];
			}

			if (!inside)
			{
				int start = line.IndexOf("set(" + variable, StringComparison.Ordinal);
				if (start < 0)
				{
					continue;
				}

				inside = true;
				line = line[(start + 4 + variable.Length)..];
			}

			bool last = false;
			int close = line.IndexOf(')');
			if (close >= 0)
			{
				line = line[..close];
				last = true;
			}

			foreach (string token in line.Split(
				(char[]?)null, StringSplitOptions.RemoveEmptyEntries))
			{
				values.Add(token.Replace("${CMAKE_CURRENT_LIST_DIR}", directory));
			}

			if (last)
			{
				break;
			}
		}

		return values;
	}

	private static List<string> MacroPrefixFlags(Refii.Core.Engine_Title title) =>
		new()
		{
			"-fmacro-prefix-map=" + Path.TrimEndingDirectorySeparator(Path.GetFullPath(GetGeneratedDirectory(title))) + "=/generated",
			"-fmacro-prefix-map=" + Path.TrimEndingDirectorySeparator(Path.GetFullPath(RecompileDirectory)) + "=/recompile",
			"-fmacro-prefix-map=" + Path.TrimEndingDirectorySeparator(Path.GetFullPath(GetModsStagingDirectory(title))) + "=/mods",
		};

	/// <summary>
	/// Whether the module is compiled and linked under <c>-mcmodel=large</c>: x86-64 off Windows,
	/// where the ~300 MB module overflows 32-bit PC-relative relocations without it. AArch64's
	/// default model reaches far enough, and Clang refuses the large model for position-independent
	/// code there. Matches refii_apply_generated_payload_compile_options in the top-level CMakeLists.
	/// </summary>
	private static bool UsesLargeCodeModel =>
		!OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.X64;

	/// <summary>
	/// The flags every translation unit is compiled with.
	/// </summary>
	/// <remarks>
	/// MATCHED TO THE DEV BUILD. <c>-mcmodel=large</c> and <c>-msse4.1</c> are applied to the
	/// payload by refii_apply_generated_payload_compile_options in the top-level
	/// CMakeLists, and the install-time project set NEITHER — so a player was compiling the
	/// same sources under a different code model than anything we build or test. At ~300 MB
	/// the module overflows 32-bit PC-relative relocations without the large model.
	///
	/// Optimised even in a Debug install: this is the guest's hot code, the whole simulation
	/// runs inside it, and -O0 would hand the player a guest that is the entire frame budget.
	/// Measured per generated TU: -O0 9.6s, -O1 14.3s, -O2 17.7s. Debug info costs nothing
	/// measurable and keeps generated frames resolvable in a backtrace.
	/// </remarks>
	/// <summary>
	/// The triple to compile for, when the bundled zig is not built for this host.
	/// </summary>
	/// <remarks>
	/// WINDOWS ON ARM IS SHIPPED THE x86_64 ZIG, deliberately, so the target the toolchain would
	/// otherwise have inferred has to be named here instead.
	///
	/// zig 0.16.0's own aarch64-windows build crashes clang on -include-pch: 6 to 13 of every 16
	/// units, sequentially as much as in parallel, on a precompiled header of nothing but
	/// standard library headers, with STATUS_ACCESS_VIOLATION and STATUS_INSTRUCTION_MISALIGNMENT.
	/// Jumping to a misaligned instruction is bad codegen inside zig's binary, not anything this
	/// build does wrong -- and zig's aarch64-windows CI is down for want of hardware, so it is not
	/// a thing upstream is currently catching. The same units compiled without the PCH crash 0 of
	/// 16, which is what made the header look innocent at first.
	///
	/// The x86_64 build under emulation cross-compiles the identical objects with the PCH working,
	/// and measured 0.27s per unit against 0.64s for dropping the PCH on the native build: the
	/// header is worth more than the emulation costs, so this is 2.4x FASTER, not a compromise.
	///
	/// aarch64-windows-gnu is what the native toolchain already defaulted to, so no ABI changes --
	/// see Toolchain's note on the GNU/MinGW ABI. Linux and Windows x86_64 name nothing and keep
	/// native detection, which is what the glibc discovery described there depends on.
	/// </remarks>
	private static IReadOnlyList<string> TargetFlags() =>
		OperatingSystem.IsWindows()
				&& RuntimeInformation.OSArchitecture == Architecture.Arm64
			? new[] { "-target", "aarch64-windows-gnu" }
			: Array.Empty<string>();

	/// <summary>
	/// The CPU every unit and the precompiled header are compiled for on ARM64 Linux, stated
	/// once: zig detects the host CPU from the core the compiler happens to start on, so on a
	/// machine with mixed cores (Cortex-X4 beside Cortex-A720) the header and a unit can name
	/// different CPUs, and clang refuses the header. Empty where a target is stated
	/// (<see cref="TargetFlags"/>), which already compiles for the generic CPU.
	/// </summary>
	private static readonly Lazy<string> HostArmCpu = new(() =>
	{
		if (RuntimeInformation.OSArchitecture != Architecture.Arm64 || !OperatingSystem.IsLinux())
		{
			return string.Empty;
		}

		try
		{
			return ArmCpuFlag(File.ReadAllText("/proc/cpuinfo"));
		}
		catch (Exception error) when (error is IOException or UnauthorizedAccessException)
		{
			return ArmCpuFlag(string.Empty);
		}
	});

	/// <summary>
	/// The <c>-mcpu</c> flag for an ARM64 Linux host: the generic CPU plus the instruction sets
	/// EVERY core in <paramref name="cpuInfo"/> (the text of <c>/proc/cpuinfo</c>) reports, so
	/// the module runs on whichever core the scheduler picks.
	/// </summary>
	internal static string ArmCpuFlag(string cpuInfo)
	{
		HashSet<string>? common = null;
		foreach (string line in cpuInfo.Split('\n'))
		{
			int colon = line.IndexOf(':');
			if (colon < 0 || !line.AsSpan(0, colon).Trim().Equals("Features", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			var core = new HashSet<string>(
				line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
				StringComparer.Ordinal);
			if (common is null)
			{
				common = core;
			}
			else
			{
				common.IntersectWith(core);
			}
		}

		var flag = new StringBuilder("-mcpu=generic");
		foreach ((string kernel, string zig) in ArmFeatures)
		{
			if (common is not null && common.Contains(kernel))
			{
				flag.Append('+').Append(zig);
			}
		}

		return flag.ToString();
	}

	/// <summary>The kernel's name for an ARM64 instruction set, and zig's name for the same one.</summary>
	private static readonly (string Kernel, string Zig)[] ArmFeatures =
	{
		("atomics", "lse"),
		("crc32", "crc"),
		("asimdrdm", "rdm"),
		("asimddp", "dotprod"),
		("aes", "aes"),
		("sha2", "sha2"),
	};

	private static List<string> CompileFlags(IReadOnlyList<string> includeDirectories) =>
		CompileFlags(Refii.Core.Engine_Title.Active, includeDirectories);

	private static List<string> CompileFlags(Refii.Core.Engine_Title title, IReadOnlyList<string> includeDirectories)
	{
		var flags = new List<string>
		{
			"-std=gnu++23",
			"-fPIC",
			"-O2",
			// NO -g. This is a release build of the player's own game code, and the debug info
			// was not free: refii_register.cpp is 61,000 lines of one function, where the DWARF
			// for it dominated the unit's compile time. Nothing ships a debugger for it.
		};

		// Ahead of everything else so the rest is read against the right target.
		flags.InsertRange(0, TargetFlags());

		if (RuntimeInformation.OSArchitecture == Architecture.X64)
		{
			// -march=native: the module is built on the machine that runs it, so it targets this
			// CPU. Stated outright because zig only defaults to the host CPU when no -m feature
			// flag is given; -msse4.1 alone drops it to baseline x86-64, and std::fma then calls
			// compiler-rt's software fma (66% of the XAudio render thread, measured). Forcing
			// -mfma instead would not start on a CPU without FMA3.
			flags.Add("-march=native");
			flags.Add("-msse4.1");
		}
		else if (HostArmCpu.Value is { Length: > 0 } armCpu)
		{
			flags.Add(armCpu);
		}

		if (UsesLargeCodeModel)
		{
			flags.Add("-mcmodel=large");
		}

		if (OperatingSystem.IsWindows())
		{
			// Guest SEH scopes emit __try/__except, which traps hardware faults only under
			// asynchronous EH; the SDK's own recomp targets set the same flag (REXGLUE_RECOMP_OPTIONS
			// in generated/rexglue.cmake). zig c++ is clang, so it is the GNU spelling.
			flags.Add("-fasync-exceptions");
		}

		// MATCHED TO THE PAYLOAD TARGETS IN OUR OWN BUILD. The hook sources now compile
		// here, so the definitions that make them payload-shaped have to be stated here too:
		// logging crosses as C rather than as spdlog, and Tracy is absent. Omitting either
		// produces a module that imports C++ symbols again and fails to link on Windows.
		flags.Add("-DREFII_PAYLOAD_LOG_BRIDGE=1");

		// Which executable this payload is recompiled from. guest_build_stamp.cpp states it to
		// the runtime at load, which is how the hooks and the host bridge pick per-build values.
		if (title.IsUnpatched(GetGameVersionName(title)))
		{
			flags.Add("-DREFII_GUEST_BUILD_UNPATCHED=1");
		}

		// HEADER-ONLY fmt. The log bridge still formats with ::fmt::format before handing the
		// finished string across the C boundary, so the payload pulls in fmt::vformat, which is
		// NOT inline. On Linux that resolves inside librexruntime because both sides are GNU; on
		// Windows the runtime is MSVC-built and mangles it differently, so it can never resolve
		// and the link ends on one undefined symbol. Inlining fmt removes the import entirely,
		// which is the same answer on both platforms rather than a Windows special case.
		flags.Add("-DFMT_HEADER_ONLY");

		// SPDLOG_COMPILED_LIB is not optional here. Without it spdlog's headers switch to
		// header-only and define logger::flush() inline, which collides with the payload's
		// own forwarder for it:
		//   "error: redefinition of 'flush'" against spdlog/logger-inl.h
		// SPDLOG_FMT_EXTERNAL goes with it, because that is how this build compiles spdlog
		// and the two must agree on which fmt the headers expect.
		flags.Add("-DSPDLOG_COMPILED_LIB");
		flags.Add("-DSPDLOG_FMT_EXTERNAL");
		foreach (string undefine in new[]
		{
			"TRACY_ENABLE", "TRACY_MANUAL_LIFETIME", "TRACY_DELAYED_INIT",
			"TRACY_ON_DEMAND", "TRACY_FIBERS", "TRACY_IMPORTS", "REXGLUE_ENABLE_PROFILING",
		})
		{
			flags.Add("-U" + undefine);
		}

		// FORCE-INCLUDED AHEAD OF EVERYTHING. The SDK's chrono headers specialize
		// std::chrono::clock_time_conversion, which libstdc++ declares and libc++ does not, and
		// an explicit specialization of an undeclared template is an error rather than something
		// the preprocessor can be talked out of. On Linux the build compiles against staged
		// libstdc++ headers so it never arises; on Windows refii_stage_stdcxx.cmake stages
		// nothing and zig supplies libc++, so it always does. The header self-guards on
		// _LIBCPP_VERSION and costs a libstdc++ build one <version> include.
		string chronoShim = Path.Combine(RecompileDirectory, "src", "libcxx_chrono_shim.h");
		if (File.Exists(chronoShim))
		{
			flags.Add("-include");
			flags.Add(chronoShim);
		}

		flags.Add("-I" + GetGeneratedDirectory(title));
		foreach (string directory in includeDirectories)
		{
			flags.Add("-I" + directory);
		}

		return flags;
	}

	private static string StdCxxDirectory => Path.Combine(RecompileDirectory, "stdcxx");

	/// <summary>
	/// Points the compiler at the staged libstdc++ headers instead of Zig's libc++.
	/// </summary>
	/// <remarks>
	/// ZIG BUNDLES libc++, AND THE PAYLOAD NEEDS libstdc++ — see cmake/refii_stage_stdcxx.cmake
	/// for why. Under libc++ our own rex/chrono/chrono.h does not even parse:
	///   "error: explicit specialization of undeclared template struct 'clock_time_conversion'"
	/// and the std::-typed symbols the payload imports from librefii_vfs would not resolve,
	/// because libc++ mangles them through an inline namespace.
	///
	/// ORDER IS THE CONTRACT. The C++ headers must come FIRST and Zig's libc headers after,
	/// because &lt;cmath&gt; reaches the C library through <c>#include_next &lt;math.h&gt;</c>,
	/// which resumes the search after the directory it was itself found in. Reversed, the
	/// build fails with "fatal error: 'math.h' file not found".
	///
	/// SO THE C++ HEADERS ARE -I, NOT -isystem. On a host with no glibc headers of its own
	/// (SteamOS ships none in /usr/include) zig falls back to its bundled libc and emits
	/// -isystem for those directories AHEAD of anything given here; clang then drops our
	/// later copies as duplicates, which leaves every libc directory before the C++ ones
	/// and nothing for the #include_next to find. -I is searched before all -isystem, so
	/// the order holds whichever libc zig picks. libstdc++'s headers mark themselves
	/// system headers, so nothing is lost in diagnostics.
	///
	/// Returns empty when nothing is staged, which is the Windows case: that build links
	/// MSVC's runtime and this question does not arise.
	/// </remarks>
	private static List<string> StandardLibraryFlags(Toolchain? resolved = null)
	{
		var flags = new List<string>();

		string order = Path.Combine(StdCxxDirectory, "order.txt");
		if (!File.Exists(order))
		{
			return flags;
		}

		flags.Add("-nostdinc++");
		foreach (string slot in File.ReadAllLines(order))
		{
			if (slot.Trim().Length == 0)
			{
				continue;
			}

			flags.Add("-I");
			flags.Add(Path.Combine(StdCxxDirectory, slot.Trim()));
		}

		string toolchainRoot = resolved.HasValue && !string.IsNullOrEmpty(resolved.Value.Root)
			? resolved.Value.Root
			: Toolchain.BundleDirectory;
		string libc = Path.Combine(toolchainRoot, "lib", "libc", "include");
		bool arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;
		foreach (string variant in new[]
		{
			arm ? "aarch64-linux-gnu" : "x86_64-linux-gnu",
			"generic-glibc",
			arm ? "aarch64-linux-any" : "x86-linux-any",
			"any-linux-any",
		})
		{
			string directory = Path.Combine(libc, variant);
			if (Directory.Exists(directory))
			{
				flags.Add("-isystem");
				flags.Add(directory);
			}
		}

		return flags;
	}

	/// <summary>
	/// Compiles all cache-miss translation units in LPT largest-first order, <see cref="JobCount"/> at a time,
	/// storing newly built objects and their <c>clang -MD</c> dependencies into <paramref name="cache"/>.
	/// </summary>
	private static async Task<Result> CompileAllAsync(
		Refii.Core.Engine_Title title,
		Toolchain zig,
		IReadOnlyList<string> sources,
		IReadOnlyList<string> flags,
		IReadOnlyList<IReadOnlyList<string>> rawExtraFlags,
		IReadOnlyList<IReadOnlyList<string>> normalizedExtraFlags,
		IReadOnlyList<int> missIndices,
		string[] objectPaths,
		CompileCache cache,
		string precompiledHeader,
		Action<Progress> progress,
		BuildControl control,
		CancellationToken cancellation)
	{
		int total = sources.Count;
		int cachedCount = total - missIndices.Count;
		if (missIndices.Count == 0)
		{
			LogWrite($"compile cache hit for all {total} translation unit(s)");
			progress(new Progress($"Reusing cached game code objects ({total}/{total})...", 0.94f));
			return new Result(true, string.Empty);
		}

		int done = cachedCount;
		string failure = string.Empty;
		object failureLock = new();
		int pchBroken = 0;

		int jobs = JobCount();
		List<int> dispatchOrder = jobs > 1
			? CompileScheduler.OrderUnitsLargestFirst(missIndices, sources)
			: missIndices.ToList();

		// Persistent zig-cache only needs a single solo warmup unit on its very first cold run for a
		// given toolchain binary; pick the SMALLEST unit so the solo probe finishes in ~0.15s instead of
		// stalling all other cores behind the largest shard.
		string zigCacheDir = GetZigCacheDirectory(title);
		string toolchainStampKey = CompileCache.HashText(ToolchainBinaryIdentity(zig))[..16];
		string warmedStampPath = Path.Combine(zigCacheDir, "zig-warmed-" + toolchainStampKey + ".stamp");
		bool needsSoloWarmup = !File.Exists(warmedStampPath) && dispatchOrder.Count > 1 && jobs > 1;
		if (needsSoloWarmup)
		{
			int smallestUnit = dispatchOrder[^1];
			dispatchOrder.RemoveAt(dispatchOrder.Count - 1);
			dispatchOrder.Insert(0, smallestUnit);
		}

		var warmed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		if (!needsSoloWarmup)
		{
			warmed.TrySetResult(true);
		}

		string workDir = GetWorkDirectory(title);
		Directory.CreateDirectory(workDir);

		if (cachedCount > 0)
		{
			LogWrite($"compile cache hit {cachedCount}/{total}; compiling {missIndices.Count} unit(s) across {jobs} job(s)");
			progress(new Progress(
				"Compiling game code... " + done + "/" + total,
				0.40f + 0.55f * Math.Clamp((float)done / total, 0.0f, 1.0f)));
		}

		using var gate = new SemaphoreSlim(jobs);
		var running = new List<Task>(dispatchOrder.Count);

		for (int orderPos = 0; orderPos < dispatchOrder.Count; orderPos++)
		{
			bool isWarmupProbe = needsSoloWarmup && orderPos == 0;
			int slot = dispatchOrder[orderPos];
			running.Add(Task.Run(async () =>
			{
				if (!isWarmupProbe)
				{
					await warmed.Task.WaitAsync(cancellation).ConfigureAwait(false);
				}

				await control.WaitWhilePausedAsync(cancellation).ConfigureAwait(false);
				await gate.WaitAsync(cancellation).ConfigureAwait(false);
				try
				{
					lock (failureLock)
					{
						if (failure.Length != 0)
						{
							return;
						}
					}

					string workObjPath = Path.Combine(workDir, slot.ToString("D4") + ".o");
					string workDepPath = workObjPath + ".d";
					IReadOnlyList<string> unitExtra = rawExtraFlags[slot];

					List<string> BuildArguments(bool withPch)
					{
						var arguments = new List<string>(flags)
						{
							"-MD", "-MF", workDepPath,
						};
						if (withPch)
						{
							arguments.Add("-include-pch");
							arguments.Add(precompiledHeader);
						}

						if (unitExtra.Count > 0)
						{
							arguments.AddRange(unitExtra);
						}

						arguments.Add("-c");
						arguments.Add(sources[slot]);
						arguments.Add("-o");
						arguments.Add(workObjPath);
						return arguments;
					}

					// Units carrying per-unit extra flags (-fvisibility=hidden or mod -I/-D) skip -include-pch
					// because Clang rejects PCHs whose preprocessor macros or visibility differ.
					bool usePch = precompiledHeader.Length != 0
						&& unitExtra.Count == 0
						&& Volatile.Read(ref pchBroken) == 0;

					(int code, string output) = await RunCaptureAsync(
						title, zig.Zig, Prefixed("c++", BuildArguments(usePch)), control, cancellation)
						.ConfigureAwait(false);

					if (code != 0 && usePch && IsCrashExit(code))
					{
						if (Interlocked.Exchange(ref pchBroken, 1) == 0)
						{
							LogWrite("the precompiled header crashes this toolchain; "
								+ "compiling the rest without it");
						}

						usePch = false;
						(code, output) = await RunCaptureAsync(
							title, zig.Zig, Prefixed("c++", BuildArguments(false)), control, cancellation)
							.ConfigureAwait(false);
					}

					if (code != 0)
					{
						lock (failureLock)
						{
							if (failure.Length == 0)
							{
								failure = Path.GetFileName(sources[slot]) + ":\n" + output;
							}
						}

						return;
					}

					if (isWarmupProbe)
					{
						try
						{
							File.WriteAllText(warmedStampPath, toolchainStampKey + "\n");
						}
						catch (Exception)
						{
						}
					}

					objectPaths[slot] = cache.Store(
						sources[slot],
						workObjPath,
						workDepPath,
						usePch,
						normalizedExtraFlags[slot]);

					int finished = Interlocked.Increment(ref done);
					progress(new Progress(
						"Compiling game code... " + finished + "/" + total,
						0.40f + 0.55f * Math.Clamp((float)finished / total, 0.0f, 1.0f)));
				}
				finally
				{
					gate.Release();
					if (isWarmupProbe)
					{
						warmed.TrySetResult(true);
					}
				}
			}, cancellation));
		}

		try
		{
			await Task.WhenAll(running).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			return new Result(false, "cancelled");
		}

		return failure.Length == 0
			? new Result(true, string.Empty)
			: new Result(false, failure);
	}

	/// <summary>
	/// Links the compiled and cached translation unit objects plus shipped libraries into <paramref name="title"/>'s module.
	/// </summary>
	private static async Task<Result> LinkAsync(
		Refii.Core.Engine_Title title,
		Toolchain zig,
		IReadOnlyList<string> objectPaths,
		BuildControl control,
		CancellationToken cancellation)
	{
		string titleBinDir = GetTitleBinDirectory(title);
		string modulePath = GetModulePath(title);
		Directory.CreateDirectory(titleBinDir);
		var arguments = new List<string> { "-shared", "-o", modulePath };
		arguments.InsertRange(0, TargetFlags());
		if (!OperatingSystem.IsWindows())
		{
			if (UsesLargeCodeModel)
			{
				arguments.Add("-mcmodel=large");
			}

			arguments.Add("-Wl,-rpath,$ORIGIN:$ORIGIN/..:$ORIGIN/lib");
		}

		for (int index = 0; index < objectPaths.Count; index++)
		{
			arguments.Add(objectPaths[index]);
		}

		foreach (string name in LinkedLibraryNames(title))
		{
			string found = FindShippedLibrary(title, name);
			if (found.Length == 0)
			{
				return new Result(false,
					"the " + name + " library is missing from " + ShippedLibraryDirectory(title, name));
			}

			arguments.Add(found);
		}

		if (StandardLibraryFlags(zig).Count > 0)
		{
			arguments.Add("-nostdlib++");
			arguments.Add("-lstdc++");
		}

		(int code, string output) = await RunCaptureAsync(
			title, zig.Zig, Prefixed("c++", LinkArguments(title, arguments)), control, cancellation)
			.ConfigureAwait(false);

		return code == 0
			? new Result(true, string.Empty)
			: new Result(false, output);
	}

	/// <summary>
	/// The link arguments, moved into a response file on Windows to stay well below CreateProcess's 32,767-char limit.
	/// </summary>
	private static IReadOnlyList<string> LinkArguments(
		Refii.Core.Engine_Title title,
		IReadOnlyList<string> arguments)
	{
		if (!OperatingSystem.IsWindows())
		{
			return arguments;
		}

		var body = new StringBuilder();
		foreach (string argument in arguments)
		{
			body.Append('"').Append(argument).Append('"').Append('\n');
		}

		string workDir = GetWorkDirectory(title);
		string path = Path.Combine(workDir, "link.rsp");
		Directory.CreateDirectory(workDir);
		File.WriteAllText(path, body.ToString());
		LogWrite("link response file " + path + ":");
		LogWrite(body.ToString());
		return new[] { "@" + path };
	}

	/// <summary>Finds a shipped library by base name in bin/ (or the active title's instance folder), or "".</summary>
	private static string FindShippedLibrary(string name) =>
		FindShippedLibrary(Refii.Core.Engine_Title.Active, name);

	/// <summary>Finds a shipped library by base name for <paramref name="title"/>, or "".</summary>
	private static string FindShippedLibrary(Refii.Core.Engine_Title title, string name) =>
		FindLibrary(ShippedLibraryDirectory(title, name), name, Path.Combine(GetTitleBinDirectory(title),
			Refii.Core.Engine_NativeLibraries.FileNameFor("refii_hostbridge")));

	/// <summary>A title's instance library is in its folder; every other native is shared, in bin/.</summary>
	private static string ShippedLibraryDirectory(Refii.Core.Engine_Title title, string name) =>
		Refii.Core.Engine_Title.InstanceLibraries.Contains(name) ? GetTitleBinDirectory(title) : BinDirectory;

	/// <summary>
	/// Finds a library by base name in one directory, in any build configuration. When more than one
	/// configuration is present, the one <paramref name="bridge"/> imports wins: the bridge is loaded
	/// by its fixed name, and a module linked against another configuration brings a second runtime
	/// into the process.
	/// </summary>
	private static string FindLibrary(string directory, string name, string bridge = "")
	{
		var found = new List<string>();
		var imported = new List<string>();
		byte[]? bridgeBytes = null;

		foreach (string suffix in LibrarySuffixes)
		{
			foreach (string candidate in new[]
			{
				"lib" + name + suffix + ".so",
				"lib" + name + suffix + ".a",
				name + suffix + ".lib",
				"lib" + name + suffix + ".dll.a",
				name + suffix + ".dll",
				"lib" + name + suffix + ".dll",
			})
			{
				string path = Path.Combine(directory, candidate);
				if (File.Exists(path))
				{
					found.Add(path);
				}
			}
		}

		if (found.Count == 0)
		{
			return string.Empty;
		}

		if (found.Count > 1 && bridge.Length != 0)
		{
			try
			{
				bridgeBytes = File.ReadAllBytes(bridge);
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException)
			{
				bridgeBytes = null;
			}

			if (bridgeBytes != null)
			{
				foreach (string path in found)
				{
					if (ImportsConfiguration(bridgeBytes, name, Path.GetFileName(path)))
					{
						imported.Add(path);
					}
				}

				if (imported.Count > 0 && imported.Count < found.Count)
				{
					LogWrite("more than one " + name + " in " + directory + " ("
						+ string.Join(", ", found.ConvertAll(Path.GetFileName))
						+ "); taking the one " + Path.GetFileName(bridge) + " imports: "
						+ Path.GetFileName(imported[0]));
					return imported[0];
				}
			}
		}

		if (found.Count > 1)
		{
			found.Sort((left, right) =>
				File.GetLastWriteTimeUtc(right).CompareTo(File.GetLastWriteTimeUtc(left)));
			LogWrite("more than one " + name + " in " + directory + " ("
				+ string.Join(", ", found.ConvertAll(Path.GetFileName))
				+ "); taking the newest: " + Path.GetFileName(found[0]));
		}

		return found[0];
	}

	private static readonly string[] LibrarySuffixes = { string.Empty, "rd", "d" };

	/// <summary>
	/// True when a binary's import names include the build configuration <paramref name="fileName"/>
	/// belongs to — its shared library, or the one its static or import library stands for.
	/// </summary>
	internal static bool ImportsConfiguration(ReadOnlySpan<byte> binary, string name, string fileName)
	{
		foreach (string import in ImportNames(name, fileName))
		{
			if (binary.IndexOf(Encoding.ASCII.GetBytes(import)) >= 0)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>The names a binary imports the build configuration of <paramref name="fileName"/> by.</summary>
	private static string[] ImportNames(string name, string fileName)
	{
		foreach (string suffix in LibrarySuffixes)
		{
			string stem = name + suffix;
			if (fileName.StartsWith(stem + ".", StringComparison.Ordinal)
				|| fileName.StartsWith("lib" + stem + ".", StringComparison.Ordinal))
			{
				return new[] { "lib" + stem + ".so", stem + ".dll", "lib" + stem + ".dll" };
			}
		}

		return Array.Empty<string>();
	}

	/// <summary>
	/// True when an install holds more than one build configuration of a library the module links
	/// and the module imports one the bridge does not. Reads the module only in that case.
	/// </summary>
	private static bool LinkedAgainstAnotherConfiguration(
		Refii.Core.Engine_Title title, string modulePath, out string strayLibrary)
	{
		strayLibrary = string.Empty;

		try
		{
			foreach (string name in LinkedLibraryNames(title))
			{
				string directory = ShippedLibraryDirectory(title, name);
				string chosen = Path.GetFileName(FindShippedLibrary(title, name));
				if (chosen.Length == 0)
				{
					continue;
				}

				foreach (string suffix in LibrarySuffixes)
				{
					string other = Refii.Core.Engine_NativeLibraries.FileNameFor(name + suffix);
					if (other == chosen
						|| ImportNames(name, other).SequenceEqual(ImportNames(name, chosen))
						|| !File.Exists(Path.Combine(directory, other)))
					{
						continue;
					}

					if (FileContainsAny(modulePath, ImportNames(name, other)))
					{
						strayLibrary = other;
						return true;
					}
				}
			}
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			LogWrite("could not read the module's imports: " + e.Message);
		}

		return false;
	}

	/// <summary>True when a file holds any of the ASCII <paramref name="needles"/>.</summary>
	internal static bool FileContainsAny(string path, IReadOnlyList<string> needles)
	{
		var patterns = new List<byte[]>();
		int longest = 0;
		foreach (string needle in needles)
		{
			byte[] pattern = Encoding.ASCII.GetBytes(needle);
			patterns.Add(pattern);
			longest = Math.Max(longest, pattern.Length);
		}

		if (longest == 0)
		{
			return false;
		}

		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16,
			FileOptions.SequentialScan);
		var buffer = new byte[(1 << 20) + longest];
		int kept = 0;

		while (true)
		{
			int read = stream.Read(buffer, kept, buffer.Length - kept);
			if (read <= 0)
			{
				return false;
			}

			var window = new ReadOnlySpan<byte>(buffer, 0, kept + read);
			foreach (byte[] pattern in patterns)
			{
				if (window.IndexOf(pattern) >= 0)
				{
					return true;
				}
			}

			kept = Math.Min(longest - 1, window.Length);
			window[^kept..].CopyTo(buffer);
		}
	}

	private static List<string> Prefixed(string first, IReadOnlyList<string> rest)
	{
		var arguments = new List<string>(rest.Count + 1) { first };
		arguments.AddRange(rest);
		return arguments;
	}

	/// <summary>
	/// Writes the record the launcher reads to decide whether <paramref name="title"/>'s module is still current.
	/// </summary>
	private static void WriteBuildInfo(
		Refii.Core.Engine_Title title,
		Toolchain zig,
		string modsHash,
		string? moduleInputsDigest)
	{
		try
		{
			string buildInputsValue = !string.IsNullOrEmpty(moduleInputsDigest)
				? ModuleInputsPrefix + moduleInputsDigest
				: HashBuildInputs(title);

			var record = new Dictionary<string, string>
			{
				["game_hash"] = HashGameExecutable(title),
				["build_inputs"] = buildInputsValue,
				["mods_hash"] = modsHash,
				["compiler"] = "zig " + GetToolchainVersion(zig),
				["generator"] = "rexglue " + GetSdkVersion(),
				["platform"] = RuntimeInformation.RuntimeIdentifier,
				["built_utc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
			};

			string buildInfoPath = GetBuildInfoPath(title);
			string tempPath = buildInfoPath + ".tmp";
			File.WriteAllText(tempPath, JsonSerializer.Serialize(
				record, new JsonSerializerOptions
				{
					WriteIndented = true,
					TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
				}));
			File.Move(tempPath, buildInfoPath, overwrite: true);
		}
		catch (Exception)
		{
		}
	}

	/// <summary>
	/// Windows status codes that mean zig DIED rather than rejecting the source.
	/// </summary>
	private static bool IsCrashExit(int code) =>
		code == unchecked((int)0xC0000005)   // STATUS_ACCESS_VIOLATION
			|| code == unchecked((int)0xC0000374)   // STATUS_HEAP_CORRUPTION
			|| code == unchecked((int)0xC0000409);  // STATUS_STACK_BUFFER_OVERRUN

	/// <summary>How many times a crashed (not failed) zig invocation is run again.</summary>
	private const int CrashRetries = 2;

	/// <summary>
	/// Runs zig, retrying if it crashes. Every caller is a deterministic, re-runnable step --
	/// the precompiled header, one translation unit, or the link -- so running one again is
	/// always safe.
	/// </summary>
	private static async Task<(int Code, string Output)> RunCaptureAsync(
		Refii.Core.Engine_Title title,
		string executable,
		IReadOnlyList<string> arguments,
		BuildControl control,
		CancellationToken cancellation)
	{
		for (int attempt = 0; ; attempt++)
		{
			(int Code, string Output) result = await RunCaptureOnceAsync(
				title, executable, arguments, control, cancellation).ConfigureAwait(false);

			if (!IsCrashExit(result.Code) || attempt >= CrashRetries
					|| cancellation.IsCancellationRequested)
			{
				return result;
			}

			LogWrite("retrying " + Path.GetFileName(executable) + " after crash exit 0x"
				+ result.Code.ToString("X8") + ", attempt " + (attempt + 2)
				+ " of " + (CrashRetries + 1));
		}
	}

	/// <summary>
	/// Runs a process to completion and returns its exit code and output. Attached to
	/// <paramref name="control"/> while it runs, so a pause freezes it and a resume thaws it.
	/// </summary>
	private static async Task<(int Code, string Output)> RunCaptureOnceAsync(
		Refii.Core.Engine_Title title,
		string executable,
		IReadOnlyList<string> arguments,
		BuildControl control,
		CancellationToken cancellation)
	{
		string zigCacheDir = GetZigCacheDirectory(title);
		Directory.CreateDirectory(zigCacheDir);

		if (ActiveDriver?.RunProcessAsync is { } customProcess)
		{
			var env = new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["ZIG_GLOBAL_CACHE_DIR"] = zigCacheDir,
				["ZIG_LOCAL_CACHE_DIR"] = zigCacheDir,
			};
			Log(executable, arguments);
			try
			{
				(int code, string output) = await customProcess(
					executable, arguments, CompilerWorkingDirectory, env, control, cancellation).ConfigureAwait(false);
				return (code, LogResult(code, output));
			}
			catch (OperationCanceledException)
			{
				return (-1, LogResult(-1, "cancelled"));
			}
			catch (Exception exception)
			{
				return (-1, LogResult(-1, exception.ToString()));
			}
		}

		var startInfo = new ProcessStartInfo
		{
			FileName = executable,
			WorkingDirectory = CompilerWorkingDirectory,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
		};

		// Persistent per-title Zig cache directory so built libc++, compiler_rt, and import libs
		// survive across rebuilds instead of being compiled from scratch on every run.
		startInfo.EnvironmentVariables["ZIG_GLOBAL_CACHE_DIR"] = zigCacheDir;
		startInfo.EnvironmentVariables["ZIG_LOCAL_CACHE_DIR"] = zigCacheDir;

		foreach (string argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		Log(executable, arguments);

		try
		{
			using var process = new Process { StartInfo = startInfo };
			if (!process.Start())
			{
				return (-1, LogResult(-1, "could not start " + executable));
			}

			control.Attach(process);
			Task<string> output = process.StandardOutput.ReadToEndAsync();
			Task<string> errors = process.StandardError.ReadToEndAsync();
			try
			{
				await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				control.Detach(process);
				try
				{
					process.Kill(entireProcessTree: true);
				}
				catch (Exception)
				{
				}

				return (-1, LogResult(-1, "cancelled"));
			}

			control.Detach(process);
			return (process.ExitCode, LogResult(process.ExitCode,
				(await output.ConfigureAwait(false) + await errors.ConfigureAwait(false)).Trim()));
		}
		catch (OperationCanceledException)
		{
			return (-1, LogResult(-1, "cancelled"));
		}
		catch (Exception exception)
		{
			return (-1, LogResult(-1, exception.ToString()));
		}
	}

	private static readonly object s_logGate = new();
	private static StringBuilder? s_logBuffer;
	private static string? s_logBufferPath;
	private static int s_logScopeDepth;

	private sealed class LogBufferScope : IDisposable
	{
		private int _disposed;

		public void Dispose()
		{
			if (Interlocked.Exchange(ref _disposed, 1) != 0)
			{
				return;
			}

			lock (s_logGate)
			{
				if (s_logScopeDepth > 0)
				{
					s_logScopeDepth--;
				}

				FlushLogBufferLocked();
				if (s_logScopeDepth == 0)
				{
					s_logBuffer = null;
					s_logBufferPath = null;
				}
			}
		}
	}

	private static IDisposable BeginLogScope()
	{
		lock (s_logGate)
		{
			s_logScopeDepth++;
			s_logBuffer ??= new StringBuilder(16 * 1024);
			return new LogBufferScope();
		}
	}

	private static string CurrentLogPath() =>
		ActiveDriver?.CacheRootOverride is { Length: > 0 } cacheOverride
			? Path.Combine(cacheOverride, "logs", "refii-launcher.log")
			: LauncherPaths.GetLauncherLogPath();

	private static void FlushLogBufferLocked()
	{
		if (s_logBuffer == null || s_logBuffer.Length == 0 || string.IsNullOrEmpty(s_logBufferPath))
		{
			return;
		}

		try
		{
			string? directory = Path.GetDirectoryName(s_logBufferPath);
			if (!string.IsNullOrEmpty(directory))
			{
				Directory.CreateDirectory(directory);
			}

			File.AppendAllText(s_logBufferPath, s_logBuffer.ToString());
		}
		catch (Exception)
		{
		}
		finally
		{
			s_logBuffer.Clear();
		}
	}

	/// <summary>
	/// The build's own log, synchronized and buffered during active pipeline runs so concurrent
	/// compiler workers and P/Invoke codegen callbacks never race or contend on file handles.
	/// </summary>
	private static void LogWrite(string text)
	{
		string formatted = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + text + Environment.NewLine;
		lock (s_logGate)
		{
			string path = CurrentLogPath();
			if (s_logScopeDepth > 0 && s_logBuffer != null)
			{
				if (!string.IsNullOrEmpty(s_logBufferPath) &&
				    !string.Equals(s_logBufferPath, path, StringComparison.Ordinal))
				{
					FlushLogBufferLocked();
				}

				s_logBufferPath = path;
				s_logBuffer.Append(formatted);
				if (s_logBuffer.Length >= 64 * 1024)
				{
					FlushLogBufferLocked();
				}

				return;
			}

			try
			{
				string? directory = Path.GetDirectoryName(path);
				if (!string.IsNullOrEmpty(directory))
				{
					Directory.CreateDirectory(directory);
				}

				File.AppendAllText(path, formatted);
			}
			catch (Exception)
			{
			}
		}
	}

	/// <summary>Records a phase boundary and flushes buffered log lines so disk logs reflect active phase progress.</summary>
	internal static void LogPhase(string phase)
	{
		LogWrite("=== " + phase);
		lock (s_logGate)
		{
			FlushLogBufferLocked();
		}
	}

	/// <summary>
	/// Records the command line. Arguments are logged one per line rather than joined: the
	/// compile invocations carry dozens of -isystem paths with spaces in them, and a joined
	/// line cannot be pasted back into a shell to reproduce the failure by hand.
	/// </summary>
	private static void Log(string executable, IReadOnlyList<string> arguments)
	{
		var text = new StringBuilder();
		text.Append("run: ").Append(executable);
		foreach (string argument in arguments)
		{
			text.Append(Environment.NewLine).Append("       ").Append(argument);
		}

		LogWrite(text.ToString());
	}

	/// <summary>
	/// Records an exit code, and the output only when it is not zero — a successful compile
	/// of 150 translation units has nothing worth keeping and would bury the one that failed.
	/// Returns its argument so call sites stay single-expression.
	/// </summary>
	private static string LogResult(int code, string output)
	{
		if (code == 0)
		{
			LogWrite("exit 0");
		}
		else
		{
			LogWrite("exit " + code + (output.Length > 0 ? Environment.NewLine + output : string.Empty));
		}

		return output;
	}

	private static async Task<Result> RunStepAsync(
		string executable,
		IReadOnlyList<string> arguments,
		string workingDirectory,
		Action<string> onLine,
		CancellationToken cancellation,
		IReadOnlyList<string>? additionalPathDirectories = null)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = executable,
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,

			// Same as RunCaptureAsync: a GUI parent starting a console child on Windows
			// gets a console window per invocation unless this says otherwise.
			CreateNoWindow = true,
		};

		foreach (string argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		// Only the Windows bootstrap uses this, and its tools may live outside PATH (the
		// copies Visual Studio bundles): cmake finds ninja and the compiler by name, so their
		// directories go ahead of the inherited PATH.
		if (additionalPathDirectories is not null && additionalPathDirectories.Count > 0)
		{
			string existing = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
			var searchDirectories = new List<string>(additionalPathDirectories);
			if (existing.Length != 0)
			{
				searchDirectories.Add(existing);
			}
			startInfo.Environment["PATH"] = string.Join(Path.PathSeparator, searchDirectories);
		}

		// The tail of the output, kept so a failure can say what actually went wrong
		// instead of only an exit code. Bounded: a failing compile can produce megabytes.
		var recent = new Queue<string>();

		// The WHOLE output, for the log. The 40-line tail above is what a person can be shown
		// in a dialog; it is not enough to diagnose a generator that printed the real cause
		// early and then kept going. Buffered and written once rather than appended per line,
		// because this can be thousands of lines and reopening the file for each is slower
		// than the generator itself.
		var transcript = new StringBuilder();

		// LOCKED, because two threads call this. `BeginOutputReadLine` and `BeginErrorReadLine`
		// each raise their events on their own pool thread, and both land here — so `recent` and
		// `transcript` were being mutated concurrently. A `Queue<T>` growing under that races on
		// its backing array and throws `Destination array was not long enough` out of
		// `SetCapacity`, on a pool thread, where nothing catches it: the whole app went down
		// mid-codegen with the build half done.
		object gate = new();

		void Record(string? line)
		{
			if (line is null)
			{
				return;
			}

			lock (gate)
			{
				transcript.AppendLine(line);

				recent.Enqueue(line);
				while (recent.Count > 40)
				{
					recent.Dequeue();
				}
			}

			// Outside the lock: the callback draws progress, and holding a lock across it would
			// let a slow paint stall the process's own output pipe.
			onLine(line);
		}

		Log(executable, arguments);

		try
		{
			using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
			process.OutputDataReceived += (_, args) => Record(args.Data);
			process.ErrorDataReceived += (_, args) => Record(args.Data);

			if (!process.Start())
			{
				return new Result(false, "could not start " + executable);
			}

			process.BeginOutputReadLine();
			process.BeginErrorReadLine();

			try
			{
				await process.WaitForExitAsync(cancellation);
			}
			catch (OperationCanceledException)
			{
				try
				{
					process.Kill(entireProcessTree: true);
				}
				catch (Exception)
				{
				}

				return new Result(false, "cancelled");
			}

			// The transcript goes to the log whether this succeeded or not: a tool that exits 0
			// without producing what it was run for is a failure this pipeline has hit, and the
			// reason is in the output, not the exit code.
			//
			// Read under the same lock the writers take — the reader threads are drained by
			// WaitForExit() by now, but taking it costs nothing and keeps one rule for the pair.
			string full;
			string tail;
			lock (gate)
			{
				full = transcript.ToString();
				tail = string.Join('\n', recent);
			}

			LogWrite("output of " + Path.GetFileName(executable) + Environment.NewLine + full);

			if (process.ExitCode != 0)
			{
				LogWrite("exit " + process.ExitCode);
				return new Result(false, "exit code " + process.ExitCode + "\n" + tail);
			}

			LogWrite("exit 0");
			return new Result(true, string.Empty);
		}
		catch (Exception exception)
		{
			string failed;
			lock (gate)
			{
				failed = transcript.ToString();
			}

			LogWrite("output of " + Path.GetFileName(executable) + Environment.NewLine + failed);
			LogWrite("threw: " + exception);
			return new Result(false, exception.Message);
		}
	}
}
