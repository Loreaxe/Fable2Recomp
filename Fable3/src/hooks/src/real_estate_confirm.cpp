// refii - the real-estate mode's confirmations: its two answer polls run only on the answer of a
// box the mode itself asked with.
//
// THE GUEST'S MECHANISM (CInteractionModeRealEstate, vtable TU3 0x820D54C8 / disc 0x820D2858; mode
// fields TU3 / disc: waiting +0x68 / +0xB0, last serial +0x74 / +0xBC, pending id +0x7C / +0xC4).
// A row's ask — the row handler lhGuiRealEstateScreen_OnMenuEvent(mode, row, player) TU3 0x82B59320 /
// disc 0x82B1CFC0 and the nine ask bodies only it calls — pushes a yes/no box or a numeric adjuster
// and stores the pending id and waiting = 1 (TU3 0x82B594FC / 0x82B59504, 0x82B59640 / 0x82B59648,
// 0x82B59A58, 0x82B59B54, 0x82B59C50, 0x82B5A35C, 0x82B5AA24, 0x82B5AD00, 0x82B5B15C, 0x82B5DDE0,
// 0x82B5E130; disc 0x82B1D194 / 0x82B1D19C, 0x82B1D2D4 / 0x82B1D2DC, 0x82B1D6F0, 0x82B1D7EC,
// 0x82B1D8E8, 0x82B1DEF4, 0x82B1E4EC, 0x82B1E9B0, 0x82B1EDAC, 0x82B219C0, 0x82B21D08). No ask
// stamps the serial: +0x74 / +0xBC is written by the constructor alone (`lwz r11,0x9B0(r4)` /
// `addi r11,r11,-1` / `stw r11,0x74(r31)`, TU3 0x82B55544..0x82B55558; disc 0x82B192A0) and by the
// polls. The mode's update runs both polls while waiting (`lbz r11,0x68(r30)` / `bl sub_82B5C270` /
// `bl sub_82B5C818`, TU3 0x82B5C0D4..0x82B5C0EC; disc `lbz r11,0xB0(r31)` 0x82B1FC60, `bl` 0x82B1FC70 /
// 0x82B1FC78; the polls' only callers).
//   - The yes/no poll sub_82B5C270(mode) / disc sub_82B1FCA8 takes the NEWEST event-68 record of the
//     message system (count MS+0x338, list MS+0x334, record serial +0x28, answer +0x24: TU3
//     0x82B5C298..0x82B5C2B4, disc 0x82B1FCD0..0x82B1FCEC), and when its serial is past the mode's
//     (`cmpw r9,r10` / `ble`, 0x82B5C2B8 / 0x82B1FCF0) clears waiting, takes the serial and, on a
//     non-zero answer, switches on the pending id: sell, buy, rent out, evict, repair, divorce, fire
//     the nanny.
//   - The adjuster poll sub_82B5C818(mode) / disc sub_82B20240 does the same with the newest event-69
//     record (count MS+0x344, list MS+0x340, serial +0x28, value +0x24: 0x82B5C848..0x82B5C860 /
//     0x82B20270..0x82B20288) for pending 6 (budget), 7 and 8 (rent, profits).
// Neither poll knows which box the record answered, and nothing clears the pending id. So any yes
// answered since the mode was built — a question queued ahead of the confirmation, or one answered
// before the row was even asked, whose record waited unread while the mode was not waiting — buys,
// sells or divorces the tick after the ask.
//
// THE GATE. Three of the guest's own calls say whose answer a record is:
//   - the row handler (or the yes/no poll, whose nanny case asks again: `bl sub_82B5DFD0` TU3
//     0x82B5C690, disc `bl sub_82B21BB0` 0x82B200C0) is running while a box is pushed
//     (rex_BoxQueue_Push): that box is this mode's confirmation;
//   - that box's own handler took Godot's answer and its event was posted (AnswerBoxAction: the
//     message serial before and after the handler; a record's serial is the counter before its
//     post, `lwz r11,0x9B0(r30)` / `stw r11,…` TU3 0x821CFB44..0x821CFB5C, disc +0x980 0x821CFA74);
//   - a box left its queue: no longer a confirmation.
// Each poll's original runs only on a tick with such an answer waiting for its mode, and only when
// the newest record of its event carries a serial the confirmation's own handler posted (PostEvent
// sub_821CFA18 inserts each record at the head of its event's list, `bl sub_8219ED10` with the
// list's first node TU3 0x821CFC40..0x821CFC48, disc `bl sub_8219F270` 0x821CFB70..0x821CFB78, and
// the polls read that first node: the record checked is the one the poll is about to act on, read
// for that precondition alone). On every other tick the poll returns with nothing read and nothing
// written: the mode stays waiting, as the guest leaves it when an adjuster is cancelled. A new ask
// supersedes the mode's older confirmations. The switch, its ability-feature and co-op tests, its
// statistics and its close are the guest's, each build's own.

#include <cstdint>
#include <mutex>
#include <vector>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "box_answers.h"
#include "gui_statements.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_RealEstate_OnMenuEvent);
REX_EXTERN(__imp__rex_RealEstate_Poll);
REX_EXTERN(__imp__rex_RealEstate_AdjusterPoll);

namespace {

using namespace refii::fable3;

const refii::guest::Addr kGame{0x83597034u};

/// The yes/no answer's event and the adjuster's accept (the per-event vectors are 12 bytes at
/// MS+4+12n: +0x334 and +0x340).
constexpr uint32_t kEventQuestion = 68u;
constexpr uint32_t kEventAdjuster = 69u;
/// A record's serial, from the record the list's first entry points at (`lwz r11,0(r11)` /
/// `addi r11,r11,8` / `lwz r9,0x20(r11)` TU3 0x82B5C2AC..0x82B5C2B4; `lwz r9,0(r11)` /
/// `lwz r11,0x28(r9)` 0x82B5C85C..0x82B5C860).
constexpr uint32_t kRecordSerial = 0x28u;

/// A box a mode asked with, still in a queue.
struct Confirm {
  uint32_t box = 0u;
  uint32_t mode = 0u;
  int32_t row = 0;
};

/// A confirmation's answer whose event was posted, waiting for its mode's poll: the serials its
/// handler posted are [first, end).
struct Answer {
  uint32_t mode = 0u;
  uint32_t event = 0u;
  uint32_t box = 0u;
  int32_t row = 0;
  uint32_t first = 0u;
  uint32_t end = 0u;
};

std::mutex g_mutex;
std::vector<Confirm>* g_confirms = nullptr;
std::vector<Answer>* g_answers = nullptr;

/// The mode whose row handler or yes/no poll is running on this thread, and the row it was handed.
thread_local uint32_t t_asking_mode = 0u;
thread_local int32_t t_asking_row = 0;

std::vector<Confirm>& Confirms() {
  if (g_confirms == nullptr) {
    g_confirms = new std::vector<Confirm>();
  }
  return *g_confirms;
}

std::vector<Answer>& Answers() {
  if (g_answers == nullptr) {
    g_answers = new std::vector<Answer>();
  }
  return *g_answers;
}

/// MS = *(*(*(*(GAME+12)+28)+4)+28), the chain both polls read (TU3 0x82B5C284..0x82B5C294).
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

/// The serial of the newest record of `event`, as the event's poll finds it; false with none.
bool NewestSerial(const uint8_t* base, uint32_t event, uint32_t& serial) {
  const uint32_t system = MessageSystem(base);
  if (system == 0u) {
    return false;
  }
  const uint32_t vector = system + 4u + 12u * event;
  const uint32_t list = LoadGuestU32(base, vector);
  if (LoadGuestU32(base, vector + 4u) == 0u || !Plausible(list)) {
    return false;
  }
  const uint32_t record = LoadGuestU32(base, list);
  if (!Plausible(record)) {
    return false;
  }
  serial = LoadGuestU32(base, record + kRecordSerial);
  return true;
}

/// Takes the answer waiting for `mode`'s poll of `event`, when the newest record of that event is
/// one the confirmation's handler posted. A newer record of another box drops the answer: its value
/// is not this confirmation's, and the poll must not act on it.
bool TakeAnswer(const uint8_t* base, uint32_t mode, uint32_t event, Answer& out) {
  std::lock_guard<std::mutex> lock(g_mutex);
  std::vector<Answer>& answers = Answers();
  for (size_t i = 0; i < answers.size(); ++i) {
    if (answers[i].mode != mode || answers[i].event != event) {
      continue;
    }
    out = answers[i];
    answers.erase(answers.begin() + static_cast<std::ptrdiff_t>(i));
    uint32_t serial = 0u;
    if (!NewestSerial(base, event, serial)) {
      REXLOG_WARN("[fable3-realestate] row {}: box {:08X} answered but event {} has no record: nothing runs", out.row,
                  out.box, event);
      return false;
    }
    if (serial < out.first || serial >= out.end) {
      REXLOG_WARN("[fable3-realestate] row {}: the newest event {} (serial {}) is not box {:08X}'s answer "
                  "(serials {}..{}): nothing runs",
                  out.row, event, serial, out.box, out.first, out.end);
      return false;
    }
    return true;
  }
  return false;
}

void LogGateOnce(const char* poll) {
  REXLOG_INFO("[fable3-realestate] the {} poll is gated on the mode's own confirmation", poll);
}

}  // namespace

namespace refii::fable3 {

bool RealEstateAsking() { return t_asking_mode != 0u; }

void NoteRealEstateConfirmQueued(uint32_t box) {
  std::lock_guard<std::mutex> lock(g_mutex);
  Confirms().push_back(Confirm{box, t_asking_mode, t_asking_row});
  REXLOG_INFO("[fable3-realestate] box {:08X} queued by mode {:08X}'s row {}: its confirmation", box, t_asking_mode,
              t_asking_row);
}

void NoteRealEstateAnswered(uint32_t box, bool adjuster, uint32_t serial_before, uint32_t serial_after) {
  std::lock_guard<std::mutex> lock(g_mutex);
  for (const Confirm& confirm : Confirms()) {
    if (confirm.box != box) {
      continue;
    }
    if (serial_after == serial_before) {
      REXLOG_INFO("[fable3-realestate] confirmation {:08X} of row {} answered with no event posted: nothing runs", box,
                  confirm.row);
      return;
    }
    Answers().push_back(Answer{confirm.mode, adjuster ? kEventAdjuster : kEventQuestion, box, confirm.row,
                               serial_before, serial_after});
    REXLOG_INFO("[fable3-realestate] confirmation {:08X} of row {} answered (serials {}..{}): mode {:08X}'s next "
                "poll takes it",
                box, confirm.row, serial_before, serial_after, confirm.mode);
    return;
  }
}

void NoteRealEstateBoxGone(uint32_t box) {
  std::lock_guard<std::mutex> lock(g_mutex);
  std::vector<Confirm>& confirms = Confirms();
  for (size_t i = 0; i < confirms.size(); ++i) {
    if (confirms[i].box == box) {
      confirms.erase(confirms.begin() + static_cast<std::ptrdiff_t>(i));
      return;
    }
  }
}

void ForgetRealEstateConfirms() {
  std::lock_guard<std::mutex> lock(g_mutex);
  Confirms().clear();
  Answers().clear();
}

}  // namespace refii::fable3

/// (mode, row, player): every box the row's ask pushes is this mode's confirmation. A new ask
/// stores a new pending id (the asks' stores above), so it supersedes the mode's older
/// confirmations and any answer its mode has not polled yet: an older box answered later would
/// otherwise run the NEW pending id.
REX_EXTERN(rex_RealEstate_OnMenuEvent) {
  static bool logged = false;
  if (!logged) {
    logged = true;
    REXLOG_INFO("[fable3-realestate] the row handler names its confirmations");
  }
  const uint32_t mode = ctx.r3.u32;
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    std::vector<Answer>& answers = Answers();
    for (size_t i = answers.size(); i-- > 0;) {
      if (answers[i].mode == mode) {
        answers.erase(answers.begin() + static_cast<std::ptrdiff_t>(i));
      }
    }
    std::vector<Confirm>& confirms = Confirms();
    for (size_t i = confirms.size(); i-- > 0;) {
      if (confirms[i].mode == mode) {
        REXLOG_INFO("[fable3-realestate] row {}'s confirmation {:08X} superseded by a new ask", confirms[i].row,
                    confirms[i].box);
        confirms.erase(confirms.begin() + static_cast<std::ptrdiff_t>(i));
      }
    }
  }
  const uint32_t outer_mode = t_asking_mode;
  const int32_t outer_row = t_asking_row;
  t_asking_mode = mode;
  t_asking_row = ctx.r4.s32;
  __imp__rex_RealEstate_OnMenuEvent(ctx, base);
  t_asking_mode = outer_mode;
  t_asking_row = outer_row;
}

/// (mode), each tick the mode waits: the guest's yes/no poll, run only for the answer of a
/// confirmation this mode asked with. Wrapped, not replaced: the switch on the pending id differs by
/// build (TU3's Buy answers whether it bought, 0x82B5B4D8; the disc's does not, 0x82B1F128) and is
/// the guest's own.
REX_EXTERN(rex_RealEstate_Poll) {
  static bool logged = false;
  if (!logged) {
    logged = true;
    LogGateOnce("yes/no");
  }
  const uint32_t mode = ctx.r3.u32;
  Answer answer;
  if (base == nullptr || !TakeAnswer(base, mode, kEventQuestion, answer)) {
    return;
  }
  REXLOG_INFO("[fable3-realestate] mode {:08X}: confirmation {:08X} of row {} answered: the guest's poll runs", mode,
              answer.box, answer.row);
  const uint32_t outer_mode = t_asking_mode;
  const int32_t outer_row = t_asking_row;
  t_asking_mode = mode;
  t_asking_row = answer.row;
  __imp__rex_RealEstate_Poll(ctx, base);
  t_asking_mode = outer_mode;
  t_asking_row = outer_row;
}

/// (mode), each tick the mode waits: the guest's adjuster poll, run only for the accepted value of
/// an adjuster this mode asked with. Wrapped for the same reason.
REX_EXTERN(rex_RealEstate_AdjusterPoll) {
  static bool logged = false;
  if (!logged) {
    logged = true;
    LogGateOnce("adjuster");
  }
  const uint32_t mode = ctx.r3.u32;
  Answer answer;
  if (base == nullptr || !TakeAnswer(base, mode, kEventAdjuster, answer)) {
    return;
  }
  REXLOG_INFO("[fable3-realestate] mode {:08X}: adjuster {:08X} of row {} accepted: the guest's poll runs", mode,
              answer.box, answer.row);
  __imp__rex_RealEstate_AdjusterPoll(ctx, base);
}
