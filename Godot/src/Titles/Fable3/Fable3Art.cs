namespace Refii.Titles.Fable3;

using Godot;

/// <summary>
/// Fable 3's GUI art by the path the guest names, through the one `game://` loader.
/// </summary>
/// <remarks>
/// WHERE IT LIVES. The guest mounts its GUI art as a header bank and a body bank,
/// `gui/gui_texture_headers.bnk` and `gui/gui_textures.bnk` (strings 0x82078F38 / 0x82078F04,
/// mounted in `sub_822E7C60` / `sub_822E82C0`), and names each texture by its art path
/// (`art\gui\hudassests\version4\buttons\start\24x24\start_complete`, 0x82080A74). The shared
/// mount joins the two parts under `art/gui/…` exactly as named: the splash, its logo and shadow,
/// the START icon and `qc010_opening.tex` all read as one entry in `art/gui/gui_textures.bnk`
/// with a 92-byte header in `art/gui/gui_texture_headers.bnk`, and decode (BC1/BC3, header v4).
///
/// THE LOADER MUST BE REGISTERED AT THE FRONT before the first `game://` load. Fable 3's screens
/// asked ResourceLoader directly, ahead of anything that calls
/// <see cref="Refii.Textures.Engine_BnkTextureLoader.EnsureRegistered"/>, so Godot's binary loader
/// ran first on every path and logged `Cannot open file 'game://…'` before the bank loader
/// answered it — the art arrived, the log read as missing art.
/// </remarks>
public static class Fable3Art
{
	/// <summary>The texture at a bank path (forward or back slashes, any case), or null when the mount does not hold it.</summary>
	public static Texture2D? Load(string path)
	{
		string virtualPath = path.Replace('\\', '/').ToLowerInvariant();
		if (!(Refii.Assets.Engine_GameAssets.Instance is { IsMounted: true } assets && assets.TryGetFile(virtualPath, out _)))
		{
			return null;
		}

		Refii.Textures.Engine_BnkTextureLoader.EnsureRegistered();
		Texture2D? texture = ResourceLoader.Load<Texture2D>(Refii.Textures.Engine_BnkTextureLoader.PathPrefix + virtualPath);

		// A button's art becomes the proxy the glyph swap redraws (Fable3Glyphs), as the shared cache's does;
		// a player-two surface takes player two's twin.
		return texture is null ? null : Refii.Ui.Engine_UiKeyGlyphs.ForSurface(Refii.Ui.Engine_UiKeyGlyphs.Wrap(virtualPath, texture));
	}
}
