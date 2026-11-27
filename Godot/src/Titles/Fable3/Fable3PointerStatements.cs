namespace Refii.Titles.Fable3;

using System.Collections.Generic;

/// <summary>
/// The guest's words that one of its surfaces has taken the pad from the heroes, for the shared
/// input (<see cref="Fable3Input.WorldHasInput"/>): the cursor is free under any of them and the
/// world's levers (<see cref="Fable3HudInput"/>) are not handed.
/// </summary>
/// <remarks>
/// `@GameMode.Ui(on)` — the presented game's top mode is CGameModeUI (game_state_hooks.cpp: a
/// box's pause form TU3 sub_82958388 / disc sub_8291F0C0, the menu box's open, the save menu's
/// open; popped by id 8). `@GameMode.Gui(on)` — its top is the GUI's pause, pushed by
/// lhGuiManager_EnterGuiMode for a GUI screen (the epilogue, the credits, the stats): the GUI
/// input's router hands the action to the screen and skips the dispatcher. Both are stated by the
/// mode stack's push and pop. `@Box.Hold(box, held)` — a box's Show put the control restriction
/// "DisBox" on the heroes and its Hide lifted it (box_hold_statements.cpp: BoxShown TU3
/// sub_82958550 / disc sub_8291F288, BoxHidden TU3 sub_82958710 / disc sub_8291F3F0). The HUD
/// root's hide (`@Hud.Root`, <see cref="Fable3Elements.HudHidden"/>) is a drawing call only 17
/// Shows make; these two are the holds themselves.
/// </remarks>
public static class Fable3PointerStatements
{
	private static readonly HashSet<uint> Holding = new();

	/// <summary>The presented game's top mode is the UI mode.</summary>
	public static bool UiMode { get; private set; }

	/// <summary>The presented game's top mode is the GUI's pause: a GUI screen has the game.</summary>
	public static bool GuiMode { get; private set; }

	/// <summary>A box holds the heroes' controls.</summary>
	public static bool BoxHolds => Holding.Count != 0;

	/// <summary>A guest surface has the pad: the UI mode or the GUI's pause is on top, or a box holds the heroes.</summary>
	public static bool GuestHoldsThePad => UiMode || GuiMode || BoxHolds;

	/// <summary>`@GameMode.Ui(on)`. True when it changed.</summary>
	public static bool StateUiMode(bool on)
	{
		if (UiMode == on)
		{
			return false;
		}

		UiMode = on;
		return true;
	}

	/// <summary>`@GameMode.Gui(on)`. True when it changed.</summary>
	public static bool StateGuiMode(bool on)
	{
		if (GuiMode == on)
		{
			return false;
		}

		GuiMode = on;
		return true;
	}

	/// <summary>`@Box.Hold(box, held)`. True when it changed.</summary>
	public static bool StateBoxHold(uint box, bool held) => held ? Holding.Add(box) : Holding.Remove(box);

	/// <summary>
	/// The screen manager was destroyed with its boxes (`@Screens.Destroyed`): a box freed with it
	/// never ran its Hide, and the heroes it held are gone with their world.
	/// </summary>
	public static void ScreensDestroyed() => Holding.Clear();

	/// <summary>Forgets everything (a new session; the tests).</summary>
	public static void Clear()
	{
		Holding.Clear();
		UiMode = false;
		GuiMode = false;
	}
}
