#include "../../Multiverse.Core/EncodedQueueAdmissionPolicy.h"
#include "../../Multiverse.Core/SharedEncodedPacketQueue.h"
#include <cassert>
#include <cstdint>
#include <deque>
#include <iostream>
#include <vector>
using namespace multiverse;
static EncodedPacket packet(std::uint64_t id, bool key, std::size_t bytes = 128) {
    EncodedPacket p; p.FrameId = id; p.KeyFrame = key; p.Data.resize(bytes, 7); return p;
}
int main() {
    EncodedQueueAdmissionPolicy policy;
    SharedEncodedPacketQueue q;
    assert(!policy.ShouldDefer({false, false, 0, 0}, 0, 4));
    q.SetActive(true);
    auto s = q.GetState();
    assert(s.Active && s.WaitingForKeyFrame && s.Packets == 0);
    assert(!policy.ShouldDefer(s, 0, 4));
    assert(policy.ShouldDefer(s, 1, 4));
    assert(!q.Push(packet(1, true)));
    assert(!q.Push(packet(2, false)));
    assert(!q.Push(packet(3, false)));
    assert(policy.ShouldDefer(q.GetState(), 0, 4));
    assert(policy.ShouldDefer({true, false, 1, 4u * 1024u * 1024u}, 0, 4));
    assert(policy.ShouldDefer({true, false, 2, 128}, 4, 4));
    assert(!policy.ShouldDefer({true, false, 1, 128}, 0, 4));
    q.Clear(true);
    assert(q.GetState().WaitingForKeyFrame);
    assert(!policy.ShouldDefer(q.GetState(), 0, 4));
    assert(policy.ShouldDefer(q.GetState(), 1, 4));
    assert(!q.Push(packet(4, false)));
    assert(!q.Push(packet(5, true)));
    assert(q.GetState().Packets == 1);

    // Real queue, simulated asynchronous encoder: 60-Hz producer, a four-slot
    // encoder, and a slower 20-Hz consumer. Admission happens before encoding.
    // No already-coded P-frame is discarded to make room for another input.
    q.SetActive(false); q.SetActive(true);
    assert(q.ConsumeIdrRequest());
    std::deque<EncodedPacket> inFlight;
    std::vector<std::uint8_t> buffer(1024);
    std::uint64_t id = 0, lastDelivered = 0;
    int admitted = 0, deferred = 0, delivered = 0;
    for (int tick = 0; tick < 1200; ++tick) {
        if (!policy.ShouldDefer(q.GetState(), static_cast<std::uint32_t>(inFlight.size()), 4)) {
            inFlight.push_back(packet(++id, admitted == 0));
            ++admitted;
        } else ++deferred;
        if (!inFlight.empty()) {
            assert(!q.Push(std::move(inFlight.front())));
            inFlight.pop_front();
        }
        if (tick % 3 == 0) {
            std::uint32_t bytes = 0; std::uint64_t frameId = 0; bool key = false;
            if (q.Pop(buffer.data(), static_cast<std::uint32_t>(buffer.size()), bytes, frameId, key) == SharedEncodedPacketQueue::PopResult::Success) {
                assert(frameId > lastDelivered);
                lastDelivered = frameId;
                ++delivered;
            }
        }
        assert(q.ResetCount() == 0);
        assert(q.GetState().Packets <= EncodedQueueAdmissionPolicy::SoftPackets);
    }
    assert(deferred > 0 && delivered > 300);
    assert(lastDelivered > 0);

    // A healthy 60-Hz sender must not be throttled by the soft limit.
    // Model two ticks of asynchronous encode latency and a 60-Hz consumer.
    q.SetActive(false); q.SetActive(true); q.ConsumeIdrRequest();
    struct Pending { EncodedPacket packet; int ready; };
    std::deque<Pending> pending;
    int healthyAdmitted = 0, healthyDelivered = 0;
    for (int tick = 0; tick < 900; ++tick) {
        while (!pending.empty() && pending.front().ready <= tick) {
            assert(!q.Push(std::move(pending.front().packet)));
            pending.pop_front();
        }
        std::uint32_t bytes = 0; std::uint64_t frameId = 0; bool key = false;
        if (q.Pop(buffer.data(), static_cast<std::uint32_t>(buffer.size()), bytes, frameId, key)
            == SharedEncodedPacketQueue::PopResult::Success) ++healthyDelivered;
        if (!policy.ShouldDefer(q.GetState(), static_cast<std::uint32_t>(pending.size()), 4)) {
            pending.push_back({packet(++id, healthyAdmitted == 0), tick + 2});
            ++healthyAdmitted;
        }
        assert(q.ResetCount() == 0);
    }
    assert(healthyDelivered >= 895);
    assert(healthyAdmitted >= 897);
    // An inactive consumer must never impose remote-stream backpressure.
    q.SetActive(false);
    assert(!policy.ShouldDefer(q.GetState(), 4, 4));
    // The emergency byte bound remains unchanged, even for a single IDR.
    q.SetActive(true);
    assert(!q.Push(packet(++id, true, 1024)));
    assert(q.Push(packet(++id, false, 8u * 1024u * 1024u)));
    assert(q.ResetCount() == 1);
    assert(q.GetState().WaitingForKeyFrame);
    assert(q.ConsumeIdrRequest());
    assert(!q.ConsumeIdrRequest());
    // A valid recovery IDR can restore the queue without accepting stale P-frames.
    assert(!q.Push(packet(++id, false)));
    assert(!q.Push(packet(++id, true)));
    assert(q.GetState().Packets == 1);
    std::cout << "healthy 60-Hz admission and emergency recovery: PASS\n";
    std::cout << "queue admission: PASS (admitted=" << admitted << ", deferred=" << deferred << ", delivered=" << delivered << ")\n";
}
