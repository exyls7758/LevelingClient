using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Multiverse.Services
{
    internal sealed class RemoteAudioDatagram
    {
        public uint Sequence { get; init; }
        public long TimestampUs { get; init; }
        public ushort SamplesPerChannel { get; init; }
        public byte[] Payload { get; init; } = Array.Empty<byte>();
    }

    /// <summary>
    /// Audio has its own real-time UDP lane so it can never head-of-line block
    /// video, control, cursor or gamepad traffic. The sender queue is tiny and
    /// DropOldest: if the machine/network falls behind, fresh audio wins.
    /// </summary>
    internal sealed class RemoteAudioSender : IDisposable
    {
        public const int AudioPort = 47823;
        public const uint Magic = 0x4D564155; // MVAU
        public const byte ProtocolVersion = 1;
        public const int HeaderBytes = 24;
        public const int MaxPayloadBytes = 1150;

        private readonly IPEndPoint _remote;
        private readonly UdpClient _udp;
        private readonly Channel<RemoteAudioDatagram> _queue;
        private readonly CancellationTokenSource _cancel = new();
        private readonly Task _worker;
        private long _sentPackets;
        private long _sentBytes;
        private long _droppedPackets;
        private volatile bool _stopping;
        private bool _disposed;

        public RemoteAudioSender(IPAddress remoteAddress)
        {
            _remote = new IPEndPoint(remoteAddress, AudioPort);
            _udp = new UdpClient(remoteAddress.AddressFamily);
            _queue = Channel.CreateBounded<RemoteAudioDatagram>(
                new BoundedChannelOptions(3)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.DropOldest,
                    AllowSynchronousContinuations = false
                });
            _worker = Task.Run(() => SendLoopAsync(_cancel.Token));
        }

        public long SentPackets => Interlocked.Read(ref _sentPackets);
        public long SentBytes => Interlocked.Read(ref _sentBytes);
        public long DroppedPackets => Interlocked.Read(ref _droppedPackets);

        public bool TryQueue(RemoteAudioDatagram datagram)
        {
            if (_disposed || _stopping || datagram.Payload.Length == 0 ||
                datagram.Payload.Length > MaxPayloadBytes)
            {
                Interlocked.Increment(ref _droppedPackets);
                return false;
            }

            bool accepted = _queue.Writer.TryWrite(datagram);
            if (!accepted)
                Interlocked.Increment(ref _droppedPackets);
            return accepted;
        }

        private async Task SendLoopAsync(CancellationToken token)
        {
            try
            {
                while (await _queue.Reader.WaitToReadAsync(token).ConfigureAwait(false))
                {
                    while (_queue.Reader.TryRead(out RemoteAudioDatagram? datagram))
                    {
                        byte[] packet = BuildPacket(datagram);
                        await _udp.SendAsync(packet, packet.Length, _remote)
                            .ConfigureAwait(false);
                        Interlocked.Increment(ref _sentPackets);
                        Interlocked.Add(ref _sentBytes, packet.Length);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException ex)
            {
                // Audio transport failure is intentionally isolated. Expected
                // socket interruption during Stop is not reported as a failure.
                if (!_stopping && !_cancel.IsCancellationRequested)
                    StartupDiagnostics.RecordException("AUDIO_TX_WORKER_ERROR", ex);
            }
            catch (Exception ex)
            {
                if (!_stopping && !_cancel.IsCancellationRequested)
                    StartupDiagnostics.RecordException("AUDIO_TX_WORKER_ERROR", ex);
            }
        }

        private static byte[] BuildPacket(RemoteAudioDatagram datagram)
        {
            int payloadLength = Math.Min(datagram.Payload.Length, MaxPayloadBytes);
            byte[] packet = new byte[HeaderBytes + payloadLength];
            Span<byte> header = packet.AsSpan(0, HeaderBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(0, 4), Magic);
            header[4] = ProtocolVersion;
            header[5] = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(
                header.Slice(6, 2), checked((ushort)payloadLength));
            BinaryPrimitives.WriteUInt32LittleEndian(
                header.Slice(8, 4), datagram.Sequence);
            BinaryPrimitives.WriteInt64LittleEndian(
                header.Slice(12, 8), datagram.TimestampUs);
            BinaryPrimitives.WriteUInt16LittleEndian(
                header.Slice(20, 2), datagram.SamplesPerChannel);
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(22, 2), 0);
            Buffer.BlockCopy(datagram.Payload, 0, packet, HeaderBytes, payloadLength);
            return packet;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _stopping = true;
            _queue.Writer.TryComplete();
            _cancel.Cancel();
            try { _udp.Dispose(); } catch { }
            try
            {
                ConfirmedTask.WaitOrThrow(_worker, 750, "Audio TX");
            }
            catch (Exception ex)
            {
                StartupDiagnostics.RecordException("AUDIO_TX_STOP_TIMEOUT", ex);
                throw;
            }
            _cancel.Dispose();
            _disposed = true;
        }
    }

    internal sealed class RemoteAudioReceiver : IDisposable
    {
        public const int AudioPort = RemoteAudioSender.AudioPort;
        public const int MaxPayloadBytes = RemoteAudioSender.MaxPayloadBytes;

        private readonly IPAddress? _expectedRemoteAddress;
        private readonly CancellationTokenSource _cancel = new();
        private UdpClient? _udp;
        private Task? _worker;
        private long _receivedPackets;
        private long _receivedBytes;
        private long _invalidPackets;
        private volatile bool _stopping;
        private bool _disposed;

        public RemoteAudioReceiver(IPAddress? expectedRemoteAddress)
        {
            _expectedRemoteAddress = expectedRemoteAddress;
        }

        public event Action<RemoteAudioDatagram>? PacketReceived;

        public long ReceivedPackets => Interlocked.Read(ref _receivedPackets);
        public long ReceivedBytes => Interlocked.Read(ref _receivedBytes);
        public long InvalidPackets => Interlocked.Read(ref _invalidPackets);

        public void Start()
        {
            if (_disposed || _stopping || _worker is not null)
                return;

            IPAddress bindAddress = _expectedRemoteAddress?.AddressFamily == AddressFamily.InterNetworkV6
                ? IPAddress.IPv6Any : IPAddress.Any;
            _udp = new UdpClient(new IPEndPoint(bindAddress, AudioPort));
            _udp.Client.ReceiveBufferSize = 256 * 1024;
            _worker = Task.Run(() => ReceiveLoopAsync(_cancel.Token));
        }

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            UdpClient? udp = _udp;
            if (udp is null)
                return;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    UdpReceiveResult result = await udp.ReceiveAsync(token)
                        .ConfigureAwait(false);

                    if (_expectedRemoteAddress is not null &&
                        !result.RemoteEndPoint.Address.Equals(_expectedRemoteAddress))
                    {
                        Interlocked.Increment(ref _invalidPackets);
                        continue;
                    }

                    if (!TryParse(result.Buffer, out RemoteAudioDatagram? datagram))
                    {
                        Interlocked.Increment(ref _invalidPackets);
                        continue;
                    }

                    Interlocked.Increment(ref _receivedPackets);
                    Interlocked.Add(ref _receivedBytes, result.Buffer.Length);
                    try { PacketReceived?.Invoke(datagram); } catch { }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException ex)
            {
                // Receiver is best-effort and never owns the session lifecycle.
                // Ignore the expected interruption caused by Stop/Dispose.
                if (!_stopping && !_cancel.IsCancellationRequested)
                    StartupDiagnostics.RecordException("AUDIO_RX_WORKER_ERROR", ex);
            }
            catch (Exception ex)
            {
                if (!_stopping && !_cancel.IsCancellationRequested)
                    StartupDiagnostics.RecordException("AUDIO_RX_WORKER_ERROR", ex);
            }
        }

        internal static bool TryParse(
            byte[] packet,
            [NotNullWhen(true)] out RemoteAudioDatagram? datagram)
        {
            datagram = null;
            if (packet.Length < RemoteAudioSender.HeaderBytes)
                return false;

            ReadOnlySpan<byte> header =
                packet.AsSpan(0, RemoteAudioSender.HeaderBytes);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0, 4)) !=
                    RemoteAudioSender.Magic ||
                header[4] != RemoteAudioSender.ProtocolVersion)
            {
                return false;
            }

            ushort payloadLength =
                BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(6, 2));
            if (payloadLength == 0 ||
                payloadLength > MaxPayloadBytes ||
                packet.Length != RemoteAudioSender.HeaderBytes + payloadLength)
            {
                return false;
            }

            byte[] payload = new byte[payloadLength];
            Buffer.BlockCopy(
                packet,
                RemoteAudioSender.HeaderBytes,
                payload,
                0,
                payloadLength);

            datagram = new RemoteAudioDatagram
            {
                Sequence = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(8, 4)),
                TimestampUs = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(12, 8)),
                SamplesPerChannel = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(20, 2)),
                Payload = payload
            };
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _stopping = true;
            _cancel.Cancel();
            try { _udp?.Dispose(); } catch { }
            try
            {
                ConfirmedTask.WaitOrThrow(_worker, 750, "Audio RX");
            }
            catch (Exception ex)
            {
                StartupDiagnostics.RecordException("AUDIO_RX_STOP_TIMEOUT", ex);
                throw;
            }
            _worker = null;
            _udp = null;
            _cancel.Dispose();
            _disposed = true;
        }
    }
}
