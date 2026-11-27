using Refii.Host;

namespace Refii.Ui;

/// <summary>
/// WHAT ANOTHER PLAYER'S CARD OFFERS, for every title: the orb's (and Fable 3's partner list's, proposal's,
/// world map's) gamer card ask comes down to one question here — join their game, invite them into this one,
/// or neither. On the console the guide's card had Join Game and Invite to Game; the guide is not ours to
/// draw, so each title asks this in its own look (Fable 2 <c>Engine_UiGamerCard</c> in a question box, Fable 3
/// <c>Fable3GamerCard</c> on its card's rows).
/// </summary>
/// <remarks>PURE: from the card the node resolved (<see cref="NetBridge.LatestGamerCard"/>) and whether this game has a seat.</remarks>
public static class Engine_UiCardOffer
{
	public enum Offer
	{
		/// <summary>The XUID matched no player the node knows (they left, or the link is down).</summary>
		Unknown,

		/// <summary>They are already in this game.</summary>
		Partner,

		/// <summary>They are hosting: join their game.</summary>
		Join,

		/// <summary>They are not hosting and this game has a seat: invite them into it.</summary>
		Invite,

		/// <summary>Neither side has a game for the other.</summary>
		Nothing,
	}

	/// <summary>
	/// What the card offers. <paramref name="canInvite"/> is whether this game would take them: we
	/// host it and nobody else is in it.
	/// </summary>
	public static Offer Decide(NetBridge.GamerCard card, bool canInvite)
	{
		if (!card.Known)
		{
			return Offer.Unknown;
		}

		if (card.Partner)
		{
			return Offer.Partner;
		}

		if (card.Hosting)
		{
			return Offer.Join;
		}

		return canInvite ? Offer.Invite : Offer.Nothing;
	}

	/// <summary>Whether the offer is a question (A acts) rather than a notice.</summary>
	public static bool Asks(Offer offer) => offer is Offer.Join or Offer.Invite;

}
