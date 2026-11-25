namespace Refii.Titles.Fable2;

using System.Runtime.CompilerServices;
using Refii.Core;
using Refii.Ui;

/// <summary>
/// Fable 2 to the shell: its game-level pieces, installed into the shell's registries when it is the
/// active title (<see cref="Engine_TitleGame"/>).
/// </summary>
public sealed class Fable2Title : Engine_TitleGame
{
	[ModuleInitializer]
	internal static void RegisterTitle() => Register(new Fable2Title());

	public override Engine_Title Title => Engine_Title.Fable2;

	/// <summary>The save stream and the guest's tolerance of unknown records are proven (ModSaveFilter).</summary>
	public override bool FiltersSaveRecords => true;

	/// <summary>The host page book (Engine_UiHostBoxPage) presents the mod save gate's question.</summary>
	public override bool AsksHostQuestions => true;

	/// <summary>The front-end dialog, the page book and the query paper present them.</summary>
	public override bool PresentsHostBoxes => true;

	private static readonly ArchonModManager Archon = new();

	public override Refii.Mods.Engine_ExternalModManager ExternalMods => Archon;

	public override System.Collections.Generic.IReadOnlyList<Refii.Mods.ModGuideCard> ModGuideCards(string page, bool closing) =>
		Fable2ModGuideCards.For(page, closing);

	protected override void Install()
	{
		Fable2PageProviders.Register();
		Fable2MenuSources.Register();
		Refii.Ui.Engine_UiVerbs.AddTitleVerbs(Fable2UiVerbs.All);
		Refii.Ui.Engine_UiHostOptionsPage.Register(Engine_UiVibrationOptions.OpenCommand, _ => Engine_UiVibrationOptions.Page);
	}

	public override void RegisterUiVerbs(Refii.Ui.Engine_UiCommandSet set) => Fable2UiVerbs.RegisterHandlers(set);

	public override Godot.Node CreateUi() => new GameUi { Name = "GameUi" };

	public override void ApplyUiTemplates(Refii.Ui.Engine_UiTemplateSet set) => Fable2UiTemplates.Apply(set);

	private GamePubGames? _pubGames;

	public override void AddSessionNodes(Godot.Node session, System.Action<bool>? holdGuest = null)
	{
		HostUiSource.Install();
		Godot.GD.Print("[game] the UI reads the live guest now (Engine_UiSource -> HostUiSource)");

		session.AddChild(new GameDecorate { Name = "DecorateMode" });
		session.AddChild(new GameCharacterMorphs { Name = "CharacterMorphs" });
		session.AddChild(new GameMorphMirror { Name = "MorphMirrorInteraction" });
		// Its dialog holds the host's guest where the host asks (the Android host's own thread).
		session.AddChild(new GameGoldenCock { Name = "GoldenCockInteraction", HoldGuest = holdGuest });
		session.AddChild(new GameGoldenCockCollision { Name = "GoldenCockCollision" });

		// The pub games watch the guest's published table state; they are not launched from here.
		_pubGames = new GamePubGames { Name = "GamePubGames" };
		session.AddChild(_pubGames);
	}

	public override void Step() => _pubGames?.Poll();

	public override System.Collections.Generic.Dictionary<string, object?>? DebugRequest(
		string op, System.Text.Json.JsonElement request) => Fable2Debug.Request(op, request);

	public override System.Collections.Generic.IReadOnlyList<string> DebugOps => Fable2Debug.Ops;

	/// <summary>The row pressed as a player's A on the Godot list that holds it (GameUi.RequestRow) — the
	/// ported screens' OnMenuEvent stubs answer nothing a posted event says.</summary>
	public override System.Collections.Generic.Dictionary<string, object?>? DebugMenuEvent(string name, int type, uint id) =>
		GameUi.RequestRow(name, type, id);

	public override bool? GuestVibrationEnabled => Engine_UiVibrationOptions.GuestEnabled();

	public override bool LocalCoop => CoopSession.LocalCoop;

	public override bool OnlineClient => CoopSession.IsClient;

	public override bool PeerJoined => CoopSession.PeerStanding;

	public override bool InteractionOwnsPad => GameGoldenCock.OwnsInput || GameMorphMirror.OwnsInput || GameDecorate.Active;

	public override bool InteractionOwnsDevices => GameMorphMirror.OwnsInput || GameDecorate.Active;

	public override string? KeyLabel(string button) =>
		GameDecorate.Active ? GameDecorate.KeyLabel(button) : GameMorphMirror.KeyLabel(button);

	public override bool WorldInteractionAllowed =>
		GameUi.WorldInteractionAllowed && !GameGoldenCock.OwnsInput && !GameMorphMirror.OwnsInput && !GameDecorate.Active &&
		GameGoldenCock.SessionAllowsInteraction(CoopSession.State, CoopSession.Side, CoopSession.LocalCoop);

	// Decorating owns the camera too: it places it itself while it is up.
	public override bool ApplyCamera(Godot.Camera3D camera) => GameMorphMirror.ApplyCamera(camera) || GameDecorate.Active;

	public override bool InteractionBoardUp => GamePubGames.Showing.Length != 0;

	public override string? UseItemAction(uint record) => $"{Engine_UiInventorySource.InventoryAction}use:{record}";

	public override string? FindSaveDirectory(string saveRoot, string save) => Engine_SaveHero.FindSaveDirectory(saveRoot, save);

	/// <summary>
	/// A Fable 2 save's own files, the ones a 360 writes inside its STFS package: what a console or
	/// Xenia reads, and all a copy for one carries.
	/// </summary>
	private static readonly string[] GuestSaveFiles =
	{
		"mainsave.bin", "herosave.bin", "chaptersave.bin", "saveuid.bin", "texturemorphs.bin",
		"failquestsave.bin", "Fable2PubInfo.xml",
	};

	public override System.Collections.Generic.IReadOnlyList<string> SaveGuestFiles => GuestSaveFiles;

	public override Refii.Text.Engine_BabelDatabase? Strings => Engine_UiInventorySource.Strings;

	public override bool InteractionDialogUp => GamePubGames.DialogUp;

	public override void RequestBack()
	{
		if (GameDecorate.Active) GameDecorate.RequestBack();
		else GameMorphMirror.RequestBack();
	}

	public override Refii.Host.HostBridge.GuestEntity Appearance(Refii.Host.HostBridge.GuestEntity entity) =>
		GameDecorate.Appearance(entity);

	public override bool RestatesEntities => GameDecorate.RestatesEntities;

	public override bool WantsOwnNode(Refii.Host.HostBridge.GuestEntity entity) => GameDecorate.Targeted(entity.UniqueId);

	public override uint CompanionEntityAddress => Fable2Bridge.GetMinimapLive().Dog;

	public override Godot.Collections.Dictionary ModMinimap(uint hero) => Engine_UiMinimapMod.State(hero);

	public override Godot.Vector2? ModMinimapPoint(Godot.Vector3 world) => Engine_UiMinimapMod.Point(world);

	public override Godot.Collections.Array ModInventoryItems(int player) => Engine_UiInventorySource.ModItems(player);

	public override string? ModInventoryAction(string operation, uint record, int category, int player) =>
		Engine_UiInventorySource.ModAction(operation, record, category, player);


	public override int HeroMorphPreviewGeneration => GameCharacterMorphs.PreviewGeneration;

	public override bool HeroMorphPreviewHeld => GameMorphMirror.OwnsInput;

	public override int HeroMorphPreviewPlayer => GameCharacterMorphs.PreviewPlayer;

	public override float ManualTan(int player) => GameCharacterMorphs.ManualTanOf(player);

	public override bool ObserveEntity(in Refii.Host.HostBridge.GuestEntity entity) =>
		GameGoldenCock.ObserveGuest(entity) || GameMorphMirror.ObserveGuest(entity);

	public override void ObserveHero(Refii.Host.HostBridge.GuestEntity hero, uint frame) =>
		GameGoldenCockCollision.ObserveHero(hero, frame);

	public override bool WheelClaimsFaceButtons => Engine_UiSpellAssign.Secondary is not null;

	/// <summary>
	/// The game's own world hold: the pump pushes a <c>CGameModePause</c> GameFlow command, as
	/// <c>lhGuiManager_EnterGuiMode</c> does under a screen, and pops it again (guest_tick_pump.cpp,
	/// <c>WorldHold:</c>). The game keeps its steps, so the GUI, audio and timed waits run on.
	/// </summary>
	public override bool HoldWorld(bool held)
	{
		Refii.Host.HostBridge.PostFrontEndAction(held ? "WorldHold:on" : "WorldHold:off");
		return true;
	}

	public override void PumpMods(bool guestUp)
	{
		Engine_UiModAbilities.Pump(guestUp);
		Engine_UiSpellPicks.Pump(guestUp);
	}

	public override void OnAssetsMounted(Refii.Bnk.BnkFileSystem mounted)
	{
		CoopHeroLua.Attach(mounted);
		Engine_SaveCompatLua.Attach(mounted, CoopHeroLua.Key);
		CoopText.Attach(mounted);
		try { Engine_GoldenCockRuntime.Attach(mounted, Engine_GoldenCockPlacements.Load()); }
		catch (System.Exception exception) { Godot.GD.PushError("[golden-cock] " + exception.Message); }
		try { Engine_MorphMirrorRuntime.Attach(mounted, Engine_MorphMirrorPlacements.Load()); }
		catch (System.Exception exception) { Godot.GD.PushError("[morph-mirror] " + exception.Message); }
		try { Engine_DecorateCatalog.Attach(mounted); }
		catch (System.Exception exception) { Godot.GD.PushError("[decorate] " + exception.Message); }
		try { Engine_NewGameRuntime.Attach(mounted); }
		catch (System.Exception exception) { Godot.GD.PushError("[new-game] " + exception.Message); }
	}

	public override void DressLevel(Godot.Node3D root, Refii.Level.LevelEntry level)
	{
		Engine_GoldenCockPlacements.AddToLevel(root, level);
		Engine_MorphMirrorPlacements.AddToLevel(root, level);
	}

	public override void CaptureLevelPlacements(Godot.Node3D levelRoot)
	{
		if (Engine_GoldenCockPlacements.Capture(levelRoot))
			Godot.GD.Print("[golden-cock] Saved level placement for the installed game.");
		if (Engine_MorphMirrorPlacements.Capture(levelRoot))
			Godot.GD.Print("[morph-mirror] Saved the marked mirror and its set for the installed game.");
	}

	private static readonly Fable2Rig Rigs = new();

	public override Refii.Animations.Engine_TitleRig Rig => Rigs;

	private static readonly Fable2Props Buildings = new();

	public override Refii.Level.Engine_TitleProps Props => Buildings;

	public override bool IsTitleRecord(uint record) => record == Engine_MorphMirrorRuntime.Record;

	public override bool? AllowsVistaOcean(Refii.Level.LevelEntry level) => Fable2LevelRules.AllowsVistaOcean(level);

	public override Refii.Level.DynamicWeatherRule? WeatherRule(Refii.Level.LevelEntry level) => Fable2LevelRules.WeatherRule(level);

	public override bool HandleBusLine(int kind, string fields)
	{
		if (kind != CoopHeroKit.PickKind)
		{
			return false;
		}

		CoopHeroKit.HandlePick(fields);
		return true;
	}

	public override Refii.Ui.Engine_UiPopulationTable? PopulationTable(string script) => Engine_UiMenuPopulation.For(script);

	public override string? HeldMenuScript =>
		Engine_UiDyeFlow.Active ? Engine_UiDyeFlow.MenuScript
		: Engine_UiAugmentFlow.HeldScript;

	public override string? OverrideMenuScript(uint screenId)
	{
		string? held = HeldMenuScript;
		if (held is null)
		{
			return null;
		}

		// Both held lists live on the items screen.
		if (screenId == Engine_UiDyeFlow.ItemsScreen)
		{
			return held;
		}

		if (screenId != 0)
		{
			Engine_UiDyeFlow.End();
			Engine_UiAugmentFlow.End();
		}

		return null;
	}

	public override bool IsModAbilityRow(string command) =>
		command.StartsWith(Engine_UiModAbilities.BuyAction, System.StringComparison.Ordinal);

	private static readonly System.Collections.Generic.HashSet<string> OwnedHud = new(System.StringComparer.Ordinal)
	{
		Engine_UiSpellAssign.ElementName,
		Engine_UiSpellAssign.ElementName + "2",
	};

	public override System.Collections.Generic.IReadOnlyCollection<string> HudOwnedSubtrees => OwnedHud;

	public override Refii.Ui.Engine_HudTitleLayout HudLayout => Fable2HudLayout.Instance;

	public override Refii.Ui.Engine_UiStatedBoxes StatedBoxes => Engine_UiDisplayBoxes.Statements;

	private static readonly string[] Pips =
	{
		Engine_UiHudExpressionMeter.HeroPip, Engine_UiHudExpressionMeter.HenchmanPip,
		Engine_UiHudExpressionMeter.BothPip,
	};

	public override System.Collections.Generic.IReadOnlyList<string> HudPipBindings => Pips;

	public override bool IsFrontEndPresentation(string presentation) => Engine_UiFrontEndScreens.Is(presentation);

	public override (bool Guards, string? Refusal) FrontEndOpenGuard(Refii.Ui.Engine_FrontEndMenu menu,
		Refii.Ui.Engine_UiExpandableMenu.Item item) => Fable2FrontEndRoot.OpenGuard(menu, item);

	public override string ScreenMovie(uint screenId, Refii.Ui.Engine_UiExpandableMenu.Item? row,
		Refii.Host.HostBridge.HeroVitals vitals) =>
		Refii.Ui.Engine_UiTemplates.Screen(screenId)?.Provider == Fable2PageProviders.Abilities
			? Engine_UiAbilityPage.MovieFor(row, vitals.AbilityLevels)
			: string.Empty;

	public override int MenuPlayer => Engine_UiPropertySource.MenuPlayer();

	public override int OptionsCursorRow => Engine_UiOptionsPending.Row;

	public override int OptionsCursorIndex(int row) => Engine_UiOptionsPending.IndexOf(row);

	public override void SetOptionsCursorIndex(int row, int index) => Engine_UiOptionsPending.SetIndex(row, index);

	/// <summary>
	/// The shipped scheme's buttons (<c>GameControlsCombatOnRelease</c> over <c>GameControlsBase</c>,
	/// as GameInput.KeyboardMouse.cs's table states them): each does several things by context.
	/// </summary>
	public override string? PadButtonRole(Refii.Core.PadInput button) => button switch
	{
		Refii.Core.PadInput.A => "Interact, sprint, accept",
		Refii.Core.PadInput.B => "Magic, back",
		Refii.Core.PadInput.X => "Melee, block, flourish",
		Refii.Core.PadInput.Y => "Ranged, aim",
		Refii.Core.PadInput.LB => "First person, tab left",
		Refii.Core.PadInput.RB => "Tab right",
		Refii.Core.PadInput.LT => "Target lock",
		Refii.Core.PadInput.RT => "Spells",
		Refii.Core.PadInput.L3 => "Photo mode (with R3)",
		Refii.Core.PadInput.R3 => "Camera mode",
		Refii.Core.PadInput.Start => "Pause",
		Refii.Core.PadInput.Back => "Expressions",
		Refii.Core.PadInput.DpadUp => "Item up",
		Refii.Core.PadInput.DpadDown => "Item down",
		Refii.Core.PadInput.DpadLeft => "Item left",
		Refii.Core.PadInput.DpadRight => "Item right",
		_ => null,
	};

	public override void EnterHostOptions(Refii.Ui.Engine_UiHostOptionsPage page) => Engine_UiOptionsPending.EnterHost(page);

	public override void ReloadModData()
	{
		Engine_UiSpellAssign.ReloadSecondary();
		Engine_UiModAbilities.Reload();
	}

	public override Refii.Game.GameTitleInput Input => Fable2Input.Instance;

	public override System.Collections.Generic.IEnumerable<Refii.Game.GameDevTab> GuestDevTabs()
	{
		yield return new GameDevTabGuest();
	}

	public override uint? PlayerBuilding(int player) =>
		player == 2 ? Fable2Bridge.PlayerTwoBuilding() : Fable2Bridge.HeroBuilding();

	public override int HighlightWidthTexels => Fable2Bridge.HighlightWidthTexels();

	// The pub games' SWF assets: bitmaps, shapes and fonts under game://FlashGames/.

	public override void IndexLooseData(Refii.Bnk.BnkFileSystem? filesystem, string gameDirectory) =>
		PubGames.Engine_SwfAssets.Index(filesystem, gameDirectory);

	public override void AddResourceLoaders() => PubGames.Engine_SwfAssets.EnsureLoaders();

	public override void RemoveResourceLoaders() => PubGames.Engine_SwfAssets.RemoveLoaders();

	public override void PrepareEditor()
	{
		try
		{
			PubGames.Engine_SwfAssets.EnsureLoaders();
			PubGames.Engine_SwfAssets.EnsureIndexed();
		}
		catch (System.Exception exception)
		{
			Godot.GD.PushWarning($"[swf] pub games not indexed: {exception.Message}");
		}
	}

	public override TitleAssetKind AssetKind(string virtualPath) =>
		PubGames.Engine_SwfAssets.RecognisesTexture(virtualPath) ? TitleAssetKind.Texture
		: PubGames.Engine_SwfAssets.RecognisesMesh(virtualPath) ? TitleAssetKind.Mesh
		: PubGames.Engine_SwfAssets.RecognisesFont(virtualPath) ? TitleAssetKind.Font
		: TitleAssetKind.None;

	public override Godot.Image? AssetImage(string virtualPath) => PubGames.Engine_SwfAssets.TryGetImage(virtualPath);
}
