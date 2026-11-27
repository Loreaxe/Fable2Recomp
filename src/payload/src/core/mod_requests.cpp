// refii — a mod's requests for Godot, shared by every title's payload: effects and lights
// (REFII_MOD_HOST_FX_KIND), boxes, notices, menus, marks, meters, fades, the HUD, input and pad
// presses (REFII_MOD_HOST_UI_KIND), menu rows and host screens (REFII_MOD_HOST_MENU_KIND). Pure
// formatting and posting through the bus (core/mod_values.h): no guest read, so a title's natives
// and helpers call these whatever game they serve. The contract is core/refii_mod_requests.h.

#include "core/refii_mod_requests.h"

#include <atomic>
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <string>
#include <string_view>
#include <vector>

#include <rex/logging.h>
#include <rex/ppc/context.h>

#include "core/camera_views.h"
#include "core/mod_values.h"
#include "core/payload_bridge.h"

// ---------------------------------------------------------------------------------------------
// Effects: a request for Godot, posted on the host-only kind REFII_MOD_HOST_FX_KIND.
// ---------------------------------------------------------------------------------------------

namespace {

/// The payload's ids are 1 .. 2^23-1; Godot's own play_fx counts from 2^23 (Engine_FxPlayer), so
/// every id is exact as a Lua number (a 32-bit float) and no surface's id collides with another's.
constexpr uint32_t kFxIdSpan = 0x7FFFFFu;
std::atomic<uint32_t> g_fx_ids{0};

/// The fields text RefiiHost parses: key=value, a 'quoted' text never carrying a quote.
uint32_t PostPlayFx(const char* name, const float* xyz, uint32_t handle, const refii_mod_fx_options* options) {
  if (name == nullptr || name[0] == 0) {
    return 0;
  }
  char clean[128];
  size_t n = 0;
  for (; name[n] != 0 && n + 1 < sizeof(clean); ++n) {
    const char c = name[n];
    clean[n] = (c == '\'' || c == ',' || static_cast<unsigned char>(c) < 0x20) ? '_' : c;
  }
  clean[n] = 0;
  const refii_mod_fx_options o = options != nullptr ? *options : refii_mod_fx_default_options();
  const float origin[3] = {xyz != nullptr ? xyz[0] : 0.0f, xyz != nullptr ? xyz[1] : 0.0f, xyz != nullptr ? xyz[2] : 0.0f};
  const uint32_t id = (g_fx_ids.fetch_add(1u, std::memory_order_relaxed) % kFxIdSpan) + 1u;
  // The helper point (options->at), as a quoted field: a name never carries a quote or a comma.
  char at[96];
  size_t m = 0;
  for (const char* c = o.at; c != nullptr && *c != 0 && m + 1 < sizeof(at); ++c) {
    at[m++] = (*c == '\'' || *c == ',' || static_cast<unsigned char>(*c) < 0x20) ? '_' : *c;
  }
  at[m] = 0;
  char fields[640];
  const int written = std::snprintf(
      fields, sizeof(fields),
      "op='play',id=%u,name='%s',x=%.9g,y=%.9g,z=%.9g,entity=%u,duration=%.9g,scale=%.9g,r=%.9g,g=%.9g,b=%.9g,"
      "intensity=%.9g,loop=%d,ox=%.9g,oy=%.9g,oz=%.9g,at='%s'",
      id, clean, origin[0], origin[1], origin[2], handle, o.duration, o.scale, o.tint[0], o.tint[1], o.tint[2],
      o.intensity, o.loop != 0 ? 1 : 0, o.offset[0], o.offset[1], o.offset[2], at);
  if (written <= 0 || static_cast<size_t>(written) >= sizeof(fields)) {
    return 0;
  }
  refii::mods::PostEvent(REFII_MOD_HOST_FX_KIND, std::string_view(fields, static_cast<size_t>(written)));
  return id;
}

}  // namespace

// The camera mod API's shape calls, made where the mods' Lua makes them: Refii._camera_call (the
// preamble, Engine_ModLuaCamera.cs) in the game VM, owner "native".
extern "C" int32_t refii_mod_camera_state(PPCContext* ctx, uint8_t* base, int32_t player, const char* call,
                                          const char* arguments) {
  if (ctx == nullptr || base == nullptr || (player != 1 && player != 2) || call == nullptr || call[0] == 0) {
    return 0;
  }
  for (const char* c = call; *c != 0; ++c) {
    if ((*c < 'a' || *c > 'z') && *c != '_') {
      return 0;
    }
  }
  std::string chunk = "if Refii and Refii._camera_call then Refii._camera_call('native','";
  chunk += call;
  chunk += "',";
  chunk += std::to_string(player);
  if (arguments != nullptr && arguments[0] != 0) {
    chunk += ",";
    chunk += arguments;
  }
  chunk += ") end";
  return refii_mod_run_game_lua(ctx, base, chunk.c_str());
}

// The camera's read calls: the shell's views and the statements' per-player blocks, as the runtime
// holds them (src/runtime/src/core/stated_view.cpp, core/camera_views.h). No guest read.
namespace {
constexpr float kDegrees = 57.29577951308232f;
}  // namespace

extern "C" int32_t refii_mod_camera_pose(int32_t player, float* out12) {
  float view[refii::camera::kViewWords];
  if (out12 == nullptr || refii_bridge_camera_view_read(player, view) == 0) {
    return 0;
  }
  for (int i = 0; i < 9; ++i) {
    out12[i] = view[i];
  }
  out12[9] = view[9] * kDegrees;
  out12[10] = view[10] * kDegrees;
  out12[11] = view[11];
  return 1;
}

extern "C" int32_t refii_mod_camera_stated(int32_t player, float* out11) {
  float block[11];
  if (out11 == nullptr || refii_bridge_camera_stated_block(player, block) == 0) {
    return 0;
  }
  for (int i = 0; i < 11; ++i) {
    out11[i] = block[i];
  }
  out11[9] = block[9] * kDegrees;
  return 1;
}

extern "C" int32_t refii_mod_camera_view(int32_t player, float* out4) {
  float view[refii::camera::kViewWords];
  if (out4 == nullptr || refii_bridge_camera_view_read(player, view) == 0) {
    return 0;
  }
  for (int i = 0; i < 4; ++i) {
    out4[i] = view[12 + i];
  }
  return 1;
}

extern "C" int32_t refii_mod_camera_screen_mode(int32_t* split) { return refii_bridge_camera_screen_read(split); }

// Engine_CameraViews.Ray's: forward + u·tan(h/2)·right + v·tan(v/2)·up, right = forward × up.
extern "C" int32_t refii_mod_camera_ray(int32_t player, float x, float y, float* out6) {
  float v[refii::camera::kViewWords];
  if (out6 == nullptr || refii_bridge_camera_view_read(player, v) == 0) {
    return 0;
  }
  const float* f = v + 3;
  const float* up = v + 6;
  float right[3] = {f[1] * up[2] - f[2] * up[1], f[2] * up[0] - f[0] * up[2], f[0] * up[1] - f[1] * up[0]};
  const float rl = std::sqrt(right[0] * right[0] + right[1] * right[1] + right[2] * right[2]);
  if (rl > 0.0f) {
    for (float& c : right) {
      c /= rl;
    }
  }
  const float a = v[17] + (2.0f * x - 1.0f) * std::tan(v[9] * 0.5f);
  const float b = v[18] + (1.0f - 2.0f * y) * std::tan(v[10] * 0.5f);
  float d[3];
  for (int i = 0; i < 3; ++i) {
    d[i] = f[i] + right[i] * a + up[i] * b;
  }
  const float dl = std::sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
  for (int i = 0; i < 3; ++i) {
    out6[i] = v[i];
    out6[3 + i] = dl > 0.0f ? d[i] / dl : f[i];
  }
  return 1;
}

extern "C" uint32_t refii_mod_play_fx(const char* name, const float* xyz, const refii_mod_fx_options* options) {
  return PostPlayFx(name, xyz, 0u, options);
}

extern "C" uint32_t refii_mod_play_fx_on(const char* name, uint32_t entity, const refii_mod_fx_options* options) {
  if (entity == 0) {
    return 0;
  }
  return PostPlayFx(name, nullptr, refii_bridge_mod_handle(entity), options);
}

/// The Lua surfaces' form: the entity is already a handle (Refii.play_fx_on).
extern "C" uint32_t refii_mod_play_fx_handle(const char* name, const float* xyz, uint32_t handle,
                                             const refii_mod_fx_options* options) {
  return PostPlayFx(name, xyz, handle, options);
}

extern "C" uint32_t refii_mod_play_fx_at(const char* name, uint32_t entity, const char* helper,
                                         const refii_mod_fx_options* options) {
  refii_mod_fx_options o = options != nullptr ? *options : refii_mod_fx_default_options();
  o.at = helper;
  return refii_mod_play_fx_on(name, entity, &o);
}

extern "C" void refii_mod_stop_fx(uint32_t id) {
  if (id == 0) {
    return;
  }
  char fields[64];
  const int written = std::snprintf(fields, sizeof(fields), "op='stop',id=%u", id);
  if (written > 0 && static_cast<size_t>(written) < sizeof(fields)) {
    refii::mods::PostEvent(REFII_MOD_HOST_FX_KIND, std::string_view(fields, static_cast<size_t>(written)));
  }
}

// ---------------------------------------------------------------------------------------------
// Lights: a request for Godot on the effect kind (Engine_ModLights reads op='light' / 'light_off')
// ---------------------------------------------------------------------------------------------

namespace {

/// The payload's light ids share the effects' span (1 .. 2^23-1); Godot's own count from 2^23.
std::atomic<uint32_t> g_light_ids{0};

/// The light request as the bus carries it: the entity as a handle (0 for the world point).
uint32_t PostLight(uint32_t handle, const float* xyz, const float* rgb, float energy, float range,
                   const refii_mod_light_options* options) {
  if (rgb == nullptr) {
    return 0;
  }
  const uint32_t id = (g_light_ids.fetch_add(1u, std::memory_order_relaxed) % kFxIdSpan) + 1u;
  const float origin[3] = {xyz != nullptr ? xyz[0] : 0.0f, xyz != nullptr ? xyz[1] : 0.0f, xyz != nullptr ? xyz[2] : 0.0f};
  const float offset[3] = {options != nullptr ? options->offset[0] : 0.0f, options != nullptr ? options->offset[1] : 0.0f,
                           options != nullptr ? options->offset[2] : 0.0f};
  char at[96];
  size_t m = 0;
  for (const char* c = options != nullptr ? options->at : nullptr; c != nullptr && *c != 0 && m + 1 < sizeof(at); ++c) {
    at[m++] = (*c == '\'' || *c == ',' || static_cast<unsigned char>(*c) < 0x20) ? '_' : *c;
  }
  at[m] = 0;
  // The spot fields (dx dy dz cone atten) ride every request; cone 0 is the omni light.
  const float direction[3] = {options != nullptr ? options->direction[0] : 0.0f,
                              options != nullptr ? options->direction[1] : 0.0f,
                              options != nullptr ? options->direction[2] : 0.0f};
  char fields[512];
  const int written = std::snprintf(
      fields, sizeof(fields),
      "op='light',id=%u,entity=%u,x=%.9g,y=%.9g,z=%.9g,r=%.9g,g=%.9g,b=%.9g,energy=%.9g,range=%.9g,ox=%.9g,oy=%.9g,oz=%.9g,"
      "at='%s',flicker=%.9g,dx=%.9g,dy=%.9g,dz=%.9g,cone=%.9g,atten=%.9g",
      id, handle, origin[0], origin[1], origin[2], rgb[0], rgb[1], rgb[2], energy, range, offset[0], offset[1], offset[2], at,
      options != nullptr ? options->flicker : 0.0f, direction[0], direction[1], direction[2],
      options != nullptr ? options->cone : 0.0f, options != nullptr ? options->attenuation : 0.0f);
  if (written <= 0 || static_cast<size_t>(written) >= sizeof(fields)) {
    return 0;
  }
  refii::mods::PostEvent(REFII_MOD_HOST_FX_KIND, std::string_view(fields, static_cast<size_t>(written)));
  return id;
}

}  // namespace

extern "C" uint32_t refii_mod_light(uint32_t entity, const float* xyz, const float* rgb, float energy, float range,
                                    const refii_mod_light_options* options) {
  return PostLight(entity != 0u ? refii_bridge_mod_handle(entity) : 0u, entity != 0u ? nullptr : xyz, rgb, energy, range, options);
}

/// The Lua surfaces' form: the entity is already a handle (Refii.light).
extern "C" uint32_t refii_mod_light_handle(uint32_t handle, const float* xyz, const float* rgb, float energy, float range,
                                           const refii_mod_light_options* options) {
  return PostLight(handle, handle != 0u ? nullptr : xyz, rgb, energy, range, options);
}

extern "C" void refii_mod_light_remove(uint32_t id) {
  if (id == 0) {
    return;
  }
  char fields[64];
  const int written = std::snprintf(fields, sizeof(fields), "op='light_off',id=%u", id);
  if (written > 0 && static_cast<size_t>(written) < sizeof(fields)) {
    refii::mods::PostEvent(REFII_MOD_HOST_FX_KIND, std::string_view(fields, static_cast<size_t>(written)));
  }
}

// ---------------------------------------------------------------------------------------------
// The UI: a request for Godot, posted on the host-only kind REFII_MOD_HOST_UI_KIND
// ---------------------------------------------------------------------------------------------

namespace {

/// A text for the bus's quoted field: no quote (the field's end) and no tab or line feed (the
/// record's); a line break crosses as the two characters \n, which Godot reads back.
std::string QuotedField(const char* text, size_t limit) {
  std::string out;
  for (const char* c = text; c != nullptr && *c != 0 && out.size() < limit; ++c) {
    if (*c == '\'') {
      out += '`';
    } else if (*c == '\n') {
      out += "\\n";
    } else if (static_cast<unsigned char>(*c) < 0x20u) {
      out += ' ';
    } else {
      out += *c;
    }
  }
  return out;
}

/// The payload's box ids are 1 .. 2^23-1; Godot's own message_box counts from 2^23 (Engine_UiModRequest).
std::atomic<uint32_t> g_box_ids{0};

int32_t ClampPlayer(int32_t player) { return player == 1 || player == 2 ? player : 0; }

}  // namespace

extern "C" int32_t refii_mod_open_menu(int32_t player, const char* name) {
  const std::string menu = QuotedField(name, 96);
  if (menu.empty()) {
    return 0;
  }
  const std::string fields = "op='open_menu',player=" + std::to_string(ClampPlayer(player)) + ",name='" + menu + "'";
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, fields);
  return 1;
}

extern "C" uint32_t refii_mod_message_box(int32_t player, const char* title, const char* text, const char* accept,
                                          const char* cancel) {
  const std::string head = QuotedField(title, 128);
  const std::string body = QuotedField(text, 480);
  if (head.empty() && body.empty()) {
    return 0;
  }
  const uint32_t id = (g_box_ids.fetch_add(1u, std::memory_order_relaxed) % kFxIdSpan) + 1u;
  const std::string fields = "op='message_box',id=" + std::to_string(id) + ",player=" +
                             std::to_string(ClampPlayer(player)) + ",title='" + head + "',text='" + body +
                             "',accept='" + QuotedField(accept, 64) + "',cancel='" + QuotedField(cancel, 64) +
                             "',buttons=" + (cancel != nullptr ? "2" : "1");
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, fields);
  return id;
}

extern "C" uint32_t refii_mod_notice(int32_t player, const char* title, const char* text, float seconds) {
  const std::string head = QuotedField(title, 128);
  const std::string body = QuotedField(text, 480);
  if (head.empty() && body.empty()) {
    return 0;
  }
  const uint32_t id = (g_box_ids.fetch_add(1u, std::memory_order_relaxed) % kFxIdSpan) + 1u;
  const float shown = std::isfinite(seconds) && seconds > 0.0f ? seconds : 0.0f;
  char length[32];
  std::snprintf(length, sizeof(length), "%.3f", static_cast<double>(shown));
  const std::string fields = "op='notice',id=" + std::to_string(id) + ",player=" + std::to_string(ClampPlayer(player)) +
                             ",title='" + head + "',text='" + body + "',seconds=" + length;
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, fields);
  return id;
}

extern "C" int32_t refii_mod_text(const char* text, char* out, uint32_t capacity) {
  if (text == nullptr || out == nullptr || capacity == 0u) {
    return -1;
  }
  return refii_bridge_mod_text(text, std::strlen(text), out, capacity);
}

extern "C" uint32_t refii_mod_menu_box(int32_t player, const char* title, const char* text, const char* const* options,
                                       int32_t count, int32_t cancelable) {
  constexpr int32_t kMaxOptions = 16;  // Engine_UiModRequest.MaxMenuOptions
  const int32_t n = options == nullptr || count < 0 ? 0 : (count > kMaxOptions ? kMaxOptions : count);
  if (n == 0) {
    return 0;
  }
  const uint32_t id = (g_box_ids.fetch_add(1u, std::memory_order_relaxed) % kFxIdSpan) + 1u;
  std::string fields = "op='menu_box',id=" + std::to_string(id) + ",player=" + std::to_string(ClampPlayer(player)) +
                       ",title='" + QuotedField(title, 128) + "',text='" + QuotedField(text, 480) +
                       "',cancel=" + (cancelable != 0 ? "1" : "0") + ",count=" + std::to_string(n);
  for (int32_t i = 0; i < n; ++i) {
    fields += ",o" + std::to_string(i) + "='" + QuotedField(options[i], 96) + "'";
  }
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, fields);
  return id;
}

// ---------------------------------------------------------------------------------------------
// HUD: markers, fades and meters — requests for Godot on the UI kind (Engine_UiModBoxes hands
// them to Engine_HudModMarkers, Engine_UiModFade and Engine_HudModMeters). Never an event.
// ---------------------------------------------------------------------------------------------

namespace {

/// Marker ids: the payload's 1 .. 2^23-1, Godot's from 2^23 (Engine_HudModMarkers.FirstHostId).
std::atomic<uint32_t> g_mark_ids{0};

std::string Float(float value) {
  char text[32];
  std::snprintf(text, sizeof(text), "%.9g", static_cast<double>(std::isfinite(value) ? value : 0.0f));
  return text;
}

}  // namespace

extern "C" uint32_t refii_mod_mark(int32_t player, uint32_t entity, const float* xyz, const char* icon,
                                   const char* text) {
  const std::string picture = QuotedField(icon, 160);
  const std::string line = QuotedField(text, 160);
  if (picture.empty() && line.empty()) {
    return 0;
  }
  const uint32_t id = (g_mark_ids.fetch_add(1u, std::memory_order_relaxed) % kFxIdSpan) + 1u;
  const uint32_t handle = entity != 0u ? refii_bridge_mod_handle(entity) : 0u;
  const std::string fields = "op='mark',id=" + std::to_string(id) + ",player=" + std::to_string(ClampPlayer(player)) +
                             ",entity=" + std::to_string(handle) + ",x=" + Float(xyz != nullptr ? xyz[0] : 0.0f) +
                             ",y=" + Float(xyz != nullptr ? xyz[1] : 0.0f) + ",z=" + Float(xyz != nullptr ? xyz[2] : 0.0f) +
                             ",icon='" + picture + "',text='" + line + "'";
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, fields);
  return id;
}

extern "C" void refii_mod_unmark(uint32_t id) {
  if (id == 0u) {
    return;
  }
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, "op='unmark',id=" + std::to_string(id));
}

extern "C" uint32_t refii_mod_world_meter(int32_t player, uint32_t entity, const float* xyz, float value, float max,
                                          const char* style) {
  if (!(max > 0.0f) || !std::isfinite(max)) {
    return 0;
  }
  const uint32_t id = (g_mark_ids.fetch_add(1u, std::memory_order_relaxed) % kFxIdSpan) + 1u;
  const uint32_t handle = entity != 0u ? refii_bridge_mod_handle(entity) : 0u;
  const std::string fields = "op='mark',id=" + std::to_string(id) + ",player=" + std::to_string(ClampPlayer(player)) +
                             ",entity=" + std::to_string(handle) + ",x=" + Float(xyz != nullptr ? xyz[0] : 0.0f) +
                             ",y=" + Float(xyz != nullptr ? xyz[1] : 0.0f) + ",z=" + Float(xyz != nullptr ? xyz[2] : 0.0f) +
                             ",icon='',text='',meter=1,value=" + Float(value) + ",max=" + Float(max) + ",style='" +
                             QuotedField(style, 32) + "'";
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, fields);
  return id;
}

extern "C" int32_t refii_mod_mark_meter(uint32_t id, float value, float max, const char* style) {
  if (id == 0u) {
    return 0;
  }
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, "op='mark_meter',id=" + std::to_string(id) + ",value=" + Float(value) +
                                                     ",max=" + Float(max) + ",style='" + QuotedField(style, 32) + "'");
  return 1;
}

extern "C" int32_t refii_mod_fade(int32_t player, int32_t to_black, float seconds) {
  const std::string fields = "op='fade',player=" + std::to_string(ClampPlayer(player)) + ",to=" +
                             (to_black != 0 ? "1" : "0") + ",seconds=" + Float(seconds > 0.0f ? seconds : 0.0f);
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, fields);
  return 1;
}

extern "C" int32_t refii_mod_meter(int32_t player, const char* id, const char* label, float value, float max,
                                   const char* style) {
  const std::string key = QuotedField(id, 96);
  if (key.empty()) {
    return 0;
  }
  const std::string fields = "op='meter',player=" + std::to_string(ClampPlayer(player)) + ",id='" + key + "',label='" +
                             QuotedField(label, 128) + "',value=" + Float(value) + ",max=" + Float(max) + ",style='" +
                             QuotedField(style, 32) + "'";
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, fields);
  return 1;
}

extern "C" int32_t refii_mod_meter_remove(const char* id) {
  const std::string key = QuotedField(id, 96);
  if (key.empty()) {
    return 0;
  }
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, "op='meter_remove',id='" + key + "'");
  return 1;
}

// Input claims and HUD overrides: state Godot's pad publish and HUD feed read (ModInput,
// Engine_UiHudOverrides), posted on the host-only UI kind like the meters.

extern "C" int32_t refii_mod_input_claim(int32_t player, const char* action, int32_t on) {
  const std::string name = QuotedField(action, 96);
  if (name.empty()) {
    return 0;
  }
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, "op='input_claim',player=" + std::to_string(ClampPlayer(player)) +
                                                     ",action='" + name + "',on=" + (on != 0 ? "1" : "0"));
  return 1;
}

extern "C" int32_t refii_mod_hud_icon(int32_t player, const char* element, const char* texture) {
  const std::string name = QuotedField(element, 96);
  if (name.empty()) {
    return 0;
  }
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, "op='hud_icon',player=" + std::to_string(ClampPlayer(player)) +
                                                     ",element='" + name + "',texture='" + QuotedField(texture, 160) + "'");
  return 1;
}

extern "C" int32_t refii_mod_hud_slide(int32_t player, const char* element, int32_t slide) {
  const std::string name = QuotedField(element, 96);
  if (name.empty() || slide < 0) {
    return 0;
  }
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, "op='hud_slide',player=" + std::to_string(ClampPlayer(player)) +
                                                     ",element='" + name + "',slide=" + std::to_string(slide));
  return 1;
}

extern "C" int32_t refii_mod_hud_hold(int32_t player, const char* piece, int32_t on) {
  const std::string name = QuotedField(piece, 96);
  if (name.empty()) {
    return 0;
  }
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, "op='hud_hold',player=" + std::to_string(ClampPlayer(player)) +
                                                     ",piece='" + name + "',on=" + (on != 0 ? "1" : "0"));
  return 1;
}

// A part of an entity hidden in Godot's drawing (Refii.Entities.Engine_HiddenParts), by the entity's
// handle and the player whose view loses it (0 every view): Godot owns the skeleton the part is a
// bone of, and the views.
extern "C" int32_t refii_mod_hide_part_for(uint32_t entity, const char* part, int32_t on, int32_t player) {
  const std::string name = QuotedField(part, 96);
  const uint32_t handle = entity != 0u ? refii_bridge_mod_handle(entity) : 0u;
  if (name.empty() || handle == 0u || player < 0 || player > 2) {
    return 0;
  }
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, "op='hide_part',entity=" + std::to_string(handle) + ",part='" + name +
                                                     "',on=" + (on != 0 ? "1" : "0") +
                                                     ",player=" + std::to_string(player));
  return 1;
}

extern "C" int32_t refii_mod_hide_part(uint32_t entity, const char* part, int32_t on) {
  return refii_mod_hide_part_for(entity, part, on, 0);
}

// Presses: state Godot's pad publish ORs into the player's pad (ModInputPresses), on the UI kind
// like the claims. A press is one player's pad: 1 or 2, never both.

extern "C" int32_t refii_mod_press(int32_t player, const char* button, int32_t held) {
  const std::string name = QuotedField(button, 32);
  if (name.empty() || (player != 1 && player != 2)) {
    return 0;
  }
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, "op='press',player=" + std::to_string(player) + ",button='" + name +
                                                     "',held=" + std::to_string(held < 0 ? 0 : (held > 255 ? 255 : held)));
  return 1;
}

extern "C" int32_t refii_mod_tap(int32_t player, const char* button, int32_t ticks) {
  const std::string name = QuotedField(button, 32);
  if (name.empty() || (player != 1 && player != 2) || ticks < 1) {
    return 0;
  }
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, "op='tap',player=" + std::to_string(player) + ",button='" + name +
                                                     "',ticks=" + std::to_string(ticks > 600 ? 600 : ticks));
  return 1;
}

// ---------------------------------------------------------------------------------------------
// Menu rows and host screens: a request for Godot, posted on the host-only kind
// REFII_MOD_HOST_MENU_KIND (Engine_UiModRows reads it)
// ---------------------------------------------------------------------------------------------

namespace {

/// One bus record's share of a rows text: the record is at most 2048 bytes (the runtime's
/// kMaxFieldsLength, src/runtime/src/core/mod_values.cpp) with the head and the menu name around it.
constexpr size_t kRowsChunk = 1800;
std::atomic<uint32_t> g_rows_seq{0};

}  // namespace

extern "C" int32_t refii_mod_set_menu_rows(int32_t player, const char* menu, const char* rows) {
  const std::string list = QuotedField(menu, 96);
  if (list.empty()) {
    return 0;
  }
  // The whole text as the bus carries it; past REFII_MOD_MENU_ROWS_MAX it is cut at the last whole
  // row (an unescaped ';') and the mod errors channel says so — never a silent truncation.
  std::string text = QuotedField(rows, REFII_MOD_MENU_ROWS_MAX + 1);
  if (text.size() > REFII_MOD_MENU_ROWS_MAX) {
    size_t cut = REFII_MOD_MENU_ROWS_MAX;
    while (cut > 0 && !(text[cut] == ';' && (cut == 0 || text[cut - 1] != '\\'))) {
      --cut;
    }
    text.resize(cut);
    char error[256];
    std::snprintf(error, sizeof(error), "set_menu_rows '%s': the rows are over %d bytes; cut to %zu at the last whole row",
                  list.c_str(), REFII_MOD_MENU_ROWS_MAX, cut);
    refii_bridge_mod_error_post(error, std::strlen(error));
    REXLOG_WARN("[mods] {}", error);
  }
  // In parts: Godot keeps them by seq and applies the rows once every part has arrived.
  const size_t parts = text.empty() ? 1u : (text.size() + kRowsChunk - 1) / kRowsChunk;
  const uint32_t seq = g_rows_seq.fetch_add(1u, std::memory_order_relaxed) + 1u;
  for (size_t part = 0; part < parts; ++part) {
    const std::string fields = "op='menu_rows',player=" + std::to_string(ClampPlayer(player)) + ",menu='" + list +
                               "',seq=" + std::to_string(seq) + ",part=" + std::to_string(part) +
                               ",parts=" + std::to_string(parts) + ",rows='" +
                               text.substr(part * kRowsChunk, kRowsChunk) + "'";
    refii::mods::PostEvent(REFII_MOD_HOST_MENU_KIND, fields);
  }
  return 1;
}

extern "C" int32_t refii_mod_open_screen(int32_t player, uint32_t screen) {
  if (screen == 0u) {
    return 0;
  }
  const std::string fields =
      "op='open_screen',player=" + std::to_string(ClampPlayer(player)) + ",screen=" + std::to_string(screen);
  refii::mods::PostEvent(REFII_MOD_HOST_MENU_KIND, fields);
  return 1;
}

// ---------------------------------------------------------------------------------------------
// Partner messages, signals and published texts: the bodies Fable 2's mod_api.cpp had, shared.

namespace {

bool PartnerWord(const char* text) {
  if (text == nullptr || text[0] == 0) {
    return false;
  }
  size_t n = 0;
  for (const char* c = text; *c != 0; ++c, ++n) {
    const bool ok = (*c >= 'a' && *c <= 'z') || (*c >= 'A' && *c <= 'Z') || (*c >= '0' && *c <= '9') || *c == '_' ||
                    *c == '.' || *c == '-';
    if (!ok || n >= REFII_MOD_PARTNER_NAME_MAX) {
      return false;
    }
  }
  return true;
}

/// Standard base64 (the route line carries no ';', '|' or line break; the payload may hold any byte).
std::string Base64(const char* bytes, size_t size) {
  static constexpr char kAlphabet[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
  std::string out;
  out.reserve((size + 2) / 3 * 4);
  for (size_t i = 0; i < size; i += 3) {
    const uint32_t a = static_cast<uint8_t>(bytes[i]);
    const uint32_t b = i + 1 < size ? static_cast<uint8_t>(bytes[i + 1]) : 0u;
    const uint32_t c = i + 2 < size ? static_cast<uint8_t>(bytes[i + 2]) : 0u;
    const uint32_t word = (a << 16) | (b << 8) | c;
    out += kAlphabet[(word >> 18) & 63u];
    out += kAlphabet[(word >> 12) & 63u];
    out += i + 1 < size ? kAlphabet[(word >> 6) & 63u] : '=';
    out += i + 2 < size ? kAlphabet[word & 63u] : '=';
  }
  return out;
}

bool ScopedName(const char* text) {
  if (text == nullptr || text[0] == 0 || text[0] == '.' || std::strchr(text, '.') == nullptr) {
    return false;
  }
  size_t n = 0;
  for (const char* c = text; *c != 0; ++c, ++n) {
    const bool ok = (*c >= 'a' && *c <= 'z') || (*c >= 'A' && *c <= 'Z') || (*c >= '0' && *c <= '9') || *c == '_' ||
                    *c == '.' || *c == '-';
    if (!ok || n >= REFII_MOD_SIGNAL_NAME_MAX) {
      return false;
    }
  }
  return true;
}

size_t BoundedPayload(const char* what, const char* name, const char* payload) {
  const size_t size = payload != nullptr ? std::strlen(payload) : 0u;
  if (size <= REFII_MOD_SIGNAL_PAYLOAD_MAX) {
    return size;
  }
  char error[256];
  std::snprintf(error, sizeof(error), "%s '%s': %zu bytes, cut at %d", what, name, size, REFII_MOD_SIGNAL_PAYLOAD_MAX);
  refii_bridge_mod_error_post(error, std::strlen(error));
  REXLOG_WARN("[mods] {}", error);
  return REFII_MOD_SIGNAL_PAYLOAD_MAX;
}

}  // namespace

extern "C" int32_t refii_mod_send_to_partner(const char* mod, const char* name, const char* payload) {
  // Only inside an online session: outside one both players are owned here and there is no partner
  // machine (a line posted then would wait on the route for the next peer).
  const uint32_t owned = refii_bridge_player_owned();
  if (owned != 1u && owned != 2u) {
    return 0;
  }
  if (!PartnerWord(mod) || !PartnerWord(name)) {
    return 0;
  }
  const size_t size = payload != nullptr ? std::strlen(payload) : 0u;
  if (size > REFII_MOD_PARTNER_PAYLOAD_MAX) {
    return 0;
  }
  // pm:<from>:<mod>:<name>:<base64 payload> — the route's mod lane (core/event_relay.h), drained on the peer.
  const std::string line = "pm:" + std::to_string(owned) + ":" + mod + ":" + name + ":" + Base64(payload, size);
  refii_bridge_route_post(line.c_str());
  return 1;
}

extern "C" int32_t refii_mod_signal(const char* name, const char* payload) {
  if (!ScopedName(name)) {
    return 0;
  }
  const size_t size = BoundedPayload("signal", name, payload);
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND,
                         std::string("op='signal',name='") + name + "',payload='" + Base64(payload, size) + "'");
  return 1;
}

extern "C" int32_t refii_mod_publish_text(const char* key, const char* text) {
  if (!ScopedName(key)) {
    return 0;
  }
  const size_t size = BoundedPayload("publish_text", key, text);
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND,
                         std::string("op='publish_text',key='") + key + "',text='" + Base64(text, size) + "'");
  return 1;
}

// ---------------------------------------------------------------------------------------------
// The player store, handles and the name hash: title-free, so every title's native mods have them
// (core/refii_mod_requests.h). Fable 2's mod_api.cpp had these bodies.
// ---------------------------------------------------------------------------------------------

extern "C" float refii_mod_player_value(int32_t player, const char* key, float fallback) {
  float value = fallback;
  return key != nullptr && refii_bridge_player_get(player, key, std::strlen(key), &value) != 0 ? value : fallback;
}

extern "C" int32_t refii_mod_player_get(int32_t player, const char* key, float* out) {
  return key != nullptr ? refii_bridge_player_get(player, key, std::strlen(key), out) : 0;
}

extern "C" void refii_mod_player_set(int32_t player, const char* key, float value) {
  if (key != nullptr) {
    refii_bridge_player_write(player, key, std::strlen(key), value, 0, 0);
  }
}

extern "C" void refii_mod_player_add(int32_t player, const char* key, float delta, int32_t saved) {
  if (key != nullptr) {
    refii_bridge_player_write(player, key, std::strlen(key), delta, 1, saved);
  }
}

extern "C" void refii_mod_player_save(int32_t player, const char* key, float value) {
  if (key != nullptr) {
    refii_bridge_player_write(player, key, std::strlen(key), value, 0, 1);
  }
}

extern "C" int32_t refii_mod_player_text(int32_t player, const char* key, char* out, uint32_t capacity) {
  return key != nullptr ? refii_bridge_player_get_text(player, key, std::strlen(key), out, capacity) : -1;
}

extern "C" void refii_mod_player_set_text(int32_t player, const char* key, const char* text) {
  if (key != nullptr) {
    refii_bridge_player_write_text(player, key, std::strlen(key), text != nullptr ? text : "",
                                   text != nullptr ? std::strlen(text) : 0u, 0);
  }
}

extern "C" void refii_mod_player_save_text(int32_t player, const char* key, const char* text) {
  if (key != nullptr) {
    refii_bridge_player_write_text(player, key, std::strlen(key), text != nullptr ? text : "",
                                   text != nullptr ? std::strlen(text) : 0u, 1);
  }
}

extern "C" int32_t refii_mod_player_hash(int32_t player, const char* key, uint32_t* out) {
  char text[REFII_MOD_PLAYER_TEXT_MAX + 1];
  if (refii_mod_player_text(player, key, text, sizeof(text)) <= 0) {
    return 0;
  }
  char* end = nullptr;
  const unsigned long long value = std::strtoull(text, &end, 0);
  if (end == text || *end != 0 || value > 0xFFFFFFFFull) {
    return 0;
  }
  if (out != nullptr) {
    *out = static_cast<uint32_t>(value);
  }
  return 1;
}

extern "C" void refii_mod_player_save_hash(int32_t player, const char* key, uint32_t hash) {
  char text[16];
  std::snprintf(text, sizeof(text), "0x%08x", hash);
  refii_mod_player_save_text(player, key, text);
}

extern "C" int32_t refii_mod_event_replica(void) { return refii_bridge_player_in_replica(); }

extern "C" int32_t refii_mod_player_here(int32_t player) {
  return (player == 1 || player == 2) && ((refii_bridge_player_owned() >> (player - 1)) & 1u) != 0u ? 1 : 0;
}

extern "C" uint32_t refii_mod_handle(uint32_t address) { return refii_bridge_mod_handle(address); }

extern "C" uint32_t refii_mod_handle_address(uint32_t handle) { return refii_bridge_mod_handle_address(handle); }

extern "C" uint32_t refii_mod_fnv1(const char* text) {
  uint32_t hash = 0x811C9DC5u;
  for (const char* c = text; c != nullptr && *c != 0; ++c) {
    hash = (hash * 0x01000193u) ^ static_cast<uint8_t>(*c);
  }
  return hash;
}

// ---------------------------------------------------------------------------------------------
// The guest tick's mod callbacks and the game-VM helpers built on the title's refii_mod_run_game_lua.
// ---------------------------------------------------------------------------------------------

namespace {

// Function-local, so a mod's static registrar can run before this TU's own statics.
std::vector<refii_mod_tick_fn>& Ticks() {
  static std::vector<refii_mod_tick_fn> ticks;
  return ticks;
}

bool LuaIdentifier(const char* key) {
  if (key == nullptr || *key == 0) {
    return false;
  }
  for (const char* c = key; *c != 0; ++c) {
    const bool ok = (*c >= 'a' && *c <= 'z') || (*c >= 'A' && *c <= 'Z') || *c == '_' ||
                    (c != key && *c >= '0' && *c <= '9');
    if (!ok) {
      return false;
    }
  }
  return true;
}

}  // namespace

extern "C" void refii_mod_register_tick(refii_mod_tick_fn fn) {
  if (fn != nullptr) {
    Ticks().push_back(fn);
  }
}

void refii_mod_run_ticks(PPCContext* ctx, uint8_t* base, float seconds) {
  for (refii_mod_tick_fn fn : Ticks()) {
    PPCContext work = *ctx;
    fn(&work, base, seconds);
  }
}

extern "C" int32_t refii_mod_game_lua_int(PPCContext* ctx, uint8_t* base, const char* expr,
                                          int32_t lo, int32_t hi, int32_t* out) {
  if (ctx == nullptr || expr == nullptr || lo > hi) {
    return 0;
  }

  const auto holds = [&](const char* op, int32_t value) {
    char chunk[512];
    std::snprintf(chunk, sizeof(chunk),
                  "local v = (%s) assert(type(v) == 'number' and v %s %d)", expr, op, value);
    return refii_mod_run_game_lua(ctx, base, chunk) != 0;
  };

  if (!holds(">=", lo) || !holds("<=", hi)) {
    return 0;
  }

  // The greatest integer m with v >= m: `v >= mid` halves the range each run.
  while (lo < hi) {
    const int32_t mid = static_cast<int32_t>(static_cast<int64_t>(lo) + (static_cast<int64_t>(hi) - lo + 1) / 2);
    if (holds(">=", mid)) {
      lo = mid;
    } else {
      hi = mid - 1;
    }
  }

  if (out != nullptr) {
    *out = lo;
  }
  return 1;
}

extern "C" int32_t refii_mod_save_set_int(PPCContext* ctx, uint8_t* base, const char* key, int32_t value) {
  if (ctx == nullptr || !LuaIdentifier(key)) {
    return 0;
  }
  char chunk[160];
  std::snprintf(chunk, sizeof(chunk), "RefiiModSave = RefiiModSave or {} RefiiModSave.%s = %d", key, value);
  return refii_mod_run_game_lua(ctx, base, chunk);
}

extern "C" int32_t refii_mod_save_get_int(PPCContext* ctx, uint8_t* base, const char* key, int32_t lo, int32_t hi,
                                          int32_t* out) {
  if (ctx == nullptr || !LuaIdentifier(key)) {
    return 0;
  }
  char expr[160];
  std::snprintf(expr, sizeof(expr), "RefiiModSave and RefiiModSave.%s", key);
  return refii_mod_game_lua_int(ctx, base, expr, lo, hi, out);
}

// --- Mods' own morph types (refii_mod_morph_*) ------------------------------------------------

namespace {

/// One registered mod morph, as Engine_ModMorphs.Publish states it.
struct ModMorphEntry {
  uint32_t id;
  float initial, minimum, maximum;
};

/// Engine_ModMorphs.Capacity.
constexpr int32_t kMaxModMorphs = 256;

std::mutex g_mod_morph_lock;
std::vector<ModMorphEntry> g_mod_morphs;
float g_mod_morph_generation = -1.0f;

/// The registry Godot published (`refii.morphs.*`), read again when its generation moved.
void RefreshModMorphs() {
  const float generation = refii::mods::Setting("refii.morphs.generation", 0.0f);
  std::lock_guard<std::mutex> lock(g_mod_morph_lock);
  if (generation == g_mod_morph_generation) {
    return;
  }
  g_mod_morph_generation = generation;
  g_mod_morphs.clear();
  const int32_t count = std::clamp(static_cast<int32_t>(refii::mods::Setting("refii.morphs.count", 0.0f)), 0, kMaxModMorphs);
  char key[64];
  const auto read = [&key](int32_t index, const char* field, float fallback) {
    std::snprintf(key, sizeof(key), "refii.morphs.%d.%s", index, field);
    return refii::mods::Setting(key, fallback);
  };
  for (int32_t i = 0; i < count; ++i) {
    const float hi = read(i, "hi", -1.0f);
    const float lo = read(i, "lo", -1.0f);
    if (!(hi >= 0.0f && hi <= 0x7FFF && lo >= 0.0f && lo <= 0xFFFF)) {
      continue;
    }
    ModMorphEntry entry{(static_cast<uint32_t>(hi) << 16) | static_cast<uint32_t>(lo), read(i, "default", 0.0f),
                        read(i, "min", 0.0f), read(i, "max", 1.0f)};
    if (entry.id < 0x100u || !(entry.maximum > entry.minimum) || !std::isfinite(entry.initial)) {
      continue;
    }
    g_mod_morphs.push_back(entry);
  }
}

bool FindModMorph(uint32_t id, ModMorphEntry& out) {
  if (id < 0x100u) {
    return false;
  }
  RefreshModMorphs();
  std::lock_guard<std::mutex> lock(g_mod_morph_lock);
  for (const ModMorphEntry& entry : g_mod_morphs) {
    if (entry.id == id) {
      out = entry;
      return true;
    }
  }
  return false;
}

void ModMorphKey(char* out, size_t size, uint32_t id) { std::snprintf(out, size, "morph.%u", id); }

}  // namespace

extern "C" uint32_t refii_mod_morph_id(const char* scoped) {
  const char* begin = scoped != nullptr ? scoped : "";
  while (*begin == ' ' || *begin == '\t') {
    ++begin;
  }
  const char* end = begin + std::strlen(begin);
  while (end > begin && (end[-1] == ' ' || end[-1] == '\t')) {
    --end;
  }
  uint32_t hash = 0x811C9DC5u;
  for (const char* c = begin; c < end; ++c) {
    const uint8_t byte = static_cast<uint8_t>(*c);
    hash = (hash * 0x01000193u) ^ (byte >= 'A' && byte <= 'Z' ? byte + 32u : byte);
  }
  const uint32_t id = hash & 0x7FFFFFFFu;
  return id < 0x100u ? id + 0x100u : id;
}

extern "C" int32_t refii_mod_morph_list(uint32_t* out, int32_t capacity) {
  RefreshModMorphs();
  std::lock_guard<std::mutex> lock(g_mod_morph_lock);
  const int32_t count = static_cast<int32_t>(g_mod_morphs.size());
  for (int32_t i = 0; out != nullptr && i < count && i < capacity; ++i) {
    out[i] = g_mod_morphs[i].id;
  }
  return count;
}

extern "C" int32_t refii_mod_morph_registered(uint32_t id, float* out_default, float* out_min, float* out_max) {
  ModMorphEntry entry{};
  if (!FindModMorph(id, entry)) {
    return 0;
  }
  if (out_default != nullptr) {
    *out_default = entry.initial;
  }
  if (out_min != nullptr) {
    *out_min = entry.minimum;
  }
  if (out_max != nullptr) {
    *out_max = entry.maximum;
  }
  return 1;
}

extern "C" int32_t refii_mod_morph_weight(int32_t player, uint32_t id, float* out) {
  ModMorphEntry entry{};
  if ((player != 1 && player != 2) || !FindModMorph(id, entry)) {
    return 0;
  }
  char key[32];
  ModMorphKey(key, sizeof(key), id);
  float weight = entry.initial;
  if (refii_mod_player_get(player, key, &weight) == 0 || !std::isfinite(weight)) {
    weight = entry.initial;
  }
  if (out != nullptr) {
    *out = std::clamp(weight, entry.minimum, entry.maximum);
  }
  return 1;
}

extern "C" int32_t refii_mod_set_morph_weight(int32_t player, uint32_t id, float weight) {
  ModMorphEntry entry{};
  if ((player != 1 && player != 2) || !FindModMorph(id, entry)) {
    return 0;
  }
  char key[32];
  ModMorphKey(key, sizeof(key), id);
  refii_mod_player_save(player, key, std::clamp(std::isfinite(weight) ? weight : entry.initial, entry.minimum, entry.maximum));
  return 1;
}

