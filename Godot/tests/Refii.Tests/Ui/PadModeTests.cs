namespace Refii.Tests.Ui;

using Godot;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Game;
using Refii.Ui;

/// <summary>
/// The pure rules behind the debug channel's joypad press (GameDebugJoypad) and `menu event`'s row
/// lookup (Engine_UiExpandableMenu.VisibleNamed). Pure model code.
/// </summary>
[TestSuite]
public class PadModeTests
{
	[TestCase]
	public void AnXInputMaskBecomesItsJoypadButtonsInOrder()
	{
		AssertThat(GameDebugJoypad.Buttons(0x1000u | 0x0010u)).IsEqual(new[] { JoyButton.A, JoyButton.Start });
		AssertThat(GameDebugJoypad.Buttons(0x0200u | 0x0001u | 0x8000u))
			.IsEqual(new[] { JoyButton.Y, JoyButton.RightShoulder, JoyButton.DpadUp });
		AssertThat(GameDebugJoypad.Buttons(0u)).IsEmpty();
	}

	[TestCase]
	public void SticksTurnToGodotsFrameAndTriggersClamp()
	{
		(JoyAxis Axis, float Value)[] axes = GameDebugJoypad.Axes(0.5f, 1f, null, -2f, 1.5f, null);
		AssertThat(axes).IsEqual(new[]
		{
			(JoyAxis.LeftX, 0.5f), (JoyAxis.LeftY, -1f), (JoyAxis.RightY, 1f), (JoyAxis.TriggerLeft, 1f),
		});
		AssertThat(GameDebugJoypad.Axes(null, null, null, null, null, null)).IsEmpty();
	}

	[TestCase]
	public void AMenuEventFindsTheVisibleRowByNameThenLabel()
	{
		var menu = new Engine_FrontEndMenu();
		var logbook = new Engine_UiExpandableMenu.Item("Logbook", "Logbook") { EventType = 300, Command = "PauseTab:7" };
		var map = new Engine_UiExpandableMenu.Item("Map", "Map") { StatedLabel = "Quests/Maps", Command = "PauseTab:2" };
		menu.Add(logbook);
		menu.Add(map);
		AssertThat(menu.Model.VisibleNamed("Logbook", 300, 0u)).IsSame(logbook);
		AssertThat(menu.Model.VisibleNamed("Logbook", 0, 0u)).IsSame(logbook);
		AssertThat(menu.Model.VisibleNamed("Quests/Maps", 0, 0u)).IsSame(map);
		AssertThat(menu.Model.VisibleNamed("Options", 300, 0u)).IsNull();
	}

	[TestCase]
	public void OfTwoRowsOfOneNameTheTypeAndIdChoose()
	{
		var menu = new Engine_FrontEndMenu();
		var first = new Engine_UiExpandableMenu.Item("Row", "Row") { EventType = 41, IdLow = 1000 };
		var second = new Engine_UiExpandableMenu.Item("Row", "Row") { EventType = 41, IdLow = 1011 };
		menu.Add(first);
		menu.Add(second);
		AssertThat(menu.Model.VisibleNamed("Row", 41, 1011u)).IsSame(second);
		AssertThat(menu.Model.VisibleNamed("Row", 41, 5u)).IsSame(first);
	}
}
