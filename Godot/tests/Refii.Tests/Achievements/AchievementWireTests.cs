namespace Refii.Tests.Achievements;

using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Achievements;
using Refii.Titles.Fable2;

/// <summary>
/// A player's achievements as their machine states them over presence: the hero's set and the
/// profile total's round-trip, ids past 255 and id 0 do not travel, anything not of the form reads as
/// nothing, and the orb's card reads both counts out of the title's total.
/// </summary>
[TestSuite]
public class AchievementWireTests
{
	[TestCase]
	public void BothSetsRoundTrip()
	{
		byte[] wire = Engine_AchievementWire.Encode(new uint[] { 10, 23, 66 }, new uint[] { 1, 10, 23, 50, 66 });
		AssertThat(wire[0]).IsEqual((byte)1);
		AssertThat((int)wire[1]).IsEqual(9);
		AssertThat(wire.Length).IsEqual(20);

		Engine_AchievementWire.View view = Engine_AchievementWire.Decode(wire)!;
		AssertThat(string.Join(",", view.Hero.OrderBy(id => id))).IsEqual("10,23,66");
		AssertThat(string.Join(",", view.Profile.OrderBy(id => id))).IsEqual("1,10,23,50,66");
	}

	[TestCase]
	public void OnlyIdsOneTo255Travel()
	{
		Engine_AchievementWire.View view = Engine_AchievementWire.Decode(
			Engine_AchievementWire.Encode(new uint[] { 0, 255, 256 }, new uint[] { 300 }))!;
		AssertThat(string.Join(",", view.Hero)).IsEqual("255");
		AssertThat(view.Profile.Count).IsEqual(0);
		AssertThat(Engine_AchievementWire.Encode(new uint[] { 255 }, new uint[] { 255 }).Length).IsEqual(66);
	}

	[TestCase]
	public void NothingStatedIsAnEmptyView()
	{
		Engine_AchievementWire.View view = Engine_AchievementWire.Decode(
			Engine_AchievementWire.Encode(System.Array.Empty<uint>(), System.Array.Empty<uint>()))!;
		AssertThat(view.Hero.Count).IsEqual(0);
		AssertThat(view.Profile.Count).IsEqual(0);
	}

	[TestCase]
	public void AnythingElseReadsAsNothing()
	{
		AssertThat(Engine_AchievementWire.Decode(null)).IsNull();
		AssertThat(Engine_AchievementWire.Decode(new byte[] { 1 })).IsNull();
		AssertThat(Engine_AchievementWire.Decode(new byte[] { 2, 1, 0, 0 })).IsNull();
		AssertThat(Engine_AchievementWire.Decode(new byte[] { 1, 2, 0, 0 })).IsNull();
		AssertThat(Engine_AchievementWire.Decode(new byte[] { 1, 33 }.Concat(new byte[66]).ToArray())).IsNull();
	}

	[TestCase]
	public void TheCardReadsBothCountsOutOfTheTotal()
	{
		Engine_AchievementWire.View view = Engine_AchievementWire.Decode(
			Engine_AchievementWire.Encode(new uint[] { 10, 11 }, new uint[] { 10, 11, 12 }))!;
		int Count(System.Func<uint, bool> unlocked) => new uint[] { 10, 11, 12, 13 }.Count(unlocked);
		Refii.Core.Engine_Title was = Refii.Core.Engine_Title.Active;
		try
		{
			Refii.Core.Engine_Title.Active = Refii.Core.Engine_Title.Fable2;
			AssertThat(Engine_UiGamerCard.Achievements(view, Count, 4))
				.IsEqual(Refii.Text.Engine_OwnWords.Say("REFII_F2_CARD_ACHIEVEMENTS", 2, 4, 3));
		}
		finally
		{
			Refii.Core.Engine_Title.Active = was;
		}

		AssertThat(string.Format(System.Globalization.CultureInfo.InvariantCulture,
				Refii.Text.Engine_OwnWords.Fable2.Get("REFII_F2_CARD_ACHIEVEMENTS", "en-uk")!, 2, 4, 3))
			.IsEqual("Their hero: 2 of 4 achievements\nTheir profile: 3 of 4");
		AssertThat(Engine_UiGamerCard.Achievements(null, Count, 4)).IsNull();
		AssertThat(Engine_UiGamerCard.Achievements(view, Count, 0)).IsNull();
	}
}
