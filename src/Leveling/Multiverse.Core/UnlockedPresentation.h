#pragma once
#include <mutex>
namespace multiverse {
template<class F> auto InvokePresentation(std::unique_lock<std::mutex>& lock, F&& present) -> decltype(present()) {
    // Resource mutation remains serialized; a compositor wait must not hold
    // the lock needed to copy the next decoded picture into another slot.
    struct Relock { std::unique_lock<std::mutex>& lock; ~Relock() { lock.lock(); } };
    lock.unlock();
    Relock restore{lock};
    return present();
}
}
