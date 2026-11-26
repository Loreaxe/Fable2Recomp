namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Godot;
using Refii.Ui;

/// <summary>
/// Fable 3's in-game HUD on the SHARED engine: one presentation host over the title's banks and
/// one <see cref="Fable3HudDocument"/> per presentation the HUD draws — `hud.bgf` (the widgets
/// below), `interactionmode.bgf` (the interaction mode), the end screens
/// (<see cref="Fable3EndScreens"/>) and the box screens' frames (<see cref="Fable3ScreenFrames"/>) — each on the shared
/// <see cref="Engine_HudView"/>, driven by what the guest states. The same view, layout, art and
/// resolve draw Fable 2's HUD; this is the title's feed into them.
/// </summary>
/// <remarks>
/// THE HUD'S VISIBILITY IS ITS ROOT'S SLIDE, as Fable 2's is: hud.bgf's `Scene` is 1 shown by
/// sub_825C1D28, 2 hidden by sub_825C1CB0 (every box's Show) and 3 for an ambient cutscene by the
/// cutscene rules sub_8249A030 (bit 27, 0x8249B6DC) — `@Hud.Root`, screen_stated_hooks.cpp — and
/// the root's authored slides (`PauseMenuToGame`, `GameToPauseMenu`, `ACS`) fade the widgets
/// beneath, so no host gate. (sub_825C3A78 is the interaction mode's tick: it writes
/// InteractionMode.bgf's root, IM+0, never this one.)
///
/// THE WIDGETS ARE CUT WHOLE (<see cref="Groups"/>): each component is one piece following the
/// guest's state for every element beneath it, placed by where its art sits so an edge widget
/// stays on its edge at any aspect.
/// </remarks>
public partial class Fable3Hud : Fable3Canvas
{
	/// <summary>
	/// The widgets of `hud.bgf` cut whole, by component name under `Layer` (or the container
	/// named). Every component under `Layer` with art is here or in <see cref="Owned"/>; a new one
	/// is reported at build (`[fable3-hud] not cut`).
	/// </summary>
	public static readonly Engine_HudLayout.GroupSubtree[] Groups =
	{
		// The quest / info box `TopBoxRefined.TopBox`: band, title, body, hold buttons. Cut as the
		// shared view's `group.QuestBox.TopBox`, so its rows — the texts bound TopTitle / TopMain and
		// the button row — are placed by Engine_UiTopBoxLayout from the text Godot draws, as Fable
		// 2's are. The first line stands where the file's box fit puts it (sub_82920610: the
		// `BoxName 'TopBox'` chain, Engine_UiTextBoxFit) and Godot stacks the rest from the text it
		// draws. The lines' texts are the text registry's writes, else the lines the box's caller
		// passed (sub_82920BC8).
		new("TopBox", "QuestBox", null, 0f, true, "TopBoxRefined"),
		new("Subtitles", "Subtitles", null, 0f, true),
		// The counters and timers: the counter tick sub_825E7C28 toggles `Counter%d` (registry 9,
		// 0x825e7c74) and lays the joined text into the `Corner` box (0x825e7cdc).
		new("CornerBox", "Counters", null, 0f, true),
		// The d-pad and player two's: `dpad.dpad.dpadmenu`'s slide (2 open, 1 closed), each slot's
		// icon, count and flash as its writers state them (sub_825D8B30, sub_825D8988 — the
		// `@Hud.Dpad.*` statements, Fable3HudRegistry); DP*Counter's slides through the SlideSet writer.
		new("dpad", "Dpad", null, 0f, true),
		new("dpad2", "Dpad2", null, 0f, true),
		new("ReceivedItems", "ReceivedItems", null, 0f, true),
		new("MoneyAlert", "MoneyAlert", null, 0f, true),
		new("MoneyAlertSecondPlayer", "MoneyAlertSecondPlayer", null, 0f, true),
		new("FollowerCollection", "FollowerCollection", null, 0f, true),
		new("FollowerCollection2", "FollowerCollection2", null, 0f, true),
		new("ObjectiveList", "ObjectiveList", null, 0f, true),
		new("RelationshipBar", "RelationshipBar", null, 0f, true),
		new("TargetNames", "TargetNames", null, 0f, true),
		new("JudgementConsequences", "JudgementConsequences", null, 0f, true),
		new("CrimeTimer", "CrimeTimer", null, 0f, true),
		new("JobMultiplier", "JobMultiplier", null, 0.5f, true),
		new("CrucibleMeter", "CrucibleMeter", null, 0f, true),
		new("ShardHealth", "ShardHealth", null, 0f, true),
		new("LookAt", "LookAt", null, 0f, true),
		// The screen-edge damage frame, scrubbed by its root's `LowHealth` meter.
		new("DamageTaken", "DamageTaken", null, -0.5f, true),
		new("AbilitiesBox", "AbilitiesBox", null, 0f, true, "ExperianceDisplay"),
	};

	/// <summary>
	/// `hud.bgf` widgets another Fable 3 surface draws (<see cref="Fable3Title.HudOwnedSubtrees"/>):
	/// the pooled world icons and the hold-fill prompt, which the guest places in world space
	/// every frame (sub_82B51B58) and <see cref="Fable3WorldIcons"/> draws from the icon feed
	/// through the shared Engine_HudWorldSprites; and the co-op partners' markers (types 52/53 on
	/// the records 0x832A1D70 / 0x832A1DB0 sub_82B522A0 builds from these elements), which
	/// <see cref="Fable3WorldIcons"/> places from their stated icons and clamps to the screen edge.
	/// </summary>
	public static readonly string[] Owned =
		{ "WorldIcons", "FillWorldIcon", "HeroOffScreenWorldIcon", "HenchmanOffScreenWorldIcon" };

	/// <summary>
	/// The interaction mode's widgets in `interactionmode.bgf`, each cut whole and drawn from what
	/// its writers were CALLED with alone (as every Fable3HudDocument is):
	/// the root's slide from the mode's tick and clear and its active flag (interaction_hooks.cpp),
	/// the list's slide from its path setter, the bars' from their show, every text, texture and
	/// visibility at its writer's entry. The buttons, `LowerElements.InteractionMenuRight`: every interaction class hides
	/// Button1..4 (sub_82819AE0(button, pres, 0), e.g. sub_829651F8 at 0x82965324, sub_82B23C58 at 0x82B23D78) and shows the ones it offers through
	/// sub_82961340 — opacity 100 enabled / 50 not (0x8296139C), the `Title` label (0x829613E4),
	/// the glyph's `_text`/`_orb` textures (0x82961464, sub_829614A0 at 0x82961644), shown
	/// (0x82961494): `@Interaction.Button`, the glyphs `@Ui.Texture`, the hides `@Ui.Visible`.
	/// </summary>
	public static readonly Engine_HudLayout.GroupSubtree[] InteractionGroups =
	{
		new("InteractionMenuRight", "InteractionButtons", null, 0f, true, "LowerElements"),

		// The option list (`LowerElements.InteractionMenuScrollableList`) is not cut here: Godot draws it
		// itself, in the PC arrangement, from the same statements (Fable3InteractionList). The document's
		// playback still runs its component, whose clock is the guest's scroll gate (StateReached).

		// The info box: one component whose slide is the panel, IM+64 — staged by sub_82963040
		// (0x8296307C), rested at 1 by the mode's clear (0x825C400C) and close (0x825C62F0), stated at
		// each by interaction_hooks.cpp (`@Interaction.Panel`, Fable3HudRegistry) — the value the
		// screen tick writes as `LowerElements.InfoBox`'s state (0x825C3B14) — 5 NPCInfo, 6 WeaponInfo,
		// 9 SaveGameInfo, 11 DogInfo, 1 closed; the
		// panels' texts are element writes beneath it (`NPCInfo.Contents.Name` …, the joined
		// interaction text `TextBox.Contents.Text1`, sub_82BEF428 at 0x82BEF5B4), as are the map's
		// `WorldMapRegion` and the shops' `BuildingInfo`.
		new("InfoBox", "InteractionInfo", null, 0f, true, "LowerElements"),

		// The trigger and bumper bars: `TriggerButtons` is the mode's IM+36 (vtable 0x820D1D50,
		// sub_82B19150: `.Triggers.LT/RT_Complete`, stored 0x825C4100) and `BumperButtons` its IM+32
		// (vtable 0x820D1CD8, sub_82B18EF0: `.Buttons.LB/RB_Complete`, stored 0x825C40AC) — label
		// text (sub_82B3EB28, commit 0x82B3EB58: `@Ui.Text`), the sides' `_Complete` shown
		// (sub_82B3EB60 at 0x82B3EB9C: `@Ui.Visible`), root slide 1 hide / 2 TopBoxOn / 3 TopBoxOff
		// (sub_82B3EA18 at 0x82B3EAF0: `@Ui.State`), and while shown 2 / 3 again as a TopBox message goes
		// up / the channels empty (sub_825C6D78 at 0x825C6DD4 / 0x825C6E18: `@Ui.State`). The
		// GUI.*Triggers* natives drive IM+32 =
		// BumperButtons and the *Bumpers* ones IM+36 = TriggerButtons (0x8251DC98).
		new("TriggerButtons", "InteractionTriggers", null, 0f, true, "UpperElements"),
		new("BumperButtons", "InteractionBumpers", null, 0f, true, "UpperElements"),

		// The dye shop's bar: `ShowDyingBar(r, g, b, value, [entity], [min], [max])` (0x8251F2C0 ->
		// sub_8295C940) toggles the `DyeingBar` slide-toggle (sub_825E4120 -> sub_82921200 ->
		// sub_8227DA00 at 0x8281A4AC) and sets the `DyeingBar` meter to (value - min) / (max - min)
		// (sub_825E3F28 -> sub_8291F988), which the meter tick states through sub_8281A5D8
		// (0x8291F738); `HideDyingBar` toggles it off (sub_8295CA98). One unit, so the `Arrows`
		// meter stays on its hue bar.
		new("DyeingBar", "DyeingBar", null, 0f, true, "UpperElements"),

		// The weapon side panel: `ShowWeaponPanel` (sub_8295C500 kind 1) toggles
		// `WeaponPanelToggle` on through the same toggle registry and fills `WeaponsInformation`
		// (sub_829590A8: registry slides, texts and meters); Clothes / Augment / HideSideInfoPanel
		// toggle it off.
		new("WeaponInfoPanel", "WeaponPanel", null, 0f, true, "MiddleElements"),

		// The relationship / podium mini-game bar (`RelationshipHero.Start` -> sub_825C5A60): every
		// write on it is `minigamebarcontrol.lua`'s — slide 2 Show / 1 Hide, the segments, the pip,
		// the HitHighlights slide — reproduced from the guest's chunks and parameters
		// (Fable3MiniGameBar, Fable3LuaContracts); the pip icons and counters are the C++'s writes.
		new("Bar", "MiniGameBar", null, 0f, true, "Layer"),
	};

	private Fable3Presentations? _host;
	private bool _stood;
	private static Fable3Hud? _standing;
	private Engine_UiScreenFade _fade = null!;
	private readonly List<Fable3HudDocument> _documents = new();

	protected override void Build()
	{
		_fade = new Engine_UiScreenFade { Name = "ScreenFade" };
		AddChild(_fade);

		_documents.Add(new Fable3HudDocument(Fable3Elements.HudDocument, Groups, layouts: true));
		_documents.Add(new Fable3HudDocument(Fable3Elements.InteractionDocument, InteractionGroups, layouts: false));
		_documents.AddRange(Fable3EndScreens.Documents());
		_documents.AddRange(Fable3ScreenFrames.Documents());

		_host = new Fable3Presentations { Name = "Presentations", LoadsInBackground = true };
		_host.Loaded += Stand;
		Root.AddChild(_host);
		_standing = this;
	}

	public override void _ExitTree()
	{
		if (_standing == this)
		{
			_standing = null;
		}
	}

	/// <summary>
	/// A document's pattern book — its art by element name, for a Fable 3 surface that borrows a
	/// piece of another document (<see cref="Fable3ListCursor"/>) — or null until the banks are
	/// indexed and the documents stand.
	/// </summary>
	public static Engine_UiTheme? Book(string document) =>
		_standing is { _stood: true, _host: { } host } ? host.PatternBookFor(document) : null;

	/// <summary>Builds every document's view once the banks are indexed.</summary>
	private void Stand()
	{
		if (_host is null)
		{
			return;
		}

		_stood = true;
		foreach (Fable3HudDocument document in _documents)
		{
			if (document.View is null && document.Stand(_host, Root) && document.Document == "hud" &&
				_host.DocumentFor(document.Document) is { } screen)
			{
				ReportUncut(screen);
				Fable3WorldIcons.UseHud(screen);
			}
		}
	}

	protected override void Frame(double delta)
	{
		Engine_UiSceneBuilder.SetViewportActive(
			CanvasViewport,
			!Refii.Game.GameWorld.CoverHeld && !Refii.Game.GameVideo.PlayedWithin(2) && !Fable3Splash.Showing);
		_fade.Drive();
		Vector2 window = GetViewportRect().Size;
		foreach (Fable3HudDocument document in _documents)
		{
			document.Frame(window);
			Fable3EndScreens.Gate(document);
			Fable3ScreenFrames.Gate(document);
		}
	}

	/// <summary>Names every component under `Layer` with art that is neither cut nor owned: a widget the file added.</summary>
	private static void ReportUncut(Engine_UiScreen screen)
	{
		var named = new HashSet<string>(System.StringComparer.Ordinal);
		foreach (Engine_HudLayout.GroupSubtree group in Groups)
		{
			named.Add(group.Name);
			// A group cut beneath a component (`TopBoxRefined.TopBox`) stands for that component.
			if (group.Parent is { } holder)
			{
				named.Add(holder);
			}
		}

		named.UnionWith(Owned);
		var uncut = new List<string>();
		foreach (UiNode node in screen.Nodes)
		{
			if (node.Parent?.Name != Fable3HudStatements.LayerSegment || node.Type != BgfElementType.Component ||
				named.Contains(node.Name) || !HasArt(node))
			{
				continue;
			}

			uncut.Add(node.Name);
		}

		if (uncut.Count != 0)
		{
			GD.Print($"[fable3-hud] not cut (drawn by no piece): {string.Join(", ", uncut)}");
		}
	}

	private static bool HasArt(UiNode node)
	{
		if (node.Quad is not null || node.Text is not null)
		{
			return true;
		}

		foreach (UiNode child in node.Children)
		{
			if (HasArt(child))
			{
				return true;
			}
		}

		return false;
	}
}
