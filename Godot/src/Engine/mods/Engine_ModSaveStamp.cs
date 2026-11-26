using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Refii.Save;

namespace Refii.Mods;

/// <summary>
/// The mods layer of a save's sidecar (<see cref="SaveSidecar"/>): the mod set a save was written
/// under, and how it compares with the set running now. List logic only; the disk is
/// <see cref="ModSaveGate"/>'s.
/// </summary>
/// <remarks>
/// A mod FUSES into a save through the records its GDB patch adds (inventory and equipment carry
/// record ids), through the player store's keys and through <c>RefiiModSave</c>. The stamp carries,
/// per mod, the added record ids (from the patch compositor at mount time) and the key prefixes
/// the mod owns (its name and a dot), so a save can be filtered for a mod that is no longer
/// installed without the mod's package.
///
/// Presence is by NAME. A mod present at another version is not missing — its records are its
/// own to keep stable — and the next save re-stamps the version. A mod the save never saw is
/// safe to add: no box, re-stamped on the next save. A session mod (mod sync) is stamped with
/// <c>session: true</c> and never counts as missing.
/// </remarks>
public static class ModSaveStamp
{
	/// <summary>
	/// The GDB record ids each enabled mod ADDS, by mod name — set by the mount from
	/// <see cref="Refii.Gdb.GdbPatchCompositor.AddedRecordsByMod"/>, read when a save is stamped.
	/// </summary>
	public static IReadOnlyDictionary<string, IReadOnlyCollection<uint>> RecordsByMod { get; set; } =
		new Dictionary<string, IReadOnlyCollection<uint>>(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// The names of the mods applied for the co-op session only — never the player's own list.
	/// // session mods: mod sync sets this when it applies a host's set (ModSession); a session
	/// mod is stamped `session: true` and never fuses.
	/// </summary>
	public static Func<ISet<string>>? SessionNames { get; set; }

	/// <summary>The key prefixes a mod owns in the player store and RefiiModSave: its name and a dot.</summary>
	public static List<string> KeyPrefixes(string name) => new() { name + "." };

	/// <summary>The stamp for <paramref name="set"/>, highest priority first, order 0 first.</summary>
	public static SaveSidecar.ModsLayer Build(
		ModFingerprint set,
		IReadOnlyDictionary<string, IReadOnlyCollection<uint>> recordsByMod,
		ISet<string>? sessionNames = null)
	{
		var layer = new SaveSidecar.ModsLayer { Api = set.ApiVersion, Digest = set.Digest() };
		int order = 0;
		foreach (ModFingerprint.Mod mod in set.Mods)
		{
			var entry = new SaveSidecar.ModEntry
			{
				Name = mod.Name, Title = mod.Title, Version = mod.Version, Sha256 = mod.Sha256,
				Order = order++, Native = mod.Native,
				Session = sessionNames is not null && sessionNames.Contains(mod.Name),
				Keys = KeyPrefixes(mod.Name),
			};
			if (recordsByMod.TryGetValue(mod.Name, out IReadOnlyCollection<uint>? records))
			{
				entry.Records.AddRange(records.OrderBy(r => r));
			}

			layer.Set.Add(entry);
		}

		return layer;
	}

	/// <summary>
	/// The stamp for the set running now: <see cref="ModFingerprint.Current()"/> with the mount's
	/// record sets and the session's names.
	/// </summary>
	public static SaveSidecar.ModsLayer Current() =>
		Build(ModFingerprint.Current(), RecordsByMod, SessionNames?.Invoke());

	// ---------------------------------------------------------------- the comparison

	/// <summary>
	/// How a save's stamp compares with the enabled set: <see cref="Missing"/> stamped mods not
	/// enabled now (session mods never are), <see cref="Changed"/> stamped mods enabled at another
	/// version or build, <see cref="Added"/> enabled mods the save never saw.
	/// </summary>
	public sealed record Diff(
		IReadOnlyList<SaveSidecar.ModEntry> Missing,
		IReadOnlyList<(SaveSidecar.ModEntry Was, ModFingerprint.Mod Now)> Changed,
		IReadOnlyList<ModFingerprint.Mod> Added)
	{
		public bool Same => Missing.Count == 0 && Changed.Count == 0 && Added.Count == 0;

		/// <summary>The records every missing mod added: what the filter removes.</summary>
		public HashSet<uint> MissingRecords()
		{
			var records = new HashSet<uint>();
			foreach (SaveSidecar.ModEntry mod in Missing)
			{
				records.UnionWith(mod.Records);
			}

			return records;
		}

		/// <summary>The key prefixes every missing mod owns.</summary>
		public List<string> MissingKeys()
		{
			var keys = new List<string>();
			foreach (SaveSidecar.ModEntry mod in Missing)
			{
				keys.AddRange(mod.Keys.Count != 0 ? mod.Keys : KeyPrefixes(mod.Name));
			}

			return keys;
		}
	}

	public static Diff Compare(SaveSidecar.ModsLayer? stamp, ModFingerprint current)
	{
		var now = new Dictionary<string, ModFingerprint.Mod>(StringComparer.OrdinalIgnoreCase);
		foreach (ModFingerprint.Mod mod in current.Mods)
		{
			now[mod.Name] = mod;
		}

		var missing = new List<SaveSidecar.ModEntry>();
		var changed = new List<(SaveSidecar.ModEntry, ModFingerprint.Mod)>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (SaveSidecar.ModEntry was in stamp?.Set ?? new List<SaveSidecar.ModEntry>())
		{
			seen.Add(was.Name);
			if (!now.TryGetValue(was.Name, out ModFingerprint.Mod present))
			{
				if (!was.Session)
				{
					missing.Add(was);
				}

				continue;
			}

			if (!string.Equals(was.Version, present.Version, StringComparison.Ordinal) ||
				!string.Equals(was.Sha256, present.Sha256, StringComparison.OrdinalIgnoreCase))
			{
				changed.Add((was, present));
			}
		}

		var added = new List<ModFingerprint.Mod>();
		foreach (ModFingerprint.Mod mod in current.Mods)
		{
			if (!seen.Contains(mod.Name))
			{
				added.Add(mod);
			}
		}

		return new Diff(missing, changed, added);
	}

	// ---------------------------------------------------------------- the words

	/// <summary>The page title of the notice put before a stamped save loads, in the game's language.</summary>
	public static string NoticeTitle => Refii.Text.Engine_OwnWords.Say("REFII_MODS_IN_SAVE");

	/// <summary>The line a missing mod carries in the notice, in the game's language.</summary>
	public static string NotEnabledMark => Refii.Text.Engine_OwnWords.Say("REFII_MODS_NOT_ENABLED");

	/// <summary>
	/// The notice put before a STAMPED save loads: one heading line, the save's own mods one per
	/// line (title and version; session mods are not the save's), the ones not enabled now marked,
	/// and one line about them when there are any. The guest TOLERATES an unknown record on load
	/// (an inventory item becomes an unresolved entry, Category -1; a worn one contributes
	/// nothing; a weapon id wields a component-less entity — TU1 `sub_82763F2C`, `sub_824C0678`,
	/// `sub_824AC94C`), so the filter is what keeps those from ever reaching the guest, and the
	/// notice says what is left out rather than threatening a crash.
	///
	/// AN EXTERNAL MANAGER'S MODS (<paramref name="external"/>, the title's sidecar layer — Fable 2's
	/// Archon's Toolbox) follow under their own heading, written by that manager, and are NOT filtered: their
	/// state is the toolbox runner's script table inside the save's Lua blob — one Pluto object
	/// graph (TU1 `pluto_persist` 0x82BFAC78: every new object is numbered by the running counter
	/// `++a1[1]` at 0x82BFAD20 and written as `[1][ref][type]…`, a repeat as `[0][ref]`; a table is
	/// its metatable then key/value pairs to a nil, 0x82BFA290; a function is bytecode and
	/// upvalues, 0x82BFA4D0) in which the runner's mod tables hold closures and are referenced from
	/// the hook chains. Cutting one entry out renumbers every object after it and leaves every
	/// back-reference into it dangling, so the blob is kept verbatim (<see cref="SaveStream.Tail"/>)
	/// and the notice states that their state stays in the save.
	/// </summary>
	public static string NoticeText(
		SaveSidecar.ModsLayer stamp, Diff diff, Engine_ExternalModManager.Diff? external = null)
	{
		var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (SaveSidecar.ModEntry mod in diff.Missing)
		{
			missing.Add(mod.Name);
		}

		int own = 0;
		foreach (SaveSidecar.ModEntry mod in stamp.Set)
		{
			if (!mod.Session)
			{
				++own;
			}
		}

		var text = new StringBuilder();
		if (own != 0)
		{
			text.Append(Refii.Text.Engine_OwnWords.Say("REFII_MODS_SAVE_MADE_WITH")).Append('\n');
		}

		foreach (SaveSidecar.ModEntry mod in stamp.Set)
		{
			if (mod.Session)
			{
				continue;
			}

			text.Append(mod.Label);
			if (missing.Contains(mod.Name))
			{
				text.Append(NotEnabledMark);
			}

			text.Append('\n');
		}

		if (missing.Count != 0)
		{
			text.Append('\n').Append(Refii.Text.Engine_OwnWords.Say("REFII_MODS_KEPT_FOR_LATER")).Append('\n');
		}

		if (external is not null && external.Notice.Length != 0)
		{
			if (text.Length != 0)
			{
				text.Append('\n');
			}

			text.Append(external.Notice);
		}

		return text.ToString();
	}

	/// <summary>
	/// The next stamp after a save is written: the running set, with the held records of mods
	/// still missing carried over (a mod enabled again had its records restored on load, so its
	/// hold is dropped).
	/// </summary>
	public static SaveSidecar.ModsLayer Restamp(SaveSidecar.ModsLayer? previous, SaveSidecar.ModsLayer current)
	{
		if (previous is null)
		{
			return current;
		}

		foreach ((string name, SaveSidecar.HeldRecords held) in previous.Held)
		{
			if (current.Find(name) is null && !held.IsEmpty)
			{
				current.Held[name] = held;
			}
		}

		return current;
	}
}
