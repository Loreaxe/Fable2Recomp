# Native VFS runtime for loose asset overrides.
#
# `refii_vfs` owns cvars, host-side path resolution and Fable 2's own shared state. Strong PPC
# overrides are exposed as source files and compiled by the unified `Fable2/src/hooks` object
# target. The state every title shares (audio, the tick queue, mods, player state, the relay,
# the rex:: bridge) is refii_runtime's, src/runtime, which this links.

set(REFII_VFS_ROOT ${CMAKE_CURRENT_LIST_DIR})

set(REFII_VFS_INCLUDE_DIRS
    ${REFII_VFS_ROOT}/include
)

set(REFII_VFS_RUNTIME_SOURCES
    ${REFII_VFS_ROOT}/src/runtime/cvars.cpp
    ${REFII_VFS_ROOT}/src/runtime/path_resolution.cpp
    # Bink video state shared by the guest hooks and the host bridge. Must live in
    # refii_vfs, which BOTH link, and not in the hook set: a copy in each library
    # means the hook writes one global and the bridge reads another.
    ${REFII_VFS_ROOT}/src/core/bink_frame_state.cpp
    # Which build of the game is running and the addresses that differ between builds. In
    # refii_vfs so the payload's hooks and the host bridge read ONE answer.
    ${REFII_VFS_ROOT}/src/core/guest_build.cpp
    # The guest tick pump's sampled pages, and the host-step loading-screen finisher. Here
    # for the same reason as bink_frame_state.cpp, and by the same rule: the PAYLOAD publishes
    # and the HOST reads, so the state cannot sit beside the pump hook. It did, which left the
    # host bridge naming symbols only the payload defined — resolvable on Linux through
    # RTLD_GLOBAL, unresolvable in a Windows DLL, and against refii_payload/payload.h either
    # way. The queue the host posts onto the tick thread is refii_runtime's.
    ${REFII_VFS_ROOT}/src/core/guest_tick_pump_state.cpp
    # The effect transforms and the targeting highlight the guest's own setters state. Same
    # rule and same direction of travel as the queue above: the PAYLOAD writes (the hooks in
    # Fable2/src/vfs/src/hooks/particle_transform.cpp and Fable2/src/vfs/src/hooks/target_highlight.cpp) and the HOST BRIDGE
    # reads. They sat beside those hooks, reached by `dlsym(RTLD_DEFAULT, ...)`, which resolves
    # only because the payload is dlopen'd RTLD_GLOBAL on Linux -- on Windows there is no
    # RTLD_DEFAULT to compile against and no global scope to look a name up in.
    ${REFII_VFS_ROOT}/src/core/stated_fx_state.cpp
    ${REFII_VFS_ROOT}/src/core/target_highlight_state.cpp
    # Each renderable's stated appearance (alpha, tint, scale, drawable): same rule, filled by
    # Fable2/src/vfs/src/hooks/entity_appearance.cpp.
    ${REFII_VFS_ROOT}/src/core/entity_appearance_state.cpp
    # Each character's texture composites, filled by Fable2/src/vfs/src/hooks/texture_layers.cpp.
    ${REFII_VFS_ROOT}/src/core/texture_layer_state.cpp
    # The decals the guest states, filled by Fable2/src/vfs/src/hooks/decals.cpp.
    ${REFII_VFS_ROOT}/src/core/decal_state.cpp
    ${REFII_VFS_ROOT}/src/core/loading_text_state.cpp
    # The FX mesh particle descriptors, filled by Fable2/src/vfs/src/hooks/fx_meshes.cpp.
    ${REFII_VFS_ROOT}/src/core/fx_mesh_state.cpp
    ${REFII_VFS_ROOT}/src/core/item_dye_layer_state.cpp
    ${REFII_VFS_ROOT}/src/core/part_morph_state.cpp
    # Which of the hero's garments are dyed, sampled from the guest's own test.
    ${REFII_VFS_ROOT}/src/core/dye_state.cpp
    # The pub-game seam: the host posts Godot's fscommands, the pump replacement drains them.
    ${REFII_VFS_ROOT}/src/core/pubgame_state.cpp
    # One-time collectables per player: silver keys each player takes for themselves.
    ${REFII_VFS_ROOT}/src/core/coop_collectables.cpp
    # A villager's opinion of player two, swapped in for their deeds.
    ${REFII_VFS_ROOT}/src/core/coop_opinion.cpp
    # Player two's split-screen HUD: the pump states it, the host bridge reads it.
    ${REFII_VFS_ROOT}/src/core/coop_hud_two.cpp
    ${REFII_VFS_ROOT}/src/core/hero_partners.cpp
    # (The told-statement queues the hooks push and the bridge takes are shared state in
    # refii_runtime, src/runtime/src/core/told_statements.cpp, as the GUI native call queue is.)
    ${REFII_VFS_ROOT}/src/gui/gui_menu_chunks.cpp
)

set(REFII_VFS_HOOK_INTERNAL_SOURCES
    ${REFII_VFS_ROOT}/src/core/internal_state_guest.cpp
    # States the payload's build (TU1 or unpatched) to refii_vfs at load.
    ${REFII_VFS_ROOT}/src/hooks/guest_build_stamp.cpp
    ${REFII_VFS_ROOT}/src/streams/host_stream_records.cpp
    ${REFII_VFS_ROOT}/src/streams/host_disk_stream_objects.cpp
    ${REFII_VFS_ROOT}/src/streams/host_async_io_queue.cpp
    ${REFII_VFS_ROOT}/src/resources/resource_wrappers.cpp
    ${REFII_VFS_ROOT}/src/resources/texture_trace_formatting.cpp
    ${REFII_VFS_ROOT}/src/frontend/post_video_trace_state.cpp
    ${REFII_VFS_ROOT}/src/frontend/post_video_handoff_state.cpp
    ${REFII_VFS_ROOT}/src/resources/native_texture_parser_trace.cpp
    ${REFII_VFS_ROOT}/src/streams/resource_wrapper_handlers.cpp
    ${REFII_VFS_ROOT}/src/streams/resource_callbacks_and_lua_bridge.cpp
    ${REFII_VFS_ROOT}/src/index/asset_aliases_and_optional_probes.cpp
    ${REFII_VFS_ROOT}/src/banks/manifest_resolution.cpp
    ${REFII_VFS_ROOT}/src/banks/synthetic_bank_runtime.cpp
    ${REFII_VFS_ROOT}/src/banks/synthetic_vfs_hash_index.cpp
    ${REFII_VFS_ROOT}/src/index/runtime_tables.cpp
    ${REFII_VFS_ROOT}/src/index/runtime_alias_cache.cpp
    ${REFII_VFS_ROOT}/src/index/resource_hash_index.cpp
    ${REFII_VFS_ROOT}/src/index/concrete_asset_resolution.cpp
    ${REFII_VFS_ROOT}/src/index/ownership_decisions.cpp
    ${REFII_VFS_ROOT}/src/index/open_pipeline.cpp
    ${REFII_VFS_ROOT}/src/frontend/main_menu_trace.cpp
    ${REFII_VFS_ROOT}/src/frontend/loading_sequence_bridge.cpp
    ${REFII_VFS_ROOT}/src/frontend/startup_video_handoff_gates.cpp
)

set(REFII_VFS_PPC_HOOK_SOURCES
    ${REFII_VFS_ROOT}/src/hooks/texture_parser.cpp
    ${REFII_VFS_ROOT}/src/hooks/boot_lifecycle.cpp
    ${REFII_VFS_ROOT}/src/hooks/crt_math.cpp
    ${REFII_VFS_ROOT}/src/hooks/crt_memory.cpp
    ${REFII_VFS_ROOT}/src/hooks/gui_natives.cpp
    ${REFII_VFS_ROOT}/src/hooks/display_box_hooks.cpp
    ${REFII_VFS_ROOT}/src/hooks/gui_registry_hooks.cpp
    ${REFII_VFS_ROOT}/src/hooks/reticule_hooks.cpp
    ${REFII_VFS_ROOT}/src/hooks/gui_menu_hooks.cpp
    # The pause minimap's markers and the inline texture stores, stated for Godot.
    ${REFII_VFS_ROOT}/src/hooks/gui_pause_hooks.cpp
    # Gameface presentations built headless: the guest keeps its UI objects, Godot draws.
    ${REFII_VFS_ROOT}/src/hooks/gui_headless_presentation.cpp
    # The screen that is up and each screen's presentation, stated from the calls that set them.
    ${REFII_VFS_ROOT}/src/hooks/gui_stated_screens.cpp
    ${REFII_VFS_ROOT}/src/hooks/loading_screen_stated.cpp
    ${REFII_VFS_ROOT}/src/hooks/options_levels_stated.cpp
    ${REFII_VFS_ROOT}/src/hooks/rumble_stated.cpp
    ${REFII_VFS_ROOT}/src/hooks/screen_fade_stated.cpp
    ${REFII_VFS_ROOT}/src/hooks/spell_widget_stated.cpp
    ${REFII_VFS_ROOT}/src/hooks/hud_expression_meter_stated.cpp
    ${REFII_VFS_ROOT}/src/hooks/slow_time_dial_stated.cpp
    ${REFII_VFS_ROOT}/src/hooks/crime_screen_stated.cpp
    ${REFII_VFS_ROOT}/src/hooks/frontend_stated.cpp
    ${REFII_VFS_ROOT}/src/hooks/logbook_stats_stated.cpp
    # The ported screens (S7): handler slots replaced, keeping the guest's statements and game calls.
    ${REFII_VFS_ROOT}/src/hooks/gui_screen_stub_abilities.cpp
    ${REFII_VFS_ROOT}/src/hooks/gui_screen_stub_book_levers.cpp
    ${REFII_VFS_ROOT}/src/hooks/gui_screen_stub_clothing.cpp
    ${REFII_VFS_ROOT}/src/hooks/gui_screen_stub_items.cpp
    ${REFII_VFS_ROOT}/src/hooks/gui_screen_stub_logbook.cpp
    ${REFII_VFS_ROOT}/src/hooks/gui_screen_stub_options.cpp
    ${REFII_VFS_ROOT}/src/hooks/gui_screen_stub_pause_root.cpp
    ${REFII_VFS_ROOT}/src/hooks/gui_screen_stub_receive_items.cpp
    ${REFII_VFS_ROOT}/src/hooks/gui_screen_stub_small_screens.cpp
    ${REFII_VFS_ROOT}/src/hooks/gui_screen_stub_support.cpp
    ${REFII_VFS_ROOT}/src/hooks/preview_figures_stated.cpp
    # The shopkeeper, the previewed item, the pending weapons and the experience counter's
    # visibility, stated from the calls that set them.
    ${REFII_VFS_ROOT}/src/hooks/shop_keeper_stated.cpp
    ${REFII_VFS_ROOT}/src/hooks/preview_item_stated.cpp
    ${REFII_VFS_ROOT}/src/hooks/pending_weapons_stated.cpp
    ${REFII_VFS_ROOT}/src/hooks/experience_counter_stated.cpp
    # The list screens' row tickets, from their allocate and clear.
    ${REFII_VFS_ROOT}/src/hooks/menu_tickets_stated.cpp
    ${REFII_VFS_ROOT}/src/hooks/gui_element_hooks.cpp
    # The world icons, stated at their Init, Updates and destructor into the shared table.
    ${REFII_VFS_ROOT}/src/hooks/world_icon_hooks.cpp
    # The pub-game pump with the Scaleform module taken out, once Godot claims the games.
    ${REFII_VFS_ROOT}/src/hooks/pubgame_pump.cpp
    ${REFII_VFS_ROOT}/src/hooks/babel_text_hooks.cpp
    ${REFII_VFS_ROOT}/src/hooks/guest_tick_pump.cpp
    # The renderer's deferred-release list, released on a co-op joiner too.
    ${REFII_VFS_ROOT}/src/hooks/deferred_release_hooks.cpp
    ${REFII_VFS_ROOT}/src/hooks/game_init_trace.cpp
    # Mods' own Lua, run in the game VM after the startup and camera scripts.
    ${REFII_VFS_ROOT}/src/hooks/mod_lua_hooks.cpp
    # The API a hard mod's native code calls into: tick callbacks, the hero, stats, spells.
    ${REFII_VFS_ROOT}/src/hooks/mod_api.cpp
    # The mod event bus: typed events on the game's seams, and the mods' Lua handlers.
    ${REFII_VFS_ROOT}/src/hooks/mod_events.cpp
    ${REFII_VFS_ROOT}/src/hooks/household_persist.cpp
    # Player state's saves: a hero's saved mod values beside the save the game writes it into.
    ${REFII_VFS_ROOT}/src/hooks/player_state_hooks.cpp
    # The host's queued helper calls and queries (Godot scripts), run in the guest tick.
    ${REFII_VFS_ROOT}/src/hooks/mod_commands.cpp
    # GDB hot reload: a remounted database read again by the guest's own loader, older copies synced.
    ${REFII_VFS_ROOT}/src/hooks/gdb_hot_reload.cpp
    # Mod abilities' purchases and spell picks, run by the engine from what the mods declare.
    ${REFII_VFS_ROOT}/src/hooks/mod_abilities.cpp
    # The Refii.* natives registered into the game VM for mods' Lua.
    ${REFII_VFS_ROOT}/src/hooks/mod_lua_natives.cpp
    ${REFII_VFS_ROOT}/src/hooks/lua_gc_slice.cpp
    ${REFII_VFS_ROOT}/src/hooks/frontend_video_handoff.cpp
    ${REFII_VFS_ROOT}/src/hooks/particle_transform.cpp
    # Which spell a spell's effects belong to, as its own update states it; plays ride that update.
    ${REFII_VFS_ROOT}/src/hooks/spell_fx_owner.cpp
    # Mod effects' scale and tint on the guest's instances, the bank reloaded on a remount.
    ${REFII_VFS_ROOT}/src/hooks/particle_dress.cpp
    # Mod emitters' spin and mod mesh particles' rest orientation, read from the same dress table.
    ${REFII_VFS_ROOT}/src/hooks/particle_motion.cpp
    # Mods' Lua on the title and the front end: a host Lua state per front-end presentation, and
    # its interpreter (the vendored Lua 5.1.2 as the guest configures it, REFII_LUA_FRONTEND_VM_PARTS).
    ${REFII_VFS_ROOT}/src/hooks/mod_frontend_lua.cpp
    ${REFII_VFS_ROOT}/src/hooks/mod_frontend_lua_vm.cpp
    ${REFII_VFS_ROOT}/src/hooks/spell_fx_lifetime.cpp
    ${REFII_VFS_ROOT}/src/hooks/target_highlight.cpp
    ${REFII_VFS_ROOT}/src/hooks/entity_appearance.cpp
    ${REFII_VFS_ROOT}/src/hooks/texture_layers.cpp
    ${REFII_VFS_ROOT}/src/hooks/decals.cpp
    # The mouse's turn handed to the active camera's own ProcessGameAction before each update.
    ${REFII_VFS_ROOT}/src/hooks/mouse_look.cpp
    ${REFII_VFS_ROOT}/src/hooks/fx_meshes.cpp
    ${REFII_VFS_ROOT}/src/hooks/attached_particle_restore.cpp
    ${REFII_VFS_ROOT}/src/hooks/native_misc.cpp
    ${REFII_VFS_ROOT}/src/hooks/resource_manager_open.cpp
    ${REFII_VFS_ROOT}/src/hooks/resource_manager_streams.cpp
    ${REFII_VFS_ROOT}/src/hooks/streams.cpp
    ${REFII_VFS_ROOT}/src/hooks/file_api.cpp
    # The guest's XAudio shims, mirrored so Godot can play each voice itself.
    ${REFII_VFS_ROOT}/src/hooks/xaudio_voices.cpp
)
if(ANDROID)
    # The phone's lock-free double-free guard, in place of babel_text_hooks.cpp's.
    list(APPEND REFII_VFS_PPC_HOOK_SOURCES ${REFII_VFS_ROOT}/src/hooks/babel_text_hooks_android.cpp)
endif()

set(REFII_VFS_HOOK_PARTS
    ${REFII_VFS_ROOT}/include/vfs_hooks_private.h
    ${REFII_VFS_ROOT}/include/vfs_hook_constants.h
    ${REFII_VFS_ROOT}/include/vfs_hook_types.h
    ${REFII_VFS_ROOT}/include/banks/types.h
    ${REFII_VFS_ROOT}/include/banks/hash_bank_runtime.h
    ${REFII_VFS_ROOT}/include/banks/manifest_resolution.h
    ${REFII_VFS_ROOT}/include/banks/synthetic_bank_runtime.h
    ${REFII_VFS_ROOT}/include/core/guest_state.h
    ${REFII_VFS_ROOT}/include/frontend/loading_sequence.h
    ${REFII_VFS_ROOT}/include/frontend/post_video.h
    ${REFII_VFS_ROOT}/include/frontend/types.h
    ${REFII_VFS_ROOT}/include/index/asset_aliases.h
    ${REFII_VFS_ROOT}/include/index/concrete_asset_resolution.h
    ${REFII_VFS_ROOT}/include/index/ownership.h
    ${REFII_VFS_ROOT}/include/index/open_pipeline.h
    ${REFII_VFS_ROOT}/include/index/resource_hash_index.h
    ${REFII_VFS_ROOT}/include/index/resource_hash_resolution.h
    ${REFII_VFS_ROOT}/include/index/runtime_alias_cache.h
    ${REFII_VFS_ROOT}/include/index/runtime_tables.h
    ${REFII_VFS_ROOT}/include/loose_asset_runtime_stats.h
    ${REFII_VFS_ROOT}/include/resources/resource_wrappers.h
    ${REFII_VFS_ROOT}/include/resources/native_texture_parser_trace.h
    ${REFII_VFS_ROOT}/include/runtime/cvars.h
    ${REFII_VFS_ROOT}/include/resources/texture_trace_formatting.h
    ${REFII_VFS_ROOT}/include/resources/texture_tracing.h
    ${REFII_VFS_ROOT}/include/streams/host_streams.h
    ${REFII_VFS_ROOT}/include/streams/host_async_io_queue.h
    ${REFII_VFS_ROOT}/include/streams/host_disk_stream_objects.h
    ${REFII_VFS_ROOT}/include/streams/host_stream_records.h
    ${REFII_VFS_ROOT}/include/streams/resource_callbacks.h
    ${REFII_VFS_ROOT}/include/streams/resource_wrapper_handlers.h
    ${REFII_VFS_ROOT}/include/streams/types.h
    ${REFII_VFS_ROOT}/include/hooks/ppc_externs.h
)
set_source_files_properties(${REFII_VFS_HOOK_PARTS} PROPERTIES HEADER_FILE_ONLY TRUE)

set(REFII_VFS_HOOK_SOURCES
    ${REFII_VFS_HOOK_INTERNAL_SOURCES}
    ${REFII_VFS_PPC_HOOK_SOURCES}
)

add_library(refii_vfs SHARED
    ${REFII_VFS_RUNTIME_SOURCES}
)
target_include_directories(refii_vfs
    PUBLIC
        ${REFII_VFS_INCLUDE_DIRS}
)
target_link_libraries(refii_vfs
    PUBLIC
        refii_runtime
        rex::runtime
)

set_target_properties(refii_vfs PROPERTIES
    POSITION_INDEPENDENT_CODE ON
    OUTPUT_NAME refii_vfs
    LIBRARY_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    RUNTIME_OUTPUT_DIRECTORY "${REFII_LIBRARY_OUTPUT_DIR}"
    BUILD_RPATH "$ORIGIN"
    INSTALL_RPATH "$ORIGIN"
)

if(WIN32)
    set_target_properties(refii_vfs PROPERTIES
        WINDOWS_EXPORT_ALL_SYMBOLS ON
    )
endif()

set(REFII_VFS_HOOK_SOURCES
    ${REFII_VFS_HOOK_SOURCES}
    PARENT_SCOPE
)

set(REFII_VFS_INCLUDE_DIRS
    ${REFII_VFS_INCLUDE_DIRS}
    PARENT_SCOPE
)

set(REFII_VFS_LIBRARIES
    refii_vfs
    PARENT_SCOPE
)
