// refii — EACH HERO'S OWNED PROPERTIES, AS THE GUEST'S OWN CALLS STATE THEM.
//
// THIS FILE MUST BE IN librefii_vfs, NOT IN THE PPC HOOK SET (the rule of target_highlight_state.cpp beside
// it): the PAYLOAD writes (hooks/src/property_list_hooks.cpp, property_hooks.cpp, mod_events.cpp, on the
// guest's thread) and the HOST reads (RefiiHostGetProperties, on Godot's).
//
// Keyed by the owner's CECPlayerProperties (hero component 69) and the building's UID. Every entry was handed
// to the store by a call the guest made: a record built for a purchase or read by the loader (all its fields),
// a removal, a per-property setter (marital, rented, tenants and workers), or the guest re-asking one record's
// value, income or occupancy (the record pointer it was handed, matched to the record that entry was told
// with). A destroyed component drops its entries. Nothing here walks a guest list.

#include <cstdint>
#include <cstring>
#include <map>
#include <mutex>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

#include "core/payload_bridge_fable2.h"

namespace {

struct Owned {
  uint32_t record = 0u;
  RefiiStatedProperty said{};
};

std::mutex g_mutex;

/// props → (uid → entry), in UID order (a stable list order for the map's rows).
std::unordered_map<uint32_t, std::map<uint64_t, Owned>> g_owned;

/// The record a fill was told with → (props, uid).
std::unordered_map<uint32_t, std::pair<uint32_t, uint64_t>> g_records;

void Unrecord(uint32_t record) {
  if (record != 0u) {
    g_records.erase(record);
  }
}

}  // namespace

extern "C" void refii_bridge_property_list_clear(uint32_t props) {
  std::lock_guard<std::mutex> lock(g_mutex);
  const auto found = g_owned.find(props);
  if (found == g_owned.end()) {
    return;
  }
  for (const auto& [uid, owned] : found->second) {
    Unrecord(owned.record);
  }
  g_owned.erase(found);
}

extern "C" void refii_bridge_property_list_put(uint32_t props, uint32_t record, const RefiiStatedProperty* property) {
  if (props == 0u || property == nullptr || property->uid == 0u) {
    return;
  }
  std::lock_guard<std::mutex> lock(g_mutex);
  Owned& owned = g_owned[props][property->uid];
  Unrecord(owned.record);
  owned.record = record;
  owned.said = *property;
  owned.said.name[sizeof(owned.said.name) - 1u] = '\0';
  if (record != 0u) {
    g_records[record] = {props, property->uid};
  }
}

extern "C" void refii_bridge_property_list_remove(uint32_t props, uint64_t uid) {
  std::lock_guard<std::mutex> lock(g_mutex);
  const auto found = g_owned.find(props);
  if (found == g_owned.end()) {
    return;
  }
  const auto entry = found->second.find(uid);
  if (entry == found->second.end()) {
    return;
  }
  Unrecord(entry->second.record);
  found->second.erase(entry);
}

extern "C" void refii_bridge_property_list_flag(uint32_t props, uint64_t uid, uint32_t flag, int32_t on) {
  std::lock_guard<std::mutex> lock(g_mutex);
  const auto found = g_owned.find(props);
  if (found == g_owned.end()) {
    return;
  }
  const auto entry = found->second.find(uid);
  if (entry == found->second.end()) {
    return;
  }
  uint32_t& flags = entry->second.said.flags;
  flags = on != 0 ? (flags | flag) : (flags & ~flag);
}

extern "C" void refii_bridge_property_list_record_field(uint32_t record, int32_t field, int32_t value) {
  std::lock_guard<std::mutex> lock(g_mutex);
  const auto told = g_records.find(record);
  if (told == g_records.end()) {
    return;
  }
  const auto found = g_owned.find(told->second.first);
  if (found == g_owned.end()) {
    return;
  }
  const auto entry = found->second.find(told->second.second);
  if (entry == found->second.end()) {
    return;
  }
  RefiiStatedProperty& said = entry->second.said;
  switch (field) {
    case REFII_PROPERTY_FIELD_TENANTS:
      said.flags = value != 0 ? (said.flags | REFII_PROPERTY_TENANTS) : (said.flags & ~REFII_PROPERTY_TENANTS);
      break;
    case REFII_PROPERTY_FIELD_WORKERS:
      said.flags = value != 0 ? (said.flags | REFII_PROPERTY_WORKERS) : (said.flags & ~REFII_PROPERTY_WORKERS);
      break;
    case REFII_PROPERTY_FIELD_VALUE:
      said.value = value;
      break;
    case REFII_PROPERTY_FIELD_INCOME:
      said.income = value;
      break;
    default:
      break;
  }
}

extern "C" uint32_t refii_bridge_property_list_snapshot(uint32_t props, RefiiStatedProperty* out, uint32_t capacity) {
  std::lock_guard<std::mutex> lock(g_mutex);
  const auto found = g_owned.find(props);
  if (found == g_owned.end()) {
    return 0u;
  }
  uint32_t written = 0u;
  for (const auto& [uid, owned] : found->second) {
    if (out != nullptr && written < capacity) {
      out[written] = owned.said;
    }
    ++written;
  }
  return written;
}
