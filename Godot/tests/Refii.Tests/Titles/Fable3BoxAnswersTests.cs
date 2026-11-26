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
	}

	/// <summary>A click accepts on the list pages only; on the settings pages the wheel steps the value.</summary>
	[TestCase]
	public void OptionsSettingsPagesTakeTheClickAsTheCursorAndTheWheelAsTheValue()
	{
		AssertThat(Fable3BoxAnswers.OptionsClickAccepts(1)).IsTrue();
		AssertThat(Fable3BoxAnswers.OptionsClickAccepts(2)).IsTrue();
		AssertThat(Fable3BoxAnswers.OptionsClickAccepts(5)).IsTrue();
		AssertThat(Fable3BoxAnswers.OptionsClickAccepts(3)).IsFalse();
		AssertThat(Fable3BoxAnswers.OptionsClickAccepts(4)).IsFalse();
		AssertThat(Fable3BoxAnswers.OptionsWheelSteps(2)).ContainsExactly(32, 32);
		AssertThat(Fable3BoxAnswers.OptionsWheelSteps(-1)).ContainsExactly(31);
		AssertThat(Fable3BoxAnswers.OptionsWheelSteps(0)).IsEmpty();
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
	public void OptionsPressesAreTheHandlersActions()
	{
		AssertThat(Fable3BoxAnswers.OptionsAction(A)).IsEqual(Fable3BoxAnswers.ActionA);
		AssertThat(Fable3BoxAnswers.OptionsAction(B)).IsEqual(75);
		AssertThat(Fable3BoxAnswers.OptionsAction(Up)).IsEqual(29);
		AssertThat(Fable3BoxAnswers.OptionsAction(new Fable3BoxAnswers.Press(false, false, false, false, false, false, false, true))).IsEqual(32);
		AssertThat(Fable3BoxAnswers.OptionsAction(X)).IsNull();
		AssertThat(Fable3BoxAnswers.Options(31)).IsEqual("options:1:31");
		AssertThat(Fable3BoxAnswers.Options(74, 2)).IsEqual("options:2:74");
	}

	private static Engine_UiModel.GuiNativeCall State(params float[] fields)
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

		return new Engine_UiModel.GuiNativeCall("@Options.State", arguments);
	}

	[TestCase]
	public void TheOptionsStateJoinsTheStatedLevels()
	{
		// page 3, row 4, invert on, subtitles off, tutorial on, breadcrumb 3, speaker 0 (the box's),
		// language 1 of 4, flags 0/0, from the pause, in game, save allowed, no query, unchanged, load
		// not offered.
		uint[]? words = Fable3OptionsState.Parse(State(3, 4, 1, 0, 1, 3, 0, 1, 4, 0, 0, 1, 0, 1, 0, 0, 0));
		AssertThat(words).IsNotNull();
		AssertThat(words![0]).IsEqual(0x8B0010A0u);
		AssertThat(words[Fable3OptionsState.QueryWord]).IsEqual(0u);

		Fable3OptionsModel.Snapshot snapshot = Fable3OptionsState.Snapshot(words, 0.25f, 0.5f, 0.75f, 1.0f, 2);
		AssertThat(snapshot.Page).IsEqual(Fable3OptionsModel.PageGameSettings);
		AssertThat(snapshot.SelectedRow).IsEqual(4);
		AssertThat(snapshot.Controller).IsTrue();
		AssertThat(snapshot.Subtitles).IsFalse();
		AssertThat(snapshot.Breadcrumb).IsEqual(3);
		// The applier's speaker mode wins over the screen's field; the levels are the appliers'.
		AssertThat(snapshot.Speaker).IsEqual(2);
		AssertThat(snapshot.Gamma).IsEqual(0.25f);
		AssertThat(snapshot.Music).IsEqual(0.5f);
		AssertThat(snapshot.Sfx).IsEqual(0.75f);
		AssertThat(snapshot.Voice).IsEqual(1.0f);
		AssertThat(snapshot.CanSave).IsTrue();
		AssertThat(snapshot.CanLoad).IsFalse();

		// Unknown speaker: the screen's own field stands.
		AssertThat(Fable3OptionsState.Snapshot(words, 0.5f, 1f, 1f, 0.9f, -1).Speaker).IsEqual(0);

		// A short statement is no state.
		AssertThat(Fable3OptionsState.Parse(State(3, 4))).IsNull();
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
}
