# Native Lua runtime and tooling.
#
# `refii_lua` owns host-side runtime helpers. Strong PPC overrides are exposed
# as source files and compiled by the unified `Fable2/src/hooks` object target.

set(REFII_LUA_ROOT ${CMAKE_CURRENT_LIST_DIR})

set(REFII_LUA_INCLUDE_DIRS
    ${REFII_LUA_ROOT}/include
)

set(REFII_LUA_RUNTIME_PUBLIC_HEADERS
    ${REFII_LUA_ROOT}/include/core/native_lua_hooks.h
    ${REFII_LUA_ROOT}/include/lua_script_paths.h
)

set(REFII_LUA_RUNTIME_PRIVATE_HEADERS
    ${REFII_LUA_ROOT}/include/core/native_lua_rexglue_compat.h
)

set(REFII_LUA_RUNTIME_SOURCES
    ${REFII_LUA_ROOT}/src/core/native_lua_runtime.cpp
)

set(REFII_LUA_RUNTIME_PARTS
    ${REFII_LUA_RUNTIME_PUBLIC_HEADERS}
    ${REFII_LUA_RUNTIME_PRIVATE_HEADERS}
)
set_source_files_properties(${REFII_LUA_RUNTIME_PARTS} PROPERTIES HEADER_FILE_ONLY TRUE)

# THE PAYLOAD COMPILES THESE, IT DOES NOT LINK THEM. Every one of these files calls the
# recompiled guest -- the hooks declare the guest functions they wrap -- and the
# guest only exists inside refii_generated, which is built on the player's machine. That
# rules out shipping them as a library: a Windows DLL must resolve every symbol at link
# time and cannot, while a STATIC lib links but is MSVC-built, so its /DEFAULTLIB:msvcrtd
# directives break the MinGW-driver payload link. Staging the sources puts them through
# the same compiler as the generated code, which is what Fable2/src/vfs already does with its 51
# hook sources. refii_lua itself stays for staging and is linked by nothing.
set(REFII_LUA_HOOK_SOURCES
    ${REFII_LUA_ROOT}/src/core/native_lua_hooks.cpp
    ${REFII_LUA_RUNTIME_SOURCES}
)

set(REFII_LUA_HOOK_PARTS
    ${REFII_LUA_ROOT}/include/core/native_lua_hooks.h
)
set_source_files_properties(${REFII_LUA_HOOK_PARTS} PROPERTIES HEADER_FILE_ONLY TRUE)

# THE FRONT END'S MOD LUA (Fable2/src/vfs/src/hooks/mod_frontend_lua_vm.cpp) includes these by
# path, inside a namespace, as the oracle below does: the hooks target carries them uncompiled
# (HEADER_FILE_ONLY) so the payload staging ships them for the player's build.
set(REFII_LUA_FRONTEND_VM_C_SOURCES
    lapi.c lcode.c ldebug.c ldo.c ldump.c lfunc.c lgc.c llex.c lmem.c lobject.c lopcodes.c
    lparser.c lstate.c lstring.c ltable.c ltm.c lundump.c lvm.c lzio.c
    lauxlib.c lbaselib.c lmathlib.c lstrlib.c ltablib.c
)
list(TRANSFORM REFII_LUA_FRONTEND_VM_C_SOURCES PREPEND "${REFII_LUA_ROOT}/thirdparty/lua512/src/")
file(GLOB REFII_LUA_FRONTEND_VM_HEADERS "${REFII_LUA_ROOT}/thirdparty/lua512/src/*.h")
set(REFII_LUA_FRONTEND_VM_PARTS
    ${REFII_LUA_FRONTEND_VM_C_SOURCES}
    ${REFII_LUA_FRONTEND_VM_HEADERS}
    ${REFII_LUA_ROOT}/src/tools/luadec/luac32_luaconf.h
)

set(REFII_LUADEC_SOURCES
    ${REFII_LUA_ROOT}/src/tools/luadec/luadec_chunk.cpp
    ${REFII_LUA_ROOT}/src/tools/luadec/luadec.cpp
    ${REFII_LUA_ROOT}/src/tools/luadec/luadec_oracle.cpp
)

set(REFII_LUADEC_HEADERS
    ${REFII_LUA_ROOT}/src/tools/luadec/luadec_chunk.h
    ${REFII_LUA_ROOT}/src/tools/luadec/luadec.h
    ${REFII_LUA_ROOT}/src/tools/luadec/luadec_oracle.h
    ${REFII_LUA_ROOT}/src/tools/luadec/luac32_luaconf.h
)

set_source_files_properties(${REFII_LUADEC_HEADERS} PROPERTIES HEADER_FILE_ONLY TRUE)

source_group(TREE ${REFII_LUA_ROOT} FILES
    ${REFII_LUA_RUNTIME_PARTS}
    ${REFII_LUA_RUNTIME_SOURCES}
    ${REFII_LUA_HOOK_PARTS}
    ${REFII_LUA_HOOK_SOURCES}
    ${REFII_LUADEC_HEADERS}
)

set(REFII_LUA_LIBRARY_TYPE SHARED)
if(WIN32)
    set(REFII_LUA_LIBRARY_TYPE STATIC)
endif()

add_library(refii_lua ${REFII_LUA_LIBRARY_TYPE}
    ${REFII_LUA_RUNTIME_SOURCES}
    ${REFII_LUA_RUNTIME_PARTS}
)
target_include_directories(refii_lua
    PUBLIC
        ${REFII_LUA_INCLUDE_DIRS}
        # For core/payload_bridge_string.h — the caller-buffer helper both bridge ABIs use.
        # An include path only; refii_lua does not link refii_runtime. Copying the helper here
        # instead would be a second implementation of the one thing that must behave
        # identically on both sides of the boundary.
        ${REFII_RUNTIME_INCLUDE_DIRS}
)
target_link_libraries(refii_lua
    PUBLIC
        rex::runtime
)
set_target_properties(refii_lua PROPERTIES
    CXX_STANDARD 23
    CXX_STANDARD_REQUIRED ON
    POSITION_INDEPENDENT_CODE ON
    OUTPUT_NAME refii_lua
    ARCHIVE_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    LIBRARY_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    RUNTIME_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    BUILD_RPATH "$ORIGIN"
    INSTALL_RPATH "$ORIGIN"
)

if(WIN32 AND NOT REFII_LUA_LIBRARY_TYPE STREQUAL "STATIC")
    set_target_properties(refii_lua PROPERTIES
        WINDOWS_EXPORT_ALL_SYMBOLS ON
    )
endif()

# luadec: the Lua 5.1 decompiler and its byte-exact oracle. The oracle
# compiles the vendored Lua 5.1.2 (+ `continue`) as C++ inside a namespace
# with the guest's 4-byte float lua_Number, so it can sit in one binary next
# to the runtime's own Lua. Its one translation unit takes the vendored
# sources' warnings as they are.
add_library(refii_luadec_core STATIC
    ${REFII_LUADEC_SOURCES}
    ${REFII_LUADEC_HEADERS}
)
target_include_directories(refii_luadec_core
    PUBLIC
        ${REFII_LUA_ROOT}/src/tools/luadec
    PRIVATE
        ${REFII_LUA_ROOT}/thirdparty/lua512/src
)
if(WIN32)
    target_compile_definitions(refii_luadec_core PRIVATE LUA_USE_WINDOWS)
elseif(APPLE)
    target_compile_definitions(refii_luadec_core PRIVATE LUA_USE_MACOSX)
else()
    target_compile_definitions(refii_luadec_core PRIVATE LUA_USE_LINUX)
endif()
if(MSVC)
    set_source_files_properties(${REFII_LUA_ROOT}/src/tools/luadec/luadec_oracle.cpp
        PROPERTIES COMPILE_OPTIONS "/w")
else()
    set_source_files_properties(${REFII_LUA_ROOT}/src/tools/luadec/luadec_oracle.cpp
        PROPERTIES COMPILE_OPTIONS "-w")
endif()
set_target_properties(refii_luadec_core PROPERTIES
    OUTPUT_NAME refii_luadec_core
    CXX_STANDARD 23
    CXX_STANDARD_REQUIRED ON
    ARCHIVE_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    POSITION_INDEPENDENT_CODE ON
    # Only the C ABI leaves the shared library; the namespaced Lua stays inside.
    CXX_VISIBILITY_PRESET hidden
)

# The editor's decompiler: the Godot bank-mount plugin decompiles a script
# bank entry on demand through this C ABI (Engine_LuaDecompiler.cs) and saves
# an edited copy into the mod folder, where the mount overlays it like any
# other file. Staged beside the other host libraries.
add_library(refii_luadec SHARED
    ${REFII_LUA_ROOT}/src/tools/luadec/luadec_c_api.cpp
)
target_link_libraries(refii_luadec PRIVATE refii_luadec_core)
set_target_properties(refii_luadec PROPERTIES
    OUTPUT_NAME refii_luadec
    CXX_STANDARD 23
    CXX_STANDARD_REQUIRED ON
    POSITION_INDEPENDENT_CODE ON
    CXX_VISIBILITY_PRESET hidden
    LIBRARY_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    RUNTIME_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
)
if(REFII_STAGE_GODOT_BUILD AND DEFINED REFII_GODOT_BUILD_BIN_DIR)
    add_custom_command(TARGET refii_luadec POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E make_directory "${REFII_GODOT_BUILD_BIN_DIR}"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "$<TARGET_FILE:refii_luadec>"
            "${REFII_GODOT_BUILD_BIN_DIR}"
        COMMENT "Staging refii_luadec for Godot"
    )
endif()

if(REFII_BUILD_TOOLS)
    # The oracle on its own: compile Lua text to a guest chunk, or verify a
    # decompiled file against the chunk it came from.
    add_executable(refii-luac32
        ${REFII_LUA_ROOT}/src/tools/luadec/luac32_main.cpp
    )
    target_link_libraries(refii-luac32 PRIVATE refii_luadec_core)
    set_target_properties(refii-luac32 PROPERTIES
        OUTPUT_NAME refii-luac32
        CXX_STANDARD 23
        CXX_STANDARD_REQUIRED ON
        RUNTIME_OUTPUT_DIRECTORY "${REFII_TOOL_OUTPUT_DIR}"
    )
    refii_stage_windows_runtime_dlls(refii-luac32)

    # The decompiler on its own: one chunk to Lua text or a listing.
    add_executable(refii-luadec
        ${REFII_LUA_ROOT}/src/tools/luadec/luadec_main.cpp
    )
    target_link_libraries(refii-luadec PRIVATE refii_luadec_core)
    set_target_properties(refii-luadec PROPERTIES
        OUTPUT_NAME refii-luadec
        CXX_STANDARD 23
        CXX_STANDARD_REQUIRED ON
        RUNTIME_OUTPUT_DIRECTORY "${REFII_TOOL_OUTPUT_DIR}"
    )
    refii_stage_windows_runtime_dlls(refii-luadec)
endif()


set(REFII_LUA_HOOK_SOURCES
    ${REFII_LUA_HOOK_SOURCES}
    PARENT_SCOPE
)

set(REFII_LUA_FRONTEND_VM_PARTS
    ${REFII_LUA_FRONTEND_VM_PARTS}
    PARENT_SCOPE
)

set(REFII_LUA_INCLUDE_DIRS
    ${REFII_LUA_INCLUDE_DIRS}
    PARENT_SCOPE
)

set(REFII_LUA_LIBRARIES
    refii_lua
    PARENT_SCOPE
)
