#pragma once

#include <Windows.h>
#include <chrono>
#include <cstdint>

#ifndef CREATE_WAITABLE_TIMER_HIGH_RESOLUTION
#define CREATE_WAITABLE_TIMER_HIGH_RESOLUTION 0x00000002
#endif

namespace multiverse
{
    class HighResolutionFrameTimer final
    {
    public:
        HighResolutionFrameTimer()
        {
            m_timer = CreateWaitableTimerExW(
                nullptr,
                nullptr,
                CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
                TIMER_ALL_ACCESS);

            if (!m_timer)
            {
                // Older Windows/driver combinations may reject the high-
                // resolution flag. Fall back to a normal one-shot waitable
                // timer without changing the system-wide timer resolution.
                m_timer = CreateWaitableTimerExW(
                    nullptr,
                    nullptr,
                    0,
                    TIMER_ALL_ACCESS);
            }
        }

        ~HighResolutionFrameTimer()
        {
            if (m_timer)
                CloseHandle(m_timer);
        }

        HighResolutionFrameTimer(const HighResolutionFrameTimer&) = delete;
        HighResolutionFrameTimer& operator=(const HighResolutionFrameTimer&) = delete;

        bool WaitUntil(std::chrono::steady_clock::time_point deadline) noexcept
        {
            const auto now = std::chrono::steady_clock::now();
            if (deadline <= now)
                return true;

            if (!m_timer)
                return false;

            const auto remaining = deadline - now;
            const auto nanos = std::chrono::duration_cast<std::chrono::nanoseconds>(remaining).count();
            // Relative waitable-timer due times are negative and use 100 ns
            // units. Round up so a sub-100 ns remainder never becomes zero.
            const auto ticks100ns =
                static_cast<LONGLONG>((nanos + 99) / 100);

            LARGE_INTEGER dueTime{};
            dueTime.QuadPart = -((ticks100ns > 0) ? ticks100ns : 1);

            if (!SetWaitableTimerEx(
                    m_timer,
                    &dueTime,
                    0,
                    nullptr,
                    nullptr,
                    nullptr,
                    0))
            {
                return false;
            }

            return WaitForSingleObject(m_timer, INFINITE) == WAIT_OBJECT_0;
        }

    private:
        HANDLE m_timer = nullptr;
    };
}
