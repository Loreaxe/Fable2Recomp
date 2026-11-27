#include <achievement/achievement_runtime.h>

#include <array>
#include <atomic>
#include <chrono>
#include <map>
#include <mutex>
#include <set>
#include <string>
#include <tuple>
#include <unordered_map>
#include <utility>

namespace refii::achievement {
namespace {

constexpr std::uint32_t kUsers = 4;

/// Now as a Windows FILETIME — 100ns ticks since 1601-01-01 — the unit the enumerator returns.
std::uint64_t NowAsFileTime() {
  using namespace std::chrono;
  const auto since_epoch = system_clock::now().time_since_epoch();
  const std::uint64_t ticks =
      static_cast<std::uint64_t>(duration_cast<nanoseconds>(since_epoch).count() / 100);
  return ticks + 116444736000000000ull;
}

std::mutex g_mutex;
std::uint64_t g_next_sequence = 1;
std::vector<AchievementUnlock> g_writes;
std::vector<std::uint64_t> g_write_times;
/// (user, id, binding) of every recorded write, for the session de-dup.
std::set<std::tuple<std::uint32_t, std::uint32_t, std::uint64_t>> g_written;
std::vector<UserBinding> g_bindings;
std::array<std::uint64_t, kUsers> g_bound{};  // the serial in force per user, 0 = never bound
std::map<std::string, std::unordered_map<std::uint32_t, std::uint64_t>> g_save_unlocks;
std::vector<std::uint8_t> g_title_xdbf;
std::atomic<std::uint32_t> g_ui_requests{0};

const UserBinding* BindingLocked(const std::uint64_t serial) {
  return serial == 0 || serial > g_bindings.size() ? nullptr : &g_bindings[serial - 1];
}

/// True when a write made under `write_binding` (by `write_user`) is the hero of `binding` (user
/// `user`): the same save, or, for a hero no save holds, the same binding (the same never-bound user).
bool SameHeroLocked(const std::uint64_t write_binding, const std::uint64_t binding, const std::uint32_t write_user,
                    const std::uint32_t user) {
  if (write_binding == binding) {
    return binding != 0 || write_user == user;
  }
  const UserBinding* a = BindingLocked(write_binding);
  const UserBinding* b = BindingLocked(binding);
  return a != nullptr && b != nullptr && !a->save.empty() && a->save == b->save;
}

}  // namespace

void RecordAchievementWrites(const std::span<const AchievementWrite> writes) {
  const std::lock_guard<std::mutex> lock(g_mutex);
  for (const AchievementWrite &write : writes) {
    const std::uint64_t binding = write.user_index < kUsers ? g_bound[write.user_index] : 0;
    if (!g_written.emplace(write.user_index, write.achievement_id, binding).second) {
      continue;
    }
    g_writes.push_back({write.user_index, write.achievement_id, g_next_sequence++, binding});
    g_write_times.push_back(NowAsFileTime());
  }
}

std::vector<AchievementUnlock> GetAchievementWritesSnapshot() {
  const std::lock_guard<std::mutex> lock(g_mutex);
  return g_writes;
}

std::uint64_t BindUser(const std::uint32_t user_index, const std::string_view save) {
  if (user_index >= kUsers) {
    return 0;
  }
  const std::lock_guard<std::mutex> lock(g_mutex);
  g_bindings.push_back({g_bindings.size() + 1, user_index, std::string(save)});
  g_bound[user_index] = g_bindings.size();
  return g_bindings.size();
}

std::vector<UserBinding> GetBindingsSnapshot() {
  const std::lock_guard<std::mutex> lock(g_mutex);
  return g_bindings;
}

void SetSaveUnlocks(std::vector<SaveUnlocks> saves) {
  std::map<std::string, std::unordered_map<std::uint32_t, std::uint64_t>> table;
  const std::uint64_t now = NowAsFileTime();
  for (SaveUnlocks &save : saves) {
    auto &ids = table[std::move(save.save)];
    for (std::size_t i = 0; i < save.ids.size(); ++i) {
      const std::uint64_t when = i < save.filetimes.size() && save.filetimes[i] != 0 ? save.filetimes[i] : now;
      ids.emplace(save.ids[i], when);
    }
  }
  const std::lock_guard<std::mutex> lock(g_mutex);
  g_save_unlocks = std::move(table);
}

std::uint64_t GetUserAchievementUnlockTime(const std::uint32_t user_index, const std::uint32_t achievement_id) {
  if (user_index >= kUsers) {
    return 0;
  }
  const std::lock_guard<std::mutex> lock(g_mutex);
  const std::uint64_t serial = g_bound[user_index];
  if (const UserBinding* binding = BindingLocked(serial); binding != nullptr && !binding->save.empty()) {
    if (const auto save = g_save_unlocks.find(binding->save); save != g_save_unlocks.end()) {
      if (const auto it = save->second.find(achievement_id); it != save->second.end()) {
        return it->second;
      }
    }
  }
  for (std::size_t i = 0; i < g_writes.size(); ++i) {
    const AchievementUnlock &write = g_writes[i];
    if (write.achievement_id == achievement_id &&
        SameHeroLocked(write.binding, serial, write.user_index, user_index)) {
      return g_write_times[i];
    }
  }
  return 0;
}

void SetTitleXdbf(const std::span<const std::uint8_t> bytes) {
  const std::lock_guard<std::mutex> lock(g_mutex);
  g_title_xdbf.assign(bytes.begin(), bytes.end());
}

std::vector<std::uint8_t> GetTitleXdbfSnapshot() {
  const std::lock_guard<std::mutex> lock(g_mutex);
  return g_title_xdbf;
}

void RequestAchievementsUi(const std::uint32_t user_index) {
  (void)user_index;
  g_ui_requests.fetch_add(1, std::memory_order_relaxed);
}

std::uint32_t GetAchievementsUiRequestCount() {
  return g_ui_requests.load(std::memory_order_relaxed);
}

} // namespace refii::achievement

extern "C" std::uint64_t RefiiAchievementBindUser(const std::uint32_t user_index, const char* save) {
  return refii::achievement::BindUser(user_index, save != nullptr ? save : "");
}
