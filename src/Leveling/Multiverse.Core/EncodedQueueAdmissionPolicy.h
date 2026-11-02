#pragma once
#include <cstdint>

namespace multiverse
{
    struct EncodedQueueState
    {
        bool Active = false;
        bool WaitingForKeyFrame = true;
        std::uint32_t Packets = 0;
        std::uint32_t Bytes = 0;
    };

    // Admission occurs BEFORE encoding. It never removes coded references.
    // The existing eight-frame/eight-MiB queue remains the hard safety bound.
    class EncodedQueueAdmissionPolicy final
    {
    public:
        static constexpr std::uint32_t SoftPackets = 3;
        static constexpr std::uint32_t SoftBytes = 4u * 1024u * 1024u;

        bool ShouldDefer(const EncodedQueueState& queue,
            std::uint32_t inFlight, std::uint32_t inputSlots) const noexcept
        {
            if (!queue.Active)
                return false;
            // Drain all previously accepted outputs before submitting the
            // next recovery IDR. This prevents a burst of obsolete P-frames
            // from filling the queue while the IDR is still in flight.
            if (queue.WaitingForKeyFrame)
                return queue.Packets != 0 || inFlight != 0;
            const std::uint32_t reserve = inputSlots > 0 ? inputSlots : 1;
            return queue.Packets >= SoftPackets ||
                queue.Bytes >= SoftBytes ||
                static_cast<std::uint64_t>(queue.Packets) + inFlight >=
                    static_cast<std::uint64_t>(SoftPackets) + reserve - 1;
        }
    };
}
