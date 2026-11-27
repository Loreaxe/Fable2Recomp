// The drain put-back rule (src/runtime/include/core/queue_put_back.h): what a drain could not hand over
// goes back to the FRONT of the queue in its own order, ahead of anything queued since.
// Build: c++ -std=c++20 -I src/runtime/include tools/tests/queue_put_back_test.cpp && ./a.out

#include <cassert>
#include <cstdio>
#include <string>
#include <vector>

#include "core/queue_put_back.h"

int main() {
    using refii::vfs::PutBackFront;

    // Five taken, two handed over, two more queued meanwhile: the three unwritten lead, in order.
    std::vector<std::string> queue = {"f", "g"};
    std::vector<std::string> taken = {"a", "b", "c", "d", "e"};
    PutBackFront(queue, taken, 2);
    assert((queue == std::vector<std::string>{"c", "d", "e", "f", "g"}));
    assert((taken == std::vector<std::string>{"a", "b"}));

    // Everything handed over: nothing moves.
    std::vector<std::string> all = {"a", "b"};
    std::vector<std::string> later = {"x"};
    PutBackFront(later, all, 2);
    assert((later == std::vector<std::string>{"x"}));
    assert(all.size() == 2u);

    // Nothing handed over, empty queue: the whole take returns as it was.
    std::vector<std::string> none = {"a", "b", "c"};
    std::vector<std::string> empty;
    PutBackFront(empty, none, 0);
    assert((empty == std::vector<std::string>{"a", "b", "c"}));
    assert(none.empty());

    // A second partial drain keeps the order across drains.
    std::vector<std::string> again = empty;
    std::vector<std::string> rest;
    rest.swap(again);
    PutBackFront(again, rest, 1);
    assert((again == std::vector<std::string>{"b", "c"}));

    std::puts("queue_put_back_test: ok");
    return 0;
}
