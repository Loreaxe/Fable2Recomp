namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// The pause menu's item list, built the way the guest builds its own.
/// </summary>
/// <remarks>
/// The list is the guest's; layout, highlight and navigation are ours — the same split as
/// <see cref="Engine_FrontEndMenu"/>.
///
/// Source: `sub_8298DF98`, the hero variant of the builder `sub_826A2C88` runs when it pushes
/// screen id 0. Items spawn through the same GUI-var stager as the front end's
/// (`sub_826CE8D0` -> `g_Menu:SpawnButton()`), whose argument mapping is `String1` = item NAME,
/// `String2` = display TEXT, `String3` = icon, `String4` = parent, `Float3` = the command. The
/// co-op henchman list (`sub_8298EDE0`) is not here, nor is the hero list's `Kick` entry —
/// `0x8298EC64` spawns it only when `*(*(dword_83496AB8+12)+284) == 1` and its label is a `%1`
/// substitution of the henchman's name.
///
/// The Saving children are named by their own displayed text: `sub_826CE848` takes a tag alone,
/// `sub_8230E0E8` localises it, `sub_82B3F898` narrows it to bytes, and the narrowed text is
/// passed as `String1` — so their identity is "Save and Continue", a string in the text bank
/// rather than an identifier in the image.
///
/// Dispatch is the tab lever, not the pump: each screen entry names the tab `sub_826B89E0`'s
/// switch maps to its screen, and the host writes the guest's pending-tab fields (see
/// Engine_UiSource.Current.PauseTabSelect). The tab path to Abilities omits the mode call the
/// ITEM path makes (`vfn+32(screen14, 0)`) and so inherits the screen's last mode — the guest's
/// own LB/RB behaves identically.
///
/// The Saving group's children are rows 0/1/2 of `sub_826A5FC0`'s {type 2, row} branch: they open
/// query boxes 23/24/25 (row 2 quits immediately when `sub_82448900` says nothing is held). The
/// row command names the row; the verb posts "PauseSaving:&lt;player&gt;:&lt;row&gt;" to the pump,
/// whose ported root runs that branch (gui_screen_stub_pause_root.cpp) and the guest's own query box takes
/// the confirm.
/// </remarks>
public static class Engine_PauseMenu
{
	/// <summary>Command prefix a tab entry dispatches with; the suffix is the tab id.</summary>
	public const string TabCommandPrefix = "PauseTab:";

	/// <summary>Command prefix of a Saving child; the suffix is the guest's event param —
	/// 0 save-and-continue, 1 save-and-quit, 2 quit-without-saving.</summary>
	public const string SavingCommandPrefix = "PauseSaving:";

	/// <summary>
	/// Command prefix of a row that leaves the APP rather than the world; the suffix is the
	/// guest saving param to run first — 1 save then leave, 2 leave without saving.
	/// </summary>
	/// <remarks>
	/// Posts the same `PauseSaving:N` event the title rows do, then latches on the front end
	/// coming up. The confirmation box, the save and the teardown stay the guest's and stay in
	/// the guest's order, so the process cannot end while the save is still being written; a
	/// player who answers "no" never reaches the front end, so a refusal needs no signal.
	/// </remarks>
	public const string QuitCommandPrefix = "PauseQuit:";

	/// <summary>
	/// The old spelling of the open-screen verb (<see cref="Engine_UiHostScreens.OpenPrefix"/>,
	/// <c>Screen:open:&lt;id&gt;</c>), still answered for a player's file written with it. The tab
	/// lever cannot express a host screen — `sub_826B89E0`'s switch has no case for a screen the
	/// guest never had — so it never reaches the bridge and the guest's pause root stays put.
	/// </summary>
	public const string OwnedCommandPrefix = "PauseOwned:";

	/// <summary>
	/// Command prefix of a co-op row (Fable 3's Leave / Kick): `PauseCoop:leave` posts the pump's
	/// `CoopLeave`, `PauseCoop:kick` its `CoopKick` — the guest's own drop-out and kick answers,
	/// without the guest's boxes (the confirmation is the row's own child).
	/// </summary>
	public const string CoopCommandPrefix = "PauseCoop:";

	/// <summary>
	/// Player two's Quit row: the verb posts "PauseHenchmanQuit:&lt;player&gt;", whose pump runs the
	/// guest's own henchman arm `sub_826A6610` (gui_screen_stub_pause_root.cpp): its box 26 or 27.
	/// </summary>
	public const string HenchmanQuitCommand = "PauseHenchmanQuit";

	/// <summary>The host's Kick row, offered only while an online partner stands.</summary>
	public const string KickRow = "Kick";

	/// <summary>Tab ids as `sub_826B89E0`'s switch numbers them.</summary>
	public const uint TabMap = 2;
	public const uint TabAbilities = 3;
	public const uint TabItems = 4;
	public const uint TabWeapons = 5;
	public const uint TabClothing = 6;
	public const uint TabLogbook = 7;
	public const uint TabOptions = 8;

	/// <summary>
	/// Builds the pause list from the TEMPLATE — `sub_8298DF98`'s spawn list as data: same names,
	/// same event params on the Saving children, same tab ids, and the guest's own
	/// SetInitiallySelectedItem(2). ONE record for both players: player two's rows stand under the
	/// row condition <see cref="Engine_UiRowConditions.PlayerTwo"/>, which this states, so a mod
	/// patches the pause book once and reaches both.
	/// </summary>
	/// <param name="playerTwo">
	/// True when this list is player two's (a joiner's, or local player two's) — the caller reads
	/// it, the same way <see cref="ApplyTabAvailability"/> is handed its mask rather than fetching
	/// one.
	/// </param>
	public static Engine_FrontEndMenu BuildHero(bool playerTwo = false)
	{
		// BOTH PLAYERS' PAUSE SCREENS NAME `PauseMenu`, so the variant cannot be chosen by the
		// population script; it is the fact the build states. Godot owns the list either way — the
		// guest's spawn capture is not the authority on what this menu contains. What the guest
		// supplies is the availability mask, applied by ApplyTabAvailability below, and the commit
		// entry points behind each command.
		return Engine_UiTemplates.Menu(RootTemplate)?.Build(Engine_UiRowConditions.PauseFacts(playerTwo)) ??
			new Engine_FrontEndMenu();
	}

	/// <summary>The template the pause list is built from, for both players.</summary>
	public const string RootTemplate = "pause-root";

	/// <summary>
	/// Applies the bridge's availability mask (bit N = tab N available) to the tab entries.
	/// True when anything changed, so the caller knows to refresh its view.
	///
	/// The mask is `sub_826B83D0`, the predicate the guest's LB/RB nav loops, read out of the
	/// hero's restriction flags. The push lever never re-checks it, so a ghosted entry must
	/// never dispatch.
	///
	/// Ghosted, not removed: `sub_8298F648` spawns every tab then calls
	/// `sub_823DF660(0, name, ghost, 1)` (`SetItemGhosting`) for Map, Abilities, Items, Weapons,
	/// Clothing and Logbook from the hero's bytes +52/+51/+45 and the childhood test. The tab
	/// cycler skips a ghosted row; the list does not.
	/// </summary>
	public static bool ApplyTabAvailability(Engine_FrontEndMenu menu, uint mask)
	{
		bool changed = false;
		changed |= menu.SetGhosted("Map", (mask & (1u << (int)TabMap)) == 0);
		changed |= menu.SetGhosted("Abilities", (mask & (1u << (int)TabAbilities)) == 0);
		changed |= menu.SetGhosted("Items", (mask & (1u << (int)TabItems)) == 0);
		changed |= menu.SetGhosted("Weapons", (mask & (1u << (int)TabWeapons)) == 0);
		changed |= menu.SetGhosted("Clothing", (mask & (1u << (int)TabClothing)) == 0);
		changed |= menu.SetGhosted("Logbook", (mask & (1u << (int)TabLogbook)) == 0);
		changed |= menu.SetGhosted("Options", (mask & (1u << (int)TabOptions)) == 0);
		return changed;
	}

	/// <summary>
	/// The Save rows' ghost, as the guest's pause list states it on every tick it is up: its vf+8
	/// (`sub_8298DEF0`, disc 0x828427B8) ends in `sub_8298F578(list, sub_82448900(SaveMgr))`, which is
	/// `SetItemGhosting(name, held == 0)` for "Save and Continue" and "Save and Quit" (0x8298F5D8 /
	/// 0x8298F61C). The guest names those rows by their localised text, so they are joined by their
	/// place under "Saving" (ids 0 and 1, `sub_8298DF98`), never by name. Both of Godot's Save and
	/// Quit rows take the guest's one. True when anything changed.
	/// </summary>
	public static bool ApplyStatedSaveGhosts(
		Engine_FrontEndMenu menu, System.Collections.Generic.IReadOnlyList<Engine_UiMenuModel.Row> stated)
	{
		bool changed = false;
		foreach (Engine_UiMenuModel.Row row in stated)
		{
			if (row.Parent != "Saving")
			{
				continue;
			}

			if (row.Id == 0u)
			{
				changed |= menu.SetGhosted("SaveAndContinue", row.Ghosted);
			}
			else if (row.Id == 1u)
			{
				changed |= menu.SetGhosted("SaveAndQuit", row.Ghosted);
				changed |= menu.SetGhosted("SaveAndQuitToDesktop", row.Ghosted);
			}
		}

		return changed;
	}
}
