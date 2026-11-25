using System;
using System.Collections.Generic;

namespace Refii.Core;

/// <summary>
/// What a title adds to the shared shell: the game-level code that knows ONE game — its screens,
/// its interactions, its co-op model — handed to the shell through the calls below and never named
/// by it.
/// </summary>
/// <remarks>
/// THE SHELL NEVER REFERENCES A TITLE. Each title's implementation lives under
/// <c>Godot/src/Titles/&lt;Folder&gt;/</c> in namespace <c>Refii.Titles.&lt;Folder&gt;</c> and registers
/// itself from a module initializer; the shell asks for <see cref="Active"/> and calls through this
/// type. A test fails when any file outside <c>Titles/</c> names that namespace, so the direction of
/// the dependency is checked rather than hoped for.
/// </remarks>
public abstract class Engine_TitleGame
{
	private static readonly Dictionary<Engine_Title, Engine_TitleGame> Registered = new();

	/// <summary>Called by a title's module initializer.</summary>
	public static void Register(Engine_TitleGame game)
	{
		lock (Registered)
		{
			Registered[game.Title] = game;
		}
	}

	/// <summary>The active title's game, or a title that adds nothing when none registered.</summary>
	public static Engine_TitleGame Active => For(Engine_Title.Active);

	/// <summary>
	/// <paramref name="title"/>'s game, or a title that adds nothing when none registered — for the
	/// shell's answers that name one title's data whichever title is active.
	/// </summary>
	public static Engine_TitleGame For(Engine_Title title)
	{
		lock (Registered)
		{
			return Registered.TryGetValue(title, out Engine_TitleGame? game) ? game : new Bare(title);
		}
	}

	/// <summary>The title this game belongs to.</summary>
	public abstract Engine_Title Title { get; }

	/// <summary>
	/// The running guest creates and places every standing effect its level places, so the level
	/// build spawns none of them from the level data (<see cref="Refii.Fx.Engine_LiveFx.GuestOwnsLevelEffects"/>).
	/// Both Fable 2 and Fable 3 guests create and publish their level-placed <c>CECParticleEmitter</c>
	/// and <c>CECParticleAttacher</c> effects through the live particle wrappers.
	/// </summary>
	public virtual bool GuestStatesLevelEffects => true;

	/// <summary>
	/// A morph-blended model keeps its cloth. Fable 2's guest drops it: its MorphTarget blend copies
	/// the records without the cloth definition (record+64), so its garments draw skinned.
	/// </summary>
	public virtual bool MorphedModelsKeepCloth => false;

	/// <summary>
	/// The mod save filter may rewrite this title's <c>mainsave.bin</c> (a missing mod's records out,
	/// held for restore): only where the save stream's layout and the guest's tolerance of a record it
	/// cannot resolve were read in the binary. Fable 2's were; elsewhere a stamped save loads with its
	/// records as they are and only the player store is filtered.
	/// </summary>
	public virtual bool FiltersSaveRecords => false;

	/// <summary>
	/// The title presents a host question on a page (Engine_UiHostBoxes.AskPage, the host page book).
	/// Where it cannot, the mod save gate does not refuse a start to ask: a save made with mods that are
	/// not enabled loads with their values held out, as Continue on the question would.
	/// </summary>
	public virtual bool AsksHostQuestions => false;

	/// <summary>
	/// The title presents the host's questions and notices (Engine_UiHostBoxes: a mod's message box from
	/// Godot, the co-op consent and mod questions, refunds). Where it does not, a question is answered at
	/// once as withdrawn (-1) instead of waiting for a box nobody draws, and a notice is shown as a mod
	/// notice (Engine_UiNotices, which every title presents).
	/// </summary>
	public virtual bool PresentsHostBoxes => false;

	/// <summary>
	/// Mod abilities are priced in guild seals (abilities.json <c>seals</c>, Fable 3's progression) rather than
	/// by the game's experience table (<c>costLike</c>, Fable 2's). An ability with no price this title reads
	/// is not declared to it.
	/// </summary>
	public virtual bool PricesAbilitiesInSeals => false;

	/// <summary>
	/// The title's UI over the world, standing up beside the session before any guest runs (it
	/// draws from the shipped assets); null when the title has none. The game window drives it
	/// through <c>Refii.Game.IGameTitleUi</c> when the node implements it.
	/// </summary>
	public virtual Godot.Node? CreateUi() => null;

	/// <summary>
	/// Once the guest is up: the title's own nodes — its interactions, its feeds — added to the
	/// session, and its UI pointed at the live guest.
	/// </summary>
	public virtual void AddSessionNodes(Godot.Node session, System.Action<bool>? holdGuest = null)
	{
	}

	/// <summary>
	/// The title's own UI records — its menus' population tables and its templates file — applied
	/// to the shell's template set first, before any mod's.
	/// </summary>
	public virtual void ApplyUiTemplates(Refii.Ui.Engine_UiTemplateSet set)
	{
	}

	/// <summary>
	/// The handlers for the title's own verbs, on the shell's dispatcher, after the shell's own
	/// (the catalog entries are added at <see cref="Install"/>).
	/// </summary>
	public virtual void RegisterUiVerbs(Refii.Ui.Engine_UiCommandSet set)
	{
	}

	/// <summary>
	/// The title's own debug-channel op (<c>{"op": …}</c>) answered, or null when it is not one of
	/// <see cref="DebugOps"/>. Asked after the shell's own ops.
	/// </summary>
	public virtual System.Collections.Generic.Dictionary<string, object?>? DebugRequest(
		string op, System.Text.Json.JsonElement request) => null;

	/// <summary>
	/// The debug channel's `menu event` (name, event type, id): how the title presses that row. Null
	/// when its lists are not Godot's, and the channel posts the guest's <c>MenuEvent</c> as before.
	/// </summary>
	public virtual System.Collections.Generic.Dictionary<string, object?>? DebugMenuEvent(string name, int type, uint id) => null;

	/// <summary>The debug-channel ops <see cref="DebugRequest"/> answers.</summary>
	public virtual System.Collections.Generic.IReadOnlyList<string> DebugOps => System.Array.Empty<string>();

	/// <summary>
	/// The title's part in the pads (<see cref="Refii.Game.GameTitleInput"/>): what the guest sees of
	/// each, which player a pad is for the mods, and when the world has the input.
	/// </summary>
	public virtual Refii.Game.GameTitleInput Input => Refii.Game.GameTitleInput.Raw;

	/// <summary>The dev menu's tabs on the title's guest state, in the menu's guest slot.</summary>
	public virtual System.Collections.Generic.IEnumerable<Refii.Game.GameDevTab> GuestDevTabs() =>
		System.Array.Empty<Refii.Game.GameDevTab>();

	/// <summary>The guest's own controller-vibration switch, or null when the title states none.</summary>
	public virtual bool? GuestVibrationEnabled => null;

	// ---------------------------------------------------------------- the session's players

	/// <summary>Two players share this machine's screen.</summary>
	public virtual bool LocalCoop => false;

	/// <summary>
	/// Whether this title's bridge states the guest's entities (the entity walk). A title that does
	/// not yet has no first entity pass to wait for, so the world's load hold does not wait on one.
	/// </summary>
	public virtual bool StatesEntities => true;

	/// <summary>This machine joined another's game online.</summary>
	public virtual bool OnlineClient => false;

	/// <summary>An online partner has joined this machine's game or the one it joined.</summary>
	public virtual bool PeerJoined => false;

	// ---------------------------------------------------------------- the title's interactions

	/// <summary>
	/// A title interaction (a menu it stands over the world, an object the player is using) has
	/// the pad: the guest sees no buttons.
	/// </summary>
	public virtual bool InteractionOwnsPad => false;

	/// <summary>A title interaction has every device: the mods' input and the keyboard's labels too.</summary>
	public virtual bool InteractionOwnsDevices => false;

	/// <summary>The keycap for a pad button while <see cref="InteractionOwnsDevices"/>, or null.</summary>
	public virtual string? KeyLabel(string button) => null;

	/// <summary>
	/// Which pad button one of the title's own GUI textures draws, and how it layers, for the glyph
	/// swap (<see cref="Refii.Ui.Engine_UiKeyGlyphs"/>); null where the path is not button art the
	/// title names — the shared table (Fable 2's `art/gui/controller/…`) is asked after.
	/// </summary>
	public virtual Refii.Ui.Engine_UiKeyGlyphs.GlyphArt? GlyphArtFor(string path) => null;

	/// <summary>
	/// What the game's pad button does, for its Controls remap row ("Melee, block, flourish" for
	/// Fable 2's X); null where the title has not said, and the row shows the button alone.
	/// </summary>
	public virtual string? PadButtonRole(Refii.Core.PadInput button) => null;

	/// <summary>Whether the world is open to the player's own interactions (photo mode opens only then).</summary>
	public virtual bool WorldInteractionAllowed => true;

	/// <summary>A title interaction places the camera this frame; true when it did.</summary>
	public virtual bool ApplyCamera(Godot.Camera3D camera) => false;

	/// <summary>A title board is on screen and takes the pad itself (Fable 2's pub-game table).</summary>
	public virtual bool InteractionBoardUp => false;

	/// <summary>A title dialog is absorbing the input (a pub game's own prompt).</summary>
	public virtual bool InteractionDialogUp => false;

	/// <summary>The system's Back reached a title panel that owns the screen: the title closes it.</summary>
	public virtual void RequestBack()
	{
	}

	// ---------------------------------------------------------------- the entity feed

	/// <summary>
	/// What the title's rigs are to the shared animation and entity code: its bones by role, its
	/// bone naming, its face, crossfade and pose-check rules (<see cref="Refii.Animations.Engine_TitleRig"/>).
	/// </summary>
	public virtual Refii.Animations.Engine_TitleRig Rig => Refii.Animations.Engine_TitleRig.None;

	/// <summary>Bumped when the title changes the hero's look for a preview, so its body is rebuilt.</summary>
	public virtual int HeroMorphPreviewGeneration => 0;

	/// <summary>The title is holding the hero's look in preview (a mirror the player stands at).</summary>
	public virtual bool HeroMorphPreviewHeld => false;

	/// <summary>The guest player (1 or 2) whose body <see cref="HeroMorphPreviewGeneration"/> rebuilds; 0 the local hero.</summary>
	public virtual int HeroMorphPreviewPlayer => 0;

	/// <summary>Player <paramref name="player"/>'s skin tan the title states for its composites, 0..1; 0.5 is the guest's own.</summary>
	public virtual float ManualTan(int player) => 0.5f;

	/// <summary>The hero's companion's entity address (Fable 2's dog), or 0.</summary>
	public virtual uint CompanionEntityAddress => 0u;

	/// <summary>The title's region map as a mod reads it (<c>RefiiHost.minimap</c>); empty with none.</summary>
	public virtual Godot.Collections.Dictionary ModMinimap(uint hero) => new();

	/// <summary>A guest world position on that map, in the same units; null with no map.</summary>
	public virtual Godot.Vector2? ModMinimapPoint(Godot.Vector3 world) => null;

	/// <summary>What player 1's or 2's hero carries, as a mod's screen reads it (<c>RefiiHost.inventory_items</c>); empty with none.</summary>
	public virtual Godot.Collections.Array ModInventoryItems(int player) => new();

	/// <summary>The pump's action for what a mod's screen asks of an item (<c>RefiiHost.inventory_action</c>): <paramref name="player"/> 0 for the hero whose menus are open, 1 or 2 for another local hero; null when it is not one.</summary>
	public virtual string? ModInventoryAction(string operation, uint record, int category, int player) => null;


	/// <summary>
	/// The building entity a player (1 or 2) stands in, as the guest's own per-frame query found
	/// it (0: none); null when the guest gave no answer this frame, so the caller holds the last.
	/// </summary>
	public virtual uint? PlayerBuilding(int player) => null;

	/// <summary>The guest's stated targeting-highlight blur width, in texels of its half-resolution buffer; 0 when not stated.</summary>
	public virtual int HighlightWidthTexels => 0;

	/// <summary>
	/// A live entity as the title shows it: the guest's statement, or the title's own stand-in
	/// for one it is previewing (Fable 2's decorating ghost). The shell builds what comes back.
	/// </summary>
	public virtual Refii.Host.HostBridge.GuestEntity Appearance(Refii.Host.HostBridge.GuestEntity entity) => entity;

	/// <summary>False while <see cref="Appearance"/> would hand every entity back as it came, this walk.</summary>
	public virtual bool RestatesEntities => false;

	/// <summary>A live entity the title draws or answers itself; true when the shell should skip it.</summary>
	public virtual bool ObserveEntity(in Refii.Host.HostBridge.GuestEntity entity) => false;

	/// <summary>
	/// A live entity the title wants on a node of its own rather than in a batch (one it is moving
	/// or previewing), whatever its stated look says.
	/// </summary>
	public virtual bool WantsOwnNode(Refii.Host.HostBridge.GuestEntity entity) => false;

	/// <summary>The hero's entity, once per frame it is placed.</summary>
	public virtual void ObserveHero(Refii.Host.HostBridge.GuestEntity hero, uint frame)
	{
	}

	/// <summary>
	/// While the spell wheel is held, the title's own pick takes the X and Y buttons from the
	/// guest (a mod's secondary spell).
	/// </summary>
	public virtual bool WheelClaimsFaceButtons => false;

	/// <summary>
	/// Holds the world still the way the game's own pause does, with no screen up (photo mode), or
	/// lets it go. True when the title does it itself; false leaves the caller to withhold the
	/// session's steps, which a starved guest's clock outruns at about half speed.
	/// </summary>
	public virtual bool HoldWorld(bool held) => false;

	/// <summary>The mods' title-side declarations reach the game, once per guest and after a remount.</summary>
	public virtual void PumpMods(bool guestUp)
	{
	}

	// ---------------------------------------------------------------- assets and mods

	/// <summary>
	/// The mount is up, mods applied: the title adds what it serves into it (its own Lua, its
	/// text rewrites, its runtimes' scripts). Runs on every mount and remount.
	/// </summary>
	public virtual void OnAssetsMounted(Refii.Bnk.BnkFileSystem mounted)
	{
	}

	/// <summary>The mods changed: the title re-reads what it takes from them.</summary>
	public virtual void ReloadModData()
	{
	}

	/// <summary>
	/// The title's own loose data indexed into a mount being built: after the game's banks, its
	/// update and loose files, before the mods, so a mod can shadow it (Fable 2's pub games). With
	/// a null filesystem, the title's index alone, for loaders that read it directly.
	/// </summary>
	public virtual void IndexLooseData(Refii.Bnk.BnkFileSystem? filesystem, string gameDirectory)
	{
	}

	/// <summary>
	/// The mounted paths of the title's two shader banks (the named one and the release one) when
	/// it ships them inside its banks; null when they are loose files under <c>data/Shaders</c>.
	/// The shader compile reads them out of the mount into the title's unpacked workspace.
	/// </summary>
	public virtual (string Named, string Release)? PackedShaderBanks => null;

	/// <summary>The resource loaders for the title's own asset paths, put in once however often asked.</summary>
	public virtual void AddResourceLoaders()
	{
	}

	/// <summary>Takes out what <see cref="AddResourceLoaders"/> put in.</summary>
	public virtual void RemoveResourceLoaders()
	{
	}

	/// <summary>
	/// The editor is starting, on its main thread: the title's assets made loadable before the
	/// editor restores a scene that names them. Never throws.
	/// </summary>
	public virtual void PrepareEditor()
	{
	}

	/// <summary>What a mounted path is among the title's own assets, for the editor's viewers.</summary>
	public virtual TitleAssetKind AssetKind(string virtualPath) => TitleAssetKind.None;

	/// <summary>A title texture's pixels at a mounted path, or null when it is not one.</summary>
	public virtual Godot.Image? AssetImage(string virtualPath) => null;

	/// <summary>The kinds of asset a title serves under its own paths.</summary>
	public enum TitleAssetKind
	{
		None,
		Texture,
		Mesh,
		Font,
	}

	// ---------------------------------------------------------------- the level build

	/// <summary>A level's root is built: the title adds its own placed things to it.</summary>
	public virtual void DressLevel(Godot.Node3D root, Refii.Level.LevelEntry level)
	{
	}

	/// <summary>
	/// The editor saves the level: the title captures its own placed things from the edited root
	/// (Fable 2's Golden Cock and mirror) for the installed game.
	/// </summary>
	public virtual void CaptureLevelPlacements(Godot.Node3D levelRoot)
	{
	}

	/// <summary>Whether a level's vista draws an open ocean horizon, or null when the title states nothing.</summary>
	public virtual bool? AllowsVistaOcean(Refii.Level.LevelEntry level) => null;

	/// <summary>The weather the title fixes for a level, or null to leave it to the shell's rules.</summary>
	public virtual Refii.Level.DynamicWeatherRule? WeatherRule(Refii.Level.LevelEntry level) => null;

	/// <summary>What the title knows about its own placed buildings (<see cref="Refii.Level.Engine_TitleProps"/>).</summary>
	public virtual Refii.Level.Engine_TitleProps Props => Refii.Level.Engine_TitleProps.None;

	/// <summary>A saved entity the title places itself, so the level's save map skips it.</summary>
	public virtual bool IsTitleRecord(uint record) => false;

	/// <summary>
	/// A line off the mod event bus that is the title's own (a kind it owns), taken; false when
	/// it is not one and the shell carries on with it.
	/// </summary>
	public virtual bool HandleBusLine(int kind, string fields) => false;

	// ---------------------------------------------------------------- the UI's title answers

	/// <summary>The static population table a menu's `MenuPopulationScript` names, or null.</summary>
	public virtual Refii.Ui.Engine_UiPopulationTable? PopulationTable(string script) => null;

	/// <summary>A menu the title holds up in place of the screen's own (a dye being chosen), or null.</summary>
	public virtual string? HeldMenuScript => null;

	/// <summary>
	/// The menu a screen shows while the title holds one (<see cref="HeldMenuScript"/>), or null for
	/// the screen's own; on any other screen the title puts its held menu away.
	/// </summary>
	public virtual string? OverrideMenuScript(uint screenId) => null;

	/// <summary>A row's command that has nothing to unlearn (a mod's ability), so Y does nothing.</summary>
	public virtual bool IsModAbilityRow(string command) => false;

	/// <summary>The HUD elements the title's own views draw whole, by element name.</summary>
	public virtual System.Collections.Generic.IReadOnlyCollection<string> HudOwnedSubtrees => System.Array.Empty<string>();

	/// <summary>
	/// What the title's `hud.bgf` is to the shared HUD layout and view: its group table, placements,
	/// widgets and seat rules (<see cref="Refii.Ui.Engine_HudTitleLayout"/>).
	/// </summary>
	public virtual Refii.Ui.Engine_HudTitleLayout HudLayout => Refii.Ui.Engine_HudTitleLayout.None;

	/// <summary>
	/// The guest's display and menu boxes as the title states them open, for the shared box
	/// surfaces (<see cref="Refii.Ui.Engine_UiStatedBoxes"/>).
	/// </summary>
	public virtual Refii.Ui.Engine_UiStatedBoxes StatedBoxes => Refii.Ui.Engine_UiStatedBoxes.None;

	/// <summary>The HUD's pip anchors, in resolve order (Fable 2's expression meter: hero, henchman, both).</summary>
	public virtual System.Collections.Generic.IReadOnlyList<string> HudPipBindings => System.Array.Empty<string>();

	/// <summary>
	/// The title's guard on a front-end entry that opens a screen: whether it guards
	/// <paramref name="item"/>, and the refusal's text tag when it refuses (null: the screen opens).
	/// </summary>
	public virtual (bool Guards, string? Refusal) FrontEndOpenGuard(Refii.Ui.Engine_FrontEndMenu menu,
		Refii.Ui.Engine_UiExpandableMenu.Item item) => (false, null);

	/// <summary>Whether a presentation is one of the title's front-end screens (drawn by its own front end).</summary>
	public virtual bool IsFrontEndPresentation(string presentation) => false;

	/// <summary>The movie a screen's detail page plays for a row, or empty.</summary>
	public virtual string ScreenMovie(uint screenId, Refii.Ui.Engine_UiExpandableMenu.Item? row,
		Refii.Host.HostBridge.HeroVitals vitals) => string.Empty;

	/// <summary>The title's menu action that uses an inventory record (a mod's use_item), or null.</summary>
	public virtual string? UseItemAction(uint record) => null;

	/// <summary>The folder of a named save under a save root, as the title lays its saves out; null when it has none.</summary>
	public virtual string? FindSaveDirectory(string saveRoot, string save) => null;

	/// <summary>
	/// The guest's own files in a save's folder — what a console or Xenia reads, and all a copy for
	/// one carries (<see cref="Refii.Save.SaveExport"/>); empty when the title names none.
	/// </summary>
	public virtual System.Collections.Generic.IReadOnlyList<string> SaveGuestFiles => System.Array.Empty<string>();

	/// <summary>The title's string book in the running culture, for shared consumers resolving a Babel tag; null before it is read.</summary>
	public virtual Refii.Text.Engine_BabelDatabase? Strings => null;

	/// <summary>
	/// A mod marker's icon names (<c>quest</c>, <c>gold</c>, <c>follow</c>…) to this title's own textures;
	/// null keeps the shared table (<see cref="Refii.Ui.Engine_HudModMarkers.IconAliases"/>, Fable 2's art).
	/// </summary>
	public virtual System.Collections.Generic.IReadOnlyDictionary<string, string>? MarkerIconAliases => null;

	/// <summary>The player whose menus are open on this machine (0 when the title has no such notion).</summary>
	public virtual int MenuPlayer => 0;

	/// <summary>The title's options-screen cursor the host options page rides: its row.</summary>
	public virtual int OptionsCursorRow => 0;

	/// <summary>The value index the title's options cursor holds at a row.</summary>
	public virtual int OptionsCursorIndex(int row) => 0;

	/// <summary>Moves one row of the open host options page to <paramref name="index"/> (a rebind row's captured press).</summary>
	public virtual void SetOptionsCursorIndex(int row, int index)
	{
	}

	/// <summary>The host options page takes the title's options cursor.</summary>
	public virtual void EnterHostOptions(Refii.Ui.Engine_UiHostOptionsPage page)
	{
	}

	/// <summary>Each session step, after the guest stepped.</summary>
	public virtual void Step()
	{
	}

	/// <summary>
	/// The title's Modding Guide cards for a page (<see cref="Refii.Mods.ModGuidePages"/>) —
	/// <paramref name="closing"/>: the ones after its widgets — asked of each title with hard mods.
	/// </summary>
	public virtual System.Collections.Generic.IReadOnlyList<Refii.Mods.ModGuideCard> ModGuideCards(string page, bool closing) =>
		System.Array.Empty<Refii.Mods.ModGuideCard>();

	/// <summary>
	/// A mod manager outside ours whose mods the title's saves can carry, or null for none.
	/// </summary>
	public virtual Refii.Mods.Engine_ExternalModManager? ExternalMods => null;

	/// <summary>
	/// Puts the title's game-level pieces into the shell's registries — its screen providers, its
	/// verbs, its menu sources. Called once, before the first registry answers anything.
	/// </summary>
	protected virtual void Install()
	{
	}

	private static Engine_Title? _installed;
	private static readonly object InstallLock = new();

	/// <summary>
	/// Installs the active title's game-level pieces if they are not in yet. Every shell registry a
	/// title fills calls this before answering, so nothing is ever read before the title is in.
	/// </summary>
	public static void EnsureInstalled()
	{
		lock (InstallLock)
		{
			if (_installed == Engine_Title.Active)
			{
				return;
			}

			// Marked first: an install that registers into a registry re-enters here.
			_installed = Engine_Title.Active;
			Active.Install();
		}
	}

	/// <summary>A title with no game-level code of its own: the shell runs it as it is.</summary>
	private sealed class Bare : Engine_TitleGame
	{
		public Bare(Engine_Title title) => Title = title;

		public override Engine_Title Title { get; }
	}
}
