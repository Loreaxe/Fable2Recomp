namespace Refii.Titles.Fable3;

using Godot;
using Refii.Ui;

/// <summary>
/// THE CO-OP MARKERS' SCREEN RULES (types 52/53, sub_82B51B58), the parts that need no camera:
/// the edge clamp and the corner the on-screen lift is measured from. Window pixels, y down — the
/// guest's projection sub_82287A08 maps NDC y to `(y0 + y1) / 2 - (y1 - y0) / 2 * y`
/// (0x82287BDC..0x82287BE0), so its viewport rect's y1 is the bottom edge.
/// </summary>
public static class Fable3MarkerPlacement
{
	/// <summary>The icon's side in hud.bgf units: `Contents.Icon.ImageModel`, scale 0.375 of the 100-unit quad on every slide.</summary>
	public const float Side = 37.5f;

	/// <summary>The icon's height above the element's root on `Fade In`: `Contents.Icon` position.y 25.</summary>
	public const float IconLift = 25f;

	/// <summary>
	/// The marker camera's near plane: `CEnginePrimitiveCamera`'s +0x30 (the camera-def the Update reads,
	/// `*(dword_83556540->vt[5]() + 4) + 0x20`, 0x82B51CBC / 0x82B51CC4), 0.1 from its ctor sub_82C3C7E8
	/// (`lfs f13, flt_8206F79C` 0x82C3C838, `stfs f13, 0x30(r31)` 0x82C3C864) — no other writer
	/// found (View_SetRenderCamera stores position and orientation only). The world-to-screen tests
	/// depth against it (sub_8226B1B8 copies it to view+0x88 at 0x8226B1CC; sub_82287A08 compares at
	/// 0x82287A98 and returns the (0, 0, -1, 0) sentinel below it). Guest units, which Godot's are.
	/// </summary>
	public const float Near = 0.1f;

	/// <summary>
	/// Whether a point at view depth <paramref name="depth"/> is behind the marker camera: the
	/// projection's `fcmpu depth, near` / `bge` (0x82287A98) fails, unordered included.
	/// </summary>
	public static bool Behind(float depth) => !(depth >= Near);

	/// <summary>
	/// How far a behind point is pushed along the camera's forward before it is projected and
	/// dropped to the bottom edge: <c>max(2 · near, 2 · |depth|)</c> — `fmuls f12, f13(near), f0(2.0)`
	/// 0x82B51D08, `fabs` / `fmuls` 0x82B51D7C..0x82B51D80, `fsubs` 0x82B51D84, then the {1, -1} table
	/// unk_82073D10 picked by lt|unordered and `fsel f6, f7, f12, f9` 0x82B51DA0 (2|depth| when
	/// unordered); P' = P + forward · push (`vmaddfp` 0x82B51DB0).
	/// </summary>
	public static float Push(float depth)
	{
		float floor = 2f * Near;
		float far = 2f * System.MathF.Abs(depth);
		return floor - far >= 0f ? floor : far;
	}

	/// <summary>A marker's two authored fades, hud.bgf opacity tracks on its `Contents.Icon`.</summary>
	public readonly record struct Fades(BgfAnimation In, BgfAnimation Out);

	/// <summary>
	/// The fades of the marker component <paramref name="component"/> under `Layer`: the opacity track
	/// its relative slide 1 (`Fade In`, Init's first show, sub_82B52068 0x82B520AC) and slide 2 (`Fade
	/// Out`, the death's, sub_82B51FE0 0x82B5204C) switch on for `Contents.Icon`. Null when the file
	/// does not carry them.
	/// </summary>
	public static Fades? FadesOf(Engine_UiScreen screen, string component)
	{
		UiNode? root = null;
		foreach (UiNode node in screen.Nodes)
		{
			if (node.Name == component && node.Parent?.Name == "Layer")
			{
				root = node;
				break;
			}
		}

		UiNode? icon = Child(Child(root, "Contents"), "Icon");
		if (root is null || icon is null)
		{
			return null;
		}

		Engine_BgfDocument document = screen.Document;
		var owned = new System.Collections.Generic.List<BgfSlide>();
		foreach (BgfSlide slide in document.Slides)
		{
			if (slide.OwnerElement == root.Ordinal)
			{
				owned.Add(slide);
			}
		}

		BgfAnimation? fadeIn = owned.Count > 1 ? OpacityTrack(document, owned[1], icon.Ordinal) : null;
		BgfAnimation? fadeOut = owned.Count > 2 ? OpacityTrack(document, owned[2], icon.Ordinal) : null;
		return fadeIn is not null && fadeOut is not null ? new Fades(fadeIn, fadeOut) : null;
	}

	/// <summary>
	/// The icon's opacity, 0..1, <paramref name="seconds"/> into its fade — the authored track at
	/// that time on the element's 1000-per-second timeline, evaluated as the guest's sub_82C3B078
	/// does (<see cref="BgfAnimation.Evaluate"/>: linear in time when both control times are 0,
	/// 0x82C3B1BC..0x82C3B1D4, then the cubic value blend 0x82C3B1F0..0x82C3B24C). Without the file's
	/// tracks the fade is linear over the second.
	/// </summary>
	public static float Alpha(Fades? fades, bool fadingOut, float seconds)
	{
		float t = System.Math.Clamp(seconds, 0f, 1f);
		if (fades is not { } tracks)
		{
			return fadingOut ? 1f - t : t;
		}

		BgfAnimation track = fadingOut ? tracks.Out : tracks.In;
		return System.Math.Clamp(track.Evaluate(t * 1000f) / 100f, 0f, 1f);
	}

	private static UiNode? Child(UiNode? parent, string name)
	{
		if (parent is null)
		{
			return null;
		}

		foreach (UiNode child in parent.Children)
		{
			if (child.Name == name)
			{
				return child;
			}
		}

		return null;
	}

	private static BgfAnimation? OpacityTrack(Engine_BgfDocument document, BgfSlide slide, int element)
	{
		foreach (BgfSlideEntry entry in slide.Entries)
		{
			if (entry.ElementOrdinal != element)
			{
				continue;
			}

			foreach (int index in entry.ActivatedAnimations ?? System.Array.Empty<int>())
			{
				if (index >= 0 && index < document.Animations.Length &&
					document.Animations[index].ElementOrdinal == element &&
					document.Animations[index].AttributeId == OpacityId)
				{
					return document.Animations[index];
				}
			}
		}

		return null;
	}

	private static readonly uint OpacityId = Refii.Core.Engine_GameHash.AttributeId("opacity");

	/// <summary>
	/// A marker's root, placed in its screen camera's viewport pixels (<paramref name="view"/>), on the window: the
	/// screen's top-left plus the point scaled from the viewport to the screen's size. One screen is the window
	/// itself (origin 0, size = view); a split half is its own screen (Fable3WorldIcons.MarkerScreens).
	/// </summary>
	public static Vector2 OnWindow(Vector2 placed, Vector2 view, Vector2 origin, Vector2 size) =>
		origin + (placed * (size / view));

	/// <summary>
	/// sub_82B519C8: the pixel held inside the viewport rect (x0, y0, x1, y1) = (0, 0, w, h) less its
	/// margins — x in [x0 + 0.1w + 10, x1 - 0.1(x1 - (x0 + 0.1w)) - 10] (0x82B51A24, 0x82B51A2C /
	/// 0x82B51A8C), y in [y0 + 0.1h + 40, y1 - 0.09h] (0x82B51A14 / 0x82B51A50, 0x82B51A28) — then
	/// unprojected at its own depth (sub_82269260 at 0x82B51B48). The 10 and 40 are the guest's
	/// 720-line pixels, scaled to the window. No rotation: the record's `Contents.Arrow` (+20, resolved
	/// 0x82B526C8) is read by no code, and the `Clamped` slide that activates it is never played.
	/// </summary>
	public static Vector2 Clamp(Vector2 pixel, Vector2 window)
	{
		float line = window.Y / 720f;
		float left = (window.X * 0.1f) + (10f * line);
		float right = window.X - (window.X * 0.09f) - (10f * line);
		float top = (window.Y * 0.1f) + (40f * line);
		float bottom = window.Y - (window.Y * 0.09f);
		return new Vector2(Mathf.Min(Mathf.Max(pixel.X, left), right), Mathf.Min(Mathf.Max(pixel.Y, top), bottom));
	}

	/// <summary>
	/// The corner sub_82B529E8 measures the lift from: the bounds' (right, bottom) — sub_8281AA20's
	/// union keeps left, top, right, bottom with y up (0x8281ACC4..0x8281ACFC), and the corner built
	/// at 0x82B52A70 / 0x82B52A78 is (right, bottom). The element's visible content is
	/// `Contents.Icon.ImageModel`, <see cref="Side"/> square about y <see cref="IconLift"/>: right
	/// +18.75, bottom +6.25 above the root (the `Title` has no area, which the union's area
	/// test skips at 0x8281ACBC: the one 52/53 source sub_82196410 adds the icon with the empty title
	/// word_82000B68, `addi r6, var_B0` 0x82196A6C into sub_825ED9C8 0x82196A88). <paramref name="placed"/> is the root as last placed;
	/// <paramref name="unit"/> window pixels per hud.bgf unit.
	/// </summary>
	public static Vector2 LiftCorner(Vector2 placed, float unit) =>
		new(placed.X + (Side * 0.5f * unit), placed.Y - ((IconLift - (Side * 0.5f)) * unit));
}
