using System;
using System.IO;
using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Bnk;
using Refii.Launcher.Core;
using static GdUnit4.Assertions;
using Refii.Titles.Fable2;

namespace Refii.Tests;

/// <summary>Verifies Android update migration and upstream bank precedence while retaining the cache budget contract.</summary>
[TestSuite]
public sealed class AndroidUpdateBankTests
{
    /// <summary>Update data wins over the disc, mods win over the update, and remounting preserves existing bytes.</summary>
    [TestCase]
    public void UpdateMountLifecycle()
    {
        string root = Path.Combine(Refii.Tests.TestTemp.Root, "refii-update-" + Guid.NewGuid().ToString("N"));
        string game = Path.Combine(root, "game"), mod = Path.Combine(root, "mod");
        Directory.CreateDirectory(Path.Combine(game, "data"));
        // An empty pub-games folder keeps the Flash index on this fixture; without one, Mount falls back to
        // an installed game on the machine and decodes its art through Godot, which the test host cannot.
        Directory.CreateDirectory(Path.Combine(game, "data", Refii.Titles.Fable2.PubGames.PubGamesCatalog.FolderName));
        Directory.CreateDirectory(mod);
        try
        {
            byte[] Bank(byte value) => BnkWriter.Write(new[] { new BnkWriterEntry { Path = "world/value.bin", Bytes = new byte[] { value } } });
            File.WriteAllBytes(Path.Combine(game, "base.bnk"), Bank(1));
            string extracted = Path.Combine(game, TitleUpdate.DataRelativePath(Refii.Core.Engine_Title.Fable2.TitleUpdateDataFiles[0]));
            File.WriteAllBytes(extracted, Bank(2));
            File.WriteAllBytes(Path.Combine(mod, "mod.bnk"), Bank(3));
            AndroidLauncherSettings.PrepareUpdateBank(game);
            string update = Refii.Core.Engine_GameData.UpdateRootFor(game);
            string copied = Path.Combine(update, TitleUpdate.DataRelativePath(Refii.Core.Engine_Title.Fable2.TitleUpdateDataFiles[0]));
            byte[] preserved = File.ReadAllBytes(copied);
            // Remove only the fixture's source copy to prove that the update volume
            // is supplying the override independently of loose disc files.
            File.Delete(extracted);
            using (var mounted = BnkFileSystem.Mount(game, updateRoot: update))
                AssertThat(Read(mounted)).IsEqual("02");
            using (var mounted = BnkFileSystem.Mount(game, overlays: new[] { ("example", mod) }, updateRoot: update))
                AssertThat(Read(mounted)).IsEqual("03");
            File.WriteAllBytes(extracted, Bank(9));
            AndroidLauncherSettings.PrepareUpdateBank(game);
            AssertThat(File.ReadAllBytes(copied).AsSpan().SequenceEqual(preserved)).IsTrue();
            using (var mounted = BnkFileSystem.Mount(game, updateRoot: update))
                AssertThat(Read(mounted)).IsEqual("02");
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>Reads the mounted member through the real bank lookup and archive owner.</summary>
    /// <param name="filesystem">Current mounted bank collection.</param>
    /// <returns>The synthetic value or a visible missing-file failure.</returns>
    private static string Read(BnkFileSystem filesystem) => filesystem.TryGetFile("world/value.bin", out var file)
        ? Convert.ToHexString(file.ReadAllBytes()) : "missing";
}
