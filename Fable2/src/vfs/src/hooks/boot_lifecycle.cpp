#include <atomic>
#include <unordered_map>
#include <chrono>
#include <cstring>
#include <cstdlib>
#include <thread>
#include "vfs_hooks_private.h"
#include "core/payload_bridge_fable2.h"
#include "core/precise_sleep.h"

#include <rex/system/xthread.h>
#include "core/guest_build.h"

namespace {
constexpr refii::guest::Addr kFableRendererFrameCriticalSection{0x8349F7FCu};
constexpr uint32_t kFableCriticalSectionRecursionOffset = 0x14u;
constexpr uint32_t kFableCriticalSectionOwnerOffset = 0x18u;
std::atomic<uint32_t> g_fable_renderer_dynamic_critical_section{0};

bool ShouldLogNativeStartupPlaybackAudit(std::string_view key,
                                         uint32_t first_count,
                                         uint32_t interval) {

  if (!refii::vfs::kTraceFrontendHandoffDiagnostics ||
      !refii::vfs::VfsTracingEnabled() ||
      !refii::vfs::VfsVerboseIoTracingEnabled()) {
    return false;
  }
  if (refii::vfs::RefiiStartupVideosPlaybackCriticalActive()) {
    return false;
  }

  static std::mutex mutex;
  static std::unordered_map<std::string, uint32_t> counts;

  std::lock_guard<std::mutex> lock(mutex);
  const uint32_t next_count = ++counts[std::string(key)];
  return next_count <= first_count ||
         (interval != 0 && (next_count % interval) == 0);
}

uint32_t CurrentFableGuestThreadObject() {
  if (!rex::system::XThread::IsInThread()) {
    return 0;
  }
  return rex::system::XThread::GetCurrentThread()->guest_object();
}

uint32_t FableCriticalSectionRecursionDepthForCurrentThread(
    uint8_t *base, uint32_t critical_section_guest) {
  if (base == nullptr || critical_section_guest == 0) {
    return 0;
  }
  const uint32_t owner_guest = refii::vfs::LoadGuestU32(
      base, critical_section_guest + kFableCriticalSectionOwnerOffset);
  if (owner_guest == 0 || owner_guest != CurrentFableGuestThreadObject()) {
    return 0;
  }
  return std::min<uint32_t>(
      refii::vfs::LoadGuestU32(
          base, critical_section_guest + kFableCriticalSectionRecursionOffset),
      16u);
}

uint32_t FableRendererCriticalSectionForCurrentThread(uint8_t *base) {
  const uint32_t dynamic =
      g_fable_renderer_dynamic_critical_section.load(std::memory_order_acquire);
  if (FableCriticalSectionRecursionDepthForCurrentThread(base, dynamic) != 0) {
    return dynamic;
  }
  return kFableRendererFrameCriticalSection;
}

uint32_t ReleaseFableCriticalSectionForStartupVideoWait(
    const PPCContext &ctx, uint8_t *base, uint32_t critical_section_guest) {
  const uint32_t release_count =
      FableCriticalSectionRecursionDepthForCurrentThread(base,
                                                         critical_section_guest);
  for (uint32_t i = 0; i < release_count; ++i) {
    PPCContext critical_ctx = ctx;
    critical_ctx.r3.u32 = critical_section_guest;
    __imp__RtlLeaveCriticalSection(critical_ctx, base);
  }
  return release_count;
}

void ReacquireFableCriticalSectionForStartupVideoWait(
    const PPCContext &ctx, uint8_t *base, uint32_t critical_section_guest,
    uint32_t acquire_count) {
  for (uint32_t i = 0; i < acquire_count; ++i) {
    PPCContext critical_ctx = ctx;
    critical_ctx.r3.u32 = critical_section_guest;
    __imp__RtlEnterCriticalSection(critical_ctx, base);
  }
}

bool ShouldTraceGameDataBootBoundary(uint32_t lr) {
  if (!refii::vfs::kTraceFrontendHandoffDiagnostics ||
      !refii::vfs::VfsTracingEnabled()) {
    return false;
  }

  switch (refii::guest::ToTu1(lr)) {
  case 0x822F4094:
  case 0x822F40CC:
  case 0x822F4124:
  case 0x822F413C:
  case 0x822F4140:
  case 0x822F4170:
  case 0x822F41B0:
  case 0x822F41F0:
  case 0x822F4208:
  case 0x822F443C:
  case 0x822F49E8:
    return true;
  default:
    return false;
  }
}
} // namespace

// D3D_InitializeRingBuffer, 0x82BA2830 — the no-GPU latch is armed IN-LINE, on the guest's
// own thread, the moment ring setup completes. The boot race this kills: with no GPU,
// `VdEnableRingBufferRPtrWriteBack` is a no-op, and the first latch-clear reader commits the
// device to GPU-completion waits that never finish — `D3D_CDevice_KickOff` (0x821e8f70) parks
// in `D3D_CDevice_BlockOnFence`, and `D3D_CDevice_SetFence` (0x822986ec) silently skips the
// completion write-back for any fence issued while the bit is down. The host's step-loop arm
// polls at 30 Hz and lost that race roughly half of one day's launches.
//
// This site is provably early enough: the wait gate also requires `dword_83497144`
// (HSIO-training-failed, 0x82ba6be0), which is still 0 until the device-init tail — after this
// function returns and after this write. So no wait-capable reader can ever see the bit clear.
// Re-runs on a device reset re-arm the same byte; the OR is idempotent. The hostbridge's own
// arm (`ArmGuestNoGpuMode`) stays as the belt for any path that skips ring init.
REX_EXTERN(rex_D3DDevice_InitializeRingBuffer) {
  REX_FUNC_PROLOGUE();

  const uint32_t device = ctx.r3.u32;
  __imp__rex_D3DDevice_InitializeRingBuffer(ctx, base);

  if (device != 0u) {
    const uint8_t before = base[device + 0x2ABDu];
    base[device + 0x2ABDu] = static_cast<uint8_t>(before | 0x02u);
    REXLOG_INFO(
        "Refii boot: no-GPU latch armed in-line after ring-buffer init "
        "(device=0x{:08X}, 0x{:02X} -> 0x{:02X})",
        device, before, before | 0x02u);
  }
}

REX_EXTERN(rex_lhRenderer_SuspendThread) {
  REX_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const uint32_t owner_guest = original_ctx.r3.u32;
  const uint32_t handoff_guest =
      owner_guest != 0 ? refii::vfs::LoadGuestU32(base, owner_guest + 4) : 0;
  const bool native_startup_frame_wait = original_ctx.lr == refii::guest::Address(0x823890ECu);

  const auto begin = std::chrono::steady_clock::now();
  refii::vfs::RecordRefiiRenderProducerScheduleSnapshot(
      base, owner_guest, original_ctx.lr, original_ctx.r6.u32,
      original_ctx.r5.u32, original_ctx.f1.f64);
  const bool trace = refii::vfs::ShouldTracePostVideoTextureOwnedHandoffProbe(
                         "StartupVideoFrame_823889A8") ||
                     (refii::vfs::kTraceNonVfsBootFrameProbes &&
                      refii::vfs::VfsTracingEnabled() &&
                      (original_ctx.lr == refii::guest::Address(0x823890ECu) ||
                       refii::vfs::ShouldTracePostVideoBoundaryProbe(
                           "PostVideoFrame_823889A8")));
  if (trace) {
    REXLOG_INFO("VFS trace startup-video frame handshake enter sub_823889A8 "
                "lr=0x{:08X} owner=0x{:08X} handoff=0x{:08X} "
                "state=[{}] args=[{}] post_video=[{}] main_menu=[{}]",
                original_ctx.lr, owner_guest, handoff_guest,
                refii::vfs::FormatPostVideoFrameWaitState(base, owner_guest),
                refii::vfs::FormatPostVideoBoundaryArgs(base, original_ctx),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  const uint32_t renderer_critical_section =
      FableRendererCriticalSectionForCurrentThread(base);
  const uint32_t render_frame_lock_release_count =
      native_startup_frame_wait
          ? ReleaseFableCriticalSectionForStartupVideoWait(
                ctx, base, renderer_critical_section)
          : 0u;
  __imp__rex_lhRenderer_SuspendThread(ctx, base);
  if (render_frame_lock_release_count != 0) {
    ReacquireFableCriticalSectionForStartupVideoWait(
        original_ctx, base, renderer_critical_section,
        render_frame_lock_release_count);
  }

  const int64_t elapsed_ms =
      std::chrono::duration_cast<std::chrono::milliseconds>(
          std::chrono::steady_clock::now() - begin)
          .count();
  if (refii::vfs::RefiiPostVideoVfsHandoffTraceActive()) {
    refii::vfs::RecordRefiiPostVideoStageTiming("sub_823889A8", original_ctx.lr,
                                                ctx.r3.u32, elapsed_ms);
  }
  if (trace) {
    refii::vfs::RecordRefiiPostVideoUiEvent(
        "StartupVideoFrame_823889A8", original_ctx.lr, ctx.r3.u32, owner_guest,
        handoff_guest, original_ctx.r5.u32);
    REXLOG_INFO("VFS trace startup-video frame handshake exit sub_823889A8 "
                "lr=0x{:08X} result=0x{:08X} elapsed_ms={} state_after=[{}] "
                "post_video=[{}] main_menu=[{}]",
                original_ctx.lr, ctx.r3.u32, elapsed_ms,
                refii::vfs::FormatPostVideoFrameWaitState(base, owner_guest),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  if (native_startup_frame_wait &&
      (elapsed_ms >= 50 || render_frame_lock_release_count != 0) &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "StartupVideoFrameWaitNativeHandoff", 16)) {
    REXLOG_INFO(
        "Refii VFS startup-video native handoff wait "
        "lr=0x{:08X} result=0x{:08X} elapsed_ms={} "
        "released_render_frame_lock={} owner=0x{:08X} handoff=0x{:08X} "
        "state_after=[{}]",
        original_ctx.lr,
        ctx.r3.u32,
        elapsed_ms,
        render_frame_lock_release_count,
        owner_guest,
        handoff_guest,
        refii::vfs::FormatPostVideoFrameWaitState(base, owner_guest));
  }
  if (elapsed_ms >= 250 && refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "PostVideoStageSlow_sub_823889A8", 8)) {
    REXLOG_INFO("Refii VFS post-video stage slow "
                "stage=sub_823889A8 lr=0x{:08X} result=0x{:08X} "
                "elapsed_ms={} stage_timing=[{}] handoff=[{}] main_menu=[{}]",
                original_ctx.lr, ctx.r3.u32, elapsed_ms,
                refii::vfs::FormatRefiiPostVideoStageTimingForLog(),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
}

// ============================================================================================
// THE MOVIE'S DESTINATION RECT, as the guest states it. `sub_82A41E30(B, rect, z, rotation,
// mirrorU, mirrorV)` is a leaf: r3 is the player B (the r3 `sub_82A41B48` opens, and the base
// every Bink offset in this tree is measured from), r4 points at four SIGNED INTS — left, top,
// right, bottom in back-buffer pixels — which it converts and stores as floats at B+8/+0xC/
// +0x10/+0x14 (`extsw; std; lfd; fcfid; frsp; stfs` x4, 0x82A41E30..0x82A41EB8). The draw
// `sub_82A41F10` builds its quad from those as two corners.
//
// All four callers state it AFTER the open, 12-29 instructions later: the two front-end
// constructors (`sub_82388ED0` 0x82389018, `sub_82389040` 0x82389148) and the cutscene player
// (`sub_823C1658` 0x823C1A04) once, the UI sprite's movie component (`sub_82A83E20`
// 0x82A83EDC) on create and again on every element update. No constructor initialises
// B+8..B+0x3C, so until this call those bytes are whatever the heap held.
//
// // The host takes the ARGUMENTS here: polling the floats out of B between the open and this
// // call reads that uninitialised heap as a rect.
// ============================================================================================
REX_EXTERN(rex_lhBinkPlayer_SetDestRect) {
  REX_FUNC_PROLOGUE();
  const uint32_t player = ctx.r3.u32;
  const uint32_t rect = ctx.r4.u32;
  __imp__rex_lhBinkPlayer_SetDestRect(ctx, base);
  if (player == 0 || rect == 0) {
    return;
  }

  refii::vfs::NoteBinkMovieRect(
      player,
      static_cast<int32_t>(refii::vfs::LoadGuestU32(base, rect)),
      static_cast<int32_t>(refii::vfs::LoadGuestU32(base, rect + 4)),
      static_cast<int32_t>(refii::vfs::LoadGuestU32(base, rect + 8)),
      static_cast<int32_t>(refii::vfs::LoadGuestU32(base, rect + 12)));
}

// A TRACK'S VOLUME, as the guest states it. `sub_82A41D88(B, id, volume)` clamps f1 to 0..1
// and calls `BinkSetVolume(*(B+64), id, volume * 32768)`; it does nothing when B+222 (the
// sound byte) is clear (0x82A41DA4). The cutscene player states two after every open: track 0
// at `movie+84` and the voice track at `movie+88` (0x823C1A20, 0x823C1A30).
//
// The arguments are taken BEFORE the call: the open hook below clears the sound byte, so the
// original returns early and the guest's copy of the track stays silent.
REX_EXTERN(rex_lhBinkPlayer_SetTrackVolume) {
  REX_FUNC_PROLOGUE();
  refii::vfs::NoteBinkVolume(ctx.r3.u32, ctx.r4.u32, static_cast<float>(ctx.f1.f64));
  __imp__rex_lhBinkPlayer_SetTrackVolume(ctx, base);
}

REX_EXTERN(rex_lhBinkPlayer_Open) {
  REX_FUNC_PROLOGUE();

  // THE HOST PLAYS THE SOUND, from the same decode that produces the picture. The guest's
  // `sound` argument (r5, stored at B+222 by `stb r5, 0xDE(r30)` 0x82A41B68) is recorded and
  // then passed on as 0, which takes the opener's own mute branch — `BinkSetVolume(bink, id,
  // 0)` for ids 0..4 at 0x82A41C48..0x82A41C88. The guest's voices still run, silent, so
  // `BinkWait` keeps its audio clock. Without this the movie is heard twice, the guest's copy
  // on the guest's own decode timing.
  refii::vfs::NoteBinkSound(base, ctx.r3.u32, ctx.r5.u32 & 0xFFu);
  ctx.r5.u64 = 0;

  // r4 is the file name handed to BinkOpen (`sub_832BB4B8(a2, *(a1 + 224))`). Capturing
  // it is what lets the host play the video itself rather than scraping the guest's
  // decoded planes — no dependence on the guest's render path or its frame timing. Read
  // BEFORE the call, which is free to clobber the argument registers.
  refii::vfs::NoteBinkVideoRequested(base, ctx.r4.u32);

  // Bink movie open. r3 is B, the BINK object; saved now because the call returns its own
  // value in r3.
  const uint32_t bink_object = ctx.r3.u32;

  // BINKFRAMEBUFFERS is inline at player+68 (sub_82A42D20: `v2 = a1 + 68;
  // memset(a1 + 68, 0, 0x78); BinkGetFrameBuffersInfo(*(a1 + 64), v2)`), so the ADDRESS is
  // valid on entry even though the contents are filled during the call.
  //
  // PUBLISHED AFTER THE CALL: `sub_82A42D20` fills the frame-buffer block during it, and a
  // host that saw the player earlier would read that block half-built.
  //
  // The rect is a second guest call and cannot be atomic with this one. It does not need to
  // be: publishing the player CLEARS the stated rect, and `BinkMovieDestRect` answers "none
  // yet" until the `sub_82A41E30` hook above states one for this player.
  __imp__rex_lhBinkPlayer_Open(ctx, base);
  refii::vfs::NoteBinkMovieOpen(bink_object, base);
  return;

  const PPCContext original_ctx = ctx;
  const auto begin = std::chrono::steady_clock::now();
  const bool trace =
      refii::vfs::ShouldTracePostVideoTextureOwnedHandoffProbe(
          "StartupVideoFrame_82A41B48") ||
      (refii::vfs::kTraceNonVfsBootFrameProbes &&
       refii::vfs::VfsTracingEnabled() &&
       (original_ctx.lr == refii::guest::Address(0x82389110u) || original_ctx.lr == refii::guest::Address(0x82388FA8u) ||
        refii::vfs::ShouldTracePostVideoBoundaryProbe(
            "PostVideoFrame_82A41B48")));
  if (trace) {
    REXLOG_INFO(
        "VFS trace startup-video frame target build enter sub_82A41B48 "
        "lr=0x{:08X} args=[{}] frame_state=[{}] "
        "frame_source_words={} post_video=[{}] main_menu=[{}]",
        original_ctx.lr,
        refii::vfs::FormatPostVideoBoundaryArgs(base, original_ctx),
        refii::vfs::FormatPostVideoFrameTeardownState(base,
                                                      original_ctx.r3.u32),
        refii::vfs::FormatGuestWordsIfPointer(base, original_ctx.r4.u32, 8),
        refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
        refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__rex_lhBinkPlayer_Open(ctx, base);

  const int64_t elapsed_ms =
      std::chrono::duration_cast<std::chrono::milliseconds>(
          std::chrono::steady_clock::now() - begin)
          .count();
  if (refii::vfs::RefiiPostVideoVfsHandoffTraceActive()) {
    refii::vfs::RecordRefiiPostVideoStageTiming("sub_82A41B48", original_ctx.lr,
                                                ctx.r3.u32, elapsed_ms);
  }

  if (trace) {
    refii::vfs::RecordRefiiPostVideoUiEvent(
        "StartupVideoFrame_82A41B48", original_ctx.lr, ctx.r3.u32,
        original_ctx.r3.u32, original_ctx.r4.u32, original_ctx.r5.u32);
    REXLOG_INFO("VFS trace startup-video frame target build exit sub_82A41B48 "
                "lr=0x{:08X} result=0x{:08X} elapsed_ms={} "
                "frame_state_after=[{}] post_video=[{}] main_menu=[{}]",
                original_ctx.lr, ctx.r3.u32, elapsed_ms,
                refii::vfs::FormatPostVideoFrameTeardownState(
                    base, original_ctx.r3.u32),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  if (ctx.r3.u32 == 0 && original_ctx.lr == refii::guest::Address(0x823C19D8u) &&
      refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "PostLoadingAfterHolderBinkReject", 12)) {
    const std::string source_text =
        refii::vfs::ReadMaybeGuestString(base, original_ctx.r4.u32, 512);
    REXLOG_INFO(
        "Refii VFS post-loading after-holder Bink reject "
        "lr=0x{:08X} child=0x{:08X} source=0x{:08X} "
        "source_text={} source_words=[{}] child_words=[{}] "
        "flags220_224=0x{:02X}/0x{:02X}/0x{:02X}/0x{:02X}/0x{:08X} "
        "elapsed_ms={} handoff=[{}] main_menu=[{}]",
        original_ctx.lr, original_ctx.r3.u32, original_ctx.r4.u32,
        source_text.empty() ? "<unreadable>" : source_text,
        refii::vfs::FormatGuestWordsIfPointer(base, original_ctx.r4.u32, 8),
        refii::vfs::FormatGuestWordsIfPointer(base, original_ctx.r3.u32, 64),
        refii::vfs::LooksLikeGuestPointer(original_ctx.r3.u32)
            ? refii::vfs::LoadGuestU8(base, original_ctx.r3.u32 + 220)
            : 0,
        refii::vfs::LooksLikeGuestPointer(original_ctx.r3.u32)
            ? refii::vfs::LoadGuestU8(base, original_ctx.r3.u32 + 221)
            : 0,
        refii::vfs::LooksLikeGuestPointer(original_ctx.r3.u32)
            ? refii::vfs::LoadGuestU8(base, original_ctx.r3.u32 + 222)
            : 0,
        refii::vfs::LooksLikeGuestPointer(original_ctx.r3.u32)
            ? refii::vfs::LoadGuestU8(base, original_ctx.r3.u32 + 223)
            : 0,
        refii::vfs::LooksLikeGuestPointer(original_ctx.r3.u32)
            ? refii::vfs::LoadGuestU32(base, original_ctx.r3.u32 + 224)
            : 0,
        elapsed_ms, refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
        refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  if (elapsed_ms >= 250 && refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "PostVideoStageSlow_sub_82A41B48", 8)) {
    REXLOG_INFO("Refii VFS post-video stage slow "
                "stage=sub_82A41B48 lr=0x{:08X} result=0x{:08X} "
                "elapsed_ms={} stage_timing=[{}] handoff=[{}] main_menu=[{}]",
                original_ctx.lr, ctx.r3.u32, elapsed_ms,
                refii::vfs::FormatRefiiPostVideoStageTimingForLog(),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
}

REX_EXTERN(rex_lhBinkMoviePlayer_Ctor) {
  REX_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const auto begin = std::chrono::steady_clock::now();
  const bool native_startup_frame_construct =
      original_ctx.lr == refii::guest::Address(0x82377CD4u);
  const bool native_startup_audit =
      native_startup_frame_construct &&
      ShouldLogNativeStartupPlaybackAudit("NativeStartupFrame_82389040", 4, 1);
  const bool trace =
      refii::vfs::ShouldTracePostVideoTextureOwnedHandoffProbe(
          "StartupVideoFrame_82389040") ||
      (refii::vfs::kTraceNonVfsBootFrameProbes &&
       refii::vfs::VfsTracingEnabled() && original_ctx.lr != refii::guest::Address(0x82377CD4u) &&
       refii::vfs::ShouldTracePostVideoBoundaryProbe(
           "PostVideoFrame_82389040"));
  if (native_startup_audit && !trace) {
    REXLOG_INFO(
        "Refii VFS native startup frame construct enter sub_82389040 "
        "lr=0x{:08X} frame_object=0x{:08X} render_resource=0x{:08X} "
        "frame_source=0x{:08X} source_words={}",
        original_ctx.lr, original_ctx.r3.u32, original_ctx.r4.u32,
        original_ctx.r5.u32,
        refii::vfs::FormatGuestWordsIfPointer(base, original_ctx.r5.u32, 4));
  }
  if (trace) {
    REXLOG_INFO(
        "VFS trace startup-video frame object construct enter "
        "sub_82389040 lr=0x{:08X} args=[{}] frame_object=0x{:08X} "
        "render_resource=0x{:08X} frame_source=0x{:08X} "
        "frame_source_words={} post_video=[{}] main_menu=[{}]",
        original_ctx.lr,
        refii::vfs::FormatPostVideoBoundaryArgs(base, original_ctx),
        original_ctx.r3.u32, original_ctx.r4.u32, original_ctx.r5.u32,
        refii::vfs::FormatGuestWordsIfPointer(base, original_ctx.r5.u32, 8),
        refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
        refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  const uint32_t renderer_critical_section =
      FableRendererCriticalSectionForCurrentThread(base);
  const uint32_t render_frame_lock_release_count =
      native_startup_frame_construct
          ? ReleaseFableCriticalSectionForStartupVideoWait(
                ctx, base, renderer_critical_section)
          : 0u;
  __imp__rex_lhBinkMoviePlayer_Ctor(ctx, base);
  if (render_frame_lock_release_count != 0) {
    ReacquireFableCriticalSectionForStartupVideoWait(
        original_ctx, base, renderer_critical_section,
        render_frame_lock_release_count);
  }

  const int64_t elapsed_ms =
      std::chrono::duration_cast<std::chrono::milliseconds>(
          std::chrono::steady_clock::now() - begin)
          .count();
  if (refii::vfs::RefiiPostVideoVfsHandoffTraceActive()) {
    refii::vfs::RecordRefiiPostVideoStageTiming("sub_82389040", original_ctx.lr,
                                                ctx.r3.u32, elapsed_ms);
  }

  if (trace || (native_startup_audit && !trace)) {
    refii::vfs::RecordRefiiPostVideoUiEvent(
        "StartupVideoFrame_82389040", original_ctx.lr, ctx.r3.u32,
        original_ctx.r3.u32, original_ctx.r4.u32, original_ctx.r5.u32);
  }
  if (native_startup_audit && !trace) {
    REXLOG_INFO(
        "Refii VFS native startup frame construct exit sub_82389040 "
        "lr=0x{:08X} result=0x{:08X} elapsed_ms={} "
        "frame_object_after={}",
        original_ctx.lr, ctx.r3.u32, elapsed_ms,
        refii::vfs::FormatGuestWordsIfPointer(base, original_ctx.r3.u32, 8));
  }
  if (native_startup_frame_construct &&
      (elapsed_ms >= 50 || render_frame_lock_release_count != 0)) {
    static std::atomic<uint32_t> startup_construct_logs{0};
    const uint32_t count =
        startup_construct_logs.fetch_add(1, std::memory_order_relaxed) + 1u;
    if (count <= 16u || (count % 120u) == 0u) {
      REXLOG_INFO(
          "Refii VFS startup-video native frame construct handoff #{} "
          "lr=0x{:08X} result=0x{:08X} elapsed_ms={} "
          "released_render_frame_lock={} frame_object=0x{:08X} "
          "render_resource=0x{:08X} frame_source=0x{:08X} "
          "finished={} failed={} bink=0x{:08X} bink_frame={}/{}",
          count, original_ctx.lr, ctx.r3.u32, elapsed_ms,
          render_frame_lock_release_count, original_ctx.r3.u32,
          original_ctx.r4.u32, original_ctx.r5.u32,
          refii::vfs::LoadGuestU8(base, original_ctx.r3.u32 + 233u),
          refii::vfs::LoadGuestU8(base, original_ctx.r3.u32 + 244u),
          refii::vfs::LoadGuestU32(base, original_ctx.r3.u32 + 76u),
          refii::vfs::LoadGuestU32(
              base,
              refii::vfs::LoadGuestU32(base, original_ctx.r3.u32 + 76u) +
                  12u),
          refii::vfs::LoadGuestU32(
              base,
              refii::vfs::LoadGuestU32(base, original_ctx.r3.u32 + 76u) +
                  8u));
    }
  }
  if (trace) {
    REXLOG_INFO(
        "VFS trace startup-video frame object construct exit "
        "sub_82389040 lr=0x{:08X} result=0x{:08X} elapsed_ms={} "
        "frame_object_after={} post_video=[{}] main_menu=[{}]",
        original_ctx.lr, ctx.r3.u32, elapsed_ms,
        refii::vfs::FormatGuestWordsIfPointer(base, original_ctx.r3.u32, 16),
        refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
        refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
  if (elapsed_ms >= 250 && refii::vfs::RefiiPostVideoVfsHandoffTraceActive() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "PostVideoStageSlow_sub_82389040", 8)) {
    REXLOG_INFO("Refii VFS post-video stage slow "
                "stage=sub_82389040 lr=0x{:08X} result=0x{:08X} "
                "elapsed_ms={} stage_timing=[{}] handoff=[{}] main_menu=[{}]",
                original_ctx.lr, ctx.r3.u32, elapsed_ms,
                refii::vfs::FormatRefiiPostVideoStageTimingForLog(),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
}

REX_EXTERN(rex_lhGame_UpdateThread) {
  REX_FUNC_PROLOGUE();

  // Brackets the "Permanent Bank Mounter" thread body (`0x822F3898`, spawned by `sub_822F3660`).
  // It ends by setting the byte `sub_822F3B20` returns, which `sub_82377A00` polls at
  // `0x82377D74` with `Sleep(100)`; enter with no exit means no front end.
  const PPCContext original_ctx = ctx;
  REXLOG_INFO("Refii boot: bank mounter ENTER lr=0x{:08X} ctx=0x{:08X}",
              original_ctx.lr, original_ctx.r3.u32);

  __imp__rex_lhGame_UpdateThread(ctx, base);

  REXLOG_INFO("Refii boot: bank mounter EXIT lr=0x{:08X} result=0x{:08X}",
              original_ctx.lr, ctx.r3.u32);
}

REX_EXTERN(rex_lhGameData_Init) {
  REX_FUNC_PROLOGUE();

  // Brackets the mounter's first stage, `0x822F3FF8` (vfsconfig / permanent resources) — the only
  // stage reaching an unbounded wait (`WaitForSingleObjectEx(evt+0x80, INFINITE)`).
  const PPCContext original_ctx = ctx;
  REXLOG_INFO("Refii boot: mounter stage 1 (vfsconfig/permanent resources) ENTER "
              "lr=0x{:08X} game=0x{:08X}",
              original_ctx.lr, original_ctx.r3.u32);

  __imp__rex_lhGameData_Init(ctx, base);

  REXLOG_INFO("Refii boot: mounter stage 1 EXIT lr=0x{:08X} result=0x{:08X}",
              original_ctx.lr, ctx.r3.u32);
}

REX_EXTERN(rex_lhFile_CloseAndRelease) {
  REX_FUNC_PROLOGUE();

  // No native passthrough macro: it would return before the Refii-owned check. The call to the
  // original at the end is the passthrough for the guest's own objects.

  const PPCContext original_ctx = ctx;
  const bool trace = ShouldTraceGameDataBootBoundary(original_ctx.lr);
  if (trace) {
    REXLOG_INFO("VFS trace GameData file close/release enter lr=0x{:08X} "
                "file=0x{:08X} words=0x{:08X} 0x{:08X} 0x{:08X} 0x{:08X}",
                original_ctx.lr, original_ctx.r3.u32,
                refii::vfs::LooksLikeGuestPointer(original_ctx.r3.u32)
                    ? refii::vfs::LoadGuestU32(base, original_ctx.r3.u32 + 0)
                    : 0,
                refii::vfs::LooksLikeGuestPointer(original_ctx.r3.u32)
                    ? refii::vfs::LoadGuestU32(base, original_ctx.r3.u32 + 4)
                    : 0,
                refii::vfs::LooksLikeGuestPointer(original_ctx.r3.u32)
                    ? refii::vfs::LoadGuestU32(base, original_ctx.r3.u32 + 8)
                    : 0,
                refii::vfs::LooksLikeGuestPointer(original_ctx.r3.u32)
                    ? refii::vfs::LoadGuestU32(base, original_ctx.r3.u32 + 12)
                    : 0);
  }

  if (original_ctx.lr == refii::guest::Address(0x822F4208u) &&
      refii::vfs::LooksLikeGuestPointer(original_ctx.r3.u32)) {
    const uint32_t vtable = refii::vfs::LoadGuestU32(base, original_ctx.r3.u32);
    const uint32_t total_stream =
        refii::vfs::LoadGuestU32(base, original_ctx.r3.u32 + 8);
    const uint32_t read_stream =
        refii::vfs::LoadGuestU32(base, original_ctx.r3.u32 + 12);
    const bool has_refii_stream =
        refii::vfs::IsRefiiHostDiskStream(base, total_stream) ||
        refii::vfs::IsRefiiHostDiskStream(base, read_stream);
    const bool known_lh_file_vtable =
        vtable == refii::guest::Address(0x8200DF08u) || vtable == refii::guest::Address(0x820A4928u);
    if (!known_lh_file_vtable) {
      PPCContext release_ctx = original_ctx;
      if (refii::vfs::IsRefiiHostDiskStream(base, total_stream)) {
        refii::vfs::ReleaseGuestObject(release_ctx, base, total_stream);
      }
      if (read_stream != total_stream &&
          refii::vfs::IsRefiiHostDiskStream(base, read_stream)) {
        refii::vfs::ReleaseGuestObject(release_ctx, base, read_stream);
      }
      if (trace || refii::vfs::VfsTracingEnabled()) {
        REXLOG_WARN("Refii VFS skipped invalid GameData lhFile final close "
                    "lr=0x{:08X} file=0x{:08X} vtable=0x{:08X} "
                    "total=0x{:08X} read=0x{:08X} released_refii={}",
                    original_ctx.lr, original_ctx.r3.u32, vtable, total_stream,
                    read_stream, has_refii_stream);
      }
      ctx.r3.u32 = original_ctx.r3.u32;
      return;
    }
  }

  __imp__rex_lhFile_CloseAndRelease(ctx, base);

  if (trace) {
    REXLOG_INFO("VFS trace GameData file close/release exit lr=0x{:08X} "
                "result=0x{:08X}",
                original_ctx.lr, ctx.r3.u32);
  }
}

// ============================================================================================
// PRESENT PACING — the vblank the null device does not have.
//
// On hardware every swap waited for a vblank (presentation interval 2 at 60 Hz = 30 fps) and
// the CPU felt it through the swap fence in `D3DDevice_Swap`. The no-GPU latch makes every
// fence pass at once, so the 3D Engine thread presented ~186 times a second into nothing,
// two cores spinning (measured 2026-09-04) and Godot's main thread starved on a 16-core box
// at load 22. `rex_lhRenderer_EndScene` (0x82B6F1D0, `Render_PresentFrame`) is the guest's
// present; after it returns this thread sleeps until the next 1/REFII_PRESENT_HZ boundary,
// the wait a vblank would have imposed. The completion publisher stamps completions in real
// time, so the sim clock (`sub_82B6FCE8` from the completion stamp) sees a steady 30 Hz.
// REFII_PRESENT_HZ=0 leaves the guest unthrottled.
// ============================================================================================
namespace {
std::chrono::steady_clock::duration PresentPeriod() {
  static const std::chrono::steady_clock::duration period = [] {
    const char* value = std::getenv("REFII_PRESENT_HZ");
    int hz = 30;
    if (value != nullptr) {
      hz = std::atoi(value);
    }
    if (hz <= 0) {
      return std::chrono::steady_clock::duration::zero();
    }
    return std::chrono::duration_cast<std::chrono::steady_clock::duration>(
        std::chrono::duration<double>(1.0 / hz));
  }();
  return period;
}

void PaceGuestPresent() {
  const auto period = PresentPeriod();
  if (period == std::chrono::steady_clock::duration::zero()) {
    return;
  }

  static std::chrono::steady_clock::time_point next{};
  const auto now = std::chrono::steady_clock::now();
  if (next < now - period) {
    next = now;  // fell behind (a load, a stall): resume from now rather than catch up
  }

  if (next > now) {
    refii::PreciseSleepUntil(next);
  }

  next += period;
}
}  // namespace

REX_EXTERN(rex_lhRenderer_EndScene) {
  REX_FUNC_PROLOGUE();
  const uint32_t caller = ctx.lr;
  __imp__rex_lhRenderer_EndScene(ctx, base);

  // NOT for the 3D Engine's own present (0x8238866C in `sub_82388380`): that thread holds the
  // engine context lock across its whole frame (entered at 0x82388270 and again at 0x823883E4,
  // released only at 0x8238832C), and a 33 ms sleep under it left the lock free for
  // microseconds a frame. The kernel's auto-reset event (`PosixCondition<Event>`: a flag and a
  // condition variable) lets the leaving thread consume its own wake-up when it re-enters at
  // // once, so a queued game-thread waiter starves: the engine pause request before each boot
  // // movie (`sub_823889A8` -> RtlEnterCriticalSection). That present is paced after the frame
  // // instead — see `sub_82A1AC40` below.
  if (caller != refii::guest::Address(0x82388670u)) {
    PaceGuestPresent();
  }
}

// Slot 3 of the app object's vtable (`off_82002818` + 12, built in `sub_822EA228`), called by
// the 3D Engine thread proc at 0x82388344 once per loop — after it released the engine context
// lock at 0x8238832C and before it re-enters at 0x82388270. The only point in the loop where the
// lock is free, so the frame's pacing sleep lives here: every waiter gets a whole present
// period to take the lock, and the frame itself is paced exactly as before.
REX_EXTERN(rex_lhApp_IdleWaitForFlags) {
  REX_FUNC_PROLOGUE();
  const uint32_t caller = ctx.lr;
  __imp__rex_lhApp_IdleWaitForFlags(ctx, base);
  if (caller == refii::guest::Address(0x82388348u)) {
    PaceGuestPresent();
  }
}

// ============================================================================================
// THE GUEST FRAME. `sub_821A61B0(loop, alpha)` is the 30 Hz HF pass of the main loop
// (`sub_82276D10`, game thread): it runs after the game tick and writes every character's
// pose into comp6+0x30 (`sub_8220D1B8` → `sub_82204FF0` → `sub_821F2C78`) with the camera.
// The guest keeps no counter of it — the HF index is a local — so this is the one place the
// host learns that a new frame of guest state is in memory. Godot reads that state once per
// count and interpolates between counts.
// ============================================================================================
// The top GameFlow command's pose slot is the pass's last call (0x821A623C..54 / disc 0x821A609C..B4); under
// the player's own pause in an online session the world's poses follow it (coop_hero_hooks.cpp).
extern "C" void RefiiCoopOwnPauseRunsPoses(PPCContext* ctx, uint8_t* base, double alpha);

REX_EXTERN(rex_lhGameLoop_HighFrequencyUpdate) {
  REX_FUNC_PROLOGUE();
  const double alpha = ctx.f1.f64;
  __imp__rex_lhGameLoop_HighFrequencyUpdate(ctx, base);
  RefiiCoopOwnPauseRunsPoses(&ctx, base, alpha);
  refii_bridge_note_guest_frame();
}

// ============================================================================================
// THE NULL RENDER FRAME. Godot renders; the guest's 3D Engine thread still walked its scene,
// built every draw and handed it to a device that discards it — ~30% of a core after pacing,
// two cores before. `Render_SubPassOrchestrator_INFERRED` (sub_821C3C60) is the whole draw:
// material/sampler setup, the render context and frustum, shader constants, the opaque pass,
// the transparent/post pass, the fullscreen effects. Both call sites in `sub_821C3858`
// (0x821C3B38, 0x821C3B4C) branch past its result. Everything the rest of the game reads
// back happens OUTSIDE it and still runs: the ring replay and fence that the GameThread waits
// on (`sub_822DFE08`, opcode 17), view create/destroy, the preload gate → `IsEnginePreLoading`,
// fades, camera keyframes, the loading-screen state machine and its PulseEvent, the Bink and
// pub-game state machines, the engine's rendered-time stamp, the present. REFII_GUEST_RENDER=1
// restores the guest's draw (a reference instrument, like the renderer build).
// ============================================================================================
namespace {
// The guest's draw is OFF: Godot renders. REFII_GUEST_RENDER=1, or the debug channel's
// `guestrender` op at runtime, runs it as a reference instrument: its constant setters then
// fill the device shadow the classic renderer is checked against.
bool GuestRenderWanted() {
  static const bool from_environment = [] {
    const char* value = std::getenv("REFII_GUEST_RENDER");
    return value != nullptr && std::string_view(value) == "1";
  }();
  // The debug channel's switch (src/core/guest_render_state.cpp): -1 follows the environment.
  const int32_t override_value = refii_bridge_guest_render_override();
  return override_value >= 0 ? override_value != 0 : from_environment;
}
}  // namespace

// ============================================================================================
// THE LIGHT VISIBILITY STAMP, STATED BY THE HOST. The light manager's update `sub_82240CE8`
// (0x821C3AF4, before the draw) keeps a non-directional light active only while
// `sub_82AB5AB8` passes: `(ctx+0xA0 - L+0x94) <u 2` (0x82AB5B34-48), ctx being the first
// render context of index 0 in `dword_834A57B0..B4` (`sub_821EFDB0`, 0 when none). L+0x94 is
// written only by the light's vtable +8 `sub_8221FAE0` (`L[37 + ctx->index] = ctx+0xA0`),
// which the draw's visibility walk (`sub_82192528` -> `sub_82ABD718`) calls on the primitives
// whose AABB (L+0x50 min, L+0x60 max; `sub_82A6DF48`) passes the view's planes at view+0x2F0
// (`sub_822362F8`: 16-byte planes, count at +0x100, outside when -w > dot3(p-vertex, n)).
// The planes are built by `sub_8228B4E0` at 0x821C3B08, before the draw. With the draw
// skipped no light was ever stamped (all stayed at the constructor's -100) and the manager
// held the sun alone: 182 torches and lamps in Westcliff at night, none active.
//
// The walk's answer, given here: the scene's lights (registry nodes with flag 1 at
// scene+3412+24..+28, objects at node+12..+16, as `sub_821FF800` iterates them) whose class
// stamps through `sub_8221FAE0` get the context's counter when their box met the planes of
// THIS OR THE PREVIOUS draw, and a stamp two passes old otherwise. The guest's counter
// advances once per stamping draw (`sub_821AE258`), so with several views a frame (the main
// view, reflections, a menu's preview) its test `< 2` passes a light any of the last two draws
// saw; a last-writer stamp would let a narrow view switch off a lamp the main view shows.
// Occlusion queries (the walk's vtable +0x48 when pass+0x709) have no answer without a GPU
// and are not applied.
// ============================================================================================
namespace {
// The draw each light was last seen in, by light address; draws counted here.
std::unordered_map<uint32_t, uint32_t> g_light_last_drawn;
uint32_t g_light_draws = 0;

void StateDrawnLights(uint8_t* base, uint32_t view) {
  ++g_light_draws;
  const auto load32 = [base](uint32_t at) {
    uint32_t raw;
    std::memcpy(&raw, base + at, sizeof(raw));
    return __builtin_bswap32(raw);
  };
  const auto loadf = [&load32](uint32_t at) {
    const uint32_t bits = load32(at);
    float value;
    std::memcpy(&value, &bits, sizeof(value));
    return value;
  };
  const auto store32 = [base](uint32_t at, uint32_t value) {
    const uint32_t raw = __builtin_bswap32(value);
    std::memcpy(base + at, &raw, sizeof(raw));
  };
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xE0000000u; };
  const uint32_t kStampLight = refii::guest::Address(0x8221FAE0u);

  const uint32_t scene = load32(view + 0x14u);
  if (!plausible(scene)) {
    return;
  }

  // `sub_821EFDB0`: the first registered context whose index is 0; its +0xA0 is the counter.
  uint32_t counter = 0;
  const uint32_t contexts = load32(refii::guest::Address(0x834A57B0u));
  const uint32_t contexts_end = load32(refii::guest::Address(0x834A57B4u));
  if (plausible(contexts) && contexts_end >= contexts && contexts_end - contexts <= 4u * 64u) {
    for (uint32_t at = contexts; at + 4u <= contexts_end; at += 4u) {
      const uint32_t context = load32(at);
      if (plausible(context) && load32(context) == 0u) {
        counter = load32(context + 0xA0u);
        break;
      }
    }
  }

  const uint32_t planes = view + 0x2F0u;
  const int32_t plane_count = static_cast<int32_t>(load32(planes + 0x100u));
  const auto box_meets_planes = [&](uint32_t light) {
    const float lo[3] = {loadf(light + 0x50u), loadf(light + 0x54u), loadf(light + 0x58u)};
    const float hi[3] = {loadf(light + 0x60u), loadf(light + 0x64u), loadf(light + 0x68u)};
    for (int32_t i = 0; i < plane_count && i < 16; ++i) {
      const uint32_t plane = planes + static_cast<uint32_t>(i) * 16u;
      float dot = 0.0f;
      for (uint32_t axis = 0; axis < 3u; ++axis) {
        const float n = loadf(plane + axis * 4u);
        dot += n * (n >= 0.0f ? hi[axis] : lo[axis]);
      }
      if (-loadf(plane + 12u) > dot) {
        return false;
      }
    }
    return true;
  };

  const uint32_t registry = scene + 3412u;
  const uint32_t nodes_begin = load32(registry + 24u);
  const uint32_t nodes_end = load32(registry + 28u);
  if (!plausible(nodes_begin) || nodes_end < nodes_begin || (nodes_end - nodes_begin) % 24u != 0u ||
      (nodes_end - nodes_begin) / 24u > 64u) {
    return;
  }
  for (uint32_t node = nodes_begin; node < nodes_end; node += 24u) {
    if ((load32(node + 4u) & 1u) == 0u) {
      continue;
    }
    const uint32_t objects = load32(node + 12u);
    const uint32_t objects_end = load32(node + 16u);
    if (!plausible(objects) || objects_end < objects || objects_end - objects > 4u * 4096u) {
      continue;
    }
    for (uint32_t at = objects; at < objects_end; at += 4u) {
      const uint32_t light = load32(at);
      if (!plausible(light)) {
        continue;
      }
      const uint32_t vtable = load32(light);
      if (!plausible(vtable) || load32(vtable + 8u) != kStampLight) {
        continue;
      }
      uint32_t& last = g_light_last_drawn[light];
      if (box_meets_planes(light)) {
        last = g_light_draws;
      }
      const bool recent = last != 0u && g_light_draws - last < 2u;
      store32(light + 0x94u, recent ? counter : counter - 2u);
    }
  }
}
}  // namespace

REX_EXTERN(rex_lhRenderer_DrawScene) {
  REX_FUNC_PROLOGUE();
  if (GuestRenderWanted()) {
    __imp__rex_lhRenderer_DrawScene(ctx, base);
    return;
  }

  StateDrawnLights(base, ctx.r3.u32);

  // THE TWO QUESTIONS THE DRAW USED TO ANSWER, ANSWERED BY THE HOST. Nothing in the guest
  // draws any more, so "are this view's textures resident?" has one honest answer: yes,
  // Godot has them. Two readers ask it: the pre-warm `sub_82A54500` (evaluates every layer
  // node's readiness and sets the view's warmed byte at +3692, else waits out a timeout at
  // +3704 — the level load sat 6 minutes on that) and the texture holders' boxes (`box+8`,
  // written by `sub_82A67188` from the resident mip; the front-end tick waits on two and
  // parks the title in state 9). Both readiness paths needed the draw to REQUEST the
  // textures first (`*(tex+132)` wanted mips), so calling them here produced nothing.
  //
  // Views: the table `dword_8349F79C..dword_8349F7A0` (128 slots), each V with +3692 the
  // warmed byte and the object container at V+3412: sorted 24-byte nodes from `*(list+4)`
  // to `*(list+8)`, node+0 the object tag, node+12..+16 the object pointer array
  // (`sub_82A8B998`, `sub_821D4F60`). Tag 46 = texture holder (`sub_82A41680`: +80 box).
  // Verified live 2026-09-04 against the front end (2218 holders) and the level view.
  const auto load32 = [base](uint32_t at) {
    uint32_t raw;
    std::memcpy(&raw, base + at, sizeof(raw));
    return __builtin_bswap32(raw);
  };
  const auto store32 = [base](uint32_t at, uint32_t value) {
    const uint32_t raw = __builtin_bswap32(value);
    std::memcpy(base + at, &raw, sizeof(raw));
  };
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xE0000000u; };

  static uint32_t frames = 0, warmed = 0, boxes = 0;
  static uint32_t refused_nodes = 0, refused_objects = 0;
  ++frames;

  // Span ceilings reject torn reads, not real containers: the walk runs while the guest may be
  // mid-write, so a begin/end pair can describe gigabytes. They are sized far above any real
  // list, and every rejection is counted and logged, because a skipped node leaves its texture
  // boxes unstated and the level load waits on residency.
  constexpr uint32_t kMaxNodes = 64u * 1024u;
  constexpr uint32_t kMaxObjects = 256u * 1024u;

  // THE COMPLETION DRAIN STAYS. Streamed-texture reads complete in `sub_82191F50` (each
  // pending entry's vtable +20 → `sub_82A7AF68`, `--dword_83496E78`), which the draw called
  // from its post pass and nothing else calls while a view is up. The level's declared
  // textures (the preload list `dword_8349FB5C`, processed by `sub_82181828`) are read
  // regardless of drawing and their completions gate `IsEnginePreLoading`: without this
  // call the fence monitor showed "streamed-texture residency BLOCKS the level load" for
  // five minutes (2026-09-04, build131).
  __imp__rex_lhStreamedTexture_PollPendingCompletions(ctx, base);

  const uint32_t table_begin = load32(refii::guest::Address(0x8349F79Cu));
  const uint32_t table_end = load32(refii::guest::Address(0x8349F7A0u));
  if (!plausible(table_begin) || table_end < table_begin || table_end - table_begin > 4u * 1024u) {
    return;
  }

  for (uint32_t slot = table_begin; slot + 4u <= table_end; slot += 4u) {
    const uint32_t view = load32(slot);
    if (!plausible(view)) {
      continue;
    }

    if (base[view + 3692u] == 0u) {
      base[view + 3692u] = 1u;
      ++warmed;
    }

    const uint32_t list = view + 3412u;
    const uint32_t nodes_begin = load32(list + 4u);
    const uint32_t nodes_end = load32(list + 8u);
    if (!plausible(nodes_begin) || nodes_end < nodes_begin ||
        (nodes_end - nodes_begin) % 24u != 0u) {
      continue;
    }
    if ((nodes_end - nodes_begin) / 24u > kMaxNodes) {
      if (++refused_nodes <= 8u || (refused_nodes & (refused_nodes - 1u)) == 0u) {
        REXLOG_WARN("Refii null-frame: view 0x{:08X} node list spans {} entries, over the {} "
                    "torn-read ceiling — SKIPPED, its texture boxes go unstated (#{})",
                    view, (nodes_end - nodes_begin) / 24u, kMaxNodes, refused_nodes);
      }
      continue;
    }

    for (uint32_t node = nodes_begin; node + 24u <= nodes_end; node += 24u) {
      if (load32(node) != 46u) {
        continue;
      }

      const uint32_t objects_begin = load32(node + 12u);
      const uint32_t objects_end = load32(node + 16u);
      if (!plausible(objects_begin) || objects_end < objects_begin) {
        continue;
      }
      if ((objects_end - objects_begin) / 4u > kMaxObjects) {
        if (++refused_objects <= 8u || (refused_objects & (refused_objects - 1u)) == 0u) {
          REXLOG_WARN("Refii null-frame: view 0x{:08X} holder array spans {} entries, over the {} "
                      "torn-read ceiling — SKIPPED, those textures stay unready (#{})",
                      view, (objects_end - objects_begin) / 4u, kMaxObjects, refused_objects);
        }
        continue;
      }

      for (uint32_t at = objects_begin; at + 4u <= objects_end; at += 4u) {
        const uint32_t holder = load32(at);
        if (!plausible(holder) || load32(holder + 4u) != 46u) {
          continue;
        }

        const uint32_t box = load32(holder + 80u);
        if (plausible(box) && load32(box + 8u) == 0u) {
          store32(box + 8u, 1u);
          ++boxes;
        }
      }
    }
  }

  if (frames % 300u == 1u) {
    REXLOG_INFO("Refii null-frame: frames={} views warmed={} boxes stated={} refused nodes={} "
                "objects={}", frames, warmed, boxes, refused_nodes, refused_objects);
  }
}

// ============================================================================================
// THE VIEW PRE-WARM, ANSWERED AT THE ASK. `sub_82A54500(view, now)` is the guest's "is this
// view's geometry resident?": while the view's warmed byte at +3692 is clear it walks every
// layer node under +3412 (`sub_82A547B0`, recursive) and loads each missing mesh LOD
// SYNCHRONOUSLY on the 3D Engine thread — `sub_82AA3DB8` -> `sub_8221CFE8` ->
// `sub_82AB49D0` -> `sub_82C75D30`, a `while (!GetStatus()) SleepEx(0, 1)` poll per load —
// and sets +3692 once every node is ready or `now - start` passes the timeout at +3704.
//
// Godot draws the geometry, so the answer is yes, and the sweep above already states it —
// one call too late: `sub_82181B68` runs this walk at 0x821C3AB0, before the draw at
// 0x821C3B38 in the same `sub_821C3858` frame, so a view created since the last frame gets
// // its whole first walk before the sweep sees it: seconds per new view, after every level
// // release and whenever a view is created in play. The scene ring's only consumer sits on
// // this thread, so the game thread parks in `sub_8222ABE0` for the duration and the world
// // freezes while Godot keeps drawing. Stating the byte here removes the walk entirely.
//
// The writes are the guest's own completion set at 0x82A54708-0x82A54724: `+3692 = 1`,
// `dbl_834BEC48 = 0`, and the inner render view's `+7232 = 250` when `+3676` names one.
// ============================================================================================
REX_EXTERN(rex_lhRenderView_UpdatePrewarm) {
  REX_FUNC_PROLOGUE();
  if (GuestRenderWanted()) {
    __imp__rex_lhRenderView_UpdatePrewarm(ctx, base);
    return;
  }

  const uint32_t view = ctx.r3.u32;
  if (view < 0x10000u || view >= 0xE0000000u) {
    return;
  }
  if (base[view + 3692u] != 0u) {
    return;
  }

  const auto load32 = [base](uint32_t at) {
    uint32_t raw;
    std::memcpy(&raw, base + at, sizeof(raw));
    return __builtin_bswap32(raw);
  };
  const auto store32 = [base](uint32_t at, uint32_t value) {
    const uint32_t raw = __builtin_bswap32(value);
    std::memcpy(base + at, &raw, sizeof(raw));
  };

  base[view + 3692u] = 1u;
  std::memset(base + refii::guest::Address(0x834BEC48u), 0, sizeof(double));
  const uint32_t inner = load32(view + 3676u);
  if (inner >= 0x10000u && inner < 0xE0000000u) {
    store32(inner + 7232u, 250u);
  }

  static uint32_t stated = 0;
  ++stated;
  if (stated <= 8u || (stated & (stated - 1u)) == 0u) {
    REXLOG_INFO("Refii view pre-warm stated for view 0x{:08X} (#{}) — the guest's synchronous "
                "LOD walk did not run", view, stated);
  }
}

// ============================================================================================
// THE FRONT END'S AUDIO PRELOAD, QUEUED AT ONCE. `rex_lhFrontEndState_LoadAudioBanks` (TU1
// 0x823191F8, unpatched 0x82354A70) is the body of the "Front end audio loading" thread the
// front end starts at init (0x82319268 via 0x823192E0). For every bank `audio.lua` registered
// with its preload flag (+12) and no handle yet (+8) it calls `lhGameData_LoadNamedBankEntry`,
// then `SleepEx(400, 1)` (0x82319244-0x8231924C).
//
// The load only queues the bank: `lhGameData_LoadNamedBankEntry` passes 1 as the last argument
// (`li r6, 1` at 0x822F4830) down to `sub_82C04280`, which waits on its sync event only when it
// is 0. So the sleeps are pacing for the 360's disc and nothing else. Leaving the front end
// polls `Sleep(100)` until this thread sets +192 (0x8231A1B0-0x8231A1FC), so a save picked
// before the last sleep had run sat on the save cards for the rest of them: 4.4 s measured.
//
// The walk is the guest's, the same entries in the same order; only the sleeps are gone.
// ============================================================================================
REX_EXTERN(rex_lhFrontEndState_LoadAudioBanks) {
  REX_FUNC_PROLOGUE();
  const auto load32 = [base](uint32_t at) {
    uint32_t raw;
    std::memcpy(&raw, base + at, sizeof(raw));
    return __builtin_bswap32(raw);
  };

  const uint32_t state = ctx.r3.u32;
  const uint32_t list_begin = refii::guest::Address(0x834975CCu);
  const uint32_t list_end = refii::guest::Address(0x834975D0u);

  const PPCContext saved = ctx;
  uint32_t queued = 0;
  for (uint32_t entry = load32(list_begin); entry != load32(list_end); entry += 16u) {
    if (base[entry + 12u] == 0u || load32(entry + 8u) != 0u) {
      continue;
    }
    ctx = saved;
    ctx.r3.u32 = load32(state + 136u);
    ctx.r4.u32 = entry;
    rex_lhGameData_LoadNamedBankEntry(ctx, base);
    ++queued;
  }
  ctx = saved;

  REXLOG_INFO("Refii front-end audio: {} preload bank(s) queued at once (the guest paced them "
              "400 ms apart)", queued);
}
