using System;
using System.IO;
using System.Linq;
using GdUnit4;
using Refii.Launcher.Recompile;
using Refii.Recompile;
using static GdUnit4.Assertions;

namespace Refii.Tests.Recompile;

/// <summary>
/// <see cref="CompileCache"/> against dependency files shaped like the bundled zig's: an install path with spaces,
/// and zig's own <c>lib/</c> headers named relative to the compiler's working directory (<c>bin/recompile</c>).
/// </summary>
[TestSuite]
public sealed class CompileCacheReuseTests
{
    private const string Recipe = "recipe-1";
    private const string ZigHeader = "zig/lib/include/stddef.h";

    /// <summary>One install laid out as the launcher lays it out, with the files two units and the header read.</summary>
    private sealed class Install : IDisposable
    {
        public readonly string Root;
        public readonly string Recompile;
        public readonly string Zig;
        public readonly string Generated;
        public readonly string Mods;
        public readonly string Objects;
        public readonly string Work;
        public readonly string PchHeader;
        public readonly string SdkHeader;
        public readonly string CombatHeader;
        public readonly string Combat;
        public readonly string Other;

        public Install()
        {
            Root = Path.Combine(TestTemp.Root, "compile_reuse_" + Guid.NewGuid().ToString("N"), "Fable Return to Albion");
            Recompile = Path.Combine(Root, "bin", "recompile");
            Zig = Path.Combine(Recompile, "zig");
            string compile = Path.Combine(Root, "cache", "Fable2", "compile");
            Generated = Path.Combine(compile, "generated");
            Mods = Path.Combine(compile, "mods");
            Objects = Path.Combine(compile, "objects");
            Work = Path.Combine(compile, "work");
            foreach (string directory in new[]
            {
                Path.Combine(Zig, "lib", "include"), Path.Combine(Recompile, "include"),
                Path.Combine(Recompile, "Fable2", "hooks"), Generated, Work,
            })
            {
                Directory.CreateDirectory(directory);
            }

            PchHeader = Path.Combine(Generated, "refii_pch.h");
            SdkHeader = Path.Combine(Recompile, "include", "sdk.h");
            CombatHeader = Path.Combine(Recompile, "Fable2", "hooks", "combat.h");
            Combat = Path.Combine(Recompile, "Fable2", "hooks", "combat.cpp");
            Other = Path.Combine(Recompile, "Fable2", "hooks", "other.cpp");
            File.WriteAllText(Path.Combine(Zig, "lib", "include", "stddef.h"), "// zig stddef\n");
            File.WriteAllText(SdkHeader, "#pragma once\nint sdk_v = 1;\n");
            File.WriteAllText(PchHeader, "#pragma once\n#include <stddef.h>\n#include \"sdk.h\"\n");
            File.WriteAllText(CombatHeader, "#pragma once\nint combat_v = 1;\n");
            File.WriteAllText(Combat, "#include \"combat.h\"\nint combat() { return combat_v; }\n");
            File.WriteAllText(Other, "int other() { return 2; }\n");
            CompileCache.ClearMemoizedHashes();
        }

        public string Salt => Generated + "\0" + Recompile;

        public CompileCache Open(string recipe = Recipe, string? compilerDirectory = null) => new(
            Objects,
            new[]
            {
                CompileInputRoot.ImmutableToolchain("toolchain/", Zig),
                CompileInputRoot.DiskHashed("recompile/", Recompile, trackInputsAndShadowing: true),
                CompileInputRoot.DiskHashed("mods/", Mods),
                CompileInputRoot.DiskHashed("generated/", Generated),
            },
            new[] { "Fable2/hooks/combat.cpp", "Fable2/hooks/combat.h", "Fable2/hooks/other.cpp", "include/sdk.h" },
            new[] { "include" },
            recipe,
            compilerDirectory ?? Recompile);

        /// <summary>Compiles the header as the launcher does: its depfile names zig's header relative to the compiler.</summary>
        public string BuildPch(CompileCache cache)
        {
            string pch = Path.Combine(Work, "refii_pch.pch");
            File.WriteAllText(pch, "PCH");
            File.WriteAllText(pch + ".d", Rule(pch, PchHeader, ZigHeader, SdkHeader));
            return cache.StorePch(pch + ".d", pch, PchHeader, Salt);
        }

        /// <summary>Compiles one unit with the header; its depfile lists the header's inputs again, as clang does.</summary>
        public string BuildUnit(CompileCache cache, string source, string pch, params string[] own)
        {
            string obj = Path.Combine(Work, Path.GetFileNameWithoutExtension(source) + "-" + Guid.NewGuid().ToString("N") + ".o");
            File.WriteAllText(obj, "OBJ " + File.ReadAllText(source));
            File.WriteAllText(obj + ".d", Rule(obj, new[] { source }.Concat(own).Concat(new[] { pch, PchHeader, ZigHeader, SdkHeader }).ToArray()));
            return cache.Store(source, obj, obj + ".d", usesPch: true);
        }

        /// <summary>A make rule as clang writes it: spaces escaped, one prerequisite per continued line.</summary>
        private static string Rule(string target, params string[] inputs) =>
            Escape(target) + ": \\\n" + string.Join(" \\\n", inputs.Select(input => "  " + Escape(input))) + "\n";

        private static string Escape(string path) => path.Replace(" ", "\\ ");

        public void Dispose()
        {
            Directory.Delete(Path.GetDirectoryName(Root)!, recursive: true);
        }
    }

    /// <summary>A build with zig-shaped dependency files keeps every unit, and the next build reuses all of them.</summary>
    [TestCase]
    public void RelativeToolchainDependenciesAreNamedAndEveryUnitIsReused()
    {
        using var install = new Install();
        CompileCache first = install.Open();
        AssertThat(first.Find(install.Combat)).IsNull();
        string pch = install.BuildPch(first);
        AssertThat(first.PchFailure).IsNull();
        AssertThat(pch.StartsWith(install.Objects, StringComparison.Ordinal)).IsTrue();
        string combat = install.BuildUnit(first, install.Combat, pch, install.CombatHeader);
        string other = install.BuildUnit(first, install.Other, pch);
        AssertThat(first.StoreFailure).IsNull();
        AssertThat(first.NotKept).IsEqual(0);
        AssertThat(first.Complete).IsTrue();
        AssertThat(first.TrackedInputs.Contains("include/sdk.h")).IsTrue();
        AssertThat(first.TrackedInputs.Contains("Fable2/hooks/combat.h")).IsTrue();
        AssertThat(first.TrackedInputs.Any(input => input.StartsWith("zig/", StringComparison.Ordinal))).IsFalse();
        first.KeepOnlyUsed(GameCodeBuild.ObjectCacheMaxGenerations, GameCodeBuild.ObjectCacheMaxBytes);

        CompileCache second = install.Open();
        AssertThat(second.Find(install.Combat)).IsEqual(combat);
        AssertThat(second.Find(install.Other)).IsEqual(other);
        AssertThat(second.FindPchArtifact(install.PchHeader, install.Salt)).IsEqual(pch);
    }

    /// <summary>A relative dependency read against any directory but the compiler's is refused, and the reason names
    /// the path as resolved and as written, for the header and for each unit.</summary>
    [TestCase]
    public void ARelativeDependencyOutsideTheCompilerDirectoryIsRefusedWithItsReason()
    {
        using var install = new Install();
        CompileCache cache = install.Open(compilerDirectory: install.Root);
        string pch = install.BuildPch(cache);
        string unrooted = "an input under no root: " + Path.Combine(install.Root, "zig", "lib", "include", "stddef.h")
            + " (written as " + ZigHeader + ")";
        AssertThat(pch).IsEqual(Path.Combine(install.Work, "refii_pch.pch"));
        AssertThat(cache.PchFailure).IsEqual(unrooted);

        string kept = install.BuildUnit(cache, install.Other, pch);
        AssertThat(kept.StartsWith(install.Work, StringComparison.Ordinal)).IsTrue();
        AssertThat(cache.StoreFailure).IsEqual(unrooted + " for " + install.Other);
        AssertThat(cache.NotKept).IsEqual(1);
        AssertThat(cache.Complete).IsFalse();
    }

    /// <summary>A unit whose own inputs are all named but whose header went unrecorded says why the header was not.</summary>
    [TestCase]
    public void AnUnrecordedHeaderIsReportedWithItsOwnReason()
    {
        using var install = new Install();
        CompileCache cache = install.Open();
        string pch = Path.Combine(install.Work, "refii_pch.pch");
        File.WriteAllText(pch, "PCH");
        AssertThat(cache.StorePch(pch + ".d", pch, install.PchHeader, install.Salt)).IsEqual(pch);
        AssertThat(cache.PchFailure).IsEqual("no dependency file at " + pch + ".d");

        install.BuildUnit(cache, install.Other, pch);
        AssertThat(cache.StoreFailure).IsEqual(
            "no precompiled-header record for " + install.Other + ": no dependency file at " + pch + ".d");

        string obj = Path.Combine(install.Work, "nodeps.o");
        File.WriteAllText(obj, "OBJ");
        AssertThat(cache.Store(install.Combat, obj, obj + ".d", usesPch: false)).IsEqual(obj);
        AssertThat(cache.NotKept).IsEqual(2);
        AssertThat(cache.StoreFailure!.StartsWith("no precompiled-header record", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>An edited hook or hook header recompiles only its unit; the generated header, the recipe, and a damaged
    /// object each invalidate what depends on them.</summary>
    [TestCase]
    public void EachInputInvalidatesOnlyWhatReadsIt()
    {
        using var install = new Install();
        CompileCache first = install.Open();
        string pch = install.BuildPch(first);
        install.BuildUnit(first, install.Combat, pch, install.CombatHeader);
        string other = install.BuildUnit(first, install.Other, pch);
        first.KeepOnlyUsed(GameCodeBuild.ObjectCacheMaxGenerations, GameCodeBuild.ObjectCacheMaxBytes);

        File.WriteAllText(install.Combat, "#include \"combat.h\"\nint combat() { return combat_v + 1; }\n");
        CompileCache edited = install.Open();
        AssertThat(edited.Find(install.Combat)).IsNull();
        AssertThat(edited.Find(install.Other)).IsEqual(other);
        string editedPch = edited.FindPchArtifact(install.PchHeader, install.Salt)!;
        AssertThat(editedPch).IsEqual(pch);
        string combat = install.BuildUnit(edited, install.Combat, editedPch, install.CombatHeader);
        AssertThat(edited.StoreFailure).IsNull();
        edited.KeepOnlyUsed(GameCodeBuild.ObjectCacheMaxGenerations, GameCodeBuild.ObjectCacheMaxBytes);

        File.WriteAllText(install.CombatHeader, "#pragma once\nint combat_v = 2;\n");
        CompileCache header = install.Open();
        AssertThat(header.Find(install.Combat)).IsNull();
        AssertThat(header.Find(install.Other)).IsEqual(other);
        File.WriteAllText(install.CombatHeader, "#pragma once\nint combat_v = 1;\n");
        CompileCache.ClearMemoizedHashes();
        AssertThat(install.Open().Find(install.Combat)).IsEqual(combat);

        AssertThat(install.Open("recipe-2").Find(install.Other)).IsNull();

        File.WriteAllText(install.PchHeader, "#pragma once\n#include <stddef.h>\n#include \"sdk.h\"\nint more;\n");
        CompileCache generated = install.Open();
        AssertThat(generated.Find(install.Combat)).IsNull();
        AssertThat(generated.Find(install.Other)).IsNull();
        AssertThat(generated.FindPchArtifact(install.PchHeader, install.Salt)).IsNull();
        File.WriteAllText(install.PchHeader, "#pragma once\n#include <stddef.h>\n#include \"sdk.h\"\n");
        CompileCache.ClearMemoizedHashes();

        AssertThat(install.Open().Find(install.Other)).IsEqual(other);
        File.WriteAllText(other, "DAMAGED");
        AssertThat(install.Open().Find(install.Other)).IsNull();
    }
}
