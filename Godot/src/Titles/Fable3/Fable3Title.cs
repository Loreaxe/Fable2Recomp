namespace Refii.Titles.Fable3;

using System.Runtime.CompilerServices;
using Refii.Core;

/// <summary>
/// Fable 3 to the shell: its native screens, which its hooks state on the host-only bus.
/// </summary>
public sealed class Fable3Title : Engine_TitleGame
{
	[ModuleInitializer]
	internal static void RegisterTitle() => Register(new Fable3Title());

	public override Engine_Title Title => Engine_Title.Fable3;

	/// <summary>
	/// Measured 2026-11-09 (Mistpeak camp, the guest's own cloth publish): the guest simulated and
	/// drew the morphed hero's hat and both coat pieces as cloth (3 pieces stated and drawn for the
	/// hero entity), so its morphed garments keep their cloth.
	/// </summary>
	public override bool MorphedModelsKeepCloth => true;

	/// <summary>An online joiner, as the payload states it from the game's own Network.IsClient each tick.</summary>
	public override bool OnlineClient => Refii.Host.HostBridge.PlayerOwned() == 2u;

	/// <summary>Online, as the game answers it (Network.IsInLiveGame, stated by the payload each tick).</summary>
	public override bool PeerJoined => Refii.Host.HostBridge.PlayerOwned() is 1u or 2u;

	/// <summary>Fable3Boxes presents the host's questions and notices and a mod's menu box in Fable 3's own box look.</summary>
	public override bool PresentsHostBoxes => true;

	/// <summary>Whose menu a host menu or box is: this machine's player, 2 on an online joiner, else 1.</summary>
	public override int MenuPlayer => Refii.Host.HostBridge.PlayerOwned() == 2u ? 2 : 1;

	/// <summary>A mod marker's icon names to this game's own world-icon art.</summary>
	public override System.Collections.Generic.IReadOnlyDictionary<string, string>? MarkerIconAliases => Fable3IconArt.MarkerAliases;

	/// <summary>Fable 3's progression is bought in guild seals: a mod ability's price is its <c>seals</c> list.</summary>
	public override bool PricesAbilitiesInSeals => true;

	/// <summary>
	/// The mod abilities, declared to the payload's Lua engine (Fable3CoreLua: Refii._ability_declare), which
	/// lives in the game VM and so is emptied when a load rebuilds it: the engine publishes how many it holds
	/// (<c>refii.abilities</c>), and a count short of the declared ones declares them again.
	/// </summary>
	public override void PumpMods(bool guestUp)
	{
		DriveModScreen(guestUp);
		// The mods' own morphs (Engine_ModMorphs) for the morph hook to answer, as Fable 2's GameCharacterMorphs
		// publishes them: again after each mount.
		if (_morphRegistry != Refii.Hero.Engine_ModMorphs.Generation)
		{
			_morphRegistry = Refii.Hero.Engine_ModMorphs.Generation;
			foreach (string problem in Refii.Hero.Engine_ModMorphs.Problems)
			{
				Godot.GD.PushWarning("[mods] morphs " + problem);
			}

			Refii.Hero.Engine_ModMorphs.Publish(Refii.Hero.Engine_ModMorphs.All, Refii.Host.HostBridge.SetModSetting, ++_morphsPublished);
		}

		Refii.Ui.Engine_UiModAbilities.Pump(guestUp);
		if (!guestUp || !Refii.Host.HostBridge.IsInGame)
		{
			return;
		}

		int declared = 0;
		foreach (Refii.Ui.Engine_UiModAbilities.Ability ability in Refii.Ui.Engine_UiModAbilities.All)
		{
			declared += ability.Seals is { Count: > 0 } ? 1 : 0;
		}

		if (declared == 0)
		{
			return;
		}

		ulong now = Godot.Time.GetTicksMsec();
		if (now - _lastAbilityCheck < 2000)
		{
			return;
		}

		_lastAbilityCheck = now;
		float held = Refii.Host.HostBridge.TryGetModValue("refii.abilities", out float value) ? value : 0f;
		if (held < declared)
		{
			Refii.Ui.Engine_UiModAbilities.Redeclare();
		}
	}

	private ulong _lastAbilityCheck;
	private Godot.CanvasItem? _hud;
	private Godot.CanvasItem? _statedScreen;
	private bool _modScreenUp;
	private bool _wasInGame;
	private int _morphRegistry = -1;
	private int _morphsPublished;

	/// <summary>
	/// A MOD'S SCREEN (<see cref="Refii.Mods.ModScreen"/>: a Godot script's <c>own_screen</c>) stands where
	/// the game's own screens would, as on Fable 2: they and the HUD draw nothing, and only Start reaches the
	/// game (<see cref="Fable3Input"/>), so the script's screen has the pad, the mouse and the keys. Released
	/// when the game is left.
	/// </summary>
	private void DriveModScreen(bool guestUp)
	{
		bool inGame = guestUp && Refii.Host.HostBridge.IsInGame;
		// A mod's HUD overrides go when the game is left, as on Fable 2 (GameUi's leave).
		if (_wasInGame && !inGame)
		{
			Refii.Ui.Engine_UiHudOverrides.Clear();
		}

		_wasInGame = inGame;
		if (Refii.Mods.ModScreen.Owned && !inGame)
		{
			Refii.Mods.ModScreen.Release();
		}

		bool up = Refii.Mods.ModScreen.Owned && inGame;
		if (up == _modScreenUp)
		{
			return;
		}

		_modScreenUp = up;
		foreach (Godot.CanvasItem? item in new[] { _hud, _statedScreen })
		{
			if (item is not null && Godot.GodotObject.IsInstanceValid(item))
			{
				item.Visible = !up;
			}
		}
	}

	/// <summary>Fable 3's shader banks are entries of levels.bnk, not loose files.</summary>
	public override (string Named, string Release)? PackedShaderBanks =>
		("shaders/shaders.sbk", "shaders/shadersrelease.sbk");

	/// <summary>Fable 3's helper calls for mods' Lua (<see cref="Fable3CoreLua"/>), run before any mod's.</summary>
	public override void OnAssetsMounted(Refii.Bnk.BnkFileSystem mounted) => Fable3CoreLua.Attach(mounted);

	private Fable3Splash? _splash;

	/// <summary>The loading screen's layer: over the screen fade (7), as Fable 2's is.</summary>
	private const int LoadingLayer = 8;

	public override Godot.Node CreateUi()
	{
		Fable3UiSource.Install();

		var layer = new Godot.CanvasLayer { Name = "Fable3Ui", Layer = 5 };
		layer.AddChild(new Fable3Strings { Name = "Strings" });
		// The map table the guest presents in place of the world, under every UI surface.
		layer.AddChild(new Fable3MapTable { Name = "MapTable" });
		// The HUD on the shared view, under every other surface as the guest's HUD layer is; its
		// screen fade stands on its own canvas layer over all of them.
		var hud = new Fable3Hud { Name = "Hud" };
		_hud = hud;
		layer.AddChild(hud);
		var natives = new Fable3GuiNatives { Name = "GuiNatives" };
		layer.AddChild(natives);
		// The world icons sit under the boxes and screens, as the guest's HUD layer does.
		layer.AddChild(new Fable3WorldIcons { Name = "WorldIcons" });
		layer.AddChild(new Fable3Options { Name = "Options" });
		layer.AddChild(new Fable3HostOptions { Name = "HostOptions" });
		var statedScreen = new Fable3StatedScreen { Name = "StatedScreen" };
		_statedScreen = statedScreen;
		layer.AddChild(statedScreen);
		layer.AddChild(new Fable3Boxes(natives) { Name = "Boxes" });
		// The console's keyboard, over every screen that asks for it.
		layer.AddChild(new Fable3Keyboard { Name = "Keyboard" });
		// Over the HUD, as Fable 2's toast stands above every presentation.
		layer.AddChild(new Fable3Achievements { Name = "Achievements" });
		// A mod's notices, as the game's own TopBox messages.
		layer.AddChild(new Fable3Notices { Name = "Notices" });
		// Refii's own co-op channel: the players' values, the relayed events and the route (partner messages).
		layer.AddChild(new Fable3CoopChannel { Name = "CoopChannel" });
		_splash = new Fable3Splash { Name = "Splash" };
		layer.AddChild(_splash);
		layer.AddChild(new Fable3Culture { Name = "Culture" });
		// The interaction list in the PC arrangement, Godot's own drawing of the guest's stated rows.
		layer.AddChild(new Fable3InteractionList { Name = "InteractionList" });
		// After every screen has read the pointer: its leftovers are the pad's.
		layer.AddChild(new Fable3PointerLeftovers { Name = "PointerLeftovers" });
		// The PC's lite pause: the host's own, with its notice.
		layer.AddChild(new Fable3LitePause { Name = "LitePause" });
		// The menu pointer's picture: the game's own cursor.
		layer.AddChild(new Fable3MenuCursor { Name = "MenuCursor" });

		var loading = new Godot.CanvasLayer { Name = "Fable3Loading", Layer = LoadingLayer };
		loading.AddChild(new Fable3LoadingScreen { Name = "LoadingScreen" });
		layer.AddChild(loading);
		return layer;
	}

	public override Refii.Text.Engine_BabelDatabase? Strings => Fable3Strings.Current;

	public override Refii.Game.GameTitleInput Input => Fable3Input.Instance;

	public override Refii.Ui.Engine_UiKeyGlyphs.GlyphArt? GlyphArtFor(string path) => Fable3Glyphs.ArtFor(path);

	/// <summary>Player one's dog, as the game binds it to its hero (RefiiHostGetHeroDogs: EntityManager+0xB8 / +0xC0, set by sub_82399CB8, read by sub_821E8250).</summary>
	public override uint CompanionEntityAddress
	{
		get
		{
			try
			{
				return RefiiHostGetHeroDogs(out uint one, out _) == 0u ? 0u : one;
			}
			catch (System.EntryPointNotFoundException)
			{
				return 0u;
			}
		}
	}

	[System.Runtime.InteropServices.DllImport(Refii.Host.HostBridge.Library)]
	private static extern uint RefiiHostGetHeroDogs(out uint one, out uint two);

	/// <summary>
	/// What each game button does, for the Controls page's remap rows: the shipped scheme the guest
	/// loads (`GameControlsCombatOnRelease`, GDB 0x8c1585e4: sub_8294BD08 from sub_8250E798 at boot).
	/// </summary>
	public override string? PadButtonRole(Refii.Core.PadInput button) => button switch
	{
		Refii.Core.PadInput.A => "Interact, run, accept, choice 1",
		Refii.Core.PadInput.B => "Magic, back",
		Refii.Core.PadInput.X => "Melee, block, choice 2",
		Refii.Core.PadInput.Y => "Ranged, aim, choice 3",
		Refii.Core.PadInput.LB => "First person, choice 5",
		Refii.Core.PadInput.RB => "Quick menu, choice 4",
		Refii.Core.PadInput.LT => "Target lock, zoom aim",
		Refii.Core.PadInput.RT => "Look at (hold)",
		Refii.Core.PadInput.L3 => "Photo mode (with R3)",
		Refii.Core.PadInput.R3 => "Camera mode",
		Refii.Core.PadInput.Start => "The Sanctuary",
		Refii.Core.PadInput.Back => "Nickname",
		Refii.Core.PadInput.DpadUp => "Shortcut up",
		Refii.Core.PadInput.DpadDown => "Shortcut down",
		Refii.Core.PadInput.DpadLeft => "Shortcut left",
		Refii.Core.PadInput.DpadRight => "Shortcut right",
		_ => null,
	};

	/// <summary>The host options page (the Controls page) on Fable 3's own driver (<see cref="Fable3HostOptions"/>).</summary>
	public override void EnterHostOptions(Refii.Ui.Engine_UiHostOptionsPage page) => Fable3HostOptions.Enter(page);

	public override void SetOptionsCursorIndex(int row, int index) => Fable3HostOptions.SetIndex(row, index);

	public override int OptionsCursorRow => Fable3HostOptions.Cursor;

	public override int OptionsCursorIndex(int row) => Fable3HostOptions.IndexOf(row);

	/// <summary>
	/// `f3ui` (match, doc, limit): per standing presentation document, each element whose path holds
	/// the match — stated, fed and resolved (<see cref="Fable3HudDocument.Describe"/>).
	/// </summary>
	public override System.Collections.Generic.Dictionary<string, object?>? DebugRequest(
		string op, System.Text.Json.JsonElement request)
	{
		if (op != "f3ui")
		{
			return null;
		}

		string match = request.TryGetProperty("match", out System.Text.Json.JsonElement m) ? m.GetString() ?? string.Empty : string.Empty;
		string doc = request.TryGetProperty("doc", out System.Text.Json.JsonElement d) ? d.GetString() ?? string.Empty : string.Empty;
		int limit = request.TryGetProperty("limit", out System.Text.Json.JsonElement l) ? l.GetInt32() : 60;
		var documents = new System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>();
		foreach (Fable3HudDocument document in Fable3HudDocument.Live)
		{
			if (doc.Length == 0 || document.Document == doc)
			{
				documents.Add(document.Describe(match, limit));
			}
		}

		return new System.Collections.Generic.Dictionary<string, object?> { ["ok"] = true, ["documents"] = documents };
	}

	public override System.Collections.Generic.IReadOnlyList<string> DebugOps => new[] { "f3ui" };

	private static readonly Fable3Rig Rigs = new();

	public override Refii.Animations.Engine_TitleRig Rig => Rigs;

	/// <summary>The `hud.bgf` widgets Fable 3's own surfaces draw rather than the shared HUD view (<see cref="Fable3Hud.Owned"/>).</summary>
	public override System.Collections.Generic.IReadOnlyCollection<string> HudOwnedSubtrees => Fable3Hud.Owned;

	/// <summary>
	/// Fable 3 rolls no weather. Precipitation is four floats of the evaluated theme block
	/// (Snow Size, Snow Fall Speed, Rain Size, Rain Density at T'+0xD4..+0xE0; the rain builder
	/// sub_82C8FFE8 reads scene+236/240, the snow builder sub_82C91F28 scene+228/232) and a
	/// scripted change of weather is a scripted THEME (Lua BlendToEnvironmentTheme, sub_8248FEA8).
	/// RefiiHostGetWeather states the block, so every level follows the theme.
	/// </summary>
	public override Refii.Level.DynamicWeatherRule? WeatherRule(Refii.Level.LevelEntry level) =>
		Refii.Level.DynamicWeatherRule.Theme with { Reason = "fable 3 states precipitation in its theme block" };

	public override bool HandleBusLine(int kind, string fields)
	{
		if (kind != Fable3Splash.StateKind)
		{
			return false;
		}

		if (_splash is not null && Godot.GodotObject.IsInstanceValid(_splash))
		{
			_splash.Stated(fields);
		}

		return true;
	}
}
