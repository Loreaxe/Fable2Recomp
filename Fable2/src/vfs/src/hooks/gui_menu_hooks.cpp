// C++ stages attributes onto the "Parameters" presentation element (Lua's `g_Param.element`),
// then runs a one-line `g_Menu:` chunk that reads them back (28 `g_Menu:` + 3 `g_MenuFormatter:`
// literals, all via `sub_82426470`). Presentation-element state (`sub_82426798`, `sub_82426260`,
// `sub_82425DD0`, ...) does not pass through here.

#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <utility>
#include <vector>

#include "vfs_hooks_private.h"
#include "core/guest_state.h"
#include "gui/gui_menu_chunks.h"
#include "hooks/frontend_stated.h"
#include "hooks/ppc_externs.h"
#include "core/guest_build.h"
#include "core/payload_bridge_fable2.h"
#include "core/headless_presentation.h"

// The chunk runner `sub_82426470` is hooked in resource_manager_open.cpp.


namespace {

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

/// The in-game screen that is up, as the screen setter stated it (TU1 numbering, 31 for none) —
/// the id every reader below is asked under.
uint32_t CurrentScreen() {
    const uint32_t stated = refii_bridge_stated_screen_now();
    return stated == 0xFFFFFFFFu ? 31u : stated;
}

/// One guest call on a scratch frame carved below the caller's stack; every register restored.
void CallOnFrame(PPCContext& ctx, uint8_t* base, uint32_t frame, uint32_t tu1, uint32_t r3,
                 uint32_t r4, uint32_t r5, double f1 = 0.0) {
    const uint32_t address = refii::guest::Address(tu1);
    PPCFunc* fn = refii::vfs::FindGuestFunction(address);
    if (fn == nullptr) return;
    const PPCContext saved = ctx;
    ctx.r1.u32 = frame;
    ctx.r3.u32 = r3;
    ctx.r4.u32 = r4;
    ctx.r5.u32 = r5;
    ctx.r6.u32 = 0u;
    ctx.f1.f64 = f1;
    ctx.ctr.u32 = address;
    fn(ctx, base);
    ctx = saved;
}

/// A narrow CCharString of `value` at `handle`, built from `text` with the guest's own
/// constructor `sub_8222CED0(handle, text, -1)` (disc `sub_8222CF18`).
void BuildString(PPCContext& ctx, uint8_t* base, uint32_t frame, uint32_t text, uint32_t handle,
                 const char* value) {
    uint32_t i = 0;
    for (; value[i] != '\0' && i < 127u; ++i) {
        base[text + i] = static_cast<uint8_t>(value[i]);
    }
    base[text + i] = 0;
    refii::vfs::StoreGuestU32(base, handle, 0u);
    CallOnFrame(ctx, base, frame, 0x8222CED0u, handle, text, 0xFFFFFFFFu);
}

/// The row the reader answers with: Godot's statement for the current screen, else the shipped
/// script's own no-row answer.
struct AnsweredRow {
    int32_t event_type = 0;
    int32_t id = 0;
    char name[128] = {};
    char label[128] = {};
};

/// `g_Menu:GetHighlightedMenuItemData()` with nothing highlighted (`expandablemenu.lua`, the
/// `else` arm): String1 "No MenuItem", String2 "No Text Tag", Float1..3 0 — so `out[0]` 0 and
/// `out[1]` 0 through 0x826c1c90 / 0x826c1ce4.
AnsweredRow NoRow() {
    AnsweredRow row;
    std::snprintf(row.name, sizeof(row.name), "%s", "No MenuItem");
    std::snprintf(row.label, sizeof(row.label), "%s", "No Text Tag");
    return row;
}

/// The abilities screen's category header type (`Submenu`, 301). Its refresh `sub_826C3C78`
/// (disc `sub_8259FC18`) builds an ability record from the highlight unless the type is 301 or
/// 23 (`cmpwi r11,0x12D` / `cmpwi r11,0x17` at 0x826C3CFC / 0x826C3D04, disc 0x8259FC9C ..), and
/// that build leaves `Type` unwritten when the id is no ability (`sub_82896868` stores only
/// +0x14..+0x20) — then indexes by it (`sub_826C2F98` → `sub_824E4E28`). A row the guest knows
/// no record for is answered as its category.
constexpr int32_t kSubmenuType = 301;

/// Upgrade furniture's in-game screen id (TU1 numbering, as `CurrentScreen` answers).
constexpr uint32_t kUpgradeFurnitureScreen = 26u;

/// The row whose MenuEvent the pump is dispatching right now; guest thread only.
struct DispatchedRow {
    bool active = false;
    int32_t event_type = 0;
    int32_t id = 0;
    std::string name;
};

DispatchedRow& Dispatched() {
    static DispatchedRow row;
    return row;
}

bool StatedRowForCurrentScreen(AnsweredRow* row) {
    uint32_t stated_screen = 0;
    if (!refii_bridge_get_stated_highlight(&stated_screen, &row->event_type, &row->id, row->name,
                                           row->label, sizeof(row->name))) {
        return false;
    }
    if (CurrentScreen() != stated_screen) {
        return false;
    }

    // THE ROW BEING COMMITTED IS THE HIGHLIGHTED ROW, for as long as its handler runs. In the
    // guest an event comes from the highlighted row's own activation, so its handler's
    // "selected" statements and highlighted-row reads are about that row. Ours is queued and
    // drained a tick later, and Godot's cursor may have moved on by then: the shop's sold-out
    // ghost and `SOLD` rename (`sub_826B0B90`) landed on the row the player had moved to.
    const DispatchedRow& dispatched = Dispatched();
    if (dispatched.active &&
        (dispatched.id != row->id || dispatched.name != row->name)) {
        row->event_type = dispatched.event_type;
        row->id = dispatched.id;
        std::snprintf(row->name, sizeof(row->name), "%s", dispatched.name.c_str());
        row->label[0] = '\0';
    }
    return true;
}

}  // namespace

namespace refii::vfs {

bool StatedHighlightName(uint8_t* /*base*/, std::string* name, int32_t* id) {
    AnsweredRow row;
    if (!StatedRowForCurrentScreen(&row)) {
        return false;
    }
    *name = row.name;
    if (id != nullptr) {
        *id = row.id;
    }
    return true;
}

void SetDispatchedRow(const std::string& name, int32_t event_type, int32_t id) {
    DispatchedRow& row = Dispatched();
    row.active = true;
    row.event_type = event_type;
    row.id = id;
    row.name = name;
}

void ClearDispatchedRow() { Dispatched().active = false; }

}  // namespace refii::vfs

/// THE HIGHLIGHTED ROW, ANSWERED NATIVELY. `sub_826C1B50(menuHelper, out)` (TU1, disc
/// `sub_8259DB00`; 48 callers across every list screen) ran `g_Menu:GetHighlightedMenuItemData()`
/// and copied back `out[0] = (int)Float3` (event type, 0x826c1ce4), `out[1] = ((int)Float2 << 16)
/// + (int)Float1` (id, 0x826c1c90), `out[2]` String1 and `out[3]` String2 (`sub_822651A0` at
/// 0x826c1d24 / 0x826c1d6c). The four are written from the row Godot states for the CURRENT
/// screen (`RefiiHostSetHighlightedRow`), else the script's own no-row answer; the Lua is never
/// asked.
REX_EXTERN(rex_lhGuiMenu_GetHighlightedItemData) {
    REX_FUNC_PROLOGUE();

    const uint32_t out = ctx.r4.u32;
    if (!Plausible(out)) {
        return;
    }

    AnsweredRow row;
    const bool stated = StatedRowForCurrentScreen(&row);
    const uint32_t screen = CurrentScreen();
    if (!stated) {
        row = NoRow();
        if (screen == 14u) {
            row.event_type = kSubmenuType;
        }

        // THE SHOP BEFORE GODOT'S FIRST STATEMENT. The guest's list opens with its cursor on the
        // first row, a category header (type 301, id 0: PopulateMenu's first spawn). The front
        // page's edge `sub_82991720` (disc `sub_828E30D0`) shows the page on type 301 and hides it
        // on any other (`li r5,1` / `li r5,2` → `sub_82426D18`, 0x829917b8..0x829917d4), and the
        // shop's edge `sub_826B13B8` runs it only when the ID changes (0x826b13d8) — the no-row
        // id 0 is the header's id too, so a no-row type 0 on the opening tick hid the front page
        // for the whole first visit.
        if (screen == 22u) {
            row.event_type = kSubmenuType;
        }

        // UPGRADE FURNITURE BEFORE GODOT'S FIRST STATEMENT. The screen's Update `sub_826B64D8`
        // (disc `sub_8259B6C8`) reads the highlight on the tick the screen opens (0x826b655c /
        // 0x8259b74c), before its list is spawned, and with an id unlike its cache +348
        // (0x826b656c) refills both pages. The id is taken as a furniture level by both: page 1
        // `sub_8299B6D8` looks the unlock card up (`sub_825CC2F0` 0x8299bad0) and uses it only
        // when found (0x8299baec); page 2 `sub_8299C710` (disc `sub_828F22B8`) looks it up
        // (0x8299caf4, result ignored) and, when the id equals the installed level F+16
        // (0x8299cb10), names the card through its handle (0x8299cb78 / `sub_82764688`). The
        // script's no-row id 0 equals the level of a piece never upgraded, and with no level-1
        // card in the hero's inventory that handle is empty: guest 0x10. -1 is no level, so page 2
        // skips that branch; Godot's statement a frame later differs from the cached -1 and both
        // pages refill from the stated row.
        if (screen == kUpgradeFurnitureScreen) {
            row.id = -1;
        }
    }

    // Items, clothing and gift (15 / 16 / 25) state the guest's own rows (item_rows_stated.cpp): the
    // id IS the population ticket the row was spawned with, which the Update's lookup
    // (`sub_82880BC0` at 0x826D1754 / 0x826D2EDC / gift 0x826C46CC) turns into the cached handle
    // (`+396` / `+244`) the commit acts on. Answered as stated.

    // Change-keyed: the item and clothing screens' Update asks every frame.
    {
        static std::string said;
        std::string key = std::to_string(screen) + "|" + row.name + "|" +
                          std::to_string(row.event_type) + "|" + std::to_string(row.id);
        if (key != said) {
            said = std::move(key);
            REXLOG_INFO("Refii menu: GetHighlightedMenuItemData on screen {} answered natively — "
                        "'{}' type {} id 0x{:08X} ({})",
                        screen, row.name, row.event_type, static_cast<uint32_t>(row.id),
                        stated ? "Godot's row" : "no row stated");
        }
    }

    refii::vfs::StoreGuestU32(base, out + 0u, static_cast<uint32_t>(row.event_type));
    refii::vfs::StoreGuestU32(base, out + 4u, static_cast<uint32_t>(row.id));

    // String1 / String2 assigned into the out slots as the original does (`sub_822651A0`), from a
    // scratch string released after (`sub_82214DD8`).
    const PPCContext saved = ctx;
    const uint32_t frame = (saved.r1.u32 - 0x300u) & ~0xFu;
    const uint32_t text = frame + 0x100u;
    const uint32_t handle = frame + 0x200u;
    const auto assign = [&](uint32_t slot, const char* value) {
        BuildString(ctx, base, frame, text, handle, value);
        CallOnFrame(ctx, base, frame, 0x822651A0u, slot, handle, 0u);
        CallOnFrame(ctx, base, frame, 0x82214DD8u, handle, 0u, 0u);
    };
    assign(out + 8u, row.name);
    assign(out + 12u, row.label);
    ctx = saved;
}

/// IS THE HIGHLIGHTED ROW AT THE ROOT, answered from Godot's fold. `sub_826C1D98()` (TU1, disc
/// `sub_8259DD48`) ran `g_Menu:IsHighlightedItemAtRoot()` on the current presentation and
/// returned `Float1 != 0` as a byte (`clrlwi r3, r11, 24` at 0x826c1e5c / disc 0x8259de0c). The
/// script's rule is `not item or item.ParentItem == MenuRoot`. Answered from the fold depth Godot
/// states for the current screen (`RefiiHostSetListFoldDepth`): 0, or none stated, is at the
/// root. Readers: the back arm `sub_826D5AF0` (0x826d5b18 — at root it hands the screen back to
/// the pause root, else `OnPressB` closes the fold), the A arm `sub_826D58F0` (0x826d598c, the
/// select cue below the root), abilities 0x826c2adc, items 0x826d27ec, clothing 0x826d52fc /
/// 0x826d53a8, logbook 0x826d9870, world map 0x826c6518, `sub_826C4C50` 0x826c4c78.
REX_EXTERN(rex_lhGuiMenu_IsHighlightedItemAtRoot) {
    REX_FUNC_PROLOGUE();

    const uint32_t current = CurrentScreen();
    const uint32_t depth = refii_bridge_list_fold_depth(current);
    REXLOG_INFO("Refii menu: IsHighlightedItemAtRoot on screen {} answered natively — fold depth "
                "{}, at root {}",
                current, depth, depth == 0u ? 1 : 0);
    ctx.r3.u64 = depth == 0u ? 1u : 0u;
}

/// THE FRONT END'S ROOT, the same question. `sub_826DED60(screen)` (TU1, disc `sub_826C71C8`)
/// runs the chunk on `*(screen + 20)` and returns `Float1 != 0` (0x826dee1c / disc 0x826c7284).
/// Its one reader, `lhGui_FrontendRoot_OnScreenAction` case 37 state 0 (0x826dfb88 / disc
/// 0x826c7fdc): at root it plays root slide 3 and goes to state 7 (back to the title), else
/// `OnPressB` into the `UIContract`. Answered from the fold Godot states for its front-end main
/// menu list (`kFrontEndMainMenuList`).
REX_EXTERN(rex_lhGuiFrontEnd_IsHighlightedItemAtRoot) {
    REX_FUNC_PROLOGUE();

    const uint32_t depth = refii_bridge_list_fold_depth(refii::vfs::kFrontEndMainMenuList);
    REXLOG_INFO("Refii menu: front-end IsHighlightedItemAtRoot answered natively — fold depth {}, "
                "at root {}",
                depth, depth == 0u ? 1 : 0);
    refii::vfs::FrontEndRootTestAnswered(depth == 0u);
    ctx.r3.u64 = depth == 0u ? 1u : 0u;
}

// THE PRESENTATION EVENTS, POSTED BY THE HOST TO THE CALLBACKS THE GUEST REGISTERED.
//
// The guest's front-end state machine and the HUD's transition advance on presentation events.
// Each is a behaviour element whose script's `onDeactivate` runs `fireEvent("<EVENT>",
// getElement("parent", self.element))` (`behaviorendlegal.lua` and its siblings); the Lua
// `fireEvent` (`sub_82BBCC60`, disc `sub_82BB8790`) queued `{element, sub_82189140(name) &
// 0x7FFFFFFF}` (`clrlwi r4, r3, 1` at 0x82BBCDF4) and the Update's drain (`sub_82174F70`, its one
// caller 0x822AC9C0) ran each callback registered for (element, id) as `fn(ctx, &entry)`
// (`sub_82BB2920` 0x82BB2A54..0x82BB2A64). Neither the Lua nor the Update runs, so the guest never
// delivers one; the host does, timed by Godot's playback of the same file (Engine_UiBehaviourEvents
// names which scripts it posts for).
//
// THE REGISTRATIONS ARE THE GUEST'S. CPresentation vtable +108 (TU1 `sub_82BAFFB8`, disc
// `sub_82BA9870`: r4 element, r5 id, r6 fn, r7 ctx into `sub_82BB2320(this + 380, ...)`) and +112
// (TU1 `sub_82BB0018`, disc `sub_82BA98D0`, the same four into `sub_82BB2600`) are the host's: each
// is stated to the core (refii::headless::NoteRegistration) and kept nowhere in the guest, the
// stated presentation having no list at +380; +108's write of flag 0x80 on the element
// (`vf+80(element, 0x80, 1)`, 0x82bb0000) lands in the handle's host flags. The registrars: the title's vtable
// +20 `sub_826E2C30` (disc `sub_826C5ED0`) and the main menu's `sub_826DE740` (disc
// `sub_826C6C60`) with ctx their screen, the HUD's `sub_823CA428` (disc `sub_823FACE0`) with ctx the
// HUD. A post finds the registrations on the named presentation's root, or on its element of the
// parent's name, for the event's id, and runs each (fn, ctx) once.

namespace {

/// The HUD's FINISHED_TRANSITIONING_TO_GAME callback (TU1 `sub_823CA5F8`, disc `sub_823FAEB0`)
/// reads no argument and dereferences `*(GUI + 0x44)` (0x823CA608) and `*(GUI + 0x48)` (0x823CA644,
/// `sub_82677940` reads `+12`) unchecked: it is run only while the in-game GUI (not the front
/// end's, vtable 0x820A924C) holds both — a guard for the call, not a value.
constexpr refii::guest::Addr kHudTransitionCallback{0x823CA5F8u};
constexpr refii::guest::Addr kGuiObject{0x83496B08u};
constexpr refii::guest::Addr kFrontEndGuiVtable{0x820A924Cu};

bool HudHoldsItsScreens(uint8_t* base) {
    const uint32_t gui = refii::vfs::LoadGuestU32(base, kGuiObject);
    return Plausible(gui) && refii::vfs::LoadGuestU32(base, gui) != static_cast<uint32_t>(kFrontEndGuiVtable) &&
           Plausible(refii::vfs::LoadGuestU32(base, gui + 0x44u)) &&
           Plausible(refii::vfs::LoadGuestU32(base, gui + 0x48u));
}

void StateRegistration(const PPCContext& ctx, bool add) {
    static std::atomic<uint64_t> stated{0};
    const refii::headless::Registration r{ctx.r4.u32, ctx.r5.u32, ctx.r6.u32, ctx.r7.u32};
    refii::headless::NoteRegistration(ctx.r3.u32, r, add);
    const uint64_t n = stated.fetch_add(1, std::memory_order_relaxed) + 1u;
    if (n <= 64u || (n & (n - 1u)) == 0u) {
        REXLOG_INFO("Refii presentation event: {} on presentation 0x{:08X} — element 0x{:08X}, id 0x{:08X}, "
                    "fn 0x{:08X}, ctx 0x{:08X} (#{})",
                    add ? "registered" : "unregistered", ctx.r3.u32, r.element, r.id, r.fn, r.ctx, n);
    }
}

}  // namespace

namespace refii::vfs {
void StatedRegistrationFlag(uint32_t element);  // gui_element_hooks.cpp
}  // namespace refii::vfs

REX_EXTERN(rex_CPresentation_RegisterEventCallback) {
    REX_FUNC_PROLOGUE();
    StateRegistration(ctx, true);
    refii::vfs::StatedRegistrationFlag(ctx.r4.u32);
    ctx.r3.u32 = 0u;
}

REX_EXTERN(rex_CPresentation_UnregisterEventCallback) {
    REX_FUNC_PROLOGUE();
    StateRegistration(ctx, false);
    ctx.r3.u32 = 0u;
}

namespace refii::vfs {

bool PostPresentationEvent(PPCContext& ctx, uint8_t* base, const std::string& spec) {
    const std::size_t first = spec.find(':');
    const std::size_t second = first == std::string::npos ? std::string::npos : spec.find(':', first + 1u);
    if (second == std::string::npos) {
        REXLOG_INFO("Refii presentation event: '{}' is not <presentation>:<parent>:<EVENT> — dropped",
                    spec);
        return true;
    }
    const std::string presentation = spec.substr(0, first);
    const std::string parent = spec.substr(first + 1u, second - first - 1u);
    const std::string event = spec.substr(second + 1u);

    const uint32_t id = refii::headless::NameHash(event.c_str()) & 0x7FFFFFFFu;
    const uint32_t parent_hash = parent.empty() ? 0u : refii::headless::NameHash(parent.c_str());
    uint32_t live = 0u;
    const std::vector<refii::headless::Registration> callbacks =
        refii::headless::Registrations(presentation, parent_hash, id, &live);
    if (callbacks.empty()) {
        REXLOG_INFO("Refii presentation event: {} (0x{:08X}) on '{}' of {} — {} — dropped", event, id, parent,
                    presentation, live == 0u ? "the presentation is not up" : "nothing registered for it");
        return true;
    }

    // The entry `{element, id}` on a frame claimed below the guest's sp; the callee frames below it.
    const PPCContext saved = ctx;
    const uint32_t frame = (saved.r1.u32 - 0x40u) & ~0xFu;
    const uint32_t entry = frame + 0x20u;
    for (const refii::headless::Registration& r : callbacks) {
        if (r.fn == static_cast<uint32_t>(kHudTransitionCallback) && !HudHoldsItsScreens(base)) {
            REXLOG_INFO("Refii presentation event: {} on '{}' of {} with the HUD's screens not held — dropped",
                        event, parent, presentation);
            continue;
        }
        PPCFunc* fn = FindGuestFunction(r.fn);
        if (fn == nullptr) {
            REXLOG_WARN("Refii presentation event: {} registered 0x{:08X}, not a recompiled entry — dropped",
                        event, r.fn);
            continue;
        }
        StoreGuestU32(base, entry + 0u, r.element);
        StoreGuestU32(base, entry + 4u, id);
        ctx.r1.u32 = frame;
        ctx.r3.u32 = r.ctx;
        ctx.r4.u32 = entry;
        ctx.ctr.u32 = r.fn;
        FrontEndEventBegin();
        fn(ctx, base);
        FrontEndEventEnd(presentation.c_str(), parent.c_str(), event.c_str());
        ctx = saved;
        REXLOG_INFO("Refii presentation event: {} (0x{:08X}) on '{}' of {} posted to 0x{:08X} (ctx 0x{:08X})",
                    event, id, parent, presentation, r.fn, r.ctx);
    }
    return true;
}

}  // namespace refii::vfs

// THE PARAMETERS, A HOST STORE. The presentation's "Parameters" element (Lua's `g_Param.element`)
// is the mailbox the C++ and the UI scripts trade values through; each presentation's store holds
// what the file's Parameters element authors (core/headless_presentation.h), and the six functions
// that reach the element are answered from it — none reads guest memory or builds an element.
// The writes are existence-checked as the shipped store is: the service's lookup (TU1
// `sub_82BB52E0` → `sub_822848E0`) finds the attribute or the setter writes nothing
// (`sub_82284798` 0x822847dc `if (result && ...)`). Ids are the name's hash in 27 bits
// (`sub_82189140(name) & 0x7FFFFFF`, 0x826B9250 / disc 0x8259D408).
//   SetStringParam  TU1 0x826B9510 / disc 0x8259D6C8  (r3 dead, r4 name, r5 value, r6 presentation or 0)
//   SetFloatParam   TU1 0x826B9440 / disc 0x8259D5F8  (f1 value, r3 dead, r4 name, r6 presentation or 0)
//   GetFloatParam   TU1 0x826B9210 / disc 0x8259D3C8  (r3 name, r4 presentation) → f1, 0 on a miss
//   GetStringParam  TU1 0x826B92D0 / disc 0x8259D488  (r3 out, r5 name) off the CURRENT presentation
//                   (`mr r5, r3` after GetCurrent, 0x826B92FC), "Default Error String" with none
//   ReadStringParam TU1 0x826B9358 / disc 0x8259D510  (r3 out, r4 name, r5 presentation): the
//                   attribute's text, "Default Error String" on a miss
// `sub_826B9188` (disc `sub_8259D340`) forwards to GetFloatParam. A setter's presentation 0 is
// the current one (`lhGuiPresentationSet_GetCurrent(*(GUI + 56))`, 0x826B9530); none, no write.
// Every set is also staged for the menu-chunk record Godot reads (gui/gui_menu_chunks.h).

namespace {

constexpr refii::guest::Addr kPresentationSetGetCurrent{0x823DCBE0u};  // (set) -> presentation
constexpr char kDefaultErrorString[] = "Default Error String";         // 0x826B932C / 0x826B93E8
constexpr uint32_t kParamIdMask = 0x07FFFFFFu;

uint32_t ParamId(const std::string& name) { return refii::headless::NameHash(name.c_str()) & kParamIdMask; }

/// The presentation the GUI is driving; 0 when it has none.
uint32_t CurrentPresentation(PPCContext& ctx, uint8_t* base) {
    const uint32_t gui = refii::vfs::LoadGuestU32(base, kGuiObject);
    if (!Plausible(gui)) {
        return 0u;
    }
    PPCFunc* fn = refii::vfs::FindGuestFunction(static_cast<uint32_t>(kPresentationSetGetCurrent));
    if (fn == nullptr) {
        return 0u;
    }
    PPCContext call = ctx;
    call.r3.u32 = refii::vfs::LoadGuestU32(base, gui + 56u);
    call.ctr.u32 = static_cast<uint32_t>(kPresentationSetGetCurrent);
    fn(call, base);
    return call.r3.u32;
}

/// A miss is counted and said at milestones: the parameter the C++ or a script answer named is not
/// one the presentation's file carries, so nothing landed — as in the shipped store.
void NoteMiss(const char* what, uint32_t presentation, const std::string& name) {
    static std::atomic<uint64_t> misses{0};
    const uint64_t n = misses.fetch_add(1, std::memory_order_relaxed) + 1u;
    if (n <= 16u || (n & (n - 1u)) == 0u) {
        REXLOG_INFO("Refii parameters: {} '{}' on 0x{:08X} — the presentation's Parameters does not carry "
                    "it (#{})",
                    what, name, presentation, n);
    }
}

/// `text` constructed into the CCharString at `out` with the guest's own ctor, as the readers do.
void AnswerString(PPCContext& ctx, uint8_t* base, uint32_t out, const std::string& text) {
    const PPCContext saved = ctx;
    const uint32_t frame = (saved.r1.u32 - 0x300u) & ~0xFu;
    BuildString(ctx, base, frame, frame + 0x100u, out, text.c_str());
    ctx = saved;
    ctx.r3.u32 = out;
}

/// ReadStringParam's answer for `name` on `presentation`.
std::string StringParam(uint32_t presentation, const std::string& name) {
    refii::headless::Param p;
    if (refii::headless::ParamGet(presentation, ParamId(name), &p) && p.text) {
        return p.string;
    }
    NoteMiss("string read", presentation, name);
    return kDefaultErrorString;
}

}  // namespace

namespace refii::vfs {

void AnswerParamNumber(uint32_t presentation, const char* name, float value) {
    uint32_t bits = 0;
    std::memcpy(&bits, &value, sizeof bits);
    if (!refii::headless::ParamSetNumber(presentation, ParamId(name), bits)) {
        NoteMiss("script answer", presentation, name);
    }
}

void AnswerParamText(uint32_t presentation, const char* name, const std::string& value) {
    if (!refii::headless::ParamSetText(presentation, ParamId(name), value)) {
        NoteMiss("script answer", presentation, name);
    }
}

}  // namespace refii::vfs

REX_EXTERN(rex_lhGuiPresentation_SetStringParam) {
    REX_FUNC_PROLOGUE();
    const std::string name = refii::vfs::ReadGuestLhString(base, ctx.r4.u32);
    const std::string value = refii::vfs::ReadStatedLhBytes(base, ctx.r5.u32);
    refii::vfs::StageMenuAttribute(name, refii::vfs::StatedBytesToUtf8(value), 0.0f, /*is_number=*/false);
    const uint32_t presentation = ctx.r6.u32 != 0u ? ctx.r6.u32 : CurrentPresentation(ctx, base);
    if (presentation != 0u && !refii::headless::ParamSetText(presentation, ParamId(name), value)) {
        NoteMiss("string write", presentation, name);
    }
}

/// `f1` holds the value as a double; the store keeps it narrowed (`stfs` 0x826B94E4).
REX_EXTERN(rex_lhGuiPresentation_SetFloatParam) {
    REX_FUNC_PROLOGUE();
    const std::string name = refii::vfs::ReadGuestLhString(base, ctx.r4.u32);
    const float value = static_cast<float>(ctx.f1.f64);
    refii::vfs::StageMenuAttribute(name, std::string(), value, /*is_number=*/true);
    const uint32_t presentation = ctx.r6.u32 != 0u ? ctx.r6.u32 : CurrentPresentation(ctx, base);
    if (presentation == 0u) {
        return;
    }
    uint32_t bits = 0;
    std::memcpy(&bits, &value, sizeof bits);
    if (!refii::headless::ParamSetNumber(presentation, ParamId(name), bits)) {
        NoteMiss("number write", presentation, name);
    }
}

/// Readers, both builds (TU1 / disc): the event pumps' `EventQueueSize` — in game `sub_82370450`
/// 0x823704D0 / `sub_82338648` 0x823386C8, the front end `lhFrontEnd_Update` 0x82379410 /
/// 0x82379EB0, each pulling an event when it is above 0 (0x823704EC, 0x82379428); `GUI:PullEvent`
/// `sub_826C1810` / `sub_8259D7C0` behind them; `CountSpawnedItems` 0x82998F24 / 0x828ED834 and
/// `GetLastItemIndex` 0x826CE75C / 0x8240824C (`Float1`, the runner's answers); the front end's
/// dead `GetHighlightedMenuItemData` through `sub_826B9188`. `EventQueueSize` is written only by
/// `guieventqueue.lua` (`PushButtonEvent` / `PullEvent`), which is cut: the queue is empty, 0 —
/// commits reach the handlers from the host (`DispatchMenuEventDirect`, guest_tick_pump.cpp).
REX_EXTERN(rex_lhGuiPresentation_GetFloatParam) {
    REX_FUNC_PROLOGUE();
    const std::string name = refii::vfs::ReadGuestLhString(base, ctx.r3.u32);
    const uint32_t presentation = ctx.r4.u32;
    float value = 0.0f;
    refii::headless::Param p;
    if (name == "EventQueueSize") {
        value = 0.0f;
    } else if (refii::headless::ParamGet(presentation, ParamId(name), &p) && !p.text) {
        std::memcpy(&value, &p.bits, sizeof value);
    } else {
        NoteMiss("number read", presentation, name);
    }
    ctx.f1.f64 = static_cast<double>(value);
}

/// Readers, both builds: `sub_823DF660` (disc `sub_8283F948`) reads `String2` at 0x823df7e4 (disc
/// 0x8283facc) after its `g_Menu:GetMenuItemDataByName()` and states it back as
/// `RefreshButtonText`'s `String2`; `lhGuiMenu_GetHighlightedItemData` is replaced whole.
REX_EXTERN(rex_lhGuiPresentation_GetStringParam) {
    REX_FUNC_PROLOGUE();
    const uint32_t out = ctx.r3.u32;
    const std::string name = refii::vfs::ReadGuestLhString(base, ctx.r5.u32);
    const uint32_t presentation = CurrentPresentation(ctx, base);
    AnswerString(ctx, base, out, presentation != 0u ? StringParam(presentation, name) : kDefaultErrorString);
}

/// Its other caller is `GUI:PullEvent` (`sub_826C1810` 0x826c187c), behind an empty queue.
REX_EXTERN(rex_lhGuiPresentation_ReadStringParam) {
    REX_FUNC_PROLOGUE();
    const uint32_t out = ctx.r3.u32;
    const std::string name = refii::vfs::ReadGuestLhString(base, ctx.r4.u32);
    AnswerString(ctx, base, out, StringParam(ctx.r5.u32, name));
}

/// THE PRESENTATION'S LUA ENTRY, `sub_82372500(LHString*, presentation)` (disc `sub_82306718`).
/// Two callers per build: the runner (0x824264C8 / 0x823F8960), whose hook no longer calls the
/// original, and the deferred drain `sub_821738A0` (0x821739A0 / disc `sub_82173718` 0x82173818),
/// which walks the GUI+0x58 queue only the runner fills. Swallowed, so no guest UI chunk reaches
/// a presentation's Lua by either path. The host's own front-end mod VM calls the original
/// (`__imp__`, mod_frontend_lua.cpp) and is not routed here.
REX_EXTERN(rex_lhGuiPresentation_RunLuaString) {
    REX_FUNC_PROLOGUE();
    static std::atomic<uint32_t> swallowed{0};
    const uint32_t n = swallowed.fetch_add(1, std::memory_order_relaxed) + 1u;
    if (n <= 8u || (n & (n - 1u)) == 0u) {
        REXLOG_INFO("Refii menu: a queued UI chunk reached the presentation's Lua entry "
                    "(#{}, presentation 0x{:08X}) — cut, not run",
                    n, ctx.r4.u32);
    }
}
