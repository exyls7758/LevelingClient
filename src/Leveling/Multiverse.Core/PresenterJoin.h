#pragma once
#include <windows.h>
#include <thread>

namespace multiverse {
// A timeout leaves the joinable worker and all its resources owned by the caller.
inline bool JoinPresenter(std::thread& worker, DWORD timeoutMs)
{
    if (!worker.joinable()) return true;
    if (worker.get_id() == std::this_thread::get_id()) return false;
    if (WaitForSingleObject(worker.native_handle(), timeoutMs) != WAIT_OBJECT_0)
        return false;
    worker.join();
    return true;
}
}
