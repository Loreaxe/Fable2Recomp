using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Refii.Save;

namespace Refii.Achievements;

/// <summary>
/// ACHIEVEMENTS PER CHARACTER: each hero earns its own. A character is the hero a save holds — Refii's
/// own id in the save's sidecar (<see cref="SaveSidecar.CharacterLayer"/>; the guest's SaveUID changes at
/// every write) — and its unlocks are its own store, <c>&lt;profile&gt;/&lt;Game&gt;/achievements/&lt;id&gt;.refii</c>
/// (the <see cref="Engine_AchievementStore"/> codec), mirrored into the sidecar so a moved or copied save
/// carries them. The profile's <c>achievements.refii</c> is the PROFILE TOTAL: every unlock any of the
/// profile's characters earned, and every unlock earned before characters existed.
/// </summary>
/// <remarks>
/// <para>One profile, one save set: both local players pick their heroes from the same saves. The
/// guest's adoption points state which save each user's hero comes from (<see cref="Bind"/>, native
/// <c>refii::achievement::BindUser</c>); a binding to a save resolves to the character in that save's
/// sidecar, a save without one (a vanilla save, a save back from Xenia or a 360) becomes a new
/// character, and a binding with no save is a new hero whose character is given the first save the
/// guest writes it into (<see cref="Stamp"/>).</para>
/// <para>The guest reads its heroes' unlocks back per user and synchronously, so every save's set is
/// stated down ahead of time (<see cref="SaveTable"/>).</para>
/// <para>Pure: no Godot type; the title's folder in the profile is handed in.</para>
/// </remarks>
public sealed class Engine_AchievementCharacters
{
	public const string FolderName = "achievements";
	public const string StoreExtension = ".refii";

	private readonly string _gameDirectory;
	private readonly string _saveGames;
	private readonly string _ledgerPath;
	private readonly Func<string> _mintId;
	private readonly Dictionary<string, Engine_AchievementStore> _characters = new();
	private readonly HashSet<string> _dirtyCharacters = new();
	private readonly Dictionary<ulong, Bound> _bindings = new();
	private readonly Dictionary<uint, ulong> _current = new();
	private readonly Dictionary<uint, string> _unboundCharacter = new();
	private bool _ledgerDirty;

	/// <summary>One binding as resolved: its save and character.</summary>
	public sealed record Bound(ulong Serial, uint User, string Save, string Character);

	/// <param name="gameDirectory">The title's folder in the profile (<c>Engine_Title.SaveDirectory</c>).</param>
	/// <param name="saveGames">The title's save packages' folder in the profile (<c>Engine_SaveLayout.SaveGames</c>).</param>
	/// <param name="ledgerPath">The profile total's file (<c>Engine_Achievements.StorePath</c>).</param>
	/// <param name="mintId">A new character id (tests pass a counter).</param>
	public Engine_AchievementCharacters(string gameDirectory, string saveGames, string ledgerPath, Func<string>? mintId = null)
	{
		_gameDirectory = gameDirectory;
		_saveGames = saveGames;
		_ledgerPath = ledgerPath;
		_mintId = mintId ?? (() => Guid.NewGuid().ToString("N"));
		Ledger = Load(ledgerPath);
	}

	/// <summary>The profile total.</summary>
	public Engine_AchievementStore Ledger { get; }

	/// <summary>Why a store was not read, by path; empty when every file read.</summary>
	public List<string> LoadProblems { get; } = new();

	public string CharacterPath(string characterId) =>
		Path.Combine(_gameDirectory, FolderName, characterId + StoreExtension);

	/// <summary>A character id is a lowercase hex Guid: safe as a file name, nothing else is taken.</summary>
	public static bool IsCharacterId(string? id) =>
		id is { Length: 32 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

	/// <summary>A character's store, loaded on first ask.</summary>
	public Engine_AchievementStore Character(string characterId)
	{
		if (_characters.TryGetValue(characterId, out Engine_AchievementStore? store))
		{
			return store;
		}

		store = IsCharacterId(characterId) ? Load(CharacterPath(characterId)) : new Engine_AchievementStore();
		_characters[characterId] = store;
		return store;
	}

	private Engine_AchievementStore Load(string path)
	{
		Engine_AchievementStore store = Engine_AchievementStore.Load(path);
		if (store.LoadProblem is { } problem)
		{
			LoadProblems.Add($"{path} {problem}");
		}

		return store;
	}

	/// <summary>The folder of save <paramref name="save"/>, or null.</summary>
	public string? SaveDirectory(string save)
	{
		if (save.Length == 0 || save.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || save is "." or "..")
		{
			return null;
		}

		string candidate = Path.Combine(_saveGames, save);
		return Directory.Exists(candidate) ? candidate : null;
	}

	/// <summary>
	/// A guest adoption point. A save's sidecar names its character (one is minted and written into a
	/// sidecar that names none, and the save's mirrored unlocks join its store); no save is a new hero.
	/// Returns the binding as resolved.
	/// </summary>
	public Bound Bind(ulong serial, uint user, string save)
	{
		string character;
		if (SaveDirectory(save) is { } directory)
		{
			SaveSidecar sidecar = SaveSidecar.Read(directory);
			if (sidecar.Refii?.Character is { } held && IsCharacterId(held.Id))
			{
				character = held.Id;
				Engine_AchievementStore store = Character(character);
				foreach (uint id in held.Achievements)
				{
					if (!store.IsUnlocked(id) && store.Add(user, id, 0))
					{
						_dirtyCharacters.Add(character);
					}
				}
			}
			else
			{
				character = _mintId();
				sidecar.Refii ??= new SaveSidecar.RefiiLayer { Written = SaveSidecar.Now() };
				sidecar.Refii.Character = new SaveSidecar.CharacterLayer { Id = character, Name = save };
				try
				{
					sidecar.Write(directory);
				}
				catch (Exception e) when (e is IOException or UnauthorizedAccessException)
				{
					LoadProblems.Add($"{directory}: its character could not be written ({e.Message})");
				}
			}
		}
		else
		{
			character = _mintId();
		}

		var bound = new Bound(serial, user, save, character);
		_bindings[serial] = bound;
		_current[user] = serial;
		return bound;
	}

	/// <summary>The binding a write was made under; a never-bound user (serial 0) has a hero of its own until it is bound.</summary>
	public Bound Resolve(ulong serial, uint user)
	{
		if (serial != 0 && _bindings.TryGetValue(serial, out Bound? bound))
		{
			return bound;
		}

		if (!_unboundCharacter.TryGetValue(user, out string? character))
		{
			character = _mintId();
			_unboundCharacter[user] = character;
		}

		return new Bound(0, user, string.Empty, character);
	}

	/// <summary>The binding user <paramref name="user"/>'s hero is under now, or null before any.</summary>
	public Bound? CurrentOf(uint user) =>
		_current.TryGetValue(user, out ulong serial) && _bindings.TryGetValue(serial, out Bound? bound) ? bound : null;

	/// <summary>
	/// A guest write: the unlock goes to the character of the binding it was made under and to the
	/// profile total. (NewForCharacter, NewForProfile).
	/// </summary>
	public (bool NewForCharacter, bool NewForProfile) Record(uint user, uint achievementId, ulong binding, long unixSeconds)
	{
		// New BY ID: a store keys its records by (user, id), and a hero is one hero whichever user plays it.
		Bound bound = Resolve(binding, user);
		Engine_AchievementStore store = Character(bound.Character);
		bool character = !store.IsUnlocked(achievementId) && store.Add(user, achievementId, unixSeconds);
		bool profile = !Ledger.IsUnlocked(achievementId) && Ledger.Add(user, achievementId, unixSeconds);
		if (character)
		{
			_dirtyCharacters.Add(bound.Character);
		}

		_ledgerDirty |= profile;
		return (character, profile);
	}

	/// <summary>
	/// The guest wrote user <paramref name="user"/>'s hero into save <paramref name="save"/>: the save now
	/// holds that hero's character, with its unlocks. A hero no save held takes this save as its own
	/// (later bindings to it find the same character).
	/// </summary>
	public void Stamp(SaveSidecar sidecar, uint user, string save)
	{
		Bound bound = CurrentOf(user) ?? Resolve(0, user);
		if (bound.Save.Length == 0 && bound.Serial != 0)
		{
			_bindings[bound.Serial] = bound with { Save = save };
		}

		sidecar.Refii ??= new SaveSidecar.RefiiLayer();
		sidecar.Refii.Character = new SaveSidecar.CharacterLayer
		{
			Id = bound.Character,
			Name = sidecar.Refii.Character is { Id: var id, Name: var name } && id == bound.Character ? name : save,
			Achievements = Ids(Character(bound.Character)),
		};
	}

	/// <summary>The ids a store holds, ascending.</summary>
	public static List<uint> Ids(Engine_AchievementStore store) =>
		store.Unlocks.Select(u => u.AchievementId).Distinct().OrderBy(id => id).ToList();

	/// <summary>
	/// Every save as the guest's per-user enumerator reads it: one line per save whose sidecar names a
	/// character, <c>save\tid:filetime,...</c>.
	/// </summary>
	public string SaveTable()
	{
		var text = new StringBuilder();
		if (!Directory.Exists(_saveGames))
		{
			return string.Empty;
		}

		foreach (string save in Directory.EnumerateDirectories(_saveGames).OrderBy(d => d, StringComparer.Ordinal))
		{
			if (SaveSidecar.Read(save).Refii?.Character is not { } held || !IsCharacterId(held.Id))
			{
				continue;
			}

			text.Append(Path.GetFileName(save)).Append('\t');
			bool first = true;
			foreach (Engine_AchievementStore.Unlock unlock in Character(held.Id).Unlocks.OrderBy(u => u.AchievementId))
			{
				text.Append(first ? string.Empty : ",").Append(unlock.AchievementId.ToString(CultureInfo.InvariantCulture))
					.Append(':').Append(FileTime(unlock.UnlockedUnixSeconds).ToString(CultureInfo.InvariantCulture));
				first = false;
			}

			text.Append('\n');
		}

		return text.ToString();
	}

	/// <summary>Unix seconds as a FILETIME (0 stays 0: "now" at the seam).</summary>
	public static ulong FileTime(long unixSeconds) =>
		unixSeconds > 0 ? ((ulong)unixSeconds * 10_000_000ul) + 116444736000000000ul : 0ul;

	/// <summary>Writes every store that changed and the profile total. Returns the paths that could not be written.</summary>
	public List<string> Save()
	{
		var failed = new List<string>();
		foreach (string id in _dirtyCharacters.ToList())
		{
			if (!IsCharacterId(id))
			{
				_dirtyCharacters.Remove(id);
				continue;
			}

			string path = CharacterPath(id);
			try
			{
				_characters[id].Save(path);
				_dirtyCharacters.Remove(id);
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException)
			{
				failed.Add($"{path}: {e.Message}");
			}
		}

		if (_ledgerDirty)
		{
			try
			{
				Ledger.Save(_ledgerPath);
				_ledgerDirty = false;
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException)
			{
				failed.Add($"{_ledgerPath}: {e.Message}");
			}
		}

		return failed;
	}

	/// <summary>The store user <paramref name="user"/>'s hero earns into now (its binding's character), or null before any binding.</summary>
	public Engine_AchievementStore? StoreOfUser(uint user) =>
		CurrentOf(user) is { } bound ? Character(bound.Character) :
		_unboundCharacter.TryGetValue(user, out string? id) ? Character(id) : null;

	/// <summary>Forgets an id (0: every id) everywhere this session holds it (the debug channel's re-lock). How many unlocks went.</summary>
	public int Remove(uint achievementId)
	{
		int removed = 0;
		foreach ((string id, Engine_AchievementStore store) in _characters)
		{
			int before = store.Count;
			if (achievementId == 0 ? store.Clear() > 0 : store.Remove(achievementId))
			{
				_dirtyCharacters.Add(id);
			}

			removed += before - store.Count;
		}

		int had = Ledger.Count;
		if (achievementId == 0 ? Ledger.Clear() > 0 : Ledger.Remove(achievementId))
		{
			_ledgerDirty = true;
		}

		return removed + had - Ledger.Count;
	}

	/// <summary>Marks the profile total to be written (a store's own change made outside <see cref="Record"/>).</summary>
	public void TouchLedger() => _ledgerDirty = true;
}
