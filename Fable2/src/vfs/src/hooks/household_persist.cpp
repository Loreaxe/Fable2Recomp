// refii — PLAYER TWO'S HOUSEHOLD STAYS IN THE HOST'S WORLD, as Fable 3's does: a spouse never moves
// worlds, and the world remembers which hero married them (Fable 3 by HeroUID; here by the hero's
// identity, core/hero_partners.cpp).
//
// Fable 2 saves a hero's family (CECPlayerFamily, comp136) and properties (CECPlayerProperties,
// comp69) with the hero entity in the world save; player two's hero is made anew each session and
// its HeroSave holds neither. So the host keeps them itself, with the guest's own serializers:
//   - the guest's entity snapshot (`sub_823694F8` / `sub_823696D0`) writes an entity into a memory
//     file through a zlib stream and a tagged-tree archive, and reads it back; the same chain here
//     carries hero two's two components (slot 9, `(comp, archive, kind 1, saving)`; kind 1 is the
//     world save's, the only one both write anything for: `if (a3 == 1)` in `sub_827B0B70` and
//     `sub_828D1A00`), under "RefiiHousehold" / "C136" / "C69";
//   - the bytes are a saved blob of the WORLD (player one's store), `refii.household.<id>`, written
//     beside the host's save with it;
//   - when that hero is back (a new hero two entity whose identity has a blob), the blob loads onto
//     its fresh components — comp69's load clears its lists first, comp136's appends, so only onto
//     an empty family — then each one's slot 12 runs as after a world load (`sub_8238D7A0`): the
//     family puts its spouses home, the properties pay what was earned while away (`sub_828CEAD0`);
//   - each spouse is told who they are married to again (`sub_827DAD80(villager comp26, hero)`: the
//     weak reference at +60 and the UID cache at +100, the villager's whole hero-facing state; its
//     own save keeps only the UID, which no longer resolves once the old hero two is gone). The
//     marriage itself is the family's record (`sub_821C6DB0` / `sub_827B3C88` read comp136 alone);
//   - each spouse's and child's feelings for player two (their base opinion axes, kept beside the
//     blob as `refii.hop.<id>.<uid>.<axis>`) seed player two's opinion set of them again;
//   - each child is given the new hero two as a parent. The birth (SexFunction.Begin, run in the
//     bed user's coroutine) puts the hero in the baby's comp26 parent lists (`Villager.AddParent`
//     → `sub_827DB578`: a weak reference at +68, the UID at +108); nothing hero-facing reads them
//     (that is comp136), but a child whose parents' weak references are all gone is orphan-checked
//     against hero one's family and evicted (`sub_827D9C00`, from the post-load and RemoveParent).
//     So the old hero's UID goes as RemoveParent removes one (the tail moved down, end − 8), a
//     dead weak reference through the vector's own erase (`sub_8236A190`), and AddParent adds the
//     new hero. The blob starts with the hero two it was stashed from: "RHH1", its UID (BE u64).
// ONLINE, THE JOINER'S HERO TWO IS A MIRROR. The family and property components are never sent by
// the guest's netcode (no CECNetwork message case touches comp 136 or 69), and the economy is the
// host's (a client's `sub_8242E578` neither changes nor sends). A client does not even build the two
// components — its build loop drops kinds 69 and 136 (`sub_8238DCC8` 0x8238DF3C's table) — so the
// joiner builds them through RefiiCoopClientKeepsComponent (hooks/src/coop_client_components_hooks.cpp);
// without it Restore below finds no component and the mirror stays empty. So the household lives on
// the host's copy of hero two, and the joiner's own hero two shows it: the blob reaches the joiner through
// the player store (ps.b), and whenever it changes the joiner empties its family (the guest's own
// erase per record, `sub_82472EC8`, and list clear `sub_82A14D90`; no guest routine empties the
// component and keeps it usable) and loads the blob over it — WITHOUT the post-load slot, which
// spawns children and moves spouses (the host's world does that). The joiner's own household
// ticks for hero two are off (mod_events.cpp, property_hooks.cpp); its household actions are the
// host's to run.
//
// THE HOST HERO'S PROPERTIES REACH THE JOINER THE SAME WAY. The joiner's copy of hero one has a comp69
// too (the client build keeps it, coop_client_components_hooks.cpp) but an empty one, so on the
// joiner every building the host's hero owns read as for sale and its owner as the occupant's
// (coop_estate_owner.cpp). The host stashes hero one's properties alone (no family) as a second blob,
// `refii.properties.one`, at the same beat, and the joiner loads it over its copy of hero one, again
// without the post-load slot; the joiner's own payout for that copy is off (property_hooks.cpp).
//
// ONLY A PLAYER TWO WHO BROUGHT A HERO HAS A HOUSEHOLD (RefiiCoopGetOwnHero). The plain henchman is
// no one's spouse or landlord: nothing is restored onto them, stashed for them or written into the
// host's save, and a hero who becomes the henchman mid-session stops being stashed at once — their
// kept household stays as it was last stashed, for when they come back as that hero. (Their identity
// answers 0 for the henchman too, core/hero_partners.cpp.)
//
// The host stashes hero two's household every few seconds while it is in the game, once it has
// settled (restored, or found nothing to restore), and before player one's save is written; at the
// same beat it pays either hero what they were owed while away (shared property, hero_partners.cpp).

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <functional>
#include <string>
#include <vector>

#include <rex/logging.h>
#include <rex/ppc/context.h>

#include "core/guest_build.h"
#include "core/payload_bridge_fable2.h"
#include "core/refii_mod.h"
#include "vfs_hooks_private.h"

/// Player two brought a hero (a character from a save) rather than standing as the plain henchman.
extern "C" int RefiiCoopGetOwnHero(void);
/// A player's owed half of shared rent and sales, paid (hooks/src/property_hooks.cpp).
extern "C" int32_t RefiiPropertyPayOwed(PPCContext& ctx, uint8_t* base, int32_t player);
// The mod API's household events (mod_events.cpp).
extern "C" void refii_mod_raise_partnership(PPCContext* ctx, uint8_t* base, int32_t kind, int32_t previous);
extern "C" void refii_mod_raise_household_restored(PPCContext* ctx, uint8_t* base, uint32_t hero, int32_t spouses);

namespace {

using refii::mods::CallGuest;
using refii::mods::CallVirtual;
using refii::mods::Frame;
using refii::mods::LoadU32;

constexpr uint32_t kFamily = 136u;
constexpr uint32_t kProperties = 69u;
constexpr uint32_t kVillager = 26u;
constexpr uint32_t kSerialize = 9u;
constexpr uint32_t kAfterLoad = 12u;
// The archive's slots: +4 enter (writer), +8 begin / try-enter (reader), +12 leave; a stream's +8
// releases it.
constexpr uint32_t kEnter = 1u;
constexpr uint32_t kTryEnter = 2u;
constexpr uint32_t kLeave = 3u;
constexpr uint32_t kRelease = 2u;
constexpr uint32_t kStreamBuffer = 0x10000u;
constexpr double kStashSeconds = 5.0;

bool Heap(uint32_t at) { return at >= 0x10000u && at < 0xE0000000u; }

constexpr uint8_t kMagic[4] = {'R', 'H', 'H', '1'};
constexpr size_t kHeader = 12u;

std::string BlobKey(uint64_t id) {
  char key[48];
  std::snprintf(key, sizeof(key), "refii.household.%012llX", static_cast<unsigned long long>(id));
  return key;
}

/// Guest scratch below the caller's frame: the stream holders, the archive (the reader's 192
/// bytes), the element names.
struct Scratch {
  PPCContext frame;
  uint32_t area = 0u;

  explicit Scratch(const PPCContext& ctx) : frame(Frame(ctx, 0x600u)) {
    area = (frame.r1.u32 + 0x80u) & ~0xFu;
  }
  uint32_t File() const { return area; }
  uint32_t Basic() const { return area + 0x08u; }
  uint32_t Packed() const { return area + 0x10u; }
  uint32_t Archive() const { return area + 0x20u; }
  uint32_t Top() const { return area + 0x100u; }
  uint32_t FamilyTag() const { return area + 0x120u; }
  uint32_t PropertiesTag() const { return area + 0x128u; }
  /// 0x100 bytes the body of a GuestArchiveSave / GuestArchiveLoad names its elements in.
  uint32_t Names2() const { return area + 0x140u; }

  void Names(uint8_t* base) const {
    std::memcpy(base + Top(), "RefiiHousehold", 15u);
    std::memcpy(base + FamilyTag(), "C136", 5u);
    std::memcpy(base + PropertiesTag(), "C69", 4u);
  }

  /// A guest call: r3 its result, or `failed` when the address is not a function.
  uint32_t Call(uint8_t* base, uint32_t address, uint32_t r3, uint32_t r4 = 0u, uint32_t r5 = 0u, uint32_t r6 = 0u,
                uint32_t failed = 0u) const {
    PPCContext work = frame;
    work.r3.u64 = r3;
    work.r4.u64 = r4;
    work.r5.u64 = r5;
    work.r6.u64 = r6;
    return CallGuest(work, base, refii::guest::Address(address)) ? work.r3.u32 : failed;
  }

  uint32_t Virtual(uint8_t* base, uint32_t object, uint32_t slot, uint32_t r4 = 0u, uint32_t r5 = 0u,
                   uint32_t r6 = 0u) const {
    PPCContext work = frame;
    work.r4.u64 = r4;
    work.r5.u64 = r5;
    work.r6.u64 = r6;
    return Heap(object) && CallVirtual(work, base, object, slot) ? work.r3.u32 : 0u;
  }

  /// A memory file's stream pair over `file` (CBasicStream `sub_82B4E3F0`, then the zlib stream
  /// `sub_82B4D990`), the archive built on it by `constructor`; the two holders released after, as
  /// the guest's snapshot does (the archive keeps its own reference).
  bool Open(uint8_t* base, uint32_t file, uint32_t constructor) const {
    Call(base, 0x82B4E3F0u, Basic(), file, 0u, kStreamBuffer);
    const uint32_t basic = LoadU32(base, Basic());
    Call(base, 0x82B4D990u, Packed(), basic, kStreamBuffer, 0u);
    const uint32_t packed = LoadU32(base, Packed());
    if (!Heap(basic) || !Heap(packed)) {
      return false;
    }
    Call(base, constructor, Archive(), packed);
    Virtual(base, packed, kRelease);
    Virtual(base, basic, kRelease);
    return true;
  }
};

/// A hero's family (unless `with_family` is false) and properties as bytes, empty when one is
/// missing or the guest's stream could not be made.
std::vector<uint8_t> Stash(const PPCContext& ctx, uint8_t* base, uint32_t hero, bool with_family = true) {
  const uint32_t family = with_family ? refii_mod_component(base, hero, kFamily) : 0u;
  const uint32_t properties = refii_mod_component(base, hero, kProperties);
  if ((with_family && family == 0u) || properties == 0u) {
    return {};
  }
  Scratch s(ctx);
  s.Names(base);
  s.Call(base, 0x82B4AF88u, s.File());  // an empty memory file, AddRef'd
  const uint32_t file = LoadU32(base, s.File());
  if (!Heap(file)) {
    return {};
  }
  std::vector<uint8_t> bytes;
  if (s.Open(base, file, 0x82C89940u)) {
    const uint32_t writer = s.Archive();
    s.Call(base, 0x82C8A910u, writer, s.Top());
    if (with_family) {
      s.Virtual(base, writer, kEnter, s.FamilyTag());
      s.Virtual(base, family, kSerialize, writer, 1u, 1u);
      s.Virtual(base, writer, kLeave);
    }
    s.Virtual(base, writer, kEnter, s.PropertiesTag());
    s.Virtual(base, properties, kSerialize, writer, 1u, 1u);
    s.Virtual(base, writer, kLeave);
    s.Call(base, 0x82C892E0u, writer);  // the top element's end and the end-of-stream token
    s.Call(base, 0x82C89880u, writer);  // the writer goes: the zlib stream flushes into the file
    const uint32_t data = LoadU32(base, file + 20u);
    const uint32_t size = LoadU32(base, file + 32u);
    if (Heap(data) && size != 0u && size < (4u << 20)) {
      const uint64_t uid = (static_cast<uint64_t>(LoadU32(base, hero + 20u)) << 32) | LoadU32(base, hero + 24u);
      bytes.assign(kMagic, kMagic + 4);
      for (int shift = 56; shift >= 0; shift -= 8) {
        bytes.push_back(static_cast<uint8_t>(uid >> shift));
      }
      bytes.insert(bytes.end(), base + data, base + data + size);
    }
  }
  s.Virtual(base, file, kRelease);
  return bytes;
}

/// The stashed household onto hero two's fresh components, then each one's post-load slot.
/// The hero two a blob was stashed from, 0 when it carries none.
uint64_t StashedHero(const std::vector<uint8_t>& blob) {
  if (blob.size() <= kHeader || std::memcmp(blob.data(), kMagic, 4u) != 0) {
    return 0u;
  }
  uint64_t uid = 0u;
  for (size_t i = 4; i < kHeader; ++i) {
    uid = (uid << 8) | blob[i];
  }
  return uid;
}

bool Restore(const PPCContext& ctx, uint8_t* base, uint32_t hero, const std::vector<uint8_t>& blob,
             bool after_load = true) {
  const uint32_t family = refii_mod_component(base, hero, kFamily);
  const uint32_t properties = refii_mod_component(base, hero, kProperties);
  const size_t skip = StashedHero(blob) != 0u ? kHeader : 0u;
  const std::vector<uint8_t> bytes(blob.begin() + static_cast<std::ptrdiff_t>(skip), blob.end());
  if (properties == 0u || bytes.empty()) {
    return false;
  }
  Scratch s(ctx);
  s.Names(base);
  s.Call(base, 0x82B4AF20u, s.File(), static_cast<uint32_t>(bytes.size()), 0u);  // a file that size
  const uint32_t file = LoadU32(base, s.File());
  const uint32_t data = Heap(file) ? LoadU32(base, file + 20u) : 0u;
  if (!Heap(data)) {
    if (Heap(file)) {
      s.Virtual(base, file, kRelease);
    }
    return false;
  }
  std::memcpy(base + data, bytes.data(), bytes.size());
  bool loaded = false;
  if (s.Open(base, file, 0x82C8BF70u)) {
    const uint32_t reader = s.Archive();
    if ((s.Call(base, 0x82C8A910u, reader, s.Top()) & 0xFFu) != 0u) {
      if (family != 0u && (s.Virtual(base, reader, kTryEnter, s.FamilyTag()) & 0xFFu) != 0u) {
        s.Virtual(base, family, kSerialize, reader, 1u, 0u);
        s.Virtual(base, reader, kLeave);
        loaded = true;
      }
      if ((s.Virtual(base, reader, kTryEnter, s.PropertiesTag()) & 0xFFu) != 0u) {
        s.Virtual(base, properties, kSerialize, reader, 1u, 0u);
        s.Virtual(base, reader, kLeave);
        loaded = true;
      }
      s.Virtual(base, reader, kLeave);
    }
    s.Call(base, 0x82C8B610u, reader);
  }
  s.Virtual(base, file, kRelease);
  for (const uint32_t component : {family, properties}) {
    if (loaded && after_load && component != 0u && base[component + 8u] == 0u) {
      s.Virtual(base, component, kAfterLoad);
    }
  }
  return loaded;
}

/// The players object (`**(*(*(0x83496AB8)+12)+88)+4)`), whose UID lookup is `sub_82264488`.
uint32_t Players(uint8_t* base) {
  const uint32_t root = LoadU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = Heap(root) ? LoadU32(base, root + 12u) : 0u;
  const uint32_t manager = Heap(game) ? LoadU32(base, game + 88u) : 0u;
  const uint32_t cell = Heap(manager) ? LoadU32(base, manager + 4u) : 0u;
  return Heap(cell) ? LoadU32(base, cell) : 0u;
}

struct Member {
  uint32_t uid_at = 0u;  // the UID's guest address in the family record
  uint64_t uid = 0u;
  uint32_t entity = 0u;  // 0 when not in the world now
  bool spouse = false;
};

/// Hero two's household: each record's spouse (records at *(family+20), the spouse's UID at
/// node+8) and its children (the record's list at node+20, a child's UID at child+8), each looked
/// up in the world by UID (`sub_82264488`).
std::vector<Member> Members(const PPCContext& ctx, uint8_t* base, uint32_t hero) {
  std::vector<Member> members;
  const uint32_t family = refii_mod_component(base, hero, kFamily);
  const uint32_t players = Players(base);
  if (family == 0u || !Heap(players)) {
    return members;
  }
  const auto add = [&](uint32_t uid_at, bool spouse) {
    Member member;
    member.uid_at = uid_at;
    member.uid = (static_cast<uint64_t>(LoadU32(base, uid_at)) << 32) | LoadU32(base, uid_at + 4u);
    member.spouse = spouse;
    PPCContext work = Frame(ctx);
    work.r3.u64 = players;
    work.r4.u64 = uid_at;
    if (CallGuest(work, base, refii::guest::Address(0x82264488u)) && refii_mod_entity_alive(base, work.r3.u32) != 0) {
      member.entity = work.r3.u32;
    }
    members.push_back(member);
  };
  const uint32_t head = LoadU32(base, family + 20u);
  uint32_t node = Heap(head) ? LoadU32(base, head) : 0u;
  for (uint32_t n = 0; Heap(node) && node != head && n < 16u; ++n, node = LoadU32(base, node)) {
    add(node + 8u, true);
    const uint32_t children = LoadU32(base, node + 20u);
    uint32_t child = Heap(children) ? LoadU32(base, children) : 0u;
    for (uint32_t c = 0; Heap(child) && child != children && c < 8u; ++c, child = LoadU32(base, child)) {
      add(child + 8u, false);
    }
  }
  return members;
}

/// The key of player two's (`id`) base opinion axis `axis` of the villager `uid`, a saved value of
/// the world.
std::string AxisKey(uint64_t id, uint64_t uid, uint32_t axis) {
  char key[64];
  std::snprintf(key, sizeof(key), "refii.hop.%012llX.%016llX.%u", static_cast<unsigned long long>(id),
                static_cast<unsigned long long>(uid), axis);
  return key;
}

/// A child's parent hero, from `old_uid` to `hero` (comp26: parent weak references +68 begin +72
/// end +76, 8-byte {vtable, handle}; parent UIDs +108 begin +112 end +116).
void Reparent(const PPCContext& ctx, uint8_t* base, uint32_t child, uint64_t old_uid, uint32_t hero) {
  const uint32_t villager = refii_mod_component(base, child, kVillager);
  if (villager == 0u) {
    return;
  }
  if (old_uid == ((static_cast<uint64_t>(LoadU32(base, hero + 20u)) << 32) | LoadU32(base, hero + 24u))) {
    old_uid = 0u;  // already this hero's (the mirror of a blob stashed from the same hero two)
  }
  const uint32_t uids_end = LoadU32(base, villager + 116u);
  for (uint32_t at = LoadU32(base, villager + 112u); old_uid != 0u && Heap(at) && at < uids_end; at += 8u) {
    if (((static_cast<uint64_t>(LoadU32(base, at)) << 32) | LoadU32(base, at + 4u)) != old_uid) {
      continue;
    }
    std::memmove(base + at, base + at + 8u, uids_end - at - 8u);
    refii::mods::StoreU32(base, villager + 116u, uids_end - 8u);
    break;
  }
  for (uint32_t guard = 0; guard < 8u; ++guard) {
    const uint32_t begin = LoadU32(base, villager + 72u);
    const uint32_t end = LoadU32(base, villager + 76u);
    uint32_t dead = 0u;
    for (uint32_t at = begin; Heap(at) && at < end; at += 8u) {
      const uint32_t handle = LoadU32(base, at + 4u);
      if (!Heap(handle) || !Heap(LoadU32(base, handle))) {
        dead = at;
        break;
      }
    }
    if (dead == 0u) {
      break;
    }
    Scratch s(ctx);
    s.Call(base, 0x8236A190u, s.Archive(), villager + 68u, dead);
  }
  PPCContext add = Frame(ctx);
  add.r3.u64 = villager;
  add.r4.u64 = hero;
  CallGuest(add, base, refii::guest::Address(0x827DB578u));
}

/// Each spouse married to this hero two again, and each member's feelings for them as kept.
/// Returns the spouses found in the world.
uint32_t Remarry(PPCContext& ctx, uint8_t* base, uint32_t hero, uint64_t id, uint64_t old_uid) {
  uint32_t found = 0u;
  for (const Member& member : Members(ctx, base, hero)) {
    if (member.entity == 0u) {
      continue;
    }
    if (!member.spouse) {
      Reparent(ctx, base, member.entity, old_uid, hero);
    }
    const uint32_t villager = refii_mod_component(base, member.entity, kVillager);
    if (member.spouse && villager != 0u) {
      PPCContext marry = Frame(ctx);
      marry.r3.u64 = villager;
      marry.r4.u64 = hero;
      if (CallGuest(marry, base, refii::guest::Address(0x827DAD80u))) {
        ++found;
      }
    }
    float axes[5] = {};
    bool kept = true;
    for (uint32_t axis = 0; axis < 5u && kept; ++axis) {
      const std::string key = AxisKey(id, member.uid, axis);
      kept = refii_bridge_player_get(1, key.c_str(), key.size(), &axes[axis]) != 0;
    }
    if (kept) {
      refii::vfs::SeedPlayerTwoOpinion(ctx, base, refii_mod_component(base, member.entity, 47u), axes);
    }
  }
  return found;
}

/// Player two's feelings of each member in the world now, kept with the world.
void KeepFeelings(const PPCContext& ctx, uint8_t* base, uint32_t hero, uint64_t id) {
  for (const Member& member : Members(ctx, base, hero)) {
    float axes[5] = {};
    const uint32_t opinion = member.entity != 0u ? refii_mod_component(base, member.entity, 47u) : 0u;
    if (opinion == 0u || !refii::vfs::PlayerTwoOpinionAxes(opinion, axes)) {
      continue;
    }
    for (uint32_t axis = 0; axis < 5u; ++axis) {
      const std::string key = AxisKey(id, member.uid, axis);
      refii_bridge_player_write(1, key.c_str(), key.size(), axes[axis], 0, 1);
    }
  }
}

/// The family component (comp136) emptied to a fresh one's state, its memory only: each record
/// erased as the guest's update erases one (`sub_82472EC8(out, list, list:node)`: unlink, record
/// dtor `sub_82472920`, free), the murdered-spouse list cleared (`sub_82A14D90`, sentinel kept),
/// the grown-up-child vector emptied, the serialized scalars back to the constructor's (+28, +36,
/// +40..+44 with CanShowFamilies 1, +48, SpouseInteractingWithHero +64). The gossip set (+52) is
/// left alone: clearing it reaches the gossip manager.
void EmptyFamily(const PPCContext& ctx, uint8_t* base, uint32_t hero) {
  const uint32_t family = refii_mod_component(base, hero, kFamily);
  if (family == 0u) {
    return;
  }
  Scratch s(ctx);
  for (uint32_t guard = 0; guard < 64u && LoadU32(base, family + 24u) != 0u; ++guard) {
    const uint32_t head = LoadU32(base, family + 20u);
    const uint32_t node = Heap(head) ? LoadU32(base, head) : 0u;
    if (!Heap(node) || node == head) {
      break;
    }
    PPCContext work = s.frame;
    work.r3.u64 = s.Archive();
    work.r4.u64 = family + 16u;
    work.r5.u64 = (static_cast<uint64_t>(family + 16u) << 32) | node;
    if (!CallGuest(work, base, refii::guest::Address(0x82472EC8u))) {
      break;
    }
  }
  s.Call(base, 0x82A14D90u, family + 72u);
  refii::mods::StoreU32(base, family + 92u, LoadU32(base, family + 88u));
  for (const uint32_t word : {28u, 36u, 48u, 64u, 68u}) {
    refii::mods::StoreU32(base, family + word, 0u);
  }
  for (const uint32_t byte : {40u, 41u, 42u, 43u}) {
    base[family + byte] = 0u;
  }
  base[family + 44u] = 1u;
}

}  // namespace

namespace {

bool FamilyEmpty(uint8_t* base, uint32_t hero) {
  const uint32_t family = refii_mod_component(base, hero, kFamily);
  const uint32_t head = family != 0u ? LoadU32(base, family + 20u) : 0u;
  return Heap(head) && LoadU32(base, head) == head;
}

struct Household {
  uint32_t hero = 0u;
  uint64_t id = 0u;
  bool settled = false;
  double stash_in = 0.0;
  double followers_in = 0.0;
  int32_t partnership = -1;  // the last seen, for the mod API's Partnership event
};

/// A hero's homes (comp69 records with the home flag +15); `marital` the marital home (+12) first.
uint64_t HomeOf(uint8_t* base, uint32_t hero, int32_t* count) {
  const uint32_t props = hero != 0u ? refii_mod_component(base, hero, kProperties) : 0u;
  const uint32_t head = props != 0u ? LoadU32(base, props + 28u) : 0u;
  uint64_t first = 0u;
  uint64_t marital = 0u;
  int32_t homes = 0;
  uint32_t node = Heap(head) ? LoadU32(base, head) : 0u;
  for (uint32_t n = 0; Heap(node) && node != head && n < 256u; ++n, node = LoadU32(base, node)) {
    const uint32_t record = LoadU32(base, node + 8u);
    if (!Heap(record) || base[record + 15u] == 0u) {
      continue;
    }
    const uint64_t uid = (static_cast<uint64_t>(LoadU32(base, record)) << 32) | LoadU32(base, record + 4u);
    ++homes;
    first = first != 0u ? first : uid;
    marital = marital != 0u || base[record + 12u] == 0u ? marital : uid;
  }
  if (count != nullptr) {
    *count = homes;
  }
  return marital != 0u ? marital : first;
}

/// PLAYER TWO'S FOLLOWERS, as the host's world has them, for the online joiner: the guest never
/// sends who follows whom, and the villagers' AI is the host's, so the joiner's hero two lists
/// nobody — and its bed (Follow.IsBeingFollowedBySexPartner), estate screen (the follow scans)
/// and wedding ask exactly that. The host publishes hero two's follow list (comp94: sentinel
/// *(follow+40), the follower's weak holder at node+12) as a world blob, `refii.followers.two`:
/// per follower its UID and flags — 1 engaged and 2 following for sex in player two's own opinion
/// set (block bytes 165 / 167: comp47 +771 / +775), 4 married to hero two (`sub_821C6DB0`).
void PublishFollowers(const PPCContext& ctx, uint8_t* base, uint32_t two) {
  const uint32_t follow = refii_mod_component(base, two, 94u);
  const uint32_t family = refii_mod_component(base, two, kFamily);
  std::vector<uint8_t> list;
  const uint32_t sentinel = follow != 0u ? LoadU32(base, follow + 40u) : 0u;
  uint32_t node = Heap(sentinel) ? LoadU32(base, sentinel) : 0u;
  for (uint32_t n = 0; Heap(node) && node != sentinel && n < 32u; ++n, node = LoadU32(base, node)) {
    const uint32_t weak = LoadU32(base, node + 12u);
    const uint32_t follower = Heap(weak) ? LoadU32(base, weak) : 0u;
    if (refii_mod_entity_alive(base, follower) == 0) {
      continue;
    }
    uint8_t flags = 0u;
    uint8_t block[REFII_OPINION_BLOCK_BYTES] = {};
    const uint32_t opinion = refii_mod_component(base, follower, 47u);
    if (opinion != 0u && refii_bridge_opinion_two_load(opinion, block) != 0) {
      flags |= block[165] != 0u ? 1u : 0u;
      flags |= block[167] != 0u ? 2u : 0u;
    }
    if (family != 0u) {
      PPCContext work = Frame(ctx);
      work.r3.u64 = family;
      work.r4.u64 = follower;
      if (CallGuest(work, base, refii::guest::Address(0x821C6DB0u)) && (work.r3.u32 & 0xFFu) != 0u) {
        flags |= 4u;
      }
    }
    for (int shift = 0; shift < 8; ++shift) {
      list.push_back(base[follower + 20u + static_cast<uint32_t>(shift)]);
    }
    list.push_back(flags);
  }
  if (list.empty()) {
    refii_bridge_player_blob_set(1, "refii.followers.two", nullptr, 0u);
  } else {
    refii_bridge_player_blob_set(1, "refii.followers.two", list.data(), static_cast<uint32_t>(list.size()));
  }
}

Household g_household;

/// The joiner's mirror: the hero two it fills and the blob it last loaded; the copy of the host's
/// hero one it fills with the host's properties, and that blob.
struct Mirror {
  uint32_t hero = 0u;
  std::vector<uint8_t> loaded;
  uint32_t one = 0u;
  std::vector<uint8_t> one_loaded;
};

Mirror g_mirror;

constexpr char kPropertiesOne[] = "refii.properties.one";

/// On an online joiner: the copy of the host's hero one carries the host's property list.
void DriveMirrorOne(PPCContext& ctx, uint8_t* base) {
  const uint32_t one = refii_mod_player_hero(base, 1);
  if (one == 0u || refii_mod_entity_alive(base, one) == 0) {
    g_mirror.one = 0u;
    g_mirror.one_loaded.clear();
    return;
  }
  if (one != g_mirror.one) {
    g_mirror.one = one;
    g_mirror.one_loaded.clear();
  }
  const int32_t size = refii_bridge_player_blob_get(1, kPropertiesOne, nullptr, 0u);
  if (size <= 0) {
    return;
  }
  std::vector<uint8_t> bytes(static_cast<size_t>(size));
  refii_bridge_player_blob_get(1, kPropertiesOne, bytes.data(), static_cast<uint32_t>(bytes.size()));
  if (bytes == g_mirror.one_loaded) {
    return;
  }
  if (!Restore(ctx, base, one, bytes, false)) {
    REXLOG_WARN("Refii household: the host hero's properties ({} bytes) did not load onto this machine's copy",
                bytes.size());
  }
  g_mirror.one_loaded = std::move(bytes);
}

/// On an online joiner: hero two shows the household the host keeps for them.
void DriveMirror(PPCContext& ctx, uint8_t* base) {
  DriveMirrorOne(ctx, base);
  const uint32_t two = refii_mod_player_hero(base, 2);
  if (two == 0u || refii_mod_entity_alive(base, two) == 0) {
    g_mirror.hero = 0u;
    g_mirror.loaded.clear();
    return;
  }
  if (two != g_mirror.hero) {
    g_mirror.hero = two;
    g_mirror.loaded.clear();
  }
  const uint64_t id = refii_bridge_hero_id(2);
  const std::string key = id != 0u ? BlobKey(id) : std::string();
  const int32_t size = id != 0u ? refii_bridge_player_blob_get(1, key.c_str(), nullptr, 0u) : -1;
  if (size <= 0) {
    return;
  }
  std::vector<uint8_t> bytes(static_cast<size_t>(size));
  refii_bridge_player_blob_get(1, key.c_str(), bytes.data(), static_cast<uint32_t>(bytes.size()));
  if (bytes == g_mirror.loaded) {
    return;
  }
  EmptyFamily(ctx, base, two);
  if (Restore(ctx, base, two, bytes, false)) {
    Remarry(ctx, base, two, id, StashedHero(bytes));
  }
  g_mirror.loaded = std::move(bytes);
}

void StashNow(const PPCContext& ctx, uint8_t* base) {
  if (!g_household.settled || g_household.hero == 0u || g_household.id == 0u) {
    return;
  }
  const std::vector<uint8_t> bytes = Stash(ctx, base, g_household.hero);
  if (!bytes.empty()) {
    refii_bridge_player_blob_set(1, BlobKey(g_household.id).c_str(), bytes.data(), static_cast<uint32_t>(bytes.size()));
  }
  KeepFeelings(ctx, base, g_household.hero, g_household.id);
  // Online, the host hero's properties for the joiner's copy of them (DriveMirrorOne).
  const uint32_t one = refii_mod_coop_mode(base) == 2 ? refii_mod_player_hero(base, 1) : 0u;
  const std::vector<uint8_t> ones = one != 0u ? Stash(ctx, base, one, false) : std::vector<uint8_t>{};
  if (!ones.empty()) {
    refii_bridge_player_blob_set(1, kPropertiesOne, ones.data(), static_cast<uint32_t>(ones.size()));
  }
}

}  // namespace

namespace refii::vfs {

/// Player two's household on the host: restored when their hero arrives, stashed while it stays.
/// Guest tick thread (RunModTicks), where guest calls are legal.
void DriveHouseholds(PPCContext& ctx, uint8_t* base, float seconds) {
  if (RefiiCoopGetOwnHero() == 0) {
    g_household = Household{};
    g_mirror = Mirror{};
    return;
  }
  // Online, the host's buildings as the host has them, for the joiner's screens.
  DriveEstateStatements(ctx, base, seconds);
  DriveVillagerUse(ctx, base, seconds);
  DriveJoinerShop(ctx, base, seconds);
  if (refii_mod_player_here(1) == 0 && refii_mod_player_here(2) != 0) {
    g_household = Household{};
    DriveMirror(ctx, base);
    return;
  }
  g_mirror = Mirror{};
  const uint32_t two = refii_mod_player_hero(base, 2);
  if (two == 0u || refii_mod_player_here(1) == 0 || refii_mod_entity_alive(base, two) == 0) {
    g_household = Household{};
    return;
  }
  if (two != g_household.hero) {
    g_household = Household{};
    g_household.hero = two;
  }
  if (!g_household.settled) {
    const uint64_t id = refii_bridge_hero_id(2);
    if (id == 0u) {
      return;  // their identity has not reached this machine yet
    }
    g_household.id = id;
    g_household.settled = true;
    const std::string key = BlobKey(id);
    const int32_t size = refii_bridge_player_blob_get(1, key.c_str(), nullptr, 0u);
    if (size > 0) {
      std::vector<uint8_t> bytes(static_cast<size_t>(size));
      refii_bridge_player_blob_get(1, key.c_str(), bytes.data(), static_cast<uint32_t>(bytes.size()));
      if (!FamilyEmpty(base, two)) {
        REXLOG_WARN("Refii household: player two ({:012X}) already has a family here; their kept household "
                    "is not loaded over it",
                    id);
      } else if (Restore(ctx, base, two, bytes)) {
        const uint32_t spouses = Remarry(ctx, base, two, id, StashedHero(bytes));
        REXLOG_INFO("Refii household: player two ({:012X}) is back; their household is restored ({} bytes, "
                    "{} spouse(s) found)",
                    id, bytes.size(), spouses);
        refii_mod_raise_household_restored(&ctx, base, two, static_cast<int32_t>(spouses));
      } else {
        REXLOG_WARN("Refii household: player two's ({:012X}) kept household did not load", id);
      }
    }
  }
  g_household.followers_in -= seconds;
  if (g_household.followers_in <= 0.0) {
    g_household.followers_in = 1.0;
    PublishFollowers(ctx, base, two);
    // Fable 3's partnership between the two heroes, raised to mods when it changes (the host's).
    // FABLE 3'S MARRIAGE NEEDS A HOME: each hero's home count, a session value of the world, so the
    // proposer's machine can tell (an online joiner has no copy of the host hero's properties).
    const uint32_t one = refii_mod_player_hero(base, 1);
    int32_t homes_one = 0;
    int32_t homes_two = 0;
    const uint64_t home_one = HomeOf(base, one, &homes_one);
    const uint64_t home_two = HomeOf(base, two, &homes_two);
    refii_bridge_player_write(1, "refii.homes.1", std::strlen("refii.homes.1"), static_cast<float>(homes_one), 0, 0);
    refii_bridge_player_write(1, "refii.homes.2", std::strlen("refii.homes.2"), static_cast<float>(homes_two), 0, 0);
    const int32_t now = refii_bridge_partnership();
    if (g_household.partnership >= 0 && now != g_household.partnership) {
      // Married: the marital home is theirs together (Fable 3: the host chooses it, co-owned with the
      // partner) — hero one's marital home, else their first home, else player two's.
      if (now == 2) {
        const bool ones = home_one != 0u;
        const uint64_t home = ones ? home_one : home_two;
        const uint64_t partner = refii_bridge_hero_id(ones ? 2 : 1);
        if (home != 0u && partner != 0u) {
          refii_bridge_coowner_set(home, partner);
        }
      }
      refii_mod_raise_partnership(&ctx, base, now, g_household.partnership);
    }
    g_household.partnership = now;
  }
  g_household.stash_in -= seconds;
  if (g_household.stash_in <= 0.0) {
    g_household.stash_in = kStashSeconds;
    StashNow(ctx, base);
    // Both heroes are here: whatever either was owed while away is theirs now.
    RefiiPropertyPayOwed(ctx, base, 1);
    RefiiPropertyPayOwed(ctx, base, 2);
  }
}

void HouseholdChanged() { g_household.stash_in = 0.0; }

std::vector<uint8_t> GuestArchiveSave(const PPCContext& ctx, uint8_t* base, const char* top,
                                      const std::function<void(const GuestArchive&)>& body) {
  Scratch s(ctx);
  const size_t top_size = std::strlen(top) + 1u;
  if (top_size > 0x20u) {
    return {};
  }
  std::memcpy(base + s.Top(), top, top_size);
  s.Call(base, 0x82B4AF88u, s.File());  // an empty memory file, AddRef'd
  const uint32_t file = LoadU32(base, s.File());
  if (!Heap(file)) {
    return {};
  }
  std::vector<uint8_t> bytes;
  if (s.Open(base, file, 0x82C89940u)) {
    const uint32_t writer = s.Archive();
    s.Call(base, 0x82C8A910u, writer, s.Top());
    body(GuestArchive{s.frame, writer, s.Names2(), true});
    s.Call(base, 0x82C892E0u, writer);  // the top element's end and the end-of-stream token
    s.Call(base, 0x82C89880u, writer);  // the writer goes: the zlib stream flushes into the file
    const uint32_t data = LoadU32(base, file + 20u);
    const uint32_t size = LoadU32(base, file + 32u);
    if (Heap(data) && size != 0u && size < (4u << 20)) {
      bytes.assign(base + data, base + data + size);
    }
  }
  s.Virtual(base, file, kRelease);
  return bytes;
}

bool GuestArchiveLoad(const PPCContext& ctx, uint8_t* base, const char* top, const std::vector<uint8_t>& bytes,
                      const std::function<void(const GuestArchive&)>& body) {
  const size_t top_size = std::strlen(top) + 1u;
  if (bytes.empty() || top_size > 0x20u) {
    return false;
  }
  Scratch s(ctx);
  std::memcpy(base + s.Top(), top, top_size);
  s.Call(base, 0x82B4AF20u, s.File(), static_cast<uint32_t>(bytes.size()), 0u);  // a file that size
  const uint32_t file = LoadU32(base, s.File());
  const uint32_t data = Heap(file) ? LoadU32(base, file + 20u) : 0u;
  if (!Heap(data)) {
    if (Heap(file)) {
      s.Virtual(base, file, kRelease);
    }
    return false;
  }
  std::memcpy(base + data, bytes.data(), bytes.size());
  bool found = false;
  if (s.Open(base, file, 0x82C8BF70u)) {
    const uint32_t reader = s.Archive();
    if ((s.Call(base, 0x82C8A910u, reader, s.Top()) & 0xFFu) != 0u) {
      found = true;
      body(GuestArchive{s.frame, reader, s.Names2(), false});
      s.Virtual(base, reader, kLeave);
    }
    s.Call(base, 0x82C8B610u, reader);
  }
  s.Virtual(base, file, kRelease);
  return found;
}

bool HouseholdIsMirror(uint8_t* base, uint32_t hero) {
  return hero != 0u && refii_mod_player_here(1) == 0 && refii_mod_player_here(2) != 0 &&
         hero == refii_mod_player_hero(base, 2);
}

/// Before player one's save is written: hero two's household as it stands goes into it.
void StashHouseholdForSave(PPCContext& ctx, uint8_t* base) {
  if (RefiiCoopGetOwnHero() != 0 && refii_mod_player_hero(base, 2) == g_household.hero && refii_mod_player_here(1) != 0) {
    StashNow(ctx, base);
  }
}

}  // namespace refii::vfs
