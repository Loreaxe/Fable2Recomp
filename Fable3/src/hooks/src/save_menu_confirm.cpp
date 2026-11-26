// refii - the save menu's confirmation: the row runs on its own box's answer, and the guest's
// serial poll is off the path.
//
// THE GUEST'S MECHANISM. The save menu SV = *(SM+0x38) confirms Save, Save and Quit and (when a
// save is possible) Quit with a yes/no box. Its accept sub_829B55F8(SV) (disc sub_8296CDB0; one
// caller each, the menu's input at TU3 0x829B5324 / disc 0x8296CADC) stamps SV+8 with the message
// system's serial of the last event posted (`lwz r9,0x9B0(r6)` / `addi r5,r9,-1` / `stw r5,8(r3)`,
// TU3 0x829B562C..0x829B5634; disc `0x980`, 0x8296CDE0), pushes the box on Q0 (sub_82623BF0 at
// TU3 0x829B570C / 0x829B5824; disc sub_825FBBF8 at 0x8296CEBC / 0x8296CFD0) and stores the row in
// SV+4 (TU3 0x829B5728 / 0x829B5840; disc 0x8296CED8 / 0x8296CFEC). The box is not kept. Rows 3 with
// no save possible and 4 (Character Management) run inside the accept, with no confirmation.
// The per-tick poll sub_82291780(SV) (disc sub_82293478; one caller each, the GUI tick at TU3
// 0x821B6798 / disc 0x821B6658) does nothing unless SV+0 and SV+4 > 0 (0x82291794..0x822917AC);
// then it reads the NEWEST event-68 record of the message system — the list at MS+0x334, its count
// at MS+0x338 (0x822917C8..0x822917DC) — and, when its serial +0x28 is past SV+8 (0x822917E0..
// 0x822917E8), takes its value +0x24 as the answer: 1 runs the pending row, anything else only
// forgets it (`li r11,-1` / `stw r11,4(r30)`, 0x822918C0). SV+4 has no other reader (the open, the
// accept and the poll are its only users). So ANY event 68 posted after the accept answers the save
// menu — a yes/no pending in Q0 ahead of the confirmation, a natal query, or any question once the
// confirmation left its queue unanswered (it is named "Unnamed", as every unnamed message is).
//
// THE ROWS, as the poll runs them on a yes (TU3; the disc's poll is the same instruction for
// instruction, 0x8229348C..0x822935B8, its save sub_822F5098 taking five arguments, mgr at +0x98):
//   1 Save           the close sub_829B50A0(SV) (0x82291898), then SaveGame sub_822F8940(mgr, 0, 0, 0,
//                    1, 0) with mgr = *(*(GAME+8)+0x94) (0x8229189C..0x822918BC);
//   2 Save and Quit  the quit cue — sub_823333B0(&h, GUI, &dword_8359BDDC) and the handle's release
//                    sub_82E02BE8(&h) (0x82291844..0x82291860) — then SaveGame(mgr, 0, 0, 0, 0, 0)
//                    (0x82291864..0x82291884) and the quit to the front end sub_8234F698(GAME)
//                    (0x8229188C). No close: the screen manager's destruction ends the menu
//                    (display_box_hooks.cpp rex_ScreenManager_Destroy);
//   3 Quit           the close (0x82291814), the cue (0x82291818..0x82291834), the quit (0x8229183C).
//
// THE LEVER, AT THE STATEMENTS. Three of the guest's own calls say everything needed:
//   - the accept is running (this wrapper) while a box is pushed (rex_BoxQueue_Push): that box is
//     the confirmation of the row the accept leaves in SV+4;
//   - a confirmation's own handler took Godot's answer and the answer posted its event 68
//     (AnswerBoxAction: the handler's answer 1, the message serial moved): yes or no;
//   - a box left its queue (the queue tick's release, the pending erase): no longer a confirmation.
// The poll is replaced. On the GUI tick after a confirmation's "yes" it runs that confirmation's row,
// the calls above in the poll's order; a "no" runs nothing (the menu stays open, as the poll's
// forget leaves it). No event record is read, so nothing but the confirmation's own answer runs a
// row. Nothing of the guest's is written: SV+4 is the poll's alone, and the next accept or open
// replaces it.

#include <cstdint>
#include <mutex>
#include <vector>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "box_answers.h"
#include "gui_statements.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_SaveMenu_Accept);
REX_EXTERN(rex_SaveMenu_Close);
REX_EXTERN(rex_SaveGame);
REX_EXTERN(rex_QuitToFrontEnd);
REX_EXTERN(rex_GuiSound_Play);
REX_EXTERN(rex_SoundHandle_Release);

namespace {

using namespace refii::fable3;

const refii::guest::Addr kGuiManager{0x83596F84u};
const refii::guest::Addr kGame{0x83597034u};
/// The cue the poll plays before a quit (`addi r5,r10,dword_8359BDDC@l` 0x82291824 / 0x82291850).
const refii::guest::Addr kQuitCue{0x8359BDDCu};

/// SV+0 open, SV+4 the row awaiting its confirmation: the poll's own two tests (`lbz r11,0(r3)`
/// 0x82291794, `lwz r9,4(r3)` 0x822917A4).
constexpr uint32_t kSaveMenuOpen = 0u;
constexpr uint32_t kSaveMenuPendingRow = 4u;

constexpr int32_t kRowSave = 1;
constexpr int32_t kRowSaveAndQuit = 2;
constexpr int32_t kRowQuit = 3;

/// A confirmation in a queue: the box and the row its accept left pending.
struct Confirm {
  uint32_t box = 0u;
  int32_t row = 0;
};

/// A confirmation answered, waiting for the next poll.
struct Answer {
  int32_t row = 0;
  bool yes = false;
};

std::mutex g_mutex;
std::vector<Confirm>* g_confirms = nullptr;  // pushed by the accept and still in a queue
std::vector<uint32_t>* g_pushed = nullptr;   // pushed by the accept now running
bool g_has_answer = false;
Answer g_answer;

thread_local bool t_accepting = false;

std::vector<Confirm>& Confirms() {
  if (g_confirms == nullptr) {
    g_confirms = new std::vector<Confirm>();
  }
  return *g_confirms;
}

std::vector<uint32_t>& Pushed() {
  if (g_pushed == nullptr) {
    g_pushed = new std::vector<uint32_t>();
  }
  return *g_pushed;
}

void PlayQuitCue(PPCContext& ctx, uint8_t* base) {
  PPCContext work = ctx;
  work.r1.u32 = (ctx.r1.u32 - 0x200u) & ~0xFu;
  const uint32_t handle = work.r1.u32 + 0x100u;
  work.r3.u64 = handle;
  work.r4.u64 = LoadGuestU32(base, kGuiManager);
  work.r5.u64 = kQuitCue;
  rex_GuiSound_Play(work, base);
  work.r3.u64 = handle;
  rex_SoundHandle_Release(work, base);
}

void Save(PPCContext& ctx, uint8_t* base, bool player_save) {
  const uint32_t game = LoadGuestU32(base, kGame);
  const uint32_t services = Plausible(game) ? LoadGuestU32(base, game + 8u) : 0u;
  const uint32_t manager = Plausible(services) ? LoadGuestU32(base, services + refii::guest::Pick(0x94u, 0x98u)) : 0u;
  if (!Plausible(manager)) {
    REXLOG_WARN("[fable3-savemenu] no save manager: the save was not run");
    return;
  }
  PPCContext work = ctx;
  work.r1.u32 = (ctx.r1.u32 - 0x200u) & ~0xFu;
  work.r3.u64 = manager;
  work.r4.u64 = 0u;
  work.r5.u64 = 0u;
  work.r6.u64 = 0u;
  work.r7.u64 = player_save ? 1u : 0u;
  work.r8.u64 = 0u;  // TU3's sixth argument; the disc's save takes five
  rex_SaveGame(work, base);
}

void Quit(PPCContext& ctx, uint8_t* base) {
  PPCContext work = ctx;
  work.r1.u32 = (ctx.r1.u32 - 0x200u) & ~0xFu;
  work.r3.u64 = LoadGuestU32(base, kGame);
  rex_QuitToFrontEnd(work, base);
}

void Close(PPCContext& ctx, uint8_t* base, uint32_t menu) {
  PPCContext work = ctx;
  work.r1.u32 = (ctx.r1.u32 - 0x200u) & ~0xFu;
  work.r3.u64 = menu;
  rex_SaveMenu_Close(work, base);
}

}  // namespace

namespace refii::fable3 {

bool SaveMenuAccepting() { return t_accepting; }

void NoteSaveConfirmQueued(uint32_t box) {
  std::lock_guard<std::mutex> lock(g_mutex);
  Pushed().push_back(box);
}

void NoteQuestionAnswered(uint32_t box, bool yes, bool posted) {
  std::lock_guard<std::mutex> lock(g_mutex);
  for (const Confirm& confirm : Confirms()) {
    if (confirm.box != box) {
      continue;
    }
    if (!posted) {
      // The answer posted no event 68 (no player-one hero, `beq` TU3 0x82879B1C / 0x82879B2C): on the
      // 360 the poll found nothing of this box's.
      REXLOG_INFO("[fable3-savemenu] confirmation {:08X} of row {} closed with no answer posted: nothing runs",
                  box, confirm.row);
      return;
    }
    g_has_answer = true;
    g_answer = Answer{confirm.row, yes};
    REXLOG_INFO("[fable3-savemenu] confirmation {:08X} of row {} answered {}: the next poll {}", box, confirm.row,
                yes ? "yes" : "no", yes ? "runs the row" : "runs nothing");
    return;
  }
}

void NoteBoxGone(uint32_t box) {
  std::lock_guard<std::mutex> lock(g_mutex);
  std::vector<Confirm>& confirms = Confirms();
  for (size_t i = 0; i < confirms.size(); ++i) {
    if (confirms[i].box == box) {
      confirms.erase(confirms.begin() + static_cast<std::ptrdiff_t>(i));
      return;
    }
  }
}

void ForgetSaveConfirms() {
  std::lock_guard<std::mutex> lock(g_mutex);
  Confirms().clear();
  Pushed().clear();
  g_has_answer = false;
}

}  // namespace refii::fable3

/// (SV): every box the accept pushes is a confirmation of the row it leaves in SV+4.
REX_EXTERN(rex_SaveMenu_Accept) {
  const uint32_t menu = ctx.r3.u32;
  t_accepting = true;
  __imp__rex_SaveMenu_Accept(ctx, base);
  t_accepting = false;
  if (base == nullptr || !Plausible(menu)) {
    return;
  }
  const int32_t row = static_cast<int32_t>(LoadGuestU32(base, menu + kSaveMenuPendingRow));
  std::lock_guard<std::mutex> lock(g_mutex);
  for (uint32_t box : Pushed()) {
    Confirms().push_back(Confirm{box, row});
    REXLOG_INFO("[fable3-savemenu] confirmation {:08X} of row {} queued by the menu's accept", box, row);
  }
  Pushed().clear();
}

/// (SV), once a GUI tick: the row a confirmation's "yes" confirmed, in the poll's own calls; the
/// guest's poll body never runs.
REX_EXTERN(rex_SaveMenu_Poll) {
  const uint32_t menu = ctx.r3.u32;
  Answer answer;
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    if (!g_has_answer) {
      return;
    }
    answer = g_answer;
    g_has_answer = false;
  }
  if (!answer.yes) {
    return;
  }
  if (base == nullptr || !Plausible(menu) || base[menu + kSaveMenuOpen] == 0u) {
    REXLOG_INFO("[fable3-savemenu] row {} confirmed with the menu closed: nothing runs", answer.row);
    return;
  }
  REXLOG_INFO("[fable3-savemenu] row {} confirmed: {}", answer.row,
              answer.row == kRowSave          ? "close, save"
              : answer.row == kRowSaveAndQuit ? "cue, save, quit to the front end"
              : answer.row == kRowQuit        ? "close, cue, quit to the front end"
                                              : "no such row");
  switch (answer.row) {
    case kRowSave:
      Close(ctx, base, menu);
      Save(ctx, base, true);
      break;
    case kRowSaveAndQuit:
      PlayQuitCue(ctx, base);
      Save(ctx, base, false);
      Quit(ctx, base);
      break;
    case kRowQuit:
      Close(ctx, base, menu);
      PlayQuitCue(ctx, base);
      Quit(ctx, base);
      break;
    default:
      break;
  }
}
