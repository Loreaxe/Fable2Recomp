using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Refii.Recompile;

namespace Refii.AndroidRuntime;

/// <summary>Compiled translation units kept between phone builds, found by what they were built from.</summary>
/// <remarks>
/// <para>An update rarely changes much of the game code: of 23 consecutive APK bundles, one touched a
/// header the generated units include, yet every changed bundle compiled all 383 units again. A killed
/// build lost its finished units the same way. An object here is named by the recipe, the unit's name
/// and its source bytes, and is reused only while every file the compiler reported reading (clang -MD)
/// still has the bytes it had. An interrupted build's objects count, so a new build resumes it.</para>
/// <para>Bundle files are compared by the hashes in the validated bundle.json, generated files by
/// hashing them once. A file the compiler read outside the bundle and generated folders makes its unit
/// uncacheable rather than guessed at. A new file an include could now find first, which no recorded hash
/// would show, also counts as a change (<see cref="SameNames"/>).</para>
/// <para>Each store is atomic and keeps the object's hash, so a torn or damaged object is a miss. The
/// directory is app-private, as the module is, and holds only what the last success used plus what later
/// failures left: about 100 MB of objects and 10 MB of records for Fable 2.</para>
/// <para>Delegates to the shared <see cref="CompileCache"/> and <see cref="CompileDepfile"/> engine layer.</para>
/// </remarks>
internal sealed class AndroidCompileCache
{
    private const string BundlePrefix = "bundle/";
    private const string GeneratedPrefix = "generated/";

    private readonly CompileCache _inner;

    /// <summary>Whether every unit of this build has a complete record, so <see cref="BundleInputs"/> is the module's whole input.</summary>
    public bool Complete => _inner.Complete;

    /// <summary>The bundle files this build's units read, by bundle-relative path.</summary>
    public IReadOnlyCollection<string> BundleInputs => _inner.TrackedInputs;

    /// <summary>Opens the cache for one build.</summary>
    /// <param name="root">The app-private cache directory; created when absent.</param>
    /// <param name="bundleDirectory">The extracted and validated compiler bundle.</param>
    /// <param name="generated">This attempt's generated source directory.</param>
    /// <param name="files">The validated bundle's file hashes.</param>
    /// <param name="includes">This build's bundle-relative include directories.</param>
    /// <param name="recipe">What else decides an object's bytes (<see cref="AndroidPhoneCompiler.UnitRecipe"/>).</param>
    /// <param name="compilerDirectory">The directory the compiler runs in, which relative dependency paths are relative to.</param>
    public AndroidCompileCache(string root, string bundleDirectory, string generated, Dictionary<string, string> files,
        IReadOnlyCollection<string> includes, string recipe, string compilerDirectory)
    {
        var roots = new[]
        {
            CompileInputRoot.Prehashed(BundlePrefix, bundleDirectory, files, trackInputsAndShadowing: true),
            CompileInputRoot.DiskHashed(GeneratedPrefix, generated, trackInputsAndShadowing: false),
        };
        _inner = new CompileCache(root, roots, files.Keys, includes, recipe, compilerDirectory);
    }

    /// <summary>The bundle's paths by file name, ordered, for the same-name check.</summary>
    /// <param name="files">A bundle's file hashes.</param>
    public static ILookup<string, string> FileNameIndex(Dictionary<string, string> files) =>
        CompileCache.FileNameIndex(files);

    /// <summary>The files an include of one of these inputs could have found: same file name, below an
    /// include directory or below a folder holding an input (an include searches its own file's folder first).</summary>
    /// <param name="bundlePaths">Bundle-relative input paths.</param>
    /// <param name="index">The bundle's <see cref="FileNameIndex"/>.</param>
    /// <param name="includes">The bundle-relative include directories.</param>
    /// <returns>A hash that changes when such a file appears or goes. Unrelated files in the bundle, such as
    /// a third-party tree's own allocator.h, are not reachable and leave it alone.</returns>
    public static string SameNames(IEnumerable<string> bundlePaths, ILookup<string, string> index, IEnumerable<string> includes) =>
        CompileCache.SameNames(bundlePaths, index, includes);

    /// <summary>Hex SHA-256 of a text.</summary>
    public static string HashText(string text) => CompileCache.HashText(text);

    /// <summary>The object for a unit whose recorded inputs are all unchanged, or null.</summary>
    /// <param name="source">The unit's source path.</param>
    /// <returns>A verified object path that later builds may keep using.</returns>
    public string? Find(string source) => _inner.Find(source);

    /// <summary>Records the precompiled header's inputs, which its units read without opening them again.</summary>
    /// <param name="dependencies">The header's clang -MD output.</param>
    /// <param name="output">The precompiled header, which is not itself an input.</param>
    public void StorePch(string dependencies, string output) => _inner.StorePch(dependencies, output);

    /// <summary>Moves a freshly compiled object into the cache, when its inputs can all be named.</summary>
    /// <param name="source">The unit's source path.</param>
    /// <param name="built">The compiled object in the attempt.</param>
    /// <param name="dependencies">Its clang -MD output.</param>
    /// <param name="usesPch">Whether it was compiled with the precompiled header.</param>
    /// <returns>Where the object now is, which is <paramref name="built"/> when it could not be cached.</returns>
    public string Store(string source, string built, string dependencies, bool usesPch) =>
        _inner.Store(source, built, dependencies, usesPch);

    /// <summary>Removes everything this build did not use, once it has succeeded.</summary>
    public void KeepOnlyUsed() => _inner.KeepOnlyUsed(maxGenerations: 1);

    /// <summary>The paths in a clang -MD file, without its target.</summary>
    /// <param name="text">The make rule clang wrote.</param>
    /// <remarks>Make's escapes: a backslash before a space or '#', '$$' for '$', and backslash-newline to continue a line.</remarks>
    public static List<string> ParseDependencies(string text) => CompileDepfile.Parse(text);
}

/// <summary>What a built module needs from the APK's own native libraries, read from ELF dynamic symbol tables.</summary>
/// <remarks>A module links them dynamically and their headers are already among its compile inputs, so
/// a rebuilt library need not rebuild the module unless it stopped exporting something the module binds
/// (loaded RTLD_NOW, a missing symbol would stop the game at load). This reads only the section headers,
/// .dynsym and its strings: a 120 MB library costs a few megabytes. Anything unreadable is null, and
/// the caller builds again.</remarks>
internal static class AndroidModuleImports
{
    /// <summary>One library's dynamic symbols.</summary>
    /// <param name="Defined">Names it exports.</param>
    /// <param name="Undefined">Names it needs from elsewhere.</param>
    internal sealed record Symbols(HashSet<string> Defined, List<string> Undefined);

    /// <summary>Reads an ELF64 little-endian file's dynamic symbols.</summary>
    /// <param name="path">The library or module.</param>
    /// <returns>Its symbols, or null when it has no readable .dynsym.</returns>
    public static Symbols? Read(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            byte[] head = new byte[64];
            if (file.Read(head, 0, 64) != 64 || head[0] != 0x7f || head[1] != 'E' || head[2] != 'L' || head[3] != 'F' || head[4] != 2 || head[5] != 1) return null;
            long sections = BitConverter.ToInt64(head, 40);
            int entrySize = BitConverter.ToUInt16(head, 58), count = BitConverter.ToUInt16(head, 60);
            if (sections <= 0 || entrySize < 64 || count == 0 || sections + (long)entrySize * count > file.Length) return null;
            byte[] table = ReadAt(file, sections, entrySize * count);
            for (int i = 0; i < count; i++)
            {
                int at = i * entrySize;
                if (BitConverter.ToUInt32(table, at + 4) != 11) continue; // SHT_DYNSYM
                long symbolsAt = BitConverter.ToInt64(table, at + 24), symbolsSize = BitConverter.ToInt64(table, at + 32);
                int link = BitConverter.ToInt32(table, at + 40);
                if (link <= 0 || link >= count) return null;
                long namesAt = BitConverter.ToInt64(table, link * entrySize + 24), namesSize = BitConverter.ToInt64(table, link * entrySize + 32);
                if (symbolsSize % 24 != 0 || symbolsAt < 0 || namesAt < 0 || symbolsAt + symbolsSize > file.Length || namesAt + namesSize > file.Length) return null;
                byte[] symbols = ReadAt(file, symbolsAt, checked((int)symbolsSize));
                byte[] names = ReadAt(file, namesAt, checked((int)namesSize));
                var result = new Symbols(new HashSet<string>(StringComparer.Ordinal), new List<string>());
                for (int entry = 24; entry + 24 <= symbols.Length; entry += 24)
                {
                    uint nameAt = BitConverter.ToUInt32(symbols, entry);
                    if (nameAt == 0 || nameAt >= names.Length) continue;
                    int end = Array.IndexOf(names, (byte)0, (int)nameAt);
                    if (end < 0) return null;
                    string name = Encoding.UTF8.GetString(names, (int)nameAt, end - (int)nameAt);
                    if (BitConverter.ToUInt16(symbols, entry + 6) == 0) result.Undefined.Add(name);
                    else if ((symbols[entry + 4] >> 4) != 0) result.Defined.Add(name); // not STB_LOCAL
                }
                return result;
            }
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OverflowException or ArgumentException) { return null; }
    }

    /// <summary>The names a module binds in these libraries: what it needs that they export today.</summary>
    /// <param name="module">The linked module.</param>
    /// <param name="libraries">The APK's native libraries it can bind.</param>
    /// <returns>The names, or null when a file could not be read.</returns>
    public static List<string>? Bound(string module, IEnumerable<string> libraries)
    {
        var symbols = Read(module);
        var exported = Exports(libraries);
        return symbols == null || exported == null ? null
            : symbols.Undefined.Where(exported.Contains).Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Everything these libraries export, or null when one could not be read.</summary>
    /// <param name="libraries">Paths of ELF libraries.</param>
    public static HashSet<string>? Exports(IEnumerable<string> libraries)
    {
        var all = new HashSet<string>(StringComparer.Ordinal);
        foreach (string library in libraries)
        {
            if (Read(library) is not { } symbols) return null;
            all.UnionWith(symbols.Defined);
        }
        return all;
    }

    /// <summary>Reads an exact byte range.</summary>
    private static byte[] ReadAt(FileStream file, long offset, int length)
    {
        file.Position = offset;
        byte[] bytes = new byte[length];
        file.ReadExactly(bytes);
        return bytes;
    }
}
