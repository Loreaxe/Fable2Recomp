// refii — the host bridge's shared exports: the ABI version, the save root and player state,
// the asset reader, the mod API, the app root, audio, controller input, the guest frame, the
// time base, achievements and rumble.

#include <algorithm>
#include <atomic>
#include <cctype>
#include <chrono>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <csignal>
#ifndef _WIN32
#include <dlfcn.h>
#include <execinfo.h>
#include <fcntl.h>
#include <sys/syscall.h>
#include <sys/resource.h>
#include <setjmp.h>
#include <ucontext.h>
#include <unistd.h>
#include <ctime>
#endif
#include <cstring>
#include <span>
#include <exception>
#include <filesystem>
#include <fstream>
#include <set>
#include <memory>
#include <mutex>
#include <sstream>
#include <string>
#include <string_view>
#include <thread>
#include <unordered_map>
#include <vector>


#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <dbghelp.h>
#endif

#include <achievement/achievement_runtime.h>
#include <rex/chrono/clock.h>
#include <rex/cvar.h>
#include <rex/logging.h>

namespace rex {
/// Defined in the runtime's logging.cpp and not declared by its header; the crash reporters
/// need it, since a record left in spdlog's queue when the process dies is never written.
void FlushLogging();
}  // namespace rex
#include <rex/system/kernel_state.h>

#include "core/told_statements.h"
#include "core/host_asset_reader.h"
#include "core/precise_sleep.h"
#include "core/payload_bridge.h"
#include "core/rex_payload_bridge.h"
#include <rex/memory/utils.h>
#include <rex/system/mmio_handler.h>
#include <rex/system/xmemory.h>
#include <rex/rex_app.h>
#include <rex/ui/windowed_app.h>

#include <rex/filesystem/entry.h>
#include <rex/filesystem/vfs.h>
#include <rex/perf/counter.h>
#include <rex/runtime.h>
#include <rex/system/thread_state.h>
#include <rex/system/xsocket.h>
#include <rex/system/xthread.h>

#include <array>
#include <set>
#include <utility>
#ifdef REXGLUE_ENABLE_PROFILING
#include <tracy/Tracy.hpp>
#include <tracy/TracyC.h>
#endif

#include "host_input.h"
#include "refii_host.h"

#include "host_abi.h"
#include "host_core.h"
#include "host_tracy.h"
#include "hang_watchdog.h"
#include "core/traced_mutex.h"
#include "core/audio_state.h"
#include "core/mod_values.h"
#include "core/event_relay.h"
#include "core/player_state.h"
#include "core/guest_tick_queue.h"
#include "core/gui_native_calls.h"
#include "core/queue_put_back.h"
#include "refii_payload/payload.h"
#include "core/mouse_look_state.h"
#include "core/movie_state.h"

using namespace refii::hostcore;

extern "C" {
/// ABI version. Bump on any breaking change so a stale library is detected rather than
/// crashing the editor.

///
/// 4 adds RefiiHostSetControllerState / RefiiHostGetVibration.
/// 5 adds RefiiHostGetVideoFrame.
/// 6 adds RefiiHostGetVideoPlanes (GPU-side colour conversion).
/// 7 adds RefiiHostGetRequestedVideo.
/// 8 adds RefiiHostGetRequestedVideoName.
/// 9 adds RefiiHostFinishMovie.
/// 10 adds RefiiHostGetFrontEndState.
/// 11 adds RefiiHostGetFrontEnd.
/// 12 adds RefiiHostGetUiElements (13 replaced a truncating slides-only form).
/// 14 adds RefiiHostMovieState.
/// 15 adds RefiiHostGetCamera.
/// 16 adds RefiiHostGetUiAttributes.
/// 17 widens RefiiHostGetFrontEnd to NINE words — the ninth is the active screen's vtable,
///    which is what names the screen the front end hands over to.
/// 18 adds a tenth: the GUI object's own vtable, without which the other nine cannot be
///    known to be about a front-end GUI at all.
/// 19 adds RefiiHostIsInGame and RefiiHostGetRenderFence — the guest's own in-game flag,
///    and the silent fence the level load waits on.
/// 20 adds RefiiHostGetLoadingScreen — the take-down handshake the game thread blocks on.
/// 126 adds RefiiHostGetCoopShares — the co-op screen's gold/experience shares, so the host
///     owns those labels instead of taking text the guest stops writing.
/// 125 adds RefiiHostGetCoopRole — 0 no session, 1 host (solo reads as host), 2 joiner, off
///     the co-op manager's own session/host/joiner bytes. The pause menu and the native-hero
///     work both need to know which side they are running on, and nothing else in the bridge
///     said.
/// 23 adds RefiiHostGetStartLevel — the world/level/entrance the guest asked for.
/// 22 adds RefiiHostGetSceneStreams — the command-stream rings, read-only.
/// 21 adds RefiiHostGetInitMarkers — how far Game_InitDatabases has got, which it does not
///    say itself.
/// 24 adds RefiiHostGetEntities — every live entity's world transform and model.
/// 25 names the placement vtables RefiiHostGetEntities could not resolve.
/// 142 widens RefiiHostGetEntities to stride 20: the guest's previous transform (component key
///     3 at +0xA0 position, +0xB0 quaternion, latched by the tick's first act `sub_8222B2E0`)
///     and its interpolation enable at +0xC9, the pair the guest blends in `sub_822112D8`.
/// 155 widens the entity walk to 29 words: the renderable's stated APPEARANCE — alpha, camera
///     alpha, scale, tint and the drawable/saturation flags — as the guest's appearance setters
///     stated it (hooked at their common tail `sub_82573318` in entity_appearance.cpp).
/// 154 adds RefiiHostGetDisplayBox — the showing display box and its class, so the owned query
///     box stands up for `GUI.Display*Question` as well as the modal router's QueryBox.
/// 153 adds the pub-game seam: RefiiHostClaimPubGames, RefiiHostPostPubGameCommand,
///     RefiiHostTakePubGameEvents, RefiiHostGetPubGameProfile; drops RefiiHostSetPubGameResult.
/// 152 adds RefiiHostTraceGpuPasses — Godot's per-pass GPU timestamps as Tracy GPU zones and
///     plots, so the GPU frame is split on the same timeline as the CPU phases.
/// 151 widens RefiiHostGetRealEstate to fifteen words: the hero's TITLE record, which is how
///     the guest names the owner of a hero-owned building (`sub_82998FB0` -> `sub_824D8A10`).
/// 150 widens RefiiHostGetWorldSprites to eight words: the ENTITY through the sprite's handle
///     (`*(*(obj+28))`; `obj+24`, published until now, is the handle's vtable and the SAME on
///     every sprite) and the sprite OBJECT as its identity.
/// 149 adds RefiiHostGetItemAugments — the augment pairs socketed into one inventory record,
///     off the item's own type-1 extra record (what `sub_82469AE8` prints on the item page).
/// 148 takes the targeting highlight from the guest's own setter (`sub_825738F0`, hooked in
///     target_highlight.cpp) instead of polling the renderable; {0,0,0,255} is the actively
///     written clear value, so a poll cannot tell it from "never targeted".
/// 147 adds RefiiHostGetStatedFx / RefiiHostGetStatedFxCount — the effect transforms the guest
///     states through `sub_832325A8` and `sub_83231C40`, recorded by a hook.
/// 146 widens the entity walk to 21 words: the renderable's stated highlight RGBA (+96..99).
///     Highlighted iff those bytes differ from {0,0,0,255}.
/// 145 widens RefiiHostGetEntityAnimState to twelve words a leaf: the record's weight_previous
///     and weight_current. The guest crossfades the winner against the runner-up by
///     `(1 - alpha) * winner.weight_previous + alpha * winner.weight_current` (0x82211B08..
///     0x82211B24 in `sub_82211660`, reading 0x14 and 0x18 off the winner record).
/// 144 adds RefiiHostGetAnimWalkStats — why a rig is not clip-driven, split into "no component
///     bit", "component unresolved", "controller found" and "controller found but empty".
/// 143 adds RefiiHostGetCameraPair — the previous camera the guest latched at the game tick
///     (`game+0x10`, stored by the inlined run at `0x82277234`) beside the live one, the pair
///     `sub_8228FEF0` blends by the alpha `sub_822112D8` gets for bodies. The camera-manager
///     chain is entry+4 -> player, +4 -> view, +4 -> manager; the live block is `manager+0x90`.
/// 26 returns that unknown-vtable list as {vtable, count} pairs with an explicit length,
///    since a vtable word reading 0 is an observation the walk can make and 25's zero
///    terminator swallowed the rest of the list.
/// 27 adds RefiiHostGetEntityPose — a live character's bone pose and its parent table.
/// 28 adds RefiiHostGetEntityRestPose — the bind pose Godot needs to build a skin.
/// 29 widens RefiiHostGetUiElements to stride 4, adding the element TYPE byte so the ordinal
///    join between the guest walk and the .bgf can be CHECKED, not assumed.
/// 30 widens it to stride 5, adding the element FLAGS halfword — bit 0x1000 `globalactive`
///    is the guest's own draw gate.
/// 31 adds RefiiHostGetFrontEndModel — the front end as a MODEL (which entries exist, which
///    screen is up) rather than as widgets, for a natively rebuilt menu.
/// 33 adds RefiiHostGetEntityBoneNames. The guest skeleton and the model number their bones
///    differently (161 against 143 for the child hero), so the name is the only join.
/// 34 widens RefiiHostGetRenderFence with the five values that say WHICH arm holds the
///    engine-preloading flag, and renames its third word from "retired" to the deadline it
///    actually is.
/// 35 adds RefiiHostPostFrontEndAction and RefiiHostGuestTickPump — the first route for HOST
///    work onto the guest's own tick thread. A memory write cannot do this job: the
///    start-game commit ALLOCATES, and its globals are refcounted strings.
/// 36 adds RefiiHostGetEntityComponents — the {id, pointer} pairs one entity carries, which
///    is a live-memory fact no decompile answers.
/// 37 adds RefiiHostGetPresentations and gives RefiiHostGetUiElements a leading presentation
///    argument (0 keeps the old front-end meaning). The old walker went through `gui+188`, a
///    field only the derived front-end GUI has, so the HUD and the thirty in-game menus were
///    unreachable through it.
/// 38 widens RefiiHostGetPresentations to stride 5, adding whether the presentation is
///    actually SHOWING — the root element's `active` bit (0x2000), the freeze byte, and the
///    guest's own per-frame active-element count. Live in the registry is NOT visible; the
///    guest keeps closed menus standing up permanently.
/// 39 adds the presentation-level SUPPRESSION byte `pres+0x368` to the state word (bit 2).
///    That is the byte the scene draw itself reads, and the one the game writes to park a
///    menu; the update gate `+0x369` and the root `active` bit are not the whole contract.
/// 139 adds the stated MOON PHASE to RefiiHostGetTimeOfDay (`timing+0x38`, the field
///     `Timing.GetMoonPhase`/`sub_825A1BF0` returns); -1 when it could not be read.
/// 138 gives RefiiHostGetUiAttributes two leading columns per element — the element address
///     and its name hash — so the caller can check the ordinal join.
///     The row stride is now `2 + id_count * 2`.
/// 40 gives RefiiHostGetUiAttributes a leading presentation argument, matching
///    RefiiHostGetUiElements — opacity is how the game hides the in-game HUD, one
///    `setAttribute` of 0 on Scene.Layer multiplied down the tree.
/// 41 adds RefiiHostGetEntityMorphParts — the model hashes a COMPOSITE character is built
///    from, off CECGraphicAppearanceMorph (component 17). Those characters carry a live
///    skeleton and a zero model hash on component 6, so this is the only route to geometry.
/// 42 widens RefiiHostGetFrontEndModel to 11 words: how many saves the guest has ENUMERATED,
///    from the same vector its own "Load Game" branch tests.
/// 43 adds the audio surface. RefiiHostPullAudio drains the guest's MIXED output and, by
///    draining it, drives the guest's audio clock — see host_audio.cpp. The RefiiHostVoice*
///    calls expose individual voices so Godot can place each sound itself.
/// 44 adds RefiiHostGetSimTick (dword_83319650, the guest's own 15 Hz game tick) and WIDENS
///    RefiiHostGetEntityPose with `out_torn`. That widening is the reason this list matters:
///    changing a signature is as much an ABI break as adding an export, it went in without a
///    bump, and the two sides drifted to 45-against-44. The mismatch is not caught cleanly —
///    it segfaulted in UI startup with one line of log to explain it.
/// 45 adds RefiiHostGetLoadingScreenArt and RefiiHostWorldIsReady — which loading screen the
///    guest picked, and its own "the world is ready" byte.
/// 50 adds RefiiHostWorldIsActive — World+0xA8, the ONE "the world is up and populated" bit
///    that both the travel and the load-from-save paths write. Replaces a host-side timeout.
/// 49 adds RefiiHostDescribeGameFlowQueue — the queue as text, because a walk of it kept
///    coming back empty while a level change was demonstrably in flight.
/// 48 adds RefiiHostSetWorldBuildPending — the host answering "IsEnginePreLoading" so the
///    guest waits in ITS OWN loading screen (state 7) until Godot has mounted the region.
/// 47 adds RefiiHostGetLevelChange — the queued CmdLevelChange itself: its state word, its
///    destination and its fullLoad flag. game+0x105 is a rising edge only (cleared in state 3,
///    AFTER the state-2 entity teardown), so it cannot say when to free a scene.
/// 46 adds RefiiHostGetCurrentLevel — the level the guest is STANDING IN, read from the live
///    World and Level objects. The start globals it replaces are written only by game-START
///    paths, so polling them cannot see a door, a teleporter or fast travel.
/// 51 adds RefiiHostGetEntityLocomotion — the locomotion state the guest is ACTUALLY IN, by
///    name. The hero's locomotion machine IS an entity mode (component 21, the mode whose
///    GetModeType() is 2/3/4), and it keeps its current state as a named object, so the host
///    can follow the guest's own choice instead of re-deriving it from inputs it would have to
///    guess at. The velocity/control pair it tests against is NOT included, and deliberately:
///    that context is a stack temporary built per tick in sub_8228D058 and is not resident.
/// 67 adds RefiiHostGetUiPreviewObject — the model hash and placement of the 3D object a menu
///    panel shows.
/// 97 moves ACHIEVEMENTS to Godot: RefiiHostGetTitleXdbf, RefiiHostSetAchievementUnlocks and
///    RefiiHostGetAchievementsUiRequests join RefiiHostGetAchievementUnlocks; the definition and
///    icon exports are gone with the native store. See Engine_Achievements.
/// 68 adds the ACHIEVEMENT exports — unlocks, definitions and icon PNGs. The runtime half
///    stays native: it is guest-facing and owns the XDBF. See Engine_AchievementToast.
/// 66 adds RefiiHostGetWorldIcons — the world-icon queue and its two gates, the channel
///    property income (icon type 37) and every scripted GUI.AddWorldIcon travel on.
/// 65 adds RefiiHostGetMovieRect — the destination rect the guest states for the live movie,
///    so an embedded preview is known to be embedded AT OPEN, not a sampling tick later.
/// 64 adds RefiiHostGetMenuBox — the Lua-opened option list's MODEL (open flag, count,
///    selection, cancelable, per-option enabled bits), a plain struct at `*(guiOwner+56)`,
///    not element state.
/// 63 widens RefiiHostGetPauseState with the modal MODE (`guiOwner+40`: 1 idle, 2 MessageBox,
///    3 QueryBox, 5 numeric adjuster) — what separates a box the host owns from one it must
///    leave mirrored.
/// 62 adds RefiiHostPauseTabsAvailable — `sub_826B83D0` as pure reads; the guest's tab push
///    never re-checks availability, so the host must not offer a tab prev/next would skip.
/// 61 adds RefiiHostGetPauseState and RefiiHostPauseTabSelect — the owned pause menu's read
///    and its dispatch. The dispatch is the guest's own lever: a pure write the per-tick GUI
///    update polls, after which the guest calls `sub_826B89E0` itself.
/// 60 adds RefiiHostGetSubtitles — the front-end subtitles setting as the guest's own reader
///    (`sub_82382558`) last answered on the tick thread, so the menu can mark the current
///    choice the way `sub_826E2220` does with SetItemEmphasis.
/// 59 adds RefiiHostGetDeadEntities — the guest's own DeadEntities set, so a destroyed
///    placement is not resurrected as the region's build-time copy standing in its place.
/// 58 adds RefiiHostGetEntityMeshParts — the mesh list on CECGraphicAppearanceStaticMultipleMesh
///    (component 5). Buildings and multi-part scenery keep their geometry there, not in the
///    single ModelFile slot, so an entity carrying one read as "names no model" and the region's
///    build-time copy was the only thing drawing it.
/// 76 adds RefiiHostGetLuaMenu — the expandable menu's own Lua model, read out of the
///    presentation's `lua_State`.
/// 78 adds RefiiHostGetHeroVitals and RefiiHostGetHeroInventory — hero component data as pure
///    reads. Displayed morality/purity/renown and ability ids 2/3/6 are excluded: they are
///    buffed or config-derived and can only be CALLED.
/// 77 adds RefiiHostGetScreenFade — the screen fade as a colour Godot can draw. Read-only:
///    the guest advances it at 15 Hz and two of its readers hang a level change if it stops.
/// 79 adds RefiiHostGetGameLoopPacer — `rex_lhGameLoop_Tick`'s 15 Hz fixed-step arithmetic.
///    `game+0x110` is the sim time; 0x83497124 is a 0/1 SLOT INDEX, not a count.
/// 80 adds RefiiHostTakeGuiNativeCalls — the ~120 `GUI.*` natives, intercepted by
///    implementation pointer in the game VM's `luaD_precall` (`sub_82228730`), with their
///    ARGUMENTS. Taking a call does NOT suppress it. See docs/ui_native_cut.md.
/// 81 adds RefiiHostMenuPopulating — the menu capture's settle bracket; cleared from the guest
///    tick, the first moment a population is whole.
/// 82 adds RefiiHostTakeMenuChunks — the menu contract at the C++ boundary: attributes staged
///    onto the "Parameters" element, then a one-line Lua chunk reading them back. Hooking the
///    two stagers and the runner captures every mutation. See docs/ui_native_cut.md.
/// 83 adds RefiiHostTakeUiElementChanges — element state taken at its writers: `sub_82284798`
///    (attribute-service vtable slot +0x48) for attributes, `sub_82284BE8` for the element
///    flags (0x1000 draw gate, 0x2000 active), `sub_82BB5760` for the slide.
/// 84 also walks TABLE arguments in RefiiHostTakeGuiNativeCalls, using the GAME VM's Table
///    layout — node count at +8, node array at +0x14, 20-byte nodes — by node scan, since this
///    build's string hash is MurmurHash2.
/// 84 widens RefiiHostGetUiElements to stride 6, adding the ELEMENT ADDRESS, which joins the
///    ordinal-indexed walk to the change feed.
/// 87 adds RefiiHostGetCompletedQuests (filtered on the guest thread, sorted by the caller on
///    the GDB property `DisplayPriority`), RefiiHostGetCrimeScreen, RefiiHostGetWorldMapRegion
///    / RefiiHostSetWorldMapRegion (the pump samples the named region, read back one tick
///    later) and RefiiHostGetOptions (pages 1 and 2).
/// 92 adds RefiiHostSetAssetNameResolver — Godot's hash index, the other half of 90; the guest
///    names most resources by a 32-bit hash rather than by path.
/// 101 adds RefiiHostGetSales — the SalesAndShortages list off the world, as {NameTagHash,
///    isShortage}. `record+20` is a raw FNV-1 text key, so again no string crosses.
/// 100 adds RefiiHostGetFastTravelRegions — the regions with an active stop, as FNV-1 of their
///    names, which are their own Babel keys. No string crosses the boundary.
/// 104 adds RefiiHostGetInventoryScreenMode — screen 15 is ONE screen with a mode word at
///     `screen+372` (1 Items, 3 Weapons, 4 Clothing), so its list cannot be bound by id alone.
/// 103 widens RefiiHostGetFastTravelRegions to {nameHash, ordinal} pairs and fixes its stride.
///     The container is pairs, not pointers — `sub_82518330` steps 8 — and event 41's handler
///     `sub_826C7E90` identifies a region by `Float1 - 999` indexed into that walk, never by
///     name, so the ordinal has to cross.
/// 102 adds RefiiHostGetFamily — the world map's `Family` names. The pump calls the guest's own
///     collector `sub_8246F020` while screen 23 is up and frees it with `sub_826C9450`; this
///     export only reads the shared store. Walking it here would mean reproducing an entity-UID
///     map lookup and a component-20 name fallback we have no reason to own.
/// 99 adds RefiiHostGetQuestLog — component 102's flat map at +0x10/+0x14, stride 12, `Quest*`
///    at element+4; RecordID at quest+4 and the five state bytes packed into one word. Quests and
///    Jobs are this one list split by the definition's GDB `Type`.
/// 98 adds RefiiHostGetWornItems — the hero's worn vector at component 15 + 16..20, stride 24,
///    element[0] an Entity* whose +4 is the GDB record hash and +16 the category. Nothing
///    synchronises it and a push reallocates, so the walk re-reads begin/end and discards a
///    torn read.
/// 90 adds RefiiHostSetAssetReader — the first call that goes the other way, handing the guest
///    Godot's asset reader so one mount serves both sides.
/// 89 widens RefiiHostGetWorldMapRegion by the region's ECONOMY. Not a field: `sub_8246FE20`
///    averages the region's villages on every ask, so the pump calls it.
/// 88 adds RefiiHostGetOptionsSliders — options pages 3 and 4 off the live screen object, not
///    the profile blob (freed before the call returns): gamma at `page3+0x30` (`sub_829C07D0`),
///    the three volumes and the speaker mode at `page4+0x2C/0x30/0x34/0x38` (`sub_829C1488`).
/// 86 adds RefiiHostGetHeroDerived — the values ABI 78 excluded, sampled by the pump on the
///    guest tick thread; this export only reads the shared store.
/// 85 adds RefiiHostSetGuiPredicate — suppression: the thunk writes the host's value onto the
///    Lua stack and returns without calling through. Opt-in by name, because fifteen of the
///    twenty predicates read state the guest's own setters maintain.
/// 106 adds RefiiHostGetRealEstate — screen 28's one building. Its left column is a list of
///     COMMANDS, not properties, so there is nothing to select and nothing to key on: one
///     record, sampled while the screen stands. The guest's own value functions answer every
///     number because each of them walks a prosperity term, a GDB adjustment array or a
///     five-axis opinion weighting; the host owns all of the presentation.
/// 107 widens RefiiHostGetQuestLog to EIGHT words — the star rating, both rewards, the
///     objective's `%1` parameter, the days remaining and the task count, all plain fields on
///     the quest object — and adds RefiiHostGetQuestTasks, which crosses one quest's task
///     lines as Babel TAGS. The world map's quest and job pages are blank without them: the
///     quest page is a single concatenated string and the task lines are most of it.
/// 108 was to have added HUD slide and image registry readers and does not: neither has a
///     readable address. See the note above RefiiHostGetHudTexts — the writers are
///     write-through into a runtime slide controller, and the image hash lives on the live node
///     rather than on the binding record this file's walker returns. Slides and textures
///     already cross per element through the ordinary element walk.
/// 109 widens RefiiHostGetSales to FIVE words — the description and region Babel key hashes,
///     the days left (`Duration - today + activationDay`) and the raw price multiplier. Two
///     words were enough to build the ROW and left the sales PAGE entirely blank.
/// 110 adds RefiiHostSet/GetQuestTravel — a job's distance and travel time. The two providers
///     are the region page's own and take an `LHString*`; the quest's `ObjectiveLevel` is inline
///     at `quest+132`, so no extra hop. The travel provider takes 0 here where the region page
///     takes 1, and under an hour it appends nothing — an empty travel time is shipped behaviour.
/// 111 widens RefiiHostGetQuestTravel by the objective level's REGION and the A-button
///     PROMPT. Neither is a field on the quest. The region is a lookup — `sub_829AFE58` asks
///     the region container which region owns the level (`sub_82518490`) and sets the element
///     by TAG. The prompt is a DECISION over four guest predicates (`sub_824707E8`,
///     `sub_82470F80`, `sub_82471438` and a level comparison), so the pump runs the guest's
///     own tests and crosses the tag it lands on, or nothing when the button is hidden.
///     Both lines were declared in the page and never assigned. It also adds
///     RefiiHostGetFamilyDetails — the FamilyPage had no provider at all, and it needed no new
///     walk: the pump already collects the 52-byte records for the row list, and ten of the
///     page's eleven elements are formatted strings sitting in them. And
///     RefiiHostSet/GetShopPrice — the shop's `PriceValue`, which the item book could never
///     answer because what a shop CHARGES is not what an item is WORTH.
/// 116 adds RefiiHostGetAttachments — every stated parent/child/dummy attachment, so Godot can
///     hang a weapon off ITS OWN bone. The guest recomputes an attached entity's placement from
///     its own pose every update; with the host playing the animation, that placement no longer
///     describes the body the child is attached to.
/// 117 widens RefiiHostGetEntityAnimState to TEN words a leaf — overlay membership, the mask
///     node and the priority bitmask — and adds RefiiHostGetAnimMask, so the host composites
///     overlays per bone the way `sub_8220CB18` does instead of folding them as primaries.
///     It also appends component 7's two eye models to RefiiHostGetEntityMorphParts.
/// 118 adds RefiiHostGetTopBoxBody — the front TopBox record's body as its provider object
///     states it, so the host composes the box the way `sub_821787D0` does instead of reading a
///     registry string that outlives its record.
/// 119 adds RefiiHostGetBoneMorph — the entity's combined skeletal morph record (comp6+152):
/// per-bone translation offsets and scales the guest applies after the animation fold — and
/// RefiiHostGetEntityWornItems, the hero walk of ABI 98 for any entity, with gender and open flags.
/// 120 adds RefiiHostGetTargetedQuery — the targeted-citizen record at `*(*(GUI+32)+112)`, so
/// the host can restate registry 10 (SlideSetName) and the four registry-9 toggles the guest
/// publishes from it. Neither registry is walkable: both are write-through.
/// 127 adds RefiiNetCoopSend / RefiiNetCoopReceive — Refii's OWN co-op channel, on its own
/// port, carrying host state the title has no concept of (which body player two is standing in).
/// Nothing the guest sends or parses changes, so a save stays a save.
/// 128 adds RefiiHostGetCoopMannequin — the co-op screen's spawned henchman preview. Screen 24
/// does not use the 128-byte preview struct the other six do: `sub_82438818` spawns a live
/// `CoopScreenTempHenchmanToDisplay` entity, stores it at `screen+276` and a preview-manager
/// ticket at `screen+284`. There is no model hash to hand over; the host draws the entity.
/// 129 adds RefiiHostGetHeroWeapons — the two GDB record ids the hero's weapon set holds
/// (`CECWeaponSetManager`, id 28: `+84` melee, `+88` ranged), the same record-id space the
/// inventory keys on. What player two's own hero carries across the wire.
/// 130 adds RefiiHostGetHeroLooks — the hero's character-creation record (comp 17 + 180, what
/// `sub_825B5F30` writes) and the three CECStats body scalars the vitals reader does not carry:
/// tan +52, fat +56 (both 0..1), height +60 (-1..1) — `sub_824D77F8` / `sub_824D7868` /
/// `sub_824D7A68` are the clamped setters, verified.
/// 131 adds RefiiHostGetCoopHeroSave — whether a co-op hero save has been chosen (the guest's
/// own predicate `sub_8243AE90`: the LHString at `SaveMgr+4` has a length) and the HeroSave block
/// `sub_824482E0` read into `SaveMgr+1016` for it, flattened with its two experience vectors.
/// 132 adds RefiiHostGetCoopHeroSaveName — the chosen co-op hero save's file name, the
/// `szFileName` the guest keeps at `SaveMgr+4`, which is the save's directory under
/// `user_data_root/<title folder>/00000001/`. The host reads that save's own files from it.
/// 133 adds RefiiHostGetCoopPlayerEntities — both players' entity addresses off the player map
/// (`game+0x9C`, keys 1 and 2, record+16 → control → entity), so the host can name which rig is
/// player two's when a co-op body draws wrong.
/// 134 adds RefiiHostGetCoopSession — the co-op session as ONE record off the authoritative
/// CNetworkManager (`g_XrnmNetContext`, 0x83497490): session +52, host +53, client +54,
/// lost-connection latch +238 (set first thing in HandleLostConnection 0x8232D4D8), client
/// joined +221 (set on "Client joined!", cleared in that handler), peer count +100, and both
/// players' entities off the player map. The host's session model reads this and nothing else.
/// 135 adds RefiiHostGetCoopHeldUids — the net ids of the item entities a player holds, as the
/// export's own comment cites it ("ABI 135"); 32-bit until 170.
/// 136 unknown: no comment in the code names what it added.
/// 137 adds RefiiHostGetGuestFrame — the guest's render frame, counted by the hook on the
/// 30 Hz HF pass (`sub_821A61B0`) that writes every pose. Godot reads guest state once per
/// count and interpolates between counts.
/// 156 adds RefiiHostGetEntityDrawRange — an entity's stated draw distance and fade factor off
/// its renderable (component 3 +0x28 / +0x24), the D the guest fades it out by and cuts its shadow at.
/// 157 adds RefiiHostGetWeather — the four precipitation floats of the guest's BLENDED
///     environment theme (T+0x480 SnowSize, +0x490 SnowFallSpeed, +0x4A0 RainSize, +0x4B0
///     RainDensity), the record its own rain and snow draws gate on. Indoors the painted
///     theme yields nothing and these collapse to zero, which is the only reason a room is dry.
/// 158 adds RefiiHostGetMovieSound — the sound the guest states for the movie it opened: the
///     opener's `sound` argument, the track ids of its `BinkSetSoundTrack`, and the volume it
///     states for each through `sub_82A41D88`. The host plays those tracks from its own decode;
///     the guest's copy is opened muted.
/// 159 adds the character morph feeds, each taken where the guest states it inside
///     `sub_8220DE88`: RefiiHostGetEntityPartMorphs — a part's morph targets and weights
///     (`sub_82A48B90`); RefiiHostGetEntityTextureLayers — every texture composite
///     (`sub_825B38B0`) and overlay (`sub_8222AAC8`, command type 14); and
///     RefiiHostGetBoneMorphHeight — the morph record's bone-1 height factor.
/// 160 adds dyeing: RefiiHostGetDyeStates — which garments are dyed — and the pump action
///     `Inventory:dye:<dye>:<target>:<slot>`.
/// 161 RefiiHostGetShopPrice also returns the row's item record (`*(*holder + 4)`), so the shop
///     page reads the item the ticket names instead of guessing it from the row's label.
/// 165 adds RefiiHostGetQuestTrailOverride — whether the trail follows a sale or family member
///     instead of the primary quest, which decides the world map's row emphasis.
/// 166 adds RefiiHostGetMinimapLive, widens RefiiHostGetWorldMapRegion by the region's first
///     level name, key and scenario, and RefiiHostGetQuestLog by each quest's objective entity —
///     the world map's minimap page, drawn by the host.
/// 164 adds RefiiHostGetSpellWheel — the quick spell wheel's row, cells and lists as the guest's
///     own widget holds them, so the host draws the guest's cursor instead of keeping one.
/// 163 adds RefiiHostGetThemeStack — the themes the guest has pushed on its environment theme
///     stack, by name hash and live weight, so Time Control's `EnvThemeSlowTime2` (and every
///     other pushed theme) blends over the host's own tone map at the guest's stated weight.
/// 162 adds RefiiHostGetClothingMannequin — screen 16's preview mannequin, which the remaster
///     draws for an outfit row and dresses through the pump's `Inventory:tryon`.
/// 167 adds RefiiHostGuestBuild / RefiiHostGuestAddress / RefiiHostGuestAddressToTu1 -- which
/// executable is running (0 TU1, 1 unpatched) and the address translation between the two, and
/// reports every GUI screen id in TU1 numbering whichever build is running.
/// 168 adds RefiiNetOwnSession / RefiiNetJoinSession -- our session id for a Discord join
///     secret, and a join from that material without a friend's presence.
/// 169 moves Discord into RexNet: RefiiNetDiscordActivity / RefiiNetDiscordEnabled feed the
///     SDK's own Rich Presence (application id from the title's rexnet.toml); the join
///     secret and the join itself are the node's, RefiiNetJoinSession routes through it.
///     Also adds RefiiNetDeclined / RefiiNetDelivered — the peers that dismissed, and the
///     peers that acknowledged, a friend request we sent — and widens RefiiHostGetCoopSession
///     from 10 to 12 words (game+284 mode, player two's pad) for a caller whose buffer asks.
/// 170 widens RefiiHostGetCoopHeldUids to the entity's 64-bit uid.
/// 171 adds RefiiNetJoinRequests / RefiiNetJoinRequestReply — the Discord "ask to join" prompt
///     the node holds for an answer, and the answer — and RefiiNetNotices, the node's own
///     word on an invite that could not be delivered or a join the host never answered.
///     Also adds RefiiHostGetHeroStateApplied — the HeroState version the pump last applied
///     to a player's current body, the ack Godot sends the joiner.
/// 172 adds RefiiHostGetItemDyeLayers — the texture layers the guest's own dye builder states
///     for one inventory item, worn or carried, so the menu shows a dyed garment dyed.
/// 173 adds RefiiHostGetClassicFrame — the frame-constant sources the title's render passes
///     read (ambient, sun, lightmap factor, normal darkening, the atmosphere block, the theme
///     block), RefiiHostGetClassicLights (the scene's active light list) and
///     RefiiHostGetClassicMists (its mist volumes), for the classic renderer's feed; replaces
///     the device constant shadow read, which is empty here.
/// 174 adds RefiiHostGetClassicParticles — the render scene's particle groups as their twins
///     hold them (records, material, keyframes, transform), for the classic particle draw.
/// 175 adds RefiiHostGetThemeBlends — the script theme blend list (`BlendToEnvironmentTheme`,
///     `mgr+148`), which the guest folds before the stack; RefiiHostGetThemeStack reads only the
///     spell list at `mgr+164`.
/// 181 adds RefiiHostGetEntityCasterFlags — an entity's stated sun-shadow casting off its
///     renderable (component 3 +0x8C, +0xC1), for the classic cascades; RefiiHostGetClassicTrails
///     also writes the displacement trails, flagged in header [55]; RefiiHostGetEntityInteriorMeshes
///     — a building's interior records, the weather's roofs; RefiiHostGetClassicWeather's header
///     carries the snow's push slots; RefiiHostGetDisplacementQuads — the drunk warp's quad;
///     RefiiHostGetFxMeshes — the effects' mesh particles (hooks rex_lhFx_CreateMeshDescriptor,
///     rex_lhRenderTwin_Destroy; since 243 rex_lhRender_ApplyCommandToHandle); RefiiHostGetEntityDisplacementParts — a composite's
///     displacement-only parts (GDB DisplacementMeshes); RefiiHostGetSpotFrustums — the spots'
///     receive frustums.
/// 180 adds RefiiHostGetDecals — the decals the guest stated (Fable2/src/vfs/src/hooks/decals.cpp) with their
///     owners' live matrices, for the host's decal draw.
/// 179 adds RefiiHostGetClassicWeather — the light zone, clock and lights the title's rain and
///     snow draws read, for the classic weather.
/// 182 adds RefiiHostTracy* (host_tracy.cpp) — the game's in-process Tracy capture: start, stop,
///     save, status and the dev menu's queries (frames, zones, statistics, plots, messages, GPU,
///     context switches, samples, disassembly).
/// 183 adds the Tracy lock and memory views (RefiiHostTracyGetLocks, GetMemoryPools,
///     GetMemoryLargest), Godot-side memory and locks (RefiiHostTraceAlloc/Free, TraceLock*),
///     find-zone and sample trees (RefiiHostTracyFindZone, GetZoneStatsRange, GetSampleTree),
///     RefiiHostTracyLive, and guest stack sampling (RefiiHostGuestSamplerStart/Stop).
/// 184 adds hard mods' named values: RefiiHostGetModValue (what a mod's hook published) and
///     RefiiHostSetModSetting (what Godot hands it), over Fable2/src/vfs/src/core/mod_values.cpp.
/// 185 adds RefiiHostGetSpellBlade — the abilities book's own copy of the spell-assignment
///     widget (screen 14 + 200), read exactly as the quick wheel's is, so the host draws ONE
///     owned widget for both trays.
/// 186 adds RefiiHostGetNewGameState, RefiiHostGetSaveRoot and RefiiHostGetGoldenCockInteraction
///     (NG+ and the golden cock).
/// 187 widens RefiiHostGetActiveFx to 22 words: the entity that OWNS each effect node (the
///     emitter component's owner at comp85+4, reached handle+0x28 -> &comp85+12) and the entity
///     it is ATTACHED to (comp85+0x20's box), so the host binds an effect to the cast it belongs
///     to by entity rather than by where it stands.
/// 188 adds the mod event bus for Godot scripts and mod input: RefiiHostDrainModEvents and
///     RefiiHostSetModEventMask (events the payload posts for the kinds the host asked for),
///     RefiiHostPostModInput (a mod's input.json action edge into the guest's InputAction event),
///     RefiiHostPostModCommand (a script's helper call or query, run in the guest tick),
///     RefiiHostDrainModErrors (the mods' Lua errors), RefiiHostModHandleAddress (a handle's entity).
/// 189 adds Godot script verdicts: RefiiHostSetModVerdictHandler (a host function the payload
///     calls on the guest thread inside a seam) and RefiiHostSetModVerdictMask (which kinds).
/// 190 adds RefiiHostGetPlayerCamera — one player's own live camera (their camera manager's
///     +0x90 block), for split screen: local player two has a camera manager of their own there
///     (coop_hero_hooks.cpp); and RefiiHostGetMenuOwner — which player owns the open in-game GUI,
///     the guest's own `sub_8236E400` sampled each guest tick (guest_tick_pump.cpp).
/// 191 adds RefiiHostTakeExitRefused — the players a region exit refused because their
///     partner was out of range since the last call (coop_hero_hooks.cpp, the travel rule).
/// 192 adds RefiiHostGetPlayerTwoPrompts — the things whose use prompt answers player two; and a
///     pooled world icon's entity is now its prompted thing (it was the weak reference's first word);
///     and RefiiHostGetExitWaiting — online, this machine's player waits at an exit for the partner.
/// 193 adds RefiiHostGetExitRequest — the online joiner's newest region-exit use (sequence and
///     guest position), restated to the host, whose exit update fires the exit standing there.
/// 194 adds player state (Fable2/src/vfs/src/core/player_state.cpp): RefiiHostPlayerStateOutgoing (what
///     this machine owes the co-op peer about the players' mod values), RefiiHostPlayerStateReceive
///     (one message from the peer) and RefiiHostPlayerStateDescribe (both players' values, for the
///     debug channel).
/// 195 adds partner consent (Fable2/src/vfs/src/core/coop_consent.cpp): RefiiHostConsentTake (a co-op
///     script's question for the partner — sleep or sex on a bed, or a mod's own question with its
///     text) and RefiiHostConsentGive (the answer).
/// 196 adds RefiiHostGetCollectedKeys — the silver keys in the world a player has already taken
///     (core/coop_collectables.cpp): (entity address, mask) pairs, bit 0 player one, bit 1 player two.
/// 197 adds the player's stated status and signals over RexNet presence (Fable 3's orb status
///     and orb emotes): RefiiNetSetPlayerStatus, RefiiNetSendPlayerSignal, and RefiiNetFriends'
///     records gain each friend's status and last signal (10 fields).
/// 198 adds player two's HUD on split screen: RefiiHostGetPlayerTwoTargetedQuery (their own
///     targeted-citizen readout, from the villager's opinion of player two) and
///     RefiiHostGetPlayerTwoHud (their readout's title and d-pad icons, from the guest's own
///     builders called for hero two).
/// 199 adds the mod event masks' second word, kinds 64..127: RefiiHostSetModEventMaskHigh and
///     RefiiHostSetModVerdictMaskHigh (Crime, Achievement, CollectableTaken, PartnerConsent).
/// 200 adds RefiiHostGetDrunkPlayers — which heroes are past the drunk warp's threshold, so each
///     split view draws its own player's warp.
/// 201 adds per-player use prompts on split screen: RefiiHostGetPlayerOneHiddenPrompts (things only
///     player two sees; RefiiHostGetPlayerTwoPrompts is now every thing player two sees, both
///     players' in-range tests run) and RefiiHostGetPlayerTwoHold (player two's own hold meter).
/// 202 adds per-player opinion icons and hold prompts: RefiiHostGetWorldSprites flag 4 (an icon of
///     player two's, its key ending "_P2"), RefiiHostGetPlayerTwoHoldPrompt (player two's own hold
///     prompt), and RefiiHostGetEntityUid / RefiiHostGetOpinionOfUid (a villager's opinion of
///     either player by its network id, for the online joiner's targeted readout).
/// 203 adds RefiiNetPeerName and RefiiNetPartnerName — the name to show for a peer by RexNet's rule
///     (a friend's display name, anyone else's pseudonym); co-op no longer carries names.
/// 205 adds RefiiHostGetProperties (a player's properties with their condition and repair bill) and
///     RefiiHostTakePropertyNotice (disrepair and repair outcomes).
/// 204 adds RefiiHostGetPromptTitles — the use-prompt text the other player would be shown, per
///     thing, for each split half to draw its own player's text — and
///     RefiiHostGetClassicSceneLights (every light of the render scene, for the classic renderer's
///     gather for player two's eye); RefiiHostGetClassicLights carries each light's address at [18].
/// 206 adds Fable 3's player-to-player partnerships: RefiiHostGetPartnership / RefiiHostSetPartnership
///     (marriage and business partnership between the two heroes in the game), RefiiHostGetProperties
///     flag 64 (a building shared with the holder's partner), and property notices 4..6 (owed gold
///     paid, a sale's half to the partner, a purchase shared).
/// 207 adds RefiiHostTakeEstateIntent — an online joiner's real-estate actions for its hero two,
///     sent to the host and run there for the host's hero two (the pump's Estate: action).
/// 208 adds RefiiHostGetItemCount — how many of one item definition a player's hero carries (the
///     player marriage asks the proposer for a ring) — and RefiiHostGetPlayerValue (a player store
///     value, e.g. the heroes' home counts the household tick publishes).
/// 209 adds RefiiHostRequestPropertyEstate / RefiiHostGetPropertyEstate — screen 28's page numbers
///     for the property under the world map's cursor — RefiiHostSetPlayerValue (a saved player
///     value written from the host, e.g. a tip shown once), and each property's purchase price as an
///     eighth RefiiHostGetProperties field.
/// 210 adds RefiiHostUiDataGeneration — bumped when anything an open menu lists changes (an ability
///     level, experience, an item, gold, a drawn weapon, the pump's own item/ability/shop/repair
///     actions); Godot re-reads the open list when it moves.
/// 211 adds RefiiHostGetCoopFigures — both co-op screen figures: player two's henchman and the
///     player-one mannequin the original spawns for the left page (found by its preview ticket) —
///     and retires RefiiHostGetCoopMannequin, which it replaces — and RefiiHostGetCoopScreen: each
///     page's focus and accept, and the save picker's state and card text, for the host-drawn
///     co-op screen.
/// 212 adds RefiiHostPostMouseLook / RefiiHostGetMouseLook — the mouse's camera turn, handed to the
///     active camera's own ProcessGameAction once per camera update, and what the camera took of
///     it, for the host's display-rate prediction.
/// 213 changes RefiiHostGetMouseLook: what each camera update took of the posted turn, the yaw the
///     camera turned for it in that update, and how the host draws a turn not yet taken (orbit
///     about the stated focus, in place, or not at all) — the camera now applies the mouse's turn
///     whole in the update it is handed, so the host draws it exactly instead of predicting it.
/// 214 adds RefiiHostSetMouseLookDevice — whether the mouse is the player's look device: the camera
///     it drives keeps its heading as the hero moves and a target lock takes it as flicks — and
///     RefiiHostGetMouseLook states the flicks handed to a lock.
/// 215 changes RefiiHostGetMouseLook to fill an array of doubles: the account gains the pitch
///     (posted, taken, absorbed), the AIM mode's pivot as a point in the camera's level frame and
///     its elevation bound, and the ranged aim whose reticule turns with the view — the mouse now
///     turns the ranged-aim camera and its aim whole in one update (Fable2/src/vfs/src/hooks/mouse_look.cpp).
/// 216 adds RefiiHostPostMouseWheel — the mouse wheel's notches in play, which switch the target of
///     a lock a mod states switches by the wheel (refii_mod_wheel_switches_target), one switch per
///     notch through the camera's own target switch.
/// 217 adds RefiiNetSyncConnect / State / Send / Receive / Close — Refii's mod sync stream: the
///     guest TCP shim's reliable, ordered rexnet stream on port 0xEF12, over which a co-op joiner
///     downloads the host's mod packages (Godot's ModSync; the shadowed xam_net.cpp owns the port).
/// 218 adds RefiiHostGetSaveCardNames — the load fan's save FILE names (the `szFileName` vector
///     at the front-end screen's +84, the join `LoadSave:N` commits by), so the host reads each
///     card's own sidecar (refii_save.json) for its mods marker and the export copy.
/// 219 adds the hang watchdog (hang_watchdog.cpp): RefiiHostFrameAlive (the main thread's
///     per-frame stamp; the first call starts the watchdog), RefiiHostRegisterFramePhase /
///     RefiiHostSetFramePhase (the frame phase the main thread is in, by registered index),
///     RefiiHostSetHostLogPath (Godot's log file, stated once so a report names it — never a
///     per-print feed: a managed Logger is a StringName-mutex hang vector) and
///     RefiiHostHangReport (what was written: stall count, last report path, last stall
///     length). A main thread that has not stamped for 8 s gets its native backtrace, every
///     thread's /proc state, the guest thread table and the memory numbers written to
///     exe_dir/logs/refii_hang_<utc>.txt.
/// 220 adds RefiiHostGetCurrentCulture — the locale code of the guest's current language row, so
///     the host's text book and fonts follow a language the player picks at the title.
/// 221 makes this library Godot's audio output. It is also a GDExtension
///     (`refii_godot_audio_init`, godot_audio_effect.cpp): its RefiiGuestAudioEffect writes
///     the guest's frame onto each speaker pair of a bus, so a 5.1 device gets the guest's 5.1
///     and a stereo one the SDK's ITU fold. RefiiHostAudioOutputConfigure / Detach / Stats
///     replace RefiiHostPullAudio, RefiiHostAudioChannels and RefiiHostAudioFloatsPerFrame;
///     RefiiHostAudioFrontGain(channels) gives host-decoded sound (movies) the fold's front
///     weight, since on the console it went through the same mastering voice.
/// 222 adds RefiiHostGetOptionsPage — one options page as the GUEST holds it: its row cursor and
///     every row widget's own pickbox index / slider value, so the host paints the page's captions
///     and pips from the state the guest's handler moves instead of stepping a record of its own.
/// 223 adds RefiiHostGetHudPulserStates — each HUD pulser's mode (`rec+12`: 0 looping, 1 finishing,
///     2 stopped) beside its phase, so the host's own pulse clock stops when the guest's does.
/// 224 adds RefiiHostGetQuestTaskStates — each task line's completed byte, beside the tags
///     RefiiHostGetQuestTasks crosses, for the pause root's objective line.
/// 225 adds RefiiHostGetFurniture — the upgrade-furniture screen's targeted piece — and
///     RefiiHostGetQueryCard, the villager card subject's rename facts, RefiiHostGetCoopState and
///     RefiiHostGetShopFront, and RefiiHostSetHighlightedRow.
/// 226 adds RefiiHostTakeToldStatements — every slide entry, play / pause, attribute and `active`
///     write the guest tells a presentation's elements — RefiiHostTakeToldTexts, its texts, and
///     RefiiHostFrontEndPresentation, the front end's key for them.
/// 227 adds RefiiHostGetLogbookOrbView — the other player's stats the logbook was opened on
/// from an orb's View Stats.
/// 228 adds RefiiHostGetClassicLocalLights — each local light's packed position (L+288, not
///     the manager's bounding sphere) and a shadowing spot's map parameters, for the classic
///     renderer's light block and spot shadow maps; and RefiiHostGetClassicShadowSpheres — the
///     render scene's shadow spheres (category 36) with their last two stated keyframes.
/// 229 adds RefiiHostSetConfigPath — the running title's config file, stated by the host
///     beside the app root, so each title's guest reads its own.
/// 230 carries two independent 229s: Fable 3's RefiiHostSetConfigPath above, and the lua branch's
///     Android port — RefiiHostSetControllerStateTracked, RefiiHostGetControllerReadReceipt,
///     RefiiHostSetGameModulePath — and RefiiNetGamerCard (the player an orb's Interact asked the
///     guide's gamer card for, resolved to a peer for the shell's Join / Invite question;
///     RefiiNetPeerAction's join also takes a non-friend's session from the shard beat).
/// 231 adds RefiiHostGetPlayerText — a player store's TEXT value, read synchronously beside
///     RefiiHostGetPlayerValue (RefiiHost.player_text / player_value_hash).
/// 232 adds RefiiHostModVerdictRead — a read-only query answered at once from inside a Godot
///     verdict handler, on the guest thread while the payload waits in the seam (RefiiHost.read).
/// 233 adds RefiiHostSetListFoldDepth — the fold depth of Godot's list per screen, which the
///     guest's `IsHighlightedItemAtRoot` readers answer from — and drops `ScreenAction:N:root`.
/// 234 adds RefiiHostSetLogDirectory — each title's logs in its own folder (logs/<Title>).
/// 235 restates RefiiHostGetMapMarkers: the pause minimap's markers as the guest names them to
///     its placer — token (0 Quest, 1 QuestExit, 2 QuestGiver) and world point — not the
///     placer's drawn quads.
/// 236 adds RefiiHostGetPendingWeapons — the weapon switches the menu has pending, which the
///     weapons list emphasises before the weapon in hand (`sub_826D0C08`).
/// 237 drops the built-presentation reads — RefiiHostGetUiElements, RefiiHostGetUiTextureHashes,
///     RefiiHostGetUiTextureStats, RefiiHostGetUiMovie, RefiiHostTakeUiElementChanges and
///     RefiiHostUiElementCounters: every Fable 2 presentation is headless (CGameface vtable +28),
///     so there is no element tree to walk and nothing on the host reads one.
/// 238 adds RefiiHostGetPlayerInventory — player 1's or 2's inventory, whoever's menus are open,
///     each record flagged worn, from the pump's per-player snapshot (Fable 2) — and
///     RefiiHostSetTimeBaseFrozen — intentional host clock freeze (pause, load cover) so
///     the driven-mode watchdog does not resume real-rate guest stepping behind the loading screen.
/// 239 widens RefiiHostGetActiveFx to 27 words: [22..25] an effect's stated colour (red, green,
///     blue, alpha), which Fable 3's guest applies at every draw (Fable 2 writes white), and [26]
///     its particle flushes; flags 128 (spline points stated) and 256 (saturation mask); adds
///     RefiiHostGetFxSpline — those points by handle.
/// 240 restates RefiiHostGetClothPieceVertices: a pose by index (0 the newest of up to four) with
///     the guest frame, fractional, of the instant it was posed for (a double), in place of the
///     current/previous game tick; RefiiHostGetEntityClothPieces' [6] is the poses kept.
/// 241 adds RefiiHostPlayerOwned: who this machine plays (3 both, 1 online host, 2 online joiner).
/// 242 adds RefiiHostGetEntityClothDistance — an entity's stated MaxClothDistance off its renderable
///     (component 3 +0x2C), the distance the guest's cloth gate steps an instance within.
/// 243 restates RefiiHostGetFxMeshes [15..19]: the tint and alpha the effect STATES to each twin
///     (model commands 17 and 16, taken at Render_ApplyCommandToHandle) in place of the twin's
///     +240 and its state block's interpolated alpha; RefiiHostGetEntities' appearance flags gain
///     4 = the definition's DisableAlphaDepthWrites (CECGraphicAppearance +192).
/// 244 keys Fable 2's dye reads by ITEM INSTANCE: RefiiHostGetDyeStates widens to three words
///     {item, record, flags} and RefiiHostGetItemDyeLayers takes the item's guest address; adds
///     RefiiHostGetHeroInventoryItems — the inventory snapshot with each record's item — and, for
///     the co-op kit, RefiiHostGetEntityWornDyes, RefiiHostGetEntityScars and
///     RefiiHostGetEntityGender.
/// 245 adds RefiiHostSetHostAim(active, fwd_x, fwd_y, fwd_z) for host-authoritative ranged aiming
///     (guest world space) and appends [15..17] (head_x, head_y, head_z in guest world space) and
///     [18] (flag bits: bit 0 LookInverted, bit 1 HostAimApplied, bit 2 HasHead) to
///     RefiiHostGetMouseLook.
/// 246 adds RefiiHostGetExpressionWheelPlayer (Fable 2) — the player whose expression wheel is open,
///     0 for none, as the wheel's open and close hooks state it, in place of the host peeking the
///     quick menu's open byte and the wheel controller's owner.
/// 247 adds RefiiNetNetwork — the node's own view of RexNet for the dev menu's Network tab: this
///     peer and its shard, the shard's live members, and every peer holding a virtual address.
/// 248 adds RefiiHostNoteEngineError and RefiiHostNoteEngineState — Godot's reported errors and
///     its own state line, kept for the crash record, which also gains the machine's free memory
///     and commit on Windows.
/// 249 RefiiHostGetPresentations is the shell's, for every title: the presentations the guest's
///     own create and destroy state are live, with the two gates as the guest last wrote them. No
///     guest memory is read; the root word and the active count are 0. Adds
///     RefiiHostPresentationsGeneration, which moves when that list changes, and
///     RefiiHostStatedScreen, the screen the guest's setter last said is up. The list's fifth
///     word is the screen id the guest loaded the presentation for, plus one. Removes
///     RefiiHostGetInGameScreen, which read the screen manager out of guest memory, and (Fable 2)
///     RefiiHostGetTargetedQuery: the readout's toggles and slide set are the registry writers'
///     statements and its entity the publisher's (`@Hud.Targeted`); and RefiiHostGetTopBoxFront
///     and RefiiHostGetTopBoxBody: the TopBox composer states its front record
///     (`@Hud.TopBoxFront`, `@Hud.TopBoxHidden`) and its texts (`@Gui.TextGroup`). Removes
///     RefiiHostGetMenuBox from the shell: a menu box is its populate, its cursor write and its
///     close (`@MenuBox.Open`, `@MenuBox.Select`, `@MenuBox.Close`).
/// 250 adds RefiiHostStatedValue, one word a title's own call stated, by key. Removes (Fable 2
///     read them out of guest memory) RefiiHostGetAbilitiesMode: the abilities screen's enter and
///     exit of spell assignment state it (key 1); RefiiHostGetInventoryScreenMode: the items
///     screen's enter shows its tab strip on 4 (items) or 5 (weapons), the stated tab;
///     RefiiHostGetUiAttributes; RefiiHostGetWorldIcons; and RefiiHostPauseTabSelect, which wrote
///     the tab strip's request fields: the pump action `PauseTab:<n>` calls the guest's own push;
///     and RefiiHostGetDisplayBox: a display box is its class's Open and Close.
///     RefiiHostGetLoadingScreen and RefiiHostGetLoadingScreenArt are the shell's, answered for
///     every title from the stated loading screen. Removes (Fable 2) RefiiHostGetLoadingScreenLayout:
///     fixed art and the text-only variant are the screen constructors' arguments and the quit fade
///     the art decision's (stated values 4, 5, 6).
///     Removes RefiiHostGetExpressionMeter: the meter's own tick, arm, judge, start and off state
///     its fields (`@Hud.ExpressionMeter`); and RefiiHostGetSpellWheel and RefiiHostGetSpellBlade:
///     the widget's open, show, move and close state its cursor and lists (`@Spell.Widget`).
/// 251 removes (Fable 2) RefiiHostGetOptionsPage: an options page's row cursor is its widgets'
///     SetSelected (`@Widget.Select`), a pickbox's index its SetIndex (`@PickBox.Set`) and a
///     slider's value its SetValue (`@Slider.Set`), each under the path the widget's Init was
///     handed. Fable 2's RefiiHostGetOptionsSliders answers from the profile's gamma getter and
///     setter and the audio manager's four level setters (stated values 7..12), not the screen.
/// 252 removes (Fable 2) RefiiHostGetQueryCard, RefiiHostGetCrimeScreen,
///     RefiiHostGetLogbookOrbView, RefiiHostGetClothingMannequin and RefiiHostGetCoopFigures: the
///     villager card's rename prompt is the guest's told slide; the crime screen is its opener's
///     arguments (`@CrimeScreen.Open`); another player's logbook is the stats fill's argument
///     (`@Logbook.Stats`); the preview figures are the preview scene's filing and destroying of
///     their records (`@Preview.Add` / `@Preview.Remove`).
/// 253 removes RefiiHostFrontEndGuiIsUp, RefiiHostGetFrontEnd, RefiiHostGetFrontEndInit,
///     RefiiHostFrontEndPresentation and RefiiHostGetSaveCards, and (Fable 2)
///     RefiiHostGetFrontEndModel and RefiiHostGetSaveCardNames: the front end is stated value 13,
///     kept from the front-end GUI's constructor and destructor, its screen switch, the screens'
///     activates, the posted presentation events and the screens' input slots; the save list is
///     the text builder's own pushes (`@FrontEnd.SaveList` / `@FrontEnd.SaveText`); the front-end
///     presentation is the stated live list's entry of that file.
/// 254 removes RefiiHostGetHudScriptMeters and RefiiHostGetHudScriptTexts (no caller: the script
///     store's setters state every meter and text through the registry writers) and (Fable 2)
///     RefiiHostGetHeroInventory, which RefiiHostGetHeroInventoryItems replaced; Fable 3 answers
///     RefiiNetHostState and RefiiNetSetJoinable, which the shared NetBridge binds.
/// 255 removes (Fable 2) RefiiHostGetUiPreviewObject and RefiiHostGetPendingWeapons: the previewed
///     item is stated from the preview's set and hide (`@PreviewItem.*`), the pending weapons from
///     the menu's pick and clear (stated values 15 and 16).
/// 256 removes (Fable 2) RefiiHostGetOptions: the options list's captions are the pickboxes the
///     screen's pages populate on its enter (`@PickBox.Set`).
/// 257 removes (Fable 2) RefiiHostGetCoopScreen and RefiiHostGetCoopState's two accepted words: the
///     co-op screen's enter, SetAccepted, finish and save picker state them (`@Coop.*`).
/// 258 adds RefiiHostGetCameraStatements: what each title's camera setters, activation, easing
///     step and push are called with, in the guest's order (src/runtime/include/core/
///     camera_statements.h); the shell rebuilds the camera from them.
/// 259 removes RefiiHostGetCamera, RefiiHostGetCameraPair and RefiiHostGetPlayerCamera: the shell
///     rebuilds every manager's camera from the statements of 258 (Godot/src/Engine/camera/
///     Engine_CameraFeed.cs); Fable 3 no longer reads the finished block at the view's fill or the
///     frame blend. Adds RefiiHostStateCamera: the shell states the drawn camera back for the hooks
///     that read it natively (Fable 3's mouse look and Refii.fov).
/// 260 removes (Fable 2) RefiiHostGetShopFront, RefiiHostSetShopPrice, RefiiHostGetShopPrice and
///     RefiiHostGetFurniture: the shop's front page, its asking price and the upgrade-furniture pages
///     are stated by the guest's own page fills (`@PageFill`, page_fills_stated.cpp).
/// 261 removes (Fable 2) RefiiHostGetRealEstate: screen 28's pages are stated by their own fills
///     (`@PageFill` scopes page1 / page2 / tick); the map's property row keeps RefiiHostGetPropertyEstate.
/// 262 narrows RefiiHostGetWorldMapRegion to the minimap's keys (index, name, first level, level key,
///     scenario; Fable 3 answers the same empty shape): the world map's region, quest, job, sale and
///     family pages are stated by their own fills (`@PageFill` scopes row.region .. row.family) and its
///     rows are the guest's own population. Removes (Fable 2) RefiiHostGetSales,
///     RefiiHostGetFastTravelRegions, RefiiHostGetFamily, RefiiHostGetFamilyDetails,
///     RefiiHostGetQuestTrailOverride, RefiiHostSetQuestTravel, RefiiHostGetQuestTravel and
///     RefiiHostGetQuestTasks.
/// 270 adds RefiiHostGetMenuRowItem — a captured row's icon, its stated mark and the item its row builder
///     was handed (Fable 2: the item books' rows are the guest's own, hooks/item_rows_stated.cpp; Fable 3
///     answers no row). Removes RefiiHostGetWornItems (both titles) and (Fable 2) RefiiHostGetDyeStates:
///     the lists' worn, held and empty-slot marks are the guest's statements; RefiiHostGetItemDyeLayers now
///     names the one item it reads.
/// 271 removes (Fable 2) RefiiHostGetItemAugments: the item books' pages are stated by their own fills
///     (`@PageFill` scope row on screens 15 / 16 / 25, with the quad colour kind `tint`), the augment lines in
///     the stated description.
/// 272 adds RefiiHostStateCameraViews — the shell states the screen and, per player, the pose it draws
///     and the part of the window it fills, every drawn frame (core/camera_views.h), for the camera mod
///     API's read calls — and RefiiHostGetCameraOffset: the display offset a mod stated (camera_offset).
/// 273 adds RefiiHostGetSystemMessageBox and RefiiHostAnswerSystemMessageBox: the guest's
///     XamShowMessageBoxUI is shown by the shell and answered with a button's index (rexglue shadow
///     xam_ui.cpp), for every title.
/// 274 makes achievements per character: RefiiHostGetAchievementUnlocks also gives each write's
///     binding serial; RefiiHostGetAchievementBindings gives the guest's adoption points' bindings
///     (user -> save); RefiiHostSetSaveAchievementUnlocks states every save's unlocked set for the
///     per-user enumerator and replaces RefiiHostSetAchievementUnlocks, which is removed.
/// 275 adds RefiiHostGetHeroSaveChoice and RefiiHostAnswerHeroSaveChoice: the save a joining player's
///     hero comes from is that player's choice, asked by the guest's join and answered by the shell
///     (achievement/hero_save_choice.h).
/// 276 adds (Fable 2) RefiiHostGetTargetedBy — who targets each thing, as the guest's targeted setter
///     leaves it — so each split view draws its own player's targeting highlight; and
///     RefiiHostGetPlayerTwoPrompts / RefiiHostGetPlayerOneHiddenPrompts now also attribute a targeted
///     thing's "Interact" icon (a villager's job title) to the players targeting it.
/// 277 adds RefiiHostSetPlayerText: the shell writes a player's session TEXT (core/player_state.h) — the
///     achievements statement (core/achievement_statement.h) both mod surfaces and the co-op partner read.
///     RefiiHostAnswerHeroSaveChoice also takes -3: the joining player cancels the join.
/// 278 removes RefiiHostGetWorldMapRegion (both titles; Fable 3 answered it empty) and (Fable 2)
///     RefiiHostSetWorldMapRegion: the world map's minimap page is stated by its own fill (`@PageFill` scope
///     row.minimap — its image, rotation, legend, projection and icons; minimap_page_stated.cpp).
/// 279 removes (Fable 2) RefiiHostRequestPropertyEstate and RefiiHostGetPropertyEstate: the world map's
///     Properties rows and pages are each hero's OWNED properties as the guest's own calls state them (the
///     purchases, the loader's per-record read, the removal and the per-property setters; Fable 2
///     hooks/src/property_list_hooks.cpp), which RefiiHostGetProperties now answers from; a property's page is
///     its record, no sign's numbers sampled for it.
/// 280 adds RefiiHostGetPartnerCameraAlphas (both titles): player two's camera's fades, its own book, stated by
///     the core camera chunk (Refii._partner_camera_alphas; core/payload_bridge.h). Player two's camera no longer
///     writes the renderable's camera alpha, which holds player one's camera's fades; the shell draws player
///     two's book in player two's half alone.
REFII_HOST_ABI_EXPORT int RefiiHostBridgeAbiVersion(void) { return 280; }

/// Player two's camera's fade book: up to `capacity` guest entity addresses and the camera alpha it gives each;
/// returns how many the book holds (may exceed `capacity`), `out_serial` (may be null) one more at every
/// statement.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetPartnerCameraAlphas(uint32_t* out_entities, float* out_alphas,
                                                               uint32_t capacity, uint32_t* out_serial) {
    return refii_bridge_partner_camera_alphas_read(out_entities, out_alphas, capacity, out_serial);
}

extern "C" uint32_t RefiiSystemMessageBoxFront(char* out, uint32_t capacity);
extern "C" void RefiiXamNotePad(uint32_t user, int32_t connected);
extern "C" int RefiiSystemMessageBoxAnswer(uint32_t id, int32_t button);
extern "C" uint32_t RefiiHeroSaveChoiceFront(char* out, uint32_t capacity);
extern "C" int32_t RefiiHeroSaveChoiceAnswer(uint32_t id, int32_t choice);

/// The oldest system message box the guest waits on (XamShowMessageBoxUI), fields split by 0x1F:
/// id, user, flags, focused button, button count, title, text, the captions. 0 when none waits.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetSystemMessageBox(char* out, uint32_t capacity) {
    return RefiiSystemMessageBoxFront(out, capacity);
}

/// The shell's answer to system message box `id`: a button's index, -1 to cancel it. 0 when the
/// box is gone or has no such button.
REFII_HOST_ABI_EXPORT int RefiiHostAnswerSystemMessageBox(uint32_t id, int32_t button) {
    return RefiiSystemMessageBoxAnswer(id, button);
}

/// The save choice a joining player's hero waits on (the guest's join, achievement/hero_save_choice.h),
/// fields split by 0x1F: id, user, the profile's save count, the choice count, each save's name in the
/// guest's own order of preference. The record's length; 0 when none waits. Copied when it fits.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHeroSaveChoice(char* out, uint32_t capacity) {
    return RefiiHeroSaveChoiceFront(out, capacity);
}

/// The shell's answer to save choice `id`: a save's index, or -1 for a new hero. 0 when the question is
/// gone or the index is out of range.
REFII_HOST_ABI_EXPORT int RefiiHostAnswerHeroSaveChoice(uint32_t id, int32_t choice) {
    return RefiiHeroSaveChoiceAnswer(id, choice);
}

/// The loading screen, as the title's own calls state it: made by its manager, the fade its tick
/// leaves, the take-down requested and finished. Returns 1 while a screen exists.
REFII_HOST_ABI_EXPORT int RefiiHostGetLoadingScreen(uint32_t* out_manager, uint32_t* out_screen, float* out_fade,
                                                   uint32_t* out_requested, uint32_t* out_done) {
    return refii_bridge_loading_screen_read(out_manager, out_screen, out_fade, out_requested, out_done, nullptr,
                                            nullptr, nullptr, 0u);
}

/// The art the guest chose for its loading screen and the screen's kind, each in the title's own
/// terms, as its art decision stated them. Returns 1 when a name is held.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetLoadingScreenArt(uint32_t* out_type, uint32_t* out_body, char* name,
                                                           uint32_t capacity) {
    if (out_type != nullptr) *out_type = 0u;
    if (out_body != nullptr) *out_body = 0u;
    if (name != nullptr && capacity > 0u) name[0] = '\0';

    uint32_t kind = 0u;
    uint32_t body = 0u;
    if (refii_bridge_loading_screen_read(nullptr, nullptr, nullptr, nullptr, nullptr, &kind, &body, name,
                                         capacity) == 0) {
        if (name != nullptr && capacity > 0u) name[0] = '\0';
        return 0u;
    }

    if (out_type != nullptr) *out_type = kind;
    if (out_body != nullptr) *out_body = body;
    return name != nullptr && capacity > 0u && name[0] != '\0' ? 1u : 0u;
}

/// The camera statements after sequence `after`, oldest first, refii::camera::kRecordWords words
/// each, up to `max_records`; returns how many (src/runtime/include/core/camera_statements.h).
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetCameraStatements(uint32_t after, uint32_t* out_words,
                                                            uint32_t max_records) {
    return refii_bridge_camera_statements_read(after, out_words, max_records);
}

/// The camera the shell draws, as the guest's block (position, up, forward, horizontal FOV in
/// radians, cut), stated back each frame for the hooks that read it natively.
REFII_HOST_ABI_EXPORT void RefiiHostStateCamera(const float* eleven) {
    if (eleven != nullptr) {
        refii_bridge_camera_stated(eleven, eleven + 3, eleven + 6, eleven[9], eleven[10] != 0.0f ? 1 : 0);
    }
}

/// The screen and the views the shell draws this frame, refii::camera::kViewsWords floats
/// (src/runtime/include/core/camera_views.h).
REFII_HOST_ABI_EXPORT void RefiiHostStateCameraViews(const float* words, uint32_t count) {
    refii_bridge_camera_views_stated(words, count);
}

/// The display offset stated for `player` (1 or 2): seven floats into `out7` (right, up, back in metres
/// along the drawn camera's axes; yaw, pitch, roll, fov in degrees). 1 while one is stated.
REFII_HOST_ABI_EXPORT int32_t RefiiHostGetCameraOffset(int32_t player, float* out7) {
    return refii_bridge_camera_offset_read(player, out7);
}

/// The word a title's own call last stated under `key` (payload_bridge.h lists the keys); 0 until
/// one is.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostStatedValue(uint32_t key) { return refii_bridge_stated_value_now(key); }

/// The presentations the guest has live, five words each: address, its creation serial (a later
/// one is larger), name offset, state, and the screen id the guest loaded it for plus one (0 for
/// none). State bit 0 is set (live), bit 1 the
/// update gate is parked, bit 2 the draw gate is parked, bit 3 is set (stated, not read from a
/// tree). The names go NUL-separated into `names`. Returns how many there are, which may be more
/// than fitted.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetPresentations(uint32_t* words, uint32_t word_capacity, char* names,
                                                         uint32_t name_capacity) {
    constexpr uint32_t kMost = 256u;
    uint32_t told[kMost * 5u] = {};
    const uint32_t count = refii::vfs::ToldHeadlessList(told, kMost * 5u, names, name_capacity);
    const uint32_t listed = count < kMost ? count : kMost;
    for (uint32_t i = 0; i < listed; ++i) {
        if (words == nullptr || i * 5u + 4u >= word_capacity) {
            break;
        }
        const uint32_t gates = told[i * 5u + 3u];
        words[i * 5u] = told[i * 5u];
        words[i * 5u + 1u] = told[i * 5u + 1u];
        words[i * 5u + 2u] = told[i * 5u + 2u];
        words[i * 5u + 3u] = 9u | ((gates & 2u) != 0u ? 2u : 0u) | ((gates & 1u) != 0u ? 4u : 0u);
        words[i * 5u + 4u] = told[i * 5u + 4u];
    }
    return count;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostPresentationsGeneration(void) {
    return refii::vfs::ToldHeadlessGeneration();
}

/// The screen the guest's screen setter last said is up, in the title's numbering; 0xFFFFFFFF when
/// it said none is.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostStatedScreen(void) { return refii_bridge_stated_screen_now(); }

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetSaveRoot(char* output, uint32_t capacity) {
    auto* state = rex::system::kernel_state();
    if (!output || capacity == 0 || !state || !state->emulator()) return 0;
    const auto path = state->emulator()->user_data_root().u8string();
    if (path.empty() || path.size() >= capacity) return 0;
    std::memcpy(output, path.data(), path.size());
    output[path.size()] = 0;
    return 1;
}

}  // extern "C"

namespace {

/// The running title's savegame folder of the signed-in profile, for the player state's save
/// files: the content manager's own path for savegame content (type 1), so it is the folder the
/// guest's saves are created in.
std::string PlayerStateSaveRoot() {
    auto* state = rex::system::kernel_state();
    if (!state || !state->emulator() || state->emulator()->user_data_root().empty() ||
        !state->content_manager() || !state->user_profile()) {
        return {};
    }
    const auto path = (state->content_manager()->ResolveTitleRoot(
                           state->user_profile()->xuid(), rex::system::xam::kCurrentlyRunningTitleId) /
                       "00000001")
                          .u8string();
    return std::string(path.begin(), path.end());
}

const bool g_player_state_rooted = [] {
    refii::players::SetSaveRootResolver(&PlayerStateSaveRoot);
    return true;
}();

}  // namespace

extern "C" {

/// The players' mod values and the relayed mod events this machine owes the co-op peer, one
/// message per line (see core/player_state.h, core/event_relay.h). Called every frame; the caller
/// sends them only inside a session.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostPlayerStateOutgoing(char* out, uint32_t capacity) {
    if (out == nullptr || capacity == 0) {
        return 0;
    }
    size_t written = refii::players::TakeOutgoing(out, capacity);
    written += refii::relay::TakeOutgoing(out + written, capacity - written);
    return static_cast<uint32_t>(written);
}

/// One co-op channel message; 1 when it was player state or a relayed event (taken), 0 for
/// anything else.
REFII_HOST_ABI_EXPORT int32_t RefiiHostPlayerStateReceive(const char* message, uint32_t length) {
    if (message == nullptr) {
        return 0;
    }
    const std::string_view text(message, length);
    return refii::relay::Receive(text) || refii::players::Receive(text) ? 1 : 0;
}

/// Both players' mod values and who owns them, as one line. Returns the bytes written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostPlayerStateDescribe(char* out, uint32_t capacity) {
    if (out == nullptr || capacity == 0) {
        return 0;
    }
    const std::string text = refii::players::Describe();
    const size_t length = std::min<size_t>(text.size(), capacity - 1);
    std::memcpy(out, text.data(), length);
    out[length] = 0;
    return static_cast<uint32_t>(length);
}

/// Who this machine plays (core/player_state): 3 both (alone, couch co-op), 1 player one (online host),
/// 2 player two (online joiner), as the title states it each tick (Fable 2 DrivePlayerState, Fable 3
/// StateCoop).
REFII_HOST_ABI_EXPORT uint32_t RefiiHostPlayerOwned(void) { return refii_bridge_player_owned(); }

/// The mouse's account as of the last camera update (core/mouse_look_state.h), into
/// `out_values` (up to `count`): [0] the game tick of the last take; [1] yaw posted, [2] yaw taken
/// (cumulative, one origin, posted degrees), [3] yaw the camera turned for it (cumulative, its
/// degrees, rightward); [4] pitch posted, [5] pitch taken, [6] pitch the camera turned for it
/// (upward); [7] the camera's degrees per posted degree; [8..10] the pivot — for `mode` 1 the
/// focus's distance along the level line of sight in [8], for 3 the point [8] metres along the
/// camera's level forward, [9] along its level right, [10] up; [11] how a turn not yet taken is
/// drawn — 0 not at all, 1 orbit about the focus, 2 in place, 3 rigidly about the pivot with the
/// ranged aim (yaw, then pitch about the level right axis); [12] the target switches the mouse
/// handed to a lock, by flick or wheel notch (run total); [13] for mode 3 the camera's elevation bound in degrees; [14] the
/// ranged aim object whose reticule turns with the view (mode 3), else 0; [15..17] hero head pivot
/// in guest world space (x, y, z) when reported; [18] flag bits (bit 0 LookInverted, bit 1
/// HostAimApplied, bit 2 HasHead). Returns the state's
/// sequence — odd while an update is between taking and stating, so a read that straddles one can
/// be told from a whole one.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetMouseLook(double* out_values, uint32_t count) {
    return refii::vfs::MouseLookState(out_values, count);
}


/// A player's store value (core/player_state.h): 1 with it in `out`, 0 when the key is unset.
REFII_HOST_ABI_EXPORT int32_t RefiiHostGetPlayerValue(int32_t player, const char* key, float* out) {
    return key != nullptr ? refii_bridge_player_get(player, key, std::strlen(key), out) : 0;
}


/// A player's store TEXT (core/player_state.h): its length with the text in `out` (NUL-terminated,
/// cut to `capacity`), -1 when the key holds no text.
REFII_HOST_ABI_EXPORT int32_t RefiiHostGetPlayerText(int32_t player, const char* key, char* out, uint32_t capacity) {
    return key != nullptr ? refii_bridge_player_get_text(player, key, std::strlen(key), out, capacity) : -1;
}


/// Pump health: ticks observed, actions run, actions still queued.
///
/// Three numbers rather than one because they fail differently. Zero ticks is a hook that
/// never landed. Ticks climbing with a non-zero queue is the guest running but not yet ready
/// to take the action. Ticks climbing with an empty queue and no drains is nobody posting.
REFII_HOST_ABI_EXPORT void RefiiHostGuestTickPump(uint64_t* out_ticks, uint64_t* out_drained,
                            uint32_t* out_queued) {
    refii::vfs::GuestTickPumpCounters(out_ticks, out_drained, out_queued);
}


/// Asks the guest to perform a front-end action, BY NAME.
///
/// It does not happen here and it does not happen now. The name is queued, and the guest
/// picks it up on its own tick thread, in the title's tick pump hook (core/guest_tick_queue.h). That
/// indirection is the entire point: this call is safe from Godot's thread precisely because
/// it touches no guest state, and the work lands where the guest's own code would do it.
///
/// Returns the queue depth after posting. A depth that climbs is a pump that has stopped
/// draining — usually because the front end is not up, which holds actions rather than
/// dropping them.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostPostFrontEndAction(const char* name) {
    return refii::vfs::PostGuestTickAction(name);
}


/// Adds the mouse's camera turn since the last post: degrees at the profile's middle camera
/// sensitivity, right and up positive. The title's camera hook hands all of it to the local
/// player's active camera before that camera's next update.
REFII_HOST_ABI_EXPORT void RefiiHostPostMouseLook(float yaw_degrees, float pitch_degrees) {
    if (std::isfinite(yaw_degrees) && std::isfinite(pitch_degrees)) {
        refii::vfs::MouseLookPost(yaw_degrees, pitch_degrees);
    }
}


/// Adds mouse-wheel notches made while the mouse is the camera's, rightward positive: a notch down
/// +1, up -1. The local player's next camera update takes them; under a lock a mod states switches
/// by the wheel each is one target switch, to the right or the left, and otherwise they are
/// dropped.
REFII_HOST_ABI_EXPORT void RefiiHostPostMouseWheel(int32_t notches) {
    refii::vfs::MouseLookWheel(notches);
}


/// Whether the mouse is the player's look device (the host's device switch, stated every frame).
/// While it is, the camera it drives keeps its heading as the hero moves, and a target lock takes
/// the mouse as flicks between targets; a pad keeps the shipped camera.
REFII_HOST_ABI_EXPORT void RefiiHostSetMouseLookDevice(int32_t mouse) {
    refii::vfs::MouseLookSetDevice(mouse != 0);
}


/// Sets or clears the host-authoritative ranged aim direction (unit vector in guest world space).
/// While active, the guest's RangedCombatCamera adopts this forward direction directly instead of
/// chasing an eased cursor inside a cone.
REFII_HOST_ABI_EXPORT void RefiiHostSetHostAim(int32_t active, float fwd_x, float fwd_y, float fwd_z) {
    refii::vfs::MouseLookSetHostAim(active != 0, fwd_x, fwd_y, fwd_z);
}


/// Writes a player's session TEXT (core/player_state.h) for `player` (1 or 2): never saved, the same on
/// both co-op machines (the owner's write goes to the peer, a peer's to the owner). Returns 1 when written.
REFII_HOST_ABI_EXPORT int32_t RefiiHostSetPlayerText(int32_t player, const char* key, const char* text) {
    if (key == nullptr || (player != 1 && player != 2)) {
        return 0;
    }
    const char* value = text != nullptr ? text : "";
    refii_bridge_player_write_text(player, key, std::strlen(key), value, std::strlen(value), 0);
    return 1;
}


/// Writes a SAVED player value (core/player_state.h) for `player` (1 or 2), as a mod's would: kept
/// with that player's save, the same on both machines. Returns 1 when written.
REFII_HOST_ABI_EXPORT int32_t RefiiHostSetPlayerValue(int32_t player, const char* key, float value) {
    if (key == nullptr || (player != 1 && player != 2)) {
        return 0;
    }
    refii_bridge_player_write(player, key, std::strlen(key), value, 0, 1);
    return 1;
}

// =================== The movie the guest states (core/movie_state.h) ===================
//
// A title's Bink hooks state each call its players make into refii_runtime; Godot's GameVideo
// plays the movie natively from these and tells the guest when its copy has ended.

/// Copies the .bik the guest handed its Bink open, so the HOST can play it.
///
/// A player opened from a buffer the guest has already read (the startup logos) hands the bytes
/// straight over — no second read of a file already paid for. A movie opened BY NAME carries no
/// buffer and comes back from RefiiHostGetRequestedVideoName, which the host resolves itself.
///
/// Pass a null buffer to probe the size. `out_generation` changes on every request, including a
/// repeat of the same movie. Returns 1 on success, 0 when nothing has been requested or the
/// buffer is too small.
REFII_HOST_ABI_EXPORT int RefiiHostGetRequestedVideo(unsigned char* out, uint32_t capacity,
                                                     uint32_t* out_size, uint32_t* out_generation) {
    uint32_t address = 0;
    uint32_t size = 0;
    uint32_t generation = 0;
    uint32_t swapped = 0;
    refii::movie::Requested(&address, &size, &generation, &swapped);

    if (out_generation != nullptr) {
        *out_generation = generation;
    }
    if (out_size != nullptr) {
        *out_size = size;
    }

    if (address == 0 || size == 0) {
        return 0;
    }

    if (out == nullptr) {
        return 1;  // size probe
    }

    if (capacity < size) {
        return 0;
    }

    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    // A loader that byte-swaps every dword on the way in — a big-endian console reading a
    // little-endian container — leaves 'iKIB' in memory rather than 'BIKi', which decodes as
    // nothing. Swapped back on the way out, the bytes are the file as it is on disc; a trailing
    // partial dword keeps its order, as it was never a whole word to swap.
    CopyGuest(base, address, out, size);
    if (swapped != 0) {
        rex::memory::copy_and_swap_32_unaligned(out, out, size / 4u);
    }
    return 1;
}

/// The PATH form of the same request — "game:\data\art\videos\....bik", resolved by the host
/// against the videos folders it indexes. `out_generation` is the SAME counter as the buffer
/// form: a request is one or the other. Returns the length written, 0 when the last request
/// carried a buffer instead.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetRequestedVideoName(char* out, uint32_t capacity,
                                                              uint32_t* out_generation) {
    uint32_t generation = 0;
    refii::movie::Requested(nullptr, nullptr, &generation, nullptr);
    if (out_generation != nullptr) {
        *out_generation = generation;
    }

    return refii::movie::RequestedName(out, capacity);
}

// ---------------------------------------------------------------- the UI cut's seam
// Shared: every title's game Lua VM has a `GUI` namespace, and a title's luaD_precall hook posts
// the calls into core/gui_native_calls.cpp; these drain and answer them for whichever title runs.

/// Answers one `GUI` predicate from the host, by name.
///
/// `kind` is 0 to clear (the guest's implementation runs again), 1 boolean, 2 float. `bits` is
/// 0/1 for a boolean and the float's raw bits for a float.
REFII_HOST_ABI_EXPORT void RefiiHostSetGuiPredicate(const char* name, uint32_t kind,
                                                    uint32_t bits) {
    if (name == nullptr) {
        return;
    }

    refii::vfs::SetGuiNativeAnswer(name, static_cast<refii::vfs::GuiNativeAnswerKind>(kind), bits);
}

/// Drains the `GUI.*` native calls intercepted since the last drain.
///
/// Each call arrives with the name the script used and the values it passed, before the
/// guest's own implementation has touched them. The encoding is flat: `out_utf8` receives
/// NUL-separated fields, `out_kinds` one byte per field. Each record is:
///
///     name \0 arg0 \0 arg1 \0 ... \0
///
/// with `out_kinds` holding the record's argument count followed by one kind byte per argument
/// (0 none, 1 number, 2 boolean, 3 string, 4 other). Numbers arrive as decimal text and
/// booleans as "0"/"1".
///
/// `lua_Number` is `float` in this title: the payload reads `*(float*)tv`, and taking eight
/// bytes as a double gives plausible garbage.
///
/// Returns the number of records written. Zero means nothing was called, which is the normal
/// answer on most frames.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostTakeGuiNativeCalls(char* out_utf8, uint32_t utf8_capacity,
                                                           uint8_t* out_kinds,
                                                           uint32_t kinds_capacity,
                                                           uint32_t max_records) {
    if (out_utf8 == nullptr || out_kinds == nullptr || utf8_capacity == 0 || kinds_capacity == 0) {
        return 0;
    }

    std::vector<refii::vfs::GuiNativeCall> calls = refii::vfs::TakeGuiNativeCalls();
    uint32_t written = 0;
    uint32_t text_at = 0;
    uint32_t kind_at = 0;

    const auto put = [&](const std::string& field) {
        if (text_at + field.size() + 1u > utf8_capacity) {
            return false;
        }
        std::memcpy(out_utf8 + text_at, field.data(), field.size());
        text_at += static_cast<uint32_t>(field.size());
        out_utf8[text_at++] = '\0';
        return true;
    };

    // The first call NOT handed over; it and everything after go back to the front of the queue.
    std::size_t handed = 0;
    for (const refii::vfs::GuiNativeCall& call : calls) {
        if (written >= max_records) {
            break;
        }
        if (kind_at + 1u + call.arguments.size() > kinds_capacity) {
            break;
        }

        const uint32_t text_mark = text_at;
        const uint32_t kind_mark = kind_at;
        bool ok = put(call.name);
        out_kinds[kind_at++] = static_cast<uint8_t>(call.arguments.size());

        // A table argument spends more than one kind byte, so each write tests for itself and
        // a record that runs out of either buffer is rolled back whole.
        const auto put_kind = [&](refii::vfs::GuiNativeArgument::Kind kind) {
            if (kind_at >= kinds_capacity) {
                return false;
            }
            out_kinds[kind_at++] = static_cast<uint8_t>(kind);
            return true;
        };

        for (const refii::vfs::GuiNativeArgument& argument : call.arguments) {
            if (!ok) break;
            ok = put_kind(argument.kind);
            if (!ok) break;
            switch (argument.kind) {
                case refii::vfs::GuiNativeArgument::Kind::kNumber:
                    ok = put(fmt::format("{}", argument.number));
                    break;
                case refii::vfs::GuiNativeArgument::Kind::kBoolean:
                    ok = put(argument.boolean ? "1" : "0");
                    break;
                case refii::vfs::GuiNativeArgument::Kind::kString:
                    ok = put(argument.text);
                    break;
                case refii::vfs::GuiNativeArgument::Kind::kTable:
                    // The field COUNT as a text field, then name/kind/value per field — the
                    // same flat two-buffer shape the rest of the record uses.
                    ok = put(fmt::format("{}", argument.fields.size()));
                    for (const refii::vfs::GuiNativeArgument::Field& field : argument.fields) {
                        if (!ok) break;
                        ok = put(field.name) && put_kind(field.kind);
                        if (!ok) break;
                        switch (field.kind) {
                            case refii::vfs::GuiNativeArgument::Kind::kNumber:
                                ok = put(fmt::format("{}", field.number));
                                break;
                            case refii::vfs::GuiNativeArgument::Kind::kBoolean:
                                ok = put(field.boolean ? "1" : "0");
                                break;
                            case refii::vfs::GuiNativeArgument::Kind::kString:
                                ok = put(field.text);
                                break;
                            default:
                                ok = put(std::string());
                                break;
                        }
                    }
                    break;
                default:
                    ok = put(std::string());
                    break;
            }
        }

        if (!ok) {
            // A record is all or nothing: half a call would attach the wrong argument list to
            // a name, so the partial write is rolled back and the drain stops here.
            text_at = text_mark;
            kind_at = kind_mark;
            break;
        }

        ++written;
        ++handed;
    }

    // NOTHING IS DROPPED. The caller drains again while calls wait; a first record that fits no
    // buffer makes it grow its buffers, and only one that still does not fit at the ceiling is
    // dropped, by name.
    if (handed < calls.size() && written == 0u &&
        (utf8_capacity >= refii::vfs::kDrainTextCeiling || kinds_capacity >= refii::vfs::kDrainKindCeiling)) {
        REXLOG_WARN("Refii UI cut: GUI call '{}' ({} argument(s)) fits no drain buffer ({} text bytes, {} kind bytes) — dropped",
                    calls[handed].name, calls[handed].arguments.size(), utf8_capacity, kinds_capacity);
        ++handed;
    }
    refii::vfs::PutBackGuiNativeCalls(calls, handed);

    return written;
}

/// Counters for the UI cut's own report: how many native calls have been intercepted, how many
/// are waiting, and how many `GUI` names the registration hook resolved.
///
/// `named` is 0 until `RegisterGUI` has run and settles at the registered count, which
/// separates "nothing intercepted" from "the namespace was never seen".
REFII_HOST_ABI_EXPORT void RefiiHostGuiNativeCounters(uint64_t* out_intercepted,
                                                      uint32_t* out_pending,
                                                      uint32_t* out_named) {
    refii::vfs::GuiNativeCounters(out_intercepted, out_pending, out_named);
}

/// The sound of the movie the guest last opened, as the guest stated it: `out_sound` 0 for a
/// movie it plays silent; `out_ids` Bink track IDS, not file indices; `out_volumes` linear 0..1,
/// which can change while the movie plays. Returns the track count written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetMovieSound(uint32_t* out_sound, uint32_t* out_ids,
                                                      float* out_volumes, uint32_t capacity) {
    return refii::movie::Sound(out_sound, out_ids, out_volumes, capacity);
}

/// Whether the guest still wants the movie the host is showing: 0 none, 1 still wanted, 2 the
/// guest has skipped it. The host stops on 2 rather than playing to its own end, or it keeps a
/// movie over whatever the guest has moved on to.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostMovieState(void) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    return refii::movie::State(memory->virtual_membase());
}

/// Tells the guest the movie the host was showing has finished: the player's own done byte,
/// written as its decode would write it, so the guest advances when the movie visibly ends
/// rather than when its own invisible decode does. Returns the player retired, or 0.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostFinishMovie(void) {
    auto* memory = SafeKernelMemory();
    if (memory == nullptr) {
        return 0;
    }

    return refii::movie::Finish(memory->virtual_membase());
}

/// Where the guest wants the live movie drawn — the rect it states for itself, in its own 2D
/// screen space, so "fullscreen or embedded" needs no inference. Returns 0 when no movie is
/// live or none is stated yet.
REFII_HOST_ABI_EXPORT int RefiiHostGetMovieRect(float* out_x0, float* out_y0,
                                                float* out_x1, float* out_y1) {
    return refii::movie::Rect(out_x0, out_y0, out_x1, out_y1) != 0 ? 1 : 0;
}

extern "C" void RefiiVfsSetGuestRender(int32_t on);

/// Runs (1) or skips (0) the guest's own scene draw, or returns it to REFII_GUEST_RENDER (-1).
/// A reference instrument for the classic renderer: with the draw on, the title's constant
/// setters fill the device shadow. Additive and optional (the binding tolerates its absence),
/// so the ABI number is unchanged.
REFII_HOST_ABI_EXPORT void RefiiHostSetGuestRender(int32_t on) { RefiiVfsSetGuestRender(on); }

/// Installs Godot's asset reader, so the guest's loaders resolve through it and one mount
/// serves both sides.
///
/// `reader` is a C function pointer into managed code, called ON THE GUEST'S THREAD. It is
/// asked with a null buffer for the size, then again to fill one; it must be reentrant and must
/// not throw across the boundary. Passing nullptr unregisters, which Godot does on the way out
/// so a late guest read gets "no reader" rather than a torn-down runtime.
REFII_HOST_ABI_EXPORT void RefiiHostSetAssetReader(refii::vfs::HostAssetReaderFn reader) {
    refii::vfs::SetHostAssetReader(reader);
}

/// Installs Godot's text lookup for mods (Engine_ModText): Lua's Refii.text and a native mod's
/// refii_mod_text ask it, ON THE ASKING (guest) THREAD, through refii_bridge_mod_text. It must be
/// reentrant and must not throw across the boundary. nullptr unregisters. Additive and optional
/// (the binding tolerates its absence), so the ABI number is unchanged.
REFII_HOST_ABI_EXPORT void RefiiHostSetModTextLookup(refii::mods::HostModTextFn lookup) {
    refii::mods::SetHostModTextLookup(lookup);
}

/// Installs Godot's asset override query — answers whether an asset is a virtual mod override.
/// When true, native VFS prioritizes Godot's virtual asset over loose files on disk.
REFII_HOST_ABI_EXPORT void RefiiHostSetAssetOverrideQuery(
        refii::vfs::HostAssetIsOverrideFn query) {
    refii::vfs::SetHostAssetOverrideQuery(query);
}

/// Installs Godot's HASH index — same direction and threading as RefiiHostSetAssetReader, but
/// installed independently. `resolver` writes the asset's path and returns its length, 0 for
/// "no such hash".
REFII_HOST_ABI_EXPORT void RefiiHostSetAssetNameResolver(
        refii::vfs::HostAssetNameForHashFn resolver) {
    refii::vfs::SetHostAssetNameResolver(resolver);
}

/// A hard mod's published value by key ("combat.stamina"): 1 and the value when the mod's hook
/// has published one, 0 otherwise. The hook states it; nothing here samples the guest.
REFII_HOST_ABI_EXPORT int32_t RefiiHostGetModValue(const char* key, uint32_t key_length,
                                                    float* out_value) {
    if (key == nullptr) {
        return 0;
    }
    float value = 0.0f;
    if (!refii::mods::Published(std::string_view(key, key_length), value)) {
        return 0;
    }
    if (out_value != nullptr) {
        *out_value = value;
    }
    return 1;
}

/// Hands a hard mod a setting by key; its hook reads it with refii::mods::Setting.
REFII_HOST_ABI_EXPORT void RefiiHostSetModSetting(const char* key, uint32_t key_length,
                                                  float value) {
    if (key != nullptr) {
        refii::mods::SetSetting(std::string_view(key, key_length), value);
    }
}

/// Moves the mod events queued for the host into `out` as "<kind>\t<fields>\n" records (the
/// fields as the bus states them for Lua: numbers, true/false, 'strings'). Returns the bytes
/// written; an event that does not fit waits for the next call.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostDrainModEvents(char* out, uint32_t capacity) {
    return static_cast<uint32_t>(refii::mods::DrainEventsForHost(out, capacity));
}

/// Which event kinds the host wants posted: word 0 of the mask (bit = kind, kinds 0..63). The
/// mask is REFII_MOD_HOST_MASK_WORDS words (core/mod_values.h); the words past this one and the
/// next arrive through the command lane (`mask_word`). Every word 0 clears the queue.
REFII_HOST_ABI_EXPORT void RefiiHostSetModEventMask(uint64_t mask) {
    refii::mods::SetHostEventMaskWord(0, mask);
}

/// Word 1 of the same mask: kinds 64..127 (bit = kind − 64).
REFII_HOST_ABI_EXPORT void RefiiHostSetModEventMaskHigh(uint64_t mask) {
    refii::mods::SetHostEventMaskWord(1, mask);
}

/// A helper call or a query for the guest tick (the grammar is Fable2/src/vfs/src/hooks/mod_commands.cpp); a
/// query's reply comes back through RefiiHostDrainModEvents under kind 1000 with its id.
REFII_HOST_ABI_EXPORT void RefiiHostPostModCommand(const char* line, uint32_t line_length) {
    if (line != nullptr) {
        refii::mods::PostCommandFromHost(std::string_view(line, line_length));
    }
}

/// The mods' Lua errors since the last call, one per line, oldest first. Returns the bytes written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostDrainModErrors(char* out, uint32_t capacity) {
    return static_cast<uint32_t>(refii::mods::DrainErrorsForHost(out, capacity));
}

/// The host's verdict handler for Godot scripts (null to remove), called on the guest thread.
REFII_HOST_ABI_EXPORT void RefiiHostSetModVerdictHandler(refii_mod_verdict_fn handler) {
    refii_bridge_mod_set_verdict_handler(handler);
}

/// A read-only query verb and its words ("VERB ARGS...") answered at once into `out` as
/// "ok=true|false,<fields>" — only from inside the verdict handler, on the guest thread; the length,
/// REFII_MOD_READ_OUTSIDE anywhere else, REFII_MOD_READ_REFUSED for a verb that is not a read.
REFII_HOST_ABI_EXPORT int32_t RefiiHostModVerdictRead(const char* line, size_t line_length, char* out, size_t capacity) {
    return refii_bridge_mod_verdict_read(line, line_length, out, capacity);
}

/// Which event kinds the verdict handler is asked about: word 0 (bit = kind, kinds 0..63); the
/// mask has the event mask's shape, its later words arriving through the command lane.
REFII_HOST_ABI_EXPORT void RefiiHostSetModVerdictMask(uint64_t mask) {
    refii_bridge_mod_set_verdict_mask_word(0, mask);
}

/// Word 1 of the same mask: kinds 64..127 (bit = kind − 64).
REFII_HOST_ABI_EXPORT void RefiiHostSetModVerdictMaskHigh(uint64_t mask) {
    refii_bridge_mod_set_verdict_mask_word(1, mask);
}

/// The entity a mod handle stands for (what a Godot script holds), or 0.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostModHandleAddress(uint32_t handle) {
    return refii_bridge_mod_handle_address(handle);
}

/// A mod input action's edge for player 1 or 2; the guest raises InputAction on its next tick.
REFII_HOST_ABI_EXPORT void RefiiHostPostModInput(const char* action, uint32_t action_length,
                                                 int32_t player, int32_t pressed) {
    if (action != nullptr) {
        refii::mods::PostInputFromHost(std::string_view(action, action_length), player, pressed != 0);
    }
}

/// States which directory owns this install, before the guest is brought up.
///
/// Embedded, GetExecutableFolder() is the HOST's directory — Godot's — so the guest's
/// config, saves and cache would land beside whatever binary is hosting it.
///
/// Must be called BEFORE RefiiHostInitialize: paths are resolved once, during startup.
REFII_HOST_ABI_EXPORT void RefiiHostSetAppRoot(const char* root) {
    RefiiSetAppRoot(root != nullptr && root[0] != '\0' ? std::filesystem::path{root}
                                                       : std::filesystem::path{});
}

/// States the runtime config file the guest loads its cvars from: the running title's own, never
/// another title's. The host knows which title it started; this core and the runtime serve every
/// title and name none.
///
/// Selects the user's own game module (Android's private copy) before guest initialization.
/// Returns zero with a diagnostic for an invalid path or a change after a load attempt.
REFII_HOST_ABI_EXPORT int RefiiHostSetGameModulePath(const char* path, char* out, std::size_t capacity) {
    std::string error;
    const bool success = refii::payload::SetModulePath(path != nullptr ? path : "", error);
    WriteOut(success ? "game module path selected" : error, out, capacity);
    return success ? 1 : 0;
}

/// Must be called BEFORE RefiiHostInitialize, with RefiiHostSetAppRoot: paths are resolved once.
REFII_HOST_ABI_EXPORT void RefiiHostSetConfigPath(const char* path) {
    RefiiSetConfigPath(path != nullptr && path[0] != '\0' ? std::filesystem::path{path}
                                                         : std::filesystem::path{});
}

/// Must be called BEFORE RefiiHostInitialize: the running title's log folder, which the guest's
/// logs, its crash record and the hang watchdog write into.
REFII_HOST_ABI_EXPORT void RefiiHostSetLogDirectory(const char* path) {
    RefiiSetLogDirectory(path != nullptr && path[0] != '\0' ? std::filesystem::path{path}
                                                            : std::filesystem::path{});
}

/// One error the host engine reported, kept for the crash record (the last 32 distinct lines,
/// repeats counted). Any thread, before or after RefiiHostInitialize.
REFII_HOST_ABI_EXPORT void RefiiHostNoteEngineError(const char* text) {
    RefiiNoteEngineError(text);
}

/// What the host engine last said of itself (adapter, graphics memory in use, window), one line,
/// the newest replacing the last; the crash record carries it.
REFII_HOST_ABI_EXPORT void RefiiHostNoteEngineState(const char* text) {
    RefiiNoteEngineState(text);
}

// ---------------------------------------------------------------------------------
// Audio
// ---------------------------------------------------------------------------------

/// The frame geometry, so the host does not hard-code what the binary states. The frames
/// themselves are taken by the Godot audio effect in godot_audio_effect.cpp — in-process, on
/// Godot's audio thread — which also carries the output's configure, detach and stats exports.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostAudioSampleRate(void) { return refii::vfs::kGuestAudioSampleRate; }
REFII_HOST_ABI_EXPORT uint32_t RefiiHostAudioSamplesPerFrame(void) {
    return refii::vfs::kGuestAudioSamplesPerFrame;
}

/// Health. `pushed` settles at 187.5/s (48000/256) once the guest is producing; `dropped`
/// climbing means nothing is draining.
REFII_HOST_ABI_EXPORT void RefiiHostAudioStats(uint32_t* out_queued_frames, uint64_t* out_pushed,
                         uint64_t* out_popped, uint64_t* out_dropped,
                         int* out_driver_installed) {
    if (out_queued_frames != nullptr) {
        *out_queued_frames = refii::vfs::HostAudioQueuedFrames();
    }
    uint64_t pushed = 0;
    uint64_t popped = 0;
    uint64_t dropped = 0;
    refii::vfs::HostAudioCounters(&pushed, &popped, &dropped);
    if (out_pushed != nullptr) {
        *out_pushed = pushed;
    }
    if (out_popped != nullptr) {
        *out_popped = popped;
    }
    if (out_dropped != nullptr) {
        *out_dropped = dropped;
    }
    if (out_driver_installed != nullptr) {
        *out_driver_installed = refii::vfs::HostAudioDriverInstalled() ? 1 : 0;
    }
}

/// One live voice, flattened for P/Invoke. Mirrors refii::vfs::VoiceRecord field for
/// field; keep the two in step.
struct RefiiHostVoice {
    uint32_t handle;
    uint32_t generation;
    uint32_t sample_rate;
    uint32_t channels;
    uint32_t started;
    uint32_t retired;
    float volume;
    float pitch_ratio;
    uint32_t queued_bytes;
    uint32_t submitted_seq;
    float pan_left;
    float pan_right;
};

/// Snapshots the live voices. Pass a null buffer to count. Returns how many exist, which
/// may exceed `capacity` — the host should grow and ask again rather than lose voices.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetVoices(RefiiHostVoice* out, uint32_t capacity) {
    static_assert(sizeof(RefiiHostVoice) == sizeof(refii::vfs::VoiceRecord),
                  "RefiiHostVoice must mirror VoiceRecord exactly; a mismatch marshals "
                  "garbage into C# rather than failing to build");

    if (out == nullptr) {
        return refii::vfs::TakeVoiceRecords(nullptr, 0);
    }
    return refii::vfs::TakeVoiceRecords(reinterpret_cast<refii::vfs::VoiceRecord*>(out),
                                        capacity);
}

/// Drains one voice's PCM as interleaved 16-bit samples. `generation` guards against the
/// guest recycling a handle: a mismatch returns 0 rather than the wrong sound's audio.
/// Returns bytes written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostTakeVoicePcm(uint32_t handle, uint32_t generation, void* out,
                               uint32_t capacity) {
    return refii::vfs::TakeVoicePcm(handle, generation, out, capacity);
}

/// True once the voice hooks have seen any voice at all — distinguishes "Stage 2 is built
/// but the guest has not reached audio" from "Stage 2 is not running".
REFII_HOST_ABI_EXPORT int RefiiHostVoiceCaptureActive(void) { return refii::vfs::VoiceCaptureActive() ? 1 : 0; }

/// Hands per-voice playback to the host. While set, the guest's MIXED frame is discarded
/// instead of queued, since Godot is playing the individual voices. The mixer keeps running
/// and keeps being credited: it is the audio clock.
REFII_HOST_ABI_EXPORT void RefiiHostSetVoicePlaybackOwned(int owned) {
    refii::vfs::SetVoiceCaptureOwnsOutput(owned != 0);
}

/// The host's arguments as a clamped ControllerState.
static refii::host::ControllerState HostControllerState(int connected, uint32_t buttons,
                                 uint32_t left_trigger, uint32_t right_trigger,
                                 int32_t thumb_lx, int32_t thumb_ly, int32_t thumb_rx,
                                 int32_t thumb_ry) {
    const auto clamp_axis = [](int32_t value) {
        if (value < -32768) return static_cast<int16_t>(-32768);
        if (value > 32767) return static_cast<int16_t>(32767);
        return static_cast<int16_t>(value);
    };

    refii::host::ControllerState state;
    state.connected = connected != 0;
    state.buttons = static_cast<uint16_t>(buttons & 0xFFFFu);
    state.left_trigger = static_cast<uint8_t>(left_trigger > 255 ? 255 : left_trigger);
    state.right_trigger = static_cast<uint8_t>(right_trigger > 255 ? 255 : right_trigger);
    state.thumb_lx = clamp_axis(thumb_lx);
    state.thumb_ly = clamp_axis(thumb_ly);
    state.thumb_rx = clamp_axis(thumb_rx);
    state.thumb_ry = clamp_axis(thumb_ry);
    return state;
}

/// Publishes one controller's state, in XInput terms, for the guest to read.
///
/// Godot owns input the same way it owns the window: it reads the real device and pushes
/// a snapshot here, and the guest's XInput calls are served from it. Translating to Xbox
/// vocabulary happens once, on the way in.
///
/// `buttons` is a mask of X_INPUT_GAMEPAD_* bits; triggers are 0..255; thumbsticks are
/// -32768..32767 with +Y up, which is the Xbox convention and NOT Godot's screen-space
/// one — the caller flips it.
///
/// Safe to call every frame from the host's main thread while the guest reads on its
/// own. The desktop keeps its seqlock; Android takes a small native snapshot lock so the
/// state and its optional consumption tag have one coherent lifetime.
REFII_HOST_ABI_EXPORT void RefiiHostSetControllerState(uint32_t user_index, int connected, uint32_t buttons,
                                 uint32_t left_trigger, uint32_t right_trigger,
                                 int32_t thumb_lx, int32_t thumb_ly, int32_t thumb_rx,
                                 int32_t thumb_ry) {
    refii::host::PublishControllerState(user_index, HostControllerState(connected, buttons,
        left_trigger, right_trigger, thumb_lx, thumb_ly, thumb_rx, thumb_ry));
    // A user signed in to the shared profile is signed out once its pad has gone (shadow xam_user.cpp).
    RefiiXamNotePad(user_index, connected);
}

/// Android's physical-phase publication. The tag is independent of analog packet updates; it is
/// acknowledged only after the input system copies a non-null state.
REFII_HOST_ABI_EXPORT void RefiiHostSetControllerStateTracked(uint32_t user_index, int connected,
                                 uint32_t buttons, uint32_t left_trigger, uint32_t right_trigger,
                                 int32_t thumb_lx, int32_t thumb_ly, int32_t thumb_rx,
                                 int32_t thumb_ry, uint64_t receipt_token) {
    refii::host::PublishControllerStateTracked(user_index, HostControllerState(connected, buttons,
        left_trigger, right_trigger, thumb_lx, thumb_ly, thumb_rx, thumb_ry), receipt_token);
    RefiiXamNotePad(user_index, connected);
}

/// The last successful non-null state-copy receipt (capabilities, vibration, NULL queries and
/// service-step counts excluded).
REFII_HOST_ABI_EXPORT uint64_t RefiiHostGetControllerReadReceipt(uint32_t user_index) {
    return refii::host::ControllerReadReceipt(user_index);
}

/// The guest's SIMULATION TICK, as the guest itself counts it.
///
/// `dword_83319650` is incremented by the world update, and it is the only thing in the
/// process that states when the simulation has actually advanced. Everything else a host
/// might time against — the audio clock, Godot's frame delta, wall time — measures OUR
/// clocks, and the whole question this answers is how the guest's clock relates to ours.
///
/// Two uses, and they are why it is exported rather than inferred:
///
/// 1. RATE. Sampled against wall time it gives the simulation rate directly. The sim is
///    paced by the audio device (the guest blocks in `PushHostAudioFrame`), and Godot
///    presents on vsync; those are two independent clocks, so a state that advances N times
///    a second sampled M times a second shows every character stepping unevenly however
///    correct each individual sample is. Knowing N is the difference between fixing that and
///    guessing at it.
///
/// 2. COHERENCE. Read before and after a pose copy it brackets the read. The rig arrays are
///    memcpy'd out of guest memory from Godot's thread while the simulation thread writes
///    them, with no lock on either side — so a straddled read is possible by construction,
///    and this is the cheapest thing that can DETECT one. Reading twice and comparing would
///    cost a second copy of the payload; a rig is up to 160 bones of 48 bytes and does not
///    deserve that.
///
/// Zero means the guest is absent. Callers must compare for INEQUALITY and must not assume
/// the value only ever grows, or grows by one: whether a level load resets it is not settled,
/// and a rate derived from a backwards step would be nonsense reported as fact.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetGuestFrame(void) {
    return refii_bridge_guest_frame();
}

/// The guest's time base, owned by the host: set the mode (negative leaves it), read the
/// counters, return the current mode.
///
/// Mode 0 is the real clock: the guest's loop runs its own wall-clock accumulator and, when it
/// falls behind, advances one step and re-bases, discarding the backlog (0x82276FA4). Mode 1
/// paces it — at most one high-frequency step between reads on a thread. Mode 2 drives it: time
/// moves only when the host grants steps, so the guest's tick rate is Godot's and its stepping
/// is deterministic (required for lockstep multiplayer).
///
/// `out_watchdog` counts lapses where no grant arrived and time resumed on its own so guest
/// threads on timed waits did not freeze. See rex_payload_bridge.h for the high-half fields.
REFII_HOST_ABI_EXPORT int RefiiHostSetTimeBaseMode(int mode, uint64_t* out_capped,
                                                   uint64_t* out_granted,
                                                   uint64_t* out_watchdog) {
    if (mode >= 0 || mode == -2 || mode == -3) {
        refii_bridge_rex_set_time_base_mode(mode);
    }

    refii_bridge_rex_time_base_stats(out_capped, out_granted, out_watchdog);
    return refii_bridge_rex_time_base_mode();
}

/// Grants the guest `steps` high-frequency steps of time. Driven mode only; ignored otherwise.
REFII_HOST_ABI_EXPORT void RefiiHostAdvanceTimeBase(uint32_t steps) {
    refii_bridge_rex_advance_time_base(steps);
}

/// Freezes or unfreezes the driven time base so intentional host holds (pause, load cover) do not
/// trigger the grant-silence watchdog.
REFII_HOST_ABI_EXPORT void RefiiHostSetTimeBaseFrozen(uint32_t frozen) {
    refii_bridge_rex_set_time_base_frozen(frozen != 0u ? 1 : 0);
}

//----------------------------------------------------------------------------------
// Achievements
//
// Godot owns achievements (Engine_Achievements): the definitions parsed from the title's XDBF,
// the unlock store and its file, the toast and the page. `refii_achievement` is the guest-facing
// seam only, and these exports are its two directions: the guest's writes and its UI
// requests come up; the XDBF comes up once; the unlocked set the host states goes down for the
// guest's own enumerator to read back.
//----------------------------------------------------------------------------------

/// Writes NEWER than `after_sequence`, oldest first. Returns how many were written.
///
/// A sequence, not a flushing queue: the seam keeps every write of the session stamped with a
/// monotonic sequence, so the read is idempotent and two readers cannot lose one between them.
/// `out_bindings` takes the serial of the writing user's binding (RefiiHostGetAchievementBindings),
/// 0 when the user was never bound.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetAchievementUnlocks(uint64_t after_sequence,
                                                             uint32_t* out_ids,
                                                             uint32_t* out_users,
                                                             uint64_t* out_sequences,
                                                             uint64_t* out_bindings,
                                                             uint32_t max_unlocks) {
    const std::vector<refii::achievement::AchievementUnlock> writes =
        refii::achievement::GetAchievementWritesSnapshot();

    uint32_t written = 0;
    for (const auto& write : writes) {
        if (write.sequence <= after_sequence) continue;
        if (written >= max_unlocks) break;

        if (out_ids != nullptr) out_ids[written] = write.achievement_id;
        if (out_users != nullptr) out_users[written] = write.user_index;
        if (out_sequences != nullptr) out_sequences[written] = write.sequence;
        if (out_bindings != nullptr) out_bindings[written] = write.binding;
        ++written;
    }

    return written;
}

/// The title's XDBF resource. Returns the byte count — call with a null buffer to size it
/// first. Zero until the executable module has been launched.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetTitleXdbf(uint8_t* out_bytes, uint32_t capacity) {
    const std::vector<uint8_t> xdbf = refii::achievement::GetTitleXdbfSnapshot();
    if (out_bytes != nullptr && capacity != 0) {
        const size_t take = xdbf.size() < capacity ? xdbf.size() : capacity;
        std::memcpy(out_bytes, xdbf.data(), take);
    }

    return static_cast<uint32_t>(xdbf.size());
}

/// The guest's adoption points' bindings with a serial above `after_serial`, oldest first, one per
/// line: `serial<TAB>user<TAB>save<LF>` (save empty: a hero no save holds yet). Returns the bytes the
/// whole answer needs; writes it only when `capacity` holds it all (plus the null).
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetAchievementBindings(uint64_t after_serial, char* out, uint32_t capacity) {
    std::string text;
    for (const auto& binding : refii::achievement::GetBindingsSnapshot()) {
        if (binding.serial <= after_serial) continue;
        text += std::to_string(binding.serial) + '\t' + std::to_string(binding.user_index) + '\t' + binding.save + '\n';
    }
    if (out != nullptr && capacity > text.size()) {
        std::memcpy(out, text.data(), text.size());
        out[text.size()] = 0;
    }
    return static_cast<uint32_t>(text.size());
}

/// Every save's unlocked set, as the host's per-character stores have it, for the guest's per-user
/// `XamUserCreateAchievementEnumerator`: one save per line, `save<TAB>id:filetime,id:filetime,...<LF>`
/// (a filetime of 0: unlocked, time unknown). Replaces the previous statement.
REFII_HOST_ABI_EXPORT void RefiiHostSetSaveAchievementUnlocks(const char* table) {
    std::vector<refii::achievement::SaveUnlocks> saves;
    const std::string_view text = table != nullptr ? std::string_view(table) : std::string_view();
    for (size_t at = 0; at < text.size();) {
        size_t end = text.find('\n', at);
        if (end == std::string_view::npos) end = text.size();
        const std::string_view line = text.substr(at, end - at);
        at = end + 1;
        const size_t tab = line.find('\t');
        if (tab == std::string_view::npos || tab == 0) continue;
        refii::achievement::SaveUnlocks save;
        save.save = std::string(line.substr(0, tab));
        const std::string list(line.substr(tab + 1));
        for (size_t p = 0; p < list.size();) {
            size_t comma = list.find(',', p);
            if (comma == std::string::npos) comma = list.size();
            const std::string item = list.substr(p, comma - p);
            p = comma + 1;
            const size_t colon = item.find(':');
            const uint32_t id = static_cast<uint32_t>(std::strtoul(item.substr(0, colon).c_str(), nullptr, 10));
            if (id == 0) continue;
            save.ids.push_back(id);
            save.filetimes.push_back(colon == std::string::npos ? 0ull : std::strtoull(item.substr(colon + 1).c_str(), nullptr, 10));
        }
        saves.push_back(std::move(save));
    }
    refii::achievement::SetSaveUnlocks(std::move(saves));
}

/// How many times the guest has called `XamShowAchievementsUI` this session; the host opens
/// its page on every increment.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetAchievementsUiRequests(void) {
    return refii::achievement::GetAchievementsUiRequestCount();
}

/// The rumble the guest last asked for, 0..65535 per motor. Returns 1 on success.
REFII_HOST_ABI_EXPORT int RefiiHostGetVibration(uint32_t user_index, uint32_t* out_left, uint32_t* out_right) {
    uint16_t left = 0;
    uint16_t right = 0;
    if (!refii::host::TakeVibration(user_index, &left, &right)) {
        return 0;
    }

    if (out_left != nullptr) {
        *out_left = left;
    }
    if (out_right != nullptr) {
        *out_right = right;
    }
    return 1;
}
}  // extern "C"
