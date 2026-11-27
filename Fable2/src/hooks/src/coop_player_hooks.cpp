// Co-op: the Player unpublish, and a trace of the join handshake.
//
// PLAYER UNPUBLISH. A `Player` (0x48 bytes, ctor `sub_82389938`) is owned solely by the
// `PlayerManager` at `game+156`, but `sub_82371860` publishes the same pointer RAW into the
// App's two input handlers (`*(App+20)+12`, `*(App+24)+12`, at 0x823718A0 / 0x823718D0) and
// into 48-byte input records under `*(App+32)+56`. The guest's own unpublish `sub_82371948` is
// reached from ONE site, 0x82316A78, which needs both players present — the leave-co-op path.
// Running it from the destructor covers every other way a Player dies. It is idempotent: the
// clear and the `--App+101` are both guarded on a handler actually having pointed at this
// Player (0x8237199C).
//
// JOIN HANDSHAKE TRACE. The joining client is meant to leave the front end by itself:
// `sub_82319BE8`, the app-state Run loop, does
//     82319dc0 lbz r10,0x34(r11)  ; CM+52 in a session
//     82319dd0 lbz r10,0x36(r11)  ; CM+54 we are the JOINER
//     82319dec beq loc_82319E1C   ; not both -> stay in the front end
//     82319e00 stb r25,0x5C(r10)  ; *( *(0x83496AC0)+12 + 92 ) = 1
// and `+92` both suppresses the front-end update (0x8231A02C) and breaks the loop (0x82319D00).
// Those bytes are set by two received things: `sub_822EEA50` (join response approved) sets
// CXboxLive+1328 and CM+218 at 0x822EF1D8/DC, and inbound packet 0x118 -> `sub_82334E60` sets
// CM+52/CM+54 at 0x82334ED8/DC. The joiner's co-op SCREEN is separately opened by receiving
// message 156, handled at 0x823317C4 (verified: `li r4,0x18` screen id 24 at 0x823317F8,
// `lbz r8,0x10D(r31)` the up-latch at 0x8233180C).
//
// Observed: P2 stays in the front-end multiplayer menu through the join and must back out of
// the pause menu to regain control. So at least one of those bytes is never set. These hooks
// say which, rather than guessing.

#include <array>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstdlib>
#include <cstring>

#include <rex/logging.h>
#include <rex/ppc/context.h>
#include <rex/ppc/func.h>

#include "core/guest_state.h"
#include "core/payload_bridge_fable2.h"
#include "core/guest_build.h"
#include "core/refii_mod.h"

#include "coop_held_attach_rule.h"

extern "C" REX_FUNC(__imp__rex_lhPlayer_ScalarDeletingDtor);
extern "C" REX_FUNC(__imp__rex_lhGui_RegisterPlayer);
extern "C" REX_FUNC(__imp__rex_lhNetManager_ProcessInboundSessionPacket);
extern "C" REX_FUNC(__imp__rex_lhNetManager_ProcessInboundGamePacket);
extern "C" REX_FUNC(__imp__rex_lhNetManager_AcceptJoinAsClient);
extern "C" REX_FUNC(__imp__rex_lhXboxLive_ProcessJoinResponse);
extern "C" REX_FUNC(__imp__rex_lhNetManager_DisplayCoopScreen);
extern "C" REX_FUNC(__imp__rex_lhCoopScreen_SpawnPreviewMannequin);
extern "C" REX_FUNC(__imp__rex_lhNetManager_GetHenchmanCreatureName);
extern "C" REX_FUNC(__imp__rex_lhCoopScreen_GetMannequinDefName);
extern "C" REX_FUNC(__imp__rex_lhCharString_CtorFromCStr);

/// The bit itself lives with the pump, in refii_vfs — see `guest_tick_pump.cpp`. It is set from
/// the host through the front-end action `CoopHeroBody:`, and the hooks already link that way
/// round (`core/guest_state.h` below). 0 henchman, 1 their own hero.
extern "C" int RefiiCoopGetOwnHero(void);

extern "C" REX_FUNC(__imp__rex_lhEntityManager_ChangeEntityType);
extern "C" REX_FUNC(__imp__rex_lhNetManager_SetupHenchman);
extern "C" void RefiiCoopRedress(void* ctx, uint8_t* base);
extern "C" void RefiiCoopForgetBody(uint32_t entity);
extern "C" uint32_t RefiiCoopPlayerTwoEntity(uint8_t* base);
extern "C" const char* RefiiCoopMeleeName(void);
extern "C" const char* RefiiCoopRangedName(void);
extern "C" REX_FUNC(__imp__rex_lhNetManager_GetHenchmanMeleeWeaponName);
extern "C" REX_FUNC(__imp__rex_lhNetManager_GetHenchmanRangedWeaponName);
extern "C" REX_FUNC(__imp__rex_lhCoop_ArmHenchmanWeapons);
extern "C" REX_FUNC(__imp__rex_CECHeldItems_OnNetMessage);
extern "C" REX_FUNC(__imp__rex_lhEntity_Destroy);
extern "C" REX_FUNC(__imp__rex_CECNetwork_SendTransform);
extern "C" REX_FUNC(__imp__rex_lhNetManager_FindEntityByNetId);
extern "C" REX_FUNC(__imp__rex_CECNetwork_ApplyReplicaTarget);

// 0x82371948, which refii_config.toml misnames `rex_lhScene_UpdateHeightfield` — the body
// unpublishes a Player. That config line is the name's only reference in the tree.
extern "C" REX_FUNC(__imp__rex_lhScene_UpdateHeightfield);

namespace {
constexpr refii::guest::Addr kAppPointer{0x83496B08u};
constexpr refii::guest::Addr kGameRoot{0x83496AB8u};

constexpr uint32_t kFirstHandler = 20u;
constexpr uint32_t kSecondHandler = 24u;

constexpr bool Plausible(uint32_t address) {
  return address >= 0x10000u && address < 0xC0000000u;

}

bool IsPlayerBody(uint8_t* base, uint32_t entity);  // defined with the cadence hooks below
uint32_t DestroyLocalHeldItems(const PPCContext& saved, uint8_t* base, uint32_t entity);  // below

/// The co-op manager — `g_XrnmNetContext` (0x83497490) itself. `game+140` is only a counted
/// COPY of it, and a title-joined client had that copy EMPTY in game; the global is the truth.
uint32_t CoopManager(uint8_t* base) {
  using refii::vfs::LoadGuestU32;
  const uint32_t manager = LoadGuestU32(base, refii::guest::Address(0x83497490u));
  if (Plausible(manager)) return manager;
  const uint32_t root = LoadGuestU32(base, kGameRoot);
  const uint32_t game = Plausible(root) ? LoadGuestU32(base, root + 12u) : 0u;
  return Plausible(game) ? LoadGuestU32(base, game + 140u) : 0u;
}

/// The five bytes the front-end teardown is gated on, in one line.
void ReportJoinState(uint8_t* base, const char* where) {
  using refii::vfs::LoadGuestU8;

  const uint32_t cm = CoopManager(base);
  if (!Plausible(cm)) {
    REXLOG_INFO("coop JOIN [{}]: no co-op manager yet", where);
    return;
  }

  REXLOG_INFO(
      "coop JOIN [{}]: CM+52(session)={} CM+53(host)={} CM+54(joiner)={} CM+218={} "
      "CM+217={} CM+223={}",
      where, LoadGuestU8(base, cm + 52u), LoadGuestU8(base, cm + 53u),
      LoadGuestU8(base, cm + 54u), LoadGuestU8(base, cm + 218u), LoadGuestU8(base, cm + 217u),
      LoadGuestU8(base, cm + 223u));
}
}  // namespace


// THE CO-OP/GAME CHANNEL — the OTHER receive path, and the one we were blind to.
//
// `sub_8232B518` below carries session packets (ids 0x113+, 17 entries at jumptable
// 0x8232B588). Everything else — ids 1..273 through the jumptable at 0x8232E410, indexed
// `id - 1` — arrives HERE, and that is where the co-op screen (156), every screen fade
// (205/206/207/267), the screen-set messages (227/230/236/256/257) and the level-change
// barrier's siblings live. A trace that only hooked the session path could never see them.
//
// WHY THIS MATTERS: a sweep of all 351 typed-send call sites found 172 gated on the HOST pair
// (`coopMgr+52 && +53`) and EVERY id they send has a live receive handler — ids 263/264 are the
// only unhandled ones in 1..273 and neither is ever sent. So a client-side omission is never
// "the guest forgot to handle it"; it is "the message did not arrive". This says which did.
//
// One line per DISTINCT id, not per arrival: the question is which ids ever come through, and
// the simulation mirrors (entity, AI, economy) would otherwise bury the answer.
REX_EXTERN(rex_lhNetManager_ProcessInboundGamePacket) {
  using refii::vfs::LoadGuestU8;

  const uint32_t message = ctx.r4.u32;
  if (Plausible(message)) {
    const uint32_t id = (static_cast<uint32_t>(LoadGuestU8(base, message)) << 8) |
                        LoadGuestU8(base, message + 1u);

    static std::atomic<bool> seen[512]{};
    if (id < 512u && !seen[id].exchange(true, std::memory_order_relaxed)) {
      REXLOG_INFO("coop CHANNEL: first inbound game message id={} (0x{:X})", id, id);
    }
  }

  __imp__rex_lhNetManager_ProcessInboundGamePacket(ctx, base);
}

// ProcessInboundSessionPacket — a3 (r5) is the packet; its 16-bit id is the switch key.
REX_EXTERN(rex_lhNetManager_ProcessInboundSessionPacket) {
  using refii::vfs::LoadGuestU8;

  const uint32_t packet = ctx.r5.u32;
  if (Plausible(packet)) {
    const uint32_t id =
        (static_cast<uint32_t>(LoadGuestU8(base, packet)) << 8) | LoadGuestU8(base, packet + 1u);
    static std::atomic<bool> seen[512]{};
    if (id < 512u && !seen[id].exchange(true, std::memory_order_relaxed)) {
      REXLOG_INFO("coop JOIN: first inbound session packet id=0x{:X}", id);
    }
  }

  __imp__rex_lhNetManager_ProcessInboundSessionPacket(ctx, base);
}

// Packet 0x118's handler — sets CM+52 = 1, CM+54 = 1 (0x82334ED8 / 0x82334EDC).
REX_EXTERN(rex_lhNetManager_AcceptJoinAsClient) {
  __imp__rex_lhNetManager_AcceptJoinAsClient(ctx, base);
  ReportJoinState(base, "packet 0x118 handled");
}

// XboxLive_ProcessJoinResponse — on APPROVED sets CXboxLive+1328 and CM+218 (0x822EF1D8/DC).
REX_EXTERN(rex_lhXboxLive_ProcessJoinResponse) {
  REXLOG_INFO("coop JOIN: ProcessJoinResponse entered");
  __imp__rex_lhXboxLive_ProcessJoinResponse(ctx, base);
  ReportJoinState(base, "join response processed");
}

// Multiplayer.DisplayCoopScreen — the only thing that opens screen 24.
REX_EXTERN(rex_lhNetManager_DisplayCoopScreen) {
  ReportJoinState(base, "DisplayCoopScreen entered");
  __imp__rex_lhNetManager_DisplayCoopScreen(ctx, base);
}

// The henchman mannequin spawn. `screen+276` is the raw entity (0x824388CC) and `screen+284`
// the preview ticket (0x824389B4); both zero means the preview was never built.
REX_EXTERN(rex_lhCoopScreen_SpawnPreviewMannequin) {
  using refii::vfs::LoadGuestU32;

  const uint32_t screen = ctx.r3.u32;
  const char* outer = refii::vfs::SetPreviewFigureRole("henchman");
  __imp__rex_lhCoopScreen_SpawnPreviewMannequin(ctx, base);
  refii::vfs::SetPreviewFigureRole(outer);

  if (Plausible(screen)) {
    // A new figure, often at the old one's address: what was latched as dressed on that address
    // belongs to the destroyed one.
    RefiiCoopForgetBody(LoadGuestU32(base, screen + 276u));
    REXLOG_INFO("coop MANNEQUIN: sub_82438818 ran — screen+276(entity)=0x{:08X} ticket=0x{:08X}",
                LoadGuestU32(base, screen + 276u), LoadGuestU32(base, screen + 284u));
  }
}

// sub_82371860 — publish a Player into the App's handlers.
REX_EXTERN(rex_lhGui_RegisterPlayer) {
  using refii::vfs::LoadGuestU32;

  const uint32_t player = ctx.r4.u32;
  const uint32_t player_id = Plausible(player) ? LoadGuestU32(base, player + 36u) : 0u;
  __imp__rex_lhGui_RegisterPlayer(ctx, base);

  const uint32_t app = LoadGuestU32(base, kAppPointer);
  if (Plausible(app)) {
    const uint32_t a = LoadGuestU32(base, app + kFirstHandler);
    const uint32_t b = LoadGuestU32(base, app + kSecondHandler);
    REXLOG_INFO("coop TRACE: publish(player=0x{:08X} id={}) -> h1=0x{:08X} h2=0x{:08X}", player,
                player_id, Plausible(a) ? LoadGuestU32(base, a + 12u) : 0u,
                Plausible(b) ? LoadGuestU32(base, b + 12u) : 0u);
  }
}

// Player::~Player — vtable off_820A9984 slot 0, through the shared_ptr deleter.
REX_EXTERN(rex_lhPlayer_ScalarDeletingDtor) {
  using refii::vfs::LoadGuestU32;

  const uint32_t player = ctx.r3.u32;
  const uint32_t app = LoadGuestU32(base, kAppPointer);

  // `sub_82371948` dereferences BOTH handler slots with no null check of its own (0x8237197C),
  // and this destructor also runs at shutdown when the App global can outlive what it points at.
  if (Plausible(player) && Plausible(app) &&
      Plausible(LoadGuestU32(base, app + kFirstHandler)) &&
      Plausible(LoadGuestU32(base, app + kSecondHandler))) {
    PPCContext unpublish = ctx;
    unpublish.r3.u32 = app;
    unpublish.r4.u32 = player;
    unpublish.lr = refii::guest::Address(0x82389B18u);
    __imp__rex_lhScene_UpdateHeightfield(unpublish, base);
  }

  __imp__rex_lhPlayer_ScalarDeletingDtor(ctx, base);
}

// --- Player two in their own hero's body --------------------------------
//
// `sub_8233C508(out, manager, index)` is a pure name table: eight henchman creatures, picked by
// the co-op screen's appearance pickbox. IT IS THE ONLY NAME SOURCE FOR PLAYER TWO — both real
// callers go through it, `sub_8233D090` (spawn at 0x8233D270, RE-SKIN at 0x8233D2E0) and
// `sub_8233CB18`. So answering it differently is the whole feature: every path that makes player
// two a body asks us for the name first.
//
// WHICH IS WHY IT IS DONE HERE AND NOT AS A ONE-SHOT SWAP. `sub_8233D090` is not called once.
// `sub_82436548` re-runs it EVERY FRAME the co-op screen is up (0x824365CC gates on
// `!screen+268 && screen+48 && screen+160`, then 0x824365DC → `sub_82437168` → 0x8243720C), and
// `sub_823276D0` re-applies it from the manager's stored index on level change and save load
// (`CmdLevelChange_Update` 0x823D6174/0x823D6268, `CmdLoadSaveGame_Update` 0x823D7938). A body
// swapped by calling `sub_82389FB8` ourselves is therefore undone within a frame on the screen,
// and again the moment play starts. Answering the name means the guest's own machinery spawns,
// re-skins and previews the hero for us, on both machines, with no race to lose.
//
// The index mapping is the pickbox's own: 0-2 female, 3-5 male, 6 male child, 7 female child.
REX_EXTERN(rex_lhNetManager_GetHenchmanCreatureName) {
  if (RefiiCoopGetOwnHero() == 0) {
    __imp__rex_lhNetManager_GetHenchmanCreatureName(ctx, base);
    return;
  }

  // GENDER COMES OFF THE INDEX THE GUEST SET, and the mapping is the guest's own: 0-2 female,
  // 3-5 male, 6 male child, 7 female child. When a save has been chosen, `sub_8243AEF8` derives
  // that index from the save's real Gender and Morality, so it is the authority — a captured
  // kit's gender was taken from no real hero and built a female hero over a male henchman.
  const uint32_t index = ctx.r5.u32;
  const char* creature = index == 6u   ? "CreatureHeroChild"
                         : index == 7u ? "CreatureHeroFemaleChild"
                         : index >= 3u ? "CreatureHero"
                                       : "CreatureHeroFemale";

  // The callee wants a guest pointer, so the name goes on a scratch frame below the caller's,
  // above the callee's argument-save area (frame+0x60 and up). `r5 = -1` is the length
  // `sub_822078A0` reads as strlen, and the guest sets it at every one of these call sites
  // (0x8233C560 and its siblings).
  const uint32_t frame = (ctx.r1.u32 - 0x100u) & ~0xFu;
  const uint32_t text = frame + 0x60u;

  const char* at = creature;
  for (uint32_t i = 0; *at != '\0'; ++i, ++at) {
    base[text + i] = static_cast<uint8_t>(*at);
  }

  base[text + std::strlen(creature)] = 0;

  PPCContext work = ctx;
  work.r1.u32 = frame;
  work.r4.u32 = text;
  work.r5.u32 = 0xFFFFFFFFu;
  __imp__rex_lhCharString_CtorFromCStr(work, base);

  static std::atomic<bool> said{false};
  if (!said.exchange(true, std::memory_order_relaxed)) {
    REXLOG_INFO("coop HERO: player two's creature is '{}' (pickbox index {})", creature, index);
  }
}

// --- The co-op screen's preview, in the hero's body too --------------------
//
// `sub_82437BF0(out, manager, index)` is the preview's own name table (0x82437BF0, decompiled):
// six `CreatureHenchmanMannequin*` by the same pickbox index, and the two child HERO mannequins
// `CreatureHeroMannequin{Male,Female}Child`. The adult hero mannequins exist beside them in the
// image (`CreatureHeroMannequinMale` 0x820CFF38, `...Female` 0x820CFF54 — the equipment book's
// mannequin), so while player two stands in their own hero the preview answers those, and the
// guest's own `sub_82438818` spawns the hero on the right page. The pump dresses it and asks for
// a respawn when the bit flips.
REX_EXTERN(rex_lhCoopScreen_GetMannequinDefName) {
  const uint32_t index = ctx.r5.u32;
  if (RefiiCoopGetOwnHero() == 0 || index >= 6u) {
    __imp__rex_lhCoopScreen_GetMannequinDefName(ctx, base);
    return;
  }

  const char* creature = index >= 3u ? "CreatureHeroMannequinMale" : "CreatureHeroMannequinFemale";
  const uint32_t frame = (ctx.r1.u32 - 0x100u) & ~0xFu;
  const uint32_t text = frame + 0x60u;  // above the callee's argument-save area
  const char* at = creature;
  for (uint32_t i = 0; *at != '\0'; ++i, ++at) {
    base[text + i] = static_cast<uint8_t>(*at);
  }

  base[text + std::strlen(creature)] = 0;

  PPCContext work = ctx;
  work.r1.u32 = frame;
  work.r4.u32 = text;
  work.r5.u32 = 0xFFFFFFFFu;
  __imp__rex_lhCharString_CtorFromCStr(work, base);

  static std::atomic<bool> said{false};
  if (!said.exchange(true, std::memory_order_relaxed)) {
    REXLOG_INFO("coop MANNEQUIN: the preview's creature is '{}' (pickbox index {})", creature, index);
  }
}

// --- Dressing the hero body --------------------------------------------
//
// Neither of the guest's two ways of giving player two a body dresses a HERO body. A RETYPE
// (`sub_82365258`) tests the new name for "Henchman" (0x82365938) and, for a hero name, re-wears
// the STASH — filled only when a hero becomes a henchman, so empty here. CREATION
// (`sub_82315030`, the branch `sub_8233D090` takes when no entity exists yet) runs the mode-0
// `Initialise` that reads `InitialItems`/`InitialModifiers`, but on a co-op client its gate byte
// `comp91+104` is set only AFTER creation (0x82315224), so those passes are skipped. A run
// confirmed the second path: both players typed to the same def, no retype, no dress.
//
// So the pump's redress runs after BOTH — `sub_8233D090` is the one entry every body build goes
// through, create or retype, and `sub_82365258` also covers the direct nudge `sub_82389FB8`.
// The redress itself dresses once per entity and outfit; see CoopRedress in the pump.
// Runs every frame the co-op screen is up, so the latches are dropped only when it actually
// gave player two a DIFFERENT entity; a retype inside it goes through the hook below.
REX_EXTERN(rex_lhNetManager_SetupHenchman) {
  const uint32_t before = RefiiCoopPlayerTwoEntity(base);
  __imp__rex_lhNetManager_SetupHenchman(ctx, base);
  const uint32_t after = RefiiCoopPlayerTwoEntity(base);
  if (after != before) {
    RefiiCoopForgetBody(before);
    RefiiCoopForgetBody(after);
  }
  RefiiCoopRedress(&ctx, base);
}

// `sub_82365258(manager, entity, name)`: the entity (r4) keeps its address and gets new
// components, so everything latched on it is stale. Only a PLAYER body is ours to redress.
REX_EXTERN(rex_lhEntityManager_ChangeEntityType) {
  using refii::vfs::LoadGuestU8;
  const uint32_t entity = ctx.r4.u32;
  {
    // On a client, player two's body may hold the joiner's own LOCAL weapons when it is
    // retyped (`sub_82365258` keeps component 24 and its items; the host does not broadcast
    // this body's retype, 0x823652C0..0x823652CC, so the client makes the call itself). The
    // host then arms the body and its attach displaces them without destroying them on a
    // client (see the held-items hook below). The guest's own client arm releases and
    // destroys local held items before a body is armed; do that here.
    const uint32_t cm = CoopManager(base);
    const bool client = Plausible(cm) && LoadGuestU8(base, cm + 52u) != 0u &&
                        LoadGuestU8(base, cm + 54u) != 0u;
    if (client && Plausible(entity) && entity == RefiiCoopPlayerTwoEntity(base)) {
      DestroyLocalHeldItems(ctx, base, entity);
    }
  }
  __imp__rex_lhEntityManager_ChangeEntityType(ctx, base);
  if (!Plausible(entity) || !IsPlayerBody(base, entity)) {
    return;
  }
  RefiiCoopForgetBody(entity);
  RefiiCoopRedress(&ctx, base);
}

// --- Player two's own weapons ------------------------------------------
//
// `sub_8233D090` equips player two from two name tables — `sub_8233C640` (melee, 8 names) and
// `sub_8233C778` (ranged, 11) — both called from it alone (xrefs verified). It then creates,
// carries, equips and REPLICATES the weapons itself (`sub_82315AD0` → `sub_8238B9C8` →
// `sub_824FB450` → `sub_824AD108` → `sub_824AD698`). So answering the hero's own weapon names
// here is the whole feature, the same lever as the body: nothing on the equip path changes.
//
// The names come from the host: the equipped slots hold GDB record ids, which the host's GDB
// view turns into def names and the kit carries as strings. A name is written to a scratch
// frame for `sub_8222CED0`, exactly as the body override does.
namespace {
void AnswerWeaponName(PPCContext& ctx, uint8_t* base, const char* name) {
  const uint32_t frame = (ctx.r1.u32 - 0x100u) & ~0xFu;

  const uint32_t text = frame + 0x60u;  // above the callee's argument-save area; 0x81 bytes fit
  const std::size_t length = std::strlen(name);
  for (std::size_t i = 0; i < length && i < 0x80u; ++i) {
    base[text + i] = static_cast<uint8_t>(name[i]);
  }

  base[text + (length < 0x80u ? length : 0x80u)] = 0;

  PPCContext work = ctx;
  work.r1.u32 = frame;
  work.r4.u32 = text;
  work.r5.u32 = 0xFFFFFFFFu;
  __imp__rex_lhCharString_CtorFromCStr(work, base);
}
}  // namespace


REX_EXTERN(rex_lhNetManager_GetHenchmanMeleeWeaponName) {
  const char* name = RefiiCoopGetOwnHero() != 0 ? RefiiCoopMeleeName() : "";
  if (name == nullptr || name[0] == '\0') {
    __imp__rex_lhNetManager_GetHenchmanMeleeWeaponName(ctx, base);
    return;
  }

  AnswerWeaponName(ctx, base, name);
}

REX_EXTERN(rex_lhNetManager_GetHenchmanRangedWeaponName) {
  const char* name = RefiiCoopGetOwnHero() != 0 ? RefiiCoopRangedName() : "";
  if (name == nullptr || name[0] == '\0') {
    __imp__rex_lhNetManager_GetHenchmanRangedWeaponName(ctx, base);
    return;
  }

  AnswerWeaponName(ctx, base, name);
}

// `sub_82315AD0(playerMap, meleeNetId, rangedNetId)` arms player two from the name globals
// `dword_834975B4/B8`: it destroys every held item, creates the pair (host) or attaches the
// replicas by net id (client), and replicates (`sub_82317EA8`). Callers: `sub_8233D090`,
// `sub_8232E398`, `sub_824AAF10`. A hero body is armed by RECORD (the pump's EquipRecord and
// CoopAttachHeld), and a record with no def name cannot be answered by the tables, so for a
// hero body the routine is a no-op.
//
// ON THE HOST THE PAIR IT MAKES IS TRACKED, BY UID. The host branch creates both items through
// `sub_8238B9C8(table, name, -1)` (0x82315E84 / 0x82315F28; disc `sub_82391198`, 0x823521E4 /
// 0x82352288), which takes the uid from the EntityManager's counter — `**(*(game+0x58)+4)` + 4,
// post-incremented (0x8238BA60..0x8238BA78; disc 0x82391230..0x82391248), the field the save
// writes as "CurrentUniqueID" (`sub_82366EE8`; disc `sub_82362588`) — and `sub_8238B860` stores
// it at entity+0x14, low word 0 at +0x18 (0x8238B8CC / 0x8238B8D4). So the pair is the weapon
// entities whose uid lies between the counter before and after the call. The joiner sheathes it
// (only it is authoritative for the body, see coop_held_attach_rule.h); a joiner that leaves or
// is killed before its attach lands leaves the pair held by no one on the host, where nothing
// destroys it. A pair the next arm replaces, and any pair still loose when the partner goes
// (RefiiCoopDropLooseHenchmanPair, the pump's partner-gone edge), is destroyed here.
namespace {
uint32_t EntityUidCounter(uint8_t* base);
bool TrackHenchmanPair(const PPCContext& saved, uint8_t* base, uint32_t first, uint32_t end);
}  // namespace

REX_EXTERN(rex_lhCoop_ArmHenchmanWeapons) {
  using refii::vfs::LoadGuestU32;
  using refii::vfs::LoadGuestU8;
  if (RefiiCoopGetOwnHero() != 0) {
    static bool said = false;
    if (!said) {
      said = true;
      REXLOG_INFO("coop hooks: sub_82315AD0 (arm player two from the name tables) skipped — "
                  "player two is a hero armed by record");
    }
    ctx.r3.u32 = 0u;
    return;
  }

  const uint32_t cm = CoopManager(base);
  const bool host = Plausible(cm) && LoadGuestU8(base, cm + 52u) != 0u &&
                    LoadGuestU8(base, cm + 53u) != 0u;
  const uint32_t counter = host ? EntityUidCounter(base) : 0u;
  const PPCContext saved = ctx;
  const uint32_t first = counter != 0u ? LoadGuestU32(base, counter) : 0u;
  __imp__rex_lhCoop_ArmHenchmanWeapons(ctx, base);
  if (counter != 0u) {
    TrackHenchmanPair(saved, base, first, LoadGuestU32(base, counter));
  }
}

namespace {
/// One component off an entity by id, or 0: the presence bitmask (eight dwords at `+0x24`),
/// then the sorted stride-8 `{s32 id, u32 ptr}` vector at `+0x48..+0x4C`. The bridge's walk,
/// without its accelerator hop.
uint32_t ComponentOf(uint8_t* base, uint32_t entity, uint32_t id) {
  using refii::vfs::LoadGuestU32;
  if (!Plausible(entity) || id >= 256u) return 0u;
  const uint32_t present = LoadGuestU32(base, entity + 0x24u + (id / 32u) * 4u);
  if (((present >> (id % 32u)) & 1u) == 0u) return 0u;
  const uint32_t pairs = LoadGuestU32(base, entity + 0x48u);
  const uint32_t pairs_end = LoadGuestU32(base, entity + 0x4Cu);
  if (!Plausible(pairs) || pairs_end < pairs || (pairs_end - pairs) > 8u * 512u) return 0u;
  for (uint32_t at = pairs; at + 8u <= pairs_end; at += 8u) {
    if (LoadGuestU32(base, at) == id) {
      const uint32_t component = LoadGuestU32(base, at + 4u);
      return Plausible(component) ? component : 0u;
    }
  }
  return 0u;
}

/// The item entities a CECCarrying (component 24) holds: entries of 12 bytes at `+16..+20`,
/// holder at entry+8, item = `*holder`. Fills `out`, returns the count.
std::size_t HeldItems(uint8_t* base, uint32_t carrying, std::array<uint32_t, 16>& out) {
  using refii::vfs::LoadGuestU32;
  std::size_t count = 0u;
  const uint32_t begin = LoadGuestU32(base, carrying + 16u);
  const uint32_t end = LoadGuestU32(base, carrying + 20u);
  if (!Plausible(begin) || end < begin || (end - begin) > 12u * 64u) return 0u;
  for (uint32_t at = begin; at + 12u <= end && count < out.size(); at += 12u) {
    const uint32_t holder = LoadGuestU32(base, at + 8u);
    const uint32_t item = Plausible(holder) ? LoadGuestU32(base, holder) : 0u;
    if (Plausible(item)) out[count++] = item;
  }
  return count;
}

/// Whether an item entity is a replica of a host entity on THIS client: the replica map answers
/// the item for the net id in its CECNetwork (+120). A local item has no CECNetwork, carries no
/// id there, or is not in the map. `saved` is a context to call the lookup with.
bool IsReplicaHere(const PPCContext& saved, uint8_t* base, uint32_t item) {
  using refii::vfs::LoadGuestU32;
  const uint32_t network = ComponentOf(base, item, 91u);
  if (network == 0u) return false;
  const uint64_t id = (static_cast<uint64_t>(LoadGuestU32(base, network + 120u)) << 32) |
                      LoadGuestU32(base, network + 124u);
  const uint32_t cm = CoopManager(base);
  if (id == 0u || !Plausible(cm)) return false;
  PPCContext find = saved;
  find.r3.u32 = cm;
  find.r4.u64 = id;
  __imp__rex_lhNetManager_FindEntityByNetId(find, base);
  return find.r3.u32 == item;
}

/// The guest's own client-side pre-loop for a body that is about to be armed by the host
/// (`sub_82315AD0`, 0x82315d70..0x82315db8): every held item is released from its slot
/// (`sub_824FDA98(carrying, item)`) and destroyed (`sub_8238D828`). Done here for player two's
/// body on a client at the retype into their own hero, before the host's attach displaces the
/// joiner's local weapons onto the floor — and only for LOCAL items; a replica is the host's
/// to destroy. Returns how many were destroyed.
uint32_t DestroyLocalHeldItems(const PPCContext& saved, uint8_t* base, uint32_t entity) {
  using refii::vfs::LoadGuestU8;
  using refii::vfs::LoadGuestU32;
  const uint32_t carrying = ComponentOf(base, entity, 24u);
  if (carrying == 0u) return 0u;
  std::array<uint32_t, 16> held{};
  const std::size_t count = HeldItems(base, carrying, held);
  PPCFunc* release = refii::vfs::FindGuestFunction(refii::guest::Address(0x824FDA98u));
  uint32_t destroyed = 0u;
  for (std::size_t i = 0; i < count; ++i) {
    const uint32_t item = held[i];
    if ((LoadGuestU8(base, item + 144u) & 0x40u) == 0u) continue;
    if (IsReplicaHere(saved, base, item)) continue;
    if (release != nullptr) {
      PPCContext call = saved;
      call.r3.u32 = carrying;
      call.r4.u32 = item;
      release(call, base);
    }
    PPCContext destroy = saved;
    destroy.r3.u32 = item;
    __imp__rex_lhEntity_Destroy(destroy, base);
    REXLOG_INFO("coop hooks: local held item 0x{:08X} (record {:08X}) released and destroyed "
                "ahead of the retype, as the guest's own client arm does",
                item, LoadGuestU32(base, item + 28u));
    ++destroyed;
  }
  return destroyed;
}
/// The EntityManager's uid counter ("CurrentUniqueID"): `**(*(game+0x58)+4)` + 4, as
/// `sub_8238B9C8` reads it (0x8238BA5C..0x8238BA6C; disc 0x8239122C..0x8239123C). Address or 0.
uint32_t EntityUidCounter(uint8_t* base) {
  using refii::vfs::LoadGuestU32;
  const uint32_t root = LoadGuestU32(base, kGameRoot);
  const uint32_t game = Plausible(root) ? LoadGuestU32(base, root + 12u) : 0u;
  const uint32_t players = Plausible(game) ? LoadGuestU32(base, game + 0x58u) : 0u;
  const uint32_t slot = Plausible(players) ? LoadGuestU32(base, players + 4u) : 0u;
  const uint32_t manager = Plausible(slot) ? LoadGuestU32(base, slot) : 0u;
  return Plausible(manager) ? manager + 4u : 0u;
}

/// The weapon class the guest gives an item (`sub_824AD108` at 0x824AD254): component 179's
/// `+12`, 2..4 melee (1), 5..8 ranged (2), anything else 0. The pump's WeaponClassOf.
uint32_t WeaponClass(uint8_t* base, uint32_t item) {
  const uint32_t kind = ComponentOf(base, item, 179u);
  const uint32_t type = kind != 0u ? refii::vfs::LoadGuestU32(base, kind + 12u) : 0u;
  return type >= 2u && type <= 4u ? 1u : type >= 5u && type <= 8u ? 2u : 0u;
}

/// The entity the host's net manager answers for a uid {high, 0}, or 0.
uint32_t EntityByUid(const PPCContext& saved, uint8_t* base, uint32_t high) {
  const uint32_t cm = CoopManager(base);
  if (high == 0u || !Plausible(cm)) return 0u;
  PPCContext find = saved;
  find.r3.u32 = cm;
  find.r4.u64 = static_cast<uint64_t>(high) << 32;
  __imp__rex_lhNetManager_FindEntityByNetId(find, base);
  return Plausible(find.r3.u32) ? find.r3.u32 : 0u;
}

/// The henchman pair the host's arm made last, as uid high words ([0] melee, [1] ranged), 0 when
/// none. Guest thread only (the arm and the pump's partner-gone edge both run there).
uint32_t g_henchman_pair[2] = {0u, 0u};

/// Destroys each tracked henchman weapon that is loose (coop_held_attach_rule.h): alive, and its
/// carrier — the item's component 83 (`sub_824FB5D8` reads it, 0x824FB744..0x824FB840; disc
/// 0x82500F0C..0x82501004), a weak ref at +12 whose block is at +16
/// (`Lua_Carried_GetCarrierEntity` → `sub_82915080`, `lhSmartPtr_Deref`) — is not a live entity
/// whose component 24 holds it. Forgets the pair. Returns how many were destroyed.
uint32_t DropLooseHenchmanPair(const PPCContext& saved, uint8_t* base, const char* why) {
  using refii::vfs::LoadGuestU32;
  using refii::vfs::LoadGuestU8;
  uint32_t destroyed = 0u;
  for (uint32_t& high : g_henchman_pair) {
    const uint32_t item = EntityByUid(saved, base, high);
    high = 0u;
    if (item == 0u) continue;
    const bool alive = (LoadGuestU8(base, item + 144u) & 0x40u) != 0u;
    const uint32_t carried = ComponentOf(base, item, 83u);
    const uint32_t block = carried != 0u ? LoadGuestU32(base, carried + 16u) : 0u;
    const uint32_t carrier = Plausible(block) ? LoadGuestU32(base, block) : 0u;
    const bool carrier_alive = Plausible(carrier) && (LoadGuestU8(base, carrier + 144u) & 0x40u) != 0u;
    bool holds = false;
    if (carrier_alive) {
      const uint32_t carrying = ComponentOf(base, carrier, 24u);
      std::array<uint32_t, 16> held{};
      const std::size_t count = carrying != 0u ? HeldItems(base, carrying, held) : 0u;
      for (std::size_t i = 0; i < count; ++i) holds = holds || held[i] == item;
    }
    if (!refii::coop::HenchmanItemIsLoose(alive, carrier_alive, holds)) continue;
    PPCContext destroy = saved;
    destroy.r3.u32 = item;
    __imp__rex_lhEntity_Destroy(destroy, base);
    ++destroyed;
    REXLOG_INFO("coop hooks: henchman weapon 0x{:08X} (record {:08X}) held by no one — destroyed "
                "({})", item, LoadGuestU32(base, item + 28u), why);
  }
  return destroyed;
}

/// After the host's arm: the weapons it made are the entities with a uid in [first, end) and a
/// weapon class. A new pair replaces the tracked one, whose loose items are destroyed first.
bool TrackHenchmanPair(const PPCContext& saved, uint8_t* base, uint32_t first, uint32_t end) {
  using refii::vfs::LoadGuestU32;
  uint32_t made[2] = {0u, 0u};
  for (uint32_t high = first; high != end && high - first < 16u; ++high) {
    const uint32_t item = EntityByUid(saved, base, high);
    if (item == 0u || (refii::vfs::LoadGuestU8(base, item + 144u) & 0x40u) == 0u ||
        LoadGuestU32(base, item + 20u) != high) {
      continue;
    }
    const uint32_t kind = WeaponClass(base, item);
    if (kind != 0u && made[kind - 1u] == 0u) made[kind - 1u] = high;
  }
  if (made[0] == 0u && made[1] == 0u) return false;
  DropLooseHenchmanPair(saved, base, "replaced by the next arm");
  g_henchman_pair[0] = made[0];
  g_henchman_pair[1] = made[1];
  REXLOG_INFO("coop hooks: the host's henchman arm made melee uid {:08X}, ranged uid {:08X} — "
              "tracked until the joiner holds them or leaves", made[0], made[1]);
  return true;
}
}  // namespace

/// The partner is gone (the pump's partner-gone edge, CoopRedress): the henchman pair the host
/// made and no one holds is destroyed. Guest tick thread.
extern "C" void RefiiCoopDropLooseHenchmanPair(void* ctx, uint8_t* base) {
  if (ctx == nullptr || base == nullptr) return;
  DropLooseHenchmanPair(*static_cast<const PPCContext*>(ctx), base, "the partner is gone");
}

// `sub_824FEC48(carrying, msg)` — the held-items message off the net dispatcher
// (`sub_82392250`): type 1 resolves the u64 net id at msg+0x48 (`sub_82334A60`) and attaches
// it (`sub_824FB5D8`); anything else RELEASES the named slot (`sub_824FCB20`).
//
// NEITHER PATH DESTROYS ON A CLIENT. `sub_824FCB20` detaches the item and leaves it as a free
// world entity. An ATTACH onto an occupied slot empties it first through `sub_824FDB70`
// (0x824FB860..0x824FB878), which releases the occupant and destroys it only when
// netmgr+0x34 && +0x35 (0x824FDB80..0x824FDBE8) — the host. So when the host arms player two,
// a LOCAL weapon already in that slot on the joiner's copy of the body is displaced and left
// on the joiner's floor: the sword on the ice
// (run 2026-10-08 01:08, no release message for the body — the displacement happens inside
// the attach). The guest's own client branch of `sub_82315AD0` destroys every local held item
// before a body is armed (0x82315d8c/0x82315db8); the same is done here for whatever this
// message took off the body, attach or release: an item that left and is not a replica is
// destroyed. A REPLICA IS WHAT THE REPLICA MAP SAYS: the client's create handler stores the
// host's id at CECNetwork+120, so `sub_82334A60(netmgr, *(comp91+120))` answers the item
// itself; a local item has no CECNetwork, carries no id there, or is not in the map.
// Entity+144 bit 0x40 is ALIVE (set by the constructor `sub_8238B320`, cleared only by
// `sub_8238D828`), not "activated".
extern "C" int RefiiCoopIsHostWeapon(uint32_t body, uint32_t item);

REX_EXTERN(rex_CECHeldItems_OnNetMessage) {
  using refii::vfs::LoadGuestU32;
  using refii::vfs::LoadGuestU8;

  const uint32_t carrying = ctx.r3.u32;
  const uint32_t message = ctx.r4.u32;
  const uint32_t cm = CoopManager(base);
  const bool client = Plausible(cm) && LoadGuestU8(base, cm + 52u) != 0u &&
                      LoadGuestU8(base, cm + 54u) != 0u;
  const bool host = Plausible(cm) && LoadGuestU8(base, cm + 52u) != 0u &&
                    LoadGuestU8(base, cm + 53u) != 0u;

  // ON THE HOST, a held-items message for player two's body comes from the joiner, which
  // controls that body: its draw and sheathe are a release (`sub_824FCB20`, never destroys)
  // and an attach of the same replica, and run here so the host shows them. A release with
  // no attach after it leaves the host's weapon loose; the pump's CoopKeepHostWeapons puts it
  // back. Said, capped, so a run shows what the joiner sent.
  if (host && Plausible(message) && Plausible(carrying)) {
    const uint32_t owner = LoadGuestU32(base, carrying + 4u);
    if (Plausible(owner) && owner == RefiiCoopPlayerTwoEntity(base)) {
      const uint32_t type = LoadGuestU32(base, message + 4u);
      static uint32_t logged = 0u;
      if (logged < 64u) {
        ++logged;
        REXLOG_INFO("coop hooks: held-items message type {} from the joiner for player two's "
                    "body 0x{:08X}", type, owner);
      }

      // THE JOINER ARMS PLAYER TWO'S BODY; the host follows its attach (coop_held_attach_rule.h).
      //
      // A HENCHMAN'S PAIR IS THE GUEST'S OWN. `sub_82315AD0` (disc `sub_82351E30`) creates
      // "DefaultWeaponHenchman" / "DefaultRangedWeaponHenchman" on the host (0x82315DE0..0x82315F98,
      // disc 0x82352140..0x823522F8) and sheathes them only where `sub_82214AF8(comp91)` holds
      // (0x8231614C → 0x8231626C, disc 0x823524AC → 0x823525CC): on the joiner, not the host. The
      // joiner's attach is this message, and the guest's handler attaches it (0x824FECCC, disc
      // 0x82504494). Refusing it left a longsword (1A01F786) and a flintlock (7706C9BF) on the
      // host's floor at every henchman join (run 2026-11-27, six pairs).
      //
      // A HERO'S BODY IS ARMED BY RECORD on the host (EquipRecord, RefiiCoopIsHostWeapon), and the
      // joiner's own items carry ids from the JOINER's counter; resolved on the host
      // (`sub_82334A60`) such an id can name an unrelated host entity, which the attach would take
      // into player two's hand — destroying the weapon in the slot (`sub_824FDB70`) and tearing
      // the entity off its owner. For a hero, only the host's own weapons are attached; the host
      // re-arms a loose one (CoopKeepHostWeapons). A henchman holds no joiner-local item: the
      // guest's arm releases and destroys every held item on both machines first (0x82315D8C /
      // 0x82315DB8, disc 0x823520EC / 0x82352118) and the joiner then attaches replicas only.
      //
      // AN ATTACH OF WHAT THE SLOT ALREADY HOLDS IS ALREADY TRUE. `sub_824FB5D8` empties an
      // occupied slot first (`sub_824F9AC8` false → `sub_824FDB70`, 0x824FB860..0x824FB878),
      // and on the host that DESTROYS the occupant (`sub_8238D828`, 0x824FDB90..0x824FDBE8) —
      // the very item about to be attached when the joiner names the weapon the host already
      // armed player two with (CoopHeroEquip). The attach then holds a destroyed entity and the
      // allocator's lists break within a second (runs 2026-10-19 15:35 and 16:10: InsertFreeNode
      // and HeapPool_Alloc, each ~0.5 s after this message). Nothing to do: the state is the
      // message's already.
      if (type == 1u) {
        const uint64_t id = (static_cast<uint64_t>(LoadGuestU32(base, message + 0x48u)) << 32) |
                            LoadGuestU32(base, message + 0x4Cu);
        PPCContext find = ctx;
        find.r3.u32 = cm;
        find.r4.u64 = id;
        __imp__rex_lhNetManager_FindEntityByNetId(find, base);
        const uint32_t item = find.r3.u32;
        std::array<uint32_t, 16> held{};
        const std::size_t count = HeldItems(base, carrying, held);
        bool already_held = false;
        for (std::size_t i = 0; Plausible(item) && i < count; ++i) {
          already_held = already_held || held[i] == item;
        }
        const bool own_hero = RefiiCoopGetOwnHero() != 0;
        const auto answer = refii::coop::JoinerAttachToPlayerTwo(
            already_held, own_hero, own_hero && RefiiCoopIsHostWeapon(owner, item) != 0);
        if (answer == refii::coop::JoinerAttach::kAlreadyTrue) {
          static uint32_t skipped = 0u;
          if (skipped < 16u) {
            ++skipped;
            REXLOG_INFO("coop hooks: the joiner's attach of item 0x{:08X} to player two's body "
                        "is already true — not re-attached (re-attaching destroys it)", item);
          }
          return;
        }
        if (answer == refii::coop::JoinerAttach::kRefuse) {
          static uint32_t refused = 0u;
          if (refused < 16u) {
            ++refused;
            REXLOG_INFO("coop hooks: the joiner's attach to player two's body names net id "
                        "{:016X} → entity 0x{:08X} (record {:08X}, alive {}), not a weapon the host "
                        "armed player two with — refused",
                        id, item, Plausible(item) ? LoadGuestU32(base, item + 28u) : 0u,
                        Plausible(item) && (LoadGuestU8(base, item + 144u) & 0x40u) != 0u);
          }
          return;
        }
        static uint32_t followed = 0u;
        if (followed < 16u) {
          ++followed;
          REXLOG_INFO("coop hooks: the joiner's attach to player two's {} body names net id "
                      "{:016X} → entity 0x{:08X} (record {:08X}) — attached, as the guest does",
                      own_hero ? "hero" : "henchman", id, item,
                      Plausible(item) ? LoadGuestU32(base, item + 28u) : 0u);
        }
      }
    }
  }

  if (!client || !Plausible(message) || !Plausible(carrying)) {
    __imp__rex_CECHeldItems_OnNetMessage(ctx, base);
    return;
  }

  const uint32_t type = LoadGuestU32(base, message + 4u);
  std::array<uint32_t, 16> before{};
  const std::size_t had = HeldItems(base, carrying, before);
  const PPCContext saved = ctx;
  // The host's statement is applied, never echoed: on a body this client controls,
  // `sub_824FB5D8` / `sub_824FCB20` would broadcast the same attach or release back
  // (`sub_82214AF8`: client && CECNetwork+104), and an attach echoed onto the host's slot that
  // already holds that item empties it through `sub_824FDB70` — which destroys it there.
  const uint32_t owner = LoadGuestU32(base, carrying + 4u);
  const uint32_t network = Plausible(owner) ? ComponentOf(base, owner, 91u) : 0u;
  const uint8_t controlled = network != 0u ? LoadGuestU8(base, network + 104u) : 0u;
  if (network != 0u) base[network + 104u] = 0u;
  __imp__rex_CECHeldItems_OnNetMessage(ctx, base);
  if (network != 0u) base[network + 104u] = controlled;

  std::array<uint32_t, 16> after{};
  const std::size_t has = HeldItems(base, carrying, after);
  for (std::size_t i = 0; i < had; ++i) {
    const uint32_t item = before[i];
    bool still = false;
    for (std::size_t j = 0; j < has && !still; ++j) still = after[j] == item;
    if (still) continue;
    if ((LoadGuestU8(base, item + 144u) & 0x40u) == 0u) continue;  // already destroyed
    if (IsReplicaHere(saved, base, item)) continue;                  // the host owns it
    REXLOG_INFO("coop hooks: held-items {} from the host took LOCAL item 0x{:08X} (record {:08X}) "
                "off 0x{:08X} — destroyed, as the guest's own client arm does",
                type == 1u ? "attach" : "release", item, LoadGuestU32(base, item + 28u), carrying);
    PPCContext destroy = saved;
    destroy.r3.u32 = item;
    __imp__rex_lhEntity_Destroy(destroy, base);
  }
}

// REPLICA MOTION AND ANIMATION ON ONE TIMELINE — the guest's own transport, made to keep pace
// with itself.
//
// A networked entity's authority sends two independent streams and neither carries a sender
// time (IDA 2026-10-08):
//   TRANSFORM — `sub_822BCA68` (CECNetwork tick, under `sub_82214AF8`): message 5 when it moved
//   past a threshold, 6 once at rest, never more often than an interval in GAME TICKS (15 Hz):
//   `*(this+248) >= *(this+252) + interval` (signed, 0x822BCDB8..0x822BCDC8), 2 for a player,
//   5 / 8 for nearby and visible, 30 otherwise. The receiver (`sub_8239E9E0`) plays each sample
//   at arrival + 0.1 s, the replica tick lerps or extrapolates (to alpha 3), and the mover
//   (`sub_8239ED30`) sets the placement HALFWAY to that target each tick.
//   ANIMATION — `sub_8239CC80`: the blend tree goes out when its clip set changed and
//   `now_ms >= *(this+0x100) + 100/250/350` (hero / NPC / NPC past 25 units, 0x8239CE44..
//   0x8239CEC0), or unchanged after 500/750/1000 ms; its change test hashes clip handles only,
//   so a walk-to-run blend crosses only on that slow resend. The receiver plays a tree from its
//   own arrival (`sub_82563B78`), no delay; `sub_825656A8` floors sends at 75 ms.
// So the limbs ran ahead of a body that crawled behind its samples: measured, the idle clip
// began 0.45 s before player two's replica stopped; NPCs, at 5-8 ticks and 250 ms, did worse.
// Here:
//   - the transform sender is let through every tick — for the two players always, for anyone
//     else while it sent within the last 8 ticks (the guest's own near and visible classes) —
//     by backdating `this+252` so the guest's own test passes; thresholds and the at-rest
//     message are untouched, so a still body still sends nothing;
//   - the animation sender's last-send stamp (`this+0x100`, the guest's ms TickCount
//     `sub_822660B0`) is set a second back each tick, past every interval it tests, so each
//     tree goes out at the guest's own 75 ms floor — fresh weights, not only fresh clips;
//   - every replica's mover places the interpolated target exactly, behind the original's own
//     gates (|target - current|^2 >= flt_82099A80, dot <= flt_83320A74), so a setter runs
//     exactly when the original's would;
//   - the 0.1 s playback delay is the guest's; Godot holds network-sampled animation back by
//     the same 0.1 s (Engine_ClipAnimator.DelayNetworkLeaves), so limbs and body share it.
// The first version of this layer widened delays and dropped the mover's gates — a Havok
// phantom move for every replica every tick — and every run with it crashed on heap
// corruption. The hooks were audited register-clean against the binary (2026-10-08).
namespace {
/// Whether `entity` is one of the two player bodies — the entity manager's own slots, which
/// `sub_82232778` reads (`EM+104`/`+112`, objects at `+108`/`+116`).
bool IsPlayerBody(uint8_t* base, uint32_t entity) {
  using refii::vfs::LoadGuestU32;
  const uint32_t root = LoadGuestU32(base, kGameRoot);
  const uint32_t game = Plausible(root) ? LoadGuestU32(base, root + 12u) : 0u;
  const uint32_t holder = Plausible(game) ? LoadGuestU32(base, game + 88u) : 0u;
  const uint32_t slot = Plausible(holder) ? LoadGuestU32(base, holder + 4u) : 0u;
  const uint32_t manager = Plausible(slot) ? LoadGuestU32(base, slot) : 0u;
  if (!Plausible(manager)) return false;
  for (uint32_t at : {108u, 116u}) {
    const uint32_t object = at == 108u ? refii_bridge_hero_one_holder(base, manager) : LoadGuestU32(base, manager + at);
    if (Plausible(object) && LoadGuestU32(base, object) == entity) return true;
  }
  return false;
}

float GuestF32(uint8_t* base, uint32_t at) {
  const uint32_t bits = refii::vfs::LoadGuestU32(base, at);
  float value;
  std::memcpy(&value, &bits, sizeof(value));
  return value;
}
}  // namespace

REX_EXTERN(rex_CECNetwork_SendTransform) {
  using refii::vfs::LoadGuestU32;
  using refii::vfs::StoreGuestU32;

  const uint32_t net = ctx.r3.u32;
  if (Plausible(net)) {
    const uint32_t entity = LoadGuestU32(base, net + 4u);
    const uint32_t now = LoadGuestU32(base, net + 248u);
    const uint32_t last = LoadGuestU32(base, net + 252u);
    // Due by a tick but not yet by the guest's interval: backdate so its own test passes. 30
    // is its widest interval and `now - 30` must not wrap; a fresh component (last 0) is the
    // guest's. A player always; anyone else while in the guest's own near classes.
    const bool nearby = Plausible(entity) && (IsPlayerBody(base, entity) || now - last <= 8u);
    if (nearby && now >= 30u && last != 0u && now > last && now < last + 30u) {
      StoreGuestU32(base, net + 252u, now - 30u);
    }

    // The animation sender's stamp, a second back: every interval it tests has passed.
    static PPCFunc* const tick_count = refii::vfs::FindGuestFunction(refii::guest::Address(0x822660B0u));
    if (tick_count != nullptr) {
      PPCContext call = ctx;
      tick_count(call, base);
      const uint32_t now_ms = call.r3.u32;
      if (now_ms > 1000u) StoreGuestU32(base, net + 0x100u, now_ms - 1000u);
    }
  }

  __imp__rex_CECNetwork_SendTransform(ctx, base);
}

// THE OUTBOUND QUEUE'S REPLACE, done once. `sub_823355C0` (disc `sub_82317F98`) queues a
// message; types 5, 6, 0x35, 0x8B, 0xAF-0xBF replace a pending node with the same key. The node
// is {next, prev, key[3], buffer{ptr, size, c, d}}, 36 bytes (`sub_82341470`). The replace
// builds an empty temp buffer at r1+0x8C (`sub_8255B920`; an empty buffer still owns a block,
// `Render_AllocD3DObject` never returns null), copies key+temp as 7 words into node+8
// (0x823359D4..F4) so the node's ptr IS the temp's, frees the temp (0x823359FC), then
// `sub_8255B7D0(node+0x14)` frees that pointer again before refilling it (0x82335A60). The
// shipped game double-frees here; the old payload also leaks. Reached whenever a transform is
// queued while the last one for that object is still pending, which the paced sender above
// does every tick. Here: the key and the temp's c/d into the node, its own ptr/size kept for
// `sub_8255B7D0` to free once; then the temp's own destruction at 0x823359F8.
extern "C" uint8_t* RefiiGuestBase(void);

bool RefiiNetQueueReplaceOnce(PPCRegister& r1, PPCRegister& r31) {
  using refii::vfs::LoadGuestU32;
  using refii::vfs::StoreGuestU32;
  uint8_t* base = RefiiGuestBase();
  if (base == nullptr) return false;
  const uint32_t node = r31.u32;
  const uint32_t key = r1.u32 + 0x80u;
  const uint32_t temp = r1.u32 + 0x8Cu;
  for (uint32_t i = 0; i < 3u; ++i) StoreGuestU32(base, node + 8u + 4u * i, LoadGuestU32(base, key + 4u * i));
  StoreGuestU32(base, node + 0x1Cu, LoadGuestU32(base, temp + 8u));
  StoreGuestU32(base, node + 0x20u, LoadGuestU32(base, temp + 12u));
  return true;
}

// THE REPLICA MOVER: the original's two gates, then the target itself
// instead of halfway to it. Calls are the original's: vt+64 / vt+68 write 16 bytes through r3
// with `this` in r4 (0x8239EDA8, 0x8239EDC0); vt+100 takes `this` in r3 and the position in
// v1; vt+104 takes `this` in r3 and the quaternion in r4:r5, negated when it faces away from
// the current one (the original's vcmpgefp/vsel, 0x8239EE88..0x8239EEA4). The snap class
// (entity+0x40 bit 10, a matrix through vt+132) goes to the original.
REX_EXTERN(rex_CECNetwork_ApplyReplicaTarget) {
  using refii::vfs::LoadGuestU32;

  const uint32_t net = ctx.r3.u32;
  const uint32_t entity = Plausible(net) ? LoadGuestU32(base, net + 4u) : 0u;

  // A KNOCKED-DOWN REPLICA IS PLACED TOO, as shipped: each machine simulates its own copy of the
  // ragdoll (the network knockdown `sub_8268D970` snaps the placement to the sender and enables
  // the same ragdoll, `sub_82689FC8` → `sub_826F1080`), the owner's impulse (msg 11), root keyframe
  // (msg 15, every 5th frame) and end (msg 14) keep the copies together, and the transform stream
  // keeps running — the shipped mover has no knockdown test (TU1 / disc).
  const uint32_t placement = Plausible(entity) ? LoadGuestU32(base, entity + 124u) : 0u;
  const uint32_t vtable = Plausible(placement) ? LoadGuestU32(base, placement) : 0u;
  const bool snaps = Plausible(entity) && ((LoadGuestU32(base, entity + 64u) >> 10) & 1u) != 0u;
  PPCFunc* get_position = Plausible(vtable) ? refii::vfs::FindGuestFunction(LoadGuestU32(base, vtable + 64u)) : nullptr;
  PPCFunc* get_orientation = Plausible(vtable) ? refii::vfs::FindGuestFunction(LoadGuestU32(base, vtable + 68u)) : nullptr;
  PPCFunc* set_position = Plausible(vtable) ? refii::vfs::FindGuestFunction(LoadGuestU32(base, vtable + 100u)) : nullptr;
  PPCFunc* set_orientation = Plausible(vtable) ? refii::vfs::FindGuestFunction(LoadGuestU32(base, vtable + 104u)) : nullptr;
  if (snaps || get_position == nullptr || get_orientation == nullptr ||
      set_position == nullptr || set_orientation == nullptr) {
    __imp__rex_CECNetwork_ApplyReplicaTarget(ctx, base);
    return;
  }

  const PPCContext saved = ctx;
  // Scratch below the caller's frame. Every guest call made from here, the setters included, runs
  // with r1 at `frame`, so its own frame lies below the scratch: a setter called with the caller's
  // r1 built its frame over it and overwrote the current orientation read before it — the test
  // then compared garbage and skipped, so a replica's facing changed only while it stood still.
  const uint32_t frame = (saved.r1.u32 - 0x100u) & ~0xFu;
  const uint32_t current_position = frame + 0x60u;
  const uint32_t current_orientation = frame + 0x70u;
  const auto get = [&](PPCFunc* fn, uint32_t out) {
    PPCContext call = saved;
    call.r1.u32 = frame;
    call.r3.u32 = out;
    call.r4.u32 = placement;
    fn(call, base);
  };
  get(get_position, current_position);
  get(get_orientation, current_orientation);
  float position_now[3];
  float orientation_now[4];
  for (uint32_t i = 0; i < 3u; ++i) position_now[i] = GuestF32(base, current_position + 4u * i);
  for (uint32_t i = 0; i < 4u; ++i) orientation_now[i] = GuestF32(base, current_orientation + 4u * i);

  // v1 as the guest's `stvx` would store it: the recompiler keeps a vector byte-reversed
  // (VectorMaskL row 0), so reversing its bytes gives the big-endian memory image.
  uint8_t target_image[16];
  for (uint32_t i = 0; i < 16u; ++i) target_image[i] = saved.v1.u8[15u - i];
  float distance = 0.0f;
  for (uint32_t i = 0; i < 3u; ++i) {
    uint32_t word;
    std::memcpy(&word, target_image + 4u * i, sizeof(word));
    word = __builtin_bswap32(word);
    float target_axis;
    std::memcpy(&target_axis, &word, sizeof(target_axis));
    const float d = target_axis - position_now[i];
    distance += d * d;
  }

  // Target quaternion as the guest passes it: r4 = {x, y}, r5 = {z, w}, big-endian floats.
  const uint32_t target_bits[4] = {static_cast<uint32_t>(saved.r4.u64 >> 32), static_cast<uint32_t>(saved.r4.u64),
                                   static_cast<uint32_t>(saved.r5.u64 >> 32), static_cast<uint32_t>(saved.r5.u64)};
  float target[4];
  float dot = 0.0f;
  for (uint32_t i = 0; i < 4u; ++i) {
    std::memcpy(&target[i], &target_bits[i], sizeof(float));
    dot += orientation_now[i] * target[i];
  }

  if (!(distance < GuestF32(base, refii::guest::Address(0x82099A80u)))) {
    PPCContext call = saved;  // v1 already holds the target position
    call.r1.u32 = frame;
    call.r3.u32 = placement;
    set_position(call, base);
  }

  if (!std::isfinite(dot) || dot > GuestF32(base, refii::guest::Address(0x83320A74u))) {
    return;
  }
  uint32_t bits[4];
  for (uint32_t i = 0; i < 4u; ++i) {
    const float value = dot < 0.0f ? -target[i] : target[i];
    std::memcpy(&bits[i], &value, sizeof(uint32_t));
  }
  PPCContext call = saved;
  call.r1.u32 = frame;
  call.r3.u32 = placement;
  call.r4.u64 = (static_cast<uint64_t>(bits[0]) << 32) | bits[1];
  call.r5.u64 = (static_cast<uint64_t>(bits[2]) << 32) | bits[3];
  set_orientation(call, base);
}

// PLAYER TWO'S STAB RESPONSE REACHES THE BODY'S OWNER. The host decides a knockdown on player two's
// body (the joiner's predicted hit response runs with the knockdown bit masked: `sub_8295CEB8` sets
// listener+36 = 0x3FFD), so the stab response that follows it starts on the host's copy, and its
// Start sends only `if (sub_82214AF8(netComp))` — false on the host for the joiner's body. Recoil
// and stun need nothing: the joiner's own predicted run plays them and, being authoritative for
// its body, broadcasts them (0x82711258 / 0x82711708).
extern "C" REX_FUNC(__imp__rex_ActionKnockdownStabResponse_Start);

namespace {

std::atomic<bool> g_said_host_reaction{false};

/// The session's role bytes: in a session, and the host.
bool SessionRole(uint8_t* base, bool* host) {
  using refii::vfs::LoadGuestU32;
  const uint32_t root = LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = Plausible(root) ? LoadGuestU32(base, root + 0xCu) : 0u;
  const uint32_t session = Plausible(game) ? LoadGuestU32(base, game + 0x8Cu) : 0u;
  if (!Plausible(session) || refii::vfs::LoadGuestU8(base, session + 0x34u) == 0u) return false;
  *host = refii::vfs::LoadGuestU8(base, session + 0x35u) != 0u;
  return true;
}

/// The performer's network component (the weak reference at action+4, holder at +8).
uint32_t PerformerNet(uint8_t* base, uint32_t action, uint32_t* entity_out) {
  using refii::vfs::LoadGuestU32;
  const uint32_t holder = Plausible(action) ? LoadGuestU32(base, action + 8u) : 0u;
  const uint32_t entity = Plausible(holder) ? LoadGuestU32(base, holder) : 0u;
  *entity_out = entity;
  return Plausible(entity) ? refii_mod_component(base, entity, 91u) : 0u;
}

}  // namespace

// The stab response (message 20, Start `sub_826F9A50`, sent only under authority at 0x826f9f60):
// the receiver's dispatcher builds a different class (`sub_826FA240`, vtable off_820D1088) that
// never sends, so the host speaks for player two's body with no echo to guard against.
REX_EXTERN(rex_ActionKnockdownStabResponse_Start) {
  const uint32_t action = ctx.r3.u32;
  bool host = false;
  uint32_t entity = 0u;
  const uint32_t net = SessionRole(base, &host) ? PerformerNet(base, action, &entity) : 0u;
  const uint8_t owned_by_joiner = Plausible(net) ? refii::vfs::LoadGuestU8(base, net + 104u) : 0u;
  if (!host || owned_by_joiner == 0u || entity != refii_mod_player_hero(base, 2)) {
    __imp__rex_ActionKnockdownStabResponse_Start(ctx, base);
    return;
  }
  refii::vfs::StoreGuestU8(base, net + 104u, 0u);
  __imp__rex_ActionKnockdownStabResponse_Start(ctx, base);
  refii::vfs::StoreGuestU8(base, net + 104u, owned_by_joiner);
  if (!g_said_host_reaction.exchange(true)) {
    REXLOG_INFO("coop hooks: player two's stab response is sent to the joiner that owns the body");
  }
}
