using System;
using System.Collections.Generic;
using Refii.Text;

namespace Refii.Ui;

/// <summary>
/// What the Multiplayer page says, in our own words (<see cref="Engine_OwnWords"/>, the shell's table: the
/// page serves both titles): which key a state, an option, a status or a greeting is said through, and the
/// lines composed from more than one key.
/// </summary>
/// <remarks>
/// PURE: no node, no engine type; <see cref="Engine_UiNetworkSource"/> and
/// <see cref="Engine_UiJoinRequests"/> hand in what the node stated. A player's STATUS travels to friends as
/// text (<c>NetBridge.SetPlayerStatus</c>, read back as <c>Friend.Status</c>), so its wire form stays the
/// English of <see cref="WireStatuses"/> on every build and is translated only where it is drawn
/// (<see cref="DrawnStatus"/>).
/// </remarks>
public static class Engine_UiNetworkWords
{
	/// <summary>Every key the page says, each stated in <see cref="Engine_OwnWords.Shared"/>.</summary>
	public static readonly IReadOnlyList<string> Keys = new[]
	{
		"REFII_MP_MODS_NO_FOLDER", "REFII_MP_MODS_UNREADABLE", "REFII_MP_NO_MODS", "REFII_MP_STATE_IN_A_GAME",
		"REFII_MP_STATE_IN_TITLE", "REFII_MP_STATE_ONLINE", "REFII_MP_STATE_OFFLINE", "REFII_MP_REQUEST_REACHED",
		"REFII_MP_INVITE_UNDELIVERED", "REFII_MP_JOIN_UNANSWERED", "REFII_MP_NET_ERROR", "REFII_MP_REQUEST_DECLINED",
		"REFII_MP_ASKS_TO_JOIN", "REFII_MP_DECLINE", "REFII_MP_SELF_NAMED", "REFII_MP_DISMISS", "REFII_MP_INVITED_YOU",
		"REFII_MP_GAME_OF", "REFII_MP_GAME_OPEN", "REFII_MP_HOSTING_OFF", "REFII_MP_JOIN_ANYONE",
		"REFII_MP_JOIN_INVITES", "REFII_MP_JOIN_NO_ONE", "REFII_MP_NOT_HOSTED", "REFII_MP_MY_STATUS", "REFII_MP_SEND",
		"REFII_MP_ABOUT_FRIENDS", "REFII_MP_ABOUT_REQUESTS", "REFII_MP_ABOUT_INVITES", "REFII_MP_ABOUT_GAMES",
		"REFII_MP_FRIEND_HOSTING_GAME", "REFII_MP_FRIEND_HOSTING_TITLE", "REFII_MP_FRIEND_ONLINE",
		"REFII_MP_FRIEND_OFFLINE", "REFII_MP_PUBLIC_FULL", "REFII_MP_PUBLIC_OPEN", "REFII_MP_NEEDS_INVITE",
		"REFII_MP_A_JOINS", "REFII_MP_NOT_LISTED", "REFII_MP_REQUEST_WANTS", "REFII_MP_REQUEST_ANSWER",
		"REFII_MP_REQUEST_WITHDRAWN", "REFII_MP_ABOUT_DISMISS", "REFII_MP_ABOUT_JOINASK", "REFII_MP_ABOUT_JOINDENY",
		"REFII_MP_ABOUT_INVITE", "REFII_MP_MOD_MISSING", "REFII_MP_MOD_VERSION_BY", "REFII_MP_MOD_VERSION",
		"REFII_MP_MOD_BY", "REFII_MP_NONE_MODS", "REFII_MP_NONE_FRIENDS", "REFII_MP_NONE_FRIENDS_OFF",
		"REFII_MP_NONE_REQUESTS", "REFII_MP_NONE_INVITES", "REFII_MP_NONE_GAMES", "REFII_MP_NONE_GAMES_OFF",
		"REFII_MP_NONE_OTHER", "REFII_MP_ABOUT_STATUS", "REFII_MP_ABOUT_GREET", "REFII_MP_ABOUT_JOINABLE",
		"REFII_MP_ADD_OFFER", "REFII_MP_CODE_COPIED", "REFII_MP_CODE_COPY", "REFII_MP_CODE_NONE",
		"REFII_MP_STATUS_EXPLORING", "REFII_MP_STATUS_COOP", "REFII_MP_STATUS_TRADING", "REFII_MP_STATUS_GIFTS",
		"REFII_MP_GREET_WAVE", "REFII_MP_GREET_JOIN", "REFII_MP_GREET_THANKS", "REFII_MP_GREET_WELL_MET",
		"REFII_MP_HEARD_WAVE", "REFII_MP_HEARD_JOIN", "REFII_MP_HEARD_THANKS", "REFII_MP_HEARD_WELL_MET",
		"REFII_MP_A_FRIEND", "REFII_MP_DISCORD_PLAYER", "REFII_MP_STATUS_NOW",
	};

	/// <summary>
	/// The statuses a player can state (Fable 3's orb status) AS SENT: English on every build, never drawn
	/// as written. Index = the player's kept choice.
	/// </summary>
	public static readonly string[] WireStatuses =
	{
		"Exploring",
		"Looking for co-op",
		"Trading",
		"Giving gifts",
	};

	private static readonly string[] StatusKeys =
	{
		"REFII_MP_STATUS_EXPLORING",
		"REFII_MP_STATUS_COOP",
		"REFII_MP_STATUS_TRADING",
		"REFII_MP_STATUS_GIFTS",
	};

	/// <summary>The key of status <paramref name="index"/> (clamped to the stated ones).</summary>
	public static string StatusKey(int index) => StatusKeys[Math.Clamp(index, 0, StatusKeys.Length - 1)];

	/// <summary>Status <paramref name="index"/> as drawn.</summary>
	public static string Status(int index) => Engine_OwnWords.Say(StatusKey(index));

	/// <summary>The key of a status as received, or null when it is none of <see cref="WireStatuses"/>.</summary>
	public static string? WireStatusKey(string? wire)
	{
		for (int i = 0; i < WireStatuses.Length; ++i)
		{
			if (string.Equals(WireStatuses[i], wire, StringComparison.Ordinal))
			{
				return StatusKeys[i];
			}
		}

		return null;
	}

	/// <summary>A friend's status as drawn: a known wire text through its key, any other text as received.</summary>
	public static string DrawnStatus(string? wire) =>
		WireStatusKey(wire) is { } key ? Engine_OwnWords.Say(key) : wire ?? string.Empty;

	/// <summary>The own-status row's label.</summary>
	public static string MyStatus(int index) => Engine_OwnWords.Say("REFII_MP_MY_STATUS", Status(index));

	/// <summary>What the page says once the status was stepped.</summary>
	public static string StatusNow(int index) => Engine_OwnWords.Say("REFII_MP_STATUS_NOW", Status(index));

	private static readonly (string Label, string Heard)[] GreetingKeys =
	{
		("REFII_MP_GREET_WAVE", "REFII_MP_HEARD_WAVE"),
		("REFII_MP_GREET_JOIN", "REFII_MP_HEARD_JOIN"),
		("REFII_MP_GREET_THANKS", "REFII_MP_HEARD_THANKS"),
		("REFII_MP_GREET_WELL_MET", "REFII_MP_HEARD_WELL_MET"),
	};

	/// <summary>How many greetings a player can send (Fable 3's orb emotes): signal id = index + 1.</summary>
	public static int GreetingCount => GreetingKeys.Length;

	/// <summary>The key of greeting <paramref name="index"/>'s row caption (clamped).</summary>
	public static string GreetingKey(int index) => GreetingKeys[Math.Clamp(index, 0, GreetingKeys.Length - 1)].Label;

	/// <summary>The key of the line told when a friend sent greeting <paramref name="index"/> (clamped).</summary>
	public static string HeardKey(int index) => GreetingKeys[Math.Clamp(index, 0, GreetingKeys.Length - 1)].Heard;

	/// <summary>The greeting row's label.</summary>
	public static string SendGreeting(int index) =>
		Engine_OwnWords.Say("REFII_MP_SEND", Engine_OwnWords.Say(GreetingKey(index)));

	/// <summary>The line told when a friend sent greeting <paramref name="index"/>; a nameless friend is "A friend".</summary>
	public static string Heard(int index, string? name) =>
		Engine_OwnWords.Say(HeardKey(index), string.IsNullOrEmpty(name) ? Engine_OwnWords.Say("REFII_MP_A_FRIEND") : name);

	/// <summary>The name a Discord ask is shown under; Discord can hand over an empty one.</summary>
	public static string DiscordName(string? name) =>
		string.IsNullOrEmpty(name) ? Engine_OwnWords.Say("REFII_MP_DISCORD_PLAYER") : name;

	/// <summary>The key of a friend's state: hosting (in a game / in a named title), online, offline.</summary>
	public static string FriendStateKey(bool joinable, bool online, string? title) =>
		joinable
			? (string.IsNullOrEmpty(title) ? "REFII_MP_STATE_IN_A_GAME" : "REFII_MP_STATE_IN_TITLE")
			: online ? "REFII_MP_STATE_ONLINE" : "REFII_MP_STATE_OFFLINE";

	/// <summary>
	/// A friend row's state text: the state, then the friend's status (<see cref="DrawnStatus"/>) while
	/// they are online and stated one.
	/// </summary>
	public static string FriendState(bool joinable, bool online, string? title, string? status)
	{
		string state = Engine_OwnWords.Say(FriendStateKey(joinable, online, title), title ?? string.Empty);
		return online && !string.IsNullOrEmpty(status) ? $"{state} — {DrawnStatus(status)}" : state;
	}

	/// <summary>A friend's (or a friend's game's) page: their status quoted, then where they are and what A does.</summary>
	public static string FriendPage(bool joinable, bool online, string? title, string? status)
	{
		string stated = string.IsNullOrEmpty(status) ? string.Empty : $"\"{DrawnStatus(status)}\"\n\n";
		string where = joinable
			? (string.IsNullOrEmpty(title)
				? Engine_OwnWords.Say("REFII_MP_FRIEND_HOSTING_GAME")
				: Engine_OwnWords.Say("REFII_MP_FRIEND_HOSTING_TITLE", title))
			: Engine_OwnWords.Say(online ? "REFII_MP_FRIEND_ONLINE" : "REFII_MP_FRIEND_OFFLINE");
		return stated + where;
	}

	/// <summary>The key of the who-can-join option: 0 anyone, 1 invites only, anything else no one.</summary>
	public static string JoinModeKey(int mode) => mode switch
	{
		0 => "REFII_MP_JOIN_ANYONE",
		1 => "REFII_MP_JOIN_INVITES",
		_ => "REFII_MP_JOIN_NO_ONE",
	};

	/// <summary>The who-can-join row's label; while no game is hosted it says so after the option.</summary>
	public static string WhoCanJoin(int mode, bool hosting)
	{
		string label = Engine_OwnWords.Say(JoinModeKey(mode));
		return hosting ? label : label + Engine_OwnWords.Say("REFII_MP_NOT_HOSTED");
	}

	/// <summary>A stranger's request row: their own name, marked as self-named, then their note when they wrote one.</summary>
	public static string RequestLabel(string name, bool selfNamed, string? note)
	{
		string shown = selfNamed ? Engine_OwnWords.Say("REFII_MP_SELF_NAMED", name) : name;
		return string.IsNullOrEmpty(note) ? shown : $"{shown} — \"{note}\"";
	}

	/// <summary>A request's page: its note (or that they want to be a friend), then what the two rows do.</summary>
	public static string RequestPage(string? note) =>
		(string.IsNullOrEmpty(note) ? Engine_OwnWords.Say("REFII_MP_REQUEST_WANTS") : $"\"{note}\"") +
		"\n\n" + Engine_OwnWords.Say("REFII_MP_REQUEST_ANSWER");

	/// <summary>A public game's row: its host and how many seats are open.</summary>
	public static string PublicGameRow(string host, int open, int total) =>
		Engine_OwnWords.Say("REFII_MP_GAME_OPEN", host, Number(open), Number(total));

	/// <summary>A public game's page: full, or how many seats are open and whether A joins or an invitation is needed.</summary>
	public static string PublicGamePage(int open, int total, bool requiresInvite) =>
		open == 0
			? Engine_OwnWords.Say("REFII_MP_PUBLIC_FULL", Number(total), Number(total))
			: Engine_OwnWords.Say("REFII_MP_PUBLIC_OPEN", Number(open), Number(total)) + "\n\n" +
				Engine_OwnWords.Say(requiresInvite ? "REFII_MP_NEEDS_INVITE" : "REFII_MP_A_JOINS");

	/// <summary>The key of a mod's credit line, or null when it states neither a version nor an author.</summary>
	public static string? ModCreditKey(string? version, string? author) =>
		!string.IsNullOrEmpty(version)
			? (string.IsNullOrEmpty(author) ? "REFII_MP_MOD_VERSION" : "REFII_MP_MOD_VERSION_BY")
			: string.IsNullOrEmpty(author) ? null : "REFII_MP_MOD_BY";

	/// <summary>A mod's credit line in our words; the version and the author are the mod's own.</summary>
	public static string ModCredit(string? version, string? author) => ModCreditKey(version, author) switch
	{
		"REFII_MP_MOD_VERSION_BY" => Engine_OwnWords.Say("REFII_MP_MOD_VERSION_BY", version!, author!),
		"REFII_MP_MOD_VERSION" => Engine_OwnWords.Say("REFII_MP_MOD_VERSION", version!),
		"REFII_MP_MOD_BY" => Engine_OwnWords.Say("REFII_MP_MOD_BY", author!),
		_ => string.Empty,
	};

	/// <summary>A mod's page: the author's description (their own words, as given), then the credit line.</summary>
	public static string ModPage(string? described, string? version, string? author)
	{
		string credit = ModCredit(version, author);
		return string.IsNullOrEmpty(described)
			? credit
			: credit.Length == 0 ? described : $"{described}\n\n{credit}";
	}

	private static string Number(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
