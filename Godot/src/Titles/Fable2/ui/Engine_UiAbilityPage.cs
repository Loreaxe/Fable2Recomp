using System.Collections.Generic;
using System.Globalization;
using Refii.Gdb;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// The abilities detail page, owned — the right-hand page of screen 14, rebuilt from the shipped
/// GDB and Babel instead of read off the guest.
///
/// The guest's own fill is edge-gated on its Lua highlight: `sub_826C3C78` compares
/// `(Float2 &lt;&lt; 16) | Float1` from `g_Menu:GetHighlightedMenuItemData()` against `screen+624` and
/// skips the fill when it has not moved (`beq cr6, loc_826C3EEC` at `0x826c3d24`). Godot owns the
/// cursor here, so that key never changes and the writers behind it — `sub_826C2F98`,
/// `sub_8246D9F8`, `sub_829A73D0`, `sub_829A5C28` — never run.
///
/// The arithmetic is the guest's. The name is `Babel(NameTextTag)`, a space, and a level tag
/// composed by concatenation: `itoa(level + 1)` appended to `"GUI_SCREEN_ABILITIES_LEVEL_"`
/// (`sub_8246D9F8` at `0x8246daa4`/`0x8246dacc`) — no format string, no zero padding. The
/// description is `Babel(DescriptionTextTag)` with `%1` replaced by a stat fragment, and only the
/// next level's value is shown (`sub_828970E8` computes both and formats the second).
/// </summary>
public static class Engine_UiAbilityPage
{
	private const string Page1Name = "Foreground.Book.Pages.Page1.Contents.AbilityName";

	private const string Page2Name = "Foreground.Book.Pages.Page2.Contents.AbilityName";

	private const string Page2Description =
		"Foreground.Book.Pages.Page2.Contents.Text.Description";

	private const string Page1TotalXp = "Foreground.Book.Pages.Page1.Contents.TotalXP";

	/// <summary>
	/// The "N experience to learn" line on page 4. The template has listed this element all
	/// along and no provider wrote it — `sub_829A9D50` fills it every frame from the level
	/// record's `ExperienceCost`, which the Learn label already computes here.
	/// </summary>
	private const string AbilityCost = "Page4.Contents.AbilityCost";

	/// <summary>The cost caption. The typo is the shipped binary's, not ours.</summary>
	private const string CostTag = "GUI_SCREEN_ABLITY_EXPERIENCE_COST";

	/// <summary>
	/// Page1's one stat line, under the tarot card. `sub_829A5F68` HIDES all four of these
	/// first and then refills per ability — so leaving them alone keeps the previous ability's
	/// line standing, which is how "Brutal Style 2: Flourishes" ended up under Physique.
	/// </summary>
	private const string Page1Target = "Page1.Contents.Target";

	private const string Page1TargetValue = "Page1.Contents.TargetValue";

	private const string Page1Range = "Page1.Contents.Range";

	private const string Page1RangeValue = "Page1.Contents.RangeValue";

	/// <summary>
	/// The two "styles" abilities name their level instead of a number —
	/// `GUI_SCREEN_ABILITIES_LEVELS_{BRUTAL,SKILL}_STYLES_01..04`, `sub_829A6330` /
	/// `sub_829A6500`.
	/// </summary>
	private static readonly Dictionary<int, string> StyleLevelPrefix = new()
	{
		[1] = "GUI_SCREEN_ABILITIES_LEVELS_BRUTAL_STYLES_0",
		[4] = "GUI_SCREEN_ABILITIES_LEVELS_SKILL_STYLES_0",
	};

	/// <summary>
	/// The wallet readouts, keyed without a page so one entry serves both: the guest writes them
	/// in a `do { … } while (v88 &lt;= 2)` over pages 1 and 2, and
	/// <see cref="Engine_UiOwnedBook.HostValues"/> matches on trailing whole segments.
	/// </summary>
	/// <summary>
	/// The four orb GROUPS, in the same order as their value texts. These carry a SLIDE, not
	/// text: `sub_826C3C10(pres, path, flag)` is `sub_82426BA8(elem, pres, flag ? 2 : 1)`.
	/// </summary>
	private static readonly string[] OrbGroupPaths =
	{
		"Contents.experienceorbs.General",
		"Contents.experienceorbs.Strength",
		"Contents.experienceorbs.Skill",
		"Contents.experienceorbs.Will",
	};

	/// <summary>
	/// The lit orb's slide. The guest calls `goToSlide(2)` — `TransitionOn`, a 200ms slide
	/// whose end time CHAINS to slide 3 `On` (orb at 100, scale 0.8, full halo) in the shipped
	/// runtime. The book plays no slide chains, so sitting on the transition left every orb
	/// half-faded; the settled state is what the player of the original saw.
	/// </summary>
	private const int OrbHighlighted = 3;

	private const int OrbPlain = 1;

	private static readonly string[] OrbValuePaths =
	{
		"Contents.experienceorbs.General.valueText",
		"Contents.experienceorbs.Strength.valueText",
		"Contents.experienceorbs.Skill.valueText",
		"Contents.experienceorbs.Will.valueText",
	};

	/// <summary>
	/// The TotalXP heading, by discipline. Index is the discipline `sub_824E4DD8` returns; slot 0
	/// is General, which has no heading (that function answers 0 for a Type outside 1..14).
	/// </summary>
	private static readonly string[] TotalXpTag =
	{
		string.Empty,
		"GUI_SCREEN_ABILITIES_STRENGTH_TOTAL_XP",
		"GUI_SCREEN_ABILITIES_SKILL_TOTAL_XP",
		"GUI_SCREEN_ABILITIES_WILL_TOTAL_XP",
	};

	/// <summary>The token the guest substitutes the stat fragment into — `L"%1"`.</summary>
	private const string StatToken = "%1";

	private const string LevelTagPrefix = "GUI_SCREEN_ABILITIES_LEVEL_";

	private const string MaximisedTag = "GUI_SCREEN_ABILITIES_ABILITY_MAXIMISED";

	private const string TimesSignTag = "GUI_SCREEN_ABILITIES_TIMES_SIGN";

	/// <summary>The two action buttons, keyed without a page so one entry serves both copies.</summary>
	private const string LearnLabel = "Contents.Buttons.Buttons.Learn.Learn";

	private const string UnlearnLabel = "Contents.Buttons.Buttons.Unlearn.Unlearn";

	/// <summary>
	/// Button text, with and without the level/cost parameters. The parameterised forms carry
	/// `%1` (level) and `%2` (cost), so they are usable only when a next level and a resolved cost
	/// both exist; otherwise the plain form.
	/// </summary>
	private const string LearnTag = "GUI_SCREEN_ABILITIES_LEARN";

	private const string LearnWithParametersTag = "GUI_SCREEN_ABILITIES_LEARN_WITH_PARAMETERS";

	private const string DiscardTag = "GUI_SCREEN_ABILITIES_DISCARD";

	private const string DiscardWithParametersTag =
		"GUI_SCREEN_ABILITIES_DISCARD_WITH_PARAMETERS";

	/// <summary>The second substitution token — `L"%2"`, the cost.</summary>
	private const string CostToken = "%2";

	/// <summary>
	/// `CreateMenuItem` event types that mean the row is a category, not an ability:
	/// `sub_826C3C78` clears `screen+196` when the row's `Float3` is 301 or 23 (`0x826c3d0c`) and
	/// skips `sub_826C2F98`, hiding the page rather than filling it.
	/// </summary>
	private const int SubmenuEventType = 301;

	private const int SpellAssignEventType = 23;

	/// <summary>
	/// The stat property whose next-level value fills `%1`, by ability Type — `sub_82896D18`'s
	/// 14-case switch. Only four types name a property; 1, 4 and 7..14 yield an empty string, so
	/// an absent type means the ability has no per-level number to show.
	/// </summary>
	private static readonly Dictionary<int, string> StatProperty = new()
	{
		[2] = "MeleeDamageMultiplier",        // Physique
		[3] = "MaxHealthModifier",            // Toughness
		[5] = "RangedDamageMultiplier",       // Accuracy
		[6] = "MeleeAttackSpeedMultiplier",   // Speed
	};

	/// <summary>Toughness states a difference in whole points; the other three a multiplier.</summary>
	private const int DeltaStatType = 3;

	/// <summary>The stats block's heading element — shared by both the physical and spell forms.</summary>
	private const string StatsHeading = "Text.Stats.targetted.Stats";

	/// <summary>
	/// The row label for the one row the physical path writes, by Type — `sub_829A7740`'s
	/// four-way switch at `0x829a77f0`. Damage for both the melee and ranged multipliers.
	/// </summary>
	private static readonly Dictionary<int, string> StatRowTag = new()
	{
		[2] = "GUI_SPELLS_STATS_DAMAGE",
		[3] = "GUI_SPELLS_STATS_LIFE",
		[5] = "GUI_SPELLS_STATS_DAMAGE",
		[6] = "GUI_SPELLS_STATS_SPEED",
	};

	private const string PhysicalStatsHeadingTag = "GUI_ABILITY_STATS";

	private const string SpellStatsHeadingTag = "GUI_SPELLS_STATS_TARGETED_STATS";

	/// <summary>
	/// Every row slot the block owns — seven targeted, four area, per `sub_829A9490`, which hides
	/// both runs before anything is written. All are cleared every time (the guest scales an
	/// unwritten row's opacity by 1/1024; here the equivalent is empty text).
	/// </summary>
	private const int TargetedRows = 7;

	private const int AreaRows = 4;

	/// <summary>The level at which the guest hides the block outright — `0x829a761c`.</summary>
	private const int StatsHiddenAtLevel = 5;

	/// <summary>
	/// The values for the highlighted row, or an empty set when nothing should be shown. Empty is
	/// a real answer: <see cref="Engine_UiOwnedBook.HostValues"/> blanks a field handed an empty
	/// string, which is what a category row and no selection both want.
	/// </summary>
	/// <param name="levels">
	/// Per-ability current level indexed by Type — <c>HostBridge.HeroVitals.AbilityLevels</c>,
	/// which the bridge already reads from hero component 57 at `stats + 0x110 + 4*id`, the
	/// guest's own `4 * (Type + 68)`.
	/// </param>
	public static IReadOnlyDictionary<string, string> Values(
		Engine_UiExpandableMenu.Item? row,
		int[]? levels,
		float[]? wallet,
		float[]? spellDamage,
		Refii.Text.Engine_BabelDatabase? strings,
		bool discardOpen = false)
	{
		var values = new Dictionary<string, string>(9)
		{
			[Page1Name] = string.Empty,
			[Page2Name] = string.Empty,
			[Page2Description] = string.Empty,
			[Page1TotalXp] = string.Empty,
			[LearnLabel] = string.Empty,
			[UnlearnLabel] = string.Empty,
			[AbilityCost] = string.Empty,
		};

		// The wallet is the player's, not the row's: on screen whatever the cursor is over, so it
		// is filled before the row is considered. Truncated, not rounded — the guest applies
		// `fctiwz` (`0x826c34d0`), which rounds toward zero.
		for (int pool = 0; pool < OrbValuePaths.Length; ++pool)
		{
			values[OrbValuePaths[pool]] = wallet is not null && pool < wallet.Length
				? ((int)wallet[pool]).ToString(CultureInfo.InvariantCulture)
				: string.Empty;
		}

		if (row is null || IsCategory(row) || string.IsNullOrEmpty(row.Name))
		{
			Stats(values, strings, default, 0, string.Empty, known: false, damage: 1f);
			return values;
		}

		if (Engine_UiModAbilities.TryGet(row.Name, out Engine_UiModAbilities.Ability mod))
		{
			return ModValues(values, strings, mod, wallet);
		}

		if (!Engine_AbilityRecords.TryGet(row.Name, out Engine_AbilityRecords.Ability ability))
		{
			// The row's own label is the fallback: the localised name the guest stated when it
			// spawned the row, so an unresolved GDB costs only the level suffix and description.
			values[Page1Name] = row.StatedLabel ?? string.Empty;
			values[Page2Name] = values[Page1Name];
			Stats(values, strings, default, 0, string.Empty, known: false, damage: 1f);
			Report($"'{row.Name}' IS NOT A GDB RECORD — the page keeps only the row's own " +
				$"label ('{values[Page1Name]}'); every other field is empty and the guest's " +
				"last text stands in its place");
			return values;
		}

		int level = levels is not null && ability.Type >= 0 && ability.Type < levels.Length
			? levels[ability.Type]
			: 0;

		string nextRecord = Engine_AbilityRecords.RecordForLevel(ability.BaseRecord, level + 1);
		// MAXED BY THE GROUP'S COUNT, the guest's own rule (`sub_824E2D40`) and what the rows, the
		// slides and the movie already use. An orphan `…Level5` record exists for the four-level
		// Styles, so "the next record exists" said level 5 was on offer, priced it off the base
		// (100) and titled it "Level 5" while the guest's own Learn would have answered MAXED.
		int maxLevel = Engine_AbilityRecords.MaxLevel(ability.BaseRecord);
		bool maxed = maxLevel > 0
			? level >= maxLevel
			: nextRecord.Length == 0 || !Engine_AbilityRecords.Exists(nextRecord);

		string suffix = maxed
			? Babel(strings, MaximisedTag)
			: Babel(strings, LevelTagPrefix + (level + 1).ToString(CultureInfo.InvariantCulture));

		// The offered level's own tags, the base's behind them (a shipped typo tag — Chaos's
		// `…CHAOSDESC_LEVEL_01` — is in no book, and then the base's text stands).
		string nameTag = ability.NameTag, descriptionTag = ability.DescriptionTag;
		if (Engine_AbilityRecords.TryTags(ability.BaseRecord, level, out string levelName, out string levelDescription,
				out string baseName, out string baseDescription))
		{
			nameTag = Babel(strings, levelName).Length != 0 ? levelName : baseName.Length != 0 ? baseName : ability.NameTag;
			descriptionTag = Babel(strings, levelDescription).Length != 0 ? levelDescription
				: baseDescription.Length != 0 ? baseDescription : ability.DescriptionTag;
		}
		ability = ability with { NameTag = nameTag, DescriptionTag = descriptionTag };

		string name = Babel(strings, ability.NameTag);
		if (name.Length == 0)
		{
			name = row.StatedLabel ?? string.Empty;
		}

		values[Page1Name] = suffix.Length == 0 ? name : $"{name} {suffix}";
		values[Page2Name] = values[Page1Name];
		values[Page2Description] = Description(strings, ability, level, nextRecord, maxed);
		values[Page1TotalXp] = TotalXp(strings, ability.Type, wallet);

		// Learn: `%1` = level + 1, `%2` = `AbilityExperienceCost(level + 1)` (0x826C3304), the plain tag
		// when maxed (0x826C373C). Discard, whenever the guest's gate passes for the row, maxed or not
		// (0x826C3870; Engine_AbilityDiscardGates), the plain tag at 50 otherwise (Opacities):
		// `%1` = the level held, `%2` = `AbilityExperienceCost(level held) / 2` (0x826C38C4 `bl`,
		// 0x826C38C8 `srawi r10,r3,1`, 0x826C38D4 `addze`; disc 0x8259F864..0x8259F874).
		int cost = 0;
		bool priced = !maxed && Engine_AbilityRecords.TryExperienceCost(ability.BaseRecord, level + 1, out cost);
		values[LearnLabel] = Button(
			strings, priced ? LearnWithParametersTag : LearnTag,
			level + 1, priced ? cost : 0, priced);
		int held = 0;
		bool refunds = discardOpen && Engine_AbilityRecords.TryExperienceCost(ability.BaseRecord, level, out held);
		values[UnlearnLabel] = Button(
			strings, refunds ? DiscardWithParametersTag : DiscardTag,
			level, refunds ? DiscardRefund(held) : 0, refunds);

		// `%1` is the next level's cost; a maxed ability has none and the line goes.
		values[AbilityCost] = priced
			? Babel(strings, CostTag).Replace(StatToken,
				cost.ToString(CultureInfo.InvariantCulture), System.StringComparison.Ordinal)
			: string.Empty;
		// The multiplier is indexed by level, slot 0 unused. A missing sample answers 1.0, as
		// `sub_82541B30` does when its table has not loaded.
		float damage = spellDamage is not null && level >= 0 && level < spellDamage.Length
			? spellDamage[level]
			: 1f;
		Stats(values, strings, ability, level, nextRecord, known: true, damage);
		Target(values, strings, ability, level, nextRecord, maxed);

		// ONE LINE PER ROW, change-keyed — because "33 values bound" and a blank page are not
		// contradictory statements and only this tells them apart. A key can land on a real
		// element and still show nothing: `ApplyText` treats an empty host value as "leave the
		// guest's text alone", so an unresolved record and a page we never wrote look identical
		// from the outside. Says which branch ran and what came out of it.
		Report($"'{row.Name}' -> {ability.BaseRecord} type {ability.Type} level {level}" +
			$"{(maxed ? " MAXED" : string.Empty)} | name '{values[Page1Name]}'" +
			$" | xp '{values[Page1TotalXp]}' | learn '{values[LearnLabel]}'" +
			$" | target '{values[Page1Target]}' | movie '{MovieFor(row, levels)}'");
		return values;
	}

	/// <summary>
	/// A mod ability's page: the same lines as the guest's, from the mod's tags and the values its
	/// hook publishes. Everything the guest's own branch writes is written here too, so no line
	/// from the previously highlighted ability is left standing.
	/// </summary>
	private static IReadOnlyDictionary<string, string> ModValues(
		Dictionary<string, string> values,
		Refii.Text.Engine_BabelDatabase? strings,
		Engine_UiModAbilities.Ability mod,
		float[]? wallet)
	{
		int level = Engine_UiModAbilities.Level(mod);
		bool maxed = level >= mod.MaxLevel;
		int? cost = maxed ? null : Engine_UiModAbilities.Cost(mod);

		string name = Babel(strings, mod.NameTag);
		if (name.Length == 0)
		{
			name = mod.Name;
		}

		string suffix = maxed
			? Babel(strings, MaximisedTag)
			: Babel(strings, LevelTagPrefix + (level + 1).ToString(CultureInfo.InvariantCulture));
		values[Page1Name] = suffix.Length == 0 ? name : $"{name} {suffix}";
		values[Page2Name] = values[Page1Name];
		values[Page2Description] = Babel(strings, mod.DescriptionTag)
			.Replace(StatToken, maxed ? string.Empty : Engine_UiModAbilities.Stat(mod), System.StringComparison.Ordinal);
		values[Page1TotalXp] = ModTotalXp(strings, mod, wallet);

		bool priced = cost is not null;
		values[LearnLabel] = Button(
			strings, priced ? LearnWithParametersTag : LearnTag, level + 1, cost ?? 0, priced);
		int? refund = level > 0 ? Engine_UiModAbilities.RefundAmount(mod) : null;
		values[UnlearnLabel] = !mod.Refunds
			? string.Empty
			: Button(strings, refund is not null ? DiscardWithParametersTag : DiscardTag,
				level, refund ?? 0, refund is not null);
		values[AbilityCost] = priced
			? Babel(strings, CostTag).Replace(StatToken,
				cost!.Value.ToString(CultureInfo.InvariantCulture), System.StringComparison.Ordinal)
			: string.Empty;

		Stats(values, strings, default, 0, string.Empty, known: false, damage: 1f);
		values[Page1Target] = string.Empty;
		values[Page1TargetValue] = string.Empty;
		values[Page1Range] = string.Empty;
		values[Page1RangeValue] = string.Empty;

		Report($"'{mod.Name}' -> mod ability ({mod.Mod}) level {level}{(maxed ? " MAXED" : string.Empty)}" +
			$" | name '{values[Page1Name]}' | learn '{values[LearnLabel]}'");
		return values;
	}

	/// <summary>
	/// A mod ability's TotalXP: one discipline's heading and sum as the game shows them; for an
	/// ability drawing on several, General plus each of its pools under the mod's own heading
	/// (<c>totalXpTag</c>), or the bare number when it names none.
	/// </summary>
	private static string ModTotalXp(
		Refii.Text.Engine_BabelDatabase? strings, Engine_UiModAbilities.Ability mod, float[]? wallet)
	{
		IReadOnlyList<string> disciplines = Engine_UiModAbilities.DisciplinesOf(mod);
		if (disciplines.Count == 1)
		{
			return TotalXp(strings, ModDisciplineType(disciplines[0]), wallet);
		}

		if (wallet is null || wallet.Length < 4)
		{
			return string.Empty;
		}

		int total = (int)wallet[0];
		foreach (string discipline in disciplines)
		{
			int pool = Engine_UiModAbilities.Pool(discipline);
			if (pool > 0)
			{
				total += (int)wallet[pool];
			}
		}

		string heading = mod.TotalXpTag.Length == 0 ? string.Empty : Babel(strings, mod.TotalXpTag);
		string number = total.ToString(CultureInfo.InvariantCulture);
		return heading.Length == 0 ? number : $"{heading} {number}";
	}

	/// <summary>A type in the category's discipline, for <see cref="TotalXp"/>.</summary>
	private static int ModDisciplineType(string category) => category switch
	{
		Engine_UiAbilitySource.Strength => 2,
		Engine_UiAbilitySource.Skill => 5,
		Engine_UiAbilitySource.Will => 8,
		_ => 0,
	};

	/// <summary>The last line printed, so an unchanged row costs nothing.</summary>
	private static string _reported = string.Empty;

	private static void Report(string line)
	{
		if (_reported == line)
		{
			return;
		}

		_reported = line;
		Godot.GD.Print($"[ability-page] {line}");
	}

	/// <summary>
	/// PAGE 1'S OWN LINE, under the tarot card. `sub_829A5F68` hides `Target`, `TargetValue`,
	/// `Range` and `RangeValue` before writing anything, then refills per ability — so a page
	/// that never writes them shows the LAST ability's line. That is how "Brutal Style 2:
	/// Flourishes" came to stand under Physique.
	///
	/// The two styles name their level (`sub_829A6330` / `sub_829A6500`); Physique, Toughness,
	/// Accuracy and Speed show the same stat their description substitutes; the eight spells
	/// have per-spell writers whose numbers already appear in the Page 2 stats block, so their
	/// line stays blank rather than half-filled.
	/// </summary>
	private static void Target(
		Dictionary<string, string> values, Refii.Text.Engine_BabelDatabase? strings,
		Engine_AbilityRecords.Ability ability, int level, string nextRecord, bool maxed)
	{
		values[Page1Target] = string.Empty;
		values[Page1TargetValue] = string.Empty;
		values[Page1Range] = string.Empty;
		values[Page1RangeValue] = string.Empty;

		if (StyleLevelPrefix.TryGetValue(ability.Type, out string? prefix))
		{
			// The level NAMED is the one on offer, matching the title line beside it.
			int shown = System.Math.Clamp(maxed ? level : level + 1, 1, 4);
			values[Page1Target] = Babel(
				strings, prefix + shown.ToString(CultureInfo.InvariantCulture));
			return;
		}

		if (!StatRowTag.TryGetValue(ability.Type, out string? tag) ||
			!StatProperty.TryGetValue(ability.Type, out string? property) ||
			nextRecord.Length == 0 ||
			!Engine_AbilityRecords.TryFloat(nextRecord, property, out float value))
		{
			return;
		}

		values[Page1Target] = Babel(strings, tag);
		values[Page1TargetValue] = ability.Type == DeltaStatType
			? FormatStat((int)value)
			: FormatStat(value);
	}

	/// <summary>
	/// The experience total for this ability's discipline: the heading, a space, then the General
	/// pool added to the discipline's own — affordability is
	/// `cost &lt;= orbs[discipline] + orbs[General]`. Both terms are truncated before the addition,
	/// as the guest's two `fctiwz` conversions are.
	/// </summary>
	private static string TotalXp(
		Refii.Text.Engine_BabelDatabase? strings, int type, float[]? wallet)
	{
		int discipline = Discipline(type);
		if (discipline == 0 || wallet is null || wallet.Length < 4)
		{
			return string.Empty;
		}

		string heading = Babel(strings, TotalXpTag[discipline]);
		int total = (int)wallet[0] + (int)wallet[discipline];
		string number = total.ToString(CultureInfo.InvariantCulture);
		return heading.Length == 0 ? number : $"{heading} {number}";
	}

	/// <summary>
	/// Which experience pool an ability draws on — `sub_824E4DD8`: Types 1..3 answer 1
	/// (Strength), 4..6 answer 2 (Skill), 7..14 answer 3 (Will), anything else 0. The numbers are
	/// the pool indices in hero component 164.
	/// </summary>
	private static int Discipline(int type) => type switch
	{
		>= 1 and <= 3 => 1,
		>= 4 and <= 6 => 2,
		>= 7 and <= 14 => 3,
		_ => 0,
	};

	/// <summary>
	/// The description with its `%1` filled in. The token is replaced even when the fragment is
	/// empty (Types 1, 4 and 7..14), so it is removed rather than drawn.
	/// </summary>
	private static string Description(
		Refii.Text.Engine_BabelDatabase? strings,
		Engine_AbilityRecords.Ability ability,
		int level,
		string nextRecord,
		bool maxed)
	{
		string text = Babel(strings, ability.DescriptionTag);
		if (text.Length == 0)
		{
			return string.Empty;
		}

		return text.Replace(StatToken, maxed ? string.Empty : Fragment(strings, ability, level, nextRecord));
	}

	/// <summary>The stat fragment for `%1` — the next level's number, formatted the guest's way.</summary>
	private static string Fragment(
		Refii.Text.Engine_BabelDatabase? strings,
		Engine_AbilityRecords.Ability ability,
		int level,
		string nextRecord)
	{
		if (!StatProperty.TryGetValue(ability.Type, out string? property) ||
			!Engine_AbilityRecords.TryFloat(nextRecord, property, out float next))
		{
			return string.Empty;
		}

		if (ability.Type == DeltaStatType)
		{
			// `sub_82897248` — whole points, no Babel, current level subtracted. The current
			// record is the base at level 0 and `Level<n>` after that.
			string currentRecord = Engine_AbilityRecords.RecordForLevel(ability.BaseRecord, level);
			Engine_AbilityRecords.TryFloat(currentRecord, property, out float current);
			return $"+ {(int)next - (int)current}";
		}

		// The guest's own formatter, behind the localised multiplication sign and a space.
		string number = FormatStat(next);
		string sign = Babel(strings, TimesSignTag);
		return sign.Length == 0 ? number : $"{sign} {number}";
	}

	/// <summary>The Discard prompt's two elements, as the page fill names them (`.Contents.Buttons.Buttons.Unlearn.Unlearn`
	/// / `….y_button`, built at 0x8325FCE0 / 0x8325FD20).</summary>
	private static readonly string[] DiscardElements =
		{ UnlearnLabel, "Contents.Buttons.Buttons.Unlearn.y_button" };

	/// <summary>
	/// The Discard prompt's opacity for an ability row: 1 with the guest's gate open, 0.5 shut — the page
	/// fill's `sub_8246B8E8(…, 100.0)` at 0x826C395C / 0x826C396C and `50.0` at 0x826C39C0 / 0x826C39D0
	/// (disc 0x8259F8FC.. / 0x8259F960..). No claim off one of the fourteen.
	/// </summary>
	public static IReadOnlyDictionary<string, float> DiscardOpacities(Engine_UiExpandableMenu.Item? row, bool open)
	{
		if (row is null || IsCategory(row) || string.IsNullOrEmpty(row.Name) ||
			Engine_UiModAbilities.TryGet(row.Name, out _) || !Engine_AbilityRecords.TryGet(row.Name, out _))
		{
			return Engine_UiPageProviders.NoOpacities;
		}

		float opacity = open ? 1f : 0.5f;
		var claims = new Dictionary<string, float>(DiscardElements.Length);
		foreach (string element in DiscardElements)
		{
			claims[element] = opacity;
		}

		return claims;
	}

	/// <summary>The discard text's refund: half the held level's cost, toward zero (`srawi` + `addze`, 0x826C38C8).</summary>
	public static int DiscardRefund(int heldLevelCost) => heldLevelCost / 2;

	/// <summary>
	/// A button label with its `%1` (level) and `%2` (cost) filled in. Both tokens are replaced
	/// whenever the parameterised form is used; otherwise the plain tag is returned as Babel
	/// gives it.
	/// </summary>
	private static string Button(
		Refii.Text.Engine_BabelDatabase? strings, string tag, int level, int cost, bool parameterised)
	{
		string text = Babel(strings, tag);
		if (text.Length == 0 || !parameterised)
		{
			return text;
		}

		return text
			.Replace(StatToken, level.ToString(CultureInfo.InvariantCulture))
			.Replace(CostToken, cost.ToString(CultureInfo.InvariantCulture));
	}

	/// <summary>
	/// `sub_823B0900`: `swprintf(L"%.*f", 2, v)` then trailing zeros stripped, but on reaching the
	/// decimal point one zero is pushed back — 2.50 prints "2.5", 1.25 "1.25", 3.00 "3.0".
	/// </summary>
	private static string FormatStat(float value)
	{
		string text = value.ToString("F2", CultureInfo.InvariantCulture);
		text = text.TrimEnd('0');
		return text.EndsWith(".", System.StringComparison.Ordinal) ? text + "0" : text;
	}

	/// <summary>
	/// The stats block: a heading and one written row for a physical ability, the spell rows for a
	/// spell, and cleared rows for anything else. Spell rows scale by the hero-side damage
	/// multiplier `sub_8246C678(level)`, which the pump samples.
	/// </summary>
	private static void Stats(
		Dictionary<string, string> values,
		Refii.Text.Engine_BabelDatabase? strings,
		Engine_AbilityRecords.Ability ability,
		int level,
		string nextRecord,
		bool known,
		float damage)
	{
		for (int row = 1; row <= TargetedRows; ++row)
		{
			values[$"Stats.targetted.Effect{row}"] = string.Empty;
			values[$"Stats.targetted.EffectValue{row}"] = string.Empty;
		}

		for (int row = 1; row <= AreaRows; ++row)
		{
			values[$"Stats.area.Effect{row}"] = string.Empty;
			values[$"Stats.area.EffectValue{row}"] = string.Empty;
		}

		values[StatsHeading] = string.Empty;

		// The guest's own two gates: the Type whitelist at `0x829a75c0` and the max-level test
		// at `0x829a761c`. A Type outside 1..14 hides the block too, which the whitelist covers.
		bool spell = ability.Type >= 7 && ability.Type <= 14;
		bool physical = StatRowTag.ContainsKey(ability.Type);
		if (!known || level == StatsHiddenAtLevel || !(spell || physical))
		{
			return;
		}

		values[StatsHeading] =
			Babel(strings, spell ? SpellStatsHeadingTag : PhysicalStatsHeadingTag);

		if (spell)
		{
			// Seven targeted slots and four area ones — `sub_829A9490` hides exactly those runs
			// before anything is written, which is where the counts come from.
			Fill(values, strings, "Stats.targetted", SpellRows(strings, ability.Type, level, false, damage),
				TargetedRows);
			Fill(values, strings, "Stats.area", SpellRows(strings, ability.Type, level, true, damage),
				AreaRows);
			return;
		}

		if (!physical)
		{
			return;
		}

		string value = Fragment(strings, ability, level, nextRecord);
		if (value.Length == 0)
		{
			return;
		}

		values["Stats.targetted.Effect1"] = Babel(strings, StatRowTag[ability.Type]);
		values["Stats.targetted.EffectValue1"] = value;
	}


	/// <summary>
	/// The eight spell families, by ability Type — `sub_8260CFA0`, indexed by
	/// `sub_8246E3E8`'s Type-7 fan-out. The name is the GDB property that opens the chain.
	/// </summary>
	private static readonly string[] SpellFamily =
	{
		"Lightning", "Fireball", "SlowTime", "Swords",
		"Vortex", "Chaos", "ForcePush", "DeadRising",
	};

	private const string SecondsTag = "GUI_SPELLS_STATS_SECONDS";

	private const string MetresTag = "GUI_SPELLS_STATS_METRES";

	/// <summary>
	/// One row of a spell's stats block: a label tag and the value already rendered.
	/// </summary>
	private readonly record struct SpellStat(string Tag, string Value);

	/// <summary>
	/// A spell family's rows for one half of the block. `sub_829A79C0` fans out to eight writers
	/// that do not share a shape: Fireball sums two properties, SlowTime's rows are conditional so
	/// later rows shift up, and ForcePush's area half prints collision damage unmultiplied.
	///
	/// `damage` is the hero-side multiplier `sub_8246C678(level)` returns.
	/// </summary>
	private static List<SpellStat> SpellRows(
		Refii.Text.Engine_BabelDatabase? strings, int type, int level, bool area, float damage)
	{
		var rows = new List<SpellStat>(4);
		int family = type - 7;
		if (family < 0 || family >= SpellFamily.Length ||
			!Engine_AbilityRecords.TryPowerEntry(SpellFamily[family], area, level, out GdbCursor at))
		{
			return rows;
		}

		float F(string property) => Engine_AbilityRecords.FloatOn(at, property);
		int I(string property) => Engine_AbilityRecords.IntOn(at, property);
		bool B(string property) => Engine_AbilityRecords.BoolOn(at, property);

		// `f(v) + " " + Babel(unit)` — the guest's own composition, with no unit meaning no space.
		string Unit(float value, string tag)
		{
			string number = FormatStat(value);
			string unit = Babel(strings, tag);
			return unit.Length == 0 ? number : $"{number} {unit}";
		}

		string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

		switch (family)
		{
			case 0:  // Lightning
				rows.Add(new("GUI_SPELLS_STATS_LIGHTNING_DURATION",
					Unit(F("ElectrifyDuration"), SecondsTag)));
				rows.Add(new("GUI_SPELLS_STATS_DAMAGE", FormatStat(F("TotalDamage") * damage)));
				if (area)
				{
					rows.Add(new("GUI_SPELLS_STATS_NUMBER_OF_TARGETS",
						Count(I("MaxNumVictimsToAffect"))));
				}

				break;

			case 1:  // Fireball — ONE row, and it is the SUM of two properties
				rows.Add(new("GUI_SPELLS_STATS_DAMAGE",
					FormatStat((F("CreatureDamage") + F("CreatureImpactDamage")) * damage)));
				break;

			case 2:  // SlowTime
				if (area)
				{
					rows.Add(new("GUI_SPELLS_STATS_DURATION", Unit(F("Duration"), SecondsTag)));
					break;
				}

				// Conditional rows shift the rest up: the guest appends rather than indexing.
				if (F("DamageAmount") > 0f)
				{
					rows.Add(new("GUI_SPELLS_STATS_DAMAGE", FormatStat(F("DamageAmount"))));
				}

				rows.Add(new("GUI_SPELLS_STATS_SLOWTIME_DISTANCE",
					Unit(F("MaxTargetTravelDistance"), MetresTag)));
				rows.Add(new("GUI_SPELLS_STATS_SLOWTIME_DAMAGE_MULTIPLIER",
					FormatStat(F("WeaponDamageMultiplier"))));
				rows.Add(new("GUI_SPELLS_STATS_SLOWTIME_DAMAGE_DURATION",
					Unit(F("WeaponDamageDuration"), SecondsTag)));

				// A label with an empty value is the guest's own answer here.
				if (B("AppearBehindTarget"))
				{
					rows.Add(new("GUI_SPELLS_STATS_SLOWTIME_APPEAR", string.Empty));
				}

				if ((int)F("TargetKnockdownKinetcisFactor") > 0)
				{
					rows.Add(new("GUI_SPELLS_STATS_SLOWTIME_KNOCKDOWN",
						FormatStat((int)F("TargetKnockdownKinetcisFactor"))));
				}

				rows.Add(new("GUI_SPELLS_STATS_STUN_TIME",
					Unit(F("TargetStunnedTime"), SecondsTag)));
				break;

			case 3:  // Swords
				rows.Add(new("GUI_SPELLS_STATS_BLADES_NUMBER", Count(I("NumSwords"))));
				rows.Add(area
					? new SpellStat("GUI_SPELLS_STATS_BLADES_DAMAGE_PER_SWORD",
						FormatStat(F("DamagePerSword") * damage))
					: new SpellStat("GUI_SPELLS_STATS_TOTAL_DAMAGE",
						FormatStat(F("TotalDamage") * damage)));
				break;

			case 4:  // Vortex — the same three rows on both halves
				rows.Add(new("GUI_SPELLS_STATS_DURATION", Unit(F("Duration"), SecondsTag)));
				rows.Add(new("GUI_SPELLS_STATS_TOTAL_DAMAGE",
					FormatStat(F("TotalDamage") * damage)));
				rows.Add(new("GUI_SPELLS_STATS_NUMBER_OF_TARGETS",
					Count(I("MaxNumCreatures"))));
				break;

			case 5:  // Chaos — one row, both halves
				rows.Add(new("GUI_SPELLS_STATS_DURATION", Unit(F("Duration"), SecondsTag)));
				break;

			case 6:  // ForcePush
				rows.Add(new("GUI_SPELLS_STATS_DAMAGE",
					FormatStat(F("DamageAmount") * damage)));

				// The area half does not multiply: `0x829a8fa4` prints CollisionDamageAmount raw
				// where the targeted half scales it.
				rows.Add(new("GUI_SPELLS_STATS_FORCE_PUSH_COLLISION",
					FormatStat(area ? F("CollisionDamageAmount")
									: F("CollisionDamageAmount") * damage)));
				if ((int)F("StunnedTime") > 0)
				{
					rows.Add(new("GUI_SPELLS_STATS_STUN_TIME", Count((int)F("StunnedTime"))));
				}

				break;

			case 7:  // DeadRising
				rows.Add(new("GUI_SPELLS_STATS_DEAD_RISING_NUMBER", Count(I("NumToRise"))));
				rows.Add(new("GUI_SPELLS_STATS_DURATION", Unit(F("Duration"), SecondsTag)));

				// The area half reads the two flags swapped — `0x829a9364`/`0x829a9378` take
				// `Invulnerable` into the targeted half's `InvulnerableNonFlourish` slot and vice
				// versa. Shipped bug, reproduced.
				bool first = B(area ? "InvulnerableNonFlourish" : "Invulnerable");
				bool second = B(area ? "Invulnerable" : "InvulnerableNonFlourish");
				if (first)
				{
					rows.Add(new("GUI_SPELLS_STATS_DEAD_RISING_INVULNERABLE", " "));
				}

				if (second)
				{
					rows.Add(new("GUI_SPELLS_STATS_DEAD_RISING_INVULNERABLE_FLOURISH", " "));
				}

				break;
		}

		return rows;
	}


	/// <summary>The tarot card quad — one element carries both the still and the movie.</summary>
	/// <summary>The tarot card's quad — the still lands here, and the preview movie paints
	/// over the same element when no guest sprite names one.</summary>
	public const string TarotFrame = "Contents.tarot.tarot_card_frame";

	/// <summary>
	/// The tarot card's texture, by document path. The still only: the guest puts the `.tex` up
	/// immediately (`sub_829A5668`) and swaps a `.bik` in after one second of accumulated frame
	/// time (`sub_829A5A18`), which needs a per-element video channel that does not exist.
	///
	/// No prefix on the image: `sub_829A56F8` builds it as `ImageFilePathAndName + ".tex"`; the
	/// `GAME:\data\` prefix at `0x8349D430` applies to the `.bik` alone.
	/// </summary>
	public static IReadOnlyDictionary<string, uint> Textures(Engine_UiExpandableMenu.Item? row)
	{
		var textures = new Dictionary<string, uint>(1);
		if (row is not null && Engine_UiModAbilities.TryGet(row.Name, out Engine_UiModAbilities.Ability mod))
		{
			if (Engine_UiModAbilities.ImageKey(mod) is { } card)
			{
				textures[TarotFrame] = card;
			}

			return textures;
		}

		if (row is null || IsCategory(row) || string.IsNullOrEmpty(row.Name) ||
			!Engine_AbilityRecords.TryGet(row.Name, out Engine_AbilityRecords.Ability ability) ||
			ability.ImagePath.Length == 0)
		{
			return textures;
		}

		// `ResourcePath` normalises separators and lower-cases before hashing, as the guest does
		// over `LHString::GetAsLowerCase`, so the raw path goes in.
		textures[TarotFrame] =
			Refii.Core.Engine_GameHash.ResourcePath(ability.ImagePath + ".tex");
		return textures;
	}

	/// <summary>Writes a run of rows into their slots, leaving the rest blank.</summary>
	private static void Fill(
		Dictionary<string, string> values,
		Refii.Text.Engine_BabelDatabase? strings,
		string group,
		List<SpellStat> rows,
		int slots)
	{
		for (int slot = 1; slot <= slots; ++slot)
		{
			bool has = slot <= rows.Count;
			values[$"{group}.Effect{slot}"] =
				has ? Babel(strings, rows[slot - 1].Tag) : string.Empty;
			values[$"{group}.EffectValue{slot}"] = has ? rows[slot - 1].Value : string.Empty;
		}
	}

	/// <summary>A tag through Babel, or empty — never the tag itself.</summary>
	private static string Babel(Refii.Text.Engine_BabelDatabase? strings, string tag) =>
		strings is not null && tag.Length != 0 && strings.TryGetString(tag, out string value)
			? value
			: string.Empty;

	/// <summary>
	/// Whether this row is a heading rather than an ability, off the event type the guest stated
	/// to `sub_826CE8D0` when the row was spawned — not inferred from the row having children.
	/// </summary>
	private static bool IsCategory(Engine_UiExpandableMenu.Item row) =>
		row.EventType == SubmenuEventType || row.EventType == SpellAssignEventType;

	/// <summary>
	/// WHICH ORB GROUP IS LIT. Every group was left on its authored slide, so none of them ever
	/// answered to the highlighted ability and they all read the same whatever folder you were
	/// in. `sub_826C2F98` states the rule, once per page, for pages 1 and 2:
	///
	///   - Strength/Skill/Will take slide 2 when the ability's own discipline matches,
	///   - General takes slide 2 while it holds anything at all (`(int)orbs[0] &gt; 0`),
	///   - and a MAXED ability puts every group back to slide 1 — there is nothing to spend on.
	///
	/// The value texts beside them are written unconditionally and are already in `Values`.
	/// What slides 1 and 2 look like is `.bgf`-authored and not ours to decide.
	/// </summary>
	public static IReadOnlyDictionary<string, int> Slides(
		Engine_UiExpandableMenu.Item? row, int[]? levels, float[]? orbs)
	{
		var slides = new Dictionary<string, int>(OrbGroupPaths.Length);
		for (int at = 0; at < OrbGroupPaths.Length; ++at)
		{
			slides[OrbGroupPaths[at]] = OrbPlain;
		}

		if (row is not null && orbs is { Length: > 3 } &&
			Engine_UiModAbilities.TryGet(row.Name, out Engine_UiModAbilities.Ability mod))
		{
			// The same rule for a mod's ability: its category's orb, General while it holds
			// anything, and nothing lit once it is maxed.
			if (Engine_UiModAbilities.Level(mod) >= mod.MaxLevel)
			{
				return slides;
			}

			// Every discipline it draws on: one orb for a plain ability, two for a hybrid.
			foreach (string named in Engine_UiModAbilities.DisciplinesOf(mod))
			{
				int modDiscipline = Engine_UiModAbilities.Pool(named);
				if (modDiscipline > 0)
				{
					slides[OrbGroupPaths[modDiscipline]] = OrbHighlighted;
				}
			}

			if ((int)orbs[0] > 0)
			{
				slides[OrbGroupPaths[0]] = OrbHighlighted;
			}

			return slides;
		}

		if (row is null || orbs is not { Length: > 3 } ||
			!Engine_AbilityRecords.TryGet(row.Name,
				out Engine_AbilityRecords.Ability ability))
		{
			return slides;
		}

		int level = levels is { Length: > 14 } && ability.Type is > 0 and < 15
			? levels[ability.Type]
			: 0;
		int max = Engine_AbilityRecords.MaxLevel(ability.BaseRecord);
		if (max > 0 && level >= max)
		{
			return slides;
		}

		// 1..3 Strength, 4..6 Skill, 7..14 Will — `sub_824E4DD8`.
		int discipline = ability.Type switch
		{
			>= 1 and <= 3 => 1,
			>= 4 and <= 6 => 2,
			>= 7 and <= 14 => 3,
			_ => 0,
		};

		if (discipline > 0)
		{
			slides[OrbGroupPaths[discipline]] = OrbHighlighted;
		}

		if ((int)orbs[0] > 0)
		{
			slides[OrbGroupPaths[0]] = OrbHighlighted;
		}

		return slides;
	}

	/// <summary>
	/// THE PREVIEW MOVIE, chosen HERE rather than read back off the guest.
	///
	/// `sub_829A56F8` builds the name and the slot-3 tick opens it — but only on a frame where
	/// the packed row hash at `screen+624` changed, and that hash is the guest's own highlight,
	/// which stops moving the moment Godot owns the cursor. So the guest chose exactly one movie
	/// — the first row's, Brutal Styles — and every other ability kept showing it.
	///
	///     if (Type == 1 || Type == 4) {                       // the two "styles"
	///         name = "GAME:\data\" + VideoFilePathAndName + "_";
	///         name += (level &gt;= max) ? "ALL" : str(level + 1);
	///     } else  name = "GAME:\data\" + VideoFilePathAndName;
	///     name += ".bik";
	///
	/// Empty when the row is a category or states no video — the guest leaves the last movie
	/// standing there, and so do we.
	/// </summary>
	/// <summary>
	/// Whether the row wants NO movie in the frame — a mod's ability, which has a still card and
	/// no preview. Distinct from an empty <see cref="MovieFor"/>, which leaves the last movie up.
	/// </summary>
	public static bool WantsNoMovie(Engine_UiExpandableMenu.Item? row) =>
		row is not null && Engine_UiModAbilities.TryGet(row.Name, out _);

	public static string MovieFor(Engine_UiExpandableMenu.Item? row, int[]? levels)
	{
		if (row is null ||
			!Engine_AbilityRecords.TryGet(row.Name,
				out Engine_AbilityRecords.Ability ability))
		{
			return string.Empty;
		}

		// OFF THE LEVEL-1 RECORD. `sub_82896EA8` walks `AbilityGroupRecords` before reading
		// `VideoFilePathAndName`; the base record only carries it for the abilities that happen
		// to inherit one, which is why Brutal Styles found a movie and Physique did not — and a
		// blank name leaves the previous ability's movie standing.
		string video = Engine_AbilityRecords.VideoPathOf(ability.BaseRecord);
		if (video.Length == 0)
		{
			video = ability.VideoPath;
		}

		if (video.Length == 0)
		{
			return string.Empty;
		}

		if (ability.Type is not (HeavyWeapons or RangedWeapons))
		{
			return video + MovieExtension;
		}

		int level = levels is { Length: > 14 } && ability.Type is > 0 and < 15
			? levels[ability.Type]
			: 0;
		int max = Engine_AbilityRecords.MaxLevel(ability.BaseRecord);
		string suffix = max > 0 && level >= max
			? MovieAllSuffix
			: (level + 1).ToString(CultureInfo.InvariantCulture);
		return video + "_" + suffix + MovieExtension;
	}

	/// <summary>The two abilities whose movie is per-level — `sub_829A56F8`'s `Type == 1 || 4`.</summary>
	private const int HeavyWeapons = 1;

	private const int RangedWeapons = 4;

	private const string MovieAllSuffix = "ALL";

	private const string MovieExtension = ".bik";
}
