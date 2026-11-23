// refii — which build of the game is running, and the values that differ between builds.
//
// TWO EXECUTABLES ARE SUPPORTED: the title-updated one (TU1) and the disc one (unpatched). The
// update is a full relink, so no guest address is shared, and it renumbered a few things besides
// (screen ids, some struct fields). Functions are bound by NAME through the per-build ReXGlue
// config in Fable2/versions/. Everything else a hook holds as a number goes through this header.
//
// THE TU1 VALUE IS THE NAME. Source keeps writing the TU1 address, wrapped:
//
//   LoadGuestU32(base, refii::guest::Address(0x83496B08u))     // an address we read at
//   switch (refii::guest::ToTu1(vtable)) { case 0x820AE668u: } // a live value we classify
//   refii::guest::Pick(22u, 21u)                               // a non-address: TU1, unpatched
//
// so a grep for the address still finds it, and the unpatched value lives in one table,
// Fable2/versions/Fable2/guest_addresses.inc.
//
// THE PAYLOAD STATES THE BUILD. It is compiled per install from the player's own executable, so
// it knows which one that was (REFII_GUEST_BUILD_UNPATCHED) and says so when it loads; the host
// bridge and Godot, which are compiled once, ask. C linkage because the payload may import no
// C++ symbol from refii_vfs -- see core/payload_bridge.h.

#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/// 0 = TU1 (also the answer before any payload has loaded), 1 = unpatched.
int32_t refii_guest_build(void);
void refii_guest_build_set(int32_t build);

/// The running build's address for a TU1 address. Unchanged under TU1.
uint32_t refii_guest_address(uint32_t tu1_address);

/// The TU1 address for one read from the running guest. Unchanged under TU1.
uint32_t refii_guest_address_to_tu1(uint32_t live_address);

#ifdef __cplusplus
}  // extern "C"

namespace refii::guest {

enum class Build : int32_t { kTu1 = 0, kUnpatched = 1 };

inline Build Current() { return static_cast<Build>(refii_guest_build()); }

inline uint32_t Address(uint32_t tu1_address) { return refii_guest_address(tu1_address); }

inline uint32_t ToTu1(uint32_t live_address) { return refii_guest_address_to_tu1(live_address); }

/// A value that is not an address and differs between the builds.
template <typename T>
inline T Pick(T tu1, T unpatched) {
  return Current() == Build::kUnpatched ? unpatched : tu1;
}

/// In-game GUI screen ids. TU1 added a screen at id 13, so from there on the unpatched build's
/// ids are one lower: abilities is 14 in TU1 and 13 unpatched, real estate 28 and 27. Source
/// uses TU1 numbering throughout; every id READ from the guest is normalised with this, and an
/// id handed TO the guest goes through ScreenId().
inline uint32_t ScreenIdToTu1(uint32_t live_id) {
  return Current() == Build::kUnpatched && live_id >= 13u && live_id < 64u ? live_id + 1u : live_id;
}

inline uint32_t ScreenId(uint32_t tu1_id) {
  return Current() == Build::kUnpatched && tu1_id >= 14u && tu1_id < 64u ? tu1_id - 1u : tu1_id;
}

/// The in-game screen manager, `*(GUI + 56)`. It holds one presentation slot per screen id at
/// +12, thirty in TU1 and twenty-nine unpatched, so a slot is found by the build's own id and
/// every field after the array (TU1 offset 132 and up) is 4 lower unpatched: the shared slot's
/// owning id is +152 and +148, its presentation +156 and +152.
inline uint32_t ScreenManagerField(uint32_t tu1_offset) {
  return Current() == Build::kUnpatched && tu1_offset >= 132u ? tu1_offset - 4u : tu1_offset;
}

/// The offset of TU1 screen id `tu1_id`'s slot in the screen manager, or 0 when the running
/// build has no such screen (TU1's id 13, the DLC "next episode" screen).
inline uint32_t ScreenManagerSlot(uint32_t tu1_id) {
  if (Current() == Build::kUnpatched && tu1_id == 13u) {
    return 0u;
  }
  return 12u + 4u * ScreenId(tu1_id);
}

/// The in-game GUI owner, `*(GUI + 28)`. A byte per screen id sits at +4, so the flag bytes
/// after that block -- TU1 +34 to +38: the expression wheel's open flag at +35, the spell
/// menus' at +36 and +37 -- are one lower unpatched. +40 and up agree.
inline uint32_t GuiOwnerField(uint32_t tu1_offset) {
  return Current() == Build::kUnpatched && tu1_offset >= 34u && tu1_offset < 40u
             ? tu1_offset - 1u
             : tu1_offset;
}

/// A field offset in the front end's main-menu screen object (vtable 0x820D0934 in TU1).
/// TU1 added 12 bytes ahead of offset 72, so from there on every field is 12 lower in the
/// unpatched build: the save-card cursor is +72 and +60, the save names vector +84 and +72, the
/// save records +148 and +136, the cancel flag +206 and +194. Read in both builds' handlers
/// (TU1 `sub_826DEE40` / `sub_826DFD58`, unpatched `sub_826C72A8` / `sub_826C81A0`).
inline uint32_t FrontEndMenuField(uint32_t tu1_offset) {
  return Current() == Build::kUnpatched && tu1_offset >= 72u ? tu1_offset - 12u : tu1_offset;
}

/// One embedded ChapterSave record in the main menu's save vector: 40 bytes in TU1, 36
/// unpatched (TU1 appended a field at +36). `+12` level name and `+24` compatible are the same.
inline uint32_t SaveRecordStride() { return Pick(40u, 36u); }

/// A field offset in the game object, `*(game root + 12)` -- the `this` of rex_lhGameLoop_Tick.
/// TU1 inserted a dword at +256, so its fields from +260 to +271 are 4 lower in the unpatched
/// build: the simulation gate is +260 and +256, the world-ready byte +261 and +257. Below +256
/// and from +272 up the two builds agree. (`lbz 260` against `lbz 256` at the same place in
/// the two builds' ticks.)
inline uint32_t GameField(uint32_t tu1_offset) {
  return Current() == Build::kUnpatched && tu1_offset >= 260u && tu1_offset < 272u
             ? tu1_offset - 4u
             : tu1_offset;
}

/// A named guest address: declared once with its TU1 value, converts to the running build's
/// on every use. For constants that were `constexpr uint32_t kFoo = 0x8...;`.
class Addr {
 public:
  constexpr explicit Addr(uint32_t tu1_address) : tu1_(tu1_address) {}
  operator uint32_t() const { return refii_guest_address(tu1_); }  // NOLINT: implicit by design
  constexpr uint32_t tu1() const { return tu1_; }

 private:
  uint32_t tu1_;
};

}  // namespace refii::guest
#endif
