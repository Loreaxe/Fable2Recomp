using Godot;

namespace Refii.Ui;

/// <summary>
/// The game's one command dispatcher (<see cref="Engine_UiCommandSet"/>): the engine's verbs are
/// registered here on first use — the host actions (<see cref="Engine_UiScreenActions"/>), the mod
/// menu verbs (<see cref="Engine_UiModMenus"/>) and the open-screen verb
/// (<see cref="Engine_UiHostScreens"/>) — the in-game UI adds the pause and front-end verbs it
/// answers (GameUi), and a mod's Godot script adds its own through <c>RefiiHost.register_command</c>.
/// Every list's press and every screen's own button comes through <see cref="Run"/>.
/// </summary>
public static class Engine_UiCommands
{
	private static Engine_UiCommandSet? _set;

	private static readonly object Gate = new();

	public static Engine_UiCommandSet Set
	{
		get
		{
			lock (Gate)
			{
				if (_set is null)
				{
					var set = new Engine_UiCommandSet();
					Engine_UiScreenActions.RegisterVerbs(set);
					Refii.Core.Engine_TitleGame.Active.RegisterUiVerbs(set);
					Engine_UiModMenus.RegisterVerbs(set);
					set.Register(Engine_UiVerbs.OpenScreen, Engine_UiHostScreens.Open);
					set.Register(Engine_UiVerbs.AchievementsView, Engine_UiAchievementPage.View);
					Engine_UiHostScreens.Register(Engine_UiAchievementPage.ScreenId, Engine_UiAchievementPage.Open);
					_set = set;
				}

				return _set;
			}
		}
	}

	/// <summary>Registers a verb on the running dispatcher. A mod's verb may not replace the game's.</summary>
	public static bool Register(Engine_UiVerb verb, Engine_UiCommandHandler handler) => Set.Register(verb, handler);

	/// <summary>
	/// Runs a command chosen in <paramref name="context"/>. A refusal is reported here, once per
	/// press; <see cref="Engine_UiCommandOutcome.NotOurs"/> is the caller's to answer.
	/// </summary>
	public static Engine_UiCommandResult Run(string? command, Engine_UiCommandContext context)
	{
		Engine_UiCommandResult result = Set.Run(command, context);
		string where = context.Menu.Length == 0 ? Engine_UiCommandSet.ListName(context.List) : $"{context.Menu}/{context.Row}";
		switch (result.Outcome)
		{
			case Engine_UiCommandOutcome.Refused:
				GD.PushWarning($"[ui-commands] '{command}' on {where} refused: {result.Message}");
				break;

			case Engine_UiCommandOutcome.Done:
				GD.Print($"[ui-commands] '{command}' ran on {where}" +
					(context.Player is 1 or 2 ? $" for player {context.Player}" : string.Empty));
				break;
		}

		return result;
	}

	/// <summary>Runs a command on <paramref name="list"/> with no row to name. True when it ran.</summary>
	public static bool Run(string? command, Engine_UiCommandList list) =>
		Run(command, new Engine_UiCommandContext(list, Player: Engine_UiModMenus.MenuPlayer)).Outcome ==
			Engine_UiCommandOutcome.Done;
}
