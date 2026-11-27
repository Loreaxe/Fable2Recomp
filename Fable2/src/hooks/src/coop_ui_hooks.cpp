// Co-op: each player's own expression wheel and tutorials, where the guest reads hero one.
//
// THE EXPRESSION WHEEL is one widget W = *(*(GUI+32)+60) that either player opens. Opening it
// stores the opener's player id at W+24 (`sub_826A9A60`, 0x826a9a80), and Accept's flag
// ("the owner is player one", `sub_826AA230`) already makes the chosen expression play on hero
// two (`sub_826AAD88`, 0x826aae1c). What still reads hero one whoever owns it:
//   - the list build `sub_826ABCD0` (`bl sub_8222C1B8` at 0x826abd80): the expression book
//     (component 154) and the inventory (13) the entries come from;
//   - the perform step `sub_826AA790` (0x826aa818): the inventory behind item entries;
//   - the sector-6 enable in `sub_826AB1F8` (inline players+108 read, 0x826ab238);
//   - the child flag W+31 in `sub_826A9A60` (inline read, 0x826a9aa4).
// Each answers the owner's hero here (RefiiCoopWheelHero). The list and the item component are
// what the wheel shows and uses; the other two keep the safety toggle and the child rule on the
// right hero.
//
// TUTORIALS run only on the session host and only for hero one: DisplayTutorial
// (`sub_821B5260`) refuses unless net+52 && +53 (join at 0x821b52bc), the queue tick
// `sub_8227BF90` the same (0x8227c008), and the tick holds or drops tutorials on hero one's
// Player component (+59/+64 at 0x8227c094, +40 at 0x8227c3e0). Fable 3 queues them on every
// machine for its own hero (`sub_821D11E0`, no host gate). On an online joiner both gates pass
// and both hero reads answer the joiner's own hero (hero two); the host-sent ones
// (ShouldSendToClient, messages 177/178/254) still arrive as shipped.
//
// JOBS: the job meter's handler `sub_823D11C8` takes Player 1's pad only (0x823d1228); while
// player two works a job (the core Lua's statement) it takes player two's.
//
// PROPERTY: the real-estate screen resolves its buyer as hero one (`sub_826B1A08`, stored at
// screen+144, 0x826b1a6c); opened from a sale sign player two used, it is player two.
//
// THE WHEEL'S BOOK, JOBS, PROPERTY AND ORBS ARE A HERO'S: each answers player two only while player two
// brought a hero (RefiiCoopGetOwnHero). The plain henchman keeps the shipped reads — hero one's
// expression book, player one's job pad, hero one buying, the orbs flying to hero one.
//
// NAMES: the game shows another player by the name that player's machine sent (the co-op join
// messages, the orb presence channel). Each machine now sends its pseudonym (the kernel's profile
// name), and every on-screen name answers RexNet's rule instead (RexNet §17.3.8): a friend's
// display name, anyone else's pseudonym, the local player's own display name.
//   - by XUID, where the game keeps one beside the name: the partner (`sub_822EF760`) and the orb
//     players (`sub_82560590`);
//   - by the pseudonym the sender's game sent, where it prints a message's name as it came: the
//     gift accepted box (`sub_823A1248`), the donation recipient line (`sub_8235A500`) and the
//     version-mismatch boxes (`sub_82359B40` host, `sub_823596C0` joiner); a name that is no known
//     player's pseudonym is shown as "Another player";
//   - the local player's own name where the game reads its profile name for its own screens
//     (RefiiOwnGamertag / RefiiOwnGamertagNarrow: the profile-space boxes and the co-op screen's
//     own panel). Every name is shown whole: none is cut to the game's 16-byte fields.

#include <atomic>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <string>
#include <unordered_map>

#include <rex/ppc/context.h>
#include <rex/ppc/func.h>

#include "core/guest_build.h"
#include "core/guest_state.h"
#include "core/payload_bridge_fable2.h"
#include "core/refii_mod.h"
#include "exp_orb_hooks.h"

extern "C" uint8_t* RefiiGuestBase(void);
extern "C" int RefiiCoopJobWorker(void);
/// Player two brought a hero (a character from a save) rather than standing as the plain henchman.
extern "C" int RefiiCoopGetOwnHero(void);
extern "C" int RefiiCoopTakeEstateBuyer(void);
extern "C" uint32_t RefiiCoopPlayerWithId(uint8_t* base, uint32_t id);

namespace {

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

/// This machine is the online joiner: net manager +52 and +54 set, +53 clear.
bool IsJoiner(uint8_t* base) {
  const uint32_t root = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = Plausible(root) ? refii::vfs::LoadGuestU32(base, root + 0xCu) : 0u;
  const uint32_t net = Plausible(game) ? refii::vfs::LoadGuestU32(base, game + 0x8Cu) : 0u;
  return Plausible(net) && refii::vfs::LoadGuestU8(base, net + 0x34u) != 0u &&
         refii::vfs::LoadGuestU8(base, net + 0x36u) != 0u &&
         refii::vfs::LoadGuestU8(base, net + 0x35u) == 0u;
}

/// The Player for an id (`sub_82264230`'s map, read): the PlayerManager at game+156.
uint32_t PlayerWithId(uint8_t* base, uint32_t id) { return RefiiCoopPlayerWithId(base, id); }

/// Player two's hero when it carries `component`, else 0.
uint32_t HeroTwoWith(uint8_t* base, uint32_t component) {
  const uint32_t hero = refii_mod_player_hero(base, 2);
  return hero != 0u && refii_mod_component(base, hero, component) != 0u ? hero : 0u;
}

}  // namespace

// A hero-one read inside the wheel (`hero`), with the wheel W in `wheel`: player two's hero
// when player two owns the open wheel.
void RefiiCoopWheelHero(PPCRegister& hero, PPCRegister& wheel) {
  uint8_t* base = RefiiGuestBase();
  if (base == nullptr || RefiiCoopGetOwnHero() == 0 || !Plausible(wheel.u32) ||
      refii::vfs::LoadGuestU32(base, wheel.u32 + 24u) != 2u) {
    return;
  }
  const uint32_t two = refii_mod_player_hero(base, 2);
  if (two != 0u) {
    hero.u64 = two;
  }
}

// The tutorial manager's host gates (`r11` = "host"): a joiner runs its own queue.
void RefiiCoopTutorialsHere(PPCRegister& r11) {
  uint8_t* base = RefiiGuestBase();
  if (base != nullptr && IsJoiner(base)) {
    r11.u64 = 1u;
  }
}

// The tutorial tick's hero-one reads (`r3`): on a joiner, the joiner's own hero.
void RefiiCoopTutorialHero(PPCRegister& r3) {
  uint8_t* base = RefiiGuestBase();
  if (base == nullptr || !IsJoiner(base)) {
    return;
  }
  const uint32_t own = refii_mod_player_hero(base, 2);
  if (own != 0u) {
    r3.u64 = own;
  }
}

// The job meter's Player (`r3`, Player 1): player two's while player two works the job.
void RefiiCoopJobMeterPlayer(PPCRegister& r3) {
  uint8_t* base = RefiiGuestBase();
  if (base == nullptr || RefiiCoopJobWorker() != 2 || RefiiCoopGetOwnHero() == 0) {
    return;
  }
  const uint32_t two = PlayerWithId(base, 2u);
  if (Plausible(two)) {
    r3.u64 = two;
  }
}

// The real-estate screen's buyer (`r6`, hero one) about to be stored at screen+144: player two
// when the sale sign was player two's.
void RefiiCoopEstateBuyer(PPCRegister& r6) {
  uint8_t* base = RefiiGuestBase();
  if (base == nullptr || RefiiCoopTakeEstateBuyer() != 2 || RefiiCoopGetOwnHero() == 0) {
    return;
  }
  // The screen reads the buyer's properties (69), use target (77) and purse (81).
  uint32_t two = HeroTwoWith(base, 69u);
  if (two != 0u && refii_mod_component(base, two, 77u) != 0u && refii_mod_component(base, two, 81u) != 0u) {
    r6.u64 = two;
  }
}

// PLAYER ONE'S USER INDEX, read with no null test. Four places look up Player 1
// (`sub_82264230(pm, 1)`) and read its Xbox user index (+0x28) the next instruction: the
// logbook's populate (0x829b871c, before XamUserGetSigninState), and three others that hand it
// to a user call (0x82444d6c, 0x824496dc, 0x826d9664). A joiner's player map has no key 1, and
// shipped, the henchman never opened a logbook — player two doing so crashed reading 0x28
// (2026-10-20). With no Player 1 here, the user is this machine's own: Player 2.
void RefiiCoopPlayerOneOrLocal(PPCRegister& r3) {
  uint8_t* base = RefiiGuestBase();
  if (base == nullptr || r3.u32 != 0u) {
    return;
  }
  const uint32_t local = PlayerWithId(base, 2u);
  if (Plausible(local)) {
    r3.u64 = local;
  }
}

// EXPERIENCE ORBS GO TO WHOEVER EARNED THEM. A kill's reward records its killer as a weak handle
// at reward+0x50 (`sub_82772260`, 0x82772830), but the orb spawned from it (`sub_827730F0`) keeps
// only the creature that died; the orb's update (`sub_8255C858`, host only) then flies to and
// credits hero one — a "sucker" that is hero two is redirected to hero one (0x8255cac8), and with
// none set the target is hero one outright (0x8255cadc). Player two's kills paid player one.
// The killer is noted per orb at spawn, and where every target path meets (0x8255cc24, r31 the
// target) an orb a hero earned flies to that hero; the credit (`sub_8255EF58` → comp164) follows.
namespace {
std::mutex g_orb_mutex;
std::unordered_map<uint32_t, uint32_t> g_orb_earner;  // orb component → earner's mod handle
}  // namespace

// After the orb's creator store (`r31` the orb component, `r14` the reward).
void RefiiCoopOrbEarner(PPCRegister& r31, PPCRegister& r14) {
  uint8_t* base = RefiiGuestBase();
  if (base == nullptr || !Plausible(r31.u32)) {
    return;
  }
  const uint32_t holder = Plausible(r14.u32) ? refii::vfs::LoadGuestU32(base, r14.u32 + 0x54u) : 0u;
  const uint32_t killer = Plausible(holder) ? refii::vfs::LoadGuestU32(base, holder) : 0u;
  const bool hero = killer != 0u &&
                    (killer == refii_mod_player_hero(base, 1) || killer == refii_mod_player_hero(base, 2));
  std::lock_guard<std::mutex> lock(g_orb_mutex);
  if (g_orb_earner.size() > 4096u) {
    g_orb_earner.clear();
  }
  if (hero) {
    g_orb_earner[r31.u32] = refii_mod_handle(killer);
  } else {
    g_orb_earner.erase(r31.u32);
  }
}

// The orb's target (`r31`, a hero) where its paths meet; `r30` set means "measure to the sucker".
// The orb component is the update's `this`, kept in `r21` (`mr r21, r3` at 0x8255c884).
void RefiiCoopOrbTarget(PPCRegister& r31, PPCRegister& r30, PPCRegister& orb) {
  uint8_t* base = RefiiGuestBase();
  if (base == nullptr || !Plausible(r31.u32)) {
    return;
  }
  const uint32_t one = refii_mod_player_hero(base, 1);
  const uint32_t two = refii_mod_player_hero(base, 2);
  if (two != 0u && RefiiCoopGetOwnHero() != 0 && (r31.u32 == one || r31.u32 == two)) {
    uint32_t earner = 0u;
    {
      std::lock_guard<std::mutex> lock(g_orb_mutex);
      const auto found = g_orb_earner.find(orb.u32);
      earner = found != g_orb_earner.end() ? refii_mod_handle_address(found->second) : 0u;
    }
    if ((earner == one || earner == two) && earner != r31.u32 && refii_mod_entity_alive(base, earner) != 0) {
      r31.u64 = earner;
      r30.u64 = 0u;
    }
  }
  refii::hooks::FixupExperienceOrb(base, r31.u32, orb.u32);
}

// --- THE NAMES THE GAME SHOWS FOR OTHER PLAYERS -------------------------------------------------

extern "C" REX_FUNC(__imp__rex_CXboxLive_PartnerName);
extern "C" REX_FUNC(__imp__rex_NLivePresence_PlayerName);
extern "C" REX_FUNC(__imp__rex_NLivePresence_GiftAccepted);
// TU1 only (the episodic donation text is not in the disc build): weak, so the disc payload loads.
extern "C" __attribute__((weak)) REX_FUNC(__imp__rex_lhCoopText_DonationRecipient);
extern "C" REX_FUNC(__imp__rex_lhCoopText_RefusedByHost);
extern "C" REX_FUNC(__imp__rex_lhCoopText_RefusedByJoiner);

namespace {

/// Guest heap objects may sit in the physical heaps above 0xC0000000.
bool PlausibleHeap(uint32_t at) { return at >= 0x10000u && at < 0xE0000000u; }

/// Builds the game's string at `out` from RexNet's name for the player with `xuid`, with the
/// readers' own constructor (`sub_8222CED0(out, text, -1)`; the disc build reads the -1 as the
/// length). False, and nothing built, when RexNet does not know the player.
bool BuildSafeName(PPCContext& ctx, uint8_t* base, uint32_t out, uint64_t xuid) {
  char name[64] = {};
  if (!PlausibleHeap(out) || refii_bridge_safe_name_for_xuid(xuid, name, sizeof(name)) == 0) {
    return false;
  }
  PPCContext work = refii::mods::Frame(ctx);
  const uint32_t text = (work.r1.u32 + 0x80u) & ~0xFu;
  std::memcpy(base + text, name, std::strlen(name) + 1u);
  work.r3.u64 = out;
  work.r4.u64 = text;
  work.r5.u64 = 0xFFFFFFFFu;
  if (!refii::mods::CallGuest(work, base, refii::guest::Address(0x8222CED0u))) {
    return false;
  }
  ctx.r3.u64 = out;
  return true;
}

}  // namespace

// The co-op partner's name (`sub_822EF760`: r3 the string to build, r4 CXboxLive): the first member
// record's name (+0x31) — its XUID beside it at +0x78, both copied from the join request at
// 0x822EE214..0x822EE224. Connecting-to, paused, henchman and kick lines all read it. TU1 0x822EF760.
REX_EXTERN(rex_CXboxLive_PartnerName) {
  const uint32_t live = ctx.r4.u32;
  const uint32_t begin = PlausibleHeap(live) ? refii::vfs::LoadGuestU32(base, live + 1156u) : 0u;
  const uint32_t end = PlausibleHeap(live) ? refii::vfs::LoadGuestU32(base, live + 1160u) : 0u;
  if (PlausibleHeap(begin) && end >= begin + 296u &&
      BuildSafeName(ctx, base, ctx.r3.u32, refii::vfs::LoadGuestU64(base, begin + 0x78u))) {
    return;
  }
  __imp__rex_CXboxLive_PartnerName(ctx, base);
}

// An orb player's name (`sub_82560590`: r3 the string to build, r4 the key whose +16 is the
// player's XUID — the presence map's key, the message's +0x0D, tested against the local XUID and
// XamUserAreUsersFriends at 0x822FB470..0x822FB4AC). The orb box's title reads it. TU1 0x82560590.
REX_EXTERN(rex_NLivePresence_PlayerName) {
  if (PlausibleHeap(ctx.r4.u32) && BuildSafeName(ctx, base, ctx.r3.u32, refii::vfs::LoadGuestU64(base, ctx.r4.u32 + 16u))) {
    return;
  }
  __imp__rex_NLivePresence_PlayerName(ctx, base);
}

namespace {

/// What a name another machine sent is shown as (core/coop_names.cpp); empty stays empty.
std::string ShownForSent(const std::string& sent) {
  char name[64] = {};
  if (sent.empty() || refii_bridge_safe_name_for_sent(sent.c_str(), name, sizeof(name)) != 0) {
    return sent.empty() ? sent : std::string(name);
  }
  return "Another player";
}

/// The text of the game's string object at `object` (a pointer to its rep {chars, length, ...}).
std::string StringObjectText(uint8_t* base, uint32_t object) {
  const uint32_t rep = PlausibleHeap(object) ? refii::vfs::LoadGuestU32(base, object) : 0u;
  const uint32_t chars = PlausibleHeap(rep) ? refii::vfs::LoadGuestU32(base, rep) : 0u;
  const uint32_t length = PlausibleHeap(rep) ? refii::vfs::LoadGuestU32(base, rep + 4u) : 0u;
  if (!PlausibleHeap(chars) || length > 256u) {
    return std::string();
  }
  return std::string(reinterpret_cast<const char*>(base + chars), length);
}

/// Runs `original` with the name argument in `arg` (a string object the callee releases) replaced
/// by one holding the name to show. The replacement is built on the guest stack below the caller's
/// frame (`sub_8222CED0(new, text, -1)`) and released by the callee as the original would have
/// been; the caller's own object is released here (`sub_82214DD8`, which zeroes it).
template <typename Original>
void WithShownNameArg(PPCContext& ctx, uint8_t* base, PPCRegister PPCContext::*arg, Original&& original) {
  const uint32_t object = (ctx.*arg).u32;
  const std::string sent = StringObjectText(base, object);
  const std::string shown = ShownForSent(sent);
  if (shown == sent) {
    original(ctx);
    return;
  }
  const uint32_t caller_sp = ctx.r1.u32;
  const PPCContext work = refii::mods::Frame(ctx, 0x100u);
  const uint32_t replacement = (work.r1.u32 + 0x80u) & ~0xFu;
  const uint32_t text = replacement + 0x10u;
  const std::string clipped = shown.substr(0, 63u);
  std::memcpy(base + text, clipped.c_str(), clipped.size() + 1u);
  refii::mods::StoreU32(base, replacement, 0u);
  PPCContext step = work;
  step.r3.u64 = replacement;
  step.r4.u64 = text;
  step.r5.u64 = 0xFFFFFFFFu;
  if (!refii::mods::CallGuest(step, base, refii::guest::Address(0x8222CED0u))) {
    original(ctx);
    return;
  }
  (ctx.*arg).u64 = replacement;
  ctx.r1.u64 = work.r1.u32;
  original(ctx);
  ctx.r1.u64 = caller_sp;
  step = work;
  step.r3.u64 = object;
  refii::mods::CallGuest(step, base, refii::guest::Address(0x82214DD8u));
}

}  // namespace

// "%1 has accepted your gift" (`sub_823A1248`: r4 the message; it reads only the donation id at
// +8 (0x823A1260, 0x823A12FC) and the sender's name at +12 as a C string (0x823A130C), a 16-byte
// field). The original is given a copy of the message — the id, then the name to show in full —
// built on the guest stack below the caller's frame. TU1 0x823A1248.
REX_EXTERN(rex_NLivePresence_GiftAccepted) {
  const uint32_t message = ctx.r4.u32;
  char sent[17] = {};
  if (PlausibleHeap(message)) {
    std::memcpy(sent, base + message + 12u, 16u);
  }
  const std::string shown = ShownForSent(sent);
  if (!PlausibleHeap(message) || shown == sent) {
    __imp__rex_NLivePresence_GiftAccepted(ctx, base);
    return;
  }
  const uint32_t caller_sp = ctx.r1.u32;
  const PPCContext work = refii::mods::Frame(ctx, 0x100u);
  const uint32_t copy = (work.r1.u32 + 0x80u) & ~0xFu;
  const std::string name = shown.substr(0, 63u);
  std::memcpy(base + copy, base + message, 12u);
  std::memcpy(base + copy + 12u, name.c_str(), name.size() + 1u);
  ctx.r4.u64 = copy;
  ctx.r1.u64 = work.r1.u32;
  __imp__rex_NLivePresence_GiftAccepted(ctx, base);
  ctx.r1.u64 = caller_sp;
}

// The donation recipient line (`sub_8235A500`: r3 the text, r4 the donor's name, released at
// 0x8235A5BC). TU1 0x8235A500.
REX_EXTERN(rex_lhCoopText_DonationRecipient) {
  if (&__imp__rex_lhCoopText_DonationRecipient == nullptr) {
    return;
  }
  WithShownNameArg(ctx, base, &PPCContext::r4, [base](PPCContext& run) { __imp__rex_lhCoopText_DonationRecipient(run, base); });
}

// The host's version-mismatch box for a joiner (`sub_82359B40`: r6 the joiner's name, from the
// join request, released on every path). TU1 0x82359B40.
REX_EXTERN(rex_lhCoopText_RefusedByHost) {
  WithShownNameArg(ctx, base, &PPCContext::r6, [base](PPCContext& run) { __imp__rex_lhCoopText_RefusedByHost(run, base); });
}

// The joiner's version-mismatch box (`sub_823596C0`: r6 the host's name, from the join response,
// released at 0x82359B00..0x82359B2C; the disc build's `sub_82339090` takes and releases it the
// same way without printing it). TU1 0x823596C0.
REX_EXTERN(rex_lhCoopText_RefusedByJoiner) {
  WithShownNameArg(ctx, base, &PPCContext::r6, [base](PPCContext& run) { __imp__rex_lhCoopText_RefusedByJoiner(run, base); });
}

// The local player's own name where the game reads its profile name for its own screens. The
// profile name is the pseudonym, because it travels; this is the name the player chose, shown to
// themselves, in full (a RexNet name is at most 24 bytes, FRIEND_CODE_NAME_MAX).
//   - RefiiOwnGamertag, just before the read name is used, r4 the buffer `XamUserGetName` filled:
//     64-byte stack arrays the game reads 16 bytes into — the second profile-space box
//     (`sub_826E1AC0`, 0x826E1B78) and the co-op screen's own panel (`sub_82882A28`, 0x82882A50);
//   - RefiiOwnGamertagNarrow, for the first profile-space box (`sub_8243BC48`, 0x8243BC98), whose
//     buffer is 16 bytes (r1+0x90): the name goes to r1+0x78, the 24-byte local below it that the
//     function first uses at its end (0x8243BD20), 40 bytes with the buffer, and r4 points there.
namespace {
constexpr uint32_t kOwnNameWide = 64u;
constexpr uint32_t kOwnNameNarrowAt = 0x78u;
constexpr uint32_t kOwnNameNarrow = 40u;
}  // namespace

void RefiiOwnGamertag(PPCRegister& r4) {
  uint8_t* base = RefiiGuestBase();
  char name[kOwnNameWide] = {};
  if (base == nullptr || !PlausibleHeap(r4.u32) || refii_bridge_own_name(name, sizeof(name)) == 0) {
    return;
  }
  std::memcpy(base + r4.u32, name, std::strlen(name) + 1u);
}

void RefiiOwnGamertagNarrow(PPCRegister& r1, PPCRegister& r4) {
  uint8_t* base = RefiiGuestBase();
  char name[kOwnNameNarrow] = {};
  if (base == nullptr || !PlausibleHeap(r1.u32) || refii_bridge_own_name(name, sizeof(name)) == 0) {
    return;
  }
  const uint32_t at = r1.u32 + kOwnNameNarrowAt;
  std::memcpy(base + at, name, std::strlen(name) + 1u);
  r4.u64 = at;
}

