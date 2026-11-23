using System.Collections.Generic;
using System.Linq;
using Refii.Gdb;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// Screen 17's per-row detail pages, owned.
///
/// Two change detectors (`sub_826D9168`), both seeded to -1: `screen+96` vs the row's EventType
/// selects which tab page is up; `screen+100` vs the composite key drives the detail fill. Only
/// the second is ours to replace.
///
/// Page paths are `Foreground.Book.Pages.&lt;Subject&gt;Page` in `logbookscreen.bgf` — a `PageN`
/// path resolves to nothing on this screen.
/// </summary>
public static class Engine_UiLogbookPage
{
	/// <summary>
	/// The subject page for a row, as `sub_826D9168`'s page table selects it — 60/64 morality,
	/// 61 expressions, 62 completed quests, 65 renown, 66 attractiveness, 67 stats, 68 dog,
	/// 69 help — by switching the pages' `active` attribute. The guest keys that on its own
	/// highlight, which never moves on this host-driven list, so the selection is stated here:
	/// the row's page on, the other seven off; a heading (a folder) shows none.
	/// </summary>
	public static IReadOnlyDictionary<string, bool> Active(Engine_UiExpandableMenu.Item? row)
	{
		var active = new Dictionary<string, bool>();
		foreach (string page in SubjectPages)
		{
			active[page] = false;
		}

		if (row is not null && SubjectPageOf(row.EventType) is { } shown)
		{
			active[shown] = true;
		}

		// The description's inline D-pad glyph is placed by the guest's text layout for a
		// description that carries the markup; the host writes the description itself, and
		// the placeholder otherwise sits at its authored spot over the effects rows.
		active["Foreground.Book.Pages.ExpressionsPage.Contents.Description.InlineIcon"] = false;

		// Effect slots beyond the expression's count are HIDDEN (`sub_82426260(…, 0)`), and
		// the `Stats` heading with its rule goes with them when there are none (`0x829bd278`).
		int count = EffectCountOf(row);
		for (int slot = 0; slot < EffectSlots; ++slot)
		{
			bool filled = slot < count;
			active[$"Foreground.Book.Pages.ExpressionsPage.Contents.Effects.Effect{slot + 1}"] = filled;
			active[$"Foreground.Book.Pages.ExpressionsPage.Contents.Effects.EffectValue{slot + 1}"] = filled;
		}

		active["Foreground.Book.Pages.ExpressionsPage.Contents.Effects.Stats"] = count > 0;
		active["Foreground.Book.Pages.ExpressionsPage.Contents.Effects.line"] = count > 0;

		return active;
	}

	/// <summary>
	/// The shown subject page's OPEN pose. Every subject page's master leaves `Contents`
	/// inactive and its first slide lights it — the guest's `goToSlide` on its own highlight
	/// edge, which no longer fires under an owned cursor. The Stats page that rendered was the
	/// one the guest had left at slide 1; the rest sat at 0 with a bare parchment.
	/// </summary>
	public static IReadOnlyDictionary<string, int> Slides(
		Engine_UiExpandableMenu.Item? row, Refii.Host.HostBridge.HeroDerived hero)
	{
		var slides = new Dictionary<string, int>(6);

		// THE DOG'S TWO LEVELS ARE SLIDES, NOT TEXT: `sub_829BC750` runs `goToSlide(level)` on
		// `Dog.CombatAmount` (c97+188, the fighter level) and `Dog.SearchingAmount` (c97+184,
		// the hunter level) — five-star groups whose slide N lights N stars.
		if (hero.DogKnown)
		{
			slides["Foreground.Book.Pages.DogPage.Contents.LogBook.Dog.CombatAmount"] =
				System.Math.Max(0, hero.DogCombat);
			slides["Foreground.Book.Pages.DogPage.Contents.LogBook.Dog.SearchingAmount"] =
				System.Math.Max(0, hero.DogSearching);
		}
		if (row is not null && SubjectPageOf(row.EventType) is { } shown)
		{
			slides[shown] = OpenSlide;
		}

		// THE METERS. The Alignment page's two sliders (Evil–Good, Corrupt–Pure) and the
		// Attractiveness page's one (Ugly–Attractive) author `unselected` (1: group on, full
		// opacity), `selected` (2: frame fades in), `disabled` (3: half opacity) and
		// `selected2unselected` (4); their masters leave the group OFF. The guest shows them
		// read-only on `unselected` from its own activate fill, which no longer runs under
		// the owned cursor; the pips already sit where the guest's fill left them.
		slides["Foreground.Book.Pages.MoralityPage.Contents.LogBook.Morality.slider"] = MeterSlide;
		slides["Foreground.Book.Pages.MoralityPage.Contents.LogBook.Morality.slider2"] = MeterSlide;
		slides["Foreground.Book.Pages.AttractivenessPage.Contents.LogBook.slider"] = MeterSlide;

		// The Stats page's stick-prompt row (`Scrollbar`: one slide lights it); the region
		// driver fades its arrows at the limits and rides the glyph on the fraction.
		slides["Foreground.Book.Pages.StatsPage.Contents.LogBook.Scrollbar"] = OpenSlide;
		slides["Foreground.Book.Pages.RenownPage.Contents.LogBook.Scrollbar"] = OpenSlide;

		return slides;
	}

	/// <summary>`unselected` — the read-only pose of the logbook's meters.</summary>
	private const int MeterSlide = 1;

	/// <summary>The shown page's contents on the leaf, whatever its open slide parks them at.</summary>
	public static IReadOnlyDictionary<string, Godot.Vector3> Positions(
		Engine_UiExpandableMenu.Item? row, Refii.Host.HostBridge.HeroDerived hero)
	{
		var places = new Dictionary<string, Godot.Vector3>(4);
		if (row is not null && SubjectPageOf(row.EventType) is { } shown)
		{
			places[$"{shown}.Contents"] = Godot.Vector3.Zero;
		}

		// THE METER PIPS — `sub_82881E38`: `<slider>.grp.pip` position = (v·440 − 210, 0, −1),
		// v clamped to [0, 1]. The Alignment sliders take the morality and purity fractions
		// (`sub_829B91F0`).
		//
		// The Attractiveness slider takes the fraction ALREADY STATED by `sub_829BA860`, which
		// clamps its sum to [-100, 100] and scales by `(total + 100)·0.005` before handing it to
		// the meter. Rescaling here would apply that curve twice; the sum it came from is also
		// not the sum of the eleven rows this page shows, so it cannot be rebuilt from them.
		if (hero.Known)
		{
			places["Foreground.Book.Pages.MoralityPage.Contents.LogBook.Morality.slider.grp.pip"] =
				Pip(hero.MoralityFraction);
			places["Foreground.Book.Pages.MoralityPage.Contents.LogBook.Morality.slider2.grp.pip"] =
				Pip(hero.PurityFraction);
			places["Foreground.Book.Pages.AttractivenessPage.Contents.LogBook.slider.grp.pip"] =
				Pip(hero.AttractivenessFraction);
		}

		return places;
	}

	private static Godot.Vector3 Pip(float value) =>
		new(Godot.Mathf.Clamp(value, 0f, 1f) * 440f - 210f, 0f, -1f);

	private const int OpenSlide = 1;

	private static readonly string[] SubjectPages =
	{
		"Foreground.Book.Pages.MoralityPage", "Foreground.Book.Pages.ExpressionsPage",
		"Foreground.Book.Pages.CompletedQuestsPage", "Foreground.Book.Pages.RenownPage",
		"Foreground.Book.Pages.AttractivenessPage", "Foreground.Book.Pages.StatsPage",
		"Foreground.Book.Pages.DogPage", "Foreground.Book.Pages.HelpPage",
	};

	private static string? SubjectPageOf(int eventType) => eventType switch
	{
		60 or 64 => "Foreground.Book.Pages.MoralityPage",
		61 => "Foreground.Book.Pages.ExpressionsPage",
		62 => "Foreground.Book.Pages.CompletedQuestsPage",
		65 => "Foreground.Book.Pages.RenownPage",
		66 => "Foreground.Book.Pages.AttractivenessPage",
		67 => "Foreground.Book.Pages.StatsPage",
		68 => "Foreground.Book.Pages.DogPage",
		69 => "Foreground.Book.Pages.HelpPage",
		_ => null,
	};


	/// <summary>
	/// The help page's sixteen tutorial subjects, in table order — index-parallel `_NAME` and
	/// `_DESC` tag tables; the row's key is `index + 239` (`sub_829BDB58` at `0x829BDD2C` indexes
	/// `unk_8349E0C0 + 4*key - 956`, 956 = 239*4). Order comes from each slot's static
	/// initialiser, not address order (the literals descend). Both fields are tags: the handler
	/// ignores the spawner's localised String2 and re-resolves String1.
	/// </summary>
	private static readonly string[] HelpSubjects =
	{
		"CAMERA", "MELEE", "BRUTAL", "WILL", "RANGED", "SKILL", "SAFETY", "HOUSE",
		"SHOP", "PEOPLE", "DOG", "MARRIAGE", "EXPERIENCE", "COOP", "RENOWN", "FOLLOWERS",
	};


	private const int QuestsEventType = 62;

	/// <summary>The key of the first completed-quest row — `sub_829B8B18` spawns `i + 1000`.</summary>
	private const int QuestKeyBase = 1000;

	private const string QuestTitle = "CompletedQuestsPage.Contents.Title";

	private const string QuestDescription = "CompletedQuestsPage.Contents.Quests.DescriptionValue";

	private const int HelpEventType = 69;

	/// <summary>The key of the first help row — `sub_829B8D88` spawns `i + 239`.</summary>
	private const int HelpKeyBase = 239;

	private const string HelpTitle = "HelpPage.Contents.Title";

	private const string HelpText = "HelpPage.Contents.LogBook.Text";

	private const string ExpressionTitle = "ExpressionsPage.Title";

	private const string ExpressionDescription = "ExpressionsPage.Contents.Description";

	/// <summary>
	/// The EventType the guest maps to the expressions tab — `sub_826D9168`'s page table:
	/// 60/64 morality, <b>61 expressions</b>, 62 completed quests, 65 renown, 66
	/// attractiveness, 67 stats, 68 dog, 69 help.
	/// </summary>
	private const int ExpressionsEventType = 61;

	/// <summary>Morality shares a page with two EventTypes; renown has one.</summary>
	private static readonly int[] MoralityEventTypes = { 60, 64 };

	private const int RenownEventType = 65;

	/// <summary>
	/// The band edges the guest tests the NORMALISED value against — `sub_829B91F0` at
	/// `0x829b9280`..`0x829b92c0`. Below the low edge and above the high edge are the named
	/// extremes; between them is NEUTRAL, on both axes.
	/// </summary>
	private const float LowBand = 0.375f;

	private const float HighBand = 0.625f;

	/// <summary>
	/// The eleven attractiveness rows, in the order the sampler fills them — which is the order
	/// the page's own `Attractiveness.` prefix concatenates.
	/// </summary>
	/// <summary>
	/// The 93 stat rows, as (counter id, element path under the Stats container). The id indexes
	/// the counter array, not this list, so gaps (10, 12, 21, 26, …) are counters the page does
	/// not display; the guest holds the same pairing in a `std::map<int, pair<LHString,LHString>>`
	/// and names the element `group + suffix`.
	///
	/// The pairing must be read off the key INSERT order and the suffix-global reference order,
	/// not off the decompiler's value stores (it aliases key and string pointer through one stack
	/// slot) and not off global address order (money key 22 is out of sequence). Each global's
	/// string comes from its own static initialiser (`sub_8222CED0(&global, ".Suffix")`).
	/// </summary>
	/// <summary>
	/// The stat rows this page shows — stated by the screen's template, not held here. See
	/// <see cref="Engine_UiScreenTemplate.StatFields"/>.
	/// </summary>
	private static IReadOnlyList<(int Id, string Path)> StatRows =>
		Engine_UiTemplates.Screen(LogbookScreenId)?.StatFields ??
		(IReadOnlyList<(int Id, string Path)>)System.Array.Empty<(int, string)>();

	private const uint LogbookScreenId = 17u;

	private static readonly string[] AttractivenessRows =
	{
		"Attractiveness.ClothingValue",
		"Attractiveness.HairValue",
		"Attractiveness.BeardValue",
		"Attractiveness.MoustacheValue",
		"Attractiveness.MakeupValue",
		"Attractiveness.TattooValue",
		"Attractiveness.PurityCorruptionValue",
		"Attractiveness.MuscleValue",
		"Attractiveness.FatnessValue",
		"Attractiveness.ScarsValue",
		"Attractiveness.HomeBonusValue",
	};

	/// <summary>Another player's stats as the stats page's fill was given them: 124 counters and the name.</summary>
	public sealed record OrbView(int[] Stats, string Name);

	/// <summary>
	/// The player the stats page was last filled for when not the hero, else null: the fill
	/// `sub_829BAEB8` states it at every open of the book (logbook_stats_stated.cpp).
	/// </summary>
	public static OrbView? OtherPlayer { get; private set; }

	private const int OrbCounters = 124;

	/// <summary>`@Logbook.Stats`: 0 for the hero's own book; 1, the name and 124 counters for another player's.</summary>
	public static void StatsFilled(Engine_UiModel.GuiNativeCall call)
	{
		if (call.Argument(0).Text != "1" || call.Arguments.Count != OrbCounters + 2)
		{
			OtherPlayer = null;
			return;
		}

		var stats = new int[OrbCounters];
		for (int i = 0; i < stats.Length; ++i)
		{
			int.TryParse(call.Argument(i + 2).Text, System.Globalization.NumberStyles.Integer,
				System.Globalization.CultureInfo.InvariantCulture, out stats[i]);
		}

		OtherPlayer = new OrbView(stats, call.Argument(1).Text ?? string.Empty);
	}

	/// <summary>The detail values for the highlighted row, or blanks.</summary>
	public static IReadOnlyDictionary<string, string> Values(
		Engine_UiExpandableMenu.Item? row,
		Refii.Host.HostBridge.HeroDerived hero,
		List<Refii.Host.HostBridge.CompletedQuest> quests,
		Refii.Text.Engine_BabelDatabase? strings,
		OrbView? orb = null)
	{
		var values = new Dictionary<string, string>(10)
		{
			[ExpressionTitle] = string.Empty,
			[ExpressionDescription] = string.Empty,
		};

		// The hero pages do not depend on the cursor: morality, renown and the title are player
		// state, filled whatever row is highlighted.
		HeroPages(values, hero, strings);

		// ANOTHER PLAYER'S BOOK. An orb's View Stats opens this screen on that player's stats
		// object (`sub_823C48D8`), and the guest's open fills the stats page from it alone
		// (`sub_826D8DD8` → `sub_829BAEB8`), titled with its name — the only row is Stats.
		if (orb is not null)
		{
			values["StatsPage.Contents.LogBook.Title"] = orb.Name;
			foreach ((int stat, string path) in StatRows)
			{
				values[$"Stats.{path}"] = stat >= 0 && stat < orb.Stats.Length
					? orb.Stats[stat].ToString(System.Globalization.CultureInfo.InvariantCulture)
					: string.Empty;
			}
		}
		HelpPage(values, row, strings);
		QuestsPage(values, row, quests, strings);

		// A row on any other tab leaves this page alone rather than blanking it — the guest
		// shows one page at a time and the others keep whatever they last held.
		if (row is null || row.EventType != ExpressionsEventType)
		{
			return values;
		}

		// On this page the row key is the GDB record HASH, not the name: spawner `sub_829B8888`
		// resolves via `sub_821DA348` and passes the result as the row id; consumer
		// `sub_829BCDF0` takes it straight back to `sub_821DA348` (`0x829bce1c`). Other books
		// key the same composite differently (entity handle, record name), so each states its own.
		uint id = ((uint)row.IdHigh << 16) | ((uint)row.IdLow & 0xFFFFu);

		GdbMulti? byHash = Engine_GameGdb.Database;
		GdbCursor at = default;
		bool found = byHash is not null && byHash.TryLookup(id, out at);

		// The name is tried second rather than not at all: it costs one lookup and covers a
		// row whose id we failed to capture at spawn time.
		if (!found && !Engine_GameGdb.TryLookup(row.Name ?? string.Empty, out at))
		{
			return values;
		}

		GdbMulti? multi = Engine_GameGdb.Database;
		if (multi is null)
		{
			return values;
		}

		string title = Babel(strings, Text(multi, at, "NameTag"));
		values[ExpressionTitle] = title.Length != 0 ? title : row.StatedLabel ?? string.Empty;
		values[ExpressionDescription] =
			Babel(strings, Text(multi, at, "ExpressionDescriptionTextTag"));

		// THE EFFECT ROWS — `sub_829BCF98`: the definition's `Effects` component carries 21
		// opinion floats; every non-zero one is a row (`sub_824706A0`), four slots shown, the
		// name the matching `GUI_SCREEN_APPEARANCE_*` tag and the value `(int)(v·100)` with a
		// percent sign (`L"%d%%"`, no rounding, negatives as-is). Unused slots are hidden, not
		// blanked — see <see cref="Active"/>.
		List<(string Tag, float Value)> effects = ExpressionEffects(multi, at, id);
		for (int slot = 0; slot < EffectSlots; ++slot)
		{
			string name = slot < effects.Count ? Babel(strings, effects[slot].Tag) : string.Empty;
			string amount = slot < effects.Count
				? $"{(int)(effects[slot].Value * 100f)}%"
				: string.Empty;
			values[$"ExpressionsPage.Contents.Effects.Effect{slot + 1}"] = name;
			values[$"ExpressionsPage.Contents.Effects.EffectValue{slot + 1}"] = amount;
		}

		return values;
	}

	private const string ExpressionIcon = "ExpressionsPage.Contents.Icon";

	/// <summary>
	/// THE EXPRESSION'S ICON — the fill `sub_829BCDF0` (disc `sub_828D61D0`) looks the row's id up
	/// (`sub_821DA348` 0x829bce1c, the record hash as in <see cref="Values"/>) and hands the record's
	/// `IconGraphic` string to `GuiElement_SetTexture` (0x829bcee8), which stores the resource hash
	/// of its lower-cased text (`sub_8286DDC0`). Empty for any other row: the guest leaves the icon.
	/// </summary>
	public static IReadOnlyDictionary<string, uint> Textures(Engine_UiExpandableMenu.Item? row)
	{
		var textures = new Dictionary<string, uint>(1);
		if (row is null || row.EventType != ExpressionsEventType || Engine_GameGdb.Database is not { } multi)
		{
			return textures;
		}

		uint id = ((uint)row.IdHigh << 16) | ((uint)row.IdLow & 0xFFFFu);
		if (!multi.TryLookup(id, out GdbCursor at) && !Engine_GameGdb.TryLookup(row.Name ?? string.Empty, out at))
		{
			return textures;
		}

		string icon = Text(multi, at, "IconGraphic");
		if (icon.Length != 0)
		{
			textures[ExpressionIcon] = Refii.Core.Engine_GameHash.ResourcePath(icon);
		}

		return textures;
	}

	private const int EffectSlots = 4;

	/// <summary>The 21 opinion fields of an expression's `Effects` component, with their display tags (`0x83253A88` / `0x83253C68`, same order).</summary>
	private static readonly (string Field, string Tag)[] OpinionFields =
	{
		("Attractiveness", "GUI_SCREEN_APPEARANCE_ATTRACTIVENESS"),
		("Poshness", "GUI_SCREEN_APPEARANCE_POSHNESS"),
		("Friendliness", "GUI_SCREEN_APPEARANCE_FRIENDLINESS"),
		("Politeness", "GUI_SCREEN_APPEARANCE_POLITENESS"),
		("Ridiculousness", "GUI_SCREEN_APPEARANCE_RIDICULOUSNESS"),
		("Entertainment", "GUI_SCREEN_APPEARANCE_ENTERTAINMENT"),
		("Romance", "GUI_SCREEN_APPEARANCE_ROMANCE"),
		("Extroversion", "GUI_SCREEN_APPEARANCE_EXTROVERSION"),
		("Morality", "GUI_SCREEN_APPEARANCE_MORALITY"),
		("Scariness", "GUI_SCREEN_APPEARANCE_SCARINESS"),
		("Crudeness", "GUI_SCREEN_APPEARANCE_CRUDENESS"),
		("Raunchiness", "GUI_SCREEN_APPEARANCE_RAUNCHINESS"),
		("Purity", "GUI_SCREEN_APPEARANCE_PURITY"),
		("Funny", "GUI_SCREEN_APPEARANCE_FUNNY"),
		("Aggressiveness", "GUI_SCREEN_APPEARANCE_AGGRESSIVENESS"),
		("Childishness", "GUI_SCREEN_APPEARANCE_CHILDISHNESS"),
		("MoralityGood", "GUI_SCREEN_APPEARANCE_MORALITYGOOD"),
		("MoralityEvil", "GUI_SCREEN_APPEARANCE_MORALITYEVIL"),
		("Rudeness", "GUI_SCREEN_APPEARANCE_RUDENESS"),
		("Ugliness", "GUI_SCREEN_APPEARANCE_UGLINESS"),
		("Transvestism", "GUI_SCREEN_APPEARANCE_TRANSVESTISM"),
	};

	private static readonly uint EffectsComponent = Refii.Core.Engine_GameHash.Fnv1("Effects");

	/// <summary>The last expression's effects, keyed by its record hash — `Active` asks per frame.</summary>
	private static (uint Id, List<(string Tag, float Value)> Effects) _effectsCache;

	private static List<(string Tag, float Value)> ExpressionEffects(GdbMulti multi, GdbCursor definition, uint id)
	{
		if (_effectsCache.Id == id && _effectsCache.Effects is not null)
		{
			return _effectsCache.Effects;
		}

		var effects = new List<(string, float)>();
		if (multi.TryFindInherited(definition, EffectsComponent, 6, out _, out uint link, out _) &&
			multi.TryLookup(link, out GdbCursor component))
		{
			foreach ((string field, string tag) in OpinionFields)
			{
				if (multi.TryFindInherited(component, Refii.Core.Engine_GameHash.Fnv1(field), 3, out _, out uint raw, out _))
				{
					float value = System.BitConverter.Int32BitsToSingle((int)raw);
					if (value != 0f)
					{
						effects.Add((tag, value));
					}
				}
			}
		}

		_effectsCache = (id, effects);
		return effects;
	}

	/// <summary>How many effect rows the highlighted expression fills (0 when it is not an expression row).</summary>
	private static int EffectCountOf(Engine_UiExpandableMenu.Item? row)
	{
		if (row is null || row.EventType != ExpressionsEventType || Engine_GameGdb.Database is not { } multi)
		{
			return 0;
		}

		uint id = ((uint)row.IdHigh << 16) | ((uint)row.IdLow & 0xFFFFu);
		if (_effectsCache.Id == id && _effectsCache.Effects is not null)
		{
			return _effectsCache.Effects.Count;
		}

		return multi.TryLookup(id, out GdbCursor at) ? ExpressionEffects(multi, at, id).Count : 0;
	}

	/// <summary>
	/// The completed-quests page: one quest's rewards, description and conclusion.
	///
	/// Markup is the guest's token for token — no newlines; `&lt;br&gt;` breaks, `&lt;s1&gt;…&lt;/s1&gt;` is the
	/// heading style, money uses `&lt;gold_img&gt;` and renown `&lt;bullet.img&gt;` (not interchangeable).
	/// Rewards precede the description: `sub_829BD5C8` runs before `sub_829BD8F8` into one
	/// accumulator. Item bullets are a GDB walk (`sub_82523078` enumerates the quest record's
	/// `RewardItems` sub-record with inheritance), so they need nothing live from the guest.
	/// </summary>
	private static void QuestsPage(
		Dictionary<string, string> values,
		Engine_UiExpandableMenu.Item? row,
		List<Refii.Host.HostBridge.CompletedQuest> quests,
		Refii.Text.Engine_BabelDatabase? strings)
	{
		values[QuestTitle] = string.Empty;
		values[QuestDescription] = string.Empty;

		if (row is null || row.EventType != QuestsEventType || quests.Count == 0)
		{
			return;
		}

		uint key = ((uint)row.IdHigh << 16) | ((uint)row.IdLow & 0xFFFFu);
		long index = (long)key - QuestKeyBase;
		if (index < 0 || index >= quests.Count)
		{
			return;
		}

		// Descending by `DisplayPriority`, the guest's comparator. The bridge hands the list over
		// filtered but unsorted; the sort must be stable to keep bridge order among equals.
		var ordered = new List<Refii.Host.HostBridge.CompletedQuest>(quests);
		ordered = ordered
			.Select((quest, at) => (quest, at))
			.OrderByDescending(pair => Priority(pair.quest.Record))
			.ThenBy(pair => pair.at)
			.Select(pair => pair.quest)
			.ToList();

		Refii.Host.HostBridge.CompletedQuest chosen = ordered[(int)index];
		if (!Engine_GameGdb.TryLookupHash(chosen.Record, out GdbCursor at))
		{
			return;
		}

		GdbMulti? multi = Engine_GameGdb.Database;
		if (multi is null)
		{
			return;
		}

		values[QuestTitle] = Babel(strings, Text(multi, at, "NameTag"));

		var text = new System.Text.StringBuilder();

		// `sub_829BD5C8` shows the header when items are non-empty OR money > 0 OR renown > 0
		// (0x829BD624).
		List<string> items = RewardItems(multi, at, strings);
		bool money = chosen.Money > 0;
		bool renown = chosen.Renown > 0;
		if (items.Count > 0 || money || renown)
		{
			text.Append("<s1>")
				.Append(Babel(strings, "GUI_SCREEN_WORLD_MAP_QUESTS_PAGE_REWARDS"))
				.Append("</s1><br>");

			// One `<br>` per item, then one more for the list: the trailing break at
			// 0x829BD7A0 sits inside the `count > 0` arm.
			if (items.Count > 0)
			{
				foreach (string name in items)
				{
					text.Append("<bullet.img> ").Append(name).Append("<br>");
				}

				text.Append("<br>");
			}

			if (money)
			{
				text.Append("<gold_img> ")
					.Append(Babel(strings, "GUI_SCREEN_WORLD_MAP_QUESTS_PAGE_MONEY"))
					.Append(' ')
					.Append(chosen.Money.ToString(System.Globalization.CultureInfo.InvariantCulture))
					.Append("<br><br>");
			}

			if (renown)
			{
				text.Append("<bullet.img> ")
					.Append(Babel(strings, "GUI_SCREEN_WORLD_MAP_QUESTS_PAGE_RENOWN"))
					.Append(' ')
					.Append(chosen.Renown.ToString(System.Globalization.CultureInfo.InvariantCulture))
					.Append("<br><br>");
			}
		}

		text.Append("<s1>")
			.Append(Babel(strings, "GUI_SCREEN_WORLD_MAP_QUESTS_PAGE_DESCRIPTION"))
			.Append("</s1><br>")
			.Append(Babel(strings, Text(multi, at, "DescriptionTag")))
			.Append("<br><br>");

		if (!string.IsNullOrEmpty(chosen.Conclusion))
		{
			text.Append("<s1>")
				.Append(Babel(strings, "GUI_SCREEN_LOGBOOK_COMPLETED_QUESTS_CONCLUSION"))
				.Append("</s1><br>")
				.Append(Babel(strings, chosen.Conclusion))
				.Append("<br><br>");
		}

		values[QuestDescription] = text.ToString();
	}

	/// <summary>
	/// A quest's reward item names, already localised, in the guest's own order.
	///
	/// No type filter: `sub_82523078` reads every enumerated property's value as a record hash
	/// (`v11 = *v10` at 0x8252312C) and lets the lookup fail. The real filter is that a record
	/// with no `InventoryItemComponent` produces no bullet (`if (v14)` at 0x829BD6F8).
	/// `NameTag` holds a Babel key, resolved by `sub_8230E0E8` at 0x829BD758.
	/// </summary>
	private static List<string> RewardItems(
		GdbMulti multi, GdbCursor quest, Refii.Text.Engine_BabelDatabase? strings)
	{
		var names = new List<string>();
		if (!Engine_AbilityRecords.TryRef(multi, quest, "RewardItems", out GdbCursor rewards))
		{
			return names;
		}

		foreach ((uint _, GdbFieldType _, uint raw) in multi.InheritedFields(rewards))
		{
			if (raw == 0u || !multi.TryLookup(raw, out GdbCursor item) ||
				!Engine_AbilityRecords.TryRef(multi, item, "InventoryItemComponent", out GdbCursor part))
			{
				continue;
			}

			string name = Babel(strings, Text(multi, part, "NameTag"));
			if (name.Length != 0)
			{
				names.Add(name);
			}
		}

		return names;
	}

	/// <summary>A quest record's `DisplayPriority`, 0 when absent — the guest's own default.</summary>
	private static int Priority(uint record) =>
		Engine_GameGdb.TryLookupHash(record, out GdbCursor at)
			? Engine_AbilityRecords.IntOn(at, "DisplayPriority")
			: 0;

	/// <summary>
	/// The help page — a subject's name and description straight from Babel; no GDB record and
	/// no hero state, just two tag tables and an index.
	/// </summary>
	private static void HelpPage(
		Dictionary<string, string> values,
		Engine_UiExpandableMenu.Item? row,
		Refii.Text.Engine_BabelDatabase? strings)
	{
		values[HelpTitle] = string.Empty;
		values[HelpText] = string.Empty;

		if (row is null || row.EventType != HelpEventType)
		{
			return;
		}

		// The key is unsigned in the guest: below the base it wraps and fails the bound rather
		// than indexing backwards.
		uint key = ((uint)row.IdHigh << 16) | ((uint)row.IdLow & 0xFFFFu);
		long index = (long)key - HelpKeyBase;
		if (index < 0 || index >= HelpSubjects.Length)
		{
			return;
		}

		string subject = HelpSubjects[index];
		values[HelpTitle] = Babel(strings, $"TEXT_TUTORIAL_MENU_{subject}_NAME");
		values[HelpText] = Babel(strings, $"TEXT_TUTORIAL_MENU_{subject}_DESC");
	}

	/// <summary>
	/// The morality and renown pages, from the guest's own accessors.
	///
	/// Bands are tested on the accessor's answer, never the raw field: `sub_8223D320` adds
	/// component 57's modifier maps plus the GDB `EvilMoralityAddition`/`HolyMoralityAddition`
	/// constants for a flagged hero, and with edges at 0.375/0.625 the raw field names the wrong
	/// personality near either one. Hence `RefiiHostGetHeroDerived` beside `…GetHeroVitals`.
	/// </summary>
	private static void HeroPages(
		Dictionary<string, string> values,
		Refii.Host.HostBridge.HeroDerived hero,
		Refii.Text.Engine_BabelDatabase? strings)
	{
		values["MoralityPage.Contents.LogBook.Morality.Name"] = string.Empty;
		values["MoralityPage.Contents.LogBook.Morality.Desc"] = string.Empty;
		values["MoralityPage.Contents.LogBook.Title"] = string.Empty;
		values["RenownPage.Contents.LogBook.Title"] = string.Empty;
		values["AttractivenessPage.Contents.LogBook.Title"] = string.Empty;
		values["StatsPage.Contents.LogBook.Title"] = string.Empty;
		values["DogPage.Contents.LogBook.Title"] = string.Empty;
		values["Dog.CombatAmount"] = string.Empty;
		values["Dog.SearchingAmount"] = string.Empty;
		values["Dog.CurrentStateAmount"] = string.Empty;

		foreach ((int _, string path) in StatRows)
		{
			values[$"Stats.{path}"] = string.Empty;
		}

		foreach (string row in AttractivenessRows)
		{
			values[row] = string.Empty;
		}
		values["RenownPage.Contents.LogBook.Renown.CurrentStateValue"] = string.Empty;

		// The four fixed axis captions are authored tags and do not depend on the hero at all.
		values["MoralityPage.Contents.LogBook.Morality.Evil"] =
			Babel(strings, "GUI_SCREEN_PERSONALITY_MORALITY_EVIL");
		values["MoralityPage.Contents.LogBook.Morality.Good"] =
			Babel(strings, "GUI_SCREEN_PERSONALITY_MORALITY_GOOD");
		values["MoralityPage.Contents.LogBook.Morality.Corrupt"] =
			Babel(strings, "GUI_SCREEN_PERSONALITY_MORALITY_CORRUPT");
		values["MoralityPage.Contents.LogBook.Morality.Pure"] =
			Babel(strings, "GUI_SCREEN_PERSONALITY_MORALITY_PURE");

		if (!hero.Known)
		{
			return;
		}

		string moral = Band(hero.MoralityFraction, "EVIL", "GOOD");
		string pure = Band(hero.PurityFraction, "CORRUPT", "PURE");

		// `(morality, purity)` in THAT order — the guest composes the tag the same way.
		values["MoralityPage.Contents.LogBook.Morality.Name"] =
			Babel(strings, $"GUI_SCREEN_PERSONALITY_MORALITY_{moral}_{pure}_NAME");
		values["MoralityPage.Contents.LogBook.Morality.Desc"] =
			Babel(strings, $"GUI_SCREEN_PERSONALITY_MORALITY_{moral}_{pure}_DESC");

		string title = Title(hero.TitleTag, strings);
		values["MoralityPage.Contents.LogBook.Title"] = title;
		values["RenownPage.Contents.LogBook.Title"] = title;

		values["RenownPage.Contents.LogBook.Renown.CurrentStateValue"] =
			hero.Renown.ToString(System.Globalization.CultureInfo.InvariantCulture);

		// THE SIX REGION ROWS — `sub_829B9960`/`sub_824E6168`: each shows the Babel tag
		// `GUI_SCREEN_PERSONALITY_RENOWN_LEVEL_<n>`, n = how many of the GDB
		// `RegionRenownBands` lie below the hero's renown plus that region's own bonus (the
		// per-region map at c57+168, which the sample does not carry — taken as 0), and blank
		// once the renown clears every band.
		string level = RenownLevelTag(hero.Renown);
		string levelText = level.Length == 0 ? string.Empty : Babel(strings, level);
		foreach (string region in RenownRegionRows)
		{
			values[$"RenownPage.Contents.LogBook.Renown.{region}"] = levelText;
		}

		values["AttractivenessPage.Contents.LogBook.Title"] = title;
		values["StatsPage.Contents.LogBook.Title"] = title;
		// The dog's name, or the Babel key the guest resolves in its place (`sub_823A8148`):
		// 'Dog' on an unnamed dog. Left on the authored tag when neither resolves.
		if (hero.DogKnown && !string.IsNullOrEmpty(hero.DogName))
		{
			values["DogPage.Contents.LogBook.Title"] = hero.DogName;
		}
		else if (hero.DogKnown && strings is not null &&
			strings.TryGetString(hero.DogNameKey, out string dogName))
		{
			values["DogPage.Contents.LogBook.Title"] = dogName;
		}
		else
		{
			values.Remove("DogPage.Contents.LogBook.Title");
		}

		if (hero.StatsKnown)
		{
			foreach ((int id, string path) in StatRows)
			{
				values[$"Stats.{path}"] =
					hero.Stat(id).ToString(System.Globalization.CultureInfo.InvariantCulture);
			}
		}

		if (hero.DogKnown)
		{
			values["Dog.CombatAmount"] =
				hero.DogCombat.ToString(System.Globalization.CultureInfo.InvariantCulture);
			values["Dog.SearchingAmount"] =
				hero.DogSearching.ToString(System.Globalization.CultureInfo.InvariantCulture);
			values["Dog.CurrentStateAmount"] = Babel(strings, DogStateTag(hero.DogState));
		}

		// Each accessor ends in `clamp(v * 100, +/-100)` and `sub_829BABF0` formats it with a
		// plain wide "%d": truncate, never round.
		for (int row = 0; row < AttractivenessRows.Length; ++row)
		{
			values[AttractivenessRows[row]] =
				row < hero.Attractiveness.Length
					? ((int)hero.Attractiveness[row]).ToString(
						System.Globalization.CultureInfo.InvariantCulture)
					: string.Empty;
		}
	}

	private static readonly string[] RenownRegionRows =
	{
		"BowerstoneValue", "RavenscarValue", "WestcliffValue",
		"BloodstoneValue", "FairfaxValue", "BowerlakeValue",
	};

	/// <summary>
	/// The `RegionRenownBands` record's numeric fields, ascending — read once. Every numeric
	/// field of the record is a band; the guest walks them sorted (`sub_824E6168`).
	/// </summary>
	private static float[]? _renownBands;

	private static string RenownLevelTag(int renown)
	{
		if (_renownBands is null)
		{
			var bands = new List<float>();
			if (Refii.Gdb.Engine_GameGdb.Database is { } multi &&
				multi.TryLookup(Refii.Core.Engine_GameHash.Fnv1("RegionRenownBands"), out Refii.Gdb.GdbCursor cursor))
			{
				foreach ((uint _, Refii.Gdb.GdbFieldType type, uint raw) in multi.InheritedFields(cursor))
				{
					switch (type)
					{
						case Refii.Gdb.GdbFieldType.Float:
							bands.Add(System.BitConverter.Int32BitsToSingle((int)raw));
							break;
						case Refii.Gdb.GdbFieldType.Int:
							bands.Add((int)raw);
							break;
					}
				}
			}

			bands.Sort();
			_renownBands = bands.ToArray();
			Godot.GD.Print($"[logbook] RegionRenownBands: {(_renownBands.Length == 0 ? "NOT FOUND" : string.Join(", ", _renownBands))}");
		}

		int index = 0;
		foreach (float band in _renownBands)
		{
			if (renown > band)
			{
				++index;
			}
		}

		return index >= _renownBands.Length ? string.Empty : $"GUI_SCREEN_PERSONALITY_RENOWN_LEVEL_{index}";
	}

	/// <summary>
	/// The dog's state caption. Eight tags, indexed by `c97+276`, which the guest clamps to 0
	/// for anything at or above 8 — so an unknown state reads NORMAL rather than blank.
	/// </summary>
	private static string DogStateTag(int state) => state switch
	{
		1 => "GUI_SCREEN_LOGBOOK_DOG_STATE_HAPPY",
		2 => "GUI_SCREEN_LOGBOOK_DOG_STATE_ALERT",
		3 => "GUI_SCREEN_LOGBOOK_DOG_STATE_ANGRY",
		4 => "GUI_SCREEN_LOGBOOK_DOG_STATE_HEELING",
		5 => "GUI_SCREEN_LOGBOOK_DOG_STATE_SCARED",
		6 => "GUI_SCREEN_LOGBOOK_DOG_STATE_SAD",
		7 => "GUI_SCREEN_LOGBOOK_DOG_STATE_LEADING",
		_ => "GUI_SCREEN_LOGBOOK_DOG_STATE_NORMAL",
	};

	/// <summary>Which of the three bands a normalised value falls in.</summary>
	private static string Band(float fraction, string low, string high) =>
		fraction < LowBand ? low : fraction > HighBand ? high : "NEUTRAL";

	/// <summary>
	/// The hero's title, resolved from the GDB record hash the pump published.
	///
	/// The pump must not call `sub_824D8A10`: it allocates (refcounts through `dword_83497088`
	/// and builds a string) and `operator new` on the guest tick thread hangs. It publishes the
	/// raw handle at `c57+92`; the chain is walked here against our own GDB —
	/// record -> `HeroTitleComponent` (reference) -> `TextTag`, a Babel tag, not localised text.
	/// </summary>
	private static string Title(string handle, Refii.Text.Engine_BabelDatabase? strings)
	{
		if (string.IsNullOrEmpty(handle) ||
			!uint.TryParse(handle, System.Globalization.NumberStyles.HexNumber,
				System.Globalization.CultureInfo.InvariantCulture, out uint record) ||
			record == 0u)
		{
			return string.Empty;
		}

		GdbMulti? multi = Engine_GameGdb.Database;
		if (multi is null || !multi.TryLookup(record, out GdbCursor at))
		{
			return string.Empty;
		}

		if (!multi.TryFindInherited(
				at, Refii.Core.Engine_GameHash.Fnv1("HeroTitleComponent"),
				(int)GdbFieldType.Reference, out _, out uint component, out _) ||
			!multi.TryLookup(component, out GdbCursor componentAt))
		{
			return string.Empty;
		}

		return Babel(strings, Text(multi, componentAt, "TextTag"));
	}

	/// <summary>A string property's text, following the record's parent chain.</summary>
	internal static string Text(GdbMulti multi, GdbCursor at, string property)
	{
		if (!multi.TryFindInherited(
				at, Refii.Core.Engine_GameHash.Fnv1(property), (int)GdbFieldType.String,
				out GdbCursor owner, out uint raw, out _) ||
			!owner.Valid)
		{
			return string.Empty;
		}

		return owner.View.Names.TryGetValue(raw, out string? text) ? text : string.Empty;
	}

	internal static string Babel(Refii.Text.Engine_BabelDatabase? strings, string tag) =>
		strings is not null && !string.IsNullOrEmpty(tag) &&
		strings.TryGetString(tag, out string value)
			? value
			: string.Empty;
}
