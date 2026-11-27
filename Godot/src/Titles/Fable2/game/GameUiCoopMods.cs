using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Refii.Host;
using Refii.Mods;
using Refii.Ui;

namespace Refii.Titles.Fable2;

using Refii.Game;

/// <summary>
/// Both co-op players run the same mods: the host's set, in the host's order, is the session's.
/// A joiner whose set differs is offered it — what it lacks is downloaded from the host, what it
/// has with the same bytes is used as-is, its own mods the host does not run are suspended —
/// once per join, with consent, for the session only. A set that cannot be taken on (a hard mod
/// on either side, another mod API, more than the session may download) is refused as a mismatch.
/// </summary>
/// <remarks>
/// THE RECORD IS STATE, LIKE EVERYTHING ELSE ON THE CHANNEL. Each side restates its
/// <see cref="ModFingerprint"/> (<c>mods=…</c>) on every co-op tick, and each side compares the
/// peer's stated digest with its own once the guest says a peer is in the session
/// (<see cref="CoopSession.PeerStanding"/>: the host's client-joined byte, the joiner's accept
/// byte). Before that the role bytes do not yet say who is who — a joiner's own solo game reads
/// as a host — so a record that arrives early is only stored.
///
/// ON A MISMATCH THE JOINER ACTS AND THE HOST WAITS. The joiner plans the session set
/// (<see cref="ModSync.Resolve"/>), asks its player in the game's own box, fetches what it lacks
/// over the mod sync stream (<see cref="HostBridge.SyncStreamConnect"/>: the reliable rexnet
/// stream on Refii's own port), verifies each package's SHA-256, folds the set in live
/// (<see cref="ModSession.Apply"/>: the mount's passes again, the mods' Lua run again) and from
/// then on restates the SESSION set, which carries the host's digest. Meanwhile it restates its
/// phase (<c>modsync=…</c>); the host serves the stream and refuses only when the joiner declines,
/// cannot, fails, never states a phase (an older build) or takes too long. The host never adopts
/// anything: its set wins, whichever side has more.
///
/// A REFUSAL IS REFUSED ON BOTH MACHINES. The reason is said on the multiplayer row's page
/// (never a box) and the session is left the guest's own way, the pump's <c>CoopLeave</c>: on the
/// host that drops the joiner, on the joiner it leaves the host.
///
/// EVERY EXIT RESTORES. <see cref="ResetCoopMods"/> runs on every session end the hero channel
/// sees (the peer gone, the session ended, the side changed): the transfer is dropped, the stream
/// closed, the box withdrawn, and the player's own mods remounted with their Lua run again. The
/// session set is memory only; the launcher's list and the player's settings are never written.
/// </remarks>
public partial class GameUi
{
	/// <summary>The pump action that leaves the co-op session as the guest's own Reject does.</summary>
	private const string CoopLeaveAction = "CoopLeave";

	/// <summary>
	/// Channel messages a standing peer may send without its mods record before it is taken for
	/// a build that never states one. It restates every half second beside several other
	/// records, so a peer that does state it is never this far behind. The same budget covers a
	/// joiner that states its mods but, on a mismatch, never states a sync phase.
	/// </summary>
	private const int CoopModsUnstatedLimit = 20;

	/// <summary>The most a host waits for a joiner's sync, consent and download included.</summary>
	private const double ModSyncHostWaitSeconds = 900.0;

	/// <summary>How long the joiner waits for the stream to open.</summary>
	private const double ModSyncConnectSeconds = 30.0;

	/// <summary>A download with no bytes for this long has lost the host.</summary>
	private const double ModSyncStallSeconds = 60.0;

	private const double ModSyncProgressSeconds = 0.5;

	/// <summary>Chunks received between two acknowledgements.</summary>
	private const int ModSyncAckEvery = 2;

	private const string ModSyncBoxKey = "modsync";

	/// <summary>
	/// This instance's set, hashed off the main thread from the first frame on. Dropped and hashed
	/// again when the session set is applied or restored, so the restatement carries what runs.
	/// </summary>
	private Task<ModFingerprint>? _coopModsOwn;

	private bool _coopModsFaultSaid;

	/// <summary>The peer's last stated set, or null.</summary>
	private ModFingerprint? _coopModsPeer;

	/// <summary>The digest last logged as agreeing, so the agreement is said once.</summary>
	private string _coopModsAgreedSaid = string.Empty;

	/// <summary>Whether this session was refused; nothing is checked again until it resets.</summary>
	private bool _coopModsRefused;

	/// <summary>Messages from the standing peer before its first mods record.</summary>
	private int _coopModsUnstated;

	// ---------------------------------------------------------------- the joiner's sync

	private ModSync.Phase _modSyncPhase;

	private string _modSyncDetail = string.Empty;

	private ModSync.Plan? _modSyncPlan;

	/// <summary>Every installed package and the session cache, hashed off the main thread.</summary>
	private Task<(List<ModSync.Item> Installed, List<ModSync.Item> Cached)>? _modSyncLocal;

	private ModSyncFetch? _modSyncFetch;

	private double _modSyncProgressAt;

	/// <summary>The progress notice last told, so only our own is taken down.</summary>
	private string _modSyncNotice = string.Empty;

	private ulong _modSyncConnectAtMs;

	private string? _modSyncCacheDir;

	// ---------------------------------------------------------------- the host's view

	private ModSync.Phase _modSyncPeerPhase;

	private string _modSyncPeerDetail = string.Empty;

	/// <summary>Messages from the standing joiner since the mismatch, before its first phase.</summary>
	private int _modSyncPeerUnstated;

	private ulong _modSyncMismatchSinceMs;

	private bool _modSyncPeerToldFetching;

	private ModSyncServe? _modSyncServe;

	/// <summary>Staging for the stream's bytes; main thread only.</summary>
	private static readonly byte[] ModSyncBuffer = new byte[256 * 1024];

	/// <summary>Where received packages live: Godot's <c>user://</c>, keyed by hash (ModSyncCache).</summary>
	private string ModSyncCacheDir =>
		_modSyncCacheDir ??= ProjectSettings.GlobalizePath("user://" + ModSyncCache.Folder);

	/// <summary>The own set once hashed, or null while it is still being hashed.</summary>
	private ModFingerprint? CoopModsOwn()
	{
		_coopModsOwn ??= Task.Run(() => ModFingerprint.CurrentForCoop());
		if (_coopModsOwn.IsFaulted && !_coopModsFaultSaid)
		{
			// Said, not hidden: with no set to state, every peer refuses this one.
			_coopModsFaultSaid = true;
			GD.PushError($"[game-ui] co-op — the mod set could not be read: {_coopModsOwn.Exception?.GetBaseException().Message}");
		}

		return _coopModsOwn.IsCompletedSuccessfully ? _coopModsOwn.Result : null;
	}

	/// <summary>Restates this instance's set to the peer, and the joiner's sync phase while one stands; nothing until it is hashed.</summary>
	private void SendCoopMods()
	{
		if (CoopModsOwn() is { } own)
		{
			HostBridge.CoopSend(own.Encode());
		}

		if (SessionRole == 2u && _modSyncPhase != ModSync.Phase.None)
		{
			HostBridge.CoopSend(ModSync.EncodePhase(_modSyncPhase, _modSyncDetail));
		}
	}

	/// <summary>
	/// Takes one channel message. True when it was the peer's mods record or sync phase (stored);
	/// any other message from a standing peer counts toward <see cref="CoopModsUnstatedLimit"/>.
	/// </summary>
	private bool TakeCoopMods(string message)
	{
		if (message.StartsWith(ModFingerprint.WirePrefix, System.StringComparison.Ordinal))
		{
			if (ModFingerprint.TryDecode(message, out ModFingerprint? peer) && peer is not null)
			{
				_coopModsPeer = peer;
			}

			return true;
		}

		if (ModSync.TryDecodePhase(message, out ModSync.Phase phase, out string detail))
		{
			if (phase != _modSyncPeerPhase || detail != _modSyncPeerDetail)
			{
				GD.Print($"[game-ui] co-op — the joiner's mod sync is {phase}{(detail.Length == 0 ? string.Empty : ": " + detail)}");
			}

			_modSyncPeerPhase = phase;
			_modSyncPeerDetail = detail;
			return true;
		}

		if (CoopSession.PeerStanding)
		{
			if (_coopModsPeer is null)
			{
				++_coopModsUnstated;
			}

			if (_modSyncMismatchSinceMs != 0 && _modSyncPeerPhase == ModSync.Phase.None)
			{
				++_modSyncPeerUnstated;
			}
		}

		return false;
	}

	/// <summary>
	/// Compares the sets once a peer stands in the session, and syncs or refuses a mismatch.
	/// Called once a frame after the inbox is drained; the transfer pumps run from here too.
	/// </summary>
	private void CheckCoopMods(uint role)
	{
		// Starts the hash on the first frame, long before a join, so a first-time hash of a large
		// package is not still running when the peer starts counting.
		ModFingerprint? own = CoopModsOwn();
		if (_coopModsRefused || !CoopSession.PeerStanding || role == 0u || own is null)
		{
			return;
		}

		// The stream is served (host) or pumped (joiner) whatever the sets say.
		if (role == 1u)
		{
			ServeModSync(own);
		}
		else
		{
			PumpModSyncFetch();
		}

		string self = role == 1u ? "host" : "joiner";
		string other = role == 1u ? "joiner" : "host";
		if (_coopModsPeer is not { } peer)
		{
			if (_coopModsUnstated >= CoopModsUnstatedLimit)
			{
				RefuseCoopForMods(
					$"the {other}'s Refii does not state its mods",
					Engine_UiCoopModWords.Unstated(otherIsHost: role != 1u));
			}

			return;
		}

		if (peer.Digest() == own.Digest())
		{
			if (_coopModsAgreedSaid != own.Digest())
			{
				_coopModsAgreedSaid = own.Digest();
				GD.Print($"[game-ui] co-op — mods agree with the {other} ({own.Mods.Count} enabled, digest {own.Digest()})" +
					(ModSession.IsActive ? " — the host's set, held for this session" : string.Empty));
				if (role == 1u && _modSyncPeerToldFetching)
				{
					_coopNotices.Tell(Engine_UiCoopModWords.PartnerRunning());
				}
			}

			return;
		}

		if (role == 1u)
		{
			FollowJoinerModSync(own, peer);
		}
		else
		{
			DriveJoinerModSync(own, peer);
		}
	}

	// ---------------------------------------------------------------- the host

	/// <summary>The host on a mismatch: waits on the joiner's stated phase, refusing what cannot end in agreement.</summary>
	private void FollowJoinerModSync(ModFingerprint own, ModFingerprint peer)
	{
		if (_modSyncMismatchSinceMs == 0)
		{
			_modSyncMismatchSinceMs = Time.GetTicksMsec();
			GD.Print($"[game-ui] co-op — mods differ (digest {own.Digest()} here, {peer.Digest()} on the joiner: " +
				$"{own.Describe(peer, "host", "joiner")}); waiting for the joiner's mod sync");
		}

		switch (_modSyncPeerPhase)
		{
			case ModSync.Phase.None:
				if (_modSyncPeerUnstated >= CoopModsUnstatedLimit)
				{
					string diff = own.Describe(peer, "host", "joiner");
					RefuseCoopForMods(
						$"digest {own.Digest()} here, {peer.Digest()} on the joiner: {diff}; the joiner's Refii states no mod sync",
						Engine_UiCoopModWords.OldJoiner(diff));
				}

				return;

			case ModSync.Phase.Declined:
				RefuseCoopForMods("the joiner declined this game's mods",
					Engine_UiCoopModWords.JoinerDeclined());
				return;

			case ModSync.Phase.Blocked:
			case ModSync.Phase.Failed:
				RefuseCoopForMods($"the joiner's mod sync {_modSyncPeerPhase}: {_modSyncPeerDetail}",
					Engine_UiCoopModWords.JoinerCannot(_modSyncPeerDetail));
				return;

			case ModSync.Phase.Fetching:
				if (!_modSyncPeerToldFetching)
				{
					_modSyncPeerToldFetching = true;
					_coopNotices.Tell(Engine_UiCoopModWords.PartnerReceiving());
				}

				break;
		}

		if (Time.GetTicksMsec() - _modSyncMismatchSinceMs > (ulong)(ModSyncHostWaitSeconds * 1000.0))
		{
			RefuseCoopForMods("the joiner's mod sync took too long",
				Engine_UiCoopModWords.JoinerSlow(ModSyncHostWaitSeconds));
		}
	}

	/// <summary>The host's side of the stream: one package per request, chunks within the window.</summary>
	private void ServeModSync(ModFingerprint own)
	{
		HostBridge.SyncStreamState state = HostBridge.SyncStreamStatus();
		if (state is HostBridge.SyncStreamState.ClosedByPeer or HostBridge.SyncStreamState.Overflowed or
			HostBridge.SyncStreamState.ConnectFailed)
		{
			GD.Print($"[game-ui] co-op — mod sync stream {state}; closed");
			HostBridge.SyncStreamClose();
			_modSyncServe = null;
			return;
		}

		if (state != HostBridge.SyncStreamState.Open)
		{
			return;
		}

		ModSyncServe serve = _modSyncServe ??= new ModSyncServe();
		int taken;
		while ((taken = HostBridge.SyncStreamReceive(ModSyncBuffer)) > 0)
		{
			serve.Reader.Feed(ModSyncBuffer.AsSpan(0, taken));
		}

		if (serve.Reader.Error.Length != 0)
		{
			GD.Print($"[game-ui] co-op — mod sync: the joiner's request does not read ({serve.Reader.Error}); stream closed");
			HostBridge.SyncStreamClose();
			_modSyncServe = null;
			return;
		}

		while (serve.Reader.Next() is { } frame)
		{
			switch (frame.Kind)
			{
				case ModSync.Kind.Get:
					string request = System.Text.Encoding.UTF8.GetString(frame.Payload);
					string[] fields = request.Split('\t');
					ModFingerprint.Mod? found = null;
					foreach (ModFingerprint.Mod mod in own.Mods)
					{
						if (fields.Length == 2 && string.Equals(mod.Name, fields[1], System.StringComparison.OrdinalIgnoreCase) &&
							ModSync.SameBytes(mod.Sha256, fields[0]) && !mod.Native)
						{
							found = mod;
							break;
						}
					}

					string path = found is { } wanted ? ModSyncPackagePath(wanted.Name) : string.Empty;
					if (found is null || path.Length == 0)
					{
						GD.Print($"[game-ui] co-op — mod sync: the joiner asked for '{request}', which this game does not run; refused");
						Send(ModSync.EncodeFrame(ModSync.Kind.Refuse, "the host does not run that mod"));
						break;
					}

					serve.Feeder = null;
					serve.Loading = Task.Run(() => System.IO.File.ReadAllBytes(path));
					serve.LoadingName = found.Value.Name;
					serve.LoadingSha = found.Value.Sha256;
					GD.Print($"[game-ui] co-op — mod sync: sending {found.Value.Label} ({ModSync.Bytes(found.Value.Size)}) to the joiner");
					break;

				case ModSync.Kind.Ack:
					if (serve.Feeder is { } feeder && frame.Payload.Length == 4)
					{
						feeder.Ack(System.BitConverter.ToUInt32(frame.Payload, 0));
						foreach (byte[] chunk in feeder.Next())
						{
							Send(chunk);
						}

						if (feeder.Done)
						{
							GD.Print($"[game-ui] co-op — mod sync: {feeder.Name} delivered ({feeder.ChunkCount} chunks)");
							serve.Feeder = null;
						}
					}

					break;
			}
		}

		if (serve.Loading is { IsCompleted: true } loading)
		{
			serve.Loading = null;
			if (loading.IsCompletedSuccessfully)
			{
				var feeder = new ModSync.Feeder(serve.LoadingName, serve.LoadingSha, loading.Result);
				serve.Feeder = feeder;
				Send(feeder.Head());
				foreach (byte[] chunk in feeder.Next())
				{
					Send(chunk);
				}
			}
			else
			{
				GD.PushError($"[game-ui] co-op — mod sync: {serve.LoadingName} could not be read: {loading.Exception?.GetBaseException().Message}");
				Send(ModSync.EncodeFrame(ModSync.Kind.Refuse, "the host could not read the package"));
			}
		}

		static void Send(byte[] frame) => HostBridge.SyncStreamSend(frame, frame.Length);
	}

	/// <summary>The package this game runs under <paramref name="name"/>, by the mount's own resolution.</summary>
	private static string ModSyncPackagePath(string name)
	{
		foreach (ModInstall install in ModSession.Resolve(Refii.Assets.Engine_GameAssets.ModsRoot).Installs)
		{
			if (string.Equals(install.Name, name, System.StringComparison.OrdinalIgnoreCase))
			{
				return install.PackagePath;
			}
		}

		return string.Empty;
	}

	// ---------------------------------------------------------------- the joiner

	/// <summary>The joiner on a mismatch: plan, ask, fetch, apply — one phase per join.</summary>
	private void DriveJoinerModSync(ModFingerprint own, ModFingerprint host)
	{
		switch (_modSyncPhase)
		{
			case ModSync.Phase.None:
				string cacheDir = ModSyncCacheDir;
				_modSyncLocal ??= Task.Run(() =>
					(ModFingerprint.Installed(Refii.Assets.Engine_GameAssets.ModsRoot), ModSyncCache.List(cacheDir)));
				if (_modSyncLocal.IsFaulted)
				{
					FailModSync("your installed mods could not be read: " + _modSyncLocal.Exception?.GetBaseException().Message);
					return;
				}

				if (!_modSyncLocal.IsCompletedSuccessfully)
				{
					return;
				}

				(List<ModSync.Item> installed, List<ModSync.Item> cached) = _modSyncLocal.Result;
				ModSync.Plan plan = ModSync.Resolve(host, own, installed, cached);
				_modSyncPlan = plan;
				if (!plan.Ok)
				{
					_modSyncPhase = ModSync.Phase.Blocked;
					_modSyncDetail = plan.Blocked;
					RefuseCoopForMods($"digest {own.Digest()} here, {host.Digest()} on the host; cannot sync: {plan.Blocked}",
						Engine_UiCoopModWords.HostBlocked(plan.Blocked));
					return;
				}

				_modSyncPhase = ModSync.Phase.Asking;
				_modSyncDetail = $"{plan.DownloadCount} to download, {plan.Suspend.Count} to suspend";
				GD.Print($"[game-ui] co-op — mods differ from the host's ({own.Describe(host, "joiner", "host")}); " +
					$"asking: {plan.DownloadCount} download(s) of {ModSync.Bytes(plan.DownloadBytes)}, " +
					$"{plan.Replaced.Count} replaced, {plan.Suspend.Count} suspended");
				AskModSyncConsent();
				return;

			case ModSync.Phase.Asking:
				// A box replaced by another question is asked again once the slot is free.
				if (!Engine_UiHostBoxes.Asking(ModSyncBoxKey) && Engine_UiHostBoxes.Question is null)
				{
					AskModSyncConsent();
				}

				return;

			case ModSync.Phase.Ready:
				// The restatement carries the session set; the host's digest agrees on its next
				// compare. A host whose set changed under the sync is planned again from the start.
				if (host.Digest() != _modSyncPlan?.HostDigest)
				{
					GD.Print("[game-ui] co-op — the host's mods changed during the sync; planning again");
					_modSyncPhase = ModSync.Phase.None;
					_modSyncPlan = null;
				}

				return;
		}
	}

	private void AskModSyncConsent()
	{
		if (_modSyncPlan is not { } plan)
		{
			return;
		}

		Engine_UiHostBoxes.Ask(ModSync.ConsentText(plan, PartnerName()), answer =>
		{
			if (_modSyncPhase != ModSync.Phase.Asking)
			{
				return;
			}

			switch (answer)
			{
				case 0:
					GD.Print("[game-ui] co-op — the host's mods accepted for this session");
					if (plan.DownloadCount == 0)
					{
						ApplyModSyncPlan();
					}
					else
					{
						_modSyncPhase = ModSync.Phase.Fetching;
						_modSyncFetch = new ModSyncFetch(new List<ModSync.Item>(plan.Downloads));
						_modSyncConnectAtMs = Time.GetTicksMsec();
						_modSyncDetail = $"0/{plan.DownloadCount}";
						if (!HostBridge.SyncStreamConnect())
						{
							FailModSync("the download stream could not be opened (no peer address yet)");
						}
					}

					break;

				case 1:
					_modSyncPhase = ModSync.Phase.Declined;
					_modSyncDetail = string.Empty;
					RefuseCoopForMods("the player declined the host's mods",
						Engine_UiCoopModWords.YouDeclined());
					break;
			}
		}, ModSync.AcceptCaption, ModSync.DeclineCaption, ModSyncBoxKey);
	}

	/// <summary>The joiner's side of the stream: request, take chunks, acknowledge, verify, store; then the next.</summary>
	private void PumpModSyncFetch()
	{
		if (_modSyncPhase != ModSync.Phase.Fetching || _modSyncFetch is not { } fetch)
		{
			return;
		}

		HostBridge.SyncStreamState state = HostBridge.SyncStreamStatus();
		ulong now = Time.GetTicksMsec();
		switch (state)
		{
			case HostBridge.SyncStreamState.ConnectFailed:
				FailModSync("the download stream could not be opened");
				return;
			case HostBridge.SyncStreamState.ClosedByPeer:
				FailModSync("the host closed the download stream");
				return;
			case HostBridge.SyncStreamState.Overflowed:
				FailModSync("the download stream overflowed");
				return;
			case HostBridge.SyncStreamState.Idle:
			case HostBridge.SyncStreamState.Connecting:
				if (now - _modSyncConnectAtMs > (ulong)(ModSyncConnectSeconds * 1000.0))
				{
					FailModSync($"the download stream did not open in {ModSyncConnectSeconds:0} seconds");
				}

				return;
		}

		ModSync.Item item = fetch.Items[fetch.Index];
		if (!fetch.Requested)
		{
			fetch.Requested = true;
			fetch.LastBytesAtMs = now;
			byte[] get = ModSync.EncodeFrame(ModSync.Kind.Get, item.Mod.Sha256 + "\t" + item.Mod.Name);
			HostBridge.SyncStreamSend(get, get.Length);
			GD.Print($"[game-ui] co-op — mod sync: requesting {item.Mod.Label} ({ModSync.Bytes(item.Mod.Size)}), {fetch.Index + 1} of {fetch.Items.Count}");
		}

		int taken;
		while ((taken = HostBridge.SyncStreamReceive(ModSyncBuffer)) > 0)
		{
			fetch.Reader.Feed(ModSyncBuffer.AsSpan(0, taken));
			fetch.LastBytesAtMs = now;
		}

		if (fetch.Reader.Error.Length != 0)
		{
			FailModSync("the host's data does not read: " + fetch.Reader.Error);
			return;
		}

		while (fetch.Reader.Next() is { } frame)
		{
			switch (frame.Kind)
			{
				case ModSync.Kind.Head:
					var head = ModSync.ParseHead(System.Text.Encoding.UTF8.GetString(frame.Payload));
					if (head is null || !ModSync.SameBytes(head.Value.Sha256, item.Mod.Sha256) ||
						head.Value.Size != item.Mod.Size || head.Value.Size > ModSync.MaxSessionBytes)
					{
						FailModSync($"the host offered a different package for {item.Mod.Label} than it stated");
						return;
					}

					fetch.Assembler = new ModSync.Assembler(item.Mod.Name, head.Value.Sha256, head.Value.Size, head.Value.ChunkBytes, head.Value.ChunkCount);
					fetch.SinceAck = 0;
					break;

				case ModSync.Kind.Chunk:
					if (fetch.Assembler is not { } assembler || frame.Payload.Length < 4)
					{
						FailModSync("a chunk arrived before its package's head");
						return;
					}

					uint index = System.BitConverter.ToUInt32(frame.Payload, 0);
					if (!assembler.Take(index, frame.Payload.AsSpan(4)))
					{
						FailModSync($"chunk {index} of {item.Mod.Label} is out of order or the wrong size");
						return;
					}

					if (++fetch.SinceAck >= ModSyncAckEvery || assembler.Complete)
					{
						fetch.SinceAck = 0;
						byte[] ack = ModSync.EncodeAck((uint)assembler.Received);
						HostBridge.SyncStreamSend(ack, ack.Length);
					}

					if (assembler.Complete)
					{
						byte[]? bytes = assembler.Verify(item.Mod.Sha256);
						if (bytes is null)
						{
							FailModSync($"{item.Mod.Label} arrived with a different SHA-256 than the host stated");
							return;
						}

						if (!ModSyncCache.Store(ModSyncCacheDir, assembler.Sha256, bytes, out string error))
						{
							FailModSync($"{item.Mod.Label} could not be kept: {error}");
							return;
						}

						GD.Print($"[game-ui] co-op — mod sync: {item.Mod.Label} received and verified ({ModSync.Bytes(bytes.LongLength)})");
						_modSyncPlan?.Received(item.Mod.Name, assembler.Sha256, ModSyncCache.PathFor(ModSyncCacheDir, assembler.Sha256));
						fetch.BytesDone += bytes.LongLength;
						fetch.Assembler = null;
						fetch.Requested = false;
						++fetch.Index;
						_modSyncDetail = $"{fetch.Index}/{fetch.Items.Count}";
						if (fetch.Index >= fetch.Items.Count)
						{
							HostBridge.SyncStreamClose();
							TakeDownModSyncNotice();
							ApplyModSyncPlan();
							return;
						}
					}

					break;

				case ModSync.Kind.Refuse:
					FailModSync($"the host refused {item.Mod.Label}: {System.Text.Encoding.UTF8.GetString(frame.Payload)}");
					return;

				default:
					FailModSync($"an unexpected {frame.Kind} frame from the host");
					return;
			}
		}

		if (now - fetch.LastBytesAtMs > (ulong)(ModSyncStallSeconds * 1000.0))
		{
			FailModSync($"no data from the host for {ModSyncStallSeconds:0} seconds");
			return;
		}

		_modSyncProgressAt -= GetProcessDeltaTime();
		if (_modSyncProgressAt <= 0.0)
		{
			_modSyncProgressAt = ModSyncProgressSeconds;
			long done = fetch.Assembler?.BytesReceived ?? 0;
			string text = ModSync.ProgressText(PartnerName(), item.Mod, fetch.Index, fetch.Items.Count, done, item.Mod.Size);
			if (Engine_UiHostBoxes.Notice is null || Engine_UiHostBoxes.Notice == _modSyncNotice)
			{
				_modSyncNotice = text;
				Engine_UiHostBoxes.Tell(text);
			}
		}
	}

	/// <summary>Folds the session set in: the live remount with the mods' Lua run again.</summary>
	private void ApplyModSyncPlan()
	{
		if (_modSyncPlan is not { } plan)
		{
			return;
		}

		_modSyncPhase = ModSync.Phase.Applying;
		_modSyncDetail = string.Empty;
		List<ModSession.Entry> set = plan.ToSession(ModSyncCacheDir);
		var inUse = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
		foreach (ModSession.Entry entry in set)
		{
			inUse.Add(entry.Sha256);
		}

		int evicted = ModSyncCache.Evict(ModSyncCacheDir, ModSyncCache.KeepBytes, inUse);
		if (evicted != 0)
		{
			GD.Print($"[game-ui] co-op — mod sync: {evicted} older package(s) evicted from the session cache");
		}

		GD.Print($"[game-ui] co-op — mod sync: applying the host's set ({set.Count} mods) for this session");
		if (!ModSession.Apply(set, report =>
			{
				if (_modSyncPhase != ModSync.Phase.Applying)
				{
					return;
				}

				if (!report.Ok)
				{
					FailModSync("the host's mods could not be mounted: " + report.Error);
					return;
				}

				_modSyncPhase = ModSync.Phase.Ready;
				_modSyncDetail = plan.HostDigest;
				_coopModsOwn = null;
				GD.Print($"[game-ui] co-op — mod sync: the host's set is mounted ({string.Join(", ", report.Mods)}); " +
					$"Lua {(report.LuaQueued ? "queued" : "not run")}: {report.LuaNote}");
				_coopNotices.Tell(Engine_UiCoopModWords.SessionMounted(set.Count, PartnerName()));
			}, out string error))
		{
			FailModSync("the host's mods could not be mounted: " + error);
		}
	}

	/// <summary>A sync that cannot finish: said, stated to the host, and the session refused.</summary>
	private void FailModSync(string why)
	{
		HostBridge.SyncStreamClose();
		TakeDownModSyncNotice();
		_modSyncFetch = null;
		_modSyncPhase = ModSync.Phase.Failed;
		_modSyncDetail = why;
		RefuseCoopForMods("mod sync failed: " + why,
			Engine_UiCoopModWords.SyncFailed(why));
	}

	private void TakeDownModSyncNotice()
	{
		if (_modSyncNotice.Length != 0 && Engine_UiHostBoxes.Notice == _modSyncNotice)
		{
			Engine_UiHostBoxes.Dismiss();
		}

		_modSyncNotice = string.Empty;
	}

	/// <summary>
	/// Says the refusal on the multiplayer row's page and leaves the session; once a session.
	/// <paramref name="logged"/> is the log's reason (English); <paramref name="said"/> is the player's line,
	/// one of <see cref="Engine_UiCoopModWords"/>.
	/// </summary>
	private void RefuseCoopForMods(string logged, string said)
	{
		_coopModsRefused = true;
		GD.Print($"[game-ui] co-op — REFUSED: {logged}; leaving the session");
		Engine_UiNetworkSource.Say(said, hold: true);
		HostBridge.PostFrontEndAction(CoopLeaveAction);
	}

	/// <summary>
	/// Drops the peer's set and the verdict, so the next session is checked afresh; and every
	/// exit's restore — the transfer dropped, the stream closed, the box withdrawn, the player's
	/// own mods mounted again with their Lua run again.
	/// </summary>
	private void ResetCoopMods()
	{
		_coopModsPeer = null;
		_coopModsAgreedSaid = string.Empty;
		_coopModsRefused = false;
		_coopModsUnstated = 0;

		HostBridge.SyncStreamClose();
		Engine_UiHostBoxes.Withdraw(ModSyncBoxKey);
		TakeDownModSyncNotice();
		_modSyncPhase = ModSync.Phase.None;
		_modSyncDetail = string.Empty;
		_modSyncPlan = null;
		_modSyncLocal = null;
		_modSyncFetch = null;
		_modSyncPeerPhase = ModSync.Phase.None;
		_modSyncPeerDetail = string.Empty;
		_modSyncPeerUnstated = 0;
		_modSyncMismatchSinceMs = 0;
		_modSyncPeerToldFetching = false;
		_modSyncServe = null;

		if (ModSession.IsActive)
		{
			GD.Print("[game-ui] co-op — mod sync: the session ended; mounting your own mods again");
			if (ModSession.Restore(report =>
				{
					_coopModsOwn = null;
					if (report.Ok)
					{
						GD.Print($"[game-ui] co-op — mod sync: your own mods are mounted again ({string.Join(", ", report.Mods)}); " +
							$"Lua {(report.LuaQueued ? "queued" : "not run")}");
					}
					else
					{
						GD.PushError("[game-ui] co-op — mod sync: restoring your own mods failed: " + report.Error);
					}
				}, out string error))
			{
				return;
			}

			// The set is dropped either way; the next mount is the player's own.
			_coopModsOwn = null;
			GD.PushError("[game-ui] co-op — mod sync: your own mods could not be remounted now: " + error);
		}
	}

	/// <summary>The joiner's transfer: the packages to fetch, in order, and the one in flight.</summary>
	private sealed class ModSyncFetch
	{
		public readonly List<ModSync.Item> Items;

		public int Index;

		public bool Requested;

		public readonly ModSync.FrameReader Reader = new();

		public ModSync.Assembler? Assembler;

		public int SinceAck;

		public long BytesDone;

		public ulong LastBytesAtMs;

		public ModSyncFetch(List<ModSync.Item> items)
		{
			Items = items;
		}
	}

	/// <summary>The host's serving state: the request reader and the package being fed.</summary>
	private sealed class ModSyncServe
	{
		public readonly ModSync.FrameReader Reader = new();

		public ModSync.Feeder? Feeder;

		public Task<byte[]>? Loading;

		public string LoadingName = string.Empty;

		public string LoadingSha = string.Empty;
	}
}
