namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Refii.Ui;

/// <summary>
/// THE TOPBOX'S HELD BUTTONS ON GODOT'S CLOCK. A TopBox message can ask for A or Y to be held
/// (its `IsHoldAButton` / `IsHoldYButton`); the guest's hold step is replaced (topbox_answer.cpp
/// rex_TopBox_HoldStep) and takes its progress from here: one <see cref="Engine_UiHoldMeter"/> per
/// button, filling at the guest's stated held rate — per millisecond, as the guest's GUI clock counts
/// (<see cref="MillisecondsPerSecond"/>) — while the guest says the button is held
/// (`@TopBox.Held` — its input sets it on the press this side handed it and clears it on the release,
/// or when the hero moves) and this side still has it down, draining at the stated released rate
/// otherwise. Each change of a fill is stated back in thousandths
/// (<see cref="Fable3BoxAnswers.TopBoxFill"/>); the guest posts the answer on the step the fill is
/// full. A new message starts both fills empty. Pure — no Godot type — so the tests drive it.
/// </summary>
public sealed class Fable3TopBoxHold
{
	/// <summary>One button's side of a step: the message asks for it held, this side has it down, the guest's stated hold.</summary>
	public readonly record struct Button(bool Asked, bool Down, Fable3UiStatements.TopBoxHold Stated);

	/// <summary>The stated rates are per millisecond of the guest's GUI clock; Godot's frame delta is in seconds.</summary>
	public const double MillisecondsPerSecond = 1000.0;

	private readonly Engine_UiHoldMeter[] _meters = { new(1.0), new(1.0) };
	private readonly int[] _stated = { 0, 0 };
	private uint _message;

	/// <summary>The fill of A (0) or Y (1), 0..1.</summary>
	public float Fill(int button) => _meters[button].Progress;

	/// <summary>
	/// One frame: moves both fills and appends the levers that state a changed fill for
	/// <paramref name="player"/>. <paramref name="message"/> is the id of the message up, 0 for none.
	/// </summary>
	public void Step(double delta, uint message, Button a, Button y, int player, List<string> levers)
	{
		if (message != _message)
		{
			_message = message;
			_meters[0].Reset();
			_meters[1].Reset();
		}

		for (int i = 0; i < 2; ++i)
		{
			Button button = i == 0 ? a : y;
			bool filling = message != 0u && button.Asked && button.Down && button.Stated.Held;
			_meters[i].Step(delta, filling, button.Stated.HeldRate * MillisecondsPerSecond,
				button.Stated.ReleasedRate * MillisecondsPerSecond);
			int thousandths = (int)System.MathF.Round(_meters[i].Progress * 1000f);
			if (thousandths != _stated[i])
			{
				_stated[i] = thousandths;
				levers.Add(Fable3BoxAnswers.TopBoxFill(i == 0, thousandths, player));
			}
		}
	}
}
