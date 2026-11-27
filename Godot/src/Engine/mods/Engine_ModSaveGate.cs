using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using Refii.Host;
using Refii.Save;
using Refii.Ui;

namespace Refii.Mods;

/// <summary>
/// Mod filtering on the save system: every save is stamped with the mod set it was written under,
/// a save loaded with some of those mods missing is filtered of their records first, and a save
/// can be copied vanilla-only for a console or Xenia.
/// </summary>
/// <remarks>
/// THE SEAMS, all the guest's own statements:
/// - THE STAMP: after the guest closes a save package (`rex_lhSaveManager_WritePackage` /
///   `WriteHeroPackage`, r4 the package name, r5 the player), the player-state hooks post
///   `REFII_MOD_HOST_SAVE_KIND` with the name and player; <see cref="OnSaveWritten"/> writes the
///   sidecar (<see cref="SaveSidecar"/>) beside it: the `refii` layer and the `mods` layer with the
///   running set (<see cref="ModSaveStamp.Current"/>). Every save path — manual, autosave,
///   checkpoint, co-op, player two's own package — goes through those two writers.
/// - THE GATE: the front end's own start call (`rex_lhFrontEnd_SetStartGameTarget`, event 99
///   `start_game`, r4 the save name) runs this class's verdict on the guest thread. A save
///   stamped with mods of its own is REFUSED once (the event's cancel) and the player is told
///   on the host page book (<see cref="Engine_UiHostBoxes.AskPage"/>) which mods it was made
///   with and which are not enabled now; on Continue held records of mods enabled again are
///   put back, the save is filtered when any are missing, and the event's own `commit` — the
///   pump action the start ran under: `LoadSave:N` from the fan, or "Load Game" when the one
///   save commits directly — is posted again and let through once, by (save, commit). Back
///   leaves the front end where it was — the fan — and the save's files untouched.
/// - THE FILTER (<see cref="ModSaveFilter"/>) rewrites `mainsave.bin` in the save's folder with
///   the missing mods' records out, the player store without their keys, and keeps everything it
///   removed under `mods.held` in the sidecar; the unfiltered main save is kept beside it
///   (<see cref="UnfilteredName"/>) until the next save replaces both. A mod enabled again gets
///   its held records put back on the next load, before the guest reads the file.
/// - RefiiModSave keys of a missing mod are dropped in the game VM once the save's Lua table is
///   loaded (`save_loaded`, event 4), through the host's `run_lua` command.
///
/// Player two's hero from a picked save is read host-side (<c>CoopHeroKit</c>) and filtered with
/// the same record sets by the title; online, a joiner's own save loads through
/// this gate on the joiner's machine.
///
/// What the guest does with a record it cannot resolve was read in the binary: it tolerates
/// one (see <see cref="ModSaveStamp.NoticeText"/>), so the filter is what keeps dead entries out
/// of a save, not what keeps the game up; it is not optional either way.
/// </remarks>
public static class ModSaveGate
{
	/// <summary>REFII_MOD_HOST_SAVE_KIND: a written package, `player=N,name='X'`.</summary>
	public const int SaveWrittenKind = 1004;

	/// <summary>The front end's start (`start_game`), where the verdict runs.</summary>
	public const int StartGameKind = 99;

	/// <summary>A save's Lua table loaded (`save_loaded`), where RefiiModSave keys are dropped.</summary>
	public const int SaveLoadedKind = 4;

	/// <summary>The unfiltered `mainsave.bin` kept beside a filtered one until the next save.</summary>
	public const string UnfilteredName = "refii_mainsave_unfiltered.bin";

	/// <summary>The active save profile (the guest's user_data_root), by convention; the title's folder is below it.</summary>
	public static string UserDataRoot { get; set; } =
		Refii.Core.Engine_AppRoot.SaveDirectory;

	private static readonly object Lock = new();

	// What the verdict refused and the box is asking about.
	private sealed record Pending(
		string Save, string Directory, ModSaveStamp.Diff Diff, string Commit,
		Engine_ExternalModManager.Diff? External, ModFingerprint Current);

	private static Pending? _pending;

	// The one start let through after Continue: the re-posted commit for that save. A statement
	// of the same save under another commit (the guest's own tick carries "") does not consume it.
	private static (string Save, string Commit)? _allowedOnce;
	private static string _luaClear = string.Empty;

	/// <summary>Whether the host wants `save_loaded` posted: a filtered load is waiting to drop keys.</summary>
	public static bool WantsSaveLoaded
	{
		get
		{
			lock (Lock)
			{
				return _luaClear.Length != 0;
			}
		}
	}

	// ---------------------------------------------------------------- the gate

	/// <summary>
	/// The verdict on <c>start_game</c>, on the guest thread: true to refuse the start. A save
	/// stamped with mods of its own is refused once and the player told; one with held records
	/// for mods enabled again has them restored here, before the guest reads the file.
	/// </summary>
	public static bool Verdict(int kind, IReadOnlyList<KeyValuePair<string, object>> fields)
	{
		if (kind != StartGameKind)
		{
			return false;
		}

		string save = Field(fields, "save");
		if (save.Length == 0)
		{
			return false;
		}

		string commit = CommitOf(fields);
		lock (Lock)
		{
			if (_allowedOnce is { } allowed && string.Equals(allowed.Save, save, StringComparison.Ordinal) &&
				string.Equals(allowed.Commit, commit, StringComparison.Ordinal))
			{
				_allowedOnce = null;
				return false;
			}
		}

		string? directory = Engine_SaveDirectory.Find(UserDataRoot, save);
		if (directory is null)
		{
			return false;
		}

		SaveSidecar sidecar = SaveSidecar.Read(directory);
		if (!sidecar.Modded)
		{
			return false;
		}

		// A save made under Archon's Toolbox alone has no mods layer; the page still names its mods.
		SaveSidecar.ModsLayer stamp = sidecar.Mods ?? new SaveSidecar.ModsLayer();
		ModFingerprint current;
		try
		{
			current = ModFingerprint.Current();
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			GD.PushWarning($"[mod-save] the enabled set could not be read ({e.Message}); '{save}' loads unfiltered");
			return false;
		}

		ModSaveStamp.Diff diff = ModSaveStamp.Compare(stamp, current);

		// A SAVE WITH NO MODS OF ITS OWN loads as it is: a session mod is the host's, not the
		// save's, and says nothing about what the save holds.
		int own = 0;
		foreach (SaveSidecar.ModEntry mod in stamp.Set)
		{
			if (!mod.Session)
			{
				++own;
			}
		}

		// AN EXTERNAL MANAGER'S MODS are the save's own too (the title's sidecar layer — Fable 2's
		// Archon's Toolbox): compared with the install now, never filtered.
		Engine_ExternalModManager? external = Refii.Core.Engine_TitleGame.Active.ExternalMods;
		string? externalLayer = external is not null && sidecar.Layers.TryGetValue(external.Layer, out string? layer)
			? layer
			: null;
		Engine_ExternalModManager.Diff? externalDiff = externalLayer is null ? null : external!.Compare(externalLayer);
		int externalOwn = externalLayer is null ? 0 : external!.ModCount(externalLayer);
		if (own == 0 && externalOwn == 0)
		{
			// Nothing to ask, so held records of mods enabled again go back here, inside the
			// start call, before the guest reads the file. A save that is asked about has them
			// put back on Continue instead (never before the answer).
			RestoreHeldQuietly(save, directory, sidecar, current);
			return false;
		}

		if (diff.Added.Count != 0 || diff.Changed.Count != 0)
		{
			GD.Print($"[mod-save] '{save}': {diff.Added.Count} mod(s) new since it was saved, {diff.Changed.Count} changed — loads as it is, re-stamped on the next save");
		}

		// A TITLE THAT CANNOT ASK (no host page book) loads it as Continue would: held records of mods
		// enabled again back, the missing mods' values held out of the save, and the start let through.
		if (!Refii.Core.Engine_TitleGame.Active.AsksHostQuestions)
		{
			RestoreHeldQuietly(save, directory, sidecar, current);
			bool missing = diff.Missing.Count != 0;
			if (missing)
			{
				try
				{
					FilterSave(directory, diff);
				}
				catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
				{
					GD.PushError($"[mod-save] '{save}' could not be filtered: {e.Message} — loads as it is");
				}

				lock (Lock)
				{
					_luaClear = ModSaveFilter.LuaClearKeys(diff.MissingKeys());
				}
			}

			GD.Print($"[mod-save] '{save}' was saved under {own} mod(s), {diff.Missing.Count} not enabled now — " +
				(missing ? "their values held out" : "loads as it is") + " (this title asks no question)");
			return false;
		}

		// EVERY STAMPED SAVE IS TOLD ABOUT: the player sees which mods it was made with before it
		// loads, and which of them are not enabled now. Refused once, asked on the page book, and
		// let through on Continue.
		var pending = new Pending(save, directory, diff, commit, externalDiff, current);
		lock (Lock)
		{
			// THE FRONT END STATES THE SAME START TWICE: once under the pump's action, with the
			// commit to post again, and once more on its own tick with none. Taking the second as
			// the pending load replaced `LoadSave:N` with "", so Yes on the box had nothing to
			// post and the player was told to choose once more instead of loading (2026-10-26).
			// The refusal stands; the box already up is the one answered.
			if (_pending is { } asked && string.Equals(asked.Save, save, StringComparison.Ordinal) &&
				pending.Commit.Length == 0 && asked.Commit.Length != 0)
			{
				GD.Print($"[mod-save] '{save}' stated again with no commit while its box is up — keeping '{asked.Commit}'");
				return true;
			}
			_pending = pending;
		}

		GD.Print($"[mod-save] '{save}' was saved under {own} mod(s), {diff.Missing.Count} not enabled now" +
			(externalOwn == 0 ? string.Empty : $"; {externalOwn} {external!.Name} mod(s), {externalDiff!.Missing} not running now{(externalDiff.Present ? string.Empty : $" ({external.Name} not applied)")}") +
			$" — refused, asking (commit '{pending.Commit}')");
		Callable.From(() => ShowNotice(stamp)).CallDeferred();
		return true;
	}

	/// <summary>
	/// The action posted again once the box is answered: the event's `commit`, the guest-tick
	/// pump's name for the action the start ran under (`LoadSave:N` from the fan, "Load Game"
	/// when the one save commits directly). "" for a start the pump did not run — nothing is
	/// posted then, and the player is told to choose once more; a commit remembered host-side
	/// could be a previous load's, so it is never read from anywhere else.
	/// </summary>
	public static string CommitOf(IReadOnlyList<KeyValuePair<string, object>> fields) => Field(fields, "commit");

	private static string Field(IReadOnlyList<KeyValuePair<string, object>> fields, string name)
	{
		string found = string.Empty;
		foreach ((string key, object value) in fields)
		{
			if (key == name && value is string s)
			{
				found = s;
			}
		}

		return found;
	}

	private static void ShowNotice(SaveSidecar.ModsLayer stamp)
	{
		Pending? pending;
		lock (Lock)
		{
			pending = _pending;
		}

		if (pending is null)
		{
			return;
		}

		Engine_UiHostBoxes.AskPage(ModSaveStamp.NoticeTitle, ModSaveStamp.NoticeText(stamp, pending.Diff, pending.External),
			onYes: () => Continue(pending), onNo: () => Cancel(pending), key: "mod-save");
	}

	private static void Cancel(Pending pending)
	{
		lock (Lock)
		{
			if (ReferenceEquals(_pending, pending))
			{
				_pending = null;
			}
		}

		GD.Print($"[mod-save] '{pending.Save}' not loaded — the player cancelled");
	}

	private static void Continue(Pending pending)
	{
		lock (Lock)
		{
			if (ReferenceEquals(_pending, pending))
			{
				_pending = null;
			}
		}

		// The answer is in: first held records of mods enabled again go back, then the save is
		// filtered of the missing ones; a save whose mods are all enabled loads as it is.
		RestoreHeldQuietly(pending.Save, pending.Directory, SaveSidecar.Read(pending.Directory), pending.Current);
		bool filter = pending.Diff.Missing.Count != 0;
		if (filter)
		{
			try
			{
				FilterSave(pending.Directory, pending.Diff);
			}
			catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
			{
				GD.PushError($"[mod-save] '{pending.Save}' could not be filtered: {e.Message} — not loaded");
				Engine_UiHostBoxes.Tell("The save could not be filtered:\n" + e.Message);
				return;
			}
		}

		if (pending.Commit.Length == 0)
		{
			GD.PushWarning($"[mod-save] '{pending.Save}' filtered, but no commit to post again — choose it once more");
			return;
		}

		lock (Lock)
		{
			_allowedOnce = (pending.Save, pending.Commit);
			_luaClear = filter ? ModSaveFilter.LuaClearKeys(pending.Diff.MissingKeys()) : string.Empty;
		}

		HostBridge.PostFrontEndAction(pending.Commit);
		GD.Print($"[mod-save] '{pending.Save}' {(filter ? "filtered" : "unchanged")} — '{pending.Commit}' posted again");
	}

	// ---------------------------------------------------------------- the files

	/// <summary>
	/// Filters the save in <paramref name="directory"/> of the missing mods' records: the main
	/// save rewritten (the unfiltered one kept beside it), the player store without their keys,
	/// everything removed held in the sidecar for restore.
	/// </summary>
	public static void FilterSave(string directory, ModSaveStamp.Diff diff)
	{
		HashSet<uint> records = diff.MissingRecords();
		List<string> keys = diff.MissingKeys();
		SaveSidecar sidecar = SaveSidecar.Read(directory);
		sidecar.Mods ??= new SaveSidecar.ModsLayer();

		string main = Path.Combine(directory, "mainsave.bin");
		SaveSidecar.HeldRecords held = new();
		if (records.Count != 0 && File.Exists(main) && Refii.Core.Engine_TitleGame.Active.FiltersSaveRecords)
		{
			SaveStream stream = SaveStream.ReadFile(main);
			if (ModSaveFilter.RecordsIn(stream).Overlaps(records))
			{
				held = ModSaveFilter.Filter(stream, records);
				string unfiltered = Path.Combine(directory, UnfilteredName);
				if (!File.Exists(unfiltered))
				{
					File.Copy(main, unfiltered);
				}

				stream.WriteFile(main);
			}
		}

		string store = Path.Combine(directory, SaveSidecar.PlayerStateFiles[0]);
		Dictionary<string, string> storeHeld = new();
		if (keys.Count != 0 && File.Exists(store))
		{
			(string text, storeHeld) = ModSaveFilter.FilterPlayerStore(File.ReadAllText(store), keys);
			if (storeHeld.Count != 0)
			{
				File.WriteAllText(store, text);
			}
		}

		// Held per mod: an item goes under the mod whose record set names it; a key under the mod
		// whose prefix it carries.
		foreach (SaveSidecar.ModEntry mod in diff.Missing)
		{
			var own = new SaveSidecar.HeldRecords { Title = mod.Title, Version = mod.Version };
			var records0 = new HashSet<uint>(mod.Records);
			foreach (SaveNode item in held.Inventory)
			{
				if (item.Child("RecordID")?.Text is uint r && records0.Contains(r)) own.Inventory.Add(item);
			}

			foreach ((uint record, SaveNode modifier) in held.Worn)
			{
				if (records0.Contains(record)) own.Worn.Add((record, modifier));
			}

			if (records0.Contains(held.Melee)) own.Melee = held.Melee;
			if (records0.Contains(held.Ranged)) own.Ranged = held.Ranged;
			var values = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach ((string key, string value) in storeHeld)
			{
				foreach (string prefix in mod.Keys.Count != 0 ? mod.Keys : ModSaveStamp.KeyPrefixes(mod.Name))
				{
					if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
					{
						values[key] = value;
					}
				}
			}

			if (values.Count != 0)
			{
				own.Player["1"] = values;
			}

			if (!own.IsEmpty)
			{
				if (sidecar.Mods.Held.TryGetValue(mod.Name, out SaveSidecar.HeldRecords? before))
				{
					own.Inventory.InsertRange(0, before.Inventory);
					own.Worn.InsertRange(0, before.Worn);
					if (own.Melee == 0) own.Melee = before.Melee;
					if (own.Ranged == 0) own.Ranged = before.Ranged;
					foreach ((string player, Dictionary<string, string> kept) in before.Player)
					{
						if (!own.Player.TryGetValue(player, out Dictionary<string, string>? mine))
						{
							own.Player[player] = kept;
							continue;
						}

						foreach ((string key, string value) in kept) mine.TryAdd(key, value);
					}
				}

				sidecar.Mods.Held[mod.Name] = own;
			}
		}

		sidecar.Write(directory);
		GD.Print($"[mod-save] filtered '{Path.GetFileName(directory)}': {held.Inventory.Count} item(s), {held.Worn.Count} worn, " +
			$"{(held.Melee != 0 ? 1 : 0) + (held.Ranged != 0 ? 1 : 0)} weapon id(s), {storeHeld.Count} store key(s) held for {diff.Missing.Count} mod(s)");
	}

	/// <summary><see cref="RestoreHeld"/> with a file failure reported, not thrown.</summary>
	private static void RestoreHeldQuietly(string save, string directory, SaveSidecar sidecar, ModFingerprint current)
	{
		try
		{
			RestoreHeld(directory, sidecar, current);
		}
		catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
		{
			GD.PushWarning($"[mod-save] held records of '{save}' could not be restored: {e.Message}");
		}
	}

	/// <summary>
	/// Puts back the held records of every mod in <paramref name="current"/>, into the main save
	/// and the player store, and drops the hold.
	/// </summary>
	private static void RestoreHeld(string directory, SaveSidecar sidecar, ModFingerprint current)
	{
		if (sidecar.Mods is not { } mods || mods.Held.Count == 0)
		{
			return;
		}

		var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (ModFingerprint.Mod mod in current.Mods)
		{
			enabled.Add(mod.Name);
		}

		var restored = new List<string>();
		SaveStream? stream = null;
		string main = Path.Combine(directory, "mainsave.bin");
		string store = Path.Combine(directory, SaveSidecar.PlayerStateFiles[0]);
		string storeText = File.Exists(store) ? File.ReadAllText(store) : string.Empty;
		bool storeChanged = false;
		foreach ((string name, SaveSidecar.HeldRecords held) in mods.Held)
		{
			if (!enabled.Contains(name))
			{
				continue;
			}

			if ((held.Inventory.Count != 0 || held.Worn.Count != 0 || held.Melee != 0 || held.Ranged != 0) && File.Exists(main) &&
				Refii.Core.Engine_TitleGame.Active.FiltersSaveRecords)
			{
				stream ??= SaveStream.ReadFile(main);
				if (!ModSaveFilter.Restore(stream, held))
				{
					GD.PushWarning($"[mod-save] '{Path.GetFileName(directory)}' has no hero to give {name}'s held records back to");
					continue;
				}
			}

			if (held.Player.TryGetValue("1", out Dictionary<string, string>? values) && values.Count != 0)
			{
				storeText = ModSaveFilter.RestorePlayerStore(storeText, values);
				storeChanged = true;
			}

			restored.Add(name);
		}

		if (restored.Count == 0)
		{
			return;
		}

		if (stream is not null)
		{
			stream.WriteFile(main);
		}

		if (storeChanged)
		{
			File.WriteAllText(store, storeText);
		}

		foreach (string name in restored)
		{
			mods.Held.Remove(name);
		}

		sidecar.Write(directory);
		GD.Print($"[mod-save] '{Path.GetFileName(directory)}': held records of {string.Join(", ", restored)} put back");
	}

	// ---------------------------------------------------------------- the stamp

	/// <summary>
	/// A written package (`player=N,name='X'`): the sidecar beside it re-stamped with the running set and
	/// the hero it now holds (player N's).
	/// </summary>
	public static void OnSaveWritten(string fields)
	{
		string name = string.Empty;
		int player = 0;
		foreach ((string key, object value) in RefiiHost.ParseFieldsPlain(fields))
		{
			if (key == "name" && value is string s) name = s;
			if (key == "player" && value is double d) player = (int)d;
			if (key == "player" && value is long l) player = (int)l;
		}

		if (name.Length == 0)
		{
			return;
		}

		// A hero package alone (player two's new save in Fable 3 holds no world) is still the hero's save.
		string? directory = Engine_SaveDirectory.Find(UserDataRoot, name) ?? Engine_SaveDirectory.FindPackage(UserDataRoot, name);
		if (directory is null)
		{
			GD.PushWarning($"[mod-save] save '{name}' (player {player}) written but no folder found under {UserDataRoot}; not stamped");
			return;
		}

		try
		{
			Stamp(directory, player, name);
			GD.Print($"[mod-save] '{name}' (player {player}) stamped");
		}
		catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
		{
			GD.PushWarning($"[mod-save] '{name}' could not be stamped: {e.Message}");
		}
	}

	/// <summary>
	/// Writes the sidecar for the save in <paramref name="directory"/> under the running set; with a
	/// player, the save now holds that player's hero (its character and unlocks,
	/// <see cref="Refii.Achievements.Engine_Achievements.StampSave"/>).
	/// </summary>
	public static SaveSidecar Stamp(string directory, int player = 0, string save = "")
	{
		SaveSidecar sidecar = SaveSidecar.Read(directory);
		SaveSidecar.CharacterLayer? held = sidecar.Refii?.Character;
		sidecar.Refii = new SaveSidecar.RefiiLayer
		{
			Build = ModApi.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
			Written = SaveSidecar.Now(),
			Character = held,
		};
		if (player is 1 or 2)
		{
			Refii.Achievements.Engine_Achievements.StampSave(sidecar, player,
				save.Length != 0 ? save : Path.GetFileName(directory));
		}
		SaveSidecar.ModsLayer current = ModSaveStamp.Restamp(sidecar.Mods, ModSaveStamp.Current());
		sidecar.Mods = current.Set.Count != 0 || current.Held.Count != 0 ? current : null;
		// The title's external mod manager, as applied to the install now: present or not,
		// re-stamped every save.
		if (Refii.Core.Engine_TitleGame.Active.ExternalMods is { } external)
		{
			if (external.StampNow() is { } stamped)
			{
				sidecar.Layers[external.Layer] = stamped;
			}
			else
			{
				sidecar.Layers.Remove(external.Layer);
			}
		}
		sidecar.Write(directory);

		// The guest wrote a new main save: the unfiltered copy of the old one is stale.
		string unfiltered = Path.Combine(directory, UnfilteredName);
		if (File.Exists(unfiltered))
		{
			File.Delete(unfiltered);
		}

		return sidecar;
	}

	// ---------------------------------------------------------------- after the load

	/// <summary>`save_loaded`: a filtered load drops the missing mods' RefiiModSave keys in the game VM.</summary>
	public static void OnSaveLoaded()
	{
		string chunk;
		lock (Lock)
		{
			chunk = _luaClear;
			_luaClear = string.Empty;
		}

		if (chunk.Length != 0)
		{
			HostBridge.PostModCommand("run_lua " + chunk);
		}
	}

	// ---------------------------------------------------------------- player two's kit

	// ---------------------------------------------------------------- the list and the export

	/// <summary>
	/// "Copy for Xbox / Xenia": the save's guest files, the main save without every stamped mod's
	/// records, written under <see cref="SaveExport.DestinationFor"/>. Returns the notice to show.
	/// </summary>
	public static string ExportForConsole(string saveName)
	{
		string? directory = saveName.Length == 0 ? null : Engine_SaveDirectory.Find(UserDataRoot, saveName);
		if (directory is null)
		{
			return $"No save named '{saveName}' was found.";
		}

		SaveSidecar sidecar = SaveSidecar.Read(directory);
		var records = new HashSet<uint>();
		if (sidecar.Mods is { } mods)
		{
			foreach (SaveSidecar.ModEntry mod in mods.Set) records.UnionWith(mod.Records);
			foreach (SaveSidecar.HeldRecords held in mods.Held.Values)
			{
				foreach (SaveNode item in held.Inventory)
				{
					if (item.Child("RecordID")?.Text is uint r) records.Add(r);
				}
			}
		}

		string destination = SaveExport.DestinationFor(UserDataRoot, saveName);
		try
		{
			int removed = SaveExport.Copy(directory, destination, records);
			GD.Print($"[mod-save] '{saveName}' copied for a console to {destination}: {removed} mod entr{(removed == 1 ? "y" : "ies")} left out");
			return "Copied for Xbox / Xenia to\n" + destination + "\n\n" +
				(records.Count == 0 ? "The save had no mods: the copy is the same save." :
					$"{removed} mod entr{(removed == 1 ? "y was" : "ies were")} left out; the original keeps everything.") +
				$"\n\nXenia: put the folder under your profile's {Refii.Core.Engine_Title.Active.TitleId:X8}/00000001. Xbox 360: wrap it into a CON package with Velocity or Horizon.";
		}
		catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
		{
			GD.PushError($"[mod-save] '{saveName}' could not be copied: {e.Message}");
			return "The save could not be copied:\n" + e.Message;
		}
	}
}
