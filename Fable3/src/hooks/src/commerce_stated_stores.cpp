// refii - the shop's and the pawn shop's field stores that no call states, stated at the store
// instruction itself (a [[midasm_hook]] on each store, the value register and the object it is
// stored into, before the store runs): the shop mode's state and its "stealing allowed", the
// pawn-shop mode's state and its "stealing allowed", and the pawn shop's display page and category.
// Every site of each field in the bodies that write it, both builds (an instruction scan of the
// mode's method range: TU3 0x8295AF00..0x82963000 / disc 0x82B2E000..0x82B36000 for the shop,
// TU3 0x82B6B600..0x82B72000 / disc 0x82B36180..0x82B3D000 for the pawn shop; the same sites one to
// one on the two builds, in the same functions).
//
// THE SHOP MODE (CInteractionModeMenuShop, vtable TU3 0x820C2FA0 / disc 0x820D3500): state mode+0xF8
// / +0x140 (0 at the plinth, 1 browsing, 2 stealing or haggling, 3 just bought, 4 the weapon page):
//   ctor sub_8295B018 `stw r30,0xF8(r31)` 0x8295B088 / sub_82B2E0F8 `stw r30,0x140(r31)` 0x82B2E168;
//   re-point sub_8295B378 `stw r25` 0x8295B4A8, `stw r27` 0x8295B4C8 (r31) / 0x82B2E578, 0x82B2E598;
//   vf22 (the shop closing under the hero) `stw r11,0xF8(r28)` 0x8295D910 / 0x82B3086C;
//   vf7 `stw r28,0xF8(r29)` 0x8295DEE4, `stw r11` 0x8295DF7C, 0x8295E36C, 0x8295E3A0 /
//       0x82B30E28, 0x82B30EC0, 0x82B312B4, 0x82B312E8;
//   the purchase reaction sub_8295EF58 `stw r10,0xF8(r30)` 0x8295F09C / sub_82B31DE0 0x82B31F24;
//   the re-copy sub_8295F118 `stw r28,0xF8(r29)` 0x8295F2B0 / sub_82B31FA0 0x82B32138;
//   the steal's start sub_82960608 `stw r11,0xF8(r30)` 0x829608FC / sub_82B33310 0x82B335A0.
// Stealing allowed, mode+0x105 / +0x14D, written only by its recompute sub_82962540 / sub_82B35448
// (called by vf4 every tick in state 1): `stb r11,0x105(r28)` 0x82962714, `stb r5` 0x8296282C,
// `stb r8` 0x8296295C, `stb r9` 0x82962A4C / `stb r11,0x14D(r28)` 0x82B3561C, `stb r5` 0x82B35734,
// `stb r8` 0x82B35864, `stb r9` 0x82B35954.
//
// THE PAWN-SHOP MODE (vtable TU3 0x820D6050 / disc 0x820D36D0): state mode+0x110 / +0x158 (0 talking,
// 1 the list, 2 expression): ctor sub_82B6B608 `stw r30,0x110(r31)` 0x82B6B680 / sub_82B36188
// 0x82B36200; vf7 `stw r11,0x110(r28)` 0x82B6CEE4, 0x82B6CF4C / 0x82B3794C, 0x82B379B4; the re-copy
// sub_82B6DDD0 `stw r31,0x110(r30)` 0x82B6DDF0 / sub_82B38830 0x82B38850; the steal's start
// sub_82B6F7F8 `stw r11,0x110(r30)` 0x82B6FAC8 / sub_82B3A218 0x82B3A4D8. Stealing allowed
// mode+0x11A / +0x162: ctor `stb r30,0x11A(r31)` 0x82B6B6B8 / 0x82B3623C; its recompute
// sub_82B702C0 / sub_82B3ACC0 `stb r11,0x11A(r28)` 0x82B70494, `stb r5` 0x82B705AC, `stb r9`
// 0x82B7069C / `stb r11,0x162(r28)` 0x82B3AE94, `stb r5` 0x82B3AFAC, `stb r9` 0x82B3B09C.
//
// THE PAWN SHOP'S DISPLAY D = *(shop+0xE0) (`lwz r31,0xE0(r3)` of sub_821826E8's shop, TU3
// 0x82A46F80): page D+0x10 and category D+0x14, moved only by the pawn mode's bumpers and triggers:
//   page forward sub_82A46F68 `stw r11,0x10(r31)` 0x82A46FA4 (+1), 0x82A46FB4 (wrap to 0) /
//       sub_82A06DB0 0x82A06DEC, 0x82A06DFC; page back, inline in vf7, `stw r10,0x10(r31)` 0x82B6D7B0,
//       `stw r11` 0x82B6D7BC / 0x82B38210, 0x82B3821C;
//   category back sub_82922F30 `stw r11,0x14(r29)` 0x82922FC4, 0x82923014, `stw r28` 0x8292303C /
//       sub_828CA880 0x828CA914, 0x828CA964, 0x828CA98C; forward sub_82922DF8 `stw r11` 0x82922E90,
//       0x82922EE0, `stw r28` 0x82922F08 / sub_828CA748 0x828CA7E0, 0x828CA830, 0x828CA858.
//   (Each of the three functions has the pawn mode's vf7 as its one caller.)
//
// THE SHOP MODE'S CLOSE vf2 TU3 0x8295D0B8 / disc 0x82B30030 (slot 2 of the shop vtable alone, the
// words 0x820C2FA8 / 0x820D3508): the script call sub_82962400 / sub_82B35308, the player-mode
// interaction's end sub_82960068(mode, 1) / sub_82B32D70 and the NPC base close (0x8295D0D4..0x8295D0EC
// / 0x82B3004C..0x82B30064). Wrapped: `@Shop.Close(mode)` at its entry, then the guest's close.
//
// Stated on a change of the value per object: `@Shop.State(mode, n)`, `@Shop.StealAllowed(mode, on)`,
// `@Pawn.State(mode, n)`, `@Pawn.StealAllowed(mode, on)`, `@Pawn.Page(display, n)`,
// `@Pawn.Category(display, n)`.

#include <cstdint>
#include <mutex>
#include <unordered_map>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "gui_statements.h"

REX_EXTERN(__imp__rex_ShopMode_Close);

namespace {

using namespace refii::fable3;

enum Field : uint32_t { kShopState, kShopSteal, kPawnState, kPawnSteal, kPawnPage, kPawnCategory, kFields };

constexpr const char* kStatement[kFields] = {"@Shop.State", "@Shop.StealAllowed", "@Pawn.State",
                                             "@Pawn.StealAllowed", "@Pawn.Page", "@Pawn.Category"};
constexpr bool kBoolean[kFields] = {false, true, false, true, false, false};

std::mutex g_mutex;
std::unordered_map<uint64_t, uint32_t>* g_last = nullptr;
bool g_logged[kFields] = {};

void State(Field field, uint32_t object, uint32_t value) {
  if (kBoolean[field]) {
    value &= 0xFFu;
  }
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_last == nullptr) {
      g_last = new std::unordered_map<uint64_t, uint32_t>();
    }
    const uint64_t key = (static_cast<uint64_t>(field) << 32) | object;
    auto it = g_last->find(key);
    if (it != g_last->end() && it->second == value) {
      return;
    }
    (*g_last)[key] = value;
    if (!g_logged[field]) {
      g_logged[field] = true;
      REXLOG_INFO("[fable3-commerce] {} stated at its store ({:08X} = {})", kStatement[field], object, value);
    }
  }
  Statement statement(kStatement[field]);
  statement.Add(Text(Hex(object)));
  if (kBoolean[field]) {
    statement.Add(Boolean(value != 0u));
  } else {
    statement.Add(Number(static_cast<float>(static_cast<int32_t>(value))));
  }
  statement.Post();
}

}  // namespace

/// [value, mode] at each store of the shop mode's state.
void RefiiF3ShopState(PPCRegister& value, PPCRegister& mode) { State(kShopState, mode.u32, value.u32); }

/// [value, mode] at each store of the shop mode's stealing-allowed byte.
void RefiiF3ShopStealAllowed(PPCRegister& value, PPCRegister& mode) { State(kShopSteal, mode.u32, value.u32); }

/// [value, mode] at each store of the pawn-shop mode's state.
void RefiiF3PawnState(PPCRegister& value, PPCRegister& mode) { State(kPawnState, mode.u32, value.u32); }

/// [value, mode] at each store of the pawn-shop mode's stealing-allowed byte.
void RefiiF3PawnStealAllowed(PPCRegister& value, PPCRegister& mode) { State(kPawnSteal, mode.u32, value.u32); }

/// [value, display] at each store of the pawn shop's display page.
void RefiiF3PawnPage(PPCRegister& value, PPCRegister& display) { State(kPawnPage, display.u32, value.u32); }

/// [value, display] at each store of the pawn shop's display category.
void RefiiF3PawnCategory(PPCRegister& value, PPCRegister& display) { State(kPawnCategory, display.u32, value.u32); }

/// The shop mode's vf2 (mode, a2): `@Shop.Close(mode)`; the values stored for it are forgotten.
REX_EXTERN(rex_ShopMode_Close) {
  static bool logged = false;
  const uint32_t mode = ctx.r3.u32;
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_last != nullptr) {
      g_last->erase((static_cast<uint64_t>(kShopState) << 32) | mode);
      g_last->erase((static_cast<uint64_t>(kShopSteal) << 32) | mode);
    }
    if (!logged) {
      logged = true;
      REXLOG_INFO("[fable3-commerce] the shop's close is stated");
    }
  }
  Statement("@Shop.Close").Add(Text(Hex(mode))).Post();
  __imp__rex_ShopMode_Close(ctx, base);
}
