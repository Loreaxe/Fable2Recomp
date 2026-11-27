// refii - Fable 3's session boxes, Godot's: the join and visibility options, the friends channel and
// co-op partners. Godot draws them and owns their pad (Fable3BoxAnswers / Fable3SessionBoxAnswers);
// each answer goes to the box's OWN pad handler as a `box:` lever with the player who pressed
// (display_box_hooks.cpp's gate, GateOwnedInput / RunOwnedHandler), after storing the one field that
// handler's answer reads. TU3 addresses first, disc second; every body named here is the one word of
// its vtable (word scan of 0x82000000..0x82160000 on both builds: one vtable word and its .pdata row).
//
// THE ROUTER sub_825F38F8 / disc sub_825C8B68 calls the showing box's slot 4 (`lwz r10,0x10(r11)` /
// `bctrl` 0x825F3974..0x825F397C) and never reads its answer: its own answer is 1 for 29..36 and
// 72..85 while a box shows (0x825F39EC..0x825F3A40), 0 for anything else. So a gated handler changes
// nothing for the dispatcher: 41 / 42 go on past the router whether or not a box's handler took them.
//
// JOIN / VISIBILITY (CSimpleOnlineOptionsBox: vtable 0x820C6B58 / 0x820C35F8; +4 Show sub_829BF3E0 /
// sub_82976A08, +16 HandleInput sub_829BF5E0 / sub_82976BC8, +20 Update sub_829BF5B0 / sub_82976B98;
// +8 Hide sub_827DAAA8 / sub_829759D0 is shared with the promise and hero-status boxes and is only
// called through the vtable). One class, its type +48 the ctor's r4 (18 join, 19 visibility:
// Lua_GUI_ShowJoinOptions / ShowVisibilityOptions -> sub_82551788 / sub_82521928), its entity the
// weak reference +56/+60.
//   - Show, REPLACED: `+12 = 1` (0x829BF3F4 / 0x82976A24), BoxShown (0x829BF3F8 / 0x82976A28); answer 0
//     unless the entity lives (`lbz r11,0x94(r3)` bit 0x40, 0x829BF43C / 0x82976A6C); the setting read
//     sub_829BFAC0 / sub_829770A8 (0x829BF464) without its presentation tail (0x829BFD38..0x829BFD7C):
//     the entity's component 37 (CECPlayer) +0x6C for join (`lwz r30,0x6C(r31)` 0x829BFBC8 / 0x829771B0:
//     0 -> 1, 1 -> 2, 2 -> 3, else 1) or +0x68 for visibility (`lwz r11,0x68(r31)` 0x829BFCE0 /
//     0x829772C8: 0 -> 3, 1 -> 2, else 1; no component leaves +52 as it is), stored at +52 as the guest
//     stores it (0x829BFBE0 / 0x829BFCF4 / 0x829BFD00); the HUD root's hide sub_824CD7E8 (0x829BF594 /
//     0x82976B70); the answer 1. Dropped: the presentation sub_829588C8 (0x829BF400), the cursor slide
//     and the current-setting text (0x829BF4A0 / 0x829BF530), the hero's gamer picture (0x829BF584).
//     The current setting's tag is the Show's table entry: join `dword_8359D468[row-1]` / disc
//     dword_8355C65C, visibility `dword_8359D474[row-1]` / disc dword_8355C668 (0x829BF4D4..0x829BF4FC).
//     `@Screen.Online(box, type, row, tag)`.
//   - Update, REPLACED: the hero's name and picture sub_829C00D8 / disc sub_82977670 (0x829BF5C4, under
//     the presentation at 0x829C028C) dropped; the answer `+12` (`lbz r3,0xC(r31)` 0x829BF5C8).
//   - HandleInput, GATED: shown `+12` (0x829BF5EC), the entity's component 37 pad `+0x14 -> +0x24`
//     against r5 when the entity lives (`cmpw cr6,r11,r27` 0x829BF760 / 0x82976D48); 74 plays
//     `dword_8359BF48` / disc dword_8355BB48, applies sub_829BFD88 / sub_82977370 (0x829BF7F8, which
//     reads the row `+52`: `lwz r11,0x34(r28)` 0x829BFEE8 / 0x829C0030) and hides (`bctrl` 0x829BF80C);
//     75 plays `dword_8359BF4C` and hides (0x829BF854). The apply calls no presentation: join mode
//     sub_82431AB0(comp37, row - 1) / sub_82413D78 (0x829BFF10..0x829BFF38), visibility `comp37+0x68`
//     and the live presence (sub_82536978 0x829C0094 / disc sub_826DE718 0x82977664).
//     The stick's movers sub_829BF870 (29) / sub_829BF998 (30) (disc sub_82976E58 / sub_82976F80) step
//     `+52` within 1..3 (`stw r9,0x34(r29)` 0x829BF908 / 0x829BFA30), playing `dword_8359BF58` up /
//     `dword_8359BF5C` down (disc dword_8355BB58 / BB5C) only when it moves, then redraw behind the
//     presentation (0x829BF944).
//     Levers: `choose:<1..3>` (stores `+52`, then 74; a refused answer puts `+52` back), `close` (75),
//     `move:<1..3>` (Godot's cursor: after the handler's own test — shown, and the entity's pad when it
//     lives and has component 37, 0x829BF5EC..0x829BF764 — `+52` stored and the mover's cue played).
//
// THE FRIENDS CHANNEL (box name "FriendsChannel"; vtable 0x820C6BE0 / 0x820C3640; no class name in
// the PC build's RTTI — its .bgf name is in that build's presentation table, the class is not: +4 Show
// sub_829C04B0 / sub_82977A48, +8 Hide sub_829C05A8 / sub_82977B20, +16 HandleInput sub_829C0600 /
// sub_82977B78, +20 Update sub_829C0A50 / sub_82977F58). Built inline by Lua_GUI_ShowFriendsChannel
// 0x82551950 / 0x82521AF0: 64 / 60 bytes, the entity +40/+44.
//   - Show, REPLACED: `+12 = 1`, BoxShown, the cursor `+36 = 1` (0x829C04C4..0x829C04CC / 0x82977A5C..
//     0x82977A64), TU3 `+60 = 1` (0x829C04D0); answer 0 unless the entity lives (0x829C0514 / 0x82977AA8);
//     TU3 only: in a live game `+60 = 0` (Lua_Network_IsInLiveGame 0x829C054C, `stb r11,0x3C(r31)`
//     0x829C0564); the HUD root's hide (0x829C0580 / 0x82977AF4); the open byte TU3 `+12 = 1` (0x829C0588)
//     / disc `+48 = 1` (0x82977AFC); the answer 1. Dropped: the presentation (0x829C04D8), the cursor
//     slide (0x829C0548), the ghosting of row 1 sub_829C0DA8 (0x829C0568) and the hero's name and gamer
//     picture sub_829C0A80 / sub_82977F88 (0x829C0570 / 0x82977AE4). `@Screen.Friends(box, invite)`:
//     TU3's `+60`, the disc true (it has no such byte).
//   - Update, REPLACED: sub_829C0A80 / sub_82977F88 (0x829C0A64 / 0x82977F6C) dropped; the answer TU3
//     `+12` (0x829C0A68) / disc `+48` (`lbz r3,0x30(r31)` 0x82977F70).
//   - HandleInput, GATED: the entity's component 37 pad against r5 (`cmpw cr6,r11,r27` 0x829C0780 /
//     0x82977CF8); 74 runs the row `+36` (`lwz r11,0x24(r29)` 0x829C08D8; `addi r11,r11,-1`, `cmplwi
//     r11,3`, `bdzf` 0x829C08DC..0x829C08F8): row 1 sub_8236BA78 / sub_82365190 (XamShowGameInviteUI,
//     TU3 only when `+60`, else the refusal cue 0x8359BF4C and no hide), row 2 sub_8236BB08 / sub_82365220
//     (XamShowCommunitySessionsUI through the table LoadLibraryA("xam.xex") filled, ordinal 0x30B), row 3
//     sub_8236BC10 / sub_82365328 (XamShowPartyUI, ordinal 0x305), each with the user `comp37+0x14 ->
//     +0x28`, the accept cue `dword_8359BF48`, and the hide (`bctrl` 0x829C09EC / 0x82977EF4); 75 plays
//     `dword_8359BF4C` and hides (0x829C0A34 / 0x82977F3C). Nothing reads the system UI's answer.
//     The stick (26 / 27 to 29 / 30, sub_82B74918) steps `+36` within 1..3 (0x829C07EC..0x829C0870),
//     playing `dword_8359BF58` up / `dword_8359BF5C` down only when it moves.
//     Levers: `row:<1..3>` (stores `+36`, then 74; a refused answer puts it back), `close` (75),
//     `move:<1..3>` (Godot's cursor: after the handler's own test — shown, the entity live, its
//     component 37 present and its pad r5, 0x829C060C..0x829C0784 — `+36` stored, the mover's cue).
//
// CO-OP PARTNERS (CMultiplayerPartnersBox: vtable 0x820DB154 / 0x820D8754; +16 HandleInput
// sub_82B916C0 / sub_82B5CA70). Its presentation is still made: its row writer sub_82B92300 /
// sub_82B5D6A8 — called by its Show (0x82B91540) and Update (0x82B9169C) — computes each window row's
// online and joinable bytes (sub_8238F7E0 / sub_8238F850, 0x82B92384 / 0x82B9239C) and the cursor row's
// joinable into `+52` (`stb r10,0x34(r31)` 0x82B92490 / 0x82B5D838) behind its presentation lookup
// (0x82B92310), and the A answer reads `+52`.
//   - HandleInput, GATED: shown and `+76 == r5` (`lwz r11,0x4C(r3)` / `cmpw cr6,r11,r5` 0x82B916E0 /
//     0x82B5CA90); the record is `+36 + 64 * (+60 + +56)` of the list +36..+40 (0x82B91778..0x82B917A4);
//     74 plays `dword_8359BF48` and, with `+52`, hides and joins that record's session
//     sub_82396260(*(NET+0x20), XUID +16 or +8, 0) / sub_8238BEA0 (0x82B917B0..0x82B917D8); 77 with a
//     list plays `unk_8359BF44` and shows the gamer card sub_82B92B40 / sub_82B5DEE8 (0x82B919C4); 76
//     with a list hides and opens the divorce box sub_82B92D28 / sub_82B5E0D0 (0x82B91A08); 75 plays
//     `dword_8359BF4C` and hides (0x82B91964).
//   - Levers: `row:<i>` / `join:<i>` / `card:<i>` / `divorce:<i>` — after the handler's own test (shown,
//     `+76`) and the list bound, the cursor `+56 = min(i, 7)` and scroll `+60 = i - +56` (the window the
//     stick's own steps keep, 0x82B91850..0x82B91880), then the row writer (so `+52` and the window follow
//     the record), `@Screen.Cursor(box, i)`, and for join / card / divorce the handler with 74 / 77 / 76;
//     `close` (75). A `row` lever plays the stick's cue `unk_8359BF54` / disc unk_8355BB54, which the
//     guest plays on every step press before its own move (0x82B918A0..0x82B918BC / 0x82B917F4..).
//
// UI CUES are still the guest's: every one named above plays from the handler Godot's lever runs.

#include <atomic>
#include <cstdint>
#include <cstdlib>
#include <string>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "box_answers.h"
#include "gui_statements.h"
#include "mod_seam.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_JoinOptionsBox_HandleInput);
REX_EXTERN(__imp__rex_FriendsChannelBox_HandleInput);
REX_EXTERN(__imp__rex_PartnersBox_HandleInput);
REX_EXTERN(rex_JoinOptionsBox_HandleInput);
REX_EXTERN(rex_FriendsChannelBox_HandleInput);
REX_EXTERN(rex_PartnersBox_HandleInput);
REX_EXTERN(rex_PartnersBox_Rows);
REX_EXTERN(rex_DisplayBox_Shown);
REX_EXTERN(rex_Hud_Hide);
REX_EXTERN(rex_Network_IsInLiveGame);

namespace {

using namespace refii::fable3;

const refii::guest::Addr kGuiManager{0x83596F84u};
constexpr uint32_t kGuiHud = 0x20u;

/// The session boxes' vtables (TU3; classified by ToTu3).
constexpr uint32_t kVtOnlineOptions = 0x820C6B58u;
constexpr uint32_t kVtFriendsChannel = 0x820C6BE0u;
constexpr uint32_t kVtPartners = 0x820DB154u;

constexpr uint32_t kBoxShown = 12u;
constexpr uint32_t kComponentPlayer = 37u;  // CECPlayer: +0x14 -> +0x24 the pad, +0x6C join, +0x68 visibility

/// CSimpleOnlineOptionsBox.
constexpr uint32_t kOnlineType = 0x30u;
constexpr uint32_t kOnlineRow = 0x34u;
constexpr uint32_t kOnlineEntityRef = 0x38u;
constexpr uint32_t kTypeJoin = 18u;
constexpr uint32_t kTypeVisibility = 19u;
constexpr uint32_t kPlayerJoinMode = 0x6Cu;
constexpr uint32_t kPlayerVisibility = 0x68u;
const refii::guest::Addr kJoinTags{0x8359D468u};
const refii::guest::Addr kVisibilityTags{0x8359D474u};
/// The movers' cues: up, down (the join / visibility and friends movers), and the partners' step.
const refii::guest::Addr kCueStepUp{0x8359BF58u};
const refii::guest::Addr kCueStepDown{0x8359BF5Cu};
const refii::guest::Addr kCueStep{0x8359BF54u};

/// The friends channel.
constexpr uint32_t kFriendsRow = 0x24u;
constexpr uint32_t kFriendsEntityRef = 0x28u;
constexpr uint32_t kFriendsInvite = 0x3Cu;  // TU3 only
uint32_t FriendsOpen() { return refii::guest::Pick(0x0Cu, 0x30u); }

/// CMultiplayerPartnersBox.
constexpr uint32_t kPartnersListBegin = 0x24u;
constexpr uint32_t kPartnersListEnd = 0x28u;
constexpr uint32_t kPartnersCursor = 0x38u;
constexpr uint32_t kPartnersScroll = 0x3Cu;
constexpr uint32_t kPartnersPad = 0x4Cu;
constexpr uint32_t kPartnersRecord = 64u;
constexpr uint32_t kPartnersWindowLast = 7u;

constexpr uint32_t kActionAPressed = 74u;
constexpr uint32_t kActionBPressed = 75u;
constexpr uint32_t kActionXPressed = 76u;
constexpr uint32_t kActionYPressed = 77u;

void Said(std::atomic<bool>& said, const char* what) {
  if (!said.exchange(true)) {
    REXLOG_INFO("[fable3-boxes] {}", what);
  }
}

void CallBox(PPCContext& ctx, uint8_t* base, void (*fn)(PPCContext&, uint8_t*), uint32_t box) {
  PPCContext work = seam::Frame(ctx);
  work.r3.u64 = box;
  fn(work, base);
}

void HideHudRoot(PPCContext& ctx, uint8_t* base) {
  const uint32_t gui = LoadGuestU32(base, kGuiManager);
  PPCContext work = seam::Frame(ctx);
  work.r3.u64 = Plausible(gui) ? LoadGuestU32(base, gui + kGuiHud) : 0u;
  rex_Hud_Hide(work, base);
}

/// The object a box's weak reference at `ref` holds, when it passes the guest's live test.
uint32_t LiveEntity(const uint8_t* base, uint32_t ref) {
  const uint32_t entity = seam::Deref(base, ref);
  return seam::Live(base, entity) ? entity : 0u;
}

/// A mover's store and its cue: `+offset = row`, the up cue when it went down a row, the down cue up.
void Move(PPCContext& ctx, uint8_t* base, uint32_t box, uint32_t offset, uint32_t row) {
  const uint32_t before = LoadGuestU32(base, box + offset);
  seam::StoreU32(base, box + offset, row);
  if (row != before) {
    PlayGuestCue(ctx, base, row < before ? static_cast<uint32_t>(kCueStepUp) : static_cast<uint32_t>(kCueStepDown));
  }
}

uint32_t ToInt(const std::string& text, bool& ok) {
  char* end = nullptr;
  const long value = std::strtol(text.c_str(), &end, 10);
  ok = !text.empty() && end != nullptr && *end == '\0' && value >= 0;
  return ok ? static_cast<uint32_t>(value) : 0u;
}

/// Stores `value` at `box + offset`, runs the handler with `action`, and puts the old value back
/// when the handler refused (its answer 0: not shown, not this player's).
template <typename Store>
bool StoreAndRun(PPCContext& ctx, uint8_t* base, GuestFunction handler, uint32_t box, uint32_t offset, uint32_t value,
                 uint32_t action, uint32_t player, const char* lever, Store store) {
  const uint32_t before = LoadGuestU32(base, box + offset);
  store(box + offset, value);
  if (RunOwnedHandler(ctx, base, handler, box, action, player) == 0u) {
    store(box + offset, before);
    REXLOG_INFO("[fable3-boxes] answer '{}': the box's handler refused it", lever);
  }
  return true;
}

}  // namespace

namespace refii::fable3 {

int64_t BoxOwnerPad(const uint8_t* base, uint32_t entity_ref) {
  const uint32_t entity = LiveEntity(base, entity_ref);
  const uint32_t player = entity != 0u ? seam::Component(base, entity, kComponentPlayer) : 0u;
  if (player == 0u) {
    return kNoOwnerTest;
  }
  const uint32_t record = LoadGuestU32(base, player + 0x14u);
  return Plausible(record) ? static_cast<int64_t>(static_cast<int32_t>(LoadGuestU32(base, record + 0x24u))) : -1;
}

}  // namespace refii::fable3

/// CSimpleOnlineOptionsBox::Show(box) without its presentation: the setting it starts on, stated.
REX_EXTERN(rex_OnlineOptionsBox_Show) {
  static std::atomic<bool> said{false};
  Said(said, "join / visibility options: Show and Update replaced (stated, no presentation made)");
  const uint32_t box = ctx.r3.u32;
  base[box + kBoxShown] = 1u;
  CallBox(ctx, base, rex_DisplayBox_Shown, box);
  const uint32_t entity = LiveEntity(base, box + kOnlineEntityRef);
  if (entity == 0u) {
    ctx.r3.u64 = 0u;
    return;
  }
  const uint32_t type = LoadGuestU32(base, box + kOnlineType);
  const uint32_t player = seam::Component(base, entity, kComponentPlayer);
  if (type == kTypeJoin) {
    const uint32_t mode = player != 0u ? LoadGuestU32(base, player + kPlayerJoinMode) : 0u;
    seam::StoreU32(base, box + kOnlineRow, mode < 3u ? mode + 1u : 1u);
  } else if (type == kTypeVisibility && player != 0u) {
    const uint32_t value = LoadGuestU32(base, player + kPlayerVisibility);
    seam::StoreU32(base, box + kOnlineRow, value == 0u ? 3u : value == 1u ? 2u : 1u);
  }
  const int32_t stored = static_cast<int32_t>(LoadGuestU32(base, box + kOnlineRow));
  const uint32_t row = static_cast<uint32_t>(stored < 1 ? 1 : stored > 3 ? 3 : stored);
  const uint32_t table = type == kTypeJoin ? static_cast<uint32_t>(kJoinTags)
                         : type == kTypeVisibility ? static_cast<uint32_t>(kVisibilityTags)
                                                    : 0u;
  Statement("@Screen.Online")
      .Add(Text(Hex(box)))
      .Add(Number(static_cast<float>(type)))
      .Add(Number(static_cast<float>(row)))
      .Add(Text(Hex(table != 0u ? LoadGuestU32(base, table + 4u * (row - 1u)) : 0u)))
      .Post();
  HideHudRoot(ctx, base);
  ctx.r3.u64 = 1u;
}

/// CSimpleOnlineOptionsBox::Update(box) without the hero's name and picture: the answer `+12`.
REX_EXTERN(rex_OnlineOptionsBox_Update) {
  static std::atomic<bool> said{false};
  Said(said, "join / visibility options: its Update ran replaced (no name or picture)");
  ctx.r3.u64 = base[ctx.r3.u32 + kBoxShown];
}

/// The friends channel's Show(box) without its presentation: whether its invite row takes A, stated.
REX_EXTERN(rex_FriendsChannelBox_Show) {
  static std::atomic<bool> said{false};
  Said(said, "friends channel: Show and Update replaced (stated, no presentation made)");
  const uint32_t box = ctx.r3.u32;
  const bool tu3 = refii::guest::Current() == refii::guest::Build::kTu3;
  base[box + kBoxShown] = 1u;
  CallBox(ctx, base, rex_DisplayBox_Shown, box);
  seam::StoreU32(base, box + kFriendsRow, 1u);
  if (tu3) {
    base[box + kFriendsInvite] = 1u;
  }
  if (LiveEntity(base, box + kFriendsEntityRef) == 0u) {
    ctx.r3.u64 = 0u;
    return;
  }
  if (tu3) {
    PPCContext work = seam::Frame(ctx);
    rex_Network_IsInLiveGame(work, base);
    if ((work.r3.u32 & 0xFFu) != 0u) {
      base[box + kFriendsInvite] = 0u;
    }
  }
  Statement("@Screen.Friends")
      .Add(Text(Hex(box)))
      .Add(Boolean(!tu3 || base[box + kFriendsInvite] != 0u))
      .Post();
  HideHudRoot(ctx, base);
  base[box + FriendsOpen()] = 1u;
  ctx.r3.u64 = 1u;
}

/// The friends channel's Update(box) without the hero's name and picture: its open byte.
REX_EXTERN(rex_FriendsChannelBox_Update) {
  static std::atomic<bool> said{false};
  Said(said, "friends channel: its Update ran replaced (no name or picture)");
  ctx.r3.u64 = base[ctx.r3.u32 + FriendsOpen()];
}

REX_EXTERN(rex_JoinOptionsBox_HandleInput) {
  static std::atomic<bool> said{false};
  Said(said, "join / visibility options: its pad is Godot's (rex_JoinOptionsBox_HandleInput gated)");
  GateOwnedInput(ctx, base, __imp__rex_JoinOptionsBox_HandleInput);
}

REX_EXTERN(rex_FriendsChannelBox_HandleInput) {
  static std::atomic<bool> said{false};
  Said(said, "friends channel: its pad is Godot's (rex_FriendsChannelBox_HandleInput gated)");
  GateOwnedInput(ctx, base, __imp__rex_FriendsChannelBox_HandleInput);
}

REX_EXTERN(rex_PartnersBox_HandleInput) {
  static std::atomic<bool> said{false};
  Said(said, "co-op partners: its pad is Godot's (rex_PartnersBox_HandleInput gated)");
  GateOwnedInput(ctx, base, __imp__rex_PartnersBox_HandleInput);
}

namespace refii::fable3 {

bool AnswerSessionBox(PPCContext& ctx, uint8_t* base, uint32_t box, uint32_t vtable, const std::string& verb,
                      const std::string& value, uint32_t player, const char* action) {
  auto store_word = [base](uint32_t at, uint32_t word) { seam::StoreU32(base, at, word); };
  bool ok = false;
  switch (refii::guest::ToTu3(vtable)) {
    case kVtOnlineOptions:
      if (verb == "close") {
        RunOwnedHandler(ctx, base, rex_JoinOptionsBox_HandleInput, box, kActionBPressed, player);
        return true;
      }
      if (verb == "move") {
        const uint32_t row = ToInt(value, ok);
        const int64_t pad = BoxOwnerPad(base, box + kOnlineEntityRef);
        if (!ok || row < 1u || row > 3u || base[box + kBoxShown] == 0u ||
            (pad != kNoOwnerTest && pad != static_cast<int64_t>(player))) {
          REXLOG_INFO("[fable3-boxes] answer '{}': not shown, not this box's player, or no such row; refused", action);
          return false;
        }
        Move(ctx, base, box, kOnlineRow, row);
        return true;
      }
      if (verb == "choose") {
        const uint32_t row = ToInt(value, ok);
        if (!ok || row < 1u || row > 3u) {
          return false;
        }
        return StoreAndRun(ctx, base, rex_JoinOptionsBox_HandleInput, box, kOnlineRow, row, kActionAPressed, player,
                           action, store_word);
      }
      return false;
    case kVtFriendsChannel:
      if (verb == "close") {
        RunOwnedHandler(ctx, base, rex_FriendsChannelBox_HandleInput, box, kActionBPressed, player);
        return true;
      }
      if (verb == "row") {
        const uint32_t row = ToInt(value, ok);
        if (!ok || row < 1u || row > 3u) {
          return false;
        }
        return StoreAndRun(ctx, base, rex_FriendsChannelBox_HandleInput, box, kFriendsRow, row, kActionAPressed,
                           player, action, store_word);
      }
      if (verb == "move") {
        const uint32_t row = ToInt(value, ok);
        const int64_t pad = BoxOwnerPad(base, box + kFriendsEntityRef);
        if (!ok || row < 1u || row > 3u || base[box + kBoxShown] == 0u || pad != static_cast<int64_t>(player)) {
          REXLOG_INFO("[fable3-boxes] answer '{}': not shown, not this box's player, or no such row; refused", action);
          return false;
        }
        Move(ctx, base, box, kFriendsRow, row);
        return true;
      }
      return false;
    case kVtPartners: {
      if (verb == "close") {
        RunOwnedHandler(ctx, base, rex_PartnersBox_HandleInput, box, kActionBPressed, player);
        return true;
      }
      const uint32_t code = verb == "join"      ? kActionAPressed
                            : verb == "card"    ? kActionYPressed
                            : verb == "divorce" ? kActionXPressed
                                                : 0u;
      if (code == 0u && verb != "row") {
        return false;
      }
      const uint32_t index = ToInt(value, ok);
      const uint32_t records =
          (LoadGuestU32(base, box + kPartnersListEnd) - LoadGuestU32(base, box + kPartnersListBegin)) / kPartnersRecord;
      // The handler's own test, made before anything is stored: shown, and this box's player.
      if (!ok || base[box + kBoxShown] == 0u || LoadGuestU32(base, box + kPartnersPad) != player || index >= records) {
        REXLOG_INFO("[fable3-boxes] answer '{}': not this box's player, or no such record of {}; refused", action,
                    records);
        return false;
      }
      if (code == 0u) {
        PlayGuestCue(ctx, base, kCueStep);
      }
      const uint32_t cursor = index < kPartnersWindowLast ? index : kPartnersWindowLast;
      seam::StoreU32(base, box + kPartnersCursor, cursor);
      seam::StoreU32(base, box + kPartnersScroll, index - cursor);
      CallBox(ctx, base, rex_PartnersBox_Rows, box);
      Statement("@Screen.Cursor").Add(Text(Hex(box))).Add(Number(static_cast<float>(index))).Post();
      if (code != 0u) {
        RunOwnedHandler(ctx, base, rex_PartnersBox_HandleInput, box, code, player);
      }
      return true;
    }
    default:
      return false;
  }
}

}  // namespace refii::fable3
