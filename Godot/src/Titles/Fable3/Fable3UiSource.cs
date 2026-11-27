namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Refii.Host;
using Refii.Ui;

/// <summary>
/// Fable 3's UI statements, for the shared UI: what its host bridge states, forwarded. Every
/// member here is an export the bridge answers (Fable3/src/hostbridge/src/hud_feed.cpp,
/// options_feed.cpp, and the shared core's achievement seam); the rest keep the base answer of
/// "nothing" until Fable 3 states them.
/// </summary>
public sealed class Fable3UiSource : Engine_UiSource
{
	public static void Install() => Current = new Fable3UiSource();

	public override bool IsLive => true;

	public override Engine_UiModel.ScreenFade GetScreenFade() => HostBridge.GetScreenFade();

	public override Dictionary<uint, float> GetHudMeters() => HostBridge.GetHudMeters();

	/// <summary>The HUD's pulsers, registry [4] (hud_feed.cpp: RefiiHostGetHudPulserStates).</summary>
	public override Dictionary<uint, Engine_UiSource.HudPulser> GetHudPulsers() => HostBridge.GetHudPulsers();

	public override Dictionary<uint, string> GetHudTexts() => HostBridge.GetHudTexts();

	/// <summary>The guest's language table (options_feed.cpp: RefiiHostGetLanguages), Fable 2's layout at Fable 3's addresses.</summary>
	public override List<Engine_UiModel.Language> GetLanguages() => HostBridge.GetLanguages();

	// ---- ACHIEVEMENTS: the shared core's seam (src/hostcore/src/host_exports.cpp), one per title ----

	public override Engine_UiModel.AchievementUnlock[] GetAchievementUnlocks(ulong afterSequence, int max = 16) =>
		HostBridge.GetAchievementUnlocks(afterSequence, max);

	public override byte[]? GetTitleXdbf() => HostBridge.GetTitleXdbf();

	public override void SetSaveAchievementUnlocks(string table) => HostBridge.SetSaveAchievementUnlocks(table);

	public override System.Collections.Generic.List<Engine_UiModel.AchievementBinding> GetAchievementBindings(ulong afterSerial) =>
		HostBridge.GetAchievementBindings(afterSerial);

	public override uint GetAchievementsUiRequests() => HostBridge.GetAchievementsUiRequests();

	/// <summary>
	/// The `GUI.*` calls Fable 3's game VM made since the last take, with their arguments: the
	/// UI cut's seam (Fable3/src/hooks/src/gui_natives.cpp posts, the shared core drains).
	/// </summary>
	public override List<Engine_UiModel.GuiNativeCall> TakeGuiNativeCalls() => HostBridge.TakeGuiNativeCalls();

	public override (ulong Intercepted, uint Pending, uint Named) GuiNativeCounters() => HostBridge.GuiNativeCounters();
}
