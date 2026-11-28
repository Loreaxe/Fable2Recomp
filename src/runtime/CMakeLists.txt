# The shared runtime state: what the host and a title's payload agree on whatever the title.
#
# `refii_runtime` is SHARED for the reason refii_vfs is: the two ends of every piece of state
# here live in different libraries. The HOST writes or reads (the host bridge, the app's audio
# and input drivers) and the PAYLOAD reads or writes (the generated code's rex:: calls, a
# title's hooks), and each needs ONE copy process-wide. A static copy in each would have one
# side filling a queue the other never sees, which is not a link error on Linux.
#
# NOT A TITLE LIBRARY. It is staged into bin/ beside rexruntime and refii_core, links no title
# code, and every title's instance (bin/<Title>/) and payload link the same file.
#
# The payload's side of these entries is in src/payload/src/core (REFII_PAYLOAD_CORE_SOURCES),
# compiled into the module on the player's machine.

set(REFII_RUNTIME_ROOT ${CMAKE_CURRENT_LIST_DIR})

set(REFII_RUNTIME_INCLUDE_DIRS
    ${REFII_RUNTIME_ROOT}/include
)

add_library(refii_runtime SHARED
    # Audio state, shared by three libraries: the voice hooks (the payload), the host audio
    # driver (refii_host) and the host bridge.
    ${REFII_RUNTIME_ROOT}/src/core/audio_state.cpp
    # Host side of the payload's log bridge and of the rex:: calls the generated code makes
    # (core/rex_payload_bridge.h). The payload is the caller, so these cannot sit in it.
    ${REFII_RUNTIME_ROOT}/src/core/log_bridge.cpp
    ${REFII_RUNTIME_ROOT}/src/core/rex_bridge_host.cpp
    # The queue the host posts onto the guest's tick thread, its counters and the guest frame.
    ${REFII_RUNTIME_ROOT}/src/core/guest_tick_queue.cpp
    # A title's tick pump states each step; a host thread walks a stalled step's guest back chain.
    ${REFII_RUNTIME_ROOT}/src/core/guest_step_sampler.cpp
    # The debug channel's guest-render switch: the host sets it, a title's draw hook reads it.
    ${REFII_RUNTIME_ROOT}/src/core/guest_render_state.cpp
    # The mouse's camera turn: the host posts, a title's camera hook takes it.
    ${REFII_RUNTIME_ROOT}/src/core/mouse_look_state.cpp
    # Hard mods' named values: a mod's hook publishes, Godot reads; Godot sets, the hook reads.
    ${REFII_RUNTIME_ROOT}/src/core/mod_values.cpp
    # Mods' per-player values: owned by the machine that plays the hero, the same on both co-op
    # machines, the saved ones kept beside that hero's save.
    ${REFII_RUNTIME_ROOT}/src/core/player_state.cpp
    # Mod events decided on one co-op machine, carried to the other.
    ${REFII_RUNTIME_ROOT}/src/core/event_relay.cpp
    # Partner consent: a co-op script asks the other player; the host bridge carries it.
    ${REFII_RUNTIME_ROOT}/src/core/coop_consent.cpp
    # The names the game shows for players, by RexNet's rule.
    ${REFII_RUNTIME_ROOT}/src/core/coop_names.cpp
    # Refii's co-op channel told that the peer left the title's session.
    ${REFII_RUNTIME_ROOT}/src/core/coop_channel.cpp
    # The host's asset reader: Godot's bank reads, called by the payload.
    ${REFII_RUNTIME_ROOT}/src/core/host_asset_reader.cpp
    # The movie the guest states: a title's Bink hooks write it, the host bridge's movie
    # exports read it.
    ${REFII_RUNTIME_ROOT}/src/core/movie_state.cpp
    # The UI cut's arrival queue: the `GUI.*` native calls a title's luaD_precall hook posts, with
    # their arguments, drained by the shared host exports; and the host's answers to predicates.
    ${REFII_RUNTIME_ROOT}/src/core/gui_native_calls.cpp
    # What a title's guest tells its presentations' elements (slides, attributes, active, texts,
    # textures) and its headless presentations' registry: a title's hooks push, its host bridge
    # takes for the shell's RefiiHostTakeTold* / RefiiHostToldHeadless* exports.
    ${REFII_RUNTIME_ROOT}/src/core/told_statements.cpp
    # The camera a title states and the host's world-build hold, between its hooks and its
    # host bridge.
    ${REFII_RUNTIME_ROOT}/src/core/stated_view.cpp
    # Player two's camera's fades, stated by the core camera chunk, drawn in player two's half.
    ${REFII_RUNTIME_ROOT}/src/core/stated_camera_fade.cpp
    # The world a title states between its hooks and its host bridge: its levels' up and down,
    # its effects by handle, its decals.
    ${REFII_RUNTIME_ROOT}/src/core/stated_world.cpp
    # The appearance a title states between its hooks and its host bridge: each character part's
    # morph targets and each original texture's layers.
    ${REFII_RUNTIME_ROOT}/src/core/stated_appearance.cpp
    # The cloth a title states between its hooks and its host bridge: each piece's drawn vertices
    # as the guest's draw publishes them.
    ${REFII_RUNTIME_ROOT}/src/core/stated_cloth.cpp
    ${REFII_RUNTIME_ROOT}/src/core/stated_classic_particles.cpp
    # The world icons and choice records a title states between its hooks and its host bridge.
    ${REFII_RUNTIME_ROOT}/src/core/stated_world_icons.cpp
    # The HUD element registries (texts, meters, pulsers), the screen fade and the aim reticules a
    # title states.
    ${REFII_RUNTIME_ROOT}/src/core/stated_hud.cpp
    # The loading screen, the game mode stacks and GUI screen, and the installed cultures a title
    # states for the shell.
    ${REFII_RUNTIME_ROOT}/src/core/stated_game_state.cpp
    # The options a title states: each applier's value and the saved profile's.
    ${REFII_RUNTIME_ROOT}/src/core/stated_options.cpp
    # The GUI scenes a title presents in place of the world (map tables): objects, camera, presented.
    ${REFII_RUNTIME_ROOT}/src/core/stated_scene_objects.cpp
)
target_include_directories(refii_runtime
    PUBLIC
        ${REFII_RUNTIME_INCLUDE_DIRS}
)
target_link_libraries(refii_runtime
    PUBLIC
        rex::runtime
)

set_target_properties(refii_runtime PROPERTIES
    POSITION_INDEPENDENT_CODE ON
    OUTPUT_NAME refii_runtime
    LIBRARY_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    RUNTIME_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    BUILD_RPATH "$ORIGIN"
    INSTALL_RPATH "$ORIGIN"
)

# Closed over the runtime: a reference to a title's symbol is a link error here, not a lazy
# binding that ends the process the first time it runs.
if(NOT WIN32)
    target_link_options(refii_runtime PRIVATE -Wl,--no-undefined)
endif()

if(WIN32)
    set_target_properties(refii_runtime PROPERTIES
        WINDOWS_EXPORT_ALL_SYMBOLS ON
    )
endif()

set(REFII_RUNTIME_INCLUDE_DIRS
    ${REFII_RUNTIME_INCLUDE_DIRS}
    PARENT_SCOPE
)
