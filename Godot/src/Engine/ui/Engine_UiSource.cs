using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// The UI model seam: the game installs a guest-backed source, the editor an authored one.
/// Every read has an absent answer (null / empty / 0); every lever returns the tick pump's
/// queue depth, never an outcome.
/// </summary>
public abstract class Engine_UiSource
{
	/// <summary>The source this session reads. Defaults to the authored stand-in; the game installs the guest-backed source once at startup.</summary>
	public static Engine_UiSource Current { get; set; } = new Engine_UiAuthoredSource();

	/// <summary>True when backed by a live guest. For reports only, never for logic.</summary>
	public virtual bool IsLive => false;

	// ---- THE SCREENS AND THEIR ELEMENTS ------------------------------------------------

	/// <summary>Every live presentation: address, root, name, state, active element count.</summary>
	public virtual List<Engine_UiModel.Presentation> GetPresentations() => new();

	/// <summary>The screen fade the guest is driving.</summary>
	public virtual Engine_UiModel.ScreenFade GetScreenFade() => default;

	/// <summary>Which in-game screens the guest considers current.</summary>
	public virtual Engine_UiModel.InGameScreens? GetInGameScreens() => null;

	// ---- THE MENUS ---------------------------------------------------------------------

	/// <summary>The spawned rows' identities, with the generation that dates them.</summary>
	public virtual List<Engine_UiModel.MenuRow> GetMenuRows(out ulong generation)
	{
		generation = 0;
		return new List<Engine_UiModel.MenuRow>();
	}

	/// <summary>
	/// The screen the captured rows belong to, or <see cref="Engine_UiMenuSources.AnyScreen"/>
	/// when no in-game screen states one. The capture is a single list; this is what says whose
	/// population it is. See <see cref="Engine_UiMenuSources.BoundScreen"/>.
	/// </summary>
	public virtual uint MenuScreenId() => Engine_UiMenuSources.AnyScreen;

	/// <summary>The Lua-opened option box, or null when none is open.</summary>
	public virtual Engine_UiModel.MenuBoxState? GetMenuBox() => null;

	/// <summary>The address of the display box showing, or 0: the box an answer is addressed to —
	/// the newest one its class's Open stated (<see cref="Engine_UiStatedBoxes.Showing"/>).</summary>
	public virtual uint DisplayBoxAddress() => Engine_UiStatedBoxes.Current.Showing?.Address ?? 0u;

	/// <summary>
	/// The screen id the showing display box stands on, <see cref="uint.MaxValue"/> when none.
	/// A box is never the manager's current screen, so a template for a box's screen (the quest
	/// card, 5) is stood up from this instead.
	/// </summary>
	public virtual uint DisplayBoxScreen() => uint.MaxValue;

	/// <summary>The pause gate: underlying screen id, pending modal id and mode, current tab.</summary>
	public virtual Engine_UiModel.PauseState? GetPauseState() => null;

	/// <summary>The pause-root minimap's quest markers.</summary>
	public virtual List<Engine_UiModel.MapMarker> GetMapMarkers() =>
		new List<Engine_UiModel.MapMarker>();

	// ---- THE HUD -----------------------------------------------------------------------

	/// <summary>Meter values by FNV-1 name hash.</summary>
	public virtual Dictionary<uint, float> GetHudMeters() => new();

	/// <summary>
	/// A HUD pulser as the guest holds it: its loop phase 0..1, and whether it runs. A stopped one
	/// (the guest's mode 2) stays at its phase — 0, the tick the stop applied — until started again.
	/// </summary>
	public readonly record struct HudPulser(float Phase, bool Running);

	/// <summary>The HUD's pulsers by `FNV1(PulserName)`. See the bridge.</summary>
	public virtual Dictionary<uint, HudPulser> GetHudPulsers() => new();

	/// <summary>Text values by FNV-1 name hash.</summary>
	public virtual Dictionary<uint, string> GetHudTexts() => new();


	/// <summary>The Lua-owned text channel — the other of the HUD's two.</summary>
	public virtual Dictionary<uint, uint> GetHudScriptTexts() => new();

	/// <summary>The Lua-owned meter channel.</summary>
	public virtual Dictionary<uint, float> GetHudScriptMeters() => new();

	/// <summary>
	/// The HUD's five channels read into dictionaries the caller owns and reuses, cleared first;
	/// false when this source only hands out its own (the caller then takes those, as before).
	/// </summary>
	/// <remarks>
	/// The HUD reads every channel once per guest frame. A source that answers with a new
	/// dictionary each time (the guest's) costs five dictionaries and their growth per frame,
	/// and the texts a new string each; filling the reader's own keeps the storage.
	/// </remarks>
	public virtual bool FillHud(Dictionary<uint, float> meters, Dictionary<uint, HudPulser> pulsers,
		Dictionary<uint, string> texts, Dictionary<uint, float> scriptMeters, Dictionary<uint, uint> scriptTexts) => false;

	// ---- THE FRONT END'S OWN STATE -----------------------------------------------------

	/// <summary>Signed-in, saved-game count and the guest's own cap.</summary>
	public virtual Engine_UiModel.SaveState GetSaveState() => default;

	/// <summary>The installed languages, and which one is current.</summary>
	public virtual List<Engine_UiModel.Language> GetLanguages() =>
		new List<Engine_UiModel.Language>();

	/// <summary>The guest's achievement writes newer than <paramref name="afterSequence"/>, oldest first.</summary>
	public virtual Engine_UiModel.AchievementUnlock[] GetAchievementUnlocks(
		ulong afterSequence, int max = 16) =>
		System.Array.Empty<Engine_UiModel.AchievementUnlock>();

	/// <summary>The title's XDBF resource, or null before the module has launched.</summary>
	public virtual byte[]? GetTitleXdbf() => null;

	/// <summary>States every save's unlocked set (<c>save\tid:filetime,...</c> lines) for the guest's per-user enumerator.</summary>
	public virtual void SetSaveAchievementUnlocks(string table)
	{
	}

	/// <summary>The guest's adoption points' bindings newer than <paramref name="afterSerial"/>.</summary>
	public virtual System.Collections.Generic.List<Engine_UiModel.AchievementBinding> GetAchievementBindings(ulong afterSerial) => new();

	/// <summary>How many times the guest has called `XamShowAchievementsUI` this session.</summary>
	public virtual uint GetAchievementsUiRequests() => 0;

	// ---- THE UI CUT --------------------------------------------------------------------

	/// <summary>The `GUI.*` native calls (name plus arguments) made since the last drain; an authored source posts the call a script would have made. See docs/ui_native_cut.md.</summary>
	public virtual System.Collections.Generic.List<Engine_UiModel.GuiNativeCall>
		TakeGuiNativeCalls() => new();

	/// <summary>Intercepted, pending and registration-resolved `GUI` name counts. <c>Named</c> is 0 until the guest's `RegisterGUI` has run.</summary>
	public virtual (ulong Intercepted, uint Pending, uint Named) GuiNativeCounters() =>
		(0UL, 0u, 0u);

	/// <summary>True while a menu population is in flight; consumers hold their last whole population while set. See <see cref="GetMenuRows"/>.</summary>
	public virtual bool MenuPopulating() => false;

	// ---- THE LEVERS --------------------------------------------------------------------
	// Each returns what the bridge returns (queue depth or accepted flag), never an outcome.

	/// <summary>Asks for a front-end action by name.</summary>
	public virtual uint PostFrontEndAction(string name) => 0;

	/// <summary>
	/// The player (1 or 2) whose pad drove the press a lever answers. The guest hands every screen,
	/// modal and box handler this number as r5: its GUI input object's vf+16 (player one's
	/// `sub_822A4E38`: 2 on an online joiner, else 1; local player two's `sub_828EDC88`: 2), read
	/// at 0x82404A90 (modal router) and 0x82404AC4 (box chain). 1 where no co-op exists.
	/// </summary>
	public virtual int ActingPlayer => 1;

	/// <summary>Posts `lever:&lt;player&gt;:rest` with <see cref="ActingPlayer"/>.</summary>
	public uint PostPlayerLever(string lever, string rest) => PostFrontEndAction(PlayerLever(lever, ActingPlayer, rest));

	/// <summary>`lever:&lt;player&gt;:rest` — the form of every lever that carries the acting player.</summary>
	public static string PlayerLever(string lever, int player, string rest) => $"{lever}:{player}:{rest}";

	/// <summary>
	/// Answers the guest's menu box: 0 cancels, 1..N pick that option. Runs the guest's commit
	/// on its thread — `sub_823DC720` (choose) / `sub_823DC2B0` (cancel) — which posts message
	/// event type 34, ExtraData = option index (0 = dismissed).
	/// </summary>
	public uint MenuBoxAnswer(int answer) =>
		PostFrontEndAction($"MenuBoxAnswer:{System.Math.Max(0, answer)}");

	/// <summary>Moves the guest's menu-box highlight to option 1..N without committing.</summary>
	public uint MenuBoxSelect(int option) =>
		PostFrontEndAction($"MenuBoxSelect:{System.Math.Max(0, option)}");

	/// <summary>One action code to whichever display box is up.</summary>
	public enum BoxAction
	{
		/// <summary>Step the numeric adjuster's value up.</summary>
		Up = 28,

		/// <summary>Step it down.</summary>
		Down = 29,

		/// <summary>Dismiss — what the quest box and the message box answer to.</summary>
		Dismiss = 36,

		/// <summary>Confirm. Yes on a query box, accept on the adjuster.</summary>
		Confirm = 46,

		/// <summary>Cancel. No on a query box.</summary>
		Cancel = 47,
	}

	/// <summary>
	/// Runs the guest's own box handler, which posts message event 36 (1/0 for yes/no) or
	/// 37/38 (the adjuster's number). Handlers gate on the box's open byte, so a late action
	/// is a no-op.
	/// </summary>
	public uint DisplayBoxAction(BoxAction action) =>
		DisplayBoxAddress() is var box && box != 0
			? PostPlayerLever("BoxAction", $"{(int)action}@{box:X8}")
			: PostPlayerLever("BoxAction", $"{(int)action}");

	/// <summary>One action code to the current in-game screen's own input method.</summary>
	public virtual uint ScreenAction(uint code) => 0;

	/// <summary>Pushes a menu button event by identity.</summary>
	public virtual uint MenuEvent(string name, int eventType, uint id) => 0;

	/// <summary>States the highlighted row of an in-game screen's list (screen 0 clears) — see
	/// <c>HostBridge.SetHighlightedRow</c>.</summary>
	public virtual void StateHighlightedRow(uint screen, int eventType, int id, string name, string label)
	{
	}

	/// <summary>Selects a pause tab. False when the source refused it.</summary>
	public virtual bool PauseTabSelect(uint tab) => false;

	/// <summary>Commits one spell into one slot on the assignment blade of <paramref name="player"/>'s hero (1 or 2).</summary>
	public virtual uint AssignSpell(int slot, int spell, int player = 1) => 0;
}
