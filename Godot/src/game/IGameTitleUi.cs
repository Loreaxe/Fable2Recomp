using Refii.Ui;

namespace Refii.Game;

/// <summary>
/// The title's UI over the world, as the game window drives it: the window hands it the video
/// player, conceals it under a full-screen movie and reveals it when the guest has a UI again, and
/// tells it which front-end screen the guest is on. The title creates it
/// (<see cref="Refii.Core.Engine_TitleGame.CreateUi"/>); the window never names its type.
/// </summary>
public interface IGameTitleUi
{
	GameVideo? Video { get; set; }

	/// <summary>A movie plays inside one of the UI's screens rather than over it.</summary>
	bool HasEmbeddedMovie { get; }

	/// <summary>The guest has an in-game UI up.</summary>
	bool HasInGameUi { get; }

	bool IsRevealed { get; }

	void Reveal();

	void Conceal(string reason = "unstated");

	void FollowGuest(Engine_UiModel.FrontEndScreen screen);

	/// <summary>The theme photo mode's own panel is drawn in, or null until the UI has one.</summary>
	Engine_UiTheme? PhotoModeTheme { get; }

	/// <summary>The UI's own account of its interactive state, for the Android host's debug report.</summary>
	object? DescribeInteractiveUi() => null;
}
