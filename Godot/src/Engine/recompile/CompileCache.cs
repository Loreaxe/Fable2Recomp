using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Refii.Recompile;

/// <summary>
/// Describes one named root directory whose files may be referenced in <c>clang -MD</c> dependency records.
/// </summary>
public sealed class CompileInputRoot
{
    /// <summary>Record prefix ending in <c>/</c> (for example <c>"bundle/"</c>, <c>"recompile/"</c>, <c>"generated/"</c>).</summary>
    public string Prefix { get; }

    /// <summary>Normalized full directory path on disk without trailing separator.</summary>
    public string DirectoryPath { get; }

    /// <summary>Optional precomputed relative-path-to-SHA256 map (used by validated Android bundles).</summary>
    public IReadOnlyDictionary<string, string>? PrecomputedHashes { get; }

    /// <summary>
    /// Whether this root represents an immutable toolchain sysroot/resource tree whose version is already bound
    /// into the build recipe (avoiding thousands of redundant system-header hashes per build).
    /// </summary>
    public bool IsImmutableToolchain { get; }

    /// <summary>Whether files in this root participate in include-directory header-shadowing checks (<see cref="CompileCache.SameNames"/>).</summary>
    public bool TrackInputsAndShadowing { get; }

    private readonly Dictionary<string, string>? _caseMap;

    private CompileInputRoot(
        string prefix,
        string directoryPath,
        IReadOnlyDictionary<string, string>? precomputedHashes,
        bool isImmutableToolchain,
        bool trackInputsAndShadowing)
    {
        if (string.IsNullOrEmpty(prefix) || !prefix.EndsWith('/'))
        {
            throw new ArgumentException("Root prefix must be non-empty and end with '/'.", nameof(prefix));
        }

        Prefix = prefix;
        DirectoryPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
        PrecomputedHashes = precomputedHashes;
        IsImmutableToolchain = isImmutableToolchain;
        TrackInputsAndShadowing = trackInputsAndShadowing;

        if (OperatingSystem.IsWindows() && precomputedHashes != null)
        {
            _caseMap = new Dictionary<string, string>(precomputedHashes.Count, StringComparer.OrdinalIgnoreCase);
            foreach (string key in precomputedHashes.Keys)
            {
                _caseMap[key] = key;
            }
        }
    }

    /// <summary>Creates a root backed by a pre-validated relative-path hash manifest (e.g. Android <c>bundle.json</c>).</summary>
    /// <param name="prefix">Record prefix ending in <c>/</c>.</param>
    /// <param name="directoryPath">Root folder on disk.</param>
    /// <param name="fileHashes">Validated map of forward-slash relative path to lowercase hex SHA-256.</param>
    /// <param name="trackInputsAndShadowing">Whether to collect read inputs and include them in <see cref="CompileCache.SameNames"/>.</param>
    /// <returns>Configured <see cref="CompileInputRoot"/>.</returns>
    public static CompileInputRoot Prehashed(
        string prefix,
        string directoryPath,
        IReadOnlyDictionary<string, string> fileHashes,
        bool trackInputsAndShadowing = true) =>
        new(prefix, directoryPath, fileHashes, isImmutableToolchain: false, trackInputsAndShadowing);

    /// <summary>Creates a root whose files are hashed on demand from disk using stat-memoized <c>Engine_Hash</c> SHA-256.</summary>
    /// <param name="prefix">Record prefix ending in <c>/</c>.</param>
    /// <param name="directoryPath">Root folder on disk.</param>
    /// <param name="trackInputsAndShadowing">Whether to collect read inputs and include them in <see cref="CompileCache.SameNames"/>.</param>
    /// <returns>Configured <see cref="CompileInputRoot"/>.</returns>
    public static CompileInputRoot DiskHashed(
        string prefix,
        string directoryPath,
        bool trackInputsAndShadowing = false) =>
        new(prefix, directoryPath, precomputedHashes: null, isImmutableToolchain: false, trackInputsAndShadowing);

    /// <summary>Creates an immutable toolchain header root (e.g. bundled <c>zig/lib</c>) whose version is bound in the build recipe.</summary>
    /// <param name="prefix">Record prefix ending in <c>/</c>.</param>
    /// <param name="directoryPath">Toolchain root folder on disk.</param>
    /// <returns>Configured <see cref="CompileInputRoot"/>.</returns>
    public static CompileInputRoot ImmutableToolchain(string prefix, string directoryPath) =>
        new(prefix, directoryPath, precomputedHashes: null, isImmutableToolchain: true, trackInputsAndShadowing: false);

    /// <summary>Normalizes a relative forward-slash path to canonical casing when backed by a precomputed map on Windows.</summary>
    /// <param name="relative">Forward-slash path relative to <see cref="DirectoryPath"/>.</param>
    /// <returns>Canonical relative path.</returns>
    internal string NormalizeRelative(string relative) =>
        _caseMap != null && _caseMap.TryGetValue(relative, out string? canonical) ? canonical : relative;
}

/// <summary>
/// Content-addressed translation-unit (<c>.o</c>) and precompiled-header (<c>.pch</c>) compilation cache
/// shared across Desktop (<c>GameCodeBuild</c>) and Android (<c>AndroidPhoneCompiler</c>).
/// </summary>
/// <remarks>
/// Uses <see cref="Refii.Core.Engine_Hash"/> exclusively so it runs identically on Desktop (.NET 8) and
/// Android (.NET 9 Mono and Bionic NativeAOT without OpenSSL). Thread-safe across concurrent compiler workers.
/// </remarks>
public sealed class CompileCache
{
    private const string GenerationsFileName = "generations.txt";
    private const string ToolchainToken = "toolchain";

    private static readonly ConcurrentDictionary<string, (long Length, long LastWriteUtcTicks, string Hash)> StatHashCache =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly object _gate = new();
    private readonly string _root;
    private readonly string _compilerDirectory;
    private readonly string _recipe;
    private readonly IReadOnlyList<CompileInputRoot> _roots;
    private readonly CompileInputRoot? _trackedRoot;
    private readonly ILookup<string, string> _byFileName;
    private readonly IReadOnlyCollection<string> _includes;
    private readonly Dictionary<string, string?> _hashes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _pchRecords = new(StringComparer.Ordinal);
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedPchFiles = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _trackedInputs = new(StringComparer.Ordinal);
    private HashSet<string> _pchNames = new(StringComparer.Ordinal);
    private string? _pchId;

    /// <summary>
    /// Whether every unit processed in this build had all of its dependencies resolved inside registered roots,
    /// meaning <see cref="TrackedInputs"/> is a complete input closure for module-currency checks.
    /// </summary>
    public bool Complete { get; private set; } = true;

    /// <summary>The first reason a compiled unit was not kept this run (null when every unit was): its source under
    /// no root, a dependency no root holds or that cannot be hashed, an unreadable dependency file, or the missing
    /// precompiled-header record together with <see cref="PchFailure"/>.</summary>
    public string? StoreFailure { get; private set; }

    /// <summary>How many compiled units were not kept this run.</summary>
    public int NotKept { get; private set; }

    /// <summary>Why <see cref="StorePch"/> recorded no precompiled header this run (null when it did or was not called).</summary>
    public string? PchFailure { get; private set; }

    /// <summary>Relative paths read from the tracked primary root (e.g. <c>bundle/</c> or <c>recompile/</c>) across all units.</summary>
    public IReadOnlyCollection<string> TrackedInputs
    {
        get
        {
            lock (_gate)
            {
                return _trackedInputs.ToArray();
            }
        }
    }

    /// <summary>
    /// Opens a content-addressed compile cache for one build run.
    /// </summary>
    /// <param name="root">Cache storage directory on disk; created when absent.</param>
    /// <param name="roots">Ordered input roots (more specific nested roots must precede broader parent roots).</param>
    /// <param name="allTrackedRelativePaths">All relative file paths present under the tracked include root, for <see cref="SameNames"/>.</param>
    /// <param name="includes">Ordered relative include directories under the tracked include root.</param>
    /// <param name="recipe">Hash of compiler identity, target, and flags that affect object output.</param>
    /// <param name="compilerDirectory">The working directory the compiler runs in. A dependency file names an input
    /// relative to it (zig names its own <c>lib/</c> that way when the directory contains it), so relative paths
    /// resolve against this directory, never against this process's current directory.</param>
    public CompileCache(
        string root,
        IReadOnlyList<CompileInputRoot> roots,
        IEnumerable<string> allTrackedRelativePaths,
        IReadOnlyCollection<string> includes,
        string recipe,
        string compilerDirectory)
    {
        _root = root;
        _compilerDirectory = Path.GetFullPath(compilerDirectory);
        _roots = roots;
        _trackedRoot = roots.FirstOrDefault(candidate => candidate.TrackInputsAndShadowing);
        _byFileName = FileNameIndex(allTrackedRelativePaths);
        _includes = includes;
        _recipe = recipe;
        Directory.CreateDirectory(root);
    }

    /// <summary>Indexes relative forward-slash paths by basename in ordinal order for <see cref="SameNames"/>.</summary>
    /// <param name="relativePaths">Forward-slash relative file paths.</param>
    /// <returns>Lookup from file basename to ordered relative paths.</returns>
    public static ILookup<string, string> FileNameIndex(IEnumerable<string> relativePaths) =>
        relativePaths
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToLookup(path => path[(path.LastIndexOf('/') + 1)..], StringComparer.Ordinal);

    /// <summary>Indexes a relative-path hash dictionary by basename in ordinal order for <see cref="SameNames"/>.</summary>
    /// <param name="files">Dictionary mapping forward-slash relative file paths to hashes.</param>
    /// <returns>Lookup from file basename to ordered relative paths.</returns>
    public static ILookup<string, string> FileNameIndex(Dictionary<string, string> files) =>
        FileNameIndex(files.Keys);

    /// <summary>
    /// Computes a shadowing fingerprint over all reachable files sharing a basename with any read input,
    /// detecting newly added headers earlier on the include search path or beside an including file.
    /// </summary>
    /// <param name="trackedPaths">Relative paths read from the tracked root.</param>
    /// <param name="index">Basename lookup built by <see cref="FileNameIndex(IEnumerable{string})"/>.</param>
    /// <param name="includes">Relative include directories under the tracked root.</param>
    /// <returns>Lowercase hex SHA-256 digest of reachable same-named candidates.</returns>
    public static string SameNames(IEnumerable<string> trackedPaths, ILookup<string, string> index, IEnumerable<string> includes)
    {
        var inputs = trackedPaths.ToList();
        var folders = new HashSet<string>(includes, StringComparer.Ordinal);
        foreach (string path in inputs)
        {
            folders.Add(path[..Math.Max(0, path.LastIndexOf('/'))]);
        }

        bool Reachable(string path)
        {
            for (int end = path.LastIndexOf('/'); end > 0; end = path.LastIndexOf('/', end - 1))
            {
                if (folders.Contains(path[..end]))
                {
                    return true;
                }
            }

            return false;
        }

        var text = new StringBuilder();
        foreach (string name in inputs
            .Select(path => path[(path.LastIndexOf('/') + 1)..])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal))
        {
            text.Append(name);
            foreach (string path in index[name].Where(Reachable))
            {
                text.Append('\0').Append(path);
            }

            text.Append('\n');
        }

        return HashText(text.ToString());
    }

    /// <summary>Computes the lowercase hex SHA-256 of UTF-8 text via <see cref="Refii.Core.Engine_Hash"/>.</summary>
    /// <param name="text">Input string.</param>
    /// <returns>64-character lowercase hex SHA-256.</returns>
    public static string HashText(string text) =>
        Convert.ToHexString(Refii.Core.Engine_Hash.Sha256(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>Computes the lowercase hex SHA-256 of a file stream without stat caching (for mutable artifacts like <c>.o</c>).</summary>
    /// <param name="path">File path on disk.</param>
    /// <returns>64-character lowercase hex SHA-256.</returns>
    public static string HashFileDirect(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(Refii.Core.Engine_Hash.Sha256(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// Computes the lowercase hex SHA-256 of an input file, reusing a cached digest only when its full path, byte length,
    /// and UTC last-write ticks are unchanged and strictly older than a 2-second racy-timestamp safety window.
    /// </summary>
    /// <param name="path">File path on disk.</param>
    /// <returns>64-character lowercase hex SHA-256, or null if the file does not exist.</returns>
    public static string? HashFileMemoized(string path)
    {
        string full = Path.GetFullPath(path);
        var info = new FileInfo(full);
        if (!info.Exists)
        {
            StatHashCache.TryRemove(full, out _);
            return null;
        }

        long length = info.Length;
        long ticks = info.LastWriteTimeUtc.Ticks;
        long nowTicks = DateTime.UtcNow.Ticks;
        bool safeFromSameTickEdit = nowTicks - ticks > TimeSpan.TicksPerSecond * 2;
        if (safeFromSameTickEdit &&
            StatHashCache.TryGetValue(full, out var cached) &&
            cached.Length == length &&
            cached.LastWriteUtcTicks == ticks)
        {
            return cached.Hash;
        }

        string hash = HashFileDirect(full);
        if (safeFromSameTickEdit)
        {
            StatHashCache[full] = (length, ticks, hash);
        }
        else
        {
            StatHashCache.TryRemove(full, out _);
        }

        return hash;
    }

    /// <summary>Invalidates all stat-memoized input file hashes (useful if tests rewrite files within identical clock ticks).</summary>
    public static void ClearMemoizedHashes() => StatHashCache.Clear();

    /// <summary>
    /// Looks up a cached object file for <paramref name="source"/> whose recorded source, header, PCH, and shadowing
    /// dependencies all still match current disk state.
    /// </summary>
    /// <param name="source">Source file path.</param>
    /// <param name="extraFlags">Optional per-unit extra compiler flags (e.g. mod includes or chain defines).</param>
    /// <returns>Verified path to the cached <c>.o</c> file, or null on cache miss.</returns>
    public string? Find(string source, IReadOnlyList<string>? extraFlags = null)
    {
        lock (_gate)
        {
            try
            {
                string? name = NameOf(source);
                string? id = name == null ? null : UnitId(name, extraFlags);
                if (id == null)
                {
                    return null;
                }

                string record = Path.Combine(_root, id + ".deps");
                string objectPath = Path.Combine(_root, id + ".o");
                if (!File.Exists(record) || !File.Exists(objectPath))
                {
                    return null;
                }

                var inputs = new List<string>();
                string objectHash = string.Empty;
                foreach (string line in File.ReadLines(record))
                {
                    if (line.StartsWith("object ", StringComparison.Ordinal))
                    {
                        objectHash = line[7..];
                    }
                    else if (line.StartsWith("pch ", StringComparison.Ordinal))
                    {
                        if (!PchRecordHolds(line[4..]))
                        {
                            return null;
                        }
                    }
                    else if (!Holds(line, inputs))
                    {
                        return null;
                    }
                }

                if (objectHash.Length == 0 || HashFileDirect(objectPath) != objectHash)
                {
                    return null;
                }

                _used.Add(id);
                _trackedInputs.UnionWith(inputs);
                AddOwnInput(name!);
                return objectPath;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Looks up a cached binary <c>.pch</c> artifact for <paramref name="headerPath"/> when binary PCH caching is used
    /// (Desktop builds where input/generated directories remain at stable paths across runs).
    /// </summary>
    /// <param name="headerPath">Path to the header being precompiled (e.g. <c>refii_pch.h</c>).</param>
    /// <param name="pathSalt">Path-binding string (generated + recompile directory paths) since Clang embeds paths inside <c>.pch</c>.</param>
    /// <returns>Verified path to the cached <c>.pch</c> file and activated PCH state, or null on miss.</returns>
    public string? FindPchArtifact(string headerPath, string pathSalt)
    {
        lock (_gate)
        {
            try
            {
                string? headerName = NameOf(headerPath);
                if (headerName == null || CurrentHash(headerName) is not { } headerHash)
                {
                    return null;
                }

                string slotKey = HashText(_recipe + "\0pch-binary\0" + headerName + "\0" + headerHash + "\0" + pathSalt)[..32];
                string pointerFile = Path.Combine(_root, "pchbin-" + slotKey + ".ref");
                string binaryFile = Path.Combine(_root, "pchbin-" + slotKey + ".pch");
                if (!File.Exists(pointerFile) || !File.Exists(binaryFile))
                {
                    return null;
                }

                string[] parts = File.ReadAllText(pointerFile).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2)
                {
                    return null;
                }

                string pchId = parts[0];
                string expectedBinaryHash = parts[1];
                if (!PchRecordHolds(pchId))
                {
                    return null;
                }

                if (HashFileDirect(binaryFile) != expectedBinaryHash)
                {
                    return null;
                }

                // Rehydrate _pchNames from pch-<pchId>.deps so new units compiled against this cached PCH omit duplicate PCH deps.
                string pchRecord = Path.Combine(_root, "pch-" + pchId + ".deps");
                var pchNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (string line in File.ReadLines(pchRecord))
                {
                    if (line.StartsWith("names ", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    int space = line.IndexOf(' ');
                    if (space > 0)
                    {
                        pchNames.Add(line[(space + 1)..]);
                    }
                }

                _pchNames = pchNames;
                _pchId = pchId;
                _usedPchFiles.Add("pch-" + pchId + ".deps");
                _usedPchFiles.Add("pchbin-" + slotKey + ".ref");
                _usedPchFiles.Add("pchbin-" + slotKey + ".pch");
                return binaryFile;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Records a newly compiled precompiled header's dependency closure, optionally caching the <c>.pch</c> binary itself.
    /// </summary>
    /// <param name="dependencies">Path to the PCH's <c>clang -MD</c> <c>.d</c> file.</param>
    /// <param name="output">Path to the compiled <c>.pch</c> file.</param>
    /// <param name="headerPathForBinaryCache">Optional header path when storing the binary <c>.pch</c> artifact in the cache.</param>
    /// <param name="pathSalt">Optional path-binding string paired with <paramref name="headerPathForBinaryCache"/>.</param>
    /// <returns>Path to the <c>.pch</c> to pass to <c>-include-pch</c> (cached copy when stored, else <paramref name="output"/>).</returns>
    public string StorePch(
        string dependencies,
        string output,
        string? headerPathForBinaryCache = null,
        string? pathSalt = null)
    {
        lock (_gate)
        {
            _pchNames = new HashSet<string>(StringComparer.Ordinal);
            _pchId = null;
            PchFailure = null;
            if (!TryNames(dependencies, output, null, out var names, out string? failure))
            {
                PchFailure = failure;
                Complete = false;
                return output;
            }

            var text = new StringBuilder();
            AppendRecord(text, names);
            string content = text.ToString();
            string id = HashText(content)[..32];
            string recordFile = "pch-" + id + ".deps";
            WriteAtomic(Path.Combine(_root, recordFile), content);
            _pchNames = names.ToHashSet(StringComparer.Ordinal);
            _pchId = id;
            _pchRecords[id] = true;
            _usedPchFiles.Add(recordFile);
            if (_trackedRoot != null)
            {
                _trackedInputs.UnionWith(names
                    .Where(name => name.StartsWith(_trackedRoot.Prefix, StringComparison.Ordinal))
                    .Select(name => name[_trackedRoot.Prefix.Length..]));
            }

            if (headerPathForBinaryCache != null && pathSalt != null && File.Exists(output))
            {
                try
                {
                    string? headerName = NameOf(headerPathForBinaryCache);
                    if (headerName != null && CurrentHash(headerName) is { } headerHash)
                    {
                        string slotKey = HashText(_recipe + "\0pch-binary\0" + headerName + "\0" + headerHash + "\0" + pathSalt)[..32];
                        string binaryName = "pchbin-" + slotKey + ".pch";
                        string refName = "pchbin-" + slotKey + ".ref";
                        string binaryTarget = Path.Combine(_root, binaryName);
                        string tempBinary = binaryTarget + ".tmp";
                        File.Copy(output, tempBinary, overwrite: true);
                        File.Move(tempBinary, binaryTarget, overwrite: true);
                        string binaryHash = HashFileDirect(binaryTarget);
                        WriteAtomic(Path.Combine(_root, refName), id + " " + binaryHash + "\n");
                        _usedPchFiles.Add(binaryName);
                        _usedPchFiles.Add(refName);
                        return binaryTarget;
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Non-fatal: keep using caller's local PCH file for this build.
                }
            }

            return output;
        }
    }

    /// <summary>
    /// Moves a freshly compiled object file into the cache when all of its <c>clang -MD</c> dependencies can be named.
    /// </summary>
    /// <param name="source">Source file path.</param>
    /// <param name="built">Path to the newly compiled <c>.o</c> file.</param>
    /// <param name="dependencies">Path to its <c>clang -MD</c> <c>.d</c> file.</param>
    /// <param name="usesPch">Whether the unit was compiled with <c>-include-pch</c>.</param>
    /// <param name="extraFlags">Optional per-unit extra compiler flags.</param>
    /// <returns>Final path of the object file (inside cache when stored, or <paramref name="built"/> when uncacheable).</returns>
    public string Store(
        string source,
        string built,
        string dependencies,
        bool usesPch,
        IReadOnlyList<string>? extraFlags = null)
    {
        lock (_gate)
        {
            try
            {
                string? name = NameOf(source);
                string? id = name == null ? null : UnitId(name, extraFlags);
                string? failure = null;
                List<string> names = new();
                if (id == null)
                {
                    failure = (name == null ? "a source under no root: " : "a source that cannot be hashed: ") + source;
                }
                else if (!TryNames(dependencies, built, name, out names, out string? namesFailure))
                {
                    failure = namesFailure + " for " + source;
                }
                else if (usesPch && _pchId == null)
                {
                    failure = "no precompiled-header record for " + source + ": "
                        + (PchFailure ?? "the header was not recorded this build");
                }

                if (failure != null)
                {
                    StoreFailure ??= failure;
                    NotKept++;
                    Complete = false;
                    return built;
                }

                var own = usesPch ? names.Where(input => !_pchNames.Contains(input)).ToList() : names;
                var text = new StringBuilder();
                text.Append("object ").Append(HashFileDirect(built)).Append('\n');
                if (usesPch)
                {
                    text.Append("pch ").Append(_pchId).Append('\n');
                }

                AppendRecord(text, own);
                string target = Path.Combine(_root, id + ".o");
                File.Move(built, target, overwrite: true);
                WriteAtomic(Path.Combine(_root, id + ".deps"), text.ToString());
                _used.Add(id);
                if (_trackedRoot != null)
                {
                    _trackedInputs.UnionWith(own
                        .Where(input => input.StartsWith(_trackedRoot.Prefix, StringComparison.Ordinal))
                        .Select(input => input[_trackedRoot.Prefix.Length..]));
                }

                AddOwnInput(name!);
                return target;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                StoreFailure ??= source + " could not be written to the cache: " + error.Message;
                NotKept++;
                Complete = false;
                return File.Exists(built) ? built : throw new IOException("A compiled unit could not be kept", error);
            }
        }
    }

    /// <summary>
    /// Prunes unused cache entries after a successful build.
    /// </summary>
    /// <param name="maxGenerations">
    /// Number of successful build generations to retain (<c>1</c> keeps only entries used by this build, matching Android;
    /// <c>&gt; 1</c> retains recent generations on Desktop so toggling mods or configs reuses objects immediately).
    /// </param>
    /// <param name="maxBytes">Optional total byte budget across retained <c>.o</c>/<c>.pch</c> files (<c>0</c> = unlimited).</param>
    /// <param name="preservedFileNames">Optional non-object filenames in <c>root</c> to leave untouched (e.g. <c>codegen.partition.json</c>).</param>
    public void KeepOnlyUsed(
        int maxGenerations = 1,
        long maxBytes = 0,
        IReadOnlySet<string>? preservedFileNames = null)
    {
        lock (_gate)
        {
            if (maxGenerations <= 1 && maxBytes <= 0)
            {
                PruneSingleGeneration(preservedFileNames);
                return;
            }

            PruneMultiGeneration(Math.Max(1, maxGenerations), maxBytes, preservedFileNames);
        }
    }

    private void PruneSingleGeneration(IReadOnlySet<string>? preservedFileNames)
    {
        var pchRecords = new HashSet<string>(_usedPchFiles, StringComparer.Ordinal);
        foreach (string id in _used)
        {
            string record = Path.Combine(_root, id + ".deps");
            if (!File.Exists(record))
            {
                continue;
            }

            foreach (string line in File.ReadLines(record))
            {
                if (line.StartsWith("pch ", StringComparison.Ordinal))
                {
                    pchRecords.Add("pch-" + line[4..] + ".deps");
                }
            }
        }

        foreach (string file in Directory.EnumerateFiles(_root).ToList())
        {
            string name = Path.GetFileName(file);
            if (name == CodegenTransforms.PartitionFile || (preservedFileNames != null && preservedFileNames.Contains(name)))
            {
                continue;
            }

            bool kept = name.StartsWith("pch-", StringComparison.Ordinal) || name.StartsWith("pchbin-", StringComparison.Ordinal)
                ? pchRecords.Contains(name)
                : _used.Contains(Path.GetFileNameWithoutExtension(name));
            if (!kept)
            {
                File.Delete(file);
            }
        }
    }

    private void PruneMultiGeneration(int maxGenerations, long maxBytes, IReadOnlySet<string>? preservedFileNames)
    {
        string genPath = Path.Combine(_root, GenerationsFileName);
        long currentGen = 1;
        var unitGens = new Dictionary<string, long>(StringComparer.Ordinal);
        var pchFileGens = new Dictionary<string, long>(StringComparer.Ordinal);

        if (File.Exists(genPath))
        {
            foreach (string line in File.ReadLines(genPath))
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[0] == "gen" && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedGen))
                {
                    currentGen = parsedGen + 1;
                }
                else if (parts.Length == 3 && long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long itemGen))
                {
                    if (parts[0] == "unit")
                    {
                        unitGens[parts[1]] = itemGen;
                    }
                    else if (parts[0] == "pch")
                    {
                        pchFileGens[parts[1]] = itemGen;
                    }
                }
            }
        }

        foreach (string id in _used)
        {
            unitGens[id] = currentGen;
        }

        foreach (string pchFile in _usedPchFiles)
        {
            pchFileGens[pchFile] = currentGen;
        }

        long minGen = currentGen - maxGenerations + 1;
        var keptUnits = unitGens
            .Where(pair => pair.Value >= minGen && File.Exists(Path.Combine(_root, pair.Key + ".o")) && File.Exists(Path.Combine(_root, pair.Key + ".deps")))
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .ToList();

        var keptUnitSet = new HashSet<string>(StringComparer.Ordinal);
        long accumulatedBytes = 0;
        foreach (var (id, gen) in keptUnits)
        {
            long size = new FileInfo(Path.Combine(_root, id + ".o")).Length;
            if (maxBytes > 0 && gen < currentGen && accumulatedBytes + size > maxBytes)
            {
                continue;
            }

            accumulatedBytes += size;
            keptUnitSet.Add(id);
        }

        var neededPchFiles = new HashSet<string>(
            pchFileGens.Where(pair => pair.Value >= minGen).Select(pair => pair.Key),
            StringComparer.Ordinal);
        foreach (string id in keptUnitSet)
        {
            string record = Path.Combine(_root, id + ".deps");
            if (!File.Exists(record))
            {
                continue;
            }

            foreach (string line in File.ReadLines(record))
            {
                if (line.StartsWith("pch ", StringComparison.Ordinal))
                {
                    neededPchFiles.Add("pch-" + line[4..] + ".deps");
                }
            }
        }

        // Enforce byte budget on older-generation binary .pch files as well.
        foreach (string pchName in neededPchFiles.Where(name => name.EndsWith(".pch", StringComparison.Ordinal)).ToList())
        {
            string full = Path.Combine(_root, pchName);
            if (!File.Exists(full))
            {
                neededPchFiles.Remove(pchName);
                neededPchFiles.Remove(Path.ChangeExtension(pchName, ".ref"));
                continue;
            }

            long gen = pchFileGens.GetValueOrDefault(pchName, currentGen);
            long size = new FileInfo(full).Length;
            if (maxBytes > 0 && gen < currentGen && accumulatedBytes + size > maxBytes)
            {
                neededPchFiles.Remove(pchName);
                neededPchFiles.Remove(Path.ChangeExtension(pchName, ".ref"));
            }
            else
            {
                accumulatedBytes += size;
            }
        }

        foreach (string file in Directory.EnumerateFiles(_root).ToList())
        {
            string name = Path.GetFileName(file);
            if (name == CodegenTransforms.PartitionFile ||
                name == GenerationsFileName ||
                (preservedFileNames != null && preservedFileNames.Contains(name)))
            {
                continue;
            }

            bool kept = name.StartsWith("pch-", StringComparison.Ordinal) || name.StartsWith("pchbin-", StringComparison.Ordinal)
                ? neededPchFiles.Contains(name)
                : (name.EndsWith(".o", StringComparison.Ordinal) || name.EndsWith(".deps", StringComparison.Ordinal))
                    && keptUnitSet.Contains(Path.GetFileNameWithoutExtension(name));
            if (!kept)
            {
                File.Delete(file);
            }
        }

        var state = new StringBuilder();
        state.Append("gen ").Append(currentGen.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (string id in keptUnitSet.OrderBy(id => id, StringComparer.Ordinal))
        {
            long gen = unitGens.GetValueOrDefault(id, currentGen);
            state.Append("unit ").Append(id).Append(' ').Append(gen.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        foreach (string pchName in neededPchFiles.OrderBy(name => name, StringComparer.Ordinal))
        {
            if (File.Exists(Path.Combine(_root, pchName)))
            {
                long gen = pchFileGens.GetValueOrDefault(pchName, currentGen);
                state.Append("pch ").Append(pchName).Append(' ').Append(gen.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
        }

        WriteAtomic(genPath, state.ToString());
    }

    private string? UnitId(string name, IReadOnlyList<string>? extraFlags)
    {
        if (CurrentHash(name) is not { } hash)
        {
            return null;
        }

        if (extraFlags == null || extraFlags.Count == 0)
        {
            return HashText(_recipe + "\0" + name + "\0" + hash)[..32];
        }

        var key = new StringBuilder(_recipe).Append('\0').Append(name).Append('\0').Append(hash).Append("\0flags");
        foreach (string flag in extraFlags)
        {
            key.Append('\0').Append(flag);
        }

        return HashText(key.ToString())[..32];
    }

    private void AddOwnInput(string name)
    {
        if (_trackedRoot != null && name.StartsWith(_trackedRoot.Prefix, StringComparison.Ordinal))
        {
            _trackedInputs.Add(name[_trackedRoot.Prefix.Length..]);
        }
    }

    private string? NameOf(string path)
    {
        string full = Path.GetFullPath(path);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        foreach (CompileInputRoot root in _roots)
        {
            if (full.StartsWith(root.DirectoryPath + Path.DirectorySeparatorChar, comparison))
            {
                string relative = full[(root.DirectoryPath.Length + 1)..].Replace('\\', '/');
                return root.Prefix + root.NormalizeRelative(relative);
            }
        }

        return null;
    }

    private CompileInputRoot? RootForName(string name)
    {
        foreach (CompileInputRoot root in _roots)
        {
            if (name.StartsWith(root.Prefix, StringComparison.Ordinal))
            {
                return root;
            }
        }

        return null;
    }

    private string? CurrentHash(string name)
    {
        if (_hashes.TryGetValue(name, out string? known))
        {
            return known;
        }

        CompileInputRoot? root = RootForName(name);
        if (root == null)
        {
            return null;
        }

        string relative = name[root.Prefix.Length..];
        string? hash;
        if (root.PrecomputedHashes != null)
        {
            hash = root.PrecomputedHashes.TryGetValue(relative, out string? recorded) ? recorded : null;
        }
        else if (root.IsImmutableToolchain)
        {
            string diskPath = Path.Combine(root.DirectoryPath, relative.Replace('/', Path.DirectorySeparatorChar));
            hash = File.Exists(diskPath) ? ToolchainToken : null;
        }
        else
        {
            string diskPath = Path.Combine(root.DirectoryPath, relative.Replace('/', Path.DirectorySeparatorChar));
            hash = HashFileMemoized(diskPath);
        }

        _hashes[name] = hash;
        return hash;
    }

    private bool Holds(string line, List<string> inputs)
    {
        if (line.StartsWith("names ", StringComparison.Ordinal))
        {
            return _trackedRoot == null || SameNames(inputs, _byFileName, _includes) == line[6..];
        }

        int space = line.IndexOf(' ');
        if (space <= 0)
        {
            throw new FormatException("Malformed compile cache record");
        }

        string name = line[(space + 1)..];
        if (CurrentHash(name) != line[..space])
        {
            return false;
        }

        if (_trackedRoot != null && name.StartsWith(_trackedRoot.Prefix, StringComparison.Ordinal))
        {
            inputs.Add(name[_trackedRoot.Prefix.Length..]);
        }

        return true;
    }

    private bool PchRecordHolds(string id)
    {
        if (_pchRecords.TryGetValue(id, out bool known))
        {
            return known;
        }

        string record = Path.Combine(_root, "pch-" + id + ".deps");
        if (!File.Exists(record))
        {
            return false;
        }

        var own = new List<string>();
        bool holds = File.ReadLines(record).All(line => Holds(line, own));
        _pchRecords[id] = holds;
        if (holds)
        {
            _trackedInputs.UnionWith(own);
        }

        return holds;
    }

    /// <summary>
    /// Names every input in a <c>clang -MD</c> dependency file by its root prefix. A relative path is relative to
    /// the compiler's working directory. Fails, saying why, on a missing file or on any input that no root holds
    /// or that cannot be hashed: a unit whose inputs cannot all be named cannot be proven current later.
    /// </summary>
    private bool TryNames(string dependencies, string output, string? source, out List<string> names, out string? failure)
    {
        names = new List<string>();
        failure = null;
        if (!File.Exists(dependencies))
        {
            failure = "no dependency file at " + dependencies;
            return false;
        }

        var set = new SortedSet<string>(StringComparer.Ordinal);
        if (source != null)
        {
            set.Add(source);
        }

        string outputFull = Path.GetFullPath(output, _compilerDirectory);
        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        foreach (string path in CompileDepfile.Parse(File.ReadAllText(dependencies)))
        {
            string full = Path.GetFullPath(path, _compilerDirectory);
            if (string.Equals(full, outputFull, pathComparison) || full.EndsWith(".pch", pathComparison))
            {
                continue;
            }

            string? name = NameOf(full);
            if (name == null || CurrentHash(name) == null)
            {
                failure = (name == null ? "an input under no root: " : "an input that cannot be hashed: ") + full
                    + (path == full ? string.Empty : " (written as " + path + ")");
                return false;
            }

            set.Add(name);
        }

        names = set.ToList();
        return true;
    }

    private void AppendRecord(StringBuilder text, IReadOnlyCollection<string> names)
    {
        foreach (string name in names)
        {
            text.Append(CurrentHash(name)).Append(' ').Append(name).Append('\n');
        }

        if (_trackedRoot != null)
        {
            string prefix = _trackedRoot.Prefix;
            text.Append("names ")
                .Append(SameNames(
                    names.Where(name => name.StartsWith(prefix, StringComparison.Ordinal)).Select(name => name[prefix.Length..]),
                    _byFileName,
                    _includes))
                .Append('\n');
        }
    }

    private static void WriteAtomic(string path, string text)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, path, overwrite: true);
    }
}
