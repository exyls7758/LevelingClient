#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Multiverse.Services
{
    /// <summary>
    /// Fundação do transporte de vídeo do Multiverse.
    ///
    /// Objetivos:
    /// - datagramas pequenos (1200 bytes) para evitar fragmentação de IP;
    /// - nenhuma fila grande: em jogo remoto frame velho perde valor rapidamente;
    /// - threads dedicadas para TX/RX;
    /// - mínimo de alocações no hot path de datagramas;
    /// - protocolo usado tanto no loopback diagnóstico quanto na sessão remota real.
    ///
    /// Core 09: o mesmo MVTP transporta NVENC -> UDP -> reassembly -> NVDEC,
    /// com pacing de alta resolução e recuperação explícita por IDR.
    /// </summary>
    internal static class MultiverseVideoProtocol
    {
        public const uint Magic = 0x5054564D; // "MVTP" em little-endian
        public const ushort Version = 6;

        // Mantemos o datagrama inteiro abaixo de 1200 bytes.
        public const int DatagramBytes = 1200;
        public const int HeaderBytes = 48;
        public const int MaxPayloadBytes =
            DatagramBytes - HeaderBytes;

        public const ushort FlagKeyFrame = 1 << 0;
        public const ushort FlagFecParity = 1 << 1;

        // Um pacote de paridade XOR para cada grupo de 8 fragmentos.
        // Recupera uma perda por grupo sem esperar RTT/IDR.
        public const int FecDataFragments = 8;

        // Limites defensivos.
        public const int MaxFrameBytes =
            8 * 1024 * 1024;

        public const int MaxFragmentsPerFrame =
            8192;

        public static void WriteHeader(
            Span<byte> destination,
            ushort flags,
            uint streamId,
            ulong frameId,
            long sendTimestamp,
            uint frameBytes,
            ushort fragmentIndex,
            ushort fragmentCount,
            uint payloadOffset,
            ushort payloadBytes,
            uint datagramSequence)
        {
            if (destination.Length < HeaderBytes)
                throw new ArgumentException(
                    "Buffer do cabeçalho é pequeno.");

            BinaryPrimitives.WriteUInt32LittleEndian(
                destination.Slice(0, 4),
                Magic);

            BinaryPrimitives.WriteUInt16LittleEndian(
                destination.Slice(4, 2),
                Version);

            BinaryPrimitives.WriteUInt16LittleEndian(
                destination.Slice(6, 2),
                flags);

            BinaryPrimitives.WriteUInt32LittleEndian(
                destination.Slice(8, 4),
                streamId);

            BinaryPrimitives.WriteUInt64LittleEndian(
                destination.Slice(12, 8),
                frameId);

            BinaryPrimitives.WriteInt64LittleEndian(
                destination.Slice(20, 8),
                sendTimestamp);

            BinaryPrimitives.WriteUInt32LittleEndian(
                destination.Slice(28, 4),
                frameBytes);

            BinaryPrimitives.WriteUInt16LittleEndian(
                destination.Slice(32, 2),
                fragmentIndex);

            BinaryPrimitives.WriteUInt16LittleEndian(
                destination.Slice(34, 2),
                fragmentCount);

            BinaryPrimitives.WriteUInt32LittleEndian(
                destination.Slice(36, 4),
                payloadOffset);

            BinaryPrimitives.WriteUInt16LittleEndian(
                destination.Slice(40, 2),
                payloadBytes);

            // 42..43 reservado para evolução do protocolo.
            destination[42] = 0;
            destination[43] = 0;

            BinaryPrimitives.WriteUInt32LittleEndian(
                destination.Slice(44, 4),
                datagramSequence);
        }

        public static bool TryReadHeader(
            ReadOnlySpan<byte> source,
            out VideoDatagramHeader header)
        {
            header = default;

            if (source.Length < HeaderBytes)
                return false;

            if (BinaryPrimitives.ReadUInt32LittleEndian(
                    source.Slice(0, 4)) != Magic)
            {
                return false;
            }

            ushort version =
                BinaryPrimitives.ReadUInt16LittleEndian(
                    source.Slice(4, 2));

            if (version != Version)
                return false;

            header = new VideoDatagramHeader
            {
                Flags =
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        source.Slice(6, 2)),

                StreamId =
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        source.Slice(8, 4)),

                FrameId =
                    BinaryPrimitives.ReadUInt64LittleEndian(
                        source.Slice(12, 8)),

                SendTimestamp =
                    BinaryPrimitives.ReadInt64LittleEndian(
                        source.Slice(20, 8)),

                FrameBytes =
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        source.Slice(28, 4)),

                FragmentIndex =
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        source.Slice(32, 2)),

                FragmentCount =
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        source.Slice(34, 2)),

                PayloadOffset =
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        source.Slice(36, 4)),

                PayloadBytes =
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        source.Slice(40, 2)),

                DatagramSequence =
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        source.Slice(44, 4))
            };

            if (header.FrameBytes == 0 ||
                header.FrameBytes > MaxFrameBytes ||
                header.FragmentCount == 0 ||
                header.FragmentCount >
                    MaxFragmentsPerFrame ||
                header.FragmentIndex >=
                    header.FragmentCount ||
                header.PayloadBytes == 0 ||
                header.PayloadBytes >
                    MaxPayloadBytes)
            {
                return false;
            }

            ulong payloadEnd =
                (ulong)header.PayloadOffset +
                header.PayloadBytes;

            if (payloadEnd > header.FrameBytes)
                return false;

            // The sender uses a canonical fixed-size fragment layout. Reject
            // inconsistent counts, offsets and lengths before allocating an
            // assembly. Otherwise overlapping fragments can look "complete"
            // while leaving invalid bytes in an H.264 access unit.
            int expectedFragmentCount = (int)(((ulong)header.FrameBytes +
                MaxPayloadBytes - 1) / MaxPayloadBytes);
            if (header.FragmentCount != expectedFragmentCount)
                return false;

            uint expectedOffset = (uint)header.FragmentIndex * MaxPayloadBytes;
            if (header.PayloadOffset != expectedOffset)
                return false;

            int expectedPayloadBytes = (int)Math.Min((ulong)MaxPayloadBytes,
                (ulong)header.FrameBytes - expectedOffset);
            if (header.FecParity)
            {
                if (header.FragmentIndex % FecDataFragments != 0)
                    return false;
                int groupEnd = Math.Min(expectedFragmentCount,
                    header.FragmentIndex + FecDataFragments);
                expectedPayloadBytes = groupEnd - header.FragmentIndex > 1
                    ? MaxPayloadBytes
                    : expectedPayloadBytes;
            }

            if (header.PayloadBytes != expectedPayloadBytes)
                return false;

            return true;
        }
    }


    // Numeric-only, best-effort diagnostics. Never let logging stop UDP.
    internal static class VideoTransportDiagnostics
    {
        internal static void Record(string reason, object? details = null)
        {
            try { VideoIncidentRecorder.Instance.RecordEvent(reason, details); }
            catch { }
        }
    }

    internal static class UdpSocketResilience
    {
        // Windows can surface an ICMP Port Unreachable as WSAECONNRESET on
        // UDP sockets. During a brief route/NAT/Tailscale transition that must
        // not tear down the entire real-time transport.
        internal const int SioUdpConnReset = unchecked((int)0x9800000C);

        internal static void ConfigureUdpSocketForTransientNetworkChanges(
            Socket socket)
        {
            if (!OperatingSystem.IsWindows())
                return;

            try
            {
                socket.IOControl(
                    SioUdpConnReset,
                    new byte[] { 0, 0, 0, 0 },
                    null);
            }
            catch
            {
                // Best-effort only. Per-datagram error handling below remains
                // authoritative even on systems/drivers that reject this IOCTL.
            }
        }

        internal static bool IsTransientUdpSocketError(
            SocketError error)
        {
            return error == SocketError.ConnectionReset ||
                   error == SocketError.ConnectionAborted ||
                   error == SocketError.NetworkReset ||
                   error == SocketError.NetworkDown ||
                   error == SocketError.NetworkUnreachable ||
                   error == SocketError.HostUnreachable ||
                   error == SocketError.HostDown ||
                   error == SocketError.TimedOut ||
                   error == SocketError.WouldBlock ||
                   error == SocketError.TryAgain ||
                   error == SocketError.NoBufferSpaceAvailable;
        }
    }

    internal struct VideoDatagramHeader
    {
        public ushort Flags;
        public uint StreamId;
        public ulong FrameId;
        public long SendTimestamp;
        public uint FrameBytes;
        public ushort FragmentIndex;
        public ushort FragmentCount;
        public uint PayloadOffset;
        public ushort PayloadBytes;
        public uint DatagramSequence;

        public bool KeyFrame =>
            (Flags &
             MultiverseVideoProtocol.FlagKeyFrame) != 0;

        public bool FecParity =>
            (Flags &
             MultiverseVideoProtocol.FlagFecParity) != 0;
    }

    internal sealed class ReassembledVideoFrame
    {
        public required byte[] Data { get; init; }
        public required ulong FrameId { get; init; }
        public required bool KeyFrame { get; init; }
        public required long SendTimestamp { get; init; }
        public required long ReceiveTimestamp { get; init; }
    }

    internal sealed class VideoUdpReceiverSnapshot
    {
        public bool Running { get; init; }
        public long ReceivedFrames { get; init; }
        public long ReceivedPackets { get; init; }
        public long ReceivedBytes { get; init; }
        public VideoFrameIntervalSnapshot PacketIntervals { get; init; } = new();
        public VideoFrameIntervalSnapshot FrameIntervals { get; init; } = new();
        public long KeyFrames { get; init; }
        public long SequenceGaps { get; init; }
        public long IncompleteFramesDropped { get; init; }
        public long FecRecoveredFragments { get; init; }
        public long ForeignPacketsDropped { get; init; }
        public int PendingFrames { get; init; }
        public double LastOneWayMs { get; init; }
        public double AverageOneWayMs { get; init; }
        public string LastError { get; init; } = "";
    }

    /// <summary>
    /// Receiver/reassembler pensado para o futuro cliente.
    /// Mantém no máximo poucos frames incompletos e descarta antigos:
    /// jogo remoto prefere o frame mais novo a uma fila crescente.
    /// </summary>
    internal sealed class VideoUdpReceiver :
        IDisposable
    {
        private sealed class FrameAssembly
        {
            public required byte[] Buffer;
            public required bool[] Fragments;
            public required ushort FragmentCount;
            public required bool KeyFrame;
            public required long SendTimestamp;

            public required byte[]?[] FecParity;

            public int ReceivedFragments;
            public int ReceivedBytes;
            public bool Complete;

            public long FirstTouchTimestamp =
                Stopwatch.GetTimestamp();

            public long LastTouchTimestamp =
                Stopwatch.GetTimestamp();
        }

        private readonly object _lifecycleSync =
            new();

        private readonly object _framesSync =
            new();

        private readonly IPAddress _bindAddress;
        private readonly int _requestedPort;
        private readonly bool _measureOneWayLatency;
        private readonly IPAddress? _expectedRemoteAddress;

        private Socket? _socket;
        private Thread? _thread;

        private volatile bool _stop;
        private volatile bool _running;
        private volatile bool _disposed;

        private readonly byte[] _receiveBuffer =
            new byte[
                MultiverseVideoProtocol.DatagramBytes];

        private readonly Dictionary<ulong, FrameAssembly>
            _frames = new();

        // Late UDP packets must never resurrect a frame that was already
        // delivered or intentionally abandoned. Radmin/Internet jitter can
        // reorder a burst by hundreds of milliseconds; without this guard an
        // old fragment could create a new incomplete assembly, become the
        // "oldest" frame again and trigger another IDR/drop cycle.
        private ulong _lastDeliveredFrameId;
        private bool _hasDeliveredFrame;
        private readonly HashSet<ulong> _retiredFrameIds = new();

        private readonly VideoFrameIntervalMeter _packetIntervals = new();
        private readonly VideoFrameIntervalMeter _frameIntervals = new();
        private long _receivedFrames;
        private long _receivedPackets;
        private long _receivedBytes;
        private long _keyFrames;

        private long _sequenceGaps;
        private long _incompleteFramesDropped;
        private long _fecRecoveredFragments;
        private long _foreignPacketsDropped;

        // Rastreador de sequência com janela de reordenação. O Core 08.1
        // contava um pacote reordenado como perda e ainda movia o cursor para
        // trás, inflando a perda cumulativa e derrubando o pacer.
        private const uint SequenceReorderWindow = 128;
        private readonly HashSet<uint> _recentSequences = new();
        private uint _highestSequence;
        private uint _sequenceFinalizeCursor;
        private bool _hasSequence;

        // Cada VideoUdpSender usa um streamId aleatório. Não misturamos
        // fragmentos de um sender anterior com o stream novo depois de
        // reiniciar a transmissão.
        private readonly uint _expectedStreamId;
        private uint _activeStreamId;
        private uint _retiredStreamId;
        private bool _hasActiveStream;
        private bool _hasRetiredStream;

        // Depois de qualquer frame H.264 incompleto, não alimentamos P-frames
        // ao decoder até chegar uma nova IDR. Isso troca corrupção visual por
        // uma recuperação curta e determinística.
        private bool _requireKeyFrame = true;
        private const double RecoveryKeyFrameGraceMs = 250.0;
        private const double NormalKeyFrameGraceMs = 160.0;
        private const double DependentFrameGraceMs = 12.0;
        private long _lastOneWayMicroseconds;
        private long _totalOneWayMicroseconds;
        private long _latencySamples;
        private long _lastCleanupTimestamp;

        private string _lastError = "";

        public VideoUdpReceiver(
            IPAddress bindAddress,
            int port,
            bool measureOneWayLatency = true,
            IPAddress? expectedRemoteAddress = null, uint expectedStreamId = 0)
        {
            _bindAddress =
                bindAddress ??
                throw new ArgumentNullException(
                    nameof(bindAddress));

            _requestedPort =
                port;

            _measureOneWayLatency =
                measureOneWayLatency;

            _expectedRemoteAddress =
                expectedRemoteAddress;
            _expectedStreamId = expectedStreamId;
        }

        public int LocalPort { get; private set; }

        public bool Running =>
            _running;

        public event Action<ReassembledVideoFrame>? FrameCompleted;
        public event Action? ReferenceLost;

        public void Start()
        {
            lock (_lifecycleSync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(VideoUdpReceiver));
                if (_thread is not null)
                {
                    if (_running && !_stop) return;
                    throw new InvalidOperationException(
                        "O worker UDP anterior ainda não teve sua parada confirmada.");
                }

                _stop = false;
                _packetIntervals.Reset();
                _frameIntervals.Reset();
                _lastError = "";
                _lastCleanupTimestamp =
                    Stopwatch.GetTimestamp();

                lock (_framesSync)
                {
                    _frames.Clear();
                    _lastDeliveredFrameId = 0;
                    _hasDeliveredFrame = false;
                    _retiredFrameIds.Clear();
                    _requireKeyFrame = true;
                    _hasSequence = false;
                    _recentSequences.Clear();
                    _highestSequence = 0;
                    _sequenceFinalizeCursor = 0;
                    _hasActiveStream = false;
                    _hasRetiredStream = false;
                    _activeStreamId = 0;
                    _retiredStreamId = 0;
                }

                try
                {
                    _socket =
                        new Socket(
                            AddressFamily.InterNetwork,
                            SocketType.Dgram,
                            ProtocolType.Udp);
    
                    UdpSocketResilience
                        .ConfigureUdpSocketForTransientNetworkChanges(
                            _socket);
    
                    // Buffer gigante em streaming interativo vira fila invisível no kernel.
                    // 512 KiB absorve micro-bursts/IDR, mas impede segundos de vídeo
                    // velho aguardando ReceiveFrom após um stall do processo/rede.
                    _socket.ReceiveBufferSize =
                        512 * 1024;
    
                    _socket.ReceiveTimeout =
                        100;
    
                    _socket.Bind(
                        new IPEndPoint(
                            _bindAddress,
                            _requestedPort));
    
                    LocalPort =
                        ((IPEndPoint)
                            _socket.LocalEndPoint!)
                        .Port;
    
                    _thread =
                        new Thread(ReceiverLoop)
                        {
                            IsBackground = true,
                            Name =
                                "Multiverse Video UDP RX",
                            Priority =
                                ThreadPriority.AboveNormal
                        };
    
                    _running = true;
                    _thread.Start();
                }
                catch
                {
                    // Failure before the worker starts must release its socket.
                    _stop = true;
                    _running = false;
                    _thread = null;
                    _socket?.Dispose();
                    _socket = null;
                    LocalPort = 0;
                    throw;
                }
            }
        }

        private void ReceiverLoop()
        {
            EndPoint remote =
                new IPEndPoint(
                    IPAddress.Any,
                    0);

            try
            {
                while (!_stop)
                {
                    int bytes;

                    try
                    {
                        bytes =
                            _socket!.ReceiveFrom(
                                _receiveBuffer,
                                0,
                                _receiveBuffer.Length,
                                SocketFlags.None,
                                ref remote);
                    }
                    catch (SocketException ex)
                        when (
                            ex.SocketErrorCode ==
                                SocketError.TimedOut ||
                            ex.SocketErrorCode ==
                                SocketError.WouldBlock)
                    {
                        CleanupOldFrames();
                        DispatchReadyFrames(null);
                        continue;
                    }
                    catch (SocketException ex)
                        when (UdpSocketResilience
                            .IsTransientUdpSocketError(
                                ex.SocketErrorCode))
                    {
                        // Transient UDP RX: ignore a momentary ICMP/route/NAT
                        // failure and keep the same receiver alive. A complete
                        // frame/keyframe will naturally recover the picture.
                        _lastError =
                            $"Transient UDP RX: {ex.SocketErrorCode}";
                        VideoTransportDiagnostics.Record("CLIENT_UDP_RECEIVE_ERROR",
                            new { SocketError = (int)ex.SocketErrorCode });
                        CleanupOldFrames();
                        Thread.Sleep(4);
                        continue;
                    }

                    if (_expectedRemoteAddress is not null &&
                        remote is IPEndPoint source &&
                        !SameAddress(source.Address, _expectedRemoteAddress))
                    {
                        Interlocked.Increment(ref _foreignPacketsDropped);
                        continue;
                    }

                    if (bytes <
                        MultiverseVideoProtocol
                            .HeaderBytes)
                    {
                        continue;
                    }

                    ReadOnlySpan<byte> datagram =
                        _receiveBuffer.AsSpan(
                            0,
                            bytes);

                    if (!MultiverseVideoProtocol
                            .TryReadHeader(
                                datagram,
                                out var header))
                    {
                        continue;
                    }

                    int expectedBytes =
                        MultiverseVideoProtocol
                            .HeaderBytes +
                        header.PayloadBytes;

                    if (bytes <
                        expectedBytes)
                    {
                        continue;
                    }

                    if (!AcceptStream(header))
                    {
                        continue;
                    }

                    TrackSequence(
                        header.DatagramSequence);

                    Interlocked.Increment(
                        ref _receivedPackets);
                    _packetIntervals.Mark();

                    Interlocked.Add(
                        ref _receivedBytes,
                        bytes);

                    ReassembledVideoFrame? completed =
                        AddFragment(
                            header,
                            datagram.Slice(
                                MultiverseVideoProtocol
                                    .HeaderBytes,
                                header.PayloadBytes));

                    MaybeCleanupOldFrames();

                    // Nunca chama NVDEC enquanto segura _framesSync.
                    // O receptor precisa voltar ao ReceiveFrom o mais cedo
                    // possível para não perder datagramas em cenas pesadas.
                    DispatchReadyFrames(completed);
                }
            }
            catch (ObjectDisposedException)
            {
                // Encerramento normal.
            }
            catch (SocketException)
                when (_stop)
            {
                // Encerramento normal.
            }
            catch (Exception ex)
            {
                _lastError =
                    $"RX: {ex.Message}";
            }
            finally
            {
                _running = false;
            }
        }

        private void DispatchReadyFrames(ReassembledVideoFrame? completed)
        {
            // Drain in coded order, including after timeout cleanup releases a
            // complete recovery IDR. Never invoke decode while holding the lock.
            while (!_stop)
            {
                if (completed is null)
                {
                    lock (_framesSync) completed = TakeReadyFrameLocked();
                }
                if (completed is null) return;
                if (completed.KeyFrame)
                    VideoTransportDiagnostics.Record("CLIENT_KEYFRAME_REASSEMBLED",
                        new { StreamId = _activeStreamId, completed.FrameId, Bytes = completed.Data.Length });
                FrameCompleted?.Invoke(completed);
                completed = null;
            }
        }

        private static bool SameAddress(IPAddress left, IPAddress right)
        {
            if (left.Equals(right)) return true;
            try { return left.MapToIPv6().Equals(right.MapToIPv6()); }
            catch { return false; }
        }

        private bool AcceptStream(
            VideoDatagramHeader header)
        {
            if (!VideoGenerationIdentity.AcceptWireStream(_expectedStreamId, header.StreamId)) return false;
            lock (_framesSync)
            {
                if (!_hasActiveStream)
                {
                    _activeStreamId =
                        header.StreamId;
                    _hasActiveStream = true;
                    return true;
                }

                if (header.StreamId ==
                    _activeStreamId)
                {
                    return true;
                }

                // Datagramas atrasados do stream aposentado jamais podem
                // reabrir um GOP antigo.
                if (
                    _hasRetiredStream &&
                    header.StreamId ==
                        _retiredStreamId)
                {
                    return false;
                }

                // Um stream novo só toma posse em uma IDR e no primeiro
                // fragmento. Assim não começamos no meio de um keyframe.
                if (!header.KeyFrame ||
                    header.FragmentIndex != 0)
                {
                    return false;
                }

                _retiredStreamId =
                    _activeStreamId;
                _hasRetiredStream = true;
                _activeStreamId =
                    header.StreamId;

                _frames.Clear();
                _lastDeliveredFrameId = 0;
                _hasDeliveredFrame = false;
                _retiredFrameIds.Clear();
                _requireKeyFrame = true;
                _hasSequence = false;
                _recentSequences.Clear();
                _highestSequence = 0;
                _sequenceFinalizeCursor = 0;

                return true;
            }
        }

        private static bool IsSequenceNewer(
            uint candidate,
            uint reference)
        {
            uint distance =
                unchecked(
                    candidate - reference);

            return
                distance != 0 &&
                distance < 0x80000000u;
        }

        private void TrackSequence(
            uint sequence)
        {
            if (!_hasSequence)
            {
                _hasSequence = true;
                _highestSequence = sequence;
                _sequenceFinalizeCursor =
                    unchecked(
                        sequence - 1);
                _recentSequences.Add(
                    sequence);
                return;
            }

            // Already finalized sequences cannot change loss accounting.
            if (!IsSequenceNewer(sequence, _sequenceFinalizeCursor)) return;
            _recentSequences.Add(sequence);

            if (IsSequenceNewer(
                    sequence,
                    _highestSequence))
            {
                _highestSequence =
                    sequence;
            }

            // Finalize the interval by counting received entries, not by
            // walking every missing packet. Work stays bounded by the reorder
            // window even after a long outage or a large sequence jump.
            uint distance = unchecked(_highestSequence - _sequenceFinalizeCursor);
            if (distance > SequenceReorderWindow)
            {
                uint count = distance - SequenceReorderWindow;
                int received = _recentSequences.RemoveWhere(value =>
                    unchecked(value - _sequenceFinalizeCursor) <= count);
                Interlocked.Add(ref _sequenceGaps, (long)count - received);
                _sequenceFinalizeCursor = unchecked(_sequenceFinalizeCursor + count);
            }
        }

        private ReassembledVideoFrame? AddFragment(
            VideoDatagramHeader header,
            ReadOnlySpan<byte> payload)
        {
            lock (_framesSync)
            {
                if (
                    (_hasDeliveredFrame &&
                     header.FrameId <=
                        _lastDeliveredFrameId) ||
                    _retiredFrameIds.Contains(
                        header.FrameId))
                {
                    // Packet atrasado de frame já finalizado. Ignorar sem
                    // recriar assembly / sem contar como nova perda.
                    return TakeReadyFrameLocked();
                }

                if (!_frames.TryGetValue(
                        header.FrameId,
                        out FrameAssembly? frame))
                {
                    DropVeryOldFramesLocked();

                    frame =
                        new FrameAssembly
                        {
                            Buffer =
                                new byte[
                                    checked(
                                        (int)header.FrameBytes)],

                            Fragments =
                                new bool[
                                    header.FragmentCount],

                            FecParity =
                                new byte[
                                    (header.FragmentCount +
                                     MultiverseVideoProtocol.FecDataFragments - 1) /
                                    MultiverseVideoProtocol.FecDataFragments][],

                            FragmentCount =
                                header.FragmentCount,

                            KeyFrame =
                                header.KeyFrame,

                            SendTimestamp =
                                header.SendTimestamp
                        };

                    _frames.Add(
                        header.FrameId,
                        frame);
                }

                if (frame.FragmentCount !=
                        header.FragmentCount ||
                    frame.Buffer.Length !=
                        header.FrameBytes)
                {
                    DropFrameLocked(
                        header.FrameId);

                    return TakeReadyFrameLocked();
                }

                if (header.FecParity)
                {
                    int groupIndex =
                        header.FragmentIndex /
                        MultiverseVideoProtocol
                            .FecDataFragments;

                    if (groupIndex >= 0 &&
                        groupIndex <
                            frame.FecParity.Length)
                    {
                        frame.FecParity[groupIndex] =
                            payload.ToArray();

                        frame.LastTouchTimestamp =
                            Stopwatch.GetTimestamp();

                        TryRecoverFecGroupLocked(
                            frame,
                            groupIndex);
                    }

                    return TakeReadyFrameLocked();
                }

                if (!frame.Complete)
                {
                    int fragmentIndex =
                        header.FragmentIndex;

                    if (!frame.Fragments[
                            fragmentIndex])
                    {
                        int payloadOffset =
                            checked(
                                (int)header.PayloadOffset);

                        if (
                            payloadOffset < 0 ||
                            payloadOffset +
                                payload.Length >
                                frame.Buffer.Length)
                        {
                            DropFrameLocked(
                                header.FrameId);

                            return TakeReadyFrameLocked();
                        }

                        payload.CopyTo(
                            frame.Buffer.AsSpan(
                                payloadOffset,
                                payload.Length));

                        frame.Fragments[
                            fragmentIndex] =
                            true;

                        frame.ReceivedFragments++;
                        frame.ReceivedBytes +=
                            payload.Length;

                        frame.LastTouchTimestamp =
                            Stopwatch.GetTimestamp();

                        int groupIndex =
                            fragmentIndex /
                            MultiverseVideoProtocol
                                .FecDataFragments;

                        TryRecoverFecGroupLocked(
                            frame,
                            groupIndex);

                        if (
                            frame.ReceivedFragments ==
                                frame.FragmentCount &&
                            frame.ReceivedBytes ==
                                frame.Buffer.Length)
                        {
                            frame.Complete = true;
                        }
                    }
                }

                return TakeReadyFrameLocked();
            }
        }

        private static int ExpectedFragmentBytes(
            int frameBytes,
            int fragmentIndex)
        {
            int offset =
                fragmentIndex *
                MultiverseVideoProtocol
                    .MaxPayloadBytes;

            return Math.Max(
                0,
                Math.Min(
                    MultiverseVideoProtocol
                        .MaxPayloadBytes,
                    frameBytes - offset));
        }

        private void TryRecoverFecGroupLocked(
            FrameAssembly frame,
            int groupIndex)
        {
            if (groupIndex < 0 ||
                groupIndex >= frame.FecParity.Length)
            {
                return;
            }

            byte[]? parity =
                frame.FecParity[groupIndex];

            if (parity is null ||
                parity.Length == 0)
            {
                return;
            }

            int start =
                groupIndex *
                MultiverseVideoProtocol
                    .FecDataFragments;

            int end =
                Math.Min(
                    frame.FragmentCount,
                    start +
                        MultiverseVideoProtocol
                            .FecDataFragments);

            int missing = -1;
            int missingCount = 0;

            for (int i = start;
                 i < end;
                 i++)
            {
                if (!frame.Fragments[i])
                {
                    missing = i;
                    missingCount++;

                    if (missingCount > 1)
                        return;
                }
            }

            if (missingCount != 1 ||
                missing < 0)
            {
                return;
            }

            int expectedBytes =
                ExpectedFragmentBytes(
                    frame.Buffer.Length,
                    missing);

            if (expectedBytes <= 0 ||
                expectedBytes > parity.Length)
            {
                return;
            }

            byte[] recovered =
                (byte[])parity.Clone();

            for (int fragment = start;
                 fragment < end;
                 fragment++)
            {
                if (fragment == missing ||
                    !frame.Fragments[fragment])
                {
                    continue;
                }

                int fragmentBytes =
                    ExpectedFragmentBytes(
                        frame.Buffer.Length,
                        fragment);

                int fragmentOffset =
                    fragment *
                    MultiverseVideoProtocol
                        .MaxPayloadBytes;

                int xorBytes =
                    Math.Min(
                        recovered.Length,
                        fragmentBytes);

                for (int i = 0;
                     i < xorBytes;
                     i++)
                {
                    recovered[i] ^=
                        frame.Buffer[
                            fragmentOffset + i];
                }
            }

            int missingOffset =
                missing *
                MultiverseVideoProtocol
                    .MaxPayloadBytes;

            recovered.AsSpan(
                    0,
                    expectedBytes)
                .CopyTo(
                    frame.Buffer.AsSpan(
                        missingOffset,
                        expectedBytes));

            frame.Fragments[missing] = true;
            frame.ReceivedFragments++;
            frame.ReceivedBytes +=
                expectedBytes;

            frame.FecParity[groupIndex] = null;

            Interlocked.Increment(
                ref _fecRecoveredFragments);

            if (
                frame.ReceivedFragments ==
                    frame.FragmentCount &&
                frame.ReceivedBytes ==
                    frame.Buffer.Length)
            {
                frame.Complete = true;
            }
        }

        private ReassembledVideoFrame?
            TakeReadyFrameLocked()
        {
            while (_frames.Count > 0)
            {
                ulong oldestId =
                    ulong.MaxValue;

                FrameAssembly? oldest =
                    null;

                ulong firstCompleteId =
                    ulong.MaxValue;

                FrameAssembly? firstComplete =
                    null;

                foreach (var pair in _frames)
                {
                    if (pair.Key < oldestId)
                    {
                        oldestId =
                            pair.Key;
                        oldest =
                            pair.Value;
                    }

                    if (pair.Value.Complete &&
                        pair.Key < firstCompleteId)
                    {
                        firstCompleteId =
                            pair.Key;
                        firstComplete =
                            pair.Value;
                    }
                }

                if (oldest is null)
                    return null;

                if (!oldest.Complete)
                {
                    // Core 08.8: um P-frame incompleto nunca mais segura todos
                    // os frames seguintes por 80 ms. Esse comportamento era o
                    // gargalo que transformava ~2% de perda em ~10-12 FPS.
                    long now =
                        Stopwatch.GetTimestamp();

                    double ageMs =
                        (now - oldest.FirstTouchTimestamp) *
                        1000.0 /
                        Stopwatch.Frequency;

                    bool laterFrameReady =
                        firstComplete is not null &&
                        firstCompleteId > oldestId;

                    double graceMs = oldest.KeyFrame
                        ? (_requireKeyFrame ? RecoveryKeyFrameGraceMs : NormalKeyFrameGraceMs)
                        : DependentFrameGraceMs;

                    // Pressure alone must not invalidate the first/recovery
                    // IDR before its allowed assembly window has elapsed.
                    bool pressure =
                        _frames.Count >= 4 && ageMs >= graceMs;

                    if ((laterFrameReady &&
                         ageMs >= graceMs) ||
                        pressure)
                    {
                        DropFrameLocked(
                            oldestId);
                        continue;
                    }

                    return null;
                }

                _frames.Remove(
                    oldestId);

                _lastDeliveredFrameId =
                    oldestId;

                _hasDeliveredFrame =
                    true;

                // Entries older than the delivered floor can never be useful.
                _retiredFrameIds.RemoveWhere(
                    id =>
                        id <=
                        _lastDeliveredFrameId);

                if (
                    _requireKeyFrame &&
                    !oldest.KeyFrame)
                {
                    // CORE 11.71: uma referência incompleta torna os P-frames
                    // seguintes inseguros para decode. O receiver já pediu IDR
                    // via ReferenceLost; aguardamos esse ponto limpo de recovery.
                    continue;
                }

                if (oldest.KeyFrame)
                {
                    _requireKeyFrame =
                        false;

                    Interlocked.Increment(
                        ref _keyFrames);
                }

                Interlocked.Increment(
                    ref _receivedFrames);
                _frameIntervals.Mark();

                long receiveNow =
                    Stopwatch.GetTimestamp();

                if (
                    _measureOneWayLatency &&
                    oldest.SendTimestamp > 0 &&
                    receiveNow >= oldest.SendTimestamp)
                {
                    double seconds =
                        (receiveNow - oldest.SendTimestamp) /
                        (double)Stopwatch.Frequency;

                    long microseconds =
                        (long)(
                            seconds *
                            1_000_000.0);

                    Interlocked.Exchange(
                        ref _lastOneWayMicroseconds,
                        microseconds);

                    Interlocked.Add(
                        ref _totalOneWayMicroseconds,
                        microseconds);

                    Interlocked.Increment(
                        ref _latencySamples);
                }

                return new ReassembledVideoFrame
                {
                    Data = oldest.Buffer,
                    FrameId = oldestId,
                    KeyFrame = oldest.KeyFrame,
                    SendTimestamp = oldest.SendTimestamp,
                    ReceiveTimestamp = receiveNow
                };
            }

            return null;
        }

        private void MaybeCleanupOldFrames()
        {
            long now =
                Stopwatch.GetTimestamp();

            long previous =
                Interlocked.Read(
                    ref _lastCleanupTimestamp);

            if (
                previous > 0 &&
                (now - previous) *
                    1000.0 /
                    Stopwatch.Frequency <
                    25.0)
            {
                return;
            }

            Interlocked.Exchange(
                ref _lastCleanupTimestamp,
                now);

            CleanupOldFrames();
        }

        private void DropVeryOldFramesLocked()
        {
            // Nunca deixa a reassembly crescer indefinidamente.
            if (_frames.Count < 6)
                return;

            ulong oldest = ulong.MaxValue;
            ulong oldestDependent = ulong.MaxValue;
            foreach (var pair in _frames)
            {
                if (pair.Key < oldest)
                    oldest = pair.Key;
                if (!pair.Value.KeyFrame && pair.Key < oldestDependent)
                    oldestDependent = pair.Key;
            }

            // When waiting for a clean IDR, discard a dependent assembly
            // first rather than evicting the only possible recovery point.
            if (_requireKeyFrame && oldestDependent != ulong.MaxValue)
                oldest = oldestDependent;

            if (oldest !=
                ulong.MaxValue)
            {
                DropFrameLocked(
                    oldest);
            }
        }

        private void CleanupOldFrames()
        {
            lock (_framesSync)
            {
                if (_frames.Count == 0)
                    return;

                long now =
                    Stopwatch.GetTimestamp();

                List<ulong>? expired = null;

                foreach (var pair in _frames)
                {
                    // IDRs podem ser várias vezes maiores que P-frames.
                    // Fully assembled frames are drained in coded order, not
                    // treated as network loss because their predecessor waits.
                    if (pair.Value.Complete) continue;
                    // 100 ms no Core 08.1 matava keyframes válidas quando o
                    // pacer estava recuperando bitrate. Mantemos uma janela
                    // maior para IDR e ainda curta para P-frame.
                    double maxAgeSeconds =
                        pair.Value.KeyFrame
                            ? (_requireKeyFrame
                                ? RecoveryKeyFrameGraceMs / 1000.0
                                : NormalKeyFrameGraceMs / 1000.0)
                            : 0.025;

                    double maxAgeTicks =
                        Stopwatch.Frequency *
                        maxAgeSeconds;

                    if (now -
                            pair.Value
                                .LastTouchTimestamp >
                        maxAgeTicks)
                    {
                        expired ??=
                            new List<ulong>();

                        expired.Add(
                            pair.Key);
                    }
                }

                if (expired is not null)
                {
                    foreach (ulong id in expired)
                    {
                        DropFrameLocked(id);
                    }
                }
            }
        }

        private void DropFrameLocked(
            ulong frameId)
        {
            if (!_frames.TryGetValue(
                    frameId,
                    out FrameAssembly? dropped))
            {
                return;
            }

            if (_frames.Remove(
                    frameId))
            {
                _retiredFrameIds.Add(
                    frameId);

                // Keep a small rolling tombstone set for frames dropped ahead
                // of the delivery floor (for example a P-frame expiring while
                // a larger IDR is still completing).
                if (_retiredFrameIds.Count > 512)
                {
                    ulong minimum =
                        ulong.MaxValue;

                    foreach (ulong retiredId in
                             _retiredFrameIds)
                    {
                        if (retiredId < minimum)
                            minimum = retiredId;
                    }

                    if (minimum != ulong.MaxValue)
                        _retiredFrameIds.Remove(minimum);
                }

                Interlocked.Increment(
                    ref _incompleteFramesDropped);

                // CORE 11.71: qualquer frame H.264 incompleto invalida a
                // cadeia preditiva seguinte. Continuar entregando P-frames
                // depois de perder uma referência produz macroblocos/rasgos
                // até a próxima IDR. Preferimos uma pausa curta e limpa:
                // descartamos P-frames dependentes até chegar uma nova IDR.
                bool wasAwaitingKeyFrame = _requireKeyFrame;
                _requireKeyFrame = true;

                // Repeated dependent-frame drops while already waiting for
                // recovery should not create an unnecessary IDR request storm.
                if (!wasAwaitingKeyFrame || dropped.KeyFrame)
                {
                    try { ReferenceLost?.Invoke(); }
                    catch { }
                }
            }
        }

        public VideoUdpReceiverSnapshot
            GetSnapshot()
        {
            int pending;

            lock (_framesSync)
            {
                pending =
                    _frames.Count;
            }

            long samples =
                Interlocked.Read(
                    ref _latencySamples);

            double averageMs =
                samples > 0
                    ? Interlocked.Read(
                        ref _totalOneWayMicroseconds) /
                        1000.0 /
                        samples
                    : 0.0;

            return new VideoUdpReceiverSnapshot
            {
                Running = _running,

                ReceivedFrames =
                    Interlocked.Read(
                        ref _receivedFrames),

                ReceivedPackets =
                    Interlocked.Read(
                        ref _receivedPackets),

                ReceivedBytes =
                    Interlocked.Read(
                        ref _receivedBytes),
                PacketIntervals = _packetIntervals.GetSnapshot(),
                FrameIntervals = _frameIntervals.GetSnapshot(),

                KeyFrames =
                    Interlocked.Read(
                        ref _keyFrames),

                SequenceGaps =
                    Interlocked.Read(
                        ref _sequenceGaps),

                IncompleteFramesDropped =
                    Interlocked.Read(
                        ref _incompleteFramesDropped),

                FecRecoveredFragments =
                    Interlocked.Read(
                        ref _fecRecoveredFragments),

                ForeignPacketsDropped =
                    Interlocked.Read(
                        ref _foreignPacketsDropped),

                PendingFrames =
                    pending,

                LastOneWayMs =
                    Interlocked.Read(
                        ref _lastOneWayMicroseconds) /
                    1000.0,

                AverageOneWayMs =
                    averageMs,

                LastError =
                    _lastError
            };
        }

        public void Stop()
        {
            Thread? thread;
            lock (_lifecycleSync)
            {
                if (_disposed) return;
                _stop = true;
                try { _socket?.Close(); } catch { }
                thread = _thread;
            }

            ConfirmedThread.JoinOrThrow(thread, 750, "UDP RX");

            lock (_lifecycleSync)
            {
                if (!ReferenceEquals(_thread, thread))
                    throw new InvalidOperationException("A geração do receptor mudou durante a parada.");
                _thread = null;
                _socket?.Dispose();
                _socket = null;
                _running = false;
            }
            lock (_framesSync)
            {
                if (_frames.Count > 0)
                {
                    Interlocked.Add(ref _incompleteFramesDropped, _frames.Count);
                    _frames.Clear();
                }
            }
        }

        public void Dispose()
        {
            lock (_lifecycleSync)
            {
                if (_disposed) return;
                Stop();
                _disposed = true;
                GC.SuppressFinalize(this);
            }
        }
    }
}
