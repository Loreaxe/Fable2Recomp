namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// The screens whose guest handler is ported (S7, gui_screen_stubs.cpp): their slots keep only the
/// guest's statements and game calls, so the book buttons that were spine calls inside the handler's
/// input slot are posted as the spine levers instead — B at the root `PauseBack` (`sub_826B8130` +
/// `sub_826D5AF0`), LB / RB `PauseCycle:&lt;player&gt;:prev|next` (`sub_826B8780` / `sub_826B8878`,
/// player one only, the guest's gate).
/// </summary>
public static class Fable2PortedScreens
{
	/// <summary>The logbook, 17 (TU1 numbering).</summary>
	public const uint Logbook = 17;

	/// <summary>Whether <paramref name="screen"/>'s book buttons are levers rather than handler input.</summary>
	public static bool OwnsBookButtons(uint screen) => screen == Logbook;

	/// <summary>The lever a book button posts on a ported screen, or null when it still goes to the
	/// screen's input (<see cref="Engine_UiScreenInput"/> codes).</summary>
	public static string? Lever(uint screen, uint code, int player) =>
		!OwnsBookButtons(screen) ? null : code switch
		{
			Engine_UiScreenInput.Back => "PauseBack",
			Engine_UiScreenInput.TabPrev => $"PauseCycle:{player}:prev",
			Engine_UiScreenInput.TabNext => $"PauseCycle:{player}:next",
			_ => null,
		};
}
