// The ported screens' levers (gui_screen_stub_*.cpp), dispatched by the guest tick pump.
#ifndef REFII_VFS_HOOKS_GUI_SCREEN_STUBS_H
#define REFII_VFS_HOOKS_GUI_SCREEN_STUBS_H

#include <cstdint>
#include <string>

#include <rex/ppc/context.h>

namespace refii::vfs {

/// `PauseBack` — a ported book's B at its root (TU1 screen id `screen`: 14..18, 23).
bool DispatchPauseBack(PPCContext& ctx, uint8_t* base, uint32_t screen);

/// `PauseCycle:<p>:prev|next` — a ported book's LB / RB, behind that screen's own gate.
bool DispatchPauseCycle(PPCContext& ctx, uint8_t* base, uint32_t screen, uint32_t player, bool next);

/// `Options:<p>:<setting>:<value>` — one options setting through the guest's own setter: gamma
/// (profile + live brightness), gammalive (live brightness only), sfx / music / speech / speaker
/// (applied live), subtitles, invert, and player one's breadcrumbs / tutorials / orbs / autojoin.
bool DispatchOptions(PPCContext& ctx, uint8_t* base, uint32_t player, const std::string& setting, double value);

/// `Options:<p>:audio:<sfx>:<music>:<speech>:<speaker>` — the Audio page's commit: applied live
/// and written to the profile.
bool DispatchOptionsAudioCommit(PPCContext& ctx, uint8_t* base, const float levels[3], uint32_t speaker);

/// `Start:<p>` — the player's Start through the guest's own handler `sub_82371B10`: opens the book from
/// play, closes it from any pause screen, the lite pause box in a cutscene or conversation.
bool DispatchStart(PPCContext& ctx, uint8_t* base, uint32_t player);

/// `PauseClose:<p>` — the pause root's B at the top of its list: the book closes.
bool DispatchPauseClose(PPCContext& ctx, uint8_t* base, uint32_t player);

/// `PauseSaving:<p>:<row>` — a Saving row (0 save and continue, 1 save and quit, 2 quit without
/// saving) as the guest's handler answers it: its confirm box, or the quit when nothing is held.
bool DispatchPauseSaving(PPCContext& ctx, uint8_t* base, uint32_t player, uint32_t row);

/// `PauseHenchmanQuit:<p>` — player two's Quit row as the guest's henchman list answers it
/// (`sub_826A6610`): its own box 27 (a save held) or 26 (progress to the hero); player two's root only.
bool DispatchPauseHenchmanQuit(PPCContext& ctx, uint8_t* base, uint32_t player);

/// The pause map image's texture (gui_pause_hooks.cpp), stated for the headless element.
void StatePauseMapTexture(uint8_t* base, uint32_t element, uint32_t hash);

/// `Ability:<p>:learn|unlearn:<1..14>` — one level of an ability bought or given back for player `p`
/// through the guest's own transaction, which opens its own result box; the discard behind the guest's
/// gate (discarding enabled, a level to give back).
bool DispatchAbility(PPCContext& ctx, uint8_t* base, uint32_t player, bool unlearn, uint32_t index);

/// The items (15) and clothing (16) screens' OnEnter and Update, run by their co-op wrappers
/// (coop_hero_hooks.cpp) with r3 the handler.
void ItemsScreenOnEnter(PPCContext& ctx, uint8_t* base);
void ItemsScreenUpdate(PPCContext& ctx, uint8_t* base);
void ClothingScreenOnEnter(PPCContext& ctx, uint8_t* base);
void ClothingScreenUpdate(PPCContext& ctx, uint8_t* base);

/// `Clothing:<p>:wear|outfit:<ticket>:<record>` and `Clothing:<p>:remove:<event type 91..97>` — the
/// clothing screen's consequences on player `p`'s hero, worn-ness decided on that hero. `ticket` is the
/// row's population ticket (0: the record's first row), `value` the record or the event type. False
/// when the lever is not this file's to take (no clothing handler, player two's outfit): the pump then
/// runs the player's direct inventory call.
bool DispatchClothing(PPCContext& ctx, uint8_t* base, uint32_t player, const std::string& verb, uint32_t ticket,
                      uint32_t value);

/// `Card:<p>:dismiss|equip` — the received-item card's Done (after its one second) and Equip now,
/// through the guest's own dismiss `sub_82538BF0`.
bool DispatchCard(PPCContext& ctx, uint8_t* base, uint32_t player, const std::string& verb);

/// `Shop:<p>:close` — the shop's B, the guest's own helper `sub_826D5A60`: at the list's root
/// `ExitScreen(QM, 1)`, whose OnExit applies the visit.
bool DispatchShopClose(PPCContext& ctx, uint8_t* base, uint32_t player);

/// `Gift:<p>:close` — the gift screen's B, the guest's own helper `sub_826C4C50` (player one's alone).
bool DispatchGiftClose(PPCContext& ctx, uint8_t* base, uint32_t player);

/// `Furniture:<p>:close` — upgrade furniture's B (player one's alone): `ExitScreen(QM, 1)` and the
/// preview's Hide.
bool DispatchFurnitureClose(PPCContext& ctx, uint8_t* base, uint32_t player);

/// `Decoration:<p>:mount:<ticket>|takedown|close` — the decoration screen's A on the row the ticket
/// names (the guest's own mount `sub_826B7550`), its Y (`sub_8243ED88`) and its B, each ending in
/// `ExitScreen(QM, 1)` and the preview's Hide as the handler's arms do; player one's alone.
bool DispatchDecoration(PPCContext& ctx, uint8_t* base, uint32_t player, const std::string& verb, uint32_t ticket);

/// The real-estate screen's (28) Update, run by its household wrapper (mod_events.cpp) with r3 the
/// handler.
void RealEstateScreenUpdate(PPCContext& ctx, uint8_t* base);

/// The page's numbers (`RealEstate`) for the household the real-estate handler `screen` is bound to:
/// the buyer at +0x90 and the building at +0xA4, as `sub_826B1A08` (disc `sub_82596CB8`) stored them
/// at OnEnter. Published empty when the binding holds no building. Defined in guest_tick_pump.cpp.
void PublishBoundRealEstate(PPCContext& ctx, uint8_t* base, uint32_t screen);

/// `RealEstate:<p>:event:<110..120>` — a real-estate row's commit by the event its spawn stated: the
/// handler's own OnMenuEvent with that type (its confirm, numeric box and consequence are the guest's).
bool DispatchRealEstateEvent(PPCContext& ctx, uint8_t* base, uint32_t player, uint32_t type);

/// `RealEstate:<p>:close` — the real-estate screen's B: `ExitScreen(QM, 1)`.
bool DispatchRealEstateClose(PPCContext& ctx, uint8_t* base, uint32_t player);

/// The crime screen's opener is about to run (crime_screen_stated.cpp): the arrested player the
/// arrest stated just before it becomes the open screen's.
void CrimeScreenOpening();

/// `Crime:<p>:pay|resist|service` — a crime row's A: the handler's own OnMenuEvent with that row's
/// event (50 / 51 / 52); its gates, its message and its exit are the guest's.
bool DispatchCrime(PPCContext& ctx, uint8_t* base, uint32_t player, const std::string& choice);

/// `Query:<p>:close|rename` — the villager card's B (its close helper and `ExitScreen(QM, 1)`) and its
/// Y (the guest's keyboard request); neither while the keyboard is pending.
bool DispatchQuery(PPCContext& ctx, uint8_t* base, uint32_t player, const std::string& verb);

/// `SafetyToggle:<p>` — the expression wheel's safety branch on hero one, behind the guest's own gate
/// (hud_levers.cpp); player two is refused, as their wheel only closes.
bool DispatchSafetyToggle(PPCContext& ctx, uint8_t* base, uint32_t player);

/// `TargetQuery:<p>` — the HUD's raw-Y arm `sub_823FDA78` on an event carrying player one's pad and Y:
/// the target, the gates and the card's opener are the guest's (hud_levers.cpp).
bool DispatchTargetQuery(PPCContext& ctx, uint8_t* base, uint32_t player);

/// `CreditsEnd` — Godot's credits roll ended: the quiet exit.
bool DispatchCreditsEnd(PPCContext& ctx, uint8_t* base);

}  // namespace refii::vfs

#endif
