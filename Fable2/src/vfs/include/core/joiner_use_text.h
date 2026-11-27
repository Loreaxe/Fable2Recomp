// refii — THE TEXT OF AN ONLINE JOINER'S CARRIED USE (mod_lua_natives.cpp writes it, mod_events.cpp reads it).
//
// A joiner's use of a scripted object runs on the host for the host's hero two (core Lua
// RefiiCoop.AddScript): a placed thing's by name (`useat`), a carried item's as
//   joiner → host   carry|<record hash hex>|<class>[|<target uid hex>]
// on the household channel (`estate.do=`): use this item of hero two's as that script, with the hero's
// target as the joiner's hero had it (0 or absent for none). What the host's run shows its user goes to
// the joiner on THE ROUTE (core Lua). A statement that breaks a rule is dropped whole.

#ifndef REFII_CORE_JOINER_USE_TEXT_H
#define REFII_CORE_JOINER_USE_TEXT_H

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>

namespace refii::coop {

/// A script class name: 1..63 of [A-Za-z0-9_], not starting with a digit.
constexpr bool UseClassName(std::string_view name) {
  if (name.empty() || name.size() > 63 || (name[0] >= '0' && name[0] <= '9')) {
    return false;
  }
  for (const char c : name) {
    const bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
    if (!ok) {
      return false;
    }
  }
  return true;
}

/// The next `|`-separated field of `rest` (consumed), or nullopt when none is left.
inline std::optional<std::string_view> UseField(std::string_view& rest) {
  if (rest.empty()) {
    return std::nullopt;
  }
  const size_t bar = rest.find('|');
  const std::string_view field = rest.substr(0, bar);
  rest = bar == std::string_view::npos ? std::string_view{} : rest.substr(bar + 1);
  return field;
}

/// A hex field of 1..`digits` digits.
inline bool UseHexField(std::string_view field, uint64_t* out, size_t digits) {
  if (field.empty() || field.size() > digits) {
    return false;
  }
  uint64_t value = 0;
  for (const char c : field) {
    uint64_t digit = 0;
    if (c >= '0' && c <= '9') {
      digit = static_cast<uint64_t>(c - '0');
    } else if (c >= 'A' && c <= 'F') {
      digit = static_cast<uint64_t>(c - 'A' + 10);
    } else if (c >= 'a' && c <= 'f') {
      digit = static_cast<uint64_t>(c - 'a' + 10);
    } else {
      return false;
    }
    value = (value << 4) | digit;
  }
  *out = value;
  return true;
}

/// A `carry|…` statement (without its `carry|`): the item's record hash (not 0), the script class, and
/// the target's uid (0 when absent).
inline bool UseCarryParse(std::string_view message, uint32_t* hash, std::string* name, uint64_t* target) {
  std::string_view rest = message;
  const auto hash_field = UseField(rest);
  const auto class_field = UseField(rest);
  const auto target_field = UseField(rest);
  uint64_t value = 0;
  uint64_t aimed = 0;
  if (!hash_field || !class_field || !rest.empty() || !UseHexField(*hash_field, &value, 8) || value == 0u ||
      !UseClassName(*class_field) || (target_field && !UseHexField(*target_field, &aimed, 16))) {
    return false;
  }
  *hash = static_cast<uint32_t>(value);
  name->assign(class_field->data(), class_field->size());
  *target = aimed;
  return true;
}

}  // namespace refii::coop

#endif  // REFII_CORE_JOINER_USE_TEXT_H
