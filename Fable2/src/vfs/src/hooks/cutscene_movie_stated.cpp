// THE CUTSCENE MOVIE PLAYER'S START, as the guest calls it: `sub_823C0D90(player, name, gendered,
// localised, sound)` (TU1; disc 0x824F74B0), r3 the player (`lhScriptTimerSingleton_GetInstance`), r4 the
// name's CCharString, r5..r7 three bytes. Its callers are the five `GUI.Play*MovieInternal` thunks
// (0x82306548, 0x82306588, 0x823065C8, 0x82306608, 0x82306648), the inbound game packet that mirrors a
// partner's movie (0x82331240) and ONE call that no Lua native states: the wedding, `sub_826B3778` at
// 0x826B40BC / disc `sub_82598A10` at 0x82599354 — `"Marriage.bik"`, gendered 0, localised 1, sound 1,
// made under the open real-estate screen (GitHub #69).
//
// What the call does with the name: localised, the root is the language's mounted `videos` package
// (`sub_82383BB8("videos", &root)` 0x823C0E40 → XLip's `sub_82BF4048`; no package → return 0), else
// `GAME:\` + the videos folder; root + name is stored at player+8 (0x823C0EDC) and tested with
// `GetFileAttributesA` (`sub_82B42340` 0x823C0F00); a missing file is retried lower-cased with
// `game:\data\` rewritten to `epi:\` (0x823C0FC8), and a second miss returns 0. On 1 the player is in
// state 1: its game update `sub_822A9D60` waits for the GUI mode count `**(G+68)` to reach 0
// (0x822A9F84..0x822A9FA4) before it pushes its game-flow command and state 2, and the render update
// `sub_823C1658` then opens Bink on the path at player+8 (0x823C19D8) — the open the host plays from
// (rex_lhBinkPlayer_Open, boot_lifecycle.cpp). When the movie ends (state 4), a path holding "Marriage"
// raises tutorial 65 (`lhTutorials_Display`, 0x822A9EA0..0x822A9F14).
//
// The call is passed through whole; its arguments, its answer and the path it settled on are logged, so
// a movie that never reaches a Bink open says here whether the guest started one.
#include <cstdint>
#include <string>

#include <rex/logging.h>

#include "core/guest_state.h"
#include "hooks/ppc_externs.h"
#include "vfs_hooks_private.h"

PPC_EXTERN_FUNC(__imp__rex_lhCutsceneMoviePlayer_StartMovie);

REX_EXTERN(rex_lhCutsceneMoviePlayer_StartMovie) {
  REX_FUNC_PROLOGUE();
  const uint32_t player = ctx.r3.u32;
  const std::string name = refii::vfs::ReadGuestLhString(base, ctx.r4.u32);
  const uint32_t gendered = ctx.r5.u32 & 0xFFu;
  const uint32_t localised = ctx.r6.u32 & 0xFFu;
  const uint32_t sound = ctx.r7.u32 & 0xFFu;
  __imp__rex_lhCutsceneMoviePlayer_StartMovie(ctx, base);
  REXLOG_INFO("Refii cutscene movie: StartMovie('{}', gendered {}, localised {}, sound {}) answered {} — path '{}'",
              name, gendered, localised, sound, ctx.r3.u32 & 0xFFu,
              player >= 0x10000u ? refii::vfs::ReadGuestLhString(base, player + 8u) : std::string());
}
