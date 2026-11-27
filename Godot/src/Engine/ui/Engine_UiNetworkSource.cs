using System.Collections.Generic;
using Refii.Host;
using Refii.Mods;

namespace Refii.Ui;

/// <summary>
/// The rows behind the shell's own content screen: the mods that are on, and RexNet's
/// friends, requests, invites and joinable games.
/// </summary>
/// <remarks>
/// A DATA-AREA SOURCE, like <see cref="Engine_UiInventorySource"/>: nothing here watches the
/// guest build a menu, because the guest has no screen for any of it. The mod rows come from the
/// load order on disk and the network rows from <see cref="NetBridge"/>, which reads the live
/// node — so a row is current at the moment it is asked for and no state is kept here.
///
/// Every row is <c>Committable: false</c> and carries its own <see cref="Command"/>: these are
/// invented rows with no guest identity to commit against, and the action belongs to the row
/// rather than to a template entry because the peer or mod it names IS the argument.
///
/// A row is GHOSTED rather than dropped when it cannot be acted on — an offline friend, a mod
/// whose folder is missing. Dropping it would make the list change shape as people come and go,
/// which moves the cursor under the player's hand.
/// </remarks>
public static class Engine_UiNetworkSource
{
	public const string ModsSource = "mods";
	public const string FriendsSource = "friends";
	public const string RequestsSource = "requests";

	public const string InvitesSource = "invites";
	public const string GamesSource = "games";

	/// <summary>Every subject in one list, for the multiplayer book.</summary>
	public const string AllSource = "multiplayer";

	/// <summary>The hosting toggle, as its own source so its label tracks the state.</summary>
	public const string HostingSource = "hosting";

	/// <summary>
	/// The four subjects that get a page of their own, in list order: the row's name (its identity,
	/// never shown: <see cref="IsGroupName"/>) and the key its label is drawn from (<see cref="Refii.Text.Engine_OwnWords"/>).
	/// </summary>
	public static readonly (string Source, string Title, string Tag)[] Pages =
	{
		(FriendsSource, "Friends", "REFII_MP_FRIENDS"),
		(RequestsSource, "Friend Requests", "REFII_MP_FRIEND_REQUESTS"),
		(InvitesSource, "Invitations", "REFII_MP_INVITATIONS"),
		(GamesSource, "Games", "REFII_MP_GAMES"),
	};

	private const string FriendsGroup = "Friends";
	private const string RequestsGroup = "Friend Requests";
	private const string InvitesGroup = "Invitations";
	private const string GamesGroup = "Games";

	/// <summary>Is this the name of one of the book's group headers?</summary>
	public static bool IsGroupName(string name) =>
		name is FriendsGroup or RequestsGroup or InvitesGroup or GamesGroup;

	/// <summary>Command prefix a row dispatches with; the suffix is the verb and its argument.</summary>
	public const string CommandPrefix = "Net:";

	/// <summary>Where the player's mods live, as the launcher's panel writes them.</summary>
	public static string ModsRoot { get; set; } = string.Empty;

	/// <summary>
	/// How long a produced list stands before it is read again.
	/// </summary>
	/// <remarks>
	/// THE FRONT END RE-SOURCES EVERY FRAME. `RefreshSources` compares the finished set and does
	/// nothing when it has not moved, but it has to PRODUCE the set to compare it — and
	/// producing these means a TOML read off disk and a P/Invoke into the node. At 60 fps that
	/// is a file open per frame for a list that changes when a player alt-tabs to the launcher.
	/// A quarter second is under the eye's threshold for a friend coming online and is 15 reads
	/// a second rather than 60.
	/// </remarks>
	private const ulong FreshMs = 250;

	private static readonly Dictionary<string, (ulong At, IReadOnlyList<Engine_UiMenuSources.Row> Rows)>
		Cached = new();

	/// <summary>
	/// Produces a list at most once per <see cref="FreshMs"/>.
	/// </summary>
	/// <remarks>
	/// The SAME list instance is handed back within the window, which is what keeps
	/// `RefreshSources` seeing "unchanged" rather than an equal-but-new set every frame.
	/// </remarks>
	private static IReadOnlyList<Engine_UiMenuSources.Row> Fresh(
		string key, System.Func<IReadOnlyList<Engine_UiMenuSources.Row>> produce)
	{
		ulong now = Godot.Time.GetTicksMsec();
		if (Cached.TryGetValue(key, out var held) && now - held.At < FreshMs)
		{
			return held.Rows;
		}

		IReadOnlyList<Engine_UiMenuSources.Row> rows = produce();
		Cached[key] = (now, rows);
		return rows;
	}

	/// <summary>Drops every held list, so the next ask re-reads.</summary>
	public static void Invalidate()
	{
		Cached.Clear();
		_friendsAt = 0;
	}

	/// <summary>
	/// The node's friends, read at most once per <see cref="FreshMs"/>: the friend rows, the
	/// games rows and every row's detail page share one read rather than each taking a
	/// P/Invoke of their own.
	/// </summary>
	private static List<NetBridge.Friend> FriendList()
	{
		ulong now = Godot.Time.GetTicksMsec();
		if (_friendsAt != 0 && now - _friendsAt < FreshMs)
		{
			return _friends;
		}

		_friends = NetBridge.Friends();
		_friendsAt = now;
		return _friends;
	}

	private static List<NetBridge.Friend> _friends = new();

	private static ulong _friendsAt;

	private static Engine_UiMenuSources.Row Row(
		string name, string label, string command, bool ghosted = false) =>
		new(
			Name: name,
			Label: label,
			Icon: string.Empty,
			EventType: 0,
			Id: 0u,
			Rating: Engine_UiMenuConstants.EmptyRatingSlide,
			Committable: false,
			Ghosted: ghosted,
			Command: command);

	/// <summary>The enabled mods, in load order — the order that decides who shadows whom.</summary>
	public static IReadOnlyList<Engine_UiMenuSources.Row> Mods(string argument) =>
		Fresh("Mods", BuildMods);

	private static IReadOnlyList<Engine_UiMenuSources.Row> BuildMods()
	{
		var rows = new List<Engine_UiMenuSources.Row>();
		if (ModsRoot.Length == 0)
		{
			Placeholder(rows, "mods", Words("REFII_MP_MODS_NO_FOLDER"));
			return rows;
		}

		ModLoadOrder order;
		try
		{
			order = ModLoadOrder.Read(ModsRoot);
		}
		catch (System.Exception)
		{
			// The panel writes this file; a half-written one is not worth a dead screen.
			Placeholder(rows, "mods", Words("REFII_MP_MODS_UNREADABLE"));
			return rows;
		}

		Dictionary<string, ModMetadata> installed = Installed();
		foreach (string mod in order.Enabled)
		{
			// The mod's own title, in load order; a mod whose package is gone is ghosted.
			bool present = installed.TryGetValue(mod, out ModMetadata? meta);
			string title = present && meta!.Title.Length != 0 ? Refii.Mods.ModText.Game(meta.Title) : mod;
			rows.Add(Row($"{ModRowPrefix}{mod}", title, $"{CommandPrefix}mod:{mod}", ghosted: !present));
		}

		Placeholder(rows, "mods", Words(ModsRoot.Length == 0 ? "REFII_MP_MODS_NO_FOLDER" : "REFII_MP_NO_MODS"));
		return rows;
	}

	/// <summary>The row-name prefix of a mod row; the rest is the mod's folder name.</summary>
	public const string ModRowPrefix = "mod:";

	/// <summary>An installed mod's own statement of itself, or null when its package is gone.</summary>
	public static ModMetadata? ModInfo(string mod) =>
		Installed().TryGetValue(mod, out ModMetadata? meta) ? meta : null;

	/// <summary>
	/// The installed packages' metadata, read again only when the mods folder changes — the
	/// list re-sources four times a second and each read opens every package.
	/// </summary>
	private static Dictionary<string, ModMetadata> Installed()
	{
		System.DateTime stamp;
		try
		{
			stamp = System.IO.Directory.GetLastWriteTimeUtc(ModsRoot);
		}
		catch (System.Exception)
		{
			stamp = default;
		}

		if (_installed is null || _installedRoot != ModsRoot || _installedStamp != stamp)
		{
			_installed = new Dictionary<string, ModMetadata>(System.StringComparer.OrdinalIgnoreCase);
			foreach (ModInstall install in ModWorkspace.DiscoverModInstalls(ModsRoot))
			{
				_installed[install.Name] = install.Metadata;
			}

			_installedRoot = ModsRoot;
			_installedStamp = stamp;
		}

		return _installed;
	}

	private static Dictionary<string, ModMetadata>? _installed;

	private static string _installedRoot = string.Empty;

	private static System.DateTime _installedStamp;

	public static IReadOnlyList<Engine_UiMenuSources.Row> Friends(string argument) =>
		Fresh("Friends", BuildFriends);

	private static IReadOnlyList<Engine_UiMenuSources.Row> BuildFriends()
	{
		var rows = new List<Engine_UiMenuSources.Row>();
		foreach (NetBridge.Friend friend in FriendList())
		{
			string name = friend.DisplayName.Length == 0 ? Named(friend.PeerId) : friend.DisplayName;
			string state = Engine_UiNetworkWords.FriendState(
				friend.Joinable, friend.Online, friend.TitleName, friend.Status);

			rows.Add(Row(
				$"friend:{friend.PeerId}",
				$"{name} — {state}",
				$"{CommandPrefix}join:{friend.PeerId}",
				ghosted: !friend.Online));
		}


		Placeholder(rows, "friends", Words("REFII_MP_NO_FRIENDS"));
		return rows;
	}

	public static IReadOnlyList<Engine_UiMenuSources.Row> Requests(string argument) =>
		Fresh("Requests", BuildRequests);

	private static IReadOnlyList<Engine_UiMenuSources.Row> BuildRequests()
	{
		// DECLINES ARRIVE HERE, on the same refresh that re-reads the requests: the node
		// drains them on read, so this is the one place that takes them, and the add row's
		// page is where they are said.
		foreach (string peer in NetBridge.Delivered())
		{
			string who = _sentNames.TryGetValue(peer, out string? name) && name.Length != 0
				? name
				: Named(peer);
			// "Sent" was the node taking it; this is the other side's acknowledgement.
			_declinedSays = Words("REFII_MP_REQUEST_REACHED", who);
		}

		foreach (NetBridge.Notice notice in NetBridge.Notices())
		{
			string who = _sentNames.TryGetValue(notice.PeerId, out string? name) && name.Length != 0
				? name
				: Named(notice.PeerId);
			_declinedSays = notice.Kind switch
			{
				NetBridge.Notice.InviteFailed => Words("REFII_MP_INVITE_UNDELIVERED", who),
				NetBridge.Notice.JoinTimedOut => Words("REFII_MP_JOIN_UNANSWERED", who),
				// The node's own message is drawn as it gave it.
				_ => notice.Message.Length == 0 ? Words("REFII_MP_NET_ERROR") : notice.Message,
			};
		}

		foreach (string peer in NetBridge.Declined())
		{
			string who = _sentNames.TryGetValue(peer, out string? name) && name.Length != 0
				? name
				: Named(peer);
			_declinedSays = Words("REFII_MP_REQUEST_DECLINED", who);
		}

		var rows = new List<Engine_UiMenuSources.Row>();

		// DISCORD "ASK TO JOIN", answered here or by the in-world question. The node queues each
		// ask for the title under its default policy (a stranger is never let in unless the
		// player says so); both places read the one store, so an ask answered in either leaves
		// both (Engine_UiJoinRequests).
		Engine_UiJoinRequests.Refresh(Godot.Time.GetTicksMsec());
		foreach (NetBridge.JoinRequest ask in Engine_UiJoinRequests.Pending())
		{
			string who = Engine_UiJoinRequests.Who(ask);
			rows.Add(Row(
				$"joinask:{ask.UserId}", Words("REFII_MP_ASKS_TO_JOIN", who),
				$"{CommandPrefix}join-allow:{ask.UserId}"));
			rows.Add(Row(
				$"joindeny:{ask.UserId}", Words("REFII_MP_DECLINE"), $"{CommandPrefix}join-deny:{ask.UserId}"));
		}

		foreach (NetBridge.Request request in NetBridge.Requests())
		{
			// A REQUEST IS THE ONE PLACE A STRANGER'S OWN NAME IS SHOWN (RexNet §17.3.8: without it
			// a request is undecidable), and it is shown as theirs, self-named, not as a verified name.
			bool selfNamed = request.DisplayName.Length != 0;
			string label = Engine_UiNetworkWords.RequestLabel(
				selfNamed ? request.DisplayName : Named(request.PeerId), selfNamed, request.Note);
			rows.Add(Row(
				$"request:{request.PeerId}", label, $"{CommandPrefix}accept:{request.PeerId}"));
			// THE OTHER ANSWER, as its own row. A request the player does not want is kept by
			// the node across runs now, so it needs a way out that is not "accept".
			rows.Add(Row(
				$"dismiss:{request.PeerId}", Words("REFII_MP_DISMISS"), $"{CommandPrefix}dismiss:{request.PeerId}"));
		}


		Placeholder(rows, "requests", Words("REFII_MP_NO_REQUESTS"));
		return rows;
	}

	public static IReadOnlyList<Engine_UiMenuSources.Row> Invites(string argument) =>
		Fresh("Invites", BuildInvites);

	private static IReadOnlyList<Engine_UiMenuSources.Row> BuildInvites()
	{
		var rows = new List<Engine_UiMenuSources.Row>();
		foreach (NetBridge.Invite invite in NetBridge.Invites())
		{
			rows.Add(Row(
				$"invite:{invite.PeerId}",
				Words("REFII_MP_INVITED_YOU", Named(invite.PeerId)),
				$"{CommandPrefix}accept-invite:{invite.PeerId}"));
		}


		Placeholder(rows, "invites", Words("REFII_MP_NO_INVITATIONS"));
		return rows;
	}

	/// <summary>
	/// Friends who are hosting, plus whatever the last DHT sweep turned up.
	/// </summary>
	/// <remarks>
	/// FRIENDS FIRST AND WITHOUT A SEARCH. A friend's session arrives on their presence record,
	/// which the node already holds, so those rows are there the moment the screen opens;
	/// `Net:search` is only needed for public sessions from strangers.
	/// </remarks>
	public static IReadOnlyList<Engine_UiMenuSources.Row> Games(string argument) =>
		Fresh("Games", BuildGames);

	private static IReadOnlyList<Engine_UiMenuSources.Row> BuildGames()
	{
		var rows = new List<Engine_UiMenuSources.Row>();

		// ONE ROW PER HOST. A friend's session also turns up in a search, and the row name is
		// the peer id, so the friend row stands and the search's copy is not added under it.
		var listed = new HashSet<string>();
		foreach (NetBridge.Friend friend in FriendList())
		{
			if (!friend.Joinable || !listed.Add(friend.PeerId))
			{
				continue;
			}

			string name = friend.DisplayName.Length == 0 ? Named(friend.PeerId) : friend.DisplayName;
			rows.Add(Row(
				$"game:{friend.PeerId}",
				Words("REFII_MP_GAME_OF", name),
				$"{CommandPrefix}join:{friend.PeerId}"));
		}

		foreach (NetBridge.Session session in Found)
		{
			if (!listed.Add(session.HostPeerId))
			{
				continue;
			}

			rows.Add(Row(
				$"game:{session.HostPeerId}",
				Engine_UiNetworkWords.PublicGameRow(Named(session.HostPeerId), session.SlotsOpen, session.SlotsTotal),
				$"{CommandPrefix}join:{session.HostPeerId}",
				ghosted: session.SlotsOpen == 0));
		}


		Placeholder(rows, "games", Words("REFII_MP_NO_GAMES"));
		return rows;
	}

	/// <summary>
	/// What the last search turned up.
	/// </summary>
	/// <remarks>
	/// HELD HERE because the native read DRAINS: `TakeSessionResults` empties the queue, so a
	/// list rebuilt straight from it would be full on one frame and empty on the next. Cleared
	/// when a new search starts.
	/// </remarks>
	private static readonly List<NetBridge.Session> Found = new();

	/// <summary>Starts a sweep and drops what the last one found.</summary>
	public static void BeginSearch()
	{
		Found.Clear();
		Invalidate();
		NetBridge.SearchSessions();
	}

	/// <summary>Drains any results that have landed. True when the list changed.</summary>
	public static bool PollSearch()
	{
		List<NetBridge.Session> landed = NetBridge.TakeSessions();
		if (landed.Count == 0)
		{
			return false;
		}

		Found.AddRange(landed);
		Invalidate();
		return true;
	}

	/// <summary>
	/// A ghosted stand-in when a list is empty.
	/// </summary>
	/// <remarks>
	/// An empty fold opens onto a single line and reads as a fold that did not open at all.
	/// NOT ghosted: a ghosted row cannot be activated (`Select` refuses one before anything is
	/// pushed), so the one row an empty section has would be the one row that does nothing. It
	/// carries `about:` instead, which explains the section and what fills it.
	/// </remarks>
	private static void Placeholder(List<Engine_UiMenuSources.Row> rows, string key, string text)
	{
		if (rows.Count == 0)
		{
			rows.Add(Row($"none:{key}", text, $"{CommandPrefix}about:{key}"));
		}
	}

	/// <summary>
	/// The hosting row, whose label IS the current state — a toggle the player reads rather
	/// than a command they have to remember the effect of.
	/// </summary>
	public static IReadOnlyList<Engine_UiMenuSources.Row> Hosting(string argument) =>
		Fresh("Hosting", BuildHosting);

	private static IReadOnlyList<Engine_UiMenuSources.Row> BuildHosting()
	{
		var rows = new List<Engine_UiMenuSources.Row>();
		if (NetBridge.HostState() is not { } state)
		{
			Placeholder(rows, "hosting", Words("REFII_MP_HOSTING_OFF"));
			return rows;
		}

		// GHOSTED WHEN NOT HOSTING, because the byte is read only while a session is up: the
		// admit test checks hosting FIRST and refuses before it ever looks at joinable.
		// FABLE 3'S JOIN OPTIONS: Anyone, Invites only, No one — A steps to the next.
		Engine_UiJoinRequests.JoinMode mode = Engine_UiJoinRequests.Mode;
		rows.Add(Row(
			"host:joinable",
			Engine_UiNetworkWords.WhoCanJoin((int)mode, state.Hosting),
			$"{CommandPrefix}joinmode:{(((int)mode + 1) % 3).ToString(System.Globalization.CultureInfo.InvariantCulture)}",
			ghosted: !state.Hosting));

		// FABLE 3'S ORB STATUS AND ORB EMOTES, over presence: what this player is here for, said to
		// everyone who sees them, and a greeting sent to all of them.
		int status = Engine_UiJoinRequests.StatusIndex;
		rows.Add(Row(
			"host:status",
			Engine_UiNetworkWords.MyStatus(status),
			$"{CommandPrefix}status:{((status + 1) % Engine_UiJoinRequests.Statuses.Length).ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
		for (int i = 0; i < Engine_UiNetworkWords.GreetingCount; ++i)
		{
			rows.Add(Row(
				$"host:greet{i + 1}",
				Engine_UiNetworkWords.SendGreeting(i),
				$"{CommandPrefix}greet:{(i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
		}

		return rows;
	}

	/// <summary>
	/// The whole book's list: every subject, each row tagged with its group so
	/// <c>Engine_UiTemplates.Expanded</c> raises a header for it.
	/// </summary>
	public static IReadOnlyList<Engine_UiMenuSources.Row> All(string argument) =>
		Fresh("All", BuildAll);

	private static IReadOnlyList<Engine_UiMenuSources.Row> BuildAll()
	{
		var rows = new List<Engine_UiMenuSources.Row>();
		Group(rows, FriendsGroup, BuildFriends());
		Group(rows, RequestsGroup, BuildRequests());
		Group(rows, InvitesGroup, BuildInvites());
		Group(rows, GamesGroup, BuildGames());
		return rows;
	}

	private static void Group(
		List<Engine_UiMenuSources.Row> into, string group,
		IReadOnlyList<Engine_UiMenuSources.Row> rows)
	{
		foreach (Engine_UiMenuSources.Row row in rows)
		{
			into.Add(row with { Group = group, GroupTag = group });
		}
	}

	/// <summary>
	/// What the page says about one row.
	/// </summary>
	/// <remarks>
	/// Re-read from the node rather than carried on the row: a row is spawned once and the
	/// answer ("online", "in a game") changes underneath it. Keyed by the row NAME, which
	/// carries the peer id, so nothing has to be stored beside the list.
	/// </remarks>
	public static string Detail(string rowName)
	{
		if (IsGroupName(rowName))
		{
			return rowName switch
			{
				FriendsGroup => Words("REFII_MP_ABOUT_FRIENDS"),
				RequestsGroup => Words("REFII_MP_ABOUT_REQUESTS"),
				InvitesGroup => Words("REFII_MP_ABOUT_INVITES"),
				_ => Words("REFII_MP_ABOUT_GAMES"),
			};
		}

		int colon = rowName.IndexOf(':');
		string kind = colon < 0 ? rowName : rowName[..colon];
		string peer = colon < 0 ? string.Empty : rowName[(colon + 1)..];

		switch (kind)
		{
			case "friend":
			case "game":
				foreach (NetBridge.Friend friend in FriendList())
				{
					if (friend.PeerId != peer)
					{
						continue;
					}

					return Engine_UiNetworkWords.FriendPage(
						friend.Joinable, friend.Online, friend.TitleName, friend.Status);
				}

				// A game row can name a stranger's session off the last search.
				foreach (NetBridge.Session session in Found)
				{
					if (session.HostPeerId == peer)
					{
						return Engine_UiNetworkWords.PublicGamePage(
							session.SlotsOpen, session.SlotsTotal, session.RequiresInvite);
					}
				}

				return Words("REFII_MP_NOT_LISTED");

			case "request":
				foreach (NetBridge.Request request in NetBridge.Requests())
				{
					if (request.PeerId == peer)
					{
						return Engine_UiNetworkWords.RequestPage(request.Note);
					}
				}

				return Words("REFII_MP_REQUEST_WITHDRAWN");

			case "dismiss":
				return Words("REFII_MP_ABOUT_DISMISS");

			case "joinask":
				return Words("REFII_MP_ABOUT_JOINASK");

			case "joindeny":
				return Words("REFII_MP_ABOUT_JOINDENY");

			case "invite":
				return Words("REFII_MP_ABOUT_INVITE");

			case "mod":
				// THE AUTHOR'S OWN WORDS, from the package's mod.toml — each `{KEY}` in them resolved
				// from the mod's text, as the row's title is (BuildMods); only the credit line is ours.
				if (ModInfo(peer) is not { } meta)
				{
					return Words("REFII_MP_MOD_MISSING");
				}

				return Engine_UiNetworkWords.ModPage(
					Refii.Mods.ModText.Game(meta.Description), meta.Version, Refii.Mods.ModText.Game(meta.Author));

			case "none":
				// THE PAGE AN EMPTY SECTION HAS: what fills it, written where the player is
				// looking. Never a box — a box draws under the book and blocks it.
				return peer switch
				{
					"mods" => Words("REFII_MP_NONE_MODS"),
					"friends" => Words(NetBridge.Available ? "REFII_MP_NONE_FRIENDS" : "REFII_MP_NONE_FRIENDS_OFF"),
					"requests" => Words("REFII_MP_NONE_REQUESTS"),
					"invites" => Words("REFII_MP_NONE_INVITES"),
					"games" => Words(NetBridge.Available ? "REFII_MP_NONE_GAMES" : "REFII_MP_NONE_GAMES_OFF"),
					_ => Words("REFII_MP_NONE_OTHER"),
				};

			default:
				return string.Empty;
		}
	}

	/// <summary>How long the page holds an outcome before going back to the offer.</summary>
	private const ulong CopiedShownMs = 4000;

	private static ulong _copiedAt;

	/// <summary>Notes that the code was just copied, so the page can say so.</summary>
	public static void MarkCopied() => _copiedAt = Godot.Time.GetTicksMsec();

	/// <summary>
	/// What the add-a-friend row's page is saying, and the code it is holding.
	/// </summary>
	/// <remarks>
	/// ON THE PAGE, NOT IN A BOX. Every outcome of a paste — an empty clipboard, something that
	/// is not a code, a name to confirm — belongs where the player is already looking. A box
	/// says the same words and costs a press to dismiss.
	///
	/// The pending code makes the confirm a SECOND PRESS on the same row rather than a modal:
	/// the page names who is about to be added, and A sends it.
	/// </remarks>
	private static string _addSays = string.Empty;

	private static ulong _addAt;

	private static string _addPending = string.Empty;

	/// <summary>The code awaiting a confirming press, or empty.</summary>
	public static string PendingAdd => _addPending;

	/// <summary>Names of the people we sent requests to, by peer id, for a decline to name.</summary>
	private static readonly Dictionary<string, string> _sentNames = new();

	/// <summary>What the add row's page says after a decline, until the next paste.</summary>
	private static string _declinedSays = string.Empty;

	/// <summary>Notes who a request went to.</summary>
	public static void RememberSent(string peerId, string name) => _sentNames[peerId] = name;

	/// <summary>States what the add row's page says now, and what it is holding.</summary>
	public static void SetAddState(string says, string pending = "")
	{
		_addSays = says;
		_addPending = pending;
		_addAt = Godot.Time.GetTicksMsec();
		_declinedSays = string.Empty;
	}

	/// <summary>What the page says about a row that is an action rather than a subject.</summary>
	public static string ActionDetail(string rowName) => rowName switch
	{
		"host:status" => Words("REFII_MP_ABOUT_STATUS"),
		_ when rowName.StartsWith("host:greet", System.StringComparison.Ordinal) => Words("REFII_MP_ABOUT_GREET"),
		"Hosting" or "host:joinable" => Words("REFII_MP_ABOUT_JOINABLE"),
		// A PENDING CONFIRM HOLDS THE PAGE until it is answered; a plain outcome fades back to
		// the offer so the row does not keep shouting about a paste from minutes ago.
		"AddFriend" => _addPending.Length != 0 || Godot.Time.GetTicksMsec() - _addAt < CopiedShownMs
			? _addSays
			: _declinedSays.Length != 0
				? _declinedSays
				: Words("REFII_MP_ADD_OFFER"),
		// THE CODE ITSELF, on the page. It exists to be handed to someone, so a page that
		// describes it without showing it is a page that does nothing; A copies it.
		"MyCode" => NetBridge.Self() is { FriendCode.Length: > 0 } self
			? $"{self.FriendCode}\n\n" + Words(Godot.Time.GetTicksMsec() - _copiedAt < CopiedShownMs
				? "REFII_MP_CODE_COPIED"
				: "REFII_MP_CODE_COPY")
			: Words("REFII_MP_CODE_NONE"),
		_ => string.Empty,
	};

	/// <summary>
	/// What the page says about the row the player last acted on.
	/// </summary>
	/// <remarks>
	/// NEVER A BOX. The front end's warning box draws UNDER the multiplayer book and blocks it,
	/// so every outcome of a press — a join under way, a search started, a request accepted, a
	/// refusal — is written onto the row's own page, where the player is already looking. It is
	/// keyed to the row it answered (<see cref="CurrentRow"/> at the time of the press) so
	/// moving the cursor shows the next row's plain page; a plain outcome fades back after
	/// <see cref="SaidShownMs"/>, and a held one (a join in flight) stays until the cursor moves.
	/// </remarks>
	private const ulong SaidShownMs = 6000;

	private static string _saysRow = string.Empty;

	private static string _says = string.Empty;

	private static ulong _saysAt;

	private static bool _saysHeld;

	/// <summary>Whether the held outcome has been on its page: only a line the player saw is let go.</summary>
	private static bool _saysShown;

	/// <summary>The row whose page is up now — the page states it every frame.</summary>
	public static string CurrentRow
	{
		get => _currentRow;
		set
		{
			// A HELD CODE BELONGS TO THE ADD ROW. It used to survive the cursor leaving: the
			// next press on AddFriend skipped the clipboard and sent the code from minutes ago.
			if (value != "AddFriend" && _addPending.Length != 0)
			{
				_addPending = string.Empty;
			}

			// A HELD OUTCOME STAYS UNTIL THE CURSOR MOVES (the remark above). It was kept for good:
			// a join's "Joining." still stood on the friend's page after that game had been played
			// and quit, since nothing but the row name ever matched it again.
			if (value != _saysRow)
			{
				ReleaseHeld();
			}

			_currentRow = value;
		}
	}

	private static string _currentRow = string.Empty;

	/// <summary>Writes an outcome onto the current row's page.</summary>
	public static void Say(string text, bool hold = false)
	{
		_saysRow = CurrentRow;
		_says = text;
		_saysAt = Godot.Time.GetTicksMsec();
		_saysHeld = hold;
		_saysShown = false;
	}

	/// <summary>
	/// Lets a held outcome go once it has been on its page: the cursor left its row, or the book
	/// was closed — by B, or by the game coming up when the join landed. A line held while no book
	/// stood (a co-op refusal during the load) waits for the player to reach its row first.
	/// </summary>
	public static void ReleaseHeld()
	{
		if (!_saysHeld || !_saysShown)
		{
			return;
		}

		_saysHeld = false;
		_saysShown = false;
		_says = string.Empty;
	}

	/// <summary>The outcome standing on this row's page, or empty.</summary>
	public static string Said(string rowName)
	{
		if (_says.Length == 0 || rowName != _saysRow ||
			(!_saysHeld && Godot.Time.GetTicksMsec() - _saysAt >= SaidShownMs))
		{
			return string.Empty;
		}

		_saysShown = true;
		return _says;
	}

	/// <summary>The tail of a peer id — enough to tell two strangers apart in a row.</summary>
	/// <summary>
	/// The name a peer is shown by (RexNet §17.3.7–§17.3.8): a friend's own name, anyone else's
	/// pseudonym, the same on every observer. The id's tail only when the node cannot say.
	/// </summary>
	private static string Named(string peerId) => NetBridge.PeerName(peerId) ?? Short(peerId);

	/// <summary>
	/// One of our own lines (<see cref="Refii.Text.Engine_OwnWords"/>; the keys are listed in
	/// <see cref="Engine_UiNetworkWords.Keys"/>). Named apart from <see cref="Say"/>, which writes an outcome.
	/// </summary>
	private static string Words(string key) => Refii.Text.Engine_OwnWords.Say(key);

	private static string Words(string key, params object[] args) => Refii.Text.Engine_OwnWords.Say(key, args);

	private static string Short(string peerId) =>
		peerId.Length <= 8 ? peerId : "…" + peerId[^8..];
}
