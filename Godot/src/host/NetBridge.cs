using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Refii.Host;

/// <summary>
/// RexNet as the shell sees it: who we are, who our friends are, and what is joinable.
/// </summary>
/// <remarks>
/// OVER THE LIVE NODE. The native side reads <c>RexNet::shared()</c> — the instance the guest
/// runtime already stands up — rather than the <c>rexnet_*</c> FFI, which would create a second
/// libp2p node with its own identity. See the RexNet section of <c>host_bridge.cpp</c>.
///
/// Every reader returns LF-separated records of TAB-separated fields. A malformed record is
/// SKIPPED rather than throwing: the fields are peer-supplied and this is a menu, so one bad
/// row must not take the screen down with it.
///
/// Nothing here is cached. The node's own snapshots are the model, and a menu that redrew from
/// a host-side copy would show a friend as online after they had gone.
/// </remarks>
public static class NetBridge
{
	private const string Library = "refii_hostbridge";

	/// <summary>Enough for a few hundred friends; a longer answer is truncated to nothing.</summary>
	private const int ReadBuffer = 64 * 1024;

	/// <summary>What <see cref="PeerAction"/> does, as the native switch numbers it.</summary>
	public enum Act
	{
		AcceptRequest = 0,
		DismissRequest = 1,
		RemoveFriend = 2,
		AcceptInvite = 3,
		DeclineInvite = 4,
		Invite = 5,

		/// <summary>
		/// Join this peer's advertised session, uninvited.
		/// </summary>
		/// <remarks>
		/// Raises the accept for ourselves off the peer's presence — the host peer and the
		/// 16-byte session id are the same material an invite carries — and starts the search
		/// whose result resolves it. The guest's own invite pump does the rest.
		/// </remarks>
		Join = 6,
	}

	/// <summary>Our own identity, or null when RexNet is off or not up yet.</summary>
	public readonly record struct Identity(string DisplayName, string PeerId, string FriendCode);

	public readonly record struct Friend(
		string PeerId,
		string DisplayName,
		bool Online,
		uint TitleId,
		string TitleName,
		bool HasSession,
		string SessionId,
		int PresenceState,
		string Status = "",
		uint Signal = 0u)
	{
		/// <summary>Joinable means they are hosting AND we can reach them.</summary>
		public bool Joinable => Online && HasSession;
	}

	public readonly record struct Request(string PeerId, string DisplayName, string Note);

	public readonly record struct Invite(string PeerId, uint TitleId, string SessionId);

	public readonly record struct Session(
		string HostPeerId, string SessionId, int SlotsTotal, int SlotsOpen, bool RequiresInvite);

	/// <summary>
	/// Whether we are hosting a session, and whether we will admit a joiner.
	/// </summary>
	/// <remarks>
	/// Hosting is the session machinery's to decide; only <see cref="SetJoinable"/> is ours.
	/// Null when the guest is not up — both bytes live in guest memory.
	/// </remarks>
	public static (bool Hosting, bool Joinable)? HostState()
	{
		foreach (string[] f in Records(RefiiNetHostState, 2))
		{
			return (f[0] == "1", f[1] == "1");
		}

		return null;
	}

	/// <summary>Opens or closes our session to joiners.</summary>
	/// <summary>
	/// States this player's status to everyone who sees their presence (Fable 3's orb status:
	/// looking for co-op, trading…); empty clears it.
	/// </summary>
	public static bool SetPlayerStatus(string status)
	{
		try
		{
			return RefiiNetSetPlayerStatus(status) != 0;
		}
		catch (DllNotFoundException)
		{
			return false;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	/// <summary>Sends a greeting (id 1..255) to everyone who sees this player's presence (Fable 3's orb emotes).</summary>
	public static bool SendPlayerSignal(int id)
	{
		try
		{
			return RefiiNetSendPlayerSignal(id) != 0;
		}
		catch (DllNotFoundException)
		{
			return false;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	public static bool SetJoinable(bool on)
	{
		try
		{
			return RefiiNetSetJoinable(on ? 1 : 0) != 0;
		}
		catch (DllNotFoundException)
		{
			return false;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	/// <summary>True when the node answered — the one test for "is RexNet available".</summary>
	public static bool Available => Self() is not null;

	public static Identity? Self()
	{
		foreach (string[] f in Records(RefiiNetIdentity, 3))
		{
			return new Identity(f[0], f[1], f[2]);
		}

		return null;
	}

	/// <summary>
	/// The friends list. Polled every frame for greetings, so the answer is parsed only when the
	/// bridge's bytes differ from the last call's; an unchanged answer returns the same list, which
	/// callers read and never alter.
	/// </summary>
	public static List<Friend> Friends()
	{
		lock (ReadLock)
		{
			int written = Read(RefiiNetFriends);
			if (written < 0)
			{
				return _friends;
			}

			if (written == _friendsBytes.Length && _readBuffer.AsSpan(0, written).SequenceEqual(_friendsBytes))
			{
				return _friends;
			}

			_friendsBytes = _readBuffer.AsSpan(0, written).ToArray();
			var list = new List<Friend>();
			foreach (string[] f in Split(written, 10))
			{
				list.Add(new Friend(
					f[0], f[1], f[2] == "1", Num(f[3]), f[4], f[5] == "1", f[6], (int)Num(f[7]), f[8], Num(f[9])));
			}

			_friends = list;
			return list;
		}
	}

	private static List<Friend> _friends = new();
	private static byte[] _friendsBytes = System.Array.Empty<byte>();

	/// <summary>This node: its peer id, the shard it is in (empty outside one), its virtual address and the title.</summary>
	public readonly record struct NetworkSelf(string PeerId, string ShardId, uint VirtualIp, uint TitleId);

	/// <summary>A member of our shard. State: 0 offline, 1 online, 2 in game, 3 joinable.</summary>
	public readonly record struct ShardMember(
		string PeerId, string Name, bool IsFriend, int State, bool HasSession, string SessionId);

	/// <summary>A peer holding a virtual address. Status: 0 idle, 1 pending, 2 connected, 3 lost.</summary>
	public readonly record struct Link(
		string PeerId,
		string Name,
		uint VirtualIp,
		int Status,
		bool Degraded,
		bool ControlLinked,
		bool IsFriend,
		bool IsRelay,
		uint RttMs);

	/// <summary>The node's view of the network.</summary>
	public sealed record NetworkView(NetworkSelf Self, List<ShardMember> Shard, List<Link> Links);

	/// <summary>The node's own view of RexNet, or null with no node up.</summary>
	public static NetworkView? Network()
	{
		string text;
		lock (ReadLock)
		{
			int written = Read(RefiiNetNetwork);
			if (written <= 0)
			{
				return null;
			}

			text = Encoding.UTF8.GetString(_readBuffer, 0, written);
		}

		NetworkSelf? self = null;
		var shard = new List<ShardMember>();
		var links = new List<Link>();
		foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			string[] f = line.Split('\t');
			switch (f[0])
			{
				case "self" when f.Length == 5:
					self = new NetworkSelf(f[1], f[2], Num(f[3]), Num(f[4]));
					break;
				case "shard" when f.Length == 7:
					shard.Add(new ShardMember(f[1], f[2], f[3] == "1", (int)Num(f[4]), f[5] == "1", f[6]));
					break;
				case "link" when f.Length == 10:
					links.Add(new Link(
						f[1], f[2], Num(f[3]), (int)Num(f[4]), f[5] == "1", f[6] == "1", f[7] == "1", f[8] == "1", Num(f[9])));
					break;
			}
		}

		return self is { } own ? new NetworkView(own, shard, links) : null;
	}

	public static List<Request> Requests()
	{
		var list = new List<Request>();
		foreach (string[] f in Records(RefiiNetRequests, 3))
		{
			list.Add(new Request(f[0], f[1], f[2]));
		}

		return list;
	}

	/// <summary>Peers a request we sent reached (their ack), since the last call. Drained.</summary>
	public static List<string> Delivered()
	{
		var list = new List<string>();
		foreach (string[] f in Records(RefiiNetDelivered, 1))
		{
			list.Add(f[0]);
		}

		return list;
	}

	public readonly record struct Notice(int Kind, string PeerId, string Message)
	{
		public const int Error = 0;
		public const int InviteFailed = 1;
		public const int JoinTimedOut = 2;
	}

	/// <summary>
	/// The node's own word since the last call: an invite it could not deliver, a join the host
	/// never answered, an error. Drained.
	/// </summary>
	public static List<Notice> Notices()
	{
		var list = new List<Notice>();
		foreach (string[] f in Records(RefiiNetNotices, 3))
		{
			list.Add(new Notice(int.TryParse(f[0], out int kind) ? kind : 0, f[1], f[2]));
		}

		return list;
	}

	/// <summary>Peers that dismissed a request we sent, since the last call. Drained.</summary>
	public static List<string> Declined()
	{
		var list = new List<string>();
		foreach (string[] f in Records(RefiiNetDeclined, 1))
		{
			list.Add(f[0]);
		}

		return list;
	}

	public static List<Invite> Invites()
	{
		var list = new List<Invite>();
		foreach (string[] f in Records(RefiiNetInvites, 3))
		{
			list.Add(new Invite(f[0], Num(f[1]), f[2]));
		}

		return list;
	}

	/// <summary>
	/// Drains the accumulated session search.
	/// </summary>
	/// <remarks>
	/// DRAINING: the native call empties the queue, so the caller keeps what it reads and a
	/// second call returns nothing. <see cref="SearchSessions"/> starts a fresh sweep.
	/// </remarks>
	public static List<Session> TakeSessions()
	{
		var list = new List<Session>();
		foreach (string[] f in Records(RefiiNetSessionResults, 5))
		{
			list.Add(new Session(f[0], f[1], (int)Num(f[2]), (int)Num(f[3]), f[4] == "1"));
		}

		return list;
	}

	public static bool SearchSessions() => Call(RefiiNetSessionSearch);

	/// <summary>
	/// Who a <c>REXN-…</c> code names, without acting on it, or null when it is not a code.
	/// </summary>
	/// <remarks>
	/// A pure parse on the native side, so it answers with the node down. The name is the one
	/// the code's owner asserted when they generated it, and it may be empty.
	/// </remarks>
	public static (string Name, string PeerId)? PreviewCode(string code)
	{
		if (string.IsNullOrWhiteSpace(code))
		{
			return null;
		}

		var buffer = new byte[1024];
		uint written;
		try
		{
			written = RefiiNetPreviewCode(code, buffer, (uint)buffer.Length);
		}
		catch (DllNotFoundException)
		{
			return null;
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}

		if (written == 0 || written > buffer.Length)
		{
			return null;
		}

		string[] parts = Encoding.UTF8.GetString(buffer, 0, (int)written)
			.TrimEnd('\n').Split('\t');
		return parts.Length == 2 ? (parts[0], parts[1]) : null;
	}

	/// <summary>
	/// The name to show for a peer, by RexNet's rule (§17.3.7–§17.3.8): a friend's own display
	/// name, anyone else's pseudonym ("Curious Cat") — never a name a non-friend asserted. Null
	/// when the id is not one or the node is absent.
	/// </summary>
	public static string? PeerName(string peerId) =>
		string.IsNullOrEmpty(peerId) ? null : ReadName(buffer => RefiiNetPeerName(peerId, buffer, (uint)buffer.Length));

	/// <summary>The online co-op partner's name to show, by the same rule; null when none is known.</summary>
	public static string? PartnerName() => ReadName(buffer => RefiiNetPartnerName(buffer, (uint)buffer.Length));

	/// <summary>
	/// States this player's achievements (<see cref="Refii.Achievements.Engine_AchievementWire"/>) to everyone
	/// who sees their presence — whoever views their orb or plays with them; empty clears them.
	/// </summary>
	public static bool SetPlayerAchievements(byte[] wire)
	{
		try
		{
			return RefiiNetSetPlayerAchievements(wire, (uint)wire.Length) != 0;
		}
		catch (DllNotFoundException)
		{
			return false;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	/// <summary>
	/// The achievements a player's machine stated (<see cref="SetPlayerAchievements"/> on theirs), as sent;
	/// <paramref name="peerId"/> empty names the online co-op partner. Null when none was heard.
	/// </summary>
	public static byte[]? PeerAchievements(string peerId)
	{
		string? hex = ReadName(buffer => RefiiNetPeerAchievements(peerId ?? string.Empty, buffer, (uint)buffer.Length));
		if (hex is null || hex.Length == 0 || hex.Length % 2 != 0)
		{
			return null;
		}

		try
		{
			return Convert.FromHexString(hex);
		}
		catch (FormatException)
		{
			return null;
		}
	}

	private static string? ReadName(Func<byte[], uint> read)
	{
		var buffer = new byte[256];
		uint written;
		try
		{
			written = read(buffer);
		}
		catch (DllNotFoundException)
		{
			return null;
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}

		if (written == 0 || written > buffer.Length)
		{
			return null;
		}

		string name = Encoding.UTF8.GetString(buffer, 0, (int)written).TrimEnd('\n');
		return name.Length == 0 ? null : name;
	}

	/// <summary>Outcome of <see cref="AddFriendByCode"/>: 0 failed, 1 sent, 2 that was our own code.</summary>
	public static int AddFriendByCodeResult(string code, string note)
	{
		try
		{
			return RefiiNetAddFriendByCode(code ?? string.Empty, note ?? string.Empty);
		}
		catch (DllNotFoundException)
		{
			return 0;
		}
		catch (EntryPointNotFoundException)
		{
			return 0;
		}
	}

	/// <summary>Sends a friend request to a <c>REXN-…</c> code.</summary>
	public static bool AddFriendByCode(string code, string note)
	{
		try
		{
			return RefiiNetAddFriendByCode(code ?? string.Empty, note ?? string.Empty) != 0;
		}
		catch (DllNotFoundException)
		{
			return false;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	/// <summary>Our session's id as 32 hex digits, or null when we are not hosting one.</summary>
	public static string? OwnSessionId()
	{
		var buffer = new byte[64];
		uint written;
		try
		{
			written = RefiiNetOwnSession(buffer, (uint)buffer.Length);
		}
		catch (DllNotFoundException)
		{
			return null;
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}

		return written == 0 || written > buffer.Length
			? null
			: Encoding.UTF8.GetString(buffer, 0, (int)written);
	}

	/// <summary>
	/// States what Discord shows for us; the node owns the Discord connection, the party and
	/// the join secret. <paramref name="joinable"/> null means "whenever a session exists".
	/// </summary>
	public static void DiscordActivity(string details, string state, bool? joinable)
	{
		try
		{
			RefiiNetDiscordActivity(details ?? string.Empty, state ?? string.Empty,
				joinable is null ? -1 : joinable.Value ? 1 : 0);
		}
		catch (DllNotFoundException)
		{
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	/// <summary>Rich Presence on or off, the player's setting.</summary>
	public static void DiscordEnabled(bool enabled)
	{
		try
		{
			RefiiNetDiscordEnabled(enabled ? 1 : 0);
		}
		catch (DllNotFoundException)
		{
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	/// <summary>
	/// Joins a session from its material — the host's peer id, the title id and the session
	/// id as <see cref="OwnSessionId"/> gives it — without the host being a friend. What a
	/// Discord join secret carries.
	/// </summary>
	public static bool JoinSession(string peerId, uint titleId, string sessionId)
	{
		try
		{
			return RefiiNetJoinSession(peerId ?? string.Empty, titleId, sessionId ?? string.Empty) != 0;
		}
		catch (DllNotFoundException)
		{
			return false;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	/// <summary>A Discord "ask to join" the node is holding for an answer.</summary>
	public readonly record struct JoinRequest(string UserId, string UserName);

	/// <summary>
	/// The join requests that arrived since the last call. Drained: the node hands each one
	/// over once, so the caller keeps what it reads until it answers with
	/// <see cref="JoinRequestReply"/>.
	/// </summary>
	public static List<JoinRequest> JoinRequests()
	{
		var list = new List<JoinRequest>();
		foreach (string[] f in Records(RefiiNetJoinRequests, 2))
		{
			list.Add(new JoinRequest(f[0], f[1]));
		}

		return list;
	}

	/// <summary>Answers one join request by the Discord user id it named.</summary>
	public static bool JoinRequestReply(string userId, bool accept)
	{
		if (string.IsNullOrEmpty(userId))
		{
			return false;
		}

		try
		{
			return RefiiNetJoinRequestReply(userId, accept ? 1 : 0) != 0;
		}
		catch (DllNotFoundException)
		{
			return false;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	/// <summary>
	/// The player an orb's Interact asked the guide's gamer card for, as the node resolved them.
	/// </summary>
	/// <param name="Request">Counts the asks this run; act on one larger than the last answered.</param>
	/// <param name="PeerId">Empty when the XUID matched no player the node knows.</param>
	/// <param name="Name">RexNet's name for them: a friend's own, anyone else's pseudonym.</param>
	/// <param name="Hosting">They advertise a session we could join.</param>
	/// <param name="Partner">They are already this game's co-op partner.</param>
	/// <param name="Xuid">The XUID the guest asked about, in hex — for the log.</param>
	public readonly record struct GamerCard(
		uint Request, string PeerId, string Name, bool Friend, bool Hosting, bool Partner, string Xuid = "")
	{
		public bool Known => PeerId.Length > 0;
	}

	/// <summary>The latest gamer card the guest asked for, or null when it has asked for none.</summary>
	public static GamerCard? LatestGamerCard()
	{
		foreach (string[] f in Records(RefiiNetGamerCard, 5))
		{
			uint flags = Num(f[3]);
			return new GamerCard(Num(f[0]), f[1], f[2], (flags & 1u) != 0, (flags & 2u) != 0,
				(flags & 4u) != 0, f[4]);
		}

		return null;
	}

	public static bool PeerAction(string peerId, Act act)
	{
		try
		{
			return RefiiNetPeerAction(peerId ?? string.Empty, (int)act) != 0;
		}
		catch (DllNotFoundException)
		{
			return false;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	private static uint Num(string text) => uint.TryParse(text, out uint value) ? value : 0u;

	private static bool Call(Func<int> entry)
	{
		try
		{
			return entry() != 0;
		}
		catch (DllNotFoundException)
		{
			return false;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	/// <summary>
	/// Splits one reader's answer into records of exactly <paramref name="fields"/> fields.
	/// </summary>
	/// <remarks>
	/// A record with the wrong count is dropped. The bridge is a different binary with its own
	/// build, so a field added on one side and not the other is a real state, and dropping the
	/// row keeps the menu standing while it is fixed.
	/// </remarks>
	private static IEnumerable<string[]> Records(Reader entry, int fields)
	{
		// Parsed under the lock, since the readers share one buffer: several are drained once a
		// frame, and a 64 KB array per call was the game's largest steady allocation.
		lock (ReadLock)
		{
			int written = Read(entry);
			return written <= 0 ? System.Array.Empty<string[]>() : Split(written, fields);
		}
	}

	private static readonly object ReadLock = new();
	private static readonly byte[] _readBuffer = new byte[ReadBuffer];

	/// <summary>Fills <see cref="_readBuffer"/> from <paramref name="entry"/>: the byte count, 0 for nothing, -1 for no bridge.</summary>
	private static int Read(Reader entry)
	{
		uint written;
		try
		{
			written = entry(_readBuffer, (uint)_readBuffer.Length);
		}
		catch (DllNotFoundException)
		{
			return -1;
		}
		catch (EntryPointNotFoundException)
		{
			// An older bridge with no RexNet exports. Not a fault: the screen says so.
			return -1;
		}

		return written > _readBuffer.Length ? 0 : (int)written;
	}

	/// <summary>The buffer's first <paramref name="written"/> bytes as records of exactly <paramref name="fields"/> fields.</summary>
	private static List<string[]> Split(int written, int fields)
	{
		var records = new List<string[]>();
		string text = Encoding.UTF8.GetString(_readBuffer, 0, written);
		foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			string[] parts = line.Split('\t');
			if (parts.Length == fields)
			{
				records.Add(parts);
			}
		}

		return records;
	}

	private delegate uint Reader(byte[] outText, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiNetIdentity(byte[] outText, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiNetFriends(byte[] outText, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiNetNetwork(byte[] outText, uint capacity);

	[DllImport(Library)]
	private static extern int RefiiNetSetPlayerStatus([MarshalAs(UnmanagedType.LPUTF8Str)] string status);

	[DllImport(Library)]
	private static extern int RefiiNetSendPlayerSignal(int id);

	[DllImport(Library)]
	private static extern uint RefiiNetRequests(byte[] outText, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiNetDeclined(byte[] outText, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiNetNotices(byte[] outText, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiNetDelivered(byte[] outText, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiNetInvites(byte[] outText, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiNetSessionResults(byte[] outText, uint capacity);

	[DllImport(Library)]
	private static extern int RefiiNetSessionSearch();

	[DllImport(Library)]
	private static extern uint RefiiNetHostState(byte[] outText, uint capacity);

	[DllImport(Library)]
	private static extern int RefiiNetSetJoinable(int on);

	[DllImport(Library)]
	private static extern uint RefiiNetPeerName([MarshalAs(UnmanagedType.LPUTF8Str)] string peerId, byte[] outText, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiNetPartnerName(byte[] outText, uint capacity);

	[DllImport(Library)]
	private static extern int RefiiNetSetPlayerAchievements(byte[] wire, uint length);

	[DllImport(Library)]
	private static extern uint RefiiNetPeerAchievements([MarshalAs(UnmanagedType.LPUTF8Str)] string peerId, byte[] outText, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiNetPreviewCode([MarshalAs(UnmanagedType.LPUTF8Str)] string code, byte[] outText, uint capacity);

	[DllImport(Library)]
	private static extern int RefiiNetAddFriendByCode([MarshalAs(UnmanagedType.LPUTF8Str)] string code, [MarshalAs(UnmanagedType.LPUTF8Str)] string note);

	[DllImport(Library)]
	private static extern int RefiiNetPeerAction([MarshalAs(UnmanagedType.LPUTF8Str)] string peerId, int action);

	[DllImport(Library)]
	private static extern uint RefiiNetOwnSession(byte[] outText, uint capacity);

	[DllImport(Library)]
	private static extern int RefiiNetJoinSession([MarshalAs(UnmanagedType.LPUTF8Str)] string peerId, uint titleId, [MarshalAs(UnmanagedType.LPUTF8Str)] string sessionId);

	[DllImport(Library)]
	private static extern void RefiiNetDiscordActivity([MarshalAs(UnmanagedType.LPUTF8Str)] string details, [MarshalAs(UnmanagedType.LPUTF8Str)] string state, int joinable);

	[DllImport(Library)]
	private static extern void RefiiNetDiscordEnabled(int enabled);

	[DllImport(Library)]
	private static extern uint RefiiNetGamerCard(byte[] outText, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiNetJoinRequests(byte[] outText, uint capacity);

	[DllImport(Library)]
	private static extern int RefiiNetJoinRequestReply([MarshalAs(UnmanagedType.LPUTF8Str)] string userId, int accept);
}
