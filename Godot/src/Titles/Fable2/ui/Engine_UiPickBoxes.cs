namespace Refii.Titles.Fable2;

using System.Collections.Generic;
using System.Globalization;
using Refii.Ui;

/// <summary>
/// Each pickbox's choice as the guest STATES it (hooks/gui_headless_presentation.cpp): Init (TU1
/// `sub_82881FD0`, disc `sub_828860E8`) names the pickbox's text element, `&lt;path&gt;.grp.text`,
/// posted as `@PickBox.Init`; SetIndex (TU1 `sub_828821D0`, disc `sub_828862E8`) stores the index
/// and hands that element the chosen option, posted as `@PickBox.Set` with the path, the index and
/// the text. Keyed by the element path, so a screen reads its pickboxes by the paths it lays out.
/// </summary>
public static class Engine_UiPickBoxes
{
	private static readonly Dictionary<string, (int Index, string Text)> Stated = new(System.StringComparer.Ordinal);

	/// <summary>`@PickBox.Init`: pickbox (hex), text path. The element holds no option yet.</summary>
	public static void Init(Engine_UiModel.GuiNativeCall call)
	{
		if (call.Argument(1).Text is { Length: > 0 } path)
		{
			Stated.Remove(path);
		}
	}

	/// <summary>`@PickBox.Set`: pickbox (hex), text path, index, text.</summary>
	public static void Set(Engine_UiModel.GuiNativeCall call)
	{
		if (call.Argument(1).Text is { Length: > 0 } path &&
			int.TryParse(call.Argument(2).Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
		{
			State(path, index, call.Argument(3).Text ?? string.Empty);
		}
	}

	/// <summary>The pickbox text element at <paramref name="path"/> was set to option <paramref name="index"/>.</summary>
	public static void State(string path, int index, string text) => Stated[path] = (index, text);

	/// <summary>The caption last stated for the pickbox text element at <paramref name="path"/>, or null.</summary>
	public static string? Caption(string path) => Stated.TryGetValue(path, out var stated) ? stated.Text : null;

	/// <summary>The index last stated for the pickbox text element at <paramref name="path"/>, or -1.</summary>
	public static int Index(string path) => Stated.TryGetValue(path, out var stated) ? stated.Index : -1;

	/// <summary>Forgets every statement.</summary>
	public static void Clear() => Stated.Clear();
}
