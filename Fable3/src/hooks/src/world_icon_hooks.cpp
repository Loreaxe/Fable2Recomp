// refii - Fable 3's world icons and emotion choices, stated at the guest's own entry points into
// the table both modules link (core/payload_bridge.h, src/runtime/src/core/stated_world_icons.cpp);
// the host bridge (Fable3/src/hostbridge/src/icon_feed.cpp) reads only that table. Every hook runs
// the guest's body unchanged: the icon objects and records stay alive and run — the hold fill
// (disc sub_825EC530 posts message 116 at disc 0x825ECB20 and runs CECOnActionUse_Use at disc 0x825ECD64), the
// choice commit (sub_826103E8 finds its icon through sub_82615168 at 0x826105D0) and every read-back
// are the guest's. The drawing is Godot's. A choice's press and release are Godot's lever
// (emotion_choice_answer.cpp); a record is stated as its creator built it, so its action is the pressed
// code it was added with.
//
// Every address is the Fable 3 TU3 IDB ("disc" marks one TU3 lacks); each offset names its base.
//
// THE REGISTRY R = *(*(GUI+0x20)+0x18), GUI = *0x83596F84, ticked by disc sub_825EC530(R, time, ctx, draw).
//   - Add (sub_82614D30 by entity, sub_82614E78 by position) only builds a 64-byte request
//     (sub_826135E0) and queues it on R+88 (sub_82616B28 at 0x82614DAC / 0x82614F04).
//   - The tick takes each accepted request (sub_82616590, disc 0x825EC684) to sub_82615E98 (disc 0x825EC69C):
//     an icon already held under the same name and entity is initialised again (`vt+4` at
//     0x82615FF0); otherwise a CWorldIcon3D (560 bytes, vtable 0x82002978 stored 0x82B84E84; built
//     0x82616148), a CEmotionWorldIcon (912, type 77, vtable 0x820C6D50 stored 0x829C0E78; built
//     0x82616188) or a HUD-element icon (192, vtable 0x820D889C stored 0x82B870BC by sub_82B86FC8,
//     0x82616194) is built and initialised (`vt+4`, 0x826162B4). Every Init reaches
//     CWorldIconBase's sub_82B87938 (from 0x82B85DE4 and 0x82B86BEC; the emotion Init sub_829C10F0
//     runs the 3D one at 0x829C110C).
//   - Each tick it asks every icon it holds whether it lives (`vt+20`, disc 0x825EC7C4 / 0x8261430C /
//     0x82614748) and Updates it (`vt+16`, icon, visible, ctx) — disc 0x825EC824 for the entity-less
//     vector R+20..R+24, 0x82614368 / 0x826143B4 / 0x826147D4 for the entity map. `visible` is
//     sub_8222FA78: the icon's player (+103, written 0x82616230) is the current one (0x8222FABC),
//     R+55 may draw world icons, the HUD's hidden bit, and for the opinion and hint kinds R+56 and
//     R+57 (SetCanShowHintWorldIcons writes R+57 at 0x8254BFA8) — and, in an entity's group, only
//     the icon that wins the group (0x826147B8). A dead icon is erased instead: sub_82615CD8(out,
//     R, vector, iterator) is the one erase of an icon (disc 0x825EC858, 0x826143D4, 0x82614808, Remove
//     sub_82615018 at 0x826150D8, the record's remover sub_8226B2E0 -> sub_82194468 at 0x8219451C).
//   - The Updates: CWorldIcon3D's sub_82225AC8 (slot 4; the emotion Update sub_829C20A0 runs it at
//     0x829C20E4 and then the ring tick sub_829C2F98 at 0x829C2164) and the HUD-element icon's
//     sub_82B866C8 (slot 4; `visible` shows its element through `vt+24` at 0x82B86AC8). Both start
//     with the base sub_8222E3F8: seconds left +48 -= dt (0x8222E430), age +68 += dt (0x8222E45C).
//   - Every icon class ends in CWorldIconBase's destructor sub_82B878A8 (0x82B8515C, 0x82B8651C,
//     0x82B87874).
//
// AN ICON (offsets from the icon object):
//   +16 name CCharString (0x82B87950)   +20 type (`vt+8`: 0x82B85E40, 0x82B86C58)
//   +24/+28 entity weak ref, `*(+28)` the node whose first word is the CThing (0x8222E4C4..F5F4)
//   +48 seconds left, +52 persistent (0x82B87A4C..0x82B87A64)
//   +60..+63 colour, drawn as (+62, +61, +60, +63) / 255 (`vt+52` disc sub_82B53EA8 copies it,
//   disc 0x82B53EB0..disc 0x82B53EC8; the draw sub_82B85628 reads it at 0x82B859A0..0x82B85A2C)
//   +68 age   +100 fading out (cleared by Init 0x82B8797C, set at age 4 s 0x82225C44)
//   +103 player   +112 the anchor: GetWorldPosition (`vt+48`, sub_8221A590 `stvx128 v127, r27, r25`
//   with r25 = 112 at 0x8221AA30; the emotion icon's sub_829C2350 adds its orbit, 0x829C2B8C) —
//   the liveness test computes it each tick (0x8222E54C) and the draw projects it
//   3D and emotion only: +144 texture id (0x82B85E74; the emotion's swap sub_829C2CC8 at 0x829C2CE0),
//   +148 the GUI image, -1 while none (0x82B8590C, released 0x82B85EEC), +152 scale (0x82225BC8..
//   0x82225C10), +156 title and +372 opinion LHStrings (`vt+12` sub_82B85F90, 0x82B85FAC/0x82B85FBC)
//   emotion only: +692 ring on (sub_829C1D20, 0x829C1D50), +696 progress 0..300 (0x829C3240), +700
//   complete (0x829C31B8), +701 manual (0x829C1D94), +720 manual speed (0x829C30DC)
//   HUD-element only: +144 is its pooled element slot (0x82B870A8), +148 its element state.
//
// THE CHOICE RECORDS. Every creator — Lua Emotion.AddEmotion (sub_8260FE98 0x8260FF10), the shop
// plinth (sub_826100C8 0x82610194), OnActionUse (sub_826101C8 0x82610278), sub_8260FFC8 (0x8261009C),
// the net clone (sub_822935B8) — ends in sub_8260FF38(E, shared_ptr*), which pushes the shared
// pointer onto E's list (sub_82476A28 copies its object word into the node at 0x82476A68). The
// record's shared count deletes it through sub_826131F0 (the deleter sub_82613178 stores at
// 0x826131E4), which removes its icon (sub_8226B2E0 at 0x82613238). The record (sub_8260F610):
//   +0 action (0x8260F634; the release rewrites it, disc 0x825E9600)   +4 Name (0x8260F674)
//   +8/+12 player uid (0x8260F85C/0x8260F864)   +16 Text LHString (0x8260F6C4)
//   +20/+24 entity weak ref (0x8260F6F8/0x8260F700)   +32 Position (0x8260F718)
//   +52 RecordName (0x8260F754)   +56 kind (sub_82613368, 0x8260F760)   +60 Holdable (0x8260F778)
//   +61 ManuallyIncreasePower (0x8260F790)   +62 held (0x8260F794; press disc 0x825E9924, release
//   disc 0x825E9608, both in sub_826103E8)   +64 ManualPowerIncreaseSpeed (0x8260F7C8)
// The record's icon is the one sub_82615168(R, name, entity) finds: the record's Name and entity
// (sub_8260FB48 queues it under rec+4 and the entity at rec+20, 0x8260FBD0), matched by
// sub_82265BC0 on the icon's +16 and, when an entity is given, its +24.

#include <atomic>
#include <cstdint>
#include <cstring>
#include <string>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "core/payload_bridge.h"
#include "gui_statements.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_WorldIcon_Init);
REX_EXTERN(__imp__rex_WorldIcon3D_Update);
REX_EXTERN(__imp__rex_HudElementIcon_Update);
REX_EXTERN(__imp__rex_EmotionIcon_RingTick);
REX_EXTERN(__imp__rex_IconRegistry_Erase);
REX_EXTERN(__imp__rex_WorldIcon_Destroy);
REX_EXTERN(__imp__rex_EmotionChoices_Add);
REX_EXTERN(__imp__rex_EmotionChoice_Delete);

namespace {

using refii::fable3::LoadGuestF32;
using refii::fable3::LoadGuestU32;
using refii::fable3::Plausible;
using refii::fable3::ReadCharString;
using refii::fable3::ReadLhString;

constexpr uint32_t kVtWorldIcon3D = 0x82002978u;  // classified by ToTu3
constexpr uint32_t kVtEmotionIcon = 0x820C6D50u;  // classified by ToTu3
constexpr uint32_t kVtHudElementIcon = 0x820D889Cu;  // classified by ToTu3

constexpr uint32_t kMaxChoices = 64u;

uint8_t Byte(const uint8_t* base, uint32_t address) { return base[address]; }

/// The CThing behind the weak ref at `at`: `*(*(at+4))`, 0 when it is gone.
uint32_t WeakRefTarget(const uint8_t* base, uint32_t at) {
  const uint32_t node = LoadGuestU32(base, at + 4u);
  if (!Plausible(node)) {
    return 0u;
  }
  const uint32_t thing = LoadGuestU32(base, node);
  return Plausible(thing) ? thing : 0u;
}

uint32_t ClassOf(const uint8_t* base, uint32_t icon) {
  switch (refii::guest::ToTu3(LoadGuestU32(base, icon))) {
    case kVtWorldIcon3D:
      return REFII_WORLD_ICON_CLASS_3D;
    case kVtEmotionIcon:
      return REFII_WORLD_ICON_CLASS_EMOTION;
    case kVtHudElementIcon:
      return REFII_WORLD_ICON_CLASS_HUD_ELEMENT;
    default:
      return 0u;
  }
}

const char* ClassName(uint32_t kind) {
  switch (kind) {
    case REFII_WORLD_ICON_CLASS_3D:
      return "3D";
    case REFII_WORLD_ICON_CLASS_EMOTION:
      return "emotion";
    case REFII_WORLD_ICON_CLASS_HUD_ELEMENT:
      return "HUD element";
    default:
      return "?";
  }
}

std::atomic<uint64_t> g_types_seen[2]{};

/// The icon as its last writer left it; `visible` is the Update's argument, -1 keeps the last one.
void StateIcon(const uint8_t* base, uint32_t icon, int32_t visible) {
  if (!Plausible(icon)) {
    return;
  }
  const uint32_t kind = ClassOf(base, icon);
  if (kind == 0u) {
    return;
  }
  const bool pooled = kind == REFII_WORLD_ICON_CLASS_HUD_ELEMENT;
  const bool emotion = kind == REFII_WORLD_ICON_CLASS_EMOTION;

  uint32_t words[REFII_WORLD_ICON_WORDS]{};
  float values[REFII_WORLD_ICON_VALUES]{};
  const uint32_t type = LoadGuestU32(base, icon + 20u);
  words[0] = kind;
  words[1] = type;
  words[2] = pooled ? 0u : LoadGuestU32(base, icon + 144u);
  words[3] = (static_cast<uint32_t>(Byte(base, icon + 62u)) << 24) |
             (static_cast<uint32_t>(Byte(base, icon + 61u)) << 16) |
             (static_cast<uint32_t>(Byte(base, icon + 60u)) << 8) | Byte(base, icon + 63u);
  words[4] = WeakRefTarget(base, icon + 24u);
  uint32_t flags = 0u;
  if (Byte(base, icon + 52u) != 0u) flags |= REFII_WORLD_ICON_PERSISTENT;
  if (Byte(base, icon + 100u) != 0u) flags |= REFII_WORLD_ICON_FADING;
  if (!pooled && LoadGuestU32(base, icon + 148u) != 0xFFFFFFFFu) flags |= REFII_WORLD_ICON_IMAGE_LIVE;
  if (emotion) {
    if (Byte(base, icon + 692u) != 0u) flags |= REFII_WORLD_ICON_RING_ON;
    if (Byte(base, icon + 700u) != 0u) flags |= REFII_WORLD_ICON_RING_COMPLETE;
    if (Byte(base, icon + 701u) != 0u) flags |= REFII_WORLD_ICON_RING_MANUAL;
  }
  words[5] = flags;
  words[6] = emotion ? LoadGuestU32(base, icon + 696u) : 0u;
  words[7] = Byte(base, icon + 103u);

  values[0] = LoadGuestF32(base, icon + 112u);
  values[1] = LoadGuestF32(base, icon + 116u);
  values[2] = LoadGuestF32(base, icon + 120u);
  values[3] = pooled ? 1.0f : LoadGuestF32(base, icon + 152u);
  values[4] = LoadGuestF32(base, icon + 48u);
  values[5] = LoadGuestF32(base, icon + 68u);
  values[6] = emotion ? LoadGuestF32(base, icon + 720u) : 0.0f;

  const std::string name = ReadCharString(base, icon + 16u);
  std::string text = name;
  text.push_back('\0');
  if (!pooled) {
    text += ReadLhString(base, icon + 156u);
    text.push_back('\0');
    text += ReadLhString(base, icon + 372u);
  } else {
    text.push_back('\0');
  }
  refii_bridge_world_icon_stated(icon, visible, words, values, text.data(), static_cast<uint32_t>(text.size()));

  if (type < 128u) {
    const uint64_t bit = 1ull << (type & 63u);
    if ((g_types_seen[type >> 6].fetch_or(bit) & bit) == 0u) {
      REXLOG_INFO("Refii world icons: first type {} ({}) stated: '{}' entity {:08X} texture {:08X}", type,
                  ClassName(kind), name, words[4], words[2]);
    }
  }
}

/// The choice record as its creator built it.
void StateChoice(const uint8_t* base, uint32_t record) {
  if (!Plausible(record)) {
    return;
  }
  uint32_t words[REFII_EMOTION_CHOICE_WORDS]{};
  float values[REFII_EMOTION_CHOICE_VALUES]{};
  words[0] = LoadGuestU32(base, record + 0u);
  words[1] = LoadGuestU32(base, record + 8u);
  words[2] = LoadGuestU32(base, record + 12u);
  words[3] = WeakRefTarget(base, record + 20u);
  words[4] = LoadGuestU32(base, record + 56u);
  uint32_t flags = 0u;
  if (Byte(base, record + 60u) != 0u) flags |= REFII_EMOTION_CHOICE_HOLDABLE;
  if (Byte(base, record + 61u) != 0u) flags |= REFII_EMOTION_CHOICE_MANUAL;
  if (Byte(base, record + 62u) != 0u) flags |= REFII_EMOTION_CHOICE_HELD;
  words[5] = flags;
  values[0] = LoadGuestF32(base, record + 32u);
  values[1] = LoadGuestF32(base, record + 36u);
  values[2] = LoadGuestF32(base, record + 40u);
  values[3] = LoadGuestF32(base, record + 64u);

  std::string text = ReadCharString(base, record + 4u);
  text.push_back('\0');
  text += ReadLhString(base, record + 16u);
  text.push_back('\0');
  text += ReadCharString(base, record + 52u);
  refii_bridge_emotion_choice_stated(record, words, values, text.data(), static_cast<uint32_t>(text.size()));
}

uint32_t ChoicesUp() { return refii_bridge_emotion_choices_read(nullptr, nullptr, nullptr, nullptr, 0u, kMaxChoices); }

}  // namespace

// (icon, request): CWorldIconBase's Init, reached by every class's (`vt+4`).
REX_EXTERN(rex_WorldIcon_Init) {
  const uint32_t icon = ctx.r3.u32;
  __imp__rex_WorldIcon_Init(ctx, base);
  StateIcon(base, icon, -1);
}

// (icon, visible, ctx): CWorldIcon3D's Update, and the first half of the emotion icon's.
REX_EXTERN(rex_WorldIcon3D_Update) {
  const uint32_t icon = ctx.r3.u32;
  const int32_t visible = (ctx.r4.u32 & 0xFFu) != 0u ? 1 : 0;
  __imp__rex_WorldIcon3D_Update(ctx, base);
  StateIcon(base, icon, visible);
}

// (icon, visible, ctx): the HUD-element icon's Update.
REX_EXTERN(rex_HudElementIcon_Update) {
  const uint32_t icon = ctx.r3.u32;
  const int32_t visible = (ctx.r4.u32 & 0xFFu) != 0u ? 1 : 0;
  __imp__rex_HudElementIcon_Update(ctx, base);
  StateIcon(base, icon, visible);
}

// (icon): the emotion icon's ring tick, the one writer of its progress and completion.
REX_EXTERN(rex_EmotionIcon_RingTick) {
  const uint32_t icon = ctx.r3.u32;
  const bool was_complete = Plausible(icon) && Byte(base, icon + 700u) != 0u;
  __imp__rex_EmotionIcon_RingTick(ctx, base);
  StateIcon(base, icon, -1);
  if (Plausible(icon) && !was_complete && Byte(base, icon + 700u) != 0u) {
    REXLOG_INFO("Refii emotion choices: ring complete on '{}' (icon {:08X})", ReadCharString(base, icon + 16u), icon);
  }
}

// (out, R, vector, iterator): the registry erases the icon at *iterator.
REX_EXTERN(rex_IconRegistry_Erase) {
  const uint32_t at = ctx.r6.u32;
  if (Plausible(at)) {
    const uint32_t icon = LoadGuestU32(base, at);
    if (icon != 0u) {
      refii_bridge_world_icon_removed(icon);
    }
  }
  __imp__rex_IconRegistry_Erase(ctx, base);
}

// (icon): CWorldIconBase's destructor, which every class's ends in.
REX_EXTERN(rex_WorldIcon_Destroy) {
  refii_bridge_world_icon_removed(ctx.r3.u32);
  __imp__rex_WorldIcon_Destroy(ctx, base);
}

// (E, shared_ptr*): a record joins the choice list.
REX_EXTERN(rex_EmotionChoices_Add) {
  const uint32_t holder = ctx.r4.u32;
  const uint32_t record = Plausible(holder) ? LoadGuestU32(base, holder) : 0u;
  __imp__rex_EmotionChoices_Add(ctx, base);
  if (Plausible(record)) {
    StateChoice(base, record);
    REXLOG_INFO("Refii emotion choices: + '{}' ('{}', record {:08X}, action {}, holdable {}, manual {}; {} up)",
                ReadCharString(base, record + 4u), ReadCharString(base, record + 52u), record,
                LoadGuestU32(base, record), Byte(base, record + 60u), Byte(base, record + 61u), ChoicesUp());
  }
}

// (record): the record's deleter.
REX_EXTERN(rex_EmotionChoice_Delete) {
  const uint32_t record = ctx.r3.u32;
  if (Plausible(record)) {
    const uint32_t up = ChoicesUp();
    refii_bridge_emotion_choice_removed(record);
    if (const uint32_t now = ChoicesUp(); now < up) {
      REXLOG_INFO("Refii emotion choices: - '{}' (record {:08X}; {} up)", ReadCharString(base, record + 4u), record,
                  now);
    }
  }
  __imp__rex_EmotionChoice_Delete(ctx, base);
}
