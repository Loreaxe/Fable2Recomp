// refii - two functions the Fable III PC build puts on keys and the 360 scheme does not bind:
// the quicksave and the safety toggle (`pc-extra:quicksave:<p>`, `pc-extra:safety:<p>`).
//
// Both run the 360 guest's own functions, through the game VM's natives, on the VM's thread.
//
// QUICKSAVE. The PC's input receiver takes action 92 (`cmp dword ptr [esi],5Ch`, PC 0xB9BFE4) to
// sub_B9AEB0, which asks IsAbleToSaveGameManually(mgr, the player's hero) and then calls
// AutoSaveGame(mgr, 0, 1) (PC 0xB9AF30 / 0xB9AF3F). Both functions are in both 360 builds with
// the same bodies: IsAbleToSaveGameManually TU3 0x822FD1E0 / disc 0x822F9818 (the hero entity in
// r4, refused unless it is alive: `*(a2+148) & 0x40`), AutoSaveGame TU3 0x822F9200 / disc
// 0x822F58A8 — a2 set only marks an autosave pending (`+476 = 1`), a2 clear with a3 set saves
// now (TU3 sub_822F8940(mgr,1,0,0,…), disc sub_822F5098(mgr,1,0,0,…)). Lua reaches them as the
// globals `IsAbleToSaveGameManually(entity)` (registered TU3 0x8230209C / disc 0x822FD8A4) and
// `AutoSaveGame(a2, a3)` (TU3 0x82301E14 / disc 0x822FD61C; its dispatcher hands the first
// argument to a2 and the second to a3, TU3 0x82303900 = disc 0x8247DAD8). So the PC's call is
// `AutoSaveGame(false, true)`. The save is the guest's own: same file, same name, same format.
//
// SAFETY. The PC's release processor toggles it (CInputProcessRelease_vf1: IsSafetyModeEnabled,
// then SetSafetyMode(P, 0) at PC 0x13570EF or (P, 1) at 0x135710D). The 360 builds have both
// functions and no caller of that shape (their callers are the HUD suggestion's accept, cases 1
// and 2 of TU3 sub_828BD968 at 0x828BE130..0x828BE150 / disc 0x82867CB0..0x82867CD0, and two
// message handlers): SetSafetyMode(P, on) TU3 0x8242DFB0 / disc 0x824107B0 (`lbz r10,0x19(r3)`,
// no change when equal, `stb r4,0x19(r3)`), IsSafetyModeEnabled TU3 0x823CBB28 / disc 0x823A9720
// (`lbz r3,0x19(r3)`), P the hero's player component. Lua: `Player.SetSafetyMode(entity, on)`
// (registered TU3 0x824365EC / disc 0x82418634) and `Player.IsSafetyModeEnabled(entity)`.
//
// THE LITE PAUSE (the PC's P) has no lever here. The PC function behind the key (PC sub_6AE7D0,
// a message box named `PauseLite` with `GUI_SCREEN_PAUSE_LITE_GAME_PAUSED`) is in neither 360
// build; the 360's own lite pause, the cutscene's, is built inline in its GUI input receiver
// behind a game-mode test (TU3 CGUIInput_vf1 0x82600F2C..0x8260105C: mode 2, `GUI_SCREEN_PAUSE_ICS`,
// a message box pushed on the first queue), not in a function that can be called; and no native
// names a box.

#include <cstdint>
#include <cstring>
#include <string>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "box_answers.h"
#include "mod_lua.h"

namespace {

/// The player's hero as the game's own scripts name it: the mod core's handle when it is loaded,
/// else the local hero for player one.
constexpr const char kHero[] =
    "local h = nil "
    "if Refii and Refii.entity and Refii.player_hero then h = Refii.entity(Refii.player_hero(p)) end "
    "if h == nil and p == 1 and GetLocalHero then h = GetLocalHero() end "
    "if h == nil then return end ";

constexpr const char kQuickSave[] =
    "if IsAbleToSaveGameManually(h) then AutoSaveGame(false, true) end";

constexpr const char kSafety[] =
    "if Player.IsAvailable(h) then Player.SetSafetyMode(h, not Player.IsSafetyModeEnabled(h)) end";

}  // namespace

namespace refii::fable3 {

bool AnswerPcExtra(PPCContext& ctx, uint8_t* base, const char* action) {
  const char* rest = action + sizeof(kPcExtraAction) - 1u;
  const char* colon = std::strchr(rest, ':');
  if (colon == nullptr || (colon[1] != '1' && colon[1] != '2') || colon[2] != '\0') {
    REXLOG_WARN("[fable3-pc-extras] '{}' names no player; dropped", action);
    return false;
  }
  const std::string verb(rest, static_cast<size_t>(colon - rest));
  const char* body = verb == "quicksave" ? kQuickSave : verb == "safety" ? kSafety : nullptr;
  if (body == nullptr) {
    REXLOG_WARN("[fable3-pc-extras] '{}' is no extra; dropped", action);
    return false;
  }
  const std::string chunk = std::string("local p = ") + colon[1] + " " + kHero + body;
  std::string error;
  const bool ran = modlua::Run(ctx, base, chunk, "=refii/pc_extras", &error);
  if (ran) {
    REXLOG_INFO("[fable3-pc-extras] {} ran for player {}", verb, colon[1]);
  } else {
    REXLOG_WARN("[fable3-pc-extras] {} for player {} did not run: {}", verb, colon[1], error);
  }
  return ran;
}

}  // namespace refii::fable3
