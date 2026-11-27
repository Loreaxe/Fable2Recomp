// refii — PLAYER STATE'S SAVES: a hero's saved mod values go into the save the game writes that
// hero into, and come back when the game loads the hero from one (core/player_state.h).
//
// The game's own statements, read in the binary (TU1 / disc) rather than inferred:
//
//   Every save package is written by one of two writers, called from the write driver
//   (sub_82445398 / sub_824E8990) and from player two's (sub_82445680 / sub_824E8C78):
//     sub_824457E8 / sub_824E8DE0  the full write (main save, hero save, chapter, morphs)
//     sub_82445A28 / sub_824E9020  the hero save alone
//   Both take r3 the save manager, r4 the package's name (an LHString*: *r4 -> {char*, length}),
//   r5 the PLAYER NUMBER whose hero they serialise (player+0x24: `lwz r5,0x24(r27)` at 0x82445514
//   and 0x82445530, and 0x82445700 for player two), r6 the user index; both return their result
//   in r3 AFTER the package is closed (`bl sub_82449BD8` then `mr r3,r31`, 0x82445AA0). So on their
//   return, with r3 set, the package named by r4 holds player r5's hero — the moment to write.
//   Neither opens a package CREATE_ALWAYS (modes 4 and 3, 0x824458AC / 0x82445A58), so the file
//   written beside the game's survives every later save.
//
//   Loads:
//     sub_82446B10 / sub_824EA150  SaveGame_LoadFromDevice(save manager, name, fail-quest): the
//       main save, player one's. It assigns the name to SaveMgr+0 first (0x82446B34) and returns 1
//       when the save is loaded.
//     sub_824482E0 / sub_824EB910  the co-op hero load (save manager, user index, name): a save's
//       HeroSave for PLAYER TWO — a local player two's pick, or a joiner's own hero, picked on the
//       co-op screen before joining. It assigns the name to SaveMgr+4 once the stream is read
//       (0x82448408), and SaveMgr+4 is where player two's hero is then saved (sub_82445680).
//
// ACHIEVEMENTS PER CHARACTER (achievement/achievement_runtime.h): the same two loads are the
// guest's adoption points, "user U now plays the hero of save S", stated to the seam BEFORE the
// read-back that follows:
//   - SaveGame_LoadFromDevice opens the save as the signed-in user `dword_83496AF8` / disc
//     `dword_83496934` (`lis r11,dword_83496AF8@ha` 0x82446B38 / 0x824EA178); player one's Player is
//     bound later (sub_823E0628 -> the read-back sub_824DF1A8 / sub_824A5918), so the binding is
//     stated at entry. A new game is stated by the front end's start (mod_events.cpp,
//     Frontend_SetStartGameTarget with no save).
//   - LoadCoopHero opens it as user r4 (`stw r29,0x7E8(r30)` 0x82448304: SaveMgr+2024). Player two's
//     hero already has its Player and its read-back was made at the join, so after the pick the
//     guest's own refresh runs on hero two's stats — sub_824DF018 / sub_824A5788, what the co-op
//     screen's Finish sub_8243A4D0 calls (0x8243A7E4 / 0x8247B9DC): it takes the user out of the
//     barred set (`addi r3,..,0x810` + `bl sub_8244A2B8` 0x824DF188), clears stats+992
//     (`stb r29,0x3E0(r28)` 0x824DF190) and reads back (`bl sub_824DF1A8`). The binding's user is the
//     one the guest writes hero two's achievements for, Player two's +0x28 (`lwz r31,0x28(r11)`
//     0x824D9A74 / disc `lwz r29,0x28(r11)` 0x824A0A7C).
//   - The co-op screen's sign-in ask, sub_82882AD8 / sub_828867C0 (its one caller the right page's X,
//     sub_82438CF8 0x8243933C / sub_82479F18 0x8247A538): player two signs in to the shared profile
//     there (rexglue shadow xam_user.cpp) after its read-back barred it unsigned at the join
//     (sub_824DF1A8 0x824DF300 mask test -> 0x824DF424 into SaveMgr+2064), so the same refresh runs
//     once it answers signed in.
//
// ONE SAVE SET, TWO PLAYERS: player two picks from the saves player one picks from (the Y picker
// sub_8243AD48 / sub_8247C000 lists them for either user: one content root). The save player one plays
// is not on player two's list: the picker's list is the content enumeration it makes
// (sub_82443048 / sub_824E6728 -> XamContentCreateEnumerator, each entry's name pushed into the
// screen's +400 vector and its display name into +416, 0x82443184..0x824431B8), and inside the picker's
// own listing, in local co-op, the content layer leaves player one's save out (rexglue shadow
// content_manager.cpp, RefiiContentListingOmit). Player one's save is SaveMgr+0: the guest's own
// "the save player one is playing" is that name (sub_82439630 / sub_8247A828 copies SaveMgr+0,
// `lwz r4,0x94(r10)` + `bl sub_821F0240` 0x8243965C..0x82439664 / disc `bl sub_821F0108` 0x8247A85C,
// and compares it with each listed name). A save player one plays that still reaches hero two (an
// older build's pick) is never written with hero two: the write-back sub_82445680 / sub_824E8C78
// writes hero two into SaveMgr+4 (`addi r30,r31,4` 0x82445698, `mr r4,r30` + `bl
// lhSaveManager_WriteHeroPackage` 0x824456F4..0x82445704 / 0x824E8CEC..0x824E8CFC) with no test of
// which save that is, and player one's is SaveMgr+0 (LoadFromDevice 0x82446B34). That write is refused
// below, and the hero player two plays from it is a hero no save holds (its own character, never player
// one's).

#include <cstdint>
#include <string>

#include <rex/logging.h>

#include "core/guest_build.h"
#include "core/mod_values.h"
#include "core/payload_bridge_fable2.h"
#include "core/refii_mod.h"
#include "core/refii_mod_events.h"
#include "hooks/gui_screen_stub_support.h"
#include "hooks/ppc_externs.h"
#include "vfs_hooks_private.h"

PPC_EXTERN_FUNC(__imp__rex_lhSaveManager_WritePackage);
PPC_EXTERN_FUNC(__imp__rex_lhSaveManager_WriteHeroPackage);
PPC_EXTERN_FUNC(__imp__rex_lhSaveManager_LoadFromDevice);
PPC_EXTERN_FUNC(__imp__rex_lhSaveManager_LoadCoopHero);
PPC_EXTERN_FUNC(__imp__rex_lhGuiCoopScreen_SignInPlayerTwo);
PPC_EXTERN_FUNC(__imp__rex_lhGuiCoopScreen_ListPickerSaves);

extern "C" uint64_t RefiiAchievementBindUser(uint32_t user_index, const char* save);
extern "C" void RefiiContentListingOmit(const char* file_name);
// The user a sign-in ask on this thread is for (rexglue shadow xam_user.cpp).
extern "C" void RefiiXamSigninAskFor(int32_t user);

namespace refii::vfs {
uint32_t PlayerWithId(uint8_t* base, uint32_t id);
uint32_t EntityComponent(uint8_t* base, uint32_t entity, uint32_t id);
}  // namespace refii::vfs

namespace {

/// The signed-in user the main save is loaded as (TU1 0x83496AF8 / disc 0x83496934).
constexpr uint32_t kSignedInUser = 0x83496AF8u;
/// The stats component (57) of a hero's entity, the refresh's argument (sub_8243A4D0's lookup of
/// component 0x39 on `sub_8229EE78(player)`, 0x8243A69C..0x8243A7E4).
constexpr uint32_t kStatsComponent = 57u;
constexpr uint32_t kPlayerEntity = 0x8229EE78u;
constexpr uint32_t kStatsRefresh = 0x824DF018u;

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

/// The text of the LHString at `at` ({char*, length} behind one pointer), or empty.
std::string LhText(uint8_t* base, uint32_t at) {
  const uint32_t rep = Plausible(at) ? refii::mods::LoadU32(base, at) : 0u;
  if (!Plausible(rep)) {
    return {};
  }
  const uint32_t text = refii::mods::LoadU32(base, rep);
  const uint32_t length = refii::mods::LoadU32(base, rep + 4u);
  if (!Plausible(text) || length == 0u || length > 64u) {
    return {};
  }
  return std::string(reinterpret_cast<const char*>(base + text), length);
}

/// The SaveWritten event for a write that returned, `ok` the writer's own result
/// (core/refii_mod_events.h SaveWrittenEvent).
void RaiseSaveWritten(PPCContext& ctx, uint8_t* base, const std::string& name, uint32_t player, bool ok) {
  refii::mods::SaveWrittenEvent e{};
  PPCContext e_ctx = ctx;
  e.ctx = &e_ctx;
  e.base = base;
  e.player = static_cast<int32_t>(player);
  e.save = name.c_str();
  e.ok = ok;
  refii::mods::Dispatch(e);
}

/// After a package write: player `player`'s saved values go beside it.
void Written(PPCContext& ctx, uint8_t* base, uint32_t name_at, uint32_t player, bool ok) {
  const std::string name = LhText(base, name_at);
  if (player != 1u && player != 2u) {
    return;
  }
  if (!ok || name.empty()) {
    RaiseSaveWritten(ctx, base, name, player, ok);
    return;
  }
  refii_bridge_player_write_saved(static_cast<int32_t>(player), name.c_str());
  // The host stamps the package's sidecar (refii_save.json) with the mod set it was written
  // under: REFII_MOD_HOST_SAVE_KIND crosses the event drain whatever the mask says.
  std::string fields = "player=" + std::to_string(player) + ",name='";
  for (const char c : name) {
    if (c != '\'' && c != '\\' && c != '\n' && c != ',') {
      fields += c;
    }
  }
  fields += "'";
  refii::mods::PostEvent(REFII_MOD_HOST_SAVE_KIND, fields);
  // After the stamp's post, so the host has stamped the package when its save_written arrives.
  RaiseSaveWritten(ctx, base, name, player, true);
}

/// Local co-op (`game+0x11C == 2`, the guest's IsLocalCoopGame; game = `*(dword_83496AB8 + 12)`): both
/// heroes are this machine's and their saves one save set. An online joiner's SaveMgr+0 and +4 are its
/// own hero's, which it does write back.
bool LocalCoop(uint8_t* base) {
  const uint32_t root = refii::mods::LoadU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = Plausible(root) ? refii::mods::LoadU32(base, root + 12u) : 0u;
  return Plausible(game) && refii::mods::LoadU32(base, game + 0x11Cu) == 2u;
}

/// Hero two re-reads its achievements: the guest's refresh sub_824DF018 / sub_824A5788 on hero two's
/// stats (component 57 of Player 2's entity, `sub_8229EE78` / disc `sub_82455F20`).
void RefreshPlayerTwoAchievements(PPCContext& ctx, uint8_t* base, const char* why) {
  const uint32_t player = refii::vfs::PlayerWithId(base, 2u);
  if (!Plausible(player)) {
    return;
  }
  const refii::vfs::stub::Scratch s = refii::vfs::stub::Claim(ctx);
  const uint32_t entity = refii::vfs::stub::Call(ctx, base, s.frame, kPlayerEntity, {player});
  const uint32_t stats = Plausible(entity) ? refii::vfs::EntityComponent(base, entity, kStatsComponent) : 0u;
  if (stats == 0u) {
    return;
  }
  refii::vfs::stub::Call(ctx, base, s.frame, kStatsRefresh, {stats});
  REXLOG_INFO("Refii achievements: player two's hero re-read its achievements {} (stats 0x{:08X})", why, stats);
}

}  // namespace

// The full write: r4 the package's name, r5 the player number. TU1 0x824457E8.
REX_EXTERN(rex_lhSaveManager_WritePackage) {
  REX_FUNC_PROLOGUE();
  const uint32_t name_at = ctx.r4.u32;
  const uint32_t player = ctx.r5.u32;
  if (player == 1u) {
    // The world is in this package: player two's household as it stands goes with it.
    PPCContext stash = ctx;
    refii::vfs::StashHouseholdForSave(stash, base);
  }
  __imp__rex_lhSaveManager_WritePackage(ctx, base);
  Written(ctx, base, name_at, player, (ctx.r3.u32 & 0xFFu) != 0u);
}

// The hero save alone: the same arguments. TU1 0x82445A28. Player two's hero into the save player one
// plays (SaveMgr+0) is not written (see the head note): r3 = 0, the writer's "not written".
REX_EXTERN(rex_lhSaveManager_WriteHeroPackage) {
  REX_FUNC_PROLOGUE();
  const uint32_t manager = ctx.r3.u32;
  const uint32_t name_at = ctx.r4.u32;
  const uint32_t player = ctx.r5.u32;
  if (player == 2u && LocalCoop(base)) {
    const std::string target = LhText(base, name_at);
    if (!target.empty() && target == LhText(base, manager)) {
      REXLOG_WARN("Refii saves: player two's hero NOT written into '{}', the save player one plays", target);
      ctx.r3.u64 = 0u;
      RaiseSaveWritten(ctx, base, target, player, false);
      return;
    }
  }
  __imp__rex_lhSaveManager_WriteHeroPackage(ctx, base);
  Written(ctx, base, name_at, player, (ctx.r3.u32 & 0xFFu) != 0u);
}

// Player one's save loaded: the name is in SaveMgr+0 (r3+0). TU1 0x82446B10.
REX_EXTERN(rex_lhSaveManager_LoadFromDevice) {
  REX_FUNC_PROLOGUE();
  const uint32_t manager = ctx.r3.u32;
  const std::string asked = LhText(base, ctx.r4.u32);
  const uint32_t user = refii::mods::LoadU32(base, refii::guest::Address(kSignedInUser));
  if (!asked.empty() && user < 4u) {
    RefiiAchievementBindUser(user, asked.c_str());
  }
  __imp__rex_lhSaveManager_LoadFromDevice(ctx, base);
  const std::string name = (ctx.r3.u32 & 0xFFu) != 0u ? LhText(base, manager) : std::string();
  if (!name.empty()) {
    refii_bridge_player_load_saved(1, name.c_str());
  }
}

// Player two's hero loaded from a save: the name it was asked for (r5) is SaveMgr+4 once the
// stream was read; a failed open leaves SaveMgr+4 as it was. TU1 0x824482E0.
REX_EXTERN(rex_lhSaveManager_LoadCoopHero) {
  REX_FUNC_PROLOGUE();
  const uint32_t manager = ctx.r3.u32;
  const uint32_t content_user = ctx.r4.u32;
  const std::string asked = LhText(base, ctx.r5.u32);
  __imp__rex_lhSaveManager_LoadCoopHero(ctx, base);
  if (!asked.empty() && LhText(base, manager + 4u) == asked) {
    refii_bridge_player_load_saved(2, asked.c_str());
    // Hero two's user: its Player's +0x28 when hero two has one (local co-op), else the user the
    // save was opened as (an online joiner picks its own hero, as its own user, before it joins).
    const uint32_t player = refii::vfs::PlayerWithId(base, 2u);
    const uint32_t user = Plausible(player) ? refii::mods::LoadU32(base, player + 0x28u) : content_user;
    if (user < 4u) {
      // The save player one plays is never player two's character: that hero is one no save holds.
      const bool players_one = LocalCoop(base) && asked == LhText(base, manager);
      RefiiAchievementBindUser(user, players_one ? "" : asked.c_str());
      if (players_one) {
        REXLOG_INFO("Refii achievements: player two picked '{}', the save player one plays: its hero is "
                    "player two's own, written into no save", asked);
      }
      RefreshPlayerTwoAchievements(ctx, base, "after the pick");
    }
  }
}

// The co-op screen's sign-in ask for player two (r3 the user: the screen's +0x12C, player two's Player's
// +0x28, the pad that joined — `stw r11,0x12C(r31)` 0x82437724): that user is the one signed in, and hero
// two's achievements are read again for it. TU1 0x82882AD8 / disc 0x828867C0.
REX_EXTERN(rex_lhGuiCoopScreen_SignInPlayerTwo) {
  REX_FUNC_PROLOGUE();
  const uint32_t user = ctx.r3.u32;
  RefiiXamSigninAskFor(user < 4u ? static_cast<int32_t>(user) : -1);
  __imp__rex_lhGuiCoopScreen_SignInPlayerTwo(ctx, base);
  RefiiXamSigninAskFor(-1);
  if ((ctx.r3.u32 & 0xFFu) != 0u && user < 4u) {
    PPCContext work = ctx;
    RefreshPlayerTwoAchievements(work, base, "after signing in");
  }
}

// Player two's save list on the co-op screen (r3 the screen, r4 whether to ask for a device first):
// TU1 0x8243AD48 / disc 0x8247C000, reached from the right page's Y (sub_82438CF8 0x824393A0 /
// sub_82479F18 0x8247A59C) and the screen's update (sub_824353E0 0x82435B0C). It clears the screen's
// name and display-name vectors (`bl hkpConstrainedSystemFilter_hkpConstrainedSystemFilter` on +0x190
// 0x8243AD8C / 0x8247C044) and fills them from the content enumeration of +0x12C's user when that user
// is signed in, else of +0x120's (`bl sub_82443048` 0x8243AE20 / 0x8243AE74, disc `bl sub_824E6728`
// 0x8247C0D8 / 0x8247C12C). In local co-op the save player one plays (SaveMgr+0, SaveMgr =
// `*(*(dword_83496AB8 + 12) + 0x94)` 0x8243AD68..0x8243AD6C / disc dword_83496920 0x8247C01C..0x8247C024)
// is left out of that enumeration, so neither vector, the cards nor the rows the screen states hold it.
// An online joiner lists its own saves, all of them.
REX_EXTERN(rex_lhGuiCoopScreen_ListPickerSaves) {
  REX_FUNC_PROLOGUE();
  std::string omit;
  if (LocalCoop(base)) {
    const uint32_t root = refii::mods::LoadU32(base, refii::guest::Address(0x83496AB8u));
    const uint32_t game = Plausible(root) ? refii::mods::LoadU32(base, root + 12u) : 0u;
    const uint32_t manager = Plausible(game) ? refii::mods::LoadU32(base, game + 0x94u) : 0u;
    omit = LhText(base, manager);
  }
  if (omit.empty()) {
    __imp__rex_lhGuiCoopScreen_ListPickerSaves(ctx, base);
    return;
  }
  RefiiContentListingOmit(omit.c_str());
  __imp__rex_lhGuiCoopScreen_ListPickerSaves(ctx, base);
  RefiiContentListingOmit(nullptr);
  REXLOG_INFO("Refii co-op: player two's save list made without '{}', the save player one plays", omit);
}
