// refii — the guest tick pump: a place for the host to run work ON THE GUEST'S OWN TICK THREAD.
// The host never calls the guest cross-thread; it posts, and the guest picks the work up at a
// point in its own tick where its state is coherent.
//
// The site: `sub_822C1BD0`, a seven-instruction thunk forwarding through `[this+0x24]`, reached
// only through slot 5 (offset 0x14) of the vtable on `dword_8349E9EC`, loaded at exactly three
// places — one per tick loop:
//
//     0x82377B68  in sub_82377A00   boot movies, 60 Hz
//     0x82319E88  in sub_82319BE8   front end,   30 Hz  (dbl_83319630)
//     0x82277190  in sub_82276D10   in game,     15 Hz  (dbl_83319668)
//
// Its only xref is data (`0x8200282C`, the vtable slot), so those three are the complete
// reachable set: exactly once per tick, in every state, and nowhere else.
//
// The thread is not the process's initial one. `sub_822EA1C0` spawns the worker and then parks:
//
//     822ea1f4  addi r3, r10, sub_822EA228@l
//     822ea1f8  bl   beginthread
//     822ea1fc  li   r4, -1
//     822ea204  bl   sub_821969C8          ; WaitForSingleObjectEx(h, INFINITE, 0)
//
// so the tick lives on the spawned "GameThread", pinned to hardware thread 0.
//
// This fires during the boot movies, before the front-end state exists (`dword_83496AC0` is
// written only when it is constructed), so every pointer read here is checked and an action
// that cannot run yet stays queued. Actions are named strings, matched in `RunPendingActions`;
// an unknown action is dropped and logged.

#include "core/hero_derived.h"
#include "vfs_hooks_private.h"

#include <algorithm>
#include <atomic>
#include <cctype>
#include <chrono>
#include <cstdint>
#include <cstdlib>
#include <cstdio>
#include <cmath>
#include <cstring>
#include <map>
#include <mutex>
#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#else
#include <signal.h>
#endif
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include <rex/kernel/xboxkrnl/threading.h>

#include "core/guest_state.h"
#include "core/mod_values.h"
#include "core/refii_mod_events.h"
#include "core/refii_mod.h"
#include "gui/gui_display_box.h"
#include "hooks/frontend_stated.h"
#include "hooks/gui_screen_stubs.h"
#include "hooks/keyboard_ui.h"
#include "hooks/page_fills_stated.h"
#include "hooks/item_rows_stated.h"
#include "hooks/pathfinder_batch.h"
#include "hooks/ppc_externs.h"
#include "gui/gui_told_statements.h"
#include "core/gui_native_calls.h"
#include "core/guest_build.h"

// The New Game commit, `sub_826E0D60`: hooked in hooks/gui_menu_hooks.cpp, so the host calls the
// original (ppc_externs.h).

// The front end's own menu-action dispatcher, vtable slot +16 of 0x820D0934. Called directly:
// the slot is fixed.
PPC_EXTERN_FUNC(rex_lhGui_FrontendRoot_MenuActionDispatch);
// The front end's two dialog owners' input slots (vtable +12) — DispatchFrontEndDialog.
PPC_EXTERN_FUNC(rex_lhGui_FrontendRoot_OnScreenAction);
PPC_EXTERN_FUNC(rex_lhGui_FrontendTitle_OnScreenAction);

// "How many saved games does the signed-in user have?" — the probe the front end uses:
//
//   sub_82443308  no parameters, no device, no overlapped structure. Enumerates content type 1
//                 for user `dword_83496AF8` with DeviceID 0 — ALL devices — and returns the
//                 item count. Closes its handle unconditionally.
//   sub_824431E8  same enumeration, but reads a DeviceID out of the save manager's map at
//                 `saveMgr+0x7EC`, and returns 0 when that map has no entry.
//   sub_82443048  same again, and fills a vector of save NAMES.
//
// The last two answer 0 until `sub_82441CB8` has written that map, so only `sub_82443308` is
// usable from a host poll.
// The storage device picker, and the only writer of the save manager's device map:
//
//   82441d88  j_XamShowDeviceSelectorUI(userIndex, 1, 0, size, &deviceId, ov)
//   82441d98  XGetOverlappedResult(ov, 0, 1)        ; bWait=1 — IT BLOCKS
//   82441dac  slot = sub_8244A0C8(saveMgr + 2028, &userIndex)   ; 2028 = 0x7EC
//   82441dbc  *slot = deviceId
//   82441dcc  return deviceId != 0
//
// `saveMgr + 0x7EC` is measured from the FIRST ARGUMENT, which the guest builds at 0x826E008C
// as `*(*(0x83496AC0 + 0xC) + 0x28)`.
//
// Safe to block here: our XamShowDeviceSelectorUI returns X_ERROR_IO_PENDING and completes on
// the kernel dispatch thread via CompleteOverlappedDeferred. Costs 100ms, once, at New Game.
PPC_EXTERN_FUNC(rex_lhSaveManager_SelectStorageDevice);
PPC_EXTERN_FUNC(rex_CNetworkManager_LeaveSession);
PPC_EXTERN_FUNC(rex_CGameModePause_ctor);
PPC_EXTERN_FUNC(rex_GameFlow_QueueCommand);
PPC_EXTERN_FUNC(rex_GameFlow_PopCommand);
PPC_EXTERN_FUNC(rex_lhGuiTabStrip_Push);
PPC_EXTERN_FUNC(rex_lhLua_SetRumbleEnabled);
extern "C" int RefiiCoopPlayerTwoNode(void);
extern "C" int32_t RefiiPropertyRepair(PPCContext& ctx, uint8_t* base, uint32_t hero, uint64_t uid);
extern "C" void RefiiPropertyRepairAll(PPCContext& ctx, uint8_t* base, uint32_t hero, int32_t* repaired, int32_t* spent,
                                       int32_t* left);
extern "C" int RefiiCoopSplitScreen(void);
extern "C" int RefiiCoopSplitScreenWanted(void);
extern "C" void RefiiCoopLocalPlayerTwoCall(int on);
extern "C" uint32_t refii_household_resolve_uid(PPCContext* ctx, uint8_t* base, uint64_t uid);

PPC_EXTERN_FUNC(rex_lhSaveManager_CountSavedGamesAllDevices);

// The New Game guard's third test and its "continue without saving" (TU1 0x826E0134..0x826E01DC,
// disc 0x826C8578..0x826C861C): `sub_824431E8(saveMgr, user)` / `sub_824E68C8` counts the saves on
// the device mapped for the user, compared `cmplw` against the limit `dword_83319620` /
// `dword_833194D8`; at or over it the guest erases the user's entry in the device map,
// `sub_82667F50(saveMgr + 0x7EC, &user)` / `sub_824ED700` (std::map<long,long>::erase by key), so
// no container is made and nothing is ever saved.
PPC_EXTERN_FUNC(rex_lhSaveManager_CountSavedGamesOnDevice);
PPC_EXTERN_FUNC(rex_lhUserDeviceMap_Erase);
constexpr refii::guest::Addr kSaveLimit{0x83319620u};
constexpr uint32_t kUserDeviceMap = 0x7ECu;

// Frontend_SetStartGameTarget — the LOAD commit, which commits by itself:
//
//   823798ac  sub_822651A0(&g_PendingLoadSaveName, a2)   ; a2 = the save NAME
//   8237989c  sub_822651A0(&g_StartLevelName,      a3)   ; a3 = the LEVEL to load
//   823798e0  *(*(dword_83496AC0 + 12) + 92) = 1         ; the run loop's exit flag
//   823798e8  sub_82319928(*(dword_83496AC0 + 12))       ; builds the next game state
//
// Both arguments are handle SLOTS, not strings: `sub_822651A0` copies a 4-byte handle and bumps
// the refcount at `*handle + 12`, so it must be handed the ADDRESS of a live handle. `a1` is
// never referenced. It dereferences `dword_83496AC0 + 12` with no null check, so it is only
// safe while the front end is up.
PPC_EXTERN_FUNC(rex_lhFrontEnd_SetStartGameTarget);

// The screen-id -> handler map lookup, `*(GUI+28)` and a screen id in, handler object out.
// A find on the red-black tree at `guiOwner+68` (miss returns 0); registration is the
// guiOwner ctor `sub_8236D078`, which maps screen id 0 to the pause main menu handler
// (ctor `sub_826A1818`, vtable 0x820CDE18).
PPC_EXTERN_FUNC(rex_lhGuiManager_FindScreenHandler);
PPC_EXTERN_FUNC(rex_lhGuiPresentation_GetRootElement);  // the presentation's scene-root element
PPC_EXTERN_FUNC(rex_lhGuiElement_GoToSlide);  // goToSlide(element, presentation, index)

// The MenuBox accept — `sub_823DC720(ctl)`, ctl = *(guiOwner+56). Reads the selected index
// at ctl+0, checks the item's enabled byte, invokes the callback, and CLOSES/publishes
// itself (player message 35/101). Refuses a disabled item.
PPC_EXTERN_FUNC(rex_lhMenuBox_Accept);

// The four display-box input handlers — slot 3 of each box class's vtable. Named statically
// because the recompiler emits no indirect-call path; the discriminator is the box's own
// vtable, i.e. its class identity:
//
//   off_820AE688  QueryBox      (YesNo, PubGameYesNo, QuestPrimaryQuestion)  -> sub_823CF3B0
//   off_820AE6A8  NumericAdjuster                                            -> sub_823CF8A8
//   off_820AE6C8  QuestBoxParams                                             -> sub_823D0090
//   off_820AE668  MessageBox    (also Download and Divorce)                  -> sub_823CEDD8
//
// Two classes are deliberately absent: the AcceptanceBox (`off_820EBF3C`) has `li r3,0; blr` in
// both input slots (its interaction lives in the Gameface Lua behind
// `g_DisplayBox:OpenAsAcceptanceBox()`), and the ButtonBox (`off_820028F4`) is a no-op at slot 3
// and takes input at slot 4 with a different argument shape (an input-phase record).
PPC_EXTERN_FUNC(rex_lhDisplayQueryBox_HandleAction);
PPC_EXTERN_FUNC(rex_lhDisplayNumericAdjuster_HandleAction);
PPC_EXTERN_FUNC(rex_lhDisplayQuestBox_HandleAction);
PPC_EXTERN_FUNC(rex_lhDisplayMessageBox_HandleAction);

// The MenuBox input handler — `sub_823DBC70(ctl, input)` (disc `sub_8240BEA0`). Its B arm is the
// only place the guest cancels a box: cancelled byte, then the close (0x823dc0cc..0x823dc0d4).
PPC_EXTERN_FUNC(rex_lhMenuBox_HandleInput);

// The MenuBox selector — `sub_823DBB48(ctl, index)`: old selection to slide 1, new to slide 2,
// writes ctl+0. Keeps the guest's drawn selection in step with the host highlight.
PPC_EXTERN_FUNC(rex_lhMenuBox_SetSelection);

// The vtable+24 screen-input methods, per screen id. Named statically because the recompiler
// emits no indirect-call path; each is checked against its screen id (from the guest's `mgr+4`)
// before use.
PPC_EXTERN_FUNC(rex_lhGuiAbilitiesScreen_OnScreenAction);  // 14 abilities
PPC_EXTERN_FUNC(rex_lhGuiItemsScreen_OnScreenAction);  // 15 items/weapons
PPC_EXTERN_FUNC(rex_lhGuiClothingScreen_OnScreenAction);  // 16 clothing
PPC_EXTERN_FUNC(rex_lhGuiLogbookScreen_OnScreenAction);  // 17 logbook
PPC_EXTERN_FUNC(rex_lhGuiOptionsScreen_OnScreenAction);  // 18 options
PPC_EXTERN_FUNC(rex_lhGuiCrimeScreen_OnScreenAction);  // 21 crime
PPC_EXTERN_FUNC(rex_lhGuiShopScreen_OnScreenAction);  // 22 shop
PPC_EXTERN_FUNC(rex_lhGuiWorldMapScreen_OnScreenAction);  // 23 world map
PPC_EXTERN_FUNC(rex_lhGuiGiftGivingScreen_OnScreenAction);  // 25 gift
PPC_EXTERN_FUNC(rex_lhGuiSelectionScreen_OnScreenAction);  // 26 selection
PPC_EXTERN_FUNC(rex_lhGuiDecorationScreen_OnScreenAction);  // 27 decoration
PPC_EXTERN_FUNC(rex_lhGuiQueryScreen_OnScreenAction);  // 10 query screen (37 close, 38 rename)
PPC_EXTERN_FUNC(rex_lhGuiRealEstateScreen_OnScreenAction);  // 28 real estate (`*(0x820CEA58+24)`, read from the image)
PPC_EXTERN_FUNC(rex_lhGuiRealEstateScreen_OnMenuEvent);  // 28 real estate (`*(0x820CEA58+20)`, read from the image)
PPC_EXTERN_FUNC(rex_lhGuiManager_CanPlayerOpenQuickMenu);  // expression wheel: may open? (hero alive, mode 1/5/20/21/22)
PPC_EXTERN_FUNC(rex_lhGuiManager_OpenExpressionWheel);  // expression wheel: open (GameAction 40's body)
PPC_EXTERN_FUNC(rex_lhGuiManager_CloseExpressionWheel);  // expression wheel: close (GameAction 41's body)
PPC_EXTERN_FUNC(rex_lhGuiManager_OpenSpellsMenu);  // spells menu: open (GameAction 42's body)
PPC_EXTERN_FUNC(rex_lhGuiManager_CloseSpellsMenu);  // spells menu: close (GameAction 43's body)
PPC_EXTERN_FUNC(rex_lhGuiWillAssignWidget_OnScreenAction);  // will-assignment widget input: 26 stick, 28..31, 36 copy-down
PPC_EXTERN_FUNC(rex_lhGuiExpressionWheel_EndHeldExpression);  // expression wheel: end a held expression (GameAction 25)
PPC_EXTERN_FUNC(rex_lhGuiExpressionWheel_Accept);  // expression wheel: accept (36) — enter a group / start the item
PPC_EXTERN_FUNC(rex_lhGuiExpressionWheel_Back);  // expression wheel: back (37) — leave the group, or close at the top
PPC_EXTERN_FUNC(rex_lhGuiExpressionWheel_SetCursorFromStick);  // expression wheel: cursor from a stick vector (x, y)
PPC_EXTERN_FUNC(rex_lhGuiInput_PlayAPress);  // GameAction 36's play consumer (r3 a consumer whose +12 is the player)
PPC_EXTERN_FUNC(rex_lhGuiInput_PostExpressionMenuOpened);  // message 32 at the player's hero (r3 as above)
PPC_EXTERN_FUNC(rex_lhGui_RegisterPlayer);  // GUI RegisterPlayer(GUI, player): ++GUI+0x65, binds the action consumer
PPC_EXTERN_FUNC(rex_lhGuiMinigameCompletedScreen_HandleAction);  // 11 minigame completed (6)
PPC_EXTERN_FUNC(rex_lhGuiQuestCompletionScreen_HandleAction);  // 12 quest completion (6, player 1)
PPC_EXTERN_FUNC(rex_lhGuiCreditsScreen_OnScreenAction);
PPC_EXTERN_FUNC(rex_lhGuiNextEpisodeScreen_HandleAction);

// The GUI input router — `(guiOwner, actionCode, pad)`. Its guard admits input while a
// query box is pending (`guiOwner+44` in {23,24,25,28}) and routes to the CURRENT screen
// handler's vtable+28; for the pause family that is `sub_826A2950`, which reloads +44 for
// both its switch and the callee (the pad argument is discarded) and answers in
// `sub_826A7738`: code 46 = confirm (save/quit per the box), 47 = cancel. The kick box
// (id 28) instead answers the generic accept/back codes 36/37 inline. Call the ROUTER, not the
// handler method, so the guest's own gate stays in the path.
PPC_EXTERN_FUNC(rex_lhGuiManager_RouteModalInput);

// The spell assignment writer — `(spellManager, 0, slot, spellIndex, notify)`:
//
//   82541f58  stbx r10, r3, r31   ; byte[mgr + 128 + slot] = spell
//   82541f60  stb  r10, 5(r11)    ; byte[mgr + 133 + slot] = spell   (the mirror)
//   82541f7c  sub_822D25F0(*(mgr+4))    ; local player? then fire the UI/net event
//
// It validates nothing about the spell, so the caller must only send a pair the offering matrix
// admits. Used in preference to both wrappers: `sub_823B96E0` has only data references, so the
// recompiler may not emit it, and `sub_82541168` goes through `sub_82173EB0`, which CREATES the
// `CECSpellManager` component when it is absent.
PPC_EXTERN_FUNC(rex_CECSpellManager_SetSpellSlot);
PPC_EXTERN_FUNC(rex_lhWorld_ComputeWorldLevelFnv1Key);  // World_ComputeWorldLevelFnv1Key(world, "Albion", &levelName)
PPC_EXTERN_FUNC(rex_lhWorld_LookupScenarioNameByLevelKey);  // World_LookupLevelNameByKey(world, key) -> &scenario handle

// The LHString primitives: `sub_8222CED0(LHString* dest, const char* literal)` constructs one
// from a NUL-terminated guest string; `sub_82214DD8` releases it.
PPC_EXTERN_FUNC(rex_lhCharString_CtorFromCStr);

// `lhLua_RunScriptBuffer(lua_State* L, const char* buf, size, LHString* name, -)` (TU1
// 0x82451F18, base 0x824155C8) — `lua_load(L, getS, {buf, size}, "@" + name)` at 0x82451fb8,
// then `luaD_pcall` with no handler, the stack restored either way; 1 on success. `lua_load`'s
// `f_parser` takes a first byte other than 0x1B as SOURCE (0x82a34060), so plain text runs. r7
// is never read.
PPC_EXTERN_FUNC(rex_lhLua_RunScriptBuffer);

// `sub_824E2F28(CECStats*, abilityId, rank, forceReplicate)` — the rank writer the purchase
// wrappers call once they have charged experience and raised their box. It CLAMPS for us
// (`sub_824E2D40` gives the ceiling, 0x824E2F48) and stores at 0x824E3240; `a4` forces the
// co-op replication at 0x824E39A4. Going straight to it skips the cost and the message box.
PPC_EXTERN_FUNC(rex_CECStats_SetAbilityLevel);

// `sub_824E0B40(CECStats*)` — the appearance flush. EVERY BRANCH IS PRESENCE-GUARDED, so it is
// safe on a body that lacks any of them: component 116 gets `+16 = 1` and a repack, component 15
// takes the two invalidation stamps at `+100`/`+104`, component 21 gets `sub_8241FEA8`.
PPC_EXTERN_FUNC(rex_CECStats_FlushAppearance);

// `sub_825B5F30(comp17, recordId)` — the face: resolves the id, matches it against the def's
// `CharacterCreationRecord` sections, writes `comp17+180` (0x825B60A4 / 0x825B60E0) and rebuilds
// through `sub_825B8AD0`. Verified in the decompile.
PPC_EXTERN_FUNC(rex_CECCompositeModel_ApplyCharacterCreationRecord);
PPC_EXTERN_FUNC(rex_CECWeaponSet_EquipWeaponByRecord);
PPC_EXTERN_FUNC(rex_CECAppearanceModifiers_RemoveWornByCategory);
PPC_EXTERN_FUNC(rex_CECWeaponSet_GetSlotNameForRecord);   // weapon-set: slot name for a record's WeaponType
PPC_EXTERN_FUNC(rex_CECInventory_InstantiateHeldRecord);   // inventory: instantiate a held record as an item entity
PPC_EXTERN_FUNC(rex_lhEntity_Activate);   // entity: activate (components' vtable+44, world registration)
PPC_EXTERN_FUNC(rex_CECHeldItems_AttachItemToSlot);   // held-items: attach an item to a named slot
PPC_EXTERN_FUNC(rex_CECWeaponSet_RecordDrawnItem);   // weapon-set: record the drawn item
PPC_EXTERN_FUNC(rex_CECWeaponSet_PlayWeaponDrawEffect);   // weapon-set: draw effect
PPC_EXTERN_FUNC(rex_CECHeldItems_ReleaseSlot);   // held-items: release a named slot, returning the item it held
PPC_EXTERN_FUNC(rex_lhNetManager_FindEntityByNetId);   // network: net id -> entity (client: the replication map; host: uid)
PPC_EXTERN_FUNC(rex_lhEntity_Destroy);   // entity: destroy
PPC_EXTERN_FUNC(rex_lhCoopScreen_SpawnPreviewMannequin);   // co-op screen: spawn the preview entity for the current pickbox
PPC_EXTERN_FUNC(rex_lhGuiCoopScreen_SetPickerCursor);   // co-op save picker: the cursor's setter (r3 screen, r4 save)
PPC_EXTERN_FUNC(rex_lhGuiCoopScreen_CommitPickedSave);   // co-op save picker: load the save at +392 (r3 screen)
PPC_EXTERN_FUNC(rex_lhGuiCoopScreen_OnScreenAction);     // co-op screen vf+24 (r3 screen, r4 code, r5 player)
PPC_EXTERN_FUNC(rex_lhGuiCoopScreen_ClosePicker);   // co-op save picker: close the fan (r3 screen)

// Dressing player two. `sub_824F0370(ItemRef out[4], CECInventory*, defHash)` puts a def's item
// into the inventory (bumping the count of one already held); `sub_824BF068(comp15, defHash)`
// wears it, and ONLY if comp 13 already holds it (0x824BF1C4); `sub_82764DC0(item, &ref)`
// releases the ref `sub_824F0370` handed back. `sub_824EFFC8(comp13)` / `sub_824BEB78(comp15)`
// are creation's own `InitialItems` / `InitialModifiers` passes, the fallback when no kit names
// an outfit. All verified in the decompile; all present in the recompile.
// A kit's dye restated on a worn item: `Lua_AppearanceModifierManager_SetClothingItemDyeColour`
// (r3 comp15, r4 a registered item ref, r5 the slot byte — nonzero writes +4/+8, zero +12/+16 of
// the item's type-2 record, created by `sub_823A2040` when the item has none — r6 red to byte +2,
// r7 green +1, r8 blue +0, alpha +3 = 0xFF, f1 brightness), then `sub_824BFD78(comp15, ref)`,
// the wearer's dye refresh. The ORIGINAL body: the clothing_dyed event the mod layer raises
// from its hook is for a player dyeing, not for a body being given its owner's kit.
PPC_EXTERN_FUNC(__imp__rex_CECAppearanceModifiers_SetDyeColour);
// A worn item taken off a body whose owner no longer states it:
// `Lua_AppearanceModifierManager_RemoveItem(comp15, holder)` (TU1 0x824BF208 / disc 0x8256FE20)
// erases the worn record whose {item, index} equal the holder's first two words
// (`lwz r11,0(r31); lwz r6,0(r4); cmpw` .. `lwz r11,4(r31); lwz r6,4(r4); cmpw`, 0x824bf268..
// 0x824bf280) and refreshes the wearer. The original, as for the dye: no clothing_removed event.
PPC_EXTERN_FUNC(__imp__rex_CECAppearanceModifiers_RemoveItem);
PPC_EXTERN_FUNC(rex_CECInventory_AddItemByDefHash);
PPC_EXTERN_FUNC(rex_CECInventory_AddOneOfRecord);
PPC_EXTERN_FUNC(rex_CECAppearanceModifiers_WearItemByDefHash);
PPC_EXTERN_FUNC(rex_lhTrackedObject_RemoveHandleRef);
PPC_EXTERN_FUNC(rex_CECInventory_AddInitialItems);
PPC_EXTERN_FUNC(rex_CECAppearanceModifiers_ApplyInitialModifiers);
PPC_EXTERN_FUNC(rex_lhPlayer_ChangeEntityType);  // Player::ChangeEntityType(Player*, LHString creatureName)
PPC_EXTERN_FUNC(rex_lhScriptGui_FadeScreenIn);  // FadeScreenIn(double seconds)

/// The abilities screen's event handler — `vtable+20` of the class `sub_826C1E80` builds.
/// Called by static dispatch (the recompiler emits no indirect-call path), but the live
/// object's slot is read and compared against this address first, so a screen whose slot holds
/// anything else is refused. Vtable `0x820CF4BC`: `+16` `0x826C23A8` (per-frame update), `+20`
/// `0x826C2660` (this), `+24` `0x826C2958` (ScreenAction).
PPC_EXTERN_FUNC(rex_lhCharString_Dtor);
PPC_EXTERN_FUNC(rex_CECInventory_FindItemByRecordHash);   // inventory record -> item handle, BY RECORD HASH
PPC_EXTERN_FUNC(rex_lhTrackedObject_RemoveHandleRef);   // release an item handle taken by sub_824F1C90
PPC_EXTERN_FUNC(rex_lhEntity_UseInventoryItem);   // use an item, by handle
PPC_EXTERN_FUNC(rex_lhGiftComponent_GiveGift);   // give a gift, by handle
PPC_EXTERN_FUNC(rex_lhGuiInventory_ActivateItemByCategory);   // ACTIVATE an item by handle — the guest's own category switch
PPC_EXTERN_FUNC(rex_lhHeroStats_GetMorality);   // GetMorality — the DISPLAYED value
PPC_EXTERN_FUNC(rex_lhHeroStats_GetPurity);   // GetPurity   — the DISPLAYED value
PPC_EXTERN_FUNC(rex_lhHeroStats_GetRenown);   // GetRenown   — the DISPLAYED value
PPC_EXTERN_FUNC(rex_lhGuiPauseMenu_IsTabAvailable);   // is this pause tab available — the guest's own predicate
PPC_EXTERN_FUNC(rex_CECAppearanceModifiers_GetModifierEffectTerm);   // appearance-modifier term, by kind
PPC_EXTERN_FUNC(rex_CECAppearanceModifiers_GetPreferenceTerm);   // preference term, by axis
PPC_EXTERN_FUNC(rex_CECAppearanceModifiers_GetHomeBonusTerm);   // home bonus
PPC_EXTERN_FUNC(rex_CECAppearanceModifiers_GetPurityOpinionTerm);   // purity/corruption opinion
PPC_EXTERN_FUNC(rex_lhWideString_Dtor);   // ~LHString — the WIDE release, what the filler uses on
                                 // every provider output (`sub_82214DD8` is the narrow one)
PPC_EXTERN_FUNC(rex_lhGuiAbilitiesScreen_OnMenuEvent);   // 14 abilities
PPC_EXTERN_FUNC(rex_lhGuiPauseRootScreen_OnMenuEvent);   //  0 pause root
PPC_EXTERN_FUNC(rex_lhGuiItemsScreen_OnMenuEvent);   // 15 items
PPC_EXTERN_FUNC(rex_lhGuiClothingScreen_OnMenuEvent);   // 16 clothing
PPC_EXTERN_FUNC(rex_lhGuiLogbookScreen_OnMenuEvent);   // 17 logbook
PPC_EXTERN_FUNC(rex_lhGuiOptionsScreen_OnMenuEvent);   // 18 options
PPC_EXTERN_FUNC(rex_lhGuiCrimeScreen_OnMenuEvent);   // 21 crime
PPC_EXTERN_FUNC(rex_lhGuiShopScreen_OnMenuEvent);   // 22 shop
PPC_EXTERN_FUNC(rex_lhGuiWorldMapScreen_OnMenuEvent);   // 23 world map
PPC_EXTERN_FUNC(rex_lhGuiGiftGivingScreen_OnMenuEvent);   // 25 gift giving
PPC_EXTERN_FUNC(rex_lhGuiScreen_OnMenuEventNavSoundsOnly);   // 26 furniture, 27 decoration, 24 co-op — one folded body

// The presentation the GUI is currently driving — `sub_823DCBE0(*(GUI+56))`.
PPC_EXTERN_FUNC(rex_lhGuiPresentationSet_GetCurrent);


namespace refii::vfs {

static bool ReadEnvFlag(const char* name, bool default_value) {
#if defined(_WIN32)
  char buf[32];
  const DWORD len = ::GetEnvironmentVariableA(name, buf, static_cast<DWORD>(sizeof(buf)));
  if (len == 0 || len >= sizeof(buf)) {
    return default_value;
  }
  return default_value ? (buf[0] != '0') : (buf[0] == '1');
#else
  const char* value = std::getenv(name);
  if (value == nullptr) {
    return default_value;
  }
  return default_value ? (value[0] != '0') : (value[0] == '1');
#endif
}

// Defined with the co-op helpers further down; the dispatchers above them need these.
// (A declaration OUTSIDE this namespace named a different, undefined function — and the
// generated library refused to load on `_Z12IsCoopJoinerPh`.)
bool IsCoopJoiner(uint8_t* base);
bool CoopSessionLive(uint8_t* base);
uint32_t CoopNetworkManager(uint8_t* base);
void CoopRepairNetworkSlot(uint8_t* base);
void CoopApplyHero(PPCContext& ctx, uint8_t* base, int player, uint32_t entity);
void HeapCheckAfter(uint8_t* base, const char* step);
bool EquipRecord(PPCContext& ctx, uint8_t* base, uint32_t entity, uint32_t id, std::string& report);
void CoopAttachHeld(PPCContext& ctx, uint8_t* base, int player, uint32_t entity);
void CoopKeepHostWeapons(PPCContext& ctx, uint8_t* base, uint32_t entity);
bool ApplyStats(PPCContext& ctx, uint8_t* base, uint32_t entity, const std::string& text);
bool ApplyFace(PPCContext& ctx, uint8_t* base, uint32_t entity, uint32_t record);
void CoopResetSession();

// The queue and the counters live in src/core/guest_tick_pump_state.cpp, which links into
// refii_vfs: the HOST posts and this side drains, so state beside the pump would be a copy the
// payload writes and the bridge never reads. Reached through the GuestTickPump* wrappers in
// vfs_hooks_private.h.

/// The front-end state object. `sub_826E0D60` dereferences it with NO null check —
/// `lwz r11, dword_83496AC0` then `lwz r9, 8(r11)` at 0x826E0E30 — so an action that runs
/// while the front end is down is a fault, not a no-op.
constexpr refii::guest::Addr kFrontEndState{0x83496AC0u};

/// The signed-in user index, and the mask of which pads are signed in. Read exactly as the
/// guest's own New Game guard reads them at 0x826E0064/0x826E0068.
constexpr refii::guest::Addr kSignedInUserIndex{0x83496AF8u};
constexpr refii::guest::Addr kSignedInUserMask{0x83496A70u};

/// saveMgr = *(*(kFrontEndState + 0xC) + 0x28), per 0x826E0084..0x826E008C.
constexpr uint32_t kGameStateOffset = 0x0Cu;
constexpr uint32_t kSaveManagerOffset = 0x28u;

/// The interned `BOY_SELECTED` / `GIRL_SELECTED` event ids, interned at static init as
/// `sub_82189140(literal) & 0x7FFFFFFF`. Read the id the guest computed; never recompute it.
constexpr refii::guest::Addr kEventBoySelected{0x8349B5A0u};
constexpr refii::guest::Addr kEventGirlSelected{0x8349B5A4u};

/// The save manager and the signed-in user, as the New Game branch reads them (0x826E0064,
/// 0x826E0084..0x826E008C); false when no profile is signed in (GUARD 1, 0x826E0078) or the front
/// end's state is not built.
bool SignedInSaveManager(uint8_t* base, uint32_t& saveManager, uint32_t& userIndex) {
  const uint32_t game = LoadGuestU32(base, kFrontEndState);
  if (game == 0) {
    return false;
  }
  userIndex = LoadGuestU32(base, kSignedInUserIndex);
  const uint32_t signedIn = LoadGuestU32(base, kSignedInUserMask);
  if (userIndex >= 32u || ((1u << userIndex) & signedIn) == 0) {
    return false;
  }
  const uint32_t state = LoadGuestU32(base, game + kGameStateOffset);
  saveManager = state == 0 ? 0u : LoadGuestU32(base, state + kSaveManagerOffset);
  return saveManager != 0;
}

/// Runs the guest's own storage-device selection, so the game that starts can be saved.
///
/// `StartNewGame` bypasses `GUI_FrontendRoot_MenuActionDispatch`, which is where the guest's
/// three guards live; the second (0x826E0094) is this call. Without it the device map at
/// `saveMgr+0x7EC` stays empty and the game can never write a save. The sign-in test is repeated
/// because the picker is pointless without it; the third guard, the slot limit, is
/// `ForgetDeviceAtSaveLimit`, run right after. Idempotent: the guest also calls this on its Load
/// Game branch (0x826E03A0).
bool SelectStorageDevice(PPCContext& ctx, uint8_t* base) {
  uint32_t saveManager = 0u;
  uint32_t userIndex = 0u;
  if (!SignedInSaveManager(base, saveManager, userIndex)) {
    return false;
  }

  // A gap on the guest stack, as StartNewGame claims: the callee frames below r1.
  const PPCContext saved = ctx;
  ctx.r1.u32 = (ctx.r1.u32 - 0x40u) & ~0xFu;
  ctx.r3.u32 = saveManager;
  ctx.r4.u32 = userIndex;
  ctx.r5.u32 = 1u;  // a3: compute the required size, as the guest passes at 0x826E0080

  rex_lhSaveManager_SelectStorageDevice(ctx, base);

  const bool selected = (ctx.r3.u32 & 0xFFu) != 0;
  ctx = saved;
  return selected;
}

/// The New Game guard's third test, with the guest's own calls (TU1 0x826E0134..0x826E01DC, disc
/// 0x826C8578..0x826C861C): with as many saves on the mapped device as the limit allows, the guest
/// erases the user's device mapping — which IS "continue without saving": no container is made, and
/// the in-game save path (`sub_82445398`, open-always) has no device to write to. Run after
/// `SelectStorageDevice`, which maps it.
void ForgetDeviceAtSaveLimit(PPCContext& ctx, uint8_t* base) {
  uint32_t saveManager = 0u;
  uint32_t userIndex = 0u;
  if (!SignedInSaveManager(base, saveManager, userIndex)) {
    return;
  }

  const PPCContext saved = ctx;
  const uint32_t frame = (ctx.r1.u32 - 0x40u) & ~0xFu;
  ctx.r1.u32 = frame;
  ctx.r3.u32 = saveManager;
  ctx.r4.u32 = userIndex;
  rex_lhSaveManager_CountSavedGamesOnDevice(ctx, base);
  const uint32_t count = ctx.r3.u32;
  const uint32_t limit = LoadGuestU32(base, kSaveLimit);
  ctx = saved;
  if (count < limit) {
    return;
  }

  // `stw r11, var_98(r1)` then `addi r4, r1, var_98` (0x826E01CC..0x826E01D4): the key is a word
  // on the stack, above the callee's frame.
  const uint32_t key = frame + 0x20u;
  StoreGuestU32(base, key, userIndex);
  ctx.r1.u32 = frame;
  ctx.r3.u32 = saveManager + kUserDeviceMap;
  ctx.r4.u32 = key;
  rex_lhUserDeviceMap_Erase(ctx, base);
  ctx = saved;
  REXLOG_INFO("Refii: game started without saving ({}/{})", count, limit);
}

/// Starts a new game, on the guest's own thread.
///
/// Not `GUI_FrontendRoot_MenuActionDispatch` (0x826DFD58): its "New Game" branch only reaches
/// the GENDER CARD — GotoSlide 1 on the `choosecard` element (0x826E021C), `screen+24 = 2`
/// (0x826E022C) — and the commit happens only once the presentation's Behavior element fires
/// BOY_SELECTED or GIRL_SELECTED back into the handler below. Godot draws that card itself
/// (`Engine_FrontEndCardChoice`) and posts the event here. Not `Frontend_SetStartGameTarget`
/// (0x82379838) either: that is the LOAD path and sets no entrance, scenario or quest.
///
/// The real commit is `sub_82379628` — world "Albion", level "BWSSlums", entrance
/// "QC010_ChildhoodStart", and an empty `g_PendingLoadSaveName`, which makes
/// `Game_InitDatabases` take the new-game branch. `sub_826E0D60` is its only caller and takes
/// an EVENT rather than a string. It must run on the guest thread: the commit allocates
/// (`sub_82319928` publishes a new in-game state) and the start-game globals are refcounted
/// LHStrings.
///
/// The arguments, from the disassembly:
///
///   826e0d70  lwz  r11, 4(r4)          ; the ONLY thing read from r4
///   826e0e1c  li   r5, -1
///   826e0e20  addi r3, r1, var_10      ; r3 OVERWRITTEN before any read on this path
///   826e0e38  lwz  r3, 0x50(r9)        ; the frontend object comes from the GLOBAL
///   826e0e3c  bl   sub_82379628
///
/// So `r3` is a genuine don't-care here and `r4` need only be a guest pointer whose word at
/// `+4` is the event id. Note this is true ONLY on the SELECTED branches — the three earlier
/// comparisons in the same function (`BOY_ACTIVE` and friends) write through `r3` at
/// `stw r11, 0x18(r3)`, so passing a junk r3 with one of those ids would corrupt the screen.
///
/// Returns false when the front end is not up, leaving the action queued.
bool StartNewGame(PPCContext& ctx, uint8_t* base, bool female) {
  const uint32_t frontend = LoadGuestU32(base, kFrontEndState);
  if (frontend == 0) {
    return false;
  }

  // Before the commit: the save manager records the device against the user, and the new game
  // reads it the first time it writes. Not fatal when it fails, as in the guest.
  if (!SelectStorageDevice(ctx, base)) {
    REXLOG_WARN(
        "Refii: no storage device selected; this game will not be able to save");
  } else {
    ForgetDeviceAtSaveLimit(ctx, base);
  }

  const uint32_t event =
      LoadGuestU32(base, female ? kEventGirlSelected : kEventBoySelected);
  if (event == 0) {
    // Static init has not interned the events yet. Queued rather than sent, because sending
    // id 0 would fall off the end of the compare chain and silently do nothing — the failure
    // that looks like success.
    return false;
  }

  // A frame of our own on the GUEST stack, claimed by lowering r1. `r4` is dereferenced by
  // guest code so the record must be a guest address, and it cannot sit below the current sp:
  // `sub_826E0D60` and `sub_82379628` frame down from r1 and would overwrite it.
  const uint32_t sp = ctx.r1.u32;
  const uint32_t frame = (sp - 0x40u) & ~0xFu;
  const uint32_t record = frame + 0x20u;

  StoreGuestU32(base, record + 0u, 0u);
  StoreGuestU32(base, record + 4u, event);

  // Whole-context save/restore: the pump stands inside `sub_822C1BD0`, whose own delegation
  // still needs its `r3`.
  const PPCContext saved = ctx;
  ctx.r1.u32 = frame;
  ctx.r3.u32 = 0u;  // don't-care, per the disassembly above
  ctx.r4.u32 = record;

  __imp__rex_lhGui_FrontendRoot_HandleGuiEvent(ctx, base);

  ctx = saved;

  // A game just started, so the saved-game count is stale; re-sampled when the front end is up.
  GuestTickPumpRequestSaveCount();
  return true;
}

/// The GUI object `dword_83496B08`: two writers through the same base constructor, the front
/// end's and the in-game one.
constexpr refii::guest::Addr kGuiObject{0x83496B08u};

/// THE IN-GAME SCREEN SET, AS THE GUEST'S OWN CALLS STATE IT (gui_stated_screens.cpp, the openers
/// in display_box_hooks.cpp, the screen setter in mod_events.cpp): false while the set is not up
/// or a box holds the modal latch — a box owns input while it is up, and its own lever answers.
/// The screen is in TU1 numbering, 31 when none is up.
bool StatedInGameScreen(uint32_t* out_screen_id) {
  uint32_t mode = 1u;
  if (refii_bridge_stated_pause(&mode, nullptr, nullptr) == 0u || mode != 1u) {
    return false;
  }
  if (out_screen_id != nullptr) {
    const uint32_t stated = refii_bridge_stated_screen_now();
    *out_screen_id = stated == 0xFFFFFFFFu ? 31u : stated;
  }
  return true;
}

/// The in-game screen owner `*(GUI + 28)`: the object the guest's own screen lookup, modal router
/// and exit are CALLED on. 0 when the GUI object up is not the in-game one — `dword_83496B08` has
/// two writers, and only the in-game object (vtable 0x82001610) has an owner there.
uint32_t InGameScreenOwner(uint8_t* base) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  const uint32_t gui = LoadGuestU32(base, kGuiObject);
  if (!plausible(gui) || LoadGuestU32(base, gui) != refii::guest::Address(0x82001610u)) {
    return 0u;
  }
  const uint32_t owner = LoadGuestU32(base, gui + 28u);
  return plausible(owner) ? owner : 0u;
}

/// The handler the in-game GUI holds for a screen (TU1 id), by the guest's own lookup
/// `lhGuiManager_FindScreenHandler(owner, id)` (0x8236FFC0, disc 0x823381B8): a find on the map at
/// `owner + 68` that answers 0 for an absent key and inserts nothing (0x8236fffc..0x82370034, disc
/// 0x823381f4..0x8233822c). An object to call or to hand to a guest call, never a value to mirror.
uint32_t InGameScreenHandler(PPCContext& ctx, uint8_t* base, uint32_t screen_id) {
  const uint32_t owner = InGameScreenOwner(base);
  if (owner == 0u) {
    return 0u;
  }
  PPCContext work = ctx;
  work.r3.u32 = owner;
  work.r4.u32 = refii::guest::ScreenId(screen_id);  // the guest's map is keyed by ITS id
  rex_lhGuiManager_FindScreenHandler(work, base);
  const uint32_t handler = work.r3.u32;
  return handler >= 0x10000u && handler < 0xC0000000u ? handler : 0u;
}

/// Takes one saved-game count, on the guest's thread, by calling the guest's own probe.
///
/// Gated on the front end being up, though the probe is not: that is when the guest itself asks
/// (`sub_826E2220`, at menu-spawn), and the SLOT LIMIT `dword_83319620` holds 150 in the image
/// until `sub_82378618` — the front-end enter — overwrites it (6; 10 after our post-hook in
/// frontend_stated.cpp), so count and limit must
/// come from the same side of that store.
///
/// No scratch frame here: `sub_82443308` takes no arguments and frames its own 46320 bytes
/// downward from r1 after an `RtlCheckStack12` probe. The context save/restore still applies.
void SampleSavedGameCount(PPCContext& ctx, uint8_t* base) {
  if (!GuestTickPumpWantsSaveCount()) {
    return;
  }
  if (!FrontEndUp()) {
    // Front end not up: left pending rather than answered with a zero (-1 is not 0).
    return;
  }

  // Cleared BEFORE the probe: a request made while it runs must survive.
  GuestTickPumpClearSaveCountRequest();

  const PPCContext saved = ctx;
  rex_lhSaveManager_CountSavedGamesAllDevices(ctx, base);
  const int32_t count = static_cast<int32_t>(ctx.r3.u32);
  ctx = saved;

  GuestTickPumpNoteSaveCount(count);
  REXLOG_INFO("Refii guest-tick pump: the guest reports {} saved game(s)", count);
}

/// The front-end root screen's save list, as `sub_82443048` leaves it.
///
/// Two parallel arrays, both indexed by the same selection:
///
///   this+84   begin of a vector<handle>, 4-byte stride — the save FILE NAMES
///   this+88   its end, so the count is (end - begin) / 4
///   this+148  begin of a vector of EMBEDDED 40-byte records — the ChapterSave metadata
///
/// Within a record, `+12` is the LevelName handle and `+24` is the compatibility verdict that
/// `sub_82441780` computed from VersionNumber and CurrentEpisode. The guest's own confirm
/// refuses a save whose `+24` is zero (0x826E0480), so this does too.
///
/// THOSE ARE THE TU1 OFFSETS. Without the title update the same fields are 12 lower and the
/// record is 36 bytes: see refii::guest::FrontEndMenuField and SaveRecordStride.
constexpr uint32_t kSaveNamesBegin = 84u;
constexpr uint32_t kSaveNamesEnd = 88u;
constexpr uint32_t kSaveRecordsBegin = 148u;
constexpr uint32_t kSaveRecordLevelName = 12u;
constexpr uint32_t kSaveRecordCompatible = 24u;

/// THE WORLD HELD WITHOUT A SCREEN ("WorldHold:on" / "WorldHold:off"; photo mode). The game holds
/// its world under the pause book by pushing a `CGameModePause` GameFlow command
/// (lhGuiManager_EnterGuiMode, TU1 0x8236ed2c..0x8236ed5c: `alloc(0x14)`, `CGameModePause_ctor`,
/// `GameFlow_QueueCommand(flow, cmd)` with flow = `*(*(*(*0x83496AB8+0xC)+0x80)+4)`) and lets it
/// go with `sub_82318800(flow, 1)` (lhGuiManager_ExitScreen 0x8236ee58..0x8236ee70). Only the top
/// command runs its world update; the pause's is empty, so `dword_83319650` (Timing.GetWorldFrame)
/// stops while the engine's frame, the GUI and audio go on — which withholding the host's step
/// grants never achieved: the clock's watchdog resumes time after eight missing steps. Pushed only
/// when the top is not already a pause (the game's own book would not push either, 0x8236ed1c),
/// popped only while ours is the top of the stack it was pushed on: a book opened over it pops it
/// itself on its exit, and a level teardown takes it with the stack.
constexpr const char* kActionWorldHoldOn = "WorldHold:on";
constexpr const char* kActionWorldHoldOff = "WorldHold:off";
constexpr uint32_t kGameModePauseId = 1u;
uint32_t g_world_hold_flow = 0u;

/// The GameFlow command stack, or 0 out of a game.
uint32_t GameFlowStack(uint8_t* base) {
  const auto step = [&](uint32_t at, uint32_t offset) -> uint32_t {
    return LooksLikeGuestPointer(at) ? LoadGuestU32(base, at + offset) : 0u;
  };
  const uint32_t root = LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t flow = step(step(step(root, 0xCu), 0x80u), 0x4u);
  return LooksLikeGuestPointer(flow) ? flow : 0u;
}

/// The id of the command on top of `flow` (its vtable slot 1, as sub_82318800 asks it), or ~0u.
/// The list's sentinel is `flow+4`, its count `flow+8`, the top the sentinel's prev, the command `+8`.
uint32_t TopGameFlowCommand(PPCContext& ctx, uint8_t* base, uint32_t flow) {
  if (LoadGuestU32(base, flow + 8u) == 0u) {
    return ~0u;
  }
  const uint32_t sentinel = LoadGuestU32(base, flow + 4u);
  const uint32_t node = LooksLikeGuestPointer(sentinel) ? LoadGuestU32(base, sentinel + 4u) : 0u;
  const uint32_t command = LooksLikeGuestPointer(node) && node != sentinel ? LoadGuestU32(base, node + 8u) : 0u;
  if (!LooksLikeGuestPointer(command)) {
    return ~0u;
  }
  PPCContext call = refii::mods::Frame(ctx);
  return refii::mods::CallVirtual(call, base, command, 1u) ? call.r3.u32 : ~0u;
}

bool WorldHold(PPCContext& ctx, uint8_t* base, bool on) {
  const uint32_t flow = GameFlowStack(base);
  if (flow == 0u) {
    g_world_hold_flow = 0u;
    REXLOG_INFO("Refii world hold: no GameFlow stack (not in a game) - '{}' dropped", on ? "on" : "off");
    return true;
  }

  const uint32_t top = TopGameFlowCommand(ctx, base, flow);
  if (on) {
    if (g_world_hold_flow == flow && top == kGameModePauseId) {
      return true;
    }
    if (top == kGameModePauseId) {
      g_world_hold_flow = 0u;
      REXLOG_INFO("Refii world hold: the game's own pause is already on top - nothing pushed");
      return true;
    }
    const uint32_t command = refii::vfs::CallLhMalloc(ctx, base, 0x14u);
    if (!LooksLikeGuestPointer(command)) {
      REXLOG_WARN("Refii world hold: no guest memory for the pause command - the world is not held");
      return true;
    }
    PPCContext work = refii::mods::Frame(ctx);
    work.r3.u32 = command;
    rex_CGameModePause_ctor(work, base);
    work = refii::mods::Frame(ctx);
    work.r3.u32 = flow;
    work.r4.u32 = command;
    rex_GameFlow_QueueCommand(work, base);
    g_world_hold_flow = flow;
    REXLOG_INFO("Refii world hold: CGameModePause pushed over command {} - the world stands", top);
    return true;
  }

  const bool ours = g_world_hold_flow == flow;
  g_world_hold_flow = 0u;
  if (!ours || top != kGameModePauseId) {
    REXLOG_INFO("Refii world hold: nothing of ours to pop (top command {}) - left as the game has it", top);
    return true;
  }
  PPCContext work = refii::mods::Frame(ctx);
  work.r3.u32 = flow;
  work.r4.u32 = kGameModePauseId;
  rex_GameFlow_PopCommand(work, base);
  REXLOG_INFO("Refii world hold: CGameModePause popped - the world runs");
  return true;
}

/// Loads the save at `index`, by handing the guest the two handle slots it wants.
///
/// The addresses ARE the arguments: `Frontend_SetStartGameTarget` takes pointers to live handle
/// slots inside the guest's own vectors, so this passes `begin + 4*index` and
/// `records + 40*index + 12` — nothing to allocate and no scratch frame to claim.
///
/// Returns false when the front end is down or the list cannot support the index, leaving the
/// action queued rather than calling with a bad pointer.
bool LoadSavedGame(PPCContext& ctx, uint8_t* base, uint32_t index) {
  // DROPPED ONCE THE GAME IS UP. `byte_83496A5E` is the guest's own in-game flag (set by
  // Game_InitDatabases at 0x82311648, cleared by the game mode's shutdown). A load that was
  // still held when the game came up by another route is stale; held on, it fired the moment
  // Quit to Title brought the front end back with its save vectors filled, and re-loaded the
  // save from the title screen.
  if (base[refii::guest::Address(0x83496A5Eu)] != 0u) {
    REXLOG_INFO("Refii guest-tick pump: LoadSave:{} dropped — the game is already up", index);
    return true;
  }

  // The main menu its own activate stated; the two vectors are read only for the handle slots
  // the call takes, and the compatibility byte the guest's own confirm tests before it.
  const uint32_t screen = FrontEndMenuScreen();
  if (screen == 0) {
    return false;
  }

  const uint32_t begin = LoadGuestU32(base, screen + refii::guest::FrontEndMenuField(kSaveNamesBegin));
  const uint32_t end = LoadGuestU32(base, screen + refii::guest::FrontEndMenuField(kSaveNamesEnd));
  const uint32_t records = LoadGuestU32(base, screen + refii::guest::FrontEndMenuField(kSaveRecordsBegin));
  if (begin == 0 || end <= begin || records == 0) {
    // The vectors are filled only inside the dispatcher's own "Load Game" branch, so an empty
    // list here means that branch has not run yet — held, not failed.
    return false;
  }

  const uint32_t count = (end - begin) / 4u;
  if (index >= count) {
    REXLOG_INFO("Refii guest-tick pump: save {} is out of range (the guest lists {})", index,
                count);
    return true;  // Dropped rather than retried forever; the list will not grow on its own.
  }

  const uint32_t record = records + (index * refii::guest::SaveRecordStride());
  if (base[record + kSaveRecordCompatible] == 0) {
    // The guest's own gate at 0x826E0480. A save this byte rejects is the one that raises
    // GUI_CORRUPT_SAVE_GAME_WARNING rather than loading.
    REXLOG_INFO("Refii guest-tick pump: save {} is marked incompatible — refused", index);
    return true;
  }

  const PPCContext saved = ctx;
  ctx.r3.u32 = 0u;  // never referenced by the callee
  ctx.r4.u32 = begin + (index * 4u);
  ctx.r5.u32 = record + kSaveRecordLevelName;

  rex_lhFrontEnd_SetStartGameTarget(ctx, base);

  ctx = saved;
  return true;
}

/// Performs a menu entry the way the guest performs it: by NAME, through its own dispatcher.
/// `sub_826DFD58` is a strcmp chain over the front end's own item names ("Load Game",
/// "Downloadable Content", "SubtitlesOn", "SubtitlesOff"); an unknown name matches nothing.
///
/// The record is three words and needs no guest object: `a2+0` points at a string body whose
/// own `+0` is a `char*`, `a2+4` is a command id, `a2+8` an extra param. The dispatcher
/// dereferences `a2[0]` once (`lwz r3, 0(r28)` at 0x826DFFEC feeding the strcmp at 0x826E0040)
/// and never retains or frees the record, so a scratch frame on the guest stack suffices.
///
/// `a2+4` must not be 302, 141 or 140: those are consumed by a switch ahead of the name chain
/// (302 Language, reading `a2+8`; 140/141 the menu slide sounds). Zero falls through.
///
/// Returns false when the front end is not up, leaving the action queued.
/// The dispatcher's three ID-based cases, decided BEFORE the name chain is reached.
///
/// 140/141 are the menu's slide sounds — "SE_GUI_SLIDE_MENU_UP" (`dword_83497DD8`) and
/// "SE_GUI_SLIDE_MENU_DOWN" (`dword_83497DDC`) on the emitter at `*(*(dword_83496AC0+8)+0x50)`.
/// 302 is Language: it reads `a2+8` as the index into the guest's language table and is the
/// only route to that action — the language entries never appear in the name chain.
constexpr uint32_t kCommandNone = 0u;
constexpr uint32_t kCommandSetLanguage = 302u;
// The dispatcher's ordinary item activation: `switch (a2[1])` case 2 string-compares the item
// NAME (`GUI_FrontendRoot_MenuActionDispatch`, 0x826DFD58). Every root entry spawns with this
// id — `sub_826E2220` passes 2 for all of them.
constexpr uint32_t kCommandActivateItem = 2u;

// Subtitles sampling edges — see the sampler at the end of OnGuestTick.
/// "Language:<index>" — the index rides in the name because the queue carries only names. The
/// guest spawns one language entry per installed language with command 302 and the row index.
constexpr const char* kActionLanguagePrefix = "Language:";

/// "PauseSaving:<player>:<row>" — one of the pause menu's Saving rows as the ported root answers
/// it (gui_screen_stub_pause_root.cpp DispatchPauseSaving): 0 save-and-continue, 1 save-and-quit,
/// 2 quit-without-saving.
constexpr const char* kActionPauseSavingPrefix = "PauseSaving:";

/// "PauseHenchmanQuit:<player>" — player two's Quit row (gui_screen_stub_pause_root.cpp
/// DispatchPauseHenchmanQuit): the guest's own henchman quit box, 26 or 27.
constexpr const char* kActionPauseHenchmanQuitPrefix = "PauseHenchmanQuit:";

/// "PauseTab:<tab>" — a pause tab picked: 1 root, 2 map, 3 abilities, 4 items, 5 weapons,
/// 6 clothing, 7 logbook, 8 options. The guest's own tab request ends in `sub_826B89E0(tab)`
/// when the tab is not the one the strip is on (`cmpw cr6,r3,r11; beq; bl sub_826B89E0`
/// 0x8229ad14); this makes that call under the same rule, on the pause root with no box up.
constexpr const char* kActionPauseTabPrefix = "PauseTab:";

/// "Rumble:<0|1>" — the rumble switch through its one writer, the `SetRumbleEnabled` native
/// (`stb r3, byte_8331924F` 0x823b3a7c).
constexpr const char* kActionRumblePrefix = "Rumble:";

/// "QuickWheel:open|close|select|back|release|point:<i>" — the expression wheel through the
/// guest's own routines, because the GameAction chain that would run them (40 hold / 41
/// release / 36 accept / 37 back / 25 end-held, `sub_824040B8` and `sub_82405190`) does not
/// fire here. Every gate stays the guest's: `sub_8236F400` (hero alive, not busy, player mode
/// 1/5/20/21/22), `QM+35` (open), `W+0` (state), and each routine's own cursor tests. The
/// STICK needs no lever — `sub_82402F70` routes the raw left-stick event to `sub_826AA060`
/// itself, gated on `QM+35`; `point:<i>` is the fallback that feeds the same routine the unit
/// vector of sector i (angle 3π/4 − iπ/4, 0 = NE clockwise to 7 = N).
/// "rb:press" / "rb:release" are RB's own GameActions 40 / 41 (`sub_824040B8`), the input map's toggle
/// byte deciding hold or toggle as it does there (see DispatchQuickWheel).
/// "QuickWheel:2:<verb>" is local player two's: the wheel opens with player two's Player (it
/// stores the opener's id at W+24, `sub_826A9A60`), and every other verb acts only on a wheel
/// that player owns — each pad drives its own wheel, never the other's.
constexpr const char* kActionQuickWheelPrefix = "QuickWheel:";

/// "SpellsMenu:[2:]open|close" — a DEBUG VERB, not the mechanism. No prefix acts for this machine's
/// local player (LocalPlayer), "2:" for local player two (PlayerWithId 2), as QuickWheel's.
///
/// THE PREMISE THIS SHIPPED ON IS FALSE. It said "no GameAction arrives at the consumer at all",
/// so the host had to drive 42/43 itself. But `sub_8236F788` has exactly ONE code caller in the
/// image — 0x82404290, inside the GameAction dispatcher `sub_824040B8` case 0x2A — and the only
/// non-zero writers of the open flag QM+36/+37 are its own stores at 0x8236f814/0x8236f81c.
/// The flag goes set in a live run while this lever logs nothing but refusals, so the GUEST is
/// running 42/43 on its own and these two verbs RACE it. See the note in DispatchSpellsMenu for
/// what that race costs. Keep them for driving the blade from the debug channel; do not treat
/// them as the way the feature works. The shipped scheme binds 42 to XboxButton 16,
/// the RIGHT TRIGGER: `GameControlsCombatOnRelease` is an @alias onto a scheme that overrides
/// `GameActionGUISpellsMenu`'s input over a base of 10 (RB). That pairing is GDB data — the
/// binding vector ships EMPTY in `sub_8244B678` — so it cannot be read out of the executable.
constexpr const char* kActionSpellsMenuPrefix = "SpellsMenu:";

/// "SpellBlade:[2:]up|down|left|right|release|copydown" — one input into the QUICK SPELL BLADE,
/// the five-power-level widget the right trigger opens over the world.
///
/// IT IS WIRED ON THE ABILITIES SCREEN AND NOWHERE ELSE. `sub_826C2958` routes that screen's
/// input into `sub_826B5430(screen14 + 200, ...)` at 0x826c29a0, which `DispatchScreenAction`
/// reaches. The quick blade is the SAME class at `*(*(GUI+32)+92)` (player 1) / `+96` (player
/// 2), standing over the running game with no screen up — so `StatedInGameScreen` refuses it and
/// nothing else here can address it. `sub_82405190`, the guest's own route, needs an object
/// whose vtable slot 16 returns the player index; we have no such object and will not fabricate
/// one. `sub_826B5430` needs neither a vtable nor a player index, so it is the first
/// host-addressable link.
///
/// THE CODES ARE THE GUEST'S OWN GAMEACTION IDS, AND THE STICK IS NOT ONE OF THEM. `sub_826B5430`
/// switches on what `sub_828818E8(this + 132, code)` RETURNS (0x826b5450 / 0x826b5474): 28 UP
/// `++row`, 29 DOWN `--row`, 30/31 move the carousel and commit, 36 A copies down. Code 26, the
/// left stick, is resolved into a direction ONLY while the latch byte at widget+165 is set
/// (`if (!*((_BYTE *)a1 + 33)) goto LABEL_17;` at 0x82881914) — and the constructor
/// `sub_826B4358` writes that byte 1 at 0x826b43d4 and then 0 at 0x826b4410 (`stb r10, 0xA5(r3)`,
/// r10 the zero register), so it is compiled off for this widget and a 26 falls through to
/// nothing. A `stick` post moved the box exactly never. The guest moves it on the held d-pad ids,
/// which is what these verbs hand over.
///
/// POST THE HELD DIRECTION EVERY FRAME, AND `release` ONCE WHEN IT LETS GO. The repeat is the
/// widget's own latch (`sub_82881B28`: the first press lands at once, the second after 0.3 s,
/// then every 0.1 s, 0x826b43f0..0x826b4440), so the host keeps no clock; a second 28 inside
/// the window returns 0 (0x82881a48). Ids 32..35 reset the latch (0x82881a88..0x82881a9c), so
/// the next press after a release lands at once again, as the guest's would.
///
/// THE OPEN BYTE IS NOT TIDINESS. `*(HUD+92)` is allocated once at GUI construction and nulled
/// only in the destructor — open and close never touch it — but the row ceiling at widget+412
/// and the cell tables at widget+180..+408 are written ONLY by `sub_826B4848`, which runs as
/// part of opening. A plausible pointer over uninitialised tables is what the gate is for.
constexpr const char* kActionSpellBladePrefix = "SpellBlade:";

/// "RegisterPlayer" — the guest's own `sub_82371860(GUI, player)`, run only while the in-game
/// GUI reports NO registered player (`*(u8*)(GUI+0x65) == 0`).
///
/// That byte gates the whole Gameface update: `sub_821A1140` advances every presentation's
/// timeline and drains the event queue only while `byte_83319409 && *(GUI+0x65)`. The guest
/// increments it from `sub_82314D30`, which also binds the player into the action consumer
/// (`sub_825E8DE0`/`sub_823D2508`) — the bound player at consumer+12 the GameAction chain needs.
constexpr const char* kActionRegisterPlayer = "RegisterPlayer";

/// "CoopHeroBody:<male|female|child|femalechild|henchman...>" — retype player two's entity.
///
/// STAGE 1 of the native-hero work (docs/coop_native_hero_plan.md). The shipped game spawns
/// player two from one of eight fixed henchman creatures (`sub_8233C508`) and has no path for a
/// visitor's own hero at all. The body, though, is one guest call: `sub_82389FB8` is
/// `Player::ChangeEntityType`, the same entry the shipped debug menu drives as
/// `Player.ChangePlayerEntityType`, and it already tells the co-op manager (0x8233CB18).
///
/// This answers the one question nothing else can: whether a SECOND `CreatureHero` spawns
/// cleanly. Player one is EntityUID 1 and player two would be 2 through the same GDB path, and
/// the shipped game never spawns two.
constexpr const char* kActionCoopHeroBodyPrefix = "CoopHeroBody:";
/// "CoopSplitScreen:<0|1>" — whether each local player has their own camera now: the shell's split
/// (Refii.Game.GameSplitView.GuestSplit: split mode, or hybrid while split), read by the co-op hooks
/// (coop_hero_hooks.cpp) and the camera Lua (Refii.split_screen) every time they ask.
constexpr const char* kActionCoopSplitScreenPrefix = "CoopSplitScreen:";
/// `CoopPickSave:<N>` — player two's save picker commits save N; `CoopPickCursor:<N>` puts the
/// picker's own cursor on save N. See DispatchCoopPick.
constexpr const char* kActionCoopPickSavePrefix = "CoopPickSave:";
constexpr const char* kActionCoopPickCursorPrefix = "CoopPickCursor:";
/// `CoopLeave` — player two leaves the game (Fable 3's "Leave"): see DispatchCoopLeave.
constexpr const char* kActionCoopLeave = "CoopLeave";
/// `CoopKick` — the online host removes player two (Fable 3's "Kick"): see DispatchCoopKick.
constexpr const char* kActionCoopKick = "CoopKick";
/// `CoopExitUse:<x>:<y>:<z>` — the online joiner used the region exit standing at that guest
/// position; the host's exit update fires it (coop_hero_hooks.cpp, RefiiCoopTakeExitUse).
constexpr const char* kActionCoopExitUsePrefix = "CoopExitUse:";
/// `ModWeather:<from_host 0|1>|<seconds>|<level>|<weather>|<previous>` — the dynamic weather in
/// effect changed on the host side (Engine_DynamicWeatherChoice); raised as the DynamicWeather mod
/// event (mod_events.cpp RaiseDynamicWeather).
constexpr const char* kActionModWeatherPrefix = "ModWeather:";

/// "HeroState:<1|2>:<base64>" — one player's WHOLE hero as the HeroState binary v1
/// (little-endian, magic "HST1"), standard base64 with padding, in ONE action. The pump
/// decodes it (DispatchHeroState) into the per-player stores the apply steps read; the version
/// is the generation CoopApplyHero latches per body, so a changed state on the joiner is a new
/// version and re-applies, while a restated identical one does nothing. Layout, in order:
///   u32 magic 'H','S','T','1'; u32 version; u8 flags (bit0 female, bit1 hasLooks, bit2 known);
///   u8 nameLen, nameLen bytes of UTF-8 (<= 48); u32 face (0 none); f32 fat, height, tan;
///   f32 strength, skill, will; f32 morality, purity; i32 renown, money;
///   u8 rankCount, i32[rankCount] ability ranks, ids 0..14 in order; u8 expCount, f32[expCount];
///   u8 totalCount, f32[totalCount]; u32 melee, u32 ranged (drawn weapon records, 0 none);
///   u16 wornCount, u32[wornCount] worn def hashes; u16 invCount, (u32 record, u16 count)[];
///   then, when present, the dog: u32 definition (0 none), u8 searching, u8 combat,
///   u8 trickCount, (u8 len, len bytes of a trick's GDB name)[trickCount],
///   u8 wornCount, u32[wornCount] the dog's worn def hashes, u8 nameLen, its given name (UTF-8);
///   then, when present, the looks extension: u32 magic 'H','S','L','1'; u16 dyeCount,
///   (u32 worn record, u8 slots, per set bit — slot 1 then slot 2 — u8 red, u8 green, u8 blue,
///   f32 brightness)[dyeCount]; u8 scarCount, (u32 BodyArea, u32 TextureMorphRecordID,
///   u8 Permanent)[scarCount]. Bytes past what a reader knows are ignored, so an older pump
///   takes a newer record and a newer pump an older one.
/// A malformed payload (bad magic, short buffer, a count past the end, version 0) is logged
/// once and ignored; the stores keep the last good version.
constexpr const char* kActionHeroStatePrefix = "HeroState:";

/// `CoopHeroHeld:<1|2>:<meleeNetId>,<rangedNetId>` — net ids of the item entities the host's
/// player holds; a client attaches the replicas (CoopAttachHeld). Stored; the tick attaches.
constexpr const char* kActionCoopHeroHeldPrefix = "CoopHeroHeld:";

/// "CoopHeroReset" — no payload: drop every stored hero state, outfit, inventory, held pair,
/// the own-hero bit, every per-body latch and the applied versions. Godot posts it on peer
/// loss and on a role change; the pump also runs it on the session's live→not-live edge.
constexpr const char* kActionCoopHeroReset = "CoopHeroReset";

/// `Card:<p>:dismiss|equip` — the received-item card's Done and Equip now, see
/// gui_screen_stub_receive_items.cpp DispatchCard.
constexpr const char* kActionCardPrefix = "Card:";

/// "MenuBoxSelect:<index>" — move the MenuBox highlight to a 0-based option, the guest's own
/// way, so the drawn card follows the host highlight.
constexpr const char* kActionMenuBoxSelectPrefix = "MenuBoxSelect:";

/// "MenuBoxAnswer:<n>" — n == 0 cancels the MenuBox; n >= 1 accepts option n-1. The guest's
/// accept/close does the publish (player message 35 subcode 101) and the teardown itself.
constexpr const char* kActionMenuBoxAnswerPrefix = "MenuBoxAnswer:";

/// "BoxAction:<player>:<code>[@<box>]" — one action code to whichever display box is up — see
/// DispatchDisplayBoxAction. The player is TakeActingPlayer's.
constexpr const char* kActionBoxActionPrefix = "BoxAction:";

/// "FrontEndDialog:<pad>:<code>" — answers the front end's own `dialog` box (36 accept, 37 cancel)
/// through the owning screen's input slot, for the pad that pressed. See DispatchFrontEndDialog.
constexpr const char* kActionFrontEndDialogPrefix = "FrontEndDialog:";

/// "TitleStart:<pad>" — the title's A, for the pad that pressed it. See DispatchTitleStart.
constexpr const char* kActionTitleStartPrefix = "TitleStart:";

/// "FrontEndBack:<pad>" — the main menu's B where no host panel answers it. See DispatchFrontEndBack.
constexpr const char* kActionFrontEndBackPrefix = "FrontEndBack:";

/// "ScreenAction:<player>:<code>" — one action code to the CURRENT in-game screen's own input method
/// (vtable+24): 36 select, 37 back, 39 the per-screen Y action, 72/73 tab prev/next.
constexpr const char* kActionScreenActionPrefix = "ScreenAction:";

/// "QueryAnswer:<player>:<code>[@<id>]" — answer the pending query box through the guest's own input router
/// (`sub_8236E458`). The CODE is the guest's action id, chosen host-side PER BOX ID: 46/47
/// for the saving/shop/real-estate confirms, 36/37 for the henchman and coop boxes, 39 for
/// the fast-travel confirm, 75 for the shop box's equip action. The router forwards it to
/// the UNDERLYING screen's handler — the openers never change the screen id — so this one
/// lever answers a box over any screen.
constexpr const char* kActionQueryAnswerPrefix = "QueryAnswer:";

/// "MenuEvent:<name>:<type>:<id>" — commit a menu row BY IDENTITY: the 12-byte
/// `{LHString name, int32 type, int32 id}` the guest's own pump `sub_82370450` would have pulled
/// from `GUI:PullEvent()` (`sub_826C1810`), delivered to the current screen's `vtable+20` as that
/// pump does (`sub_8236FFC0(*(GUI+28), *(*(GUI+56)+4))`, `bctrl` at 0x823705E0) — see
/// `DispatchMenuEventDirect`. The guest's Lua queue behind that pump is cut with its UI Lua
/// (`EventQueueSize` reads 0, gui_menu_hooks.cpp), so this is the only commit path.
///
/// `id` is the FULL 32-bit row id, as `sub_826CE8D0` split it into `Float1`/`Float2` at spawn.
/// Ghosted rows are the caller's problem: the shipped `SelectMenuItem`'s ghost test is the host's.
constexpr const char* kActionMenuEventPrefix = "MenuEvent:";

/// `Inventory:<op>:<recordHash>` — see DispatchInventoryAction.
constexpr const char* kActionInventoryPrefix = "Inventory:";

/// `Shop:<p>:close` — the shop's B (gui_screen_stub_shop.cpp). `Gift:<p>:close` — the
/// gift screen's B, and `Gift:<p>:item:<ticket>:<record>` — a gift row's A: the gift screen's own row
/// answer on that ticket (CommitItemThroughScreen; its confirm, commit and exit are the guest's).
constexpr const char* kActionShopPrefix = "Shop:";
constexpr const char* kActionGiftPrefix = "Gift:";

/// `Items:<p>:…` / `Clothing:<p>:…` — the item books' rows, see DispatchItemLever.
constexpr const char* kActionItemsPrefix = "Items:";
constexpr const char* kActionClothingPrefix = "Clothing:";

/// `InventoryFor:<player>:<op>:<recordHash>` — the same, on player 1's or 2's own hero when their
/// menus are not the open ones (local co-op); see DispatchInventoryAction's `addressed`.
constexpr const char* kActionInventoryForPrefix = "InventoryFor:";

/// `Ability:<p>:learn|unlearn:<index>` — one level of one of the fourteen abilities bought or given
/// back for player p, see gui_screen_stub_abilities.cpp DispatchAbility.
constexpr const char* kActionAbilityPrefix = "Ability:";

/// `Furniture:<p>:upgrade:<level>` — the furniture screen's upgrade on a stated level, and
/// `Furniture:<p>:close`, its B; see DispatchFurnitureAction.
constexpr const char* kActionFurniturePrefix = "Furniture:";

/// `Decoration:<p>:mount:<ticket>|takedown|close` — the decoration screen's A on a stated row, its Y
/// and its B (gui_screen_stub_decoration.cpp).
constexpr const char* kActionDecorationPrefix = "Decoration:";

/// `Crime:<p>:pay|resist|service` — a crime row's A (gui_screen_stub_crime.cpp). `Query:<p>:close|rename`
/// — the villager card's B and Y (gui_screen_stub_query.cpp). Each only while its own screen stands
/// with no box over it.
constexpr const char* kActionCrimePrefix = "Crime:";
constexpr const char* kActionQueryPrefix = "Query:";

/// `SafetyToggle:<p>` and `TargetQuery:<p>` — the HUD's two levers in play (hud_levers.cpp).
constexpr const char* kActionSafetyTogglePrefix = "SafetyToggle:";
constexpr const char* kActionTargetQueryPrefix = "TargetQuery:";

/// `RealEstate:<p>:event:<110..120>` — a real-estate row's commit by its stated event, and
/// `RealEstate:<p>:close`, the screen's B (gui_screen_stub_real_estate.cpp). (`Estate:` is the online
/// joiner's intent, replayed on the host.)
constexpr const char* kActionRealEstatePrefix = "RealEstate:";

/// THE IN-GAME GUI'S OWN ACTIONS run inside the guest's Lua window, not before the tick body.
///
/// `sub_82426470` executes a posted chunk only while `GUI+0x64` is set; the game loop
/// (`sub_82276D10` @0x82277130 → `sub_821A1140` @0x821a11d8) sets it at the start of a game
/// tick and the GUI frame update (`sub_82286FD8`, in-game GUI vtable slot 2) clears it
/// @0x82287170 AFTER running the GUI tick `sub_8229ABD8`. `rex_lhTickDelegate_ForwardToInner`
/// runs before the tick body, when the byte is clear, so a chunk posted there was refused and
/// `MenuSelect` was "dropped" on every press. These actions are drained from the GUI tick hook
/// (`rex_lhGuiManager_TickUpdate`) instead, where the guest's own menu Lua runs; the handler
/// calls (`vtable+20`, `+24`, `+28`) ride the same pass so a highlight lever and the press that
/// follows it land in order, in one tick.
bool IsInGameUiAction(const std::string& name) {
  static constexpr const char* kPrefixes[] = {
      "MenuEvent:",   "ScreenAction:", "QueryAnswer:",
      "BoxAction:",   "Card:", "Shop:", "Gift:", "MenuBoxSelect:", "MenuBoxAnswer:", "PauseTab:",
      "ButtonBox:",   "AcceptHold", "PauseBack", "PauseCycle:", "CreditsEnd", "Options:",
      "PauseClose:",  "Furniture:", "Decoration:", "RealEstate:", "Crime:", "Query:",
      refii::vfs::kKeyboardAnswerAction, refii::vfs::kKeyboardCancelAction,
      kActionCoopPickSavePrefix, kActionCoopPickCursorPrefix,
  };
  for (const char* prefix : kPrefixes) {
    if (name.rfind(prefix, 0) == 0) {
      return true;
    }
  }
  return false;
}

/// THE CO-OP SCREEN'S PRESSES (`CoopAction:`) are delivered where the
/// guest's own input dispatch delivered the pad's — the flow tick's entry, as Start — and are not
/// held to the in-game GUI: a joiner arriving from the title has the screen under the front end's
/// GUI (DispatchCoopAction finds the handler either way).
bool IsCoopScreenInput(const std::string& name) {
  return name.rfind("CoopAction:", 0) == 0;
}

/// THE PLAYER'S START is delivered where the guest's own input dispatch delivers it: `lhEngine_FrameUpdate`
/// (0x821C8FB0 / disc 0x821C8F08) runs the action handlers `sub_821CCED8` at 0x821C9020 (`CGUIInput_vf6` →
/// `sub_82403920` → `bl sub_82371B10`, r5 = 0, at 0x82403D50) and then the game-flow tick at 0x821C902C —
/// every sim step, whatever state is on top. The GUI tick is skipped under a full cutscene (0x82287054
/// `cmpwi cr6,r3,7`), so draining Start there held the press until the cutscene ended and then opened
/// the book; the guest's own Start under state 7 opens nothing (r31 = 1 at 0x82371C7C: no lite box at
/// 0x82371CA4, no book at 0x82371CEC). Drained at the flow tick's entry (rex_lhGameFlow_Tick), once.
bool IsInputAction(const std::string& name) {
  return name.rfind("Start:", 0) == 0 || IsCoopScreenInput(name);
}

/// Takes the `<player>:` that leads the levers answering a handler — `QueryAnswer:`, `BoxAction:`,
/// `ScreenAction:`, `Card:` — and returns it, 1 or 2; 0 when it is absent or out of
/// range. It is the handler's r5, which the guest fills from its GUI input object's vf+16
/// (`sub_822A4E38` player one: 2 on an online joiner, else 1; `sub_828EDC88` player two: 2), read at
/// 0x82404A90 for the modal router and 0x82404AC4 for the box chain. Godot states it
/// (Engine_UiSource.ActingPlayer).
uint32_t TakeActingPlayer(std::string& text) {
  if (text.size() < 2u || text[1] != ':' || (text[0] != '1' && text[0] != '2')) {
    return 0u;
  }
  const uint32_t player = static_cast<uint32_t>(text[0] - '0');
  text.erase(0, 2);
  return player;
}

/// "SpellAssign:<slot>:<spell>[:<player>]" — put a will on one of the five d-pad slots, of
/// player 1's hero or (player 2) the co-op partner's.
///
/// The guest's widget commits on LEFT/RIGHT, not on A: `sub_826B5330` (code 31) moves the cell
/// cursor and calls the writer in the same breath, `sub_826B5240` (code 30) mirrors it. A
/// (`sub_826B4EF8`, code 36) instead copies the current row's spell into every LATER row that
/// also offers it.
///
/// `slot` is the row, 0..4, and `spell` is the guest's own 0..7 will index — not an ability id
/// and not a GDB row. Both are the numbers `sub_826B4848` builds its table from.
constexpr const char* kActionSpellAssignPrefix = "SpellAssign:";

/// "LoadSave:<index>" — pick a save out of the list the guest enumerated. An index, not a name:
/// the guest identifies a save by the ADDRESS of its handle slot inside its own vector, and the
/// index is the join it uses itself (`this+72`, moved by its own left/right handler).
constexpr const char* kActionLoadSavePrefix = "LoadSave:";

/// "PresentationEvent:<presentation>:<parent element>:<EVENT>" — a behaviour element's
/// `onDeactivate` `fireEvent(EVENT, parent)`, timed by Godot's playback of the presentation and
/// delivered to the callback the guest registered for it (hooks/gui_menu_hooks.cpp).
constexpr const char* kActionPresentationEventPrefix = "PresentationEvent:";

bool DispatchByName(PPCContext& ctx, uint8_t* base, const std::string& name,
                    uint32_t command = kCommandNone, uint32_t param = 0u) {
  // DROPPED ONCE THE GAME IS UP, as LoadSavedGame is: `byte_83496A5E` is the guest's in-game
  // flag (Game_InitDatabases, 0x82311648). A front-end action still held when the game came up
  // can never run — the front end is gone — and re-deferring it every tick held it for the whole
  // session, to fire on Quit to Title.
  if (base[refii::guest::Address(0x83496A5Eu)] != 0u) {
    REXLOG_INFO("Refii guest-tick pump: front-end action '{}' (command {}) dropped — the game "
                "is already up", name, command);
    return true;
  }

  const uint32_t screen = FrontEndMenuScreen();
  if (screen == 0) {
    return false;
  }

  // A frame of our own on the GUEST stack, claimed by lowering r1, so the callee frames below
  // it instead of over the argument it was passed.
  const uint32_t sp = ctx.r1.u32;
  const uint32_t frame = (sp - 0x80u) & ~0xFu;

  // Laid out upward from the frame base so nothing overlaps: text, then the 4-byte body that
  // points at it, then the 12-byte record that points at the body.
  const uint32_t text = frame + 0x20u;
  const uint32_t body = frame + 0x60u;
  const uint32_t record = frame + 0x68u;

  // 0x40 bytes for the name. Refused rather than truncated: a truncated name would match a
  // DIFFERENT branch of the strcmp chain.
  if (name.size() + 1u > 0x40u) {
    REXLOG_INFO("Refii guest-tick pump: '{}' is too long to dispatch — dropped", name);
    return true;
  }

  for (size_t i = 0; i < name.size(); ++i) {
    base[text + i] = static_cast<uint8_t>(name[i]);
  }
  base[text + name.size()] = 0;

  StoreGuestU32(base, body, text);
  StoreGuestU32(base, record + 0u, body);
  StoreGuestU32(base, record + 4u, command);
  // `a2+8` is read only on case 302, which passes it to `sub_82382C68` as the LANGUAGE INDEX;
  // every other branch ignores it.
  StoreGuestU32(base, record + 8u, param);

  // Whole-context save/restore. The pump is standing in the middle of `sub_822C1BD0`, whose
  // own delegation still needs its registers.
  const PPCContext saved = ctx;
  ctx.r1.u32 = frame;
  ctx.r3.u32 = screen;
  ctx.r4.u32 = record;

  rex_lhGui_FrontendRoot_MenuActionDispatch(ctx, base);

  ctx = saved;
  return true;
}

/// The MenuBox's controller, or 0 when no box is open.
///
/// `ctl = *(guiOwner+56)` — a plain struct, no vtable — and `ctl+12` is its open flag. A
/// Lua-opened list (DisplayMenuBox) that can stand over any screen, so there is no modal latch
/// to gate on.
uint32_t MenuBoxController(uint8_t* base) {
  const uint32_t kInGameGuiVtable = refii::guest::Address(0x82001610u);
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  const uint32_t gui = LoadGuestU32(base, kGuiObject);
  if (!plausible(gui) || LoadGuestU32(base, gui) != kInGameGuiVtable) {
    return 0u;
  }

  const uint32_t owner = LoadGuestU32(base, gui + 28u);
  if (!plausible(owner)) {
    return 0u;
  }

  const uint32_t control = LoadGuestU32(base, owner + 56u);
  if (!plausible(control) || base[control + 12u] == 0u) {
    return 0u;  // no box open
  }

  return control;
}

/// Moves the MenuBox highlight, the guest's own way — its selector also reslides the two
/// affected buttons, so the drawn card and the owned highlight cannot disagree.
bool DispatchMenuBoxSelect(PPCContext& ctx, uint8_t* base, uint32_t index) {
  const uint32_t control = MenuBoxController(base);
  if (control == 0u) {
    REXLOG_INFO("Refii guest-tick pump: MenuBoxSelect:{} with no menu box — dropped", index);
    return true;
  }

  const uint32_t begin = LoadGuestU32(base, control + 20u);
  const uint32_t end = LoadGuestU32(base, control + 24u);
  const uint32_t count = (begin == 0u || end < begin) ? 0u : (end - begin) / 8u;
  if (index >= count) {
    REXLOG_INFO("Refii guest-tick pump: MenuBoxSelect:{} past {} option(s) — dropped",
                index, count);
    return true;
  }

  const PPCContext saved = ctx;
  ctx.r3.u32 = control;
  ctx.r4.u32 = index;
  rex_lhMenuBox_SetSelection(ctx, base);
  ctx = saved;
  return true;
}

/// Answers the MenuBox: 0 cancels, n >= 1 accepts option n-1.
///
/// Cancel is the guest's own B: the box's input handler given a press event (type `+44` 13,
/// button `+8` 0xC → case 1, 0x823dbce8 / disc 0x8240bf18), which sets the cancelled byte and
/// closes behind its own gates — cancellable `+14`, 0.3 s since its last action. Accept is the
/// selector (stores the index, 0x823dbbb0 / disc 0x8240bde0) then `sub_823DC720`, which
/// re-checks the enabled byte, fires the callback, publishes and closes.
bool DispatchMenuBoxAnswer(PPCContext& ctx, uint8_t* base, uint32_t answer) {
  const uint32_t control = MenuBoxController(base);
  if (control == 0u) {
    REXLOG_INFO("Refii guest-tick pump: MenuBoxAnswer:{} with no menu box — dropped", answer);
    return true;
  }

  const PPCContext saved = ctx;

  if (answer == 0u) {
    // The press on a frame claimed below the guest's sp; the handler's frame goes below it.
    const uint32_t frame = (saved.r1.u32 - 0x80u) & ~0xFu;
    const uint32_t press = frame + 0x20u;
    for (uint32_t i = 0; i < 0x40u; i += 4u) StoreGuestU32(base, press + i, 0u);
    StoreGuestU8(base, press + 8u, 0x0Cu);
    StoreGuestU32(base, press + 44u, 13u);
    ctx.r1.u32 = frame;
    ctx.r3.u32 = control;
    ctx.r4.u32 = press;
    rex_lhMenuBox_HandleInput(ctx, base);
    ctx = saved;
    if (LoadGuestU8(base, control + 12u) != 0u) {
      REXLOG_INFO("Refii guest-tick pump: MenuBoxAnswer:0 — the box's own B refused it (not "
                  "cancellable, or inside its 0.3 s)");
    }
    return true;
  }

  const uint32_t index = answer - 1u;
  const uint32_t begin = LoadGuestU32(base, control + 20u);
  const uint32_t end = LoadGuestU32(base, control + 24u);
  const uint32_t count = (begin == 0u || end < begin) ? 0u : (end - begin) / 8u;
  if (index >= count) {
    REXLOG_INFO("Refii guest-tick pump: MenuBoxAnswer:{} past {} option(s) — dropped",
                answer, count);
    return true;
  }

  ctx.r3.u32 = control;
  ctx.r4.u32 = index;
  rex_lhMenuBox_SetSelection(ctx, base);
  ctx = saved;
  ctx.r3.u32 = control;
  rex_lhMenuBox_Accept(ctx, base);
  ctx = saved;
  return true;
}

/// Hands one action code to whichever display box is currently up.
///
/// A box's reply is not a return value: its handler posts a MESSAGE EVENT the scripts read with
/// `MessageEvents.IsMessagePosted`, carrying the answer as ExtraData (type 36 for a yes/no with
/// 1 or 0, 37/38 for the numeric adjuster with the chosen number). Only running the guest's own
/// handler produces that.
///
/// The codes: 46 confirms and 47 cancels on the query box and the adjuster; 28/29 step the
/// adjuster's value; 36 dismisses the quest box and the message box. Every handler gates on the
/// box's own open byte, so a late code is a no-op.
bool DispatchDisplayBoxAction(PPCContext& ctx, uint8_t* base, uint32_t code,
                              uint32_t expected, uint32_t player) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  const uint32_t gui = LoadGuestU32(base, kGuiObject);
  if (!plausible(gui)) {
    REXLOG_INFO("Refii guest-tick pump: BoxAction:{} with no GUI — dropped", code);
    return true;
  }

  const uint32_t manager = LoadGuestU32(base, gui + 28u);
  if (!plausible(manager)) {
    REXLOG_INFO("Refii guest-tick pump: BoxAction:{} with no box manager — dropped", code);
    return true;
  }

  // `owner = *(*(GUI+28)+52)` — a dereference, as `IsDisplayBoxActive` (0x82305f10) passes it
  // to `sub_822A5C50`.
  const uint32_t owner = LoadGuestU32(base, manager + 52u);
  if (!plausible(owner)) {
    REXLOG_INFO("Refii guest-tick pump: BoxAction:{} with no box owner — dropped", code);
    return true;
  }

  // The box currently SHOWING, by the router's own rule — see gui/gui_display_box.h.
  const uint32_t box = refii::vfs::ShowingDisplayBox(
      owner, [base](uint32_t at) { return LoadGuestU32(base, at); });
  if (!plausible(box)) {
    REXLOG_INFO("Refii guest-tick pump: BoxAction:{} with no box up — dropped", code);
    return true;
  }

  // THE ANSWER NAMES ITS BOX: a surface that drew box X answers box X, and a press that lands
  // after X closed does not answer whatever the router shows next.
  if (expected != 0u && box != expected) {
    REXLOG_INFO("Refii guest-tick pump: BoxAction:{}@{:08X} — the box showing is 0x{:08X} — "
                "dropped", code, expected, box);
    return true;
  }

  const uint32_t vtable = LoadGuestU32(base, box);
  const PPCContext saved = ctx;
  ctx.r3.u32 = box;
  ctx.r4.u32 = code;
  ctx.r5.u32 = player;  // the box chain passes the input object's vf+16 (0x82404AC4 → 0x82404AD0)

  switch (refii::guest::ToTu1(vtable)) {
    case 0x820AE688u: rex_lhDisplayQueryBox_HandleAction(ctx, base); break;  // QueryBox / YesNo
    case 0x820AE6A8u: rex_lhDisplayNumericAdjuster_HandleAction(ctx, base); break;  // NumericAdjuster
    case 0x820AE6C8u: rex_lhDisplayQuestBox_HandleAction(ctx, base); break;  // QuestBoxParams
    case 0x820AE668u: rex_lhDisplayMessageBox_HandleAction(ctx, base); break;  // MessageBox / Download / Divorce
    default:
      ctx = saved;
      REXLOG_INFO("Refii guest-tick pump: BoxAction:{} — box 0x{:08X} has vtable 0x{:08X}, "
                  "which is not one this dispatches (the acceptance and button boxes take "
                  "their input elsewhere)",
                  code, box, vtable);
      return true;
  }

  ctx = saved;
  return true;
}

/// Answers the front end's `dialog` box. Every front-end warning goes through `sub_826E40E0`
/// (the main-menu root reaches it via slot +32 `sub_826E1C50`, the title directly), and its
/// answer is the owner's own input slot (+12): root `sub_826DEE40` / disc `sub_826C72A8` (36
/// accept, 37 cancel when `+206`), title `sub_826E2F18` / disc `sub_826C61B8` (36 in mode 12). r5
/// is the pad that pressed. The root acts only for the title's pad (`cmpw cr6,r5,r4` 0x826dee60 /
/// 0x826c72c8, r4 = `dword_83496AF8`); the title takes any pad. A press from another pad on the
/// main menu is still sent — the root's compare decides — and logged with the pad the title took A
/// from. The owner is the one OpenDialog was called on, while its warning is raised
/// (frontend_stated.cpp).
bool DispatchFrontEndDialog(PPCContext& ctx, uint8_t* base, uint32_t pad, uint32_t code) {
  bool title = false;
  const uint32_t owner = FrontEndDialogOwner(&title);
  if (owner == 0u) {
    REXLOG_INFO("Refii guest-tick pump: FrontEndDialog:{}:{} with no dialog up — dropped", pad, code);
    return true;
  }
  if (!title && pad != FrontEndPad()) {
    REXLOG_INFO("Refii guest-tick pump: FrontEndDialog:{}:{} is not the owning pad — the main menu takes only "
                "the pad the title took A from ({})", pad, code, FrontEndPad());
  }

  const PPCContext saved = ctx;
  ctx.r3.u32 = owner;
  ctx.r4.u32 = code;
  ctx.r5.u32 = pad;
  {
    const FrontEndLeverCall lever;
    if (title) {
      rex_lhGui_FrontendTitle_OnScreenAction(ctx, base);
    } else {
      rex_lhGui_FrontendRoot_OnScreenAction(ctx, base);
    }
  }
  ctx = saved;
  REXLOG_INFO("Refii guest-tick pump: FrontEndDialog:{}:{} answered 0x{:08X} ({})", pad, code, owner,
              title ? "title" : "main menu");
  return true;
}

/// THE TITLE'S A. The title's input slot `sub_826E2F18` / disc `sub_826C61B8` (r3 title, r4 code,
/// r5 pad) is the whole contract: every call stamps the idle clock (`stfd f9,0x48(r31)` 0x826e2f60 /
/// disc +0x40); with no dialog raised (`lbz r10,0x39(r31)` 0x826e3010 / 0x826c62a4) in mode 8 the
/// attract's skip (`cmpwi r11,8` 0x826e301c, `stb r10,0xF4(r11)` 0x826e3064 / 0x826c62f8); else on
/// 36 with the latch set (`lbz r11,0x50(r31)` 0x826e3078 / `0x48` 0x826c630c) the latch clear, the
/// root's slide 3, mode 10, the voice cue, and the session's pad (`stw r29,dword_83496AF8`
/// 0x826e30dc / `dword_83496934` 0x826c6370). Mode 11's handover in the title's Update reads that
/// pad: with its bit clear in the signed-in mask it shows the sign-in UI and waits on it
/// (`slw r8,r9,r11; and r7,r8,r10; cmplwi; bne` then `bl j_XamShowSigninUI` 0x82379564..0x823795A0 /
/// 0x8237A004..0x8237A040). Only user 0 signs in here and the sign-in UI signs nobody in, so while
/// some pad is signed in a pad that is not is refused for the start (not for the attract's skip),
/// with that reason; with none signed in the start goes as the console's would, and the session
/// runs on no profile. The title is the one its activate stated; mode 12 is the dialog's, answered
/// by FrontEndDialog.
bool DispatchTitleStart(PPCContext& ctx, uint8_t* base, uint32_t pad) {
  uint32_t mode = 0u;
  bool armed = false;
  const uint32_t title = FrontEndTitleScreen();
  if (!FrontEndTitleState(&mode, &armed) || title == 0u) {
    REXLOG_INFO("Refii guest-tick pump: TitleStart:{} with no title active — dropped", pad);
    return true;
  }
  if (mode == 12u || mode == 1u) {
    REXLOG_INFO("Refii guest-tick pump: TitleStart:{} dropped — the title's dialog is up (mode {}); "
                "FrontEndDialog answers it", pad, mode);
    return true;
  }
  if (mode != 8u) {
    if (!armed) {
      REXLOG_INFO("Refii guest-tick pump: TitleStart:{} dropped — the title is not taking A (mode {})", pad, mode);
      return true;
    }
    const uint32_t signed_in = LoadGuestU32(base, kSignedInUserMask);
    if (pad >= 4u || (signed_in != 0u && ((1u << pad) & signed_in) == 0u)) {
      REXLOG_INFO("Refii guest-tick pump: TitleStart:{} refused — pad {} is not signed in (mask 0x{:X}); "
                  "the title is started from a signed-in pad", pad, pad, signed_in);
      return true;
    }
  }

  const PPCContext saved = ctx;
  ctx.r3.u32 = title;
  ctx.r4.u32 = 36u;
  ctx.r5.u32 = pad;
  {
    const FrontEndLeverCall lever;
    rex_lhGui_FrontendTitle_OnScreenAction(ctx, base);
  }
  ctx = saved;
  REXLOG_INFO("Refii guest-tick pump: TitleStart:{} sent to the title 0x{:08X} in mode {}{}", pad, title, mode,
              mode == 8u ? " (the attract's skip)" : "");
  return true;
}

/// THE MAIN MENU'S B. The root's input slot `sub_826DEE40` / disc `sub_826C72A8` on 37 (r5 the pad;
/// only the title's pad acts, `cmpw cr6,r5,r4` 0x826dee60 / 0x826c72c8): in mode 0 at the list's
/// root back to the title (`li r11,7; stw r11,0x18(r31)` 0x826dfbb0 / 0x826c8004), in mode 5 the load
/// fan's close (`+24 = 0` 0x826dfb7c / 0x826c7fd0). Those are the two modes Godot's menu mask holds B in without a host panel
/// answering it; any other mode is refused here.
bool DispatchFrontEndBack(PPCContext& ctx, uint8_t* base, uint32_t pad) {
  const uint32_t menu = FrontEndMenuScreen();
  uint32_t activation = 0u;
  const uint32_t mode = FrontEndMenuMode();
  if (menu == 0u || FrontEndActiveScreen(&activation) != menu || (mode != 0u && mode != 5u)) {
    REXLOG_INFO("Refii guest-tick pump: FrontEndBack:{} dropped — the main menu is not on its list or its "
                "load fan (mode {})", pad, mode);
    return true;
  }
  if (pad != FrontEndPad()) {
    REXLOG_INFO("Refii guest-tick pump: FrontEndBack:{} is not the owning pad — the main menu takes only "
                "the pad the title took A from ({})", pad, FrontEndPad());
  }
  const PPCContext saved = ctx;
  ctx.r3.u32 = menu;
  ctx.r4.u32 = 37u;
  ctx.r5.u32 = pad;
  {
    const FrontEndLeverCall lever;
    rex_lhGui_FrontendRoot_OnScreenAction(ctx, base);
  }
  ctx = saved;
  REXLOG_INFO("Refii guest-tick pump: FrontEndBack:{} answered 0x{:08X} in mode {}", pad, menu, mode);
  return true;
}

/// "LoadFanBack" — the guest's own B on its Load Game fan, for a host flow that ends the fan itself
/// (the replace-a-save pick). The main menu's input slot `sub_826DEE40` / disc `sub_826C72A8` on 37
/// in mode 5 plays the `loadgame` close (slide 2, 0x826dfacc / 0x826c7f20) and returns the screen to
/// its list (`+24 = 0`, 0x826dfb7c). Sent only while the stated mode is 5: in another mode 37 is
/// another arm (mode 0 leaves for the title).
constexpr const char* kActionLoadFanBack = "LoadFanBack";

bool DispatchLoadFanBack(PPCContext& ctx, uint8_t* base) {
  const uint32_t menu = FrontEndMenuScreen();
  if (menu == 0u || FrontEndMenuMode() != 5u) {
    REXLOG_INFO("Refii guest-tick pump: LoadFanBack with no load fan up — dropped");
    return true;
  }
  const PPCContext saved = ctx;
  ctx.r3.u32 = menu;
  ctx.r4.u32 = 37u;
  ctx.r5.u32 = LoadGuestU32(base, refii::guest::Address(0x83496AF8u));
  {
    const FrontEndLeverCall lever;
    rex_lhGui_FrontendRoot_OnScreenAction(ctx, base);
  }
  ctx = saved;
  REXLOG_INFO("Refii guest-tick pump: LoadFanBack answered 0x{:08X}", menu);
  return true;
}

/// Hands one action code to a display MessageBox the host was told opened (vtable 0x820AE668,
/// see hooks/display_box_hooks.cpp), BY ADDRESS — the stated box, not whichever the router
/// would pick. The box is re-checked here, since the host's word is a frame old: still this
/// class and still open. Its handler `sub_823CEDD8` answers only the player at `box+24` unless that
/// is -1 (`cmpwi r11,-1` 0x823CEDF0, `cmplw r11,r5` 0x823CEDF8), so r5 is the acting player.
bool DispatchStatedMessageBoxAction(PPCContext& ctx, uint8_t* base, uint32_t box, uint32_t code,
                                    uint32_t player) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  if (!plausible(box) || LoadGuestU32(base, box) != refii::guest::Address(0x820AE668u) ||
      refii::vfs::LoadGuestU8(base, box + 12u) == 0u) {
    REXLOG_INFO("Refii guest-tick pump: BoxAction:{}@{:08X} — no open MessageBox there, dropped",
                code, box);
    return true;
  }

  const PPCContext saved = ctx;
  ctx.r3.u32 = box;
  ctx.r4.u32 = code;
  ctx.r5.u32 = player;
  rex_lhDisplayMessageBox_HandleAction(ctx, base);
  ctx = saved;
  REXLOG_INFO("Refii guest-tick pump: BoxAction:{}@{:08X} — answered the stated MessageBox",
              code, box);
  return true;
}

/// Plays a NEW HUD instance's root to slide 1, its shown pose. Deliberate replacement: a level's
/// fresh HUD presentation comes up on its authored hidden pose and the headless build has nothing
/// that plays it shown — the guest's only show is its exit transition, and a load runs no exit.
///
/// Everything else is the guest's own, on the mode object `M = *(GUI + 68)`: a screen open
/// (`sub_8236E630` → `sub_82405678`) plays slide 2 and stores `*M = 4, M+5 = 0`; the exits store
/// `*M = 3, M+5 = 1` (`sub_8236ED98` 0x8236f0b8 / disc 0x823372b8, `sub_82370178` 0x823703c4 /
/// disc 0x823385bc); the machine `sub_821764D8` (disc `sub_821762A0`) plays root slide 1 at
/// `*M == 1` (0x82176784 / disc 0x8217654c) and sets `M+4 = M+5`. The stated screen reads "none"
/// only from inside those exits (gui_stated_screens.cpp), so a close needs nothing here, and M is
/// never written.
void ArmHudShow(PPCContext& ctx, uint8_t* base) {
  constexpr uint32_t kNoScreen = 31u;
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  static uint32_t last_presentation = 0u;

  uint32_t screen_id = 0;
  if (!StatedInGameScreen(&screen_id) || screen_id != kNoScreen) {
    return;
  }

  const uint32_t gui = LoadGuestU32(base, kGuiObject);
  const uint32_t hud = plausible(gui) ? LoadGuestU32(base, gui + 32u) : 0u;
  const uint32_t presentation = plausible(hud) ? LoadGuestU32(base, hud + 4u) : 0u;
  const uint32_t mode = plausible(gui) ? LoadGuestU32(base, gui + 68u) : 0u;
  if (!plausible(presentation) || !plausible(mode)) {
    return;
  }

  if (presentation == last_presentation) {
    return;
  }
  last_presentation = presentation;

  if (static_cast<int32_t>(LoadGuestU32(base, mode)) > 0) {
    return;  // the guest's own transition is playing; it will land on its own
  }

  PPCContext call = ctx;
  call.r3.u32 = presentation;
  rex_lhGuiPresentation_GetRootElement(call, base);
  const uint32_t root = call.r3.u32;
  if (!plausible(root)) {
    REXLOG_INFO("Refii guest-tick pump: HUD show — the HUD presentation names no root element");
    return;
  }

  call = ctx;
  call.r3.u32 = root;
  call.r4.u32 = presentation;
  call.r5.u32 = 1u;
  rex_lhGuiElement_GoToSlide(call, base);
  REXLOG_INFO("Refii guest-tick pump: HUD shown — root slide 1 played on the fresh HUD "
              "presentation 0x{:08X}",
              presentation);
}

/// One action code to the current in-game screen's own input method (vtable+24).
///
/// Named statically per screen id because the recompiler emits no indirect-call path; the id is
/// the screen the guest's setter states and each handler comes from the guest's own map. The
/// third argument is the PLAYER INDEX (1 or 2) — gift, selection and decoration `bnelr` unless it
/// is 1.
bool DispatchScreenAction(PPCContext& ctx, uint8_t* base, uint32_t code, uint32_t player) {
  uint32_t screen_id = 0;
  if (!StatedInGameScreen(&screen_id)) {
    REXLOG_INFO("Refii guest-tick pump: ScreenAction:{} with no in-game screen — dropped", code);
    return true;
  }

  const PPCContext saved = ctx;
  const uint32_t handler = InGameScreenHandler(ctx, base, screen_id);
  if (handler == 0u) {
    REXLOG_INFO("Refii guest-tick pump: ScreenAction:{} — screen {} has no handler",
                code, screen_id);
    return true;
  }

  ctx.r3.u32 = handler;
  ctx.r4.u32 = code;
  ctx.r5.u32 = player;

  switch (screen_id) {
    case 14u: rex_lhGuiAbilitiesScreen_OnScreenAction(ctx, base); break;
    case 15u: rex_lhGuiItemsScreen_OnScreenAction(ctx, base); break;
    case 16u: rex_lhGuiClothingScreen_OnScreenAction(ctx, base); break;
    case 17u: rex_lhGuiLogbookScreen_OnScreenAction(ctx, base); break;
    case 18u: rex_lhGuiOptionsScreen_OnScreenAction(ctx, base); break;
    case 21u: rex_lhGuiCrimeScreen_OnScreenAction(ctx, base); break;
    case 22u: rex_lhGuiShopScreen_OnScreenAction(ctx, base); break;
    case 23u: rex_lhGuiWorldMapScreen_OnScreenAction(ctx, base); break;
    case 25u: rex_lhGuiGiftGivingScreen_OnScreenAction(ctx, base); break;
    case 26u: rex_lhGuiSelectionScreen_OnScreenAction(ctx, base); break;
    case 27u: rex_lhGuiDecorationScreen_OnScreenAction(ctx, base); break;
    case 10u: rex_lhGuiQueryScreen_OnScreenAction(ctx, base); break;
    case 28u: rex_lhGuiRealEstateScreen_OnScreenAction(ctx, base); break;
    case 11u: rex_lhGuiMinigameCompletedScreen_HandleAction(ctx, base); break;
    case 12u: rex_lhGuiQuestCompletionScreen_HandleAction(ctx, base); break;
    case 13u: rex_lhGuiNextEpisodeScreen_HandleAction(ctx, base); break;  // TU1 only
    case 29u: rex_lhGuiCreditsScreen_OnScreenAction(ctx, base); break;
    default:
      ctx = saved;
      REXLOG_INFO("Refii guest-tick pump: ScreenAction:{} — screen {} is not owned",
                  code, screen_id);
      return true;
  }

  ctx = saved;
  return true;
}

/// Writes one d-pad slot's will, resolving the hero's CECSpellManager first.
///
/// The chain is `sub_823B96E0`'s own, read rather than called (0x823B970C..0x823B9794):
///
///     v5   = *(*(*(0x83496AB8 + 12) + 88) + 4)
///     hero = *(*(v5) + 108)                       // one more load than it looks
///     guards: byte[hero+144] & 0x40, byte[hero+40] & 1
///     mgr  = component 56 of hero — the cached slot at hero+140, else the sorted
///            {id, pointer} pairs at hero+72 .. hero+76
///
/// Resolved here on the guest thread, not passed in: a pointer read on Godot's thread would be
/// a frame old, and a level change frees the hero underneath it. The RANGE check is ours — the
/// writer validates nothing, so slot > 4 scribbles past the assignment array and spell > 7 puts
/// an index with no icon-table entry on the d-pad.
extern "C" uint32_t refii_mod_player_hero(uint8_t* base, int32_t player);
extern "C" uint32_t refii_mod_handle(uint32_t address);
extern "C" uint32_t refii_mod_handle_address(uint32_t handle);
extern "C" int32_t refii_mod_entity_alive(uint8_t* base, uint32_t entity);
extern "C" int32_t refii_mod_entity_name(uint8_t* base, uint32_t entity, char* out, uint32_t size);
extern "C" uint32_t refii_mod_component(uint8_t* base, uint32_t entity, uint32_t id);
extern "C" uint32_t RefiiCoopPlayerDog(uint8_t* base, int player);

bool DispatchSpellAssign(PPCContext& ctx, uint8_t* base, uint32_t slot, uint32_t spell,
                         uint32_t player) {
  constexpr uint32_t kSlots = 5u;
  constexpr uint32_t kSpells = 8u;
  constexpr uint32_t kSpellManagerComponent = 56u;
  const uint32_t kGameRoot = refii::guest::Address(0x83496AB8u);

  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  if (slot >= kSlots || spell >= kSpells || player > 2u) {
    REXLOG_INFO("Refii guest-tick pump: SpellAssign:{}:{}:{} is out of range — dropped", slot,
                spell, player);
    return true;
  }

  // `dword_83496AB8` is IDA's name for the WORD AT 0x83496AB8 — the game root is behind one
  // load, and `+12` is measured from that (the rule every other reader here follows). Player 2's
  // hero is the co-op partner's, by the same roster walk the mod helpers use.
  uint32_t hero = 0u;
  if (player == 2u) {
    hero = refii_mod_player_hero(base, 2);
  } else {
    const uint32_t root = LoadGuestU32(base, kGameRoot);
    const uint32_t game = plausible(root) ? LoadGuestU32(base, root + 12u) : 0u;
    const uint32_t holder = plausible(game) ? LoadGuestU32(base, game + 88u) : 0u;
    const uint32_t party = plausible(holder) ? LoadGuestU32(base, holder + 4u) : 0u;
    const uint32_t roster = plausible(party) ? LoadGuestU32(base, party) : 0u;
    const uint32_t entry = plausible(roster) ? refii_bridge_hero_one_holder(base, roster) : 0u;
    hero = plausible(entry) ? LoadGuestU32(base, entry) : 0u;
  }

  if (!plausible(hero) || (LoadGuestU8(base, hero + 144u) & 0x40u) == 0u ||
      (LoadGuestU8(base, hero + 40u) & 0x01u) == 0u) {
    REXLOG_INFO("Refii guest-tick pump: SpellAssign:{}:{} with no hero — dropped", slot, spell);
    return true;
  }

  uint32_t manager = 0u;
  const uint32_t cache = LoadGuestU32(base, hero + 140u);
  const uint32_t table = LoadGuestU32(base, hero + 72u);
  if (plausible(cache) && plausible(table)) {
    manager = LoadGuestU32(
        base, table + (static_cast<uint32_t>(LoadGuestU8(base, cache + 56u)) << 3) + 4u);
  } else if (plausible(table)) {
    const uint32_t end = LoadGuestU32(base, hero + 76u);
    if (end > table && (end - table) <= 0x2000u) {
      for (uint32_t at = table; at < end; at += 8u) {
        if (LoadGuestU32(base, at) == kSpellManagerComponent) {
          manager = LoadGuestU32(base, at + 4u);
          break;
        }
      }
    }
  }

  if (!plausible(manager)) {
    REXLOG_INFO("Refii guest-tick pump: SpellAssign:{}:{} with no CECSpellManager — dropped",
                slot, spell);
    return true;
  }

  const PPCContext saved = ctx;
  ctx.r3.u32 = manager;
  ctx.r4.u32 = 0u;    // the guest passes 0 here from every one of its own call sites
  ctx.r5.u32 = slot;
  ctx.r6.u32 = spell;
  ctx.r7.u32 = 1u;    // notify: fire the UI/net event for the local player

  rex_CECSpellManager_SetSpellSlot(ctx, base);

  ctx = saved;
  return true;
}

// Defined with the menu-event machinery below.
bool DispatchMenuEventDirect(PPCContext& ctx, uint8_t* base, const std::string& name,
                             uint32_t type, uint32_t id);

/// Runs one Lua source chunk in the GAME script VM — the state the startup scripts, the camera
/// among them, run in. False when there is no game VM. Not the GUI's: Gameface links a second
/// Lua build with its own states and a different `lua_State` layout (`luaD_pcall` reads
/// `errfunc` at +96 there, +100 here), so the two must never be crossed.
///
/// The state is `Game+0xAC`, written by `Game_InitDatabases` (`stw r3, 0xAC(r27)` at 0x82310e30)
/// and closed by the game's teardown, so it is re-read on every call. The game itself reaches it
/// as `*(*(*(0x83496AB8)+0xC)+0x78)+4)` — the script system holding the same pointer
/// (`World_LoadLevel_Execute` 0x8231c78c).
bool RunGameLua(PPCContext& ctx, uint8_t* base, const std::string& chunk) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  const uint32_t root = LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = plausible(root) ? LoadGuestU32(base, root + 0xCu) : 0u;
  const uint32_t state = plausible(game) ? LoadGuestU32(base, game + 0xACu) : 0u;
  if (!plausible(state)) {
    return false;
  }

  const uint32_t sp = ctx.r1.u32;
  const uint32_t frame = (sp - 0x400u) & ~0xFu;
  const uint32_t name = frame + 0x3E0u;
  const uint32_t nameHandle = frame + 0x3F0u;

  // A chunk that fits goes on the frame; a longer one (a mod's live run, a Godot script's
  // run_lua) in a guest heap block for the length of the call, as the composed init chunk does.
  constexpr uint32_t kFrameChunk = 0x3B0u;
  uint32_t text = frame + 0x20u;
  uint32_t heap = 0u;
  if (chunk.size() + 1u > kFrameChunk) {
    heap = CallLhMalloc(ctx, base, static_cast<uint32_t>(chunk.size()) + 1u);
    if (!plausible(heap)) {
      REXLOG_INFO("Refii guest-tick pump: no guest memory for a {}-byte game Lua chunk — dropped",
                  chunk.size());
      return false;
    }
    text = heap;
  }

  std::memcpy(base + text, chunk.data(), chunk.size());
  base[text + chunk.size()] = 0;
  std::memcpy(base + name, "refii", 6);
  StoreGuestU32(base, nameHandle, 0u);

  PPCContext work = ctx;
  work.r1.u32 = frame;
  work.r3.u32 = nameHandle;
  work.r4.u32 = name;
  work.r5.u32 = 0xFFFFFFFFu;
  rex_lhCharString_CtorFromCStr(work, base);

  work = ctx;
  work.r1.u32 = frame;
  work.r3.u32 = state;
  work.r4.u32 = text;
  work.r5.u32 = static_cast<uint32_t>(chunk.size());
  work.r6.u32 = nameHandle;
  work.r7.u32 = 0u;
  rex_lhLua_RunScriptBuffer(work, base);
  const bool ok = work.r3.u32 != 0u;

  work = ctx;
  work.r1.u32 = frame;
  work.r3.u32 = nameHandle;
  rex_lhCharString_Dtor(work, base);
  if (heap != 0u) {
    CallLhFree(ctx, base, heap);
  }
  return ok;
}

uint32_t PlayerObject(uint8_t* base);

/// Both are defined further down this file; the inventory dispatch below needs them here.
uint32_t HeroEntity(uint8_t* base);
uint32_t HeroComponent(uint8_t* base, uint32_t entity, uint32_t id);
bool MenusActForPlayerTwo(uint8_t* base);
std::string ReadStatedWideLhString(uint8_t* base, uint32_t handle);

/// `Inventory:<op>:<recordHash>` — WEAR, GIFT or USE an item the hero already has, by its GDB record
/// hash, with the guest's menus taking no part at all.
///
/// This is the seam that lets Godot own the item lists outright. Everything else on this screen
/// goes through the current screen's `vtable+20` with a row the guest's populate handed out.
/// These two do not: they are the guest's own SCRIPT-COMMAND level entry points, bound as `AddAndReplaceItemOfRecordID` and
/// `Player:GiveGift`, and both are self-contained. Decompiled and verified rather than taken on
/// report:
///
///   sub_824BF068(comp15, hash)   `sub_821DA348` GDB lookup (0x824BF080), binary search for
///                                component 13 (0x824BF11C), `sub_824F1C90` -> EntityRef
///                                (0x824BF1B8), `sub_824BECF8` wear (0x824BF1D0),
///                                `sub_82764DC0` release (0x824BF1E4)
///   sub_823F32A0(comp33, hash)   the same shape via `sub_824F1C18` (0x823F33DC) and
///                                `sub_823F2718` give (0x823F33E8)
///
/// Both take the component and the hash and manage the refcounted handle themselves, so the host
/// supplies only what it already reads during its inventory walk. The record hash is a LOOKUP KEY
/// INTO THE HERO'S OWN INVENTORY, not a spawn — it cannot conjure an item the hero lacks.
///
/// Runs on the pump, i.e. on the guest's tick thread, because everything below it does.

/// One guest call on a frame claimed below `saved.r1`; returns r3 and restores the context.
uint32_t CallGuestAt(PPCContext& ctx, uint8_t* base, const PPCContext& saved, uint32_t frame,
                     uint32_t address, uint32_t r3, uint32_t r4 = 0u, uint32_t r5 = 0u) {
  PPCFunc* fn = FindGuestFunction(address);
  if (fn == nullptr) {
    return 0u;
  }
  ctx = saved;
  ctx.r1.u32 = frame;
  ctx.r3.u32 = r3;
  ctx.r4.u32 = r4;
  ctx.r5.u32 = r5;
  ctx.ctr.u32 = address;
  fn(ctx, base);
  const uint32_t result = ctx.r3.u32;
  ctx = saved;
  return result;
}

/// Puts one inventory piece on `appearance` (a component 15) the way `sub_826CB218` does:
/// `sub_824C4258` "already worn?", else `sub_824BECF8`, which strips whatever overlaps the
/// piece's `BodyAreasCovered`. Returns true when it was put on.
bool WearPiece(PPCContext& ctx, uint8_t* base, const PPCContext& saved, uint32_t frame,
               uint32_t appearance, uint32_t handle) {
  if ((CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x824C4258u), appearance, handle) & 0xFFu) != 0u) {
    return false;
  }
  CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x824BECF8u), appearance, handle);
  return true;
}

/// Every piece of the suit at `suit` (an inventory handle) the hero carries in `inventory`,
/// put on `appearance`: `sub_826CC618` reads `SuitParts` into a vector (begin/end/capacity at
/// +4/+8/+12, `sub_8237C1D8`) and `sub_824F1C90` finds the first owned copy of each.
void WearSuitPieces(PPCContext& ctx, uint8_t* base, const PPCContext& saved, uint32_t frame,
                    uint32_t suit, uint32_t inventory, uint32_t appearance, uint32_t* owned,
                    uint32_t* worn) {
  const uint32_t parts = frame + 0x60u;
  const uint32_t piece = frame + 0x80u;
  for (uint32_t word = 0; word < 4u; ++word) {
    StoreGuestU32(base, parts + (word * 4u), 0u);
  }
  CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x826CC618u), 0u, suit, parts);

  const uint32_t begin = LoadGuestU32(base, parts + 4u);
  const uint32_t end = LoadGuestU32(base, parts + 8u);
  for (uint32_t at = begin; begin != 0u && at < end; at += 4u) {
    for (uint32_t word = 0; word < 4u; ++word) {
      StoreGuestU32(base, piece + (word * 4u), 0u);
    }
    CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x824F1C90u), piece, inventory, LoadGuestU32(base, at));
    const uint32_t item = LoadGuestU32(base, piece);
    if (item == 0u) {
      continue;
    }

    ++*owned;
    if (WearPiece(ctx, base, saved, frame, appearance, piece)) {
      ++*worn;
    }
    CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x82764DC0u), item, piece);
  }

  if (begin != 0u) {
    CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x8221BD38u), begin);
  }
}

/// AN OUTFIT IS ITS PIECES. Screen 16's outfit row (event 89) is answered by `sub_826D3A10`,
/// whose work is `sub_826CBF10(_, &suitHandle, creature)`: every inventory item whose record is
/// one of the suit's `SuitParts` goes through `sub_826CB218`. The suit item itself is never
/// worn; slots no piece touches stay. Neither callee reads the first argument, so 0 stands in
/// for the screen's manager.
///
/// A joiner cannot take that call: it picks the player through the `game+140` chooser, the read
/// that faulted for `unequip` on a client. It runs the same inner pair on its own hero instead.
void DressOutfit(PPCContext& ctx, uint8_t* base, const PPCContext& saved, uint32_t frame,
                 uint32_t suit, uint32_t hero, uint32_t suit_hash, bool direct) {
  if (!direct) {
    const uint32_t dressed = CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x826CBF10u), 0u, suit) & 0xFFu;
    REXLOG_INFO("Refii guest-tick pump: Inventory:outfit — sub_826CBF10 on suit 0x{:08X}: {}",
                suit_hash, dressed != 0u ? "pieces equipped" : "the hero owns none of its pieces");
    return;
  }

  const uint32_t inventory = HeroComponent(base, hero, 13u);
  const uint32_t appearance = HeroComponent(base, hero, 15u);
  if (inventory == 0u || appearance == 0u) {
    REXLOG_INFO("Refii guest-tick pump: Inventory:outfit — joiner hero 0x{:08X} has no "
                "component 13/15", hero);
    return;
  }

  uint32_t owned = 0;
  uint32_t worn = 0;
  WearSuitPieces(ctx, base, saved, frame, suit, inventory, appearance, &owned, &worn);
  REXLOG_INFO("Refii guest-tick pump: Inventory:outfit — joiner dressed suit 0x{:08X}: {} of {} "
              "owned piece(s) put on", suit_hash, worn, owned);
}

/// TRYING AN OUTFIT ON — `Inventory:tryon:<record>`, the remaster's preview of the highlighted
/// outfit on screen 16's mannequin. The mannequin is a preview body only: nothing here, and no
/// lever, reads it to decide what the player wears (gui_screen_stub_clothing.cpp). The original changes the mannequin only on a press; in dye
/// mode it already dresses it on HIGHLIGHT (`sub_826D31E0`), and that is the mechanism used here.
///
/// First the mannequin is put back in the hero's clothes, as `sub_826D3358` does: the worn list
/// copied (`sub_824C5FC8(manq15 + 12, hero15 + 12)`) with byte +61. A list copy rebuilds nothing,
/// so the four calls `sub_824BECF8` makes after its own list change follow (`sub_824C1FC8`,
/// `sub_824C09C0`, `sub_824C1730`, `sub_824C4780`). Then every clothing slot is emptied and the
/// record's pieces go on — every owned `SuitParts` piece for a suit (category 25), the garment
/// itself otherwise. Record 0 only resets.
void TryOnMannequin(PPCContext& ctx, uint8_t* base, const PPCContext& saved, uint32_t frame,
                    uint32_t hero, uint32_t record) {
  const uint32_t mannequin = StatedClothingMannequin();
  const uint32_t dummy = mannequin != 0u ? HeroComponent(base, mannequin, 15u) : 0u;
  const uint32_t worn = HeroComponent(base, hero, 15u);
  const uint32_t inventory = HeroComponent(base, hero, 13u);
  if (dummy == 0u || worn == 0u || inventory == 0u) {
    REXLOG_INFO("Refii guest-tick pump: Inventory:tryon — no clothing mannequin (0x{:08X}) or "
                "hero component 13/15", mannequin);
    return;
  }

  CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x824C5FC8u), dummy + 12u, worn + 12u);
  base[dummy + 61u] = base[worn + 61u];
  for (const uint32_t rebuild : {refii::guest::Address(0x824C1FC8u), refii::guest::Address(0x824C09C0u), refii::guest::Address(0x824C1730u), refii::guest::Address(0x824C4780u)}) {
    CallGuestAt(ctx, base, saved, frame, rebuild, dummy);
  }

  if (record == 0u) {
    return;
  }

  // ONLY THE OUTFIT. Every clothing slot (categories 17..23) is emptied first through the
  // guest's own per-slot unequip `sub_824BF4D8(comp15, category)` — it drops the worn items of
  // that category and runs the same rebuild — so a slot the outfit leaves empty, or whose piece
  // the hero does not carry, shows nothing rather than what the hero has on.
  for (uint32_t category = 17u; category <= 23u; ++category) {
    CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x824BF4D8u), dummy, category);
  }

  const uint32_t handle = frame + 0xA0u;
  for (uint32_t word = 0; word < 4u; ++word) {
    StoreGuestU32(base, handle + (word * 4u), 0u);
  }
  CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x824F1C90u), handle, inventory, record);
  const uint32_t item = LoadGuestU32(base, handle);
  if (item == 0u) {
    REXLOG_INFO("Refii guest-tick pump: Inventory:tryon — record 0x{:08X} is not carried", record);
    return;
  }

  uint32_t owned = 0;
  uint32_t put = 0;
  if (LoadGuestU32(base, item + 16u) == 25u) {
    WearSuitPieces(ctx, base, saved, frame, handle, inventory, dummy, &owned, &put);
  } else {
    owned = 1u;
    put = WearPiece(ctx, base, saved, frame, dummy, handle) ? 1u : 0u;
  }
  CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x82764DC0u), item, handle);
  REXLOG_INFO("Refii guest-tick pump: Inventory:tryon — mannequin 0x{:08X} wears 0x{:08X}: {} of "
              "{} owned piece(s) put on", mannequin, record, put, owned);
}

/// AN ITEM ROW COMMITTED THROUGH THE SCREEN THAT LISTS IT, so the screen's own answer runs with
/// every side effect it has. Two list screens answer their item rows' event type 3 with a decision
/// tree of the guest's own:
///
///   items 15 (`sub_826D1800`, disc `sub_8257D250`) — case 3 refuses a weapon while swimming
///   (0x826D1B5C, `GUI_CANNOT_EQUIP_WEAPON_WHILE_SWIMMING`), completes tutorial 188 (0x826D1BDC),
///   activates `screen+396` (`lhGuiInventory_ActivateItemByCategory` 0x826D1BEC), restates the
///   weapons' rows (0x826D1C08) and queues the `GUI_SCREEN_EXPRESSIONS_LEARNED` box for an
///   expression book (0x826D1C9C..0x826D1DC8);
///   gift 25 (`sub_826C45C8`, disc `sub_825A0568`) — looks the pushed id up in the row map
///   (0x826C46CC), asks query 15 (`GUI_GIFT_GIVING_SCREEN_WOULD_YOU_LIKE`, 0x826C4B80) and on
///   its YES (`sub_826C4CC0`) gives the item, or the orb in orb mode (`+48 == 1`,
///   `sub_826C5578`), then leaves the screen (0x826C4DEC, disc 0x825A0C58's twin).
///
/// The row's id is the populate's TICKET for the item: `ticket` when the populate filed it for
/// `record` (MenuRecordForTicket), else the record's first row (MenuTicketForRecord). The items
/// screen acts on the handle its Update caches from the highlight, so the commit first makes the
/// Update's own lookup for that ticket (`sub_82880BC0(*(QM+60)+4, ticket, screen+396)`, 0x826D1754,
/// disc `sub_827BE1E8`) and goes on only when the handle it leaves is an item of `record`.
///
/// The clothing screen's rows are `Clothing:` levers (gui_screen_stub_clothing.cpp), and player
/// two's books keep the direct calls: the screens' answers act on player one.
/// False when this is not that screen, or the row is not on it.
bool CommitItemThroughScreen(PPCContext& ctx, uint8_t* base, const std::string& op,
                             uint32_t record, uint32_t ticket) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  uint32_t screen_id = 0;
  if (!StatedInGameScreen(&screen_id) || IsCoopJoiner(base) || MenusActForPlayerTwo(base)) {
    return false;
  }

  uint32_t cache = 0u;
  if (screen_id == 15u && (op == "use" || op == "equip")) {
    cache = 0x18Cu;
  } else if (!(screen_id == 25u && op == "gift")) {
    return false;
  }

  if (ticket == 0u || MenuRecordForTicket(ticket) != record) {
    ticket = MenuTicketForRecord(record);
  }
  if (ticket == 0u) {
    REXLOG_INFO("Refii guest-tick pump: Inventory:{} — record 0x{:08X} is on no row of screen "
                "{}'s map", op, record, screen_id);
    return false;
  }

  if (cache != 0u) {
    const uint32_t screen = InGameScreenHandler(ctx, base, screen_id);
    if (screen == 0u) {
      return false;
    }
    const uint32_t registry = MenuTicketRegistry();
    PPCFunc* lookup = FindGuestFunction(refii::guest::Address(0x82880BC0u));
    if (!plausible(registry) || lookup == nullptr) {
      return false;
    }
    const PPCContext saved = ctx;
    ctx.r1.u32 = (saved.r1.u32 - 0x100u) & ~0xFu;
    ctx.r3.u32 = registry;
    ctx.r4.u32 = ticket;
    ctx.r5.u32 = screen + cache;
    ctx.ctr.u32 = refii::guest::Address(0x82880BC0u);
    lookup(ctx, base);
    ctx = saved;
    const uint32_t item = LoadGuestU32(base, screen + cache);
    if (!plausible(item) || LoadGuestU32(base, item + 4u) != record) {
      REXLOG_INFO("Refii guest-tick pump: Inventory:{} — ticket {} is not an item of record 0x{:08X} on "
                  "screen {}", op, ticket, record, screen_id);
      return false;
    }
  }

  char name[32];
  std::snprintf(name, sizeof(name), "Item:%08X", record);
  if (!DispatchMenuEventDirect(ctx, base, name, 3u, ticket)) {
    return false;
  }
  REXLOG_INFO("Refii guest-tick pump: Inventory:{} committed record 0x{:08X} as ticket {} on "
              "screen {}'s own handler", op, record, ticket, screen_id);
  return true;
}

/// `addressed` 0 acts for the hero whose menus are open (HeroEntity); 1 or 2 names a player whose menus
/// are NOT, in local co-op (`game+0x11C == 2`, the guest's IsLocalCoopGame), where both heroes are this
/// machine's: that hero — `refii_mod_player_hero`, the entity manager's slot the guest reads — is acted
/// on through the direct calls player two's own book takes, never through the open screen's rows (they
/// list the other player's inventory). Only use, equip, wear, outfit and unequip.
bool DispatchInventoryAction(PPCContext& ctx, uint8_t* base, const std::string& body,
                             int32_t addressed, uint32_t ticket = 0u) {
  if (addressed != 0) {
    const uint32_t root = LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
    const uint32_t game = LooksLikeGuestPointer(root) ? LoadGuestU32(base, root + 12u) : 0u;
    const bool local = LooksLikeGuestPointer(game) && LoadGuestU32(base, game + 0x11Cu) == 2u;
    const std::string verb = body.substr(0, body.find(':'));
    if (!local || (addressed != 1 && addressed != 2) ||
        (verb != "use" && verb != "equip" && verb != "wear" && verb != "outfit" && verb != "unequip")) {
      REXLOG_INFO("Refii guest-tick pump: InventoryFor:{}:{} — {}; dropped", addressed, body,
                  local ? "not an action a named player takes" : "not local co-op");
      return true;
    }
  }
  const auto hero_of = [&]() {
    return addressed != 0 ? refii_mod_player_hero(base, addressed) : HeroEntity(base);
  };
  // Player two's own hero, or a named one, acted on directly (see the joiner notes below).
  const bool direct = addressed != 0 || IsCoopJoiner(base) || MenusActForPlayerTwo(base);

  const std::size_t colon = body.rfind(':');
  if (colon == std::string::npos) {
    REXLOG_INFO("Refii guest-tick pump: 'Inventory:{}' is not op:hash — dropped", body);
    return true;
  }

  const std::string op = body.substr(0, colon);
  char* end = nullptr;
  const unsigned long hash = std::strtoul(body.c_str() + colon + 1, &end, 10);
  // `tryon:0` is the one zero that means something: put the mannequin back in the hero's clothes.
  if (end == body.c_str() + colon + 1 || *end != '\0' || (hash == 0u && op != "tryon")) {
    REXLOG_INFO("Refii guest-tick pump: 'Inventory:{}' has no record hash — dropped", body);
    return true;
  }

  // TAKING SOMETHING OFF IS A CATEGORY, NOT A RECORD, and it resolves everything itself.
  //
  // The clothing screen spawns one `INV_ITEM_CLOTHES_NONE_<CAT>` row per slot with event types
  // 90..97 (`sub_829B3340`, `sub_829B2D58`), and answers a press on one through
  // `sub_826D3F40` -> `sub_826CC430(_, eventType)`. That call reads NOTHING from the screen:
  // it takes the hero out of `dword_83496AB8`, turns the event type into a category through
  // `sub_826CC340`'s jump table (0x826CC4DC), binary-searches component 15 and hands both to
  // `sub_824BF4D8` (0x826CC5FC). Its first argument is never read — the decompile assigns r3
  // and uses it nowhere — so 0 is as good as the manager the guest passes.
  //
  // The screen calls around it (`sub_826D48F0`, `sub_826D7F90`) drive the 3D preview and the
  // presentation; both are Godot's now.
  if (op == "unequip" && direct) {
    // THE INNER CALL, on the joiner's own hero. `sub_826CC430` picks the player through the
    // `game+140` chooser and faulted at 0x34 on a client (see CoopRepairNetworkSlot); what it
    // does after that is `sub_824BF4D8(comp15, slot)` with slot = `sub_826CC340(type - 91)`,
    // a seven-entry table returning 17..23 (0x826CC374: `li r3, 0x11` … `0x17`) — the
    // clothing categories. Verified in the decompile; client-safe (no manager read).
    const uint32_t entity = hero_of();
    const uint32_t appearance = entity != 0u ? HeroComponent(base, entity, 15u) : 0u;
    const uint32_t slot = static_cast<uint32_t>(hash) - 74u;
    if (appearance == 0u || slot < 17u || slot > 23u) {
      REXLOG_INFO("Refii guest-tick pump: Inventory:unequip — no appearance component or "
                  "event type {} outside 91..97", hash);
      return true;
    }

    PPCContext work = ctx;
    work.r1.u32 = (ctx.r1.u32 - 0x200u) & ~0xFu;
    work.r3.u32 = appearance;
    work.r4.u32 = slot;
    rex_CECAppearanceModifiers_RemoveWornByCategory(work, base);
    REXLOG_INFO("Refii guest-tick pump: Inventory:unequip — joiner cleared slot {} on its hero "
                "0x{:08X} directly (sub_824BF4D8)", slot, entity);
    return true;
  }

  if (op == "unequip") {
    PPCFunc* unequip = FindGuestFunction(refii::guest::Address(0x826CC430u));
    if (unequip == nullptr) {
      REXLOG_INFO("Refii guest-tick pump: Inventory:unequip — 0x826CC430 is not callable here");
      return true;
    }

    const PPCContext saved_unequip = ctx;
    ctx.r3.u32 = 0u;
    ctx.r4.u32 = static_cast<uint32_t>(hash);
    // r5 is an entity that REPLACES the chosen player when non-zero (0x826cc44c `mr r31, r5`,
    // 0x826cc4c4..cc `cmplwi r31, 0` / `mr r9, r31`); 0 takes the player.
    ctx.r5.u32 = 0u;
    ctx.ctr.u32 = refii::guest::Address(0x826CC430u);
    unequip(ctx, base);
    ctx = saved_unequip;
    REXLOG_INFO("Refii guest-tick pump: Inventory:unequip ran sub_826CC430(event type {})", hash);
    return true;
  }

  // ONLY `wear` TAKES A RECORD HASH. This was one call for all three and two of them were wrong.
  //
  // `sub_824BF068` (wear) hands r4 straight to `sub_821DA348`, which stores it and looks it up
  // with `GDBManager_FindEntryByHash` (0x821DA394) — a hash. But `sub_823E1370` (use) and
  // `sub_823F32A0` (gift) both go through `sub_824F1C18`, whose third argument is a `void***`
  // that `sub_823FE8B8` dereferences TWICE to reach characters before calling
  // `NGDB_CRecord_Get_int` (0x823FE948) — a NAME. Passing a hash there dereferences the hash as
  // a pointer, and an FNV-1 value sits squarely inside the guest's plausible address range, so
  // it reads whatever is at 0x959C9372 and resolves nothing. Silently.
  //
  // Both name-taking wrappers exist only to turn the name into a hash and then reach the SAME
  // inner pair, so this calls the inner pair directly:
  //
  //   `sub_824F1C90(&handle, component13, hash)` walks the inventory list at `+16` matching
  //   `*(item + 4) == hash` (0x824F1CE4) — the item's own record hash — and takes a reference
  //   with `sub_82764D00`. Release it with `sub_82764DC0`, as both wrappers do.
  //   Then `sub_823E0FF8(entity, &handle, "")` uses it, or `sub_823F2718(component33, &handle)`
  //   gives it. `sub_823F2718` resolves the RECIPIENT itself, off the giver's property 77 and
  //   that object's vtable+68, so no target has to be supplied.
  if (addressed == 0 && (op == "use" || op == "equip" || op == "gift") &&
      CommitItemThroughScreen(ctx, base, op, static_cast<uint32_t>(hash), ticket)) {
    return true;
  }

  // A gift is only ever the gift screen's: its confirm, the orb mode and the exit are the
  // screen's (CommitItemThroughScreen), and a gift without them repeats into one interaction.
  if (op == "gift" && !IsCoopJoiner(base) && !MenusActForPlayerTwo(base)) {
    REXLOG_INFO("Refii guest-tick pump: Inventory:{} — not on the gift screen's rows, not given",
                body);
    return true;
  }

  const uint32_t hero = hero_of();
  if (hero == 0u) {
    REXLOG_INFO("Refii guest-tick pump: Inventory:{} — no hero", body);
    return true;
  }

  if (op == "tryon") {
    const PPCContext saved_tryon = ctx;
    TryOnMannequin(ctx, base, saved_tryon, (ctx.r1.u32 - 0x200u) & ~0xFu, hero,
                   static_cast<uint32_t>(hash));
    ctx = saved_tryon;
    return true;
  }

  if (op == "wear") {
    const uint32_t appearance = HeroComponent(base, hero, 15u);
    PPCFunc* wear = FindGuestFunction(refii::guest::Address(0x824BF068u));
    if (appearance == 0u || wear == nullptr) {
      REXLOG_INFO("Refii guest-tick pump: Inventory:wear — no component 15, or 0x824BF068 is "
                  "not callable here");
      return true;
    }

    const PPCContext saved = ctx;
    ctx.r3.u32 = appearance;
    ctx.r4.u32 = static_cast<uint32_t>(hash);
    ctx.ctr.u32 = refii::guest::Address(0x824BF068u);
    wear(ctx, base);
    ctx = saved;
    REXLOG_INFO("Refii guest-tick pump: Inventory:wear ran sub_824BF068(component 0x{:08X}, "
                "record 0x{:08X})", appearance, static_cast<uint32_t>(hash));
    return true;
  }

  if (op != "use" && op != "gift" && op != "equip" && op != "outfit") {
    REXLOG_INFO("Refii guest-tick pump: inventory op '{}' is not one this knows", op);
    return true;
  }

  // Component 13 is the inventory the handle is looked up in; component 33 is what the gift
  // itself is performed against, and `sub_823E0FF8` wants the owning ENTITY rather than either.
  const uint32_t inventory = HeroComponent(base, hero, 13u);
  const uint32_t giving = op == "gift" ? HeroComponent(base, hero, 33u) : 0u;
  if (inventory == 0u || (op == "gift" && giving == 0u)) {
    REXLOG_INFO("Refii guest-tick pump: Inventory:{} — no hero component 13/33 (hero 0x{:08X})",
                body, hero);
    return true;
  }

  // `equip` is the guest's OWN activation switch, and it covers more than weapons:
  // `sub_826C9AB8` reads the item's `Category` and routes it — 1 to `sub_826CA098` (the weapon
  // slots), 5/6 and the key-item set to `sub_826CA170` (use), 17..23 to `sub_826CB218` (wear).
  // It reads no screen, no menu and no Lua: its first argument is the object at
  // `*(gameRoot + 52)`, which holds the two equipped-weapon handles at `+0` and `+16`, and both
  // of the guest's own callers fetch it from exactly that global.
  //
  // Player one's rows on the items and clothing screens never get here: they commit through
  // the screen's own answer (CommitItemThroughScreen). This is player two's book, and any
  // inventory action posted with no list screen up.
  //
  // `use` goes through the same switch, because that is the screen's own call for it: event 3
  // runs `sub_826C9AB8(slots, screen+396, 1)` at 0x826D1BEC for every category. Categories
  // 2..4/0x10/0x18/0x1A and 5/6 reach `sub_826CA170`, which uses the item (`sub_823E0FF8`,
  // 0x826ca20c) and then, when its InventoryItemComponent states `ExitPauseMenuOnUse`
  // (0x826ca250), leaves the pause menu itself: `sub_8236ED98(*(0x83496B08 + 28), 1)` at
  // 0x826ca278. `sub_823E0FF8` alone uses the item without that close; any confirmation is the
  // item's own use script (`OnCarriedActionUse*` :Update()).
  const uint32_t activate = refii::guest::Address(0x826C9AB8u);
  // Player two's own hero, acted on directly: an online joiner's, or local player two's while
  // their pause is open (the guest's pending records land on player one either way).
  const bool joiner = direct;
  const bool switched = op == "equip" || (op == "use" && !joiner);
  PPCFunc* find = FindGuestFunction(refii::guest::Address(0x824F1C90u));
  PPCFunc* release = FindGuestFunction(refii::guest::Address(0x82764DC0u));
  PPCFunc* act = FindGuestFunction(
      op == "gift" ? refii::guest::Address(0x823F2718u) : (switched ? activate : refii::guest::Address(0x823E0FF8u)));
  if (find == nullptr || release == nullptr || act == nullptr) {
    REXLOG_INFO("Refii guest-tick pump: Inventory:{} — the handle pair is not callable here",
                body);
    return true;
  }

  // Claimed by lowering r1 so a callee frames below the handle it is passed, not over it.
  const PPCContext saved = ctx;
  const uint32_t frame = (ctx.r1.u32 - 0x200u) & ~0xFu;
  const uint32_t handle = frame + 0x20u;
  const uint32_t text = frame + 0x40u;
  for (uint32_t word = 0; word < 4u; ++word) {
    StoreGuestU32(base, handle + (word * 4u), 0u);
  }

  ctx = saved;
  ctx.r1.u32 = frame;
  ctx.r3.u32 = handle;
  ctx.r4.u32 = inventory;
  ctx.r5.u32 = static_cast<uint32_t>(hash);
  find(ctx, base);
  ctx = saved;

  const uint32_t item = LoadGuestU32(base, handle);
  if (item == 0u) {
    REXLOG_INFO("Refii guest-tick pump: Inventory:{} — record 0x{:08X} is not in the hero's "
                "inventory", body, static_cast<uint32_t>(hash));
    return true;
  }

  if (op == "outfit") {
    DressOutfit(ctx, base, saved, frame, handle, hero, static_cast<uint32_t>(hash), direct);
  } else if (op == "equip" && joiner) {
    // A JOINER EQUIPS PLAYER TWO DIRECTLY. The pending-record slot at `GUI+52` (record A) is
    // applied at pause exit by `sub_8236ED98` to the entity at EM+104 — player ONE — on every
    // side (0x8236ee98..0x8236eeb0); only `GUI+80` (record B) reaches EM+112, and nothing the
    // menus write goes there. So on a client the guest's own weapons book arms the host's
    // copy instead of the player's hero. `sub_824ABA28(comp28, recordId, 0)` is what that
    // pending record runs in the end (message class 0x820CF028 slot 3), on the right body.
    // The step-by-step equip (EquipRecord), because the guest's own `sub_824ABA28` leaves a
    // client's instantiated item unactivated and the attach declines it.
    std::string report;
    ctx = saved;
    const bool drawn = EquipRecord(ctx, base, hero, static_cast<uint32_t>(hash), report);
    ctx = saved;
    REXLOG_INFO("Refii guest-tick pump: Inventory:equip — joiner {} record 0x{:08X} on its hero "
                "0x{:08X}:{}", drawn ? "drew" : "could NOT draw", static_cast<uint32_t>(hash),
                hero, report);
  } else if (switched) {
    const uint32_t root = LoadGuestU32(base, refii::guest::Address(0x83496B08u));
    const uint32_t slots = root != 0u ? LoadGuestU32(base, root + 52u) : 0u;

    ctx = saved;
    ctx.r1.u32 = frame;
    ctx.r3.u32 = slots;
    ctx.r4.u32 = handle;
    ctx.r5.u32 = 1u;  // picks the local hero's weak-ref slot inside sub_826CA170
    act(ctx, base);
    ctx = saved;
  } else if (op == "gift") {
    ctx = saved;
    ctx.r1.u32 = frame;
    ctx.r3.u32 = giving;
    ctx.r4.u32 = handle;
    act(ctx, base);
    ctx = saved;
  } else {
    // The empty reason string `sub_823E1370` builds at 0x823E14CC before the use.
    ctx = saved;
    ctx.r1.u32 = frame;
    ctx.r3.u32 = text;
    ctx.r4.u32 = refii::guest::Address(0x82000CA0u);   // byte_82000CA0, the empty literal
    ctx.r5.u32 = 0xFFFFFFFFu;   // -1 = NUL-terminated
    rex_lhCharString_CtorFromCStr(ctx, base);
    ctx = saved;

    ctx = saved;
    ctx.r1.u32 = frame;
    ctx.r3.u32 = LoadGuestU32(base, inventory + 4u);  // the owning entity
    ctx.r4.u32 = handle;
    ctx.r5.u32 = text;
    act(ctx, base);
    const bool used = (ctx.r3.u32 & 0xFFu) != 0u;
    ctx = saved;

    ctx = saved;
    ctx.r1.u32 = frame;
    ctx.r3.u32 = text;
    rex_lhCharString_Dtor(ctx, base);
    ctx = saved;

    // A joiner's hero is not the one `sub_826CA170` resolves (`sub_8222C1B8`, the local
    // player), so the switch is not used here; its tail is, as `sub_826CA170` runs it after a
    // successful use (0x826ca224..0x826ca278): the item's InventoryItemComponent
    // (`sub_822A99E8(sub_821B24F8(item + 4), "InventoryItemComponent")`, 0x826ca238
    // `mr r4, r7` with r7 the literal at 0x820AF11C), its bool
    // `ExitPauseMenuOnUse` (`sub_82310290`, literal at 0x820D43B4), and on true
    // `sub_8236ED98(*(0x83496B08 + 28), 1)`.
    PPCFunc* record = FindGuestFunction(refii::guest::Address(0x821B24F8u));
    PPCFunc* component = FindGuestFunction(refii::guest::Address(0x822A99E8u));
    PPCFunc* flag = FindGuestFunction(refii::guest::Address(0x82310290u));
    PPCFunc* leave = FindGuestFunction(refii::guest::Address(0x8236ED98u));
    // THE REF'S CURRENT ITEM, re-read after the use: a use that consumed the last unit destroys
    // the object, which unregisters and zeroes the ref (the quick slot crashed in exactly this
    // state, sub_82764DC0+0x6D reading the null item's ref list at +0x2C). Nothing is left to ask
    // or to release then.
    const uint32_t remaining = LoadGuestU32(base, handle);
    if (used && remaining != 0u && record != nullptr && component != nullptr && flag != nullptr &&
        leave != nullptr) {
      ctx = saved;
      ctx.r1.u32 = frame;
      ctx.r3.u32 = remaining + 4u;
      record(ctx, base);
      const uint32_t item_record = ctx.r3.u32;
      ctx = saved;
      ctx.r1.u32 = frame;
      ctx.r3.u32 = item_record;
      ctx.r4.u32 = refii::guest::Address(0x820AF11Cu);  // "InventoryItemComponent"
      component(ctx, base);
      const uint32_t item_component = ctx.r3.u32;
      bool exits = false;
      if (item_component != 0u) {
        ctx.r1.u32 = frame;
        ctx.r3.u32 = item_component;
        ctx.r4.u32 = refii::guest::Address(0x820D43B4u);  // "ExitPauseMenuOnUse"
        flag(ctx, base);
        exits = (ctx.r3.u32 & 0xFFu) != 0u;
      }
      ctx = saved;

      const uint32_t root = LoadGuestU32(base, refii::guest::Address(0x83496B08u));
      const uint32_t gui = root != 0u ? LoadGuestU32(base, root + 28u) : 0u;
      if (exits && gui != 0u) {
        ctx.r1.u32 = frame;
        ctx.r3.u32 = gui;
        ctx.r4.u32 = 1u;
        leave(ctx, base);
        ctx = saved;
        REXLOG_INFO("Refii guest-tick pump: Inventory:use — the item states ExitPauseMenuOnUse; "
                    "left the pause menu (sub_8236ED98)");
      }
    }
  }

  if (LoadGuestU32(base, handle) != 0u) {
    ctx = saved;
    ctx.r1.u32 = frame;
    ctx.r3.u32 = LoadGuestU32(base, handle);
    ctx.r4.u32 = handle;
    release(ctx, base);
    ctx = saved;
  }

  REXLOG_INFO("Refii guest-tick pump: Inventory:{} ran on item 0x{:08X} (record 0x{:08X}) — no "
              "GUI event, no screen", op, item, static_cast<uint32_t>(hash));
  return true;
}

/// THE ITEM BOOKS' ROWS, as levers that carry the acting player, the row's ticket and its record:
/// `Items:<p>:use|equip:<ticket>:<record>` on the items screen (15), `Clothing:<p>:wear|outfit:<ticket>:
/// <record>` and `Clothing:<p>:remove:<event type 91..97>` on the clothing screen (16). The items screen's
/// row is its own handler's type 3 arm on that ticket (CommitItemThroughScreen); the clothing screen's
/// are gui_screen_stub_clothing.cpp's, decided on the player's worn list. An online joiner's hero is not
/// the one the guest's screen code picks (the `game+140` chooser), and player two's items commit to no
/// pending record of their own: both take the direct inventory call (DispatchInventoryAction).
/// The items book's rows restated after a use or an equip from them: its list's own populate, h+24 vf+4
/// (`sub_829B0A50`: `li r4,1 ; b sub_829B0A58`, the mode at h+372 read inside, 0x829B0B7C) — the call the
/// guest's row answer makes after a weapon (0x826D1C08 / disc 0x8257D654), here after every use so a count
/// the use changed and a stack it emptied are stated again (the remaster's live list; the guest's own
/// populate states every row). Under the listed hero (ListedHeroScope). Nothing while screen 15 is not up.
void RestateItemsList(PPCContext& ctx, uint8_t* base) {
  const uint32_t screen = InGameScreenHandler(ctx, base, 15u);
  if (screen == 0u || refii_bridge_stated_screen_now() != 15u) {
    return;
  }
  ListedHeroScope listed(base, 15u);
  const PPCContext saved = ctx;
  const uint32_t list = screen + 24u;
  const uint32_t vtable = LoadGuestU32(base, list);
  const uint32_t populate = LooksLikeGuestPointer(vtable) ? LoadGuestU32(base, vtable + 4u) : 0u;
  PPCFunc* fn = populate != 0u ? FindGuestFunction(populate) : nullptr;
  if (fn != nullptr) {
    ctx.r1.u32 = (saved.r1.u32 - 0x200u) & ~0xFu;
    ctx.r3.u32 = list;
    SafeInvokeGuestCall(fn, ctx, base);
  }
  ctx = saved;
}

bool DispatchItemLever(PPCContext& ctx, uint8_t* base, bool clothing, uint32_t player, const std::string& verb,
                       uint32_t ticket, uint32_t value) {
  const std::string direct = (clothing && verb == "remove" ? std::string("unequip") : verb) + ":" + std::to_string(value);
  if (clothing && !IsCoopJoiner(base) && refii::vfs::DispatchClothing(ctx, base, player, verb, ticket, value)) {
    return true;
  }
  return DispatchInventoryAction(ctx, base, direct, 0, ticket);
}

extern "C" int32_t RefiiFurnitureUpgrade(PPCContext* ctx, uint8_t* base, int32_t level, int32_t player);

/// `Furniture:<p>:upgrade:<level>` — the transaction of the furniture screen's A (`sub_826B6A18`, disc
/// `sub_8259BB80`) on the level the row states (`Float1 + (Float2 << 16)`, as `sub_8299D910`
/// spawned it), never the guest's highlight; RefiiFurnitureUpgrade (mod_events.cpp) runs it for the
/// player named. `Furniture:<p>:close` — the screen's B (gui_screen_stub_furniture.cpp). Both only
/// while screen 26 stands with no box over it.
bool DispatchFurnitureAction(PPCContext& ctx, uint8_t* base, const std::string& body) {
  constexpr const char* kUpgrade = "upgrade:";
  std::string rest = body;
  const uint32_t player = TakeActingPlayer(rest);
  const bool upgrade = rest.rfind(kUpgrade, 0) == 0;
  const char* digits = upgrade ? rest.c_str() + std::strlen(kUpgrade) : rest.c_str();
  char* end = nullptr;
  const long level = upgrade ? std::strtol(digits, &end, 10) : -1;
  const bool levelled = upgrade && end != nullptr && end != digits && *end == '\0' && level >= 0 && level <= 0xFFFF;
  if (player == 0u || (rest != "close" && !levelled)) {
    REXLOG_INFO("Refii guest-tick pump: 'Furniture:{}' is not <p>:upgrade:<level> or <p>:close — dropped", body);
    return true;
  }
  uint32_t mode = 1u;
  const bool up = refii_bridge_stated_pause(&mode, nullptr, nullptr) != 0u;
  if (!up || mode != 1u || refii_bridge_stated_screen_now() != 26u) {
    REXLOG_INFO("Refii guest-tick pump: 'Furniture:{}' refused (screen {}, modal mode {})", body,
                refii_bridge_stated_screen_now(), mode);
    return true;
  }
  if (!levelled) {
    return refii::vfs::DispatchFurnitureClose(ctx, base, player);
  }
  static constexpr const char* kOutcome[] = {
      "upgraded", "no targeted piece", "the piece is not upgradeable", "already installed",
      "no unlock card for it", "the level was not set (cancelled or refused)",
      "player two has no redecorating of their own, refused",
  };
  const int32_t outcome =
      RefiiFurnitureUpgrade(&ctx, base, static_cast<int32_t>(level), static_cast<int32_t>(player));
  if (outcome == 0) {
    refii::vfs::FurnitureUpgraded();
  }
  REXLOG_INFO("Refii guest-tick pump: Furniture:{}:upgrade:{} — {}", player, level,
              outcome >= 0 && outcome < 7 ? kOutcome[outcome] : "?");
  return true;
}

/// `Decoration:<p>:…` and `RealEstate:<p>:…` — see their prefixes; each only while its own screen
/// stands with no box over it (a box takes its own answer, `QueryAnswer`).
bool DispatchHouseScreenLever(PPCContext& ctx, uint8_t* base, const std::string& name, bool estate) {
  std::string rest = name.substr(std::strlen(estate ? kActionRealEstatePrefix : kActionDecorationPrefix));
  const uint32_t player = TakeActingPlayer(rest);
  unsigned long value = 0ul;
  const bool valued = std::sscanf(rest.c_str(), estate ? "event:%lu" : "mount:%lu", &value) == 1;
  const bool plain = rest == "close" || (!estate && rest == "takedown");
  if (player == 0u || (!valued && !plain)) {
    REXLOG_INFO("Refii guest-tick pump: '{}' is not {} — dropped", name,
                estate ? "RealEstate:<p>:event:<110..120> or RealEstate:<p>:close"
                       : "Decoration:<p>:mount:<ticket>, Decoration:<p>:takedown or Decoration:<p>:close");
    return true;
  }
  // The modal MODE, not its id: real estate's buy-denied notice is a box with id 0 (mode 2).
  uint32_t mode = 1u;
  const bool up = refii_bridge_stated_pause(&mode, nullptr, nullptr) != 0u;
  if (!up || mode != 1u || refii_bridge_stated_screen_now() != (estate ? 28u : 27u)) {
    REXLOG_INFO("Refii guest-tick pump: '{}' refused (screen {}, modal mode {})", name,
                refii_bridge_stated_screen_now(), mode);
    return true;
  }
  if (estate) {
    return valued ? refii::vfs::DispatchRealEstateEvent(ctx, base, player, static_cast<uint32_t>(value))
                  : refii::vfs::DispatchRealEstateClose(ctx, base, player);
  }
  return refii::vfs::DispatchDecoration(ctx, base, player, valued ? "mount" : rest, static_cast<uint32_t>(value));
}

/// `Crime:<p>:…` and `Query:<p>:…` — see their prefixes.
bool DispatchCardScreenLever(PPCContext& ctx, uint8_t* base, const std::string& name, bool crime) {
  std::string rest = name.substr(std::strlen(crime ? kActionCrimePrefix : kActionQueryPrefix));
  const uint32_t player = TakeActingPlayer(rest);
  const bool known = crime ? rest == "pay" || rest == "resist" || rest == "service" : rest == "close" || rest == "rename";
  if (player == 0u || !known) {
    REXLOG_INFO("Refii guest-tick pump: '{}' is not {} — dropped", name,
                crime ? "Crime:<p>:pay|resist|service" : "Query:<p>:close|rename");
    return true;
  }
  uint32_t mode = 1u;
  const bool up = refii_bridge_stated_pause(&mode, nullptr, nullptr) != 0u;
  if (!up || mode != 1u || refii_bridge_stated_screen_now() != (crime ? 21u : 10u)) {
    REXLOG_INFO("Refii guest-tick pump: '{}' refused (screen {}, modal mode {})", name,
                refii_bridge_stated_screen_now(), mode);
    return true;
  }
  return crime ? refii::vfs::DispatchCrime(ctx, base, player, rest) : refii::vfs::DispatchQuery(ctx, base, player, rest);
}

/// `SafetyToggle:<p>` and `TargetQuery:<p>` — in play alone: no in-game screen up and no box.
bool DispatchHudLever(PPCContext& ctx, uint8_t* base, const std::string& name, bool safety) {
  std::string rest = name.substr(std::strlen(safety ? kActionSafetyTogglePrefix : kActionTargetQueryPrefix)) + ":";
  const uint32_t player = TakeActingPlayer(rest);
  if (player == 0u || !rest.empty()) {
    REXLOG_INFO("Refii guest-tick pump: '{}' is not {}<1|2> — dropped", name,
                safety ? kActionSafetyTogglePrefix : kActionTargetQueryPrefix);
    return true;
  }
  // StatedInGameScreen: the in-game screen set is up with no box; 31 is its "no screen".
  uint32_t screen = 0u;
  if (!StatedInGameScreen(&screen) || screen != 31u) {
    REXLOG_INFO("Refii guest-tick pump: '{}' refused — not in play (stated screen {})", name,
                refii_bridge_stated_screen_now());
    return true;
  }
  return safety ? refii::vfs::DispatchSafetyToggle(ctx, base, player) : refii::vfs::DispatchTargetQuery(ctx, base, player);
}

/// The `Player` with this id, out of the `PlayerManager` at `game+156`.
///
/// NOT `PlayerObject`, which walks `game+88` — the ENTITY/WORLD manager, the same
/// `**(*(game+88)+4)` that `sub_82389FB8` hands to `sub_82365258`. It answers a party holder
/// that suits the dog walk and is not a `Player` at all: its `+36` is a pointer where a
/// PlayerID belongs. Publishing it through `sub_82371860` put a non-Player into
/// `*(App+20)+12`, and the input dispatch then read it as one every frame —
/// `sub_82403350` -> `Player::GetEntity` (`sub_8229EE78`) -> `*(obj+16)` -> SIGSEGV.
///
/// The manager's vector is 12-byte `{PlayerID, Player*, control block}` entries between
/// `manager+8` and `manager+12`, sorted by id — the layout `sub_82446928` walks (0x82446A70)
/// and `sub_82314D30` inserts into. Walked here rather than calling `sub_82264230` so the
/// pump needs no guest call on the tick thread.
uint32_t PlayerWithId(uint8_t* base, uint32_t id) {
  const uint32_t kGameRoot = refii::guest::Address(0x83496AB8u);
  constexpr uint32_t kPlayerManager = 156u;
  constexpr uint32_t kEntryStride = 12u;
  constexpr uint32_t kMaxEntries = 8u;
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  const uint32_t root = LoadGuestU32(base, kGameRoot);
  const uint32_t game = plausible(root) ? LoadGuestU32(base, root + 12u) : 0u;
  const uint32_t manager = plausible(game) ? LoadGuestU32(base, game + kPlayerManager) : 0u;
  if (!plausible(manager)) {
    return 0u;
  }

  const uint32_t begin = LoadGuestU32(base, manager + 8u);
  const uint32_t end = LoadGuestU32(base, manager + 12u);
  if (!plausible(begin) || end < begin || ((end - begin) % kEntryStride) != 0u ||
      (end - begin) > kEntryStride * kMaxEntries) {
    return 0u;
  }

  for (uint32_t entry = begin; entry != end; entry += kEntryStride) {
    if (LoadGuestU32(base, entry) == id) {
      const uint32_t player = LoadGuestU32(base, entry + 4u);
      return plausible(player) ? player : 0u;
    }
  }

  return 0u;
}

/// The local `Player` — id 1 when it exists, else id 2.
///
/// "The party's first player", which several dispatches want, but as an actual `Player`. On a
/// JOINING instance only id 2 is present (a run's trace showed one publish, `id=2`, with
/// handler1 still 0), so the fallback is the henchman's own player, not a failure.
///
/// This replaced `PlayerObject` at every site that hands the value to a guest routine expecting
/// a `Player`. `PlayerObject` walks `game+88` and answers `**(*(game+88)+4)`, the ENTITY/WORLD
/// manager — `sub_82389FB8` gives that same expression to `sub_82365258` as the entity manager.
/// Its `+36` is a pointer where a PlayerID belongs, and `+16` is not a weak_ptr control block,
/// so `sub_8236F400` faulted on `v6 = *v5` (0x8236F428) reading `*(a2+16)`. The two only agreed
/// while our own `RegisterPlayer` was publishing that same wrong object into the handler slot.
/// Only `SampleDog` still wants `PlayerObject`: the dog handle is at `+196`, and a `Player` is
/// 0x48 bytes.
uint32_t FirstPlayer(uint8_t* base) {
  const uint32_t one = PlayerWithId(base, 1u);
  return one != 0u ? one : PlayerWithId(base, 2u);
}

/// The player THIS machine's pad drives — the guest's own rule in `sub_8240B2A0` (TU1
/// `sub_826B83D0`): id 1 when the net context's bytes +52 and +53 are both set (host, and solo
/// reads as host), else id 2 (a client). The host-owned menus — spells, blade, expression
/// wheel — act for this player: on a joiner `FirstPlayer` is the sync's copy of the HOST's
/// hero, so the spells menu opened for player one there and the joiner's own never drew.
uint32_t LocalPlayer(uint8_t* base) {
  const uint32_t net = CoopNetworkManager(base);
  const bool both = net != 0u && base[net + 52u] != 0u && base[net + 53u] != 0u;
  const uint32_t player = PlayerWithId(base, both || net == 0u ? 1u : 2u);
  return player != 0u ? player : FirstPlayer(base);
}

/// Is player two standing in their own hero's body rather than the pickbox henchman? The bit is
/// refii_bridge_two_is_hero (core/hero_partners.cpp, refii_vfs, so the host bridge's side reads the
/// same one); RefiiCoopGetOwnHero is its name for the hooks.
///
/// A BIT, NOT A COMMAND: nothing applies it here. `coop_player_hooks.cpp` overrides
/// `sub_8233C508`, the guest's creature-name table for player two, and reads this to answer it —
/// so the guest's own spawn, re-skin, level-change and save-load paths all build the right body
/// without us racing any of them. The two Refii instances agree over Refii's own co-op channel;
/// the guest has no concept of this, so there is nothing of the title's to keep in step.

/// Local co-op on split screen (each local player their own camera) rather than one shared
/// screen. Off until the host states it: split mode, or hybrid while split.
std::atomic<bool> g_coop_split_screen{false};

/// Player two's dog, as a mod handle (it resolves to nothing once the dog is destroyed). The
/// guest keeps one "the dog" slot (players+192, hero one's); Fable 3 keeps a record per owner.
/// The dog itself says whose it is: the host creates it with the instance name
/// kCoopPartnerDogName, which the create message carries to the joiner's replica (the name
/// string in `sub_82337A98`'s spawn message, rebuilt by the client at 0x8232EB18). Recorded by
/// the first bind that sees it; cleared by Lua before a breed change replaces it.
/// Guest tick thread only.
uint32_t g_coop_partner_dog = 0u;

/// Set while the pump asks a guest page question for player two's book (local co-op, player
/// two's pause). The tab predicate (`sub_826B83D0`) inlines the guest's local-player rule — id 1
/// when net+52 and +53 are set, else 2, then `sub_82264230(pm, id)` (0x826B8434) — which is always
/// player one in local co-op. RefiiCoopMenuPlayer states id 2 there while this is set. Guest tick
/// thread only.
bool g_menu_player_two = false;

/// The player working a job (core Lua, `Refii._job_worker`): 2 while player two works one, so
/// the job's meter takes player two's pad (coop_ui_hooks.cpp). Guest tick thread only.
int g_coop_job_worker = 0;

/// Who the next real-estate screen buys for (core Lua, `Refii._estate_buyer`, stated as the sale
/// sign opens it): 2 for player two. Taken by the screen's resolve. Guest tick thread only.
int g_coop_estate_buyer = 0;
constexpr const char* kCoopPartnerDogName = "RefiiPartnerDog";

/// The guest's memory base, for the C entry points the hooks payload calls without one. Set on
/// the first guest tick; it never moves.
std::atomic<uint8_t*> g_guest_base{nullptr};

/// The joiner's last exit use, waiting for the host's exit at that position to update. Guest
/// tick thread only (posted and taken there).
struct CoopExitUse {
  bool pending = false;
  float at[3] = {0.0f, 0.0f, 0.0f};
  uint32_t frame = 0;  // the guest's frame counter (0x83319650) when it arrived
};
CoopExitUse g_coop_exit_use;

/// Defined at the foot of this file, outside `refii::vfs`, so the hooks payload can reach it.
extern "C" void RefiiCoopSetOwnHero(int on);

/// coop_player_hooks.cpp: destroys the henchman weapons the host's arm made that no one holds.
extern "C" void RefiiCoopDropLooseHenchmanPair(void* ctx, uint8_t* base);

/// A component off an entity, by id — the guest's own layout.
///
/// `entity+0x24` is a presence bitmask (bit `id&31` of word `id>>5`); the `(id, ptr)` pairs run
/// from `entity+0x48` to `entity+0x4C`; `entity+0x8C`, when set, is a byte table mapping id to a
/// pair slot. `sub_8238CDC8`, the guest's own component writer, sets that bit at 0x8238D238.
uint32_t EntityComponent(uint8_t* base, uint32_t entity, uint32_t id) {
  if (id >= 256u || !LooksLikeGuestPointer(entity)) {
    return 0u;
  }

  const uint32_t present = LoadGuestU32(base, entity + 0x24u + (id / 32u) * 4u);
  if (((present >> (id % 32u)) & 1u) == 0u) {
    return 0u;
  }

  const uint32_t pairs = LoadGuestU32(base, entity + 0x48u);
  const uint32_t pairs_end = LoadGuestU32(base, entity + 0x4Cu);
  if (!LooksLikeGuestPointer(pairs) || pairs_end < pairs || (pairs_end - pairs) > 8u * 512u) {
    return 0u;
  }

  const uint32_t accelerator = LoadGuestU32(base, entity + 0x8Cu);
  if (LooksLikeGuestPointer(accelerator)) {
    const uint32_t at = pairs + 8u * LoadGuestU8(base, accelerator + id);
    if (at + 8u <= pairs_end && LoadGuestU32(base, at) == id) {
      const uint32_t component = LoadGuestU32(base, at + 4u);
      return LooksLikeGuestPointer(component) ? component : 0u;
    }
  }

  for (uint32_t at = pairs; at + 8u <= pairs_end; at += 8u) {
    if (LoadGuestU32(base, at) == id) {
      const uint32_t component = LoadGuestU32(base, at + 4u);
      return LooksLikeGuestPointer(component) ? component : 0u;
    }
  }

  return 0u;
}

/// Player one's entity, or 0 — the adjacent handle object, `EM+104` with its pointer at `+108`
/// (`sub_8222C1B8`, the twin of the accessor below). Same layout, one slot earlier.
uint32_t CoopPlayerOneEntity(uint8_t* base) {
  const auto step = [&](uint32_t at, uint32_t offset) -> uint32_t {
    return LooksLikeGuestPointer(at) ? LoadGuestU32(base, at + offset) : 0u;
  };

  const uint32_t manager =
      step(step(step(step(LoadGuestU32(base, refii::guest::Address(0x83496AB8u)), 12u), 88u), 4u), 0u);
  const uint32_t holder = LooksLikeGuestPointer(manager) ? refii_bridge_hero_one_holder(base, manager) : 0u;
  const uint32_t entity = step(holder, 0u);
  return LooksLikeGuestPointer(entity) ? entity : 0u;
}

/// Player two's entity, or 0.
///
/// INLINED RATHER THAN CALLED. `sub_828E5A78` is the guest's own accessor and `sub_8246C970`
/// proves it IS player two — its `a2` is a bool and the FALSE arm (0x8246C9B8) takes this one,
/// the arm the shipped "HenchmanAbilities" screen drives, with player one on the adjacent slot
/// `EM+104`. But THE RECOMPILER NEVER EMITTED IT: it is the one address in this file's set that
/// `librefii_generated.so` does not define, and referencing it stopped that library loading at
/// all, which takes the whole guest down with it.
///
/// No call is needed. The body is two loads — the handle block at `EM+112` holds its object at
/// `EM+116`, and the entity is one more deref; the remaining branch is the stale-handle cleanup,
/// which returns 0 anyway.
uint32_t CoopPlayerTwoEntity(uint8_t* base) {
  const auto step = [&](uint32_t at, uint32_t offset) -> uint32_t {
    return LooksLikeGuestPointer(at) ? LoadGuestU32(base, at + offset) : 0u;
  };

  // `**(*(*(0x83496AB8)+12)+88)+4)`, exactly as `sub_8246C970` reads it at 0x8246C9A8.
  const uint32_t manager =
      step(step(step(step(LoadGuestU32(base, refii::guest::Address(0x83496AB8u)), 12u), 88u), 4u), 0u);

  const uint32_t held = step(manager, 116u);
  const uint32_t entity = step(held, 0u);
  return LooksLikeGuestPointer(entity) ? entity : 0u;
}

/// The stored hero record per slot — what DispatchHeroState fills, applied by CoopApplyHero.
/// The text fields keep the shapes the apply steps parse (see DispatchHeroState for each).
struct CoopHeroRecord {
  uint32_t generation = 0u;   // the HeroState version; 0 = nothing to apply
  uint32_t melee = 0u;        // weapon record ids to draw (ApplyEquip)
  uint32_t ranged = 0u;
  std::string stats;          // HeroSave block text (28 fields), player two only
  std::string skills;         // ability ranks text, player two only
  std::string build;          // body-shape text, player two only
  uint32_t face = 0u;         // character-creation record, player two only
  std::string name;           // display name, logged only
  uint32_t dog_definition = 0u;  // the dog's breed (definition), 0 none
  int32_t dog_searching = 0;     // its two levels
  int32_t dog_combat = 0;
  std::string dog_tricks;        // learned tricks, GDB names, comma-separated
  std::vector<uint32_t> dog_worn;  // what the dog wears (its collar), def hashes
  std::string dog_name;          // the name its player gave it, empty for none
};
CoopHeroRecord g_coop_record[2];
std::map<uint32_t, std::string> g_coop_skills_applied;  // body -> ranks last applied
std::map<uint32_t, std::string> g_coop_build_applied;   // body -> morphs last applied

/// The ability ranks — CoopHeroRecord::skills, hex ids 0..14 in order (0 unused).
///
/// IDEMPOTENT AND RE-APPLIED, not fired once. Re-typing player two's entity rebuilds its
/// components from the GDB, so ranks written before a re-skin are gone after it — and the co-op
/// screen re-skins every frame. The tick therefore restates this, and the pass writes only the
/// ids that actually differ, so it settles to a no-op once the body has stopped changing.
/// True when applied or nothing to apply; false when the body has no stats component yet.
bool ApplySkills(PPCContext& ctx, uint8_t* base, uint32_t entity, const std::string& levels) {
  constexpr uint32_t kStatsComponent = 57u;
  constexpr uint32_t kFirstAbility = 1u;
  constexpr uint32_t kLastAbility = 14u;

  if (entity == 0u || levels.empty()) {
    return true;
  }

  const uint32_t stats = EntityComponent(base, entity, kStatsComponent);
  if (stats == 0u) {
    return false;
  }

  if (g_coop_skills_applied[entity] == levels) {
    return true;
  }
  g_coop_skills_applied[entity] = levels;

  uint32_t id = 0u;
  uint32_t written = 0u;
  for (std::size_t at = 0; at <= levels.size() && id <= kLastAbility; ++id) {
    const std::size_t comma = levels.find(',', at);
    const std::string field = levels.substr(at, comma == std::string::npos ? comma : comma - at);

    at = comma == std::string::npos ? levels.size() + 1u : comma + 1u;
    if (id < kFirstAbility || field.empty()) {
      continue;
    }

    const uint32_t rank = static_cast<uint32_t>(std::strtoul(field.c_str(), nullptr, 16));

    // The rank slot the writer itself uses — `4 * (id + 68)` off the component, 0x824E2F58.
    if (LoadGuestU32(base, stats + 4u * (id + 68u)) == rank) {
      continue;
    }

    PPCContext work = ctx;
    work.r3.u32 = stats;
    work.r4.u32 = id;
    work.r5.u32 = rank;
    work.r6.u32 = 1u;  // force the replication to the peer
    rex_CECStats_SetAbilityLevel(work, base);
    ++written;
  }

  if (written != 0u) {
    REXLOG_INFO("Refii guest-tick pump: CoopHeroSkills — {} rank(s) applied to player two "
                "(entity 0x{:08X}, stats 0x{:08X})", written, entity, stats);
  }

  return true;
}

/// The body shape — CoopHeroRecord::build, "strength,skill,will,fat,height,tan" off the HeroState.
///
/// The three body-morph percentages live on the SAME component as the ability ranks — CECStats,
/// id 57 — at `+0x40` strength, `+0x44` skill, `+0x48` will. Those offsets are not taken on
/// trust: `RefiiHostGetHeroVitals` has been reading the hero's own build from exactly them since
/// ABI 78.
///
/// Written directly rather than through the guest's own setters, which take DELTAS and fire
/// gossip events ("ModifyFat") — a character being applied is not a character being changed. The
/// flush afterwards is the guest's, so the fan-out to the morph and clothing components is its
/// own. Values are clamped here because a direct write skips the setters' clamp.
/// True when applied or nothing to apply; false when the body has no stats component yet.
bool ApplyBuild(PPCContext& ctx, uint8_t* base, uint32_t entity, const std::string& build) {
  constexpr uint32_t kStatsComponent = 57u;
  // strength, skill, will (vitals reader, ABI 78), then fat `+56` (`sub_824D7868`), height
  // `+60` (`sub_824D7A68`, the one -1..1 scalar) and tan `+52` (`sub_824D77F8`).
  constexpr uint32_t kSlots[] = {0x40u, 0x44u, 0x48u, 0x38u, 0x3Cu, 0x34u};
  constexpr float kLow[] = {0.0f, 0.0f, 0.0f, 0.0f, -1.0f, 0.0f};

  if (entity == 0u || build.empty()) {
    return true;
  }

  const uint32_t stats = EntityComponent(base, entity, kStatsComponent);
  if (stats == 0u) {
    return false;
  }

  if (g_coop_build_applied[entity] == build) {
    return true;
  }
  g_coop_build_applied[entity] = build;

  uint32_t written = 0u;
  std::size_t at = 0;
  for (uint32_t slot = 0; slot < 6u && at <= build.size(); ++slot) {
    const std::size_t comma = build.find(',', at);
    const std::string field = build.substr(at, comma == std::string::npos ? comma : comma - at);

    at = comma == std::string::npos ? build.size() + 1u : comma + 1u;
    if (field.empty()) {
      continue;
    }

    float value = std::strtof(field.c_str(), nullptr);
    value = value < kLow[slot] ? kLow[slot] : value > 1.0f ? 1.0f : value;

    uint32_t bits = 0u;
    std::memcpy(&bits, &value, sizeof(bits));
    if (LoadGuestU32(base, stats + kSlots[slot]) == bits) {
      continue;
    }

    StoreGuestU32(base, stats + kSlots[slot], bits);
    ++written;
  }

  if (written != 0u) {
    PPCContext work = ctx;
    work.r3.u32 = stats;
    rex_CECStats_FlushAppearance(work, base);

    REXLOG_INFO("Refii guest-tick pump: CoopHeroBuild — {} morph(s) applied to player two "
                "(entity 0x{:08X}, stats 0x{:08X})", written, entity, stats);
  }

  return true;
}

bool IsCoopJoiner(uint8_t* base);

/// The outfits — GDB def hashes off each hero's worn walk — per player, and the entity + outfit
/// last dressed for each, so a body is dressed ONCE. The co-op screen rebuilds player two every
/// frame and `sub_824F0370` bumps a held item's count: without the latch it is a shirt a frame.
/// Index 0 is player one, 1 is player two.
// Index 2 is the co-op screen's PREVIEW entity, which wears player two's outfit.
std::vector<uint32_t> g_coop_wear[3];
uint32_t g_coop_dressed_entity[3] = {0u, 0u, 0u};
std::string g_coop_dressed_set[3];

/// One worn item's dye as its owner's HeroState states it — the item instance's type-2 record
/// (`sub_823A1E18`), read by RefiiHostGetEntityWornDyes. `slots` bit 0 is slot 1 (+4 colour,
/// +8 brightness), bit 1 slot 2 (+12, +16); a clear bit is the undyed state `sub_826CAFA8`
/// tests for (colour 0x000000FF, brightness 0).
struct CoopItemDye {
  uint32_t record = 0u;
  uint8_t slots = 0u;
  uint8_t red[2] = {0u, 0u};
  uint8_t green[2] = {0u, 0u};
  uint8_t blue[2] = {0u, 0u};
  float brightness[2] = {0.0f, 0.0f};
};

/// One scar as its owner's HeroState states it — a `CECMorph` (component 116) record at +20,
/// as `CECMorph_vf9` saves it: BodyArea mask, TextureMorphRecordID, Permanent.
struct CoopScar {
  uint32_t area = 0u;
  uint32_t record = 0u;
  uint8_t permanent = 0u;
};

/// The dyes and scars per player (index 2 the co-op preview, mirroring player two), and the
/// body + statement last restated for each, so a body takes each statement once. A re-dress
/// clears the dye latch: wearing again can hand the body different item instances.
std::vector<CoopItemDye> g_coop_dyes[3];
uint32_t g_coop_dyed_entity[3] = {0u, 0u, 0u};
std::string g_coop_dyed_set[3];
std::vector<CoopScar> g_coop_scars[3];
uint32_t g_coop_scarred_entity[3] = {0u, 0u, 0u};
std::string g_coop_scarred_set[3];

/// The inventory per player — (record, count) pairs off the HeroState, the drawn weapons
/// included — and the entity + set last given for each. See CoopGivePlayer.
std::vector<std::pair<uint32_t, uint32_t>> g_coop_give[3];
std::string g_coop_given_set[3];
uint32_t g_coop_given_entity[3] = {0u, 0u, 0u};


/// How many of a record the inventory (component 13) holds: the MS-STL list at `inv+16`
/// (sentinel), item at `node+8`, record at `item+4`, count at `item+12` — the layout the
/// inventory export walks. An item added by def hash lands here under that hash as its record
/// (`sub_8261A058` falls through to `sub_8261A508(inv, hash)` for a def with no TableEntry).
uint32_t HeldCount(uint8_t* base, uint32_t inventory, uint32_t record) {
  const uint32_t sentinel = LoadGuestU32(base, inventory + 16u);
  uint32_t node = LooksLikeGuestPointer(sentinel) ? LoadGuestU32(base, sentinel) : 0u;
  for (uint32_t hops = 0; hops < 4096u && LooksLikeGuestPointer(node) && node != sentinel; ++hops) {
    const uint32_t item = LoadGuestU32(base, node + 8u);
    if (LooksLikeGuestPointer(item) && LoadGuestU32(base, item + 4u) == record) {
      return LoadGuestU32(base, item + 12u);
    }

    node = LoadGuestU32(base, node);
  }

  return 0u;
}

/// The latch texts — exactly what each step latches, so a comparison against a latch is a
/// comparison against what that body was last given: "record:count," per inventory record,
/// "hash," per worn def hash, "area:record:permanent," per scar.
std::string CoopGiveText(int slot) {
  std::string set;
  for (const auto& record : g_coop_give[slot]) {
    set += std::to_string(record.first) + ':' + std::to_string(record.second) + ',';
  }
  return set;
}

std::string CoopWearText(int slot) {
  std::string set;
  for (uint32_t hash : g_coop_wear[slot]) {
    set += std::to_string(hash) + ',';
  }
  return set;
}

std::string CoopScarText(int slot) {
  std::string set;
  for (const CoopScar& scar : g_coop_scars[slot]) {
    set += std::to_string(scar.area) + ':' + std::to_string(scar.record) + ':' +
           std::to_string(scar.permanent) + ',';
  }
  return set;
}

/// The (record, count) pairs of a CoopGiveText latch.
std::vector<std::pair<uint32_t, uint32_t>> CoopGiveTextRecords(const std::string& text) {
  std::vector<std::pair<uint32_t, uint32_t>> records;
  for (std::size_t at = 0; at < text.size();) {
    const std::size_t comma = text.find(',', at);
    const std::string field = text.substr(at, comma == std::string::npos ? comma : comma - at);
    const std::size_t colon = field.find(':');
    if (colon != std::string::npos) {
      records.emplace_back(static_cast<uint32_t>(std::strtoul(field.c_str(), nullptr, 10)),
                           static_cast<uint32_t>(std::strtoul(field.c_str() + colon + 1, nullptr, 10)));
    }
    at = comma == std::string::npos ? text.size() : comma + 1u;
  }
  return records;
}

/// The def hashes of a CoopWearText latch.
std::vector<uint32_t> CoopWearTextHashes(const std::string& text) {
  std::vector<uint32_t> hashes;
  for (std::size_t at = 0; at < text.size();) {
    const std::size_t comma = text.find(',', at);
    hashes.push_back(static_cast<uint32_t>(std::strtoul(text.c_str() + at, nullptr, 10)));
    at = comma == std::string::npos ? text.size() : comma + 1u;
  }
  return hashes;
}

/// Puts a player's transported inventory into their body's inventory, once per (entity, set).
/// `sub_824F0820(out, comp13, record, notify)` adds exactly ONE — a held record's count is
/// bumped by one per call (0x824F08BC) — so a count is that many calls. Skipped for records the
/// body already holds at that count or more, so a restatement after a partial give completes
/// it rather than doubling it. Returns true when given (or nothing to give); false when the
/// body has no inventory yet — the caller asks again.
///
/// A NEW STATEMENT ON THE SAME BODY TAKES BACK what the last one gave and this one does not:
/// for each record the latch names, down to the new count (0 when unnamed), never past what the
/// body holds. One at a time through the guest's own `Inventory.RemoveItemOfType` core,
/// `sub_824F14B8(comp13, record)` (disc `sub_8261B1A0`): `CECInventory_FindItemByRecordHash`,
/// then `sub_824F12A0(comp13, ref, 0)` takes ONE instance (`sub_82764E70(item, ref[1])`,
/// 0x824f1378) and, when it was the last (`lwz r10,0xC` == 1, 0x824f132c), takes the item off
/// its wearer first (`sub_824F3958`).
bool CoopGivePlayer(PPCContext& ctx, uint8_t* base, int player, uint32_t entity) {
  const int slot = player - 1;
  if (entity == 0u || g_coop_give[slot].empty()) {
    return true;
  }

  const std::string set = CoopGiveText(slot);
  if (entity == g_coop_given_entity[slot] && set == g_coop_given_set[slot]) {
    return true;
  }

  const uint32_t inventory = EntityComponent(base, entity, 13u);
  if (inventory == 0u) {
    return false;
  }

  // Scratch above the callee's argument-save area: frame+0x60 and up.
  const uint32_t frame = (ctx.r1.u32 - 0x100u) & ~0xFu;
  const uint32_t ref = frame + 0x60u;

  uint32_t taken = 0u;
  PPCFunc* remove_one = FindGuestFunction(refii::guest::Address(0x824F14B8u));
  if (entity == g_coop_given_entity[slot] && remove_one != nullptr) {
    for (const auto& [record, before] : CoopGiveTextRecords(g_coop_given_set[slot])) {
      uint32_t now = 0u;
      for (const auto& stated : g_coop_give[slot]) {
        if (stated.first == record) now += stated.second;
      }
      for (uint32_t n = now; n < before && HeldCount(base, inventory, record) > now; ++n) {
        PPCContext take = ctx;
        take.r1.u32 = frame;
        take.r3.u32 = inventory;
        take.r4.u32 = record;
        if (!SafeInvokeGuestCall(remove_one, take, base)) {
          break;
        }
        ++taken;
      }
    }
  }

  g_coop_given_entity[slot] = entity;
  g_coop_given_set[slot] = set;

  uint32_t given = 0u;
  for (const auto& record : g_coop_give[slot]) {
    const uint32_t have = HeldCount(base, inventory, record.first);
    for (uint32_t n = have; n < record.second && n < 999u; ++n) {
      for (uint32_t i = 0; i < 4u; ++i) StoreGuestU32(base, ref + 4u * i, 0u);

      PPCContext give = ctx;
      give.r1.u32 = frame;
      give.r3.u32 = ref;
      give.r4.u32 = inventory;
      give.r5.u32 = record.first;
      give.r6.u32 = 1u;
      rex_CECInventory_AddOneOfRecord(give, base);

      const uint32_t item = LoadGuestU32(base, ref);
      if (item != 0u) {
        PPCContext release = ctx;
        release.r1.u32 = frame;
        release.r3.u32 = item;
        release.r4.u32 = ref;
        rex_lhTrackedObject_RemoveHandleRef(release, base);
        ++given;
      }
    }
  }

  REXLOG_INFO("Refii guest-tick pump: CoopGive — player {} 0x{:08X}: {} item(s) added, {} taken "
              "back, across {} record(s)", player, entity, given, taken, g_coop_give[slot].size());
  return true;
}

void WearHashes(PPCContext& ctx, uint8_t* base, uint32_t inventory, uint32_t modifiers,
                const std::vector<uint32_t>& hashes, uint32_t* worn_out, uint32_t* given_out);

/// Dresses one player's current body, once per (entity, outfit): each hash is given only when
/// the inventory does not already hold it (HeldCount, keyed by the def hash, which is the
/// record an AddItemByDefHash lands under), and always worn. Returns true when dressed (or no
/// stated hero to dress as); false when the body is not dressable yet — the caller asks again.
///
/// THE STATED WORN LIST IS THE WHOLE OUTFIT, and wearing replaces only the clusters the new
/// item covers, so what the statement does not name is taken off first: each worn record of
/// component 15 (+0x10..+0x14, 24-byte {item +0, index +4}, def hash at item+4) through the
/// guest's RemoveItem with that {item, index}.
///   - A BODY'S FIRST DRESS: every worn record the statement does not name. A body is created
///     wearing its definition's `InitialModifiers` only on the copy that owns it
///     (`CECAppearanceModifierManager_vf8` 0x824BDFB8: `bl sub_82214AF8` 0x824BE0FC, then
///     `bl CECAppearanceModifiers_ApplyInitialModifiers` 0x824BE110; disc 0x8256ED14 /
///     0x8256ED28), so the host's copy of a preview and the joiner's copy of its own body start
///     in `CreatureHero`'s starter clothes and the other machine's copy of each starts bare.
///     The same rule on every copy is what makes the two screens agree.
///   - A NEW OUTFIT ON THE SAME BODY: what the last statement named and this one does not; a
///     record neither names is one the guest put on since and stays.
/// A statement that names nothing worn takes nothing off on a first dress: the body keeps the
/// starter clothes its owner copy was made in (a new-game hero's save names no worn list and the
/// solo game shows it in `InitialModifiers`). A body with no statement yet (generation 0) is left
/// as the guest made it.
bool CoopRedressPlayer(PPCContext& ctx, uint8_t* base, int player, uint32_t entity) {
  const int slot = player - 1;
  if (entity == 0u) {
    return true;
  }

  const std::string set = CoopWearText(slot);
  if (entity == g_coop_dressed_entity[slot] && set == g_coop_dressed_set[slot]) {
    return true;
  }

  // Slot 2 is the co-op screen's preview, which mirrors player two's statement.
  if (g_coop_record[player == 3 ? 1 : slot].generation == 0u) {
    g_coop_dressed_entity[slot] = 0u;
    g_coop_dressed_set[slot].clear();
    g_coop_dyed_entity[slot] = 0u;
    g_coop_dyed_set[slot].clear();
    return true;
  }

  const uint32_t inventory = EntityComponent(base, entity, 13u);
  const uint32_t modifiers = EntityComponent(base, entity, 15u);
  if (inventory == 0u || modifiers == 0u) {
    return false;  // not a dressable body yet — the next tick asks again
  }

  const bool first = entity != g_coop_dressed_entity[slot];
  const std::vector<uint32_t> last =
      first ? std::vector<uint32_t>() : CoopWearTextHashes(g_coop_dressed_set[slot]);
  const std::vector<uint32_t>& wear = g_coop_wear[slot];
  const auto comes_off = [&](uint32_t hash) {
    if (std::find(wear.begin(), wear.end(), hash) != wear.end()) {
      return false;
    }
    return (first && !wear.empty()) || std::find(last.begin(), last.end(), hash) != last.end();
  };

  const uint32_t begin = LoadGuestU32(base, modifiers + 0x10u);
  const uint32_t end = LoadGuestU32(base, modifiers + 0x14u);
  if (begin != end && (!LooksLikeGuestPointer(begin) || end < begin ||
                       ((end - begin) % 24u) != 0u || (end - begin) > 24u * 64u)) {
    return false;  // a torn worn vector — the next tick reads it again
  }

  // Snapshot first: each removal erases its record and moves the rest down.
  std::vector<std::pair<uint32_t, uint32_t>> off;
  for (uint32_t at = begin; at < end; at += 24u) {
    const uint32_t item = LoadGuestU32(base, at);
    if (LooksLikeGuestPointer(item) && comes_off(LoadGuestU32(base, item + 4u))) {
      off.emplace_back(item, LoadGuestU32(base, at + 4u));
    }
  }

  uint32_t taken_off = 0u;
  const uint32_t frame = (ctx.r1.u32 - 0x100u) & ~0xFu;
  const uint32_t holder = frame + 0x60u;
  for (const auto& [item, index] : off) {
    StoreGuestU32(base, holder, item);
    StoreGuestU32(base, holder + 4u, index);
    PPCContext work = ctx;
    work.r1.u32 = frame;
    work.r3.u32 = modifiers;
    work.r4.u32 = holder;
    __imp__rex_CECAppearanceModifiers_RemoveItem(work, base);
    ++taken_off;
  }

  g_coop_dressed_entity[slot] = entity;
  g_coop_dressed_set[slot] = set;
  g_coop_dyed_entity[slot] = 0u;
  g_coop_dyed_set[slot].clear();

  uint32_t worn = 0u;
  uint32_t given = 0u;
  WearHashes(ctx, base, inventory, modifiers, g_coop_wear[slot], &worn, &given);

  REXLOG_INFO("Refii guest-tick pump: CoopRedress — player {} 0x{:08X} dressed ({}): {} of {} "
              "outfit item(s) given and worn ({} newly given), {} taken off", player, entity,
              first ? "first dress" : "new outfit", worn, g_coop_wear[slot].size(), given,
              taken_off);
  return true;
}

/// Gives each def hash the inventory does not already hold (HeldCount, keyed by the def hash,
/// which is the record an AddItemByDefHash lands under) and wears it.
void WearHashes(PPCContext& ctx, uint8_t* base, uint32_t inventory, uint32_t modifiers,
                const std::vector<uint32_t>& hashes, uint32_t* worn_out, uint32_t* given_out) {
  // Scratch above the callee's argument-save area: frame+0x60 and up.
  const uint32_t frame = (ctx.r1.u32 - 0x100u) & ~0xFu;
  const uint32_t ref = frame + 0x60u;

  uint32_t worn = 0u;
  uint32_t given = 0u;
  for (uint32_t hash : hashes) {
    if (HeldCount(base, inventory, hash) == 0u) {
      for (uint32_t i = 0; i < 4u; ++i) StoreGuestU32(base, ref + 4u * i, 0u);

      PPCContext give = ctx;
      give.r1.u32 = frame;
      give.r3.u32 = ref;
      give.r4.u32 = inventory;
      give.r5.u32 = hash;
      rex_CECInventory_AddItemByDefHash(give, base);

      const uint32_t item = LoadGuestU32(base, ref);
      if (item == 0u) {
        continue;
      }
      ++given;

      PPCContext release = ctx;
      release.r1.u32 = frame;
      release.r3.u32 = item;
      release.r4.u32 = ref;
      rex_lhTrackedObject_RemoveHandleRef(release, base);
    }

    PPCContext wear = ctx;
    wear.r1.u32 = frame;
    wear.r3.u32 = modifiers;
    wear.r4.u32 = hash;
    rex_CECAppearanceModifiers_WearItemByDefHash(wear, base);
    ++worn;
  }
  *worn_out = worn;
  *given_out = given;
}

/// Gives one player's worn items the dyes their owner's HeroState states, through the guest's
/// own setter, once per (body, statement); run after CoopRedressPlayer, which clears the latch
/// when it wears again.
///
/// Each worn record of component 15 (+0x10..+0x14, 24-byte records {item +0, index +4}) is
/// matched to the kit's entry for its record hash (item+4), in order when a record repeats, and
/// handed to `SetClothingItemDyeColour` as the dye screen's `sub_826D3108` hands a target to the inventory's
/// apply: a ref {item, index, 0, 0} registered on the item (`sub_82764D00(item, ref)`), so the
/// split `sub_823A2040` makes of a stack rebinds it, released after
/// (`sub_82764DC0` = rex_lhTrackedObject_RemoveHandleRef). A slot the kit states undyed is
/// written back to the undyed state — colour {0, 0, 0}, alpha 0xFF, brightness 0, which is
/// `dword_8331B158` and 0, what `sub_826CAE08` restores — only when `sub_826CAFA8` says it is
/// dyed now, so an undyed item never gains a type-2 record. The refresh the setter ends with
/// (`sub_824BFD78`) is handed this body's comp15; the inventory's own apply `sub_826CAB78` and
/// scrub `sub_826CAE08` refresh the GUI player's (`sub_8229EE78(sub_821C7CE0(*0x83496B08))`,
/// component 15) instead, which is why they are not used for player two.
/// True when restated (or nothing to restate); false when the body has no comp15 yet.
bool CoopDyePlayer(PPCContext& ctx, uint8_t* base, int player, uint32_t entity) {
  const int slot = player - 1;
  if (entity == 0u || g_coop_wear[slot].empty()) {
    return true;
  }

  std::string set;
  for (const CoopItemDye& dye : g_coop_dyes[slot]) {
    char entry[96];
    uint32_t bright[2];
    std::memcpy(bright, dye.brightness, sizeof(bright));
    std::snprintf(entry, sizeof(entry), "%08X:%u:%02X%02X%02X:%08X:%02X%02X%02X:%08X,", dye.record,
                  dye.slots, dye.red[0], dye.green[0], dye.blue[0], bright[0], dye.red[1],
                  dye.green[1], dye.blue[1], bright[1]);
    set += entry;
  }

  if (entity == g_coop_dyed_entity[slot] && set == g_coop_dyed_set[slot]) {
    return true;
  }

  const uint32_t appearance = EntityComponent(base, entity, 15u);
  if (appearance == 0u) {
    return false;
  }

  PPCFunc* retain = FindGuestFunction(refii::guest::Address(0x82764D00u));
  PPCFunc* is_dyed = FindGuestFunction(refii::guest::Address(0x826CAFA8u));
  if (retain == nullptr || is_dyed == nullptr) {
    return true;
  }

  const uint32_t begin = LoadGuestU32(base, appearance + 0x10u);
  const uint32_t end = LoadGuestU32(base, appearance + 0x14u);
  if (begin != end && (!LooksLikeGuestPointer(begin) || end < begin ||
                       ((end - begin) % 24u) != 0u || (end - begin) > 24u * 64u)) {
    return false;  // a torn worn vector — the next tick reads it again
  }

  // Snapshot the worn records first: the setter's split and refresh may move the vector.
  std::vector<std::pair<uint32_t, uint32_t>> worn;
  for (uint32_t at = begin; at < end; at += 24u) {
    const uint32_t item = LoadGuestU32(base, at);
    if (LooksLikeGuestPointer(item)) {
      worn.emplace_back(item, LoadGuestU32(base, at + 4u));
    }
  }

  g_coop_dyed_entity[slot] = entity;
  g_coop_dyed_set[slot] = set;

  const PPCContext saved = ctx;
  const uint32_t frame = (ctx.r1.u32 - 0x100u) & ~0xFu;
  const uint32_t ref = frame + 0x60u;
  std::vector<bool> taken(g_coop_dyes[slot].size(), false);
  uint32_t dyed = 0u;
  uint32_t scrubbed = 0u;
  for (const auto& [item, index] : worn) {
    const uint32_t record = LoadGuestU32(base, item + 4u);
    const CoopItemDye* stated = nullptr;
    for (std::size_t i = 0; i < g_coop_dyes[slot].size(); ++i) {
      if (!taken[i] && g_coop_dyes[slot][i].record == record) {
        taken[i] = true;
        stated = &g_coop_dyes[slot][i];
        break;
      }
    }

    StoreGuestU32(base, ref, item);
    StoreGuestU32(base, ref + 4u, index);
    StoreGuestU32(base, ref + 8u, 0u);
    StoreGuestU32(base, ref + 12u, 0u);
    PPCContext work = saved;
    work.r1.u32 = frame;
    work.r3.u32 = item;
    work.r4.u32 = ref;
    if (!SafeInvokeGuestCall(retain, work, base)) {
      continue;
    }

    for (uint32_t dye_slot = 0u; dye_slot < 2u; ++dye_slot) {
      const bool wanted = stated != nullptr && (stated->slots & (1u << dye_slot)) != 0u;
      if (!wanted) {
        work = saved;
        work.r1.u32 = frame;
        work.r3.u32 = 0u;
        work.r4.u32 = ref;
        work.r5.u32 = dye_slot;
        if (!SafeInvokeGuestCall(is_dyed, work, base) || (work.r3.u32 & 0xFFu) == 0u) {
          continue;
        }
      }

      work = saved;
      work.r1.u32 = frame;
      work.r3.u32 = appearance;
      work.r4.u32 = ref;
      work.r5.u32 = dye_slot == 0u ? 1u : 0u;
      work.r6.u32 = wanted ? stated->red[dye_slot] : 0u;
      work.r7.u32 = wanted ? stated->green[dye_slot] : 0u;
      work.r8.u32 = wanted ? stated->blue[dye_slot] : 0u;
      work.f1.f64 = wanted ? static_cast<double>(stated->brightness[dye_slot]) : 0.0;
      __imp__rex_CECAppearanceModifiers_SetDyeColour(work, base);
      ++(wanted ? dyed : scrubbed);
    }

    if (const uint32_t held = LoadGuestU32(base, ref); held != 0u) {
      PPCContext release = saved;
      release.r1.u32 = frame;
      release.r3.u32 = held;
      release.r4.u32 = ref;
      rex_lhTrackedObject_RemoveHandleRef(release, base);
    }
  }
  ctx = saved;

  if (dyed != 0u || scrubbed != 0u) {
    REXLOG_INFO("Refii guest-tick pump: CoopDye — player {} 0x{:08X}: {} slot(s) dyed, {} "
                "restored undyed, across {} worn item(s)", player, entity, dyed, scrubbed,
                worn.size());
  }
  return true;
}

/// Gives one player's body the scars their owner's HeroState states, once per (body,
/// statement), through the guest's own entry for a stated scar: `sub_825F2F88(morph, msg)`, the
/// receiver of the morph's replication message (`sub_82392250` hands it the payload for
/// component 116; the sender is `sub_825F2518`, which posts one `off_820A9B88` message per scar
/// with +4 BodyArea, +8 TextureMorphRecordID, +12 Permanent). It appends the scar to +20 unless
/// one with the same area bit, record and permanence is there, so a restatement adds nothing
/// twice. When any was added the textures are rebuilt with
/// `Lua_GraphicAppearanceMorph_UpdateTextureMorphs(comp17)` (`sub_825B60F8`), the call the
/// morph's own replicated-state receiver `sub_825F2A30` makes after its change. The guest names
/// no entry that removes a GIVEN scar (`RemoveRandomScar` picks at random), so a scar the owner
/// lost is not taken off. True always: a body with no component 116 carries no scars.
bool CoopScarPlayer(PPCContext& ctx, uint8_t* base, int player, uint32_t entity) {
  const int slot = player - 1;
  if (entity == 0u || g_coop_scars[slot].empty()) {
    return true;
  }

  const std::string set = CoopScarText(slot);
  if (entity == g_coop_scarred_entity[slot] && set == g_coop_scarred_set[slot]) {
    return true;
  }

  const uint32_t morph = EntityComponent(base, entity, 116u);
  PPCFunc* add = FindGuestFunction(refii::guest::Address(0x825F2F88u));
  PPCFunc* update = FindGuestFunction(refii::guest::Address(0x825B60F8u));
  if (morph == 0u || add == nullptr) {
    return true;
  }

  g_coop_scarred_entity[slot] = entity;
  g_coop_scarred_set[slot] = set;

  const auto count = [&]() {
    const uint32_t first = LoadGuestU32(base, morph + 0x18u);
    const uint32_t last = LoadGuestU32(base, morph + 0x1Cu);
    return last >= first ? (last - first) / 12u : 0u;
  };

  const PPCContext saved = ctx;
  const uint32_t frame = (ctx.r1.u32 - 0x100u) & ~0xFu;
  const uint32_t message = frame + 0x60u;
  const uint32_t before = count();
  for (const CoopScar& scar : g_coop_scars[slot]) {
    for (uint32_t i = 0u; i < 16u; i += 4u) StoreGuestU32(base, message + i, 0u);
    StoreGuestU32(base, message + 4u, scar.area);
    StoreGuestU32(base, message + 8u, scar.record);
    StoreGuestU8(base, message + 12u, scar.permanent != 0u ? 1u : 0u);
    PPCContext work = saved;
    work.r1.u32 = frame;
    work.r3.u32 = morph;
    work.r4.u32 = message;
    SafeInvokeGuestCall(add, work, base);
  }
  const uint32_t added = count() - std::min(count(), before);

  const uint32_t graphic = EntityComponent(base, entity, 17u);
  if (added != 0u && graphic != 0u && update != nullptr) {
    PPCContext work = saved;
    work.r1.u32 = frame;
    work.r3.u32 = graphic;
    SafeInvokeGuestCall(update, work, base);
  }
  ctx = saved;

  REXLOG_INFO("Refii guest-tick pump: CoopScar — player {} 0x{:08X}: {} of {} stated scar(s) "
              "added (morph 0x{:08X} holds {})", player, entity, added,
              g_coop_scars[slot].size(), morph, count());
  return true;
}

/// Both players, whenever a body exists that has not been dressed with the current outfit.
///
/// Neither of the guest's ways of giving a player a body dresses a hero body across the wire:
/// a RETYPE re-wears an empty stash (0x82365938), CREATION on a client runs its
/// `InitialItems` pass only after the gate byte is set (0x82315224), and the host's hero
/// APPEARANCE is never replicated to a client at all — only the weapon slots are
/// (`sub_824AD698`), which is why the joiner's player one stands armed and naked.
///
/// PLAYER ONE ONLY ON THE JOINER. On the host player one is the real hero, already dressed from
/// the save; giving those items again would raise their counts. Player two dresses on both
/// sides, only while standing in their own hero's body — a henchman brings its own clothes.
///
/// Called from the hooks after a body build and from the tick, keyed on the entity address, so
/// a body created by the join sync — a path no hook covers — is dressed on the next tick.
/// The co-op screen's preview follows the own-hero bit: when the bit flips while the screen
/// is up the guest is asked to respawn the preview (`sub_82438818(handler)`, which now answers
/// the hero mannequin through the overridden name table), and a hero preview is dressed in
/// player two's outfit like the body itself. Not gated on a peer standing — it is a local
/// preview entity on a screen the local player is looking at.
///
/// A NEW HERO ON THE SAME FIGURE IS A NEW FIGURE. The guest respawns the preview only when the
/// pickbox's mannequin preset changes (`sub_82436F38`: `lwz r10,0x24(r3)`; `cmpw r10,r4`; `beq`
/// past `bl lhCoopScreen_SpawnPreviewMannequin`, 0x82436F78..0x82436F98; disc `sub_82478158`
/// 0x82478198..0x824781B8), and every hero is the same hero mannequin, so a second pick keeps
/// the figure the first was dressed on — and wearing only replaces the clusters it covers, and
/// a scar cannot be taken off. So when the figure stands dressed, given or scarred from another
/// statement than player two's current one, the guest's own respawn is asked for
/// (`sub_82438818`, disc `sub_82479A38`: it destroys the figure at +276 through its id at +284,
/// 0x824388a4, and spawns the preset's afresh), and the new figure is dressed on the next tick.
/// The own-hero bit the preview was last respawned for. Cleared with the session.
bool g_coop_bit_seen = false;

void CoopDressMannequin(PPCContext& ctx, uint8_t* base) {
  constexpr uint32_t kCoopScreenId = 24u;
  constexpr int kPreview = 2;
  const bool bit = refii_bridge_two_is_hero() != 0;
  if (refii_bridge_stated_screen_now() != kCoopScreenId) {
    return;
  }

  // The henchman figure as the preview scene filed it (preview_figures_stated.cpp); the handler
  // only as the object the guest's respawn is called on.
  const uint32_t entity = StatedPreviewFigure("henchman");
  const bool other_hero =
      bit && entity != 0u &&
      ((entity == g_coop_dressed_entity[kPreview] && CoopWearText(kPreview) != g_coop_dressed_set[kPreview]) ||
       (entity == g_coop_given_entity[kPreview] && CoopGiveText(kPreview) != g_coop_given_set[kPreview]) ||
       (entity == g_coop_scarred_entity[kPreview] && CoopScarText(kPreview) != g_coop_scarred_set[kPreview]));
  if (bit != g_coop_bit_seen || other_hero) {
    const uint32_t handler = InGameScreenHandler(ctx, base, kCoopScreenId);
    if (handler == 0u) {
      return;
    }
    g_coop_bit_seen = bit;
    PPCContext work = ctx;
    work.r1.u32 = (ctx.r1.u32 - 0x100u) & ~0xFu;
    work.r3.u32 = handler;
    rex_lhCoopScreen_SpawnPreviewMannequin(work, base);

    // The new figure may land on the old one's address: nothing latched for it carries over.
    g_coop_build_applied.erase(entity);
    g_coop_dressed_entity[kPreview] = 0u;
    g_coop_dressed_set[kPreview].clear();
    g_coop_given_entity[kPreview] = 0u;
    g_coop_given_set[kPreview].clear();
    g_coop_dyed_entity[kPreview] = 0u;
    g_coop_dyed_set[kPreview].clear();
    g_coop_scarred_entity[kPreview] = 0u;
    g_coop_scarred_set[kPreview].clear();
    REXLOG_INFO("Refii guest-tick pump: co-op preview 0x{:08X} respawned for {}", entity,
                other_hero ? "another hero's kit" : bit ? "own-hero bit 1" : "own-hero bit 0");
    return;  // the new entity is dressed on the next tick
  }

  if (bit && entity != 0u) {
    const bool dressed_before = g_coop_dressed_entity[kPreview] == entity;
    CoopGivePlayer(ctx, base, 3, entity);
    CoopRedressPlayer(ctx, base, 3, entity);
    CoopDyePlayer(ctx, base, 3, entity);
    CoopScarPlayer(ctx, base, 3, entity);
    ApplyBuild(ctx, base, entity, g_coop_record[1].build);

    // THE FIGURE'S PARTS FOLLOW ITS OUTFIT. Wearing only marks the appearance changed; the part
    // records (component 17's built list, what the figure is drawn from) are rebuilt by the
    // draw virtual `sub_8220DE88` (disc `sub_8220DFC0`), which runs `RebuildParts(comp17)`
    // first when its render instances are empty or the change bit 0x40 is set (0x8220DEE4..
    // 0x8220DEFC; disc 0x8220E01C..0x8220E034). A preview figure is drawn only by the Gameface
    // render path, which a headless guest does not run, so a figure dressed after its first
    // build kept the parts it was spawned with. The rebuild is made here, once per dressing.
    const uint32_t morph = EntityComponent(base, entity, 17u);
    if (!dressed_before && g_coop_dressed_entity[kPreview] == entity && morph != 0u) {
      PPCContext work = ctx;
      work.r1.u32 = (ctx.r1.u32 - 0x100u) & ~0xFu;
      work.r3.u32 = morph;
      __imp__rex_lhGraphicAppearanceMorph_RebuildParts(work, base);
      REXLOG_INFO("Refii guest-tick pump: co-op preview 0x{:08X} parts rebuilt for its outfit "
                  "(comp17 0x{:08X})", entity, morph);
    }
  } else {
    g_coop_dressed_entity[2] = 0u;
    g_coop_dressed_set[2].clear();
    g_coop_dyed_entity[2] = 0u;
    g_coop_dyed_set[2].clear();
    g_coop_scarred_entity[2] = 0u;
    g_coop_scarred_set[2].clear();
  }
}

// Defined with their owners further down.
extern std::map<uint32_t, uint32_t> g_coop_applied_generation;
extern std::map<uint32_t, std::pair<uint32_t, uint32_t>> g_coop_equip_latched;
extern std::map<uint32_t, std::array<uint32_t, 2>> g_coop_host_weapon;
extern std::map<uint32_t, uint32_t> g_coop_host_weapon_loose;
extern uint32_t g_coop_stats_entity;
extern uint32_t g_coop_face_entity;
extern uint32_t g_coop_face_record;

extern uint64_t g_coop_held[2][2];
extern std::string g_coop_stats_set;
extern std::atomic<uint32_t> g_herostate_applied[2];
extern uint32_t g_herostate_body[2];
}  // namespace refii::vfs
/// The copy the host bridge reads — src/core/guest_tick_pump_state.cpp, linked into refii_vfs;
/// C linkage because the payload may import only C symbols from a host library.
extern "C" void refii_bridge_publish_herostate_applied(uint32_t player, uint32_t version);
namespace refii::vfs {

/// Every per-body latch, dropped together when the peer goes: a re-joined player can land on a
/// RECYCLED entity address, and a latch that remembered the old body would skip the new one.
extern std::map<uint32_t, std::pair<uint64_t, uint64_t>> g_coop_held_attached;

/// CoopAttachHeld's pacing, per body: the pair it last reported waiting for, the tick it may
/// try again, and (pair, silent attach attempts so far).
std::map<uint32_t, std::pair<uint64_t, uint64_t>> g_coop_held_waited;
std::map<uint32_t, uint32_t> g_coop_held_next_try;
std::map<uint32_t, std::pair<std::pair<uint64_t, uint64_t>, uint32_t>> g_coop_held_attempts;

/// Ticks of the pump's co-op pass, the clock CoopAttachHeld paces its retries by.
uint32_t g_coop_ticks = 0u;

void CoopResetLatches() {
  g_coop_held_attached.clear();
  g_coop_held_waited.clear();
  g_coop_held_next_try.clear();
  g_coop_held_attempts.clear();
  g_coop_applied_generation.clear();
  g_coop_equip_latched.clear();
  g_coop_host_weapon.clear();
  g_coop_host_weapon_loose.clear();
  g_coop_skills_applied.clear();
  g_coop_build_applied.clear();
  for (int slot = 0; slot < 3; ++slot) {
    g_coop_dressed_entity[slot] = 0u;
    g_coop_dressed_set[slot].clear();
    g_coop_given_entity[slot] = 0u;
    g_coop_given_set[slot].clear();
    g_coop_dyed_entity[slot] = 0u;
    g_coop_dyed_set[slot].clear();
    g_coop_scarred_entity[slot] = 0u;
    g_coop_scarred_set[slot].clear();
  }
  g_coop_stats_entity = 0u;
  g_coop_face_entity = 0u;
  g_coop_face_record = 0u;
  for (int slot = 0; slot < 2; ++slot) {
    g_herostate_body[slot] = 0u;
    g_herostate_applied[slot].store(0u, std::memory_order_relaxed);
    refii_bridge_publish_herostate_applied(static_cast<uint32_t>(slot) + 1u, 0u);
  }
}

/// Every latch keyed on ONE body, dropped when that body is rebuilt in place (a retype keeps
/// the entity address and replaces its components) or player two changes bodies.
void CoopForgetBody(uint32_t entity) {
  if (entity == 0u) {
    return;
  }

  g_coop_held_attached.erase(entity);
  g_coop_held_waited.erase(entity);
  g_coop_held_next_try.erase(entity);
  g_coop_held_attempts.erase(entity);
  g_coop_applied_generation.erase(entity);
  g_coop_equip_latched.erase(entity);
  g_coop_host_weapon.erase(entity);
  g_coop_host_weapon_loose.erase(entity);
  g_coop_skills_applied.erase(entity);
  g_coop_build_applied.erase(entity);
  for (int slot = 0; slot < 3; ++slot) {
    if (g_coop_dressed_entity[slot] == entity) {
      g_coop_dressed_entity[slot] = 0u;
      g_coop_dressed_set[slot].clear();
    }
    if (g_coop_given_entity[slot] == entity) {
      g_coop_given_entity[slot] = 0u;
      g_coop_given_set[slot].clear();
    }
    if (g_coop_dyed_entity[slot] == entity) {
      g_coop_dyed_entity[slot] = 0u;
      g_coop_dyed_set[slot].clear();
    }
    if (g_coop_scarred_entity[slot] == entity) {
      g_coop_scarred_entity[slot] = 0u;
      g_coop_scarred_set[slot].clear();
    }
  }
  if (g_coop_stats_entity == entity) {
    g_coop_stats_entity = 0u;
  }
  if (g_coop_face_entity == entity) {
    g_coop_face_entity = 0u;
    g_coop_face_record = 0u;
  }
  for (int slot = 0; slot < 2; ++slot) {
    if (g_herostate_body[slot] == entity) {
      g_herostate_body[slot] = 0u;
      g_herostate_applied[slot].store(0u, std::memory_order_relaxed);
      refii_bridge_publish_herostate_applied(static_cast<uint32_t>(slot) + 1u, 0u);
    }
  }
}

/// See kActionCoopHeroReset: the whole session's hero data and every latch.
void CoopResetSession() {
  for (CoopHeroRecord& record : g_coop_record) {
    record = CoopHeroRecord{};
  }
  for (int slot = 0; slot < 3; ++slot) {
    g_coop_give[slot].clear();
    g_coop_wear[slot].clear();
    g_coop_dyes[slot].clear();
    g_coop_scars[slot].clear();
  }
  for (auto& pair : g_coop_held) {
    pair[0] = 0u;
    pair[1] = 0u;
  }
  refii_bridge_two_is_hero_set(0);
  g_coop_stats_set.clear();
  g_coop_bit_seen = false;
  CoopResetLatches();
}

/// ANIMATE THE CO-OP PREVIEW. The guest poses a preview entity only from the GUI's Gameface
/// render path (`sub_82379170` → gameface vt+48 → `sub_822CD208` → `sub_826D6C90`), which
/// this headless host never runs — and which CANNOT be called from the tick: it builds the
/// model instance against the render world and the present thread then called through a
/// freed pointer (both machines, 2 s after the first call). The simulation's own skeleton pass
/// (`sub_8220D1B8`, 0x8220D1B8, decompiled) has the lever instead: it reads a FORCE byte at
/// `comp3+132` (0x8220d4cc), poses the entity when it is set even though the visibility and
/// distance gates say no (0x8220d78c), then sets the pose-enable bit `comp3+144 |= 4` and
/// clears the byte (0x8220dc08–0x8220dc38) — so it is re-set here every tick while the co-op
/// screen holds a preview. Whatever idle the creature's own controller carries then advances
/// with the sim's step, and the feed's rig follows it.
/// Every preview figure the guest's preview scene has filed and not destroyed
/// (preview_figures_stated.cpp), posed from the tick: the guest poses them only from the Gameface
/// render path, so the motion force byte (comp3+132, `sub_8220D1B8`) is re-set every tick.
void PosePreviewFigures(uint8_t* base) {
  // A ring of the entities already reported, overwritten oldest-first: a ninth entity evicts
  // one rather than logging every tick.
  static uint32_t said[8] = {};
  static uint32_t said_next = 0u;

  uint32_t entities[8];
  const char* roles[8];
  const uint32_t count = StatedPreviewFigures(entities, roles, 8u);
  for (uint32_t i = 0u; i < count; ++i) {
    const uint32_t entity = entities[i];
    if (!LooksLikeGuestPointer(entity)) continue;
    const uint32_t motion = EntityComponent(base, entity, 3u);
    if (motion == 0u) continue;
    base[motion + 132u] = 1u;
    bool seen = false;
    for (uint32_t reported : said) {
      seen = seen || reported == entity;
    }
    if (seen) continue;
    said[said_next++ % 8u] = entity;
    REXLOG_INFO("Refii guest-tick pump: '{}' preview 0x{:08X} is posed by the simulation "
                "(force byte comp3+132 re-set each tick)", roles[i], entity);
  }
}

void CoopRedress(PPCContext& ctx, uint8_t* base) {
  CoopRepairNetworkSlot(base);
  CoopDressMannequin(ctx, base);
  PosePreviewFigures(base);
  // A peer standing online, or LOCAL CO-OP (game+0x11C == 2, the guest's IsLocalCoopGame): local
  // player two is their own hero too, from the save they picked (GameUi posts their record).
  static bool was_live = false;
  const uint32_t game_root = LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = LooksLikeGuestPointer(game_root) ? LoadGuestU32(base, game_root + 12u) : 0u;
  const bool local = LooksLikeGuestPointer(game) && LoadGuestU32(base, game + 0x11Cu) == 2u;
  const bool live = CoopSessionLive(base) || local;
  if (was_live && !live) {
    // The henchman pair the host made for a joiner that left before sheathing it lies on the
    // floor held by no one (coop_player_hooks.cpp, `sub_82315AD0`); it goes with the partner.
    RefiiCoopDropLooseHenchmanPair(&ctx, base);
    CoopResetSession();
    REXLOG_INFO("Refii guest-tick pump: co-op — the partner is gone; every per-body latch dropped");
  }
  was_live = live;
  if (!live) {
    return;  // no partner: player two is being torn down or never existed
  }

  // Player one's record applies only on the joiner (its copy of the host's hero); player
  // two's only while player two stands in their own hero's body.
  if (IsCoopJoiner(base)) {
    CoopApplyHero(ctx, base, 1, CoopPlayerOneEntity(base));
    CoopAttachHeld(ctx, base, 1, CoopPlayerOneEntity(base));
  }

  if (refii_bridge_two_is_hero() != 0) {
    const uint32_t two = CoopPlayerTwoEntity(base);
    CoopApplyHero(ctx, base, 2, two);

    // A field restated after the body's apply has landed: each has its own latch, so this
    // settles to a no-op once the stored text stops changing.
    const auto applied = g_coop_applied_generation.find(two);
    if (two != 0u && applied != g_coop_applied_generation.end() && applied->second != 0u) {
      ApplyStats(ctx, base, two, g_coop_record[1].stats);
      ApplySkills(ctx, base, two, g_coop_record[1].skills);
      ApplyBuild(ctx, base, two, g_coop_record[1].build);
      ApplyFace(ctx, base, two, g_coop_record[1].face);
      CoopDyePlayer(ctx, base, 2, two);
      CoopScarPlayer(ctx, base, 2, two);
    }

    if (IsCoopJoiner(base)) {
      CoopAttachHeld(ctx, base, 2, two);
    } else {
      CoopKeepHostWeapons(ctx, base, two);
    }
  } else {
    g_coop_dressed_entity[1] = 0u;
    g_coop_dressed_set[1].clear();
    g_coop_dyed_entity[1] = 0u;
    g_coop_dyed_set[1].clear();
    g_coop_scarred_entity[1] = 0u;
    g_coop_scarred_set[1].clear();
  }
}

/// The HeroSave block — CoopHeroRecord::stats, the HeroState's money/gender/morality/purity/
/// renown/ranks/experience as 28 comma-separated fields.
///
/// THE GUEST'S OWN APPLY, HANDED OUR BLOCK. `sub_8243AEF8` loads a chosen save's HeroSave into
/// `SaveMgr+1016` and then walks player two's components (`entity+72..76`, stride 8, component
/// at `+4`) calling each one's vtable slot 40 with that block (0x8243B02C–0x8243B060). The block
/// is what `sub_82440F78` deserialises: +0 Money, +4 Gender, +8 Morality, +12 Purity, +16 Renown,
/// +20..+76 HeroAbility0..14, an `Experience` float vector object at +80 (begin +84, end +88),
/// `TotalExperience` at +96 (begin +100, end +104), +112 SecondsPlayedAsHenchman, +116
/// CanUnlockAchievements. Built here on a scratch frame, the two arrays beside it, and walked
/// the same way — so money, morality, purity, renown, ranks and experience land on player two
/// on BOTH machines from the kit, with no save on the host and no picker on the joiner.
///
/// Once per (entity, block): the walk is what a save load does, and a save is loaded once.
uint32_t g_coop_stats_entity = 0u;
std::string g_coop_stats_set;

/// True when applied, already applied to this body, or nothing/garbage to apply; false when
/// the body's component vector is not readable yet.
bool ApplyStats(PPCContext& ctx, uint8_t* base, uint32_t entity, const std::string& text) {
  if (text.empty() || entity == 0u) {
    return true;
  }

  std::vector<std::string> fields;
  std::size_t at = 0;
  while (at <= text.size()) {
    const std::size_t comma = text.find(',', at);
    fields.push_back(text.substr(at, comma == std::string::npos ? comma : comma - at));
    at = comma == std::string::npos ? text.size() + 1u : comma + 1u;
  }

  // money, gender, morality, purity, renown, 15 ranks, 4 pools, 4 totals = 28 fields.
  if (fields.size() < 28u) {
    static std::string dropped;
    if (dropped != text) {
      dropped = text;
      REXLOG_INFO("Refii guest-tick pump: CoopHeroStats has {} field(s), wants 28 — dropped",
                  fields.size());
    }
    return true;
  }

  // ONCE PER BODY. The block is the chosen save's snapshot; from then on the host's world owns
  // player two's money and experience, and a restated block (its money field drifting with the
  // joiner's own reads) must not keep resetting them.
  if (entity == g_coop_stats_entity) {
    return true;
  }

  const uint32_t pairs = LoadGuestU32(base, entity + 0x48u);
  const uint32_t pairs_end = LoadGuestU32(base, entity + 0x4Cu);
  if (!LooksLikeGuestPointer(pairs) || pairs_end < pairs || (pairs_end - pairs) > 8u * 512u) {
    return false;
  }

  g_coop_stats_entity = entity;
  g_coop_stats_set = text;

  const auto as_int = [&](std::size_t i) -> uint32_t {
    return static_cast<uint32_t>(std::strtol(fields[i].c_str(), nullptr, 10));
  };
  const auto as_float_bits = [&](std::size_t i) -> uint32_t {
    const float value = std::strtof(fields[i].c_str(), nullptr);
    uint32_t bits = 0u;
    std::memcpy(&bits, &value, sizeof(bits));
    return bits;
  };

  // THE GUEST'S WHOLE HEROSAVE BLOCK, 0x3E0 bytes: 120 bytes of fields, then at +120 the
  // 872-byte hero struct `sub_82440BB8` / `sub_82449060` fill (vptr first). Two slot-40 methods
  // read the second part: CECStats copies block+0x78 into stats+0x500 (`sub_824DEA68` 0x824DEEAC
  // → `sub_82449118`: three 128-wchar arrays, qwords, a flag) and CECMoney reads its value from
  // block+0x180 (`sub_8242E380` → `sub_82EE8630`, `ld r3,0x108(r3)` on block+0x78). A 0x80-byte
  // block handed them ~870 bytes of the pump's live stack — unterminated strings in player two's
  // hero struct and a garbage purse. The struct part is player two's OWN (stats+0x500, so that
  // copy changes nothing) and the money sits where CECMoney reads it.
  // Scratch above the callee's argument-save area: frame+0x60 and up.
  const uint32_t frame = (ctx.r1.u32 - 0x600u) & ~0xFu;
  const uint32_t block = frame + 0x60u;      // 0x3E0 bytes
  const uint32_t pools = frame + 0x440u;     // 4 floats
  const uint32_t totals = frame + 0x450u;    // 4 floats
  for (uint32_t i = 0; i < 0x3E0u; i += 4u) StoreGuestU32(base, block + i, 0u);
  const uint32_t stats = EntityComponent(base, entity, 57u);
  if (stats != 0u) {
    for (uint32_t i = 0; i < 872u; i += 4u) {
      StoreGuestU32(base, block + 120u + i, LoadGuestU32(base, stats + 0x500u + i));
    }
  }

  StoreGuestU32(base, block + 0u, as_int(0));            // Money
  StoreGuestU32(base, block + 0x180u, 0u);               // the hero struct's money, high word
  StoreGuestU32(base, block + 0x184u, as_int(0));        // and low word — what CECMoney reads
  StoreGuestU32(base, block + 4u, as_int(1));            // Gender, 2 = male
  StoreGuestU32(base, block + 8u, as_float_bits(2));     // Morality
  StoreGuestU32(base, block + 12u, as_float_bits(3));    // Purity
  StoreGuestU32(base, block + 16u, as_int(4));           // Renown
  for (uint32_t i = 0; i < 15u; ++i) {
    StoreGuestU32(base, block + 20u + 4u * i, as_int(5u + i));
  }
  for (uint32_t i = 0; i < 4u; ++i) {
    StoreGuestU32(base, pools + 4u * i, as_float_bits(20u + i));
    StoreGuestU32(base, totals + 4u * i, as_float_bits(24u + i));
  }
  StoreGuestU32(base, block + 84u, pools);              // Experience begin
  StoreGuestU32(base, block + 88u, pools + 16u);        // end
  StoreGuestU32(base, block + 92u, pools + 16u);        // capacity end
  StoreGuestU32(base, block + 100u, totals);            // TotalExperience begin
  StoreGuestU32(base, block + 104u, totals + 16u);
  StoreGuestU32(base, block + 108u, totals + 16u);
  StoreGuestU32(base, block + 112u, 0u);                // SecondsPlayedAsHenchman
  // A BYTE, not a word: `sub_82A1CE48` fills a bool, and on this big-endian guest a word of 1
  // would leave 0x00 at +116 and the 0x01 at +119.
  base[block + 116u] = 1u;                              // CanUnlockAchievements

  // THE JOINER KEEPS THE FOUR THE HOST REPLICATES. Money, morality, purity and renown are SET by
  // slot 40 (CECMoney 0x8242E3AC `stw r11,0xC(r31)`; CECStats 0x824DEBE0 +0x10, 0x824DEC6C +0x24,
  // 0x824DED08 +0x1C) and on the host the same call sends new − old to the peer (0x8242E530,
  // 0x824DEC2C/0x824DECBC/0x824DED2C), which ADDS it there (CECMoney_Modify 0x8242E6B8,
  // lhHeroStats_ModifyRenown 0x824D63F4). Both machines applying the save left the joiner at
  // 2K − H — doubled on every import. Here the joiner restates its current values, so its own
  // call changes nothing and the host's delta lands them on the save's.
  if (IsCoopJoiner(base)) {
    if (const uint32_t purse = EntityComponent(base, entity, 81u)) {
      const uint32_t current = LoadGuestU32(base, purse + 0x0Cu);
      StoreGuestU32(base, block + 0u, current);
      StoreGuestU32(base, block + 0x184u, current);
    }
    if (stats != 0u) {
      StoreGuestU32(base, block + 8u, LoadGuestU32(base, stats + 0x10u));    // Morality
      StoreGuestU32(base, block + 12u, LoadGuestU32(base, stats + 0x24u));   // Purity
      StoreGuestU32(base, block + 16u, LoadGuestU32(base, stats + 0x1Cu));   // Renown
    }
  }

  uint32_t called = 0u;
  for (uint32_t pair = pairs; pair + 8u <= pairs_end; pair += 8u) {
    const uint32_t component = LoadGuestU32(base, pair + 4u);
    if (!LooksLikeGuestPointer(component)) continue;
    const uint32_t vtable = LoadGuestU32(base, component);
    if (!LooksLikeGuestPointer(vtable)) continue;
    const uint32_t method = LoadGuestU32(base, vtable + 40u);
    PPCFunc* apply = FindGuestFunction(method);
    if (apply == nullptr) continue;

    PPCContext work = ctx;
    work.r1.u32 = frame;
    work.r3.u32 = component;
    work.r4.u32 = block;
    apply(work, base);
    ++called;
  }

  REXLOG_INFO("Refii guest-tick pump: CoopHeroStats — HeroSave block applied to player two "
              "0x{:08X} through {} component slot-40 method(s); money {} renown {}",
              entity, called, fields[0], fields[4]);
  return true;
}

/// Player two's character-creation record, on component 17. Once per (entity, record).
uint32_t g_coop_face_entity = 0u;
uint32_t g_coop_face_record = 0u;

/// True when set or nothing to set; false when the body has no morph component yet.
bool ApplyFace(PPCContext& ctx, uint8_t* base, uint32_t entity, uint32_t record) {
  if (record == 0u || entity == 0u ||
      (entity == g_coop_face_entity && record == g_coop_face_record)) {
    return true;
  }

  const uint32_t morph = EntityComponent(base, entity, 17u);
  if (morph == 0u) {
    return false;  // the body has no appearance-morph component yet; the next apply asks again
  }

  g_coop_face_entity = entity;
  g_coop_face_record = record;

  PPCContext work = ctx;
  work.r1.u32 = (ctx.r1.u32 - 0x100u) & ~0xFu;
  work.r3.u32 = morph;
  work.r4.u32 = record;
  rex_CECCompositeModel_ApplyCharacterCreationRecord(work, base);

  REXLOG_INFO("Refii guest-tick pump: CoopHeroFace — record 0x{:08X} set on player two "
              "0x{:08X} (component 17 0x{:08X})", record, entity, morph);
  return true;
}

/// The guest's own equip-by-record, STEP BY STEP, with the one thing it cannot do for us done
/// here. `sub_824ABA28(comp28, id, 0)` (0x824ABA28, decompiled) is: slot name from the record's
/// WeaponType (`sub_824AC920`), instantiate the held record (`sub_824F26A8` → `sub_824F2870` →
/// `sub_8238B860`), attach (`sub_824FB5D8(comp24, name, item)`), record (`sub_824AD108`), draw
/// effect (`sub_824AB170`). `sub_8238B860` (0x8238B860) skips ACTIVATION (`sub_8238BE08`) when the
/// manager says client and `+239` is clear — the sync's own flag — and the attach then declines
/// an unactivated item, which is what left swords on the ground. Here the item is activated
/// when the guest did not, each step reports, and nothing is retried: a pair is one attempt per
/// body, because every attempt instantiates an item.
/// The weapon class the guest gives an item (`sub_824AD108` at 0x824AD254): component 179's
/// `+12` through `sub_82561420` — 2..4 melee (1), 5..8 ranged (2), anything else 0.
uint32_t WeaponClassOf(uint8_t* base, uint32_t item) {
  const uint32_t kind = EntityComponent(base, item, 179u);
  const uint32_t type = kind != 0u ? LoadGuestU32(base, kind + 12u) : 0u;
  return type >= 2u && type <= 4u ? 1u : type >= 5u && type <= 8u ? 2u : 0u;
}

/// The alive item with `record` the body holds in any slot (component 24, 12-byte entries at
/// `+16..+20`, holder at entry+8, item = `*holder`), or 0.
uint32_t HeldItemWithRecord(uint8_t* base, uint32_t entity, uint32_t record) {
  const uint32_t held = EntityComponent(base, entity, 24u);
  if (held == 0u) return 0u;
  const uint32_t begin = LoadGuestU32(base, held + 16u);
  const uint32_t end = LoadGuestU32(base, held + 20u);
  if (!LooksLikeGuestPointer(begin) || end < begin || (end - begin) > 12u * 64u) return 0u;
  for (uint32_t at = begin; at + 12u <= end; at += 12u) {
    const uint32_t holder = LoadGuestU32(base, at + 8u);
    const uint32_t item = LooksLikeGuestPointer(holder) ? LoadGuestU32(base, holder) : 0u;
    if (LooksLikeGuestPointer(item) && (LoadGuestU8(base, item + 144u) & 0x40u) != 0u &&
        LoadGuestU32(base, item + 28u) == record) {
      return item;
    }
  }
  return 0u;
}

/// The host's equipped item per body and class ([0] melee, [1] ranged), set by EquipRecord on
/// the host; and the tick each was first seen off the body (0 while held).
std::map<uint32_t, std::array<uint32_t, 2>> g_coop_host_weapon;
std::map<uint32_t, uint32_t> g_coop_host_weapon_loose;

/// One record onto one body: the guest's own equip-by-record sequence, step by step, with the
/// activation the guest skips on a client done here. Appends the outcome to `report`; returns
/// true when the item was attached. NO latch — the callers decide when to try.
bool EquipRecord(PPCContext& ctx, uint8_t* base, uint32_t entity, uint32_t id, std::string& report) {
  const uint32_t weapons = EntityComponent(base, entity, 28u);
  const uint32_t inventory = EntityComponent(base, entity, 13u);
  const uint32_t held = EntityComponent(base, entity, 24u);
  char step[160];
  if (weapons == 0u || inventory == 0u || held == 0u) {
    std::snprintf(step, sizeof(step), " %08X: body lacks comp 28/13/24;", id);
    report += step;
    return false;
  }

  // Scratch above the callee's argument-save area: frame+0x60 and up.
  const uint32_t frame = (ctx.r1.u32 - 0x400u) & ~0xFu;
  const uint32_t handle = frame + 0x60u;     // 4 words: sub_824F1C90's item handle
  const uint32_t name = frame + 0x80u;       // LHString "Weapon", the item entity's name
  const uint32_t text = frame + 0xC0u;
  static const char kWeapon[] = "Weapon";
  std::memcpy(base + text, kWeapon, sizeof(kWeapon));

  const auto call = [&](PPCFunc* fn, uint32_t r3, uint32_t r4 = 0u, uint32_t r5 = 0u) -> uint32_t {
    PPCContext work = ctx;
    work.r1.u32 = frame;
    work.r3.u32 = r3;
    work.r4.u32 = r4;
    work.r5.u32 = r5;
    fn(work, base);
    return work.r3.u32;
  };

  // 1. Held? (a plain lookup, released again)
  for (uint32_t i = 0; i < 4u; ++i) StoreGuestU32(base, handle + 4u * i, 0u);
  call(rex_CECInventory_FindItemByRecordHash, handle, inventory, id);
  const uint32_t record = LoadGuestU32(base, handle);
  if (record == 0u) {
    std::snprintf(step, sizeof(step), " %08X: NOT HELD in comp 13;", id);
    report += step;
    return false;
  }
  call(rex_lhTrackedObject_RemoveHandleRef, record, handle);

  // 2. The slot name for the record's WeaponType.
  const uint32_t slot = call(rex_CECWeaponSet_GetSlotNameForRecord, weapons, id);
  const uint32_t slot_block = slot != 0u ? LoadGuestU32(base, slot) : 0u;
  if (slot_block == 0u || LoadGuestU32(base, slot_block + 4u) == 0u) {
    std::snprintf(step, sizeof(step), " %08X: no slot name (WeaponType unresolved);", id);
    report += step;
    return false;
  }

  // 3. Instantiate the held record, with the sync's activation flag raised on a client.
  call(rex_lhCharString_CtorFromCStr, name, text, 0xFFFFFFFFu);
  const uint32_t manager = CoopNetworkManager(base);
  const bool client = IsCoopJoiner(base);
  const uint8_t was = manager != 0u ? LoadGuestU8(base, manager + 239u) : 0u;
  if (client && manager != 0u) base[manager + 239u] = 1u;
  const uint32_t item = call(rex_CECInventory_InstantiateHeldRecord, inventory, id, name);
  if (client && manager != 0u) base[manager + 239u] = was;
  if (item == 0u) {
    call(rex_lhCharString_Dtor, name);
    std::snprintf(step, sizeof(step), " %08X: instantiate returned nothing;", id);
    report += step;
    return false;
  }

  // Activated is entity+144 bit 0x10: `sub_8238BE08` clears 0x80, starts every component and
  // sets 0x90 (0x8238BE2C, 0x8238BE74). Bit 0x40 is ALIVE — set by the constructor, cleared
  // only by destroy — so testing it here never ran the activation.
  bool activated = (LoadGuestU8(base, item + 144u) & 0x10u) != 0u;
  if (!activated) {
    call(rex_lhEntity_Activate, item);
    activated = (LoadGuestU8(base, item + 144u) & 0x10u) != 0u;
  }

  // 3a. Player two's weapon is never a saved world entity: the guest's own no-save bit,
  // `entity+0x91 |= 0x40`, exactly as its henchman arm sets it on the pair it makes
  // (`sub_82315AD0` 0x82315FFC..0x82316004 / 0x82316034..0x8231603C; disc `sub_82351E30`
  // 0x8235235C..0x82352364 / 0x82352394..0x8235239C) and its player-two spawn sets it on the body
  // (`sub_82315030` 0x82315658; disc `sub_82351390` 0x823519B8). The save's "Entities" writer
  // skips an entity with it (`sub_82367110` 0x823671E0..0x823671F0; disc `sub_823627B0`
  // 0x82362880..0x82362890). Today nothing marks these items saving — the constructor sets +0x90
  // to 0xCC (`sub_8238B320` 0x8238B3FC) and the attach adds bit 0x02 only for a holder whose
  // Carrying+0x3D is set (0x824FB914..0x824FB928; disc 0x825010DC..0x825010F0), which only the Lua
  // `Carrying.SetAsLevelSavingCarriedObjects` does and no shipped script calls — so this states
  // the contract rather than curing a seen defect: a host save never writes them, on any path.
  if (entity == CoopPlayerTwoEntity(base)) {
    base[item + 0x91u] = static_cast<uint8_t>(LoadGuestU8(base, item + 0x91u) | 0x40u);
  }

  // 3b. On the host, every OTHER held item of the new one's class leaves too. The slot of a
  // class is per record (`sub_824AC820` keys the sheath slot on WeaponType), so a new ranged
  // weapon can go to `.SheathRangedWeaponBack` while the old one hangs on `.SheathWeaponFront`;
  // releasing only the target slot left two crossbows on the body. Class is the guest's own
  // test (`sub_824AD108` 0x824AD254): component 179 `+12` through `sub_82561420`, 2..4 melee,
  // 5..8 ranged. Released by item (`sub_824FDA98`) and destroyed, as `sub_824FDB70` does.
  if (!client) {
    const auto weapon_class = [&](uint32_t entity_item) { return WeaponClassOf(base, entity_item); };
    const uint32_t wanted = weapon_class(item);
    PPCFunc* release_item = FindGuestFunction(refii::guest::Address(0x824FDA98u));
    std::array<uint32_t, 16> others{};
    std::size_t n = 0u;
    const uint32_t begin = LoadGuestU32(base, held + 16u);
    const uint32_t end = LoadGuestU32(base, held + 20u);
    if (wanted != 0u && release_item != nullptr && LooksLikeGuestPointer(begin) && end >= begin &&
        end - begin <= 12u * 64u) {
      for (uint32_t at = begin; at + 12u <= end && n < others.size(); at += 12u) {
        const uint32_t holder = LoadGuestU32(base, at + 8u);
        const uint32_t other = LooksLikeGuestPointer(holder) ? LoadGuestU32(base, holder) : 0u;
        if (LooksLikeGuestPointer(other) && other != item &&
            (LoadGuestU8(base, other + 144u) & 0x40u) != 0u && weapon_class(other) == wanted) {
          others[n++] = other;
        }
      }
    }
    for (std::size_t i = 0; i < n; ++i) {
      call(release_item, held, others[i]);
      call(rex_lhEntity_Destroy, others[i]);
      std::snprintf(step, sizeof(step), " %08X: old %s item 0x%08X (record %08X) removed;", id,
                    wanted == 1u ? "melee" : "ranged", others[i], LoadGuestU32(base, others[i] + 28u));
      report += step;
    }
  }

  // 4. Release the slot's current item and destroy it — the guest's own `sub_824FDB70` on the
  // host. Without the release the new item attaches beside the old one.
  {
    const uint32_t old = call(rex_CECHeldItems_ReleaseSlot, held, slot);
    if (old != 0u && LooksLikeGuestPointer(old) && old != item &&
        (LoadGuestU8(base, old + 144u) & 0x40u) != 0u) {
      call(rex_lhEntity_Destroy, old);
    }
  }

  // 5. Attach, record, draw. On the host the attach is BROADCAST for a body the joiner
  // controls: `sub_824FB5D8` sends held-items type 1 only under `sub_82214AF8`, which on the
  // host needs CECNetwork+104 clear, so for that one call it is cleared. The joiner's handler
  // then attaches the replica right behind its create, in the same stream — not after our
  // CoopHeroHeld round trip, which left it lying where it was made for a moment at spawn
  // (2026-10-08 02:59: on the ground, then into the hand). The record and the effect stay
  // unbroadcast: a weapon-set message (`sub_824AD698`) would overwrite the joiner's
  // +84/+88 with entity ids (`sub_824AB0A0`).
  {
    const uint32_t network = client ? 0u : EntityComponent(base, entity, 91u);
    const uint8_t controlled = network != 0u ? LoadGuestU8(base, network + 104u) : 0u;
    if (network != 0u) base[network + 104u] = 0u;
    call(rex_CECHeldItems_AttachItemToSlot, held, slot, item);
    if (network != 0u) base[network + 104u] = controlled;
  }
  call(rex_CECWeaponSet_RecordDrawnItem, weapons, item);
  call(rex_CECWeaponSet_PlayWeaponDrawEffect, weapons, item);
  call(rex_lhCharString_Dtor, name);
  if (!client) {
    // The previous weapon of this class, if it was left loose in the world, goes now.
    const uint32_t weapon_class = WeaponClassOf(base, item);
    if (weapon_class != 0u) {
      uint32_t& tracked = g_coop_host_weapon[entity][weapon_class - 1u];
      if (tracked != 0u && tracked != item && (LoadGuestU8(base, tracked + 144u) & 0x40u) != 0u &&
          HeldItemWithRecord(base, entity, LoadGuestU32(base, tracked + 28u)) != tracked) {
        call(rex_lhEntity_Destroy, tracked);
        std::snprintf(step, sizeof(step), " %08X: loose old item 0x%08X removed;", id, tracked);
        report += step;
      }
      tracked = item;
    }
  }
  std::snprintf(step, sizeof(step), " %08X: item 0x%08X %s, attached;", id, item,
                activated ? "active" : "NOT ACTIVE even after sub_8238BE08");
  report += step;
  return true;
}

/// Body → the (melee, ranged) pair every record of which was drawn. A pair with a record the
/// body did not hold yet is NOT latched: EquipRecord refuses before instantiating anything, so
/// the next apply (after the give lands) tries again at no cost. A record the body already
/// holds in any slot counts as done without instantiating a second item.
std::map<uint32_t, std::pair<uint32_t, uint32_t>> g_coop_equip_latched;

/// True when every record is drawn (or there is nothing for this side to draw); false when the
/// body has no weapon set yet or a record could not be drawn.
bool ApplyEquip(PPCContext& ctx, uint8_t* base, int player, uint32_t entity, uint32_t melee,
                uint32_t ranged) {
  if (entity == 0u || (melee == 0u && ranged == 0u)) {
    return true;
  }

  // CONTRACT: a joiner never instantiates. `CoopHeroEquip:1` and `:2` are stored on a joiner
  // and ignored here; the host's items replicate as entities and CoopAttachHeld attaches
  // those by net id.
  if (IsCoopJoiner(base)) {
    return true;
  }

  const auto seen = g_coop_equip_latched.find(entity);
  if (seen != g_coop_equip_latched.end() && seen->second == std::make_pair(melee, ranged)) {
    return true;
  }

  const uint32_t weapons = EntityComponent(base, entity, 28u);
  if (weapons == 0u) {
    return false;  // no weapon set yet; the next apply asks again
  }

  std::string report;
  char step[64];
  bool all = true;
  for (uint32_t id : {melee, ranged}) {
    if (id == 0u) {
      continue;
    }

    // Held already, in any slot. Not the weapon set's +84/+88: on a body the joiner controls
    // its weapon-set message (`sub_824AB0A0`) rewrites them with entity ids.
    if (HeldItemWithRecord(base, entity, id) != 0u) {
      std::snprintf(step, sizeof(step), " %08X: already held;", id);
      report += step;
      continue;
    }

    if (!EquipRecord(ctx, base, entity, id, report)) {
      all = false;
    }
  }

  if (all) {
    g_coop_equip_latched[entity] = std::make_pair(melee, ranged);
  }

  // One line per distinct outcome: a pair waiting on its give would otherwise log every tick.
  static std::map<uint32_t, std::string> reported;
  if (reported[entity] != report) {
    reported[entity] = report;
    REXLOG_INFO("Refii guest-tick pump: CoopHeroEquip — player {} 0x{:08X}:{} weapon set 0x{:08X} "
                "now holds melee 0x{:08X}, ranged 0x{:08X}{}",
                player, entity, report, weapons, LoadGuestU32(base, weapons + 84u),
                LoadGuestU32(base, weapons + 88u), all ? "" : " — incomplete, retried on the next apply");
  }

  return all;
}

/// THE HOST KEEPS PLAYER TWO'S WEAPONS ON PLAYER TWO. The joiner controls that body, so its
/// draw and sheathe arrive as held-items messages: a release (`sub_824FCB20`, which never
/// destroys) followed by an attach of the same replica into the next slot. A release with no
/// attach after it — the joiner's guest acting on a stale weapon set, or spawning an item the
/// host cannot resolve — leaves the host's weapon alive in the world, on the floor of both
/// machines. The weapons EquipRecord put on the body are therefore re-attached to their
/// record's slot once they have been off every slot for kLooseTicks, silently (the host is
/// not the body's authority, `sub_82214AF8`, so the attach broadcasts nothing). A destroyed
/// item is forgotten.
void CoopKeepHostWeapons(PPCContext& ctx, uint8_t* base, uint32_t entity) {
  constexpr uint32_t kLooseTicks = 4u;
  const auto found = g_coop_host_weapon.find(entity);
  if (entity == 0u || found == g_coop_host_weapon.end()) return;
  const uint32_t weapons = EntityComponent(base, entity, 28u);
  const uint32_t held = EntityComponent(base, entity, 24u);
  if (weapons == 0u || held == 0u) return;

  const uint32_t frame = (ctx.r1.u32 - 0x400u) & ~0xFu;
  const auto call = [&](PPCFunc* fn, uint32_t r3, uint32_t r4 = 0u, uint32_t r5 = 0u) -> uint32_t {
    PPCContext work = ctx;
    work.r1.u32 = frame;
    work.r3.u32 = r3;
    work.r4.u32 = r4;
    work.r5.u32 = r5;
    fn(work, base);
    return work.r3.u32;
  };

  bool loose = false;
  for (uint32_t& item : found->second) {
    if (item == 0u) continue;
    if ((LoadGuestU8(base, item + 144u) & 0x40u) == 0u) {
      item = 0u;  // destroyed: the guest or a later equip replaced it
      continue;
    }
    const uint32_t record = LoadGuestU32(base, item + 28u);
    if (HeldItemWithRecord(base, entity, record) == item) continue;
    loose = true;
    uint32_t& since = g_coop_host_weapon_loose[entity];
    if (since == 0u) since = g_coop_ticks;
    if (g_coop_ticks - since < kLooseTicks) continue;

    const uint32_t slot = call(rex_CECWeaponSet_GetSlotNameForRecord, weapons, record);
    const uint32_t slot_block = slot != 0u ? LoadGuestU32(base, slot) : 0u;
    if (slot_block == 0u || LoadGuestU32(base, slot_block + 4u) == 0u) continue;
    call(rex_CECHeldItems_AttachItemToSlot, held, slot, item);
    REXLOG_INFO("Refii guest-tick pump: CoopHeroEquip — player two 0x{:08X}: weapon 0x{:08X} "
                "(record {:08X}) was off the body {} ticks — {}",
                entity, item, record, g_coop_ticks - since,
                HeldItemWithRecord(base, entity, record) == item ? "re-attached to its slot"
                                                                  : "re-attach REFUSED");
  }
  if (!loose) g_coop_host_weapon_loose[entity] = 0u;
}

/// The host's held items per player, as 64-bit net ids (the entity uid, high word `+0x14`,
/// low word `+0x18`): [player-1][0] melee, [1] ranged.
uint64_t g_coop_held[2][2] = {{0u, 0u}, {0u, 0u}};

/// Body → the (melee, ranged) net ids it was last attached with.
std::map<uint32_t, std::pair<uint64_t, uint64_t>> g_coop_held_attached;

/// See kActionCoopHeroHeldPrefix — stores; the tick attaches.
bool DispatchCoopHeroHeld(const std::string& text) {
  const std::size_t colon = text.find(':');
  if (colon == std::string::npos) {
    return true;
  }

  const int player = std::atoi(text.substr(0, colon).c_str());
  if (player != 1 && player != 2) {
    return true;
  }

  const std::string pair = text.substr(colon + 1u);
  const std::size_t comma = pair.find(',');
  g_coop_held[player - 1][0] =
      static_cast<uint64_t>(std::strtoull(pair.substr(0, comma).c_str(), nullptr, 16));
  g_coop_held[player - 1][1] = comma == std::string::npos
      ? 0u
      : static_cast<uint64_t>(std::strtoull(pair.substr(comma + 1u).c_str(), nullptr, 16));
  return true;
}

/// On a client: the host's replicated weapon entities, by net id, put on the body SILENTLY when
/// the guest's own attach did not land.
///
/// THE GUEST'S ATTACH RACES ITS OWN CREATE. The host's EquipRecord ends in `sub_824FB5D8`
/// (attach to slot), which on an authoritative holder sends a held-items message of type 1
/// (0x824fcb10); the client's handler `sub_824FEC48` resolves the replica with
/// `sub_82334A60(netmgr, netId)` and attaches it — but only an ACTIVE item (`+144 & 0x40`,
/// 0x824fb610), and a replica spawned by the create message just before it is not yet active
/// (`sub_8238B860` skips activation on a client), so the attach is dropped on the floor and
/// nothing retries it. Player one's weapons land because they were active long before the
/// join sync restated them; player two's are made at the moment they are equipped (run
/// 2026-10-07 20:31: both replicas present, "NOT on the body yet", for the whole session).
///
/// NEVER ATTACH WITH AUTHORITY FROM HERE. Both machines are authoritative for player two's
/// body (`sub_82214AF8`: the host because it is not locally controlled there, the client
/// because it is), so an attach made here would be sent to the host, whose `sub_824FB5D8`
/// finds the slot occupied (`sub_824F9AC8`) and RELEASES AND DESTROYS the occupant
/// (`sub_824FDB70`, 0x824fb878) — the very item — before re-attaching it; an earlier version
/// of this routine that released and attached with authority bounced one weapon off and the
/// other on every few seconds (run 19:21). The send is gated on the holder's CECNetwork
/// byte `+104` ("locally controlled", read by `sub_82214AF8` at 0x82214b78), so that byte is
/// cleared for the one call and restored: the attachment graph is updated here, nothing
/// crosses the wire, and the host keeps its own attachment, which it already has.
void CoopAttachHeld(PPCContext& ctx, uint8_t* base, int player, uint32_t entity) {
  if (entity == 0u) {
    return;
  }

  const uint64_t melee_net = g_coop_held[player - 1][0];
  const uint64_t ranged_net = g_coop_held[player - 1][1];
  if (melee_net == 0u && ranged_net == 0u) {
    return;
  }

  const auto pair = std::make_pair(melee_net, ranged_net);
  const auto seen = g_coop_held_attached.find(entity);
  if (seen != g_coop_held_attached.end() && seen->second == pair) {
    return;
  }

  // Paced: one try per body every kRetryTicks, and at most kMaxAttempts silent attaches per
  // (body, pair) — one line when that cap is hit, then nothing until the pair changes.
  constexpr uint32_t kRetryTicks = 8u;
  constexpr uint32_t kMaxAttempts = 20u;
  auto& attempts = g_coop_held_attempts[entity];
  if (attempts.first != pair) {
    attempts = std::make_pair(pair, 0u);
  }
  if (attempts.second >= kMaxAttempts) {
    return;
  }
  uint32_t& next_try = g_coop_held_next_try[entity];
  if (g_coop_ticks < next_try) {
    return;
  }
  next_try = g_coop_ticks + kRetryTicks;

  const uint32_t manager = CoopNetworkManager(base);
  const uint32_t weapons = EntityComponent(base, entity, 28u);
  const uint32_t held = EntityComponent(base, entity, 24u);
  if (manager == 0u || weapons == 0u || held == 0u) {
    return;
  }

  const uint32_t frame = (ctx.r1.u32 - 0x400u) & ~0xFu;
  const auto call = [&](PPCFunc* fn, uint32_t r3, uint64_t r4 = 0u, uint32_t r5 = 0u) -> uint32_t {
    PPCContext work = ctx;
    work.r1.u32 = frame;
    work.r3.u32 = r3;
    work.r4.u64 = r4;
    work.r5.u32 = r5;
    fn(work, base);
    return work.r3.u32;
  };

  // What the body holds: component 24 entries of 12 bytes at `+16..+20`, holder at entry+8,
  // item = `*holder` (`sub_824FB4E8`).
  const auto held_here = [&](uint32_t item) -> bool {
    const uint32_t begin = LoadGuestU32(base, held + 16u);
    const uint32_t end = LoadGuestU32(base, held + 20u);
    if (!LooksLikeGuestPointer(begin) || end < begin || (end - begin) > 12u * 64u) return false;
    for (uint32_t at = begin; at + 12u <= end; at += 12u) {
      const uint32_t holder = LoadGuestU32(base, at + 8u);
      if (LooksLikeGuestPointer(holder) && LoadGuestU32(base, holder) == item) return true;
    }
    return false;
  };
  const auto held_items = [&](std::array<uint32_t, 16>& out) -> std::size_t {
    std::size_t n = 0u;
    const uint32_t begin = LoadGuestU32(base, held + 16u);
    const uint32_t end = LoadGuestU32(base, held + 20u);
    if (!LooksLikeGuestPointer(begin) || end < begin || (end - begin) > 12u * 64u) return 0u;
    for (uint32_t at = begin; at + 12u <= end && n < out.size(); at += 12u) {
      const uint32_t holder = LoadGuestU32(base, at + 8u);
      const uint32_t item = LooksLikeGuestPointer(holder) ? LoadGuestU32(base, holder) : 0u;
      if (LooksLikeGuestPointer(item)) out[n++] = item;
    }
    return n;
  };
  // A replica is what the replica map answers for the id in its CECNetwork (+120, the u64 the
  // client's create handler stores at 0x8232EBF4..); a local item is not in the map.
  const auto is_replica = [&](uint32_t item) -> bool {
    const uint32_t network = EntityComponent(base, item, 91u);
    if (network == 0u) return false;
    const uint64_t id = (static_cast<uint64_t>(LoadGuestU32(base, network + 120u)) << 32) |
                        LoadGuestU32(base, network + 124u);
    return id != 0u && call(rex_lhNetManager_FindEntityByNetId, manager, id) == item;
  };
  // What a guest call took off the body: every LOCAL item that left is destroyed (a client's
  // release never destroys, `sub_824FDB70` 0x824FDB80..0x824FDBE8); a replica is the host's.
  const auto destroy_local_leavers = [&](const std::array<uint32_t, 16>& before, std::size_t had,
                                         uint32_t keep, const char* by) {
    for (std::size_t i = 0; i < had; ++i) {
      const uint32_t gone = before[i];
      if (gone == keep || held_here(gone) || (LoadGuestU8(base, gone + 144u) & 0x40u) == 0u ||
          is_replica(gone)) {
        continue;
      }
      REXLOG_INFO("Refii guest-tick pump: CoopHeroHeld — player {} 0x{:08X}: local item 0x{:08X} "
                  "(record {:08X}) displaced by {} 0x{:08X} — destroyed",
                  player, entity, gone, LoadGuestU32(base, gone + 28u), by, keep);
      call(rex_lhEntity_Destroy, gone);
    }
  };
  // THE WEAPON SET STATES WHAT THE BODY HOLDS. The draw action (`sub_82837AB8`, no authority
  // gate) takes the record from weapons+84 (melee, 0x82837D48) or +88 (ranged, 0x82837E10),
  // looks in that record's slot and, when the item there carries another record, empties the
  // slot (`sub_824FDB70`, 0x82837F48) and instantiates the record LOCALLY (0x828380A0). On the
  // joiner those fields still named the save's weapons, so every draw released the host's
  // weapon onto the floor of both machines and spawned the old one (run 2026-10-08 02:07:55).
  // The guest's own RecordDrawnItem (`sub_824AD108`) stores the replica's record by its class,
  // run with the body's authority cleared so it sends no weapon-set message.
  const auto state_in_weapon_set = [&](uint32_t item, uint32_t record) {
    const uint32_t kind = EntityComponent(base, item, 179u);
    const uint32_t type = kind != 0u ? LoadGuestU32(base, kind + 12u) : 0u;
    const uint32_t field = type >= 2u && type <= 4u ? 84u : type >= 5u && type <= 8u ? 88u : 0u;
    if (field == 0u || LoadGuestU32(base, weapons + field) == record) return;
    std::array<uint32_t, 16> before{};
    const std::size_t had = held_items(before);
    const uint32_t network = EntityComponent(base, entity, 91u);
    const uint8_t controlled = network != 0u ? LoadGuestU8(base, network + 104u) : 0u;
    if (network != 0u) base[network + 104u] = 0u;
    call(rex_CECWeaponSet_RecordDrawnItem, weapons, item);
    if (network != 0u) base[network + 104u] = controlled;
    destroy_local_leavers(before, had, item, "the weapon set's record of replica");
    REXLOG_INFO("Refii guest-tick pump: CoopHeroHeld — player {} 0x{:08X}: weapon set 0x{:08X} "
                "now states {} {:08X} (replica 0x{:08X})",
                player, entity, weapons, field == 84u ? "melee" : "ranged", record, item);
  };

  // Outcomes per net id, formatted only when a line is logged.
  enum class Outcome { kOnBody, kNoSlot, kAttached, kRefused };
  struct Step {
    uint64_t net;
    uint32_t item;
    uint32_t record;
    Outcome outcome;
  };
  Step steps[2];
  std::size_t count = 0u;
  bool complete = true;
  bool attempted = false;
  for (uint64_t net : {melee_net, ranged_net}) {
    if (net == 0u) {
      continue;
    }

    const uint32_t item = call(rex_lhNetManager_FindEntityByNetId, manager, net);
    if (!LooksLikeGuestPointer(item) || (LoadGuestU8(base, item + 144u) & 0x40u) == 0u) {
      complete = false;
      continue;  // not replicated here yet
    }

    const uint32_t record = LoadGuestU32(base, item + 28u);
    if (held_here(item)) {
      state_in_weapon_set(item, record);
      steps[count++] = {net, item, record, Outcome::kOnBody};
      continue;
    }

    const uint32_t slot = call(rex_CECWeaponSet_GetSlotNameForRecord, weapons, record);
    const uint32_t slot_block = slot != 0u ? LoadGuestU32(base, slot) : 0u;
    if (slot_block == 0u || LoadGuestU32(base, slot_block + 4u) == 0u) {
      steps[count++] = {net, item, record, Outcome::kNoSlot};
      complete = false;
      continue;
    }

    // Silently: the holder is not "locally controlled" for the length of the call.
    // An occupied slot is emptied first by `sub_824FDB70` (0x824FB878), which RELEASES the
    // occupant and destroys it only on the host (netmgr+0x34 && +0x35, 0x824FDB80..0x824FDBE8):
    // on this client a local occupant is left in the world. Every local item the attach took
    // off the body is destroyed here; a replica is the host's.
    std::array<uint32_t, 16> before{};
    const std::size_t had = held_items(before);
    const uint32_t network = EntityComponent(base, entity, 91u);
    const uint8_t controlled = network != 0u ? LoadGuestU8(base, network + 104u) : 0u;
    if (network != 0u) base[network + 104u] = 0u;
    call(rex_CECHeldItems_AttachItemToSlot, held, slot, item);
    if (network != 0u) base[network + 104u] = controlled;
    attempted = true;
    destroy_local_leavers(before, had, item, "replica");

    const bool attached = held_here(item);
    if (!attached) {
      complete = false;
    } else {
      state_in_weapon_set(item, record);
    }
    steps[count++] = {net, item, record, attached ? Outcome::kAttached : Outcome::kRefused};
  }

  if (attempted) {
    ++attempts.second;
  }

  const auto report = [&]() -> std::string {
    std::string text;
    char step[160];
    for (std::size_t i = 0; i < count; ++i) {
      const Step& s = steps[i];
      switch (s.outcome) {
        case Outcome::kOnBody:
          std::snprintf(step, sizeof(step), " net %016llX: replica 0x%08X (record %08X) on the body;",
                        static_cast<unsigned long long>(s.net), s.item, s.record);
          break;
        case Outcome::kNoSlot:
          std::snprintf(step, sizeof(step), " net %016llX: record %08X has no slot name;",
                        static_cast<unsigned long long>(s.net), s.record);
          break;
        default:
          std::snprintf(step, sizeof(step), " net %016llX: replica 0x%08X (record %08X) %s;",
                        static_cast<unsigned long long>(s.net), s.item, s.record,
                        s.outcome == Outcome::kAttached ? "attached here, silently"
                                                        : "attach REFUSED");
          break;
      }
      text += step;
    }
    return text;
  };

  if (!complete) {
    if (attempts.second >= kMaxAttempts) {
      REXLOG_INFO("Refii guest-tick pump: CoopHeroHeld — player {} 0x{:08X}: gave up after {} "
                  "silent attach attempts (melee net {:016X}, ranged net {:016X}){}",
                  player, entity, attempts.second, melee_net, ranged_net, report());
      return;
    }

    if (g_coop_held_waited[entity] != pair) {
      g_coop_held_waited[entity] = pair;
      REXLOG_INFO("Refii guest-tick pump: CoopHeroHeld — player {} 0x{:08X}: waiting for the "
                  "host's replicas (melee net {:016X}, ranged net {:016X}){}",
                  player, entity, melee_net, ranged_net, report());
    }
    return;
  }

  g_coop_held_attached[entity] = pair;
  REXLOG_INFO("Refii guest-tick pump: CoopHeroHeld — player {} 0x{:08X}:{} weapon set 0x{:08X} "
              "holds melee 0x{:08X}, ranged 0x{:08X}",
              player, entity, report(), weapons, LoadGuestU32(base, weapons + 84u),
              LoadGuestU32(base, weapons + 88u));
}

/// Body → generation last applied. Cleared with the other latches when the session drops.
std::map<uint32_t, uint32_t> g_coop_applied_generation;

/// The HeroState version last applied per player, and the body it was applied to — the
/// bridge's RefiiHostGetHeroStateApplied answer, which Godot acks back to the joiner.
std::atomic<uint32_t> g_herostate_applied[2] = {0u, 0u};
uint32_t g_herostate_body[2] = {0u, 0u};

/// Standard base64 (RFC 4648 alphabet, '=' padding, whitespace ignored). False on any other
/// character or a dangling 1-char tail.
bool DecodeBase64(const std::string& text, std::vector<uint8_t>& out) {
  out.clear();
  out.reserve(text.size() / 4u * 3u);
  uint32_t acc = 0u;
  int bits = 0;
  std::size_t pad = 0u;
  for (char c : text) {
    int value;
    if (c >= 'A' && c <= 'Z') value = c - 'A';
    else if (c >= 'a' && c <= 'z') value = c - 'a' + 26;
    else if (c >= '0' && c <= '9') value = c - '0' + 52;
    else if (c == '+') value = 62;
    else if (c == '/') value = 63;
    else if (c == '=') { ++pad; continue; }
    else if (c == ' ' || c == '\r' || c == '\n' || c == '\t') continue;
    else return false;
    if (pad != 0u) {
      return false;  // data after padding
    }
    acc = (acc << 6) | static_cast<uint32_t>(value);
    bits += 6;
    if (bits >= 8) {
      bits -= 8;
      out.push_back(static_cast<uint8_t>((acc >> bits) & 0xFFu));
    }
  }
  return bits < 6;  // a lone sextet cannot start a byte
}

/// A little-endian cursor over a decoded HeroState; every read is bounds-checked and the first
/// short read latches `bad`, so a truncated or count-overrun payload parses to nothing.
struct HeroStateReader {
  const std::vector<uint8_t>& bytes;
  std::size_t at = 0u;
  bool bad = false;

  bool take(void* into, std::size_t size) {
    if (bad || size > bytes.size() - at) {
      bad = true;
      return false;
    }
    std::memcpy(into, bytes.data() + at, size);
    at += size;
    return true;
  }
  uint8_t u8() { uint8_t v = 0u; take(&v, 1u); return v; }
  uint16_t u16() { uint16_t v = 0u; take(&v, 2u); return v; }
  uint32_t u32() { uint32_t v = 0u; take(&v, 4u); return v; }
  int32_t i32() { int32_t v = 0; take(&v, 4u); return v; }
  float f32() { float v = 0.0f; take(&v, 4u); return v; }
};

/// A float the way the old C# encoders wrote one ("0.###" / "0.####"): fixed `decimals`
/// places, trailing zeros and a bare point trimmed. ApplyStats/ApplyBuild parse with strtof.
std::string TrimmedFixed(float value, int decimals) {
  char text[64];
  std::snprintf(text, sizeof(text), "%.*f", decimals, static_cast<double>(value));
  std::string out = text;
  if (out.find('.') != std::string::npos) {
    while (!out.empty() && out.back() == '0') out.pop_back();
    if (!out.empty() && out.back() == '.') out.pop_back();
  }
  if (out == "-0") out = "0";
  return out;
}

/// See kActionHeroStatePrefix. Parses one player's HeroState into the per-player stores the
/// apply steps already read, exactly as the nine field actions used to fill them:
///   record.generation = version; melee/ranged = the record ids; face = face;
///   record.stats  = "money,gender,morality,purity,renown,r0..r14,e0..e3,t0..t3"
///                   (gender 2 male / 1 female, ranks decimal, floats TrimmedFixed 3);
///   record.skills = "r0,r1,...,r14" hex (empty unless `known`);
///   record.build  = "strength,skill,will,fat,height,tan" TrimmedFixed 4 (only with hasLooks);
///   g_coop_wear   = the worn hashes; g_coop_give = inventory plus one each of melee/ranged
///                   when the inventory lacks them (the drawn weapons are held entities, not
///                   inventory records, so a give without them leaves the equip nothing to draw).
/// Player two's outfit and inventory are mirrored into slot 2, the co-op screen's preview.
/// The tick's CoopApplyHero does the rest; the version is its generation.
bool DispatchHeroState(const std::string& text) {
  constexpr uint32_t kMagic = 0x31545348u;  // 'H','S','T','1' little-endian
  constexpr std::size_t kNameLimit = 48u;

  static std::string rejected;  // the last malformed action, logged once
  const auto reject = [&](const char* why) {
    if (rejected != text) {
      rejected = text;
      REXLOG_INFO("Refii guest-tick pump: HeroState rejected — {} ({} chars)", why, text.size());
    }
    return true;
  };

  const std::size_t colon = text.find(':');
  if (colon == std::string::npos) {
    return reject("no player");
  }
  const int player = std::atoi(text.substr(0, colon).c_str());
  if (player != 1 && player != 2) {
    return reject("player is not 1 or 2");
  }

  std::vector<uint8_t> bytes;
  if (!DecodeBase64(text.substr(colon + 1u), bytes)) {
    return reject("bad base64");
  }

  HeroStateReader in{bytes};
  if (in.u32() != kMagic) {
    return reject("bad magic");
  }
  const uint32_t version = in.u32();
  const uint8_t flags = in.u8();
  const bool female = (flags & 1u) != 0u;
  const bool has_looks = (flags & 2u) != 0u;
  const bool known = (flags & 4u) != 0u;
  const uint8_t name_len = in.u8();
  if (in.bad || name_len > kNameLimit) {
    return reject("name length");
  }
  std::string name(name_len, '\0');
  if (name_len != 0u) {
    in.take(&name[0], name_len);
  }
  const uint32_t face = in.u32();
  const float fat = in.f32();
  const float height = in.f32();
  const float tan = in.f32();
  const float strength = in.f32();
  const float skill = in.f32();
  const float will = in.f32();
  const float morality = in.f32();
  const float purity = in.f32();
  const int32_t renown = in.i32();
  const int32_t money = in.i32();

  const uint8_t rank_count = in.u8();
  std::vector<int32_t> ranks;
  for (uint8_t i = 0; i < rank_count && !in.bad; ++i) ranks.push_back(in.i32());
  const uint8_t exp_count = in.u8();
  std::vector<float> pools;
  for (uint8_t i = 0; i < exp_count && !in.bad; ++i) pools.push_back(in.f32());
  const uint8_t total_count = in.u8();
  std::vector<float> totals;
  for (uint8_t i = 0; i < total_count && !in.bad; ++i) totals.push_back(in.f32());

  const uint32_t melee = in.u32();
  const uint32_t ranged = in.u32();

  const uint16_t worn_count = in.u16();
  std::vector<uint32_t> worn;
  for (uint16_t i = 0; i < worn_count && !in.bad; ++i) {
    const uint32_t hash = in.u32();
    if (hash != 0u) worn.push_back(hash);
  }
  const uint16_t inv_count = in.u16();
  std::vector<std::pair<uint32_t, uint32_t>> give;
  for (uint16_t i = 0; i < inv_count && !in.bad; ++i) {
    const uint32_t record = in.u32();
    const uint16_t count = in.u16();
    if (record != 0u && count != 0u) give.emplace_back(record, count);
  }
  uint32_t dog_definition = 0u;
  int32_t dog_searching = 0;
  int32_t dog_combat = 0;
  std::string dog_tricks;
  if (!in.bad && in.at < bytes.size()) {
    dog_definition = in.u32();
    dog_searching = in.u8();
    dog_combat = in.u8();
    const uint8_t trick_count = in.u8();
    for (uint8_t i = 0; i < trick_count && !in.bad; ++i) {
      const uint8_t length = in.u8();
      std::string trick(length, '\0');
      if (length != 0u) in.take(&trick[0], length);
      // A trick name goes into a Lua literal and a comma-separated list: GDB names only.
      bool plain = !trick.empty();
      for (char c : trick) plain = plain && (std::isalnum(static_cast<unsigned char>(c)) || c == '_');
      if (plain) dog_tricks += (dog_tricks.empty() ? "" : ",") + trick;
    }
  }
  std::vector<uint32_t> dog_worn;
  std::string dog_name;
  if (!in.bad && in.at < bytes.size()) {
    const uint8_t worn_count = in.u8();
    for (uint8_t i = 0; i < worn_count && !in.bad; ++i) {
      const uint32_t hash = in.u32();
      if (hash != 0u) dog_worn.push_back(hash);
    }
    const uint8_t name_length = in.u8();
    if (!in.bad && name_length <= kNameLimit) {
      dog_name.assign(name_length, '\0');
      if (name_length != 0u) in.take(&dog_name[0], name_length);
    }
  }
  if (in.bad) {
    return reject("short buffer");
  }

  // THE LOOKS EXTENSION, after the dog: "HSL1", u16 dyed worn items {u32 record, u8 slots, per
  // set bit (slot 1, then slot 2) u8 red, u8 green, u8 blue, f32 brightness}, u8 scars {u32 area,
  // u32 record, u8 permanent}. Absent from an older peer's record, and an older pump stops before
  // it. Read on its own cursor: a malformed extension drops the extension, never the record.
  constexpr uint32_t kLooksMagic = 0x314C5348u;  // 'H','S','L','1' little-endian
  std::vector<CoopItemDye> dyes;
  std::vector<CoopScar> scars;
  if (bytes.size() - in.at >= 4u) {
    HeroStateReader looks{bytes, in.at};
    if (looks.u32() == kLooksMagic) {
      const uint16_t dye_count = looks.u16();
      for (uint16_t i = 0; i < dye_count && !looks.bad; ++i) {
        CoopItemDye dye;
        dye.record = looks.u32();
        dye.slots = looks.u8() & 3u;
        for (uint32_t slot = 0u; slot < 2u; ++slot) {
          if ((dye.slots & (1u << slot)) == 0u) continue;
          dye.red[slot] = looks.u8();
          dye.green[slot] = looks.u8();
          dye.blue[slot] = looks.u8();
          dye.brightness[slot] = looks.f32();
          if (!std::isfinite(dye.brightness[slot])) dye.slots &= ~(1u << slot);
        }
        if (dye.record != 0u && dye.slots != 0u) dyes.push_back(dye);
      }
      const uint8_t scar_count = looks.u8();
      for (uint8_t i = 0; i < scar_count && !looks.bad; ++i) {
        CoopScar scar;
        scar.area = looks.u32();
        scar.record = looks.u32();
        scar.permanent = looks.u8() != 0u ? 1u : 0u;
        if (scar.area != 0u && scar.record != 0u) scars.push_back(scar);
      }
      if (looks.bad) {
        REXLOG_INFO("Refii guest-tick pump: HeroState v{} for player {}: looks extension "
                    "truncated — dyes and scars dropped", version, player);
        dyes.clear();
        scars.clear();
      }
    }
  }
  if (version == 0u) {
    return reject("version 0");
  }

  // The stats block, the old CoopHeroStats text: 5 + 15 + 4 + 4 = 28 fields, missing ranks
  // and pools padded with 0 as the old encoder padded them.
  std::string stats = std::to_string(money) + "," + (female ? "1" : "2") + "," +
                      TrimmedFixed(morality, 3) + "," + TrimmedFixed(purity, 3) + "," +
                      std::to_string(renown);
  for (std::size_t i = 0; i < 15u; ++i) {
    stats += "," + std::to_string(i < ranks.size() ? ranks[i] : 0);
  }
  for (std::size_t i = 0; i < 4u; ++i) {
    stats += "," + TrimmedFixed(i < pools.size() ? pools[i] : 0.0f, 3);
  }
  for (std::size_t i = 0; i < 4u; ++i) {
    stats += "," + TrimmedFixed(i < totals.size() ? totals[i] : 0.0f, 3);
  }

  // The ranks as the old CoopHeroSkills text: hex, ids 0..14 in order, only for a known hero.
  std::string skills;
  if (known) {
    for (std::size_t i = 0; i < ranks.size(); ++i) {
      char hex[16];
      std::snprintf(hex, sizeof(hex), "%s%X", i == 0u ? "" : ",",
                    static_cast<unsigned>(ranks[i] < 0 ? 0 : ranks[i]));
      skills += hex;
    }
  }

  // The body shape as the old CoopHeroBuild text, only when the state carries looks.
  std::string build;
  if (has_looks) {
    build = TrimmedFixed(strength, 4) + "," + TrimmedFixed(skill, 4) + "," +
            TrimmedFixed(will, 4) + "," + TrimmedFixed(fat, 4) + "," +
            TrimmedFixed(height, 4) + "," + TrimmedFixed(tan, 4);
  }

  // The drawn weapons too (the old GiveList): one each unless the inventory already lists it.
  for (uint32_t weapon : {melee, ranged}) {
    if (weapon == 0u) continue;
    bool listed = false;
    for (const auto& record : give) listed = listed || record.first == weapon;
    if (!listed) give.emplace_back(weapon, 1u);
  }

  const int slot = player - 1;
  CoopHeroRecord& record = g_coop_record[slot];
  record.generation = version;
  record.melee = melee;
  record.ranged = ranged;
  record.stats = std::move(stats);
  record.skills = std::move(skills);
  record.build = has_looks ? std::move(build) : std::string();
  record.face = face;
  record.name = std::move(name);
  record.dog_definition = dog_definition;
  record.dog_searching = dog_searching;
  record.dog_combat = dog_combat;
  record.dog_tricks = std::move(dog_tricks);
  record.dog_worn = std::move(dog_worn);
  record.dog_name = std::move(dog_name);
  if (player == 2) {
    g_coop_wear[2] = worn;
    g_coop_give[2] = give;
    g_coop_dyes[2] = dyes;
    g_coop_scars[2] = scars;
  }
  g_coop_wear[slot] = std::move(worn);
  g_coop_give[slot] = std::move(give);
  g_coop_dyes[slot] = std::move(dyes);
  g_coop_scars[slot] = std::move(scars);

  REXLOG_INFO("Refii guest-tick pump: HeroState v{} for player {}: {} worn, {} record(s), "
              "melee 0x{:08X} ranged 0x{:08X}, face 0x{:08X}, {} rank(s), looks {}, known {}, "
              "name '{}', dog 0x{:08X}, {} dyed worn item(s), {} scar(s)", version, player,
              g_coop_wear[slot].size(), g_coop_give[slot].size(), melee, ranged, face, ranks.size(),
              has_looks ? "yes" : "no", known ? "yes" : "no", record.name, record.dog_definition,
              g_coop_dyes[slot].size(), g_coop_scars[slot].size());
  return true;
}

/// THE ONE APPLY. Give the inventory, wear the outfit, dye what is worn, give the scars, draw
/// the weapons, then for player two the HeroSave block, ranks, body morphs and face — in that
/// order, because wear needs the records held, a dye needs the item worn, equip needs the
/// weapons held, and the stats walk needs the body to be the one they belong to. Once per (body, generation): every step is idempotent on its own, so a new
/// generation re-runs only what changed (a new outfit re-wears; the same weapons are not
/// re-drawn). Each step reports done or DEFERRED (a component the body does not have yet, a
/// record not held yet); the generation is latched only when every step is done, and every
/// step runs each tick until then. A body the guest rebuilds (level change, retype) is a new
/// entity and gets the standing generation again from the tick.
void CoopApplyHero(PPCContext& ctx, uint8_t* base, int player, uint32_t entity) {
  CoopHeroRecord& record = g_coop_record[player - 1];
  if (entity == 0u || record.generation == 0u) {
    return;
  }

  if (g_coop_applied_generation[entity] == record.generation) {
    return;
  }

  // Each step is guest work on a body; the heap walk after each names the one that breaks it.
  bool done = CoopGivePlayer(ctx, base, player, entity);
  HeapCheckAfter(base, player == 2 ? "CoopApplyHero give (player two)" : "CoopApplyHero give (player one)");
  done = CoopRedressPlayer(ctx, base, player, entity) && done;
  HeapCheckAfter(base, "CoopApplyHero redress");
  done = CoopDyePlayer(ctx, base, player, entity) && done;
  HeapCheckAfter(base, "CoopApplyHero dye");
  done = CoopScarPlayer(ctx, base, player, entity) && done;
  HeapCheckAfter(base, "CoopApplyHero scars");
  done = ApplyEquip(ctx, base, player, entity, record.melee, record.ranged) && done;
  HeapCheckAfter(base, "CoopApplyHero equip");
  if (player == 2) {
    done = ApplyStats(ctx, base, entity, record.stats) && done;
    HeapCheckAfter(base, "CoopApplyHero stats");
    done = ApplySkills(ctx, base, entity, record.skills) && done;
    HeapCheckAfter(base, "CoopApplyHero skills");
    done = ApplyBuild(ctx, base, entity, record.build) && done;
    HeapCheckAfter(base, "CoopApplyHero build");
    done = ApplyFace(ctx, base, entity, record.face) && done;
    HeapCheckAfter(base, "CoopApplyHero face");
  }

  if (!done) {
    return;  // a deferred step asks again on the next tick
  }

  g_coop_applied_generation[entity] = record.generation;
  g_herostate_body[player - 1] = entity;
  g_herostate_applied[player - 1].store(record.generation, std::memory_order_relaxed);
  refii_bridge_publish_herostate_applied(static_cast<uint32_t>(player), record.generation);
  REXLOG_INFO("Refii guest-tick pump: CoopApplyHero — generation (HeroState version) {} applied "
              "to player {} 0x{:08X}", record.generation, player, entity);
}

/// PLAYER TWO'S SAVE PICKER PAST THE SIXTH SAVE (kActionCoopPickSavePrefix /
/// kActionCoopPickCursorPrefix). The picker is the co-op screen's (id 24) handler, TU1
/// `sub_82438CF8` / disc `sub_82479F18`, with the saves' names at +404..+408, their 40 / disc 36-byte
/// headers at +452 and its cursor at +392 — the same offsets in both builds. The guest's own
/// vocabulary on it, past its gates `lbz +160` = 0 (0x82438d24 / disc 0x82479f44) and `lbz +304`
/// != 0 (0x82438d4c / disc 0x82479f6c):
///   - the cursor setter TU1 `sub_82439738` / disc `sub_8247A930` takes only
///     `0 <= N < min(count, 6)` (`addi r11,r11,-6` .. `cmpw r28,r9` / `bge`, 0x82439764..0x82439778,
///     disc 0x8247a95c..0x8247a970), so its cursor never reaches a seventh save;
///   - A (code 36) commits and closes: TU1 `lbz r9,0x18(r10)` on `*(+452) + 40 * cursor`, refusing an
///     unloadable save (0x82438d9c..0x82438dbc), then `bl sub_8243AEF8`; `bl sub_8243B390`
///     (0x82438dc4 / 0x82438dcc). Disc has no validity test: `bl sub_8247C1B0`; `bl sub_8247C648`
///     (0x82479fc0 / 0x82479fc8).
///   - The commit `sub_8243AEF8` / disc `sub_8247C1B0` reads only +392 — `lwz r10,0x188(r30)` /
///     `slwi r10,r10,2` / `add r31,r10,r11` on +404 (0x8243af10..0x8243af1c, disc 0x8247c1c8..
///     0x8247c1d4) — and loads that name with `lhSaveManager_LoadCoopHero` (0x8243af50 /
///     0x8247c208); it has no bound of its own.
///   - The close `sub_8243B390` / disc `sub_8247C648` restarts the card `table[+396 + +392]` on slide 2
///     (`lwz r9,0x18C` / `lwz r10,0x188` / `add r7,r9,r10`, 0x8243b3e4..0x8243b3f4; disc 0x8247c69c..
///     0x8247c6ac; the table is TU1 `dword_8349814C` / disc `unk_83498070`, seven words, `+396` 1..3),
///     the card the setter lifted (slide 1, 0x82439818), then hides the picker (`sub_8243B440(r3,0)`).
/// Nothing else reads +392 (the constructor writes it, 0x82434ef4; the opener and the handler's
/// left/right are the rest).
///
/// So a save the setter takes (N < min(count, 6)) runs the guest's path exactly: the setter when the
/// cursor is elsewhere, then the commit and the close. A later save is committed by the same commit
/// with +392 = N for that call, and +392 is put back on the card the setter last lifted before the
/// close runs: that card is the one the close must drop, and an index of 6 or more would read past
/// the seven-word table. The validity byte is honoured in both builds — an unloadable save is not
/// committed. `CoopPickCursor:N` is the setter alone, for N it takes.
bool DispatchCoopPick(PPCContext& ctx, uint8_t* base, const std::string& rest, bool commit) {
  constexpr uint32_t kCoopScreenId = 24u;
  constexpr uint32_t kRightAccepted = 160u;
  constexpr uint32_t kPickerShown = 304u;
  constexpr uint32_t kCursor = 392u;
  constexpr uint32_t kNamesBegin = 404u;
  constexpr uint32_t kNamesEnd = 408u;
  constexpr uint32_t kHeaders = 452u;
  constexpr uint32_t kHeaderValid = 24u;
  constexpr uint32_t kSetterCards = 6u;
  const char* verb = commit ? "CoopPickSave" : "CoopPickCursor";

  char* end = nullptr;
  const unsigned long parsed = std::strtoul(rest.c_str(), &end, 10);
  if (end == rest.c_str() || *end != '\0') {
    REXLOG_INFO("Refii guest-tick pump: {}:{} has no save index — dropped", verb, rest);
    return true;
  }
  const uint32_t save = static_cast<uint32_t>(parsed);

  const uint32_t screen = InGameScreenHandler(ctx, base, kCoopScreenId);
  if (screen == 0u || base[screen + kPickerShown] == 0u || base[screen + kRightAccepted] != 0u) {
    REXLOG_INFO("Refii guest-tick pump: {}:{} dropped — player two's save picker is not up", verb, save);
    return true;
  }
  const uint32_t names = LoadGuestU32(base, screen + kNamesBegin);
  const uint32_t count = (LoadGuestU32(base, screen + kNamesEnd) - names) / 4u;
  const uint32_t setter_cards = std::min(count, kSetterCards);
  const uint32_t cursor = LoadGuestU32(base, screen + kCursor);
  if (save >= count || (!commit && save >= setter_cards)) {
    REXLOG_INFO("Refii guest-tick pump: {}:{} dropped — {} save(s), the setter takes {}", verb, save,
                count, setter_cards);
    return true;
  }

  PPCContext work = ctx;
  work.r1.u32 = (ctx.r1.u32 - 0x100u) & ~0xFu;
  const auto call = [&](auto fn, uint32_t r4) {
    PPCContext c = work;
    c.r3.u32 = screen;
    c.r4.u32 = r4;
    fn(c, base);
    return c.r3.u32;
  };

  if (save < setter_cards && save != cursor) {
    call(rex_lhGuiCoopScreen_SetPickerCursor, save);
  }
  if (!commit) {
    REXLOG_INFO("Refii guest-tick pump: co-op picker cursor {} -> {} by the guest's setter", cursor, save);
    return true;
  }

  const uint32_t header = LoadGuestU32(base, screen + kHeaders) + (refii::guest::SaveRecordStride() * save);
  if (base[header + kHeaderValid] == 0u) {
    REXLOG_INFO("Refii guest-tick pump: CoopPickSave:{} refused — its header did not parse (+24 = 0)", save);
    return true;
  }

  const std::string name = refii::vfs::ReadGuestLhString(base, names + (save * 4u));
  if (save < setter_cards) {
    call(rex_lhGuiCoopScreen_CommitPickedSave, 0u);
  } else {
    StoreGuestU32(base, screen + kCursor, save);
    call(rex_lhGuiCoopScreen_CommitPickedSave, 0u);
    StoreGuestU32(base, screen + kCursor, cursor);
  }
  call(rex_lhGuiCoopScreen_ClosePicker, 0u);
  REXLOG_INFO("Refii guest-tick pump: co-op picker committed save {} '{}' of {} for player two{}", save, name,
              count, save < setter_cards ? "" : " (past the setter's six; cursor kept on its card)");
  return true;
}

/// THE CO-OP SCREEN'S HANDLER (id 24, disc 23), found the way the guest's openers reach the
/// screen: `QM = *(*dword_83496B08 + 0x1C)` (DisplayCoopScreen `lwz r11,dword_83496B08; lwz
/// r3,0x1C(r11)` 0x8232cc74..0x8232cc78 / disc `dword_834968A4` 0x8230f64c..0x8230f650) for whichever
/// GUI is published — the in-game one, or the front end's while a joiner arrives from the title —
/// then `lhGuiManager_FindScreenHandler(QM, 24)`. An object to call, never to read.
uint32_t CoopScreenHandler(PPCContext& ctx, uint8_t* base) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  const uint32_t gui = LoadGuestU32(base, kGuiObject);
  const uint32_t owner = plausible(gui) ? LoadGuestU32(base, gui + 0x1Cu) : 0u;
  if (!plausible(owner)) {
    return 0u;
  }
  PPCContext work = ctx;
  work.r3.u32 = owner;
  work.r4.u32 = refii::guest::ScreenId(24u);
  rex_lhGuiManager_FindScreenHandler(work, base);
  return plausible(work.r3.u32) ? work.r3.u32 : 0u;
}

/// `CoopAction:<code>:<player>` — one press on the co-op screen, for the player it is theirs:
/// the screen's own input slot vf+24, TU1 `sub_824389D0` / disc `sub_82479BF0` (one vtable each),
/// with r3 the screen, r4 the code, r5 the player. Its own gates stand: nothing while closing
/// (`lbz r11,0x10C(r3)` 0x824389d0 / 0x82479bf0), r5 1 to the left page `sub_82438A08` unless the
/// picker is up (`lbz r11,0x130(r3)` 0x824389f0), r5 2 to the right page `sub_82438CF8` /
/// `sub_82479F18`, any other player ignored (`cmpwi cr6,r5,2; bnelr` 0x824389e4). Each page first
/// runs its repeat filter (`sub_828818E8` / `sub_82841730` on +308 / +348): 28..31 pass a repeat
/// gate, 32..35 reset it, so every direction is posted as its press and its release. Codes:
/// 28..31 up / down / left / right, 32..35 their releases, 36 A, 37 B, 38 X, 39 Y. Update stays
/// the guest's: the commit, the sync packets, Finish and the close all run there.
bool DispatchCoopAction(PPCContext& ctx, uint8_t* base, uint32_t code, uint32_t player) {
  const uint32_t screen = CoopScreenHandler(ctx, base);
  if (screen == 0u) {
    REXLOG_INFO("Refii guest-tick pump: CoopAction:{}:{} dropped — no co-op screen handler", code, player);
    return true;
  }
  PPCContext work = ctx;
  work.r3.u32 = screen;
  work.r4.u32 = code;
  work.r5.u32 = player;
  rex_lhGuiCoopScreen_OnScreenAction(work, base);
  REXLOG_INFO("Refii guest-tick pump: CoopAction:{}:{} answered 0x{:08X}", code, player, screen);
  return true;
}

/// See kActionCoopHeroBodyPrefix.
bool DispatchCoopHeroBody(PPCContext& ctx, uint8_t* base, const std::string& which) {
  // The four hero bodies, from `scripts/Startup/DebugMenu.txt` — the readable index of spawnable
  // names, since GDB record names are FNV-1 hashes and appear nowhere as strings. The henchman
  // names are here too so a run can put the old body back without restarting.
  // `auto` takes the body from the co-op screen's own appearance pickbox, so pressing X on a
  // female henchman gives a female hero. The index lives at `coopMgr+260` (`sub_8233D090`
  // stores it at 0x8233D110) and maps the same way `sub_8233C508` reads it: 0-2 female,
  // 3-5 male, 6 male child, 7 female child.
  std::string choice = which;
  const char* henchman = nullptr;
  if (choice == "auto" || choice == "pickbox") {
    const auto step = [&](uint32_t at, uint32_t offset) -> uint32_t {
      return LooksLikeGuestPointer(at) ? LoadGuestU32(base, at + offset) : 0u;
    };

    const uint32_t coop = step(step(LoadGuestU32(base, refii::guest::Address(0x83496AB8u)), 12u), 140u);
    const uint32_t appearance =
        LooksLikeGuestPointer(coop) ? LoadGuestU32(base, coop + 260u) : 0u;

    if (choice == "pickbox") {
      // `sub_8233C508`'s own table, so going BACK lands on the henchman the pickbox is
      // actually showing rather than a fixed one.
      static const char* const kHenchmen[] = {
          "CreatureHeroHenchmanFemaleNeutral", "CreatureHeroHenchmanFemaleGood",
          "CreatureHeroHenchmanFemaleEvil",    "CreatureHeroHenchmanMaleNeutral",
          "CreatureHeroHenchmanMaleGood",      "CreatureHeroHenchmanMaleEvil",
          "CreatureHeroChild",                 "CreatureHeroFemaleChild",
      };

      henchman = kHenchmen[appearance < 8u ? appearance : 1u];
    } else {
      choice = appearance == 6u   ? "child"
               : appearance == 7u ? "femalechild"
               : appearance >= 3u ? "male"
                                  : "female";
    }
  }

  const char* creature =
      henchman != nullptr       ? henchman
      : choice == "male"        ? "CreatureHero"
      : choice == "female"      ? "CreatureHeroFemale"
      : choice == "child"       ? "CreatureHeroChild"
      : choice == "femalechild" ? "CreatureHeroFemaleChild"
      : choice == "henchman"    ? "CreatureHeroHenchmanFemaleGood"
                                : nullptr;
  if (creature == nullptr) {
    REXLOG_INFO("Refii guest-tick pump: CoopHeroBody:{} names no creature — dropped", which);
    return true;
  }

  // THE BIT IS THE MECHANISM; the call below is only a nudge.
  //
  // `sub_8233D090` is not a one-shot. `sub_82436548` re-runs it EVERY FRAME the co-op screen is
  // up (0x824365CC gates on `!screen+268 && screen+48 && screen+160`, then 0x824365DC →
  // `sub_82437168` → 0x8243720C), and `sub_823276D0` re-applies it from the manager's stored
  // index on level change and on save load. Retyping the entity ourselves would therefore be
  // undone within a frame on the screen, and again the moment play starts. What survives all of
  // that is answering `sub_8233C508` — the sole source of player two's creature name — which
  // `coop_player_hooks.cpp` overrides and this bit drives.
  const bool own = henchman == nullptr && choice != "henchman";
  if (own != (refii_bridge_two_is_hero() != 0)) {
    // A different body is coming for player two: nothing latched on the current one holds.
    CoopForgetBody(CoopPlayerTwoEntity(base));
  }
  RefiiCoopSetOwnHero(own ? 1 : 0);

  const uint32_t player = PlayerWithId(base, 2u);
  if (player == 0u) {
    REXLOG_INFO("Refii guest-tick pump: CoopHeroBody:{} — no player two in the manager", which);
    return true;
  }

  // `sub_82389FB8` locks the Player's weak_ptr FIRST (0x82389FC4) and only retypes when it
  // resolves to a live entity. Player two is created with no entity attached (`a8 = 0` at
  // 0x82316C00) and `sub_8233D090` attaches it later (0x8233D698), so this is a no-op until the
  // henchman has actually spawned — call it after the join completes, not during.
  // Scratch above the callee's argument-save area: frame+0x60 and up.
  const uint32_t sp = ctx.r1.u32;
  const uint32_t frame = (sp - 0x200u) & ~0xFu;
  const uint32_t text = frame + 0x60u;
  const uint32_t handle = frame + 0x1A0u;

  const std::size_t length = std::strlen(creature);
  for (std::size_t i = 0; i < length; ++i) {
    base[text + i] = static_cast<uint8_t>(creature[i]);
  }

  base[text + length] = 0;
  StoreGuestU32(base, handle, 0u);

  // The name is an LHString, not a char* — `sub_8233C508` builds the guest's own the same way.
  // r5 = -1 is the length `sub_822078A0` reads as strlen, and it must be set.
  PPCContext work = ctx;
  work.r1.u32 = frame;
  work.r3.u32 = handle;
  work.r4.u32 = text;
  work.r5.u32 = 0xFFFFFFFFu;
  rex_lhCharString_CtorFromCStr(work, base);

  work = ctx;
  work.r1.u32 = frame;
  work.r3.u32 = player;
  work.r4.u32 = handle;
  rex_lhPlayer_ChangeEntityType(work, base);

  work = ctx;
  work.r1.u32 = frame;
  work.r3.u32 = handle;
  rex_lhCharString_Dtor(work, base);

  REXLOG_INFO("Refii guest-tick pump: CoopHeroBody:{} — asked player two (0x{:08X}) to become "
              "'{}'", which, player, creature);
  return true;
}

/// See kActionRegisterPlayer.
bool DispatchRegisterPlayer(PPCContext& ctx, uint8_t* base) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  const uint32_t gui = LoadGuestU32(base, kGuiObject);
  const uint32_t player = PlayerWithId(base, 1u);
  if (!plausible(gui) || player == 0u) {
    REXLOG_INFO("Refii guest-tick pump: RegisterPlayer with no GUI / player — dropped");
    return true;
  }

  const uint32_t before = LoadGuestU8(base, gui + 0x65u);
  if (before != 0u) {
    REXLOG_INFO("Refii guest-tick pump: RegisterPlayer — {} already registered, nothing done", before);
    return true;
  }

  const PPCContext saved = ctx;
  ctx.r3.u32 = gui;
  ctx.r4.u32 = player;
  rex_lhGui_RegisterPlayer(ctx, base);
  ctx = saved;
  REXLOG_INFO("Refii guest-tick pump: RegisterPlayer — GUI+0x65 {} -> {} (player 0x{:08X})",
              before, LoadGuestU8(base, gui + 0x65u), player);
  return true;
}

/// A consumer record for the play-mode GameAction routines that read only `a1+12`, the player
/// (`lwz r3,0xC(r31)` and `lwz r11,0xC(r31)` in `sub_824044B8` / `sub_82404708`): 16 bytes on a
/// frame below the guest stack, r1 lowered past it, `fn` called with r3 = the record.
template <typename Fn>
void CallWithPlayerRecord(PPCContext& ctx, uint8_t* base, uint32_t player, Fn fn) {
  const PPCContext saved = ctx;
  const uint32_t frame = (ctx.r1.u32 - 0x80u) & ~0xFu;
  const uint32_t record = frame + 0x60u;
  StoreGuestU32(base, record + 0u, 0u);
  StoreGuestU32(base, record + 4u, 0u);
  StoreGuestU32(base, record + 8u, 0u);
  StoreGuestU32(base, record + 12u, player);
  ctx.r1.u32 = frame;
  ctx.r3.u32 = record;
  fn(ctx, base);
  ctx = saved;
}

/// RB's toggle byte, as GameActions 40 / 41 read it: the input map's `GUIQuickAccessMenuOnToggle`
/// (`lhInputMap_LoadFromGameData` 0x82882CF0 stores it at map+16), reached as
/// `*(*(*(*(dword_83496AB8)+0xC)+0x7C)+0x48)+0x10` — TU1 0x82404198..0x824041A8 (case 40) and
/// 0x824041F8.. (case 41), disc `dword_83496920` 0x823B53D8..0x823B53E8.
bool QuickWheelToggles(uint8_t* base) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  const uint32_t root = LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = plausible(root) ? LoadGuestU32(base, root + 0xCu) : 0u;
  const uint32_t input = plausible(game) ? LoadGuestU32(base, game + 0x7Cu) : 0u;
  const uint32_t map = plausible(input) ? LoadGuestU32(base, input + 0x48u) : 0u;
  return plausible(map) && LoadGuestU8(base, map + 0x10u) != 0u;
}

/// The expression wheel's three levers — see kActionQuickWheelPrefix.
///
/// QM is `*(GUI+28)`; the controller W is `*(*(GUI+32)+60)`; the player object is the one
/// `sub_824040B8` hands the same routines (`a1[3]`, the consumer's bound player), which is the
/// party's first player — `PlayerObject`.
bool DispatchQuickWheel(PPCContext& ctx, uint8_t* base, const std::string& lever) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  const bool two = lever.rfind("2:", 0) == 0;
  const std::string verb = two ? lever.substr(2) : lever;
  const uint32_t gui = LoadGuestU32(base, kGuiObject);
  const uint32_t quick = plausible(gui) ? LoadGuestU32(base, gui + 28u) : 0u;
  const uint32_t hud = plausible(gui) ? LoadGuestU32(base, gui + 32u) : 0u;
  const uint32_t wheel = plausible(hud) ? LoadGuestU32(base, hud + 60u) : 0u;
  const uint32_t player = two ? PlayerWithId(base, 2u) : LocalPlayer(base);
  if (!plausible(quick) || !plausible(wheel) || player == 0u) {
    REXLOG_INFO("Refii guest-tick pump: QuickWheel:{} with no quick menu / player — dropped", lever);
    return true;
  }

  // The open wheel's owner (W+24). A lever acts only on its own player's wheel; Accept's flag
  // is "the owner is player one" — 0 performs on player two (`sub_826AAD88`, 0x826aae1c).
  const uint32_t owner = LoadGuestU32(base, wheel + 24u);
  const bool open = LoadGuestU8(base, quick + refii::guest::GuiOwnerField(35u)) != 0u &&
                    (verb == "open" || owner == (two ? 2u : LoadGuestU32(base, player + 36u)));
  const PPCContext saved = ctx;
  if (verb == "open") {
    if (open) {
      return true;
    }

    ctx.r3.u32 = quick;
    ctx.r4.u32 = player;
    rex_lhGuiManager_CanPlayerOpenQuickMenu(ctx, base);
    const bool may = (ctx.r3.u32 & 0xFFu) != 0u;
    if (may) {
      ctx = saved;
      ctx.r3.u32 = quick;
      ctx.r4.u32 = player;
      rex_lhGuiManager_OpenExpressionWheel(ctx, base);
    }

    ctx = saved;
    REXLOG_INFO("Refii guest-tick pump: QuickWheel:open — {}",
                may ? (LoadGuestU8(base, quick + refii::guest::GuiOwnerField(35u)) != 0u ? "opened" : "refused by sub_8236F110")
                    : "refused by sub_8236F400 (hero busy or not in a walking mode)");
    return true;
  }

  // RB, GameAction 40 (press) and 41 (release) as `sub_824040B8` runs them (TU1 0x82404180..0x824041F4
  // and 0x824041F8..; disc 0x823B53C0..): 40 with the wheel up closes it only in toggle mode, with it
  // down opens it through CanPlayerOpenQuickMenu; 41 closes it only in hold mode. The wheel a lever
  // closes is its own player's (the other levers' rule).
  if (verb == "rb:press" || verb == "rb:release") {
    const bool toggles = QuickWheelToggles(base);
    const bool up = LoadGuestU8(base, quick + refii::guest::GuiOwnerField(35u)) != 0u;
    const bool own = up && owner == (two ? 2u : LoadGuestU32(base, player + 36u));
    const char* did = "nothing";
    if (verb == "rb:press" && !up) {
      ctx.r3.u32 = quick;
      ctx.r4.u32 = player;
      rex_lhGuiManager_CanPlayerOpenQuickMenu(ctx, base);
      const bool may = (ctx.r3.u32 & 0xFFu) != 0u;
      ctx = saved;
      if (may) {
        ctx.r3.u32 = quick;
        ctx.r4.u32 = player;
        rex_lhGuiManager_OpenExpressionWheel(ctx, base);
        ctx = saved;
        did = LoadGuestU8(base, quick + refii::guest::GuiOwnerField(35u)) != 0u ? "opened" : "refused by sub_8236F110";
      } else {
        did = "refused by sub_8236F400";
      }
    } else if (own && (verb == "rb:press") == toggles) {
      ctx.r3.u32 = quick;
      ctx.r4.u32 = 1u;
      rex_lhGuiManager_CloseExpressionWheel(ctx, base);
      ctx = saved;
      did = "closed";
    }
    REXLOG_INFO("Refii guest-tick pump: QuickWheel:{} — {} ({} mode)", lever, did, toggles ? "toggle" : "hold");
    return true;
  }

  if (verb == "close") {
    if (open) {
      ctx.r3.u32 = quick;
      ctx.r4.u32 = 1u;
      rex_lhGuiManager_CloseExpressionWheel(ctx, base);
      ctx = saved;
      REXLOG_INFO("Refii guest-tick pump: QuickWheel:close — closed");
    }

    return true;
  }

  if (verb == "select") {
    if (open) {
      // The A press's play consumer runs too, as GameAction 36 runs it beside the wheel's accept:
      // `sub_824044B8` / `sub_823B56F8` posts MESSAGE_EVENT_EXPRESSION_MENU_OPENED (32,
      // `messageeventenum.lua`) at the hero with the wheel up (`lbz QM+35` 0x824046D4 → `bl sub_82404708`
      // 0x824046E4; disc QM+34 0x823B5914 → 0x823B5924), which villagers' NoticeExpression
      // (`aibase.lua`, `behaviourbaby.lua`) read to keep watching. A joiner's A is not masked from
      // its guest, which runs it itself.
      if (!IsCoopJoiner(base)) {
        CallWithPlayerRecord(ctx, base, player, [](PPCContext& c, uint8_t* b) { rex_lhGuiInput_PlayAPress(c, b); });
      }
      ctx.r3.u32 = wheel;
      ctx.r4.u32 = owner == 1u ? 1u : 0u;
      rex_lhGuiExpressionWheel_Accept(ctx, base);
      ctx = saved;
      REXLOG_INFO("Refii guest-tick pump: QuickWheel:select — cursor {}/{} state {}",
                  static_cast<int32_t>(LoadGuestU32(base, wheel + 16u)),
                  static_cast<int32_t>(LoadGuestU32(base, wheel + 20u)),
                  LoadGuestU32(base, wheel));
    }

    return true;
  }

  if (verb == "back") {
    if (open) {
      ctx.r3.u32 = wheel;
      rex_lhGuiExpressionWheel_Back(ctx, base);
      ctx = saved;
      REXLOG_INFO("Refii guest-tick pump: QuickWheel:back");
    }

    return true;
  }

  if (verb == "release") {
    // `sub_826AA5D8` ends a HELD expression (state 2) and puts the wheel back on slide 4;
    // it never closes. Harmless when nothing is held.
    if (open) {
      ctx.r3.u32 = wheel;
      ctx.r4.u32 = player;
      rex_lhGuiExpressionWheel_EndHeldExpression(ctx, base);
      ctx = saved;
    }

    return true;
  }

  if (verb.rfind("point:", 0) == 0) {
    char* end = nullptr;
    const unsigned long sector = std::strtoul(verb.c_str() + 6, &end, 10);
    if (end == verb.c_str() + 6 || *end != '\0' || sector > 7u) {
      REXLOG_INFO("Refii guest-tick pump: QuickWheel:{} is not a sector 0..7 — dropped", verb);
      return true;
    }

    if (open && LoadGuestU32(base, wheel) == 1u) {
      // The unit vector `sub_826AA060` resolves to sector i: it negates y and compares
      // (x, -y) against (sin a, cos a), so x = sin a and y = -cos a land exactly on it.
      const double angle = 2.3561945 - (static_cast<double>(sector) * 0.7853982);
      const float x = static_cast<float>(std::sin(angle));
      const float y = static_cast<float>(-std::cos(angle));
      uint32_t xBits = 0;
      uint32_t yBits = 0;
      std::memcpy(&xBits, &x, sizeof(xBits));
      std::memcpy(&yBits, &y, sizeof(yBits));
      ctx.r3.u32 = wheel;
      // One 64-bit r4: `std r4` at 0x826AA078 then `lfs` from its first and second word —
      // x is the HIGH word, y the LOW. `Register` is a union, so `.u32` would set only y.
      ctx.r4.u64 = (static_cast<uint64_t>(xBits) << 32) | yBits;
      rex_lhGuiExpressionWheel_SetCursorFromStick(ctx, base);
      ctx = saved;
      // The left stick's play consumer `sub_82404C50` / `sub_823B5E90` posts message 32 for every
      // stick record while QM+0x28 is 1 or 5 and the wheel is up (0x82404C7C..0x82404D14; disc
      // 0x823B5EBC..0x823B5F54); the stick is the lever's under the wheel, so the post is made here.
      const uint32_t mode = LoadGuestU32(base, quick + 0x28u);
      if (!IsCoopJoiner(base) && (mode == 1u || mode == 5u)) {
        CallWithPlayerRecord(ctx, base, player,
                             [](PPCContext& c, uint8_t* b) { rex_lhGuiInput_PostExpressionMenuOpened(c, b); });
      }
    }

    return true;
  }

  REXLOG_INFO("Refii guest-tick pump: QuickWheel:{} is not a verb — dropped", verb);
  return true;
}

/// The spells menu's two levers — see kActionSpellsMenuPrefix.
///
/// QM is `*(GUI+28)`, the SAME object the expression wheel rides; the player is `PlayerObject`,
/// which is the `a1[3]` that `sub_824040B8` hands these routines. Both derive the player INDEX
/// themselves from `*(player+36)`, so the lever passes none.
///
/// EVERY GATE STAYS THE GUEST'S, and all of them are inside `sub_8236F788`
/// (0x8236f79c..0x8236f7fc): the already-open flag at QM+36 (player 1) / QM+37 (player 2),
/// `sub_826B4448`, and `sub_8236F400` (hero alive, player mode 1/5/20/21/22). It returns 1
/// when it opened and 0 when it refused.
///
/// THE FIVE-WIDE AXIS IS THE POWER (CHARGE) LEVEL, NOT A RING OR A D-PAD SLOT, and
/// `sub_826B4448` sweeps the EIGHT SPELL TYPES at power level 0 only — its cursor starts at
/// `mgr+196` with no level term (0x826b4710) and strides 5 (0x826b473c), needing a type that
/// is both AWARDED (`mgr+156+5*type`) and PURCHASED (`mgr+196+5*type`), and it wants TWO
/// (`cmpwi r9, 1 / bgt`, 0x826b4730). So one will at level 0 is a correct refusal: there is
/// nothing to assign between. That is the gate the expression wheel does not have.
///
/// A REFUSAL HAS SEVEN CAUSES INSIDE `sub_826B4448` ALONE (no hero handle, entity flag 0x40,
/// component 118 absent, component 56 absent, `mgr+617` clear, `sub_8253E220`, then the
/// count), plus the already-open flag and `sub_8236F400`'s own four. The log below must NOT
/// name one of them: it cannot tell them apart from here.
bool DispatchSpellsMenu(PPCContext& ctx, uint8_t* base, const std::string& lever) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  const bool two = lever.rfind("2:", 0) == 0;
  const std::string verb = two ? lever.substr(2) : lever;
  const uint32_t gui = LoadGuestU32(base, kGuiObject);
  const uint32_t quick = plausible(gui) ? LoadGuestU32(base, gui + 28u) : 0u;
  const uint32_t player = two ? PlayerWithId(base, 2u) : LocalPlayer(base);
  if (!plausible(quick) || player == 0u) {
    REXLOG_INFO("Refii guest-tick pump: SpellsMenu:{} with no quick menu / player — dropped",
                lever);
    return true;
  }

  const uint32_t which = LoadGuestU32(base, player + 36u);
  const bool open = LoadGuestU8(base, quick + refii::guest::GuiOwnerField(which == 2u ? 37u : 36u)) != 0u;
  const PPCContext saved = ctx;

  // THE PUMP MAY ONLY CLOSE WHAT THE PUMP OPENED — this is a control-loss bug, not tidiness.
  //
  // `sub_8236F968` DISARMS ITSELF BEFORE IT DOES THE WORK: it clears QM+36/+37 at 0x8236f9b8
  // unconditionally, and only then pops the id-5 `GuiBlock` consumer at 0x8236fa00, keyed on
  // `*(a2 + 36)` — the player object WE pass. Miss that pop and the consumer stays on the
  // player's stack for ever, because every other closer is gated on the flag just cleared
  // (`sub_824040B8` case 0x2B at 0x824042e8, `sub_8236EB68` at 0x8236ec88 / 0x8236ecb4). One
  // missed pop is unrecoverable.
  //
  // A stuck id-5 is exactly "opened the blade, closed it, lost all control": `sub_823D93E8`
  // rebuilds the action map from the TOP consumer's id, and the id-5 arm (0x823D96D0)
  // registers only 5, 9, 11, 1, 12 and 10 — movement and camera (0, 2, 6, 3, 4, 7) are simply
  // absent.
  //
  // AND THE GUEST RUNS 42/43 ITSELF. `sub_8236F788` has exactly ONE code caller in the image,
  // 0x82404290 inside the GameAction dispatcher, and the only non-zero writers of QM+36/+37
  // are its own stores at 0x8236f814/0x8236f81c — yet the flag goes set while this lever logs
  // nothing but refusals. So the two mechanisms race, and the host's must never close a blade
  // the guest opened.
  static bool pump_opened_by_player[2] = {false, false};
  bool& pump_opened = pump_opened_by_player[which == 2u ? 1 : 0];

  if (verb == "open") {
    if (open) {
      // Already up, and not by us: the guest's case 0x2A did it, so its own 0x2B owns the
      // close and the pop that goes with it.
      pump_opened = false;
      return true;
    }

    ctx.r3.u32 = quick;
    ctx.r4.u32 = player;
    rex_lhGuiManager_OpenSpellsMenu(ctx, base);
    const bool opened = (ctx.r3.u32 & 0xFFu) != 0u;
    ctx = saved;
    pump_opened = opened;
    REXLOG_INFO("Refii guest-tick pump: SpellsMenu:{} — {}", lever,
                opened ? "opened"
                       : "refused by sub_8236F788 — one of its own gates said no; the "
                         "commonest is sub_826B4448 wanting TWO spell types awarded AND "
                         "purchased at power level 0, but this cannot tell which fired");
    return true;
  }

  if (verb == "close") {
    if (!open) {
      pump_opened = false;
      return true;
    }

    if (!pump_opened) {
      REXLOG_INFO("Refii guest-tick pump: SpellsMenu:close — the GUEST opened this one, so its "
                  "own GameAction 43 owns the close; closing it here would clear the flag at "
                  "0x8236f9b8 and orphan the id-5 GuiBlock, which costs the player movement "
                  "and camera for the rest of the session");
      return true;
    }

    ctx.r3.u32 = quick;
    ctx.r4.u32 = player;
    rex_lhGuiManager_CloseSpellsMenu(ctx, base);
    ctx = saved;
    pump_opened = false;
    REXLOG_INFO("Refii guest-tick pump: SpellsMenu:{} — closed", lever);
    return true;
  }

  REXLOG_INFO("Refii guest-tick pump: SpellsMenu:{} is not a verb — dropped", lever);
  return true;
}

/// The quick blade's lever — see kActionSpellBladePrefix.
///
/// `sub_826B5430(r3 = widget, r4 = code, r5 = player, r6 = screen)`. r5 is never read; r6 is
/// read only by case 36's `sub_826B4EF8(a1, a4)`. Both are passed as the guest passes them
/// (0x824052a0 / 0x8240526c) rather than zeroed, so the call is the guest's call.
bool DispatchSpellBlade(PPCContext& ctx, uint8_t* base, const std::string& lever) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  const bool second = lever.rfind("2:", 0) == 0;
  const std::string verb = second ? lever.substr(2) : lever;

  uint32_t code = 0u;
  if (verb == "up") {
    code = 28u;  // `++row` toward the higher power levels (0x826b54a8)
  } else if (verb == "down") {
    code = 29u;  // `--row` (0x826b5538)
  } else if (verb == "left") {
    code = 30u;  // `sub_826B5240`: previous offered will, committed at once
  } else if (verb == "right") {
    code = 31u;  // `sub_826B5330`: next offered will, committed at once
  } else if (verb == "release") {
    code = 32u;  // any of 32..35 resets the repeat latch (0x82881a88)
  } else if (verb == "copydown") {
    code = 36u;  // `sub_826B4EF8`: copy this level's will down into every later level offering it
  } else if (verb == "stick") {
    static bool said = false;
    if (!said) {
      said = true;
      REXLOG_INFO("Refii guest-tick pump: SpellBlade:stick is dead — code 26 resolves to nothing "
                  "while widget+165 is 0, which sub_826B4358 leaves it at; post up/down/left/right");
    }
    return true;
  } else {
    REXLOG_INFO("Refii guest-tick pump: SpellBlade:{} is not a verb — dropped", verb);
    return true;
  }

  const uint32_t gui = LoadGuestU32(base, kGuiObject);
  const uint32_t quick = plausible(gui) ? LoadGuestU32(base, gui + 28u) : 0u;
  const uint32_t hud = plausible(gui) ? LoadGuestU32(base, gui + 32u) : 0u;
  const uint32_t player = second ? PlayerWithId(base, 2u) : LocalPlayer(base);

  // SILENT REFUSALS THROUGHOUT: `stick` is posted every frame a trigger is held, so a log line
  // on the ordinary "nothing to do" answer would bury the run.
  if (!plausible(quick) || !plausible(hud) || player == 0u) {
    return true;
  }

  // The aim/expression latch wins outright — `sub_82405190` tests it FIRST (0x824051b0) and
  // never reaches the blade while it is set. Same order here, or a held trigger would drive the
  // expression ring and the blade at once.
  if (LoadGuestU8(base, quick + refii::guest::GuiOwnerField(35u)) != 0u) {
    return true;
  }

  const bool two = LoadGuestU32(base, player + 36u) == 2u;
  if (LoadGuestU8(base, quick + refii::guest::GuiOwnerField(two ? 37u : 36u)) == 0u) {
    return true;  // closed: widget+412 and the cell tables are uninitialised heap
  }

  const uint32_t widget = LoadGuestU32(base, hud + (two ? 96u : 92u));
  if (!plausible(widget)) {
    return true;
  }

  const uint32_t row_before = LoadGuestU32(base, widget + 176u);
  const uint32_t cell_before = LoadGuestU32(base, widget + 200u + (row_before * 4u));

  const PPCContext saved = ctx;
  ctx.r3.u32 = widget;
  ctx.r4.u32 = code;
  ctx.r5.u32 = two ? 2u : 1u;
  ctx.r6.u32 = LoadGuestU32(base, hud + 4u);
  rex_lhGuiWillAssignWidget_OnScreenAction(ctx, base);
  ctx = saved;

  const uint32_t row_after = LoadGuestU32(base, widget + 176u);
  const uint32_t cell_after = LoadGuestU32(base, widget + 200u + (row_after * 4u));
  if (row_after != row_before || cell_after != cell_before) {
    // ONLY ON A CHANGE. Every other frame of a held stick is the guest's own repeat throttle
    // saying no, which is the normal answer and not worth a line.
    REXLOG_INFO("Refii guest-tick pump: SpellBlade:{} — row {} -> {}, cell {} -> {} "
                "(maxRow {}, analog byte {})",
                verb, row_before, row_after, cell_before, cell_after,
                static_cast<int32_t>(LoadGuestU32(base, widget + 412u)),
                LoadGuestU8(base, widget + 165u));
  }

  return true;
}

/// Runs the current screen's own event handler on a host-built event — what the guest's own pump
/// `sub_82370450` does with an event `GUI:PullEvent()` returned: builds the 12-byte
/// `{LHString name, int32 type, int32 id}` and calls the screen's `vtable+20` (0x823705E0).
/// Everything downstream (the arming check at `screen+198`, the purchase, the message box) is the
/// guest's own code. The slot is READ from the live object's vtable and compared against the
/// verified handlers; false when the screen is not one this build can call.
bool DispatchMenuEventDirect(PPCContext& ctx, uint8_t* base, const std::string& name,
                             uint32_t type, uint32_t id) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  // A BOX THAT IS UP REFUSES THE EVENT, as the guest's own gate would. `sub_8236FA68`, the box
  // opener, acts only when the mode reads idle and otherwise does nothing at all, silently
  // (0x8236fa90), and every screen handler (`sub_826C2660` and its siblings) returns before
  // reading the event unless the box id is zero. The box is drawn and answered by its own
  // surface; the event is not forced through, which orphaned a box opened between Godot's sample
  // and this tick. Mode and id are the openers' own statements.
  {
    uint32_t mode = 1u;
    uint32_t modal = 0u;
    if (refii_bridge_stated_pause(&mode, &modal, nullptr) != 0u && mode != 1u) {
      REXLOG_INFO("Refii guest-tick pump: '{}' not committed — a box is up (mode {}, id {})",
                  name, mode, modal);
      return true;
    }
  }

  uint32_t screen_id = 0;
  if (!StatedInGameScreen(&screen_id)) {
    return false;
  }

  const uint32_t handler = InGameScreenHandler(ctx, base, screen_id);
  if (handler == 0u) {
    return false;
  }

  const uint32_t vtable = LoadGuestU32(base, handler);
  if (!plausible(vtable)) {
    return false;
  }

  // The slot is READ from the live object and matched against the handlers this build can
  // reach (`0x820CF4BC+20` is `0x826C2660`, `0x820CFEB0+20` is `0x826D1800`, and so on). The
  // recompiler emits no indirect-call path, so `(*(*(u32*)screen + 20))(screen, &event)` has to
  // become a switch; keying on the slot rather than the screen id keeps the object in charge.
  //
  // `sub_826B65F0` is ONE folded body serving three vtables (26, 27 and co-op): two scroll sounds
  // for EventTypes 140 and 141 and nothing else, ported empty (gui_screen_stub_shared.cpp).
  const uint32_t slot = LoadGuestU32(base, vtable + 20u);

  // Furniture/decoration (26/27) and options (18) do not act on the event at all: their choice
  // is made in the input handler's 36 arm from the highlighted row (`sub_826B6A18`,
  // `sub_826B7550`, `sub_826DA228` @0x826da3ec). The host commits 26 with
  // `Furniture:<p>:upgrade:<level>` (DispatchFurnitureAction) and 27 with
  // `Decoration:<p>:mount:<ticket>` (DispatchDecoration); an event pushed at them answers nothing. Real estate's rows (28) are committed by their event
  // alone, `RealEstate:<p>:event:<type>` (DispatchRealEstateEvent).

  switch (refii::guest::ToTu1(slot)) {
    case 0x826C2660u: case 0x826A26E8u: case 0x826D1800u: case 0x826D4D58u:
    case 0x826D9620u: case 0x826DA038u: case 0x826A92D8u: case 0x826AE5C0u:
    case 0x826C6110u: case 0x826C45C8u: case 0x826B65F0u:
    case 0x826B2210u:  // 28 real estate — `*(0x820CEA58+20)`
      break;
    default:
      REXLOG_INFO("Refii guest-tick pump: screen {} dispatches events to 0x{:08X}, which is not "
                  "a handler this build can call directly — '{}' not committed",
                  screen_id, slot, name);
      return false;
  }

  // Claimed by lowering r1 so the callee's frame cannot land on the struct it is handed.
  const uint32_t sp = ctx.r1.u32;
  const uint32_t frame = (sp - 0x200u) & ~0xFu;
  const uint32_t text = frame + 0x20u;
  const uint32_t event = frame + 0x1A0u;

  constexpr uint32_t kMaxName = 0x17Fu;
  if (name.size() + 1u > kMaxName) {
    return false;
  }

  for (std::size_t i = 0; i < name.size(); ++i) {
    base[text + i] = static_cast<uint8_t>(name[i]);
  }

  base[text + name.size()] = 0;
  StoreGuestU32(base, event + 0u, 0u);

  PPCContext work = ctx;
  work.r1.u32 = frame;
  work.r3.u32 = event;
  work.r4.u32 = text;
  work.r5.u32 = 0xFFFFFFFFu;  // "NUL-terminated" — the length the guest passes at 0x8246dab8
  rex_lhCharString_CtorFromCStr(work, base);

  // After the constructor: it owns `+0` only.
  StoreGuestU32(base, event + 4u, type);
  StoreGuestU32(base, event + 8u, id);

  REXLOG_INFO("Refii guest-tick pump: dispatching '{}' (type {}, id 0x{:08X}) straight to "
              "screen {}'s handler 0x{:08X}", name, type, id, screen_id, slot);

  work = ctx;
  work.r1.u32 = frame;
  work.r3.u32 = handler;
  work.r4.u32 = event;
  switch (refii::guest::ToTu1(slot)) {
    case 0x826C2660u: rex_lhGuiAbilitiesScreen_OnMenuEvent(work, base); break;
    case 0x826A26E8u: rex_lhGuiPauseRootScreen_OnMenuEvent(work, base); break;
    case 0x826D1800u: rex_lhGuiItemsScreen_OnMenuEvent(work, base); break;
    case 0x826D4D58u: rex_lhGuiClothingScreen_OnMenuEvent(work, base); break;
    case 0x826D9620u: rex_lhGuiLogbookScreen_OnMenuEvent(work, base); break;
    case 0x826DA038u: rex_lhGuiOptionsScreen_OnMenuEvent(work, base); break;
    case 0x826A92D8u: rex_lhGuiCrimeScreen_OnMenuEvent(work, base); break;
    case 0x826AE5C0u: rex_lhGuiShopScreen_OnMenuEvent(work, base); break;
    case 0x826C6110u: rex_lhGuiWorldMapScreen_OnMenuEvent(work, base); break;
    case 0x826C45C8u: rex_lhGuiGiftGivingScreen_OnMenuEvent(work, base); break;
    case 0x826B65F0u: rex_lhGuiScreen_OnMenuEventNavSoundsOnly(work, base); break;
    case 0x826B2210u: rex_lhGuiRealEstateScreen_OnMenuEvent(work, base); break;

    // NEVER SILENTLY. This default was commented "unreachable: the switch above already
    // refused anything else" and was not: the admission switch and this one are two hand-kept
    // lists of the same slots, and 0x826B2210 was added to that one alone. Screen 28 was
    // admitted, fell through here, and did nothing — while the two REXLOG lines bracketing
    // this switch, both OUTSIDE it, reported a dispatch and a return. Thirteen Buy presses
    // read as thirteen successful commits that raised no box.
    default:
      REXLOG_INFO("Refii guest-tick pump: screen {}'s handler 0x{:08X} is admitted but has no "
                  "call in the dispatch switch — '{}' was NOT delivered",
                  screen_id, slot, name);
      break;
  }

  REXLOG_INFO("Refii guest-tick pump: '{}' RETURNED from the screen handler", name);

  work = ctx;
  work.r1.u32 = frame;
  work.r3.u32 = event;
  rex_lhCharString_Dtor(work, base);
  return true;
}

/// The hero entity, or 0 — the same walk the host bridge's `ResolvePlayerHero` runs, which the
/// renown, orb and quest-log reads prove live every frame. The old chain here skipped the
/// GAME-ROOT DEREF — `0x83496AB8` holds a POINTER, `*(root)+12` is the game — and read twelve
/// bytes past the global instead, so every sampler gated on it published nothing (a run named
/// the gate: "quest travel … refused at hero").
uint32_t HeroEntity(uint8_t* base) {
  const uint32_t kGameRoot = refii::guest::Address(0x83496AB8u);
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  const uint32_t root = LoadGuestU32(base, kGameRoot);
  const uint32_t game = plausible(root) ? LoadGuestU32(base, root + 12u) : 0u;
  if (!plausible(game)) {
    return 0u;
  }

  // The manager itself (`g_XrnmNetContext`); the game object's copy at +0x8C is a fallback —
  // a title-joined client has that copy empty in game.
  uint32_t session = LoadGuestU32(base, refii::guest::Address(0x83497490u));
  if (!plausible(session)) {
    session = LoadGuestU32(base, game + 0x8Cu);
  }
  if (!plausible(session)) {
    return 0u;
  }

  const uint32_t player_key =
      MenusActForPlayerTwo(base) ||
              !(LoadGuestU8(base, session + 0x34u) != 0u && LoadGuestU8(base, session + 0x35u) != 0u)
          ? 2u
          : 1u;

  const uint32_t player_map = LoadGuestU32(base, game + 0x9Cu);
  if (!plausible(player_map)) {
    return 0u;
  }

  uint32_t entry = LoadGuestU32(base, player_map + 8u);
  const uint32_t entries_end = LoadGuestU32(base, player_map + 12u);
  if (!plausible(entry) || entries_end < entry || (entries_end - entry) > 12u * 64u) {
    return 0u;
  }

  // Prefer the role's slot, fall back to whichever player record exists — see the bridge's
  // ResolvePlayerHero for why a fixed key 2 read nothing on a joiner before it joined.
  uint32_t record = 0u;
  uint32_t other = 0u;
  for (; entry < entries_end; entry += 12u) {
    const uint32_t key = LoadGuestU32(base, entry);
    if (key == player_key) {
      record = LoadGuestU32(base, entry + 4u);
      break;
    }
    if ((key == 1u || key == 2u) && other == 0u) {
      other = LoadGuestU32(base, entry + 4u);
    }
  }
  if (record == 0u) {
    record = other;
  }

  if (!plausible(record)) {
    return 0u;
  }

  const uint32_t control = LoadGuestU32(base, record + 16u);
  const uint32_t hero = plausible(control) ? LoadGuestU32(base, control) : 0u;
  return plausible(hero) ? hero : 0u;
}

/// The pause root's owner back to player one — the tail of the drop-out answer `sub_826A6E70`
/// (0x826A6FA4..0x826A6FCC): root = `FindScreenHandler(QM, 0)`, root vf+32
/// with 0, which on the root's vtable (TU1 0x820CDE18 / disc 0x820B06E8) is the folded setter
/// 0x82EC34C8 / 0x82348CA8 (`stw r4,4(r3)`). Called only when the root carries that vtable.
void ResetPauseOwner(PPCContext& ctx, uint8_t* base, const PPCContext& saved, uint32_t frame) {
  const uint32_t root = InGameScreenHandler(ctx, base, 0u);
  ctx = saved;
  if (root == 0u || LoadGuestU32(base, root) != refii::guest::Address(0x820CDE18u)) {
    REXLOG_INFO("Refii guest-tick pump: CoopLeave — no pause root to put back to player one");
    return;
  }
  CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x82EC34C8u), root, 0u);
  REXLOG_INFO("Refii guest-tick pump: CoopLeave — pause root owner {} (player one)",
              LoadGuestU32(base, root + 4u));
}

/// "ButtonBox:<n>" and "AcceptHold[:4]" — the answer of the showing +0x20-queue box, as its own
/// raw input slot gives it: message 34 subcode 101 with the answer, `sub_823D0A10(_, 34, n)` (TU1
/// 0x823D0A10 / disc 0x827DE838: `li r4,0x22` 0x823D0A44, `li r8,0x65` 0x823D0A2C, `mr r9,r5`
/// 0x823D0A28, then `sub_82279B80`). Neither slot closes its box; the box stays up for the script.
///   - button box (vtable 0x820028F4 / disc 0x82002914), slot +16 `sub_8299EEF0` / `sub_828426D8`:
///     on a release (`+0x2C == 0xF`) of A/B/X/Y (`+8` 0x0B..0x0E) it posts n = 1/2/3/4
///     (0x8299EF00 `li r5,1`, 0x8299EF58 `li r5,2`, 0x8299EF78 `li r5,3`, 0x8299EF98 `li r5,4`);
///   - hold-to-accept card (vtable 0x820EBF1C / disc 0x820DCE84), slot +16 `sub_8299E8B0` /
///     `sub_82842030`: only while `+0x24` is set (0x8299E8CC); A posts 1 (`sub_8299E9C8(box, rec,
///     0xB, 1)` 0x8299E974, or the fill's end in Update 0x8299E88C), Y posts 4 only while `+0x25` is
///     set (0x8299E988..0x8299E9A4).
bool DispatchButtonBoxAnswer(PPCContext& ctx, uint8_t* base, uint32_t answer, bool hold) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  const uint32_t gui = LoadGuestU32(base, kGuiObject);
  const uint32_t manager = plausible(gui) ? LoadGuestU32(base, gui + 28u) : 0u;
  const uint32_t owner = plausible(manager) ? LoadGuestU32(base, manager + 52u) : 0u;
  const uint32_t box = plausible(owner)
                           ? refii::vfs::ShowingDisplayBox(
                                 owner, [base](uint32_t at) { return LoadGuestU32(base, at); })
                           : 0u;
  const uint32_t want = refii::guest::Address(hold ? 0x820EBF1Cu : 0x820028F4u);
  if (!plausible(box) || LoadGuestU32(base, box) != want) {
    REXLOG_INFO("Refii guest-tick pump: {}:{} — the box showing (0x{:08X}) is not a {} — dropped",
                hold ? "AcceptHold" : "ButtonBox", answer, box, hold ? "hold-to-accept card" : "button box");
    return true;
  }
  if (hold && (LoadGuestU8(base, box + 0x24u) == 0u ||
               (answer == 4u && LoadGuestU8(base, box + 0x25u) == 0u))) {
    REXLOG_INFO("Refii guest-tick pump: AcceptHold:{} — the card at 0x{:08X} does not take it "
                "(+0x24 {}, +0x25 {}) — dropped",
                answer, box, LoadGuestU8(base, box + 0x24u), LoadGuestU8(base, box + 0x25u));
    return true;
  }

  const PPCContext saved = ctx;
  const uint32_t frame = (saved.r1.u32 - 0x200u) & ~0xFu;
  CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x823D0A10u), box, 34u, answer);
  REXLOG_INFO("Refii guest-tick pump: {}:{} — message 34 posted with {} for box 0x{:08X}",
              hold ? "AcceptHold" : "ButtonBox", answer, answer, box);
  return true;
}

/// PLAYER TWO LEAVES, as the guest's own answer to its drop-out boxes does (`sub_826A6E70` on
/// "yes", 0x826A6EB8..0x826A6FA0), without the box (Godot asked):
///   - an online joiner disconnects: `sub_8233E668(net, 1)` — HandleLostConnection saves the
///     joiner's HeroSave (`sub_82445398`, 0x8232DEC0) and returns to the front end;
///   - local player two (game+0x11C == 2), after `sub_8236ED98(gui, 1)` closes the pause: with a
///     save named at SaveMgr+4 (box 27) `sub_82445680(SaveMgr, 1, 0, 0)` writes their HeroSave, the
///     name is cleared and SaveMgr+2024 = -1; without one (box 26) `sub_826A6FD8` / `sub_826A73D0`
///     merge their experience and gold into player one (neither reads its argument); then
///     `sub_8233C2A0(net, 0, -1)` drops them (`sub_823168D8` destroys the entity and erases the
///     slot).
/// Both arms end as the guest's does, with the pause root's owner put back to player one:
/// `FindScreenHandler(QM, 0)` (0x826A6FB0) then the root's vf+32 with 0 (0x826A6FC0..0x826A6FCC),
/// the folded setter `stw r4,4(r3); blr` (TU1 0x82EC34C8 / disc 0x82348CA8).
bool DispatchCoopLeave(PPCContext& ctx, uint8_t* base) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  const uint32_t root = LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = plausible(root) ? LoadGuestU32(base, root + 12u) : 0u;
  // The manager itself (g_XrnmNetContext); the game object's copy at +140 can be empty.
  uint32_t net = LoadGuestU32(base, refii::guest::Address(0x83497490u));
  if (!plausible(net) && plausible(game)) {
    net = LoadGuestU32(base, game + 140u);
  }
  const uint32_t manager = LoadGuestU32(base, refii::guest::Address(0x83496B08u));
  const uint32_t gui = plausible(manager) ? LoadGuestU32(base, manager + 28u) : 0u;
  if (!plausible(game) || !plausible(net) || !plausible(gui)) {
    REXLOG_INFO("Refii guest-tick pump: CoopLeave — no game, session or GUI");
    return true;
  }

  const PPCContext saved = ctx;
  const uint32_t frame = (saved.r1.u32 - 0x200u) & ~0xFu;
  const bool joiner = LoadGuestU8(base, net + 52u) != 0u && LoadGuestU8(base, net + 54u) != 0u;
  if (joiner) {
    // The drop-out box's own answer for an online joiner (`sub_826A6E70`, 0x826A6ED8..0x826A6F20):
    // `sub_8233E668(net, 1)`, the quiet leave, then the pause closed. NOT box 44's answer 36
    // (`sub_8236E458`), which follows the leave with `sub_822EA848` — XboxLive's JoinSession on the
    // session it saved (`sub_822EA738`, "CXboxLive::JoinSession called") — and so walked the
    // joiner straight back into the session it had just left (2026-10-20 14:36:50).
    CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x8233E668u), net, 1u);
    CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x8236ED98u), gui, 1u);
    ResetPauseOwner(ctx, base, saved, frame);
    ctx = saved;
    REXLOG_INFO("Refii guest-tick pump: CoopLeave — the joiner left the session (its hero saved)");
    return true;
  }
  if (LoadGuestU32(base, game + 0x11Cu) != 2u) {
    // ONLINE HOST: the same quiet leave drops the link to the joiner (a mods refusal, the pause's
    // Leave). With no peer and no join in flight it does nothing.
    CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x8233E668u), net, 1u);
    REXLOG_INFO("Refii guest-tick pump: CoopLeave — the host left the session (the joiner dropped)");
    return true;
  }

  CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x8236ED98u), gui, 1u);
  const uint32_t saves = LoadGuestU32(base, game + 148u);
  const uint32_t name = plausible(saves) ? LoadGuestU32(base, saves + 4u) : 0u;
  const bool has_save = plausible(name) && static_cast<int32_t>(LoadGuestU32(base, name + 4u)) > 0;
  if (has_save) {
    PPCContext four = saved;
    four.r6.u64 = 0u;
    CallGuestAt(ctx, base, four, frame, refii::guest::Address(0x82445680u), saves, 1u, 0u);
    CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x822651A0u), saves + 4u,
                refii::guest::Address(0x834A5E78u));
    StoreGuestU32(base, saves + 2024u, 0xFFFFFFFFu);
  } else {
    CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x826A6FD8u), 0u);
    CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x826A73D0u), 0u);
  }
  CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x8233C2A0u), net, 0u, 0xFFFFFFFFu);
  ResetPauseOwner(ctx, base, saved, frame);
  REXLOG_INFO("Refii guest-tick pump: CoopLeave — local player two dropped out ({}; mode now {})",
              has_save ? "their hero saved" : "no save: progress merged into player one",
              LoadGuestU32(base, game + 0x11Cu));
  return true;
}

/// THE ONLINE HOST KICKS PLAYER TWO, as the guest's own "Kick" box does on "yes" (`sub_826A2950`
/// case 0x1C, 0x826A2A04..0x826A2A44): `sub_8233AC68(net)` queues message 0x121 (the joiner's
/// "kicked" flag, net+321 → GUI_SCREEN_COOP_KICKED), `sub_822C3F50(net, 1)` and
/// `sub_82182C30(net, 1)` flush the reliable and unreliable lists, and the pause root's countdown
/// is armed, h+280 = 5 (`stw r9, 0x118(r30)` 0x826A2A3C / disc 0x8240EDF4). The root's ported Update counts it down and at 0 leaves the
/// session and the book (`LeaveSession(net, 0)` + `ExitScreen(QM, 1)`, 0x826A26A0 / 0x826A26B0); its
/// OnExit leaves the session at once if the book closes first (0x826A2A90). Only with the root up.
bool DispatchCoopKick(PPCContext& ctx, uint8_t* base) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  const uint32_t root = LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = plausible(root) ? LoadGuestU32(base, root + 12u) : 0u;
  const uint32_t net = plausible(game) ? LoadGuestU32(base, game + 140u) : 0u;
  if (!plausible(net) || LoadGuestU32(base, game + 0x11Cu) != 1u ||
      LoadGuestU8(base, net + 52u) == 0u || LoadGuestU8(base, net + 53u) == 0u ||
      LoadGuestU32(base, net + 100u) == 0u) {
    REXLOG_INFO("Refii guest-tick pump: CoopKick — not an online host with a peer; ignored");
    return true;
  }
  const bool up = refii_bridge_stated_pause(nullptr, nullptr, nullptr) != 0u && refii_bridge_stated_screen_now() == 0u;
  const uint32_t pause = up ? InGameScreenHandler(ctx, base, 0u) : 0u;
  if (pause == 0u || LoadGuestU32(base, pause) != refii::guest::Address(0x820CDE18u)) {
    REXLOG_INFO("Refii guest-tick pump: CoopKick — the pause root is not the screen up; ignored");
    return true;
  }
  const PPCContext saved = ctx;
  const uint32_t frame = (saved.r1.u32 - 0x200u) & ~0xFu;
  CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x8233AC68u), net);
  CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x822C3F50u), net, 1u);
  CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x82182C30u), net, 1u);
  StoreGuestU32(base, pause + 280u, 5u);
  REXLOG_INFO("Refii guest-tick pump: CoopKick — player two told; the pause root's countdown armed (h+280 = 5)");
  return true;
}

/// LOCAL CO-OP, AND PLAYER TWO OWNS THE OPEN PAUSE: the menus are player two's. The guest states
/// the owner (the pause root's stated player, StateMenuOwner) and opens player two's root
/// for them (`sub_826A2C88(…, 0)`), but its pages read "the local player" — record 1 on one
/// machine (`sub_821C7CE0`, and inline in `sub_829B1968` at 0x829B19B4). So while this holds,
/// the hero the menus read and act on is player two's, as it is on an online joiner's machine.
bool MenusActForPlayerTwo(uint8_t* base) {
  if (refii_bridge_menu_owner_two() == 0) {
    return false;
  }
  const uint32_t root = LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = root >= 0x10000u && root < 0xC0000000u ? LoadGuestU32(base, root + 12u) : 0u;
  return game >= 0x10000u && game < 0xC0000000u && LoadGuestU32(base, game + 0x11Cu) == 2u;
}

/// One component of an entity, by id, or 0.
///
/// The presence bitmask is an ARRAY, not one word: `*(u32*)(entity + 36 + 4*(id>>5))` tested at
/// `id & 31`. Component 57 lives in the second word.
uint32_t HeroComponent(uint8_t* base, uint32_t entity, uint32_t id) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  if (!plausible(entity) || id >= 256u) {
    return 0u;
  }

  const uint32_t present = LoadGuestU32(base, entity + 36u + (id >> 5) * 4u);
  if (((present >> (id & 31u)) & 1u) == 0u) {
    return 0u;
  }

  const uint32_t table = LoadGuestU32(base, entity + 72u);
  const uint32_t end = LoadGuestU32(base, entity + 76u);
  if (!plausible(table) || end <= table || (end - table) > 0x2000u) {
    return 0u;
  }

  for (uint32_t at = table; at < end; at += 8u) {
    if (LoadGuestU32(base, at) == id) {
      const uint32_t component = LoadGuestU32(base, at + 4u);
      return plausible(component) ? component : 0u;
    }
  }

  return 0u;
}

/// Samples the hero's DERIVED state — the values only the guest's own accessors can answer.
///
/// Called, not reproduced, for the three that need it: `sub_8223D320` adds the timed and
/// permanent modifier maps inside component 57, then the GDB constants
/// `EvilMoralityAddition`/`HolyMoralityAddition` when the hero carries alignment flags 7 and 6,
/// then clamps against `c57+20`/`+24` with NaN falling to the low bound. Renown's clamp is
/// BRANCH-LOCAL — a negative stored renown with no active modifier stays negative.
///
/// The TITLE is read rather than called: `sub_824D8A10` allocates (refcounts through
/// `dword_83497088` and builds a string), and the host can resolve the tag from the record hash
/// at `c57+92`. Sampled on a cadence, not every tick — the logbook's own fill runs once on open.
/// The player object — one hop above the hero. `HeroEntity` returns the hero ENTITY; the dog is
/// a sibling handle on the object that owns both, so it is not reachable from the hero.
uint32_t PlayerObject(uint8_t* base) {
  const uint32_t kGameRoot = refii::guest::Address(0x83496AB8u);
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  // Root first: `*(0x83496AB8)`, then `+12` — read without the load this chain ended in
  // zeros and the dog page stayed blank (`SampleDog` said no dog while the guest's own
  // trick ticks were on screen; the live walk reaches the dog with the load, 2026-09-03).
  const uint32_t root = LoadGuestU32(base, kGameRoot);
  const uint32_t game = plausible(root) ? LoadGuestU32(base, root + 12u) : 0u;
  const uint32_t holder = plausible(game) ? LoadGuestU32(base, game + 88u) : 0u;
  const uint32_t party = plausible(holder) ? LoadGuestU32(base, holder + 4u) : 0u;
  const uint32_t player = plausible(party) ? LoadGuestU32(base, party) : 0u;
  return plausible(player) ? player : 0u;
}

/// The dog page's numbers — all plain reads off component 97, plus the name off component 20.
/// THE LOCAL PLAYER'S DOG: hero one's is the guest's own slot (players+196), player two's the
/// co-op dog bound to them (RefiiCoopPlayerDog), so a joiner's page shows the joiner's dog.
void SampleDog(uint8_t* base, HeroDerived& sampled) {
  constexpr uint32_t kDogStatsComponent = 97u;
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  sampled.dog_known = false;
  sampled.dog_entity = 0u;

  const uint32_t hero = HeroEntity(base);
  const int owner = hero != 0u && hero == refii_mod_player_hero(base, 2) ? 2 : 1;
  const uint32_t dog = RefiiCoopPlayerDog(base, owner);
  if (!plausible(dog)) {
    return;
  }

  const uint32_t stats = HeroComponent(base, dog, kDogStatsComponent);
  if (stats == 0u) {
    return;
  }

  sampled.dog_known = true;
  sampled.dog_entity = dog;
  sampled.dog_definition = LoadGuestU32(base, dog + 0x1Cu);  // the definition, i.e. the breed
  sampled.dog_searching = static_cast<int32_t>(LoadGuestU32(base, stats + 184u));
  sampled.dog_combat = static_cast<int32_t>(LoadGuestU32(base, stats + 188u));

  const uint32_t state = LoadGuestU32(base, stats + 276u);
  sampled.dog_state = state >= 8u ? 0 : static_cast<int32_t>(state);

  // THE TITLE IS THE DOG'S NAME. `sub_829BC750` hands component 20 to `sub_823A8148`, which
  // copies the LHString at `c20+52` when its block has a length and otherwise resolves the
  // Babel key at `c20+48` through the text manager. Both go across; the host resolves the key.
  constexpr uint32_t kDogNamingComponent = 20u;
  sampled.dog_name[0] = '\0';
  sampled.dog_name_key = 0u;
  const uint32_t naming = HeroComponent(base, dog, kDogNamingComponent);
  if (naming != 0u) {
    const uint32_t custom = LoadGuestU32(base, naming + 52u);
    if (plausible(custom) && LoadGuestU32(base, custom + 4u) != 0u) {
      std::snprintf(sampled.dog_name, sizeof(sampled.dog_name), "%s",
                    ReadStatedWideLhString(base, naming + 52u).c_str());
    }
    sampled.dog_name_key = LoadGuestU32(base, naming + 48u);
  }

  // The trick vector stores GDB record IDs, not name hashes: `sub_8219C5E8` runs `FNV1(name)`
  // through `NGDB_CRecord_Get_int(mgr, hash, 4)` first, so the stored key is that lookup's
  // record ID. The ids are published raw; the host resolves names against its own GDB.
  sampled.dog_trick_count = 0u;
  const uint32_t begin = LoadGuestU32(base, stats + 740u);
  const uint32_t end = LoadGuestU32(base, stats + 744u);
  if (plausible(begin) && end > begin && (end - begin) <= 0x400u) {
    uint32_t slot = 0;
    for (uint32_t at = begin; at < end && slot < 32u; at += 8u, ++slot) {
      sampled.dog_trick_ids[slot] = LoadGuestU32(base, at);
    }

    sampled.dog_trick_count = slot;
  } else {
    sampled.dog_trick_count = 0u;
  }
}

/// The hero-side damage multiplier the spell stats rows scale by — a pure table read.
///
/// `sub_82541B30` is a 28-instruction leaf — no calls, no allocation — so it is reproduced here
/// as three loads rather than called.
void SampleSpellDamage(uint8_t* base, uint32_t hero, HeroDerived& sampled) {
  constexpr uint32_t kMagicComponent = 118u;
  constexpr uint32_t kSpellManagerComponent = 56u;
  const uint32_t kTableLoaded = refii::guest::Address(0x83496C19u);
  const uint32_t kTable = refii::guest::Address(0x833192A4u);

  for (uint32_t level = 0; level < 6u; ++level) {
    sampled.spell_damage[level] = 1.0f;
  }

  const uint32_t magic = HeroComponent(base, hero, kMagicComponent);
  const uint32_t owner = magic != 0u ? LoadGuestU32(base, magic + 4u) : 0u;
  const uint32_t manager =
      owner != 0u ? HeroComponent(base, owner, kSpellManagerComponent) : 0u;
  if (manager == 0u || LoadGuestU8(base, kTableLoaded) == 0u) {
    return;
  }

  const int32_t tier_raw = static_cast<int32_t>(LoadGuestU32(base, manager + 420u));
  if (tier_raw < 0) {
    return;
  }

  const uint32_t tier = tier_raw > 2 ? 2u : static_cast<uint32_t>(tier_raw);
  for (uint32_t level = 0; level < 6u; ++level) {
    const uint32_t raw = LoadGuestU32(base, kTable + 4u * (11u * tier + level));
    std::memcpy(&sampled.spell_damage[level], &raw, sizeof(float));
  }
}

/// The world map's region page, sampled by CALLING the guest's own eleven providers.
///
/// They all share one shape — `(LHString* out, LHString* regionName)` — and each resolves the
/// hero, component 250 and the region key itself, returning the FINISHED string (miles or hours
/// with the right plural tag, a "found / total" pair, a comma-joined merchant list). Several of
/// the underlying numbers are not readable at all: distance and travel time are pathfinding
/// accessors and the star rating averages live entity state.
///
/// Gated on the map being up, because these allocate (`sub_821F0240` takes a temp string ref per
/// call). One region per sample, the one the host asked for.
/// THE WORLD MAP'S `Family` LIST, through the guest's own collector.
///
/// `sub_8246F020(vector)` is what `sub_829A42C8` calls before it spawns a single row, and it is
/// the whole read: hero out of `dword_83496AB8`, component 136, the intrusive list at
/// `comp136+16`, a 64-bit entity UID per node resolved against the entity registry, and the name
/// off component 20 of the live entity (`sub_823A8148`: the LH string at `comp20+52` when it has
/// a length, else `TextManager` on the tag at `comp20+48`) — with the record's own stored name as
/// the fallback for a member who is not resident. Walking that ourselves would be four guessed
/// offsets and a red-black tree; calling it is one function and the guest's own answer.
///
/// The vector it fills is 16 bytes: `[1]` begin, `[2]` end, `[3]` capacity, records of 52 bytes.
/// `sub_829A42C8` reads `(end - begin) / 52` (`divw` against `li r10, 0x34` at 0x829A4304) and
/// starts at `begin + 8`, stepping 13 dwords — so the display name is the LH string handle at
/// `record+8`, and it is the only field the list uses.
///
/// IT ALLOCATES, so it is paired with `sub_826C9450` on every path and gated on the world map
/// being up. That destructor runs `sub_8246FBD8` over each record and frees the block, which is
/// exactly what the screen does at 0x829A4520.
/// One Fable "LH string" of 16-bit characters at a handle whose LAYOUT IS KNOWN.
///
/// `handle` holds a block pointer; the block is `{ const wchar_t* chars, int length }`. That is
/// the shape `sub_82B3F898` reads — `v4 = *a2; length = *(v4 + 4); chars = *(wchar_t**)*a2`,
/// then `*(_WORD *)&chars[i]` per character at 0x82B3F910.
///
/// Never probe the depth. A reader that chased up to three hops and rejected non-Latin text
/// skipped a Cyrillic label ("Русский", units 0x0420 0x0443), loaded those two units as the
/// pointer 0x04200443 and faulted on the Windows reserved arena. `ReadGuestLhString` is the
/// ASCII reader for the same two hops and would return the high bytes as text.
///
/// The length is the string's own (`block + 4`). A display box's body is one of these: tutorial 65,
/// raised by the wedding movie's end (`sub_822A9D60` 0x822A9F04..0x822A9F14), is 327 units, and a
/// reader that refused anything past 256 stated that box with no body — up, undrawn, and holding the
/// game until A (GitHub #69). The bound is only a guard against a wild length.
constexpr uint32_t kStatedWideLhStringMaxUnits = 16384u;

std::string ReadStatedWideLhString(uint8_t* base, uint32_t handle) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  if (base == nullptr || !plausible(handle)) {
    return std::string();
  }

  uint32_t block = 0;
  if (!TryLoadGuestU32(base, handle, &block) || !plausible(block)) {
    return std::string();
  }

  uint32_t chars = 0;
  uint32_t length = 0;
  if (!TryLoadGuestU32(base, block, &chars) ||
      !TryLoadGuestU32(base, block + 4u, &length) ||
      !plausible(chars) || length == 0u || length > kStatedWideLhStringMaxUnits) {
    return std::string();
  }

  std::vector<uint8_t> raw(static_cast<size_t>(length) * 2u);
  const size_t readable = TryCopyGuestBytes(base, chars, raw.data(), raw.size());
  if (readable < 2u) {
    return std::string();
  }
  const uint32_t max_units = std::min<uint32_t>(length, static_cast<uint32_t>(readable / 2u));

  std::string value;
  value.reserve(max_units);
  for (uint32_t i = 0; i < max_units; ++i) {
    const uint16_t unit = static_cast<uint16_t>(
        (static_cast<uint32_t>(raw[i * 2u]) << 8) | raw[i * 2u + 1u]);
    if (unit == 0) {
      break;
    }

    if (unit < 0x80u) {
      value.push_back(static_cast<char>(unit));
    } else if (unit < 0x800u) {
      value.push_back(static_cast<char>(0xC0u | (unit >> 6)));
      value.push_back(static_cast<char>(0x80u | (unit & 0x3Fu)));
    } else {
      value.push_back(static_cast<char>(0xE0u | (unit >> 12)));
      value.push_back(static_cast<char>(0x80u | ((unit >> 6) & 0x3Fu)));
      value.push_back(static_cast<char>(0x80u | (unit & 0x3Fu)));
    }
  }

  return value;
}

/// THE WORLD MAP'S MINIMAP PAGE KEYS for the region the host names (`RefiiHostSetWorldMapRegion`, the
/// region row's own id less the 999 `sub_829A3A30` adds, 0x829A3B4C): the region's name, its first level,
/// that level's world key and its scenario — what the minimap fill `sub_829ABFC0` / disc `sub_828FE480`
/// keys its GDB `MinimapInfo` and art on. That fill writes its image, legend art and markers with no name
/// lookup (texture stores `sub_82425C10`, the GUI render lists, the image's transforms), so it is not a
/// page fill Godot can be told; Godot draws the minimap from these keys (Engine_UiMinimapPage). The
/// region INFO page is its own fill's statement (gui_screen_stub_world_map.cpp, scope "row.region").
void SampleWorldMapRegion(PPCContext& ctx, uint8_t* base) {
  constexpr uint32_t kWorldMapScreenId = 23u;
  const uint32_t kGameRoot = refii::guest::Address(0x83496AB8u);
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  static uint32_t s_last_world_map_region = ~0u;
  uint32_t screen_id = 0;
  if (!StatedInGameScreen(&screen_id) || screen_id != kWorldMapScreenId) {
    s_last_world_map_region = ~0u;
    return;
  }

  const uint32_t wanted = RequestedWorldMapRegion();
  if (wanted == s_last_world_map_region) {
    return;
  }

  // party -> region manager -> the level list, exactly the chain `sub_823C9000` walks.
  //
  // THE FIRST DEREFERENCE IS THE GLOBAL'S OWN. `dword_83496AB8` HOLDS the app pointer; the
  // guest's own prologue is `lwz r11, dword_83496AB8 ; lwz r11, 0xC(r11)`, so the chain starts
  // at `*(0x83496AB8)` and only then takes `+12`. This read `*(0x83496AB8 + 12)` — the dword at
  // 0x83496AC4, a different global entirely — and every offset after it was measured from
  // whatever that happened to be. The host asks for a region by the index the guest's own region
  // walk `sub_82518330` gives it, the one `sub_829A3A30` spawned the row with, so this walk is that
  // walk.

  // WHY A SILENT BAIL IS BANNED HERE: the host read `count 0 / known false` for a whole
  // session and could not say WHICH of the seven reads below was refusing — every one of them
  // returns identically. One line per distinct failure, once each per run.
  static std::atomic<uint32_t> bail_said{0};
  const auto say_bail = [&](uint32_t which, uint32_t at, uint32_t value) {
    const uint32_t bit = 1u << which;
    if ((bail_said.fetch_or(bit) & bit) == 0u) {
      REXLOG_INFO("Refii world-map sample: bail {} at 0x{:08X} value 0x{:08X}", which, at,
                  value);
    }
  };

  const uint32_t root = LoadGuestU32(base, kGameRoot);
  const uint32_t game = plausible(root) ? LoadGuestU32(base, root + 12u) : 0u;
  const uint32_t holder = plausible(game) ? LoadGuestU32(base, game + 88u) : 0u;
  const uint32_t party = plausible(holder) ? LoadGuestU32(base, holder + 4u) : 0u;
  const uint32_t regions = plausible(party) ? LoadGuestU32(base, party + 8u) : 0u;
  const uint32_t inner = plausible(regions) ? LoadGuestU32(base, regions + 4u) : 0u;
  const uint32_t list = plausible(inner) ? LoadGuestU32(base, inner + 12u) : 0u;
  if (!plausible(list)) {
    if (!plausible(root)) say_bail(0, kGameRoot, root);
    else if (!plausible(game)) say_bail(1, root + 12u, game);
    else if (!plausible(holder)) say_bail(2, game + 88u, holder);
    else if (!plausible(party)) say_bail(3, holder + 4u, party);
    else if (!plausible(regions)) say_bail(4, party + 8u, regions);
    else if (!plausible(inner)) say_bail(5, regions + 4u, inner);
    else say_bail(6, inner + 12u, list);
    return;
  }

  // `sub_82518330` walks [list+8, list+12) with stride 8 keeping NON-NULL first dwords; walked
  // here instead so no vector is allocated. Non-null, not plausible — an entry that is neither
  // still consumes an output index in the guest's walk, and this counter is compared against an
  // ordinal produced by that same rule.
  const uint32_t begin = LoadGuestU32(base, list + 8u);
  const uint32_t end = LoadGuestU32(base, list + 12u);
  if (!plausible(begin) || end <= begin || (end - begin) > 0x4000u ||
      ((end - begin) % 8u) != 0u) {
    say_bail(7, list + 8u, begin);
    say_bail(8, list + 12u, end);
    return;
  }

  WorldMapRegion region;
  uint32_t seen = 0;
  uint32_t name_handle = 0;
  for (uint32_t at = begin; at < end; at += 8u) {
    if (LoadGuestU32(base, at) == 0u) {
      continue;
    }

    if (seen == wanted) {
      // THE SLOT IS TWO DEREFERENCES FROM THE CHARACTERS, and this captured the slot's own
      // address — every read downstream was one level short, so the sample published with an
      // empty name while the walk itself was fine. `*slot` is the region object; its word 0
      // is the name handle, so `**slot` is the characters pointer — which is how
      // `sub_82519DF8` matches a region, `strcmpi(**v5, **a2)`. The keys do not take this
      // string; see the level lookup below.
      name_handle = LoadGuestU32(base, at);
    }

    ++seen;
  }

  region.count = seen;
  if (name_handle == 0u) {
    PublishWorldMapRegion(region);
    return;
  }

  region.known = true;
  region.index = wanted;
  std::snprintf(region.name, sizeof(region.name), "%s",
                ReadGuestLhString(base, name_handle).c_str());

  // THE KEYS ARE THE REGION'S FIRST LEVEL, as an `LHString*` — the address of a one-word handle:
  // the region record's level vector at +16 / +20, stride 8, `**(v13 + 16)` (the info fill
  // `sub_829AB0E0` takes the same level at 0x829AB228). `sub_821F0240(dst, src)` is `*dst = *src` plus
  // a refcount bump, so the guest hands its callees the level OBJECT, whose word 0 is its name handle;
  // the object pointer is passed straight through, the same read without owning a reference.
  const uint32_t levels = LoadGuestU32(base, name_handle + 16u);
  const uint32_t levels_end = LoadGuestU32(base, name_handle + 20u);
  if (!plausible(levels) || levels_end <= levels) {
    say_bail(9, name_handle + 16u, levels);
    PublishWorldMapRegion(region);
    return;
  }

  const uint32_t level = LoadGuestU32(base, levels);
  if (!plausible(level)) {
    say_bail(10, levels, level);
    PublishWorldMapRegion(region);
    return;
  }

  // Claimed by lowering r1 so a callee's frame cannot land on the string it is handed.
  const uint32_t sp = ctx.r1.u32;
  const uint32_t frame = (sp - 0x200u) & ~0xFu;
  const PPCContext saved = ctx;

  // THE MINIMAP PAGE'S KEYS, taken the way `sub_829ABFC0` takes them: the region's first level
  // name, its world/level key through the guest's own hasher, and the scenario the world names
  // for that key (`World+88` table; the lookup returns the address of the pair's string handle,
  // or an empty global when there is none).
  //
  // BOTH STRING ARGUMENTS ARE LHString HANDLES, NOT CHARACTERS. `sub_8231CA18(world, a2, a3)`
  // copies `a2` as a handle (`sub_821E3108` -> `sub_822651A0(a1, a2)`) and appends `a3` the
  // same way, then releases both. Handing it the bytes "Albion" made it treat 'Albi' as a
  // string object and free it, which took the game down from inside the heap's spin lock the
  // first time the map opened. So "Albion" is built as a guest string on the frame with the
  // guest's own constructor and released after, and the level goes as the guest's callees take
  // it: `level` IS the handle pointer the guest's `&regionName` names.
  std::snprintf(region.level_name, sizeof(region.level_name), "%s",
                ReadGuestLhString(base, level).c_str());
  {
    const uint32_t world_text = frame + 0x100u;
    const uint32_t world_handle = frame + 0x110u;
    static const char kWorld[] = "Albion";
    for (uint32_t i = 0; i < sizeof(kWorld); ++i) {
      base[world_text + i] = static_cast<uint8_t>(kWorld[i]);
    }
    StoreGuestU32(base, world_handle, 0u);

    ctx = saved;
    ctx.r1.u32 = frame;
    ctx.r3.u32 = world_handle;
    ctx.r4.u32 = world_text;
    ctx.r5.u32 = 0xFFFFFFFFu;  // NUL-terminated, as the event constructor is handed it
    if (SafeInvokeGuestCall(rex_lhCharString_CtorFromCStr, ctx, base)) {
      ctx = saved;
      ctx.r1.u32 = frame;
      ctx.r3.u32 = inner;
      ctx.r4.u32 = world_handle;
      ctx.r5.u32 = level;
      if (SafeInvokeGuestCall(rex_lhWorld_ComputeWorldLevelFnv1Key, ctx, base)) {
        region.level_key = ctx.r3.u32;
      }

      ctx = saved;
      ctx.r1.u32 = frame;
      ctx.r3.u32 = world_handle;
      SafeInvokeGuestCall(rex_lhCharString_Dtor, ctx, base);

      ctx = saved;
      ctx.r1.u32 = frame;
      ctx.r3.u32 = inner;
      ctx.r4.u32 = region.level_key;
      if (SafeInvokeGuestCall(rex_lhWorld_LookupScenarioNameByLevelKey, ctx, base)) {
        // `rex_lhWorld_LookupScenarioNameByLevelKey` (`0x8231E258`) returns the `LHString*`
        // handle slot address directly (`v7 + 4` in `World+88`, or `&dword_834A5E78`).
        // Do NOT dereference `scenario_slot` before passing it to `ReadGuestLhString`.
        const uint32_t scenario_slot = ctx.r3.u32;
        std::snprintf(region.scenario, sizeof(region.scenario), "%s",
                      plausible(scenario_slot) ? ReadGuestLhString(base, scenario_slot).c_str()
                                               : "");
      }
      ctx = saved;
    }
  }

  PublishWorldMapRegion(region);
  s_last_world_map_region = wanted;
}

/// THE WORLD MAP'S PROPERTY ROW — the numbers of screen 28's page for a property the hero holds,
/// asked of the guest for a building the host names by UID (SamplePropertyEstate). The row is the
/// remaster's (Engine_UiPropertySource, Fable 3's map properties): no guest page states it, so these
/// are the guest's providers answering for that building — value, economy, income, the sale floor,
/// the purchase price, the three not-owned deltas, the ownership and tenant walks. Screen 28 itself is
/// stated by its own page fills (page_fills_stated.cpp). False when the entity carries no CECBuilding.
bool FillRealEstate(PPCContext& ctx, uint8_t* base, uint32_t hero, uint32_t building_entity,
                    RealEstate& estate) {
  constexpr uint32_t kPropertiesComponent = 69u;   // the hero's ownership list
  constexpr uint32_t kBuildingComponent = 67u;
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  // Claimed low so a callee's frame cannot land on an out-parameter it is handed.
  const uint32_t sp = ctx.r1.u32;
  const uint32_t frame = (sp - 0x200u) & ~0xFu;
  const uint32_t out = frame + 0x20u;
  const PPCContext saved = ctx;

  const auto call = [&](PPCFunc* fn, uint32_t r3, uint32_t r4) -> uint32_t {
    if (fn == nullptr) {
      return 0u;
    }
    ctx = saved;
    ctx.r1.u32 = frame;
    ctx.r3.u32 = r3;
    ctx.r4.u32 = r4;
    const uint32_t result = SafeInvokeGuestCall(fn, ctx, base) ? ctx.r3.u32 : 0u;
    ctx = saved;
    return result;
  };

  const uint32_t building = HeroComponent(base, building_entity, kBuildingComponent);
  if (!plausible(building)) {
    return false;
  }

  const uint32_t properties = HeroComponent(base, hero, kPropertiesComponent);

  estate.known = true;
  estate.type_mask = LoadGuestU32(base, building + 28u);

  // Both walk the ownership list at `properties+28` comparing the 64-bit entity id at
  // `BE+20`/`BE+24`, taking and releasing a reference per node — called, not reproduced,
  // because reproducing it would mean touching those refcounts by hand.
  estate.owned = call(FindGuestFunction(refii::guest::Address(0x828D14E8u)), properties, building_entity) != 0u;
  estate.has_tenant = call(FindGuestFunction(refii::guest::Address(0x828D2800u)), properties, building_entity) != 0u;

  estate.value = static_cast<int32_t>(call(FindGuestFunction(refii::guest::Address(0x8282AEB0u)), building, 0u));
  estate.economy = static_cast<int32_t>(call(FindGuestFunction(refii::guest::Address(0x8282C1C0u)), building, 0u));
  estate.income = static_cast<int32_t>(call(FindGuestFunction(refii::guest::Address(0x8282C110u)), building, 0u));

  const int32_t floor_price =
      static_cast<int32_t>(call(FindGuestFunction(refii::guest::Address(0x8282BCA0u)), building, 0u));
  estate.sale_price = estate.owned
      ? floor_price
      : static_cast<int32_t>(call(FindGuestFunction(refii::guest::Address(0x8282BA48u)), building, 0u));
  estate.selling_fees = floor_price - estate.value;

  estate.not_vacant = static_cast<int32_t>(call(FindGuestFunction(refii::guest::Address(0x8282C470u)), building, 0u));
  estate.owner_killed = static_cast<int32_t>(call(FindGuestFunction(refii::guest::Address(0x8282C870u)), building, 0u));
  estate.hero_opinion = static_cast<int32_t>(call(FindGuestFunction(refii::guest::Address(0x8282C610u)), building, 0u));

  // DECORATION HAS NO FUNCTION — the filler computes it inline at 0x82999804 as the furniture
  // value scaled by the building's decoration multiplier, so this is the one number the host
  // side has to do itself.
  {
    const int32_t furniture = static_cast<int32_t>(LoadGuestU32(base, building + 44u));
    const uint32_t bits = LoadGuestU32(base, building + 116u);
    float multiplier = 0.0f;
    std::memcpy(&multiplier, &bits, sizeof(multiplier));
    estate.decoration = static_cast<int32_t>(static_cast<float>(furniture) * multiplier);
  }

  // The GDB record the page's text hangs off. `sub_821DA3B0` fills a `{hash, cachedPtr}` pair;
  // only the hash crosses, and the host resolves BuildingName, BuildingAddress,
  // BuildingAnecdotes, BasicSalePrice and CanSellOncePurchased from its own GDB.
  StoreGuestU32(base, out, 0u);
  StoreGuestU32(base, out + 4u, 0u);
  call(FindGuestFunction(refii::guest::Address(0x821DA3B0u)), out, building);
  estate.record = LoadGuestU32(base, out);

  // The bonuses hang off a DIFFERENT record. `sub_82917F60` runs the same `sub_821DA3B0`
  // indirection over component 184 and reads its `Bonuses` array, so the building's own record
  // does not carry them; both hashes have to cross.
  {
    constexpr uint32_t kBonusComponent = 184u;
    const uint32_t bonuses = HeroComponent(base, building_entity, kBonusComponent);
    if (plausible(bonuses)) {
      StoreGuestU32(base, out, 0u);
      StoreGuestU32(base, out + 4u, 0u);
      call(FindGuestFunction(refii::guest::Address(0x821DA3B0u)), out, bonuses);
      estate.bonuses_record = LoadGuestU32(base, out);
    }
  }

  // WHOEVER IS IN IT, on the guest's own three branches (`sub_82998FB0`, 0x8299959c on):
  //   hero owns it, rented out  -> the TENANT, `sub_8282D5D8`, else GUI_..._TENANT_OTHER;
  //   hero owns it, not rented  -> the hero's TITLE (below), never a creature name;
  //   hero does not own it      -> the owner, `sub_8282D410`, else GUI_..._OWNER_OTHER.
  // `sub_8282D410` answers the HERO for a hero-owned building, and `sub_823C8510` cannot name
  // the hero (it carries no component 20). `sub_823C8510` takes the entity in r4 — a lost
  // argument in the decompile, explicit in the filler's own `mr r4, r3` — and answers a WIDE
  // string.
  {
    const uint32_t occupant = estate.has_tenant
        ? call(FindGuestFunction(refii::guest::Address(0x8282D5D8u)), building, 0u)
        : estate.owned ? 0u
                       : call(FindGuestFunction(refii::guest::Address(0x8282D410u)), building, 0u);
    if (plausible(occupant)) {
      StoreGuestU32(base, out, 0u);
      call(FindGuestFunction(refii::guest::Address(0x823C8510u)), out, occupant);
      std::snprintf(estate.occupant, sizeof(estate.occupant), "%s",
                    ReadGuestWideString(base, out).c_str());
      ctx = saved;
      ctx.r1.u32 = frame;
      ctx.r3.u32 = out;
      SafeInvokeGuestCall(rex_lhWideString_Dtor, ctx, base);
      ctx = saved;
    }
  }

  // The hero's current title record — `*(comp57+92)`, the hash half of the lazy GDB ref
  // `sub_824D8A10` resolves; the host walks `HeroTitleComponent.TextTag` from its own GDB.
  {
    constexpr uint32_t kTitleComponent = 57u;
    const uint32_t titles = HeroComponent(base, hero, kTitleComponent);
    if (plausible(titles)) {
      estate.title_record = LoadGuestU32(base, titles + 92u);
    }
  }

  return true;
}

/// THE CO-OP SCREEN — see `CoopScreen`. Sampled every tick the screen object exists (the
/// screen is cached by id, so the host uses the sample only while its book stands). One guest
/// call: the sign-in import the buttons writer itself makes (`sub_82439988` 0x824399ec).
void SampleCoopScreen(PPCContext& ctx, uint8_t* base) {
  constexpr uint32_t kCoopScreenId = 24u;
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  const uint32_t screen = InGameScreenHandler(ctx, base, kCoopScreenId);
  CoopScreen coop;
  const uint32_t root = LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = plausible(root) ? LoadGuestU32(base, root + 12u) : 0u;
  if (!plausible(screen) || !plausible(game)) {
    PublishCoopScreen(coop);
    return;
  }

  coop.known = true;
  coop.session = static_cast<int32_t>(LoadGuestU32(base, game + 284u));
  const uint32_t net = LoadGuestU32(base, game + 0x8Cu);
  coop.online_client = plausible(net) && LoadGuestU8(base, net + 0x34u) != 0u &&
                       LoadGuestU8(base, net + 0x36u) != 0u;
  coop.peer_ready = LoadGuestU8(base, screen + 296u) != 0u;
  coop.flag297 = LoadGuestU8(base, screen + 297u) != 0u;
  coop.flag298 = LoadGuestU8(base, screen + 298u) != 0u;
  coop.child = LoadGuestU8(base, screen + 270u) != 0u;
  coop.child_level = LoadGuestU8(base, screen + 271u) != 0u;
  coop.user_right_save = static_cast<int32_t>(LoadGuestU32(base, screen + 292u));

  // `sub_8243AE90`: P = *(game+0x94), Q = *(P+4) a string handle, chosen iff its length != 0.
  const uint32_t picked = LoadGuestU32(base, game + 0x94u);
  const uint32_t name = plausible(picked) ? LoadGuestU32(base, picked + 4u) : 0u;
  coop.save_chosen = plausible(name) && LoadGuestU32(base, name + 4u) != 0u;

  // The right group's widgets: `std::map<int, widget>` at scr+144 (map+4 the head, head+4 the
  // root; node +0 left, +8 right, +12 key, +16 widget, +25 nil). Key k is pickbox k+1
  // (`sub_82436ED8` asks key 0 for pickbox 1).
  const uint32_t head = LoadGuestU32(base, screen + 144u + 4u);
  for (uint32_t key = 0; key < 3u && plausible(head); ++key) {
    uint32_t node = LoadGuestU32(base, head + 4u);
    uint32_t widget = 0u;
    for (int guard = 0; guard < 32 && plausible(node) && LoadGuestU8(base, node + 25u) == 0u; ++guard) {
      const int32_t at = static_cast<int32_t>(LoadGuestU32(base, node + 12u));
      if (at == static_cast<int32_t>(key)) {
        widget = LoadGuestU32(base, node + 16u);
        break;
      }
      node = LoadGuestU32(base, node + (static_cast<int32_t>(key) < at ? 0u : 8u));
    }
    if (plausible(widget)) {
      coop.pick_selected[key] = static_cast<int32_t>(LoadGuestU32(base, widget + 36u));
      coop.pick_enabled[key] = LoadGuestU8(base, widget + 25u) != 0u;
      coop.pick_state[key] = static_cast<int32_t>(LoadGuestU32(base, widget + 28u));
    }
  }

  const uint32_t left = LoadGuestU32(base, screen + 264u);
  const uint32_t right = LoadGuestU32(base, screen + 260u);
  coop.profile_left = plausible(left) ? static_cast<int32_t>(LoadGuestU32(base, left + 56u)) : 0;
  coop.profile_right = plausible(right) ? static_cast<int32_t>(LoadGuestU32(base, right + 56u)) : 0;

  const uint32_t user = LoadGuestU32(base, screen + 300u);
  if (user < 4u) {
    const uint32_t sp = ctx.r1.u32;
    const uint32_t frame = (sp - 0x200u) & ~0xFu;
    const PPCContext saved = ctx;
    coop.right_signed_in =
        CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x82CC29A0u), user) != 0u;
  }

  PublishCoopScreen(coop);
}

/// THE WORLD MAP'S PROPERTY ROW — the same page numbers for the property the cursor is on (the
/// host names its UID, RequestPropertyEstate). The building is found by UID in the guest's own
/// map (`refii_household_resolve_uid`); one in a region that is not loaded has no entity, and is
/// answered as unknown for that UID so the host falls back to the ownership record.
void SamplePropertyEstate(PPCContext& ctx, uint8_t* base) {
  constexpr uint32_t kWorldMapScreenId = 23u;
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  uint32_t screen_id = 0;
  const uint64_t uid = RequestedPropertyEstate();
  if (uid == 0u || !StatedInGameScreen(&screen_id) ||
      screen_id != kWorldMapScreenId) {
    return;
  }

  // A new row is answered on this tick; the one already answered refreshes every 8th.
  static uint64_t answered = 0u;
  static uint32_t ticks = 0u;
  if (uid == answered && (++ticks % 8u) != 0u) {
    return;
  }
  answered = uid;

  RealEstate estate;
  const uint32_t building_entity = refii_household_resolve_uid(&ctx, base, uid);
  if (plausible(building_entity) && (LoadGuestU8(base, building_entity + 0x90u) & 0x40u) != 0u) {
    FillRealEstate(ctx, base, HeroEntity(base), building_entity, estate);
  }
  PublishPropertyEstate(uid, estate);
}

/// The completed-quest list the logbook's page indexes into.
///
/// The guest rebuilds this every time (`sub_82470220`): it copies the quest-log component's
/// entries, sorts them DESCENDING by the GDB property `DisplayPriority`, then applies three
/// byte filters. The filters run here and the sort is left to the host, since filtering
/// preserves relative order.
///
/// The three filters are bytes on the quest object: `sub_82469330` tests `quest+12`,
/// `sub_82469428` tests `quest+21`, `sub_82469628` tests `quest+77`, each against 1. The source
/// list is resident — only the guest's OUTPUT copy is transient — so this walk allocates nothing.
void SampleQuests(uint8_t* base, uint32_t hero) {
  constexpr uint32_t kQuestLogComponent = 102u;
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  const uint32_t log = HeroComponent(base, hero, kQuestLogComponent);
  if (log == 0u) {
    PublishQuests(nullptr, 0u);
    return;
  }

  const uint32_t begin = LoadGuestU32(base, log + 0x10u);
  const uint32_t end = LoadGuestU32(base, log + 0x14u);
  if (!plausible(begin) || end <= begin || (end - begin) > 0x8000u) {
    PublishQuests(nullptr, 0u);
    return;
  }

  static QuestEntry entries[kMaxCompletedQuests];
  uint32_t count = 0;

  // Stride 12, and the quest pointer is at `+4` of each inline record.
  for (uint32_t at = begin; at < end && count < kMaxCompletedQuests; at += 12u) {
    const uint32_t quest = LoadGuestU32(base, at + 4u);
    if (!plausible(quest)) {
      continue;
    }

    if (LoadGuestU8(base, quest + 12u) != 1u || LoadGuestU8(base, quest + 21u) != 1u ||
        LoadGuestU8(base, quest + 77u) != 1u) {
      continue;
    }

    QuestEntry& entry = entries[count];
    entry = QuestEntry{};

    // `quest+4` is the GDB def handle `{hash, cachedPtr}` — the NAME and DESCRIPTION tags and
    // `DisplayPriority` all hang off the record, never off the quest object.
    entry.record = LoadGuestU32(base, quest + 4u);
    entry.money = static_cast<int32_t>(LoadGuestU32(base, quest + 36u));
    entry.renown = static_cast<int32_t>(LoadGuestU32(base, quest + 32u));

    const std::string conclusion = ReadGuestLhString(base, quest + 0x50u);
    std::snprintf(entry.conclusion, sizeof(entry.conclusion), "%s", conclusion.c_str());
    ++count;
  }

  PublishQuests(entries, count);
}

/// Writes a C string into guest scratch; returns its address.
uint32_t PlaceGuestString(uint8_t* base, uint32_t at, const char* text) {
  uint32_t i = 0u;
  for (; text[i] != '\0'; ++i) {
    StoreGuestU8(base, at + i, static_cast<uint8_t>(text[i]));
  }
  StoreGuestU8(base, at + i, 0u);
  return at;
}

/// The texture layers the GUEST'S OWN dye builder states for one dyed item, worn or carried,
/// written as a character's composites are ([original, count, count x kTextureLayerWords]).
///
/// What the draw virtual does for a worn dyed item (`sub_8220DE88` 0x8220f1a8..0x8220f2d4,
/// read in the disassembly): the item's type-2 dye component (`sub_823A1E18`), the item's
/// definition (`sub_821B24F8(item+4)`), its `AppearanceModifierComponent` (`sub_822A99E8`,
/// a plain C-string field name), then `DyeTextures` — or `OverrideOpenDyeTextures` when the
/// hero's worn items are open (comp15+0x3D), the garment covers body area 0x800
/// (`sub_82A00580(app, "BodyAreasCovered")`) and that entry resolves an `OriginalTexture`
/// (`sub_82783418`) — and the same name with `_2`, each handed to
/// `sub_825B7470(vector, entry, dye)`, which pushes 32-byte records {+0 original, +4 texture,
/// +8 mode, +12 weight, +16 tint B G R A, +20 brightness, +24 layer key, +28 replacement}.
/// Only the vector is ours (a zeroed 0x14-byte header; `sub_825B9F60` frees it) — the guest
/// allocates and fills it. The records then take the virtual's own order: grouped by original
/// (stable sort on +0, 0x8220f5d8), a base layer {original, mode 0, weight 1} first, then each
/// record by layer key and texture id (`sub_825BCBE8`, 0x8220f8a8); a layer is tinted when its
/// tint is not {0,0,0,0xFF} or its brightness is beyond 1e-4 (the virtual's isDyed).
uint32_t BuildItemDyeLayers(PPCContext& ctx, uint8_t* base, const PPCContext& saved,
                            uint32_t frame, uint32_t item, uint32_t appearance, uint32_t* out,
                            uint32_t capacity) {
  PPCFunc* dye_of = FindGuestFunction(refii::guest::Address(0x823A1E18u));
  PPCFunc* definition_of = FindGuestFunction(refii::guest::Address(0x821B24F8u));
  PPCFunc* field = FindGuestFunction(refii::guest::Address(0x822A99E8u));
  PPCFunc* areas_of = FindGuestFunction(refii::guest::Address(0x82A00580u));
  PPCFunc* texture_of = FindGuestFunction(refii::guest::Address(0x82783418u));
  PPCFunc* build = FindGuestFunction(refii::guest::Address(0x825B7470u));
  PPCFunc* release = FindGuestFunction(refii::guest::Address(0x825B9F60u));
  if (dye_of == nullptr || definition_of == nullptr || field == nullptr || areas_of == nullptr ||
      texture_of == nullptr || build == nullptr || release == nullptr || out == nullptr) {
    return 0u;
  }

  // Scratch above the frame the guest calls run on: the record vector, two out-params and the
  // field names, as C strings — `sub_822A99E8` and its kin take a plain `char*` (the virtual
  // passes string literals: 0x8220f178 `addi r21, r11, "AppearanceModifierComponent"`).
  const uint32_t vec = frame + 0x40u;
  const uint32_t dye_out = frame + 0x60u;
  const uint32_t texture_out = frame + 0x64u;
  const uint32_t s_component = PlaceGuestString(base, frame + 0x80u, "AppearanceModifierComponent");
  const uint32_t s_dye = PlaceGuestString(base, frame + 0xA0u, "DyeTextures");
  const uint32_t s_dye2 = PlaceGuestString(base, frame + 0xB0u, "DyeTextures_2");
  const uint32_t s_open = PlaceGuestString(base, frame + 0xC0u, "OverrideOpenDyeTextures");
  const uint32_t s_open2 = PlaceGuestString(base, frame + 0xE0u, "OverrideOpenDyeTextures_2");
  const uint32_t s_areas = PlaceGuestString(base, frame + 0x100u, "BodyAreasCovered");
  const uint32_t s_original = PlaceGuestString(base, frame + 0x120u, "OriginalTexture");
  for (uint32_t i = 0u; i < 0x14u; i += 4u) {
    StoreGuestU32(base, vec + i, 0u);
  }

  const auto call = [&](PPCFunc* fn, uint32_t r3, uint32_t r4, uint32_t r5) {
    ctx = saved;
    ctx.r1.u32 = frame;
    ctx.r3.u32 = r3;
    ctx.r4.u32 = r4;
    ctx.r5.u32 = r5;
    return SafeInvokeGuestCall(fn, ctx, base) ? ctx.r3.u32 : 0u;
  };

  uint32_t written = 0u;
  StoreGuestU32(base, dye_out, 0u);
  if ((call(dye_of, item, dye_out, 0u) & 0xFFu) != 0u) {
    const uint32_t dye = LoadGuestU32(base, dye_out);
    const uint32_t definition = LooksLikeGuestPointer(dye) ? call(definition_of, item + 4u, 0u, 0u) : 0u;
    const uint32_t component =
        LooksLikeGuestPointer(definition) ? call(field, definition, s_component, 0u) : 0u;
    if (LooksLikeGuestPointer(component)) {
      uint32_t name = s_dye;
      uint32_t name2 = s_dye2;
      const bool worn_open = appearance != 0u &&
          ((LoadGuestU32(base, appearance + 0x3Cu) >> 16) & 0xFFu) != 0u;
      if (worn_open && (call(areas_of, component, s_areas, 0u) & 0x800u) != 0u) {
        const uint32_t open = call(field, component, s_open, 0u);
        if (LooksLikeGuestPointer(open)) {
          StoreGuestU32(base, texture_out, 0u);
          call(texture_of, texture_out, open, s_original);
          if (LoadGuestU32(base, texture_out) != 0u) {
            name = s_open;
            name2 = s_open2;
          }
        }
      }

      const uint32_t first = call(field, component, name, 0u);
      if (LooksLikeGuestPointer(first)) {
        call(build, vec, first, dye);
      }
      const uint32_t second = call(field, component, name2, 0u);
      if (LooksLikeGuestPointer(second)) {
        call(build, vec, second, dye);
      }

      struct Record {
        uint32_t original, texture, mode, weight, tint, brightness, key;
      };
      Record records[16];
      uint32_t count = 0u;
      const uint32_t begin = LoadGuestU32(base, vec + 4u);
      const uint32_t end = LoadGuestU32(base, vec + 8u);
      if (LooksLikeGuestPointer(begin) && end >= begin && ((end - begin) & 31u) == 0u) {
        for (uint32_t at = begin; at < end && count < 16u; at += 32u) {
          records[count++] = Record{LoadGuestU32(base, at),       LoadGuestU32(base, at + 4u),
                                    LoadGuestU32(base, at + 8u),  LoadGuestU32(base, at + 12u),
                                    LoadGuestU32(base, at + 16u), LoadGuestU32(base, at + 20u),
                                    LoadGuestU32(base, at + 24u)};
        }
      }
      call(release, vec, 0u, 0u);

      std::stable_sort(records, records + count,
                       [](const Record& a, const Record& b) { return a.original < b.original; });
      const auto tinted = [](const Record& r) {
        float brightness;
        std::memcpy(&brightness, &r.brightness, sizeof(brightness));
        return r.tint != 0x000000FFu || std::fabs(brightness) > 1e-4f;
      };
      const float one = 1.0f;
      uint32_t one_bits;
      std::memcpy(&one_bits, &one, sizeof(one_bits));
      for (uint32_t i = 0u; i < count;) {
        uint32_t j = i;
        while (j < count && records[j].original == records[i].original) {
          ++j;
        }
        std::stable_sort(records + i, records + j, [](const Record& a, const Record& b) {
          return a.key != b.key ? static_cast<int32_t>(a.key) < static_cast<int32_t>(b.key)
                                : a.texture < b.texture;
        });
        // One texture's overflow costs that texture only: past kTextureLayersPerTexture its
        // layers are clipped (the base and the first by key order kept), and a texture that does
        // not fit what is left is skipped while the item's other textures still go out.
        const uint32_t layers =
            std::min(1u + (j - i), static_cast<uint32_t>(kTextureLayersPerTexture));
        const uint32_t needed = 2u + layers * kTextureLayerWords;
        if (written + needed > capacity) {
          i = j;
          continue;
        }
        out[written++] = records[i].original;
        out[written++] = layers;  // kind 0: a composite
        uint32_t* layer = out + written;
        layer[0] = records[i].original;
        layer[1] = 0u;
        layer[2] = one_bits;
        layer[3] = 0u;
        layer[4] = 0xFFFFFFFFu;
        layer[5] = 0u;
        layer[6] = 0u;
        layer += kTextureLayerWords;
        for (uint32_t k = i; k < i + (layers - 1u); ++k, layer += kTextureLayerWords) {
          layer[0] = records[k].texture;
          layer[1] = records[k].mode;
          layer[2] = records[k].weight;
          layer[3] = tinted(records[k]) ? 1u : 0u;
          layer[4] = records[k].tint;
          layer[5] = records[k].brightness;
          layer[6] = 0u;
        }
        written += layers * kTextureLayerWords;
        i = j;
      }
    }
  }

  ctx = saved;
  return written;
}

/// THE ONE DYE READ THE LIST KEEPS (the stated exception, user decision 2026-11-27): the guest states no
/// colour for a garment in a list — only the mannequin entity wears it — so a dyed row's layers are read
/// for ONE item, the item the stated row was built from (RefiiHostGetItemDyeLayers names it: the
/// captured row's item, refii_bridge_menu_spawn_row_item), and only while the capture still holds a row of
/// that item. `sub_826CAFA8(_, &ref, slot)` (disc `sub_825A6F30`) reads only `*ref` and the item's type-2
/// record (`sub_823A1E18`); the layers are the guest's own builder's (BuildItemDyeLayers).
void StateRowItemDyeLayers(PPCContext& ctx, uint8_t* base) {
  const uint32_t item = refii_bridge_wanted_item_dye_layers();
  PPCFunc* is_dyed = FindGuestFunction(refii::guest::Address(0x826CAFA8u));
  if (item == 0u || is_dyed == nullptr || !LooksLikeGuestPointer(item) || !MenuRowsHoldItem(item)) {
    refii_bridge_set_item_dye_layers(nullptr, nullptr, nullptr, 0u, nullptr);
    return;
  }
  const uint32_t hero = HeroEntity(base);
  const uint32_t appearance = hero != 0u ? HeroComponent(base, hero, 15u) : 0u;
  const PPCContext saved = ctx;
  const uint32_t frame = (ctx.r1.u32 - 0x200u) & ~0xFu;
  const uint32_t ref = frame + 0x20u;
  bool dyed = false;
  for (uint32_t slot = 0u; slot < 2u && !dyed; ++slot) {
    StoreGuestU32(base, ref, item);
    StoreGuestU32(base, ref + 4u, 0u);
    StoreGuestU32(base, ref + 8u, 0u);
    StoreGuestU32(base, ref + 12u, 0u);
    ctx = saved;
    ctx.r1.u32 = frame;
    ctx.r3.u32 = 0u;
    ctx.r4.u32 = ref;
    ctx.r5.u32 = slot;
    dyed = SafeInvokeGuestCall(is_dyed, ctx, base) && (ctx.r3.u32 & 0xFFu) != 0u;
  }
  ctx = saved;
  static uint32_t words[kItemDyeLayerWords];
  const uint32_t wrote =
      dyed ? BuildItemDyeLayers(ctx, base, saved, frame, item, appearance, words, static_cast<uint32_t>(kItemDyeLayerWords))
           : 0u;
  const uint32_t offset = 0u;
  refii_bridge_set_item_dye_layers(&item, &offset, &wrote, wrote != 0u ? 1u : 0u, words);
}

/// Which pause tabs the guest would offer, by calling its own predicate for tabs 2..8.
///
/// `sub_826B83D0(unused, tab)` is what the guest's tab navigation runs; there is no availability
/// mask in the image and `MenuCycler.bgf` has no per-tab element. Its first argument is dead
/// (r3 is overwritten at 0x826B8420 before any read), so 0 is passed.
///
/// Must run on the guest thread: with a stale control handle, 0x826B845C takes `sub_82193E38`,
/// which drops a refcount, may free the control block and nulls the handle.
///
/// Sampled on the GUI tick because the game-loop tick stops while the pause book is open.
void SamplePauseTabs(PPCContext& ctx, uint8_t* base) {
  constexpr uint32_t kFirstTab = 2u;
  constexpr uint32_t kLastTab = 8u;

  uint32_t screen_id = 0;
  if (!StatedInGameScreen(&screen_id)) {
    return;
  }

  // THE PREDICATE'S CARDINAL PRECONDITION, checked here because we call it from outside the
  // game's flow. `sub_8240B2A0` (TU1 `sub_826B83D0`) picks the local player from the player
  // table -- id 1 when the net context's bytes +52 and +53 are both set, else id 2 -- takes
  // that player's entity handle at +12, and reads a flag byte at entity+144 with no null
  // check. The guest only ever runs it from pause-tab navigation, when that entity exists.
  // We run it every eighth GUI tick, and on a JOINER in the moment between the session
  // forming and the player's entity existing it read 0+144: the co-op CTD of 2026-10-07,
  // "read of guest 0x00000090" in rex_lhGuiPauseMenu_IsTabAvailable. Skipped until the
  // entity is there; the previous mask stands.
  {
    const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
    const uint32_t root = LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
    const uint32_t game = plausible(root) ? LoadGuestU32(base, root + 12u) : 0u;
    const uint32_t net = plausible(game) ? LoadGuestU32(base, game + 140u) : 0u;
    if (!plausible(net)) {
      return;
    }
    const bool both = base[net + 52u] != 0u && base[net + 53u] != 0u;
    const uint32_t player = PlayerWithId(base, both && !MenusActForPlayerTwo(base) ? 1u : 2u);
    const uint32_t block = plausible(player) ? LoadGuestU32(base, player + 12u + 4u) : 0u;
    const uint32_t entity = plausible(block) ? LoadGuestU32(base, block) : 0u;
    if (!plausible(entity)) {
      return;
    }
  }

  const PPCContext saved = ctx;

  // Player two's book asks about player two's tabs (their own restriction flags).
  g_menu_player_two = MenusActForPlayerTwo(base);
  uint32_t mask = 0u;
  for (uint32_t tab = kFirstTab; tab <= kLastTab; ++tab) {
    ctx = saved;
    ctx.r3.u32 = 0u;
    ctx.r4.u32 = tab;
    if (SafeInvokeGuestCall(rex_lhGuiPauseMenu_IsTabAvailable, ctx, base)) {
      if (ctx.r3.u32 != 0u) {
        mask |= 1u << tab;
      }
    }
  }
  g_menu_player_two = false;

  ctx = saved;

  // Tab 8 is unconditional in the predicate, so a sampled mask is never 0 and the host can use
  // 0 to mean "not sampled yet" without ambiguity.
  refii_bridge_note_pause_tabs(mask);
}

/// The attractiveness ratio as the page's writer `sub_829BA860` stated it (float bits). It is
/// not the sum of the eleven rows: the writer adds five preference axes (`sub_824C3AF8` with
/// 0, 1, 2, 4, 3) where the page displays three, plus the GDB "BalancingAugmentations" record's
/// "Attractiveness" times 100 when `sub_8223D888`/`sub_8223D570` say the hero carries it, then
/// clamps to [-100, 100] and states `(clamped + 100) * 0.0049999999` to
/// `sub_82881E38(page + 36, ratio)` at 0x829BABD0.
std::atomic<uint32_t> g_attractiveness_total_bits{0};
std::atomic<bool> g_attractiveness_total_known{false};

/// The eleven attractiveness contributions, CALLED rather than reproduced: each term is a vector
/// walk over the hero's appearance modifiers, a GDB chain (`AppearanceModifierComponent` ->
/// `DefaultMaleEffects`, overridden by `OverrideFemaleEffects` only when the female record
/// declares the property itself rather than inheriting it), a coverage weight computed against a
/// body-area table using a scratch `visited` byte, and a per-term clamp. `sub_824C3AF8` resolves
/// its `Effects` list through a runtime name registry, so it is not decidable from the image.
///
/// Gated on the logbook being up: these allocate (`sub_824C3260` copies a vector), and guest-heap
/// allocation on the tick thread is only worth paying while the page is open.
void SampleAttractiveness(PPCContext& ctx, uint8_t* base, uint32_t hero, HeroDerived& sampled) {
  constexpr uint32_t kAppearanceComponent = 15u;
  constexpr uint32_t kLogbookScreenId = 17u;

  uint32_t screen_id = 0;
  if (!StatedInGameScreen(&screen_id) || screen_id != kLogbookScreenId) {
    return;
  }

  const uint32_t look = HeroComponent(base, hero, kAppearanceComponent);
  if (look == 0u) {
    return;
  }

  const PPCContext saved = ctx;

  // `sub_824C3570(c15, kind)` — one appearance-modifier kind.
  const auto modifier = [&](uint32_t kind) {
    ctx = saved;
    ctx.r3.u32 = look;
    ctx.r4.u32 = kind;
    return SafeInvokeGuestCall(rex_CECAppearanceModifiers_GetModifierEffectTerm, ctx, base)
        ? static_cast<float>(ctx.f1.f64)
        : 0.0f;
  };

  // `sub_824C3AF8(c15, axis)` — one preference axis.
  const auto preference = [&](uint32_t axis) {
    ctx = saved;
    ctx.r3.u32 = look;
    ctx.r4.u32 = axis;
    return SafeInvokeGuestCall(rex_CECAppearanceModifiers_GetPreferenceTerm, ctx, base)
        ? static_cast<float>(ctx.f1.f64)
        : 0.0f;
  };

  // Clothing is the sum of kinds 17..23, as `sub_829BA860` stores at `screen+92`. Each kind is
  // already clamped to +/-100 by its accessor and the guest adds the clamped values.
  float clothing = 0.0f;
  for (uint32_t kind = 17u; kind <= 23u; ++kind) {
    clothing += modifier(kind);
  }

  // The page's own row order, which is the order the template names them in.
  sampled.attractiveness[0] = clothing;
  sampled.attractiveness[1] = modifier(9u);    // hair
  sampled.attractiveness[2] = modifier(10u);   // beard
  sampled.attractiveness[3] = modifier(11u);   // moustache
  sampled.attractiveness[4] = modifier(13u);   // make-up
  sampled.attractiveness[5] = modifier(12u);   // tattoos

  ctx = saved;
  ctx.r3.u32 = look;
  sampled.attractiveness[6] =
      SafeInvokeGuestCall(rex_CECAppearanceModifiers_GetPurityOpinionTerm, ctx, base)
          ? static_cast<float>(ctx.f1.f64)
          : 0.0f;  // purity/corruption

  sampled.attractiveness[7] = preference(3u);  // muscle
  sampled.attractiveness[8] = preference(1u);  // fatness
  sampled.attractiveness[9] = preference(4u);  // scars

  ctx = saved;
  ctx.r3.u32 = look;
  sampled.attractiveness[10] =
      SafeInvokeGuestCall(rex_CECAppearanceModifiers_GetHomeBonusTerm, ctx, base)
          ? static_cast<float>(ctx.f1.f64)
          : 0.0f;  // home bonus

  ctx = saved;

  // The total the page stated on its last write. Absent until the page has drawn once, which is
  // why it is a separate flag rather than a zero.
  if (g_attractiveness_total_known.load(std::memory_order_relaxed)) {
    const uint32_t bits = g_attractiveness_total_bits.load(std::memory_order_relaxed);
    std::memcpy(&sampled.attractiveness_fraction, &bits, sizeof(float));
  }
}

void SampleLogbookAttractiveness(PPCContext& ctx, uint8_t* base, bool logbook_active) {
  static bool s_sampled_this_visit = false;
  static uint32_t s_last_total_bits = 0u;
  static bool s_last_total_known = false;

  if (!logbook_active) {
    s_sampled_this_visit = false;
    return;
  }

  const bool total_known = g_attractiveness_total_known.load(std::memory_order_relaxed);
  const uint32_t total_bits = g_attractiveness_total_bits.load(std::memory_order_relaxed);
  if (s_sampled_this_visit && total_known == s_last_total_known && total_bits == s_last_total_bits) {
    return;
  }

  const uint32_t hero = HeroEntity(base);
  if (hero == 0u) {
    return;
  }

  HeroDerived sampled = HeroDerivedSnapshot();
  SampleAttractiveness(ctx, base, hero, sampled);
  PublishHeroDerived(sampled);

  s_sampled_this_visit = true;
  s_last_total_known = total_known;
  s_last_total_bits = total_bits;
}

/// The hero's inventory, walked on the guest's OWN thread and published for the host
/// (`RefiiHostGetHeroInventoryItems` serves the snapshot). The walk Godot ran per sourced entry from
/// its own thread read an MS-STL `std::list` the guest was patching: `_Mysize` is bumped before
/// the links, and an erase frees its node the moment a count reaches zero. Here nothing moves
/// under the walk. The shape is the bridge's: five words per record — {recordId, count,
/// category, viewed, item} — `+0x10` the sentinel, `node+8` the item (the instance the dye
/// tables key on), `item+4/+0xC/+0x10/+0x14` (`GetNumberOfItems`, `sub_824F23D0`); the
/// four-word snapshot is published with it. Sampled with the hero derived values: every 15 ticks
/// and on the tick after a UI change; a publish that differs bumps the generation, so a removal
/// no hook announces (a gift, a drop) still reaches the open list within the cadence.
void SamplePlayerInventories(uint8_t* base);

void SampleHeroInventory(uint8_t* base) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  std::vector<uint32_t> words;
  const uint32_t hero = HeroEntity(base);
  const uint32_t inventory = hero != 0u ? HeroComponent(base, hero, 13u) : 0u;
  if (inventory != 0u) {
    const uint32_t sentinel = LoadGuestU32(base, inventory + 0x10u);
    if (plausible(sentinel)) {
      constexpr uint32_t kMaxRecords = 4096u;
      uint32_t node = LoadGuestU32(base, sentinel);
      uint32_t found = 0u;
      while (plausible(node) && node != sentinel && found < kMaxRecords) {
        const uint32_t item = LoadGuestU32(base, node + 8u);
        if (plausible(item)) {
          words.push_back(LoadGuestU32(base, item + 0x04u));
          words.push_back(LoadGuestU32(base, item + 0x0Cu));
          words.push_back(LoadGuestU32(base, item + 0x10u));
          words.push_back(LoadGuestU8(base, item + 0x14u) != 0u ? 1u : 0u);
          words.push_back(item);
          ++found;
        }
        node = LoadGuestU32(base, node);
      }
    }
  }
  refii_bridge_publish_hero_inventory_items(words.data(), static_cast<uint32_t>(words.size() / 5u));
  SamplePlayerInventories(base);
}

/// EACH PLAYER'S INVENTORY, for a read that names its player (`RefiiHostGetPlayerInventory`): the
/// walk above on the hero `refii_mod_player_hero` resolves — the guest's own route to a player's
/// entity, `*(*(*(*(0x83496AB8)+0xC)+0x58)+4)` then `EM+104` for player one (`sub_8222C1B8`, disc
/// `sub_8222C210`) and `EM+112` for player two (`sub_828E5A78`, disc `sub_828BEEC0`), as
/// `CECMoney_Modify` reads it (TU1 0x8242E750..0x8242E75C, disc 0x8239E870..0x8239E87C). The list is
/// `GetNumberOfItems`' (TU1 `sub_824F23D0`, disc `sub_8261C0B8`: `+0x10` the sentinel, `node+8` the
/// item, `item+0xC` its count). Flags bit 0 is `Viewed` (`item+0x14`), bit 1 WORN: the record is on
/// component 15's worn vector, `+0x10`..`+0x14`, stride 0x18, element `+0` the item and `item+4` its
/// record — `IsWearingItemOfType` (TU1 `sub_824C4380` 0x824C4388..0x824C4400, disc `sub_82574F98`
/// 0x82574FA0..0x82575018). Walked here, on the guest's thread, for the reason the hero's is.
void SamplePlayerInventories(uint8_t* base) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  for (int32_t player = 1; player <= 2; ++player) {
    std::vector<uint32_t> words;
    const uint32_t hero = refii_mod_player_hero(base, player);
    const uint32_t inventory = hero != 0u ? HeroComponent(base, hero, 13u) : 0u;
    const uint32_t appearance = hero != 0u ? HeroComponent(base, hero, 15u) : 0u;

    std::vector<uint32_t> worn;
    if (appearance != 0u) {
      constexpr uint32_t kWornStride = 0x18u;
      const uint32_t begin = LoadGuestU32(base, appearance + 0x10u);
      const uint32_t end = LoadGuestU32(base, appearance + 0x14u);
      if (plausible(begin) && end > begin && (end - begin) % kWornStride == 0u &&
          (end - begin) / kWornStride <= 256u) {
        for (uint32_t at = begin; at < end; at += kWornStride) {
          const uint32_t item = LoadGuestU32(base, at);
          if (plausible(item)) {
            worn.push_back(LoadGuestU32(base, item + 0x04u));
          }
        }
      }
    }

    const uint32_t sentinel = inventory != 0u ? LoadGuestU32(base, inventory + 0x10u) : 0u;
    if (plausible(sentinel)) {
      constexpr uint32_t kMaxRecords = 4096u;
      uint32_t node = LoadGuestU32(base, sentinel);
      uint32_t found = 0u;
      while (plausible(node) && node != sentinel && found < kMaxRecords) {
        const uint32_t item = LoadGuestU32(base, node + 8u);
        if (plausible(item)) {
          const uint32_t record = LoadGuestU32(base, item + 0x04u);
          const bool on = std::find(worn.begin(), worn.end(), record) != worn.end();
          words.push_back(record);
          words.push_back(LoadGuestU32(base, item + 0x0Cu));
          words.push_back(LoadGuestU32(base, item + 0x10u));
          words.push_back((LoadGuestU8(base, item + 0x14u) != 0u ? 1u : 0u) | (on ? 2u : 0u));
          ++found;
        }
        node = LoadGuestU32(base, node);
      }
    }
    refii_bridge_publish_player_inventory(player, words.data(), static_cast<uint32_t>(words.size() / 4u));
  }
}

void SampleHeroDerived(PPCContext& ctx, uint8_t* base) {
  constexpr uint32_t kStatsComponent = 57u;

  const uint32_t hero = HeroEntity(base);
  const uint32_t stats = hero != 0u ? HeroComponent(base, hero, kStatsComponent) : 0u;
  if (stats == 0u) {
    return;
  }

  HeroDerived sampled = HeroDerivedSnapshot();
  sampled.known = true;

  const PPCContext saved = ctx;

  ctx.r3.u32 = stats;
  if (SafeInvokeGuestCall(rex_lhHeroStats_GetMorality, ctx, base)) {
    sampled.morality = static_cast<float>(ctx.f1.f64);
  }

  ctx = saved;
  ctx.r3.u32 = stats;
  if (SafeInvokeGuestCall(rex_lhHeroStats_GetPurity, ctx, base)) {
    sampled.purity = static_cast<float>(ctx.f1.f64);
  }

  ctx = saved;
  ctx.r3.u32 = stats;
  if (SafeInvokeGuestCall(rex_lhHeroStats_GetRenown, ctx, base)) {
    sampled.renown = static_cast<int32_t>(ctx.r3.u32);
  }

  ctx = saved;

  // The ranges the bands are tested against. Plain fields, so they are read.
  const auto read_float = [base](uint32_t at) {
    const uint32_t raw = LoadGuestU32(base, at);
    float value = 0.0f;
    std::memcpy(&value, &raw, sizeof(value));
    return value;
  };

  sampled.morality_min = read_float(stats + 20u);
  sampled.morality_max = read_float(stats + 24u);
  sampled.purity_min = read_float(stats + 40u);
  sampled.purity_max = read_float(stats + 44u);

  // `c57+92` is a GDB def HANDLE, not a pointer: `{ u32 recordIdHash, u32 cachedEntry }`,
  // written as one 8-byte store by `sub_824D8568`. Publish the hash; the cached pointer is
  // meaningless outside the guest's address space.
  const uint32_t title_record = LoadGuestU32(base, stats + 92u);
  std::snprintf(sampled.title_tag, sizeof(sampled.title_tag), "%08X", title_record);

  // The stat counters — a flat read, so it runs every sample.
  for (uint32_t id = 0; id < HeroDerived::kStatCount; ++id) {
    sampled.stats[id] = static_cast<int32_t>(LoadGuestU32(base, stats + 348u + 4u * id));
  }

  sampled.stats_known = true;

  SampleDog(base, sampled);
  SampleQuests(base, hero);
  SampleSpellDamage(base, hero, sampled);

  // The attractiveness block only while the logbook is up: it is the one sampler that allocates
  // from the guest heap. `sub_824C3AF8` builds an `LHString` from "PersonalityAdultBase" before
  // any guard, appends the axis name and grows and frees a vector — three heap round-trips per
  // axis — and `sub_824C4030` allocates unconditionally; all of it through `sub_8222CED0` ->
  // 0x8221F258 -> `sub_8221F2C0`. The page samples on activate, so a half-second delay after
  // opening is invisible. When the logbook is closed, `sampled` already holds the previous
  // attractiveness snapshot from `HeroDerivedSnapshot()` above.
  constexpr uint32_t kLogbookScreenId = 17u;
  uint32_t screen_id = 0;
  if (StatedInGameScreen(&screen_id) && screen_id == kLogbookScreenId) {
    SampleAttractiveness(ctx, base, hero, sampled);
  }

  PublishHeroDerived(sampled);
}

/// Commits a menu row by identity — see kActionMenuEventPrefix.
bool DispatchMenuEvent(PPCContext& ctx, uint8_t* base, const std::string& name, uint32_t type,
                       uint32_t id) {
  // The screen's own handler, called with the event. The guest's Lua event queue
  // (`guieventqueue.lua`, drained by `sub_82370450`) is cut with the rest of its UI Lua, so a
  // class this build cannot call is reported, not pushed.
  refii::vfs::SetDispatchedRow(name, static_cast<int32_t>(type), static_cast<int32_t>(id));
  const bool delivered = DispatchMenuEventDirect(ctx, base, name, type, id);
  refii::vfs::ClearDispatchedRow();
  if (!delivered) {
    REXLOG_INFO("Refii guest-tick pump: MenuEvent '{}' type={} id=0x{:08X} has no handler this "
                "build calls — not delivered",
                name, type, id);
  }
  return true;
}

/// Answers the pending query box through the guest's own input router.
///
/// The CODE is chosen host-side PER BOX ID — `sub_826A7738` answers 46/47 for the saving
/// boxes while the henchman boxes answer 36/37, the fast-travel confirm is 39, the shop's
/// equip action 75 — and the router forwards it to the UNDERLYING screen handler's
/// vtable+28, so every side effect (the save, the purchase, the kick, the close sounds)
/// runs inside the guest's own handler. Never call the close primitives
/// (`sub_8236FEC8/FE38`) instead: they only close, skipping those side effects.
///
/// Drops when stale: the post is only made while a box is up. Gated on the modal latch being
/// non-idle — modes 2 (MessageBox), 3 (QueryBox) and 5 (numeric adjuster) all answer through
/// this router, whose own guard re-checks everything inside.
///
/// `player` is the acting player, the router's r5 (`mr r27,r5` 0x8236E470 → `mr r5,r27` 0x8236E5B8
/// → the handler's vf+28): the guest passes its input object's vf+16 there (0x82404A90).
bool DispatchQueryAnswer(PPCContext& ctx, uint8_t* base, uint32_t code, bool addressed,
                         uint32_t modal_id, uint32_t player) {
  // The box that is up, as its opener stated it (display_box_hooks.cpp): the mode and the id.
  uint32_t mode = 1u;
  uint32_t stated_id = 0u;
  const bool up = refii_bridge_stated_pause(&mode, &stated_id, nullptr) != 0u;
  const uint32_t owner = up ? InGameScreenOwner(base) : 0u;  // the router is called on it
  if (owner == 0u) {
    REXLOG_INFO("Refii guest-tick pump: QueryAnswer:{} with no in-game GUI — dropped", code);
    return true;
  }

  if (mode == 1u) {    // the modal latch is idle — no box
    // The gate is the MODE alone: query id 0 is a real box (the shop's tattoo notice), so
    // "id == 0" would drop a live answer. Modes 2 (MessageBox), 3 (QueryBox) and 5 (the
    // numeric adjuster) all answer through the same router.
    REXLOG_INFO("Refii guest-tick pump: QueryAnswer:{} with no box pending — dropped", code);
    return true;
  }

  // THE ANSWER NAMES ITS BOX. The router (`sub_8236E458`) answers whatever box is up at tick
  // time, and every handler re-reads `owner+44` for the id; an answer posted for one box and
  // landing after it closed answered the next one — a save confirm's 46 dismissing its own
  // result box. The surface states the id it drew; a mismatch is dropped, not delivered.
  if (addressed && stated_id != modal_id) {
    REXLOG_INFO("Refii guest-tick pump: QueryAnswer:{}@{} — the box up is {} (mode {}), not "
                "the one answered — dropped",
                code, modal_id, stated_id, mode);
    return true;
  }

  // 32/33 release the adjuster's increment/decrement. The router hands any code to the screen's
  // vf+28, which in mode 5 passes it to the widget's input `sub_826B8EB0` (disc `sub_8240CD98`,
  // e.g. real estate 0x826B2624); its direction gate `sub_828818E8(widget+0x44)` (disc
  // `sub_82841730`) zeroes the held direction on 32..35 (`stw r9, 0(r31)` 0x82881A8C / disc
  // 0x828418D4). Without it `*(widget+0x44)` keeps the last press and the hold acceleration
  // `sub_822CF9A0` fires on every hold clock with nothing held, after the box closed too.
  if ((code == 32u || code == 33u) && mode != 5u) {
    REXLOG_INFO("Refii guest-tick pump: QueryAnswer:{} — a release with no adjuster up (mode {}) — dropped", code,
                mode);
    return true;
  }

  const PPCContext saved = ctx;
  ctx.r3.u32 = owner;
  ctx.r4.u32 = code;
  ctx.r5.u32 = player;

  rex_lhGuiManager_RouteModalInput(ctx, base);

  ctx = saved;
  return true;
}

/// Takes the whole queue under the lock and runs it outside.
///
/// Swapped rather than popped one at a time: actions make guest calls, and a host mutex must
/// never be held across guest code. A post made while the pump runs lands in the next tick.
/// Set while the drain runs actions: a redress the hooks ask for from inside one (a body
/// retype posted as an action) waits for the tick instead of nesting.
std::atomic<bool> g_pump_draining{false};

namespace {
/// The action DrainOnGuestThread is running, read on the same thread by the hooks it reaches.
std::string g_dispatching;
}  // namespace

const std::string& GuestTickPumpDispatching() { return g_dispatching; }

// --- Guest heap integrity, checked every tick --------------------------------------------
//
// THE INSTRUMENT FOR A CORRUPTION NOBODY CAN SEE IN A CRASH LOG. Three runs (2026-10-07
// 22:07, 23:31; 2026-10-08 00:33) died on both machines inside the TU1 coalescing allocator
// with its free list carrying garbage — 0x19BE as a node, then the vtable 0x82001770 as a
// block — long after the write that put it there. The allocator (`sub_832378D0` alloc,
// `sub_83237AB8` free, `MemSys_CoalesceHeap_Free`, `sub_83238460` the `lh_free` entry) is
// three heaps at `dword_83496DB0/B4/B8`. Each heap: `+4` region size, `+8` bucket count,
// `+24` the region list — a region is {base +0, bucket array +4, next +8}. A bucket is a
// list of free NODES sorted by (size, block): node {size +0, block header +4, prev +8,
// next +12}. A block header sits 16 bytes under the user pointer: {magic 0xABCDEF12 +0, its
// free node (-1 below the heap's minimum, 0 when allocated) +4, previous block's size +8,
// size +12}. SIZE COUNTS FROM THE HEADER (`sub_83237618` makes the first block
// `regionSize - 0x210` long at region+0x210, so header + size == region end); the region
// record is the first 16 bytes of the region {base, bucket array at region+16, next, size}
// and the bucket heads are per region. The walk checks
// every link both ways and every header against its node; the first tick that disagrees,
// confirmed on the next tick (other guest threads allocate under the heap lock while this
// reads without it), is reported ONCE with the actions the pump ran most recently, which is
// the attribution the crash logs could never give. Both builds: the three heap globals are
// mapped (the allocator `sub_83237F70` / unpatched `sub_832316F8` is the same code at both).

constexpr std::size_t kHeapCheckActionRing = 8u;
std::string g_heap_check_actions[kHeapCheckActionRing];
std::size_t g_heap_check_action_next = 0u;

void HeapCheckNoteAction(const std::string& name) {
  g_heap_check_actions[g_heap_check_action_next % kHeapCheckActionRing] =
      name.size() > 96u ? name.substr(0, 96u) + "..." : name;
  ++g_heap_check_action_next;
}

struct HeapViolation {
  uint32_t heap = 0u;
  uint32_t bucket = 0u;
  uint32_t node = 0u;
  const char* what = "";
};

/// Walks one heap; returns the first violation or an empty one. `steps` is a budget.
HeapViolation WalkGuestHeap(uint8_t* base, uint32_t heap_global, uint32_t heap_index, uint32_t& steps) {
  HeapViolation none;
  const auto plausible = [](uint32_t a) { return a >= 0x10000u && a < 0xC0000000u; };
  const uint32_t heap = LoadGuestU32(base, heap_global);
  if (!plausible(heap)) return none;
  const uint32_t region_size = LoadGuestU32(base, heap + 4u);
  const uint32_t buckets = LoadGuestU32(base, heap + 8u);
  if (buckets == 0u || buckets > 4096u) return none;
  uint32_t region = LoadGuestU32(base, heap + 24u);
  for (uint32_t r = 0; plausible(region) && r < 64u; ++r, region = LoadGuestU32(base, region + 8u)) {
    const uint32_t region_base = LoadGuestU32(base, region);
    const uint32_t bucket_array = LoadGuestU32(base, region + 4u);
    if (!plausible(region_base) || !plausible(bucket_array)) continue;
    const uint32_t region_end = region_base + region_size;
    for (uint32_t b = 0; b < buckets; ++b) {
      uint32_t node = LoadGuestU32(base, bucket_array + 4u * b);
      uint32_t prev = 0u;
      for (uint32_t n = 0; node != 0u && n < 65536u; ++n) {
        if (steps-- == 0u) return none;
        HeapViolation v;
        v.heap = heap_index;
        v.bucket = b;
        v.node = node;
        if (!plausible(node) || (node & 3u) != 0u) { v.what = "node pointer outside the guest or unaligned"; return v; }
        const uint32_t size = LoadGuestU32(base, node);
        const uint32_t block = LoadGuestU32(base, node + 4u);
        const uint32_t node_prev = LoadGuestU32(base, node + 8u);
        const uint32_t next = LoadGuestU32(base, node + 12u);
        if (node_prev != prev) { v.what = "prev link does not point back"; return v; }
        if (block < region_base + 16u + 4u * buckets || block + 16u > region_end) { v.what = "block outside the region"; return v; }
        if (LoadGuestU32(base, block) != 0xABCDEF12u) { v.what = "block header magic"; return v; }
        if (LoadGuestU32(base, block + 4u) != node) { v.what = "block header does not name this node"; return v; }
        if (LoadGuestU32(base, block + 12u) != size) { v.what = "block header size differs from the node's"; return v; }
        if (size < 16u || (size & 15u) != 0u || block + size > region_end) { v.what = "block size runs past the region"; return v; }
        prev = node;
        node = next;
      }
    }
  }
  return none;
}

/// The first step a walk found the heaps broken after, and the node it saw — attribution for the
/// tick report below. A walk takes no heap lock, so a suspect is reported only when the confirmed
/// node is the same one.
std::string g_heap_suspect;
uint32_t g_heap_suspect_node = 0u;
bool g_heap_reported = false;

/// Walks the heaps after one step of guest work (a pump action, a hero-apply step) and records
/// the first step after which they are broken.
void HeapCheckAfter(uint8_t* base, const char* step) {
  if (g_heap_reported || !g_heap_suspect.empty()) {
    return;
  }
  static const uint32_t kHeaps[3] = {0x83496DB0u, 0x83496DB4u, 0x83496DB8u};
  uint32_t steps = 200000u;
  HeapViolation found;
  for (uint32_t i = 0; i < 3u && found.what[0] == '\0'; ++i) {
    found = WalkGuestHeap(base, refii::guest::Address(kHeaps[i]), i, steps);
  }
  if (found.what[0] != '\0') {
    g_heap_suspect = std::string(step).substr(0, 96u);
    g_heap_suspect_node = found.node;
  }
}

void CheckGuestHeaps(uint8_t* base, uint32_t tick) {
  static const bool enabled = ReadEnvFlag("REFII_CHECK_GUEST_HEAPS", false);
  bool& reported = g_heap_reported;
  static HeapViolation pending;
  static uint32_t pending_tick = 0u;
  if (!enabled || reported) {
    return;
  }

  static const uint32_t kHeaps[3] = {0x83496DB0u, 0x83496DB4u, 0x83496DB8u};
  uint32_t steps = 200000u;
  HeapViolation found;
  for (uint32_t i = 0; i < 3u && found.what[0] == '\0'; ++i) {
    found = WalkGuestHeap(base, refii::guest::Address(kHeaps[i]), i, steps);
  }

  if (found.what[0] == '\0') {
    pending = HeapViolation{};
    return;
  }

  // Confirmed only when the same node is wrong on two consecutive ticks.
  if (pending.node != found.node || pending.what != found.what || tick != pending_tick + 1u) {
    pending = found;
    pending_tick = tick;
    return;
  }

  reported = true;
  std::string actions;
  for (std::size_t i = 0; i < kHeapCheckActionRing; ++i) {
    const std::string& a = g_heap_check_actions[(g_heap_check_action_next + i) % kHeapCheckActionRing];
    if (!a.empty()) actions += " [" + a + "]";
  }
  const uint32_t node = found.node;
  const auto word = [&](uint32_t at) { return LooksLikeGuestPointer(at) || at < 0x10000u ? LoadGuestU32(base, at) : 0u; };
  REXLOG_INFO("Refii HEAP CORRUPTION at tick {} — heap {} bucket {} node 0x{:08X}: {} (node words "
              "{:08X} {:08X} {:08X} {:08X}); first seen after: {}; last actions ran:{}",
              tick, found.heap, found.bucket, node, found.what, word(node), word(node + 4u),
              word(node + 8u), word(node + 12u),
              g_heap_suspect_node == node ? g_heap_suspect.c_str() : "(no step walk saw this node)",
              actions.empty() ? " (none)" : actions.c_str());
}

PPC_EXTERN_FUNC(rex_CECExpressionPerformer_StartExpression);

/// One quick action for player `player`'s hero, the sequence the game's own quick menu runs
/// (refii_mod_use_item / equip_weapon / perform_expression / assign_spell in core/refii_mod.h):
///   kind 1  use the inventory item with record `record`: the item handle by record
///           (`sub_824F1C90(out, inventory, record)`), `sub_823E0FF8(hero, handle, "")` uses it,
///           `sub_82764DC0(item, handle)` releases the handle, and the UI data generation moves.
///   kind 2  perform the expression `record` the performer (component 154) knows: its +0x88
///           vector of 20-byte entries {record, cached entry, _, uses, recency}; the game's own
///           StartExpression(performer, {record, entry}, 0). Refused while +20 says one plays.
///   kind 3  equip the weapon with record `record` from the inventory: the weapon set's own
///           EquipWeaponByRecord(comp28, record, 0) — on an online JOINER the pick goes to Godot
///           instead (REFII_MOD_HOST_HERO_KIND: CoopHeroKit.SetLocalWeapon), where the kit carries
///           it to the host, who equips it and replicates the held item back.
///   kind 4  assign spell type `record` (0..7) to every tier the hero knows (the manager's +156
///           known and +196 offered bytes, index 5*spell+tier) through DispatchSpellAssign.
/// Gated as the quick menu is: the hero alive (+144 & 0x40), the d-pad up (hud+56 → +32), and
/// `rex_lhGuiManager_CanPlayerOpenQuickMenu(quick, player)`. True when the action was performed.
/// `flags` (core/refii_mod.h REFII_QUICK_*): UNGATED keeps only the hero alive and no cutscene
/// (refii_mod_in_cutscene); HELD (kind 2) leaves the expression unreleased — without it the start
/// is followed by the performer's own end (`sub_82414A50`, GameAction 25's call), the wheel's tap.
bool PerformQuickslot(PPCContext& ctx, uint8_t* base, uint32_t player, uint32_t kind, uint32_t record, uint32_t flags) {
  if (player < 1 || player > 2 || kind < 1 || kind > 4 || (kind == 4 ? record >= 8 : record == 0)) return false;
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  const uint32_t hero = refii_mod_player_hero(base, player);
  const uint32_t owner = PlayerWithId(base, player);
  const uint32_t gui = LoadGuestU32(base, kGuiObject);
  const uint32_t quick = plausible(gui) ? LoadGuestU32(base, gui + 28u) : 0u;
  const uint32_t hud = plausible(gui) ? LoadGuestU32(base, gui + 32u) : 0u;
  const uint32_t dpad = plausible(hud) ? LoadGuestU32(base, hud + 56u) : 0u;
  const bool ungated = (flags & REFII_QUICK_UNGATED) != 0u;
  if (!plausible(hero) || (LoadGuestU8(base, hero + 144u) & 0x40u) == 0u) return false;
  const PPCContext saved = ctx;
  if (ungated) {
    if (refii_mod_in_cutscene(&ctx, base, static_cast<int32_t>(player)) != 0) return false;
  } else {
    if (!plausible(owner) || !plausible(quick) || !plausible(dpad) || LoadGuestU8(base, dpad + 32u) == 0u) return false;
    ctx.r3.u32 = quick;
    ctx.r4.u32 = owner;
    rex_lhGuiManager_CanPlayerOpenQuickMenu(ctx, base);
    const bool allowed = (ctx.r3.u32 & 0xFFu) != 0u;
    ctx = saved;
    if (!allowed) return false;
  }
  const uint32_t frame = (saved.r1.u32 - 0x200u) & ~0xFu;
  bool done = false;
  if (kind == 4) {
    const uint32_t manager = EntityComponent(base, hero, 56u);
    if (plausible(manager)) {
      for (uint32_t tier = 0; tier < 5; ++tier) {
        const uint32_t index = 5u * record + tier;
        if (LoadGuestU8(base, manager + 156u + index) && LoadGuestU8(base, manager + 196u + index)) {
          DispatchSpellAssign(ctx, base, tier, record, player);
          done = true;
        }
      }
    }
  } else if (kind == 2) {
    const uint32_t performer = EntityComponent(base, hero, 154u);
    if (!plausible(performer) || LoadGuestU8(base, performer + 20u) != 0u) return false;
    const uint32_t begin = LoadGuestU32(base, performer + 0x88u);
    const uint32_t end = LoadGuestU32(base, performer + 0x8Cu);
    if (!plausible(begin) || end < begin || end - begin > 20u * 512u || (end - begin) % 20u != 0u) return false;
    for (uint32_t at = begin; at < end; at += 20u) {
      if (LoadGuestU32(base, at) != record) continue;
      const uint32_t pair = frame + 0x60u;
      StoreGuestU32(base, pair, record);
      StoreGuestU32(base, pair + 4u, LoadGuestU32(base, at + 4u));
      ctx.r1.u32 = frame;
      ctx.r3.u32 = performer;
      ctx.r4.u32 = pair;
      ctx.r5.u32 = 0u;
      rex_CECExpressionPerformer_StartExpression(ctx, base);
      done = (ctx.r3.u32 & 0xFFu) != 0u;
      ctx = saved;
      if (done && (flags & REFII_QUICK_HELD) == 0u) {
        CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x82414A50u), performer);
      }
      break;
    }
  } else {
    const uint32_t inventory = EntityComponent(base, hero, 13u);
    if (!plausible(inventory)) return false;
    const uint32_t handle = frame + 0x60u;
    for (uint32_t word = 0; word < 4; ++word) StoreGuestU32(base, handle + word * 4u, 0u);
    CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x824F1C90u), handle, inventory, record);
    const uint32_t item = LoadGuestU32(base, handle);
    if (!plausible(item)) return false;
    if (kind == 3) {
      const uint32_t weapons = EntityComponent(base, hero, 28u);
      if (IsCoopJoiner(base)) {
        // The joiner's weapon set is the host's replica: the pick crosses to Godot's CoopHeroKit,
        // whose kit the host equips from (CoopHeroHeld brings the held item back).
        char fields[64];
        std::snprintf(fields, sizeof(fields), "player=%u,op='weapon',record='0x%08x'", player, record);
        refii::mods::PostEvent(REFII_MOD_HOST_HERO_KIND, fields);
        done = true;
      } else if (plausible(weapons)) {
        if (HeldItemWithRecord(base, hero, record) == 0u) {
          ctx.r1.u32 = frame;
          ctx.r3.u32 = weapons;
          ctx.r4.u32 = record;
          ctx.r5.u32 = 0u;
          rex_CECWeaponSet_EquipWeaponByRecord(ctx, base);
        }
        done = true;
      }
      ctx = saved;
    } else {
      const uint32_t text = frame + 0x80u;
      ctx.r1.u32 = frame;
      ctx.r3.u32 = text;
      ctx.r4.u32 = PlaceGuestString(base, frame + 0xA0u, "");
      ctx.r5.s32 = -1;
      rex_lhCharString_CtorFromCStr(ctx, base);
      CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x823E0FF8u), hero, handle, text);
      ctx = saved;
      ctx.r1.u32 = frame;
      ctx.r3.u32 = text;
      rex_lhCharString_Dtor(ctx, base);
      ctx = saved;
      done = true;
    }
    // THE REF IS RELEASED ON THE ITEM IT NOW NAMES, AND ONLY IF ONE IS LEFT. The ref is registered
    // on the item (sub_82764D00) and rebound when the use splits a stack, so the item to release
    // against is the ref's current word, not the one looked up above. A use that consumed the last
    // unit destroys the object, which unregisters and zeroes the ref: there is nothing to release,
    // and sub_82764DC0(0, ref) reads the null item's ref list at +0x2C — the crash two players sent
    // from beta 6a (sub_82764DC0+0x6D, access 0x2C) after using up a potion from a quick slot.
    const uint32_t remaining = LoadGuestU32(base, handle);
    if (plausible(remaining)) {
      CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x82764DC0u), remaining, handle);
    }
    refii_bridge_ui_data_changed();
  }
  ctx = saved;
  return done;
}

enum class DrainPass { kTick, kGui, kInput };

/// Whether the in-game GUI is up: its init stated the screen set (rex_lhGuiScreenSet_ShowRoot)
/// and its Gameface has not been destroyed since.
bool InGameGuiUp() { return refii_bridge_stated_pause(nullptr, nullptr, nullptr) != 0u; }

/// One pass over the queue. kTick is the tick-delegate pass (the simulation's actions: inventory,
/// abilities, co-op, the front end); kGui the GUI-tick pass inside the Lua window
/// (IsInGameUiAction); kInput the game-flow tick's entry, where the guest's input dispatch has just
/// run (IsInputAction). An action for another pass is put back untouched. An in-game action posted
/// with no in-game GUI up is dropped here rather than held: held, it fired the moment the next game
/// came up.
void DrainOnGuestThread(PPCContext& ctx, uint8_t* base, DrainPass pass) {
  const uint32_t taken = GuestTickPumpBeginDrain();
  if (taken == 0) {
    return;
  }

  const bool gui_up = InGameGuiUp();

  struct Draining {
    Draining() { g_pump_draining.store(true, std::memory_order_relaxed); }
    ~Draining() { g_pump_draining.store(false, std::memory_order_relaxed); }
  } draining;

  for (uint32_t slot = 0; slot < taken; ++slot) {
    const std::string name = GuestTickPumpTakenAction(slot);
    bool ran = false;

    const bool input_action = IsInputAction(name);
    const bool ui_action = IsInGameUiAction(name);
    if ((ui_action || input_action) && !gui_up && !IsCoopScreenInput(name)) {
      REXLOG_INFO("Refii guest-tick pump: '{}' posted with no in-game GUI up — dropped", name);
      GuestTickPumpNoteRan();
      continue;
    }
    const DrainPass wanted = input_action ? DrainPass::kInput : ui_action ? DrainPass::kGui : DrainPass::kTick;
    if (wanted != pass) {
      GuestTickPumpDeferAction(name);
      continue;
    }

    // Named for the hooks the dispatch below reaches (GuestTickPumpDispatching), cleared with
    // the slot however the dispatch leaves.
    struct Dispatching {
      explicit Dispatching(const std::string& action) { g_dispatching = action; }
      ~Dispatching() { g_dispatching.clear(); }
    } dispatching(name);

    // Logged before the call, not only after it: everything below dispatches into the guest on
    // its own thread, so an action that never returns would otherwise log nothing. The last
    // `dispatching '<name>'` with no matching `ran` names the action the thread is parked in.
    //
    // A Coop* field is restated every few seconds; it logs at INFO only when its text differs
    // from the last text seen under its prefix (through the player digit, so `:1:` and `:2:`
    // are separate), else at debug. A HeroState is one versioned payload, logged by its
    // prefix, player and byte count — never the base64 body.
    bool quiet = false;
    std::string shown = name;
    if (name.rfind("Coop", 0) == 0) {
      static std::map<std::string, std::string> coop_last;
      std::size_t cut = name.find(':');
      if (cut != std::string::npos) {
        const std::size_t second = name.find(':', cut + 1u);
        cut = second != std::string::npos ? second : cut;
      }
      const std::string prefix = cut == std::string::npos ? name : name.substr(0, cut + 1u);
      std::string& last = coop_last[prefix];
      quiet = last == name;
      last = name;
    } else if (name.rfind(kActionHeroStatePrefix, 0) == 0) {
      const std::size_t body = name.find(':', std::strlen(kActionHeroStatePrefix));
      shown = (body == std::string::npos ? name : name.substr(0, body + 1u)) + "<" +
              std::to_string(body == std::string::npos ? 0u : name.size() - body - 1u) +
              " base64 chars>";
    }
    if (quiet) {
      REXLOG_DEBUG("Refii guest-tick pump: dispatching '{}' ({} of {} this tick)",
                   shown, slot + 1, taken);
    } else {
      REXLOG_INFO("Refii guest-tick pump: dispatching '{}' ({} of {} this tick)",
                  shown, slot + 1, taken);
    }

    // Dispatch BY NAME: the guest's front-end dispatcher is a strcmp chain over these strings,
    // so an entry the guest never had simply never matches. The two new-game names are the
    // exception — the guest's "New Game" branch only opens the gender card, which Godot draws
    // itself, so these carry the ANSWER straight to the commit.
    if (name == kActionWorldHoldOn || name == kActionWorldHoldOff) {
      ran = WorldHold(ctx, base, name == kActionWorldHoldOn);
    } else if (name == "New Game") {
      ran = StartNewGame(ctx, base, /*female=*/false);
    } else if (name == "New Game Female") {
      ran = StartNewGame(ctx, base, /*female=*/true);
    } else if (name.rfind(kActionPresentationEventPrefix, 0) == 0) {
      ran = refii::vfs::PostPresentationEvent(
          ctx, base, name.substr(std::strlen(kActionPresentationEventPrefix)));
    } else if (name.rfind(kActionLanguagePrefix, 0) == 0) {
      // The name carries the index; the dispatcher takes it from `a2+8` and the name is never
      // looked at on this path, so it is left empty rather than sent as "Language:3".
      const std::string index = name.substr(std::strlen(kActionLanguagePrefix));
      char* end = nullptr;
      const unsigned long value = std::strtoul(index.c_str(), &end, 10);
      if (end == index.c_str() || *end != '\0') {
        REXLOG_INFO("Refii guest-tick pump: '{}' has no language index — dropped", name);
        GuestTickPumpNoteRan();
        continue;
      }

      ran = DispatchByName(ctx, base, std::string(), kCommandSetLanguage,
                           static_cast<uint32_t>(value));
    } else if (name.rfind(kActionLoadSavePrefix, 0) == 0) {
      const std::string index = name.substr(std::strlen(kActionLoadSavePrefix));
      char* end = nullptr;
      const unsigned long value = std::strtoul(index.c_str(), &end, 10);
      if (end == index.c_str() || *end != '\0') {
        REXLOG_INFO("Refii guest-tick pump: '{}' has no save index — dropped", name);
        GuestTickPumpNoteRan();
        continue;
      }

      ran = LoadSavedGame(ctx, base, static_cast<uint32_t>(value));
    } else if (name.rfind(kActionSpellAssignPrefix, 0) == 0) {
      // Two numbers, `slot:spell`, and an optional third, the player (1 by default; 2 the co-op
      // partner) — see kActionSpellAssignPrefix.
      const std::string pair = name.substr(std::strlen(kActionSpellAssignPrefix));
      const std::size_t colon = pair.find(':');
      char* slotEnd = nullptr;
      char* spellEnd = nullptr;
      unsigned long slot = 0;
      unsigned long spell = 0;
      unsigned long player = 1;
      if (colon != std::string::npos) {
        const std::string slotText = pair.substr(0, colon);
        std::string spellText = pair.substr(colon + 1);
        const std::size_t second = spellText.find(':');
        if (second != std::string::npos) {
          char* playerEnd = nullptr;
          const std::string playerText = spellText.substr(second + 1);
          player = std::strtoul(playerText.c_str(), &playerEnd, 10);
          if (playerEnd == playerText.c_str() || *playerEnd != '\0') {
            player = 0;  // out of range: dropped below
          }
          spellText = spellText.substr(0, second);
        }
        slot = std::strtoul(slotText.c_str(), &slotEnd, 10);
        spell = std::strtoul(spellText.c_str(), &spellEnd, 10);
        if (slotEnd == slotText.c_str() || *slotEnd != '\0' ||
            spellEnd == spellText.c_str() || *spellEnd != '\0') {
          slotEnd = nullptr;
        }
      }

      if (colon == std::string::npos || slotEnd == nullptr) {
        REXLOG_INFO("Refii guest-tick pump: '{}' is not slot:spell[:player] — dropped", name);
        GuestTickPumpNoteRan();
        continue;
      }

      ran = DispatchSpellAssign(ctx, base, static_cast<uint32_t>(slot),
                                static_cast<uint32_t>(spell), static_cast<uint32_t>(player));
    } else if (name.rfind("debug-call:", 0) == 0) {
      // `debug-call:ADDR[:r3[:r4[:r5[:r6]]]]` — one guest function, called on the guest's own
      // tick thread with the registers stated, result r3 recorded for the host. The debug
      // channel's REPL primitive; only the loopback channel posts these.
      std::vector<uint64_t> values;
      std::size_t from = std::strlen("debug-call:");
      bool parsed = true;
      while (from <= name.size()) {
        const std::size_t colon = name.find(':', from);
        const std::string part = name.substr(
            from, colon == std::string::npos ? std::string::npos : colon - from);
        char* end = nullptr;
        values.push_back(std::strtoull(part.c_str(), &end, 16));
        if (end == part.c_str() || *end != '\0') {
          parsed = false;
          break;
        }
        if (colon == std::string::npos) {
          break;
        }
        from = colon + 1;
      }

      if (!parsed || values.empty() || values.size() > 5) {
        NoteDebugCallResult(0, 0, "debug-call needs ADDR[:r3[:r4[:r5[:r6]]]], all hex");
      } else {
        const uint32_t target = static_cast<uint32_t>(values[0]);
        PPCFunc* function = FindGuestFunction(target);
        if (function == nullptr) {
          NoteDebugCallResult(target, 0, "no recompiled function at that address");
        } else {
          const PPCContext saved = ctx;
          if (values.size() > 1) ctx.r3.u64 = values[1];
          if (values.size() > 2) ctx.r4.u64 = values[2];
          if (values.size() > 3) ctx.r5.u64 = values[3];
          if (values.size() > 4) ctx.r6.u64 = values[4];
          function(ctx, base);
          const uint64_t result = ctx.r3.u64;
          ctx = saved;
          NoteDebugCallResult(target, result, "ok");
        }
      }
      ran = true;
    } else if (name == "RefiiNewGame") {
      const bool luaRan = RunNewGameTransition(ctx, base);
      NoteDebugCallResult(0, luaRan ? 1u : 0u, "RefiiNewGame");
      ran = true;
    } else if (name.rfind("debug-gamelua:", 0) == 0) {
      // The same probe against the GAME VM, where the shipped game scripts and mods' own Lua
      // live. It answers one bit: whether the chunk ran without error -- so
      // `assert(SoulsCombat.LockLatched)` reads a value out.
      const bool luaRan = RunGameLua(ctx, base, name.substr(std::strlen("debug-gamelua:")));
      NoteDebugCallResult(0, luaRan ? 1u : 0u,
                          luaRan ? "ok" : "error, false assertion, or no game VM");
      ran = true;
    } else if (name.rfind("debug-lua:", 0) == 0) {
      // The guest's GUI Lua is cut (rex_lhGui_RunLuaString): no presentation carries a script
      // library to ask. The game VM's probe is `debug-gamelua:`.
      NoteDebugCallResult(0, 0u, "the guest's GUI Lua is cut — use debug-gamelua");
      ran = true;
    } else if (name.rfind(kActionMenuEventPrefix, 0) == 0) {
      // `MenuEvent:<name>:<type>:<id>` — the name may itself contain no colon, so the two
      // numbers are taken from the END and everything before them is the name.
      const std::string body = name.substr(std::strlen(kActionMenuEventPrefix));
      const std::size_t lastColon = body.rfind(':');
      const std::size_t firstColon =
          lastColon == std::string::npos ? std::string::npos : body.rfind(':', lastColon - 1);
      if (lastColon == std::string::npos || firstColon == std::string::npos) {
        REXLOG_INFO("Refii guest-tick pump: '{}' is not name:type:id — dropped", name);
        GuestTickPumpNoteRan();
        continue;
      }

      const std::string typeText = body.substr(firstColon + 1, lastColon - firstColon - 1);
      const std::string idText = body.substr(lastColon + 1);
      char* typeEnd = nullptr;
      char* idEnd = nullptr;
      const unsigned long type = std::strtoul(typeText.c_str(), &typeEnd, 10);
      const unsigned long id = std::strtoul(idText.c_str(), &idEnd, 10);
      if (typeEnd == typeText.c_str() || *typeEnd != '\0' ||
          idEnd == idText.c_str() || *idEnd != '\0') {
        REXLOG_INFO("Refii guest-tick pump: '{}' has no type/id — dropped", name);
        GuestTickPumpNoteRan();
        continue;
      }

      ran = DispatchMenuEvent(ctx, base, body.substr(0, firstColon),
                              static_cast<uint32_t>(type), static_cast<uint32_t>(id));
    } else if (name == kActionCoopHeroReset) {
      CoopResetSession();
      REXLOG_INFO("Refii guest-tick pump: CoopHeroReset — hero states, outfits, inventories, "
                  "held pairs, the own-hero bit and every latch dropped");
      ran = true;
    } else if (name.rfind(kActionHeroStatePrefix, 0) == 0) {
      ran = DispatchHeroState(name.substr(std::strlen(kActionHeroStatePrefix)));
    } else if (name.rfind(kActionCoopHeroHeldPrefix, 0) == 0) {
      ran = DispatchCoopHeroHeld(name.substr(std::strlen(kActionCoopHeroHeldPrefix)));
    } else if (name == kActionCoopLeave) {
      ran = DispatchCoopLeave(ctx, base);
    } else if (name == kActionCoopKick) {
      ran = DispatchCoopKick(ctx, base);
    } else if (name.rfind("Property:repair:", 0) == 0) {
      // `Property:repair:<player>:<building uid hex>` — the world map's property page: the
      // player's own building repaired, Fable 3's bill charged (hooks/src/property_hooks.cpp).
      unsigned player = 0;
      unsigned long long uid = 0;
      if (std::sscanf(name.c_str() + std::strlen("Property:repair:"), "%u:%llx", &player, &uid) == 2 &&
          (player == 1u || player == 2u)) {
        const uint32_t hero = refii_mod_player_hero(base, static_cast<int32_t>(player));
        const int32_t outcome = hero != 0u ? RefiiPropertyRepair(ctx, base, hero, uid) : 0;
        refii_bridge_property_notice_post_kind(outcome > 0 ? 1 : outcome < 0 ? 2 : 3, uid);
        refii_bridge_ui_data_changed();
        REXLOG_INFO("Refii guest-tick pump: Property:repair player {} building {:016X} — {}", player, uid,
                    outcome > 0 ? "repaired" : outcome < 0 ? "not enough gold" : "nothing to repair");
      }
      ran = true;
    } else if (name.rfind("Property:repairall:", 0) == 0) {
      // `Property:repairall:<player>` — the world map's Repair All: every building of the player's
      // below full condition, cheapest first, while the gold lasts. One notice (kind 7) carries the
      // outcome packed: player << 56 | still worn << 48 | repaired << 32 | gold spent.
      unsigned player = 0;
      if (std::sscanf(name.c_str() + std::strlen("Property:repairall:"), "%u", &player) == 1 &&
          (player == 1u || player == 2u)) {
        const uint32_t hero = refii_mod_player_hero(base, static_cast<int32_t>(player));
        int32_t repaired = 0;
        int32_t spent = 0;
        int32_t left = 0;
        if (hero != 0u) {
          RefiiPropertyRepairAll(ctx, base, hero, &repaired, &spent, &left);
        }
        const uint64_t packed = (static_cast<uint64_t>(player) << 56) |
                                (static_cast<uint64_t>(std::min(left, 255)) << 48) |
                                (static_cast<uint64_t>(std::min(repaired, 65535)) << 32) |
                                static_cast<uint32_t>(spent);
        refii_bridge_property_notice_post_kind(7, packed);
        refii_bridge_ui_data_changed();
        REXLOG_INFO("Refii guest-tick pump: Property:repairall player {} — {} repaired for {} gold, {} left", player,
                    repaired, spent, left);
      }
      ran = true;
    } else if (name.rfind("Estate:", 0) == 0) {
      // `Estate:<action>:<sign uid hex>:<value>` — an online joiner's real-estate action for its
      // hero two, run here on the host's (mod_events.cpp ReplayEstateIntent).
      refii::vfs::ReplayEstateIntent(ctx, base, name.substr(std::strlen("Estate:")));
      ran = true;
    } else if (name.rfind(kActionModWeatherPrefix, 0) == 0) {
      refii::vfs::RaiseDynamicWeather(ctx, base, name.substr(std::strlen(kActionModWeatherPrefix)));
      ran = true;
    } else if (name.rfind(kActionCoopExitUsePrefix, 0) == 0) {
      float at[3] = {0.0f, 0.0f, 0.0f};
      const std::string rest = name.substr(std::strlen(kActionCoopExitUsePrefix));
      if (std::sscanf(rest.c_str(), "%f:%f:%f", &at[0], &at[1], &at[2]) == 3) {
        g_coop_exit_use.pending = true;
        std::memcpy(g_coop_exit_use.at, at, sizeof(at));
        g_coop_exit_use.frame = LoadGuestU32(base, refii::guest::Address(0x83319650u));
        REXLOG_INFO("Refii guest-tick pump: the joiner used the exit at ({:.1f}, {:.1f}, {:.1f})",
                    at[0], at[1], at[2]);
      }
      ran = true;
    } else if (name.rfind(kActionCoopSplitScreenPrefix, 0) == 0) {
      const bool on = name.substr(std::strlen(kActionCoopSplitScreenPrefix)) == "1";
      if (on != g_coop_split_screen.exchange(on, std::memory_order_relaxed)) {
        REXLOG_INFO("Refii guest-tick pump: local co-op on {} screen{}", on ? "split" : "a shared",
                    on && RefiiCoopPlayerTwoNode() != 0 && RefiiCoopSplitScreen() == 0
                        ? " wanted; player two's camera comes with their next join"
                        : "");
      }
      ran = true;
    } else if (IsCoopScreenInput(name)) {
      // `CoopAction:<code>:<player>`.
      const std::string rest = name.substr(name.find(':') + 1u);
      const std::size_t colon = rest.find(':');
      const std::string first = colon == std::string::npos ? rest : rest.substr(0, colon);
      const std::string second = colon == std::string::npos ? std::string() : rest.substr(colon + 1u);
      char* first_end = nullptr;
      char* second_end = nullptr;
      const unsigned long a = std::strtoul(first.c_str(), &first_end, 10);
      const unsigned long b = std::strtoul(second.c_str(), &second_end, 10);
      const bool parsed = !first.empty() && *first_end == '\0' && !second.empty() && *second_end == '\0';
      if (!parsed || a < 28u || a > 39u || (b != 1u && b != 2u)) {
        REXLOG_INFO("Refii guest-tick pump: '{}' is not a co-op screen press — dropped", name);
        GuestTickPumpNoteRan();
        continue;
      }
      ran = DispatchCoopAction(ctx, base, static_cast<uint32_t>(a), static_cast<uint32_t>(b));
    } else if (name.rfind(kActionCoopPickSavePrefix, 0) == 0) {
      ran = DispatchCoopPick(ctx, base, name.substr(std::strlen(kActionCoopPickSavePrefix)), true);
    } else if (name.rfind(kActionCoopPickCursorPrefix, 0) == 0) {
      ran = DispatchCoopPick(ctx, base, name.substr(std::strlen(kActionCoopPickCursorPrefix)), false);
    } else if (name.rfind(kActionCoopHeroBodyPrefix, 0) == 0) {
      ran = DispatchCoopHeroBody(ctx, base,
                                 name.substr(std::strlen(kActionCoopHeroBodyPrefix)));
    } else if (name.rfind(kActionQuickWheelPrefix, 0) == 0) {
      ran = DispatchQuickWheel(ctx, base, name.substr(std::strlen(kActionQuickWheelPrefix)));
    } else if (name.rfind(kActionSpellBladePrefix, 0) == 0) {
      ran = DispatchSpellBlade(ctx, base, name.substr(std::strlen(kActionSpellBladePrefix)));
    } else if (name.rfind(kActionSpellsMenuPrefix, 0) == 0) {
      ran = DispatchSpellsMenu(ctx, base, name.substr(std::strlen(kActionSpellsMenuPrefix)));
    } else if (name.rfind(kActionInventoryPrefix, 0) == 0) {
      ran = DispatchInventoryAction(ctx, base,
                                    name.substr(std::strlen(kActionInventoryPrefix)), 0);
      refii_bridge_ui_data_changed();
    } else if (name.rfind(kActionItemsPrefix, 0) == 0 || name.rfind(kActionClothingPrefix, 0) == 0) {
      // Only on the book the lever names, with no box up (the consequences' own gate, `QM+40 == 0`).
      const bool clothing = name.rfind(kActionClothingPrefix, 0) == 0;
      std::string rest = name.substr(std::strlen(clothing ? kActionClothingPrefix : kActionItemsPrefix));
      const uint32_t player = TakeActingPlayer(rest);
      const size_t colon = rest.find(':');
      // `Clothing:<p>:dye2` names no row: the held dye on the highlighted target's second layer.
      const bool dye2 = clothing && rest == "dye2";
      const std::string verb = dye2 ? rest : colon == std::string::npos ? std::string() : rest.substr(0, colon);
      unsigned long ticket = 0ul;
      unsigned long value = dye2 ? 1ul : 0ul;
      const char* numbers = colon == std::string::npos ? "" : rest.c_str() + colon + 1;
      const bool remove = clothing && verb == "remove";
      const bool parsed = dye2 || (remove ? std::sscanf(numbers, "%lu", &value) == 1
                                          : std::sscanf(numbers, "%lu:%lu", &ticket, &value) == 2);
      const bool known =
          clothing ? (verb == "wear" || verb == "outfit" || remove || dye2) : (verb == "use" || verb == "equip");
      if (player == 0u || !known || !parsed || value == 0ul) {
        REXLOG_INFO("Refii guest-tick pump: '{}' is not {} — dropped", name,
                    clothing ? "Clothing:<p>:wear|outfit:<ticket>:<record>, Clothing:<p>:remove:<type> or "
                               "Clothing:<p>:dye2"
                             : "Items:<p>:use|equip:<ticket>:<record>");
        GuestTickPumpNoteRan();
        continue;
      }
      uint32_t modal = 0u;
      const bool up = refii_bridge_stated_pause(nullptr, &modal, nullptr) != 0u;
      if (!up || modal != 0u || refii_bridge_stated_screen_now() != (clothing ? 16u : 15u)) {
        REXLOG_INFO("Refii guest-tick pump: '{}' refused (screen {}, modal {})", name,
                    refii_bridge_stated_screen_now(), modal);
        GuestTickPumpNoteRan();
        continue;
      }
      ran = DispatchItemLever(ctx, base, clothing, player, verb, static_cast<uint32_t>(ticket),
                              static_cast<uint32_t>(value));
      if (!clothing) {
        RestateItemsList(ctx, base);
      }
      refii_bridge_ui_data_changed();
    } else if (name.rfind(kActionInventoryForPrefix, 0) == 0) {
      const std::string rest = name.substr(std::strlen(kActionInventoryForPrefix));
      const int32_t player = rest.size() > 2 && rest[1] == ':' ? rest[0] - '0' : 0;
      ran = DispatchInventoryAction(ctx, base, player != 0 ? rest.substr(2) : rest, player != 0 ? player : -1);
      refii_bridge_ui_data_changed();
    } else if (name.rfind(kActionAbilityPrefix, 0) == 0) {
      // `Ability:<p>:learn|unlearn:<1..14>` — only on the abilities screen with no box up (its
      // OnMenuEvent's own gate, `QM+44 == 0` 0x826C268C), gui_screen_stub_abilities.cpp DispatchAbility.
      std::string rest = name.substr(std::strlen(kActionAbilityPrefix));
      const uint32_t player = TakeActingPlayer(rest);
      const size_t colon = rest.find(':');
      const std::string verb = colon == std::string::npos ? std::string() : rest.substr(0, colon);
      char* end = nullptr;
      const unsigned long index =
          colon == std::string::npos ? 0ul : std::strtoul(rest.c_str() + colon + 1, &end, 10);
      uint32_t modal = 0u;
      const bool up = refii_bridge_stated_pause(nullptr, &modal, nullptr) != 0u;
      if (player == 0u || (verb != "learn" && verb != "unlearn") || end == nullptr || *end != '\0' || index < 1u ||
          index > 14u) {
        REXLOG_INFO("Refii guest-tick pump: '{}' is not Ability:<p>:learn|unlearn:<1..14> — dropped", name);
        GuestTickPumpNoteRan();
        continue;
      }
      if (!up || modal != 0u || refii_bridge_stated_screen_now() != 14u) {
        REXLOG_INFO("Refii guest-tick pump: '{}' refused (screen {}, modal {})", name,
                    refii_bridge_stated_screen_now(), modal);
        GuestTickPumpNoteRan();
        continue;
      }
      ran = refii::vfs::DispatchAbility(ctx, base, player, verb == "unlearn", static_cast<uint32_t>(index));
      refii_bridge_ui_data_changed();
    } else if (name.rfind(kActionFurniturePrefix, 0) == 0) {
      ran = DispatchFurnitureAction(ctx, base, name.substr(std::strlen(kActionFurniturePrefix)));
    } else if (name.rfind(kActionDecorationPrefix, 0) == 0 || name.rfind(kActionRealEstatePrefix, 0) == 0) {
      ran = DispatchHouseScreenLever(ctx, base, name, name.rfind(kActionRealEstatePrefix, 0) == 0);
    } else if (name.rfind(kActionCrimePrefix, 0) == 0 || name.rfind(kActionQueryPrefix, 0) == 0) {
      ran = DispatchCardScreenLever(ctx, base, name, name.rfind(kActionCrimePrefix, 0) == 0);
    } else if (name.rfind(kActionSafetyTogglePrefix, 0) == 0 || name.rfind(kActionTargetQueryPrefix, 0) == 0) {
      ran = DispatchHudLever(ctx, base, name, name.rfind(kActionSafetyTogglePrefix, 0) == 0);
    } else if (name.rfind(refii::vfs::kKeyboardAnswerAction, 0) == 0) {
      // `keyboard:<text>` — the platform keyboard's answer, everything after the colon the text.
      refii::vfs::AnswerKeyboardUi(base, name.c_str() + std::strlen(refii::vfs::kKeyboardAnswerAction));
      ran = true;
    } else if (name == refii::vfs::kKeyboardCancelAction) {
      refii::vfs::AnswerKeyboardUi(base, nullptr);
      ran = true;
      refii_bridge_ui_data_changed();
    } else if (name == kActionRegisterPlayer) {
      ran = DispatchRegisterPlayer(ctx, base);
    } else if (name.rfind("Start:", 0) == 0) {
      // `Start:<player>` — the player's Start through the guest's own handler (gui_screen_stub_pause_root.cpp),
      // which keeps every gate of its own; the pad's Start never reaches the guest in game.
      std::string rest = name.substr(std::strlen("Start:")) + ":";
      const uint32_t player = rest.size() == 2u ? TakeActingPlayer(rest) : 0u;
      if (player == 0u) {
        REXLOG_INFO("Refii guest-tick pump: '{}' names no player — dropped", name);
        GuestTickPumpNoteRan();
        continue;
      }
      ran = refii::vfs::DispatchStart(ctx, base, player);
    } else if (name.rfind("PauseClose:", 0) == 0) {
      // `PauseClose:<player>` — the pause root's B at the top of its list. Only on the root with no
      // box up.
      std::string rest = name.substr(std::strlen("PauseClose:")) + ":";
      const uint32_t player = rest.size() == 2u ? TakeActingPlayer(rest) : 0u;
      uint32_t modal = 0u;
      const bool up = refii_bridge_stated_pause(nullptr, &modal, nullptr) != 0u;
      if (player == 0u || !up || modal != 0u || refii_bridge_stated_screen_now() != 0u) {
        REXLOG_INFO("Refii guest-tick pump: '{}' refused (screen {}, modal {})", name,
                    refii_bridge_stated_screen_now(), modal);
        GuestTickPumpNoteRan();
        continue;
      }
      ran = refii::vfs::DispatchPauseClose(ctx, base, player);
    } else if (name == "PauseBack" || name.rfind("PauseCycle:", 0) == 0) {
      // `PauseBack` / `PauseCycle:<player>:prev|next` — a ported book's B at its root and its LB /
      // RB, the spine calls its handler made (gui_screen_stub_book_levers.cpp). Only on a ported book (14..18,
      // the world map 23) with no
      // box up; the root (0) takes the cycle only — its B is `PauseClose:`.
      uint32_t modal = 0u;
      const bool up = refii_bridge_stated_pause(nullptr, &modal, nullptr) != 0u;
      const uint32_t screen = refii_bridge_stated_screen_now();
      if (!up || modal != 0u ||
          ((screen < 14u || screen > 18u) && screen != 23u && (screen != 0u || name == "PauseBack"))) {
        REXLOG_INFO("Refii guest-tick pump: '{}' refused (screen {}, modal {})", name, screen, modal);
        GuestTickPumpNoteRan();
        continue;
      }
      if (name == "PauseBack") {
        ran = refii::vfs::DispatchPauseBack(ctx, base, screen);
      } else {
        std::string rest = name.substr(std::strlen("PauseCycle:"));
        const uint32_t player = TakeActingPlayer(rest);
        if (player == 0u || (rest != "prev" && rest != "next")) {
          REXLOG_INFO("Refii guest-tick pump: '{}' is not a cycle — dropped", name);
          GuestTickPumpNoteRan();
          continue;
        }
        ran = refii::vfs::DispatchPauseCycle(ctx, base, screen, player, rest == "next");
      }
    } else if (name.rfind("Options:", 0) == 0) {
      // `Options:<p>:<setting>:<value>` / `Options:<p>:audio:<sfx>:<music>:<speech>:<speaker>` — the
      // options screen's settings through the guest's setters (gui_screen_stub_options.cpp). Only on the
      // options screen with no box up.
      uint32_t modal = 0u;
      const bool up = refii_bridge_stated_pause(nullptr, &modal, nullptr) != 0u;
      std::string rest = name.substr(std::strlen("Options:"));
      const uint32_t player = TakeActingPlayer(rest);
      const size_t colon = rest.find(':');
      if (!up || modal != 0u || refii_bridge_stated_screen_now() != 18u || player == 0u || colon == std::string::npos) {
        REXLOG_INFO("Refii guest-tick pump: '{}' refused (screen {}, modal {})", name,
                    refii_bridge_stated_screen_now(), modal);
        GuestTickPumpNoteRan();
        continue;
      }
      const std::string setting = rest.substr(0, colon);
      const std::string values = rest.substr(colon + 1u);
      if (setting == "audio") {
        float levels[3] = {0.0f, 0.0f, 0.0f};
        unsigned speaker = 0u;
        if (std::sscanf(values.c_str(), "%f:%f:%f:%u", &levels[0], &levels[1], &levels[2], &speaker) != 4 ||
            speaker > 2u) {
          REXLOG_INFO("Refii guest-tick pump: '{}' is not sfx:music:speech:speaker — dropped", name);
          GuestTickPumpNoteRan();
          continue;
        }
        for (float& level : levels) {
          level = std::clamp(level, 0.0f, 1.0f);
        }
        ran = refii::vfs::DispatchOptionsAudioCommit(ctx, base, levels, speaker);
      } else {
        char* end = nullptr;
        const double value = std::strtod(values.c_str(), &end);
        if (end == values.c_str() || *end != '\0' || value < 0.0 || value > 4.0) {
          REXLOG_INFO("Refii guest-tick pump: '{}' has no value — dropped", name);
          GuestTickPumpNoteRan();
          continue;
        }
        ran = refii::vfs::DispatchOptions(ctx, base, player, setting, value);
      }
    } else if (name == "CreditsEnd") {
      if (refii_bridge_stated_screen_now() != 29u) {
        REXLOG_INFO("Refii guest-tick pump: 'CreditsEnd' off the credits — dropped");
        GuestTickPumpNoteRan();
        continue;
      }
      ran = refii::vfs::DispatchCreditsEnd(ctx, base);
    } else if (name.rfind(kActionPauseTabPrefix, 0) == 0) {
      const uint32_t tab = static_cast<uint32_t>(std::strtoul(name.c_str() + std::strlen(kActionPauseTabPrefix), nullptr, 10));
      uint32_t modal = 0u;
      uint32_t shown = 0u;
      const bool up = refii_bridge_stated_pause(nullptr, &modal, &shown) != 0u;
      if (tab < 2u || tab > 8u || !up || modal != 0u || refii_bridge_stated_screen_now() != 0u || tab == shown) {
        REXLOG_INFO("Refii guest-tick pump: '{}' refused (screens {}, modal {}, tab {})", name, up, modal, shown);
        GuestTickPumpNoteRan();
        continue;
      }
      PPCContext work = refii::mods::Frame(ctx);
      work.r3.u32 = tab;
      rex_lhGuiTabStrip_Push(work, base);
      ran = true;
    } else if (name.rfind(kActionRumblePrefix, 0) == 0) {
      PPCContext work = refii::mods::Frame(ctx);
      work.r3.u32 = name.c_str()[std::strlen(kActionRumblePrefix)] == '1' ? 1u : 0u;
      rex_lhLua_SetRumbleEnabled(work, base);
      REXLOG_INFO("Refii guest-tick pump: SetRumbleEnabled({})", work.r3.u32);
      ran = true;
    } else if (name.rfind(kActionPauseSavingPrefix, 0) == 0) {
      // Only on the pause root with no box up: a stale row would open a confirm over the next screen.
      std::string index = name.substr(std::strlen(kActionPauseSavingPrefix));
      const uint32_t player = TakeActingPlayer(index);
      uint32_t modal = 0u;
      const bool up = refii_bridge_stated_pause(nullptr, &modal, nullptr) != 0u;
      if (player == 0u || index.size() != 1u || index[0] < '0' || index[0] > '2' || !up || modal != 0u ||
          refii_bridge_stated_screen_now() != 0u) {
        REXLOG_INFO("Refii guest-tick pump: '{}' refused (screen {}, modal {})", name,
                    refii_bridge_stated_screen_now(), modal);
        GuestTickPumpNoteRan();
        continue;
      }
      ran = refii::vfs::DispatchPauseSaving(ctx, base, player, static_cast<uint32_t>(index[0] - '0'));
    } else if (name.rfind(kActionPauseHenchmanQuitPrefix, 0) == 0) {
      // As PauseSaving: on the pause root with no box up.
      std::string rest = name.substr(std::strlen(kActionPauseHenchmanQuitPrefix));
      const uint32_t player = rest.size() == 1u && (rest[0] == '1' || rest[0] == '2')
                                  ? static_cast<uint32_t>(rest[0] - '0')
                                  : 0u;
      uint32_t modal = 0u;
      const bool up = refii_bridge_stated_pause(nullptr, &modal, nullptr) != 0u;
      if (player == 0u || !up || modal != 0u || refii_bridge_stated_screen_now() != 0u) {
        REXLOG_INFO("Refii guest-tick pump: '{}' refused (screen {}, modal {})", name,
                    refii_bridge_stated_screen_now(), modal);
        GuestTickPumpNoteRan();
        continue;
      }
      ran = refii::vfs::DispatchPauseHenchmanQuit(ctx, base, player);
    } else if (name.rfind(kActionQueryAnswerPrefix, 0) == 0) {
      // `QueryAnswer:<player>:<code>[@<modal id>]` — the id names the box the surface drew.
      std::string codeText = name.substr(std::strlen(kActionQueryAnswerPrefix));
      const uint32_t player = TakeActingPlayer(codeText);
      bool addressed = false;
      uint32_t modal_id = 0u;
      if (const size_t at = codeText.find('@'); at != std::string::npos) {
        addressed = true;
        modal_id = static_cast<uint32_t>(std::strtoul(codeText.c_str() + at + 1, nullptr, 10));
        codeText.resize(at);
      }
      char* end = nullptr;
      const unsigned long code = std::strtoul(codeText.c_str(), &end, 10);
      // Only the codes the taxonomy's handlers answer — see the dispatcher. 28/29 are the
      // numeric adjuster's increment/decrement, 32/33 their releases (adjuster only, below).
      if (player == 0u || end == codeText.c_str() || *end != '\0' ||
          (code != 28u && code != 29u && code != 32u && code != 33u && code != 36u && code != 37u &&
           code != 39u && code != 46u && code != 47u && code != 75u)) {
        REXLOG_INFO("Refii guest-tick pump: '{}' has no answer code — dropped", name);
        GuestTickPumpNoteRan();
        continue;
      }

      ran = DispatchQueryAnswer(ctx, base, static_cast<uint32_t>(code), addressed, modal_id, player);
    } else if (name.rfind(kActionShopPrefix, 0) == 0 || name.rfind(kActionGiftPrefix, 0) == 0) {
      // Only on the screen the lever names. A box over it takes its own answer (QueryAnswer), not these.
      const bool gift = name.rfind(kActionGiftPrefix, 0) == 0;
      std::string rest = name.substr(std::strlen(gift ? kActionGiftPrefix : kActionShopPrefix));
      const uint32_t player = TakeActingPlayer(rest);
      unsigned long ticket = 0ul;
      unsigned long record = 0ul;
      const bool item = gift && std::sscanf(rest.c_str(), "item:%lu:%lu", &ticket, &record) == 2 && record != 0ul;
      if (player == 0u || (rest != "close" && !item)) {
        REXLOG_INFO("Refii guest-tick pump: '{}' is not {} — dropped", name,
                    gift ? "Gift:<p>:close or Gift:<p>:item:<ticket>:<record>" : "Shop:<p>:close");
        GuestTickPumpNoteRan();
        continue;
      }
      uint32_t modal = 0u;
      const bool up = refii_bridge_stated_pause(nullptr, &modal, nullptr) != 0u;
      if (!up || modal != 0u || refii_bridge_stated_screen_now() != (gift ? 25u : 22u)) {
        REXLOG_INFO("Refii guest-tick pump: '{}' refused (screen {}, modal {})", name,
                    refii_bridge_stated_screen_now(), modal);
        GuestTickPumpNoteRan();
        continue;
      }
      if (item) {
        ran = DispatchInventoryAction(ctx, base, "gift:" + std::to_string(record), 0, static_cast<uint32_t>(ticket));
        refii_bridge_ui_data_changed();
      } else {
        ran = gift ? refii::vfs::DispatchGiftClose(ctx, base, player) : refii::vfs::DispatchShopClose(ctx, base, player);
      }
    } else if (name.rfind(kActionCardPrefix, 0) == 0) {
      // Only while the card is the stated screen with no box over it.
      std::string verb = name.substr(std::strlen(kActionCardPrefix));
      const uint32_t player = TakeActingPlayer(verb);
      uint32_t modal = 0u;
      refii_bridge_stated_pause(nullptr, &modal, nullptr);
      if (player == 0u || (verb != "dismiss" && verb != "equip")) {
        REXLOG_INFO("Refii guest-tick pump: '{}' is not Card:<p>:dismiss|equip — dropped", name);
        GuestTickPumpNoteRan();
        continue;
      }
      if (modal != 0u || refii_bridge_stated_screen_now() != 8u) {
        REXLOG_INFO("Refii guest-tick pump: '{}' refused (screen {}, modal {})", name,
                    refii_bridge_stated_screen_now(), modal);
        GuestTickPumpNoteRan();
        continue;
      }
      ran = refii::vfs::DispatchCard(ctx, base, player, verb);
    } else if (name.rfind(kActionScreenActionPrefix, 0) == 0) {
      std::string codeText = name.substr(std::strlen(kActionScreenActionPrefix));
      const uint32_t player = TakeActingPlayer(codeText);
      char* end = nullptr;
      const unsigned long code = std::strtoul(codeText.c_str(), &end, 10);
      // The codes these screens answer; anything else is a host defect. 28..31 are the
      // DIRECTIONAL presses: the list screens swallow them (their cursor is Godot's), but the
      // OPTIONS page answers them natively — 28/29 move the row, 30/31 change its VALUE (pickbox index or slider
      // float). 6 is the one-off panels' DISMISS: the minigame tally (`sub_823CDC08`) and the
      // quest completion card (`sub_823D3F98`) answer only it. 32..35 are the RELEASES: the
      // direction gate `sub_828818E8` resets its last direction and both repeat clocks on them
      // (the `case 32..35` arm), so a `31, 35` pair lands every time — a mouse moving a slider
      // several steps posts its steps as press/release pairs in one drain.
      if (player == 0u || end == codeText.c_str() || *end != '\0' ||
          (code != 6u && !(code >= 28u && code <= 37u) && code != 39u && code != 59u &&
           code != 72u && code != 73u)) {
        REXLOG_INFO("Refii guest-tick pump: '{}' has no screen action — dropped", name);
        GuestTickPumpNoteRan();
        continue;
      }

      ran = DispatchScreenAction(ctx, base, static_cast<uint32_t>(code), player);
    } else if (name.rfind(kActionMenuBoxSelectPrefix, 0) == 0 ||
               name.rfind(kActionMenuBoxAnswerPrefix, 0) == 0) {
      const bool select = name.rfind(kActionMenuBoxSelectPrefix, 0) == 0;
      const std::string number = name.substr(std::strlen(
          select ? kActionMenuBoxSelectPrefix : kActionMenuBoxAnswerPrefix));
      char* end = nullptr;
      const unsigned long value = std::strtoul(number.c_str(), &end, 10);
      // The box caps at six options, so an index past that is a host defect, not a state
      // race — refused rather than clamped.
      if (end == number.c_str() || *end != '\0' || value > 6u) {
        REXLOG_INFO("Refii guest-tick pump: '{}' has no menu index — dropped", name);
        GuestTickPumpNoteRan();
        continue;
      }

      ran = select
                ? DispatchMenuBoxSelect(ctx, base, static_cast<uint32_t>(value))
                : DispatchMenuBoxAnswer(ctx, base, static_cast<uint32_t>(value));
    } else if (name.rfind(kActionBoxActionPrefix, 0) == 0) {
      // `BoxAction:<player>:<code>` answers the box the router would pick (`ShowingDisplayBox`);
      // `BoxAction:<player>:<code>@<hex>` answers that box only — the stated display MessageBox
      // at its own address, any other display box only while it is the one showing.
      std::string number = name.substr(std::strlen(kActionBoxActionPrefix));
      const uint32_t player = TakeActingPlayer(number);
      uint32_t target = 0u;
      if (const size_t at = number.find('@'); at != std::string::npos) {
        target = static_cast<uint32_t>(std::strtoul(number.c_str() + at + 1, nullptr, 16));
        number.resize(at);
      }
      char* end = nullptr;
      const unsigned long value = std::strtoul(number.c_str(), &end, 10);
      // The codes the box handlers answer: 28/29 step the adjuster, 36 dismisses, 46 confirms,
      // 47 cancels. Anything else is refused rather than silently ignored by a handler.
      if (player == 0u || end == number.c_str() || *end != '\0' ||
          (value != 28u && value != 29u && value != 36u && value != 46u && value != 47u)) {
        REXLOG_INFO("Refii guest-tick pump: '{}' has no box action — dropped", name);
        GuestTickPumpNoteRan();
        continue;
      }

      // A stated display MessageBox is answered at its own address (its Open was hooked); every
      // other class is answered only while it is the box showing.
      const bool stated_message =
          target != 0u && target >= 0x10000u && target < 0xC0000000u &&
          LoadGuestU32(base, target) == refii::guest::Address(0x820AE668u);
      ran = stated_message
                ? DispatchStatedMessageBoxAction(ctx, base, target, static_cast<uint32_t>(value), player)
                : DispatchDisplayBoxAction(ctx, base, static_cast<uint32_t>(value), target, player);
    } else if (name.rfind("ButtonBox:", 0) == 0 || name == "AcceptHold" || name == "AcceptHold:4") {
      // `ButtonBox:<1..4>` / `AcceptHold[:4]` — see DispatchButtonBoxAnswer.
      const bool hold = name.rfind("AcceptHold", 0) == 0;
      const char* digits = hold ? (name.size() > 10u ? name.c_str() + 11 : "1")
                                : name.c_str() + std::strlen("ButtonBox:");
      char* end = nullptr;
      const unsigned long value = std::strtoul(digits, &end, 10);
      if (end == digits || *end != '\0' || value < 1u || value > 4u) {
        REXLOG_INFO("Refii guest-tick pump: '{}' has no button answer — dropped", name);
        GuestTickPumpNoteRan();
        continue;
      }
      ran = DispatchButtonBoxAnswer(ctx, base, static_cast<uint32_t>(value), hold);
    } else if (name == kActionLoadFanBack) {
      ran = DispatchLoadFanBack(ctx, base);
    } else if (name.rfind(kActionFrontEndDialogPrefix, 0) == 0) {
      // `<pad>:<code>`: the pad 0..3 that pressed, the code 36 or 37.
      const char* digits = name.c_str() + std::strlen(kActionFrontEndDialogPrefix);
      char* end = nullptr;
      const unsigned long pad = std::strtoul(digits, &end, 10);
      char* code_end = nullptr;
      const unsigned long value =
          end != digits && *end == ':' ? std::strtoul(end + 1, &code_end, 10) : 0ul;
      if (end == digits || *end != ':' || code_end == end + 1 || *code_end != '\0' || pad > 3u ||
          (value != 36u && value != 37u)) {
        REXLOG_INFO("Refii guest-tick pump: '{}' is not pad:code of a dialog answer — dropped", name);
        GuestTickPumpNoteRan();
        continue;
      }

      ran = DispatchFrontEndDialog(ctx, base, static_cast<uint32_t>(pad), static_cast<uint32_t>(value));
    } else if (name.rfind(kActionTitleStartPrefix, 0) == 0 || name.rfind(kActionFrontEndBackPrefix, 0) == 0) {
      const bool start = name.rfind(kActionTitleStartPrefix, 0) == 0;
      const char* digits =
          name.c_str() + std::strlen(start ? kActionTitleStartPrefix : kActionFrontEndBackPrefix);
      char* end = nullptr;
      const unsigned long pad = std::strtoul(digits, &end, 10);
      if (end == digits || *end != '\0' || pad > 3u) {
        REXLOG_INFO("Refii guest-tick pump: '{}' has no pad 0..3 — dropped", name);
        GuestTickPumpNoteRan();
        continue;
      }

      ran = start ? DispatchTitleStart(ctx, base, static_cast<uint32_t>(pad))
                  : DispatchFrontEndBack(ctx, base, static_cast<uint32_t>(pad));
    } else if (name == "SubtitlesOn" || name == "SubtitlesOff" ||
               name == "Downloadable Content") {
      // Through the guest's own dispatcher: its id-2 branch compares the item name and does the
      // whole job — the subtitle arms write the setting (`sub_82443820`) and swap the emphasis,
      // and Downloadable Content raises its own no-profile dialog when nobody is signed in.
      ran = DispatchByName(ctx, base, name, kCommandActivateItem, 0u);
    } else if (name.rfind("UiCue:", 0) == 0) {
      // `UiCue:<fe|game>:<cue>` — a sound the guest's own screen code plays for a press Godot
      // handled itself (the gender card, a host-made fold or commit), so the arm that states it
      // never ran. Played exactly as the guest's arms do: `sub_82371E80(buf, emitter, &name, 0)`
      // then `sub_82C05EF8(buf)` (0x826DFBDC/0x826DFBE4 on the front end; 0x826D59B0/0x826D59B8
      // in game). The emitter is the front end's `*(*(0x83496AC0+8)+0x50)` or the in-game GUI
      // `*(0x83496B08)`; the name one of the guest's own string globals, from a closed table.
      static constexpr std::pair<const char*, uint32_t> kCues[] = {
          {"slideup", 0x83497DD8u},  {"slidedown", 0x83497DDCu}, {"page", 0x83497DE8u},
          {"bladein", 0x83497DF0u},  {"bladeout", 0x83497DF4u},  {"select", 0x83497E50u},
          {"cancel", 0x83497E4Cu},   {"newgame", 0x8349B60Cu},   {"boy", 0x8349B610u},
          {"girl", 0x8349B614u},
      };
      const std::string rest = name.substr(std::strlen("UiCue:"));
      const std::size_t colon = rest.find(':');
      const bool front = colon != std::string::npos && rest.compare(0, colon, "fe") == 0;
      const std::string cue = colon != std::string::npos ? rest.substr(colon + 1) : std::string();
      uint32_t global = 0u;
      for (const auto& [key, address] : kCues) {
        if (cue == key) {
          global = refii::guest::Address(address);
        }
      }
      const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
      uint32_t emitter = 0u;
      if (front) {
        const uint32_t root = LoadGuestU32(base, refii::guest::Address(0x83496AC0u));
        const uint32_t inner = plausible(root) ? LoadGuestU32(base, root + 8u) : 0u;
        emitter = plausible(inner) ? LoadGuestU32(base, inner + 0x50u) : 0u;
      } else {
        emitter = LoadGuestU32(base, refii::guest::Address(0x83496B08u));
      }
      PPCFunc* play = FindGuestFunction(refii::guest::Address(0x82371E80u));
      PPCFunc* release = FindGuestFunction(refii::guest::Address(0x82C05EF8u));
      if (global != 0u && plausible(emitter) && play != nullptr && release != nullptr) {
        const PPCContext saved = ctx;
        const uint32_t frame = (ctx.r1.u32 - 0x200u) & ~0xFu;
        const uint32_t buffer = frame + 0x80u;
        ctx.r1.u32 = frame;
        ctx.r3.u32 = buffer;
        ctx.r4.u32 = emitter;
        ctx.r5.u32 = global;
        ctx.r6.u32 = 0u;
        play(ctx, base);
        ctx = saved;
        ctx.r1.u32 = frame;
        ctx.r3.u32 = buffer;
        release(ctx, base);
        ctx = saved;
      }
      quiet = true;
      ran = true;
    } else {
      // Everything else goes through the guest's by-name dispatcher; an unshipped name matches
      // nothing in the strcmp chain, so there is no host-side list to keep in step.
      ran = DispatchByName(ctx, base, name);
    }

    if (ran) {
      if (quiet) {
        REXLOG_DEBUG("Refii guest-tick pump: ran '{}' on the guest tick thread", name);
      } else {
        REXLOG_INFO("Refii guest-tick pump: ran '{}' on the guest tick thread", name);
      }
      GuestTickPumpNoteRan();
      HeapCheckNoteAction(name);
      HeapCheckAfter(base, name.c_str());
    } else {
      // The world is not ready — no front-end state, or the event ids are not interned yet.
      // Held rather than dropped or forced: the pump runs from the boot movies onward, so
      // "too early" is normal for the first few hundred ticks. Logged on a rising power of two
      // so a held action is distinguishable from one the drain never saw, without flooding.
      static std::atomic<uint64_t> deferrals{0};
      const uint64_t seen = deferrals.fetch_add(1, std::memory_order_relaxed) + 1;
      if ((seen & (seen - 1)) == 0) {
        REXLOG_INFO("Refii guest-tick pump: '{}' DEFERRED — the guest is not ready for it "
                    "(deferral #{}); the queue will hold it until it is",
                    name, seen);
      }

      GuestTickPumpDeferAction(name);
    }
  }

  // Puts the deferred ones back at the front of the queue and drops the batch.
  GuestTickPumpEndDrain();
}

// ResolveInGameLoadingScreen lives in src/core/guest_tick_pump_state.cpp and is reached through
// the wrapper in vfs_hooks_private.h — one copy, shared with the host's own finisher.

/// Is a game WORLD up and populated — `World + 0xA8`, the bit both the travel and load-from-save
/// paths set; the same walk `RefiiHostWorldIsActive` uses. False through boot and the front end,
/// which keeps the finish below off the startup loading owner.
bool InGameWorldActive(uint8_t* base) {
  const auto step = [&](uint32_t at, uint32_t offset) -> uint32_t {
    return LooksLikeGuestPointer(at) ? LoadGuestU32(base, at + offset) : 0u;
  };
  uint32_t at = LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  at = step(at, 0x0Cu);
  at = step(at, 0x58u);
  at = step(at, 0x04u);
  at = step(at, 0x00u);
  return LooksLikeGuestPointer(at) && LoadGuestU8(base, at + 0xA8u) != 0;
}

/// Is this player the co-op JOINER — the co-op manager's own `+52` session and `+54` joiner
/// bytes, the same pair `RefiiHostGetCoopRole` reads and the same pair the guest's joiner-side
/// front-end teardown is gated on (`sub_82319BE8`, 0x82319DC0-0x82319E00).
/// The CNetworkManager itself — `g_XrnmNetContext` (0x83497490). `game+140` is only a counted
/// COPY of this pointer, filled by `sub_82319928` at a start commit; on a title-joined client it
/// read 0 in game (the joiner's level load builds a game object that path never fills), and
/// every guest chooser that reads `*(game+140)+52` faulted at 0x34. The global is the truth.
uint32_t CoopNetworkManager(uint8_t* base) {
  const uint32_t manager = LoadGuestU32(base, refii::guest::Address(0x83497490u));
  if (LooksLikeGuestPointer(manager)) {
    return manager;
  }

  const uint32_t root = LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = LooksLikeGuestPointer(root) ? LoadGuestU32(base, root + 12u) : 0u;
  const uint32_t copy = game != 0u ? LoadGuestU32(base, game + 140u) : 0u;
  return LooksLikeGuestPointer(copy) ? copy : 0u;
}

bool IsCoopJoiner(uint8_t* base) {
  const uint32_t coop = CoopNetworkManager(base);
  return coop != 0u && LoadGuestU8(base, coop + 52u) != 0 && LoadGuestU8(base, coop + 54u) != 0;
}

/// Whether a co-op session with a PEER is standing: `+52` session, not `+238` (the
/// lost-connection latch `sub_8232D4D8` sets first thing), and for a host `+221` ("Client
/// joined!", cleared in that same handler) or for a client `+54`. Everything the pump does to
/// player two's body is gated on this: the host's guest tears player two down inside
/// HandleLostConnection, and a tick that still held the address wrote through a freed entity
/// into code space.
bool CoopSessionLive(uint8_t* base) {
  const uint32_t coop = CoopNetworkManager(base);
  if (coop == 0u || LoadGuestU8(base, coop + 52u) == 0 || LoadGuestU8(base, coop + 238u) != 0) {
    return false;
  }

  const bool host = LoadGuestU8(base, coop + 53u) != 0;
  return host ? LoadGuestU8(base, coop + 221u) != 0 : LoadGuestU8(base, coop + 54u) != 0;
}

/// Puts the manager's counted pointer into `game+140/+144` when the guest left it empty — the
/// exact three words `sub_82319928` writes (0x82319928: `*(game+140) = g_XrnmNetContext;
/// *(game+144) = dword_83497494; ++*control`). The guest's own role choosers
/// (`(session && host) ? 1 : 2`, in `sub_829B1968`, `sub_826CC430`, `sub_821C7CE0` …) read the
/// copy, so a client with an empty copy cannot open its own weapons list or unequip a garment.
void CoopRepairNetworkSlot(uint8_t* base) {
  const uint32_t root = LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = LooksLikeGuestPointer(root) ? LoadGuestU32(base, root + 12u) : 0u;
  if (game == 0u || !LooksLikeGuestPointer(game)) {
    return;
  }

  const uint32_t manager = CoopNetworkManager(base);
  if (manager == 0u || LoadGuestU32(base, game + 140u) != 0u) {
    return;
  }

  const uint32_t control = LoadGuestU32(base, refii::guest::Address(0x83497494u));
  StoreGuestU32(base, game + 140u, manager);
  StoreGuestU32(base, game + 144u, control);
  if (LooksLikeGuestPointer(control)) {
    StoreGuestU32(base, control, LoadGuestU32(base, control) + 1u);
  }

  REXLOG_INFO("Refii guest-tick pump: co-op — game 0x{:08X} had no network manager; filled "
              "+140 with 0x{:08X} (control 0x{:08X}) the way sub_82319928 does", game, manager,
              control);
}

/// The fade the in-game loading screen is drawing — `screen+172`, the float
/// `RefiiHostGetLoadingScreen` reports and the one the finish below zeroes. 1.0 is a full cover.
float LoadingScreenFade(uint8_t* base, uint32_t screen) {
  const uint32_t raw = LoadGuestU32(base, screen + 172u);
  float fade = 0.0f;
  std::memcpy(&fade, &raw, sizeof(fade));
  return fade;
}

/// The co-op screen a joiner has already faded in for, so the fade-in happens once per showing.
std::atomic<uint32_t> g_coop_screen_faded_in{0};

/// FADE THE JOINER IN WHEN ITS OWN CO-OP SCREEN COMES UP — a DEADLOCK, not a cosmetic black.
///
/// A joining client loads the level behind a fade-out and the guest then opens screen 24 on it,
/// but nothing ever lifts the fade: `FadeScreenIn` (`sub_82305C08`) is something the HOST does
/// and BROADCASTS. Its body only reaches the send at 0x82305C98 when
/// `*(coopMgr+52) && *(coopMgr+53)` — the networked-HOST pair, tested at 0x82305C64 — so a
/// client never calls it for itself and only ever receives one.
///
/// Measured live: the joiner sat with `screenId 24` up and `[screen-fade] a1.00 t1.00` over it.
/// The player could not see, let alone accept, the screen the HOST was waiting on — so the host
/// sat on its own co-op screen for ever and neither side moved.
///
/// Calling the guest's own fade-in here runs `sub_823CE950` and skips the broadcast arm, which
/// is exactly what a client should do. Latched on the screen id so it fires once per showing and
/// cannot fight a fade the game starts later.
void MaybeFadeInCoopScreen(PPCContext& ctx, uint8_t* base) {
  constexpr uint32_t kCoopScreenId = 24u;

  if (base == nullptr || !IsCoopJoiner(base)) {
    g_coop_screen_faded_in.store(0, std::memory_order_relaxed);
    return;
  }

  // The screen the guest's setter states is up (`sub_8236E630`, its argument).
  if (refii_bridge_stated_screen_now() != kCoopScreenId) {
    g_coop_screen_faded_in.store(0, std::memory_order_relaxed);
    return;
  }

  if (g_coop_screen_faded_in.load(std::memory_order_relaxed) == kCoopScreenId) {
    return;
  }

  g_coop_screen_faded_in.store(kCoopScreenId, std::memory_order_relaxed);

  PPCContext work = ctx;
  work.f1.f64 = 0.5;  // seconds, the same shape the Lua native takes
  rex_lhScriptGui_FadeScreenIn(work, base);

  REXLOG_INFO("Refii co-op: the joiner's own screen 24 is up behind the host's fade — faded in "
              "here, because FadeScreenIn is the HOST's to broadcast (0x82305C64) and a client "
              "never calls it for itself");
}

/// Armed while a joiner's co-op screen is up; cleared once the joiner is in game.
std::atomic<bool> g_joiner_gui_exit_armed{false};

/// THE JOINER LEAVES THE CO-OP SCREEN'S PAUSE. Opening screen 24 runs GUI:OnEnter
/// (`sub_8232CBA8` → `sub_8236EB68`), which pushes the GUI's GameFlow mode 1 (`sub_823D4C48`)
/// over the in-game mode and suspends it (`sub_823D7FC8`: +60 = 0, control states off). Only
/// GUI:OnExit (`sub_8236ED98` / disc `sub_82336F98`) pops it, and only while that mode is the top
/// command (`sub_821ECA30`). On a joiner the screen closes and the mode stays: measured live, the
/// joiner's GameFlow held [in-game (+60 0), mode 1] with no screen up, so none of player two's
/// controls — movement, camera — were active. Calling the guest's own exit there (live, through
/// the debug channel) popped it, entered the in-game mode and activated all twelve. Armed by the
/// co-op screen; run when no screen is up and mode 1 is on top; disarmed once in game.
void MaybeExitJoinerCoopPause(PPCContext& ctx, uint8_t* base) {
  if (base == nullptr || !IsCoopJoiner(base)) {
    g_joiner_gui_exit_armed.store(false, std::memory_order_relaxed);
    return;
  }
  // The stated screen set: the screen its setter says is up, 31 when its exit says none is.
  if (refii_bridge_stated_pause(nullptr, nullptr, nullptr) == 0u) {
    return;
  }
  const uint32_t stated = refii_bridge_stated_screen_now();
  const uint32_t current = stated == 0xFFFFFFFFu ? 31u : stated;
  if (current == 24u) {
    g_joiner_gui_exit_armed.store(true, std::memory_order_relaxed);
    return;
  }
  if (!g_joiner_gui_exit_armed.load(std::memory_order_relaxed) || current != 31u) {
    return;
  }
  const uint32_t gui = InGameScreenOwner(base);  // the object the guest's exit is called on
  if (gui == 0u) {
    return;
  }

  // The top GameFlow command and its type (vt+4), read as sub_821ECA30 reads them:
  // *(*(game+128)+4) the list, its sentinel's +4 the top node, node+8 the command.
  const uint32_t root = LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = LooksLikeGuestPointer(root) ? LoadGuestU32(base, root + 12u) : 0u;
  const uint32_t holder = LooksLikeGuestPointer(game) ? LoadGuestU32(base, game + 128u) : 0u;
  const uint32_t flow = LooksLikeGuestPointer(holder) ? LoadGuestU32(base, holder + 4u) : 0u;
  const uint32_t sentinel = LooksLikeGuestPointer(flow) ? LoadGuestU32(base, flow + 4u) : 0u;
  const uint32_t node = LooksLikeGuestPointer(sentinel) ? LoadGuestU32(base, sentinel + 4u) : 0u;
  const uint32_t command = LooksLikeGuestPointer(node) && node != sentinel ? LoadGuestU32(base, node + 8u) : 0u;
  const uint32_t vtable = LooksLikeGuestPointer(command) ? LoadGuestU32(base, command) : 0u;
  PPCFunc* const type_of = LooksLikeGuestPointer(vtable) ? FindGuestFunction(LoadGuestU32(base, vtable + 4u)) : nullptr;
  static PPCFunc* const gui_exit = FindGuestFunction(refii::guest::Address(0x8236ED98u));
  if (type_of == nullptr || gui_exit == nullptr) {
    return;
  }
  PPCContext test = ctx;
  test.r3.u32 = command;
  type_of(test, base);
  const uint32_t type = test.r3.u32;
  if (type == 6u) {  // the in-game mode is on top: the join is through
    g_joiner_gui_exit_armed.store(false, std::memory_order_relaxed);
    return;
  }
  if (type != 1u) {
    return;
  }
  PPCContext work = ctx;
  work.r3.u32 = gui;
  work.r4.u64 = 1u;
  gui_exit(work, base);
  REXLOG_INFO("Refii co-op: the joiner's co-op screen closed under the GUI's pause mode — ran the "
              "guest's GUI exit (sub_8236ED98) so player two's controls and camera come back");
}

/// The screen already brought down, so the finish fires once per load. Cleared when no screen
/// is up, which re-arms it for the next level change.
std::atomic<uint32_t> g_finished_loading_screen{0};

/// Bring the IN-GAME loading screen down — the host standing in for the present loop that would.
///
/// DELIBERATE REPLACEMENT, with its writes: the take-down is inline in the screen's render update
/// `sub_8235E5A0` (0x8235E7C4..0x8235E7D4, disc `sub_8235ABA0`), which no headless thread can run
/// (below), and no other guest function makes it.
///
/// Nothing presents in this headless mode, so after an in-game level load the guest never runs
/// the render tick that fades its loading screen out and releases the game thread; it sits at
/// `fade=1 requested=0` with the loading music playing. This runs from the in-game tick
/// (`sub_82276D10` -> the pump), gated on the game world being active so it can only touch an
/// in-game screen.
void MaybeFinishInGameLoadingScreen(PPCContext& ctx, uint8_t* base) {
  if (base == nullptr) {
    return;
  }

  if (!InGameWorldActive(base) || refii_bridge_world_build_pending() != 0) {
    return;
  }

  const uint32_t screen = ResolveInGameLoadingScreen(base);
  if (screen == 0) {
    g_finished_loading_screen.store(0, std::memory_order_relaxed);
    return;
  }
  // `+176` (requested) is the edge — not `done`, not the object address. It is the only field
  // that falls back to 0 between loads: `sub_82361458` sets it per shutdown and the completion
  // clears it (`sub_8235E5A0`, 0x8235E7CC, and the latch below). `+177` is write-once outside
  // the constructor `sub_8235CA90`, and the screen object is REUSED across loads, so latching
  // on either would stand this down permanently after the first level load.
  if (LoadGuestU8(base, screen + 176u) == 0) {
    // THE CO-OP JOINER NEVER ASKS, and its screen stays at a full cover for ever.
    //
    // Measured over three runs: a joining client sits at `fade=1 requested=False done=False`
    // with the world BUILT behind it — level construction finished, entities live, the camera
    // driven, frames presenting — and the only thing on screen is this screen's own black. One
    // run did request it and came down normally, so it is a RACE on the guest's side, not a
    // path that never runs.
    //
    // `+176` stays the edge for everyone else, because it is the only field that falls back to
    // 0 between loads and latching on `done` or the object address would stand this down
    // permanently after the first level change. The joiner gets a second trigger instead, and
    // one narrow enough not to touch the ordinary path: we are the joiner, the world is up and
    // populated (`InGameWorldActive`, checked above), and the screen is still covering
    // outright. A world that is not yet ready fails the first test, so this cannot uncover an
    // unfinished level.
    if (!IsCoopJoiner(base) || LoadingScreenFade(base, screen) < 0.999f) {
      g_finished_loading_screen.store(0, std::memory_order_relaxed);
      return;
    }

    REXLOG_INFO("Refii in-game loading screen: the co-op joiner never requested a shutdown and "
                "the world is up behind a full cover — finishing it here (screen=0x{:08X})",
                screen);
  }
  if (g_finished_loading_screen.load(std::memory_order_relaxed) == screen) {
    return;
  }

  if (LoadGuestU32(base, screen + 168u) < 10u) {
    StoreGuestU32(base, screen + 168u, 10u);
  }
  StoreGuestU32(base, screen + 172u, 0u);

  // Never drive `sub_8235E5A0` from this thread — it deadlocks:
  //
  //   rex_lhGameLoop_Tick -> sub_822C1BD0 -> OnGuestTick -> MaybeFinishInGameLoadingScreen
  //     -> sub_8235E5A0 -> rex_lhRenderer_BeginGPUFrame -> RtlEnterCriticalSection  [blocks]
  //
  // The screen's own tick opens a GPU frame, and that lock is held by the 3D Engine thread,
  // parked inside `rex_lhRenderer_RenderFrame` -> `sub_822DFE08` spinning on SwitchToThread
  // with no GPU to drain the ring. The game thread then waits for ever, the 15 Hz tick stops
  // and `dword_83319650` freezes, which also freezes the world update and the camera.
  //
  // Do the teardown by hand instead: latch `done` and pulse the event `sub_82361458` blocks on.
  // Pure memory writes plus one event signal, no renderer lock.
  StoreGuestU8(base, screen + 176u, 0u);
  StoreGuestU8(base, screen + 177u, 1u);
  const uint32_t event = LoadGuestU32(base, screen + 180u);
  if (LooksLikeGuestPointer(event)) {
    PPCContext signal_ctx = ctx;
    signal_ctx.r3.u32 = event;
    signal_ctx.lr = refii::guest::Address(0x8235E7D8u);
    ::__imp__rex_SetEvent(signal_ctx, base);
  }

  refii_bridge_loading_screen_updated(screen, 0.0f, 0, 1);
  g_finished_loading_screen.store(screen, std::memory_order_relaxed);
  REXLOG_INFO("Refii in-game loading screen brought down (screen=0x{:08X})", screen);
}

/// Let the in-game tick actually simulate: `game+0x104`, the flag it gates all of it on.
///
/// `rex_lhGameLoop_Tick` (`sub_82276D10`) opens with
///
///     lbz r11, 260(r31)   ; game+0x104   -> if zero, SKIP
///     lbz r11, 261(r31)   ; game+0x105   -> if zero, SKIP
///
/// and everything behind that gate is the in-game world: the entity advance, the animation pose
/// build, the camera update. After a level load here `+0x105` is 1 and `+0x104` is 0, so the
/// tick does none of it; particle systems live OUTSIDE the gate, on `sub_821DFDD0`, which is why
/// FX animate while the hero, NPCs and camera are frozen.
///
/// The guest sets the flag from `sub_82312EE8` (`li r11,1; stb r11,260(r3)`) and siblings, all
/// on the front-end -> gameplay transition this hosted build never completes. Gated on
/// `InGameWorldActive` and on the guest's own `+0x105`. Off-switch `REFII_ARM_INGAME_SIM=0`.
void MaybeArmInGameSimulation(PPCContext& ctx, uint8_t* base) {
  (void)ctx;
  if (base == nullptr) {
    return;
  }

  // Off by default because it does not work: forcing `+0x104` to 1 opens the gate and the guest
  // faults within about half a second (0xC0000005). The clear flag is a SYMPTOM — the game
  // object has not completed its transition into gameplay, and the code behind the gate
  // dereferences state that transition builds. Opt in with `REFII_ARM_INGAME_SIM=1`.
  static const bool enabled = ReadEnvFlag("REFII_ARM_INGAME_SIM", false);
  if (!enabled || !InGameWorldActive(base)) {
    return;
  }

  const uint32_t mode = LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  if (!LooksLikeGuestPointer(mode)) {
    return;
  }
  const uint32_t game = LoadGuestU32(base, mode + 0x0Cu);
  if (!LooksLikeGuestPointer(game)) {
    return;
  }

  // Only when the guest says the world is ready and the flag is clear — writing every tick
  // would mask the guest setting it.
  if (LoadGuestU8(base, game + refii::guest::GameField(0x105u)) == 0 || LoadGuestU8(base, game + refii::guest::GameField(0x104u)) != 0) {
    return;
  }

  StoreGuestU8(base, game + refii::guest::GameField(0x104u), 1u);

  static std::atomic<uint32_t> armed{0};
  if (armed.fetch_add(1, std::memory_order_relaxed) < 4u) {
    REXLOG_INFO("Refii in-game simulation armed: game 0x{:08X} +0x104 0 -> 1 (+0x105={}), the "
                "game tick can now run the entity/animation/camera block",
                game, LoadGuestU8(base, game + refii::guest::GameField(0x105u)));
  }
}

// FinishStuckInGameLoadingScreenFromHost lives in src/core/guest_tick_pump_state.cpp: it runs on
// the HOST step thread, and refii_vfs is the only library both ends link. It needs no payload —
// guest memory through base plus the take-down event signal, no PPC call.

/// One tick's worth of pump work. Split out so the hook body stays the delegation it must be.

/// Set across player two's d-pad fill, so the shared tutorial calls inside it are skipped.
std::atomic<bool> g_dpad_two_querying{false};

/// A NUL-terminated guest string, at most `cap` bytes.
std::string GuestCString(uint8_t* base, uint32_t at, uint32_t cap = 260u) {
  std::string text;
  for (uint32_t i = 0; i < cap; ++i) {
    const uint8_t c = LoadGuestU8(base, at + i);
    if (c == 0u) {
      break;
    }
    text.push_back(static_cast<char>(c));
  }
  return text;
}

/// PLAYER TWO'S D-PAD AND TARGETED TITLE on split screen. The guest's HUD keeps one d-pad and one
/// targeted readout, both for the local hero; player two's half is stated from the SAME calls,
/// made for hero two:
///   d-pad — `sub_823D28F8` builds four slots (`sub_823D3650`, 28 bytes each), fills them with
///           `sub_823E51A0(component 33, slots, 1)` (`sub_822D1B48`, 0x822d1eb8), and
///           `sub_823D2C98` gives each slot's element (up, left, down, right — `sub_823D34B8`)
///           the `IconGraphic` of the slot's definition (`sub_82805258`, `sub_82323540`), or
///           `Art\GUI\WheelIcons\expressions.tex` when the slot is empty (type 1 at +4, types
///           2..4 at +20). The fill also drives the shared dig-hint tutorial (ids 14 and 15,
///           0x823e5ee0..0x823e5f34); those calls are skipped for player two's fill
///           (RefiiCoopDpadQuerySkip), so the tutorial stays the local hero's.
///   title — `sub_823FD410` writes `sub_823C87B0(entity, occupation)` to `TargetedQueryTitle`;
///           the occupation is component 26's +160 when its +164 byte is clear, else empty, and
///           `TEXT_OCCUPATION_VILLAGER` when that is empty (`sub_82176808`, 0x82176a20..0x82176ab8).
///           The target is hero two's focus target (component 77, `*(*(comp + 0xC8))`).
void SamplePlayerTwoHud(PPCContext& ctx, uint8_t* base) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  const uint32_t hero = RefiiCoopSplitScreen() != 0 ? refii_mod_player_hero(base, 2) : 0u;
  const uint32_t pad = plausible(hero) && (LoadGuestU8(base, hero + 144u) & 0x40u) != 0u
                           ? EntityComponent(base, hero, 33u) : 0u;
  if (!plausible(pad)) {
    refii_bridge_player_two_hud_state(0, 0u, nullptr, nullptr);
    return;
  }

  const PPCContext saved = ctx;
  const uint32_t frame = (saved.r1.u32 - 0x400u) & ~0xFu;
  const uint32_t slots = frame + 0x20u;      // the vector object (16 bytes)
  const uint32_t proto = frame + 0x40u;      // the empty slot it is filled with (28 bytes)
  const uint32_t occupation = frame + 0x70u; // char LHString handle
  const uint32_t title = frame + 0x74u;      // wide LHString handle
  const uint32_t prop = PlaceGuestString(base, frame + 0x80u, "IconGraphic");
  for (uint32_t at = slots; at < frame + 0x78u; at += 4u) {
    StoreGuestU32(base, at, 0u);
  }

  std::string icons[4];
  CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x823D3650u), slots, 4u, proto);
  if (plausible(LoadGuestU32(base, slots + 4u))) {
    g_dpad_two_querying.store(true, std::memory_order_relaxed);
    CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x823E51A0u), pad, slots, 1u);
    g_dpad_two_querying.store(false, std::memory_order_relaxed);
    const uint32_t begin = LoadGuestU32(base, slots + 4u);
    const uint32_t end = LoadGuestU32(base, slots + 8u);
    for (uint32_t i = 0; i < 4u && begin + (28u * (i + 1u)) <= end; ++i) {
      const uint32_t slot = begin + (28u * i);
      const uint32_t type = LoadGuestU32(base, slot);
      const bool filled = type == 1u ? LoadGuestU32(base, slot + 4u) != 0u
                                     : (type > 1u && type <= 4u && LoadGuestU32(base, slot + 20u) != 0u);
      icons[i] = "Art\\GUI\\WheelIcons\\expressions.tex";
      if (filled) {
        const uint32_t def = CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x82805258u), slot);
        if (plausible(def)) {
          const uint32_t text = CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x82323540u), def, prop);
          icons[i] = plausible(text) ? GuestCString(base, text) : std::string();
        }
      }
    }
    CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x823D3808u), slots);
  }

  // The targeted title, for a living citizen in hero two's focus.
  const uint32_t targeting = EntityComponent(base, hero, 77u);
  const uint32_t holder = plausible(targeting) ? LoadGuestU32(base, targeting + 0xC8u) : 0u;
  uint32_t target = plausible(holder) ? LoadGuestU32(base, holder) : 0u;
  if (!plausible(target) || (LoadGuestU8(base, target + 144u) & 0x40u) == 0u ||
      (((LoadGuestU32(base, target + 36u) >> 26) & 1u) == 0u && ((LoadGuestU32(base, target + 48u) >> 1) & 1u) == 0u)) {
    target = 0u;
  }
  std::string said;
  if (target != 0u) {
    std::string job;
    if (((LoadGuestU32(base, target + 36u) >> 26) & 1u) != 0u) {
      const uint32_t work = EntityComponent(base, target, 26u);
      if (plausible(work) && LoadGuestU8(base, work + 164u) == 0u) {
        const uint32_t rep = LoadGuestU32(base, work + 160u);
        const uint32_t chars = plausible(rep) ? LoadGuestU32(base, rep) : 0u;
        job = plausible(chars) ? GuestCString(base, chars, 120u) : std::string();
        if (job.empty()) {
          job = "TEXT_OCCUPATION_VILLAGER";
        }
      }
    }
    CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x82275438u), occupation,
                PlaceGuestString(base, frame + 0x100u, job.c_str()));
    CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x823C87B0u), title, target, occupation);
    said = ReadStatedWideLhString(base, title);
    CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x821C67F8u), title);
  }

  const char* const paths[4] = {icons[0].c_str(), icons[1].c_str(), icons[2].c_str(), icons[3].c_str()};
  refii_bridge_player_two_hud_state(1, target, said.c_str(), paths);
}

/// PLAYER TWO'S DRUNK WARP on split screen. The guest makes one fullscreen displacement effect,
/// for the local hero: `sub_822B62F0(owner)` creates it ("Art\GUI\HUD\FXT_Fullscreen_Drunk",
/// `sub_821DE2E8`) while that hero's drunkenness (component 157 +12) is at or past its threshold
/// (+36), keeps the handle at owner+4 (-1 none) and destroys it (`sub_82175918(handle, 1)`) once
/// sober; the hero is `sub_8229EE78(sub_821C7CE0())`, the local player. Here the same builder runs
/// for player two, with the local player answering player two for the call
/// (RefiiCoopLocalPlayerTwoCall), on an owner of our own; off split screen their effect goes.
/// Both effects are the same record, so the host routes the warp by whose drunk, not by quad.
uint32_t g_drunk_two_owner = 0u;

void DrivePlayerTwoDrunk(PPCContext& ctx, uint8_t* base) {
  const PPCContext saved = ctx;
  const uint32_t frame = (saved.r1.u32 - 0x200u) & ~0xFu;
  if (g_drunk_two_owner == 0u) {
    if (RefiiCoopSplitScreen() == 0) {
      return;
    }
    const uint32_t owner = CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x8221F258u), 16u);
    if (owner < 0x10000u) {
      return;
    }
    StoreGuestU32(base, owner, 0u);
    StoreGuestU32(base, owner + 4u, 0xFFFFFFFFu);
    g_drunk_two_owner = owner;
  }
  if (RefiiCoopSplitScreen() == 0 || refii_mod_player_hero(base, 2) == 0u) {
    const uint32_t handle = LoadGuestU32(base, g_drunk_two_owner + 4u);
    if (handle != 0xFFFFFFFFu) {
      CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x82175918u), handle, 1u);
      StoreGuestU32(base, g_drunk_two_owner + 4u, 0xFFFFFFFFu);
    }
    return;
  }
  RefiiCoopLocalPlayerTwoCall(1);
  CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x822B62F0u), g_drunk_two_owner);
  RefiiCoopLocalPlayerTwoCall(0);
}

/// Who owns the open in-game GUI, for the host (local co-op routes that player's pad to the menu
/// and names them on it; the menu fills the window as in single player). The guest's own test is `sub_8236E400(owner)`: the game-flow gate
/// `sub_821ECA30()` (the top command is the GUI mode) and the pause root handler's player being 1
/// (0x8236e430). The player is the handler's setter's own argument (StatedPausePlayerTwo,
/// gui_stated_screens.cpp): the pause opener's, and each world screen's opener's — the arrest's
/// criminal, the item action's player, the shopkeeper's user (coop_hero_hooks.cpp
/// RefiiCoopShopOwner) and the real estate's buyer (gui_screen_stub_real_estate.cpp). The game-flow
/// gate is asked of the guest, as it asks it.
void StateMenuOwner(PPCContext& ctx, uint8_t* base) {
  int32_t two = 0;
  if (StatedPausePlayerTwo()) {
    const PPCContext saved = ctx;
    const uint32_t frame = (saved.r1.u32 - 0x200u) & ~0xFu;
    two = (CallGuestAt(ctx, base, saved, frame, refii::guest::Address(0x821ECA30u), 0u) & 0xFFu) != 0u ? 1 : 0;
  }
  refii_bridge_set_menu_owner_two(two);
}

void OnGuestTick(PPCContext& ctx, uint8_t* base) {
  g_guest_base.store(base, std::memory_order_relaxed);

  const uint64_t tick = GuestTickPumpObserveTick();
  // The pump announcing itself, once and then rarely. Ticks 1, 600 and 6000 fall in different
  // states (boot movies 60 Hz, front end 30, game 15), so a once-only line could not tell "the
  // pump runs" from "the pump ran during the logos and stopped".
  if (tick == 0 || tick == 600 || tick == 6000 || tick == 60000) {
    REXLOG_INFO("Refii guest-tick pump: tick {} on the guest tick thread (drained {})",
                tick + 1, GuestTickPumpDrained());
  }

  // A stack overflow on this thread faults on its guard page, and a handler run on that same
  // stack cannot even be entered. Give the signal somewhere to run.
  EnsureAlternateSignalStack();

  // Raise Dead's creation effect, which the guest spawns and never kills. Here because both calls
  // re-enter the guest and this is an update context, which is where the guest makes the same two
  // calls for its other spells. It returns immediately when nothing is pending.
  SweepOrphanSpellFx(ctx, base);

  // The menu capture settles here: a population runs to completion inside the GUI update that
  // began it, so by the next tick the captured rows are whole. There is no end marker to use
  // instead — the in-game lists never call `UpdateAfterSpawning`.
  refii_bridge_menu_settle();
  StateMenuOwner(ctx, base);
  refii_bridge_note_coop_split_in_effect(RefiiCoopSplitScreen());

  DrainOnGuestThread(ctx, base, DrainPass::kTick);

  // Hard mods' per-tick callbacks (core/refii_mod.h).
  RunModTicks(ctx, base);

  // The hero sample, on a cadence — see SampleHeroDerived — and on the tick after a change an
  // open menu lists, bumping the UI generation again once the changed values are published.
  const bool ui_dirty = refii_bridge_ui_data_take_dirty() != 0;
  if ((tick % 15u) == 0u) {
    SampleHeroDerived(ctx, base);
    SampleHeroInventory(base);
    ArmHudShow(ctx, base);
  } else if (ui_dirty) {
    SampleHeroDerived(ctx, base);
    SampleHeroInventory(base);
  }
  if (ui_dirty) {
    refii_bridge_ui_data_published();
  }

  // The present loop that would normally fade the in-game loading screen out is off in this
  // headless mode, so the guest never releases the game thread from it. Do it here, from the
  // in-game tick, once the world it was loading is up.
  MaybeFinishInGameLoadingScreen(ctx, base);
  MaybeFadeInCoopScreen(ctx, base);
  MaybeExitJoinerCoopPause(ctx, base);
  ++g_coop_ticks;
  CheckGuestHeaps(base, g_coop_ticks);
  CoopRedress(ctx, base);
  SamplePlayerTwoHud(ctx, base);
  DrivePlayerTwoDrunk(ctx, base);

  MaybeArmInGameSimulation(ctx, base);

  // After the drain, so a "New Game" that just ran is reflected in the next answer rather than
  // the one before it.
  SampleSavedGameCount(ctx, base);
}

// `ReadGuestLhString` lives in core/guest_state.h — one implementation, shared with the
// menu-chunk hooks that need the same handle chase.

/// One spawned row. `stack_rating` says whether this emitter HAS a ninth argument.
///
/// The two emitters do NOT share a signature. `sub_826CE8D0` takes nine arguments, the ninth
/// (the rating) on the stack at caller_SP + 0x54 — which is why this runs before the recompiled
/// prologue. `sub_826E4798` takes exactly eight in r3..r10 and no stack arguments, hard-coding
/// `Float4 = 0.0`; reading r1+0x54 for it reads the caller's frame.
///
/// The label (r5) is a wide LHString handle in both emitters and both pressings:
/// `lwz r11, 0(r5)`, empty literal if zero, else `lwz r4, 0(r11)` into the wide-to-UTF-8
/// conversion (0x826E4804 / 0x826CE930 on TU1).
void OnMenuSpawn(PPCContext& ctx, uint8_t* base, bool stack_rating) {
  const std::string name = Latin1ToUtf8(ReadStatedLhBytes(base, ctx.r4.u32));
  const std::string parent = Latin1ToUtf8(ReadStatedLhBytes(base, ctx.r7.u32));
  const std::string label = ReadStatedWideLhString(base, ctx.r5.u32);
  const int32_t rating =
      stack_rating ? static_cast<int32_t>(LoadGuestU32(base, ctx.r1.u32 + 0x54u)) : 0;
  refii_bridge_menu_spawn_row_labelled(name.c_str(), parent.c_str(), ctx.r8.u32,
                                       static_cast<int32_t>(ctx.r9.u32), rating,
                                       label.c_str());

  // String3, the icon (r6, `String3` 0x826CE9B4), and the item the row stands for: the one the row
  // builder `sub_826CE230` was handed when this spawn is its own (item_rows_stated.cpp), else, on an
  // item book, the item the last ticket was filed against when the row's id is that ticket (the suits,
  // spawned straight after `sub_82880AC8` at 0x829B3224 with `mr r8, r21` 0x829B3270).
  const std::string icon = Latin1ToUtf8(ReadStatedLhBytes(base, ctx.r6.u32));
  ItemRowFacts facts;
  uint32_t flags = 0u;
  uint32_t record = 0u;
  uint32_t item = 0u;
  uint32_t count = 0u;
  uint32_t category = 0u;
  if (TakeItemRowForSpawn(&facts) && facts.item != 0u) {
    flags = kMenuRowItem | kMenuRowCounted | (facts.viewed ? kMenuRowViewed : 0u);
    record = facts.record;
    item = facts.item;
    count = facts.count;
    category = facts.category;
  } else {
    uint32_t screen_id = 0u;
    uint32_t ticket = 0u;
    if (ctx.r8.u32 != 0u && StatedInGameScreen(&screen_id) &&
        (screen_id == 15u || screen_id == 16u || screen_id == 25u) &&
        LastMenuTicket(&ticket, &record, &item) && ticket == ctx.r8.u32 && item != 0u) {
      flags = kMenuRowItem;
    } else {
      record = 0u;
      item = 0u;
    }
  }
  refii_bridge_menu_spawn_row_item(icon.c_str(), flags, record, item, count, category);
}
}  // namespace refii::vfs

REX_EXTERN(rex_lhTickDelegate_ForwardToInner) {
  REX_FUNC_PROLOGUE();

  refii::vfs::OnGuestTick(ctx, base);

  __imp__rex_lhTickDelegate_ForwardToInner(ctx, base);
}

// THE PAUSE HAS ITS OWN HEARTBEAT, AND THE PAUSE-SCREEN SAMPLERS MUST RIDE IT.
//
// `sub_822C1BD0` — the game-loop tick every sampler used to hang off — STOPS while the pause
// book is open: the pump's tick counter sat frozen at 1546 across eight seconds of an open
// world map. So a sampler gated on "screen 23 is up" could never fire — its precondition only
// holds while its carrier is stopped — and the region and family pages read blank forever
// while the hero and quest data (sampled in gameplay, before the pause) looked fine beside
// them.
//
// `sub_8229ABD8` is the pause-tab lever poller, run every GUI tick — the heartbeat that beats
// exactly when the game-loop tick does not. Cadenced: these samplers call guest providers, and
// every 8th GUI tick is twice a second at 15 Hz.
REX_EXTERN(rex_lhGuiManager_TickUpdate) {
  REX_FUNC_PROLOGUE();

  // The in-game GUI's own actions, inside the Lua window — see IsInGameUiAction.
  refii::vfs::DrainOnGuestThread(ctx, base, refii::vfs::DrainPass::kGui);

  static std::atomic<uint32_t> gui_ticks{0};
  if ((gui_ticks.fetch_add(1, std::memory_order_relaxed) % 8u) == 0u) {
    uint32_t screen_id = 0;
    if (refii::vfs::StatedInGameScreen(&screen_id)) {
      refii::vfs::SamplePauseTabs(ctx, base);
      refii::vfs::SampleLogbookAttractiveness(ctx, base, screen_id == 17u);
      switch (screen_id) {
        case 15u:
        case 16u:
          refii::vfs::StateRowItemDyeLayers(ctx, base);
          break;
        case 23u:
          refii::vfs::SampleWorldMapRegion(ctx, base);
          break;
        case 24u:
          refii::vfs::SampleCoopScreen(ctx, base);
          break;
        default:
          break;
      }
    }
  }
  refii::vfs::SamplePropertyEstate(ctx, base);

  __imp__rex_lhGuiManager_TickUpdate(ctx, base);
}

// THE GAME-FLOW TICK `sub_821762C0` (disc `sub_82176088`), called by `lhEngine_FrameUpdate` once a sim
// step right after the guest's input dispatch (0x821C9020 / 0x821C902C; disc 0x821C9078 / 0x821C9084).
// It runs only the top state's sim-step slot (0x821764C4 `lwz r10,0x18(r11)`, 0x821764CC `bctrl`; disc
// 0x8217628C). Two things are stated here:
//   - the player's Start (IsInputAction), drained at the entry where the guest's own dispatch delivered it;
//   - THE WORLD HELD, key 19: whether the step ran no world tick. `sub_82275F68(world)` (disc
//     `sub_82275F00`) is the world's sim tick — game time, actions, the animation components — and has two
//     callers, PlayGame's step (0x821D49D8 / disc 0x821D4788) and the full cutscene's (0x823D98D0 / disc
//     0x82460EB0). The display box state (vt 0x820AED78 / disc 0x820B3BE8) and GUI mode (0x820AEC7C /
//     0x820B3AEC) have `nullsub_1` in that slot (+0x18 = 0x82B96E78 `blr`), so under the lite pause box
//     and the pause book the guest's world does not advance. 2: it ran under a pause all the same — the
//     player's own pause in an online session (RefiiCoopOwnPauseRunsWorld, coop_hero_hooks.cpp).
extern "C" int RefiiCoopOwnPauseRunsWorld(PPCContext* ctx, uint8_t* base);
namespace {
thread_local bool t_world_ticked = false;
constexpr uint32_t kWorldHeld = 19u;
}  // namespace

REX_EXTERN(rex_lhGameFlow_Tick) {
  REX_FUNC_PROLOGUE();
  refii::vfs::DrainOnGuestThread(ctx, base, refii::vfs::DrainPass::kInput);
  // A Start drained just now opened its player's book (the opener's vf+32 stated the owner):
  // the host reads the owner of THIS open, not the tick-start one, before it draws the list.
  refii::vfs::StateMenuOwner(ctx, base);
  t_world_ticked = false;
  __imp__rex_lhGameFlow_Tick(ctx, base);
  const bool under_own_pause = !t_world_ticked && RefiiCoopOwnPauseRunsWorld(&ctx, base) != 0;
  refii_bridge_stated_value(kWorldHeld, under_own_pause ? 2u : t_world_ticked ? 0u : 1u);
}

REX_EXTERN(rex_lhWorld_SimTick) {
  REX_FUNC_PROLOGUE();
  t_world_ticked = true;
  // Before the guest takes the world lock: the previous tick's pathfinder batch closes first
  // (hooks/pathfinder_batch.cpp states why).
  refii::vfs::WaitForPathfinderBatch(base);
  __imp__rex_lhWorld_SimTick(ctx, base);
}

// The attractiveness ratio, read from the page's own store after its writer runs.
//
// `sub_829BA860` is the logbook attractiveness page's writer and the only caller of
// `sub_82881E38(page + 36, ratio)` for this meter. The meter stores the clamped ratio at
// `meter + 40` (0x82881E88), i.e. `page + 76`. The store is gated: `sub_82881E38` returns
// without writing unless `meter + 12` is set (0x82881E50), so the same gate applies here.
REX_EXTERN(rex_lhGui_LogbookAttractivenessPage_UpdateMeter) {
  REX_FUNC_PROLOGUE();

  const uint32_t page = ctx.r3.u32;

  __imp__rex_lhGui_LogbookAttractivenessPage_UpdateMeter(ctx, base);

  if (page == 0u) {
    return;
  }

  const uint32_t meter = page + 36u;
  if (refii::vfs::LoadGuestU8(base, meter + 12u) == 0u) {
    return;
  }

  refii::vfs::g_attractiveness_total_bits.store(refii::vfs::LoadGuestU32(base, meter + 40u),
                                                std::memory_order_relaxed);
  refii::vfs::g_attractiveness_total_known.store(true, std::memory_order_relaxed);
}

// The spawn, captured — the IN-GAME one, against GUI manager `dword_83496B08`.
REX_EXTERN(rex_lhGui_InGameMenu_SpawnButton) {
  REX_FUNC_PROLOGUE();

  refii::vfs::OnMenuSpawn(ctx, base, /*stack_rating=*/true);

  __imp__rex_lhGui_InGameMenu_SpawnButton(ctx, base);
}

// And the front end's — a second function with the same body against a different GUI manager
// (`dword_83496AC0`). These are the only two emitters of `g_Menu:SpawnButton()` in the image.
// Its `Float4` is hardcoded to 0.0: no front-end row has stars.
REX_EXTERN(rex_lhGui_Frontend_SpawnButton) {
  REX_FUNC_PROLOGUE();

  // EIGHT ARGUMENTS, NO STACK RATING — this emitter hard-codes Float4 = 0.0. See OnMenuSpawn.
  refii::vfs::OnMenuSpawn(ctx, base, /*stack_rating=*/false);

  __imp__rex_lhGui_Frontend_SpawnButton(ctx, base);
}

// The list boundary. `sub_826CE110` runs `"g_Menu:DeleteAllSpawnedMenuItems()"` and takes no
// arguments; it is the one marker both the in-game and front-end populations share. The END is
// not markable — only front-end lists call `UpdateAfterSpawning` — so the capture publishes per
// row rather than waiting for one.

/// The screen whose population is starting, as the guest's screen setter states it. The front
/// end has no in-game screen set, so its rows are keyed "no in-game screen" — which is what stops
/// them being handed to an in-game template. See `refii_bridge_menu_spawn_begin_for`.
static uint32_t PopulatingScreenId() {
  uint32_t screen_id = 0;
  return refii::vfs::StatedInGameScreen(&screen_id) ? screen_id : 0xFFFFFFFFu;
}

REX_EXTERN(rex_lhGui_InGameMenu_DeleteAllSpawnedMenuItems) {
  REX_FUNC_PROLOGUE();

  refii_bridge_menu_spawn_begin_for(PopulatingScreenId());

  __imp__rex_lhGui_InGameMenu_DeleteAllSpawnedMenuItems(ctx, base);
}

// The other six boundaries — populators that fire `DeleteAllSpawnedMenuItems` INLINE rather
// than through the wrapper above: world map sub_829A2F40 @0x829a2fc8, abilities sub_829A45A0,
// shop sub_82994218, logbook sub_829B8648, gift-gold sub_82430468, front end sub_826E1F70.
// Without them a screen's rows join onto the previous population's. The string match in the
// exec hook (resource_manager_open.cpp) is the net for any emitter not listed here.
#define REFII_MENU_BOUNDARY_HOOK(fn)                                            \
  REX_EXTERN(fn) {                                                             \
    REX_FUNC_PROLOGUE();                                                       \
    refii_bridge_menu_spawn_begin_for(PopulatingScreenId());               \
    __imp__##fn(ctx, base);                                                    \
  }

REFII_MENU_BOUNDARY_HOOK(rex_lhGuiAbilitiesScreen_PopulateMenu)
REFII_MENU_BOUNDARY_HOOK(rex_lhGuiShopScreen_PopulateMenu)
REFII_MENU_BOUNDARY_HOOK(rex_lhGuiLogbookScreen_PopulateMenu)
REFII_MENU_BOUNDARY_HOOK(rex_lhGuiGiftGoldScreen_PopulateMenu)

// The world map's population (`(h+24)->vf+4`, run by its OnEnter): the rows Godot lists are these
// spawns, and the pages it draws for them are the page fills' restated statements
// (gui_screen_stub_world_map.cpp), which start over with the population: a row whose page reads as it
// did last visit is stated again.
REX_EXTERN(rex_lhGuiWorldMapScreen_PopulateMenu) {
  REX_FUNC_PROLOGUE();
  refii_bridge_menu_spawn_begin_for(PopulatingScreenId());
  refii::vfs::ForgetRestatedPageFills(23u);
  __imp__rex_lhGuiWorldMapScreen_PopulateMenu(ctx, base);
}

REX_EXTERN(rex_lhGui_FrontendRoot_PopulateMainMenu) {
  REX_FUNC_PROLOGUE();
  refii_bridge_menu_spawn_begin_for(PopulatingScreenId());
  const uint32_t menu = ctx.r3.u32;
  refii::vfs::FrontEndMenuActivating();
  __imp__rex_lhGui_FrontendRoot_PopulateMainMenu(ctx, base);
  refii::vfs::FrontEndMenuActivated(menu);
}

#undef REFII_MENU_BOUNDARY_HOOK

extern "C" {
uint8_t* RefiiGuestBase(void) {
  return refii::vfs::g_guest_base.load(std::memory_order_relaxed);
}

void RefiiCoopSetPartnerDog(uint32_t dog);

/// Player `player`'s dog: hero one's is the guest's own slot (players+196, the one Lua GetDog
/// reads), player two's the co-op record. 0 when there is none or it is dead.
uint32_t RefiiCoopPlayerDog(uint8_t* base, int player) {
  if (base == nullptr) return 0u;
  if (player == 2) {
    const uint32_t handle = refii::vfs::g_coop_partner_dog;
    const uint32_t dog = handle != 0u ? refii::vfs::refii_mod_handle_address(handle) : 0u;
    if (dog != 0u && refii::vfs::refii_mod_entity_alive(base, dog) != 0) {
      return dog;
    }
    // NOT RECORDED: a joiner's replica (a client skips the create's activation, so no bind
    // records it) or a dog loaded with a level. Found by its name in the world's entity list
    // (the manager at 0x83496B28, +8 the head, node = entity+8), and recorded, so it is the
    // answer until it dies. The walk runs at most once every 16 guest frames (the frame counter
    // at 0x83319650): every call between two walks gets the last walk's answer, so callers in
    // one frame agree.
    static uint32_t walked_frame = 0u;
    static bool walked = false;
    const uint32_t frame = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83319650u));
    if (walked && frame - walked_frame < 16u) {
      return 0u;
    }
    walked = true;
    walked_frame = frame;
    if (refii::vfs::refii_mod_player_hero(base, 2) == 0u) {
      return 0u;
    }
    const uint32_t manager = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496B28u));
    uint32_t node = manager >= 0x10000u && manager < 0xC0000000u
                        ? refii::vfs::LoadGuestU32(base, manager + 8u)
                        : 0u;
    char name[32];
    for (uint32_t i = 0; i < 20000u && node >= 0x10000u && node < 0xC0000000u;
         ++i, node = refii::vfs::LoadGuestU32(base, node)) {
      const uint32_t entity = node - 8u;
      if (refii::vfs::refii_mod_entity_alive(base, entity) == 0 ||
          refii::vfs::refii_mod_component(base, entity, 97u) == 0u ||
          refii::vfs::refii_mod_entity_name(base, entity, name, sizeof(name)) == 0 ||
          std::strcmp(name, refii::vfs::kCoopPartnerDogName) != 0) {
        continue;
      }
      RefiiCoopSetPartnerDog(entity);
      return entity;
    }
    return 0u;
  }
  if (player != 1) return 0u;
  const uint32_t players = refii::vfs::PlayerObject(base);
  const uint32_t holder = players != 0u ? refii::vfs::LoadGuestU32(base, players + 196u) : 0u;
  const uint32_t dog = holder >= 0x10000u && holder < 0xC0000000u ? refii::vfs::LoadGuestU32(base, holder) : 0u;
  return dog >= 0x10000u && dog < 0xC0000000u && refii::vfs::refii_mod_entity_alive(base, dog) != 0 ? dog : 0u;
}

/// Records `dog` (0 clears) as player two's.
void RefiiCoopSetPartnerDog(uint32_t dog) {
  refii::vfs::g_coop_partner_dog = dog != 0u ? refii::vfs::refii_mod_handle(dog) : 0u;
}

/// Whose `dog` is, by its instance name: 0 not player two's, 1 player two's (recorded now when
/// none is), 2 a second dog of player two's while the recorded one lives — a stale copy (a
/// level's saved state, a lost destroy) that is not to be bound.
int RefiiCoopPartnerDogRole(uint32_t dog) {
  uint8_t* base = refii::vfs::g_guest_base.load(std::memory_order_relaxed);
  if (base == nullptr || dog == 0u) return 0;
  char name[32];
  if (refii::vfs::refii_mod_entity_name(base, dog, name, sizeof(name)) == 0 ||
      std::strcmp(name, refii::vfs::kCoopPartnerDogName) != 0) {
    return 0;
  }
  const uint32_t recorded = RefiiCoopPlayerDog(base, 2);
  if (recorded == 0u) {
    RefiiCoopSetPartnerDog(dog);
    return 1;
  }
  return recorded == dog ? 1 : 2;
}

/// Player two's dog as their HeroState states it: the definition (0 when they have none or no
/// state has arrived), its two levels, and the learned tricks as comma-separated GDB names into
/// `tricks`. Returns the state's version (0 = none). Guest tick thread only, as the record is.
uint32_t RefiiCoopPartnerDogKit(uint32_t* definition, int32_t* searching, int32_t* combat,
                                char* tricks, uint32_t capacity) {
  const refii::vfs::CoopHeroRecord& record = refii::vfs::g_coop_record[1];
  if (definition != nullptr) *definition = record.dog_definition;
  if (searching != nullptr) *searching = record.dog_searching;
  if (combat != nullptr) *combat = record.dog_combat;
  if (tricks != nullptr && capacity != 0u) {
    std::snprintf(tricks, capacity, "%s", record.dog_tricks.c_str());
  }
  return record.generation;
}

/// Dresses player two's dog in what their HeroState says it wore (its collar), once per dog:
/// given by def hash and worn, as player two's own outfit is. True when dressed or nothing to
/// dress; false when the dog has no inventory or appearance component yet.
int RefiiCoopDressPartnerDog(PPCContext* ctx, uint8_t* base, uint32_t dog) {
  static uint32_t dressed = 0u;
  const refii::vfs::CoopHeroRecord& record = refii::vfs::g_coop_record[1];
  if (ctx == nullptr || base == nullptr || dog == 0u || dog == dressed || record.dog_worn.empty()) {
    return 1;
  }
  const uint32_t inventory = refii::vfs::EntityComponent(base, dog, 13u);
  const uint32_t modifiers = refii::vfs::EntityComponent(base, dog, 15u);
  if (inventory == 0u || modifiers == 0u) {
    return 0;
  }
  dressed = dog;
  uint32_t worn = 0u;
  uint32_t given = 0u;
  refii::vfs::WearHashes(*ctx, base, inventory, modifiers, record.dog_worn, &worn, &given);
  REXLOG_INFO("Refii guest-tick pump: player two's dog 0x{:08X} wears {} item(s) ({} newly given)",
              dog, worn, given);
  return 1;
}

/// The name player two gave their dog, into `out`; false when they gave none.
int RefiiCoopPartnerDogName(char* out, uint32_t capacity) {
  const std::string& name = refii::vfs::g_coop_record[1].dog_name;
  if (out != nullptr && capacity != 0u) std::snprintf(out, capacity, "%s", name.c_str());
  return name.empty() ? 0 : 1;
}

/// The Player for an id 1/2 (the PlayerManager's map), or 0.
uint32_t RefiiCoopPlayerWithId(uint8_t* base, uint32_t id) { return refii::vfs::PlayerWithId(base, id); }

void RefiiCoopSetJobWorker(int player) { refii::vfs::g_coop_job_worker = player; }
int RefiiCoopJobWorker(void) { return refii::vfs::g_coop_job_worker; }
void RefiiCoopSetEstateBuyer(int player) { refii::vfs::g_coop_estate_buyer = player; }
int RefiiCoopTakeEstateBuyer(void) {
  const int buyer = refii::vfs::g_coop_estate_buyer;
  refii::vfs::g_coop_estate_buyer = 0;
  return buyer;
}

void RefiiCoopSetOwnHero(int on) {
  refii_bridge_two_is_hero_set(on != 0 ? 1 : 0);
}

/// Whether `item` is a weapon the host's EquipRecord put on `body` (CoopKeepHostWeapons' list).
/// Guest tick thread only, as the list is.
int RefiiCoopIsHostWeapon(uint32_t body, uint32_t item) {
  const auto found = refii::vfs::g_coop_host_weapon.find(body);
  if (item == 0u || found == refii::vfs::g_coop_host_weapon.end()) {
    return 0;
  }
  return found->second[0] == item || found->second[1] == item ? 1 : 0;
}

/// Whether player two's d-pad fill is running (coop_hero_hooks.cpp skips the shared tutorial).
int32_t refii_bridge_dpad_query_two(void) {
  return refii::vfs::g_dpad_two_querying.load(std::memory_order_relaxed) ? 1 : 0;
}

extern "C" int RefiiCoopSplitCameraBuilt(void);

/// Split screen WANTED: the host's switch, and LOCAL co-op (game+0x11C == 2). The host states the
/// switch whatever the session; an online joiner's Player must not take player two's split-screen
/// camera — that ticked a second manager on the joiner and redirected its own camera's
/// activations. Only the Player constructor asks this (coop_hero_hooks.cpp).
int RefiiCoopSplitScreenWanted(void) {
  if (!refii::vfs::g_coop_split_screen.load(std::memory_order_relaxed)) {
    return 0;
  }
  uint8_t* base = refii::vfs::g_guest_base.load(std::memory_order_relaxed);
  if (base == nullptr) {
    return 0;
  }
  const uint32_t root = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = root >= 0x10000u && root < 0xC0000000u ? refii::vfs::LoadGuestU32(base, root + 12u) : 0u;
  return game >= 0x10000u && game < 0xC0000000u && refii::vfs::LoadGuestU32(base, game + 0x11Cu) == 2u ? 1 : 0;
}

/// Split screen IN EFFECT: wanted, and player two's current Player was built with a camera of
/// its own. The constructor reads the switch once, in the drop-in before the co-op screen opens,
/// so a split chosen on that screen (or later) reaches player two's next Player; until then every
/// reader — the second camera tick, the peer view, the listeners, the camera Lua's own-cameras
/// and free-roam rules (Refii.split_screen), the host — keeps the shared screen's rules.
int RefiiCoopSplitScreen(void) {
  return RefiiCoopSplitScreenWanted() != 0 && RefiiCoopSplitCameraBuilt() != 0 ? 1 : 0;
}

/// The joiner's pending exit use, if it is not older than 300 guest frames: its guest position.
int RefiiCoopPeekExitUse(uint8_t* base, float* out_xyz) {
  auto& use = refii::vfs::g_coop_exit_use;
  if (!use.pending || out_xyz == nullptr || base == nullptr) {
    return 0;
  }
  if (refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83319650u)) - use.frame > 300u) {
    use.pending = false;
    return 0;
  }
  std::memcpy(out_xyz, use.at, sizeof(use.at));
  return 1;
}

void RefiiCoopClearExitUse(void) { refii::vfs::g_coop_exit_use.pending = false; }

int RefiiCoopGetOwnHero(void) {
  return refii_bridge_two_is_hero() != 0 ? 1 : 0;
}

/// Player two's weapon names for the hooks' table overrides. ALWAYS EMPTY: the names went with
/// the CoopHeroWeapons action — the HeroState carries weapon RECORDS and the pump draws them
/// itself (ApplyEquip / CoopAttachHeld). Kept so Fable2/src/hooks still links.
const char* RefiiCoopMeleeName(void) { return ""; }
const char* RefiiCoopRangedName(void) { return ""; }

/// Dress player two's body after the guest has built or re-typed it — see CoopRedress. Not
/// from inside the pump's own drain: the tick runs the same pass next.
void RefiiCoopRedress(void* ctx, uint8_t* base) {
  if (refii::vfs::g_pump_draining.load(std::memory_order_relaxed)) {
    return;
  }
  refii::vfs::CoopRedress(*static_cast<PPCContext*>(ctx), base);
}

/// Drop every latch keyed on one body — the hooks call it when the guest rebuilds a player
/// body in place (retype) or gives player two a new one.
void RefiiCoopForgetBody(uint32_t entity) { refii::vfs::CoopForgetBody(entity); }

/// Player two's current entity, or 0.
uint32_t RefiiCoopPlayerTwoEntity(uint8_t* base) { return refii::vfs::CoopPlayerTwoEntity(base); }

}  // extern "C"

// The local-player pick inlined in the pause-tab predicate (see g_menu_player_two): `li r4, 1` /
// `li r4, 2` by the session bytes, then `bl sub_82264230`. Player two's book states player two.
void RefiiCoopMenuPlayer(PPCRegister& r4) {
  if (refii::vfs::g_menu_player_two) {
    r4.u64 = 2u;
  }
}

// The same pick inlined in the world map's family list `lhWorldMap_BuildFamilyList` (TU1 0x8246F080,
// disc 0x8283ACA0: `bl sub_82264230` / `sub_822641F0` with r4 the player). Its seven callers are all the
// world map's (xrefs, both builds) — the population's Family rows `sub_829A42C8` / `sub_828F5248`, the
// category counts `sub_826CFD48` / `sub_82409828`, the trail marks `sub_829A3260` / `sub_828F41E0`, the
// family page's vector `sub_829B03F0` / `sub_82902858` (the page fill reads it), and the family row's
// answers `sub_826C6F40` / `sub_825A2E78`, `sub_826C7A40` / `sub_825A3970`, `sub_826C8588` /
// `sub_825A44B8` — so the list is the family of whoever's map is open: player two's while their pause
// is up.
void RefiiCoopFamilyListPlayer(PPCRegister& r4) {
  uint8_t* base = refii::vfs::g_guest_base.load(std::memory_order_relaxed);
  if (base != nullptr && refii::vfs::MenusActForPlayerTwo(base)) {
    r4.u64 = 2u;
  }
}

// The same pick inlined in the inventory lists, ON the `bl` after both `li r4` (TU1 / disc): the items
// rows `sub_829B1968` 0x829B19EC / `sub_828CAA88` 0x828CAB0C, the clothing rows `sub_829B3D28` 0x829B3DA0
// / 0x828CCEC0, the dye targets `sub_829B2168` 0x829B22B4 / 0x828CB3D4, the augment targets `sub_829B0BF0`
// 0x829B0CF0 / 0x828C9E10, the remover targets `sub_829B10A8` 0x829B11A8 / 0x828CA2C8, the gift
// `lhGuiGiftGoldScreen_PopulateMenu` 0x82430500 / 0x823A4690, the category counts `sub_826CF6A8`
// 0x826CF708 / 0x824091F8, the best ratings `sub_826CF1B8` 0x826CF218 / 0x82408D08 and the page's item
// info `sub_8246B0E8` 0x8246B1B4 / 0x82836DE4 (each `bl sub_82264230` / `bl sub_822641F0`, the record's
// +0xC+4 holder then its component 13). Every caller is a book of the in-game GUI (the pause root's counts
// `sub_8298DF98` among them), so the lists are the inventory of whoever's pause is up: player two's in
// local co-op. A joiner's machine picks 2 itself.
void RefiiCoopInventoryListPlayer(PPCRegister& r4) {
  uint8_t* base = refii::vfs::g_guest_base.load(std::memory_order_relaxed);
  if (base != nullptr && refii::vfs::MenusActForPlayerTwo(base)) {
    r4.u64 = 2u;
  }
}
