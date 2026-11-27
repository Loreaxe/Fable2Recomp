#pragma once

#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

// A PLAYER'S ACHIEVEMENTS, AS THE MACHINE THAT PLAYS THEIR HERO STATES THEM, on the co-op channel: a
// session TEXT in that player's store (core/player_state.h), key kStatementKey, written by the shell
// (Godot's Engine_Achievements) for every player this machine plays, so it reaches the other co-op
// machine as every player value does. Both mod surfaces read it — Lua's Refii.achievement_unlocked /
// Refii.achievements / Refii.character_id here, Godot's RefiiHost twins through the same text — so the
// two answer alike, and the online partner's sets arrive with the session whether or not their RexNet
// presence reaches this node.
//
// Form: `<character>|<wire hex>`: the hero's character id (the one its saves carry in refii_save.json,
// "" before the guest bound one), then Engine_AchievementWire's bytes as hex — a version byte (1), a
// bitmap length L (1..32), the hero's bitmap and the profile total's, L bytes each, bit `id` of byte
// `id / 8` being achievement `id`. An empty text: the player plays no hero here.
namespace refii::achievements {

inline constexpr char kStatementKey[] = "refii.achievements";

struct Statement {
  bool valid = false;
  std::string character;
  /// Ascending.
  std::vector<std::uint32_t> hero;
  std::vector<std::uint32_t> profile;
};

namespace detail {

inline int HexDigit(char c) {
  return c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
}

inline void Bits(const std::vector<std::uint8_t>& bytes, std::size_t at, std::size_t length, std::vector<std::uint32_t>& out) {
  for (std::size_t i = 0; i < length; ++i) {
    for (int bit = 0; bit < 8; ++bit) {
      const std::uint32_t id = static_cast<std::uint32_t>(i * 8u + static_cast<std::size_t>(bit));
      if ((bytes[at + i] & (1u << bit)) != 0u && id != 0u) {
        out.push_back(id);
      }
    }
  }
}

}  // namespace detail

/// The statement a player store text holds; `valid` false for a text that is not one (or empty).
inline Statement ParseStatement(std::string_view text) {
  Statement statement;
  const std::size_t bar = text.find('|');
  if (bar == std::string_view::npos) {
    return statement;
  }
  const std::string_view hex = text.substr(bar + 1u);
  if (hex.size() < 4u || hex.size() % 2u != 0u) {
    return statement;
  }
  std::vector<std::uint8_t> bytes(hex.size() / 2u);
  for (std::size_t i = 0; i < bytes.size(); ++i) {
    const int high = detail::HexDigit(hex[2u * i]);
    const int low = detail::HexDigit(hex[2u * i + 1u]);
    if (high < 0 || low < 0) {
      return statement;
    }
    bytes[i] = static_cast<std::uint8_t>((high << 4) | low);
  }
  const std::size_t length = bytes[1];
  if (bytes[0] != 1u || length == 0u || length > 32u || bytes.size() != 2u + 2u * length) {
    return statement;
  }
  statement.character = std::string(text.substr(0, bar));
  detail::Bits(bytes, 2u, length, statement.hero);
  detail::Bits(bytes, 2u + length, length, statement.profile);
  statement.valid = true;
  return statement;
}

/// The hero's ids as Refii.achievements answers them: "id,id,…" ascending, "" for none.
inline std::string IdList(const std::vector<std::uint32_t>& ids) {
  std::string out;
  for (const std::uint32_t id : ids) {
    if (!out.empty()) {
      out += ',';
    }
    out += std::to_string(id);
  }
  return out;
}

inline bool Holds(const std::vector<std::uint32_t>& ids, std::uint32_t id) {
  for (const std::uint32_t held : ids) {
    if (held == id) {
      return true;
    }
  }
  return false;
}

}  // namespace refii::achievements
