using System.Collections.Generic;
using Refii.Host;

namespace Refii.Ui;

/// <summary>
/// The Discord "ask to join" requests the node has handed over and nobody has answered yet — ONE
/// store, read by both places that answer them: the Multiplayer page's rows
/// (<see cref="Engine_UiNetworkSource"/>) and the in-world question (<c>GameUi</c>).
/// </summary>
/// <remarks>
/// <see cref="NetBridge.JoinRequests"/> DRAINS the node's queue, so whoever reads it first owns
/// what it returned. Both readers go through <see cref="Refresh"/>, which keeps every request
/// here until it is answered (<see cref="Answer"/>, from either place) or goes stale, so a request
/// read by one is never lost to the other and an answered one leaves both at once.
/// </remarks>
public static class Engine_UiJoinRequests
{
	/// <summary>Who can join this player's game — Fable 3's join options.</summary>
	public enum JoinMode
	{
		/// <summary>Anyone they are friends with, uninvited (the game is joinable).</summary>
		Anyone,

		/// <summary>Only someone they invite (the game is not joinable; asks still come in).</summary>
		InvitesOnly,

		/// <summary>No one: not joinable, and asks to join are declined as they arrive.</summary>
		NoOne,
	}

	private const string SettingsPath = "user://coop_join.cfg";

	private static JoinMode? _mode;

	/// <summary>The player's join option, kept for them across runs.</summary>
	public static JoinMode Mode
	{
		get
		{
			if (_mode is null)
			{
				var file = new Godot.ConfigFile();
				_mode = file.Load(SettingsPath) == Godot.Error.Ok
					? (JoinMode)System.Math.Clamp(file.GetValue("join", "mode", 0).AsInt32(), 0, 2)
					: JoinMode.Anyone;
			}

			return _mode.Value;
		}
		set
		{
			_mode = value;
			var file = new Godot.ConfigFile();
			file.Load(SettingsPath);
			file.SetValue("join", "mode", (int)value);
			file.Save(SettingsPath);
		}
	}

	/// <summary>
	/// The statuses a player can state (Fable 3's orb status) AS SENT to RexNet: the wire text, English on
	/// every build. Drawn through <see cref="Engine_UiNetworkWords.Status"/> (own row) and
	/// <see cref="Engine_UiNetworkWords.DrawnStatus"/> (a friend's), never as written here. The greetings
	/// (Fable 3's orb emotes, signal id = index + 1) are <see cref="Engine_UiNetworkWords.GreetingCount"/>.
	/// </summary>
	public static readonly string[] Statuses = Engine_UiNetworkWords.WireStatuses;

	private static int? _status;

	/// <summary>The player's stated status, kept for them across runs; stated to RexNet when set.</summary>
	public static int StatusIndex
	{
		get
		{
			if (_status is null)
			{
				var file = new Godot.ConfigFile();
				_status = file.Load(SettingsPath) == Godot.Error.Ok
					? System.Math.Clamp(file.GetValue("join", "status", 0).AsInt32(), 0, Statuses.Length - 1)
					: 0;
			}

			return _status.Value;
		}
		set
		{
			_status = System.Math.Clamp(value, 0, Statuses.Length - 1);
			var file = new Godot.ConfigFile();
			file.Load(SettingsPath);
			file.SetValue("join", "status", _status.Value);
			file.Save(SettingsPath);
			NetBridge.SetPlayerStatus(Statuses[_status.Value]);
		}
	}

	/// <summary>Each friend's last signal seen, so a new one is told once.</summary>
	private static readonly Dictionary<string, uint> _signalsSeen = new();

	/// <summary>
	/// The greetings friends sent since the last call, as the line to tell: a friend's signal
	/// whose sequence moved. The first read of each friend only learns where they are.
	/// </summary>
	public static List<string> TakeGreetings(IEnumerable<NetBridge.Friend> friends)
	{
		var heard = new List<string>();
		foreach (NetBridge.Friend friend in friends)
		{
			bool known = _signalsSeen.TryGetValue(friend.PeerId, out uint seen);
			_signalsSeen[friend.PeerId] = friend.Signal;
			if (!known || friend.Signal == seen || friend.Signal == 0u)
			{
				continue;
			}

			int id = (int)(friend.Signal & 0xFFu);
			if (id >= 1 && id <= Engine_UiNetworkWords.GreetingCount)
			{
				heard.Add(Engine_UiNetworkWords.Heard(id - 1, friend.DisplayName));
			}
		}

		return heard;
	}

	/// <summary>How long an unanswered ask stays listed; Discord's own ask lapses sooner.</summary>
	public const ulong LifetimeMs = 60_000;

	private static readonly List<(NetBridge.JoinRequest Request, ulong SeenMs)> _pending = new();

	/// <summary>Arrivals not yet asked in the world, oldest first.</summary>
	private static readonly List<NetBridge.JoinRequest> _arrived = new();

	/// <summary>The display name an ask is shown under; Discord can hand over an empty one.</summary>
	public static string Who(NetBridge.JoinRequest ask) => Engine_UiNetworkWords.DiscordName(ask.UserName);

	/// <summary>
	/// Takes what the node handed over since the last read and drops what went stale. A repeat
	/// ask from the same user replaces the earlier one and counts as a new arrival.
	/// </summary>
	public static void Refresh(ulong nowMs)
	{
		foreach (NetBridge.JoinRequest ask in NetBridge.JoinRequests())
		{
			// "No one" (Fable 3's join option Off): an ask is declined as it arrives.
			if (Mode == JoinMode.NoOne)
			{
				NetBridge.JoinRequestReply(ask.UserId, false);
				continue;
			}

			_pending.RemoveAll(entry => entry.Request.UserId == ask.UserId);
			_pending.Add((ask, nowMs));
			_arrived.RemoveAll(entry => entry.UserId == ask.UserId);
			_arrived.Add(ask);
		}

		_pending.RemoveAll(entry => nowMs - entry.SeenMs > LifetimeMs);
		_arrived.RemoveAll(ask => !IsPending(ask.UserId));
	}

	/// <summary>Every unanswered ask, oldest first.</summary>
	public static IEnumerable<NetBridge.JoinRequest> Pending()
	{
		foreach ((NetBridge.JoinRequest ask, ulong _) in _pending)
		{
			yield return ask;
		}
	}

	public static bool IsPending(string userId) =>
		_pending.Exists(entry => entry.Request.UserId == userId);

	/// <summary>The oldest ask not yet raised in the world, taken; null when there is none.</summary>
	public static NetBridge.JoinRequest? TakeArrival()
	{
		if (_arrived.Count == 0)
		{
			return null;
		}

		NetBridge.JoinRequest ask = _arrived[0];
		_arrived.RemoveAt(0);
		return ask;
	}

	/// <summary>
	/// Answers one ask through the node and forgets it everywhere. False when the node refused —
	/// the ask may have lapsed on Discord's side; it is forgotten either way.
	/// </summary>
	public static bool Answer(string userId, bool accept)
	{
		_pending.RemoveAll(entry => entry.Request.UserId == userId);
		_arrived.RemoveAll(entry => entry.UserId == userId);
		return NetBridge.JoinRequestReply(userId, accept);
	}
}
