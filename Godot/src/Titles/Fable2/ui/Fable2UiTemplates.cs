using System.Collections.Generic;
using Godot;

namespace Refii.Titles.Fable2;

using Refii.Ui;
using static Refii.Ui.Engine_UiTemplates;

/// <summary>
/// Fable 2's own UI templates: its menus' population tables (translated from
/// `expandablemenupopulationscript.lua`), the screen and menu records decoded from the binary, and
/// the templates file generated from them (<see cref="Engine_UiGameTemplates.Text"/>). The shell
/// applies them first, before any mod's (<see cref="Engine_UiTemplates"/>).
/// </summary>
public static class Fable2UiTemplates
{
	/// <summary>The game's records, as the shell composes them: the population tables, then the file.</summary>
	public static void Apply(Engine_UiTemplateSet set)
	{
		RegisterPopulationTables(set.Menus);
		set.ApplyText(Engine_UiGameTemplates.Text, owner: string.Empty, "the game's templates", shipped: true);
	}

	/// <summary>
	/// The game's records as AUTHORED here in C#, beside the evidence each was decoded from: the
	/// population tables, <see cref="BuildDefaults"/> and the wide layouts. Never what runs — the
	/// game runs <see cref="Engine_UiGameTemplates.Text"/> through the reader like a mod — but the
	/// form that file is generated from (<see cref="GameTemplatesText"/>), and a test holds the two
	/// identical record for record.
	/// </summary>
	public static Engine_UiTemplateSet AuthoredDefaults()
	{
		var set = new Engine_UiTemplateSet();
		RegisterPopulationTables(set.Menus);
		BuildDefaults(set.Menus, set.Screens);
		StateWideLayouts(set.Screens);
		StateRestStates(set.Screens);
		StatePurses(set.Screens);
		return set;
	}

	/// <summary>
	/// The game's templates file as generated from <see cref="AuthoredDefaults"/>: every menu the
	/// authored records add or change over the population tables, whole, and every screen, each
	/// field only where it is not the reader's default. <see cref="Engine_UiGameTemplates.Text"/>
	/// is this text.
	/// </summary>
	public static string GameTemplatesText()
	{
		Engine_UiTemplateSet authored = AuthoredDefaults();
		var tables = new Dictionary<string, Engine_UiMenuTemplate>();
		RegisterPopulationTables(tables);

		var menus = new System.Text.Json.Nodes.JsonObject();
		foreach ((string name, Engine_UiMenuTemplate menu) in authored.Menus)
		{
			System.Text.Json.Nodes.JsonObject record = Engine_UiTemplateJson.MenuJson(menu);
			if (tables.TryGetValue(name, out Engine_UiMenuTemplate? table) &&
				Engine_UiTemplateJson.Write(Engine_UiTemplateJson.MenuJson(table)) == Engine_UiTemplateJson.Write(record))
			{
				continue;
			}

			menus[name] = record;
		}

		var screens = new System.Text.Json.Nodes.JsonObject();
		foreach ((uint id, Engine_UiScreenTemplate screen) in authored.Screens)
		{
			screens[id.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
				Engine_UiTemplateJson.ScreenJson(screen, full: false);
		}

		return Engine_UiTemplateJson.Write(new System.Text.Json.Nodes.JsonObject
		{
			["menus"] = menus,
			["screens"] = screens,
		});
	}

	// -----------------------------------------------------------------------------------
	// The built-in records — the decoded contracts as data.
	// -----------------------------------------------------------------------------------

	/// <summary>
	/// Registers every table translated from `expandablemenupopulationscript.lua` as a menu
	/// record, keyed by the `MenuPopulationScript` name the `.bgf` states. These are the EDITABLE
	/// form of those lists — snapshotted and overridable — never a second source of content: the
	/// rows a screen shows at runtime are the guest's, through the button pool.
	///
	/// A hand-written record with the same name wins — `BuildDefaults` runs this first and then
	/// states its own, so the pause and front-end roots keep the forms decoded from the binary.
	/// </summary>
	private static void RegisterPopulationTables(Dictionary<string, Engine_UiMenuTemplate> menus)
	{
		foreach (Engine_UiPopulationTable table in Engine_UiMenuPopulation.All)
		{
			var menu = new Engine_UiMenuTemplate { Name = table.Script };
			var byName = new Dictionary<string, Engine_UiMenuEntry>(System.StringComparer.Ordinal);

			foreach (Engine_UiPopulationTable.Entry entry in table.Entries)
			{
				var record = new Engine_UiMenuEntry
				{
					Name = entry.Name,
					Tag = entry.TextTag,
					Icon = entry.IconTexture,
					EventType = (int)entry.EventType,
				};
				byName[entry.Name] = record;

				if (entry.Parent.Length != 0 &&
					byName.TryGetValue(entry.Parent, out Engine_UiMenuEntry? parent))
				{
					parent.Children.Add(record);
				}
				else
				{
					menu.Entries.Add(record);
				}
			}

			if (table.HighlightIndex >= 1 && table.HighlightIndex <= menu.Entries.Count)
			{
				menu.Highlight = menu.Entries[table.HighlightIndex - 1].Name;
			}

			// EVERY MENU DECLARES WHERE ITS ROWS COME FROM. A table with no authored entries is
			// one the guest populates at runtime — the item, clothing, quest and region lists —
			// and its rows are the guest's DATA, not its layout. Naming `capture:Root` here says
			// that in the template rather than in a branch in the list builder: the assembly is
			// the same for every menu and only the SOURCE differs, so a screen that breaks
			// breaks the way all of them would.
			//
			// `capture` is the spawn capture, which is where those rows live today. Pointing a
			// menu at a real data-area provider later is an edit to this one field.
			if (menu.Entries.Count == 0)
			{
				menu.Source = "capture:" + Engine_UiExpandableMenu.RootName;
			}

			// A CATEGORY WITH NO AUTHORED CHILDREN TAKES THEM FROM THE CAPTURE. The population
			// script declares the category and the guest fills it at runtime — the logbook's
			// `Expressions` and `CompletedQuests`, the equipment screen's slots — so an entry
			// that is a submenu and authors nothing under itself is not an empty menu, it is one
			// whose children are data.
			//
			// It has to say so, because `UpdateAllGhosting` auto-ghosts a submenu with zero
			// children (`AutomaticGhostingOnSubmenus`) and a ghosted row refuses its own press.
			// Without this the logbook built three categories that were greyed out and could not
			// be opened at all.
			foreach (Engine_UiMenuEntry entry in menu.Entries)
			{
				SourceChildlessSubmenus(entry);
			}

			menus[table.Script] = menu;
		}
	}

	/// <summary>`sub_829A4680` and its three siblings — all four abilities rows share it.</summary>
	private const string AbilityHeaderIcon =
		@"Art\GUI\WheelIcons\Abilities\icon_strength.tga";

	/// <summary>One abilities category — its children read from the GDB and the hero's levels.</summary>
	private static Engine_UiMenuEntry AbilityCategory(string name, string tag) =>
		new()
		{
			Name = name,
			Tag = tag,
			Icon = AbilityHeaderIcon,
			EventType = (int)Engine_UiMenuConstants.EventType.Submenu,
			Source = Engine_UiAbilitySource.SourceName + ":" + name,
		};

	/// <summary>
	/// One shop group row — `sub_82994218` states this icon for all six
	/// (`dword_8349D040`). Children come from the capture; see the call site.
	/// </summary>
	private static Engine_UiMenuEntry ShopCategory(string name, string tag) => new()
	{
		Name = name,
		Tag = tag,
		Icon = @"Art\GUI\WheelIcons\icon_items.tga",
		EventType = (int)Engine_UiMenuConstants.EventType.Submenu,

		// The capture's rows with the transaction stamped on — see Engine_UiMenuSources.ShopRows.
		// Without a command a shop row falls back to an identity push, and the shop's buy and
		// sell are reached through the screen's own dispatcher, which takes a ticket rather than
		// anything an identity names.
		Source = Fable2MenuSources.ShopSource + ":" + name,
	};

	/// <summary>One weapons-screen category row: the rows the guest spawned under it (`sub_826CEB60` files a weapon
	/// under "Melee" or "Ranged" by its `WeaponType`), their A the equip, the caption the guest's header
	/// (`sub_829B1890`, 0x829B1900 / 0x829B195C).</summary>
	private static Engine_UiMenuEntry WeaponCategory(string name, string tag) => new()
	{
		Name = name,
		Tag = tag,

		// `dword_8349D930` again — the shared category icon.
		Icon = @"Art\GUI\WheelIcons\icon_world_map.tga",
		EventType = (int)Engine_UiMenuConstants.EventType.Submenu,
		Source = $"{Engine_UiItemRows.SourceName}:equip:{name}",
		Counted = false,
	};

	/// <summary>
	/// One of the world map's five guest categories — a submenu whose children are the rows the
	/// guest's population spawned under it (`capture:&lt;name&gt;`), with the guest's own (type, id)
	/// and its own caption: `sub_826CF098` spawns the header with the count of its list and the
	/// unviewed mark appended (" (", the count, ")" and `&lt;exclamation.img&gt;`, 0x826CF0D8..0x826CF134),
	/// so the template counts nothing (<see cref="Engine_UiMenuEntry.Counted"/> false) and the header
	/// takes that caption from the same population.
	/// </summary>
	private static Engine_UiMenuEntry WorldMapCategory(string name, string tag) =>
		new()
		{
			Name = name,
			Tag = tag,

			// `dword_8349D354` — every world-map child row states this icon.
			Icon = @"Art\GUI\WheelIcons\icon_logbook.tga",
			EventType = (int)Engine_UiMenuConstants.EventType.Submenu,
			Source = "capture:" + name,
			Counted = false,
		};

	/// <summary>The entry, with its caption's `" (N)"` suppressed — see `Engine_UiMenuEntry.Counted`.</summary>
	private static Engine_UiMenuEntry Uncounted(Engine_UiMenuEntry entry)
	{
		entry.Counted = false;
		return entry;
	}

	/// <summary>One item-screen category row: a submenu whose children are the data areas.</summary>
	/// <summary>
	/// The category rows' shared tag prefix. All eighteen resolve in the shipped `book.babel`,
	/// checked before use — and note the display text is NOT the internal name: `..._TOOLS` is
	/// 'Miscellaneous', `..._BOOKS` is 'Books &amp; Documents', `..._SHIRTS` is 'Upper Body'.
	/// </summary>
	private const string CategoryTag = "GUI_SCREEN_INVENTORY_CATEGORIES_";

	/// <summary>
	/// One category row of an item book: the rows the guest spawned under it, with the row's A stamped on as
	/// <paramref name="operation"/> (empty: the rows commit as themselves), and the guest's own header caption
	/// (`sub_826CF098`: the count and the new mark; `sub_826CF020` for the Outfits: none).
	/// </summary>
	private static Engine_UiMenuEntry ItemCategory(string name, string tag, string operation) =>
		new()
		{
			Name = name,
			Tag = tag,

			// `dword_8349D930` — every one of the nine states the same icon.
			Icon = @"Art\GUI\WheelIcons\icon_world_map.tga",
			EventType = (int)Engine_UiMenuConstants.EventType.Submenu,
			Source = $"{Engine_UiItemRows.SourceName}:{operation}:{name}",
			Counted = false,
		};

	/// <summary>
	/// Points a childless submenu entry at the spawn capture for its children, recursively. See
	/// the call site for why an authored category must declare this.
	/// </summary>
	private static void SourceChildlessSubmenus(Engine_UiMenuEntry entry)
	{
		foreach (Engine_UiMenuEntry child in entry.Children)
		{
			SourceChildlessSubmenus(child);
		}

		if (entry.Children.Count == 0 && entry.Source.Length == 0 && entry.Name.Length != 0 &&
			entry.EventType == (int)Engine_UiMenuConstants.EventType.Submenu)
		{
			entry.Source = "capture:" + entry.Name;
		}
	}

	private static void BuildDefaults(
		Dictionary<string, Engine_UiMenuTemplate> menus, Dictionary<uint, Engine_UiScreenTemplate> screens)
	{
		// THE LOGBOOK DOES NOT OFFER `Achievements`. The guest spawns the row — it is in its own
		// population table — and its action is `XamShowAchievementsUI` (`sub_826D9620` case 150),
		// the console's guide blade, which on this platform opens nothing at all. Every press of
		// it was a press that did nothing.
		//
		// The achievements are their own screen now, on their own pause tab; see
		// `Engine_UiHostScreen`. This is the statement that the dead row goes, and it is a
		// TEMPLATE RECORD rather than a special case in the list builder, so any other row on any
		// other guest menu can be dropped the same way — and put back, from
		// `user://ui/templates.json`, without a rebuild.
		// THE AUGMENT AND REMOVER TARGETS: the items book's own lists (`sub_829B0BF0` / `sub_829B10A8`, entered by
		// the augment's and the remover's own events 83 / 310), both under the script 'AugmentMenu'
		// (`dword_8349D92C`, 0x829B0C58 / 0x829B1110) whose shipped table authors Ranged, Melee and Accessories;
		// the guest files each target under its own category (`sub_826CEB60`). The tab strip's title is the
		// guest's (`sub_826B81C0` "GUI_SCREEN_TITLE_AUGMENT" / "GUI_SCREEN_AUGMENT_REMOVAL_TITLE").
		// THE DYE TARGETS: the clothing book in dye mode (`sub_829B2168`, the script 'DyeMenu' `dword_8349D928`,
		// 0x829B2228), its seven shipped categories, and the `Dye Hero Hair` category it spawns itself under
		// Root with the worn hair, beard and moustache (0x829B26B8 .. 0x829B2988).
		foreach (string targets in new[] { "AugmentMenu", "DyeMenu" })
		{
			if (!menus.TryGetValue(targets, out Engine_UiMenuTemplate? list))
			{
				continue;
			}

			foreach (Engine_UiMenuEntry entry in list.Entries)
			{
				entry.Source = $"{Engine_UiItemRows.SourceName}::{entry.Name}";
				entry.Counted = false;
			}

			if (targets == "DyeMenu")
			{
				list.Entries.Add(new Engine_UiMenuEntry
				{
					Name = "Dye Hero Hair",
					Tag = CategoryTag + "DYE_HERO_HAIR",
					Icon = @"Art\GUI\WheelIcons\icon_world_map.tga",
					EventType = (int)Engine_UiMenuConstants.EventType.Submenu,
					Source = $"{Engine_UiItemRows.SourceName}::Dye Hero Hair",
					Counted = false,
				});
			}
		}

		if (menus.TryGetValue("OptionsMenu", out Engine_UiMenuTemplate? options) &&
			options.Entries.FindIndex(entry => entry.Name == "Video") is >= 0 and var videoIndex)
		{
			Engine_UiMenuEntry gamma = options.Entries[videoIndex];
			gamma.Tag = "GUI_SCREEN_OPTIONS_GAMMA";
			options.Entries[videoIndex] = new Engine_UiMenuEntry
			{
				Name = "VideoSettings",
				Tag = "GUI_SCREEN_OPTIONS_VIDEO",
				Icon = gamma.Icon,
				HostOnly = true,
				Counted = false,
				Children =
				{
					gamma,
					new Engine_UiMenuEntry
					{
						Name = "DynamicSkyAndWeather",
						Tag = "REFII_F2_DYNAMIC_SKY",
						Icon = gamma.Icon,
						HostOnly = true,
						Counted = false,
						Command = Engine_UiWeatherOptions.OpenCommand,
					},
				},
			};

			// MODS: a fold with one row per enabled mod that ships settings, each opening that
			// mod's host options page (Engine_UiHostOptionsPage) on the same page furniture the
			// sky's switches use. Sourced, so the fold follows the load order and an empty one
			// leaves the list; host-only, because the guest has no screen behind any of it.
			options.Entries.Insert(videoIndex + 1, new Engine_UiMenuEntry
			{
				Name = "ModSettings",
				Tag = "REFII_MODS",
				Icon = gamma.Icon,
				HostOnly = true,
				Counted = false,
				Source = Refii.Mods.ModSettingsUi.SourceName,
			});
		}

		// CONTROLS: the game's own Controls page folded with the game's rumble switch
		// (Engine_UiVibrationOptions — the guest's switch, which shipped with no page behind it)
		// and the one host Controls page (Engine_UiControlsPage): glyph style and pack, the
		// controller remap, every keyboard and mouse binding and every mod's actions. The guest's
		// row keeps its name and event, so the fold's first child is the page it always was.
		if (options is not null &&
			options.Entries.FindIndex(entry => entry.Name == "Controls") is >= 0 and var controlsIndex)
		{
			Engine_UiMenuEntry controls = options.Entries[controlsIndex];
			options.Entries[controlsIndex] = new Engine_UiMenuEntry
			{
				Name = "ControlsSettings",
				Tag = controls.Tag,
				Icon = controls.Icon,
				HostOnly = true,
				Counted = false,
				Children =
				{
					controls,
					new Engine_UiMenuEntry
					{
						Name = Engine_UiVibrationOptions.PageName,
						Tag = Engine_UiVibrationOptions.PageTitle,
						Icon = controls.Icon,
						HostOnly = true,
						Counted = false,
						Command = Engine_UiVibrationOptions.OpenCommand,
					},
					new Engine_UiMenuEntry
					{
						Name = Engine_UiControlsPage.PageName,
						Tag = Engine_UiControlsPage.PageTitle,
						Icon = controls.Icon,
						HostOnly = true,
						Counted = false,
						Command = Engine_UiControlsPage.OpenCommand,
					},
				},
			};
		}

		// THE FOUR GUEST PAGES open as Godot's page level: the ported options handler takes no page
		// input, so each page row names its page (Engine_UiOptionsPending) — Game 70, Controls 71,
		// Video 72, Audio 73, the event types the handler's '$' arm turned into a page (`data - 70`,
		// TU1 0x826DA404).
		if (options is not null)
		{
			void NamePages(System.Collections.Generic.List<Engine_UiMenuEntry> entries)
			{
				foreach (Engine_UiMenuEntry entry in entries)
				{
					if (entry.EventType is >= 70 and <= 73 && Engine_UiOptionsPending.IsGuestPage(entry.Name))
					{
						entry.Command = Fable2UiVerbs.OptionsPageAction + entry.Name;
					}

					NamePages(entry.Children);
				}
			}

			NamePages(options.Entries);
		}

		if (menus.TryGetValue("LogbookMenu", out Engine_UiMenuTemplate? logbook))
		{
			foreach (Engine_UiMenuEntry entry in logbook.Entries)
			{
				if (string.Equals(entry.Name, Engine_UiAchievementPage.FolderRowName,
						System.StringComparison.Ordinal))
				{
					entry.Drop = true;
				}
			}
		}

		// THE PAUSE ROOT — ONE RECORD FOR BOTH PLAYERS. The hero's rows are `sub_8298DF98`'s spawn
		// list verbatim: names are the identities its dispatch recognises, the Saving children
		// dispatch by EVENT PARAM ({type 2, param}), the tabs by the tab lever's ids, and
		// SetInitiallySelectedItem(2) is the highlight. Player two's rows are ours (below) and
		// stand under the row condition `player-two` (Engine_UiRowConditions), which
		// `Engine_PauseMenu.BuildHero` states; a row with no condition is on both lists, so a mod
		// patches this one record and reaches both players.
		const string hero = "!" + Engine_UiRowConditions.PlayerTwo;
		const string playerTwo = Engine_UiRowConditions.PlayerTwo;

		// A HERO'S PAGES ARE A HERO'S. Player two's Map, Items, Weapons, Clothing and Logbook stand only
		// while player two brought a hero (`player-two-hero`); the plain henchman's book is the 360's
		// henchman list (`sub_8298EDE0`): Abilities, Options and HenchmanQuit.
		const string playerTwoHero = playerTwo + ", " + Engine_UiRowConditions.PlayerTwoHero;
		menus["pause-root"] = new Engine_UiMenuTemplate
		{
			Name = "pause-root",
			Highlight = "Map",
			Entries =
			{
				new Engine_UiMenuEntry
				{
					Name = "Saving", Tag = "GUI_SCREEN_PAUSE_SAVING", When = hero,

					// AUTHORED, NOT SOURCED. `sub_8298DF98` spawns three of these under
					// "Saving" once (0x8298E0F8/E138/E178, EventType 2, String1 = the localised
					// label); sourcing them as well seats the same rows twice, and only the
					// authored ones carry the `PauseSaving:N` commands.
					//
					// FIVE ROWS. The guest's "quit" means quit to its TITLE; the console owned
					// everything past that, so there is no shipped row that leaves the app. The
					// two desktop rows are HOST-ONLY and post the SAME guest event as their
					// title counterparts, so the guest's confirmation box and its save
					// sequencing run either way — see Engine_PauseMenu.QuitCommandPrefix.
					//
					// THE CONSOLE'S PAIR IS THE GAME'S OWN TEXT: its quit goes to the title, and the
					// bank says it as "Save and Quit" (`GUI_SCREEN_PAUSE_SAVE_AND_QUIT`) and "Quit and
					// Don't Save" (`GUI_SCREEN_PAUSE_QUIT_DONT_SAVE`), in every language it ships. The
					// two desktop rows are the PC's additions, which the bank has no words for: our
					// own (Refii.Text.Engine_OwnWords), said in the language in force.
					Children =
					{
						new Engine_UiMenuEntry
						{
							Name = "SaveAndContinue",
							Tag = "GUI_SCREEN_PAUSE_SAVE_AND_CONTINUE",
							Command = $"{Engine_PauseMenu.SavingCommandPrefix}0",
						},
						new Engine_UiMenuEntry
						{
							Name = "SaveAndQuit",
							Tag = "GUI_SCREEN_PAUSE_SAVE_AND_QUIT",
							Command = $"{Engine_PauseMenu.SavingCommandPrefix}1",
						},
						new Engine_UiMenuEntry
						{
							Name = "SaveAndQuitToDesktop", HostOnly = true,
							Tag = "REFII_F2_SAVE_QUIT_DESKTOP",
							Command = $"{Engine_PauseMenu.QuitCommandPrefix}1",
						},
						new Engine_UiMenuEntry
						{
							Name = "QuitDontSave",
							Tag = "GUI_SCREEN_PAUSE_QUIT_DONT_SAVE",
							Command = $"{Engine_PauseMenu.SavingCommandPrefix}2",
						},
						new Engine_UiMenuEntry
						{
							Name = "QuitToDesktopDontSave", HostOnly = true,
							Tag = "REFII_F2_QUIT_DESKTOP_NO_SAVE",
							Command = $"{Engine_PauseMenu.QuitCommandPrefix}2",
						},
					},
				},

				// THE TABS, A VARIANT PER PLAYER. Player two is a hero too — the hero's book, Fable
				// 3's own menus per player (user, 2026-10-19) — but their tabs are HOST-ONLY (see
				// `HostTab`): the guest's capture on that machine is the henchman list, and the
				// stated-label pass would hand these rows its captions by ordinal.
				Tab("Map", "GUI_SCREEN_PAUSE_WORLD_MAP", Engine_PauseMenu.TabMap, hero),
				HostTab("Map", "GUI_SCREEN_PAUSE_WORLD_MAP", Engine_PauseMenu.TabMap, playerTwoHero),
				Tab("Abilities", "GUI_SCREEN_PAUSE_ABILITIES", Engine_PauseMenu.TabAbilities, hero),
				HostTab("Abilities", "GUI_SCREEN_PAUSE_ABILITIES", Engine_PauseMenu.TabAbilities, playerTwo),
				Tab("Items", "GUI_SCREEN_PAUSE_ITEMS", Engine_PauseMenu.TabItems, hero),
				HostTab("Items", "GUI_SCREEN_PAUSE_ITEMS", Engine_PauseMenu.TabItems, playerTwoHero),
				Tab("Weapons", "GUI_SCREEN_PAUSE_WEAPONS", Engine_PauseMenu.TabWeapons, hero),
				HostTab("Weapons", "GUI_SCREEN_PAUSE_WEAPONS", Engine_PauseMenu.TabWeapons, playerTwoHero),
				Tab("Clothing", "GUI_SCREEN_PAUSE_CLOTHING", Engine_PauseMenu.TabClothing, hero),
				HostTab("Clothing", "GUI_SCREEN_PAUSE_CLOTHING", Engine_PauseMenu.TabClothing, playerTwoHero),
				Tab("Logbook", "GUI_SCREEN_PAUSE_LOGBOOK", Engine_PauseMenu.TabLogbook, hero),
				HostTab("Logbook", "GUI_SCREEN_PAUSE_LOGBOOK", Engine_PauseMenu.TabLogbook, playerTwoHero),

				// ACHIEVEMENTS — a host tab, sitting between Logbook and Options because THIS
				// LIST IS THE ORDER. `HostOnly` keeps the entry out of the label join against the
				// guest's button pool and out of the availability mask, neither of which has a row
				// for it, so the guest's own rows still join by NAME wherever they end up.
				//
				// Its command OPENS A SCREEN (Engine_UiHostScreens) — `sub_826B89E0`'s switch has
				// no case to select — which is what lets it sit anywhere at all. The tag is OUR
				// OWN key (Refii.Text.Engine_OwnWords): the shipped text bank has no achievements string.
				new Engine_UiMenuEntry
				{
					Name = "Achievements", Tag = "REFII_ACHIEVEMENTS", HostOnly = true,
					Command = Engine_UiHostScreens.OpenCommand(Engine_UiAchievementPage.ScreenId),
				},

				// The multiplayer book, on the same verb.
				new Engine_UiMenuEntry
				{
					Name = "Multiplayer", Tag = "REFII_MULTIPLAYER", HostOnly = true,
					Command = Engine_UiHostScreens.OpenCommand(Engine_UiMultiplayerPage.ScreenId),
				},

				Tab("Options", "GUI_SCREEN_PAUSE_OPTIONS", Engine_PauseMenu.TabOptions),

				// FABLE 3'S KICK, the online host's (the guest's own root adds "Kick" only when
				// game+0x11C == 1 and the henchman exists, `sub_8298DF98`). Offered by GameUi only
				// while an online partner stands. The row is the game's own text key for it
				// (`GUI_NET_PAUSED_KICK_CLIENT`, TU1 0x8230CD68, disc 0x823331A8). The child is the
				// confirmation, ours, after Fable 3's GUI_SCREEN_PAUSE_CONFIRM_KICK_HENCHMAN; what it
				// says of the kicked player is what the guest does: a kicked joiner's own hero is
				// saved on the way out, as on a leave or a drop (their kick handler runs the
				// lost-connection handler first, TU1 0x8233E848, which writes the hero package when
				// they hold a save, 0x8232DEC0; disc 0x823212B8, 0x82310898).
				new Engine_UiMenuEntry
				{
					Name = Engine_PauseMenu.KickRow, Tag = "GUI_NET_PAUSED_KICK_CLIENT", HostOnly = true, When = hero,
					Children =
					{
						new Engine_UiMenuEntry
						{
							Name = "KickConfirm", HostOnly = true,
							Tag = "REFII_F2_KICK_CONFIRM",
							Command = $"{Engine_PauseMenu.CoopCommandPrefix}kick",
						},
					},
				},

				// PLAYER TWO'S WAY OUT, the original's one row: `sub_8298EDE0` spawns "HenchmanQuit"
				// (`GUI_SCREEN_PAUSE_HENCHMAN_QUIT`, 0x8298F1E0 / disc 0x82843984) for a local and an
				// online player two alike, and no quit to the title or the desktop — on a local machine
				// either would end player one's game too. Its A runs the guest's own henchman arm
				// (`PauseHenchmanQuit`, `sub_826A6610`): box 27 with a save held, box 26 (their
				// experience and gold go to the hero) without, each with the guest's own numbers; the
				// yes is the guest's drop-out answer `sub_826A6E70`.
				new Engine_UiMenuEntry
				{
					Name = "HenchmanQuit", Tag = "GUI_SCREEN_PAUSE_HENCHMAN_QUIT", HostOnly = true, When = playerTwo,
					Command = Engine_PauseMenu.HenchmanQuitCommand,
				},
			},
		};

		// PLAYER TWO'S ROWS ARE OURS, AUTHORED, NOT THE GUEST'S. Both players' pause screens name
		// the same population script — a live run showed `[screen-menu] 'PauseMenu' is GODOT'S
		// ... (source 'capture:Root')` on the joiner, not `HenchmanPauseMenu` — so the variant
		// cannot be chosen by that name; the `player-two` fact is `Fable2Bridge.IsCoopJoiner`'s
		// reading in GameUi. Without it the joiner got the capture's four rows AND the whole
		// single-player tab set: nine-plus rows with a duplicate `Options`, every command running
		// against a row it did not belong to, so picking `Abilities` reached `Save`.
		//
		// The guest states four rows there (`HenchmanAbilities`, `HenchmanConfig`, `Options`,
		// `HenchmanQuit`). They are not the authority on what this menu contains — Godot owns the
		// list, the order and the layout; what we take from the guest is the AVAILABILITY mask and
		// the commit entry points, exactly as the solo menu does. A pre-generated HENCHMAN's gear is
		// fixed by the co-op screen's pickboxes; `Engine_PauseMenu.ApplyTabAvailability` ghosts
		// whichever tabs the mask (`RefiiHostPauseTabsAvailable`, the guest's own `sub_826B83D0`)
		// says are unavailable. IF A RUN SHOWS the mask reporting Weapons/Clothing AVAILABLE to a
		// henchman, they need dropping outright rather than ghosting; that is the one thing here
		// not yet confirmed against a session.

		// THE ITEM SCREEN'S NINE CATEGORY ROWS, from the guest's own spawn order in
		// `sub_829B1578` (0x829B15E4 .. 0x829B1886): KeyItems, Books, Food and Drink, Potions,
		// Gifts, Trophies, Augmentations, Dyes, Furniture. Every one carries the same icon
		// (`dword_8349D930`) and event type 301, and every one is parented to `Root`.
		//
		// FIXED, NOT DERIVED FROM WHAT THE HERO CARRIES: the guest spawns all nine and GHOSTS the
		// empty ones rather than dropping them — a real run showed `Food ghosted` and
		// `Trophies ghosted` sitting between populated rows. Our own automatic submenu ghosting
		// reproduces that for free once the children come from the source below.
		//
		// The children are the DATA AREAS, not the guest's spawn stream: each row names the
		// category integers it holds and `Engine_UiInventorySource` reads the hero's inventory
		// and `globals.gdb` for them. `KeyItems` takes eight integers because the guest folds
		// 9..14 and 26 onto it — which is why hairstyles and makeup are filed there.
		// THE ABILITIES SCREEN — `sub_829A45A0`'s four root rows, which it spawns with direct
		// `sub_826CE8D0` calls rather than through `sub_826CF098`. All four state the SAME icon
		// (`Art\GUI\WheelIcons\Abilities\icon_strength.tga`) and an empty `String2`; the
		// captions arrive later through `g_Menu:RefreshButton()` in `sub_829A5398`.
		//
		// `SpellAssigning` is NOT a category — it has no children and event type 23, and
		// activating it hands the screen to the spell-assignment widget at `screen+200` in place
		// (`sub_826C2480`). Its Babel tag reads 'Spell Selector', which is the row observed live.
		menus["AbilitiesMenu"] = new Engine_UiMenuTemplate
		{
			Name = "AbilitiesMenu",
			Entries =
			{
				AbilityCategory(Engine_UiAbilitySource.Strength,
					"GUI_SCREEN_ABILITIES_LEARN_STRENGTH"),
				AbilityCategory(Engine_UiAbilitySource.Skill,
					"GUI_SCREEN_ABILITIES_LEARN_SKILL"),
				AbilityCategory(Engine_UiAbilitySource.Will,
					"GUI_SCREEN_ABILITIES_LEARN_WILL"),
				new Engine_UiMenuEntry
				{
					Name = "SpellAssigning",
					Tag = "GUI_SCREEN_ABILITIES_SPELL_ASSIGNER",
					Icon = AbilityHeaderIcon,

					// 23 — the spell-assign event, which the screen answers itself.
					EventType = (int)Engine_UiMenuConstants.EventType.AssignSpellsAbilityItem,
				},
			},
		};

		// THE WORLD MAP'S FIVE CATEGORY ROWS — `sub_829A2F40`'s spawn order (0x829A30A0 ..
		// 0x829A3220), each `sub_826CF098(list, name, tag, icon, counts)`, with the names its rows are
		// filed under (TU1 `dword_8349D340..350`, disc `0x8349D2B4..2C4`: Regions, Quests, Jobs, Sales,
		// Family). Note the key is `Family` while the tag reads 'Families'.
		//
		// THEIR ROWS ARE THE GUEST'S POPULATION, spawned by the same function: regions
		// `sub_829A3A30` (type 41, id index + 999), quests `sub_829A3BA0` (42, the ordinal in
		// `lhWorldMap_BuildQuestList`), jobs `sub_829A3E98` (43, `BuildJobList`), sales
		// `sub_829A40C0` (44, `BuildSalesList`), family `sub_829A42C8` (45, `BuildFamilyList`). A row
		// commits as itself — `MenuEvent` with that (type, id) to the screen's own OnMenuEvent
		// `sub_826C6110` — and its page is its own fill's statement (Engine_UiWorldMapPage). The
		// trail marks `sub_829A3260` state each row's emphasis by its spawned name.
		menus["WorldMapMenu"] = new Engine_UiMenuTemplate
		{
			Name = "WorldMapMenu",
			Entries =
			{
				WorldMapCategory("Quests", "GUI_SCREEN_WORLD_MAP_QUESTS"),
				WorldMapCategory("Jobs", "GUI_SCREEN_WORLD_MAP_JOBS"),
				WorldMapCategory("Sales", "GUI_SCREEN_WORLD_MAP_SALES"),
				WorldMapCategory("Family", "GUI_SCREEN_WORLD_MAP_FAMILY"),
				WorldMapCategory("Regions", "GUI_SCREEN_WORLD_MAP_REGIONS"),

				// OUR OWN CATEGORY, not the guest's: the player's properties with Fable 3's condition
				// and repair (no shipped tag). Counted by us; no guest row or page stands behind it.
				new Engine_UiMenuEntry
				{
					Name = "Properties",
					Tag = "Properties",
					Icon = @"Art\GUI\WheelIcons\icon_logbook.tga",
					EventType = (int)Engine_UiMenuConstants.EventType.Submenu,
					Source = Engine_UiPropertySource.SourceName,
				},
			},
		};

		// THE THREE CONDITIONAL LISTS — real estate and the two furniture screens.
		//
		// Each names no population script and each spawns rows that depend on state we do not
		// read: `sub_82998300` walks a decision tree over the property record and emits only the
		// applicable actions, and `sub_8299D5B0` branches on whether the target has component 68
		// (upgrade) or 239 (replace). Authoring their rows would show actions the game has
		// disabled, so the rows come from the capture — but the MENU is a template record now,
		// so a row can be dropped, renamed or reordered from `templates.json` exactly as on
		// every other screen, and all of them go through one assembly path.
		// REAL ESTATE AND THE TWO FURNITURE LISTS STAY UNBOUND, and that is a retraction.
		//
		// I bound all three to templates sourced `capture:Root`, and it bled: the real-estate
		// screen came up showing `New Game`, `Continue`, `Downloadable Content`, `Language` and
		// `Subtitles` above its own two rows. The spawn capture is ONE GLOBAL LIST —
		// `Engine_UiSource.GetMenuRows` returns the last population with a generation and
		// `Engine_UiModel.MenuRow` carries no presentation — so `capture:Root` hands back
		// whatever was spawned under `Root` most recently, and the front end's rows were still
		// in it.
		//
		// That is harmless for a screen whose parent names are its own (the shop's `BuyItems`,
		// the weapons screen's `Melee`) and fatal for one asking for the bare root. These three
		// ask for the root, so they keep the per-presentation model path they had — which was
		// working — until the capture is keyed by presentation.
		//
		// The furniture rows carry event type 0 and `sub_826B65F0` handles only 140/141, so an
		// identity push does nothing. All three commit by the screen's `RowAction`: upgrade
		// furniture the level (to `sub_826B6A18`'s transaction), decoration the row's ticket (to
		// the guest's own mount `sub_826B7550`), real estate the row's event, 110..120 (to the
		// handler's own OnMenuEvent). A real-estate row is KEYED by that event too
		// (`Engine_UiMenuConstants.RealEstateRowName`): its stated name is the localised label cut
		// to bytes.

		// THE SHOP'S SIX GROUPS — `sub_82994218`'s own spawn order, each a counted header
		// through `sub_826CF098` and each emitted only when its own count is non-zero (the guest
		// then greys it with `sub_826D01B8`). The internal names are the globals at
		// `0x8349D048..D05C`, and they are what the rows are filed under as `String4`.
		//
		// The rows stay on the CAPTURE, for the same reason the weapons rows do and one more: a
		// shop row's identity is a per-population ticket from `sub_82880AC8`, and the buy and
		// sell handlers are reached through the shop screen's own dispatcher rather than through
		// anything that takes a record. Authoring the six groups makes the SHAPE editable —
		// rename them, reorder them, drop the sell half — without touching the rows.
		//
		// All six tags checked against the shipped book before use.
		menus["ShopMenu"] = new Engine_UiMenuTemplate
		{
			Name = "ShopMenu",
			Entries =
			{
				ShopCategory("BuyItems", "GUI_SCREEN_SHOP_BUY_ITEMS"),
				ShopCategory("SellItems", "GUI_SCREEN_SHOP_SELL_ITEMS"),
				ShopCategory("BuyWeapons", "GUI_SCREEN_SHOP_BUY_WEAPONS"),
				ShopCategory("SellWeapons", "GUI_SCREEN_SHOP_SELL_WEAPONS"),
				ShopCategory("BuyClothing", "GUI_SCREEN_SHOP_BUY_CLOTHING"),
				ShopCategory("SellClothing", "GUI_SCREEN_SHOP_SELL_CLOTHING"),
			},
		};

		// THE WEAPONS LIST — screen 15 in mode 3, `sub_829B1890`'s two headers over
		// `sub_829B1968`'s rows.
		//
		// THE ROWS ARE OURS, AND SO IS THE EQUIP. `screen+396` turned out to be a 16-byte item
		// HANDLE, not a scalar — the screen's Update refills it every frame from
		// `g_Menu:GetHighlightedMenuItemData()`, so the guest's own answer to event 3 equips
		// whatever ITS cursor is on and an identity push is right only while the two cursors
		// agree. `sub_826C9AB8` takes that handle by address and reads nothing else, so naming
		// the record and building the handle from the hero's component 13 removes the cursor from
		// the path altogether.
		//
		// Both tags checked against the shipped book before use.
		menus["WeaponsMenu"] = new Engine_UiMenuTemplate
		{
			Name = "WeaponsMenu",
			Entries =
			{
				// `sub_829B1890` spawns both through `sub_826CF098`, so both carry a count.
				WeaponCategory("Melee", CategoryTag + "MELEE_WEAPONS"),
				WeaponCategory("Ranged", CategoryTag + "RANGED_WEAPONS"),
			},
		};

		// THE GIFT SCREEN'S ROWS ARE THE GUEST'S. `lhGuiGiftGoldScreen_PopulateMenu` (0x82430468 / disc
		// 0x823A45F8) spawns a category header only for a group it lists (`sub_826CF098` 0x82430778, the name
		// and caption from `sub_82430B58`), each item that passes `sub_8242FB28`'s gift rules and the
		// `VisibleInGUI` veto under it (`sub_826CE230` 0x82430960; on TU1 the ring rule `sub_824303E8`
		// 0x82430888, which the disc build has not), and the Gold row on the root while the purse holds any
		// (`sub_826CE848(…, "GUI_SCREEN_GIFT_GOLD", icon_gold_coin, "Root", 7, 7, …)` 0x82430AD0). The source
		// takes them in that order, grouped under the guest's own headers. An item row's A is the gift lever;
		// the Gold row commits as itself (its type 7 opens the amount picker, `sub_826C45C8`).
		menus["GiftGivingMenu"] = new Engine_UiMenuTemplate
		{
			Name = "GiftGivingMenu",
			Source = $"{Engine_UiItemRows.SourceName}:gift:",
		};

		foreach (string itemScript in new[] { "ItemsMenu" })
		{
			menus[itemScript] = new Engine_UiMenuTemplate
			{
				Name = itemScript,
				Entries =
				{
					ItemCategory("KeyItems", CategoryTag + "TOOLS", "use"),
					ItemCategory("Books", CategoryTag + "BOOKS", "use"),
					ItemCategory("Food and Drink", CategoryTag + "FOOD", "use"),
					ItemCategory("Potions", CategoryTag + "POTIONS", "use"),
					ItemCategory("Gifts", CategoryTag + "GIFTS", "use"),
					ItemCategory("Trophies", CategoryTag + "TROPHIES", "use"),

					// An augment, a remover and a dye commit as themselves: their own events 83 / 310 / 80
					// (`sub_826CEEF0`) open the guest's target lists.
					ItemCategory("Augmentations", CategoryTag + "AUGMENTATIONS", string.Empty),
					ItemCategory("Dyes", CategoryTag + "DYES", string.Empty),
					ItemCategory("Furniture", CategoryTag + "FURNITURE", "use"),
				},
			};
		}

		// THE CLOTHING SCREEN'S EIGHT ROWS — `sub_829B3340`'s seven plus `Outfits` above them,
		// which is the read a real run gives: Outfits, Headgear (6), Coats (5), Upper Body (3),
		// Hands (4), Lower Body (5), Feet (6), Masks (2). The counts array is indexed the same
		// way, `16 * Category`, for 17..23.
		//
		// `Outfits` is category 25 (Suits) — the one the ITEM list excludes outright and this
		// one is entirely about, which is why the exclusion is per-request rather than global.
		// `EquipmentMenu` is the name the screen's widget actually states — confirmed in a run,
		// which reported `population script 'EquipmentMenu'` and the eight rows in exactly this
		// order. `ClothingMenu` is registered too but is not what screen 16 asks for.
		menus["EquipmentMenu"] = new Engine_UiMenuTemplate
		{
			Name = "EquipmentMenu",
			Entries =
			{
				// `sub_829B2D58` spawns this header through `sub_826CF020`, which takes no count
				// — the only one of the eight that does.
				Uncounted(ItemCategory("Suits", CategoryTag + "SUITS", "wear")),
				ItemCategory("Hats", CategoryTag + "HATS", "wear"),
				ItemCategory("Coats", CategoryTag + "COATS", "wear"),
				ItemCategory("Shirts", CategoryTag + "SHIRTS", "wear"),
				ItemCategory("Gloves", CategoryTag + "GLOVES", "wear"),
				ItemCategory("Pants", CategoryTag + "PANTS", "wear"),
				ItemCategory("Shoes", CategoryTag + "SHOES", "wear"),

				// Internal name `Accessories`, Babel tag `..._MASKS` — the two disagree and the
				// tag is the one that draws. The dog's items are filed here too (`sub_826CEB60`).
				ItemCategory("Accessories", CategoryTag + "MASKS", "wear"),
			},
		};

		// THE ACHIEVEMENTS SCREEN'S LIST — one authored summary row, then one row per
		// achievement straight from the title's own XDBF (`Engine_Achievements`).
		//
		// The source hangs on the ROOT, not on the summary row: these are headings on a list
		// screen, and nesting them under the summary would make the whole screen a fold that has
		// to be opened before anything shows.
		//
		// The summary row is authored and host-only — it carries no achievement of its own, it
		// is where the tally is read — and it starts highlighted so the screen opens on the
		// count rather than on whichever achievement happens to be first.
		// THE MULTIPLAYER BOOK'S LIST. One sourced entry at the root: the source tags every row
		// with its group and `Expanded` raises a header per group, so Friends, Friend Requests,
		// Invitations and Games are folds without four templates to keep in step.
		// THE MULTIPLAYER BOOK'S LEFT LIST. Static: three actions that act where they stand,
		// then the four subjects, each of which OPENS A RIGHT PAGE rather than folding. The rows
		// of a subject are a list with a cursor of its own — see Engine_UiMultiplayerScreen.
		var multiplayer = new Engine_UiMenuTemplate
		{
			Name = Engine_UiMultiplayerPage.MenuName,
			Entries =
			{
				new Engine_UiMenuEntry
				{
					Name = "Hosting", Tag = "REFII_F2_MP_MY_GAME", HostOnly = true,
					Source = Engine_UiNetworkSource.HostingSource,
				},
				new Engine_UiMenuEntry
				{
					Name = "AddFriend", Tag = "REFII_F2_MP_ADD_FRIEND", HostOnly = true,
					Command = $"{Engine_UiNetworkSource.CommandPrefix}paste",
				},
				new Engine_UiMenuEntry
				{
					Name = "MyCode", Tag = "REFII_F2_MP_MY_CODE", HostOnly = true,
					Command = $"{Engine_UiNetworkSource.CommandPrefix}code",
				},
			},
		};

		// EACH SUBJECT IS A FOLD, the same shape as `My game`: its rows are its children, so an
		// empty one still opens onto a row that says so. The right page shows whatever the
		// cursor is on — the list is the subject, the page is the detail.
		foreach ((string source, string title, string tag) in Engine_UiNetworkSource.Pages)
		{
			multiplayer.Entries.Add(new Engine_UiMenuEntry
			{
				Name = title, Tag = tag, HostOnly = true, Source = source,
			});
		}

		menus[Engine_UiMultiplayerPage.MenuName] = multiplayer;

		// THE ACTIVE MODS BOOK'S LIST: the enabled mods at the root, in load order — the same
		// source the title's fold lists, so the book opens on the row that was pressed.
		menus[Engine_UiMultiplayerPage.ModsMenuName] = new Engine_UiMenuTemplate
		{
			Name = Engine_UiMultiplayerPage.ModsMenuName,
			Title = "REFII_ACTIVE_MODS",
			Source = Engine_UiNetworkSource.ModsSource,
		};

		// THE HOST PAGE BOOK'S LIST: a host question's two answers as rows (Engine_UiHostBoxPage).
		menus[Engine_UiHostBoxPage.MenuName] = new Engine_UiMenuTemplate
		{
			Name = Engine_UiHostBoxPage.MenuName,
			Entries =
			{
				new Engine_UiMenuEntry
				{
					Name = "Continue", Tag = "REFII_CONTINUE", HostOnly = true,
					Command = Engine_UiHostBoxPage.AcceptCommand,
				},
				new Engine_UiMenuEntry
				{
					Name = "Back", Tag = "REFII_BACK", HostOnly = true,
					Command = Engine_UiHostBoxPage.CancelCommand,
				},
			},
		};

		menus["achievements-root"] = new Engine_UiMenuTemplate
		{
			Name = "achievements-root",
			Highlight = Engine_UiAchievementPage.FolderRowName,
			Source = Engine_UiAchievementPage.SourceName,
			Entries =
			{
				new Engine_UiMenuEntry
				{
					Name = Engine_UiAchievementPage.FolderRowName,
					Tag = "REFII_ACHIEVEMENTS",
					EventType = Engine_UiAchievementPage.EventType,
					HostOnly = true,
				},
			},
		};

		// The front-end root — `sub_826E2220`'s spawn order. "New Game" OPENS the gender card
		// rather than dispatching (slide 1 on `choosecard`, substate 2; activating by name would
		// consume the boy/girl answer unasked). Subtitles expands in place. Availability (Load
		// Game needs a save, Language two installed) stays the guest's spawn condition.
		menus["frontend-root"] = new Engine_UiMenuTemplate
		{
			Name = "frontend-root",
			Entries =
			{
				new Engine_UiMenuEntry
				{
					Name = "New Game", Tag = "GUI_SCREEN_FRONT_END_NEW_GAME",
					Opens = Fable2FrontEndRoot.HeroCardScreen,
				},
				new Engine_UiMenuEntry { Name = "Load Game", Tag = "GUI_SCREEN_FRONT_END_LOAD_GAME" },
				// TWO FOLDS, not one. Mods are content; friends, invitations and games are a
				// different subject and were only ever together because there was one row to
				// hang them on. The front-end list already nests (`Subtitles` does) and a
				// sourced entry refreshes its children in place, so neither needs a screen, a
				// second cursor or an input route of its own. Every child is HostOnly — the
				// guest has no screen for any of this.
				new Engine_UiMenuEntry
				{
					Name = "Downloadable Content", Tag = "GUI_DOWNLOADABLE_CONTENT",
					HostOnly = true,
					Children =
					{
						// A ROW, NOT A FOLD: it opens the Active Mods book, the mods listed on its
						// left page and the highlighted one's description on its right.
						new Engine_UiMenuEntry
						{
							Name = "Mods", Tag = "REFII_ACTIVE_MODS", HostOnly = true,
							Command = $"{Engine_UiNetworkSource.CommandPrefix}mods",
						},
					},
				},
				// ONE ROW, NOT A FOLD. It opens the multiplayer BOOK — the same screen the pause
				// menu's tab opens — because the subjects are lists with cursors of their own
				// and a fold has nowhere to put them. See Engine_UiMultiplayerScreen.
				new Engine_UiMenuEntry
				{
					Name = "Multiplayer", Tag = "REFII_MULTIPLAYER", HostOnly = true,
					Command = $"{Engine_UiNetworkSource.CommandPrefix}book",
				},
				new Engine_UiMenuEntry { Name = "Language", Tag = "GUI_SCREEN_FRONT_END_LANGUAGE" },
				new Engine_UiMenuEntry
				{
					Name = "Subtitles", Tag = "GUI_SCREEN_OPTIONS_SUBTITLES",
					Children =
					{
						new Engine_UiMenuEntry { Name = "SubtitlesOn", Tag = "GUI_CONTROLS_ON" },
						new Engine_UiMenuEntry { Name = "SubtitlesOff", Tag = "GUI_CONTROLS_OFF" },
					},
				},

				// THE WAY OUT. A console front end had none, so there is no guest dispatch to
				// reach and no shipped tag to localise against — host-only, our own key, for
				// the same reasons as `Achievements`. Last because this list IS the order.
				new Engine_UiMenuEntry
				{
					Name = "QuitToDesktop", Tag = "REFII_QUIT_TO_DESKTOP", HostOnly = true,
					Command = Engine_FrontEndMenu.QuitToDesktopCommand,
				},
			},
		};

		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 0, Name = "pause", Presentation = "pausemainmenu",

			// The cover is inside the pool here — `LeftFrame` at +10, `Buttons` at 0 — so the
			// rows belong exactly where `Buttons` is and any lift clears the cover.
			ListLift = 0f,
			Menu = "pause-root",
			ChromeUnits = { "Foreground" },

			// Godot owns the row list and its cursor; the rest of the assembly is guest art,
			// riding in on the guest's stated position for `ExpandableMenu`.
			OwnedElsewhere = { "ExpandableMenu.Buttons", "MenuHighlight" },
			PageRootUnit = "Pages",

			// The minimap legend scrolls: `sub_826A19E0` registers it under `Mask` with the
			// page's bar (0x826A23BC / 0x826A23DC), when the legend is the page shown.
			ScrollContent = "Foreground.Pages.Contents.MiniMap.Legend",
			ScrollMask = "Mask",
			ScrollBar = "Foreground.Pages.Contents.Scrollbar",
			BackdropUnit = "BackgroundTint",

			// `Legend.Fixed` hangs under the objective's laid-out text, only while there is one
			// (0x826a2050..0x826a20b0): y = CurrentObjective.y − its height.
			FlowMeasured = "Foreground.Pages.Contents.MiniMap.Legend.CurrentObjective",
			FlowBase = "Foreground.Pages.Contents.MiniMap.Legend.Fixed",
			FlowMoved = { "Foreground.Pages.Contents.MiniMap.Legend.Fixed" },
			FlowWhenDrawn = true,

			// The readouts, the quest lines, the legend and the map image are the guest's own
			// statements; the tokens and the quest markers' projection are
			// `Engine_UiPauseMinimap`.
		});

		// THE ITEM-BOOK FAMILY, which had no template records at all — items, clothing, gift
		// giving, furniture upgrade and decoration. They were five bare ids in a `uint[]` inside
		// the dispatcher, so they were the one part of the screen set that no template described
		// and the editor could never see. Three `.bgf` files, one skeleton, one provider; the
		// shop (22) is the same family and is recorded with its own art above.
		// ONE REGISTRATION PER ID. There were two passes over 15/16/25/26/27 and `AddScreen`
		// overwrites, so everything the first pass set was discarded before anything read it —
		// twice over. It cost the list binding once, and it cost these five screens their PAGE
		// PROVIDER for far longer: `Provider` was set only on the pass that lost, so
		// `Engine_UiDetailPages.Owns` was false for the whole item-book family and the right
		// page was left entirely to the guest, which fills it on a highlight-change edge keyed
		// off ITS Lua cursor — a cursor that stops moving the moment Godot owns the list. That
		// is the "the page appears and then hides, and is never consistent" report: whatever the
		// guest last wrote stayed until something else overwrote it.
		//
		// The surviving pass is below and now carries the provider.

		// The ABILITIES panel. `AbilityCost` carries the parameterised
		// `GUI_SCREEN_ABLITY_EXPERIENCE_COST` — the typo is the shipped binary's, not ours.
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 14, Name = "abilities", OwnsList = true,
			Provider = Fable2PageProviders.Abilities,
			HasSpellBlade = true,
			Presentation = "abilitiesscreen",
			ChromeUnits = { "Foreground" },

			// Godot owns the row list and its cursor; the chrome rides the guest's stated position
			// for `ExpandableMenu`. The assignment blade follows the guest's C++ widget at
			// screen14+200 as its methods state it (`@Spell.Widget`) and is drawn on the book's own clone.
			OwnedElsewhere = { "ExpandableMenu.Buttons", "MenuHighlight" },
			PageRootUnit = "Pages", BackdropUnit = "background_red",

			// Page2's description scrolls: the Layer `Mask` is a `Scroller`, so the automatic
			// registrar (`sub_828C2F38` via `sub_829A7390`) takes it, bar at 0x829A73B4.
			ScrollContent = "Foreground.Book.Pages.Page2.Contents.Text",
			ScrollMask = "Mask",
			ScrollBar = "Foreground.Book.Pages.Page2.Contents.Scrollbar",

			// Page4, not Page3 — the abilities book skips one. Its stack parks at x = 400.
			Pages =
			{
				"Foreground.Book.Pages.Page1", "Foreground.Book.Pages.Page2",
				"Foreground.Book.Pages.Page4",
			},
			PageStackAt = Vector3.Zero,
		});

		foreach (uint id in new uint[] { 15, 16, 25, 26, 27 })
		{
			AddScreen(screens, new Engine_UiScreenTemplate
			{
				ScreenId = id, OwnsList = true,

				// Page2's item description scrolls — automatic `Mask` registration on items,
				// clothing (`sub_829B7328`), gift (`sub_829A9E08`) and decoration
				// (`sub_8299CEA8`). Upgrade furniture (26) has no mask: the guest registers
				// nothing there.
				// The item books act by stated command, and so do the two furniture screens: an
				// upgrade row is a level and the verb runs `sub_826B6A18`'s transaction on it; a
				// decoration row is the guest's ticket for a trophy and the verb runs the guest's
				// own mount `sub_826B7550` on it.
				RowAction = id switch
				{
					Engine_UiFurniturePage.ScreenId => Engine_UiFurniturePage.UpgradeAction,
					27 => Fable2UiVerbs.DecorationMountAction,
					_ => string.Empty,
				},

				// DECORATION'S Y REMOVES what the slot holds: the input handler's code 39 arm
				// (`sub_826B7770` case 13, 0x826B78C4..0x826B7908; disc `sub_8259CA10`
				// 0x8259CB64..0x8259CBA8) takes no highlight — `sub_8243ED88(*(screen+0x58))`
				// (disc `sub_825A91F8`), then ExitScreen. The list's Y is Godot's, posted as the
				// screen's own lever.
				FlipAction = id == 27 ? Fable2UiVerbs.DecorationTakeDownAction : string.Empty,
				ScrollContent = id == 26 ? string.Empty : "Foreground.Book.Pages.Page2.Contents.ItemDescription",
				ScrollMask = id == 26 ? string.Empty : "Mask",
				ScrollBar = id == 26 ? string.Empty : "Foreground.Book.Pages.Page2.Contents.Scrollbar",

				// Two pages, authored ON the leaf (x = 0) in every book of this family —
				// `giftgivingscreen`, `equipmentscreen` and `upgradefurniture` — so no
				// PageStackAt. (`upgradefurniture` also authors `Page1_Background`, which is
				// Page1's backdrop, not a selectable page.)
				Pages =
				{
					"Foreground.Book.Pages.Page1", "Foreground.Book.Pages.Page2",
				},
				Name = id switch
				{
					15 => "items", 16 => "clothing", 25 => "gift",
					26 => "upgradefurniture", _ => "decoration",
				},

				// Three `.bgf` files, one skeleton, ONE PROVIDER — the right-hand page of all
				// five, and of the shop, which records the same provider with its own art.
				Provider = Fable2PageProviders.ItemBook,

				// WHICH LIST EACH OF THESE IS, because not one of them names a population
				// script — `sub_82430468` (gift) and `sub_829B0A58` (inventory) build their rows
				// in native C++ and issue no `RepopulateMenuFromPopulationScript` at all, so
				// both the attribute and the captured-statement fallback come up empty and the
				// list dropped onto the raw model.
				//
				// A run settled which id is which: screen 15 came up holding exactly the nine
				// item categories — KeyItems, Books, Food, Potions (7), Gifts, Trophies,
				// Augments, Dyes, Furniture — with X and Y reading 'Use' and 'Remove'. The
				// 'Gift' title on the same presentation is the shared file's authored default,
				// not the screen's claim: 15, 25 and 27 are all `GiftGivingScreen.bgf`.
				// 26 AND 27 ARE DELIBERATELY UNBOUND — see the conditional-list note below.
				MenuScript = id switch
				{
					15 => "ItemsMenu",
					25 => "GiftGivingMenu",
					_ => string.Empty,
				},

				// SCREEN 15 HAS TWO LISTS, NOT THREE. Its mode word at `screen+372` only ever
				// holds 1 (Items) or 3 (Weapons): the pause dispatcher `sub_826B89E0` writes
				// `*(screen15 + 372) = 1` for tab 4 and `= 3` for tab 5, and for tab 6 it takes
				// a DIFFERENT screen — `*(screen16 + 220) = 4`. Clothing is screen 16.
				//
				// The `[4] = ClothingMenu` entry here could therefore never fire. (Mode 2 is
				// also real but transient: screen 16's back handler writes it when returning
				// from dye, and screen 15's activate coerces it straight back to 1.)
				MenuScriptByMode = id == 15
					? new Dictionary<int, string> { [1] = "ItemsMenu", [3] = "WeaponsMenu" }
					: new Dictionary<int, string>(),
				Presentation = id switch
				{
					16 => "equipmentscreen", 26 => "upgradefurniture", _ => "giftgivingscreen",
				},

				// 15, 25 and 27 are `GiftGivingScreen.bgf`, 16 `EquipmentScreen.bgf`, 26
				// `UpgradeFurniture.bgf` — the same skeleton, each cloned from its own file.
				ChromeUnits = { "Foreground" },

				// Godot owns the row list and its cursor; the rest of the assembly is guest art,
				// riding in on the guest's stated position for `ExpandableMenu`.
				OwnedElsewhere = { "ExpandableMenu.Buttons", "MenuHighlight" },
				PageRootUnit = "Pages",

				// THE PAGE STACK IS PINNED ON THE LEAF. The Book's own fold slides move `Pages`
				// (`ShowPages` x = 405, `HidePages` x = 0) on the guest's highlight edge, which
				// no longer fires — so which of the two `Pages` sat at depended on where the
				// guest's mirror froze, and at 405 every page's content lay past the right edge
				// (the potion pages drew nothing). Godot owns the page: it stays where the
				// content fits.
				PageStackAt = Vector3.Zero,

				// The backdrop is a LEAF name, never "Background" — that name has 27 bearers,
				// every earlier one a menu row's icon plate. UpgradeFurniture is grey where the
				// gift and equipment books are green, so this is per-id.
				BackdropUnit = id == 26 ? "background_grey" : "background_green",
				// Same skeleton, different parent for the proxy: equipmentscreen.bgf parks it
				// under TypeSpecific, the gift and furniture books under ModelHider.
				PreviewAnchor = id == 16
					? "Foreground.Book.Pages.Page1.Contents.TypeSpecific.model_proxy.model"
					: "Foreground.Book.Pages.Page1.Contents.ModelHider.model_proxy.model",
			});
		}

		// The logbook names its pages by SUBJECT, not `Page1`/`Page2` —
		// `Foreground.Book.Pages.<Subject>Page` — so a `PageN` path resolves to nothing here.
		// Eight pages, 280 text elements between them, hence swept rather than listed. Subject
		// order is the guest's EventType mapping: 60/64 morality, 61 expressions, 62 completed
		// quests, 65 renown, 66 attractiveness, 67 stats, 68 dog, 69 help.
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 17, Name = "logbook", OwnsList = true,

			// The logbook's pages are named by SUBJECT, not numbered — its provider switches
			// them — but its stack parks at x = 400 like the abilities book's, so it is placed.
			PageStackAt = Vector3.Zero,
			Provider = Fable2PageProviders.Logbook,

			// THE STATS PAGE IS ONE 3300-UNIT COLUMN on a single `FullPage` slide (eight
			// categories, Combat at y 190 down to Time Played at y −3100) behind the page's
			// Layer-level `Mask` (500×440 at (208, −6)); the guest's `sub_829BAEB8` scrolls it
			// with the right stick and its `Scrollbar` is a stick-prompt row with Up/Down arrows
			// and the stick glyph — the same region contract the region page ships on screen 23.
			// The Renown page scrolls the same way (`sub_828C3250(…, "Foreground.Mask",
			// "…RenownPage")`): its last two expression rows sit below the mask's band.
			//
			// Expressions, Completed Quests and Help register too (`sub_829BCD58`,
			// `sub_829BD418`, `sub_829BDA90`); their rows author no MaskName, the guest clipping
			// them at registration — see Engine_UiOwnedBook.AssignRegionMasks.
			ScrollContent =
				"Foreground.Book.Pages.StatsPage.Contents.LogBook.Stats|" +
				"Foreground.Book.Pages.RenownPage.Contents.LogBook.Renown|" +
				"Foreground.Book.Pages.ExpressionsPage.Contents|" +
				"Foreground.Book.Pages.CompletedQuestsPage.Contents.Quests|" +
				"Foreground.Book.Pages.HelpPage.Contents.LogBook",
			ScrollMask = "Mask|Mask|Mask|Mask|Mask",
			ScrollBar =
				"Foreground.Book.Pages.StatsPage.Contents.LogBook.Scrollbar|" +
				"Foreground.Book.Pages.RenownPage.Contents.LogBook.Scrollbar|" +
				"Foreground.Book.Pages.ExpressionsPage.Scrollbar|" +
				"Foreground.Book.Pages.CompletedQuestsPage.Contents.Scrollbar|" +
				"Foreground.Book.Pages.HelpPage.Contents.Scrollbar",

			// The Expressions page hangs its effects block under the measured description
			// (`sub_829BD340`: `Effects.y = Description.y − wrapped height`). With the block as
			// its own base the reflow's delta lands it exactly there.
			FlowMeasured = "Foreground.Book.Pages.ExpressionsPage.Contents.Description",
			FlowBase = "Foreground.Book.Pages.ExpressionsPage.Contents.Effects",
			FlowMoved = { "Foreground.Book.Pages.ExpressionsPage.Contents.Effects" },

			// The expression's icon is the record's `IconGraphic`, drawn by the provider
			// (Engine_UiLogbookPage.Textures) from the row Godot states, as the guest's fill
			// `sub_829BCDF0` sets it (0x829BCEE8; disc `sub_828D61D0` 0x828D62C8).

			// Lifted verbatim from `Engine_UiLogbookPage.StatRows`.
			StatFields =
			{
		// combat
		(2,"combat.DecapitationValue"),(3,"combat.GroinshotsValue"),(4,"combat.EnemiesDisarmedValue"),
		(5,"combat.ReversalsValue"),(6,"combat.DamageDealtValue"),(7,"combat.TotalManaSpentValue"),
		(8,"combat.DamageTakenValue"),(9,"combat.DeathsValue"),(11,"combat.ResurrectionPhialsValue"),
		(13,"combat.HitsTakenValue"),(14,"combat.AttacksBlockedValue"),(15,"combat.FlourishesDoneValue"),
		(16,"combat.CoopStrikesValue"),
		// kills
		(109,"kills.TotalValue"),(110,"kills.BansheesKilledValue"),(111,"kills.BalverinesKilledValue"),
		(112,"kills.BanditsKilledValue"),(113,"kills.BeetlesKilledValue"),(114,"kills.TrollsKilledValue"),
		(115,"kills.ShardsKilledValue"),(116,"kills.ShadowsKilledValue"),
		(117,"kills.HollowmenKilledValue"),(118,"kills.GuardsKilledValue"),
		(119,"kills.LucienGuardsKilledValue"),(120,"kills.LucienSoldiersKilledValue"),
		(121,"kills.HobbesKilledValue"),(122,"kills.VillagersKilledValue"),
		(123,"kills.HighwayMenKilledValue"),
		// money
		(17,"money.MoneyPropertiesOwnedValue"),(18,"money.MoneyEarnedValue"),(19,"money.MoneySpentValue"),
		(20,"money.MoneyRightNowValue"),(22,"money.MoneyMadeMageDiceValue"),
		(23,"money.MoneyMadeBartendingValue"),(24,"money.MoneyMadeWoodchoppingValue"),
		(25,"money.MoneyMadeBlacksmithingValue"),(27,"money.MoneyMadeKeystoneValue"),
		(28,"money.MoneyMadeWizardsTowerValue"),(29,"money.MoneyMadeRealEstateValue"),
		(30,"money.MoneyMadeAssasinationsValue"),(31,"money.MoneyMadeBountyHuntsValue"),
		(32,"money.MoneyMadeSlaveryValue"),
		// adventure
		(33,"adventure.TotalRenownValue"),(106,"adventure.MostTrophyRenownValue"),
		(35,"adventure.GoldDonatedToTempleValue"),(36,"adventure.PeopleSacrificedToEvilValue"),
		(37,"adventure.AssassinationsCompletedValue"),(38,"adventure.BountiesCollectedValue"),
		(39,"adventure.SlavesRescuedValue"),(40,"adventure.PeoplePutIntoSlaveryValue"),
		(41,"adventure.MostGoodPointsValue"),(42,"adventure.MostEvilPointsValue"),
		(43,"adventure.MostNobilityPointsValue"),(44,"adventure.MostCorruptionPointsValue"),
		(45,"adventure.TotalExpEarnedValue"),(46,"adventure.TotalStrengthExpEarnedValue"),
		(47,"adventure.TotalSkillExpEarnedValue"),(48,"adventure.TotalWillExpEarnedValue"),

		// `Genearal` is the SHIPPED SPELLING. The element in `logbookscreen.bgf` carries the
		// typo, so correcting it here would resolve to nothing at all.
		(49,"adventure.TotalGenearalExpEarnedValue"),

		// games — note 83 is registered LAST despite its lower id
		(50,"games.TopScoreWestcliffShootingValue"),(96,"games.BiggestWinMageDiceValue"),
		(97,"games.BiggestWinKeystoneValue"),(98,"games.BiggestWinWizardsTowerValue"),
		(83,"games.BestFullCrucibleTimeValue"),
		// relationships
		(54,"relationships.NumPeopleFallenInLoveWithYouValue"),(55,"relationships.TimesMarriedValue"),
		(56,"relationships.TimesDivorcedValue"),(57,"relationships.TimesWidowedValue"),
		(58,"relationships.NumChildrenValue"),(59,"relationships.MostLoveEarnedValue"),
		(60,"relationships.MostHumourEarnedValue"),(61,"relationships.MostFearEarnedValue"),
		(62,"relationships.MostHateEarnedValue"),(63,"relationships.TimesMaritalSexValue"),
		(64,"relationships.TimesExtraMaritalSexValue"),(65,"relationships.TimesPaidForSexValue"),
		(66,"relationships.NumNpcRewardsCollectedValue"),
		(67,"relationships.MostCoopExpressionBonusesValue"),
		// crime
		(69,"crime.NumCrimesCommitedValue"),(70,"crime.CrimeFinesLeviedValue"),
		(71,"crime.CrimeFinesPaidValue"),(72,"crime.CrimeFinesPaidCommunityServiceValue"),
		// other
		(74,"other.TotalFoodDrinkConsumedValue"),(75,"other.TotalAlcoholConsumedValue"),
		(76,"other.TotalPotionsConsumedValue"),(77,"other.AmountOfTimeDrunkValue"),
		(78,"other.TotalVomitsValue"),(79,"other.TotalDistanceTraveledValue"),
		(80,"other.NumChickensKickedValue"),(81,"other.FurthestChickenKickValue"),
		(0,"other.ExpressionsValue"),(84,"other.STDsValue"),(1,"other.TimePlayedValue"),
			},
			Presentation = "logbookscreen",

			// `Money` has two bearers here; the money bar is the second — bearer 0 is a stats
			// row, `StatsPage.Contents.LogBook.Stats.money.Money`.
			ChromeUnits = { "Foreground" },

			// Godot owns the row list and its cursor; the rest of the assembly is guest art,
			// riding in on the guest's stated position for `ExpandableMenu`.
			OwnedElsewhere = { "ExpandableMenu.Buttons", "MenuHighlight" },
			PageRootUnit = "Pages", BackdropUnit = "background_yellow",
		});

		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 18, Name = "options", OwnsList = true,
			// Four pages; the stack is authored on the leaf already.
			Pages = { "Foreground.Book.Pages.Page1", "Foreground.Book.Pages.Page2", "Foreground.Book.Pages.Page3", "Foreground.Book.Pages.Page4" },
			Provider = Fable2PageProviders.Options,
			FadesBottomEnd = false,
			Presentation = "optionsscreen",
			ChromeUnits = { "Foreground" },

			// Godot owns the row list and its cursor; the rest of the assembly is guest art,
			// riding in on the guest's stated position for `ExpandableMenu`.
			OwnedElsewhere = { "ExpandableMenu.Buttons", "MenuHighlight" },
			PageRootUnit = "Pages", BackdropUnit = "background_purple",
			// The stack is authored at x 405 — off the leaf — like the logbook's.
			PageStackAt = Vector3.Zero,
		});

		// The CRIME panel — three pages of one multi-line text field each: fine plus charge
		// sheet, charge sheet alone, community-service text.
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 21, Name = "crime", OwnsList = true,

			// A row is its event (50 pay, 51 resist, 52 community service): the verb posts it as
			// the screen's own lever, `Crime:<p>:pay|resist|service`.
			RowAction = Fable2UiVerbs.CrimeChoiceAction,
			// Three pages, one per row; the stack is authored on the leaf.
			Pages = { "Foreground.Book.Pages.Page1", "Foreground.Book.Pages.Page2", "Foreground.Book.Pages.Page3" },
			Provider = Fable2PageProviders.Crime,
			Presentation = "crimescreen",
			ChromeUnits = { "Foreground" },

			// Godot owns the row list and its cursor; the rest of the assembly is guest art,
			// riding in on the guest's stated position for `ExpandableMenu`.
			OwnedElsewhere = { "ExpandableMenu.Buttons", "MenuHighlight" },
			PageRootUnit = "Pages", BackdropUnit = "background_grey",

			// Each crime page scrolls its text — `sub_82991338`, `sub_82991500`, `sub_829915A0`
			// register `PageN.Contents.Text` under `Mask` after writing it.
			ScrollContent = "Foreground.Book.Pages.Page1.Contents.Text|Foreground.Book.Pages.Page2.Contents.Text|Foreground.Book.Pages.Page3.Contents.Text",
			ScrollMask = "Mask|Mask|Mask",
			ScrollBar =
				"Foreground.Book.Pages.Page1.Contents.Scrollbar|Foreground.Book.Pages.Page2.Contents.Scrollbar|Foreground.Book.Pages.Page3.Contents.Scrollbar",
		});

		// The SHOP's panel: `PriceValue` is the full chain of base price, shop multiplier,
		// wealth and opinion (`sub_82992F90` via `sub_828727B8`/`sub_82872910`), while
		// `ValueValue` is the unmodified `ShopItemComponent.BasePrice`.
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 22, Name = "shop", OwnsList = true,
			// Three pages, and the stack parks at x = 400 — off the canvas — so it is placed.
			Pages = { "Foreground.Book.Pages.Page1", "Foreground.Book.Pages.Page2", "Foreground.Book.Pages.Page3" },
			PageStackAt = Vector3.Zero,

			// `ShopMenu` is not a string in the binary — `sub_82994218` builds the list natively
			// and names no population script, exactly as the gift and inventory screens do.
			MenuScript = "ShopMenu",
			Provider = Fable2PageProviders.ItemBook,
			Presentation = "shopscreen",

			// The shop hoists its page paper OUT of the pages: `FrontPage`, `PageTextureFar` and
			// `PageTextureNear` are siblings of `Book`, not children of a page.
			ChromeUnits = { "Foreground" },

			// Godot owns the row list and its cursor; the rest of the assembly is guest art,
			// riding in on the guest's stated position for `ExpandableMenu`.
			OwnedElsewhere = { "ExpandableMenu.Buttons", "MenuHighlight" },
			PageRootUnit = "Pages", BackdropUnit = "background_green",

			// ONE guest region holding two rows (`sub_829933C8`, automatic): Page2's text and
			// Page3's group. Only Page2's bar is ever handed to `sub_828C33F0`.
			ScrollContent = "Foreground.Book.Pages.Page2.Contents.Text.Group|Foreground.Book.Pages.Page3.Contents.Group",
			ScrollMask = "Mask|Mask",
			ScrollBar = "Foreground.Book.Pages.Page2.Contents.Scrollbar|",

			// The front page (name, anecdote, keeper, owner, opinions, sale, discount, wealth) is
			// what its fill `sub_82991800` wrote at the open, as the fill states it
			// (Engine_UiShopFrontPage, Engine_UiPageFills).
			PreviewAnchor = "Foreground.Book.Pages.Page1.Contents.ModelHider.model_proxy.model",
		});

		// The WORLD MAP's region info page — sub-state +88's writer `sub_829AB0E0`, mode 41.
		// StarRating is a METER and stays off this list.
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 23, Name = "worldmap", OwnsList = true,
			// Two numbered pages (the named category pages are the provider's); the stack parks at 400.
			Pages = { "Foreground.Book.Pages.Page1", "Foreground.Book.Pages.Page2" },
			PageStackAt = Vector3.Zero,
			Provider = Fable2PageProviders.WorldMap,
			Presentation = "worldmapscreen",

			// NO TINT BACKDROP — the world map's ground is six map tiles, so they are chrome
			// rather than a backdrop, and there is nothing to stretch over the revealed edges.
			ChromeUnits =
			{
				"WorldMap_Spring_A", "WorldMap_Spring_B", "WorldMap_Spring_C",
				"WorldMap_Spring_D", "WorldMap_Spring_C2", "WorldMap_Spring_A2",
				"Foreground",
			},

			// Godot owns the row list and its cursor; the rest of the assembly is guest art,
			// riding in on the guest's stated position for `ExpandableMenu`.
			OwnedElsewhere = { "ExpandableMenu.Buttons", "MenuHighlight" },
			PageRootUnit = "Pages",

			// The region page scrolls — `sub_829AB060` registers `Page1.Contents.Text` under
			// `MaskPage2` with the page's own scrollbar, and `sub_826C63C8` code 27 (the right
			// stick) drives it.
			// The Quests (`sub_829AF5D0`, `MaskQuests`), Family (`sub_829B03F0`, `MaskFamily`)
			// and Legend (`sub_829ABF30`, automatic, `MaskPage3`) pages scroll too. Sales
			// registers `MaskPage2` over its Description with its own bar — a property's sign
			// runs longer than the page (Engine_UiPropertySource.FillPage).
			ScrollContent =
				"Foreground.Book.Pages.Page1.Contents.Text|" +
				"Foreground.Book.Pages.QuestsPage.Contents.Description|" +
				"Foreground.Book.Pages.FamilyPage.Contents.Group|" +
				"Foreground.Book.Pages.Page2.Contents.Legend|" +
				"Foreground.Book.Pages.SalesPage.Contents.Description",
			ScrollMask = "MaskPage2|MaskQuests|MaskFamily|MaskPage3|MaskPage2",
			ScrollBar =
				"Foreground.Book.Pages.Page1.Contents.Scrollbar|" +
				"Foreground.Book.Pages.QuestsPage.Contents.Scrollbar|" +
				"Foreground.Book.Pages.FamilyPage.Contents.Scrollbar|" +
				"Foreground.Book.Pages.Page2.Contents.Scrollbar|" +
				"Foreground.Book.Pages.SalesPage.Contents.Scrollbar",

			// And it reflows — `sub_829AB8A8` measures the wrapped merchants list and moves
			// every row below it by the overflow, silver keys taking the gargoyle row's place
			// everywhere but Mystery Island. The list is the guest's own move list, verbatim.
			FlowMeasured = "Foreground.Book.Pages.Page1.Contents.Text.MerchantsList",
			FlowBase = "Foreground.Book.Pages.Page1.Contents.Text.MerchantListBase",
			FlowMoved =
			{
				"Foreground.Book.Pages.Page1.Contents.Text.OwnedAmount",
				"Foreground.Book.Pages.Page1.Contents.Text.BuildingsOwned",
				"Foreground.Book.Pages.Page1.Contents.Text.ChestAmount",
				"Foreground.Book.Pages.Page1.Contents.Text.ChestsOpened",
				"Foreground.Book.Pages.Page1.Contents.Text.DigspotAmount",
				"Foreground.Book.Pages.Page1.Contents.Text.DigspotsDug",
				"Foreground.Book.Pages.Page1.Contents.Text.Lines.line10",
				"Foreground.Book.Pages.Page1.Contents.Text.Lines.line11",
				"Foreground.Book.Pages.Page1.Contents.Text.DivespotsSearchedAmount",
				"Foreground.Book.Pages.Page1.Contents.Text.DivespotsSearched",
				"Foreground.Book.Pages.Page1.Contents.Text.GargoylesDestroyedAmount",
				"Foreground.Book.Pages.Page1.Contents.Text.GargoylesDestroyed",
				"Foreground.Book.Pages.Page1.Contents.Text.Lines.line12",
				"Foreground.Book.Pages.Page1.Contents.Text.Lines.line13",
				"Foreground.Book.Pages.Page1.Contents.Text.Lines.line14",
			},
			FlowTailAnchor = "Foreground.Book.Pages.Page1.Contents.Text.GargoylesDestroyedAmount",
			FlowTailMoved =
			{
				"Foreground.Book.Pages.Page1.Contents.Text.SilverKeysFoundAmount",
				"Foreground.Book.Pages.Page1.Contents.Text.SilverKeysFound",
			},
		});

		// 28 REAL ESTATE — `sub_82998300` spawns the conditional command rows (buy, sell, rent
		// out, evict, adjust rent…) and the detail text is written on activate, so the list is
		// owned like every other book's.
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 28, Name = "realestate", Presentation = "realestatescreen",
			// Two pages describing the one building; the stack is authored on the leaf.
			Pages = { "Foreground.Book.Pages.Page1", "Foreground.Book.Pages.Page2" },
			Provider = Fable2PageProviders.RealEstate,
			OwnsList = true,

			// A row is its event (110..120): the verb posts it to the handler's own OnMenuEvent,
			// which reads the event's type and nothing else (0x826B2224).
			RowAction = Fable2UiVerbs.RealEstateEventAction,

			// DELIBERATELY UNBOUND — see the conditional-list note in BuildDefaults.
			FadesTopEnd = false,
			ChromeUnits = { "Foreground" },

			// Godot owns the row list and its cursor; the rest of the assembly is guest art,
			// riding in on the guest's stated position for `ExpandableMenu`.
			OwnedElsewhere = { "ExpandableMenu.Buttons", "MenuHighlight" },
			PageRootUnit = "Pages", BackdropUnit = "background_grey",

			// Both pages scroll under `Mask` (explicit — the mask is not a `Scroller`):
			// `sub_82998FB0` registers `Page1.Group` (not under Contents) and `sub_8299B068`
			// `Page2.Contents.Group`.
			ScrollContent = "Foreground.Book.Pages.Page1.Group|Foreground.Book.Pages.Page2.Contents.Group",
			ScrollMask = "Mask|Mask",
			ScrollBar = "Foreground.Book.Pages.Page1.Contents.Scrollbar|Foreground.Book.Pages.Page2.Contents.Scrollbar",
		});

		// 1 PHOTO MODE — one dialog box, not a book.
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 1, Name = "photomode", Presentation = "photomodescreen",
			ChromeUnits = { "PhotoMode" },
		});

		// 2 THE SPELL WHEEL. A ring of slots rather than a page — `MenuItemTemplate` is the slot
		// the guest clones per spell, and `CurrentItemLight` is the selection glow. No backdrop:
		// the wheel stands over the world.
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 2, Name = "spellwheel", Presentation = "spellsmenu",

			// No chrome, deliberately: `SpellsMenu.bgf` is stale art. Its `Ring`, `Root`,
			// `MenuItemTemplate` and `CurrentItemLight` name no string in the binary, and
			// `clone` appears zero times across the nine live `expandablemenu` scripts — rows
			// come from a FIXED AUTHORED POOL (`MenuItemButton1..N`, matching `NUM_VISIBLE`)
			// rebound as the list scrolls.
			//
			// The real spell widget is `SpellAssigning`, in `hud.bgf` and in the pause book at
			// `Foreground.Book.Pages.Page4.Contents.Blade.SpellAssigning`: five rows at y
			// -160/-80/0/+80/+160 with a five-cell carousel, the same controller as the
			// abilities blade.
		});

		// 24 CO-OP — NOT A BOOK. A two-page spread with its own frames and backgrounds, a
		// connecting state and a separate load-game panel; its canvas is 1280x720 where the
		// in-game books are 960x720.
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 24, Name = "coop", Presentation = "coopscreen",
			ChromeUnits =
			{
				"LeftPage", "RightPage", "LeftFrame2", "RightFrame2",
				"LeftBackground", "RightBackground", "Connecting", "menu", "loadgame",
			},

			// No backdrop unit: coopscreen.bgf ships none (its `BackgroundTint` is the load-game
			// dimmer). The provider anchors the frame groups to the visible edge instead.

			// WHERE THE MANNEQUIN HANGS. Player two's henchman preview is a live entity the guest
			// spawns (`sub_82438818`, stored at `screen+276`), drawn by the host at the right page's
			// authored model anchor. The left page's anchor is player one's and stays empty until
			// the host's own hero is previewed there.
			//
			// NO `Foreground.` PREFIX. This book clones `LeftPage` and `RightPage` as separate chrome
			// units, each a top-level node named for the unit (`clone.Name = unit`), so unlike the
			// item books — one `Foreground` unit — there is no `Foreground` node to walk from. The
			// authored path matched nothing for exactly that reason.
			PreviewAnchor = "RightPage.model_proxy.model",

			// THE PICKBOXES AND THE BUTTON CAPTIONS STAY THE GUEST'S. Chrome alone drew the two
			// pages with the .bgf's AUTHORED labels, which is why `Player 1`, `Cancel` and
			// `Accept` appeared while the runtime values did not.
			//
			// The SLIDERS ARE NOT IN THIS LIST ANY MORE. Taking them from the guest did not
			// hold: a run showed all six reading `tag='Text' shows='Text'`, the authored
			// placeholder, once the guest stopped writing them — the exact failure this field's
			// own contract warns about ("a field the guest stopped writing went quietly
			// stale"). `CoopProvider` hides them now: progression is per player, so there is no
			// share to show.
			// `loadgame` too: the save picker Y opens (`sub_8243B070`) writes each card's quest line
			// and the description into that unit, and without it the authored placeholder
			// ("fdggdf dfg …") stood where the guest's text was.
			// The pickbox and button captions, the connecting line and the selector and button
			// slides are composed from the screen's own data (Engine_UiCoopPage) — nothing is
			// read back from these units.
			RestFromFile = true,

			// AND THIS IS WHAT LIGHTS THE PROFILE BLOCKS. The gamertag, its stars, the
			// reputation and gamerscore captions and `Select Gamer Profile` are all BUILT and
			// POPULATED — a live read showed the gamertag already resolved to a real name — and
			// held dark by the guest's slides.
			//
			// WRITING A VALUE IS NOT ENOUGH HERE, which this note used to claim. A value only
			// DROPS the guest's opacity so the slide or the authored number takes over, and
			// both are zero for this block: `ProfileSelector.Buttons` carries `auth=0`. Nor
			// does opacity inherit — a live read found `ProfileSelector` at 1.00 with all
			// forty-odd elements under it at 0.01. The provider states the number outright
			// over the whole subtree; see its `Opacities`.
			Provider = Fable2PageProviders.Coop,
		});

		// The sub-screens are CUT CONTENT, so they have no templates: `expressionsscreen`,
		// `orbinfoscreen`, `regionsscreen`, `questsscreen`, `weaponsscreen`,
		// `inventoryitemsscreen` and `quickmainmenu` ship in the bank and are never loaded — the
		// executable names 33 of the 52 `.bgf`, and every path into the loader passes a global
		// literal or a value out of the screen-id table.
		//
		// Regions is a SUBMENU of the world map instead, populated by `sub_829A3A30` with row
		// id = index + 999 and event type 41 (the fast-travel contract). Quests live on the
		// logbook, the world map's page and the HUD banner.

		// The three remaining dialogs with no owned view: all flat — no book, no page stack.
		//
		// THE QUEST CARD IS A DISPLAY BOX (`GUI.DisplayQuestBoxParams`, class 0x820AE6C8), never
		// the current screen: it stands from `DisplayBoxScreen`. Its Open `sub_823CFBE0` writes
		// every string once — QuestDescription, the DetailsGroup amounts, Remember, the
		// conditions — so the text is the guest's; the info panel runs only for a current screen.
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 5, Name = "questrepeatable", Presentation = "questrepeatable",
			OwnsRevealedInput = true,
			RevealedAccept = Engine_UiScreenActions.BoxDismiss,
			ChromeUnits =
			{
				"PaperBackground", "Buttons", "Stars", "DetailsGroup", "QuestTitle",
				"QuestDescription", "QuestDescription2", "RiskLevel", "Remember",
				"Condition1", "Condition2",
			},

			// Stated by the box's own Open (`@DisplayBox.Open` kind 4), filled as it draws.
			Provider = Fable2PageProviders.QuestBox,
			RestFromFile = true,
		});

		// THE END CREDITS — drawn by Engine_UiCreditsRoll (the file's elements are anonymous,
		// so nothing here names chrome). The template carries only the input: the handler
		// `sub_826B7EF8` skips the roll on 36 or 37.
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 29, Name = "creditsscreen", Presentation = "creditsscreen",
			OwnsRevealedInput = true,
			RevealedAccept = Engine_UiScreenActions.ScreenSelect,
			RevealedBack = Engine_UiScreenActions.ScreenBack,
		});

		// THE OLD-STYLE INFO CARD: `GUI.DisplayInfoBoxParams` with no `DisplayBoxStyle` (the
		// native's default is 0, `sub_823033D0`) builds class 0x820EBF1C, and photo mode's
		// uploading notice is the same class. Its Open `sub_8299E490` writes Paper.Text / Title /
		// Accept / Info and the button alphas, then plays root slide 7 — the `Paper` variant the
		// display-box surface vetoes. The book stands only while that class is the box showing
		// (HostUiSource.ScreenOfBox): the acceptance and button boxes share the file and are the
		// display-box panel's. Input stays the guest's: A and hold-A are the class's raw slot
		// (`CAcceptHoldBox_vf4`), which posts message 34, and it does not freeze the game.
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 7, Name = "displaybox", Presentation = "displaybox",
			ChromeUnits = { "Paper" },

			// The card is stated by its class's Open (`@DisplayBox.Open` kind 8), the `infocard` provider.
			Provider = Fable2PageProviders.InfoCard,
			RestFromFile = true,
		});

		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 11, Name = "minigamecompleted", Presentation = "minigamecompleted",
			OwnsRevealedInput = true,
			RevealedAccept = Fable2UiVerbs.MiniGameConfirmCommand,

			// `Select` has two bearers — the button GROUP is 0, its label Text is 1 — so the
			// bare name is right here and the label comes through the text sweep.
			// `Title` too: un-cloned it had no node, and the tally drew with no heading.
			ChromeUnits = { "paperbox", "Select", "Motif", "Amounts", "Title" },

			// The title, the button, the rows and their count-up are what `TurnOnMiniGameComplete`
			// and `AddAmountToMiniGameComplete` state (Engine_UiGuiWidgets), drawn by the `minigame`
			// provider over the names the file binds.
			Provider = Fable2PageProviders.MiniGame,
			RestFromFile = true,
		});

		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 12, Name = "questcompletion", Presentation = "questcompletion",
			OwnsRevealedInput = true,
			RevealedAccept = Engine_UiScreenActions.ScreenDismiss,

			// `Component` is the paperbox; `frame2` is a SECOND, hand-authored ten-piece frame
			// whose parts collide by name with the paperbox's, which is why the units are named
			// at the top level and never by their pieces.
			//
			// THE FIVE TEXT ELEMENTS ARE UNITS TOO. `QuestTitle`, `QuestDescription`, `Reward`,
			// `RenownPoints` and `Points` are SIBLINGS of the units above under `elements`, and
			// `elements` is not itself a unit — so nothing cloned them, they got no field, and
			// the page drew a title, one bullet and the Done button over an otherwise blank
			// parchment. That is the whole of the "reward screen is incomplete" report: no
			// heading, no description, no renown line, no amount. They cannot be reached by
			// naming `elements` instead, because that would fold `Component` and `frame2` into
			// one nested unit and collide their ten-piece frames.
			ChromeUnits =
			{
				"Component", "frame2", "leaf", "Bullet1", "Bullet2", "Buttons", "Image",
				"QuestTitle", "QuestDescription", "Reward", "RenownPoints", "Points",
			},

			// EVERY STRING, BULLET AND THE PICTURE ARE REGISTRY STATEMENTS, as on the tally next
			// door: `ShowEpilogueScreen` (`sub_823D3FF0`), `SetGoldRewardInEpilogue` and
			// `AddItemRewardToEpilogueScreen` publish the Epilogue* texts, the EpilogueItem1/2
			// toggles and the `EpilogueImage` screenshot (registry 12, TU1). The class writes no
			// element directly.
			RestFromFile = true,
		});

		// 10 QUERY — the villager card. Opened from three sites — the interaction chain
		// `sub_823FDA78`, the queued-request switch `sub_82805360` case 14, and the script
		// command `TriggerRenameScreen` — all calling `sub_826CCC88(screen, entity)`, which
		// stores the subject at `screen+28` and fills ONCE; there is no per-frame filler.
		//
		// Everything it shows is published by name, so it needs no provider: three float meters
		// (`Love`, `Funny`, `Attractive` — the middle one INVERTED, `(x-1) * -0.5 * 100`) plus
		// string headings and likes/dislikes rows, all through the FNV-1 registries the HUD
		// reads. `elements` is the whole card; `Layer`'s other two children are the key light
		// and the camera, which the host supplies.
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 10, Name = "query", Presentation = "queryscreen",
			OwnsRevealedInput = true,

			// B closes the card and X asks to rename its target (`Query:<p>:close|rename`,
			// gui_screen_stub_query.cpp); the guest asks the platform keyboard, which
			// Engine_UiPlatformKeyboard answers.
			RevealedBack = Fable2UiVerbs.QueryCloseAction,
			XAction = Fable2UiVerbs.QueryRenameAction,
			ChromeUnits = { "elements" },

			// THE CARD IS REGISTRY STATEMENTS: `sub_826CCC88` and its fillers publish every text
			// (QueryName, QueryDesc, the likes and dislikes), the gift/divorce/marry toggles and
			// eight meters by name, which the book reads by its file's bindings — the meters
			// scrub their element's own timeline (Engine_UiOwnedBook.ScrubMeters). The one direct
			// write, the rename prompt's slide, is the guest's told `GoToSlide` on
			// `elements.buttons` (`sub_826CCFC0` 0x826cd184, disc 0x82583eac).
			RestFromFile = true,
		});

		// THE FRONT END. Its overlays — the storage dialog, the loading animation, the countdown
		// — live in the same tree as the menu frames and the guest lights one at a time, which
		// the per-unit draw gate handles. One unit: `Layer` has fourteen spatial children and
		// all the menu chrome hangs off `Foreground`:
		//
		//     Foreground / ExpandableMenu (x -800) / Buttons (26 rows), MenuHighlight,
		//                                            KeyLight, LeftFrame
		//                / Book (x 400)            / Pages / FriendsPage, RightFrame
		//
		// `LeftFrame` is INSIDE ExpandableMenu, not a sibling of `Book`: the assembly is authored
		// at x -800 and the shipped Lua tweens it in, so cloning `LeftFrame` alone would park it
		// at its authored canvas x of -1036, off screen. Taking `Foreground` whole keeps the
		// parenting, so the guest's one stated position on `ExpandableMenu` carries it in.
		//
		// `Theme` is not a unit: it is the off-canvas art library at y -1000 the frames are
		// cloned FROM. `choosecard`, `loadgame` and `dialog` are siblings of `Foreground`, drawn
		// by their own Engine_FrontEnd* views.
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 1010, Name = "frontend", Presentation = "frontendmainmenu",
			ChromeUnits = { "Foreground", "LiveCountdown", "LoadingAnimation" },

			// The front end stands on its file and the guest's statements to it (§15j): its
			// slides, plays, attributes, switches, texts and the load cards' portraits.
			RestFromFile = true,
			PageRootUnit = "Pages",

			// The row pool and its cursor sit INSIDE `Foreground` and Godot draws both.
			OwnedElsewhere =
			{
				"ExpandableMenu.Buttons", "MenuHighlight", "choosecard", "loadgame", "dialog",
			},
		});

		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 1011, Name = "title", Presentation = "frontendstartupscreen",
			RestFromFile = true,

			// "Press Start" has a space in the element name.
			//
			// `White` is a UNIT, not a backdrop: a direct child of `Layer`, a 1280x720 quad at
			// (0,0,0) scale (12.8, 7.2), sibling of `logo`. Canvas +Z is toward the camera, so
			// at z 0 it sits IN FRONT of the logo at z -1 and PressStart at z -2 — the logo's
			// white flash, authored at full opacity with its fade in the Anark scene and in no
			// script. Engine_FrontEndTitle runs that fade.
			ChromeUnits = { "logo", "White", "PressStart", "Legal" },

			// The guest's dialog is drawn once, by the host's warning box (GameUi.DriveGuestDialog)
			// with its told texts, as on the main menu. Its behaviours still fire from the book's
			// playback of the file, which is not the unit.
			OwnedElsewhere = { "dialog" },
		});

		// THE SCROLLING BACKDROP is a 7x2 grid of 512x512 tiles with a second copy at +3453 to
		// hide the wrap — the period is 3453, not the 3584 the tile maths suggests, because the
		// copy overlaps by 131. One unit: the whole `Scrolling` group, scrolled as a whole.
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 1012, Name = "backdrop", Presentation = "scrollingbackground",
			ChromeUnits = { "Scrolling" },
			RestFromFile = true,
			PageRootUnit = "Scrolling",

			// The scroll is the guest's own statement: it moves `Group` leftwards and the clone
			// binding writes every stated transform, so the wrap copy rides in with it. A host
			// rate would be a second clock against the one the title's music and fade run on.
		});

		// THE ACHIEVEMENTS SCREEN — host-only id 1013, on the logbook's own furniture.
		//
		// The guest has no screen for these. Its answer to the logbook's `Achievements` row is
		// `XamShowAchievementsUI` (`sub_826D9620` case 150), the console's guide blade, which on
		// this platform opens nothing at all — so there is no guest screen id to borrow and no
		// tab for `sub_826B89E0`'s switch to map. It is ours end to end.
		//
		// On `logbookscreen` because the shape already fits: a full-height row list on the left
		// and a subject page laying out a title, a 64-unit picture and a paragraph on the right,
		// which is exactly an achievement. Nothing about that art is logbook-specific — the book
		// is cloned from the FILE-ONLY pattern book, so this record needs no live presentation of
		// the guest's and stands whether or not the logbook has ever been opened.
		//
		// The Active Mods book is the same screen over its own list: one page, two subjects. The
		// host page book (Engine_UiHostBoxPage) is the same screen again: a host question's
		// title and body on the page, Continue and Back as the rows.
		foreach ((uint id, string name, string provider) in new[]
		{
			(Engine_UiMultiplayerPage.ScreenId, "multiplayer", Fable2PageProviders.Multiplayer),
			(Engine_UiMultiplayerPage.ModsScreenId, "mods", Fable2PageProviders.Multiplayer),
			(Engine_UiHostBoxPage.ScreenId, "hostbox", Engine_UiPageProviders.HostBox),
		})
		{
			AddScreen(screens, new Engine_UiScreenTemplate
			{
				ScreenId = id, Name = name, OwnsList = true,
				Provider = provider,
				Presentation = "logbookscreen",
				ChromeUnits = { "Foreground" },
				OwnedElsewhere = { "ExpandableMenu.Buttons", "MenuHighlight" },
				PageRootUnit = "Pages", BackdropUnit = "background_yellow",

				// THE PAGE IS BOXED AND SCROLLS: the borrowed Expressions page's contents are the
				// logbook's own scroll region (screen 17), clipped to `Mask`, with its scroll bar —
				// a mod's description or a friend's status runs as long as it is written.
				ScrollContent = "Foreground.Book.Pages.ExpressionsPage.Contents",
				ScrollMask = "Mask",
				ScrollBar = "Foreground.Book.Pages.ExpressionsPage.Scrollbar",
			});
		}

		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = Engine_UiAchievementPage.ScreenId, Name = "achievements", OwnsList = true,
			Provider = Fable2PageProviders.Achievements,
			Presentation = "logbookscreen",

			// The list a host screen stands (Engine_UiHostScreen).
			Menu = Engine_UiAchievementPage.MenuName,
			ChromeUnits = { "Foreground" },

			// Godot owns the row list and its cursor. On a host-only screen it owns the anchor
			// too — there is no guest `ExpandableMenu` position to ride in on, so the view sits
			// at the pool's authored spot.
			OwnedElsewhere = { "ExpandableMenu.Buttons", "MenuHighlight" },
			PageRootUnit = "Pages", BackdropUnit = "background_yellow",

			// THE PAGE IS BOXED AND SCROLLS, as its siblings on this book: an achievement's
			// description runs as long as it is written.
			ScrollContent = "Foreground.Book.Pages.ExpressionsPage.Contents",
			ScrollMask = "Mask",
			ScrollBar = "Foreground.Book.Pages.ExpressionsPage.Scrollbar",
		});

		// Screen 8 — the receive-items card, owned elsewhere; recorded for its preview anchor.
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 8, Name = "receiveitems", Presentation = "receiveitemsscreen",
			PreviewAnchor = "Foreground.SpinningCube.Cube",

			// Front, back — see `BoxUnit`.
			BoxPresentation = "receiveitemsscreen",
			BoxUnits = { "Foreground", "CardBack" },
			BoxPaths = { ["NameAndIcon"] = "NameAndIcon", ["Title"] = "Title", ["Text"] = "Text", ["IconGraphic"] = "IconGraphic", ["Buttons"] = "Buttons", ["Mask"] = "Mask", ["NameAndIcon/IconGraphic"] = "NameAndIcon/IconGraphic", ["NameAndIcon/Title"] = "NameAndIcon/Title", ["NameAndIcon/Text"] = "NameAndIcon/Text", ["Buttons/Buttons/"] = "Buttons/Buttons/", ["PaperBackground"] = "PaperBackground", ["Group"] = "Group", ["Scrollbar/UpArrow"] = "Scrollbar/UpArrow", ["Scrollbar/DownArrow"] = "Scrollbar/DownArrow", ["Scrollbar/Icon"] = "Scrollbar/Icon" },
		});

		// THE MODAL BOXES. Drawn by their own views (`Engine_UiBoxes`), which take the
		// presentation and the art units from these records BY ROLE — the position in
		// `boxUnits` is the role, stated beside each — so a mod re-points a box at its own art
		// in the same file it re-points a book. `boxPresentation`, not `presentation`: these
		// are not screens, and a record that named a live file would be taken for its book.
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = MessageBoxId, Name = "messagebox", BoxPresentation = "messageboxmulti",
			BoxUnits = { "Background", "paper" },             // dim, paper
			BoxPaths = { ["MessageBox"] = "MessageBox", ["paperbox"] = "paperbox", ["MessageText"] = "MessageText", ["MessageBox/Action"] = "MessageBox/Action", ["MessageBox/MessageText"] = "MessageBox/MessageText" },
		});
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = QueryBoxId, Name = "querybox", BoxPresentation = "querybox",
			BoxUnits = { "QueryBox", "Background", "Paper" }, // box, dim, paper
			BoxPaths = { ["Choices"] = "Choices", ["Affirmative"] = "Affirmative", ["Negative"] = "Negative", ["XAChoices"] = "XAChoices", ["EquipAndContinue"] = "EquipAndContinue", ["Continue"] = "Continue", ["ReloadChoices"] = "ReloadChoices", ["Reload"] = "Reload", ["Cancel"] = "Cancel", ["Text"] = "Text" },
		});
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = NumericBoxId, Name = "numericbox", BoxPresentation = "numericadjusterbox",
			BoxUnits = { "QueryBox", "Background", "Paper" }, // box, dim, paper
			BoxPaths = { ["Text"] = "Text", ["PriceWidget"] = "PriceWidget", ["Arrows"] = "Arrows", ["up"] = "up", ["down"] = "down", ["PriceWidget/Text"] = "PriceWidget/Text", ["PriceWidget/Arrows/"] = "PriceWidget/Arrows/" },
		});
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = MenuBoxId, Name = "menubox", BoxPresentation = "menubox",
			BoxUnits = { "Group" },                           // the option group
			BoxPaths = { ["Choices"] = "Choices", ["Title"] = "Title", ["Multiplayer_Button"] = "Multiplayer_Button", ["Choice1"] = "Choice1", ["Frame"] = "Frame", ["OK/CANCEL"] = "OK/CANCEL", ["Negative"] = "Negative", ["GamerTag"] = "GamerTag", ["gamertag"] = "gamertag", ["Choices/Multiplayer_Button"] = "Choices/Multiplayer_Button", ["Choice1/Frame/Choice1"] = "Choice1/Frame/Choice1", ["Choice1/Choice1"] = "Choice1/Choice1", ["Choice1/RimSelect"] = "Choice1/RimSelect", ["OK/CANCEL/Negative"] = "OK/CANCEL/Negative" },
		});
	}

	/// <summary>
	/// One pause tab — the command the guest's dispatch recognises, plus whatever the guest
	/// spawned UNDER it. `Source = "spawn"` reads the SPAWN CAPTURE alone (no Gameface Lua): the
	/// whole spawn tree flat, each row's parent stated by name (`String4`), so a tab's children
	/// are in hand whether or not the Lua has laid them out.
	///
	/// The capture is taken AT `SpawnButton`, so it carries name, parent, the decorated label,
	/// `(EventType, id)` and the rating, but NOT `Flags` (ghosted/showing) or `Emphasis`, which
	/// are written onto the Lua item afterwards by `SetItemEmphasis` and the ghosting setters —
	/// which is why the in-game screens cannot use `spawn` yet: a ghosted row that became
	/// committable would let the player activate something the game disabled. The pause menu is
	/// exempt because its ghosting is already ours —
	/// <see cref="Engine_PauseMenu.ApplyTabAvailability"/> ghosts an unavailable tab from
	/// `RefiiHostPauseTabsAvailable`, the guest's `sub_826B83D0` read directly.
	/// </summary>
	/// <param name="name">The guest's tab identity, also used to source any nested rows.</param>
	/// <param name="tag">The fallback localized label before the guest states its decoration.</param>
	/// <param name="tab">The native pause-tab lever's id.</param>
	/// <param name="when">The row condition the entry is built under; empty always holds.</param>
	/// <returns>A command row whose optional source does not make it an empty submenu.</returns>
	internal static Engine_UiMenuEntry Tab(string name, string tag, uint tab, string when = "") => new()
	{
		Name = name,
		Tag = tag,
		When = when,
		Command = $"{Engine_PauseMenu.TabCommandPrefix}{tab}",
		Source = "spawn",

		// A TAB ENTERS A SCREEN — `sub_8298DF98` spawns every tab with event type 300
		// (0x8298e438 …). Left 0, the source made it a submenu (301) with no children, and the
		// automatic submenu ghosting greyed Map, Items, Weapons, Clothing, Logbook and Options
		// for good. The guest's only tab ghosting is `sub_8298F648`'s availability, which
		// Engine_PauseMenu.ApplyTabAvailability states.
		EventType = (int)Engine_UiMenuConstants.EventType.EnterScreen,
	};

	/// <summary>
	/// A tab row the HOST authors outright — label from its tag, no guest row behind it. For
	/// the co-op variant: player two's guest pause root is the HENCHMAN's, whose spawned rows
	/// are other rows entirely, so a spawn-sourced "Weapons" bound to its ordinal came up
	/// captioned "Config" and ghosted by the henchman mask.
	/// </summary>
	private static Engine_UiMenuEntry HostTab(string name, string tag, uint tab, string when = "") => new()
	{
		Name = name,
		Tag = tag,
		When = when,
		Command = $"{Engine_PauseMenu.TabCommandPrefix}{tab}",
		// HOST-ONLY, so the stated-label pass leaves it alone: the positional fallback there
		// handed these rows the henchman list's captions by ordinal ('Config', 'Options').
		HostOnly = true,
	};

	private static void AddScreen(Dictionary<uint, Engine_UiScreenTemplate> screens, Engine_UiScreenTemplate template) =>
		screens[template.ScreenId] = template;

	/// <summary>
	/// The built-in books' wide layouts, from each file's own tree (`ui_viewer`,
	/// `REFII_UI_TREE=Layer`): the list assembly `Foreground.ExpandableMenu` carries the left
	/// cover, and every other child of `Foreground` belongs to the right-hand page. Stated in
	/// one table because the shipped books share one skeleton; an override record states its
	/// own lists.
	/// </summary>
	/// <summary>
	/// THE BOOKS THAT STAND ON THEIR FILE (<see cref="Engine_UiScreenTemplate.RestFromFile"/>), and
	/// what the guest's open leaves that their rest slides do not. Every value here was measured
	/// on the live game with the `rest:` check (2026-10-26) — a screen is listed once its check
	/// shows nothing left but what the host drives:
	///
	///   `ExpandableMenu.KeyLight` x 22 on every book; the item list's background page at x 66
	///   and its list title faded out; the dye swatch faded out on items and clothing; the
	///   clothing mannequin proxy at x 20; the world map's quest-page scroll bar faded out (it is
	///   no scroll region of ours).
	///
	/// The shop's front page is the guest's one-time fill, read whole as a guest unit; real
	/// estate's second page is the host's page turn and its rows blank by value. Gift (25) and
	/// decoration (27) are stated from IDA rather than measured: the item book's provider states
	/// what their page refreshes write (gold bag, prompts, hidden value lines, the title).
	///
	/// Pause stands on its file with <see cref="Engine_UiPauseMinimap"/> stating the minimap, its
	/// legend and the quest lines. Gift, furniture, decoration, crime and the one-off panels are
	/// not measured yet. Upgrade furniture (26) is stated from its targeted piece
	/// (<see cref="Engine_UiFurniturePage"/>, ABI 225).
	/// </summary>
	private static void StateRestStates(Dictionary<uint, Engine_UiScreenTemplate> screens)
	{
		const string keyLight = "Foreground.ExpandableMenu.KeyLight";
		foreach (uint id in new uint[] { 0, 14, 15, 16, 17, 18, 21, 22, 23, 25, 26, 27, 28 })
		{
			if (screens.TryGetValue(id, out Engine_UiScreenTemplate? screen))
			{
				screen.RestFromFile = true;
				screen.RestPose[keyLight] = new float?[] { 22f, null, null, null, null, null };
			}
		}

		if (screens.TryGetValue(15, out Engine_UiScreenTemplate? items))
		{
			items.RestPose["Foreground.Book.Pages.Page1_Background"] = new float?[] { 66f, null, null, null, null, null };
			items.RestOpacity["Foreground.ExpandableMenu.Title"] = 0f;
			items.RestOpacity["Foreground.Book.Pages.Page1.Contents.DyeSwatch"] = 0f;
		}

		// Gift and decoration share the items file but not its enter: only 15 hides the list
		// title and plays `Foreground` slide 2 (`sub_829B4E10`); both hide the swatch at enter
		// and never show it (`sub_829AA460` 0x829aa4cc, `sub_8299BF48` 0x8299bfd0).
		foreach (uint id in new uint[] { 25, 26, 27 })
		{
			if (screens.TryGetValue(id, out Engine_UiScreenTemplate? sibling))
			{
				sibling.RestOpacity["Foreground.Book.Pages.Page1.Contents.DyeSwatch"] = 0f;
			}
		}

		if (screens.TryGetValue(16, out Engine_UiScreenTemplate? clothing))
		{
			clothing.RestPose["Foreground.Book.Pages.Page1.Contents.TypeSpecific.model_proxy"] =
				new float?[] { 20f, null, null, null, null, null };
			clothing.RestOpacity["Foreground.Book.Pages.Page1.Contents.DyeSwatch"] = 0f;
		}

		// The shop: the item page's swatch as on the items screen. The front page is the guest's
		// one-time fill (`sub_82991800`), read whole as a guest unit — see GuestTextUnits.
		if (screens.TryGetValue(22, out Engine_UiScreenTemplate? shop))
		{
			shop.RestOpacity["Foreground.Book.Pages.Page1.Contents.DyeSwatch"] = 0f;
		}

		if (screens.TryGetValue(23, out Engine_UiScreenTemplate? map))
		{
			map.RestOpacity["Foreground.Book.Pages.QuestsPage.Contents.Scrollbar"] = 0f;
		}
	}

	/// <summary>
	/// THE SCREENS WHOSE HANDLER WRITES THE PURSE (<see cref="Engine_UiScreenTemplate.Purse"/>):
	/// every caller of the purse writer `sub_8246B5E8` (disc `sub_82837218`) — crime 0x826A90B8,
	/// shop 0x826AE4E8, real estate 0x826B1F2C, furniture 0x826B6164, decoration 0x826B70B4,
	/// abilities 0x826C2290, gift 0x826C45AC, world map 0x826C5E48, items 0x826D15B4, clothing
	/// 0x826D2D54, logbook 0x826D914C, options 0x826D9E94 — each writes
	/// `Foreground.ExpandableMenu.Money.Text` through `lhGuiElement_SetText` (0x8246b808), told.
	/// The pause root (0x826A4D80) owns no list, so its told purse already stands.
	/// </summary>
	private static void StatePurses(Dictionary<uint, Engine_UiScreenTemplate> screens)
	{
		foreach (uint id in new uint[] { 14, 15, 16, 17, 18, 21, 22, 23, 25, 26, 27, 28 })
		{
			if (screens.TryGetValue(id, out Engine_UiScreenTemplate? screen))
			{
				screen.Purse = true;
			}
		}
	}

	private static void StateWideLayouts(Dictionary<uint, Engine_UiScreenTemplate> screens)
	{
		const string list = "Foreground.ExpandableMenu";
		var right = new Dictionary<uint, string[]>
		{
			[0] = new[] { "Foreground.RightFrame", "Foreground.Pages", "Foreground.ReusableTokens" },
			[14] = new[] { "Foreground.Book" },
			[15] = new[] { "Foreground.Book" },
			[16] = new[] { "Foreground.Book" },
			[25] = new[] { "Foreground.Book" },
			[26] = new[] { "Foreground.Book" },
			[27] = new[] { "Foreground.Book" },
			[17] = new[] { "Foreground.Book", "Foreground.Mask" },
			[18] = new[] { "Foreground.Book" },
			[21] = new[] { "Foreground.Book" },
			[22] = new[]
			{
				"Foreground.Book", "Foreground.FrontPage",
				"Foreground.PageTextureFar", "Foreground.PageTextureNear",
			},
			[23] = new[] { "Foreground.Book" },
			[28] = new[] { "Foreground.Book" },
			[Engine_UiMultiplayerPage.ScreenId] = new[] { "Foreground.Book", "Foreground.Mask" },
			[Engine_UiMultiplayerPage.ModsScreenId] = new[] { "Foreground.Book", "Foreground.Mask" },
			[Engine_UiHostBoxPage.ScreenId] = new[] { "Foreground.Book", "Foreground.Mask" },
			[Engine_UiAchievementPage.ScreenId] = new[] { "Foreground.Book", "Foreground.Mask" },
		};

		foreach ((uint id, string[] pieces) in right)
		{
			if (screens.TryGetValue(id, out Engine_UiScreenTemplate? screen))
			{
				screen.WideLeft = new List<string> { list };
				screen.WideRight = new List<string>(pieces);
			}
		}

		// THE LOGBOOK PRESENTATION'S PAGE ENDS SHORT OF THE COVER. `logbookscreen.bgf` authors every
		// subject page's title centred at x 207 over a divider 384 wide (`Title` pos (207, 279),
		// halign 1, wrap 0; `motifs` pos (207, 236), scale 3.84 of the 100-unit quad: x 15..399),
		// on parchment that runs from the spine to about x 400. A title wider than its divider
		// ran under the spine and the cover (a friend row's "Name — in Fable II — Exploring").
		// Every screen drawn from that presentation states its TEXT COLUMN as its page: the subject
		// pages' lines start at x 50 (left-aligned labels, `Clothing` pos (50, 130) halign 0) and end
		// at x 387 (right-aligned values, `ClothingValue` pos (387, 130) halign 2) — the authored inset
		// inside the divider's 15..399, so a fitted title keeps the page's own margin.
		foreach (uint id in new[]
		{
			17u, Engine_UiMultiplayerPage.ScreenId, Engine_UiMultiplayerPage.ModsScreenId,
			Engine_UiHostBoxPage.ScreenId, Engine_UiAchievementPage.ScreenId,
		})
		{
			if (screens.TryGetValue(id, out Engine_UiScreenTemplate? screen))
			{
				screen.PageSpan = new Vector2(50f, 387f);
			}
		}

		// The front end is the same book skeleton; its painting is the `backdrop` record's.
		if (screens.TryGetValue(1010, out Engine_UiScreenTemplate? frontEnd))
		{
			frontEnd.WideLeft = new List<string> { list };
			frontEnd.WideRight = new List<string> { "Foreground.Book" };
		}

		if (screens.TryGetValue(1012, out Engine_UiScreenTemplate? painting))
		{
			painting.WideGround = new List<string> { "Scrolling" };
		}

		// CO-OP HAS NO BACKDROP (coopscreen.bgf, `Foreground` at 1.3): the pages and frames
		// ride out and each page's parchment widens from the gutter to stay under them.
		if (screens.TryGetValue(24, out Engine_UiScreenTemplate? coop))
		{
			coop.WideLeft = new List<string> { "Foreground.LeftPage", "Foreground.LeftFrame2" };
			coop.WideRight = new List<string> { "Foreground.RightPage", "Foreground.RightFrame2" };
			coop.WideStretch = new List<string>
			{
				"Foreground.LeftBackground.PageTexture", "Foreground.RightBackground.PageTexture",
			};
		}

		if (screens.TryGetValue(23, out Engine_UiScreenTemplate? worldMap))
		{
			worldMap.WideGround = new List<string>
			{
				"WorldMap_Spring_A", "WorldMap_Spring_B", "WorldMap_Spring_C",
				"WorldMap_Spring_D", "WorldMap_Spring_C2", "WorldMap_Spring_A2",
			};
		}
	}

}
