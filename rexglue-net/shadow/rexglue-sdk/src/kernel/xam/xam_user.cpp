/**
 ******************************************************************************
 * Xenia : Xbox 360 Emulator Research Project                                 *
 ******************************************************************************
 * Copyright 2022 Ben Vanik. All rights reserved.                             *
 * Released under the BSD license - see LICENSE in the root for more details. *
 ******************************************************************************
 *
 * @modified    Tom Clay, 2026 - Adapted for ReXGlue runtime
 *
 * @modified    Ryan Fisher, 2026 - RexNet netplay integration
 */

// Disable warnings about unused parameters for kernel functions
#pragma GCC diagnostic ignored "-Wunused-parameter"


#include <chrono>
#include <cstring>

#include <achievement/achievement_runtime.h>
#include <atomic>
#include <rex/cvar.h>
#include <rex/input/input.h>
#include <rex/input/input_system.h>
#include <rex/runtime.h>
#include <rex/kernel/xam/private.h>
#include <rex/logging.h>
#include <rex/math.h>
#include <rex/hook.h>
#include <rex/types.h>
#include <rex/string.h>
#include <rex/system/kernel_state.h>
#include <rex/system/xam/user_profile.h>
#include <rex/system/xenumerator.h>
#include <rex/system/xio.h>
#include <rex/system/xthread.h>
#include <rex/system/xtypes.h>
#include <rex/thread.h>

REXCVAR_DEFINE_UINT32(user_language, 1, "Kernel", "User's language ID");

namespace rex {
namespace kernel {
namespace xam {
using namespace rex::system;
using namespace rex::system::xam;

// REFII SHADOW: PLAYER TWO ON THE SHARED PROFILE. Local co-op is one profile and one save set, so
// when a title asks to sign a second player in (XamShowSigninUI, below) the next connected pad's
// user is signed in LOCALLY to the same profile: the same settings, the same saves (the content
// layer has one root for every user index), never Live. Each user keeps its own XUID — the shared
// profile's with the user index in its lowest bits — because both titles tell users apart by XUID
// (Fable 3 maps an XUID back to its user by the low word, TU3 sub_8236AFA0
// `LODWORD(v13.xuid) == a2` 0x8236B058; Fable 2 keeps it on the Player, lhSceneNode_Create 0x82389AD8).
//
// WHO SIGNS IN: the user the title's own ask names. XamShowSigninUI names none, but both titles ask
// for one user and test that user after: Fable 2 `sub_82882AD8(user)` (TU1 / disc `sub_828867C0`,
// `1 << a1 & dword_83496A70` 0x82882B04 / 0x82882B98) with the co-op screen's +0x12C — player two's
// Player's +0x28, the pad that joined (`sub_82264230(netmgr, 2)` / `lwz r11,0x28(r3)` / `stw
// r11,0x12C(r31)` 0x8243770C..0x82437724); Fable 3's drop-in join `sub_82459A80(user, ..)` / disc
// `sub_8243A190` (the pad that pressed START) calls `sub_8236B410(.., user, ..)` 0x82459B30 and then
// tests that user's record 0x82459B54. The title's hooks state that user around their ask
// (RefiiXamSigninAskFor); with none stated the lowest connected pad not signed in is signed in.
//
// SIGNED OUT WHEN ITS PAD GOES. A user signed in here is signed out once its pad has been disconnected
// for kSignOutAfter (the shell states every pad's connection each frame, RefiiXamNotePad): the user's
// state drops to 0 and XN_SYS_SIGNINCHANGED goes out, as when a profile signs out at the dashboard.
// Each title then takes the change its own way — Fable 2 rebuilds its signed-in mask from
// XamUserGetSigninState (`sub_82184DA8` case 0xA, 0x82184FB4..0x82185054), Fable 3 re-reads its user
// records (`sub_821881C0` 0x82188240 -> `sub_8236B788`, then `+25` for the session's names,
// `sub_82197320` 0x82197378) — and the player plays on unsigned, as one who chose to continue without
// signing in; the title's next ask (Fable 2's co-op X, Fable 3's next drop-in) signs them in again.
// Player one (user 0, the profile's own) is never signed out.

/// Users 1..3 signed in to the shared profile, one bit each.
static std::atomic<uint32_t> g_local_signed_in{0};

/// The user the title's ask on this thread names (RefiiXamSigninAskFor), -1 none.
static thread_local int32_t t_signin_for = -1;

/// How long a signed-in user's pad stays disconnected before the user is signed out: longer than a
/// wireless pad's reconnect, so a blip signs nobody out.
static constexpr std::chrono::seconds kSignOutAfter{3};

/// Per user 1..3: when its pad was first seen disconnected while signed in (0: connected).
static std::atomic<int64_t> g_pad_gone_since[4]{};

static bool LocallySignedIn(uint32_t user_index) {
  return user_index >= 1 && user_index < 4 && (g_local_signed_in.load() & (1u << user_index)) != 0;
}

/// The shared profile when `user_index` is signed in to it, else null. User 0 is always the profile's.
static UserProfile* SignedInProfile(uint32_t user_index) {
  if (user_index != 0 && !LocallySignedIn(user_index)) {
    return nullptr;
  }
  return REX_KERNEL_STATE()->user_profile();
}

/// The XUID of `user_index`: the profile's own for user 0, the user index in its lowest bits for a
/// second player (bits 54/55 untouched: user_profile.cpp's permission mask).
static uint64_t UserXuid(uint32_t user_index, const UserProfile* profile) {
  return profile->xuid() ^ static_cast<uint64_t>(user_index);
}

/// The sign-in state the title reads for `user_index`: the profile's for user 0, signed in locally
/// (1) for a second player — never Live.
static uint32_t UserSigninState(uint32_t user_index, const UserProfile* profile) {
  return user_index == 0 ? profile->signin_state() : 1u;
}

/// A local user signed in to the shared profile whose XUID this is, or -1 (a remote player's).
static int32_t LocalUserOfXuid(uint64_t xuid) {
  const auto& profile = REX_KERNEL_STATE()->user_profile();
  for (uint32_t user = 0; user < 4; ++user) {
    if (SignedInProfile(user) != nullptr && UserXuid(user, profile) == xuid) {
      return static_cast<int32_t>(user);
    }
  }
  return -1;
}

i32 XamUserGetXUID_entry(u32 user_index, u32 type_mask, mapped_u64 xuid_ptr) {
  assert_true(type_mask == 1 || type_mask == 2 || type_mask == 3 || type_mask == 4 ||
              type_mask == 7);
  if (!xuid_ptr) {
    return X_E_INVALIDARG;
  }
  uint32_t result = X_E_NO_SUCH_USER;
  uint64_t xuid = 0;
  if (user_index < 4) {
    if (const auto user_profile = SignedInProfile(user_index)) {
      auto type = user_profile->type() & type_mask;
      if (type & (2 | 4)) {
        // maybe online profile?
        xuid = UserXuid(user_index, user_profile);
        result = X_E_SUCCESS;
      } else if (type & 1) {
        // maybe offline profile?
        xuid = UserXuid(user_index, user_profile);
        result = X_E_SUCCESS;
      }
    }
  } else {
    result = X_E_INVALIDARG;
  }
  *xuid_ptr = xuid;
  return result;
}

u32 XamUserGetSigninState_entry(u32 user_index) {
  uint32_t signin_state = 0;
  if (user_index < 4) {
    if (const auto user_profile = SignedInProfile(user_index)) {
      signin_state = UserSigninState(user_index, user_profile);
    }
    // Titles gate their whole Live stack on this; log transitions so netplay bring-up can see what
    // the guest saw (0 none / 1 local / 2 signed in to Live), per user.
    static uint32_t last_logged_state[4] = {UINT32_MAX, UINT32_MAX, UINT32_MAX, UINT32_MAX};
    const uint32_t user = static_cast<uint32_t>(user_index);
    if (signin_state != last_logged_state[user] && (user == 0 || signin_state != 0)) {
      last_logged_state[user] = signin_state;
      REXKRNL_INFO("XamUserGetSigninState({}) -> {}", user, signin_state);
    }
  }
  return signin_state;
}

typedef struct {
  rex::be<uint64_t> xuid;
  rex::be<uint32_t> unk08;  // maybe zero?
  rex::be<uint32_t> signin_state;
  rex::be<uint32_t> unk10;  // ?
  rex::be<uint32_t> unk14;  // ?
  char name[16];
} X_USER_SIGNIN_INFO;
static_assert_size(X_USER_SIGNIN_INFO, 40);

i32 XamUserGetSigninInfo_entry(u32 user_index, u32 flags, ppc_ptr_t<X_USER_SIGNIN_INFO> info) {
  if (!info) {
    return X_E_INVALIDARG;
  }

  std::memset(info, 0, sizeof(X_USER_SIGNIN_INFO));
  const auto user_profile = SignedInProfile(user_index);
  if (!user_profile) {
    return X_E_NO_SUCH_USER;
  }

  // unk08 (dwInfoFlags) stays 0: never XUSER_INFO_FLAG_GUEST (2) — Fable 3 counts a guest as not
  // signed in (TU3 sub_8236B788 0x8236B850 -> record[2]), and a player on the shared profile is none.
  info->xuid = UserXuid(user_index, user_profile);
  info->signin_state = UserSigninState(user_index, user_profile);
  rex::string::copy_truncating(info->name, user_profile->name(), rex::countof(info->name));
  return X_E_SUCCESS;
}

u32 XamUserGetName_entry(u32 user_index, mapped_string buffer, u32 buffer_len) {
  if (user_index >= 4) {
    return X_E_INVALIDARG;
  }

  const auto user_profile = SignedInProfile(user_index);
  if (!user_profile) {
    return X_E_NO_SUCH_USER;
  }

  const auto& user_name = user_profile->name();
  rex::string::copy_truncating(buffer, user_name, std::min(buffer_len, uint32_t(16)));
  return X_E_SUCCESS;
}

u32 XamUserGetGamerTag_entry(u32 user_index, mapped_wstring buffer, u32 buffer_len) {
  if (user_index >= 4) {
    return X_E_INVALIDARG;
  }

  const auto user_profile = SignedInProfile(user_index);
  if (!user_profile) {
    return X_E_NO_SUCH_USER;
  }

  if (!buffer || buffer_len < 16) {
    return X_E_INVALIDARG;
  }

  auto user_name = rex::string::to_utf16(user_profile->name());
  rex::string::copy_and_swap_truncating(buffer, user_name, std::min(buffer_len, uint32_t(16)));
  return X_E_SUCCESS;
}

typedef struct {
  rex::be<uint32_t> setting_count;
  rex::be<uint32_t> settings_ptr;
} X_USER_READ_PROFILE_SETTINGS;
static_assert_size(X_USER_READ_PROFILE_SETTINGS, 8);

// https://github.com/oukiar/freestyledash/blob/master/Freestyle/Tools/Generic/xboxtools.cpp
uint32_t XamUserReadProfileSettingsEx(uint32_t title_id, uint32_t user_index, uint32_t xuid_count,
                                      be<uint64_t>* xuids, uint32_t setting_count,
                                      be<uint32_t>* setting_ids, uint32_t unk,
                                      be<uint32_t>* buffer_size_ptr, uint8_t* buffer,
                                      XAM_OVERLAPPED* overlapped) {
  if (!xuid_count) {
    assert_null(xuids);
  } else {
    assert_true(xuid_count == 1);
    assert_not_null(xuids);
    // NOTE: any XUID is legal here, not just the signed-in user -- reading
    // another player's profile is what this API is for (see is_local_xuid).
    // TODO(gibbed): we assert here, but in case a title passes xuid_count > 1
    // until it's implemented for release builds...
    xuid_count = 1;
  }
  assert_zero(unk);  // probably flags

  // must have at least 1 to 32 settings
  if (setting_count < 1 || setting_count > 32) {
    return X_ERROR_INVALID_PARAMETER;
  }

  // buffer size pointer must be valid
  if (!buffer_size_ptr) {
    return X_ERROR_INVALID_PARAMETER;
  }

  // if buffer size is non-zero, buffer pointer must be valid
  auto buffer_size = static_cast<uint32_t>(*buffer_size_ptr);
  if (buffer_size && !buffer) {
    return X_ERROR_INVALID_PARAMETER;
  }

  uint32_t needed_header_size = 0;
  uint32_t needed_data_size = 0;
  for (uint32_t i = 0; i < setting_count; ++i) {
    needed_header_size += sizeof(X_USER_PROFILE_SETTING);
    UserProfile::Setting::Key setting_key;
    setting_key.value = static_cast<uint32_t>(setting_ids[i]);
    switch (static_cast<UserProfile::Setting::Type>(setting_key.type)) {
      case UserProfile::Setting::Type::WSTRING:
      case UserProfile::Setting::Type::BINARY:
        needed_data_size += setting_key.size;
        break;
      default:
        break;
    }
  }
  if (xuids) {
    needed_header_size *= xuid_count;
    needed_data_size *= xuid_count;
  }
  needed_header_size += sizeof(X_USER_READ_PROFILE_SETTINGS);

  uint32_t needed_size = needed_header_size + needed_data_size;
  if (!buffer || buffer_size < needed_size) {
    if (!buffer_size) {
      *buffer_size_ptr = needed_size;
    }
    return X_ERROR_INSUFFICIENT_BUFFER;
  }

  // Title ID = 0 means us.
  // 0xfffe07d1 = profile?

  if (!xuids && !SignedInProfile(user_index)) {
    // Only signed-in local users (the shared profile's).
    if (overlapped) {
      REX_KERNEL_STATE()->CompleteOverlappedImmediate(
          REX_KERNEL_MEMORY()->HostToGuestVirtual(overlapped), X_ERROR_NO_SUCH_USER);
      return X_ERROR_IO_PENDING;
    }
    return X_ERROR_NO_SUCH_USER;
  }

  const auto& user_profile = REX_KERNEL_STATE()->user_profile();

  // A title may read settings for ANY XUID, not just the signed-in user --
  // that is the point of this API. In a multiplayer session the host reads a
  // joining player's profile to populate their gamercard. We hold no local
  // copy of a remote player's profile, so report the requested settings as
  // "not set" (from = 0) with the requested XUID echoed back. Absent profile
  // data for a remote player is a normal response on hardware and titles
  // handle it; asserting local-only here aborted the host the moment a peer
  // joined.
  const int32_t xuid_user = xuids ? LocalUserOfXuid(static_cast<uint64_t>(xuids[0])) : -1;
  const bool is_local_xuid = !xuids || xuid_user >= 0;

  // First call asks for size (fill buffer_size_ptr).
  // Second call asks for buffer contents with that size.

  // TODO(gibbed): setting validity checking without needing a user profile
  // object. Only meaningful for the local profile; a remote XUID reports
  // every setting as unset rather than failing the call.
  bool any_missing = false;
  if (is_local_xuid) {
    for (uint32_t i = 0; i < setting_count; ++i) {
      auto setting_id = static_cast<uint32_t>(setting_ids[i]);
      auto setting = user_profile->GetSetting(setting_id);
      if (!setting) {
        any_missing = true;
        REXKRNL_ERROR(
            "xeXamUserReadProfileSettingsEx requested unimplemented setting "
            "{:08X}",
            setting_id);
      }
    }
  }
  if (any_missing) {
    // TODO(benvanik): don't fail? most games don't even check!
    if (overlapped) {
      REX_KERNEL_STATE()->CompleteOverlappedImmediate(
          REX_KERNEL_MEMORY()->HostToGuestVirtual(overlapped), X_ERROR_INVALID_PARAMETER);
      return X_ERROR_IO_PENDING;
    }
    return X_ERROR_INVALID_PARAMETER;
  }

  auto out_header = reinterpret_cast<X_USER_READ_PROFILE_SETTINGS*>(buffer);
  auto out_setting = reinterpret_cast<X_USER_PROFILE_SETTING*>(&out_header[1]);
  out_header->setting_count = static_cast<uint32_t>(setting_count);
  out_header->settings_ptr = REX_KERNEL_MEMORY()->HostToGuestVirtual(out_setting);

  UserProfile::SettingByteStream out_stream(REX_KERNEL_MEMORY()->HostToGuestVirtual(buffer), buffer,
                                            buffer_size, needed_header_size);
  for (uint32_t n = 0; n < setting_count; ++n) {
    uint32_t setting_id = setting_ids[n];
    UserProfile::Setting* setting = is_local_xuid ? user_profile->GetSetting(setting_id) : nullptr;

    std::memset(out_setting, 0, sizeof(X_USER_PROFILE_SETTING));
    out_setting->from = !setting || !setting->is_set ? 0 : setting->is_title_specific() ? 2 : 1;
    if (xuids) {
      out_setting->xuid = static_cast<uint64_t>(xuids[0]);
    } else {
      out_setting->user_index = static_cast<uint32_t>(user_index);
    }
    out_setting->setting_id = setting_id;

    if (setting && setting->is_set) {
      setting->Append(&out_setting->data, &out_stream);
    }
    ++out_setting;
  }

  if (overlapped) {
    REX_KERNEL_STATE()->CompleteOverlappedImmediate(
        REX_KERNEL_MEMORY()->HostToGuestVirtual(overlapped), X_ERROR_SUCCESS);
    return X_ERROR_IO_PENDING;
  }
  return X_ERROR_SUCCESS;
}

u32 XamUserReadProfileSettings_entry(u32 title_id, u32 user_index, u32 xuid_count, mapped_u64 xuids,
                                     u32 setting_count, mapped_u32 setting_ids,
                                     mapped_u32 buffer_size_ptr, mapped_void buffer_ptr,
                                     ppc_ptr_t<XAM_OVERLAPPED> overlapped) {
  return XamUserReadProfileSettingsEx(title_id, user_index, xuid_count, xuids, setting_count,
                                      setting_ids, 0, buffer_size_ptr, buffer_ptr, overlapped);
}

u32 XamUserReadProfileSettingsEx_entry(u32 title_id, u32 user_index, u32 xuid_count,
                                       mapped_u64 xuids, u32 setting_count, mapped_u32 setting_ids,
                                       mapped_u32 buffer_size_ptr, u32 unk_2,
                                       mapped_void buffer_ptr,
                                       ppc_ptr_t<XAM_OVERLAPPED> overlapped) {
  return XamUserReadProfileSettingsEx(title_id, user_index, xuid_count, xuids, setting_count,
                                      setting_ids, unk_2, buffer_size_ptr, buffer_ptr, overlapped);
}

u32 XamUserWriteProfileSettings_entry(u32 title_id, u32 user_index, u32 setting_count,
                                      ppc_ptr_t<X_USER_PROFILE_SETTING> settings,
                                      ppc_ptr_t<XAM_OVERLAPPED> overlapped) {
  if (!setting_count || !settings) {
    return X_ERROR_INVALID_PARAMETER;
  }

  // A signed-in player two writes the shared profile's settings, as player one does: one profile.
  const auto user_profile = SignedInProfile(user_index);
  if (!user_profile) {
    if (overlapped) {
      REX_KERNEL_STATE()->CompleteOverlappedImmediate(overlapped.guest_address(),
                                                      X_ERROR_NO_SUCH_USER);
      return X_ERROR_IO_PENDING;
    }
    return X_ERROR_NO_SUCH_USER;
  }

  // Update and save settings.

  for (uint32_t n = 0; n < setting_count; ++n) {
    const X_USER_PROFILE_SETTING& setting = settings[n];

    auto setting_type = static_cast<UserProfile::Setting::Type>(setting.data.type);
    if (setting_type == UserProfile::Setting::Type::UNSET) {
      continue;
    }

    REXKRNL_DEBUG(
        "XamUserWriteProfileSettings: setting index [{}]:"
        " from={} setting_id={:08X} data.type={}",
        n, (uint32_t)setting.from, (uint32_t)setting.setting_id, setting.data.type);

    switch (setting_type) {
      case UserProfile::Setting::Type::CONTENT:
      case UserProfile::Setting::Type::BINARY: {
        uint8_t* binary_ptr = REX_KERNEL_MEMORY()->TranslateVirtual(setting.data.binary.ptr);
        size_t binary_size = setting.data.binary.size;
        std::vector<uint8_t> bytes;
        if (setting.data.binary.ptr) {
          // Copy provided data
          bytes.resize(binary_size);
          std::memcpy(bytes.data(), binary_ptr, binary_size);
        } else {
          // Data pointer was NULL, so just fill with zeroes
          bytes.resize(binary_size, 0);
        }
        user_profile->AddSetting(
            std::make_unique<xam::UserProfile::BinarySetting>(setting.setting_id, bytes));
      } break;
      case UserProfile::Setting::Type::WSTRING:
      case UserProfile::Setting::Type::DOUBLE:
      case UserProfile::Setting::Type::FLOAT:
      case UserProfile::Setting::Type::INT32:
      case UserProfile::Setting::Type::INT64:
      case UserProfile::Setting::Type::DATETIME:
      default: {
        REXKRNL_ERROR("XamUserWriteProfileSettings: Unimplemented data type {}", setting_type);
      } break;
    };
  }

  if (overlapped) {
    REX_KERNEL_STATE()->CompleteOverlappedImmediate(overlapped.guest_address(), X_ERROR_SUCCESS);
    return X_ERROR_IO_PENDING;
  }
  return X_ERROR_SUCCESS;
}

u32 XamUserCheckPrivilege_entry(u32 user_index, u32 mask, mapped_u32 out_value) {
  // checking all users?
  if (user_index != 0xFF) {
    if (user_index >= 4) {
      return X_ERROR_INVALID_PARAMETER;
    }

    if (user_index && !SignedInProfile(user_index)) {
      return X_ERROR_NO_SUCH_USER;
    }
  }

  // Privileges gate online features. A signed-in-to-Live profile (netplay
  // active) must report them GRANTED — titles treat a denied
  // XPRIVILEGE_MULTIPLAYER_SESSIONS (0xFE) as "this account may not play
  // online" and refuse to host/join sessions (Fable 2's NLivePresence
  // raises a fatal EError 6 and abandons its whole presence machine). When
  // only signed in locally (offline), keep denying so single-player titles
  // don't wander into online paths.
  // A second player on the shared profile is signed in locally only, so it is denied every privilege.
  const auto& profile = REX_KERNEL_STATE()->user_profile();
  const bool signed_in_to_live = (user_index == 0 || user_index == 0xFF) && profile &&
                                 profile->signin_state() == 2;  // eXamUserSigninState_SignedInToLive
  *out_value = signed_in_to_live ? 1 : 0;
  return X_ERROR_SUCCESS;
}

u32 XamUserContentRestrictionGetFlags_entry(u32 user_index, mapped_u32 out_flags) {
  if (user_index) {
    return X_ERROR_NO_SUCH_USER;
  }

  // No restrictions?
  *out_flags = 0;
  return X_ERROR_SUCCESS;
}

u32 XamUserContentRestrictionGetRating_entry(u32 user_index, u32 unk1, mapped_u32 out_unk2,
                                             mapped_u32 out_unk3) {
  if (user_index) {
    return X_ERROR_NO_SUCH_USER;
  }

  // Some games have special case paths for 3F that differ from the failure
  // path, so my guess is that's 'don't care'.
  *out_unk2 = 0x3F;
  *out_unk3 = 0;
  return X_ERROR_SUCCESS;
}

u32 XamUserContentRestrictionCheckAccess_entry(u32 user_index, u32 unk1, u32 unk2, u32 unk3,
                                               u32 unk4, mapped_u32 out_unk5, u32 overlapped_ptr) {
  *out_unk5 = 1;

  if (overlapped_ptr) {
    // TODO(benvanik): does this need the access arg on it?
    REX_KERNEL_STATE()->CompleteOverlappedImmediate(overlapped_ptr, X_ERROR_SUCCESS);
  }

  return X_ERROR_SUCCESS;
}

u32 XamUserIsOnlineEnabled_entry(u32 user_index) {
  return 1;
}

u32 XamUserGetMembershipTier_entry(u32 user_index) {
  if (user_index >= 4) {
    return X_ERROR_INVALID_PARAMETER;
  }
  if (user_index) {
    return X_ERROR_NO_SUCH_USER;
  }
  return 6 /* 6 appears to be Gold */;
}

u32 XamUserAreUsersFriends_entry(u32 user_index, u32 unk1, u32 unk2, mapped_u32 out_value,
                                 u32 overlapped_ptr) {
  uint32_t are_friends = 0;
  X_RESULT result;

  if (user_index >= 4) {
    result = X_ERROR_INVALID_PARAMETER;
  } else {
    if (const auto user_profile = SignedInProfile(user_index)) {
      if (UserSigninState(user_index, user_profile) == 0) {
        result = X_ERROR_NOT_LOGGED_ON;
      } else {
        // No friends!
        are_friends = 0;
        result = X_ERROR_SUCCESS;
      }
    } else {
      // Only support user 0.
      result = X_ERROR_NO_SUCH_USER;  // if user is local -> X_ERROR_NOT_LOGGED_ON
    }
  }

  if (out_value) {
    assert_true(!overlapped_ptr);
    *out_value = result == X_ERROR_SUCCESS ? are_friends : 0;
    return result;
  } else if (overlapped_ptr) {
    assert_true(!out_value);
    REX_KERNEL_STATE()->CompleteOverlappedImmediateEx(
        overlapped_ptr, result == X_ERROR_SUCCESS ? X_ERROR_SUCCESS : X_ERROR_FUNCTION_FAILED,
        X_HRESULT_FROM_WIN32(result), result == X_ERROR_SUCCESS ? are_friends : 0);
    return X_ERROR_IO_PENDING;
  } else {
    assert_always();
    return X_ERROR_INVALID_PARAMETER;
  }
}

u32 XamShowSigninUI_entry(u32 unk, u32 unk_mask) {
  // Mask values vary. Probably matching user types? Local/remote?

  // A SECOND PLAYER SIGNS IN TO THE SHARED PROFILE, with no box: the dashboard's sign-in signs in the
  // player at a connected controller, so the lowest connected pad whose user is not signed in is
  // signed in here. Both titles ask only for a user who is not signed in (Fable 2: the co-op screen's
  // X, sub_82438CF8 case 38 -> sub_82882AD8 `XamShowSigninUI(2, 0)` 0x82882B10, gated on the
  // signed-in mask 0x82882B04; Fable 3: the drop-in join's sign-in round sub_8236B410 before its
  // warning box) and re-read every user's state on the XN_SYS_SIGNINCHANGED below (Fable 2
  // sub_82184DA8 case 0xA rebuilds the mask from XamUserGetSigninState, 0x82184FB4..0x82185028; Fable 3
  // TU3 sub_821881C0 0x82188240 -> sub_8236B788).
  {
    auto* input = static_cast<rex::input::InputSystem*>(REX_KERNEL_STATE()->emulator()->input_system());
    const auto connected = [input](uint32_t user) {
      rex::input::X_INPUT_CAPABILITIES caps = {};
      return input != nullptr && input->GetCapabilities(user, 0, &caps) == X_ERROR_SUCCESS;
    };
    const auto sign_in = [&](uint32_t user, const char* why) {
      g_local_signed_in.fetch_or(1u << user);
      g_pad_gone_since[user].store(0);
      REXKRNL_INFO("XamShowSigninUI({}, {:#x}): user {} signed in to the shared profile '{}' (local, {:016X}), {}",
                   static_cast<uint32_t>(unk), static_cast<uint32_t>(unk_mask), user,
                   REX_KERNEL_STATE()->user_profile()->name(),
                   UserXuid(user, REX_KERNEL_STATE()->user_profile()), why);
    };
    const int32_t named = t_signin_for;
    if (named >= 1 && named < 4 && !LocallySignedIn(static_cast<uint32_t>(named)) &&
        connected(static_cast<uint32_t>(named))) {
      sign_in(static_cast<uint32_t>(named), "the user the title's ask names");
    } else {
      if (named >= 1 && named < 4 && !LocallySignedIn(static_cast<uint32_t>(named))) {
        REXKRNL_WARN("XamShowSigninUI: the title asked for user {}, whose pad is not connected", named);
      }
      for (uint32_t user = 1; user < 4 && named < 1; ++user) {
        if (!LocallySignedIn(user) && connected(user)) {
          sign_in(user, "the lowest connected pad not signed in");
          break;
        }
      }
    }
  }

  // The dashboard's sequence: XN_SYS_UI on, XN_SYS_SIGNINCHANGED, XN_SYS_UI off. A caller may
  // wait for BOTH edges — Fable 2's `sub_82882AD8` spins until XN_SYS_UI(1) sets its
  // UI-showing flag (0x82882b30), then until XN_SYS_UI(0) clears it; without the "on" edge
  // it never leaves the first spin.
  //
  // THE "OFF" EDGE COMES LATER, as the dashboard's UI closes later: only "on" goes now, and
  // XN_SYS_SIGNINCHANGED and "off" follow on the kernel's dispatch thread after the deferred
  // dialogs' delay. Fable 3 TU3's listener sub_821881C0 drains EVERY pending notification in one
  // pump (`bl XNotifyGetNext` 0x821882C0, `bne cr6, loc_82188210` 0x821882C8; XN_SYS_UI -> `stb r9,
  // 0x18(r31)` 0x82188234), so "on" and "off" queued together set and cleared the flag inside one pump
  // and sub_8236B410's first spin (`lbz r11,0x18(r30)` 0x8236B4A4..0x8236B4CC, pumping through
  // sub_821976B8 0x8236B4B8) never saw it rise. Disc's listener sub_821882E8 takes one
  // notification per pump (`bl XNotifyGetNext` 0x8218831C, every path to the return 0x821883EC), which
  // is why disc passed.
  REX_KERNEL_STATE()->BroadcastNotification(0x00000009, 1);
  REX_KERNEL_STATE()->PostToDispatchThread([]() {
    rex::thread::Sleep(std::chrono::milliseconds(100));
    REX_KERNEL_STATE()->BroadcastNotification(0x0000000A, 1);
    REX_KERNEL_STATE()->BroadcastNotification(0x00000009, 0);
    REXKRNL_INFO("XamShowSigninUI: the sign-in UI closed (XN_SYS_SIGNINCHANGED, XN_SYS_UI off)");
  });
  return X_ERROR_SUCCESS;
}

// TODO(gibbed): probably a FILETIME/LARGE_INTEGER, unknown currently
struct X_ACHIEVEMENT_UNLOCK_TIME {
  rex::be<uint32_t> unk_0;
  rex::be<uint32_t> unk_4;
};

struct X_ACHIEVEMENT_DETAILS {
  rex::be<uint32_t> id;
  rex::be<uint32_t> label_ptr;
  rex::be<uint32_t> description_ptr;
  rex::be<uint32_t> unachieved_ptr;
  rex::be<uint32_t> image_id;
  rex::be<uint32_t> gamerscore;
  X_ACHIEVEMENT_UNLOCK_TIME unlock_time;
  rex::be<uint32_t> flags;

  static const size_t kStringBufferSize = 464;
};
static_assert_size(X_ACHIEVEMENT_DETAILS, 36);

class XStaticAchievementEnumerator : public XEnumerator {
 public:
  struct AchievementDetails {
    uint32_t id;
    std::u16string label;
    std::u16string description;
    std::u16string unachieved;
    uint32_t image_id;
    uint32_t gamerscore;
    struct {
      uint32_t unk_0;
      uint32_t unk_4;
    } unlock_time;
    uint32_t flags;
  };

  XStaticAchievementEnumerator(KernelState* kernel_state, size_t items_per_enumerate,
                               uint32_t flags)
      : XEnumerator(kernel_state, items_per_enumerate,
                    sizeof(X_ACHIEVEMENT_DETAILS) +
                        (!!(flags & 7) ? X_ACHIEVEMENT_DETAILS::kStringBufferSize : 0)),
        flags_(flags) {}

  void AppendItem(AchievementDetails item) { items_.push_back(std::move(item)); }

  uint32_t WriteItems(uint32_t buffer_ptr, uint8_t* buffer_data, uint32_t* written_count) override {
    size_t count = std::min(items_.size() - current_item_, items_per_enumerate());
    if (!count) {
      return X_ERROR_NO_MORE_FILES;
    }

    size_t size = count * item_size();

    auto details = reinterpret_cast<X_ACHIEVEMENT_DETAILS*>(buffer_data);
    size_t string_offset = items_per_enumerate() * sizeof(X_ACHIEVEMENT_DETAILS);
    auto string_buffer =
        StringBuffer{buffer_ptr + static_cast<uint32_t>(string_offset), &buffer_data[string_offset],
                     count * X_ACHIEVEMENT_DETAILS::kStringBufferSize};
    for (size_t i = 0, o = current_item_; i < count; ++i, ++current_item_) {
      const auto& item = items_[current_item_];
      details[i].id = item.id;
      details[i].label_ptr = !!(flags_ & 1) ? AppendString(string_buffer, item.label) : 0;
      details[i].description_ptr =
          !!(flags_ & 2) ? AppendString(string_buffer, item.description) : 0;
      details[i].unachieved_ptr = !!(flags_ & 4) ? AppendString(string_buffer, item.unachieved) : 0;
      details[i].image_id = item.image_id;
      details[i].gamerscore = item.gamerscore;
      details[i].unlock_time.unk_0 = item.unlock_time.unk_0;
      details[i].unlock_time.unk_4 = item.unlock_time.unk_4;
      details[i].flags = item.flags;
    }

    if (written_count) {
      *written_count = static_cast<uint32_t>(count);
    }

    return X_ERROR_SUCCESS;
  }

 private:
  struct StringBuffer {
    uint32_t ptr;
    uint8_t* data;
    size_t remaining_bytes;
  };

  uint32_t AppendString(StringBuffer& sb, const std::u16string_view string) {
    size_t count = string.length() + 1;
    size_t size = count * sizeof(char16_t);
    if (size > sb.remaining_bytes) {
      assert_always();
      return 0;
    }
    auto ptr = sb.ptr;
    rex::string::copy_and_swap_truncating(reinterpret_cast<char16_t*>(sb.data), string, count);
    sb.ptr += static_cast<uint32_t>(size);
    sb.data += size;
    sb.remaining_bytes -= size;
    return ptr;
  }

 private:
  uint32_t flags_;
  std::vector<AchievementDetails> items_;
  size_t current_item_ = 0;
};

u32 XamUserCreateAchievementEnumerator_entry(u32 title_id, u32 user_index, u32 xuid, u32 flags,
                                             u32 offset, u32 count, mapped_u32 buffer_size_ptr,
                                             mapped_u32 handle_ptr) {
  if (!count || !buffer_size_ptr || !handle_ptr) {
    return X_ERROR_INVALID_PARAMETER;
  }

  if (user_index >= 4) {
    return X_ERROR_INVALID_PARAMETER;
  }

  size_t entry_size = sizeof(X_ACHIEVEMENT_DETAILS);
  if (flags & 7) {
    entry_size += X_ACHIEVEMENT_DETAILS::kStringBufferSize;
  }

  if (buffer_size_ptr) {
    *buffer_size_ptr = static_cast<uint32_t>(entry_size) * count;
  }

  auto e = object_ref<XStaticAchievementEnumerator>(
      new XStaticAchievementEnumerator(REX_KERNEL_STATE(), count, flags));
  auto result = e->Initialize(user_index, 0xFB, 0xB000A, 0xB000B, 0);
  if (XFAILED(result)) {
    return result;
  }

  // ACHIEVED | ACHIEVED_ONLINE flags the game checks to consider an achievement earned.
  constexpr uint32_t kAchievedFlags = 0x00030000;

  // Unlocks are what Godot states (Engine_Achievements pushes its store through the bridge)
  // plus anything the guest wrote this session — the seam, never the SDK's AchievementManager.
  // PER USER: the hero user_index plays, answered from the save its hero came from
  // (refii::achievement::BindUser at the guest's adoption points) — Fable 2 reads back at 0x824DF328,
  // Fable 3 at 0x823E3164, each for the user of the hero whose Player is being bound.
  auto fill_unlock = [user = static_cast<uint32_t>(user_index)](
                         XStaticAchievementEnumerator::AchievementDetails& item, uint32_t id,
                         const rex::system::KernelState* ks) {
    (void)ks;
    uint64_t ft = refii::achievement::GetUserAchievementUnlockTime(user, id);
    if (ft) {
      item.flags |= kAchievedFlags;
      item.unlock_time.unk_0 = static_cast<uint32_t>(ft & 0xFFFF'FFFF);
      item.unlock_time.unk_4 = static_cast<uint32_t>(ft >> 32);
    }
  };

  const auto* ks = REX_KERNEL_STATE();

  // Prefer the runtime store (populated from TOML or XDBF at boot) so that
  // dev-edited labels/descriptions are visible to the game's own queries.
  const auto store = ks->loaded_achievements();
  if (!store.empty()) {
    for (const auto& info : store) {
      auto item = XStaticAchievementEnumerator::AchievementDetails{
          info.id,
          rex::string::to_utf16(info.label),
          rex::string::to_utf16(info.description),
          rex::string::to_utf16(info.unachieved_description),
          info.image_id,
          info.gamerscore,
          {0, 0},
          info.flags};
      fill_unlock(item, info.id, ks);
      e->AppendItem(item);
    }
  } else {
    const util::XdbfGameData db = ks->title_xdbf();
    if (db.is_valid()) {
      const XLanguage language =
          db.GetExistingLanguage(static_cast<XLanguage>(REXCVAR_GET(user_language)));
      for (const util::XdbfAchievementTableEntry& entry : db.GetAchievements()) {
        auto item = XStaticAchievementEnumerator::AchievementDetails{
            entry.id,
            rex::string::to_utf16(db.GetStringTableEntry(language, entry.label_id)),
            rex::string::to_utf16(db.GetStringTableEntry(language, entry.description_id)),
            rex::string::to_utf16(db.GetStringTableEntry(language, entry.unachieved_id)),
            entry.image_id,
            entry.gamerscore,
            {0, 0},
            entry.flags};
        fill_unlock(item, entry.id, ks);
        e->AppendItem(item);
      }
    }
  }

  *handle_ptr = e->handle();
  return X_ERROR_SUCCESS;
}

u32 XamParseGamerTileKey_entry(mapped_u32 key_ptr, mapped_u32 out1_ptr, mapped_u32 out2_ptr,
                               mapped_u32 out3_ptr) {
  *out1_ptr = 0xC0DE0001;
  *out2_ptr = 0xC0DE0002;
  *out3_ptr = 0xC0DE0003;
  return X_ERROR_SUCCESS;
}

u32 XamReadTileToTexture_entry(u32 unknown, u32 title_id, u64 tile_id, u32 user_index,
                               mapped_void buffer_ptr, u32 stride, u32 height, u32 overlapped_ptr) {
  // TODO(gibbed): unknown=0,2,3,9
  if (!tile_id) {
    return X_ERROR_INVALID_PARAMETER;
  }

  size_t size = size_t(stride) * size_t(height);
  std::memset(buffer_ptr, 0xFF, size);

  if (overlapped_ptr) {
    REX_KERNEL_STATE()->CompleteOverlappedImmediate(overlapped_ptr, X_ERROR_SUCCESS);
    return X_ERROR_IO_PENDING;
  }
  return X_ERROR_SUCCESS;
}

u32 XamWriteGamerTile_entry(u32 arg1, u32 arg2, u32 arg3, u32 arg4, u32 arg5, u32 overlapped_ptr) {
  if (overlapped_ptr) {
    REX_KERNEL_STATE()->CompleteOverlappedImmediate(overlapped_ptr, X_ERROR_SUCCESS);
    return X_ERROR_IO_PENDING;
  }
  return X_ERROR_SUCCESS;
}

u32 XamSessionCreateHandle_entry(mapped_u32 handle_ptr) {
  *handle_ptr = 0xCAFEDEAD;
  return X_ERROR_SUCCESS;
}

u32 XamSessionRefObjByHandle_entry(u32 handle, mapped_u32 obj_ptr) {
  assert_true(handle == 0xCAFEDEAD);
  // TODO(PermaNull): Implement this properly,
  // For the time being returning 0xDEADF00D will prevent crashing.
  *obj_ptr = 0xDEADF00D;
  return X_ERROR_SUCCESS;
}

}  // namespace xam
}  // namespace kernel
}  // namespace rex

// The title's hooks name the user their sign-in ask is for, on the asking thread, around the ask
// (Fable 2 rex_lhGuiCoopScreen_SignInPlayerTwo, Fable 3 rex_CoopJoin_SetupHero); -1 clears it.
extern "C" void RefiiXamSigninAskFor(int32_t user) { rex::kernel::xam::t_signin_for = user; }

// The shell's pad publication (RefiiHostSetControllerState, every frame per pad): a user signed in here
// whose pad stays disconnected for kSignOutAfter is signed out (head note).
extern "C" void RefiiXamNotePad(uint32_t user, int32_t connected) {
  using namespace rex::kernel::xam;
  if (user < 1 || user >= 4) {
    return;
  }
  if (connected != 0 || !LocallySignedIn(user)) {
    g_pad_gone_since[user].store(0);
    return;
  }
  const int64_t now = std::chrono::duration_cast<std::chrono::milliseconds>(
                          std::chrono::steady_clock::now().time_since_epoch())
                          .count();
  int64_t since = g_pad_gone_since[user].load();
  if (since == 0) {
    g_pad_gone_since[user].store(now);
    return;
  }
  if (now - since < std::chrono::duration_cast<std::chrono::milliseconds>(kSignOutAfter).count()) {
    return;
  }
  g_pad_gone_since[user].store(0);
  if ((g_local_signed_in.fetch_and(~(1u << user)) & (1u << user)) == 0) {
    return;
  }
  REXKRNL_INFO("Refii sign-in: user {}'s pad has been disconnected for {} s: user {} signed out (XN_SYS_SIGNINCHANGED)",
               user, kSignOutAfter.count(), user);
  REX_KERNEL_STATE()->PostToDispatchThread([]() { REX_KERNEL_STATE()->BroadcastNotification(0x0000000A, 1); });
}

REX_EXPORT(__imp__XamUserGetXUID, rex::kernel::xam::XamUserGetXUID_entry)
REX_EXPORT(__imp__XamUserGetSigninState, rex::kernel::xam::XamUserGetSigninState_entry)
REX_EXPORT(__imp__XamUserGetSigninInfo, rex::kernel::xam::XamUserGetSigninInfo_entry)
REX_EXPORT(__imp__XamUserGetName, rex::kernel::xam::XamUserGetName_entry)
REX_EXPORT(__imp__XamUserGetGamerTag, rex::kernel::xam::XamUserGetGamerTag_entry)
REX_EXPORT(__imp__XamUserReadProfileSettings, rex::kernel::xam::XamUserReadProfileSettings_entry)
REX_EXPORT(__imp__XamUserReadProfileSettingsEx,
           rex::kernel::xam::XamUserReadProfileSettingsEx_entry)
REX_EXPORT(__imp__XamUserWriteProfileSettings, rex::kernel::xam::XamUserWriteProfileSettings_entry)
REX_EXPORT(__imp__XamUserCheckPrivilege, rex::kernel::xam::XamUserCheckPrivilege_entry)
REX_EXPORT(__imp__XamUserContentRestrictionGetFlags,
           rex::kernel::xam::XamUserContentRestrictionGetFlags_entry)
REX_EXPORT(__imp__XamUserContentRestrictionGetRating,
           rex::kernel::xam::XamUserContentRestrictionGetRating_entry)
REX_EXPORT(__imp__XamUserContentRestrictionCheckAccess,
           rex::kernel::xam::XamUserContentRestrictionCheckAccess_entry)
REX_EXPORT(__imp__XamUserIsOnlineEnabled, rex::kernel::xam::XamUserIsOnlineEnabled_entry)
REX_EXPORT(__imp__XamUserGetMembershipTier, rex::kernel::xam::XamUserGetMembershipTier_entry)
REX_EXPORT(__imp__XamUserAreUsersFriends, rex::kernel::xam::XamUserAreUsersFriends_entry)
REX_EXPORT(__imp__XamShowSigninUI, rex::kernel::xam::XamShowSigninUI_entry)
REX_EXPORT(__imp__XamUserCreateAchievementEnumerator,
           rex::kernel::xam::XamUserCreateAchievementEnumerator_entry)
REX_EXPORT(__imp__XamParseGamerTileKey, rex::kernel::xam::XamParseGamerTileKey_entry)
REX_EXPORT(__imp__XamReadTileToTexture, rex::kernel::xam::XamReadTileToTexture_entry)
REX_EXPORT(__imp__XamWriteGamerTile, rex::kernel::xam::XamWriteGamerTile_entry)
REX_EXPORT(__imp__XamSessionCreateHandle, rex::kernel::xam::XamSessionCreateHandle_entry)
REX_EXPORT(__imp__XamSessionRefObjByHandle, rex::kernel::xam::XamSessionRefObjByHandle_entry)

REX_EXPORT_STUB(__imp__XamUserAddRecentPlayer);
REX_EXPORT_STUB(__imp__XamUserAllowedToPostToSocialNetwork);
REX_EXPORT_STUB(__imp__XamUserCreateAvatarAssetEnumerator);
REX_EXPORT_STUB(__imp__XamUserCreatePlayerEnumerator);
REX_EXPORT_STUB(__imp__XamUserCreateStatsEnumerator);
REX_EXPORT_STUB(__imp__XamUserCreateTitlesPlayedEnumerator);
REX_EXPORT_STUB(__imp__XamUserFlushLogonQueue);
REX_EXPORT_STUB(__imp__XamUserGetAge);
REX_EXPORT_STUB(__imp__XamUserGetAgeGroup);
REX_EXPORT_STUB(__imp__XamUserGetCachedUserFlags);
REX_EXPORT_STUB(__imp__XamUserGetDeviceId);
REX_EXPORT_STUB(__imp__XamUserGetIndexFromXUID);
REX_EXPORT_STUB(__imp__XamUserGetMembershipTierFromXUID);
REX_EXPORT_STUB(__imp__XamUserGetOnlineCountryFromXUID);
REX_EXPORT_STUB(__imp__XamUserGetOnlineLanguageFromXUID);
REX_EXPORT_STUB(__imp__XamUserGetOnlineXUIDFromOfflineXUID);
REX_EXPORT_STUB(__imp__XamUserGetReportingInfo);
REX_EXPORT_STUB(__imp__XamUserGetRequestedUserIndexMask);
REX_EXPORT_STUB(__imp__XamUserGetSubscriptionType);
REX_EXPORT_STUB(__imp__XamUserGetUserFlags);
REX_EXPORT_STUB(__imp__XamUserGetUserFlagsFromXUID);
REX_EXPORT_STUB(__imp__XamUserGetUserIndexMask);
REX_EXPORT_STUB(__imp__XamUserGetUserTenure);
REX_EXPORT_STUB(__imp__XamUserGetUsersMissingAvatars);
REX_EXPORT_STUB(__imp__XamUserGetXUIDForTFA);
REX_EXPORT_STUB(__imp__XamUserInvalidateProfileSetting);
REX_EXPORT_STUB(__imp__XamUserIsGuest);
REX_EXPORT_STUB(__imp__XamUserIsLogonPreviewModeEnabled);
REX_EXPORT_STUB(__imp__XamUserIsParentalControlled);
REX_EXPORT_STUB(__imp__XamUserIsPartial);
REX_EXPORT_STUB(__imp__XamUserIsPartialProfile);
REX_EXPORT_STUB(__imp__XamUserIsUnsafeProgrammingAllowed);
REX_EXPORT_STUB(__imp__XamUserLockLogonPreviewMode);
REX_EXPORT_STUB(__imp__XamUserLogon);
REX_EXPORT_STUB(__imp__XamUserLogonEx);
REX_EXPORT_STUB(__imp__XamUserLookupDevice);
REX_EXPORT_STUB(__imp__XamUserNuiBind);
REX_EXPORT_STUB(__imp__XamUserNuiEnableBiometric);
REX_EXPORT_STUB(__imp__XamUserNuiGetEnrollmentIndex);
REX_EXPORT_STUB(__imp__XamUserNuiGetUserIndex);
REX_EXPORT_STUB(__imp__XamUserNuiGetUserIndexForBind);
REX_EXPORT_STUB(__imp__XamUserNuiGetUserIndexForSignin);
REX_EXPORT_STUB(__imp__XamUserNuiIsBiometricEnabled);
REX_EXPORT_STUB(__imp__XamUserNuiUnbind);
REX_EXPORT_STUB(__imp__XamUserOverrideBindingCallbacks);
REX_EXPORT_STUB(__imp__XamUserOverrideDeviceBindings);
REX_EXPORT_STUB(__imp__XamUserOverrideGlobalState);
REX_EXPORT_STUB(__imp__XamUserOverrideUserInfo);
REX_EXPORT_STUB(__imp__XamUserPrefetchProfileSettings);
REX_EXPORT_STUB(__imp__XamUserProfileSync);
REX_EXPORT_STUB(__imp__XamUserReadUserPreference);
REX_EXPORT_STUB(__imp__XamUserResetSubscriptionType);
REX_EXPORT_STUB(__imp__XamUserUnlockLogonPreviewMode);
REX_EXPORT_STUB(__imp__XamUserUpdateRecentPlayer);
REX_EXPORT_STUB(__imp__XamUserValidateAvatarManifest);
REX_EXPORT_STUB(__imp__XamUserWriteUserPreference);
REX_EXPORT_STUB(__imp__XamVerifyPasscode);
