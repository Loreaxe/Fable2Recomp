namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// THE SCREENS THE GUEST STATES ARE UP, joined to their presentations without the registry: a
/// headless presentation (gui_headless_presentation.cpp) is in no kernel list, so the screen
/// manager's slot for the stated screen id (`mgr+12+4*id`, the shared slot) names it and the
/// screen's template names its file.
/// </summary>
public static class Engine_UiStatedScreens
{
	/// <summary>The registry entry a stated screen stands as: root active, live, contributing.</summary>
	public static Engine_UiModel.Presentation Entry(uint address, string file) =>
		new(address, 0u, file + ".bgf", 9u, -1);

	/// <summary>
	/// The presentation the manager holds for screen <paramref name="screenId"/> and its file: the
	/// slot of that id, else a slot whose screen shares the template's file (the pause tabs share
	/// `pausemainmenu`, and the manager's map keeps one id per address). Null when none is built.
	/// </summary>
	public static (uint Address, string File)? Presentation(
		System.Collections.Generic.IReadOnlyDictionary<uint, int> owned, uint screenId) =>
		Presentation(owned, screenId, id => Engine_UiTemplates.Screen(id)?.Presentation);

	/// <summary><see cref="Presentation(System.Collections.Generic.IReadOnlyDictionary{uint, int}, uint)"/> over a given screen-to-file map.</summary>
	public static (uint Address, string File)? Presentation(
		System.Collections.Generic.IReadOnlyDictionary<uint, int> owned, uint screenId,
		System.Func<uint, string?> fileOf)
	{
		if (fileOf(screenId) is not { Length: > 0 } file)
		{
			return null;
		}

		foreach ((uint address, int id) in owned)
		{
			if ((uint)id == screenId)
			{
				return (address, file);
			}
		}

		foreach ((uint address, int id) in owned)
		{
			if (string.Equals(fileOf((uint)id), file, System.StringComparison.OrdinalIgnoreCase))
			{
				return (address, file);
			}
		}

		return null;
	}


}
