namespace Refii.Ui;

/// <summary>
/// ONE PAD'S BUTTONS AS EDGES: the mask of buttons down this step against the last, split into the
/// buttons that went down and the buttons that came up. A surface that hands a button's going down
/// to someone must hand its coming up to the same one, whatever else changed meanwhile, so the
/// tracker also keeps, per button, an owner tag the caller set at the press and reads at the release.
/// The mask's bits are the caller's (a title adds bits of its own for analog triggers). Knows no
/// device and no Godot type.
/// </summary>
public sealed class Engine_UiPadEdges
{
	private readonly int[] _owner = new int[32];

	/// <summary>The buttons down as of the last step.</summary>
	public uint Down { get; private set; }

	/// <summary>Takes this step's mask; the buttons that went down and the buttons that came up since the last.</summary>
	public (uint Pressed, uint Released) Step(uint down)
	{
		uint pressed = down & ~Down;
		uint released = Down & ~down;
		Down = down;
		return (pressed, released);
	}

	/// <summary>Tags the single-bit <paramref name="button"/> with <paramref name="owner"/> (who its press was handed to).</summary>
	public void Own(uint button, int owner) => _owner[Index(button)] = owner;

	/// <summary>The tag of the single-bit <paramref name="button"/>, 0 when none was set since its last release.</summary>
	public int Owner(uint button) => _owner[Index(button)];

	/// <summary>Reads and clears the tag of the single-bit <paramref name="button"/> (its release).</summary>
	public int Release(uint button)
	{
		int index = Index(button);
		int owner = _owner[index];
		_owner[index] = 0;
		return owner;
	}

	private static int Index(uint button) => System.Numerics.BitOperations.TrailingZeroCount(button) & 31;
}
