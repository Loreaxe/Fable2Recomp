// XSessionLeaveRemote / XSessionLeaveLocal: a member leaves a session the title holds.
//
// THE CONTRACT. Both are the title's own XAM stubs (TU1 0x82D00E90 / 0x82D00DE8, disc 0x82CF93D8 /
// 0x82CF9330): `XamSessionRefObjByHandle`, then `XMsgStartIORequest(0xFB, 0x000B0013, overlapped,
// {session, count, xuids | user indices, 0, 0}, 0x14)`. With no overlapped they answer 0 when the
// request was taken and ERROR_FUNCTION_FAILED (0x65B) when XMsgStartIORequest refused it
// (TU1 0x82D00EF0 `cmpwi r3,0` / `bge` / `li r31,0x65B`). On a console the request is the session's
// own bookkeeping: it needs no word from the player who left and succeeds for a session the title
// holds, whether that player's console is still reachable or not.
//
// This kernel's XGI app carries no case for message 0x000B0013 and refuses it, and the kernel is
// not ours to change. So the refusal is answered here as the console answers the call: success.
// Everything else the stub returns (a bad handle's error, an overlapped request's pending) stands.
//
// WHY THE GAME NEEDS IT. The co-op session manager gives a slot back only when the call answers 0:
// `CXboxLiveManager::LeaveSessionRemote` (TU1 `sub_823575F0`: `bl XSessionLeaveRemote` 0x82357664,
// `cmplwi r3,0` / `beq` 0x8235766C, then `FILLED -= players`; disc `sub_823349D0`, 0x82334A44 /
// 0x82334A4C), and the member-update handler's local branch the same for XSessionLeaveLocal (TU1
// 0x822EF4D0, disc 0x822EF6A0). With the failure the slot stayed filled, and the next join request
// was answered JOINRESPONSE_SESSIONFULL until the host left the game.
//
// THE PEER IS GONE. Both run only from the member-update handler's erase branch (TU1
// `sub_822EF238`: XSessionLeaveLocal 0x822EF4D0 for the local record `+0x200`, else
// `sub_823575F0` 0x822EF530 → XSessionLeaveRemote 0x82357664; disc `sub_822EF408` 0x822EF6A0 /
// 0x822EF700 → 0x82334A44), so a leave that succeeds ends the co-op session's other machine.
// Refii's co-op channel forgets the address it learned: kept, the host restated to a joiner that
// had quit, and a joiner restarted under that stream was held out of the shard (RexNet counts
// title traffic as a game in progress) and found no session to join.

#include <cstdint>

#include <rex/logging.h>
#include <rex/ppc/context.h>
#include <rex/ppc/func.h>

#include "core/payload_bridge.h"

extern "C" REX_FUNC(__imp__rex_XSessionLeaveRemote);
extern "C" REX_FUNC(__imp__rex_XSessionLeaveLocal);

namespace {

constexpr uint32_t kErrorFunctionFailed = 0x65Bu;

/// Runs the title's stub; a synchronous request the XGI app refused answers success. A leave that
/// succeeded ends the co-op channel's peer.
void LeaveSession(PPCContext& ctx, uint8_t* base, PPCFunc* stub, const char* call) {
  const uint32_t players = ctx.r4.u32;
  const bool synchronous = ctx.r6.u32 == 0u;
  stub(ctx, base);
  if (synchronous && ctx.r3.u32 == kErrorFunctionFailed) {
    REXLOG_INFO("{}: {} player(s) left the session (the XGI request was refused; answered as a "
                "console answers it)", call, players);
    ctx.r3.u64 = 0u;
  }
  if (synchronous && ctx.r3.u32 == 0u) {
    refii_bridge_coop_channel_forget();
  }
}

}  // namespace

REX_EXTERN(rex_XSessionLeaveRemote) {
  LeaveSession(ctx, base, &__imp__rex_XSessionLeaveRemote, "XSessionLeaveRemote");
}

REX_EXTERN(rex_XSessionLeaveLocal) {
  LeaveSession(ctx, base, &__imp__rex_XSessionLeaveLocal, "XSessionLeaveLocal");
}
