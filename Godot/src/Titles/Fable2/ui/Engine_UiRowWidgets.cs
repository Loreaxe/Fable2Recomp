namespace Refii.Titles.Fable2;

using System.Collections.Generic;
using System.Globalization;
using Refii.Ui;

/// <summary>
/// A page's row widgets as the guest STATES them (hooks/gui_headless_presentation.cpp), keyed by
/// the element path the widget's base Init was handed (TU1 `sub_82882340`, disc `sub_82886458`),
/// posted as `@Widget.Init`. SetSelected (TU1 `sub_82882440`, disc `sub_82886558`) is the row
/// cursor arriving on or leaving the widget, posted as `@Widget.Select`; a slider's SetValue (TU1
/// `sub_82881E38`, disc `sub_82885F50`) is its clamped 0..1 value, posted as `@Slider.Set`.
/// </summary>
public static class Engine_UiRowWidgets
{
	private static readonly HashSet<string> Selected = new(System.StringComparer.Ordinal);

	private static readonly Dictionary<string, float> Values = new(System.StringComparer.Ordinal);

	/// <summary>`@Widget.Init`: widget (hex), path. A new widget is unselected and holds no value.</summary>
	public static void Init(Engine_UiModel.GuiNativeCall call)
	{
		if (call.Argument(1).Text is { Length: > 0 } path)
		{
			StateInit(path);
		}
	}

	/// <summary>`@Widget.Select`: widget (hex), path, 0|1.</summary>
	public static void Select(Engine_UiModel.GuiNativeCall call)
	{
		if (call.Argument(1).Text is { Length: > 0 } path)
		{
			StateSelected(path, call.Argument(2).Text == "1");
		}
	}

	/// <summary>`@Slider.Set`: slider (hex), path, the value's float bits.</summary>
	public static void SetSlider(Engine_UiModel.GuiNativeCall call)
	{
		if (call.Argument(1).Text is { Length: > 0 } path &&
			uint.TryParse(call.Argument(2).Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint bits))
		{
			StateValue(path, System.BitConverter.UInt32BitsToSingle(bits));
		}
	}

	public static void StateInit(string path)
	{
		Selected.Remove(path);
		Values.Remove(path);
	}

	public static void StateSelected(string path, bool selected)
	{
		if (selected)
		{
			Selected.Add(path);
		}
		else
		{
			Selected.Remove(path);
		}
	}

	public static void StateValue(string path, float value) => Values[path] = value;

	/// <summary>Whether the row cursor is on the widget at <paramref name="path"/>.</summary>
	public static bool IsSelected(string path) => Selected.Contains(path);

	/// <summary>The value last stated for the slider at <paramref name="path"/>, or null.</summary>
	public static float? Value(string path) => Values.TryGetValue(path, out float value) ? value : null;

	/// <summary>Forgets every statement.</summary>
	public static void Clear()
	{
		Selected.Clear();
		Values.Clear();
	}
}
