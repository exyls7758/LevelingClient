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

    internal sealed class TransportSnapshot
    {
        public bool Running { get; init; }

        public string Mode { get; init; } = "LOOPBACK";
        public string RemoteEndpoint { get; init; } = "";

        public double RoundTripMs { get; init; }
        public double JitterMs { get; init; }
        public double EstimatedPacketLossPercent { get; init; }

        public long SentFrames { get; init; }
        public long SentPackets { get; init; }
        public long SentBytes { get; init; }

        public long ReceivedFrames { get; init; }
        public long ReceivedPackets { get; init; }
        public long ReceivedBytes { get; init; }

        public long KeyFrames { get; init; }
        public bool AwaitingKeyFrame { get; init; }

        // "Sequence gaps" é um indicador de datagramas ausentes.
        // Em redes reais pode também refletir reordenação.
        public long SequenceGaps { get; init; }

        public long IncompleteFramesDropped { get; init; }
        public long FecRecoveredFragments { get; init; }
        public long ForeignPacketsDropped { get; init; }
        public int PendingFrames { get; init; }
        public long DecodeDispatchDrops { get; init; }
        public double LastPacketAgeMs { get; init; } = -1;
        public double LastFrameAgeMs { get; init; } = -1;
        public double LastPacketGapMs { get; init; }
        public double MaxPacketGapMs { get; init; }
        public double LastFrameGapMs { get; init; }
        public double MaxFrameGapMs { get; init; }
        public long FrameGapsOver100Ms { get; init; }
        public long PacketGapsOver100Ms { get; init; }

        // Telemetria do pacer/congestion control do host remoto.
        public double CurrentPacingMbps { get; init; }
        public double TargetPacingMbps { get; init; }
        public double ActualSendMbps { get; init; }
        public double EncoderTargetMbps { get; init; }
        public double PacerBacklogMs { get; init; }
        public double PacerLatenessMs { get; init; }
        public string CongestionState { get; init; } = "IDLE";
        public long FramesDroppedBeforeSend { get; init; }

        // Válido no teste local, pois TX/RX usam o mesmo relógio.
        public double LastLoopbackOneWayMs { get; init; }
        public double AverageLoopbackOneWayMs { get; init; }

        public string LastError { get; init; } = "";
    }

    internal interface IVideoTransport :
        IDisposable
    {
        bool Running { get; }

        event Action<ReassembledVideoFrame>? FrameReceived;

        void Start();
        void Stop();

        TransportSnapshot GetSnapshot();
    }

    internal sealed class LoopbackVideoTransport :
        IVideoTransport
    {
        private readonly NativeCaptureSession _capture;

        private VideoUdpReceiver? _receiver;
        private VideoUdpSender? _sender;

        private bool _disposed;

        public LoopbackVideoTransport(
            NativeCaptureSession capture)
        {
            _capture =
                capture ??
                throw new ArgumentNullException(
                    nameof(capture));
        }

        public bool Running =>
            _sender?.Running == true &&
            _receiver?.Running == true;

        public event Action<ReassembledVideoFrame>? FrameReceived;

        public void Start()
        {
            if (_disposed)
                throw new ObjectDisposedException(
                    nameof(LoopbackVideoTransport));

            if (Running)
                return;

            Stop();

            // Primeiro abre o RX para obter uma porta local válida.
            _receiver =
                new VideoUdpReceiver(
                    IPAddress.Loopback,
                    0);

            _receiver.FrameCompleted +=
                OnReceiverFrameCompleted;

            _receiver.Start();

            _sender =
                new VideoUdpSender(
                    _capture,
                    new IPEndPoint(
                        IPAddress.Loopback,
                        _receiver.LocalPort));

            _sender.Start();
        }

        public void Stop()
        {
            // Para TX antes de RX para que nenhum novo datagrama
            // chegue enquanto o receiver está encerrando.
            _sender?.Stop();
            _sender?.Dispose();
            _sender = null;

            if (_receiver is not null)
            {
                _receiver.FrameCompleted -=
                    OnReceiverFrameCompleted;

                _receiver.Stop();
                _receiver.Dispose();
                _receiver = null;
            }
        }

        private void OnReceiverFrameCompleted(
            ReassembledVideoFrame frame)
        {
            // O callback do decoder precisa ser O(1) e não bloqueante.
            // O player usa política latest-frame e não executa decode
            // diretamente na thread de rede.
            FrameReceived?.Invoke(frame);
        }

        public TransportSnapshot GetSnapshot()
        {
            VideoUdpSenderSnapshot tx =
                _sender?.GetSnapshot()
                ?? new VideoUdpSenderSnapshot();

            VideoUdpReceiverSnapshot rx =
                _receiver?.GetSnapshot()
                ?? new VideoUdpReceiverSnapshot();

            string error =
                !string.IsNullOrWhiteSpace(
                    tx.LastError)
                    ? tx.LastError
                    : rx.LastError;

            return new TransportSnapshot
            {
                Running = Running,

                Mode =
                    "LOOPBACK",

                RemoteEndpoint =
                    "127.0.0.1",

                RoundTripMs =
                    rx.AverageOneWayMs * 2.0,

                SentFrames =
                    tx.SentFrames,

                SentPackets =
                    tx.SentPackets,

                SentBytes =
                    tx.SentBytes,

                ReceivedFrames =
                    rx.ReceivedFrames,

                ReceivedPackets =
                    rx.ReceivedPackets,

                ReceivedBytes =
                    rx.ReceivedBytes,

                KeyFrames =
                    rx.KeyFrames,

                SequenceGaps =
                    rx.SequenceGaps,

                IncompleteFramesDropped =
                    rx.IncompleteFramesDropped,

                FecRecoveredFragments =
                    rx.FecRecoveredFragments,

                ForeignPacketsDropped =
                    rx.ForeignPacketsDropped,

                PendingFrames =
                    rx.PendingFrames,

                LastLoopbackOneWayMs =
                    rx.LastOneWayMs,

                AverageLoopbackOneWayMs =
                    rx.AverageOneWayMs,

                LastError = error
            };
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            Stop();
            GC.SuppressFinalize(this);
        }
    }

    internal sealed class VideoUdpSenderSnapshot
    {
        public bool Running { get; init; }
        public long SentFrames { get; init; }
        public long SentPackets { get; init; }
        public long SentBytes { get; init; }
        public VideoFrameIntervalSnapshot PacketIntervals { get; init; } = new();
        public VideoFrameIntervalSnapshot FrameIntervals { get; init; } = new();
        public long FramesDroppedBeforeSend { get; init; }
        public double CurrentPacingMbps { get; init; }
        public double TargetPacingMbps { get; init; }
        public double ActualSendMbps { get; init; }
        public double EncoderTargetMbps { get; init; }
        public double PacerBacklogMs { get; init; }
        public double PacerLatenessMs { get; init; }
        public string CongestionState { get; init; } = "IDLE";
        public string LastError { get; init; } = "";
    }

    /// <summary>
    /// TX UDP de baixa latência.
    ///
    /// Core 08.8:
    /// - pacing em micro-bursts, não um Sleep por datagrama;
    /// - espera de alta resolução com waitable timer do Windows;
    /// - UDP conectado para reduzir custo por Send;
    /// - congestion control com histerese para não oscilar por um pico isolado;
    /// - throughput REAL medido e exposto na telemetria;
    /// - nunca descarta P-frame arbitrariamente depois do NVENC.
    /// </summary>
    internal sealed class VideoUdpSender :
        IDisposable
    {
        // O bitrate escolhido pelo usuário é o bitrate de VÍDEO/NVENC.
        // A rede precisa de margem adicional para o cabeçalho MVTP e para
        // 1 fragmento FEC a cada 8 fragmentos de dados. O fator é derivado
        // do próprio protocolo em vez de usar margens mágicas 0.86/0.72.
        private const double MinRemoteEncoderRateBps =
            4_000_000.0;

        private const double ProtocolOverheadFactor =
            ((double)MultiverseVideoProtocol.DatagramBytes /
             MultiverseVideoProtocol.MaxPayloadBytes) *
            (1.0 +
             1.0 / MultiverseVideoProtocol.FecDataFragments);

        // Uma pequena folga no pacer evita que o envio fique exatamente no
        // limite teórico do stream. Ela NÃO aumenta o bitrate do NVENC.
        private const double HealthyProbeFactor =
            1.04;

        // During the first feedback window, avoid hitting a new route at the
        // full configured rate before RTT/loss/RX measurements exist. The
        // controller re-evaluates after 250 ms and can immediately probe up.
        private const double StartupPacingFactor =
            0.82;

        // Um burst por ~1,5 ms preserva o bitrate médio sem depender de
        // sleeps sub-milisegundo para cada datagrama de 1200 bytes.
        private const double TargetBurstIntervalSeconds =
            0.0015;

        private const int MaxPacketsPerBurst =
            12;

        private const double RateUpdateIntervalMs =
            250.0;

        private const long IdrRequestCooldownMs =
            750;

        private readonly NativeCaptureSession _capture;
        private ExclusivePacketConsumer<EncodedPacket>.Lease? _consumerLease;
        private readonly IPEndPoint _remote;
        private readonly uint _configuredBitrateKbps;
        private readonly Func<VideoSenderNetworkFeedback>?
            _feedbackProvider;

        private readonly bool _adaptivePacing;

        private readonly object _lifecycleSync =
            new();

        private Socket? _socket;
        private Thread? _thread;
        private HighResolutionWaiter? _waiter;

        private volatile bool _stop;
        private volatile bool _running;
        private volatile bool _disposed;

        private readonly VideoFrameIntervalMeter _packetIntervals = new();
        private readonly VideoFrameIntervalMeter _frameIntervals = new();
        private long _sentFrames;
        private long _sentPackets;
        private long _sentBytes;
        private long _framesDroppedBeforeSend;

        // Se a fila nativa do NVENC resetar por pressão, o pacote P que já
        // estava sendo produzido pode sobreviver ao clear, enquanto a IDR só
        // virá no frame seguinte. Detectamos QueueResets e aguardamos essa IDR.
        private bool _hasObservedQueueResets;
        private ulong _lastObservedQueueResets;
        private long _lastQueueResetCheckTimestamp;
        private bool _awaitingRecoveryKeyFrame;
        private long _lastLocalIdrRequestTick;

        private uint _datagramSequence;
        private readonly Func<uint>? _sequenceProvider;
        private readonly uint _streamId;

        private string _lastError = "";
        private string _congestionState = "IDLE";

        // Valores double publicados lock-free através de bits.
        private long _currentPacingRateBits;
        private long _targetPacingRateBits;
        private long _actualSendRateBits;
        private long _pacerBacklogBits;
        private long _pacerLatenessBits;

        private long _lastRateUpdateTimestamp;

        // O Core 08.8 faz o NVENC produzir aproximadamente o mesmo que a
        // rede consegue entregar. Isso evita fila nativa crescer enquanto
        // o pacer já reduziu o envio.
        private long _lastEncoderRateUpdateTick;
        private uint _appliedEncoderBitrateKbps;
        private long _encoderTargetRateBits;
        private long _lastObservedRemoteDecodeDrops;

        // Estado do micro-burst pacer. Somente a thread TX escreve.
        private long _burstWindowStartTimestamp;
        private int _burstBytesUsed;

        // Medição de throughput real da chamada Socket.Send.
        private long _throughputWindowStartTimestamp;
        private long _throughputWindowBytes;

        // Histerese do congestion controller.
        private int _goodSamples;
        private int _badSamples;
        private int _severeSamples;
        private long _lastCongestionTimestamp;

        // Um único buffer reutilizável no hot path.
        private readonly byte[] _datagram =
            new byte[
                MultiverseVideoProtocol.DatagramBytes];

        private readonly byte[] _fecParity =
            new byte[
                MultiverseVideoProtocol.MaxPayloadBytes];

        private static double ConfiguredEncoderRateBps(
            uint bitrateKbps)
        {
            return Math.Max(
                MinRemoteEncoderRateBps,
                bitrateKbps * 1000.0);
        }

        private static double ConfiguredTransportRateBps(
            uint bitrateKbps)
        {
            return
                ConfiguredEncoderRateBps(bitrateKbps) *
                ProtocolOverheadFactor;
        }

        public VideoUdpSender(
            NativeCaptureSession capture,
            IPEndPoint remote)
            : this(
                capture,
                remote,
                configuredBitrateKbps: 0,
                configuredFps: 0,
                feedbackProvider: null)
        {
        }

        public VideoUdpSender(
            NativeCaptureSession capture,
            IPEndPoint remote,
            uint configuredBitrateKbps,
            uint configuredFps,
            Func<VideoSenderNetworkFeedback>?
                feedbackProvider, uint wireStreamId = 0, Func<uint>? sequenceProvider = null)
        {
            _capture =
                capture ??
                throw new ArgumentNullException(
                    nameof(capture));

            _remote =
                remote ??
                throw new ArgumentNullException(
                    nameof(remote));

            _configuredBitrateKbps =
                configuredBitrateKbps;

            _feedbackProvider =
                feedbackProvider;

            _adaptivePacing =
                configuredBitrateKbps > 0 &&
                feedbackProvider is not null;

            _sequenceProvider = sequenceProvider;
            _streamId = wireStreamId != 0 ? wireStreamId :
                unchecked(
                    (uint)Random.Shared.Next(
                        1,
                        int.MaxValue));
        }

        public bool Running =>
            _running;

        public void Start()
        {
            lock (_lifecycleSync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(VideoUdpSender));
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
                _congestionState =
                    _adaptivePacing
                        ? "QUALITY"
                        : "LOOPBACK";

                _goodSamples = 0;
                _badSamples = 0;
                _severeSamples = 0;
                _lastCongestionTimestamp = 0;

                _hasObservedQueueResets = false;
                _lastObservedQueueResets = 0;
                _lastQueueResetCheckTimestamp = 0;
                _awaitingRecoveryKeyFrame = false;
                _lastLocalIdrRequestTick = 0;
                Interlocked.Exchange(
                    ref _framesDroppedBeforeSend,
                    0);

                long now =
                    Stopwatch.GetTimestamp();

                _lastRateUpdateTimestamp =
                    now;

                _burstWindowStartTimestamp =
                    now;

                _burstBytesUsed = 0;

                _throughputWindowStartTimestamp =
                    now;

                _throughputWindowBytes = 0;

                SetActualSendRate(0);
                SetEncoderTargetRate(
                    _configuredBitrateKbps * 1000.0);
                SetPacerBacklogMs(0);
                SetPacerLatenessMs(0);
                _lastEncoderRateUpdateTick = 0;
                _appliedEncoderBitrateKbps =
                    _configuredBitrateKbps;
                _lastObservedRemoteDecodeDrops = 0;

                double configuredTransportRateBps =
                    ConfiguredTransportRateBps(
                        _configuredBitrateKbps);

                // CORE 11.72: the very first IDR no longer hits a brand-new
                // route at 100% before any network feedback exists. This only
                // affects the first 250 ms feedback window; PROBE can then jump
                // directly to the configured rate when RTT/loss remain healthy.
                double initialRateBps =
                    Math.Max(
                        MinRemoteEncoderRateBps * ProtocolOverheadFactor,
                        configuredTransportRateBps *
                            StartupPacingFactor);

                SetCurrentPacingRate(
                    initialRateBps);

                SetTargetPacingRate(
                    _adaptivePacing
                        ? configuredTransportRateBps *
                            HealthyProbeFactor
                        : configuredTransportRateBps);

                _socket =
                    new Socket(
                        AddressFamily.InterNetwork,
                        SocketType.Dgram,
                        ProtocolType.Udp);

                UdpSocketResilience
                    .ConfigureUdpSocketForTransientNetworkChanges(
                        _socket);

                // Micro-bursts precisam de uma margem curta no kernel, mas não
                // de megabytes que esconderiam vários frames de atraso.
                _socket.SendBufferSize =
                    _adaptivePacing
                        ? 128 * 1024
                        : 4 * 1024 * 1024;

                try
                {
                    _socket.DontFragment = true;
                }
                catch
                {
                }

                // UDP conectado evita resolver/copiar EndPoint em todo Send e
                // ainda mantém o protocolo exatamente igual na rede.
                _socket.Connect(
                    _remote);

                if (_adaptivePacing)
                {
                    _waiter =
                        new HighResolutionWaiter();
                }

                _consumerLease = _capture.AcquirePacketConsumer();

                _thread =
                    new Thread(SenderLoop)
                    {
                        IsBackground = true,
                        Name =
                            "Multiverse Video UDP TX",
                        Priority =
                            ThreadPriority.AboveNormal
                    };

                _running = true;
                _thread.Start();
            }
        }

        private void SenderLoop()
        {
            var spin = new SpinWait();

            try
            {
                while (!_stop)
                {
                    var lease = _consumerLease;
                    if (lease is null || !lease.IsCurrent)
                        break;
                    EncodedPacket? frame = lease.TryRead();

                    if (frame is null ||
                        frame.Data.Length == 0)
                    {
                        // Keep feedback freshness and the encoder target live
                        // even when capture/NVENC has temporarily stopped.
                        // UpdateAdaptivePacingRate is internally rate-limited.
                        UpdateAdaptivePacingRate();
                        spin.SpinOnce();

                        if (spin.Count > 18)
                        {
                            // Sem pacote pronto, ceder ~1 ms é mais barato que
                            // hot-spin e não aumenta a latência de forma perceptível.
                            Thread.Sleep(1);
                        }
                        else if (spin.Count > 8)
                        {
                            Thread.Yield();
                        }

                        continue;
                    }

                    spin.Reset();

                    if (_stop || lease.IsCurrent == false)
                        break;
                    if (!PrepareEncodedFrameForSend(frame))
                    {
                        continue;
                    }

                    UpdateAdaptivePacingRate();

                    try
                    {
                        SendFrame(frame);
                    }
                    catch (SocketException ex)
                        when (UdpSocketResilience
                            .IsTransientUdpSocketError(
                                ex.SocketErrorCode))
                    {
                        // Transient UDP TX: route/NAT changes and a short
                        // network outage must drop only this frame, not kill
                        // the sender thread and consequently the whole session.
                        _lastError =
                            $"Transient UDP TX: {ex.SocketErrorCode}";

                        VideoTransportDiagnostics.Record("HOST_UDP_SEND_ERROR",
                            new { StreamId = _streamId, SocketError = (int)ex.SocketErrorCode });
                        _awaitingRecoveryKeyFrame = true;
                        RequestLocalIdrDebounced();
                        Thread.Sleep(8);
                        continue;
                    }
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException ex)
                when (
                    _stop ||
                    ex.SocketErrorCode ==
                        SocketError.Interrupted ||
                    ex.SocketErrorCode ==
                        SocketError.OperationAborted)
            {
            }
            catch (Exception ex)
            {
                _lastError =
                    $"TX: {ex.Message}";
            }
            finally
            {
                _running = false;
            }
        }

        private bool PrepareEncodedFrameForSend(
            EncodedPacket frame)
        {
            CheckNativeEncoderQueueReset();

            if (frame.KeyFrame)
            {
                _awaitingRecoveryKeyFrame = false;
                return true;
            }

            if (!_awaitingRecoveryKeyFrame)
            {
                return true;
            }

            // O GOP já foi invalidado por um reset confirmado da fila nativa.
            // Estes P-frames não são decodificáveis com segurança no cliente;
            // descartamos apenas até a IDR que o próprio encoder já forçou.
            Interlocked.Increment(
                ref _framesDroppedBeforeSend);

            RequestLocalIdrDebounced();
            return false;
        }

        private void CheckNativeEncoderQueueReset()
        {
            long now =
                Stopwatch.GetTimestamp();

            if (
                _lastQueueResetCheckTimestamp != 0 &&
                (now - _lastQueueResetCheckTimestamp) *
                    1000.0 /
                    Stopwatch.Frequency <
                    50.0)
            {
                return;
            }

            _lastQueueResetCheckTimestamp =
                now;

            EncoderStats? stats;

            try
            {
                stats =
                    _capture.GetEncoderStats();
            }
            catch
            {
                return;
            }

            if (stats is null)
                return;

            ulong resets =
                stats.QueueResets;

            if (!_hasObservedQueueResets)
            {
                _hasObservedQueueResets = true;
                _lastObservedQueueResets =
                    resets;
                return;
            }

            if (resets <=
                _lastObservedQueueResets)
            {
                return;
            }

            ulong previousResets = _lastObservedQueueResets;
            _lastObservedQueueResets = resets;
            VideoTransportDiagnostics.Record("HOST_QUEUE_RESET",
                new { StreamId = _streamId, Resets = resets,
                    Delta = resets - previousResets, QueuedPackets = stats.QueuedPackets,
                    InFlightFrames = stats.InFlightFrames });

            _awaitingRecoveryKeyFrame = true;
            RequestLocalIdrDebounced();
        }

        private void RequestLocalIdrDebounced()
        {
            long now =
                Environment.TickCount64;

            long previous =
                Interlocked.Read(
                    ref _lastLocalIdrRequestTick);

            if (
                previous != 0 &&
                now - previous <
                    IdrRequestCooldownMs)
            {
                return;
            }

            Interlocked.Exchange(
                ref _lastLocalIdrRequestTick,
                now);

            try
            {
                int hr = HostTransmissionService.Instance.TryRequestIdr(_capture);
                VideoTransportDiagnostics.Record("HOST_LOCAL_IDR_REQUEST",
                    new { StreamId = _streamId, HResult = hr });
            }
            catch
            {
                // O sender continua vivo; o cliente também possui o caminho
                // de solicitação de IDR pelo canal TCP de controle.
            }
        }

        private void UpdateAdaptivePacingRate()
        {
            if (!_adaptivePacing)
                return;

            long now =
                Stopwatch.GetTimestamp();

            long previous =
                Interlocked.Read(
                    ref _lastRateUpdateTimestamp);

            if (
                previous > 0 &&
                (now - previous) *
                    1000.0 /
                    Stopwatch.Frequency <
                    RateUpdateIntervalMs)
            {
                return;
            }

            Interlocked.Exchange(
                ref _lastRateUpdateTimestamp,
                now);

            VideoSenderNetworkFeedback feedback;
            try
            {
                feedback = _feedbackProvider?.Invoke() ?? default;
            }
            catch (Exception ex)
            {
                // A failed statistics callback must not kill the UDP sender.
                _lastError = "Feedback: " + ex.Message;
                feedback = default;
            }
            bool feedbackFresh = VideoFeedbackFreshness.IsFresh(feedback);

            double configuredTransportRateBps =
                ConfiguredTransportRateBps(
                    _configuredBitrateKbps);

            double healthyTargetBps =
                configuredTransportRateBps *
                HealthyProbeFactor;

            SetTargetPacingRate(
                healthyTargetBps);

            double current =
                GetCurrentPacingRate();

            if (current <= 0)
            {
                current =
                    configuredTransportRateBps;
            }

            double rtt =
                Math.Max(
                    0,
                    feedback.RoundTripMs);

            double baseRtt =
                feedback.BaseRoundTripMs > 0
                    ? feedback.BaseRoundTripMs
                    : rtt;

            double queueDelay =
                baseRtt > 0 &&
                rtt > baseRtt
                    ? rtt - baseRtt
                    : 0.0;

            double rttRatio =
                baseRtt > 0
                    ? rtt /
                        Math.Max(
                            1.0,
                            baseRtt)
                    : 1.0;

            double loss =
                Math.Max(
                    0,
                    feedback.PacketLossPercent);

            double jitter =
                Math.Max(
                    0,
                    feedback.JitterMs);

            double remoteRxMbps =
                Math.Max(
                    0,
                    feedback.RemoteRxMbps);

            double actualTxMbps =
                GetActualSendRate() /
                1_000_000.0;

            bool receiverBehind =
                actualTxMbps >= 4.0 &&
                remoteRxMbps > 0.2 &&
                remoteRxMbps <
                    actualTxMbps * 0.72;

            bool receiverQueueHigh =
                feedback.RemotePendingFrames >= 6;

            bool newDecodeDrop =
                feedback.RemoteDecodeDrops >
                    _lastObservedRemoteDecodeDrops;

            _lastObservedRemoteDecodeDrops =
                Math.Max(
                    _lastObservedRemoteDecodeDrops,
                    feedback.RemoteDecodeDrops);

            bool extreme =
                rtt >= 1000.0 ||
                queueDelay >= 700.0;

            bool severe =
                rtt >= Math.Max(
                    350.0,
                    baseRtt * 5.0) ||
                queueDelay >= 250.0 ||
                loss >= 20.0 ||
                (receiverQueueHigh && newDecodeDrop);

            bool bad =
                rtt >= Math.Max(
                    180.0,
                    baseRtt * 2.6) ||
                queueDelay >= 100.0 ||
                jitter >= 45.0 ||
                loss >= 10.0 ||
                receiverBehind ||
                receiverQueueHigh ||
                newDecodeDrop;

            bool caution =
                rtt >= Math.Max(
                    115.0,
                    baseRtt * 1.8) ||
                queueDelay >= 55.0 ||
                jitter >= 25.0 ||
                loss >= 4.0 ||
                (actualTxMbps >= 5.0 &&
                 remoteRxMbps > 0.2 &&
                 remoteRxMbps < actualTxMbps * 0.86);

            double next =
                current;

            if (!feedback.Connected)
            {
                _congestionState = "WAIT";
                _goodSamples = 0;
                _badSamples = 0;
                _severeSamples = 0;

                next =
                    Math.Min(
                        current,
                        MinRemoteEncoderRateBps *
                            ProtocolOverheadFactor);
            }
            else if (!feedbackFresh)
            {
                // Hold the last safe rate. A missing RTT or an old receiver
                // sample is not evidence that congestion has disappeared.
                // Do not repeatedly multiply the rate down while feedback is
                // missing, either: that would create an artificial FPS collapse.
                _congestionState = "STALE";
                _goodSamples = 0;
                _badSamples = 0;
                _severeSamples = 0;
                _lastCongestionTimestamp = now;
            }
            else if (extreme)
            {
                _congestionState = "RECOVERY";
                _lastCongestionTimestamp = now;
                _goodSamples = 0;
                _badSamples++;
                _severeSamples++;

                // Pico extremo: reage já, mas não cai para 20% em uma única
                // amostra como na versão anterior. Isso evitava a recuperação rápida.
                next =
                    current * 0.55;
            }
            else if (severe)
            {
                _congestionState = "BACKOFF";
                _lastCongestionTimestamp = now;
                _goodSamples = 0;
                _badSamples++;
                _severeSamples++;

                if (_severeSamples >= 1)
                {
                    next =
                        current * 0.72;
                }
            }
            else if (bad)
            {
                _congestionState = "CONGESTED";
                _lastCongestionTimestamp = now;
                _goodSamples = 0;
                _badSamples++;
                _severeSamples = 0;

                // Exige duas amostras ruins consecutivas para um backoff real.
                if (_badSamples >= 2)
                {
                    next =
                        current * 0.85;
                }
            }
            else if (caution)
            {
                _congestionState = "CAUTION";
                _lastCongestionTimestamp = now;
                _goodSamples = 0;
                _badSamples++;
                _severeSamples = 0;

                // Pequeno pico isolado não deve fazer o bitrate "caçar".
                if (_badSamples >= 3)
                {
                    next =
                        current * 0.93;
                }
            }
            else
            {
                double sinceCongestionMs =
                    _lastCongestionTimestamp > 0
                        ? (now - _lastCongestionTimestamp) *
                            1000.0 /
                            Stopwatch.Frequency
                        : double.MaxValue;

                // Depois de um pico real, segura o bitrate por 1 s antes de
                // voltar a sondar. Isso elimina o ciclo 50 ms -> pico ->
                // recuperação imediata -> novo pico observado na versão anterior.
                if (sinceCongestionMs < 1000.0)
                {
                    _congestionState = "STABILIZE";
                    _goodSamples = 0;
                    _badSamples =
                        Math.Max(
                            0,
                            _badSamples - 1);
                    _severeSamples = 0;
                }
                else
                {
                    _congestionState = "HEALTHY";
                    _goodSamples++;
                    _badSamples =
                        Math.Max(
                            0,
                            _badSamples - 1);
                    _severeSamples = 0;

                    if (_goodSamples >= 2)
                    {
                        double step =
                            Math.Max(
                                700_000.0,
                                configuredTransportRateBps *
                                    0.04);

                        // Se estamos muito abaixo do bitrate configurado,
                        // recupera rápido, mas só após o período STABILIZE.
                        if (
                            current <
                                configuredTransportRateBps * 0.75 &&
                            rttRatio < 1.35 &&
                            loss < 1.0)
                        {
                            step =
                                Math.Max(
                                    step,
                                    configuredTransportRateBps *
                                        0.06);
                        }

                        next =
                            current + step;
                    }
                }
            }

            next =
                Math.Clamp(
                    next,
                    MinRemoteEncoderRateBps *
                        ProtocolOverheadFactor,
                    healthyTargetBps);

            if (next < current * 0.90)
            {
                // Uma redução grande começa uma nova janela de burst. Nada de
                // "recuperar dívida" disparando pacotes em rajada.
                _burstWindowStartTimestamp =
                    now;
                _burstBytesUsed = 0;
            }

            SetCurrentPacingRate(
                next);

            ApplyEncoderRateIfNeeded(
                next);
        }

        private void ApplyEncoderRateIfNeeded(
            double pacingRateBps)
        {
            if (!_adaptivePacing || _configuredBitrateKbps == 0 ||
                _stop || _consumerLease?.IsCurrent != true)
            {
                return;
            }

            long now =
                Environment.TickCount64;

            if (_lastEncoderRateUpdateTick != 0 &&
                now - _lastEncoderRateUpdateTick < 700)
            {
                return;
            }

            // O pacing inclui MVTP + FEC; o bitrate do NVENC não.
            // Convertemos de volta para o orçamento de vídeo em vez de
            // reduzir duas vezes com fatores mágicos. Congestionamento já
            // reduz o pacing na máquina de estados acima.
            double targetBps =
                Math.Clamp(
                    pacingRateBps /
                    ProtocolOverheadFactor,
                    MinRemoteEncoderRateBps,
                    _configuredBitrateKbps * 1000.0);

            uint targetKbps =
                (uint)Math.Clamp(
                    Math.Round(
                        targetBps / 1000.0),
                    2000.0,
                    _configuredBitrateKbps);

            uint currentKbps =
                _appliedEncoderBitrateKbps == 0
                    ? _configuredBitrateKbps
                    : _appliedEncoderBitrateKbps;

            double delta =
                Math.Abs(
                    (double)targetKbps -
                    currentKbps) /
                Math.Max(
                    1.0,
                    currentKbps);

            if (delta < 0.06 &&
                targetKbps !=
                    _configuredBitrateKbps)
            {
                return;
            }

            _lastEncoderRateUpdateTick =
                now;

            try
            {
                int hr =
                    _capture
                        .ReconfigureEncoderBitrate(
                            targetKbps);

                if (hr >= 0)
                {
                    _appliedEncoderBitrateKbps =
                        targetKbps;

                    SetEncoderTargetRate(
                        targetKbps * 1000.0);
                }
            }
            catch
            {
                // O transporte segue vivo usando o último bitrate válido.
            }
        }

        private void SendFrame(
            EncodedPacket frame)
        {
            Socket? socket = _socket;

            if (socket is null)
                return;

            int frameBytes =
                frame.Data.Length;

            int fragmentCountInt =
                (frameBytes +
                 MultiverseVideoProtocol.MaxPayloadBytes -
                 1) /
                MultiverseVideoProtocol.MaxPayloadBytes;

            if (fragmentCountInt <= 0 ||
                fragmentCountInt >
                    ushort.MaxValue ||
                fragmentCountInt >
                    MultiverseVideoProtocol
                        .MaxFragmentsPerFrame)
            {
                return;
            }

            ushort fragmentCount =
                (ushort)fragmentCountInt;

            ushort baseFlags =
                frame.KeyFrame
                    ? MultiverseVideoProtocol
                        .FlagKeyFrame
                    : (ushort)0;

            long sendTimestamp =
                Stopwatch.GetTimestamp();

            int offset = 0;
            int fecGroupStart = 0;
            int fecGroupCount = 0;
            int fecParityBytes = 0;

            Array.Clear(
                _fecParity,
                0,
                _fecParity.Length);

            for (ushort fragmentIndex = 0;
                 fragmentIndex < fragmentCount &&
                 !_stop && _consumerLease?.IsCurrent == true;
                 fragmentIndex++)
            {
                int payloadBytes =
                    Math.Min(
                        MultiverseVideoProtocol
                            .MaxPayloadBytes,
                        frameBytes - offset);

                if (payloadBytes <= 0)
                    break;

                ReadOnlySpan<byte> payload =
                    frame.Data.AsSpan(
                        offset,
                        payloadBytes);

                SendOneDatagram(
                    socket,
                    baseFlags,
                    frame.FrameId,
                    sendTimestamp,
                    (uint)frameBytes,
                    fragmentIndex,
                    fragmentCount,
                    (uint)offset,
                    payload);

                for (int i = 0;
                     i < payloadBytes;
                     i++)
                {
                    _fecParity[i] ^=
                        payload[i];
                }

                fecParityBytes =
                    Math.Max(
                        fecParityBytes,
                        payloadBytes);

                fecGroupCount++;
                offset += payloadBytes;

                bool endOfGroup =
                    fecGroupCount >=
                        MultiverseVideoProtocol
                            .FecDataFragments ||
                    fragmentIndex + 1 ==
                        fragmentCount;

                if (endOfGroup &&
                    fecParityBytes > 0 &&
                    !_stop)
                {
                    SendOneDatagram(
                        socket,
                        (ushort)(
                            baseFlags |
                            MultiverseVideoProtocol
                                .FlagFecParity),
                        frame.FrameId,
                        sendTimestamp,
                        (uint)frameBytes,
                        (ushort)fecGroupStart,
                        fragmentCount,
                        (uint)(
                            fecGroupStart *
                            MultiverseVideoProtocol
                                .MaxPayloadBytes),
                        _fecParity.AsSpan(
                            0,
                            fecParityBytes));

                    Array.Clear(
                        _fecParity,
                        0,
                        fecParityBytes);

                    fecGroupStart =
                        fragmentIndex + 1;
                    fecGroupCount = 0;
                    fecParityBytes = 0;
                }
            }

            if (offset == frameBytes)
            {
                long sentFrameCount = Interlocked.Increment(ref _sentFrames);
                _frameIntervals.Mark();
                if (sentFrameCount == 1)
                    VideoTransportDiagnostics.Record("HOST_FIRST_FRAME_SENT",
                        new { StreamId = _streamId, frame.FrameId });
                if (frame.KeyFrame)
                    VideoTransportDiagnostics.Record("HOST_KEYFRAME_SENT",
                        new { StreamId = _streamId, frame.FrameId,
                            Bytes = frameBytes, Fragments = fragmentCount });
            }
        }

        private void SendOneDatagram(
            Socket socket,
            ushort flags,
            ulong frameId,
            long sendTimestamp,
            uint frameBytes,
            ushort fragmentIndex,
            ushort fragmentCount,
            uint payloadOffset,
            ReadOnlySpan<byte> payload)
        {
            if (payload.Length <= 0 ||
                payload.Length >
                    MultiverseVideoProtocol.MaxPayloadBytes)
            {
                return;
            }

            uint sequence = _sequenceProvider?.Invoke() ?? unchecked(++_datagramSequence);

            MultiverseVideoProtocol
                .WriteHeader(
                    _datagram.AsSpan(
                        0,
                        MultiverseVideoProtocol
                            .HeaderBytes),
                    flags,
                    _streamId,
                    frameId,
                    sendTimestamp,
                    frameBytes,
                    fragmentIndex,
                    fragmentCount,
                    payloadOffset,
                    (ushort)payload.Length,
                    sequence);

            payload.CopyTo(
                _datagram.AsSpan(
                    MultiverseVideoProtocol
                        .HeaderBytes,
                    payload.Length));

            int datagramBytes =
                MultiverseVideoProtocol
                    .HeaderBytes +
                payload.Length;

            if (_adaptivePacing)
            {
                PaceBeforeDatagram(
                    datagramBytes);
            }

            int sent =
                socket.Send(
                    _datagram,
                    0,
                    datagramBytes,
                    SocketFlags.None);

            Interlocked.Increment(
                ref _sentPackets);
            _packetIntervals.Mark();

            Interlocked.Add(
                ref _sentBytes,
                sent);

            UpdateActualSendRate(
                sent);
        }

        private void PaceBeforeDatagram(
            int datagramBytes)
        {
            double rateBps =
                GetCurrentPacingRate();

            if (rateBps <= 0)
                return;

            long now =
                Stopwatch.GetTimestamp();

            if (_burstWindowStartTimestamp <= 0)
            {
                _burstWindowStartTimestamp =
                    now;
                _burstBytesUsed = 0;
            }

            double idealPackets =
                rateBps *
                TargetBurstIntervalSeconds /
                8.0 /
                MultiverseVideoProtocol.DatagramBytes;

            int packetsPerBurst =
                Math.Clamp(
                    (int)Math.Round(
                        idealPackets),
                    1,
                    MaxPacketsPerBurst);

            int burstBudgetBytes =
                packetsPerBurst *
                MultiverseVideoProtocol.DatagramBytes;

            long burstIntervalTicks =
                Math.Max(
                    1L,
                    (long)Math.Ceiling(
                        burstBudgetBytes *
                        8.0 *
                        Stopwatch.Frequency /
                        rateBps));

            if (
                _burstBytesUsed > 0 &&
                _burstBytesUsed + datagramBytes >
                    burstBudgetBytes)
            {
                long due =
                    _burstWindowStartTimestamp +
                    burstIntervalTicks;

                if (due > now)
                {
                    _waiter?.WaitUntil(
                        due);
                }

                long after =
                    Stopwatch.GetTimestamp();

                double latenessMs =
                    after > due
                        ? (after - due) *
                            1000.0 /
                            Stopwatch.Frequency
                        : 0.0;

                double previousLateness =
                    GetPacerLatenessMs();

                SetPacerLatenessMs(
                    previousLateness <= 0
                        ? latenessMs
                        : previousLateness *
                            0.82 +
                          latenessMs *
                            0.18);

                // Se o scheduler atrasou mais de duas janelas, reancora em
                // "agora". Assim não existe catch-up burst posterior.
                _burstWindowStartTimestamp =
                    after > due +
                        burstIntervalTicks * 2
                        ? after
                        : due;

                _burstBytesUsed = 0;

                SetPacerBacklogMs(
                    latenessMs);
            }

            _burstBytesUsed +=
                datagramBytes;
        }

        private void UpdateActualSendRate(
            int sentBytes)
        {
            if (sentBytes <= 0)
                return;

            long now =
                Stopwatch.GetTimestamp();

            if (_throughputWindowStartTimestamp <= 0)
            {
                _throughputWindowStartTimestamp =
                    now;
            }

            _throughputWindowBytes +=
                sentBytes;

            double elapsedSeconds =
                (now -
                 _throughputWindowStartTimestamp) /
                (double)Stopwatch.Frequency;

            if (elapsedSeconds < 0.25)
                return;

            double bitsPerSecond =
                _throughputWindowBytes *
                8.0 /
                Math.Max(
                    0.001,
                    elapsedSeconds);

            double previous =
                GetActualSendRate();

            SetActualSendRate(
                previous <= 0
                    ? bitsPerSecond
                    : previous * 0.68 +
                      bitsPerSecond * 0.32);

            _throughputWindowBytes = 0;
            _throughputWindowStartTimestamp =
                now;
        }

        private double GetCurrentPacingRate()
        {
            return BitConverter.Int64BitsToDouble(
                Interlocked.Read(
                    ref _currentPacingRateBits));
        }

        private void SetCurrentPacingRate(
            double value)
        {
            Interlocked.Exchange(
                ref _currentPacingRateBits,
                BitConverter.DoubleToInt64Bits(
                    value));
        }

        private double GetTargetPacingRate()
        {
            return BitConverter.Int64BitsToDouble(
                Interlocked.Read(
                    ref _targetPacingRateBits));
        }

        private void SetTargetPacingRate(
            double value)
        {
            Interlocked.Exchange(
                ref _targetPacingRateBits,
                BitConverter.DoubleToInt64Bits(
                    value));
        }

        private double GetActualSendRate()
        {
            return BitConverter.Int64BitsToDouble(
                Interlocked.Read(
                    ref _actualSendRateBits));
        }

        private void SetActualSendRate(
            double value)
        {
            Interlocked.Exchange(
                ref _actualSendRateBits,
                BitConverter.DoubleToInt64Bits(
                    value));
        }

        private double GetEncoderTargetRate()
        {
            return BitConverter.Int64BitsToDouble(
                Interlocked.Read(
                    ref _encoderTargetRateBits));
        }

        private void SetEncoderTargetRate(
            double value)
        {
            Interlocked.Exchange(
                ref _encoderTargetRateBits,
                BitConverter.DoubleToInt64Bits(
                    value));
        }

        private double GetPacerBacklogMs()
        {
            return BitConverter.Int64BitsToDouble(
                Interlocked.Read(
                    ref _pacerBacklogBits));
        }

        private void SetPacerBacklogMs(
            double value)
        {
            Interlocked.Exchange(
                ref _pacerBacklogBits,
                BitConverter.DoubleToInt64Bits(
                    value));
        }

        private double GetPacerLatenessMs()
        {
            return BitConverter.Int64BitsToDouble(
                Interlocked.Read(
                    ref _pacerLatenessBits));
        }

        private void SetPacerLatenessMs(
            double value)
        {
            Interlocked.Exchange(
                ref _pacerLatenessBits,
                BitConverter.DoubleToInt64Bits(
                    value));
        }

        public VideoUdpSenderSnapshot
            GetSnapshot()
        {
            return new VideoUdpSenderSnapshot
            {
                Running = _running,

                SentFrames =
                    Interlocked.Read(
                        ref _sentFrames),

                SentPackets =
                    Interlocked.Read(
                        ref _sentPackets),

                SentBytes =
                    Interlocked.Read(
                        ref _sentBytes),
                PacketIntervals = _packetIntervals.GetSnapshot(),
                FrameIntervals = _frameIntervals.GetSnapshot(),

                // Só cresce quando detectamos um GOP já quebrado por reset da
                // fila nativa e descartamos P-frames até a IDR de recuperação.
                FramesDroppedBeforeSend =
                    Interlocked.Read(
                        ref _framesDroppedBeforeSend),

                CurrentPacingMbps =
                    GetCurrentPacingRate() /
                    1_000_000.0,

                TargetPacingMbps =
                    GetTargetPacingRate() /
                    1_000_000.0,

                ActualSendMbps =
                    GetActualSendRate() /
                    1_000_000.0,

                EncoderTargetMbps =
                    GetEncoderTargetRate() /
                    1_000_000.0,

                PacerBacklogMs =
                    GetPacerBacklogMs(),

                PacerLatenessMs =
                    GetPacerLatenessMs(),

                CongestionState =
                    _congestionState,

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
                // Closing the socket interrupts a blocking send. The packet
                // consumer and pacing timer remain owned until the thread exits.
                try { _socket?.Close(); } catch { }
                thread = _thread;
            }

            ConfirmedThread.JoinOrThrow(thread, 1500, "UDP TX");

            lock (_lifecycleSync)
            {
                // Only the owner of this exact worker may clear its resources.
                if (!ReferenceEquals(_thread, thread))
                    throw new InvalidOperationException("A geração do sender mudou durante a parada.");
                _thread = null;
                _waiter?.Dispose();
                _waiter = null;
                _socket?.Dispose();
                _socket = null;
                _consumerLease?.Dispose();
                _consumerLease = null;
                _running = false;
                _congestionState = "IDLE";
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

        /// <summary>
        /// Waitable timer de alta resolução (Windows 10+). Se a flag de alta
        /// resolução não estiver disponível, cai para um timer normal e termina
        /// os últimos microssegundos com Yield/Spin.
        /// </summary>
        private sealed class HighResolutionWaiter :
            IDisposable
        {
            private const uint CreateWaitableTimerHighResolution =
                0x00000002;

            // SYNCHRONIZE | TIMER_MODIFY_STATE. Evita pedir permissões
            // desnecessárias ao criar o timer do pacer.
            private const uint TimerDesiredAccess =
                0x00100002;

            private const uint Infinite =
                0xFFFFFFFF;

            private IntPtr _timer;

            public HighResolutionWaiter()
            {
                _timer =
                    CreateWaitableTimerExW(
                        IntPtr.Zero,
                        null,
                        CreateWaitableTimerHighResolution,
                        TimerDesiredAccess);

                if (_timer == IntPtr.Zero)
                {
                    _timer =
                        CreateWaitableTimerExW(
                            IntPtr.Zero,
                            null,
                            0,
                            TimerDesiredAccess);
                }
            }

            public void WaitUntil(
                long dueTimestamp)
            {
                while (true)
                {
                    long now =
                        Stopwatch.GetTimestamp();

                    long remaining =
                        dueTimestamp - now;

                    if (remaining <= 0)
                        return;

                    double remainingMs =
                        remaining *
                        1000.0 /
                        Stopwatch.Frequency;

                    if (
                        _timer != IntPtr.Zero &&
                        remainingMs >= 0.45)
                    {
                        // Acorda ~0,12 ms antes e finaliza com spin/yield.
                        double waitMs =
                            Math.Max(
                                0.10,
                                remainingMs - 0.12);

                        long due100Ns =
                            -Math.Max(
                                1L,
                                (long)Math.Round(
                                    waitMs *
                                    10_000.0));

                        if (SetWaitableTimer(
                                _timer,
                                ref due100Ns,
                                0,
                                IntPtr.Zero,
                                IntPtr.Zero,
                                false))
                        {
                            WaitForSingleObject(
                                _timer,
                                Infinite);
                            continue;
                        }
                    }

                    if (remainingMs >= 0.12)
                    {
                        Thread.Yield();
                    }
                    else
                    {
                        Thread.SpinWait(32);
                    }
                }
            }

            public void Dispose()
            {
                IntPtr timer =
                    Interlocked.Exchange(
                        ref _timer,
                        IntPtr.Zero);

                if (timer != IntPtr.Zero)
                {
                    CloseHandle(
                        timer);
                }
            }

            [System.Runtime.InteropServices.DllImport(
                "kernel32.dll",
                CharSet = System.Runtime.InteropServices.CharSet.Unicode,
                SetLastError = true)]
            private static extern IntPtr
                CreateWaitableTimerExW(
                    IntPtr lpTimerAttributes,
                    string? lpTimerName,
                    uint dwFlags,
                    uint dwDesiredAccess);

            [System.Runtime.InteropServices.DllImport(
                "kernel32.dll",
                SetLastError = true)]
            [return: System.Runtime.InteropServices.MarshalAs(
                System.Runtime.InteropServices.UnmanagedType.Bool)]
            private static extern bool SetWaitableTimer(
                IntPtr hTimer,
                ref long lpDueTime,
                int lPeriod,
                IntPtr pfnCompletionRoutine,
                IntPtr lpArgToCompletionRoutine,
                [System.Runtime.InteropServices.MarshalAs(
                    System.Runtime.InteropServices.UnmanagedType.Bool)]
                bool fResume);

            [System.Runtime.InteropServices.DllImport(
                "kernel32.dll",
                SetLastError = true)]
            private static extern uint WaitForSingleObject(
                IntPtr hHandle,
                uint dwMilliseconds);

            [System.Runtime.InteropServices.DllImport(
                "kernel32.dll",
                SetLastError = true)]
            [return: System.Runtime.InteropServices.MarshalAs(
                System.Runtime.InteropServices.UnmanagedType.Bool)]
            private static extern bool CloseHandle(
                IntPtr hObject);
        }
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
