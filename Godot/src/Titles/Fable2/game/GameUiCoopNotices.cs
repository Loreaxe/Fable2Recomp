using System.Collections.Generic;
using Godot;
using Refii.Host;
using Refii.Ui;

namespace Refii.Titles.Fable2;

using Refii.Game;

/// <summary>
/// Fable 3's co-op join flow in the world: the join and leave notices, the "press START" invitation,
/// a Discord ask-to-join and an orb's gamer card raised as questions, all in the game's own boxes.
/// </summary>
/// <remarks>
/// THE TRANSITIONS ARE THE ONES <see cref="DriveCoopHeroChannel"/> ALREADY READS — the phase seen
/// before this frame's poll and the session model after it — handed to
/// <see cref="Engine_UiCoopNotices"/>, which picks the text. Nothing here detects a session change
/// on its own.
///
/// AN ASK IS ANSWERED IN ONE PLACE OR THE OTHER. The Multiplayer page lists the same asks out of
/// <see cref="Engine_UiJoinRequests"/>; an answer from either forgets the ask in both, and an
/// answer on the page takes the question down.
/// </remarks>
public partial class GameUi
{
	private readonly Engine_UiCoopNotices _coopNotices = new();

	/// <summary>Whether this run has stated the player's status to RexNet.</summary>
	private bool _playerStatusStated;

	/// <summary>Local player two's presence as last seen, beside <c>_coopPhaseSeen</c>.</summary>
	private bool _coopLocalSeen;

	/// <summary>The clock for the two reads that allocate: the pad list and the node's asks.</summary>
	private double _coopJoinPoll;

	private const double CoopJoinPollSeconds = 0.5;

	/// <summary>The Discord user whose ask stands as the in-world question; empty for none.</summary>
	private string _joinAsked = string.Empty;

	/// <summary>
	/// The session transitions of this frame, told. Called by <see cref="DriveCoopHeroChannel"/>
	/// after its poll.
	/// </summary>
	private void TellCoopTransitions(CoopSession.Phase was)
	{
		if (was == CoopSession.State && _coopLocalSeen == CoopSession.LocalCoop)
		{
			return;
		}

		bool told = HostBridge.IsInGame;
		_coopNotices.Session(was, CoopSession.State, CoopSession.Side);
		_coopNotices.LocalCoop(_coopLocalSeen, CoopSession.LocalCoop, told);
		_coopLocalSeen = CoopSession.LocalCoop;
	}

	/// <summary>
	/// One frame of the co-op notices: the pads and the asks on their clock, then whichever notice
	/// is due into the info box.
	/// </summary>
	private void DriveCoopNotices(double delta)
	{
		bool inGame = HostBridge.IsInGame;
		_coopJoinPoll -= delta;
		if (_coopJoinPoll <= 0.0)
		{
			_coopJoinPoll = CoopJoinPollSeconds;

			// A second player can join only into a game with nobody else in it.
			bool open = inGame && !CoopSession.PeerStanding && !CoopSession.LocalCoop &&
				!CoopSession.IsClient;
			_coopNotices.Pads(Refii.Core.Engine_Joypads.Connected(), open);
			DriveJoinQuestion(inGame);
			DriveGamerCard(inGame, open);

			// Friends' greetings (their orb emotes), told as they arrive; the stated status goes
			// out once a run so friends see it without the page ever being opened.
			if (!_playerStatusStated && NetBridge.Available)
			{
				_playerStatusStated = true;
				NetBridge.SetPlayerStatus(Engine_UiJoinRequests.Statuses[Engine_UiJoinRequests.StatusIndex]);
			}

			foreach (string greeting in Engine_UiJoinRequests.TakeGreetings(NetBridge.Friends()))
			{
				_coopNotices.Tell(greeting);
			}
		}

		// Property notices (Engine_UiPropertySource): a rented home in disrepair, a repair's outcome.
		while (Fable2Bridge.TakePropertyNotice() is { } notice)
		{
			if (ForwardPropertyNotice(notice.Kind, notice.Uid))
			{
				continue;
			}

			if (Engine_UiPropertySource.Notice(notice.Kind, notice.Uid) is { } said)
			{
				_coopNotices.Tell(said);
			}
		}

		if (_coopNotices.Tick(delta, PartnerName(), inGame) is { } due)
		{
			// A text key is shown as the game's string book has it, else in our own words.
			string text = !Engine_UiCoopNotices.IsTextKey(due) ? due
				: Engine_UiInventorySource.Strings is { } strings && strings.TryGetString(due, out string own) &&
					own.Length != 0
					? own
					: Refii.Text.Engine_OwnWords.Say(due);
			GD.Print($"[coop-notice] {text}");
			Engine_UiNotices.Post(0u, 0, string.Empty, text, Engine_UiCoopNotices.Seconds, Engine_UiCoopNotices.BoxName);
		}

		DriveNotices(inGame);
	}

	private static string JoinAskKey(string userId) => "joinask:" + userId;

	private string _partnerName = string.Empty;
	private ulong _partnerNameAtMs;

	/// <summary>
	/// The partner's name as this player is shown it: local player two's from their kit; an online
	/// partner's from RexNet (a friend's display name, anyone else's pseudonym — RexNet §17.3.8),
	/// never a name the partner sent. Re-read twice a second; empty until known.
	/// </summary>
	private string PartnerName()
	{
		if (CoopSession.LocalCoop)
		{
			return CoopHeroKit.Remote.Name ?? string.Empty;
		}

		ulong now = Time.GetTicksMsec();
		if (now - _partnerNameAtMs >= 500ul || _partnerName.Length == 0)
		{
			_partnerNameAtMs = now;
			_partnerName = CoopSession.PeerStanding ? NetBridge.PartnerName() ?? string.Empty : string.Empty;
		}

		return _partnerName;
	}

	/// <summary>
	/// Raises the oldest unasked Discord ask as Fable 3's question, one at a time and never over
	/// another host question; takes the question down when the ask was answered on the page or
	/// went stale.
	/// </summary>
	private void DriveJoinQuestion(bool inGame)
	{
		Engine_UiJoinRequests.Refresh(Time.GetTicksMsec());
		if (_joinAsked.Length != 0)
		{
			if (!Engine_UiJoinRequests.IsPending(_joinAsked))
			{
				Engine_UiHostBoxes.Withdraw(JoinAskKey(_joinAsked));
			}

			if (Engine_UiHostBoxes.Asking(JoinAskKey(_joinAsked)))
			{
				return;
			}

			_joinAsked = string.Empty;
		}

		if (!inGame || Engine_UiHostBoxes.Question is not null ||
			Engine_UiJoinRequests.TakeArrival() is not { } ask)
		{
			return;
		}

		string userId = ask.UserId;
		_joinAsked = userId;
		Engine_UiHostBoxes.Ask(
			Refii.Text.Engine_OwnWords.Say("REFII_F2_JOIN_ASK", Engine_UiJoinRequests.Who(ask)),
			() => AnswerJoinAsk(userId, true),
			() => AnswerJoinAsk(userId, false),
			JoinAskKey(userId));
	}

	/// <summary>The last gamer-card ask (<see cref="NetBridge.GamerCard.Request"/>) answered.</summary>
	private uint _gamerCardAnswered;

	/// <summary>
	/// An orb's Interact: the gamer card it asked for, raised as the card's Join Game or Invite to
	/// Game question (<see cref="Engine_UiGamerCard"/>), or told as a notice when it offers
	/// neither. <paramref name="open"/> is whether this game has a seat for a joiner.
	/// </summary>
	private void DriveGamerCard(bool inGame, bool open)
	{
		if (NetBridge.LatestGamerCard() is not { } card || card.Request == _gamerCardAnswered)
		{
			return;
		}

		_gamerCardAnswered = card.Request;
		if (!inGame)
		{
			return;
		}

		bool canInvite = open && NetBridge.HostState() is { Hosting: true };
		Engine_UiCardOffer.Offer offer = Engine_UiCardOffer.Decide(card, canInvite);
		string text = Engine_UiGamerCard.Text(offer, card.Name);
		// Their hero and their profile total, read-only, as their machine states them.
		if (card.Known && Engine_UiGamerCard.Achievements(Refii.Achievements.Engine_Achievements.PeerView(card.PeerId),
				Refii.Achievements.Engine_Achievements.Count, Refii.Achievements.Engine_Achievements.TotalCount) is { } achievements)
		{
			text += "\n\n" + achievements;
		}

		// Their whole list, read-only, on screen 1013: offered when their machine stated it (presence, or
		// for the co-op partner the co-op channel too).
		string peer = card.PeerId;
		string name = card.Name;
		string listOf = card.Partner && Refii.Achievements.Engine_Achievements.PeerView(peer) is null ? string.Empty : peer;
		bool viewable = card.Known && (listOf.Length == 0
			? Refii.Achievements.Engine_Achievements.PartnerView(1) is not null
			: Refii.Achievements.Engine_Achievements.PeerView(listOf) is not null);
		int player = Engine_UiModMenus.MenuPlayer is 2 ? 2 : 1;
		GD.Print($"[coop-notice] gamer card {card.Request}: xuid {card.Xuid} -> '{card.Name}' {card.PeerId} " +
			$"(friend {card.Friend}, hosting {card.Hosting}, partner {card.Partner}) -> {offer}" +
			(viewable ? ", their achievements viewable" : string.Empty));
		string view = Refii.Text.Engine_OwnWords.Say("REFII_ACH_VIEW");
		if (!Engine_UiCardOffer.Asks(offer))
		{
			if (!viewable)
			{
				_coopNotices.Tell(text);
				return;
			}

			// A notice with one thing to do: A opens their list, B closes.
			Engine_UiHostBoxes.Ask(text, answer =>
				{
					if (answer == 0)
					{
						OpenAchievementsOf(listOf, name, player);
					}
				},
				view, null, "gamercard:" + peer);
			return;
		}

		void Act()
		{
			bool sent = NetBridge.PeerAction(peer,
				offer == Engine_UiCardOffer.Offer.Join ? NetBridge.Act.Join : NetBridge.Act.Invite);
			GD.Print($"[coop-notice] gamer card {offer} {name}: {(sent ? "sent" : "refused by the node")}");
			_coopNotices.Tell(Engine_UiGamerCard.Outcome(offer, name, sent));
		}

		if (viewable)
		{
			// The card's question, and X for their list.
			Engine_UiHostBoxes.Ask(text, answer =>
				{
					if (answer == 0)
					{
						Act();
					}
					else if (answer == 2)
					{
						OpenAchievementsOf(listOf, name, player);
					}
				},
				Engine_UiGamerCard.Accept(offer) ?? string.Empty, Refii.Text.Engine_OwnWords.Say("REFII_BACK"), view,
				"gamercard:" + peer);
			return;
		}

		Engine_UiHostBoxes.Ask(text, answer =>
			{
				if (answer == 0)
				{
					Act();
				}
			},
			Engine_UiGamerCard.Accept(offer), null, "gamercard:" + peer);
	}

	/// <summary>The in-world question's answer, through the same store the page answers with.</summary>
	private void AnswerJoinAsk(string userId, bool accept)
	{
		_joinAsked = string.Empty;
		bool sent = Engine_UiJoinRequests.Answer(userId, accept);
		GD.Print($"[coop-notice] join ask {userId} {(accept ? "accepted" : "declined")}" +
			(sent ? string.Empty : " — the node refused the reply"));
		if (!sent)
		{
			_coopNotices.Tell(Refii.Text.Engine_OwnWords.Say("REFII_F2_JOIN_ASK_LAPSED"));
		}
		else if (accept)
		{
			_coopNotices.Tell(Refii.Text.Engine_OwnWords.Say("REFII_F2_JOIN_ASK_INVITED"));
		}
	}

	/// <summary>An ask answered on the Multiplayer page: its in-world question goes with it.</summary>
	private void WithdrawJoinQuestion(string userId)
	{
		Engine_UiHostBoxes.Withdraw(JoinAskKey(userId));
		if (_joinAsked == userId)
		{
			_joinAsked = string.Empty;
		}
	}
}
