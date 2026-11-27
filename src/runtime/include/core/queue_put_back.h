#pragma once

#include <cstddef>
#include <iterator>
#include <utility>
#include <vector>

namespace refii::vfs {

/// Puts `taken[from..]` back at the FRONT of `queue`, in their own order, ahead of whatever was
/// queued since they were taken: a drain that could not hand all of them over loses none and
/// the next drain reads them first.
template <typename T>
void PutBackFront(std::vector<T>& queue, std::vector<T>& taken, std::size_t from) {
    if (from >= taken.size()) {
        return;
    }
    queue.insert(queue.begin(), std::make_move_iterator(taken.begin() + static_cast<std::ptrdiff_t>(from)),
                 std::make_move_iterator(taken.end()));
    taken.resize(from);
}

/// The size past which a drain's caller stops growing its buffers: a single record that still
/// does not fit there is dropped (named and sized in the log) so the queue is never wedged.
constexpr std::size_t kDrainTextCeiling = 16u * 1024u * 1024u;
constexpr std::size_t kDrainKindCeiling = 1024u * 1024u;

}  // namespace refii::vfs
