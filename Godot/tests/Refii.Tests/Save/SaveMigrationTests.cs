namespace Refii.Tests.Save;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Save;

/// <summary>
/// The one-time migration into the one save folder: every source layout lands in the profile's
/// title folders, the install's own folders move and outside ones are copied untouched, nothing is
/// deleted, identical saves merge, clashing names take the next free name with the header rewritten,
/// saves past the title's slot limit go to Older saves, one identity survives with the friends merged,
/// and a second run changes nothing. Every tree is synthetic, under the test temp root.
/// </summary>
[TestSuite]
public class SaveMigrationTests
{
	private const uint Fable2 = 0x4D5307F1;
	private const uint Fable3 = 0x4D5308D6;
	private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Local);

	private static string NewRoot()
	{
		string root = Path.Combine(Refii.Tests.TestTemp.Root, "refii-saves-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		return root;
	}

	private static void Put(string path, string text)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, text);
	}

	/// <summary>A 0x148-byte XCONTENT_AGGREGATE_DATA header naming <paramref name="name"/>, as the guest writes one.</summary>
	private static byte[] Header(string name)
	{
		byte[] header = new byte[0x148];
		Encoding.ASCII.GetBytes(name).CopyTo(header, 0x108);
		header[0x140] = 0x4D;
		return header;
	}

	private static string HeaderName(string path) =>
		Encoding.ASCII.GetString(File.ReadAllBytes(path), 0x108, 42).TrimEnd('\0');

	/// <summary>A save package with a main save written at <paramref name="when"/> and its header.</summary>
	private static void Save(string titleDir, string name, string text, DateTime when)
	{
		string package = Path.Combine(titleDir, "00000001", name);
		Put(Path.Combine(package, "mainsave.bin"), "main " + text);
		Put(Path.Combine(package, "herosave.bin"), "hero " + text);
		File.SetLastWriteTimeUtc(Path.Combine(package, "mainsave.bin"), when);
		string header = Path.Combine(titleDir, "Headers", "00000001", name + ".header");
		Directory.CreateDirectory(Path.GetDirectoryName(header)!);
		File.WriteAllBytes(header, Header(name));
	}

	/// <summary>Every file under a tree with its content hash: what "unchanged" means.</summary>
	private static SortedDictionary<string, string> Snapshot(string root)
	{
		var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
		if (!Directory.Exists(root)) return files;
		foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
		{
			files[Path.GetRelativePath(root, file)] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
		}

		return files;
	}

	private static Engine_SaveMigration.Source Own(string path, string label, uint title) => new(path, label, Owned: true, title);

	private static Engine_SaveMigration.Source Outside(string path, string label, uint title, params uint[] titles) =>
		new(path, label, Owned: false, title, Titles: titles.Length == 0 ? null : titles);

	[TestCase]
	public void EverySourceKindLandsInTheProfileLayout()
	{
		string root = NewRoot();
		try
		{
			string assets = Path.Combine(root, "install", "assets");
			string home = Path.Combine(root, "home");
			DateTime t0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

			// The current desktop roots: Fable 2's (the kept identity) and Fable 3's (another key).
			string f2 = Path.Combine(assets, "Fable2", "save");
			Save(Path.Combine(f2, "E00AFA3420120108", "4D5307F1"), "Hero000", "f2-a", t0.AddDays(10));
			Save(Path.Combine(f2, "E00AFA3420120108", "4D5307F1"), "Hero001", "f2-b", t0.AddDays(11));
			Put(Path.Combine(f2, "4D5307F1", "profile", "Brave Fox", "63E83FFF"), "f2 settings");
			Put(Path.Combine(f2, "rexnet", "identity.key"), "key f2");
			Put(Path.Combine(f2, "rexnet", "friends.txt"), "# friends\npeerA\npeerB\n");
			Put(Path.Combine(f2, "achievements.refii"), "f2 achievements");
			Put(Path.Combine(f2, "E00AFA3420120108", "refii-snapshots", "Hero001", "quicksave.quick", "mainsave.bin"), "snap");
			// PR 29's set-aside profile beside it: an older generation of the same saves.
			Save(Path.Combine(f2, ".E0AAAAAA20120108.replaced-000001700000", "4D5307F1"), "Hero000", "f2-old", t0.AddDays(1));

			string f3 = Path.Combine(assets, "Fable3", "save");
			Save(Path.Combine(f3, "E0AB489F20120108", "4D5308D6"), "Hero000", "f3-a", t0.AddDays(12));
			Put(Path.Combine(f3, "4D5308D6", "profile", "Whimsical Heron", "63E83FFF"), "f3 settings");
			Put(Path.Combine(f3, "rexnet", "identity.key"), "key f3");
			Put(Path.Combine(f3, "rexnet", "friends.txt"), "peerB\npeerC\n");
			Put(Path.Combine(f3, "rexnet", "display_name.txt"), "Loremendris\n");
			Put(Path.Combine(f3, "achievements.refii"), "f3 achievements");

			// The pre-split root, holding a byte-identical copy of Fable 2's Hero000.
			Save(Path.Combine(assets, "save", "E00AFA3420120108", "4D5307F1"), "Hero000", "f2-a", t0.AddDays(10));
			// An export beside the Fable 2 root.
			Put(Path.Combine(assets, "Fable2", "save-export", "4D5307F1", "00000001", "Hero000", "mainsave.bin"), "export");

			// Outside the install: the oldest bare layout, and a Xenia profile with its dashboard.
			string local = Path.Combine(home, ".local", "share", "refii");
			Save(Path.Combine(local, "4D5307F1"), "Hero000", "local", t0.AddDays(2));
			Put(Path.Combine(local, "4D5307F1", "profile", "User", "63E83FFF"), "local settings");
			Put(Path.Combine(local, "rexnet", "identity.key"), "key local");
			Put(Path.Combine(local, "rexnet", "friends.txt"), "peerX\n");
			string xenia = Path.Combine(home, "Games", "xenia-emu", "drive_c", "Xenia_Canary", "content");
			Save(Path.Combine(xenia, "E03000007424AB0D", "4D5307F1"), "Hero000", "xenia", t0.AddDays(3));
			Put(Path.Combine(xenia, "E03000007424AB0D", "FFFE07D1", "00010000", "E03000007424AB0D", "Account"), "dashboard");

			var outside = new[] { Snapshot(local), Snapshot(xenia) };
			string saves = Path.Combine(assets, "saves");
			List<Engine_SaveMigration.Source> sources = Engine_SaveMigration.DefaultSources(assets, home);
			Engine_SaveMigration.Report report = Engine_SaveMigration.Run(saves, sources, Now);

			AssertThat(report.Ran).IsTrue();
			AssertThat(report.Failures).IsEqual(0);
			string profile = Path.Combine(saves, "Brave Fox");
			AssertThat(report.ProfileDirectory).IsEqual(profile);
			AssertThat(Engine_SaveProfiles.ActiveDirectory(saves)).IsEqual(profile);
			AssertThat(Engine_SaveProfiles.ReadId(profile)).IsNotNull();

			// One identity: Fable 2's key, Fable 3's retired beside it, friends merged once each.
			AssertThat(File.ReadAllText(Path.Combine(profile, "rexnet", "identity.key"))).IsEqual("key f2");
			AssertThat(File.ReadAllText(Path.Combine(profile, "rexnet", "retired", "Fable3 save", "identity.key"))).IsEqual("key f3");
			string[] friends = File.ReadAllLines(Path.Combine(profile, "rexnet", "friends.txt")).Where(l => l.Length != 0 && !l.StartsWith('#')).ToArray();
			AssertThat(friends).IsEqual(new[] { "peerA", "peerB", "peerC" });
			// An identity from outside the install is kept whole, its friends not merged.
			AssertThat(File.ReadAllText(Path.Combine(profile, "rexnet", "retired", "local share refii", "identity.key"))).IsEqual("key local");
			AssertThat(File.ReadAllText(Path.Combine(profile, "rexnet", "retired", "local share refii", "friends.txt"))).IsEqual("peerX\n");
			AssertThat(File.Exists(Path.Combine(profile, "rexnet", "display_name.txt"))).IsFalse();
			AssertThat(File.ReadAllText(Path.Combine(profile, "rexnet", "retired", "Fable3 save", "display_name.txt"))).IsEqual("Loremendris\n");

			// Fable 2: five distinct saves (the pre-split Hero000 merged) within six slots.
			string game2 = Path.Combine(profile, "Fable 2");
			string[] slots2 = Directory.GetDirectories(Path.Combine(game2, "00000001")).Select(Path.GetFileName).OfType<string>().OrderBy(n => n).ToArray();
			AssertThat(slots2).IsEqual(new[] { "Hero000", "Hero001", "Hero002", "Hero003", "Hero004" });
			AssertThat(report.Merged).IsEqual(1);
			AssertThat(report.Saves[Fable2].Slotted).IsEqual(5);
			AssertThat(report.Saves[Fable2].Older).IsEqual(0);
			AssertThat(File.ReadAllText(Path.Combine(game2, "00000001", "Hero000", "mainsave.bin"))).IsEqual("main f2-a");
			AssertThat(File.ReadAllText(Path.Combine(game2, "00000001", "Hero001", "mainsave.bin"))).IsEqual("main f2-b");
			foreach (string slot in slots2)
			{
				AssertThat(HeaderName(Path.Combine(game2, "Headers", "00000001", slot + ".header"))).IsEqual(slot);
			}

			AssertThat(File.ReadAllText(Path.Combine(game2, "profile", "63E83FFF"))).IsEqual("f2 settings");
			AssertThat(File.ReadAllText(Path.Combine(game2, "Older saves", "local share refii", "profile", "User", "63E83FFF"))).IsEqual("local settings");
			AssertThat(File.ReadAllText(Path.Combine(game2, "refii-snapshots", "Hero001", "quicksave.quick", "mainsave.bin"))).IsEqual("snap");
			AssertThat(File.ReadAllText(Path.Combine(game2, "achievements.refii"))).IsEqual("f2 achievements");
			AssertThat(File.ReadAllText(Path.Combine(game2, "save-export", "4D5307F1", "00000001", "Hero000", "mainsave.bin"))).IsEqual("export");
			AssertThat(Directory.Exists(Path.Combine(profile, "FFFE07D1"))).IsFalse();

			// Fable 3 in its own folder of the same profile.
			string game3 = Path.Combine(profile, "Fable 3");
			AssertThat(File.ReadAllText(Path.Combine(game3, "00000001", "Hero000", "mainsave.bin"))).IsEqual("main f3-a");
			AssertThat(File.ReadAllText(Path.Combine(game3, "profile", "63E83FFF"))).IsEqual("f3 settings");
			AssertThat(File.ReadAllText(Path.Combine(game3, "achievements.refii"))).IsEqual("f3 achievements");

			// The install's own roots moved and are marked; the merged duplicate is left, never deleted.
			AssertThat(Directory.Exists(Path.Combine(f2, "E00AFA3420120108", "4D5307F1", "00000001", "Hero000"))).IsFalse();
			AssertThat(File.Exists(Path.Combine(f2, Engine_SaveMigration.MarkerFileName))).IsTrue();
			AssertThat(File.Exists(Path.Combine(f3, Engine_SaveMigration.MarkerFileName))).IsTrue();
			AssertThat(File.ReadAllText(Path.Combine(assets, "save", "E00AFA3420120108", "4D5307F1", "00000001", "Hero000", "mainsave.bin"))).IsEqual("main f2-a");
			AssertThat(File.Exists(Path.Combine(assets, "save", Engine_SaveMigration.MarkerFileName))).IsTrue();
			// Outside the install: copied, never written to.
			AssertThat(Snapshot(local)).IsEqual(outside[0]);
			AssertThat(Snapshot(xenia)).IsEqual(outside[1]);
			AssertThat(File.Exists(Path.Combine(local, Engine_SaveMigration.MarkerFileName))).IsFalse();
			AssertThat(File.ReadAllText(Path.Combine(saves, "migration.log"))).Contains("copied " + Path.Combine(xenia, "E03000007424AB0D", "4D5307F1", "00000001", "Hero000"));
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	[TestCase]
	public void ASecondRunChangesNothingAndNeverCopiesADeletedSaveBack()
	{
		string root = NewRoot();
		try
		{
			string outside = Path.Combine(root, "outside");
			Save(Path.Combine(outside, "E0EE843420120108", "4D5307F1"), "Hero000", "outside", DateTime.UtcNow);
			string own = Path.Combine(root, "assets", "Fable2", "save");
			Save(Path.Combine(own, "E00AFA3420120108", "4D5307F1"), "Hero003", "own", DateTime.UtcNow);
			string saves = Path.Combine(root, "assets", "saves");
			var sources = new[] { Own(own, "Fable2 save", Fable2), Outside(outside, "outside", Fable2) };

			AssertThat(Engine_SaveMigration.Run(saves, sources, Now).Ran).IsTrue();
			SortedDictionary<string, string> after = Snapshot(root);
			Engine_SaveMigration.Report second = Engine_SaveMigration.Run(saves, sources, Now);
			AssertThat(second.Ran).IsFalse();
			AssertThat(Snapshot(root)).IsEqual(after);

			// The player deletes the copied save in game: the next launch does not bring it back.
			string profile = Engine_SaveProfiles.ActiveDirectory(saves);
			Directory.Delete(Path.Combine(profile, "Fable 2", "00000001", "Hero000"), true);
			AssertThat(Engine_SaveMigration.Run(saves, sources, Now).Ran).IsFalse();
			AssertThat(Directory.Exists(Path.Combine(profile, "Fable 2", "00000001", "Hero000"))).IsFalse();
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	[TestCase]
	public void AClashingNameTakesTheNextFreeNameAndItsHeaderFollows()
	{
		string root = NewRoot();
		try
		{
			string a = Path.Combine(root, "a"), b = Path.Combine(root, "b");
			Save(Path.Combine(a, "E00AFA3420120108", "4D5307F1"), "Hero000", "first", DateTime.UtcNow.AddHours(-1));
			Save(Path.Combine(a, "E00AFA3420120108", "4D5307F1"), "Hero001", "second", DateTime.UtcNow.AddHours(-2));
			Save(Path.Combine(b, "E0EE843420120108", "4D5307F1"), "Hero000", "other", DateTime.UtcNow.AddHours(-3));
			string saves = Path.Combine(root, "saves");
			Engine_SaveMigration.Report report = Engine_SaveMigration.Run(saves, new[] { Own(a, "a", Fable2), Own(b, "b", Fable2) }, Now);

			string game = Path.Combine(Engine_SaveProfiles.ActiveDirectory(saves), "Fable 2");
			AssertThat(report.Renamed).IsEqual(1);
			AssertThat(File.ReadAllText(Path.Combine(game, "00000001", "Hero002", "mainsave.bin"))).IsEqual("main other");
			AssertThat(HeaderName(Path.Combine(game, "Headers", "00000001", "Hero002.header"))).IsEqual("Hero002");
			AssertThat(File.ReadAllBytes(Path.Combine(game, "Headers", "00000001", "Hero002.header"))[0x140]).IsEqual((byte)0x4D);
			AssertThat(File.ReadAllText(Path.Combine(game, "00000001", "Hero000", "mainsave.bin"))).IsEqual("main first");
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	[TestCase]
	public void SavesPastTheSlotLimitGoToOlderSavesAndSwapBackIn()
	{
		string root = NewRoot();
		try
		{
			string own = Path.Combine(root, "own");
			DateTime t0 = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
			for (int i = 0; i < 12; i++)
			{
				Save(Path.Combine(own, "E00AFA3420120108", "4D5307F1"), $"Hero{i:000}", "save " + i, t0.AddDays(i));
			}

			for (int i = 0; i < 7; i++)
			{
				Save(Path.Combine(own, "E0AB489F20120108", "4D5308D6"), "Hero00" + i, "f3 " + i, t0.AddDays(i));
			}

			string saves = Path.Combine(root, "saves");
			Engine_SaveMigration.Report report = Engine_SaveMigration.Run(saves, new[] { Own(own, "own", Fable2) }, Now);
			AssertThat(report.Saves[Fable2].Slotted).IsEqual(10);
			AssertThat(report.Saves[Fable2].Older).IsEqual(2);
			AssertThat(report.Saves[Fable3].Slotted).IsEqual(5);
			AssertThat(report.Saves[Fable3].Older).IsEqual(2);

			string profile = Engine_SaveProfiles.ActiveDirectory(saves);
			string slots = Path.Combine(profile, "Fable 2", "00000001");
			// The newest ten fill the slots: Hero002..Hero011; the two oldest wait in Older saves.
			AssertThat(Directory.GetDirectories(slots).Select(Path.GetFileName).OrderBy(n => n).ToArray())
				.IsEqual(Enumerable.Range(2, 10).Select(i => $"Hero{i:000}").ToArray());
			List<Engine_SaveMigration.OlderSave> older = Engine_SaveMigration.ListOlderSaves(profile, Fable2);
			AssertThat(older.Select(save => save.Name).ToArray()).IsEqual(new[] { "Hero001", "Hero000" });
			AssertThat(older[0].Label).IsEqual("own E00AFA3420120108");
			AssertThat(older[0].Header).IsNotNull();

			// Every slot used: a swap needs a save to go out, which goes to Older saves itself.
			AssertThat(Engine_SaveMigration.SwapIn(profile, Fable2, older[0], null, out string error)).IsNull();
			AssertThat(error).Contains("every slot is used");
			AssertThat(Engine_SaveMigration.SwapIn(profile, Fable2, older[0], "Hero002", out error)).IsEqual("Hero001");
			AssertThat(File.ReadAllText(Path.Combine(slots, "Hero001", "mainsave.bin"))).IsEqual("main save 1");
			AssertThat(File.Exists(Path.Combine(profile, "Fable 2", "Headers", "00000001", "Hero001.header"))).IsTrue();
			AssertThat(Directory.Exists(Path.Combine(slots, "Hero002"))).IsFalse();
			AssertThat(Engine_SaveMigration.ListOlderSaves(profile, Fable2).Any(save => save.Label == "Swapped out" && save.Name == "Hero002")).IsTrue();
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	[TestCase]
	public void TheProfileIsNamedAsTheKeptIdentitySignedInAndTakesTheStatedNameOnce()
	{
		string root = NewRoot();
		try
		{
			string own = Path.Combine(root, "own");
			Put(Path.Combine(own, "rexnet", "identity.key"), "key");
			Put(Path.Combine(own, "4D5307F1", "profile", "User", "63E83FFF"), "signed out");
			Put(Path.Combine(own, "4D5307F1", "profile", "Patient Hedgehog", "63E83FFF"), "signed in");
			string saves = Path.Combine(root, "saves");
			Engine_SaveMigration.Run(saves, new[] { Own(own, "own", Fable2) }, Now);
			string profile = Path.Combine(saves, "Patient Hedgehog");
			AssertThat(Engine_SaveProfiles.ActiveDirectory(saves)).IsEqual(profile);
			AssertThat(File.ReadAllText(Path.Combine(profile, "Fable 2", "profile", "63E83FFF"))).IsEqual("signed in");

			// The guest states the name it signs in with; a provisional profile takes it once.
			string id = Engine_SaveProfiles.ReadId(profile)!;
			Put(Path.Combine(profile, "rexnet", "profile_name.txt"), "Curious Cat\n");
			string renamed = Engine_SaveProfiles.Ensure(saves);
			AssertThat(renamed).IsEqual(Path.Combine(saves, "Curious Cat"));
			AssertThat(Engine_SaveProfiles.ReadId(renamed)).IsEqual(id);
			AssertThat(Engine_SaveProfiles.IsProvisional(renamed)).IsFalse();
			Put(Path.Combine(renamed, "rexnet", "profile_name.txt"), "Lucky Dog\n");
			AssertThat(Engine_SaveProfiles.Ensure(saves)).IsEqual(renamed);

			// Renamed by hand, it is found again by its id.
			Directory.Move(renamed, Path.Combine(saves, "Mine"));
			AssertThat(Engine_SaveProfiles.Ensure(saves)).IsEqual(Path.Combine(saves, "Mine"));
			AssertThat(File.ReadAllText(Path.Combine(saves, "profiles.toml"))).Contains("folder = \"Mine\"");
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	[TestCase]
	public void AFreshInstallGetsOneProvisionalProfile()
	{
		string root = NewRoot();
		try
		{
			string saves = Path.Combine(root, "saves");
			AssertThat(Engine_SaveProfiles.ActiveDirectory(saves)).IsEqual(Path.Combine(saves, "Player"));
			AssertThat(Directory.Exists(saves)).IsFalse();
			AssertThat(Engine_SaveMigration.Run(saves, Engine_SaveMigration.DefaultSources(Path.Combine(root, "assets"), null), Now).Ran).IsFalse();
			string profile = Engine_SaveProfiles.Ensure(saves);
			AssertThat(profile).IsEqual(Path.Combine(saves, "Player"));
			AssertThat(Engine_SaveProfiles.IsProvisional(profile)).IsTrue();
			AssertThat(Engine_SaveProfiles.Ensure(saves)).IsEqual(profile);
			AssertThat(Engine_SaveProfiles.Load(saves).Profiles.Count).IsEqual(1);
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	/// <summary>
	/// The game's replace-a-save and the launcher's Swap share one move: the save folder and its header
	/// go whole to Older saves under the label, nothing is deleted, a name already there takes the next
	/// free number with the header's file name rewritten, and an unknown save moves nothing.
	/// </summary>
	[TestCase]
	public void SetAsideMovesTheSaveAndItsHeaderToOlderSavesWithoutDeleting()
	{
		string root = NewRoot();
		try
		{
			string profile = Path.Combine(root, "profile");
			string game = Path.Combine(profile, "Fable 2");
			DateTime t0 = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
			Save(game, "Hero003", "three", t0);
			Save(game, "Hero004", "four", t0.AddDays(1));
			SortedDictionary<string, string> before = Snapshot(Path.Combine(game, "00000001", "Hero003"));

			string? aside = Engine_SaveMigration.SetAside(profile, Fable2, "Hero003", Engine_SaveMigration.OverwrittenLabel, out string error);
			AssertThat(aside).IsEqual("Hero003");
			AssertThat(error).IsEqual(string.Empty);
			string older = Path.Combine(game, "Older saves", "Overwritten");
			AssertThat(Directory.Exists(Path.Combine(game, "00000001", "Hero003"))).IsFalse();
			AssertThat(File.Exists(Path.Combine(game, "Headers", "00000001", "Hero003.header"))).IsFalse();
			AssertThat(Snapshot(Path.Combine(older, "00000001", "Hero003"))).IsEqual(before);
			AssertThat(HeaderName(Path.Combine(older, "Headers", "00000001", "Hero003.header"))).IsEqual("Hero003");
			AssertThat(Directory.Exists(Path.Combine(game, "00000001", "Hero004"))).IsTrue();

			// A second Hero003 set aside later keeps the first and takes the next free number.
			Save(game, "Hero003", "three again", t0.AddDays(2));
			aside = Engine_SaveMigration.SetAside(profile, Fable2, "Hero003", Engine_SaveMigration.OverwrittenLabel, out error);
			AssertThat(aside).IsEqual("Hero004");
			AssertThat(File.ReadAllText(Path.Combine(older, "00000001", "Hero003", "mainsave.bin"))).IsEqual("main three");
			AssertThat(File.ReadAllText(Path.Combine(older, "00000001", "Hero004", "mainsave.bin"))).IsEqual("main three again");
			AssertThat(HeaderName(Path.Combine(older, "Headers", "00000001", "Hero004.header"))).IsEqual("Hero004");

			// Listed for the launcher's Swap under its label.
			List<Engine_SaveMigration.OlderSave> listed = Engine_SaveMigration.ListOlderSaves(profile, Fable2);
			AssertThat(listed.Count(save => save.Label == Engine_SaveMigration.OverwrittenLabel)).IsEqual(2);

			// No such save: nothing moves.
			SortedDictionary<string, string> all = Snapshot(game);
			AssertThat(Engine_SaveMigration.SetAside(profile, Fable2, "Hero009", Engine_SaveMigration.OverwrittenLabel, out error)).IsNull();
			AssertThat(error).Contains("Hero009");
			AssertThat(Engine_SaveMigration.SetAside(profile, Fable2, "..", Engine_SaveMigration.OverwrittenLabel, out error)).IsNull();
			AssertThat(Snapshot(game)).IsEqual(all);
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}
}
