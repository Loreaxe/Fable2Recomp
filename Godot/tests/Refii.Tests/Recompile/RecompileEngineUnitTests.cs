using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GdUnit4;
using Refii.Recompile;
using static GdUnit4.Assertions;

namespace Refii.Tests.Recompile;

/// <summary>
/// Unit tests for the shared cross-platform <see cref="Refii.Recompile"/> engine (<see cref="CompileDepfile"/>,
/// <see cref="CodegenTransforms"/>, <see cref="CodegenCache"/>, <see cref="CompileCache"/>, and <see cref="CompileScheduler"/>).
/// </summary>
[TestSuite]
public sealed class RecompileEngineUnitTests
{
    /// <summary>Verifies <see cref="CompileDepfile.Parse"/> handles Windows drive colons, escaped spaces, hashes, dollars, and CRLF continuations.</summary>
    [TestCase]
    public void DepfileParserHandlesWindowsDriveLettersEscapesAndContinuations()
    {
        string depText =
            "C:\\Build\\work\\0001.o: C:\\Games\\Fable\\ 2\\generated\\refii_recomp.0.cpp \\\r\n" +
            "  C:/Games/Fable\\ 2/include/header\\#1.h \\\n" +
            @"  D:\SDK\include\dollar$$file.hpp \" + "\r\n" +
            @"  E:\Trailing\\\ space.h E:\Dir\\ next.h" + "\r\n" +
            "\r\n" +
            @"C:\Games\Fable\ 2\include\header\#1.h:" + "\r\n";

        List<string> parsed = CompileDepfile.Parse(depText);
        AssertThat(parsed.ToArray()).IsEqual(new[]
        {
            @"C:\Games\Fable 2\generated\refii_recomp.0.cpp",
            "C:/Games/Fable 2/include/header#1.h",
            @"D:\SDK\include\dollar$file.hpp",
            @"E:\Trailing\ space.h",
            @"E:\Dir\",
            "next.h",
        });
    }

    /// <summary>Verifies <see cref="CompileDepfile.Parse"/> supports multiple dependency-bearing rules and multi-target rules.</summary>
    [TestCase]
    public void DepfileParserHandlesMultiTargetAndMultipleRules()
    {
        string depText =
            "/work/a.o /work/a.d: /src/a.cpp /inc/common.h\n" +
            "/work/a.o: /inc/extra.h\n";

        List<string> parsed = CompileDepfile.Parse(depText);
        AssertThat(parsed.ToArray()).IsEqual(new[]
        {
            "/src/a.cpp",
            "/inc/common.h",
            "/inc/extra.h",
        });
    }

    /// <summary>Verifies <see cref="CodegenTransforms.ThreadLocalLockDepth"/> upgrades the legacy lock block once and is idempotent.</summary>
    [TestCase]
    public void ThreadLocalLockDepthUpgradesOnceAndIsIdempotent()
    {
        string legacyHeader = "#pragma once\n" + CodegenTransforms.ProcessWideGuestLock.ReplaceLineEndings("\n") + "// footer\n";
        string upgraded = CodegenTransforms.ThreadLocalLockDepth(legacyHeader);

        AssertThat(upgraded.Contains("ppc_global_lock_depth_()", StringComparison.Ordinal)).IsTrue();
        AssertThat(upgraded.Contains("ppc_global_lock_count_()", StringComparison.Ordinal)).IsFalse();
        AssertThat(CodegenTransforms.ThreadLocalLockDepth(upgraded)).IsEqual(upgraded);
    }

    /// <summary>Verifies <see cref="CodegenTransforms.BoundRegistration"/> chunks >512 SetFunction calls while preserving call order.</summary>
    [TestCase]
    public void BoundRegistrationSplitsLargeFunctionAndPreservesSmallFunction()
    {
        string small =
            "#include \"refii_init.h\"\n" +
            "void refii_RegisterFunctions(rex::runtime::IModuleRegistrar* registrar) {\n" +
            "  registrar->SetFunction(0x82000000, sub_82000000);\n" +
            "}\n";
        AssertThat(CodegenTransforms.BoundRegistration(small)).IsEqual(small);

        var lines = Enumerable.Range(0, 1100)
            .Select(i => $"  registrar->SetFunction(0x{0x82000000u + (uint)(i * 4):X8}, sub_{0x82000000u + (uint)(i * 4):X8});");
        string large =
            "#include \"refii_init.h\"\n" +
            "void refii_RegisterFunctions(rex::runtime::IModuleRegistrar* registrar) {\n" +
            string.Join("\n", lines) + "\n}\n";

        string chunked = CodegenTransforms.BoundRegistration(large);
        AssertThat(chunked.Contains("refii_RegisterFunctions_PhonePart_0(registrar);", StringComparison.Ordinal)).IsTrue();
        AssertThat(chunked.Contains("refii_RegisterFunctions_PhonePart_1(registrar);", StringComparison.Ordinal)).IsTrue();
        AssertThat(chunked.Contains("refii_RegisterFunctions_PhonePart_2(registrar);", StringComparison.Ordinal)).IsTrue();
        AssertThat(chunked.Contains("refii_RegisterFunctions_PhonePart_3", StringComparison.Ordinal)).IsFalse();
        AssertThat(chunked.Contains("sub_82000000", StringComparison.Ordinal)).IsTrue();
        AssertThat(chunked.Contains($"sub_{0x82000000u + 1099u * 4:X8}", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>Verifies <see cref="CodegenTransforms.OptimizeRegistration"/> upgrades verified mappings, passes through table-driven TUs, and fails closed.</summary>
    [TestCase]
    public void OptimizeRegistrationUpgradesVerifiedMappingsAndFallsBackSafely()
    {
        var setCalls = Enumerable.Range(0, 600)
            .Select(i => $"  registrar->SetFunction(0x{0x82000000u + (uint)(i * 4):X8}, sub_{0x82000000u + (uint)(i * 4):X8});");
        var mappingRows = Enumerable.Range(0, 600)
            .Select(i => $"    {{ 0x{0x82000000u + (uint)(i * 4):X8}, sub_{0x82000000u + (uint)(i * 4):X8} }},");

        foreach (string prefix in new[] { "refii", "refiii" })
        {
            string fnName = prefix + "_RegisterFunctions";
            string legacyRegister =
                $"#include \"{prefix}_funcs.h\"\n" +
                $"void {fnName}(rex::runtime::IModuleRegistrar* registrar) {{\n" +
                string.Join("\n", setCalls) + "\n}\n";
            string matchingInit =
                $"#include \"{prefix}_pch.h\"\n" +
                "PPCFuncMapping PPCFuncMappings[] = {\n" +
                string.Join("\n", mappingRows) + "\n" +
                "    { 0, nullptr }\n" +
                "};\n";

            string upgraded = CodegenTransforms.OptimizeRegistration(legacyRegister, matchingInit, fnName);
            AssertThat(upgraded.Contains($"#include \"{prefix}_pch.h\"", StringComparison.Ordinal)).IsTrue();
            AssertThat(upgraded.Contains($"{prefix}_funcs.h", StringComparison.Ordinal)).IsFalse();
            AssertThat(upgraded.Contains("for (const PPCFuncMapping* m = PPCFuncMappings;", StringComparison.Ordinal)).IsTrue();

            // Already table-driven output passes through both OptimizeRegistration and BoundRegistration unchanged.
            AssertThat(CodegenTransforms.OptimizeRegistration(upgraded, null, fnName)).IsEqual(upgraded);
            AssertThat(CodegenTransforms.BoundRegistration(upgraded, fnName)).IsEqual(upgraded);

            // Missing or mismatched PPCFuncMappings falls back to 512-call BoundRegistration chunks.
            string fallbackNull = CodegenTransforms.OptimizeRegistration(legacyRegister, null, fnName);
            AssertThat(fallbackNull.Contains($"{fnName}_PhonePart_0(registrar);", StringComparison.Ordinal)).IsTrue();

            string mismatchedInit = matchingInit.Replace("sub_82000000", "sub_82000004", StringComparison.Ordinal);
            string fallbackMismatch = CodegenTransforms.OptimizeRegistration(legacyRegister, mismatchedInit, fnName);
            AssertThat(fallbackMismatch.Contains($"{fnName}_PhonePart_0(registrar);", StringComparison.Ordinal)).IsTrue();
        }

        // Unknown / unsafe statements inside registration body still throw InvalidDataException (fail-closed).
        string malformed =
            "#include \"refii_init.h\"\n" +
            "void refii_RegisterFunctions(rex::runtime::IModuleRegistrar* registrar) {\n" +
            "  system(\"bad\");\n" +
            "}\n";
        bool threw = false;
        try
        {
            CodegenTransforms.OptimizeRegistration(malformed, null);
        }
        catch (InvalidDataException)
        {
            threw = true;
        }

        AssertThat(threw).IsTrue();
    }

    /// <summary>Verifies <see cref="CodegenTransforms.ReadGeneratedSources"/> validates source paths and rejects escapes.</summary>
    [TestCase]
    public void ReadGeneratedSourcesValidatesRootContainment()
    {
        string tempRoot = Path.Combine(TestTemp.Root, "recompile_sources_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string genDir = Path.Combine(tempRoot, "generated");
            Directory.CreateDirectory(genDir);
            string shard0 = Path.Combine(genDir, "refii_recomp.0.cpp");
            File.WriteAllText(shard0, "// shard 0\n");
            string listPath = Path.Combine(genDir, "sources.cmake");
            File.WriteAllText(listPath, "set(GENERATED_SOURCES\n  \"${CMAKE_CURRENT_LIST_DIR}/refii_recomp.0.cpp\" # comment\n)\n");

            string[] valid = CodegenTransforms.ReadGeneratedSources(listPath, genDir);
            AssertThat(valid.Length).IsEqual(1);
            AssertThat(valid[0]).IsEqual(Path.GetFullPath(shard0));

            string outside = Path.Combine(tempRoot, "escape.cpp");
            File.WriteAllText(outside, "// escape\n");
            File.WriteAllText(listPath, "set(GENERATED_SOURCES \"${CMAKE_CURRENT_LIST_DIR}/../escape.cpp\")\n");

            bool threw = false;
            try
            {
                CodegenTransforms.ReadGeneratedSources(listPath, genDir);
            }
            catch (InvalidDataException)
            {
                threw = true;
            }

            AssertThat(threw).IsTrue();
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    /// <summary>Verifies <see cref="CodegenTransforms.RunWithPartitionSeedAsync"/> retries cleanly when a stale seed is rejected.</summary>
    [TestCase]
    public async Task PartitionSeedRetriesCleanlyWhenRejected()
    {
        string tempRoot = Path.Combine(TestTemp.Root, "partition_seed_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string cacheDir = Path.Combine(tempRoot, "cache");
            string genDir = Path.Combine(tempRoot, "generated");
            Directory.CreateDirectory(cacheDir);
            File.WriteAllText(Path.Combine(cacheDir, CodegenTransforms.PartitionFile), "corrupt-seed");

            int attempts = 0;
            bool retryPrepared = false;
            await CodegenTransforms.RunWithPartitionSeedAsync(
                cacheDir,
                genDir,
                () =>
                {
                    attempts++;
                    string seeded = Path.Combine(genDir, CodegenTransforms.PartitionFile);
                    if (File.Exists(seeded) && File.ReadAllText(seeded) == "corrupt-seed")
                    {
                        throw new InvalidDataException("Bad partition seed");
                    }

                    Directory.CreateDirectory(genDir);
                    File.WriteAllText(seeded, "{\"shards\":1}");
                    return Task.CompletedTask;
                },
                prepareBeforeRetry: () => retryPrepared = true);

            AssertThat(attempts).IsEqual(2);
            AssertThat(retryPrepared).IsTrue();
            AssertThat(File.ReadAllText(Path.Combine(cacheDir, CodegenTransforms.PartitionFile))).IsEqual("{\"shards\":1}");
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    /// <summary>Verifies <see cref="CodegenCache"/> reuses intact output, removes stray files, and invalidates on edits or digest changes.</summary>
    [TestCase]
    public void CodegenCacheVerifiesGeneratedTreeAndPrunesStrays()
    {
        string tempRoot = Path.Combine(TestTemp.Root, "codegen_cache_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string genDir = Path.Combine(tempRoot, "generated");
            Directory.CreateDirectory(genDir);
            File.WriteAllText(Path.Combine(genDir, "refii_pch.h"), "#pragma once\n");
            File.WriteAllText(Path.Combine(genDir, "refii_recomp.0.cpp"), "#include \"refii_pch.h\"\n");
            File.WriteAllText(Path.Combine(genDir, "sources.cmake"), "set(GENERATED_SOURCES \"${CMAKE_CURRENT_LIST_DIR}/refii_recomp.0.cpp\")\n");
            File.WriteAllText(Path.Combine(genDir, "refii_manifest.effective.toml"), "transient\n");

            var ignored = new HashSet<string>(StringComparer.Ordinal) { "refii_manifest.effective.toml" };
            string digest1 = CodegenCache.ComputeInputDigest(
                "AABBCC",
                new[] { ("refii_manifest.toml", new byte[] { 1, 2, 3 }) },
                "gen-v1",
                "0.10.0");

            CodegenCache.Record(genDir, digest1, ignored);

            // Add an unlisted stray file; TryReuse should remove it while keeping ignored files and returning true.
            string stray = Path.Combine(genDir, "stray.tmp");
            File.WriteAllText(stray, "junk");
            AssertThat(CodegenCache.TryReuse(genDir, digest1, ignored)).IsTrue();
            AssertThat(File.Exists(stray)).IsFalse();
            AssertThat(File.Exists(Path.Combine(genDir, "refii_manifest.effective.toml"))).IsTrue();

            // Digest change must miss.
            string digest2 = CodegenCache.ComputeInputDigest(
                "AABBCC",
                new[] { ("refii_manifest.toml", new byte[] { 1, 2, 4 }) },
                "gen-v1",
                "0.10.0");
            AssertThat(CodegenCache.TryReuse(genDir, digest2, ignored)).IsFalse();

            // Same-length content tamper inside generated file must miss even within the same clock second.
            File.WriteAllText(Path.Combine(genDir, "refii_recomp.0.cpp"), "#include \"refii_pch.X\"\n");
            AssertThat(CodegenCache.TryReuse(genDir, digest1, ignored)).IsFalse();
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    /// <summary>Verifies <see cref="CompileCache"/> binary PCH caching, per-unit extra flag isolation, and 3-generation retention.</summary>
    [TestCase]
    public void CompileCacheSupportsBinaryPchExtraFlagsAndGenerationalRetention()
    {
        string tempRoot = Path.Combine(TestTemp.Root, "compile_cache_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string cacheDir = Path.Combine(tempRoot, "objects");
            string recompileDir = Path.Combine(tempRoot, "recompile");
            string zigDir = Path.Combine(recompileDir, "zig");
            string genDir = Path.Combine(tempRoot, "generated");
            string workDir = Path.Combine(tempRoot, "work");
            Directory.CreateDirectory(Path.Combine(zigDir, "lib"));
            Directory.CreateDirectory(Path.Combine(recompileDir, "hooks"));
            Directory.CreateDirectory(Path.Combine(recompileDir, "include"));
            Directory.CreateDirectory(genDir);
            Directory.CreateDirectory(workDir);

            string sysHeader = Path.Combine(zigDir, "lib", "stdint.h");
            string sdkHeader = Path.Combine(recompileDir, "include", "sdk.h");
            string pchHeader = Path.Combine(genDir, "refii_pch.h");
            string hookCpp = Path.Combine(recompileDir, "hooks", "combat.cpp");
            File.WriteAllText(sysHeader, "// toolchain stdint\n");
            File.WriteAllText(sdkHeader, "#pragma once\nint sdk_v = 1;\n");
            File.WriteAllText(pchHeader, "#pragma once\n#include \"sdk.h\"\n");
            File.WriteAllText(hookCpp, "int combat() { return 1; }\n");

            CompileInputRoot[] MakeRoots() => new[]
            {
                CompileInputRoot.ImmutableToolchain("toolchain/", zigDir),
                CompileInputRoot.DiskHashed("recompile/", recompileDir, trackInputsAndShadowing: true),
                CompileInputRoot.DiskHashed("generated/", genDir, trackInputsAndShadowing: false),
            };

            string[] trackedRel = { "hooks/combat.cpp", "include/sdk.h" };
            string[] includes = { "include" };
            string salt = genDir + "\0" + recompileDir;

            // Generation 1: vanilla hook (no extra flags) + binary PCH stored.
            var gen1 = new CompileCache(cacheDir, MakeRoots(), trackedRel, includes, "recipe-1", workDir);
            string pchOut = Path.Combine(workDir, "refii_pch.pch");
            string pchDep = pchOut + ".d";
            File.WriteAllText(pchOut, "PCH-BYTES-V1");
            File.WriteAllText(pchDep, $"{pchOut}: {pchHeader} {sdkHeader} {sysHeader}\n");
            string storedPch = gen1.StorePch(pchDep, pchOut, pchHeader, salt);
            AssertThat(File.Exists(storedPch)).IsTrue();

            string obj1 = Path.Combine(workDir, "combat1.o");
            string dep1 = obj1 + ".d";
            File.WriteAllText(obj1, "OBJ-VANILLA");
            File.WriteAllText(dep1, $"{obj1}: {hookCpp} {pchHeader} {sdkHeader} {sysHeader}\n");
            string cachedVanilla = gen1.Store(hookCpp, obj1, dep1, usesPch: true);
            gen1.KeepOnlyUsed(maxGenerations: 3, maxBytes: 1024 * 1024);

            // Generation 2: same hook compiled WITH chain define (-Drex_A=rex_A__refii) must miss vanilla and store separately.
            var chainFlags = new[] { "-Drex_A=rex_A__refii" };
            var gen2 = new CompileCache(cacheDir, MakeRoots(), trackedRel, includes, "recipe-1", workDir);
            AssertThat(gen2.FindPchArtifact(pchHeader, salt)).IsEqual(storedPch);
            AssertThat(gen2.Find(hookCpp, chainFlags)).IsNull();

            string obj2 = Path.Combine(workDir, "combat2.o");
            string dep2 = obj2 + ".d";
            File.WriteAllText(obj2, "OBJ-CHAINED");
            File.WriteAllText(dep2, $"{obj2}: {hookCpp} {sdkHeader} {sysHeader}\n");
            string cachedChained = gen2.Store(hookCpp, obj2, dep2, usesPch: false, chainFlags);
            AssertThat(cachedChained == cachedVanilla).IsFalse();
            gen2.KeepOnlyUsed(maxGenerations: 3, maxBytes: 1024 * 1024);

            // Generation 3: disabling the chain must still hit Generation 1's vanilla object!
            var gen3 = new CompileCache(cacheDir, MakeRoots(), trackedRel, includes, "recipe-1", workDir);
            AssertThat(gen3.Find(hookCpp)).IsEqual(cachedVanilla);
            gen3.KeepOnlyUsed(maxGenerations: 3, maxBytes: 1024 * 1024);

            // Corrupting the cached binary PCH file on disk must force FindPchArtifact to return null.
            File.WriteAllText(storedPch, "CORRUPTED-PCH");
            var gen4 = new CompileCache(cacheDir, MakeRoots(), trackedRel, includes, "recipe-1", workDir);
            AssertThat(gen4.FindPchArtifact(pchHeader, salt)).IsNull();
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    /// <summary>Verifies LPT unit ordering and Desktop/Android concurrency planning formulas in <see cref="CompileScheduler"/>.</summary>
    [TestCase]
    public void SchedulerOrdersLargestUnitsFirstAndPlansJobsSafely()
    {
        string tempRoot = Path.Combine(TestTemp.Root, "scheduler_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string small = Path.Combine(tempRoot, "small.cpp");
            string mediumA = Path.Combine(tempRoot, "medA.cpp");
            string mediumB = Path.Combine(tempRoot, "medB.cpp");
            string huge = Path.Combine(tempRoot, "huge.cpp");
            File.WriteAllBytes(small, new byte[10]);
            File.WriteAllBytes(mediumA, new byte[500]);
            File.WriteAllBytes(mediumB, new byte[500]);
            File.WriteAllBytes(huge, new byte[10_000]);

            string[] paths = { small, mediumA, huge, mediumB };
            List<int> ordered = CompileScheduler.OrderUnitsLargestFirst(new[] { 0, 1, 2, 3 }, paths);
            AssertThat(ordered.ToArray()).IsEqual(new[] { 2, 1, 3, 0 });

            long abundantRam = 32L * 1024 * 1024 * 1024;
            AssertThat(CompileScheduler.PlanDesktopJobs(cores: 16, abundantRam, useAllCores: false)).IsEqual(14);
            AssertThat(CompileScheduler.PlanDesktopJobs(cores: 16, abundantRam, useAllCores: true)).IsEqual(16);
            AssertThat(CompileScheduler.PlanDesktopJobs(cores: 2, abundantRam, useAllCores: false)).IsEqual(1);
            AssertThat(CompileScheduler.PlanDesktopJobs(cores: 1, abundantRam, useAllCores: false)).IsEqual(1);

            // Tight RAM (2.3 GiB free -> floor((2.3 - 1.0) GiB / 600 MiB) = 2 workers) caps concurrency below CPU target.
            long tightRam = 2400L * 1024 * 1024;
            AssertThat(CompileScheduler.PlanDesktopJobs(cores: 16, tightRam, useAllCores: true)).IsEqual(2);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
