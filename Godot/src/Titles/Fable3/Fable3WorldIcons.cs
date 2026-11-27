namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;
using Refii.Game;
using Refii.Host;
using Refii.Text;
using Refii.Ui;

/// <summary>
/// Fable 3's world icons — opinion splashes, quest and reward markers, NPC status, property
/// signs, button prompts, the emotion choices — drawn by the shared
/// <see cref="Engine_HudWorldSprites"/> from the icons the guest STATES
/// (Fable3/src/hooks/src/world_icon_hooks.cpp into the runtime's stated table;
/// Fable3/src/hostbridge/src/icon_feed.cpp: RefiiHostGetWorldSprites), and the emotion ring drawn
/// over them from the stated choice records (RefiiHostGetFable3EmotionChoices).
/// </summary>
/// <remarks>
/// THE GUEST OWNS EVERY LIFETIME. An icon is stated by its own Init, each Update the registry's
/// tick runs on it (whose `visible` argument decides whether it is drawn this frame) and the
/// emotion icon's ring tick; it leaves when the registry erases it or it is destroyed. Nothing
/// here adds, times or removes an icon. What is ours is the drawing: the anchor is projected
/// through Godot's world camera into a 720-high canvas the sprites layer lives in, the art is the
/// texture the guest names by type, a button prompt (a pooled HUD-element icon with no texture of
/// its own) shows its button glyph.
///
/// THE EMOTION RING is the guest's too: the icon's progress (0..300 steps) fills while the
/// choice is held and completes at 300, both written by its ring tick (sub_8297A400); the ring
/// here is that number as an arc at the icon.
///
/// SPLIT, EACH HALF IS A SCREEN OF ITS OWN (Engine_HudSpriteView, as Fable 2's layers): player one's
/// sprite layer is seated in player one's half and placed through player one's view camera, player
/// two's layer in player two's through theirs, and a sprite is drawn in each half whose view shows its
/// anchor — the guest states no player for an icon (its +103 is the game's vt+0x18, `li r3,0`: TU3
/// 0x82616230 / disc 0x825EEEC0 against 0x8222FABC / 0x82230C54), so the one shared screen showed every
/// icon to both players. An emotion choice whose record states its owner (rec+8, a hero's uid) draws
/// only in its owner's half (Engine_HudSpriteView.DrawnIn). Player two's layer resolves its art in
/// player two's glyph set (Engine_UiKeyGlyphs.Surface(2)). One screen (solo, shared couch, online):
/// the window through its camera, exactly as before.
/// </remarks>
public partial class Fable3WorldIcons : SubViewportContainer
{
	private const float CanvasHeight = Engine_UiSceneBuilder.CanvasHeight;
	private const float PixelsPerUnit = Engine_UiSceneBuilder.PixelsPerUnit;

	/// <summary>
	/// The guest's ring radius, in its 720-line pixels: sub_82979188 builds the 301-point path
	/// `(sin a * aspect * 14, -cos a * 14)` for a = i/300 * 2pi (0x8297935C / 0x82979374), the
	/// aspect `(w/h) / (16:9 or 4:3)` (0x82979320) keeping it round on the display, so a circle.
	/// </summary>
	private const float RingRadius = 14f;

	/// <summary>The guest's icon sizes, in its 720-line pixels: 64 for the opinion, status, quest,
	/// reward, dog and property kinds, 32 for the rest (sub_82B51678), against the shared
	/// drawer's 50-unit square.</summary>
	private static float SizeFor(uint type) =>
		(type is >= 1u and <= 31u || type is >= 36u and <= 51u || type == 63u || type == 65u || type is >= 73u and <= 75u ? 64f : 32f) /
		Engine_HudWorldSprites.IconSize;

	/// <summary>The emotion icon's registry type (sub_825E8938 registers it as 77, 0x825E89C0).</summary>
	private const uint EmotionIconType = 77u;

	/// <summary>A target's "Interact" icon: pooled type 34 (`li r7,0x22` 0x8217907C / disc 0x82179184).</summary>
	private const uint InteractIconType = 34u;

	private SubViewport _viewport = null!;
	private Camera3D _canvasCamera = null!;
	private Engine_HudWorldSprites _sprites = null!;

	/// <summary>Player two's half's sprites on split screen, through player two's camera.</summary>
	private Engine_HudWorldSprites _spritesTwo = null!;

	/// <summary>[0] the whole window's (or player one's half while split), [1] player two's half while split.</summary>
	private readonly Engine_HudSpriteView[] _views = { new(), new() };

	/// <summary>The window through its camera, on every layout: the aim reticules' placement.</summary>
	private readonly Engine_HudSpriteView _windowView = new();

	/// <summary>Whether the sprite layers carry seat transforms a whole window must undo.</summary>
	private bool _seated;

	/// <summary>Whether player two's layer holds this split's sprites.</summary>
	private bool _twoFed;
	private readonly Engine_HudReticule[] _reticules = new Engine_HudReticule[2];
	private Vector2 _fittedTo;
	private bool _placed;
	private bool _probed;
	private string _culture = string.Empty;
	private readonly List<Engine_HudWorldSprites.Sprite> _list = new();
	private readonly List<Engine_HudWorldSprites.Sprite> _listTwo = new();
	/// <summary>The mount path for each texture id the guest can hand an icon: the type table's at
	/// start, the emotion records' as their choices appear.</summary>
	private readonly Dictionary<uint, string> _pathsByHash = new();


	/// <summary>The ring of every held choice this frame in each half that draws it, in window pixels: centre,
	/// radius, progress, complete, stroke.</summary>
	private readonly List<(Vector2 Centre, float Radius, float Progress, bool Complete, float Stroke)> _rings = new();

	/// <summary>This frame's emotion icons by the guest's object: whether the drawer stacked a title under the square, and its scale.</summary>
	private readonly Dictionary<uint, (bool Titled, float Scale)> _emotionSquares = new();

	/// <summary>
	/// When the icons' viewport renders: this container is never hidden with it — its own <c>_Draw</c>
	/// holds the partner markers and the hold rings — so the frame after the last icon went must
	/// still render (<see cref="Engine_UiViewportIdle"/>).
	/// </summary>
	private readonly Engine_UiViewportIdle _idle = new();

	public override void _Ready()
	{
		// Anchors AND offsets, as Fable3Canvas: anchors alone leave the container 0×0 under the
		// CanvasLayer and its stretched viewport 2×2 — nothing rendered.
		SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		MouseFilter = MouseFilterEnum.Ignore;
		Stretch = true;

		_viewport = Engine_UiSceneBuilder.CreateCanvasViewport("Canvas", SubViewport.UpdateMode.Disabled);
		AddChild(_viewport);

		var root = new Node3D { Name = "Root" };
		_viewport.AddChild(root);

		_canvasCamera = Engine_UiSceneBuilder.CreateCanvasCamera("CanvasCamera", near: 0.1f, far: 500f, z: 100f);
		root.AddChild(_canvasCamera);

		_sprites = new Engine_HudWorldSprites
		{
			Name = "WorldSprites",
			ResolveTexture = ResolveTexture,
			Place = (guest, _) => PlaceIn(_views[0], guest),
		};
		root.AddChild(_sprites);

		_spritesTwo = new Engine_HudWorldSprites
		{
			Name = "WorldSpritesPlayerTwo",
			ResolveTexture = ResolveTextureTwo,
			Place = (guest, _) => PlaceIn(_views[1], guest),
			SightView = 1,
			Visible = false,
			ProcessMode = ProcessModeEnum.Disabled,
		};
		root.AddChild(_spritesTwo);

		// The aim reticules (Fable3Reticles), in front of the icons: pushed by the guest as sprites, never elements.
		for (int slot = 0; slot < 2; ++slot)
		{
			_reticules[slot] = new Engine_HudReticule
			{
				Name = slot == 0 ? "SpellReticule" : "RangedReticule",
				Look = Fable3Reticles.Look,
				Position = new Vector3(0f, 0f, 1f),
				ResolveTexture = ResolveTexture,
				Place = guest => PlaceIn(_windowView, guest)?.Canvas,
			};
			root.AddChild(_reticules[slot]);
		}

		foreach ((uint type, string path) in Fable3IconArt.Paths)
		{
			// The guest's texture id is FNV-1 of `art\<path>.tex` LOWER-CASED: sub_82B512C0 (0x82B51304)
			// hands sub_82B530E8's string to sub_8312F3D8, whose string wrapper sub_822D76E0 copies it
			// through sub_82E34768 before sub_822D78B0 hashes the bytes — the live id C51DF0BA of a
			// type-72 icon is FNV-1 of `art\gui_memmount\hudassests\…\guildseal_gold.tex`, not of
			// the mixed-case literal (run r1, 2026-11-01).
			_pathsByHash[Refii.Core.Engine_GameHash.Fnv1(("art\\" + path + ".tex").ToLowerInvariant())] = Fable3IconArt.BankPath(path);
		}
	}

	/// <summary>
	/// A texture by the guest's id: the type table's path, else the mount's own hash index — the
	/// banks name every entry, and the guest's id IS the bank key (FNV-1 of the lower-cased path,
	/// sub_8312F3D8). THE EMOTION ICONS (type 77) come this way: sub_82978558 hands the choice's
	/// RecordName to sub_82978D90, which reads the record's `Texture` field (FNV-1("Texture") =
	/// `dword_8355C6EC`, init 0x831dea80..b8; inherited — `EmotionIconClothingStatue` is an alias
	/// of #d69b1457 whose parent #508b355c holds it) as that hash (0x82979038), and sub_8297A130
	/// stores it at icon+144 (0x8297a148): 342be71b =
	/// `art\gui_memmount\hudassests\version4\buttons\a\24x24_norim\a_complete.tex`. A miss
	/// names what was tried, once per hash.
	/// </summary>
	private Texture2D? ResolveTexture(uint hash)
	{
		if (!_pathsByHash.TryGetValue(hash, out string? path))
		{
			path = Refii.Bnk.AssetHashIndex.PathForHash(hash);
			if (path is null)
			{
				GD.Print($"[world-sprites] texture {hash:X8}: no type-table path and no mount entry hashes to it");
				return null;
			}

			GD.Print($"[world-sprites] texture {hash:X8} is the mount's '{path}'");
		}

		Texture2D? texture = Fable3Art.Load(path);
		if (texture is null)
		{
			GD.Print($"[world-sprites] texture {hash:X8}: the mount has no '{path}'");
		}

		return texture;
	}

	/// <summary><see cref="ResolveTexture"/> in player two's glyph set: a button's art is player two's twin.</summary>
	private Texture2D? ResolveTextureTwo(uint hash)
	{
		using (Engine_UiKeyGlyphs.Surface(2))
		{
			return ResolveTexture(hash);
		}
	}

	public override void _Process(double delta)
	{
		// The container covers the window: its parent is a CanvasLayer, not a Control, so nothing
		// else resizes it when the window does.
		Vector2 cover = GetViewportRect().Size;
		if (Size != cover && cover.X > 0f && cover.Y > 0f)
		{
			Position = Vector2.Zero;
			Size = cover;
		}

		Vector2 size = GetViewportRect().Size;
		if (size != _fittedTo && size.X > 0f && size.Y > 0f)
		{
			_fittedTo = size;
			Engine_UiSceneBuilder.FitCanvasCamera(_canvasCamera, size);
		}

		if (_sprites.Theme is null || _culture != Engine_GameCulture.Current)
		{
			_culture = Engine_GameCulture.Current;
			_sprites.Theme = new Engine_UiTheme
			{
				Fonts = Fable3Fonts.For(_culture),
				Strings = Fable3Strings.Current,
				Textures = Fable3Art.Load,
			};
			_spritesTwo.Theme = new Engine_UiTheme
			{
				Fonts = _sprites.Theme.Fonts,
				Strings = _sprites.Theme.Strings,
				Textures = LoadArtTwo,
			};
		}

		// THE SCREENS THIS FRAME: the window through its camera, or each half through its own player's
		// camera (Engine_HudSpriteView). Each view's camera is taken here, once: the rings below and both
		// layers' placements later this frame read the same snapshot, as the one projector did.
		Camera3D? camera = GetWindow().GetCamera3D();
		Vector2 window = GetViewportRect().Size;
		bool split = Engine_HudSpriteView.Aim(_views[0], _views[1], camera, window, testSight: false);
		SeatLayers(split);
		AimWindow(_windowView);
		bool hasProjector = _views[0].Update();
		bool twoProjects = split && _views[1].Update();
		_windowView.Update();

		// THE HUD ROOT'S STATE IS THE GUEST'S GATE: the icons are HUD elements, and hud.bgf's root is
		// set to state 2 by sub_825C1CB0 from every box's Show — the Options screen among them — and
		// back to 1 by sub_825C1D28 (`@Hud.Root`, Fable3Elements.HudHidden), so nothing of the HUD is
		// drawn then.
		_list.Clear();
		_listTwo.Clear();
		_markerSprites.Clear();
		_emotionSquares.Clear();
		IReadOnlyList<EmotionChoice> choices = GetEmotionChoices();
		(ulong One, ulong Two) heroes = (0ul, 0ul);
		bool heroesRead = false;
		foreach (HostBridge.WorldSprite sprite in HostBridge.GetWorldSprites())
		{
			if (Fable3Elements.HudHidden)
			{
				break;
			}

			// The co-op partners' markers (52/53) are placed and drawn here from the stated icon
			// alone (PlaceMarker, _Draw), never from their hud.bgf element.
			if (sprite.Type is MarkerHero or MarkerHenchman)
			{
				_markerSprites.Add(sprite);
				continue;
			}

			// A pooled prompt (the interaction mode's A/B/X/Y world icons) is a button: drawn as the
			// shared glyph for it — the pad's in the player's style, the bound key's keycap on the
			// keyboard — as every other prompt is (Engine_UiKeyGlyphs), not as a lettered badge.
			string button = sprite.Pooled ? Fable3IconArt.ButtonFor(sprite.Type) : string.Empty;
			Texture2D? glyph = button.Length > 0 ? Engine_UiKeyGlyphs.KeycapFor(button) : null;
			bool inOne = true;
			bool inTwo = split;
			if (sprite.Type == EmotionIconType)
			{
				_emotionSquares[sprite.Object] = (sprite.Title.Length > 0, sprite.Scale * SizeFor(sprite.Type));
				if (!heroesRead)
				{
					heroesRead = true;
					heroes = LocalHeroes();
				}

				EmotionChoice? choice = ChoiceOf(sprite.Object, choices);
				if (split)
				{
					ulong owner = choice?.Player ?? 0ul;
					inOne = Engine_HudSpriteView.DrawnIn(0, owner, heroes.One, heroes.Two);
					inTwo = Engine_HudSpriteView.DrawnIn(1, owner, heroes.One, heroes.Two);
					ReportRoute(choice, inOne, inTwo);
				}
				else
				{
					glyph = ChoiceArt(sprite, choice, heroes.Two) ?? glyph;
				}
			}
			else if (split && sprite.Pooled && sprite.Type == InteractIconType &&
				Fable3Targeting.InteractBy(sprite.Entity) is int targeting and not 0)
			{
				// A target's "Interact" icon is one per target, raised for whichever player targets it
				// (Fable3Targeting): each half draws it only for its own player's target.
				inOne = Refii.Entities.Engine_EntityHighlight.DrawnInView(0, split, targeting);
				inTwo = Refii.Entities.Engine_EntityHighlight.DrawnInView(1, split, targeting);
			}

			if (inOne)
			{
				_list.Add(new Engine_HudWorldSprites.Sprite(
					sprite.Type, sprite.Pooled ? 0u : sprite.TextureHash, sprite.Colour, sprite.Entity, sprite.Anchor,
					sprite.Scale * SizeFor(sprite.Type), sprite.Title, sprite.Opinion, sprite.Object, Texture: glyph));
			}

			// Player two's half: their own keycap for a prompt; any other art by hash through ResolveTextureTwo.
			if (inTwo)
			{
				Texture2D? glyphTwo = null;
				if (button.Length > 0)
				{
					using (Engine_UiKeyGlyphs.Surface(2))
					{
						glyphTwo = Engine_UiKeyGlyphs.KeycapFor(button);
					}
				}

				_listTwo.Add(new Engine_HudWorldSprites.Sprite(
					sprite.Type, sprite.Pooled ? 0u : sprite.TextureHash, sprite.Colour, sprite.Entity, sprite.Anchor,
					sprite.Scale * SizeFor(sprite.Type), sprite.Title, sprite.Opinion, sprite.Object, Texture: glyphTwo));
			}
		}

		// THE MODS' MARKERS (mark / unmark on every surface) through the one composition after the game's
		// icons, on the same layer and projector. One screen: a couch pair shares it (both players'
		// markers), an online machine shows its own player's. Split: each half its own player's, sized by
		// that half's camera, as Fable 2's halves.
		if (!Fable3Elements.HudHidden && Engine_HudModMarkers.Count != 0)
		{
			Engine_HudModMarkers.LoadIcon ??= name => Engine_HudModMarkers.LoadIconWith(name, Fable3Art.Load);
			double clock = Godot.Time.GetTicksMsec() / 1000.0;
			if (split)
			{
				Engine_HudModMarkers.ComposeMarkers(_list, 1, false,
					_markerDistanceOne ??= guest => ViewDistanceSquared(_views[0], guest), clock);
				Engine_HudModMarkers.ComposeMarkers(_listTwo, 2, false,
					_markerDistanceTwo ??= guest => ViewDistanceSquared(_views[1], guest), clock);
			}
			else
			{
				uint owned = HostBridge.PlayerOwned();
				Engine_HudModMarkers.ComposeMarkers(_list, owned == 2u ? 2 : 1, owned == 3u,
					_markerDistance ??= MarkerDistanceSquared, clock);
			}
		}

		_sprites.Feed(_list);

		// Player two's layer is fed only while it is drawn, and emptied once when the split ends.
		if (split)
		{
			_spritesTwo.Feed(_listTwo);
			_twoFed = true;
		}
		else if (_twoFed)
		{
			_spritesTwo.Feed(System.Array.Empty<Engine_HudWorldSprites.Sprite>());
			_twoFed = false;
		}

		bool reticuleActive = false;
		float canvasHeight = CanvasHeight / PixelsPerUnit;
		for (int slot = 0; slot < 2; ++slot)
		{
			Engine_HudReticule reticule = _reticules[slot];
			reticule.CanvasHalfExtent = size.Y > 0f ? new Vector2(size.X / size.Y * canvasHeight, canvasHeight) * 0.5f : Vector2.Zero;
			for (int player = 0; player < 2; ++player)
			{
				float guestHeight = 720f;
				Engine_HudReticule.Aim? aim = Fable3Elements.HudHidden ? null : Fable3Reticles.Read(player, slot, out guestHeight);
				if (aim is not null)
				{
					reticuleActive = true;
				}
				reticule.UnitsPerGuestPixel = canvasHeight / guestHeight;
				reticule.Feed(player, aim);
			}
		}

		// The list and the aims are already empty under a hidden HUD root (above), so the root is no
		// term here: the viewport must still render that emptiness once.
		Engine_UiSceneBuilder.SetViewportActive(
			_viewport, _idle.Renders(Refii.Game.GameWorld.CoverHeld, _list.Count != 0 || _listTwo.Count != 0 || reticuleActive));

		// Ground truth once a sprite was placed on screen (last frame's placement): the log's
		// `1 on screen` is the drawer's count, not pixels (r8 shows none of them).
		if (_placed && !_probed)
		{
			_probed = true;
			Fable3CanvasProbe.Report("world-sprites", this, _viewport, _canvasCamera, _sprites);
		}

		_placed = false;

		// THE RING IS CONCENTRIC WITH THE ICON'S SQUARE. The guest centres its path on the centre of
		// the icon's own quad (sub_8297A400: the bounds sub_82B509F8 at 0x8297A448, halved at
		// 0x8297A480..0x8297A4B4, projected by sub_82287A08 at 0x8297A4C4) and adds each point in
		// screen pixels before unprojecting it (sub_82269260 at 0x8297A7B8). The shared drawer stacks
		// the square above the anchor (title on the anchor), so the centre is lifted by the title
		// and half the square, exactly as it places them. Split: in each half that draws the icon,
		// through that half's camera, in that half's units (its seat scale).
		_rings.Clear();
		ChoicesUp = choices.Count > 0;
		PlaceMarkers(window, split);
		if (hasProjector && !Fable3Elements.HudHidden && window.Y > 0f)
		{
			float pixelsPerUnit = window.Y * PixelsPerUnit / CanvasHeight;
			float piece = _sprites.PieceScale;
			foreach (EmotionChoice choice in choices)
			{
				if (!choice.RingOn || !_emotionSquares.TryGetValue(choice.Icon, out (bool Titled, float Scale) square))
				{
					continue;
				}

				Vector3 world = GameWorld.GuestToGodot(choice.Anchor);
				float lift = ((square.Titled ? Engine_HudWorldSprites.TextSize : 0f) +
					(Engine_HudWorldSprites.IconSize * square.Scale * 0.5f)) * piece;
				for (int half = 0; half < (split ? 2 : 1); ++half)
				{
					Engine_HudSpriteView view = _views[half];
					if (split && ((half == 1 && !twoProjects) ||
						!Engine_HudSpriteView.DrawnIn(half, choice.Player, heroes.One, heroes.Two)))
					{
						continue;
					}

					if (!view.Projector.TryProject(world, out Vector2 pixel, out _) || (split && !view.Shows(pixel)))
					{
						continue;
					}

					float unit = pixelsPerUnit * view.Frame.Scale;
					Vector2 centre = view.WindowPixel(pixel) - new Vector2(0f, lift * unit);
					_rings.Add((centre, RingRadius * piece * unit, choice.Progress, choice.Complete,
						3f * (window.Y / CanvasHeight) * view.Frame.Scale));
				}
			}
		}

		QueueRedraw();
	}

	/// <summary>Seats each layer in its view's half while split; back on the whole window once the split ends.</summary>
	private void SeatLayers(bool split)
	{
		if (split || _seated)
		{
			_seated = split;
			_sprites.Transform = _views[0].Frame.Transform(0f);
			_spritesTwo.Transform = _views[1].Frame.Transform(0f);
		}

		if (_spritesTwo.Visible != split)
		{
			_spritesTwo.Visible = split;
			_spritesTwo.ProcessMode = split ? ProcessModeEnum.Inherit : ProcessModeEnum.Disabled;
		}
	}

	/// <summary>A view on the whole window through the window's camera as they stand when asked.</summary>
	private void AimWindow(Engine_HudSpriteView view)
	{
		Vector2 window = GetViewportRect().Size;
		view.Camera = GetWindow().GetCamera3D();
		view.Viewport = window;
		view.Frame = Engine_UiSeats.Of(window, Engine_UiSeats.Seat.Whole);
		view.TestSight = false;
	}

	private System.Func<Vector3, float?>? _markerDistance;
	private System.Func<Vector3, float?>? _markerDistanceOne;
	private System.Func<Vector3, float?>? _markerDistanceTwo;

	/// <summary>The window camera's squared distance to a guest point, for the markers' size curve.</summary>
	private float? MarkerDistanceSquared(Vector3 guest) =>
		GetWindow().GetCamera3D() is { } camera && GodotObject.IsInstanceValid(camera)
			? camera.GlobalPosition.DistanceSquaredTo(GameWorld.GuestToGodot(guest))
			: null;

	/// <summary>A half's camera's squared distance to a guest point; null with no camera.</summary>
	private static float? ViewDistanceSquared(Engine_HudSpriteView view, Vector3 guest) =>
		view.Camera is { } camera && GodotObject.IsInstanceValid(camera)
			? camera.GlobalPosition.DistanceSquaredTo(GameWorld.GuestToGodot(guest))
			: null;

	/// <summary><see cref="Fable3Art.Load"/> in player two's glyph set, for player two's layer's text icons.</summary>
	private static Texture2D? LoadArtTwo(string path)
	{
		using (Engine_UiKeyGlyphs.Surface(2))
		{
			return Fable3Art.Load(path);
		}
	}

	/// <summary>This machine's two local heroes' uids (entity manager +0x80 / +0x88, entity+0x18), 0 without local co-op.</summary>
	private static (ulong One, ulong Two) LocalHeroes()
	{
		if (!Refii.Core.Engine_TitleGame.Active.LocalCoop)
		{
			return (0ul, 0ul);
		}

		(uint one, uint two) = HostBridge.CoopPlayerEntities();
		return (HostBridge.GetEntityUid(one), HostBridge.GetEntityUid(two));
	}

	/// <summary>The stated choice record an emotion icon is drawn for, by the icon object (choice.Icon); null for none.</summary>
	private static EmotionChoice? ChoiceOf(uint icon, IReadOnlyList<EmotionChoice> choices)
	{
		if (icon == 0u)
		{
			return null;
		}

		foreach (EmotionChoice choice in choices)
		{
			if (choice.Icon == icon)
			{
				return choice;
			}
		}

		return null;
	}

	/// <summary>The choice records already reported, by icon: whose half they are drawn in on split.</summary>
	private readonly HashSet<uint> _reportedRoute = new();

	/// <summary>Logs once per icon the halves a stated owner keeps a choice to on split.</summary>
	private void ReportRoute(EmotionChoice? choice, bool inOne, bool inTwo)
	{
		if (choice is not { } stated || stated.Player == 0ul || _reportedRoute.Count >= 64 || !_reportedRoute.Add(stated.Icon))
		{
			return;
		}

		GD.Print($"[world-sprites] choice '{stated.Name}' (owner {stated.Player:X16}) on split: " +
			(inOne && inTwo ? "no local hero's, drawn in both halves" : inTwo ? "player two's half only" : "player one's half only"));
	}

	/// <summary>Player two's copy of each choice art by the guest's texture id (<see cref="ChoiceArt"/>).</summary>
	private readonly Dictionary<uint, Texture2D?> _artTwo = new();

	/// <summary>The choice records already reported as player two's, by record.</summary>
	private readonly HashSet<uint> _reportedTwo = new();

	/// <summary>
	/// An emotion choice's button art in the glyph set of the player the choice is for, on one screen. THE
	/// RECORD STATES ITS OWNER: the hero uid at rec+8 (64 bits, high word at +8; 0 = any player), which the
	/// choices' input matches word for word against the pressing player's hero, `ld r11,0x18(hero)` (TU3
	/// 0x8261043C / disc 0x825E9228) against `ld r11,8(rec)` (TU3 0x82610508..0x82610520 / disc
	/// 0x825E92F4..0x825E930C; a null owner passes, TU3 0x826104D4..0x826104F4 / disc 0x825E92C0..0x825E92E0).
	/// A choice whose owner is this machine's local player two (Engine_ControllerFamily.OwnerSet) draws player
	/// two's twin of its art; null keeps the shared texture the drawer resolves by hash. Split, each half's
	/// layer resolves its own set (ResolveTextureTwo).
	/// </summary>
	private Texture2D? ChoiceArt(HostBridge.WorldSprite sprite, EmotionChoice? choice, ulong localTwo)
	{
		if (localTwo == 0ul || sprite.TextureHash == 0u || choice is not { } stated ||
			Refii.Core.Engine_ControllerFamily.OwnerSet(stated.Player, localTwo) != 2)
		{
			return null;
		}

		if (!_artTwo.TryGetValue(sprite.TextureHash, out Texture2D? art))
		{
			art = ResolveTextureTwo(sprite.TextureHash);
			_artTwo[sprite.TextureHash] = art;
		}

		if (_reportedTwo.Add(sprite.Object))
		{
			GD.Print($"[world-sprites] choice '{stated.Name}' is player two's (owner {stated.Player:X16}): " +
				$"player two's glyphs{(art is null ? ", art NOT resolved" : string.Empty)}");
		}

		return art;
	}

	/// <summary>
	/// A guest world anchor on one layer's canvas: through the view's camera to a pixel, then into that
	/// view's seat canvas units (Engine_HudSpriteView.Place; a half drops an anchor outside its view). The
	/// whole window is taken as it stands when asked. No line-of-sight depth is captured for this title,
	/// so the marker is left untested (sight.x &lt; 0).
	/// </summary>
	private Engine_HudWorldSprites.Placed? PlaceIn(Engine_HudSpriteView view, Vector3 guest)
	{
		if (view.Frame.IsWhole)
		{
			AimWindow(view);
		}

		Engine_HudWorldSprites.Placed? placed = view.Place(GameWorld.GuestToGodot(guest), clip: true);
		if (placed is not null)
		{
			_placed = true;
		}

		return placed;
	}

	// ---- The co-op partners' markers, types 52/53 ---------------------------------------------
	//
	// THE STATED ICON IS THE MARKER. sub_82B52440 builds a HUD-element icon (vtable 0x820D5EBC,
	// 0x82B52534) on the record 0x832A1D70 for type 52 and 0x832A1DB0 for 53 (icon+144, 0x82B52520),
	// whose elements sub_82B522A0 resolves from `HeroOffScreenWorldIcon` / `HenchmanOffScreenWorldIcon`
	// (0x82B523E0 / 0x82B52420). Its type setter (`vt+8` sub_82B520D0) gives the icon model the
	// type's art from sub_82B530E8 (SetTexture at 0x82B5214C): `icon_player1` / `icon_player2`
	// (Fable3IconArt). Its Update sub_82B51B58 shows the element exactly when its `visible`
	// argument is set (`vt+24` sub_82B52818 at 0x82B51F58), which is when the feed exports the icon,
	// and places it from the icon's world point (`vt+48`, stated as +112):
	//   - projected through the game camera (sub_82287A08 at 0x82B51BF0); OFF SCREEN when the depth
	//     is negative (0x82B51C1C) or the pixel is outside the camera's viewport rect
	//     (+0x8C..+0x98, 0x82B51C24..0x82B51C58);
	//   - BEHIND the camera the point is pushed along the view's forward axis (the view quaternion's
	//     +Y, sub_82195450 at 0x82B51CE8) by max(2 * the view's +0x30, 2 * |its distance from the
	//     camera plane|) (0x82B51D78..0x82B51DB0), projected again and its y set to the viewport's
	//     bottom (+0x48, 0x82B51DC8..0x82B51DD0);
	//   - ON SCREEN the point is first lifted by the icon's world offset +160 (`vaddfp` 0x82B51E94).
	//     The constructor zeroes it and sets +176 (0x82B52540 / 0x82B52548); every Update clears
	//     +176 (0x82B51FCC). The offset is MEASURED ONCE, on an on-screen Update after the first,
	//     while its z is still 0 (0x82B51E34..0x82B51E48): sub_82B529E8 takes the element's bounds
	//     where it was last placed (sub_8281AA20: left, top, right, bottom, y up; the base Update
	//     dirties them every frame, +72 = -1 at 0x8222F55C), maps the corners (left, top) and
	//     (right, bottom) through the HUD camera to pixels and back into the world at the
	//     point's own depth (sub_82C403E0 = project, project, unproject sub_82269260), and the
	//     offset's z is the point's z minus the bottom corner's (`fsubs` 0x82B51E68, stored into
	//     lane z by unk_82065370 at 0x82B51E7C). Placed at the point the frame before, the icon's
	//     bottom edge comes to stand on the point, at the depth it was measured;
	//   - every placement of 52/53 then goes through the edge clamp sub_82B519C8 (0x82B51DDC,
	//     0x82B51EEC): x into [x0 + 10% + 10, x1 - 9% - 10], y into [y0 + 10% + 40, y1 - 9%] of the
	//     viewport rect, in its 720-line pixels (0x82B51A14..0x82B51AF4).
	// Nothing in the guest rotates the element: the Update writes its position alone (0x82B51FC8),
	// and the `Contents.Arrow` it resolves (record+20, 0x82B526C8) is read by no code; hud.bgf
	// makes it active only on the `Clamped` slide (6), which nothing plays. The slides the guest
	// plays are Init's first-show state (1 `Fade In` for these types: sub_82B52068 at 0x82B520AC,
	// played at 0x82B51F28), the death's 2 `Fade Out` (sub_82B51FE0 at 0x82B5204C, with one second
	// left at 0x82B52020) and 7 `Hidden` (0x82B52940).

	/// <summary>The `HeroOffScreenWorldIcon` marker: art `icon_player1`, record 0x832A1D70.</summary>
	private const uint MarkerHero = 52u;

	/// <summary>The `HenchmanOffScreenWorldIcon` marker: art `icon_player2`, record 0x832A1DB0.</summary>
	private const uint MarkerHenchman = 53u;

	/// <summary>`Fade In` and `Fade Out` run the icon's opacity 0..100 and back over 1000 ms (hud.bgf anims 44 / 45).</summary>
	private const float MarkerFadeSeconds = 1f;

	/// <summary>The two markers' authored fades, read from hud.bgf once it stands (<see cref="UseHud"/>).</summary>
	private static Fable3MarkerPlacement.Fades? _heroFades;
	private static Fable3MarkerPlacement.Fades? _henchmanFades;

	/// <summary>Takes the markers' fade tracks from the HUD's own document (Fable3Hud, when hud.bgf stands).</summary>
	internal static void UseHud(Engine_UiScreen screen)
	{
		_heroFades = Fable3MarkerPlacement.FadesOf(screen, "HeroOffScreenWorldIcon");
		_henchmanFades = Fable3MarkerPlacement.FadesOf(screen, "HenchmanOffScreenWorldIcon");
		GD.Print($"[fable3-markers] fade tracks: hero {(_heroFades is null ? "absent" : "read")}, " +
			$"henchman {(_henchmanFades is null ? "absent" : "read")}");
	}

	private readonly List<HostBridge.WorldSprite> _markerSprites = new();

	/// <summary>This frame's markers in window pixels: art, the icon's centre, its side, its opacity.</summary>
	private readonly List<(Texture2D Art, Vector2 Centre, float Side, float Alpha)> _markers = new();

	/// <summary>Each marker icon's stated age (+68) when it was first drawn: `Fade In` runs from there.</summary>
	private readonly Dictionary<uint, float> _markerShownAt = new();

	/// <summary>Each marker icon's root as last placed on each screen (<see cref="MarkerKey"/>), in that screen
	/// camera's pixels: the guest's previous SetPosition (0x82B51FC8).</summary>
	private readonly Dictionary<ulong, Vector2> _markerPlaced = new();

	/// <summary>Each marker icon's measured world lift on each screen (+160's z, Godot's up), once measured.</summary>
	private readonly Dictionary<ulong, float> _markerLift = new();

	private readonly Dictionary<uint, Texture2D?> _markerArt = new();
	private readonly HashSet<ulong> _markersReported = new();

	/// <summary>A marker on one screen: the icon object, and 0 for the whole window or player one's half, 1 for
	/// player two's.</summary>
	private static ulong MarkerKey(uint icon, int screen) => ((ulong)(uint)screen << 32) | icon;

	/// <summary>A screen the markers are placed on: its camera, that camera's viewport in pixels, and where it
	/// stands on the window (top-left and size, window pixels).</summary>
	private readonly List<(int Index, Camera3D Camera, Vector2 View, Vector2 Origin, Vector2 Size)> _markerScreens = new();

	/// <summary>
	/// The screens the markers stand on: the sprites' views (Engine_HudSpriteView.Aim). One screen: the window,
	/// through its camera. SPLIT: each half is a screen of its own (Engine_UiSeats), seen through its own player's
	/// camera, and shows the other player's hero's marker (Engine_HudSpriteView.PartnerMarkerHalf: the hero
	/// marker, `icon_player1`, in player two's half; the partner marker, `icon_player2`, in player one's), placed
	/// through that half's camera inside that half. The guest states each of the two records against the camera
	/// of the half it is drawn in and switches it by that half's camera (coop_player_icons.cpp), so a stated
	/// marker is drawn. Through the window camera across the whole window, player one's hero (that camera's
	/// centre) put the `icon_player1` badge on the divider (int7g).
	/// </summary>
	private void MarkerScreens(bool split)
	{
		_markerScreens.Clear();
		for (int index = 0; index < (split ? 2 : 1); ++index)
		{
			Engine_HudSpriteView view = _views[index];
			if (view.Camera is { } eye && GodotObject.IsInstanceValid(eye) && eye.IsInsideTree())
			{
				_markerScreens.Add((index, eye, view.Viewport, view.Frame.Origin, view.Frame.Window));
			}
		}
	}

	/// <summary>
	/// The markers the guest states this frame, placed by Godot's own camera as sub_82B51B58
	/// places them (see above) and faded as their slides do: each fade's own hud.bgf opacity track
	/// (<see cref="Fable3MarkerPlacement.Alpha"/>).
	/// </summary>
	private void PlaceMarkers(Vector2 window, bool split)
	{
		_markers.Clear();
		if (window.X <= 0f || window.Y <= 0f || _markerSprites.Count == 0)
		{
			return;
		}

		MarkerScreens(split);
		if (_markerScreens.Count == 0)
		{
			return;
		}

		foreach (HostBridge.WorldSprite sprite in _markerSprites)
		{
			if (!_markerArt.TryGetValue(sprite.Type, out Texture2D? art))
			{
				foreach ((uint type, string path) in Fable3IconArt.Paths)
				{
					if (type == sprite.Type)
					{
						art = Fable3Art.Load(Fable3IconArt.BankPath(path));
						break;
					}
				}

				_markerArt[sprite.Type] = art;
				if (art is null)
				{
					GD.Print($"[fable3-markers] type {sprite.Type}: no art for the type");
				}
			}

			if (art is null)
			{
				continue;
			}

			if (!_markerShownAt.TryGetValue(sprite.Object, out float shownAt) || sprite.Age < shownAt)
			{
				shownAt = sprite.Age;
				_markerShownAt[sprite.Object] = shownAt;
			}

			// Fade Out starts with one second left (0x82B52020); Fade In at the first show.
			Fable3MarkerPlacement.Fades? fades = sprite.Type == MarkerHero ? _heroFades : _henchmanFades;
			float alpha = sprite.Fading
				? Fable3MarkerPlacement.Alpha(fades, true, (MarkerFadeSeconds - sprite.SecondsLeft) / MarkerFadeSeconds)
				: Fable3MarkerPlacement.Alpha(fades, false, (sprite.Age - shownAt) / MarkerFadeSeconds);
			foreach ((int index, Camera3D eye, Vector2 view, Vector2 origin, Vector2 size) in _markerScreens)
			{
				if (view.X <= 0f || view.Y <= 0f || size.Y <= 0f || (split && Engine_HudSpriteView.PartnerMarkerHalf(sprite.Type) != index))
				{
					continue;
				}

				// Placed in the screen camera's own pixels, then onto the window; sized by the screen's height.
				ulong key = MarkerKey(sprite.Object, index);
				(Vector2 placed, string where) = PlaceMarker(eye, view, key, sprite.Anchor, view.Y * PixelsPerUnit / CanvasHeight);
				Vector2 root = Fable3MarkerPlacement.OnWindow(placed, view, origin, size);
				float unit = size.Y * PixelsPerUnit / CanvasHeight;
				_markers.Add((art, root - new Vector2(0f, Fable3MarkerPlacement.IconLift * unit), Fable3MarkerPlacement.Side * unit, alpha));
				if (_markersReported.Count < 32 && _markersReported.Add(key))
				{
					GD.Print($"[fable3-markers] type {sprite.Type} icon {sprite.Object:X8} entity {sprite.Entity:X8}: " +
						$"drawn {where} at ({root.X:F0},{root.Y:F0}) of {window.X:F0}x{window.Y:F0}" +
						(split ? $" in player {index + 1}'s half" : string.Empty));
				}
			}
		}

		// The guest's offset lives as long as its icon; an icon hidden a while keeps it. Forgotten
		// only once more icons were seen than any session has partners.
		if (_markerPlaced.Count > 32)
		{
			var held = new HashSet<uint>();
			foreach (HostBridge.WorldSprite sprite in _markerSprites)
			{
				held.Add(sprite.Object);
			}

			foreach (ulong key in new List<ulong>(_markerPlaced.Keys))
			{
				if (!held.Contains((uint)key))
				{
					_markerPlaced.Remove(key);
					_markerLift.Remove(key);
				}
			}
		}

		if (_markerShownAt.Count > 8)
		{
			var live = new HashSet<uint>();
			foreach (HostBridge.WorldSprite sprite in _markerSprites)
			{
				live.Add(sprite.Object);
			}

			foreach (uint key in new List<uint>(_markerShownAt.Keys))
			{
				if (!live.Contains(key))
				{
					_markerShownAt.Remove(key);
				}
			}
		}
	}

	/// <summary>
	/// A marker's root in the screen camera's pixels (y down; <paramref name="window"/> is that camera's viewport,
	/// <paramref name="icon"/> the marker's key on that screen): the stated world point through the camera,
	/// pushed in front when behind it and dropped to the bottom edge, raised by its measured world
	/// lift when on screen (<see cref="MarkerLift"/>), then held inside sub_82B519C8's margins with
	/// its 10 / 40 pixels scaled from 720 lines to the window. Behind and the push are the guest
	/// camera's (<see cref="Fable3MarkerPlacement.Behind"/>, <see cref="Fable3MarkerPlacement.Push"/>:
	/// its 0.1 near plane, not Godot's camera's). <paramref name="unit"/> is that viewport's pixels per hud.bgf unit.
	/// </summary>
	private (Vector2 Root, string Where) PlaceMarker(Camera3D camera, Vector2 window, ulong icon, Vector3 guest, float unit)
	{
		Vector3 world = GameWorld.GuestToGodot(guest);
		Vector2 pixel;
		string where;
		Transform3D eye = camera.GlobalTransform;
		Vector3 forward = -eye.Basis.Z.Normalized();
		float distance = forward.Dot(world - eye.Origin);
		if (Fable3MarkerPlacement.Behind(distance))
		{
			pixel = camera.UnprojectPosition(world + (forward * Fable3MarkerPlacement.Push(distance)));
			pixel.Y = window.Y;
			where = "behind";
		}
		else
		{
			pixel = camera.UnprojectPosition(world);
			bool onScreen = pixel.X >= 0f && pixel.X < window.X && pixel.Y >= 0f && pixel.Y < window.Y;
			if (onScreen)
			{
				if (!_markerLift.ContainsKey(icon) && _markerPlaced.TryGetValue(icon, out Vector2 placed) &&
					MarkerLift(camera, world, placed, unit) is { } measured)
				{
					_markerLift[icon] = measured;
				}

				float lift = _markerLift.TryGetValue(icon, out float held) ? held : 0f;
				pixel = camera.UnprojectPosition(world + new Vector3(0f, lift, 0f));
			}

			where = onScreen ? "on screen" : "off screen";
		}

		pixel = Fable3MarkerPlacement.Clamp(pixel, window);
		_markerPlaced[icon] = pixel;
		return (pixel, where);
	}

	/// <summary>
	/// The guest's one measurement of the on-screen lift (sub_82B51B58 0x82B51E04..0x82B51E7C over
	/// sub_82B529E8): the world height from the icon's bottom-right corner as it was last placed
	/// (<see cref="Fable3MarkerPlacement.LiftCorner"/>), unprojected at the point's own depth, down
	/// to <paramref name="world"/>. Null for a lift of 0, which the guest measures again (0x82B51E3C).
	/// </summary>
	private static float? MarkerLift(Camera3D camera, Vector3 world, Vector2 placed, float unit)
	{
		float depth = -(camera.GlobalTransform.AffineInverse() * world).Z;
		if (!(depth > 0f))
		{
			return null;
		}

		float lift = world.Y - camera.ProjectPosition(Fable3MarkerPlacement.LiftCorner(placed, unit), depth).Y;
		return lift != 0f && float.IsFinite(lift) ? lift : null;
	}

	public override void _Draw()
	{
		foreach ((Texture2D art, Vector2 centre, float side, float alpha) in _markers)
		{
			DrawTextureRect(art, new Rect2(centre - (new Vector2(side, side) * 0.5f), new Vector2(side, side)), false,
				new Color(1f, 1f, 1f, alpha));
		}

		if (_rings.Count == 0)
		{
			return;
		}

		// Only the filled part: the guest's path is points 0..progress from twelve o'clock clockwise
		// (the loop to `v28` at 0x8297A7D4), with no track behind it.
		foreach ((Vector2 centre, float radius, float progress, bool complete, float stroke) in _rings)
		{
			if (progress > 0f)
			{
				float sweep = Mathf.Tau * Mathf.Clamp(progress, 0f, 1f);
				Color ink = complete ? new Color(1f, 0.86f, 0.55f) : Colors.White;
				DrawArc(centre, radius, -Mathf.Pi * 0.5f, (-Mathf.Pi * 0.5f) + sweep, 48, ink, stroke, true);
			}
		}
	}

	// ---- The emotion choices, as the guest states them (icon_feed.cpp) ------------------------

	/// <summary>One stated choice record and the stated state of its emotion icon.</summary>
	public readonly record struct EmotionChoice(
		int Action, ulong Player, uint Entity, uint IconType, bool Holdable, bool Manual, bool Held, bool RingOn,
		bool Complete, bool Drawn, uint Icon, Vector3 Anchor, float Progress, float Speed, float Scale,
		string Name, string Text, string RecordName);

	private const int ChoiceWords = 8;
	private const int ChoiceValues = 6;
	private const int MaxChoices = 16;
	private static readonly uint[] _choiceWords = new uint[MaxChoices * ChoiceWords];
	private static readonly float[] _choiceValues = new float[MaxChoices * ChoiceValues];
	private static readonly byte[] _choiceText = new byte[8192];
	private static readonly List<EmotionChoice> _choices = new();

	/// <summary>
	/// Whether the guest has emotion choices up (a stated record, read this frame): the face buttons
	/// — and on the keyboard 1-5 and the left mouse button, as Fable III PC binds them — pick them.
	/// </summary>
	public static bool ChoicesUp { get; private set; }

	/// <summary>The choices this frame; the list is reused until the next call.</summary>
	public static IReadOnlyList<EmotionChoice> GetEmotionChoices()
	{
		_choices.Clear();
		if (!HostBridge.IsGuestUp)
		{
			return _choices;
		}

		uint count;
		try
		{
			count = RefiiHostGetFable3EmotionChoices(
				_choiceWords, (uint)_choiceWords.Length, _choiceValues, (uint)_choiceValues.Length,
				_choiceText, (uint)_choiceText.Length, MaxChoices);
		}
		catch (System.EntryPointNotFoundException)
		{
			return _choices;
		}

		int at = 0;
		string Next()
		{
			int end = System.Array.IndexOf(_choiceText, (byte)0, at);
			if (end < 0)
			{
				end = _choiceText.Length;
			}

			string value = System.Text.Encoding.UTF8.GetString(_choiceText, at, end - at);
			at = System.Math.Min(end + 1, _choiceText.Length);
			return value;
		}

		for (int i = 0; i < count; ++i)
		{
			int w = i * ChoiceWords;
			int v = i * ChoiceValues;
			uint flags = _choiceWords[w + 5];
			string name = Next();
			string text = Next();
			string record = Next();
			// The owner uid is 64 bits at rec+8, high word first (big-endian): words +1 = rec+8, +2 = rec+12,
			// as the record's writer stores the hero's +0x18 / +0x1C (TU3 0x8260F858..0x8260F864 / disc
			// 0x825E86A0..0x825E86AC) and RefiiHostGetEntityUid packs them.
			_choices.Add(new EmotionChoice(
				(int)_choiceWords[w], ((ulong)_choiceWords[w + 1] << 32) | _choiceWords[w + 2], _choiceWords[w + 3],
				_choiceWords[w + 4], (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, (flags & 8) != 0,
				(flags & 16) != 0, (flags & 32) != 0, _choiceWords[w + 6],
				new Vector3(_choiceValues[v], _choiceValues[v + 1], _choiceValues[v + 2]),
				_choiceValues[v + 3], _choiceValues[v + 4], _choiceValues[v + 5], name, text, record));
		}

		return _choices;
	}

	[DllImport(HostBridge.Library)]
	private static extern uint RefiiHostGetFable3EmotionChoices(
		[Out] uint[] words, uint wordCapacity, [Out] float[] values, uint valueCapacity,
		[Out] byte[] text, uint textCapacity, uint maxChoices);
}
