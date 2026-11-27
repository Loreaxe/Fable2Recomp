using System.Collections.Generic;

using Refii.Ui;

namespace Refii.Titles.Fable2;

using Refii.Host;

/// <summary>
/// <see cref="Engine_UiSource"/> and Fable 2's own reads (<see cref="Fable2UiSource"/>) answered by the live guest through <see cref="HostBridge"/>.
/// Pure pass-through: no caching, no gating.
/// </summary>
public sealed class HostUiSource : Fable2UiSource
{
	/// <summary>Makes the live guest the UI's source for the rest of the session.</summary>
	public static void Install() => Current = new HostUiSource();

	public override bool IsLive => true;

	public override int ActingPlayer =>
		Fable2ActingPlayer.Of(CoopSession.IsClient, CoopSession.LocalCoop, Fable2Input.MenuOwner);

	public override List<Engine_UiModel.Presentation> GetPresentations() =>
		HostBridge.GetPresentations();

	public override Engine_UiModel.ScreenFade GetScreenFade() => HostBridge.GetScreenFade();

	public override Engine_UiModel.InGameScreens? GetInGameScreens() =>
		HostBridge.GetInGameScreens();

	public override List<Engine_UiModel.MenuRow> GetMenuRows(out ulong generation) =>
		HostBridge.GetMenuRows(out generation);

	public override uint MenuScreenId() => HostBridge.GetMenuScreenId();

	public override Engine_UiModel.MenuBoxState? GetMenuBox() => Engine_UiDisplayBoxes.MenuState();

	public override FrontEndDialogState? GetFrontEndDialog() =>
		HostBridge.TryGetFrontEndDialog(out bool accepting, out bool title) ? new FrontEndDialogState(accepting, title) : null;

	/// <summary>
	/// The screen the showing box stands on, by the guest's own mapping — the class's type (vtable
	/// slot 7, `li r3,N`, the kind its Open stated) through `sub_823D0B90`: 0 → 3, 2 → 19, 3 → 4,
	/// 4 → 5, 5 → 6, 6..8 → 7. Screen 7 (`displaybox.bgf`, the `infocard` book) stands only for the
	/// hold-to-accept card (8): the acceptance and button boxes (6, 7) are the display-box panel's
	/// (Engine_UiStatedSurface.DisplayBox), and standing the book for them too drew them twice.
	/// </summary>
	public override uint DisplayBoxScreen() => ScreenOfBox(Engine_UiDisplayBoxes.Showing?.Kind ?? -1);

	/// <summary>The screen a box of <paramref name="kind"/> stands on, <see cref="uint.MaxValue"/> for none.</summary>
	public static uint ScreenOfBox(int kind) => kind switch
	{
		Engine_UiDisplayBoxes.Message => 3u,
		Engine_UiDisplayBoxes.Query => 19u,
		Engine_UiDisplayBoxes.Adjuster => 4u,
		Engine_UiDisplayBoxes.Quest => 5u,
		Engine_UiDisplayBoxes.Intermission => 6u,
		Engine_UiDisplayBoxes.AcceptHold => 7u,
		_ => uint.MaxValue,
	};

	public override Engine_UiModel.PauseState? GetPauseState() => HostBridge.GetPauseState();

	public override List<Engine_UiModel.MapMarker> GetMapMarkers() => HostBridge.GetMapMarkers();

	public override Engine_UiModel.SpellAssignState GetSpellAssign() => HostBridge.GetSpellAssign();

	public override Engine_UiModel.SpellWheelState GetSpellWheel(int player) => HostBridge.GetSpellWheel(player);

	public override Engine_UiModel.SpellWheelState GetSpellBlade() => HostBridge.GetSpellBlade();

	public override bool FillHud(Dictionary<uint, float> meters,
		Dictionary<uint, Refii.Ui.Engine_UiSource.HudPulser> pulsers, Dictionary<uint, string> texts,
		Dictionary<uint, float> scriptMeters, Dictionary<uint, uint> scriptTexts)
	{
		// The meters and the texts are the registry writers' own statements (`@Gui.Meter`,
		// `@Gui.Text`, gui_registry_hooks.cpp); nothing is read for them.
		meters.Clear();
		texts.Clear();
		// The pulsers run here from the guest's own start and stop (`@Gui.Pulser`).
		Engine_UiHudPulsers.Fill(pulsers, Refii.Entities.Engine_DrawnFrame.NowUsec);
		// The script store's meters and texts reach the registry through the same writers: its
		// setters (`sub_823C59E0`, `sub_823C5B00`) call SeekMeter and SetText, and the save loader
		// re-issues every stored one through them (`sub_823C4A88` 0x823c5154, 0x823c5288).
		scriptMeters.Clear();
		scriptTexts.Clear();
		return true;
	}

	public override Engine_UiModel.SaveState GetSaveState() => HostBridge.GetSaveState();

	public override List<Engine_UiModel.Language> GetLanguages() => HostBridge.GetLanguages();

	public override Engine_UiModel.AchievementUnlock[] GetAchievementUnlocks(
		ulong afterSequence, int max = 16) =>
		HostBridge.GetAchievementUnlocks(afterSequence, max);

	public override byte[]? GetTitleXdbf() => HostBridge.GetTitleXdbf();

	public override void SetSaveAchievementUnlocks(string table) => HostBridge.SetSaveAchievementUnlocks(table);

	public override System.Collections.Generic.List<Engine_UiModel.AchievementBinding> GetAchievementBindings(ulong afterSerial) =>
		HostBridge.GetAchievementBindings(afterSerial);

	public override uint GetAchievementsUiRequests() => HostBridge.GetAchievementsUiRequests();

	public override List<Engine_UiModel.GuiNativeCall> TakeGuiNativeCalls() =>
		HostBridge.TakeGuiNativeCalls();

	public override (ulong Intercepted, uint Pending, uint Named) GuiNativeCounters() =>
		HostBridge.GuiNativeCounters();

	public override List<Engine_UiModel.MenuChunk> TakeMenuChunks() =>
		HostBridge.TakeMenuChunks();

	public override bool MenuPopulating() => HostBridge.MenuPopulating();

	public override void SetPredicate(string name, bool value) =>
		HostBridge.SetGuiPredicate(name, value);

	public override void SetPredicate(string name, float value) =>
		HostBridge.SetGuiPredicate(name, value);

	public override void SuppressNative(string name) => HostBridge.SuppressGuiNative(name);

	public override uint PostFrontEndAction(string name) => HostBridge.PostFrontEndAction(name);

	public override uint ScreenAction(uint code) => HostBridge.ScreenAction(code);

	public override uint MenuEvent(string name, int eventType, uint id) =>
		HostBridge.MenuEvent(name, eventType, id);

	public override void StateHighlightedRow(uint screen, int eventType, int id, string name, string label) =>
		HostBridge.SetHighlightedRow(screen, eventType, id, name, label);

	public override void StateListFoldDepth(uint screen, uint depth) =>
		HostBridge.SetListFoldDepth(screen, depth);


	public override bool PauseTabSelect(uint tab) => HostBridge.PauseTabSelect(tab);

	public override uint AssignSpell(int slot, int spell, int player = 1) => HostBridge.AssignSpell(slot, spell, player);
}
