// refii - Fable 3's answers to the shell's title feeds: nothing stated, yet.
//
// THE SHELL BINDS ONE ABI. Every export Godot's shared HostBridge declares is answered by every
// title's bridge: the core's (src/hostcore) for what is the same for each title, the title's own
// for what reads its guest. Fable 2 reads each of these out of its binary. Fable 3's are not
// found yet, so each answers as a guest that states nothing does: no rows, no entity, no screen,
// a write that takes no effect. A feed moves out of this file into host_bridge.cpp when the call
// that states it is found in Fable 3's binary.
//
// Signatures are Fable 2's, export for export (Fable2/src/hostbridge/src/host_bridge.cpp), so a
// managed declaration binds either title. TitleBoundaryTests checks the set.

#include <algorithm>
#include <cstdint>

#include "host_abi.h"
#include "../../vfs/include/fable3_guest_build.h"


namespace {

template <typename T>
void Zero(T* out) {
    if (out != nullptr) {
        *out = 0;
    }
}

}  // namespace

extern "C" {

REFII_HOST_ABI_EXPORT uint32_t RefiiHostDescribeGameFlowQueue(char* /*out*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityLocomotion(uint32_t /*entity*/, uint32_t* /*out_words*/, uint32_t /*word_count*/, char* /*out_name*/, uint32_t /*name_capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetFrontEndState(void) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetGameLoopPacer(uint32_t* /*words*/, uint32_t /*word_capacity*/, double* /*values*/, uint32_t /*value_capacity*/) {
    return 0;
}

// The shared NetBridge's two title reads (hosting and joinable live on Fable 2's net context).
REFII_HOST_ABI_EXPORT uint32_t RefiiNetHostState(char* /*out*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT int RefiiNetSetJoinable(int /*on*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT void RefiiHostGetInitMarkers(uint32_t* out) {
    if (out != nullptr) {
        std::fill_n(out, 4, 0u);
    }
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetMapMarkers(uint32_t* /*out_hashes*/, float* /*out_values*/, uint32_t /*cap*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetMenuRow(uint32_t /*index*/, char* /*out_name*/, uint32_t /*name_capacity*/, char* /*out_parent*/, uint32_t /*parent_capacity*/, uint32_t* /*out_id*/, int32_t* /*out_event_type*/, uint64_t* /*out_generation*/, int32_t* /*out_rating*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetMenuRowLabel(uint32_t /*index*/, char* /*out_label*/, uint32_t /*label_capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetMenuRowItem(uint32_t /*index*/, char* /*out_icon*/, uint32_t /*icon_capacity*/, int32_t* /*out_emphasis*/, uint32_t* /*out_words*/, uint32_t /*word_capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetMenuScreenId(void) {
    return 0;
}

REFII_HOST_ABI_EXPORT int RefiiHostGetPlayerTwoHoldPrompt(float* /*out_at*/, char* /*title*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetPromptTitles(char* /*out*/, uint32_t /*capacity*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT int RefiiHostGetRenderFence(uint32_t* /*out_manager*/, double* /*out_submitted*/, double* /*out_retired*/, uint32_t* /*out_busy*/, uint32_t* /*out_arms*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetSaveState(uint32_t* /*out_signed_in*/, int32_t* /*out_count*/, uint32_t* /*out_limit*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT void RefiiHostGetSceneStreams(uint32_t* out) {
    if (out != nullptr) {
        std::fill_n(out, 24, 0u);
    }
}

REFII_HOST_ABI_EXPORT int RefiiHostGetSpellAssign(uint32_t* /*out*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT int RefiiHostGetStartLevel(char* /*world*/, char* /*level*/, char* /*entrance*/, uint32_t /*capacity*/) {
    return 0;
}

/// -1: not stated (Fable 2's 1 on, 0 off).
REFII_HOST_ABI_EXPORT int RefiiHostGetSubtitles(void) {
    return -1;
}

REFII_HOST_ABI_EXPORT int RefiiHostGetVideoFrame(uint8_t* /*out_rgba*/, uint32_t /*capacity*/, uint32_t* /*out_width*/, uint32_t* /*out_height*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT int RefiiHostGetVideoPlanes(uint8_t* /*out_y*/, uint32_t /*y_capacity*/, uint8_t* /*out_cr*/, uint32_t /*cr_capacity*/, uint8_t* /*out_cb*/, uint32_t /*cb_capacity*/, uint32_t* /*out_desc*/) {
    return 0;
}

// The running build and its translation (fable3_guest_build.h): the shell ABI's "TU1" is the build
// a title's source names, which for Fable 3 is TU3; 1 is the disc.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGuestAddress(uint32_t tu3_address) {
    return refiii_guest_address(tu3_address);
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGuestAddressToTu1(uint32_t live_address) {
    return refiii_guest_address_to_tu3(live_address);
}

REFII_HOST_ABI_EXPORT int32_t RefiiHostGuestBuild(void) {
    return refiii_guest_build();
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostInstallWeaponAugmentHook(void) {
    return 0;
}

REFII_HOST_ABI_EXPORT void RefiiHostMenuChunkCounters(uint64_t* out_recorded, uint32_t* out_pending, uint64_t* out_dropped) {
    Zero(out_recorded);
    Zero(out_pending);
    Zero(out_dropped);
}

REFII_HOST_ABI_EXPORT int RefiiHostMenuPopulating(void) {
    return 0;
}

REFII_HOST_ABI_EXPORT void RefiiHostRefreshSaveState(void) {}

REFII_HOST_ABI_EXPORT void RefiiHostSetHighlightedRow(uint32_t /*screen*/, int32_t /*event_type*/, int32_t /*id*/, const char* /*name*/, const char* /*label*/) {}

REFII_HOST_ABI_EXPORT void RefiiHostSetListFoldDepth(uint32_t /*screen*/, uint32_t /*depth*/) {}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostTakeMenuChunks(char* /*out_utf8*/, uint32_t /*utf8_capacity*/, uint8_t* /*out_kinds*/, uint32_t /*kinds_capacity*/, uint32_t /*max_records*/) {
    return 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostUiDataGeneration(void) {
    return 0;
}

}  // extern "C"
