// refii - Fable 3's world icons and emotion choices, as the guest states them: every row here comes
// from the table Fable3/src/hooks/src/world_icon_hooks.cpp fills at the guest's own entry points
// (core/payload_bridge.h, src/runtime/src/core/stated_world_icons.cpp). Nothing here reads guest
// memory.
//
// AN ICON is stated by its Init (CWorldIconBase sub_82B87938), each Update the registry's tick runs
// on it (CWorldIcon3D sub_82225AC8, the HUD-element icon's sub_82B866C8: `visible` is that call's
// argument, sub_8222FA78) and the emotion icon's ring tick (sub_829C2F98); it leaves when the
// registry erases it (sub_82615CD8) or it is destroyed (sub_82B878A8). Only the icons an Update drew
// in the guest's latest HF step or the one before are exported (the table's VISIBLE, stamped by the
// step game_tick_pump.cpp states): the guest draws an icon only inside a visible Update (the draw
// `vt+64` at 0x822261B0), hides the rest (`vt+60` at 0x82225B10, its element through `vt+24` at
// 0x82B86AC8), and runs no Update at all while a GUI screen is over the game (the tick's
// sub_821F7F98 at 0x826138C0) — the Sanctuary, the pause and save screens.
//
// A CHOICE is stated by the list add every creator reaches (sub_8260FF38), restated after the
// press/release handler (sub_826103E8) and leaves at its deleter (sub_826131F0). Its icon is the
// stated emotion icon the guest's own lookup sub_82615168(R, Name, entity) would return: the same
// name and, when the record names an entity, the same entity (sub_82265BC0) — while it is drawn, as
// above; a record whose icon is not drawn is exported with none.

#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

#include "core/payload_bridge.h"
#include "host_abi.h"

namespace {

constexpr uint32_t kMaxIcons = 1024u;
constexpr uint32_t kMaxChoices = 64u;
constexpr uint32_t kTableText = 256u * 1024u;

/// One stated icon, as the table reads it back.
struct StatedIcon {
  uint32_t icon = 0u;
  const uint32_t* words = nullptr;
  const float* values = nullptr;
  const char* name = "";
  const char* title = "";
  const char* opinion = "";

  uint32_t Kind() const { return words[0]; }
  uint32_t Flags() const { return words[REFII_WORLD_ICON_FLAGS_WORD]; }
  bool Visible() const { return (Flags() & REFII_WORLD_ICON_VISIBLE) != 0u; }
  bool Pooled() const { return Kind() == REFII_WORLD_ICON_CLASS_HUD_ELEMENT; }
};

/// The table's icons, read once per call. The reader's buffers are this thread's own.
struct IconSnapshot {
  std::vector<uint32_t> icons = std::vector<uint32_t>(kMaxIcons);
  std::vector<uint32_t> words = std::vector<uint32_t>(kMaxIcons * REFII_WORLD_ICON_WORDS);
  std::vector<float> values = std::vector<float>(kMaxIcons * REFII_WORLD_ICON_VALUES);
  std::vector<char> text = std::vector<char>(kTableText);
  std::vector<StatedIcon> rows;

  void Read() {
    const uint32_t count = refii_bridge_world_icons_read(icons.data(), words.data(), values.data(), text.data(),
                                                         static_cast<uint32_t>(text.size()), kMaxIcons);
    rows.clear();
    const char* at = text.data();
    for (uint32_t i = 0; i < count; ++i) {
      StatedIcon row;
      row.icon = icons[i];
      row.words = words.data() + (i * REFII_WORLD_ICON_WORDS);
      row.values = values.data() + (i * REFII_WORLD_ICON_VALUES);
      row.name = at;
      at += std::strlen(at) + 1u;
      row.title = at;
      at += std::strlen(at) + 1u;
      row.opinion = at;
      at += std::strlen(at) + 1u;
      rows.push_back(row);
    }
  }
};

IconSnapshot& Snapshot() {
  thread_local IconSnapshot snapshot;
  snapshot.Read();
  return snapshot;
}

/// NUL-terminated strings packed into a caller's buffer; `Fits` first, so an entry's strings go in
/// whole or not at all.
struct TextWriter {
  char* text;
  uint32_t capacity;
  uint32_t at = 0u;

  bool Fits(uint32_t bytes) const { return text == nullptr || at + bytes <= capacity; }

  void Put(const char* value) {
    if (text == nullptr) {
      return;
    }
    const size_t length = std::strlen(value);
    std::memcpy(text + at, value, length);
    at += static_cast<uint32_t>(length);
    text[at++] = '\0';
  }
};

uint32_t Length(const char* value) { return static_cast<uint32_t>(std::strlen(value)) + 1u; }

}  // namespace

extern "C" {

/// Fable 2's layout, export for export. Words per icon: type, texture id, rgba, entity,
/// persistent, flags, pool slot (none: 0xFFFFFFFF), the guest's icon object. Values: anchor x y z,
/// scale, seconds left, age. Text: title NUL opinion NUL. Flags: bit0 fading out (+100), bit1 a
/// HUD-element icon (no texture of its own; the type names its button or marker), bit3 emotion
/// ring on (+692), bit4 ring complete (+700), bit5 the guest holds a live image for it (+148).
/// rgba: the icon's colour with alpha 255 — the guest fades by SCALE, which `scale` carries.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetWorldSprites(uint32_t* words, uint32_t word_capacity, float* values,
                                                        uint32_t value_capacity, char* text, uint32_t text_capacity,
                                                        uint32_t max_icons) {
  constexpr uint32_t kWords = 8u;
  constexpr uint32_t kValues = 6u;
  if (text != nullptr && text_capacity > 0u) {
    text[0] = '\0';
  }
  if (words == nullptr || values == nullptr) {
    return 0u;
  }

  TextWriter writer{text, text_capacity};
  uint32_t written = 0u;
  for (const StatedIcon& icon : Snapshot().rows) {
    if (written >= max_icons) {
      break;
    }
    if (!icon.Visible() || icon.Kind() == 0u) {
      continue;
    }
    const char* title = icon.Pooled() ? "" : icon.title;
    const char* opinion = icon.Pooled() ? "" : icon.opinion;
    if ((written + 1u) * kWords > word_capacity || (written + 1u) * kValues > value_capacity ||
        !writer.Fits(Length(title) + Length(opinion))) {
      break;
    }

    const uint32_t stated = icon.Flags();
    uint32_t flags = (stated & REFII_WORLD_ICON_FADING) != 0u ? 1u : 0u;
    if (icon.Pooled()) {
      flags |= 2u;
    } else {
      if ((stated & REFII_WORLD_ICON_RING_ON) != 0u) flags |= 8u;
      if ((stated & REFII_WORLD_ICON_RING_COMPLETE) != 0u) flags |= 16u;
      if ((stated & REFII_WORLD_ICON_IMAGE_LIVE) != 0u) flags |= 32u;
    }

    uint32_t* w = words + (written * kWords);
    float* v = values + (written * kValues);
    w[0] = icon.words[1];
    w[1] = icon.words[2];
    w[2] = (icon.words[3] & 0xFFFFFF00u) | 0xFFu;
    w[3] = icon.words[4];
    w[4] = (stated & REFII_WORLD_ICON_PERSISTENT) != 0u ? 1u : 0u;
    w[5] = flags;
    w[6] = 0xFFFFFFFFu;
    w[7] = icon.icon;
    v[0] = icon.values[0];
    v[1] = icon.values[1];
    v[2] = icon.values[2];
    v[3] = icon.values[3];
    v[4] = icon.values[4];
    v[5] = icon.values[5];
    writer.Put(title);
    writer.Put(opinion);
    ++written;
  }
  return written;
}

/// The stated choice records, in the order they were added, each with its stated emotion icon.
/// Words per choice: action, player uid words +8 and +12, entity, kind (+56), flags, ICON (0 while
/// none is stated), 0. Flags: bit0 holdable, bit1 manual power, bit2 held (+62), bit3 the icon's
/// ring is on (+692), bit4 the ring is complete (+700), bit5 the icon has a live image (+148).
/// Values: anchor x y z (the icon's +112, else the record's Position), ring progress 0..1
/// (+696 / 300), manual speed (the icon's +720, else the record's +64), the icon's scale (+152).
/// Text: Name NUL Text NUL RecordName NUL.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetFable3EmotionChoices(uint32_t* words, uint32_t word_capacity, float* values,
                                                                uint32_t value_capacity, char* text,
                                                                uint32_t text_capacity, uint32_t max_choices) {
  constexpr uint32_t kWords = 8u;
  constexpr uint32_t kValues = 6u;
  if (text != nullptr && text_capacity > 0u) {
    text[0] = '\0';
  }
  if (words == nullptr || values == nullptr) {
    return 0u;
  }

  thread_local std::vector<uint32_t> records(kMaxChoices);
  thread_local std::vector<uint32_t> choice_words(kMaxChoices * REFII_EMOTION_CHOICE_WORDS);
  thread_local std::vector<float> choice_values(kMaxChoices * REFII_EMOTION_CHOICE_VALUES);
  thread_local std::vector<char> choice_text(64u * 1024u);
  const uint32_t count = refii_bridge_emotion_choices_read(records.data(), choice_words.data(), choice_values.data(),
                                                           choice_text.data(),
                                                           static_cast<uint32_t>(choice_text.size()), kMaxChoices);
  const IconSnapshot& icons = Snapshot();

  TextWriter writer{text, text_capacity};
  uint32_t written = 0u;
  const char* at = choice_text.data();
  for (uint32_t i = 0; i < count && written < max_choices; ++i) {
    const uint32_t* cw = choice_words.data() + (i * REFII_EMOTION_CHOICE_WORDS);
    const float* cv = choice_values.data() + (i * REFII_EMOTION_CHOICE_VALUES);
    const char* name = at;
    at += std::strlen(at) + 1u;
    const char* label = at;
    at += std::strlen(at) + 1u;
    const char* record_name = at;
    at += std::strlen(at) + 1u;
    if ((written + 1u) * kWords > word_capacity || (written + 1u) * kValues > value_capacity ||
        !writer.Fits(Length(name) + Length(label) + Length(record_name))) {
      break;
    }

    const uint32_t entity = cw[3];
    const StatedIcon* icon = nullptr;
    for (const StatedIcon& candidate : icons.rows) {
      if (candidate.Kind() == REFII_WORLD_ICON_CLASS_EMOTION && candidate.Visible() &&
          std::strcmp(candidate.name, name) == 0 && (entity == 0u || candidate.words[4] == entity)) {
        icon = &candidate;
        break;
      }
    }

    uint32_t flags = cw[5] & (REFII_EMOTION_CHOICE_HOLDABLE | REFII_EMOTION_CHOICE_MANUAL | REFII_EMOTION_CHOICE_HELD);
    if (icon != nullptr) {
      const uint32_t stated = icon->Flags();
      if ((stated & REFII_WORLD_ICON_RING_ON) != 0u) flags |= 8u;
      if ((stated & REFII_WORLD_ICON_RING_COMPLETE) != 0u) flags |= 16u;
      if ((stated & REFII_WORLD_ICON_IMAGE_LIVE) != 0u) flags |= 32u;
    }

    uint32_t* w = words + (written * kWords);
    float* v = values + (written * kValues);
    w[0] = cw[0];
    w[1] = cw[1];
    w[2] = cw[2];
    w[3] = entity;
    w[4] = cw[4];
    w[5] = flags;
    w[6] = icon != nullptr ? icon->icon : 0u;
    w[7] = 0u;
    const float* anchor = icon != nullptr ? icon->values : cv;
    v[0] = anchor[0];
    v[1] = anchor[1];
    v[2] = anchor[2];
    v[3] = icon != nullptr ? static_cast<float>(static_cast<int32_t>(icon->words[6])) / 300.0f : 0.0f;
    v[4] = icon != nullptr ? icon->values[6] : cv[3];
    v[5] = icon != nullptr ? icon->values[3] : 1.0f;
    writer.Put(name);
    writer.Put(label);
    writer.Put(record_name);
    ++written;
  }
  return written;
}

}  // extern "C"
