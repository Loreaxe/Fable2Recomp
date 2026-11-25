// The ported screens' levers (gui_screen_stub_*.cpp), dispatched by the guest tick pump.
#ifndef REFII_VFS_HOOKS_GUI_SCREEN_STUBS_H
#define REFII_VFS_HOOKS_GUI_SCREEN_STUBS_H

#include <cstdint>
#include <string>

#include <rex/ppc/context.h>

namespace refii::vfs {

/// The logbook's opener on another player ran (`sub_823C48D8`); `over_world_map` when the world map
/// was the screen up, which is where its B returns.
void StateLogbookOpenedOnPlayer(bool over_world_map);

/// `PauseBack` — a ported book's B at its root (TU1 screen id `screen`: 14..18).
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

/// `CreditsEnd` — Godot's credits roll ended: the quiet exit.
bool DispatchCreditsEnd(PPCContext& ctx, uint8_t* base);

}  // namespace refii::vfs

#endif
