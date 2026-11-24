// THE FRONT END'S MOD LUA, the interpreter: the vendored Lua 5.1.2 (with the game's `continue`)
// configured as the guest's Gameface Lua (luac32_luaconf.h: 32-bit float numbers, "%.14g"),
// compiled as C++ inside `refii::frontend_lua` the way the decompiler's oracle compiles it
// (luadec_oracle.cpp): the core plus the base, string, table and math libraries. The front end's
// state and its natives are mod_frontend_lua.cpp's.
//
// The sources are staged with the hooks for the player's build, uncompiled: this file includes
// them by path (Fable2/src/hooks/CMakeLists.txt, cmake/refii_stage_hook_sources.cmake).

#include <assert.h>
#include <ctype.h>
#include <errno.h>
#include <limits.h>
#include <locale.h>
#include <math.h>
#include <setjmp.h>
#include <stdarg.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#include "../../../lua/src/tools/luadec/luac32_luaconf.h"

#define LUA_CORE
// clang's <stddef.h> re-emits `namespace std { nullptr_t }` inside the namespace under
// _MSC_EXTENSIONS (see luadec_oracle.cpp).
#pragma push_macro("_MSC_EXTENSIONS")
#undef _MSC_EXTENSIONS
namespace refii::frontend_lua {
#include "../../../lua/thirdparty/lua512/src/lapi.c"
#include "../../../lua/thirdparty/lua512/src/lcode.c"
#include "../../../lua/thirdparty/lua512/src/ldebug.c"
#include "../../../lua/thirdparty/lua512/src/ldo.c"
#include "../../../lua/thirdparty/lua512/src/ldump.c"
#include "../../../lua/thirdparty/lua512/src/lfunc.c"
#include "../../../lua/thirdparty/lua512/src/lgc.c"
#include "../../../lua/thirdparty/lua512/src/llex.c"
#include "../../../lua/thirdparty/lua512/src/lmem.c"
#include "../../../lua/thirdparty/lua512/src/lobject.c"
#include "../../../lua/thirdparty/lua512/src/lopcodes.c"
#include "../../../lua/thirdparty/lua512/src/lparser.c"
#include "../../../lua/thirdparty/lua512/src/lstate.c"
#include "../../../lua/thirdparty/lua512/src/lstring.c"
#include "../../../lua/thirdparty/lua512/src/ltable.c"
#include "../../../lua/thirdparty/lua512/src/ltm.c"
#include "../../../lua/thirdparty/lua512/src/lundump.c"
#include "../../../lua/thirdparty/lua512/src/lvm.c"
#include "../../../lua/thirdparty/lua512/src/lzio.c"
#include "../../../lua/thirdparty/lua512/src/lauxlib.c"
#include "../../../lua/thirdparty/lua512/src/lbaselib.c"
#include "../../../lua/thirdparty/lua512/src/lmathlib.c"
#include "../../../lua/thirdparty/lua512/src/lstrlib.c"
#include "../../../lua/thirdparty/lua512/src/ltablib.c"
}  // namespace refii::frontend_lua
#pragma pop_macro("_MSC_EXTENSIONS")
