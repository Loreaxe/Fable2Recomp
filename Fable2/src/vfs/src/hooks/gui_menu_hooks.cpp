// C++ stages attributes onto the "Parameters" presentation element (Lua's `g_Param.element`),
// then runs a one-line `g_Menu:` chunk that reads them back (28 `g_Menu:` + 3 `g_MenuFormatter:`
// literals, all via `sub_82426470`). Presentation-element state (`sub_82426798`, `sub_82426260`,
// `sub_82425DD0`, ...) does not pass through here.

#include <cstdint>
#include <cstdio>
#include <string>
#include <utility>

#include "vfs_hooks_private.h"
#include "core/guest_state.h"
#include "gui/gui_menu_chunks.h"
#include "hooks/frontend_stated.h"
#include "hooks/ppc_externs.h"
#include "core/guest_build.h"
#include "core/payload_bridge_fable2.h"

// The attribute stagers. `r3` is dead on both: each resolves the "Parameters" element from
// `dword_8349AF78` and overwrites r3 before use (`lwz r3, 0x38(r11)` at 0x826B9534 / 0x826B9468).
PPC_EXTERN_FUNC(__imp__rex_lhGuiPresentation_SetStringParam);
PPC_EXTERN_FUNC(__imp__rex_lhGuiPresentation_SetFloatParam);
PPC_EXTERN_FUNC(__imp__rex_lhGuiPresentation_GetFloatParam);
PPC_EXTERN_FUNC(__imp__rex_lhGuiPresentation_GetStringParam);

/// String stager — `r4` attribute name, `r5` value, `r6` presentation (0 = current).
REX_EXTERN(rex_lhGuiPresentation_SetStringParam) {
    REX_FUNC_PROLOGUE();

    refii::vfs::StageMenuAttribute(
        refii::vfs::ReadGuestLhString(base, ctx.r4.u32),
        refii::vfs::ReadGuestLhString(base, ctx.r5.u32),
        0.0f,
        /*is_number=*/false);

    __imp__rex_lhGuiPresentation_SetStringParam(ctx, base);
}

/// Float stager — `r4` attribute name, `f1` value (a double in the register, stored narrowed),
/// `r6` presentation.
REX_EXTERN(rex_lhGuiPresentation_SetFloatParam) {
    REX_FUNC_PROLOGUE();

    refii::vfs::StageMenuAttribute(
        refii::vfs::ReadGuestLhString(base, ctx.r4.u32),
        std::string(),
        static_cast<float>(ctx.f1.f64),
        /*is_number=*/true);

    __imp__rex_lhGuiPresentation_SetFloatParam(ctx, base);
}

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

/// The list screens whose rows are keyed in the shared row-id map by a population TICKET: items
/// (15), clothing (16), gift (25). Godot states their rows by the item's record hash.
bool KeysRowsByTicket(uint32_t screen) { return screen == 15u || screen == 16u || screen == 25u; }

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

namespace {

/// The one `Float1` read the runner answered ahead of: armed by `g_Menu:CountSpawnedItems()` on
/// the guest thread, taken by that function's own `GetFloatParam` four calls later.
struct PendingFloat1 {
    bool armed = false;
    double value = 0.0;
};

PendingFloat1& Float1Answer() {
    static PendingFloat1 pending;
    return pending;
}

/// The one `String2` read the runner answered ahead of: armed by `g_Menu:GetMenuItemDataByName()`
/// on the guest thread, taken by its reader's string getter call two calls later.
struct PendingString2 {
    bool armed = false;
    std::string value;
};

PendingString2& String2Answer() {
    static PendingString2 pending;
    return pending;
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

void AnswerNextFloat1(double value) {
    PendingFloat1& pending = Float1Answer();
    pending.armed = true;
    pending.value = value;
}

void DisarmFloat1() {
    Float1Answer().armed = false;
    String2Answer().armed = false;
}

void AnswerNextString2(std::string value) {
    PendingString2& pending = String2Answer();
    pending.armed = true;
    pending.value = std::move(value);
}

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

    // Items, clothing and gift: the stated record answered as the guest's own ticket for it, so
    // the Update's lookup (`sub_82880BC0` at 0x826D1754 / 0x826D2EDC / gift 0x826C46CC) fills
    // the cached handle (`+396` / `+244`) the commit acts on. A stated id that is no item's
    // record (a dye slot, a None row) is answered as stated.
    uint32_t ticket = 0u;
    if (stated && KeysRowsByTicket(screen) && row.id != 0) {
        ticket = refii::vfs::MenuTicketForRecord(static_cast<uint32_t>(row.id));
        if (ticket != 0u) {
            row.id = static_cast<int32_t>(ticket);
        }
    }

    // Change-keyed: the item and clothing screens' Update asks every frame.
    {
        static std::string said;
        std::string key = std::to_string(screen) + "|" + row.name + "|" +
                          std::to_string(row.event_type) + "|" + std::to_string(row.id);
        if (key != said) {
            said = std::move(key);
            REXLOG_INFO("Refii menu: GetHighlightedMenuItemData on screen {} answered natively — "
                        "'{}' type {} id 0x{:08X} ({}{})",
                        screen, row.name, row.event_type, static_cast<uint32_t>(row.id),
                        stated ? "Godot's row" : "no row stated",
                        ticket != 0u ? ", the record's ticket" : "");
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

// THE FRONT END'S PRESENTATION EVENTS, POSTED BY THE HOST.
//
// The guest's front-end state machine advances on presentation events. Each is a behaviour
// element whose script's `onDeactivate` runs `fireEvent("<EVENT>", getElement("parent",
// self.element))` (`behaviorendlegal.lua` and its siblings) at the end of its time window; the
// Lua `fireEvent` (`sub_82BBCC60`, disc `sub_82BB8790`) hashes the name with `sub_82189140`,
// masks it (`clrlwi r4, r3, 1` at 0x82BBCDF4) and calls the presentation's vtable +24
// (0x82BBCDF8, `sub_82BB0648`), which queues `{element, id}` on the presentation's event queue
// (+324). `sub_82174F70` (disc `sub_82174D38`) drains it: the callback list registered for
// (element, id) through vtable +108 (`sub_82BAFFB8` → `sub_82BB2320`, entries `{fn, ctx}`) is
// run by `sub_82BB2920` (disc `sub_82BAC4E8`) as `fn(ctx, &entry)` — `lwz r3, 4(r30)`,
// `mr r4, r27`, `bctrl` at 0x82BB2A54..0x82BB2A64.
//
// The host times the events from Godot's playback of the same file (Engine_UiBehaviourEvents) and
// posts each here, to the callback and ctx the guest's registration names: the screen's vtable +20
// (title `sub_826E2C30`, root `sub_826DE740`; disc `sub_826C5ED0`, `sub_826C6C60`) registers
// `this` as ctx, the HUD's `sub_823CA428` (disc `sub_823FACE0`) registers the HUD object. Each
// callback reads only `+4` of the entry, the id. The callbacks are hooked: a call that reaches
// one from the guest's own queue is the presentation's timeline firing the same event, and is
// swallowed for every event the host posts — one source per event, with presentations built or not.

namespace {

enum class EventOwner { kTitle, kRoot, kHud };

struct PresentationEventRoute {
    const char* presentation;  // the file the behaviour ships in
    const char* parent;        // the behaviour's parent element, "" for the root
    const char* event;
    EventOwner owner;
    uint32_t id_tu1;           // the id interned at static init, `sub_82189140(name) & 0x7FFFFFFF`
    PPCFunc* callback;         // the original, not the hook
};

/// Registrations, from the registrars' decompiles (TU1; disc ids by the same initialisers).
/// `frontendstartupscreen.bgf`: BehaviorEndLegal, BehaviorEndStarted, BehaviorCanPressA under the
/// root, BehaviorDialogActive and BehaviorUnFreeze under `dialog`. `frontendmainmenu.bgf`:
/// BehaviorOutroFinished under the root, BehaviorBoy/Girl and BehaviorUnFreeze under
/// `choosecard`, BehaviorLoadCard under `loadgame`, BehaviorDialogActive and BehaviorUnFreeze
/// under `dialog`. `hud.bgf`: FinishedTransitioningToGame under the root. The registrations with
/// no shipped emitter (`loadgame`'s LOAD_CARD_SELECTED and UNFREEZE) and the gender card's
/// BOY/GIRL_SELECTED (the card choice's own statement, `StartNewGame`) are not routed.
const PresentationEventRoute kPresentationEvents[] = {
    {"frontendstartupscreen", "", "END_LEGAL", EventOwner::kTitle, 0x8349B644u,
     __imp__rex_lhGuiFrontEndTitle_OnEndLegal},
    {"frontendstartupscreen", "", "END_STARTED", EventOwner::kTitle, 0x8349B648u,
     __imp__rex_lhGuiFrontEndTitle_OnEndStarted},
    {"frontendstartupscreen", "", "CAN_PRESS_A", EventOwner::kTitle, 0x8349B64Cu,
     __imp__rex_lhGuiFrontEndTitle_OnCanPressA},
    {"frontendstartupscreen", "dialog", "DIALOG_ACTIVE", EventOwner::kTitle, 0x8349B65Cu,
     __imp__rex_lhGuiFrontEndTitle_OnDialogEvent},
    {"frontendstartupscreen", "dialog", "UNFREEZE", EventOwner::kTitle, 0x8349B660u,
     __imp__rex_lhGuiFrontEndTitle_OnDialogEvent},
    {"frontendmainmenu", "", "OUTRO_FINISHED", EventOwner::kRoot, 0x8349B594u,
     __imp__rex_lhGuiFrontEndRoot_OnOutroFinished},
    {"frontendmainmenu", "choosecard", "BOY_ACTIVE", EventOwner::kRoot, 0x8349B598u,
     __imp__rex_lhGui_FrontendRoot_HandleGuiEvent},
    {"frontendmainmenu", "choosecard", "GIRL_ACTIVE", EventOwner::kRoot, 0x8349B59Cu,
     __imp__rex_lhGui_FrontendRoot_HandleGuiEvent},
    {"frontendmainmenu", "choosecard", "UNFREEZE", EventOwner::kRoot, 0x8349B5B0u,
     __imp__rex_lhGui_FrontendRoot_HandleGuiEvent},
    {"frontendmainmenu", "loadgame", "LOAD_CARD_ACTIVE", EventOwner::kRoot, 0x8349B5A8u,
     __imp__rex_lhGuiFrontEndRoot_OnLoadCardEvent},
    {"frontendmainmenu", "dialog", "DIALOG_ACTIVE", EventOwner::kRoot, 0x8349B5B4u,
     __imp__rex_lhGuiFrontEndRoot_OnDialogEvent},
    {"frontendmainmenu", "dialog", "UNFREEZE", EventOwner::kRoot, 0x8349B5B0u,
     __imp__rex_lhGuiFrontEndRoot_OnDialogEvent},
    {"hud", "", "FINISHED_TRANSITIONING_TO_GAME", EventOwner::kHud, 0x83497B80u,
     __imp__rex_lhGuiHud_OnFinishedTransitioningToGame},
};

constexpr refii::guest::Addr kGuiObject{0x83496B08u};
constexpr refii::guest::Addr kFrontEndGuiVtable{0x820A924Cu};

/// The callback's ctx, or 0 when its owner is not up. The front end's two are the `this` each
/// registrar (vtable +20: title `sub_826E2C30`, root `sub_826DE740`) registers, the screens the
/// front end activates — as their activates stated them (frontend_stated.cpp). The HUD callback
/// `sub_823CA5F8` reads no argument — `r3` is first written at 0x823CA644 — and dereferences
/// `*(GUI + 0x44)` (0x823CA608) and `*(GUI + 0x48)` (0x823CA644, `sub_82677940` reads `+12`)
/// unchecked, so it is posted only while the in-game GUI (not the front end's, vtable 0x820A924C)
/// holds both: a guard for the call, not a value.
uint32_t EventOwnerObject(uint8_t* base, EventOwner owner) {
    if (owner == EventOwner::kHud) {
        const uint32_t gui = refii::vfs::LoadGuestU32(base, kGuiObject);
        if (!Plausible(gui) || refii::vfs::LoadGuestU32(base, gui) == kFrontEndGuiVtable ||
            !Plausible(refii::vfs::LoadGuestU32(base, gui + 0x44u)) ||
            !Plausible(refii::vfs::LoadGuestU32(base, gui + 0x48u))) {
            return 0u;
        }
        return gui;
    }
    return owner == EventOwner::kTitle ? refii::vfs::FrontEndTitleScreen() : refii::vfs::FrontEndMenuScreen();
}

/// Whether the host posts `id` to the callback `original` — the guest's own delivery of it is then
/// swallowed. Compared against the interned ids, as the callbacks compare.
bool HostPosts(uint8_t* base, PPCFunc* original, uint32_t id) {
    for (const PresentationEventRoute& route : kPresentationEvents) {
        if (route.callback == original &&
            refii::vfs::LoadGuestU32(base, refii::guest::Address(route.id_tu1)) == id) {
            return true;
        }
    }
    return false;
}

/// One guest delivery reaching a hooked callback: swallowed when the host posts that event, else
/// the original runs. `r4` is the queued entry; its `+4` is the id.
void GuestDelivery(PPCContext& ctx, uint8_t* base, PPCFunc* original, const char* callback) {
    const uint32_t entry = ctx.r4.u32;
    const uint32_t id = Plausible(entry) ? refii::vfs::LoadGuestU32(base, entry + 4u) : 0u;
    if (id != 0u && HostPosts(base, original, id)) {
        REXLOG_INFO("Refii presentation event: the guest's own 0x{:08X} to {} swallowed — the host "
                    "posts it from Godot's playback",
                    id, callback);
        return;
    }
    original(ctx, base);
}

}  // namespace

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

    const PresentationEventRoute* route = nullptr;
    for (const PresentationEventRoute& candidate : kPresentationEvents) {
        if (presentation == candidate.presentation && parent == candidate.parent &&
            event == candidate.event) {
            route = &candidate;
            break;
        }
    }
    if (route == nullptr) {
        REXLOG_INFO("Refii presentation event: {} on '{}' of {} has no registration the host posts "
                    "to — dropped",
                    event, parent, presentation);
        return true;
    }

    const uint32_t id = LoadGuestU32(base, refii::guest::Address(route->id_tu1));
    if (id == 0u) {
        // Interned at static init; held rather than sent as 0, which matches nothing.
        return false;
    }

    const uint32_t owner = EventOwnerObject(base, route->owner);
    if (owner == 0u) {
        REXLOG_INFO("Refii presentation event: {} on '{}' of {} with its screen not up — dropped",
                    event, parent, presentation);
        return true;
    }

    // The entry `{element, id}` on a frame claimed below the guest's sp; the callee frames below it.
    const PPCContext saved = ctx;
    const uint32_t frame = (saved.r1.u32 - 0x40u) & ~0xFu;
    const uint32_t entry = frame + 0x20u;
    StoreGuestU32(base, entry + 0u, 0u);
    StoreGuestU32(base, entry + 4u, id);
    ctx.r1.u32 = frame;
    ctx.r3.u32 = route->owner == EventOwner::kHud ? 0u : owner;
    ctx.r4.u32 = entry;
    FrontEndEventBegin();
    route->callback(ctx, base);
    FrontEndEventEnd(route->presentation, route->parent, route->event);
    ctx = saved;

    REXLOG_INFO("Refii presentation event: {} (0x{:08X}) on '{}' of {} posted to 0x{:08X}", event, id,
                parent, presentation, owner);
    return true;
}

}  // namespace refii::vfs

/// `sub_826E2DA0` (disc `sub_826C6040`) — END_LEGAL on the title's root: state 9 → 0.
REX_EXTERN(rex_lhGuiFrontEndTitle_OnEndLegal) {
    REX_FUNC_PROLOGUE();
    GuestDelivery(ctx, base, __imp__rex_lhGuiFrontEndTitle_OnEndLegal, "the title's END_LEGAL");
}

/// `sub_826E2EB0` (disc `sub_826C6150`) — END_STARTED on the title's root: state 10 → 11.
REX_EXTERN(rex_lhGuiFrontEndTitle_OnEndStarted) {
    REX_FUNC_PROLOGUE();
    GuestDelivery(ctx, base, __imp__rex_lhGuiFrontEndTitle_OnEndStarted, "the title's END_STARTED");
}

/// `sub_826E2E20` (disc `sub_826C60C0`) — CAN_PRESS_A on the title's root: `+80 = 1` (disc +72).
REX_EXTERN(rex_lhGuiFrontEndTitle_OnCanPressA) {
    REX_FUNC_PROLOGUE();
    GuestDelivery(ctx, base, __imp__rex_lhGuiFrontEndTitle_OnCanPressA, "the title's CAN_PRESS_A");
}

/// `sub_826E3740` (disc `sub_826C69B0`) — the title's `dialog`: DIALOG_ACTIVE → state 12,
/// UNFREEZE → state 0.
REX_EXTERN(rex_lhGuiFrontEndTitle_OnDialogEvent) {
    REX_FUNC_PROLOGUE();
    GuestDelivery(ctx, base, __imp__rex_lhGuiFrontEndTitle_OnDialogEvent, "the title's dialog");
}

/// `sub_826E13F0` (disc `sub_826C9810`) — OUTRO_FINISHED on the main menu's root: back to the title.
REX_EXTERN(rex_lhGuiFrontEndRoot_OnOutroFinished) {
    REX_FUNC_PROLOGUE();
    GuestDelivery(ctx, base, __imp__rex_lhGuiFrontEndRoot_OnOutroFinished,
                  "the main menu's OUTRO_FINISHED");
}

/// `sub_826E0D60` (disc `sub_826C9188`) — `choosecard`: BOY_ACTIVE → mode 3, GIRL_ACTIVE → 4,
/// UNFREEZE → 0 (host-posted); BOY/GIRL_SELECTED commit through `sub_82379628` and pass through.
REX_EXTERN(rex_lhGui_FrontendRoot_HandleGuiEvent) {
    REX_FUNC_PROLOGUE();
    GuestDelivery(ctx, base, __imp__rex_lhGui_FrontendRoot_HandleGuiEvent, "the gender card");
}

/// `sub_826E0E58` (disc `sub_826C9280`) — `loadgame`: LOAD_CARD_ACTIVE → mode 5 (host-posted),
/// UNFREEZE (`dword_8349B5B0`) → mode 0 (0x826e0ea0), which nothing shipped fires and the host
/// does not post: a guest delivery of it is stated here.
REX_EXTERN(rex_lhGuiFrontEndRoot_OnLoadCardEvent) {
    REX_FUNC_PROLOGUE();
    const uint32_t entry = ctx.r4.u32;
    const bool unfreeze =
        Plausible(entry) && refii::vfs::LoadGuestU32(base, entry + 4u) ==
                                refii::vfs::LoadGuestU32(base, refii::guest::Address(0x8349B5B0u));
    if (unfreeze) refii::vfs::FrontEndEventBegin();
    GuestDelivery(ctx, base, __imp__rex_lhGuiFrontEndRoot_OnLoadCardEvent, "the load card");
    if (unfreeze) refii::vfs::FrontEndEventEnd("frontendmainmenu", "loadgame", "UNFREEZE");
}

/// `sub_826E1040` (disc `sub_826C9460`) — the main menu's `dialog`: DIALOG_ACTIVE → mode 6,
/// UNFREEZE → the warning's answer.
REX_EXTERN(rex_lhGuiFrontEndRoot_OnDialogEvent) {
    REX_FUNC_PROLOGUE();
    GuestDelivery(ctx, base, __imp__rex_lhGuiFrontEndRoot_OnDialogEvent, "the main menu's dialog");
}

/// `sub_823CA5F8` (disc `sub_823FAEB0`) — FINISHED_TRANSITIONING_TO_GAME on the HUD's root: opens
/// the screen held for it (`sub_82677940(*(GUI + 0x48))`) unless `*(GUI + 0x44)` holds one.
REX_EXTERN(rex_lhGuiHud_OnFinishedTransitioningToGame) {
    REX_FUNC_PROLOGUE();
    GuestDelivery(ctx, base, __imp__rex_lhGuiHud_OnFinishedTransitioningToGame,
                  "the HUD's FINISHED_TRANSITIONING_TO_GAME");
}

/// THE FLOAT READ-BACK, answered for the Lua that no longer runs. `sub_826B9210(LHString** name,
/// presentation)` (disc `sub_8259D3C8`) → f1 reads one float off the presentation's `Parameters`
/// element; it dereferences r4 unchecked (`vtable+56` at 0x826B9234). Its readers, both builds
/// (TU1 / disc): the event pumps' `EventQueueSize` — in game `sub_82370450` 0x823704D0 /
/// `sub_82338648` 0x823386C8, the front end `lhFrontEnd_Update` 0x82379410 / 0x82379EB0, each
/// pulling and dispatching an event when it is above 0 (0x823704EC, 0x82379428); `GUI:PullEvent`
/// `sub_826C1810` / `sub_8259D7C0`, reached only behind those; `CountSpawnedItems` 0x82998F24 /
/// 0x828ED834; `GetLastItemIndex` 0x826CE75C / 0x8240824C; the front end's dead
/// `GetHighlightedMenuItemData` through `sub_826B9188` / `sub_8259D340`; and the three readers
/// replaced whole above and below.
///
/// `EventQueueSize` is written only by `guieventqueue.lua` (`PushButtonEvent` / `PullEvent`),
/// which is cut: the queue is empty, 0. Commits reach the screens' handlers from the host
/// directly (`DispatchMenuEventDirect`, guest_tick_pump.cpp). `Float1` after `CountSpawnedItems`
/// is the runner's answer. Any other name reads the element as before; a null presentation reads
/// 0, the getter's own miss value (0x826B92C0), instead of faulting.
REX_EXTERN(rex_lhGuiPresentation_GetFloatParam) {
    REX_FUNC_PROLOGUE();

    const std::string name = refii::vfs::ReadGuestLhString(base, ctx.r3.u32);
    const uint32_t presentation = ctx.r4.u32;

    if (name == "EventQueueSize") {
        ctx.f1.f64 = 0.0;
        return;
    }

    PendingFloat1& pending = Float1Answer();
    if (pending.armed && name == "Float1") {
        pending.armed = false;
        ctx.f1.f64 = pending.value;
        return;
    }

    if (!Plausible(presentation)) {
        ctx.f1.f64 = 0.0;
        return;
    }
    __imp__rex_lhGuiPresentation_GetFloatParam(ctx, base);
}

/// THE STRING READ-BACK, answered for the Lua that no longer runs. `sub_826B92D0(out, _, name)`
/// (disc `sub_8259D488`, both 0x88 bytes): r3 the out CCharString, r5 the attribute's name
/// (`mr r30, r5` 0x826b92ec, handed on as r4 at 0x826b9320). With a current presentation it reads
/// the attribute off `Parameters` (`sub_826B9358` 0x826b9324), else constructs "Default Error
/// String" (0x826b9338); a headless `Parameters` carries no string attribute, so the read itself
/// answers that too. Readers, both builds: `sub_823DF660` (disc `sub_8283F948`) reads `String2`
/// at 0x823df7e4 (disc 0x8283facc) right after its `g_Menu:GetMenuItemDataByName()` (0x823df7ac),
/// strips `<exclamation.img` from it and states it back as `RefreshButtonText`'s `String2`
/// (0x823df8ec / 0x823df914) — the pause root's ghosting pass `sub_8298F648` is its one caller
/// that asks (`li r6, 1` at 0x8298f968..0x8298fa2c); and `lhGuiMenu_GetHighlightedItemData`,
/// replaced whole above. `String2` after `GetMenuItemDataByName` is the runner's answer, the
/// named row's text tag; every other read runs the original.
REX_EXTERN(rex_lhGuiPresentation_GetStringParam) {
    REX_FUNC_PROLOGUE();

    PendingString2& pending = String2Answer();
    if (!pending.armed || refii::vfs::ReadGuestLhString(base, ctx.r5.u32) != "String2") {
        __imp__rex_lhGuiPresentation_GetStringParam(ctx, base);
        return;
    }
    pending.armed = false;

    const uint32_t out = ctx.r3.u32;
    const PPCContext saved = ctx;
    const uint32_t frame = (saved.r1.u32 - 0x300u) & ~0xFu;
    BuildString(ctx, base, frame, frame + 0x100u, out, pending.value.c_str());
    ctx = saved;
    ctx.r3.u32 = out;
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
