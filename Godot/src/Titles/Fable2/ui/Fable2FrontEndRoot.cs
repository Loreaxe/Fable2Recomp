namespace Refii.Titles.Fable2;

using Refii.Ui;
using Item = Refii.Ui.Engine_UiExpandableMenu.Item;

/// <summary>
/// Fable 2's front-end root list on the shared <see cref="Engine_FrontEndMenu"/>: its template,
/// the guest's availability rules for its entries (`sub_826E2220`) and New Game's storage guards
/// (`sub_826DFD58`).
/// </summary>
public static class Fable2FrontEndRoot
{
	/// <summary>
	/// Builds the root menu from its TEMPLATE — `sub_826E2220`'s spawn order as data, with
	/// "New Game" OPENING the gender card rather than dispatching (posting the bare name
	/// consumes the boy/girl answer unasked and starts a male hero silently — see
	/// <see cref="Engine_FrontEndCardChoice"/>). A `user://ui/templates.json` override
	/// replaces the record whole, which is how a mod extends the front end without code.
	/// </summary>
	public static Engine_FrontEndMenu BuildRoot() =>
		Engine_UiTemplates.Menu(RootTemplate)?.Build() ?? new Engine_FrontEndMenu();

	/// <summary>The template the front end's root list is built from.</summary>
	public const string RootTemplate = "frontend-root";

	/// <summary>
	/// The gender card <c>New Game</c> opens — <see cref="Engine_FrontEndCardChoice"/>. A name
	/// rather than a reference: the menu is the model and knows nothing about node trees.
	/// </summary>
	public const string HeroCardScreen = "HeroCard";

	/// <summary>
	/// The saved-game slot cap, for a caller with no guest to read it from. Six, not the 150 at
	/// `dword_83319620`: `sub_82378618` (front-end enter, vtable slot 0x820A9260) stores 6 over
	/// it unconditionally before the menu can dispatch.
	/// </summary>
	public const int DefaultSaveLimit = 6;

	/// <summary>The guest's own text tag for a New Game with no profile signed in.</summary>
	public const string NoProfileWarning = "GUI_NO_PROFILE_SELECTED_WARNING";

	/// <summary>The guest's own text tag for a New Game with every save slot used.</summary>
	public const string SaveLimitWarning = "GUI_REACHED_SAVE_GAME_LIMIT_WARNING";

	/// <summary>
	/// <c>New Game</c>'s guards, run BEFORE the card appears: 0x826E01FC, the switch to the
	/// `choosecard` element, is the success arm of the third guard (<see cref="NewGameRefusal"/>).
	/// </summary>
	public static (bool Guards, string? Refusal) OpenGuard(Engine_FrontEndMenu menu, Item item) =>
		item.Opens == HeroCardScreen ? (true, NewGameRefusal(menu.Saves)) : (false, null);

	/// <summary>
	/// The guest's own New Game guards — `sub_826DFD58` from 0x826E0040 — two of its three:
	///
	///   0x826E0078  `(1 &lt;&lt; *0x83496AF8) &amp; *0x83496A70` — a profile is signed in.
	///   0x826E0094  `sub_82441CB8(saveMgr, user, 1)` — a storage device is selected.
	///   0x826E0148  `sub_824431E8(...) &lt; *0x83319620` — a slot is free.
	///
	/// The middle one is deliberately absent: `sub_82441CB8` puts up the console device picker
	/// (`j_XamShowDeviceSelectorUI` then a blocking `XGetOverlappedResult(bWait=1)`), and its
	/// failure text GUI_NO_DEVICE_SELECTED_WARNING names a state this port cannot enter.
	///
	/// The count is a substitution: the third guard uses `sub_824431E8`, which enumerates the
	/// SELECTED device, and our sample comes from `sub_82443308`, which enumerates all of them.
	/// They agree only with one device, which follows from skipping the picker. `sub_824431E8`
	/// reads a DeviceID only the blocking picker ever writes, so it would return zero.
	///
	/// The limit matters because the write side (`sub_82445050` at 0x82445108) makes the same
	/// comparison against 6 and returns 0 silently; this dialog is the only feedback.
	/// </summary>
	private static string? NewGameRefusal(Engine_UiModel.SaveState saves)
	{
		if (!saves.SignedIn)
		{
			return NoProfileWarning;
		}

		return saves.AtSaveLimit ? SaveLimitWarning : null;
	}

	/// <summary>
	/// Applies the guest's own availability rules.
	///
	/// Load Game's spawn guard at 0x826E2264 is `signedIn &amp;&amp; sub_82443308() != 0` — a live
	/// XamContentCreateEnumerator over content type 1, DeviceID 0. The save-name vector at the
	/// front-end screen's +84/+88 is NOT that answer: it is filled only inside the dispatcher's
	/// "Load Game" branch (`sub_82443048` at 0x826E03CC) and is empty at menu-build time.
	///
	/// <paramref name="saves"/> therefore carries the pump's sample and distinguishes "not asked
	/// yet" from "none" — see <see cref="Engine_UiModel.SaveState"/>.
	/// </summary>
	public static void ApplyAvailability(Engine_FrontEndMenu menu,
		int languageCount, Engine_UiModel.SaveState saves,
		System.Collections.Generic.IReadOnlyList<Engine_UiModel.Language>? languages = null)
	{
		foreach (Item item in menu.Roots)
		{
			item.Available = item.Name switch
			{
				// Held back until the pump has answered, rather than asserting no saves.
				"Load Game" => saves.CanLoad,
				// Spawned only above one installed language (`lwz r11, dword_83319934` /
				// `cmplwi r11, 1` / `ble` past the spawn).
				"Language" => languageCount > 1,
				_ => item.Available,
			};
		}

		// Last known state wins over unknown: the bridge answers count -1 when a sample lands
		// badly, and letting that overwrite a known 6/6 slips a New Game press past the
		// save-limit refusal.
		if (saves.Count >= 0 || menu.Saves.Count < 0)
		{
			menu.Saves = saves;
		}

		PopulateLanguages(menu, languages);
		menu.Relayout();
	}

	/// <summary>
	/// Fills "Language" with one child per installed language, as `sub_826E1C98` does: it walks
	/// the guest's 96-byte-stride table and spawns display NAME as the item name, group
	/// "Language", command 302, index = the row.
	///
	/// The guest's own asymmetry: children spawn whenever the table has entries, but the
	/// "Language" PARENT only appears above one installed language, so a single-language install
	/// has an unreachable child list — <see cref="ApplyAvailability"/> reproduces that.
	///
	/// The list is the caller's, like the save state: reading it means reading guest memory, and
	/// this class must be constructible with no guest behind it.
	/// </summary>
	private static void PopulateLanguages(Engine_FrontEndMenu menu,
		System.Collections.Generic.IReadOnlyList<Engine_UiModel.Language>? languages)
	{
		if (languages is null || menu.Find("Language") is not { } language)
		{
			return;
		}

		language.Children.Clear();

		foreach (Engine_UiModel.Language installed in languages)
		{
			// The NAME is the guest's display string and doubles as the text tag; the front end
			// puts it through Babel and an unresolved tag renders verbatim.
			language.Children.Add(new Item(installed.Name, installed.Name, "Language")
			{
				Command = $"Language:{installed.Index}",

				// `sub_826E1C98` marks the current language with SetItemEmphasis(1).
				Emphasised = installed.IsCurrent,
			});
		}

		// Availability is NOT decided here: ApplyAvailability owns it, from the guest's own
		// statement (`model.HasLanguageEntry`, the same `dword_83319934 > 1` condition
		// `sub_826E2220` uses). One flag, one owner.
	}
}
