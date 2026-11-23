// refii — the loading screen's TEXT as the guest states it, as SHARED STATE.
//
// In librefii_vfs for the reason entity_appearance_state.cpp gives: the PAYLOAD writes
// (Fable2/src/vfs/src/hooks/loading_screen_stated.cpp, on the guest's 3D thread) and the HOST
// reads (the host bridge's RefiiHostGetLoadingScreenText).

#include <algorithm>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <string>

#include "core/payload_bridge_fable2.h"

namespace {

struct Text {
  uint32_t screen = 0u;
  uint32_t manager = 0u;
  uint32_t tip_key = 0u;
  uint32_t tip_index = 0u;
  uint32_t tip_count = 0u;
  uint32_t spinner_draws = 0u;
  bool banner = false;
  std::string level;
  std::string overlay;
};

std::mutex g_lock;
Text g_text;

}  // namespace

extern "C" {

void refii_bridge_loading_text_made(uint32_t manager, uint32_t screen, int32_t banner, const char* level,
                                    const char* overlay) {
  std::lock_guard<std::mutex> guard(g_lock);
  g_text = Text{};
  g_text.screen = screen;
  g_text.manager = manager;
  g_text.banner = banner != 0;
  g_text.level = level != nullptr ? level : "";
  g_text.overlay = overlay != nullptr ? overlay : "";
}

void refii_bridge_loading_text_gone(uint32_t screen) {
  std::lock_guard<std::mutex> guard(g_lock);
  if (g_text.screen == screen) {
    g_text = Text{};
  }
}

int32_t refii_bridge_loading_text_overlay(uint32_t manager, const char* text) {
  std::lock_guard<std::mutex> guard(g_lock);
  if (g_text.screen == 0u || g_text.manager != manager) {
    return 0;
  }
  g_text.overlay = text != nullptr ? text : "";
  return 1;
}

void refii_bridge_loading_text_tip(uint32_t screen, uint32_t key, uint32_t index, uint32_t count) {
  std::lock_guard<std::mutex> guard(g_lock);
  if (g_text.screen != screen) {
    return;
  }
  g_text.tip_key = key;
  g_text.tip_index = index;
  g_text.tip_count = count;
}

void refii_bridge_loading_text_spinner(uint32_t screen) {
  std::lock_guard<std::mutex> guard(g_lock);
  if (g_text.screen == screen) {
    ++g_text.spinner_draws;
  }
}

int32_t refii_bridge_loading_text_read(uint32_t* out_words, char* text, uint32_t capacity) {
  std::lock_guard<std::mutex> guard(g_lock);
  const bool stated = g_text.screen != 0u;
  if (out_words != nullptr) {
    out_words[0] = g_text.tip_index;
    out_words[1] = g_text.tip_count;
    out_words[2] = g_text.tip_key;
    out_words[3] = g_text.spinner_draws != 0u ? g_text.spinner_draws - 1u : 0u;
    out_words[4] = stated ? 1u : 0u;
    out_words[5] = g_text.banner ? 1u : 0u;
  }
  if (text != nullptr && capacity > 0u) {
    uint32_t at = 0u;
    for (const std::string* part : {&g_text.level, &g_text.overlay}) {
      if (at >= capacity) {
        break;
      }
      const uint32_t length = static_cast<uint32_t>(std::min<size_t>(part->size(), capacity - at - 1u));
      std::memcpy(text + at, part->data(), length);
      text[at + length] = '\0';
      at += length + 1u;
    }
    if (at < capacity) {
      text[at] = '\0';
    }
  }
  return stated ? 1 : 0;
}

}  // extern "C"
