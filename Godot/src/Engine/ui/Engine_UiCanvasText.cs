using Godot;

namespace Refii.Ui;

/// <summary>
/// Draws the game's own bitmap font into a 2D CanvasItem.
///
/// WHY A SECOND TEXT PATH EXISTS, and why it is not a second implementation of one.
/// <see cref="Engine_UiTextMesh"/> builds glyph geometry for the Gameface screens, which are
/// 3D scenes rendered through a SubViewport with their own camera. The loading screen is not a
/// Gameface screen — it is its own class in the guest, drawn straight to the backbuffer — so on
/// our side it is a <c>CanvasLayer</c> above the UI viewport, and a 3D mesh cannot be placed on
/// one. What is shared is the thing that matters: the FONT. Both paths take the same
/// <see cref="UiFont"/> out of <see cref="Engine_UiFontLibrary"/> and the same per-glyph
/// metrics, so the letters are the game's in both.
///
/// The metrics are the engine's own, from <see cref="BitmapGlyph"/>: a glyph advances by
/// <c>LeftBearing + PostAdvance</c> and paints <c>DrawWidth</c> pixels starting at
/// <c>LeftBearing</c>. Using the atlas cell's width as the advance instead would space every
/// proportional font wrongly.
/// </summary>
public static class Engine_UiCanvasText
{
	/// <summary>How wide a string will be, in font pixels at scale 1.</summary>
	public static float Measure(UiFont font, string text)
	{
		float width = 0f;
		foreach (char c in text)
		{
			width += font.Font.GetGlyph(c).Advance;
		}

		return width;
	}

	/// <summary>
	/// How wide a line of the title's MARKED text will be, in font pixels at scale 1: its runs as
	/// <paramref name="contract"/>'s markup splits them (<see cref="Engine_UiTextMarkup"/>), a text run
	/// by its glyphs, an inline icon by the title's pen advance
	/// (<see cref="Engine_UiTextContract.IconPenAdvance"/>), the same cells
	/// <see cref="Engine_UiTextMesh"/> lays; a break ends the line.
	/// </summary>
	public static float MeasureMarked(UiFont font, string text, Engine_UiTextContract contract)
	{
		float width = 0f;
		foreach (Engine_UiTextMarkup.Run run in contract.Markup.Split(text))
		{
			if (run.Break)
			{
				break;
			}

			width += run.Tag is { } tag
				? contract.IconPenAdvance(tag is { Kind: Engine_UiTextMarkup.Kind.Icon, DoubleWidth: true }, font.Font.LineHeight)
				: Measure(font, run.Text);
		}

		return width;
	}

	/// <summary>
	/// Draws one line of the title's MARKED text — a guest string that may carry its own tags, such as
	/// a price's `&lt;gold_img&gt;` — returning the advance used. The runs are <paramref name="contract"/>'s
	/// markup's, as the 3D text mesh draws them: a text run in its colour run's tint (replacing the RGB
	/// where the title's runs replace, <see cref="Engine_UiTextContract.ColourRunsReplace"/>), an icon as
	/// its tag's art (<paramref name="textures"/> given the tag's path + `.tex`) of the title's icon size,
	/// its top-left <see cref="Engine_UiTextContract.IconInset"/> left of and above the pen, the pen then
	/// moved by the title's icon advance; an unknown tag draws nothing and a break ends the line. Same
	/// <paramref name="origin"/> and <paramref name="scale"/> as <see cref="DrawLine"/>.
	/// </summary>
	public static float DrawMarked(CanvasItem into, UiFont font, string text, Vector2 origin, float scale, Color colour,
		Engine_UiTextContract contract, System.Func<string, Texture2D?>? textures)
	{
		float pen = 0f;
		foreach (Engine_UiTextMarkup.Run run in contract.Markup.Split(text))
		{
			if (run.Break)
			{
				break;
			}

			if (run.Tag is { } tag)
			{
				bool wide = tag is { Kind: Engine_UiTextMarkup.Kind.Icon, DoubleWidth: true };
				if (tag.Kind == Engine_UiTextMarkup.Kind.Icon && textures?.Invoke(tag.Texture + ".tex") is { } art)
				{
					(float width, float height) = contract.IconSize(wide, font.Font.LineHeight);
					into.DrawTextureRect(art, new Rect2(
						origin + new Vector2((pen - contract.IconInset) * scale, -contract.IconInset * scale),
						new Vector2(width * scale, height * scale)), false, new Color(1f, 1f, 1f, colour.A));
				}

				pen += contract.IconPenAdvance(wide, font.Font.LineHeight);
				continue;
			}

			Color tint = run.Colour is not { } c ? colour
				: contract.ColourRunsReplace ? new Color(c.R, c.G, c.B, colour.A)
				: colour * c;
			pen += DrawLine(into, font, run.Text, origin + new Vector2(pen * scale, 0f), scale, tint) / (scale != 0f ? scale : 1f);
		}

		return pen * scale;
	}

	/// <summary>
	/// Draws one line, returning the advance used.
	///
	/// <paramref name="origin"/> is the LEFT baseline-top corner in the canvas item's own
	/// space, and <paramref name="scale"/> multiplies the font's authored pixel size — the
	/// loading screen's coordinates are in a virtual 1280x720 canvas that has to be scaled to
	/// the window, so the caller supplies the factor rather than this guessing at it.
	/// </summary>
	public static float DrawLine(CanvasItem into, UiFont font, string text,
		Vector2 origin, float scale, Color colour)
	{
		float pen = 0f;
		Texture2D atlas = Engine_BitmapFontTexture.PlainAtlasOf(font.Atlas);
		foreach (char c in text)
		{
			BitmapGlyph glyph = font.Font.GetGlyph(c);

			// A space has no cell to draw; advancing past it is the whole of its contribution.
			if (glyph.DrawWidth > 0 && glyph.U1 > glyph.U0)
			{
				Rect2 source = Engine_BitmapFontTexture.GetGlyphRect(font.Font, glyph);

				var destination = new Rect2(
					origin + new Vector2((pen + glyph.LeftBearing) * scale, 0f),
					new Vector2(source.Size.X * scale, source.Size.Y * scale));

				// The atlas texture is supersampled; the metrics stay in shipped texels.
				float super = Engine_BitmapFontTexture.SupersampleOf(font.Atlas);
				into.DrawTextureRectRegion(
					atlas, destination,
					new Rect2(source.Position * super, source.Size * super), colour);
			}

			pen += glyph.Advance;
		}

		return pen * scale;
	}

	/// <summary>
	/// Draws text wrapped to a box, top-left anchored, returning the height used.
	///
	/// A NEWLINE IS A HARD BREAK, and it is the engine's own rule rather than a convenience:
	/// `sub_82360968` scans the string for character 10 (`sub_82B3F140(&amp;s, 10)`) and counts the
	/// hits to decide how far up to shift the block. A renderer that treats '\n' as ordinary
	/// whitespace disagrees with the very measurement the caller centres by.
	///
	/// A LINE ONLY BREAKS WHEN IT HAS TO. That sounds too obvious to state, and the previous
	/// version got it wrong in a way that only showed on the LAST line: it scanned to the end of
	/// the text looking for break candidates and took the last whitespace it found, so a
	/// remainder that fitted the box perfectly well was still split at its final space. On
	/// screen that is a full-width paragraph followed by a short line and then one orphaned
	/// word — which reads as a font-metrics problem and is nothing of the kind.
	///
	/// A single word longer than the box is left to overhang rather than being cut: truncation
	/// is a different behaviour and this does not have the engine's rule for it.
	/// </summary>
	public static float DrawWrapped(CanvasItem into, UiFont font, string text,
		Vector2 origin, float boxWidth, float scale, Color colour)
	{
		float lineHeight = font.Font.LineHeight * scale;

		int lines = LayOut(font, text, boxWidth, scale, (line, index) =>
			DrawLine(into, font, line, origin + new Vector2(0f, index * lineHeight),
				scale, colour));

		return lines * lineHeight;
	}

	/// <summary>
	/// How many lines <see cref="DrawWrapped"/> will produce, without drawing any of them.
	///
	/// NEEDED BECAUSE THE TIP IS CENTRED ON ITS BLOCK, and a block cannot be centred until its
	/// height is known. The loading screen's anchor is the MIDDLE of the text, not its top edge,
	/// so the caller has to shift up by half the laid-out height before the first line goes
	/// down. Sharing <see cref="LayOut"/> with the drawing path is the point: a measurement that
	/// wrapped even slightly differently from the draw would centre the block by the wrong
	/// number and look like a font-metrics fault.
	/// </summary>
	public static int MeasureWrappedLines(UiFont font, string text, float boxWidth, float scale) =>
		LayOut(font, text, boxWidth, scale, null);

	/// <summary>
	/// The one wrapping algorithm, shared by measuring and drawing.
	///
	/// Calls <paramref name="emit"/> once per line with its zero-based index, and returns how
	/// many there were. A null <paramref name="emit"/> measures.
	/// </summary>
	private static int LayOut(UiFont font, string text, float boxWidth, float scale,
		System.Action<string, int>? emit)
	{
		int produced = 0;
		int start = 0;

		while (true)
		{
			// The segment up to the next hard break, or to the end.
			int hard = text.IndexOf('\n', start);
			int limit = hard >= 0 ? hard : text.Length;

			// Wrap that segment, and draw at least one line even when it is empty — a blank
			// line between paragraphs is content, not something to collapse away.
			while (true)
			{
				int end = limit;
				int candidate = -1;
				float width = 0f;

				for (int i = start; i < limit; ++i)
				{
					width += font.Font.GetGlyph(text[i]).Advance * scale;

					// Overflowing is what forces a break, and only then.
					if (width > boxWidth && candidate >= 0)
					{
						end = candidate;
						break;
					}

					if (char.IsWhiteSpace(text[i]))
					{
						candidate = i;
					}
				}

				string line = text[start..end].TrimEnd();
				if (line.Length > 0)
				{
					emit?.Invoke(line, produced);
				}

				++produced;

				// The break itself is consumed, so a wrapped line does not start with a space.
				start = end;
				while (start < limit && char.IsWhiteSpace(text[start]))
				{
					++start;
				}

				if (start >= limit)
				{
					break;
				}
			}

			if (hard < 0)
			{
				return produced;
			}

			start = hard + 1;
		}
	}
}
