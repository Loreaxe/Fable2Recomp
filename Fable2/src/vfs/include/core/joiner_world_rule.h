// refii — WHAT AN ONLINE JOINER'S WORLD MAP LISTS, AND WHO MAY TRAVEL FROM IT (Fable 2).
//
// The world map's rows are the guest's own population (lhGuiWorldMapScreen_PopulateMenu): quests and
// jobs from the local player's quest tracker (comp 102), sales from the world's sales-and-shortages
// manager, families from the local player's family (comp 136), regions from the fast-travel manager's
// active stops. On a network client the guest has none of the world's: its build loop drops comp 102,
// and the quests, the sales and the stops are made by the host's scripts and simulation, which a client
// never runs. Which of them are the joiner's own and which the host world's (coop_world_state.cpp):
//   quests, jobs   the world's: every quest registers on QuestManager.HeroEntity = GetPlayerHero()
//                  (gameflow.lua), hero one's tracker — the one the guest's own pick reads on the
//                  session's host (net context +0x34 && +0x35 → record 1).
//   sales          the world's: the manager hangs off the world (players cell +100), no hero.
//   regions        the world's: FastTravel.SetStopAsActive(region, stop, on) names no hero.
//   families       the joiner's own: its hero's comp 136, the household the host keeps for it.
// So the host states the three world parts with their own serializers and the joiner loads them onto its
// copies — the quests onto the tracker its own lists read (the guest's pick: on a joiner its own hero's,
// the only record its players map holds).
//
// ONLY PLAYER ONE FAST-TRAVELS (user decision 2026-11-28). The map's every travel is gated by the guest's
// own permission (lhQuestTrail_HasNoDestination: the fast-travel manager enabled and no blocked text) and
// refused by its own box (GUI_SCREEN_WORLD_MAP_CANNOT_FAST_TRAVEL); for a player who is not player one —
// an online joiner, or local player two while their menus are up — the manager answers "not enabled".

#ifndef REFII_CORE_JOINER_WORLD_RULE_H
#define REFII_CORE_JOINER_WORLD_RULE_H

#include <cstdint>
#include <string_view>

namespace refii::coop {

/// Whether a stated world part may be loaded over this machine's copy now: not while the world map is
/// up, whose rows and pages index the lists the load replaces (a row's id is its ordinal).
constexpr bool WorldStateLoadable(uint32_t screen_now, uint32_t world_map_screen) { return screen_now != world_map_screen; }

/// THE CARRIED USES THAT MOVE THE SESSION (the shipped carried-use scripts): each sends the world to another
/// level — the four DLC2 items through `LevelExit.Interact(Debug.CreateEntityByHero(…), true)`, the Dark Guild
/// Seal through `Debug.LoadLevel("Albion", "TatteredSpire", …)`. Refused for a player who is not player one,
/// with the item script's own refusal text where it has one (`GUI.DisplayMessageBox(
/// "TEXT_DLC2_QUEST_QD50_CANT_USE_ITEM")`, line 6 of each DLC2 script), else the world map's own.
struct SessionTravelItem {
  std::string_view script;
  std::string_view refusal;
};
inline constexpr SessionTravelItem kSessionTravelItems[] = {
    {"OnCarriedActionUseArenaMiniature", "TEXT_DLC2_QUEST_QD50_CANT_USE_ITEM"},
    {"OnCarriedActionUseSkull", "TEXT_DLC2_QUEST_QD50_CANT_USE_ITEM"},
    {"OnCarriedActionUseSnowglobe", "TEXT_DLC2_QUEST_QD50_CANT_USE_ITEM"},
    {"OnCarriedActionUseSpireStatue", "TEXT_DLC2_QUEST_QD50_CANT_USE_ITEM"},
    {"OnCarriedActionUseDarkGuildSeal", "GUI_SCREEN_WORLD_MAP_CANNOT_FAST_TRAVEL"},
};

/// The refusal text key of a session-moving carried use, empty for any other script.
constexpr std::string_view SessionTravelRefusal(std::string_view script) {
  for (const SessionTravelItem& item : kSessionTravelItems) {
    if (item.script == script) {
      return item.refusal;
    }
  }
  return {};
}

/// Whether the world map's travel is refused here: on an online joiner always (its map is never the
/// session's to move), and on the host while local player two's menus are up.
constexpr bool MapTravelRefused(bool online_joiner, bool menus_act_for_player_two) {
  return online_joiner || menus_act_for_player_two;
}

}  // namespace refii::coop

#endif  // REFII_CORE_JOINER_WORLD_RULE_H
