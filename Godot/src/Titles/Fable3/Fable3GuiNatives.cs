namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Godot;
using Refii.Ui;

/// <summary>
/// The UI cut's routing table for Fable 3: what Godot does with each `GUI.*` native its game VM
/// calls (the seam: Fable3/src/hooks/src/gui_natives.cpp posts, the shared core drains,
/// <see cref="Engine_UiGuiNatives"/> dispatches).
/// </summary>
/// <remarks>
/// A call arrives with the name the script used and its arguments, BEFORE the guest's own
/// implementation runs; taking it does not suppress it, so every predicate the scripts read
/// back still answers out of the guest's own state. The boxes Godot answers are the exception, by
/// statement rather than by native: their pad is Godot's (display_box_hooks.cpp).
///
/// Three answers, as Fable 2's table has them: <c>Register</c> (Godot draws the surface it
/// affects), <c>Observe</c> (deliberately not owned here, with the reason — the census counts
/// these apart from the unrouted), neither (the census reports it with its arguments, so the
/// table is built from what the running game calls).
///
/// Argument shapes are decompiled facts of the Fable 3 IDB (disc build), per native below. The
/// text argument of every box native is resolved by one helper, <c>sub_82516418(out, L, first)</c>:
/// the tag as given through the text manager, then with a <c>TEXT_</c> prefix, else the literal;
/// each following argument substitutes <c>%N</c>, numbers as integers (sub_82300020), strings
/// resolved the same way — the shape the counter and timer routes below resolve by.
/// </remarks>
public partial class Fable3GuiNatives : Node
{
	/// <summary>The box the message natives name — `unk_835582B8`, "Unnamed", as Fable 2's.</summary>
	private const string UnnamedBox = Engine_UiGuiBoxes.MessageBoxName;

	private readonly Engine_UiGuiNatives _router = new();

	/// <summary>The boxes, counters and menu box the natives put up; <see cref="Fable3Boxes"/> draws it.</summary>
	public Engine_UiGuiBoxes Boxes { get; } = new();

	public override void _Ready() => Route();

	public override void _Process(double delta)
	{
		_router.Drive();
		Boxes.Tick(delta);
	}

	private void Route()
	{
		// The Gameface element writes (gui_element_hooks.cpp, ui_stated_hooks.cpp), by document and path.
		Fable3Elements.Register(_router);
		// The surfaces' values by the calls that carry them (ui_stated_hooks.cpp, topbox_hooks.cpp,
		// screen_stated_hooks.cpp) and the bar and objective natives below.
		Fable3UiStatements.Register(_router);
		Fable3ScreenStatements.Register(_router);
		// The HUD's registry writers, d-pad and interaction panel (hud_hooks.cpp, interaction_hooks.cpp).
		Fable3HudRegistry.Register(_router);
		Fable3EndScreens.Register(_router);
		Fable3Reticles.Register(_router);

		// ---- THE BOXES: MADE, SHOWN, DROPPED AND CLOSED (display_box_hooks.cpp) -------------
		//
		// The natives only QUEUE a box; many are queued by C++ and never pass a native. Every box
		// goes through the queue's push sub_825FBBF8, where its creator's fields are complete
		// (@Box.Queued); the queue's tick shows the front one (@Box.Show) and releases it when its
		// Update answers 0 — lifetime, Hide or RemoveDisplayBox alike (@Box.Close); a pending box
		// removed before it showed is @Box.Dropped. The pause screen's message box (sub_825CEEB0)
		// is stated the same way under queue -1. Godot owns the pad of the boxes it answers
		// (<see cref="Fable3BoxAnswers.Owns"/>): the guest's handlers do nothing, and the answer
		// goes back as a tick action to the box's own answer function.
		_router.Register("@Box.Queued", BoxQueued);
		_router.Register("@Box.Show", call => BoxShown(Parse(call.Argument(1).Text)));
		_router.Register("@Box.Dropped", call => _queued.Remove(Parse(call.Argument(0).Text)));
		_router.Register("@Box.Close", call => BoxClosed(Parse(call.Argument(1).Text)));

		// The screen manager SM = *(GUI+0x1C) destroyed (TU3 sub_82334A48, disc sub_8232D740; the GUI
		// shutdown, Save and Quit and the quit to the front end): it frees the box manager +0x30 with
		// both queues, the menu box +0x34 and the save menu +0x38 without closing any of them.
		_router.Register("@Screens.Destroyed", _ => ScreensDestroyed());

		// ---- THE OPTIONS SCREEN: its handler's state as it returns (options_hooks.cpp) ----------
		_router.Register("@Options.State", call => Fable3OptionsState.Stated(call));

		const string queued = "queues a box; it is stated at the queue's push (@Box.Queued), as the queue shows it (@Box.Show) and as it drops it (@Box.Close)";
		foreach (string name in new[] { "DisplayMessageBox", "DisplayYesNoQuestion", "DisplayYesNoQuestionWithTextOverride", "DisplayQuestPrimaryQuestion", "DisplayNumericAdjusterParams", "DisplayQuestBoxParams", "DisplayButtonBox", "DisplayFoodItemQueryBox", "DisplayNatalQueryBox", "DisplayDivorceMessage", "DisplayIntermissionScreen", "DisplayIntermissionScreenWithLifetime", "DisplayItemNameWithCount", "DisplayNameFromItem", "RemoveDisplayBox", "RemoveAllDisplayBoxesWithName", "DisplayDownloadMessageBox", "DisplaySaveGameIncompatibilityMessageBox", "DisplayNonInteractiveMessageBox" })
		{
			_router.Observe(name, queued);
		}

		// ---- COUNTERS AND TIMERS: the registry at `*(H+0x48)` ---------------------------------
		//
		// `SetCounter(name, tag, first, second, flag)` (0x8251AA38) files a record into the counter
		// registry (sub_825E7860(H+72, FNV1(name), tag, (int)first, (int)second, flag)); the timers
		// (`SetTimer` 0x8251AF38, `SetTimerWithText` 0x8251B158 -> sub_825E7AF8, the count-up twin
		// 0x8251B180) file timed ones; `RemoveCounter`/`RemoveTimer` are one body (0x8251B1A8). The
		// registry's tick sub_825E7C28 states them as elements: each record's `Counter%d` toggle
		// (registry 9, 0x825e7c74), the joined text laid into the `Corner` box (0x825e7cdc) and the
		// `Corner` fader (0x825e7d10) — hud.bgf's `CornerBox`, drawn by the shared HUD view (Fable3Hud).
		const string counters = "files the counter registry *(H+72); its tick writes the CornerBox elements the shared HUD view draws";
		foreach (string name in new[] { "SetCounter", "SetTimer", "SetTimerWithText", "SetCountUpTimerWithText", "PauseTimer", "PlayTimer", "RemoveCounter", "RemoveTimer", "DisplayCornerBoxTradeItemInfo", "RemoveCornerBoxTradeItemInfo" })
		{
			_router.Observe(name, counters);
		}

		_router.Observe("RemoveCounterIfFastTravelOrChangeLevel",
			"marks the counter for removal on the guest's next travel; the removal itself is the guest's");

		// ---- THE MENU BOX: `*(SM+0x34)`, stated by its row adds, its open and its close -----------
		//
		// sub_825FA220 opens it with the rows its filler added (sub_825F9E68 / sub_825F9FC0), the
		// title it resolved and whether B cancels; sub_825FA420 closes it and posts
		// MESSAGE_EVENT_MENUBOX (65) with the 1-based row or 0. The highlight is Godot's: the pick
		// goes back as `menubox:<p>:<row>` to the guest's own highlight and select.
		_router.Register("@MenuBox.Open", MenuBoxOpened);
		_router.Register("@MenuBox.Close", _ => Boxes.CloseMenuBox());
		_router.Observe("DisplayMenuBox", "fills the menu box's rows; stated when it opens (@MenuBox.Open)");
		_router.Observe("DisplayUncancelableMenuBox", "fills the menu box's rows; stated when it opens (@MenuBox.Open)");

		// ---- STATED ELSEWHERE: drawn from the guest's own state, not from the call --------------
		//
		// Fable 3's info boxes are TOPBOX messages: `DisplayInfoBoxEx(name, seconds, tag, …)`
		// (0x82518168) builds sub_824B4840(msg, FNV1(name), "", seconds*1000) and posts it on
		// channel 3 of the TopBox manager `*(H+0x4C)`; `DisplayInfoBoxParams` with a non-zero
		// DisplayBoxStyle posts channel 1/2/3 by style. The manager's publisher sub_8217D880 writes
		// the box's elements (texts through the `TopBox` box layout, toggles, slide sets) — hud.bgf's
		// `TopBoxRefined`, drawn by the shared HUD view (Fable3Hud).
		const string topBox = "a TopBox channel message — its publisher writes the TopBoxRefined elements the shared HUD view draws";
		foreach (string name in new[] { "DisplayInfoBox", "DisplayInfoBoxEx", "DisplayInfoBoxParams", "ShowTopBoxMessage", "TurnOffTopBox", "ClearTopBox", "SetTopBoxHidden", "HideInfoBox" })
		{
			_router.Observe(name, topBox);
		}

		// The fader `*(GUI+0x3C)`: every start ends in sub_825D5700, which states the colour and
		// seconds, and its advance sub_82287090 states the holds and the lock (hud_hooks.cpp); the
		// host runs the fade on its own clock between them (RefiiHostGetScreenFade) while the guest
		// polls IsScreenFully* on its own fader.
		const string fade = "the fade's start and advance are stated (hud_hooks.cpp) and run on the host's clock; the guest polls its own fader";
		foreach (string name in new[] { "FadeScreenIn", "FadeScreenOut", "FadeScreenOutToColour", "FadeScreenOutIfEntityIsVisible", "IsScreenFading", "IsScreenFullyFaded", "IsScreenFullyFadedIn", "IsScreenFadeLocked", "LockScreenFade", "UnlockScreenFade" })
		{
			_router.Observe(name, fade);
		}

		// The HUD element registries at `*(*(H+0x24)+0x18)`: the writers publish by name hash
		// through the text writer sub_82733F30 and the meters' value writer sub_8291F6E8, both
		// stated (hud_hooks.cpp) — the same channel the subtitler and the TopBox publisher write through.
		const string registry = "a HUD registry write, stated at the text and meter value writers (hud_hooks.cpp)";
		foreach (string name in new[] { "SetGUIText", "ToggleElement", "SetMeterRatioOverTime", "SetMeterPercentOverTime", "SetMeterPosition", "ResetMeterPosition", "FadeElementIn", "FadeElementOutFast", "FadeElementOutSlow" })
		{
			_router.Observe(name, registry);
		}

		// Movies: the Bink hooks state the open, rect and close (Fable3/src/hooks/src/movie_player.cpp).
		foreach (string name in new[] { "PlayMovieInternal", "PlaySilentMovieInternal", "PlayLocalisedMovieInternal", "PlayMovieWithGenderedVoiceInternal", "PlayLocalisedMovieWithGenderedVoiceInternal" })
		{
			_router.Observe(name, "the Bink player's open states the movie (movie_player.cpp)");
		}

		// The loading screen: level_feed.cpp states the screen, its fade and the art it picked.
		foreach (string name in new[] { "SetLoadingScreenBackgroundName", "SetLoadingScreenTextTagGroupName", "SetOpeningLoadingScreen" })
		{
			_router.Observe(name, "the loading screen object states itself (RefiiHostGetLoadingScreen / Art)");
		}

		// ---- THE INTERACTION MODE: element writes on interactionmode.bgf, drawn by the shared HUD view ---
		//
		// `OpenInteractionMode(table)` (0x8251D940 -> sub_825C4348(IM, 3, …, table)) and
		// `RefreshInteractionMode(table)` (0x8251DFF0) hand the mode its button slots and
		// `ListOfOptions`; the mode writes them itself: the buttons on `InteractionMenuRight`
		// (sub_82961340), the list as a three-row window on `InteractionMenuScrollableList`
		// (sub_82963BE0 at 0x8251E210 -> sub_82963DE0: the cursor row in Button3, 0x82963ED8, then
		// Button2 / Button1, 0x82963F5C / 0x82964050; scroll = the list root's state 3 / 4,
		// 0x82963D9C / 0x82964520), its texts on `LowerElements.InfoBox` (the joined rows commit to
		// `InfoBox.TextBox.Contents.Text1`). The guest answers A/B/X/Y itself (sub_8295A178).
		const string interaction = "the mode writes its buttons, list window and texts as interactionmode.bgf elements (sub_82961340, sub_82963DE0, the InfoBox commits), drawn by the shared HUD view";
		foreach (string name in new[] { "OpenInteractionMode", "RefreshInteractionMode", "RefreshInteractionModeForClient", "CloseInteractionMode", "CloseAnyNonCrimeRelatedInteractionModes" })
		{
			_router.Observe(name, interaction);
		}

		_router.Observe("AddInteractionModeText",
			"joined by the guest into the mode's one text box (sub_82958878), committed to LowerElements.InfoBox.TextBox.Contents.Text1 and drawn by the InfoBox group");
		_router.Observe("ClearInteractionModeText", "clears the same text box (sub_82957D00); its empty text is stated at sub_82BEF428 (@Ui.Text)");

		const string infoBox = "element writes under LowerElements.InfoBox (texts + the page state at 0x825C3B14), drawn by the shared HUD view";
		foreach (string name in new[] { "AddInteractionModeTextForItem", "AddInteractionModeTextForReceivedGift", "AddInteractionModeTitleAndDescription", "OpenInteractionTextWithTitleAndScrollableDescription" })
		{
			_router.Observe(name, infoBox);
		}

		// The trigger and bumper natives reach the bar classes (the *Triggers* calls IM+32 =
		// UpperElements.BumperButtons, 0x8251DC98; the *Bumpers* calls IM+36 = TriggerButtons), as
		// the GUIWorld table does; the classes' own writes are stated (interaction_hooks.cpp).
		const string bar = "the bar class states its slide (@Ui.State, vf0 sub_82B3EA18), its label (@Ui.Text, vf1 sub_82B3EB28) and its sides (@Ui.Visible) on interactionmode.bgf";
		foreach (string name in new[] { "ShowInteractionTriggers", "ShowInteractionBumpers", "SetInteractionTriggersText", "SetInteractionBumpersText", "SetInteractionTriggersVisibility", "SetInteractionBumpersVisibility" })
		{
			_router.Observe(name, bar);
		}

		_router.Observe("SetInteractionModeWorldIconPositions", "mode+309..312 pick the side (sub_82956068) of the world icons 66..69 the mode registers on R, whose Update states their anchor");
		_router.Observe("StartInteractionMiniGame", "the mode's mini-game (IM+40) fills the hero expression hold fraction the emotion ring reads live; rumble and speech are the guest's");
		_router.Observe("ExecuteInteractionScript", "a Lua chunk in the interactionmode presentation (sub_82819D40); no shipped script calls it");
		_router.Observe("OpenPodiumInteractionMenu", "interaction mode 4 (sub_82B23840): its buttons are InteractionMenuRight element writes the shared HUD view draws");
		Fable3Keyboard.Register(_router);
		_router.Observe("RenameEntityWithInteractionMode", "opens XamShowKeyboardUI (stated as @Keyboard.Open, answered by Fable3Keyboard); the rename is the guest's");

		// ---- THE WORLD ICONS AND THE EMOTION RING: stated by world_icon_hooks.cpp ---------------
		//
		// `AddWorldIcon` and friends only QUEUE a request on R+88; R's tick makes the icon, keeps
		// its lifetime, fades it by scale and expires it — so Fable3WorldIcons draws the icons the
		// tick states (Init, Update with its `visible`, erase; RefiiHostGetWorldSprites), not the calls.
		const string iconRegistry = "a request on the icon registry R (*(H+0x18)); the icons R's tick makes are stated (RefiiHostGetWorldSprites)";
		foreach (string name in new[] { "AddWorldIcon", "AddWorldIconWithOffset", "RemoveWorldIcon", "SetCanShowHintWorldIcons" })
		{
			_router.Observe(name, iconRegistry);
		}

		// `SetShouldDrawPlayerIcons(b)` (0x8251C0F0) writes the byte `*(H+80)+16` (0x8251C154); the
		// player markers (52/53) are drawn from their HUD element (Fable3Hud), so the call is the statement.
		_router.Observe("SetShouldDrawPlayerIcons", "writes *(H+80)+16; this call is its statement");

		// `Emotion.AddEmotion(table)` (0x825229F0 -> sub_825E9088) builds a record, which every
		// creator hands to the list add sub_825E8D30, and registers its type-77 icon on R;
		// `Emotion.Close(table)` (0x82522A28 -> sub_825E9EB0) erases the record, whose deleter
		// sub_825EBD70 removes the icon. Both are stated at those entry points
		// (RefiiHostGetFable3EmotionChoices); the table is kept for the census of the choice
		// vocabulary (Name, Text, RecordName, GameAction, Holdable, ManuallyIncreasePower).
		_router.Observe("Emotion.AddEmotion", "a record on E (*(H+0x1C)) and its icon on R, both stated (RefiiHostGetFable3EmotionChoices)");
		_router.Observe("Emotion.Close", "erases the record on E; its deleter (sub_825EBD70) states it gone and removes the icon");

		const string emotionIcon = "writes the type-77 icon on R (orbit/tilt/rumble land in icon+112 via sub_829797B8, texture in +144, FX through the hooked FX wrappers), stated at its Update and ring tick";
		foreach (string name in new[] { "Emotion.AddIcon", "Emotion.SetActualSpriteScreenSize", "Emotion.SetMajorRadius", "Emotion.SetManualPowerIncreaseSpeed", "Emotion.SetMinorRadius", "Emotion.SetOffset", "Emotion.SetParticle", "Emotion.SetParticleOffset", "Emotion.SetRumbleAmplitude", "Emotion.SetRumbleSpeed", "Emotion.SetSpeed", "Emotion.SetTexture", "Emotion.SetTiltXAngle", "Emotion.SetTiltYAngle", "Emotion.SetTiltZAngle" })
		{
			_router.Observe(name, emotionIcon);
		}

		_router.Observe("Emotion.SetCanAddSimEmotionIcons", "E+28; false erases every choice (sub_825E9B20), each deleter stating it gone");
		_router.Observe("Emotion.SetCanAddSimEmotionIconsNonNetworked", "E+29; false erases every choice (sub_825E9B20), each deleter stating it gone");

		// ---- THE GUEST'S OWN FLAGS, polled by its scripts ------------------------------------------
		//
		// `IsDPadEnabled()` (0x8251BE10 -> sub_825D7598 on the d-pad menu `*(H+48)`: bytes +40
		// enabled, +41/+42 suppressed) and `SetDPadEnabled(b)` (0x8251BDF0: `*(H+48)+40 = b`,
		// 0x8251BE00) are the d-pad menu's own state; what it shows reaches us as the
		// `dpad.dpad.dpadmenu` element's state (sub_825D8B30, `@Hud.Dpad.Slide`).
		_router.Observe("IsDPadEnabled", "the d-pad menu's own byte (*(H+48)+40); its visibility is stated as the dpad.dpad.dpadmenu element");
		_router.Observe("SetDPadEnabled", "writes *(H+48)+40; the d-pad menu states itself through its elements");

		// `SetJobMultiplierLevel(level)` (0x8251CD90 -> sub_82950B60 on `*(H+68)+20`): commits the
		// level text on the box of the object's text element `obj+4` (sub_82B45268 at 0x82950bec)
		// and plays slide 1 from off / 2 up / 3 down on its element `obj+0` (sub_8227DA00 at
		// 0x82950c28 / 0x82950c64 / 0x82950c9c) — `Layer.JobMultiplier` in the r7 census. Element
		// writes, drawn by the shared HUD view's `JobMultiplier` group (Fable3Hud).
		_router.Observe("SetJobMultiplierLevel", "writes the JobMultiplier element's text and slides; the shared HUD view draws them");

		// `IsCounterShown(name)` (0x8251ADE0): FNV-1 of the name against the counter registry
		// `*(H+72)` (entries +16 hash, +4 shown; 0x8251AE3C..0x8251AE70) — the registry SetCounter
		// files; the guest polls it for its own flow.
		_router.Observe("IsCounterShown", "polls the counter registry *(H+72); the answer stays the guest's");

		// ---- THE HUD WIDGETS: element writes through the hooked setters, or no visible effect ------
		_router.Observe("AddGUIldDpadShortcut", "files (icon, name) under d-pad button 36..39 on the mode-3 interaction (M3+728, 0x8251DE78); the d-pad refresh sub_825D7D98 states it as the slot icon (sub_82818EF0 at 0x825D8A3C)");
		_router.Observe("ClearGUIldDpadShortcut", "empties the slot's (icon, name) pair on M3+728; the next d-pad refresh restates the slot icon (0x825D8A3C)");
		_router.Observe("SetDPadCenter", "no visible effect: the setCenterOn/Off event (0x8251C0DC) has no listener in any shipped presentation and *(H+48)+43 (0x8251C0E4) has no reader");
		_router.Observe("SetHUDSuggestionMode", "writes the d-pad mode *(H+48) (0x8251C710); mode 2 slides dpad.dpad.dpadmenu in through sub_825D8B30 (sub_8227DA00 at 0x825D8BBC)");
		_router.Observe("ShowDyingBar", "toggles DyeingBar (sub_8281A490 at 0x8281A4AC) and sets its meter (sub_8291F6E8 at 0x8291F738); the hue tint (sub_82B42B68 at 0x8295D9EC) is unhooked");
		_router.Observe("HideDyingBar", "toggles DyeingBar off (sub_8295CA98 -> 0x8281A4AC)");
		_router.Observe("SetPixelsForDyeBarToMovePerTick", "sets the dye step M3+512; the dye tick sub_8295D958 restates the DyeingBar meter (0x8291F738)");
		_router.Observe("ShowGuildSeal", "seal vt[0] sub_825F7E58: GuildSealCollection<n> slide 2 through the slideset registry (0x8218D8E0)");
		_router.Observe("HideGuildSeal", "seal vt[1] sub_825F8020: GuildSealCollection<n> / GuildSealNumbers<n> slide 1 (0x8218D8E0)");
		_router.Observe("SetGuildSealCounterValue", "sets the seal's target count; its tick sub_825F8358 states the <n>_GSNum_<i> digits (sub_825F8BC0 -> 0x8218D8E0) and the fxgui_follower_* effects (particle wrappers)");
		_router.Observe("SetGuildSealFillPercent", "sets GuildSealFillMeter<n> (sub_825F7CD0 -> meter tick 0x8291F738)");
		_router.Observe("SetFollowerBarAlwaysOnInMap", "no visible effect: *(H+76)+56 (0x8251D6A0) is read only by the TopBox save sub_824B65B0");
		_router.Observe("SetFollowerBarSlide", "no visible effect: *(H+76)+52 (0x8251D688) is read only by the TopBox save sub_824B65B0");
		_router.Observe("ShowFollowerBarForXSeconds", "no visible effect: its meters MaskBar / RenownMarker1/2 (sub_824B6B78) name no shipped element");
		_router.Observe("SetHourGlassFillState", "no visible effect: *(H+88)+112 feeds meter HourGlassMask (sub_82917108), which names no shipped element");
		_router.Observe("SetShowHourGlass", "no visible effect: *(H+88)+117 gates meter HUDHourGlass (sub_829119D0), which names no shipped element");
		_router.Observe("ShowPlayerMoney", "money widget sub_8281E270: group opacity (0x82819BD8), amount text (0x8281E6E0), MoneyAlert meter (0x8291F738)");
		_router.Observe("StopShowingPlayerMoney", "money widget sub_8281EA00: MoneyAlert meter to 0 over 500 ms (0x8291F738)");
		_router.Observe("StopShowing2ndPlayerMoney", "second money widget sub_8281EA00: MoneyAlertSecondPlayer meter to 0 over 500 ms (0x8291F738)");
		_router.Observe("AddSubObjective", "adds a row to *(H+92); refresh sub_82953E68 states ObjectiveList.ObjectiveList (0x82953EAC), Bar<i>.Text (sub_828191A8 0x8281926C) and tickbox.tick (0x82953F64)");
		_router.Observe("SetSubObjectiveCompletion", "sets the row's done flag and refreshes (sub_82953DD0 -> sub_82953E68)");
		_router.Observe("RemoveSubObjective", "unlinks the row (--O+4 at 0x8251D7A8) without a refresh: the next sub_82953E68 draws it");
		_router.Observe("ClearSubObjectives", "empties the list (sub_822DFF08) and refreshes (0x8251D7F0): slide 1 (0x82953FC0)");
		_router.Observe("HideSubObjectives", "ObjectiveList.ObjectiveList slide 1 (sub_8281A428 at 0x8251D89C)");
		_router.Observe("ShowSubObjectives", "refreshes and re-arms the HUD tick's refresh (*(H+92)+12 = 3, 0x8251D8E0 / 0x82259AB0); each refresh's writes are stated at sub_8281A428 / sub_828191A8");
		_router.Observe("ShowWeaponPanel", "WeaponPanelToggle on (0x8281A4AC) and WeaponsInformation filled through the registries (sub_829590A8)");
		_router.Observe("ShowClothesPanel", "WeaponPanelToggle off; its MiddleElements.SideInfoPanel writes resolve nothing (no such element in interactionmode.bgf)");
		_router.Observe("ShowAugmentPanel", "WeaponPanelToggle off; its MiddleElements.SideInfoPanel writes resolve nothing (no such element in interactionmode.bgf)");
		_router.Observe("HideSideInfoPanel", "WeaponPanelToggle off (0x8251EFA4); MiddleElements.SideInfoPanel (0x8251EFE4) is no shipped element");
		_router.Observe("SetItemForInfoPanel", "stores the item on M3+432 (sub_8295C708); the panel fills read it");
		_router.Observe("TurnJobMultiplierOff", "JobMultiplier slide 4/5 (sub_8227DA00 at 0x8251CE48); the shared HUD view draws it");
		_router.Observe("ShowMiscGUIElements", "debug show-all: corner records, a TopBox info message, ExpAmounts and *(H+104) slides (0x8251FE68), all through hooked writers");
		_router.Observe("ShowAchievementProgess", "files '<name>: cur/max' as corner records (sub_825D1A10 -> sub_825E79D8); the counter tick lays them into CornerBox (0x825E7CDC)");
		_router.Observe("SetExperienceShouldShowAbilities", "no visible effect: writes **(H+84) (0x8251D290), which has no reader");
		_router.Observe("SetMiniGameCounter", "mini-game bar Counter<n> slide 2 (bl 0x826ECB94) and text (guard 0x826ECBCC) on interactionmode Bar, each stated at its call (@Ui.State / @Ui.Text)");
		_router.Observe("Create2dParticle", "spawns an effect through the hooked particle wrappers (Create 0x823AC02C, Start 0x823AC038, SetPosition sub_8318FB30): the FX feed draws it");

		// ---- CUTSCENE PRESENTATION: no shipped script calls these (gamescripts/_r/guiscripts banks) ----
		_router.Observe("PlayFullCutscene", "pushes game mode 7 (vt 0x820C115C, 0x8251BEA4): the GUI draw then paints two 100-line bars instead of the GUI (0x8225DD98); no shipped script calls it");
		_router.Observe("StopFullCutscene", "pops game mode 7 (sub_8240E820(…, 7) at 0x8251BFB8); no shipped script calls it");
		_router.Observe("SetInteractiveCutsceneBorder", "writes H+6 (0x8251C03C), which nothing in the binary reads; no shipped script calls it");
		_router.Observe("SetInteractiveCutsceneMode", "writes H+5 (0x8251C024): a debug label (0x82259C18) and the TopBox dropping QuestObjective_Prompt (0x82421CAC), stated through the TopBox elements; no shipped script calls it");
		_router.Observe("SetMovieAsSkippable", "writes the movie manager's skip byte M+93 (0x8251C8E4), read only by the START listener that stops the movie (0x82346924); every PlayMovie* already passes 1");

		// ---- NATAL (KINECT): unreachable ----------------------------------------------------------
		const string natal = "Kinect: the XEX imports no NUI entry and no script calling it is ever loaded (Setup RunScript lists, no QN020 in ScriptActivation) — unreachable";
		foreach (string name in new[] { "EnableNatalCursor", "HideNatalExpressionMaker", "HideNatalShop", "HideNatalStatueMaker", "HideNatalStocks", "SetNatalExpressionMakerRecordingText", "SetNatalExpressionMakerState", "SetNatalStatueMakerCountdownText", "SetNatalStatueMakerState", "SetNatalStocksHeldText", "SetNatalStocksInfoText", "SetNatalStocksTopText", "ShowNatalExpressionMaker", "ShowNatalShop", "ShowNatalStatueMaker", "ShowNatalStocks", "UnloadNatalGUIScreens" })
		{
			_router.Observe(name, natal);
		}

		// ---- THE WORLD MAP'S PANELS, REAL ESTATE, EPILOGUE, CREDITS, GUILD FLAGS --------------------
		_router.Observe("DisplayWorldMapScreen", "opens/closes interaction mode 10 (sub_825C4348(IM,10,entity) 0x8251A9F4 / sub_825C5D08 0x8251AA30); its WorldMapRegion panel and buttons are element writes (text commit 0x82BEA78C, SetOpacity, sub_82961340) drawn by the interactionmode document");
		_router.Observe("RefreshWorldMap", "rebuilds the world-map object at GUI+0x54 (sub_825BED70 -> sub_824CC0E0); no surface, no shipped Lua caller");
		_router.Observe("SetShowWorldMapKingdomCash", "writes *(*(H+0x58)+0x74), read by the world-map region panel sub_829171C0 (0x82917290) to show the kingdom's gold through the money widget (text commit 0x8281E6E0)");
		_router.Observe("SetWorldMapExitDirectlyToGame", "writes the world map's +125, read by its close sub_824CDBB8 (0x824CDC7C) to fade and leave the Sanctuary (reason 12) — guest flow, no surface");
		_router.Observe("ToggleWorldMap", "pushes the world-map component at GUI+0x54 (sub_8240E7A0 0x8251D3B4; pops type 12 at 0x8251D310); it presents its holder's scene (sub_8292AEA8), stated by map_table_hooks.cpp and drawn by Fable3MapTable");
		_router.Observe("ToggleTownMapPrototype", "pushes the town-map component sub_82950FD0 (0x8251D4CC, push 0x8251D504; pops type 12 at 0x8251D47C); it presents its holder's scene (sub_8292AEA8 0x829514B8), stated by map_table_hooks.cpp and drawn by Fable3MapTable");
		_router.Observe("DisplayRealEstateScreen", "opens interaction mode 6 (sub_825C4348(IM,6,…) 0x8251A8A8): BuildingInfo texts (commit 0x82BEA78C), its verb rows (sub_82963DE0 -> sub_82961340) and buttons are element writes drawn by the interactionmode document; the buy/sell confirmations are box-manager boxes (@Box.Queued / @Box.Show)");
		_router.Observe("DisplayRealEstateScreenUnloaded", "opens interaction mode 6/7 for a property in an unloaded region (sub_825C4BB8 0x8251A970, same sub_82B191F0 object): element writes drawn by the interactionmode document");
		_router.Observe("AddItemRewardToEpilogueScreen", "toggles EpilogueItem1/2 (SlideToggleName, sub_82921200 -> @Hud.Slide) and writes their texts (text registry sub_82733F30 -> @Hud.Text) on questcompletion.bgf, drawn by Fable3EndScreens");
		_router.Observe("SetGoldRewardInEpilogueScreen", "writes EpilogueRenown / EpilogueRenownAmt through the text registry (sub_82733F30 -> @Hud.Text), drawn by Fable3EndScreens");
		_router.Observe("ShowJudgementConsequences", "slides JudgementConsequences on / TargetName1-3 off (SetState 0x8218D8E0) and writes the four Judgement{Left,Right}{Top,Bottom} texts (text registry -> @Hud.Text); the shared HUD view's JudgementConsequences group draws them");
		_router.Observe("ClearJudgementConsequences", "slides JudgementConsequences to 3 (SetState); the shared HUD view hides it from the element state");
		_router.Observe("ShowCreditsScreen", "loads creditsscreen.bgf and starts the roll (sub_825CF128 0x8251D198); stated as @Credits.Open/Close, its lines and scroll as @Ui.Text / @Ui.Position at the roll's own commits and SetPosition calls, drawn by Fable3EndScreens");
		_router.Observe("SetCanCouchPlayerEnterGUIld", "writes *(H+0x58)+0x55, read by the guest's Sanctuary entry gate sub_823A9AF0 (0x823A9BA0) — no surface");
		_router.Observe("SetCanGiveMoney", "writes *(H+0x58)+0x50, read by the guest's give-money suggestion gate (sub_829686E8, 0x829691A0)");
		_router.Observe("SetCanReceivedItemsGoToGUIld", "writes *(H+0x58)+0x54, read by the received-item overlay (sub_825DEDF0, 0x825DF7D0), which adds its RI_ROOM choice on E — drawn by the emotion-choice feed");

		// ---- THE INTERACTION INFOBOX PANELS: the InfoBox component's own state (0x825C3B14) ------------
		_router.Observe("ShowDogStatsBox", "InfoBox state 11 (DogInfo): DogInfo.Contents.Name text (0x828190CC), Aggression/Exploration/CharismaValue states (sub_8281A428 0x8295CE00/18/30) — drawn by the InfoBox group");
		_router.Observe("ShowWeaponInfoBox", "InfoBox state 6/1 (WeaponInfo): SlideSets WeaponInfoType/WeaponCarried/PowerStarRating/upgrade1-3 (SetState 0x8218D8E0), TextNames WeaponName/Damage/Value (commit 0x8223A8DC); a networked player's entity sends net message 6 (0x82957E24) instead");
		const string npcInfo = "InfoBox state 5 (NPCInfo): title/description into NPCInfo.Contents.Name/PersonalityTraits (sub_8295CE40), Reward/GuildSealsValue cleared, GuildSeal hidden — drawn by the InfoBox group";
		foreach (string name in new[] { "ShowInfoBoxWithCurrentJoinOptions", "ShowInfoBoxWithCurrentOrbVisibilityOptions", "ShowInfoBoxWithCurrentOrbChatOptions", "ShowInfoBoxWithTitleAndDescription" })
		{
			_router.Observe(name, npcInfo);
		}

		_router.Observe("ShowSaveInfoBoxFromStatue", "InfoBox state 9 (SaveGameInfo): LastPlayedDate/Title/TotalPlayTimeNum/CurrentLocationPlace/PartnershipsNum/Partnerships text (sub_82B45268)");

		// ---- THE RECEIVED ITEM: in the world (ReceiveItemsInWorld byte_8329EC9D is 1 in retail) -------
		const string received = "the in-world received item (sub_825DEDF0 on H+100): the item spawned as an entity (sub_823F1990 at 0x825df330, the entity feed), its A/Y choices on E (sub_825E8DC0, the emotion-choice feed), the name in TargetName1 (commit 0x8223A8DC, SetState 0x8218D8E0)";
		_router.Observe("DisplayReceivedItem", received);
		_router.Observe("DisplayReceivedMoney", received + "; the item is ObjectInventoryGoldBag (sub_825E0250)");
		_router.Observe("CloseReceivedItemOverlay", "destroys the in-world item's entity (0x825e1cf0), drops its choices and slides TargetName2 to 3 (SetState 0x825e1e88)");

		// ---- PRESENTATION LUA CHUNKS: their writes reach the Gameface Lua hooks (gui_element_hooks.cpp) ---
		_router.Observe("ExecuteHudScript", "runs a chunk in hud.bgf's Lua (sub_82819D40 at 0x8251D5E4); its setAttribute / goToSlide writes are stated by the Gameface Lua hooks (sub_82C24A70, the slide funnel sub_82268BE0)");
		_router.Observe("ExecuteScreenScript", "runs a chunk in the current screen's Lua (sub_82819D40 at 0x8251D634); its writes are stated by the Gameface Lua hooks");

		StateVerdicts();
	}

	// ---- EVERY OTHER NATIVE'S VERDICT (the 270 `GUI.*` registrations; SEAM.md §9) ---------------
	//
	// The seam posts BEFORE the guest's implementation and never suppresses it (gui_natives.cpp),
	// so each native's verdict is one of: owned (registered above), observed with the reason the
	// guest's own result stands, or PENDING a Godot surface — those stay out of `Observe` so the
	// census keeps reporting them as NOT ROUTED with their arguments until the surface exists.

	/// <summary>
	/// The box-manager screens: each native constructs its screen class and queues it through
	/// sub_825C8528 — ShowHeroStatusScreen stores vtable 0x820C345C inline (0x825217B0), OpenStatsBox
	/// calls the Stats ctor sub_8296D2C8, ShowPromiseScreen sub_827A3290, ShowQuestsSummary
	/// sub_829735B0, ShowKingsSchedule sub_82972D58; the Close natives drop it (sub_825C8860 /
	/// sub_825C88F0). Shown and dropped by the box manager (@Box.Show/Close `screen:*`), drawn by
	/// Fable3StatedScreen and Fable3Options.
	/// </summary>
	private static readonly string[] ScreenNatives =
	{
		"ShowHeroStatusScreen", "OpenStatsBox", "ShowPromiseScreen", "ShowQuestsSummary", "ShowKingsSchedule",
		"OpenOptionsScreen", "OpenFrontEndOptionsMenu", "CloseStatsBox", "CloseKingsSchedule",
		"DisplayMiniStatementBox", "ShowWeddingPickerBox", "ShowJoinOptions", "ShowVisibilityOptions", "OpenMultiplayerPartnersBox", "OpenOrbChatBox", "CloseOrbChatBox", "ShowFriendsChannel",
	};

	/// <summary>Registered with no body: impl 0x82C1BA40 is `blr` (nullsub_1), or (DisplayAcceptanceBox, 0x82518350..0x82518448) parses its arguments and calls no queue.</summary>
	private static readonly string[] StubNatives =
	{
		"AddHealthBar", "RemoveHealthBar", "DisplayFastTravelScreen", "SetCutsceneVignette", "SetOlympicsLevel",
		"SetOlympicsScoreForPlayer", "SetOlympicsTime", "DisplayAcceptanceBox",
	};

	/// <summary>Value-returning natives (dispatchers 0x821BF2A8 / 0x82192EE8 / 0x822E1288 / 0x82496868 / RAW returning): the script reads the guest's answer back.</summary>
	private static readonly string[] AnsweringNatives =
	{
		"Emotion.EmotionIconsAlreadyBeingHeld", "Emotion.GetEmotionIconNames", "GetDistFromLineAndPointFromVertical", "GetDyeBrightness",
		"GetEntityPlayerIsInteractingWithInInteractionMode", "GetEntityPlayerIsInteractingWithInInteractionModeIDAsString",
		"GetEntityUIDAsString", "GetLevelIDForLevel", "GetNumberOfSubObjectives", "GetPointDistFromInfiniteLine3D",
		"GetPointDistFromLine3D", "GetPointProportionAlongLine", "GetPositionOnInfiniteLine3D", "IsAnyMenuOpen",
		"IsCrimeScreenActive", "IsDisplayBoxActive", "IsEntityOnScreen", "IsEpilogueScreenVisible", "IsFullCutscenePlaying",
		"IsFullPauseMenuDisabledForHero", "IsHero", "IsHeroStatusScreenOpen", "IsInInteractionModeMenuDecoration",
		"IsJoinOptionsOpen", "IsKingsScheduleOpen", "IsLoadingScreenActive", "IsMessageBoxOpen", "IsMiniStatementBoxActive",
		"IsMoviePlaying", "IsOrbChatBoxOpen", "IsPodiumInteractionMenuOpen", "IsPromiseScreenOpen", "IsReceiveItemOverlayOpen",
		"IsSafeForWeaponLeveling", "IsSoundCategoryPlaying", "IsStatsBoxOpen", "IsUIActive", "IsVisibilityOptionsOpen",
		"TranslateMeDo",
	};

	/// <summary>Sound cues the GUI namespace carries; the guest's audio plays them.</summary>
	private static readonly string[] SoundNatives = { "PlaySound", "PlaySoundInGameLayer", "PlaySoundWithCategory", "StopSoundCategoryPlaying" };

	/// <summary>Natives whose surface Godot does not draw yet, by surface: NOT ROUTED in the census on purpose.</summary>
	private static readonly (string Surface, string[] Names)[] PendingNatives =
	{
	};

	private void StateVerdicts()
	{
		foreach (string name in ScreenNatives)
		{
			_router.Observe(name, "queues a box-manager screen (sub_825C8528) — stated when it shows (@Box.Show screen:*), drawn by Fable3StatedScreen / Fable3Options");
		}

		_router.Observe("OpenSaveMenu",
			"opens the save menu object *(SM+56) on SaveMenu.bgf (sub_8296C5D0); its root's state states it (1..4 open, 5 closed), drawn by Fable3StatedScreen");

		foreach (string name in StubNatives)
		{
			_router.Observe(name, "the guest's implementation does nothing (nullsub_1 at 0x82C1BA40, or parses and queues nothing)");
		}

		foreach (string name in AnsweringNatives)
		{
			_router.Observe(name, "answers the script from the guest's own state; the seam never suppresses, so the guest's answer stands");
		}

		foreach (string name in SoundNatives)
		{
			_router.Observe(name, "a sound cue the guest's audio plays — no surface");
		}

		int pending = 0;
		foreach ((string _, string[] names) in PendingNatives)
		{
			pending += names.Length;
		}

		GD.Print($"[ui-cut] fable3 verdicts: {ScreenNatives.Length} screen opener(s), {StubNatives.Length} stub(s), " +
			$"{AnsweringNatives.Length} answering, {SoundNatives.Length} sound cue(s) observed; {pending} PENDING a Godot surface " +
			$"(reported NOT ROUTED when called) across {PendingNatives.Length} surface(s)");
	}

	/// <summary>The boxes on screen, by the guest's box address, so a close drops the right one.</summary>
	private readonly Dictionary<uint, Engine_UiGuiBoxes.Box> _shown = new();

	/// <summary>Boxes pushed and not yet shown, by address: their push statement. Bounded: a box the
	/// guest frees without an erase (a manager torn down whole) is forgotten with the oldest.</summary>
	private readonly Dictionary<uint, Engine_UiModel.GuiNativeCall> _queued = new();
	private readonly Queue<uint> _queuedOrder = new();
	private const int MaxQueued = 64;

	/// <summary>The boxes Godot answers, in the order they showed: the last is the front.</summary>
	private readonly List<OwnedBox> _owned = new();

	/// <summary>A box on screen whose pad is Godot's: the guest's address, its kind and its model.</summary>
	public sealed record OwnedBox(uint Address, string Kind, Engine_UiGuiBoxes.Box Box, bool Download, int Queue = 0);

	/// <summary>The kind of the owned box <paramref name="box"/> is the model of, or null for one Godot does not answer.</summary>
	public string? OwnedKind(Engine_UiGuiBoxes.Box box)
	{
		foreach (OwnedBox owned in _owned)
		{
			if (ReferenceEquals(owned.Box, box))
			{
				return owned.Kind;
			}
		}

		return null;
	}

	/// <summary>
	/// The box the pad answers now, or null: the newest of the first queue's (and the pause screen's
	/// message box), else the newest of the second's — the router hands the pad to Q0's showing box
	/// before Q1's (sub_825F38F8, 0x825F3910..0x825F3960).
	/// </summary>
	public OwnedBox? FrontOwned
	{
		get
		{
			for (int i = _owned.Count - 1; i >= 0; --i)
			{
				if (_owned[i].Queue <= 0)
				{
					return _owned[i];
				}
			}

			return _owned.Count == 0 ? null : _owned[^1];
		}
	}

	private void BoxQueued(Engine_UiModel.GuiNativeCall call)
	{
		uint address = Parse(call.Argument(1).Text);
		if (address == 0u)
		{
			return;
		}

		if (_queued.Count >= MaxQueued && _queuedOrder.TryDequeue(out uint oldest))
		{
			_queued.Remove(oldest);
		}

		_queued[address] = call;
		_queuedOrder.Enqueue(address);
	}

	private void BoxShown(uint address)
	{
		if (!_queued.Remove(address, out Engine_UiModel.GuiNativeCall call))
		{
			GD.Print($"[fable3-boxes] box {address:X8} shown with no push stated (queued before the hooks ran); not drawn");
			return;
		}

		BoxOpened(call);
	}

	/// <summary>
	/// The box `@Box.Queued(queue, box, kind, name, text, …)` stated, now showing: question boxes
	/// carry their yes/no tags, the adjuster its min/max/increment/value, an info card its title,
	/// accept and help tags and the two button flags. Every box stays until `@Box.Close` — the
	/// lifetime is the guest's.
	/// </summary>
	private void BoxOpened(Engine_UiModel.GuiNativeCall call)
	{
		uint address = Parse(call.Argument(1).Text);
		string kind = call.Argument(2).Text;
		string name = call.Argument(3).Text is { Length: > 0 } stated ? stated : UnnamedBox;
		string text = call.Argument(4).Text;
		BoxClosed(address);

		Engine_UiGuiBoxes.Box box;
		switch (kind)
		{
			case "question":
				box = Boxes.Question(name, text);
				if (call.Argument(5).Text.Length > 0 || call.Argument(6).Text.Length > 0)
				{
					box.Buttons = new Engine_UiGuiBoxes.BoxButtons(true, false, false, true, false, call.Argument(5).Text, call.Argument(6).Text);
				}
				break;
			case "adjuster":
				box = Boxes.Adjuster(text, (int)call.Argument(5).Number, (int)call.Argument(6).Number,
					(int)call.Argument(7).Number, (int)call.Argument(8).Number, 0, false);
				break;
			case "quest":
				box = Boxes.QuestBox(name, text, 0, 1);
				break;
			case "noninteractive":
				// TU3's DisplayNonInteractiveMessageBox (display_box_hooks.cpp kVtNonInteractive): the
				// message box with its button row hidden (its Show hides `paper.Buttons`, 0x82879578)
				// and no input; it goes when RemoveDisplayBox closes it. Not owned: no pad answers it.
				box = Boxes.MessageBoxNamed(name, text);
				box.Buttons = new Engine_UiGuiBoxes.BoxButtons(false, false, false, false, false, string.Empty, string.Empty);
				break;
			case "card":
				// CAcceptHoldBox (type 8, `DisplayInfoBoxParams` with DisplayBoxStyle 0): its stated
				// title (+36), accept and help tags (+44 / +48), A and Y shown (+40 / +41) and whether A
				// must be held (+42). Godot draws it and owns its pad and its hold (display_box_hooks.cpp).
				box = Boxes.InfoBoxParams(name, call.Argument(5).Text, -1f, text, 0, new Engine_UiGuiBoxes.BoxButtons(
					call.Argument(8).Boolean, call.Argument(10).Boolean, false, call.Argument(9).Boolean, false,
					call.Argument(6).Text, call.Argument(7).Text));
				break;
			case "buttons":
				// CButtonBox (type 7, `DisplayButtonBox(name, text)`, the scripts' acceptance box with help,
				// miscfunctions.lua GUI.ShowAcceptanceBoxWithHelp): its text alone; it answers every face
				// button, A accept and Y help the two its script reads.
				box = Boxes.MessageBoxNamed(name, text);
				box.Title = "GUI_ACCEPTANCE_BOX";
				box.Buttons = new Engine_UiGuiBoxes.BoxButtons(true, false, false, true, false, string.Empty, string.Empty);
				break;
			case "unknown":
			case string screen when screen.StartsWith("screen:", System.StringComparison.Ordinal):
				// The box-manager SCREENS: Options (`screen:options`, drawn by Fable3Options from
				// its stated state, its pad Godot's), hero status, stats, promises, quest summary and the king's schedule
				// (element writes under their own root, drawn by Fable3StatedScreen), and any class
				// the hooks do not name yet — the census names it for the next table entry.
				if (address != 0u && _screens.Add(address))
				{
					_screenKinds[address] = kind;
					Fable3ScreenBoxes.Shown(+1, Fable3ScreenBoxes.RootOf(kind), address);
					GD.Print($"[fable3-screens] {kind} box {address:X8} '{name}' shown — text '{text}'");
				}

				return;
			default:
				box = Boxes.MessageBoxNamed(name, text);
				if (kind is "message" or "modal" && call.Argument(5).Boolean)
				{
					// The download form: box+33 set, the X button named by the tag at box+36
					// (DisplayDownloadMessageBox); the pause screen's box names GUI_BUTTONS_DOWNLOAD.
					box.AcceptCaption = call.Argument(6).Text;
				}

				break;
		}

		if (address != 0u)
		{
			_shown[address] = box;
			if (Fable3BoxAnswers.Owns(kind))
			{
				_owned.RemoveAll(owned => owned.Address == address);
				_owned.Add(new OwnedBox(address, kind, box, kind is "message" or "modal" && call.Argument(5).Boolean,
					(int)call.Argument(0).Number));
			}
		}
	}

	/// <summary>The screen boxes showing, by the guest's box address, and each one's kind.</summary>
	private readonly HashSet<uint> _screens = new();
	private readonly Dictionary<uint, string> _screenKinds = new();

	/// <summary>
	/// `@Screens.Destroyed(sm)`: every box queued or showing on the freed manager, its menu box and
	/// the save menu went with it (display_box_hooks.cpp rex_ScreenManager_Destroy).
	/// </summary>
	private void ScreensDestroyed()
	{
		var closing = new List<uint>(_shown.Keys);
		closing.AddRange(_screens);
		foreach (uint address in closing)
		{
			BoxClosed(address);
		}

		_queued.Clear();
		_queuedOrder.Clear();
		Boxes.CloseMenuBox();
		Fable3UiStatements.ScreensDestroyed();
		GD.Print($"[fable3-boxes] screen manager destroyed: {closing.Count} box(es) closed with it, menu box and save menu closed");
	}

	private void BoxClosed(uint address)
	{
		if (address != 0u && _shown.Remove(address, out Engine_UiGuiBoxes.Box? box))
		{
			Boxes.Remove(box);
		}

		_owned.RemoveAll(owned => owned.Address == address);

		if (address != 0u && _screens.Remove(address))
		{
			_screenKinds.Remove(address, out string? kind);
			Fable3ScreenBoxes.Shown(-1, Fable3ScreenBoxes.RootOf(kind ?? "unknown"), address);
			Fable3ScreenStatements.Forget(address);
			if (kind == "screen:options")
			{
				Fable3OptionsState.Closed(address);
			}
		}
	}

	/// <summary>
	/// `@MenuBox.Open(title, cancelable, row…)`: the title as the filler resolved it, a row its
	/// TextTag prefixed `+` or `-` for enabled. The highlight starts on the first row, as the
	/// open's own `*MB = 0` (0x825FA348) has it.
	/// </summary>
	private void MenuBoxOpened(Engine_UiModel.GuiNativeCall call)
	{
		var options = new List<Engine_UiGuiBoxes.MenuOption>();
		if (call.Arguments is { Count: > 2 } arguments)
		{
			for (int i = 2; i < arguments.Count; ++i)
			{
				string row = arguments[i].Text;
				if (row.Length == 0)
				{
					continue;
				}

				options.Add(new Engine_UiGuiBoxes.MenuOption(
					Engine_UiGuiText.Resolve(row[1..], Fable3Strings.Current), row[0] == '+'));
			}
		}

		Boxes.ShowMenuBox(call.Argument(0).Text, options, call.Argument(1).Boolean);
		Boxes.Menu!.Highlight = 0;
	}

	private static uint Parse(string hex) =>
		uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint value) ? value : 0u;
}
