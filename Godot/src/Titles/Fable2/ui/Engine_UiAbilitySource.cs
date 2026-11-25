using System.Collections.Generic;
using Refii.Core;
using Refii.Gdb;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// THE ABILITIES SCREEN'S FOURTEEN ABILITIES, from the shipped GDB and the hero's own levels.
///
/// This one needs no new bridge export at all: the levels are already in
/// `HostBridge.GetHeroVitals().AbilityLevels` (component 57 at `stats + 0x110 + 4*Type`, the
/// guest's `4 * (Type + 68)`), and everything else is in `globals.gdb`.
///
/// The set of fourteen and their categories are HARDCODED IN THE GUEST — `sub_824E45F8` is a bare
/// 14-case jump table from Type to a base record name, and the category membership is written out
/// three separate times in C++ (`sub_829A45A0`'s literal lists, `sub_826CFA00`'s 1-3 / 4-6 / 7-14
/// count ranges, `sub_829A5C28`'s XP-pool switch). There is no table in the data to read, so
/// transcribing it is following the guest rather than inventing.
///
/// The DISPLAY NAME is per LEVEL, not per ability. `sub_829A4FE8` takes the base record's
/// `AbilityGroupRecords`, indexes it to the level being offered — `isMaxed ? maxLevel : level + 1`
/// — and reads that level record's `NameTextTag`, which Babel then resolves. A maxed ability has
/// the tag's LAST CHARACTER overwritten with '6' (`li r10, 0x36 ; stb r10, -1(r8)`), and those
/// `..._06` tags exist in the shipped book.
/// </summary>
public static class Engine_UiAbilitySource
{
	public const string SourceName = "abilities";

	/// <summary>
	/// Type to base record name, from `sub_824E45F8`'s jump table, and the category each belongs
	/// under — Strength 1..3, Skill 4..6, Will 7..14.
	/// </summary>
	private static readonly (int Type, string Record, string Category)[] Abilities =
	{
		(1, "AbilityHeavyWeaponsBase", Strength),
		(2, "AbilityPhysiqueBase", Strength),
		(3, "AbilityToughnessBase", Strength),
		(4, "AbilityRangedWeaponsBase", Skill),
		(5, "AbilityAccuracyBase", Skill),
		(6, "AbilitySpeedBase", Skill),
		(7, "AbilityLightningBase", Will),
		(8, "AbilityFireballBase", Will),
		(9, "AbilitySlowTimeBase", Will),
		(10, "AbilitySwordsBase", Will),
		(11, "AbilityVortexBase", Will),
		(12, "AbilityChaosBase", Will),
		(13, "AbilityForcePushBase", Will),
		(14, "AbilityDeadRisingBase", Will),
	};

	/// <summary>The three category rows' internal names, as `sub_829A45A0` spawns them.</summary>
	public const string Strength = "Strength";

	public const string Skill = "Skill";

	public const string Will = "Will";

	/// <summary>`sub_829A4790` / `0x829A48BC` / `0x829A49F0` — the event type is the CATEGORY,
	/// not the ability: 20 Strength, 21 Skill, 22 Will.</summary>
	private static readonly Dictionary<string, int> CategoryEvent = new()
	{
		[Strength] = 20,
		[Skill] = 21,
		[Will] = 22,
	};

	/// <summary>The event a mod-declared category's rows carry: Will's, the nearest of the three.</summary>
	private const int ModCategoryEvent = 22;

	/// <summary>`dword_8349D3AC` — every one of the fourteen states this icon.</summary>
	private const string AbilityIcon = @"Art\GUI\WheelIcons\icon_abilities.tga";

	private static readonly uint GroupRecordsField = Engine_GameHash.Fnv1("AbilityGroupRecords");
	private static readonly uint NameTextTagField = Engine_GameHash.Fnv1("NameTextTag");

	/// <summary>The action prefix the guest-tick pump answers — see `DispatchAbilityAction`.</summary>
	public const string AbilityAction = "Ability:";

	private static readonly List<Engine_UiMenuSources.Row> None = new();

	public static string Report { get; private set; } = "not read yet";
	/// <summary>The last report per category, so a re-read that changes nothing says nothing.</summary>
	private static readonly System.Collections.Generic.Dictionary<string, string> Reported = new();

	/// <summary>The abilities of one category — `abilities:Strength` and its two siblings.</summary>
	public static IReadOnlyList<Engine_UiMenuSources.Row> Rows(string category)
	{
		if (category.Length == 0)
		{
			return None;
		}

		if (!CategoryEvent.TryGetValue(category, out int eventType))
		{
			// A category only mods declare (a hybrid tree): its row comes from a mod's templates,
			// its abilities act host-side, and they carry Will's event like any mod row would.
			bool declared = false;
			foreach (Engine_UiModAbilities.Ability mod in Engine_UiModAbilities.All)
			{
				if (mod.Category == category)
				{
					declared = true;
					break;
				}
			}

			if (!declared)
			{
				return None;
			}

			eventType = ModCategoryEvent;
		}

		Refii.Host.HostBridge.HeroVitals vitals = Refii.Host.HostBridge.GetHeroVitals();
		if (!vitals.Known || vitals.AbilityLevels is not { Length: > 14 } levels)
		{
			Report = "the hero states no ability levels";
			return None;
		}

		var rows = new List<Engine_UiMenuSources.Row>();
		int unnamed = 0;

		foreach ((int type, string record, string owner) in Abilities)
		{
			if (owner != category)
			{
				continue;
			}

			int level = levels[type];
			string label = LabelOf(record, level, out bool named);
			if (!named)
			{
				++unnamed;
			}

			// THE RANK PIPS ARE `Float4`, and we were sending EmptyRatingSlide for every row —
			// so no ability ever showed its rank. `sub_829A4FE8` at 0x829A5210 states the rule:
			//
			//     *a4 = 2 * v25;                       // v25 = owned levels
			//     if (!maxed) *a4 = 2 * v25 + 1;
			//     if (maxed && (id == 1 || id == 4)) *a4 = 8;
			//
			// The artwork each index selects is authored in the `.bgf`; the index is the whole
			// of the guest's statement.
			int maxLevel = Engine_AbilityRecords.MaxLevel(record);
			bool maxed = maxLevel > 0 && level >= maxLevel;
			int rating = maxed
				? (type == HeavyWeapons || type == RangedWeapons ? MaxedStylesSlide : 2 * level)
				: (2 * level) + 1;

			// AND WHETHER IT COUNTS TOWARD ITS CATEGORY'S CAPTION. The guest's header does not
			// say how many abilities a discipline HAS — `sub_829A5398` labels it through
			// `sub_829A52F8(tag, n, out)` where n is `sub_826CFA00`'s count of abilities that
			// can be bought RIGHT NOW: not maxed, and next level's `ExperienceCost` within
			// reach of this pool plus General (`sub_824E4E28`). Ours read "how many are under
			// it", which is a different and much less useful number.
			//
			// Expressed through the row's own `Counted` flag rather than a special case in the
			// menu, because that flag already means exactly this.
			bool upgradeable = !maxed &&
				Engine_AbilityRecords.TryExperienceCost(record, level + 1, out int next) &&
				next <= Affordable(vitals, category);

			rows.Add(new Engine_UiMenuSources.Row(
				// The guest's own `String1` is the base record name, and so is ours — which also
				// makes the label join find it.
				Name: record,
				Label: label,
				Icon: AbilityIcon,
				EventType: eventType,

				// THE GUEST'S OWN IDENTITY for the row: the base record's ENTRY id (the name is an
				// alias — `AbilityHeavyWeaponsBase` is 0x064A4B7E for entry 0x19F1B0CD). Nothing is
				// dispatched on it: A buys and Y unlearns BY TYPE (the command below, AskUnlearn).
				Id: Engine_AbilityRecords.EntryId(record) is var entry && entry != 0
					? entry
					: Refii.Core.Engine_GameHash.Fnv1(record),
				Rating: rating,
				Committable: false,
				Counted: upgradeable,

				// A MAXED ABILITY HAS NOTHING TO BUY. `sub_824E4E28` returns 0 the moment the
				// current level reaches `sub_824E2D40`'s count, and the guest's own answer to a
				// press then is the `GUI_SCREEN_ABILITIES_MAXED` box. The row stays live because
				// the guest leaves it live — only `AbilitySlowTimeBase` and `AbilityDeadRisingBase`
				// are ever ghosted, on flags we cannot read yet (see below).
				Ghosted: false,

				// THE PURCHASE, straight at the simulation. `sub_8246C970` (Strength and Skill)
				// and `sub_8246CF48` (Will) take the ability index and a hero selector and read
				// NOTHING from the screen, the menu or Lua — see the pump's DispatchAbilityAction.
				Command: $"{AbilityAction}learn:{type}"));
		}

		// MODS' ABILITIES stand after the category's own (Engine_UiModAbilities): the same row
		// shape and pip rule; the level is the book's player's saved value, the price and
		// affordability what the engine states beside it, and A posts the engine's purchase.
		int modIndex = 0;
		foreach (Engine_UiModAbilities.Ability mod in Engine_UiModAbilities.All)
		{
			++modIndex;
			if (mod.Category != category)
			{
				continue;
			}

			int level = Engine_UiModAbilities.Level(mod);
			bool maxed = level >= mod.MaxLevel;
			int? cost = Engine_UiModAbilities.Cost(mod);
			string label = Engine_UiInventorySource.Strings is { } book &&
				book.TryGetString(mod.NameTag, out string named) && named.Length != 0
					? named
					: mod.Name;
			rows.Add(new Engine_UiMenuSources.Row(
				Name: mod.Name,
				Label: label,
				Icon: AbilityIcon,
				EventType: eventType,
				Id: 0x10000u + (uint)modIndex,
				Rating: maxed ? 2 * level : (2 * level) + 1,
				Committable: false,
				Counted: !maxed && cost is not null && Engine_UiModAbilities.CanAfford(mod),
				Ghosted: false,
				Command: Engine_UiModAbilities.BuyAction + mod.Name,

				// NEVER STATED TO THE GUEST. Its id names no GDB record, and the screen's highlight
				// edge `sub_826C3C78` (disc `sub_8259FC18`) ignores `sub_82896900`'s failed lookup
				// (0x826c3d58) and calls `sub_826C2F98` anyway (0x826c3e18) with the ability Type
				// still unset — `sub_82896868` never writes `+0`. That stack word reaches
				// `sub_824E4E28` as the index of `lwzx r31, r10, r29` at 0x824e4f5c
				// (`abilities + 4 * (Type + 68)`), a read anywhere in guest memory.
				FromMod: true));
		}

		int buyable = 0;
		foreach (Engine_UiMenuSources.Row made in rows)
		{
			if (made.Counted)
			{
				++buyable;
			}
		}

		Report = $"{rows.Count} {category} abilit(y/ies), {buyable} upgradeable now" +
			(unnamed == 0 ? string.Empty : $", {unnamed} with no NameTextTag");
		if (!Reported.TryGetValue(category, out string? said) || said != Report)
		{
			Reported[category] = Report;
			Godot.GD.Print($"[ability-source] {Report}");
		}

		return rows;
	}

	/// <summary>
	/// The pool an ability of this discipline can spend: its OWN orbs plus GENERAL. That sum is
	/// the guest's, at 0x824E4E28 —
	///
	///     cost &lt;= (int)*(float *)(4*a3 + *(_DWORD*)(v6+16))   // orbs[category]
	///           + (int)**(float **)(v6 + 16);                  // + orbs[General]
	///
	/// truncated per term, not on the sum, which is why this truncates each one too.
	/// </summary>
	private static int Affordable(Refii.Host.HostBridge.HeroVitals vitals, string category)
	{
		if (vitals.ExperienceAvailable is not { Length: > 3 } orbs)
		{
			return 0;
		}

		int pool = category switch
		{
			Strength => 1,
			Skill => 2,
			Will => 3,
			_ => 0,
		};

		return (int)orbs[pool] + (int)orbs[0];
	}

	/// <summary>The two "styles" abilities, whose maxed pip index is 8 rather than `2 × level`.</summary>
	private const int HeavyWeapons = 1;

	private const int RangedWeapons = 4;

	private const int MaxedStylesSlide = 8;

	/// <summary>
	/// The name of the level this ability is OFFERING — `level + 1`, or the top level when it is
	/// already maxed, with the maxed tag's last character forced to '6'.
	/// </summary>
	/// <summary>
	/// Y on one of the fourteen, as the guest's Y arm runs it (`sub_826C2D40` / `sub_826C2E68`): with the
	/// guest's discard gate open for the row (Engine_AbilityDiscardGates, 0x826C2DC0 / 0x826C2EF0) the
	/// discard confirm, whose YES is the guest's own unlearn BY TYPE — `Ability:&lt;p&gt;:unlearn:&lt;type&gt;`,
	/// `sub_8246D510` behind the same gate natively — which takes one level back, refunds half its cost
	/// and opens its own result box; with it shut, nothing (the arm falls through to its destructors).
	/// Never the guest's Y lever (code 39): its YES handler (`sub_826C2B58`) resolves the ability from
	/// the guest menu's cached highlight at screen+624. False when the row is not one of the fourteen.
	/// </summary>
	public static bool AskUnlearn(Engine_UiExpandableMenu.Item row)
	{
		string record = row.Name;
		int type = 0;
		foreach ((int candidate, string name, _) in Abilities)
		{
			if (name == record)
			{
				type = candidate;
				break;
			}
		}

		if (type == 0)
		{
			return false;
		}

		if (!Engine_AbilityDiscardGates.IsOpenNow(Engine_AbilityDiscardGates.RowId(row.IdHigh, row.IdLow)))
		{
			return true;
		}

		Refii.Host.HostBridge.HeroVitals vitals = Refii.Host.HostBridge.GetHeroVitals();
		int level = vitals.Known && vitals.AbilityLevels is { Length: > 14 } levels ? levels[type] : 0;
		Refii.Text.Engine_BabelDatabase? book = Engine_UiInventorySource.Strings;
		string Text(string tag)
		{
			string text = book is not null && book.TryGetString(tag, out string found) ? found : tag;
			return text.Replace("%1", LabelOf(record, System.Math.Max(level - 1, 0), out _),
				System.StringComparison.Ordinal);
		}

		Engine_UiHostBoxes.Ask(Text("GUI_SCREEN_ABILITIES_DISCARD_CONFIRM"), () =>
			Engine_UiSource.Current.PostPlayerLever("Ability", $"unlearn:{type}"));
		return true;
	}

	/// <summary>
	/// A will's name as the abilities book shows its first level, by spell type 0..7 (0 Lightning ..
	/// 7 Dead Rising: the abilities 7..14 in the same order); the base record name when unnamed.
	/// </summary>
	public static string WillLabel(int spell) =>
		spell is >= 0 and <= 7 ? LabelOf(Abilities[6 + spell].Record, 0, out _) : string.Empty;

	private static string LabelOf(string record, int level, out bool named)
	{
		named = false;
		if (Engine_UiInventorySource.Globals is not { } globals ||
			!globals.TryLookup(Engine_GameHash.Fnv1(record), out GdbCursor baseRecord) ||
			!globals.TryFindInherited(baseRecord, GroupRecordsField,
				(int)GdbFieldType.Reference, out _, out uint group, out _) ||
			!globals.TryLookup(group, out GdbCursor levels))
		{
			return record;
		}

		// The level entries are named `Record1`..`RecordN`. THE LEVEL IS IN THE FIELD NAME, not
		// the position: the GDB stores fields in HASH order, so walking them positionally hands
		// out level 4 first. Their type is ResourceReference (7), unlike the type-6 reference that
		// reached this record.
		int maxLevel = 0;
		for (int at = 1; at <= 16; ++at)
		{
			if (!globals.TryFindInherited(levels, Engine_GameHash.Fnv1($"Record{at}"),
					(int)GdbFieldType.ResourceReference, out _, out _, out _))
			{
				break;
			}

			maxLevel = at;
		}

		if (maxLevel == 0)
		{
			return record;
		}

		bool maxed = level >= maxLevel;
		int offering = maxed ? maxLevel : System.Math.Clamp(level + 1, 1, maxLevel);

		if (!globals.TryFindInherited(levels, Engine_GameHash.Fnv1($"Record{offering}"),
				(int)GdbFieldType.ResourceReference, out _, out uint levelRecord, out _) ||
			!globals.TryLookup(levelRecord, out GdbCursor cursor) ||
			!globals.TryFindInherited(cursor, NameTextTagField, (int)GdbFieldType.String,
				out _, out uint tagHash, out _) ||
			!cursor.View.Names.TryGetValue(tagHash, out string? tag) || tag.Length == 0)
		{
			return record;
		}

		// `..._LEVEL_05` becomes `..._LEVEL_06` when maxed — the guest overwrites the last
		// character rather than looking up a different tag, and those tags exist.
		string wanted = maxed ? tag[..^1] + "6" : tag;
		named = true;

		return Engine_UiInventorySource.Strings is { } book &&
			book.TryGetString(wanted, out string resolved) && resolved.Length != 0
				? resolved
				: wanted;
	}
}
