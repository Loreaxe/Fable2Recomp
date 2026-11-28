// refii - THE TEXTS TWO GODOT-OWNED SCREENS BUILT ONLY FOR AN ELEMENT, stated from the guest's own
// calls. Their screens are never loaded (screen_cut_hooks.cpp) and their path-setter writes are stated
// by the token; these two texts went to a Find'ed element and are stated here instead. TU3 addresses;
// both bodies read on both builds (the same instruction for instruction, registers included).
//
// THE PROMISE'S GAMER NAME. CPromiseMessageBox_Show sub_827DA840 / disc sub_827A3398 (box) resolves
// `Promises.Player.GamerTag` (dword_8359BC10, Find 0x827DA8C8) and only then asks the name of its
// gamer record `box+0x20`: sub_8245F2A8 / disc sub_8243FBB8 (record) (`addi r3,r31,0x20; bl`
// 0x827DA8D8..0x827DA8DC / 0x827A3430..0x827A3434), whose LHString it commits (0x827DA900). Under the
// token Find answers nothing, so the name is never asked. rex_PromiseBox_Show runs the Show, then asks
// it once, as the Show did once, and states `@Ui.Text(=promises, 0, "Promises.Player.GamerTag", name)`.
// The rest of the Show is stated by the token (the signature and button slides) or by the ctor (the
// promise's tag, `@Screen.Promise`); its picture sub_827DACA0 resolves first and so requests nothing.
//
// THE QUEST SUMMARY'S ROW TEXTS. The row writer sub_829BCA18 / disc sub_82973FB0 (box r29, slot r30,
// quest r28) builds its texts BEFORE it asks `Pres` (`mr r3,r29; bl sub_829588C8` 0x829BCB70 /
// 0x82974108), on its frame: the quest's name as a CCharString at r1+0x5C (sub_82517498 then the copy
// sub_82228298, 0x829BCAA0..0x829BCAAC), the location as an LHString at r1+0x54 (the quest's +0xA0, else
// its region sub_829BD308, through the world's text table sub_82354C18, 0x829BCAB8..0x829BCB3C), the
// guild seals as an LHString at r1+0x58 (sub_82304950 of `quest+0x34` when above 0, 0x829BCB48..
// 0x829BCB64), all three empty for no quest (`beq` 0x829BCA94 straight to 0x829BCB70). The name is
// turned into its text only behind the row's element (sub_823CBB30 0x829BCBD8 — the same lookup
// GuiElement_SetTextName makes, so Godot resolves it as it does `@Ui.TextName`). Midasm
// RefiiF3QuestRowTexts at 0x829BCB70 / 0x82974108: `@Screen.QuestRowText(box, slot, name, location,
// seals)`.

#include <atomic>
#include <cstdint>
#include <string>

#include <rex/logging.h>
#include <rex/ppc.h>
#include <rex/system/kernel_state.h>
#include <rex/system/xmemory.h>

#include "gui_statements.h"
#include "mod_seam.h"

REX_EXTERN(__imp__rex_PromiseBox_Show);
REX_EXTERN(rex_GamerRecord_Name);

namespace {

using namespace refii::fable3;

constexpr uint32_t kPromiseScreen = 0x0Du;
constexpr uint32_t kPromiseGamerRecord = 0x20u;

uint8_t* GuestBase() {
  rex::system::KernelState* state = rex::system::kernel_state();
  rex::memory::Memory* memory = state != nullptr ? state->memory() : nullptr;
  return memory != nullptr ? memory->virtual_membase() : nullptr;
}

}  // namespace

/// CPromiseMessageBox_Show(box): the Show, then its gamer name.
REX_EXTERN(rex_PromiseBox_Show) {
  const uint32_t box = ctx.r3.u32;
  __imp__rex_PromiseBox_Show(ctx, base);
  const uint64_t answer = ctx.r3.u64;
  if (base != nullptr && Plausible(box)) {
    PPCContext work = refii::fable3::seam::Frame(ctx);
    work.r3.u64 = box + kPromiseGamerRecord;
    rex_GamerRecord_Name(work, base);
    StateText(ScreenToken(kPromiseScreen), 0u, "Promises.Player.GamerTag", ReadLhString(base, work.r3.u32), "827A3438");
    static std::atomic<bool> said{false};
    if (!said.exchange(true)) {
      REXLOG_INFO("[fable3-screens] promise: its gamer name is asked after the Show and stated (no element)");
    }
  }
  ctx.r3.u64 = answer;
}

/// The quest summary's row writer before its `Pres`: box r29, slot r30, quest r28, the frame r1.
void RefiiF3QuestRowTexts(PPCRegister& box, PPCRegister& slot, PPCRegister& quest, PPCRegister& stack) {
  uint8_t* base = GuestBase();
  if (base == nullptr) {
    return;
  }
  Statement("@Screen.QuestRowText")
      .Add(Text(Hex(box.u32)))
      .Add(Number(static_cast<float>(static_cast<int32_t>(slot.u32))))
      .Add(Text(quest.u32 != 0u ? ReadCharString(base, stack.u32 + 0x5Cu, 260u) : std::string()))
      .Add(Text(ReadLhString(base, stack.u32 + 0x54u)))
      .Add(Text(ReadLhString(base, stack.u32 + 0x58u)))
      .Post();
}
