namespace Refii.Titles.Fable2;

using Refii.Host;

/// <summary>
/// The co-op session as ONE model: role and phase, polled once a frame from the guest's
/// authoritative network manager and never from raw bytes anywhere else. Fable 2 answers "which
/// player am I" by re-reading two bytes through a copy on the game object every time and tears
/// the peer down inline on a lost connection; this is the per-connection identity and lifecycle
/// a multiplayer engine would have given the game, supplied from the host side.
/// </summary>
public static class CoopSession
{
	public enum Phase { Solo, Hosting, PeerJoined, PeerLost }

	public enum Role { Solo, Host, Client }

	public static Phase State { get; private set; } = Phase.Solo;

	public static Role Side { get; private set; } = Role.Solo;

	/// <summary>Whether a peer is in the session right now (not merely a session flag).</summary>
	public static bool PeerStanding => State == Phase.PeerJoined;

	public static bool IsClient => Side == Role.Client;

	/// <summary>
	/// A second player on THIS machine — the guest's own `IsLocalCoopGame` (`game+284 == 2`,
	/// set by `Coop_SetSharedScreenMode` on a START from a pad that is not player one's). Both
	/// players are local then, and no peer or channel exists: the role bytes still read as a
	/// solo host.
	/// </summary>
	public static bool LocalCoop { get; private set; }

	/// <summary>Player two's guest pad in local co-op, else −1; only the change is reported.</summary>
	private static int _localPadTwo = -1;

	/// <summary>Player two's guest pad in local co-op (`Player+40` of player two), else −1.</summary>
	public static int LocalPadTwo => _localPadTwo;

	private static Fable2Bridge.CoopSessionWords _last;

	/// <summary>Polls the bridge and advances the state machine. Call once per frame.</summary>
	public static void Poll()
	{
		if (Fable2Bridge.CoopSessionRaw() is not { } words)
		{
			_managerWas = false;
			SetLocalCoop(false, -1);
			Transition(Phase.Solo, Role.Solo);
			return;
		}

		_last = words;
		SetLocalCoop(words.LocalCoop, words.LocalCoop ? (int)words.PlayerTwoPad : -1);
		StateSplitScreen(words.ManagerPresent);

		// ROLE: the guest's own reading of its bytes. Note a solo game carries {session, host}
		// too (Frontend_SetStartGameTarget sets them on every start), so "host" alone is not
		// "in co-op"; the peer's presence is the client-joined byte, or being the client.
		Role side = !words.ManagerPresent || !words.Session ? Role.Solo
			: words.Client ? Role.Client
			: Role.Host;
		bool peer = words.Session && !words.Lost &&
			(side == Role.Client || (side == Role.Host && words.ClientJoined));

		Phase next = peer ? Phase.PeerJoined
			: State == Phase.PeerJoined || State == Phase.PeerLost && words.Lost ? Phase.PeerLost
			: side == Role.Host && words.Session ? Phase.Hosting
			: Phase.Solo;
		Transition(next, side);
	}

	/// <summary>What was last stated to the guest: 1 split, 0 shared, -1 nothing yet this game.</summary>
	private static int _splitStated = -1;

	private static bool _managerWas;

	/// <summary>
	/// States the local co-op split to the guest (<c>CoopSplitScreen:</c>), on every game start and
	/// whenever it changes (<see cref="Refii.Game.GameSplitView.GuestSplit"/>: split mode, or hybrid while
	/// split): each player's own camera, readers and free roam follow it at once (player two's camera
	/// manager stands in every local co-op game, coop_hero_hooks.cpp).
	/// </summary>
	private static void StateSplitScreen(bool managerPresent)
	{
		if (managerPresent && !_managerWas)
		{
			_splitStated = -1;
		}

		_managerWas = managerPresent;
		if (!managerPresent)
		{
			return;
		}

		int want = Refii.Game.GameSplitView.GuestSplit ? 1 : 0;
		if (want == _splitStated)
		{
			return;
		}

		_splitStated = want;
		HostBridge.PostFrontEndAction("CoopSplitScreen:" + want);
		Godot.GD.Print($"[coop-session] local co-op screen: {(want == 1 ? "split" : "shared")}");
	}

	private static void SetLocalCoop(bool local, int pad)
	{
		if (local == LocalCoop && pad == _localPadTwo)
		{
			return;
		}

		LocalCoop = local;
		_localPadTwo = pad;
		Godot.GD.Print(local
			? $"[coop-session] local co-op — player two on pad {pad}"
			: "[coop-session] local co-op ended");
	}

	private static void Transition(Phase next, Role side)
	{
		Side = side;
		if (next == State)
		{
			return;
		}

		Phase was = State;
		State = next;
		Godot.GD.Print($"[coop-session] {was} → {next} (role {side}, peers {_last.PeerCount}, " +
			$"one 0x{_last.PlayerOne:X8}, two 0x{_last.PlayerTwo:X8}" +
			$"{(_last.GameCopyPresent ? string.Empty : ", game+140 copy EMPTY")})");
	}
}
