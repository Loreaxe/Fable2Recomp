using System;
using System.Collections.Generic;
using System.IO;

namespace Refii.Launcher.Core;

public static class Installer
{
    /// <summary>
    /// The folder every title installs under (the player's "Set Install Folder"); each title's
    /// install is its own folder inside it.
    /// </summary>
    public static string GetInstallRootPath() =>
        LauncherPaths.ResolveFromLauncherDirectory(Ini.GetString("GamePath", "assets/", "Game"));

    /// <summary>The install base path for <paramref name="title"/>: <c>&lt;root&gt;/Fable2/</c>, <c>&lt;root&gt;/Fable3/</c>.</summary>
    public static string GetInstallBasePath(Refii.Core.Engine_Title title) =>
        Path.Combine(GetInstallRootPath(), title.Folder) + Path.DirectorySeparatorChar;

    /// <summary>The active title's install: <c>&lt;root&gt;/Fable2/</c>, <c>&lt;root&gt;/Fable3/</c>.</summary>
    public static string GetInstallBasePath() =>
        GetInstallBasePath(Refii.Core.Engine_Title.Active);

    public static string GetInstalledExecutablePath() =>
        Path.Combine(Platform.GetExecutableDirectory(), Platform.ExecutableName);

    /// <summary>The runtime config path for <paramref name="title"/> (<see cref="Refii.Core.Engine_Title.ConfigPath"/>).</summary>
    public static string GetRuntimeConfigPath(Refii.Core.Engine_Title title) =>
        title.ConfigPath;

    /// <summary>
    /// The active title's runtime config, at the app root (<see cref="Refii.Core.Engine_Title.ConfigPath"/>).
    /// </summary>
    /// <remarks>
    /// The guest resolves this same file, so it cannot hang off the standalone
    /// executable's directory: under the Godot host there is no standalone executable, and
    /// in a dev tree that search picked whichever build output it found first — writing
    /// settings into a config the running guest never reads.
    /// </remarks>
    public static string GetRuntimeConfigPath() =>
        Refii.Core.Engine_AppRoot.ConfigPath;

    public static string GetGameDataDirectoryName() =>
        Ini.GetString("GameDataDirectory", "game", "Game");

    public static string GetGameMarkerRelativePath() =>
        Ini.GetString("GameMarkerFile", "game/default.xexp", "Game");

    private const int WindowsMaxPath = 260;
    private const int DeepestAssetRelativeLength = 16 + 178;

    private static bool? _longPathsUsable;

    public static bool LongPathsUsable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        if (_longPathsUsable.HasValue)
        {
            return _longPathsUsable.Value;
        }

        bool usable = false;
        string probe = Path.Combine(Path.GetTempPath(), new string('p', 250));
        try
        {
            if (probe.Length > WindowsMaxPath)
            {
                Directory.CreateDirectory(probe);
                usable = Directory.Exists(probe);
            }
        }
        catch (Exception)
        {
            usable = false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(probe))
                {
                    Directory.Delete(probe);
                }
            }
            catch (Exception)
            {
            }
        }

        _longPathsUsable = usable;
        return usable;
    }

    public static bool InstallBasePathLeavesWindowsPathRoom(string basePath, out string error)
    {
        if (!OperatingSystem.IsWindows() || LongPathsUsable())
        {
            error = string.Empty;
            return true;
        }

        int baseLength = Path.GetFullPath(basePath).Length;
        if (baseLength + 1 + DeepestAssetRelativeLength >= WindowsMaxPath)
        {
            error = "The install folder path is too long for Windows: game " +
                    "files would exceed the 260 character path limit. Move " +
                    @"the launcher to a shorter path (for example C:\Games\" +
                    "Refii) and try again.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    /// One of the title update's data files (<see cref="Refii.Core.Engine_Title.TitleUpdateDataFiles"/>),
    /// on the update volume the runtime mounts beside the game data: <c>&lt;assets&gt;/update/data/&lt;name&gt;</c>,
    /// for a patch at <c>&lt;assets&gt;/game/default.xexp</c>.
    /// </summary>
    public static string GetTitleUpdateDataPath(string patchPath, string name) =>
        Path.Combine(
            Refii.Core.Engine_GameData.UpdateRootFor(Path.GetDirectoryName(patchPath) ?? string.Empty),
            TitleUpdate.DataRelativePath(name));

    /// <summary>
    /// Writes the title update beside the installed executable, from a .xexp or the package
    /// it came in, after checking that it patches that executable. A package's data files go
    /// to the update volume with it.
    /// </summary>
    /// <remarks>
    /// The check runs against the executable the ISO extraction just wrote, not the one the
    /// dialog looked at, so a path pasted by hand or an unattended run gets the same answer.
    /// </remarks>
    public static bool InstallTitleUpdate(string sourcePath, string destinationPath, out string error) =>
        InstallTitleUpdate(Refii.Core.Engine_Title.Active, sourcePath, destinationPath, out error);

    /// <summary>
    /// Writes the title update beside <paramref name="title"/>'s installed executable after checking
    /// that it patches that executable.
    /// </summary>
    public static bool InstallTitleUpdate(
        Refii.Core.Engine_Title title, string sourcePath, string destinationPath, out string error)
    {
        string installBase = GetInstallBasePath(title);
        if (!SafePath.IsSafeDestinationUnderBase(destinationPath, installBase, out error))
        {
            return false;
        }

        var dataPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in title.TitleUpdateDataFiles)
        {
            string dataPath = GetTitleUpdateDataPath(destinationPath, name);
            if (!SafePath.IsSafeDestinationUnderBase(dataPath, installBase, out error))
            {
                return false;
            }

            dataPaths[name] = dataPath;
        }

        // No title update chosen: the disc executable is installed as it is. A patch left by an
        // earlier install has to go, because its presence is what selects the patched build,
        // and its data with it, because the guest would layer it over the disc data.
        if (sourcePath.Length == 0)
        {
            try
            {
                if (File.Exists(destinationPath))
                {
                    File.Delete(destinationPath);
                }

                DeleteTitleUpdateData(dataPaths.Values);

                error = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        if (!TitleUpdate.TryLoad(sourcePath, title, out TitleUpdate update, out error))
        {
            return false;
        }

        string executablePath = Path.Combine(Path.GetDirectoryName(destinationPath) ?? string.Empty, "default.xex");
        if (!TitleUpdate.TryReadExecutable(executablePath, out XexIdentity executable, out string xexError))
        {
            error = "The extracted game executable could not be identified: " + xexError;
            return false;
        }

        if (!SupportedGame.IsExecutable(title, executable, out error) ||
            !SupportedGame.IsTitleUpdate(title, update.Identity, out error) ||
            !update.AppliesTo(executable, out error))
        {
            return false;
        }

        try
        {
            string? parent = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent!);
            }

            File.WriteAllBytes(destinationPath, update.Patch);

            // Data from an earlier package never outlives the patch it came with.
            DeleteTitleUpdateData(dataPaths.Values);
            foreach ((string name, byte[] bytes) in update.DataFiles)
            {
                string dataPath = dataPaths[name];
                string? dataParent = Path.GetDirectoryName(dataPath);
                if (!string.IsNullOrEmpty(dataParent))
                {
                    Directory.CreateDirectory(dataParent!);
                }

                File.WriteAllBytes(dataPath, bytes);
            }

            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static void DeleteTitleUpdateData(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// Whether the installed executable and title update belong together, with a line to show.
    /// Cached briefly: the panels ask every refresh and the answer only changes on an install.
    /// </summary>
    public static bool CheckInstalledTitleUpdate(out string summary) =>
        CheckInstalledTitleUpdate(Refii.Core.Engine_Title.Active, out summary);

    /// <summary>
    /// Whether <paramref name="title"/>'s installed executable and title update belong together, with a line to show.
    /// </summary>
    public static bool CheckInstalledTitleUpdate(Refii.Core.Engine_Title title, out string summary)
    {
        long now = Environment.TickCount64;
        if (_titleUpdateCache.TryGetValue(title.Key, out var cached) && now - cached.Stamp < StepStatusTtlMs)
        {
            summary = cached.Summary;
            return cached.Ok;
        }

        bool ok = CheckInstalledTitleUpdateNow(title, out summary);
        _titleUpdateCache[title.Key] = (ok, summary, now);
        return ok;
    }

    private static readonly Dictionary<string, (bool Ok, string Summary, long Stamp)> _titleUpdateCache =
        new(StringComparer.Ordinal);

    private static bool CheckInstalledTitleUpdateNow(Refii.Core.Engine_Title title, out string summary)
    {
        string gameData = GetActiveGameDataDirectoryPath(title);
        string failedMarker = Path.Combine(gameData, InstallFailedFileName);
        if (File.Exists(failedMarker))
        {
            string detail = string.Empty;
            try
            {
                detail = File.ReadAllText(failedMarker).Trim();
            }
            catch
            {
            }

            summary = detail.Length != 0
                ? "Game install failed: " + detail
                : "Game install failed or incomplete. Reinstall from your disc image.";
            return false;
        }

        string executablePath = Path.Combine(gameData, "default.xex");
        string patchPath = Path.Combine(gameData, "default.xexp");

        if (!File.Exists(executablePath))
        {
            summary = string.Empty;
            return true;
        }

        if (!TitleUpdate.TryReadExecutable(executablePath, out XexIdentity executable, out string xexError))
        {
            summary = "The installed executable could not be identified: " + xexError;
            return false;
        }

        if (!SupportedGame.IsExecutable(title, executable, out string unsupported))
        {
            summary = unsupported;
            return false;
        }

        if (!File.Exists(patchPath))
        {
            summary = "Game version " + executable.VersionText + ", media ID " +
                      executable.ShortMediaIdText + ", installed without the title update.";
            return true;
        }

        if (!TitleUpdate.TryLoad(patchPath, title, out TitleUpdate update, out string patchError))
        {
            summary = "The installed title update is unusable: " + patchError;
            return false;
        }

        if (!SupportedGame.IsTitleUpdate(title, update.Identity, out string mismatch) ||
            !update.AppliesTo(executable, out mismatch))
        {
            summary = mismatch;
            return false;
        }

        // The data is what a bare .xexp install lacks: the patched code runs, on the disc's
        // scripts and text. Said here so the reinstall panel shows it, not an error.
        bool data = title.TitleUpdateDataFiles.Count != 0;
        foreach (string name in title.TitleUpdateDataFiles)
        {
            data &= File.Exists(GetTitleUpdateDataPath(patchPath, name));
        }

        string bank = data
            ? " and its data"
            : ", without its data (install the update from its package to get it)";
        summary = "Game version " + executable.VersionText + " with title update " +
                  update.Identity.VersionText + bank + ", media ID " + executable.ShortMediaIdText + ".";
        return true;
    }

    /// <summary>Path to <paramref name="title"/>'s game marker file (<c>game/default.xexp</c>).</summary>
    public static string GetGameMarkerPath(Refii.Core.Engine_Title title) =>
        Path.Combine(GetInstallBasePath(title), GetGameMarkerRelativePath());

    public static string GetGameMarkerPath() =>
        GetGameMarkerPath(Refii.Core.Engine_Title.Active);

    public static string GetGameTitle() => Refii.Core.Engine_Title.Active.Name;

    /// <summary>Path to <paramref name="title"/>'s configured game data directory.</summary>
    public static string GetGameDataDirectoryPath(Refii.Core.Engine_Title title) =>
        Path.Combine(GetInstallBasePath(title), GetGameDataDirectoryName());

    public static string GetGameDataDirectoryPath() =>
        GetGameDataDirectoryPath(Refii.Core.Engine_Title.Active);

    private static string GetWorkspaceBasePath(string gameInstallRoot)
    {
        string basePath = Path.TrimEndingDirectorySeparator(gameInstallRoot);
        if (string.Equals(Path.GetFileName(basePath), "data", StringComparison.OrdinalIgnoreCase))
        {
            basePath = Path.GetDirectoryName(basePath) ?? basePath;
        }

        if (string.Equals(Path.GetFileName(basePath), "game", StringComparison.OrdinalIgnoreCase))
        {
            basePath = Path.GetDirectoryName(basePath) ?? basePath;
        }

        return basePath;
    }

    private const long ActiveGameDataTtlMs = 1000;
    private static readonly Dictionary<string, (string Path, long Stamp)> _activeGameDataCache =
        new(StringComparer.Ordinal);

    /// <summary>Resolved or configured game data directory path for <paramref name="title"/>.</summary>
    public static string GetActiveGameDataDirectoryPath(Refii.Core.Engine_Title title)
    {
        long now = Environment.TickCount64;
        if (_activeGameDataCache.TryGetValue(title.Key, out var cached) && now - cached.Stamp < ActiveGameDataTtlMs)
        {
            return cached.Path;
        }

        string resolved = ResolveGameDataDirectory(title);
        string active = resolved.Length > 0 ? resolved : GetGameDataDirectoryPath(title);
        _activeGameDataCache[title.Key] = (active, now);
        return active;
    }

    public static string GetActiveGameDataDirectoryPath() =>
        GetActiveGameDataDirectoryPath(Refii.Core.Engine_Title.Active);

    /// <summary>Unpacked workspace path for <paramref name="title"/>.</summary>
    public static string GetLuaWorkspacePath(Refii.Core.Engine_Title title) =>
        Path.Combine(GetWorkspaceBasePath(GetActiveGameDataDirectoryPath(title)), "unpacked");

    public static string GetLuaWorkspacePath() =>
        GetLuaWorkspacePath(Refii.Core.Engine_Title.Active);

    /// <summary>The game executable the recompiler reads for <paramref name="title"/>.</summary>
    public static string GetGameExecutablePath(Refii.Core.Engine_Title title) =>
        Path.Combine(GetActiveGameDataDirectoryPath(title), "default.xex");

    /// <summary>
    /// The game executable the recompiler reads, inside the installed game data.
    /// </summary>
    public static string GetGameExecutablePath() =>
        GetGameExecutablePath(Refii.Core.Engine_Title.Active);

    /// <summary>Where the generator writes the recompiled C++ for <paramref name="title"/>.</summary>
    public static string GetGeneratedCodeDirectoryPath(Refii.Core.Engine_Title title) =>
        Path.Combine(GetWorkspaceBasePath(GetActiveGameDataDirectoryPath(title)), "generated");

    /// <summary>
    /// Where the generator writes the recompiled C++, beside the installed game data.
    /// </summary>
    /// <remarks>
    /// A SIBLING of <c>game</c> and <c>unpacked</c>, resolved the same way they are, because
    /// it is derived from the same install and has to follow it. Anchoring this on the
    /// native library directory instead put it under <c>build/&lt;arch&gt;/bin</c> while the
    /// installer wrote the game to <c>assets/game</c> — two different roots, so the build
    /// looked for an executable nothing had written.
    ///
    /// Deleted once the module is built; nothing may be kept here.
    /// </remarks>
    public static string GetGeneratedCodeDirectoryPath() =>
        GetGeneratedCodeDirectoryPath(Refii.Core.Engine_Title.Active);

    /// <summary>
    /// Persistent compile cache directory (<c>cache/&lt;Folder&gt;/compile</c>) holding cached generated C++,
    /// precompiled headers, object files, and Zig runtime libraries for <paramref name="title"/>.
    /// </summary>
    /// <param name="title">Title whose compile cache directory is requested.</param>
    /// <returns>Full path to <paramref name="title"/>'s compile cache directory.</returns>
    public static string GetCompileCacheDirectoryPath(Refii.Core.Engine_Title title) =>
        !string.IsNullOrEmpty(LauncherPaths.OverrideLauncherDirectory)
            ? Path.Combine(LauncherPaths.GetLauncherDirectory(), "cache", title.Folder, "compile")
            : Path.Combine(title.CacheDirectory, "compile");

    /// <summary>Persistent compile cache directory for the active title.</summary>
    /// <returns>Full path to the active title's compile cache directory.</returns>
    public static string GetCompileCacheDirectoryPath() =>
        GetCompileCacheDirectoryPath(Refii.Core.Engine_Title.Active);

    /// <summary>Which derived artefacts the Install tab tracks by a file the pipeline writes.</summary>
    public readonly record struct ReinstallStepStatus(bool Shaders, bool Assets, string AssetsDetail);

    private const long StepStatusTtlMs = 1000;
    private static readonly Dictionary<string, (ReinstallStepStatus Status, long Stamp)> _stepStatusCache =
        new(StringComparer.Ordinal);

    public static void InvalidateReinstallStepStatus()
    {
        _stepStatusCache.Clear();
        _activeGameDataCache.Clear();
        _titleUpdateCache.Clear();
    }

    public static ReinstallStepStatus GetReinstallStepStatus() =>
        GetReinstallStepStatus(Refii.Core.Engine_Title.Active);

    /// <summary>Returns cached derived-artefact step status for <paramref name="title"/>.</summary>
    public static ReinstallStepStatus GetReinstallStepStatus(Refii.Core.Engine_Title title)
    {
        long now = Environment.TickCount64;
        if (_stepStatusCache.TryGetValue(title.Key, out var cached) && now - cached.Stamp < StepStatusTtlMs)
        {
            return cached.Status;
        }

        string workspace = GetLuaWorkspacePath(title);
        bool assets = Refii.Textures.Engine_TexPrebuild.IsCurrent(title, out string assetsDetail);
        var status = new ReinstallStepStatus(
            File.Exists(Path.Combine(workspace, "shaders", "compile_report.tsv")),
            assets,
            assetsDetail);
        _stepStatusCache[title.Key] = (status, now);
        return status;
    }

    /// <summary>Content directory path for <paramref name="title"/>.</summary>
    public static string GetGameContentDirectoryPath(Refii.Core.Engine_Title title) =>
        Path.Combine(GetActiveGameDataDirectoryPath(title), "data");

    public static string GetGameContentDirectoryPath() =>
        GetGameContentDirectoryPath(Refii.Core.Engine_Title.Active);

    /// <summary>Ensures <paramref name="title"/>'s runtime config exists and carries current defaults.</summary>
    public static bool EnsureRuntimeConfigFile(Refii.Core.Engine_Title title, out string error) =>
        RuntimeConfig.EnsureDefaultsForTitle(title, GetRuntimeConfigPath(title), out error);

    public static bool EnsureRuntimeConfigFile(out string error) =>
        EnsureRuntimeConfigFile(Refii.Core.Engine_Title.Active, out error);

    public const string InstallOkFileName = ".install-ok";
    public const string InstallFailedFileName = ".install-failed";

    public static string GetGameInstallReceiptPath(Refii.Core.Engine_Title title) =>
        Path.Combine(GetGameDataDirectoryPath(title), InstallOkFileName);

    public static string GetGameInstallReceiptPath() =>
        GetGameInstallReceiptPath(Refii.Core.Engine_Title.Active);

    public static string GetGameInstallFailedPath(Refii.Core.Engine_Title title) =>
        Path.Combine(GetGameDataDirectoryPath(title), InstallFailedFileName);

    public static string GetGameInstallFailedPath() =>
        GetGameInstallFailedPath(Refii.Core.Engine_Title.Active);

    /// <summary>
    /// True when a directory really holds an extracted install.
    ///
    /// The marker is necessary but NOT sufficient. A source checkout ships
    /// <c>assets/game/default.xexp</c> with no banks beside it, so a marker-only test
    /// reports "installed" for a tree that has no game in it — and everything
    /// downstream then tries to mount nothing and reports 0 files instead of saying the
    /// game is missing. The banks are the payload, so the banks are the test.
    /// </summary>
    public static bool HasExtractedAssets(string gameDataDirectory, string markerPath)
    {
        if (!Directory.Exists(gameDataDirectory) || !File.Exists(markerPath) || !HasBanks(gameDataDirectory))
        {
            return false;
        }

        string failedMarker = Path.Combine(gameDataDirectory, InstallFailedFileName);
        if (File.Exists(failedMarker))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// True when a tree carries at least one <c>.bnk</c>.
    ///
    /// Two levels, not recursive: an unpacked asset tree is large enough that walking
    /// all of it to conclude "no banks" stalls startup for tens of seconds, and the
    /// banks always sit at the top or one level down.
    /// </summary>
    public static bool HasBanks(string root)
    {
        if (!Directory.Exists(root))
        {
            return false;
        }

        try
        {
            foreach (string _ in Directory.EnumerateFiles(root, "*.bnk", SearchOption.TopDirectoryOnly))
                return true;

            foreach (string child in Directory.EnumerateDirectories(root))
                foreach (string _ in Directory.EnumerateFiles(child, "*.bnk", SearchOption.TopDirectoryOnly))
                    return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// Where the game data actually is, or empty when it is not installed anywhere.
    ///
    /// The configured install location wins — that is what a player sets and where an
    /// install writes. A DEV tree is the exception: nothing has been installed under the
    /// launcher directory and the data sits staged in a build output instead, so the
    /// shared convention is consulted before giving up. Both candidates must carry banks
    /// to be accepted; a marker on its own is not an install.
    /// </summary>
    public static string ResolveGameDataDirectory() =>
        ResolveGameDataDirectory(Refii.Core.Engine_Title.Active);

    /// <summary>Where <paramref name="title"/>'s game data actually is, or empty when not installed.</summary>
    public static string ResolveGameDataDirectory(Refii.Core.Engine_Title title)
    {
        string configured = GetGameDataDirectoryPath(title);
        string marker = GetGameMarkerPath(title);
        if (HasExtractedAssets(configured, marker))
        {
            return configured;
        }

        // An install without the title update has no patch to act as the marker; the
        // executable beside where it would sit stands in for it.
        string executable = Path.Combine(
            Path.GetDirectoryName(marker) ?? string.Empty, "default.xex");
        if (HasExtractedAssets(configured, executable))
        {
            return configured;
        }

        string resolved = title == Refii.Core.Engine_Title.Active
            ? Refii.Core.Engine_GameData.Resolve()
            : Refii.Core.Engine_GameData.ResolveFor(title);
        return HasBanks(resolved) ? resolved : string.Empty;
    }

    /// <summary>True when <paramref name="title"/>'s game data is installed.</summary>
    public static bool AssetsExist(Refii.Core.Engine_Title title) =>
        ResolveGameDataDirectory(title).Length > 0;

    public static bool AssetsExist() => AssetsExist(Refii.Core.Engine_Title.Active);

    /// <summary>
    /// Whether refii's own runtime is present — NOT whether the game is installed.
    /// </summary>
    /// <remarks>
    /// THE HOST BRIDGE IS THE RUNTIME. This used to test for a standalone <c>refii</c>
    /// executable beside the app, which no longer ships: Godot hosts the guest in its own
    /// process, so there is no second binary to find. An exported build therefore always
    /// failed the test and reported "Missing Runtime Files" while being perfectly intact.
    ///
    /// A false answer here means the APP is broken — a bad package, a missing native — and
    /// installing the game cannot fix it. Whether the player's game is installed is
    /// <see cref="AssetsExist()"/>, and whether its code is built is
    /// <c>GameCodeBuild.NeedsBuild</c>. Whether the active title has a runtime of its own is
    /// <see cref="TitleRuntimeExists()"/>.
    /// </remarks>
    public static bool InstalledBuildExists() =>
        File.Exists(Path.Combine(
            Refii.Core.Engine_AppRoot.BinDirectory,
            Refii.Core.Engine_NativeLibraries.FileNameFor("refii_core"))) ||
        File.Exists(GetInstalledExecutablePath());

    /// <summary>
    /// Whether <paramref name="title"/>'s ReXGlue instance ships with this app: its host bridge in
    /// <c>bin/&lt;Folder&gt;/</c>. False for a title whose runtime is not built yet.
    /// </summary>
    public static bool TitleRuntimeExists(Refii.Core.Engine_Title title) =>
        File.Exists(Path.Combine(
            title.BinDirectory,
            Refii.Core.Engine_NativeLibraries.FileNameFor("refii_hostbridge")));

    /// <summary>
    /// Whether the active title's ReXGlue instance ships with this app: its host bridge in
    /// <c>bin/&lt;Folder&gt;/</c>. False for a title whose runtime is not built yet, which can be
    /// installed but not played.
    /// </summary>
    public static bool TitleRuntimeExists() =>
        TitleRuntimeExists(Refii.Core.Engine_Title.Active);

    public static bool UninstallManagedFiles(out string log) =>
        UninstallManagedFiles(Refii.Core.Engine_Title.Active, out log);

    /// <summary>Removes <paramref name="title"/>'s managed game, update, and compile-cache directories, leaving saves and mods intact.</summary>
    public static bool UninstallManagedFiles(Refii.Core.Engine_Title title, out string log)
    {
        var removed = new System.Text.StringBuilder();
        bool hasRemovedFiles = false;
        string[] directories =
        {
            GetGameDataDirectoryPath(title),
            GetUpdateDirectoryPath(title),
            GetCompileCacheDirectoryPath(title),
            GetGeneratedCodeDirectoryPath(title),
        };

        foreach (string directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
                removed.AppendLine("Removed " + directory);
                hasRemovedFiles = true;
            }
            catch (System.Exception exception)
            {
                removed.AppendLine("Failed to remove " + directory + ": " + exception.Message);
            }
        }

        InvalidateReinstallStepStatus();
        log = hasRemovedFiles
            ? removed.ToString().TrimEnd() + "\n" + title.Name + " install removed successfully."
            : string.Empty;
        return hasRemovedFiles;
    }

    public static string GetUpdateDirectoryName() =>
        Ini.GetString("UpdateDirectory", "update", "Game");

    /// <summary>Path to <paramref name="title"/>'s update directory.</summary>
    public static string GetUpdateDirectoryPath(Refii.Core.Engine_Title title) =>
        Path.Combine(GetInstallBasePath(title), GetUpdateDirectoryName());

    public static string GetUpdateDirectoryPath() =>
        GetUpdateDirectoryPath(Refii.Core.Engine_Title.Active);
}
