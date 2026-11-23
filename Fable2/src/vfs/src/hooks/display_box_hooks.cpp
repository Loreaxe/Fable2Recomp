// The display MessageBox, stated to the host at its own Open and Close.
//
// Class vtable 0x820AE668 (ctor `sub_823CEA78`): slot 0 dtor `sub_823CEAE8`, slot 1 Open
// `sub_823CEB58`, slot 2 Close `sub_823CEC98`, slot 3 action input `sub_823CEDD8`. Every one
// goes through the box queue at `*(*(GUI+28)+52)+4` — `GUI.DisplayMessageBox` (0x82302824), the
// modal hints (`sub_82450538` 0x82450654), the pause menu's notices, the sign-in box and ~25 more
// natives build one there — and the queue's tick opens the head when nothing else is up.
//
// Most of them never pass a `GUI.*` native, so the call feed cannot see them, and none of them
// can be answered by a raw pad press: the router hands the raw event to the box's slot +16
// (`sub_82403350` 0x82403634), which for this class is `li r3,0; blr`. Only action code 36
// through slot 3 closes one. Open pushes GameFlow state 8 (`sub_823D0A70(box, 0)` 0x823cec70),
// which takes both players' movement until that close — an unseen box is a frozen game.
//
// So Open and Close post `@MessageBox.Open` / `@MessageBox.Close` onto the GUI call feed, with
// the box's address and its identity (`box+4`, the name `RemoveDisplayBox` matches). The host
// draws the box from its live `messageboxmulti` presentation and answers the stated address.

#include <cstdint>
#include <initializer_list>
#include <cstdio>
#include <string>
#include <vector>

#include "vfs_hooks_private.h"
#include "core/guest_build.h"
#include "core/guest_state.h"
#include "core/gui_native_calls.h"
#include "hooks/ppc_externs.h"

namespace refii::vfs {
std::string ReadStatedWideLhString(uint8_t* base, uint32_t handle);
}  // namespace refii::vfs

PPC_EXTERN_FUNC(__imp__rex_lhGuiNumericAdjuster_Render);
PPC_EXTERN_FUNC(__imp__rex_lhGuiGamerCard_SetName);

namespace refii::vfs {
// gui_stated_screens.cpp: a menu box opened, for the question that latches the modal on it.
void StatedMenuBoxOpened();
}  // namespace refii::vfs

namespace {

/// The open byte every handler gates on — `lbz r11, 0xC(r3)`; Open sets it at 0x823cec7c,
/// Close clears it at 0x823cedb8.
constexpr uint32_t kBoxOpen = 12u;

/// The identity handle, 8-bit class — Close copies it with `strcpy_s(**(char***)(box+4))`
/// at 0x823ced18.
constexpr uint32_t kBoxIdentity = 4u;

/// The body text, a wide LHString handle — the display-box controller base (`sub_823D0768`).
constexpr uint32_t kBoxBody = 8u;

void State(uint8_t* base, const char* name, uint32_t box) {
    refii::vfs::GuiNativeCall call;
    call.name = name;

    refii::vfs::GuiNativeArgument address;
    address.kind = refii::vfs::GuiNativeArgument::Kind::kString;
    char hex[9];
    std::snprintf(hex, sizeof hex, "%08X", box);
    address.text = hex;
    call.arguments.push_back(std::move(address));

    refii::vfs::GuiNativeArgument identity;
    identity.kind = refii::vfs::GuiNativeArgument::Kind::kString;
    identity.text = refii::vfs::ReadGuestLhString(base, box + kBoxIdentity);
    call.arguments.push_back(std::move(identity));

    // The box's BODY — the controller base's wide text at +8 (`sub_823D0768`), what Open writes
    // into the presentation — so the host draws the box from its own statement.
    refii::vfs::GuiNativeArgument body;
    body.kind = refii::vfs::GuiNativeArgument::Kind::kString;
    body.text = refii::vfs::ReadStatedWideLhString(base, box + kBoxBody);
    call.arguments.push_back(std::move(body));

    refii::vfs::PostGuiNativeCall(std::move(call));
}

}  // namespace


namespace {

/// The modal router's opener states its box: kind (2 message, 3 query, 5 adjuster), the modal id
/// the answer is keyed on, and the TEXT, read from the opener's own argument — the string the
/// guest then writes into `Group.QueryBox.Text` / the message box's text — so the host draws the
/// box from the call, not from the presentation it fills. An adjuster adds its numbers and its
/// widget's address, hex — the key its renders are stated under (`@Adjuster.Render`).
void StateModal(uint32_t kind, uint32_t id, const std::string& text,
                std::initializer_list<int32_t> numbers = {}, uint32_t widget = 0u) {
    refii::vfs::GuiNativeCall call;
    call.name = "@Modal.Open";
    const auto add = [&call](std::string value) {
        refii::vfs::GuiNativeArgument argument;
        argument.kind = refii::vfs::GuiNativeArgument::Kind::kString;
        argument.text = std::move(value);
        call.arguments.push_back(std::move(argument));
    };
    add(std::to_string(kind));
    add(std::to_string(id));
    add(text);
    for (const int32_t number : numbers) {
        add(std::to_string(number));
    }
    if (widget != 0u) {
        char hex[9];
        std::snprintf(hex, sizeof hex, "%08X", widget);
        add(hex);
    }
    refii::vfs::PostGuiNativeCall(std::move(call));
}

}  // namespace

// The modal router's opener `sub_8236FA68(gui28, text, kind, modalId, flag, extra)` (disc
// `sub_82337C68`): it acts only while `gui28+40 == 1` (0x8236fa90), sets the mode and id, pushes
// screen 19 (query) or 3 (message) and writes the text; it releases the text on the way out, so
// the text is read before the call. Stated only when the box opened.
REX_EXTERN(rex_lhGuiManager_OpenModal) {
    const uint32_t owner = ctx.r3.u32;
    const uint32_t text = ctx.r4.u32;
    const uint32_t kind = ctx.r5.u32;
    const uint32_t id = ctx.r6.u32;
    const bool opens = refii::vfs::LoadGuestU32(base, owner + 40u) == 1u;
    const std::string said = opens ? refii::vfs::ReadStatedWideLhString(base, text) : std::string();
    // The opener saves the screen that is up, sets the box's screen and puts the saved one back
    // (0x8236fc90, disc 0x82337e8c): the screen stated before the call stands after it.
    const uint32_t screen = refii_bridge_stated_screen_now();
    __imp__rex_lhGuiManager_OpenModal(ctx, base);
    refii_bridge_stated_screen(screen);
    if (opens && refii::vfs::LoadGuestU32(base, owner + 40u) == kind) {
        refii_bridge_stated_modal(kind, id);
        StateModal(kind, id, said);
    }
}

// The numeric adjuster's opener `sub_8236FCA8(gui28, text, modalId, value, min, max, step, flags)`
// (disc `sub_82337EA0`; 0x8236fcb4..0x8236fde4): mode 5 and the id at +44, the text into
// `Group.QueryBox.Text`, the numbers into the widget at `*(gui28+0x40)` (+0x2C value, +0x20 min,
// +0x24 max, +0x28 step, +0x1C flags: 1 money, 2 percent, 4 sign). The widget is `gui28[16]`
// (`lwz` at 0x8236fd68 / disc 0x82337f60), stated with the numbers: its renders key on it.
REX_EXTERN(rex_lhGuiManager_OpenNumericAdjuster) {
    const uint32_t owner = ctx.r3.u32;
    const uint32_t text = ctx.r4.u32;
    const uint32_t id = ctx.r5.u32;
    const int32_t value = static_cast<int32_t>(ctx.r6.u32);
    const int32_t minimum = static_cast<int32_t>(ctx.r7.u32);
    const int32_t maximum = static_cast<int32_t>(ctx.r8.u32);
    const int32_t step = static_cast<int32_t>(ctx.r9.u32);
    const int32_t flags = static_cast<int32_t>(ctx.r10.u32);
    const bool opens = refii::vfs::LoadGuestU32(base, owner + 40u) == 1u;
    const std::string said = opens ? refii::vfs::ReadStatedWideLhString(base, text) : std::string();
    // As the modal opener: the saved screen is put back (0x8236fd64, disc 0x82337f5c).
    const uint32_t screen = refii_bridge_stated_screen_now();
    __imp__rex_lhGuiManager_OpenNumericAdjuster(ctx, base);
    refii_bridge_stated_screen(screen);
    if (opens && refii::vfs::LoadGuestU32(base, owner + 40u) == 5u) {
        refii_bridge_stated_modal(5u, id);
        StateModal(5u, id, said, {value, minimum, maximum, step, flags},
                   refii::vfs::LoadGuestU32(base, owner + 0x40u));
    }
}

// The adjuster widget's render `sub_826B8BB0(widget)` (disc `sub_8240CA98`), r3 the widget: the one
// writer of its value text, called by the router's opener (0x8236fde8, 0x8236fe00, 0x8236fe08 / disc
// 0x82337fe0, 0x82337ff8, 0x82338000), the display box's Open (0x823cf750 / 0x823bdd90) and both
// steps (0x826b8db4, 0x826b8e94 / 0x8240cc9c, 0x8240cd7c). It reads flags +0x1C (0x826b8bd4 /
// 0x8240cabc: 4 a `+` on a positive value, 1 money, else 2 a `%`), value +0x2C (0x826b8c04 /
// 0x8240caec), min +0x20 (0x826b8c80 / 0x8240cb68), step +0x28 (0x826b8c88 / 0x8240cb70) and max
// +0x24 (0x826b8ca8 / 0x8240cb90); the arrow at +0x10 stands while value - min >= step, the one at
// +0xC while max - value >= step. Stated `@Adjuster.Render <widget> value min max step flags`.
REX_EXTERN(rex_lhGuiNumericAdjuster_Render) {
    const uint32_t widget = ctx.r3.u32;
    refii::vfs::GuiNativeCall call;
    call.name = "@Adjuster.Render";
    const auto add = [&call](std::string value) {
        refii::vfs::GuiNativeArgument argument;
        argument.kind = refii::vfs::GuiNativeArgument::Kind::kString;
        argument.text = std::move(value);
        call.arguments.push_back(std::move(argument));
    };
    char hex[9];
    std::snprintf(hex, sizeof hex, "%08X", widget);
    add(hex);
    for (const uint32_t at : {0x2Cu, 0x20u, 0x24u, 0x28u, 0x1Cu}) {
        add(std::to_string(static_cast<int32_t>(refii::vfs::LoadGuestU32(base, widget + at))));
    }
    refii::vfs::PostGuiNativeCall(std::move(call));
    __imp__rex_lhGuiNumericAdjuster_Render(ctx, base);
}

// MessageBox::Open — stated after the guest's own Open, so the presentation exists and the open
// byte is set by the time the host asks for either.
REX_EXTERN(rex_lhDisplayMessageBox_Open) {
    const uint32_t box = ctx.r3.u32;
    __imp__rex_lhDisplayMessageBox_Open(ctx, base);
    State(base, "@MessageBox.Open", box);
}

// MessageBox::Close — `RemoveDisplayBox` reaches a SHOWING box through this slot too
// (`sub_826B9AA8` 0x826b9b58).
REX_EXTERN(rex_lhDisplayMessageBox_Close) {
    const uint32_t box = ctx.r3.u32;
    __imp__rex_lhDisplayMessageBox_Close(ctx, base);
    State(base, "@MessageBox.Close", box);
}

// MessageBox::~MessageBox — only a box destroyed while still OPEN is stated: a normal close
// already was, and its 0.75 s fade ends here.
REX_EXTERN(rex_lhDisplayMessageBox_Dtor) {
    const uint32_t box = ctx.r3.u32;
    if (refii::vfs::LoadGuestU8(base, box + kBoxOpen) != 0u) {
        State(base, "@MessageBox.Close", box);
    }
    __imp__rex_lhDisplayMessageBox_Dtor(ctx, base);
}

// ---------------------------------------------------------------------------------------------
// THE DISPLAY-ONLY BOXES on the owner's +0x20 queue — the intermission strip (kind 5, screen 6) and
// the acceptance / button boxes (kinds 6 / 7, screen 7; router `sub_823D0B90`). Every opener (the
// `GUI.*` natives, the level exits, the game loop's chapter text, a partner's packet) builds one of
// these classes and the queue's tick calls its Open (vf1) and Close (vf2) with r3 the box, so the
// class methods are the one place every case passes:
//   intermission vtable 0x820EBFA8: Open `sub_8299EB40` writes `box+8` into `Group.Text` and plays
//     the root's slide 1; Close `sub_8299EBF8` plays slide 2 (disc 0x820DCF10, 0x82842328,
//     0x828423E0).
//   acceptance 0x820EBF3C / button 0x820028F4 share Show `sub_8299EE40` (`box+8` into
//     `DefaultGroup.FramePositioningGroup.Text`, then `g_DisplayBox:OpenAsAcceptanceBox()`) and
//     the close `sub_823D0998` (`g_DisplayBox:Close()`), reached through the acceptance's thunk
//     `sub_8299EA48` and the button's `CButtonBox_vf2` (disc 0x820DCEA4 / 0x82002914, 0x82842628,
//     0x827DE7C0).
// Each is posted `@DisplayBox.Open <box> <name> <body> <kind>` / `@DisplayBox.Close <box> <name>
// <body> <kind>` after the original, so the host stands its panel on the box's own statement.
//
// The answerable classes state themselves the same way, kind = the class's own type (vtable slot 7,
// `li r3,N`, which `sub_823D0B90` maps to a screen): query 2, numeric adjuster 3, quest box 4,
// hold-to-accept card 8. Their fields follow the kind — see each hook.
// ---------------------------------------------------------------------------------------------

namespace {

constexpr refii::guest::Addr kAcceptanceBoxVtable{0x820EBF3Cu};

void StateDisplay(uint8_t* base, const char* name, uint32_t box, uint32_t kind,
                  std::initializer_list<std::string> fields = {}) {
    refii::vfs::GuiNativeCall call;
    call.name = name;
    const auto add = [&call](std::string value) {
        refii::vfs::GuiNativeArgument argument;
        argument.kind = refii::vfs::GuiNativeArgument::Kind::kString;
        argument.text = std::move(value);
        call.arguments.push_back(std::move(argument));
    };
    char hex[9];
    std::snprintf(hex, sizeof hex, "%08X", box);
    add(hex);
    add(refii::vfs::ReadGuestLhString(base, box + kBoxIdentity));
    add(refii::vfs::ReadStatedWideLhString(base, box + kBoxBody));
    add(std::to_string(kind));
    for (const std::string& field : fields) {
        add(field);
    }
    refii::vfs::PostGuiNativeCall(std::move(call));
}

std::string Signed(uint8_t* base, uint32_t at) {
    return std::to_string(static_cast<int32_t>(refii::vfs::LoadGuestU32(base, at)));
}

std::string Flag(uint8_t* base, uint32_t at) {
    return refii::vfs::LoadGuestU8(base, at) != 0u ? "1" : "0";
}

std::string Tag(uint8_t* base, uint32_t at) { return refii::vfs::ReadGuestLhString(base, at); }

constexpr uint32_t kQueryKind = 2u;
constexpr uint32_t kAdjusterKind = 3u;
constexpr uint32_t kQuestKind = 4u;
constexpr uint32_t kAcceptHoldKind = 8u;

/// Acceptance (6) or button (7), by the box's class.
uint32_t AcceptKind(uint8_t* base, uint32_t box) {
    return refii::vfs::LoadGuestU32(base, box) == static_cast<uint32_t>(kAcceptanceBoxVtable) ? 6u : 7u;
}

}  // namespace

REX_EXTERN(rex_lhIntermissionBox_Open) {
    const uint32_t box = ctx.r3.u32;
    __imp__rex_lhIntermissionBox_Open(ctx, base);
    StateDisplay(base, "@DisplayBox.Open", box, 5u);
}

REX_EXTERN(rex_lhIntermissionBox_Close) {
    const uint32_t box = ctx.r3.u32;
    __imp__rex_lhIntermissionBox_Close(ctx, base);
    StateDisplay(base, "@DisplayBox.Close", box, 5u);
}

REX_EXTERN(rex_lhAcceptBox_Show) {
    const uint32_t box = ctx.r3.u32;
    __imp__rex_lhAcceptBox_Show(ctx, base);
    StateDisplay(base, "@DisplayBox.Open", box, AcceptKind(base, box));
}

REX_EXTERN(rex_lhAcceptBox_Close) {
    const uint32_t box = ctx.r3.u32;
    const uint32_t kind = AcceptKind(base, box);
    __imp__rex_lhAcceptBox_Close(ctx, base);
    StateDisplay(base, "@DisplayBox.Close", box, kind);
}

// CQueryBox (vtable 0x820AE688, `GUI.DisplayYesNoQuestion` and its two siblings): Open
// `sub_823CF228` writes `box+8` into `Group.QueryBox.Text` and sets the open byte; Close
// `sub_823CF300` clears it (disc 0x823BD8D0 / 0x823BD9A8). Answered by its slot 3, 46/47.
REX_EXTERN(rex_lhQueryBox_Open) {
    const uint32_t box = ctx.r3.u32;
    __imp__rex_lhQueryBox_Open(ctx, base);
    StateDisplay(base, "@DisplayBox.Open", box, kQueryKind);
}

REX_EXTERN(rex_lhQueryBox_Close) {
    const uint32_t box = ctx.r3.u32;
    __imp__rex_lhQueryBox_Close(ctx, base);
    StateDisplay(base, "@DisplayBox.Close", box, kQueryKind);
}

// CNumericAdjuster (vtable 0x820AE6A8, `GUI.DisplayNumericAdjusterParams`): its widget is
// `box+24`, the router adjuster's layout — the ctor `sub_823CF508` (disc 0x823BDBB0) stores min
// +56, max +60, step +64, value +68 and flags +52 (1 money, 2 percent, 4 sign); Open
// `sub_823CF6B8` formats it (`sub_826B8BB0(box+24)`) and writes `box+8`; Close `sub_823CF7F0`
// (disc 0x823BDCF8 / 0x823BDE30). Fields: value, min, max, step, flags — the order `@Modal.Open`
// states the router's — then the widget `box+24`, hex, that Open renders (0x823cf750 / 0x823bdd90).
REX_EXTERN(rex_lhNumericAdjusterBox_Open) {
    const uint32_t box = ctx.r3.u32;
    __imp__rex_lhNumericAdjusterBox_Open(ctx, base);
    char widget[9];
    std::snprintf(widget, sizeof widget, "%08X", box + 24u);
    StateDisplay(base, "@DisplayBox.Open", box, kAdjusterKind,
                 {Signed(base, box + 68u), Signed(base, box + 56u), Signed(base, box + 60u),
                  Signed(base, box + 64u), Signed(base, box + 52u), std::string(widget)});
}

REX_EXTERN(rex_lhNumericAdjusterBox_Close) {
    const uint32_t box = ctx.r3.u32;
    __imp__rex_lhNumericAdjusterBox_Close(ctx, base);
    StateDisplay(base, "@DisplayBox.Close", box, kAdjusterKind);
}

// CQuestInfoBox (vtable 0x820AE6C8, `GUI.DisplayQuestBoxParams`): the ctor `sub_826B96F8` (disc
// 0x8240D160) copies the call's table into +24 Type, +28 RiskLevel, +32 the condition count, and
// the tags +36 Condition1, +40 Condition2, +44 Occupation, +48 Appearance, +52 Features,
// +56 TargetName, +60 Whereabouts, +64 Reason. Open `sub_823CFF60` plays root slide Type and runs
// the filler `sub_823CFBE0`, which writes `box+8` and those tags, localised, into the page; Close
// `sub_823D0000` (disc 0x823BE608 / 0x823BE6A8). Fields in that order, the tags raw.
REX_EXTERN(rex_lhQuestInfoBox_Open) {
    const uint32_t box = ctx.r3.u32;
    __imp__rex_lhQuestInfoBox_Open(ctx, base);
    StateDisplay(base, "@DisplayBox.Open", box, kQuestKind,
                 {Signed(base, box + 24u), Signed(base, box + 28u), Signed(base, box + 32u),
                  Tag(base, box + 36u), Tag(base, box + 40u), Tag(base, box + 44u),
                  Tag(base, box + 48u), Tag(base, box + 52u), Tag(base, box + 56u),
                  Tag(base, box + 60u), Tag(base, box + 64u)});
}

REX_EXTERN(rex_lhQuestInfoBox_Close) {
    const uint32_t box = ctx.r3.u32;
    __imp__rex_lhQuestInfoBox_Close(ctx, base);
    StateDisplay(base, "@DisplayBox.Close", box, kQuestKind);
}

// CAcceptHoldBox (vtable 0x820EBF1C, the `Paper` card: style-0 `DisplayInfoBoxParams`, photo
// mode's upload notice): the ctor `sub_8299E318` (disc 0x82841A98) keeps the title tag +32, ShowA
// +36, ShowY +37, HoldA +38, the accept caption tag +40 and the help caption tag +44. Open
// `sub_8299E490` writes `box+8` into `Paper.Text`, the resolved +32 into `Paper.Title`, each caption
// with its prompt's opacity, and plays root slide 7; Close `sub_8299E708` plays 8 (disc 0x82841C10
// / 0x82841E88). Its answer is its own input slot (+16, `CAcceptHoldBox_vf4`), the guest's.
// Fields: title tag, ShowA, ShowY, HoldA, accept tag, help tag.
REX_EXTERN(rex_lhAcceptHoldBox_Open) {
    const uint32_t box = ctx.r3.u32;
    __imp__rex_lhAcceptHoldBox_Open(ctx, base);
    StateDisplay(base, "@DisplayBox.Open", box, kAcceptHoldKind,
                 {Tag(base, box + 32u), Flag(base, box + 36u), Flag(base, box + 37u),
                  Flag(base, box + 38u), Tag(base, box + 40u), Tag(base, box + 44u)});
}

REX_EXTERN(rex_lhAcceptHoldBox_Close) {
    const uint32_t box = ctx.r3.u32;
    __imp__rex_lhAcceptHoldBox_Close(ctx, base);
    StateDisplay(base, "@DisplayBox.Close", box, kAcceptHoldKind);
}

// A BOX DESTROYED WHILE OPEN. The queues hold their boxes by counted pointer, whose deleter
// (`sub_82529238`) runs the class's slot 0. The queue's tick releases its current box only once its
// slot 5 answers 0, after a Close; the owner's teardown (`sub_82373458` 0x82373588 / 0x823735a0,
// inlined on disc at `sub_82335218` 0x823353bc / 0x823353d4) releases both queues' current boxes
// with no Close. Slot 0 is `sub_823CF618` for query, adjuster, intermission, acceptance and button
// (disc `sub_828422C0`), `sub_823CFB70` for the quest box (disc `sub_823BE1B0`) and `sub_8299E400`
// for the hold-to-accept card (disc `sub_82841B80`); each states `@DisplayBox.Close` for a box whose
// open byte still stands.
namespace {

constexpr refii::guest::Addr kQueryBoxVtable{0x820AE688u};
constexpr refii::guest::Addr kAdjusterBoxVtable{0x820AE6A8u};
constexpr refii::guest::Addr kIntermissionBoxVtable{0x820EBFA8u};
constexpr refii::guest::Addr kButtonBoxVtable{0x820028F4u};

void StateDestroyedOpen(uint8_t* base, uint32_t box, uint32_t kind) {
    if (refii::vfs::LoadGuestU8(base, box + kBoxOpen) != 0u) {
        StateDisplay(base, "@DisplayBox.Close", box, kind);
    }
}

}  // namespace

REX_EXTERN(rex_lhInfoBox_Dtor) {
    const uint32_t box = ctx.r3.u32;
    const uint32_t vtable = refii::vfs::LoadGuestU32(base, box);
    const uint32_t kind = vtable == static_cast<uint32_t>(kQueryBoxVtable)          ? kQueryKind
                          : vtable == static_cast<uint32_t>(kAdjusterBoxVtable)     ? kAdjusterKind
                          : vtable == static_cast<uint32_t>(kIntermissionBoxVtable) ? 5u
                          : vtable == static_cast<uint32_t>(kAcceptanceBoxVtable)   ? 6u
                          : vtable == static_cast<uint32_t>(kButtonBoxVtable)       ? 7u
                                                                                    : 0xFFFFFFFFu;
    if (kind != 0xFFFFFFFFu) {
        StateDestroyedOpen(base, box, kind);
    }
    __imp__rex_lhInfoBox_Dtor(ctx, base);
}

REX_EXTERN(rex_lhQuestInfoBox_Dtor) {
    StateDestroyedOpen(base, ctx.r3.u32, kQuestKind);
    __imp__rex_lhQuestInfoBox_Dtor(ctx, base);
}

REX_EXTERN(rex_lhAcceptHoldBox_Dtor) {
    StateDestroyedOpen(base, ctx.r3.u32, kAcceptHoldKind);
    __imp__rex_lhAcceptHoldBox_Dtor(ctx, base);
}

// ---------------------------------------------------------------------------------------------
// THE MENU BOX, the controller `*(guiOwner+56)` (a plain struct): +0 selected, +8 presentation,
// +12 open, +13 cancelled, +14 cancelable, +20/+24 the option vector (8-byte `{item*, ref*}`; an
// item's +0 is its text tag, +4 its enabled byte), +32 the title (wide), +44 the Orb's player card.
// Every opener — `DisplayMenuBox` (`sub_82303BF0`), the Orb (`sub_823DB968`), the sleep menu, and
// `sub_826C7E90` — goes through the open `sub_823DC100`, whose populate `sub_823DC400` (disc
// 0x8240C630) writes the title and each row's resolved tag and enabled slide, and returns 1; a
// row's enable (`sub_823DBBD0`) repopulates an open box. The close `sub_823DC2B0` (disc 0x8240C4E0)
// posts message 35 with `+13 ? 0 : selected + 1` (0x823dc354), then clears the title and rows.
//
// Populate states `@MenuBox.Open <box> <title> <cancelable> <card> <row>…`, a row its tag prefixed
// `+` enabled or `-` disabled; close states `@MenuBox.Close <box> <answer>` when it closed an open
// box.
// ---------------------------------------------------------------------------------------------

namespace {

constexpr uint32_t kMenuBoxMaxRows = 6u;

bool PlausibleItem(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

void StateMenuBoxOpen(uint8_t* base, uint32_t box) {
    refii::vfs::GuiNativeCall call;
    call.name = "@MenuBox.Open";
    const auto add = [&call](std::string value) {
        refii::vfs::GuiNativeArgument argument;
        argument.kind = refii::vfs::GuiNativeArgument::Kind::kString;
        argument.text = std::move(value);
        call.arguments.push_back(std::move(argument));
    };
    char hex[9];
    std::snprintf(hex, sizeof hex, "%08X", box);
    add(hex);
    add(refii::vfs::ReadStatedWideLhString(base, box + 32u));
    add(Flag(base, box + 14u));
    add(refii::vfs::LoadGuestU32(base, box + 44u) != 0u ? "1" : "0");
    const uint32_t begin = refii::vfs::LoadGuestU32(base, box + 20u);
    const uint32_t end = refii::vfs::LoadGuestU32(base, box + 24u);
    if (PlausibleItem(begin) && end > begin && (end - begin) / 8u <= kMenuBoxMaxRows) {
        for (uint32_t at = begin; at < end; at += 8u) {
            const uint32_t item = refii::vfs::LoadGuestU32(base, at);
            if (!PlausibleItem(item)) {
                add(std::string());
                continue;
            }
            add((refii::vfs::LoadGuestU8(base, item + 4u) != 0u ? "+" : "-") + Tag(base, item));
        }
    }
    refii::vfs::PostGuiNativeCall(std::move(call));
}

}  // namespace

// The menu box's cursor `sub_823DBB48(box, index)` (disc `sub_8240BD78`): `*box = index`, -1 for
// none. Stated `@MenuBox.Select <box> <index>`.
REX_EXTERN(rex_lhMenuBox_SetSelection) {
    const uint32_t box = ctx.r3.u32;
    const int32_t index = static_cast<int32_t>(ctx.r4.u32);
    __imp__rex_lhMenuBox_SetSelection(ctx, base);
    refii::vfs::GuiNativeCall call;
    call.name = "@MenuBox.Select";
    char hex[9];
    std::snprintf(hex, sizeof hex, "%08X", box);
    for (std::string value : {std::string(hex), std::to_string(index)}) {
        refii::vfs::GuiNativeArgument argument;
        argument.kind = refii::vfs::GuiNativeArgument::Kind::kString;
        argument.text = std::move(value);
        call.arguments.push_back(std::move(argument));
    }
    refii::vfs::PostGuiNativeCall(std::move(call));
}

REX_EXTERN(rex_lhMenuBox_Populate) {
    const uint32_t box = ctx.r3.u32;
    __imp__rex_lhMenuBox_Populate(ctx, base);
    if ((ctx.r3.u32 & 0xFFu) != 0u) {
        refii::vfs::StatedMenuBoxOpened();
        StateMenuBoxOpen(base, box);
    }
}

// The Orb box's player card `sub_823DB850(box, xuid, name)` (disc `sub_8240BA80`): r3 the menu-box
// controller, r4 the XUID, r5 the NAME — an lhCharString (`mr r30, r5` 0x823db868 / 0x8240ba98),
// copied (0x823db8d0 / 0x8240bb00) into the card ctor's r9 (`sub_8281E7B8` 0x823db914 / disc
// `sub_82840878` 0x8240bb44), which assigns it to `card+4` (0x8281e820 / 0x828408e0): the text the
// card's fill `sub_8281F010` writes into its gamertag element (0x8281f164). The only caller, the Orb
// open `sub_823DB968` (0x823dba34 / 0x8240bc64), passes the empty string (0x834A5E78 / 0x834A5B6C).
// Stated `@GamerCard.Name <box> <name>`. The score (`card+44`) is no argument: the card reads it
// from its profile read (`sub_8281EF80` 0x8281eff0, setting 0x10040006).
REX_EXTERN(rex_lhGuiGamerCard_SetName) {
    const uint32_t box = ctx.r3.u32;
    const std::string name = refii::vfs::ReadGuestLhString(base, ctx.r5.u32);
    __imp__rex_lhGuiGamerCard_SetName(ctx, base);
    refii::vfs::GuiNativeCall call;
    call.name = "@GamerCard.Name";
    refii::vfs::GuiNativeArgument address;
    address.kind = refii::vfs::GuiNativeArgument::Kind::kString;
    char hex[9];
    std::snprintf(hex, sizeof hex, "%08X", box);
    address.text = hex;
    call.arguments.push_back(std::move(address));
    refii::vfs::GuiNativeArgument said;
    said.kind = refii::vfs::GuiNativeArgument::Kind::kString;
    said.text = name;
    call.arguments.push_back(std::move(said));
    refii::vfs::PostGuiNativeCall(std::move(call));
}

// The menu box's close is hooked once, by the mod events (mod_events.cpp rex_lhMenuBox_Close), which
// calls this with the box's state read after its verdict and before the guest's close: +8 rows and
// +12 open, the answer `+13 ? 0 : +0 + 1` that the close posts as message 35 (0x823DC354).
namespace refii::vfs {

void StateMenuBoxClose(uint8_t* base, uint32_t box) {
    const bool open = refii::vfs::LoadGuestU32(base, box + 8u) != 0u &&
                      refii::vfs::LoadGuestU8(base, box + 12u) != 0u;
    if (!open) {
        return;
    }
    const uint32_t answer = refii::vfs::LoadGuestU8(base, box + 13u) != 0u
                                ? 0u
                                : refii::vfs::LoadGuestU32(base, box) + 1u;
    refii::vfs::GuiNativeCall call;
    call.name = "@MenuBox.Close";
    refii::vfs::GuiNativeArgument address;
    address.kind = refii::vfs::GuiNativeArgument::Kind::kString;
    char hex[9];
    std::snprintf(hex, sizeof hex, "%08X", box);
    address.text = hex;
    call.arguments.push_back(std::move(address));
    refii::vfs::GuiNativeArgument value;
    value.kind = refii::vfs::GuiNativeArgument::Kind::kString;
    value.text = std::to_string(answer);
    call.arguments.push_back(std::move(value));
    refii::vfs::PostGuiNativeCall(std::move(call));
}

}  // namespace refii::vfs

// ---------------------------------------------------------------------------------------------
// A BOX FOR ONE PLAYER (local split screen). The box classes name no player: `GUI.DisplayMessageBox`
// (`sub_823027B0`) and `GUI.DisplayYesNoQuestion` (`sub_823044A8`) build their box and push it on the
// owner's +4 queue (`sub_826B99D8(owner + 4, box)` at 0x82302824 / 0x82304520), the menu boxes on the
// +0x20 one, and the queue's tick opens it later. The core co-op Lua states whose a box is around the
// one guest call that builds it (Refii._ui_seat): every box that call queued is that player's, stated
// on the GUI call feed as `@Box.Seat <address> <player>` so the host seats it in their half and gives
// it their pad. A queue `{vtable, ?, begin +8, end +12, capacity +16, current +20}` holds 8-byte
// elements whose first word is the box (`sub_822F5948`); the current box is +20.
// ---------------------------------------------------------------------------------------------

namespace {

constexpr uint32_t kGuiGlobal = 0x83496B08u;
constexpr uint32_t kQueues[] = {4u, 0x20u};
constexpr uint32_t kMaxQueued = 64u;

bool PlausibleBox(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

uint32_t BoxOwner(uint8_t* base) {
    const uint32_t gui = refii::vfs::LoadGuestU32(base, refii::guest::Address(kGuiGlobal));
    const uint32_t holder = PlausibleBox(gui) ? refii::vfs::LoadGuestU32(base, gui + 28u) : 0u;
    const uint32_t owner = PlausibleBox(holder) ? refii::vfs::LoadGuestU32(base, holder + 52u) : 0u;
    return PlausibleBox(owner) ? owner : 0u;
}

/// Every box the two queues hold now, current ones included.
std::vector<uint32_t> QueuedBoxes(uint8_t* base) {
    std::vector<uint32_t> boxes;
    const uint32_t owner = BoxOwner(base);
    if (owner == 0u) {
        return boxes;
    }
    for (const uint32_t queue : kQueues) {
        const uint32_t begin = refii::vfs::LoadGuestU32(base, owner + queue + 8u);
        const uint32_t end = refii::vfs::LoadGuestU32(base, owner + queue + 12u);
        if (PlausibleBox(begin) && end > begin && (end - begin) / 8u <= kMaxQueued) {
            for (uint32_t at = begin; at < end; at += 8u) {
                const uint32_t box = refii::vfs::LoadGuestU32(base, at);
                if (PlausibleBox(box)) {
                    boxes.push_back(box);
                }
            }
        }
        const uint32_t current = refii::vfs::LoadGuestU32(base, owner + queue + 20u);
        if (PlausibleBox(current)) {
            boxes.push_back(current);
        }
    }
    return boxes;
}

struct SeatScope {
    int32_t player = 0;
    std::vector<uint32_t> before;
};

/// Guest tick thread only (the Lua natives run there).
std::vector<SeatScope> g_seat_scopes;

}  // namespace

namespace refii::vfs {

void BoxSeatEnter(uint8_t* base, int32_t player) {
    if (g_seat_scopes.size() >= 8u) {
        return;
    }
    g_seat_scopes.push_back(SeatScope{player, QueuedBoxes(base)});
}

void BoxSeatLeave(uint8_t* base) {
    if (g_seat_scopes.empty()) {
        return;
    }
    const SeatScope scope = std::move(g_seat_scopes.back());
    g_seat_scopes.pop_back();
    for (const uint32_t box : QueuedBoxes(base)) {
        bool known = false;
        for (const uint32_t was : scope.before) {
            known = known || was == box;
        }
        if (known) {
            continue;
        }
        refii::vfs::GuiNativeCall call;
        call.name = "@Box.Seat";
        refii::vfs::GuiNativeArgument address;
        address.kind = refii::vfs::GuiNativeArgument::Kind::kString;
        char hex[9];
        std::snprintf(hex, sizeof hex, "%08X", box);
        address.text = hex;
        call.arguments.push_back(std::move(address));
        refii::vfs::GuiNativeArgument player;
        player.kind = refii::vfs::GuiNativeArgument::Kind::kString;
        player.text = std::to_string(scope.player);
        call.arguments.push_back(std::move(player));
        refii::vfs::PostGuiNativeCall(std::move(call));
    }
}

}  // namespace refii::vfs
