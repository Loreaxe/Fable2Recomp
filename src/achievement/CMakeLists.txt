set(REFII_ACHIEVEMENT_ROOT ${CMAKE_CURRENT_LIST_DIR})

# The guest-facing seam only: the kernel hooks write into it, the host bridge reads out of it.
# Definitions, the store, persistence and every pixel live in Godot (Engine_Achievements).
#
# ONE INSTANCE, IN rexruntime. The seam is process state — the write ring, the unlocked set, the
# XDBF — and the kernel (rexruntime) and the host bridge must see the SAME state. As a STATIC
# library linked into both it existed twice, and which copy a call reached depended on load
# order: refii_vfs depends on rexruntime and the host P/Invokes it (bank reads) before it touches
# the bridge, so rexruntime bound its calls to its own copy while the bridge read its own empty
# one. The guest's writes were recorded and never seen (no unlock, no toast, no file), and the
# host's unlocked set never reached the guest's enumerator — so the guest re-wrote the same
# achievement at every boot. On Windows there is no interposition at all and the two copies
# were unconditional.
#
# OBJECT, not STATIC, because WINDOWS_EXPORT_ALL_SYMBOLS builds rexruntime's .def from its own
# objects and never from inside a .lib (see refii_rexglue_export_rexnet_ffi). The bridge takes
# the header only and resolves the calls through rex::runtime.
add_library(refii_achievement OBJECT
    ${REFII_ACHIEVEMENT_ROOT}/src/achievement_runtime.cpp
    # The save a joining player's hero comes from, asked of the host (hero_save_choice.h).
    ${REFII_ACHIEVEMENT_ROOT}/src/hero_save_choice.cpp
)
set_target_properties(refii_achievement PROPERTIES
    POSITION_INDEPENDENT_CODE ON
)
target_include_directories(refii_achievement
    PUBLIC
        ${REFII_ACHIEVEMENT_ROOT}/include
)
