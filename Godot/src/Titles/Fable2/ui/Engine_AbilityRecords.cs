using System;
using System.Collections.Generic;
using System.Globalization;
using Refii.Core;

namespace Refii.Titles.Fable2;

using Refii.Gdb;

/// <summary>
/// Ability records from the shipped GDB. Live inputs: per-ability level (hero component 57,
/// `stats + 0x110 + 4*id` = the guest's `4 * (Type + 68)` at `0x82896ad0`) and the orb wallet
/// (component 164). Names run `Ability&lt;X&gt;Base` -> `Ability&lt;X&gt;Level&lt;N&gt;`
/// (`sub_824E45F8`); maxed = the next record is absent (`sub_824E2D40`).
/// </summary>
public static class Engine_AbilityRecords
{
	/// <summary>The suffix every ability's root record carries.</summary>
	private const string BaseSuffix = "Base";

	private const string LevelInfix = "Level";

	/// <summary>An ability's static half, as the detail page needs it.</summary>
	public readonly record struct Ability(
		int Type,
		string NameTag,
		string DescriptionTag,
		string BaseRecord,
		string ImagePath,
		string VideoPath)
	{
		public bool Known => Type != 0;
	}

	/// <summary>
	/// The ENTRY id of a record, by any name that reaches it: `AbilityHeavyWeaponsBase` is alias
	/// 0x064A4B7E for entry 0x19F1B0CD, and the guest's `GDBManager_FindEntryByHash` takes the
	/// entry's own id — the number its menu Lua pushes as a row's identity. 0 when unknown.
	/// </summary>
	public static uint EntryId(string recordName)
	{
		GdbMulti? multi = Database;
		return !string.IsNullOrEmpty(recordName) && multi is not null &&
			multi.TryLookup(Engine_GameHash.Fnv1(recordName), out GdbCursor at) && at.Valid
			? at.View.RecordHash(at.Record)
			: 0u;
	}

	/// <summary>The static record behind a spawned row, by the name the guest spawned it with.</summary>
	public static bool TryGet(string recordName, out Ability ability)
	{
		ability = default;
		if (string.IsNullOrEmpty(recordName))
		{
			return false;
		}

		GdbMulti? multi = Database;
		if (multi is null || !multi.TryLookup(Engine_GameHash.Fnv1(recordName), out GdbCursor at))
		{
			return false;
		}

		// THE BASE RECORD CARRIES EVERYTHING — `Type`, `NameTextTag`, `DescriptionTextTag`,
		// both paths and `AbilityGroupRecords`. An earlier note here claimed the opposite and
		// was simply wrong; the shipped database says so plainly:
		//
		//     #19f1b0cd,Type,int,1
		//     #19f1b0cd,NameTextTag,string,GUI_SCREEN_ABILITIES_BRUTAL_STYLES_NAME_01
		//     #19f1b0cd,VideoFilePathAndName,string,art\videos\brutal
		//
		// What actually failed was the LOOKUP: `AbilityHeavyWeaponsBase` is not an entry id at
		// all, it is ALIAS `0x064A4B7E` for entry `0x19F1B0CD`, and the reader did not read the
		// alias table. Every ability reported "IS NOT A GDB RECORD" for that one reason.
		//
		// The level record is still preferred where it states its own value, because a level
		// names itself (`..._NAME_01`); it inherits the rest from the base through `parent`, so
		// this reads the same values either way rather than depending on which side has them.
		GdbCursor from = at;
		if (TryLevelRecord(recordName, 1, out GdbCursor levelOne))
		{
			from = levelOne;
		}

		// The base is still tried, so a record that DOES state its own type keeps working and a
		// modded ability may put the properties wherever it likes.
		if ((!TryInt(multi, from, "Type", out int type) || type <= 0) &&
			(!TryInt(multi, at, "Type", out type) || type <= 0))
		{
			return false;
		}

		ability = new Ability(
			type,
			Text(multi, from, at, "NameTextTag"),
			Text(multi, from, at, "DescriptionTextTag"),
			recordName,

			// Base paths, no extension: the guest appends `.tex` to the image and builds the
			// movie as `GAME:\data\` + video + level suffix. See Engine_UiAbilityPage.
			Text(multi, from, at, "ImageFilePathAndName"),
			Text(multi, from, at, "VideoFilePathAndName"));

		return true;
	}

	/// <summary>
	/// THE TAGS THE PAGE SHOWS FOR A HERO AT <paramref name="level"/>: the OFFERED record's own —
	/// the next level's while one can be bought, the held level's at the maximum — with the base
	/// record's behind each as the fallback. The level records name themselves (`..._NAME_01`,
	/// `..._LEVEL_01`), and for the two Styles the description IS the level ("Brutal Style 1:
	/// Blocking … Future Abilities: 2, 3, 4" against "Next Ability: Style 3 … Already Unlocked"),
	/// so a page reading Record1's tags told a hero with four styles what level one does — the
	/// "abilities menu shows the first level information" report.
	/// </summary>
	public static bool TryTags(string baseRecord, int level, out string nameTag, out string descriptionTag,
		out string nameFallback, out string descriptionFallback)
	{
		nameTag = descriptionTag = nameFallback = descriptionFallback = string.Empty;
		GdbMulti? multi = Database;
		if (multi is null || string.IsNullOrEmpty(baseRecord) ||
			!multi.TryLookup(Engine_GameHash.Fnv1(baseRecord), out GdbCursor at))
		{
			return false;
		}

		int max = MaxLevel(baseRecord);
		int offered = max > 0 && level >= max ? max : level + 1;
		GdbCursor from = at;
		if (!TryLevelRecord(baseRecord, offered, out from) &&
			!(level > 0 && TryLevelRecord(baseRecord, level, out from)) &&
			!TryLevelRecord(baseRecord, 1, out from))
		{
			from = at;
		}

		nameFallback = TryText(multi, at, "NameTextTag");
		descriptionFallback = TryText(multi, at, "DescriptionTextTag");
		nameTag = Text(multi, from, at, "NameTextTag");
		descriptionTag = Text(multi, from, at, "DescriptionTextTag");
		return nameTag.Length != 0 || descriptionTag.Length != 0;
	}

	/// <summary>A string property off the level record, falling back to the base.</summary>
	private static string Text(GdbMulti multi, GdbCursor level, GdbCursor root, string property)
	{
		string found = TryText(multi, level, property);
		return found.Length != 0 ? found : TryText(multi, root, property);
	}

	/// <summary>
	/// The record holding the stats for a given level: the base record at level 0, otherwise
	/// <c>&lt;prefix&gt;Level&lt;n&gt;</c>. Empty when the ability's name does not end in `Base`.
	/// </summary>
	public static string RecordForLevel(string baseRecord, int level)
	{
		if (!baseRecord.EndsWith(BaseSuffix, StringComparison.Ordinal))
		{
			return string.Empty;
		}

		if (level <= 0)
		{
			return baseRecord;
		}

		string prefix = baseRecord[..^BaseSuffix.Length];
		return $"{prefix}{LevelInfix}{level}";
	}

	/// <summary>
	/// HOW MANY LEVELS THIS ABILITY HAS — the element count of the base record's
	/// `AbilityGroupRecords` list, which is exactly what `sub_824E2D40` returns:
	///
	///     824e2d70  addi r7, r11, aAbilitygroupre@l   ; "AbilityGroupRecords"
	///     824e2d7c  bl sub_822A99E8                   ; property lookup -> child list
	///     824e2d9c  lwz r3, var_1C(r1)                ; return its size
	///
	/// The entries are named `Record1`..`RecordN` and the GDB stores fields in HASH order, so
	/// the level is in the NAME, never the position — walking positionally hands out level 4
	/// first. Zero when the record states no list.
	/// </summary>
	public static int MaxLevel(string baseRecord)
	{
		GdbMulti? multi = Database;
		if (multi is null || baseRecord.Length == 0 ||
			!multi.TryLookup(Engine_GameHash.Fnv1(baseRecord), out GdbCursor at) ||
			!multi.TryFindInherited(at, Engine_GameHash.Fnv1(GroupRecordsProperty),
				(int)GdbFieldType.Reference, out _, out uint group, out _) ||
			!multi.TryLookup(group, out GdbCursor levels))
		{
			return 0;
		}

		int max = 0;
		for (int at1 = 1; at1 <= 16; ++at1)
		{
			if (!multi.TryFindInherited(levels, Engine_GameHash.Fnv1($"Record{at1}"),
					(int)GdbFieldType.ResourceReference, out _, out _, out _))
			{
				break;
			}

			max = at1;
		}

		return max;
	}

	private const string GroupRecordsProperty = "AbilityGroupRecords";

	/// <summary>
	/// THE LEVEL RECORD AT `n`, following `AbilityGroupRecords`. Entries are `Record1..RecordN`
	/// and the GDB stores fields in HASH order, so the level is in the NAME — indexing
	/// positionally hands out level 4 first.
	/// </summary>
	public static bool TryLevelRecord(string baseRecord, int level, out GdbCursor cursor)
	{
		cursor = default;
		GdbMulti? multi = Database;
		return multi is not null && baseRecord.Length != 0 && level >= 1 &&
			multi.TryLookup(Engine_GameHash.Fnv1(baseRecord), out GdbCursor at) &&
			multi.TryFindInherited(at, Engine_GameHash.Fnv1(GroupRecordsProperty),
				(int)GdbFieldType.Reference, out _, out uint group, out _) &&
			multi.TryLookup(group, out GdbCursor levels) &&
			multi.TryFindInherited(levels,
				Engine_GameHash.Fnv1($"Record{level.ToString(CultureInfo.InvariantCulture)}"),
				(int)GdbFieldType.ResourceReference, out _, out uint record, out _) &&
			multi.TryLookup(record, out cursor);
	}

	/// <summary>
	/// The ability's preview-video path. READ OFF THE LEVEL-1 RECORD, not the base —
	/// `sub_82896EA8` @0x82896F6C walks `AbilityGroupRecords` first. Taking it off the base
	/// found it only for the abilities that happen to inherit it, so every other ability named
	/// no movie and the previous one kept playing.
	/// </summary>
	public static string VideoPathOf(string baseRecord)
	{
		GdbMulti? multi = Database;
		if (multi is null || !TryLevelRecord(baseRecord, 1, out GdbCursor level))
		{
			return string.Empty;
		}

		return multi.TryFindInherited(level, Engine_GameHash.Fnv1("VideoFilePathAndName"),
				(int)GdbFieldType.String, out _, out uint hash, out _) &&
			level.View.Names.TryGetValue(hash, out string? path)
				? path
				: string.Empty;
	}

	/// <summary>Whether a record exists (absent next level = maxed).</summary>
	public static bool Exists(string recordName)
	{
		GdbMulti? multi = Database;
		return multi is not null && recordName.Length != 0 &&
			multi.TryLookup(Engine_GameHash.Fnv1(recordName), out _);
	}

	/// <summary>
	/// `CECStats_AbilityExperienceCost(level)` (TU1 0x824E49D0 / disc 0x824AB140): `Record{level}`'s
	/// `ExperienceCost`, else the base's own — `sub_824E48F8` takes the base when `level <= 0`
	/// (0x824e4924) or past the list (0x824e4960 / 0x824e4974). Learn prices `level held + 1`
	/// (0x826C3304 `addi r5,r11,1`), the discard text `level held` (0x826C38C0 `mr r5,r14`). BY NAME,
	/// never by position: the level entries are stored in HASH order.
	/// </summary>
	public static bool TryExperienceCost(string baseRecord, int level, out int cost)
	{
		cost = 0;
		GdbMulti? multi = Database;
		if (multi is null || baseRecord.Length == 0)
		{
			return false;
		}

		if (TryLevelRecord(baseRecord, level, out GdbCursor record) &&
			TryInt(multi, record, "ExperienceCost", out cost))
		{
			return true;
		}

		return multi.TryLookup(Engine_GameHash.Fnv1(baseRecord), out GdbCursor at) &&
			TryInt(multi, at, "ExperienceCost", out cost);
	}

	/// <summary>
	/// The spell-stat chain `HeroMagicDef` -> family -> `&lt;Family&gt;Targeted`/`&lt;Family&gt;Area` ->
	/// `Power&lt;level&gt;`, each hop a type-6 reference (`sub_8260D580`); all stats live on the
	/// `Power` entry. False at the first missing hop, as `sub_829A79C0`.
	/// </summary>
	public static bool TryPowerEntry(string family, bool area, int level, out GdbCursor entry)
	{
		entry = default;
		GdbMulti? multi = Engine_GameGdb.Database;
		if (multi is null || string.IsNullOrEmpty(family))
		{
			return false;
		}

		return Engine_GameGdb.TryLookup("HeroMagicDef", out GdbCursor root) &&
			TryRef(multi, root, family, out GdbCursor spell) &&
			TryRef(multi, spell, family + (area ? "Area" : "Targeted"), out GdbCursor half) &&
			TryRef(multi, half, "Power" + level.ToString(CultureInfo.InvariantCulture), out entry);
	}

	/// <summary>One type-6 record reference hop, following the parent chain.</summary>
	public static bool TryRef(GdbMulti multi, GdbCursor at, string property, out GdbCursor target)
	{
		target = default;
		return multi.TryFindInherited(
				at, Engine_GameHash.Fnv1(property), (int)GdbFieldType.Reference,
				out _, out uint hash, out _) &&
			multi.TryLookup(hash, out target);
	}

	/// <summary>A float property on a cursor. Absent reads as 0, which is the guest's default.</summary>
	public static float FloatOn(GdbCursor at, string property)
	{
		GdbMulti? multi = Engine_GameGdb.Database;
		return multi is not null &&
			multi.TryFindInherited(
				at, Engine_GameHash.Fnv1(property), (int)GdbFieldType.Float,
				out _, out uint raw, out _)
			? BitConverter.Int32BitsToSingle(unchecked((int)raw))
			: 0f;
	}

	/// <summary>
	/// A whole-number property on a cursor, authored as `Int` OR `Enum`, 0 when absent. The
	/// shipped database mixes the two inside one family — `Category` on an inventory component
	/// is an ENUM while `Rating` beside it is an INT — and <see cref="IntOn"/> reads only the
	/// latter, which is how every weapon's stats block came back empty.
	/// </summary>
	public static int IntOrEnumOn(GdbCursor at, string property)
	{
		GdbMulti? multi = Engine_GameGdb.Database;
		return multi is not null && TryInt(multi, at, property, out int value) ? value : 0;
	}

	/// <summary>An int property on a cursor, 0 when absent.</summary>
	public static int IntOn(GdbCursor at, string property)
	{
		GdbMulti? multi = Engine_GameGdb.Database;
		return multi is not null &&
			multi.TryFindInherited(
				at, Engine_GameHash.Fnv1(property), (int)GdbFieldType.Int,
				out _, out uint raw, out _)
			? unchecked((int)raw)
			: 0;
	}

	/// <summary>A bool property on a cursor, false when absent.</summary>
	public static bool BoolOn(GdbCursor at, string property)
	{
		GdbMulti? multi = Engine_GameGdb.Database;
		return multi is not null &&
			multi.TryFindInherited(
				at, Engine_GameHash.Fnv1(property), (int)GdbFieldType.Bool,
				out _, out uint raw, out _) &&
			raw != 0u;
	}

	/// <summary>A float property on a named record, following the record's parent chain.</summary>
	public static bool TryFloat(string recordName, string property, out float value)
	{
		value = 0f;
		GdbMulti? multi = Database;
		if (multi is null || recordName.Length == 0 ||
			!multi.TryLookup(Engine_GameHash.Fnv1(recordName), out GdbCursor at) ||
			!multi.TryFindInherited(
				at, Engine_GameHash.Fnv1(property), (int)GdbFieldType.Float,
				out _, out uint raw, out _))
		{
			return false;
		}

		value = BitConverter.Int32BitsToSingle(unchecked((int)raw));
		return true;
	}

	/// <summary>An int property on a cursor, following the parent chain.</summary>
	/// <summary>
	/// A whole-number property, WHICHEVER of the two numeric types it was authored as.
	///
	/// `Int` and `Enum` are both a plain u32 in the container and the shipped database mixes
	/// them inside one record: `Type` on an ability is `int` while `ExperienceCost` beside it is
	/// `enum`. A single-type read fails SILENTLY on the other, which is why every ability
	/// costed 0 and none was ever reported upgradeable.
	/// </summary>
	private static bool TryInt(GdbMulti multi, GdbCursor at, string property, out int value)
	{
		value = 0;
		uint field = Engine_GameHash.Fnv1(property);
		if (!multi.TryFindInherited(at, field, (int)GdbFieldType.Int, out _, out uint raw, out _) &&
			!multi.TryFindInherited(at, field, (int)GdbFieldType.Enum, out _, out raw, out _))
		{
			return false;
		}

		value = unchecked((int)raw);
		return true;
	}

	/// <summary>A string property's text, following the parent chain; the raw value is a hash into the owning view's name table.</summary>
	private static string TryText(GdbMulti multi, GdbCursor at, string property)
	{
		if (!multi.TryFindInherited(
				at, Engine_GameHash.Fnv1(property), (int)GdbFieldType.String,
				out GdbCursor owner, out uint raw, out _) ||
			!owner.Valid)
		{
			return string.Empty;
		}

		return owner.View.Names.TryGetValue(raw, out string? text) ? text : string.Empty;
	}

	/// <summary>The shared runtime database; see <see cref="Engine_GameGdb"/>.</summary>
	private static GdbMulti? Database => Engine_GameGdb.Database;
}
