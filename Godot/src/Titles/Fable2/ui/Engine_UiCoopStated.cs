namespace Refii.Titles.Fable2;

using System.Collections.Generic;
using System.Globalization;
using Refii.Ui;

/// <summary>
/// The co-op screen (24) as its own calls STATE it (hooks/gui_headless_presentation.cpp): the enter
/// (`@Coop.Enter`, which clears both pages' accepted and the closing byte), each page's SetAccepted
/// (`@Coop.Accepted`), the finish (`@Coop.Closing`), and the save picker's open, shown flag and
/// cursor (`@CoopPicker.Open` / `.Shown` / `.Cursor`). The right page's focus is the row cursor's
/// statement on its pickboxes (<see cref="Engine_UiRowWidgets"/>). Nothing here reads the guest.
/// </summary>
public static class Engine_UiCoopStated
{
	/// <summary>The screen's state; null until the screen has been entered.</summary>
	public readonly record struct State(
		bool LeftAccepted, int RightFocus, bool RightAccepted, bool PickerOpen, int PickerCursor,
		IReadOnlyList<(string Text, bool Valid)> Saves, int FirstCard, bool Closing);

	private const string RightPickbox = "Foreground.RightPage.Pickbox.pickbox";

	private static uint _screen;
	private static bool _leftAccepted;
	private static bool _rightAccepted;
	private static bool _closing;
	private static bool _pickerShown;
	private static int _pickerCursor;
	private static int _firstCard;
	private static List<(string Text, bool Valid)> _saves = new();

	/// <summary>The screen's state as last stated, or null before its first enter.</summary>
	public static State? Current =>
		_screen == 0u
			? null
			: new State(_leftAccepted, RightFocus(), _rightAccepted, _pickerShown, _pickerCursor, _saves,
				_firstCard, _closing);

	/// <summary>`@Coop.Enter`: screen (hex). A different screen object starts with the picker closed.</summary>
	public static void Enter(Engine_UiModel.GuiNativeCall call)
	{
		uint screen = Hex(call.Argument(0).Text);
		if (screen != _screen)
		{
			_pickerShown = false;
			_pickerCursor = 0;
			_firstCard = 0;
			_saves = new List<(string Text, bool Valid)>();
		}

		_screen = screen;
		_leftAccepted = false;
		_rightAccepted = false;
		_closing = false;
	}

	/// <summary>`@Coop.Accepted`: map (hex), the page's name prefix, 0|1.</summary>
	public static void Accepted(Engine_UiModel.GuiNativeCall call)
	{
		string page = call.Argument(1).Text;
		bool accepted = call.Argument(2).Text == "1";
		if (page.Contains("RightPage", System.StringComparison.Ordinal))
		{
			_rightAccepted = accepted;
		}
		else if (page.Contains("LeftPage", System.StringComparison.Ordinal))
		{
			_leftAccepted = accepted;
		}
	}

	/// <summary>`@Coop.Closing`: screen (hex).</summary>
	public static void Closing(Engine_UiModel.GuiNativeCall call) => _closing = true;

	/// <summary>`@CoopPicker.Shown`: screen (hex), 0|1.</summary>
	public static void PickerShown(Engine_UiModel.GuiNativeCall call) =>
		_pickerShown = call.Argument(1).Text == "1";

	/// <summary>`@CoopPicker.Cursor`: screen (hex), the save the cursor is on.</summary>
	public static void PickerCursor(Engine_UiModel.GuiNativeCall call) =>
		_pickerCursor = Int(call.Argument(1).Text);

	/// <summary>
	/// `@CoopPicker.Open`: screen (hex), first card, saves, cursor, a bit per save whose header
	/// parsed, then each save's card text.
	/// </summary>
	public static void PickerOpen(Engine_UiModel.GuiNativeCall call)
	{
		_firstCard = Int(call.Argument(1).Text);
		int count = Int(call.Argument(2).Text);
		_pickerCursor = Int(call.Argument(3).Text);
		uint valid = (uint)Int(call.Argument(4).Text);
		var saves = new List<(string Text, bool Valid)>(count);
		for (int i = 0; i < count && i < 32; ++i)
		{
			saves.Add((call.Argument(5 + i).Text, (valid & (1u << i)) != 0u));
		}

		_saves = saves;
	}

	/// <summary>The right page's focused row: the pickbox the row cursor is on, -1 for none.</summary>
	private static int RightFocus()
	{
		for (int k = 0; k < 3; ++k)
		{
			if (Engine_UiRowWidgets.IsSelected(RightPickbox + (k + 1).ToString(CultureInfo.InvariantCulture)))
			{
				return k;
			}
		}

		return -1;
	}

	private static uint Hex(string text) =>
		uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value) ? value : 0u;

	private static int Int(string text) =>
		int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;
}
