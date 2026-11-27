namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// The screens whose guest handler is ported (S7, gui_screen_stub_*.cpp): their slots keep only the
/// guest's statements and game calls, so the book buttons that were spine calls inside the handler's
/// input slot are posted as the spine levers instead — B at a book's root `PauseBack` (`sub_826B8130` +
/// `sub_826D5AF0`), B at the pause root `PauseClose:&lt;player&gt;` (`ExitScreen(QM, 1)`, 0x826D5AA4),
/// LB / RB `PauseCycle:&lt;player&gt;:prev|next` (`sub_826B8780` / `sub_826B8878`, player one only, the
/// guest's gate). Start is the guest's own handler `sub_82371B10` as `Start:&lt;player&gt;`. The shop's
/// (22) and the gift screen's (25) B is the guest's own back as `Shop|Gift:&lt;player&gt;:close`; upgrade
/// furniture's (26), decoration's (27) and real estate's (28) B is `Furniture|Decoration|RealEstate:&lt;player&gt;:close`
/// and their rows post <see cref="RowLever"/>. The item
/// books' rows (items 15, clothing 16) and the received-item card (8) post their consequences the same
/// way: <see cref="ItemLever"/>, and the card surface's `Card:&lt;player&gt;:dismiss|equip`.
/// </summary>
public static class Fable2PortedScreens
{
	/// <summary>The pause root, 0.</summary>
	public const uint PauseRoot = 0;

	/// <summary>The abilities book, 14 (TU1 numbering).</summary>
	public const uint Abilities = 14;

	/// <summary>The items / weapons book, 15 (TU1 numbering).</summary>
	public const uint Items = 15;

	/// <summary>The clothing book, 16 (TU1 numbering).</summary>
	public const uint Clothing = 16;

	/// <summary>The logbook, 17 (TU1 numbering).</summary>
	public const uint Logbook = 17;

	/// <summary>The options screen, 18 (TU1 numbering).</summary>
	public const uint Options = 18;

	/// <summary>The world map, 23 (TU1 numbering).</summary>
	public const uint WorldMap = 23;

	/// <summary>The shop, 22 (TU1 numbering).</summary>
	public const uint Shop = 22;

	/// <summary>The gift screen, 25 (TU1 numbering).</summary>
	public const uint Gift = 25;

	/// <summary>The villager card, 10 (both builds).</summary>
	public const uint Query = 10;

	/// <summary>The crime screen, 21 (TU1 numbering).</summary>
	public const uint Crime = 21;

	/// <summary>The safety toggle in play, the expression wheel's own branch on hero one
	/// (`SafetyToggle:&lt;p&gt;`, hud_levers.cpp); the guest refuses player two.</summary>
	public static string SafetyToggle(int player) => $"SafetyToggle:{player}";

	/// <summary>The HUD target's villager card in play, the guest's raw-Y arm with every gate its own
	/// (`TargetQuery:&lt;p&gt;`, hud_levers.cpp); the guest takes player one's pad alone.</summary>
	public static string TargetQuery(int player) => $"TargetQuery:{player}";

	/// <summary>Upgrade furniture, 26 (TU1 numbering).</summary>
	public const uint Furniture = 26;

	/// <summary>Decoration (the trophy mount), 27 (TU1 numbering).</summary>
	public const uint Decoration = 27;

	/// <summary>Real estate, 28 (TU1 numbering).</summary>
	public const uint RealEstate = 28;

	/// <summary>The lever name of a screen whose B is its own `&lt;name&gt;:&lt;player&gt;:close`, or null.</summary>
	private static string? ClosesByLever(uint screen) => screen switch
	{
		Shop => "Shop",
		Gift => "Gift",
		Furniture => "Furniture",
		Decoration => "Decoration",
		RealEstate => "RealEstate",
		_ => null,
	};

	/// <summary>
	/// The crime screen's row verb (`Crime:choice`, the pressed row's event 50 / 51 / 52 its argument) is
	/// `Crime:&lt;p&gt;:pay|resist|service`; the villager card's (`Query:close`, `Query:rename`)
	/// `Query:&lt;p&gt;:close|rename`.
	/// A row's verb on upgrade furniture (`Furniture:upgrade:&lt;level&gt;`), decoration
	/// (`Decoration:mount:&lt;ticket&gt;`, `Decoration:takedown`) or real estate (`RealEstate:event`, the
	/// pressed row's own event type 110..120 its argument) as the lever its ported screen takes, the
	/// acting player after the screen's name: `Furniture:&lt;p&gt;:upgrade:&lt;level&gt;`,
	/// `Decoration:&lt;p&gt;:mount:&lt;ticket&gt;|takedown`, `RealEstate:&lt;p&gt;:event:&lt;type&gt;`. Null for a verb
	/// that is not that screen's, or a real-estate row whose event is not one of the eleven.
	/// </summary>
	public static string? RowLever(uint screen, int player, string command, int rowEventType)
	{
		if (screen is Crime or Query)
		{
			return (screen, command, rowEventType) switch
			{
				(Crime, "Crime:choice", (int)Engine_UiMenuConstants.EventType.CrimePayFineItem) =>
					Engine_UiSource.PlayerLever("Crime", player, "pay"),
				(Crime, "Crime:choice", (int)Engine_UiMenuConstants.EventType.CrimeResistArrestItem) =>
					Engine_UiSource.PlayerLever("Crime", player, "resist"),
				(Crime, "Crime:choice", (int)Engine_UiMenuConstants.EventType.CrimeCommunityServiceItem) =>
					Engine_UiSource.PlayerLever("Crime", player, "service"),
				(Query, "Query:close" or "Query:rename", _) =>
					Engine_UiSource.PlayerLever("Query", player, command["Query:".Length..]),
				_ => null,
			};
		}

		if (ClosesByLever(screen) is not { } name || screen is Shop or Gift ||
			!command.StartsWith(name + ":", System.StringComparison.Ordinal))
		{
			return null;
		}

		string rest = command[(name.Length + 1)..];
		bool Numbered(string verb) =>
			rest.StartsWith(verb, System.StringComparison.Ordinal) &&
			uint.TryParse(rest[verb.Length..], System.Globalization.NumberStyles.None,
				System.Globalization.CultureInfo.InvariantCulture, out _);

		return screen switch
		{
			Furniture when Numbered("upgrade:") => Engine_UiSource.PlayerLever(name, player, rest),
			Decoration when rest == "takedown" || Numbered("mount:") => Engine_UiSource.PlayerLever(name, player, rest),
			RealEstate when rest == "event" && Engine_UiMenuConstants.RealEstateRowName(rowEventType) is not null =>
				Engine_UiSource.PlayerLever(name, player, $"event:{rowEventType}"),
			_ => null,
		};
	}

	/// <summary>Whether <paramref name="screen"/>'s book buttons are levers rather than handler input.</summary>
	public static bool OwnsBookButtons(uint screen) =>
		screen is PauseRoot or Abilities or Items or Clothing or Logbook or Options or WorldMap;

	/// <summary>The lever a book button posts on a ported screen, or null when it still goes to the
	/// screen's input (<see cref="Engine_UiScreenInput"/> codes).</summary>
	public static string? Lever(uint screen, uint code, int player) =>
		ClosesByLever(screen) is { } closes
			? code == Engine_UiScreenInput.Back
				? Engine_UiSource.PlayerLever(closes, player, "close")
				: null
			: !OwnsBookButtons(screen) ? null : code switch
		{
			Engine_UiScreenInput.Back => screen == PauseRoot ? $"PauseClose:{player}" : "PauseBack",
			Engine_UiScreenInput.TabPrev => $"PauseCycle:{player}:prev",
			Engine_UiScreenInput.TabNext => $"PauseCycle:{player}:next",

			// Y on the clothing book: its own code 39, the held dye on the highlighted target's second layer
			// (`sub_826D3108(h, 1)` 0x826D52D4, the lever DispatchClothing runs).
			Engine_UiScreenInput.Unlearn when screen == Clothing => Engine_UiSource.PlayerLever("Clothing", player, "dye2"),
			_ => null,
		};

	/// <summary>The player's Start, through the guest's own handler: opens the book from play, closes
	/// it from any pause screen, the lite pause box in a cutscene or conversation.</summary>
	public static string Start(int player) => $"Start:{player}";

	/// <summary>An abilities row's command (`Ability:learn|unlearn:&lt;1..14&gt;`, the row's identity that mods
	/// read) as the lever with the acting player, `Ability:&lt;p&gt;:learn|unlearn:&lt;n&gt;`: the guest's own
	/// transaction for that player's hero (DispatchAbility). Null when it is not an ability command.</summary>
	public static string? Ability(int player, string command) =>
		command.StartsWith(Engine_UiAbilitySource.AbilityAction, System.StringComparison.Ordinal) &&
		command.Length > Engine_UiAbilitySource.AbilityAction.Length
			? Engine_UiSource.PlayerLever("Ability", player, command[Engine_UiAbilitySource.AbilityAction.Length..])
			: null;

	/// <summary>
	/// An item book row's command (`Inventory:&lt;op&gt;:&lt;n&gt;`, the row's identity that mods read) as the
	/// lever its ported screen takes, carrying the acting player, the row's population ticket and its
	/// record: on the items book `Items:&lt;p&gt;:use|equip:&lt;ticket&gt;:&lt;record&gt;` (the handler's own row answer on
	/// that ticket); on the clothing book `Clothing:&lt;p&gt;:wear|outfit:&lt;ticket&gt;:&lt;record&gt;` — a dog item's
	/// `equip` is the same row answer — and `Clothing:&lt;p&gt;:remove:&lt;event type 91..97&gt;` for a None row's
	/// `unequip`; on the gift screen `Gift:&lt;p&gt;:item:&lt;ticket&gt;:&lt;record&gt;` (the screen's own row answer: its
	/// confirm, commit and exit). Ticket 0 names the record's first row. Null for any other command or
	/// screen: it is posted as it stands. `ticket` is the row's own id — the guest's ticket for that row
	/// (Engine_UiItemRows); 0 from a list that names no row (a mod's `use_item`).
	/// </summary>
	public static string? ItemLever(uint screen, int player, string command, uint ticket)
	{
		const string prefix = Engine_UiInventorySource.InventoryAction;
		if (!command.StartsWith(prefix, System.StringComparison.Ordinal))
		{
			return null;
		}

		string body = command[prefix.Length..];
		int colon = body.IndexOf(':');
		if (colon <= 0 || !uint.TryParse(body[(colon + 1)..], System.Globalization.NumberStyles.None,
			System.Globalization.CultureInfo.InvariantCulture, out uint value) || value == 0u)
		{
			return null;
		}

		return (screen, body[..colon]) switch
		{
			(Items, "use" or "equip") => Engine_UiSource.PlayerLever("Items", player, $"{body[..colon]}:{ticket}:{value}"),
			(Clothing, "wear" or "equip") => Engine_UiSource.PlayerLever("Clothing", player, $"wear:{ticket}:{value}"),
			(Clothing, "outfit") => Engine_UiSource.PlayerLever("Clothing", player, $"outfit:{ticket}:{value}"),
			(Clothing, "unequip") => Engine_UiSource.PlayerLever("Clothing", player, $"remove:{value}"),
			(Gift, "gift") => Engine_UiSource.PlayerLever("Gift", player, $"item:{ticket}:{value}"),
			_ => null,
		};
	}

	/// <summary>A Saving row (0 save and continue, 1 save and quit, 2 quit without saving), answered
	/// as the guest's root answers it: its confirm box, or the quit when nothing is held.</summary>
	public static string Saving(int player, string row) => $"{Engine_PauseMenu.SavingCommandPrefix}{player}:{row}";

	/// <summary>Player two's Quit row's lever: "PauseHenchmanQuit:&lt;player&gt;".</summary>
	public static string HenchmanQuit(int player) => $"{Engine_PauseMenu.HenchmanQuitCommand}:{player}";
}
