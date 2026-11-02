using System.Net;
using System.Net.Sockets;
using Multiverse.Services;

foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
{
    if (address.AddressFamily == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6)
        throw new Exception("IPv6 unavailable: test not validated");
    // Sequential cycles also verify that disposal releases the fixed audio port.
    for (int cycle = 0; cycle < 2; cycle++)
    {
        using var receiver = new RemoteAudioReceiver(address);
        var received = new TaskCompletionSource<RemoteAudioDatagram>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.PacketReceived += packet => received.TrySetResult(packet);
        receiver.Start();
        using var sender = new RemoteAudioSender(address);
        sender.TryQueue(new RemoteAudioDatagram { Sequence = 7, SamplesPerChannel = 960, Payload = new byte[] { 1, 2, 3 } });
        var result = await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (result.Sequence != 7 || !result.Payload.SequenceEqual(new byte[] { 1, 2, 3 })) throw new Exception("UDP audio mismatch");
        Console.WriteLine($"PASS audio UDP {address} start/stop cycle {cycle + 1}");
    }
}
namespace Multiverse.Services
{
    internal static class StartupDiagnostics
    {
        public static void RecordException(string stage, Exception ex) => Console.Error.WriteLine(stage + ": " + ex.Message);
    }
}
