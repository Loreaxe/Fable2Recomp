// The headless Gameface core — see core/headless_presentation.h for the contract. PAYLOAD side:
// compiled into each title's module (REFII_PAYLOAD_CORE_SOURCES); it calls the guest through the
// title's GamefaceTable and reaches the host only through C entries (core/payload_bridge.h).
//
// THE CREATE. The maker (CGameface vtable +28) gets a zeroed block of the presentation's size whose
// only words are its three vtables, the stand-in scene and the root's handle; every element is a
// zeroed handle the host keys, made when the guest first names it.
//
// THE SCENE. +24 is called unchecked as scene vf +16 after a slide change; it is a zeroed block
// with a guest copy of CScene's vtable whose refresh slot is the presentation's own `blr` and whose
// render-list slot answers an empty list.
//
// THE DESTROY. The handles, their string cells, the scene block and the presentation block are
// freed; nothing of a headless presentation is in the renderer or the kernel's list.

#include "core/headless_presentation.h"

#include <algorithm>
#include <atomic>
#include <cctype>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <utility>

#include <rex/logging.h>

#include "core/host_asset_reader.h"
#include "core/payload_bridge.h"
#include "core/told_statements.h"

namespace refii::headless {
namespace {

// ---------------------------------------------------------------------------------------------
// Guest memory, big-endian.

uint32_t LoadU32(const uint8_t* base, uint32_t at) {
    uint32_t v = 0;
    std::memcpy(&v, base + at, sizeof v);
    return __builtin_bswap32(v);
}

void StoreU32(uint8_t* base, uint32_t at, uint32_t value) {
    const uint32_t v = __builtin_bswap32(value);
    std::memcpy(base + at, &v, sizeof v);
}

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

bool Milestone(uint64_t n) { return n <= 32u || (n & (n - 1u)) == 0u; }

// ---------------------------------------------------------------------------------------------
// The element as the guest's deserialiser lays it out: what a handle's host record answers.

constexpr uint32_t kElemTime = 48u;  // the root's timeline time (NoteRootRead)
constexpr uint16_t kFlagOwnsTimeline = 0x0001u;
constexpr uint16_t kFlagTimeWindow = 0x0002u;
constexpr uint16_t kFlagActive = 0x2000u;
constexpr uint32_t kAttributeRecord = 8u;

// The host block behind one presentation: the stand-in scene and its empty list.
constexpr uint32_t kBlockScene = 0x000u;       // 0x700 (> the render-list offset)
constexpr uint32_t kBlockRenderList = 0x700u;  // 0x20
constexpr uint32_t kBlockSize = 0x720u;

constexpr int32_t kNoOrdinal = -1;

// ---------------------------------------------------------------------------------------------
// THE FILE: the .bgf's element table and slide records, as far as a headless tree needs them.

struct FileTable {
    struct Element {
        uint32_t name = 0;
        int32_t parent = -1;
        uint8_t flags = 0;  // bit 0 active, bit 2 a component (its own timeline)
        std::vector<std::pair<uint32_t, uint32_t>> strings;  // attribute id, string index
        std::vector<std::pair<uint32_t, uint32_t>> records;  // numeric: the store's word, value
        uint16_t slide_base = 0;
        uint8_t slide_count = 0;
        bool OwnsTimeline() const { return (flags & 4u) != 0u; }
    };
    struct Slide {
        char name[16] = {};
        uint8_t flags = 0;
        uint32_t start = 0;
        uint32_t end = 0;
    };
    std::vector<Element> elements;
    std::vector<std::vector<int32_t>> children;
    std::vector<std::string> strings;
    std::vector<Slide> slides;

    int32_t ChildNamed(int32_t ordinal, uint32_t name) const {
        if (ordinal < 0 || static_cast<size_t>(ordinal) >= children.size()) {
            return kNoOrdinal;
        }
        for (const int32_t child : children[static_cast<size_t>(ordinal)]) {
            if (elements[static_cast<size_t>(child)].name == name) {
                return child;
            }
        }
        return kNoOrdinal;
    }

    const std::string* StringAttribute(int32_t ordinal, uint32_t id) const {
        if (ordinal < 0 || static_cast<size_t>(ordinal) >= elements.size()) {
            return nullptr;
        }
        for (const auto& [attribute, index] : elements[static_cast<size_t>(ordinal)].strings) {
            if (attribute == id && index < strings.size()) {
                return &strings[index];
            }
        }
        return nullptr;
    }
};

class BgfReader {
public:
    BgfReader(const std::vector<uint8_t>& bytes, bool big_endian) : bytes_(bytes), big_endian_(big_endian) {}
    bool U32(uint32_t* out) {
        if (at_ + 4u > bytes_.size()) {
            return false;
        }
        const uint8_t* b = bytes_.data() + at_;
        *out = big_endian_ ? (uint32_t{b[0]} << 24) | (uint32_t{b[1]} << 16) | (uint32_t{b[2]} << 8) | b[3]
                           : (uint32_t{b[3]} << 24) | (uint32_t{b[2]} << 16) | (uint32_t{b[1]} << 8) | b[0];
        at_ += 4u;
        return true;
    }
    bool U16(uint16_t* out) {
        if (at_ + 2u > bytes_.size()) {
            return false;
        }
        const uint8_t* b = bytes_.data() + at_;
        *out = big_endian_ ? static_cast<uint16_t>((b[0] << 8) | b[1]) : static_cast<uint16_t>((b[1] << 8) | b[0]);
        at_ += 2u;
        return true;
    }
    bool U8(uint8_t* out) {
        if (at_ >= bytes_.size()) {
            return false;
        }
        *out = bytes_[at_++];
        return true;
    }
    bool Skip(size_t n) {
        if (at_ + n > bytes_.size()) {
            return false;
        }
        at_ += n;
        return true;
    }
    size_t At() const { return at_; }
    void Seek(size_t at) { at_ = at; }
    const uint8_t* Data() const { return bytes_.data(); }
    size_t Size() const { return bytes_.size(); }

private:
    const std::vector<uint8_t>& bytes_;
    bool big_endian_;
    size_t at_ = 0;
};

/// The store's word for a file attribute's packed word (id bits 0-26, type 27-29, two high bits):
/// the deserialiser's own conversion (TU1 0x82bc8308, disc 0x82bc1c38) —
/// `(id << 5) | (type << 2) | (bit 30 << 1) | bit 31`.
uint32_t StoreWord(uint32_t p) {
    return ((((((p >> 2) | (p & 0xC0000000u)) >> 4) & 0x7FFFFFFu) | (p & 0xF8000000u)) >> 25 & 0x1Fu) | (p << 5);
}

/// The .bgf: header 0x20, then size-prefixed sections in order — Memory (19 counts: elements =
/// [0] + [1], strings = [6], slides = [14]), String, Element, Contract, Animation, Logic, Params,
/// Slide. An element record: name hash, parent ordinal, object id, type byte, flags byte (bit 2
/// adds a pad byte and a word), attribute count, 8-byte attribute records. A slide record: name
/// (16 bytes), owner ordinal, flags byte, start, end, entries (ordinal, active byte, commands
/// (8 bytes each), actions (4 bytes each)) — the loader's (TU1 `sub_82BC8F30`) own reads. A
/// component's slides are the run its own owner index names: base = the first one's index,
/// count = how many (`sub_82BB3778`).
std::shared_ptr<const FileTable> ParseBgfTable(const std::vector<uint8_t>& d) {
    constexpr size_t kSectionsStart = 0x20u;
    constexpr uint32_t kMemoryCounts = 19u;
    constexpr uint8_t kComponentFlag = 4u;
    constexpr uint32_t kStringValue = 5u;
    if (d.size() < kSectionsStart || std::memcmp(d.data() + 6, "AnarkBGF", 8) != 0) {
        return nullptr;
    }
    BgfReader r(d, d[0] == 1u);
    uint32_t version = 0;
    r.Seek(14u);
    if (!r.U32(&version) || version != 3u) {
        return nullptr;
    }
    r.Seek(kSectionsStart);
    uint32_t memory_size = 0;
    if (!r.U32(&memory_size)) {
        return nullptr;
    }
    const size_t memory_end = r.At() + memory_size;
    uint32_t counts[kMemoryCounts] = {};
    for (uint32_t i = 0; i < kMemoryCounts; ++i) {
        if (!r.U32(&counts[i])) {
            return nullptr;
        }
    }
    if (memory_end > d.size()) {
        return nullptr;
    }
    r.Seek(memory_end);
    const uint64_t element_count = uint64_t{counts[0]} + counts[1];
    const uint32_t string_count = counts[6];
    const uint32_t slide_count = counts[14];
    if (element_count > 0x100000u || string_count > 0x100000u || slide_count > 0x10000u) {
        return nullptr;
    }

    auto table = std::make_shared<FileTable>();
    uint32_t section = 0;
    if (!r.U32(&section)) {
        return nullptr;
    }
    table->strings.reserve(string_count);
    for (uint32_t i = 0; i < string_count; ++i) {
        uint32_t length = 0;
        if (!r.U32(&length) || r.At() + length > r.Size()) {
            return nullptr;
        }
        table->strings.emplace_back(reinterpret_cast<const char*>(r.Data() + r.At()), length);
        r.Skip(length);
    }

    if (!r.U32(&section)) {
        return nullptr;
    }
    table->elements.resize(static_cast<size_t>(element_count));
    table->children.resize(static_cast<size_t>(element_count));
    for (size_t i = 0; i < table->elements.size(); ++i) {
        FileTable::Element& e = table->elements[i];
        uint32_t parent = 0;
        uint32_t object = 0;
        uint8_t type = 0;
        uint32_t attributes = 0;
        if (!r.U32(&e.name) || !r.U32(&parent) || !r.U32(&object) || !r.U8(&type) || !r.U8(&e.flags)) {
            return nullptr;
        }
        if ((e.flags & kComponentFlag) != 0u && !r.Skip(5u)) {
            return nullptr;
        }
        if (!r.U32(&attributes) || attributes > 0x10000u) {
            return nullptr;
        }
        for (uint32_t a = 0; a < attributes; ++a) {
            uint32_t packed = 0;
            uint32_t value = 0;
            if (!r.U32(&packed) || !r.U32(&value)) {
                return nullptr;
            }
            if (((packed >> 27) & 7u) == kStringValue) {
                e.strings.emplace_back(packed & 0x07FFFFFFu, value);
            } else {
                e.records.emplace_back(StoreWord(packed), value);
            }
        }
        e.parent = static_cast<int32_t>(parent);
        if (e.parent >= 0 && static_cast<size_t>(e.parent) < i) {
            table->children[static_cast<size_t>(e.parent)].push_back(static_cast<int32_t>(i));
        }
    }

    // Contract, Animation, Logic, Params: framed, skipped.
    for (int s = 0; s < 4; ++s) {
        uint32_t size = 0;
        if (!r.U32(&size) || r.At() + size > r.Size()) {
            return table;  // the elements stand without slides
        }
        r.Seek(r.At() + size);
    }
    uint32_t slide_section = 0;
    if (!r.U32(&slide_section)) {
        return table;
    }
    std::vector<FileTable::Slide> slides;
    std::vector<int32_t> owners;
    slides.reserve(slide_count);
    for (uint32_t i = 0; i < slide_count; ++i) {
        FileTable::Slide slide;
        if (r.At() + 16u > r.Size()) {
            return table;
        }
        std::memcpy(slide.name, r.Data() + r.At(), 16u);
        r.Skip(16u);
        uint32_t owner = 0;
        uint32_t entries = 0;
        if (!r.U32(&owner) || !r.U8(&slide.flags) || !r.U32(&slide.start) || !r.U32(&slide.end) ||
            !r.U32(&entries) || entries > 0x10000u) {
            return table;
        }
        // Each entry (F3 `sub_82C30B38`): ordinal, active byte, commands (packed attribute word,
        // value), refs (4 bytes each); read past, the slides' playing is Godot's.
        for (uint32_t j = 0; j < entries; ++j) {
            uint32_t ordinal = 0;
            uint8_t active = 0;
            uint32_t commands = 0;
            uint32_t refs = 0;
            if (!r.U32(&ordinal) || !r.U8(&active) || !r.U32(&commands) || commands > 0x10000u ||
                !r.Skip(size_t{commands} * 8u) || !r.U32(&refs) || refs > 0x10000u || !r.Skip(size_t{refs} * 4u)) {
                return table;
            }
        }
        slides.push_back(slide);
        owners.push_back(static_cast<int32_t>(owner));
    }
    // Each owner's run, as the builder numbers it: base at its first slide, count of its slides.
    for (size_t i = 0; i < owners.size(); ++i) {
        const int32_t owner = owners[i];
        if (owner < 0 || static_cast<size_t>(owner) >= table->elements.size()) {
            continue;
        }
        FileTable::Element& e = table->elements[static_cast<size_t>(owner)];
        if (e.slide_count == 0u) {
            e.slide_base = static_cast<uint16_t>(i);
        }
        if (e.slide_count < 0xFFu) {
            ++e.slide_count;
        }
    }
    table->slides = std::move(slides);
    return table;
}

/// The file a headless presentation names, read once per path (`GAME:\data\…` → the mount's
/// `data/…`, then without `data/`).
std::shared_ptr<const FileTable> FileTableFor(const std::string& file, const char* title) {
    static std::mutex m;
    static std::unordered_map<std::string, std::shared_ptr<const FileTable>> tables;
    std::string path = file;
    std::replace(path.begin(), path.end(), '\\', '/');
    const size_t device = path.find(':');
    const size_t slash = path.find('/');
    if (device != std::string::npos && (slash == std::string::npos || device < slash)) {
        path.erase(0, device + 1);
    }
    while (!path.empty() && path.front() == '/') {
        path.erase(0, 1);
    }
    std::string key = path;
    std::transform(key.begin(), key.end(), key.begin(),
                   [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    {
        std::lock_guard<std::mutex> lock(m);
        const auto it = tables.find(key);
        if (it != tables.end()) {
            return it->second;
        }
    }
    std::shared_ptr<const FileTable> table;
    std::vector<uint8_t> bytes;
    const bool data = key.rfind("data/", 0) == 0;
    if (refii::vfs::ReadHostAsset(path, bytes) || (data && refii::vfs::ReadHostAsset(path.substr(5), bytes))) {
        table = ParseBgfTable(bytes);
    }
    size_t records = 0;
    if (table) {
        for (const FileTable::Element& e : table->elements) {
            records += e.records.size();
        }
    }
    REXLOG_INFO("Refii headless presentation ({}): '{}' {} ({} element(s), {} numeric attribute(s), {} "
                "slide(s), {} byte(s))",
                title, file,
                table ? "read: its elements answer the file's children, attributes, flags and slides"
                      : "DID NOT READ — its elements answer no children, attributes or slides",
                table ? table->elements.size() : 0u, records, table ? table->slides.size() : 0u, bytes.size());
    std::lock_guard<std::mutex> lock(m);
    tables[key] = table;
    return table;
}

// ---------------------------------------------------------------------------------------------
// The live presentations and their elements.

struct Headless {
    uint32_t presentation = 0;
    uint32_t root = 0;
    uint32_t block = 0;
    uint32_t gameface = 0;
    uint32_t serial = 0;
    int32_t start_ms = 0;
    bool said_500 = false;
    bool said_1000 = false;
    std::string file;
    std::vector<uint32_t> elements;          // id - 1; the root is id 0
    std::shared_ptr<const FileTable> table;  // null when the file did not read
    std::unordered_map<uint32_t, Param> params;  // the Parameters store, by attribute id
    std::vector<Registration> registrations;     // vtable +108 less +112, in call order
    std::unordered_map<int32_t, uint32_t> handles;  // file ordinal -> handle
    std::vector<uint32_t> cells;                 // string attribute cells handed to the guest
};

struct ElementState {
    uint32_t presentation = 0;
    uint32_t id = 0;
    int32_t ordinal = kNoOrdinal;
    uint32_t name = 0;  // the name hash it was made for
};

/// A handle's timeline record, as the guest's lays it at element +48 (time, length, offset, pending)
/// and +64 (the flag byte: 0x04 paused, 0x10 restarted, 0x08 running, 0x02 wrapped, 0x20 ping-pong,
/// 0xC0 the play count).
struct Timeline {
    int32_t time = 0;
    int32_t length = 0;
    int32_t offset = 0;
    int32_t pending = 0;
    uint8_t flags = 0;
};

/// What the host holds for a handle (THE STATED ELEMENTS).
struct HandleState {
    uint32_t parent = 0;  // the parent's handle, 0 for the root
    uint16_t flags = 0;
    uint8_t slide_count = 0;
    uint8_t current = 0;
    uint8_t previous = 0;
    Timeline timeline;
    std::vector<std::pair<uint32_t, uint32_t>> stored;  // attribute id -> the last value stored
    std::vector<std::pair<uint32_t, uint32_t>> cells;   // string attribute id -> its guest cell
};

std::mutex g_mutex;
std::vector<Headless> g_live;
std::unordered_map<uint32_t, ElementState> g_element_keys;
std::unordered_map<uint32_t, HandleState> g_handles;
uint32_t g_scene_vtable = 0;
uint32_t g_next_serial = 0;
std::atomic<uint32_t> g_inert_record{0};
std::atomic<uint64_t> g_made{0};
std::atomic<uint64_t> g_freed{0};

Headless* LiveLocked(uint32_t presentation) {
    for (Headless& e : g_live) {
        if (e.presentation == presentation) {
            return &e;
        }
    }
    return nullptr;
}

// ---------------------------------------------------------------------------------------------
// Guest calls.

PPCFunc* Resolve(const GamefaceTable& t, uint32_t address) {
    static std::mutex m;
    static std::unordered_map<uint32_t, PPCFunc*> cache;
    if (address == 0u || t.find == nullptr) {
        return nullptr;
    }
    std::lock_guard<std::mutex> lock(m);
    const auto it = cache.find(address);
    if (it != cache.end()) {
        return it->second;
    }
    PPCFunc* fn = t.find(address);
    cache.emplace(address, fn);
    if (fn == nullptr) {
        REXLOG_ERROR("Refii headless presentation ({}): guest function 0x{:08X} is not a recompiled entry",
                     t.title, address);
    }
    return fn;
}

/// One guest call on a copy of `ctx` (frames below `sp`); answers r3.
uint32_t CallAt(const GamefaceTable& t, PPCContext& ctx, uint8_t* base, uint32_t sp, uint32_t address,
                uint32_t r3, uint32_t r4 = 0u, uint32_t r5 = 0u, uint32_t r6 = 0u, uint32_t r7 = 0u) {
    PPCFunc* fn = Resolve(t, address);
    if (fn == nullptr) {
        return 0u;
    }
    PPCContext call = ctx;
    call.r1.u32 = sp;
    call.r3.u32 = r3;
    call.r4.u32 = r4;
    call.r5.u32 = r5;
    call.r6.u32 = r6;
    call.r7.u32 = r7;
    call.ctr.u32 = address;
    fn(call, base);
    return call.r3.u32;
}

uint32_t Call(const GamefaceTable& t, PPCContext& ctx, uint8_t* base, uint32_t address, uint32_t r3,
              uint32_t r4 = 0u, uint32_t r5 = 0u, uint32_t r6 = 0u, uint32_t r7 = 0u) {
    return CallAt(t, ctx, base, ctx.r1.u32, address, r3, r4, r5, r6, r7);
}

uint32_t Slot(const uint8_t* base, uint32_t object, uint32_t offset) {
    return LoadU32(base, LoadU32(base, object) + offset);
}

uint32_t Alloc(const GamefaceTable& t, PPCContext& ctx, uint8_t* base, uint32_t size) {
    return t.alloc != nullptr ? t.alloc(ctx, base, size) : 0u;
}

void Free(const GamefaceTable& t, PPCContext& ctx, uint8_t* base, uint32_t address) {
    if (address != 0u && t.free != nullptr) {
        t.free(ctx, base, address);
    }
}

int32_t ClockMs(const GamefaceTable& t, PPCContext& ctx, uint8_t* base, uint32_t gameface) {
    const uint32_t clock = gameface + t.gameface_clock;
    return static_cast<int32_t>(Call(t, ctx, base, Slot(base, clock, t.clock_slot_ms), clock));
}

std::string GuestCString(const uint8_t* base, uint32_t at) {
    std::string out;
    if (!Plausible(at)) {
        return out;
    }
    for (uint32_t i = 0; i < 260u; ++i) {
        const char c = static_cast<char>(base[at + i]);
        if (c == '\0') {
            break;
        }
        out.push_back(c);
    }
    return out;
}

/// The value a global id word holds, masked to the store's 27 bits; 0 when the title has none.
uint32_t IdAt(const GamefaceTable& t, const uint8_t* base, uint32_t global) {
    return global != 0u ? LoadU32(base, global) & t.attribute_id_mask : 0u;
}

bool EnsureSceneVtable(const GamefaceTable& t, PPCContext& ctx, uint8_t* base, uint32_t presentation) {
    if (g_scene_vtable != 0u) {
        return true;
    }
    const uint32_t nullsub = Slot(base, presentation, t.pres_slot_nullsub);
    const uint32_t scene = Alloc(t, ctx, base, t.scene_slots * 4u);
    if (scene == 0u) {
        return false;
    }
    for (uint32_t i = 0; i < t.scene_slots; ++i) {
        StoreU32(base, scene + 4u * i, LoadU32(base, t.scene_vtable + 4u * i));
    }
    StoreU32(base, scene + 4u * t.scene_slot_refresh, nullsub);
    g_scene_vtable = scene;
    const bool resolved = Resolve(t, nullsub) != nullptr &&
                          Resolve(t, LoadU32(base, t.scene_vtable + 4u * t.scene_slot_list)) != nullptr;
    REXLOG_INFO("Refii headless presentation ({}): scene vtable 0x{:08X} (copy of 0x{:08X}, refresh = "
                "0x{:08X}); slots resolve: {}",
                t.title, scene, t.scene_vtable, nullsub, resolved ? "yes" : "NO");
    return true;
}

void NotifyGameface(const GamefaceTable& t, PPCContext& ctx, uint8_t* base, uint32_t gameface, uint32_t code,
                    uint32_t presentation) {
    const uint32_t callback = LoadU32(base, gameface + t.gameface_callback);
    if (callback != 0u) {
        Call(t, ctx, base, callback, code, presentation, 0u, LoadU32(base, gameface + t.gameface_callback_ctx));
    }
}

void EnsureInertRecord(const GamefaceTable& t, PPCContext& ctx, uint8_t* base) {
    if (g_inert_record.load() != 0u || t.quad_record_ctor == 0u || t.quad_record_size == 0u) {
        return;
    }
    const uint32_t record = Alloc(t, ctx, base, t.quad_record_size);
    if (record == 0u) {
        return;
    }
    std::memset(base + record, 0, t.quad_record_size);
    Call(t, ctx, base, t.quad_record_ctor, record);
    uint32_t none = 0u;
    if (!g_inert_record.compare_exchange_strong(none, record)) {
        Free(t, ctx, base, record);
        return;
    }
    REXLOG_INFO("Refii headless presentation ({}): inert quad record 0x{:08X} ({} bytes, guest ctor 0x{:08X})",
                t.title, record, t.quad_record_size, t.quad_record_ctor);
}

// ---------------------------------------------------------------------------------------------
// Elements.

/// The flags a file element carries, as the deserialiser leaves them (`id_start` / `id_end` the start
/// and end time ids).
uint16_t FileFlags(const FileTable::Element& e, bool root, uint32_t id_start, uint32_t id_end) {
    uint16_t flags = (e.flags & 1u) != 0u ? kFlagActive : 0u;
    if (root || e.OwnsTimeline()) {
        flags |= kFlagOwnsTimeline;  // the root always: the timeline-owner walk has no null exit
    }
    for (const auto& [word, value] : e.records) {
        (void)value;
        const uint32_t id = word >> 5;
        if ((id_start != 0u && id == id_start) || (id_end != 0u && id == id_end)) {
            flags |= kFlagTimeWindow;
        }
    }
    return flags;
}

/// The parameter store, seeded with what the file's Parameters element (a root child of that name)
/// authors: numeric attributes as their value words, strings as text (caller holds g_mutex).
void SeedParamsLocked(Headless& entry, uint32_t parameters_hash, size_t* numbers, size_t* strings) {
    if (!entry.table || entry.table->elements.empty() || parameters_hash == 0u) {
        return;
    }
    const int32_t ordinal = entry.table->ChildNamed(0, parameters_hash);
    if (ordinal < 0) {
        return;
    }
    const FileTable::Element& e = entry.table->elements[static_cast<size_t>(ordinal)];
    for (const auto& [word, value] : e.records) {
        Param p;
        p.bits = value;
        entry.params[word >> 5] = std::move(p);
        ++*numbers;
    }
    for (const auto& [id, index] : e.strings) {
        Param p;
        p.text = true;
        if (index < entry.table->strings.size()) {
            p.string = entry.table->strings[index];
        }
        entry.params[id] = std::move(p);
        ++*strings;
    }
}

bool KeyOfLocked(uint32_t element, uint32_t* presentation, uint32_t* serial, uint32_t* id) {
    const auto it = g_element_keys.find(element);
    if (it == g_element_keys.end()) {
        return false;
    }
    const Headless* entry = LiveLocked(it->second.presentation);
    if (entry == nullptr) {
        return false;
    }
    *presentation = entry->presentation;
    *serial = entry->serial;
    *id = it->second.id;
    return true;
}


// ---------------------------------------------------------------------------------------------
// THE STATED ELEMENTS (core/headless_presentation.h).

/// The handle for file element `ordinal` of a stated presentation, made on first use (caller holds
/// g_mutex): a zeroed guest block, keyed and stated like any element (id, parent id, name).
uint32_t HandleLocked(const GamefaceTable& t, PPCContext& ctx, uint8_t* base, Headless& entry, int32_t ordinal) {
    const auto made = entry.handles.find(ordinal);
    if (made != entry.handles.end()) {
        return made->second;
    }
    if (!entry.table || ordinal < 0 || static_cast<size_t>(ordinal) >= entry.table->elements.size()) {
        return 0u;
    }
    const FileTable::Element& e = entry.table->elements[static_cast<size_t>(ordinal)];
    uint32_t parent = 0u;
    uint32_t parent_id = 0u;
    if (ordinal != 0) {
        if (e.parent < 0 || e.parent >= ordinal) {
            return 0u;
        }
        parent = HandleLocked(t, ctx, base, entry, e.parent);
        if (parent == 0u) {
            return 0u;
        }
        parent_id = g_element_keys[parent].id;
    }
    const uint32_t handle = Alloc(t, ctx, base, kHandleBytes);
    if (handle == 0u) {
        return 0u;
    }
    std::memset(base + handle, 0, kHandleBytes);
    uint32_t id = 0u;
    if (ordinal != 0) {
        entry.elements.push_back(handle);
        id = static_cast<uint32_t>(entry.elements.size());
    }
    entry.handles[ordinal] = handle;
    g_element_keys[handle] = ElementState{entry.presentation, id, ordinal, e.name};
    HandleState state;
    state.parent = parent;
    state.flags = FileFlags(e, ordinal == 0, IdAt(t, base, t.attr_start_time), IdAt(t, base, t.attr_end_time));
    state.slide_count = (state.flags & kFlagOwnsTimeline) != 0u ? e.slide_count : 0u;
    g_handles[handle] = std::move(state);
    if (ordinal != 0) {
        refii_bridge_told_headless_element(entry.presentation, entry.serial, id, parent_id, e.name);
    }
    return handle;
}

/// A live handle's state and its presentation's entry (caller holds g_mutex), or null.
HandleState* HandleLockedOf(uint32_t element, Headless** entry = nullptr, int32_t* ordinal = nullptr) {
    const auto state = g_handles.find(element);
    if (state == g_handles.end()) {
        return nullptr;
    }
    const auto key = g_element_keys.find(element);
    if (key == g_element_keys.end()) {
        return nullptr;
    }
    Headless* live = LiveLocked(key->second.presentation);
    if (live == nullptr) {
        return nullptr;
    }
    if (entry != nullptr) {
        *entry = live;
    }
    if (ordinal != nullptr) {
        *ordinal = key->second.ordinal;
    }
    return &state->second;
}

/// The timeline advanced to `clock` (TU1 `sub_82284DF8`, 0x82284df8..0x8228500c): true on the
/// play-once end edge.
bool Advance(Timeline& t, int32_t clock) {
    if ((t.flags & 0x04u) != 0u) {
        if ((t.flags & 0x10u) != 0u) {
            t.pending = clock;
        } else if ((t.flags & 0x02u) != 0u) {
            t.offset = t.time - clock;
        } else {
            t.offset = 2 * t.length - t.time - clock;
        }
        return false;
    }
    if ((t.flags & 0x10u) != 0u) {
        if (t.pending >= 0) {
            t.offset -= t.pending;
            t.pending = -1;
        } else {
            t.offset -= clock;
        }
        t.flags = static_cast<uint8_t>(t.flags & ~0x10u);
    }
    t.flags = static_cast<uint8_t>(t.flags | 0x08u);
    t.time = t.offset + clock;
    if (t.time < 0) {
        t.time = 0;
        t.flags = static_cast<uint8_t>(t.flags & ~0x08u);
        return false;
    }
    const bool counted = (t.flags & 0xC0u) != 0u && t.length != 0;
    if (counted && t.time > static_cast<int32_t>(t.flags >> 6) * t.length) {
        t.flags = static_cast<uint8_t>(t.flags & ~0x08u);
        t.time = static_cast<int32_t>(t.flags >> 6) * t.length;
    }
    if (t.length == 0) {
        return false;
    }
    if ((t.flags & 0x20u) == 0u && counted && t.time == static_cast<int32_t>(t.flags >> 6) * t.length) {
        t.time = t.length;
        return true;
    }
    if ((t.flags & 0x20u) != 0u && (t.time / t.length) % 2 >= 1) {
        t.time = t.length - t.time % t.length;
        t.flags = static_cast<uint8_t>(t.flags & ~0x02u);
        return false;
    }
    t.time -= (t.time / t.length) * t.length;
    t.flags = static_cast<uint8_t>(t.flags | 0x02u);
    return false;
}

}  // namespace

// ---------------------------------------------------------------------------------------------

namespace {

/// The handles, their cells, the scene block and the presentation block, after the entry left the
/// live list.
void DestroyStated(const GamefaceTable& t, PPCContext& ctx, uint8_t* base, Headless& entry) {
    std::vector<uint32_t> cells;
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        for (const auto& [ordinal, handle] : entry.handles) {
            (void)ordinal;
            const auto state = g_handles.find(handle);
            if (state != g_handles.end()) {
                for (const auto& [id, cell] : state->second.cells) {
                    (void)id;
                    cells.push_back(cell);
                }
                g_handles.erase(state);
            }
            g_element_keys.erase(handle);
        }
    }
    for (const uint32_t cell : cells) {
        Free(t, ctx, base, cell);
    }
    for (const auto& [ordinal, handle] : entry.handles) {
        (void)ordinal;
        Free(t, ctx, base, handle);
    }
    Free(t, ctx, base, entry.block);
    Free(t, ctx, base, entry.presentation);
}

}  // namespace

/// The zeroed presentation block (its three vtables, the scene, the root's handle at service +8) and
/// the host's record of it.
uint32_t Create(const GamefaceTable& t, PPCContext& ctx, uint8_t* base, uint32_t gameface, uint32_t path) {
    EnsureInertRecord(t, ctx, base);
    const std::string file = GuestCString(base, path);
    std::shared_ptr<const FileTable> table = FileTableFor(file, t.title);
    const uint32_t presentation = Alloc(t, ctx, base, t.presentation_size);
    const uint32_t block = Alloc(t, ctx, base, kBlockSize);
    bool scene_vtable = false;
    if (presentation != 0u) {
        std::memset(base + presentation, 0, t.presentation_size);
        StoreU32(base, presentation, t.presentation_vtable);
        std::lock_guard<std::mutex> lock(g_mutex);
        scene_vtable = EnsureSceneVtable(t, ctx, base, presentation);
    }
    if (presentation == 0u || block == 0u || !scene_vtable) {
        Free(t, ctx, base, presentation);
        Free(t, ctx, base, block);
        REXLOG_WARN("Refii headless presentation ({}): no guest heap for '{}'", t.title, file);
        return 0u;
    }
    std::memset(base + block, 0, kBlockSize);
    const uint32_t scene = block + kBlockScene;
    StoreU32(base, scene, g_scene_vtable);
    StoreU32(base, scene + t.scene_render_list, block + kBlockRenderList);
    StoreU32(base, presentation + t.pres_scene, scene);
    StoreU32(base, presentation + t.pres_service, t.service_vtable);
    StoreU32(base, presentation + t.pres_animation, t.animation_vtable);

    const uint32_t parameters = t.parameters_name != 0u ? LoadU32(base, t.parameters_name) : 0u;
    const uint32_t parameters_text = Plausible(parameters) ? LoadU32(base, parameters) : 0u;
    const uint32_t parameters_hash = Plausible(parameters_text) ? Call(t, ctx, base, t.name_hash, parameters_text) : 0u;
    const int32_t start_ms = ClockMs(t, ctx, base, gameface);

    uint32_t root = 0;
    uint32_t serial = 0;
    size_t parameters_numbers = 0;
    size_t parameters_strings = 0;
    size_t live = 0;
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        serial = ++g_next_serial & 0x7FFFFFFFu;
        Headless entry;
        entry.presentation = presentation;
        entry.block = block;
        entry.gameface = gameface;
        entry.serial = serial;
        entry.start_ms = start_ms;
        entry.file = file;
        entry.table = table;
        g_live.push_back(std::move(entry));
        Headless& made = g_live.back();
        refii_bridge_told_headless_presentation(presentation, serial, file.c_str(), static_cast<uint32_t>(file.size()));
        root = HandleLocked(t, ctx, base, made, 0);
        made.root = root;
        SeedParamsLocked(made, parameters_hash, &parameters_numbers, &parameters_strings);
        live = g_live.size();
    }
    StoreU32(base, presentation + t.pres_root, root);

    const uint64_t made = ++g_made;
    if (Milestone(made)) {
        REXLOG_INFO("Refii headless presentation ({}) #{}: 0x{:08X} '{}' serial {} on gameface 0x{:08X} — stated "
                    "elements, root handle 0x{:08X}, Parameters {} number(s) {} string(s), scene 0x{:08X}, clock {} "
                    "ms; live {}",
                    t.title, made, presentation, file, serial, gameface, root, parameters_numbers, parameters_strings,
                    scene, start_ms, live);
    }
    NotifyGameface(t, ctx, base, gameface, 2u, presentation);
    NotifyGameface(t, ctx, base, gameface, 0u, presentation);
    return presentation;
}

bool Destroy(const GamefaceTable& t, PPCContext& ctx, uint8_t* base, uint32_t gameface, uint32_t presentation) {
    Headless entry;
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        const auto it = std::find_if(g_live.begin(), g_live.end(),
                                     [&](const Headless& e) { return e.presentation == presentation; });
        if (it == g_live.end()) {
            return false;
        }
        entry = std::move(*it);
        g_live.erase(it);
    }
    refii_bridge_told_headless_gone(presentation, entry.serial);
    NotifyGameface(t, ctx, base, gameface, 1u, presentation);
    DestroyStated(t, ctx, base, entry);
    const uint64_t freed = ++g_freed;
    if (Milestone(freed)) {
        REXLOG_INFO("Refii headless presentation ({}): 0x{:08X} '{}' serial {} freed with {} handle(s) (#{})", t.title,
                    presentation, entry.file, entry.serial, entry.handles.size(), freed);
    }
    return true;
}

void DestroyRemaining(const GamefaceTable& t, PPCContext& ctx, uint8_t* base, uint32_t gameface) {
    std::vector<uint32_t> remaining;
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        for (const Headless& entry : g_live) {
            if (entry.gameface == gameface) {
                remaining.push_back(entry.presentation);
            }
        }
    }
    for (const uint32_t presentation : remaining) {
        Destroy(t, ctx, base, gameface, presentation);
    }
    if (!remaining.empty()) {
        REXLOG_INFO("Refii headless presentation ({}): {} left on gameface 0x{:08X} freed with it", t.title,
                    remaining.size(), gameface);
    }
}

size_t CountOn(uint32_t gameface) {
    std::lock_guard<std::mutex> lock(g_mutex);
    return static_cast<size_t>(
        std::count_if(g_live.begin(), g_live.end(), [&](const Headless& e) { return e.gameface == gameface; }));
}

void NoteRootRead(const GamefaceTable& t, PPCContext& ctx, uint8_t* base, uint32_t root) {
    if (root == 0u) {
        return;
    }
    uint32_t gameface = 0;
    uint32_t presentation = 0;
    int32_t start_ms = 0;
    bool said_500 = false;
    bool said_1000 = false;
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        const auto it = std::find_if(g_live.begin(), g_live.end(), [&](const Headless& e) { return e.root == root; });
        if (it == g_live.end()) {
            return;
        }
        gameface = it->gameface;
        presentation = it->presentation;
        start_ms = it->start_ms;
        said_500 = it->said_500;
        said_1000 = it->said_1000;
    }
    const int32_t time = std::max(0, ClockMs(t, ctx, base, gameface) - start_ms);
    StoreU32(base, root + kElemTime, static_cast<uint32_t>(time));
    const bool past_500 = time >= 500;
    const bool past_1000 = time >= 1000;
    if ((past_500 && !said_500) || (past_1000 && !said_1000)) {
        {
            std::lock_guard<std::mutex> lock(g_mutex);
            if (Headless* e = LiveLocked(presentation)) {
                e->said_500 = e->said_500 || past_500;
                e->said_1000 = e->said_1000 || past_1000;
            }
        }
        REXLOG_INFO("Refii headless presentation ({}): root 0x{:08X} of 0x{:08X} read at {} ms (past {} ms)", t.title,
                    root, presentation, time, past_1000 ? 1000 : 500);
    }
}

bool ElementKey(uint32_t element, uint32_t* presentation, uint32_t* serial, uint32_t* id) {
    std::lock_guard<std::mutex> lock(g_mutex);
    return KeyOfLocked(element, presentation, serial, id);
}

bool IsHeadless(uint32_t presentation) {
    std::lock_guard<std::mutex> lock(g_mutex);
    return LiveLocked(presentation) != nullptr;
}

int32_t FileOrdinal(uint32_t element) {
    std::lock_guard<std::mutex> lock(g_mutex);
    const auto it = g_element_keys.find(element);
    if (it == g_element_keys.end() || LiveLocked(it->second.presentation) == nullptr) {
        return kNoOrdinal;
    }
    return it->second.ordinal;
}

uint32_t InertQuadRecord() { return g_inert_record.load(); }

bool TellAttribute(uint32_t element, uint32_t attribute, uint32_t value_bits) {
    uint32_t presentation = 0;
    uint32_t serial = 0;
    uint32_t id = 0;
    if (!ElementKey(element, &presentation, &serial, &id)) {
        return false;
    }
    refii_bridge_told_push(static_cast<uint32_t>(refii::vfs::ToldStatement::Kind::kAttribute), presentation, serial,
                           refii::vfs::kHeadlessOrdinal | id, attribute, value_bits);
    return true;
}

bool TellActive(uint32_t element, bool on) {
    uint32_t presentation = 0;
    uint32_t serial = 0;
    uint32_t id = 0;
    if (!ElementKey(element, &presentation, &serial, &id)) {
        return false;
    }
    refii_bridge_told_push(static_cast<uint32_t>(refii::vfs::ToldStatement::Kind::kActive), presentation, serial,
                           refii::vfs::kHeadlessOrdinal | id, on ? 1u : 0u, 0u);
    return true;
}

bool TellGotoSlide(uint32_t element, uint32_t slide) {
    uint32_t presentation = 0;
    uint32_t serial = 0;
    uint32_t id = 0;
    if (!ElementKey(element, &presentation, &serial, &id)) {
        return false;
    }
    refii_bridge_told_push(static_cast<uint32_t>(refii::vfs::ToldStatement::Kind::kGotoSlide), presentation, serial,
                           refii::vfs::kHeadlessOrdinal | id, slide, 0u);
    return true;
}

bool TellPaused(uint32_t element, bool paused) {
    uint32_t presentation = 0;
    uint32_t serial = 0;
    uint32_t id = 0;
    if (!ElementKey(element, &presentation, &serial, &id)) {
        return false;
    }
    refii_bridge_told_push(static_cast<uint32_t>(refii::vfs::ToldStatement::Kind::kPaused), presentation, serial,
                           refii::vfs::kHeadlessOrdinal | id, paused ? 1u : 0u, 0u);
    return true;
}

bool TellTexture(uint32_t element, uint32_t hash) {
    uint32_t presentation = 0;
    uint32_t serial = 0;
    uint32_t id = 0;
    if (!ElementKey(element, &presentation, &serial, &id)) {
        return false;
    }
    refii_bridge_told_push(static_cast<uint32_t>(refii::vfs::ToldStatement::Kind::kTexture), presentation, serial,
                           refii::vfs::kHeadlessOrdinal | id, hash, 0u);
    return true;
}

bool TellText(uint32_t element, const std::vector<uint16_t>& units) {
    uint32_t presentation = 0;
    uint32_t serial = 0;
    uint32_t id = 0;
    if (!ElementKey(element, &presentation, &serial, &id)) {
        return false;
    }
    refii_bridge_told_text_push(presentation, serial, refii::vfs::kHeadlessOrdinal | id, units.data(),
                                static_cast<uint32_t>(units.size()));
    return true;
}

// ---------------------------------------------------------------------------------------------
// The parameter store and the event registrations.

uint32_t NameHash(const char* text) {
    if (text == nullptr || text[0] == '\0') {
        return 0u;
    }
    uint32_t hash = 0u;
    for (int i = 0; i < 100; ++i) {
        const int32_t c = static_cast<int8_t>(text[i]);
        hash = hash * 65599u + static_cast<uint32_t>(c);
        if (c == 0) {
            break;
        }
    }
    return hash;
}

bool ParamGet(uint32_t presentation, uint32_t id, Param* out) {
    std::lock_guard<std::mutex> lock(g_mutex);
    const Headless* entry = LiveLocked(presentation);
    if (entry == nullptr) {
        return false;
    }
    const auto it = entry->params.find(id);
    if (it == entry->params.end()) {
        return false;
    }
    *out = it->second;
    return true;
}

bool ParamSetNumber(uint32_t presentation, uint32_t id, uint32_t bits) {
    std::lock_guard<std::mutex> lock(g_mutex);
    Headless* entry = LiveLocked(presentation);
    if (entry == nullptr) {
        return false;
    }
    const auto it = entry->params.find(id);
    if (it == entry->params.end() || it->second.text) {
        return false;
    }
    it->second.bits = bits;
    return true;
}

bool ParamSetText(uint32_t presentation, uint32_t id, const std::string& text) {
    std::lock_guard<std::mutex> lock(g_mutex);
    Headless* entry = LiveLocked(presentation);
    if (entry == nullptr) {
        return false;
    }
    const auto it = entry->params.find(id);
    if (it == entry->params.end() || !it->second.text) {
        return false;
    }
    it->second.string = text;
    return true;
}

void NoteRegistration(uint32_t presentation, const Registration& registration, bool add) {
    std::lock_guard<std::mutex> lock(g_mutex);
    Headless* entry = LiveLocked(presentation);
    if (entry == nullptr) {
        return;
    }
    std::vector<Registration>& list = entry->registrations;
    if (add) {
        list.push_back(registration);
        return;
    }
    // +112 removes the first matching entry (`sub_82BB2600`: element, id, then {fn, ctx}).
    const auto it = std::find_if(list.begin(), list.end(), [&](const Registration& r) {
        return r.element == registration.element && r.id == registration.id && r.fn == registration.fn &&
               r.ctx == registration.ctx;
    });
    if (it != list.end()) {
        list.erase(it);
    }
}

std::vector<Registration> Registrations(const std::string& stem, uint32_t parent_hash,
                                        uint32_t id, uint32_t* presentation) {
    std::vector<Registration> out;
    *presentation = 0u;
    auto lower = [](std::string text) {
        std::transform(text.begin(), text.end(), text.begin(),
                       [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
        return text;
    };
    const std::string wanted = lower(stem);
    std::lock_guard<std::mutex> lock(g_mutex);
    for (const Headless& entry : g_live) {
        std::string file = entry.file;
        std::replace(file.begin(), file.end(), '\\', '/');
        const size_t slash = file.rfind('/');
        if (slash != std::string::npos) {
            file = file.substr(slash + 1u);
        }
        const size_t dot = file.rfind('.');
        if (dot != std::string::npos) {
            file = file.substr(0, dot);
        }
        if (lower(file) != wanted) {
            continue;
        }
        *presentation = entry.presentation;
        for (const Registration& r : entry.registrations) {
            if (r.id != id) {
                continue;
            }
            const auto key = g_element_keys.find(r.element);
            const bool on_parent =
                parent_hash == 0u ? r.element == entry.root
                                  : key != g_element_keys.end() &&
                                        key->second.presentation == entry.presentation &&
                                        r.element != entry.root && key->second.name == parent_hash;
            if (!on_parent) {
                continue;
            }
            const bool seen = std::any_of(out.begin(), out.end(), [&](const Registration& o) {
                return o.fn == r.fn && o.ctx == r.ctx;
            });
            if (!seen) {
                out.push_back(r);
            }
        }
        if (!out.empty()) {
            break;
        }
    }
    return out;
}

// ---------------------------------------------------------------------------------------------
// THE STATED ELEMENTS: the answers.

uint32_t HandlePresentation(uint32_t element) {
    std::lock_guard<std::mutex> lock(g_mutex);
    Headless* entry = nullptr;
    return HandleLockedOf(element, &entry) != nullptr ? entry->presentation : 0u;
}

uint32_t StatedChildNamed(const GamefaceTable& t, PPCContext& ctx, uint8_t* base, uint32_t presentation,
                          uint32_t hash, uint32_t parent) {
    std::lock_guard<std::mutex> lock(g_mutex);
    Headless* entry = LiveLocked(presentation);
    if (entry == nullptr || !entry->table || entry->table->elements.empty()) {
        return 0u;
    }
    int32_t parent_ordinal = 0;
    if (parent == 0u) {
        // `if (!a3) { a3 = *(service + 8); if (*a3 == hash) return a3; }` (0x82bb5660..0x82bb5670).
        if (entry->table->elements[0].name == hash) {
            return entry->root;
        }
    } else {
        Headless* owner = nullptr;
        if (HandleLockedOf(parent, &owner, &parent_ordinal) == nullptr || owner != entry) {
            return 0u;
        }
    }
    const int32_t child = entry->table->ChildNamed(parent_ordinal, hash);
    if (child < 0) {
        static std::atomic<uint64_t> misses{0};
        const uint64_t n = ++misses;
        if (n <= 256u || Milestone(n)) {
            REXLOG_INFO("Refii headless presentation ({}): child-by-name 0x{:08X} under file #{} (0x{:08X}) of '{}' "
                        "is not in the file — answered 0 (#{})",
                        t.title, hash, parent_ordinal,
                        entry->table->elements[static_cast<size_t>(parent_ordinal)].name, entry->file, n);
        }
        return 0u;
    }
    return HandleLocked(t, ctx, base, *entry, child);
}

Answer StatedChildren(const GamefaceTable& t, PPCContext& ctx, uint8_t* base, uint32_t parent, bool all,
                      std::vector<uint32_t>* out) {
    std::lock_guard<std::mutex> lock(g_mutex);
    Headless* entry = nullptr;
    int32_t ordinal = kNoOrdinal;
    if (HandleLockedOf(parent, &entry, &ordinal) == nullptr) {
        return Answer::kNotHandle;
    }
    if (!entry->table || ordinal < 0 || static_cast<size_t>(ordinal) >= entry->table->children.size()) {
        return Answer::kNo;
    }
    for (const int32_t child : entry->table->children[static_cast<size_t>(ordinal)]) {
        const uint32_t handle = HandleLocked(t, ctx, base, *entry, child);
        if (handle == 0u) {
            continue;
        }
        if (all || (g_handles[handle].flags & 0x1000u) != 0u) {
            out->push_back(handle);
        }
    }
    return Answer::kYes;
}

Answer StatedParent(uint32_t element, uint32_t* parent) {
    std::lock_guard<std::mutex> lock(g_mutex);
    const HandleState* state = HandleLockedOf(element);
    if (state == nullptr) {
        return Answer::kNotHandle;
    }
    *parent = state->parent;
    return Answer::kYes;
}

Answer StatedRecord(const GamefaceTable& t, PPCContext& ctx, uint8_t* base, uint32_t element, uint32_t id,
                    uint32_t* word, uint32_t* value) {
    std::string text;
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        Headless* entry = nullptr;
        int32_t ordinal = kNoOrdinal;
        HandleState* state = HandleLockedOf(element, &entry, &ordinal);
        if (state == nullptr) {
            return Answer::kNotHandle;
        }
        if (!entry->table || ordinal < 0 || static_cast<size_t>(ordinal) >= entry->table->elements.size()) {
            return Answer::kNo;
        }
        const FileTable::Element& e = entry->table->elements[static_cast<size_t>(ordinal)];
        for (const auto& [record, authored] : e.records) {
            if ((record >> 5) != id) {
                continue;
            }
            *word = record;
            *value = authored;
            for (const auto& [stored_id, stored] : state->stored) {
                if (stored_id == id) {
                    *value = stored;
                }
            }
            return Answer::kYes;
        }
        const std::string* string = entry->table->StringAttribute(ordinal, id);
        if (string == nullptr) {
            return Answer::kNo;
        }
        *word = (id << 5) | (5u << 2);
        for (const auto& [cell_id, cell] : state->cells) {
            if (cell_id == id) {
                *value = cell;
                return Answer::kYes;
            }
        }
        text = *string;
    }
    // The deserialiser's string value: a cell whose first word is the text (`*v12` at 0x824250d4).
    const uint32_t cell = Alloc(t, ctx, base, 4u + static_cast<uint32_t>(text.size()) + 1u);
    if (cell == 0u) {
        return Answer::kNo;
    }
    StoreU32(base, cell, cell + 4u);
    std::memcpy(base + cell + 4u, text.data(), text.size());
    base[cell + 4u + text.size()] = 0u;
    std::lock_guard<std::mutex> lock(g_mutex);
    HandleState* state = HandleLockedOf(element);
    if (state == nullptr) {
        Free(t, ctx, base, cell);
        return Answer::kNo;
    }
    state->cells.emplace_back(id, cell);
    *value = cell;
    return Answer::kYes;
}

uint32_t RecordScratch(const GamefaceTable& t, PPCContext& ctx, uint8_t* base, uint32_t word, uint32_t value) {
    constexpr uint32_t kRecords = 64u;
    static std::atomic<uint32_t> ring{0u};
    static std::atomic<uint32_t> next{0u};
    uint32_t records = ring.load();
    if (records == 0u) {
        const uint32_t made = Alloc(t, ctx, base, kRecords * kAttributeRecord);
        if (made == 0u) {
            return 0u;
        }
        std::memset(base + made, 0, kRecords * kAttributeRecord);
        uint32_t none = 0u;
        if (!ring.compare_exchange_strong(none, made)) {
            Free(t, ctx, base, made);
        }
        records = ring.load();
    }
    const uint32_t at = records + (next.fetch_add(1u) % kRecords) * kAttributeRecord;
    StoreU32(base, at, word);
    StoreU32(base, at + 4u, value);
    return at;
}

Answer StatedStore(uint32_t element, uint32_t id, uint32_t value) {
    std::lock_guard<std::mutex> lock(g_mutex);
    Headless* entry = nullptr;
    int32_t ordinal = kNoOrdinal;
    HandleState* state = HandleLockedOf(element, &entry, &ordinal);
    if (state == nullptr) {
        return Answer::kNotHandle;
    }
    if (!entry->table || ordinal < 0 || static_cast<size_t>(ordinal) >= entry->table->elements.size()) {
        return Answer::kNo;
    }
    const FileTable::Element& e = entry->table->elements[static_cast<size_t>(ordinal)];
    const bool carried = std::any_of(e.records.begin(), e.records.end(),
                                     [&](const std::pair<uint32_t, uint32_t>& r) { return (r.first >> 5) == id; });
    if (!carried) {
        return Answer::kNo;
    }
    for (auto& [stored_id, stored] : state->stored) {
        if (stored_id == id) {
            stored = value;
            return Answer::kYes;
        }
    }
    state->stored.emplace_back(id, value);
    return Answer::kYes;
}

Answer StatedFlags(uint32_t element, uint16_t* flags) {
    std::lock_guard<std::mutex> lock(g_mutex);
    const HandleState* state = HandleLockedOf(element);
    if (state == nullptr) {
        return Answer::kNotHandle;
    }
    *flags = state->flags;
    return Answer::kYes;
}

Answer StatedFlagsWrite(uint32_t element, uint16_t mask, bool on) {
    std::lock_guard<std::mutex> lock(g_mutex);
    HandleState* state = HandleLockedOf(element);
    if (state == nullptr) {
        return Answer::kNotHandle;
    }
    state->flags = static_cast<uint16_t>(on ? (state->flags | mask) : (state->flags & ~mask));
    return Answer::kYes;
}

Answer StatedGotoSlide(uint32_t element, int32_t slide) {
    std::lock_guard<std::mutex> lock(g_mutex);
    HandleState* state = HandleLockedOf(element);
    if (state == nullptr) {
        return Answer::kNotHandle;
    }
    if (slide < 1 || slide >= static_cast<int32_t>(state->slide_count)) {
        return Answer::kNo;
    }
    // `sub_82BB5760` 0x82bb5880..0x82bb58a8: +44 = +43, +43 = slide, +56 = 0, +64 |= 0x10, +48 = 0.
    state->previous = state->current;
    state->current = static_cast<uint8_t>(slide);
    state->timeline.offset = 0;
    state->timeline.flags = static_cast<uint8_t>(state->timeline.flags | 0x10u);
    state->timeline.time = 0;
    return Answer::kYes;
}

Answer StatedCurrentSlide(uint32_t element, uint8_t* slide) {
    std::lock_guard<std::mutex> lock(g_mutex);
    const HandleState* state = HandleLockedOf(element);
    if (state == nullptr) {
        return Answer::kNotHandle;
    }
    *slide = state->current;
    return Answer::kYes;
}

Answer StatedSetTime(uint32_t element, int32_t time) {
    std::lock_guard<std::mutex> lock(g_mutex);
    HandleState* state = HandleLockedOf(element);
    if (state == nullptr) {
        return Answer::kNotHandle;
    }
    // `sub_82BB5B60`: +56 = +48 = time, +64 |= 0x10.
    state->timeline.offset = time;
    state->timeline.time = time;
    state->timeline.flags = static_cast<uint8_t>(state->timeline.flags | 0x10u);
    return Answer::kYes;
}

Answer StatedSetPaused(uint32_t element, bool paused) {
    std::lock_guard<std::mutex> lock(g_mutex);
    HandleState* state = HandleLockedOf(element);
    if (state == nullptr) {
        return Answer::kNotHandle;
    }
    // `sub_82BB5CB8`: +64 = (4 * paused) & 4 | +64 & 0xFB.
    state->timeline.flags = static_cast<uint8_t>((paused ? 0x04u : 0u) | (state->timeline.flags & 0xFBu));
    return Answer::kYes;
}

Answer StatedAdvance(uint32_t element, int32_t clock, int32_t* time, bool* ended) {
    std::lock_guard<std::mutex> lock(g_mutex);
    HandleState* state = HandleLockedOf(element);
    if (state == nullptr) {
        return Answer::kNotHandle;
    }
    *ended = Advance(state->timeline, clock);
    *time = state->timeline.time;
    return Answer::kYes;
}

Answer StatedOwner(uint32_t element, uint32_t* owner) {
    std::lock_guard<std::mutex> lock(g_mutex);
    const HandleState* state = HandleLockedOf(element);
    if (state == nullptr) {
        return Answer::kNotHandle;
    }
    uint32_t at = element;
    for (size_t steps = 0; state != nullptr && steps < 0x10000u; ++steps) {
        if ((state->flags & kFlagOwnsTimeline) != 0u) {
            *owner = at;
            return Answer::kYes;
        }
        at = state->parent;
        state = at != 0u ? HandleLockedOf(at) : nullptr;
    }
    return Answer::kNo;
}

Answer StatedSlideCount(uint32_t element, uint8_t* count) {
    std::lock_guard<std::mutex> lock(g_mutex);
    const HandleState* state = HandleLockedOf(element);
    if (state == nullptr) {
        return Answer::kNotHandle;
    }
    *count = state->slide_count;
    return Answer::kYes;
}

uint32_t StatedHandleAt(uint32_t presentation, int32_t ordinal) {
    std::lock_guard<std::mutex> lock(g_mutex);
    const Headless* entry = LiveLocked(presentation);
    if (entry == nullptr) {
        return 0u;
    }
    const auto it = entry->handles.find(ordinal);
    return it != entry->handles.end() ? it->second : 0u;
}

}  // namespace refii::headless
