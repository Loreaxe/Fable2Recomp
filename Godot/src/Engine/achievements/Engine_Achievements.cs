using System;
using System.Collections.Generic;
using Godot;
using Refii.Save;
using Refii.Ui;

namespace Refii.Achievements;

/// <summary>One achievement as the title defines it, in the display language.</summary>
public readonly record struct AchievementDefinition(
	uint Id, uint ImageId, uint Gamerscore, uint Flags,
	string Name, string UnlockedDescription, string LockedDescription);

/// <summary>
/// THE ACHIEVEMENT SYSTEM. Definitions come from the title's XDBF (through the seam, once),
/// unlocks come from the guest's writes (a sequence read through the same seam), the stores and
/// their files are ours, and every save's unlocked set goes back down so the guest's own per-user
/// enumerator reads the same truth. The toast takes <see cref="TakeNewUnlocks"/>; the page opens on
/// <see cref="TakeUiRequests"/> — `XamShowAchievementsUI`, which the guest calls from its
/// logbook row (`sub_826D9620` case 150).
///
/// PER CHARACTER (<see cref="Engine_AchievementCharacters"/>): each hero earns its own, from scratch;
/// the profile's <see cref="FileName"/> is the profile total, which every unlock also joins. The
/// guest's adoption points say which save each user's hero comes from (the bindings), so a write
/// lands on the hero that earned it; the questions here that take a PLAYER (1 or 2) answer for the
/// hero that player plays, and the player-less ones answer the profile total.
///
/// Everything here runs on Godot's thread: the files are written here, never on the guest thread
/// that recorded the write.
/// </summary>
public static class Engine_Achievements
{
	public const string FileName = "achievements.refii";

	private static Engine_XdbfDatabase? _database;
	private static Engine_AchievementStore? _store;
	private static Engine_AchievementCharacters? _characters;
	private static ulong _seenBinding;
	private static bool _tableDirty = true;
	private static readonly List<AchievementDefinition> _definitions = new();
	private static readonly Dictionary<uint, AchievementDefinition> _byId = new();
	private static readonly Dictionary<uint, Texture2D?> _icons = new();
	private static readonly List<uint> _newUnlocks = new();
	private static ulong _seenSequence;
	private static uint _seenUiRequests;
	private static bool _uiRequestsPrimed;
	private static int _xdbfRetryFrames;

	public static IReadOnlyList<AchievementDefinition> Definitions => _definitions;

	/// <summary>True once the XDBF has been read; definitions are empty until then.</summary>
	public static bool DefinitionsLoaded => _database is not null;

	public static string Report { get; private set; } = "not started";

	/// <summary>A harness's own store file; null takes the active title's folder in the active profile.</summary>
	public static string? StorePathOverride { get; set; }

	public static string StorePath =>
		StorePathOverride ?? System.IO.Path.Combine(Refii.Core.Engine_Title.Active.SaveDirectory, FileName);

	public static AchievementDefinition? Definition(uint id) =>
		_byId.TryGetValue(id, out AchievementDefinition definition) ? definition : null;

	/// <summary>The profile total: unlocked by any of the profile's heroes, or before heroes had their own.</summary>
	public static bool IsUnlocked(uint id) => _store?.IsUnlocked(id) ?? false;

	/// <summary>When the profile first earned it, or null while locked or when the record carries no time.</summary>
	public static DateTime? UnlockedAt(uint id) => When(_store?.Get(id));

	/// <summary>Unlocked by the hero player <paramref name="player"/> plays on this machine.</summary>
	public static bool IsUnlocked(int player, uint id) => StoreOf(player)?.IsUnlocked(id) ?? false;

	/// <summary>When the hero player <paramref name="player"/> plays earned it, or null.</summary>
	public static DateTime? UnlockedAt(int player, uint id) => When(StoreOf(player)?.Get(id));

	private static DateTime? When(Engine_AchievementStore.Unlock? unlock) =>
		unlock is { UnlockedUnixSeconds: > 0 } earned
			? DateTimeOffset.FromUnixTimeSeconds(earned.UnlockedUnixSeconds).LocalDateTime
			: null;

	/// <summary>
	/// The guest user who plays player <paramref name="player"/>'s hero on this machine: player one is
	/// user 0; player two is user 1 in local co-op and the joiner's own user 0 online.
	/// </summary>
	public static uint UserOfPlayer(int player) =>
		player == 2 && !Refii.Core.Engine_TitleGame.Active.OnlineClient ? 1u : 0u;

	/// <summary>The character id of the hero player <paramref name="player"/> plays, or null before the guest bound one.</summary>
	public static string? CharacterId(int player) => _characters?.CurrentOf(UserOfPlayer(player))?.Character;

	/// <summary>The save the hero player <paramref name="player"/> plays came from ("" for a hero no save holds yet), or null.</summary>
	public static string? CharacterSave(int player) => _characters?.CurrentOf(UserOfPlayer(player))?.Save;

	/// <summary>Every id the hero player <paramref name="player"/> plays has unlocked, ascending.</summary>
	public static List<uint> UnlockedIds(int player) =>
		StoreOf(player) is { } store ? Engine_AchievementCharacters.Ids(store) : new List<uint>();

	/// <summary>Every id the profile has unlocked, ascending.</summary>
	public static List<uint> ProfileUnlockedIds() =>
		_store is { } store ? Engine_AchievementCharacters.Ids(store) : new List<uint>();

	// ---------------------------------------------------------------- viewing another player's

	/// <summary>
	/// The hero player <paramref name="player"/> plays here and the profile total, as a view: what the
	/// page shows for a local player.
	/// </summary>
	public static Engine_AchievementWire.View ViewOf(int player) =>
		new(new HashSet<uint>(UnlockedIds(player)), new HashSet<uint>(ProfileUnlockedIds()));

	/// <summary>
	/// The partner of player <paramref name="player"/>, read-only: in local co-op the other local player's
	/// hero (the profile total is the shared one); online the partner's hero and profile total as their
	/// machine states them (<see cref="PeerView"/> of the co-op partner). Null when there is no partner, or
	/// the partner's machine has stated nothing yet.
	/// </summary>
	public static Engine_AchievementWire.View? PartnerView(int player)
	{
		int other = player == 2 ? 1 : 2;
		if (!Refii.Core.Engine_TitleGame.Active.OnlineClient && CharacterId(other) is not null && CharacterId(player) is not null)
		{
			return ViewOf(other);
		}

		// Online: the partner's machine's statement over RexNet presence, else the one it writes into its
		// player's store on the co-op channel (Engine_AchievementStatement) — that one arrives with the
		// session whether or not their presence reaches this node.
		return PeerView(string.Empty) ??
			(Refii.Core.Engine_TitleGame.Active.PeerJoined ? Stated(OnlinePartnerPlayer)?.View : null);
	}

	/// <summary>The online partner's player: the host's (1) on a joiner, the joiner's (2) on the host.</summary>
	private static int OnlinePartnerPlayer => Refii.Core.Engine_TitleGame.Active.OnlineClient ? 1 : 2;

	/// <summary>
	/// Player <paramref name="player"/>'s achievements as the machine that plays their hero states them on the
	/// co-op channel (<see cref="Engine_AchievementStatement"/>): what the mod surfaces answer, on both machines
	/// alike. Null when no statement is held (no hero played, or none has arrived).
	/// </summary>
	public static Engine_AchievementStatement.Statement? Stated(int player) =>
		player is 1 or 2
			? Engine_AchievementStatement.Parse(Refii.Host.HostBridge.GetPlayerText(player, Engine_AchievementStatement.Key))
			: null;

	/// <summary>The statement this machine makes for player <paramref name="player"/>'s hero ("" while it plays none).</summary>
	public static string StatementOf(int player) =>
		Engine_AchievementStatement.Format(CharacterId(player),
			Engine_AchievementWire.Encode(UnlockedIds(player), ProfileUnlockedIds()));

	private static int _statementFrames;

	/// <summary>
	/// Writes the statement of every player this machine plays into that player's store when the store holds
	/// another (a change, a new session's store), every half second.
	/// </summary>
	private static void StatePlayers()
	{
		if (_characters is null || --_statementFrames > 0)
		{
			return;
		}

		_statementFrames = 15;
		uint owned = Refii.Host.HostBridge.PlayerOwned();
		for (int player = 1; player <= 2; ++player)
		{
			if ((owned & (1u << (player - 1))) == 0)
			{
				continue;
			}

			string text = StatementOf(player);
			if (Refii.Host.HostBridge.GetPlayerText(player, Engine_AchievementStatement.Key) is { } held && held == text)
			{
				continue;
			}

			if (Refii.Host.HostBridge.SetPlayerText(player, Engine_AchievementStatement.Key, text))
			{
				GD.Print($"[achievements] player {player}'s statement on the co-op channel: " +
					(text.Length == 0 ? "no hero" : $"character {CharacterId(player)}, {UnlockedIds(player).Count} unlock(s)"));
			}
		}
	}

	/// <summary>Whether the partner of player <paramref name="player"/> is on this machine (local co-op).</summary>
	public static bool PartnerIsLocal(int player) =>
		!Refii.Core.Engine_TitleGame.Active.OnlineClient && CharacterId(player == 2 ? 1 : 2) is not null &&
		CharacterId(player) is not null;

	/// <summary>
	/// A player's achievements as THEIR machine states them over RexNet presence (the orb's player, or
	/// with an empty id the online co-op partner): read-only, nothing kept. Null when none were heard.
	/// </summary>
	public static Engine_AchievementWire.View? PeerView(string peerId) =>
		Engine_AchievementWire.Decode(Refii.Host.NetBridge.PeerAchievements(peerId));

	private static byte[] _statedWire = Array.Empty<byte>();
	private static bool _wireDirty = true;
	private static int _wireRetryFrames;

	/// <summary>
	/// States this machine's own hero (user 0's) and the profile total to whoever sees this player's
	/// presence — the orb's viewers and the online partner — when it changed; a statement the node
	/// refused (no node yet) is made again every few seconds.
	/// </summary>
	private static void StateWire()
	{
		if (_characters is null || --_wireRetryFrames > 0)
		{
			return;
		}

		_wireRetryFrames = _wireDirty ? 30 : 300;
		Engine_AchievementStore? hero = _characters.StoreOfUser(0);
		byte[] wire = Engine_AchievementWire.Encode(
			hero is null ? Array.Empty<uint>() : Engine_AchievementCharacters.Ids(hero), ProfileUnlockedIds());
		if (wire.AsSpan().SequenceEqual(_statedWire))
		{
			_wireDirty = false;
			return;
		}

		if (Refii.Host.NetBridge.SetPlayerAchievements(wire))
		{
			_statedWire = wire;
			_wireDirty = false;
		}
	}

	private static Engine_AchievementStore? StoreOf(int player) =>
		player is 1 or 2 ? _characters?.StoreOfUser(UserOfPlayer(player)) : null;

	public static int UnlockedCount => Count(IsUnlocked);

	public static int UnlockedCountOf(int player) => Count(id => IsUnlocked(player, id));

	public static int TotalCount => _definitions.Count;

	public static uint UnlockedGamerscore => Score(IsUnlocked);

	public static uint UnlockedGamerscoreOf(int player) => Score(id => IsUnlocked(player, id));

	/// <summary>How many of the title's achievements <paramref name="unlocked"/> says are earned.</summary>
	public static int Count(Func<uint, bool> unlocked)
	{
		int count = 0;
		foreach (AchievementDefinition definition in _definitions)
		{
			if (unlocked(definition.Id))
			{
				count++;
			}
		}

		return count;
	}

	/// <summary>The gamerscore of the title's achievements <paramref name="unlocked"/> says are earned.</summary>
	public static uint Score(Func<uint, bool> unlocked)
	{
		uint score = 0;
		foreach (AchievementDefinition definition in _definitions)
		{
			if (unlocked(definition.Id))
			{
				score += definition.Gamerscore;
			}
		}

		return score;
	}

	public static uint TotalGamerscore
	{
		get
		{
			uint score = 0;
			foreach (AchievementDefinition definition in _definitions)
			{
				score += definition.Gamerscore;
			}

			return score;
		}
	}

	/// <summary>The icon as a texture, cached; null when the title ships none for the id.</summary>
	public static Texture2D? Icon(uint id)
	{
		if (_icons.TryGetValue(id, out Texture2D? cached))
		{
			return cached;
		}

		Texture2D? texture = null;
		if (_byId.TryGetValue(id, out AchievementDefinition definition) &&
			_database?.Image(definition.ImageId) is { Length: > 0 } png)
		{
			var image = new Image();
			if (image.LoadPngFromBuffer(png) == Error.Ok)
			{
				texture = Refii.Textures.Engine_TextureUpload.CreateTexture(image);
			}
		}

		_icons[id] = texture;
		return texture;
	}

	/// <summary>Ids unlocked since the last call — the toast's queue. Loaded unlocks are not news.</summary>
	public static List<uint> TakeNewUnlocks()
	{
		var taken = new List<uint>(_newUnlocks);
		_newUnlocks.Clear();
		return taken;
	}

	/// <summary>How many times the guest asked for the achievements UI since the last call.</summary>
	public static int TakeUiRequests()
	{
		uint requests = Engine_UiSource.Current.GetAchievementsUiRequests();
		if (!_uiRequestsPrimed)
		{
			// A request from before this session's first poll is stale — the page must not open
			// on its own at boot.
			_uiRequestsPrimed = true;
			_seenUiRequests = requests;
			return 0;
		}

		int fresh = (int)(requests - _seenUiRequests);
		_seenUiRequests = requests;
		return Math.Max(fresh, 0);
	}

	/// <summary>Once per frame: load what is not loaded, take the guest's bindings and writes, persist, state the saves' sets.</summary>
	public static void Drive()
	{
		EnsureStore();
		TakeBindings();

		if (_database is null && --_xdbfRetryFrames <= 0)
		{
			// The resource exists only once the module has launched; ask again every so often
			// rather than every frame — the copy is the whole XDBF.
			_xdbfRetryFrames = 60;
			if (Engine_XdbfDatabase.TryParse(Engine_UiSource.Current.GetTitleXdbf()) is { } database)
			{
				_database = database;
				Refii.Core.Engine_TitleIcon.Adopt(database);
				BuildDefinitions(database);
				_tableDirty = true;
				Report = $"{_definitions.Count} definition(s) from the XDBF, " +
					$"{_store!.Count} unlock(s) loaded from {StorePath}" +
					(System.IO.File.Exists(StorePath) ? string.Empty : " (no file yet)");
				GD.Print($"[achievements] {Report}");
			}
		}

		Engine_UiModel.AchievementUnlock[] writes =
			Engine_UiSource.Current.GetAchievementUnlocks(_seenSequence, 64);
		if (writes.Length == 0)
		{
			PushSaveTableIfDirty();
			return;
		}

		bool changed = false;
		foreach (Engine_UiModel.AchievementUnlock write in writes)
		{
			if (write.Sequence > _seenSequence)
			{
				_seenSequence = write.Sequence;
			}

			++_writesSeen;
			if (Unlock(write.UserIndex, write.AchievementId, write.Binding, "the guest wrote it"))
			{
				changed = true;
			}
			else
			{
				// Said once per id: the guest re-states what it holds at every boot, and a write
				// that lands on an unlock already in the store is the healthy case, not news.
				if (_restated.Add(write.AchievementId))
				{
					GD.Print($"[achievements] the guest re-stated {write.AchievementId} " +
						$"({Definition(write.AchievementId)?.Name ?? "no definition yet"}) — already unlocked");
				}
			}
		}

		if (changed)
		{
			Commit();
		}

		PushSaveTableIfDirty();
	}

	/// <summary>The guest's adoption points since the last frame, each resolved to its character.</summary>
	private static void TakeBindings()
	{
		if (_characters is null)
		{
			return;
		}

		foreach (Engine_UiModel.AchievementBinding binding in Engine_UiSource.Current.GetAchievementBindings(_seenBinding))
		{
			_seenBinding = Math.Max(_seenBinding, binding.Serial);
			Engine_AchievementCharacters.Bound bound = _characters.Bind(binding.Serial, binding.User, binding.Save);
			GD.Print($"[achievements] user {binding.User} plays " +
				(binding.Save.Length == 0 ? "a hero no save holds yet" : $"the hero of '{binding.Save}'") +
				$": character {bound.Character}, {_characters.Character(bound.Character).Count} unlock(s)");
			_tableDirty = true;
		}
	}

	/// <summary>
	/// The guest wrote player <paramref name="player"/>'s hero into save <paramref name="save"/>: its
	/// sidecar names that hero's character and its unlocks (Engine_ModSaveGate.Stamp, before the sidecar
	/// is written).
	/// </summary>
	public static void StampSave(SaveSidecar sidecar, int player, string save)
	{
		EnsureStore();
		_characters!.Stamp(sidecar, UserOfPlayer(player), save);
		_tableDirty = true;
	}

	private static void PushSaveTableIfDirty()
	{
		StateWire();
		StatePlayers();
		if (!_tableDirty || _characters is null)
		{
			return;
		}

		_tableDirty = false;
		_wireDirty = true;
		Engine_UiSource.Current.SetSaveAchievementUnlocks(_characters.SaveTable());
	}

	private static int _writesSeen;

	private static readonly HashSet<uint> _restated = new();

	private static void EnsureStore()
	{
		if (_store is not null)
		{
			return;
		}

		_characters = new Engine_AchievementCharacters(Refii.Core.Engine_Title.Active.SaveDirectory,
			Engine_SaveLayout.SaveGames(Refii.Core.Engine_AppRoot.SaveDirectory, Refii.Core.Engine_Title.Active.TitleId),
			StorePath);
		_store = _characters.Ledger;
		if (_store.LoadProblem is { } problem)
		{
			GD.PushWarning($"[achievements] {StorePath} {problem} — starting empty; the file is " +
				"kept as '.unreadable' when the store is next written");
		}
	}

	/// <summary>
	/// THE ONE WAY AN ACHIEVEMENT BECOMES UNLOCKED — the guest's write and the debug channel's
	/// both land here, so what the test tooling exercises is the path the game takes: the store,
	/// the toast's queue, and (through <see cref="Commit"/>) the file and the guest's enumerator.
	/// False when it was unlocked already.
	/// </summary>
	/// <remarks>
	/// The unlock goes to the hero of the binding it was made under and to the profile total. A hero
	/// earning what it did not have toasts; a write from a user the guest never bound (the boot's
	/// re-statement, before any hero is loaded) toasts only when the profile had not earned it.
	/// </remarks>
	private static bool Unlock(uint userIndex, uint achievementId, ulong binding, string why)
	{
		if (_characters is null)
		{
			return false;
		}

		(bool forCharacter, bool forProfile) =
			_characters.Record(userIndex, achievementId, binding, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
		if (!forCharacter && !forProfile)
		{
			return false;
		}

		if ((binding != 0 ? forCharacter : forProfile) && !_newUnlocks.Contains(achievementId))
		{
			_newUnlocks.Add(achievementId);
		}

		Engine_AchievementCharacters.Bound bound = _characters.Resolve(binding, userIndex);
		GD.Print($"[achievements] unlocked {achievementId} " +
			$"({Definition(achievementId)?.Name ?? "no definition yet"}) for user {userIndex}'s hero " +
			$"(character {bound.Character}{(bound.Save.Length == 0 ? string.Empty : ", save '" + bound.Save + "'")})" +
			$"{(forProfile ? ", new to the profile" : string.Empty)} — {why}");
		return true;
	}

	/// <summary>Writes the stores that changed and states every save's set down to the guest.</summary>
	private static void Commit()
	{
		if (_characters is null || _store is null)
		{
			return;
		}

		List<string> failed = _characters.Save();
		_lastSaved = failed.Count == 0
			? $"{_store.Count} unlock(s) on the profile at {DateTime.Now:HH:mm:ss}"
			: "FAILED: " + string.Join("; ", failed);
		foreach (string failure in failed)
		{
			GD.PushWarning($"[achievements] could not write {failure}");
		}

		_tableDirty = true;
		PushSaveTableIfDirty();
	}

	private static string _lastSaved = "never this session";

	/// <summary>Unlocks one achievement as the guest's write would. For the debug channel.</summary>
	public static bool DebugUnlock(uint achievementId)
	{
		EnsureStore();
		uint user = UserOfPlayer(1);
		if (!Unlock(user, achievementId, _characters!.CurrentOf(user)?.Serial ?? 0ul, "the debug channel asked"))
		{
			return false;
		}

		Commit();
		return true;
	}

	/// <summary>Re-locks one achievement, or all of them with id 0. Returns how many went.</summary>
	public static int DebugLock(uint achievementId)
	{
		EnsureStore();
		int removed = _characters!.Remove(achievementId);
		if (removed > 0)
		{
			_restated.Clear();
			GD.Print($"[achievements] re-locked {(achievementId == 0u ? "everything" : achievementId.ToString())} " +
				$"({removed} unlock(s)) — the debug channel asked");
			Commit();
		}

		return removed;
	}

	/// <summary>The whole system's state, for the debug channel.</summary>
	public static Dictionary<string, object?> Describe(bool rows)
	{
		EnsureStore();
		var state = new Dictionary<string, object?>
		{
			["definitionsLoaded"] = DefinitionsLoaded,
			["definitions"] = _definitions.Count,
			["unlocked"] = UnlockedCount,
			["gamerscore"] = $"{UnlockedGamerscore}/{TotalGamerscore}",
			["playerOne"] = new Dictionary<string, object?>
			{
				["character"] = CharacterId(1), ["save"] = CharacterSave(1), ["unlocked"] = UnlockedCountOf(1),
			},
			["playerTwo"] = new Dictionary<string, object?>
			{
				["character"] = CharacterId(2), ["save"] = CharacterSave(2), ["unlocked"] = UnlockedCountOf(2),
			},
			["bindingsSeen"] = _seenBinding,
			["storeLoadProblems"] = _characters!.LoadProblems.ToArray(),
			["storePath"] = StorePath,
			["storeFileExists"] = System.IO.File.Exists(StorePath),
			["storeUnlocks"] = _store!.Count,
			["storeLoadProblem"] = _store.LoadProblem,
			["lastSaved"] = _lastSaved,

			// The seam: how many guest writes have crossed the bridge this session. Zero while
			// the guest's own log shows XGIUserWriteAchievements is a seam that is not crossing.
			["guestWritesSeen"] = _writesSeen,
			["guestWriteSequence"] = _seenSequence,
			["toastsQueued"] = _newUnlocks.Count,
		};

		if (rows)
		{
			var list = new List<Dictionary<string, object?>>();
			foreach (AchievementDefinition definition in _definitions)
			{
				list.Add(new Dictionary<string, object?>
				{
					["id"] = definition.Id,
					["name"] = definition.Name,
					["gamerscore"] = definition.Gamerscore,
					["unlocked"] = IsUnlocked(definition.Id),
					["when"] = UnlockedAt(definition.Id)?.ToString("yyyy-MM-dd HH:mm:ss"),
					["playerOne"] = IsUnlocked(1, definition.Id),
					["playerTwo"] = IsUnlocked(2, definition.Id),
					["hasIcon"] = _database?.Image(definition.ImageId) is { Length: > 0 },
				});
			}

			state["achievements"] = list;

			// An unlock the title does not define is still an unlock — and a sign of a bad id.
			var unknown = new List<uint>();
			foreach (Engine_AchievementStore.Unlock unlock in _store.Unlocks)
			{
				if (!_byId.ContainsKey(unlock.AchievementId))
				{
					unknown.Add(unlock.AchievementId);
				}
			}

			state["unlockedWithoutDefinition"] = unknown;
		}

		return state;
	}

	private static void BuildDefinitions(Engine_XdbfDatabase database)
	{
		_definitions.Clear();
		_byId.Clear();
		_icons.Clear();

		uint language = database.DefaultLanguage;
		if (!database.HasLanguage(language))
		{
			language = Engine_XdbfDatabase.LanguageEnglish;
		}

		foreach (Engine_XdbfDatabase.Achievement entry in database.Achievements)
		{
			var definition = new AchievementDefinition(
				entry.Id, entry.ImageId, entry.Gamerscore, entry.Flags,
				database.String(language, entry.LabelId),
				database.String(language, entry.DescriptionId),
				database.String(language, entry.UnachievedId));
			_definitions.Add(definition);
			_byId[entry.Id] = definition;
		}
	}
}
