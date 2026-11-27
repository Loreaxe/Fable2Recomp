using System;
using System.Collections.Generic;
using Godot;
using Refii.Core;
using Refii.Textures;

namespace Refii.Ui;

/// <summary>
/// The controller's button art, swapped for keycaps while the keyboard and mouse are the
/// active device and back the instant the controller is touched — and, on the pad, drawn in the
/// player's glyph style (Xbox, PlayStation, Switch: <c>glyph_style</c>), from their glyph pack
/// (<c>glyph_pack</c>, <see cref="Engine_UiGlyphPack"/>), naming the physical input the player's
/// remap binds to each game button (<see cref="Engine_PadRemap"/>).
/// </summary>
/// <remarks>
/// EVERY BUTTON GLYPH IS ONE TEXTURE OBJECT. The UI loads all of them through
/// <see cref="Engine_UiTextureCache.Load"/> — inline `&lt;a_img&gt;` icons, the .bgf prompt
/// quads cut from the `abyx` atlas, the HUD's world prompts — and every material, clone and
/// pattern book holds the texture it was given. So the cache hands out one proxy per glyph path
/// and the switch rewrites the proxy's pixels: nothing that drew a glyph has to redraw it.
///
/// A keycap is drawn at the original's size, so the UV insets the quads were cut with still
/// fit. The `abyx` atlas keeps everything but its eight button cells — its lower half is the
/// hold-meter ring. The menu cursor's select orb, cut from `frames_elements.tex`, is an atlas
/// button too (<see cref="CursorOrb"/>): its quads carry an orb texture, an opaque disc that also
/// covers the red B baked into the cursor's rim. Labels come from the game (<see cref="LabelFor"/>),
/// which knows the bindings and whether a menu or the world has the input.
///
/// THE SHIPPED ART STAYS while it is true: Xbox style, no pack file for the input, and the game
/// button pressed by itself. Anything else is drawn (<see cref="DrawPadGlyph"/>) cell by cell like
/// a keycap, so the atlas cells and the animated strips keep their UVs. The atlas quads show their
/// keycap texture while <c>ui_keyboard_mode</c> is set — set whenever a face button is not the
/// shipped art. Proxies are shared textures: they follow ONE player's device, the one with the
/// input (<see cref="GlyphPlayer"/>, <see cref="PadFamily"/>); a surface that is player two's alone
/// takes player two's own set (Engine_UiKeyGlyphs.PlayerTwo.cs).
///
/// Art that is not a texture — the pub games' SWF prompt shapes — makes the same choice per
/// button (<see cref="PromptShowsShipped"/>) and, drawn, carries a keycap texture in its place.
/// </remarks>
public static partial class Engine_UiKeyGlyphs
{
	/// <summary>The key a pad button stands for now: "A" → "E", "B" → "Esc" in a menu.</summary>
	public static Func<string, string?>? LabelFor { get; set; }

	/// <summary>The font keycaps are lettered in — the game's own, once the UI has loaded it.</summary>
	public static Engine_UiFontLibrary? Fonts { get; set; }

	/// <summary><c>glyph_style</c>: auto, xbox, playstation or switch.</summary>
	public static string StyleSetting { get; set; } = "auto";

	/// <summary><c>glyph_pack</c>: the pack's folder name, empty for the built-in glyphs.</summary>
	public static string PackName { get; set; } = string.Empty;

	/// <summary>The family of the controller the prompts follow (the player with the input).</summary>
	public static ControllerFamily PadFamily { get; set; }

	/// <summary>The player (1 or 2) whose remap and controller the prompts follow.</summary>
	public static int GlyphPlayer { get; set; } = 1;

	/// <summary>The style the pad prompts are drawn in now.</summary>
	public static GlyphStyle Style => Engine_ControllerFamily.Resolve(StyleSetting, PadFamily);

	/// <summary>How one texture of a button's art takes the glyph swap.</summary>
	public enum GlyphLayer
	{
		/// <summary>The whole button (its `complete` art, or the orb its letter sits on): drawn as the glyph.</summary>
		Whole,

		/// <summary>A layer over the orb (the letter, the rim): shipped art or nothing, so the glyph under it shows alone.</summary>
		Overlay,
	}

	/// <summary>A texture that is a button's art: the button, and how the texture layers.</summary>
	/// <param name="Backed">
	/// True when the art is a flattened prompt whose silhouette carries the button's shadow
	/// (Fable 3's <c>complete</c> textures): a glyph drawn in its place sits on that silhouette in
	/// black, as the shipped orb sits on its shadow layer. False draws the glyph alone.
	/// </param>
	public readonly record struct GlyphArt(string Button, GlyphLayer Layer, bool Backed = false);

	private sealed class Proxy
	{
		public required ImageTexture Texture;
		public required Texture2D Original;
		public required string Button;
		public required int BaseWidth;
		public required int BaseHeight;
		public required int Scale;

		public int Width => BaseWidth * Scale;
		public int Height => BaseHeight * Scale;

		public GlyphLayer Layer;

		/// <summary><see cref="GlyphArt.Backed"/>.</summary>
		public bool Backed;

		/// <summary>A stick or D-pad prompt's direction (`LeftUp`), else none.</summary>
		public Direction Arrow;

		/// <summary>Player two's twin (Engine_UiKeyGlyphs.PlayerTwo.cs), drawn on first ask.</summary>
		public ImageTexture? Two;
	}

	private static readonly Dictionary<string, Proxy> _proxies = new(StringComparer.Ordinal);

	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Texture2D, Image> _sourceImages = new();

	/// <summary>
	/// Whether <see cref="Wrap"/> wraps <paramref name="virtualPath"/> in a glyph proxy that
	/// reads the CPU <see cref="Image"/> of the source texture.
	/// </summary>
	public static bool NeedsSourceImage(string virtualPath) =>
		ArtFor(virtualPath.Replace('\\', '/').ToLowerInvariant()) is not null;

	/// <summary>
	/// Registers a CPU copy of <paramref name="texture"/>'s <see cref="Image"/> so <see cref="Wrap"/>
	/// and <see cref="Draw"/> never need a synchronous GPU readback (<c>Texture2D.GetImage()</c>).
	/// </summary>
	public static void RegisterSourceImage(Texture2D texture, Image image) =>
		_sourceImages.AddOrUpdate(texture, image);

	private static Image? SourceImageOf(Texture2D texture) =>
		_sourceImages.TryGetValue(texture, out Image? cached) && !cached.IsEmpty()
			? cached
			: Refii.Core.Engine_ServerSyncs.Image(texture);

	private static int _drawnSignature;

	private static bool _drawnOnce;

	/// <summary>The buttons the `abyx` atlas's columns show, idle and pressed rows alike.</summary>
	private static readonly string[] AtlasColumns = { "A", "B", "Y", "X" };

	/// <summary>One keycap texture per pad button, redrawn when its label changes.</summary>
	private static readonly Dictionary<string, ImageTexture> _keycaps = new(StringComparer.Ordinal);

	private const int KeycapSize = 128;

	/// <summary>Every pad button a prompt can show, in the order the redraw signature lists them.</summary>
	private static readonly string[] Buttons =
		{ "A", "B", "X", "Y", "LB", "RB", "LT", "RT", "Start", "Back", "DPad", "LS", "RS" };

	/// <summary>Bumped whenever <see cref="Update"/> redraws, for a view that chooses between the shipped art and a glyph.</summary>
	public static int Generation { get; private set; }

	/// <summary>
	/// The keycap a quad carries for <paramref name="button"/> — one object per button and set: player
	/// two's inside a player-two surface (<see cref="Surface"/>).
	/// </summary>
	public static ImageTexture KeycapFor(string button)
	{
		if (SurfacePlayer == 2)
		{
			return KeycapTwo(button);
		}

		if (!_keycaps.TryGetValue(button, out ImageTexture? keycap))
		{
			keycap = Engine_TextureUpload.CreateTexture(Keycap(button));
			_keycaps[button] = keycap;
			_keycapButtons[keycap.GetInstanceId()] = button;
		}

		return keycap;
	}

	private static readonly Dictionary<string, Texture2D> _actionGlyphs = new();

	private static int _actionGlyphsFor = -1;

	/// <summary>
	/// The glyph for an input a mod binds itself: <paramref name="keyLabel"/>'s keycap while the keyboard
	/// and mouse are in use, <paramref name="padButton"/>'s pad glyph (a name of <see cref="Buttons"/>, or
	/// <c>DpadUp</c> … for one D-pad arrow) in the player's style and pack otherwise, which is the game's
	/// own art wherever its prompts would show it; null when the device in use has nothing bound. Redrawn
	/// with every other glyph (<see cref="Generation"/>).
	/// </summary>
	public static Texture2D? ActionGlyph(string? keyLabel, string? padButton)
	{
		bool keys = Engine_UiInputMode.Keycaps;
		string? shown = keys ? keyLabel : padButton;
		if (string.IsNullOrEmpty(shown))
		{
			return null;
		}

		if (_actionGlyphsFor != Generation)
		{
			_actionGlyphs.Clear();
			_actionGlyphsFor = Generation;
		}

		string cacheKey = (keys ? "k:" : "p:") + shown;
		if (_actionGlyphs.TryGetValue(cacheKey, out Texture2D? cached))
		{
			return cached;
		}

		if (!keys && ShippedActionGlyph(shown) is { } shipped)
		{
			_actionGlyphs[cacheKey] = shipped;
			return shipped;
		}

		var canvas = new Canvas(KeycapSize, KeycapSize);
		var cell = new Rect2I(0, 0, KeycapSize, KeycapSize);
		if (keys)
		{
			if (!BlitPack(canvas, cell, KeyPackName(shown)))
			{
				DrawKeycap(canvas, cell, shown, pressed: false);
			}
		}
		else
		{
			(string button, Direction arrow) = PadCellOf(shown);
			DrawPadCell(canvas, cell, button, arrow);
		}

		ImageTexture texture = Engine_TextureUpload.CreateTexture(canvas.ToImage(mipmaps: true));
		_actionGlyphs[cacheKey] = texture;
		return texture;
	}

	/// <summary>The prompt a bound pad input shows: its button, and which way when it is one D-pad arrow.</summary>
	private static (string Button, Direction Arrow) PadCellOf(string padButton) => padButton switch
	{
		"DpadUp" => ("DPad", Direction.Up),
		"DpadDown" => ("DPad", Direction.Down),
		"DpadLeft" => ("DPad", Direction.Left),
		"DpadRight" => ("DPad", Direction.Right),
		"L3" => ("LS", Direction.None),
		"R3" => ("RS", Direction.None),
		_ => (padButton, Direction.None),
	};

	/// <summary>
	/// The game's own texture for a prompt button, the paths its text markup names; null for one with none.
	/// </summary>
	internal static string? ShippedArtPath(string button) => button switch
	{
		"A" => "art/gui/controller/icon_button_a.tex",
		"B" => "art/gui/controller/icon_button_b.tex",
		"X" => "art/gui/controller/icon_button_x.tex",
		"Y" => "art/gui/controller/icon_button_y.tex",
		"LB" => "art/gui/controller/24x24/leftbutton.tex",
		"RB" => "art/gui/controller/24x24/rightbutton.tex",
		"LT" => "art/gui/controller/24x24/lefttrigger.tex",
		"RT" => "art/gui/controller/24x24/righttrigger.tex",
		"Start" => "art/gui/controller/24x24/start.tex",
		"Back" => "art/gui/controller/24x24/back.tex",
		"DPad" => "art/gui/controller/24x24/dpad.tex",
		"LS" => "art/gui/controller/24x24/left.tex",
		"RS" => "art/gui/controller/24x24/right.tex",
		_ => null,
	};

	/// <summary>
	/// The game's art for <paramref name="padButton"/>, wherever a prompt of the game's would show it: the
	/// Xbox style, not remapped, no pack file. The game has one D-pad picture, so one arrow is drawn on it.
	/// </summary>
	private static Texture2D? ShippedActionGlyph(string padButton)
	{
		(string button, Direction arrow) = PadCellOf(padButton);
		if (ShippedArtPath(button) is not { } path || !ShowsShipped(button) ||
			(arrow != Direction.None && Engine_UiGlyphPack.Find(PackName, PadPackName(Engine_PadRemap.FromGlyphButton(button), arrow)) is not null) ||
			Engine_HudModPieces.LoadAnyTexture(path) is not { } art)
		{
			return null;
		}

		if (arrow == Direction.None)
		{
			return art;
		}

		Image? image = SourceImageOf(art);
		if (image is null || image.IsEmpty())
		{
			return null;
		}

		// Blit reads RGBA8 at the top level; the game's textures come compressed, with mips.
		var rgba = (Image)image.Duplicate();
		if (rgba.IsCompressed())
		{
			rgba.Decompress();
		}

		rgba.ClearMipmaps();
		rgba.Convert(Image.Format.Rgba8);
		var canvas = new Canvas(KeycapSize, KeycapSize);
		canvas.Blit(rgba, new Rect2I(0, 0, KeycapSize, KeycapSize));
		Arrow(canvas, KeycapSize / 2f, KeycapSize / 2f, KeycapSize * 0.42f, KeycapSize * 0.13f, arrow, Lit);
		return Engine_TextureUpload.CreateTexture(canvas.ToImage(mipmaps: true));
	}

	/// <summary>
	/// Whether a prompt for <paramref name="button"/> shows its shipped Xbox art now — on the pad
	/// and <see cref="ShowsShipped"/> — rather than its keycap.
	/// </summary>
	public static bool PromptShowsShipped(string button) => !Engine_UiInputMode.Keycaps && ShowsShipped(button);

	/// <summary>The square glyph for a button: its keycap on the keyboard, its pad glyph on the pad.</summary>
	private static Image Keycap(string button)
	{
		var canvas = new Canvas(KeycapSize, KeycapSize);
		var cell = new Rect2I(0, 0, KeycapSize, KeycapSize);
		if (DrawnKeys(Engine_UiInputMode.Keycaps))
		{
			string label = LabelFor?.Invoke(button) ?? button;
			if (!BlitPack(canvas, cell, KeyPackName(label)))
			{
				DrawKeycap(canvas, cell, label, pressed: false);
			}
		}
		else
		{
			DrawPadCell(canvas, cell, button, Direction.None);
		}

		return canvas.ToImage(mipmaps: true);
	}

	/// <summary>
	/// Which pad button a quad cut from the `abyx` atlas draws, or null for anything else (the
	/// atlas's lower half is the hold-meter ring). The element's own name says it where it can
	/// — `A`, `X`, `icon_button_y`, `AButton`, `y_button` — up to three ancestors, since the
	/// prompt group is named and the quad under it often is not; the atlas column otherwise.
	/// </summary>
	public static string? AtlasButton(string texturePath, UiNode node, float uMin, float vMax)
	{
		string path = texturePath.Replace('\\', '/').ToLowerInvariant();
		if (!path.EndsWith("art/gui/persistant/abyx.tex", StringComparison.Ordinal) || vMax > 0.51f)
		{
			return null;
		}

		UiNode? at = node;
		for (int hop = 0; at is not null && hop <= 3; ++hop, at = at.Parent)
		{
			if (ButtonNamed(at.Name) is { } named)
			{
				return named;
			}
		}

		int column = Math.Clamp((int)Math.Floor((uMin * 4f) + 0.01f), 0, 3);
		return AtlasColumns[column];
	}

	/// <summary>
	/// The button the menu cursor's orb names: the game's A, its select (<c>GUISelect</c>).
	/// </summary>
	private const string OrbButton = "A";

	/// <summary>
	/// The menu cursor's select orb in `art/gui/frames/frames_elements.tex`, the art of every
	/// `MenuHighlight`'s `green_top` and `green_top_translucent`: a green A disc centred on texel
	/// (35.5, 394.5) of the 512 atlas, radius 21.5. As atlas UV.
	/// </summary>
	private const float OrbU = 35.5f / 512f, OrbV = 394.5f / 512f, OrbDisc = 21.5f / 512f;

	/// <summary>
	/// The radius a drawn orb covers, opaque: the green disc and the red B disc `rim_and_red` bakes
	/// beneath it (radius 21.5 about texel (68, 216); green_top's authored transform lays the green
	/// disc's centre within a texel of it), inside the bronze ring's outer edge (radius 34).
	/// </summary>
	private const float OrbCover = 23f / 512f;

	/// <summary>
	/// The atlas cell that holds the orb and no other art — the left column between the cursor's
	/// rim (above) and the stars (below) — as UV: u 0..0.25, v 0.5..0.875.
	/// </summary>
	private const float OrbCellUMax = 0.25f, OrbCellVMin = 0.5f, OrbCellVMax = 0.875f;

	/// <summary>
	/// Where a quad cut from `frames_elements.tex` shows the cursor's select orb, in the quad's own
	/// 0..1 space (x right, y down from <paramref name="vMin"/>; the covered radius against the
	/// quad's width) — or null when its cell does not hold the whole green disc inside the orb's
	/// cell. `rim_and_red` is not an orb quad: the orb drawn over it covers its red B.
	/// </summary>
	public static (float X, float Y, float Radius)? CursorOrb(string texturePath, float uMin, float uMax, float vMin, float vMax)
	{
		string path = texturePath.Replace('\\', '/').ToLowerInvariant();
		float du = uMax - uMin, dv = vMax - vMin;
		if (!path.EndsWith("art/gui/frames/frames_elements.tex", StringComparison.Ordinal) || du <= 0f || dv <= 0f ||
			uMin < 0f || uMax > OrbCellUMax || vMin < OrbCellVMin || vMax > OrbCellVMax ||
			OrbU - OrbDisc < uMin || OrbU + OrbDisc > uMax || OrbV - OrbDisc < vMin || OrbV + OrbDisc > vMax)
		{
			return null;
		}

		return ((OrbU - uMin) / du, (OrbV - vMin) / dv, OrbCover / du);
	}

	/// <summary>An orb quad's cell: the orb in the quad's 0..1 space and the cell's height over its width.</summary>
	private readonly record struct OrbCell(float X, float Y, float Radius, float Aspect);

	/// <summary>One orb texture per quad cell (every shipped cursor cuts the same one), redrawn with the keycaps.</summary>
	private static readonly Dictionary<(float, float, float, float), (OrbCell Cell, ImageTexture Texture)> _orbs = new();

	/// <summary>
	/// The texture an orb quad draws in place of its atlas cell while <c>ui_keyboard_mode</c> is set
	/// (<see cref="CursorOrb"/>), or null for any other quad: the orb the shipped art shows, covered
	/// by the active style's glyph for the physical input bound to the game's A.
	/// </summary>
	public static ImageTexture? OrbFor(string texturePath, float uMin, float uMax, float vMin, float vMax)
	{
		if (CursorOrb(texturePath, uMin, uMax, vMin, vMax) is not { } orb)
		{
			return null;
		}

		var key = (uMin, uMax, vMin, vMax);
		if (!_orbs.TryGetValue(key, out var known))
		{
			var cell = new OrbCell(orb.X, orb.Y, orb.Radius, (vMax - vMin) / (uMax - uMin));
			known = (cell, Engine_TextureUpload.CreateTexture(DrawOrb(cell)));
			_orbs[key] = known;
		}

		return known.Texture;
	}

	/// <summary>
	/// An orb: an opaque plate disc over the shipped orb and the red B beneath it, the glyph for the
	/// game's A drawn in the disc's square — the pack's file or the style's glyph on the pad, the
	/// keycap on the keyboard.
	/// </summary>
	private static Image DrawOrb(OrbCell cell)
	{
		int width = KeycapSize;
		int height = Math.Max(1, (int)Math.Round(KeycapSize * cell.Aspect));
		var canvas = new Canvas(width, height);
		float cx = cell.X * width, cy = cell.Y * height, r = cell.Radius * width;
		int side = (int)Math.Round(r * 2f);
		var disc = new Rect2I((int)Math.Round(cx - r), (int)Math.Round(cy - r), side, side);
		if (Engine_UiInputMode.Keycaps)
		{
			string label = LabelFor?.Invoke(OrbButton) ?? OrbButton;
			if (!BlitPack(canvas, disc, KeyPackName(label)))
			{
				DrawKeycap(canvas, disc, label, pressed: false);
			}
		}
		else
		{
			DrawPadCell(canvas, disc, OrbButton, Direction.None);
		}

		canvas.UnderCircle(cx, cy, r, PlateFill with { A = 1f });
		return canvas.ToImage(mipmaps: true);
	}

	private static string? ButtonNamed(string name)
	{
		string n = name.ToLowerInvariant();
		foreach (string b in new[] { "a", "b", "x", "y" })
		{
			if (n == b || n == b + "button" || n == b + "_button" || n == "button_" + b ||
				n.StartsWith("icon_button_" + b, StringComparison.Ordinal) && n.Length <= 14 + 2)
			{
				return b.ToUpperInvariant();
			}
		}

		return null;
	}

	/// <summary>
	/// The texture to hand out for a path: a proxy for a button glyph, the texture itself for
	/// anything else. Always the shared set's; a loader hands a surface its own through
	/// <see cref="ForSurface"/>.
	/// </summary>
	public static Texture2D Wrap(string virtualPath, Texture2D original)
	{
		string key = virtualPath.Replace('\\', '/').ToLowerInvariant();
		if (key.EndsWith("art/gui/persistant/abyx.tex", StringComparison.Ordinal))
		{
			PadAtlas = original;
		}

		if (ArtFor(key) is not { } art)
		{
			return original;
		}

		string button = art.Button;

		if (_proxies.TryGetValue(key, out Proxy? known))
		{
			return known.Texture;
		}

		Image? image = SourceImageOf(original);
		if (image is null || image.IsEmpty())
		{
			return original;
		}

		int baseWidth = Math.Max(1, image.GetWidth());
		int baseHeight = Math.Max(1, image.GetHeight());
		int scale = Math.Max(1, (int)Math.Ceiling(MinGlyphSide / (double)Math.Max(1, Math.Min(baseWidth, baseHeight))));
		Direction arrow = ArrowFor(key);
		Image initial = BuildProxyImage(image, button, art.Layer, art.Backed, arrow, baseWidth, baseHeight, scale, Engine_UiInputMode.Keycaps);
		var proxy = new Proxy
		{
			Texture = Engine_TextureUpload.CreateTexture(initial),
			Original = original,
			Button = button,
			BaseWidth = baseWidth,
			BaseHeight = baseHeight,
			Scale = scale,
			Layer = art.Layer,
			Backed = art.Backed,
			Arrow = arrow,
		};
		RegisterSourceImage(proxy.Texture, initial);
		_proxies[key] = proxy;
		_byTexture[proxy.Texture.GetInstanceId()] = proxy;

		// The enhancer swaps a sharper image into the original later; the pad art follows it.
		// (A drawn glyph keeps itself — it is redrawn on the next switch.)
		original.Changed += () =>
		{
			if (!Engine_UiInputMode.Keycaps && ShowsShipped(proxy.Button))
			{
				Draw(proxy, Engine_UiInputMode.Keycaps);
			}

			if (proxy.Two is not null && DrawTwo(() => ShowsShipped(proxy.Button)))
			{
				RedrawTwin(proxy);
			}
		};

		return proxy.Texture;
	}

	/// <summary>
	/// Redraws every glyph when the device or a label has changed. Once a frame; cheap when
	/// nothing has.
	/// </summary>
	public static void Update()
	{
		if (UpdateTwo())
		{
			++Generation;
		}

		bool keys = Engine_UiInputMode.Keycaps;
		var hash = new HashCode();
		hash.Add(keys);
		hash.Add(PackName);
		hash.Add(Engine_UiGlyphPack.Generation);
		if (keys)
		{
			foreach (string button in Buttons)
			{
				hash.Add(LabelFor?.Invoke(button));
			}
		}
		else
		{
			hash.Add(Style);
			hash.Add(PadFamily);
			hash.Add(GlyphPlayer);
			hash.Add(Engine_PadRemap.Generation);
		}

		int signature = hash.ToHashCode();
		if (_drawnOnce && signature == _drawnSignature)
		{
			return;
		}

		_drawnOnce = true;
		_drawnSignature = signature;
		++Generation;

		RenderingServer.GlobalShaderParameterSet("ui_keyboard_mode", !AtlasShowsShipped(keys));
		foreach (Proxy proxy in _proxies.Values)
		{
			Draw(proxy, keys);
		}

		foreach ((string button, ImageTexture keycap) in _keycaps)
		{
			Engine_TextureUpload.SetImage(keycap, Keycap(button), allowD3D12Replace: false);
		}

		foreach ((OrbCell cell, ImageTexture orb) in _orbs.Values)
		{
			Engine_TextureUpload.SetImage(orb, DrawOrb(cell), allowD3D12Replace: false);
		}
	}

	/// <summary>
	/// Whether the atlas quads — the `abyx` prompts and the cursor's orb — draw their shipped cells:
	/// on the pad while every face button is the shipped art. Otherwise <c>ui_keyboard_mode</c> is
	/// set and they draw their keycap textures.
	/// </summary>
	internal static bool AtlasShowsShipped(bool keys) =>
		!keys && ShowsShipped("A") && ShowsShipped("B") && ShowsShipped("X") && ShowsShipped("Y");

	/// <summary>The direction a stick prompt's art shows (`LeftUp`, `24x24/AnimRightLeft`), else none.</summary>
	private static Direction ArrowFor(string path)
	{
		string name = System.IO.Path.GetFileNameWithoutExtension(path);
		if (name.StartsWith("anim", StringComparison.Ordinal))
		{
			name = name["anim".Length..];
		}

		foreach (string stick in new[] { "left", "right" })
		{
			if (name.Length > stick.Length && name.StartsWith(stick, StringComparison.Ordinal))
			{
				return name[stick.Length..] switch
				{
					"up" => Direction.Up,
					"down" => Direction.Down,
					"left" => Direction.Left,
					"right" => Direction.Right,
					_ => Direction.None,
				};
			}
		}

		return Direction.None;
	}

	/// <summary>A texture path's button art: the running title's own first, then the shared table.</summary>
	private static GlyphArt? ArtFor(string path) =>
		Engine_TitleGame.Active.GlyphArtFor(path) ?? (ButtonFor(path) is { } button ? new GlyphArt(button, GlyphLayer.Whole) : null);

	/// <summary>Which pad button a texture path draws, or null for any other art.</summary>
	private static string? ButtonFor(string path)
	{
		const string Controller = "art/gui/controller/";
		int at = path.IndexOf(Controller, StringComparison.Ordinal);
		if (at < 0)
		{
			return null;
		}

		// `24x24/AnimLeftLeft` is the same stick as `LeftLeft`, drawn as frames.
		string name = System.IO.Path.GetFileNameWithoutExtension(path[(at + Controller.Length)..]);
		if (name.StartsWith("anim", StringComparison.Ordinal))
		{
			name = name["anim".Length..];
		}

		return name switch
		{
			"icon_button_a" => "A",
			"icon_button_b" => "B",
			"icon_button_x" => "X",
			"icon_button_y" => "Y",
			"icon_button_lt" or "lefttrigger" => "LT",
			"righttrigger" => "RT",
			"leftbutton" => "LB",
			"rightbutton" => "RB",
			"start" => "Start",
			"back" => "Back",
			"dpad" => "DPad",
			"left" or "leftup" or "leftdown" or "leftleft" or "leftright" => "LS",
			"right" or "rightup" or "rightdown" or "rightleft" or "rightright" => "RS",
			_ => null,
		};
	}

	/// <summary>
	/// The square, centred on the art's opaque pixels, that holds them — in the scaled canvas of
	/// <paramref name="cell"/> (the pad image is <paramref name="scale"/> times smaller) — or null when
	/// the cell's art is empty. Alpha above a quarter counts: the orbs' soft glow does not.
	/// </summary>
	private static Rect2I? OpaqueFootprint(Image pad, Rect2I cell, int scale)
	{
		if (pad.IsCompressed())
		{
			pad = (Image)pad.Duplicate();
			if (pad.Decompress() != Error.Ok)
			{
				return null;
			}
		}

		int x0 = cell.Position.X / scale, y0 = cell.Position.Y / scale;
		int x1 = Math.Min(pad.GetWidth(), cell.End.X / scale), y1 = Math.Min(pad.GetHeight(), cell.End.Y / scale);
		int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
		for (int y = y0; y < y1; ++y)
		{
			for (int x = x0; x < x1; ++x)
			{
				if (pad.GetPixel(x, y).A > 0.25f)
				{
					minX = Math.Min(minX, x);
					minY = Math.Min(minY, y);
					maxX = Math.Max(maxX, x);
					maxY = Math.Max(maxY, y);
				}
			}
		}

		if (maxX < 0)
		{
			return null;
		}

		// A square key about the footprint's centre, as large as its larger side, kept in the cell.
		float cx = (minX + maxX + 1) * 0.5f * scale, cy = (minY + maxY + 1) * 0.5f * scale;
		int side = Math.Min(cell.Size.X, Math.Max(maxX - minX + 1, maxY - minY + 1) * scale);
		int left = Math.Clamp((int)Math.Round(cx - (side / 2f)), cell.Position.X, cell.End.X - side);
		int top = Math.Clamp((int)Math.Round(cy - (side / 2f)), cell.Position.Y, cell.End.Y - side);
		return new Rect2I(left, top, side, side);
	}

	/// <summary>
	/// Lays black beneath what the cell holds, at the shipped art's own alpha (destination-over):
	/// the shadow a flattened prompt carries. Fable 3's layered prompts draw it as their own
	/// element — <c>Icon.Container.A_shadow</c>, behind <c>A_Orb</c> (z 0.4 against 0.3, same
	/// position and scale 0.32 in <c>interactionmode.bgf</c>), which <c>sub_82961340</c> and
	/// <c>sub_829614A0</c> never retexture — and <c>a_shadow.tex</c> is a constant-colour plane of
	/// 0 (compFlag 6, record word 1 = 0: black, <c>sub_82D6E9C8</c> 0x82D6EE0C..0x82D6EE78) under a
	/// disc alpha. The <c>complete</c> art is that stack flattened, its alpha covering the shadow's.
	/// </summary>
	private static void UnderSilhouette(Canvas canvas, Image pad, Rect2I cell, int scale)
	{
		if (pad.IsCompressed())
		{
			pad = (Image)pad.Duplicate();
			if (pad.Decompress() != Error.Ok)
			{
				return;
			}
		}

		int x0 = cell.Position.X / scale, y0 = cell.Position.Y / scale;
		int x1 = Math.Min(pad.GetWidth(), cell.End.X / scale), y1 = Math.Min(pad.GetHeight(), cell.End.Y / scale);
		for (int y = y0; y < y1; ++y)
		{
			for (int x = x0; x < x1; ++x)
			{
				float alpha = pad.GetPixel(x, y).A;
				if (alpha <= 0f)
				{
					continue;
				}

				for (int dy = 0; dy < scale; ++dy)
				{
					for (int dx = 0; dx < scale; ++dx)
					{
						canvas.Under((x * scale) + dx, (y * scale) + dy, Colors.Black, alpha);
					}
				}
			}
		}
	}

	private static Image ToNormalizedRgba8(Image source, int targetWidth, int targetHeight, bool generateMipmaps)
	{
		var rgba = (Image)source.Duplicate();
		if (rgba.IsCompressed())
		{
			rgba.Decompress();
		}

		rgba.ClearMipmaps();
		if (rgba.GetFormat() != Image.Format.Rgba8)
		{
			rgba.Convert(Image.Format.Rgba8);
		}

		if (rgba.GetWidth() != targetWidth || rgba.GetHeight() != targetHeight)
		{
			rgba.Resize(targetWidth, targetHeight, Image.Interpolation.Cubic);
		}

		if (generateMipmaps)
		{
			rgba.GenerateMipmaps();
		}

		return rgba;
	}

	private static Image BuildProxyImage(
		Image pad,
		string button,
		GlyphLayer layer,
		bool backed,
		Direction arrow,
		int baseWidth,
		int baseHeight,
		int scale,
		bool keys)
	{
		int width = baseWidth * scale;
		int height = baseHeight * scale;

		// Keep every proxy update at a fixed RGBA8 + mipmaps layout matching `width x height` so
		// `ImageTexture.SetImage` always performs an in-place `RS::texture_2d_update` rather than
		// `RS::texture_replace` (never destroying a live ID3D12Resource mid-frame on D3D12).
		if (!keys && ShowsShipped(button))
		{
			return ToNormalizedRgba8(pad, width, height, generateMipmaps: true);
		}

		if (layer == GlyphLayer.Overlay)
		{
			// The orb under it carries the glyph; the letter over it would draw on top of the keycap.
			return new Canvas(width, height).ToImage(mipmaps: true);
		}

		// Ensure footprint/silhouette sampling uses the initial base dimensions even if the source
		// texture was later replaced by a 4x enhanced image.
		Image padAtBase = pad.GetWidth() == baseWidth && pad.GetHeight() == baseHeight
			? pad
			: ToNormalizedRgba8(pad, baseWidth, baseHeight, generateMipmaps: false);

		// One glyph per square cell along the long side, so an animated strip shows a whole key
		// in whichever frame its UVs select; a square texture is one cell.
		// A small original (Fable 3's prompts are 32 px) is drawn at a whole multiple of its size, so the
		// letter stays legible; the quads' UVs are fractions, so every cut still fits.
		int side = Math.Min(width, height);
		string label = keys ? LabelFor?.Invoke(button) ?? button : string.Empty;
		var canvas = new Canvas(width, height);
		for (int y = 0; y + side <= height; y += side)
		{
			for (int x = 0; x + side <= width; x += side)
			{
				var cell = new Rect2I(x, y, side, side);
				if (!keys)
				{
					DrawPadCell(canvas, cell, button, arrow);
				}
				else
				{
					// THE KEY COVERS WHAT THE ORB COVERED. The shipped art's transparent margin is
					// the gap the layout leaves between a prompt's label and its glyph (Fable 3's
					// `24x24_NoRim` orbs sit inside their cell); a keycap filling the whole cell ran
					// into the label. An orb that fills its cell gives the cell back unchanged.
					Rect2I key = OpaqueFootprint(padAtBase, cell, scale) ?? cell;
					if (!BlitPack(canvas, key, KeyPackName(label)))
					{
						DrawKeycap(canvas, key, label, pressed: false);
					}
				}

				if (backed)
				{
					UnderSilhouette(canvas, padAtBase, cell, scale);
				}
			}
		}

		return canvas.ToImage(mipmaps: true);
	}

	private static void Draw(Proxy proxy, bool keys)
	{
		Image? pad = SourceImageOf(proxy.Original);
		if (pad is null || pad.IsEmpty())
		{
			return;
		}

		Image drawn = BuildProxyImage(
			pad,
			proxy.Button,
			proxy.Layer,
			proxy.Backed,
			proxy.Arrow,
			proxy.BaseWidth,
			proxy.BaseHeight,
			proxy.Scale,
			keys);
		RegisterSourceImage(proxy.Texture, drawn);
		Engine_TextureUpload.SetImage(proxy.Texture, drawn, allowD3D12Replace: false);
	}

	/// <summary>The smallest side a drawn glyph is rasterised at.</summary>
	private const int MinGlyphSide = 128;

	/// <summary>
	/// The physical input a pad prompt for <paramref name="button"/> shows: the remap's for the
	/// game's buttons; the stick or D-pad itself for the sticks and the D-pad, which are not
	/// remapped as prompts (<paramref name="unbound"/> true when the game button has no source).
	/// </summary>
	private static PadInput? PhysicalFor(string button, out bool unbound)
	{
		unbound = false;
		if (button is "LS" or "RS" or "DPad" || Engine_PadRemap.FromGlyphButton(button) is not { } game)
		{
			return Engine_PadRemap.FromGlyphButton(button);
		}

		PadInput? shown = Engine_PadRemap.For(DrawnPlayer).Shown(game);
		unbound = shown is null;
		return shown;
	}

	/// <summary>Whether the pad prompt for <paramref name="button"/> is the shipped Xbox art.</summary>
	private static bool ShowsShipped(string button)
	{
		if (DrawnStyle != GlyphStyle.Xbox)
		{
			return false;
		}

		PadInput? physical = PhysicalFor(button, out bool unbound);
		bool alone = !unbound && physical == Engine_PadRemap.FromGlyphButton(button);
		return alone && KeepsShippedArt(false, DrawnStyle, alone,
			Engine_UiGlyphPack.Find(PackName, PadPackName(physical, Direction.None)) is not null);
	}

	/// <summary>
	/// Whether a prompt keeps the shipped Xbox art: on the pad (not <paramref name="keys"/>), in the
	/// Xbox style, its game button pressed by itself alone, and no pack file for that input.
	/// </summary>
	internal static bool KeepsShippedArt(bool keys, GlyphStyle style, bool pressedByItself, bool packed) =>
		!keys && style == GlyphStyle.Xbox && pressedByItself && !packed;

	/// <summary>
	/// One pad cell for <paramref name="button"/>: the pack's file, else the style's glyph — an
	/// empty cell while the game button is unbound, as an unbound key's keycap is.
	/// </summary>
	private static void DrawPadCell(Canvas canvas, Rect2I cell, string button, Direction arrow)
	{
		PadInput? physical = PhysicalFor(button, out bool unbound);
		if (unbound)
		{
			canvas.Clear(cell);
			return;
		}

		if (BlitPack(canvas, cell, PadPackName(physical, arrow)))
		{
			return;
		}

		bool ps5 = DrawnFamily == ControllerFamily.PlayStation5;
		DrawPadGlyph(canvas, cell, new PadGlyph(physical, arrow, button is "LS" or "RS"), DrawnStyle, ps5);
	}

	/// <summary>A pad input's file name in a pack (see <see cref="Engine_UiGlyphPack"/>).</summary>
	private static string PadPackName(PadInput? input, Direction arrow) => input switch
	{
		null => arrow switch
		{
			Direction.Up => "dpad_up",
			Direction.Down => "dpad_down",
			Direction.Left => "dpad_left",
			Direction.Right => "dpad_right",
			_ => "dpad",
		},
		PadInput.DpadUp => "dpad_up",
		PadInput.DpadDown => "dpad_down",
		PadInput.DpadLeft => "dpad_left",
		PadInput.DpadRight => "dpad_right",
		PadInput.L3 => "ls",
		PadInput.R3 => "rs",
		{ } named => named.ToString().ToLowerInvariant(),
	};

	/// <summary>A keycap's file name in a pack: <c>key_</c> + its label lowercased, spaces dropped.</summary>
	private static string KeyPackName(string label) =>
		label.Length == 0 ? string.Empty : "key_" + label.Replace(" ", string.Empty).ToLowerInvariant();

	/// <summary>
	/// Draws the selected pack's <paramref name="name"/> scaled into <paramref name="cell"/>; false
	/// when the pack has no such file (also tried as <c>l3</c>/<c>r3</c> for a stick).
	/// </summary>
	private static bool BlitPack(Canvas canvas, Rect2I cell, string name)
	{
		if (PackName.Length == 0 || name.Length == 0)
		{
			return false;
		}

		Image? art = Engine_UiGlyphPack.Find(PackName, name) ??
			(name is "ls" or "rs" ? Engine_UiGlyphPack.Find(PackName, name == "ls" ? "l3" : "r3") : null);
		if (art is null)
		{
			return false;
		}

		canvas.Clear(cell);
		canvas.Blit(art, cell);
		return true;
	}

	/// <summary>
	/// An RGBA8 image drawn in memory and handed to Godot once. Each pixel is stored exactly as
	/// <c>Image.SetPixel</c> stores it and read back exactly as <c>Image.GetPixel</c> reads it
	/// (core/io/image.cpp <c>_set_color_at_ofs</c>/<c>_get_color_at_ofs</c>, FORMAT_RGBA8), so
	/// the keycap is the same picture a pixel-at-a-time draw gave — without a native call per
	/// pixel, which was tens of milliseconds for a redraw of every prompt on a device switch.
	/// </summary>
	internal sealed class Canvas
	{
		public readonly int Width;
		public readonly int Height;
		public readonly byte[] Data;

		public Canvas(int width, int height)
		{
			Width = width;
			Height = height;
			Data = new byte[width * height * 4];
		}

		public Color Get(int x, int y)
		{
			int at = ((y * Width) + x) * 4;
			return new Color(
				(float)(Data[at] / 255.0),
				(float)(Data[at + 1] / 255.0),
				(float)(Data[at + 2] / 255.0),
				(float)(Data[at + 3] / 255.0));
		}

		public void Set(int x, int y, Color colour)
		{
			int at = ((y * Width) + x) * 4;
			Data[at] = Channel(colour.R);
			Data[at + 1] = Channel(colour.G);
			Data[at + 2] = Channel(colour.B);
			Data[at + 3] = Channel(colour.A);
		}

		/// <summary>Clears a cell to transparent black, as <c>FillRect</c> with (0,0,0,0) does.</summary>
		public void Clear(Rect2I cell)
		{
			int x0 = Math.Max(0, cell.Position.X);
			int x1 = Math.Min(Width, cell.End.X);
			int y0 = Math.Max(0, cell.Position.Y);
			int y1 = Math.Min(Height, cell.End.Y);
			for (int y = y0; y < y1; ++y)
			{
				Array.Clear(Data, ((y * Width) + x0) * 4, Math.Max(0, x1 - x0) * 4);
			}
		}

		public Image ToImage(bool mipmaps)
		{
			Image image = Image.CreateFromData(Width, Height, false, Image.Format.Rgba8, Data);
			if (mipmaps)
			{
				image.GenerateMipmaps();
			}

			return image;
		}

		/// <summary>Lays <paramref name="colour"/> over a pixel at <paramref name="coverage"/> (0..1), source-over.</summary>
		public void Blend(int x, int y, Color colour, float coverage)
		{
			if (x < 0 || y < 0 || x >= Width || y >= Height || coverage <= 0f)
			{
				return;
			}

			float a = colour.A * Math.Min(1f, coverage);
			Color under = Get(x, y);
			float outA = a + (under.A * (1f - a));
			if (outA <= 0f)
			{
				return;
			}

			Set(x, y, new Color(
				((colour.R * a) + (under.R * under.A * (1f - a))) / outA,
				((colour.G * a) + (under.G * under.A * (1f - a))) / outA,
				((colour.B * a) + (under.B * under.A * (1f - a))) / outA,
				outA));
		}

		/// <summary>Lays <paramref name="colour"/> beneath a pixel at <paramref name="coverage"/> (0..1), destination-over.</summary>
		public void Under(int x, int y, Color colour, float coverage)
		{
			if (x < 0 || y < 0 || x >= Width || y >= Height || coverage <= 0f)
			{
				return;
			}

			float a = colour.A * Math.Min(1f, coverage);
			Color top = Get(x, y);
			float outA = top.A + (a * (1f - top.A));
			if (outA <= 0f)
			{
				return;
			}

			Set(x, y, new Color(
				((top.R * top.A) + (colour.R * a * (1f - top.A))) / outA,
				((top.G * top.A) + (colour.G * a * (1f - top.A))) / outA,
				((top.B * top.A) + (colour.B * a * (1f - top.A))) / outA,
				outA));
		}

		/// <summary>Fills every pixel whose centre <paramref name="distance"/> puts inside (negative), antialiased over one pixel.</summary>
		private void Fill(float x0, float y0, float x1, float y1, Func<float, float, float> distance, Color colour)
		{
			int ix0 = Math.Max(0, (int)Math.Floor(x0) - 1), ix1 = Math.Min(Width, (int)Math.Ceiling(x1) + 1);
			int iy0 = Math.Max(0, (int)Math.Floor(y0) - 1), iy1 = Math.Min(Height, (int)Math.Ceiling(y1) + 1);
			for (int y = iy0; y < iy1; ++y)
			{
				for (int x = ix0; x < ix1; ++x)
				{
					float d = distance(x + 0.5f, y + 0.5f);
					if (d < 0.5f)
					{
						Blend(x, y, colour, Mathf.Clamp(0.5f - d, 0f, 1f));
					}
				}
			}
		}

		public void FillCircle(float cx, float cy, float r, Color colour) =>
			Fill(cx - r, cy - r, cx + r, cy + r, (x, y) => new Vector2(x - cx, y - cy).Length() - r, colour);

		/// <summary>Lays <paramref name="colour"/> beneath what is drawn inside a circle (destination-over), antialiased over one pixel.</summary>
		public void UnderCircle(float cx, float cy, float r, Color colour)
		{
			int x0 = Math.Max(0, (int)Math.Floor(cx - r) - 1), x1 = Math.Min(Width, (int)Math.Ceiling(cx + r) + 1);
			int y0 = Math.Max(0, (int)Math.Floor(cy - r) - 1), y1 = Math.Min(Height, (int)Math.Ceiling(cy + r) + 1);
			for (int y = y0; y < y1; ++y)
			{
				for (int x = x0; x < x1; ++x)
				{
					float coverage = Mathf.Clamp(0.5f - (new Vector2(x + 0.5f - cx, y + 0.5f - cy).Length() - r), 0f, 1f);
					if (coverage <= 0f)
					{
						continue;
					}

					float a = colour.A * coverage;
					Color over = Get(x, y);
					float outA = over.A + (a * (1f - over.A));
					if (outA <= 0f)
					{
						continue;
					}

					Set(x, y, new Color(
						((over.R * over.A) + (colour.R * a * (1f - over.A))) / outA,
						((over.G * over.A) + (colour.G * a * (1f - over.A))) / outA,
						((over.B * over.A) + (colour.B * a * (1f - over.A))) / outA,
						outA));
				}
			}
		}

		/// <summary>A ring of <paramref name="width"/> centred on radius <paramref name="r"/>.</summary>
		public void StrokeCircle(float cx, float cy, float r, float width, Color colour) =>
			Fill(cx - r - width, cy - r - width, cx + r + width, cy + r + width,
				(x, y) => Math.Abs(new Vector2(x - cx, y - cy).Length() - r) - (width / 2f), colour);

		public void FillRounded(Rect2 rect, float radius, Color colour) =>
			Fill(rect.Position.X, rect.Position.Y, rect.End.X, rect.End.Y,
				(x, y) => Rounded(x, y, rect, radius), colour);

		/// <summary>A rounded rectangle's outline, <paramref name="width"/> wide, inside its edge.</summary>
		public void StrokeRounded(Rect2 rect, float radius, float width, Color colour) =>
			Fill(rect.Position.X, rect.Position.Y, rect.End.X, rect.End.Y,
				(x, y) => Math.Abs(Rounded(x, y, rect, radius) + (width / 2f)) - (width / 2f), colour);

		/// <summary>A round-capped line <paramref name="width"/> wide.</summary>
		public void Line(float ax, float ay, float bx, float by, float width, Color colour)
		{
			var a = new Vector2(ax, ay);
			var ab = new Vector2(bx, by) - a;
			float length2 = Math.Max(1e-6f, ab.LengthSquared());
			float pad = width;
			Fill(Math.Min(ax, bx) - pad, Math.Min(ay, by) - pad, Math.Max(ax, bx) + pad, Math.Max(ay, by) + pad,
				(x, y) =>
				{
					var p = new Vector2(x, y) - a;
					float t = Mathf.Clamp(p.Dot(ab) / length2, 0f, 1f);
					return (p - (ab * t)).Length() - (width / 2f);
				}, colour);
		}

		public void FillTriangle(float ax, float ay, float bx, float by, float cx, float cy, Color colour)
		{
			float sign = ((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax)) < 0f ? -1f : 1f;
			float Edge(float px, float py, float x0, float y0, float x1, float y1)
			{
				var e = new Vector2(x1 - x0, y1 - y0);
				float length = Math.Max(1e-6f, e.Length());
				return sign * (((px - x0) * e.Y) - ((py - y0) * e.X)) / length;
			}

			Fill(Math.Min(ax, Math.Min(bx, cx)), Math.Min(ay, Math.Min(by, cy)),
				Math.Max(ax, Math.Max(bx, cx)), Math.Max(ay, Math.Max(by, cy)),
				(x, y) => Math.Max(Edge(x, y, ax, ay, bx, by), Math.Max(Edge(x, y, bx, by, cx, cy), Edge(x, y, cx, cy, ax, ay))),
				colour);
		}

		/// <summary>Draws <paramref name="art"/> (RGBA8) scaled to fit <paramref name="cell"/>, centred, aspect kept.</summary>
		public void Blit(Image art, Rect2I cell)
		{
			int aw = art.GetWidth(), ah = art.GetHeight();
			if (aw <= 0 || ah <= 0 || cell.Size.X <= 0 || cell.Size.Y <= 0)
			{
				return;
			}

			float scale = Math.Min(cell.Size.X / (float)aw, cell.Size.Y / (float)ah);
			int w = Math.Max(1, (int)Math.Round(aw * scale)), h = Math.Max(1, (int)Math.Round(ah * scale));
			Image sized = art;
			if (w != aw || h != ah)
			{
				sized = (Image)art.Duplicate();
				sized.Resize(w, h, Image.Interpolation.Lanczos);
			}

			byte[] data = sized.GetData();
			int ox = cell.Position.X + ((cell.Size.X - w) / 2), oy = cell.Position.Y + ((cell.Size.Y - h) / 2);
			for (int y = 0; y < h; ++y)
			{
				for (int x = 0; x < w; ++x)
				{
					int at = ((y * w) + x) * 4;
					if (at + 3 >= data.Length)
					{
						return;
					}

					Blend(ox + x, oy + y, new Color(data[at] / 255f, data[at + 1] / 255f, data[at + 2] / 255f, 1f), data[at + 3] / 255f);
				}
			}
		}

		private static float Rounded(float x, float y, Rect2 rect, float radius)
		{
			float cx = rect.Position.X + (rect.Size.X / 2f);
			float cy = rect.Position.Y + (rect.Size.Y / 2f);
			radius = Math.Min(radius, Math.Min(rect.Size.X, rect.Size.Y) / 2f);
			float qx = Math.Abs(x - cx) - ((rect.Size.X / 2f) - radius);
			float qy = Math.Abs(y - cy) - ((rect.Size.Y / 2f) - radius);
			float outside = new Vector2(Math.Max(qx, 0f), Math.Max(qy, 0f)).Length();
			return outside + Math.Min(Math.Max(qx, qy), 0f) - radius;
		}

		/// <summary><c>uint8_t(CLAMP(c * 255.0, 0, 255))</c>: scaled in double, truncated.</summary>
		private static byte Channel(float c) => (byte)Math.Clamp(c * 255.0, 0.0, 255.0);
	}

	// The touch controls' plate and trim colours, PadAtlas and TextImage: android/Engine_UiKeyGlyphs.Touch.cs.

	private static readonly Color CapFill = new(0.10f, 0.09f, 0.07f, 0.92f);
	private static readonly Color CapRim = new(1f, 224f / 255f, 128f / 255f, 1f);
	private static readonly Color CapPressed = new(0.22f, 0.19f, 0.12f, 0.95f);

	/// <summary>A rounded key with the label centred in the game's font, filling one cell.</summary>
	private static void DrawKeycap(Canvas image, Rect2I cell, string label, bool pressed)
	{
		image.Clear(cell);

		// No key for this prompt here: the cell stays empty rather than showing a blank cap.
		if (label.Length == 0)
		{
			return;
		}

		int inset = Math.Max(1, cell.Size.X / 16);
		var cap = new Rect2I(cell.Position + new Vector2I(inset, inset), cell.Size - new Vector2I(inset * 2, inset * 2));
		float radius = Math.Min(cap.Size.X, cap.Size.Y) * 0.22f;
		float rim = Math.Max(1f, cap.Size.X / 20f);

		for (int y = cap.Position.Y; y < cap.End.Y; ++y)
		{
			for (int x = cap.Position.X; x < cap.End.X; ++x)
			{
				float d = RoundedDistance(x + 0.5f, y + 0.5f, cap, radius);
				if (d > 0.5f)
				{
					continue;
				}

				float edge = Mathf.Clamp(0.5f - d, 0f, 1f);
				Color colour = d > -rim ? CapRim : pressed ? CapPressed : CapFill;
				image.Set(x, y, new Color(colour.R, colour.G, colour.B, colour.A * edge));
			}
		}

		DrawLabel(image, cap, label, CapRim);
	}

	/// <summary>Signed distance to a rounded rectangle's edge, negative inside.</summary>
	private static float RoundedDistance(float x, float y, Rect2I rect, float radius)
	{
		float cx = rect.Position.X + (rect.Size.X / 2f);
		float cy = rect.Position.Y + (rect.Size.Y / 2f);
		float qx = Math.Abs(x - cx) - ((rect.Size.X / 2f) - radius);
		float qy = Math.Abs(y - cy) - ((rect.Size.Y / 2f) - radius);
		float outside = new Vector2(Math.Max(qx, 0f), Math.Max(qy, 0f)).Length();
		return outside + Math.Min(Math.Max(qx, qy), 0f) - radius;
	}

	/// <summary>The decoded font atlas as coverage, cached per font: its red channel, one byte a texel.</summary>
	private static byte[]? _atlas;
	private static int _atlasWidth;
	private static int _atlasHeight;
	private static int _atlasStride;
	private static Engine_BitmapFont? _atlasFont;

	/// <summary>Decodes a font's atlas into coverage once per font (<see cref="_atlas"/>).</summary>
	private static void EnsureFontAtlas(Engine_BitmapFont font)
	{
		if (!ReferenceEquals(_atlasFont, font))
		{
			_atlasFont = font;
			_atlas = null;
			Image decoded = Image.CreateFromData(font.AtlasWidth, font.AtlasHeight, false,
				Image.Format.RgtcR, Engine_BitmapFontTexture.DecodeAtlasBlocks(font));
			if (!decoded.IsEmpty())
			{
				decoded.Decompress();
				int stride = decoded.GetFormat() switch
				{
					Image.Format.L8 or Image.Format.R8 => 1,
					Image.Format.La8 or Image.Format.Rg8 => 2,
					Image.Format.Rgb8 => 3,
					Image.Format.Rgba8 => 4,
					_ => 0,
				};
				if (stride == 0)
				{
					decoded.Convert(Image.Format.Rgba8);
					stride = 4;
				}

				_atlas = decoded.GetData();
				_atlasWidth = decoded.GetWidth();
				_atlasHeight = decoded.GetHeight();
				_atlasStride = stride;
			}
		}
	}

	private static void DrawLabel(Canvas image, Rect2I cap, string label, Color colour)
	{
		if (Fonts?.For(Fonts.Contract.NormalSize) is not { } selection || label.Length == 0)
		{
			return;
		}

		Engine_BitmapFont font = selection.Font.Font;
		EnsureFontAtlas(font);
		if (_atlas is null)
		{
			return;
		}

		float width = font.MeasureWidth(label);
		float height = font.LineHeight + 1;
		float scale = Math.Min(cap.Size.X * 0.78f / Math.Max(1f, width), cap.Size.Y * 0.62f / height);
		float penX = cap.Position.X + ((cap.Size.X - (width * scale)) / 2f);
		float top = cap.Position.Y + ((cap.Size.Y - (height * scale)) / 2f);

		foreach (char c in label)
		{
			BitmapGlyph g = font.GetGlyph(c);
			float left = penX + (g.LeftBearing * scale);
			penX = left + (g.PostAdvance * scale);
			if (g.DrawWidth <= 0)
			{
				continue;
			}

			BlitGlyph(image, g, left, top, g.DrawWidth * scale, height * scale, colour);
		}
	}

	/// <summary>Draws one glyph's coverage as the rim colour, sampled bilinearly.</summary>
	private static void BlitGlyph(Canvas image, BitmapGlyph g, float left, float top,
		float width, float height, Color colour)
	{
		int x0 = Math.Max(0, (int)Math.Floor(left));
		int x1 = Math.Min(image.Width, (int)Math.Ceiling(left + width));
		int y0 = Math.Max(0, (int)Math.Floor(top));
		int y1 = Math.Min(image.Height, (int)Math.Ceiling(top + height));
		int aw = _atlasWidth;
		int ah = _atlasHeight;

		for (int y = y0; y < y1; ++y)
		{
			float v = Mathf.Lerp(g.V0, g.V1, (y + 0.5f - top) / height);
			for (int x = x0; x < x1; ++x)
			{
				float u = Mathf.Lerp(g.U0, g.U1, (x + 0.5f - left) / width);
				float coverage = Sample((u * aw) - 0.5f, (v * ah) - 0.5f);
				if (coverage <= 0.01f)
				{
					continue;
				}

				Color under = image.Get(x, y);
				Color blended = under.Lerp(colour, coverage);
				blended.A = Math.Max(under.A, coverage);
				image.Set(x, y, blended);
			}
		}
	}

	private static float Sample(float x, float y)
	{
		int xi = (int)Math.Floor(x);
		int yi = (int)Math.Floor(y);
		float fx = x - xi;
		float fy = y - yi;
		float a = Texel(xi, yi);
		float b = Texel(xi + 1, yi);
		float c = Texel(xi, yi + 1);
		float d = Texel(xi + 1, yi + 1);
		return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
	}

	/// <summary>The atlas's red channel at a texel as <c>GetPixel(x, y).R</c> read it, 0 outside.</summary>
	private static float Texel(int x, int y) =>
		x < 0 || y < 0 || x >= _atlasWidth || y >= _atlasHeight
			? 0f
			: (float)(_atlas![((y * _atlasWidth) + x) * _atlasStride] / 255.0);
}
