namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// THE GAME'S OWN <c>ui/templates.json</c> — every menu and screen record the rebuilt UI runs,
/// in the format a mod's file takes and composed by the same reader (<see cref="Engine_UiTemplateSet"/>):
/// the guest's population tables first, then this file, then each enabled mod's in load order, then
/// the player's. The game is a mod; a modder reads here what they compose over, and the Studio's
/// "as it composes" view starts from it.
/// </summary>
/// <remarks>
/// <para>Embedded rather than shipped loose, so it is in every export of the app. It is GENERATED
/// from the C#-authored records, which keep the evidence each was decoded from
/// (<see cref="Engine_UiTemplates.AuthoredDefaults"/>, <see cref="Engine_UiTemplates.GameTemplatesText"/>),
/// and a test holds the two identical: change a record there, run that test with
/// REFII_WRITE_GAME_TEMPLATES set to a path, and paste the written text here.</para>
/// <para>Menus appear whole where the authored records add or change one over the population
/// tables; screens state only the fields that differ from the reader's default.</para>
/// </remarks>
public static class Engine_UiGameTemplates
{
	public const string Text = """
{
  "menus": {
    "OptionsMenu": {
      "highlight": "",
      "title": "",
      "source": "",
      "rowCommand": "",
      "entries": [
        {
          "name": "Game",
          "tag": "GUI_SCREEN_OPTIONS_GAME",
          "command": "OptionsPage:Game",
          "icon": "Art\\GUI\\WheelIcons\\Abilities\\icon_logbook.tga",
          "eventType": 70
        },
        {
          "name": "VideoSettings",
          "tag": "GUI_SCREEN_OPTIONS_VIDEO",
          "hostOnly": true,
          "icon": "Art\\GUI\\WheelIcons\\Abilities\\icon_logbook.tga",
          "counted": false,
          "children": [
            {
              "name": "Video",
              "tag": "GUI_SCREEN_OPTIONS_GAMMA",
              "command": "OptionsPage:Video",
              "icon": "Art\\GUI\\WheelIcons\\Abilities\\icon_logbook.tga",
              "eventType": 72
            },
            {
              "name": "DynamicSkyAndWeather",
              "tag": "Dynamic Sky and Weather",
              "command": "options:weather",
              "hostOnly": true,
              "icon": "Art\\GUI\\WheelIcons\\Abilities\\icon_logbook.tga",
              "counted": false
            }
          ]
        },
        {
          "name": "ModSettings",
          "tag": "Mods",
          "source": "mod-settings",
          "hostOnly": true,
          "icon": "Art\\GUI\\WheelIcons\\Abilities\\icon_logbook.tga",
          "counted": false
        },
        {
          "name": "ControlsSettings",
          "tag": "GUI_SCREEN_OPTIONS_CONTROLS",
          "hostOnly": true,
          "icon": "Art\\GUI\\WheelIcons\\Abilities\\icon_logbook.tga",
          "counted": false,
          "children": [
            {
              "name": "Controls",
              "tag": "GUI_SCREEN_OPTIONS_CONTROLS",
              "command": "OptionsPage:Controls",
              "icon": "Art\\GUI\\WheelIcons\\Abilities\\icon_logbook.tga",
              "eventType": 71
            },
            {
              "name": "ControllerVibration",
              "tag": "Controller Vibration",
              "command": "options:vibration",
              "hostOnly": true,
              "icon": "Art\\GUI\\WheelIcons\\Abilities\\icon_logbook.tga",
              "counted": false
            },
            {
              "name": "ControllerAndKeyboard",
              "tag": "Controller and Keyboard",
              "command": "options:input",
              "hostOnly": true,
              "icon": "Art\\GUI\\WheelIcons\\Abilities\\icon_logbook.tga",
              "counted": false
            }
          ]
        },
        {
          "name": "Audio",
          "tag": "GUI_SCREEN_OPTIONS_AUDIO",
          "command": "OptionsPage:Audio",
          "icon": "Art\\GUI\\WheelIcons\\Abilities\\icon_logbook.tga",
          "eventType": 73
        }
      ]
    },
    "LogbookMenu": {
      "highlight": "",
      "title": "",
      "source": "",
      "rowCommand": "",
      "entries": [
        {
          "name": "Personality",
          "tag": "GUI_SCREEN_LOGBOOK_PERSONALITY",
          "icon": "Art\\GUI\\WheelIcons\\icon_logbook.tga",
          "eventType": 301,
          "children": [
            {
              "name": "Alignment",
              "tag": "GUI_SCREEN_LOGBOOK_ALIGNMENT",
              "icon": "Art\\GUI\\WheelIcons\\icon_logbook.tga",
              "eventType": 64
            },
            {
              "name": "Renown",
              "tag": "GUI_SCREEN_LOGBOOK_RENOWN",
              "icon": "Art\\GUI\\WheelIcons\\icon_logbook.tga",
              "eventType": 65
            },
            {
              "name": "Attractiveness",
              "tag": "GUI_SCREEN_LOGBOOK_ATTRACTIVENESS",
              "icon": "Art\\GUI\\WheelIcons\\icon_logbook.tga",
              "eventType": 66
            },
            {
              "name": "Stats",
              "tag": "GUI_SCREEN_LOGBOOK_STATS",
              "icon": "Art\\GUI\\WheelIcons\\icon_logbook.tga",
              "eventType": 67
            },
            {
              "name": "Dog",
              "tag": "GUI_SCREEN_LOGBOOK_DOG",
              "icon": "Art\\GUI\\WheelIcons\\icon_logbook.tga",
              "eventType": 68
            }
          ]
        },
        {
          "name": "Expressions",
          "tag": "GUI_SCREEN_LOGBOOK_EXPRESSIONS",
          "source": "capture:Expressions",
          "icon": "Art\\GUI\\WheelIcons\\icon_logbook.tga",
          "eventType": 301
        },
        {
          "name": "CompletedQuests",
          "tag": "GUI_SCREEN_LOGBOOK_COMPLETED_QUESTS",
          "source": "capture:CompletedQuests",
          "icon": "Art\\GUI\\WheelIcons\\icon_logbook.tga",
          "eventType": 301
        },
        {
          "name": "Achievements",
          "tag": "GUI_SCREEN_LOGBOOK_ACHIEVEMENTS",
          "drop": true,
          "icon": "Art\\GUI\\WheelIcons\\icon_logbook.tga",
          "eventType": 150
        },
        {
          "name": "Help",
          "tag": "GUI_SCREEN_LOGBOOK_HELP",
          "source": "capture:Help",
          "icon": "Art\\GUI\\WheelIcons\\icon_logbook.tga",
          "eventType": 301
        }
      ]
    },
    "AugmentMenu": {
      "highlight": "",
      "title": "GUI_SCREEN_TITLE_AUGMENT",
      "source": "inventory:augment",
      "rowCommand": "",
      "entries": []
    },
    "AugmentRemoveMenu": {
      "highlight": "",
      "title": "GUI_SCREEN_AUGMENT_REMOVAL_TITLE",
      "source": "inventory:augmentremove",
      "rowCommand": "",
      "entries": []
    },
    "DyeMenu": {
      "highlight": "",
      "title": "",
      "source": "",
      "rowCommand": "",
      "entries": [
        {
          "name": "Hats",
          "tag": "GUI_SCREEN_INVENTORY_CATEGORIES_HATS",
          "source": "inventory:dye:17",
          "icon": "Art\\GUI\\WheelIcons\\icon_world_map.tga",
          "eventType": 301
        },
        {
          "name": "Coats",
          "tag": "GUI_SCREEN_INVENTORY_CATEGORIES_COATS",
          "source": "inventory:dye:18",
          "icon": "Art\\GUI\\WheelIcons\\icon_world_map.tga",
          "eventType": 301
        },
        {
          "name": "Shirts",
          "tag": "GUI_SCREEN_INVENTORY_CATEGORIES_SHIRTS",
          "source": "inventory:dye:19",
          "icon": "Art\\GUI\\WheelIcons\\icon_world_map.tga",
          "eventType": 301
        },
        {
          "name": "Gloves",
          "tag": "GUI_SCREEN_INVENTORY_CATEGORIES_GLOVES",
          "source": "inventory:dye:20",
          "icon": "Art\\GUI\\WheelIcons\\icon_world_map.tga",
          "eventType": 301
        },
        {
          "name": "Pants",
          "tag": "GUI_SCREEN_INVENTORY_CATEGORIES_PANTS",
          "source": "inventory:dye:21",
          "icon": "Art\\GUI\\WheelIcons\\icon_world_map.tga",
          "eventType": 301
        },
        {
          "name": "Shoes",
          "tag": "GUI_SCREEN_INVENTORY_CATEGORIES_SHOES",
          "source": "inventory:dye:22",
          "icon": "Art\\GUI\\WheelIcons\\icon_world_map.tga",
          "eventType": 301
        },
        {
          "name": "Accessories",
          "tag": "GUI_SCREEN_INVENTORY_CATEGORIES_MASKS",
          "source": "inventory:dye:23,24",
          "icon": "Art\\GUI\\WheelIcons\\icon_world_map.tga",
          "eventType": 301
        },
        {
          "name": "Dye Hero Hair",
          "tag": "GUI_SCREEN_INVENTORY_CATEGORIES_DYE_HERO_HAIR",
          "source": "inventory:dye:9,10,11",
          "icon": "Art\\GUI\\WheelIcons\\icon_world_map.tga",
          "eventType": 301
        }
      ]
    },
    "AbilitiesMenu": {
      "highlight": "",
      "title": "",
      "source": "",
      "rowCommand": "",
      "entries": [
        {
          "name": "Strength",
          "tag": "GUI_SCREEN_ABILITIES_LEARN_STRENGTH",
          "source": "abilities:Strength",
          "icon": "Art\\GUI\\WheelIcons\\Abilities\\icon_strength.tga",
          "eventType": 301
        },
        {
          "name": "Skill",
          "tag": "GUI_SCREEN_ABILITIES_LEARN_SKILL",
          "source": "abilities:Skill",
          "icon": "Art\\GUI\\WheelIcons\\Abilities\\icon_strength.tga",
          "eventType": 301
        },
        {
          "name": "Will",
          "tag": "GUI_SCREEN_ABILITIES_LEARN_WILL",
          "source": "abilities:Will",
          "icon": "Art\\GUI\\WheelIcons\\Abilities\\icon_strength.tga",
          "eventType": 301
        },
        {
          "name": "SpellAssigning",
          "tag": "GUI_SCREEN_ABILITIES_SPELL_ASSIGNER",
          "icon": "Art\\GUI\\WheelIcons\\Abilities\\icon_strength.tga",
          "eventType": 23
        }
      ]
    },
    "EquipmentMenu": {
      "highlight": "",
      "title": "",
      "source": "",
      "rowCommand": "",
      "entries": [
        {
          "name": "Suits",
          "tag": "GUI_SCREEN_INVENTORY_CATEGORIES_SUITS",
          "source": "inventory:wear:25",
          "icon": "Art\\GUI\\WheelIcons\\icon_world_map.tga",
          "eventType": 301,
          "counted": false
        },
        {
          "name": "Hats",
          "tag": "GUI_SCREEN_INVENTORY_CATEGORIES_HATS",
          "source": "inventory:wear:17",
          "icon": "Art\\GUI\\WheelIcons\\icon_world_map.tga",
          "eventType": 301
        },
        {
          "name": "Coats",
          "tag": "GUI_SCREEN_INVENTORY_CATEGORIES_COATS",
          "source": "inventory:wear:18",
          "icon": "Art\\GUI\\WheelIcons\\icon_world_map.tga",
          "eventType": 301
        },
        {
          "name": "Shirts",
          "tag": "GUI_SCREEN_INVENTORY_CATEGORIES_SHIRTS",
          "source": "inventory:wear:19",
          "icon": "Art\\GUI\\WheelIcons\\icon_world_map.tga",
          "eventType": 301
        },
        {
          "name": "Gloves",
          "tag": "GUI_SCREEN_INVENTORY_CATEGORIES_GLOVES",
          "source": "inventory:wear:20",
          "icon": "Art\\GUI\\WheelIcons\\icon_world_map.tga",
          "eventType": 301
        },
        {
          "name": "Pants",
          "tag": "GUI_SCREEN_INVENTORY_CATEGORIES_PANTS",
          "source": "inventory:wear:21",
          "icon": "Art\\GUI\\WheelIcons\\icon_world_map.tga",
          "eventType": 301
        },
        {
          "name": "Shoes",
          "tag": "GUI_SCREEN_INVENTORY_CATEGORIES_SHOES",
          "source": "inventory:wear:22",
          "icon": "Art\\GUI\\WheelIcons\\icon_world_map.tga",
          "eventType": 301
        },
        {
          "name": "Accessories",
          "tag": "GUI_SCREEN_INVENTORY_CATEGORIES_MASKS",
          "source": "inventory:wear:23,24",
          "icon": "Art\\GUI\\WheelIcons\\icon_world_map.tga",
          "eventType": 301
        }
      ]
    },
    "GiftGivingMenu": {
      "highlight": "",
      "title": "",
      "source": "inventory:gift:",
      "rowCommand": "",
      "entries": [
        {
          "name": "Gold",
          "tag": "GUI_SCREEN_GIFT_GOLD",
          "when": "has-gold",
          "icon": "Art\\GUI\\WheelIcons\\icon_gold_coin.tga",
          "eventType": 7
        }
      ]
    },
    "ShopMenu": {
      "highlight": "",
      "title": "",
      "source": "",
      "rowCommand": "",
      "entries": [
        {
          "name": "BuyItems",
          "tag": "GUI_SCREEN_SHOP_BUY_ITEMS",
          "source": "shop:BuyItems",
          "icon": "Art\\GUI\\WheelIcons\\icon_items.tga",
          "eventType": 301
        },
        {
          "name": "SellItems",
          "tag": "GUI_SCREEN_SHOP_SELL_ITEMS",
          "source": "shop:SellItems",
          "icon": "Art\\GUI\\WheelIcons\\icon_items.tga",
          "eventType": 301
        },
        {
          "name": "BuyWeapons",
          "tag": "GUI_SCREEN_SHOP_BUY_WEAPONS",
          "source": "shop:BuyWeapons",
          "icon": "Art\\GUI\\WheelIcons\\icon_items.tga",
          "eventType": 301
        },
        {
          "name": "SellWeapons",
          "tag": "GUI_SCREEN_SHOP_SELL_WEAPONS",
          "source": "shop:SellWeapons",
          "icon": "Art\\GUI\\WheelIcons\\icon_items.tga",
          "eventType": 301
        },
        {
          "name": "BuyClothing",
          "tag": "GUI_SCREEN_SHOP_BUY_CLOTHING",
          "source": "shop:BuyClothing",
          "icon": "Art\\GUI\\WheelIcons\\icon_items.tga",
          "eventType": 301
        },
        {
          "name": "SellClothing",
          "tag": "GUI_SCREEN_SHOP_SELL_CLOTHING",
          "source": "shop:SellClothing",
          "icon": "Art\\GUI\\WheelIcons\\icon_items.tga",
          "eventType": 301
        }
      ]
    },
    "WorldMapMenu": {
      "highlight": "",
      "title": "",
      "source": "",
      "rowCommand": "",
      "entries": [
        {
          "name": "Quests",
          "tag": "GUI_SCREEN_WORLD_MAP_QUESTS",
          "source": "quests",
          "icon": "Art\\GUI\\WheelIcons\\icon_logbook.tga",
          "eventType": 301
        },
        {
          "name": "Jobs",
          "tag": "GUI_SCREEN_WORLD_MAP_JOBS",
          "source": "jobs",
          "icon": "Art\\GUI\\WheelIcons\\icon_logbook.tga",
          "eventType": 301
        },
        {
          "name": "Sales",
          "tag": "GUI_SCREEN_WORLD_MAP_SALES",
          "source": "sales",
          "icon": "Art\\GUI\\WheelIcons\\icon_logbook.tga",
          "eventType": 301
        },
        {
          "name": "Family",
          "tag": "GUI_SCREEN_WORLD_MAP_FAMILY",
          "source": "family",
          "icon": "Art\\GUI\\WheelIcons\\icon_logbook.tga",
          "eventType": 301
        },
        {
          "name": "Regions",
          "tag": "GUI_SCREEN_WORLD_MAP_REGIONS",
          "source": "regions",
          "icon": "Art\\GUI\\WheelIcons\\icon_logbook.tga",
          "eventType": 301
        },
        {
          "name": "Properties",
          "tag": "Properties",
          "source": "properties",
          "icon": "Art\\GUI\\WheelIcons\\icon_logbook.tga",
          "eventType": 301
        }
      ]
    },
    "ItemsMenu": {
      "highlight": "",
      "title": "",
      "source": "",
      "rowCommand": "",
      "entries": [
        {
          "name": "KeyItems",
          "tag": "GUI_SCREEN_INVENTORY_CATEGORIES_TOOLS",
          "source": "inventory:use:2,9,10,11,12,13,14,26",
          "icon": "Art\\GUI\\WheelIcons\\icon_world_map.tga",
          "eventType": 301
        },
        {
          "name": "Books",
          "tag": "GUI_SCREEN_INVENTORY_CATEGORIES_BOOKS",
          "source": "inventory:use:16",
          "icon": "Art\\GUI\\WheelIcons\\icon_world_map.tga",
          "eventType": 301
        },
        {
          "name": "Food and Drink",
          "tag": "GUI_SCREEN_INVENTORY_CATEGORIES_FOOD",
          "source": "inventory:use:5",
          "icon": "Art\\GUI\\WheelIcons\\icon_world_map.tga",
          "eventType": 301
        },
        {
          "name": "Potions",
          "tag": "GUI_SCREEN_INVENTORY_CATEGORIES_POTIONS",
          "source": "inventory:use:6",
          "icon": "Art\\GUI\\WheelIcons\\icon_world_map.tga",
          "eventType": 301
        },
        {
          "name": "Gifts",
          "tag": "GUI_SCREEN_INVENTORY_CATEGORIES_GIFTS",
          "source": "inventory:use:3",
          "icon": "Art\\GUI\\WheelIcons\\icon_world_map.tga",
          "eventType": 301
        },
        {
          "name": "Trophies",
          "tag": "GUI_SCREEN_INVENTORY_CATEGORIES_TROPHIES",
          "source": "inventory:use:4",
          "icon": "Art\\GUI\\WheelIcons\\icon_world_map.tga",
          "eventType": 301
        },
        {
          "name": "Augmentations",
          "tag": "GUI_SCREEN_INVENTORY_CATEGORIES_AUGMENTATIONS",
          "source": "inventory:augmentselect:7",
          "icon": "Art\\GUI\\WheelIcons\\icon_world_map.tga",
          "eventType": 301
        },
        {
          "name": "Dyes",
          "tag": "GUI_SCREEN_INVENTORY_CATEGORIES_DYES",
          "source": "inventory:dyeselect:8",
          "icon": "Art\\GUI\\WheelIcons\\icon_world_map.tga",
          "eventType": 301
        },
        {
          "name": "Furniture",
          "tag": "GUI_SCREEN_INVENTORY_CATEGORIES_FURNITURE",
          "source": "inventory:use:15",
          "icon": "Art\\GUI\\WheelIcons\\icon_world_map.tga",
          "eventType": 301
        }
      ]
    },
    "WeaponsMenu": {
      "highlight": "",
      "title": "",
      "source": "",
      "rowCommand": "",
      "entries": [
        {
          "name": "Melee",
          "tag": "GUI_SCREEN_INVENTORY_CATEGORIES_MELEE_WEAPONS",
          "source": "weapons:melee",
          "icon": "Art\\GUI\\WheelIcons\\icon_world_map.tga",
          "eventType": 301
        },
        {
          "name": "Ranged",
          "tag": "GUI_SCREEN_INVENTORY_CATEGORIES_RANGED_WEAPONS",
          "source": "weapons:ranged",
          "icon": "Art\\GUI\\WheelIcons\\icon_world_map.tga",
          "eventType": 301
        }
      ]
    },
    "RealEstateMenu": {
      "highlight": "",
      "title": "",
      "source": "",
      "rowCommand": "",
      "entries": [
        {
          "name": "GUI_SCREEN_REAL_ESTATE_BUY",
          "tag": "GUI_SCREEN_REAL_ESTATE_BUY",
          "when": "estate.known,!estate.owned",
          "eventType": 111
        },
        {
          "name": "GUI_SCREEN_REAL_ESTATE_SELL",
          "tag": "GUI_SCREEN_REAL_ESTATE_SELL",
          "when": "estate.owned",
          "eventType": 110
        },
        {
          "name": "GUI_SCREEN_REAL_ESTATE_RENT_OUT",
          "tag": "GUI_SCREEN_REAL_ESTATE_RENT_OUT",
          "when": "estate.owned,estate.home,estate.vacant",
          "eventType": 112
        },
        {
          "name": "GUI_SCREEN_REAL_ESTATE_EVICT_TENNANT",
          "tag": "GUI_SCREEN_REAL_ESTATE_EVICT_TENNANT",
          "when": "estate.owned,estate.tenanted",
          "eventType": 119
        },
        {
          "name": "GUI_SCREEN_REAL_ESTATE_ADJUST_RENT",
          "tag": "GUI_SCREEN_REAL_ESTATE_ADJUST_RENT",
          "when": "estate.owned,estate.tenanted",
          "eventType": 117
        },
        {
          "name": "GUI_SCREEN_REAL_ESTATE_ADJUST_PROFITS",
          "tag": "GUI_SCREEN_REAL_ESTATE_ADJUST_PROFITS",
          "when": "estate.owned,estate.business",
          "eventType": 118
        },
        {
          "name": "GUI_SCREEN_REAL_ESTATE_SET_BUDGET",
          "tag": "GUI_SCREEN_REAL_ESTATE_SET_BUDGET",
          "when": "estate.owned,estate.home,estate.vacant",
          "eventType": 115
        }
      ]
    },
    "pause-root": {
      "highlight": "Map",
      "title": "",
      "source": "",
      "rowCommand": "",
      "entries": [
        {
          "name": "Saving",
          "tag": "GUI_SCREEN_PAUSE_SAVING",
          "when": "!player-two",
          "children": [
            {
              "name": "SaveAndContinue",
              "tag": "GUI_SCREEN_PAUSE_SAVE_AND_CONTINUE",
              "command": "PauseSaving:0"
            },
            {
              "name": "SaveAndQuit",
              "tag": "Save and Quit to Title",
              "command": "PauseSaving:1"
            },
            {
              "name": "SaveAndQuitToDesktop",
              "tag": "Save and Quit to Desktop",
              "command": "PauseQuit:1",
              "hostOnly": true
            },
            {
              "name": "QuitDontSave",
              "tag": "Quit to Title and Don't Save",
              "command": "PauseSaving:2"
            },
            {
              "name": "QuitToDesktopDontSave",
              "tag": "Quit to Desktop and Don't Save",
              "command": "PauseQuit:2",
              "hostOnly": true
            }
          ]
        },
        {
          "name": "Map",
          "tag": "GUI_SCREEN_PAUSE_WORLD_MAP",
          "command": "PauseTab:2",
          "source": "spawn",
          "when": "!player-two",
          "eventType": 300
        },
        {
          "name": "Map",
          "tag": "GUI_SCREEN_PAUSE_WORLD_MAP",
          "command": "PauseTab:2",
          "when": "player-two",
          "hostOnly": true
        },
        {
          "name": "Abilities",
          "tag": "GUI_SCREEN_PAUSE_ABILITIES",
          "command": "PauseTab:3",
          "source": "spawn",
          "when": "!player-two",
          "eventType": 300
        },
        {
          "name": "Abilities",
          "tag": "GUI_SCREEN_PAUSE_ABILITIES",
          "command": "PauseTab:3",
          "when": "player-two",
          "hostOnly": true
        },
        {
          "name": "Items",
          "tag": "GUI_SCREEN_PAUSE_ITEMS",
          "command": "PauseTab:4",
          "source": "spawn",
          "when": "!player-two",
          "eventType": 300
        },
        {
          "name": "Items",
          "tag": "GUI_SCREEN_PAUSE_ITEMS",
          "command": "PauseTab:4",
          "when": "player-two",
          "hostOnly": true
        },
        {
          "name": "Weapons",
          "tag": "GUI_SCREEN_PAUSE_WEAPONS",
          "command": "PauseTab:5",
          "source": "spawn",
          "when": "!player-two",
          "eventType": 300
        },
        {
          "name": "Weapons",
          "tag": "GUI_SCREEN_PAUSE_WEAPONS",
          "command": "PauseTab:5",
          "when": "player-two",
          "hostOnly": true
        },
        {
          "name": "Clothing",
          "tag": "GUI_SCREEN_PAUSE_CLOTHING",
          "command": "PauseTab:6",
          "source": "spawn",
          "when": "!player-two",
          "eventType": 300
        },
        {
          "name": "Clothing",
          "tag": "GUI_SCREEN_PAUSE_CLOTHING",
          "command": "PauseTab:6",
          "when": "player-two",
          "hostOnly": true
        },
        {
          "name": "Logbook",
          "tag": "GUI_SCREEN_PAUSE_LOGBOOK",
          "command": "PauseTab:7",
          "source": "spawn",
          "when": "!player-two",
          "eventType": 300
        },
        {
          "name": "Logbook",
          "tag": "GUI_SCREEN_PAUSE_LOGBOOK",
          "command": "PauseTab:7",
          "when": "player-two",
          "hostOnly": true
        },
        {
          "name": "Achievements",
          "tag": "Achievements",
          "command": "Screen:open:1013",
          "hostOnly": true
        },
        {
          "name": "Multiplayer",
          "tag": "Multiplayer",
          "command": "Screen:open:1014",
          "hostOnly": true
        },
        {
          "name": "Options",
          "tag": "GUI_SCREEN_PAUSE_OPTIONS",
          "command": "PauseTab:8",
          "source": "spawn",
          "eventType": 300
        },
        {
          "name": "Kick",
          "tag": "Kick",
          "when": "!player-two",
          "hostOnly": true,
          "children": [
            {
              "name": "KickConfirm",
              "tag": "Kick the other Hero out of your game (they lose unsaved progress)",
              "command": "PauseCoop:kick",
              "hostOnly": true
            }
          ]
        },
        {
          "name": "HenchmanQuit",
          "tag": "Leave or Quit",
          "when": "player-two",
          "hostOnly": true,
          "children": [
            {
              "name": "LeaveGame",
              "tag": "Leave this co-op game (your Hero is saved)",
              "command": "PauseCoop:leave",
              "hostOnly": true
            },
            {
              "name": "QuitDontSave",
              "tag": "Quit to Title",
              "command": "PauseSaving:2"
            },
            {
              "name": "QuitToDesktopDontSave",
              "tag": "Quit to Desktop",
              "command": "PauseQuit:2",
              "hostOnly": true
            }
          ]
        }
      ]
    },
    "multiplayer-root": {
      "highlight": "",
      "title": "",
      "source": "",
      "rowCommand": "",
      "entries": [
        {
          "name": "Hosting",
          "tag": "My game",
          "source": "hosting",
          "hostOnly": true
        },
        {
          "name": "AddFriend",
          "tag": "Add a friend (paste code)",
          "command": "Net:paste",
          "hostOnly": true
        },
        {
          "name": "MyCode",
          "tag": "My friend code",
          "command": "Net:code",
          "hostOnly": true
        },
        {
          "name": "Friends",
          "tag": "Friends",
          "source": "friends",
          "hostOnly": true
        },
        {
          "name": "Friend Requests",
          "tag": "Friend Requests",
          "source": "requests",
          "hostOnly": true
        },
        {
          "name": "Invitations",
          "tag": "Invitations",
          "source": "invites",
          "hostOnly": true
        },
        {
          "name": "Games",
          "tag": "Games",
          "source": "games",
          "hostOnly": true
        }
      ]
    },
    "mods-root": {
      "highlight": "",
      "title": "Active Mods",
      "source": "mods",
      "rowCommand": "",
      "entries": []
    },
    "hostbox-root": {
      "highlight": "",
      "title": "",
      "source": "",
      "rowCommand": "",
      "entries": [
        {
          "name": "Continue",
          "tag": "Continue",
          "command": "HostBox:accept",
          "hostOnly": true
        },
        {
          "name": "Back",
          "tag": "Back",
          "command": "HostBox:cancel",
          "hostOnly": true
        }
      ]
    },
    "achievements-root": {
      "highlight": "Achievements",
      "title": "",
      "source": "achievements",
      "rowCommand": "",
      "entries": [
        {
          "name": "Achievements",
          "tag": "Achievements",
          "hostOnly": true,
          "eventType": 150
        }
      ]
    },
    "frontend-root": {
      "highlight": "",
      "title": "",
      "source": "",
      "rowCommand": "",
      "entries": [
        {
          "name": "New Game",
          "tag": "GUI_SCREEN_FRONT_END_NEW_GAME",
          "opens": "HeroCard"
        },
        {
          "name": "Load Game",
          "tag": "GUI_SCREEN_FRONT_END_LOAD_GAME"
        },
        {
          "name": "Downloadable Content",
          "tag": "GUI_DOWNLOADABLE_CONTENT",
          "hostOnly": true,
          "children": [
            {
              "name": "Mods",
              "tag": "Active Mods",
              "command": "Net:mods",
              "hostOnly": true
            }
          ]
        },
        {
          "name": "Multiplayer",
          "tag": "Multiplayer",
          "command": "Net:book",
          "hostOnly": true
        },
        {
          "name": "Language",
          "tag": "GUI_SCREEN_FRONT_END_LANGUAGE"
        },
        {
          "name": "Subtitles",
          "tag": "GUI_SCREEN_OPTIONS_SUBTITLES",
          "children": [
            {
              "name": "SubtitlesOn",
              "tag": "GUI_CONTROLS_ON"
            },
            {
              "name": "SubtitlesOff",
              "tag": "GUI_CONTROLS_OFF"
            }
          ]
        },
        {
          "name": "QuitToDesktop",
          "tag": "Quit to Desktop",
          "command": "QuitToDesktop",
          "hostOnly": true
        }
      ]
    }
  },
  "screens": {
    "0": {
      "name": "pause",
      "restFromFile": true,
      "menu": "pause-root",
      "scrollContent": "Foreground.Pages.Contents.MiniMap.Legend",
      "scrollMask": "Mask",
      "scrollBar": "Foreground.Pages.Contents.Scrollbar",
      "flowMeasured": "Foreground.Pages.Contents.MiniMap.Legend.CurrentObjective",
      "flowBase": "Foreground.Pages.Contents.MiniMap.Legend.Fixed",
      "flowMoved": [
        "Foreground.Pages.Contents.MiniMap.Legend.Fixed"
      ],
      "flowWhenDrawn": true,
      "presentation": "pausemainmenu",
      "pageRootUnit": "Pages",
      "backdropUnit": "BackgroundTint",
      "listLift": 0,
      "chrome": [
        "Foreground"
      ],
      "ownedElsewhere": [
        "ExpandableMenu.Buttons",
        "MenuHighlight"
      ],
      "wideLeft": [
        "Foreground.ExpandableMenu"
      ],
      "wideRight": [
        "Foreground.RightFrame",
        "Foreground.Pages",
        "Foreground.ReusableTokens"
      ],
      "restPose": {
        "Foreground.ExpandableMenu.KeyLight": [
          22
        ]
      }
    },
    "14": {
      "name": "abilities",
      "ownsList": true,
      "restFromFile": true,
      "purse": true,
      "provider": "abilities",
      "hasSpellBlade": true,
      "scrollContent": "Foreground.Book.Pages.Page2.Contents.Text",
      "scrollMask": "Mask",
      "scrollBar": "Foreground.Book.Pages.Page2.Contents.Scrollbar",
      "presentation": "abilitiesscreen",
      "pageRootUnit": "Pages",
      "backdropUnit": "background_red",
      "chrome": [
        "Foreground"
      ],
      "ownedElsewhere": [
        "ExpandableMenu.Buttons",
        "MenuHighlight"
      ],
      "wideLeft": [
        "Foreground.ExpandableMenu"
      ],
      "wideRight": [
        "Foreground.Book"
      ],
      "pages": [
        "Foreground.Book.Pages.Page1",
        "Foreground.Book.Pages.Page2",
        "Foreground.Book.Pages.Page4"
      ],
      "pageStackAt": [
        0,
        0,
        0
      ],
      "restPose": {
        "Foreground.ExpandableMenu.KeyLight": [
          22
        ]
      }
    },
    "15": {
      "name": "items",
      "ownsList": true,
      "restFromFile": true,
      "purse": true,
      "menuScript": "ItemsMenu",
      "menuScriptByMode": {
        "1": "ItemsMenu",
        "3": "WeaponsMenu"
      },
      "previewAnchor": "Foreground.Book.Pages.Page1.Contents.ModelHider.model_proxy.model",
      "provider": "item-book",
      "scrollContent": "Foreground.Book.Pages.Page2.Contents.ItemDescription",
      "scrollMask": "Mask",
      "scrollBar": "Foreground.Book.Pages.Page2.Contents.Scrollbar",
      "presentation": "giftgivingscreen",
      "pageRootUnit": "Pages",
      "backdropUnit": "background_green",
      "chrome": [
        "Foreground"
      ],
      "ownedElsewhere": [
        "ExpandableMenu.Buttons",
        "MenuHighlight"
      ],
      "wideLeft": [
        "Foreground.ExpandableMenu"
      ],
      "wideRight": [
        "Foreground.Book"
      ],
      "pages": [
        "Foreground.Book.Pages.Page1",
        "Foreground.Book.Pages.Page2"
      ],
      "pageStackAt": [
        0,
        0,
        0
      ],
      "restPose": {
        "Foreground.ExpandableMenu.KeyLight": [
          22
        ],
        "Foreground.Book.Pages.Page1_Background": [
          66
        ]
      },
      "restOpacity": {
        "Foreground.ExpandableMenu.Title": 0,
        "Foreground.Book.Pages.Page1.Contents.DyeSwatch": 0
      }
    },
    "16": {
      "name": "clothing",
      "ownsList": true,
      "restFromFile": true,
      "purse": true,
      "previewAnchor": "Foreground.Book.Pages.Page1.Contents.TypeSpecific.model_proxy.model",
      "provider": "item-book",
      "scrollContent": "Foreground.Book.Pages.Page2.Contents.ItemDescription",
      "scrollMask": "Mask",
      "scrollBar": "Foreground.Book.Pages.Page2.Contents.Scrollbar",
      "presentation": "equipmentscreen",
      "pageRootUnit": "Pages",
      "backdropUnit": "background_green",
      "chrome": [
        "Foreground"
      ],
      "ownedElsewhere": [
        "ExpandableMenu.Buttons",
        "MenuHighlight"
      ],
      "wideLeft": [
        "Foreground.ExpandableMenu"
      ],
      "wideRight": [
        "Foreground.Book"
      ],
      "pages": [
        "Foreground.Book.Pages.Page1",
        "Foreground.Book.Pages.Page2"
      ],
      "pageStackAt": [
        0,
        0,
        0
      ],
      "restPose": {
        "Foreground.ExpandableMenu.KeyLight": [
          22
        ],
        "Foreground.Book.Pages.Page1.Contents.TypeSpecific.model_proxy": [
          20
        ]
      },
      "restOpacity": {
        "Foreground.Book.Pages.Page1.Contents.DyeSwatch": 0
      }
    },
    "25": {
      "name": "gift",
      "ownsList": true,
      "restFromFile": true,
      "purse": true,
      "menuScript": "GiftGivingMenu",
      "previewAnchor": "Foreground.Book.Pages.Page1.Contents.ModelHider.model_proxy.model",
      "provider": "item-book",
      "scrollContent": "Foreground.Book.Pages.Page2.Contents.ItemDescription",
      "scrollMask": "Mask",
      "scrollBar": "Foreground.Book.Pages.Page2.Contents.Scrollbar",
      "presentation": "giftgivingscreen",
      "pageRootUnit": "Pages",
      "backdropUnit": "background_green",
      "chrome": [
        "Foreground"
      ],
      "ownedElsewhere": [
        "ExpandableMenu.Buttons",
        "MenuHighlight"
      ],
      "wideLeft": [
        "Foreground.ExpandableMenu"
      ],
      "wideRight": [
        "Foreground.Book"
      ],
      "pages": [
        "Foreground.Book.Pages.Page1",
        "Foreground.Book.Pages.Page2"
      ],
      "pageStackAt": [
        0,
        0,
        0
      ],
      "restPose": {
        "Foreground.ExpandableMenu.KeyLight": [
          22
        ]
      },
      "restOpacity": {
        "Foreground.Book.Pages.Page1.Contents.DyeSwatch": 0
      }
    },
    "26": {
      "name": "upgradefurniture",
      "ownsList": true,
      "restFromFile": true,
      "purse": true,
      "rowAction": "Furniture:upgrade:{id}",
      "previewAnchor": "Foreground.Book.Pages.Page1.Contents.ModelHider.model_proxy.model",
      "provider": "item-book",
      "presentation": "upgradefurniture",
      "pageRootUnit": "Pages",
      "backdropUnit": "background_grey",
      "chrome": [
        "Foreground"
      ],
      "ownedElsewhere": [
        "ExpandableMenu.Buttons",
        "MenuHighlight"
      ],
      "wideLeft": [
        "Foreground.ExpandableMenu"
      ],
      "wideRight": [
        "Foreground.Book"
      ],
      "pages": [
        "Foreground.Book.Pages.Page1",
        "Foreground.Book.Pages.Page2"
      ],
      "pageStackAt": [
        0,
        0,
        0
      ],
      "restPose": {
        "Foreground.ExpandableMenu.KeyLight": [
          22
        ]
      },
      "restOpacity": {
        "Foreground.Book.Pages.Page1.Contents.DyeSwatch": 0
      }
    },
    "27": {
      "name": "decoration",
      "ownsList": true,
      "restFromFile": true,
      "purse": true,
      "commitsByHighlight": true,
      "previewAnchor": "Foreground.Book.Pages.Page1.Contents.ModelHider.model_proxy.model",
      "provider": "item-book",
      "flipAction": "screen:unlearn",
      "scrollContent": "Foreground.Book.Pages.Page2.Contents.ItemDescription",
      "scrollMask": "Mask",
      "scrollBar": "Foreground.Book.Pages.Page2.Contents.Scrollbar",
      "presentation": "giftgivingscreen",
      "pageRootUnit": "Pages",
      "backdropUnit": "background_green",
      "chrome": [
        "Foreground"
      ],
      "ownedElsewhere": [
        "ExpandableMenu.Buttons",
        "MenuHighlight"
      ],
      "wideLeft": [
        "Foreground.ExpandableMenu"
      ],
      "wideRight": [
        "Foreground.Book"
      ],
      "pages": [
        "Foreground.Book.Pages.Page1",
        "Foreground.Book.Pages.Page2"
      ],
      "pageStackAt": [
        0,
        0,
        0
      ],
      "restPose": {
        "Foreground.ExpandableMenu.KeyLight": [
          22
        ]
      },
      "restOpacity": {
        "Foreground.Book.Pages.Page1.Contents.DyeSwatch": 0
      }
    },
    "17": {
      "name": "logbook",
      "ownsList": true,
      "restFromFile": true,
      "purse": true,
      "provider": "logbook",
      "scrollContent": "Foreground.Book.Pages.StatsPage.Contents.LogBook.Stats|Foreground.Book.Pages.RenownPage.Contents.LogBook.Renown|Foreground.Book.Pages.ExpressionsPage.Contents|Foreground.Book.Pages.CompletedQuestsPage.Contents.Quests|Foreground.Book.Pages.HelpPage.Contents.LogBook",
      "scrollMask": "Mask|Mask|Mask|Mask|Mask",
      "scrollBar": "Foreground.Book.Pages.StatsPage.Contents.LogBook.Scrollbar|Foreground.Book.Pages.RenownPage.Contents.LogBook.Scrollbar|Foreground.Book.Pages.ExpressionsPage.Scrollbar|Foreground.Book.Pages.CompletedQuestsPage.Contents.Scrollbar|Foreground.Book.Pages.HelpPage.Contents.Scrollbar",
      "flowMeasured": "Foreground.Book.Pages.ExpressionsPage.Contents.Description",
      "flowBase": "Foreground.Book.Pages.ExpressionsPage.Contents.Effects",
      "flowMoved": [
        "Foreground.Book.Pages.ExpressionsPage.Contents.Effects"
      ],
      "presentation": "logbookscreen",
      "pageRootUnit": "Pages",
      "backdropUnit": "background_yellow",
      "chrome": [
        "Foreground"
      ],
      "ownedElsewhere": [
        "ExpandableMenu.Buttons",
        "MenuHighlight"
      ],
      "wideLeft": [
        "Foreground.ExpandableMenu"
      ],
      "wideRight": [
        "Foreground.Book",
        "Foreground.Mask"
      ],
      "statFields": [
        {
          "id": 2,
          "path": "combat.DecapitationValue"
        },
        {
          "id": 3,
          "path": "combat.GroinshotsValue"
        },
        {
          "id": 4,
          "path": "combat.EnemiesDisarmedValue"
        },
        {
          "id": 5,
          "path": "combat.ReversalsValue"
        },
        {
          "id": 6,
          "path": "combat.DamageDealtValue"
        },
        {
          "id": 7,
          "path": "combat.TotalManaSpentValue"
        },
        {
          "id": 8,
          "path": "combat.DamageTakenValue"
        },
        {
          "id": 9,
          "path": "combat.DeathsValue"
        },
        {
          "id": 11,
          "path": "combat.ResurrectionPhialsValue"
        },
        {
          "id": 13,
          "path": "combat.HitsTakenValue"
        },
        {
          "id": 14,
          "path": "combat.AttacksBlockedValue"
        },
        {
          "id": 15,
          "path": "combat.FlourishesDoneValue"
        },
        {
          "id": 16,
          "path": "combat.CoopStrikesValue"
        },
        {
          "id": 109,
          "path": "kills.TotalValue"
        },
        {
          "id": 110,
          "path": "kills.BansheesKilledValue"
        },
        {
          "id": 111,
          "path": "kills.BalverinesKilledValue"
        },
        {
          "id": 112,
          "path": "kills.BanditsKilledValue"
        },
        {
          "id": 113,
          "path": "kills.BeetlesKilledValue"
        },
        {
          "id": 114,
          "path": "kills.TrollsKilledValue"
        },
        {
          "id": 115,
          "path": "kills.ShardsKilledValue"
        },
        {
          "id": 116,
          "path": "kills.ShadowsKilledValue"
        },
        {
          "id": 117,
          "path": "kills.HollowmenKilledValue"
        },
        {
          "id": 118,
          "path": "kills.GuardsKilledValue"
        },
        {
          "id": 119,
          "path": "kills.LucienGuardsKilledValue"
        },
        {
          "id": 120,
          "path": "kills.LucienSoldiersKilledValue"
        },
        {
          "id": 121,
          "path": "kills.HobbesKilledValue"
        },
        {
          "id": 122,
          "path": "kills.VillagersKilledValue"
        },
        {
          "id": 123,
          "path": "kills.HighwayMenKilledValue"
        },
        {
          "id": 17,
          "path": "money.MoneyPropertiesOwnedValue"
        },
        {
          "id": 18,
          "path": "money.MoneyEarnedValue"
        },
        {
          "id": 19,
          "path": "money.MoneySpentValue"
        },
        {
          "id": 20,
          "path": "money.MoneyRightNowValue"
        },
        {
          "id": 22,
          "path": "money.MoneyMadeMageDiceValue"
        },
        {
          "id": 23,
          "path": "money.MoneyMadeBartendingValue"
        },
        {
          "id": 24,
          "path": "money.MoneyMadeWoodchoppingValue"
        },
        {
          "id": 25,
          "path": "money.MoneyMadeBlacksmithingValue"
        },
        {
          "id": 27,
          "path": "money.MoneyMadeKeystoneValue"
        },
        {
          "id": 28,
          "path": "money.MoneyMadeWizardsTowerValue"
        },
        {
          "id": 29,
          "path": "money.MoneyMadeRealEstateValue"
        },
        {
          "id": 30,
          "path": "money.MoneyMadeAssasinationsValue"
        },
        {
          "id": 31,
          "path": "money.MoneyMadeBountyHuntsValue"
        },
        {
          "id": 32,
          "path": "money.MoneyMadeSlaveryValue"
        },
        {
          "id": 33,
          "path": "adventure.TotalRenownValue"
        },
        {
          "id": 106,
          "path": "adventure.MostTrophyRenownValue"
        },
        {
          "id": 35,
          "path": "adventure.GoldDonatedToTempleValue"
        },
        {
          "id": 36,
          "path": "adventure.PeopleSacrificedToEvilValue"
        },
        {
          "id": 37,
          "path": "adventure.AssassinationsCompletedValue"
        },
        {
          "id": 38,
          "path": "adventure.BountiesCollectedValue"
        },
        {
          "id": 39,
          "path": "adventure.SlavesRescuedValue"
        },
        {
          "id": 40,
          "path": "adventure.PeoplePutIntoSlaveryValue"
        },
        {
          "id": 41,
          "path": "adventure.MostGoodPointsValue"
        },
        {
          "id": 42,
          "path": "adventure.MostEvilPointsValue"
        },
        {
          "id": 43,
          "path": "adventure.MostNobilityPointsValue"
        },
        {
          "id": 44,
          "path": "adventure.MostCorruptionPointsValue"
        },
        {
          "id": 45,
          "path": "adventure.TotalExpEarnedValue"
        },
        {
          "id": 46,
          "path": "adventure.TotalStrengthExpEarnedValue"
        },
        {
          "id": 47,
          "path": "adventure.TotalSkillExpEarnedValue"
        },
        {
          "id": 48,
          "path": "adventure.TotalWillExpEarnedValue"
        },
        {
          "id": 49,
          "path": "adventure.TotalGenearalExpEarnedValue"
        },
        {
          "id": 50,
          "path": "games.TopScoreWestcliffShootingValue"
        },
        {
          "id": 96,
          "path": "games.BiggestWinMageDiceValue"
        },
        {
          "id": 97,
          "path": "games.BiggestWinKeystoneValue"
        },
        {
          "id": 98,
          "path": "games.BiggestWinWizardsTowerValue"
        },
        {
          "id": 83,
          "path": "games.BestFullCrucibleTimeValue"
        },
        {
          "id": 54,
          "path": "relationships.NumPeopleFallenInLoveWithYouValue"
        },
        {
          "id": 55,
          "path": "relationships.TimesMarriedValue"
        },
        {
          "id": 56,
          "path": "relationships.TimesDivorcedValue"
        },
        {
          "id": 57,
          "path": "relationships.TimesWidowedValue"
        },
        {
          "id": 58,
          "path": "relationships.NumChildrenValue"
        },
        {
          "id": 59,
          "path": "relationships.MostLoveEarnedValue"
        },
        {
          "id": 60,
          "path": "relationships.MostHumourEarnedValue"
        },
        {
          "id": 61,
          "path": "relationships.MostFearEarnedValue"
        },
        {
          "id": 62,
          "path": "relationships.MostHateEarnedValue"
        },
        {
          "id": 63,
          "path": "relationships.TimesMaritalSexValue"
        },
        {
          "id": 64,
          "path": "relationships.TimesExtraMaritalSexValue"
        },
        {
          "id": 65,
          "path": "relationships.TimesPaidForSexValue"
        },
        {
          "id": 66,
          "path": "relationships.NumNpcRewardsCollectedValue"
        },
        {
          "id": 67,
          "path": "relationships.MostCoopExpressionBonusesValue"
        },
        {
          "id": 69,
          "path": "crime.NumCrimesCommitedValue"
        },
        {
          "id": 70,
          "path": "crime.CrimeFinesLeviedValue"
        },
        {
          "id": 71,
          "path": "crime.CrimeFinesPaidValue"
        },
        {
          "id": 72,
          "path": "crime.CrimeFinesPaidCommunityServiceValue"
        },
        {
          "id": 74,
          "path": "other.TotalFoodDrinkConsumedValue"
        },
        {
          "id": 75,
          "path": "other.TotalAlcoholConsumedValue"
        },
        {
          "id": 76,
          "path": "other.TotalPotionsConsumedValue"
        },
        {
          "id": 77,
          "path": "other.AmountOfTimeDrunkValue"
        },
        {
          "id": 78,
          "path": "other.TotalVomitsValue"
        },
        {
          "id": 79,
          "path": "other.TotalDistanceTraveledValue"
        },
        {
          "id": 80,
          "path": "other.NumChickensKickedValue"
        },
        {
          "id": 81,
          "path": "other.FurthestChickenKickValue"
        },
        {
          "id": 0,
          "path": "other.ExpressionsValue"
        },
        {
          "id": 84,
          "path": "other.STDsValue"
        },
        {
          "id": 1,
          "path": "other.TimePlayedValue"
        }
      ],
      "pageStackAt": [
        0,
        0,
        0
      ],
      "restPose": {
        "Foreground.ExpandableMenu.KeyLight": [
          22
        ]
      }
    },
    "18": {
      "name": "options",
      "ownsList": true,
      "restFromFile": true,
      "purse": true,
      "provider": "options",
      "fadesBottomEnd": false,
      "presentation": "optionsscreen",
      "pageRootUnit": "Pages",
      "backdropUnit": "background_purple",
      "chrome": [
        "Foreground"
      ],
      "ownedElsewhere": [
        "ExpandableMenu.Buttons",
        "MenuHighlight"
      ],
      "wideLeft": [
        "Foreground.ExpandableMenu"
      ],
      "wideRight": [
        "Foreground.Book"
      ],
      "pages": [
        "Foreground.Book.Pages.Page1",
        "Foreground.Book.Pages.Page2",
        "Foreground.Book.Pages.Page3",
        "Foreground.Book.Pages.Page4"
      ],
      "pageStackAt": [
        0,
        0,
        0
      ],
      "restPose": {
        "Foreground.ExpandableMenu.KeyLight": [
          22
        ]
      }
    },
    "21": {
      "name": "crime",
      "ownsList": true,
      "restFromFile": true,
      "purse": true,
      "provider": "crime",
      "scrollContent": "Foreground.Book.Pages.Page1.Contents.Text|Foreground.Book.Pages.Page2.Contents.Text|Foreground.Book.Pages.Page3.Contents.Text",
      "scrollMask": "Mask|Mask|Mask",
      "scrollBar": "Foreground.Book.Pages.Page1.Contents.Scrollbar|Foreground.Book.Pages.Page2.Contents.Scrollbar|Foreground.Book.Pages.Page3.Contents.Scrollbar",
      "presentation": "crimescreen",
      "pageRootUnit": "Pages",
      "backdropUnit": "background_grey",
      "chrome": [
        "Foreground"
      ],
      "ownedElsewhere": [
        "ExpandableMenu.Buttons",
        "MenuHighlight"
      ],
      "wideLeft": [
        "Foreground.ExpandableMenu"
      ],
      "wideRight": [
        "Foreground.Book"
      ],
      "pages": [
        "Foreground.Book.Pages.Page1",
        "Foreground.Book.Pages.Page2",
        "Foreground.Book.Pages.Page3"
      ],
      "restPose": {
        "Foreground.ExpandableMenu.KeyLight": [
          22
        ]
      }
    },
    "22": {
      "name": "shop",
      "ownsList": true,
      "restFromFile": true,
      "purse": true,
      "menuScript": "ShopMenu",
      "previewAnchor": "Foreground.Book.Pages.Page1.Contents.ModelHider.model_proxy.model",
      "provider": "item-book",
      "scrollContent": "Foreground.Book.Pages.Page2.Contents.Text.Group|Foreground.Book.Pages.Page3.Contents.Group",
      "scrollMask": "Mask|Mask",
      "scrollBar": "Foreground.Book.Pages.Page2.Contents.Scrollbar|",
      "presentation": "shopscreen",
      "pageRootUnit": "Pages",
      "backdropUnit": "background_green",
      "chrome": [
        "Foreground"
      ],
      "ownedElsewhere": [
        "ExpandableMenu.Buttons",
        "MenuHighlight"
      ],
      "wideLeft": [
        "Foreground.ExpandableMenu"
      ],
      "wideRight": [
        "Foreground.Book",
        "Foreground.FrontPage",
        "Foreground.PageTextureFar",
        "Foreground.PageTextureNear"
      ],
      "pages": [
        "Foreground.Book.Pages.Page1",
        "Foreground.Book.Pages.Page2",
        "Foreground.Book.Pages.Page3"
      ],
      "pageStackAt": [
        0,
        0,
        0
      ],
      "restPose": {
        "Foreground.ExpandableMenu.KeyLight": [
          22
        ]
      },
      "restOpacity": {
        "Foreground.Book.Pages.Page1.Contents.DyeSwatch": 0
      }
    },
    "23": {
      "name": "worldmap",
      "ownsList": true,
      "restFromFile": true,
      "purse": true,
      "provider": "worldmap",
      "scrollContent": "Foreground.Book.Pages.Page1.Contents.Text|Foreground.Book.Pages.QuestsPage.Contents.Description|Foreground.Book.Pages.FamilyPage.Contents.Group|Foreground.Book.Pages.Page2.Contents.Legend|Foreground.Book.Pages.SalesPage.Contents.Description",
      "scrollMask": "MaskPage2|MaskQuests|MaskFamily|MaskPage3|MaskPage2",
      "scrollBar": "Foreground.Book.Pages.Page1.Contents.Scrollbar|Foreground.Book.Pages.QuestsPage.Contents.Scrollbar|Foreground.Book.Pages.FamilyPage.Contents.Scrollbar|Foreground.Book.Pages.Page2.Contents.Scrollbar|Foreground.Book.Pages.SalesPage.Contents.Scrollbar",
      "flowMeasured": "Foreground.Book.Pages.Page1.Contents.Text.MerchantsList",
      "flowBase": "Foreground.Book.Pages.Page1.Contents.Text.MerchantListBase",
      "flowMoved": [
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
        "Foreground.Book.Pages.Page1.Contents.Text.Lines.line14"
      ],
      "flowTailAnchor": "Foreground.Book.Pages.Page1.Contents.Text.GargoylesDestroyedAmount",
      "flowTailMoved": [
        "Foreground.Book.Pages.Page1.Contents.Text.SilverKeysFoundAmount",
        "Foreground.Book.Pages.Page1.Contents.Text.SilverKeysFound"
      ],
      "presentation": "worldmapscreen",
      "pageRootUnit": "Pages",
      "chrome": [
        "WorldMap_Spring_A",
        "WorldMap_Spring_B",
        "WorldMap_Spring_C",
        "WorldMap_Spring_D",
        "WorldMap_Spring_C2",
        "WorldMap_Spring_A2",
        "Foreground"
      ],
      "ownedElsewhere": [
        "ExpandableMenu.Buttons",
        "MenuHighlight"
      ],
      "wideLeft": [
        "Foreground.ExpandableMenu"
      ],
      "wideRight": [
        "Foreground.Book"
      ],
      "wideGround": [
        "WorldMap_Spring_A",
        "WorldMap_Spring_B",
        "WorldMap_Spring_C",
        "WorldMap_Spring_D",
        "WorldMap_Spring_C2",
        "WorldMap_Spring_A2"
      ],
      "pages": [
        "Foreground.Book.Pages.Page1",
        "Foreground.Book.Pages.Page2"
      ],
      "pageStackAt": [
        0,
        0,
        0
      ],
      "restPose": {
        "Foreground.ExpandableMenu.KeyLight": [
          22
        ]
      },
      "restOpacity": {
        "Foreground.Book.Pages.QuestsPage.Contents.Scrollbar": 0
      }
    },
    "28": {
      "name": "realestate",
      "ownsList": true,
      "restFromFile": true,
      "purse": true,
      "provider": "realestate",
      "scrollContent": "Foreground.Book.Pages.Page1.Group|Foreground.Book.Pages.Page2.Contents.Group",
      "scrollMask": "Mask|Mask",
      "scrollBar": "Foreground.Book.Pages.Page1.Contents.Scrollbar|Foreground.Book.Pages.Page2.Contents.Scrollbar",
      "fadesTopEnd": false,
      "presentation": "realestatescreen",
      "pageRootUnit": "Pages",
      "backdropUnit": "background_grey",
      "chrome": [
        "Foreground"
      ],
      "ownedElsewhere": [
        "ExpandableMenu.Buttons",
        "MenuHighlight"
      ],
      "wideLeft": [
        "Foreground.ExpandableMenu"
      ],
      "wideRight": [
        "Foreground.Book"
      ],
      "pages": [
        "Foreground.Book.Pages.Page1",
        "Foreground.Book.Pages.Page2"
      ],
      "restPose": {
        "Foreground.ExpandableMenu.KeyLight": [
          22
        ]
      }
    },
    "1": {
      "name": "photomode",
      "presentation": "photomodescreen",
      "chrome": [
        "PhotoMode"
      ]
    },
    "2": {
      "name": "spellwheel",
      "presentation": "spellsmenu"
    },
    "24": {
      "name": "coop",
      "restFromFile": true,
      "previewAnchor": "RightPage.model_proxy.model",
      "provider": "coop",
      "presentation": "coopscreen",
      "chrome": [
        "LeftPage",
        "RightPage",
        "LeftFrame2",
        "RightFrame2",
        "LeftBackground",
        "RightBackground",
        "Connecting",
        "menu",
        "loadgame"
      ],
      "wideLeft": [
        "Foreground.LeftPage",
        "Foreground.LeftFrame2"
      ],
      "wideRight": [
        "Foreground.RightPage",
        "Foreground.RightFrame2"
      ],
      "wideStretch": [
        "Foreground.LeftBackground.PageTexture",
        "Foreground.RightBackground.PageTexture"
      ]
    },
    "5": {
      "name": "questrepeatable",
      "restFromFile": true,
      "provider": "questbox",
      "ownsRevealedInput": true,
      "revealedAccept": "box:dismiss",
      "presentation": "questrepeatable",
      "chrome": [
        "PaperBackground",
        "Buttons",
        "Stars",
        "DetailsGroup",
        "QuestTitle",
        "QuestDescription",
        "QuestDescription2",
        "RiskLevel",
        "Remember",
        "Condition1",
        "Condition2"
      ]
    },
    "29": {
      "name": "creditsscreen",
      "ownsRevealedInput": true,
      "revealedAccept": "screen:select",
      "revealedBack": "screen:back",
      "presentation": "creditsscreen"
    },
    "7": {
      "name": "displaybox",
      "restFromFile": true,
      "provider": "infocard",
      "presentation": "displaybox",
      "chrome": [
        "Paper"
      ]
    },
    "11": {
      "name": "minigamecompleted",
      "restFromFile": true,
      "provider": "minigame",
      "ownsRevealedInput": true,
      "revealedAccept": "minigame:confirm",
      "presentation": "minigamecompleted",
      "chrome": [
        "paperbox",
        "Select",
        "Motif",
        "Amounts",
        "Title"
      ]
    },
    "12": {
      "name": "questcompletion",
      "restFromFile": true,
      "ownsRevealedInput": true,
      "revealedAccept": "screen:dismiss",
      "presentation": "questcompletion",
      "chrome": [
        "Component",
        "frame2",
        "leaf",
        "Bullet1",
        "Bullet2",
        "Buttons",
        "Image",
        "QuestTitle",
        "QuestDescription",
        "Reward",
        "RenownPoints",
        "Points"
      ]
    },
    "10": {
      "name": "query",
      "restFromFile": true,
      "ownsRevealedInput": true,
      "revealedBack": "screen:back",
      "presentation": "queryscreen",
      "chrome": [
        "elements"
      ]
    },
    "1010": {
      "name": "frontend",
      "restFromFile": true,
      "presentation": "frontendmainmenu",
      "pageRootUnit": "Pages",
      "chrome": [
        "Foreground",
        "LiveCountdown",
        "LoadingAnimation"
      ],
      "ownedElsewhere": [
        "ExpandableMenu.Buttons",
        "MenuHighlight",
        "choosecard",
        "loadgame",
        "dialog"
      ],
      "wideLeft": [
        "Foreground.ExpandableMenu"
      ],
      "wideRight": [
        "Foreground.Book"
      ]
    },
    "1011": {
      "name": "title",
      "restFromFile": true,
      "presentation": "frontendstartupscreen",
      "chrome": [
        "logo",
        "White",
        "PressStart",
        "Legal"
      ],
      "ownedElsewhere": [
        "dialog"
      ]
    },
    "1012": {
      "name": "backdrop",
      "restFromFile": true,
      "presentation": "scrollingbackground",
      "pageRootUnit": "Scrolling",
      "chrome": [
        "Scrolling"
      ],
      "wideGround": [
        "Scrolling"
      ]
    },
    "1014": {
      "name": "multiplayer",
      "ownsList": true,
      "provider": "multiplayer",
      "scrollContent": "Foreground.Book.Pages.ExpressionsPage.Contents",
      "scrollMask": "Mask",
      "scrollBar": "Foreground.Book.Pages.ExpressionsPage.Scrollbar",
      "presentation": "logbookscreen",
      "pageRootUnit": "Pages",
      "backdropUnit": "background_yellow",
      "chrome": [
        "Foreground"
      ],
      "ownedElsewhere": [
        "ExpandableMenu.Buttons",
        "MenuHighlight"
      ],
      "wideLeft": [
        "Foreground.ExpandableMenu"
      ],
      "wideRight": [
        "Foreground.Book",
        "Foreground.Mask"
      ]
    },
    "1015": {
      "name": "mods",
      "ownsList": true,
      "provider": "multiplayer",
      "scrollContent": "Foreground.Book.Pages.ExpressionsPage.Contents",
      "scrollMask": "Mask",
      "scrollBar": "Foreground.Book.Pages.ExpressionsPage.Scrollbar",
      "presentation": "logbookscreen",
      "pageRootUnit": "Pages",
      "backdropUnit": "background_yellow",
      "chrome": [
        "Foreground"
      ],
      "ownedElsewhere": [
        "ExpandableMenu.Buttons",
        "MenuHighlight"
      ],
      "wideLeft": [
        "Foreground.ExpandableMenu"
      ],
      "wideRight": [
        "Foreground.Book",
        "Foreground.Mask"
      ]
    },
    "1016": {
      "name": "hostbox",
      "ownsList": true,
      "provider": "hostbox",
      "scrollContent": "Foreground.Book.Pages.ExpressionsPage.Contents",
      "scrollMask": "Mask",
      "scrollBar": "Foreground.Book.Pages.ExpressionsPage.Scrollbar",
      "presentation": "logbookscreen",
      "pageRootUnit": "Pages",
      "backdropUnit": "background_yellow",
      "chrome": [
        "Foreground"
      ],
      "ownedElsewhere": [
        "ExpandableMenu.Buttons",
        "MenuHighlight"
      ],
      "wideLeft": [
        "Foreground.ExpandableMenu"
      ],
      "wideRight": [
        "Foreground.Book",
        "Foreground.Mask"
      ]
    },
    "1013": {
      "name": "achievements",
      "ownsList": true,
      "menu": "achievements-root",
      "provider": "achievements",
      "scrollContent": "Foreground.Book.Pages.ExpressionsPage.Contents",
      "scrollMask": "Mask",
      "scrollBar": "Foreground.Book.Pages.ExpressionsPage.Scrollbar",
      "presentation": "logbookscreen",
      "pageRootUnit": "Pages",
      "backdropUnit": "background_yellow",
      "chrome": [
        "Foreground"
      ],
      "ownedElsewhere": [
        "ExpandableMenu.Buttons",
        "MenuHighlight"
      ],
      "wideLeft": [
        "Foreground.ExpandableMenu"
      ],
      "wideRight": [
        "Foreground.Book",
        "Foreground.Mask"
      ]
    },
    "8": {
      "name": "receiveitems",
      "previewAnchor": "Foreground.SpinningCube.Cube",
      "presentation": "receiveitemsscreen",
      "boxPresentation": "receiveitemsscreen",
      "boxUnits": [
        "Foreground",
        "CardBack"
      ],
      "boxPaths": {
        "NameAndIcon": "NameAndIcon",
        "Title": "Title",
        "Text": "Text",
        "IconGraphic": "IconGraphic",
        "Buttons": "Buttons",
        "Mask": "Mask",
        "NameAndIcon/IconGraphic": "NameAndIcon/IconGraphic",
        "NameAndIcon/Title": "NameAndIcon/Title",
        "NameAndIcon/Text": "NameAndIcon/Text",
        "Buttons/Buttons/": "Buttons/Buttons/",
        "PaperBackground": "PaperBackground",
        "Group": "Group",
        "Scrollbar/UpArrow": "Scrollbar/UpArrow",
        "Scrollbar/DownArrow": "Scrollbar/DownArrow",
        "Scrollbar/Icon": "Scrollbar/Icon"
      }
    },
    "1020": {
      "name": "messagebox",
      "boxPresentation": "messageboxmulti",
      "boxUnits": [
        "Background",
        "paper"
      ],
      "boxPaths": {
        "MessageBox": "MessageBox",
        "paperbox": "paperbox",
        "MessageText": "MessageText",
        "MessageBox/Action": "MessageBox/Action",
        "MessageBox/MessageText": "MessageBox/MessageText"
      }
    },
    "1021": {
      "name": "querybox",
      "boxPresentation": "querybox",
      "boxUnits": [
        "QueryBox",
        "Background",
        "Paper"
      ],
      "boxPaths": {
        "Choices": "Choices",
        "Affirmative": "Affirmative",
        "Negative": "Negative",
        "XAChoices": "XAChoices",
        "EquipAndContinue": "EquipAndContinue",
        "Continue": "Continue",
        "ReloadChoices": "ReloadChoices",
        "Reload": "Reload",
        "Cancel": "Cancel",
        "Text": "Text"
      }
    },
    "1022": {
      "name": "numericbox",
      "boxPresentation": "numericadjusterbox",
      "boxUnits": [
        "QueryBox",
        "Background",
        "Paper"
      ],
      "boxPaths": {
        "Text": "Text",
        "PriceWidget": "PriceWidget",
        "Arrows": "Arrows",
        "up": "up",
        "down": "down",
        "PriceWidget/Text": "PriceWidget/Text",
        "PriceWidget/Arrows/": "PriceWidget/Arrows/"
      }
    },
    "1023": {
      "name": "menubox",
      "boxPresentation": "menubox",
      "boxUnits": [
        "Group"
      ],
      "boxPaths": {
        "Choices": "Choices",
        "Title": "Title",
        "Multiplayer_Button": "Multiplayer_Button",
        "Choice1": "Choice1",
        "Frame": "Frame",
        "OK/CANCEL": "OK/CANCEL",
        "Negative": "Negative",
        "GamerTag": "GamerTag",
        "gamertag": "gamertag",
        "Choices/Multiplayer_Button": "Choices/Multiplayer_Button",
        "Choice1/Frame/Choice1": "Choice1/Frame/Choice1",
        "Choice1/Choice1": "Choice1/Choice1",
        "Choice1/RimSelect": "Choice1/RimSelect",
        "OK/CANCEL/Negative": "OK/CANCEL/Negative"
      }
    }
  }
}
""";
}
