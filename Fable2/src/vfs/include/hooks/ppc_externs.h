#pragma once

#include "../vfs_hooks_private.h"
#include <rex/ppc/context.h>

// Every translation unit that includes this header is compiled INTO the payload — the
// hook sources are listed in REFII_VFS_HOOK_INTERNAL_SOURCES, not in refii_vfs itself —
// so these are ordinary externs the linker resolves within the module.

#if !defined(PPC_EXTERN_FUNC)
#if defined(REX_EXTERN)
#define PPC_EXTERN_FUNC(symbol) REX_EXTERN(symbol)
#elif defined(REX_FUNC)
#define PPC_EXTERN_FUNC(symbol) extern "C" REX_FUNC(symbol)
#else
#define PPC_EXTERN_FUNC(symbol) extern "C" void symbol([[maybe_unused]] PPCContext& __restrict ctx, uint8_t* base)
#endif
#endif

PPC_EXTERN_FUNC(__imp__rex_lhResourceManager_OpenInternal);
PPC_EXTERN_FUNC(__imp__rex_lhResourceManager_FindAndCreateStream);
PPC_EXTERN_FUNC(__imp__rex_lhVFS_RequestManager_SubmitRequest);
PPC_EXTERN_FUNC(__imp__rex_lhFontGlyphVector_Resize);
PPC_EXTERN_FUNC(__imp__rex_lhMem_ReserveCommitAlignedVirtual);
PPC_EXTERN_FUNC(__imp__rex_lhVFS_ProviderSet_OpenCompositeDevice);
PPC_EXTERN_FUNC(__imp__rex_lhVFS_CompositeDevice_CreateRequest);
PPC_EXTERN_FUNC(__imp__rex_lhVFS_Win32Request_WaitForCompletion);
PPC_EXTERN_FUNC(__imp__rex_lhVFS_Win32Request_GetBufferSpec);
PPC_EXTERN_FUNC(__imp__rex_lhVFS_Win32Request_GetSize);
PPC_EXTERN_FUNC(__imp__rex_lhVFS_Win32Request_ForwardToCompletionTarget);
PPC_EXTERN_FUNC(__imp__rex_lhVFS_Win32Request_SetCallbackAndSubmit);
PPC_EXTERN_FUNC(__imp__rex_lhVFS_Request_ForwardInnerGetSize);
PPC_EXTERN_FUNC(__imp__rex_lhVFS_Win32Request_GetDevice); // Lumen: LHVFS::CWin32Request::GetDevice(void)
PPC_EXTERN_FUNC(__imp__rex_lhBufferedReader_Read);
PPC_EXTERN_FUNC(__imp__rex_lhMeshResourceContainer_Remove);
PPC_EXTERN_FUNC(__imp__rex_lhTextureStreamRequest_OpenStream);
PPC_EXTERN_FUNC(__imp__rex_lhTextureStreamRequest_LoadPixels);
PPC_EXTERN_FUNC(__imp__rex_lhDataInputStream_SetPosition); // Lumen: LHFile::CDataInputStream::SetPosition(uint)
PPC_EXTERN_FUNC(__imp__rex_lhTextureHeader_ReadFromStream);
PPC_EXTERN_FUNC(__imp__rex_lhTexture_LoadCubeMipsFromStream);
PPC_EXTERN_FUNC(__imp__rex_lhTexture_DecodeCubeFromStream);
PPC_EXTERN_FUNC(__imp__rex_lhTexture_BuildMipTextureHeader);
PPC_EXTERN_FUNC(__imp__rex_lhTexture_LoadMipsFromStream);
PPC_EXTERN_FUNC(__imp__rex_lhTexture_DecodeFromStream);
PPC_EXTERN_FUNC(__imp__rex_lhTexture_CreateFromStream);
PPC_EXTERN_FUNC(__imp__rex_lhTexture_LoadFromStreamWithHeader);
PPC_EXTERN_FUNC(__imp__rex_lhTexture_LoadFromStream);
PPC_EXTERN_FUNC(__imp__rex_lhTextureLock_ScalarDeletingDtor);
PPC_EXTERN_FUNC(__imp__rex_lhTextureLock_Dtor);
PPC_EXTERN_FUNC(__imp__rex_lhTextureLock_LockTexture);
PPC_EXTERN_FUNC(__imp__rex_lhTextureLock_LockSurface);
PPC_EXTERN_FUNC(__imp__rex_lhTextureLock_Unlock);
PPC_EXTERN_FUNC(__imp__rex_lhGfx_ReleaseVertexDecl);
PPC_EXTERN_FUNC(__imp__rex_lhGfx_BufferHandle_Assign);
PPC_EXTERN_FUNC(__imp__rex_lhGfx_LockWholeBuffer);
PPC_EXTERN_FUNC(__imp__rex_lhRect_SubtractIntoList);
PPC_EXTERN_FUNC(__imp__rex_lhGfx_CreateCubeTexture);
PPC_EXTERN_FUNC(__imp__rex_lhTexture_GetMemorySize);
PPC_EXTERN_FUNC(__imp__rex_lhTexture_LockCubeFace);
PPC_EXTERN_FUNC(__imp__rex_lhRenderBufferOwner_Ctor);
PPC_EXTERN_FUNC(__imp__rex_lhRenderBufferOwner_ScalarDeletingDtor);
PPC_EXTERN_FUNC(__imp__rex_lhRenderBufferOwner_Dtor);
PPC_EXTERN_FUNC(__imp__rex_lhTextureDecoder_GetInstance);
PPC_EXTERN_FUNC(__imp__rex_lhRefPtr_Assign);
PPC_EXTERN_FUNC(__imp__rex_lhVFS_PagedStream_Read);
PPC_EXTERN_FUNC(__imp__rex_lhVFS_PagedStream_Write);
PPC_EXTERN_FUNC(__imp__rex_lhVFS_PagedStream_FlushPage);
PPC_EXTERN_FUNC(__imp__rex_lhVFS_PagedStream_LoadPageAt);
PPC_EXTERN_FUNC(__imp__rex_lhLockFreeQueue_Push);
PPC_EXTERN_FUNC(__imp__rex_lhLockFreeQueue_Pop);
PPC_EXTERN_FUNC(__imp__rex_stdListDword_Insert);
PPC_EXTERN_FUNC(__imp__rex_lhActionHandlerRegistry_AddHandler);
PPC_EXTERN_FUNC(__imp__rex_lhActionHandlerRegistry_RemoveHandler);
PPC_EXTERN_FUNC(__imp__rex_lhActionHandlerRegistry_SetActiveTypesForPlayer);
PPC_EXTERN_FUNC(__imp__rex_lhActionHandlerRegistry_CollectHandlersByPlayerAndType);
PPC_EXTERN_FUNC(__imp__rex_lhDeferredDestroyQueue_DrainWithTimeBudget);
PPC_EXTERN_FUNC(__imp__rex_lhAudioObject_LoadFromStream);
PPC_EXTERN_FUNC(__imp__rex_lhAudioObject_LoadFromFile);
PPC_EXTERN_FUNC(__imp__rex_lhVFS_Win32Device_ScalarDeletingDtor);
PPC_EXTERN_FUNC(__imp__rex_lhVFS_Win32Device_CreateRequest);
PPC_EXTERN_FUNC(__imp__rex_lhVFS_BasicBufferedRequest_WaitForCompletion);
PPC_EXTERN_FUNC(__imp__rex_lhVFS_BasicBufferedRequest_GetResultBuffer);
PPC_EXTERN_FUNC(__imp__rex_lhVFS_BasicBufferedRequest_GetStatus);
PPC_EXTERN_FUNC(__imp__rex_lhVFS_BasicBufferedRequest_GetDevice); // Lumen: LHVFS::CBasicBufferedRequest::GetDevice(void)
PPC_EXTERN_FUNC(__imp__rex_lhIoRefCounted_Release);
PPC_EXTERN_FUNC(__imp__rex_lhIoRefCounted_AddRef);
PPC_EXTERN_FUNC(__imp__rex_lhVFS_Win32Request_GetStatus);
PPC_EXTERN_FUNC(__imp__rex_lhStream_ReturnENotImpl);
PPC_EXTERN_FUNC(__imp__rex_lhStream_ReturnEFail);
PPC_EXTERN_FUNC(__imp__rex_lhRenderState_ResetAllToDefaults);
PPC_EXTERN_FUNC(__imp__rex_lhGfx_InitRenderStateTable);
PPC_EXTERN_FUNC(__imp__rex_lhGfx_ApplyDefaultRenderStates);
PPC_EXTERN_FUNC(__imp__rex_lhGfx_InitStandardVertexDecls);
PPC_EXTERN_FUNC(__imp__rex_lhResourceWrapper_GetSize);
PPC_EXTERN_FUNC(__imp__rex_lhVFS_RequestManager_FindStreamByHashEntry);
PPC_EXTERN_FUNC(__imp__rex_lhVFS_RequestManager_BuildProviderHash);
PPC_EXTERN_FUNC(__imp__rex_lhLockedIdTable_QueueEntryUpdate);
PPC_EXTERN_FUNC(__imp__rex_D3DDevice_InitializeRingBuffer);
PPC_EXTERN_FUNC(__imp__rex_lhFile_ReadSequential);

// Render_DrawBinkMovieFrame — the game's own call into Bink, hooked to capture the BINK
// handle so the host can find the CPU-decoded video planes. Hooking inside Bink itself
// does not work; it dispatches through a function-pointer table. See
// hooks/bink_frame_capture.cpp.
// luaD_precall_2 — the GAME VM's copy, and THE single site through which every
// lua_CFunction in that VM is invoked (the indirect call at 0x822289F0). Hooked once to
// intercept the ~120 semantic GUI natives; see hooks/gui_natives.cpp.
//
// The title links TWO copies of the Lua library. luaD_precall (0x8219EF68) belongs to the
// GAMEFACE UI VM — hooking it fires tens of thousands of times and never sees a GUI
// native, because RegisterGUI registers into the game VM. The copies are not even
// layout-identical: the hook-mask byte is at L+56 here and L+54 there.
PPC_EXTERN_FUNC(__imp__rex_luaD_precall);
// The display MessageBox's dtor, Open and Close (vtable 0x820AE668), stated to the host — see
// hooks/display_box_hooks.cpp.
PPC_EXTERN_FUNC(__imp__rex_lhDisplayMessageBox_Dtor);
PPC_EXTERN_FUNC(__imp__rex_lhDisplayMessageBox_Open);
PPC_EXTERN_FUNC(__imp__rex_lhIntermissionBox_Open);
PPC_EXTERN_FUNC(__imp__rex_lhIntermissionBox_Close);
PPC_EXTERN_FUNC(__imp__rex_lhAcceptBox_Show);
PPC_EXTERN_FUNC(__imp__rex_lhAcceptBox_Close);
// The display boxes' class Open (vf1) and Close (vf2) and the menu box's populate and close,
// stated to the host — hooks/display_box_hooks.cpp.
PPC_EXTERN_FUNC(__imp__rex_lhQueryBox_Open);
PPC_EXTERN_FUNC(__imp__rex_lhQueryBox_Close);
PPC_EXTERN_FUNC(__imp__rex_lhNumericAdjusterBox_Open);
PPC_EXTERN_FUNC(__imp__rex_lhNumericAdjusterBox_Close);
PPC_EXTERN_FUNC(__imp__rex_lhQuestInfoBox_Open);
PPC_EXTERN_FUNC(__imp__rex_lhQuestInfoBox_Close);
PPC_EXTERN_FUNC(__imp__rex_lhAcceptHoldBox_Open);
PPC_EXTERN_FUNC(__imp__rex_lhAcceptHoldBox_Close);
PPC_EXTERN_FUNC(__imp__rex_lhInfoBox_Dtor);
PPC_EXTERN_FUNC(__imp__rex_lhQuestInfoBox_Dtor);
PPC_EXTERN_FUNC(__imp__rex_lhAcceptHoldBox_Dtor);
PPC_EXTERN_FUNC(__imp__rex_lhSlowTimeDial_Draw);
PPC_EXTERN_FUNC(__imp__rex_lhSlowTimeDial_Close);
PPC_EXTERN_FUNC(__imp__rex_lhGuiCrimeScreen_Open);
PPC_EXTERN_FUNC(__imp__rex_lhGuiLogbookScreen_OpenOnPlayer);
PPC_EXTERN_FUNC(__imp__rex_lhGuiLogbookStatsPage_Fill);
PPC_EXTERN_FUNC(__imp__rex_lhCoopScreen_Open);
PPC_EXTERN_FUNC(__imp__rex_lhGuiPreviewScene_FileRecord);
PPC_EXTERN_FUNC(__imp__rex_lhGuiPreviewScene_DestroyRecordEntity);
PPC_EXTERN_FUNC(__imp__rex_lhGuiShopScreen_Open);
PPC_EXTERN_FUNC(__imp__rex_lhGuiPreviewObject_Set);
PPC_EXTERN_FUNC(__imp__rex_lhGuiPreviewObject_Hide);
PPC_EXTERN_FUNC(__imp__rex_lhGuiPendingWeapons_Clear);
PPC_EXTERN_FUNC(__imp__rex_lhHudExperienceCounter_ShowHeroOne);
PPC_EXTERN_FUNC(__imp__rex_lhHudExperienceCounter_ShowHeroTwo);
PPC_EXTERN_FUNC(__imp__rex_lhHudExperienceCounter_DecideShown);
PPC_EXTERN_FUNC(__imp__rex_lhGuiRowTickets_Allocate);
PPC_EXTERN_FUNC(__imp__rex_lhGuiRowTickets_Clear);
PPC_EXTERN_FUNC(__imp__rex_lhMenuBox_Populate);
PPC_EXTERN_FUNC(__imp__rex_lhMenuBox_Close);
PPC_EXTERN_FUNC(__imp__rex_lhGuiManager_OpenModal);
PPC_EXTERN_FUNC(__imp__rex_lhGuiRegistry_SetToggle);
PPC_EXTERN_FUNC(__imp__rex_lhGuiRegistry_SetSlide);
PPC_EXTERN_FUNC(__imp__rex_lhGuiRegistry_JumpSlide);
// The meter and text writers, stated at their entry — hooks/gui_registry_hooks.cpp.
PPC_EXTERN_FUNC(__imp__rex_lhGuiRegistry_SetMeter);
PPC_EXTERN_FUNC(__imp__rex_lhGuiRegistry_SeekMeter);
PPC_EXTERN_FUNC(__imp__rex_lhGuiRegistry_RunMeter);
PPC_EXTERN_FUNC(__imp__rex_lhGuiRegistry_ScrubMeter);
PPC_EXTERN_FUNC(__imp__rex_lhGuiRegistry_SetText);
// Every explicit slide statement and play/pause on a presentation — hooks/gui_slide_hooks.cpp.
PPC_EXTERN_FUNC(__imp__rex_lhGfxTimeline_SetPaused);
PPC_EXTERN_FUNC(__imp__rex_lhGfxElements_EvaluateTracks);
PPC_EXTERN_FUNC(__imp__rex_lhGuiManager_OpenNumericAdjuster);
PPC_EXTERN_FUNC(__imp__rex_lhDisplayMessageBox_Close);
// The presentation-event callbacks the host posts — hooks/gui_menu_hooks.cpp.
PPC_EXTERN_FUNC(__imp__rex_lhGuiFrontEndTitle_OnEndLegal);
PPC_EXTERN_FUNC(__imp__rex_lhGuiFrontEndTitle_OnEndStarted);
PPC_EXTERN_FUNC(__imp__rex_lhGuiFrontEndTitle_OnCanPressA);
PPC_EXTERN_FUNC(__imp__rex_lhGuiFrontEndTitle_OnDialogEvent);
PPC_EXTERN_FUNC(__imp__rex_lhGuiFrontEndRoot_OnOutroFinished);
PPC_EXTERN_FUNC(__imp__rex_lhGui_FrontendRoot_HandleGuiEvent);
PPC_EXTERN_FUNC(__imp__rex_lhGuiFrontEndRoot_OnLoadCardEvent);
PPC_EXTERN_FUNC(__imp__rex_lhGuiFrontEndRoot_OnDialogEvent);
PPC_EXTERN_FUNC(__imp__rex_lhGuiHud_OnFinishedTransitioningToGame);
// The front end's stated model — hooks/frontend_stated.cpp.
PPC_EXTERN_FUNC(__imp__rex_lhFrontEndGui_Ctor);
PPC_EXTERN_FUNC(__imp__rex_lhFrontEndGui_ScalarDtor);
PPC_EXTERN_FUNC(__imp__rex_lhFrontEndGui_SetActiveScreen);
PPC_EXTERN_FUNC(__imp__rex_lhGuiFrontEndTitle_Activate);
PPC_EXTERN_FUNC(__imp__rex_lhGui_FrontendTitle_OnScreenAction);
PPC_EXTERN_FUNC(__imp__rex_lhGui_FrontendRoot_OnScreenAction);
PPC_EXTERN_FUNC(__imp__rex_lhGui_FrontendRoot_MenuActionDispatch);
PPC_EXTERN_FUNC(__imp__rex_lhGuiFrontEndRoot_SelectCard);
PPC_EXTERN_FUNC(__imp__rex_lhGuiFrontEndRoot_BuildSaveTexts);
PPC_EXTERN_FUNC(__imp__rex_lhSaveManager_ReadSaveHeader);
PPC_EXTERN_FUNC(__imp__rex_lhWideStringVector_PushBack);
// hooks/reticule_hooks.cpp.
PPC_EXTERN_FUNC(__imp__rex_lhGui_Reticule_SetActiveForObject);
PPC_EXTERN_FUNC(__imp__rex_lhGameInit_CreateGameSystems);
PPC_EXTERN_FUNC(__imp__rex_lhResourceManager_QueryNameHash);
PPC_EXTERN_FUNC(__imp__rex_lhLuaFunctionRef_ProtectedCall);
PPC_EXTERN_FUNC(__imp__rex_lhLuaBindings_RegisterGameNamespaces);
PPC_EXTERN_FUNC(__imp__rex_lhLua_GcSliceStep);
PPC_EXTERN_FUNC(__imp__rex_lhFactionManager_Init);
PPC_EXTERN_FUNC(__imp__rex_lhRepeatDelayTable_InitFromGDB);
PPC_EXTERN_FUNC(__imp__rex_lhGDBDefRegistry_RegisterEntriesFromDef);
PPC_EXTERN_FUNC(__imp__rex_lhDynamicRewards_LoadSettings);
PPC_EXTERN_FUNC(__imp__rex_lhSpeechFrequency_LoadLifetimeSettings);
PPC_EXTERN_FUNC(__imp__rex_lhInputMap_LoadFromGameData);
PPC_EXTERN_FUNC(__imp__rex_lhCharString_CtorConcat);
PPC_EXTERN_FUNC(__imp__rex_lhLuaScriptSystem_LoadStartupScripts);
PPC_EXTERN_FUNC(__imp__rex_lhNetManager_ResetSessionState);
PPC_EXTERN_FUNC(__imp__rex_lhLuaConsole_RunStartupConsoleScripts);

// The registration side channel — runs between pushcclosure and setfield for EVERY Lua
// native, with the closure on the stack and the name in r5. Hooked to build impl -> name
// without an address table, because the shipped metadata's address column is the THUNK for
// some entries and thunks are shared between different natives.
PPC_EXTERN_FUNC(__imp__rex_lhLuaNamespace_RegisterFunctionHash);

// Value-returning GUI thunks, hooked so the host can answer a predicate in place of the guest:
//   sub_822AA418  no args -> BOOLEAN: top = *(L+8); top[1] = 1 (LUA_TBOOLEAN); top[0] = value;
//                 *(L+8) += 8; return 1
//   sub_82197FF0  no args -> FLOAT (lua_Number is float): *(float*)top = value;
//                 *(u32*)(top+4) = 3 (LUA_TNUMBER); return 1
// `sub_82309EE8` (one string -> boolean) is absent: its answer depends on the argument.
PPC_EXTERN_FUNC(__imp__rex_lhLuaBind_ThunkReturnBool);
PPC_EXTERN_FUNC(__imp__rex_lhLuaBind_ThunkReturnFloat);

// Expression-meter void thunks, hooked so a host-owned native can be suppressed. All six
// return zero Lua results, so the hook sets r3 = 0 and returns.
//   sub_82309D48  no arguments          TurnOn/TurnOffExpressionMeter
//   sub_822B7BB8  1 number              StartBarJobFill
//   sub_823C00C8  (int, int, int)       SetLow/Med/HighFillColour
//   sub_8230A760  (number, number)      SetFillColourPercentages
//   sub_8230A7F0  inline, 5 arguments   TurnOnExpressionStaticFill
//   sub_8230A8C8  inline, 4 arguments   TurnOnExpressionDynamicFill
PPC_EXTERN_FUNC(__imp__rex_lhLuaBind_ThunkVoidNoArgs);
PPC_EXTERN_FUNC(__imp__rex_lhLuaBind_ThunkVoidOneNumber);
PPC_EXTERN_FUNC(__imp__rex_LuaAPI_SetHeroSpellTableItem);
PPC_EXTERN_FUNC(__imp__rex_lhLuaBind_ThunkVoidTwoNumbers);
PPC_EXTERN_FUNC(__imp__rex_lhLuaBind_ThunkVoidFiveArgs);
PPC_EXTERN_FUNC(__imp__rex_lhLuaBind_ThunkForwardToDispatcher);

PPC_EXTERN_FUNC(__imp__rex_lhBinkPlayer_Open);
PPC_EXTERN_FUNC(__imp__rex_lhBinkPlayer_SetDestRect);
PPC_EXTERN_FUNC(__imp__rex_lhBinkPlayer_SetTrackVolume);
PPC_EXTERN_FUNC(__imp__rex_lhRenderer_EndScene);
PPC_EXTERN_FUNC(__imp__rex_lhApp_IdleWaitForFlags);
PPC_EXTERN_FUNC(__imp__rex_lhRenderer_DrawScene);
PPC_EXTERN_FUNC(__imp__rex_lhRenderView_UpdatePrewarm);
PPC_EXTERN_FUNC(__imp__rex_lhGameLoop_HighFrequencyUpdate);
PPC_EXTERN_FUNC(__imp__rex_lhStreamedTexture_PollPendingCompletions);
PPC_EXTERN_FUNC(__imp__rex_lhTextureStreamJob_PublishMipResidency);
PPC_EXTERN_FUNC(__imp__rex_lhFile_GetStreamSizeLow);
PPC_EXTERN_FUNC(__imp__rex_lhFile_HasData);
PPC_EXTERN_FUNC(__imp__rex_lhFile_GetDataPointer);
PPC_EXTERN_FUNC(__imp__rex_lhIntrusivePtr_Assign);
PPC_EXTERN_FUNC(__imp__rex_lhIntrusivePtr_Reset);
PPC_EXTERN_FUNC(rex_lhCharString_Ctor); // Lumen: LHString::CCharString::CCharString(void)
PPC_EXTERN_FUNC(__imp__rex_lhGui_AcquireFontFromActiveContext);
PPC_EXTERN_FUNC(__imp__rex_lhFoldedGetter_DwordAtOffsetThirtyTwo);
PPC_EXTERN_FUNC(__imp__rex_SetEvent); // Lumen: PulseEvent
PPC_EXTERN_FUNC(__imp__rex_lhProfile_GetSubtitlesEnabled);  // the profile's subtitles, its reader and setter (frontend_stated.cpp)
PPC_EXTERN_FUNC(__imp__rex_lhProfile_SetSubtitlesEnabled);
PPC_EXTERN_FUNC(__imp__rex_lhFrontEndMode_Enter);
PPC_EXTERN_FUNC(__imp__rex_lhFrontEndMode_Exit);
PPC_EXTERN_FUNC(__imp__rex_lhSyncEvent_Wait);
PPC_EXTERN_FUNC(__imp__rex_lhRenderCommandStream_Drain); // the command-stream drain; headless it spins its whole budget
PPC_EXTERN_FUNC(__imp__rex_CECGraphicAppearance_SetHighlightColour); // CECGraphicAppearance: states the highlight RGBA
PPC_EXTERN_FUNC(__imp__rex_lhECTargeted_SetTargetedByPlayer); // CECTargeted: states that an entity became a target
PPC_EXTERN_FUNC(__imp__rex_CECGraphicAppearance_ReplicateAppearance); // CECGraphicAppearance: every appearance setter ends here
PPC_EXTERN_FUNC(__imp__rex_CECGraphicAppearance_Serialize); // CECGraphicAppearance: save/load of Alpha, TintColour, ...
PPC_EXTERN_FUNC(__imp__rex_CECGraphicAppearance_Ctor); // CECGraphicAppearance: constructor
PPC_EXTERN_FUNC(__imp__rex_CECGraphicAppearance_ScalarDtor); // CECGraphicAppearance: destructor
PPC_EXTERN_FUNC(__imp__rex_CECGraphicAppearance_ApplyDefinition); // CECGraphicAppearance vf8: the definition's flags (DisableAlphaDepthWrites +192)
PPC_EXTERN_FUNC(__imp__rex_lhGraphicAppearanceMorph_ApplyTextureLayers); // CECGraphicAppearanceMorph: the composite submit
PPC_EXTERN_FUNC(__imp__rex_lhGraphicAppearanceMorph_RebuildParts); // CECGraphicAppearanceMorph: rebuilds the part records
PPC_EXTERN_FUNC(__imp__rex_lhSetTextureLayersCommand_Ctor); // texture composite command: one original, its layers
PPC_EXTERN_FUNC(__imp__rex_lhTextureMorph_StateWildcard); // a whole-entity texture swap: every diffuse/specular/normal map
PPC_EXTERN_FUNC(__imp__rex_lhObjectAttachment_ResolveLoaded); // component 22 resolving loaded AttachedEntity records
PPC_EXTERN_FUNC(__imp__rex_lhMorphDescriptor_Ctor); // morph descriptor constructor: a part and its weighted targets
PPC_EXTERN_FUNC(__imp__rex_lhRenderCommandStream_PostModelCommand); // render command to one model handle; type 14 = overlays
PPC_EXTERN_FUNC(__imp__rex_lhDecal_ExecuteRenderCommand); // decal render command: owner, texture, pos, dir, sizes
PPC_EXTERN_FUNC(__imp__rex_lhDecal_ExecuteClearCommand); // decal clear-all command (region teardown)
PPC_EXTERN_FUNC(__imp__rex_lhRenderTwin_ReleaseDecals); // render twin releasing its decal slots
PPC_EXTERN_FUNC(__imp__rex_lhFx_CreateMeshDescriptor); // FX mesh particle descriptor (sub_82456D58)
PPC_EXTERN_FUNC(__imp__rex_lhRenderTwin_Destroy); // render twin destroy by handle (sub_82175918)
PPC_EXTERN_FUNC(__imp__rex_lhRender_ApplyCommandToHandle); // a model command reaching its render twin (sub_822222E0); 16 alpha, 17 tint
PPC_EXTERN_FUNC(__imp__rex_CParticleSystemEntityInstance_SetPositionAndVelocity); // CParticleSystemEntityInstance: states the world position
PPC_EXTERN_FUNC(__imp__rex_CParticleSystemEntityInstance_SetOrientation); // CParticleSystemEntityInstance: states the orientation
PPC_EXTERN_FUNC(__imp__rex_CParticleSystemEntityInstance_Ctor); // CParticleSystemEntityInstance(typeRef): a mod effect's scale and tint applied (particle_dress.cpp)
PPC_EXTERN_FUNC(__imp__rex_CParticleSystemEntityInstance_SetScale); // SetParticleScale: times the effect's authored scale
PPC_EXTERN_FUNC(__imp__rex_CParticleSystemEntityInstance_GetScale); // GetParticleScale: without it
PPC_EXTERN_FUNC(__imp__rex_CParticleEmitter_StepParticle); // one particle's integrator step: a mod emitter's spin after it (particle_motion.cpp)
PPC_EXTERN_FUNC(__imp__rex_CMeshParticle_InitVisual); // a mesh particle's visual born: a mod system's rest orientation composed in (particle_motion.cpp)
PPC_EXTERN_FUNC(__imp__rex_CECSpellManager_SetSpellSlot); // a spell slot's will; notify set = a pick (SpellPick, mod_abilities.cpp)
PPC_EXTERN_FUNC(__imp__rex_VortexComponent_Update); // the vortex's tick: moves its entity and places its ground FX (spell_fx_owner.cpp)
PPC_EXTERN_FUNC(__imp__rex_DamoclesSword_Update); // one Blades sword record's tick: places its FX entity and instance (spell_fx_owner.cpp)
PPC_EXTERN_FUNC(__imp__rex_FxEntity_PlaceAt); // an FX entity and its particle instance placed at v1 (spell_fx_owner.cpp)
PPC_EXTERN_FUNC(__imp__rex_lhFx_SpawnFromDefProperty); // spell FX: create from <Spell>SpellParticleFXInfo.<slot>
PPC_EXTERN_FUNC(__imp__rex_CRemappingFileSystem_AddRemap);
PPC_EXTERN_FUNC(__imp__rex_lhApp_CreateFrontEnd);
PPC_EXTERN_FUNC(__imp__rex_lhApp_ScalarDeletingDtor);
PPC_EXTERN_FUNC(__imp__rex_lhAppHolder_ScalarDeletingDtor);
PPC_EXTERN_FUNC(__imp__rex_lhAppHolder_Initialise);
PPC_EXTERN_FUNC(__imp__rex_lhAppHolder_Shutdown);
PPC_EXTERN_FUNC(__imp__rex_lhVirtual_ReturnNine);
PPC_EXTERN_FUNC(__imp__rex_lhDebugPrint_Nullsub); // Lumen: nullsub_1
PPC_EXTERN_FUNC(__imp__rex_lhStub_ReturnTwelve);
PPC_EXTERN_FUNC(__imp__rex_lhStub_ReturnOne);
PPC_EXTERN_FUNC(__imp__rex_lhFrontEndState_Ctor);
PPC_EXTERN_FUNC(__imp__rex_lhFrontEndGame_ScalarDtor);
PPC_EXTERN_FUNC(__imp__rex_lhFrontEndGame_VtableSlot6);
PPC_EXTERN_FUNC(__imp__rex_lhGui_PreviewScene_Dtor);
PPC_EXTERN_FUNC(__imp__rex_lhFrontEndState_ScalarDtor);
PPC_EXTERN_FUNC(__imp__rex_lhFrontEndState_Init);
PPC_EXTERN_FUNC(__imp__rex_lhFrontEndState_Shutdown);
PPC_EXTERN_FUNC(__imp__rex_lhFrontEndState_Run);
PPC_EXTERN_FUNC(__imp__rex_lhFrontEndState_AbortPendingStart);
PPC_EXTERN_FUNC(rex_lhFrontEndGame_CreateInGameState);
PPC_EXTERN_FUNC(__imp__rex_lhGuiPresentation_RunLuaString);
PPC_EXTERN_FUNC(__imp__rex_lhInGameState_Init);
PPC_EXTERN_FUNC(__imp__rex_lhInGameState_Shutdown);
PPC_EXTERN_FUNC(__imp__rex_lhInGameState_Run);
PPC_EXTERN_FUNC(__imp__rex_lhApp_RunStartupLoop);
PPC_EXTERN_FUNC(__imp__rex_lhApp_OnSkipMovieInput);
PPC_EXTERN_FUNC(__imp__rex_lhAppHolder_GetRenderer);
PPC_EXTERN_FUNC(__imp__rex_lhFrontEnd_Enter);
PPC_EXTERN_FUNC(__imp__rex_lhRenderCommandStream_PostCreateCommand);
PPC_EXTERN_FUNC(__imp__rex_lhGui_PreviewScene_CreateSceneObjects);
PPC_EXTERN_FUNC(__imp__rex_lhGui_LoadTunableVectorGlobals);
PPC_EXTERN_FUNC(__imp__rex_lhRefPtrPair_Assign);
PPC_EXTERN_FUNC(__imp__rex_lhRefPtrPair_CtorFromMovedPtr);
PPC_EXTERN_FUNC(__imp__rex_lhFrontEnd_BindScrollingElement);
PPC_EXTERN_FUNC(__imp__rex_lhFrontEnd_Update);
// The guest's once-per-tick thunk, on its own "GameThread" — see guest_tick_pump.cpp.
PPC_EXTERN_FUNC(__imp__rex_lhTickDelegate_ForwardToInner);
// Menu spawn and list boundary: `sub_826CE8D0` is `g_Menu:CreateMenuItem`'s C++ side (name,
// parent, id, event type in r4/r7/r8/r9); `sub_826CE110` wraps `DeleteAllSpawnedMenuItems`.
PPC_EXTERN_FUNC(__imp__rex_lhGui_InGameMenu_SpawnButton);
PPC_EXTERN_FUNC(__imp__rex_lhGui_InGameMenu_DeleteAllSpawnedMenuItems);

// The pause-tab lever poller, run EVERY GUI TICK — the one heartbeat that beats while the
// pause book is open (`sub_822C1BD0`, the game-loop tick, stops there). The pause-screen
// samplers ride it: a sampler gated on "screen 23 is up" can never fire from a tick the
// pause stops.
PPC_EXTERN_FUNC(__imp__rex_lhGuiManager_TickUpdate);
// The front end's emitter: `sub_826E4798` is `sub_826CE8D0` against `dword_83496AC0` rather
// than the in-game GUI manager `dword_83496B08`.
PPC_EXTERN_FUNC(__imp__rex_lhGui_Frontend_SpawnButton);
// Six inline list boundaries (world map / abilities / shop / logbook / gift-gold / front end):
// they fire `"g_Menu:DeleteAllSpawnedMenuItems()"` through the GUI Lua exec, not `sub_826CE110`.
PPC_EXTERN_FUNC(__imp__rex_lhGuiWorldMapScreen_PopulateMenu);
PPC_EXTERN_FUNC(__imp__rex_lhGuiAbilitiesScreen_PopulateMenu);
PPC_EXTERN_FUNC(__imp__rex_lhGuiShopScreen_PopulateMenu);
PPC_EXTERN_FUNC(__imp__rex_lhGuiLogbookScreen_PopulateMenu);
PPC_EXTERN_FUNC(__imp__rex_lhGui_LogbookAttractivenessPage_UpdateMeter);
PPC_EXTERN_FUNC(__imp__rex_lhGuiGiftGoldScreen_PopulateMenu);
PPC_EXTERN_FUNC(__imp__rex_lhGui_FrontendRoot_PopulateMainMenu);
PPC_EXTERN_FUNC(rex_lhGuiPresentation_LoadIfUnloaded);
PPC_EXTERN_FUNC(rex_lhGuiPresentation_RunInitScripts);
PPC_EXTERN_FUNC(__imp__rex_lhGui_RunLuaString);
PPC_EXTERN_FUNC(__imp__rex_lhRenderer_SuspendThread);
PPC_EXTERN_FUNC(__imp__rex_lhRenderer_ResumeThread);
PPC_EXTERN_FUNC(__imp__rex_lhRenderer_SetFrameRequest);
PPC_EXTERN_FUNC(__imp__rex_lhRenderer_SetRenderDeadline);
PPC_EXTERN_FUNC(__imp__rex_lhMarketplace_RefreshOfferCounts);
PPC_EXTERN_FUNC(__imp__rex_lhRenderer_FlushAndWaitFrame);
PPC_EXTERN_FUNC(__imp__rex_lhBinkMoviePlayer_Ctor);
PPC_EXTERN_FUNC(__imp__rex_lhBinkMovieHolder_UpdateAndDraw);
PPC_EXTERN_FUNC(__imp__rex_lhLoadingScreen_DrawLoadingAnimation);
PPC_EXTERN_FUNC(__imp__rex_lhLoadingScreen_ParseLoadingScreenData);
PPC_EXTERN_FUNC(__imp__rex_lhLoadingScreen_SetupProjection);
PPC_EXTERN_FUNC(__imp__rex_lhLoadingScreen_UpdateAndRender);
PPC_EXTERN_FUNC(__imp__rex_lhLoadingScreen_CollectTipTexts);
PPC_EXTERN_FUNC(__imp__rex_lhLoadingScreen_DrawTip);
PPC_EXTERN_FUNC(__imp__rex_lhLoadingScreen_DrawLevelTitle);
PPC_EXTERN_FUNC(__imp__rex_lhLoadingScreen_DrawText);
PPC_EXTERN_FUNC(__imp__rex_lhLoadingScreenManager_Render);
PPC_EXTERN_FUNC(__imp__rex_lhLockedStateSingleton_GetInstance);
PPC_EXTERN_FUNC(__imp__rex_lhPubGameManager_RenderFrameUpdate);
PPC_EXTERN_FUNC(__imp__rex_lhScriptTimerSingleton_GetInstance);
PPC_EXTERN_FUNC(rex_lhCutsceneMoviePlayer_StartMovie);
PPC_EXTERN_FUNC(__imp__rex_lhCutsceneMoviePlayer_RenderUpdate);
PPC_EXTERN_FUNC(__imp__rex_lhTextManager_GetString);
PPC_EXTERN_FUNC(__imp__rex_lhTextManager_FindTextGroup);
PPC_EXTERN_FUNC(__imp__rex_lhCharString_AssignIfDifferent);
PPC_EXTERN_FUNC(rex_lhWideString_CtorFromWStr);
PPC_EXTERN_FUNC(__imp__rex_lhLoadingScreenManager_IsActive);
PPC_EXTERN_FUNC(__imp__rex_lhLoadingScreenManager_Show);
PPC_EXTERN_FUNC(rex_lhCharString_CtorFromCStr);
PPC_EXTERN_FUNC(rex_lhCharString_Dtor);
PPC_EXTERN_FUNC(rex_lhCharString_Assign);
PPC_EXTERN_FUNC(rex_lhCharString_AssignCStr);
PPC_EXTERN_FUNC(rex_lhCutsceneMoviePlayer_SetState);
PPC_EXTERN_FUNC(rex_lhPubGameManager_SetState);
PPC_EXTERN_FUNC(rex_lhPubGameManager_StartPubGame);
PPC_EXTERN_FUNC(__imp__rex_lhRenderer_ClearColorScoped);
PPC_EXTERN_FUNC(__imp__rex_lhStreamedTextureDesc_Ctor);
PPC_EXTERN_FUNC(__imp__rex_lhStreamedTextureDesc_CloneToCommandBuffer);
PPC_EXTERN_FUNC(__imp__rex_lhBinkPlayer_Dtor);
PPC_EXTERN_FUNC(__imp__rex_lhBinkPlayer_Close);
PPC_EXTERN_FUNC(rex_stdListDword_InsertWithLengthCheck);
PPC_EXTERN_FUNC(rex_lhGuiPresentation_GetRootElement);
// Gameface presentations built headless (Fable2/src/vfs/src/hooks/gui_headless_presentation.cpp).
PPC_EXTERN_FUNC(__imp__rex_lhGuiPresentation_GetRootElement);
PPC_EXTERN_FUNC(__imp__rex_CGameface_Update);
PPC_EXTERN_FUNC(__imp__rex_lhGuiAttributeService_ChildByName);
PPC_EXTERN_FUNC(__imp__rex_lhGuiAttributeService_CollectChildren);
PPC_EXTERN_FUNC(__imp__rex_GuiElement_GetAttributeString);
PPC_EXTERN_FUNC(rex_lhGuiElement_GoToSlideAndRestart);
PPC_EXTERN_FUNC(rex_lhRenderCommandStream_BeginCommand);
PPC_EXTERN_FUNC(rex_lhRenderCommandStream_Reserve);
PPC_EXTERN_FUNC(rex_lhHandleAllocator_RefillLocalFreeList);
PPC_EXTERN_FUNC(__imp__rex_lhGameApp_PollDoneAndClearPending);
PPC_EXTERN_FUNC(__imp__rex_SwitchToThread); // Lumen: SwitchToThread
PPC_EXTERN_FUNC(__imp__rex_Sleep);
PPC_EXTERN_FUNC(rex_lhResourceName_Dtor);
PPC_EXTERN_FUNC(rex_lhResourceName_CopyCtor);
PPC_EXTERN_FUNC(rex_lhBankDirectory_EntryAt);
PPC_EXTERN_FUNC(rex_lhVFSPathVector_PushBack);
PPC_EXTERN_FUNC(rex_CRemappingFileSystem_GetOrInsertRemapEntry);
PPC_EXTERN_FUNC(__imp__RtlInitializeCriticalSection);
PPC_EXTERN_FUNC(__imp__rex_VirtualAlloc);
PPC_EXTERN_FUNC(__imp__rex_lh_malloc);
PPC_EXTERN_FUNC(rex_lh_free);
PPC_EXTERN_FUNC(__imp__rex_lhVFS_RequestManager_FindStream);
PPC_EXTERN_FUNC(__imp__rex_lhVFS_RequestManager_BuildHashTable);
PPC_EXTERN_FUNC(__imp__rex_lhVFS_Provider_FindStreamByHash);
PPC_EXTERN_FUNC(__imp__rex_lhVFS_Provider_OpenStream);
PPC_EXTERN_FUNC(__imp__rex_lhBank_OpenStreamByIndex);
PPC_EXTERN_FUNC(__imp__rex_lhBank_OpenStreamByName);
PPC_EXTERN_FUNC(__imp__rex_lhStream_Buffered_GetSize);
PPC_EXTERN_FUNC(__imp__rex_lhStream_Buffered_Init);
PPC_EXTERN_FUNC(__imp__rex_lhStream_Buffered_Read);
PPC_EXTERN_FUNC(__imp__rex_lhStream_CreateBuffered);
PPC_EXTERN_FUNC(__imp__rex_lhStream_CreateFromPosition);
PPC_EXTERN_FUNC(__imp__rex_lhStream_CreateReader);
PPC_EXTERN_FUNC(__imp__rex_lhStream_CreateReadStream);
PPC_EXTERN_FUNC(__imp__rex_lhStream_CreateSubStream);
PPC_EXTERN_FUNC(__imp__rex_lhStream_DiskStream_Dtor);
PPC_EXTERN_FUNC(__imp__rex_lhStream_DiskStream_GetSize);
PPC_EXTERN_FUNC(__imp__rex_lhStream_DiskStream_Read);
PPC_EXTERN_FUNC(__imp__rex_lhStream_DecompressStream_Read);
PPC_EXTERN_FUNC(__imp__rex_lhStream_GetPosition);
PPC_EXTERN_FUNC(__imp__rex_lhStream_GetInnerSize);
PPC_EXTERN_FUNC(__imp__rex_lhStream_ReadUInt32);
PPC_EXTERN_FUNC(__imp__rex_lhStream_Reader_Ctor);
PPC_EXTERN_FUNC(__imp__rex_lhStream_Reader_Read);
PPC_EXTERN_FUNC(__imp__rex_lhRefCounted_AddRef);
PPC_EXTERN_FUNC(__imp__rex_lhRefCounted_Release);
PPC_EXTERN_FUNC(__imp__rex_lhRefCounted_ReturnMinusOne);
PPC_EXTERN_FUNC(__imp__rex_lhResourceManager_FindAndCreateStream);
PPC_EXTERN_FUNC(__imp__rex_lhResourceManager_GetCached);
PPC_EXTERN_FUNC(__imp__rex_lhResourceManager_Open);
PPC_EXTERN_FUNC(__imp__rex_lhResourceManager_OpenInternal);
PPC_EXTERN_FUNC(__imp__rex_lhResourceManager_vtbl_FindStream);
PPC_EXTERN_FUNC(__imp__rex_lhResourceManager_vtbl_GetStreamSize);
PPC_EXTERN_FUNC(__imp__rex_lhResourceName_ComputeHash);
PPC_EXTERN_FUNC(__imp__rex_lhResourceName_ComputeHashFromName);
PPC_EXTERN_FUNC(__imp__rex_lhResource_InitModelHeaders);
PPC_EXTERN_FUNC(__imp__rex_lhResource_CreateModelCache);
PPC_EXTERN_FUNC(__imp__rex_lhResource_SetupModelCache);
PPC_EXTERN_FUNC(__imp__rex_lhResource_SetLoadMode);
PPC_EXTERN_FUNC(__imp__rex_lhResource_InitSystem);
PPC_EXTERN_FUNC(__imp__rex_lhResource_InitLoader);
PPC_EXTERN_FUNC(__imp__rex_lhMeshResource_Load);
PPC_EXTERN_FUNC(__imp__rex_lhMeshResource_Deserialize);
PPC_EXTERN_FUNC(__imp__rex_lhMeshResource_Validate);
PPC_EXTERN_FUNC(__imp__rex_lhScopedLock_Acquire);
PPC_EXTERN_FUNC(__imp__RtlEnterCriticalSection);
PPC_EXTERN_FUNC(__imp__RtlLeaveCriticalSection);
PPC_EXTERN_FUNC(__imp__rex_lhRenderer_Construct);
PPC_EXTERN_FUNC(__imp__rex_lhRenderer_Setup);
PPC_EXTERN_FUNC(__imp__rex_lhRenderer_BeginScene);
PPC_EXTERN_FUNC(__imp__rex_lhRenderer_StartThread);
PPC_EXTERN_FUNC(__imp__rex_lhGame_UpdateThread);
PPC_EXTERN_FUNC(__imp__rex_lhGameData_Init);
PPC_EXTERN_FUNC(__imp__rex_lhGameInit_SetupUI);
PPC_EXTERN_FUNC(__imp__rex_lhGameInit_SetupAudio);
PPC_EXTERN_FUNC(__imp__rex_lhGameInit_CheckUpdateMode);
PPC_EXTERN_FUNC(__imp__rex_lhGameData_RunDlcLuaStartupScript);
PPC_EXTERN_FUNC(__imp__rex_lhLua_RunScriptBuffer);
PPC_EXTERN_FUNC(__imp__rex_lhVFSConfig_FindFileSystemFailingPredicate);
PPC_EXTERN_FUNC(__imp__rex_CRemappingFileSystem_OpenFirstRemappedManifest);
PPC_EXTERN_FUNC(__imp__rex_lhItemManager_NotifyAndRemoveItem);
PPC_EXTERN_FUNC(__imp__rex_CGameface_Destructor);
PPC_EXTERN_FUNC(__imp__rex_CPresentation_SetDrawParked);
PPC_EXTERN_FUNC(__imp__rex_lhFolded_StoreWordAt4);
PPC_EXTERN_FUNC(__imp__rex_lhGuiAbilitiesScreen_Enter);
PPC_EXTERN_FUNC(__imp__rex_CPresentation_SetUpdateParked);
PPC_EXTERN_FUNC(__imp__rex_lhGuiScreenSet_EnsureLoaded);
PPC_EXTERN_FUNC(__imp__rex_lhGuiScreenSet_ShowRoot);
PPC_EXTERN_FUNC(__imp__rex_lhGuiScreenSet_Unload);
PPC_EXTERN_FUNC(__imp__rex_lhHudRegistries_ResetBindings);
PPC_EXTERN_FUNC(__imp__rex_lhHudRegistries_Destroy);
PPC_EXTERN_FUNC(__imp__rex_lhHudRegistries_Build);
PPC_EXTERN_FUNC(__imp__rex_lhHudTargeted_Publish);
PPC_EXTERN_FUNC(__imp__rex_lhHudTopBox_Compose);
PPC_EXTERN_FUNC(__imp__rex_lhHudTopBox_Front);
PPC_EXTERN_FUNC(__imp__rex_lhHudTopBox_FrontChannel);
PPC_EXTERN_FUNC(__imp__rex_lhHudTopBox_SetHidden);
PPC_EXTERN_FUNC(__imp__rex_lhGuiRegistry_SetTextGroup);
PPC_EXTERN_FUNC(__imp__rex_lhGuiRegistry_FreezeMeter);
PPC_EXTERN_FUNC(__imp__rex_lhGuiRegistry_StartPulser);
PPC_EXTERN_FUNC(__imp__rex_lhGuiRegistry_StopPulser);
PPC_EXTERN_FUNC(__imp__rex_lhGuiTabStrip_Show);
PPC_EXTERN_FUNC(__imp__rex_lhGuiAbilitiesScreen_EnterAssign);
PPC_EXTERN_FUNC(__imp__rex_lhGuiAbilitiesScreen_ExitAssign);
PPC_EXTERN_FUNC(__imp__rex_GameFlow_QueueCommand);
PPC_EXTERN_FUNC(__imp__rex_GameFlow_PopCommand);
PPC_EXTERN_FUNC(__imp__rex_lhGuiWillAssignWidget_Open);
PPC_EXTERN_FUNC(__imp__rex_lhGuiWillAssignWidget_Show);
PPC_EXTERN_FUNC(__imp__rex_lhGuiWillAssignWidget_Close);
PPC_EXTERN_FUNC(__imp__rex_lhGuiWillAssignWidget_OnScreenAction);
PPC_EXTERN_FUNC(__imp__rex_lhProfile_GetGamma);
PPC_EXTERN_FUNC(__imp__rex_lhProfile_SetGamma);
PPC_EXTERN_FUNC(__imp__rex_lhAudio_SetSfxVolume);
PPC_EXTERN_FUNC(__imp__rex_lhAudio_SetMusicVolume);
PPC_EXTERN_FUNC(__imp__rex_lhAudio_SetSpeechVolume);
PPC_EXTERN_FUNC(__imp__rex_lhAudio_SetSpeakerMode);
PPC_EXTERN_FUNC(__imp__rex_lhLua_SetRumbleEnabled);
PPC_EXTERN_FUNC(__imp__rex_lhScreenFade_Start);
PPC_EXTERN_FUNC(__imp__rex_lhScreenFade_Advance);
PPC_EXTERN_FUNC(__imp__rex_lhHudExpressionMeter_Tick);
PPC_EXTERN_FUNC(__imp__rex_lhHudExpressionMeter_ArmPip);
PPC_EXTERN_FUNC(__imp__rex_lhHudExpressionMeter_Judge);
PPC_EXTERN_FUNC(__imp__rex_lhHudExpressionMeter_Start);
PPC_EXTERN_FUNC(__imp__rex_lhHudExpressionMeter_TurnOff);
PPC_EXTERN_FUNC(__imp__rex_lhLoadingScreenManager_ShowText);
PPC_EXTERN_FUNC(__imp__rex_lhLoadingScreenManager_SetText);
PPC_EXTERN_FUNC(__imp__rex_lhLoadingScreen_Ctor);
PPC_EXTERN_FUNC(__imp__rex_lhLoadingScreen_CtorText);
PPC_EXTERN_FUNC(__imp__rex_lhLoadingScreen_ChooseArt);
PPC_EXTERN_FUNC(__imp__rex_lhLoadingScreen_Dtor);
PPC_EXTERN_FUNC(__imp__rex_lhMenuBox_SetSelection);
PPC_EXTERN_FUNC(__imp__rex_lhGuiCoopScreen_OnEnter);
PPC_EXTERN_FUNC(__imp__rex_lhGuiCoopScreen_SetAccepted);
PPC_EXTERN_FUNC(__imp__rex_lhGuiCoopScreen_Finish);
PPC_EXTERN_FUNC(__imp__rex_lhGuiCoopScreen_SetPickerShown);
PPC_EXTERN_FUNC(__imp__rex_lhGuiCoopScreen_SetPickerCursor);
PPC_EXTERN_FUNC(__imp__rex_lhGuiManager_ClearModalState);
PPC_EXTERN_FUNC(__imp__rex_lhGuiWorldMapScreen_AskStopTravel);
PPC_EXTERN_FUNC(__imp__rex_lhRenderer_FrameBody);
PPC_EXTERN_FUNC(__imp__rex_lhRenderer_ReleasePending);
PPC_EXTERN_FUNC(__imp__rex_lhFrontEndGame_VtableSlot6);
PPC_EXTERN_FUNC(__imp__rex_lhVFSConfig_LoadConfigByName);
PPC_EXTERN_FUNC(__imp__rex_lhAnimationBank_OpenGlobal);
PPC_EXTERN_FUNC(__imp__rex_lhGameData_LoadAudioDatabaseByName);
PPC_EXTERN_FUNC(__imp__rex_lhGameData_LoadNamedBankEntry);
PPC_EXTERN_FUNC(rex_lhGameData_LoadNamedBankEntry);
PPC_EXTERN_FUNC(__imp__rex_lhGameData_RegisterResourceFilesByName);
PPC_EXTERN_FUNC(__imp__rex_lhRenderer_BeginGPUFrame);
PPC_EXTERN_FUNC(__imp__rex_lhRenderer_EndGPUFrame);
PPC_EXTERN_FUNC(__imp__rex_lhBank_Open);
PPC_EXTERN_FUNC(__imp__rex_lhBank_OpenWithManifest);
PPC_EXTERN_FUNC(__imp__rex_lhBank_ParseHeader);
PPC_EXTERN_FUNC(__imp__rex_lhBank_DirectoryIterator_Ctor);
PPC_EXTERN_FUNC(__imp__rex_lhBank_DirIter_FindStreamNext);
PPC_EXTERN_FUNC(__imp__rex_lhBank_DirIter_GetName);
PPC_EXTERN_FUNC(__imp__rex_lhBank_DirIter_FindStreamByIndex);
PPC_EXTERN_FUNC(__imp__rex_CreateFileA);
PPC_EXTERN_FUNC(__imp__rex_GetFileAttributesA);
PPC_EXTERN_FUNC(__imp__rex_FindNextFileA);
PPC_EXTERN_FUNC(__imp__rex_FindFirstFileA);
PPC_EXTERN_FUNC(__imp__rex_ReadFile);
PPC_EXTERN_FUNC(__imp__rex_ReadFileAsync);
PPC_EXTERN_FUNC(__imp__rex_WriteFile);
PPC_EXTERN_FUNC(__imp__rex_GetFileSize);
PPC_EXTERN_FUNC(__imp__rex_GetFileSizeEx);
PPC_EXTERN_FUNC(__imp__rex_GetFileInformation);
PPC_EXTERN_FUNC(__imp__rex_GetFileSectorInfo);
PPC_EXTERN_FUNC(__imp__rex_GetFileTime);
PPC_EXTERN_FUNC(__imp__rex_SetFilePointer);
PPC_EXTERN_FUNC(__imp__rex_SetEndOfFile);
PPC_EXTERN_FUNC(__imp__rex_FlushFileBuffers);
PPC_EXTERN_FUNC(__imp__rex_DeleteFileA);
PPC_EXTERN_FUNC(__imp__rex_CloseHandle);
PPC_EXTERN_FUNC(__imp__rex_lh_OpenDiscFile);
PPC_EXTERN_FUNC(__imp__rex_lhFile_CloseAndRelease);
PPC_EXTERN_FUNC(__imp__rex_lhFile_Close);
PPC_EXTERN_FUNC(__imp__rex_lhFile_Read);
PPC_EXTERN_FUNC(__imp__rex_lhFile_Write);
PPC_EXTERN_FUNC(__imp__rex_lhFile_SeekFromStart);
PPC_EXTERN_FUNC(__imp__rex_lhFile_SeekFromEnd);
PPC_EXTERN_FUNC(__imp__rex_lhFile_GetSize);
PPC_EXTERN_FUNC(__imp__rex_lhFile_GetTotalSize);
PPC_EXTERN_FUNC(__imp__rex_lhFile_IsOpen);
PPC_EXTERN_FUNC(__imp__rex_lhFile_Dtor);
PPC_EXTERN_FUNC(__imp__rex_lhFile_Ctor);
PPC_EXTERN_FUNC(__imp__rex_lhFile_ResolveAndOpen);
PPC_EXTERN_FUNC(__imp__rex_lhStream_DiskStream_ReadDirect);
PPC_EXTERN_FUNC(__imp__rex_lhStream_DiskStream_GetInnerSize);
PPC_EXTERN_FUNC(__imp__rex_lhXmlDocument_ResetAndParseStream);
PPC_EXTERN_FUNC(rex_lhVFS_GetGlobalDataProvider);

// The guest's XAudio API shims — the audio seam Godot plays through.
// Hooked in Fable2/src/vfs/src/hooks/xaudio_voices.cpp. Named in the ReXGlue config like every other hook,
// so the same source binds in each supported build of the game. A config name only takes
// effect after a full codegen: until then the hook has nothing to bind to.
PPC_EXTERN_FUNC(__imp__rex_XAudioDestroyVoice);  // XAudioDestroyVoice
PPC_EXTERN_FUNC(__imp__rex_XAudioSetChannelVolumes);  // XAudioSetChannelVolumes
PPC_EXTERN_FUNC(__imp__rex_XAudioSubmitSourceBuffer);  // XAudioSubmitSourceBuffer
PPC_EXTERN_FUNC(__imp__rex_XAudioStartVoice);  // XAudioStartVoice
PPC_EXTERN_FUNC(__imp__rex_XAudioStopVoice);  // XAudioStopVoice
PPC_EXTERN_FUNC(__imp__rex_XAudioSetVolume);  // XAudioSetVolume
PPC_EXTERN_FUNC(__imp__rex_XAudioSetPitch);  // XAudioSetPitch
PPC_EXTERN_FUNC(__imp__rex_XAudioCreateSourceVoice);  // XAudioCreateSourceVoice
PPC_EXTERN_FUNC(__imp__rex_lhRangedAim_MoveCursor);  // the ranged aim cursor: mouse turn joins its stick (mouse_look.cpp)
