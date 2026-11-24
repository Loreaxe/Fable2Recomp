namespace Refii.Titles.Fable2;

/// <summary>The credits roll's numbers (`sub_826B79D8` / `sub_826B7D00`): 24 slots 32 units apart,
/// 30 units a second. Pure, so the roll's end is testable without the engine.</summary>
public static class Engine_UiCreditsTiming
{
	public const float Pitch = 32f;

	public const float Speed = 30f;

	public const int Slots = 24;

	/// <summary>
	/// When the guest's roll ends: its update hands line n (counter `h[6]`, from 0) at the n-th
	/// wrap, at 32·n units of travel, and quits once the counter reaches lines + 24 (`sub_826B7D00`
	/// → `sub_82370178`, 0x826B7E3C) — wrap lines + 23.
	/// </summary>
	public static double EndSeconds(int lines) => Pitch * (lines + Slots - 1) / (double)Speed;
}
