// refii - THE SCREENS GODOT OWNS ARE NEVER LOADED. Fable 3's box screens and their .bgf documents are
// Godot's (Fable3StatedScreen, Fable3EndScreens); the guest states what they show and no longer makes
// the presentation they were drawn in. TU3 addresses; the disc twins are named, each body read on both
// builds (the two are the same instruction for instruction wherever a twin is named here).
//
// THE ONE GATE. Every presentation of these screens is made by the screen loader sub_825F26E8 / disc
// sub_825C79A0 (L, id), L = *(GUI+0x38): ids the resident list sub_825F2CD8 does not hold (8, > 30) go
// to the special slot L+0x9C with L+0x98 = id (0x825F2710..0x825F2724), every other id to its slot
// L + 4*(id+2) (`addi r11,r30,2; slwi r11,r11,2; add. r31,r11,r29` 0x825F2770..0x825F277C); its file
// is the loader's own map at L+0xA0 (`addi r3,r29,0xA0; bl sub_82355420` 0x825F2768..0x825F276C) and
// sub_825F27D8 (rex_GuiPresentation_Load) loads it into an empty slot (0x825F2788), then the init
// scripts run on what was loaded (0x825F27C0 / 0x825F27CC). Its callers for a box screen are two:
//   - `Pres(box)` sub_829588C8 / disc sub_8291F4C8, on an empty slot (`bl sub_825F26E8` 0x82958A80 /
//     0x8291F67C), the slot of the box's type `box->vt[7]()` (0x829588E4..0x829588EC) through a
//     29-case table, read on both builds: 12 -> 0xD Promises, 13 -> 0xE HeroStatus, 20 -> 0xF
//     StatsScreen, 22 -> 0x1A ChatChannel, 23 -> 0x1B CompWeddingPicker, 25 -> 0x1D KingsSchedule,
//     27 -> 0x1E QuestSummary (5 -> 3 the intermission; the default 4);
//   - the preload pass sub_821B05A8 / disc sub_821B05B0, every GUI frame, for the type of queue 0's
//     showing (or front pending) box through its one row helper sub_821D8430 / disc sub_821D86D8:
//     a state-0 row on an empty slot loads it (`bl sub_825F26E8` 0x821B0750 / 0x821B0758).
// rex_ScreenLoader_Load answers nothing for an owned id: the slot stays empty, so the pass's next row
// loads nothing either, and `Pres` re-reads the empty slot and answers 0 (0x82958A84..0x82958ABC:
// `cmplwi r31,0; beq` over the HUD attach). The ids are the guest's own screen enumeration.
//
// THE TOKEN. A class whose writers state everything through the path setters (path, pres, value) —
// hero status, the wedding picker, the stats screen's page slides — keeps its writers when `Pres`
// answers a token for its type (rex_DisplayBox_Presentation): 0x1000 + the screen id, below every guest
// address (the heap and statics start at 0x40000000), so no hook reads through it (Plausible). The
// path setters state the token's call and return (ui_stated_hooks.cpp); Find sub_82877D40 / disc
// sub_8281A038 and Resolve sub_82877B48 / disc sub_82819E40 answer no element for it, destroying the
// path they own as their own no-presentation exits do (Find: `mr r3,r31; bl lhCharString_Dtor`
// 0x82877D78..0x82877D7C after its Resolve; Resolve: 0x82877CA4..0x82877CAC). Every element op of the
// classes tests its element first (SetVisible's sub_82877900 `cmplwi r3,0` 0x8287790C), so a writer
// that resolves elements writes nothing. The first `Pres` of each new box posts `@Presentation.Load(
// token, file)` with the file the loader's map names for the id (sub_82355420 / disc sub_8234D6E8,
// the map's find-or-insert, whose ids are all present: the loader ctor fills it), so Godot keys the
// box's statements by its document (=herostatus, …) exactly as a real load did.
//
// Classes owned with no token: the king's schedule and the chat channel, whose Shows are replaced and
// state their screens (display_box_stubs.cpp); their remaining `Pres` callers (the chat's toggle
// writer sub_829BB518, path setters behind `cmplwi r3,0` 0x829BB530) meet 0 and do nothing.

#include <atomic>
#include <cstdint>
#include <mutex>
#include <string>
#include <unordered_map>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "gui_statements.h"
#include "mod_seam.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_ScreenLoader_Load);
REX_EXTERN(__imp__rex_DisplayBox_Presentation);
REX_EXTERN(__imp__rex_GuiElement_Find);
REX_EXTERN(__imp__rex_DisplayBox_Construct);
REX_EXTERN(rex_ScreenLoader_FileOf);
REX_EXTERN(rex_lhCharString_Dtor);

namespace {

using refii::fable3::LoadGuestU32;
using refii::fable3::Plausible;

const refii::guest::Addr kGuiManager{0x83596F84u};
constexpr uint32_t kGuiLoader = 0x38u;
/// The loader's id -> file map (sub_82B73E68 fills it in the loader's ctor, 0x825ED774).
constexpr uint32_t kLoaderFiles = 0xA0u;
/// `box->vt[7]()`: the box's type, as Pres asks it.
constexpr uint32_t kBoxTypeSlot = 7u;

constexpr uint32_t kTokenBase = 0x1000u;

/// One screen Godot owns: the loader's id, and the box type whose `Pres` answers the token (-1: none,
/// the class states its screen without its writers).
struct Owned {
  uint32_t id;
  int32_t type;
};

constexpr Owned kOwned[] = {
    {0x0Eu, 13},  // HeroStatus.bgf, CHeroStatusBox: its three writers and slot writer, path setters only
    {0x1Bu, 23},  // CompWeddingPicker.bgf, CWeddingPickerBox: Show / Hide / highlight, path setters only
    {0x0Fu, 20},  // StatsScreen.bgf, CStatsBox: page slides by token, its three writers replaced (stats_screen_stubs.cpp)
    {0x0Du, 12},  // Promises.bgf, CPromiseMessageBox: slides by token, the gamer name stated (owned_screen_texts.cpp)
    {0x1Eu, 27},  // QuestSummary.bgf, CQuestSummaryBox: header by token, the rows' texts stated (owned_screen_texts.cpp)
    {0x1Au, -1},  // ChatChannel.bgf, COrbChatBox: Show and Update replaced (display_box_stubs.cpp)
    {0x1Du, -1},  // KingsSchedule.bgf, CKingsScheduleBox: Show replaced (display_box_stubs.cpp)
};

const Owned* OwnedId(uint32_t id) {
  for (const Owned& owned : kOwned) {
    if (owned.id == id) {
      return &owned;
    }
  }
  return nullptr;
}

const Owned* OwnedType(int32_t type) {
  for (const Owned& owned : kOwned) {
    if (owned.type >= 0 && owned.type == type) {
      return &owned;
    }
  }
  return nullptr;
}

std::mutex g_mutex;
/// The box each token last named its document for, by screen id.
std::unordered_map<uint32_t, uint32_t> g_named_box;
std::atomic<uint64_t> g_refused_said{0};
std::atomic<uint64_t> g_token_said{0};

bool FirstFor(std::atomic<uint64_t>& said, uint32_t id) {
  const uint64_t bit = 1ull << (id & 63u);
  return (said.fetch_or(bit) & bit) == 0u;
}

/// The file the loader's map names for `id`, as sub_825F26E8 asks it (0x825F2760..0x825F276C).
std::string FileOf(PPCContext& ctx, uint8_t* base, uint32_t id) {
  const uint32_t gui = LoadGuestU32(base, kGuiManager);
  const uint32_t loader = Plausible(gui) ? LoadGuestU32(base, gui + kGuiLoader) : 0u;
  if (!Plausible(loader)) {
    return std::string();
  }
  PPCContext work = refii::fable3::seam::Frame(ctx);
  const uint32_t key = work.r1.u32 + 0x100u;
  refii::fable3::seam::StoreU32(base, key, id);
  work.r3.u64 = loader + kLoaderFiles;
  work.r4.u64 = key;
  rex_ScreenLoader_FileOf(work, base);
  return refii::fable3::ReadCharString(base, work.r3.u32, 260u);
}

}  // namespace

namespace refii::fable3 {

bool IsPresentationToken(uint32_t presentation) {
  return presentation >= kTokenBase && presentation < kTokenBase + 0x100u;
}

uint32_t ScreenToken(uint32_t screen_id) { return kTokenBase + (screen_id & 0xFFu); }

void NameScreenToken(PPCContext& ctx, uint8_t* base, uint32_t screen_id) {
  NoteDocument(ScreenToken(screen_id), FileOf(ctx, base, screen_id));
}

}  // namespace refii::fable3

/// The display box base ctor sub_82958000 / disc sub_8291ECD8 (box, CCharString* name, LHString*):
/// every box class's ctor calls it first (59 callers, CPromiseMessageBox_ctor, CKingsScheduleBox_ctor,
/// CQuestInfoBox_ctor, the stats ctor sub_829B5A68 at 0x829B5A9C, …), so a box made at an address a
/// token last named is a new box: its first `Pres` names its document again.
REX_EXTERN(rex_DisplayBox_Construct) {
  const uint32_t box = ctx.r3.u32;
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    for (auto& [id, named] : g_named_box) {
      if (named == box) {
        named = 0u;
      }
    }
  }
  __imp__rex_DisplayBox_Construct(ctx, base);
}

/// sub_825F26E8 / disc sub_825C79A0 (L, id): an owned screen is not loaded.
REX_EXTERN(rex_ScreenLoader_Load) {
  const uint32_t id = ctx.r4.u32;
  if (OwnedId(id) != nullptr) {
    if (FirstFor(g_refused_said, id)) {
      REXLOG_INFO("[fable3-screens] screen {} is Godot's: the loader leaves it unloaded (first refusal)", id);
    }
    return;
  }
  __imp__rex_ScreenLoader_Load(ctx, base);
}

/// sub_829588C8 / disc sub_8291F4C8 (box): an owned type with a token answers it, unloaded.
REX_EXTERN(rex_DisplayBox_Presentation) {
  const uint32_t box = ctx.r3.u32;
  int32_t type = -1;
  if (base != nullptr && Plausible(box)) {
    PPCContext work = refii::fable3::seam::Frame(ctx);
    if (refii::fable3::seam::CallSlot(work, base, box, kBoxTypeSlot)) {
      type = static_cast<int32_t>(work.r3.u32);
    }
  }
  const Owned* owned = OwnedType(type);
  if (owned == nullptr) {
    __imp__rex_DisplayBox_Presentation(ctx, base);
    return;
  }
  const uint32_t token = kTokenBase + owned->id;
  bool fresh = false;
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    auto [it, inserted] = g_named_box.emplace(owned->id, box);
    fresh = inserted || it->second != box;  // 0 after the base ctor ran for this address
    it->second = box;
  }
  if (fresh) {
    const std::string file = FileOf(ctx, base, owned->id);
    refii::fable3::NoteDocument(token, file);
    if (FirstFor(g_token_said, owned->id)) {
      REXLOG_INFO("[fable3-screens] box type {} answers its token {:04X} for '{}': its writers state, nothing is drawn",
                  type, token, file);
    }
  }
  ctx.r3.u64 = token;
}

/// sub_82877D40 / disc sub_8281A038 (CCharString* path, pres): no element under a token; the path is
/// destroyed as the guest's own body does after its Resolve (0x82877D78..0x82877D7C).
REX_EXTERN(rex_GuiElement_Find) {
  if (refii::fable3::IsPresentationToken(ctx.r4.u32)) {
    rex_lhCharString_Dtor(ctx, base);
    ctx.r3.u64 = 0u;
    return;
  }
  __imp__rex_GuiElement_Find(ctx, base);
}
