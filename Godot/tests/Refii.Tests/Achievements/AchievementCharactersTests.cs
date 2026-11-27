namespace Refii.Tests.Achievements;

using System;
using System.IO;
using System.Text;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Achievements;
using Refii.Save;

/// <summary>
/// Achievements per character on the one shared profile: a binding to a save resolves to the character
/// its sidecar names (one is minted and written for a save that names none), a write lands on the
/// character of the binding it was made under and on the profile total, a hero no save holds takes the
/// save it is first written into, every save is stated with its character's set, both players can
/// play heroes from the same save set, and the stores round-trip on disk. Every tree is synthetic,
/// under the test temp root.
/// </summary>
[TestSuite]
public class AchievementCharactersTests
{
	private const uint Fable2 = 0x4D5307F1;

	private static string NewRoot()
	{
		string root = Path.Combine(Refii.Tests.TestTemp.Root, "refii-ach-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		return root;
	}

	private static string Save(string profile, string name)
	{
		string package = Path.Combine(Engine_SaveLayout.SaveGames(profile, Fable2), name);
		Directory.CreateDirectory(package);
		File.WriteAllText(Path.Combine(package, "mainsave.bin"), "main " + name);
		string header = Path.Combine(Engine_SaveLayout.Headers(profile, Fable2), name + ".header");
		Directory.CreateDirectory(Path.GetDirectoryName(header)!);
		byte[] bytes = new byte[0x148];
		Encoding.ASCII.GetBytes(name).CopyTo(bytes, 0x108);
		File.WriteAllBytes(header, bytes);
		return package;
	}

	private static Engine_AchievementCharacters Make(string profile)
	{
		Directory.CreateDirectory(profile);
		int next = 0;
		string game = Engine_SaveLayout.GameDirectory(profile, Fable2);
		return new Engine_AchievementCharacters(game, Engine_SaveLayout.SaveGames(profile, Fable2),
			Path.Combine(game, "achievements.refii"), () => (++next).ToString("x32"));
	}

	[TestCase]
	public void ASaveWithoutACharacterGetsOneAndKeepsIt()
	{
		string profile = Path.Combine(NewRoot(), "Player");
		Engine_AchievementCharacters characters = Make(profile);
		string save = Save(profile, "Hero003");

		Engine_AchievementCharacters.Bound first = characters.Bind(1, 0, "Hero003");
		AssertThat(Engine_AchievementCharacters.IsCharacterId(first.Character)).IsTrue();
		AssertThat(SaveSidecar.Read(save).Refii!.Character!.Id).IsEqual(first.Character);

		Engine_AchievementCharacters.Bound again = characters.Bind(2, 0, "Hero003");
		AssertThat(again.Character).IsEqual(first.Character);
		AssertThat(characters.CurrentOf(0)!.Serial).IsEqual(2ul);
	}

	[TestCase]
	public void EachHeroEarnsItsOwnAndTheProfileCountsOnce()
	{
		string profile = Path.Combine(NewRoot(), "Player");
		Engine_AchievementCharacters characters = Make(profile);
		Save(profile, "Hero001");
		Save(profile, "Hero002");
		characters.Bind(1, 0, "Hero001");
		AssertThat(characters.Record(0, 23, 1, 100).ToString()).IsEqual("(True, True)");
		AssertThat(characters.Record(0, 23, 1, 101).ToString()).IsEqual("(False, False)");

		characters.Bind(2, 0, "Hero002");
		AssertThat(characters.Record(0, 23, 2, 102).ToString()).IsEqual("(True, False)");
		AssertThat(characters.StoreOfUser(0)!.IsUnlocked(23)).IsTrue();
		AssertThat(characters.Ledger.Count).IsEqual(1);
	}

	[TestCase]
	public void BothPlayersPickFromTheSameSavesAndEarnApart()
	{
		string profile = Path.Combine(NewRoot(), "Player");
		Engine_AchievementCharacters characters = Make(profile);
		Save(profile, "Hero001");
		Save(profile, "Hero002");
		Engine_AchievementCharacters.Bound one = characters.Bind(1, 0, "Hero001");
		Engine_AchievementCharacters.Bound two = characters.Bind(2, 1, "Hero002");
		AssertThat(two.Character).IsNotEqual(one.Character);

		AssertThat(characters.Record(1, 40, 2, 100).ToString()).IsEqual("(True, True)");
		AssertThat(characters.StoreOfUser(1)!.IsUnlocked(40)).IsTrue();
		AssertThat(characters.StoreOfUser(0)!.IsUnlocked(40)).IsFalse();

		// The same id earned later by player one's hero is new to that hero, not to the profile.
		AssertThat(characters.Record(0, 40, 1, 101).ToString()).IsEqual("(True, False)");
	}

	[TestCase]
	public void ANewHeroTakesTheSaveItIsWrittenInto()
	{
		string profile = Path.Combine(NewRoot(), "Player");
		Engine_AchievementCharacters characters = Make(profile);
		Engine_AchievementCharacters.Bound fresh = characters.Bind(1, 1, string.Empty);
		characters.Record(1, 50, 1, 100);

		string save = Save(profile, "Hero004");
		SaveSidecar sidecar = SaveSidecar.Read(save);
		characters.Stamp(sidecar, 1, "Hero004");
		sidecar.Write(save);
		AssertThat(sidecar.Refii!.Character!.Id).IsEqual(fresh.Character);
		AssertThat(string.Join(",", sidecar.Refii.Character.Achievements)).IsEqual("50");

		Engine_AchievementCharacters.Bound loaded = characters.Bind(2, 0, "Hero004");
		AssertThat(loaded.Character).IsEqual(fresh.Character);
	}

	[TestCase]
	public void AWriteFromANeverBoundUserIsItsOwnHero()
	{
		Engine_AchievementCharacters characters = Make(Path.Combine(NewRoot(), "Player"));
		AssertThat(characters.Record(0, 50, 0, 100).ToString()).IsEqual("(True, True)");
		AssertThat(characters.Resolve(0, 0).Character).IsEqual(characters.Resolve(0, 0).Character);
		AssertThat(characters.Resolve(0, 1).Character).IsNotEqual(characters.Resolve(0, 0).Character);
	}

	[TestCase]
	public void EverySaveIsStatedWithItsCharactersSet()
	{
		string profile = Path.Combine(NewRoot(), "Player");
		Engine_AchievementCharacters characters = Make(profile);
		Save(profile, "Hero001");
		Save(profile, "Hero002");
		Save(profile, "Hero003");
		characters.Bind(1, 0, "Hero001");
		characters.Record(0, 11, 1, 1_700_000_000);
		characters.Bind(2, 1, "Hero002");
		characters.Record(1, 12, 2, 0);

		string[] lines = characters.SaveTable().Split('\n', StringSplitOptions.RemoveEmptyEntries);
		AssertThat(lines.Length).IsEqual(2);
		AssertThat(lines[0]).StartsWith("Hero001\t11:");
		AssertThat(lines[1]).IsEqual("Hero002\t12:0");
		AssertThat(Engine_AchievementCharacters.FileTime(1_700_000_000).ToString())
			.IsEqual(lines[0]["Hero001\t11:".Length..]);
	}

	[TestCase]
	public void StoresRoundTripOnDisk()
	{
		string profile = Path.Combine(NewRoot(), "Player");
		Engine_AchievementCharacters characters = Make(profile);
		Save(profile, "Hero000");
		Engine_AchievementCharacters.Bound bound = characters.Bind(1, 1, "Hero000");
		characters.Record(1, 10, 1, 5);
		AssertThat(characters.Save()).IsEmpty();

		AssertThat(File.Exists(characters.CharacterPath(bound.Character))).IsTrue();
		string game = Engine_SaveLayout.GameDirectory(profile, Fable2);
		AssertThat(File.Exists(Path.Combine(game, "achievements.refii"))).IsTrue();

		var reread = new Engine_AchievementCharacters(game, Engine_SaveLayout.SaveGames(profile, Fable2),
			Path.Combine(game, "achievements.refii"));
		AssertThat(reread.Character(bound.Character).IsUnlocked(10)).IsTrue();
		AssertThat(reread.Ledger.IsUnlocked(10)).IsTrue();
	}

	[TestCase]
	public void TheProfileTotalKeepsWhatWasEarnedBeforeCharacters()
	{
		string profile = Path.Combine(NewRoot(), "Player");
		string game = Engine_SaveLayout.GameDirectory(profile, Fable2);
		Directory.CreateDirectory(game);
		var old = new Engine_AchievementStore();
		old.Add(0, 23, 1_600_000_000);
		old.Save(Path.Combine(game, "achievements.refii"));

		Engine_AchievementCharacters characters = Make(profile);
		Save(profile, "Hero001");
		characters.Bind(1, 0, "Hero001");
		AssertThat(characters.Ledger.IsUnlocked(23)).IsTrue();
		AssertThat(characters.StoreOfUser(0)!.IsUnlocked(23)).IsFalse();
		AssertThat(characters.Record(0, 23, 1, 100).ToString()).IsEqual("(True, False)");
	}

	[TestCase]
	public void AMovedSaveBringsItsUnlocks()
	{
		string profile = Path.Combine(NewRoot(), "Player");
		Engine_AchievementCharacters characters = Make(profile);
		string save = Save(profile, "Hero007");
		var sidecar = new SaveSidecar
		{
			Refii = new SaveSidecar.RefiiLayer
			{
				Character = new SaveSidecar.CharacterLayer { Id = new string('a', 32), Name = "Hero007", Achievements = { 3, 9 } },
			},
		};
		sidecar.Write(save);

		characters.Bind(1, 0, "Hero007");
		AssertThat(string.Join(",", Engine_AchievementCharacters.Ids(characters.StoreOfUser(0)!))).IsEqual("3,9");
	}

	[TestCase]
	public void TheSidecarCarriesTheCharacter()
	{
		var sidecar = new SaveSidecar
		{
			Refii = new SaveSidecar.RefiiLayer
			{
				Build = "2",
				Character = new SaveSidecar.CharacterLayer { Id = new string('b', 32), Name = "Hero001", Achievements = { 1, 50 } },
			},
		};
		SaveSidecar back = SaveSidecar.Parse(sidecar.ToJson());
		AssertThat(back.Refii!.Character!.Id).IsEqual(new string('b', 32));
		AssertThat(back.Refii.Character.Name).IsEqual("Hero001");
		AssertThat(string.Join(",", back.Refii.Character.Achievements)).IsEqual("1,50");
		AssertThat(SaveSidecar.Parse(new SaveSidecar { Refii = new SaveSidecar.RefiiLayer() }.ToJson()).Refii!.Character).IsNull();
	}
}
