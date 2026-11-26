using System.Collections.Generic;
using System.Linq;
using Godot;
using Refii.Core;
using Refii.Host;
using Refii.Save;
using Refii.Ui;

namespace Refii.Titles.Fable2;

/// <summary>
/// New Game at the save limit, and the Load Game fan's pick of the saves a new game replaces.
///
/// The limit box keeps the game's own body (`GUI_REACHED_SAVE_GAME_LIMIT_WARNING`, which names no
/// number) and its own "Continue without saving" and "Cancel", and adds Refii's "Replace a save".
/// Continue without saving goes on to the gender card; the commit then runs the guest's own limit
/// guard, which erases the device mapping so nothing is saved (guest_tick_pump.cpp StartNewGame).
/// Replace opens the guest's Load Game fan (so it states every save's text and portrait) in a pick
/// mode: the player picks as many saves as it takes to come under the limit, confirms once, and
/// each is SET ASIDE — moved whole with its header into <c>Older saves/Overwritten</c>
/// (<see cref="Engine_SaveMigration.SetAside"/>, the launcher's Swap's own move) — then the fan is
/// closed with the guest's own B, the guest's count is re-taken and the gender card comes up.
/// Nothing is deleted, and the save files, names and layout are the original game's.
/// </summary>
public partial class GameUi
{
	/// <summary>Refii's own words on this path: our keys, resolved through the game's text book (a mod's
	/// overlay may restate them) and then our own table (Refii.Text.Engine_OwnWords) in the language in force.</summary>
	private const string ReplaceASaveText = "REFII_F2_SAVES_REPLACE_A_SAVE";
	private const string ReplaceText = "REFII_F2_SAVES_REPLACE";
	private const string ChooseOneText = "REFII_F2_SAVES_CHOOSE_ONE";
	private const string ChooseManyText = "REFII_F2_SAVES_CHOOSE_MANY";
	private const string MoreToChooseText = "REFII_F2_SAVES_MORE_TO_CHOOSE";
	private const string ChosenText = "REFII_F2_SAVES_CHOSEN";
	private const string ConfirmOneText = "REFII_F2_SAVES_CONFIRM_ONE";
	private const string ConfirmManyText = "REFII_F2_SAVES_CONFIRM_MANY";
	private const string SetAsideFailedText = "REFII_F2_SAVES_SET_ASIDE_FAILED";
	private const string PageText = "REFII_PAGE_OF";

	/// <summary>The game's own labels the limit box keeps.</summary>
	private const string ContinueWithoutSavingTag = "GUI_DEVICE_REMOVED_CONTINUE";
	private const string CancelTag = "GUI_BUTTONS_CANCEL";

	/// <summary>True from "Replace a save" until the fan closes.</summary>
	private bool _replacing;

	/// <summary>How many saves the new game needs set aside: the count less the limit, plus one.</summary>
	private int _replaceNeed;

	/// <summary>The saves picked so far, by the fan's index (the guest's enumeration order).</summary>
	private readonly List<int> _replaceChosen = new();

	/// <summary>When "Load Game" was posted for the pick; the fan not opening is given up on.</summary>
	private ulong _replaceAskedAtMs;

	private const ulong ReplaceOpenGraceMs = 6000;

	/// <summary>The answer handler of a box raised by this file, or null for the guest's warnings.</summary>
	private System.Action<int>? _warningAnswered;

	private string Said(string text) => _presentation?.Theme?.Resolve(text) ?? Refii.Text.Engine_OwnWords.Say(text);

	private string Said(string template, object argument) =>
		string.Format(System.Globalization.CultureInfo.InvariantCulture, Said(template), argument);

	/// <summary>
	/// The limit box: the game's body, A "Replace a save", Y "Continue without saving", B "Cancel".
	/// </summary>
	private void ShowSaveLimitBox(string warning)
	{
		if (_warningView is null || _menuView is null)
		{
			return;
		}

		_warningView.Show(_presentation?.Theme, warning, ReplaceASaveText, twoButton: true,
			cancelTag: CancelTag, thirdTag: ContinueWithoutSavingTag);
		_warningView.AcceptOpens = null;
		_warningAnswered = answer =>
		{
			switch (answer)
			{
				case 0:
					BeginReplace();
					break;
				case 2:
					GD.Print("[game-ui] save limit: continue without saving — the gender card, then the guest's own guard");
					OpenScreen(Fable2FrontEndRoot.HeroCardScreen);
					break;
				default:
					_menuView.Visible = true;
					break;
			}
		};
		_menuView.Visible = false;
	}

	/// <summary>
	/// One frame of a box this file raised. True when the box is up (it owns the input); the answer
	/// — 0 A, 1 B (with cancel), 2 Y (with a third) — closes it and runs the handler.
	/// </summary>
	private bool DriveAnsweredBox()
	{
		if (_warningAnswered is not { } answered || _warningView is not { Visible: true } box)
		{
			_warningAnswered = null;
			return false;
		}

		int click = box.Click();
		int answer = Fable2Input.MenuAcceptPressed || click == 1 ? 0
			: (Fable2Input.MenuBackPressed || click < 0) && box.AllowCancel ? 1
			: box.HasThird && (Fable2Input.MenuFlipPressed || click == 2) ? 2
			: -1;
		if (answer >= 0)
		{
			box.Visible = false;
			box.Release();
			_warningAnswered = null;
			answered(answer);
		}

		return true;
	}

	/// <summary>"Replace a save": the guest's own Load Game branch opens the fan, in pick mode.</summary>
	private void BeginReplace()
	{
		Engine_UiModel.SaveState saves = _menu?.Saves ?? new Engine_UiModel.SaveState(true, 0, Fable2FrontEndRoot.DefaultSaveLimit);
		_replacing = true;
		_replaceNeed = System.Math.Max(1, saves.Count - (int)saves.Limit + 1);
		_replaceChosen.Clear();
		_replaceAskedAtMs = Time.GetTicksMsec();
		Post("Load Game", $"replace a save ({saves.Count} saves, limit {saves.Limit}: {_replaceNeed} to set aside)");
	}

	/// <summary>
	/// Waiting for the pick's fan: the rows stay down until it opens, and come back if it does not.
	/// True while waiting.
	/// </summary>
	private bool AwaitingReplaceFan()
	{
		if (!_replacing || _saveCardView is { Visible: true })
		{
			return false;
		}

		if (Time.GetTicksMsec() - _replaceAskedAtMs < ReplaceOpenGraceMs)
		{
			return true;
		}

		GD.PushWarning("[game-ui] replace a save: the guest's Load Game fan did not open — back to the list");
		EndReplace();
		if (_menuView is not null)
		{
			_menuView.Visible = true;
		}

		return false;
	}

	private void EndReplace()
	{
		_replacing = false;
		_replaceChosen.Clear();
		if (_saveCardView is not null)
		{
			_saveCardView.Heading = string.Empty;
			_saveCardView.ClearMarks();
		}
	}

	/// <summary>The pick's heading: what is still to choose.</summary>
	private void ShowReplaceHeading()
	{
		if (_saveCardView is null)
		{
			return;
		}

		int left = _replaceNeed - _replaceChosen.Count;
		_saveCardView.Heading = _replaceNeed == 1 ? Said(ChooseOneText)
			: _replaceChosen.Count == 0 ? Said(ChooseManyText, _replaceNeed)
			: Said(MoreToChooseText, left);
	}

	/// <summary>A on a card in pick mode: picks it, or unpicks it; the last pick asks to confirm.</summary>
	private void PickForReplace(int save)
	{
		if (_saveCardView is null || save < 0 || save >= _saveCardNames.Count)
		{
			return;
		}

		if (_replaceChosen.Remove(save))
		{
			_saveCardView.SetMarked(save, false);
		}
		else
		{
			_replaceChosen.Add(save);
			_saveCardView.SetMarked(save, true);
		}

		ShowReplaceHeading();
		GD.Print($"[game-ui] replace a save: picked [{string.Join(", ", _replaceChosen.Select(i => _saveCardNames[i]))}] " +
			$"of {_replaceNeed} — {_saveCardView.Describe()}");
		if (_replaceChosen.Count >= _replaceNeed)
		{
			ConfirmReplace();
		}
	}

	/// <summary>The one confirmation for every pick: A Replace, B Cancel (back to the fan, picks cleared).</summary>
	private void ConfirmReplace()
	{
		if (_warningView is null && _presentation?.Theme is { } theme)
		{
			AdoptWarning(theme);
		}

		if (_warningView is null || _saveCardView is null)
		{
			return;
		}

		string names = string.Join(_replaceChosen.Count == 1 ? string.Empty : "\n",
			_replaceChosen.Select(i => CardTitle(_saveCardView.Saves[i].Description)));
		string body = Said(_replaceChosen.Count == 1 ? ConfirmOneText : ConfirmManyText, names);
		_warningView.Show(_presentation?.Theme, body, ReplaceText, twoButton: true, cancelTag: CancelTag);
		_warningView.AcceptOpens = null;
		_warningAnswered = answer =>
		{
			if (answer == 0)
			{
				ReplaceChosen();
				return;
			}

			foreach (int save in _replaceChosen)
			{
				_saveCardView?.SetMarked(save, false);
			}

			_replaceChosen.Clear();
			ShowReplaceHeading();
		};
	}

	/// <summary>A card's first line — the hero's name, as the card shows it.</summary>
	private static string CardTitle(string description)
	{
		string first = description.Replace("<br>", "\n").Split('\n')[0].Trim();
		return first.Length == 0 ? description : first;
	}

	/// <summary>
	/// Confirmed: the guest leaves its fan by its own B, every pick is set aside, the guest's count is
	/// re-taken, and the gender card comes up as New Game's would.
	/// </summary>
	private void ReplaceChosen()
	{
		HostBridge.PostFrontEndAction("LoadFanBack");
		var failures = new List<string>();
		foreach (int save in _replaceChosen)
		{
			string name = _saveCardNames[save];
			string? aside = Engine_SaveMigration.SetAside(Engine_AppRoot.SaveDirectory, Engine_Title.Fable2.TitleId, name,
				Engine_SaveMigration.OverwrittenLabel, out string error);
			if (aside is null)
			{
				failures.Add($"{name}: {error}");
				GD.PushError($"[game-ui] replace a save: {name} NOT set aside — {error}");
			}
			else
			{
				GD.Print($"[game-ui] replace a save: {name} set aside to Older saves/{Engine_SaveMigration.OverwrittenLabel} as {aside}");
			}
		}

		HostBridge.RefreshSaveState();
		EndReplace();

		// Down now, not when the guest's mode leaves 5: the gender card takes the input from here and
		// the fan's own close (DriveLoadCarousel) is not reached while the card stands.
		CloseSaveCards();
		if (failures.Count != 0)
		{
			Engine_UiHostBoxes.Tell(string.Format(System.Globalization.CultureInfo.InvariantCulture,
				Said(SetAsideFailedText), string.Join("\n", failures)));
			return;
		}

		OpenScreen(Fable2FrontEndRoot.HeroCardScreen);
	}

	/// <summary>The fan's paging and its page arrows, for the load and the pick alike.</summary>
	private void DrivePages(Vector2? pointer, bool click, out bool clickTaken)
	{
		clickTaken = false;
		if (_saveCardView is null)
		{
			return;
		}

		int turn = Fable2Input.MenuPageNextPressed ? 1 : Fable2Input.MenuPagePrevPressed ? -1 : 0;
		if (turn == 0)
		{
			turn = _saveCardView.PointerTurn(pointer, click, out clickTaken);
		}

		if (turn != 0 && _saveCardView.TurnPage(turn))
		{
			GD.Print($"[game-ui] load fan page turned {turn:+#;-#} — {_saveCardView.Describe()}");
		}
	}
}
