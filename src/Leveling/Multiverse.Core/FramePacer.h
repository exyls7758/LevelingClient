#pragma once

#include <algorithm>
#include <cstdint>

namespace multiverse
{
    class FramePacer final
    {
    public:
        bool ShouldRun(
            std::int64_t nowUs,
            std::uint32_t fps) noexcept
        {
            if (fps == 0)
                return false;

            const std::int64_t intervalUs =
                std::max<std::int64_t>(
                    1,
                    1'000'000 /
                        static_cast<std::int64_t>(fps));

            // WGC callbacks can arrive in short scheduler/compositor bursts even
            // when the source itself averages the requested FPS. A rigid
            // next-deadline gate turns that harmless jitter into real frame loss.
            //
            // Keep a small amount of pacing credit instead: enough to absorb up
            // to two early callbacks (3 frames total including the current one),
            // while the refill rate still enforces the configured average FPS.
            // This preserves the 120->60 limiter without collapsing a jittery
            // nominal-60 source toward 20-30 FPS.
            const std::int64_t maxCreditUs = intervalUs * 3;

            if (!m_initialized ||
                m_intervalUs != intervalUs ||
                nowUs < m_lastSeenUs)
            {
                m_initialized = true;
                m_intervalUs = intervalUs;
                m_lastSeenUs = nowUs;
                // Start with one frame of residual credit after accepting the
                // first frame, equivalent to a small startup burst allowance.
                m_creditUs = intervalUs * 2;
                return true;
            }

            const std::int64_t elapsedUs =
                std::max<std::int64_t>(0, nowUs - m_lastSeenUs);
            m_lastSeenUs = nowUs;
            m_creditUs =
                std::min<std::int64_t>(
                    maxCreditUs,
                    m_creditUs + elapsedUs);

            if (m_creditUs < intervalUs)
                return false;

            m_creditUs -= intervalUs;
            return true;
        }

        void Reset() noexcept
        {
            m_initialized = false;
            m_intervalUs = 0;
            m_lastSeenUs = 0;
            m_creditUs = 0;
        }

    private:
        bool m_initialized = false;
        std::int64_t m_intervalUs = 0;
        std::int64_t m_lastSeenUs = 0;
        std::int64_t m_creditUs = 0;
    };
}
