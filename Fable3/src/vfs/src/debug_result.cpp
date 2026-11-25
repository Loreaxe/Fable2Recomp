// refii - Fable 3's debug-channel answer: the outcome of the last probe the guest tick ran (the game
// VM's `debug-gamelua:` chunk or a `debug-call:`, game_tick_pump.cpp), stated by the payload and read by the shell's
// RefiiHostGetDebugCallResult (Fable3/src/hostbridge/src/host_bridge.cpp).

#include "fable3_vfs.h"

#include <cstring>
#include <mutex>
#include <string>

namespace {

std::mutex g_mutex;
uint64_t g_seq = 0;
uint32_t g_addr = 0;
uint64_t g_r3 = 0;
std::string g_note;

}  // namespace

extern "C" void refiii_vfs_note_debug_result(uint32_t addr, uint64_t r3, const char* note, size_t length) {
  std::lock_guard<std::mutex> lock(g_mutex);
  ++g_seq;
  g_addr = addr;
  g_r3 = r3;
  g_note.assign(note != nullptr ? note : "", note != nullptr ? length : 0u);
}

extern "C" uint32_t refiii_vfs_debug_result(uint64_t* out_seq, uint32_t* out_addr, uint64_t* out_r3, char* out_note,
                                            uint32_t note_capacity) {
  std::lock_guard<std::mutex> lock(g_mutex);
  if (g_seq == 0) {
    return 0;
  }
  if (out_seq != nullptr) {
    *out_seq = g_seq;
  }
  if (out_addr != nullptr) {
    *out_addr = g_addr;
  }
  if (out_r3 != nullptr) {
    *out_r3 = g_r3;
  }
  if (out_note != nullptr && note_capacity != 0u) {
    const size_t n = g_note.size() < note_capacity - 1u ? g_note.size() : note_capacity - 1u;
    std::memcpy(out_note, g_note.data(), n);
    out_note[n] = '\0';
  }
  return 1;
}
