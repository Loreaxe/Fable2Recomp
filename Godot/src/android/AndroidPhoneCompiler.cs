using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Refii.AndroidRuntime;

/// <summary>Compiles privately supplied game executables with APK-distributed Android tools.</summary>
/// <remarks>Each attempt owns a new directory. It never changes the installed module,
/// runtime configuration, source game or saves. Activation is a separate launcher action.
/// Compiler jobs are limited by the memory free (<see cref="PlanJobs"/>); cancellation terminates only owned children.</remarks>
public static class AndroidPhoneCompiler
{
    /// <summary>Physical roots for shipped tools, user inputs and new private output.</summary>
    /// <param name="BundleDirectory">Extracted public headers, sources, sysroot and manifest.</param>
    /// <param name="NativeDirectory">Read-only APK native library directory, containing executable tools.</param>
    /// <param name="GameDirectory">Installed game containing the user's default.xex and optional default.xexp.</param>
    /// <param name="PrivateDirectory">Parent for unique private compilation attempts.</param>
    /// <param name="Title">The game being compiled; Fable 2 when unset.</param>
    /// <param name="ObjectCache">Directory that keeps compiled units between builds (<see cref="AndroidCompileCache"/>); null keeps none.</param>
    /// <param name="Jobs">Compilers at once; 0 plans it from free memory for the real tools, and runs one for an injected runner.</param>
    /// <param name="Mods">Adds the enabled hard mods' native sources once the game code is generated; null adds none.</param>
    public sealed record Request(string BundleDirectory, string NativeDirectory,
        string GameDirectory, string PrivateDirectory, Refii.Core.Engine_Title? Title = null,
        string? ObjectCache = null, int Jobs = 0, AddMods? Mods = null)
    {
        /// <summary>Whether this compiles Fable 3: its own version config, hooks, includes, bridge and link set.</summary>
        internal bool IsFable3 => Title == Refii.Core.Engine_Title.Fable3;

        /// <summary>The bridge the module is loaded after: Fable 3's is librefiii_hostbridge.so (cmake/refii_android.cmake).</summary>
        internal string HostBridge => IsFable3 ? "librefiii_hostbridge.so" : "librefii_hostbridge.so";
    }

    /// <summary>Validated output of a completed attempt; no active installation is replaced.</summary>
    /// <param name="Directory">Attempt directory, also retained after failures for diagnostics.</param>
    /// <param name="Module">Compiled ARM64 module in that directory.</param>
    /// <param name="Sha256">Hash of the completed module bytes.</param>
    /// <param name="Version">Exact executable layout compiled by the generator.</param>
    public sealed record Result(string Directory, string Module, string Sha256, string Version)
    {
        /// <summary>Identity of the APK compiler bundle that built the module (<see cref="BundleIdentity"/>);
        /// empty for a module from before this was recorded. A new APK that ships other hooks or another
        /// config makes the module stale (<see cref="AndroidPhoneLauncher"/>).</summary>
        // Source-generated JSON writes every init property, null when the field is absent (an
        // installer state from an older APK), so null is read as unknown here.
        public string Bundle { get => _bundle; init => _bundle = value ?? ""; }
        private readonly string _bundle = "";

        /// <summary>The hard mods compiled in (<see cref="ModSources.Hash"/>); empty for none, and for a
        /// module from before this was recorded, which had none.</summary>
        public string Mods { get => _mods; init => _mods = value ?? ""; }
        private readonly string _mods = "";
    }

    /// <summary>Hard mods' native sources for one build, as the desktop's build adds them
    /// (<see cref="Refii.Launcher.Recompile.GameCodeBuild"/>).</summary>
    /// <param name="Sources">Further units: each mod's sources, extracted under the attempt's generated code, and
    /// the chain entries when two owners hook one function.</param>
    /// <param name="Arguments">Compiler arguments of one unit only, by its path: a mod's own and required
    /// headers, its name, and the chain defines of every unit (refii's hooks included) a chain renames.</param>
    /// <param name="Hash">The compiled set's fingerprint (<see cref="Refii.Mods.ModNative.HashSet"/>); empty for none.</param>
    public sealed record ModSources(IReadOnlyList<string> Sources,
        IReadOnlyDictionary<string, List<string>> Arguments, string Hash);

    /// <summary>Plans the hard mods for a build; throws <see cref="InvalidDataException"/> to refuse it.</summary>
    /// <param name="generated">The attempt's generated game code.</param>
    /// <param name="versionConfig">The attempt's version config, for the names it gives functions.</param>
    /// <param name="hookSources">Refii's own hook sources this build compiles.</param>
    public delegate ModSources AddMods(string generated, string versionConfig, IReadOnlyList<string> hookSources);

    /// <summary>A compiler bundle's identity: what it holds, not how it was zipped.</summary>
    /// <remarks>Two APK builds from the same sources packed byte-different data.zip files (the entries'
    /// times), and an identity from the zip's bytes made every update recompile for about 25 minutes.
    /// The sorted entry names, sizes and CRC-32s change only when a file does.</remarks>
    /// <param name="archive">The APK's res://phonecompiler/data.zip.</param>
    public static string BundleIdentity(System.IO.Compression.ZipArchive archive)
    {
        var text = new StringBuilder();
        foreach (var entry in archive.Entries.OrderBy(entry => entry.FullName, StringComparer.Ordinal))
            text.Append(entry.FullName).Append('\0').Append(entry.Length).Append('\0').Append(entry.Crc32).Append('\n');
        // Engine_Hash, never System.Security.Cryptography: the NativeAOT Android build has no OpenSSL,
        // and the launcher aborted at start ("libssl.so.10" not found) when this called SHA256.HashData.
        return Convert.ToHexString(Refii.Core.Engine_Hash.Sha256(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    /// <summary>The identity an extracted bundle was written with ("extracted.complete"), or empty.</summary>
    /// <param name="bundleDirectory">Extracted compiler bundle.</param>
    public static string RecordedBundle(string bundleDirectory)
    {
        string marker = Path.Combine(bundleDirectory, "extracted.complete");
        return File.Exists(marker) ? File.ReadAllText(marker).Trim() : "";
    }

    /// <summary>A tool invocation without shell interpretation or inherited desktop search paths.</summary>
    /// <param name="Executable">APK-distributed executable path.</param>
    /// <param name="Arguments">Individual arguments, including paths with spaces.</param>
    /// <param name="Directory">Fresh attempt's working directory.</param>
    /// <param name="NativeDirectory">Dependency lookup for the child only.</param>
    /// <param name="Log">Unique output log path.</param>
    public sealed record Command(string Executable, IReadOnlyList<string> Arguments,
        string Directory, string NativeDirectory, string Log);

    /// <summary>Runs a real child or an injected synthetic lifecycle fixture.</summary>
    /// <param name="command">Validated command and owned log.</param>
    /// <param name="cancellation">Cancellation of this attempt.</param>
    /// <returns>Actual tool exit status.</returns>
    public delegate Task<int> Execute(Command command, CancellationToken cancellation);

    /// <summary>Version-one payload accessor; returns immutable image metadata without starting guest execution.</summary>
    /// <returns>The descriptor pointer owned by the loaded module.</returns>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ImageConfig();

    /// <summary>Android ARM64 RTLD_NOW | RTLD_GLOBAL, matching the production native payload loader.</summary>
    public const int AndroidPayloadOpenFlags = 0x00002 | 0x00100;

    /// <summary>Opens a module through Android's public native loader with explicit production flags.</summary>
    [DllImport("libdl.so", EntryPoint = "dlopen", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr AndroidDlopen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    /// <summary>Resolves an export from a raw Android dlopen handle without Mono handle translation.</summary>
    [DllImport("libdl.so", EntryPoint = "dlsym", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr AndroidDlsym(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string symbol);

    /// <summary>Reads and clears this thread's Android loader error.</summary>
    [DllImport("libdl.so", EntryPoint = "dlerror", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr AndroidDlerror();

    /// <summary>Reads one auxiliary-vector entry of this process.</summary>
    [DllImport("libc.so", EntryPoint = "getauxval", CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong AndroidGetauxval(ulong type);

    /// <summary>Whether this phone's CPU has ARMv8.1 LSE atomics (AT_HWCAP bit HWCAP_ATOMICS).</summary>
    /// <remarks>The module is compiled on the phone that runs it, so it can target that CPU, as the
    /// desktop installer targets its own with -march=native. Every ARMv8.2 phone core has LSE.</remarks>
    public static bool HasLseAtomics() => Refii.Core.Engine_Platform.IsAndroid &&
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 && (AndroidGetauxval(16) & (1UL << 8)) != 0;

    /// <summary>Uses the same native loader contract as guest boot, retaining ownership at the caller.</summary>
    /// <param name="module">Hash-validated absolute APK runtime or private payload path.</param>
    /// <returns>The platform loader handle; its lifetime is the engine process.</returns>
    private static IntPtr OpenPlatformModule(string module)
    {
        if (!Refii.Core.Engine_Platform.IsAndroid) return NativeLibrary.Load(module);
        if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            throw new PlatformNotSupportedException("Phone compilation requires Android ARM64");
        IntPtr handle = AndroidDlopen(module, AndroidPayloadOpenFlags);
        if (handle == IntPtr.Zero)
            throw new DllNotFoundException(Marshal.PtrToStringUTF8(AndroidDlerror()) ?? "Android dlopen failed");
        return handle;
    }

    /// <summary>Resolves the metadata accessor using the handle format returned by the selected loader.</summary>
    /// <param name="handle">Opaque platform handle retained by the lifetime table.</param>
    /// <param name="symbol">Fixed versioned payload ABI export.</param>
    /// <returns>The native entry point, or an exception containing the loader diagnostic.</returns>
    private static IntPtr PlatformExport(IntPtr handle, string symbol)
    {
        if (!Refii.Core.Engine_Platform.IsAndroid) return NativeLibrary.GetExport(handle, symbol);
        AndroidDlerror();
        IntPtr entry = AndroidDlsym(handle, symbol);
        if (entry == IntPtr.Zero)
            throw new EntryPointNotFoundException(Marshal.PtrToStringUTF8(AndroidDlerror()) ?? "Android dlsym failed");
        return entry;
    }

    /// <summary>Pins verified native modules while the owning engine process remains alive.</summary>
    /// <remarks>Verification transfers each loader reference to this table so native
    /// descriptors and registered callbacks remain valid for the engine process.
    /// A small explicit limit bounds retained mappings; process exit performs cleanup.</remarks>
    public sealed class ModuleLifetime
    {
        private readonly Func<string, IntPtr> _load;
        private readonly Func<string, IntPtr, bool> _check;
        private readonly int _limit;
        private readonly object _gate = new();
        private readonly Dictionary<string, (string Hash, IntPtr Handle, bool Verified)> _modules = new();

        /// <summary>Creates an engine-lifetime binding table with injectable native operations.</summary>
        /// <param name="load">Loads one module and transfers its reference to this table.</param>
        /// <param name="check">Checks the metadata accessor without starting guest execution.</param>
        /// <param name="limit">Maximum distinct mappings before the launcher must restart.</param>
        public ModuleLifetime(Func<string, IntPtr> load, Func<string, IntPtr, bool> check, int limit = 4)
        {
            if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
            _load = load; _check = check; _limit = limit;
        }

        /// <summary>Checks a stable module once, retaining even failed loaded handles for safe engine lifetime.</summary>
        /// <param name="module">Canonical owned module path.</param>
        /// <param name="digest">Hash of those exact bytes; replacing a mapped file is rejected.</param>
        /// <returns>Whether the native metadata check succeeds.</returns>
        public bool Verify(string module, string digest)
        {
            lock (_gate)
            {
                if (_modules.TryGetValue(module, out var existing))
                {
                    if (existing.Hash != digest) throw new InvalidDataException("A loaded module's bytes changed; restart the launcher");
                    if (existing.Verified) return true;
                }
                else
                {
                    if (_modules.Count >= _limit) throw new InvalidDataException("Restart the launcher before verifying another build");
                    IntPtr handle = _load(module);
                    if (handle == IntPtr.Zero) throw new InvalidDataException("Native loader returned an empty handle");
                    existing = (digest, handle, false);
                    _modules.Add(module, existing);
                }
                bool verified = _check(module, existing.Handle);
                _modules[module] = (digest, existing.Handle, verified);
                return verified;
            }
        }
    }

    private static readonly ModuleLifetime LoadedRuntime = new(OpenPlatformModule, (_, _) => true, 1);

    private static readonly ModuleLifetime LoadedModules = new(module =>
    {
        LoadEvent(module, "load_begin");
        IntPtr handle = OpenPlatformModule(module);
        LoadEvent(module, "load_returned");
        return handle;
    }, (module, handle) =>
    {
        LoadEvent(module, "accessor_lookup_begin");
        IntPtr entry = PlatformExport(handle, "refii_payload_v1_image_config");
        LoadEvent(module, "accessor_lookup_returned");
        IntPtr descriptor = Marshal.GetDelegateForFunctionPointer<ImageConfig>(entry)();
        LoadEvent(module, descriptor != IntPtr.Zero ? "accessor_nonnull" : "accessor_null");
        return descriptor != IntPtr.Zero;
    });

    /// <summary>Public bundle identities and source recipe; game-derived files are absent.</summary>
    public sealed class BundleManifest
    {
        /// <summary>Supported manifest format.</summary>
        public int Schema { get; set; }
        /// <summary>Target architecture.</summary>
        public string Abi { get; set; } = "";
        /// <summary>Native API floor, matching the runtime.</summary>
        public int Api { get; set; }
        /// <summary>Pinned NDK revision used for tools and sysroot.</summary>
        public string Ndk { get; set; } = "";
        /// <summary>The ReXGlue SDK revision the phone tools were built from (build-phone-tools.py), carried
        /// forward from the tools the bundle takes; only its form is checked.</summary>
        public string SdkPin { get; set; } = "";
        /// <summary>Ordered public include directories, with bridge headers first.</summary>
        public string[] Includes { get; set; } = Array.Empty<string>();
        /// <summary>Public hook source paths, relative to the bundle.</summary>
        public string[] Hooks { get; set; } = Array.Empty<string>();
        /// <summary>Fable 3's include directories (refiii_hooks'), in its build's order.</summary>
        public string[] Fable3Includes { get; set; } = Array.Empty<string>();
        /// <summary>Fable 3's hook source paths, relative to the bundle; empty in a bundle from before Fable 3.</summary>
        public string[] Fable3Hooks { get; set; } = Array.Empty<string>();
        /// <summary>Hashes of all extracted public build inputs.</summary>
        public Dictionary<string, string> Files { get; set; } = new();
        /// <summary>Hashes of the matched APK runtime closure and executable tools.</summary>
        public Dictionary<string, string> NativeFiles { get; set; } = new();
    }

    /// <summary>Validates bundle pins and hashes before invoking any compiler or generator.</summary>
    /// <param name="request">Physical, separate input/output roots.</param>
    /// <returns>The complete matched public build recipe.</returns>
    public static BundleManifest Validate(Request request)
    {
        foreach (string root in new[] { request.BundleDirectory, request.NativeDirectory,
                     request.GameDirectory, request.PrivateDirectory })
            AndroidRuntimeOptions.PhysicalPath(root, "phone compilation root");
        if (Overlaps(request.PrivateDirectory, request.GameDirectory) ||
            Overlaps(request.PrivateDirectory, request.BundleDirectory) ||
            Overlaps(request.PrivateDirectory, request.NativeDirectory))
            throw new InvalidDataException("Compilation output must be separate from its inputs");
        if (request.ObjectCache != null && (Overlaps(request.ObjectCache, request.GameDirectory) ||
            Overlaps(request.ObjectCache, request.BundleDirectory) || Overlaps(request.ObjectCache, request.NativeDirectory) ||
            Overlaps(request.ObjectCache, request.PrivateDirectory)))
            throw new InvalidDataException("The compile cache must be separate from build inputs and attempts");
        var manifest = JsonSerializer.Deserialize(
            File.ReadAllText(Path.Combine(request.BundleDirectory, "bundle.json")), AndroidJsonContext.Default.BundleManifest)
            ?? throw new InvalidDataException("Missing compiler bundle manifest");
        if (manifest.Schema != 1 || manifest.Abi != "arm64-v8a" || manifest.Api != 31 ||
            manifest.Ndk != "28.2.13676358" || !Regex.IsMatch(manifest.SdkPin, "^[0-9a-f]{40}$"))
            throw new InvalidDataException("Compiler bundle does not match the pinned ARM64/API31 runtime");
        if (manifest.Files.Count == 0 || manifest.Hooks.Length == 0 || manifest.Includes.Length == 0)
            throw new InvalidDataException("Incomplete compiler bundle recipe");
        foreach (var pair in manifest.Files) Verify(request.BundleDirectory, pair.Key, pair.Value);
        foreach (var pair in manifest.NativeFiles) Verify(request.NativeDirectory, pair.Key, pair.Value);
        foreach (string name in new[] { "librefii_clang.so", "librefii_lld.so", "librefii_codegen.so",
                     "librefii_core.so", "librefii_runtime.so", "librefii_hostbridge.so", "librexruntime.so",
                     "librefii_lua.so", "librefii_vfs.so", "libc++_shared.so" })
            if (!manifest.NativeFiles.ContainsKey(name)) throw new InvalidDataException("Unverified tool/runtime: " + name);
        foreach (string path in manifest.Includes)
            if (!System.IO.Directory.Exists(Below(request.BundleDirectory, path)))
                throw new InvalidDataException("Missing include directory: " + path);
        foreach (string path in manifest.Hooks)
            if (!manifest.Files.ContainsKey(path) || !path.EndsWith(".cpp", StringComparison.Ordinal))
                throw new InvalidDataException("Unverified hook source: " + path);
        foreach (string path in new[] { "src/payload/src/payload_export.cpp", "tools/android/payload_abi_guard.h",
                     "versions/Fable2/refii_manifest.toml", "versions/Fable2TU1/refii_manifest.toml" })
            if (!manifest.Files.ContainsKey(path)) throw new InvalidDataException("Missing build input: " + path);
        if (request.IsFable3)
        {
            if (manifest.Fable3Hooks.Length == 0 || manifest.Fable3Includes.Length == 0 ||
                !manifest.Files.ContainsKey("versions/Fable3/refiii_manifest.toml") ||
                !manifest.Files.ContainsKey("versions/Fable3TU3/refiii_manifest.toml") ||
                !manifest.NativeFiles.ContainsKey(request.HostBridge) ||
                !manifest.NativeFiles.ContainsKey(Fable3Vfs))
                throw new InvalidDataException("This APK's compiler bundle cannot build Fable 3");
            foreach (string path in manifest.Fable3Includes)
                if (!System.IO.Directory.Exists(Below(request.BundleDirectory, path)))
                    throw new InvalidDataException("Missing include directory: " + path);
            foreach (string path in manifest.Fable3Hooks)
                if (!manifest.Files.ContainsKey(path) || !path.EndsWith(".cpp", StringComparison.Ordinal))
                    throw new InvalidDataException("Unverified hook source: " + path);
        }
        return manifest;
    }

    /// <summary>Generates, precompiles, compiles and links a new module without changing the active install.</summary>
    /// <param name="request">Explicit private inputs and public compiler roots.</param>
    /// <param name="progress">Receives phase descriptions and completed translation unit counts.</param>
    /// <param name="cancellation">Terminates owned work; partial output is retained.</param>
    /// <param name="execute">Optional synthetic runner for unit/feature tests.</param>
    /// <param name="verifyLoad">Optional synthetic loader check for failure and publication lifecycle tests.</param>
    /// <returns>The completed, structurally verified module and provenance hash.</returns>
    public static async Task<Result> CompileAsync(Request request, Action<string> progress,
        CancellationToken cancellation, Execute? execute = null, Func<string, bool>? verifyLoad = null)
    {
        cancellation.ThrowIfCancellationRequested();
        BundleManifest bundle = Validate(request);
        string source = Path.Combine(request.GameDirectory, "default.xex");
        if (!File.Exists(source)) throw new FileNotFoundException("Select game files containing default.xex", source);
        string attempt = Path.Combine(request.PrivateDirectory, "phone-build-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(attempt);
        File.WriteAllText(Path.Combine(attempt, ".refii-build-owner"), Path.GetFileName(attempt));
        string input = Path.Combine(attempt, "input");
        Directory.CreateDirectory(input);
        foreach (string name in new[] { "default.xex", "default.xexp" })
            if (File.Exists(Path.Combine(request.GameDirectory, name)))
                File.Copy(Path.Combine(request.GameDirectory, name), Path.Combine(input, name), false);
        // The title's own version folder for what was supplied; its files follow its key (refiii_*.toml).
        Refii.Core.Engine_Title title = request.Title ?? Refii.Core.Engine_Title.Fable2;
        string version = title.VersionFolder(File.Exists(Path.Combine(input, "default.xexp")));
        string key = title.Key;
        string generated = Path.Combine(attempt, "generated");
        string config = Below(request.BundleDirectory, "versions/" + version);
        File.Copy(Path.Combine(config, key + "_config.toml"), Path.Combine(attempt, key + "_config.toml"), false);
        string manifest = File.ReadAllText(Path.Combine(config, key + "_manifest.toml"));
        manifest = RewriteAssignment(manifest, "file_path", Path.Combine(input, "default.xex"));
        manifest = RewriteAssignment(manifest, "out_directory_path", generated);
        string manifestPath = Path.Combine(attempt, "refii_manifest.toml");
        File.WriteAllText(manifestPath, manifest);
        Execute runner = execute ?? RunChildAsync;
        int commandIndex = 0;
        async Task Run(string tool, IReadOnlyList<string> args)
        {
            cancellation.ThrowIfCancellationRequested();
            var command = new Command(Below(request.NativeDirectory, tool), args, attempt,
                request.NativeDirectory, Path.Combine(attempt, "command-" + commandIndex++ + ".log"));
            int status = await runner(command, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (status != 0) throw new InvalidDataException($"{tool} exited {status}; diagnostic files: {attempt}");
        }
        progress("Generating game code on this phone");
        await GenerateAsync(request, generated, () => Run("librefii_codegen.so", new[] { "--force", "codegen", manifestPath })).ConfigureAwait(false);
        string[] units = ReadSources(Path.Combine(generated, "sources.cmake"), generated);
        string header = Path.Combine(generated, "refii_pch.h");
        string generatedHeader = File.ReadAllText(header);
        string lockDepth = ThreadLocalLockDepth(generatedHeader);
        if (lockDepth != generatedHeader)
        {
            File.Copy(header, header + ".process-lock", false);
            File.WriteAllText(header, lockDepth);
        }
        string initUnitPath = Path.Combine(generated, "refii_init.cpp");
        string? initUnitSource = File.Exists(initUnitPath) ? File.ReadAllText(initUnitPath) : null;
        foreach (string unit in units.Where(path => Path.GetFileName(path) == "refii_register.cpp"))
        {
            string original = File.ReadAllText(unit);
            string bounded = Refii.Recompile.CodegenTransforms.OptimizeRegistration(original, initUnitSource);
            if (bounded != original)
            {
                // Retain the generator's exact output beside the rewritten private attempt file.
                File.Copy(unit, unit + ".unsplit", false);
                File.WriteAllText(unit, bounded);
            }
        }
        bool lseAtomics = HasLseAtomics();
        var flags = CompilerFlags(request, bundle, generated, version, lseAtomics).ToList();
        AndroidCompileCache? cache = null;
        if (request.ObjectCache != null)
        {
            // The bundle and attempt folders differ every build; __FILE__ must not, or no object could be reused.
            flags.Add("-fmacro-prefix-map=" + Path.GetFullPath(request.BundleDirectory) + "=bundle");
            flags.Add("-fmacro-prefix-map=" + Path.GetFullPath(attempt) + "=build");
            cache = new AndroidCompileCache(request.ObjectCache, request.BundleDirectory, generated, bundle.Files,
                request.IsFable3 ? bundle.Fable3Includes : bundle.Includes, UnitRecipe(bundle, request.Title, version, lseAtomics), attempt);
        }
        string[] hooks = (request.IsFable3 ? bundle.Fable3Hooks : bundle.Hooks).Select(path => Below(request.BundleDirectory, path)).ToArray();
        ModSources? mods = request.Mods?.Invoke(generated, Path.Combine(attempt, key + "_config.toml"), hooks);
        var all = units.Concat(hooks).Append(Below(request.BundleDirectory, "src/payload/src/payload_export.cpp"))
            .Concat(mods?.Sources ?? Array.Empty<string>()).ToArray();
        // A unit with arguments of its own (a mod's, or a hook a chain renames) is compiled every
        // time: the cache keys a unit on its source, not on what it is compiled with.
        bool Cacheable(int i) => mods == null || !mods.Arguments.ContainsKey(all[i]);
        string[] objects = new string[all.Length];
        var toCompile = new List<int>();
        for (int i = 0; i < all.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (Cacheable(i) && cache?.Find(all[i]) is { } kept) objects[i] = kept;
            else toCompile.Add(i);
        }
        string pch = Path.Combine(attempt, "refii.pch");
        if (toCompile.Any(i => i < units.Length))
        {
            progress("Precompiling game headers");
            string pchDependencies = pch + ".d";
            await Run("librefii_clang.so", flags.Concat(Dependencies(cache, pchDependencies)).Concat(new[] { "-Xclang", "-fno-pch-timestamp", "-x", "c++-header",
                Path.Combine(generated, "refii_pch.h"), "-o", pch }).ToArray()).ConfigureAwait(false);
            cache?.StorePch(pchDependencies, pch);
        }
        int finished = all.Length - toCompile.Count;
        if (toCompile.Count > 0) progress($"Compiling game code {finished + 1}/{all.Length}");
        Task<int> Compile(int i, CancellationToken token)
        {
            string obj = Path.Combine(attempt, "unit-" + i + ".o");
            var args = new List<string>(flags);
            if (i < units.Length) args.AddRange(new[] { "-include-pch", pch });
            if (mods != null && mods.Arguments.TryGetValue(all[i], out List<string>? own)) args.AddRange(own);
            args.AddRange(Dependencies(cache, obj + ".d"));
            args.AddRange(new[] { "-c", all[i], "-o", obj });
            return runner(new Command(Below(request.NativeDirectory, "librefii_clang.so"), args, attempt,
                request.NativeDirectory, Path.Combine(attempt, "command-" + commandIndex++ + ".log")), token);
        }
        void Compiled(int i)
        {
            string obj = Path.Combine(attempt, "unit-" + i + ".o");
            objects[i] = Cacheable(i) ? cache?.Store(all[i], obj, obj + ".d", i < units.Length) ?? obj : obj;
            if (++finished < all.Length) progress($"Compiling game code {finished + 1}/{all.Length}");
        }
        int jobs = request.Jobs > 0 ? request.Jobs : execute == null ? PlanJobs(Environment.ProcessorCount, AvailableMemory()) : 1;
        IReadOnlyList<int> scheduledUnits = jobs > 1
            ? Refii.Recompile.CompileScheduler.OrderUnitsLargestFirst(toCompile, all)
            : toCompile;
        File.WriteAllText(Path.Combine(attempt, "compile-plan.txt"), $"{all.Length - toCompile.Count} of {all.Length} units reused; {jobs} compilers at once\n");
        await RunUnitsAsync(scheduledUnits, jobs, execute == null ? AvailableMemory : () => long.MaxValue, Compile, Compiled,
            (i, status) => new InvalidDataException($"librefii_clang.so exited {status}; diagnostic files: {attempt}"), cancellation).ConfigureAwait(false);
        progress("Linking game module");
        string module = Path.Combine(attempt, "librefii_generated.so");
        var link = flags.Concat(LinkOptions(request)).Concat(objects)
            .Concat(LinkLibraries(request)).Concat(new[] { "-o", module }).ToArray();
        for (int linkAttempt = 1; ; linkAttempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            var command = new Command(Below(request.NativeDirectory, "librefii_clang.so"),
                linkAttempt == 1 ? link : link.Append(SingleThreadLink).ToArray(), attempt,
                request.NativeDirectory, Path.Combine(attempt, "command-" + commandIndex++ + ".log"));
            int status = await runner(command, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (status == 0) break;
            if (linkAttempt >= LinkAttempts || !LinkerCrashed(command.Log))
                throw new InvalidDataException($"librefii_clang.so exited {status}; diagnostic files: {attempt}");
            progress("Linking game module again");
        }
        ValidateModule(File.ReadAllBytes(module));
        bool nativeLoad = Refii.Core.Engine_Platform.IsAndroid && execute == null && verifyLoad == null;
        if (!(verifyLoad?.Invoke(module) ?? (!nativeLoad || VerifyLoadedModule(module, request.NativeDirectory, request.HostBridge))))
            throw new InvalidDataException("Compiled module failed its loader check; private output retained: " + attempt);
        cancellation.ThrowIfCancellationRequested();
        if (nativeLoad)
            File.WriteAllText(Path.Combine(attempt, "module-load-proof.json"), Refii.Core.Engine_Json.Serialize(new {
                phone_executed = true, native_load_verified = true,
                entry_symbol = "refii_payload_v1_image_config", descriptor_nonnull = true,
                module_retained_for_engine_lifetime = true,
                loader = "android_dlopen_rtld_now_global_runtime_first",
                scope = "Loaded the complete module and called its metadata accessor; no guest boot or gameplay" }));
        string digest = Hash(module);
        // A module with hard mods is never adopted by a later APK (StillCurrent): its inputs include
        // units the cache does not record.
        if (cache != null && string.IsNullOrEmpty(mods?.Hash)) RecordModuleInputs(request, bundle, cache, attempt, module, version, key, lseAtomics);
        var result = new Result(attempt, module, digest, version) { Bundle = RecordedBundle(request.BundleDirectory), Mods = mods?.Hash ?? "" };
        File.WriteAllText(Path.Combine(attempt, "completed.json"), Refii.Core.Engine_Json.Serialize(result));
        try { cache?.KeepOnlyUsed(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { progress("Could not tidy the compile cache: " + error.Message); }
        progress("Game code compiled on this phone");
        return result;
    }

    /// <summary>The generator's file of which recompiled unit each guest function goes in.</summary>
    internal const string PartitionFile = Refii.Recompile.CodegenTransforms.PartitionFile;

    /// <summary>Runs the generator with the last build's function placement, and keeps this one's for the next.</summary>
    /// <param name="request">The cache directory, if any, holds the placement.</param>
    /// <param name="generated">The attempt's empty output folder.</param>
    /// <param name="generate">Runs the generator.</param>
    /// <remarks>The generator pins each function to a file across runs through this file, but an attempt's
    /// folder is always new, so it divided the code afresh: with the desktop generator, adding one function
    /// name to the config changed 271 of 295 sources, and only 5 with the placement kept. A first build has
    /// none and is unchanged. A generator run that fails with one is retried without it, in case it is the cause.</remarks>
    private static Task GenerateAsync(Request request, string generated, Func<Task> generate) =>
        Refii.Recompile.CodegenTransforms.RunWithPartitionSeedAsync(request.ObjectCache, generated, generate);

    /// <summary>The arguments that make Clang report what a unit read, when the cache will use it.</summary>
    private static string[] Dependencies(AndroidCompileCache? cache, string file) =>
        cache == null ? Array.Empty<string>() : new[] { "-MD", "-MF", file };

    /// <summary>Memory one compiler is planned at. Measured on the PC replica, whose Clang is the same LLVM:
    /// 214 MB for a typical generated unit, 264 MB for a hook, 522 MB for the largest (refii_register.cpp).</summary>
    internal const long JobMemoryBytes = Refii.Recompile.CompileScheduler.AndroidJobMemoryBytes;

    /// <summary>Memory left to everything else while compilers run.</summary>
    internal const long FreeMemoryFloorBytes = Refii.Recompile.CompileScheduler.AndroidFreeMemoryFloorBytes;

    /// <summary>Most compilers at once; no phone has more cores worth using.</summary>
    internal const int MostJobs = Refii.Recompile.CompileScheduler.AndroidMostJobs;

    /// <summary>How many compilers to start with.</summary>
    /// <param name="cores">CPUs this process may use.</param>
    /// <param name="available">Free memory in bytes (MemAvailable); negative when unknown, which runs one.</param>
    internal static int PlanJobs(int cores, long available) =>
        Refii.Recompile.CompileScheduler.PlanAndroidJobs(cores, available);

    /// <summary>/proc/meminfo's MemAvailable in bytes, or -1 when unreadable.</summary>
    private static long AvailableMemory() => Refii.Recompile.CompileScheduler.AvailableMemoryBytes();

    /// <summary>Compiles units side by side, holding back while memory is short and backing off if the system kills one.</summary>
    /// <param name="units">The units to compile, started in this order.</param>
    /// <param name="jobs">The most to run at once.</param>
    /// <param name="memory">Free memory in bytes now; a job starts beside others only with room for it.</param>
    /// <param name="compile">Starts a unit and yields its exit status.</param>
    /// <param name="completed">Called for each success, one at a time.</param>
    /// <param name="failure">The error for a unit's nonzero status.</param>
    /// <param name="cancellation">Stops running children.</param>
    /// <remarks>lmkd answers memory pressure with SIGKILL (exit 137), and a unit it stopped is not a
    /// compile error, so it runs again with half the jobs. A real failure stops the rest.</remarks>
    internal static Task RunUnitsAsync(IReadOnlyList<int> units, int jobs, Func<long> memory,
        Func<int, CancellationToken, Task<int>> compile, Action<int> completed, Func<int, int, Exception> failure,
        CancellationToken cancellation) =>
        Refii.Recompile.CompileScheduler.RunUnitsAsync(units, jobs, memory, compile, completed, failure, cancellation);

    /// <summary>Linker options that hold for every module, before its objects.</summary>
    private static string[] LinkOptions(Request request) => new[] { "-shared", "-Wl,--no-undefined", "-Wl,-z,max-page-size=16384",
        "-Wl,-z,common-page-size=16384", ModuleLocalCalls, "-Wl,-soname,librefii_generated.so", "-L" + request.NativeDirectory,
        "-Wl,-rpath-link," + request.NativeDirectory };

    /// <summary>The native libraries a module links, after its objects.</summary>
    /// <remarks>Lua and the VFS are Fable 2's instance; Fable 3's module links the shared natives alone, as on desktop.</remarks>
    private static string[] LinkLibraries(Request request) => request.IsFable3
        ? new[] { "-lrefii_core", "-lrefii_runtime", "-lrefiii_vfs", "-lrexruntime" }
        : new[] { "-lrefii_core", "-lrefii_runtime", "-lrefii_lua", "-lrefii_vfs", "-lrexruntime" };

    /// <summary>Bump when this file changes what a build makes in a way the flags and sources do not show.</summary>
    /// <remarks>The rewrites of generated files are covered at unit level by hashing their results, but
    /// <see cref="StillCurrent"/> decides without generating anything.</remarks>
    internal const int RecipeRevision = 2;

    /// <summary>Native files in a bundle that are tools, not libraries a module can bind.</summary>
    private static readonly string[] BuildTools = { "librefii_clang.so", "librefii_lld.so", "librefii_codegen.so", "librefii_phone_bridge.so" };

    /// <summary>The flags and tools that decide a unit's object, with this build's folders left out.</summary>
    /// <param name="bundle">The bundle compiling, or the one a new APK ships.</param>
    /// <param name="title">The game being compiled; Fable 2 when null.</param>
    /// <param name="version">Patched or unpatched guest layout.</param>
    /// <param name="lseAtomics">Whether the flags target ARMv8.2.</param>
    /// <returns>A hex hash. The APK's library folder, the bundle's folder and the attempt all move between builds.</returns>
    /// <remarks>The sysroot and Clang resources are all counted rather than through each unit's record,
    /// because the link reads CRT files no dependency file lists and because headers there are probed
    /// (__has_include) for files a record would not show as absent.</remarks>
    internal static string UnitRecipe(BundleManifest bundle, Refii.Core.Engine_Title? title, string version, bool lseAtomics)
    {
        var text = new StringBuilder().Append(RecipeRevision).Append('\n').Append(bundle.Schema).Append('\n').Append(bundle.Abi).Append('\n')
            .Append(bundle.Api).Append('\n').Append(bundle.Ndk).Append('\n').Append(bundle.SdkPin).Append('\n');
        foreach (string flag in CompilerFlags(MovingRootsLeftOut(title), bundle, "/~build/generated", version, lseAtomics)) text.Append(flag).Append('\n');
        foreach (string tool in new[] { "librefii_clang.so", "librefii_codegen.so" })
            text.Append(tool).Append(' ').Append(bundle.NativeFiles.GetValueOrDefault(tool, "")).Append('\n');
        foreach (var file in bundle.Files.Where(file => file.Key.StartsWith("sysroot/", StringComparison.Ordinal) ||
                     file.Key.StartsWith("resource/", StringComparison.Ordinal)).OrderBy(file => file.Key, StringComparer.Ordinal))
            text.Append(file.Key).Append(' ').Append(file.Value).Append('\n');
        return AndroidCompileCache.HashText(text.ToString());
    }

    /// <summary>Everything that decides a whole module beyond its units' bundle inputs and its native libraries.</summary>
    /// <param name="bundle">The bundle compiling, or the one a new APK ships.</param>
    /// <param name="title">The game being compiled; Fable 2 when null.</param>
    /// <param name="version">Patched or unpatched guest layout.</param>
    /// <param name="lseAtomics">Whether the flags target ARMv8.2.</param>
    internal static string ModuleRecipe(BundleManifest bundle, Refii.Core.Engine_Title? title, string version, bool lseAtomics)
    {
        Request roots = MovingRootsLeftOut(title);
        var text = new StringBuilder(UnitRecipe(bundle, title, version, lseAtomics)).Append('\n');
        foreach (string tool in new[] { "librefii_lld.so", "libc++_shared.so" })
            text.Append(tool).Append(' ').Append(bundle.NativeFiles.GetValueOrDefault(tool, "")).Append('\n');
        foreach (string option in LinkOptions(roots).Concat(LinkLibraries(roots))) text.Append(option).Append('\n');
        foreach (string hook in roots.IsFable3 ? bundle.Fable3Hooks : bundle.Hooks) text.Append(hook).Append('\n');
        text.Append(ProcessWideGuestLock).Append(ThreadLocalGuestLock);
        return AndroidCompileCache.HashText(text.ToString());
    }

    /// <summary>Placeholder folders for a recipe's flags; no file is read through them.</summary>
    private static Request MovingRootsLeftOut(Refii.Core.Engine_Title? title) => new("/~bundle", "/~native", "/~game", "/~build", title);

    /// <summary>What a built module was made from, beside it; read back by <see cref="StillCurrent"/>.</summary>
    internal const string ModuleInputsFile = "module-inputs.txt";

    /// <summary>Records a finished module's inputs; a build that cannot name them all records nothing and rebuilds on any bundle change.</summary>
    private static void RecordModuleInputs(Request request, BundleManifest bundle, AndroidCompileCache cache, string attempt,
        string module, string version, string key, bool lseAtomics)
    {
        if (!cache.Complete) return;
        var libraries = bundle.NativeFiles.Keys.Except(BuildTools).Select(name => Path.Combine(request.NativeDirectory, name)).ToList();
        if (AndroidModuleImports.Bound(module, libraries) is not { } bound) return;
        var inputs = new SortedSet<string>(cache.BundleInputs, StringComparer.Ordinal);
        foreach (string name in new[] { "_config.toml", "_manifest.toml" }) inputs.Add("versions/" + version + "/" + key + name);
        var text = new StringBuilder().Append("recipe ").Append(ModuleRecipe(bundle, request.Title, version, lseAtomics)).Append('\n')
            .Append("names ").Append(AndroidCompileCache.SameNames(inputs, AndroidCompileCache.FileNameIndex(bundle.Files),
                request.IsFable3 ? bundle.Fable3Includes : bundle.Includes)).Append('\n');
        foreach (string input in inputs)
        {
            if (!bundle.Files.TryGetValue(input, out string? hash)) return;
            text.Append("file ").Append(hash).Append(' ').Append(input).Append('\n');
        }
        foreach (string symbol in bound) text.Append("bound ").Append(symbol).Append('\n');
        File.WriteAllText(Path.Combine(attempt, ModuleInputsFile), text.Append("end\n").ToString());
    }

    /// <summary>Whether a built module is exactly what a new APK's bundle would build, so that no build is needed.</summary>
    /// <param name="built">The completed build the launcher would play.</param>
    /// <param name="apk">The new APK's bundle manifest.</param>
    /// <param name="nativeDirectory">The new APK's native libraries.</param>
    /// <param name="title">The game; Fable 2 when null.</param>
    /// <returns>True only when it can be shown: same tools, flags and recipe, the same bytes for every bundle
    /// file the compiler read, nothing new an include could find first, and every native symbol the module
    /// binds still exported. Any doubt is false, and the launcher offers a build as before.</returns>
    /// <remarks>A bundle's identity changes far more often than this: bundle.json lists every native
    /// library's hash, sdk-generated/include/rex/version.h holds the build's timestamp, and the bundle
    /// carries thousands of headers no unit reads. None of the 23 bundles compared had a tool change.</remarks>
    public static bool StillCurrent(Result built, BundleManifest apk, string nativeDirectory, Refii.Core.Engine_Title? title)
    {
        try
        {
            string path = Path.Combine(built.Directory, ModuleInputsFile);
            if (!File.Exists(path)) return false;
            string[] lines = File.ReadAllLines(path);
            if (lines.Length < 3 || lines[^1] != "end" || !lines[0].StartsWith("recipe ", StringComparison.Ordinal) ||
                !lines[1].StartsWith("names ", StringComparison.Ordinal)) return false;
            if (lines[0][7..] != ModuleRecipe(apk, title, built.Version, HasLseAtomics())) return false;
            var inputs = new List<string>();
            var bound = new List<string>();
            foreach (string line in lines.Skip(2).SkipLast(1))
            {
                if (line.StartsWith("bound ", StringComparison.Ordinal)) { bound.Add(line[6..]); continue; }
                if (!line.StartsWith("file ", StringComparison.Ordinal)) return false;
                int space = line.IndexOf(' ', 5);
                if (space < 0) return false;
                string input = line[(space + 1)..];
                if (apk.Files.GetValueOrDefault(input) != line[5..space]) return false;
                inputs.Add(input);
            }
            if (AndroidCompileCache.SameNames(inputs, AndroidCompileCache.FileNameIndex(apk.Files),
                title == Refii.Core.Engine_Title.Fable3 ? apk.Fable3Includes : apk.Includes) != lines[1][6..]) return false;
            foreach (string library in LinkLibraries(new Request("", "", "", "", title)))
                if (!File.Exists(Path.Combine(nativeDirectory, "lib" + library[2..] + ".so"))) return false;
            var exported = AndroidModuleImports.Exports(apk.NativeFiles.Keys.Except(BuildTools).Select(name => Path.Combine(nativeDirectory, name)));
            return exported != null && bound.All(exported.Contains);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { return false; }
    }

    /// <summary>Loads the actual completed Android module and checks its versioned accessor before publication.</summary>
    /// <param name="module">Fresh private module with previously validated ELF layout and matched runtime headers.</param>
    /// <param name="nativeDirectory">Matched APK runtime closure, loaded before the payload as in native guest boot.</param>
    /// <returns>Whether the payload descriptor accessor returns a non-null pointer.</returns>
    /// <remarks>No descriptor fields are marshalled, registration function is called, guest is started,
    /// configuration is changed or save is read. Verified handles remain pinned for engine lifetime.</remarks>
    private static bool VerifyLoadedModule(string module, string nativeDirectory, string hostBridge = "librefii_hostbridge.so")
    {
        // Runtime-first lookup preserves the SDK's global flag/function owners;
        // loading the payload as the root closure can interpose its bridge wrappers.
        LoadEvent(module, "runtime_preload_begin");
        string runtime = Below(nativeDirectory, hostBridge);
        LoadedRuntime.Verify(runtime, Hash(runtime));
        LoadEvent(module, "runtime_preload_returned");
        bool verified = LoadedModules.Verify(module, Hash(module));
        LoadEvent(module, "module_retained_for_engine_lifetime");
        return verified;
    }

    /// <summary>Flushes each native stage before invoking code that could terminate the process.</summary>
    private static void LoadEvent(string module, string stage) => File.AppendAllText(
        Path.Combine(Path.GetDirectoryName(module)!, "module-load-events.log"), stage + "\n");

    /// <summary>Diagnoses a hash-approved previously phone-compiled module without repeating compilation.</summary>
    /// <param name="request">Verified matched APK tools/runtime and owned private output root.</param>
    /// <param name="sourceModule">Previously compiled module beneath that private root.</param>
    /// <param name="expectedHash">Exact externally audited source module hash.</param>
    /// <param name="version">Known executable layout of the original phone compilation.</param>
    /// <param name="progress">Status for the explicit diagnostic action.</param>
    /// <param name="cancellation">Stops before native verification or publication.</param>
    /// <param name="verifyLoad">Synthetic metadata checker for lifecycle tests.</param>
    /// <returns>A fresh verified copy; no source module, configuration or save is changed.</returns>
    public static Task<Result> VerifyExistingAsync(Request request, string sourceModule, string expectedHash,
        string version, Action<string> progress, CancellationToken cancellation, Func<string, bool>? verifyLoad = null)
    {
        cancellation.ThrowIfCancellationRequested();
        Validate(request);
        if (version is not ("Fable2" or "Fable2TU1")) throw new InvalidDataException("Unknown guest layout");
        string relative = Path.GetRelativePath(request.PrivateDirectory, sourceModule).Replace('\\', '/');
        sourceModule = Below(request.PrivateDirectory, relative);
        if (Path.GetFileName(sourceModule) != "librefii_generated.so" || !Regex.IsMatch(expectedHash, "^[0-9a-f]{64}$") ||
            Hash(sourceModule) != expectedHash) throw new InvalidDataException("Diagnostic module bytes do not match the approved hash");
        ValidateModule(File.ReadAllBytes(sourceModule));
        string attempt = Path.Combine(request.PrivateDirectory, "phone-build-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(attempt);
        string module = Path.Combine(attempt, "librefii_generated.so");
        File.Copy(sourceModule, module, false);
        if (Hash(module) != expectedHash) throw new InvalidDataException("Diagnostic module copy changed bytes");
        File.WriteAllText(Path.Combine(attempt, "compilation-origin.json"), Refii.Core.Engine_Json.Serialize(new {
            kind = "verification_only", source_module = sourceModule, source_sha256 = expectedHash,
            scope = "Copied a previously phone-compiled module; no generator, compiler or linker invoked in this check" }));
        progress("Verifying previously phone-compiled game module");
        cancellation.ThrowIfCancellationRequested();
        bool native = Refii.Core.Engine_Platform.IsAndroid && verifyLoad == null;
        if (!(verifyLoad?.Invoke(module) ?? (!native || VerifyLoadedModule(module, request.NativeDirectory))))
            throw new InvalidDataException("Previously compiled module failed its metadata check");
        cancellation.ThrowIfCancellationRequested();
        if (native) File.WriteAllText(Path.Combine(attempt, "module-load-proof.json"), Refii.Core.Engine_Json.Serialize(new {
            phone_executed = true, native_load_verified = true, descriptor_nonnull = true,
            entry_symbol = "refii_payload_v1_image_config", module_retained_for_engine_lifetime = true,
            loader = "android_dlopen_rtld_now_global_runtime_first",
            scope = "Loaded a copy of the previously phone-compiled module and called its metadata accessor; no guest boot or gameplay" }));
        var result = new Result(attempt, module, expectedHash, version) { Bundle = RecordedBundle(request.BundleDirectory) };
        File.WriteAllText(Path.Combine(attempt, "completed.json"), Refii.Core.Engine_Json.Serialize(result));
        return Task.FromResult(result);
    }

    /// <summary>Binds the module's calls to its own functions at link time instead of through the PLT.</summary>
    /// <remarks>Every generated function is exported, so each guest-to-guest call was a PLT stub and a
    /// GOT load (34,729 slots), about 3% of guest CPU. Symbols stay exported, so name lookups and the
    /// one entry the host resolves are unchanged; only data symbols keep interposition. This also keeps
    /// rex_bridge_payload.cpp's forwarders module-internal, as -fvisibility=hidden does on desktop.</remarks>
    internal const string ModuleLocalCalls = "-Wl,-Bsymbolic-functions";

    /// <summary>
    /// How many times the link is run when the linker crashes. The bundled lld has aborted on a
    /// Galaxy S10e at the end of two builds (2026-11-03 and 2026-11-06): bionic's "Pointer tag for
    /// 0x… was truncated", a free() of a pointer that was never a heap pointer, so memory corrupted
    /// inside lld. Both times the next build linked the same objects. Every unit is compiled by
    /// then, so running the link again costs a link, not the build.
    /// </summary>
    internal const int LinkAttempts = 3;

    /// <summary>The retries link on one thread, in case the corruption is a race in lld's parallel passes.</summary>
    internal const string SingleThreadLink = "-Wl,--threads=1";

    /// <summary>Whether a failed link's log says the linker died from a signal, as opposed to an error
    /// in what it was given (an undefined symbol fails the same way every time).</summary>
    internal static bool LinkerCrashed(string log)
    {
        try { return File.Exists(log) && File.ReadAllText(log).Contains("linker command failed due to signal", StringComparison.Ordinal); }
        catch (IOException) { return false; }
    }

    /// <summary>Fable 3's VFS, which its module links (refiii_vfs; librefiii_vfs.so in the APK, cmake/refii_android.cmake).</summary>
    internal const string Fable3Vfs = "librefiii_vfs.so";

    /// <summary>Bounds optimizer work for generated registration without changing calls or their order.</summary>
    /// <param name="source">Fresh generator output containing only the known registration function.</param>
    /// <returns>Equivalent ordered noinline helpers, or the unchanged small function.</returns>
    /// <remarks>Large games contain tens of thousands of virtual calls in this one function.
    /// Helpers retain full optimization while preventing inlining from rebuilding that enormous graph.
    /// Unknown generated layouts fail closed; this never rewrites guest arithmetic or hook code.</remarks>
    public static string BoundRegistration(string source) =>
        Refii.Recompile.CodegenTransforms.BoundRegistration(source);

    /// <summary>The guest's floating-point model: IEEE round-to-nearest without contraction.</summary>
    /// <remarks>This is the x86_64 desktop build's model with contraction pinned off, because simde's
    /// portable fallbacks would otherwise fuse vmaddfp's separate multiply and add on ARM only.
    /// -ffp-model=strict cost about 3% of guest CPU: every compare and conversion stayed a constrained
    /// operation, so CR compares never merged and exact float/double round trips stayed (686/581
    /// conversions in a sampled unit, 488/210 now, 450/200 on x86_64). Guest results are unchanged in
    /// round-to-nearest. FPCR writes are asm with a memory clobber and guest registers live in ctx, so
    /// flush-mode and rounding changes still order against guest FP work. Nothing reads FP exception flags.</remarks>
    internal static readonly string[] GuestFloatingPoint = { "-ffp-model=precise", "-ffp-contract=off" };

    /// <summary>Uses the same payload ABI guard, floating-point model and ARM64 target as the PC Android build.</summary>
    /// <param name="request">Explicit tool and public input roots.</param>
    /// <param name="bundle">Validated ordered include recipe.</param>
    /// <param name="generated">Private generated source directory.</param>
    /// <param name="version">Patched or unpatched guest layout.</param>
    /// <param name="lseAtomics">Target ARMv8.2 for a CPU with LSE atomics; otherwise the ARMv8.0 baseline.</param>
    /// <returns>Shell-free Clang arguments for all payload translation units.</returns>
    /// <remarks>ARMv8.2 turns the guest's ~8,500 compare-and-swap sites (stwcx.) from calls to
    /// __aarch64_cas4_acq_rel into inline CASAL. The S10e's Cortex-A76/A55 are ARMv8.2 too.</remarks>
    public static IReadOnlyList<string> CompilerFlags(Request request, BundleManifest bundle,
        string generated, string version, bool lseAtomics = false)
    {
        var flags = new List<string> { "--driver-mode=g++", "--target=aarch64-linux-android31",
            "--sysroot=" + Below(request.BundleDirectory, "sysroot"),
            "-resource-dir=" + Below(request.BundleDirectory, "resource"),
            "--ld-path=" + Below(request.NativeDirectory, "librefii_lld.so"),
            "-fintegrated-cc1", "-std=gnu++23", "-O2", "-fPIC", "-DNDEBUG", "-DANDROID",
            "-DREX_PLATFORM_ANDROID=1", "-DREX_HAS_VULKAN=1", "-DSPDLOG_COMPILED_LIB", "-DSPDLOG_FMT_EXTERNAL",
            "-DREFII_PAYLOAD_LOG_BRIDGE=1", "-fno-strict-aliasing", "-fno-char8_t",
            "-include", Below(request.BundleDirectory, "src/payload/src/libcxx_chrono_shim.h"),
            "-include", Below(request.BundleDirectory, "tools/android/payload_abi_guard.h"), "-I" + generated };
        flags.AddRange(GuestFloatingPoint);
        foreach (string macro in new[] { "TRACY_ENABLE", "TRACY_MANUAL_LIFETIME", "TRACY_DELAYED_INIT",
                     "TRACY_ON_DEMAND", "TRACY_FIBERS", "TRACY_IMPORTS", "REXGLUE_ENABLE_PROFILING" })
            flags.Add("-U" + macro);
        if ((request.Title ?? Refii.Core.Engine_Title.Fable2).IsUnpatched(version)) flags.Add("-DREFII_GUEST_BUILD_UNPATCHED=1");
        if (lseAtomics) flags.Add("-march=armv8.2-a");
        foreach (string path in request.IsFable3 ? bundle.Fable3Includes : bundle.Includes)
            flags.Add("-I" + Below(request.BundleDirectory, path));
        return flags;
    }

    /// <summary>Accepts only literal generated source paths contained in the private generation directory.</summary>
    /// <param name="list">Generator's sources.cmake file.</param>
    /// <param name="root">Private generation root.</param>
    /// <returns>Validated unique C++ translation units, without executing CMake.</returns>
    public static string[] ReadSources(string list, string root) =>
        Refii.Recompile.CodegenTransforms.ReadGeneratedSources(list, root);

    /// <summary>Rejects host modules, malformed ELF tables and unsafe 16KiB LOAD layouts before publication.</summary>
    /// <param name="data">Completed private module bytes.</param>
    public static void ValidateModule(byte[] data)
    {
        if (data.Length < 64 || data[0] != 0x7f || data[1] != 'E' || data[2] != 'L' || data[3] != 'F' ||
            data[4] != 2 || data[5] != 1 || BitConverter.ToUInt16(data, 16) != 3 || BitConverter.ToUInt16(data, 18) != 183)
            throw new InvalidDataException("Expected a little-endian ARM64 shared ELF module");
        ulong offset = BitConverter.ToUInt64(data, 32);
        ushort size = BitConverter.ToUInt16(data, 54), count = BitConverter.ToUInt16(data, 56);
        if (size != 56 || count == 0 || offset > (ulong)data.Length ||
            (ulong)size * count > (ulong)data.Length - offset)
            throw new InvalidDataException("Malformed module program headers");
        int loads = 0;
        var writable = new List<(ulong Start, ulong End)>();
        var protectedRanges = new List<(ulong Start, ulong End)>();
        for (int i = 0; i < count; i++)
        {
            int at = checked((int)offset + i * size);
            uint type = BitConverter.ToUInt32(data, at);
            if (type is not (1 or 0x6474e552)) continue;
            ulong file = BitConverter.ToUInt64(data, at + 8), address = BitConverter.ToUInt64(data, at + 16),
                length = BitConverter.ToUInt64(data, at + 32), memory = BitConverter.ToUInt64(data, at + 40),
                alignment = BitConverter.ToUInt64(data, at + 48);
            if (memory > ulong.MaxValue - address)
                throw new InvalidDataException("Module segment address overflows");
            if (type == 0x6474e552)
            {
                if (address + memory > ulong.MaxValue - 16383)
                    throw new InvalidDataException("Module RELRO rounding overflows");
                protectedRanges.Add((address, address + memory));
                continue;
            }
            loads++;
            uint permissions = BitConverter.ToUInt32(data, at + 4);
            if (alignment < 16384 || (alignment & (alignment - 1)) != 0 || address % 16384 != file % 16384 ||
                file > (ulong)data.Length || length > (ulong)data.Length - file || length > memory || (permissions & 3) == 3)
                throw new InvalidDataException("Module has invalid or writable executable LOAD segments");
            if ((permissions & 2) != 0) writable.Add((address, address + memory));
        }
        if (loads == 0) throw new InvalidDataException("Module has no LOAD segments");
        // Android rounds RELRO to the device's page size. Protecting adjacent writable
        // data would make an aligned module fail at runtime on a 16KiB device.
        foreach (var range in protectedRanges)
        {
            ulong roundedStart = range.Start / 16384 * 16384;
            ulong roundedEnd = (range.End + 16383) / 16384 * 16384;
            foreach (var segment in writable)
            {
                ulong start = Math.Max(roundedStart, segment.Start), end = Math.Min(roundedEnd, segment.End);
                if (start < end && (start < range.Start || end > range.End))
                    throw new InvalidDataException("Module RELRO overlaps writable data after 16KiB rounding");
            }
        }
    }

    /// <summary>The interrupt-lock block generators before the thread-local depth emitted into refii_pch.h.</summary>
    internal const string ProcessWideGuestLock = Refii.Recompile.CodegenTransforms.ProcessWideGuestLock;

    /// <summary>The same block as the SDK shadow's pch_h.inja now emits it.</summary>
    internal const string ThreadLocalGuestLock = Refii.Recompile.CodegenTransforms.ThreadLocalGuestLock;

    /// <summary>Gives an older generator's header the thread-local interrupt-lock depth.</summary>
    /// <param name="header">Fresh generator output for refii_pch.h.</param>
    /// <returns>The header with the process-wide block replaced, or unchanged when it is absent.</returns>
    /// <remarks>The phone's generator predates the template change. Its mfmsr took the global
    /// critical region (two runtime calls and a recursive mutex) for every guest interlocked
    /// operation, about 5% of guest CPU at the 30 fps cap. A header without the exact old block
    /// (a newer generator, or one already rewritten) keeps the generator's own correct code.</remarks>
    public static string ThreadLocalLockDepth(string header) =>
        Refii.Recompile.CodegenTransforms.ThreadLocalLockDepth(header);

    /// <summary>Runs one tool with bounded lifetime, real exit status and cancellation cleanup.</summary>
    /// <param name="command">APK-native executable and private working/log paths.</param>
    /// <param name="cancellation">Cancellation token for the attempt.</param>
    /// <returns>Actual process exit code.</returns>
    public static async Task<int> RunChildAsync(Command command, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var start = new ProcessStartInfo(command.Executable) { WorkingDirectory = command.Directory,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in command.Arguments) start.ArgumentList.Add(argument);
        start.Environment["LD_LIBRARY_PATH"] = command.NativeDirectory;
        start.Environment["TMPDIR"] = command.Directory;
        using var child = Process.Start(start) ?? throw new IOException("Cannot start bundled compiler");
        Task<string> stdout = child.StandardOutput.ReadToEndAsync();
        Task<string> stderr = child.StandardError.ReadToEndAsync();
        try
        {
            await child.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return child.ExitCode;
        }
        catch (OperationCanceledException error) when (!cancellation.IsCancellationRequested)
        {
            throw new TimeoutException("Bundled compiler exceeded its 15-minute command limit; diagnostics: " + command.Log, error);
        }
        finally
        {
            if (!child.HasExited)
            {
                try { child.Kill(entireProcessTree: true); }
                catch (PlatformNotSupportedException) { child.Kill(); }
                await child.WaitForExitAsync().ConfigureAwait(false);
            }
            File.WriteAllText(command.Log, await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false) +
                "\ntool_exit=" + child.ExitCode + "; interrupted=" + timeout.IsCancellationRequested + "\n");
        }
    }

    /// <summary>Replaces exactly one bundled TOML path using TOML-compatible escaped strings.</summary>
    private static string RewriteAssignment(string text, string name, string value)
    {
        string pattern = @"(?m)^" + Regex.Escape(name) + @"\s*=.*$";
        if (Regex.Matches(text, pattern).Count != 1) throw new InvalidDataException("Ambiguous manifest field " + name);
        return Regex.Replace(text, pattern, _ => name + " = " + Refii.Core.Engine_Json.Serialize(value.Replace('\\', '/')));
    }

    /// <summary>Rejects unsafe bundle paths, including symbolic links in any component.</summary>
    private static string Below(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\\') ||
            relative.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("Invalid bundle path: " + relative);
        string path = Path.GetFullPath(root);
        foreach (string part in relative.Split('/'))
        {
            path = Path.Combine(path, part);
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Compiler inputs may not traverse symbolic links");
        }
        return path;
    }

    /// <summary>Verifies one ordinary file's recorded bytes before compilation.</summary>
    private static void Verify(string root, string path, string expected)
    {
        if (!Regex.IsMatch(expected, "^[0-9a-f]{64}$") || Hash(Below(root, path)) != expected)
            throw new InvalidDataException("Compiler input hash mismatch: " + path);
    }

    /// <summary>Hashes a file with bounded memory.</summary>
    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(Refii.Core.Engine_Hash.Sha256(stream)).ToLowerInvariant();
    }

    /// <summary>Rejects nested input/output roots using the host filesystem's case rules.</summary>
    private static bool Overlaps(string a, string b)
    {
        string left = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
        string right = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return left.Equals(right, comparison) || left.StartsWith(right + Path.DirectorySeparatorChar, comparison) ||
            right.StartsWith(left + Path.DirectorySeparatorChar, comparison);
    }
}
