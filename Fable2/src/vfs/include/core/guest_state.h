#pragma once

#include "vfs_hook_types.h"

#include <array>
#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include <rex/ppc.h>

namespace refii::vfs {
inline namespace detail {
extern uint32_t g_refii_resource_wrapper_vtable;
extern uint32_t g_refii_resource_wrapper_interface_vtable;

bool TrySilentLargePageReserve(PPCContext& ctx, uint8_t* base);
PPCFunc* FindGuestFunction(uint32_t guest_address);
bool SafeInvokeGuestCall(PPCFunc* fn, PPCContext& ctx, uint8_t* base);

void ConfigureRefiiVfsTracyPlots();
LooseAssetRuntimeStats& LooseStats();
bool ShouldLogLooseStatsMilestone(uint64_t value);
void LogLooseStatsMilestone(std::string_view label,
                            uint64_t value,
                            size_t stream_record_count);
inline void LogLooseStatsMilestone(std::string_view label, uint64_t value) {
  LogLooseStatsMilestone(label, value, 0);
}
void RecordRefiiOwnedStreamLifecycleOpen(std::string_view provider_name);

uint32_t CallVirtualAlloc(PPCContext& source_ctx,
                          uint8_t* base,
                          uint32_t address,
                          uint32_t size,
                          uint32_t allocation_type,
                          uint32_t protect);
uint32_t CallLhMalloc(PPCContext& source_ctx, uint8_t* base, uint32_t size);
void CallLhFree(PPCContext& source_ctx, uint8_t* base, uint32_t guest_address);
void AddRefGuestObject(PPCContext& source_ctx,
                       uint8_t* base,
                       uint32_t guest_object);
void ReleaseGuestObject(PPCContext& source_ctx,
                        uint8_t* base,
                        uint32_t guest_object);

size_t TryCopyGuestBytes(uint8_t* base,
                         uint32_t guest_address,
                         void* dst,
                         size_t length);
bool TryLoadGuestU32(uint8_t* base, uint32_t guest_address, uint32_t* out);
uint8_t LoadGuestU8(uint8_t* base, uint32_t guest_address);
uint16_t LoadGuestU16(uint8_t* base, uint32_t guest_address);
uint32_t LoadGuestU32(uint8_t* base, uint32_t guest_address);
uint64_t LoadGuestU64(uint8_t* base, uint32_t guest_address);
void StoreGuestU8(uint8_t* base, uint32_t guest_address, uint8_t value);
void StoreGuestU16(uint8_t* base, uint32_t guest_address, uint16_t value);
void StoreGuestU32(uint8_t* base, uint32_t guest_address, uint32_t value);
void StoreGuestU64(uint8_t* base, uint32_t guest_address, uint64_t value);

bool IsRefiiResourceWrapperPrimaryVTable(uint32_t vtable_guest);
bool IsRefiiResourceWrapperInterfaceVTable(uint32_t vtable_guest);
std::pair<uint32_t, uint32_t> EnsureRefiiResourceWrapperVTables(
    PPCContext& ctx,
    uint8_t* base);
bool CopyToStreamDestination(PPCContext& source_ctx,
                             uint8_t* base,
                             uint32_t destination_guest,
                             uint32_t destination_kind,
                             uint32_t source_guest,
                             uint32_t byte_count);

std::string ReadGuestWideStringObject(uint8_t* base,
                                      uint32_t string_guest,
                                      size_t max_chars);
std::string ReadGuestAsciiStringObject(uint8_t* base,
                                       uint32_t string_guest,
                                       size_t max_chars);
std::string FormatGuestWords(uint8_t* base,
                             uint32_t guest,
                             uint32_t word_count);
std::string FormatGuestAsciiBytes(uint8_t* base,
                                  uint32_t guest,
                                  uint32_t byte_count);
std::string ReadBestEffortGuestString(uint8_t* base,
                                      uint32_t string_guest,
                                      uint32_t max_chars);

/// The characters behind an LHString handle — EXACTLY TWO HOPS, never a search.
///
/// The guest takes these as `char***` and reaches the bytes with two dereferences and one
/// guard (`sub_826B9510`): `if (*a2) v10 = **a2;`. See the implementation for why an earlier
/// version that probed one to three levels for "something printable" put a pointer's bytes on
/// screen as a menu label.
std::string ReadGuestLhString(uint8_t* base, uint32_t handle);

/// The same two hops for a string the guest STATES as a call argument (a menu row's name, a
/// string parameter's value): every byte to the NUL, none refused. A row name is its localised
/// label cut to one byte per character (`sub_826CE848` -> `sub_82B3F898`: `82B3F904 lhzx` /
/// `82B3F910 stbx`; disc `sub_82408338` -> `sub_82B398A8`), so Cyrillic names are control bytes
/// and accented ones are bytes above 0x7F, which ReadGuestLhString's path whitelist blanks.
std::string ReadStatedLhBytes(uint8_t* base, uint32_t handle);

/// Bytes as Latin-1, in UTF-8: what the host is handed for a stated narrow string, so a cut
/// accented character reads as itself and no two byte strings share a text.
std::string Latin1ToUtf8(const std::string& bytes);

/// The same handle shape read as WIDE UTF-16 — the guest's display-text class. See the
/// implementation for why reading one with the other returns an empty string rather than noise.
std::string ReadGuestWideString(uint8_t* base, uint32_t handle);

/// The GUI's preview figures as the preview scene files them (preview_figures_stated.cpp).
/// SetPreviewFigureRole names the figure the calling thread's guest function is about to file
/// ("clothing", "playerone", "henchman") and returns the role it replaces, to be put back on the
/// way out. StatedPreviewFigure is the entity of the newest live figure filed under a role, 0 once
/// destroyed (StatedClothingMannequin: the clothing screen's); StatedPreviewFigures lists every
/// figure filed and not yet destroyed, with its role, and returns how many it wrote.
const char* SetPreviewFigureRole(const char* role);
uint32_t StatedPreviewFigure(const char* role);
uint32_t StatedPreviewFigures(uint32_t* out_entities, const char** out_roles, uint32_t capacity);
uint32_t StatedClothingMannequin();
std::string FormatGuestObjectVTable(uint8_t* base,
                                    uint32_t object_guest,
                                    uint32_t slot_count);
bool ShouldLogVfsTraceOperation(std::string_view api);
bool ShouldLogRepeatedVfsTraceOperation(std::string_view api,
                                        uint32_t limit);
inline bool ShouldLogRepeatedVfsTraceOperation(std::string_view api) {
  return ShouldLogRepeatedVfsTraceOperation(api, 8);
}
std::string DescribeGuestObject(uint8_t* base,
                                uint32_t object_guest,
                                uint32_t word_count,
                                uint32_t slot_count);
inline std::string DescribeGuestObject(uint8_t* base, uint32_t object_guest) {
  return DescribeGuestObject(base, object_guest, 12, 12);
}
std::string ReadMaybeGuestString(uint8_t* base,
                                 uint32_t string_guest,
                                 uint32_t max_chars);
inline std::string ReadMaybeGuestString(uint8_t* base,
                                        uint32_t string_guest) {
  return ReadMaybeGuestString(base, string_guest, 256);
}

bool IsTraceInterestingAsset(std::string_view asset_path);
bool IsMainMenuTextureTraceAsset(std::string_view asset_path);
bool IsMainMenuTextureHashTraceAsset(std::string_view asset_path);
bool ShouldAllowOwnerlessSplitTextureWrapperCombine(std::string_view api,
                                                    std::string_view provider);
bool IsMainMenuTextureResourceHash(uint32_t resource_hash);
bool IsResourceWrapperTextureTraceAsset(std::string_view asset_path);
bool IsTextureResourceWrapperCaller(uint32_t lr);
bool ShouldTraceNativeResourceWrapperApi(std::string_view api, uint32_t lr);
bool IsGameFaceBoundaryPath(std::string_view asset_path);
bool ShouldLogFileApiPath(std::string_view api, std::string_view path);

float RefiiGuestF32FromWord(uint32_t word);
double RefiiGuestF64FromWord(uint64_t word);
void LogRefiiVfsChainVerdict(std::string_view chain_id,
                             uint32_t root_lr,
                             std::string_view asset,
                             std::string_view candidate,
                             std::string_view provider_relative,
                             std::string_view shape_owner,
                             std::string_view live_boundary,
                             std::string_view collapse_status,
                             std::string_view next_boundary);
}  // namespace detail
}  // namespace refii::vfs
