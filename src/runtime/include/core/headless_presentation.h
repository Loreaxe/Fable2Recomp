// THE HEADLESS GAMEFACE CORE: the presentations the guest keeps, with no .bgf scene, no guest
// CPresentation and no guest element tree. PAYLOAD side — implemented in
// src/payload/src/core/headless_presentation.cpp and compiled into each title's module beside its
// hooks; a title's hooks are thin shims over it.
//
// Both titles run Anark Gameface 4.0.5 and lay its objects out alike (CPresentation 876 bytes, the
// element service at +492 with the root at +500, the slide manager at +568, the scene at +24; an
// element's flags +28, slide count +42, current slide +43, timeline +48..+64). What differs is
// WHERE: every function and global the core calls or reads is the title's, in a GamefaceTable its
// hooks hand in (Fable 2: Fable2/src/vfs/src/hooks/gui_headless_presentation.cpp; Fable 3:
// Fable3/src/hooks/src/headless_table.h).
//
// THE STATED ELEMENTS. The create makes a zeroed block of the presentation's size whose only words
// are the three vtables the guest calls through (the presentation's at +0, the element service's at
// pres_service, the slide manager's at pres_animation), the stand-in scene at pres_scene and the
// root's handle at pres_root (service +8, which the service's root getter returns). No ctor, no
// sub-object, no element tree, no attribute store, no per-frame update in guest memory. Every
// element is a HANDLE: a zeroed guest block of kHandleBytes made when the guest first names it
// (child-by-name, the child walk), one per file element, keyed by its address. The title's hooks
// answer the service and manager slots, and the direct readers that would walk element memory, for a
// handle from the host's record of it (the Stated* functions): the .bgf's children, parent, flags,
// slide count and attribute records (read once per file through Godot's asset reader), the last
// value the guest stored, the slide and timeline the guest set. A name the file has not got answers
// 0, as child-by-name does on a loaded tree. Handle memory is never written by the core but for the
// root's +48 (NoteRootRead).
//
// The statements each element is told reach the host through the shared queues
// (core/told_statements.h), keyed (presentation, serial, kHeadlessOrdinal | id).
//
// THE PARAMETERS ARE A HOST STORE. The presentation's "Parameters" element (Lua's `g_Param`) is
// the mailbox the C++ and the UI scripts trade values through: the setters write an attribute it
// carries and nothing else, the getters read one or miss. Each presentation holds the attributes
// the file's Parameters element authors (a root child of that name), numeric as their value words
// and strings as text, and each title's parameter functions answer from them (ParamGet /
// ParamSet*); no Parameters element is made in guest memory.
//
// THE EVENT REGISTRATIONS ARE STATED. CPresentation vtable +108 registers `fn(ctx, &{element,
// id})` for an event on an element, +112 removes it; each call is kept with its presentation, so a
// posted event reaches the callbacks the guest named (Registrations).
#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

#include <rex/ppc.h>

namespace refii::headless {

/// A title's Gameface as the core reaches it. Addresses are the RUNNING build's (the title
/// converts its own); offsets are bytes; a 0 address is a step the title has not got.
struct GamefaceTable {
    const char* title = "";

    // The title's own services.
    PPCFunc* (*find)(uint32_t address) = nullptr;                    // a recompiled entry, or null
    uint32_t (*alloc)(PPCContext& ctx, uint8_t* base, uint32_t size) = nullptr;  // the guest heap
    void (*free)(PPCContext& ctx, uint8_t* base, uint32_t address) = nullptr;

    uint32_t presentation_size = 0;

    // CPresentation fields and vtable slots (byte offsets).
    uint32_t pres_scene = 0;
    uint32_t pres_service = 0;
    uint32_t pres_root = 0;
    uint32_t pres_slot_nullsub = 0;   // a `blr`, the stand-in scene's refresh

    // CGameface fields: the clock object and its ms slot, the presentation callback.
    uint32_t gameface_clock = 0;
    uint32_t clock_slot_ms = 0;
    uint32_t gameface_callback = 0;     // (code, pres, 0, ctx): 2 made, 0 loaded, 1 destroyed
    uint32_t gameface_callback_ctx = 0;

    // The stand-in scene: a copy of CScene's vtable with the refresh a `blr`, and an empty list.
    uint32_t scene_vtable = 0;
    uint32_t scene_slots = 0;
    uint32_t scene_slot_refresh = 0;
    uint32_t scene_slot_list = 0;
    uint32_t scene_render_list = 0;

    // Names: the hash an element or attribute name takes (`(char*) -> hash`), the 27-bit id mask,
    // the "Parameters" CCharString global (the element the parameter store is seeded from).
    uint32_t name_hash = 0;
    uint32_t attribute_id_mask = 0;
    uint32_t parameters_name = 0;

    // The globals holding the start / end time attribute ids (an element with either carries
    // flag 0x0002, as the deserialiser sets it).
    uint32_t attr_start_time = 0;
    uint32_t attr_end_time = 0;

    // The inert quad record the title's quad readers' joins answer for a headless element: the
    // guest's own record ctor and size; 0 when the title's readers need none.
    uint32_t quad_record_ctor = 0;
    uint32_t quad_record_size = 0;

    /// The vtables the zeroed presentation block carries and the slide manager's offset (vf +36).
    uint32_t presentation_vtable = 0;
    uint32_t service_vtable = 0;
    uint32_t animation_vtable = 0;
    uint32_t pres_animation = 0;
};

/// The bytes of a handle: past the timeline record a guest writer would reach (+43..+64).
constexpr uint32_t kHandleBytes = 0x50u;

/// CGameface's maker: a headless presentation for the path at `path`, its two notifications (2
/// made, 0 loaded) included; 0 when there was no room.
uint32_t Create(const GamefaceTable& table, PPCContext& ctx, uint8_t* base, uint32_t gameface, uint32_t path);

/// CGameface's destroy of `presentation`: true when it was headless and is gone; false when it is
/// not one of the core's (the caller runs the original).
bool Destroy(const GamefaceTable& table, PPCContext& ctx, uint8_t* base, uint32_t gameface, uint32_t presentation);

/// CGameface's destructor: destroys the headless presentations still on `gameface`. The shipped
/// destructor frees what is left in its kernel list, which never holds these.
void DestroyRemaining(const GamefaceTable& table, PPCContext& ctx, uint8_t* base, uint32_t gameface);

/// How many headless presentations live on `gameface`.
size_t CountOn(uint32_t gameface);

/// After GetRootElement returned `root`: a headless root leaves with its timeline time (clock
/// minus the presentation's creation clock) at +48.
void NoteRootRead(const GamefaceTable& table, PPCContext& ctx, uint8_t* base, uint32_t root);

/// A headless element's key, or false.
bool ElementKey(uint32_t element, uint32_t* presentation, uint32_t* serial, uint32_t* id);

/// Whether `presentation` is a live headless one.
bool IsHeadless(uint32_t presentation);

/// A headless element's ordinal in its presentation's file (the .bgf element table's index, the
/// root 0), or -1 when it is not a live handle.
int32_t FileOrdinal(uint32_t element);

/// The inert quad record (0 before the first create, or when the title has none).
uint32_t InertQuadRecord();

/// Gameface's name hash (`h = 65599 * h + c` over at most 100 signed characters, the NUL folded
/// in; "" hashes 0) — the hash every element, attribute and event name takes.
uint32_t NameHash(const char* text);

/// One parameter: a numeric attribute's value word, or a string attribute's text.
struct Param {
    bool text = false;
    uint32_t bits = 0;
    std::string string;
};

/// The parameter `id` (a name's hash in the attribute-id mask) of headless `presentation`; false
/// when the presentation is not headless or its Parameters element does not carry it.
bool ParamGet(uint32_t presentation, uint32_t id, Param* out);

/// Writes a carried numeric parameter; false (nothing written) when it is not carried as a number.
bool ParamSetNumber(uint32_t presentation, uint32_t id, uint32_t bits);

/// Writes a carried string parameter; false (nothing written) when it is not carried as a string.
bool ParamSetText(uint32_t presentation, uint32_t id, const std::string& text);

/// A stated-element answer: the element is not a handle (the guest's own object answers), or the
/// handle has / has not got what was asked.
enum class Answer { kNotHandle, kNo, kYes };

/// The presentation a handle belongs to, 0 when `element` is not a live handle.
uint32_t HandlePresentation(uint32_t element);

/// Element service vf +84 for a stated presentation: the file child of `parent` (0: the root level —
/// the root itself when the name is the root's, else a root child) named `hash`, its handle made
/// on first use; 0 when the file has no such child.
uint32_t StatedChildNamed(const GamefaceTable& table, PPCContext& ctx, uint8_t* base, uint32_t presentation,
                          uint32_t hash, uint32_t parent);

/// Element service vf +92: the handles of `parent`'s file children in the file's order, each made on
/// first use; only those carrying flag 0x1000 unless `all` (the service's vf +4 filter).
Answer StatedChildren(const GamefaceTable& table, PPCContext& ctx, uint8_t* base, uint32_t parent, bool all,
                      std::vector<uint32_t>* out);

/// Element service vf +88: the parent's handle, 0 for the root.
Answer StatedParent(uint32_t element, uint32_t* parent);

/// The attribute record lookup (TU1 `sub_822848E0`) for a handle: the record word and value of
/// attribute `id` — the last value stored, else the file's. A string attribute's value is a guest
/// cell `{char*}` holding the file's text, made on first use.
Answer StatedRecord(const GamefaceTable& table, PPCContext& ctx, uint8_t* base, uint32_t element, uint32_t id,
                    uint32_t* word, uint32_t* value);

/// A guest 8-byte record `{word, value}` from a ring of them, for a caller that takes the record's
/// address (0 when there is no room).
uint32_t RecordScratch(const GamefaceTable& table, PPCContext& ctx, uint8_t* base, uint32_t word, uint32_t value);

/// The store (service vf +72): kYes when the handle carries the attribute and now holds `value`.
Answer StatedStore(uint32_t element, uint32_t id, uint32_t value);

/// The flag word (service vf +76 tests it, +80 writes it).
Answer StatedFlags(uint32_t element, uint16_t* flags);
Answer StatedFlagsWrite(uint32_t element, uint16_t mask, bool on);

/// GotoSlide (animation vf +8): kNo when `slide` is outside 1 .. count - 1 (nothing changes);
/// otherwise the current slide, the previous, and the timeline restarted.
Answer StatedGotoSlide(uint32_t element, int32_t slide);
/// The current slide (animation vf +32).
Answer StatedCurrentSlide(uint32_t element, uint8_t* slide);
/// SetTime (animation vf +44).
Answer StatedSetTime(uint32_t element, int32_t time);
/// SetPaused (animation vf +60).
Answer StatedSetPaused(uint32_t element, bool paused);
/// Animation vf +40: the timeline advanced to `clock` (TU1 `sub_82284DF8`'s rules); `time` the
/// timeline's time after it, `ended` its play-once end edge.
Answer StatedAdvance(uint32_t element, int32_t clock, int32_t* time, bool* ended);
/// The nearest timeline owner of a handle: itself or its nearest ancestor carrying flag 0x0001 (the
/// root always does) — the owner walk's answer.
Answer StatedOwner(uint32_t element, uint32_t* owner);
/// The handle's slide count (+42): its file component's slides, 0 for an element that owns none.
Answer StatedSlideCount(uint32_t element, uint8_t* count);
/// The handle of file element `ordinal` of `presentation` when the guest has named it, else 0.
uint32_t StatedHandleAt(uint32_t presentation, int32_t ordinal);

/// One registration: `fn(ctx, &{element, id})` for `id` on `element`.
struct Registration {
    uint32_t element = 0;
    uint32_t id = 0;
    uint32_t fn = 0;
    uint32_t ctx = 0;
};

/// CPresentation vtable +108 (`add`) / +112 on `presentation`, as the guest called it.
void NoteRegistration(uint32_t presentation, const Registration& registration, bool add);

/// The registrations for event `id` on the live headless presentation whose file is `stem` (the
/// file name without directory or extension, any case), on its root when `parent_hash` is 0, else
/// on an element of that name hash; each distinct (fn, ctx) once. `presentation` receives the
/// presentation (0 when none is live).
std::vector<Registration> Registrations(const std::string& stem, uint32_t parent_hash,
                                        uint32_t id, uint32_t* presentation);

// The statements told to a headless element, through the shared queues; each is false (nothing
// posted) when `element` is not one.
bool TellAttribute(uint32_t element, uint32_t attribute, uint32_t value_bits);
bool TellActive(uint32_t element, bool on);
bool TellTexture(uint32_t element, uint32_t hash);
bool TellGotoSlide(uint32_t element, uint32_t slide);
bool TellPaused(uint32_t element, bool paused);
bool TellText(uint32_t element, const std::vector<uint16_t>& units);

}  // namespace refii::headless
