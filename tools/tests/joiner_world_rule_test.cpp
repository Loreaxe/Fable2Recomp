// c++ -std=c++20 -I Fable2/src/vfs/include tools/tests/joiner_world_rule_test.cpp && ./a.out
#include "core/joiner_world_rule.h"

#include <cstdio>

using namespace refii::coop;

static_assert(!WorldStateLoadable(23u, 23u) && WorldStateLoadable(0u, 23u) && WorldStateLoadable(26u, 23u),
              "no load while the world map is up");
static_assert(MapTravelRefused(true, false) && MapTravelRefused(true, true), "an online joiner never travels");
static_assert(MapTravelRefused(false, true), "local player two's map never travels");
static_assert(!MapTravelRefused(false, false), "player one's map travels as shipped");
static_assert(SessionTravelRefusal("OnCarriedActionUseSnowglobe") == "TEXT_DLC2_QUEST_QD50_CANT_USE_ITEM" &&
                  SessionTravelRefusal("OnCarriedActionUseArenaMiniature") == "TEXT_DLC2_QUEST_QD50_CANT_USE_ITEM" &&
                  SessionTravelRefusal("OnCarriedActionUseSkull") == "TEXT_DLC2_QUEST_QD50_CANT_USE_ITEM" &&
                  SessionTravelRefusal("OnCarriedActionUseSpireStatue") == "TEXT_DLC2_QUEST_QD50_CANT_USE_ITEM",
              "the four DLC2 items, their scripts' own refusal");
static_assert(SessionTravelRefusal("OnCarriedActionUseDarkGuildSeal") == "GUI_SCREEN_WORLD_MAP_CANNOT_FAST_TRAVEL",
              "the seal has none: the map's");
static_assert(SessionTravelRefusal("OnCarriedActionUseBonusEffects").empty() && SessionTravelRefusal("").empty() &&
                  SessionTravelRefusal("OnCarriedActionUseSnowGlobe").empty(),
              "every other script, and the exact class name only");

int main() {
  std::printf("ok\n");
  return 0;
}
