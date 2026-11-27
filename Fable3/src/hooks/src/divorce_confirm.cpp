// refii - the divorce's confirmation: the divorce runs on its own box's answer, never on another
// box's. TU3 addresses first, disc second.
//
// THE GUEST'S MECHANISM. The divorce box's accept sub_829802A8(D) / sub_82929628 (one caller each, the
// box's input at 0x8297F624 / 0x829289D4) sets `D+177 = D+176 = 1` (0x829802BC / 0x829802C4) and, for
// the asking modes 0 and 3 only (`lwz r11,0xAC(r3)` 0x829802C8..0x829802D8), builds a CQueryBox —
// vtable off_820B9A78 (`addi r9,r10,off_820B9A78@l` 0x829803AC), base ctor sub_82958000 with the static
// name dword_8359CFD8 (0x82980398), `+28 = D+120` the box's pad (`stw r29,0x1C(r31)` 0x829803A4), `+40 =
// +41 = 0` (0x829803CC / 0x829803D4), text GUI_DIVORCE_CONFIRM (mode 0) or GUI_PARTNERSHIP_SPLIT_CONFIRM
// (0x82980310 / 0x8298031C) — pushes it on Q0 (sub_82623BF0 0x829803E8 / disc sub_825FBBF8 0x82929760),
// and arms X = *(GUI+0x48) with sub_825F1708(X, entity, D+80, reason D+136, fee D+124, mode == 3)
// (0x8298044C / disc sub_825C6660 0x829297C4): a pending record at X+100 (disc X+120) and the stamp
// X+108 (disc X+128) = the message serial less one. The box is not kept.
// The poll sub_82297B40(X) / sub_82299878 (one caller each, the GUI's update at 0x822550BC /
// 0x82255E8C with `lwz r3,0x48(r31)`) returns unless X+100 (`lwz r11,0x64(r3)` 0x82297B4C / `0x78`
// 0x82299884); then takes the NEWEST event-68 record — MS = *(*(*(*(GAME+0xC)+0x1C)+4)+0x1C), its count
// MS+0x338 and its list MS+0x334, the same on both builds (0x82297B78..0x82297B8C / 0x822998B0..
// 0x822998C4) — and when its serial +0x28 is past the stamp (0x82297B90..0x82297B98) moves the stamp to it
// (0x82297B9C) and reads its value +0x24 (0x82297BA4): 1 runs the divorce — sub_825F1868(X, save) and the
// component-83 tail sub_824E25B8 (the joiner's packet or the AUTOSAVE sub_822F8940(SAVE, 0, 0, 1, 1, 0)) /
// disc sub_825C67C0(X) — and any other value only drops it; either way the pending record is released
// (sub_825FFF80 / sub_82D64B30) and X+100 cleared. So ANY event 68 posted after the accept — a yes/no
// queued ahead of the confirmation, a quest's question, a natal query — answers the divorce, and its
// "yes" divorces and saves.
//
// THE GUARD, AT THE STATEMENTS: the accept is running (this wrapper) while a box is pushed
// (rex_BoxQueue_Push): that box is the confirmation; its own handler took Godot's answer and posted its
// event 68 (display_box_hooks.cpp's yes/no answer: the handler's answer 1, the message serial moved; the
// record's own serial S is the counter BEFORE PostEvent's increment — `lwz r11,0x9B0(r30)` 0x821CFB44,
// the counter + 1 stored back at 0x821CFB58, the old value into the record at 0x821CFB5C (`stw r11,
// var_70`, the node's +0x28; the answer `stw r27, var_74`, +0x24) / disc `0x980`);
// the box left its queue. The poll runs AS THE GUEST HAS IT — its yes path differs by build (two
// arguments and a component-83 save on TU3, one on the disc) and releases the guest's own record — but
// only on a GUI tick whose newest event-68 record is the confirmation's own answer (serial S). Any other
// newer record is held: the poll does not run, the stamp stays, nothing divorces. A confirmation
// overtaken by another event 68 before the poll ran (S older than the newest) runs nothing either; the
// pending record waits for the next accept, which arms it again.

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <initializer_list>
#include <mutex>
#include <vector>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "box_answers.h"
#include "gui_statements.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_DivorceBox_Accept);
REX_EXTERN(__imp__rex_DivorceConfirm_Poll);

namespace {

using namespace refii::fable3;

const refii::guest::Addr kGame{0x83597034u};
/// The message system's event-68 list and its count (the poll's `lwz r11,0x334(r11)` / `lwz
/// r8,0x338(r11)`), and a record's serial and value (+0x28 / +0x24).
constexpr uint32_t kEvent68List = 0x334u;
constexpr uint32_t kEvent68Count = 0x338u;
constexpr uint32_t kRecordSerial = 0x28u;

uint32_t PendingOffset() { return refii::guest::Pick(0x64u, 0x78u); }
uint32_t StampOffset() { return refii::guest::Pick(0x6Cu, 0x80u); }

std::mutex g_mutex;
std::vector<uint32_t>* g_confirms = nullptr;  // pushed by the accept and still in a queue
bool g_answered = false;                      // a confirmation's answer posted, not yet polled
uint32_t g_answer_serial = 0u;
bool g_answer_yes = false;
uint32_t g_held_serial = 0u;                  // the last foreign record this guard held (logged once)

thread_local bool t_accepting = false;

std::vector<uint32_t>& Confirms() {
  if (g_confirms == nullptr) {
    g_confirms = new std::vector<uint32_t>();
  }
  return *g_confirms;
}

bool Erase(uint32_t box) {
  std::vector<uint32_t>& confirms = Confirms();
  for (size_t i = 0; i < confirms.size(); ++i) {
    if (confirms[i] == box) {
      confirms.erase(confirms.begin() + static_cast<std::ptrdiff_t>(i));
      return true;
    }
  }
  return false;
}

/// MS = *(*(*(*(GAME+0xC)+0x1C)+4)+0x1C), the poll's own chain (0x82297B60..0x82297B74), or 0.
uint32_t MessageSystem(const uint8_t* base) {
  uint32_t at = LoadGuestU32(base, kGame);
  for (uint32_t offset : {0xCu, 0x1Cu, 4u, 0x1Cu}) {
    if (!Plausible(at)) {
      return 0u;
    }
    at = LoadGuestU32(base, at + offset);
  }
  return Plausible(at) ? at : 0u;
}

}  // namespace

namespace refii::fable3 {

bool DivorceAccepting() { return t_accepting; }

void NoteDivorceConfirmQueued(uint32_t box) {
  std::lock_guard<std::mutex> lock(g_mutex);
  Confirms().push_back(box);
  REXLOG_INFO("[fable3-divorce] confirmation {:08X} queued by the divorce box's accept", box);
}

void NoteDivorceConfirmAnswered(uint32_t box, bool yes, bool posted, uint32_t serial) {
  std::lock_guard<std::mutex> lock(g_mutex);
  if (!Erase(box)) {
    return;
  }
  if (!posted) {
    REXLOG_INFO("[fable3-divorce] confirmation {:08X} answered with no event 68 posted: nothing runs", box);
    return;
  }
  g_answered = true;
  g_answer_serial = serial;
  g_answer_yes = yes;
  REXLOG_INFO("[fable3-divorce] confirmation {:08X} answered {} (event 68 serial {}): the next poll takes it", box,
              yes ? "yes" : "no", serial);
}

void NoteDivorceConfirmGone(uint32_t box) {
  std::lock_guard<std::mutex> lock(g_mutex);
  if (Erase(box)) {
    REXLOG_INFO("[fable3-divorce] confirmation {:08X} left its queue unanswered", box);
  }
}

void ForgetDivorceConfirms() {
  std::lock_guard<std::mutex> lock(g_mutex);
  Confirms().clear();
  g_answered = false;
  g_held_serial = 0u;
}

}  // namespace refii::fable3

/// (D): every box the accept pushes is the divorce's confirmation.
REX_EXTERN(rex_DivorceBox_Accept) {
  static std::atomic<bool> said{false};
  if (!said.exchange(true)) {
    REXLOG_INFO("[fable3-divorce] the divorce box's accept is wrapped (its confirmation is known by identity)");
  }
  t_accepting = true;
  __imp__rex_DivorceBox_Accept(ctx, base);
  t_accepting = false;
}

/// (X), once a GUI tick: the guest's poll, run only when the newest event-68 record is the
/// confirmation's own answer.
REX_EXTERN(rex_DivorceConfirm_Poll) {
  static std::atomic<bool> said{false};
  if (!said.exchange(true)) {
    REXLOG_INFO("[fable3-divorce] the divorce poll is guarded (it takes only its confirmation's answer)");
  }
  const uint32_t pending_owner = ctx.r3.u32;
  if (base == nullptr || !Plausible(pending_owner) || LoadGuestU32(base, pending_owner + PendingOffset()) == 0u) {
    return;  // the guest's own first test: nothing armed, nothing done (0x82297B4C..0x82297B5C)
  }
  const uint32_t ms = MessageSystem(base);
  if (ms == 0u || LoadGuestU32(base, ms + kEvent68Count) == 0u) {
    return;  // (0x82297B78..0x82297B80)
  }
  const uint32_t newest = LoadGuestU32(base, LoadGuestU32(base, ms + kEvent68List));
  if (!Plausible(newest)) {
    return;
  }
  const int32_t serial = static_cast<int32_t>(LoadGuestU32(base, newest + kRecordSerial));
  const int32_t stamp = static_cast<int32_t>(LoadGuestU32(base, pending_owner + StampOffset()));
  if (serial <= stamp) {
    return;  // not past the stamp: the guest returns (`ble` 0x82297B98)
  }
  bool run = false;
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_answered && static_cast<uint32_t>(serial) == g_answer_serial) {
      g_answered = false;
      run = true;
      REXLOG_INFO("[fable3-divorce] the poll takes its confirmation's answer ({}, serial {}): {}",
                  g_answer_yes ? "yes" : "no", serial, g_answer_yes ? "the divorce runs" : "it is dropped");
    } else {
      if (g_answered && static_cast<int32_t>(g_answer_serial) < serial) {
        g_answered = false;
        REXLOG_WARN("[fable3-divorce] the confirmation's answer (serial {}) was overtaken by event 68 serial {} "
                    "before the poll ran: nothing runs",
                    g_answer_serial, serial);
      }
      if (g_held_serial != static_cast<uint32_t>(serial)) {
        g_held_serial = static_cast<uint32_t>(serial);
        REXLOG_INFO("[fable3-divorce] event 68 serial {} is not the divorce confirmation's answer: held", serial);
      }
    }
  }
  if (run) {
    __imp__rex_DivorceConfirm_Poll(ctx, base);
  }
}
