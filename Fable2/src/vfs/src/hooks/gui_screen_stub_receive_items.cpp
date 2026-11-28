// THE PORTED SCREENS (S7). Each handler slot below is a full replacement: it keeps the guest's
// statements of the screen's data and its game side effects, as direct calls, and drops what
// only draws or animates. Godot drives the rest through the levers at the end of each file
// (guest_tick_pump.cpp dispatches them). The handler objects stay registered.
//
// Slot layout (vtable): +8 OnEnter, +16 Update, +20 OnMenuEvent(h, event), +24
// OnScreenAction(h, code, player), +36 OnExit. TU1 / disc addresses on each function.
#include <algorithm>
#include <cctype>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <initializer_list>
#include <string>

#include <rex/logging.h>

#include "core/guest_build.h"
#include "core/mod_values.h"
#include "core/refii_mod.h"
#include "core/refii_mod_events.h"
#include "hooks/gui_screen_stub_support.h"
#include "hooks/gui_screen_stubs.h"
#include "hooks/ppc_externs.h"
#include "vfs_hooks_private.h"


using namespace refii::vfs::stub;
using refii::guest::Address;
using refii::mods::LoadU32;
using refii::mods::StoreU32;

// ---- THE RECEIVED-ITEM CARD (8 on both builds) — vtable 0x820C0E08 / 0x820AEE60 --------------
//
// The card is pushed by five openers, all the guest's: an item `sub_823C4340` (SetScreen(8),
// SetActiveScreen, `CGUIReceiveItems_SetItem`, EnterGuiMode), the co-op partner's item `sub_823C4408`
// (`SetPeerItem`), money `sub_823C44E8`, a discount `sub_823C45E8` and "work harder" `sub_823C46B0`
// (disc 0x824F9570 / 0x824F9638 / 0x824F9718 / 0x824F9818 / 0x824F98E0). Each fills the handler —
// +52 the side (1 front), +56 the icon's resource hash, +60 / +64 the title and description text keys,
// +68 the extra line's key and +72 its amount (money, discount), +76 the NameAndIcon line, +80 "can be
// used now", +96 the 3D view (its +0 the record, +124 the model), +324 "the partner's card" — and leaves
// +81 = 0, "not drawn": SetItem and SetPeerItem then call the card's draw `sub_82538910(h)` themselves
// (0x8253824C / 0x82538348), the other three leave it to the first Update (0x82537058). That draw is the
// one call that presents every card, and it is where the card is stated.
//
// OnLeave (TU1 0x82536F70 / disc 0x82402710) runs on close and on replace, and states the card down.

namespace {

/// Host-only record kind; the host side names it Engine_UiReceiveItemsSurface.StateKind.
constexpr int32_t kCardKind = 1006;

/// The GUI owner stated for the open card (StateReceivedCardOwner): -1 none; else the owner before it, 1
/// player two, 0 player one, put back when the card leaves. Guest tick thread only.
int32_t g_card_owner_before = -1;

constexpr uint32_t kCardVtable = 0x820C0E08u;

/// The NameAndIcon line at `handle`: a guest string, two hops, NUL-terminated. Quotes, commas and line
/// breaks are dropped so the value stays one quoted token on the wire.
std::string NameLine(const uint8_t* base, uint32_t handle) {
  const uint32_t block = LoadU32(base, handle);
  const uint32_t chars = Plausible(block) ? LoadU32(base, block) : 0u;
  std::string out;
  for (uint32_t i = 0; Plausible(chars) && i < 128u; ++i) {
    const char c = static_cast<char>(base[chars + i]);
    if (c == 0) break;
    if (c == '\'' || c == ',' || c == '\n' || c == '\r') continue;
    out.push_back(c);
  }
  return out;
}

}  // namespace

// THE CARD'S DRAW, `sub_82538910(h)` (TU1 0x82538910 / disc 0x824040B0): the guest's presentation of
// the handler it is handed — the title `SetText(TextManager_GetString(h+60))` (0x82538984), the icon
// element's texture h+56 (0x825389F0), the description h+64 (0x82538A44), the line h+76 (0x82538A78),
// the use prompt's slide by h+80 on the side h+52 (0x82538AC0 / 0x82538B08), "GUI_DONE" on the partner's
// card h+324 (0x82538B70). Replaced in full: its argument is stated as the card record (kind 1006) and
// none of those elements is written. Kept: h+81 = 1 (0x82538BD0) and the scroll region's
// `sub_828C3358(h+24)` (0x82538BD8 / 0x82833D40).
REX_EXTERN(rex_CGUIReceiveItems_DrawCard) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const uint32_t preview = h + 96u;
  // THE AMOUNT IS THE EXTRA LINE'S. +72 is that line's amount, written with the line's key at
  // +68 (`stw r29,0x48(r31)` / `stw r11,0x44(r31)` in the money fill `sub_825383D0`, TU1 0x82538494 / 0x82538498,
  // disc `sub_82403B70`); an item's SetItem writes +68 the empty text (`stw r10,0x44(r29)` TU1 0x82538188 / disc
  // 0x82403928, the key of "" — FNV-1's basis 0x811C9DC5)
  // and leaves +72 as the last money card left it, and the card's own draw never reads +72. So it is stated only
  // under a line of its own: a gold card's 150 no longer rides on the hat card after it.
  const uint32_t extra = LoadU32(base, h + 68u);
  const uint32_t amount = extra != 0u && extra != 0x811C9DC5u ? LoadU32(base, h + 72u) : 0u;
  char fields[512];
  std::snprintf(fields, sizeof(fields),
                "up=1,peer=%u,front=1,usable=%u,record=%u,model=%u,icon=%u,title=%u,desc=%u,extra=%u,amount=%u,"
                "name='%s'",
                base[h + 324u] != 0u ? 1u : 0u, base[h + 80u] != 0u ? 1u : 0u, LoadU32(base, preview),
                LoadU32(base, preview + 124u), LoadU32(base, h + 56u), LoadU32(base, h + 60u),
                LoadU32(base, h + 64u), extra, amount,
                NameLine(base, h + 76u).c_str());
  refii::mods::PostEvent(kCardKind, fields);
  base[h + 81u] = 1u;
  const Scratch s = Claim(ctx);
  Call(ctx, base, s.frame, 0x828C3358u, {h + 24u});
  REXLOG_INFO("Refii screen stub: received item (8) — the card stated ({})", fields);
}

// OnEnter TU1 0x82536E68 / disc 0x82402608. Kept: the description scroll region's reset
// `sub_828C2F38(h+24)` and bar `sub_828C33F0(h+24, 0x834986BC)` (0x82536ED0 / 0x82536EE0; disc 0x82833920 /
// 0x82833DD8 with 0x83497A2C), the region the kept draw and OnLeave act on; h+52 = 1, the front
// (0x82536EE4); the dismiss gate `sub_828C36D0(h+248, 1.0)` (0x82536F60 / 0x82834498: one second before
// Done is taken); h+324 = 0 (0x82536F64). Dropped: the spin's state h+284..h+301 and its idle timer
// (0x82536E94..0x82536EB0), the open sound vf+4 (0x82536EC4), the root's slide 1 (0x82536F48), the flip
// timer h+224 (0x82536F54).
REX_EXTERN(rex_lhGuiReceiveItemsScreen_OnEnter) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  Call(ctx, base, s.frame, 0x828C2F38u, {h + 24u});
  Call(ctx, base, s.frame, 0x828C33F0u, {h + 24u, Address(0x834986BCu)});
  base[h + 52u] = 1u;
  {
    PPCContext arm = ctx;
    arm.r1.u32 = s.frame;
    arm.r3.u32 = h + 248u;
    arm.f1.f64 = 1.0;
    refii::mods::CallGuest(arm, base, Address(0x828C36D0u));
  }
  base[h + 324u] = 0u;
  Said("received item (8)", "OnEnter", h);
}

// Update TU1 0x82536FB8 / disc 0x82402758. Kept: the first draw — h+81 clear → h+81 = 1 and
// `sub_82538910(h)` (0x82537050 / 0x82537058), which is what states the money, discount and work-harder
// cards (their openers never call SetItem). Dropped: the spin and the anchor's turn (0x82536FD0..
// 0x82537028), the child tick `sub_826D5D00` (0x82537030), the scroll tick `sub_828C2F98` (0x82537038), the
// redraw after a flip on the timer h+236 (0x82537060..0x82537090), the 3D view's draw `sub_828C3C78`
// (0x825370E4) and the flip timer's end (0x825370F0).
REX_EXTERN(rex_lhGuiReceiveItemsScreen_Update) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  if (base[h + 81u] == 0u) {
    base[h + 81u] = 1u;
    PPCContext draw = ctx;
    draw.r1.u32 = Claim(ctx).frame;
    draw.r3.u32 = h;
    rex_CGUIReceiveItems_DrawCard(draw, base);
  }
}

// OnScreenAction TU1 0x82537128 / disc 0x824028C8 (h, code, player): 27 the spin, the scroll input
// `sub_828C30A8`, 39 the flip (slide, sound, h+52 toggled, redraw) — Godot turns its own card; 36 Done
// and 75 Equip now are the levers `Card:<p>:dismiss|equip` (DispatchCard). Nothing kept.
REX_EXTERN(rex_lhGuiReceiveItemsScreen_OnScreenAction) {
  REX_FUNC_PROLOGUE();
  Said("received item (8)", "OnScreenAction", ctx.r3.u32, ctx.r4.u32, ctx.r5.u32);
}

// OnLeave (+36) TU1 0x82536F70 / disc 0x82402710, kept whole in the guest's order: the base exit
// `sub_826D5C30(h)` (0x82536F84 / 0x82583490); the scroll region's nodes freed `sub_827A0E18(h+32)`
// (0x82536F8C / 0x828581B0); the preview's release `sub_828C3FD8(h+96)` (0x82536F94 / 0x82834DA0); h+325
// = 0 (0x82536F9C). Then the card stated down — on close, and on replace before the next item's card.
REX_EXTERN(rex_CGUIReceiveItems_OnLeave) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  Call(ctx, base, s.frame, 0x826D5C30u, {h});
  Call(ctx, base, s.frame, 0x827A0E18u, {h + 32u});
  Call(ctx, base, s.frame, 0x828C3FD8u, {h + 96u});
  base[h + 325u] = 0u;
  refii::mods::PostEvent(kCardKind, "up=0");
  Said("received item (8)", "OnLeave", h);
  if (g_card_owner_before >= 0) {
    const bool before_two = g_card_owner_before == 1;
    g_card_owner_before = -1;
    StateGuiOwner(ctx, base, s.frame, before_two);
    REXLOG_INFO("Refii screen stub: received item (8) — the card left; the GUI's owner is player {} again",
                before_two ? 2 : 1);
  }
}

// ---- THE LEVERS -----------------------------------------------------------------------------

namespace refii::vfs {

// guest_tick_pump.cpp: this machine is an online joiner; local player two owns the GUI; the inventory action
// player two's book posts (`Inventory:<op>:<record>`, acted on the hero whose menus are open).
bool IsCoopJoiner(uint8_t* base);
bool MenusActForPlayerTwo(uint8_t* base);
bool DispatchInventoryAction(PPCContext& ctx, uint8_t* base, const std::string& body, int32_t addressed,
                             uint32_t ticket);

// THE CARD IS ITS RECEIVER'S. The item card's opener `sub_823C4340` (TU1 0x823C4340 / disc 0x824F9570) states
// no GUI owner — SetScreen(8), SetActiveScreen, SetItem, EnterGuiMode (0x823C4378..0x823C43AC) — so the card
// inherits the last statement and the GUI tick serves that player's input. The core Lua states its receiver
// right after GUI.DisplayReceivedItem opened it (Refii._received_card_owner: the hero whose inventory holds the
// item), through the setter every world screen's opener uses (StateGuiOwner), and the owner before it is put
// back when the card leaves (OnLeave, above). A card replaced by the next one hands the first statement on.
void StateReceivedCardOwner(PPCContext& ctx, uint8_t* base, bool player_two) {
  if (g_card_owner_before < 0) {
    g_card_owner_before = StatedPausePlayerTwo() ? 1 : 0;
  }
  const Scratch s = Claim(ctx);
  StateGuiOwner(ctx, base, s.frame, player_two);
  REXLOG_INFO("Refii screen stub: received item (8) — the card is player {}'s (its receiver)", player_two ? 2 : 1);
}

bool DispatchCard(PPCContext& ctx, uint8_t* base, uint32_t player, const std::string& verb) {
  // The handler's two closing arms (`sub_82537128`): 36 Done, behind the one-second gate
  // `sub_828C3748(h+248)` (0x82537400 / disc 0x82834510), and 75 Equip now, behind h+80 alone
  // (0x825371D8) — `sub_8246FFF0(*(h+96))` (0x825371E8 / 0x8283BB90: the record found in the local hero's
  // inventory and activated by category). Both end in the dismiss `sub_82538BF0(h, 1)` (0x825374D4 /
  // 0x825372B0; disc 0x82404390): `ExitScreen(QM, 1)`, the preview's Hide, the host's close packet to the
  // partner, a joiner's own-press pause. Dropped: the root's slide and the sounds around it.
  const Scratch s = Claim(ctx);
  const uint32_t h = ScreenHandler(ctx, base, s.frame, 8u, kCardVtable);
  if (h == 0u) {
    REXLOG_INFO("Refii guest-tick pump: Card:{}:{} — no card handler, dropped", player, verb);
    return true;
  }
  if (verb == "dismiss") {
    if ((Call(ctx, base, s.frame, 0x828C3748u, {h + 248u}) & 0xFFu) == 0u) {
      REXLOG_INFO("Refii guest-tick pump: Card:{}:dismiss — inside the card's first second, refused", player);
      return true;
    }
    Call(ctx, base, s.frame, 0x82538BF0u, {h, 1u});
    REXLOG_INFO("Refii guest-tick pump: Card:{}:dismiss — the card dismissed (sub_82538BF0)", player);
    return true;
  }
  if (verb == "equip") {
    if (base[h + 80u] == 0u) {
      REXLOG_INFO("Refii guest-tick pump: Card:{}:equip — the card's item cannot be used now, refused", player);
      return true;
    }
    const uint32_t record = LoadU32(base, h + 96u);
    // EQUIP NOW FOR THE CARD'S OWN PLAYER. `sub_8246FFF0(record)` (disc `sub_8283BB90`, the same 140
    // instructions) looks the record up in the LOCAL player's inventory — the pick inlined, `lbz r8,0x34` /
    // `lbz r11,0x35` of the net manager, `li r4,1`, `li r4,2` when not both, `bl sub_82264230` (TU1
    // 0x82470030..0x82470068 / disc 0x8283BBD0..0x8283BC08), the record's hero, its component 13,
    // `CECInventory_FindItemByRecordHash` (0x824701B4) — and hands the item to
    // `lhGuiInventory_ActivateItemByCategory(G+52, item, 1)` (0x82470200 / disc 0x825A5A40), which reads the
    // item's InventoryItemComponent "Category" and routes it: 1 to the pending weapon pick `sub_826CA098`, 2..6 /
    // 16 / 24 / 26 to the use `sub_826CA170`, 9..13 / 17..23 to the wear `sub_826CB218(…, 0)`. Each of those
    // takes a player of its own: the use `lhEntity_GetModel(players[0])` — the hero-one slot, list+0x68 / +4
    // (`addi r3,r3,0x68 ; lwz r11,4(r3)` lhEntity_GetModel TU1 0x8222C1C4 / disc 0x8222C21C; the use's read
    // 0x826CA198..0x826CA1AC / disc 0x825A6120..0x825A6134); the wear `sub_8229EE78(sub_821C7CE0(G))`
    // (0x826CB24C / 0x826CB380); the weapon only marks GUI+52's slots (0x826CA134 / 0x826CA160), and the
    // card's own dismiss (`ExitScreen`) applies them to `lhEntity_GetModel(players[0])` again (0x8236EE98..
    // 0x8236EEB0 / disc 0x82337098..0x823370B0).
    // Player two's card — local player two owning the GUI (the card's owner, StateReceivedCardOwner), or this
    // machine an online joiner (its own card, Refii._received_item):
    //   * a WEAPON takes the road player two's own book takes — local player two: `Inventory:equip:<record>`
    //     (EquipRecord on their hero, DispatchInventoryAction's direct path); an online joiner: the co-op kit's
    //     pick, which the host arms (below). The guest's pending pick lands on the hero-one slot at the dismiss;
    //   * anything else runs the guest's own Equip now under OwnerScope: in local co-op "the local player"
    //     answers player two (the inline pick above, RefiiCoopInlineLocalPlayer at 0x82470068 / 0x8283BC08;
    //     `sub_821C7CE0`'s own, RefiiCoopShopPlayer) and the hero-one slot holds hero two (ListedHeroScope);
    //     a joiner's picks already answer its own hero (not the session host: `li r4,2`) and the slot is lent.
    const bool joiner = IsCoopJoiner(base);
    const bool local_two = !joiner && refii_mod_coop_mode(base) == 1 && MenusActForPlayerTwo(base);
    if (joiner || local_two) {
      uint32_t component = 0u;
      int32_t category = -1;
      const bool known = refii_mod_record_ref(&ctx, base, record, "InventoryItemComponent", &component) != 0 &&
                         component != 0u && refii_mod_record_int(&ctx, base, component, "Category", &category) != 0;
      if (known && category == 1 && joiner) {
        // AN ONLINE JOINER'S WEAPON IS THE HOST'S TO ARM. The joiner's book never equips on the joiner: its
        // `Inventory:equip:` becomes the co-op kit's pick (Fable2UiVerbs, CoopHeroKit.SetLocalWeapon), the host
        // arms hero two from the kit (ApplyEquip → EquipRecord; "a joiner never instantiates") and the joiner
        // attaches the host's replica by net id. An item drawn HERE instead is a joiner-local entity whose net id
        // the host cannot resolve: its attach message (`ld r4,0x48(r31)` → `lhNetManager_FindEntityByNetId` →
        // `CECHeldItems_AttachItemToSlot`, TU1 0x824FEC6C..0x824FECCC / disc 0x82504434..0x82504494) is refused on
        // the host (coop_player_hooks.cpp), and player two shows unarmed there. So the card's pick takes the
        // book's road — the same event the quick slots post on a joiner (PerformQuickslot kind 3).
        char fields[64];
        std::snprintf(fields, sizeof(fields), "player=2,op='weapon',record='0x%08x'", record);
        refii::mods::PostEvent(REFII_MOD_HOST_HERO_KIND, fields);
        Call(ctx, base, s.frame, 0x82538BF0u, {h, 1u});
        REXLOG_INFO("Refii guest-tick pump: Card:{}:equip — this joiner's card: weapon 0x{:08X} goes to the co-op kit; "
                    "the host arms player two with it, the card dismissed", player, record);
        return true;
      }
      if (known && category == 1) {
        DispatchInventoryAction(ctx, base, "equip:" + std::to_string(record), 0, 0u);
        Call(ctx, base, s.frame, 0x82538BF0u, {h, 1u});
        REXLOG_INFO("Refii guest-tick pump: Card:{}:equip — player two's card (local): weapon 0x{:08X} drawn by "
                    "player two's own equip, the card dismissed", player, record);
        return true;
      }
      {
        OwnerScope scope(base, 8u);
        Call(ctx, base, s.frame, 0x8246FFF0u, {record});
        Call(ctx, base, s.frame, 0x82538BF0u, {h, 1u});
      }
      REXLOG_INFO("Refii guest-tick pump: Card:{}:equip — player two's card ({}): record 0x{:08X} (category {}) "
                  "activated for player two's hero, the card dismissed", player, joiner ? "this joiner's" : "local",
                  record, category);
      return true;
    }
    Call(ctx, base, s.frame, 0x8246FFF0u, {record});
    Call(ctx, base, s.frame, 0x82538BF0u, {h, 1u});
    REXLOG_INFO("Refii guest-tick pump: Card:{}:equip — record 0x{:08X} activated, the card dismissed", player,
                record);
    return true;
  }
  REXLOG_INFO("Refii guest-tick pump: Card:{}:{} is not dismiss or equip — dropped", player, verb);
  return true;
}

}  // namespace refii::vfs
