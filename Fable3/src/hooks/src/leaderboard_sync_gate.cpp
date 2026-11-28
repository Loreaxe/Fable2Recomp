// refii - FABLE 3'S LEADERBOARD STATISTICS STAY THE SAVE'S: there is no Live leaderboard service.
//
// THE CONTRACT. Each hero's statistics component (CECStats, component 15; base = the component)
// keeps every leaderboard statistic in three maps, all three saved to leaderboardstatssave.bin by
// sub_823E2AF8 / disc sub_823C1688 (+0x570 AtSync, +0x560 Transmitting, +0x550 ChangeRequested)
// and summed by the getter sub_823E9310 / disc sub_823C7D20 (`addi r4,r3,0x550` lookup, then
// sub_823E93A8 / sub_823C7DB8 over `addi r5,r31,0x560` and `addi r5,r31,0x570`). AtSync is the
// component's copy of what the Live leaderboard holds, kept by a per-hero state at +0x628 that
// CECStats_vf18 (TU3 0x82241F90 / disc 0x82242EC8) steps each tick in sub_82283D70 / sub_82285B38:
//   - `bl sub_8227D400` 0x82283E38 / `bl sub_8227F0C0` 0x82285C00 asks whether the hero's user is
//     ready for the leaderboard (this function). Not ready, state != 3: `li r11,2; stw
//     r11,0x628(r31)` 0x82283E54 / 0x82285C1C. Ready while 2: `stw r30,0x628(r31)` (0) 0x82283E78 /
//     0x82285C40. CECStats_vf10 starts every hero at 2 (`stw r11,0x628(r31)` 0x823DE374 /
//     0x823BD130), so a load runs the next step.
//   - State 0, the reader idle (*(*0x835979F8 +0x18) +0xC == 0) with the hero's XUID among its
//     requested players: sub_823E3868 / disc sub_823C2270 CLEARS AtSync (`addi r31,r3,0x570`
//     0x823E3878 / 0x823C2280, `bl sub_82D41718` 0x823E3890 / `bl sub_82D00FE0` 0x823C2298) and
//     refills it from the reader alone (`bl sub_82391788` 0x823E38E0 / `bl sub_82385260`
//     0x823C22E8); then `li r11,1; stw r11,0x628(r31)` 0x82283F54 / 0x82285D1C.
//   - State 1: ChangeRequested moves to Transmitting (sub_822679C0 `lwz r11,0x628(r3); cmpwi
//     r11,1` 0x822679CC / disc inline in vf18 0x82243794) and XSessionWriteStats /
//     XSessionFlushStats send it (sub_82366AC0, sub_82366780 / disc sub_82360870, sub_82360530). A
//     flush whose overlapped completes 0 marks it 6 (`li r5,6` 0x823665B4 / 0x82360368); the result
//     pass sub_82253198 / disc sub_82253FA0 merges every 6 into AtSync (`cmpwi r27,6` 0x822532C4,
//     `addi r6,r30,0x570` 0x822532F4 / 0x822540B8) and raises the save manager's leaderboard-save
//     flag +0x1E6 / +0x1E7 (0x82253434 / 0x822541F8), on which sub_822F8ED0 / disc sub_822F5578
//     rewrites leaderboardstatssave.bin and checksumsave.bin into the hero's save, no save asked.
//
// WHY IT IS ANSWERED HERE. The loop is coherent only against a service that keeps what was
// flushed: AtSync is rebuilt from the read at every state 0, so at every load. Refii has no Live
// leaderboard: XSessionWriteStats / XSessionFlushStats are acknowledged and dropped, and
// XUserReadStats (0xB0021, sub_83131890 / disc sub_830FFB18) succeeds without writing a row into
// the guest's zero-filled results (sub_821787E0 / sub_821788A0 resize them with 0, `stb r25`
// 0x82178A34 after `li r25,0` 0x821788C0), so every load cleared AtSync. Measured on the P2
// package: Hero003 with Sword Hits 14 and Fireball 105 in ChangeRequested was moved into AtSync by
// a solo session (file rewritten 13:37:21, no save asked) and its next solo load (13:45) wrote it
// back without either; the co-op save of 12:02 lost them the same way on its 13:02 load.
//
// THE ANSWER: not ready, for every hero (player one, player two, the joiner's hero) — what a
// console whose players cannot reach Live leaderboards answers. The state stays 2 (or 3): AtSync
// is never cleared, ChangeRequested is never sent and keeps every change, a save's leftover
// Transmitting entries find no session record (status 7, `cmpwi r27,7` 0x822532B0 / 0x82254074)
// and return to ChangeRequested, and no flush raises the rewrite flag. The save format is
// untouched: ChangeRequested alone is what every save held before a flush ever succeeded.
//
// The caller uses the byte answer only: its out slot r1+0x50 is set to -1 and handed in
// (0x82283E28..0x82283E30 / 0x82285BF0..0x82285BF8) and never read again. The function has no
// other caller on either build; the statistics screen's online test (sub_8227D110) and the
// reader's (sub_82390680) are separate functions and keep their own answers.

#include <rex/logging.h>
#include <rex/ppc.h>

// bool StatsSync_UserReady(CECStats* stats, int* userOut)
REX_EXTERN(rex_StatsSync_UserReady) {
  static const bool once = [] {
    REXLOG_INFO(
        "Refii stats: no Live leaderboard service; each hero's leaderboard statistics stay in its "
        "save (sync gate sub_8227D400 answered not ready)");
    return true;
  }();
  (void)once;
  ctx.r3.u64 = 0;
}
