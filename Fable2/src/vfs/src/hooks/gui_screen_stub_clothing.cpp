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
#include <cstring>
#include <initializer_list>
#include <string>

#include <rex/logging.h>

#include "core/guest_build.h"
#include "core/guest_state.h"
#include "core/refii_mod.h"
#include "hooks/gui_screen_stub_support.h"
#include "hooks/gui_screen_stubs.h"
#include "hooks/ppc_externs.h"
#include "vfs_hooks_private.h"

using namespace refii::vfs::stub;
using refii::guest::Address;
using refii::mods::LoadU32;
using refii::mods::StoreU32;

// ---- CLOTHING (16; disc 15) — vtable 0x820CFEDC / 0x820C027C --------------------------------
//
// h+4 the owner, h+24 the row list (vt 0x820EE0D4 / 0x820E1D88: +4 populate `sub_829B1E78` /
// `sub_828CAF98`), h+44 the mannequin's preview-scene record id (-1: none), h+48 the book and h+72 its
// page stack, h+89 "details dirty", pages h+92 (description) / h+188 (stats), h+220 the mode (4 clothing;
// 5 dye mode, entered only by the items screen's events 80 / 81, which Godot never sends: its dye flow is
// its own), h+244 the current item handle, h+292 the last highlighted row id. Same offsets on both builds.
//
// THE MANNEQUIN IS A PREVIEW BODY AND NOTHING ELSE. The guest's handlers apply every change twice, to
// the player and to the mannequin (`sub_826D3608` 0x826D3800 / 0x826D39CC, `sub_826D3F40` 0x826D40E8 /
// 0x826D40F8), and its wear handler decides "already worn" on the MANNEQUIN's component 15 (r26 =
// `sub_826D8008` 0x826D362C, `IsWearingItem` 0x826D374C): a remove that reached only the player left the
// next wear of that piece refused. Here every decision is taken on the PLAYER's component 15 and every
// consequence is applied to the player alone. The mannequin calls that are kept, none of which is read
// back by a lever: its spawn `sub_826D7650` (OnEnter below), its destroy `sub_826D7E90` (the guest's own
// OnExit, TU1 0x826D2DF8 / disc 0x8257E890, and B at the list's root, DispatchPauseBack), and the try-on
// that dresses it for the highlighted outfit (`Inventory:tryon`, guest_tick_pump.cpp TryOnMannequin).
// Godot draws it through the entity feed. OnModalAnswer (0x826D5480 / 0x82580F18) stays the guest's.

namespace {

constexpr uint32_t kClothingVtable = 0x820CFEDCu;
constexpr uint32_t kCurrentItem = 0xF4u;  // h+244

bool g_clothing_update_said = false;

/// The top of the book's page stack, `*sub_826C9388(h+72)` (`lwz r11, 8(r3)` / `addi r3, r11, -4`,
/// 0x826C9388 / 0x826C93D0): 0 on the description page, the only page the hood toggle runs on.
uint32_t PageTop(uint8_t* base, uint32_t h) {
  const uint32_t begin = LoadU32(base, h + 0x48u + 4u);
  const uint32_t end = LoadU32(base, h + 0x48u + 8u);
  return Plausible(begin) && end > begin ? LoadU32(base, end - 4u) : 0u;
}

/// The two page fills every consequence handler ends with (`sub_826D3608` 0x826D39FC / 0x826D3A04):
/// the description `sub_829B6310(h+92)` / 0x828D0220 and the stats `sub_829B7368(h+188)` / 0x828D1208.
void FillPages(PPCContext& ctx, uint8_t* base, uint32_t frame, uint32_t h) {
  Call(ctx, base, frame, 0x829B6310u, {h + 0x5Cu});
  Call(ctx, base, frame, 0x829B7368u, {h + 0xBCu});
}

/// The row's item handle for `record`. Player one's is the handler's own cache h+244, filled by the
/// Update's lookup for the row's ticket (`sub_82880BC0(*(QM+60)+4, ticket, h+244)` 0x826D2EDC / disc
/// `sub_827BE1E8`): Godot's ticket when the populate filed it for this record, else the record's first
/// row. Player two's hero, and a record on no row, is found in that hero's own inventory
/// (`CECInventory_FindItemByRecordHash(&handle, comp13, record)` 0x824F1C90 / 0x8261B978) into `own`,
/// which the caller releases (`lhTrackedObject_RemoveHandleRef` 0x82764DC0 / 0x82741BD8). 0 when the hero
/// does not hold it.
uint32_t RowItem(PPCContext& ctx, uint8_t* base, uint32_t frame, uint32_t h, uint32_t player, uint32_t hero,
                 uint32_t ticket, uint32_t record, uint32_t own, bool* owned) {
  *owned = false;
  if (player == 1u) {
    if (ticket == 0u || refii::vfs::MenuRecordForTicket(ticket) != record) {
      ticket = refii::vfs::MenuTicketForRecord(record);
    }
    if (ticket != 0u) {
      const uint32_t qm = LoadU32(base, Gui(base) + 28u);
      Call(ctx, base, frame, 0x82880BC0u, {LoadU32(base, qm + 60u) + 4u, ticket, h + kCurrentItem});
      const uint32_t item = LoadU32(base, h + kCurrentItem);
      if (Plausible(item) && LoadU32(base, item + 4u) == record) {
        return h + kCurrentItem;
      }
    }
  }
  const uint32_t inventory = refii_mod_component(base, hero, 13u);
  if (inventory == 0u) {
    return 0u;
  }
  for (uint32_t word = 0u; word < 16u; word += 4u) {
    StoreU32(base, own + word, 0u);
  }
  Call(ctx, base, frame, 0x824F1C90u, {own, inventory, record});
  if (LoadU32(base, own) == 0u) {
    return 0u;
  }
  *owned = true;
  return own;
}

void ReleaseOwned(PPCContext& ctx, uint8_t* base, uint32_t frame, uint32_t handle, bool owned) {
  if (owned && LoadU32(base, handle) != 0u) {
    Call(ctx, base, frame, 0x82764DC0u, {LoadU32(base, handle), handle});
  }
}

}  // namespace

namespace refii::vfs {

// OnEnter TU1 0x826D2B88 / disc 0x8257E5C0, inside the co-op wrapper that lends hero two
// (coop_hero_hooks.cpp). Kept, in the guest's order: the two pages pushed (`sub_82B49750` / 0x8228A900)
// and the book `sub_82990610(h+48, &pages, 0x8349B298, pres, 1)` (0x826D2C08 / 0x828CE060 with
// 0x834988F4), which runs each page's +4; the mannequin — its anchor element (`sub_821F0240` +
// `sub_82426798` on 0x8349B290, 0x826D2C24 / 0x826D2C2C; disc 0x834988EC), hero one's model
// (`lhEntity_GetModel` on the roster, 0x826D2C4C) and the spawn `sub_826D7650(SCENE, hero, anchor)`
// (0x826D2C68 / disc 0x823FD238), filed as the "clothing" figure; its record's +12 = pres and h+44 = its
// id +16, or -1 when none was made (0x826D2C74..0x826D2C84); the
// header `lhGuiPresentation_SetStringParam(*(G+40), "String1", localised "GUI_EQUIPMENT_SCREEN_CLOTHING",
// pres)` (0x826D2CE0 / 0x8259D6C8; the text through `sub_8230E0E8` 0x826D2CC0 and `sub_82B3F898`
// 0x826D2CCC / 0x82B398A8); the gold text `sub_8246B5E8(pres, 0x8349B28C, 1)` (0x826D2D54 / 0x82837218
// with 0x834988E8); the rows, h+24 vf+4 (0x826D2D6C); the tab strip `sub_826B8348(*(G+76), 6)`
// (0x826D2DC4 / 0x8240B218); h+292 = -1. Dropped: the open sound vf+4 (0x826D2BB4); the two footer
// elements hidden by `sub_82426328` (0x826D2D30 / 0x826D2D40); the dye-mode arm (0x826D2D7C..0x826D2DB0).
void ClothingScreenOnEnter(PPCContext& ctx, uint8_t* base) {
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  const uint32_t pages = s.data;          // {?, begin, end, capacity}
  const uint32_t item = s.data + 0x10u;   // the page pointer handed to the push
  const uint32_t name = s.data + 0x20u;   // "String1"
  const uint32_t tag = s.data + 0x24u;    // the title's tag, consumed by the localiser
  const uint32_t wide = s.data + 0x28u;   // its localised text
  const uint32_t title = s.data + 0x2Cu;  // the text as a CCharString
  const uint32_t slot = s.data + 0x30u;   // a handle copy for the anchor's lookup
  const uint32_t g = Gui(base);
  const uint32_t pres = BookPresentation(base);
  g_clothing_update_said = false;

  for (uint32_t i = 0; i < 16u; i += 4u) {
    StoreU32(base, pages + i, 0u);
  }
  for (uint32_t page : {0x5Cu, 0xBCu}) {
    StoreU32(base, item, h + page);
    Call(ctx, base, s.frame, 0x82B49750u, {pages, item});
  }
  Call(ctx, base, s.frame, 0x82990610u, {h + 0x30u, pages, Address(0x8349B298u), pres, 1u});
  {
    const uint32_t anchor = Element(ctx, base, s.frame, slot, 0x8349B290u, pres);
    const uint32_t game = Game(base);
    const uint32_t holder = Plausible(game) ? LoadU32(base, game + 88u) : 0u;
    const uint32_t party = Plausible(holder) ? LoadU32(base, holder + 4u) : 0u;
    const uint32_t roster = Plausible(party) ? LoadU32(base, party) : 0u;
    const uint32_t hero = Call(ctx, base, s.frame, 0x8222C1B8u, {roster});  // lhEntity_GetModel
    const char* outer = refii::vfs::SetPreviewFigureRole("clothing");
    const uint32_t record = Call(ctx, base, s.frame, 0x826D7650u, {LoadU32(base, g + 8u), hero, anchor});
    refii::vfs::SetPreviewFigureRole(outer);
    if (record != 0u) {
      StoreU32(base, record + 12u, pres);
    }
    StoreU32(base, h + 0x2Cu, record != 0u ? LoadU32(base, record + 16u) : 0xFFFFFFFFu);
  }

  MakeString(ctx, base, s.frame, name, s.data + 0x40u, "String1");
  MakeString(ctx, base, s.frame, tag, s.data + 0x60u, "GUI_EQUIPMENT_SCREEN_CLOTHING");
  const uint32_t said = Call(ctx, base, s.frame, 0x8230E0E8u, {wide, tag});
  StoreU32(base, title, 0u);
  const uint32_t text = Call(ctx, base, s.frame, 0x82B3F898u, {title, said});
  Call(ctx, base, s.frame, 0x826B9510u, {LoadU32(base, g + 40u), name, text, pres});
  DropString(ctx, base, s.frame, title);
  Call(ctx, base, s.frame, 0x821C67F8u, {wide});
  DropString(ctx, base, s.frame, name);

  Call(ctx, base, s.frame, 0x8246B5E8u, {pres, Address(0x8349B28Cu), 1u});
  CallSlot(ctx, base, s.frame, h + 24u, 1u, {});
  Call(ctx, base, s.frame, 0x826B8348u, {LoadU32(base, g + 76u), 6u});
  StoreU32(base, h + 0x124u, 0xFFFFFFFFu);
  const uint32_t storage = LoadU32(base, pages + 4u);
  if (storage != 0u) {
    Call(ctx, base, s.frame, 0x8221BD38u, {storage});  // Render_FreeD3DObject
  }
  Said("clothing (16)", "OnEnter", h, LoadU32(base, h + 4u), LoadU32(base, h + 0xDCu));
}

// Update TU1 0x826D2F80 / disc 0x8257EA18, inside the same wrapper. Kept: the highlighted row
// (`lhGuiMenu_GetHighlightedItemData` 0x826D3000, answered natively from the row Godot states) and its
// edge `sub_826D2E48` / 0x8257E8E0 in clothing mode — on a new id, the current item handle
// `sub_82880BC0(*(QM+60)+4, id, h+244)` (0x826D2EDC / 0x827BE1E8), h+89 = that handle is set
// (0x826D2F1C / 0x826D2F50), the page fills `sub_829B6310(h+92)` and `sub_829B7368(h+188)` (0x826D2F58 /
// 0x826D2F60: the description, the stats, and the item's "viewed" byte `stb r25, 0(r27)` 0x829B67D8),
// h+292 = the id. Dropped: the child tick `sub_826D5D00` (0x826D2F90); the edge's page-stack return
// `sub_82990FD8` (0x826D2F10: Godot owns the page level) and its dye-mode arms, which dress the
// mannequin (`sub_826D5558`, `sub_826D31E0`, `sub_826D3358`); the delayed page refresh on the timer
// h+296 (0x826D3030); the book slide and tick `sub_82990A98` / `sub_82990730` (0x826D3074 / 0x826D307C);
// the Live photo-upload dialog h+264 (0x826D3094..0x826D30DC).
void ClothingScreenUpdate(PPCContext& ctx, uint8_t* base) {
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  uint32_t type = 0u;
  uint32_t id = 0u;
  HighlightedRow(ctx, base, s.frame, s.data, s.data + 0x40u, &type, &id);
  if (id != LoadU32(base, h + 0x124u)) {
    const uint32_t qm = LoadU32(base, Gui(base) + 28u);
    Call(ctx, base, s.frame, 0x82880BC0u, {LoadU32(base, qm + 60u) + 4u, id, h + kCurrentItem});
    const uint32_t item = LoadU32(base, h + kCurrentItem);
    base[h + 0x59u] = item != 0u ? 1u : 0u;
    const bool unviewed = Plausible(item) && base[item + 0x14u] == 0u;
    FillPages(ctx, base, s.frame, h);
    StoreU32(base, h + 0x124u, id);
    if (unviewed && base[item + 0x14u] != 0u) {
      refii_bridge_ui_data_changed();  // the fill marked the item viewed: Godot restates its row
    }
  }
  if (!g_clothing_update_said) {
    g_clothing_update_said = true;
    Said("clothing (16)", "Update", h, type, id);
  }
}

}  // namespace refii::vfs

// OnMenuEvent TU1 0x826D4D58 / disc 0x825807F0 (jump table 0x826D4DAC / 0x82580844, index type − 3).
// Every arm is a consequence Godot posts as a lever: 3 → `Clothing:<p>:wear` (the dog item's use, or the
// wear handler `sub_826D3608`); 89 → `Clothing:<p>:outfit` (`sub_826D3A10`); 91..97 →
// `Clothing:<p>:remove` (`sub_826D3F40`); 84 / 86 / 87 / 88 the dye `sub_826D3108(h, 0)`, which Godot's
// dye flow runs as `Inventory:dye`; 90 strip-all (`sub_826D4118`), for which Godot lists no row; 140 /
// 141 sounds. Nothing kept.
REX_EXTERN(rex_lhGuiClothingScreen_OnMenuEvent) {
  REX_FUNC_PROLOGUE();
  Said("clothing (16)", "OnMenuEvent", ctx.r3.u32, LoadU32(base, ctx.r4.u32 + 4u), LoadU32(base, ctx.r4.u32 + 8u));
}

// OnScreenAction TU1 0x826D5120 / disc 0x82580BB8 (jump table index code − 26): the upload dialog's
// input (h+264 vf+4, 0x826D5148), 26 / 27 the book and the figure's spin, 36 Lua OnPressA + sound, 59
// the zoom, 39 the second dye layer `sub_826D3108(h, 1)` (Godot's dye flow); B (37) and LB / RB (72 /
// 73: clothing mode, player one, 0x826D5244..0x826D5250) are the spine calls Godot posts as `PauseBack`
// / `PauseCycle:<p>:…`. Nothing kept.
REX_EXTERN(rex_lhGuiClothingScreen_OnScreenAction) {
  REX_FUNC_PROLOGUE();
  Said("clothing (16)", "OnScreenAction", ctx.r3.u32, ctx.r4.u32, ctx.r5.u32);
}

// ---- THE LEVERS -----------------------------------------------------------------------------

namespace refii::vfs {

bool DispatchClothing(PPCContext& ctx, uint8_t* base, uint32_t player, const std::string& verb, uint32_t ticket,
                      uint32_t value) {
  const Scratch s = Claim(ctx);
  const uint32_t out = s.data;          // an out word
  const uint32_t own = s.data + 0x10u;  // a 16-byte item handle of ours
  const uint32_t h = ScreenHandler(ctx, base, s.frame, 16u, kClothingVtable);
  const uint32_t g = Gui(base);
  const uint32_t qm = Plausible(g) ? LoadU32(base, g + 28u) : 0u;
  const uint32_t hero = refii_mod_player_hero(base, static_cast<int32_t>(player));
  const uint32_t worn_list = hero != 0u ? refii_mod_component(base, hero, 15u) : 0u;
  if (h == 0u || !Plausible(qm) || worn_list == 0u) {
    REXLOG_INFO("Refii guest-tick pump: Clothing:{}:{} — no clothing handler, or player {} has no hero with "
                "component 15; not taken", player, verb, player);
    return false;
  }
  const uint32_t inv = LoadU32(base, g + 52u);

  if (verb == "remove") {
    // `sub_826D3F40(h, type)` / 0x8257F9D8 on the player alone: the category `sub_826CC340(type − 91)`
    // (0x826D3F6C / 0x825A82C8; 17..23), the gate `IsWearingItemOfCategory(comp15, category)` (0x826D40B0;
    // 0x824C4418 / 0x82575030), then `lhGui_InventoryActions_UnequipClothingByEventType(INV, type, hero)`
    // (0x826D40E8; 0x826CC430 / 0x825A83B8), whose non-zero r5 names the body (`mr r9, r31` 0x826CC4CC).
    if (value < 91u || value > 97u) {
      REXLOG_INFO("Refii guest-tick pump: Clothing:{}:remove:{} — not an event type 91..97, dropped", player, value);
      return true;
    }
    const uint32_t category = Call(ctx, base, s.frame, 0x826CC340u, {value - 91u});
    const bool wearing = (Call(ctx, base, s.frame, 0x824C4418u, {worn_list, category}) & 0xFFu) != 0u;
    if (wearing) {
      Call(ctx, base, s.frame, 0x826CC430u, {inv, value, hero});
    }
    REXLOG_INFO("Refii guest-tick pump: Clothing:{}:remove:{} — category {} on hero 0x{:08X}: {}", player, value,
                category, hero, wearing ? "taken off" : "nothing worn there");
    return true;
  }

  if (verb != "wear" && verb != "outfit") {
    REXLOG_INFO("Refii guest-tick pump: Clothing:{}:{} is not wear, outfit or remove — dropped", player, verb);
    return true;
  }
  if (verb == "outfit" && player != 1u) {
    // `EquipOwnedSuitParts` walks the chooser's inventory (`sub_824F1E60` 0x826CC0D0), player one's on
    // this machine; player two's outfit takes its own hero's pieces (guest_tick_pump.cpp DressOutfit).
    return false;
  }

  bool owned = false;
  const uint32_t handle = RowItem(ctx, base, s.frame, h, player, hero, ticket, value, own, &owned);
  if (handle == 0u) {
    REXLOG_INFO("Refii guest-tick pump: Clothing:{}:{}:{}:0x{:08X} — hero 0x{:08X} does not hold it, dropped", player,
                verb, ticket, value, hero);
    return true;
  }
  const uint32_t item = LoadU32(base, handle);

  if (verb == "outfit") {
    // `sub_826D3A10` / 0x8257F4A8 on the player alone: `lhGui_InventoryActions_EquipOwnedSuitParts(INV,
    // &suit, hero)` (0x826D3F04; 0x826CBF10 / 0x825A7E98), each owned piece through `sub_826CB218`
    // (0x826CC2C0) on the body r5 names.
    const bool dressed = (Call(ctx, base, s.frame, 0x826CBF10u, {inv, handle, hero}) & 0xFFu) != 0u;
    FillPages(ctx, base, s.frame, h);
    ReleaseOwned(ctx, base, s.frame, handle, owned);
    REXLOG_INFO("Refii guest-tick pump: Clothing:{}:outfit 0x{:08X} on hero 0x{:08X} — {}", player, value, hero,
                dressed ? "pieces put on" : "no piece put on");
    return true;
  }

  // The row's A, `lhGuiClothingScreen_OnMenuEvent` type 3 (0x826D4FD8 / 0x82580A70): the definition's
  // `InventoryItemComponent.Category` (`sub_821B24F8` / `sub_822A99E8` / `sub_82430FA8`, 0x826D5020..
  // 0x826D5044; disc 0x821B2710 / 0x822A97A8 / 0x823A5128); 24, a dog item, is used and the book left —
  // `lhGuiInventory_ActivateItemByCategory(INV, &handle, 1)` + `ExitScreen(QM, 1)` (0x826D5060 /
  // 0x826D506C); anything else is the wear handler.
  const uint32_t definition = Call(ctx, base, s.frame, 0x821B24F8u, {item + 4u});
  const uint32_t component = Call(ctx, base, s.frame, 0x822A99E8u, {definition, Address(0x820AF11Cu)});
  if (component == 0u) {
    ReleaseOwned(ctx, base, s.frame, handle, owned);
    REXLOG_INFO("Refii guest-tick pump: Clothing:{}:wear 0x{:08X} — no InventoryItemComponent, nothing done", player,
                value);
    return true;
  }
  StoreU32(base, out, 0u);
  Call(ctx, base, s.frame, 0x82430FA8u, {component, Address(0x820B29BCu), out});
  if (LoadU32(base, out) == 24u) {
    Call(ctx, base, s.frame, 0x826C9AB8u, {inv, handle, 1u});
    Call(ctx, base, s.frame, 0x8236ED98u, {qm, 1u});
    ReleaseOwned(ctx, base, s.frame, handle, owned);
    REXLOG_INFO("Refii guest-tick pump: Clothing:{}:wear 0x{:08X} — a dog item: used, the book left", player, value);
    return true;
  }

  // The wear handler `sub_826D3608` / 0x8257F0A0, its decision taken on the PLAYER's component 15:
  // `IsWearingItem(comp15, &handle)` (0x826D374C; 0x824C4258 / 0x82574E70 — the item instance, not the
  // record); worn and the definition has an open model (`sub_82764A28(item)` 0x826D3770 / 0x82741840) →
  // on the description page (`*sub_826C9388(h+72) == 0`, 0x826D3794) the hood toggle `sub_826CB4F0(INV,
  // &handle, hero)` (0x826D37B4 / 0x825A7478); worn otherwise → nothing (0x826D37E4); not worn →
  // `sub_826CB218(INV, &handle, hero)` (0x826D3800 / 0x825A71A0: its own swim gate 0x826CB360 and
  // `AddAndReplaceItem`), the body's texture morphs when the item's dye record says so (`sub_823A1E18` +
  // `sub_82608E40` | `sub_82608EC0`, then `UpdateTextureMorphs(comp17)` 0x826D39BC; disc 0x82577A30,
  // 0x826182A0, 0x82618320, 0x824686C8), and tutorial 229 (`lhTutorials_Display` 0x826D39F4). The two
  // page fills end every path (0x826D39FC). Dropped: the row marks `sub_826D48F0` (0x826D37EC — Godot
  // restates its rows from the worn list on `refii_bridge_ui_data_changed`).
  const bool worn = (Call(ctx, base, s.frame, 0x824C4258u, {worn_list, handle}) & 0xFFu) != 0u;
  const bool hooded = worn && (Call(ctx, base, s.frame, 0x82764A28u, {item}) & 0xFFu) != 0u;
  const char* did = "already worn, nothing done";
  if (hooded) {
    did = "worn with an open model, off the description page: nothing done";
    if (PageTop(base, h) == 0u) {
      Call(ctx, base, s.frame, 0x826CB4F0u, {inv, handle, hero});
      did = "worn with an open model: hood toggled";
    }
  } else if (!worn) {
    Call(ctx, base, s.frame, 0x826CB218u, {inv, handle, hero});
    const uint32_t now = LoadU32(base, handle);
    if ((Load8(base, hero + 0x90u) & 0x40u) != 0u && now != 0u) {
      StoreU32(base, out, 0u);
      if ((Call(ctx, base, s.frame, 0x823A1E18u, {now, out}) & 0xFFu) != 0u) {
        const uint32_t dye = LoadU32(base, out);
        if ((Call(ctx, base, s.frame, 0x82608E40u, {dye}) & 0xFFu) != 0u ||
            (Call(ctx, base, s.frame, 0x82608EC0u, {dye}) & 0xFFu) != 0u) {
          const uint32_t morphs = refii_mod_component(base, hero, 17u);
          if (morphs != 0u) {
            Call(ctx, base, s.frame, 0x825B60F8u, {morphs});
          }
        }
      }
    }
    const uint32_t tutorials = Tutorials(base);
    if (Plausible(tutorials)) {
      Call(ctx, base, s.frame, 0x821B5260u, {LoadU32(base, tutorials + 4u), 229u});
    }
    did = (Call(ctx, base, s.frame, 0x824C4258u, {worn_list, handle}) & 0xFFu) != 0u
              ? "put on"
              : "refused by the guest's wear (swimming, or the piece it replaces stays)";
  }
  FillPages(ctx, base, s.frame, h);
  ReleaseOwned(ctx, base, s.frame, handle, owned);
  REXLOG_INFO("Refii guest-tick pump: Clothing:{}:wear ticket {} record 0x{:08X} on hero 0x{:08X} (item 0x{:08X}) — {}",
              player, ticket, value, hero, item, did);
  return true;
}

}  // namespace refii::vfs
