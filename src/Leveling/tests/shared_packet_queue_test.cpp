#include "../Multiverse.Core/SharedEncodedPacketQueue.h"
#include <cassert>
#include <cstdint>
#include <thread>
#include <vector>
#include <iostream>
using multiverse::EncodedPacket;
using multiverse::SharedEncodedPacketQueue;
static EncodedPacket pkt(std::size_t n, bool key, std::uint64_t id) {
    EncodedPacket p; p.Data.assign(n, 0x5a); p.KeyFrame = key; p.FrameId = id; return p;
}
int main() {
    SharedEncodedPacketQueue q;
    q.SetActive(true);
    assert(q.ConsumeIdrRequest());
    q.Push(pkt(100, false, 1));
    assert(q.QueuedPackets() == 0);
    q.Push(pkt(100, true, 2));
    assert(q.QueuedPackets() == 1);
    q.Clear(true);
    assert(q.ConsumeIdrRequest());
    q.Push(pkt(100, false, 3));
    assert(q.QueuedPackets() == 0);
    q.Push(pkt(100, true, 4));
    assert(q.QueuedPackets() == 1);
    q.SetActive(false);
    q.SetActive(true);
    q.ConsumeIdrRequest();
    assert(q.Push(pkt(8u * 1024u * 1024u + 1u, true, 5)));
    assert(q.QueuedPackets() == 0 && q.QueuedBytes() == 0);
    assert(q.ConsumeIdrRequest());
    q.Push(pkt(100, false, 6));
    assert(q.QueuedPackets() == 0);
    q.Push(pkt(100, true, 7));
    q.Clear(false);
    assert(q.Push(pkt(8u * 1024u * 1024u + 1u, false, 8)));
    q.Push(pkt(100, false, 9));
    assert(q.QueuedPackets() == 0);
    q.Push(pkt(100, true, 10));
    assert(q.QueuedPackets() == 1);
    q.SetActive(false);
    assert(!q.Push(pkt(8u * 1024u * 1024u + 1u, true, 11)));
    assert(q.QueuedPackets() == 0);
    for (int i = 0; i < 5000; ++i) {
        q.SetActive(true);
        q.ConsumeIdrRequest();
        q.Push(pkt(64, true, 12));
        std::thread t1([&] { q.SetActive(false); });
        std::thread t2([&] { q.Push(pkt(64, false, 13)); });
        t1.join(); t2.join();
        assert(!q.IsActive() && q.QueuedPackets() == 0 && q.QueuedBytes() == 0);
    }
    // A concurrent clear must leave the IDR request visible to the encoder.
    for (int i = 0; i < 5000; ++i) {
        q.SetActive(true);
        q.ConsumeIdrRequest();
        std::thread t([&] { q.Clear(true); });
        t.join();
        assert(q.ConsumeIdrRequest());
        q.Push(pkt(64, false, 14));
        assert(q.QueuedPackets() == 0);
    }
    std::cout << "shared_packet_queue_test: PASS\n";
}
