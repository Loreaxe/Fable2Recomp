// The GUI registries' SLIDE, IMAGE, METER and TEXT writers, stated to the host.
//
// A `.bgf` element binds to a name in one of the GUI manager's typed registries (the vector at
// `*(*(GUI+0x20)+0x20)+28`: [2] SlideFaderName, [3] SlideFader2Name, [9] SlideToggleName,
// [10] SlideSetName, …) and the guest drives it BY NAME. Three writers play slides on every element
// bound to a name, write-through, so the registry holds no value a host could read back (TU1
// addresses; disc 0x8257B408, 0x8257C9C8, 0x82845CB8):
//
//   sub_823FE848(registry, nameHash, bool on)   — toggle: slide 2 on, 1 off (`sub_82426CA8`)
//   sub_828421F8(registry, nameHash, int slide) — fader / slide set, entered (`sub_82426BA8`)
//   sub_827FA600(registry, nameHash, int slide) — the same, entered and played (`sub_82426CA8`);
//                                                  the fade natives' path
//
// All three are hooked AFTER the original and post `@Gui.Slide registry hash slide played` on the
// GUI call feed, so a Godot-drawn screen takes the slide from the guest's statement by name rather
// than from the presentation it plays on. `played` is 1 for the writers that go through
// `sub_82426CA8` (slide entry, then `SetPaused(0)` 0x82426cf8) and 0 for `sub_82426BA8`, which
// leaves an authored-paused slide waiting at t = 0. Posted on CHANGE only: the TopBox publisher
// `sub_821787D0` restates its toggles every frame.
#include <cstdint>
#include <cstdio>
#include <mutex>
#include <string>
#include <unordered_map>
#include <initializer_list>
#include <utility>
#include <vector>

#include "vfs_hooks_private.h"
#include "core/guest_build.h"
#include "core/guest_state.h"
#include "core/gui_native_calls.h"
#include "hooks/ppc_externs.h"

namespace {

bool Plausible(uint32_t value) { return value >= 0x10000u && value < 0xC0000000u; }

/// The registry's index in the manager's vector, or -1 when it is not one of them.
int RegistryIndex(uint8_t* base, uint32_t registry) {
    const uint32_t gui = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496B08u));
    if (!Plausible(gui)) return -1;
    const uint32_t manager = refii::vfs::LoadGuestU32(base, gui + 0x20u);
    if (!Plausible(manager)) return -1;
    const uint32_t vector = refii::vfs::LoadGuestU32(base, manager + 0x20u);
    if (!Plausible(vector)) return -1;
    const uint32_t begin = refii::vfs::LoadGuestU32(base, vector + 28u);
    const uint32_t end = refii::vfs::LoadGuestU32(base, vector + 32u);
    if (!Plausible(begin) || end <= begin || (end - begin) > 0x400u) return -1;
    for (uint32_t at = begin, index = 0; at < end; at += 4u, ++index) {
        if (refii::vfs::LoadGuestU32(base, at) == registry) return static_cast<int>(index);
    }
    return -1;
}

std::mutex g_said_lock;
std::unordered_map<uint64_t, uint32_t> g_said;

void State(uint8_t* base, uint32_t registry, uint32_t hash, uint32_t slide, bool played) {
    const int index = RegistryIndex(base, registry);
    if (index < 0) return;
    {
        const uint64_t key = (static_cast<uint64_t>(index) << 32) | hash;
        std::lock_guard<std::mutex> lock(g_said_lock);
        auto found = g_said.find(key);
        if (found != g_said.end() && found->second == slide) return;
        g_said[key] = slide;
    }

    refii::vfs::GuiNativeCall call;
    call.name = "@Gui.Slide";
    for (const uint32_t value : {static_cast<uint32_t>(index), hash, slide, played ? 1u : 0u}) {
        refii::vfs::GuiNativeArgument argument;
        argument.kind = refii::vfs::GuiNativeArgument::Kind::kString;
        argument.text = std::to_string(value);
        call.arguments.push_back(std::move(argument));
    }
    refii::vfs::PostGuiNativeCall(std::move(call));
}

}  // namespace

// Toggle: TU1 0x823FE848.
REX_EXTERN(rex_lhGuiRegistry_SetToggle) {
    const uint32_t registry = ctx.r3.u32;
    const uint32_t hash = ctx.r4.u32;
    const uint32_t slide = (ctx.r5.u32 & 0xFFu) != 0u ? 2u : 1u;
    __imp__rex_lhGuiRegistry_SetToggle(ctx, base);
    State(base, registry, hash, slide, true);
}

// Fader / slide set: TU1 0x828421F8.
REX_EXTERN(rex_lhGuiRegistry_SetSlide) {
    const uint32_t registry = ctx.r3.u32;
    const uint32_t hash = ctx.r4.u32;
    const uint32_t slide = ctx.r5.u32;
    __imp__rex_lhGuiRegistry_SetSlide(ctx, base);
    State(base, registry, hash, slide, false);
}

// Fader / slide set, jumping: TU1 0x827FA600.
REX_EXTERN(rex_lhGuiRegistry_JumpSlide) {
    const uint32_t registry = ctx.r3.u32;
    const uint32_t hash = ctx.r4.u32;
    const uint32_t slide = ctx.r5.u32;
    __imp__rex_lhGuiRegistry_JumpSlide(ctx, base);
    State(base, registry, hash, slide, true);
}

// Image: TU1 0x82804C28, `(registry, nameHash, const u32* textureHash)` — writes the resource
// hash onto every bound quad (`+0x200`, dirty `+0x224`). Posted `@Gui.Image hash texture` on
// change. The disc build has no such function: its one publisher, the epilogue `sub_824F23A0`
// (TU1 `sub_823D3FF0`), inlines the loop, so the original is weak there and the disc states the
// same pair through RefiiGuiImageInlined below. TU1's other caller, the episodic screen
// `sub_823D02D8` ("EpisodeImage"), is DLC the disc build does not contain.
extern "C" __attribute__((weak)) REX_FUNC(__imp__rex_lhGuiRegistry_SetImage);

namespace {

std::unordered_map<uint32_t, uint32_t> g_images;

void StateImage(uint32_t hash, uint32_t texture) {
    {
        std::lock_guard<std::mutex> lock(g_said_lock);
        auto found = g_images.find(hash);
        if (found != g_images.end() && found->second == texture) return;
        g_images[hash] = texture;
    }

    refii::vfs::GuiNativeCall call;
    call.name = "@Gui.Image";
    for (const uint32_t number : {hash, texture}) {
        refii::vfs::GuiNativeArgument argument;
        argument.kind = refii::vfs::GuiNativeArgument::Kind::kString;
        argument.text = std::to_string(number);
        call.arguments.push_back(std::move(argument));
    }
    refii::vfs::PostGuiNativeCall(std::move(call));
}

}  // namespace

REX_EXTERN(rex_lhGuiRegistry_SetImage) {
    if (&__imp__rex_lhGuiRegistry_SetImage == nullptr) {
        return;
    }

    const uint32_t hash = ctx.r4.u32;
    const uint32_t value = ctx.r5.u32;
    const uint32_t texture = Plausible(value) ? refii::vfs::LoadGuestU32(base, value) : 0u;
    __imp__rex_lhGuiRegistry_SetImage(ctx, base);
    StateImage(hash, texture);
}

extern "C" uint8_t* RefiiGuestBase(void);

// The disc epilogue's inlined image write (disc config only). At 0x824F283C `bl sub_822A9A20`
// finds the registry entry: r4 = the name hash (`lwz r4, dword_834983D4@l(r10)` 0x824F2838) and
// the texture is the handle at r1+0x58 (`lwz r9, 0xA0+var_48(r1)` 0x824F2858, stored to every
// quad's +0x200 at 0x824F288C) — the arguments TU1 passes SetImage.
void RefiiGuiImageInlined(PPCRegister& r1, PPCRegister& r4) {
    uint8_t* base = RefiiGuestBase();
    if (base == nullptr) {
        return;
    }
    StateImage(r4.u32, refii::vfs::LoadGuestU32(base, r1.u32 + 0x58u));
}

// THE METER AND TEXT WRITERS, stated at their entry from their arguments.
//
// A meter or text name's record holds the elements filed under it; each writer finds the record
// (`sub_822A9C60`, disc `sub_822A9A20`) and writes every binding, and writes nothing for a name
// with no record. The host follows the CALL, not the record: `@Gui.Meter registry hash op a b c`
// and `@Gui.Text registry hash text`, posted on change (the HUD restates its meters every frame).
// The binding fields are +20 current, +24 rate (percent per second, 0 snaps), +28 target, +12 the
// seek mode, every value clamped to 0..100 by the writer (TU1 addresses; disc in brackets):
//
//   set   sub_823FE210 [sub_8257C348](registry, hash, f1)         target f1, rate 0, mode 0
//   seek  sub_823FE2D0 [sub_8257C408](registry, hash, f1, f2)     target f1, rate (f1 - current) / f2,
//                                                                  0 when f2 <= 0
//   run   sub_823FE068 [sub_8257C1A0](registry, hash, f1, f2, f3) current f1, target f2, rate f3
//   scrub sub_823FE3D0 [sub_8257C508](registry, hash, f1)         target f1, rate 0, then each bound
//                                                                  element to goToTime(trunc(f1 x 10))
//   text  sub_823FEB28 [sub_8257C720](registry, hash, LHString*)  the string, already localised
//                                                                  (handle -> {UTF-16 BE data, length})
namespace {

std::unordered_map<uint64_t, std::string> g_values_said;

std::string Number(float value) {
    char text[32];
    std::snprintf(text, sizeof text, "%.9g", static_cast<double>(value));
    return text;
}

void PostValue(uint8_t* base, const char* name, uint32_t registry, uint32_t hash,
               std::vector<std::string> values) {
    const int index = RegistryIndex(base, registry);
    if (index < 0) return;
    std::string said;
    for (const std::string& value : values) {
        said += value;
        said += '\x1f';
    }
    {
        const uint64_t key = (static_cast<uint64_t>(index) << 32) | hash;
        std::lock_guard<std::mutex> lock(g_said_lock);
        auto found = g_values_said.find(key);
        if (found != g_values_said.end() && found->second == said) return;
        g_values_said[key] = said;
    }

    refii::vfs::GuiNativeCall call;
    call.name = name;
    values.insert(values.begin(), {std::to_string(index), std::to_string(hash)});
    for (std::string& value : values) {
        refii::vfs::GuiNativeArgument argument;
        argument.kind = refii::vfs::GuiNativeArgument::Kind::kString;
        argument.text = std::move(value);
        call.arguments.push_back(std::move(argument));
    }
    refii::vfs::PostGuiNativeCall(std::move(call));
}

/// The LHString handle at `at` as UTF-8: `*at` the rep, rep +0 the UTF-16 big-endian data, +4 its
/// length in code units.
std::string WideText(uint8_t* base, uint32_t at) {
    std::string out;
    if (!Plausible(at)) return out;
    const uint32_t rep = refii::vfs::LoadGuestU32(base, at);
    if (!Plausible(rep)) return out;
    const uint32_t chars = refii::vfs::LoadGuestU32(base, rep);
    const uint32_t length = refii::vfs::LoadGuestU32(base, rep + 4u);
    if (!Plausible(chars) || length > 4096u) return out;
    for (uint32_t c = 0; c < length; ++c) {
        const uint16_t unit = refii::vfs::LoadGuestU16(base, chars + (c * 2u));
        if (unit == 0u) break;
        if (unit < 0x80u) {
            out += static_cast<char>(unit);
        } else if (unit < 0x800u) {
            out += static_cast<char>(0xC0u | (unit >> 6));
            out += static_cast<char>(0x80u | (unit & 0x3Fu));
        } else {
            out += static_cast<char>(0xE0u | (unit >> 12));
            out += static_cast<char>(0x80u | ((unit >> 6) & 0x3Fu));
            out += static_cast<char>(0x80u | (unit & 0x3Fu));
        }
    }
    return out;
}

}  // namespace

namespace {

/// What the writers last said, forgotten. The maps drop a statement that repeats the last one, and
/// the guest moves the bound elements without a writer in three places: a level change or a save
/// load resets every binding (`sub_827F16F8`, disc `sub_827CE0B0`: slot 2 of each, from
/// `sub_82371AA0`), and the HUD's registries are destroyed and built again (`sub_827F0E20` /
/// `sub_827F0F00`, disc `sub_827CD7D8` / `sub_827CD8B8`). After any of them the guest's next
/// statement is news even when it repeats the last one.
uint32_t g_targeted_said = 0xFFFFFFFFu;  // under g_said_lock

void ForgetWhatWasSaid() {
    std::lock_guard<std::mutex> lock(g_said_lock);
    g_said.clear();
    g_images.clear();
    g_values_said.clear();
    g_targeted_said = 0xFFFFFFFFu;
}

}  // namespace

// The targeted readout's publisher `sub_823FD410(f1 opinion, r3 block, r4 record)` (disc
// `sub_8256CB40`): the record's first word is the entity it publishes (`sub_821B2390(*a3)`
// 0x823fd478, `*(*a3 + 144) & 0x40` 0x823fd530). Everything else it publishes goes through the
// registry writers above; the entity is stated here, when it is another one.
REX_EXTERN(rex_lhHudTargeted_Publish) {
    const uint32_t record = ctx.r4.u32;
    const uint32_t entity = Plausible(record) ? refii::vfs::LoadGuestU32(base, record) : 0u;
    __imp__rex_lhHudTargeted_Publish(ctx, base);
    {
        std::lock_guard<std::mutex> lock(g_said_lock);
        if (g_targeted_said == entity) return;
        g_targeted_said = entity;
    }
    refii::vfs::GuiNativeCall call;
    call.name = "@Hud.Targeted";
    refii::vfs::GuiNativeArgument argument;
    argument.kind = refii::vfs::GuiNativeArgument::Kind::kString;
    argument.text = std::to_string(entity);
    call.arguments.push_back(std::move(argument));
    refii::vfs::PostGuiNativeCall(std::move(call));
}

// ---------------------------------------------------------------------------------------------
// THE TOPBOX. Its composer `sub_821787D0(mgr, f1 dt)` (disc `sub_82178598`) states the front
// record through the registry writers above, and asks two things on the way that it never
// writes anywhere: the front record (`sub_8230D4F0(mgr)` at 0x82178848, disc `sub_82333930`) and
// its channel (`sub_8230D130(mgr)` at 0x82178970, disc `sub_82333570`). What those two answered
// inside one composition is stated here, with the record's own fields (`+0` name hash, `+4` title
// tag, `+16/+17` A shown / held, `+24/+25` Y shown / held).
//
// The composer is gated (its countdown `mgr+32`, the hidden bytes `mgr+0/+1` at 0x8217881c): a
// call that never asks for the front states nothing. `sub_8230C7A8(mgr, hidden)` (disc
// `sub_82332BE8`, `stb r4,0(r3)` 0x8230c7dc) is `GUI.SetTopBoxHidden`'s write, stated as it is.
//
// The title and body go through the text group writer `sub_823FCEB0(registry, group hash, vector)`
// (disc `sub_8257BE90`, 0x82178c14): the vector's `+4/+8` are the begin and end of 16-byte entries,
// `+0` the element's name hash and `+4` its LHString, already resolved. The writer frees the
// vector, so the entries are read before the call.
// ---------------------------------------------------------------------------------------------

namespace {

thread_local bool t_composing = false;
thread_local bool t_front_asked = false;
thread_local uint32_t t_front[7] = {};  // present, name, title, showA, holdA, showY, holdY
thread_local uint32_t t_front_channel = 0;

void PostWords(const char* name, std::initializer_list<uint32_t> words) {
    refii::vfs::GuiNativeCall call;
    call.name = name;
    for (const uint32_t word : words) {
        refii::vfs::GuiNativeArgument argument;
        argument.kind = refii::vfs::GuiNativeArgument::Kind::kString;
        argument.text = std::to_string(word);
        call.arguments.push_back(std::move(argument));
    }
    refii::vfs::PostGuiNativeCall(std::move(call));
}

}  // namespace

REX_EXTERN(rex_lhHudTopBox_Front) {
    __imp__rex_lhHudTopBox_Front(ctx, base);
    if (!t_composing) return;
    const uint32_t record = ctx.r3.u32;
    t_front_asked = true;
    t_front[0] = Plausible(record) ? 1u : 0u;
    if (t_front[0] != 0u) {
        t_front[1] = refii::vfs::LoadGuestU32(base, record);
        t_front[2] = refii::vfs::LoadGuestU32(base, record + 4u);
        t_front[3] = refii::vfs::LoadGuestU8(base, record + 16u);
        t_front[4] = refii::vfs::LoadGuestU8(base, record + 17u);
        t_front[5] = refii::vfs::LoadGuestU8(base, record + 24u);
        t_front[6] = refii::vfs::LoadGuestU8(base, record + 25u);
    }
}

REX_EXTERN(rex_lhHudTopBox_FrontChannel) {
    __imp__rex_lhHudTopBox_FrontChannel(ctx, base);
    if (t_composing) t_front_channel = ctx.r3.u32;
}

REX_EXTERN(rex_lhHudTopBox_Compose) {
    t_composing = true;
    t_front_asked = false;
    t_front_channel = 0u;
    for (uint32_t& word : t_front) word = 0u;
    __imp__rex_lhHudTopBox_Compose(ctx, base);
    t_composing = false;
    if (t_front_asked) {
        PostWords("@Hud.TopBoxFront", {t_front[0], t_front_channel, t_front[1], t_front[2], t_front[3], t_front[4],
                                       t_front[5], t_front[6]});
    }
}

REX_EXTERN(rex_lhHudTopBox_SetHidden) {
    const uint32_t hidden = (ctx.r4.u32 & 0xFFu) != 0u ? 1u : 0u;
    __imp__rex_lhHudTopBox_SetHidden(ctx, base);
    PostWords("@Hud.TopBoxHidden", {hidden});
}

REX_EXTERN(rex_lhGuiRegistry_SetTextGroup) {
    const uint32_t group = ctx.r4.u32;
    const uint32_t vector = ctx.r5.u32;
    std::vector<std::pair<uint32_t, std::string>> entries;
    if (Plausible(vector)) {
        const uint32_t begin = refii::vfs::LoadGuestU32(base, vector + 4u);
        const uint32_t end = refii::vfs::LoadGuestU32(base, vector + 8u);
        if (Plausible(begin) && end >= begin && end - begin <= 16u * 64u) {
            for (uint32_t at = begin; at + 16u <= end; at += 16u) {
                entries.emplace_back(refii::vfs::LoadGuestU32(base, at), WideText(base, at + 4u));
            }
        }
    }
    __imp__rex_lhGuiRegistry_SetTextGroup(ctx, base);
    for (auto& [element, text] : entries) {
        refii::vfs::GuiNativeCall call;
        call.name = "@Gui.TextGroup";
        for (std::string value : {std::to_string(group), std::to_string(element), std::move(text)}) {
            refii::vfs::GuiNativeArgument argument;
            argument.kind = refii::vfs::GuiNativeArgument::Kind::kString;
            argument.text = std::move(value);
            call.arguments.push_back(std::move(argument));
        }
        refii::vfs::PostGuiNativeCall(std::move(call));
    }
}

REX_EXTERN(rex_lhHudRegistries_ResetBindings) {
    __imp__rex_lhHudRegistries_ResetBindings(ctx, base);
    ForgetWhatWasSaid();
}

REX_EXTERN(rex_lhHudRegistries_Destroy) {
    __imp__rex_lhHudRegistries_Destroy(ctx, base);
    ForgetWhatWasSaid();
    PostWords("@Gui.RegistryReset", {});
}

REX_EXTERN(rex_lhHudRegistries_Build) {
    ForgetWhatWasSaid();
    PostWords("@Gui.RegistryReset", {});
    __imp__rex_lhHudRegistries_Build(ctx, base);
}

REX_EXTERN(rex_lhGuiRegistry_SetMeter) {
    const uint32_t registry = ctx.r3.u32;
    const uint32_t hash = ctx.r4.u32;
    const float value = static_cast<float>(ctx.f1.f64);
    __imp__rex_lhGuiRegistry_SetMeter(ctx, base);
    PostValue(base, "@Gui.Meter", registry, hash, {"set", Number(value), "0", "0"});
}

REX_EXTERN(rex_lhGuiRegistry_SeekMeter) {
    const uint32_t registry = ctx.r3.u32;
    const uint32_t hash = ctx.r4.u32;
    const float target = static_cast<float>(ctx.f1.f64);
    const float seconds = static_cast<float>(ctx.f2.f64);
    __imp__rex_lhGuiRegistry_SeekMeter(ctx, base);
    PostValue(base, "@Gui.Meter", registry, hash, {"seek", Number(target), Number(seconds), "0"});
}

REX_EXTERN(rex_lhGuiRegistry_RunMeter) {
    const uint32_t registry = ctx.r3.u32;
    const uint32_t hash = ctx.r4.u32;
    const float current = static_cast<float>(ctx.f1.f64);
    const float target = static_cast<float>(ctx.f2.f64);
    const float rate = static_cast<float>(ctx.f3.f64);
    __imp__rex_lhGuiRegistry_RunMeter(ctx, base);
    PostValue(base, "@Gui.Meter", registry, hash, {"run", Number(current), Number(target), Number(rate)});
}

REX_EXTERN(rex_lhGuiRegistry_ScrubMeter) {
    const uint32_t registry = ctx.r3.u32;
    const uint32_t hash = ctx.r4.u32;
    const float value = static_cast<float>(ctx.f1.f64);
    __imp__rex_lhGuiRegistry_ScrubMeter(ctx, base);
    PostValue(base, "@Gui.Meter", registry, hash, {"scrub", Number(value), "0", "0"});
}

// The meter registry's stop `sub_823FE4A0(registry, hash)` (disc `sub_8257C5D8`): every binding's
// target becomes its current value and its rate 0 (`v11[7] = v11[5]; v11[6] = 0.0` 0x823fe4e0).
REX_EXTERN(rex_lhGuiRegistry_FreezeMeter) {
    const uint32_t registry = ctx.r3.u32;
    const uint32_t hash = ctx.r4.u32;
    __imp__rex_lhGuiRegistry_FreezeMeter(ctx, base);
    PostValue(base, "@Gui.Meter", registry, hash, {"freeze", "0", "0", "0"});
}

// The pulser registry's writers: `sub_828F09C0(registry, hash, f1 rate)` (disc `sub_8283E958`)
// sets every binding's rate and mode 0 (`*(v12 + 24) = a9; *(v12 + 12) = 0` 0x828f09fc), and
// `sub_828F0A28(registry, hash)` (disc `sub_8283E9C0`) turns a looping one to finishing
// (`if ( !*(b + 12) ) *(b + 12) = 1` 0x828f0a58).
REX_EXTERN(rex_lhGuiRegistry_StartPulser) {
    const uint32_t registry = ctx.r3.u32;
    const uint32_t hash = ctx.r4.u32;
    const float rate = static_cast<float>(ctx.f1.f64);
    __imp__rex_lhGuiRegistry_StartPulser(ctx, base);
    PostValue(base, "@Gui.Pulser", registry, hash, {"start", Number(rate)});
}

REX_EXTERN(rex_lhGuiRegistry_StopPulser) {
    const uint32_t registry = ctx.r3.u32;
    const uint32_t hash = ctx.r4.u32;
    __imp__rex_lhGuiRegistry_StopPulser(ctx, base);
    PostValue(base, "@Gui.Pulser", registry, hash, {"stop", "0"});
}

REX_EXTERN(rex_lhGuiRegistry_SetText) {
    const uint32_t registry = ctx.r3.u32;
    const uint32_t hash = ctx.r4.u32;
    std::string text = WideText(base, ctx.r5.u32);
    __imp__rex_lhGuiRegistry_SetText(ctx, base);
    PostValue(base, "@Gui.Text", registry, hash, {std::move(text)});
}

// The registry images named outside SetImage (hooks/gui_headless_presentation.cpp, the spell
// charges' icons): the name hash and the resource hash the quads would carry.
void RefiiGuiStateImage(uint32_t hash, uint32_t texture) { StateImage(hash, texture); }
