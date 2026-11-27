// A player's achievements on the co-op channel (src/runtime/include/core/achievement_statement.h): the
// text Godot's Engine_AchievementStatement writes parses to the same character and sets Lua answers.
// Build: c++ -std=c++20 -I src/runtime/include tools/tests/achievement_statement_test.cpp && ./a.out

#include <cassert>
#include <cstdio>

#include "core/achievement_statement.h"

using refii::achievements::Holds;
using refii::achievements::IdList;
using refii::achievements::ParseStatement;

int main() {
  // Engine_AchievementWire.Encode({1, 23, 66}, {1, 10, 23, 66}): version 1, L 9, two 9-byte bitmaps.
  const char* text =
      "0123456789abcdef0123456789abcdef|0109"
      "020080000000000004"
      "020480000000000004";
  const auto stated = ParseStatement(text);
  assert(stated.valid);
  assert(stated.character == "0123456789abcdef0123456789abcdef");
  assert(IdList(stated.hero) == "1,23,66");
  assert(IdList(stated.profile) == "1,10,23,66");
  assert(Holds(stated.hero, 23) && !Holds(stated.hero, 10) && Holds(stated.profile, 10));

  // Not the form: no bar, bad hex, wrong version, a length the bytes do not match, the empty text.
  assert(!ParseStatement("").valid);
  assert(!ParseStatement("abc").valid);
  assert(!ParseStatement("x|zz00").valid);
  assert(!ParseStatement("x|0201ff00").valid);
  assert(!ParseStatement("x|0102ff").valid);
  // Bit 0 is never an id.
  assert(IdList(ParseStatement("|01010101").hero).empty());
  std::puts("ok");
  return 0;
}
