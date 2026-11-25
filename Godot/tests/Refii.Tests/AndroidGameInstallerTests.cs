using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Launcher.Stfs;
using Refii.Launcher.Core;
using Refii.Launcher.Xiso;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Unit and full synthetic disc/update/compile/activate/restart/cleanup lifecycle coverage without Godot GUI objects.</summary>
[TestSuite]
public sealed class AndroidGameInstallerTests
{
    /// <summary>Disc and renamed TU headers are recognized by their contents without repeating the complete extraction lifecycle.</summary>
    [TestCase]
    public void DiscAndPackageHeadersAreRecognizedIndependentOfNames()
    {
        using var fixture = new DiscFixture();
        AssertThat(XisoExtractor.TryReadRootFileHead(fixture.Image, "default.xex", 64 * 1024, out byte[] head, out _)).IsTrue();
        AssertThat(TitleUpdate.TryReadExecutable(head, out var executable, out _)).IsTrue();
        AssertThat(TitleUpdate.TryLoad(fixture.Update, out var update, out _)).IsTrue();
        AssertThat(update.SourceKind).IsEqual("title update package");
        AssertThat(update.AppliesTo(executable, out _)).IsTrue();
        AssertThat(Directory.GetFileSystemEntries(fixture.User).Length).IsEqual(0);
        fixture.AssertOriginals();
    }

    /// <summary>One coherent feature lifecycle publishes first-install settings, resumes completed output and keeps old saves/config.</summary>
    [TestCase]
    public async Task FreshInstallCompileActivationRestartAndCleanupLifecycle()
    {
        using var disc = new DiscFixture();
        using var compiler = new AndroidPhoneCompilerTests.Fixture();
        var game = disc.Prepare();
        AssertThat(game.Files).IsEqual(4);
        AssertThat(File.ReadAllBytes(Path.Combine(game.GameRoot, "default.xex"))).IsEqual(Xex(false));
        AssertThat(File.ReadAllBytes(Path.Combine(game.GameRoot, "default.xexp"))).IsEqual(Xex(true));
        AssertThat(File.ReadAllText(Path.Combine(game.GameRoot, "data", "test.gdb.tu"))).IsEqual("patch");
        AssertThat(Directory.Exists(Path.Combine(game.Directory, "input"))).IsFalse();
        AssertThat(File.Exists(Path.Combine(game.Directory, "extracted-files.json"))).IsTrue();
        var request = compiler.Request with { GameDirectory = game.GameRoot,
            PrivateDirectory = Path.Combine(disc.User, "phone-builds") };
        var result = await AndroidPhoneCompiler.CompileAsync(request, _ => { }, CancellationToken.None, compiler.Generate);
        AndroidInstallState.Save(disc.User, new(game, result));
        string log = Path.Combine(result.Directory, "command-0.log");
        File.WriteAllText(log, new string('x', 70000) + "\ntool_exit=0\n");
        AndroidInstallState.TrimBuild(result.Directory, true);
        AssertThat(File.Exists(result.Module)).IsTrue();
        AssertThat(AndroidPhoneLauncher.CanActivate(result)).IsTrue();
        AssertThat(Directory.Exists(Path.Combine(result.Directory, "generated"))).IsFalse();
        AssertThat(Directory.Exists(Path.Combine(result.Directory, "input"))).IsFalse();
        AssertThat(Directory.GetFiles(result.Directory, "*.o").Length).IsEqual(0);
        AssertThat(new FileInfo(log).Length).IsEqual(65536L);
        AssertThat(File.ReadAllText(log).EndsWith("tool_exit=0\n", StringComparison.Ordinal)).IsTrue();
        string config = Path.Combine(disc.User, AndroidRuntimeOptions.FileName);
        AssertThat(File.Exists(config)).IsFalse();
        var active = AndroidInstallState.Activate(disc.User, game.GameRoot, result);
        AssertThat(active.GameRoot).IsEqual(game.GameRoot);
        AssertThat(active.ModulePath).IsEqual(result.Module);
        AssertThat(active.SaveImportRoot == null).IsTrue();
        var resumed = AndroidInstallState.Load(disc.User);
        AssertThat(resumed.Compiled!.Sha256).IsEqual(result.Sha256);
        AssertThat(resumed.Game!.GameRoot).IsEqual(game.GameRoot);
        byte[] previous = File.ReadAllBytes(config);
        string save = Path.Combine(disc.User, "runtime", "assets", "save", "progress");
        Directory.CreateDirectory(Path.GetDirectoryName(save)!);
        File.WriteAllText(save, "player progress");
        // Replacement activation uses identical synthetic compiled bytes in another
        // owned result; repeated generation is covered by the compiler lifecycle suite.
        string secondDirectory = Path.Combine(disc.User, "phone-builds", "phone-build-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(secondDirectory);
        var second = result with { Directory = secondDirectory, Module = Path.Combine(secondDirectory, "librefii_generated.so") };
        File.Copy(result.Module, second.Module);
        File.WriteAllText(Path.Combine(secondDirectory, "completed.json"), JsonSerializer.Serialize(second));
        AndroidInstallState.Activate(disc.User, game.GameRoot, second);
        AssertThat(File.ReadAllBytes(Path.Combine(second.Directory, "previous-android-runtime.json"))).IsEqual(previous);
        AssertThat(File.ReadAllText(save)).IsEqual("player progress");
        File.AppendAllText(second.Module, "changed");
        AssertThat(Fails(() => AndroidInstallState.Activate(disc.User, game.GameRoot, second))).IsTrue();
        AssertThat(AndroidRuntimeOptions.Parse(File.ReadAllText(config), disc.User).ModulePath).IsEqual(second.Module);
        disc.AssertOriginals();
        compiler.AssertPreserved();
    }

    /// <summary>Invalid discs and mismatched updates fail before publication, clear owned partial assets and preserve originals.</summary>
    [TestCase]
    public void InvalidInputAndCancellationCleanOwnedCopies()
    {
        using var fixture = new DiscFixture();
        foreach (int kind in new[] { 0, 1, 2 })
        {
            byte[] image = Iso();
            byte[] update = Package();
            if (kind == 0) image[0x10000] = 0;
            if (kind == 1) update[0xD000 + 0x340] ^= 1;
            using var cancellation = new CancellationTokenSource();
            bool failed = false;
            try
            {
                AndroidGameInstaller.Prepare((target, _, token) => {
                    File.WriteAllBytes(target, image);
                    if (kind == 2) cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }, (target, _, _) => File.WriteAllBytes(target, update), Path.Combine(fixture.User, "phone-installs"),
                    _ => { }, cancellation.Token);
            }
            catch (Exception error) when (error is InvalidDataException or OperationCanceledException) { failed = true; }
            AssertThat(failed).IsTrue();
        }
        foreach (string attempt in Directory.EnumerateDirectories(Path.Combine(fixture.User, "phone-installs")))
        {
            AssertThat(Directory.Exists(Path.Combine(attempt, "input"))).IsFalse();
            AssertThat(Directory.Exists(Path.Combine(attempt, "game"))).IsFalse();
            AssertThat(File.Exists(Path.Combine(attempt, "prepared.json"))).IsFalse();
        }
        fixture.AssertOriginals();
    }

    /// <summary>Cancellation during streamed asset extraction closes readers and clears the partial owned game tree.</summary>
    [TestCase]
    public void ExtractionCancellationDoesNotLeaveDiscCopies()
    {
        using var fixture = new DiscFixture();
        using var cancellation = new CancellationTokenSource();
        bool cancelled = false;
        try { fixture.Prepare(progress: message => {
            if (message.StartsWith("Extracting game files:", StringComparison.Ordinal)) cancellation.Cancel();
        }, cancellation: cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        AssertThat(cancelled).IsTrue();
        AssertThat(Directory.GetFiles(fixture.User, "disc.iso", SearchOption.AllDirectories).Length).IsEqual(0);
        fixture.AssertOriginals();
    }

    /// <summary>Unpatched discs and bare extracted update patches remain supported through the same validated import lifecycle.</summary>
    [TestCase]
    public void OptionalUpdateAndBarePatchAreContentValidated()
    {
        using var fixture = new DiscFixture();
        var unpatched = fixture.Prepare(withUpdate: false);
        AssertThat(unpatched.UpdateSha256 == null).IsTrue();
        AssertThat(File.Exists(Path.Combine(unpatched.GameRoot, "default.xexp"))).IsFalse();
        string bare = Path.Combine(fixture.Root, "bare.xexp");
        File.WriteAllBytes(bare, Xex(true));
        AssertThat(TitleUpdate.TryLoad(bare, out var patch, out _)).IsTrue();
        AssertThat(patch.SourceKind).IsEqual("patch file");
        AssertThat(patch.Patch).IsEqual(Xex(true));
        fixture.AssertOriginals();
    }

    /// <summary>Update hierarchy rejects traversal, cycles, ambiguous names and parents that are regular files.</summary>
    [TestCase]
    public void UpdateHierarchyFailsClosed()
    {
        foreach (var entries in new[] {
            new[] { Entry("../outside", 0xffff) },
            new[] { Entry("x", 0, true) },
            new[] { Entry("file", 0xffff), Entry("child", 0) },
            new[] { Entry("same", 0xffff), Entry("SAME", 0xffff) },
            new[] { Entry("x", 999) } })
            AssertThat(Fails(() => AndroidGameInstaller.ResolveEntries(entries))).IsTrue();
        AssertThat(AndroidGameInstaller.ResolveEntries(new[] { Entry("data", 0xffff, true), Entry("bank.tu", 0) }))
            .IsEqual(new[] { "data", Path.Combine("data", "bank.tu") });
    }

    /// <summary>Crash recovery removes only marked incomplete imports; complete prepared assets and unrelated files survive.</summary>
    [TestCase]
    public void RecoveryAndCacheCleanupRespectOwnership()
    {
        using var fixture = new DiscFixture();
        string preparedDirectory = Path.Combine(fixture.User, "phone-installs", "install-" + Guid.NewGuid().ToString("N"));
        string preparedGame = Path.Combine(preparedDirectory, "game");
        Directory.CreateDirectory(preparedGame);
        File.WriteAllText(Path.Combine(preparedDirectory, ".refii-install-owner"), Path.GetFileName(preparedDirectory));
        File.WriteAllText(Path.Combine(preparedDirectory, "prepared.json"), "complete fixture");
        File.WriteAllBytes(Path.Combine(preparedGame, "default.xex"), Xex(false));
        string failed = Path.Combine(fixture.User, "phone-installs", "install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(failed, "input"));
        Directory.CreateDirectory(Path.Combine(failed, "game"));
        File.WriteAllText(Path.Combine(failed, ".refii-install-owner"), Path.GetFileName(failed));
        File.WriteAllText(Path.Combine(failed, "input", "disc.iso"), "partial");
        AndroidInstallState.RecoverInterrupted(fixture.User);
        AssertThat(Directory.Exists(Path.Combine(failed, "input"))).IsFalse();
        AssertThat(File.Exists(Path.Combine(preparedGame, "default.xex"))).IsTrue();
        string bundle = Path.Combine(fixture.User, "phone-toolchain-" + new string('a', 64));
        Directory.CreateDirectory(bundle);
        File.WriteAllText(Path.Combine(bundle, "header"), "public fixture");
        AndroidInstallState.TrimBundle(fixture.User, bundle);
        AssertThat(Directory.Exists(bundle)).IsTrue();
        File.WriteAllText(Path.Combine(bundle, ".refii-toolchain-owner"), Path.GetFileName(bundle));
        AndroidInstallState.TrimBundle(fixture.User, bundle);
        AssertThat(Directory.Exists(bundle)).IsFalse();
        AssertThat(Fails(() => AndroidGameInstaller.DeleteOwnedChild(preparedDirectory, "../originals"))).IsTrue();
        fixture.AssertOriginals();
    }

    /// <summary>Only complete owned installs and builds that neither the active configuration nor the installer state names are removed.</summary>
    [TestCase]
    public void PruneRemovesOnlyUnusedCompleteOwnedFolders()
    {
        string user = Path.Combine(Refii.Tests.TestTemp.Root, "refii-prune-" + Guid.NewGuid().ToString("N"));
        try
        {
            string Folder(string root, string prefix, string marker, string? complete, string? owner = "")
            {
                string folder = Path.Combine(user, root, prefix + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(folder, "game"));
                File.WriteAllText(Path.Combine(folder, "game", "default.xex"), "fixture");
                if (owner != null) File.WriteAllText(Path.Combine(folder, marker), owner.Length > 0 ? owner : Path.GetFileName(folder));
                if (complete != null) File.WriteAllText(Path.Combine(folder, complete), "{}");
                return folder;
            }
            string Install(string? complete = "prepared.json", string? owner = "") =>
                Folder("phone-installs", "install-", ".refii-install-owner", complete, owner);
            string Build(string? complete = "completed.json", string? owner = "") =>
                Folder("phone-builds", "phone-build-", ".refii-build-owner", complete, owner);
            string Json(string path) => JsonSerializer.Serialize(path);
            void Runtime(string game, string module) => File.WriteAllText(Path.Combine(user, AndroidRuntimeOptions.FileName),
                "{\"schema\":1,\"game_root\":" + Json(game) + ",\"module_path\":" + Json(module) + "}");

            string active = Install(), prepared = Install(), old = Install(), partial = Install(complete: null), foreign = Install(owner: "install-other");
            string running = Build(), compiled = Build(), stale = Build(), unfinished = Build(complete: null);
            string pc = Path.Combine(user, "phone-builds", "phone-build-pc-e659a242");
            Directory.CreateDirectory(pc);
            File.WriteAllText(Path.Combine(pc, "completed.json"), "{}");
            Runtime(Path.Combine(active, "game"), Path.Combine(running, "librefii_generated.so"));
            File.WriteAllText(Path.Combine(user, "phone-installer-state.json"),
                "{\"Game\":{\"Directory\":" + Json(prepared) + "},\"Compiled\":{\"Directory\":" + Json(compiled) + "}}");

            AssertThat(AndroidInstallState.PruneUnused(user).OrderBy(name => name).ToArray())
                .IsEqual(new[] { Path.GetFileName(stale), Path.GetFileName(old) }.OrderBy(name => name).ToArray());
            foreach (string kept in new[] { active, prepared, partial, foreign, running, compiled, unfinished, pc })
                AssertThat(Directory.Exists(kept)).IsTrue();
            AssertThat(Directory.Exists(old) || Directory.Exists(stale)).IsFalse();

            // Nothing goes while the active game or module is not one of the phone's own, or the state is unreadable.
            string another = Install(), anotherBuild = Build();
            Runtime(Path.Combine(user, "pc-game"), Path.Combine(user, "pc-module", "librefii_generated.so"));
            AssertThat(AndroidInstallState.PruneUnused(user).Count).IsEqual(0);
            Runtime(Path.Combine(active, "game"), Path.Combine(running, "librefii_generated.so"));
            File.WriteAllText(Path.Combine(user, "phone-installer-state.json"), "{ not json");
            AssertThat(AndroidInstallState.PruneUnused(user).Count).IsEqual(0);
            File.Delete(Path.Combine(user, AndroidRuntimeOptions.FileName));
            AssertThat(AndroidInstallState.PruneUnused(user).Count).IsEqual(0);
            AssertThat(Directory.Exists(another) && Directory.Exists(anotherBuild)).IsTrue();
        }
        finally { if (Directory.Exists(user)) Directory.Delete(user, true); }
    }

    /// <summary>Persisted installer state cannot activate an escaped or corrupted compiled module.</summary>
    [TestCase]
    public void PersistedStateRejectsEscapedPaths()
    {
        using var fixture = new DiscFixture();
        var game = fixture.Prepare();
        AndroidInstallState.Save(fixture.User, new(game with { Directory = fixture.Root }, null));
        AssertThat(Fails(() => AndroidInstallState.Load(fixture.User))).IsTrue();
        fixture.AssertOriginals();
    }

    /// <summary>Accepts only the same application's two Android aliases while retaining app, user and prefix boundaries.</summary>
    [TestCase]
    public void PrivateStorageAliasBoundaries()
    {
        const string root = "/data/data/org.refii.test/files/phone-builds";
        const string canonical = "/data/user/0/org.refii.test/files/phone-builds/phone-build-a/librefii_generated.so";
        AssertThat(AndroidInstallState.MatchPrivateAlias(root, canonical)).IsEqual(canonical);
        AssertThat(AndroidInstallState.MatchPrivateAlias(root, canonical.Replace("/data/user/0/", "/data/data/"))).IsEqual(canonical);
        AssertThat(AndroidInstallState.MatchPrivateAlias(root, root)).IsEqual("/data/user/0/org.refii.test/files/phone-builds");
        foreach (string outside in new[] {
            "/data/user/10/org.refii.test/files/phone-builds/module.so",
            "/data/data/org.refii.other/files/phone-builds/module.so",
            "/data/data/org.refii.test/files-extra/phone-builds/module.so",
            "/ordinary/desktop/module.so" })
            AssertThat(AndroidInstallState.MatchPrivateAlias(root, outside)).IsEqual(outside);
        AssertThat(AndroidInstallState.MatchPrivateAlias("/ordinary/desktop", canonical)).IsEqual(canonical);
    }

    /// <summary>Creates a file-table stand-in to test path validation independently of container parsing.</summary>
    private static StfsPackage.Entry Entry(string name, int parent, bool directory = false) =>
        new() { Name = name, ParentIndex = parent, IsDirectory = directory };

    /// <summary>Tests the explicit refusal contract without native Godot error logging.</summary>
    private static bool Fails(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } }

    /// <summary>Writes a big-endian field in a synthetic format fixture.</summary>
    private static void Be(byte[] data, int offset, uint value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset), value);

    /// <summary>Creates synthetic executable/patch headers with public supported-pressing metadata, without game code.</summary>
    private static byte[] Xex(bool patch)
    {
        byte[] data = new byte[1024];
        Encoding.ASCII.GetBytes("XEX2").CopyTo(data, 0);
        Be(data, 0x10, 0x200); Be(data, 0x14, 2);
        Be(data, 0x18, patch ? 0x000005ffU : 0x00040006U); Be(data, 0x1c, 0x40);
        // The program's own marks: image digest and file key on a disc, first delta block hash on a patch.
        Be(data, 0x20, 0x000003ffU); Be(data, 0x24, 0x80);
        Convert.FromHexString("E619AEAD089E7FD77B87CEC64DE051BB93C06CB5").CopyTo(data, 0x90);
        Convert.FromHexString("B77FAF838ED5A19936758B8AC1C0C428D5A18194").CopyTo(data, 0x314);
        Convert.FromHexString("E2DFE99A95BDE0771D4275148BE8FBC8").CopyTo(data, 0x350);
        Be(data, 0x44, patch ? 0x11aU : 0x1aU); Be(data, 0x48, 0x1a);
        Be(data, 0x4c, 0x4d5307f1);
        Convert.FromHexString("A509A2D6D9E998327C398033716F0A0D").CopyTo(data, 0x340);
        return data;
    }

    /// <summary>Builds a tiny XDVDFS disc with one executable and one nested bank.</summary>
    private static byte[] Iso()
    {
        byte[] data = new byte[44 * 2048];
        byte[] signature = Encoding.ASCII.GetBytes("MICROSOFT*XBOX*MEDIA");
        signature.CopyTo(data, 0x10000); signature.CopyTo(data, 0x107ec);
        BitConverter.GetBytes(40U).CopyTo(data, 0x10014); BitConverter.GetBytes(2048U).CopyTo(data, 0x10018);
        DiscEntry(data, 40 * 2048, "default.xex", 41, 1024, false, 7);
        DiscEntry(data, 40 * 2048 + 28, "data", 42, 2048, true);
        DiscEntry(data, 42 * 2048, "bank.bnk", 43, 4, false);
        Xex(false).CopyTo(data, 41 * 2048);
        Encoding.ASCII.GetBytes("bank").CopyTo(data, 43 * 2048);
        return data;
    }

    /// <summary>Writes one little-endian XDVDFS directory entry.</summary>
    private static void DiscEntry(byte[] data, int at, string name, uint sector, uint size, bool directory, ushort right = 0)
    {
        BitConverter.GetBytes(right).CopyTo(data, at + 2);
        BitConverter.GetBytes(sector).CopyTo(data, at + 4); BitConverter.GetBytes(size).CopyTo(data, at + 8);
        data[at + 12] = directory ? (byte)0x10 : (byte)0;
        data[at + 13] = (byte)name.Length; Encoding.ASCII.GetBytes(name).CopyTo(data, at + 14);
    }

    /// <summary>
    /// A package lifts exactly the title's update data files: Fable 3's collection file, bank and
    /// bank data (never the build manifest), Fable 2's tu1_data.bnk, each under the update root's data\.
    /// </summary>
    [TestCase]
    public void TitleUpdateLiftsExactlyTheTitlesDataFiles()
    {
        string root = Path.Combine(Refii.Tests.TestTemp.Root, "refii-tudata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] data = new byte[0x13000];
            Encoding.ASCII.GetBytes("LIVE").CopyTo(data, 0);
            Be(data, 0x340, 0xb000); Be(data, 0x344, 0xb0000); Be(data, 0x360, 0x4d5308d6);
            data[0x37c] = 1;
            PackageEntry(data, 0xc000, "data", 0, 0, 0xffff, true);
            PackageEntry(data, 0xc040, "default.xexp", 1, 1024, 0xffff, false);
            PackageEntry(data, 0xc080, "package_collection_info.xmb", 2, 4, 0, false);
            PackageEntry(data, 0xc0c0, "tu1.bnk", 3, 4, 0, false);
            PackageEntry(data, 0xc100, "tu1.bnk.dat", 4, 4, 0, false);
            PackageEntry(data, 0xc140, "tu1.manifest.pkg", 5, 4, 0, false);
            PackageEntry(data, 0xc180, "tu1_data.bnk", 6, 4, 0, false);
            Xex(true).CopyTo(data, 0xd000);
            Encoding.ASCII.GetBytes("coll").CopyTo(data, 0xe000);
            Encoding.ASCII.GetBytes("bank").CopyTo(data, 0xf000);
            Encoding.ASCII.GetBytes("bdat").CopyTo(data, 0x10000);
            Encoding.ASCII.GetBytes("mani").CopyTo(data, 0x11000);
            Encoding.ASCII.GetBytes("f2tu").CopyTo(data, 0x12000);
            string package = Path.Combine(root, "TU_package");
            File.WriteAllBytes(package, data);

            AssertThat(TitleUpdate.TryLoad(package, Refii.Core.Engine_Title.Fable3, out var fable3, out string error)).IsTrue();
            AssertThat(error).IsEqual(string.Empty);
            AssertThat(string.Join(",", fable3.DataFiles.Select(f => f.Name + "=" + Encoding.ASCII.GetString(f.Bytes))))
                .IsEqual("package_collection_info.xmb=coll,tu1.bnk=bank,tu1.bnk.dat=bdat");

            AssertThat(TitleUpdate.TryLoad(package, Refii.Core.Engine_Title.Fable2, out var fable2, out _)).IsTrue();
            AssertThat(string.Join(",", fable2.DataFiles.Select(f => f.Name + "=" + Encoding.ASCII.GetString(f.Bytes))))
                .IsEqual("tu1_data.bnk=f2tu");

            string patch = Path.Combine(root, "assets", "Fable3", "game", "default.xexp");
            AssertThat(Installer.GetTitleUpdateDataPath(patch, "tu1.bnk"))
                .IsEqual(Path.Combine(root, "assets", "Fable3", "update", "data", "tu1.bnk"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>Builds a renamed LIVE TU fixture with a patch and a nested update bank.</summary>
    private static byte[] Package()
    {
        byte[] data = new byte[0xf000];
        Encoding.ASCII.GetBytes("LIVE").CopyTo(data, 0);
        Be(data, 0x340, 0xb000); Be(data, 0x344, 0xb0000); Be(data, 0x360, 0x4d5307f1);
        data[0x37c] = 1;
        PackageEntry(data, 0xc000, "data", 0, 0, 0xffff, true);
        PackageEntry(data, 0xc040, "default.xexp", 1, 1024, 0xffff, false);
        PackageEntry(data, 0xc080, "test.gdb.tu", 2, 5, 0, false);
        Xex(true).CopyTo(data, 0xd000); Encoding.ASCII.GetBytes("patch").CopyTo(data, 0xe000);
        return data;
    }

    /// <summary>Writes one synthetic STFS table entry with a contiguous block chain.</summary>
    private static void PackageEntry(byte[] data, int at, string name, byte block, uint size, ushort parent, bool directory)
    {
        Encoding.ASCII.GetBytes(name).CopyTo(data, at);
        data[at + 0x28] = (byte)(name.Length | (directory ? 0x80 : 0x40));
        data[at + 0x29] = directory ? (byte)0 : (byte)1; data[at + 0x2f] = block;
        data[at + 0x32] = (byte)(parent >> 8); data[at + 0x33] = (byte)parent;
        Be(data, at + 0x34, size);
    }

    /// <summary>Owns small synthetic originals and an empty user directory for the full installer lifecycle.</summary>
    private sealed class DiscFixture : IDisposable
    {
        /// <summary>Fresh fixture root, never a real user's game directory.</summary>
        public string Root { get; } = Path.Combine(Refii.Tests.TestTemp.Root, "refii-disc-fixture-" + Guid.NewGuid().ToString("N"));
        /// <summary>App-private user stand-in.</summary>
        public string User => Path.Combine(Root, "user");
        /// <summary>Untouched disc input stand-in.</summary>
        public string Image => Path.Combine(Root, "original.iso");
        /// <summary>Extensionless/renamed original title-update stand-in.</summary>
        public string Update => Path.Combine(Root, "TU-renamed");
        /// <summary>Creates ordinary original documents without game assets or GUI engine objects.</summary>
        public DiscFixture() { Directory.CreateDirectory(User); File.WriteAllBytes(Image, Iso()); File.WriteAllBytes(Update, Package()); }
        /// <summary>Runs the real extraction code with synthetic input-copy delegates.</summary>
        public AndroidGameInstaller.Prepared Prepare(bool withUpdate = true, Action<string>? progress = null,
            CancellationToken cancellation = default) => AndroidGameInstaller.Prepare(
                (target, _, _) => File.Copy(Image, target), withUpdate ? (target, _, _) => File.Copy(Update, target) : null,
                Path.Combine(User, "phone-installs"), progress ?? (_ => { }), cancellation);
        /// <summary>Checks byte-for-byte source preservation across success and failures.</summary>
        public void AssertOriginals()
        {
            // Compare every byte directly; the assertion framework's recursive
            // per-element formatting adds no coverage for large byte arrays.
            AssertThat(File.ReadAllBytes(Image).AsSpan().SequenceEqual(Iso())).IsTrue();
            AssertThat(File.ReadAllBytes(Update).AsSpan().SequenceEqual(Package())).IsTrue();
        }
        /// <summary>Removes only this test's newly created temporary tree.</summary>
        public void Dispose() => Directory.Delete(Root, true);
    }
}
