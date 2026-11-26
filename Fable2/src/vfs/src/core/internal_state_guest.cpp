#include "vfs_hooks_private.h"
#if defined(_WIN32)
#include <windows.h>
#if !defined(_MSC_VER)
#include <setjmp.h>
#endif
#else
#include <setjmp.h>
#include <signal.h>
#include <sys/uio.h>
#include <unistd.h>
#endif
#include <array>
#include <cstddef>
#include <cstring>
#include "core/guest_build.h"

namespace refii::vfs {
inline namespace detail {

void MaybeLogStartupLuaStreamCloseVerdict(uint32_t stream_guest,
                                          const RefiiHostStreamRecord* record,
                                          size_t active_after);

bool IsGameFaceBinaryAssetKey(std::string_view asset_path);

std::mutex g_refii_resource_wrapper_vtable_mutex;
uint32_t g_refii_resource_wrapper_vtable = 0;
uint32_t g_refii_resource_wrapper_interface_vtable = 0;

std::array<uint32_t, 4> ReadHostFileFirstWords(
    const std::filesystem::path& path);
std::string FormatHostFileFirstWords(const std::array<uint32_t, 4>& words);
bool IsTextureHeaderCandidate(std::string_view candidate);
bool MakeVfsDatabaseCandidateResolution(
    const std::filesystem::path& loose_root,
    std::string_view candidate,
    LooseAssetResolution* out_resolution,
    std::string* out_candidate);
bool ResolvePairedTexturePayloadForHeader(std::string_view header_key,
                                          LooseAssetResolution* out_resolution,
                                          std::string* out_candidate);
bool MaybeAugmentRefiiSplitTextureWrapperPayloadView(
    PPCContext& ctx,
    uint8_t* base,
    uint32_t wrapper_guest,
    std::string_view api,
    RefiiResourceWrapperView* view);
void RecordRefiiMainMenuBlockerEvent(std::string_view kind,
                                     std::string message);
std::string FormatRefiiMainMenuBlockerTraceForLog();
std::string FormatRefiiPostVideoHandoffTraceForLog();
float RefiiGuestF32FromWord(uint32_t word);
bool RefiiMainMenuTraceIsTextureOwnedButNotOpened();
bool RefiiStartupVideosPlaybackCriticalActive();
void LogRefiiVfsChainVerdict(std::string_view chain_id,
                             uint32_t root_lr,
                             std::string_view asset,
                             std::string_view candidate,
                             std::string_view provider_relative,
                             std::string_view shape_owner,
                             std::string_view live_boundary,
                             std::string_view collapse_status,
                             std::string_view next_boundary);

void ConfigureRefiiVfsTracyPlots() {
  static std::once_flag configure_once;
  std::call_once(configure_once, [] {
    TracyPlotConfig("Refii VFS Queue Depth",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x38BDF8);
    TracyPlotConfig("Refii VFS Active IO Workers",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x22C55E);
    TracyPlotConfig("Refii VFS Read Request Bytes",
                    tracy::PlotFormatType::Memory,
                    false,
                    false,
                    0xF59E0B);
    TracyPlotConfig("Refii VFS Read Completed Bytes",
                    tracy::PlotFormatType::Memory,
                    false,
                    false,
                    0x84CC16);
    TracyPlotConfig("Refii VFS StreamReader Page Bytes",
                    tracy::PlotFormatType::Memory,
                    false,
                    false,
                    0xA855F7);
  });
}

LooseAssetRuntimeStats& LooseStats() {
  static LooseAssetRuntimeStats stats;
  return stats;
}

bool ShouldLogLooseStatsMilestone(uint64_t value) {
  return value == 1 || value == 16 || value == 64 || value == 256 ||
         (value != 0 && value % 1024 == 0);
}

void LogLooseStatsMilestone(std::string_view label,
                            uint64_t value,
                            size_t stream_record_count =
                                std::numeric_limits<size_t>::max()) {
  if (!ShouldLogLooseStatsMilestone(value)) {
    return;
  }

  auto& stats = LooseStats();
  const std::string records =
      stream_record_count == std::numeric_limits<size_t>::max()
          ? fmt::format("{}", stats.active_stream_records.load(
                                  std::memory_order_relaxed))
          : fmt::format("{}", stream_record_count);
  REXLOG_INFO("Loose asset stats {}={} vfs_hits={}/{} bank_hits={}/{} "
              "streams={} records={} synthetic={}/{} synthetic_vfs={}/{} "
              "reads={} active_reads={} handles={}/{} pruned={} "
              "ownership=owned:{} native_shaped:{} optional:{} "
              "native_fallback:{} owned_streams:{}/{} owned_reads:{} "
              "sources=lua:{} synthetic:{} database:{} texture:{} mesh:{} "
              "audio:{} other:{}",
              label,
              value,
              stats.vfs_hits.load(std::memory_order_relaxed),
              stats.vfs_lookups.load(std::memory_order_relaxed),
              stats.bank_hits.load(std::memory_order_relaxed),
              stats.bank_lookups.load(std::memory_order_relaxed),
              stats.streams_opened.load(std::memory_order_relaxed),
              records,
              stats.active_synthetic_banks.load(std::memory_order_relaxed),
              stats.synthetic_banks_opened.load(std::memory_order_relaxed),
              stats.synthetic_vfs_hits.load(std::memory_order_relaxed),
              stats.synthetic_vfs_lookups.load(std::memory_order_relaxed),
              stats.read_requests_completed.load(std::memory_order_relaxed),
              stats.active_read_requests.load(std::memory_order_relaxed),
              stats.active_private_file_handles.load(std::memory_order_relaxed),
              stats.active_private_find_handles.load(std::memory_order_relaxed),
              stats.stream_records_pruned.load(std::memory_order_relaxed),
              stats.refii_owned_decisions.load(std::memory_order_relaxed),
              stats.refii_owned_native_shaped.load(std::memory_order_relaxed),
              stats.refii_optional_misses.load(std::memory_order_relaxed),
              stats.refii_native_fallbacks.load(std::memory_order_relaxed),
              stats.refii_owned_streams_opened.load(std::memory_order_relaxed),
              stats.refii_owned_streams_closed.load(std::memory_order_relaxed),
              stats.refii_owned_reads_completed.load(std::memory_order_relaxed),
              stats.refii_lua_streams_opened.load(std::memory_order_relaxed),
              stats.refii_synthetic_streams_opened.load(std::memory_order_relaxed),
              stats.refii_database_streams_opened.load(std::memory_order_relaxed),
              stats.refii_texture_streams_opened.load(std::memory_order_relaxed),
              stats.refii_mesh_streams_opened.load(std::memory_order_relaxed),
              stats.refii_audio_streams_opened.load(std::memory_order_relaxed),
              stats.refii_other_streams_opened.load(std::memory_order_relaxed));
}

void RecordRefiiOwnedStreamLifecycleOpen(std::string_view provider_name) {
  auto& stats = LooseStats();
  stats.refii_owned_streams_opened.fetch_add(1, std::memory_order_relaxed);
  const auto record_class = [](std::atomic<uint64_t>& counter,
                               std::string_view label) {
    const uint64_t value =
        counter.fetch_add(1, std::memory_order_relaxed) + 1;
    LogLooseStatsMilestone(label, value,
                           std::numeric_limits<size_t>::max());
  };
  const std::string normalized = NormalizeLooseAssetKey(provider_name);
  bool classified = false;
  if (normalized.ends_with(".lua") || normalized.find("lua") != std::string::npos) {
    record_class(stats.refii_lua_streams_opened, "lua_streams_opened");
    classified = true;
  }
  if (normalized.rfind("banks/", 0) == 0 ||
      normalized.find(".bank") != std::string::npos ||
      normalized.find("_headers/") != std::string::npos) {
    record_class(stats.refii_synthetic_streams_opened,
                 "bank_container_streams_opened");
    classified = true;
  }
  if (normalized.ends_with(".gdb") || normalized.ends_with(".gdb.tu") ||
      normalized.ends_with(".babel") || normalized.ends_with(".babel.csv")) {
    record_class(stats.refii_database_streams_opened,
                 "database_streams_opened");
    classified = true;
  }
  if (IsTextureAssetCandidate(normalized) ||
      normalized.find("texture_headers/") != std::string::npos ||
      normalized.find("textures/") != std::string::npos) {
    record_class(stats.refii_texture_streams_opened,
                 "texture_streams_opened");
    classified = true;
  }
  if (normalized.find("models/") != std::string::npos ||
      normalized.find("meshes/") != std::string::npos ||
      normalized.find("/mesh/") != std::string::npos ||
      normalized.ends_with(".model") || normalized.ends_with(".mesh") ||
      normalized.ends_with(".mdl") || normalized.ends_with(".mdl.gmd") ||
      normalized.ends_with(".hkx") || normalized.ends_with(".hkx.hpb") ||
      normalized.ends_with("_models.bnk") ||
      normalized.ends_with("_model_headers.bnk")) {
    record_class(stats.refii_mesh_streams_opened, "mesh_streams_opened");
    classified = true;
  }
  if (normalized.ends_with(".adb") || normalized.ends_with(".amp") ||
      normalized.ends_with(".loco") || normalized.find("audio/") != std::string::npos) {
    record_class(stats.refii_audio_streams_opened, "audio_streams_opened");
    classified = true;
  }
  if (!classified) {
    record_class(stats.refii_other_streams_opened, "other_streams_opened");
  }
}

uint32_t HighestPowerOfTwoAtMost(uint32_t value) {
  if (value == 0) {
    return 0;
  }
  uint32_t result = 1;
  while (value >>= 1) {
    result <<= 1;
  }
  return result;
}

bool AddUint32WillOverflow(uint32_t left, uint32_t right) {
  return left > UINT32_MAX - right;
}

bool RoundUpUint32(uint32_t value, uint32_t alignment, uint32_t* out_value) {
  if (out_value == nullptr || alignment == 0) {
    return false;
  }
  const uint32_t remainder = value % alignment;
  if (remainder == 0) {
    *out_value = value;
    return true;
  }
  const uint32_t delta = alignment - remainder;
  if (AddUint32WillOverflow(value, delta)) {
    return false;
  }
  *out_value = value + delta;
  return true;
}

bool IsHeapRangeFree(rex::memory::BaseHeap* heap,
                     uint32_t address,
                     uint32_t size,
                     uint32_t page_size) {
  if (heap == nullptr || size == 0 || heap->page_size() != page_size) {
    return false;
  }

  uint32_t rounded_size = 0;
  if (!RoundUpUint32(size, page_size, &rounded_size) ||
      AddUint32WillOverflow(address, rounded_size)) {
    return false;
  }

  const uint32_t heap_base = heap->heap_base();
  const uint32_t heap_size = heap->heap_size();
  if (address < heap_base || AddUint32WillOverflow(heap_base, heap_size) ||
      address + rounded_size > heap_base + heap_size) {
    return false;
  }

  uint32_t cursor = address;
  const uint32_t end = address + rounded_size;
  while (cursor < end) {
    rex::memory::HeapAllocationInfo info{};
    if (!heap->QueryRegionInfo(cursor, &info) || info.state != 0 ||
        info.region_size == 0) {
      return false;
    }

    if (info.region_size >= end - cursor) {
      return true;
    }
    if (AddUint32WillOverflow(cursor, info.region_size)) {
      return false;
    }
    cursor += info.region_size;
  }

  return true;
}

uint32_t CallVirtualAlloc(PPCContext& source_ctx,
                          uint8_t* base,
                          uint32_t address,
                          uint32_t size,
                          uint32_t allocation_type,
                          uint32_t protect) {
  PPCContext alloc_ctx = source_ctx;
  alloc_ctx.r3.u32 = address;
  alloc_ctx.r4.u32 = size;
  alloc_ctx.r5.u32 = allocation_type;
  alloc_ctx.r6.u32 = protect;
  __imp__rex_VirtualAlloc(alloc_ctx, base);
  return alloc_ctx.r3.u32;
}

uint32_t CallLhMalloc(PPCContext& source_ctx, uint8_t* base, uint32_t size) {
  PPCContext malloc_ctx = source_ctx;
  malloc_ctx.r3.u32 = size;
  __imp__rex_lh_malloc(malloc_ctx, base);
  return malloc_ctx.r3.u32;
}

void CallLhFree(PPCContext& source_ctx, uint8_t* base, uint32_t guest_address) {
  if (guest_address == 0) {
    return;
  }

  PPCContext free_ctx = source_ctx;
  free_ctx.r3.u32 = guest_address;
  rex_lh_free(free_ctx, base);
}

uint32_t LoadGuestU32(uint8_t* base, uint32_t guest_address);

PPCFunc* FindGuestFunction(uint32_t guest_address) {
  if (guest_address == 0) {
    return nullptr;
  }

  for (PPCFuncMapping* mapping = PPCFuncMappings;
       mapping != nullptr && mapping->host != nullptr;
       ++mapping) {
    if (static_cast<uint32_t>(mapping->guest) == guest_address) {
      return mapping->host;
    }
  }

  return nullptr;
}

#if defined(_MSC_VER)
__declspec(noinline) bool SafeInvokeGuestCall(
    PPCFunc* call, PPCContext& ctx, uint8_t* base) {
  if (call == nullptr) {
    return false;
  }
  __try {
    call(ctx, base);
    return true;
  } __except (EXCEPTION_EXECUTE_HANDLER) {
    return false;
  }
}
#elif defined(_WIN32)
namespace {
thread_local jmp_buf t_guest_call_jmp_buf;
thread_local bool t_in_safe_guest_call = false;

LONG CALLBACK GuestSafeVectoredHandler(PEXCEPTION_POINTERS info) {
  if (!t_in_safe_guest_call || info == nullptr || info->ExceptionRecord == nullptr) {
    return EXCEPTION_CONTINUE_SEARCH;
  }
  static constexpr DWORD kStatusHeapCorruption = 0xC0000374u;
  const DWORD code = info->ExceptionRecord->ExceptionCode;
  if (code == EXCEPTION_ACCESS_VIOLATION ||
      code == EXCEPTION_IN_PAGE_ERROR ||
      code == EXCEPTION_ILLEGAL_INSTRUCTION ||
      code == EXCEPTION_PRIV_INSTRUCTION ||
      code == EXCEPTION_BREAKPOINT ||
      code == kStatusHeapCorruption) {
    t_in_safe_guest_call = false;
    longjmp(t_guest_call_jmp_buf, 1);
  }
  return EXCEPTION_CONTINUE_SEARCH;
}

void EnsureGuestSafeHandlerInstalled() {
  static std::once_flag s_once;
  std::call_once(s_once, []() {
    // First = 0 places this handler at the back of the VEH chain so ReXGlue's
    // MMIOHandler (First = 1) always services GPU write-watch and MMIO faults first.
    AddVectoredExceptionHandler(0, GuestSafeVectoredHandler);
  });
}
}  // namespace

__attribute__((noinline)) bool SafeInvokeGuestCall(
    PPCFunc* call, PPCContext& ctx, uint8_t* base) {
  if (call == nullptr) {
    return false;
  }
  EnsureGuestSafeHandlerInstalled();
  jmp_buf saved_buf;
  std::memcpy(&saved_buf, &t_guest_call_jmp_buf, sizeof(jmp_buf));
  const bool saved_active = t_in_safe_guest_call;
  t_in_safe_guest_call = true;
  if (setjmp(t_guest_call_jmp_buf) == 0) {
    call(ctx, base);
    t_in_safe_guest_call = saved_active;
    std::memcpy(&t_guest_call_jmp_buf, &saved_buf, sizeof(jmp_buf));
    return true;
  }
  t_in_safe_guest_call = saved_active;
  std::memcpy(&t_guest_call_jmp_buf, &saved_buf, sizeof(jmp_buf));
  return false;
}
#else
namespace {
thread_local sigjmp_buf t_guest_call_jmp_buf;
thread_local volatile sig_atomic_t t_in_safe_guest_call = 0;

struct SignalSlot {
  int sig;
  struct sigaction old_action;
};

SignalSlot g_old_signals[] = {
    {SIGILL, {}},
    {SIGTRAP, {}},
    {SIGABRT, {}},
    {SIGBUS, {}},
    {SIGSEGV, {}},
};

void GuestSafeSignalHandler(int sig, siginfo_t* info, void* ucontext) {
  if (t_in_safe_guest_call) {
    t_in_safe_guest_call = 0;
    siglongjmp(t_guest_call_jmp_buf, 1);
  }
  for (const auto& slot : g_old_signals) {
    if (slot.sig == sig) {
      if ((slot.old_action.sa_flags & SA_SIGINFO) &&
          slot.old_action.sa_sigaction != nullptr) {
        slot.old_action.sa_sigaction(sig, info, ucontext);
        return;
      }
      if (slot.old_action.sa_handler != SIG_DFL &&
          slot.old_action.sa_handler != SIG_IGN &&
          slot.old_action.sa_handler != nullptr) {
        slot.old_action.sa_handler(sig);
        return;
      }
      break;
    }
  }
  signal(sig, SIG_DFL);
  raise(sig);
}

void EnsureGuestSafeHandlerInstalled() {
  static std::once_flag s_once;
  std::call_once(s_once, []() {
    struct sigaction sa = {};
    sa.sa_sigaction = GuestSafeSignalHandler;
    sigemptyset(&sa.sa_mask);
    sa.sa_flags = SA_SIGINFO | SA_NODEFER;
    for (auto& slot : g_old_signals) {
      sigaction(slot.sig, &sa, &slot.old_action);
    }
  });
}
}  // namespace

__attribute__((noinline)) bool SafeInvokeGuestCall(
    PPCFunc* call, PPCContext& ctx, uint8_t* base) {
  if (call == nullptr) {
    return false;
  }
  EnsureGuestSafeHandlerInstalled();
  sigjmp_buf saved_buf;
  std::memcpy(&saved_buf, &t_guest_call_jmp_buf, sizeof(sigjmp_buf));
  const sig_atomic_t saved_active = t_in_safe_guest_call;
  t_in_safe_guest_call = 1;
  if (sigsetjmp(t_guest_call_jmp_buf, 1) == 0) {
    call(ctx, base);
    t_in_safe_guest_call = saved_active;
    std::memcpy(&t_guest_call_jmp_buf, &saved_buf, sizeof(sigjmp_buf));
    return true;
  }
  t_in_safe_guest_call = saved_active;
  std::memcpy(&t_guest_call_jmp_buf, &saved_buf, sizeof(sigjmp_buf));
  return false;
}
#endif

bool CopyToStreamDestination(PPCContext& source_ctx,
                             uint8_t* base,
                             uint32_t destination_base_guest,
                             uint32_t destination_kind,
                             uint32_t source_guest,
                             uint32_t byte_count) {
  if (base == nullptr || destination_base_guest == 0 || source_guest == 0 ||
      byte_count == 0 || destination_kind > 16) {
    return byte_count == 0;
  }

  const uint32_t copy_function_guest =
      LoadGuestU32(base, kStreamDestinationCopyTable + destination_kind * 4);
  PPCFunc* copy_function = FindGuestFunction(copy_function_guest);
  if (copy_function == nullptr) {
    REXLOG_WARN("Refii stream destination copy failed: kind={} "
                "function=0x{:08X} destination=0x{:08X} source=0x{:08X} "
                "bytes={}",
                destination_kind,
                copy_function_guest,
                destination_base_guest,
                source_guest,
                byte_count);
    return false;
  }

  PPCContext copy_ctx = source_ctx;
  copy_ctx.r3.u32 = destination_base_guest;
  copy_ctx.r4.u32 = source_guest;
  copy_ctx.r5.u32 = byte_count;
  copy_ctx.ctr.u32 = copy_function_guest;
  copy_function(copy_ctx, base);
  return true;
}

void AddRefGuestObject(PPCContext& source_ctx,
                       uint8_t* base,
                       uint32_t object_guest) {
  if (object_guest == 0) {
    return;
  }

  PPCContext add_ref_ctx = source_ctx;
  add_ref_ctx.r3.u32 = object_guest;
  __imp__rex_lhRefCounted_AddRef(add_ref_ctx, base);
}

void ReleaseGuestObject(PPCContext& source_ctx,
                        uint8_t* base,
                        uint32_t object_guest) {
  if (object_guest == 0) {
    return;
  }

  PPCContext release_ctx = source_ctx;
  release_ctx.r3.u32 = object_guest;
  __imp__rex_lhRefCounted_Release(release_ctx, base);
}

bool ShouldLogRepeatedVfsTraceOperation(std::string_view api,
                                        uint32_t limit);

bool TrySilentLargePageReserve(PPCContext& ctx, uint8_t* base) {
  constexpr uint32_t kLargePageSize = 64 * 1024;
  constexpr uint32_t kLargePageScanBegin = 0x40000000;
  constexpr uint32_t kLargePageScanEnd = 0x80000000;

  const uint32_t allocation_size = ctx.r3.u32;
  const uint32_t stride_hint = ctx.r4.u32;
  const uint32_t alignment = ctx.r5.u32;
  if (allocation_size == 0 || alignment != kLargePageSize) {
    return false;
  }

  auto* kernel = rex::system::kernel_state();
  if (kernel == nullptr || kernel->memory() == nullptr) {
    return false;
  }

  const uint32_t stride =
      HighestPowerOfTwoAtMost(std::max(stride_hint, alignment));
  if (stride == 0) {
    return false;
  }

  auto* memory = kernel->memory();
  for (uint32_t candidate = kLargePageScanBegin; candidate < kLargePageScanEnd;) {
    rex::memory::BaseHeap* heap = memory->LookupHeap(candidate);
    if (IsHeapRangeFree(heap, candidate, allocation_size, kLargePageSize)) {
      const uint32_t reserved =
          CallVirtualAlloc(ctx,
                           base,
                           candidate,
                           allocation_size,
                           rex::X_MEM_LARGE_PAGES | rex::X_MEM_RESERVE,
                           rex::X_PAGE_NOACCESS);
      if (reserved != 0) {
        const uint32_t committed =
            CallVirtualAlloc(ctx,
                             base,
                             reserved,
                             allocation_size,
                             rex::X_MEM_LARGE_PAGES | rex::X_MEM_COMMIT,
                             rex::X_PAGE_READWRITE);
        if (committed != 0) {
          ctx.r3.u32 = reserved;
          return true;
        }

        if (auto* reserved_heap = memory->LookupHeap(reserved)) {
          reserved_heap->Release(reserved);
        }
        if (VfsTracingEnabled() &&
            ShouldLogRepeatedVfsTraceOperation("AllocFixedCommitFailed", 8)) {
          REXLOG_WARN("Refii AllocFixed large-page commit failed reserved=0x{:08X} "
                      "size={} stride={} alignment={}",
                      reserved,
                      allocation_size,
                      stride,
                      alignment);
        }
      }
    }

    if (AddUint32WillOverflow(candidate, stride)) {
      break;
    }
    candidate += stride;
  }

  if (VfsTracingEnabled() &&
      ShouldLogRepeatedVfsTraceOperation("AllocFixedFallback", 8)) {
    REXLOG_INFO("Refii AllocFixed large-page fallback size={} stride_hint={} "
                "alignment={}",
                allocation_size,
                stride_hint,
                alignment);
  }
  return false;
}

uint8_t LoadGuestU8(uint8_t* base, uint32_t guest_address) {
  return *rex::memory::GuestPtr<uint8_t*>(base, guest_address);
}

size_t TryCopyGuestBytes(uint8_t* base, uint32_t guest_address, void* dst,
                         size_t length) {
  if (base == nullptr || dst == nullptr || length == 0) {
    return 0;
  }
  auto* const remote_base = rex::memory::GuestPtr<uint8_t*>(base, guest_address);
#if defined(_WIN32)
  SIZE_T read_bytes = 0;
  const BOOL read_ok =
      ReadProcessMemory(GetCurrentProcess(), remote_base, dst, length, &read_bytes);
  return (read_ok || read_bytes != 0) ? static_cast<size_t>(read_bytes) : 0;
#else
  struct iovec local {
    dst, length
  };
  struct iovec remote {
    remote_base, length
  };
  const std::ptrdiff_t copied =
      process_vm_readv(getpid(), &local, 1, &remote, 1, 0);
  return copied < 0 ? 0 : static_cast<size_t>(copied);
#endif
}

bool TryLoadGuestU32(uint8_t* base, uint32_t guest_address, uint32_t* out) {
  uint8_t bytes[4] = {};
  if (TryCopyGuestBytes(base, guest_address, bytes, sizeof(bytes)) !=
      sizeof(bytes)) {
    return false;
  }
  *out = (uint32_t(bytes[0]) << 24) | (uint32_t(bytes[1]) << 16) |
         (uint32_t(bytes[2]) << 8) | uint32_t(bytes[3]);
  return true;
}

uint16_t LoadGuestU16(uint8_t* base, uint32_t guest_address) {
  const auto* ptr = rex::memory::GuestPtr<uint8_t*>(base, guest_address);
  return static_cast<uint16_t>((uint16_t(ptr[0]) << 8) | uint16_t(ptr[1]));
}

uint32_t LoadGuestU32(uint8_t* base, uint32_t guest_address) {
  const auto* ptr = rex::memory::GuestPtr<uint8_t*>(base, guest_address);
  return (uint32_t(ptr[0]) << 24) | (uint32_t(ptr[1]) << 16) |
         (uint32_t(ptr[2]) << 8) | uint32_t(ptr[3]);
}

uint64_t LoadGuestU64(uint8_t* base, uint32_t guest_address) {
  return (static_cast<uint64_t>(LoadGuestU32(base, guest_address)) << 32) |
         static_cast<uint64_t>(LoadGuestU32(base, guest_address + 4));
}

void StoreGuestU8(uint8_t* base, uint32_t guest_address, uint8_t value) {
  if (base != nullptr && value != 0u) {
    const uint32_t root = LoadGuestU32(base, refii::guest::Address(0x83496B0Cu));
    if (LooksLikeGuestPointer(root)) {
      const uint32_t state = LoadGuestU32(base, root + 8u);
      const uint32_t owner = LooksLikeGuestPointer(state)
                                 ? LoadGuestU32(base, state + 40u)
                                 : 0u;
      const uint32_t frame = LooksLikeGuestPointer(owner)
                                 ? LoadGuestU32(base, owner + 4u)
                                 : 0u;
      if (LooksLikeGuestPointer(frame) && guest_address == frame + 244u) {
        const uint32_t movie = LoadGuestU32(base, frame + 76u);
        if (LooksLikeGuestPointer(movie) &&
            LoadGuestU32(base, movie + 8u) != 0u) {
          static std::atomic<uint32_t> rejected_writes{0};
          const uint32_t count =
              rejected_writes.fetch_add(1, std::memory_order_relaxed) + 1u;
          if (count <= 8u) {
            REXLOG_WARN("Refii VFS rejected synthetic active startup frame "
                        "failure-byte write #{} frame=0x{:08X} "
                        "movie=0x{:08X} bink_frame={}/{}",
                        count, frame, movie, LoadGuestU32(base, movie + 12u),
                        LoadGuestU32(base, movie + 8u));
          }
          return;
        }
      }
    }
  }
  *rex::memory::GuestPtr<uint8_t*>(base, guest_address) = value;
}

void StoreGuestU16(uint8_t* base, uint32_t guest_address, uint16_t value) {
  auto* ptr = rex::memory::GuestPtr<uint8_t*>(base, guest_address);
  ptr[0] = static_cast<uint8_t>((value >> 8) & 0xFF);
  ptr[1] = static_cast<uint8_t>(value & 0xFF);
}

void StoreGuestU32(uint8_t* base, uint32_t guest_address, uint32_t value) {
  auto* ptr = rex::memory::GuestPtr<uint8_t*>(base, guest_address);
  ptr[0] = static_cast<uint8_t>((value >> 24) & 0xFF);
  ptr[1] = static_cast<uint8_t>((value >> 16) & 0xFF);
  ptr[2] = static_cast<uint8_t>((value >> 8) & 0xFF);
  ptr[3] = static_cast<uint8_t>(value & 0xFF);
}

void StoreGuestU64(uint8_t* base, uint32_t guest_address, uint64_t value) {
  StoreGuestU32(base, guest_address, static_cast<uint32_t>(value >> 32));
  StoreGuestU32(base, guest_address + 4, static_cast<uint32_t>(value));
}

bool IsRefiiResourceWrapperPrimaryVTable(uint32_t vtable_guest) {
  return vtable_guest == kResourceStreamWrapperVTable ||
         (g_refii_resource_wrapper_vtable != 0 &&
          vtable_guest == g_refii_resource_wrapper_vtable);
}

bool IsRefiiResourceWrapperInterfaceVTable(uint32_t vtable_guest) {
  return vtable_guest == kResourceStreamWrapperInterfaceVTable ||
         (g_refii_resource_wrapper_interface_vtable != 0 &&
          vtable_guest == g_refii_resource_wrapper_interface_vtable);
}

std::pair<uint32_t, uint32_t> EnsureRefiiResourceWrapperVTables(
    PPCContext& ctx,
    uint8_t* base) {
  if (base == nullptr) {
    return {kResourceStreamWrapperVTable, kResourceStreamWrapperInterfaceVTable};
  }

  std::lock_guard<std::mutex> lock(g_refii_resource_wrapper_vtable_mutex);
  if (g_refii_resource_wrapper_vtable != 0 &&
      g_refii_resource_wrapper_interface_vtable != 0) {
    return {g_refii_resource_wrapper_vtable,
            g_refii_resource_wrapper_interface_vtable};
  }

  // NOT `interface`. That is a MACRO on Windows -- rpc.h has `#define interface struct`, and
  // the payload reaches it through zig's any-windows-any headers -- so the declaration below
  // expanded to `const uint32_t struct = ...` and produced ten parse errors in a row, none of
  // which name the real cause. It compiled on Linux because nothing there defines it.
  const uint32_t primary =
      CallLhMalloc(ctx, base, kResourceWrapperVTableCopySize);
  const uint32_t interface_vtable =
      CallLhMalloc(ctx, base, kResourceWrapperVTableCopySize);
  if (primary == 0 || interface_vtable == 0) {
    if (primary != 0) {
      CallLhFree(ctx, base, primary);
    }
    if (interface_vtable != 0) {
      CallLhFree(ctx, base, interface_vtable);
    }
    REXLOG_WARN("Refii resource wrapper private vtable allocation failed; "
                "falling back to native vtables");
    return {kResourceStreamWrapperVTable, kResourceStreamWrapperInterfaceVTable};
  }

  std::memcpy(rex::memory::GuestPtr<uint8_t*>(base, primary),
              rex::memory::GuestPtr<uint8_t*>(base, kResourceStreamWrapperVTable),
              kResourceWrapperVTableCopySize);
  std::memcpy(
      rex::memory::GuestPtr<uint8_t*>(base, interface_vtable),
      rex::memory::GuestPtr<uint8_t*>(base, kResourceStreamWrapperInterfaceVTable),
      kResourceWrapperVTableCopySize);

  // The copies are unmodified duplicates of the native vtables and exist only
  // so IsRefiiResourceWrapper*VTable can recognise our own objects by vtable
  // address. Do not install slot overrides here.
  //
  // The two vtables are the two bases of one 28-byte object built by
  // FindAndCreateStream (0x82B53D68): primary at +0, second base at +4. Their
  // slot maps differ, so a shared override list is wrong by construction --
  // 0x820F9DD8+8 is a -4 adjustor thunk and +16 is CBasicBufferedRequest::
  // GetDevice (sret), where the primary has Release and a [this+0x10]
  // forwarder. The shared forwarders (+12/+36) are deliberate: `lwz r3,0x10(r3)`
  // resolves to the stream at +16 from the primary base and to the second inner
  // interface at +20 from the second base.
  //
  // Native Release (0x822C72E8) is refcounted and deletes via vtbl[+44] only at
  // zero; we initialise the refcount at wrapper+8, so it is already correct.

  g_refii_resource_wrapper_vtable = primary;
  g_refii_resource_wrapper_interface_vtable = interface_vtable;

  if (VfsVerboseIoTracingEnabled()) {
    REXLOG_INFO("Refii resource wrapper private vtables primary=0x{:08X} "
                "interface=0x{:08X} (native slot maps, identity copies only)",
                primary,
                interface_vtable);
  }

  return {primary, interface_vtable};
}

bool IsReadableAssetChar(char ch) {
  const unsigned char value = static_cast<unsigned char>(ch);
  return std::isalnum(value) || ch == '/' || ch == '\\' || ch == '.' ||
         ch == '_' || ch == '-' || ch == ':' || ch == ' ' || ch == '@' ||
         ch == '[' || ch == ']' || ch == '*' || ch == '?' || ch == '(' ||
         ch == ')' || ch == ',' || ch == '\'' || ch == '"' || ch == '=';
}

// Best-effort guest string reads must never fault: the tail of the 4GB
// guest reservation is unmapped guard pages, and save-load name objects can
// carry sentinel data pointers (a -1 pointer walked LoadGuestU8 into the
// last page and SIGBUSed inside rex_lhResourceManager_GetCached's name
// logging — core 352739, 2026-07-19). Every string-data window stays below
// the physical arena ceiling.
constexpr uint32_t kGuestStringDataCeiling = 0xE0000000u;

std::string ReadGuestWideStringObject(uint8_t* base,
                                      uint32_t object_guest,
                                      size_t max_chars) {
  if (base == nullptr || object_guest == 0) {
    return {};
  }

  auto plausible_string_data = [](uint32_t data_guest,
                                  uint32_t length) -> bool {
    if (data_guest < 0x10000u || length == 0) {
      return false;
    }
    const uint64_t last_byte =
        uint64_t(data_guest) + uint64_t(length - 1u) * 2u + 1u;
    return last_byte < kGuestStringDataCeiling;
  };

  auto read_wide = [&](uint32_t data_guest, uint32_t length) -> std::string {
    if (data_guest == 0 || length == 0 || length > max_chars ||
        length > 0x100000 || !plausible_string_data(data_guest, length)) {
      return {};
    }

    const size_t byte_len = size_t(length) * 2u;
    uint8_t stack_buf[512];
    std::vector<uint8_t> heap_buf;
    uint8_t* buf = stack_buf;
    if (byte_len > sizeof(stack_buf)) {
      heap_buf.resize(byte_len);
      buf = heap_buf.data();
    }
    const size_t readable =
        TryCopyGuestBytes(base, data_guest, buf, byte_len);
    std::string value;
    value.reserve(std::min<size_t>(length, readable / 2u));
    for (uint32_t index = 0; index < length; ++index) {
      if (size_t(index) * 2u + 1u >= readable) {
        break;
      }
      const uint16_t code_unit =
          static_cast<uint16_t>((uint16_t(buf[index * 2u]) << 8) |
                                uint16_t(buf[index * 2u + 1u]));
      if (code_unit == 0) {
        break;
      }
      if (code_unit > 0x7F) {
        return {};
      }
      const char ch = static_cast<char>(code_unit);
      if (!IsReadableAssetChar(ch)) {
        return {};
      }
      value.push_back(ch);
    }
    return value;
  };

  auto read_wide_cstr = [&](uint32_t data_guest) -> std::string {
    if (data_guest < 0x10000u || data_guest >= kGuestStringDataCeiling) {
      return {};
    }

    const size_t bounded_chars = std::min<size_t>(
        max_chars, (kGuestStringDataCeiling - data_guest) / 2u);
    const size_t byte_len = std::min<size_t>(bounded_chars, 4096) * 2u;
    uint8_t stack_buf[512];
    std::vector<uint8_t> heap_buf;
    uint8_t* buf = stack_buf;
    if (byte_len > sizeof(stack_buf)) {
      heap_buf.resize(byte_len);
      buf = heap_buf.data();
    }
    const size_t readable =
        TryCopyGuestBytes(base, data_guest, buf, byte_len);
    std::string value;
    value.reserve(std::min<size_t>(bounded_chars, 256));
    for (size_t index = 0; index < bounded_chars; ++index) {
      if (index * 2u + 1u >= readable) {
        break;
      }
      const uint16_t code_unit =
          static_cast<uint16_t>((uint16_t(buf[index * 2u]) << 8) |
                                uint16_t(buf[index * 2u + 1u]));
      if (code_unit == 0) {
        break;
      }
      if (code_unit > 0x7F) {
        return {};
      }
      const char ch = static_cast<char>(code_unit);
      if (!IsReadableAssetChar(ch)) {
        return {};
      }
      value.push_back(ch);
    }
    return value;
  };

  uint32_t ref_string_guest = 0;
  if (TryLoadGuestU32(base, object_guest, &ref_string_guest) &&
      ref_string_guest >= 0x10000 && ref_string_guest < 0x90000000) {
    uint32_t data_guest = 0;
    uint32_t length = 0;
    if (!TryLoadGuestU32(base, ref_string_guest, &data_guest) ||
        !TryLoadGuestU32(base, ref_string_guest + 4, &length)) {
      return {};
    }
    std::string value = read_wide(data_guest, length);
    if (!value.empty()) {
      return value;
    }

    value = read_wide_cstr(ref_string_guest);
    if (!value.empty()) {
      return value;
    }
  }

  uint32_t head_data_guest = 0;
  uint32_t head_length = 0;
  if (!TryLoadGuestU32(base, object_guest, &head_data_guest) ||
      !TryLoadGuestU32(base, object_guest + 4, &head_length)) {
    return {};
  }
  std::string value = read_wide(head_data_guest, head_length);
  if (!value.empty()) {
    return value;
  }

  uint32_t length = 0;
  uint32_t capacity = 0;
  if (!TryLoadGuestU32(base, object_guest + 20, &length) ||
      !TryLoadGuestU32(base, object_guest + 24, &capacity)) {
    return {};
  }
  if (length == 0 || length > max_chars || capacity > 0x100000) {
    return read_wide_cstr(object_guest);
  }

  uint32_t data_guest = object_guest + 4;
  if (capacity >= 8 && !TryLoadGuestU32(base, object_guest + 4, &data_guest)) {
    return {};
  }
  if (data_guest == 0) {
    return {};
  }

  return read_wide(data_guest, length);
}

std::string ReadGuestAsciiStringObject(uint8_t* base,
                                       uint32_t object_guest,
                                       size_t max_chars) {
  if (base == nullptr || object_guest == 0) {
    return {};
  }

  auto plausible_string_data = [](uint32_t data_guest,
                                  uint32_t length) -> bool {
    if (data_guest < 0x10000u || length == 0) {
      return false;
    }
    const uint64_t last_byte = uint64_t(data_guest) + uint64_t(length - 1u);
    return last_byte < kGuestStringDataCeiling;
  };

  auto read_ascii = [&](uint32_t data_guest, uint32_t length) -> std::string {
    if (data_guest == 0 || length == 0 || length > max_chars ||
        length > 0x100000 || !plausible_string_data(data_guest, length)) {
      return {};
    }

    char stack_buf[512];
    std::vector<char> heap_buf;
    char* buf = stack_buf;
    if (length > sizeof(stack_buf)) {
      heap_buf.resize(length);
      buf = heap_buf.data();
    }
    const size_t readable =
        TryCopyGuestBytes(base, data_guest, buf, length);
    if (readable == 0) {
      return {};
    }

    std::string value;
    value.reserve(readable);
    for (size_t index = 0; index < readable; ++index) {
      const char ch = buf[index];
      if (ch == '\0') {
        break;
      }
      if (!IsReadableAssetChar(ch)) {
        return {};
      }
      value.push_back(ch);
    }
    return value;
  };

  auto read_ascii_cstr = [&](uint32_t data_guest) -> std::string {
    if (data_guest < 0x10000u || data_guest >= kGuestStringDataCeiling) {
      return {};
    }

    const size_t bounded_chars =
        std::min<size_t>(max_chars, kGuestStringDataCeiling - data_guest);
    const size_t byte_len = std::min<size_t>(bounded_chars, 4096);
    char stack_buf[512];
    std::vector<char> heap_buf;
    char* buf = stack_buf;
    if (byte_len > sizeof(stack_buf)) {
      heap_buf.resize(byte_len);
      buf = heap_buf.data();
    }
    const size_t readable =
        TryCopyGuestBytes(base, data_guest, buf, byte_len);
    std::string value;
    value.reserve(std::min<size_t>(readable, 256));
    for (size_t index = 0; index < readable; ++index) {
      const char ch = buf[index];
      if (ch == '\0') {
        break;
      }
      if (!IsReadableAssetChar(ch)) {
        return {};
      }
      value.push_back(ch);
    }
    return value;
  };

  uint32_t ref_string_guest = 0;
  if (TryLoadGuestU32(base, object_guest, &ref_string_guest) &&
      ref_string_guest >= 0x10000 && ref_string_guest < 0x90000000) {
    uint32_t data_guest = 0;
    uint32_t length = 0;
    if (TryLoadGuestU32(base, ref_string_guest, &data_guest) &&
        TryLoadGuestU32(base, ref_string_guest + 4, &length)) {
      std::string value = read_ascii(data_guest, length);
      if (!value.empty()) {
        return value;
      }
      value = read_ascii_cstr(data_guest);
      if (!value.empty()) {
        return value;
      }
    }
  }

  uint32_t length = 0;
  uint32_t capacity = 0;
  if (!TryLoadGuestU32(base, object_guest + 20, &length) ||
      !TryLoadGuestU32(base, object_guest + 24, &capacity)) {
    return {};
  }
  if (length == 0 || length > max_chars || capacity > 0x100000) {
    return {};
  }

  uint32_t data_guest = object_guest + 4;
  if (capacity >= 16 && !TryLoadGuestU32(base, object_guest + 4, &data_guest)) {
    return {};
  }
  std::string value = read_ascii(data_guest, length);
  if (!value.empty()) {
    return value;
  }
  return read_ascii_cstr(data_guest);
}

std::string FormatGuestWords(uint8_t* base, uint32_t guest, uint32_t word_count) {
  if (base == nullptr || guest == 0) {
    return {};
  }

  // Trace-only helper fed by pointer heuristics, so the address may land on
  // an unmapped page of the sparse guest mapping; a direct load there is a
  // host SIGBUS. Copy via process_vm_readv, which reports EFAULT instead of
  // faulting.
  std::array<uint32_t, 64> words{};
  const uint32_t clamped = std::min<uint32_t>(word_count, words.size());
  const size_t want = clamped * sizeof(uint32_t);
  auto* const remote_base = rex::memory::GuestPtr<uint8_t*>(base, guest);
#if defined(_WIN32)
  SIZE_T read_bytes = 0;
  const BOOL read_ok = ReadProcessMemory(GetCurrentProcess(), remote_base,
                                         words.data(), want, &read_bytes);
  const std::ptrdiff_t copied = (read_ok || read_bytes != 0)
                                    ? static_cast<std::ptrdiff_t>(read_bytes)
                                    : -1;
#else
  struct iovec local {
    words.data(), want
  };
  struct iovec remote {
    remote_base, want
  };
  const std::ptrdiff_t copied =
      process_vm_readv(getpid(), &local, 1, &remote, 1, 0);
#endif
  if (copied < 0) {
    return fmt::format("<unmapped:0x{:08X}>", guest);
  }

  std::ostringstream stream;
  const uint32_t got = static_cast<uint32_t>(copied) / sizeof(uint32_t);
  for (uint32_t index = 0; index < clamped; ++index) {
    if (index != 0) {
      stream << ' ';
    }
    if (index < got) {
      stream << fmt::format("0x{:08X}", __builtin_bswap32(words[index]));
    } else {
      stream << "<unmapped>";
    }
  }
  return stream.str();
}

std::string FormatGuestAsciiBytes(uint8_t* base,
                                  uint32_t guest,
                                  uint32_t max_chars) {
  if (base == nullptr || guest < 0x10000u ||
      guest >= kGuestStringDataCeiling || max_chars == 0) {
    return {};
  }

  const uint32_t bounded_chars =
      std::min<uint32_t>(max_chars, kGuestStringDataCeiling - guest);
  char stack_buf[512];
  std::vector<char> heap_buf;
  char* buf = stack_buf;
  if (bounded_chars > sizeof(stack_buf)) {
    heap_buf.resize(bounded_chars);
    buf = heap_buf.data();
  }
  const size_t readable =
      TryCopyGuestBytes(base, guest, buf, bounded_chars);
  if (readable == 0) {
    return {};
  }

  std::string value;
  value.reserve(readable);
  for (size_t index = 0; index < readable; ++index) {
    const char ch = buf[index];
    if (ch == '\0') {
      break;
    }
    if (!IsReadableAssetChar(ch)) {
      return {};
    }
    value.push_back(ch);
  }
  return value;
}

std::string ReadGuestLhString(uint8_t* base, uint32_t handle) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  if (base == nullptr || !plausible(handle)) {
    return std::string();
  }

  // Exactly two hops, never a search: the guest takes these as `char***` and reaches the
  // characters with two dereferences and one guard (`sub_826B9510`), storing with length -1,
  // i.e. NUL-terminated. `sub_826CE8D0` declares all four of its string arguments the same
  // way. Probing one to three levels returns the pointer bytes whenever they are printable.
  // A zero inner pointer is an empty string, not a failure: the guest substitutes its own
  // empty literal there.
  uint32_t inner = 0;
  if (!TryLoadGuestU32(base, handle, &inner) || inner == 0 || !plausible(inner)) {
    return std::string();
  }

  uint32_t characters = 0;
  if (!TryLoadGuestU32(base, inner, &characters) || !plausible(characters)) {
    return std::string();
  }

  return FormatGuestAsciiBytes(base, characters, 256u);
}

std::string ReadStatedLhBytes(uint8_t* base, uint32_t handle) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  uint32_t inner = 0;
  if (base == nullptr || !plausible(handle) || !TryLoadGuestU32(base, handle, &inner) ||
      !plausible(inner)) {
    return std::string();
  }

  uint32_t characters = 0;
  if (!TryLoadGuestU32(base, inner, &characters) || !plausible(characters)) {
    return std::string();
  }

  char raw[256];
  const size_t readable = TryCopyGuestBytes(base, characters, raw, sizeof(raw));
  return std::string(raw, strnlen(raw, readable));
}

std::string Latin1ToUtf8(const std::string& bytes) {
  std::string text;
  text.reserve(bytes.size());
  for (const char ch : bytes) {
    const unsigned char value = static_cast<unsigned char>(ch);
    if (value < 0x80u) {
      text.push_back(ch);
    } else {
      text.push_back(static_cast<char>(0xC0u | (value >> 6)));
      text.push_back(static_cast<char>(0x80u | (value & 0x3Fu)));
    }
  }
  return text;
}

std::string StatedBytesToUtf8(const std::string& bytes) {
  size_t at = 0;
  while (at < bytes.size()) {
    const unsigned char lead = static_cast<unsigned char>(bytes[at]);
    size_t follow = 0;
    uint32_t minimum = 0;
    uint32_t code = 0;
    if (lead < 0x80u) {
      ++at;
      continue;
    } else if (lead >= 0xC2u && lead <= 0xDFu) {
      follow = 1; minimum = 0x80u; code = lead & 0x1Fu;
    } else if (lead >= 0xE0u && lead <= 0xEFu) {
      follow = 2; minimum = 0x800u; code = lead & 0x0Fu;
    } else if (lead >= 0xF0u && lead <= 0xF4u) {
      follow = 3; minimum = 0x10000u; code = lead & 0x07u;
    } else {
      return Latin1ToUtf8(bytes);
    }
    if (at + follow >= bytes.size()) {
      return Latin1ToUtf8(bytes);
    }
    for (size_t i = 1; i <= follow; ++i) {
      const unsigned char next = static_cast<unsigned char>(bytes[at + i]);
      if ((next & 0xC0u) != 0x80u) {
        return Latin1ToUtf8(bytes);
      }
      code = (code << 6) | (next & 0x3Fu);
    }
    if (code < minimum || code > 0x10FFFFu || (code >= 0xD800u && code <= 0xDFFFu)) {
      return Latin1ToUtf8(bytes);
    }
    at += follow + 1;
  }
  return bytes;
}

std::string ReadGuestWideString(uint8_t* base, uint32_t handle) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

  if (base == nullptr || !plausible(handle)) {
    return std::string();
  }

  // THE GUEST HAS TWO STRING CLASSES AND THEY ARE NOT INTERCHANGEABLE.
  //
  // Element PATHS are narrow `CCharString`; DISPLAY TEXT is wide UTF-16 `LHString`. Same
  // one-word handle, same refcount at inner+12, different character width — and different
  // destructors, which is how a call site tells them apart: a narrow string is released with
  // `sub_82214DD8`, a wide one with `LHString_CCharString_CCharString_0` (0x821C67F8).
  //
  // Reading a wide string with the ASCII reader does not return mojibake, it returns NOTHING:
  // UTF-16 big-endian puts a zero high byte first, so the very first byte terminates. The
  // world map's eleven region fields read empty for exactly this reason while the region NAME
  // beside them read fine — that one really is narrow, being what `sub_82519DF8` hands to
  // `strcmpi`.
  uint32_t inner = 0;
  if (!TryLoadGuestU32(base, handle, &inner) || inner == 0 || !plausible(inner)) {
    return std::string();
  }

  uint32_t characters = 0;
  if (!TryLoadGuestU32(base, inner, &characters) || !plausible(characters)) {
    return std::string();
  }

  // Big-endian, NUL-terminated, out as UTF-8 — the host decodes these as UTF-8, and a '?' per
  // unit past ASCII turned every accented name ("Starý hrob") into "Star? hrob" (TEST_MATRIX 55).
  // Basic plane only, as the host bridge's own reader (host_bridge.cpp ReadGuestWideString): a
  // surrogate stops the string, since emitting a lone one is worse than stopping at it.
  uint8_t raw[512];
  const size_t readable = TryCopyGuestBytes(base, characters, raw, sizeof(raw));
  if (readable < 2u) {
    return std::string();
  }
  const size_t max_units = readable / 2u;
  std::string value;
  value.reserve(std::min<size_t>(max_units, 64u));
  for (size_t i = 0; i < max_units; ++i) {
    const uint16_t unit = static_cast<uint16_t>(
        (static_cast<uint32_t>(raw[i * 2u]) << 8) | raw[i * 2u + 1u]);
    if (unit == 0u || (unit >= 0xD800u && unit <= 0xDFFFu)) {
      break;
    }
    if (unit < 0x80u) {
      value.push_back(static_cast<char>(unit));
    } else if (unit < 0x800u) {
      value.push_back(static_cast<char>(0xC0u | (unit >> 6)));
      value.push_back(static_cast<char>(0x80u | (unit & 0x3Fu)));
    } else {
      value.push_back(static_cast<char>(0xE0u | (unit >> 12)));
      value.push_back(static_cast<char>(0x80u | ((unit >> 6) & 0x3Fu)));
      value.push_back(static_cast<char>(0x80u | (unit & 0x3Fu)));
    }
  }
  return value;
}

std::string ReadBestEffortGuestString(uint8_t* base,
                                      uint32_t guest,
                                      uint32_t max_chars) {
  std::string value = ReadGuestWideStringObject(base, guest, max_chars);
  if (!value.empty()) {
    return value;
  }
  value = ReadGuestAsciiStringObject(base, guest, max_chars);
  if (!value.empty()) {
    return value;
  }
  return FormatGuestAsciiBytes(base, guest, max_chars);
}

std::string FormatGuestObjectVTable(uint8_t* base,
                                    uint32_t object_guest,
                                    uint32_t slot_count) {
  if (base == nullptr || object_guest == 0) {
    return {};
  }

  const uint32_t vtable_guest = LoadGuestU32(base, object_guest);
  if (vtable_guest == 0) {
    return {};
  }

  return fmt::format("vtable=0x{:08X} slots={}",
                     vtable_guest,
                     FormatGuestWords(base, vtable_guest, slot_count));
}

bool ShouldLogVfsTraceOperation(std::string_view api) {
  if (!VfsTracingEnabled()) {
    return false;
  }
  if (RefiiStartupVideosPlaybackCriticalActive()) {
    return false;
  }

  static std::mutex mutex;
  static std::unordered_map<std::string, uint32_t> counts;

  std::lock_guard<std::mutex> lock(mutex);
  const uint32_t next_count = ++counts[std::string(api)];
  return next_count <= 8 || (next_count & (next_count - 1)) == 0;
}

bool ShouldLogRepeatedVfsTraceOperation(std::string_view api,
                                        uint32_t limit = 8) {
  if (!VfsTracingEnabled()) {
    return false;
  }
  if (RefiiStartupVideosPlaybackCriticalActive()) {
    return false;
  }

  static std::mutex mutex;
  static std::unordered_map<std::string, uint32_t> counts;

  std::lock_guard<std::mutex> lock(mutex);
  const uint32_t next_count = ++counts[std::string(api)];
  return next_count <= limit;
}

std::string DescribeGuestObject(uint8_t* base,
                                uint32_t object_guest,
                                uint32_t word_count = 8,
                                uint32_t slot_count = 8) {
  if (base == nullptr || object_guest == 0) {
    return "0x00000000";
  }

  return fmt::format("0x{:08X} words={} {}",
                     object_guest,
                     FormatGuestWords(base, object_guest, word_count),
                     FormatGuestObjectVTable(base, object_guest, slot_count));
}

std::string ReadMaybeGuestString(uint8_t* base,
                                 uint32_t guest,
                                 uint32_t max_chars = 4096) {
  if (base == nullptr || guest < 0x10000 || guest >= 0x90000000) {
    return {};
  }
  return ReadBestEffortGuestString(base, guest, max_chars);
}

bool IsTraceInterestingAsset(std::string_view asset_path) {
  const std::string normalized = NormalizeLooseAssetKey(asset_path);
  auto ends_with = [&normalized](std::string_view suffix) {
    return normalized.size() >= suffix.size() &&
           normalized.substr(normalized.size() - suffix.size()) == suffix;
  };
  return normalized.find("gameface") != std::string::npos ||
         normalized.find("frontendstartupscreen") != std::string::npos ||
         normalized.find("videos/") != std::string::npos ||
         normalized.find("speech.bnk") != std::string::npos ||
         normalized.find("lipsync.bnk") != std::string::npos ||
         normalized == "dir.manifest" ||
         normalized == "startup.vfsconfig" ||
         normalized == "gold_version.txt" ||
         ends_with(".bik") || ends_with(".bgf") || ends_with(".bsg") ||
         ends_with(".tex") || ends_with(".vfsconfig") ||
         ends_with(".fac");
}

bool IsMainMenuTextureTraceAsset(std::string_view asset_path) {
  const std::string normalized = NormalizeLooseAssetKey(asset_path);
  if (normalized.size() < 4 ||
      normalized.substr(normalized.size() - 4) != ".tex") {
    return false;
  }
  return normalized.find("icon_button_a.tex") != std::string::npos ||
	         normalized.find("fe_f2logo.tex") != std::string::npos ||
	         normalized.find("buttondetailmap.tex") != std::string::npos ||
	         normalized.find("framedetailmap.tex") != std::string::npos ||
	         normalized.find("frames_01.tex") != std::string::npos ||
	         normalized.find("panoramich.tex") != std::string::npos ||
	         normalized.find("loading_animation.tex") != std::string::npos;
}

bool IsMainMenuTextureHashTraceAsset(std::string_view asset_path) {
  const std::string normalized = NormalizeLooseAssetKey(asset_path);
  return IsMainMenuTextureTraceAsset(normalized) ||
         normalized.find("fxt_soft_glow.tex") != std::string::npos;
}

bool ShouldAllowOwnerlessSplitTextureWrapperCombine(std::string_view api,
                                                    std::string_view provider) {
  const std::string normalized_api = std::string(api);
  if (!IsTextureHeaderCandidate(provider)) {
    return false;
  }
  return normalized_api == "GetSize" || normalized_api == "GetBytes" ||
         normalized_api == "ResourceWrapper_Wait";
}

bool IsMainMenuTextureResourceHash(uint32_t resource_hash) {
  return resource_hash == 0x73BCB48Fu || resource_hash == 0xC5A8E73Cu;
}

void LogRefiiVfsChainVerdict(std::string_view chain_id,
                             uint32_t root_lr,
                             std::string_view asset,
                             std::string_view candidate,
                             std::string_view provider_relative,
                             std::string_view shape_owner,
                             std::string_view live_boundary,
                             std::string_view collapse_status,
                             std::string_view next_boundary) {
  if (!VfsTracingEnabled() && !LooseAssetOverrideTracingEnabled()) {
    return;
  }
  if (RefiiStartupVideosPlaybackCriticalActive()) {
    return;
  }

  const std::string normalized_asset = NormalizeLooseAssetKey(asset);
  const std::string normalized_candidate = NormalizeLooseAssetKey(candidate);
  const std::string normalized_provider = NormalizeLooseAssetKey(provider_relative);
  const std::string key_asset =
      !normalized_asset.empty()
          ? normalized_asset
          : (!normalized_candidate.empty() ? normalized_candidate
                                           : normalized_provider);
  const bool interesting =
      IsTraceInterestingAsset(key_asset) ||
      IsTraceInterestingAsset(normalized_candidate) ||
      IsTraceInterestingAsset(normalized_provider) ||
      chain_id == "lua" || chain_id == "native_shaped_stream" ||
      chain_id == "request_manager_hash";

  static std::mutex mutex;
  static std::unordered_set<std::string> emitted;
  {
    std::lock_guard<std::mutex> lock(mutex);
    const std::string key = fmt::format("{}|{}|{}|{}|{}",
                                        chain_id,
                                        key_asset,
                                        live_boundary,
                                        collapse_status,
                                        next_boundary);
    if (!emitted.insert(key).second) {
      return;
    }
    constexpr size_t kMaxChainVerdicts = 512;
    if (emitted.size() > kMaxChainVerdicts) {
      emitted.clear();
      emitted.insert(key);
    }
  }

  if (!interesting &&
      !ShouldLogRepeatedVfsTraceOperation("RefiiVfsChainVerdictGeneric", 96)) {
    return;
  }

  REXLOG_INFO("Refii VFS chain verdict chain={} lr=0x{:08X} asset={} "
              "candidate={} provider_relative={} shape_owner={} boundary={} "
              "status={} next={}",
              chain_id,
              root_lr,
              normalized_asset,
              normalized_candidate,
              normalized_provider,
              shape_owner,
              live_boundary,
              collapse_status,
              next_boundary);
}

bool IsResourceWrapperTextureTraceAsset(std::string_view asset_path) {
  const std::string normalized = NormalizeLooseAssetKey(asset_path);
  return IsMainMenuTextureTraceAsset(normalized) ||
         IsTextureHeaderCandidate(normalized);
}

// The texture code that calls the resource wrapper, as code spans per build. The unpatched
// relink spread TU1's first span over three: its functions are the same bodies at
// 0x82A6D9F0..0x82A6EF30, 0x82A6C0C0..0x82A6C5AC and 0x82A676C0..0x82A67D38 (TU1 0x82A79F98,
// 0x82A7B4E8 and 0x82A7B9A8 onward), and TU1's second span is 0x82B67418..0x82B68BB0 (TU1
// 0x82B6CE10..0x82B6E610; the two TU1 functions after that have no counterpart body there).
bool IsTextureResourceWrapperCaller(uint32_t lr) {
  struct Span {
    uint32_t begin;
    uint32_t end;
  };
  static constexpr Span kTu1[] = {{0x82A7A000u, 0x82A7C000u}, {0x82B6D000u, 0x82B6E800u}};
  static constexpr Span kUnpatched[] = {{0x82A6DA58u, 0x82A6EF30u},
                                        {0x82A6C0C0u, 0x82A6C5ACu},
                                        {0x82A676C0u, 0x82A67D18u},
                                        {0x82B67608u, 0x82B68BB0u}};
  const bool unpatched = refii::guest::Current() == refii::guest::Build::kUnpatched;
  const Span* begin = unpatched ? std::begin(kUnpatched) : std::begin(kTu1);
  const Span* end = unpatched ? std::end(kUnpatched) : std::end(kTu1);
  for (const Span* span = begin; span != end; ++span) {
    if (lr >= span->begin && lr < span->end) {
      return true;
    }
  }
  return false;
}

bool ShouldTraceNativeResourceWrapperApi(std::string_view api, uint32_t lr) {
  if (!VfsTracingEnabled() || !IsTextureResourceWrapperCaller(lr)) {
    return false;
  }
  return ShouldLogRepeatedVfsTraceOperation(
      fmt::format("NativeResourceWrapper{}", api), 32);
}

bool IsGameFaceBoundaryPath(std::string_view asset_path) {
  const std::string normalized = NormalizeLooseAssetKey(asset_path);
  return normalized.find("scripts/gameface") != std::string::npos ||
         normalized.find("guiscripts") != std::string::npos ||
         normalized.find("art/gui/gameface") != std::string::npos ||
         normalized.find("frontendstartupscreen") != std::string::npos;
}

bool ShouldLogFileApiPath(std::string_view api, std::string_view path) {
  if (RefiiStartupVideosPlaybackCriticalActive()) {
    return false;
  }
  if (IsGameFaceBoundaryPath(path)) {
    return VfsTracingEnabled();
  }
  if (!path.empty() && IsTraceInterestingAsset(path)) {
    return false;
  }
  return VfsTracingEnabled() && ShouldLogVfsTraceOperation(api);
}
}  // namespace detail
}  // namespace refii::vfs
