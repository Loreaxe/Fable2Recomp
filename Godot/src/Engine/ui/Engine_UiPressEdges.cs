namespace Refii.Ui;

/// <summary>
/// A PAD'S BUTTONS AS PRESSES, for a screen Godot drives: a button counts once, when it goes down
/// while the screen has the pad. A button already down when the screen gets the pad — the press
/// that opened it, the press that left the page over it, an input a capture just bound — is not a
/// press on it until it has come up. Pure.
/// </summary>
public struct Engine_UiPressEdges
{
	private uint _was;

	/// <summary>The buttons newly down this frame, of the mask <paramref name="down"/>.</summary>
	public uint Take(uint down)
	{
		uint pressed = down & ~_was;
		_was = down;
		return pressed;
	}

	/// <summary>The screen does not have the pad this frame: whatever is held when it gets it is not a press.</summary>
	public void Hold() => _was = uint.MaxValue;
}
