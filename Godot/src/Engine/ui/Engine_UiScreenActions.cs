namespace Refii.Ui;

/// <summary>
/// The host actions a row or a screen's own button can name, registered as verbs on the one
/// dispatcher (<see cref="Engine_UiCommands"/>), and the config-switch rows' labels.
///
/// THE ONE-OFF PANELS ANSWER A DIFFERENT VOCABULARY, and that used to be a chain of screen-id
/// comparisons in `GameUi`: the query panel closes on Back, the repeatable-quest card dismisses
/// its box on Accept, minigame-completed and quest-completion dismiss the screen. Each was a
/// branch, so each could be wrong on its own. Naming the action in the template puts them on one
/// path — a screen states which button does what, and the dispatcher runs it.
/// </summary>
public static class Engine_UiScreenActions
{
	public const string SettingToggle = "setting:toggle:";

	public const string SettingSet = "setting:set:";

	public const string SettingCycle = "setting:cycle:";

	public static void RefreshSettingLabel(Engine_UiExpandableMenu.Item item)
	{
		if (item.Command is not { } command)
		{
			return;
		}

		if (command.StartsWith(SettingToggle, System.StringComparison.Ordinal))
		{
			string key = command[SettingToggle.Length..];
			item.StatedLabel = SettingLabel(key, Refii.Core.Engine_Switches.Flag(key, false) ? "true" : "false");
			return;
		}

		if (command.StartsWith(SettingCycle, System.StringComparison.Ordinal) &&
			SplitCycle(command, out string cycleKey, out string[] values))
		{
			item.StatedLabel = SettingLabel(
				cycleKey, Refii.Core.Engine_Switches.String(cycleKey, values[0]).Trim().ToLowerInvariant());
		}
	}

	/// <summary>
	/// "Title: value" for a switch row, worded by the page that describes the switch (the dynamic
	/// sky's, <see cref="Engine_UiWeatherOptions.Description"/>) so a row and the page never say it
	/// differently; a switch no page describes shows its key and raw value.
	/// </summary>
	private static string SettingLabel(string key, string stored)
	{
		if (Engine_UiWeatherOptions.Page.RowFor(key) is { } row)
		{
			return $"{row.Label}: {row.Caption(row.IndexOfStored(stored))}";
		}

		return $"{key}: {(stored is "true" ? "On" : stored is "false" ? "Off" : stored)}";
	}

	private static bool SplitCycle(string command, out string key, out string[] values)
	{
		string body = command[SettingCycle.Length..];
		int colon = body.IndexOf(':');
		key = colon > 0 ? body[..colon] : string.Empty;
		values = colon > 0
			? body[(colon + 1)..].Split(',', System.StringSplitOptions.RemoveEmptyEntries)
			: System.Array.Empty<string>();
		return key.Length != 0 && values.Length != 0;
	}

	private static bool CycleSetting(string command)
	{
		if (!SplitCycle(command, out string key, out string[] values))
		{
			return false;
		}

		string current = Refii.Core.Engine_Switches.String(key, values[0]).Trim().ToLowerInvariant();
		int at = System.Array.IndexOf(values, current);
		string next = values[(at + 1) % values.Length];
		return WriteSetting(key, next);
	}

	private static bool ToggleSetting(string key)
	{
		string path = Refii.Core.Engine_AppRoot.ConfigPath;
		System.Collections.Generic.SortedDictionary<string, string> values =
			Refii.Launcher.Core.RuntimeConfig.ReadValues(path);
		bool on = values.TryGetValue(key, out string? current) &&
			current.Trim().Trim('"') is "true" or "1";
		return WriteSetting(key, on ? "false" : "true");
	}

	private static bool WriteSetting(string key, string value)
		=> WriteSettings(new System.Collections.Generic.Dictionary<string, string> { [key] = value });

	/// <summary>
	/// Writes <paramref name="settings"/> into the title's config, dropping the keys in
	/// <paramref name="remove"/> (a control row put back to its default), as one write. Only those
	/// keys' lines change; the rest of the player's file stays as it stands (RuntimeConfig.EditText).
	/// </summary>
	public static bool WriteSettings(
		System.Collections.Generic.IReadOnlyDictionary<string, string> settings,
		System.Collections.Generic.IEnumerable<string>? remove = null)
	{
		string path = Refii.Core.Engine_AppRoot.ConfigPath;
		if (!Refii.Launcher.Core.RuntimeConfig.EditValues(path, settings, remove ?? System.Array.Empty<string>(), out string error))
		{
			Godot.GD.Print($"[screen-actions] could not write settings to {path}: {error}");
			return false;
		}

		Refii.Core.Engine_Switches.Invalidate();
		Godot.GD.Print($"[screen-actions] settings written to {path}");
		return true;
	}

	/// <summary>Closes the screen through the guest's own back lever.</summary>
	public const string ScreenBack = "screen:back";

	/// <summary>Dismisses the screen — the one-off panels' own close.</summary>
	public const string ScreenDismiss = "screen:dismiss";

	/// <summary>The guest's own flip press (code 39) to the current screen's input method.</summary>
	public const string ScreenUnlearn = "screen:unlearn";

	/// <summary>
	/// A on the screen itself — for a list whose rows the screen does NOT answer by identity.
	///
	/// The furniture screens are the case: their rows carry event type 0, and the press handler
	/// at `vtable+20` (`sub_826B65F0`) has cases for 140 and 141 and falls out of everything
	/// else, so an identity push does nothing at all. The commit lives in the INPUT handler on
	/// code 36, which reads the highlighted row out of Lua
	/// (`sub_826C1B50` -&gt; `g_Menu:GetHighlightedMenuItemData()`) rather than off the event —
	/// so the press has to be the press.
	/// </summary>
	public const string ScreenSelect = "screen:select";

	/// <summary>
	/// Dismisses the display box rather than the screen. The repeatable-quest card has no screen
	/// class at all: it is the quest box (`sub_823D0090`), which answers 36 through the box
	/// queue's own lever.
	/// </summary>
	public const string BoxDismiss = "box:dismiss";

	/// <summary>
	/// Runs a named action through the one dispatcher (<see cref="Engine_UiCommands"/>), as chosen
	/// on <paramref name="list"/>. True when a verb ran it; an empty, unknown or refused command
	/// does nothing here.
	/// </summary>
	public static bool Run(string action, Engine_UiCommandList list = Engine_UiCommandList.Screen) =>
		Engine_UiCommands.Run(action, list);

	/// <summary>
	/// The host actions as verbs on the one dispatcher: options pages, config switches, the
	/// simulation operations the pump runs on the guest's tick, the property rows, a mod's ability
	/// purchase, and a guest screen's own levers.
	/// </summary>
	public static void RegisterVerbs(Engine_UiCommandSet set)
	{
		// A HOST OPTIONS PAGE — the dynamic sky's switches, a mod's settings: the row opens the
		// page the registry maps its command to, on the options screen's own furniture.
		set.Register(Engine_UiVerbs.Options, (_, context) => Engine_UiCommandResult.From(
			Engine_UiHostOptionsPage.Run(context.Command), $"'{context.Command}' names no options page"));

		set.Register(Engine_UiVerbs.SettingToggle, (key, _) =>
			Engine_UiCommandResult.From(key.Length != 0 && ToggleSetting(key), "the switch was not written"));
		set.Register(Engine_UiVerbs.SettingCycle, (_, context) =>
			Engine_UiCommandResult.From(CycleSetting(context.Command), "names no key and values, or was not written"));
		set.Register(Engine_UiVerbs.SettingSet, (assignment, _) =>
		{
			int equals = assignment.IndexOf('=');
			return Engine_UiCommandResult.From(
				equals > 0 && WriteSetting(assignment[..equals], assignment[(equals + 1)..]),
				"states no <key>=<value>, or was not written");
		});

		set.Register(Engine_UiVerbs.ScreenBack, (_, _) => Lever(Engine_UiScreenInput.Back));
		set.Register(Engine_UiVerbs.ScreenDismiss, (_, _) => Lever(Engine_UiScreenInput.Dismiss));
		set.Register(Engine_UiVerbs.ScreenUnlearn, (_, _) => Lever(Engine_UiScreenInput.Unlearn));
		set.Register(Engine_UiVerbs.ScreenSelect, (_, _) => Lever(Engine_UiScreenInput.Select));
		set.Register(Engine_UiVerbs.BoxDismiss, (_, _) =>
		{
			Engine_UiSource.Current.DisplayBoxAction(Engine_UiSource.BoxAction.Dismiss);
			return Engine_UiCommandResult.Done;
		});
	}

	/// <summary>A guest screen's own lever, through the bridge's screen action.</summary>
	private static Engine_UiCommandResult Lever(uint code)
	{
		Refii.Host.HostBridge.ScreenAction(code);
		return Engine_UiCommandResult.Done;
	}

	/// <summary>Whether this screen takes input directly once revealed.</summary>
	public static bool OwnsRevealedInput(uint screenId) =>
		Engine_UiTemplates.Screen(screenId)?.OwnsRevealedInput ?? false;

	/// <summary>
	/// Runs whichever of this screen's revealed-input actions the pressed button names.
	///
	/// Accept is checked before Back because no shipped screen states both, and a screen that
	/// stated both would want the affirmative to win.
	/// </summary>
	public static void RunRevealed(uint screenId, bool accept, bool back, bool flip = false, bool x = false)
	{
		if (Engine_UiTemplates.Screen(screenId) is not { } screen)
		{
			return;
		}

		if (accept && screen.RevealedAccept.Length != 0)
		{
			Run(screen.RevealedAccept, Engine_UiCommandList.Revealed);
			return;
		}

		if (back && screen.RevealedBack.Length != 0)
		{
			Run(screen.RevealedBack, Engine_UiCommandList.Revealed);
			return;
		}

		// The screen's Y, where it states one (`flipAction`).
		if (flip && screen.FlipAction.Length != 0)
		{
			Run(screen.FlipAction, Engine_UiCommandList.Revealed);
			return;
		}

		// The screen's X, where it states one (`xAction`): the villager card's rename.
		if (x && screen.XAction.Length != 0)
		{
			Run(screen.XAction, Engine_UiCommandList.Revealed);
		}
	}
}
