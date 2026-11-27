// refii — what the camera statements say per player, and the views the shell draws: the shared
// layouts of src/runtime/src/core/stated_view.cpp (core/payload_bridge.h), read by both titles' mod
// surfaces (src/payload/src/core/mod_requests.cpp, each title's Lua natives and event pump) and stated by
// the shell (Godot/src/Engine/camera/Engine_CameraViews.cs mirrors the view layout; a test reads this
// header). Kept apart from camera_statements.h, whose call ids a test counts.

#pragma once

#include <stdint.h>

namespace refii::camera {

// THE CAMERA EVENTS the statements carry (refii_bridge_camera_events_take), kEventWords words each:
//   0 kind   1 player (the player-map key of the push that ends the update)   2 ticks (kEventBlend)
//   3..13 the block the guest's getter last built for that manager: position, forward, up (guest axes),
//   horizontal fov in radians, the cut byte (float bits)   14..15 zero
enum EventKind : uint32_t {
  kEventCut = 1,    // the block's cut byte rose: 0 in the manager's previous block, set in this one
  kEventBlend = 2,  // an InterpolateFor with ticks > 0 stated since the previous push
  kEventTick = 3,   // the update's push
};
constexpr uint32_t kEventWords = 16u;

// THE VIEWS THE SHELL DRAWS (refii_bridge_camera_views_stated): word 0 the screen mode, 1 split, then
// kViewWords per player (1 then 2):
//   0..2 position   3..5 forward   6..8 up (guest axes)   9 horizontal fov   10 vertical fov (radians, of
//   the drawn view)   11 the drawn frame cut (1 / 0)   12..15 the view's x, y, width, height (0..1 of the
//   window, from its top left)   16 drawn (1 while a picture of that player is drawn here)   17..18 the
//   picture's centre off the camera's axis, as tangents along right and up (0 but through hybrid's swap,
//   where a half shows an off-centre part of its camera's centred picture)   19 zero
constexpr uint32_t kViewWords = 20u;
constexpr uint32_t kViewsWords = 2u + 2u * kViewWords;

// The screen modes (camera_screen_mode): the co-op screen the player chose and hybrid's live state.
constexpr int32_t kScreenShared = 0;
constexpr int32_t kScreenSplitVertical = 1;    // side by side
constexpr int32_t kScreenSplitHorizontal = 2;  // stacked
constexpr int32_t kScreenHybrid = 3;

}  // namespace refii::camera
