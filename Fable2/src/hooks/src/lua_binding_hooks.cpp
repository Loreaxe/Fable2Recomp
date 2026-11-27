// A Lua binding of an entity's vector getter, asked without its entity.
//
// THE BINDING. One folded body serves every `vector = object:Method(entity)` binding of the game VM
// (TU1 `sub_8281DBF8`, disc `sub_822BF168`, 0x68 bytes each, registered from 20+ component tables):
//   0x8281DC0C  bl lua_touserdata(L, -10003)      the closure's upvalue: {object, method}
//   0x8281DC24  bl lua_touserdata(L, 1)           the entity reference
//   0x8281DC28  lwz r3, 0(r3)                     read with NO test of the answer
//   0x8281DC2C  bl lhScriptEntityRef_Resolve
//   0x8281DC40  bctrl                             method(&vector, object, entity)
// (disc 0x822BF17C / 0x822BF194 / 0x822BF198 / 0x822BF19C / 0x822BF1B0). `lua_touserdata` answers 0
// for anything that is not a userdata, so a script that passes nil reads guest address 0: a fault on
// the console as here, where a script error would be a caught Lua error.
//
// WHERE IT WAS HIT. An online joiner, while joining (twice, coop3 and int4): the node for player two
// is made on a level load (`lhScene_UpdateNodes` 0x82316C10 → `lhScene_CreateNode` → 0x82316FC4
// `lhSceneNode_UpdateTerrain` → 0x8238AE24 the camera manager's settle pass `sub_82482EB0`), the
// settle pass runs the active mode's Lua FrameUpdate under a protected call (0x82482FC0
// `sub_8219A758(wrapper+0x2C, wrapper+0x18)`), and that script reached this binding with no entity
// (guest fault at 0x8281DC28, lr 0x8281DC28). Which script line passes nil is NOT known.
//
// THE ANSWER. With no entity the binding returns no value: the script gets nil where it expected a
// vector and its next use of it is an ordinary Lua error inside the caller's protected call, which
// names the script and line. The bound method's address is logged, which names the getter.

#include <atomic>
#include <cstdint>

#include <rex/logging.h>
#include <rex/ppc/context.h>
#include <rex/ppc/func.h>

#include "core/guest_build.h"
#include "core/guest_state.h"

extern "C" REX_FUNC(__imp__rex_lhLuaBind_EntityVector);

namespace {

/// `LUA_GLOBALSINDEX - 1`: the closure's first upvalue, as the binding asks for it (`li r4,-0x2713`).
constexpr uint32_t kUpvalueOne = static_cast<uint32_t>(-10003);

std::atomic<uint32_t> g_missing{0};

/// `lua_touserdata(L, index)` (TU1 0x822280F8 / disc 0x822281F8) on a copy of the caller's context.
uint32_t ToUserdata(const PPCContext& ctx, uint8_t* base, uint32_t state, uint32_t index) {
  static PPCFunc* const to_userdata = refii::vfs::FindGuestFunction(refii::guest::Address(0x822280F8u));
  if (to_userdata == nullptr) {
    return 0u;
  }
  PPCContext work = ctx;
  work.r3.u64 = state;
  work.r4.u64 = static_cast<uint64_t>(static_cast<int64_t>(static_cast<int32_t>(index)));
  to_userdata(work, base);
  return work.r3.u32;
}

}  // namespace

REX_EXTERN(rex_lhLuaBind_EntityVector) {
  const uint32_t state = ctx.r3.u32;
  if (ToUserdata(ctx, base, state, 1u) != 0u) {
    __imp__rex_lhLuaBind_EntityVector(ctx, base);
    return;
  }
  const uint32_t count = g_missing.fetch_add(1u, std::memory_order_relaxed) + 1u;
  if (count <= 16u || (count & 0xFFu) == 0u) {
    const uint32_t bound = ToUserdata(ctx, base, state, kUpvalueOne);
    const uint32_t method = bound != 0u ? refii::vfs::LoadGuestU32(base, bound + 4u) : 0u;
    REXLOG_WARN("lua binding: a vector getter (method 0x{:08X}) was asked with no entity — answered nil "
                "(#{}; the script's next line is a Lua error naming it)", method, count);
  }
  ctx.r3.u64 = 0u;
}
