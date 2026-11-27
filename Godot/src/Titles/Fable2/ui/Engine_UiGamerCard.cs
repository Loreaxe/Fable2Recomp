using Refii.Host;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// The gamer card an orb's Interact asks for, as the one question it comes down to here. On the
/// console the orb box's Interact opened the guide's card for that player, and the card's rows
/// were Join Game and Invite to Game; the guide is not ours to draw, so the card is the game's own
/// question box asking whichever of the two applies (<c>GameUi</c> raises it).
/// </summary>
/// <remarks>
/// PURE: what to say for the offer the shared rule picks (<see cref="Engine_UiCardOffer"/>, which Fable 3's
/// card asks too), from the card the node resolved (<see cref="NetBridge.LatestGamerCard"/>).
/// </remarks>
public static class Engine_UiGamerCard
{
	/// <summary>The question's or notice's text, in our own words (Refii.Text.Engine_OwnWords).</summary>
	public static string Text(Engine_UiCardOffer.Offer offer, string name) => offer switch
	{
		Engine_UiCardOffer.Offer.Join => Refii.Text.Engine_OwnWords.Say("REFII_F2_CARD_JOIN", name),
		Engine_UiCardOffer.Offer.Invite => Refii.Text.Engine_OwnWords.Say("REFII_F2_CARD_INVITE", name),
		Engine_UiCardOffer.Offer.Partner => Refii.Text.Engine_OwnWords.Say("REFII_F2_CARD_PARTNER", name),
		Engine_UiCardOffer.Offer.Nothing => Refii.Text.Engine_OwnWords.Say("REFII_F2_CARD_NOTHING", name),
		_ => Refii.Text.Engine_OwnWords.Say("REFII_F2_CARD_UNREACHABLE"),
	};

	/// <summary>
	/// The card's achievements line: the viewed player's hero and profile total as their own machine
	/// states them (<see cref="Refii.Achievements.Engine_Achievements.PeerView"/>), out of
	/// <paramref name="total"/>; null when nothing was stated or the title's list is not read yet.
	/// </summary>
	public static string? Achievements(Refii.Achievements.Engine_AchievementWire.View? view, System.Func<System.Func<uint, bool>, int> count, int total) =>
		view is null || total <= 0
			? null
			: Refii.Text.Engine_OwnWords.Say("REFII_F2_CARD_ACHIEVEMENTS", count(view.Hero.Contains), total, count(view.Profile.Contains));

	/// <summary>The question's A caption.</summary>
	public static string? Accept(Engine_UiCardOffer.Offer offer) => offer switch
	{
		Engine_UiCardOffer.Offer.Join => Refii.Text.Engine_OwnWords.Say("REFII_F2_CARD_JOIN_GAME"),
		Engine_UiCardOffer.Offer.Invite => Refii.Text.Engine_OwnWords.Say("REFII_F2_CARD_INVITE_TO_GAME"),
		_ => null,
	};

	/// <summary>What is told once A was pressed and the node took (or refused) the action.</summary>
	public static string Outcome(Engine_UiCardOffer.Offer offer, string name, bool sent) => (offer, sent) switch
	{
		(Engine_UiCardOffer.Offer.Join, true) => Refii.Text.Engine_OwnWords.Say("REFII_F2_CARD_JOINING", name),
		(Engine_UiCardOffer.Offer.Join, false) => Refii.Text.Engine_OwnWords.Say("REFII_F2_CARD_NOTHING", name),
		(Engine_UiCardOffer.Offer.Invite, true) => Refii.Text.Engine_OwnWords.Say("REFII_F2_CARD_INVITED", name),
		_ => Refii.Text.Engine_OwnWords.Say("REFII_F2_CARD_NOT_INVITED", name),
	};
}
