namespace Refii.Titles.Fable2;

/// <summary>
/// THE FRONT END'S LEVERS: the title's A and the front-end dialog's answers, each the guest's own
/// input slot +12 called with (screen, code, pad) by the pump (guest_tick_pump.cpp
/// DispatchTitleStart, DispatchFrontEndDialog). The pad is the guest pad that pressed, 0..3: the
/// title takes A from any pad and makes that pad the session's (TU1 0x826E30DC / disc 0x826C6370),
/// and the main menu answers only that pad (0x826DEE60 / 0x826C72C8).
/// </summary>
public static class Fable2FrontEndLevers
{
	/// <summary>The title's A (`TitleStart:&lt;pad&gt;`): starts the game in the idle mode, skips the
	/// attract movie in mode 8.</summary>
	public static string TitleStart(int pad) => $"TitleStart:{pad}";

	/// <summary>The front-end dialog's answer (`FrontEndDialog:&lt;pad&gt;:&lt;code&gt;`): 36 accept, 37
	/// cancel.</summary>
	public static string Dialog(int pad, int code) => $"FrontEndDialog:{pad}:{code}";

	/// <summary>The main menu's B (`FrontEndBack:&lt;pad&gt;`) where no host panel answers it: back to the
	/// title from the list, the load fan's close.</summary>
	public static string Back(int pad) => $"FrontEndBack:{pad}";

	/// <summary>
	/// What one A press on the title posts: the dialog's accept while the title's own dialog is
	/// raised and taking it (mode 12), nothing while it is raised and not taking it (the title's
	/// slot ignores every code while `+57` is set, 0x826E3010), else the title's A.
	/// </summary>
	public static string? TitleA(int pad, bool titleDialogRaised, bool dialogAccepting) =>
		titleDialogRaised ? (dialogAccepting ? Dialog(pad, 36) : null) : TitleStart(pad);
}
