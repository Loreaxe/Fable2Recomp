namespace Refii.Tests;

using System;
using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable2;

/// <summary>
/// Player two's worn title is given by NAME (`Lua_Stats_SetTitleRecord` wears a name only when it is one
/// of the hero's unlocked titles), so the name must be the one the held list carries, never the new-game
/// carry alias that shadows the record in the top database layer.
/// </summary>
[TestSuite]
public class CoopHeroTitleNameTests
{
	private const uint Conjurer = 0x133FC49Eu;

	private static readonly string[] Held =
		{ "ObjectInventoryHeroTitleBlade", "ObjectInventoryHeroTitleButcher", "ObjectInventoryHeroTitleConjurer" };

	private static string Top(uint id) => id == Conjurer ? "RefiiCarry_133FC49E" : string.Empty;

	private static string Authored(uint id) => id == Conjurer ? "ObjectInventoryHeroTitleConjurer" : string.Empty;

	private static uint RecordOf(string name) => name == "ObjectInventoryHeroTitleConjurer" ? Conjurer : 0x1000u;

	[TestCase]
	public void TestTheHeldNameOfTheRecordWins()
	{
		AssertThat(CoopHeroKit.TitleName(Conjurer, Held, Top, _ => string.Empty, RecordOf))
			.IsEqual("ObjectInventoryHeroTitleConjurer");
	}

	[TestCase]
	public void TestTheAuthoredNameBeatsTheCarryAlias()
	{
		AssertThat(CoopHeroKit.TitleName(Conjurer, Held, Top, Authored)).IsEqual("ObjectInventoryHeroTitleConjurer");
		AssertThat(CoopHeroKit.TitleName(Conjurer, null, Top, Authored)).IsEqual("ObjectInventoryHeroTitleConjurer");
	}

	[TestCase]
	public void TestNoResolverKeepsTheTopName()
	{
		AssertThat(CoopHeroKit.TitleName(Conjurer, Held, Top, null)).IsEqual("RefiiCarry_133FC49E");
	}

	private const uint Beg = 0x550013D4u;

	private static string TopTrick(uint id) => id == Beg ? "RefiiCarry_550013D4" : string.Empty;

	[TestCase]
	public void TestADogTrickGoesByItsAuthoredName()
	{
		AssertThat(CoopHeroKit.GuestRecordName(Beg, TopTrick, id => id == Beg ? "ExpressionDogTrickBeg" : string.Empty))
			.IsEqual("ExpressionDogTrickBeg");
	}

	[TestCase]
	public void TestTheCarryAliasOnlyWhenNothingElseNamesIt()
	{
		AssertThat(CoopHeroKit.GuestRecordName(Beg, TopTrick, _ => string.Empty)).IsEqual("RefiiCarry_550013D4");
		AssertThat(CoopHeroKit.GuestRecordName(Beg, TopTrick, _ => "RefiiCarry_550013D4")).IsEqual("RefiiCarry_550013D4");
		AssertThat(CoopHeroKit.GuestRecordName(0u, TopTrick, _ => "x")).IsEqual(string.Empty);
	}

	[TestCase]
	public void TestTheNamePairsNameARecordByItsAuthoredName()
	{
		var texts = new Dictionary<uint, string> { [1u] = "RefiiCarry_550013D4", [2u] = "ExpressionDogTrickBeg", [3u] = "Other" };
		Dictionary<uint, string> names = CoopHeroKit.AuthoredNames(
			new[] { (1u, Beg), (2u, Beg), (3u, 0x77u) }, alias => texts.GetValueOrDefault(alias));
		AssertThat(names[Beg]).IsEqual("ExpressionDogTrickBeg");
		AssertThat(names[0x77u]).IsEqual("Other");
		AssertThat(CoopHeroKit.GuestRecordName(Beg, TopTrick, id => names.GetValueOrDefault(id) ?? string.Empty))
			.IsEqual("ExpressionDogTrickBeg");
	}

	[TestCase]
	public void TestTheDogsTricksTravelAsRecordIds()
	{
		string[] tokens = CoopHeroKit.DogTrickTokens(new[] { Beg, 0u, 0x6BDAA851u });
		AssertThat(tokens.Length).IsEqual(2);
		AssertThat(tokens[0]).IsEqual("0x550013D4");
		AssertThat(tokens[1]).IsEqual("0x6BDAA851");
		// The pump keeps a trick token only when it is [A-Za-z0-9_] (guest_tick_pump.cpp's record-name rule).
		foreach (string token in tokens)
		{
			AssertThat(System.Text.RegularExpressions.Regex.IsMatch(token, "^[A-Za-z0-9_]+$")).IsTrue();
		}
		// The real path: the kit's HeroState carries them to the machine that spawns the dog.
		var kit = new CoopHeroKit.Kit(
			Known: true, Female: false, Morality: 0f, Purity: 0f, Renown: 0,
			Strength: 0.5f, Skill: 0.25f, Will: 0.75f,
			AbilityLevels: new int[15], Worn: Array.Empty<uint>(), Inventory: Array.Empty<(uint, int)>(),
			Name: "Sparrow", Money: 1, Experience: new float[4], TotalExperience: new float[4],
			Face: 0u, Fat: 0f, Height: 0f, Tan: 0f,
			DogDefinition: 42u, DogSearching: 2, DogCombat: 5, DogTricks: tokens);
		var decoded = CoopHeroState.Deserialize(CoopHeroState.Serialize(kit, 1u));
		AssertThat(decoded is not null).IsTrue();
		AssertThat(string.Join(",", decoded!.Value.Kit.DogTricks!)).IsEqual("0x550013D4,0x6BDAA851");
	}

	[TestCase]
	public void TestNoTitle()
	{
		AssertThat(CoopHeroKit.TitleName(0u, Held, Top, Authored, RecordOf)).IsNull();
		AssertThat(CoopHeroKit.TitleName(0x55u, new List<string>(), _ => string.Empty, _ => string.Empty)).IsNull();
	}
}
