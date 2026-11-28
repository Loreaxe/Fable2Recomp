// Co-op: player two is a hero, not a henchman (Fable 3-style co-op, phase 1;
// docs/coop_fable3_plan.md).
//
// Fable 2 makes player two a stand-in bolted onto player one, through single comparisons against
// the henchman inside otherwise player-neutral routines. Each hook below skips exactly one such
// comparison with a mid-function hook ([[midasm_hook]] in versions/*/refii_config.toml: the hook
// runs before the named instruction and then jumps), so the rest of the game's logic stands.
// Fable 3, the same engine's successor, removed the same tests (the disc IDB's counterparts are
// named with each hook). Addresses are TU1 / disc; the two builds' functions are identical in
// size and code, and every hook sits at the same offset in both.
//
//   USE: an object's script runs for its real user. The OnActionUse use routine (TU1 sub_828219F8
//     / disc sub_82753478), after the henchman getter, does `cmplw cr6,r27,r3; bne` (0x82821B88 /
//     0x82753608): a user other than player two runs the object's Lua `Create(thing, user)` at
//     0x82821CF4 / 0x82753774; player two is refused for a sale sign, an inventory holder, a bed,
//     component 80, a dive spot or a knock-door, and otherwise the user is REPLACED by the hero
//     and the routine loops — every chest, lever and door player two used ran as player one.
//     Fable 3's use (sub_829B33B0 → sub_829B3AB8) passes the real user. The scripts that took
//     "the hero" from GetPlayerHero() are answered by the core Lua (Godot/src/host/CoopHeroLua.cs).
//
//   SHOPS: a shopkeeper serves player two where player two is this machine's local player.
//     Villager use (sub_827E1178 / sub_827A8120) says TEXT_AI_HENCHMAN_REJECT to player two at a
//     shopkeeper (`clrlwi r10,r27,24; cmplwi; beq 0x827E1324` at 0x827E12F0 / 0x827A8298; r27 =
//     user is player two). The shop then opens for the LOCAL player: the screen's buyer is
//     `sub_8229EE78(sub_821C7CE0(gui))` (sub_826AF430), and sub_821C7CE0 is player one on the
//     session host — solo and local co-op read as host — else player two. So the refusal is
//     lifted exactly where player two IS the local player (an online joiner); on a host it would
//     have player one pay. TRAVEL AGENTS: the same routine's job test (`lwz r11,0x9C(r25)`, 14 the
//     coach driver, 29 the ship captain; 0x827E152C..0x827E153C / disc 0x827A84D4..0x827A84E4) then
//     says the reject line to player two (`clrlwi r11,r27,24` 0x827E1540 → `sub_825D7060`) or runs the
//     agent's script for the user (`sub_827E1668(r25, r24, OnActionUseTravelAgent)` 0x827E1574..
//     0x827E1584 / disc 0x827A851C..0x827A852C). That reject is the 360's rule for the henchman and it
//     stays theirs; a player two who brought a hero is served (RefiiCoopTravelAgentForHero; Fable 3
//     refuses every partner, `sub_827E1C40`). The gamesmaster keeps its refusal: pub games refuse in
//     a multiplayer game anyway (onactionusegamesmaster.lua's `not IsMultiplayerGame()`).
//
//   PROGRESSION: each hero keeps what they earn. Experience (rex_CECExperience_Modify), gold
//     (rex_CECMoney_Modify) and renown (rex_lhHeroStats_ModifyRenown) pool player two's gains
//     into player one and split player one's by the co-op screen's sliders (coopMgr +172/+176/
//     +180). Each routine reaches its own "credit this component" block when there is nothing to
//     split; the hook jumps straight there:
//       experience: before `clrlwi r11,r28,24` (allowSplit) at 0x8256DCBC / 0x8251C2EC, after the
//         amount <= 0 test, to 0x8256DE68 / 0x8251C498 (f30 is already loaded for its clamp);
//       gold: before `cmpwi cr6,r28,0` at 0x8242E73C / 0x8239E85C, to 0x8242E93C / 0x8239EA5C;
//       renown: right after `mr r28,r23` (the amount) at 0x824D6450 / 0x8249D5C0, to 0x824D6630 /
//         0x8249D7A0.
//     The network gates in front of each (only the host credits) are untouched. Fable 3 keeps a
//     purse per hero too (sub_823B0590: a split only between spouses or partners).
//
//   ONLY FOR A PLAYER TWO WHO BROUGHT A HERO (a character from a save; RefiiCoopGetOwnHero, the one
//     statement of it, guest_tick_pump.cpp). The plain henchman is the shipped one: every gate below
//     answers the shipped instruction for them, so their use runs as hero one, shops and reactions
//     refuse them, their crimes and deeds are hero one's and their earnings pool into hero one and
//     split by the co-op screen's sliders, exactly as on the 360.
//
//   REACTIONS: villagers react to player two. The opinion-reaction filter (sub_8278CAE0 /
//     sub_8275F0F0) refuses player two unless the reaction's data sets ForHenchman (`extrwi
//     r20,r10,1,27`): `cmplw cr6,r22,r3; beq refuse` at 0x8278D998 / 0x8275FFA8 is skipped to
//     0x8278D9A0 / 0x8275FFB0, the path a ForHenchman reaction takes.

#include <array>
#include <atomic>
#include <cstdint>
#include <algorithm>
#include <cstring>
#include <mutex>
#include <string>
#include <unordered_map>

#include <rex/logging.h>
#include <rex/ppc/context.h>
#include <rex/ppc/func.h>

#include "core/guest_build.h"
#include "core/guest_state.h"
#include "core/payload_bridge_fable2.h"
#include "core/refii_mod.h"
#include "hooks/gui_screen_stubs.h"


extern "C" REX_FUNC(__imp__rex_CECVillager_OnUse);

namespace {

/// One line the first time each hook runs, so a run says which seams were reached.
void Once(std::atomic<bool>& said, const char* what) {
  if (!said.exchange(true, std::memory_order_relaxed)) {
    REXLOG_INFO("Refii co-op heroes: {}", what);
  }
}

std::atomic<bool> g_said_use{false};
std::atomic<bool> g_said_travel{false};
std::atomic<bool> g_said_money_item{false};
std::atomic<bool> g_said_shop{false};
std::atomic<bool> g_said_experience{false};
std::atomic<bool> g_said_gold{false};
std::atomic<bool> g_said_renown{false};
std::atomic<bool> g_said_reaction{false};

/// Set by the villager-use wrapper for the length of its call: the user is player two AND player
/// two is this machine's local player. The midasm hook inside the same call reads it. Villager
/// use runs on the game thread only.
bool g_shop_for_local_player_two = false;

/// The same call's user is player two in LOCAL co-op (both heroes on this machine).
bool g_shop_user_is_local_two = false;

/// Local player two's shop is up: set when the shopkeeper path opens it for them, dropped on the
/// first read after the shop screen is gone.
std::atomic<bool> g_shopping_player_two{false};

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

}  // namespace

/// Player two brought a hero (a character from a save) rather than standing as the plain henchman.
extern "C" int RefiiCoopGetOwnHero(void);
extern "C" uint8_t* RefiiGuestBase(void);

namespace {

/// Player two is here as the plain henchman: their gains, uses and reactions are the shipped ones.
bool HenchmanHere() {
  uint8_t* base = RefiiGuestBase();
  return base != nullptr && RefiiCoopGetOwnHero() == 0 && refii_mod_player_hero(base, 2) != 0u;
}

/// The guest's IsLocalCoopGame: game+0x11C == 2.
bool LocalCoopGame(uint8_t* base) {
  const uint32_t root = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = Plausible(root) ? refii::vfs::LoadGuestU32(base, root + 0xCu) : 0u;
  return Plausible(game) && refii::vfs::LoadGuestU32(base, game + 0x11Cu) == 2u;
}

/// sub_821C7CE0's rule: the local player is player one when this machine is the session host
/// (net +0x34 and +0x35 — every solo and local co-op game reads as host), else player two.
bool LocalPlayerIsPlayerTwo(uint8_t* base) {
  const uint32_t root = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = Plausible(root) ? refii::vfs::LoadGuestU32(base, root + 0xCu) : 0u;
  const uint32_t net = Plausible(game) ? refii::vfs::LoadGuestU32(base, game + 0x8Cu) : 0u;
  if (!Plausible(net)) {
    return false;
  }
  const bool host = refii::vfs::LoadGuestU8(base, net + 0x34u) != 0u && refii::vfs::LoadGuestU8(base, net + 0x35u) != 0u;
  return !host;
}

}  // namespace

// The real user's script path. Every user reaches the hook (r27 the user, r3 the henchman from
// `lhEntity_GetRenderObject`, TU1 0x82821B84 / disc 0x82753604); for any other user the shipped `bne`
// (0x82821B8C / 0x8275360C) takes the same jump, so only player two's use is player two's. True takes
// it; false runs the shipped `cmplw cr6, r27, r3` (0x82821B88 / 0x82753608): the henchman's use is
// refused or replaced by hero one's, as shipped.
bool RefiiCoopUseByRealUser(PPCRegister& r27, PPCRegister& r3) {
  if (r27.u32 != r3.u32) {
    return true;
  }
  if (RefiiCoopGetOwnHero() == 0) {
    return false;
  }
  Once(g_said_use, "an object used by player two runs for player two");
  return true;
}

// True: run the travel agent's script for player two (r24 the user), a hero; false: the shipped test
// (the henchman's reject line).
bool RefiiCoopTravelAgentForHero(PPCRegister& r24) {
  uint8_t* base = RefiiGuestBase();
  if (base == nullptr || RefiiCoopGetOwnHero() == 0 || r24.u32 == 0u || r24.u32 != refii_mod_player_hero(base, 2)) {
    return false;
  }
  Once(g_said_travel, "a travel agent serves player two");
  return true;
}

// A MONEY ITEM GIVEN TO PLAYER TWO IS GOLD. Inventory.AddItem turns a record with a MoneyComponent into gold
// only for a receiver with a player component (kind 33): `lwz r11,4(r29)` / `lwz r10,0x28(r11)` /
// `extrwi r9,r10,1,30` / `cmplwi cr6,r9,0` / `beq` → the plain add (TU1 0x824F0B18..0x824F0B28, disc
// 0x8261A800..0x8261A810); then CECMoney_Modify on the receiver's comp81 (0x824F0C8C / 0x8261A974). The
// host's copy of an online joiner's hero has no kind 33, so a Money Bag became an ITEM in hero two's inventory
// and no gold. Before the compare (TU1 0x824F0B24, disc 0x8261A80C; r11 the receiver): a hero two who
// brought a hero passes as the player they are.
void RefiiCoopMoneyItemForPlayerTwo(PPCRegister& r9, PPCRegister& r11) {
  uint8_t* base = RefiiGuestBase();
  if (r9.u32 != 0u || base == nullptr || RefiiCoopGetOwnHero() == 0 || r11.u32 == 0u ||
      r11.u32 != refii_mod_player_hero(base, 2)) {
    return;
  }
  r9.u64 = 1u;
  Once(g_said_money_item, "a money item given to player two is gold");
}

// True: open the shop for player two (jump to the shopkeeper path); false: the shipped test.
bool RefiiCoopShopForLocalPlayerTwo() {
  if (g_shop_for_local_player_two) {
    Once(g_said_shop, "a shop serves player two");
    if (g_shop_user_is_local_two) {
      g_shopping_player_two.store(true, std::memory_order_relaxed);
    }
    return true;
  }
  return false;
}

// CRIME IS THE CRIMINAL'S. AddCrime (`sub_827C9C10`) records a crime against the criminal, but
// for a player criminal that is not hero one it then makes hero one the criminal: `bl
// sub_8222C1B8` (0x827C9F64), `cmplw r28, r3`, `mr r29, r3` (0x827C9F9C) — r29 already holds the
// real criminal (`mr r29, r28`, 0x827C9F50), and everything after uses r29. True skips the remap
// for hero two, so the alarm, the arrest, the fine and the crime screen (which already picks
// player two's, `DisplayCrimeScreen` 0x827CFF08) are player two's own.
bool RefiiCoopCrimeIsTheirs(PPCRegister& r28) {
  uint8_t* base = RefiiGuestBase();
  return base != nullptr && r28.u32 != 0u && RefiiCoopGetOwnHero() != 0 && r28.u32 == refii_mod_player_hero(base, 2);
}

// A DEED'S ALIGNMENT AND RENOWN ARE THE DOER'S. The villager's deed handler
// (`rex_CECOpinionReaction_OnDeed`, `sub_82786F58`) credits morality, purity and renown to hero
// one's stats whoever did the deed (`bl sub_8222C1B8` at 0x8278809C, then `sub_824D62A0`,
// `sub_824D6BE0`, `sub_824D5BC0`). The deed message is copied to the frame at r1+0xC0
// (`sub_82619EA8`, 0x82787F24), its doer the weak reference at message+72 — r1+0x108, {vtable,
// holder} with the entity at *holder. After the call: a deed by hero two credits hero two.
void RefiiCoopDeedDoer(PPCRegister& r1, PPCRegister& r3) {
  using refii::vfs::LoadGuestU32;
  uint8_t* base = RefiiGuestBase();
  if (base == nullptr) {
    return;
  }
  const uint32_t holder = LoadGuestU32(base, r1.u32 + 0x108u + 4u);
  const uint32_t doer = Plausible(holder) ? LoadGuestU32(base, holder) : 0u;
  if (doer != 0u && RefiiCoopGetOwnHero() != 0 && doer == refii_mod_player_hero(base, 2)) {
    r3.u64 = doer;
  }
}

// EACH PLAYER HEARS WHAT IS AROUND THEM ON SPLIT SCREEN. The mixer hears a sound from the nearest of
// its primary listener (player one's camera) and its secondary listeners — one at each player's body,
// which every Player keeps (CECPlayer +348, `sub_823ED9F0`, moved each frame by `sub_823EDF80`) —
// but only for a sound whose definition says AudibleToSecondaryListeners: the distance test
// `sub_82C5B8D8` takes the flag in r4 (`clrlwi r9, r4, 24` at 0x82C5B8E0) and otherwise measures from
// the camera alone. On split screen every sound takes the nearest listener, so player two hears the
// world around them in their half. That is LOUDNESS only; the side a sound comes from is
// RefiiCoopListenerForSound, below.
extern "C" int RefiiCoopSplitScreen(void);

void RefiiCoopAudibleToBoth(PPCRegister& r4) {
  if (RefiiCoopSplitScreen() != 0) {
    r4.u64 = 1u;
  }
}

// LOCAL PLAYER TWO SHOPS AS THEMSELVES. The shop screen (22) acts for "the local player"
// (`sub_821C7CE0`, record 1 on one machine; every buy, sell and tattoo removal: sub_826B0768
// 0x826B0948, sub_826AFCF0 0x826AFEA4, sub_826B0D10 0x826B0D6C, sub_826AF430) and judges price and
// tattoos on hero one outright (`sub_8222C1B8` at 0x8246DBB0 in sub_8246DB30, 0x8246E0E8 in
// sub_8246DE50). While player two's shop is up those answer player two and hero two; the flag
// drops on the first read after the shop screen is gone.
namespace {

bool ShopScreenUp(uint8_t* base) {
  using refii::vfs::LoadGuestU32;
  const uint32_t gui = LoadGuestU32(base, refii::guest::Address(0x83496B08u));
  const uint32_t screens = Plausible(gui) ? LoadGuestU32(base, gui + 56u) : 0u;
  const uint32_t id = Plausible(screens) ? LoadGuestU32(base, screens + 4u) : 0xFFFFFFFFu;
  return id != 0xFFFFFFFFu && refii::guest::ScreenIdToTu1(id) == 22u;
}

bool Shopping(uint8_t* base) {
  if (!g_shopping_player_two.load(std::memory_order_relaxed)) {
    return false;
  }
  if (!ShopScreenUp(base)) {
    g_shopping_player_two.store(false, std::memory_order_relaxed);
    return false;
  }
  return true;
}

}  // namespace

extern "C" int RefiiCoopShoppingPlayerTwo(void) {
  return g_shopping_player_two.load(std::memory_order_relaxed) ? 1 : 0;
}

/// Set by the pump around a guest call it makes FOR local player two (their drunk warp): "the
/// local player" answers player two for that call. Guest tick thread only.
namespace {
int g_local_player_two_call = 0;
}  // namespace

extern "C" void RefiiCoopLocalPlayerTwoCall(int on) { g_local_player_two_call += on != 0 ? 1 : -1; }

// `sub_821C7CE0`'s tail (`li r4, 1` then `b sub_82264230`): r4 is the local player's number.
void RefiiCoopShopPlayer(PPCRegister& r4) {
  uint8_t* base = RefiiGuestBase();
  if (base != nullptr && r4.u32 == 1u && (g_local_player_two_call > 0 || Shopping(base))) {
    r4.u64 = 2u;
  }
}

// The same rule INLINED: `lbz 0x34` / `lbz 0x35` of the net manager, `li r4, 1`, `li r4, 2` when not
// both, then `bl sub_82264230` (disc `sub_822641F0`) — at the `bl`, r4 is the local player's id. The
// sites a player-two screen reaches (gui_screen_stub_support.cpp OwnerScope): the gift's recipient
// `sub_8230F540` 0x8230F594 / disc 0x823031EC; the furniture's targeted piece `sub_826B61C8` 0x826B622C /
// 0x8259B41C and its label `sub_826B6678` 0x826B66DC / 0x8259B844; the decoration's mount `sub_826B7128`
// 0x826B718C / 0x8259C3A4; the furniture pages `sub_8299B6D8` 0x8299B780 / 0x828F12A8 and `sub_8299C710`
// 0x8299C780 / 0x828F2328; the decoration pages `sub_8299C008` 0x8299C0AC / 0x828F1C5C and `sub_8299CF80`
// 0x8299CFEC / 0x828F2B94. While a call is made for local player two, each answers player two.
void RefiiCoopInlineLocalPlayer(PPCRegister& r4) {
  if (r4.u32 == 1u && g_local_player_two_call > 0) {
    r4.u64 = 2u;
  }
}

// THE SHOP IS ITS USER'S GUI. The shopkeeper path of the villager use opens the shop
// (`sub_823C4060`, TU1 0x827E1460 / disc 0x827A8408) and then states the GUI's owner the way every
// world screen's opener does: `lhGuiManager_FindScreenHandler(QM, 0)` (0x827E146C / 0x827A8414) and
// the pause root's vf+32 with `li r4, 0` (0x827E147C / 0x827A8424; the call 0x827E1488 / 0x827A8430)
// — player one, whoever used the villager. The owner is what the GUI tick serves
// (`lhGuiManager_TickUpdate` → `sub_8236E400`: player two's input object App+0x18 when the root's +4
// is 1) and what the host routes (guest_tick_pump.cpp StateMenuOwner). At the `lwz r10, 0x20(r11)`
// after the `li` (0x827E1480 / 0x827A8428): local player two's shop states player two, as the crime
// arrest states its criminal (0x827D0070 / 0x82855958).
void RefiiCoopShopOwner(PPCRegister& r4) {
  if (g_shop_user_is_local_two) {
    r4.u64 = 1u;
  }
}

// After `bl sub_8222C1B8` in the price and tattoo checks: r3 is hero one; player two's shop asks
// about hero two.
void RefiiCoopShopHero(PPCRegister& r3) {
  uint8_t* base = RefiiGuestBase();
  if (base != nullptr && Shopping(base)) {
    const uint32_t two = refii_mod_player_hero(base, 2);
    if (two != 0u) {
      r3.u64 = two;
    }
  }
}

// True jumps to the routine's own "credit this component" block; false runs the shipped split (the
// henchman's gains pool into hero one and split by the co-op screen's sliders).
bool RefiiCoopExperienceOwnPool() {
  if (HenchmanHere()) {
    return false;
  }
  Once(g_said_experience, "experience credited to its earner");
  return true;
}

bool RefiiCoopGoldOwnPurse() {
  if (HenchmanHere()) {
    return false;
  }
  Once(g_said_gold, "gold credited to its earner");
  return true;
}

bool RefiiCoopRenownOwnPool() {
  if (HenchmanHere()) {
    return false;
  }
  Once(g_said_renown, "renown credited to its earner");
  return true;
}

// True skips the refusal of player two (the ForHenchman path); false runs the shipped
// `cmplw cr6, r22, r3` (TU1 0x8278D998 / disc 0x8275FFA8).
bool RefiiCoopReactToPlayerTwo() {
  if (RefiiCoopGetOwnHero() == 0) {
    return false;
  }
  Once(g_said_reaction, "a villager reaction accepts player two");
  return true;
}

// Villager use (r3 villager component, r4 user). Records the shop decision for the call.
REX_EXTERN(rex_CECVillager_OnUse) {
  const uint32_t user = ctx.r4.u32;
  const bool previous = g_shop_for_local_player_two;
  const bool previous_local = g_shop_user_is_local_two;
  // A shop serves a player two who brought a hero; the henchman gets the shipped refusal.
  const bool two = Plausible(user) && RefiiCoopGetOwnHero() != 0 && user == refii_mod_player_hero(base, 2);
  g_shop_user_is_local_two = two && LocalCoopGame(base);
  g_shop_for_local_player_two = two && (LocalPlayerIsPlayerTwo(base) || g_shop_user_is_local_two);
  __imp__rex_CECVillager_OnUse(ctx, base);
  g_shop_for_local_player_two = previous;
  g_shop_user_is_local_two = previous_local;
}

// ---------------------------------------------------------------------------------------------
// Each online player their own camera, and the world alive around both players (phases 2-3).
//
//   OWN CAMERA. Shipped, a joiner's camera ran no Lua: the manager update (sub_821B9258), when
//     net +0x34 and +0x36 (in a session, the joiner) are set, builds an empty wrapper and only
//     interpolates toward the HOST's camera, streamed as net message 0xBD (sub_82482CB0). Fable 3
//     ticks each machine's own player's camera (sub_82209680). Four steps:
//     - the update takes its Lua path on a joiner too: the joiner branch's first instruction
//       0x821B92B0 (r26 the manager) jumps to the Lua path 0x821B930C (CameraManager:FrameUpdate
//       with the player's hero, the hero the joiner's Player owns) once the manager's
//       PrimaryCamera entity resolves (manager+36, a weak ref: cell at +40, entity at *cell;
//       spawned by sub_82481670 from the Player's entity setter). ActivateCamera binds the
//       entity into the new wrapper once (sub_82481760 0x82481A6C → sub_825AD080) and the Lua
//       never re-activates a camera it holds as current, so an activation before the spawn
//       leaves the wrapper with no entity and the default pose forever. Until then the shipped
//       joiner branch runs;
//     - the Lua's ActivateCamera works there: sub_82481760 bails unless net +0x34 and +0x35
//       (the session host). The wrapper below marks a joiner's call and the hook before that
//       test (0x824817BC) jumps past it (0x824817EC). Front-end calls, where neither byte is
//       set, keep the shipped refusal;
//     - the live camera block is the machine's own: sub_82265F90 returned manager+0x130 (the
//       streamed copy) on a joiner; its game+0x11C test 0x82265FB0 jumps to the +0x90 path
//       0x82266004 on every machine;
//     - no camera is APPLIED across the wire: a received 0xBD never reaches sub_82481FC0
//       (0x82332604 → the handler's exit 0x82334954), which would MOVE the receiver's camera
//       entity (vt+100). Only the joiner sends its camera (`bl sub_82171560` 0x821B922C), and the
//       host keeps it as the peer view (FREE ROAM below);
//     The Lua side (each camera frames its own hero, the leash switch) is the core camera chunk,
//     Godot/src/host/CoopHeroLua.cs.
//
//   WORLD AROUND BOTH. The LOD distance (sub_822904E8: f1 = |v1 − centre|², the centre cached
//     once per tick at players+0xA0) centres on the MIDPOINT of the two heroes in co-op, so
//     spawners, AI rates and creature LOD die around players who part. Fable 3 takes the nearer
//     view (sub_82205D10). Here: the midpoint store (0x822906A0, positions still in v11/v12)
//     records both heroes, the camera-centre store (0x822906B8) clears them, and after the
//     result load (`lfs f1` 0x822906D0) a recorded pair turns f1 into the nearer hero's
//     distance. Lanes 1..3 are xyz (vmsum3fp128 sums guest elements 0..2).

extern "C" REX_FUNC(__imp__rex_lhCameraManager_ActivateCamera);

namespace {

std::atomic<bool> g_said_own_camera{false};
std::atomic<bool> g_said_lod{false};
std::atomic<bool> g_said_lod_camera{false};

/// Set by the ActivateCamera wrapper for its call: this machine is the online joiner.
bool g_activate_as_joiner = false;

bool IsJoiner(uint8_t* base) {
  const uint32_t root = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = Plausible(root) ? refii::vfs::LoadGuestU32(base, root + 0xCu) : 0u;
  const uint32_t net = Plausible(game) ? refii::vfs::LoadGuestU32(base, game + 0x8Cu) : 0u;
  return Plausible(net) && refii::vfs::LoadGuestU8(base, net + 0x34u) != 0u &&
         refii::vfs::LoadGuestU8(base, net + 0x36u) != 0u;
}

/// The two heroes this tick when the LOD centre is their midpoint. Written once a tick by the
/// centre's recompute, read by every query after it.
struct LodPair {
  std::atomic<bool> two{false};
  float a[3] = {0.0f, 0.0f, 0.0f};
  float b[3] = {0.0f, 0.0f, 0.0f};
};
LodPair g_lod;

float DistanceSquared(const float* p, const PPCVRegister& v) {
  const float dx = v.f32[1] - p[0];
  const float dy = v.f32[2] - p[1];
  const float dz = v.f32[3] - p[2];
  return dx * dx + dy * dy + dz * dz;
}

}  // namespace

extern "C" uint8_t* RefiiGuestBase(void);

bool RefiiCoopOwnCameraUpdate(PPCRegister& r26) {
  uint8_t* base = RefiiGuestBase();
  const uint32_t cell = base != nullptr ? refii::vfs::LoadGuestU32(base, r26.u32 + 40u) : 0u;
  if (!Plausible(cell) || !Plausible(refii::vfs::LoadGuestU32(base, cell))) {
    return false;
  }
  Once(g_said_own_camera, "a joiner's camera runs its own Lua");
  return true;
}

bool RefiiCoopActivateCameraAsJoiner() { return g_activate_as_joiner; }

// PLAYER TWO'S D-PAD FILL LEAVES THE DIG HINT ALONE. `sub_823E51A0`'s third argument also shows
// and dismisses the shared tutorial hints 14 and 15 (`bl sub_821B5260` 0x823E5EE0, `bl
// sub_8244DC88` 0x823E5F18 and 0x823E5F34); the pump fills player two's d-pad through it
// (SamplePlayerTwoHud), and those three calls are skipped while it does, so the hint stays the
// local hero's. Each next instruction reloads what it reads.
bool RefiiCoopDpadQuerySkip() { return refii_bridge_dpad_query_two() != 0; }

void RefiiCoopOwnLiveCamera() {}

void RefiiCoopLodPair(PPCVRegister& v11, PPCVRegister& v12) {
  for (int i = 0; i < 3; ++i) {
    g_lod.a[i] = v12.f32[i + 1];
    g_lod.b[i] = v11.f32[i + 1];
  }
  g_lod.two.store(true, std::memory_order_release);
  Once(g_said_lod, "the world centres on the nearer player");
}

bool PartnerCameraPosition(uint8_t* base, float* out);

// The camera branch (TU1 0x822906B8 / disc 0x82290260, `stvx128 v0,r29,r28`: v0 the camera position
// sub_82199398 built from "the" camera, player one's): taken in single player, outside co-op, with a
// hero out of the world, and whenever player one's manager holds an override camera (manager+0x3C,
// 0x82290590). With player two's view on this machine the centre is each player's own camera — the
// nearer of player one's (v0) and player two's (their camera's block) — so player two keeps the world
// around them through player one's cutscene camera. Otherwise the pair is cleared: the guest's centre.
void RefiiCoopLodCamera(PPCVRegister& v0) {
  uint8_t* base = RefiiGuestBase();
  float partner[3];
  if (base == nullptr || !PartnerCameraPosition(base, partner)) {
    g_lod.two.store(false, std::memory_order_release);
    return;
  }
  for (int i = 0; i < 3; ++i) {
    g_lod.a[i] = v0.f32[i + 1];
    g_lod.b[i] = partner[i];
  }
  g_lod.two.store(true, std::memory_order_release);
  Once(g_said_lod_camera, "the world centres on the nearer player's camera under an override camera");
}

void RefiiCoopLodNearer(PPCRegister& f1, PPCVRegister& v127) {
  if (!g_lod.two.load(std::memory_order_acquire)) {
    return;
  }
  const float a = DistanceSquared(g_lod.a, v127);
  const float b = DistanceSquared(g_lod.b, v127);
  f1.f64 = static_cast<double>(a < b ? a : b);
}

// ---------------------------------------------------------------------------------------------
// Local split screen: player two's own camera (phase 4, the guest half).
//
//   The Player constructor (sub_82389938, misnamed rex_lhSceneNode_Create) builds a camera
//   manager only for id < 2 or the online joiner (`cmplwi cr6,r4,2; blt` 0x82389974..0x823899a0,
//   then net +0x34/+0x36); the entity setter (sub_82389D60) spawns the camera entities through
//   sub_82481670 under the same gate (0x82389f64). On split screen, the hook after each id test
//   (0x823899A4 → 0x823899E0, 0x82389F6C → 0x82389FA4) lets player two's Player through: the
//   constructor's hook records the Player it built a manager for (r31), and the setter's hook
//   (r27) only takes Players so recorded — its path dereferences Player+4.
//   WANTED IS NOT IN EFFECT. The constructor reads the switch once, inside the drop-in, before
//   the co-op screen opens (TU1 `sub_8233C2A0`: `bl lhScene_UpdateNodes` 0x8233C42C builds the
//   Player, `bl lhNetManager_DisplayCoopScreen` 0x8233C4B4; disc 0x8231EE04 / 0x8231EE8C), and a
//   region change builds no Player while the map holds one (`sub_82316FE0` tests the map's count
//   first). So the constructor asks RefiiCoopSplitScreenWanted, and everything else asks
//   RefiiCoopSplitScreen — wanted AND player two's current Player is one recorded here.
//   One manager is ticked per frame (0x821d4a68: sub_821F87C8 "the camera" → sub_821B9258). The
//   update wrapper ticks player two's manager straight after player one's, with the three
//   latch bytes the update shares across managers (byte_83496BE9/BEA/BEB) kept per manager,
//   and marks the manager it is ticking: LuaAPI_ActivateCamera always targets sub_821F87C8's
//   (player one's) manager, so the ActivateCamera wrapper points a call made during player
//   two's tick at player two's manager. Player two's camera wrapper is owned by player two's
//   Player (sub_825AD080, owner id at +36), so the input dispatch (sub_821CCED8) already routes
//   player two's pad to it. Each camera frames only its own player (the core camera chunk).
//   The camera's DOF/post message (sub_82175D50, vtable off_820A645C) is not sent either
//   (`bl sub_82485478` 0x82175EC8 → 0x82175ECC): with each machine's own camera, applying the
//   peer's would overwrite one's own.

extern "C" int RefiiCoopSplitScreen(void);
extern "C" int RefiiCoopSplitScreenWanted(void);
extern "C" REX_FUNC(__imp__rex_lhCameraManager_Update);

namespace {

/// The LIVE Players the constructor built a camera manager for (player two's): added by the constructor,
/// removed when the guest destroys that manager (rex_lhCameraManager_Destroy below). Only key 2 is ever
/// added and the guest holds one key-2 Player at a time, two while a new one is built before the old
/// one's release, so four slots never fill; a full table means a manager was never destroyed, and the
/// oldest entry gives way (logged). Written on the guest thread; the host bridge asks
/// RefiiCoopSplitCameraBuilt from Godot's.
std::array<std::atomic<uint32_t>, 4> g_split_players{};
std::array<uint32_t, 4> g_split_added{};
uint32_t g_split_adds = 0;

bool IsSplitPlayer(uint32_t player) {
  for (const std::atomic<uint32_t>& slot : g_split_players) {
    if (player != 0u && slot.load(std::memory_order_relaxed) == player) {
      return true;
    }
  }
  return false;
}

void RememberSplitPlayer(uint32_t player, bool built) {
  for (std::atomic<uint32_t>& slot : g_split_players) {
    if (slot.load(std::memory_order_relaxed) == player) {
      slot.store(built ? player : 0u, std::memory_order_relaxed);
      return;
    }
  }
  if (!built) {
    return;
  }
  std::size_t at = g_split_players.size();
  for (std::size_t i = 0; i < g_split_players.size(); ++i) {
    if (g_split_players[i].load(std::memory_order_relaxed) == 0u) {
      at = i;
      break;
    }
  }
  if (at == g_split_players.size()) {
    at = 0;
    for (std::size_t i = 1; i < g_split_added.size(); ++i) {
      if (g_split_added[i] < g_split_added[at]) {
        at = i;
      }
    }
    REXLOG_WARN("Refii co-op heroes: four Players with a camera manager stand and none was destroyed; "
                "Player 0x{:08X} gives way to 0x{:08X}",
                g_split_players[at].load(std::memory_order_relaxed), player);
  }
  g_split_added[at] = ++g_split_adds;
  g_split_players[at].store(player, std::memory_order_relaxed);
}

/// The guest destroyed a manager whose Player (manager+0) this table holds: it holds it no longer.
void ForgetSplitPlayer(uint32_t player) {
  for (std::atomic<uint32_t>& slot : g_split_players) {
    if (player != 0u && slot.load(std::memory_order_relaxed) == player) {
      slot.store(0u, std::memory_order_relaxed);
    }
  }
}

/// The manager player two's tick is running for, while it runs; else 0.
uint32_t g_ticking_manager = 0;

/// Player two's copies of the update's three latch bytes.
std::array<uint8_t, 3> g_second_latches{};

constexpr std::array<uint32_t, 3> kLatches = {0x83496BE9u, 0x83496BEAu, 0x83496BEBu};

/// A player's Player: the player map at game+0x9C ({key, Player, _} entries from container+8
/// to +0xC). 0 when there is none.
uint32_t PlayerForKey(uint8_t* base, uint32_t key) {
  const uint32_t root = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = Plausible(root) ? refii::vfs::LoadGuestU32(base, root + 0xCu) : 0u;
  const uint32_t container = Plausible(game) ? refii::vfs::LoadGuestU32(base, game + 0x9Cu) : 0u;
  if (!Plausible(container)) {
    return 0u;
  }
  const uint32_t begin = refii::vfs::LoadGuestU32(base, container + 0x8u);
  const uint32_t end = refii::vfs::LoadGuestU32(base, container + 0xCu);
  if (!Plausible(begin) || end <= begin || end - begin > 0x1000u) {
    return 0u;
  }
  for (uint32_t entry = begin; entry + 12u <= end; entry += 12u) {
    if (refii::vfs::LoadGuestU32(base, entry) == key) {
      const uint32_t player = refii::vfs::LoadGuestU32(base, entry + 4u);
      return Plausible(player) ? player : 0u;
    }
  }
  return 0u;
}

/// A player's camera manager: Player+4 the holder, holder+4 the manager. 0 when there is none.
uint32_t ManagerForKey(uint8_t* base, uint32_t key) {
  const uint32_t player = PlayerForKey(base, key);
  const uint32_t holder = player != 0u ? refii::vfs::LoadGuestU32(base, player + 4u) : 0u;
  const uint32_t manager = Plausible(holder) ? refii::vfs::LoadGuestU32(base, holder + 4u) : 0u;
  return Plausible(manager) ? manager : 0u;
}

std::atomic<bool> g_said_split_camera{false};
std::atomic<bool> g_said_partner_scripts{false};

/// Player two's camera manager once the game's own update (lhCameraManager_Update, below) has run
/// for it; 0 from the Player constructor until then. A manager the update has not run for holds
/// its constructed block (position 0,0,0), which is no camera to pan a sound from.
std::atomic<uint32_t> g_second_manager_updated{0};

/// Which camera manager player two's is: moves on at every second player's Player built with one.
std::atomic<uint32_t> g_partner_manager_generation{0};

}  // namespace

// PLAYER TWO ALWAYS HAS A CAMERA MANAGER IN LOCAL CO-OP (camera stage C2d): shared screen or split, so
// the screen can split or merge under an unchanged picture. The constructor gate answers "local co-op"
// (game+0x11C == 2, the guest's IsLocalCoopGame) rather than "split wanted"; what is DRAWN and what the
// sim's readers take stay keyed to split in effect (RefiiCoopSplitScreen). A shared screen keeps the
// shipped rules: player two's manager updates (below) but its camera states nothing the save or the
// shared screen reads (the core camera chunk, across the whole update: RefiiCoopPartnerCameraUpdating),
// and its construction runs no camera script (RefiiCoopPartnerCameraScripts).
bool RefiiCoopSplitPlayerCamera(PPCRegister& r31) {
  uint8_t* base = RefiiGuestBase();
  const bool build = base != nullptr && LocalCoopGame(base);
  g_second_manager_updated.store(0u, std::memory_order_release);
  RememberSplitPlayer(r31.u32, build);
  REXLOG_INFO("Refii co-op heroes: a second player's Player 0x{:08X} constructed {} a camera "
              "manager (local co-op {}, split screen {})", r31.u32, build ? "with" : "without",
              build ? "yes" : "no", RefiiCoopSplitScreenWanted() != 0 ? "wanted" : "not wanted");
  if (build) {
    g_partner_manager_generation.fetch_add(1u, std::memory_order_acq_rel);
    Once(g_said_split_camera, "player two gets their own camera (local co-op)");
  }
  return build;
}

// THE SECOND MANAGER RUNS NO CAMERA SCRIPT. lhCameraManager_Create (TU1 0x82481328 / disc 0x82422B08; r3
// the manager, kept in r31, `stw r4,0(r31)` the Player, whose +0x24 is its player-map key, `stw
// r4,0x24(r31)` 0x82389988) runs every camera script file again (`bl lhLuaScriptSystem_RunCameraScripts`
// 0x82481568 / 0x82422D48). Shipped, a manager is only ever built at a level or save load; player two's,
// built at a drop-in mid-level, would empty the saved CameraManager.Overrides (cameramanager.lua line 2,
// `CameraManager.Overrides = {}`) and rebuild every mode table, restarting player one's camera. For a
// key-2 Player in local co-op with player one's manager standing (the scripts already ran in this VM) the
// call is skipped (0x8248156C / 0x82422D4C): the manager binds the camera tables that stand.
bool RefiiCoopPartnerCameraScripts(PPCRegister& r31) {
  uint8_t* base = RefiiGuestBase();
  if (base == nullptr || !LocalCoopGame(base)) {
    return false;
  }
  const uint32_t player = refii::vfs::LoadGuestU32(base, r31.u32);
  if (!Plausible(player) || refii::vfs::LoadGuestU32(base, player + 0x24u) != 2u || ManagerForKey(base, 1u) == 0u) {
    return false;
  }
  Once(g_said_partner_scripts, "player two's camera manager keeps the camera scripts that stand");
  return true;
}

/// 1 in local co-op while player two's current Player was built with a camera manager.
extern "C" int RefiiCoopPartnerCameraManager(void) {
  uint8_t* base = RefiiGuestBase();
  return base != nullptr && LocalCoopGame(base) && IsSplitPlayer(PlayerForKey(base, 2u)) ? 1 : 0;
}

/// 1 for the whole of player two's camera manager update (the second lhCameraManager_Update below).
/// One update runs two Lua calls: the selection, CameraManager:FrameUpdate(hero) (`bl sub_821AC8B0` on
/// manager+0x18 / +4, TU1 0x821B94EC, disc `bl sub_821ACA18` 0x821B95B4), and then the active mode's
/// FrameUpdate (`bl sub_8219A758` on wrapper+0x2C / +0x18, the override wrapper +0x3C else the primary
/// +0x2C, TU1 0x821B9514, disc `bl sub_8219A800` 0x821B95DC). The mode's update is what writes the
/// saved CameraManager.CameraPosition and states the leash and the canopy fading, so the core camera
/// chunk asks this (Refii._partner_camera_update) rather than marking the selection call alone.
extern "C" int RefiiCoopPartnerCameraUpdating(void) { return g_ticking_manager != 0u ? 1 : 0; }

/// PLAYER TWO'S CAMERA IS NEW AT EVERY JOIN. A local quit destroys player two's Player and its manager;
/// a re-join builds both again (the constructor above). A new manager has no mode wrapper: only
/// ActivateCamera (sub_82481760, TU1 / disc 0x82422FA0) builds the primary (+0x2C) and override (+0x3C)
/// wrappers, and the update calls the active mode with no test (`lwz r11,0x3C(r26)` .. `lwz r30,0x2C(r26)`
/// .. `addi r3,r30,0x2C; bl sub_8219A758` TU1 0x821B94F0..0x821B9514, disc 0x821B95B8..0x821B95DC; its
/// first load `lwz r11,0(r30)` 0x8219A76C / 0x8219A814 faults at 0x2C with no wrapper). CameraManager:
/// FrameUpdate activates only a camera that differs from self.CurrentCamera, so the core camera chunk
/// keeps player two's current camera against this number and starts it empty when the number moves.
/// Counts from 1; 0 never names a manager.
extern "C" int RefiiCoopPartnerCameraGeneration(void) {
  return static_cast<int>(g_partner_manager_generation.load(std::memory_order_acquire) & 0xFFFFFFu);
}

bool RefiiCoopSplitPlayerCameraEntities(PPCRegister& r27) { return IsSplitPlayer(r27.u32); }

/// 1 when player two's current Player (player-map key 2) is one the constructor built a camera
/// manager for on split screen; the half of "split screen in effect" the switch cannot answer.
extern "C" int RefiiCoopSplitCameraBuilt(void) {
  uint8_t* base = RefiiGuestBase();
  return base != nullptr && IsSplitPlayer(PlayerForKey(base, 2u)) ? 1 : 0;
}

/// 1 while player two has a Player (player-map key 2). Local co-op's drop-in builds it before the co-op
/// screen opens, without its hero (`lhScene_UpdateNodes` key 2, last argument 0 = no SetupCamera: TU1
/// `li r7,2` 0x82316BFC / `bl lhScene_CreateNode` 0x82316C10, disc 0x82352F5C / 0x82352F70); the hero is
/// set later by `lhNetManager_SetupHenchman`.
extern "C" int RefiiCoopPlayerTwoNode(void) {
  uint8_t* base = RefiiGuestBase();
  return base != nullptr && PlayerForKey(base, 2u) != 0u ? 1 : 0;
}

void RefiiCoopNoCameraPostSend() {}

// A PARTNER'S PAUSE PAUSES NOBODY ELSE (Fable 3: each player roams while the other is in a menu).
// Shipped, the inbound pause message (sub_8232B518, pause byte r1+0x74, UI byte r1+0x75) closes
// the receiver's own GUI (sub_8236ED98) and runs sub_8232B2B0: net+217 = 1, GameFlow mode 1
// (sub_823D4C48; mode 8 for "looking at UI") pushed — the world stops — and the
// GUI_NET_PAUSED_KICK_CLIENT box. The hook sits on the PAUSE branch only (0x8232C2F4, after the
// pause byte's test; disc 0x8230EDC4) and goes to the handler's own exit (0x8232C378 `mr r3,r25`,
// disc 0x8230EE48): a partner's pause changes nothing here. Its RESUME (0x8232C384, disc
// 0x8230EE54) runs as shipped — it pops the pushed mode and clears net+217 and the notice
// (sub_8232B480) — so nothing can strand them; on the pause byte's load the hook threw the
// resume away too, and a card dismissed first left "Host has paused the game" up for good.
std::atomic<bool> g_said_partner_pause{false};

bool RefiiCoopPartnerPauseRoams() {
  Once(g_said_partner_pause, "a partner's pause leaves this player roaming");
  return true;
}

// A CLIENT'S OWN CARD PAUSES NOBODY EITHER. Dismissing the item-received card (screen 8,
// sub_82538BF0) or the quest card (screen 12, sub_823D4750) before the host, a client raises the
// host-paused wait on itself (`bl sub_8232B2B0`, 0x82538D24 / 0x823D48A4; disc 0x824044C4 /
// 0x824F2CAC) and is held under it until the host's own dismissal sends the resume. Each player
// roams while the other is in a menu, so the client's own raise is skipped; the host's close
// (msg 0x101 / 0xE6) still closes the client's card.
std::atomic<bool> g_said_own_card{false};

bool RefiiCoopOwnCardRoams() {
  Once(g_said_own_card, "a client's own card leaves it roaming");
  return true;
}

// A PLAYER'S OWN PAUSE HOLDS NO WORLD IN AN ONLINE SESSION. The world advances only through the sim-step
// slot (+0x18) of the command on top of the GameFlow stack: the flow tick `sub_821762C0` ends in
// `lwz r3,8(r11); lwz r11,0(r3); lwz r10,0x18(r11); bctrl` (0x821764BC..CC; disc `sub_82176088`
// 0x82176284..94). CGameModePause (vt 0x820AEC7C / disc 0x820B3AEC) and the box state CGameModeUI
// (0x820AED78 / 0x820B3BE8) hold `blr` there (0x82B96E78 / 0x829CE870), and the pose pass
// `sub_821A61B0` (disc `sub_821A6010`) calls the top's +0x1C the same way (0x821A623C..54 / 0x821A609C..B4),
// where the pause holds only the pad dispatch (`sub_823D5378` / disc `sub_8245C958`). The pause is pushed with no
// session test (lhGuiManager_EnterGuiMode `bl lhGameFlow_IsGuiActive; bne` 0x8236ED1C..28 then
// `bl CGameModePause_ctor; bl GameFlow_QueueCommand` 0x8236ED3C / 0x8236ED5C; disc 0x82336F1C..5C); shipped, its
// apply tells the partner (`lbz r5,0xD9(r6); bne` — `lwz r10,0x64(r3); ble` — `bl sub_82349460`, 0x823D505C..
// 0x823D5144; disc 0x8245C63C..0x8245C724) and both worlds stop together. With the partner's half cut
// (RefiiCoopPartnerPauseRoams) the pauser alone stood still, so here the pauser's world runs too: while this
// machine is in an online session with a partner (game+0x11C == 1, net+0x34, and the count the guest's own relay
// tests, net+0x64) and pause / box commands are on top, the state under them keeps its world. Its tick is the
// guest's step for a state without player modes, `sub_823D9898` (disc `sub_82460E78`: scripts, the world's sim
// tick, the camera, the world frame) — PlayGame's own step `sub_821D4628` (disc `sub_821D43D8`) refuses when it
// is not the top (`subf r7,r31,r3 .. beq` 0x821D46D8..E8 / 0x821D4488..98) — and its poses are that state's own
// +0x1C, when it is the world's (`sub_822AF5D8` / disc `sub_822AF410`). A state under the pause whose own slots
// run no world (a movie, a level change) keeps none. Solo and local co-op are untouched: one machine, one world.
std::atomic<bool> g_said_own_pause{false};
std::atomic<bool> g_said_own_pause_missing{false};

namespace {

constexpr uint32_t kFlowStepSlot = 0x18u;
constexpr uint32_t kFlowPoseSlot = 0x1Cu;

/// The command under the pause / box commands on top of the GameFlow stack, in an online session with a
/// partner; 0 when the top is not one of them, out of such a session, or nothing lies under them. The stack is
/// the list at `*(*(game+0x80)+4)`: sentinel `+4`, count `+8`, the top the sentinel's `+4`, each node's command `+8`.
uint32_t CommandUnderOwnPause(uint8_t* base) {
  using refii::vfs::LoadGuestU32;
  const uint32_t root = LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = Plausible(root) ? LoadGuestU32(base, root + 0xCu) : 0u;
  if (!Plausible(game) || LoadGuestU32(base, game + 0x11Cu) != 1u) {
    return 0u;
  }
  const uint32_t net = LoadGuestU32(base, game + 0x8Cu);
  if (!Plausible(net) || refii::vfs::LoadGuestU8(base, net + 0x34u) == 0u || LoadGuestU32(base, net + 0x64u) == 0u) {
    return 0u;
  }
  const uint32_t holder = LoadGuestU32(base, game + 0x80u);
  const uint32_t flow = Plausible(holder) ? LoadGuestU32(base, holder + 4u) : 0u;
  if (!Plausible(flow) || LoadGuestU32(base, flow + 8u) == 0u) {
    return 0u;
  }
  const uint32_t sentinel = LoadGuestU32(base, flow + 4u);
  uint32_t node = Plausible(sentinel) ? LoadGuestU32(base, sentinel + 4u) : 0u;
  bool paused = false;
  for (int depth = 0; depth < 8 && Plausible(node) && node != sentinel; ++depth) {
    const uint32_t command = LoadGuestU32(base, node + 8u);
    if (!Plausible(command)) {
      return 0u;
    }
    const uint32_t vtable = LoadGuestU32(base, command);
    if (vtable != refii::guest::Address(0x820AEC7Cu) && vtable != refii::guest::Address(0x820AED78u)) {
      return paused && Plausible(vtable) ? command : 0u;
    }
    paused = true;
    node = LoadGuestU32(base, node + 4u);
  }
  return 0u;
}

}  // namespace

/// Called after the flow tick ran no world tick (rex_lhGameFlow_Tick). 1 when the world's step ran here.
extern "C" int RefiiCoopOwnPauseRunsWorld(PPCContext* ctx, uint8_t* base) {
  const uint32_t under = CommandUnderOwnPause(base);
  if (under == 0u) {
    return 0;
  }
  const uint32_t step = refii::vfs::LoadGuestU32(base, refii::vfs::LoadGuestU32(base, under) + kFlowStepSlot);
  if (step != refii::guest::Address(0x821D4628u) && step != refii::guest::Address(0x823D9898u)) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  if (!refii::mods::CallGuest(work, base, refii::guest::Address(0x823D9898u))) {
    Once(g_said_own_pause_missing, "own pause online: the world's step is not a known function - the world is held");
    return 0;
  }
  Once(g_said_own_pause, "this player's own pause leaves the world running (online session)");
  return 1;
}

/// Called after the pose pass (rex_lhGameLoop_HighFrequencyUpdate) with its alpha: the world's poses under the
/// player's own pause.
extern "C" void RefiiCoopOwnPauseRunsPoses(PPCContext* ctx, uint8_t* base, double alpha) {
  const uint32_t under = CommandUnderOwnPause(base);
  if (under == 0u) {
    return;
  }
  const uint32_t pose = refii::vfs::LoadGuestU32(base, refii::vfs::LoadGuestU32(base, under) + kFlowPoseSlot);
  if (pose != refii::guest::Address(0x822AF5D8u)) {
    return;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  work.f1.f64 = alpha;
  refii::mods::CallGuest(work, base, pose);
}

// A PARTNER'S LOOK-AT (held left trigger) IS THEIRS. The left trigger sets the presser's own
// look-at flag (`sub_823E9F10`, comp33+32) and, online, sends it (msg off_820A6414). The receiver
// (`sub_82392250`, 0x8239AF54) stores it in hero one's comp33+33 — "the partner's look-at" — and
// `sub_823E24B0` then takes hero one's look-at mask while either flag is set: one shared camera,
// shipped. With a camera each, player two's held trigger put player one into look-at (seen live:
// host P1 +33 = 1 for the whole hold). The hook at the store's first load (0x8239AF54) goes to
// the dispatcher's exit (0x8239CBCC): +33 stays clear and the presser's own +32 still acts.
std::atomic<bool> g_said_partner_look{false};

bool RefiiCoopPartnerLookAtStaysTheirs() {
  Once(g_said_partner_look, "a partner's held look-at stays theirs");
  return true;
}

// THE TARGETED READOUT IS THIS SCREEN'S PLAYER'S. The readout writer `sub_823FD410` sends what it
// writes to the peer in a session (message 221, `off_820A63D8` through `sub_823FDDA8`, when
// world+140 bytes 52 and 53 are set) and then fills the HUD's TargetedQuery elements; the
// receiver's case 221 (`sub_8232E398` 0x82332278) copies the peer's readout (`sub_823349C8`) and
// runs the same writer — one shared screen, shipped. With a screen each, the peer's write replaced
// this player's: the host showed player two's target, and the joiner held player one's target,
// off its screen, in place of its own. The hook at the copy (0x82332320) goes past the writer
// (0x82332334), skipping the copy and its release together.
std::atomic<bool> g_said_peer_readout{false};

bool RefiiCoopPeerReadoutIgnored() {
  Once(g_said_peer_readout, "the other player's targeted readout stays on their screen");
  return true;
}

// THE LOOK-AT TARGET IS THE LOCAL PLAYER'S. The HUD's look-at readout (`sub_8219C098`, GUI manager
// slot +76: the target's name and outline, SE_LOOKAT_NOTIFICATION) reads hero one only
// (players+104, 0x8219C0B4..0x8219C0C8) and asks its comp33 and comp77 (`sub_822C7D50`); the
// target's interaction panel (`sub_822BA8D8`) takes hero one too unless game+0x11C is 2. On a
// joiner hero one is the host's copy, with neither component: player two's held trigger targeted
// nothing. There the joiner's own hero stands in — the readout at the hero's load into r29
// (0x8219C0E0, r10), the panel where its two choices join (0x822BAA2C, r25 and its spill at
// r1+0x7C), with -1 at r1+0x60 as the guest's own hero-two choice writes (0x822BA9C8).
namespace {
uint32_t JoinerHero(uint8_t* base) {
  if (base == nullptr || !IsJoiner(base)) {
    return 0u;
  }
  const uint32_t two = refii_mod_player_hero(base, 2);
  return Plausible(two) && (refii::vfs::LoadGuestU8(base, two + 0x90u) & 0x40u) != 0u ? two : 0u;
}
}  // namespace

void RefiiCoopLookAtHero(PPCRegister& r10) {
  if (const uint32_t two = JoinerHero(RefiiGuestBase()); two != 0u) {
    r10.u64 = two;
  }
}

void RefiiCoopInteractionHero(PPCRegister& r1, PPCRegister& r25) {
  uint8_t* base = RefiiGuestBase();
  const uint32_t two = JoinerHero(base);
  if (two == 0u || r25.u32 != refii_mod_player_hero(base, 1)) {
    return;
  }
  r25.u64 = two;
  refii::vfs::StoreGuestU32(base, r1.u32 + 0x7Cu, two);
  refii::vfs::StoreGuestU32(base, r1.u32 + 0x60u, 0xFFFFFFFFu);
}

extern "C" void RefiiCameraActivated(uint8_t* base, uint32_t manager, uint32_t lua_camera);

// ActivateCamera (r3 camera manager, r4 the Lua camera table). Records a joiner's call, and
// points a call made during player two's tick at player two's manager; the activation is stated
// (Fable2/src/hooks/src/camera_statements.cpp).
REX_EXTERN(rex_lhCameraManager_ActivateCamera) {
  const bool previous = g_activate_as_joiner;
  g_activate_as_joiner = IsJoiner(base);
  if (g_ticking_manager != 0u) {
    ctx.r3.u32 = g_ticking_manager;
  }
  const uint32_t manager = ctx.r3.u32;
  const uint32_t lua_camera = ctx.r4.u32;
  __imp__rex_lhCameraManager_ActivateCamera(ctx, base);
  g_activate_as_joiner = previous;
  RefiiCameraActivated(base, manager, lua_camera);
}

extern "C" void RefiiMouseLookBeforeCameraUpdate(PPCContext& ctx, uint8_t* base, uint32_t manager);
extern "C" void RefiiMouseLookAfterCameraUpdate(PPCContext& ctx, uint8_t* base, uint32_t manager);

// The camera manager update (r3 manager). After player one's, player two's in local co-op (split or shared).
// The manager the guest itself updates is this machine's own player's (sub_821F87C8) — pad 0's,
// which the keyboard and mouse drive — and it takes the mouse's turn
// (Fable2/src/vfs/src/hooks/mouse_look.cpp); player two's split-screen manager, updated below, never does.
REX_EXTERN(rex_lhCameraManager_Update) {
  const uint32_t manager = ctx.r3.u32;
  RefiiMouseLookBeforeCameraUpdate(ctx, base, manager);
  __imp__rex_lhCameraManager_Update(ctx, base);
  RefiiMouseLookAfterCameraUpdate(ctx, base, manager);
  if (g_ticking_manager != 0u || RefiiCoopPartnerCameraManager() == 0) {
    return;
  }
  const uint32_t second = ManagerForKey(base, 2u);
  if (second == 0u || second == manager || manager != ManagerForKey(base, 1u)) {
    return;
  }

  std::array<uint8_t, 3> first_latches{};
  for (std::size_t i = 0; i < kLatches.size(); ++i) {
    const uint32_t at = refii::guest::Address(kLatches[i]);
    first_latches[i] = refii::vfs::LoadGuestU8(base, at);
    refii::vfs::StoreGuestU8(base, at, g_second_latches[i]);
  }

  g_ticking_manager = second;
  PPCContext work = ctx;
  work.r3.u32 = second;
  __imp__rex_lhCameraManager_Update(work, base);
  g_ticking_manager = 0u;
  g_second_manager_updated.store(second, std::memory_order_release);

  for (std::size_t i = 0; i < kLatches.size(); ++i) {
    const uint32_t at = refii::guest::Address(kLatches[i]);
    g_second_latches[i] = refii::vfs::LoadGuestU8(base, at);
    refii::vfs::StoreGuestU8(base, at, first_latches[i]);
  }
}

// A CAMERA MANAGER'S END. The manager has no vtable of its own: the Player holds it through an
// 8-byte holder (Player+4; vtable off_820BA384 / disc off_820B1130, stored by the Player constructor
// at 0x82389A00 / 0x8238F1D0, the manager at holder+4 0x82389A18 / 0x8238F1E8). The holder's one
// slot (sub_824833F0 / disc sub_82424C30) runs its body (sub_82483440 / sub_82424C80): `lwz
// r30,4(r31)` .. `mr r3,r30; bl sub_824834B0` then frees it (0x82483460..0x8248347C; disc
// 0x82424CA0..0x82424CBC, `bl sub_82424CF0`). That call is the manager destructor's only caller; it
// is hooked here, before its body: the manager's Player (manager+0, `stw r4,0(r31)` in
// lhCameraManager_Create 0x82481348 / 0x82422B28) leaves the table above, and the shell drops the
// manager (camera_statements.cpp RefiiCameraManagerDestroyed), so nothing answers a dead manager
// as a player's camera.
extern "C" REX_FUNC(__imp__rex_lhCameraManager_Destroy);
extern "C" void RefiiCameraManagerDestroyed(uint32_t manager);

REX_EXTERN(rex_lhCameraManager_Destroy) {
  const uint32_t manager = ctx.r3.u32;
  if (Plausible(manager)) {
    const uint32_t player = refii::vfs::LoadGuestU32(base, manager);
    if (IsSplitPlayer(player)) {
      REXLOG_INFO("Refii co-op heroes: player two's Player 0x{:08X} lost its camera manager 0x{:08X}",
                  player, manager);
    }
    ForgetSplitPlayer(player);
    uint32_t updated = manager;
    g_second_manager_updated.compare_exchange_strong(updated, 0u, std::memory_order_acq_rel);
    RefiiCameraManagerDestroyed(manager);
  }
  __imp__rex_lhCameraManager_Destroy(ctx, base);
}

// THE ACTIVE CAMERA FOLLOWS ITS RESPAWNED ENTITY. Player::SetHero (sub_82389D60) respawns the
// manager's camera entities every time a hero is set: sub_82481670 (disc sub_82422EB0) creates
// new "CameraBase" entities (sub_824832D0 → sub_8238B9C8) for PrimaryCamera (manager+36) and
// SecondaryCamera (+52). A camera wrapper already active (+44 primary, built from +36; +60
// secondary, from +52 — sub_82481760) keeps a handle to the old entity at wrapper+8, zeroed once
// it is resolved dead (sub_82193E38), and its Lua instance cached that entity in Activate
// (CameraBase:Activate's `camera`, passed by sub_825AD7E8 from wrapper+8). Re-activating the same
// camera only restarts that wrapper (0x824818D4/0x824818E4), so the camera never comes back: the
// joiner's hero is set again after its camera starts, and its view sat at the default pose. In a
// co-op session, a wrapper whose entity is not the manager's live one gets the game's own
// re-activation, with the handle rebound first: stop (sub_825ADAD0), assign (sub_82214898), start
// (sub_825AD7E8 with no blend, as sub_82481760 restarts). A wrapper with no Lua camera (+20) is the
// joiner branch's placeholder and is left alone.
extern "C" REX_FUNC(__imp__rex_lhCameraManager_SpawnCameraEntities);

namespace {
std::atomic<bool> g_said_camera_rebound{false};
}  // namespace

REX_EXTERN(rex_lhCameraManager_SpawnCameraEntities) {
  using refii::vfs::LoadGuestU32;
  const uint32_t manager = ctx.r3.u32;
  const PPCContext entry = ctx;
  __imp__rex_lhCameraManager_SpawnCameraEntities(ctx, base);

  const uint32_t root = LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = Plausible(root) ? LoadGuestU32(base, root + 0xCu) : 0u;
  const uint32_t net = Plausible(game) ? LoadGuestU32(base, game + 0x8Cu) : 0u;
  const bool session = Plausible(net) && refii::vfs::LoadGuestU8(base, net + 0x34u) != 0u;
  if (!Plausible(manager) || (!session && RefiiCoopPartnerCameraManager() == 0)) {
    return;
  }

  static PPCFunc* const stop = refii::vfs::FindGuestFunction(refii::guest::Address(0x825ADAD0u));
  static PPCFunc* const assign = refii::vfs::FindGuestFunction(refii::guest::Address(0x82214898u));
  static PPCFunc* const start = refii::vfs::FindGuestFunction(refii::guest::Address(0x825AD7E8u));
  if (stop == nullptr || assign == nullptr || start == nullptr) {
    return;
  }

  struct Slot {
    uint32_t wrapper;
    uint32_t handle;
  };
  for (const Slot slot : {Slot{44u, 36u}, Slot{60u, 52u}}) {
    const uint32_t wrapper = LoadGuestU32(base, manager + slot.wrapper);
    const uint32_t cell = LoadGuestU32(base, manager + slot.handle + 4u);
    const uint32_t entity = Plausible(cell) ? LoadGuestU32(base, cell) : 0u;
    if (!Plausible(wrapper) || !Plausible(entity) || LoadGuestU32(base, wrapper + 20u) == 0u) {
      continue;
    }
    const uint32_t bound = LoadGuestU32(base, wrapper + 12u);
    if (bound == cell) {
      continue;
    }
    PPCContext call = entry;
    call.r3.u32 = wrapper;
    stop(call, base);
    call = entry;
    call.r3.u32 = wrapper + 8u;
    call.r4.u32 = entity;
    assign(call, base);
    call = entry;
    call.r3.u32 = wrapper;
    call.r4.u64 = 0u;
    call.r5.u64 = 0u;
    start(call, base);
    Once(g_said_camera_rebound, "an active camera follows its respawned camera entity");
  }
}

// ---------------------------------------------------------------------------------------------
// FREE ROAM: the host simulates around BOTH players (Fable 3's peer view).
//
//   Fable 2's host measures what it simulates and sends from its OWN camera and hero only; Fable 3
//   sends each machine's camera (0x17B) and the host ORs the peer's view into the same tests
//   (`sub_821B4C38`, `sub_82205D10`, `sub_821FE8D8`; generators `sub_82A003A0` / `sub_82A00548`).
//   Here, with the guest's own code everywhere:
//   - the joiner sends its camera (0xBD, `sub_821B8FD8`); the host stores the decoded block
//     (r1+0x860 at 0x82332604 / disc 0x82314FDC: pos +0, up +0x10, fwd +0x20, hfov +0x30) and
//     never applies it. On local split screen the peer view is player two's own manager (+0x90);
//   - "the camera" for a peer query is that block: the camera-block getter `sub_82211598` answers
//     with it while t_peer_view is set, so the guest's own cone builder (`sub_82294468`), LOD tier
//     (`sub_822A1710`) and animation LOD (`sub_82211DF8`) run for the peer view unchanged;
//   - visibility refresh `sub_822141A8(comp, 0)` (distance² +104, in view +203; read by the transform
//     sender `sub_822BCA68`, the 0xAF sender `sub_8239CC80` and generator despawn `sub_827BDA20`)
//     runs again through its own cone path for the peer cone: nearer distance, in either view;
//   - creature LOD tier: the finer of the two; animation LOD: within either view's range;
//   - creature generators: spawn when EITHER hero is inside the create radius (+64), despawn only
//     when BOTH are beyond the destroy radius (+68), as `sub_82A003A0` / `sub_82A00548`.

extern "C" REX_FUNC(__imp__rex_lhCamera_CurrentBlock);
extern "C" REX_FUNC(__imp__rex_lhVisibility_Refresh);
extern "C" REX_FUNC(__imp__rex_lhCreature_LodTier);
extern "C" REX_FUNC(__imp__rex_lhAnim_WithinLodDistance);
extern "C" REX_FUNC(__imp__rex_CECCreatureGenerator_InCreateRange);
extern "C" REX_FUNC(__imp__rex_CECCreatureGenerator_OutOfDestroyRange);

namespace {

constexpr uint32_t kCameraBlockBytes = 64u;
constexpr uint32_t kPeerConeOffset = 64u;  // the cone (48 bytes) follows the block

/// The peer camera block as received (guest byte order), and whether one arrived this session.
std::mutex g_peer_mutex;
std::array<uint8_t, kCameraBlockBytes> g_peer_received{};
bool g_peer_have = false;
/// `g_peer_have`, set with it under the mutex, so a frame with no session and no peer view to
/// clear takes no lock.
std::atomic<bool> g_peer_have_hint{false};

/// 16-aligned guest buffer: the block the getter hands out, then the cone built from it.
uint32_t g_peer_guest = 0;
uint32_t g_peer_built_frame = 0xFFFFFFFFu;

/// Set only around the guest calls that ask for the peer view.
thread_local bool t_peer_view = false;

std::atomic<bool> g_said_peer_view{false};
std::atomic<bool> g_said_generators{false};

float LoadF32(uint8_t* base, uint32_t at) {
  const uint32_t bits = refii::vfs::LoadGuestU32(base, at);
  float value;
  std::memcpy(&value, &bits, sizeof(value));
  return value;
}

void StoreF32(uint8_t* base, uint32_t at, float value) {
  uint32_t bits;
  std::memcpy(&bits, &value, sizeof(bits));
  refii::vfs::StoreGuestU32(base, at, bits);
}

uint32_t NetManager(uint8_t* base) {
  const uint32_t root = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = Plausible(root) ? refii::vfs::LoadGuestU32(base, root + 0xCu) : 0u;
  return Plausible(game) ? refii::vfs::LoadGuestU32(base, game + 0x8Cu) : 0u;
}

/// The session host (net +0x34 in a session, +0x35 the host). Clears the peer view once the
/// session is gone, so the next session starts without one.
bool IsSessionHost(uint8_t* base) {
  const uint32_t net = NetManager(base);
  const bool session = Plausible(net) && refii::vfs::LoadGuestU8(base, net + 0x34u) != 0u;
  if (!session && g_peer_have_hint.load(std::memory_order_acquire)) {
    std::lock_guard<std::mutex> lock(g_peer_mutex);
    g_peer_have = false;
    g_peer_have_hint.store(false, std::memory_order_release);
  }
  return session && refii::vfs::LoadGuestU8(base, net + 0x35u) != 0u;
}

/// The peer view's cone, built this frame, or 0 when there is no peer view.
uint32_t PeerCone(const PPCContext& ctx, uint8_t* base) {
  uint32_t split_manager = 0u;
  if (RefiiCoopSplitScreen() != 0) {
    split_manager = ManagerForKey(base, 2u);
  }
  bool online = false;
  if (split_manager == 0u && IsSessionHost(base)) {
    std::lock_guard<std::mutex> lock(g_peer_mutex);
    online = g_peer_have;
  }
  if (split_manager == 0u && !online) {
    return 0u;
  }

  if (g_peer_guest == 0u) {
    static PPCFunc* const alloc = refii::vfs::FindGuestFunction(refii::guest::Address(0x8221F258u));
    if (alloc == nullptr) {
      return 0u;
    }
    PPCContext call = ctx;
    call.r3.u64 = kCameraBlockBytes + 48u + 16u;
    alloc(call, base);
    if (!Plausible(call.r3.u32)) {
      return 0u;
    }
    g_peer_guest = (call.r3.u32 + 15u) & ~15u;
  }

  const uint32_t frame = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83319650u));
  if (frame != g_peer_built_frame) {
    static PPCFunc* const build_cone = refii::vfs::FindGuestFunction(refii::guest::Address(0x82294468u));
    if (build_cone == nullptr) {
      return 0u;
    }
    if (split_manager != 0u) {
      std::memmove(base + g_peer_guest, base + split_manager + 0x90u, kCameraBlockBytes);
    } else {
      std::lock_guard<std::mutex> lock(g_peer_mutex);
      std::memcpy(base + g_peer_guest, g_peer_received.data(), kCameraBlockBytes);
    }
    PPCContext call = ctx;
    call.r3.u32 = g_peer_guest + kPeerConeOffset;
    t_peer_view = true;
    build_cone(call, base);
    t_peer_view = false;
    g_peer_built_frame = frame;
    Once(g_said_peer_view, "the host simulates around both players (the peer's camera view)");
  }
  return g_peer_guest + kPeerConeOffset;
}

/// Squared distance from player two's hero to `entity`, or -1 when either has no position.
float PlayerTwoDistanceSquared(const PPCContext& ctx, uint8_t* base, uint32_t entity) {
  const uint32_t two = refii_mod_player_hero(base, 2);
  if (!Plausible(two) || (refii::vfs::LoadGuestU8(base, two + 144u) & 0x40u) == 0u || !Plausible(entity)) {
    return -1.0f;
  }
  float a[3];
  float b[3];
  PPCContext work = ctx;
  if (refii_mod_entity_position(&work, base, two, a) == 0) {
    return -1.0f;
  }
  work = ctx;
  if (refii_mod_entity_position(&work, base, entity, b) == 0) {
    return -1.0f;
  }
  const float dx = a[0] - b[0];
  const float dy = a[1] - b[1];
  const float dz = a[2] - b[2];
  return dx * dx + dy * dy + dz * dz;
}

}  // namespace

/// Only the online joiner sends its camera (true skips the send).
bool RefiiCoopNoCameraSend() {
  uint8_t* base = RefiiGuestBase();
  return base == nullptr || !IsJoiner(base);
}

/// A received 0xBD: kept as the peer view, never applied.
void RefiiCoopNoCameraReceive(PPCRegister& r1) {
  uint8_t* base = RefiiGuestBase();
  if (base == nullptr) {
    return;
  }
  std::lock_guard<std::mutex> lock(g_peer_mutex);
  std::memcpy(g_peer_received.data(), base + r1.u32 + 0x860u, kCameraBlockBytes);
  g_peer_have = true;
  g_peer_have_hint.store(true, std::memory_order_release);
}

// PLAYER TWO'S SCREEN. "On screen" for the guest is player one's camera: `sub_821DD3B0(out,
// manager)` copies the manager's block (`sub_82265F90`) and the screen tests, the dog's
// off-screen teleport (`sub_82811A70`) and IsEntity/PositionVisibleOnScreen take it from
// `sub_821F87C8`'s manager. While code runs for player two (their dog), the block is player
// two's view — the same block the host keeps for the free-roam peer view (split screen: player
// two's manager; online: the joiner's streamed camera). Without one, the shipped block.
namespace {
thread_local int t_partner_screen = 0;
}  // namespace

extern "C" void RefiiCoopPartnerScreen(int on) { t_partner_screen += on != 0 ? 1 : -1; }

extern "C" REX_FUNC(__imp__rex_lhCamera_ScreenBlock);

REX_EXTERN(rex_lhCamera_ScreenBlock) {
  if (t_partner_screen > 0 && PeerCone(ctx, base) != 0u && g_peer_guest != 0u) {
    std::memmove(base + ctx.r3.u32, base + g_peer_guest, kCameraBlockBytes);
    return;
  }
  __imp__rex_lhCamera_ScreenBlock(ctx, base);
}

// The cached block (sub_82211598 / disc sub_822116D0: one copy per tick, stamp dword_8331906C ->
// unk_8331A230, read by the AI's sight and visibility filters, the LOD and the script frustum tests)
// answers player two's view for code running for player two too, and never caches it.
REX_EXTERN(rex_lhCamera_CurrentBlock) {
  if ((t_peer_view || (t_partner_screen > 0 && PeerCone(ctx, base) != 0u)) && g_peer_guest != 0u) {
    ctx.r3.u64 = g_peer_guest;
    return;
  }
  __imp__rex_lhCamera_CurrentBlock(ctx, base);
}

/// How deep the calling thread is in code running for player two (RefiiCoopPartnerScreen).
extern "C" int RefiiCoopPartnerScreenDepth(void) { return t_partner_screen; }

/// 1 while player two's view is on this machine: their own camera manager on split screen, or, on an
/// online host, the joiner's camera as received (the peer view).
extern "C" int RefiiCoopPartnerViewHere(void) {
  uint8_t* base = RefiiGuestBase();
  if (base == nullptr) {
    return 0;
  }
  if (RefiiCoopSplitScreen() != 0 && ManagerForKey(base, 2u) != 0u) {
    return 1;
  }
  if (!IsSessionHost(base)) {
    return 0;
  }
  std::lock_guard<std::mutex> lock(g_peer_mutex);
  return g_peer_have ? 1 : 0;
}

/// Player two's camera position as the block their camera's getter last built (split screen: their
/// manager's +0x90) or as the joiner stated it (online host: the received 0xBD block, +0); guest axes.
bool PartnerCameraPosition(uint8_t* base, float* out) {
  if (RefiiCoopSplitScreen() != 0) {
    const uint32_t manager = ManagerForKey(base, 2u);
    if (manager == 0u) {
      return false;
    }
    for (uint32_t i = 0; i < 3u; ++i) {
      out[i] = LoadF32(base, manager + 0x90u + i * 4u);
    }
    return true;
  }
  if (!IsSessionHost(base)) {
    return false;
  }
  std::lock_guard<std::mutex> lock(g_peer_mutex);
  if (!g_peer_have) {
    return false;
  }
  for (uint32_t i = 0; i < 3u; ++i) {
    uint32_t bits;
    std::memcpy(&bits, g_peer_received.data() + i * 4u, sizeof(bits));
    bits = __builtin_bswap32(bits);
    std::memcpy(&out[i], &bits, sizeof(bits));
  }
  return true;
}

/// Player two's camera holder (their Player+4) while split screen is in effect, once the game's camera
/// update has run for its manager (holder+4) since the Player constructor built it; else 0. Before that
/// lhCamera_ScreenBlock on it answers the constructed block at the origin. The second listener and the
/// particles' second view (coop_particle_view.cpp) build player two's block from it.
extern "C" uint32_t RefiiCoopSplitPartnerCameraHolder(uint8_t* base) {
  if (base == nullptr || RefiiCoopSplitScreen() == 0) {
    return 0u;
  }
  const uint32_t player = PlayerForKey(base, 2u);
  const uint32_t holder = player != 0u ? refii::vfs::LoadGuestU32(base, player + 4u) : 0u;
  const uint32_t manager = Plausible(holder) ? refii::vfs::LoadGuestU32(base, holder + 4u) : 0u;
  if (!Plausible(manager) || manager != g_second_manager_updated.load(std::memory_order_acquire)) {
    return 0u;
  }
  return holder;
}

// EACH SOUND COMES FROM THE SIDE ITS NEARER PLAYER SEES IT ON (split screen).
// The mixer has ONE X3DAUDIO_LISTENER for panning, mixer+0x84 (front +0, top +12, position +24,
// velocity +36, cone +48; 52 bytes): every world voice's speaker matrix is
// `X3DAudioCalculate(mixer+0x70, mixer+0x84, voice+0x190, 1, voice+0x1F4)` in the voice update
// (TU1 `sub_82C58B00` 0x82C58E58 / disc `sub_82C52A80` 0x82C52DD8; a head-relative voice,
// voice+0x260, takes the fixed listener mixer+0xB8 instead: `addi r4, r11, 0xB8` 0x82C58E4C, else
// `addi r4, r11, 0x84` 0x82C58E54 / disc 0x82C52DCC, 0x82C52DD4). The emitter's volume curve is
// flat (voice+0x1D8 = mixer+0x104: two points, both 1.0 — `sub_82C59E10` 0x82C59F94,
// `sub_82C5AB80`), so that call decides the SIDE only; loudness is the distance test above.
// The listener is the game's statement, once a frame: `sub_822A8B00(audio, position, front, top,
// 1/sin(hfov/2))` at 0x821DC3A8 (disc `sub_822A88C0` at 0x821DBF08), fed from
// `lhCamera_ScreenBlock(block, sub_821F87C8(...))` (0x821DC34C..0x821DC360) — block +0 position,
// +0x10 top, +0x20 front — which is player one's camera whatever is drawn; it reaches the mixer
// unchanged (`sub_82C57D60`, mixer vtable slot 7: front 0x84, top 0x90, position 0x9C).
// Split screen: right after the game states player one's listener, the same getter is run for
// player two's camera holder (Player+4) and kept as a second listener — only once the game's
// camera update has run for player two's manager (holder+4) since the Player constructor built
// it; before that the getter returns the constructed block at the origin. A world voice whose
// emitter (the call's own argument, emitter +28) is nearer to that camera than to the listener the
// call was given is calculated against it. Two speakers cannot give each half its own; the nearer
// player's view is the one the sound belongs to. Shared screen and online keep the one listener.
extern "C" REX_FUNC(__imp__rex_lhAudio_SetListener);

namespace {

constexpr uint32_t kListenerBytes = 52u;      // X3DAUDIO_LISTENER
constexpr uint32_t kListenerTwoOffset = 64u;  // after the camera block the getter fills

/// Player two's camera as last stated on the game thread, and the copy the mixer thread keeps in
/// the guest listener.
struct SecondListener {
  std::mutex mutex;
  float front[3] = {0.0f, 0.0f, 1.0f};
  float top[3] = {0.0f, 1.0f, 0.0f};
  float position[3] = {0.0f, 0.0f, 0.0f};
  uint32_t stated = 0;   // bumped per statement
  uint32_t written = 0;  // the statement the guest listener holds
};
SecondListener g_second_listener;

/// 16-aligned guest buffer: the camera block (64 bytes), then the second X3DAUDIO_LISTENER.
std::atomic<uint32_t> g_second_listener_guest{0};
std::atomic<bool> g_second_listener_live{false};
std::atomic<bool> g_said_second_listener{false};

void LoadVec3(uint8_t* base, uint32_t at, float* out) {
  for (uint32_t i = 0; i < 3u; ++i) {
    out[i] = LoadF32(base, at + i * 4u);
  }
}

void StoreVec3(uint8_t* base, uint32_t at, const float* in) {
  for (uint32_t i = 0; i < 3u; ++i) {
    StoreF32(base, at + i * 4u, in[i]);
  }
}

float ListenerDistanceSquared(const float* a, const float* b) {
  const float dx = a[0] - b[0];
  const float dy = a[1] - b[1];
  const float dz = a[2] - b[2];
  return dx * dx + dy * dy + dz * dz;
}

/// The emitter belongs to the second listener only when strictly nearer to it; NaN keeps the first.
bool NearerToSecond(const float* emitter, const float* first, const float* second) {
  return ListenerDistanceSquared(emitter, second) < ListenerDistanceSquared(emitter, first);
}

}  // namespace

// The game's listener statement (r3 audio; v1 position, v2 front, v3 top, f1 the zoom scale).
REX_EXTERN(rex_lhAudio_SetListener) {
  const PPCContext entry = ctx;
  __imp__rex_lhAudio_SetListener(ctx, base);

  const uint32_t holder = RefiiCoopSplitPartnerCameraHolder(base);
  if (holder == 0u) {
    g_second_listener_live.store(false, std::memory_order_release);
    return;
  }

  uint32_t guest = g_second_listener_guest.load(std::memory_order_acquire);
  if (guest == 0u) {
    static PPCFunc* const alloc = refii::vfs::FindGuestFunction(refii::guest::Address(0x8221F258u));
    if (alloc == nullptr) {
      return;
    }
    PPCContext call = entry;
    call.r3.u64 = kListenerTwoOffset + 64u + 16u;
    alloc(call, base);
    if (!Plausible(call.r3.u32)) {
      return;
    }
    guest = (call.r3.u32 + 15u) & ~15u;
    std::memset(base + guest, 0, kListenerTwoOffset + kListenerBytes);
    g_second_listener_guest.store(guest, std::memory_order_release);
  }

  PPCContext call = entry;
  call.r3.u32 = guest;
  call.r4.u32 = holder;
  __imp__rex_lhCamera_ScreenBlock(call, base);

  {
    std::lock_guard<std::mutex> lock(g_second_listener.mutex);
    LoadVec3(base, guest + 0x00u, g_second_listener.position);
    LoadVec3(base, guest + 0x10u, g_second_listener.top);
    LoadVec3(base, guest + 0x20u, g_second_listener.front);
    ++g_second_listener.stated;
  }
  g_second_listener_live.store(true, std::memory_order_release);
}

// Before `addi r4, r11, 0x84` (r11 the mixer, r5 the emitter): true = r4 is the second listener and
// the instruction is skipped; false = the shipped listener.
bool RefiiCoopListenerForSound(PPCRegister& r4, PPCRegister& r5, PPCRegister& r11) {
  if (!g_second_listener_live.load(std::memory_order_acquire) || RefiiCoopSplitScreen() == 0) {
    return false;
  }
  uint8_t* base = RefiiGuestBase();
  const uint32_t guest = g_second_listener_guest.load(std::memory_order_acquire);
  if (base == nullptr || guest == 0u || !Plausible(r5.u32) || !Plausible(r11.u32)) {
    return false;
  }
  const uint32_t two = guest + kListenerTwoOffset;
  float second[3];
  {
    std::lock_guard<std::mutex> lock(g_second_listener.mutex);
    if (g_second_listener.written != g_second_listener.stated) {
      StoreVec3(base, two + 0u, g_second_listener.front);
      StoreVec3(base, two + 12u, g_second_listener.top);
      StoreVec3(base, two + 24u, g_second_listener.position);
      g_second_listener.written = g_second_listener.stated;
    }
    std::memcpy(second, g_second_listener.position, sizeof(second));
  }
  float emitter[3];
  float first[3];
  LoadVec3(base, r5.u32 + 28u, emitter);
  LoadVec3(base, r11.u32 + 0x84u + 24u, first);
  if (!NearerToSecond(emitter, first, second)) {
    return false;
  }
  r4.u64 = two;
  if (!g_said_second_listener.exchange(true, std::memory_order_relaxed)) {
    REXLOG_INFO("Refii co-op heroes: a sound nearer player two is panned from player two's camera "
                "(sound {:.1f} {:.1f} {:.1f}; camera one {:.1f} {:.1f} {:.1f}; camera two {:.1f} {:.1f} {:.1f})",
                emitter[0], emitter[1], emitter[2], first[0], first[1], first[2], second[0], second[1],
                second[2]);
  }
  return true;
}

REX_EXTERN(rex_lhVisibility_Refresh) {
  using refii::vfs::LoadGuestU32;
  using refii::vfs::StoreGuestU32;
  const PPCContext entry = ctx;
  const uint32_t comp = ctx.r3.u32;
  const uint32_t view = ctx.r4.u32;
  __imp__rex_lhVisibility_Refresh(ctx, base);
  if (view != 0u || !Plausible(comp)) {
    return;
  }
  const uint32_t frame = LoadGuestU32(base, refii::guest::Address(0x83319650u));
  if (LoadGuestU32(base, comp + 232u) != frame) {
    return;  // not refreshed this frame: the stored answer already holds both views
  }
  const uint32_t cone = PeerCone(entry, base);
  if (cone == 0u) {
    return;
  }
  const float local_distance = LoadF32(base, comp + 104u);
  const uint8_t local_visible = refii::vfs::LoadGuestU8(base, comp + 203u);
  StoreGuestU32(base, comp + 100u, frame - 1u);  // the refresh runs once per frame...
  StoreGuestU32(base, comp + 232u, frame - 6u);  // ...and staggers unless 5 frames passed
  PPCContext peer = entry;
  peer.r4.u32 = cone;
  __imp__rex_lhVisibility_Refresh(peer, base);
  const float peer_distance = LoadF32(base, comp + 104u);
  const uint8_t peer_visible = refii::vfs::LoadGuestU8(base, comp + 203u);
  StoreF32(base, comp + 104u, peer_distance < local_distance ? peer_distance : local_distance);
  refii::vfs::StoreGuestU8(base, comp + 203u, (local_visible != 0u || peer_visible != 0u) ? 1u : 0u);
  StoreGuestU32(base, comp + 100u, frame);
  StoreGuestU32(base, comp + 232u, frame);
}

REX_EXTERN(rex_lhCreature_LodTier) {
  const PPCContext entry = ctx;
  __imp__rex_lhCreature_LodTier(ctx, base);
  if (ctx.r3.u32 == 0u || PeerCone(entry, base) == 0u) {
    return;
  }
  PPCContext peer = entry;
  t_peer_view = true;
  __imp__rex_lhCreature_LodTier(peer, base);
  t_peer_view = false;
  if (peer.r3.u32 < ctx.r3.u32) {
    ctx.r3.u64 = peer.r3.u32;
  }
}

REX_EXTERN(rex_lhAnim_WithinLodDistance) {
  const PPCContext entry = ctx;
  __imp__rex_lhAnim_WithinLodDistance(ctx, base);
  if ((ctx.r3.u32 & 0xFFu) != 0u || PeerCone(entry, base) == 0u) {
    return;
  }
  PPCContext peer = entry;
  t_peer_view = true;
  __imp__rex_lhAnim_WithinLodDistance(peer, base);
  t_peer_view = false;
  if ((peer.r3.u32 & 0xFFu) != 0u) {
    ctx.r3.u64 = 1u;
  }
}

REX_EXTERN(rex_CECCreatureGenerator_InCreateRange) {
  const PPCContext entry = ctx;
  const uint32_t generator = ctx.r3.u32;
  __imp__rex_CECCreatureGenerator_InCreateRange(ctx, base);
  if ((ctx.r3.u32 & 0xFFu) != 0u || !Plausible(generator) ||
      refii::vfs::LoadGuestU8(base, generator + 63u) == 0u) {
    return;
  }
  const float d2 = PlayerTwoDistanceSquared(entry, base, refii::vfs::LoadGuestU32(base, generator + 4u));
  const float radius = LoadF32(base, generator + 64u);
  if (d2 >= 0.0f && d2 < radius * radius) {
    ctx.r3.u64 = 1u;
    Once(g_said_generators, "creature generators answer either player");
  }
}

REX_EXTERN(rex_CECCreatureGenerator_OutOfDestroyRange) {
  const PPCContext entry = ctx;
  const uint32_t generator = ctx.r3.u32;
  __imp__rex_CECCreatureGenerator_OutOfDestroyRange(ctx, base);
  if ((ctx.r3.u32 & 0xFFu) == 0u || !Plausible(generator)) {
    return;
  }
  const float d2 = PlayerTwoDistanceSquared(entry, base, refii::vfs::LoadGuestU32(base, generator + 4u));
  const float radius = LoadF32(base, generator + 68u);
  if (d2 >= 0.0f && d2 <= radius * radius) {
    ctx.r3.u64 = 0u;
  }
}

// ---------------------------------------------------------------------------------------------
// TRAVEL: a region exit waits for both players (Fable 3's partner-range rule).
//
//   The exit update (sub_821ED378 / disc sub_821ED1E8) returns at once on an online joiner
//   (net +0x34 && +0x36, 0x821ED39C..0x821ED3C8) and otherwise tests only the HERO
//   (game+0x58 → players → sub_8222C1B8, 0x821ED418..0x821ED428) against Radius (exit+0x24,
//   squared, 0x821ED498..0x821ED4C4); the level change then carries player two from wherever
//   they are, which free roam makes anywhere. Fable 3's exit (sub_829BD0A8) refuses while the
//   partner is out of range — a walk-in exit's partner must be inside its radius, an interaction
//   exit's within 30 (x and y, flt_8329E3D8) of the one who entered — and tells the one who
//   entered TEXT_TUTORIAL_EXIT_LEVEL_BLOCKED_BY_PLAYER_DISTANCE once per approach (latch +0x68).
//   The wrapper decides for its call; the hook before `clrlwi r8,r11,24` (0x821ED4D4 /
//   0x821ED344, r11 = the hero is inside Radius) clears r11 on a refusal, which leaves the
//   message-radius banner and the leave/re-enter test (both from the same f13) as shipped.
//   Player two walking into a walk-in exit alone is told on the machine they play on; the exit
//   fires only for the hero, so nothing else changes. Fable 2 ships no text for this: the host
//   shows its own notice (refii_bridge_note_exit_refused).

extern "C" REX_FUNC(__imp__rex_CECLevelExit_Update);
extern "C" REX_FUNC(__imp__rex_CECLevelExit_Fire);
extern "C" int RefiiCoopPeekExitUse(uint8_t* base, float* out_xyz);
extern "C" void RefiiCoopClearExitUse(void);
// mod_events.cpp: who asked for the travel a fire queues (the Travel mod event's `player`), and the
// player of the newest use of a thing.
extern "C" void RefiiModNoteTraveller(uint8_t* base, int32_t player);
extern "C" int32_t RefiiModPlayerWhoUsed(uint8_t* base, uint32_t thing);

namespace {

constexpr float kInteractPartnerRange = 30.0f;  // Fable 3's flt_8329E3D8

std::atomic<bool> g_said_exit{false};

/// Set by the exit-update wrapper for its call: this exit waits for player two.
bool g_exit_refused = false;

/// Set by the exit-update wrapper for its call: the player the exit tests when not the hero.
uint32_t g_exit_tester = 0u;

/// Set by the exit-update wrapper for its call: an online joiner runs this interaction exit (its
/// prompt, the hold, the partner rule) for its own player; the fire becomes a request.
bool g_joiner_runs_exit = false;

std::atomic<bool> g_said_joiner_exit{false};

/// Set by the exit-update wrapper for its call: the player the exit fires for when it fires
/// inside the update (the Travel event's asker), and that the update is running.
int32_t g_exit_fire_player = 0;
bool g_in_exit_update = false;

/// The frame an interaction exit last raised its prompt for player two (their hold is its own).
uint32_t g_exit_prompt_two_frame = 0xFFFFFFF0u;

/// Exits a player stands refused at, so each approach is told once. Cleared when they leave.
struct ExitLatch {
  uint32_t exit = 0u;
  uint8_t told = 0u;  // bit 0 player one, bit 1 player two
};
std::array<ExitLatch, 16> g_exit_latches{};

ExitLatch& LatchFor(uint32_t exit) {
  ExitLatch* empty = nullptr;
  for (ExitLatch& latch : g_exit_latches) {
    if (latch.exit == exit) {
      return latch;
    }
    if (empty == nullptr && latch.told == 0u) {
      empty = &latch;
    }
  }
  ExitLatch& slot = empty != nullptr ? *empty : g_exit_latches[0];
  slot = ExitLatch{exit, 0u};
  return slot;
}

/// Tells `player` once per approach; `standing` false ends the approach.
void Tell(uint32_t exit, int32_t player, bool standing, bool local) {
  const uint8_t bit = player == 1 ? 1u : 2u;
  ExitLatch& latch = LatchFor(exit);
  if (!standing) {
    latch.told &= static_cast<uint8_t>(~bit);
    return;
  }
  if ((latch.told & bit) == 0u) {
    latch.told |= bit;
    if (local) {
      refii_bridge_note_exit_refused(player);
    }
  }
}

/// Whether this machine's player stood waiting at any exit this frame, published when the next
/// frame begins (refii_bridge_set_exit_waiting).
struct Waiting {
  uint32_t frame = 0xFFFFFFFFu;
  bool now = false;
};
Waiting g_waiting;

void WaitingFrame(uint8_t* base) {
  const uint32_t frame = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83319650u));
  if (frame != g_waiting.frame) {
    refii_bridge_set_exit_waiting(g_waiting.now ? 1 : 0);
    g_waiting.frame = frame;
    g_waiting.now = false;
  }
}

void NoteWaiting(uint8_t* base) {
  WaitingFrame(base);
  g_waiting.now = true;
}

float DistanceSquared3(const float* a, const float* b) {
  const float dx = a[0] - b[0];
  const float dy = a[1] - b[1];
  const float dz = a[2] - b[2];
  return dx * dx + dy * dy + dz * dz;
}

/// Whether this exit waits for player two this tick, and the notices that go with it.
bool ExitRefused(PPCContext& ctx, uint8_t* base, uint32_t exit) {
  const uint32_t root = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = Plausible(root) ? refii::vfs::LoadGuestU32(base, root + 0xCu) : 0u;
  const uint32_t mode = Plausible(game) ? refii::vfs::LoadGuestU32(base, game + 0x11Cu) : 0u;
  if (mode == 0u || !Plausible(exit)) {
    return false;  // solo
  }
  const uint32_t hero = refii_mod_player_hero(base, 1);
  const uint32_t partner = refii_mod_player_hero(base, 2);
  const uint32_t entity = refii::vfs::LoadGuestU32(base, exit + 4u);
  if (!Plausible(hero) || !Plausible(partner) || !Plausible(entity) ||
      refii::vfs::LoadGuestU8(base, exit + 0x37u) == 0u) {
    return false;  // no partner, or the exit is inactive
  }

  float at[3], one[3], two[3];
  if (refii_mod_entity_position(&ctx, base, entity, at) == 0 ||
      refii_mod_entity_position(&ctx, base, hero, one) == 0 ||
      refii_mod_entity_position(&ctx, base, partner, two) == 0) {
    return false;
  }
  const uint32_t radius_bits = refii::vfs::LoadGuestU32(base, exit + 0x24u);
  float radius = 0.0f;
  std::memcpy(&radius, &radius_bits, sizeof(radius));
  const float reach = radius * radius;
  const bool interaction = refii::vfs::LoadGuestU8(base, exit + 0x3Bu) != 0u ||
                           refii::vfs::LoadGuestU8(base, exit + 0x3Cu) != 0u;

  const bool hero_in = DistanceSquared3(one, at) < reach;
  const bool partner_in = DistanceSquared3(two, at) < reach;

  // An interaction exit or dive spot answers the NEARER player in local co-op (Fable 3: either
  // player takes an exit): the update tests that player (RefiiCoopExitTester), raises its hold
  // prompt for them, and a dive queues on their own action manager (component 49, read off the
  // tested player at 0x821EDD20). A walk-in exit tests the hero as shipped; both must stand in it.
  const bool dive = refii::vfs::LoadGuestU8(base, exit + 0x3Bu) != 0u;
  const bool joiner = IsJoiner(base);
  const bool two_can = !dive || refii_mod_component(base, partner, 49u) != 0u;
  // An online joiner's own player is player two; their machine runs the exit for them.
  g_joiner_runs_exit = joiner && mode == 1u && interaction && two_can;
  const bool two_tests = g_joiner_runs_exit ||
                         (interaction && mode == 2u && two_can &&
                          DistanceSquared3(two, at) < DistanceSquared3(one, at));
  const float* tester = two_tests ? two : one;
  const float* other = two_tests ? one : two;
  const bool tester_in = two_tests ? partner_in : hero_in;
  const float dx = other[0] - tester[0];
  const float dy = other[1] - tester[1];
  const bool partner_ok = interaction
                              ? dx * dx + dy * dy < kInteractPartnerRange * kInteractPartnerRange
                              : partner_in;

  // Player one is this machine's player off the joiner; player two is the joiner's, or local
  // player two in local co-op (mode 2).
  const bool refused = tester_in && !partner_ok;
  const bool partner_alone = !interaction && partner_in && !hero_in;
  g_exit_tester = two_tests ? partner : 0u;
  if (two_tests && tester_in && !refused) {
    g_exit_prompt_two_frame = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83319650u));
  }
  Tell(exit, two_tests ? 2 : 1, refused, two_tests ? true : !joiner);
  Tell(exit, two_tests ? 1 : 2, partner_alone, joiner || mode == 2u);
  // Online, this machine's player waiting at an exit is restated to the other machine (Fable 3's
  // net message 0x141 and its _REMOTE text): the host's player is one, the joiner's two.
  if (mode == 1u && (joiner ? partner_alone || (g_joiner_runs_exit && refused) : refused)) {
    NoteWaiting(base);
  }
  if (refused) {
    Once(g_said_exit, "a region exit waits for both players");
  }
  return refused;
}

/// The host's half of the joiner's request: the exit standing within a metre of the requested
/// position is marked interacted (+0x3D, what its Lua `Interact` sets), so this update fires it
/// at its top (0x821ED3EC). The joiner already applied the partner rule, as Fable 3's host trusts
/// its client's (`sub_829BF5F8` re-checks only the region lock, which the update keeps).
bool FireRequestedExit(PPCContext& ctx, uint8_t* base, uint32_t exit) {
  float wanted[3];
  if (!Plausible(exit) || RefiiCoopPeekExitUse(base, wanted) == 0) {
    return false;
  }
  const uint32_t entity = refii::vfs::LoadGuestU32(base, exit + 4u);
  float at[3];
  if (!Plausible(entity) || refii_mod_entity_position(&ctx, base, entity, at) == 0 ||
      DistanceSquared3(at, wanted) > 1.0f) {
    return false;
  }
  refii::vfs::StoreGuestU8(base, exit + 0x3Du, 1u);
  RefiiCoopClearExitUse();
  REXLOG_INFO("Refii co-op heroes: the joiner's exit at ({:.1f}, {:.1f}, {:.1f}) fires on the host",
              at[0], at[1], at[2]);
  return true;
}

/// The player an exit fires for inside its update (0x821ED3CC..0x821ED400): +0x3E set queues the
/// level change itself (`sub_8283C7F8`, no fire); +0x3D set — what its Lua `Interact` or the
/// joiner's request sets — fires at once for whoever used it; otherwise the walk-in test fires
/// for the player it tested (the hero, or player two by the travel rule).
int32_t ExitFirePlayer(uint8_t* base, uint32_t exit, bool joiner_request) {
  if (joiner_request) {
    return 2;
  }
  if (refii::vfs::LoadGuestU8(base, exit + 0x3Du) != 0u) {
    return RefiiModPlayerWhoUsed(base, refii::vfs::LoadGuestU32(base, exit + 4u));
  }
  return g_exit_tester != 0u ? 2 : 1;
}

}  // namespace

void RefiiCoopJoinerRunsExit(PPCRegister& r11) {
  if (g_joiner_runs_exit) {
    r11.u64 = 0u;
  }
}

void RefiiCoopExitTester(PPCRegister& r3) {
  if (g_exit_tester != 0u) {
    r3.u64 = g_exit_tester;
  }
}

void RefiiCoopExitWaitsForPartner(PPCRegister& r11) {
  if (g_exit_refused) {
    r11.u64 = 0u;
  }
}

// The level exit's update (r3 the CECLevelExit component).
REX_EXTERN(rex_CECLevelExit_Update) {
  WaitingFrame(base);
  const uint32_t exit = ctx.r3.u32;
  const bool previous = g_exit_refused;
  const uint32_t previous_tester = g_exit_tester;
  const bool previous_joiner = g_joiner_runs_exit;
  const int32_t previous_fire_player = g_exit_fire_player;
  const bool previous_in_update = g_in_exit_update;
  g_exit_tester = 0u;
  g_joiner_runs_exit = false;
  g_exit_refused = ExitRefused(ctx, base, exit);
  const bool requested = !IsJoiner(base) && FireRequestedExit(ctx, base, exit);
  g_exit_fire_player = Plausible(exit) ? ExitFirePlayer(base, exit, requested) : 0;
  g_in_exit_update = true;
  __imp__rex_CECLevelExit_Update(ctx, base);
  g_exit_refused = previous;
  g_exit_tester = previous_tester;
  g_joiner_runs_exit = previous_joiner;
  g_exit_fire_player = previous_fire_player;
  g_in_exit_update = previous_in_update;
}

// The level exit's fire (r3 the CECLevelExit component). On an online joiner it is a request:
// Fable 2 never fires an exit there (the update returns at its joiner test), and Fable 3's client
// sends net message 0x145 for the host to fire. Here the exit's position goes to the host on
// our channel (refii_bridge_set_exit_request) and the host's update fires the exit standing there.
// The asker of the travel a fire queues is noted for the Travel event: inside the update, the
// player it fires for; outside it (the interact action's `sub_8283B928`, the exit's message
// handler `sub_82849870`), the exit's newest user, or none.
REX_EXTERN(rex_CECLevelExit_Fire) {
  const uint32_t exit = ctx.r3.u32;
  const uint32_t entity = Plausible(exit) ? refii::vfs::LoadGuestU32(base, exit + 4u) : 0u;
  if (!IsJoiner(base)) {
    RefiiModNoteTraveller(base, g_in_exit_update ? g_exit_fire_player : RefiiModPlayerWhoUsed(base, entity));
    __imp__rex_CECLevelExit_Fire(ctx, base);
    return;
  }
  float at[3];
  if (Plausible(entity) && refii_mod_entity_position(&ctx, base, entity, at) != 0) {
    refii_bridge_set_exit_request(at[0], at[1], at[2]);
    RefiiModNoteTraveller(base, 2);
    Once(g_said_joiner_exit, "the joiner's exit use goes to the host");
  }
}

// ---------------------------------------------------------------------------------------------
// INTERACTION: the world's use prompts and hold-to-use answer player two too.
//
//   Pressing A on a thing is per player already: CAbilityActionPlayerInteract (sub_82975BD0 /
//   sub_82976588) takes the acting creature's OWN targeting (component 77) and runs the use for
//   it. Three single-player ties stand around it:
//   - PROMPT: a usable thing's update (CECOnActionUse, sub_82821E88 / disc sub_82753908) shows
//     its world icon only from the HERO (`bl sub_8222C1B8; mr r31,r3` at 0x8282251C/0x82822520 /
//     0x82753F9C/0x82753FA0; r31 then runs the player's checks and the WorldIconRadius test at
//     0x828226B0). The hook before that `mr` hands it the NEARER player, so a thing by player two
//     prompts for player two. Player two is taken only when they carry component 33, the
//     player's checks' own (0x82822550..0x8282267C); without it the hero stands.
//   - HOLD INPUT: the prompt manager's A handler (sub_823DD318 / disc sub_823B6A00) counts only
//     player record 1's pad (r31, 0x823DD3A8..0x823DD3B8) and SWALLOWS every other A while a
//     hold prompt is up — player two could not swing a sword while player one stood at a chest.
//     The hook before `li r11,1` (0x823DD42C / 0x823B6B14) takes player two's press as the hold's
//     when player two's own target is a hold-to-use thing (its component 78 byte +0x1A, what the
//     completion below requires), and otherwise returns 0 (0x823DD338 / 0x823B6A20): not consumed,
//     so the press reaches player two's own controls.
//   - HOLD USE: a full hold (sub_821C5BB0 / disc sub_821C5C20) uses the HERO's target as the hero
//     (`mr r31,r3` at 0x821C6018 / 0x821C6088). The hook there hands it the player who held, so
//     the use is their target's and runs for them. Every step after is null-safe.
//   - THE VIEWER WHOLE: the update fetches "the hero" three times (`sub_8222C1B8`: the witness
//     refresh 0x828222A0, the main path 0x8282251C, the door test inside `sub_82582D00`). Hero one's
//     slot is lent to the viewer for the whole update, so all three answer the same player.
//   - TEXT FOLLOWS THE VIEWER: the text and type are fixed when the prompt first shows (the update
//     returns at 0x828226CC once +0x18 is set); when the nearer player changes and stays changed,
//     the prompt is hidden (`sub_82823498`) and asked again for them.
//   - THE OTHER PLAYER'S TEXT (local co-op): the update runs a second, dry pass for the farther
//     player against their own copy of its per-viewer bytes (+0x18 shown, +0x1A hold, +0x1B
//     witnessed, +0x1C re-check time — the only bytes it writes), with their hero lent; its request,
//     hide, witness write and tutorial hints are diverted, so the manager's one prompt per thing
//     stays the nearer player's, and the text the other player would be shown ("Steal" where the
//     nearer owns the house, "Knock" at a door only one may open) is drawn in their half over it.
//   - HOLD PROMPT: the manager keeps ONE hold prompt (a type-35 request is filed at manager+64,
//     `sub_823DDF90` 0x823DE0A0, and drawn on the single FillWorldIcon element). In local co-op a
//     hold prompt asked for player two is kept here instead (the request `sub_823DD730`), so each
//     player has their own; the host draws player two's in their half.

extern "C" REX_FUNC(__imp__rex_CECOnActionUse_Update);
extern "C" REX_FUNC(__imp__rex_lhPromptManager_HoldInput);
extern "C" REX_FUNC(__imp__rex_lhPromptManager_Update);
extern "C" void refii_bridge_world_icons_step(uint64_t step);
extern "C" REX_FUNC(__imp__rex_lhPromptManager_Request);
extern "C" REX_FUNC(__imp__rex_CECOnActionUse_Hide);
extern "C" REX_FUNC(__imp__rex_lhPromptManager_Witnessed);
extern "C" REX_FUNC(__imp__rex_lhTutorials_Display);

namespace {

std::atomic<bool> g_said_prompt{false};
std::atomic<bool> g_said_hold{false};

/// The player a usable thing's update tests, for its call (0: the hero, as shipped).
uint32_t g_use_viewer = 0u;

/// For the hold handler's call: player two's own target is a hold-to-use thing.
bool g_two_may_hold = false;

/// Who holds A on the hold prompt now: 1 or 2.
int32_t g_hold_player = 1;

/// For the prompt manager's update: the creature a completed hold uses as (0: the hero).
uint32_t g_hold_creature = 0u;

/// Both players' positions, once a frame (the frame counter the use update itself reads).
struct PlayerSpots {
  uint32_t frame = 0xFFFFFFFFu;
  uint32_t one = 0u, two = 0u;
  float a[3] = {}, b[3] = {};
  bool valid = false;
};
PlayerSpots g_spots;

bool CoopGame(uint8_t* base) {
  const uint32_t root = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = Plausible(root) ? refii::vfs::LoadGuestU32(base, root + 0xCu) : 0u;
  return Plausible(game) && refii::vfs::LoadGuestU32(base, game + 0x11Cu) != 0u;
}

const PlayerSpots& Spots(PPCContext& ctx, uint8_t* base) {
  const uint32_t frame = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83319650u));
  if (frame != g_spots.frame) {
    g_spots.frame = frame;
    g_spots.one = refii_mod_player_hero(base, 1);
    g_spots.two = refii_mod_player_hero(base, 2);
    g_spots.valid = Plausible(g_spots.one) && Plausible(g_spots.two) &&
                    refii_mod_entity_position(&ctx, base, g_spots.one, g_spots.a) != 0 &&
                    refii_mod_entity_position(&ctx, base, g_spots.two, g_spots.b) != 0;
  }
  return g_spots;
}

/// The player a usable thing prompts for, when it is not the hero: local co-op, player two when
/// they stand nearer `thing`; an online joiner, always player two (this machine's player — the
/// shipped joiner was prompted only near the host's hero); an online host, the hero as shipped.
/// Player two only when they can run the player's checks (component 33).
uint32_t NearerPlayerTwo(PPCContext& ctx, uint8_t* base, uint32_t thing) {
  if (!Plausible(thing) || !CoopGame(base)) {
    return 0u;
  }
  const uint32_t root = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = Plausible(root) ? refii::vfs::LoadGuestU32(base, root + 0xCu) : 0u;
  const uint32_t mode = Plausible(game) ? refii::vfs::LoadGuestU32(base, game + 0x11Cu) : 0u;
  if (mode == 1u) {
    const uint32_t two = IsJoiner(base) ? refii_mod_player_hero(base, 2) : 0u;
    return Plausible(two) && refii_mod_component(base, two, 33u) != 0u ? two : 0u;
  }
  const PlayerSpots& spots = Spots(ctx, base);
  float at[3];
  if (!spots.valid || refii_mod_component(base, spots.two, 33u) == 0u ||
      refii_mod_entity_position(&ctx, base, thing, at) == 0) {
    return 0u;
  }
  return DistanceSquared3(spots.b, at) < DistanceSquared3(spots.a, at) ? spots.two : 0u;
}

/// Whether player two's own targeting (component 77, vfunc +0x44) holds a live thing whose
/// use is hold-to-use (component 78 byte +0x1A) — what a full hold's use requires.
bool PlayerTwoTargetsHoldUse(PPCContext& ctx, uint8_t* base) {
  const uint32_t two = refii_mod_player_hero(base, 2);
  const uint32_t targeting = Plausible(two) ? refii_mod_component(base, two, 77u) : 0u;
  if (targeting == 0u) {
    return false;
  }
  PPCContext work = refii::mods::Frame(ctx);
  if (!refii::mods::CallVirtual(work, base, targeting, 0x44u / 4u)) {
    return false;
  }
  const uint32_t target = work.r3.u32;
  if (!Plausible(target) || (refii::vfs::LoadGuestU8(base, target + 0x90u) & 0x40u) == 0u) {
    return false;
  }
  const uint32_t use = refii_mod_component(base, target, 78u);
  return use != 0u && refii::vfs::LoadGuestU8(base, use + 0x1Au) != 0u;
}

}  // namespace

// THE USE GATE READS THE VIEWER'S OWN BYTE. A usable thing's update shows its prompt only while
// its targeted component says a hero targets it — `lbz r11,0x21(r30)` / `beq` (TU1 0x828224FC /
// 0x82822504, disc 0x82753F7C / 0x82753F84), hero ONE's byte. `lhECTargeted_SetTargetedByPlayer`
// keeps hero two's at +0x22 (0x8242810C); on an online joiner hero one is the host's replica with
// no targeting process, so +0x21 never sets there and the update left before the hooks after it.
// Before the compare (TU1 0x82822500, disc 0x82753F80), the byte is the one of the player the
// update is for: hero two's when they are the viewer, and always on the joiner.
std::atomic<bool> g_said_use_gate{false};

void RefiiCoopUseGateForViewer(PPCRegister& r11, PPCRegister& r30) {
  uint8_t* base = RefiiGuestBase();
  if (base == nullptr || !Plausible(r30.u32) || (g_use_viewer == 0u && !IsJoiner(base))) {
    return;
  }
  r11.u64 = refii::vfs::LoadGuestU8(base, r30.u32 + 0x22u);
  Once(g_said_use_gate, "a usable thing's prompt gate reads player two's own targeted byte");
}

void RefiiCoopPromptForNearerPlayer(PPCRegister& r3) {
  if (g_use_viewer != 0u) {
    r3.u64 = g_use_viewer;
    Once(g_said_prompt, "a use prompt answers the nearer player");
  }
}

namespace {
/// The A event the hold handler is looking at (+44: 13 pressed, 14 held, 15 released).
int32_t g_hold_event_kind = 0;
/// For the hold handler's call: an interaction exit's prompt is up for player two, whose hold
/// goes through the guest's own (shared) hold as shipped for exits.
bool g_two_exit_hold = false;
/// Player two's own hold: whether they hold A on their own hold-to-use target.
bool g_two_held = false;

/// Player two's hold prompt, while a thing shows one for them: the thing, its use component,
/// the icon's offset from the thing (the request's v1) and the title.
struct HoldPromptTwo {
  uint32_t thing = 0u;
  uint32_t use = 0u;
  float offset[3] = {};
  std::string title;
};
HoldPromptTwo g_hold_prompt_two;

/// The hero-one slot lent to hero two for one guest call (players+108 := +116), as the opinion scope
/// lends it: every hero-one fetch inside answers hero two. Our own hero-one readers read through
/// the lend (`refii_bridge_hero_one_holder`). Does nothing when `lend` is false.
struct HeroTwoLend {
  uint8_t* base = nullptr;
  uint32_t players = 0u;
  uint32_t holder = 0u;

  HeroTwoLend(uint8_t* guest, bool lend) {
    using refii::vfs::LoadGuestU32;
    if (!lend) {
      return;
    }
    const uint32_t root = LoadGuestU32(guest, refii::guest::Address(0x83496AB8u));
    const uint32_t game = Plausible(root) ? LoadGuestU32(guest, root + 0xCu) : 0u;
    const uint32_t manager = Plausible(game) ? LoadGuestU32(guest, game + 88u) : 0u;
    const uint32_t cell = Plausible(manager) ? LoadGuestU32(guest, manager + 4u) : 0u;
    const uint32_t record = Plausible(cell) ? LoadGuestU32(guest, cell) : 0u;
    const uint32_t two = Plausible(record) ? LoadGuestU32(guest, record + 116u) : 0u;
    if (two < 0x10000u || two >= 0xE0000000u) {
      return;
    }
    base = guest;
    players = record;
    holder = LoadGuestU32(guest, record + 108u);
    refii_bridge_hero_slot_lend(holder);
    refii::mods::StoreU32(guest, record + 108u, two);
  }

  ~HeroTwoLend() {
    if (base != nullptr) {
      refii::mods::StoreU32(base, players + 108u, holder);
      refii_bridge_hero_slot_return();
    }
  }

  HeroTwoLend(const HeroTwoLend&) = delete;
  HeroTwoLend& operator=(const HeroTwoLend&) = delete;
};

/// Per usable thing (its use component): who the shown prompt was asked for, how many updates in a
/// row the nearer player has been someone else, and the other player's copy of the per-viewer
/// bytes (+0x18..+0x1F).
struct UseState {
  uint32_t thing = 0u;
  uint32_t requested_by = 0u;
  uint32_t flip = 0u;
  uint8_t other[8] = {};
};
std::unordered_map<uint32_t, UseState> g_use_state;

/// The dry pass for the other player is running: its request, hide, witness write and hints are
/// diverted (rex_lhPromptManager_Request, rex_CECOnActionUse_Hide, rex_lhPromptManager_Witnessed,
/// rex_lhTutorials_Display).
bool g_use_pass_other = false;
int32_t g_use_pass_player = 0;

/// The other player's prompt text per thing, and the prompt-manager frame it was last confirmed.
struct OtherTitle {
  int32_t player = 0;
  std::string title;
  uint32_t frame = 0u;
};
std::unordered_map<uint32_t, OtherTitle> g_other_titles;
}  // namespace

bool RefiiCoopHoldByEitherPlayer(PPCRegister& r31) {
  if ((r31.u32 & 0xFFu) != 0u) {
    g_hold_player = 1;
    return false;
  }
  if (g_two_exit_hold) {
    r31.u64 = 1u;
    g_hold_player = 2;
    Once(g_said_hold, "player two holds an exit's prompt");
    return false;
  }
  if (g_two_may_hold) {
    // Player two's own hold: the guest consumes the press as not player one's (0x823DD434), and
    // their meter is their own (rex_lhPromptManager_Update below).
    if (g_hold_event_kind == 13) {
      g_two_held = true;
    } else if (g_hold_event_kind == 15) {
      g_two_held = false;
    }
    Once(g_said_hold, "player two holds their own hold-to-use prompt");
    return false;
  }
  return true;  // not the hold's: leave the press to player two's controls
}

void RefiiCoopHoldUsesHolder(PPCRegister& r3) {
  if (g_hold_creature != 0u) {
    r3.u64 = g_hold_creature;
  }
}

namespace {

/// The things whose prompt is up and which players see each (bit 0 player one, bit 1 player
/// two), gathered over a frame and published when the next begins (refii_bridge_set_prompt_masks):
/// the host draws each icon in every half that sees it.
struct PromptMasks {
  uint32_t frame = 0xFFFFFFFFu;
  std::array<uint32_t, 64> things{};
  std::array<uint8_t, 64> masks{};
  int32_t count = 0;
};
PromptMasks g_prompt_masks;

void NotePromptMask(uint8_t* base, uint32_t thing, uint8_t mask) {
  const uint32_t frame = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83319650u));
  if (frame != g_prompt_masks.frame) {
    refii_bridge_set_prompt_masks(g_prompt_masks.things.data(), g_prompt_masks.masks.data(), g_prompt_masks.count);
    g_prompt_masks.frame = frame;
    g_prompt_masks.count = 0;
  }
  if (thing != 0u && mask != 0u && g_prompt_masks.count < static_cast<int32_t>(g_prompt_masks.things.size())) {
    g_prompt_masks.things[static_cast<std::size_t>(g_prompt_masks.count)] = thing;
    g_prompt_masks.masks[static_cast<std::size_t>(g_prompt_masks.count++)] = mask;
  }
}

/// The use update being run, and the WorldIconRadius² its in-range test was given this run.
uint32_t g_use_running = 0u;
uint32_t g_use_radius_for = 0u;
float g_use_radius_sq = 0.0f;

/// Whether `viewer` passes `thing`'s own in-range test (`sub_82821E88` 0x82822550..0x828226C8):
/// alive with a player controller (component 33), the controller's own test when its +0x7C bit 6
/// asks (`sub_823E1650(comp33, thing)`), then `sub_822BE1E8(targeting, viewer, radius², scratch)`.
bool SeesPrompt(PPCContext& ctx, uint8_t* base, uint32_t thing, uint32_t viewer, float radius_sq) {
  using refii::vfs::LoadGuestU32;
  if (!Plausible(viewer) || (refii::vfs::LoadGuestU8(base, viewer + 0x90u) & 0x40u) == 0u) {
    return false;
  }
  const uint32_t controller = refii_mod_component(base, viewer, 33u);
  const uint32_t targeting = refii_mod_component(base, thing, 23u);
  if (controller == 0u || targeting == 0u) {
    return false;
  }
  if (((LoadGuestU32(base, controller + 0x7Cu) >> 6) & 1u) != 0u) {
    PPCContext work = refii::mods::Frame(ctx);
    work.r3.u64 = controller;
    work.r4.u64 = thing;
    if (!refii::mods::CallGuest(work, base, refii::guest::Address(0x823E1650u)) || (work.r3.u32 & 0xFFu) == 0u) {
      return false;
    }
  }
  PPCContext work = refii::mods::Frame(ctx);
  const uint32_t scratch = (work.r1.u32 + 0x60u) & ~0xFu;
  for (uint32_t i = 0; i < 16u; i += 4u) {
    refii::mods::StoreU32(base, scratch + i, 0u);
  }
  work.r3.u64 = targeting;
  work.r4.u64 = viewer;
  work.r5.u64 = scratch;
  work.r6.u64 = scratch;
  work.f1.f64 = radius_sq;
  return refii::mods::CallGuest(work, base, refii::guest::Address(0x822BE1E8u)) && (work.r3.u32 & 0xFFu) != 0u;
}

}  // namespace

// The WorldIconRadius² the use update hands its in-range test (`fmuls f1,f1,f1` just before;
// TU1 0x828226B0 / disc 0x82754130), kept for the other player's test after the update.
void RefiiCoopUseRadius(PPCRegister& f1) {
  if (g_use_running != 0u) {
    g_use_radius_for = g_use_running;
    g_use_radius_sq = static_cast<float>(f1.f64);
  }
}

// A usable thing's update (r3 its CECOnActionUse component; the thing at +4). The guest keeps ONE
// prompt per thing, for the player it tests (the nearer, above, with their hero lent for the whole
// update); after it, the other player gets the same in-range test, and each half draws the icon of a
// thing its player sees. In local co-op the other player's own text comes from a dry second pass.
REX_EXTERN(rex_CECOnActionUse_Update) {
  using refii::vfs::LoadGuestU32;
  using refii::vfs::LoadGuestU8;
  const uint32_t previous = g_use_viewer;
  const uint32_t previous_running = g_use_running;
  const PPCContext entry = ctx;
  const uint32_t use = ctx.r3.u32;
  const uint32_t thing = Plausible(use) ? LoadGuestU32(base, use + 4u) : 0u;
  g_use_viewer = NearerPlayerTwo(ctx, base, thing);
  g_use_running = use;
  g_use_radius_for = 0u;
  const bool was_shown = Plausible(use) && LoadGuestU8(base, use + 0x18u) != 0u;
  {
    HeroTwoLend lend(base, g_use_viewer != 0u);
    __imp__rex_CECOnActionUse_Update(ctx, base);
  }
  const bool shown = Plausible(use) && LoadGuestU8(base, use + 0x18u) != 0u;
  const bool coop = CoopGame(base);
  if (shown && coop) {
    const uint32_t one = refii_mod_player_hero(base, 1);
    const uint32_t two = refii_mod_player_hero(base, 2);
    const bool viewer_two = g_use_viewer != 0u;
    uint8_t mask = viewer_two ? 2u : 1u;
    PPCContext work = entry;
    if (g_use_radius_for == use && SeesPrompt(work, base, thing, viewer_two ? one : two, g_use_radius_sq)) {
      mask |= viewer_two ? 1u : 2u;
    }
    NotePromptMask(base, thing, mask);
  }

  if (Plausible(use) && coop) {
    if (!shown) {
      g_use_state.erase(use);
      g_other_titles.erase(thing);
    } else {
      UseState& state = g_use_state[use];
      if (state.thing != thing) {
        state = UseState{};
        state.thing = thing;
      }
      // The text belongs to whoever it was asked for; when the nearer player changes and stays
      // changed for a few updates, the prompt is hidden and asked again for them next update.
      if (!was_shown) {
        state.requested_by = g_use_viewer;
        state.flip = 0u;
      } else if (state.requested_by != g_use_viewer) {
        if (++state.flip >= 10u) {
          state.flip = 0u;
          PPCContext work = refii::mods::Frame(entry);
          work.r3.u64 = use;
          refii::mods::CallGuest(work, base, refii::guest::Address(0x82823498u));
        }
      } else {
        state.flip = 0u;
      }

      // The other player's own text: the same update, dry, for them.
      const uint32_t two = refii_mod_player_hero(base, 2);
      if (LocalCoopGame(base) && two != 0u && refii_mod_player_hero(base, 1) != 0u &&
          LoadGuestU8(base, use + 0x18u) != 0u) {
        const bool other_is_two = g_use_viewer == 0u;
        uint8_t mine[8];
        std::memcpy(mine, base + use + 0x18u, sizeof(mine));
        std::memcpy(base + use + 0x18u, state.other, sizeof(state.other));
        const uint32_t viewer = g_use_viewer;
        g_use_viewer = other_is_two ? two : 0u;
        g_use_pass_other = true;
        g_use_pass_player = other_is_two ? 2 : 1;
        {
          PPCContext pass = entry;
          HeroTwoLend lend(base, other_is_two);
          __imp__rex_CECOnActionUse_Update(pass, base);
        }
        g_use_pass_other = false;
        g_use_viewer = viewer;
        std::memcpy(state.other, base + use + 0x18u, sizeof(state.other));
        std::memcpy(base + use + 0x18u, mine, sizeof(mine));
        if (state.other[0] == 0u) {
          g_other_titles.erase(thing);
        } else if (const auto found = g_other_titles.find(thing); found != g_other_titles.end()) {
          found->second.frame = LoadGuestU32(base, refii::guest::Address(0x83319650u));
        }
      }
    }
  }
  g_use_running = previous_running;
  g_use_viewer = previous;
}

// The use's hide (`sub_82823498`: r3 the component; removes the prompt from the manager by the
// thing's key, `sub_821BD798`, and clears +0x18, +0x1A, +0x1B, +0x1C). In the other player's dry
// pass only their copy of the bytes is cleared: the manager's prompt is the nearer player's.
// TU1 0x82823498.
REX_EXTERN(rex_CECOnActionUse_Hide) {
  const uint32_t use = ctx.r3.u32;
  if (!g_use_pass_other || !Plausible(use)) {
    __imp__rex_CECOnActionUse_Hide(ctx, base);
    return;
  }
  refii::mods::StoreU8(base, use + 0x18u, 0u);
  refii::mods::StoreU8(base, use + 0x1Au, 0u);
  refii::mods::StoreU8(base, use + 0x1Bu, 0u);
  refii::mods::StoreU32(base, use + 0x1Cu, 0u);
  g_other_titles.erase(refii::vfs::LoadGuestU32(base, use + 4u));
}

// The hold prompt's "witnessed" write to the GUI (`sub_823DD9E8`: GUI page 9, one global). The
// other player's dry pass writes nothing. TU1 0x823DD9E8.
REX_EXTERN(rex_lhPromptManager_Witnessed) {
  if (!g_use_pass_other) {
    __imp__rex_lhPromptManager_Witnessed(ctx, base);
  }
}

// TryQueueTutorial (`sub_821B5260(mgr, id)`: one tutorial queue for the whole game, its shown and
// exhausted state per hint, displayed for hero one, `sub_8227BF90`). Code running for player two
// raises none: the other player's dry use pass (hints 92, 113) and an opinion scope for hero two
// (61 TUTORIAL_MARRIAGE_ATTRACT, 203 TUTORIAL_OPINION_ATTRACTED, 183 TUTORIAL_OPINION_CHANGE from
// `sub_821AD468`), which would show player one a hint about player two's villager. TU1 0x821B5260.
REX_EXTERN(rex_lhTutorials_Display) {
  if (g_use_pass_other || refii_mod_opinion_scope_two() != 0) {
    ctx.r3.u64 = 0u;
    return;
  }
  __imp__rex_lhTutorials_Display(ctx, base);
}

// PLAYER TWO'S ITEMS AND CLOTHING PAGES (local co-op). The pause's screens 15 (items/weapons,
// vtable 0x820CFEB0) and 16 (clothing, 0x820CFEDC) are built for hero one: the clothing page's preview
// mannequin takes hero one's sex, age and body (`sub_826D7650` 0x826D76EC), both pages' gold line reads
// hero one (`sub_8246B5E8(…, 1)`) and their populates list the local hero's inventory. While player two
// owns the pause, hero two is lent into hero one's slot for each page's open and update, which are the
// ported bodies (gui_screen_stub_items.cpp, gui_screen_stub_clothing.cpp). Equipping is not done here:
// the pump acts on player two's hero directly (guest_tick_pump.cpp), because the pages' own answers
// commit to the equipment record at GUI+52, which pause exit applies to hero one.
namespace {
bool PlayerTwoOwnsPause(uint8_t* base) {
  return refii_bridge_menu_owner_two() != 0 && LocalCoopGame(base) && RefiiCoopGetOwnHero() != 0;
}
}  // namespace

REX_EXTERN(rex_lhGuiItemsScreen_OnEnter) {
  HeroTwoLend lend(base, PlayerTwoOwnsPause(base));
  refii::vfs::ItemsScreenOnEnter(ctx, base);
}

REX_EXTERN(rex_lhGuiItemsScreen_Update) {
  HeroTwoLend lend(base, PlayerTwoOwnsPause(base));
  refii::vfs::ItemsScreenUpdate(ctx, base);
}

REX_EXTERN(rex_lhGuiClothingScreen_OnEnter) {
  HeroTwoLend lend(base, PlayerTwoOwnsPause(base));
  refii::vfs::ClothingScreenOnEnter(ctx, base);
}

REX_EXTERN(rex_lhGuiClothingScreen_Update) {
  HeroTwoLend lend(base, PlayerTwoOwnsPause(base));
  refii::vfs::ClothingScreenUpdate(ctx, base);
}

// The prompt manager's request (`sub_823DD730`: r3 manager, r4 &key, r5 the thing, r6 &title,
// r7 type, v1 the icon's offset from the thing). A hold prompt (type 35) asked for player two in
// local co-op is kept as theirs and answered 1, as a request the manager took is; the use update
// then marks it shown (+0x18, +0x1A) as it would.
REX_EXTERN(rex_lhPromptManager_Request) {
  if (g_use_pass_other) {
    // The other player's dry pass: their text for this thing, never the manager's prompt.
    if (Plausible(ctx.r5.u32)) {
      OtherTitle& other = g_other_titles[ctx.r5.u32];
      other.player = g_use_pass_player;
      other.title = Plausible(ctx.r6.u32) ? refii::vfs::ReadGuestWideString(base, ctx.r6.u32) : std::string();
      other.frame = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83319650u));
    }
    ctx.r3.u64 = 1u;
    return;
  }
  if (g_use_viewer == 0u || g_use_running == 0u || ctx.r7.u32 != 35u || !Plausible(ctx.r5.u32) ||
      !LocalCoopGame(base)) {
    __imp__rex_lhPromptManager_Request(ctx, base);
    return;
  }
  HoldPromptTwo prompt;
  prompt.thing = ctx.r5.u32;
  prompt.use = g_use_running;
  refii::mods::GetVector(ctx, 1, prompt.offset);
  prompt.title = Plausible(ctx.r6.u32) ? refii::vfs::ReadGuestWideString(base, ctx.r6.u32) : std::string();
  g_hold_prompt_two = std::move(prompt);
  Once(g_said_hold, "player two has their own hold prompt");
  ctx.r3.u64 = 1u;
}

namespace {

/// Whether `event` came from player one's pad (`sub_823DD318` 0x823DD388..0x823DD3B8: the pad of
/// player record 1, `sub_82264230(*(game+156), 1)`, matched on +40).
bool PlayerOnesEvent(PPCContext& ctx, uint8_t* base, uint32_t event) {
  using refii::vfs::LoadGuestU32;
  const uint32_t root = LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = Plausible(root) ? LoadGuestU32(base, root + 0xCu) : 0u;
  if (!Plausible(game)) {
    return true;
  }
  PPCContext work = refii::mods::Frame(ctx);
  work.r3.u64 = LoadGuestU32(base, game + 156u);
  work.r4.u64 = 1u;
  if (!refii::mods::CallGuest(work, base, refii::guest::Address(0x82264230u))) {
    return true;
  }
  const uint32_t pad = work.r3.u32;
  return Plausible(pad) && LoadGuestU32(base, event + 40u) == LoadGuestU32(base, pad + 40u);
}

/// Whether `hero` moves faster than the hold handler lets a press be a hold: the creature's
/// velocity (`*(hero+0x7C)` vfunc +0x4C) squared over x and y past 1 (0x823DD3E0..0x823DD428).
bool MovingTooFastToHold(PPCContext& ctx, uint8_t* base, uint32_t hero) {
  using refii::vfs::LoadGuestU32;
  const uint32_t mover = Plausible(hero) ? LoadGuestU32(base, hero + 0x7Cu) : 0u;
  const uint32_t vtable = Plausible(mover) ? LoadGuestU32(base, mover) : 0u;
  if (!Plausible(vtable)) {
    return false;
  }
  PPCContext work = refii::mods::Frame(ctx);
  work.r3.u64 = (work.r1.u32 + 0x80u) & ~0xFu;
  work.r4.u64 = mover;
  if (!refii::mods::CallGuest(work, base, LoadGuestU32(base, vtable + 0x4Cu)) || !Plausible(work.r3.u32)) {
    return false;
  }
  const auto lane = [&](uint32_t i) {
    const uint32_t bits = LoadGuestU32(base, work.r3.u32 + i * 4u);
    float value = 0.0f;
    std::memcpy(&value, &bits, sizeof(value));
    return value;
  };
  const float x = lane(0u), y = lane(1u);
  return x * x + y * y > 1.0f;
}

}  // namespace

// The prompt manager's A handler (r3 manager, r4 the input event: +8 button, +44 kind). While
// player two has their own hold prompt, their A presses are its: taken here (+56 consumed, 1)
// before the shipped handler, which sees only the manager's one prompt.
REX_EXTERN(rex_lhPromptManager_HoldInput) {
  {
    const uint32_t event = ctx.r4.u32;
    const int32_t kind = Plausible(event) ? static_cast<int32_t>(refii::vfs::LoadGuestU32(base, event + 44u)) : 0;
    const uint32_t frame = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83319650u));
    if (g_hold_prompt_two.use != 0u && frame - g_exit_prompt_two_frame > 2u && Plausible(event) &&
        refii::vfs::LoadGuestU8(base, event + 8u) == 11u &&
        kind >= 13 && kind <= 15 && LocalCoopGame(base) && !PlayerOnesEvent(ctx, base, event) &&
        !MovingTooFastToHold(ctx, base, refii_mod_player_hero(base, 2))) {
      if (kind == 13) {
        g_two_held = true;
      } else if (kind == 15) {
        g_two_held = false;
      }
      refii::mods::StoreU8(base, event + 56u, 1u);
      ctx.r3.u64 = 1u;
      return;
    }
  }
  const bool previous = g_two_may_hold;
  const bool previous_exit = g_two_exit_hold;
  const uint32_t event = ctx.r4.u32;
  const uint32_t frame = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83319650u));
  const bool a_event = Plausible(event) && refii::vfs::LoadGuestU8(base, event + 8u) == 11u && CoopGame(base);
  g_hold_event_kind = Plausible(event) ? static_cast<int32_t>(refii::vfs::LoadGuestU32(base, event + 44u)) : 0;
  g_two_exit_hold = a_event && frame - g_exit_prompt_two_frame <= 2u;
  g_two_may_hold = a_event && !g_two_exit_hold && PlayerTwoTargetsHoldUse(ctx, base);
  __imp__rex_lhPromptManager_HoldInput(ctx, base);
  g_two_may_hold = previous;
  g_two_exit_hold = previous_exit;
}

namespace {

/// Player two's own hold meter: the guest's integrator (`sub_821C5BB0` 0x821C5EB4..0x821C5F2C)
/// at its own rates (GUIValues HeldButtonUpdateSpeed / ReleasedButtonUpdateSpeed, read live),
/// on player two's target, completing ONCE as the guest does — on the frame it reaches 1, through
/// the guest's own use `sub_828219F8(target's component 78, hero)` (0x821C62C4) — and starting
/// again once they let go or change target.
float g_two_fraction = 0.0f;
uint32_t g_two_target = 0u;

/// Player two's hold prompt as it stands: dropped once its thing stops showing it (the use's hide,
/// `sub_82823498`, clears +0x18 and +0x1A) or outside local co-op; stated to the host with its
/// world point (the thing's position plus the request's offset) and title.
void PublishHoldPromptTwo(PPCContext& ctx, uint8_t* base) {
  HoldPromptTwo& prompt = g_hold_prompt_two;
  float at[3] = {};
  const bool up = prompt.use != 0u && LocalCoopGame(base) && Plausible(prompt.thing) &&
                  refii::vfs::LoadGuestU8(base, prompt.use + 0x18u) != 0u &&
                  refii::vfs::LoadGuestU8(base, prompt.use + 0x1Au) != 0u &&
                  refii::vfs::LoadGuestU32(base, prompt.use + 4u) == prompt.thing &&
                  refii_mod_entity_position(&ctx, base, prompt.thing, at) != 0;
  if (!up) {
    prompt = HoldPromptTwo{};
    refii_bridge_player_two_hold_prompt(0, nullptr, nullptr);
    return;
  }
  for (int i = 0; i < 3; ++i) {
    at[i] += prompt.offset[i];
  }
  refii_bridge_player_two_hold_prompt(1, at, prompt.title.c_str());
}

/// The other player's prompt texts (the dry pass), as `thing \t player \t title` lines; a thing
/// no longer confirmed by its update for a few frames (unloaded, gone) is dropped.
void PublishOtherTitles(uint8_t* base) {
  const uint32_t now = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83319650u));
  std::string text;
  for (auto it = g_other_titles.begin(); it != g_other_titles.end();) {
    if (now - it->second.frame > 3u) {
      it = g_other_titles.erase(it);
      continue;
    }
    std::string title;
    for (char c : it->second.title) {
      title.push_back(c == '\t' || c == '\n' ? ' ' : c);
    }
    text += std::to_string(it->first) + '\t' + std::to_string(it->second.player) + '\t' + title + '\n';
    ++it;
  }
  refii_bridge_set_prompt_titles(text.c_str());
}

void DrivePlayerTwoHold(PPCContext& ctx, uint8_t* base, float dt) {
  using refii::vfs::LoadGuestU32;
  PublishHoldPromptTwo(ctx, base);
  PublishOtherTitles(base);
  const uint32_t two = refii_mod_player_hero(base, 2);
  uint32_t target = 0u;
  uint32_t use = 0u;
  if (two != 0u && LocalCoopGame(base) && PlayerTwoTargetsHoldUse(ctx, base)) {
    const uint32_t targeting = refii_mod_component(base, two, 77u);
    PPCContext work = refii::mods::Frame(ctx);
    if (targeting != 0u && refii::mods::CallVirtual(work, base, targeting, 0x44u / 4u)) {
      target = work.r3.u32;
      use = Plausible(target) ? refii_mod_component(base, target, 78u) : 0u;
    }
  }
  if (use == 0u) {
    g_two_fraction = 0.0f;
    g_two_target = 0u;
    g_two_held = false;
    refii_bridge_player_two_hold_state(0.0f, 0);
    return;
  }
  if (target != g_two_target) {
    g_two_target = target;
    g_two_fraction = 0.0f;
  }
  if (g_two_fraction >= 1.0f && !g_two_held) {
    g_two_fraction = 0.0f;
  }
  const auto rate = [base](uint32_t address) {
    const uint32_t bits = LoadGuestU32(base, refii::guest::Address(address));
    float value = 0.0f;
    std::memcpy(&value, &bits, sizeof(value));
    return value;
  };
  if (g_two_fraction < 1.0f && dt > 0.0f && dt < 1.0f) {
    g_two_fraction += g_two_held ? rate(0x8335C4A8u) * dt : -rate(0x8335C4A4u) * dt;
    g_two_fraction = std::clamp(g_two_fraction, 0.0f, 1.0f);
    if (g_two_fraction >= 1.0f) {
      PPCContext work = refii::mods::Frame(ctx);
      work.r3.u64 = use;
      work.r4.u64 = two;
      refii::mods::CallGuest(work, base, refii::guest::Address(0x828219F8u));
      Once(g_said_hold, "player two completed their own hold");
    }
  }
  refii_bridge_player_two_hold_state(g_two_fraction, g_two_held ? 1 : 0);
}

}  // namespace

// The prompt manager's update (r3 the manager, f1 now; +0x38 the last time): a full hold of the
// shared meter uses as the player who held (an exit's), and player two's own meter steps.
REX_EXTERN(rex_lhPromptManager_Update) {
  const uint32_t previous = g_hold_creature;
  const PPCContext entry = ctx;
  const uint32_t manager = ctx.r3.u32;
  float last = 0.0f;
  if (Plausible(manager)) {
    const uint32_t bits = refii::vfs::LoadGuestU32(base, manager + 0x38u);
    std::memcpy(&last, &bits, sizeof(last));
  }
  const float dt = static_cast<float>(ctx.f1.f64) - last;
  g_hold_creature = g_hold_player == 2 && CoopGame(base) ? refii_mod_player_hero(base, 2) : 0u;
  // The step the world icons' Updates are stamped with (stated_world_icons.cpp) is one call of this
  // update: it is the icons' only driver (lhWorldIcon_Update under it; its GUI gate `bl
  // lhGameFlow_IsGuiActive` / `bne` TU1 0x821C5BE8 / disc 0x821C5C58 runs none), and it runs less
  // often than the tick pump when a frame holds several sim steps.
  static uint64_t s_updates = 0;
  refii_bridge_world_icons_step(++s_updates);
  __imp__rex_lhPromptManager_Update(ctx, base);
  g_hold_creature = previous;
  PPCContext work = entry;
  DrivePlayerTwoHold(work, base, dt);
}
