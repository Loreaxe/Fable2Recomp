using System;
using System.Collections.Generic;
using Godot;
using Refii.Core;
using Refii.Textures;

namespace Refii.Ui;

/// <summary>
/// PLAYER TWO'S OWN GLYPHS. The shared proxies follow one device, the prompts' player
/// (<see cref="GlyphPlayer"/>, <see cref="PadFamily"/>). A surface that is player two's alone — their
/// half of a split screen (the HUD view seated <see cref="Engine_UiSeats.Seat.PlayerTwo"/>, their
/// world icons) — draws a second set: every proxy's twin and every keycap's, drawn in player two's
/// controller family (<see cref="PadFamilyTwo"/>) through player two's remap, never as keycaps (the
/// keyboard and mouse are player one's).
/// </summary>
/// <remarks>
/// <para>A SURFACE NAMES ITS PLAYER (<see cref="Surface"/>): while a player-two surface is built or
/// laid out, the shared texture cache, <see cref="Wrap"/> and <see cref="KeycapFor"/> hand out player
/// two's twins (<see cref="ForSurface"/>), and a clone of the pattern book's art — whose materials the
/// book built with player one's set — is turned to player two's (<see cref="TakePlayerTwo"/>).</para>
/// <para>THE ATLAS QUADS (`abyx`, the cursor orb) choose between their atlas cell and their keycap on
/// a global, one per set: <c>ui_keyboard_mode</c> for the shared set, <c>ui_keyboard_mode_two</c> for
/// a quad whose material says <c>glyph_player_two</c>. No material is walked on a device switch.</para>
/// </remarks>
public static partial class Engine_UiKeyGlyphs
{
	/// <summary>The family of player two's own controller (local co-op), Unknown without one.</summary>
	public static ControllerFamily PadFamilyTwo { get; set; }

	/// <summary>The style player two's set is drawn in.</summary>
	public static GlyphStyle StyleTwo => Engine_ControllerFamily.Resolve(StyleSetting, PadFamilyTwo);

	/// <summary>The set the surface being built or laid out takes: 1 the shared set, 2 player two's.</summary>
	public static int SurfacePlayer { get; private set; } = 1;

	/// <summary>Restores the surface player it was opened over.</summary>
	public readonly struct SurfaceScope : IDisposable
	{
		private readonly int _was;

		internal SurfaceScope(int was) => _was = was;

		public void Dispose() => SurfacePlayer = _was;
	}

	/// <summary>
	/// Opens a surface for <paramref name="player"/>'s glyphs (2: player two's own set; anything else
	/// the shared set) until the scope is disposed. Main thread only, as every UI build is.
	/// </summary>
	public static SurfaceScope Surface(int player)
	{
		var scope = new SurfaceScope(SurfacePlayer);
		SurfacePlayer = Engine_ControllerFamily.SurfaceSet(player);
		return scope;
	}

	/// <summary>The set being drawn: true while player two's twins and keycaps are drawn.</summary>
	private static bool _drawingTwo;

	private static GlyphStyle DrawnStyle => _drawingTwo ? StyleTwo : Style;

	private static ControllerFamily DrawnFamily => _drawingTwo ? PadFamilyTwo : PadFamily;

	private static int DrawnPlayer => _drawingTwo ? 2 : GlyphPlayer;

	private static bool DrawnKeys(bool keys) => !_drawingTwo && keys;

	/// <summary>Each shared proxy by its texture's instance id, for <see cref="ForSurface"/> and <see cref="TakePlayerTwo"/>.</summary>
	private static readonly Dictionary<ulong, Proxy> _byTexture = new();

	/// <summary>Player two's keycaps, one per button, drawn with player two's set.</summary>
	private static readonly Dictionary<string, ImageTexture> _keycapsTwo = new(StringComparer.Ordinal);

	/// <summary>The button each shared keycap texture stands for, for <see cref="TakePlayerTwo"/>.</summary>
	private static readonly Dictionary<ulong, string> _keycapButtons = new();

	private static int _drawnSignatureTwo;

	private static bool _drawnOnceTwo;

	/// <summary>
	/// The texture a surface takes for <paramref name="texture"/>: player two's twin of a shared glyph
	/// proxy inside a player-two surface, else the texture itself.
	/// </summary>
	public static Texture2D? ForSurface(Texture2D? texture) =>
		SurfacePlayer == 2 && texture is not null && GodotObject.IsInstanceValid(texture) &&
		_byTexture.TryGetValue(texture.GetInstanceId(), out Proxy? proxy)
			? TwinOf(proxy)
			: texture;

	/// <summary>
	/// Turns a freshly cloned subtree (<see cref="Engine_UiTheme.OwnMaterials"/>) to player two's set:
	/// each quad's glyph proxy and keycap becomes player two's, and an atlas quad is flagged to follow
	/// <c>ui_keyboard_mode_two</c>. Each write goes into the mesh's own material
	/// (Engine_UiSceneBuilder.PrivateMaterial).
	/// </summary>
	public static void TakePlayerTwo(Node node)
	{
		if (node is MeshInstance3D { MaterialOverride: ShaderMaterial shader } mesh)
		{
			Proxy? proxy = shader.GetShaderParameter(DiffuseParameter).AsGodotObject() is Texture2D diffuse &&
				_byTexture.TryGetValue(diffuse.GetInstanceId(), out Proxy? found) ? found : null;
			string? button = shader.GetShaderParameter(KeycapParameter).AsGodotObject() is Texture2D keycap &&
				_keycapButtons.TryGetValue(keycap.GetInstanceId(), out string? named) ? named : null;
			if (proxy is not null || button is not null)
			{
				// Written only into this mesh's own material: never the pattern book's or another clone's.
				ShaderMaterial own = Engine_UiSceneBuilder.PrivateMaterial(mesh, shader);
				if (proxy is not null)
				{
					own.SetShaderParameter(DiffuseParameter, TwinOf(proxy));
				}

				if (button is not null)
				{
					own.SetShaderParameter(KeycapParameter, KeycapTwo(button));
					own.SetShaderParameter(PlayerTwoParameter, true);
				}
			}
		}

		foreach (Node child in node.GetChildren())
		{
			TakePlayerTwo(child);
		}
	}

	/// <summary>The quad shader's parameters <see cref="TakePlayerTwo"/> rewrites (ui_quad.gdshader).</summary>
	private const string DiffuseParameter = "diffuse_tex";

	private const string KeycapParameter = "keycap_tex";

	/// <summary>A quad of player two's: its keycap follows <c>ui_keyboard_mode_two</c> (ui_quad.gdshader).</summary>
	public const string PlayerTwoParameter = "glyph_player_two";

	/// <summary>
	/// Player two's twin of a shared proxy, drawn the first time it is asked for; the shared proxy
	/// itself while its source image is not to hand.
	/// </summary>
	private static ImageTexture TwinOf(Proxy proxy)
	{
		if (proxy.Two is { } known && GodotObject.IsInstanceValid(known))
		{
			return known;
		}

		if (DrawTwoImage(proxy) is not { } drawn)
		{
			return proxy.Texture;
		}

		proxy.Two = Engine_TextureUpload.CreateTexture(drawn);
		RegisterSourceImage(proxy.Two, drawn);
		return proxy.Two;
	}

	/// <summary>Player two's keycap for a button (a shared keycap's twin).</summary>
	private static ImageTexture KeycapTwo(string button)
	{
		if (!_keycapsTwo.TryGetValue(button, out ImageTexture? keycap))
		{
			keycap = Engine_TextureUpload.CreateTexture(DrawTwo(() => Keycap(button)));
			_keycapsTwo[button] = keycap;
		}

		return keycap;
	}

	/// <summary>A proxy's image as player two's set draws it, or null without its source.</summary>
	private static Image? DrawTwoImage(Proxy proxy)
	{
		Image? pad = SourceImageOf(proxy.Original);
		if (pad is null || pad.IsEmpty())
		{
			return null;
		}

		return DrawTwo(() => BuildProxyImage(
			pad, proxy.Button, proxy.Layer, proxy.Backed, proxy.Arrow, proxy.BaseWidth, proxy.BaseHeight, proxy.Scale, keys: false));
	}

	private static T DrawTwo<T>(Func<T> draw)
	{
		bool was = _drawingTwo;
		_drawingTwo = true;
		try
		{
			return draw();
		}
		finally
		{
			_drawingTwo = was;
		}
	}

	/// <summary>Redraws a twin in place; false when the proxy has none (or no source to draw from).</summary>
	private static bool RedrawTwin(Proxy proxy)
	{
		if (proxy.Two is not { } twin || !GodotObject.IsInstanceValid(twin) || DrawTwoImage(proxy) is not { } drawn)
		{
			return false;
		}

		RegisterSourceImage(twin, drawn);
		Engine_TextureUpload.SetImage(twin, drawn, allowD3D12Replace: false);
		return true;
	}

	/// <summary>
	/// Redraws player two's set when player two's device, the style, the pack or a remap changed. Once
	/// a frame from <see cref="Update"/>; cheap when nothing has. True when it redrew a texture (never
	/// in a game with no player-two surface).
	/// </summary>
	private static bool UpdateTwo()
	{
		var hash = new HashCode();
		hash.Add(PackName);
		hash.Add(Engine_UiGlyphPack.Generation);
		hash.Add(StyleTwo);
		hash.Add(PadFamilyTwo);
		hash.Add(Engine_PadRemap.Generation);
		int signature = hash.ToHashCode();
		if (_drawnOnceTwo && signature == _drawnSignatureTwo)
		{
			return false;
		}

		_drawnOnceTwo = true;
		_drawnSignatureTwo = signature;
		RenderingServer.GlobalShaderParameterSet("ui_keyboard_mode_two", !DrawTwo(() => AtlasShowsShipped(false)));
		bool redrawn = false;
		foreach (Proxy proxy in _proxies.Values)
		{
			redrawn |= RedrawTwin(proxy);
		}

		foreach ((string button, ImageTexture keycap) in _keycapsTwo)
		{
			Engine_TextureUpload.SetImage(keycap, DrawTwo(() => Keycap(button)), allowD3D12Replace: false);
			redrawn = true;
		}

		return redrawn;
	}
}
