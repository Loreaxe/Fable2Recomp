// refii - FABLE 3'S STATS SCREEN (CStatsBox, TU3 vtable 0x820C6398 / disc 0x820C2E28) IS GODOT'S. Its
// presentation StatsScreen.bgf (screen 0xF) is never loaded (screen_cut_hooks.cpp); its three page
// writers are REPLACED here by their own computation less the element work, and every value they
// computed is stated. TU3 addresses; the disc twins are named, and every body below was read on both
// builds: the categories writer and the board line are the same instruction for instruction (address
// constants aside), the statistics writer differs in its online test only (below), the friends writer
// in its picture bookkeeping only (dropped with the pictures).
//
// THE BOX (base = the box): +0x1C / +0x20 the categories, 20-byte records {+0 the category's name tag,
// +4 / +8 its statistics' ids}, built by the Show's model sub_829B6DD8 / disc sub_8296E6B0 from the
// "StatCategoriesForStatScreen" table; +0x2C the entity reference (+0x30 its block, `lwz r11,0x30(r3)`
// 0x829B76AC); +0x34 the page (2 categories, 3 statistics, 4 friends); +0x40 the owner's player
// index; +0x44 the cursor; +0x48 the category; +0x4C the statistic the friends page is for; +0x50 the
// friends page's count. The cursor is Godot's (display_box_hooks.cpp `stats-row:<n>` stores +0x44 and
// runs the page's writer, these).
//
// CATEGORIES sub_829B7458 / disc sub_8296ED30. Window: start 0, or `cursor - 8` when the count
// `(+0x20 - +0x1C) / 20` exceeds 8 and the cursor is 8 or more (0x829B747C..0x829B74A0); eight rows
// (`cmplwi r28,8` 0x829B75F8). Behind `Pres` (0x829B74A8..0x829B74B4) it writes each row's slide and
// its name, the text of the record's tag `*(+0x1C + 20 * index)` (`lwzx` 0x829B75B4,
// sub_82312870 0x829B75BC), the A caption's tag `*dword_8359D358` / disc dword_8355C54C on
// `.Buttons.A_Button.Group.Text` (0x829B7618..0x829B7640) and the title's tag `*dword_8359D2F4` /
// disc dword_8355C4E8 (TEXT_GUI_STATS_SCREEN_TITLE) on `StatsBox.Title` (0x829B7664..0x829B768C).
// No game side effect. Stated: `@Screen.StatsList(box, 2, count, 8, start, online 0, title tag, A tag)`,
// `@Screen.StatsWindow`, and every category `@Screen.StatsRow(box, 2, index, tag, "", "", "", 0)`.
//
// STATISTICS sub_829B76A0 / disc sub_8296EF78, for the category +0x48:
//   - ONLINE, the guest's `v104 && v103`. The box entity's component 37 (`cmpwi r6,0x25` search
//     0x829B7788..0x829B77EC) gives the profile `*(*(c37+0x14)+0x28)`, -1 with no +0x14
//     (0x829B7808..0x829B781C). TU3 asks sub_8227D110(*(NET+0x28), profile) (0x829B782C): profile 0..3,
//     the profile record `*(table+8) + 32 * profile` byte 0 set AND byte 2 clear (0x8227D110..
//     0x8227D150). The disc inlines the test with byte 0 alone (`lbzx r30,r10,r8` 0x8296F11C). LISTED:
//     the gamer record sub_8242DF20 / disc sub_82410720 (out, c37) (0x829B7840) is among the session's
//     40-byte players `*(NET+0x18)` +0x7C..+0x80 with +0xC clear, by the 64-bit +0x10 (`ld` / `cmpld`
//     0x829B7844..0x829B7878); its LHString +0x20 destroyed (0x829B7888). NET = *0x835979F8.
//   - window rows 6 online, 9 offline (0x829B7898 / 0x829B78F4); start 0, or `cursor - rows + 1` when
//     the count `(+8 - +4) >> 2` exceeds the rows and the cursor reaches them (0x829B790C..0x829B792C).
//   - the player's gamer record R (0x829B7934..0x829B797C: vtable off_820812C8 / disc off_82080E58,
//     +8 and +0x10 zero, +0x18 dbl_82071BC8 / disc dbl_82072C48, an empty LHString at +0x20),
//     filled by sub_8218CAC8 / disc sub_8218CBB0 (*(*(world+4)+4), entity, &R) (0x829B7A90), world =
//     sub_8239FF30 / disc sub_82449318; nothing is written when it answers 0. Component 15 the stats
//     (`cmpwi r6,0xF` search 0x829B79EC..0x829B7A54).
//   - per row, statistic id `*(+4 + 4 * index)` (0x829B7D1C..0x829B7D24): its name tag sub_823E4C90 /
//     disc sub_823C3690 (c15, id) (0x829B7D2C); its value `c15+0xC`'s slot 0 (id) (0x829B7D88..
//     0x829B7D9C); the value's text sub_82304950 / disc sub_82300020 (out, value) then sub_823CE600 /
//     disc sub_823AC0A8 (id, &text) (0x829B7DD4 / 0x829B7DE0: two statistics are a d/h/m time).
//     Online: the leaderboard ids sub_823EAD78 / disc sub_823C9678 (out, c15, id) (0x829B7E80), the
//     friends' ranking sub_829B8188 / disc sub_8296FA78 (&R, value, &ids, &vector) (0x829B7EA4), the
//     rank 1 + the index of R's id (sub_8245F278: R+0x10 when set, else R+8) among its 16-byte
//     entries (0x829B7EB8..0x829B7F00), the vector freed (0x829B7F10); for the cursor's row with a
//     leaderboard (`ids[0] != -1` 0x829B7FA0..0x829B7FA8) the board around the player sub_829B8360 /
//     disc sub_8296FC50 (box, row element, id, &ids, &R, value) (0x829B7FC0). Offline: the rank is the
//     text of `*dword_8359D360` / disc dword_8355C554 (0x829B7FFC..0x829B8014).
//   - the A caption: `*dword_8359D35C` / disc dword_8355C550 shown on page 3 online, else hidden
//     (0x829B807C..0x829B8108); the title the category's tag (0x829B810C..0x829B8148).
//   - R's LHString destroyed at every exit (0x829B8158..0x829B8178).
// Kept as the guest computes it, through its own calls; the rank and the board for the window's rows
// only, as the guest asks them. Dropped: the slides, the row arrows, the hiding of rows 6..8
// (sub_829B97E0) and the commits. Stated: `@Screen.StatsList(box, 3, count, rows, start, online,
// title tag, A tag)`, `@Screen.StatsWindow`, `@Screen.StatsRow(box, 3, index, name tag, "", value,
// rank, rank tag)` for every statistic.
//
// THE BOARD LINE sub_829B8C48 / disc sub_82970538 (box, row element, LHString* gamertag, LHString*
// score, LHString* rank, line 1..3, highlighted line, xuid*) — what sub_829B8360 computed for its
// three lines (calls 0x829B8B9C / 0x829B8BC0 / 0x829B8BE4). REPLACED: behind `element && Pres`
// (0x829B8C80..0x829B8C88) it commits the three texts and their colour (0x829B8CB4..0x829B8E34) and
// shows the gamer picture or requests it (sub_82396C10 / sub_823969B8, 0x829B8EA4 / 0x829B8F40) —
// presentation only (the picture is dropped as the chat channel's is). Stated: `@Screen.StatsBoard(box,
// line, highlighted, gamertag, score, rank)`. The board itself (sub_829B8360) stays the guest's: it
// tests its row element and `Pres` for non-zero only (`cmplwi r20,0` / `cmplwi r3,0` 0x829B8388..
// 0x829B8394) and hands both to the line alone (`mr r4,r20` 0x829B8B94 / 0x829B8BB8 / 0x829B8BDC), so
// this writer passes the box's token as its row element.
//
// FRIENDS sub_829B8FC0 / disc sub_829708B0, for the statistic `+0x4C` of the category: with records,
// the category in range and statistics in it (0x829B90F8..0x829B9144), R as above and the friends'
// ranking of the player's own value (0x829B9198..0x829B9248); with neither, back to page 3 through the
// page setter sub_829B73B0 (0x829B9798..0x829B97A0) — kept. Else +0x50 = the entries (0x829B9268), the
// window start 0 or `cursor - 8` past nine (0x829B9264..0x829B927C), and per entry: "me" when its
// 64-bit id is R's (0x829B93F0..0x829B9434); the gamertag R's name sub_8245F2A8 / disc sub_8243FBB8
// copied (sub_8228DA30, 0x829B9450), then for anyone else the session's name for the entry
// sub_823914A0 / disc sub_82384F78 (*(NET+0x18), entry) assigned (sub_8226D528, 0x829B947C), for me
// the static LHString `dword_8359E824` / disc dword_8355DBBC appended (sub_823CDA60, 0x829B9494); the
// score the entry's +8, mine my own value (0x829B95F4..0x829B961C), its text as above; the rank
// index + 1 (0x829B9688); the title the statistic's name tag (0x829B976C..0x829B9788). Dropped: the
// slides, the arrows, the commits, the pictures and their cache (+0x54..+0x68). Stated:
// `@Screen.StatsList(box, 4, count, 9, start, online 1, title tag, 0)`, `@Screen.StatsWindow`, every
// entry `@Screen.StatsRow(box, 4, index, 0, gamertag, score, rank, 0)`.
//
// THE OWNER: the Show caches the player index of the entity's component 37 `*(+0x14)+0x24` (-1 with
// none) at `stw r11,0x40(r29)` 0x829B5EC0 / disc 0x8296D740, the value the input handler compares
// with its r5 (0x829B62C4..0x829B62E0). `@Screen.Owner(box, player)` there (RefiiF3StatsOwner).

#include <atomic>
#include <cstdint>
#include <cstring>
#include <string>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "box_answers.h"
#include "gui_statements.h"
#include "mod_seam.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(rex_StatsBox_Page);
REX_EXTERN(rex_StatsBox_Board);
REX_EXTERN(rex_StatsBox_Ranking);
REX_EXTERN(rex_Stats_NameTag);
REX_EXTERN(rex_Stats_FormatValue);
REX_EXTERN(rex_Stats_LeaderboardIds);
REX_EXTERN(rex_Text_FromInt);
REX_EXTERN(rex_GamerRecord_FromComponent);
REX_EXTERN(rex_GamerRecord_ForEntity);
REX_EXTERN(rex_GamerRecord_Name);
REX_EXTERN(rex_Game_World);
REX_EXTERN(rex_Net_GamerTag);
REX_EXTERN(rex_LHString_Copy);
REX_EXTERN(rex_LHString_Append);
REX_EXTERN(rex_LHString_Assign);
REX_EXTERN(rex_LHString_FromWide);
REX_EXTERN(rex_LHString_Dtor);
REX_EXTERN(rex_lhDelete);

namespace {

using refii::fable3::Boolean;
using refii::fable3::Hex;
using refii::fable3::LoadGuestU32;
using refii::fable3::Number;
using refii::fable3::Plausible;
using refii::fable3::ReadLhString;
using refii::fable3::Statement;
using refii::fable3::Text;
namespace seam = refii::fable3::seam;

const refii::guest::Addr kNetwork{0x835979F8u};
const refii::guest::Addr kTitleTag{0x8359D2F4u};
const refii::guest::Addr kCategoriesAcceptTag{0x8359D358u};
const refii::guest::Addr kStatisticsAcceptTag{0x8359D35Cu};
const refii::guest::Addr kOfflineRankTag{0x8359D360u};
const refii::guest::Addr kMeSuffix{0x8359E824u};
const refii::guest::Addr kGamerRecordVtable{0x820812C8u};
const refii::guest::Addr kGamerRecordSeed{0x82071BC8u};
const refii::guest::Addr kEmptyWide{0x82000B4Cu};

constexpr uint32_t kCategoriesBegin = 0x1Cu;
constexpr uint32_t kCategoriesEnd = 0x20u;
constexpr uint32_t kCategoryRecord = 20u;
constexpr uint32_t kEntityRef = 0x2Cu;
constexpr uint32_t kPage = 0x34u;
constexpr uint32_t kCursor = 0x44u;
constexpr uint32_t kCategory = 0x48u;
constexpr uint32_t kFriendsStatistic = 0x4Cu;
constexpr uint32_t kFriendsCount = 0x50u;

constexpr uint32_t kComponentStats = 15u;
constexpr uint32_t kComponentPlayer = 37u;

constexpr uint32_t kCategoryRows = 8u;
constexpr int32_t kStatisticsRowsOnline = 6;
constexpr int32_t kStatisticsRowsOffline = 9;
constexpr int32_t kFriendsRows = 9;

/// The box's token (screen_cut_hooks.cpp's 0x1000 + screen 0xF): the board's row element.
constexpr uint32_t kStatsToken = 0x1000u + 0x0Fu;

uint64_t LoadU64(const uint8_t* base, uint32_t at) {
  return (static_cast<uint64_t>(LoadGuestU32(base, at)) << 32) | LoadGuestU32(base, at + 4u);
}

int32_t I32(uint32_t value) { return static_cast<int32_t>(value); }

/// Guest stack for one writer: the calls' frames grow below `work.r1`, the buffers sit above it (from
/// +0x200, clear of a callee's stores into its caller's parameter area) up to the caller's own r1.
/// A row's buffers are taken after `Mark()` and given back with `Release()`, so a list of any length
/// stays inside the frame.
struct Scratch {
  PPCContext work;
  uint32_t next;
  uint32_t limit;
  explicit Scratch(const PPCContext& ctx)
      : work(seam::Frame(ctx, 0x800u)), next(work.r1.u32 + 0x200u), limit(work.r1.u32 + 0x7F0u) {}
  uint32_t Take(uint32_t bytes) {
    const uint32_t size = (bytes + 15u) & ~15u;
    if (next + size > limit) {
      next = limit - size;
      REXLOG_ERROR("[fable3-screens] stats: the writer's scratch frame is full; a buffer is reused");
    }
    const uint32_t at = next;
    next += size;
    return at;
  }
  uint32_t Mark() const { return next; }
  void Release(uint32_t mark) { next = mark; }
  PPCContext Call() { return work; }
};

/// The box's entity (`lhSmartPtr_Deref(box+0x2C)`), 0 when its block is gone.
uint32_t Entity(const uint8_t* base, uint32_t box) { return seam::Deref(base, box + kEntityRef); }

/// An LHString built by `make` into a scratch slot, read, and destroyed.
template <typename Make>
std::string LhText(Scratch& scratch, uint8_t* base, Make make) {
  const uint32_t slot = scratch.Take(16u);
  seam::StoreU32(base, slot, 0u);
  make(slot);
  const std::string text = ReadLhString(base, slot);
  PPCContext work = scratch.Call();
  work.r3.u64 = slot;
  rex_LHString_Dtor(work, base);
  return text;
}

std::string NumberText(Scratch& scratch, uint8_t* base, int32_t value) {
  return LhText(scratch, base, [&](uint32_t slot) {
    PPCContext work = scratch.Call();
    work.r3.u64 = slot;
    work.r4.u64 = static_cast<uint32_t>(value);
    rex_Text_FromInt(work, base);
  });
}

/// The value's text as the writers make it: "%d", then the statistic's own format.
std::string ValueText(Scratch& scratch, uint8_t* base, uint32_t id, int32_t value) {
  return LhText(scratch, base, [&](uint32_t slot) {
    PPCContext work = scratch.Call();
    work.r3.u64 = slot;
    work.r4.u64 = static_cast<uint32_t>(value);
    rex_Text_FromInt(work, base);
    work = scratch.Call();
    work.r3.u64 = id;
    work.r4.u64 = slot;
    rex_Stats_FormatValue(work, base);
  });
}

/// The statistic's value: `c15+0xC`'s slot 0 (id).
int32_t StatValue(Scratch& scratch, uint8_t* base, uint32_t stats, uint32_t id) {
  PPCContext work = scratch.Call();
  work.r4.u64 = id;
  return seam::CallSlot(work, base, stats + 0xCu, 0u) ? I32(work.r3.u32) : 0;
}

uint32_t NameTag(Scratch& scratch, uint8_t* base, uint32_t stats, uint32_t id) {
  PPCContext work = scratch.Call();
  work.r3.u64 = stats;
  work.r4.u64 = id;
  rex_Stats_NameTag(work, base);
  return work.r3.u32;
}

/// The player's gamer record as the writers seed it, filled from the box's entity; 0 when the guest's
/// filler answers 0 (the record is then already destroyed).
uint32_t GamerRecord(Scratch& scratch, uint8_t* base, uint32_t entity) {
  const uint32_t record = scratch.Take(0x40u);
  std::memset(base + record, 0, 0x40u);
  seam::StoreU32(base, record, kGamerRecordVtable);
  std::memcpy(base + record + 0x18u, base + static_cast<uint32_t>(kGamerRecordSeed), 8u);
  PPCContext work = scratch.Call();
  work.r3.u64 = record + 0x20u;
  work.r4.u64 = kEmptyWide;
  rex_LHString_FromWide(work, base);

  work = scratch.Call();
  rex_Game_World(work, base);
  const uint32_t world = work.r3.u32;
  const uint32_t holder = Plausible(world) ? LoadGuestU32(base, world + 4u) : 0u;
  work = scratch.Call();
  work.r3.u64 = Plausible(holder) ? LoadGuestU32(base, holder + 4u) : 0u;
  work.r4.u64 = entity;
  work.r5.u64 = record;
  rex_GamerRecord_ForEntity(work, base);
  if ((work.r3.u32 & 0xFFu) != 0u) {
    return record;
  }
  work = scratch.Call();
  work.r3.u64 = record + 0x20u;
  rex_LHString_Dtor(work, base);
  return 0u;
}

void DropGamerRecord(Scratch& scratch, uint8_t* base, uint32_t record) {
  if (record == 0u) {
    return;
  }
  PPCContext work = scratch.Call();
  work.r3.u64 = record + 0x20u;
  rex_LHString_Dtor(work, base);
}

/// sub_8245F278: the record's id, +0x10 when set, else +8.
uint64_t RecordId(const uint8_t* base, uint32_t record) {
  const uint64_t id = LoadU64(base, record + 0x10u);
  return id != 0u ? id : LoadU64(base, record + 8u);
}

/// The guest's ONLINE for the statistics page (`v104 && v103`).
bool Online(Scratch& scratch, uint8_t* base, uint32_t entity) {
  const uint32_t player = seam::Component(base, entity, kComponentPlayer);
  if (player == 0u) {
    return false;
  }
  const uint32_t profile_holder = LoadGuestU32(base, player + 0x14u);
  const int32_t profile = Plausible(profile_holder) ? I32(LoadGuestU32(base, profile_holder + 0x28u)) : -1;
  const uint32_t net = LoadGuestU32(base, kNetwork);
  bool signed_in = false;
  if (Plausible(net) && profile >= 0 && profile < 4) {
    const uint32_t table = LoadGuestU32(base, net + 0x28u);
    const uint32_t entry = Plausible(table) ? LoadGuestU32(base, table + 8u) + 32u * static_cast<uint32_t>(profile) : 0u;
    if (Plausible(entry)) {
      // TU3 sub_8227D110: byte 0 set and byte 2 clear; the disc's inline test reads byte 0 alone.
      signed_in = base[entry] != 0u && (refii::guest::Current() == refii::guest::Build::kDisc || base[entry + 2u] == 0u);
    }
  }

  const uint32_t record = scratch.Take(0x40u);
  std::memset(base + record, 0, 0x40u);
  PPCContext work = scratch.Call();
  work.r3.u64 = record;
  work.r4.u64 = player;
  rex_GamerRecord_FromComponent(work, base);
  bool listed = false;
  const uint32_t session = Plausible(net) ? LoadGuestU32(base, net + 0x18u) : 0u;
  if (Plausible(session) && LoadGuestU32(base, session + 0xCu) == 0u) {
    const uint64_t id = LoadU64(base, record + 0x10u);
    const uint32_t last = LoadGuestU32(base, session + 0x80u);
    for (uint32_t at = LoadGuestU32(base, session + 0x7Cu); Plausible(at) && at < last; at += 0x28u) {
      if (LoadU64(base, at + 0x10u) == id) {
        listed = true;
        break;
      }
    }
  }
  work = scratch.Call();
  work.r3.u64 = record + 0x20u;
  rex_LHString_Dtor(work, base);
  return signed_in && listed;
}

/// The friends' ranking of `value` for the statistic's leaderboard `ids` (sub_829B8188): its entries
/// (16 bytes each, the 64-bit id first, the score at +8) in a scratch vector; false when it answers 0.
bool Ranking(Scratch& scratch, uint8_t* base, uint32_t record, int32_t value, uint32_t ids, uint32_t vector) {
  seam::StoreU32(base, vector, 0u);
  seam::StoreU32(base, vector + 4u, 0u);
  seam::StoreU32(base, vector + 8u, 0u);
  PPCContext work = scratch.Call();
  work.r3.u64 = record;
  work.r4.u64 = static_cast<uint32_t>(value);
  work.r5.u64 = ids;
  work.r6.u64 = vector;
  rex_StatsBox_Ranking(work, base);
  return (work.r3.u32 & 0xFFu) != 0u;
}

void FreeVector(Scratch& scratch, uint8_t* base, uint32_t vector) {
  const uint32_t first = LoadGuestU32(base, vector);
  if (first != 0u) {
    PPCContext work = scratch.Call();
    work.r3.u64 = first;
    rex_lhDelete(work, base);
  }
}

void StateList(uint32_t box, int32_t page, uint32_t count, int32_t rows, uint32_t start, bool online, uint32_t title,
               uint32_t accept) {
  Statement("@Screen.StatsList")
      .Add(Text(Hex(box)))
      .Add(Number(static_cast<float>(page)))
      .Add(Number(static_cast<float>(count)))
      .Add(Number(static_cast<float>(rows)))
      .Add(Number(static_cast<float>(start)))
      .Add(Boolean(online))
      .Add(Text(Hex(title)))
      .Add(Text(Hex(accept)))
      .Post();
  refii::fable3::StateStatsWindow(box, page, start, count, rows);
}

void StateRow(uint32_t box, int32_t page, uint32_t index, uint32_t label_tag, const std::string& label,
              const std::string& value, const std::string& rank, uint32_t rank_tag) {
  Statement("@Screen.StatsRow")
      .Add(Text(Hex(box)))
      .Add(Number(static_cast<float>(page)))
      .Add(Number(static_cast<float>(index)))
      .Add(Text(Hex(label_tag)))
      .Add(Text(label))
      .Add(Text(value))
      .Add(Text(rank))
      .Add(Text(Hex(rank_tag)))
      .Post();
}

void SayOnce(std::atomic<bool>& said, const char* what) {
  if (!said.exchange(true)) {
    REXLOG_INFO("[fable3-screens] stats: {} is replaced (stated, no presentation)", what);
  }
}

}  // namespace

/// sub_829B7458 / disc sub_8296ED30 (box): the categories, stated.
REX_EXTERN(rex_StatsBox_WriteCategories) {
  static std::atomic<bool> said{false};
  SayOnce(said, "the categories writer");
  const uint32_t box = ctx.r3.u32;
  const uint32_t first = LoadGuestU32(base, box + kCategoriesBegin);
  const uint32_t count = (LoadGuestU32(base, box + kCategoriesEnd) - first) / kCategoryRecord;
  const int32_t cursor = I32(LoadGuestU32(base, box + kCursor));
  const uint32_t start = count > kCategoryRows && cursor >= static_cast<int32_t>(kCategoryRows)
                             ? static_cast<uint32_t>(cursor) - kCategoryRows
                             : 0u;
  StateList(box, 2, count, static_cast<int32_t>(kCategoryRows), start, false, LoadGuestU32(base, kTitleTag),
            LoadGuestU32(base, kCategoriesAcceptTag));
  for (uint32_t index = 0; index < count && index < 256u; ++index) {
    StateRow(box, 2, index, LoadGuestU32(base, first + kCategoryRecord * index), std::string(), std::string(),
             std::string(), 0u);
  }
}

/// sub_829B76A0 / disc sub_8296EF78 (box): the category's statistics, stated.
REX_EXTERN(rex_StatsBox_WriteStatistics) {
  static std::atomic<bool> said{false};
  SayOnce(said, "the statistics writer");
  const uint32_t box = ctx.r3.u32;
  Scratch scratch(ctx);
  const uint32_t entity = Entity(base, box);
  const bool online = seam::Live(base, entity) && Online(scratch, base, entity);

  const uint32_t category = LoadGuestU32(base, box + kCategoriesBegin) + kCategoryRecord * LoadGuestU32(base, box + kCategory);
  const uint32_t ids = LoadGuestU32(base, category + 4u);
  const uint32_t count = (LoadGuestU32(base, category + 8u) - ids) >> 2;
  const int32_t rows = online ? kStatisticsRowsOnline : kStatisticsRowsOffline;
  const int32_t cursor = I32(LoadGuestU32(base, box + kCursor));
  const uint32_t start = static_cast<int32_t>(count) > rows && cursor >= rows ? static_cast<uint32_t>(cursor - rows + 1) : 0u;

  const uint32_t stats = Plausible(entity) ? seam::Component(base, entity, kComponentStats) : 0u;
  if (stats == 0u) {
    return;
  }
  const uint32_t record = GamerRecord(scratch, base, entity);
  if (record == 0u) {
    return;
  }
  const bool page3 = LoadGuestU32(base, box + kPage) == 3u;
  StateList(box, 3, count, rows, start, online, LoadGuestU32(base, category),
            page3 && online ? LoadGuestU32(base, kStatisticsAcceptTag) : 0u);
  const uint32_t leaderboard = scratch.Take(8u);
  const uint32_t vector = scratch.Take(16u);
  const uint32_t mark = scratch.Mark();
  for (uint32_t index = 0; index < count && index < 256u; ++index, scratch.Release(mark)) {
    const uint32_t id = LoadGuestU32(base, ids + 4u * index);
    const uint32_t name = NameTag(scratch, base, stats, id);
    const int32_t value = StatValue(scratch, base, stats, id);
    const std::string text = ValueText(scratch, base, id, value);
    const bool in_window = index >= start && index < start + static_cast<uint32_t>(rows);
    if (!online) {
      StateRow(box, 3, index, name, std::string(), text, std::string(), LoadGuestU32(base, kOfflineRankTag));
      continue;
    }
    if (!in_window) {
      StateRow(box, 3, index, name, std::string(), text, std::string(), 0u);
      continue;
    }
    PPCContext work = scratch.Call();
    work.r3.u64 = leaderboard;
    work.r4.u64 = stats;
    work.r5.u64 = id;
    rex_Stats_LeaderboardIds(work, base);
    uint32_t place = 0u;
    if (Ranking(scratch, base, record, value, leaderboard, vector)) {
      const uint32_t first = LoadGuestU32(base, vector);
      const uint32_t entries = (LoadGuestU32(base, vector + 4u) - first) >> 4;
      const uint64_t mine = RecordId(base, record);
      for (uint32_t k = 0; k < entries; ++k) {
        if (LoadU64(base, first + 16u * k) == mine) {
          place = k;
          break;
        }
      }
    }
    FreeVector(scratch, base, vector);
    StateRow(box, 3, index, name, std::string(), text, NumberText(scratch, base, I32(place + 1u)), 0u);
    if (I32(index) == cursor && I32(LoadGuestU32(base, leaderboard)) != -1) {
      work = scratch.Call();
      work.r3.u64 = box;
      work.r4.u64 = kStatsToken;
      work.r5.u64 = id;
      work.r6.u64 = leaderboard;
      work.r7.u64 = record;
      work.r8.u64 = static_cast<uint32_t>(value);
      rex_StatsBox_Board(work, base);
    }
  }
  DropGamerRecord(scratch, base, record);
}

/// sub_829B8C48 / disc sub_82970538 (box, row element, gamertag*, score*, rank*, line, highlighted,
/// xuid*): one line of the board around the player, stated.
REX_EXTERN(rex_StatsBox_BoardLine) {
  Statement("@Screen.StatsBoard")
      .Add(Text(Hex(ctx.r3.u32)))
      .Add(Number(static_cast<float>(I32(ctx.r8.u32))))
      .Add(Number(static_cast<float>(I32(ctx.r9.u32))))
      .Add(Text(ReadLhString(base, ctx.r5.u32)))
      .Add(Text(ReadLhString(base, ctx.r6.u32)))
      .Add(Text(ReadLhString(base, ctx.r7.u32)))
      .Post();
}

/// sub_829B8FC0 / disc sub_829708B0 (box): the friends' board for one statistic, stated.
REX_EXTERN(rex_StatsBox_WriteFriends) {
  static std::atomic<bool> said{false};
  SayOnce(said, "the friends writer");
  const uint32_t box = ctx.r3.u32;
  Scratch scratch(ctx);
  const uint32_t entity = Entity(base, box);
  const uint32_t stats = Plausible(entity) ? seam::Component(base, entity, kComponentStats) : 0u;
  if (stats == 0u) {
    return;
  }
  const uint32_t first = LoadGuestU32(base, box + kCategoriesBegin);
  const uint32_t last = LoadGuestU32(base, box + kCategoriesEnd);
  const uint32_t which = LoadGuestU32(base, box + kCategory);
  if (first == last || I32(last - first) / I32(kCategoryRecord) <= I32(which)) {
    return;
  }
  const uint32_t category = first + kCategoryRecord * which;
  const uint32_t ids = LoadGuestU32(base, category + 4u);
  if (ids == LoadGuestU32(base, category + 8u)) {
    return;
  }
  const uint32_t id = LoadGuestU32(base, ids + 4u * LoadGuestU32(base, box + kFriendsStatistic));
  const uint32_t leaderboard = scratch.Take(8u);
  {
    PPCContext work = scratch.Call();
    work.r3.u64 = leaderboard;
    work.r4.u64 = stats;
    work.r5.u64 = id;
    rex_Stats_LeaderboardIds(work, base);
  }
  const uint32_t vector = scratch.Take(16u);
  seam::StoreU32(base, vector, 0u);
  const uint32_t record = GamerRecord(scratch, base, entity);
  const int32_t own = record != 0u ? StatValue(scratch, base, stats, id) : 0;
  if (record == 0u || !Ranking(scratch, base, record, own, leaderboard, vector)) {
    PPCContext work = scratch.Call();
    work.r3.u64 = box;
    work.r4.u64 = 3u;
    rex_StatsBox_Page(work, base);
    FreeVector(scratch, base, vector);
    DropGamerRecord(scratch, base, record);
    return;
  }

  const uint32_t entries = (LoadGuestU32(base, vector + 4u) - LoadGuestU32(base, vector)) >> 4;
  seam::StoreU32(base, box + kFriendsCount, entries);
  const int32_t cursor = I32(LoadGuestU32(base, box + kCursor));
  const uint32_t start = I32(entries) > kFriendsRows && cursor >= kFriendsRows ? static_cast<uint32_t>(cursor - 8) : 0u;
  StateList(box, 4, entries, kFriendsRows, start, true, NameTag(scratch, base, stats, id), 0u);
  const uint64_t mine = RecordId(base, record);
  const uint32_t net = LoadGuestU32(base, kNetwork);
  const uint32_t mark = scratch.Mark();
  for (uint32_t index = 0; index < entries && index < 256u; ++index, scratch.Release(mark)) {
    const uint32_t entry = LoadGuestU32(base, vector) + 16u * index;
    const bool me = LoadU64(base, entry) == mine;
    const std::string gamertag = LhText(scratch, base, [&](uint32_t slot) {
      PPCContext work = scratch.Call();
      work.r3.u64 = record;
      rex_GamerRecord_Name(work, base);
      const uint32_t name = work.r3.u32;
      work = scratch.Call();
      work.r3.u64 = slot;
      work.r4.u64 = name;
      rex_LHString_Copy(work, base);
      if (me) {
        work = scratch.Call();
        work.r3.u64 = slot;
        work.r4.u64 = kMeSuffix;
        rex_LHString_Append(work, base);
        return;
      }
      const uint32_t theirs = scratch.Take(16u);
      seam::StoreU32(base, theirs, 0u);
      work = scratch.Call();
      work.r3.u64 = theirs;
      work.r4.u64 = Plausible(net) ? LoadGuestU32(base, net + 0x18u) : 0u;
      work.r5.u64 = entry;
      rex_Net_GamerTag(work, base);
      work = scratch.Call();
      work.r3.u64 = slot;
      work.r4.u64 = theirs;
      rex_LHString_Assign(work, base);
      work = scratch.Call();
      work.r3.u64 = theirs;
      rex_LHString_Dtor(work, base);
    });
    const int32_t score = me ? StatValue(scratch, base, stats, id) : I32(LoadGuestU32(base, entry + 8u));
    StateRow(box, 4, index, 0u, gamertag, ValueText(scratch, base, id, score), NumberText(scratch, base, I32(index + 1u)),
             0u);
  }
  FreeVector(scratch, base, vector);
  DropGamerRecord(scratch, base, record);
}

/// The Show's owner store `stw r11,0x40(r29)` TU3 0x829B5EC0 / disc 0x8296D740: box r29, player r11.
void RefiiF3StatsOwner(PPCRegister& box, PPCRegister& player) {
  Statement("@Screen.Owner")
      .Add(Text(Hex(box.u32)))
      .Add(Number(static_cast<float>(I32(player.u32))))
      .Post();
}
