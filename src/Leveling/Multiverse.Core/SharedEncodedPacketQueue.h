#pragma once

#include <atomic>
#include <cstdint>
#include <cstring>
#include <deque>
#include <mutex>
#include <utility>
#include <vector>
#include "EncodedQueueAdmissionPolicy.h"

namespace multiverse
{
    struct EncodedPacket
    {
        std::vector<std::uint8_t> Data;
        std::uint64_t FrameId = 0;
        bool KeyFrame = false;
    };

    class SharedEncodedPacketQueue final
    {
    public:
        enum class PopResult
        {
            Empty,
            InsufficientBuffer,
            Success
        };

        void SetActive(bool active)
        {
            std::lock_guard<std::mutex> lock(m_mutex);
            const bool wasActive = m_active.exchange(active);

            if (!active)
            {
                m_queue.clear();
                m_queuedBytes = 0;
                m_waitingForKeyFrame = true;
                return;
            }

            if (!wasActive)
            {
                m_waitingForKeyFrame = true;
                m_idrRequested.store(true);
            }
        }

        bool IsActive() const noexcept
        {
            return m_active.load();
        }

        void Clear(bool requestIdr)
        {
            std::lock_guard<std::mutex> lock(m_mutex);
            m_queue.clear();
            m_queuedBytes = 0;
            if (requestIdr)
            {
                m_waitingForKeyFrame = true;
                m_idrRequested.store(true);
            }
        }

        bool Push(EncodedPacket&& packet)
        {
            if (packet.Data.empty())
                return false;

            constexpr std::size_t MaxPackets = 8;
            constexpr std::uint32_t MaxBytes = 8u * 1024u * 1024u;

            const bool oversized = packet.Data.size() > MaxBytes;
            const std::uint32_t packetBytes =
                static_cast<std::uint32_t>(packet.Data.size());

            bool reset = false;

            {
                std::lock_guard<std::mutex> lock(m_mutex);

                if (!m_active.load())
                    return false;

                if (oversized)
                {
                    m_queue.clear();
                    m_queuedBytes = 0;
                    m_waitingForKeyFrame = true;
                    m_resetCount.fetch_add(1);
                    m_idrRequested.store(true);
                    return true;
                }

                if (m_queue.size() >= MaxPackets ||
                    m_queuedBytes > MaxBytes - packetBytes)
                {
                    m_queue.clear();
                    m_queuedBytes = 0;
                    m_waitingForKeyFrame = true;
                    m_resetCount.fetch_add(1);
                    m_idrRequested.store(true);
                    reset = true;
                }

                if (m_waitingForKeyFrame)
                {
                    if (!packet.KeyFrame)
                        return reset;
                    m_waitingForKeyFrame = false;
                }

                m_queuedBytes += packetBytes;
                m_queue.push_back(std::move(packet));
            }

            return reset;
        }

        bool Peek(
            std::uint32_t& requiredBytes,
            std::uint64_t& frameId,
            bool& keyFrame) const
        {
            std::lock_guard<std::mutex> lock(m_mutex);

            if (m_queue.empty())
                return false;

            const auto& packet = m_queue.front();
            requiredBytes = static_cast<std::uint32_t>(packet.Data.size());
            frameId = packet.FrameId;
            keyFrame = packet.KeyFrame;
            return true;
        }

        PopResult Pop(
            std::uint8_t* destination,
            std::uint32_t destinationBytes,
            std::uint32_t& writtenBytes,
            std::uint64_t& frameId,
            bool& keyFrame)
        {
            std::lock_guard<std::mutex> lock(m_mutex);

            if (m_queue.empty())
                return PopResult::Empty;

            const auto& packet = m_queue.front();
            const auto packetBytes =
                static_cast<std::uint32_t>(packet.Data.size());

            if (!destination || destinationBytes < packetBytes)
            {
                writtenBytes = packetBytes;
                return PopResult::InsufficientBuffer;
            }

            std::memcpy(destination, packet.Data.data(), packetBytes);
            writtenBytes = packetBytes;
            frameId = packet.FrameId;
            keyFrame = packet.KeyFrame;
            m_queuedBytes -= packetBytes;
            m_queue.pop_front();
            return PopResult::Success;
        }

        EncodedQueueState GetState() const
        {
            std::lock_guard<std::mutex> lock(m_mutex);
            return {m_active.load(), m_waitingForKeyFrame,
                static_cast<std::uint32_t>(m_queue.size()), m_queuedBytes};
        }

        std::uint32_t QueuedPackets() const
        {
            std::lock_guard<std::mutex> lock(m_mutex);
            return static_cast<std::uint32_t>(m_queue.size());
        }

        std::uint32_t QueuedBytes() const
        {
            std::lock_guard<std::mutex> lock(m_mutex);
            return m_queuedBytes;
        }

        std::uint64_t ResetCount() const noexcept
        {
            return m_resetCount.load();
        }

        bool ConsumeIdrRequest() noexcept
        {
            return m_idrRequested.exchange(false);
        }

        void ResetCounters() noexcept
        {
            m_resetCount.store(0);
        }

    private:
        std::atomic<bool> m_active{false};
        std::atomic<bool> m_idrRequested{false};
        std::atomic<std::uint64_t> m_resetCount{0};
        mutable std::mutex m_mutex;
        std::deque<EncodedPacket> m_queue;
        std::uint32_t m_queuedBytes = 0;
        bool m_waitingForKeyFrame = true;
    };
}
