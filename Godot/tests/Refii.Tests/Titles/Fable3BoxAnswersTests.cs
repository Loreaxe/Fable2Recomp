namespace Refii.Tests.Titles;

using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable3;
using Refii.Ui;

/// <summary>
/// Godot's answers to Fable 3's boxes, menu box and Options screen (Fable3BoxAnswers), and the
/// Options screen's stated state (Fable3OptionsState): the tick action each press becomes, the
/// menu box's wrap, and the statement's words joined to the stated levels.
/// </summary>
[TestSuite]
public class Fable3BoxAnswersTests
{
	private static readonly Fable3BoxAnswers.Press A = new(true, false, false, false, false, false, false, false);
	private static readonly Fable3BoxAnswers.Press B = new(false, true, false, false, false, false, false, false);
	private static readonly Fable3BoxAnswers.Press X = new(false, false, true, false, false, false, false, false);
	private static readonly Fable3BoxAnswers.Press Up = new(false, false, false, false, true, false, false, false);

	[TestCase]
	public void QuestionsAnswerYesOnAAndNoOnB()
	{
		AssertThat(Fable3BoxAnswers.ForBox("question", 0x8A001234u, A)).IsEqual("box:1:8A001234:yes");
		AssertThat(Fable3BoxAnswers.ForBox("question", 0x8A001234u, B)).IsEqual("box:1:8A001234:no");
		AssertThat(Fable3BoxAnswers.ForBox("question", 0x8A001234u, X)).IsNull();
	}

	[TestCase]
	public void TheAdjusterAnswersWithGodotsValue()
	{
		AssertThat(Fable3BoxAnswers.ForBox("adjuster", 0x10u, A, adjusterValue: 250)).IsEqual("box:1:00000010:accept:250");
		AssertThat(Fable3BoxAnswers.ForBox("adjuster", 0x10u, B, adjusterValue: 250)).IsEqual("box:1:00000010:cancel");
		AssertThat(Fable3BoxAnswers.ForBox("adjuster", 0x10u, Up)).IsNull();
	}

	[TestCase]
	public void MessagesDismissAndTheDownloadFormTakesX()
	{
		AssertThat(Fable3BoxAnswers.ForBox("message", 0x20u, A)).IsEqual("box:1:00000020:dismiss");
		AssertThat(Fable3BoxAnswers.ForBox("message", 0x20u, B)).IsEqual("box:1:00000020:dismiss");
		AssertThat(Fable3BoxAnswers.ForBox("message", 0x20u, X)).IsNull();
		AssertThat(Fable3BoxAnswers.ForBox("message", 0x20u, X, download: true)).IsEqual("box:1:00000020:action");
		AssertThat(Fable3BoxAnswers.ForBox("modal", 0x30u, A)).IsEqual("box:1:00000030:dismiss");
		AssertThat(Fable3BoxAnswers.ForBox("modal", 0x30u, B)).IsNull();
		AssertThat(Fable3BoxAnswers.ForBox("modal", 0x30u, X, download: true)).IsEqual("box:1:00000030:action");
		AssertThat(Fable3BoxAnswers.ForBox("notice", 0x40u, B)).IsNull();
		AssertThat(Fable3BoxAnswers.ForBox("notice", 0x40u, A)).IsEqual("box:1:00000040:dismiss");
	}

	private static readonly Fable3BoxAnswers.Press Y = new(false, false, false, true, false, false, false, false);

	[TestCase]
	public void OnlyTheAnsweredKindsAreGodots()
	{
		foreach (string kind in new[] { "message", "question", "adjuster", "quest", "notice", "modal", "card", "buttons" })
		{
			AssertThat(Fable3BoxAnswers.Owns(kind)).IsTrue();
		}

		foreach (string kind in new[] { "intermission", "display", "screen:options", "unknown" })
		{
			AssertThat(Fable3BoxAnswers.Owns(kind)).IsFalse();
			AssertThat(Fable3BoxAnswers.ForBox(kind, 1u, A)).IsNull();
		}
	}

	/// <summary>
	/// The info card: a press card's A and any card's Y are its handler's (accept / help); a held
	/// card's A is never a press, only its hold's accept.
	/// </summary>
	[TestCase]
	public void TheCardAcceptsOnAAndAsksForHelpOnY()
	{
		AssertThat(Fable3BoxAnswers.ForBox("card", 0x50u, A)).IsEqual("box:1:00000050:accept");
		AssertThat(Fable3BoxAnswers.ForBox("card", 0x50u, Y)).IsEqual("box:1:00000050:help");
		AssertThat(Fable3BoxAnswers.ForBox("card", 0x50u, B)).IsNull();
		AssertThat(Fable3BoxAnswers.ForBox("card", 0x50u, A, holdCard: true)).IsNull();
		AssertThat(Fable3BoxAnswers.ForBox("card", 0x50u, Y, holdCard: true)).IsEqual("box:1:00000050:help");
		AssertThat(Fable3BoxAnswers.CardHold(0x50u)).IsEqual("box:1:00000050:accept");
		AssertThat(Fable3BoxAnswers.CardHold(0x50u, 2)).IsEqual("box:2:00000050:accept");
	}

	/// <summary>The button box answers every face button with its own code: 1 A, 2 B, 3 X, 4 Y.</summary>
	[TestCase]
	public void TheButtonBoxAnswersEachFaceButton()
	{
		AssertThat(Fable3BoxAnswers.ForBox("buttons", 0x60u, A)).IsEqual("box:1:00000060:button:1");
		AssertThat(Fable3BoxAnswers.ForBox("buttons", 0x60u, B)).IsEqual("box:1:00000060:button:2");
		AssertThat(Fable3BoxAnswers.ForBox("buttons", 0x60u, X)).IsEqual("box:1:00000060:button:3");
		AssertThat(Fable3BoxAnswers.ForBox("buttons", 0x60u, Y)).IsEqual("box:1:00000060:button:4");
		AssertThat(Fable3BoxAnswers.ForBox("buttons", 0x60u, Up)).IsNull();
	}

	[TestCase]
	public void TheTopBoxIsHandedItsButtonsGoingDownAndUp()
	{
		AssertThat(Fable3BoxAnswers.TopBox(a: true, down: true)).IsEqual("topbox:1:a:down");
		AssertThat(Fable3BoxAnswers.TopBox(a: true, down: false)).IsEqual("topbox:1:a:up");
		AssertThat(Fable3BoxAnswers.TopBox(a: false, down: true, player: 2)).IsEqual("topbox:2:y:down");
		AssertThat(Fable3BoxAnswers.TopBoxFill(a: true, thousandths: 250)).IsEqual("topbox:1:a:fill:250");
		AssertThat(Fable3BoxAnswers.TopBoxFill(a: false, thousandths: 1200, player: 2)).IsEqual("topbox:2:y:fill:1000");
		AssertThat(Fable3BoxAnswers.TopBoxFill(a: true, thousandths: -3)).IsEqual("topbox:1:a:fill:0");
	}

	/// <summary>The card's hold takes the guest's one second: 15 game ticks at 1/15 a tick.</summary>
	[TestCase]
	public void TheCardHoldsForOneSecond() => AssertThat(Fable3BoxAnswers.CardHoldSeconds).IsEqual(1.0);

	[TestCase]
	public void TheMenuBoxWrapsAsTheGuestsHandlerDoes()
	{
		AssertThat(Fable3BoxAnswers.MenuStep(0, 4, down: false)).IsEqual(3);
		AssertThat(Fable3BoxAnswers.MenuStep(3, 4, down: true)).IsEqual(0);
		AssertThat(Fable3BoxAnswers.MenuStep(1, 4, down: true)).IsEqual(2);
		AssertThat(Fable3BoxAnswers.MenuStep(0, 0, down: true)).IsEqual(0);
		AssertThat(Fable3BoxAnswers.MenuPick(2)).IsEqual("menubox:1:2");
		AssertThat(Fable3BoxAnswers.MenuCancel()).IsEqual("menubox-cancel:1");
	}

	/// <summary>
	/// Every answer names the player who pressed, the value the box's handler takes in r5: guest
	/// pad 0 is player one's, any other pad player two's.
	/// </summary>
	[TestCase]
	public void AnAnswerCarriesThePlayerWhoPressed()
	{
		AssertThat(Fable3ActingPlayer.OfPad(0)).IsEqual(1);
		AssertThat(Fable3ActingPlayer.OfPad(1)).IsEqual(2);
		AssertThat(Fable3ActingPlayer.OfPad(3)).IsEqual(2);
		AssertThat(Fable3BoxAnswers.ForBox("question", 0x8A001234u, A, player: 2)).IsEqual("box:2:8A001234:yes");
		AssertThat(Fable3BoxAnswers.ForBox("adjuster", 0x10u, A, adjusterValue: 7, player: 2)).IsEqual("box:2:00000010:accept:7");
		AssertThat(Fable3BoxAnswers.ForBox("message", 0x20u, B, player: 2)).IsEqual("box:2:00000020:dismiss");
		AssertThat(Fable3BoxAnswers.ForBox("question", 0x8A001234u, A, player: 0)).IsEqual("box:1:8A001234:yes");
	}

	/// <summary>
	/// The menu box selects for player one alone (the guest's mapper: `cmpwi r4,1`, TU3 0x82B512D4);
	/// either player cancels it.
	/// </summary>
	[TestCase]
	public void TheMenuBoxSelectsForPlayerOneOnly()
	{
		AssertThat(Fable3BoxAnswers.MenuPick(3, player: 1)).IsEqual("menubox:1:3");
		AssertThat(Fable3BoxAnswers.MenuPick(3, player: 2)).IsNull();
		AssertThat(Fable3BoxAnswers.MenuCancel(2)).IsEqual("menubox-cancel:2");
	}

	[TestCase]
	public void OptionsLeversAreTheHandlersActionsAndGodotsValues()
	{
		AssertThat(Fable3BoxAnswers.Options(Fable3BoxAnswers.ActionA)).IsEqual("options:1:74");
		AssertThat(Fable3BoxAnswers.Options(Fable3BoxAnswers.ActionStart, 2)).IsEqual("options:2:14");
		AssertThat(Fable3BoxAnswers.OptionsValue(3, 4, 0.725f)).IsEqual("options-value:1:3:4:0.725");
		AssertThat(Fable3BoxAnswers.OptionsValue(3, 9, 2f, 2)).IsEqual("options-value:2:3:9:2");
		AssertThat(Fable3BoxAnswers.OptionsValue(4, 1, 0f)).IsEqual("options-value:1:4:1:0");
		AssertThat(Fable3BoxAnswers.OptionsValue(3, 0, 1f)).IsNull();
		AssertThat(Fable3BoxAnswers.OptionsValue(3, 3, -0.1f)).IsNull();
	}

	private static Engine_UiModel.GuiNativeCall Call(string name, params float[] fields)
	{
		var arguments = new List<Engine_UiModel.GuiNativeArgument>
		{
			new(Engine_UiModel.GuiNativeArgumentKind.String, "8B0010A0"),
		};
		foreach (float field in fields)
		{
			arguments.Add(new Engine_UiModel.GuiNativeArgument(Engine_UiModel.GuiNativeArgumentKind.Number,
				field.ToString(System.Globalization.CultureInfo.InvariantCulture)));
		}

		return new Engine_UiModel.GuiNativeCall(name, arguments);
	}

	[TestCase]
	public void TheOptionsStateIsThePageAndTheGuestsGates()
	{
		// page 2, row 1, from the pause, in game, save allowed, no role, Load blocked 2 (not now),
		// Load not offered, 3 saves, 4 languages, no query, nothing waiting; player two's Options.
		Fable3OptionsState.State? state = Fable3OptionsState.Parse(Call("@Options.State", 2, 1, 1, 0, 1, 0, 2, 0, 3, 4, 0, 0, 2));
		AssertThat(state).IsNotNull();
		AssertThat(state!.Value.Box).IsEqual(0x8B0010A0u);
		AssertThat(state.Value.Page).IsEqual(Fable3OptionsModel.PageSaveManagement);
		AssertThat(state.Value.Row).IsEqual(1);
		AssertThat(state.Value.FromPause).IsTrue();
		AssertThat(state.Value.FrontEnd).IsFalse();
		AssertThat(state.Value.CanSave).IsTrue();
		AssertThat(state.Value.LoadBlocked).IsEqual(2u);
		AssertThat(state.Value.CanLoad).IsFalse();
		AssertThat(state.Value.Saves).IsEqual(3);
		AssertThat(state.Value.Languages).IsEqual(4);
		AssertThat(state.Value.Query).IsFalse();
		AssertThat(state.Value.Pending).IsFalse();
		AssertThat(state.Value.Owner).IsEqual(2);

		// A short statement is no state.
		AssertThat(Fable3OptionsState.Parse(Call("@Options.State", 3, 4))).IsNull();
	}

	[TestCase]
	public void TheOptionsValuesAreTheSettingsInTheirOrder()
	{
		// epoch 7, page 3: invert on, subtitles off, tutorial on, gamma 0.5, breadcrumb 3, music 1,
		// sfx 0.75, voice 0.9, speaker 2, language 0.
		Fable3OptionsState.Values? values = Fable3OptionsState.ParseValues(
			Call("@Options.Values", 7, 3, 1, 0, 1, 0.5f, 3, 1, 0.75f, 0.9f, 2, 0));
		AssertThat(values).IsNotNull();
		AssertThat(values!.Epoch).IsEqual(7);
		AssertThat(values.Page).IsEqual(3);
		AssertThat(values.Settings[(int)Fable3OptionSetting.Invert]).IsEqual(1f);
		AssertThat(values.Settings[(int)Fable3OptionSetting.Gamma]).IsEqual(0.5f);
		AssertThat(values.Settings[(int)Fable3OptionSetting.Breadcrumb]).IsEqual(3f);
		AssertThat(values.Settings[(int)Fable3OptionSetting.Sfx]).IsEqual(0.75f);
		AssertThat(values.Settings[(int)Fable3OptionSetting.Speaker]).IsEqual(2f);
		AssertThat(Fable3OptionsState.ParseValues(Call("@Options.Values", 7, 3))).IsNull();
	}

	/// <summary>
	/// The pointer states the Options row itself: the screen's rows are 1-based, a hover is the
	/// row alone and a click carries its accept in the same action, so the accept is of that row.
	/// </summary>
	[TestCase]
	public void TheOptionsRowIsStatedAndAClickAcceptsItInOneAction()
	{
		AssertThat(Fable3BoxAnswers.OptionsRow(0, false)).IsEqual("options-row:1:1");
		AssertThat(Fable3BoxAnswers.OptionsRow(8, false)).IsEqual("options-row:1:9");
		AssertThat(Fable3BoxAnswers.OptionsRow(2, true)).IsEqual("options-row:1:3:accept");
		AssertThat(Fable3BoxAnswers.OptionsRow(2, true, 2)).IsEqual("options-row:2:3:accept");
		AssertThat(Fable3BoxAnswers.OptionsRow(-1, true)).IsNull();
	}

	/// <summary>The save menu's four rows by the pointer, for the player who clicked; nothing past them.</summary>
	[TestCase]
	public void TheSaveMenuRowIsStatedForItsPlayer()
	{
		AssertThat(Fable3BoxAnswers.SaveMenuRow(0, false)).IsEqual("savemenu-row:1:1");
		AssertThat(Fable3BoxAnswers.SaveMenuRow(3, true)).IsEqual("savemenu-row:1:4:accept");
		AssertThat(Fable3BoxAnswers.SaveMenuRow(1, true, Fable3ActingPlayer.Two)).IsEqual("savemenu-row:2:2:accept");
		AssertThat(Fable3BoxAnswers.SaveMenuRow(4, false)).IsNull();
		AssertThat(Fable3BoxAnswers.SaveMenuRow(-1, false)).IsNull();
	}

	/// <summary>The pointer picks the menu box row it stands on, and none between or beside the rows.</summary>
	[TestCase]
	public void ThePointerPicksTheRowItStandsOn()
	{
		var rows = new System.Collections.Generic.List<Godot.Rect2>
		{
			new(new Godot.Vector2(100f, 100f), new Godot.Vector2(400f, 30f)),
			new(new Godot.Vector2(100f, 130f), new Godot.Vector2(400f, 30f)),
		};
		AssertThat(Fable3BoxAnswers.RowAt(rows, new Godot.Vector2(120f, 110f))).IsEqual(0);
		AssertThat(Fable3BoxAnswers.RowAt(rows, new Godot.Vector2(120f, 145f))).IsEqual(1);
		AssertThat(Fable3BoxAnswers.RowAt(rows, new Godot.Vector2(50f, 110f))).IsEqual(-1);
		AssertThat(Fable3BoxAnswers.RowAt(rows, new Godot.Vector2(120f, 200f))).IsEqual(-1);
	}

	/// <summary>
	/// A click on a drawn prompt is its pad button: A, B, X, Y and the bumpers have one, a trigger
	/// or an unknown name has none, and a click beside every prompt is no button.
	/// </summary>
	[TestCase]
	public void AClickOnAPromptIsItsButton()
	{
		AssertThat(Fable3Prompts.PadBit("A")).IsEqual(Refii.Game.GameInput.PadA);
		AssertThat(Fable3Prompts.PadBit("B")).IsEqual(Refii.Game.GameInput.PadB);
		AssertThat(Fable3Prompts.PadBit("X")).IsEqual(Refii.Game.GameInput.PadX);
		AssertThat(Fable3Prompts.PadBit("Y")).IsEqual(Refii.Game.GameInput.PadY);
		AssertThat(Fable3Prompts.PadBit("RT")).IsEqual(0u);
		var prompts = new System.Collections.Generic.List<(Godot.Rect2 Rect, string Button)>
		{
			(new Godot.Rect2(10f, 10f, 80f, 20f), "A"),
			(new Godot.Rect2(110f, 10f, 80f, 20f), "B"),
		};
		AssertThat(Fable3Prompts.ButtonAt(prompts, new Godot.Vector2(20f, 15f))).IsEqual("A");
		AssertThat(Fable3Prompts.ButtonAt(prompts, new Godot.Vector2(150f, 15f))).IsEqual("B");
		AssertThat(Fable3Prompts.ButtonAt(prompts, new Godot.Vector2(100f, 15f))).IsNull();
	}

	[TestCase]
	public void TheHudsLeversNameTheirPlayerAndTheGamesCode()
	{
		AssertThat(Fable3BoxAnswers.Emotion(Fable3GameActions.EmotionPressed(1))).IsEqual("emotion:1:103");
		AssertThat(Fable3BoxAnswers.Emotion(Fable3GameActions.EmotionReleased(5), player: 2)).IsEqual("emotion:2:112");
		AssertThat(Fable3BoxAnswers.WorldIconHold(down: true)).IsEqual("worldicon:1:hold:down");
		AssertThat(Fable3BoxAnswers.WorldIconHold(down: false, player: 2)).IsEqual("worldicon:2:hold:up");
		AssertThat(Fable3BoxAnswers.Dpad(Fable3GameActions.GuiRight)).IsEqual("dpad:1:32");
		AssertThat(Fable3BoxAnswers.Dpad(Fable3GameActions.GuiUpReleased, player: 2)).IsEqual("dpad:2:33");
		AssertThat(Fable3BoxAnswers.Interaction(Fable3GameActions.GuiB)).IsEqual("interaction:1:75");
		AssertThat(Fable3BoxAnswers.Interaction(Fable3GameActions.GuiRightTriggerHeld, player: 2)).IsEqual("interaction:2:91");
	}
}
