namespace Refii.Tests.Achievements;

using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Achievements;

/// <summary>
/// A player's achievements on the co-op channel: a hero's character and both sets round-trip through
/// the store text (the form native core/achievement_statement.h parses for Lua), no hero writes the
/// empty text, and anything not of the form reads as no statement.
/// </summary>
[TestSuite]
public class AchievementStatementTests
{
	private const string Character = "0123456789abcdef0123456789abcdef";

	[TestCase]
	public void AHerosStatementRoundTrips()
	{
		string text = Engine_AchievementStatement.Format(Character,
			Engine_AchievementWire.Encode(new uint[] { 23, 1, 66 }, new uint[] { 1, 10, 23, 66 }));
		AssertThat(text.StartsWith(Character + "|01")).IsTrue();
		AssertThat(text.Length <= 256).IsTrue();

		Engine_AchievementStatement.Statement stated = Engine_AchievementStatement.Parse(text)!;
		AssertThat(stated.Character).IsEqual(Character);
		AssertThat(string.Join(",", stated.View.Hero.OrderBy(id => id))).IsEqual("1,23,66");
		AssertThat(string.Join(",", stated.View.Profile.OrderBy(id => id))).IsEqual("1,10,23,66");
	}

	[TestCase]
	public void TheLongestStatementFitsTheStore()
	{
		uint[] all = Enumerable.Range(1, 255).Select(id => (uint)id).ToArray();
		AssertThat(Engine_AchievementStatement.Format(Character, Engine_AchievementWire.Encode(all, all)).Length)
			.IsEqual(32 + 1 + 132);
	}

	[TestCase]
	public void NoHeroIsTheEmptyText()
	{
		AssertThat(Engine_AchievementStatement.Format(null, Engine_AchievementWire.Encode(new uint[] { 1 }, new uint[] { 1 })))
			.IsEqual(string.Empty);
		AssertThat(Engine_AchievementStatement.Parse(string.Empty)).IsNull();
		AssertThat(Engine_AchievementStatement.Parse(null)).IsNull();
	}

	[TestCase]
	public void ATextNotOfTheFormIsNoStatement()
	{
		AssertThat(Engine_AchievementStatement.Parse("abc")).IsNull();
		AssertThat(Engine_AchievementStatement.Parse(Character + "|zz")).IsNull();
		AssertThat(Engine_AchievementStatement.Parse(Character + "|0201ff00")).IsNull();
		AssertThat(Engine_AchievementStatement.Parse("|01010000")).IsNotNull();
		AssertThat(Engine_AchievementStatement.Parse("|010100")).IsNull();
	}
}
