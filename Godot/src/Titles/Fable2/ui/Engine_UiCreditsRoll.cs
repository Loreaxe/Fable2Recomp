using System.Collections.Generic;
using Godot;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// The end credits (screen 29, `GUI.ShowCreditsScreen`), run by the guest's own rule from its
/// own data: `sub_826B79D8` loads text group `GUI_CREDITS` and parks 24 recycled Text elements
/// at y = 360 − 32·i (768 / 24); its update `sub_826B7D00` moves them up 30 units a second and,
/// as one reaches y 360, drops it 768 and hands it the next line (or nothing past the last). So
/// wrap n happens at t = 32·n / 30 and gives element n mod 24 line n — <see cref="Roll"/> — and
/// the guest closes the screen itself once the last line has gone by.
///
/// `creditsscreen.bgf`'s elements are anonymous (their names are hashes only), which is why no
/// template can name them; the roll needs none, because every Text element in the file is a
/// credits line. Drawn on its own layer ABOVE the video: the script plays `Credits.bik` and
/// opens the screen over it, and the guest drew its UI after the movie frame.
/// </summary>
public sealed partial class Engine_UiCreditsRoll : Control
{
	public const string Presentation = "creditsscreen";

	/// <summary>The game's font library.</summary>
	public Engine_UiFontLibrary? Fonts { get; set; }

	/// <summary>One line: the guest's string at its position on the 1280x720 canvas (origin at
	/// the centre, y up), in the file's authored size and colour.</summary>
	public readonly record struct Line(string Text, Vector2 At, int Size, Color Colour);

	private readonly List<Line> _lines = new();

	private const float Top = 360f;

	private const float Pitch = Engine_UiCreditsTiming.Pitch;

	private const float Span = 768f;

	private const float Speed = Engine_UiCreditsTiming.Speed;

	private const int Slots = Engine_UiCreditsTiming.Slots;

	/// <summary>
	/// The roll at <paramref name="seconds"/> since the screen opened: each slot's line and
	/// height, styled by that slot's authored Text element (in file order).
	/// </summary>
	public static List<Line> Roll(double seconds, IReadOnlyList<string> credits, IReadOnlyList<UiNode> slots)
	{
		var lines = new List<Line>(Slots);
		float travel = (float)(seconds * Speed);
		for (int i = 0; i < Slots && i < slots.Count; ++i)
		{
			float since = travel - (Pitch * i);
			if (since < 0f)
			{
				continue;
			}

			int wraps = (int)(since / Span) + 1;
			int index = i + (Slots * (wraps - 1));
			if (index >= credits.Count || credits[index].Length == 0)
			{
				continue;
			}

			float y = Top - (Pitch * i) + travel - (Span * wraps);
			UiNode slot = slots[i];
			UiText? authored = slot.Text;
			lines.Add(new Line(
				credits[index],
				new Vector2(slot.PositionX, y),
				authored is { Size: > 0 } ? authored.Size : 18,
				authored is null ? Colors.White : new Color(authored.R, authored.G, authored.B)));
		}

		return lines;
	}

	public override void _Ready()
	{
		MouseFilter = MouseFilterEnum.Ignore;
		SetAnchorsPreset(LayoutPreset.FullRect);
	}

	/// <summary>States this frame's lines; an empty set takes the roll down.</summary>
	public void Show(IReadOnlyList<Line> lines)
	{
		_lines.Clear();
		_lines.AddRange(lines);
		Visible = _lines.Count > 0;
		QueueRedraw();
	}

	public override void _Draw()
	{
		// The viewport, not the control's own rect: a Control under a bare CanvasLayer was measured
		// at size (0, 0) here, and the roll drew nothing for its whole 645 s.
		Vector2 size = GetViewportRect().Size;
		if (Fonts is null || size.Y <= 0f)
		{
			return;
		}

		float scale = size.Y / 720f;
		Vector2 centre = size * 0.5f;
		foreach (Line line in _lines)
		{
			if (Fonts.For(line.Size) is not { } picked)
			{
				continue;
			}

			// The credits carry the guest's colour tags (`<s4>Microsoft Game Studios</s4>`): drawn as
			// runs in their tint, as every other game text is (Engine_UiTextMarkup.Split).
			(UiFont font, float fontScale) = picked;
			float textScale = fontScale * scale;
			List<Engine_UiTextMarkup.Run> runs = Engine_UiTextMarkup.Split(line.Text);
			float width = 0f;
			foreach (Engine_UiTextMarkup.Run run in runs)
			{
				if (run.Tag is null && !run.Break)
				{
					width += Engine_UiCanvasText.Measure(font, run.Text) * textScale;
				}
			}

			float height = font.Font.LineHeight * textScale;
			var origin = new Vector2(
				centre.X + (line.At.X * scale) - (width * 0.5f),
				centre.Y - (line.At.Y * scale) - (height * 0.5f));
			foreach (Engine_UiTextMarkup.Run run in runs)
			{
				if (run.Tag is not null || run.Break)
				{
					continue;
				}

				Color tint = run.Colour is { } c ? new Color(c.R, c.G, c.B, line.Colour.A) : line.Colour;
				origin.X += Engine_UiCanvasText.DrawLine(this, font, run.Text, origin, textScale, tint);
			}
		}
	}
}
