// THE PORTED SCREENS (S7). Each handler slot below is a full replacement: it keeps the guest's
// statements of the screen's data and its game side effects, as direct calls, and drops what
// only draws or animates. Godot drives the rest through the levers at the end of each file
// (guest_tick_pump.cpp dispatches them). The handler objects stay registered.
//
// Slot layout (vtable): +8 OnEnter, +16 Update, +20 OnMenuEvent(h, event), +24
// OnScreenAction(h, code, player), +36 OnExit. TU1 / disc addresses on each function.
#include <algorithm>
#include <cctype>
#include <cstdint>
#include <cstring>
#include <initializer_list>
#include <string>

#include <rex/logging.h>

#include "core/guest_build.h"
#include "core/refii_mod.h"
#include "hooks/gui_screen_stub_support.h"
#include "hooks/gui_screen_stubs.h"
#include "hooks/ppc_externs.h"
#include "vfs_hooks_private.h"

PPC_EXTERN_FUNC(rex_lhGuiManager_FindScreenHandler);
PPC_EXTERN_FUNC(rex_lhProfile_SetGamma);
PPC_EXTERN_FUNC(rex_lhAudio_SetSfxVolume);
PPC_EXTERN_FUNC(rex_lhAudio_SetMusicVolume);
PPC_EXTERN_FUNC(rex_lhAudio_SetSpeechVolume);
PPC_EXTERN_FUNC(rex_lhAudio_SetSpeakerMode);

using namespace refii::vfs::stub;
using refii::guest::Address;
using refii::mods::LoadU32;
using refii::mods::StoreU32;

// ---- OPTIONS (18; disc 17) — vtable 0x820D013C / 0x820C0534 ---------------------------------
//
// Pages embedded in the handler: h+72 Game (vt 0x820F03EC / 0x820E44E4), h+184 Controls, h+312
// Video, h+408 Audio; the book at h+28. Page slot +4 is the page's reader/populate: it states the
// committed values through the pickbox SetIndex and slider SetValue Godot reads.

namespace {

constexpr uint32_t kOptionsGamePage = 72u;
constexpr uint32_t kOptionsControlsPage = 184u;
constexpr uint32_t kOptionsAudioPage = 408u;

}  // namespace

// OnEnter TU1 0x826D9D40 / disc 0x82582968. Kept, in the guest's order: h+4 = pause root's owner
// (`FindScreenHandler(QM,0)+4`, 0x826D9D80 / 0x825829B4); the four pages pushed (`sub_82B49750` /
// 0x8228A900); the book `sub_82990610(h+28, &pages, "Foreground.Book", pres, 1)` (0x826D9E4C /
// 0x828CE060), which runs every page's +4 reader; h+69 = 0; the tab strip `sub_826B8348(*(G+76), 8)`
// (0x826D9E60 / 0x8240B218); h+552 = (h+4 != 0) + 1; the gold text `sub_8246B5E8(pres, 0x8349B4D8, 1)`
// (0x826D9E94 / 0x82837218 with 0x83498AC0). Dropped: the open sound (vf+4, 0x826D9D5C) and
// `g_MenuFormatter:FadeOutTopItemEarly()` (0x826D9DB4).
REX_EXTERN(rex_lhGuiOptionsScreen_OnEnter) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  const uint32_t pages = s.data;         // {?, begin, end, capacity}
  const uint32_t item = s.data + 0x10u;  // the page pointer handed to the push
  const uint32_t text = s.data + 0x20u;  // a CCharString handle and its characters
  const uint32_t g = Gui(base);
  const uint32_t pres = BookPresentation(base);

  PPCContext find = ctx;
  find.r1.u32 = s.frame;
  find.r3.u32 = LoadU32(base, g + 28u);
  find.r4.u32 = 0u;
  rex_lhGuiManager_FindScreenHandler(find, base);
  const uint32_t root = find.r3.u32;
  StoreU32(base, h + 4u, Plausible(root) ? LoadU32(base, root + 4u) : 0u);

  for (uint32_t i = 0; i < 16u; i += 4u) {
    StoreU32(base, pages + i, 0u);
  }
  for (uint32_t page : {kOptionsGamePage, kOptionsControlsPage, 312u, kOptionsAudioPage}) {
    StoreU32(base, item, h + page);
    Call(ctx, base, s.frame, 0x82B49750u, {pages, item});
  }
  MakeString(ctx, base, s.frame, text, text + 0x10u, "Foreground.Book");
  Call(ctx, base, s.frame, 0x82990610u, {h + 28u, pages, text, pres, 1u});
  base[h + 69u] = 0u;
  Call(ctx, base, s.frame, 0x826B8348u, {LoadU32(base, g + 76u), 8u});
  StoreU32(base, h + 552u, LoadU32(base, h + 4u) != 0u ? 2u : 1u);
  Call(ctx, base, s.frame, 0x8246B5E8u, {pres, Address(0x8349B4D8u), 1u});
  DropString(ctx, base, s.frame, text);
  const uint32_t storage = LoadU32(base, pages + 4u);
  if (storage != 0u) {
    Call(ctx, base, s.frame, 0x8221BD38u, {storage});  // Render_FreeD3DObject
  }
  Said("options (18)", "OnEnter", h, LoadU32(base, h + 4u));
}

// Update TU1 0x826D9EB8 / disc 0x82582AE0: the base child tick `sub_826D5D00` and the book update
// `sub_82990730`, whose current page's +12 is the Video page's per-frame brightness preview
// (`sub_829C0AB0`). Godot previews and commits gamma through `Options:` (DispatchOptions). Nothing kept.
REX_EXTERN(rex_lhGuiOptionsScreen_Update) {
  REX_FUNC_PROLOGUE();
}

// OnMenuEvent TU1 0x826DA038 / disc 0x82582C60: events 0x8C / 0x8D play sounds. Nothing kept.
REX_EXTERN(rex_lhGuiOptionsScreen_OnMenuEvent) {
  REX_FUNC_PROLOGUE();
}

// OnScreenAction TU1 0x826DA228 / disc 0x82582E50: '$' opens a page (Lua, focus, slide), the open
// page's input `sub_829907F0` and close `sub_826D9F28`; B at the root and LB / RB are the spine
// calls Godot makes as `PauseBack` / `PauseCycle:<p>:…`. Godot owns the page level and commits
// through `Options:` (DispatchOptions). Nothing kept.
REX_EXTERN(rex_lhGuiOptionsScreen_OnScreenAction) {
  REX_FUNC_PROLOGUE();
  Said("options (18)", "OnScreenAction", ctx.r3.u32, ctx.r4.u32, ctx.r5.u32);
}

// OnExit TU1 0x826D9EF0 / disc 0x82582B18: kept whole — the base exit `sub_826D5C30` / 0x82583490
// (the tab strip's hide) and `sub_829912B0(h+28)` / 0x828CED00 (every page's +8: the Audio page
// stops its preview sound).
REX_EXTERN(rex_lhGuiOptionsScreen_OnExit) {
  REX_FUNC_PROLOGUE();
  const uint32_t h = ctx.r3.u32;
  const Scratch s = Claim(ctx);
  Call(ctx, base, s.frame, 0x826D5C30u, {h});
  Call(ctx, base, s.frame, 0x829912B0u, {h + 28u});
  Said("options (18)", "OnExit", h);
}

namespace refii::vfs {

bool DispatchOptions(PPCContext& ctx, uint8_t* base, uint32_t player, const std::string& setting, double value) {
  const Scratch s = Claim(ctx);
  const uint32_t g = Gui(base);
  const uint32_t qm = Plausible(g) ? LoadU32(base, g + 28u) : 0u;
  const auto handler = [&](uint32_t screen) {
    PPCContext find = ctx;
    find.r1.u32 = s.frame;
    find.r3.u32 = qm;
    find.r4.u32 = refii::guest::ScreenId(screen);
    rex_lhGuiManager_FindScreenHandler(find, base);
    return find.r3.u32;
  };
  const uint32_t options = handler(18u);
  if (!Plausible(qm) || !Plausible(options) || LoadU32(base, options) != Address(0x820D013Cu)) {
    REXLOG_INFO("Refii guest-tick pump: Options:{} — no options handler, dropped", setting);
    return true;
  }

  // The local record and its entity, as every page commit inlines them (TU1 0x829BF6A4..0x829BF71C,
  // disc 0x828D8AFC..): `sub_821C7CE0` / 0x821C7D38 (key 1 when NET+0x34 && NET+0x35, else 2), then
  // the handle at record+0xC, `sub_8229EE78` / 0x82455F20. User = record+0x28 (0x829C0854).
  const uint32_t record = Call(ctx, base, s.frame, 0x821C7CE0u, {});
  const uint32_t entity = Plausible(record) ? Call(ctx, base, s.frame, 0x8229EE78u, {record}) : 0u;
  const uint32_t user = Plausible(record) ? LoadU32(base, record + 0x28u) : 0u;
  const uint32_t game_root = LoadU32(base, Address(0x83496AB8u));
  const uint32_t game = Plausible(game_root) ? LoadU32(base, game_root + 0xCu) : 0u;
  const uint32_t profiles = Plausible(game) ? LoadU32(base, game + 0x94u) : 0u;  // SaveMgr, 0x829C0858
  const uint32_t audio = LoadU32(base, Address(0x83496D9Cu));                     // 0x829C1614
  const uint32_t comp33 = Plausible(entity) ? refii_mod_component(base, entity, 33u) : 0u;
  const uint32_t index = static_cast<uint32_t>(value);

  const auto float_call = [&](void (*fn)(PPCContext&, uint8_t*), uint32_t r3, uint32_t r4, double f1) {
    PPCContext work = ctx;
    work.r1.u32 = s.frame;
    work.r3.u32 = r3;
    work.r4.u32 = r4;
    work.f1.f64 = f1;
    fn(work, base);
  };
  // LIVE brightness, as the Video commit writes it (0x829C0864..0x829C08A4 / disc 0x828D9C44..
  // 0x828D9C84): S = *(ROOT->vf+0x14() + 4); S+0x14 = 1 + 0.4·(1 − 2g).
  const auto live_brightness = [&](float gamma) {
    PPCContext work = ctx;
    work.r1.u32 = s.frame;
    refii::mods::CallVirtual(work, base, game_root, 5u);
    const uint32_t camera = Plausible(work.r3.u32) ? LoadU32(base, work.r3.u32 + 4u) : 0u;
    if (!Plausible(camera)) {
      return 0.0f;
    }
    const float bright = static_cast<float>(static_cast<double>((1.0f - gamma * 2.0f) * 0.4f) + 1.0);
    refii::mods::StoreF32(base, camera + 0x14u, bright);
    return bright;
  };
  // A page's +4 reader, as the guest's own B runs it (Game 0x829BFAC4, Controls 0x829C0368, Audio
  // 0x829C1710): re-states the committed values through the widgets.
  const auto restate = [&](uint32_t page) { CallSlot(ctx, base, s.frame, options + page, 1u, {}); };
  const float level = std::clamp(static_cast<float>(value), 0.0f, 1.0f);  // slider `sub_82881E38` 0..1

  if (setting == "gamma" || setting == "gammalive") {
    // Commit `sub_829C07D0` / 0x828D9BB0: `sub_82443A08(SaveMgr, user, g)` / 0x824E7000
    // (XUserWriteProfileSettings 0x63E83FFF inside), then LIVE. `gammalive` is LIVE alone: the
    // Video page's per-frame +12 `sub_829C0AB0` and its revert on defocus `sub_829C09F0`.
    if (setting == "gamma") {
      if (!Plausible(profiles) || !Plausible(record)) {
        REXLOG_INFO("Refii guest-tick pump: Options:gamma — no profile record, dropped");
        return true;
      }
      float_call(rex_lhProfile_SetGamma, profiles, user, level);
    }
    const float bright = live_brightness(level);
    REXLOG_INFO("Refii guest-tick pump: Options:{} {:.2f} — brightness {:.3f}{}", setting, level, bright,
                setting == "gamma" ? ", profile written" : "");
    return true;
  }

  if (setting == "sfx" || setting == "music" || setting == "speech" || setting == "speaker") {
    // The leaves `sub_829C15E8` / 0x828DAA78 calls with AUDIO = *(0x83496D9C): sfx 0x829FD678, music
    // 0x829FD888, speech 0x829FD710 (f1), speaker 0x829FD920 (r4) — the Audio page's left/right.
    if (setting == "sfx") float_call(rex_lhAudio_SetSfxVolume, audio, 0u, level);
    if (setting == "music") float_call(rex_lhAudio_SetMusicVolume, audio, 0u, level);
    if (setting == "speech") float_call(rex_lhAudio_SetSpeechVolume, audio, 0u, level);
    if (setting == "speaker") float_call(rex_lhAudio_SetSpeakerMode, audio, index, 0.0);
    if (setting == "speaker") {
      REXLOG_INFO("Refii guest-tick pump: Options:speaker {} — applied live", index);
    } else {
      REXLOG_INFO("Refii guest-tick pump: Options:{} {:.2f} — applied live", setting, level);
    }
    return true;
  }

  if (setting == "subtitles") {
    // Game commit 0x829BF85C..0x829BF8A4 / 0x828D8CB8..0x828D8CFC: SUBOBJ = *(*(G+0x20)+0x44); on a
    // change SUBOBJ+4 = v, and turning off with a subtitle up (SUBOBJ+0x20) hides it now,
    // `sub_827E8390(SUBOBJ, 1)` / 0x827CC6A8. Gated like the commit on comp33 (0x829BF834).
    const uint32_t holder = LoadU32(base, g + 0x20u);
    const uint32_t subtitles = Plausible(holder) ? LoadU32(base, holder + 0x44u) : 0u;
    if (!Plausible(comp33) || !Plausible(subtitles)) {
      REXLOG_INFO("Refii guest-tick pump: Options:subtitles — no hero or subtitle object, dropped");
      return true;
    }
    const uint8_t on = index == 1u ? 1u : 0u;
    if (base[subtitles + 4u] != on) {
      base[subtitles + 4u] = on;
      if (on == 0u && LoadU32(base, subtitles + 0x20u) != 0u) {
        Call(ctx, base, s.frame, 0x827E8390u, {subtitles, 1u});
      }
    }
    restate(kOptionsGamePage);
    REXLOG_INFO("Refii guest-tick pump: Options:subtitles {}", on);
    return true;
  }

  if (setting == "invert") {
    // Controls commit `sub_829C00E0` / 0x828D9658: comp33+0x2A = (idx == 1) (0x829C02B8 / 0x828D9830).
    if (!Plausible(comp33)) {
      REXLOG_INFO("Refii guest-tick pump: Options:invert — no hero, dropped");
      return true;
    }
    base[comp33 + 0x2Au] = index == 1u ? 1u : 0u;
    restate(kOptionsControlsPage);
    REXLOG_INFO("Refii guest-tick pump: Options:invert {}", index == 1u ? 1 : 0);
    return true;
  }

  if (setting == "breadcrumbs" || setting == "tutorials" || setting == "orbs" || setting == "autojoin") {
    // The Game page's rows 1..4 exist only while the pause root's owner is player one
    // (`FindScreenHandler(QM,0)+4 == 0`, 0x829BF8B4..0x829BF8C8 / 0x828D8D0C..).
    const uint32_t root = handler(0u);
    if (player != 1u || (Plausible(root) && LoadU32(base, root + 4u) != 0u) || !Plausible(comp33)) {
      REXLOG_INFO("Refii guest-tick pump: Options:{} for player {} — player one's row, refused", setting, player);
      return true;
    }
    if (setting == "breadcrumbs" && index <= 4u) {
      Call(ctx, base, s.frame, 0x829BF2F8u, {0u, index});  // 0x829BF8F4 / 0x828D8750
    } else if (setting == "tutorials") {
      base[comp33 + 0x28u] = index == 1u ? 1u : 0u;  // 0x829BF934 / 0x828D8D8C
    } else if (setting == "orbs" && index <= 2u) {
      Call(ctx, base, s.frame, 0x829BEAF8u, {0u, index});  // 0x829BF954 / 0x828D7F50
    } else if (setting == "autojoin") {
      Call(ctx, base, s.frame, 0x829BEEA8u, {0u, index != 0u ? 1u : 0u});  // 0x829BF980 / 0x828D8300
    } else {
      REXLOG_INFO("Refii guest-tick pump: Options:{}:{} out of range — dropped", setting, index);
      return true;
    }
    restate(kOptionsGamePage);
    REXLOG_INFO("Refii guest-tick pump: Options:{} {}", setting, index);
    return true;
  }

  REXLOG_INFO("Refii guest-tick pump: Options:{} is not a setting — dropped", setting);
  return true;
}

bool DispatchOptionsAudioCommit(PPCContext& ctx, uint8_t* base, const float levels[3], uint32_t speaker) {
  // The Audio commit `sub_829C1488(page, 1)` / 0x828DA918: the four leaves (`sub_829C15E8`), then
  // `0x82443B00(SaveMgr, user, r5 = sfx<<32 | music, r6 = speech<<32 | speaker)` / 0x824E70F8
  // (0x829C1564..0x829C15DC), which writes the profile when any differs.
  const Scratch s = Claim(ctx);
  const uint32_t g = Gui(base);
  const uint32_t qm = Plausible(g) ? LoadU32(base, g + 28u) : 0u;
  PPCContext find = ctx;
  find.r1.u32 = s.frame;
  find.r3.u32 = qm;
  find.r4.u32 = refii::guest::ScreenId(18u);
  rex_lhGuiManager_FindScreenHandler(find, base);
  const uint32_t options = find.r3.u32;
  const uint32_t record = Call(ctx, base, s.frame, 0x821C7CE0u, {});
  const uint32_t game_root = LoadU32(base, Address(0x83496AB8u));
  const uint32_t game = Plausible(game_root) ? LoadU32(base, game_root + 0xCu) : 0u;
  const uint32_t profiles = Plausible(game) ? LoadU32(base, game + 0x94u) : 0u;
  if (!Plausible(qm) || !Plausible(options) || LoadU32(base, options) != Address(0x820D013Cu) ||
      !Plausible(record) || !Plausible(profiles)) {
    REXLOG_INFO("Refii guest-tick pump: Options:audio — no options handler or profile record, dropped");
    return true;
  }
  const uint32_t audio = LoadU32(base, Address(0x83496D9Cu));
  void (*const leaves[3])(PPCContext&, uint8_t*) = {rex_lhAudio_SetSfxVolume, rex_lhAudio_SetMusicVolume,
                                                    rex_lhAudio_SetSpeechVolume};
  for (int i = 0; i < 3; ++i) {
    PPCContext work = ctx;
    work.r1.u32 = s.frame;
    work.r3.u32 = audio;
    work.f1.f64 = levels[i];
    leaves[i](work, base);
  }
  PPCContext mode = ctx;
  mode.r1.u32 = s.frame;
  mode.r3.u32 = audio;
  mode.r4.u32 = speaker;
  rex_lhAudio_SetSpeakerMode(mode, base);

  uint32_t bits[3];
  std::memcpy(bits, levels, sizeof(bits));
  PPCContext save = ctx;
  save.r1.u32 = s.frame;
  save.r3.u32 = profiles;
  save.r4.u32 = LoadU32(base, record + 0x28u);
  save.r5.u64 = (static_cast<uint64_t>(bits[0]) << 32) | bits[1];
  save.r6.u64 = (static_cast<uint64_t>(bits[2]) << 32) | speaker;
  refii::mods::CallGuest(save, base, Address(0x82443B00u));
  CallSlot(ctx, base, s.frame, options + kOptionsAudioPage, 1u, {});  // the reader, 0x829C1710
  REXLOG_INFO("Refii guest-tick pump: Options:audio {:.2f} {:.2f} {:.2f} speaker {} — applied, profile written",
              levels[0], levels[1], levels[2], speaker);
  return true;
}

}  // namespace refii::vfs
