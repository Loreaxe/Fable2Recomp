namespace Refii.Ui;

/// <summary>
/// THE POINTER ON A PINNED LIST. A pick (a click on a row, a press on a row's slider) moves the
/// cursor to the picked row, and a carousel whose cursor row is pinned slides that row away from
/// under the still pointer. Until the pointer moves, its row is the one it picked: the wheel and
/// the next click belong to that row, not to whichever row the slide left under it. The pick is
/// let go when the pointer moves more than <see cref="Slack"/> from where it picked, or when the
/// cursor, having reached the picked row, leaves it (the pad or the keys took it on). Pure.
/// </summary>
public sealed class Engine_UiPointerLatch
{
	/// <summary>How far (canvas units, either axis) the pointer may drift from its pick and still be still.</summary>
	public const float Slack = 4f;

	private int _held = -1;
	private bool _reached;
	private float _x;
	private float _y;

	/// <summary>Whether a pick is held.</summary>
	public bool Holding => _held >= 0;

	/// <summary>The pointer at (<paramref name="x"/>, <paramref name="y"/>) picked <paramref name="row"/>.</summary>
	public void Pick(int row, float x, float y)
	{
		_held = row;
		_reached = false;
		_x = x;
		_y = y;
	}

	/// <summary>Lets the pick go: the rows changed, the page changed, the pointer is not the device.</summary>
	public void Clear()
	{
		_held = -1;
		_reached = false;
	}

	/// <summary>
	/// The pointer's row this frame: <paramref name="hit"/> is the row drawn under it (-1 none),
	/// (<paramref name="x"/>, <paramref name="y"/>) where it stands, <paramref name="cursor"/> the
	/// list's cursor row. A cursor still on its way to the picked row (a walked cursor) is waited for.
	/// </summary>
	public int Row(int hit, float x, float y, int cursor)
	{
		if (_held >= 0)
		{
			if (cursor == _held)
			{
				_reached = true;
			}

			bool moved = System.MathF.Abs(x - _x) > Slack || System.MathF.Abs(y - _y) > Slack;
			if (moved || (_reached && cursor != _held))
			{
				Clear();
			}
		}

		return _held >= 0 ? _held : hit;
	}
}
