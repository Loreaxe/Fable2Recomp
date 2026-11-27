#pragma once

#include <cstdint>
#include <span>
#include <string>
#include <string_view>
#include <vector>

// THE GUEST-FACING HALF OF ACHIEVEMENTS, and nothing else. Godot owns the stores, the
// definitions, the persistence, the toast and the page (Engine_Achievements); this library is
// the seam the kernel hooks write into and the host bridge reads out of:
//
//   guest XGIUserWriteAchievements  -> RecordAchievementWrites  -> GetAchievementWritesSnapshot
//   guest adoption points           -> BindUser                 -> GetBindingsSnapshot
//   guest XamUserCreateAchievementEnumerator <- SetSaveUnlocks (stated by Godot), per user
//   module launch                   -> SetTitleXdbf            -> GetTitleXdbfSnapshot
//   guest XamShowAchievementsUI     -> RequestAchievementsUi   -> GetAchievementsUiRequestCount
//
// ACHIEVEMENTS ARE PER CHARACTER. Both titles keep the "already unlocked" truth on the hero (Fable 2
// stats+941 / +2176, filled by sub_824DF1A8 at the Player binding, `lwz r4,0x28(r11)` 0x824DF2EC then
// XUserCreateAchievementEnumerator 0x824DF328; Fable 3 stats+1344, sub_823E3000 from SetupCamera,
// `lwz r4,0x28(r9)` 0x823E3128 then the enumerator 0x823E3164) and fill it from the enumerator of the
// user bound to that hero. So the enumerator answers each user from the save its hero came from: the
// guest's own adoption points state "user U now plays the hero of save S" (BindUser), Godot states
// every save's unlocked set (the set of the character that save holds), and a user with no save behind
// its hero yet — a new game, a hero no save holds — reads only what it earned under that binding.
// Every save is in the one shared profile: both local players pick from the same save set.
//
// Every entry point is safe from any thread and none touches the disk: the write hook runs on
// the guest thread that issued the XGI message, synchronously inside the kernel call.
namespace refii::achievement {

struct AchievementWrite {
  std::uint32_t user_index = 0;
  std::uint32_t achievement_id = 0;
};

struct AchievementUnlock {
  std::uint32_t user_index = 0;
  std::uint32_t achievement_id = 0;
  std::uint64_t sequence = 0;
  /// The serial of the user's binding when the guest wrote it (0: the user was never bound).
  std::uint64_t binding = 0;
};

/// "User `user_index` now plays the hero of save `save`" — `save` empty for a hero no save holds
/// yet (a new game, a new hero). Serials count up from 1.
struct UserBinding {
  std::uint64_t serial = 0;
  std::uint32_t user_index = 0;
  std::string save;
};

/// One save's unlocked set as the host states it: ids with their unlock FILETIMEs.
struct SaveUnlocks {
  std::string save;
  std::vector<std::uint32_t> ids;
  std::vector<std::uint64_t> filetimes;
};

/// Records the guest's writes, stamping each NEW (user, id, binding) with a monotonic sequence. A
/// write already recorded under the same binding this session is not stamped again; the same id
/// under a later binding (another hero) is.
void RecordAchievementWrites(std::span<const AchievementWrite> writes);

/// Every write recorded this session, in sequence order. A sequence, not a queue: the host holds
/// the last sequence it saw and asks for what is newer, so a repeated read loses nothing.
std::vector<AchievementUnlock> GetAchievementWritesSnapshot();

/// A guest adoption point: user `user_index`'s hero now comes from `save` ("" for a hero no save
/// holds yet). Returns the new serial, 0 for a user index out of range.
std::uint64_t BindUser(std::uint32_t user_index, std::string_view save);

/// Every binding of the session, in serial order.
std::vector<UserBinding> GetBindingsSnapshot();

/// Every save's unlocked set, stated by the host. Replaces the previous statement wholesale.
void SetSaveUnlocks(std::vector<SaveUnlocks> saves);

/// The unlock FILETIME of `achievement_id` for the hero user `user_index` plays, or 0 while locked:
/// the stated set of its binding's save, plus what the guest wrote this session under a binding to
/// the same save (under the same binding when there is no save behind the hero). A write lands as
/// unlocked at once, so the guest never reads back a write it just made as locked.
std::uint64_t GetUserAchievementUnlockTime(std::uint32_t user_index, std::uint32_t achievement_id);

/// The title's XDBF resource, copied out of the executable module once it is launched.
void SetTitleXdbf(std::span<const std::uint8_t> bytes);

std::vector<std::uint8_t> GetTitleXdbfSnapshot();

/// `XamShowAchievementsUI`: the guest asks for the achievements UI and the host draws it.
void RequestAchievementsUi(std::uint32_t user_index);

/// How many times the guest has asked this session.
std::uint32_t GetAchievementsUiRequestCount();

} // namespace refii::achievement

// The adoption points' C entry (the payload hooks reach rexruntime by symbol, as RefiiGuestBase):
// BindUser above.
extern "C" std::uint64_t RefiiAchievementBindUser(std::uint32_t user_index, const char* save);
