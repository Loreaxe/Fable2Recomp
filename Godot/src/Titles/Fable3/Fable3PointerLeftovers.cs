namespace Refii.Titles.Fable3;

using Godot;
using Refii.Ui;

/// <summary>
/// Fable 3's end of the pointer's frame: its screens are separate canvases, each processing on its
/// own, so this runs after all of them (a late process priority), gives a left click none took to
/// the prompt it landed on (<see cref="Fable3Prompts.Click"/>) and hands what is then left to the
/// pad — a free right click is B, the wheel's notches D-pad steps — as Fable 2's UI does at the end
/// of its frame (<see cref="Engine_UiPointer.HandLeftoversToPad"/>, one implementation).
/// </summary>
public partial class Fable3PointerLeftovers : Node
{
	public override void _Ready() => ProcessPriority = 100000;

	public override void _Process(double delta)
	{
		// A click no row took may be on a drawn prompt: that button, before the rest goes to the pad.
		Fable3Prompts.Click();
		Engine_UiPointer.HandLeftoversToPad();
	}
}
