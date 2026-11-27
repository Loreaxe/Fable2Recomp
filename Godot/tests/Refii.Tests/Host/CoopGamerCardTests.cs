namespace Refii.Tests;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Host;
using Refii.Ui;
using Refii.Titles.Fable2;

[TestSuite]
public class CoopGamerCardTests
{
	private static NetBridge.GamerCard Card(string peer = "12D3KooWPeer", bool hosting = false, bool partner = false) =>
		new(1u, peer, "Daring Pony", Friend: false, hosting, partner);

	[TestCase]
	public void TestAHostingPlayerIsOfferedAsAJoin()
	{
		AssertThat(Engine_UiCardOffer.Decide(Card(hosting: true), canInvite: true))
			.IsEqual(Engine_UiCardOffer.Offer.Join);
		AssertThat(Engine_UiCardOffer.Decide(Card(hosting: true), canInvite: false))
			.IsEqual(Engine_UiCardOffer.Offer.Join);
	}

	[TestCase]
	public void TestAPlayerNotHostingIsInvitedOnlyWhenThisGameHasASeat()
	{
		AssertThat(Engine_UiCardOffer.Decide(Card(), canInvite: true)).IsEqual(Engine_UiCardOffer.Offer.Invite);
		AssertThat(Engine_UiCardOffer.Decide(Card(), canInvite: false)).IsEqual(Engine_UiCardOffer.Offer.Nothing);
	}

	[TestCase]
	public void TestThePartnerIsNeitherJoinedNorInvited()
	{
		AssertThat(Engine_UiCardOffer.Decide(Card(hosting: true, partner: true), canInvite: true))
			.IsEqual(Engine_UiCardOffer.Offer.Partner);
	}

	[TestCase]
	public void TestAnUnresolvedXuidIsUnknown()
	{
		AssertThat(Engine_UiCardOffer.Decide(Card(peer: string.Empty, hosting: true), canInvite: true))
			.IsEqual(Engine_UiCardOffer.Offer.Unknown);
	}

	[TestCase]
	public void TestOnlyJoinAndInviteAreQuestions()
	{
		AssertThat(Engine_UiCardOffer.Asks(Engine_UiCardOffer.Offer.Join)).IsTrue();
		AssertThat(Engine_UiCardOffer.Asks(Engine_UiCardOffer.Offer.Invite)).IsTrue();
		AssertThat(Engine_UiCardOffer.Asks(Engine_UiCardOffer.Offer.Partner)).IsFalse();
		AssertThat(Engine_UiCardOffer.Asks(Engine_UiCardOffer.Offer.Nothing)).IsFalse();
		AssertThat(Engine_UiCardOffer.Asks(Engine_UiCardOffer.Offer.Unknown)).IsFalse();
		AssertThat(Engine_UiGamerCard.Accept(Engine_UiCardOffer.Offer.Join))
			.IsEqual(Refii.Text.Engine_OwnWords.Say("REFII_F2_CARD_JOIN_GAME"));
		AssertThat(Engine_UiGamerCard.Accept(Engine_UiCardOffer.Offer.Partner)).IsNull();
	}

	[TestCase]
	public void TestTheJoinQuestionNamesThePlayerAndWarnsOfLeaving()
	{
		Refii.Core.Engine_Title was = Refii.Core.Engine_Title.Active;
		try
		{
			Refii.Core.Engine_Title.Active = Refii.Core.Engine_Title.Fable2;
			string text = Engine_UiGamerCard.Text(Engine_UiCardOffer.Offer.Join, "Daring Pony");
			AssertThat(text).Contains("Daring Pony");
			AssertThat(text).IsEqual(Refii.Text.Engine_OwnWords.Say("REFII_F2_CARD_JOIN", "Daring Pony"));
		}
		finally
		{
			Refii.Core.Engine_Title.Active = was;
		}

		AssertThat(Refii.Text.Engine_OwnWords.Fable2.Get("REFII_F2_CARD_JOIN", "en-uk")).Contains("leaves this one");
	}

	[TestCase]
	public void TestEveryCardLineIsOneOfOurWordsInEveryLanguage()
	{
		// Each offer's question, caption and outcome names a key Fable 2's table states (never a literal).
		string[] keys =
		{
			"REFII_F2_CARD_JOIN", "REFII_F2_CARD_INVITE", "REFII_F2_CARD_PARTNER", "REFII_F2_CARD_NOTHING",
			"REFII_F2_CARD_UNREACHABLE", "REFII_F2_CARD_JOIN_GAME", "REFII_F2_CARD_INVITE_TO_GAME",
			"REFII_F2_CARD_JOINING", "REFII_F2_CARD_INVITED", "REFII_F2_CARD_NOT_INVITED", "REFII_F2_JOIN_ASK",
			"REFII_F2_JOIN_ASK_LAPSED", "REFII_F2_JOIN_ASK_INVITED",
		};
		foreach (string key in keys)
		{
			AssertThat(Refii.Text.Engine_OwnWords.Fable2.Has(key)).OverrideFailureMessage(key).IsTrue();
			string english = Refii.Text.Engine_OwnWords.Fable2.Get(key, "en-uk") ?? string.Empty;
			foreach (string culture in new[] { "fr-fr", "de-de", "cs-cz", "ja-jp", "zh-cht" })
			{
				AssertThat(Refii.Text.Engine_OwnWords.Fable2.Get(key, culture))
					.OverrideFailureMessage($"{key} [{culture}]").IsNotEqual(english);
			}
		}

		Refii.Core.Engine_Title was = Refii.Core.Engine_Title.Active;
		try
		{
			Refii.Core.Engine_Title.Active = Refii.Core.Engine_Title.Fable2;
			foreach (Engine_UiCardOffer.Offer offer in System.Enum.GetValues<Engine_UiCardOffer.Offer>())
			{
				AssertThat(Engine_UiGamerCard.Text(offer, "Daring Pony")).NotContains("REFII_");
				AssertThat(Engine_UiGamerCard.Outcome(offer, "Daring Pony", true)).NotContains("REFII_");
				AssertThat(Engine_UiGamerCard.Outcome(offer, "Daring Pony", false)).NotContains("REFII_");
				AssertThat(Engine_UiGamerCard.Accept(offer) ?? string.Empty).NotContains("REFII_");
			}
		}
		finally
		{
			Refii.Core.Engine_Title.Active = was;
		}
	}
}
