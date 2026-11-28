# Fable 3's native PPC hooks: strong overrides of functions the generator emits weak, compiled
# into refiii_generated. The dev build compiles them here so a hook that does not build fails
# now; the launcher compiles the staged copies (bin/recompile/Fable3/hooks) into the player's
# module, with the same definitions (GameCodeBuild's payload flags).

set(REFIII_HOOK_SOURCES
    # The device's no-GPU bit, armed as the ring buffer comes up.
    # States the payload's build (TU3 or disc) to refiii_vfs at load.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/guest_build_stamp.cpp
    ${CMAKE_SOURCE_DIR}/Fable3/src/vfs/include/fable3_guest_build.h
    ${CMAKE_CURRENT_SOURCE_DIR}/src/boot_no_gpu.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/box_hold_statements.cpp
    # Who targets each thing, stated at the guest's targeted setter for each split half's own
    # highlight and "Interact" icon.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/target_statements.cpp
    # The Bink players' open, rect, close and the startup player's skip byte, stated for the
    # host's native playback (core/movie_state.h).
    ${CMAKE_CURRENT_SOURCE_DIR}/src/movie_player.cpp
    # The present paced to the guest's stated vblank, where the 3D Engine loop holds no lock.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/present_pacing.cpp
    # The guest tick pump: the host's posted work drained once per HF step, and the step timed.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/game_tick_pump.cpp
    # The "Press START" splash's state, stated for the host's native screen.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/splash_screen.cpp
    # The camera the guest renders the world with, and its preload gate held while Godot builds.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/view_feed.cpp
    # The camera component's setters, activation, easing step and push, stated in the guest's order.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/camera_statements.cpp
    # The UI cut's seam: the `GUI.*` natives the game VM calls, with their arguments, for the host.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/gui_natives.cpp
    # The Gameface elements the guest writes (resolver + state/opacity/text setters), stated by path.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/gui_element_hooks.cpp
    # The UI's values stated at their writers' entry — texts, visibility, the presentation's
    # document, the interaction mode's panels, buttons and list — for Godot to draw headless.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/ui_stated_hooks.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/screen_cut_hooks.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/stats_screen_stubs.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/owned_screen_texts.cpp
    # Headless Gameface presentations through the shared core (core/headless_presentation.h):
    # the maker and destroy, the presentation's own slots, the parameter setters and the chunk
    # runner; Fable 3's Gameface table beside it.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/headless_presentation_hooks.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/headless_table.h
    # The readers of the presentations' per-frame runtime, answered from statements.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/ui_reader_hooks.cpp
    # The element service, slide manager and direct readers answered for the elements' handles.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/stated_element_hooks.cpp
    # The HUD text boxes' lines and the TopBox queue's post, removal, clear and answer.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/topbox_hooks.cpp
    # The HUD root, save menu, Options query, HUD widgets and box screens by the values they carry.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/screen_stated_hooks.cpp
    # The texts a box or HUD widget commits straight to its text box, stated at the commit's guard.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/headless_commit_hooks.cpp
    # The display boxes, the pause screen's message box and the menu box, stated at their push,
    # show, drop and close; Godot owns their pad and its answers run their own answer functions.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/display_box_hooks.cpp
    # The boxes' methods that draw (the intermission's show, hide and update, the card's update),
    # replaced by their game side alone; the TopBox's press answered from Godot through its own input.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/display_box_stubs.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/topbox_answer.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/save_menu_confirm.cpp
    # The real-estate mode's two answer polls, run only on the answer of a box the mode asked with.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/real_estate_confirm.cpp
    # The real-estate mode's rows stated, its button show cut, and the row lever.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/real_estate_mode.cpp
    # The shop's and the pawn shop's field stores, stated at the store instructions.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/commerce_stated_stores.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/save_menu_row.cpp
    # The session boxes (join / visibility, friends channel, co-op partners) and the family boxes
    # (bed, divorce, treasury, marriage proposal, food query): their pad Godot's, their answers the
    # boxes' own handlers; the divorce runs on its own confirmation's answer alone.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/session_boxes.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/family_boxes.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/divorce_confirm.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/pc_extras.cpp
    # The world map's close as the host asks for it: the guest's own WorldMap.Exit on the top mode.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/world_map_close.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/box_answers.h
    # The HUD's pad from Godot, each lever through the guest's own input for that surface, which the
    # guest's pad no longer reaches: an emotion choice's button, the world icon's hold, the d-pad,
    # and the interaction mode's pad while the mode up is the NPC's or the dog's.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/hud_levers.h
    ${CMAKE_CURRENT_SOURCE_DIR}/src/interaction_guiworld.h
    ${CMAKE_CURRENT_SOURCE_DIR}/src/emotion_choice_answer.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/world_icon_hold.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/hud_dpad_use.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/interaction_mode_input.cpp
    # The options: the appliers' values and the saved settings' read and write, stated to the
    # table the host bridge's options feed reads; the Options screen's state, its pad Godot's.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/options_hooks.cpp
    # The world map's game mode: its enter and leave stated, its exit and close logged; its buttons
    # Godot's, handed to its own forwarders as the player who owns it.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/map_input.cpp
    # The pad's road into the GUI's two input receivers, counted (observe only).
    ${CMAKE_CURRENT_SOURCE_DIR}/src/gui_input_mask.cpp
    # The epilogue and credits screens, stated as the screen manager opens and closes them.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/end_screen_hooks.cpp
    # The system keyboard the guest opens (the rename), answered by the host.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/keyboard_ui.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/keyboard_ui.h
    # No object added to a render scene the 3D Engine has already destroyed.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/scene_guard.cpp
    # The disc's AI: the deferred bot deletes flushed blocking at load, save and unload, and the
    # pathfinder worker passing over an entry no longer queued, as title update 3 does.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/bot_flush_hooks.cpp
    # The world: a level's up and down, each effect's creation, placement and end, each decal.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/world_hooks.cpp
    # Who plays an effect: the CECParticleEmitter that made its handle and the entity it is attached to.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/fx_owner_hooks.cpp
    # The map tables: the scene the world/town map presents, its camera, its meshes and their commands.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/map_table_hooks.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/map_table_hooks.h
    # Each character's appearance: the morph target lists and texture commands its draw virtual
    # hands the renderer, stated per entity.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/appearance_hooks.cpp
    # Each cloth piece's drawn vertices as the guest's draw publishes them to its vertex buffer,
    # and the piece's end.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/cloth_hooks.cpp
    # Each particle group's and trail's stream as the particle system's own writers lay it into
    # the frame's vertex buffer, and the render object each group's message is posted to.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/classic_particle_hooks.cpp
    # The mouse's turn, handed whole to the active camera mode around the camera manager's update.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/mouse_look.cpp
    # Player two's own camera manager in couch co-op: the gates, its update, the selector's player-two answer.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/coop_camera_hooks.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/coop_camera_readers.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/coop_particle_view.cpp
    # Each split half's offscreen hero / partner marker: tested against that half's camera, switched by it.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/coop_player_icons.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/coop_camera.h
    # The per-tick Lua GC slice, run against the real clock instead of the host-granted time base.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/lua_gc_slice.cpp
    # Mods' Lua in the game VM: the composed chunks after the startup scripts, the Refii.* natives,
    # and the host's mod lanes (commands, input, tick, the Lua masks) on the VM's own thread.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/mod_lua_hooks.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/mod_lua.h
    # The gameplay seams mods hear: deaths, health, gold, renown, guild seals, abilities, the treasury,
    # quests, fast travel, the kingdom's decisions and the level edges, raised from the next tick.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/mod_events_hooks.cpp
    # Which save each user's hero comes from (the load, the join, the adoption), for the per-user
    # achievement enumerator; the join never adopts the save player one plays.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/character_binding_hooks.cpp
    # The combat and control seams: hits, the health's take, the flinch, the block, strikes, rolls,
    # ability requests, animation events, movement's interrupt, GameActions, the target and its lock,
    # spells, the look-at flag and the cutscene statements; the action and lock helpers.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/mod_combat_hooks.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/mod_seam.h
    # The native (hard) mod C API of core/refii_mod.h and core/refii_mod_events.h, answered on Fable 3:
    # the exported seams, the game VM's Refii.* helpers, and the typed events over the text bus.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/mod_api.cpp
    # The CRT's pow/sin/cos/... answered by the host's libm.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/crt_math.cpp
    # The CRT's memcpy/memmove/memset/XMemCpy answered by the host's libc.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/crt_memory.cpp
    # The Limited Collector's Edition licence (IsPackagePremium of the Day One package).
    ${CMAKE_CURRENT_SOURCE_DIR}/src/unlock_hooks.cpp
    # Each hero's leaderboard statistics stay in its save: no Live leaderboard service to sync with.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/leaderboard_sync_gate.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/preload_hooks.cpp
    # The root file system's Open and Exists: the host mount's override served as a guest memory
    # file (the decision is Fable3/src/vfs's, refiii_vfs). Its C ABI header is listed so the
    # staging ships it, laid out relative to Fable3/src as the hook includes it.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/vfs_hooks.cpp
    ${CMAKE_SOURCE_DIR}/Fable3/src/vfs/include/fable3_vfs.h
    # The loading screen, stated (creates, destroy, art, close, update) for Godot, which draws it,
    # and its quads cut; its state machine runs unchanged.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/loading_screen_scene.cpp
    # The aim reticules' setters (ranged aim, spell target), stated as `@Reticule` per player and
    # slot and each record, as the setter left it, to the table the host bridge's reticle feed reads.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/reticle_hooks.cpp
    # The world icons (Init, Update, ring tick, erase, destroy) and the emotion choice records
    # (add, input, delete), stated to the table the host bridge's icon feed reads.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/world_icon_hooks.cpp
    # The HUD registries (insert, unfile, destroy, the text and meter value writers) and the
    # screen fade (start, advance), stated to the table the host bridge's HUD feed reads; the
    # slide registries' writers, the HUD's enable and reset, and the d-pad's slide and slots,
    # stated as `@Hud.*`.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/hud_hooks.cpp
    # The interaction mode's set and close and its info panel id, stated for Godot's interaction
    # document; the mode, its objects and its pad stay the guest's.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/interaction_hooks.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/interaction_list_step.cpp
    # The Sanctuary's mode (mode 3): its pad cut and Godot's `guiworld:` levers.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/interaction_guiworld.cpp
    # The game mode stacks (push, pop, run), the GUI screen id's writers and the culture table
    # (build, rebuild, switch), stated to the table the host bridge's level and options feeds read.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/game_state_hooks.cpp
    # What the UI hooks share (the guest's two string classes, the statement post). Listed with
    # the sources so the staging ships it beside them: the player's build compiles the staged
    # copies, and a header only in the tree is not there.
    ${CMAKE_CURRENT_SOURCE_DIR}/src/gui_statements.h
)

add_library(refiii_hooks OBJECT ${REFIII_HOOK_SOURCES})
set_target_properties(refiii_hooks PROPERTIES POSITION_INDEPENDENT_CODE ON)

# The shared runtime's include directory ahead of the SDK's, as Fable 2's hooks have it: it
# carries the payload's rex/thread/mutex.h and the C logging bridge.
target_include_directories(refiii_hooks PRIVATE ${REFII_RUNTIME_INCLUDE_DIRS})
target_link_libraries(refiii_hooks PRIVATE rex::runtime)

# The hard-mod contract (core/refii_mod.h, core/refii_mod_events.h, core/guest_build.h) lives in
# Fable2/src/vfs/include, which the player's build stages among its include directories; the dev
# build gives it to mod_api.cpp alone, whose refii::guest is Fable 2's header's (no other hook here
# may see that namespace beside fable3_guest_build.h's).
set_source_files_properties(${CMAKE_CURRENT_SOURCE_DIR}/src/mod_api.cpp PROPERTIES
    INCLUDE_DIRECTORIES "${CMAKE_SOURCE_DIR}/Fable2/src/vfs/include")

# Payload-shaped, as Fable2/src/hooks/CMakeLists.txt explains: logging crosses as C, no Tracy.
target_compile_definitions(refiii_hooks PRIVATE REFII_PAYLOAD_LOG_BRIDGE=1)
target_compile_options(refiii_hooks PRIVATE "-UTRACY_ENABLE" "-UTRACY_MANUAL_LIFETIME" "-UTRACY_DELAYED_INIT" "-UTRACY_ON_DEMAND" "-UTRACY_FIBERS" "-UTRACY_IMPORTS" "-UREXGLUE_ENABLE_PROFILING")

# THE PAYLOAD/HOST ABI RULE, checked for these hooks as for Fable 2's (cmake/refii_check_payload_abi.cmake):
# a hook importing a C++ symbol from a host library links on Linux and fails the player's
# Windows build. REFII_NM is found by Fable 2's hooks, configured first.
if(REFII_CHECK_PAYLOAD_ABI AND REFII_NM)
    set(_refiii_abi_objects "${CMAKE_BINARY_DIR}/payload_abi_objects_refiii_$<CONFIG>.txt")
    # The shared payload sources (refii_payload_core, the headless core among them) are checked
    # with the hooks: they compile into the same module on the same terms, as Fable 2's check has them.
    file(GENERATE OUTPUT "${_refiii_abi_objects}"
         CONTENT "$<JOIN:$<TARGET_OBJECTS:refiii_hooks>,\n>\n$<JOIN:$<TARGET_OBJECTS:refii_payload_core>,\n>\n")
    set(_refiii_abi_stamp "${CMAKE_CURRENT_BINARY_DIR}/payload_abi_checked_$<CONFIG>")
    add_custom_command(
        OUTPUT "${_refiii_abi_stamp}"
        COMMAND ${CMAKE_COMMAND}
            -DOBJECT_LIST=${_refiii_abi_objects}
            -DCACHE_DIR=${CMAKE_CURRENT_BINARY_DIR}/abi_cache_$<CONFIG>
            -DNM=${REFII_NM}
            -DCXXFILT=${REFII_CXXFILT}
            -DUNDNAME=${REFII_UNDNAME}
            -P "${CMAKE_SOURCE_DIR}/cmake/refii_check_payload_abi.cmake"
        COMMAND ${CMAKE_COMMAND} -E touch "${_refiii_abi_stamp}"
        DEPENDS $<TARGET_OBJECTS:refiii_hooks> $<TARGET_OBJECTS:refii_payload_core> "${_refiii_abi_objects}"
        COMMENT "Checking Fable 3's hooks import no C++ symbol from a host library"
        VERBATIM
    )
    add_custom_target(refiii_payload_abi_check ALL DEPENDS "${_refiii_abi_stamp}")
    add_dependencies(refiii_payload_abi_check refiii_hooks)
endif()
