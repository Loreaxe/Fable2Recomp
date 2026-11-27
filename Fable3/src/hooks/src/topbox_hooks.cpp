// refii - Fable 3's HUD text boxes (TopBox, the corner counters, the subtitles) and the TopBox's
// message queue, stated at the calls that carry them, for Godot's Fable3HudRegistry.
//
// Every address is the Fable 3 disc IDB (port 13339); each offset names its base.
//
// THE LINES. sub_82976D48(R7, boxHash, lines*, f1 pad) is the one writer of a text box's content:
// it finds the records filed under the box's name in the TextBox registry R7 (sub_8218D820 at
// 0x82976D64), lays the lines into each (sub_829763F8 at 0x82976DA4: wrap with the guest's font,
// write each wrapped line through the text registry sub_827681B8, place the line elements with
// sub_82976790 -> sub_82876288 at 0x8297695C) and then frees the vector (0x82976DCC..0x82976DF4).
// The vector is `+4 begin, +8 end`, 16-byte entries `{+0 line name hash, +4 LHString, +8 bool}`
// (the free walk sub_824D1E50 steps 4 words and releases +4; sub_829763F8 sizes it `& ~15` at
// 0x82976434). It is read at ENTRY: an unfiled box (no R7 record) writes nothing, and the text
// is what the caller said, unwrapped. The TopBox's publisher sub_8217D7A0 passes the box
// `*dword_83598CD0` (0x8217DB40) and the lines TopTitle (`*disc dword_83558108`) and TopMain
// (`*disc dword_83558104`, 0x8217DAE4). `@Hud.Lines(box, n, hash1, text1, flag1, ...)`.
//
// THE TOPBOX QUEUE M = *(H+0x4C): post disc sub_824B5EE0(M, channel, Msg*, broadcast, player) — 42
// callers, e.g. Lua ShowTopBoxMessage disc 0x82521114 — refuses a message whose main text (Msg+8,
// kind vf0) is a tag the book does not hold (disc 0x824B5F3C) and pushes the rest into channel
// `*(M+76)[channel]` (+4 begin, +8 end of record pointers, record+0 the id). The Msg (ctor
// sub_824CF630) is +0 id, +4 title tag, +8 main text object (vtable, +4 its tag), +16 lifetime
// (float), +20 A label tag, +24 Y label tag, bytes +28 show A, +29 hold A, +31 show Y, +32 hold Y.
// Stated on return when a channel holds the id: `@TopBox.Post`. Removal sub_824D0470(channel, id)
// — the per-channel erase every remover reaches (sub_824D1570 at 0x824D15B4) — returns 1 when it
// erased: `@TopBox.Remove(id)`. Clear sub_824D14C0(M): `@TopBox.Clear`. Answer disc sub_824B70E0(M,
// answer 1 A / 4 Y): `@TopBox.Answer(answer)`. The current message is the back of the first
// non-empty channel (sub_82194538) — Godot's to work out from these.

#include <atomic>
#include <cstdint>
#include <string>

#include <rex/ppc.h>

#include "gui_statements.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_HudTextBox_SetLines);
REX_EXTERN(__imp__rex_TopBox_Post);
REX_EXTERN(__imp__rex_TopBox_RemoveFromChannel);
REX_EXTERN(__imp__rex_TopBox_Clear);
// Disc only: TU3 has no counterpart (its answers post through the generic sub_824D1BC0, stated by
// RefiiF3TopBoxAnswered and the replaced hold step), so the TU3 payload loads without it.
extern "C" __attribute__((weak)) REX_FUNC(__imp__rex_TopBox_Answer);

namespace {

using namespace refii::fable3;

const refii::guest::Addr kTopBoxNameHash{0x83598CD0u};
constexpr uint32_t kLinesBegin = 4u;
constexpr uint32_t kLinesEnd = 8u;
constexpr uint32_t kLineSize = 16u;
constexpr uint32_t kLineText = 4u;
constexpr uint32_t kLineFlag = 8u;
constexpr uint32_t kMostLines = 32u;

constexpr uint32_t kQueueChannels = 76u;  // M+76 begin, M+80 end (sub_824D1570 at 0x824D1598)
constexpr uint32_t kChannelBegin = 4u;
constexpr uint32_t kChannelEnd = 8u;
constexpr uint32_t kMostChannels = 16u;
constexpr uint32_t kMostRecords = 64u;

constexpr uint32_t kMsgId = 0u;
constexpr uint32_t kMsgTitle = 4u;
constexpr uint32_t kMsgMain = 8u;
constexpr uint32_t kMsgLifetime = 16u;
constexpr uint32_t kMsgLabelA = 20u;
constexpr uint32_t kMsgLabelY = 24u;
constexpr uint32_t kMsgShowA = 28u;
constexpr uint32_t kMsgHoldA = 29u;
constexpr uint32_t kMsgShowY = 31u;
constexpr uint32_t kMsgHoldY = 32u;

thread_local uint32_t t_laid_out_box = 0u;

/// Whether any channel of the queue holds a record with this id.
bool Queued(const uint8_t* base, uint32_t manager, uint32_t id) {
  const uint32_t begin = LoadGuestU32(base, manager + kQueueChannels);
  const uint32_t end = LoadGuestU32(base, manager + kQueueChannels + 4u);
  if (!Plausible(begin) || end < begin || (end - begin) / 4u > kMostChannels) {
    return false;
  }
  for (uint32_t at = begin; at < end; at += 4u) {
    const uint32_t channel = LoadGuestU32(base, at);
    if (!Plausible(channel)) {
      continue;
    }
    const uint32_t first = LoadGuestU32(base, channel + kChannelBegin);
    const uint32_t last = LoadGuestU32(base, channel + kChannelEnd);
    if (!Plausible(first) || last < first || (last - first) / 4u > kMostRecords) {
      continue;
    }
    for (uint32_t slot = first; slot < last; slot += 4u) {
      const uint32_t record = LoadGuestU32(base, slot);
      if (Plausible(record) && LoadGuestU32(base, record) == id) {
        return true;
      }
    }
  }
  return false;
}

}  // namespace

uint32_t refii::fable3::LaidOutBox() { return t_laid_out_box; }

uint32_t refii::fable3::TopBoxName(const uint8_t* base) {
  return base != nullptr ? LoadGuestU32(base, kTopBoxNameHash) : 0u;
}

/// sub_82976D48(R7, box, lines, f1): the lines at entry; the box named for the position hook
/// while its layout runs.
REX_EXTERN(rex_HudTextBox_SetLines) {
  const uint32_t box = ctx.r4.u32;
  if (base != nullptr && Plausible(ctx.r5.u32)) {
    const uint32_t begin = LoadGuestU32(base, ctx.r5.u32 + kLinesBegin);
    const uint32_t end = LoadGuestU32(base, ctx.r5.u32 + kLinesEnd);
    if (Plausible(begin) && end >= begin && (end - begin) / kLineSize <= kMostLines) {
      Statement statement("@Hud.Lines");
      statement.Add(Text(Hex(box))).Add(Number(static_cast<float>((end - begin) / kLineSize)));
      for (uint32_t line = begin; line + kLineSize <= end; line += kLineSize) {
        statement.Add(Text(Hex(LoadGuestU32(base, line))))
            .Add(Text(ReadLhString(base, line + kLineText)))
            .Add(Boolean(base[line + kLineFlag] != 0u));
      }
      statement.Post();
    }
  }
  const uint32_t outer = t_laid_out_box;
  t_laid_out_box = box;
  __imp__rex_HudTextBox_SetLines(ctx, base);
  t_laid_out_box = outer;
}

/// sub_824D0CC8(M, channel, msg, target): TU3 folds the disc's (…, broadcast, player) into one target
/// in r6 (`mr r29,r6` 0x824D0CE4; 1 and 2 are the disc's players, 0 also sends it to the network,
/// 3 is local only, 4 is never queued) and reads no r7; the disc's player is r7 (`mr r28,r7` disc
/// 0x824B5F00).
REX_EXTERN(rex_TopBox_Post) {
  const uint32_t manager = ctx.r3.u32;
  const uint32_t channel = ctx.r4.u32;
  const uint32_t msg = ctx.r5.u32;
  const uint32_t player = refii::guest::Pick(ctx.r6.u32, ctx.r7.u32);
  const bool readable = base != nullptr && Plausible(manager) && Plausible(msg);
  uint32_t id = 0u, title = 0u, main_vtable = 0u, main_tag = 0u, label_a = 0u, label_y = 0u;
  float lifetime = 0.0f;
  uint8_t show_a = 0u, hold_a = 0u, show_y = 0u, hold_y = 0u;
  if (readable) {
    id = LoadGuestU32(base, msg + kMsgId);
    title = LoadGuestU32(base, msg + kMsgTitle);
    const uint32_t main = LoadGuestU32(base, msg + kMsgMain);
    if (Plausible(main)) {
      main_vtable = LoadGuestU32(base, main);
      main_tag = LoadGuestU32(base, main + 4u);
    }
    lifetime = LoadGuestF32(base, msg + kMsgLifetime);
    label_a = LoadGuestU32(base, msg + kMsgLabelA);
    label_y = LoadGuestU32(base, msg + kMsgLabelY);
    show_a = base[msg + kMsgShowA];
    hold_a = base[msg + kMsgHoldA];
    show_y = base[msg + kMsgShowY];
    hold_y = base[msg + kMsgHoldY];
  }
  __imp__rex_TopBox_Post(ctx, base);
  if (!readable || !Queued(base, manager, id)) {
    return;
  }
  Statement("@TopBox.Post")
      .Add(Text(Hex(id)))
      .Add(Number(static_cast<float>(static_cast<int32_t>(channel))))
      .Add(Text(Hex(title)))
      .Add(Text(Hex(main_vtable)))
      .Add(Text(Hex(main_tag)))
      .Add(Number(lifetime))
      .Add(Text(Hex(label_a)))
      .Add(Text(Hex(label_y)))
      .Add(Boolean(show_a != 0u))
      .Add(Boolean(hold_a != 0u))
      .Add(Boolean(show_y != 0u))
      .Add(Boolean(hold_y != 0u))
      .Add(Number(static_cast<float>(static_cast<int32_t>(player))))
      .Post();
}

/// sub_824D0470(channel, id): `@TopBox.Remove(id)` when it erased.
REX_EXTERN(rex_TopBox_RemoveFromChannel) {
  const uint32_t id = ctx.r4.u32;
  __imp__rex_TopBox_RemoveFromChannel(ctx, base);
  if ((ctx.r3.u32 & 0xFFu) != 0u) {
    Statement("@TopBox.Remove").Add(Text(Hex(id))).Post();
  }
}

/// sub_824D14C0(M).
REX_EXTERN(rex_TopBox_Clear) {
  __imp__rex_TopBox_Clear(ctx, base);
  Statement("@TopBox.Clear").Post();
}

/// disc sub_824B70E0(M, answer), called by the hold and press answers (disc 0x824B6F4C, 0x824B70D0).
REX_EXTERN(rex_TopBox_Answer) {
  if (&__imp__rex_TopBox_Answer == nullptr) {
    return;
  }
  const uint32_t answer = ctx.r4.u32;
  Statement("@TopBox.Answer").Add(Number(static_cast<float>(static_cast<int32_t>(answer)))).Post();
  __imp__rex_TopBox_Answer(ctx, base);
}

/// TU3's press answer (sub_824D1B08), on its `bl sub_824D1BC0` (0x824D1BA0): r3 is the answer, which
/// sub_824D1BC0 posts as message 101. Not the function itself: the hold box and the button box answer
/// through it too. The hold's answer is stated by the replaced hold step (topbox_answer.cpp).
void RefiiF3TopBoxAnswered(PPCRegister& answer) {
  Statement("@TopBox.Answer").Add(Number(static_cast<float>(static_cast<int32_t>(answer.u32)))).Post();
}
