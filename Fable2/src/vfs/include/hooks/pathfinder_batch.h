// The world tick's wait for the async pathfinder's open batch (src/hooks/pathfinder_batch.cpp).
#pragma once

#include <cstdint>

namespace refii::vfs {

/// Game thread, at the entry of `rex_lhWorld_SimTick` (TU1 sub_82275F68 / disc sub_82275F00),
/// before the guest's body takes the world lock: returns once the batch launched by the previous
/// tick has signalled FinishedPF, or after the bound. Counts and logs every arrival that found
/// the batch still open.
void WaitForPathfinderBatch(uint8_t* base);

}  // namespace refii::vfs
