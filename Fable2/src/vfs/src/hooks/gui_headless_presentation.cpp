// HEADLESS GAMEFACE PRESENTATIONS, Fable 2's hooks. The guest keeps its UI objects; it never
// builds a .bgf scene. The work is the shared headless core's (core/headless_presentation.h,
// src/payload/src/core/headless_presentation.cpp): these hooks are its shims, and Fable2Table()
// is Fable 2's Gameface for it — every address TU1, converted to the running build.
//
// THE CREATE. `CGameface` vtable +28 (TU1 `sub_82BBA588`, disc `sub_82BB3E88`; vtable TU1
// 0x82002F68, disc 0x82002F88) is the only maker of a presentation. Its callers are
// lhGuiPresentation_LoadIfUnloaded (TU1 `sub_823DCB10` 0x823dcbb0, disc `sub_823FE828`) and the
// CGameface's own loader (+16 `sub_82BBA370` 0x82bba47c). Shipped, it makes a CPresentation in the
// kernel (`sub_82BEDBC0`: kernel alloc 876, ctor `sub_82BAFA28`, kernel list push), names it
// (vf +72), gives it its Lua engine (0x82bba624..0x82bba718), deserialises the .bgf (CGameface+528
// vf +12) and adds its scene to the renderer. The core makes, names and gives an engine to the same
// CPresentation with the same calls (GiveEngine below is Fable 2's engine sequence) and does none
// of the rest. The engine is the mods' front-end Lua state (mod_frontend_lua.cpp); no guest chunk
// reaches it (the runner and `sub_82372500` are swallowed in gui_menu_hooks.cpp /
// resource_manager_open.cpp).
//
// ELEMENTS ARE MADE AS THE GUEST NAMES THEM, AS THE FILE HAS THEM. Child-by-name, element service
// vf +84 (TU1 `sub_82BB5658`, disc `sub_82BACC90`; r3 service, r4 name hash, r5 parent or 0), and
// the child walk, vf +92 (TU1 `sub_82BB56A8`, disc `sub_82BACCE0`), make a headless parent's
// children from the .bgf element table, laid out as the guest's deserialiser (TU1 `sub_82BC8038`,
// disc `sub_82BC1968`) would have left them: the file's active bit as flag 0x2000 (the element
// builders' `vf+80(e, 0x2000, file bit 0)`, `sub_82BB2E18` / `sub_82BB2F10`), 0x0001 on a component
// (its own timeline; attributes at +68, else +32), 0x0002 on one with a start or end time, its
// numeric attribute records in the store's word form, and a component's slide base and count
// (`sub_82BB3778`) over the presentation's slide records at Mgr:Slides +8 (pres +628). So:
//  - the attribute lookup (TU1 `sub_822848E0` / `sub_82284948`, disc `sub_82284448` /
//    `sub_822844B0`) and everything through it answers the file's value until the guest writes it,
//    and the store (`sub_82284798`, vf +72) writes into that record — the show/hide helper
//    (`sub_82426260`, disc `sub_823F86F8`) reads the authored opacity at 0x82426294, GetPosition
//    (0x824252E8, disc 0x823F7780) the authored or last-set position (the credits' scroll
//    `sub_826B7D00` 0x826b7d60, the pause minimap's origin 0x826a22b0);
//  - the active bit (vf +76) is the file's until SetActive / the flags writer changes it, and the
//    presentation Update's tree walk (`sub_822849A0`, entered on root 0x2000 at 0x822ac9e0) runs
//    over the tree as over a loaded one;
//  - GotoSlide (`sub_82BB5760`) lands on a component (count +42, base +40) and GetState
//    (`sub_82426D80`, through the timeline-owner walk `sub_82BB9AB0`) answers it. The slide
//    records carry no actions: Godot plays the slides from the statements.
// The root always owns its timeline: the owner walk has no null exit (0x82bb9af0..0x82bb9b48).
// +4 (the render node) stays 0 on every element: the writers that take the node's record null-check
// it, and the quad readers' joins that do not are answered with the core's inert quad record
// (RefiiHeadlessQuadRecord). String attributes are answered at the attribute-string read.
//
// THE SCENE, THE DESTROY, THE PER-FRAME PASS: see the core. `CGameface` vtable +32 (TU1
// `sub_82BBA8D0`, disc `sub_82BB41D0`, named rex_lhItemManager_NotifyAndRemoveItem in the config),
// vtable +40 (TU1 `sub_822BE860`, disc `sub_822BE580`). The scene's vtable is CScene's (TU1
// 0x82003E38, disc 0x82003E58), called unchecked as vf +16 by `sub_823DCF98` 0x823dd010,
// PopulateMainMenu 0x826e21b0, MenuActionDispatch 0x826e0ac8 / 0x826e0bd4 and the message-box fit
// `sub_823CEFA8` 0x823cf1f4.
//
// THE ROOT'S TIME. The co-op screen (TU1 `sub_82436548`, disc `sub_82477768`) gates player two's
// preview on the shared slot's root timeline time, `lwz r11,0x30(r3)` straight after
// GetRootElement: >= 1000 at 0x82436930 and >= 500 at 0x82436988. GetRootElement writes the root's
// +48 = clock − creation clock.
//
// EXISTENCE-CHECKED WRITES. SetPosition (TU1 `sub_824251E0`, disc `sub_823F7678`: r3 element, r4
// presentation, f1..f3; position.x checked through service vf +12 at 0x8242524c, then x/y/z
// `dword_834BE51C/520/524` through the store), SetRotation (TU1 `sub_824254C8`, disc
// `sub_823F7960`: rotation.x `dword_834BE528` checked by `sub_82284948` at 0x82425530, then
// x/y/z 528/52C/530) and SetOpacity (`GuiElement_SetOpacity_p` TU1 0x82426078, disc
// `sub_823F8510`: opacity `dword_834BE518`) write only when the element carries the attribute. A
// file element carries what the file authored, as a loaded one does, and the guest's own write and
// its statement (the store's, gui_element_hooks.cpp) follow from that. A fileless element (no
// table, or a name the file has not got) is given the attribute at the entry (RememberWrite), so
// its write lands too; only when it has no room left is a position or rotation write stated here
// from the arguments. SetOpacity's never is: the value it stores is the store's statement, and
// with no room it stores nothing (RefiiHeadlessSetOpacity).
//
// THE FRONT END'S STATEMENTS. Its writers store a quad record's +512 inline (no setter) and only
// through +4, so on a headless element they skip. The load fan's cards are stated at StampSaveCard
// (`sub_826E1408` / disc `sub_826C9828`) as (save, card slot, texture), each save's screenshot
// also as the element's texture where the stamp takes it (0x826e14b0 / disc 0x826c98d0, r30 the
// hash); the dialog at OpenDialog (`sub_826E40E0` / disc `sub_826CB158`) as its texts, cancel and
// Y glyph. See each.
//
// TEXTS. SetText (TU1 `sub_82425DD0`: r3 element, r4 presentation, r5 the text's handle) and
// SetTextFromTag (`sub_82425F40`: r3 element, r4 the tag) store only through +4 and bail without
// it. Each is stated at its entry (midasm) for a headless element: SetText's string as given;
// SetTextFromTag's through the guest's own lookup `sub_8230E0E8` (Babel, TEXT_ fallback), as the
// writer would have stored it (0x82425f78..0x82425fb8).

#include <algorithm>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <initializer_list>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#include "vfs_hooks_private.h"
#include "core/guest_build.h"
#include "core/guest_state.h"
#include "core/gui_native_calls.h"
#include "core/headless_presentation.h"
#include "core/payload_bridge_fable2.h"
#include "gui/gui_told_statements.h"
#include "hooks/frontend_stated.h"
#include "hooks/ppc_externs.h"

extern "C" uint8_t* RefiiGuestBase(void);

namespace {

using refii::vfs::LoadGuestU16;
using refii::vfs::LoadGuestU32;
using refii::vfs::StoreGuestU32;

// The kernel allocator and the presentation's ctor (`sub_82BEDBC0` 0x82bedbe8..0x82bedc14).
constexpr refii::guest::Addr kKernelAllocator{0x82BAEE60u};  // returns the allocator
constexpr refii::guest::Addr kKernelAlloc{0x82BAF660u};      // (allocator, size, class, file, line)
constexpr refii::guest::Addr kKernelFree{0x82BAF7B0u};       // (allocator, ptr, size)
constexpr refii::guest::Addr kPresentationCtor{0x82BAFA28u};  // (this, name, gameface)
constexpr refii::guest::Addr kPresentationClassName{0x820FFF98u};  // "class CPresentation"
constexpr refii::guest::Addr kKernelSourceName{0x820FFF30u};       // "...AKKernel.cpp"
constexpr uint32_t kKernelSourceLine = 71u;                        // `li r7,0x47`
constexpr uint32_t kPresentationSize = 876u;                       // `li r4,0x36C`

// The create's engine steps, 0x82bba61c..0x82bba718 (disc 0x82bb3f1c..0x82bb4018).
constexpr refii::guest::Addr kEngineFactory{0x83314AC0u};    // -> `sub_82BC4548`(pres)
constexpr refii::guest::Addr kGameplanFactory{0x83314AC4u};  // -> `li r3,0`
constexpr refii::guest::Addr kRegisterDataPort{0x82BC5DA8u};  // (gameface + 0x214, L)
constexpr refii::guest::Addr kLuaSetField{0x82BCC600u};       // (L, index, char*)
constexpr refii::guest::Addr kLuaPushClosure{0x8227B968u};    // (L, fn, 0)
constexpr refii::guest::Addr kFireGameplanEvent{0x82BC5B50u};
constexpr refii::guest::Addr kFireEventOnPresentation{0x82BC5C38u};
constexpr refii::guest::Addr kNameAkGameplan{0x820FF2B8u};         // "AKGameplan"
constexpr refii::guest::Addr kNameFireGameplanEvent{0x820FF2C4u};  // "fireGameplanEvent"
constexpr refii::guest::Addr kNameFireEventOnPresentation{0x820FF2D8u};
constexpr uint32_t kLuaGlobalsIndex = 0xFFFFD8EEu;  // -10002, `li r4,-0x2712`
constexpr uint32_t kGamefaceDataPorts = 0x214u;     // `addi r3,r28,0x214`
constexpr uint32_t kGamefaceGameplan = 0x248u;      // `lwz r9,0x248(r28)`, pushed as light userdata
constexpr uint32_t kLuaTop = 8u;                    // lua_State +8
constexpr uint32_t kLuaLightUserdata = 2u;          // `li r11,2`
constexpr uint32_t kPresEngine = 28u;               // vf +16 `stw r4,0x1C(r3)`
constexpr uint32_t kPresGameplan = 32u;             // `stw r3,0x20(r31)` 0x82bba718
constexpr uint32_t kPresSlotSetEngine = 16u;

constexpr refii::guest::Addr kNameHash{0x82189140u};       // h = 65599*h + c, NUL folded
constexpr refii::guest::Addr kParametersName{0x8349AF78u};  // CCharString "Parameters"
constexpr refii::guest::Addr kSceneVtable{0x82003E38u};     // CScene, ctor `sub_82BC9918`

// SetTextFromTag's own steps (0x82425f78 / 0x82425f84 / 0x82425fb8).
constexpr refii::guest::Addr kCharStringCopy{0x821F0240u};    // (out, CCharString*) -> out
constexpr refii::guest::Addr kTextLookup{0x8230E0E8u};        // (out wide, CCharString*), frees the arg
constexpr refii::guest::Addr kWideStringDtor{0x821C67F8u};    // (wide*)

// CCharString's own steps: GetAttributeString's result (0x824250f8), its dtor, and the texture
// setter's hash of a name (`sub_8286DDC0` 0x8286de1c..0x8286de3c: copy, lower case, hash, dtor).
// The ctor hands r5 straight to its body (`sub_822078A0`, disc `sub_822079D8`): -1 measures the
// text — every guest caller loads `li r5,-1` (0x824250f0).
constexpr refii::guest::Addr kCharStringFromCStr{0x8222CED0u};  // (this, char*, length) -> this
constexpr refii::guest::Addr kCharStringDtor{0x82214DD8u};      // (this)
constexpr refii::guest::Addr kCharStringLower{0x821EEB38u};     // (out, CCharString*) -> out
constexpr refii::guest::Addr kResourceNameHash{0x82C037F0u};    // (CCharString*) -> hash

// An attribute's id: the name hash (`sub_82189140`) in 27 bits (0x82425080..0x8242508c).
constexpr uint32_t kAttributeIdMask = 0x07FFFFFFu;

// The attribute ids' globals (static init 0x83294294 onward; the setters load them).
constexpr refii::guest::Addr kAttrStartTime{0x834BE510u};  // the deserialiser's 0x0002 pair (0x82bc83ec)
constexpr refii::guest::Addr kAttrEndTime{0x834BE514u};
constexpr refii::guest::Addr kAttrOpacity{0x834BE518u};
constexpr refii::guest::Addr kAttrPositionX{0x834BE51Cu};
constexpr refii::guest::Addr kAttrPositionY{0x834BE520u};
constexpr refii::guest::Addr kAttrPositionZ{0x834BE524u};
constexpr refii::guest::Addr kAttrRotationX{0x834BE528u};
constexpr refii::guest::Addr kAttrRotationY{0x834BE52Cu};
constexpr refii::guest::Addr kAttrRotationZ{0x834BE530u};

constexpr refii::guest::Addr kCardBlankTexture{0x8349B600u};
constexpr refii::guest::Addr kGlyphCancelTexture{0x8349B694u};
constexpr refii::guest::Addr kGlyphOtherTexture{0x8349B690u};

constexpr refii::guest::Addr kQuadRecordCtor{0x8286DAC8u};  // (this) -> this
constexpr uint32_t kQuadRecordSize = 576u;                  // `li r3,0x240` in `sub_829B8480`

constexpr uint32_t kMaxTextUnits = 4096u;

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

bool Milestone(uint64_t n) { return n <= 32u || (n & (n - 1u)) == 0u; }

uint32_t Call(PPCContext& ctx, uint8_t* base, uint32_t address, uint32_t r3, uint32_t r4 = 0u, uint32_t r5 = 0u,
              uint32_t r6 = 0u) {
    PPCFunc* fn = refii::vfs::FindGuestFunction(address);
    if (fn == nullptr) {
        REXLOG_ERROR("Refii headless presentation: guest function 0x{:08X} is not a recompiled entry", address);
        return 0u;
    }
    PPCContext call = ctx;
    call.r3.u32 = r3;
    call.r4.u32 = r4;
    call.r5.u32 = r5;
    call.r6.u32 = r6;
    call.ctr.u32 = address;
    fn(call, base);
    return call.r3.u32;
}

/// One guest call on a frame below `sp`.
uint32_t CallAt(PPCContext& ctx, uint8_t* base, uint32_t sp, uint32_t address, uint32_t r3, uint32_t r4 = 0u) {
    PPCContext at = ctx;
    at.r1.u32 = sp;
    return Call(at, base, address, r3, r4);
}

uint32_t Slot(uint8_t* base, uint32_t object, uint32_t offset) {
    return LoadGuestU32(base, LoadGuestU32(base, object) + offset);
}

/// Fable 2's create engine steps 0x82bba61c..0x82bba718: the engine (vf +16), the data ports,
/// the AKGameplan light userdata and its two closures, then the gameplan (+32).
void GiveEngine(PPCContext& ctx, uint8_t* base, uint32_t gameface, uint32_t presentation) {
    const uint32_t engine = Call(ctx, base, LoadGuestU32(base, kEngineFactory), presentation);
    Call(ctx, base, Slot(base, presentation, kPresSlotSetEngine), presentation, engine);
    if (Plausible(engine)) {
        const uint32_t L = LoadGuestU32(base, engine + 4u);
        Call(ctx, base, kRegisterDataPort, gameface + kGamefaceDataPorts, L);
        const uint32_t top = LoadGuestU32(base, L + kLuaTop);
        StoreGuestU32(base, top, LoadGuestU32(base, gameface + kGamefaceGameplan));
        StoreGuestU32(base, top + 4u, kLuaLightUserdata);
        StoreGuestU32(base, L + kLuaTop, top + 8u);
        Call(ctx, base, kLuaSetField, L, kLuaGlobalsIndex, kNameAkGameplan);
        Call(ctx, base, kLuaPushClosure, L, kFireGameplanEvent, 0u);
        Call(ctx, base, kLuaSetField, L, kLuaGlobalsIndex, kNameFireGameplanEvent);
        Call(ctx, base, kLuaPushClosure, L, kFireEventOnPresentation, 0u);
        Call(ctx, base, kLuaSetField, L, kLuaGlobalsIndex, kNameFireEventOnPresentation);
    }
    StoreGuestU32(base, presentation + kPresGameplan,
                  Call(ctx, base, LoadGuestU32(base, kGameplanFactory), presentation));
}

uint32_t GuestAlloc(PPCContext& ctx, uint8_t* base, uint32_t size) { return refii::vfs::CallLhMalloc(ctx, base, size); }

void GuestFree(PPCContext& ctx, uint8_t* base, uint32_t address) { refii::vfs::CallLhFree(ctx, base, address); }

PPCFunc* Find(uint32_t address) { return refii::vfs::FindGuestFunction(address); }

refii::headless::GamefaceTable BuildTable() {
    refii::headless::GamefaceTable t;
    t.title = "Fable 2";
    t.find = &Find;
    t.alloc = &GuestAlloc;
    t.free = &GuestFree;
    t.give_engine = &GiveEngine;
    t.kernel_allocator = kKernelAllocator;
    t.kernel_alloc = kKernelAlloc;
    t.kernel_free = kKernelFree;
    t.presentation_ctor = kPresentationCtor;
    t.presentation_class_name = kPresentationClassName;
    t.kernel_source_name = kKernelSourceName;
    t.kernel_source_line = kKernelSourceLine;
    t.presentation_size = kPresentationSize;
    // CPresentation (ctor `sub_82BAFA28`; vtable TU1 0x82006D48, disc 0x82006D68).
    t.pres_scene = 24u;          // vf +8 sets, vf +12 gets
    t.pres_engine = kPresEngine;
    t.pres_gameplan = kPresGameplan;
    t.pres_service = 492u;       // vf +32 `addi r3,r3,0x1EC`
    t.pres_root = 500u;          // service +8
    t.pres_param_records = 596u; // params (+592) +4
    t.pres_param_count = 600u;   // params +8, slot 1 `lwz r3,8(r3)`
    t.pres_param_size_a = 608u;  // params +16, the dtor's free size
    t.pres_param_size_b = 612u;  // params +20
    t.pres_slide_records = 628u; // Mgr:Slides (vf +40 `addi r3,r3,0x26C`) +8, read by vf +4 / +8
    t.pres_slot_dtor = 0u;       // `sub_82BAFD90`(this, flags)
    t.pres_slot_update = 4u;     // `sub_822AC880`(this, clock)
    t.pres_slot_nullsub = 68u;   // nullsub_1, `blr`
    t.pres_slot_set_name = 72u;  // `sub_82BB0790`(this, char*)
    t.gameplan_slot_release = 12u;  // destroy's `(+32)->vf12` 0x82bba924
    // CGameface: the clock object (+8, vf +16 = ms), the presentation callback (+588, ctx +592).
    t.gameface_clock = 8u;
    t.clock_slot_ms = 16u;
    t.gameface_callback = 588u;
    t.gameface_callback_ctx = 592u;
    // CScene: 16 slots; +16 `sub_821858F8` resets the render lists, +28 `lwz r3,0x6F0(r3)`.
    t.scene_vtable = kSceneVtable;
    t.scene_slots = 16u;
    t.scene_slot_refresh = 4u;
    t.scene_slot_list = 7u;
    t.scene_render_list = 0x6F0u;
    t.name_hash = kNameHash;
    t.attribute_id_mask = kAttributeIdMask;
    t.parameters_name = kParametersName;
    t.char_string_from_cstr = kCharStringFromCStr;
    t.char_string_dtor = kCharStringDtor;
    t.attr_start_time = kAttrStartTime;
    t.attr_end_time = kAttrEndTime;
    t.quad_record_ctor = kQuadRecordCtor;
    t.quad_record_size = kQuadRecordSize;
    return t;
}

/// Fable 2's Gameface for the core: built on first use, once the payload has stated its build.
const refii::headless::GamefaceTable& Table() {
    static const refii::headless::GamefaceTable table = BuildTable();
    return table;
}

/// A wide string handle's units (`rep = *handle`, `*rep` the units, `rep+4` the length — the
/// layout a text record holds at +140).
std::vector<uint16_t> WideUnits(uint8_t* base, uint32_t handle) {
    std::vector<uint16_t> units;
    const uint32_t rep = Plausible(handle) ? LoadGuestU32(base, handle) : 0u;
    if (!Plausible(rep)) {
        return units;
    }
    const uint32_t data = LoadGuestU32(base, rep);
    const uint32_t length = std::min(LoadGuestU32(base, rep + 4u), kMaxTextUnits);
    if (!Plausible(data)) {
        return units;
    }
    units.reserve(length);
    for (uint32_t i = 0; i < length; ++i) {
        units.push_back(LoadGuestU16(base, data + i * 2u));
    }
    return units;
}

std::atomic<uint64_t> g_passes{0};
std::atomic<uint64_t> g_texts{0};
std::atomic<uint64_t> g_absent_writes{0};
std::atomic<uint64_t> g_inert_answers{0};
std::atomic<uint64_t> g_textures_named{0};
std::atomic<uint64_t> g_stated_writes{0};

/// Inside the expression wheel's SetItem (rex_lhGuiExpressionWheel_SetItem), which states the item
/// itself: its title's SetTextFromTag and its texture write are not told to the elements.
thread_local bool t_wheel_item = false;

/// Inside an entry that states its own element values (rex_lhGuiPauseRootScreen_SetMapKey,
/// rex_lhGuiPickBox_SetIndex, rex_lhGuiLogbookScreen_FillExpression): its SetText /
/// SetTextFromTag / SetTexture are not told to the elements; SetText's string is kept for the
/// entry's statement.
thread_local bool t_entry_states = false;
thread_local std::vector<uint16_t> t_entry_text;

void StateText(uint32_t element, const std::vector<uint16_t>& units) {
    if (!refii::headless::TellText(element, units)) {
        return;
    }
    const uint64_t n = ++g_texts;
    if (Milestone(n)) {
        REXLOG_INFO("Refii headless presentation: text of {} unit(s) told to element 0x{:08X} (#{})", units.size(),
                    element, n);
    }
}

/// The ctx a midasm's register belongs to: generated code passes `ctx.rN` (codegen
/// builders/context.cpp `BuilderContext::r`), and r3 is PPCContext's first member.
PPCContext& ContextOf(PPCRegister& r3) {
    static_assert(offsetof(PPCContext, r3) == 0u, "r3 is PPCContext's first member");
    return reinterpret_cast<PPCContext&>(r3);
}

/// A text tag's string as the guest resolves it (SetTextFromTag TU1 0x82425F40 / disc 0x823F83D8),
/// on a scratch frame below sp: copy (`sub_821F0240`), lookup (`sub_8230E0E8`, which frees the
/// copy), then the wide string's dtor (`LHString_CCharString_CCharString_0`).
std::vector<uint16_t> TagText(PPCContext& ctx, uint8_t* base, uint32_t tag) {
    const uint32_t frame = (ctx.r1.u32 - 0x100u) & ~0xFu;
    const uint32_t copy = frame + 0x80u;
    const uint32_t text = frame + 0x88u;
    StoreGuestU32(base, copy, 0u);
    StoreGuestU32(base, text, 0u);
    const uint32_t copied = CallAt(ctx, base, frame, kCharStringCopy, copy, tag);
    CallAt(ctx, base, frame, kTextLookup, text, copied);
    std::vector<uint16_t> units = WideUnits(base, text);
    CallAt(ctx, base, frame, kWideStringDtor, text);
    return units;
}

}  // namespace

namespace refii::vfs {

// gui_stated_screens.cpp: the screen id being filed on this thread, -1 for none.
int32_t StatedScreenLoading();

bool HeadlessElementKey(uint32_t element, uint32_t* presentation, uint32_t* serial, uint32_t* id) {
    return refii::headless::ElementKey(element, presentation, serial, id);
}

}  // namespace refii::vfs

/// CGameface vtable +28 — (r3 gameface, r4 path) → a headless presentation, or 0.
REX_EXTERN(rex_CGameface_CreatePresentation) {
    REX_FUNC_PROLOGUE();
    const uint32_t made = refii::headless::Create(Table(), ctx, base, ctx.r3.u32, ctx.r4.u32);
    // Made while the screen set files a screen (gui_stated_screens.cpp): that screen's.
    const int32_t screen = refii::vfs::StatedScreenLoading();
    if (made != 0u && screen >= 0) {
        refii_bridge_told_headless_screen(made, static_cast<uint32_t>(screen));
    }
    ctx.r3.u32 = made;
}

/// CGameface vtable +32 — (r3 gameface, r4 presentation).
REX_EXTERN(rex_lhItemManager_NotifyAndRemoveItem) {
    REX_FUNC_PROLOGUE();
    if (!refii::headless::Destroy(Table(), ctx, base, ctx.r3.u32, ctx.r4.u32)) {
        __imp__rex_lhItemManager_NotifyAndRemoveItem(ctx, base);
        return;
    }
    ctx.r3.u32 = 0u;
}

/// CGameface's destructor (TU1 `sub_82BBA2C8`, disc `sub_82BB3BC8`), (r3 gameface) only: the plain
/// destructor, without a delete flag. An in-game reload deletes the
/// CGameface, and the guest only unloads some screens first: the HUD holder (`sub_823C9FB8`) just
/// zeroes its pointer, leaving the presentation to the kernel list this destructor frees (+40).
REX_EXTERN(rex_CGameface_Destructor) {
    REX_FUNC_PROLOGUE();
    const uint32_t gameface = ctx.r3.u32;
    refii::headless::DestroyRemaining(Table(), ctx, base, gameface);
    refii_bridge_stated_screen(0xFFFFFFFFu);  // no Gameface, no screen up
    refii_bridge_stated_screens_up(0u);
    ctx.r3.u32 = gameface;
    __imp__rex_CGameface_Destructor(ctx, base);
}

/// CPresentation vf +92 (TU1 `sub_82BB0858`, disc `sub_82BAA110`): `stb r4,0x368(r3); blr`, the
/// scene-draw gate. Non-zero parks the presentation. The constructor's zero is the only other
/// write of the byte.
REX_EXTERN(rex_CPresentation_SetDrawParked) {
    REX_FUNC_PROLOGUE();
    refii_bridge_told_headless_parked(ctx.r3.u32, 0u, ctx.r4.u32 & 0xFFu);
    __imp__rex_CPresentation_SetDrawParked(ctx, base);
}

/// CPresentation vf +100 (TU1 `sub_82BB0860`, disc `sub_82BAA118`): `stb r4,0x369(r3); blr`, the
/// per-frame update gate.
REX_EXTERN(rex_CPresentation_SetUpdateParked) {
    REX_FUNC_PROLOGUE();
    refii_bridge_told_headless_parked(ctx.r3.u32, 1u, ctx.r4.u32 & 0xFFu);
    __imp__rex_CPresentation_SetUpdateParked(ctx, base);
}

/// Element service vf +84 — (r3 service, r4 name hash, r5 parent or 0). The original answers;
/// a headless presentation makes the element it lacks.
REX_EXTERN(rex_lhGuiAttributeService_ChildByName) {
    REX_FUNC_PROLOGUE();
    const uint32_t service = ctx.r3.u32;
    const uint32_t hash = ctx.r4.u32;
    const uint32_t parent = ctx.r5.u32;
    __imp__rex_lhGuiAttributeService_ChildByName(ctx, base);
    if (ctx.r3.u32 != 0u) {
        return;
    }
    const uint32_t element = refii::headless::MakeNamed(Table(), ctx, base, service, hash, parent);
    if (element != 0u) {
        ctx.r3.u32 = element;
    }
}

/// Element service vf +92 — (r3 service, r4 parent, r5 the out array, r6 every child or only those
/// vf +4 passes): a headless parent's file children are made first.
REX_EXTERN(rex_lhGuiAttributeService_CollectChildren) {
    REX_FUNC_PROLOGUE();
    refii::headless::ExpandBeforeWalk(Table(), ctx, base, ctx.r3.u32, ctx.r4.u32);
    __imp__rex_lhGuiAttributeService_CollectChildren(ctx, base);
}

/// GuiElement_AttributeString_p (TU1 `sub_82425038`, disc `sub_823F74D0`) — (r3 the out
/// CCharString, r4 element, r5 presentation, r6 the attribute's name as a CCharString): the
/// string attribute whose id is the name's hash in 27 bits, "" when the element has none. A
/// headless element carries no string attribute, so its answer is the FILE's, constructed by the
/// guest's own CCharString ctor in place of the empty one — how every typed registry's file
/// (`GuiRegistry_File` 0x82846148, vf +12 the attribute: MeterName, TextName, SlideToggleName, …)
/// binds a headless HUD element to its name.
REX_EXTERN(rex_GuiElement_GetAttributeString) {
    REX_FUNC_PROLOGUE();
    const uint32_t out = ctx.r3.u32;
    const uint32_t element = ctx.r4.u32;
    const uint32_t presentation = ctx.r5.u32;
    const uint32_t name = ctx.r6.u32;
    __imp__rex_GuiElement_GetAttributeString(ctx, base);
    if (refii::headless::AnswerAttributeString(Table(), ctx, base, out, element, presentation, name)) {
        ctx.r3.u32 = out;
    }
}

namespace {

/// THE HUD REGISTRIES' NAMES, counted. The GUI's typed registries (`*(*(GUI+32)+32)` +28..+32,
/// `sub_827F0F00`) each hold a map at +4 whose size is +12; the filer (`sub_827F1880`) makes a
/// name per bound element, a screen's unfile (`sub_827F1A48`) erases the names its elements
/// carry, and every writer and read-back (`sub_822A9C60`) answers nothing for a name the map does
/// not hold. Logged when any count changes, so a run says when names leave and how many.
void CensusHudRegistries(uint8_t* base) {
    static std::mutex m;
    static std::vector<uint32_t> said;
    const uint32_t gui = LoadGuestU32(base, refii::guest::Address(0x83496B08u));
    const uint32_t hud = Plausible(gui) ? LoadGuestU32(base, gui + 32u) : 0u;
    const uint32_t set = Plausible(hud) ? LoadGuestU32(base, hud + 32u) : 0u;
    if (!Plausible(set)) {
        return;
    }
    const uint32_t begin = LoadGuestU32(base, set + 28u);
    const uint32_t end = LoadGuestU32(base, set + 32u);
    if (!Plausible(begin) || end < begin || (end - begin) > 64u * 4u) {
        return;
    }
    std::vector<uint32_t> counts;
    for (uint32_t at = begin; at < end; at += 4u) {
        const uint32_t registry = LoadGuestU32(base, at);
        counts.push_back(Plausible(registry) ? LoadGuestU32(base, registry + 12u) : 0xFFFFFFFFu);
    }
    std::string was;
    {
        std::lock_guard<std::mutex> lock(m);
        if (counts == said) {
            return;
        }
        for (const uint32_t n : said) {
            was += std::to_string(n) + ' ';
        }
        said = counts;
    }
    std::string now;
    for (const uint32_t n : counts) {
        now += std::to_string(n) + ' ';
    }
    REXLOG_INFO("Refii HUD registries 0x{:08X}: names per registry [ {}] (was [ {}])", set, now, was);
}

}  // namespace

/// CGameface vtable +40 — the per-frame pass; then this gameface's headless presentations'
/// Update(pres, clock), as the pass's own loop (0x822be8e8..0x822be930).
REX_EXTERN(rex_CGameface_Update) {
    REX_FUNC_PROLOGUE();
    const uint32_t gameface = ctx.r3.u32;
    __imp__rex_CGameface_Update(ctx, base);
    int32_t clock = 0;
    const size_t updated = refii::headless::UpdateAll(Table(), ctx, base, gameface, &clock);
    if (updated == 0u) {
        return;
    }
    const uint64_t passes = ++g_passes;
    if ((passes % 30u) == 0u) {
        CensusHudRegistries(base);
    }
    if (Milestone(passes) && passes < 4096u) {
        REXLOG_INFO("Refii headless presentation: gameface 0x{:08X} pass #{} updated {} headless presentation(s) "
                    "at clock {} ms",
                    gameface, passes, updated, clock);
    }
}

/// lhGuiPresentation_GetRootElement (TU1 `sub_82426518`, disc `sub_823F89B0`): a headless root
/// leaves with its timeline time.
REX_EXTERN(rex_lhGuiPresentation_GetRootElement) {
    REX_FUNC_PROLOGUE();
    __imp__rex_lhGuiPresentation_GetRootElement(ctx, base);
    refii::headless::NoteRootRead(Table(), ctx, base, ctx.r3.u32);
}

// SetStringParam (TU1 `sub_826B9510`, disc `sub_8259D6C8`) assigns the string at the attribute
// lookup's out word (`lwz r3,0x54(r1)`, `bl sub_82BAEF98` 0x826b95fc) whether or not the lookup
// (service vf +12, `sub_82BB52E0`) found the attribute — it writes that word only when it does.
// Before `lwz r11,0(r28)` (0x826b95e0, disc 0x8259d798), r3 = found: true jumps to the epilogue
// (0x826b9600, disc 0x8259d7b8). A headless element carries no string attribute.
bool RefiiPresentationAttributeAbsent(PPCRegister& r3) {
    if ((r3.u32 & 0xFFu) != 0u) {
        return false;
    }
    const uint64_t n = ++g_absent_writes;
    if (Milestone(n)) {
        REXLOG_INFO("Refii headless presentation: SetStringParam onto an absent attribute skipped (#{})", n);
    }
    return true;
}

// SetText's entry (TU1 0x82425DD0, disc 0x823F8268): r3 element, r4 presentation, r5 the text's
// handle (`mr r4,r5` 0x82425e18 into the record's assign).
void RefiiHeadlessSetText(PPCRegister& r3, PPCRegister& r4, PPCRegister& r5) {
    (void)r4;
    uint8_t* base = RefiiGuestBase();
    if (t_entry_states) {
        if (base != nullptr && r5.u32 != 0u) {
            t_entry_text = WideUnits(base, r5.u32);
        }
        return;
    }
    uint32_t presentation = 0;
    uint32_t serial = 0;
    uint32_t id = 0;
    if (base == nullptr || !Plausible(r3.u32) || r5.u32 == 0u ||
        !refii::headless::ElementKey(r3.u32, &presentation, &serial, &id)) {
        return;
    }
    StateText(r3.u32, WideUnits(base, r5.u32));
}

// SetTextFromTag's entry (TU1 0x82425F40, disc 0x823F83D8): r3 element, r4 the tag. The text is
// the guest's own lookup of the tag, on a scratch frame below the caller's sp (the writer's
// prologue has not run): copy (`sub_821F0240`), lookup (`sub_8230E0E8`, which frees the copy),
// then the wide string's dtor (`LHString_CCharString_CCharString_0`).
void RefiiHeadlessSetTextFromTag(PPCRegister& r3, PPCRegister& r4) {
    uint8_t* base = RefiiGuestBase();
    uint32_t presentation = 0;
    uint32_t serial = 0;
    uint32_t id = 0;
    if (t_wheel_item || t_entry_states || base == nullptr || !Plausible(r3.u32) || !Plausible(r4.u32) ||
        !refii::headless::ElementKey(r3.u32, &presentation, &serial, &id)) {
        return;
    }
    StateText(r3.u32, TagText(ContextOf(r3), base, r4.u32));
}

// The quad readers' joins: `r` is the record a kind-2 node gave, 0 when the element has none — a
// headless element — answered with the core's inert record (identity transforms, no render index:
// +544 and +560 = -1, so `sub_8286DBC8` queues nothing). The sixteen readers in each build that
// take a kind-2 node's record, and the seven that use it unchecked (the transform copies
// `sub_823D0D20` in the pause minimap's rect `sub_826A3D98` 0x826a3e28 / 0x826a3e58 and its
// projection `sub_826A3FD8` 0x826a404c, the HUD minimap `sub_826DCAA0` 0x826dcb40, CGameModeBase
// vf6 `sub_826E42C8` 0x826e43ac; the transform getter `sub_823D0E10` 0x823d0ee4; the HUD icon table
// `sub_826DC138` 0x826dc548; the notification icon's texture write `sub_8281F010` 0x8281f108) are
// listed in the configs.
void RefiiHeadlessQuadRecord(PPCRegister& r) {
    if (r.u32 != 0u) {
        return;
    }
    const uint32_t record = refii::headless::InertQuadRecord();
    if (record == 0u) {
        return;
    }
    r.u32 = record;
    const uint64_t n = ++g_inert_answers;
    if (Milestone(n) && n < 4096u) {
        REXLOG_INFO("Refii headless presentation: a quad reader took the inert record (#{})", n);
    }
}

namespace {

/// One attribute write stated for a headless element from a setter's arguments (only when the
/// element had no room to carry it — see EXISTENCE-CHECKED WRITES).
void StateAttribute(uint8_t* base, uint32_t element, uint32_t attribute_global, float value) {
    uint32_t bits = 0;
    std::memcpy(&bits, &value, sizeof bits);
    if (refii::headless::TellAttribute(element, LoadGuestU32(base, attribute_global), bits)) {
        const uint64_t n = ++g_stated_writes;
        if (Milestone(n)) {
            REXLOG_INFO("Refii headless presentation: element 0x{:08X} had no room for attribute 0x{:07X}; the "
                        "write is stated from the setter's arguments (#{})",
                        element, LoadGuestU32(base, attribute_global), n);
        }
    }
}

/// An existence-checked setter's entry: a fileless headless element is given the attributes the
/// setter names, so its own write lands and the store states it (kNoRoom when it has no room).
refii::headless::Memory Remember(uint8_t* base, uint32_t element, const refii::guest::Addr (&globals)[3],
                                 const float (&values)[3], size_t count) {
    uint32_t ids[3] = {};
    for (size_t i = 0; i < count; ++i) {
        ids[i] = LoadGuestU32(base, globals[i]) & kAttributeIdMask;
    }
    return refii::headless::RememberWrite(base, element, ids, values, count);
}

/// Remember, and with no room the write is stated here from the setter's arguments.
void RememberOrState(uint32_t element, const refii::guest::Addr (&globals)[3], const float (&values)[3],
                     size_t count) {
    uint8_t* base = RefiiGuestBase();
    if (base == nullptr || !Plausible(element)) {
        return;
    }
    if (Remember(base, element, globals, values, count) != refii::headless::Memory::kNoRoom) {
        return;
    }
    for (size_t i = 0; i < count; ++i) {
        StateAttribute(base, element, globals[i], values[i]);
    }
}

}  // namespace

// SetPosition's entry (TU1 0x824251E0, disc 0x823F7678): r3 element, r4 presentation, f1..f3.
void RefiiHeadlessSetPosition(PPCRegister& r3, PPCRegister& r4, PPCRegister& f1, PPCRegister& f2, PPCRegister& f3) {
    if (r4.u32 == 0u) {
        return;
    }
    static const refii::guest::Addr kIds[3] = {kAttrPositionX, kAttrPositionY, kAttrPositionZ};
    const float values[3] = {static_cast<float>(f1.f64), static_cast<float>(f2.f64), static_cast<float>(f3.f64)};
    RememberOrState(r3.u32, kIds, values, 3u);
}

// SetRotation's entry (TU1 0x824254C8, disc 0x823F7960): r3 element, r4 presentation, f1..f3.
void RefiiHeadlessSetRotation(PPCRegister& r3, PPCRegister& r4, PPCRegister& f1, PPCRegister& f2, PPCRegister& f3) {
    if (r4.u32 == 0u) {
        return;
    }
    static const refii::guest::Addr kIds[3] = {kAttrRotationX, kAttrRotationY, kAttrRotationZ};
    const float values[3] = {static_cast<float>(f1.f64), static_cast<float>(f2.f64), static_cast<float>(f3.f64)};
    RememberOrState(r3.u32, kIds, values, 3u);
}

// SetOpacity's entry (TU1 0x82426078, disc 0x823F8510): r3 element, r4 presentation, f1. Nothing is
// stated here: the value SetOpacity stores — f1, x1024 (0x824260FC / 0x823F8594) when the current
// opacity is below 0.09765625 — reaches the host through the store it calls, SetElementAttribute
// (vf +0x48 at 0x82426120 / 0x823F85B8). With no room the element holds no opacity, the current read
// (0x824260D0 / 0x823F8568) fails and the setter returns at 0x824260DC / 0x823F8574: it stores nothing.
void RefiiHeadlessSetOpacity(PPCRegister& r3, PPCRegister& r4, PPCRegister& f1) {
    uint8_t* base = RefiiGuestBase();
    if (r4.u32 == 0u || base == nullptr || !Plausible(r3.u32)) {
        return;
    }
    static const refii::guest::Addr kIds[3] = {kAttrOpacity, kAttrOpacity, kAttrOpacity};
    const float values[3] = {static_cast<float>(f1.f64), 0.0f, 0.0f};
    Remember(base, r3.u32, kIds, values, 1u);
}

// SetActive (TU1 `sub_82284210`, disc `sub_82283D80`): r3 element, r4 presentation, r5 the bool.
// It writes `active` (flags 0x2000, vf +80) only when the element's bit differs (vf +76,
// 0x82284268..0x82284274). A headless element's bit starts as the file's; the write the guest
// meant is stated at the entry from its argument whether or not it changes the bit.
void RefiiHeadlessSetActive(PPCRegister& r3, PPCRegister& r4, PPCRegister& r5) {
    if (!Plausible(r3.u32) || r4.u32 == 0u) {
        return;
    }
    refii::headless::TellActive(r3.u32, (r5.u32 & 0xFFu) != 0u);
}

namespace {

void StateTexture(uint32_t element, uint32_t hash) {
    if (Plausible(element)) {
        refii::headless::TellTexture(element, hash);
    }
}

}  // namespace

namespace {

/// The portrait the StampSaveCard call in flight on this thread stamped, 0 for none.
thread_local bool t_stamping_card = false;
thread_local uint32_t t_card_portrait = 0u;

std::atomic<uint64_t> g_cards_stated{0};
std::atomic<uint64_t> g_dialogs_stated{0};

/// A wide LHString handle's text (WideUnits) as UTF-8.
std::string Utf8(const std::vector<uint16_t>& units) {
    std::string text;
    text.reserve(units.size());
    for (size_t i = 0; i < units.size(); ++i) {
        uint32_t c = units[i];
        if (c == 0u) {
            break;
        }
        if (c >= 0xD800u && c < 0xDC00u && i + 1u < units.size() && units[i + 1u] >= 0xDC00u &&
            units[i + 1u] < 0xE000u) {
            c = 0x10000u + ((c - 0xD800u) << 10) + (units[++i] - 0xDC00u);
        }
        if (c < 0x80u) {
            text.push_back(static_cast<char>(c));
        } else if (c < 0x800u) {
            text.push_back(static_cast<char>(0xC0u | (c >> 6)));
            text.push_back(static_cast<char>(0x80u | (c & 0x3Fu)));
        } else if (c < 0x10000u) {
            text.push_back(static_cast<char>(0xE0u | (c >> 12)));
            text.push_back(static_cast<char>(0x80u | ((c >> 6) & 0x3Fu)));
            text.push_back(static_cast<char>(0x80u | (c & 0x3Fu)));
        } else {
            text.push_back(static_cast<char>(0xF0u | (c >> 18)));
            text.push_back(static_cast<char>(0x80u | ((c >> 12) & 0x3Fu)));
            text.push_back(static_cast<char>(0x80u | ((c >> 6) & 0x3Fu)));
            text.push_back(static_cast<char>(0x80u | (c & 0x3Fu)));
        }
    }
    return text;
}

std::string Hex(uint32_t value) {
    char hex[9];
    std::snprintf(hex, sizeof hex, "%08X", value);
    return hex;
}

void PostFrontEnd(const char* name, std::initializer_list<std::string> arguments) {
    refii::vfs::GuiNativeCall call;
    call.name = name;
    for (const std::string& value : arguments) {
        refii::vfs::GuiNativeArgument argument;
        argument.kind = refii::vfs::GuiNativeArgument::Kind::kString;
        argument.text = value;
        call.arguments.push_back(std::move(argument));
    }
    refii::vfs::PostGuiNativeCall(std::move(call));
}

}  // namespace

// A save's screenshot, inside StampSaveCard (TU1 `sub_826E1408` 0x826e14b0, disc `sub_826C9828`
// 0x826c98d0): r3 the card's element, r30 the hash `sub_824714E8` derived (0x826e1478).
void RefiiHeadlessStampTexture(PPCRegister& element, PPCRegister& hash) {
    if (t_stamping_card) {
        t_card_portrait = hash.u32;
    }
    StateTexture(element.u32, hash.u32);
}

// THE LOAD FAN'S CARDS. StampSaveCard (TU1 `sub_826E1408`, disc `sub_826C9828`): r3 the front-end
// root, r4 the save's index (into root+84 / disc root+72), r5 the user, r6 the card slot, the index
// into the card element names `dword_8349B5C8` (disc 0x8349B048). Its one caller is the dispatch's
// Load Game path, once per save 0 .. min(count, 6) - 1 with r6 = index + root+0x4C (TU1 0x826e0734
// .. 0x826e0748; disc root+0x40, 0x826c8b64 .. 0x826c8b78), right after it set every card 1..6 to
// the blank `dword_8349B600` (TU1 0x826e06ac .. 0x826e070c; disc `dword_8349B080`, 0x826c8adc ..
// 0x826c8b3c). It stamps the save's screenshot only for a loadable record (`+24`, 0x826e1448 / disc
// 0x826c9868) with a nonzero hash (0x826e1480 / 0x826c98a0). Stated after the call as
// `@FrontEnd.SaveCard` (index, slot, texture hash): the stamped portrait, else the blank.
PPC_EXTERN_FUNC(__imp__rex_lhGui_FrontendRoot_StampSaveCard);
REX_EXTERN(rex_lhGui_FrontendRoot_StampSaveCard) {
    REX_FUNC_PROLOGUE();
    const uint32_t index = ctx.r4.u32;
    const uint32_t slot = ctx.r6.u32;
    t_stamping_card = true;
    t_card_portrait = 0u;
    __imp__rex_lhGui_FrontendRoot_StampSaveCard(ctx, base);
    t_stamping_card = false;
    refii::vfs::FrontEndCardStamped();
    const uint32_t texture = t_card_portrait != 0u ? t_card_portrait : LoadGuestU32(base, kCardBlankTexture);
    PostFrontEnd("@FrontEnd.SaveCard", {std::to_string(index), std::to_string(slot), Hex(texture)});
    const uint64_t n = ++g_cards_stated;
    if (Milestone(n)) {
        REXLOG_INFO("Refii front end: save {} stated on card {}, texture 0x{:08X}{} (#{})", index, slot, texture,
                    t_card_portrait != 0u ? "" : " (blank)", n);
    }
}

// THE CO-OP SAVE PICKER'S FAN. Its opener (TU1 `sub_8243B070`, disc `sub_8247C328`; one caller, the
// co-op screen's input handler `sub_82438CF8` 0x82439430): r3 the screen. With a profile (+292 != -1)
// and a save listed (+404..+408, 0x8243b0a0) it builds the cards' texts (`sub_8243B678`), sets +396,
// the card the first save sits on, by the count: 1-2 saves card 3, 3-4 card 2, else card 1
// (0x8243b1fc..0x8243b224, disc 0x8247c4b4..0x8247c4dc), and stamps cards 1..6 through `sub_8243B520`
// / disc `sub_8247C7D8` (save k - first's screenshot, else the blank; 0x8243b2b4 / 0x8243b2d4, disc
// 0x8247c56c / 0x8247c58c — each portrait stated by RefiiHeadlessInlineTexture). It sets the cursor
// (+392, 0x8243b318 / disc 0x8247c5d0) and shows the picker (`sub_8243B440(screen, 1)` 0x8243b370,
// disc 0x8247c628). Stated after the call as `@CoopPicker.Open` (screen, first card, saves, cursor, a bit
// per save whose header parsed — `*(+452) + 40i + 24`, A is refused on the others at 0x82438db4 —
// then each card's text as `sub_8243B678` built it into +436): the opener's own products, taken when
// it returned; nothing is stated when its gate turned it away.
PPC_EXTERN_FUNC(__imp__rex_lhGuiCoopScreen_OpenSavePicker);
REX_EXTERN(rex_lhGuiCoopScreen_OpenSavePicker) {
    REX_FUNC_PROLOGUE();
    const uint32_t screen = ctx.r3.u32;
    __imp__rex_lhGuiCoopScreen_OpenSavePicker(ctx, base);
    if (!Plausible(screen) || LoadGuestU32(base, screen + 292u) == 0xFFFFFFFFu) {
        return;
    }
    const uint32_t saves = (LoadGuestU32(base, screen + 408u) - LoadGuestU32(base, screen + 404u)) / 4u;
    const uint32_t first = LoadGuestU32(base, screen + 396u);
    if (saves == 0u || saves > 4096u) {
        return;
    }
    const uint32_t cursor = LoadGuestU32(base, screen + 392u);
    const uint32_t headers = LoadGuestU32(base, screen + 452u);
    uint32_t valid = 0u;
    for (uint32_t i = 0; i < saves && i < 32u && Plausible(headers); ++i) {
        if (base[headers + (40u * i) + 24u] != 0u) valid |= 1u << i;
    }
    const uint32_t texts = LoadGuestU32(base, screen + 436u);
    const uint32_t texts_end = LoadGuestU32(base, screen + 440u);
    const uint32_t described = Plausible(texts) && texts_end >= texts ? (texts_end - texts) / 4u : 0u;

    refii::vfs::GuiNativeCall call;
    call.name = "@CoopPicker.Open";
    const auto add = [&call](std::string value) {
        refii::vfs::GuiNativeArgument argument;
        argument.kind = refii::vfs::GuiNativeArgument::Kind::kString;
        argument.text = std::move(value);
        call.arguments.push_back(std::move(argument));
    };
    add(Hex(screen));
    add(std::to_string(first));
    add(std::to_string(saves));
    add(std::to_string(cursor));
    add(std::to_string(valid));
    for (uint32_t i = 0; i < saves && i < 32u; ++i) {
        add(i < described ? Utf8(WideUnits(base, texts + (i * 4u))) : std::string());
    }
    refii::vfs::PostGuiNativeCall(std::move(call));
    REXLOG_INFO("Refii co-op picker: opened by 0x{:08X}, {} save(s) from card {}, cursor {}", screen, saves,
                first, cursor);
}

// THE FRONT END'S DIALOG. OpenDialog (TU1 `sub_826E40E0`, disc `sub_826CB158`) is vtable slot
// +0x20 of the title (0x820D09C4 / disc 0x820CD110) and, through `sub_826E1C50` 0x826e1c6c (disc
// `sub_826CA068` 0x826ca084), of the main-menu root (0x820D0934 / disc 0x820CD524): r3 the owner,
// r4 the OK text -> `dialog.Group.OK` (0x826e4158 / 0x826cb1cc), r5 the body ->
// `dialog.Group.yousure` (0x826e4180 / 0x826cb1f4), r6 allow-cancel (`clrlwi r11,r28,24`
// 0x826e41d4 / 0x826cb248). Set: `dialog.Group.cancel` = tag "GUI_BUTTONS_CANCEL" through
// `sub_8230E0E8` (0x826e41e0 .. 0x826e4210 / 0x826cb254 .. 0x826cb284) and
// `dialog.Group.icon_button_y` = `dword_8349B694` "art\gui\persistant\abyx.tex" (0x826e4250 /
// disc `dword_8349B0EC` 0x826cb2c4). Clear: the empty wide string (0x826e4268 / 0x826cb2dc) and
// `dword_8349B690` "art\gui\frames\blank.tex" (0x826e42b8 / disc `dword_8349B0E8` 0x826cb32c).
// Stated after the call as `@FrontEnd.Dialog` (owner, OK, body, allow-cancel, cancel tag, glyph
// hash); the texts are read before it.
PPC_EXTERN_FUNC(__imp__rex_lhGui_FrontendRoot_OpenDialog);
REX_EXTERN(rex_lhGui_FrontendRoot_OpenDialog) {
    REX_FUNC_PROLOGUE();
    const uint32_t owner = ctx.r3.u32;
    const std::string ok = Utf8(WideUnits(base, ctx.r4.u32));
    const std::string body = Utf8(WideUnits(base, ctx.r5.u32));
    const bool allow_cancel = (ctx.r6.u32 & 0xFFu) != 0u;
    refii::vfs::FrontEndDialogOpening();
    __imp__rex_lhGui_FrontendRoot_OpenDialog(ctx, base);
    refii::vfs::FrontEndDialogOpened(owner);
    const uint32_t glyph = LoadGuestU32(base, allow_cancel ? kGlyphCancelTexture : kGlyphOtherTexture);
    PostFrontEnd("@FrontEnd.Dialog", {Hex(owner), ok, body, allow_cancel ? "1" : "0",
                                      allow_cancel ? "GUI_BUTTONS_CANCEL" : "", Hex(glyph)});
    const uint64_t n = ++g_dialogs_stated;
    if (Milestone(n)) {
        REXLOG_INFO("Refii front end: dialog stated by 0x{:08X}, {} cancel, glyph 0x{:08X}, body '{}' (#{})", owner,
                    allow_cancel ? "with" : "no", glyph, body, n);
    }
}

namespace {

/// The resource hash the texture setter stores for the CCharString at `name` (`sub_8286DDC0`
/// 0x8286de1c..0x8286de3c: copy, GetAsLowerCase, ComputeHash), on a scratch frame below sp.
uint32_t TextureHashOfName(PPCContext& ctx, uint8_t* base, uint32_t name) {
    const uint32_t frame = (ctx.r1.u32 - 0x100u) & ~0xFu;
    const uint32_t copy = frame + 0x80u;
    const uint32_t lower = frame + 0x88u;
    StoreGuestU32(base, copy, 0u);
    StoreGuestU32(base, lower, 0u);
    CallAt(ctx, base, frame, kCharStringCopy, copy, name);
    CallAt(ctx, base, frame, kCharStringLower, lower, copy);
    const uint32_t hash = CallAt(ctx, base, frame, kResourceNameHash, lower);
    CallAt(ctx, base, frame, kCharStringDtor, lower);
    CallAt(ctx, base, frame, kCharStringDtor, copy);
    return hash;
}

/// A texture named onto a headless element, stated as the hash its quad would carry.
void StateNamedTexture(PPCContext& ctx, uint8_t* base, uint32_t element, uint32_t name) {
    uint32_t presentation = 0;
    uint32_t serial = 0;
    uint32_t id = 0;
    if (base == nullptr || !Plausible(element) || !Plausible(name) ||
        !refii::headless::ElementKey(element, &presentation, &serial, &id)) {
        return;
    }
    StateTexture(element, TextureHashOfName(ctx, base, name));
    const uint64_t n = ++g_textures_named;
    if (Milestone(n)) {
        REXLOG_INFO("Refii headless presentation: texture named onto element {} of 0x{:08X} (#{})", id, presentation,
                    n);
    }
}

/// The three record-level sites name their texture in a CCharString at sp+0x50.
constexpr uint32_t kStackName = 0x50u;

}  // namespace

// GuiElement_SetTexture's entry (TU1 0x82425BB8, disc 0x823F8050): r3 the name (CCharString*),
// r4 the element; it bails on the element's missing node (0x82425bc4..cc), so the texture is
// stated here — the world icons' art (`sub_82825358` 0x828253d4).
void RefiiHeadlessSetTextureByName(PPCRegister& r3, PPCRegister& r4) {
    if (t_entry_states) {
        return;
    }
    StateNamedTexture(ContextOf(r3), RefiiGuestBase(), r4.u32, r3.u32);
}

namespace {

std::atomic<uint64_t> g_wheel_items_stated{0};

/// A CCharString's bytes (`rep = *handle`, `*rep` the chars, `rep+4` the length).
std::string CharStringText(uint8_t* base, uint32_t handle) {
    std::string text;
    const uint32_t rep = Plausible(handle) ? LoadGuestU32(base, handle) : 0u;
    const uint32_t data = Plausible(rep) ? LoadGuestU32(base, rep) : 0u;
    if (!Plausible(data)) {
        return text;
    }
    const uint32_t length = std::min(LoadGuestU32(base, rep + 4u), kMaxTextUnits);
    text.reserve(length);
    for (uint32_t i = 0; i < length; ++i) {
        const char c = static_cast<char>(base[data + i]);
        if (c == '\0') {
            break;
        }
        text.push_back(c);
    }
    return text;
}

}  // namespace

// THE EXPRESSION WHEEL'S ITEM. SetItem (TU1 `sub_826AC5F0`, disc `sub_8258FD30`): r3 the wheel, r4
// the ring's element path (CCharString: `dword_8349AD28` "wheelmenu.icons.icons.menu.item" from
// `sub_826AB1F8`, `unk_8349AD2C` "...submenu.item" from `sub_826ABCD0`; disc `sub_8258E938` /
// `sub_8258F410`), r5 the texture's name (CCharString, `<name>.tex`), r6 the title's tag
// (CCharString), r7 the item slot (0..7: 6 and 7 the fixed slots, `li r7,6` 0x826ab628, `li r7,7`
// 0x826ab7cc / 0x826ac5a4; disc 0x8258ed68, 0x8258ef0c, 0x8258fce4). It resolves `<path><slot>`
// (`sub_8234ECC0` "%d", 0x826ac610) and under it `expressions` and `Title`
// (`GuiElement_Resolve_p` 0x826ac69c / 0x826ac6e4), sets the title from the tag
// (`lhGuiElement_SetTextFromTag` 0x826ac704 / disc 0x8258fe44) and the texture on `expressions`
// (`sub_8286DDC0` 0x826ac724 / disc `sub_82814D30` 0x8258fe64). Stated before the call as
// `@ExpressionWheel.Item` (path, slot, title, texture hash, tag): the title is the guest's lookup
// of the tag, the hash the one the texture write stores (TextureHashOfName).
PPC_EXTERN_FUNC(__imp__rex_lhGuiExpressionWheel_SetItem);
REX_EXTERN(rex_lhGuiExpressionWheel_SetItem) {
    REX_FUNC_PROLOGUE();
    const std::string path = CharStringText(base, ctx.r4.u32);
    const uint32_t slot = ctx.r7.u32;
    const std::string tag = CharStringText(base, ctx.r6.u32);
    const std::string title = Plausible(ctx.r6.u32) ? Utf8(TagText(ctx, base, ctx.r6.u32)) : std::string();
    const uint32_t texture = Plausible(ctx.r5.u32) ? TextureHashOfName(ctx, base, ctx.r5.u32) : 0u;
    PostFrontEnd("@ExpressionWheel.Item", {path, std::to_string(slot), title, Hex(texture), tag});
    t_wheel_item = true;
    __imp__rex_lhGuiExpressionWheel_SetItem(ctx, base);
    t_wheel_item = false;
    const uint64_t n = ++g_wheel_items_stated;
    if (Milestone(n)) {
        REXLOG_INFO("Refii expression wheel: item {} of '{}' stated, title '{}' ({}), texture 0x{:08X} (#{})", slot,
                    path, title, tag, texture, n);
    }
}

// The d-pad quick slots (`sub_823D2C98` 0x823D30D4, disc `sub_824069F0` 0x82406E2C): the slot
// element `sub_823D34B8` returned in r3, the name at sp+0x50.
void RefiiHeadlessTextureOnStack(PPCRegister& r3, PPCRegister& r1) {
    StateNamedTexture(ContextOf(r3), RefiiGuestBase(), r3.u32, r1.u32 + kStackName);
}

// The spell wheel's icon (`sub_826B5D30` 0x826B5E34 r31, disc `sub_8259AFC8` 0x8259B0CC) and the
// spell charges' registry walk (`sub_825E9EA0` 0x825EA1F4 r10, disc `sub_82405C60` 0x82405FB4):
// the element in a register, the name at sp+0x50.
void RefiiHeadlessTextureOnStackOf(PPCRegister& r3, PPCRegister& element, PPCRegister& r1) {
    StateNamedTexture(ContextOf(r3), RefiiGuestBase(), element.u32, r1.u32 + kStackName);
}

void RefiiGuiStateImage(uint32_t hash, uint32_t texture);

// The spell charges' icons (`sub_825E9EA0` 0x825EA1C8, disc `sub_82405C60` 0x82405F88): r4 the
// image name's hash, the texture's name at sp+0x50, just before the image registry's record
// lookup — stated as the call names them, whether or not the name has a record.
void RefiiHudChargeImage(PPCRegister& r3, PPCRegister& r4, PPCRegister& r1) {
    uint8_t* base = RefiiGuestBase();
    if (base == nullptr || !Plausible(r1.u32)) {
        return;
    }
    RefiiGuiStateImage(r4.u32, TextureHashOfName(ContextOf(r3), base, r1.u32 + kStackName));
}

namespace {

// The legend rows' element names (static inits `sub_8325BB70` / `sub_8325BBB0`; disc `unk_83497CC4`
// / `unk_83497CC8`) and the pickbox's text child (`sub_8326C250`; disc `unk_8349C140`), CCharStrings.
constexpr refii::guest::Addr kMapKeyName{0x8349AC0Cu};   // "Foreground.Pages.Contents.MiniMap.Legend.Fixed.KeyName"
constexpr refii::guest::Addr kMapKeyImage{0x8349AC10u};  // "...Legend.Fixed.KeyImage"
constexpr refii::guest::Addr kPickBoxText{0x8349C384u};  // ".grp.text"

std::atomic<uint64_t> g_map_keys_stated{0};
std::atomic<uint64_t> g_pickboxes_stated{0};
std::atomic<uint64_t> g_expressions_filled{0};

/// Each pickbox's text element path, as its Init named it.
std::mutex g_pickbox_mutex;
std::unordered_map<uint32_t, std::string> g_pickbox_paths;

// Each row widget's path, as its base Init was handed it (under g_pickbox_mutex).
std::unordered_map<uint32_t, std::string> g_widget_paths;
std::atomic<uint64_t> g_sliders_stated{0};

std::string WidgetPath(uint32_t widget) {
    std::lock_guard<std::mutex> lock(g_pickbox_mutex);
    const auto found = g_widget_paths.find(widget);
    return found != g_widget_paths.end() ? found->second : std::string();
}

/// Runs an entry whose own element writes are stated by the hook: their told statements are
/// dropped and SetText's string is returned.
template <typename Original>
std::string RunStating(PPCContext& ctx, uint8_t* base, Original original) {
    const bool outer = t_entry_states;
    t_entry_states = true;
    t_entry_text.clear();
    original(ctx, base);
    t_entry_states = outer;
    return Utf8(t_entry_text);
}

}  // namespace

// THE PAUSE MAP'S LEGEND ROW. SetMapKey (TU1 `sub_826A3AC8`, disc `sub_8240FE80`): r3 the pause
// root, r4 the presentation, r5 the row (1-based), r6 the label's tag (CCharString; the call
// destroys it, 0x826a3bd0), r7 the address of the icon's resource hash. Its one caller, the legend
// fill `sub_826A3658` (disc `sub_8240FA10`), names nine rows by literal tag and hash
// (`dword_8349AC14`..`dword_8349AC3C`, disc `dword_83497CCC`..`dword_83497CF4`) and advances the
// row only on a 1. It resolves `<KeyName><row>` and `<KeyImage><row>` (`sub_8234ECC0` "%d",
// 0x826a3afc / 0x826a3b1c) and, only with both: shows them (`sub_82426260` 0x826a3b48 /
// 0x826a3b88), sets the label from the tag (lookup `sub_8230E0E8` 0x826a3b60, SetText 0x826a3b70;
// disc 0x8240ff28) and stores *r7 as the image's texture (0x826a3bc0, disc 0x8240ff78), returning
// 1. The fill hides the rows past the last (0x826a3a10 / 0x826a3a84). Stated after the call as
// `@PauseMap.Key` (presentation, row, label path, image path, text, hash, tag) when it returned 1.
PPC_EXTERN_FUNC(__imp__rex_lhGuiPauseRootScreen_SetMapKey);
REX_EXTERN(rex_lhGuiPauseRootScreen_SetMapKey) {
    REX_FUNC_PROLOGUE();
    const uint32_t presentation = ctx.r4.u32;
    const uint32_t row = ctx.r5.u32;
    const std::string tag = CharStringText(base, ctx.r6.u32);
    const uint32_t hash = Plausible(ctx.r7.u32) ? LoadGuestU32(base, ctx.r7.u32) : 0u;
    const std::string text = RunStating(ctx, base, __imp__rex_lhGuiPauseRootScreen_SetMapKey);
    if ((ctx.r3.u32 & 0xFFu) == 0u) {
        return;
    }
    const std::string number = std::to_string(row);
    const std::string label = CharStringText(base, kMapKeyName) + number;
    const std::string image = CharStringText(base, kMapKeyImage) + number;
    PostFrontEnd("@PauseMap.Key", {Hex(presentation), number, label, image, text, Hex(hash), tag});
    const uint64_t n = ++g_map_keys_stated;
    if (Milestone(n)) {
        REXLOG_INFO("Refii pause legend: row {} stated '{}' ({}), icon 0x{:08X} (#{})", row, text, tag, hash, n);
    }
}

// THE PICKBOX'S ELEMENT. Init (TU1 `sub_82881FD0`, disc `sub_828860E8`; the pickbox vtable's slot
// 0, 0x820E0BD0): r3 the pickbox, r5 its path (CCharString). It empties the options (+48..+52),
// resolves `<path>` + ".grp.text" (`lhCharString_CtorConcat` 0x82882084, disc 0x8288619c) in the
// pickbox's presentation (+4) into +40 (0x82882090) and sets +12 (0x828820a4), returning 1. Its
// paths are globals: the co-op screen's `Foreground.RightPage.Pickbox.pickbox1..3` and the options
// pages'. Stated after the call as `@PickBox.Init` (pickbox, text path) when it returned 1; every
// SetIndex on the pickbox is stated under that path.
PPC_EXTERN_FUNC(__imp__rex_lhGuiPickBox_Init);
REX_EXTERN(rex_lhGuiPickBox_Init) {
    REX_FUNC_PROLOGUE();
    const uint32_t pickbox = ctx.r3.u32;
    const std::string path = CharStringText(base, ctx.r5.u32) + CharStringText(base, kPickBoxText);
    __imp__rex_lhGuiPickBox_Init(ctx, base);
    if ((ctx.r3.u32 & 0xFFu) == 0u) {
        return;
    }
    {
        std::lock_guard<std::mutex> lock(g_pickbox_mutex);
        g_pickbox_paths[pickbox] = path;
    }
    PostFrontEnd("@PickBox.Init", {Hex(pickbox), path});
}

// THE PICKBOX'S CHOICE. SetIndex (TU1 `sub_828821D0`, disc `sub_828862E8`): r3 the pickbox, r4 the
// index. With +12 set and options at +48..+52 it wraps the index (past the end to 0, below 0 to the
// last; 0x82882230..0x82882260), stores it at +36 (0x82882264) and hands option[index]
// (`add r5,r10,r11` 0x82882298) to SetText on the text element +40 (0x8288229c, disc 0x828863b4),
// returning 1. Callers: the co-op screen's henchman rows (`sub_82435FC0`, `sub_82436178`,
// `sub_82436360`, `sub_82436F38`, `sub_8243A8F0`), the options pages (`sub_829BE2C0` ..
// `sub_829C1370`) and its own left/right (`sub_828820B0`). Stated after the call as `@PickBox.Set`
// (pickbox, text path, index, text) when it returned 1: the option SetText was handed.
PPC_EXTERN_FUNC(__imp__rex_lhGuiPickBox_SetIndex);
REX_EXTERN(rex_lhGuiPickBox_SetIndex) {
    REX_FUNC_PROLOGUE();
    const uint32_t pickbox = ctx.r3.u32;
    const int32_t index = static_cast<int32_t>(ctx.r4.u32);
    const std::string text = RunStating(ctx, base, __imp__rex_lhGuiPickBox_SetIndex);
    if ((ctx.r3.u32 & 0xFFu) == 0u) {
        return;
    }
    std::string path;
    {
        std::lock_guard<std::mutex> lock(g_pickbox_mutex);
        const auto found = g_pickbox_paths.find(pickbox);
        if (found != g_pickbox_paths.end()) {
            path = found->second;
        }
    }
    PostFrontEnd("@PickBox.Set", {Hex(pickbox), path, std::to_string(index), text});
    const uint64_t n = ++g_pickboxes_stated;
    if (Milestone(n)) {
        REXLOG_INFO("Refii pickbox: 0x{:08X} '{}' set to {} '{}' (#{})", pickbox, path.empty() ? "(no Init seen)" : path,
                    index, text, n);
    }
}

// THE ROW WIDGET'S ELEMENT. The base Init the pickbox's and the slider's Init both open with (TU1
// `sub_82882340`, disc `sub_82886458`): r3 the widget, r4 its presentation, r5 its path
// (CCharString). It stores the presentation at +4 (0x82882368), assigns the path to +8
// (0x82882378), resolves it into +32 (0x82882390) and sets +25 (0x828823a4), returning 1. Stated
// after the call as `@Widget.Init` (widget, path) when it returned 1; the widget's SetSelected and
// a slider's SetValue are stated under that path.
PPC_EXTERN_FUNC(__imp__rex_lhGuiWidget_Init);
REX_EXTERN(rex_lhGuiWidget_Init) {
    REX_FUNC_PROLOGUE();
    const uint32_t widget = ctx.r3.u32;
    const std::string path = CharStringText(base, ctx.r5.u32);
    __imp__rex_lhGuiWidget_Init(ctx, base);
    if ((ctx.r3.u32 & 0xFFu) == 0u) {
        return;
    }
    {
        std::lock_guard<std::mutex> lock(g_pickbox_mutex);
        g_widget_paths[widget] = path;
    }
    PostFrontEnd("@Widget.Init", {Hex(widget), path});
}

// THE ROW CURSOR. SetSelected (TU1 `sub_82882440`, disc `sub_82886558`): r3 the widget, r4 the
// flag. With +12 and +25 set it stores the flag at +24 (0x82882480) and moves the widget's element
// to slide 2 (selected) or 1 (0x828824c0), returning 1. Its callers are the two row selectors, TU1
// `sub_829C0438(map, key)` and `sub_829C0C08(map)` (disc `sub_828D8FF0`, `sub_828DA098`), which
// clear the old row's widget, set the new one's and store the key at map+12 (0x829c053c,
// 0x829c0d10). Stated after the call as `@Widget.Select` (widget, path, 0|1) when it returned 1.
PPC_EXTERN_FUNC(__imp__rex_lhGuiWidget_SetSelected);
REX_EXTERN(rex_lhGuiWidget_SetSelected) {
    REX_FUNC_PROLOGUE();
    const uint32_t widget = ctx.r3.u32;
    const bool selected = (ctx.r4.u32 & 0xFFu) != 0u;
    __imp__rex_lhGuiWidget_SetSelected(ctx, base);
    if ((ctx.r3.u32 & 0xFFu) == 0u) {
        return;
    }
    PostFrontEnd("@Widget.Select", {Hex(widget), WidgetPath(widget), selected ? "1" : "0"});
}

// THE SLIDER'S VALUE. SetValue (TU1 `sub_82881E38`, disc `sub_82885F50`): r3 the slider, f1 the
// value. With +12 set (0x82881e50) it clamps the value to 0..1 (0x82881e6c..0x82881e84), stores it
// at +40 (0x82881e88) and places `<path>` + ".grp.pip" at x = value * 440 - 210 (0x82881eb0,
// `sub_824251E0` 0x82881ed8). Callers: the slider's own left/right (`sub_82881D60`), the options
// pages (`sub_829C08B8`, `sub_829C09F0`, `sub_829C12E0`), the co-op screen and the logbook's
// meters. Stated after the call as `@Slider.Set` (slider, path, the clamped value's float bits)
// when +12 was set.
PPC_EXTERN_FUNC(__imp__rex_lhGuiSlider_SetValue);
REX_EXTERN(rex_lhGuiSlider_SetValue) {
    REX_FUNC_PROLOGUE();
    const uint32_t slider = ctx.r3.u32;
    const float value = std::clamp(static_cast<float>(ctx.f1.f64), 0.0f, 1.0f);
    const bool ready = Plausible(slider) && base[slider + 12u] != 0u;
    __imp__rex_lhGuiSlider_SetValue(ctx, base);
    if (!ready) {
        return;
    }
    uint32_t bits = 0u;
    std::memcpy(&bits, &value, sizeof(bits));
    const std::string path = WidgetPath(slider);
    PostFrontEnd("@Slider.Set", {Hex(slider), path, std::to_string(bits)});
    const uint64_t n = ++g_sliders_stated;
    if (Milestone(n)) {
        REXLOG_INFO("Refii slider: 0x{:08X} '{}' set to {} (#{})", slider, path, value, n);
    }
}

// THE LOGBOOK'S EXPRESSION PAGE. FillExpression (TU1 `sub_829BCDF0`, disc `sub_828D61D0`): r3 the
// screen, r4 the row (type, id). On type 61 (0x829bce10) it looks the id up (`sub_821DA348`
// 0x829bce1c) and writes the record's `NameTag` (0x829bce94), `IconGraphic` (GuiElement_SetTexture
// 0x829bcee8, disc 0x828d62c8) and `ExpressionDescriptionTextTag` (0x829bcf38), then the effects
// (`sub_829BCF98`). Godot draws the same record from the row it states (Engine_UiLogbookPage), so
// nothing written inside is told to the elements.
PPC_EXTERN_FUNC(__imp__rex_lhGuiLogbookScreen_FillExpression);
REX_EXTERN(rex_lhGuiLogbookScreen_FillExpression) {
    REX_FUNC_PROLOGUE();
    RunStating(ctx, base, __imp__rex_lhGuiLogbookScreen_FillExpression);
    const uint64_t n = ++g_expressions_filled;
    if (Milestone(n)) {
        REXLOG_INFO("Refii logbook: expression page filled by the guest, untold (#{})", n);
    }
}

// THE CO-OP SCREEN (24), STATED. Its handler keeps the left page's row map at +32 and the right's at
// +144 (each map +12 the focus, +16 accepted), the save picker's shown byte +304 and cursor +392,
// and the closing byte +268. Their writers, besides the constructor `sub_82434DD8`:
//   OnEnter (TU1 `sub_824353E0`, disc `sub_82476600`; vtable slot +8): accepted 0 on both pages
//     (0x824359f4 / 0x824359f8, disc 0x82476c14 / 0x82476c18) and closing 0 (0x8243597c, disc
//     0x82476b9c), all `r27`, which is `li r27, 0` (0x82435408 / disc 0x82476628) — `@Coop.Enter`;
//   SetAccepted (TU1 `sub_8243AC48`, disc `sub_8247BF00`): r3 the map, r4 the page's name prefix,
//     r5 the flag, stored at map+16 (0x8243ac60 / disc 0x8247bf18) — `@Coop.Accepted`;
//   Finish (TU1 `sub_8243A4D0`, disc `sub_8247B6C8`): closing = 1 (0x8243a5c0 / disc 0x8247b7b8)
//     on every path past its own `+268` gate (0x8243a4e8 / disc 0x8247b6e0) — `@Coop.Closing`;
//   the picker's SetShown (TU1 `sub_8243B440`, disc `sub_8247C6F8`): r4 stored at +304
//     (0x8243b4f0 / disc 0x8247c7a8) — `@CoopPicker.Shown`;
//   the picker's cursor (TU1 `sub_82439738`, disc `sub_8247A930`): r4 stored at +392 when in range,
//     returning 1 (0x82439854 / disc 0x8247aa4c) — `@CoopPicker.Cursor`;
//   the picker's open, above, with the cursor it starts on.
// The right page's focus is the row cursor's own statement (`@Widget.Select` on its pickboxes).
REX_EXTERN(rex_lhGuiCoopScreen_OnEnter) {
    REX_FUNC_PROLOGUE();
    const uint32_t screen = ctx.r3.u32;
    __imp__rex_lhGuiCoopScreen_OnEnter(ctx, base);
    PostFrontEnd("@Coop.Enter", {Hex(screen)});
    REXLOG_INFO("Refii co-op screen: 0x{:08X} entered", screen);
}

REX_EXTERN(rex_lhGuiCoopScreen_SetAccepted) {
    REX_FUNC_PROLOGUE();
    const uint32_t map = ctx.r3.u32;
    const std::string page = CharStringText(base, ctx.r4.u32);
    const bool accepted = (ctx.r5.u32 & 0xFFu) != 0u;
    __imp__rex_lhGuiCoopScreen_SetAccepted(ctx, base);
    PostFrontEnd("@Coop.Accepted", {Hex(map), page, accepted ? "1" : "0"});
    REXLOG_INFO("Refii co-op screen: '{}' {}accepted", page, accepted ? "" : "not ");
}

REX_EXTERN(rex_lhGuiCoopScreen_Finish) {
    REX_FUNC_PROLOGUE();
    const uint32_t screen = ctx.r3.u32;
    __imp__rex_lhGuiCoopScreen_Finish(ctx, base);
    PostFrontEnd("@Coop.Closing", {Hex(screen)});
}

REX_EXTERN(rex_lhGuiCoopScreen_SetPickerShown) {
    REX_FUNC_PROLOGUE();
    const uint32_t screen = ctx.r3.u32;
    const bool shown = (ctx.r4.u32 & 0xFFu) != 0u;
    __imp__rex_lhGuiCoopScreen_SetPickerShown(ctx, base);
    PostFrontEnd("@CoopPicker.Shown", {Hex(screen), shown ? "1" : "0"});
}

REX_EXTERN(rex_lhGuiCoopScreen_SetPickerCursor) {
    REX_FUNC_PROLOGUE();
    const uint32_t screen = ctx.r3.u32;
    const uint32_t cursor = ctx.r4.u32;
    __imp__rex_lhGuiCoopScreen_SetPickerCursor(ctx, base);
    if ((ctx.r3.u32 & 0xFFu) != 0u) {
        PostFrontEnd("@CoopPicker.Cursor", {Hex(screen), std::to_string(cursor)});
    }
}
