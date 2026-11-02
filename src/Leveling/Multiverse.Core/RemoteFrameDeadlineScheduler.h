#pragma once

#include <chrono>

namespace multiverse
{
    // CORE 11.9: pacing deadline that advances only after a frame was
    // successfully submitted to NVENC. A ring miss therefore leaves the
    // current deadline due, so a frame that arrives a few milliseconds late
    // can be consumed immediately instead of being deferred by a full 60 Hz
    // period.
    class RemoteFrameDeadlineScheduler final
    {
    public:
        using Clock = std::chrono::steady_clock;
        using TimePoint = Clock::time_point;
        using Duration = Clock::duration;

        TimePoint GetOrInitializeDeadline(TimePoint now) noexcept
        {
            if (!m_initialized)
            {
                m_initialized = true;
                m_nextDeadline = now;
            }
            return m_nextDeadline;
        }

        void CommitSuccessfulFrame(
            TimePoint now,
            Duration frameDuration) noexcept
        {
            if (!m_initialized)
            {
                m_initialized = true;
                m_nextDeadline = now;
            }

            // Preserve the established 60 Hz phase for ordinary jitter. If
            // forward progress fell more than one complete frame behind,
            // resynchronize instead of producing a catch-up burst.
            if (now > m_nextDeadline + frameDuration)
                m_nextDeadline = now + frameDuration;
            else
                m_nextDeadline += frameDuration;
        }

        void Reset() noexcept
        {
            m_initialized = false;
            m_nextDeadline = {};
        }

    private:
        bool m_initialized = false;
        TimePoint m_nextDeadline{};
    };
}
