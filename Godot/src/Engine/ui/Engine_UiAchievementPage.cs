using System.Collections.Generic;
using Refii.Achievements;

namespace Refii.Ui;

/// <summary>
/// THE ACHIEVEMENTS SCREEN — its own surface, on the logbook's furniture.
///
/// The guest has no screen for these. Its answer to the logbook's `Achievements` row is
/// `XamShowAchievementsUI` (`sub_826D9620` case 150), the console's guide blade, which on this
/// platform opens nothing — so there is no guest screen id and no tab for `sub_826B89E0`'s
/// switch. The list is host-owned end to end and reached from its own pause tab.
///
/// It stands on `logbookscreen.bgf` because the shape already fits: a full-height row list and a
/// subject page laying out a title, a 64-unit picture and a paragraph, which is exactly an
/// achievement. One row per achievement out of <see cref="Engine_Achievements"/>, locked ones
/// ghosted and reading their locked text, with an authored summary row carrying the tally.
///
/// It used to unfold inside the logbook instead, under the guest's own row. That put a second
/// list inside a screen the guest also drives, and there are far too many achievements for one
/// fold — see <see cref="ScreenId"/> for where they went.
///
/// THE PAGE SHOWS A HERO: the hero of the player who opened it, with the profile's total under its
/// tally; when that player has a partner — local co-op, or online with the partner's machine stating
/// theirs over RexNet presence — a row toggles to the partner's hero and the partner's profile total,
/// read-only. Every open starts on the opener's own.
/// </summary>
public static class Engine_UiAchievementPage
{
	public const int EventType = 150;

	public const string SourceName = "achievements";

	/// <summary>
	/// The host-only screen id this list stands as — past
	/// <see cref="Engine_UiBookSet.HostOnlyScreenId"/>, so the guest's manager never reports it
	/// and the registry walk never tries to find a live presentation for it.
	/// </summary>
	public const uint ScreenId = 1013u;

	/// <summary>The screen's list, as <see cref="Engine_UiTemplates"/> names it.</summary>
	public const string MenuName = "achievements-root";

	/// <summary>The authored summary row — the tally, and where the cursor starts.</summary>
	public const string FolderRowName = "Achievements";

	private const string PageRoot = "Foreground.Book.Pages.ExpressionsPage";
	private const string Title = "ExpressionsPage.Title";
	private const string Icon = "ExpressionsPage.Contents.Icon";
	private const string Description = "ExpressionsPage.Contents.Description";
	private const string Effects = "ExpressionsPage.Contents.Effects";

	/// <summary>Every other subject page on the screen, shut while an achievement is up.</summary>
	private static readonly string[] OtherPages =
	{
		"Foreground.Book.Pages.MoralityPage", "Foreground.Book.Pages.CompletedQuestsPage",
		"Foreground.Book.Pages.RenownPage", "Foreground.Book.Pages.AttractivenessPage",
		"Foreground.Book.Pages.StatsPage", "Foreground.Book.Pages.DogPage",
		"Foreground.Book.Pages.HelpPage",
	};

	/// <summary>The verb of the partner toggle row: <c>Achievements:view:partner</c> / <c>own</c>.</summary>
	public const string ViewPrefix = "Achievements:view:";

	public const string ToggleRowName = "AchievementsView";

	private static bool _partnerView;

	/// <summary>
	/// Another player's whole list, read-only, while the page shows it: the orb's player whose card asked
	/// for it (their machine's statement over RexNet presence), "" the co-op partner; null the opener's own.
	/// </summary>
	private static string? _peer;

	private static string _peerName = string.Empty;

	/// <summary>The player who opened the page (1 or 2).</summary>
	private static int Opener => Engine_UiModMenus.MenuPlayer is 2 ? 2 : 1;

	/// <summary>
	/// Another player's achievements while the page shows them — the partner's (the toggle) or another
	/// player's (<see cref="ViewPeer"/>) — else null (the opener's own).
	/// </summary>
	private static Engine_AchievementWire.View? Partner =>
		_peer is { } peer
			? peer.Length == 0 ? Engine_Achievements.PartnerView(Opener) : Engine_Achievements.PeerView(peer)
			: _partnerView ? Engine_Achievements.PartnerView(Opener) : null;

	/// <summary>
	/// The next stand of the page shows another player's whole list, read-only: <paramref name="peerId"/> the
	/// RexNet peer whose card asked (their machine's statement), "" the co-op partner; <paramref name="name"/>
	/// heads its folder. False when nothing of theirs has arrived.
	/// </summary>
	public static bool ViewPeer(string peerId, string name)
	{
		Engine_AchievementWire.View? view = peerId.Length == 0
			? Engine_Achievements.PartnerView(Opener)
			: Engine_Achievements.PeerView(peerId);
		if (view is null)
		{
			return false;
		}

		_peer = peerId;
		_peerName = name;
		_partnerView = false;
		return true;
	}

	/// <summary>Whether the hero the page shows has <paramref name="id"/>.</summary>
	private static bool Shows(uint id) =>
		Partner is { } partner ? partner.Hero.Contains(id)
		: _peer is null && Engine_Achievements.IsUnlocked(Opener, id);

	/// <summary>Opens the page on the opener's own hero (registered as screen <see cref="ScreenId"/>'s opener).</summary>
	public static Engine_UiCommandResult Open(uint screenId, int player, Engine_UiCommandContext context)
	{
		_partnerView = false;
		_peer = null;
		return Stand(screenId, player, context);
	}

	private static Engine_UiCommandResult Stand(uint screenId, int player, Engine_UiCommandContext context) =>
		Engine_UiHostScreens.Books is { } books
			? books(screenId, player, context)
			: Engine_UiCommandResult.Refused($"screen {screenId} stands in a game, over the pause book; no game UI is up");

	/// <summary>The partner toggle's verb: shows the partner's hero, or the opener's own again.</summary>
	public static Engine_UiCommandResult View(string argument, Engine_UiCommandContext context)
	{
		string which = context.Command.StartsWith(ViewPrefix, System.StringComparison.Ordinal)
			? context.Command[ViewPrefix.Length..]
			: argument;
		bool partner = which == "partner";
		if (partner && Engine_Achievements.PartnerView(Opener) is null)
		{
			return Engine_UiCommandResult.Refused("no partner's achievements to show (no partner, or theirs have not arrived)");
		}

		_partnerView = partner;
		_peer = null;
		return Stand(ScreenId, context.Player, context);
	}

	private static readonly Dictionary<string, string> NoValues = new();
	private static readonly Dictionary<string, uint> NoTextures = new();
	private static readonly Dictionary<string, bool> NoActive = new();

	/// <summary>One row per achievement, in the title's order. Locked rows are ghosted.</summary>
	public static IReadOnlyList<Engine_UiMenuSources.Row> Rows(string argument)
	{
		var rows = new List<Engine_UiMenuSources.Row>(Engine_Achievements.TotalCount + 1);
		if (_peer is null && Engine_Achievements.PartnerView(Opener) is not null)
		{
			bool partner = _partnerView;
			rows.Add(new Engine_UiMenuSources.Row(
				Name: ToggleRowName,
				Label: Refii.Text.Engine_OwnWords.Say(partner ? "REFII_ACH_SHOW_OWN" : "REFII_ACH_SHOW_PARTNER"),
				Icon: string.Empty,
				EventType: EventType,
				Id: 0,
				Rating: 0,
				Committable: false,
				Command: ViewPrefix + (partner ? "own" : "partner")));
		}

		foreach (AchievementDefinition definition in Engine_Achievements.Definitions)
		{
			// Locked or earned is the tab face — slate for a locked row, leather for an earned one
			// (Engine_FrontEndMenuView.LockedRowsSlate).
			string name = definition.Name.Length == 0 ? Refii.Text.Engine_OwnWords.Say("REFII_ACH_NUMBERED", definition.Id) : definition.Name;
			rows.Add(new Engine_UiMenuSources.Row(
				Name: $"Achievement:{definition.Id}",
				Label: name,
				Icon: string.Empty,
				EventType: EventType,
				Id: definition.Id,
				Rating: 0,
				Committable: false,
				Ghosted: !Shows(definition.Id)));
		}

		return rows;
	}

	/// <summary>True for the folder row and every achievement row under it.</summary>
	public static bool Owns(Engine_UiExpandableMenu.Item? row) =>
		row is { EventType: EventType };

	private static AchievementDefinition? DefinitionOf(Engine_UiExpandableMenu.Item row) =>
		row.Name.StartsWith("Achievement:", System.StringComparison.Ordinal)
			? Engine_Achievements.Definition(((uint)row.IdHigh << 16) | ((uint)row.IdLow & 0xFFFFu))
			: null;

	public static IReadOnlyDictionary<string, string> Values(Engine_UiExpandableMenu.Item? row)
	{
		if (!Owns(row))
		{
			return NoValues;
		}

		var values = new Dictionary<string, string>();
		if (DefinitionOf(row!) is not { } definition)
		{
			// The folder itself (and the toggle row): the hero's tally, then its profile's total.
			values[Title] = Refii.Text.Engine_OwnWords.Say("REFII_ACHIEVEMENTS");
			values[Description] = !Engine_Achievements.DefinitionsLoaded
				? Refii.Text.Engine_OwnWords.Say("REFII_ACH_NOT_READY")
				: _peer is not null
					? (_peerName.Length == 0 ? string.Empty : _peerName + "\n\n") + PeerTally(Partner)
					: Tally(Partner, Engine_Achievements.PartnerIsLocal(Opener));
			return values;
		}

		bool unlocked = Shows(definition.Id);
		string text = unlocked || definition.LockedDescription.Length == 0
			? definition.UnlockedDescription
			: definition.LockedDescription;
		// Our own words in the game's language; the date ISO, language-neutral (as Fable 3's page).
		string status = unlocked
			? Partner is null && Engine_Achievements.UnlockedAt(Opener, definition.Id) is { } when
				? Refii.Text.Engine_OwnWords.Say("REFII_ACH_UNLOCKED_ON",
					when.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))
				: Refii.Text.Engine_OwnWords.Say("REFII_ACH_UNLOCKED")
			: Refii.Text.Engine_OwnWords.Say("REFII_ACH_LOCKED");

		values[Title] = definition.Name;
		// Two lines, not one joined by a middle dot: the shipped bitmap font draws U+00B7 as `»`.
		values[Description] = $"{text}\n\n{definition.Gamerscore} G\n{status}";
		return values;
	}

	/// <summary>
	/// The folder's text: the hero's tally and gamerscore, then the profile's total — the opener's own
	/// (<paramref name="partner"/> null), or the partner's, whose profile is the shared one in local co-op
	/// (<paramref name="partnerLocal"/>) and their own online.
	/// </summary>
	private static string Tally(Engine_AchievementWire.View? partner, bool partnerLocal)
	{
		string[] lines = TallyLines(Opener, partner, partnerLocal);
		return lines[0] + "\n" + lines[1] + "\n\n" + lines[2];
	}

	/// <summary>Another player's folder text: their hero's tally and gamerscore, then their profile's total.</summary>
	private static string PeerTally(Engine_AchievementWire.View? view)
	{
		if (view is null)
		{
			return Refii.Text.Engine_OwnWords.Say("REFII_ACH_NONE_SHARED");
		}

		string[] lines = TallyLines(Opener, view, otherLocal: _peer?.Length == 0 && Engine_Achievements.PartnerIsLocal(Opener),
			partner: _peer?.Length == 0);
		return lines[0] + "\n" + lines[1] + "\n\n" + lines[2];
	}

	/// <summary>
	/// A hero's tally, its gamerscore and its profile's total, as three lines: player
	/// <paramref name="player"/>'s own hero (<paramref name="other"/> null), or another player's as a view —
	/// the partner's (<paramref name="partner"/>) or anyone's — whose profile is the shared one when
	/// <paramref name="otherLocal"/> and their own otherwise. The one wording every title's page uses.
	/// </summary>
	public static string[] TallyLines(int player, Engine_AchievementWire.View? other, bool otherLocal, bool partner = true)
	{
		int total = Engine_Achievements.TotalCount;
		System.Func<uint, bool> hero = other is { } p ? p.Hero.Contains : id => Engine_Achievements.IsUnlocked(player, id);
		System.Func<uint, bool> profile = other is { } q ? q.Profile.Contains : Engine_Achievements.IsUnlocked;
		string heroKey = other is null ? "REFII_ACH_THIS_HERO" : partner ? "REFII_ACH_PARTNER_HERO" : "REFII_ACH_THEIR_HERO";
		return new[]
		{
			Refii.Text.Engine_OwnWords.Say(heroKey, Engine_Achievements.Count(hero), total),
			Refii.Text.Engine_OwnWords.Say("REFII_ACH_GAMERSCORE", Engine_Achievements.Score(hero), Engine_Achievements.TotalGamerscore),
			Refii.Text.Engine_OwnWords.Say(other is null || otherLocal ? "REFII_ACH_PROFILE_TOTAL" : "REFII_ACH_PARTNER_PROFILE",
				Engine_Achievements.Count(profile), total),
		};
	}

	/// <summary>The icon quad, textured with the achievement's PNG through the host registry.</summary>
	public static IReadOnlyDictionary<string, uint> Textures(Engine_UiExpandableMenu.Item? row)
	{
		if (!Owns(row) || DefinitionOf(row!) is not { } definition ||
			Engine_Achievements.Icon(definition.Id) is not { } icon)
		{
			return NoTextures;
		}

		return new Dictionary<string, uint> { [Icon] = Engine_UiHostTextures.Register(icon) };
	}

	/// <summary>
	/// THIS SCREEN STATES NO SLIDES, and that is the statement, not an omission.
	///
	/// A host slide is not a patch onto the guest's array — `Engine_UiOwnedBook.Slid` builds the
	/// whole array the moment the host names one element, filling every OTHER ordinal with 0.
	/// Zero is "no slide selected", which resolves each owner to its master baseline, and
	/// `logbookscreen`'s master hides nearly everything: naming `Foreground.Book` alone took the
	/// book from drawing to `0/677 node(s) drawing` — the guest's own logbook draws 359 of the
	/// same 677.
	///
	/// With no slide array at all the resolve skips its slide pass and every element stays
	/// unhidden, which is the right baseline for a screen with no live sample: the one thing
	/// that must be shut is the seven subject pages we are not on, and
	/// <see cref="Active"/> shuts those by name.
	/// </summary>
	/// <summary>
	/// The subject page the guest would have selected had it known the row: the expressions page
	/// on, the other seven off, and the page's expression-effects block off — its stats table
	/// has nothing to say about an achievement.
	/// </summary>
	public static IReadOnlyDictionary<string, bool> Active(Engine_UiExpandableMenu.Item? row)
	{
		if (!Owns(row))
		{
			return NoActive;
		}

		var active = new Dictionary<string, bool> { [PageRoot] = true, [Effects] = false };
		foreach (string page in OtherPages)
		{
			active[page] = false;
		}

		// Without an icon the placeholder art (an expression's) would show; hide the quad.
		if (DefinitionOf(row!) is not { } definition || Engine_Achievements.Icon(definition.Id) is null)
		{
			active[Icon] = false;
		}

		return active;
	}
}
