namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Godot;
using Refii.Host;
using Refii.Save;
using Refii.Text;

/// <summary>
/// The joining player's choice of the save their hero comes from (<see cref="Engine_HeroSaveChoice"/>).
/// The guest's drop-in join waits on it (native character_binding_hooks.cpp: the walk's first qualify
/// asks, achievement/hero_save_choice.h holds the guest thread); this page shows the saves the guest's
/// qualify passed, in its walk's order — the cursor starts on the one the guest would have taken — and
/// the guest's new hero, and answers with the row chosen. Driven by the joining player's own pad (their
/// user is their guest pad) and the pointer; B cancels the join — the guest's own way back from it, its
/// sign-in warning's Cancel (<see cref="Engine_HeroSaveChoice.Cancel"/>): player two is not in, and START
/// joins again.
/// </summary>
public partial class Fable3HeroSavePicker : Fable3HostSurface
{
	private Engine_HeroSaveChoice.Question? _question;
	private List<Engine_HeroSaveChoice.Row> _rows = new();
	private int[] _pads = System.Array.Empty<int>();

	/// <summary>Whether a choice is up.</summary>
	public static bool IsUp { get; private set; }

	protected override string LogTag => "fable3-hero-choice";

	protected override IReadOnlyList<int> Pads => _pads;

	protected override Fable3ScreenPage? Page()
	{
		if (_question is null && Engine_HeroSaveChoice.Parse(HostBridge.GetHeroSaveChoice()) is { } asked)
		{
			_question = asked;
			_pads = new[] { System.Math.Clamp(asked.User, 0, 3) };
			_rows = Engine_HeroSaveChoice.Rows(asked, Facts, word => Engine_OwnWords.Say(word));
			Cursor = 0;
			GD.Print($"[fable3-hero-choice] user {asked.User} chooses their hero: {asked.Saves.Count} save(s) " +
				$"({string.Join(", ", asked.Saves)}) or a new hero; the profile holds {asked.Listed}");
		}

		IsUp = _question is not null;
		if (_question is not { } question)
		{
			return null;
		}

		var lines = new List<Fable3ScreenPage.Row>(_rows.Count);
		foreach (Engine_HeroSaveChoice.Row row in _rows)
		{
			lines.Add(new Fable3ScreenPage.Row(row.Save ?? "NewHero",
				row.Value.Length == 0 ? row.Label : row.Label + "    " + row.Value, false, row.Description));
		}

		string who = Engine_OwnWords.Say(question.User == 0 ? "REFII_PLAYER_1" : "REFII_PLAYER_2");
		var prompts = new[]
		{
			new Fable3ScreenPage.Prompt("A", Engine_OwnWords.Say("REFII_SELECT")),
			new Fable3ScreenPage.Prompt("B", CancelWord()),
		};
		return new Fable3ScreenPage(who + ": " + Engine_OwnWords.Say("REFII_CHOOSE_HERO"), lines, Cursor,
			string.Empty, prompts, true);
	}

	protected override void Accept(int row)
	{
		if (_question is not { } question || Engine_HeroSaveChoice.Answer(question, row) is not { } choice)
		{
			return;
		}

		bool taken = HostBridge.AnswerHeroSaveChoice(question.Id, choice);
		GD.Print($"[fable3-hero-choice] choice {question.Id} answered " +
			(choice == Engine_HeroSaveChoice.NewHero ? "a new hero" : $"'{question.Saves[choice]}'") +
			(taken ? string.Empty : " (the guest no longer waits)"));
		_question = null;
		IsUp = false;
	}

	/// <summary>B: the join is cancelled as the guest's own Cancel cancels it.</summary>
	protected override void Back()
	{
		if (_question is not { } question)
		{
			return;
		}

		bool taken = HostBridge.AnswerHeroSaveChoice(question.Id, Engine_HeroSaveChoice.Cancel);
		GD.Print($"[fable3-hero-choice] choice {question.Id} cancelled: player two does not join" +
			(taken ? string.Empty : " (the guest no longer waits)"));
		_question = null;
		IsUp = false;
	}

	/// <summary>The guest's own word for this Cancel (its sign-in warning's), else its plain Cancel, else ours.</summary>
	private static string CancelWord()
	{
		foreach (string key in new[] { "GUI_SIGNIN_WARNING_CANCEL", "GUI_CANCEL" })
		{
			string text = Refii.Ui.Engine_UiGuiText.Resolve(key, Fable3Strings.Current);
			if (text.Length != 0 && text != key)
			{
				return text;
			}
		}

		return Engine_OwnWords.Say("REFII_CANCEL");
	}

	/// <summary>A save's own facts: its hero's name from Refii's sidecar, when the save was last written.</summary>
	private static Engine_HeroSaveChoice.SaveFacts Facts(string save)
	{
		string? directory = Engine_SaveDirectory.FindPackage(Refii.Core.Engine_AppRoot.SaveDirectory, save);
		if (directory is null)
		{
			return new Engine_HeroSaveChoice.SaveFacts(string.Empty, null);
		}

		string hero = SaveSidecar.Read(directory).Refii?.Character?.Name ?? string.Empty;
		System.DateTime? written = null;
		string display = string.Empty;
		try
		{
			// The guest writes ChecksumSave.bin with every save; the folder's own time when it has none.
			string checksum = System.IO.Path.Combine(directory, "checksumsave.bin");
			written = System.IO.File.Exists(checksum)
				? System.IO.File.GetLastWriteTime(checksum)
				: System.IO.Directory.GetLastWriteTime(directory);
			string header = System.IO.Path.Combine(
				Engine_SaveLayout.Headers(Refii.Core.Engine_AppRoot.SaveDirectory, Refii.Core.Engine_Title.Active.TitleId),
				save + ".header");
			if (System.IO.File.Exists(header))
			{
				display = Engine_SaveHeader.DisplayName(System.IO.File.ReadAllBytes(header)) ?? string.Empty;
			}
		}
		catch (System.IO.IOException)
		{
		}
		catch (System.UnauthorizedAccessException)
		{
		}

		return new Engine_HeroSaveChoice.SaveFacts(hero, written, display);
	}
}
