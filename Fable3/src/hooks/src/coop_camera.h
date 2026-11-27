// refii — Fable 3: player two's own camera in couch co-op (coop_camera_hooks.cpp).

#pragma once

#include <cstdint>

namespace refii::fable3::coopcam {

/// The shell's split (Refii.Game.GameSplitView.GuestSplit): `CoopSplitScreen:<0|1>`.
constexpr char kCoopSplitAction[] = "CoopSplitScreen:";

/// Takes a `CoopSplitScreen:` action off the tick pump (game_tick_pump.cpp).
void AnswerCoopSplit(const char* name);

/// The manager under player two's update right now (our second tick, coop_camera_hooks.cpp), else 0.
uint32_t PartnerManagerTicking();

/// Whether each player has their own camera now: the shell's split, a couch game, and player two's
/// Player built with a camera manager.
bool SplitInEffect(const uint8_t* base);

/// Player two's holder in a couch game when it has a camera manager, else 0.
uint32_t PartnerHolder(const uint8_t* base);

/// Whether `entity` is player two's hero in a couch game (the Player keyed 2, its hero smart pointer).
bool IsPartnerHero(const uint8_t* base, uint32_t entity);

/// The guest is destroying camera manager `manager` (camera_statements.cpp rex_CameraManager_Destroy):
/// player two's carried copy of the update's bytes belongs to it no longer.
void ManagerDestroyed(const uint8_t* base, uint32_t manager);

/// The player-two brackets on this thread: inside one the selector answers player two's holder.
uint32_t PartnerDepth();
void EnterPartner();
void LeavePartner();

/// One player-two bracket for a scope (entered only when `on`).
class PartnerScope {
 public:
  explicit PartnerScope(bool on) : on_(on) {
    if (on_) {
      EnterPartner();
    }
  }
  ~PartnerScope() {
    if (on_) {
      LeavePartner();
    }
  }
  PartnerScope(const PartnerScope&) = delete;
  PartnerScope& operator=(const PartnerScope&) = delete;

 private:
  bool on_;
};

}  // namespace refii::fable3::coopcam
