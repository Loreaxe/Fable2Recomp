namespace Refii.Core;

/// <summary>
/// A press belongs to the surface it went down on: when the input is handed back to the world, the
/// buttons still down on each pad stay hidden from the guest until they are released.
/// </summary>
/// <remarks>
/// The guest reads the pad as levels and makes its own press edges per input context, so a button
/// that closed a menu and is still down when the world has the input again is a fresh press to the
/// world's actions: the A that accepted is an interaction, the B that backed out a spell. The
/// pointer's presses are held 0.1 s (GameInput.QueuePointerPress) and a key keeps its pad button
/// across the menu and play contexts (Enter and E are both A), so both always outlive the close.
/// The mouse buttons have the same rule on their own (<see cref="Engine_PlayButtons"/>).
/// </remarks>
public sealed class Engine_HandBackHold
{
	private readonly uint[] _held;
	private readonly bool[] _holdNext;
	private bool _worldHadInput;

	/// <param name="pads">How many pads are masked.</param>
	public Engine_HandBackHold(int pads)
	{
		_held = new uint[pads];
		_holdNext = new bool[pads];
	}

	/// <summary>Every pad's buttons down at its next <see cref="Hidden"/> stay hidden until released.</summary>
	public void HoldAll()
	{
		for (int pad = 0; pad < _holdNext.Length; ++pad)
		{
			_holdNext[pad] = true;
		}
	}

	/// <summary>
	/// Whether the world has the input now. The step it gets it back — from a menu, a box, a screen,
	/// a load — is a hand-back: <see cref="HoldAll"/>. May be called once per pad a frame.
	/// </summary>
	public void Follow(bool worldHasInput)
	{
		if (worldHasInput && !_worldHadInput)
		{
			HoldAll();
		}

		_worldHadInput = worldHasInput;
	}

	/// <summary>
	/// The buttons of <paramref name="pad"/> to hide from the guest this publish, given the buttons
	/// down: those held at the hand-back and not released since.
	/// </summary>
	public uint Hidden(int pad, uint down)
	{
		if ((uint)pad >= (uint)_held.Length)
		{
			return 0u;
		}

		if (_holdNext[pad])
		{
			_holdNext[pad] = false;
			_held[pad] = down;
		}

		_held[pad] &= down;
		return _held[pad];
	}
}
