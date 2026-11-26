namespace Refii.Core;

using System.Collections.Generic;

/// <summary>
/// One post per PRESS of a bound input: a key action or a pad chord that runs a host action
/// (quicksave, the safety toggle) fires on the frame its input goes down in context, and not again
/// until the input is physically released.
/// </summary>
/// <remarks>
/// A PRESS MEANS WHAT IT MEANT WHEN IT WENT DOWN, as with <see cref="Engine_PauseBackKeys"/>. The
/// press is latched on the input alone, never on the context: read per frame with the context
/// folded in, a context that drops for a frame under a held key (the world losing the input to a
/// box, a notice, an interaction list) releases the latch, and the same press fires again when
/// the context returns. A press that went down out of context never fires, even if the context
/// arrives while it is held.
/// </remarks>
public sealed class Engine_PressLatch<TInput>
	where TInput : notnull
{
	private readonly HashSet<TInput> _down = new();

	/// <summary>
	/// One frame of <paramref name="input"/>: whether it fires now. <paramref name="held"/> is the
	/// physical input (key, button chord) without any context; <paramref name="inContext"/> is
	/// whether the action may run this frame.
	/// </summary>
	public bool Press(TInput input, bool held, bool inContext)
	{
		if (!held)
		{
			_down.Remove(input);
			return false;
		}

		return _down.Add(input) && inContext;
	}

	/// <summary>Whether <paramref name="input"/> is latched down (held since a press this latch saw).</summary>
	public bool IsDown(TInput input) => _down.Contains(input);
}
