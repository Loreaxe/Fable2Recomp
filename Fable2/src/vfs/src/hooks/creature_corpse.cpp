#include "vfs_hooks_private.h"

#include <cstdint>

#include "hooks/ppc_externs.h"

// Which dead creatures the guest leaves lying (src/core/creature_corpse_state.cpp). The note is
// `CECCreature_FinishDeath`, hooked for the mod events in mod_events.cpp; this file holds the
// forget.
//
//   CECCreature constructor, TU1 `sub_823A3148` / disc `sub_82431700` (size 0x158 both;
//   one caller each, the component factory `sub_82631390` / `sub_82645A98`, so no folded body):
//       r3 = the component. Stores the vtable (TU1 `off_82004FA8` at 0x823a3188, disc
//       `off_82004FC8` at 0x82431740) and alive = 1 at +0x18 (0x823a318c, disc 0x82431744).
//       A creature built at an address a corpse once had is not that corpse.

REX_EXTERN(rex_CECCreature_Ctor) {
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();
  refii::vfs::ForgetCreatureCorpse(ctx.r3.u32);
  REFII_VFS_NATIVE_PASSTHROUGH(rex_CECCreature_Ctor);
}
