namespace Refii.Titles.Fable3;

using Godot;

/// <summary>
/// Fable 3's menu pointer wears the game's own cursor: `cursor_selected.tex`, which the 360 banks
/// hold under `art\gui\hudassests\version4\pc\cursor\` beside `cursor_unselected.tex` (the PC
/// build shows the same picture as a Windows cursor, loaded from its own `cursor_selected.cur`).
/// </summary>
/// <remarks>
/// Only the picture is this node's. When a pointer is shown at all is the shared input's (free
/// in menus, captured in play, gone while the pad is the device: Refii.Game.GameInput and
/// Engine_UiPointer), so the cursor is set once, as soon as the banks hold it, and the system's
/// arrow is given back when the title's UI leaves the tree. The hot spot is the picture's top
/// left corner, where an arrow points.
///
/// The bank picture is 128x128; the PC shows it as a Windows cursor, which Windows draws at the
/// system cursor size (32x32 at 100% display scale). So the picture is drawn at
/// <see cref="CursorPixels"/> times the screen's scale factor, never at its stored size.
/// </remarks>
public partial class Fable3MenuCursor : Node
{
	/// <summary>The cursor's bank path.</summary>
	public const string Path = "art/gui/hudassests/version4/pc/cursor/cursor_selected.tex";

	/// <summary>The Windows cursor size at 100% display scale: the size the PC draws this picture at.</summary>
	public const int CursorPixels = 32;

	/// <summary>Seconds between two tries while the banks do not hold the art yet.</summary>
	private const double Retry = 0.5;

	private double _wait;
	private bool _set;

	public override void _Process(double delta)
	{
		if (_set || (_wait -= delta) > 0.0)
		{
			return;
		}

		_wait = Retry;
		if (Fable3Art.Load(Path)?.GetImage() is not { } image || image.IsEmpty())
		{
			return;
		}

		// A hardware cursor takes an uncompressed picture; the bank's is block-compressed.
		if (image.IsCompressed() && image.Decompress() != Error.Ok)
		{
			GD.PushWarning($"[fable3-cursor] '{Path}' did not decompress; the system cursor stays");
			_set = true;
			return;
		}

		image.Convert(Image.Format.Rgba8);
		int size = Mathf.Clamp(
			Mathf.RoundToInt(CursorPixels * DisplayServer.ScreenGetScale(DisplayServer.WindowGetCurrentScreen())),
			CursorPixels, 256);
		if (image.GetWidth() != size || image.GetHeight() != size)
		{
			image.Resize(size, size, Image.Interpolation.Lanczos);
		}

		Input.SetCustomMouseCursor(image, Input.CursorShape.Arrow, Vector2.Zero);
		_set = true;
		GD.Print($"[fable3-cursor] the menu pointer is '{Path}' ({image.GetWidth()}x{image.GetHeight()})");
	}

	public override void _ExitTree()
	{
		if (_set)
		{
			Input.SetCustomMouseCursor(null, Input.CursorShape.Arrow);
		}
	}
}
