// The `GUI.*` natives a title's game Lua VM calls, captured with their arguments at that VM's
// `luaD_precall` (recognised by implementation pointer) — see docs/ui_native_cut.md. SHARED STATE,
// in librefii_runtime: a title's hooks (the payload, on the guest's threads) post through the
// refii_bridge_gui_native_* C entries, and the shared host exports (src/hostcore) drain. Taking a
// call does not suppress it — the guest's implementation still runs; suppression is opt-in per
// native. The contract is the VM's: a name, its arguments as the stack held them.

#ifndef REFII_RUNTIME_CORE_GUI_NATIVE_CALLS_H
#define REFII_RUNTIME_CORE_GUI_NATIVE_CALLS_H

#include <cstddef>
#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

#include "core/payload_bridge.h"

namespace refii::vfs {

/// One argument as the Lua stack held it. `lua_Number` is a 32-bit float in this build. A
/// string is a Babel tag (`TEXT_QUEST_COMPLETE`), not display text; the host resolves it.
struct GuiNativeArgument {
    enum class Kind : uint8_t {
        kNone = 0,
        kNumber = 1,
        kBoolean = 2,
        kString = 3,
        kOther = 4,
        kTable = 5,  ///< walked into `fields`
    };

    /// One named entry of a table argument. Named only, not nested; values are string,
    /// number or boolean.
    struct Field {
        std::string name;
        Kind kind = Kind::kNone;
        float number = 0.0f;
        bool boolean = false;
        std::string text;
    };

    Kind kind = Kind::kNone;
    float number = 0.0f;
    bool boolean = false;
    std::string text;
    std::vector<Field> fields;
};

/// One intercepted call.
struct GuiNativeCall {
    std::string name;
    std::vector<GuiNativeArgument> arguments;
    uint64_t sequence = 0;
};

/// Names the implementation a `GUI` registration is binding. Call for the `GUI` namespace
/// only: `sub_82458C88` runs once per binding over ~3,000 natives and a string read on each
/// hangs `Game_InitDatabases`.
inline void NoteGuiNativeRegistration(uint32_t implementation, std::string name) {
  refii_bridge_note_gui_native_registration(implementation, name.data(), name.size());
}

/// The registered name for an implementation, or empty when it is not a `GUI` native. Hot:
/// runs for every native the game VM calls.
inline const std::string* GuiNativeNameFor(uint32_t implementation) {
  static thread_local std::string cached;

  char stack[256];
  size_t needed = 0;
  if (refii_bridge_gui_native_name_for(implementation, stack, sizeof stack, &needed) == 0) {
    return nullptr;
  }

  if (needed <= sizeof stack) {
    cached.assign(stack, needed);
    return &cached;
  }

  cached.resize(needed);
  size_t written = 0;
  if (refii_bridge_gui_native_name_for(implementation, cached.data(), cached.size(),
                                       &written) == 0) {
    return nullptr;
  }
  cached.resize(written < cached.size() ? written : cached.size());
  return &cached;
}

/// True once any `GUI` registration has been seen, so the call hook can skip until then.
inline bool GuiNativesRegistered() { return refii_bridge_gui_natives_registered() != 0; }

/// What the host answers a predicate with, in place of the guest's own implementation.
enum class GuiNativeAnswerKind : uint8_t {
    kNone = 0,     ///< no answer held; the guest's implementation runs
    kBoolean = 1,  ///< `bits` is 0 or 1
    kFloat = 2,    ///< `bits` is the float's raw bits
    kSuppress = 3, ///< run nothing and return NO results — for a native that pushes no value
};

/// Holds the host's answer for one native, by name. Only answer a native whose setters the
/// host owns. An empty name or `kNone` hands it back to the guest.
void SetGuiNativeAnswer(const std::string& name, GuiNativeAnswerKind kind, uint32_t bits);

/// The host's answer for an implementation, or `kNone` when it should run as shipped.
inline GuiNativeAnswerKind GuiNativeAnswerFor(uint32_t implementation, uint32_t* out_bits) {
  return static_cast<GuiNativeAnswerKind>(
      refii_bridge_gui_native_answer_for(implementation, out_bits));
}

/// Names the implementation whose thunk is about to run, on this thread. Thread-local
/// because two Lua VMs run.
inline void NoteGuiNativeEntry(uint32_t implementation) {
  refii_bridge_note_gui_native_entry(implementation);
}

/// The implementation `NoteGuiNativeEntry` last named on this thread, or 0.
inline uint32_t CurrentGuiNativeEntry() {
  return refii_bridge_current_gui_native_entry();
}

namespace blob {

inline void PutBytes(std::vector<uint8_t>& out, const void* data, size_t size) {
  const uint8_t* bytes = static_cast<const uint8_t*>(data);
  out.insert(out.end(), bytes, bytes + size);
}

template <typename T>
inline void PutScalar(std::vector<uint8_t>& out, T value) {
  PutBytes(out, &value, sizeof value);
}

inline void PutString(std::vector<uint8_t>& out, const std::string& text) {
  PutScalar<uint32_t>(out, static_cast<uint32_t>(text.size()));
  PutBytes(out, text.data(), text.size());
}

struct Reader {
  const uint8_t* cursor = nullptr;
  const uint8_t* end = nullptr;
  bool ok = true;

  void Bytes(void* destination, size_t size) {
    if (!ok || static_cast<size_t>(end - cursor) < size) {
      ok = false;
      return;
    }
    std::memcpy(destination, cursor, size);
    cursor += size;
  }

  template <typename T>
  T Scalar() {
    T value{};
    Bytes(&value, sizeof value);
    return value;
  }

  std::string String() {
    const uint32_t size = Scalar<uint32_t>();
    if (!ok || static_cast<size_t>(end - cursor) < size) {
      ok = false;
      return {};
    }
    std::string text(reinterpret_cast<const char*>(cursor), size);
    cursor += size;
    return text;
  }

  bool CountFits(uint32_t count, size_t bytes_each) const {
    return ok && static_cast<size_t>(end - cursor) / bytes_each >= count;
  }
};

constexpr size_t kMinArgumentBytes = 1 + 4 + 1 + 4 + 4;
constexpr size_t kMinFieldBytes = 4 + 1 + 4 + 1 + 4;

inline void WriteCall(const GuiNativeCall& call, std::vector<uint8_t>& out) {
  out.clear();
  PutScalar<uint64_t>(out, call.sequence);
  PutString(out, call.name);
  PutScalar<uint32_t>(out, static_cast<uint32_t>(call.arguments.size()));

  for (const GuiNativeArgument& argument : call.arguments) {
    PutScalar<uint8_t>(out, static_cast<uint8_t>(argument.kind));
    PutScalar<float>(out, argument.number);
    PutScalar<uint8_t>(out, argument.boolean ? 1u : 0u);
    PutString(out, argument.text);
    PutScalar<uint32_t>(out, static_cast<uint32_t>(argument.fields.size()));

    for (const GuiNativeArgument::Field& field : argument.fields) {
      PutString(out, field.name);
      PutScalar<uint8_t>(out, static_cast<uint8_t>(field.kind));
      PutScalar<float>(out, field.number);
      PutScalar<uint8_t>(out, field.boolean ? 1u : 0u);
      PutString(out, field.text);
    }
  }
}

inline bool ReadCall(const uint8_t* data, size_t length, GuiNativeCall& out) {
  out = GuiNativeCall{};
  if (data == nullptr) {
    return false;
  }

  Reader reader{data, data + length, true};
  out.sequence = reader.Scalar<uint64_t>();
  out.name = reader.String();

  const uint32_t argument_count = reader.Scalar<uint32_t>();
  if (!reader.CountFits(argument_count, kMinArgumentBytes)) {
    return false;
  }

  out.arguments.reserve(argument_count);
  for (uint32_t i = 0; i < argument_count; ++i) {
    GuiNativeArgument argument;
    argument.kind = static_cast<GuiNativeArgument::Kind>(reader.Scalar<uint8_t>());
    argument.number = reader.Scalar<float>();
    argument.boolean = reader.Scalar<uint8_t>() != 0u;
    argument.text = reader.String();

    const uint32_t field_count = reader.Scalar<uint32_t>();
    if (!reader.CountFits(field_count, kMinFieldBytes)) {
      return false;
    }

    argument.fields.reserve(field_count);
    for (uint32_t f = 0; f < field_count; ++f) {
      GuiNativeArgument::Field field;
      field.name = reader.String();
      field.kind = static_cast<GuiNativeArgument::Kind>(reader.Scalar<uint8_t>());
      field.number = reader.Scalar<float>();
      field.boolean = reader.Scalar<uint8_t>() != 0u;
      field.text = reader.String();
      if (!reader.ok) {
        return false;
      }
      argument.fields.push_back(std::move(field));
    }

    if (!reader.ok) {
      return false;
    }
    out.arguments.push_back(std::move(argument));
  }

  return reader.ok;
}

}  // namespace blob

inline void PostGuiNativeCall(GuiNativeCall call) {
  std::vector<uint8_t> flattened;
  blob::WriteCall(call, flattened);
  refii_bridge_post_gui_native_call(flattened.data(), flattened.size());
}

/// Takes every pending call. Empties the queue.
std::vector<GuiNativeCall> TakeGuiNativeCalls();

/// Puts `taken[from..]` back at the front of the queue, in order (a drain whose buffers were full).
void PutBackGuiNativeCalls(std::vector<GuiNativeCall>& taken, std::size_t from);

/// How many calls have been intercepted and how many are waiting, for the host's report.
void GuiNativeCounters(uint64_t* out_intercepted, uint32_t* out_pending, uint32_t* out_named);

}  // namespace refii::vfs

#endif  // REFII_RUNTIME_CORE_GUI_NATIVE_CALLS_H
