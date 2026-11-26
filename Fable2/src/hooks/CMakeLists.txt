# Unified native PPC hook object target.
#
# Feature modules expose hook source files and runtime libraries. This target is
# the single place that compiles those strong PPC overrides into object files for
# `refii_generated`, matching the generated weak-symbol override model.

set(REFII_HOOK_SOURCES
    ${REFII_LUA_HOOK_SOURCES}
    ${REFII_VFS_HOOK_SOURCES}
    ${CMAKE_CURRENT_SOURCE_DIR}/src/xstorage_hooks.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/unlock_hooks.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/coop_player_hooks.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/coop_hero_hooks.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/camera_statements.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/coop_dog_hooks.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/coop_ui_hooks.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/xsession_leave_hooks.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/property_hooks.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/character_morph_hooks.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/coop_world_hooks.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/firearm_hooks.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/exp_orb_hooks.h
    ${CMAKE_CURRENT_SOURCE_DIR}/src/exp_orb_hooks.cpp
)

# The shared runtime's include directory AHEAD of the SDK's: it carries the payload's
# rex/thread/mutex.h, which must win over the SDK header of the same name.
set(REFII_HOOK_INCLUDE_DIRS
    ${REFII_LUA_INCLUDE_DIRS}
    ${REFII_VFS_INCLUDE_DIRS}
    ${REFII_RUNTIME_INCLUDE_DIRS}
)

set(REFII_HOOK_LIBRARIES
    ${REFII_LUA_LIBRARIES}
    ${REFII_VFS_LIBRARIES}
)

# The front end's Lua interpreter: included by mod_frontend_lua_vm.cpp, never compiled on its own,
# and that one unit takes the vendored sources' warnings as they are (as the oracle's does).
set_source_files_properties(${REFII_LUA_FRONTEND_VM_PARTS} PROPERTIES HEADER_FILE_ONLY TRUE)
list(APPEND REFII_HOOK_SOURCES ${REFII_LUA_FRONTEND_VM_PARTS})
foreach(_refii_vm_unit IN LISTS REFII_VFS_HOOK_SOURCES)
    if(_refii_vm_unit MATCHES "/mod_frontend_lua_vm\\.cpp$")
        if(MSVC)
            set_source_files_properties(${_refii_vm_unit} PROPERTIES COMPILE_OPTIONS "/w")
        else()
            set_source_files_properties(${_refii_vm_unit} PROPERTIES COMPILE_OPTIONS "-w")
        endif()
    endif()
endforeach()

list(FILTER REFII_HOOK_SOURCES EXCLUDE REGEX "^$")

if(REFII_HOOK_SOURCES)
    add_library(refii_hooks OBJECT
        ${REFII_HOOK_SOURCES}
    )
    set_target_properties(refii_hooks PROPERTIES
        POSITION_INDEPENDENT_CODE ON
    )
    target_include_directories(refii_hooks
        PRIVATE
            ${REFII_HOOK_INCLUDE_DIRS}
            # Hook sources include Tracy's disabled macros even when profiling is off.
            ${REXSDK_DIR}/thirdparty/tracy/public
    )
    target_link_libraries(refii_hooks
        PRIVATE
            ${REFII_HOOK_LIBRARIES}
            rex::runtime
    )

    # THE PAYLOAD LOGS THROUGH C. See the shadowed rex/logging/macros.h: under this define
    # REX_LOG_IMPL formats with fmt payload-side and hands the host a finished char*, so no
    # spdlog::logger* and no fmt arg pack crosses a boundary the player's compiler built.
    target_compile_definitions(refii_hooks PRIVATE REFII_PAYLOAD_LOG_BRIDGE=1)

    # NO TRACY IN THE PAYLOAD. TRACY_ENABLE arrives as an INTERFACE define from rex::runtime,
    # so it cannot be dropped by not asking for it — -U undoes it after the fact (compile
    # options are emitted after definitions). ZoneScoped and friends then compile to nothing,
    # which is what a shipped payload wants anyway: the profiler is a dev instrument, and its
    # seven C++ entries would otherwise cross a boundary the player's compiler built.
    target_compile_options(refii_hooks PRIVATE "-UTRACY_ENABLE" "-UTRACY_MANUAL_LIFETIME" "-UTRACY_DELAYED_INIT" "-UTRACY_ON_DEMAND" "-UTRACY_FIBERS" "-UTRACY_IMPORTS" "-UREXGLUE_ENABLE_PROFILING")

    set(REFII_HOOK_OBJECTS
        $<TARGET_OBJECTS:refii_hooks>
        PARENT_SCOPE
    )

    # THE PAYLOAD/HOST ABI RULE, CHECKED RATHER THAN REMEMBERED.
    #
    # payload_bridge.h states it and Fable2/src/vfs/CMakeLists.txt restates it, and it was still
    # broken seven times, because Linux cannot see it being broken: both ends are Itanium
    # there, so a hook calling straight into refii_vfs links and runs. It surfaces only on
    # Windows, at the end of the player's install-time build. See
    # cmake/refii_check_payload_abi.cmake.
    #
    # Off with -DREFII_CHECK_PAYLOAD_ABI=OFF, and skipped when no llvm-nm is present: a guard
    # rail must not be the reason a build cannot run.
    option(REFII_CHECK_PAYLOAD_ABI
        "Fail the build if a payload hook imports a C++ symbol from a host library" ON)

    find_program(REFII_NM NAMES llvm-nm llvm-nm-20 llvm-nm-19 llvm-nm-18)
    find_program(REFII_CXXFILT NAMES llvm-cxxfilt llvm-cxxfilt-20 llvm-cxxfilt-19 c++filt)
    find_program(REFII_UNDNAME NAMES llvm-undname)

    if(REFII_CHECK_PAYLOAD_ABI AND NOT REFII_NM)
        message(STATUS
            "Refii: no llvm-nm found - the payload/host ABI check is skipped. A hook that "
            "imports a C++ symbol from refii_vfs will not be caught until the Windows payload "
            "link fails.")
    elseif(REFII_CHECK_PAYLOAD_ABI)
        # $<CONFIG> IS IN THE NAME. A multi-config generator evaluates this once per
        # configuration and file(GENERATE) refuses one path with two bodies -- the same wall
        # payload_include_dirs hit.
        set(_refii_abi_objects
            "${CMAKE_BINARY_DIR}/payload_abi_objects_$<CONFIG>.txt")
        # The shared payload sources (src/payload, refii_payload_core) are checked with the
        # hooks: they are compiled into the same module on the same terms.
        file(GENERATE OUTPUT "${_refii_abi_objects}"
             CONTENT "$<JOIN:$<TARGET_OBJECTS:refii_hooks>,\n>\n$<JOIN:$<TARGET_OBJECTS:refii_payload_core>,\n>\n")

        # Stamped, so it re-runs only when a hook object changed. A bare custom target would
        # re-read every object on every build.
        set(_refii_abi_stamp "${CMAKE_CURRENT_BINARY_DIR}/payload_abi_checked_$<CONFIG>")
        add_custom_command(
            OUTPUT "${_refii_abi_stamp}"
            COMMAND ${CMAKE_COMMAND}
                -DOBJECT_LIST=${_refii_abi_objects}
                -DCACHE_DIR=${CMAKE_CURRENT_BINARY_DIR}/abi_cache_$<CONFIG>
                -DNM=${REFII_NM}
                -DCXXFILT=${REFII_CXXFILT}
                -DUNDNAME=${REFII_UNDNAME}
                -P "${CMAKE_SOURCE_DIR}/cmake/refii_check_payload_abi.cmake"
            COMMAND ${CMAKE_COMMAND} -E touch "${_refii_abi_stamp}"
            DEPENDS $<TARGET_OBJECTS:refii_hooks> $<TARGET_OBJECTS:refii_payload_core> "${_refii_abi_objects}"
            COMMENT "Checking the payload imports no C++ symbol from a host library"
            VERBATIM
        )
        add_custom_target(refii_payload_abi_check ALL DEPENDS "${_refii_abi_stamp}")
        add_dependencies(refii_payload_abi_check refii_hooks refii_payload_core)
    endif()

    # The same objects as an archive, for the install-time payload build to link.
    #
    # It cannot consume $<TARGET_OBJECTS:> — it is a separate CMake project on the
    # player's machine — and it must end up with EXACTLY this object set, which is why it
    # links this whole-archive. These hooks are strong overrides of functions the
    # generator emits weak, so a weak definition satisfies nothing and a normal archive
    # link would pull in none of them.
    add_library(refii_hooks_archive STATIC $<TARGET_OBJECTS:refii_hooks>)
    set_target_properties(refii_hooks_archive PROPERTIES
        OUTPUT_NAME refii_hooks
        LINKER_LANGUAGE CXX
        POSITION_INDEPENDENT_CODE ON
        ARCHIVE_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    )
else()
    set(REFII_HOOK_OBJECTS
        PARENT_SCOPE
    )
endif()
