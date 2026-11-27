namespace Refii.Titles.Fable3;

/// <summary>
/// FABLE 3'S GAME ACTIONS, the codes its input mapper hands every pad handler — the game's own table
/// `EGameAction` (the shipped `scripts/miscellaneous/gameactionenum.lua`): each lever that hands an
/// action to a guest handler names it by these (emotion_choice_answer.cpp, hud_dpad_use.cpp,
/// interaction_mode_input.cpp). Only the codes the HUD's levers send are listed.
/// </summary>
public static class Fable3GameActions
{
	// GAME_ACTION_GUI_UP .. _RIGHT and their _RELEASED.
	public const int GuiUp = 29, GuiDown = 30, GuiLeft = 31, GuiRight = 32;
	public const int GuiUpReleased = 33, GuiDownReleased = 34, GuiLeftReleased = 35, GuiRightReleased = 36;

	// GAME_ACTION_GUI_A / B / X / Y, their _RELEASED (X first in the table) and _HELD (A, B, X, Y).
	public const int GuiA = 74, GuiB = 75, GuiX = 76, GuiY = 77;
	public const int GuiXReleased = 78, GuiAReleased = 79, GuiBReleased = 80, GuiYReleased = 81;
	public const int GuiAHeld = 82, GuiBHeld = 83, GuiXHeld = 84, GuiYHeld = 85;

	// GAME_ACTION_GUI_LEFT_TRIGGER / RIGHT_TRIGGER / LEFT_BUMPER / RIGHT_BUMPER, their _HELD and _RELEASED.
	public const int GuiLeftTrigger = 86, GuiRightTrigger = 87, GuiLeftBumper = 88, GuiRightBumper = 89;
	public const int GuiLeftTriggerHeld = 90, GuiRightTriggerHeld = 91, GuiLeftBumperHeld = 92, GuiRightBumperHeld = 93;
	public const int GuiLeftTriggerReleased = 94, GuiRightTriggerReleased = 95, GuiLeftBumperReleased = 96, GuiRightBumperReleased = 97;

	// GAME_ACTION_EMOTION_UI_CHOICE_1..5_PRESSED and _RELEASED.
	public const int EmotionChoice1Pressed = 103;
	public const int EmotionChoice1Released = 108;

	/// <summary>The pressed code of emotion choice <paramref name="choice"/> (1..5).</summary>
	public static int EmotionPressed(int choice) => EmotionChoice1Pressed + choice - 1;

	/// <summary>The released code of emotion choice <paramref name="choice"/> (1..5).</summary>
	public static int EmotionReleased(int choice) => EmotionChoice1Released + choice - 1;
}
