namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Godot;
using Refii.Achievements;
using Refii.Host;
using Refii.Text;

/// <summary>
/// ANOTHER PLAYER'S CARD IN FABLE 3: wherever the guest shows a player's gamer card it calls
/// `XamShowGamerCardUIForXUID` (the thunk TU3 sub_831314C0 / disc sub_830FF200), which the shell is asked
/// (rexglue shadow xam_ui.cpp RequestGamerCard; <see cref="NetBridge.LatestGamerCard"/> names the player).
/// Its callers: the co-op partners box's 77 (CMultiplayerPartnersBox_HandleInput sub_82B916C0 ->
/// sub_82B92B40 0x82B919C4 / disc sub_82B5CA70 -> sub_82B5DEE8), the marriage proposal's card
/// (CMarriageProposalQueryBox_HandleInput 0x8287B82C), the world map's player (CInteractionModeMenuWorldMap
/// slot 7, 0x8271A9D0), the stats box's friends page (CStatsBox_HandleInput page 4 -> sub_829B6830
/// 0x829B6310 / disc 0x8296DA10), and the session helpers sub_821A3AC0, sub_8236C168, sub_828BD968 (TU3).
/// Fable 3 drew nothing for it before; this is its card, in its own list look: the player's name, their
/// hero's and their profile's achievement counts as their machine states them over RexNet presence
/// (<see cref="Engine_Achievements.PeerView"/>), and a row that opens their list
/// (<see cref="Fable3AchievementsPage.OpenPeer"/>), and first the card's own question — Join Game while they host,
/// Invite to Game while this game hosts with its seat free (<see cref="Refii.Ui.Engine_UiCardOffer"/>). Either local
/// player's pad drives it; B leaves.
/// </summary>
public partial class Fable3GamerCard : Fable3HostSurface
{
	private const string ViewRow = "ViewAchievements";

	private NetBridge.GamerCard? _card;
	private uint _answered;
	private bool _primed;
	private readonly int[] _pads = { 0, Fable3ActingPlayer.PadOfTwo };

	protected override string LogTag => "fable3-gamer-card";

	protected override IReadOnlyList<int> Pads => _pads;

	protected override Fable3ScreenPage? Page()
	{
		if (NetBridge.LatestGamerCard() is { } asked && asked.Request != _answered)
		{
			_answered = asked.Request;
			// A card asked before this UI stood is not shown at its start.
			if (_primed && HostBridge.IsGuestUp)
			{
				_card = asked;
				Cursor = 0;
				GD.Print($"[fable3-gamer-card] card {asked.Request}: xuid {asked.Xuid} -> '{asked.Name}' {asked.PeerId}");
			}
		}

		_primed = true;
		if (_card is not { } card)
		{
			return null;
		}

		var rows = new List<Fable3ScreenPage.Row>(4);
		var prompts = new List<Fable3ScreenPage.Prompt>(2);
		// The card's question first, as the guide's card had it: join their game, or invite them into this
		// one (Engine_UiCardOffer, the rule Fable 2's card asks too).
		Refii.Ui.Engine_UiCardOffer.Offer offer = Offer(card);
		if (Refii.Ui.Engine_UiCardOffer.Asks(offer))
		{
			bool join = offer == Refii.Ui.Engine_UiCardOffer.Offer.Join;
			rows.Add(new Fable3ScreenPage.Row(join ? JoinRow : InviteRow,
				join ? GameWord("TEXT_GUI_ORB_JOIN_GAME", "REFII_F3_CARD_JOIN_GAME") : Engine_OwnWords.Say("REFII_F3_CARD_INVITE_TO_GAME"),
				false, join ? GameWord("GUI_JOIN_DESTRUCTIVE_ACTION_QUERY", "REFII_F3_CARD_JOIN_WARNING") : string.Empty));
		}

		if (card.Known && Engine_Achievements.DefinitionsLoaded && Engine_Achievements.PeerView(card.PeerId) is { } view)
		{
			string[] tally = Refii.Ui.Engine_UiAchievementPage.TallyLines(Fable3ActingPlayer.One, view, false, false);
			rows.Add(new Fable3ScreenPage.Row("Hero", tally[0], false, tally[1]));
			rows.Add(new Fable3ScreenPage.Row("Profile", tally[2]));
			rows.Add(new Fable3ScreenPage.Row(ViewRow, Engine_OwnWords.Say("REFII_ACH_VIEW")));
		}
		else
		{
			rows.Add(new Fable3ScreenPage.Row("None", Engine_OwnWords.Say("REFII_ACH_NONE_SHARED"), true));
		}

		_rows = rows;
		if (rows.Exists(r => r.Name is JoinRow or InviteRow or ViewRow))
		{
			prompts.Add(new Fable3ScreenPage.Prompt("A", Engine_OwnWords.Say("REFII_SELECT")));
		}

		prompts.Add(new Fable3ScreenPage.Prompt("B", Engine_OwnWords.Say("REFII_BACK")));
		return new Fable3ScreenPage(card.Name, rows, Cursor, Cursor >= 0 && Cursor < rows.Count ? rows[Cursor].Description : string.Empty,
			prompts, true);
	}

	private const string JoinRow = "Join";
	private const string InviteRow = "Invite";
	private List<Fable3ScreenPage.Row> _rows = new();

	/// <summary>
	/// What this card offers: join them while they host, invite them while this game hosts with its seat
	/// free (no partner joined), else nothing.
	/// </summary>
	private static Refii.Ui.Engine_UiCardOffer.Offer Offer(NetBridge.GamerCard card) =>
		Refii.Ui.Engine_UiCardOffer.Decide(card,
			NetBridge.HostState() is { Hosting: true } && !Refii.Core.Engine_TitleGame.Active.PeerJoined);

	/// <summary>Fable 3's own line for <paramref name="key"/> when its text has one, else ours.</summary>
	private static string GameWord(string key, string own)
	{
		string text = Refii.Ui.Engine_UiGuiText.Resolve(key, Fable3Strings.Current);
		return text != key && text.Length != 0 ? text : Engine_OwnWords.Say(own);
	}

	/// <summary>
	/// A on the cursor's row: Join Game / Invite to Game asks the node to act on the player (as Fable 2's card
	/// does) and tells the outcome; any other row opens the player's achievements in place of the card.
	/// </summary>
	protected override void Accept(int row)
	{
		if (_card is not { } card || !card.Known)
		{
			return;
		}

		string name = row >= 0 && row < _rows.Count ? _rows[row].Name : string.Empty;
		if (name is JoinRow or InviteRow)
		{
			bool join = name == JoinRow;
			bool sent = NetBridge.PeerAction(card.PeerId, join ? NetBridge.Act.Join : NetBridge.Act.Invite);
			GD.Print($"[fable3-gamer-card] {(join ? "join" : "invite")} {card.Name}: {(sent ? "sent" : "refused by the node")}");
			_card = null;
			Refii.Ui.Engine_UiHostBoxes.Tell(Engine_OwnWords.Say(
				join ? sent ? "REFII_F3_CARD_JOINING" : "REFII_F3_CARD_NOT_JOINED"
				: sent ? "REFII_F3_CARD_INVITED" : "REFII_F3_CARD_NOT_INVITED", card.Name));
			return;
		}

		if (Engine_Achievements.PeerView(card.PeerId) is not { } view)
		{
			return;
		}

		_card = null;
		Fable3AchievementsPage.OpenPeer(view, card.Name);
	}

	protected override void Back()
	{
		_card = null;
		GD.Print("[fable3-gamer-card] closed");
	}
}
