using System.Collections.Generic;

namespace Refii.Titles.Fable2;

using Refii.Ui;
using Entry = Refii.Ui.Engine_UiPopulationTable.Entry;
using Table = Refii.Ui.Engine_UiPopulationTable;

/// <summary>
/// `expandablemenupopulationscript.lua`, translated — the static content of the menus that have
/// any. The script switches on an `ExpandableMenu`'s `MenuPopulationScript` attribute and each
/// arm assigns `g_Menu.PopulationTable`. Twenty names are answered; most get an empty table
/// because their rows are spawned at runtime by C++. These are the tables to edit — entries
/// here, or in `user://ui/templates.json`, which overrides them, reach the screen through the
/// same `SpawnButton` path. `PauseMenu` states only `HighlightIdx = 2`.
/// </summary>
public static class Engine_UiMenuPopulation
{
	private const string Wheel = "Art\\GUI\\WheelIcons\\";
	private const string Expressions = "Art\\GUI\\Expressions\\";

	private static Entry Row(string name, string tag, string icon,
		Engine_UiMenuConstants.EventType type, string parent = "") =>
		new() { Name = name, TextTag = tag, IconTexture = icon, EventType = type, Parent = parent };

	private static readonly Dictionary<string, Table> Tables = Build();

	/// <summary>The table for a `MenuPopulationScript` name, or null when it states none.</summary>
	public static Table? For(string script) =>
		Tables.TryGetValue(script, out Table? table) ? table : null;

	/// <summary>Every translated table, for the editor's listing and the exporter.</summary>
	public static IEnumerable<Table> All => Tables.Values;

	/// <summary>
	/// Fills a model from a static table, exactly as the script's own repopulation does:
	/// `RepopulateFromPopulationTable` walks the entries, calls `CreateMenuItem` for each and
	/// `AddChild` under the named parent, then sets `CurrentHighlightIdx`.
	/// </summary>
	public static void Populate(Engine_UiExpandableMenu menu, Table table)
	{
		menu.Clear();
		foreach (Entry entry in table.Entries)
		{
			Engine_UiExpandableMenu.Item item = menu.SpawnButton(
				entry.Name, entry.TextTag, entry.IconTexture, (int)entry.EventType,
				idLow: 0, idHigh: 0,
				ratingSlide: Engine_UiMenuConstants.EmptyRatingSlide,
				parentName: entry.Parent.Length == 0
					? Engine_UiMenuConstants.RootName
					: entry.Parent);

			// A static entry has no runtime id: committing by identity would push `(type, 0)` and
			// resolve row zero. These go through the A press, where the dispatch reads the name.
			item.Committable = false;
		}

		if (table.HighlightIndex >= 1)
		{
			menu.Highlight(table.HighlightIndex);
		}
	}

	private static Dictionary<string, Table> Build()
	{
		var tables = new Dictionary<string, Table>(System.StringComparer.Ordinal);

		void Add(Table table) => tables[table.Script] = table;

		// The pause menu states only a starting cursor; its rows are the C++ side's.
		Add(new Table { Script = "PauseMenu", HighlightIndex = 2 });

		Add(new Table
		{
			Script = "HenchmanPauseMenu",
			Entries =
			{
				Row("HenchmanAbilities", "GUI_SCREEN_PAUSE_HENCHMAN_ABILITIES",
					Wheel + "icon_abilities.tga",
					Engine_UiMenuConstants.EventType.EnterScreen),
				Row("HenchmanConfig", "GUI_SCREEN_PAUSE_HENCHMAN_CONFIG",
					Wheel + "icon_options.tga",
					Engine_UiMenuConstants.EventType.EnterScreen),
				Row("HenchmanQuit", "GUI_SCREEN_PAUSE_HENCHMAN_QUIT",
					Wheel + "icon_options.tga",
					Engine_UiMenuConstants.EventType.EnterScreen),
			},
		});

		Add(new Table
		{
			Script = "OptionsMenu",
			Entries =
			{
				Row("Game", "GUI_SCREEN_OPTIONS_GAME",
					Wheel + "Abilities\\icon_logbook.tga",
					Engine_UiMenuConstants.EventType.OptionsGameItem),
				Row("Video", "GUI_SCREEN_OPTIONS_VIDEO",
					Wheel + "Abilities\\icon_logbook.tga",
					Engine_UiMenuConstants.EventType.OptionsVideoItem),
				Row("Controls", "GUI_SCREEN_OPTIONS_CONTROLS",
					Wheel + "Abilities\\icon_logbook.tga",
					Engine_UiMenuConstants.EventType.OptionsControlsItem),
				Row("Audio", "GUI_SCREEN_OPTIONS_AUDIO",
					Wheel + "Abilities\\icon_logbook.tga",
					Engine_UiMenuConstants.EventType.OptionsAudioItem),
			},
		});

		// The logbook nests: `Personality` is a submenu holding the five readouts;
		// `Expressions`, `CompletedQuests` and `Help` are root submenus, `Achievements` a plain
		// row. Entry order is the script's, and display order is table order.
		const string Logbook = Wheel + "icon_logbook.tga";
		Add(new Table
		{
			Script = "LogbookMenu",
			Entries =
			{
				Row("Personality", "GUI_SCREEN_LOGBOOK_PERSONALITY", Logbook,
					Engine_UiMenuConstants.EventType.Submenu),
				Row("Alignment", "GUI_SCREEN_LOGBOOK_ALIGNMENT", Logbook,
					Engine_UiMenuConstants.EventType.LogbookPersonalityMoralityItem, "Personality"),
				Row("Renown", "GUI_SCREEN_LOGBOOK_RENOWN", Logbook,
					Engine_UiMenuConstants.EventType.LogbookPersonalityRenownItem, "Personality"),
				Row("Attractiveness", "GUI_SCREEN_LOGBOOK_ATTRACTIVENESS", Logbook,
					Engine_UiMenuConstants.EventType.LogbookPersonalityAttractivenessItem,
					"Personality"),
				Row("Stats", "GUI_SCREEN_LOGBOOK_STATS", Logbook,
					Engine_UiMenuConstants.EventType.LogbookPersonalityStatsItem, "Personality"),
				Row("Dog", "GUI_SCREEN_LOGBOOK_DOG", Logbook,
					Engine_UiMenuConstants.EventType.LogbookPersonalityDogItem, "Personality"),
				Row("Expressions", "GUI_SCREEN_LOGBOOK_EXPRESSIONS", Logbook,
					Engine_UiMenuConstants.EventType.Submenu),
				Row("CompletedQuests", "GUI_SCREEN_LOGBOOK_COMPLETED_QUESTS", Logbook,
					Engine_UiMenuConstants.EventType.Submenu),
				Row("Achievements", "GUI_SCREEN_LOGBOOK_ACHIEVEMENTS", Logbook,
					Engine_UiMenuConstants.EventType.LogbookAchievements),
				Row("Help", "GUI_SCREEN_LOGBOOK_HELP", Logbook,
					Engine_UiMenuConstants.EventType.Submenu),
			},
		});

		// The orb variant — one row, reached when the logbook is opened on an experience orb.
		Add(new Table
		{
			Script = "LogbookMenuOrb",
			Entries =
			{
				Row("Stats", "GUI_SCREEN_LOGBOOK_STATS", Logbook,
					Engine_UiMenuConstants.EventType.LogbookPersonalityStatsItem),
			},
		});

		Add(new Table
		{
			Script = "CrimeMenu",
			Entries =
			{
				Row("PayFine", "GUI_SCREEN_CRIME_PAY_FINE",
					Wheel + "Rewards\\reward_money.tga",
					Engine_UiMenuConstants.EventType.CrimePayFineItem),
				Row("CommunityService", "GUI_SCREEN_CRIME_COMMUNITY_SERVICE",
					Wheel + "icon_abilities.tga",
					Engine_UiMenuConstants.EventType.CrimeCommunityServiceItem),
				Row("ResistArrest", "GUI_SCREEN_CRIME_RESIST_ARREST",
					Expressions + "Rude\\scary_laugh.tga",
					Engine_UiMenuConstants.EventType.CrimeResistArrestItem),
			},
		});

		Add(new Table
		{
			Script = "ExpressionsMenu",
			Entries =
			{
				Row("Friendly", "GUI_SCREEN_EXPRESSIONS_FRIENDLY",
					Expressions + "Friendly\\friendly.tga",
					Engine_UiMenuConstants.EventType.Submenu),
				Row("Fun", "GUI_SCREEN_EXPRESSIONS_FUN",
					Expressions + "Fun\\fun.tga",
					Engine_UiMenuConstants.EventType.Submenu),
				Row("Flirty", "GUI_SCREEN_EXPRESSIONS_FLIRTY",
					Expressions + "Flirty\\flirt.tga",
					Engine_UiMenuConstants.EventType.Submenu),
				Row("Rude", "GUI_SCREEN_EXPRESSIONS_RUDE",
					Expressions + "Rude\\rude.tga",
					Engine_UiMenuConstants.EventType.Submenu),
				Row("Scary", "GUI_SCREEN_EXPRESSIONS_SCARY",
					Expressions + "Rude\\scary_laugh.tga",
					Engine_UiMenuConstants.EventType.Submenu),
			},
		});

		// The two category pickers. Both use the world-map icon for every row, as authored.
		const string Category = Wheel + "icon_world_map.tga";
		Add(new Table
		{
			Script = "AugmentMenu",
			Entries =
			{
				Row("Ranged", "GUI_SCREEN_INVENTORY_CATEGORIES_RANGED_WEAPONS", Category,
					Engine_UiMenuConstants.EventType.Submenu),
				Row("Melee", "GUI_SCREEN_INVENTORY_CATEGORIES_MELEE_WEAPONS", Category,
					Engine_UiMenuConstants.EventType.Submenu),
				Row("Accessories", "GUI_SCREEN_INVENTORY_CATEGORIES_ACCESSORIES", Category,
					Engine_UiMenuConstants.EventType.Submenu),
			},
		});

		Add(new Table
		{
			Script = "DyeMenu",
			Entries =
			{
				Row("Hats", "GUI_SCREEN_INVENTORY_CATEGORIES_HATS", Category,
					Engine_UiMenuConstants.EventType.Submenu),
				Row("Coats", "GUI_SCREEN_INVENTORY_CATEGORIES_COATS", Category,
					Engine_UiMenuConstants.EventType.Submenu),
				Row("Shirts", "GUI_SCREEN_INVENTORY_CATEGORIES_SHIRTS", Category,
					Engine_UiMenuConstants.EventType.Submenu),
				Row("Gloves", "GUI_SCREEN_INVENTORY_CATEGORIES_GLOVES", Category,
					Engine_UiMenuConstants.EventType.Submenu),
				Row("Pants", "GUI_SCREEN_INVENTORY_CATEGORIES_PANTS", Category,
					Engine_UiMenuConstants.EventType.Submenu),
				Row("Shoes", "GUI_SCREEN_INVENTORY_CATEGORIES_SHOES", Category,
					Engine_UiMenuConstants.EventType.Submenu),

				// The script's own name/tag mismatch — row `Accessories`, tag `..._MASKS`. As shipped.
				Row("Accessories", "GUI_SCREEN_INVENTORY_CATEGORIES_MASKS", Category,
					Engine_UiMenuConstants.EventType.Submenu),
			},
		});

		// Empty by design: every row of these is spawned at runtime by the C++ side.
		foreach (string runtime in new[]
			{
				"AbilitiesMenu", "EquipmentMenu", "GiftGivingMenu", "ShopMenu", "WorldMapMenu",
				"RegionsMenu", "ItemsMenu", "WeaponsMenu", "ClothingMenu", "FrontEndMainMenu",
				"RealEstateMenu", "FurnitureMenu",
			})
		{
			Add(new Table { Script = runtime });
		}

		return tables;
	}
}
