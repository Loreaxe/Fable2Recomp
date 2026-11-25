namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// The screens whose guest handler is ported (S7, gui_screen_stub_*.cpp): their slots keep only the
/// guest's statements and game calls, so the book buttons that were spine calls inside the handler's
/// input slot are posted as the spine levers instead — B at a book's root `PauseBack` (`sub_826B8130` +
/// `sub_826D5AF0`), B at the pause root `PauseClose:&lt;player&gt;` (`ExitScreen(QM, 1)`, 0x826D5AA4),
/// LB / RB `PauseCycle:&lt;player&gt;:prev|next` (`sub_826B8780` / `sub_826B8878`, player one only, the
/// guest's gate). Start is the guest's own handler `sub_82371B10` as `Start:&lt;player&gt;`. The item
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

	/// <summary>Whether <paramref name="screen"/>'s book buttons are levers rather than handler input.</summary>
	public static bool OwnsBookButtons(uint screen) =>
		screen is PauseRoot or Abilities or Items or Clothing or Logbook or Options;

	/// <summary>The lever a book button posts on a ported screen, or null when it still goes to the
	/// screen's input (<see cref="Engine_UiScreenInput"/> codes).</summary>
	public static string? Lever(uint screen, uint code, int player) =>
		!OwnsBookButtons(screen) ? null : code switch
		{
			Engine_UiScreenInput.Back => screen == PauseRoot ? $"PauseClose:{player}" : "PauseBack",
			Engine_UiScreenInput.TabPrev => $"PauseCycle:{player}:prev",
			Engine_UiScreenInput.TabNext => $"PauseCycle:{player}:next",
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
	/// `unequip`. Ticket 0 names the record's first row. Null for any other command or screen (a dye, an
	/// augment, the gift screen): it is posted as it stands.
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
			_ => null,
		};
	}

	/// <summary>
	/// The population ticket the guest spawned the row named <paramref name="rowName"/> with: the id of
	/// the first captured row of that name (an item row's name is its `NameTag` on both sides,
	/// `sub_826CE8D0`'s String1), 0 when the capture holds none. The pump checks it against the record.
	/// </summary>
	public static uint TicketOf(System.Collections.Generic.IReadOnlyList<Engine_UiModel.MenuRow> captured, string rowName)
	{
		if (rowName.Length == 0)
		{
			return 0u;
		}

		foreach (Engine_UiModel.MenuRow row in captured)
		{
			if (string.Equals(row.Name, rowName, System.StringComparison.Ordinal))
			{
				return row.Id;
			}
		}

		return 0u;
	}

	/// <summary>A Saving row (0 save and continue, 1 save and quit, 2 quit without saving), answered
	/// as the guest's root answers it: its confirm box, or the quit when nothing is held.</summary>
	public static string Saving(int player, string row) => $"{Engine_PauseMenu.SavingCommandPrefix}{player}:{row}";
}
