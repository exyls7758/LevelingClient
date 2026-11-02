using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Microsoft.UI.Xaml;

namespace Multiverse.Services;

// Explicit diagnostic, loopback only. Exercises the production post-authorization
// control/session implementation in two processes. It does NOT test friend auth.
internal static class SessionLoopbackSelfTest
{
    internal static async Task RunAsync(string directory, bool host)
    {
        int exit = 1;
        Directory.CreateDirectory(directory);
        string output = Path.Combine(directory, host ? "session-host.log" : "session-client.log");
        void Log(string text) => File.AppendAllText(output, DateTime.UtcNow.ToString("O") + " " + text + Environment.NewLine);
        var session = RemoteSessionService.Instance;
        var inputPackets=new ConcurrentQueue<DesktopInputPacket>();
        // Wire test only: no DesktopInputService or Windows input sink is created.
        session.DesktopInputReceived+=(packet,_)=>inputPackets.Enqueue(packet);
        TcpListener? listener = null;
        try
        {
            Log("START production TCP/UDP loopback; authorization and codec not tested");
            int port;
            string portFile = Path.Combine(directory, "session.port");
            if (host)
            {
                listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                port = ((IPEndPoint)listener.LocalEndpoint).Port;
                File.WriteAllText(portFile, port.ToString());
            }
            else
            {
                await Until(() => File.Exists(portFile));
                port = int.Parse(File.ReadAllText(portFile));
            }
            for (int cycle = 1; cycle <= 5; cycle++)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                TcpClient tcp;
                if (host) tcp = await listener!.AcceptTcpClientAsync(timeout.Token);
                else { tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, port, timeout.Token); }
                tcp.NoDelay = true;
                var stream = tcp.GetStream();
                var reader = new BoundedLineReader(new StreamReader(stream, Encoding.UTF8, false, 4096, true), 65536);
                var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                int udpPort;
                if (host) udpPort = int.Parse((await reader.ReadLineAsync(timeout.Token))!);
                else
                {
                    using var reserve = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    reserve.Bind(new IPEndPoint(IPAddress.Loopback, 0)); udpPort = ((IPEndPoint)reserve.LocalEndPoint!).Port;
                    reserve.Dispose(); await writer.WriteLineAsync(udpPort.ToString());
                }
                // The normal auth path invokes this same activation method.
                // No listening service or authorization exception is added to normal operation.
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                int generation = (int)typeof(RemoteSessionService).GetMethod("ActivateSession", flags)!.Invoke(session,
                    new object[] { tcp, reader, writer, host ? RemoteSessionRole.Host : RemoteSessionRole.Client,
                        "loopback", "Diagnostic", new IPEndPoint(IPAddress.Loopback, udpPort), "", true, false })!;
                var control = (Task)typeof(RemoteSessionService).GetMethod("ControlLoopAsync", flags)!.Invoke(session,
                    new object[] { generation, session.GetVideoSessionToken(generation) })!;
                // Exercise the real JSON envelope, priority queue and read loop.
                // Start/ACK and held input semantics are separately tested with a fake OS sink.
                if(session.TryPublishDesktopInput(new DesktopInputPacket {Kind="key",Epoch=cycle,Sequence=1,Code=65,Down=true},generation-1))
                    throw new Exception("Old generation input was queued");
                foreach(var packet in new[]{
                    new DesktopInputPacket {Kind="key",Epoch=cycle,Sequence=2,Code=87,Down=true},
                    new DesktopInputPacket {Kind="move",Epoch=cycle,Sequence=3,X=23,Y=-17,Wheel=120},
                    new DesktopInputPacket {Kind="key",Epoch=cycle,Sequence=4,Code=87,Down=false}})
                    if(!session.TryPublishDesktopInput(packet,generation))throw new Exception("Input queue rejected current session");
                await Until(()=>inputPackets.Count==3);
                var receivedInput=inputPackets.ToArray();inputPackets.Clear();
                if(receivedInput[0].Epoch!=cycle || !System.Linq.Enumerable.Any(receivedInput,p=>p.Kind=="move"&&p.X==23&&p.Y==-17&&p.Wheel==120) ||
                    !System.Linq.Enumerable.SequenceEqual(System.Linq.Enumerable.Select(System.Linq.Enumerable.Where(receivedInput,p=>p.Kind=="key"),p=>p.Down),new[]{true,false}))
                    throw new Exception("Input envelope payload/order mismatch");
                Log($"PASS cycle={cycle} keyboard/mouse envelopes roundtrip; old generation rejected; no OS injection");
                for (int run = 1; run <= 2; run++)
                {
                    if (host)
                    {
                        long id = await session.PublishStreamConfigAsync(1920, 1080, 60, 20000, generation);
                        if (!await session.WaitForRemoteVideoReadyAsync(id, generation, TimeSpan.FromSeconds(5), timeout.Token))
                            throw new Exception("VIDEO READY timeout");
                        var config = session.GetStreamConfig()!;
                        if (config.WireStreamId == 0) throw new Exception("Negotiated wire identity lost");
                        using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                        for (ulong frame=1; frame<=3; frame++)
                        {
                            byte[] packet = new byte[MultiverseVideoProtocol.HeaderBytes + 32];
                            MultiverseVideoProtocol.WriteHeader(packet, MultiverseVideoProtocol.FlagKeyFrame,
                                config.WireStreamId, frame, Stopwatch.GetTimestamp(), 32, 0, 1, 0, 32, session.NextVideoPacketSequence());
                            packet.AsSpan(MultiverseVideoProtocol.HeaderBytes).Fill((byte)frame);
                            udp.SendTo(packet, new IPEndPoint(IPAddress.Loopback, udpPort));
                            await Task.Delay(15);
                        }
                        await Until(() => session.GetSnapshot().RemoteCompleteFrames >= 3);
                        Log($"PASS cycle={cycle} stream={id} wire={config.WireStreamId} READY and 3 real UDP frames acknowledged");
                        await session.PublishStreamStoppedAsync(generation, id);
                        // Allow client to observe stop before publishing the next stream.
                        await Task.Delay(150);
                    }
                    else
                    {
                        await Until(() => session.GetStreamConfig() is not null);
                        var config = session.GetStreamConfig()!;
                        using var receiver = new VideoUdpReceiver(IPAddress.Loopback, udpPort, false, IPAddress.Loopback, config.WireStreamId);
                        int frames=0;
                        receiver.FrameCompleted += frame => {
                            if (frame.Data.Length != 32 || frame.Data[0] != (byte)frame.FrameId) return;
                            Interlocked.Increment(ref frames);
                        };
                        receiver.Start();
                        await Task.Delay(100); // readiness is deliberately asynchronous
                        await session.PublishVideoReadyAsync(config.Generation, generation);
                        await Until(() => Volatile.Read(ref frames) == 3);
                        var rx = receiver.GetSnapshot();
                        await session.PublishTransportTelemetryAsync(generation, config.Generation,
                            rx.ReceivedPackets, rx.ReceivedBytes, rx.SequenceGaps, 0, 0, 0, 0, 0, 0, 0,
                            completeFrames: frames); // no decoder/presenter metrics are fabricated
                        await Until(() => session.GetStreamConfig() is null);
                        receiver.Stop();
                        Log($"PASS cycle={cycle} stream={config.Generation} packets={rx.ReceivedPackets} frames={frames} stop confirmed");
                    }
                }
                if (host) await session.DisconnectAsync();
                await Until(() => !session.GetSnapshot().Connected);
                await control.WaitAsync(TimeSpan.FromSeconds(5));
                Log($"DISCONNECT cycle={cycle} confirmed");
            }
            Log("PASS five TCP sessions, ten negotiated UDP generations, stop/start and reconnect");
            exit = 0;
        }
        catch (Exception ex) { Log("FAIL " + ex); }
        finally
        {
            listener?.Stop(); session.Dispose();
            Environment.ExitCode = exit; Application.Current.Exit();
        }
    }
    private static async Task Until(Func<bool> predicate)
    {
        var clock = Stopwatch.StartNew();
        while (!predicate())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Session progress timeout");
            await Task.Delay(10);
        }
    }
}
