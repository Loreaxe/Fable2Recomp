namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Godot;
using Refii.Game;
using Refii.Ui;

/// <summary>
/// Button prompts on Fable 3's canvas-drawn surfaces (the boxes, the interaction panel): the
/// shared keycap for the pad button (<see cref="Engine_UiKeyGlyphs"/>, which letters it for the
/// pad or relabels it for the keyboard exactly as Fable 2's prompts are) followed by the label.
/// A prompt on screen is also its button for the pointer (<see cref="Click"/>).
/// </summary>
public static class Fable3Prompts
{
	/// <summary>The keycap's side as a multiple of the font's line height.</summary>
	private const float CapScale = 1.15f;
	private const float Gap = 8f;

	/// <summary>Points the shared keycaps at the title's fonts once they are loaded.</summary>
	public static void UseFonts(Engine_UiFontLibrary fonts)
	{
		if (Engine_UiKeyGlyphs.Fonts is null)
		{
			Engine_UiKeyGlyphs.Fonts = fonts;
		}
	}

	/// <summary>The width a prompt takes at <paramref name="scale"/>: keycap, gap and label.</summary>
	public static float Measure(UiFont font, string label, float scale) =>
		(font.Font.LineHeight * CapScale * scale) + (Gap * scale) + (Engine_UiCanvasText.Measure(font, label) * scale);

	// A DRAWN PROMPT IS ITS BUTTON UNDER THE POINTER, as the PC build's prompts are (each carries
	// its pad action as `MouseClickGameActionToTrigger`): every prompt drawn here is remembered
	// with the place it was drawn at, and a click on one presses that button on the pad the
	// screens already read (GameInput.QueuePointerPress), so the click takes the pad's own path
	// to the box's or the screen's answer. The surface that draws clears its own prompts first
	// (Begin), so only what is on screen can be clicked.

	private readonly record struct Spot(CanvasItem Item, Rect2 Rect, string Button);

	private static readonly List<Spot> Spots = new();

	/// <summary>Forgets the prompts <paramref name="item"/> drew before: the first call of its draw.</summary>
	public static void Begin(CanvasItem item) => Spots.RemoveAll(spot => spot.Item == item);

	/// <summary>The pad bit a prompt's button presses, 0 for one a click does not press (a trigger, a stick).</summary>
	public static uint PadBit(string button) => button switch
	{
		"A" => GameInput.PadA,
		"B" => GameInput.PadB,
		"X" => GameInput.PadX,
		"Y" => GameInput.PadY,
		"LB" => GameInput.PadLeftShoulder,
		"RB" => GameInput.PadRightShoulder,
		_ => 0u,
	};

	/// <summary>The button of the first of <paramref name="rects"/> holding <paramref name="at"/>, or null.</summary>
	public static string? ButtonAt(IReadOnlyList<(Rect2 Rect, string Button)> rects, Vector2 at)
	{
		foreach ((Rect2 rect, string button) in rects)
		{
			if (rect.HasPoint(at))
			{
				return button;
			}
		}

		return null;
	}

	/// <summary>
	/// This frame's left click against the prompts on screen: on one, the click is taken and its
	/// button pressed. Only while the pointer is the menus' and the mouse the device; once a frame,
	/// after the screens have taken their own clicks (rows).
	/// </summary>
	public static void Click()
	{
		Spots.RemoveAll(spot => !GodotObject.IsInstanceValid(spot.Item));
		if (!Engine_UiPointer.LeftPressed || !Engine_UiPointer.Free || !Engine_UiInputMode.KeyboardMouse)
		{
			return;
		}

		var rects = new List<(Rect2 Rect, string Button)>(1);
		foreach (Spot spot in Spots)
		{
			if (!spot.Item.IsVisibleInTree())
			{
				continue;
			}

			rects.Clear();
			rects.Add((spot.Rect, spot.Button));
			if (ButtonAt(rects, Engine_UiPointer.LocalTo(spot.Item)) is { } button && PadBit(button) is var bit && bit != 0u)
			{
				Engine_UiPointer.TakeLeft();
				GameInput.QueuePointerPress(bit);
				return;
			}
		}
	}

	/// <summary>Draws the prompt with its top-left at <paramref name="origin"/>; returns its width.</summary>
	public static float Draw(CanvasItem into, UiFont font, string button, string label, Vector2 origin, float scale, Color colour)
	{
		float line = font.Font.LineHeight * scale;
		float cap = line * CapScale;
		Spots.Add(new Spot(into, new Rect2(origin + new Vector2(0f, (line - cap) * 0.5f), new Vector2(Measure(font, label, scale), cap)), button));
		ImageTexture keycap = Engine_UiKeyGlyphs.KeycapFor(button);
		into.DrawTextureRect(keycap, new Rect2(origin + new Vector2(0f, (line - cap) * 0.5f), new Vector2(cap, cap)), false);
		float x = origin.X + cap + (Gap * scale);
		Engine_UiCanvasText.DrawLine(into, font, label, new Vector2(x, origin.Y), scale, colour);
		return Measure(font, label, scale);
	}
}
