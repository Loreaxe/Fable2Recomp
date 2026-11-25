using System.Collections.Generic;
using Godot;
using Refii.Host;
using Refii.Ui;

namespace Refii.Titles.Fable2;

using Refii.Game;

/// <summary>
/// THE VERBS THE IN-GAME UI ANSWERS, on the one dispatcher (<see cref="Engine_UiCommands"/>): the
/// pause book's levers (tabs, saving, quitting, the co-op rows), the title's way out, the
/// multiplayer rows, and the host screens the open-screen verb stands. Every list's press reaches
/// them through the same <see cref="Engine_UiCommands.Run"/>; one that does not apply on the list it
/// was chosen on refuses with the reason (<see cref="Engine_UiVerb.Lists"/>).
/// </summary>
public partial class GameUi
{
	/// <summary>The host book screens built so far, by id (<see cref="Engine_UiHostScreen"/>).</summary>
	private readonly Dictionary<uint, Engine_UiHostScreen> _hostScreens = new();

	/// <summary>The host book screen standing over the pause book, or null.</summary>
	private Engine_UiHostScreen? _hostScreen;

	/// <summary>Registers this UI's verbs, openers and row conditions. Once, at ready.</summary>
	private void RegisterCommands()
	{
		Engine_UiCommands.Register(Fable2UiVerbs.PauseTab, (argument, context) =>
		{
			if (!uint.TryParse(argument, out uint tab))
			{
				return Engine_UiCommandResult.Refused($"'{context.Command}' names no tab");
			}

			bool accepted = HostBridge.PauseTabSelect(tab);
			GD.Print($"[game-ui] pause tab {tab} ('{context.Row}') " +
				(accepted
					? "requested — the guest pushes the screen itself"
					: "REFUSED by the bridge — not on the pause root, or a modal is pending"));
			return Engine_UiCommandResult.From(accepted, "the bridge refused it: not on the pause root, or a modal is pending");
		});

		// Through the pump, on the guest's tick thread, with the acting player: the ported root answers
		// the row as its handler did (gui_screen_stub_pause_root.cpp DispatchPauseSaving) — the guest's own confirm
		// box (23/24/25), drawn by the mirror and answered through QueryAnswer, or the quit when nothing
		// is held.
		Engine_UiCommands.Register(Fable2UiVerbs.PauseSaving, (row, context) =>
		{
			string lever = Fable2PortedScreens.Saving(Engine_UiSource.Current.ActingPlayer, row);
			HostBridge.PostFrontEndAction(lever);
			GD.Print($"[game-ui] pause saving row '{context.Row}' posted '{lever}' to the guest tick pump");
			return Engine_UiCommandResult.Done;
		});

		// Same event as the matching title row, so the same confirmation box and the same save; the
		// host adds only the latch, and the app ends when the guest reaches the front end.
		Engine_UiCommands.Register(Fable2UiVerbs.PauseQuit, (saving, context) =>
		{
			_quitToDesktopPending = true;
			_quitToDesktopWindow = QuitToDesktopWindowSeconds;
			string lever = Fable2PortedScreens.Saving(Engine_UiSource.Current.ActingPlayer, saving);
			HostBridge.PostFrontEndAction(lever);
			GD.Print($"[game-ui] pause entry '{context.Row}' posted '{lever}' " +
				"and armed the quit to the desktop; the app ends when the guest reaches the front end");
			return Engine_UiCommandResult.Done;
		});

		// Fable 3's Leave / Kick: the pump runs the guest's own answer on its tick thread.
		Engine_UiCommands.Register(Fable2UiVerbs.PauseCoop, (argument, context) =>
		{
			string coop = argument switch
			{
				"leave" => "CoopLeave",
				"kick" => "CoopKick",
				_ => string.Empty,
			};
			if (coop.Length == 0)
			{
				return Engine_UiCommandResult.Refused($"'{context.Command}': PauseCoop: takes leave or kick");
			}

			HostBridge.PostFrontEndAction(coop);
			GD.Print($"[game-ui] pause co-op row '{context.Row}' posted '{coop}'");
			return Engine_UiCommandResult.Done;
		});

		Engine_UiCommands.Register(Fable2UiVerbs.PauseOwned, Engine_UiHostScreens.Open);

		// THE MINIGAME TALLY'S A: the first press snaps Godot's count-ups (the handler's own rule,
		// 0x823CDC34..0x823CDC44); a press with every row settled closes — code 6 to the ported
		// `lhGuiMinigameCompletedScreen_HandleAction` (TU1 0x823CDC08 / disc 0x8258CB40), which runs
		// the close `sub_823CDF10` / `sub_8258CE48` (the `handler+40` latch, ExitScreen).
		Engine_UiCommands.Register(Fable2UiVerbs.MiniGameConfirm, (_, _) =>
		{
			bool snapped = _widgets.ConfirmMiniGame();
			if (!snapped)
			{
				HostBridge.ScreenAction(Engine_UiScreenInput.Dismiss);
			}

			GD.Print($"[minigame] A: {(snapped ? "rows snapped" : "rows settled — the tally closes")}");
			return Engine_UiCommandResult.Done;
		});

		// Host-owned: no guest branch to reach. No confirmation — the front end holds no unsaved
		// progress; in a game, PauseQuit: saves first.
		Engine_UiCommands.Register(Engine_UiVerbs.QuitToDesktop, (_, _) =>
		{
			GameExit.ToDesktop(GetTree());
			return Engine_UiCommandResult.Done;
		});

		Engine_UiCommands.Register(Engine_UiVerbs.Network, (argument, _) =>
		{
			DispatchNetwork(argument);
			return Engine_UiCommandResult.Done;
		});

		// THE MULTIPLAYER BOOK has behaviour of its own (Engine_UiListBookScreen); every other host
		// screen is a template book.
		Engine_UiHostScreens.Register(Engine_UiMultiplayerPage.ScreenId, (_, _, _) =>
			Engine_UiCommandResult.From(_multiplayer.Open(), "its art is not read yet; the next press opens it"));
		Engine_UiHostScreens.Books = OpenHostScreen;

		Engine_UiRowConditions.Register(Engine_UiRowConditions.Coop, () => CoopSession.PeerStanding || CoopSession.LocalCoop);
		Engine_UiRowConditions.Register(Engine_UiRowConditions.CoopOnline, () => CoopSession.PeerStanding && !CoopSession.LocalCoop);
		Engine_UiRowConditions.Register(Engine_UiRowConditions.CoopClient, () => CoopSession.IsClient);
		Engine_UiRowConditions.Register(Engine_UiRowConditions.InGame, () => HostBridge.IsInGame);
		Engine_UiRowConditions.Register(Engine_UiRowConditions.HasGold,
			() => HostBridge.GetHeroVitals() is { Known: true, Gold: > 0 });

		// SCREEN 28'S OWN STATE. `sub_82998300` spawns the estate screen's command rows (Buy,
		// Sell, Rent Out, Evict…) off the resolved building record, and this build never sees
		// them: the host draws screen 28's list, the guest's population lands in its own
		// presentation, so the rows are AUTHORED in the templates (Engine_UiGameTemplates) and
		// each states the fact its own branch of the guest's decision tree tests against the
		// cached _estateSnapshot sampled once per list pass.
		Engine_UiRowConditions.Register(Engine_UiRowConditions.EstateKnown, () => _estateSnapshot.Known);
		Engine_UiRowConditions.Register(Engine_UiRowConditions.EstateOwned,
			() => _estateSnapshot is { Known: true, Owned: true });
		Engine_UiRowConditions.Register(Engine_UiRowConditions.EstateVacant,
			() => _estateSnapshot is { Known: true, HasTenant: false });
		Engine_UiRowConditions.Register(Engine_UiRowConditions.EstateTenanted,
			() => _estateSnapshot is { Known: true, HasTenant: true });
		Engine_UiRowConditions.Register(Engine_UiRowConditions.EstateBusiness,
			() => _estateSnapshot.Known && _estateSnapshot.IsBusiness);
		Engine_UiRowConditions.Register(Engine_UiRowConditions.EstateHome,
			() => _estateSnapshot.Known && !_estateSnapshot.IsBusiness);
	}

	/// <summary>
	/// Stands a template book screen over whichever list opened it — the pause list, a screen's
	/// list, a mod's menu over either or on its own. It holds the whole pad while it stands and
	/// closes when the menus close or a guest box comes up; B returns to the list under it.
	/// </summary>
	private Engine_UiCommandResult OpenHostScreen(uint screenId, int player, Engine_UiCommandContext context)
	{
		if (!HostBridge.IsInGame || !IsRevealed)
		{
			return Engine_UiCommandResult.Refused($"screen {screenId} opens over the in-game menus");
		}

		if (!_hostScreens.TryGetValue(screenId, out Engine_UiHostScreen? screen))
		{
			// The achievements list keeps its look: leather for earned and the folder, slate for
			// locked, no rating strip.
			bool achievements = screenId == Engine_UiAchievementPage.ScreenId;
			screen = new Engine_UiHostScreen(screenId)
			{
				LockedRowsSlate = achievements,
				HideRatingStrip = achievements,
			};
			_hostScreens[screenId] = screen;
		}

		screen.Host = _presentation;
		screen.Books = _books;
		if (_hostScreen is { IsOpen: true } standing && !ReferenceEquals(standing, screen))
		{
			standing.Close();
		}

		bool up = screen.Open(player is 1 or 2 ? player : Engine_UiModMenus.MenuPlayer);
		if (up)
		{
			_hostScreen = screen;
		}

		return Engine_UiCommandResult.From(up, "its art is not read yet; the next press opens it");
	}

	/// <summary>
	/// A mod's screen stands where the pause book would (<see cref="Refii.Mods.ModScreen"/>): the book's
	/// lists hidden and the pad the mod's, with the world held by the pause under it.
	/// </summary>
	private bool DriveModScreen()
	{
		if (!Refii.Mods.ModScreen.Owned || !HostBridge.IsInGame)
		{
			if (_modScreenHidHud)
			{
				_modScreenHidHud = false;
				ShowHudViews(true);
			}

			return false;
		}

		if (_pauseView is not null)
		{
			_pauseView.Visible = false;
		}

		if (_screenMenuView is not null)
		{
			_screenMenuView.Visible = false;
		}

		if (_hostMenuView is not null)
		{
			_hostMenuView.Visible = false;
		}

		_modScreenHidHud = true;
		ShowHudViews(false);
		Fable2Input.HostUiOwnsPad = true;
		return true;
	}

	private bool _modScreenHidHud;

	private void ShowHudViews(bool show)
	{
		if (_hud is not null && IsInstanceValid(_hud))
		{
			_hud.Suppressed = !show;
			if (!show)
			{
				_hud.Visible = false;   // the walk states it only on the guest's frames, which a pause stops
			}
		}
	}

	/// <summary>
	/// The standing host screen's frame, over the pause book. True when it took the frame. Dropped
	/// the moment the pause root is not standing: a tab push, a query box or the resume all close
	/// it, the same window the list itself yields in.
	/// </summary>
	private bool DriveHostScreen(double delta)
	{
		if (_hostScreen is not { IsOpen: true } screen)
		{
			return false;
		}

		// Stands while the game's menus are open and no guest box is up: a resume, a quit or a
		// box takes it down, the same window the lists themselves yield in. A mod's standalone
		// menu opened it with no menus under it, so that one stands while the menu does.
		Engine_UiModel.PauseState? pause = HostBridge.GetPauseState();
		bool menusUp = pause is { ModalUp: false } state &&
			state.ScreenId != (uint)Engine_UiModel.InGameScreen.None;
		bool standing = HostBridge.IsInGame && IsRevealed &&
			(menusUp || (_hostMenuView is not null && pause is not { ModalUp: true }));
		if (!standing)
		{
			screen.Close();
			return false;
		}

		if (_pauseView is not null)
		{
			_pauseView.Visible = false;
		}

		if (_screenMenuView is not null)
		{
			_screenMenuView.Visible = false;
		}

		if (_hostMenuView is not null)
		{
			_hostMenuView.Visible = false;
		}

		// The whole pad is the screen's: its B closes it, never the guest's resume or back.
		Fable2Input.HostUiOwnsPad = true;

		float slabZ =
			_drawn.TryGetValue(_pauseAddress, out Engine_UiDrawnPresentation? slab) && IsInstanceValid(slab.Node)
				? slab.Node.Position.Z
				: 0f;

		// The art may still be being read; the frame is held either way rather than letting the
		// press fall through to a list the player cannot see.
		if (screen.Drive(
				Fable2Input.MenuDirection,
				Fable2Input.MenuAcceptPressed || PointerList(screen.View),
				Fable2Input.MenuBackPressed, delta, slabZ))
		{
			ReportMenuView(screen.View);
		}

		return true;
	}

	/// <summary>Drops every built host screen so the next open builds from the records as they stand (a remount).</summary>
	private void ForgetHostScreens()
	{
		foreach (Engine_UiHostScreen screen in _hostScreens.Values)
		{
			screen.Forget();
		}

		_hostScreens.Clear();
		_hostScreen = null;
	}
}
