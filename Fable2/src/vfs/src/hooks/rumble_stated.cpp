// THE RUMBLE SWITCH, STATED. The Lua native `SetRumbleEnabled` (TU1 0x823B3A78, disc 0x823A5488)
// is the switch's one writer — `stb r3, byte_8331924F` (disc `byte_833191E2`), three instructions
// — and the byte is 1 in .data. Its argument is kept under stated-value key 14 (payload_bridge.h):
// bit 1 set once the call has been made, bit 0 the switch it set.
#include <cstdint>

#include "vfs_hooks_private.h"
#include "core/payload_bridge.h"
#include "hooks/ppc_externs.h"

REX_EXTERN(rex_lhLua_SetRumbleEnabled) {
    REX_FUNC_PROLOGUE();
    const uint32_t enabled = (ctx.r3.u32 & 0xFFu) != 0u ? 1u : 0u;
    __imp__rex_lhLua_SetRumbleEnabled(ctx, base);
    refii_bridge_stated_value(14u, 2u | enabled);
}
