// refii - HEADLESS GAMEFACE PRESENTATIONS, Fable 3's hooks. The guest keeps no UI scene: no
// CPresentation of its own, no element tree, no Lua engine and no per-frame presentation update.
// The work is the shared headless core's (core/headless_presentation.h,
// src/payload/src/core/headless_presentation.cpp), as Fable 2's
// (Fable2/src/vfs/src/hooks/gui_headless_presentation.cpp); these hooks are its shims and
// headless_table.h is Fable 3's Gameface for it. TU3 addresses; the disc's rows are in
// Fable3/versions/Fable3/refiii_config.toml.
//
// THE CREATE. CGameface (K, vtable 0x82002A80) vf +28 `sub_82C558F8`(K, char* path) is the only
// maker: its one caller is the load `sub_825F27D8` (`lwz r10,0x1C(r11)` / `bctrl` 0x825F286C..
// 0x825F2874). The core answers it with a zeroed 0x36C block carrying the presentation, service and
// slide-manager vtables, the stand-in scene and the root's handle. The object must exist: some 140
// box Show / HandleInput sites null-gate on it (the box loader `sub_829588C8`). Every element is a
// handle (stated_element_hooks.cpp).
//
// THE DESTROY. K vf +32 0x82C55C40(K, pres) is the release helper's (`sub_82333F28` 0x82334004, on
// the CURRENT GUI's gameface), so it is keyed by the presentation; the core frees a headless one.
//
// THE GAMEFACE (K) IS THE HOST'S (engine cut S6). The GUI (global 0x83596F84, rebuilt by
// `sub_822D9C80`) holds K at GUI+16 only; its maker (0x825ECD3C, `li` 600 at 0x825ECC78) still
// builds the render engine and the input object, and its ctor TU3 `sub_82C553C8` / disc
// `sub_82C20CC8` is replaced: the zeroed block, the guest vtable 0x82002A80, a guest CTimer at +8
// (`sub_82C569D0`) and the input object at +0x208. The clock stays a guest object because vf +52 is
// the linker-folded `addi r3,r3,8; blr` (0x82F68250, also hkpContactMgrFactory's); the GUI's update
// (0x82254EC4, ms, restart through vf +12 when negative) and the screen activation (0x825F2DAC)
// read it through that slot. No scene manager (+0x20C), deserializer (+0x210), data store (+0x214),
// gameplan (+0x248) or kernel list (+40). The input object's +168 stays 0 (the ctor's `stw
// r29,0xA8(r11)` 0x82C555B0 is not made); its only reader, its vf +24 `sub_82C56680`, returns on 0
// (0x82C5668C). The callback word +588 stays 0: its writers are the ctor and vf +36, which nothing
// outside Gameface calls. Replaced: the ctor, the dtor `sub_82C55610`, +16, +20, +24, +32, +40, +44,
// +48. Guest code still: +0 (`sub_82C555C0`, which frees the block), +4, +8, +12, +36, +52, +56, +60.
// Outside callers of the slots (every one, both builds): +28 0x825F2874, +32 0x82334004, +40
// 0x821A7AB4, +44 0x822551C8, +48 0x82199A78 / 0x8225D084, +52 0x82254EC4 / 0x825F2DAC, +0 the GUI
// shutdown 0x82333328 and the members dtor 0x82332274; none uses +40/+44/+48's or +32's answer.
//
// THE PRESENTATION'S OWN SLOTS the game calls (both builds' bodies instruction-identical):
//   vf +4 Update TU3 `sub_8225D9F0` / disc `sub_8225E868` — called by the screen activation
//     `sub_825F2D80` (0x825F2DD4 / disc 0x825C808C); on the zeroed block its track pass would call
//     through Mgr:Animations' vtable word 0 (`lwz r11,0x2DC(r30)` / `lwz r10,4(r11)` 0x8225DB94..
//     0x8225DB9C). Nothing runs: the block has no clock, ring, tree or tracks, and Godot plays the
//     document.
//   vf +24 TU3 `sub_82C69988` / disc `sub_82C35278` — pushes a 20-byte event record into the ring
//     at +0x144 (`addi r3,r3,0x144; bl sub_82C69C48` 0x82C699E0) and answers it: the HUD's post-init
//     0x824CD6D8, Lua_GUI_SetDPadCenter 0x8254BD5C and `sub_82601F50` 0x82602030. No C++ drains it;
//     the record is written to a host-owned guest record.
//   vf +108 / +112 — below.
//
// THE PARAMETERS ARE A HOST STORE (core/headless_presentation.h). The presentation's "Parameters"
// element is the mailbox the C++ hands the shipped Lua its inputs through; each presentation's store
// holds what the file's Parameters element authors, and the two functions that reach the element are
// answered from it — neither runs, so no guest Parameters element is made. Both build the same chain
// (both builds identical): the presentation (r5, or 0 = the loader's current, `lwz r3,0x38(r11)` /
// `bl sub_825F28A8` 0x82B746BC..0x82B746C4), the "Parameters" element through the contracts walk
// `sub_82877968` (pres vf +56; its only two callers are these setters, 0x82B74644 / 0x82B7471C), then
//   SetParameterString `sub_82B74698` (disc `sub_82B3F470`): service vf +12 on the attribute
//     (`bctrl` 0x82B7475C, result ignored) and a copy into the record's text (`lwz r3,0x80+var_2C(r1)` /
//     `bl sub_82C683D8` 0x82B74778..0x82B7477C) — a miss copies through an uninitialised stack word;
//   SetParameterFloat `sub_82B745D0` (disc `sub_82B3F3A8`): service vf +72 (`lwz r10,0x48(r11)` /
//     `bctrl` 0x82B74680..0x82B74688), which writes only an attribute the element carries.
// The store writes only a carried attribute of the right kind; a miss writes nothing and is logged.
// Nothing reads a parameter back in C++: the walk has no other caller and the "Parameters" global
// `dword_8359E7EC` is read only by it (0x828779EC; the rest are its static init and atexit), so the
// only reader is the presentation's Lua, which never runs. Callers (identical on both builds):
// `String1` — RunInitScripts (the scripts directory, 0x825F29BC), the direction stores
// `sub_82601C90` / `sub_82601DF0`, the mini-game bar (the button colour 0x82716124, the background
// 0x827144F0), `sub_825F9F38`; `Float1..3` — the thumbstick stores `sub_82601518` / `sub_82601638`
// and the mini-game bar. Each set is stated as `@Lua.Param(pres, name, value)` (Fable3LuaContracts
// in Godot runs the script's layout rule from it).
//
// THE EVENT REGISTRATIONS ARE STATED. CPresentation vtable +108 (TU3 `sub_82C69360`, disc
// `sub_82C34C50`) files `fn(ctx, &{element, id})` for (element, id) in the list at pres +0x17C
// (`addi r3,r3,0x17C` / `bl sub_82C6DE00` 0x82C69378..0x82C69380) and sets the element's flag 0x80
// through service vf +80 (0x82C69384..0x82C6939C); +112 (TU3 `sub_82C693C0`, disc `sub_82C34CB0`)
// removes it. Both are host replacements: the call is kept with its presentation
// (refii::headless::NoteRegistration) and +108's flag lands in the handle's host flags. The
// game makes one: the HUD's init (TU3 `sub_824CD3F8` 0x824CD560, disc `sub_825C18C0` 0x825C1A28)
// registers `sub_824CD610` (disc `sub_825C1AD8`) with the HUD as ctx for FINISHED_TRANSITIONING_TO_GAME
// (`dword_83598CA0`, disc `dword_835588D4`) on hud.bgf's root (GuiPresentation_Root 0x824CD53C). The
// guest delivers events only through the presentation's queue drain, from Lua or the file's event
// table, neither of which runs; there is no host table of posted events on Fable 3.
//
// THE LUA. Every chunk the C++ runs in a presentation passes `sub_82877A48`(CCharString* script,
// pres): g_HUD:Enable/Disable, RunInitScripts' three, GUI:OnEnter/OnExit, the thumbstick and
// direction stores, g_MiniGameBar:*, g_DisplayBox:* and the Execute{Hud,Screen,Interaction}Script
// natives. Read from the shipped scripts (F3 phase B, B6): none posts anything the C++ waits on —
// each sets elements or Lua state; the box close, the mini-game counts and the HUD's state are the
// C++'s own. A headless presentation has no engine (+0x1C 0), so each chunk is skipped at the runner
// rather than run (the runner's engine read `sub_82334028` 0x8233404C would fault) or queued in GUI
// +116 with a raw presentation pointer. Each skipped chunk is posted as `@Lua.Chunk(pres, text)`:
// the text is the call the guest made (e.g. `g_MiniGameBar:SpawnButton()` at 0x82716154,
// `g_MiniGameBar:SetPipTravel(<t>)` at 0x82714B88), and Godot runs the script's layout rule for it.

#include <atomic>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <string>
#include <unordered_map>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "gui_statements.h"
#include "headless_table.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_CPresentation_Update);
REX_EXTERN(__imp__rex_CPresentation_QueueEvent);
REX_EXTERN(__imp__rex_CPresentation_RegisterEventCallback);
REX_EXTERN(__imp__rex_CPresentation_UnregisterEventCallback);
REX_EXTERN(__imp__rex_GuiPresentation_RunScript);

namespace {

using namespace refii::fable3;

// The kernel heap the deserialiser's own element block comes from ("Mgr:Elements",
// 0x82C640D0..0x82C640F4: allocator, size, class, file, line 0x57): the presentation blocks and the
// handles.
const refii::guest::Addr kKernelAllocator{0x82C680A0u};
const refii::guest::Addr kKernelAlloc{0x82C68A60u};
const refii::guest::Addr kKernelFree{0x82C68BB0u};
const refii::guest::Addr kElementsClassName{0x820E4CC8u};  // "Mgr:Elements"
const refii::guest::Addr kElementsSourceName{0x820E4C50u};
constexpr uint32_t kElementsSourceLine = 0x57u;

// THE GAMEFACE: its vtable (the ctor's `stw` 0x82C553EC), the guest timer's ctor (vtable 0x82001824,
// QueryPerformanceFrequency, start), the block's size (`sub_8221E3B0(600)` 0x825ECC78), the input
// object's word (vf +60 `lwz r3,0x208(r3)`) and the loader's create slot (`lwz r10,0x1C(r11)`).
const refii::guest::Addr kGamefaceVtable{0x82002A80u};
const refii::guest::Addr kTimerCtor{0x82C569D0u};
constexpr uint32_t kGamefaceSize = 600u;
constexpr uint32_t kGamefaceInput = 0x208u;
constexpr uint32_t kGamefaceSlotCreate = 28u;

constexpr uint32_t kQueueRecordBytes = 20u;  // vf +24's record, 5 words (`li r9,5` 0x82C699E8)
constexpr uint16_t kFlagRegistered = 0x80u;   // vf +108's `vf+80(element, 0x80, 1)` (0x82C69390)

// SetParameterString's current presentation (`lwz r3,0x38(r11)` of the GUI manager, then
// `bl sub_825F28A8` 0x82B746BC..0x82B746C4).
const refii::guest::Addr kGuiManager{0x83596F84u};
constexpr uint32_t kGuiLoader = 0x38u;
const refii::guest::Addr kLoaderCurrent{0x825F28A8u};

void StoreWord(uint8_t* base, uint32_t at, uint32_t value) {
  const uint32_t v = __builtin_bswap32(value);
  std::memcpy(base + at, &v, sizeof v);
}

bool Milestone(uint64_t n) { return n <= 32u || (n & (n - 1u)) == 0u; }

PPCFunc* Find(uint32_t address) {
  if (address == 0u) {
    return nullptr;
  }
  for (PPCFuncMapping* mapping = PPCFuncMappings; mapping != nullptr && mapping->host != nullptr; ++mapping) {
    if (static_cast<uint32_t>(mapping->guest) == address) {
      return mapping->host;
    }
  }
  return nullptr;
}

PPCFunc* Resolved(uint32_t address) {
  static std::mutex m;
  static std::unordered_map<uint32_t, PPCFunc*> cache;
  std::lock_guard<std::mutex> lock(m);
  const auto it = cache.find(address);
  if (it != cache.end()) {
    return it->second;
  }
  PPCFunc* fn = Find(address);
  cache.emplace(address, fn);
  if (fn == nullptr) {
    REXLOG_ERROR("Refii headless presentation (Fable 3): guest function 0x{:08X} is not a recompiled entry", address);
  }
  return fn;
}

uint32_t Call(PPCContext& ctx, uint8_t* base, uint32_t address, uint32_t r3, uint32_t r4 = 0u, uint32_t r5 = 0u,
              uint32_t r6 = 0u, uint32_t r7 = 0u) {
  PPCFunc* fn = Resolved(address);
  if (fn == nullptr) {
    return 0u;
  }
  PPCContext call = ctx;
  call.r3.u32 = r3;
  call.r4.u32 = r4;
  call.r5.u32 = r5;
  call.r6.u32 = r6;
  call.r7.u32 = r7;
  call.ctr.u32 = address;
  fn(call, base);
  return call.r3.u32;
}

/// Every block the core takes from the kernel heap, with its size: the kernel free files the
/// block back by size (`sub_82C68BB0`'s size class, `16 * (size + 196)` and the pool walk).
std::mutex g_blocks_mutex;
std::unordered_map<uint32_t, uint32_t> g_blocks;

uint32_t GuestAlloc(PPCContext& ctx, uint8_t* base, uint32_t size) {
  const uint32_t allocator = Call(ctx, base, kKernelAllocator, 0u);
  const uint32_t block =
      Call(ctx, base, kKernelAlloc, allocator, size, kElementsClassName, kElementsSourceName, kElementsSourceLine);
  if (block != 0u) {
    std::lock_guard<std::mutex> lock(g_blocks_mutex);
    g_blocks[block] = size;
  }
  return block;
}

void GuestFree(PPCContext& ctx, uint8_t* base, uint32_t address) {
  uint32_t size = 0u;
  {
    std::lock_guard<std::mutex> lock(g_blocks_mutex);
    const auto it = g_blocks.find(address);
    if (it == g_blocks.end()) {
      REXLOG_WARN("Refii headless presentation (Fable 3): 0x{:08X} is not a block the core took; not freed", address);
      return;
    }
    size = it->second;
    g_blocks.erase(it);
  }
  const uint32_t allocator = Call(ctx, base, kKernelAllocator, 0u);
  Call(ctx, base, kKernelFree, allocator, address, size);
}

const refii::headless::GamefaceTable& Table() {
  static const refii::headless::GamefaceTable table = [] {
    refii::headless::GamefaceTable t = HeadlessTableAddresses();
    t.find = &Find;
    t.alloc = &GuestAlloc;
    t.free = &GuestFree;
    return t;
  }();
  return table;
}

std::atomic<uint64_t> g_parameters{0};
std::atomic<uint64_t> g_scripts{0};

}  // namespace

const refii::headless::GamefaceTable& refii::fable3::Gameface() { return Table(); }

/// K vf +28 — (r3 K, r4 path) -> a headless presentation, or 0.
REX_EXTERN(rex_CGameface_CreatePresentation) {
  ctx.r3.u32 = refii::headless::Create(Table(), ctx, base, ctx.r3.u32, ctx.r4.u32);
}

/// K vf +32 — (r3 K, r4 presentation). A presentation the core does not hold answers 0 as well: the
/// shipped body removes it from the scene manager (`lwz r3,0x20C`, vf +8 0x82C55CAC) and the kernel
/// list (`sub_82C6AF68` 0x82C55CBC), neither of which exists.
REX_EXTERN(rex_CGameface_DestroyPresentation) {
  if (!refii::headless::Destroy(Table(), ctx, base, ctx.r3.u32, ctx.r4.u32)) {
    static std::atomic<uint64_t> unknown{0};
    const uint64_t n = ++unknown;
    if (Milestone(n)) {
      REXLOG_INFO("Refii gameface (Fable 3) 0x{:08X}: destroy of 0x{:08X}, which is no live presentation (#{})",
                  ctx.r3.u32, ctx.r4.u32, n);
    }
  }
  ctx.r3.u32 = 0u;
}

/// K's ctor (TU3 `sub_82C553C8`, disc `sub_82C20CC8`): r3 the 600-byte block, r4 the input object,
/// r5 the render engine (held only by the scene manager). See THE GAMEFACE.
REX_EXTERN(rex_CGameface_Construct) {
  const uint32_t gameface = ctx.r3.u32;
  const uint32_t input = ctx.r4.u32;
  std::memset(base + gameface, 0, kGamefaceSize);
  StoreWord(base, gameface, kGamefaceVtable);
  Call(ctx, base, kTimerCtor, gameface + Table().gameface_clock);
  StoreWord(base, gameface + kGamefaceInput, input);
  REXLOG_INFO("Refii gameface (Fable 3) 0x{:08X} made (input 0x{:08X})", gameface, input);
  ctx.r3.u32 = gameface;
}

/// K's dtor (TU3 `sub_82C55610`, disc `sub_82C20F10`), (r3 K): called only by the deleting dtor
/// (0x82C555DC), which then frees the block. Reads only `this`: the GUI shutdown (0x82333328) and
/// the members dtor (0x82332274) delete K while the GUI global may already name the next GUI.
REX_EXTERN(rex_CGameface_Destructor) {
  const uint32_t gameface = ctx.r3.u32;
  REXLOG_INFO("Refii gameface (Fable 3) 0x{:08X} destroyed: {} presentation(s) on it, callback +588 = 0x{:08X}",
              gameface, refii::headless::CountOn(gameface), LoadGuestU32(base, gameface + Table().gameface_callback));
  refii::headless::DestroyRemaining(Table(), ctx, base, gameface);
  ctx.r3.u32 = gameface;
}

/// K vf +16, the loader (TU3 `sub_82C556B8`, disc `sub_82C20FB8`): (r3 K, r4 path, r5 bool* is-bgf).
/// "bgf" after the last '.' is this K's create (vf +28, 0x82C557B4) with `*r5 = 1`; "bgp" is the
/// gameplan's load with `*r5 = 0`; anything else 0 with `*r5` untouched. There is no gameplan.
REX_EXTERN(rex_CGameface_Load) {
  const uint32_t gameface = ctx.r3.u32;
  const uint32_t path = ctx.r4.u32;
  const uint32_t is_bgf = ctx.r5.u32;
  std::string name;
  for (uint32_t i = 0; path != 0u && i < 260u && base[path + i] != 0u; ++i) {
    name.push_back(static_cast<char>(base[path + i]));
  }
  const size_t dot = name.rfind('.');
  const std::string extension = dot == std::string::npos ? std::string() : name.substr(dot + 1u);
  uint32_t made = 0u;
  if (extension == "bgf") {
    made = Call(ctx, base, LoadGuestU32(base, LoadGuestU32(base, gameface) + kGamefaceSlotCreate), gameface, path);
    base[is_bgf] = 1u;
  } else if (extension == "bgp") {
    base[is_bgf] = 0u;
    REXLOG_WARN("Refii gameface (Fable 3) 0x{:08X}: gameplan '{}' asked for; there is no gameplan", gameface, name);
  }
  ctx.r3.u32 = made;
}

/// K vf +20 (`lwz r3,0x248(r3); b` the gameplan's load, 0x82C558A8) and +24 (`sub_82C558B0`: a
/// name's hash looked up in gameplan +0x68): no gameplan, both 0.
REX_EXTERN(rex_CGameface_GameplanLoad) { ctx.r3.u32 = 0u; }

REX_EXTERN(rex_CGameface_GameplanFind) { ctx.r3.u32 = 0u; }

/// K vf +40 (TU3 `sub_82279AC8`, disc `sub_8227B7C0`): (r3 K, r4 input-pass byte). The shipped pass
/// ticks the input object, the scene manager, the gameplan (`sub_82293478(+0x248)` 0x82279B30) and
/// each presentation on the kernel list; none exists. Its callers (0x821A7AB4) ignore r3.
REX_EXTERN(rex_CGameface_Update) {}

/// K vf +44 (0x82290818, disc 0x82292510): the scene manager's vf +12 (`lwz r3,0x20C(r3)` … `bctr`),
/// from the GUI's update (0x822551C8). No scene manager: a no-op.
REX_EXTERN(rex_CGameface_ResetScenes) {}

/// K vf +48 (0x82179260, disc 0x82179368): the scene manager's vf +16, from `sub_82199990`
/// (0x82199A78) and GUI vf +12 (0x8225D084). A no-op.
REX_EXTERN(rex_CGameface_RenderScenes) {}

namespace {

/// The presentation a parameter setter names: r5, or the loader's current for 0.
uint32_t ParameterPresentation(PPCContext& ctx, uint8_t* base, uint32_t presentation) {
  if (presentation == 0u && base != nullptr) {
    const uint32_t gui = LoadGuestU32(base, kGuiManager);
    if (Plausible(gui)) {
      presentation = Call(ctx, base, kLoaderCurrent, LoadGuestU32(base, gui + kGuiLoader));
    }
  }
  return presentation;
}

/// A set the store did not take: no presentation, or the Parameters element does not carry the
/// attribute as that kind — nothing lands, as on the shipped element.
void NoteParameterMiss(const char* what, uint32_t presentation, const std::string& name) {
  static std::atomic<uint64_t> misses{0};
  const uint64_t n = ++misses;
  if (Milestone(n)) {
    REXLOG_INFO("Refii headless presentation (Fable 3): {} parameter '{}' on 0x{:08X} not carried by its "
                "Parameters element — nothing written (#{})",
                what, name, presentation, n);
  }
}

uint32_t ParamId(const std::string& name) {
  return refii::headless::NameHash(name.c_str()) & Table().attribute_id_mask;
}

/// Whether `presentation` is not the core's: the guest's own slot runs, logged.
bool Foreign(const char* slot, uint32_t presentation) {
  if (refii::headless::IsHeadless(presentation)) {
    return false;
  }
  static std::atomic<uint64_t> seen{0};
  const uint64_t n = ++seen;
  if (Milestone(n)) {
    REXLOG_WARN("Refii headless presentation (Fable 3): {} reached with 0x{:08X}, not a headless presentation — "
                "the guest's own function ran (#{})",
                slot, presentation, n);
  }
  return true;
}

void StateRegistration(const PPCContext& ctx, bool add) {
  const refii::headless::Registration r{ctx.r4.u32, ctx.r5.u32, ctx.r6.u32, ctx.r7.u32};
  refii::headless::NoteRegistration(ctx.r3.u32, r, add);
  static std::atomic<uint64_t> stated{0};
  const uint64_t n = ++stated;
  if (Milestone(n)) {
    REXLOG_INFO("Refii headless presentation (Fable 3): event {} on 0x{:08X} — element 0x{:08X}, id 0x{:08X}, "
                "fn 0x{:08X}, ctx 0x{:08X} (#{})",
                add ? "registered" : "unregistered", ctx.r3.u32, r.element, r.id, r.fn, r.ctx, n);
  }
}

}  // namespace

/// SetParameterString — (r3 name, r4 value, r5 presentation or 0): into the store.
REX_EXTERN(rex_GuiPresentation_SetParameterString) {
  const uint32_t presentation = ParameterPresentation(ctx, base, ctx.r5.u32);
  const std::string name = ReadCharString(base, ctx.r3.u32, 64u);
  const std::string value = ReadCharString(base, ctx.r4.u32, 160u);
  if (presentation != 0u) {
    Statement("@Lua.Param").Add(Text(PresentationKey(presentation))).Add(Text(name)).Add(Text(value)).Post();
  }
  if (presentation == 0u || !refii::headless::ParamSetText(presentation, ParamId(name), value)) {
    NoteParameterMiss("string", presentation, name);
  }
  const uint64_t n = ++g_parameters;
  if (Milestone(n)) {
    REXLOG_INFO("Refii headless presentation (Fable 3): parameter '{}' = '{}' of 0x{:08X} (#{})", name, value,
                presentation, n);
  }
}

/// SetParameterFloat — (r3 name, f1 value, r5 presentation or 0): into the store, narrowed to the
/// single the attribute holds.
REX_EXTERN(rex_GuiPresentation_SetParameterFloat) {
  const uint32_t presentation = ParameterPresentation(ctx, base, ctx.r5.u32);
  const std::string name = ReadCharString(base, ctx.r3.u32, 64u);
  const float value = static_cast<float>(ctx.f1.f64);
  if (presentation != 0u) {
    Statement("@Lua.Param").Add(Text(PresentationKey(presentation))).Add(Text(name)).Add(Number(value)).Post();
  }
  uint32_t bits = 0;
  std::memcpy(&bits, &value, sizeof bits);
  if (presentation == 0u || !refii::headless::ParamSetNumber(presentation, ParamId(name), bits)) {
    NoteParameterMiss("number", presentation, name);
  }
}

/// CPresentation vtable +4 — (r3 presentation, r4 clock): nothing on a headless block.
REX_EXTERN(rex_CPresentation_Update) {
  if (Foreign("vf +4", ctx.r3.u32)) {
    __imp__rex_CPresentation_Update(ctx, base);
  }
}

/// CPresentation vtable +24 — (r3 presentation, r4 the event hash, r5 the element, r6 / r7 pointers to
/// two argument words or 0, r8 / r9 two bytes): the record the original fills (0x82C69994..0x82C699DC:
/// words r5, r4, *r6, *r7, then r8, r9 and 0xC0 in the last word's bytes), written to a host-owned
/// guest record, which is the answer.
REX_EXTERN(rex_CPresentation_QueueEvent) {
  if (Foreign("vf +24", ctx.r3.u32)) {
    __imp__rex_CPresentation_QueueEvent(ctx, base);
    return;
  }
  static std::atomic<uint32_t> record{0u};
  uint32_t at = record.load();
  if (at == 0u) {
    const uint32_t made = GuestAlloc(ctx, base, kQueueRecordBytes);
    uint32_t none = 0u;
    if (made != 0u && !record.compare_exchange_strong(none, made)) {
      GuestFree(ctx, base, made);
    }
    at = record.load();
  }
  if (at != 0u) {
    StoreWord(base, at + 0u, ctx.r5.u32);
    StoreWord(base, at + 4u, ctx.r4.u32);
    StoreWord(base, at + 8u, ctx.r6.u32 != 0u ? LoadGuestU32(base, ctx.r6.u32) : 0u);
    StoreWord(base, at + 12u, ctx.r7.u32 != 0u ? LoadGuestU32(base, ctx.r7.u32) : 0u);
    StoreWord(base, at + 16u, ((ctx.r8.u32 & 0xFFu) << 24) | ((ctx.r9.u32 & 0xFFu) << 16) | 0xC000u);
  }
  static std::atomic<uint64_t> pushed{0};
  const uint64_t n = ++pushed;
  if (Milestone(n)) {
    REXLOG_INFO("Refii headless presentation (Fable 3): event 0x{:08X} pushed on 0x{:08X} for element 0x{:08X} — "
                "nothing drains it (#{})",
                ctx.r4.u32, ctx.r3.u32, ctx.r5.u32, n);
  }
  ctx.r3.u32 = at;
}

/// CPresentation vtable +108 — (r3 presentation, r4 element, r5 id, r6 fn, r7 ctx).
REX_EXTERN(rex_CPresentation_RegisterEventCallback) {
  if (Foreign("vf +108", ctx.r3.u32)) {
    __imp__rex_CPresentation_RegisterEventCallback(ctx, base);
    return;
  }
  StateRegistration(ctx, true);
  refii::headless::StatedFlagsWrite(ctx.r4.u32, kFlagRegistered, true);
}

/// CPresentation vtable +112 — the same four, removed (the guest's own leaves flag 0x80 set).
REX_EXTERN(rex_CPresentation_UnregisterEventCallback) {
  if (Foreign("vf +112", ctx.r3.u32)) {
    __imp__rex_CPresentation_UnregisterEventCallback(ctx, base);
    return;
  }
  StateRegistration(ctx, false);
  ctx.r3.u32 = 0u;
}

/// The chunk runner — (r3 CCharString* script, r4 presentation): skipped on a headless one.
REX_EXTERN(rex_GuiPresentation_RunScript) {
  const uint32_t presentation = ctx.r4.u32;
  if (presentation == 0u || !refii::headless::IsHeadless(presentation)) {
    __imp__rex_GuiPresentation_RunScript(ctx, base);
    return;
  }
  const uint64_t n = ++g_scripts;
  if (base != nullptr) {
    const std::string script = ReadCharString(base, ctx.r3.u32, 256u);
    Statement("@Lua.Chunk").Add(Text(PresentationKey(presentation))).Add(Text(script)).Post();
    if (Milestone(n)) {
      REXLOG_INFO("Refii headless presentation (Fable 3): chunk '{}' for 0x{:08X} not run — stated, Godot "
                  "draws the presentation (#{})",
                  script, presentation, n);
    }
  }
  // r3 left as given, as the runner's own exit for no presentation leaves it (`beq` 0x82877A60 to
  // the epilogue 0x82877AAC).
}
