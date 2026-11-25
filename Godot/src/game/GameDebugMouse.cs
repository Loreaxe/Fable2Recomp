namespace Refii.Game;

/// <summary>
/// Where the debug channel's `mouse` op puts the pointer: a window pixel, from either window pixels
/// or 0..1 of the window, kept inside the window.
/// </summary>
public static class GameDebugMouse
{
	/// <summary>
	/// The window pixel for (<paramref name="x"/>, <paramref name="y"/>): scaled by the window's
	/// <paramref name="width"/> × <paramref name="height"/> when <paramref name="normalized"/>,
	/// then clamped to the last pixel of each axis.
	/// </summary>
	public static (float X, float Y) WindowPoint(float x, float y, bool normalized, float width, float height)
	{
		float right = System.MathF.Max(width - 1f, 0f);
		float bottom = System.MathF.Max(height - 1f, 0f);
		if (normalized)
		{
			x *= width;
			y *= height;
		}

		return (System.Math.Clamp(x, 0f, right), System.Math.Clamp(y, 0f, bottom));
	}
}
