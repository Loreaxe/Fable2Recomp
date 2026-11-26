// refii — the camera a title states: what its camera component's setters, its manager's
// activation and its easing step are called with, in the order the guest calls them
// (src/runtime/src/core/stated_view.cpp). Both titles' hooks state through it; the shell reads the
// statements back (RefiiHostGetCameraStatements) and rebuilds the camera from them
// (Godot/src/Engine/camera/Engine_CameraRig.cs).
//
// The call ids are shared by both titles and by the shell (Engine_CameraStatements.Call mirrors
// them; a test reads this header). A record is 24 words:
//   0 sequence (from 1, one per record)
//   1 call | depth << 8 | player << 16 | flags << 24
//   2 manager   3 component   4 entity   5 the call's integer argument
//   6..21 sixteen floats   22 the guest frame it was stated in   23 zero
// Floats are the call's own arguments, in the guest's world axes; angles as the guest was handed
// them (degrees for the Lua setters).

#pragma once

#include <stdint.h>

namespace refii::camera {

constexpr uint32_t kRecordWords = 24u;

enum Call : uint32_t {
  // Component setters: comp = r3. Vector calls fill values 0..2.
  kMoveTo = 1,
  kMoveToCut = 2,
  kSetDirection = 3,      // the direction, as the setter's body is handed it
  kSetDirectionCut = 4,
  kSetTarget = 5,         // the point
  kSetTargetCut = 6,
  kSetAngles = 7,         // degrees, values 0..2
  kSetAnglesCut = 8,
  kSetFov = 9,            // degrees, value 0
  kSetFovCut = 10,
  kSetRoll = 11,          // degrees, value 0
  kSetRollCut = 12,
  kSetZoomLevel = 13,     // word
  kSetDof = 14,           // values 0..6 (Fable 2 states six, Fable 3 seven)
  kSetDofCut = 15,
  kAddShake = 16,         // value 0
  kSetShake = 17,
  kStopShake = 18,
  kInterpolateFor = 19,   // word = ticks
  kSetCutThisFrame = 20,  // word = the byte
  kSetBlur = 21,          // value 0
  kSetBlurCut = 22,
  kSetRepulsion = 23,     // Fable 3: values 0..2 the point, value 3 the radius
  // The manager: manager = r3.
  kActivate = 24,         // word = the Lua camera handed in; values 0/1 the primary and override
                          // wrappers after the call, as raw words
  // The easing step: comp = r3, entity = the component's entity.
  kEaseBegin = 25,        // word = the active flag the manager passed; values 0 the factor, 1 the
                          // shake, 2 the repulsion radius (0 on Fable 2), 3 the cut byte
  kEaseEnd = 26,
  kShakePosition = 27,    // Fable 2: values 0..2 the offset the step's shake added
  kShakeRotation = 28,    // Fable 2: values 0..5 the step's up and forward rows before the
                          // shake, 6..11 after it
  kRepulsion = 29,        // Fable 3: values 0..2 the eased position, 3..5 the position kept
  // The manager's own reads and pushes: manager = r3.
  kGuestBlock = 30,       // the block the guest's getter built, stated when it changed: entity =
                          // its source, values 0..2 position, 3..5 up, 6..8 forward, 9 horizontal
                          // fov, 10 Fable 3's shake (+0x38); word = the cut byte
  kPush = 31,             // the update's end; player = the player-map key, flags bit 0 = this
                          // machine draws it
  kSnapBegin = 32,        // the manager's settle pass (a load, a level change)
  kSnapEnd = 33,
};

constexpr uint32_t kFlagDrawn = 1u;

}  // namespace refii::camera

namespace refii::camera {

/// One record before the runtime numbers it: the hook fills words 1..21, the runtime the sequence
/// and the frame (refii_bridge_camera_statement).
struct Statement {
  uint32_t words[kRecordWords] = {};

  Statement(uint32_t call, uint32_t depth) { words[1] = (call & 0xFFu) | ((depth & 0xFFu) << 8); }
  Statement& Player(uint32_t key, uint32_t flags) {
    words[1] = (words[1] & 0xFFFFu) | ((key & 0xFFu) << 16) | ((flags & 0xFFu) << 24);
    return *this;
  }
  Statement& Manager(uint32_t manager) { words[2] = manager; return *this; }
  Statement& Component(uint32_t component) { words[3] = component; return *this; }
  Statement& Entity(uint32_t entity) { words[4] = entity; return *this; }
  Statement& Word(uint32_t word) { words[5] = word; return *this; }
  Statement& Value(uint32_t index, float value) {
    if (index < 16u) {
      __builtin_memcpy(&words[6u + index], &value, sizeof(value));
    }
    return *this;
  }
  Statement& Bits(uint32_t index, uint32_t bits) {
    if (index < 16u) {
      words[6u + index] = bits;
    }
    return *this;
  }
};

}  // namespace refii::camera
