// What the guest TELLS a presentation's elements, posted to the host as statements. Every element
// is a handle of the shared headless core (core/headless_presentation.h, THE STATED ELEMENTS); the
// element service and animation slots that state them are hooks/gui_element_hooks.cpp, the text
// and texture writers' entries hooks/gui_headless_presentation.cpp. They push through the C ABI
// (`refii_bridge_told_push`, core/payload_bridge.h) into the shared queues
// (src/runtime/src/core/told_statements.cpp), which the bridge drains with the Take* functions
// core/told_statements.h declares.
#pragma once

#include <cstdint>

#include "core/told_statements.h"

namespace refii::vfs {

/// PAYLOAD side: a headless presentation's element key (the shared headless core's), or false
/// when `element` is not one.
bool HeadlessElementKey(uint32_t element, uint32_t* presentation, uint32_t* serial, uint32_t* id);

}  // namespace refii::vfs
