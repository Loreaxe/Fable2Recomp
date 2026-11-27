// refii - FABLE 3'S ADOPTION POINTS: which save each user's hero comes from, stated to the
// achievement seam (achievement/achievement_runtime.h) before the guest reads that hero's
// achievements back; the drop-in join never adopting the save player one plays; and the joining
// player CHOOSING the save their hero comes from. Addresses are TU3 / disc.
//
// The read-back is per hero and runs once, when the hero's Player is set up:
// `lhSceneNode_SetupCamera` -> sub_8242D790 / sub_8240FF98 (0x8242D898) -> sub_823E3000 / sub_823C1A10,
// `lwz r4,0x28(r9)` (Player+0x28, the user) 0x823E3128, then `bl XUserCreateAchievementEnumerator`
// 0x823E3164 / 0x823C1B74 — so a binding is stated before the setup that follows it:
//
//   - Player one's load: the load mode's builder (rex_Game_BuildStart, mod_events_hooks.cpp).
//   - The drop-in join, sub_82459A80 / sub_8243A190 (r3 the joining pad's user: `mr r21,r3`
//     0x82459AA0 / `mr r20,r3` 0x8243A1B0): its hero is a new one until a save is adopted below. With no valid save the hero
//     is the generic one and a new save is made for it (sub_822F85A8 / sub_822F4D00, 0x8245AA84 /
//     0x8243B214), which the binding's hero then fills (the host attaches its character at that save's
//     first write).
//   - The adoption, sub_822FCB20 / sub_822F91D0 (r3 the profile manager, r4 the save's CCharString*,
//     r5 the user — the join `mr r5,r21` 0x8245A0C8 / `mr r5,r20` 0x8243A9A4).
//
// ONE SAVE SET: player two signs in to the shared profile (rexglue shadow xam_user.cpp), so the join
// lists the same saves player one's do (sub_822F5388 / sub_822F1B40 -> `j_XamContentCreateEnumerator(r3 =
// user, ..)` 0x822F540C; the content layer has one root for every user): `addi r5,r1,0x620+var_580` +
// `bl sub_822F5388` 0x82459DA0..0x82459DA8 / `addi r5,r1,0x210+var_1B0` + `bl sub_822F1B40`
// 0x8243A484..0x8243A48C fill a vector of save names (begin +4, end +8: `lwz r28,var_578` /
// `lwz r29,var_57C` 0x82459DAC..0x82459DB0, disc `lwz r11,var_1A8` / `lwz r31,var_1AC`
// 0x8243A490..0x8243A494). The join then walks it FROM THE BACK (`addi r31,r28,-4` 0x82459DC0, `addi
// r31,r31,-4` .. `cmplw r11,r29` / `bne` 0x82459EB4..0x82459EC0; disc `addi r25,r11,-4` 0x8243A4A0,
// 0x8243A570..0x8243A57C) and adopts the first save the qualify step passes: sub_822FBA38 / sub_822F80D0
// (r3 the manager, r4 the result, r5 the save's CCharString*, r6 the user; the call 0x82459E74 /
// 0x8243A558), whose "valid" is the result's +0x30 the join tests next (`lbz r10,0x620+var_590(r1)`
// 0x82459E78 / `lbz r10,0x210+var_160(r1)` 0x8243A55C). Before each call the walk resets the result:
// its three lists cleared by sub_82335018 / sub_82357A20 (`addi r3,r1,0x620+var_5B8` / `var_5AC` /
// `var_5A0` + `bl` 0x82459E30..0x82459E50, disc `var_188` / `var_17C` / `var_170` 0x8243A514..0x8243A534:
// result +8, +0x14, +0x20) and `stb +4`, `stw +0x2C`, `stb +0x30`, `stw +0x34`, `stb +0x38` zeroed
// (0x82459E3C, 0x82459E54..0x82459E68 / 0x8243A520, 0x8243A538..0x8243A54C) — every field the qualify
// writes (the SaveInfo reader sub_822F2FE8 / sub_822DDDF8: +4, the three package lists +8 / +0x14 /
// +0x20, BuildVersion +0x2C; the qualify itself +0x30 / +0x34 / +0x38, 0x822FBBDC..0x822FBC88).
//
// Inside the join, for the joining user (never the active user, player one's), the save player one
// plays is not valid: the manager's +0 (the name player one's save is written to: `sub_82228298((int
// *)a1, v26)` after the write, sub_822F8940 0x822F8BCC) or InitialSaveGameName (the save player one's
// game was loaded from).
//
// THE JOINING PLAYER CHOOSES. At the walk's first qualify call the hook qualifies every other save of
// the list itself — the guest's own qualify on the walk's own result object, reset between saves as the
// walk resets it — and asks the joining player (achievement/hero_save_choice.h: the guest thread waits
// for the answer, as it waits on the system UI the 360 showed over this join) to choose among the saves
// that passed, in the walk's order (the first is the one the walk would have taken), or a new hero.
// The walk then goes on as the guest's: the chosen save is qualified by the guest when the walk reaches
// it, and adopted by the guest's own adoption sub_822FCB20 with that save; every other save answers not
// valid. A new hero answers every save not valid: the guest's own no-save path, the generic hero and a
// new save of its own when the profile has room (`bl sub_822F5BD0` / `cmpwi r3,5` / `bge`
// 0x822F85F8..0x822F8600). With no other valid save, or no host to ask, nothing is asked and the walk
// is the guest's.
//
// A RE-MADE HERO KEEPS ITS SAVE. A drop-in is the START wrapper's first join, sub_824598D0 / sub_8243A0C8
// (`li r5,0` / `bl sub_82459A80` 0x824598E4..0x824598F0, disc 0x8243A0D8..0x8243A0E4; the wrapper's
// callers CGUIInput_vf6 0x8260142C / 0x825D6444 and the session's own TU3 0x823652F0 / 0x8239E6A0, disc
// 0x82447B80): there the player chooses. Every other join of that player re-makes the hero the drop-in
// made: the wrapper's second join (`li r5,1` / `bl sub_82459A80` 0x82459A34..0x82459A40, disc
// 0x8243A134..0x8243A140), the deferred one the busy world resumes (sub_8242AAD8 0x8242AB80 / 0x8242AC24,
// disc sub_8240D6D0 0x8240D770 / 0x8240D80C), TU3 0x8239F70C and disc 0x82448ADC. Those ask nothing while
// the guest still holds the choice: the manager's player-two user +0x148 is this user and its save +4 the
// chosen one (the adoption `sub_82228298(a1 + 1, a2)` / `a1[82] = a3` 0x822FCC1C..0x822FCC20; the new
// save `sub_82228298(a1 + 1, ..)` / `a1[82] = a2` 0x822F8868..0x822F886C) — or, for a new hero, a save
// the guest made after the answer (+4 changed since), else the new hero again; a drop-in that had nothing
// to choose is a new hero, one no host answered keeps the guest's own walk. The caller is told by the
// join's return address, as options_hooks.cpp tells the gamma reader's.

#include <cstdint>
#include <mutex>
#include <string>
#include <vector>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "core/payload_bridge.h"
#include "mod_seam.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_CoopJoin_SetupHero);
REX_EXTERN(__imp__rex_lhSaveManager_QualifyHeroSave);
REX_EXTERN(__imp__rex_lhSaveManager_AdoptHeroSave);

extern "C" uint64_t RefiiAchievementBindUser(uint32_t user_index, const char* save);
// The user a sign-in ask on this thread is for (rexglue shadow xam_user.cpp): the join's own sign-in round
// sub_8236B410 / disc `sub_82364B70` (`mr r4,r21` + `bl` 0x82459B30, disc `mr r4,r20` + `bl` 0x8243A234), asks for
// the joining pad's user.
extern "C" void RefiiXamSigninAskFor(int32_t user);
extern "C" int32_t RefiiHeroSaveChoiceAsk(uint32_t user, const char* names, uint32_t count, uint32_t listed,
                                          uint32_t present_ms);

namespace {

using namespace refii::fable3::seam;

/// InitialSaveGameName (TU3 0x83597958 / disc 0x83556E58): the save player one's game was loaded from.
const refii::guest::Addr kInitialSave{0x83597958u};
/// The active user (TU3 0x832DF7BC / disc 0x8329ED38): player one's.
const refii::guest::Addr kActiveUser{0x832DF7BCu};
/// The walk's list clear, sub_82335018 / sub_82357A20 (r3 the list).
const refii::guest::Addr kListClear{0x82335018u};
/// The qualify result's "valid" byte.
constexpr uint32_t kResultValid = 0x30u;
/// How long a question waits for a host to take it before the walk goes its own way.
constexpr uint32_t kPresentMs = 5000u;
/// The answers RefiiHeroSaveChoiceAsk gives besides an index.
constexpr int32_t kNewHero = -1;
constexpr int32_t kCancel = -3;

/// The return address of the START wrapper's first join (head note): a drop-in, where the player chooses.
const refii::guest::Addr kDropInReturn{0x824598F4u};

/// The drop-in join running on this thread.
struct Join {
  int32_t user = -1;
  /// The save-name vector the join's enumeration filled (begin +4, end +8), 0 until it has.
  uint32_t names = 0;
  enum class Mode { kUndecided, kGuest, kChosen, kNewHero, kCancelled } mode = Mode::kUndecided;
  std::string chosen;
};

thread_local Join t_join;

/// A user's choice at their drop-in, held for the joins that re-make their hero (head note): a save, a new
/// hero, or the guest's own pick (nothing to choose, or no host to ask).
struct Held {
  enum class Kind { kNone, kSave, kNewHero, kGuest } kind = Kind::kNone;
  /// The chosen save, or for a new hero the manager's +4 when it was chosen.
  std::string save;
};

std::mutex g_held_mutex;
Held g_held[4];

/// The raw text of the CCharString at `string` (a pointer to its {chars, ...} rep), or empty.
std::string SaveName(const uint8_t* base, uint32_t string) {
  const uint32_t rep = Word(base, string);
  const uint32_t chars = Plausible(rep) ? Word(base, rep) : 0u;
  if (!Plausible(chars)) {
    return {};
  }
  size_t n = 0;
  while (n < 64u && base[chars + n] != 0) {
    ++n;
  }
  return std::string(reinterpret_cast<const char*>(base + chars), n);
}

/// The save player one plays: the manager's +0, or InitialSaveGameName.
bool PlayerOnes(const uint8_t* base, uint32_t manager, const std::string& save) {
  return !save.empty() && (save == SaveName(base, manager) || save == SaveName(base, kInitialSave));
}

/// The qualify answered "not valid" without running it.
void NotValid(PPCContext& ctx, uint8_t* base, uint32_t result) {
  base[result + kResultValid] = 0u;
  ctx.r3.u64 = 0u;
}

/// The result reset as the walk resets it before each qualify (head note).
void Reset(PPCContext& ctx, uint8_t* base, uint32_t result) {
  for (const uint32_t list : {0x8u, 0x14u, 0x20u}) {
    PPCContext work = Frame(ctx);
    work.r3.u64 = result + list;
    CallAt(work, base, kListClear);
  }
  base[result + 0x4u] = 0u;
  StoreU32(base, result + 0x2Cu, 0u);
  base[result + kResultValid] = 0u;
  StoreU32(base, result + 0x34u, 0u);
  base[result + 0x38u] = 0u;
}

/// The held choice of `user` while the guest still holds it, as the mode its re-made hero takes; kUndecided when none.
Join::Mode HeldMode(const uint8_t* base, uint32_t manager, uint32_t user, std::string& save) {
  Held held;
  {
    std::lock_guard<std::mutex> lock(g_held_mutex);
    held = g_held[user];
  }
  if (held.kind == Held::Kind::kNone) {
    return Join::Mode::kUndecided;
  }
  if (held.kind == Held::Kind::kGuest) {
    return Join::Mode::kGuest;
  }
  const bool bound = Word(base, manager + 0x148u) == user;
  const std::string kept = SaveName(base, manager + 4u);
  if (held.kind == Held::Kind::kSave) {
    if (!bound || kept != held.save) {
      return Join::Mode::kUndecided;
    }
    save = kept;
    return Join::Mode::kChosen;
  }
  // A new hero: the save the guest made for it since (+4 changed, +0x148 this user), else no save holds it.
  if (bound && !kept.empty() && kept != held.save) {
    save = kept;
    return Join::Mode::kChosen;
  }
  return Join::Mode::kNewHero;
}

/// How the join's walk answers, decided at its first qualify call (head note).
void Decide(PPCContext& ctx, uint8_t* base, uint32_t manager, uint32_t result, uint32_t user) {
  Join& join = t_join;
  join.mode = Join::Mode::kGuest;
  if (user >= 4u) {
    return;
  }
  std::string kept;
  const Join::Mode held = HeldMode(base, manager, user, kept);
  if (held == Join::Mode::kGuest) {
    REXLOG_INFO("Refii co-op: user {}'s re-made hero is the guest's own pick, as at the drop-in", user);
    return;
  }
  if (held == Join::Mode::kChosen && !PlayerOnes(base, manager, kept)) {
    join.mode = Join::Mode::kChosen;
    join.chosen = kept;
    REXLOG_INFO("Refii co-op: user {}'s re-made hero keeps '{}', the save chosen for it", user, kept);
    return;
  }
  if (held == Join::Mode::kNewHero) {
    join.mode = Join::Mode::kNewHero;
    REXLOG_INFO("Refii co-op: user {}'s re-made hero stays the new hero no save holds", user);
    return;
  }

  const uint32_t begin = Word(base, join.names + 4u);
  const uint32_t end = Word(base, join.names + 8u);
  if (!Plausible(join.names) || !Plausible(begin) || end < begin || end - begin > 4u * 256u) {
    return;
  }

  std::vector<std::string> valid;
  std::string names;
  for (uint32_t slot = end; slot > begin;) {
    slot -= 4u;
    const std::string save = SaveName(base, slot);
    if (save.empty() || PlayerOnes(base, manager, save)) {
      continue;
    }
    Reset(ctx, base, result);
    PPCContext probe = ctx;
    probe.r5.u64 = slot;
    __imp__rex_lhSaveManager_QualifyHeroSave(probe, base);
    const bool passed = base[result + kResultValid] != 0u;
    REXLOG_INFO("Refii co-op: '{}' {} the guest's qualify for user {}'s hero", save, passed ? "passes" : "fails",
                user);
    if (passed) {
      valid.push_back(save);
      names += save;
      names += '\x1F';
    }
  }
  Reset(ctx, base, result);
  if (valid.empty()) {
    // The guest's walk makes the generic hero and, with room, a save for it: a new hero.
    REXLOG_INFO("Refii co-op: no save but player one's passes for user {}: nothing to choose", user);
    std::lock_guard<std::mutex> lock(g_held_mutex);
    g_held[user] = Held{Held::Kind::kNewHero, SaveName(base, manager + 4u)};
    return;
  }

  const uint32_t listed = (end - begin) / 4u;
  REXLOG_INFO("Refii co-op: user {} chooses their hero from {} save(s) or a new hero (the profile holds {})", user,
              valid.size(), listed);
  const int32_t choice = RefiiHeroSaveChoiceAsk(user, names.c_str(), static_cast<uint32_t>(valid.size()), listed,
                                                kPresentMs);
  Held answer{Held::Kind::kGuest, {}};
  if (choice >= 0 && static_cast<size_t>(choice) < valid.size()) {
    join.mode = Join::Mode::kChosen;
    join.chosen = valid[static_cast<size_t>(choice)];
    answer = Held{Held::Kind::kSave, join.chosen};
    REXLOG_INFO("Refii co-op: user {} chose '{}'", user, join.chosen);
  } else if (choice == kNewHero) {
    join.mode = Join::Mode::kNewHero;
    answer = Held{Held::Kind::kNewHero, SaveName(base, manager + 4u)};
    REXLOG_INFO("Refii co-op: user {} chose a new hero", user);
  } else if (choice == kCancel) {
    // Every save answers not valid, and past the walk the join takes its own cancel (RefiiF3JoinCancelled).
    join.mode = Join::Mode::kCancelled;
    answer = Held{};
    REXLOG_INFO("Refii co-op: user {} cancelled the join: no hero is made, no save adopted", user);
  } else {
    REXLOG_INFO("Refii co-op: no host answered user {}'s choice: the join takes the guest's own pick", user);
  }
  std::lock_guard<std::mutex> lock(g_held_mutex);
  g_held[user] = answer;
}

}  // namespace

namespace refii::fable3::binding {

/// The save names the guest's enumeration filled (rex_SaveGame_EnumerateNames, mod_lua_hooks.cpp: r3 the
/// user, r5 the vector): kept when it is the joining user's list inside a join on this thread.
void SavesListed(uint32_t user, uint32_t names) {
  if (t_join.user >= 0 && user == static_cast<uint32_t>(t_join.user)) {
    t_join.names = names;
  }
}

}  // namespace refii::fable3::binding

// The join's hero setup: the joining user's hero is a new one until sub_822FCB20 adopts a save; the
// qualify step below knows it is inside this join.
REX_EXTERN(rex_CoopJoin_SetupHero) {
  const uint32_t user = ctx.r3.u32;
  if (user < 4u) {
    RefiiAchievementBindUser(user, "");
    if (static_cast<uint32_t>(ctx.lr) == kDropInReturn) {
      // A drop-in: the player chooses again; no earlier choice is held for it.
      std::lock_guard<std::mutex> lock(g_held_mutex);
      g_held[user] = Held{};
    }
  }
  const Join outer = t_join;
  t_join = Join{};
  t_join.user = user < 4u ? static_cast<int32_t>(user) : -1;
  RefiiXamSigninAskFor(t_join.user);
  __imp__rex_CoopJoin_SetupHero(ctx, base);
  RefiiXamSigninAskFor(-1);
  t_join = outer;
}

// The qualify step: inside a join, for the joining user (never player one's), the save player one plays
// is not valid for its hero, and the save the joining player chose is the only other one that may be.
REX_EXTERN(rex_lhSaveManager_QualifyHeroSave) {
  const uint32_t manager = ctx.r3.u32;
  const uint32_t result = ctx.r4.u32;
  const uint32_t user = ctx.r6.u32;
  if (t_join.user < 0 || user != static_cast<uint32_t>(t_join.user) || user == Word(base, kActiveUser) ||
      !Plausible(result)) {
    __imp__rex_lhSaveManager_QualifyHeroSave(ctx, base);
    return;
  }

  if (t_join.mode == Join::Mode::kUndecided) {
    Decide(ctx, base, manager, result, user);
  }

  const std::string save = SaveName(base, ctx.r5.u32);
  if (PlayerOnes(base, manager, save)) {
    NotValid(ctx, base, result);
    REXLOG_INFO("Refii co-op: '{}' is the save player one plays — not player two's hero (user {})", save, user);
    return;
  }
  switch (t_join.mode) {
    case Join::Mode::kChosen:
      if (save != t_join.chosen) {
        NotValid(ctx, base, result);
        return;
      }
      break;
    case Join::Mode::kNewHero:
    case Join::Mode::kCancelled:
      NotValid(ctx, base, result);
      return;
    default:
      break;
  }
  __imp__rex_lhSaveManager_QualifyHeroSave(ctx, base);
}

// The adoption: user r5's hero comes from the save r4 names.
REX_EXTERN(rex_lhSaveManager_AdoptHeroSave) {
  const uint32_t user = ctx.r5.u32;
  const std::string save = SaveName(base, ctx.r4.u32);
  if (user < 4u && !save.empty()) {
    RefiiAchievementBindUser(user, save.c_str());
  }
  __imp__rex_lhSaveManager_AdoptHeroSave(ctx, base);
}

// THE JOIN CANCELLED. The guest's join has one way back: its sign-in rounds' warning box Cancel
// (sub_8236C588 / disc sub_82365898 answering 2), where it returns 0 at once (`li r3,0` / `addi r1,r1,0x620`
// / `b __restgprlr_15` 0x82459BC0..0x82459BD0, disc `li r3,0` / `addi r1,r1,0x210` / `b __restgprlr_16`
// 0x8243A2B0..0x8243A2C0) and the START wrapper makes nothing of a 0 (sub_824598D0: `if (!result || ..)`
// 0x82459910, no second join). The joining player's choice comes later, inside the walk; a Cancel there
// answers every save not valid, so the walk ends on its not-found arm with everything it made freed — each
// result's lists after its qualify (0x82459E88..0x82459EB0 / 0x8243A568..0x8243A56C) and the name vector
// (`bl sub_8229A7D8` / `bl sub_8221E388` 0x82459ED4..0x82459EDC, disc `bl sub_826CE160` 0x8243A584) — and
// from there (TU3 0x82459EE0 / disc 0x8243A588, the hero test that follows) the join takes that return.
bool RefiiF3JoinCancelled() {
  if (t_join.mode != Join::Mode::kCancelled) {
    return false;
  }
  REXLOG_INFO("Refii co-op: user {}'s join returns as the guest's own Cancel does: player two is not in", t_join.user);
  return true;
}
