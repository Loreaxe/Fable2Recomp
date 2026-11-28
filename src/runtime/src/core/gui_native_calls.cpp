#include "core/gui_native_calls.h"
#include "core/queue_put_back.h"

#include <atomic>
#include <cstring>
#include <mutex>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

#include <rex/logging.h>

namespace refii::vfs {
namespace {

/// The registration map: implementation pointer -> every name bound to it. Written once by
/// `RegisterGUI` at startup, read thereafter; the mutex only guards that write.
/// A vector because names alias: 120 registrations resolve to 114 implementations (e.g.
/// `RemoveCounter` and `RemoveTimer` are both `sub_82305918`). A call reports the first name;
/// registration order is source order in `sub_82307518`, so it is stable across runs.
std::unordered_map<uint32_t, std::vector<std::string>>& Names() {
    static std::unordered_map<uint32_t, std::vector<std::string>> names;
    return names;
}

std::mutex& NamesMutex() {
    static std::mutex mutex;
    return mutex;
}

/// Set once the first `GUI` registration lands, so the call hook bails during boot; a
/// per-call structure walk there hangs `Game_InitDatabases`.
std::atomic<bool> g_registered{false};

std::vector<GuiNativeCall>& Pending() {
    static std::vector<GuiNativeCall> pending;
    return pending;
}

std::mutex& PendingMutex() {
    static std::mutex mutex;
    return mutex;
}

std::atomic<uint64_t> g_intercepted{0};

/// Ceiling: the host drains once a frame and a script storm outruns that. The oldest is
/// dropped — the newest call is the screen's current state.
constexpr std::size_t kMaxPending = 512u;

}  // namespace

extern "C" void refii_bridge_note_gui_native_registration(uint32_t implementation,
                                                          const char* name_data,
                                                          size_t name_length) {
    std::string name = name_data != nullptr && name_length != 0u
                           ? std::string(name_data, name_length)
                           : std::string();

    if (implementation == 0 || name.empty()) {
        return;
    }

    std::lock_guard<std::mutex> lock(NamesMutex());
    std::vector<std::string>& names = Names()[implementation];
    for (const std::string& existing : names) {
        if (existing == name) {
            return;
        }
    }

    names.push_back(std::move(name));
    if (names.size() > 1u) {
        std::string all;
        for (const std::string& bound : names) {
            if (!all.empty()) {
                all += " = ";
            }
            all += bound;
        }

        REXLOG_INFO("Refii UI cut: GUI.{} share one implementation 0x{:08X} — a call to any of "
                    "them reports as '{}'",
                    all, implementation, names.front());
    }

    g_registered.store(true, std::memory_order_release);
}

extern "C" int32_t refii_bridge_gui_native_name_for(uint32_t implementation, char* out,
                                                    size_t capacity, size_t* out_length) {
    if (out_length != nullptr) {
        *out_length = 0;
    }
    if (implementation == 0 || !g_registered.load(std::memory_order_acquire)) {
        return 0;
    }

    std::lock_guard<std::mutex> lock(NamesMutex());
    auto found = Names().find(implementation);
    if (found == Names().end() || found->second.empty()) {
        return 0;
    }

    const std::string& name = found->second.front();
    if (out_length != nullptr) {
        *out_length = name.size();
    }
    if (out != nullptr && capacity != 0u) {
        const size_t copy = capacity < name.size() ? capacity : name.size();
        std::memcpy(out, name.data(), copy);
    }
    return 1;
}

extern "C" int32_t refii_bridge_gui_natives_registered() {
    return g_registered.load(std::memory_order_acquire) ? 1 : 0;
}

namespace {

struct GuiNativeAnswer {
    GuiNativeAnswerKind kind = GuiNativeAnswerKind::kNone;
    uint32_t bits = 0;
};

/// Answers keyed by name, resolved to an implementation at call time: the host only knows
/// names, and six natives share one implementation, so answering "RemoveTimer" must not
/// thereby answer "RemoveCounter".
std::unordered_map<std::string, GuiNativeAnswer>& Answers() {
    static std::unordered_map<std::string, GuiNativeAnswer> answers;
    return answers;
}

std::mutex& AnswersMutex() {
    static std::mutex mutex;
    return mutex;
}

thread_local uint32_t t_entry = 0;

/// How many answers are held, so a call with none held (nearly always) skips both locks. Kept
/// under `AnswersMutex`; read lock-free.
std::atomic<size_t> g_answer_count{0};

}  // namespace

void SetGuiNativeAnswer(const std::string& name, GuiNativeAnswerKind kind, uint32_t bits) {
    if (name.empty()) {
        return;
    }

    std::lock_guard<std::mutex> lock(AnswersMutex());
    if (kind == GuiNativeAnswerKind::kNone) {
        Answers().erase(name);
        g_answer_count.store(Answers().size(), std::memory_order_release);
        return;
    }

    GuiNativeAnswer& answer = Answers()[name];
    answer.kind = kind;
    answer.bits = bits;
    g_answer_count.store(Answers().size(), std::memory_order_release);
}

extern "C" uint32_t refii_bridge_gui_native_answer_for(uint32_t implementation,
                                                       uint32_t* out_bits) {
    const uint32_t none = static_cast<uint32_t>(GuiNativeAnswerKind::kNone);

    if (implementation == 0 || !g_registered.load(std::memory_order_acquire)) {
        return none;
    }

    // The same answer the empty check below gives, without the two locks every thunk call
    // would otherwise take.
    if (g_answer_count.load(std::memory_order_acquire) == 0u) {
        return none;
    }

    std::lock_guard<std::mutex> names(NamesMutex());
    auto found = Names().find(implementation);
    if (found == Names().end()) {
        return none;
    }

    std::lock_guard<std::mutex> lock(AnswersMutex());
    if (Answers().empty()) {
        return none;
    }

    // Every name bound to this implementation: aliases are one function and the call cannot
    // say which name it came in under.
    for (const std::string& name : found->second) {
        auto answer = Answers().find(name);
        if (answer != Answers().end()) {
            if (out_bits != nullptr) {
                *out_bits = answer->second.bits;
            }
            return static_cast<uint32_t>(answer->second.kind);
        }
    }

    return none;
}

extern "C" void refii_bridge_note_gui_native_entry(uint32_t implementation) {
    t_entry = implementation;
}

extern "C" uint32_t refii_bridge_current_gui_native_entry() { return t_entry; }

// A capture on this thread (refii_bridge_gui_capture_begin): each call posted while it is open is kept,
// flattened, instead of going to this machine's host — the statements another machine will make its own
// (Fable 2's villager card for an online joiner, coop_villager_use.cpp).
namespace {
thread_local std::vector<uint8_t>* t_capture = nullptr;
}

extern "C" void refii_bridge_gui_capture_begin(void) {
    delete t_capture;
    t_capture = new std::vector<uint8_t>();
}

extern "C" int32_t refii_bridge_gui_capturing(void) { return t_capture != nullptr ? 1 : 0; }

extern "C" uint32_t refii_bridge_gui_capture_end(uint8_t* out, uint32_t capacity) {
    if (t_capture == nullptr) {
        return 0u;
    }
    const uint32_t size = static_cast<uint32_t>(t_capture->size());
    if (out != nullptr && capacity >= size && size != 0u) {
        std::memcpy(out, t_capture->data(), size);
    }
    delete t_capture;
    t_capture = nullptr;
    return size;
}

extern "C" void refii_bridge_post_gui_native_call(const uint8_t* data, size_t length) {
    if (t_capture != nullptr) {
        if (data != nullptr && length != 0u && length <= 0xFFFFu) {
            t_capture->push_back(static_cast<uint8_t>(length & 0xFFu));
            t_capture->push_back(static_cast<uint8_t>(length >> 8));
            t_capture->insert(t_capture->end(), data, data + length);
        }
        return;
    }
    GuiNativeCall call;
    if (!blob::ReadCall(data, length, call)) {
        return;
    }

    call.sequence = g_intercepted.fetch_add(1, std::memory_order_relaxed) + 1u;

    std::lock_guard<std::mutex> lock(PendingMutex());
    if (Pending().size() >= kMaxPending) {
        Pending().erase(Pending().begin());
    }
    Pending().push_back(std::move(call));
}

std::vector<GuiNativeCall> TakeGuiNativeCalls() {
    std::vector<GuiNativeCall> taken;
    std::lock_guard<std::mutex> lock(PendingMutex());
    taken.swap(Pending());
    return taken;
}

void PutBackGuiNativeCalls(std::vector<GuiNativeCall>& taken, std::size_t from) {
    std::lock_guard<std::mutex> lock(PendingMutex());
    PutBackFront(Pending(), taken, from);
}

void GuiNativeCounters(uint64_t* out_intercepted, uint32_t* out_pending, uint32_t* out_named) {
    if (out_intercepted != nullptr) {
        *out_intercepted = g_intercepted.load(std::memory_order_relaxed);
    }
    if (out_pending != nullptr) {
        std::lock_guard<std::mutex> lock(PendingMutex());
        *out_pending = static_cast<uint32_t>(Pending().size());
    }
    if (out_named != nullptr) {
        std::lock_guard<std::mutex> lock(NamesMutex());
        *out_named = static_cast<uint32_t>(Names().size());
    }
}

}  // namespace refii::vfs
