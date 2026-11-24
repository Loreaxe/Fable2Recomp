// refii - THE READERS OF GAMEFACE'S PER-FRAME RUNTIME, ANSWERED FROM STATEMENTS (Fable 3). The
// presentation Update's tree walk, timeline advance and track pass produce values game C++ reads
// back; each reader below takes what it needs from what the guest itself stated, or from what
// Godot — which draws and plays the presentation — states back. TU3 addresses, both builds'
// bodies instruction for instruction identical (the disc's in Fable3/versions/Fable3).
//
// THE EXPANDABLE BOX'S CALIBRATION, CHUDExpandableBox vf+12 (vtable 0x820014F4; `sub_8227AEB0`
// (box, f1 ms)). A three-step machine, 500 ms apart (`lfs f0,flt_820711B0` / `stfs f0,0x28(r31)`
// 0x8227B038): step 0 sets the box's meters +0x1C / +0x20 to 100 (`sub_82975C30` 0x8227AF30 /
// 0x8227AF4C); step 1 stores the box's extent at the meters' end as its maximum (+0x14 / +0x18,
// 0x8227AFB4 / 0x8227AFB0) and sets the meters to 0; step 2 stores it as its minimum (+0xC / +0x10,
// 0x8227B030 / 0x8227B02C); step 3 is the box's TopBox / Subtitles / Corner gate (`sub_82275710`).
// The extent was the BoxName element's scale multiplied up its parents (`sub_82876768` 0x8227AF74,
// times 100.0 at 0x8227AF8C) as the track pass had laid it; it is the box's authored range, which
// Godot draws, so Godot states it (`ui-box:`, Fable3HudDocument) and the steps store that.
//
// THE CURRENT SLIDE, `sub_8227E438`(elem, pres): the owner (`sub_8227E390` 0x8227E48C over pres
// vf+32) and its manager +32 byte (`lbz r3,0x2B(r3)`). Every slide game C++ tests was set by its own
// GuiElement_SetState (the path setters and the registry setter end there; RestartStateAt through
// `lhGuiElement_GoToSlideAndRestart` 0x828781B4), which reaches the handle's GoTo (gui_element_hooks.cpp)
// with 1 <= slide < count (+0x2A, 0x8226718C / 0x82267198). An owner the guest never set stands on
// slide 1, where the walk puts every owner it first sees (`li r5,1` 0x821C9E7C) and where every shipped
// owner authors its rest.
//
// THE RE-VISIBILITY EDGE. The presentation Update's walk `sub_821C9CF8` sends an owner that becomes
// visible — reached by the walk, its own 0x2000 set, its time window open; opacity never counts — to
// slide 1: `821C9E4C clrlwi r9,r31,24; bne` (it was not visible, bit 0x1000) … `821C9E68 clrlwi
// r10,r11,31` (it owns a timeline) … `821C9E78 li r6,0; li r5,1` … `821C9E90 bctrl` (manager +8).
// Godot plays the document and states the edge when a component comes live by its slides' active
// bits, the stated active chain and its window (`ui-live:<document>|<ordinal>`, Fable3HudDocument);
// the handle takes slide 1 as from the walk's GoTo. The readers that see it: TargetNames' flip
// (`sub_821AF920`, hidden by SetFlagActive `sub_82605950`) and CrowdInterestBar.StarsPage
// (`sub_82B60F28`; its bar's 'Hidden' slide drops StarsPage's active bit).
//
// THE WORLD ICONS' FADE TIMERS, `sub_821CAA98` (the 16 pooled slots, then the three specials) and
// `sub_82B873A0`(record, visible): each runs the visibility toggle (or SetFlagActive for a record
// whose +0x32 is set) on the record's `Contents`, and moved the root from `Fade Out` (slide 2, 1000 ms)
// to `Hidden` (7) once the timeline passed 1000 (0x821CAB8C / 0x82B8748C, `li r5,7`). Godot draws
// those icons from the icon feed and fades them over the stated lifetime's last second itself
// (Fable3WorldIcons), so the slide after Fade Out is Godot's; the toggles stay the guest's.
//
// THE SCROLL REPEAT GATE, `sub_82878358`(elem, pres): owner time (+0x30) >= the component's Master
// end (+0x24, laid by the deserialiser only) — the interaction list's 200 ms between scroll steps,
// read by `sub_82972ED8` before it moves the selection, plays SE_GUI_INTERACT_ICON_EMPTY and posts
// event 137. Godot plays the restarted ScrollDown / ScrollUp slide and states when its clock passed
// the Master end (`ui-reached:`), naming the entry by its count of `@Ui.State` for the path.
//
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <map>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "core/headless_presentation.h"
#include "gui_statements.h"
#include "../../vfs/include/fable3_guest_build.h"

namespace {

using namespace refii::fable3;

const refii::guest::Addr kGuiManager{0x83596F84u};
constexpr uint32_t kGuiHud = 0x20u;           // `lwz r10,0x20(r29)` 0x8227AF20
constexpr uint32_t kHudRegistries = 0x24u;    // `lwz r9,0x24(r10)` 0x8227AF24
constexpr uint32_t kRegistryMeters = 0x18u;   // `lwz r8,0x18(r9)` 0x8227AF28
constexpr uint32_t kMeterTable = 4u;          // `lwz r3,4(r8)` 0x8227AF2C
const refii::guest::Addr kMeterSet{0x82975C30u};
const refii::guest::Addr kVisibilityToggle{0x828777E8u};
const refii::guest::Addr kSetFlagActive{0x828773E8u};

constexpr uint32_t kBoxElement = 4u;
constexpr uint32_t kBoxPresentation = 8u;
constexpr uint32_t kBoxMinX = 0xCu;
constexpr uint32_t kBoxMinY = 0x10u;
constexpr uint32_t kBoxMaxX = 0x14u;
constexpr uint32_t kBoxMaxY = 0x18u;
constexpr uint32_t kBoxMeterX = 0x1Cu;
constexpr uint32_t kBoxMeterY = 0x20u;
constexpr uint32_t kBoxState = 0x24u;
constexpr uint32_t kBoxCountdown = 0x28u;
constexpr uint32_t kBoxLast = 0x2Cu;
constexpr float kBoxStep = 500.0f;       // flt_820711B0
constexpr float kMeterFull = 100.0f;     // flt_8206D228

const refii::guest::Addr kIconTable{0x8359EBA0u};
constexpr uint32_t kIconSlots = 16u;
constexpr uint32_t kIconStride = 0x40u;
const refii::guest::Addr kIconFill{0x832E27D0u};
const refii::guest::Addr kIconHero{0x832E2810u};
const refii::guest::Addr kIconHenchman{0x832E2850u};
constexpr uint32_t kIconContents = 4u;
constexpr uint32_t kIconInUse = 0x31u;
constexpr uint32_t kIconFlagMode = 0x32u;

bool Milestone(uint64_t n) { return n <= 32u || (n & (n - 1u)) == 0u; }

PPCFunc* Resolved(uint32_t address) {
  static std::mutex m;
  static std::unordered_map<uint32_t, PPCFunc*> cache;
  std::lock_guard<std::mutex> lock(m);
  const auto it = cache.find(address);
  if (it != cache.end()) {
    return it->second;
  }
  PPCFunc* fn = nullptr;
  for (PPCFuncMapping* mapping = PPCFuncMappings; mapping != nullptr && mapping->host != nullptr; ++mapping) {
    if (static_cast<uint32_t>(mapping->guest) == address) {
      fn = mapping->host;
      break;
    }
  }
  cache.emplace(address, fn);
  if (fn == nullptr) {
    REXLOG_ERROR("Refii UI readers (Fable 3): guest function 0x{:08X} is not a recompiled entry", address);
  }
  return fn;
}

PPCContext Call(PPCContext& ctx, uint8_t* base, uint32_t address, uint32_t r3, uint32_t r4 = 0u, uint32_t r5 = 0u,
                uint32_t r6 = 0u, double f1 = 0.0) {
  PPCContext call = ctx;
  call.r3.u32 = r3;
  call.r4.u32 = r4;
  call.r5.u32 = r5;
  call.r6.u32 = r6;
  call.f1.f64 = f1;
  call.ctr.u32 = address;
  if (PPCFunc* fn = Resolved(address)) {
    fn(call, base);
  } else {
    call.r3.u32 = 0u;
  }
  return call;
}

void StoreF32(uint8_t* base, uint32_t at, float value) {
  uint32_t bits = 0;
  std::memcpy(&bits, &value, sizeof bits);
  bits = __builtin_bswap32(bits);
  std::memcpy(base + at, &bits, sizeof bits);
}

void StoreU32(uint8_t* base, uint32_t at, uint32_t value) {
  const uint32_t v = __builtin_bswap32(value);
  std::memcpy(base + at, &v, sizeof v);
}

/// The nearest timeline owner of `element` (GetCurrentSlide's 0x8227E48C), 0 when it is not a handle.
uint32_t Owner(uint32_t element) {
  uint32_t owner = 0u;
  return refii::headless::StatedOwner(element, &owner) == refii::headless::Answer::kYes ? owner : 0u;
}

/// `**(GUI+0x20)`, the HUD presentation every HUD reader passes (0x821CAAE0..0x821CAAE8).
uint32_t HudPresentation(const uint8_t* base) {
  const uint32_t gui = LoadGuestU32(base, kGuiManager);
  if (!Plausible(gui)) {
    return 0u;
  }
  const uint32_t hud = LoadGuestU32(base, gui + kGuiHud);
  return Plausible(hud) ? LoadGuestU32(base, hud) : 0u;
}

std::string DocumentOf(uint32_t presentation) {
  const std::string key = PresentationKey(presentation);
  return !key.empty() && key[0] == '=' ? key.substr(1) : key;
}

/// What Godot stated: the boxes' extents by (document, ordinal), and per (document, path) the
/// count of entries whose clock passed the Master end.
struct Extent {
  float min_x = 0.0f, min_y = 0.0f, max_x = 0.0f, max_y = 0.0f;
};

std::mutex g_mutex;
std::map<std::string, Extent> g_extents;
std::map<std::string, uint32_t> g_reached;
/// `@Ui.State` posts per (presentation, path) since the load, and the path a path setter's owner has.
std::map<std::pair<uint32_t, std::string>, uint32_t> g_entries;
std::map<std::pair<uint32_t, uint32_t>, std::string> g_owner_paths;

thread_local uint32_t t_path_presentation = 0u;
thread_local std::string t_path;

std::string ExtentKey(const std::string& document, int32_t ordinal) {
  return document + "#" + std::to_string(ordinal);
}

std::string ReachedKey(const std::string& document, const std::string& path) { return document + "|" + path; }

bool StatedExtent(uint32_t presentation, uint32_t element, Extent* out, std::string* key) {
  *key = ExtentKey(DocumentOf(presentation), refii::headless::FileOrdinal(element));
  std::lock_guard<std::mutex> lock(g_mutex);
  const auto it = g_extents.find(*key);
  if (it == g_extents.end()) {
    return false;
  }
  *out = it->second;
  return true;
}

void SetMeters(PPCContext& ctx, uint8_t* base, uint32_t box, float value) {
  const uint32_t gui = LoadGuestU32(base, kGuiManager);
  for (const uint32_t meter : {kBoxMeterX, kBoxMeterY}) {
    const uint32_t hud = LoadGuestU32(base, gui + kGuiHud);
    const uint32_t registries = LoadGuestU32(base, hud + kHudRegistries);
    const uint32_t meters = LoadGuestU32(base, registries + kRegistryMeters);
    Call(ctx, base, kMeterSet, LoadGuestU32(base, meters + kMeterTable), LoadGuestU32(base, box + meter), 0u, 0u,
         static_cast<double>(value));
  }
}

/// The visibility toggle / SetFlagActive on a record's Contents (0x821CAAD4..0x821CAAFC,
/// 0x82B873C0..0x82B873F0): r3 as the setter left it.
uint32_t ToggleContents(PPCContext& ctx, uint8_t* base, uint32_t record, uint32_t presentation, uint32_t visible) {
  const uint32_t contents = LoadGuestU32(base, record + kIconContents);
  if (base[record + kIconFlagMode] != 0u) {
    return Call(ctx, base, kSetFlagActive, contents, presentation, visible, 0u).r3.u32;
  }
  return Call(ctx, base, kVisibilityToggle, contents, presentation, visible).r3.u32;
}

uint32_t HiddenTick(PPCContext& ctx, uint8_t* base, uint32_t record, uint32_t visible) {
  if (LoadGuestU32(base, record + kIconContents) == 0u) {
    return 0u;
  }
  return ToggleContents(ctx, base, record, HudPresentation(base), visible);
}

std::atomic<uint64_t> g_box_steps{0};
std::atomic<uint64_t> g_slide_reads{0};
std::atomic<uint64_t> g_reached_reads{0};
std::atomic<uint64_t> g_edges{0};

}  // namespace

void refii::fable3::NoteStatedSlide(uint32_t element, uint32_t presentation, int32_t slide) {
  if (element == 0u || presentation == 0u) {
    return;
  }
  const uint32_t owner = Owner(element);
  uint8_t count = 0;
  if (owner == 0u || refii::headless::StatedSlideCount(owner, &count) != refii::headless::Answer::kYes ||
      slide < 1 || slide >= count) {
    return;
  }
  std::lock_guard<std::mutex> lock(g_mutex);
  if (t_path_presentation == presentation && !t_path.empty()) {
    g_owner_paths[{presentation, owner}] = t_path;
  }
}

void refii::fable3::NoteStatedEntry(uint32_t presentation, const std::string& path) {
  std::lock_guard<std::mutex> lock(g_mutex);
  ++g_entries[{presentation, path}];
}

void refii::fable3::ForgetPresentationReaders(uint32_t presentation) {
  const std::string reached = ReachedKey(DocumentOf(presentation), "");
  std::lock_guard<std::mutex> lock(g_mutex);
  for (auto it = g_reached.begin(); it != g_reached.end();) {
    it = it->first.compare(0, reached.size(), reached) == 0 ? g_reached.erase(it) : std::next(it);
  }
  for (auto it = g_entries.begin(); it != g_entries.end();) {
    it = it->first.first == presentation ? g_entries.erase(it) : std::next(it);
  }
  for (auto it = g_owner_paths.begin(); it != g_owner_paths.end();) {
    it = it->first.first == presentation ? g_owner_paths.erase(it) : std::next(it);
  }
}

refii::fable3::StatedPathScope::StatedPathScope(uint32_t presentation, const std::string& path)
    : presentation_(presentation), path_(path), outer_presentation_(t_path_presentation), outer_path_(t_path) {
  t_path_presentation = presentation_;
  t_path = path_;
}

refii::fable3::StatedPathScope::~StatedPathScope() {
  t_path_presentation = outer_presentation_;
  t_path = outer_path_;
}

/// `ui-box:<document>|<ordinal>|<min x>|<min y>|<max x>|<max y>` (Fable3HudDocument, as the document
/// stands), `ui-reached:<document>|<path>|<entries>` (Fable3HudDocument, the frame the entry's clock
/// passed the owner's Master end) and `ui-live:<document>|<ordinal>` (Fable3HudDocument, the frame a
/// component it plays came live).
void refii::fable3::AnswerUiReaderAction(const char* action) {
  const std::string text(action);
  const size_t colon = text.find(':');
  if (colon == std::string::npos) {
    REXLOG_WARN("Refii UI readers (Fable 3): action '{}' has no value; dropped", text);
    return;
  }
  const std::string verb = text.substr(0, colon);
  std::vector<std::string> fields;
  for (size_t at = colon + 1;;) {
    const size_t bar = text.find('|', at);
    fields.push_back(text.substr(at, bar == std::string::npos ? std::string::npos : bar - at));
    if (bar == std::string::npos) {
      break;
    }
    at = bar + 1;
  }
  if (verb == "ui-box" && fields.size() == 6u) {
    Extent e;
    e.min_x = std::strtof(fields[2].c_str(), nullptr);
    e.min_y = std::strtof(fields[3].c_str(), nullptr);
    e.max_x = std::strtof(fields[4].c_str(), nullptr);
    e.max_y = std::strtof(fields[5].c_str(), nullptr);
    const std::string key = ExtentKey(fields[0], std::atoi(fields[1].c_str()));
    {
      std::lock_guard<std::mutex> lock(g_mutex);
      g_extents[key] = e;
    }
    REXLOG_INFO("Refii UI readers (Fable 3): Godot states box {} — min ({:.7g}, {:.7g}), max ({:.7g}, {:.7g})", key,
                e.min_x, e.min_y, e.max_x, e.max_y);
    return;
  }
  if (verb == "ui-live" && fields.size() == 2u) {
    const uint32_t presentation = PresentationOfDocument(fields[0]);
    const uint32_t handle = presentation != 0u ? refii::headless::StatedHandleAt(presentation, std::atoi(fields[1].c_str())) : 0u;
    if (handle == 0u || Owner(handle) != handle) {
      static std::atomic<uint64_t> unnamed{0};
      const uint64_t n = ++unnamed;
      if (Milestone(n)) {
        REXLOG_INFO("Refii UI readers (Fable 3): {} #{} came live (Godot) — {}; no reader asks its slide (#{})",
                    fields[0], fields[1], handle == 0u ? "the guest never named it" : "not a timeline owner", n);
      }
      return;
    }
    const bool taken = refii::headless::StatedGotoSlide(handle, 1) == refii::headless::Answer::kYes;
    const uint64_t n = ++g_edges;
    if (Milestone(n)) {
      REXLOG_INFO("Refii UI readers (Fable 3): {} #{} came live (Godot) — {} (#{})", fields[0], fields[1],
                  taken ? "slide 1, as the walk's GoTo" : "it has fewer than two slides", n);
    }
    return;
  }
  if (verb == "ui-reached" && fields.size() == 3u) {
    std::lock_guard<std::mutex> lock(g_mutex);
    g_reached[ReachedKey(fields[0], fields[1])] = static_cast<uint32_t>(std::strtoul(fields[2].c_str(), nullptr, 10));
    return;
  }
  REXLOG_WARN("Refii UI readers (Fable 3): action '{}' not understood; dropped", text);
}

/// CHUDExpandableBox vf+12 — (r3 box, f1 the HUD's millisecond clock).
REX_EXTERN(rex_HudExpandableBox_Tick) {
  const uint32_t box = ctx.r3.u32;
  const double now = ctx.f1.f64;
  if (base == nullptr || !Plausible(box)) {
    return;
  }
  const float step = static_cast<float>(now - static_cast<double>(LoadGuestF32(base, box + kBoxLast)));
  StoreF32(base, box + kBoxLast, static_cast<float>(now));
  if (!(step > 0.0f)) {
    return;
  }
  const float countdown = LoadGuestF32(base, box + kBoxCountdown);
  if (countdown > 0.0f) {
    StoreF32(base, box + kBoxCountdown, countdown - step);
    return;
  }
  const uint32_t state = LoadGuestU32(base, box + kBoxState);
  if (state == 0u) {
    SetMeters(ctx, base, box, kMeterFull);
    StoreU32(base, box + kBoxState, 1u);
  } else if (state == 1u || state == 2u) {
    Extent e;
    std::string key;
    const uint32_t element = LoadGuestU32(base, box + kBoxElement);
    if (!StatedExtent(LoadGuestU32(base, box + kBoxPresentation), element, &e, &key)) {
      static std::atomic<uint64_t> unstated{0};
      const uint64_t n = ++unstated;
      if (Milestone(n)) {
        REXLOG_ERROR("Refii UI readers (Fable 3): box {} step {} at {:.0f} ms — Godot has not stated its extent; "
                     "the step waits (#{})",
                     key, state, now, n);
      }
      return;
    }
    if (state == 1u) {
      StoreF32(base, box + kBoxMaxX, e.max_x);
      StoreF32(base, box + kBoxMaxY, e.max_y);
      SetMeters(ctx, base, box, 0.0f);
      StoreU32(base, box + kBoxState, 2u);
    } else {
      StoreF32(base, box + kBoxMinX, e.min_x);
      StoreF32(base, box + kBoxMinY, e.min_y);
      StoreU32(base, box + kBoxState, 3u);
    }
  }
  StoreF32(base, box + kBoxCountdown, kBoxStep);
  const uint64_t n = ++g_box_steps;
  if (state < 3u && Milestone(n)) {
    REXLOG_INFO("Refii UI readers (Fable 3): box 0x{:08X} step {} -> {} at {:.0f} ms (#{})", box, state,
                LoadGuestU32(base, box + kBoxState), now, n);
  }
}

/// `sub_8227E438` — (r3 element, r4 presentation) -> the owner's current slide.
REX_EXTERN(rex_GuiElement_CurrentSlide) {
  const uint32_t element = ctx.r3.u32;
  const uint32_t presentation = ctx.r4.u32;
  const uint32_t via = static_cast<uint32_t>(ctx.lr);
  const uint32_t owner = presentation != 0u ? Owner(element) : 0u;
  uint8_t current = 0;
  uint8_t count = 0;
  if (owner == 0u || refii::headless::StatedCurrentSlide(owner, &current) != refii::headless::Answer::kYes ||
      refii::headless::StatedSlideCount(owner, &count) != refii::headless::Answer::kYes) {
    ctx.r3.u32 = 0u;
    return;
  }
  const uint32_t slide = current != 0u ? current : (count > 1u ? 1u : 0u);
  ctx.r3.u32 = slide;
  const uint64_t reads = ++g_slide_reads;
  if (Milestone(reads)) {
    REXLOG_INFO("Refii UI readers (Fable 3): current slide read #{} from 0x{:08X} = {} ({} #{})", reads, via, slide,
                DocumentOf(presentation), refii::headless::FileOrdinal(owner));
  }
}

/// `sub_821CAA98` — the released pooled icons and the three specials, every frame.
REX_EXTERN(rex_WorldIcon_ReleasedTick) {
  if (base == nullptr) {
    return;
  }
  const uint32_t table = kIconTable;
  for (uint32_t slot = 0; slot < kIconSlots; ++slot) {
    const uint32_t record = table + slot * kIconStride;
    if (base[record + kIconInUse] == 0u) {
      HiddenTick(ctx, base, record, 0u);
    }
  }
  for (const uint32_t record : {static_cast<uint32_t>(kIconFill), static_cast<uint32_t>(kIconHero),
                                static_cast<uint32_t>(kIconHenchman)}) {
    if (base[record + kIconInUse] == 0u) {
      HiddenTick(ctx, base, record, 0u);
    }
  }
}

/// `sub_82B873A0` — (r3 record, r4 visible).
REX_EXTERN(rex_WorldIcon_HiddenTick) {
  if (base == nullptr || !Plausible(ctx.r3.u32)) {
    return;
  }
  ctx.r3.u32 = HiddenTick(ctx, base, ctx.r3.u32, ctx.r4.u32);
}

/// `sub_82878358` — (r3 element, r4 presentation) -> whether the owner's clock passed its Master end.
REX_EXTERN(rex_GuiElement_TimeReached) {
  const uint32_t element = ctx.r3.u32;
  const uint32_t presentation = ctx.r4.u32;
  if (base == nullptr || element == 0u || presentation == 0u) {
    ctx.r3.u32 = 0u;
    return;
  }
  const uint32_t owner = Owner(element);
  if (owner == 0u) {
    ctx.r3.u32 = 0u;
    return;
  }
  const std::string document = DocumentOf(presentation);
  std::string path;
  uint32_t entries = 0u;
  uint32_t reached = 0u;
  bool stated = false;
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    const auto named = g_owner_paths.find({presentation, owner});
    if (named != g_owner_paths.end()) {
      path = named->second;
      const auto counted = g_entries.find({presentation, path});
      entries = counted != g_entries.end() ? counted->second : 0u;
      const auto told = g_reached.find(ReachedKey(document, path));
      reached = told != g_reached.end() ? told->second : 0u;
      stated = true;
    }
  }
  const bool answer = !stated || entries == 0u || reached == entries;
  ctx.r3.u32 = answer ? 1u : 0u;
  const uint64_t n = ++g_reached_reads;
  if (Milestone(n)) {
    REXLOG_INFO("Refii UI readers (Fable 3): Master end of '{}' ({} entry {}, Godot reached {}) = {} (#{})",
                stated ? path : std::string("(no path setter entered it)"), document, entries, reached, answer, n);
  }
}
