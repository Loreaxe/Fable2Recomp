using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// The UI's model without a guest — what an editor, a viewer or a test fills in by hand.
/// Every collection starts empty, so an unfed source gives <see cref="Engine_UiSource"/>'s
/// absent answers. State only, no behaviour; levers land in <see cref="Posted"/> rather than
/// reaching a guest.
/// </summary>
public sealed class Engine_UiAuthoredSource : Engine_UiSource
{
	/// <summary>Presentations the UI should believe are live.</summary>
	public List<Engine_UiModel.Presentation> Presentations { get; } = new();

	/// <summary>Spawned row identities, as a population would have staged them.</summary>
	public List<Engine_UiModel.MenuRow> Rows { get; } = new();

	/// <summary>The generation stamp handed out with <see cref="Rows"/>; bump it on a change.</summary>
	public ulong RowGeneration { get; set; }


	/// <summary>The pause gate, when a preview wants the pause UI to believe it is up.</summary>
	public Engine_UiModel.PauseState? Pause { get; set; }

	/// <summary>Minimap markers for a pause-root preview.</summary>
	public List<Engine_UiModel.MapMarker> Markers { get; } = new();

	/// <summary>HUD meter values by FNV-1 name hash.</summary>
	public Dictionary<uint, float> Meters { get; } = new();

	/// <summary>HUD text values by FNV-1 name hash.</summary>
	public Dictionary<uint, string> Texts { get; } = new();

	/// <summary>The installed languages an authored front end should offer.</summary>
	public List<Engine_UiModel.Language> Languages { get; } = new();

	/// <summary>Signed-in, count and cap, for previewing the save-card list's own gates.</summary>
	public Engine_UiModel.SaveState Saves { get; set; }

	/// <summary>
	/// Every lever the UI pulled, in order, spelled as the pump's queue spells it —
	/// <c>ScreenAction:37</c>, <c>MenuEvent:&lt;name&gt;:&lt;type&gt;:&lt;id&gt;</c>,
	/// <c>PauseTab:3</c>.
	/// </summary>
	public List<string> Posted { get; } = new();

	/// <summary>
	/// `GUI.*` calls to hand the UI on the next drain, as a script would have made them.
	/// </summary>
	public List<Engine_UiModel.GuiNativeCall> GuiCalls { get; } = new();

	public override List<Engine_UiModel.GuiNativeCall> TakeGuiNativeCalls()
	{
		var taken = new List<Engine_UiModel.GuiNativeCall>(GuiCalls);
		GuiCalls.Clear();
		return taken;
	}

	public override List<Engine_UiModel.Presentation> GetPresentations() => Presentations;

	public override List<Engine_UiModel.MenuRow> GetMenuRows(out ulong generation)
	{
		generation = RowGeneration;
		return Rows;
	}

	public override Engine_UiModel.PauseState? GetPauseState() => Pause;

	public override List<Engine_UiModel.MapMarker> GetMapMarkers() => Markers;

	public override Dictionary<uint, float> GetHudMeters() => Meters;

	public override Dictionary<uint, string> GetHudTexts() => Texts;

	public override List<Engine_UiModel.Language> GetLanguages() => Languages;

	public override Engine_UiModel.SaveState GetSaveState() => Saves;

	public override uint PostFrontEndAction(string name)
	{
		Posted.Add(name);
		return (uint)Posted.Count;
	}

	public override uint ScreenAction(uint code) => PostPlayerLever("ScreenAction", $"{code}");

	public override uint MenuEvent(string name, int eventType, uint id) =>
		PostFrontEndAction($"MenuEvent:{name}:{eventType}:{id}");

	public override bool PauseTabSelect(uint tab)
	{
		PostFrontEndAction($"PauseTab:{tab}");

		// False because nothing pushed a screen: the bridge's true means "the guest will act",
		// and the pause list would wait for a tab change that is never coming.
		return false;
	}

	public override uint AssignSpell(int slot, int spell, int player = 1) =>
		PostFrontEndAction(player == 2 ? $"SpellAssign:{slot}:{spell}:2" : $"SpellAssign:{slot}:{spell}");
}
