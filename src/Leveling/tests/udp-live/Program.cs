using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Multiverse.Services;

static void Check(bool ok, string detail) { if (!ok) throw new Exception(detail); }
static byte[] Packet(ulong frame, ushort index, bool key, uint stream = 77, uint seq = 1)
{
    int n = index == 0 ? MultiverseVideoProtocol.MaxPayloadBytes : 17;
    var packet = new byte[MultiverseVideoProtocol.HeaderBytes + n];
    MultiverseVideoProtocol.WriteHeader(packet, key ? MultiverseVideoProtocol.FlagKeyFrame : (ushort)0,
        stream, frame, Stopwatch.GetTimestamp(), (uint)(MultiverseVideoProtocol.MaxPayloadBytes + 17), index, 2,
        (uint)(index * MultiverseVideoProtocol.MaxPayloadBytes), (ushort)n, seq);
    packet.AsSpan(MultiverseVideoProtocol.HeaderBytes).Fill((byte)frame);
    return packet;
}

int failed = 0;
async Task Run(string name, Func<Task> test)
{
    try { await test(); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
}

await Run("ready frames drain without another network packet", async () =>
{
    using var receiver = new VideoUdpReceiver(IPAddress.Loopback, 0, expectedRemoteAddress: IPAddress.Loopback, expectedStreamId: 77);
    using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    var delivered = new ConcurrentQueue<ReassembledVideoFrame>();
    receiver.FrameCompleted += delivered.Enqueue;
    receiver.Start();
    var endpoint = new IPEndPoint(IPAddress.Loopback, receiver.LocalPort);
    // A small P-frame completes while the fragmented IDR is still in flight.
    sender.SendTo(Packet(1, 0, true, seq: 1), endpoint);
    sender.SendTo(Packet(2, 0, false, seq: 2), endpoint);
    sender.SendTo(Packet(2, 1, false, seq: 3), endpoint);
    sender.SendTo(Packet(1, 1, true, seq: 4), endpoint);
    var until = Stopwatch.StartNew();
    while (delivered.Count < 2 && until.ElapsedMilliseconds < 800) await Task.Delay(5);
    Check(delivered.Select(f => f.FrameId).SequenceEqual(new ulong[] {1,2}), "a completed dependent frame was stranded after its IDR");
    Check(delivered.All(f => f.Data.All(b => b == (byte)f.FrameId)), "payload mismatch");
    receiver.Stop();
    Check(!receiver.Running, "receiver still running after Stop");
});

await Run("foreign generation cannot supply a frame", async () =>
{
    using var receiver = new VideoUdpReceiver(IPAddress.Loopback, 0, expectedStreamId: 77);
    using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    var delivered = new ConcurrentQueue<ReassembledVideoFrame>(); receiver.FrameCompleted += delivered.Enqueue;
    receiver.Start(); var endpoint = new IPEndPoint(IPAddress.Loopback, receiver.LocalPort);
    sender.SendTo(Packet(1, 0, true, 76), endpoint); sender.SendTo(Packet(1, 1, true, 76), endpoint);
    sender.SendTo(Packet(2, 0, true, 77), endpoint); sender.SendTo(Packet(2, 1, true, 77), endpoint);
    await Task.Delay(150);
    Check(delivered.Count == 1 && delivered.Single().FrameId == 2, "stale generation reached decode handoff");
});

await Run("complete recovery IDR survives expiration of an older partial frame", async () =>
{
    using var receiver = new VideoUdpReceiver(IPAddress.Loopback, 0, expectedStreamId: 77);
    using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    var delivered = new ConcurrentQueue<ReassembledVideoFrame>(); receiver.FrameCompleted += delivered.Enqueue;
    receiver.Start(); var endpoint = new IPEndPoint(IPAddress.Loopback, receiver.LocalPort);
    sender.SendTo(Packet(1, 0, true, seq: 1), endpoint);
    sender.SendTo(Packet(2, 0, true, seq: 2), endpoint);
    sender.SendTo(Packet(2, 1, true, seq: 3), endpoint);
    var until = Stopwatch.StartNew();
    while (delivered.IsEmpty && until.ElapsedMilliseconds < 1200) await Task.Delay(5);
    Check(delivered.Count == 1 && delivered.Single().FrameId == 2,
        "fully assembled recovery keyframe was discarded or stranded during timeout cleanup");
});

await Run("FEC recovers a missing fragment byte for byte", async () =>
{
    using var receiver = new VideoUdpReceiver(IPAddress.Loopback, 0, expectedStreamId: 77);
    using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    var delivered = new ConcurrentQueue<ReassembledVideoFrame>(); receiver.FrameCompleted += delivered.Enqueue;
    receiver.Start(); var endpoint = new IPEndPoint(IPAddress.Loopback, receiver.LocalPort);
    byte[] parity = Packet(9, 0, true, seq: 2);
    parity[6] = (byte)(MultiverseVideoProtocol.FlagKeyFrame | MultiverseVideoProtocol.FlagFecParity);
    // XOR of a full constant fragment and a 17-byte tail, padded with zeroes.
    parity.AsSpan(MultiverseVideoProtocol.HeaderBytes, 17).Clear();
    sender.SendTo(Packet(9, 1, true, seq: 1), endpoint);
    sender.SendTo(parity, endpoint);
    var until = Stopwatch.StartNew();
    while (delivered.IsEmpty && until.ElapsedMilliseconds < 800) await Task.Delay(5);
    Check(delivered.Count == 1 && delivered.Single().Data.All(b => b == 9), "FEC payload mismatch");
    Check(receiver.GetSnapshot().FecRecoveredFragments == 1, "missing fragment was not recovered");
});

await Run("twenty UDP receiver restart cycles", async () =>
{
    using var receiver = new VideoUdpReceiver(IPAddress.Loopback, 0, expectedStreamId: 77);
    using var sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    int count = 0; receiver.FrameCompleted += _ => Interlocked.Increment(ref count);
    for (int i = 0; i < 20; i++)
    {
        receiver.Start(); var endpoint = new IPEndPoint(IPAddress.Loopback, receiver.LocalPort);
        sender.SendTo(Packet(1, 0, true, seq: 1), endpoint);
        sender.SendTo(Packet(1, 1, true, seq: 2), endpoint);
        var until = Stopwatch.StartNew();
        while (Volatile.Read(ref count) < i + 1 && until.ElapsedMilliseconds < 800) await Task.Delay(2);
        Check(count == i + 1, $"cycle {i + 1} did not deliver exactly one frame");
        receiver.Stop(); Check(!receiver.Running, "receiver retained running state");
    }
});


await Run("failed bind leaves no socket and permits retry", async () =>
{
    using var occupied = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    occupied.ExclusiveAddressUse = true;
    occupied.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    int port = ((IPEndPoint)occupied.LocalEndPoint!).Port;
    using var receiver = new VideoUdpReceiver(IPAddress.Loopback, port, expectedStreamId: 77);
    for (int attempt=0; attempt<5; attempt++) {
        try { receiver.Start(); throw new Exception("occupied port accepted"); } catch(SocketException) { }
        var socket=typeof(VideoUdpReceiver).GetField("_socket",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(receiver);
        Check(socket is null && !receiver.Running && receiver.LocalPort==0,"failed Start retained socket/state");
    }
    occupied.Dispose();
    receiver.Start(); Check(receiver.Running,"retry after bind failure failed"); receiver.Stop();
    await Task.CompletedTask;
});

await Run("large sequence discontinuity has bounded work and correct loss", async () =>
{
    using var receiver = new VideoUdpReceiver(IPAddress.Loopback, 0, expectedStreamId: 77);
    var method=typeof(VideoUdpReceiver).GetMethod("TrackSequence",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
    method.Invoke(receiver,new object[]{1u});
    var elapsed=Stopwatch.StartNew();
    method.Invoke(receiver,new object[]{1000000000u});
    Console.WriteLine("METRIC sequenceJumpMs="+elapsed.Elapsed.TotalMilliseconds.ToString("0.000"));
    Check(elapsed.ElapsedMilliseconds<150,"sequence jump stalled RX for "+elapsed.ElapsedMilliseconds+" ms");
    Check(receiver.GetSnapshot().SequenceGaps==999999871,"wrong bounded loss calculation");
    await Task.CompletedTask;
});

await Run("sequence rollover preserves two true losses and bounds history", async () =>
{
    using var receiver = new VideoUdpReceiver(IPAddress.Loopback, 0, expectedStreamId:77);
    var method=typeof(VideoUdpReceiver).GetMethod("TrackSequence",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
    uint first=uint.MaxValue-100;
    for(uint n=0;n<400;n++) if(n!=10 && n!=11) method.Invoke(receiver,new object[]{unchecked(first+n)});
    method.Invoke(receiver,new object[]{first}); // finalized duplicate
    var recent=(System.Collections.Generic.HashSet<uint>)typeof(VideoUdpReceiver).GetField("_recentSequences",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(receiver)!;
    Check(receiver.GetSnapshot().SequenceGaps==2,"rollover changed loss count");
    Check(recent.Count<=128,"sequence history grows beyond reorder window");
    await Task.CompletedTask;
});

await Run("late duplicates and reordered fragments do not resurrect delivered frames", async () =>
{
    using var receiver = new VideoUdpReceiver(IPAddress.Loopback,0,expectedStreamId:77);
    using var sender = new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp);
    var delivered=new ConcurrentQueue<ReassembledVideoFrame>();receiver.FrameCompleted+=delivered.Enqueue;
    receiver.Start();var endpoint=new IPEndPoint(IPAddress.Loopback,receiver.LocalPort);
    sender.SendTo(Packet(1,1,true,seq:2),endpoint);
    await Task.Delay(20);
    sender.SendTo(Packet(1,0,true,seq:1),endpoint);
    await Task.Delay(40);
    sender.SendTo(Packet(1,0,true,seq:1),endpoint);sender.SendTo(Packet(1,1,true,seq:2),endpoint);
    sender.SendTo(Packet(2,1,false,seq:4),endpoint);sender.SendTo(Packet(2,0,false,seq:3),endpoint);
    await Task.Delay(180);
    Check(delivered.Select(f=>f.FrameId).SequenceEqual(new ulong[]{1,2}),"duplicate delivery or reorder failure");
    Check(receiver.GetSnapshot().SequenceGaps==0,"reorder incorrectly counted as loss");
});

await Run("lost dependent frame resumes at next IDR without receiver recreation", async () =>
{
    using var receiver = new VideoUdpReceiver(IPAddress.Loopback,0,expectedStreamId:77);
    using var sender = new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp);
    var delivered=new ConcurrentQueue<ReassembledVideoFrame>();receiver.FrameCompleted+=delivered.Enqueue;
    receiver.Start();var endpoint=new IPEndPoint(IPAddress.Loopback,receiver.LocalPort);
    sender.SendTo(Packet(1,0,true,seq:1),endpoint);sender.SendTo(Packet(1,1,true,seq:2),endpoint);
    await Task.Delay(30);
    sender.SendTo(Packet(2,0,false,seq:3),endpoint);
    await Task.Delay(350);
    sender.SendTo(Packet(3,0,true,seq:5),endpoint);sender.SendTo(Packet(3,1,true,seq:6),endpoint);
    sender.SendTo(Packet(4,0,false,seq:7),endpoint);sender.SendTo(Packet(4,1,false,seq:8),endpoint);
    await Task.Delay(180);
    Check(delivered.Select(f=>f.FrameId).SequenceEqual(new ulong[]{1,3,4}),"IDR did not restore dependent frames");
    Check(receiver.Running && receiver.LocalPort==endpoint.Port,"receiver was recreated");
});

Console.WriteLine($"Live UDP regression: {failed} failures");
return failed == 0 ? 0 : 1;

namespace Multiverse.Services
{
    // Diagnostics sink only: network, fragment parsing, FEC, socket lifecycle and ordering are production code.
    internal sealed class VideoIncidentRecorder
    {
        public static VideoIncidentRecorder Instance { get; } = new();
        public void RecordEvent(string reason, object? details) { }
    }
}
