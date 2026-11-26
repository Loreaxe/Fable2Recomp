using System;
using System.Collections.Generic;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// Fable 2's own verbs — the inventory, quest, sale, ability, shop, property and mod-ability rows,
/// and the pause book's levers — declared for the catalog (<see cref="Engine_UiVerbs.Game"/>) and
/// answered on the shell's dispatcher when the title installs.
/// </summary>
public static class Fable2UiVerbs
{
	public static readonly Engine_UiVerb Inventory = new(Engine_UiInventorySource.InventoryAction, "Inventory:<op>:<record>",
		"an inventory operation on the simulation (use, equip, gift...), run on the guest's tick; on the items and clothing books it is posted as the book's own row lever", Engine_UiVerbs.Anywhere);
	public static readonly Engine_UiVerb Quest = new(Engine_UiQuestSource.QuestAction, "Quest:select:<record>",
		"makes a quest primary through the world map's own answer", Engine_UiVerbs.Anywhere);
	public static readonly Engine_UiVerb Sale = new(Engine_UiQuestSource.SaleAction, "Sale:<...>",
		"a world-map sale row's answer", Engine_UiVerbs.Anywhere);
	public static readonly Engine_UiVerb Ability = new(Engine_UiAbilitySource.AbilityAction, "Ability:learn|unlearn:<1..14>",
		"buys one level of an ability through the guest's own purchase, or gives one back through its own unlearn (the row's Y asks first)", Engine_UiVerbs.Anywhere);
	public static readonly Engine_UiVerb Furniture = new(Engine_UiFurniturePage.FurnitureAction, "Furniture:upgrade:<level>",
		"upgrades the targeted furniture to a level through the furniture screen's own transaction (unlock card, value, properties)", Engine_UiVerbs.Anywhere);
	public static readonly Engine_UiVerb Decoration = new(DecorationAction, "Decoration:mount:<ticket>|takedown",
		"the decoration screen's own answers: mounts the trophy a row's ticket names, or takes down what the mount holds; both leave the screen", Engine_UiVerbs.Anywhere);
	public static readonly Engine_UiVerb Crime = new(CrimeAction, "Crime:choice",
		"answers the crime screen with the pressed row (pay the fine, resist arrest, community service) through the screen's own handler; a choice the guest does not allow leaves the screen up", Engine_UiVerbs.Anywhere);
	public static readonly Engine_UiVerb Query = new(QueryAction, "Query:close|rename",
		"the villager card's own buttons: closes it, or asks to rename its target (the platform keyboard opens when the target takes a name)", Engine_UiVerbs.Anywhere);
	public static readonly Engine_UiVerb RealEstate = new(RealEstateAction, "RealEstate:event",
		"commits the pressed real-estate row by its event (110 sell .. 120 back) through the screen's own handler, which asks its own confirm or opens its own numeric box", Engine_UiVerbs.Anywhere);
	public static readonly Engine_UiVerb Shop = new(Fable2MenuSources.ShopAction, "Shop:buy|sell:<ticket>",
		"a shop row's transaction: the row pushed at the shop screen's own handler by its ticket (30 buys, 31 sells), which asks its own confirm", Engine_UiVerbs.Anywhere);
	public static readonly Engine_UiVerb PropertyRepair = new(Engine_UiPropertySource.RepairAction, "PropertyRepair:<uid>",
		"confirms and repairs a property for the menu's player", Engine_UiVerbs.Anywhere);
	public static readonly Engine_UiVerb PropertyRepairAll = new(Engine_UiPropertySource.RepairAllAction, "PropertyRepairAll",
		"repairs every property", Engine_UiVerbs.Anywhere);
	public static readonly Engine_UiVerb Partner = new(Engine_UiPropertySource.PartnerAction, "Partner:<...>",
		"a property partnership row's answer", Engine_UiVerbs.Anywhere);
	public static readonly Engine_UiVerb ModBuy = new(Engine_UiModAbilities.BuyAction, "Mod:buy:<ability>",
		"buys a level of a mod ability (ui/abilities.json) for the row's player: the engine's purchase", Engine_UiVerbs.Anywhere);
	public static readonly Engine_UiVerb OptionsPage = new(OptionsPageAction, "OptionsPage:Game|Controls|Video|Audio",
		"opens one of the options screen's four pages; its A runs the page's setters, its B cancels", Engine_UiVerbs.GuestScreen);
	public static readonly Engine_UiVerb PauseTab = new(Engine_PauseMenu.TabCommandPrefix, "PauseTab:<tab>",
		"opens a pause tab through the guest's tab lever (2 map .. 8 options)", Engine_UiVerbs.PauseOnly);
	public static readonly Engine_UiVerb PauseSaving = new(Engine_PauseMenu.SavingCommandPrefix, "PauseSaving:<0|1|2>",
		"the guest's saving answer: 0 save and continue, 1 save and quit, 2 quit without saving", Engine_UiVerbs.PauseOnly);
	public static readonly Engine_UiVerb PauseQuit = new(Engine_PauseMenu.QuitCommandPrefix, "PauseQuit:<1|2>",
		"the saving answer, then leaves the app when the guest reaches its title", Engine_UiVerbs.PauseOnly);
	public static readonly Engine_UiVerb PauseCoop = new(Engine_PauseMenu.CoopCommandPrefix, "PauseCoop:<leave|kick>",
		"player two leaves the co-op game, or the online host kicks them", Engine_UiVerbs.PauseOnly);
	public static readonly Engine_UiVerb PauseHenchmanQuit = new(Engine_PauseMenu.HenchmanQuitCommand, Engine_PauseMenu.HenchmanQuitCommand,
		"player two's Quit: the guest's own henchman box (with a save, their hero is saved; without one, their experience and gold go to the hero)", Engine_UiVerbs.PauseOnly);
	public static readonly Engine_UiVerb PauseOwned = new(Engine_PauseMenu.OwnedCommandPrefix, "PauseOwned:<id>",
		"the old spelling of Screen:open:<id>", Engine_UiVerbs.Anywhere);
	public static readonly Engine_UiVerb MiniGameConfirm = new(MiniGameConfirmCommand, MiniGameConfirmCommand,
		"A on the minigame tally: its rows finish counting, and the press goes on to the tally's own handler, which closes it once its rows have", Engine_UiVerbs.GuestScreen);

	/// <summary>The decoration screen's (27) verbs: a row's A with its ticket, and the list's Y.</summary>
	public const string DecorationAction = "Decoration:";
	public const string DecorationMountAction = DecorationAction + "mount:" + Engine_UiScreenTemplate.RowIdToken;
	public const string DecorationTakeDownAction = DecorationAction + "takedown";

	/// <summary>The crime screen's (21) row verb: the pressed row's own event type is the argument.</summary>
	public const string CrimeAction = "Crime:";
	public const string CrimeChoiceAction = CrimeAction + "choice";

	/// <summary>The villager card's (10) verbs: its B and its Y.</summary>
	public const string QueryAction = "Query:";
	public const string QueryCloseAction = QueryAction + "close";
	public const string QueryRenameAction = QueryAction + "rename";

	/// <summary>The real-estate screen's (28) row verb: the pressed row's own event type is the argument.</summary>
	public const string RealEstateAction = "RealEstate:";
	public const string RealEstateEventAction = RealEstateAction + "event";

	/// <summary>The options rows' command prefix: the page by the name the row carries.</summary>
	public const string OptionsPageAction = "OptionsPage:";

	/// <summary>The minigame tally's A (screen 11's `revealedAccept`).</summary>
	public const string MiniGameConfirmCommand = "minigame:confirm";

	/// <summary>Every verb Fable 2 adds to the shell's.</summary>
	public static readonly IReadOnlyList<Engine_UiVerb> All = new[]
	{
		Inventory, Quest, Sale, Ability, Furniture, Decoration, RealEstate, Crime, Query, Shop, PropertyRepair,
		PropertyRepairAll, Partner, ModBuy,
		OptionsPage, PauseTab, PauseSaving, PauseQuit, PauseCoop, PauseHenchmanQuit, PauseOwned, MiniGameConfirm,
	};

	/// <summary>Posts a lever; a verb with none for this row (a malformed command) is left unposted.</summary>
	private static Engine_UiCommandResult Post(string? lever)
	{
		if (lever is null)
		{
			return Engine_UiCommandResult.From(false, "names no lever of its screen");
		}

		Engine_UiSource.Current.PostFrontEndAction(lever);
		return Engine_UiCommandResult.Done;
	}

	/// <summary>The handlers for the rows' verbs; the pause levers are registered by the pause book.</summary>
	public static void RegisterHandlers(Engine_UiCommandSet set)
	{
		// `Inventory:<op>:<record hash>` — an operation on the SIMULATION, posted to the guest's
		// tick pump because guest code must run on the guest's thread. It reaches
		// `AddAndReplaceItemOfRecordID` / `Player:GiveGift` directly: no GUI event queue, no
		// screen class, no populated Gameface menu. See `DispatchInventoryAction`.
		set.Register(Fable2UiVerbs.Inventory, (_, context) =>
		{
			string action = context.Command;

			// A dye is CHOSEN, not used: the press opens the dye list and the simulation is
			// untouched until a target is picked.
			if (action.StartsWith(Engine_UiDyeFlow.SelectAction, System.StringComparison.Ordinal))
			{
				if (uint.TryParse(action[Engine_UiDyeFlow.SelectAction.Length..], out uint dye))
				{
					Engine_UiDyeFlow.Begin(dye);
				}

				return Engine_UiCommandResult.Done;
			}

			// An augment is CHOSEN the same way: the press opens the target list.
			if (action.StartsWith(Engine_UiAugmentFlow.SelectAction, System.StringComparison.Ordinal))
			{
				if (uint.TryParse(action[Engine_UiAugmentFlow.SelectAction.Length..], out uint augment))
				{
					Engine_UiAugmentFlow.Begin(augment);
				}

				return Engine_UiCommandResult.Done;
			}

			// A remover the same way, for the strip list.
			if (action.StartsWith(Engine_UiAugmentFlow.SelectRemoverAction, System.StringComparison.Ordinal))
			{
				if (uint.TryParse(action[Engine_UiAugmentFlow.SelectRemoverAction.Length..], out uint remover))
				{
					Engine_UiAugmentFlow.BeginRemove(remover);
				}

				return Engine_UiCommandResult.Done;
			}

			// The attach or strip itself goes to the pump; the names are kept now for its
			// verdict's message, since a successful attach consumes the augment.
			if (action.StartsWith(Engine_UiAugmentFlow.ApplyAction, System.StringComparison.Ordinal) ||
				action.StartsWith(Engine_UiAugmentFlow.RemoveAction, System.StringComparison.Ordinal))
			{
				string[] pair = action[(action.IndexOf(':', Engine_UiInventorySource.InventoryAction.Length) + 1)..].Split(':');
				if (pair.Length == 2 && uint.TryParse(pair[0], out uint augment) && uint.TryParse(pair[1], out uint target))
				{
					Engine_UiAugmentFlow.Names = (Engine_UiInventorySource.LabelFor(augment), Engine_UiInventorySource.LabelFor(target));
				}
			}

			// A joiner's weapon pick changes the record; the host equips it. The id is decimal.
			const string equip = Engine_UiInventorySource.InventoryAction + "equip:";
			if (CoopSession.IsClient && action.StartsWith(equip, System.StringComparison.Ordinal) &&
				uint.TryParse(action[equip.Length..], out uint record) &&
				CoopHeroKit.SetLocalWeapon(record, Engine_UiInventorySource.WeaponClassOf(record)))
			{
				return Engine_UiCommandResult.Done;
			}

			// On a ported item screen (items 15, clothing 16, gift 25) the row's consequence is its lever, with
			// the acting player and the ticket the guest spawned the row with (Fable2PortedScreens).
			uint screen = Engine_UiSource.Current.GetPauseState() is { } pause ? pause.ScreenId : 0u;
			Refii.Host.HostBridge.PostFrontEndAction(
				Fable2PortedScreens.ItemLever(screen, Engine_UiSource.Current.ActingPlayer, action,
					Fable2PortedScreens.TicketOf(Engine_UiMenuSources.SpawnedRows(), context.Row)) ?? action);
			return Engine_UiCommandResult.Done;
		});

		// `Quest:select:<record>` — the world map's quest rows. The pump turns the record into the
		// guest's own row ordinal and delivers event 42 to screen 23, whose handler asks "make this
		// your primary quest?" and commits through `QuestTracker.SetAsPrimary`. `Sale:` likewise.
		//
		// `Ability:learn:<1..14>` — the abilities screen. The guest's own answer to one of those
		// rows is a single two-argument call that reads nothing from the screen or the menu
		// (`sub_8246C970` / `sub_8246CF48`), posted as `Ability:<p>:learn:<n>` for the acting player.
		Engine_UiCommandHandler pump = (_, context) =>
		{
			Refii.Host.HostBridge.PostFrontEndAction(context.Command);
			return Engine_UiCommandResult.Done;
		};
		set.Register(Fable2UiVerbs.Quest, pump);
		set.Register(Fable2UiVerbs.Sale, pump);
		set.Register(Fable2UiVerbs.Ability, (_, context) =>
		{
			Engine_UiSource.Current.PostFrontEndAction(
				Fable2PortedScreens.Ability(Engine_UiSource.Current.ActingPlayer, context.Command) ?? context.Command);
			return Engine_UiCommandResult.Done;
		});

		// The two furniture screens and real estate are ported (gui_screen_stub_*.cpp): a row's verb is
		// posted as its screen's lever with the acting player — `Furniture:<p>:upgrade:<level>`,
		// `Decoration:<p>:mount:<ticket>|takedown`, and `RealEstate:<p>:event:<type>` with the type the
		// pressed row was spawned with.
		set.Register(Fable2UiVerbs.Furniture, (_, context) =>
			Post(Fable2PortedScreens.RowLever(Fable2PortedScreens.Furniture, Engine_UiSource.Current.ActingPlayer,
				context.Command, context.RowEventType)));
		set.Register(Fable2UiVerbs.Decoration, (_, context) =>
			Post(Fable2PortedScreens.RowLever(Fable2PortedScreens.Decoration, Engine_UiSource.Current.ActingPlayer,
				context.Command, context.RowEventType)));
		set.Register(Fable2UiVerbs.RealEstate, (_, context) =>
			Post(Fable2PortedScreens.RowLever(Fable2PortedScreens.RealEstate, Engine_UiSource.Current.ActingPlayer,
				context.Command, context.RowEventType)));

		// The crime screen's rows and the villager card's buttons, the same way: `Crime:<p>:pay|resist|service`
		// from the pressed row's event (50 / 51 / 52), `Query:<p>:close|rename`.
		set.Register(Fable2UiVerbs.Crime, (_, context) =>
			Post(Fable2PortedScreens.RowLever(Fable2PortedScreens.Crime, Engine_UiSource.Current.ActingPlayer,
				context.Command, context.RowEventType)));
		set.Register(Fable2UiVerbs.Query, (_, context) =>
			Post(Fable2PortedScreens.RowLever(Fable2PortedScreens.Query, Engine_UiSource.Current.ActingPlayer,
				context.Command, context.RowEventType)));

		// `OptionsPage:<page>` — the options screen's ported handler takes no page input
		// (gui_screen_stub_options.cpp), so its page level is Godot's record, seeded from what the guest's
		// page readers and level setters state.
		set.Register(Fable2UiVerbs.OptionsPage, (page, _) => Engine_UiCommandResult.From(
			Engine_UiOptionsPending.Enter(page, Refii.Host.HostBridge.GetOptionSliders(), GameUi.PauseIsPlayerTwos()),
			$"'{page}' is not an options page"));

		// `Shop:buy|sell:<ticket>` — the shop screen's handler `sub_826AE5C0` acts on the PUSHED
		// row: it caches the event's id at screen+396 and its confirm's YES re-reads it
		// (@0x826AF8BC) before buying (`sub_82872A68`, 0x826B0960) or selling (`sub_82872D70`,
		// 0x826AFEB8). So the push IS the guest's
		// contract here — type 30 buys, 31 sells, the ticket is the one its own populate handed
		// the row — and the shop's own confirm (modal 19/20) follows.
		set.Register(Fable2UiVerbs.Shop, (_, context) =>
		{
			string body = context.Command[Fable2MenuSources.ShopAction.Length..];
			int colon = body.IndexOf(':');
			string op = colon > 0 ? body[..colon] : string.Empty;
			if ((op != "buy" && op != "sell") ||
				!uint.TryParse(body[(colon + 1)..], out uint ticket) || ticket == 0)
			{
				return Engine_UiCommandResult.Refused($"'{context.Command}' names no buy|sell ticket");
			}

			int type = context.RowEventType != 0 ? context.RowEventType : (op == "buy" ? 30 : 31);
			Engine_UiSource.Current.MenuEvent(context.Row, type, ticket);
			return Engine_UiCommandResult.Done;
		});

		// The world map's property rows: confirmed, then repaired for the player whose menus are
		// open (Engine_UiPropertySource).
		Engine_UiCommandHandler property = (_, context) => Engine_UiCommandResult.From(
			Engine_UiPropertySource.Run(context.Command), $"'{context.Command}' names no property");
		set.Register(Fable2UiVerbs.PropertyRepair, property);
		set.Register(Fable2UiVerbs.PropertyRepairAll, property);
		set.Register(Fable2UiVerbs.Partner, property);

		// `Mod:buy:<ability>` — a mod ability's row: the engine's purchase for the row's player
		// (Engine_UiModAbilities, Fable2/src/vfs/src/hooks/mod_abilities.cpp).
		set.Register(Fable2UiVerbs.ModBuy, (_, context) => Engine_UiCommandResult.From(
			Engine_UiModAbilities.Buy(context.Command, context.Player), "names no ability"));
	}
}
