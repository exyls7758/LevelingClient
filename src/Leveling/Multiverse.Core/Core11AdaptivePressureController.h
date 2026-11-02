#pragma once

#include <algorithm>
#include <cstdint>

namespace multiverse
{
    enum class Core11AdaptivePressureState : std::uint32_t
    {
        Normal = 0,
        Relief = 1,
        Recovering = 2
    };

    struct Core11AdaptivePressureSample
    {
        bool EncoderRunning = false;
        double LastOutputLatencyMs = 0.0;
        std::uint32_t PendingOutputs = 0;
        std::uint32_t InputSlots = 0;
        double OldestInFlightMs = 0.0;
        std::uint32_t RingOccupied = 0;
        std::uint32_t RingSlots = 0;
    };

    struct Core11AdaptivePressureSnapshot
    {
        Core11AdaptivePressureState State = Core11AdaptivePressureState::Normal;
        std::uint32_t TargetFps = 0;
        std::uint32_t PressureStreak = 0;
        std::uint32_t HealthyStreak = 0;
        std::uint32_t Transitions = 0;
    };

    // CORE 11.17e: pressure classifier for sustained NVENC/GPU pressure.
    // State transitions remain visible for diagnostics, but they never reduce
    // the user-requested publish/submit cadence. TargetFps always mirrors the
    // requested stream FPS.
    class Core11AdaptivePressureController final
    {
    public:
        static constexpr std::int64_t SampleIntervalMs = 250;
        static constexpr std::uint32_t PressureSamplesToRelief = 3;
        static constexpr std::uint32_t HealthySamplesToRecovering = 6;
        static constexpr std::uint32_t HealthySamplesToNormal = 4;

        void Reset(std::uint32_t requestedFps) noexcept
        {
            m_requestedFps = requestedFps;
            m_snapshot = {};
            m_snapshot.State = Core11AdaptivePressureState::Normal;
            m_snapshot.TargetFps = requestedFps;
            m_lastSampleMs = -1;
            m_stateEnteredAtMs = 0;
        }

        void Observe(
            const Core11AdaptivePressureSample& sample,
            std::int64_t nowMs) noexcept
        {
            if (m_requestedFps == 0)
                return;

            if (m_lastSampleMs >= 0 &&
                nowMs >= m_lastSampleMs &&
                nowMs - m_lastSampleMs < SampleIntervalMs)
            {
                return;
            }
            m_lastSampleMs = nowMs;

            // A requested 30-Hz stream remains fixed at 30. Pressure observation
            // is diagnostic-only for every requested cadence.
            if (m_requestedFps <= 30)
            {
                m_snapshot.State = Core11AdaptivePressureState::Normal;
                m_snapshot.TargetFps = m_requestedFps;
                m_snapshot.PressureStreak = 0;
                m_snapshot.HealthyStreak = 0;
                return;
            }

            const std::uint32_t pendingNearFull =
                sample.InputSlots > 1 ? sample.InputSlots - 1 : 1;
            const std::uint32_t ringNearFull =
                sample.RingSlots > 1 ? sample.RingSlots - 1 : 1;

            const bool highLatency =
                sample.LastOutputLatencyMs >= 20.0 ||
                sample.OldestInFlightMs >= 35.0;
            const bool queuePressure =
                sample.PendingOutputs >= pendingNearFull ||
                sample.RingOccupied >= ringNearFull ||
                sample.OldestInFlightMs >= 45.0;
            const bool pressure =
                sample.EncoderRunning && highLatency && queuePressure;

            const std::uint32_t healthyRingCeiling =
                sample.RingSlots > 0
                    ? std::max<std::uint32_t>(1u, sample.RingSlots / 2u)
                    : 1u;
            const bool healthy =
                sample.EncoderRunning &&
                sample.LastOutputLatencyMs <= 15.0 &&
                sample.PendingOutputs <= 1 &&
                sample.OldestInFlightMs < 25.0 &&
                sample.RingOccupied <= healthyRingCeiling;

            switch (m_snapshot.State)
            {
            case Core11AdaptivePressureState::Normal:
                m_snapshot.HealthyStreak = 0;
                m_snapshot.PressureStreak = pressure
                    ? std::min<std::uint32_t>(m_snapshot.PressureStreak + 1u, 100u)
                    : 0u;
                if (m_snapshot.PressureStreak >= PressureSamplesToRelief)
                {
                    TransitionTo(Core11AdaptivePressureState::Relief, nowMs);
                    m_snapshot.TargetFps = m_requestedFps;
                    m_snapshot.PressureStreak = 0;
                }
                break;

            case Core11AdaptivePressureState::Relief:
                m_snapshot.TargetFps = m_requestedFps;
                if (pressure)
                {
                    m_snapshot.HealthyStreak = 0;
                    m_snapshot.PressureStreak =
                        std::min<std::uint32_t>(m_snapshot.PressureStreak + 1u, 100u);
                    break;
                }

                m_snapshot.PressureStreak = 0;
                m_snapshot.HealthyStreak = healthy
                    ? std::min<std::uint32_t>(m_snapshot.HealthyStreak + 1u, 100u)
                    : 0u;
                if (m_snapshot.HealthyStreak >= HealthySamplesToRecovering)
                {
                    TransitionTo(Core11AdaptivePressureState::Recovering, nowMs);
                    m_snapshot.HealthyStreak = 0;
                }
                break;

            case Core11AdaptivePressureState::Recovering:
                m_snapshot.TargetFps = m_requestedFps;
                if (pressure)
                {
                    TransitionTo(Core11AdaptivePressureState::Relief, nowMs);
                    m_snapshot.PressureStreak = 1;
                    m_snapshot.HealthyStreak = 0;
                    break;
                }

                m_snapshot.PressureStreak = 0;
                m_snapshot.HealthyStreak = healthy
                    ? std::min<std::uint32_t>(m_snapshot.HealthyStreak + 1u, 100u)
                    : 0u;
                if (m_snapshot.HealthyStreak >= HealthySamplesToNormal)
                {
                    TransitionTo(Core11AdaptivePressureState::Normal, nowMs);
                    m_snapshot.TargetFps = m_requestedFps;
                    m_snapshot.HealthyStreak = 0;
                }
                break;
            }
        }

        Core11AdaptivePressureSnapshot Snapshot() const noexcept
        {
            return m_snapshot;
        }

    private:
        void TransitionTo(
            Core11AdaptivePressureState state,
            std::int64_t nowMs) noexcept
        {
            if (m_snapshot.State == state)
                return;
            m_snapshot.State = state;
            m_snapshot.Transitions =
                std::min<std::uint32_t>(m_snapshot.Transitions + 1u, 0xFFFFFFFFu);
            m_stateEnteredAtMs = nowMs;
        }

        std::uint32_t m_requestedFps = 0;
        std::int64_t m_lastSampleMs = -1;
        std::int64_t m_stateEnteredAtMs = 0;
        Core11AdaptivePressureSnapshot m_snapshot{};
    };
}
