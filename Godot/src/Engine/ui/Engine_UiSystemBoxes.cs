using System;
using System.Collections.Generic;
using System.Globalization;

namespace Refii.Ui;

/// <summary>
/// THE GUEST'S SYSTEM MESSAGE BOXES: its <c>XamShowMessageBoxUI</c> calls, the console's own box (one
/// look for every title, the host's questions'). The guest states the user, title, text, buttons and
/// focused button (rexglue shadow <c>xam_ui.cpp</c>); the host asks them in the guest's own words
/// through <see cref="Engine_UiHostBoxes"/> and answers with the chosen button's index
/// (<see cref="Refii.Host.HostBridge.AnswerSystemMessageBox"/>): button 0 on A, 1 on B, 2 on X — the
/// call takes at most three. A box of one button is a notice, its dismiss button 0.
/// </summary>
/// <remarks>
/// The guest waits for the answer — Fable 3's drop-in sign-in warning spins its main thread on the
/// box's XN_SYS_UI edges (TU3 sub_8236C588 / disc sub_82365898) — so a box is asked the frame it
/// appears, once, and nothing answers it but the player.
/// </remarks>
public static class Engine_UiSystemBoxes
{
	/// <summary>One box as the guest stated it.</summary>
	public sealed record Box(uint Id, uint User, uint Flags, uint Focus, string Title, string Text, IReadOnlyList<string> Buttons);

	/// <summary>The record's field separator (the native side's 0x1F).</summary>
	public const char Separator = '\u001F';

	private const int Fixed = 7;

	/// <summary>
	/// Reads <c>RefiiHostGetSystemMessageBox</c>'s record: id, user, flags, focus, button count,
	/// title, text, then the captions. Null for no record or one that does not hold together.
	/// </summary>
	public static Box? Parse(string? record)
	{
		if (string.IsNullOrEmpty(record))
		{
			return null;
		}

		string[] fields = record.Split(Separator);
		if (fields.Length < Fixed ||
			!uint.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out uint id) || id == 0u ||
			!uint.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint user) ||
			!uint.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out uint flags) ||
			!uint.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out uint focus) ||
			!int.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out int count) ||
			fields.Length != Fixed + count)
		{
			return null;
		}

		var buttons = new string[count];
		Array.Copy(fields, Fixed, buttons, 0, count);
		return new Box(id, user, flags, focus, fields[5], fields[6], buttons);
	}

	/// <summary>The question's body: the box's title over its text.</summary>
	public static string Body(Box box) =>
		box.Title.Length == 0 ? box.Text : box.Text.Length == 0 ? box.Title : box.Title + "\n\n" + box.Text;

	/// <summary>The id of the box last asked (the native side never reuses one).</summary>
	private static uint _asked;

	/// <summary>The key the box up was asked under, empty for none.</summary>
	private static string _askedKey = string.Empty;

	/// <summary>A guest's system box is the question up: it stands over every other box.</summary>
	public static bool Up => _askedKey.Length != 0 && Engine_UiHostBoxes.Asking(_askedKey);

	/// <summary>Asks the box the guest waits on, the frame it appears. Called every session frame.</summary>
	public static void Pump(bool guestUp)
	{
		if (!guestUp)
		{
			return;
		}

		if (Parse(Refii.Host.HostBridge.GetSystemMessageBox()) is not { } box || box.Id == _asked)
		{
			return;
		}

		_asked = box.Id;
		Godot.GD.Print($"[system-box] the guest's box {box.Id} for user {box.User} waits: '{box.Title}', " +
			$"{box.Buttons.Count} button(s) '{string.Join("' | '", box.Buttons)}', focus {box.Focus}, flags {box.Flags:X}");

		void Answered(int answer)
		{
			bool taken = Refii.Host.HostBridge.AnswerSystemMessageBox(box.Id, answer);
			Godot.GD.Print($"[system-box] box {box.Id} answered {answer}" +
				(answer >= 0 && answer < box.Buttons.Count ? $" ('{box.Buttons[answer]}')" : " (cancelled)") +
				(taken ? string.Empty : ": the guest no longer waits on it"));
		}

		string body = Body(box);
		string key = "system-box:" + box.Id.ToString(CultureInfo.InvariantCulture);
		switch (box.Buttons.Count)
		{
			case 0:
				Engine_UiHostBoxes.Tell(body, _ => Answered(-1));
				break;
			case 1:
				Engine_UiHostBoxes.Tell(body, _ => Answered(0));
				break;
			case 2:
				_askedKey = key;
				Engine_UiHostBoxes.Ask(body, Answered, box.Buttons[0], box.Buttons[1], key);
				break;
			default:
				_askedKey = key;
				Engine_UiHostBoxes.Ask(body, Answered, box.Buttons[0], box.Buttons[1], box.Buttons[2], key);
				break;
		}
	}
}
