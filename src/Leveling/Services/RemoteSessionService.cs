using System;
using System.Collections.Generic;
using System.Threading.Channels;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Multiverse.Models;

namespace Multiverse.Services
{
    internal enum RemoteSessionRole
    {
        None = 0,
        Host = 1,
        Client = 2
    }

    internal sealed class RemoteStreamConfig
    {
        public uint Width { get; init; }
        public uint Height { get; init; }
        public uint Fps { get; init; }
        public uint BitrateKbps { get; init; }
        public long Generation { get; init; }
        public uint WireStreamId { get; init; }

        public override string ToString() =>
            $"{Width}x{Height} @ {Fps} FPS • {BitrateKbps / 1000.0:0.#} Mbps";
    }

    internal sealed class RemoteConnectResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = "";
    }

    internal sealed class RemoteGamepadPacket
    {
        public bool Connected { get; init; }
        public int UserIndex { get; init; } = -1;
        public uint PacketNumber { get; init; }
        public ushort Buttons { get; init; }
        public byte LeftTrigger { get; init; }
        public byte RightTrigger { get; init; }
        public short LeftThumbX { get; init; }
        public short LeftThumbY { get; init; }
        public short RightThumbX { get; init; }
        public short RightThumbY { get; init; }
        public long Sequence { get; init; }
    }

    internal sealed class RemoteSessionSnapshot
    {
        public bool Listening { get; init; }
        public bool Connected { get; init; }
        public int SessionGeneration { get; init; }
        public RemoteSessionRole Role { get; init; }

        public string LocalCode { get; init; } = "";
        public string LocalName { get; init; } = "";
        public string LocalDirectAddress { get; init; } = "";
        public string CurrentTailscaleIp { get; init; } = "";
        public string TailscaleInterface { get; init; } = "";
        public long NetworkGeneration { get; init; }

        public string RemoteCode { get; init; } = "";
        public string RemoteName { get; init; } = "";
        public string RemoteAddress { get; init; } = "";
        public string RemoteFriendDeviceId { get; init; } = "";
        public bool RemoteInputAllowed { get; init; } = true;
        public bool RemoteAudioAllowed { get; init; } = true;

        public double RoundTripMs { get; init; }
        public double BaseRoundTripMs { get; init; }
        public double QueueDelayMs { get; init; }
        public double JitterMs { get; init; }

        public double RemotePacketLossPercent { get; init; }
        public double RemoteVideoFps { get; init; }
        public double RemoteRxMbps { get; init; }
        public long RemoteTelemetryAgeMs { get; init; } = -1;
        public int RemotePendingFrames { get; init; }
        public long RemoteDecodeDrops { get; init; }
        public long RemoteDecodeStaleDrops { get; init; }
        public long RemoteReferenceWaitDrops { get; init; }
        public long RemotePresenterDrops { get; init; }
        public long RemotePresentedFrames { get; init; }
        public long RemoteCompleteFrames { get; init; }
        public long RemoteDecodedFrames { get; init; }
        public double RemoteLastPresentAgeMs { get; init; } = -1;

        public bool RemoteCursorVisible { get; init; }
        public double RemoteCursorX { get; init; }
        public double RemoteCursorY { get; init; }

        public bool RemoteGamepadConnected { get; init; }
        public long RemoteGamepadPackets { get; init; }
        public double RemoteGamepadAgeMs { get; init; }

        public RemoteStreamConfig? StreamConfig { get; init; }

        public string LastError { get; init; } = "";
        public string VideoStartupError { get; init; } = "";
        public string StatusText { get; init; } = "";
    }

    /// <summary>
    /// Core 08 control plane.
    ///
    /// This stage deliberately separates:
    /// - control/session: TCP 47820
    /// - H.264 video: UDP 47821
    /// - LAN discovery: UDP 47822
    ///
    /// A 9-digit per-process pairing code is used to accept a connection.
    /// Code-only lookup works on the same LAN through discovery.
    /// Direct/VPN/public-forwarded mode accepts:
    ///     IP:47820#123456789
    ///
    /// The video transport itself remains the already validated MVTP/UDP path.
    /// </summary>
    internal sealed partial class RemoteSessionService :
        IDisposable
    {
        private sealed class ControlEnvelope
        {
            public DesktopInputPacket? DesktopInput { get; set; }
            public int Protocol { get; set; } = RemoteSessionService.ProtocolVersion;
            public string Type { get; set; } = "";

            public string TargetCode { get; set; } = "";
            public string SourceCode { get; set; } = "";
            public string SourceName { get; set; } = "";

            public int UdpPort { get; set; }

            public long Sequence { get; set; }
            public long Timestamp { get; set; }
            public long RecoveryRequestId { get; set; }
            public uint WireStreamId { get; set; }

            public uint Width { get; set; }
            public uint Height { get; set; }
            public uint Fps { get; set; }
            public uint BitrateKbps { get; set; }

            public long ReceivedPackets { get; set; }
            public long ReceivedBytes { get; set; }
            public long SequenceGaps { get; set; }
            public long IncompleteFrames { get; set; }
            public int PendingFrames { get; set; }
            public long DecodeDrops { get; set; }
            public long DecodeStaleDrops { get; set; }
            public long ReferenceWaitDrops { get; set; }
            public long PresenterDrops { get; set; }
            public double VideoFps { get; set; }
            public long PresentedFrames { get; set; }
            public long CompleteFrames { get; set; }
            public long DecodedFrames { get; set; }
            public double LastPresentAgeMs { get; set; } = -1;

            // Cursor normalizado no monitor transmitido (0..1).
            public bool CursorVisible { get; set; }
            public double CursorX { get; set; }
            public double CursorY { get; set; }

            // CORE 11.18 INPUT: estado XInput compacto, latest-wins.
            public bool GamepadConnected { get; set; }
            public int GamepadUserIndex { get; set; } = -1;
            public uint GamepadPacketNumber { get; set; }
            public ushort GamepadButtons { get; set; }
            public byte GamepadLeftTrigger { get; set; }
            public byte GamepadRightTrigger { get; set; }
            public short GamepadLeftThumbX { get; set; }
            public short GamepadLeftThumbY { get; set; }
            public short GamepadRightThumbX { get; set; }
            public short GamepadRightThumbY { get; set; }

            // CORE 12 FRIENDS: pre-session trust/authentication metadata.
            public string DeviceId { get; set; } = "";
            public string PublicKeySpki { get; set; } = "";
            public string Nonce { get; set; } = "";
            public string Signature { get; set; } = "";
            public string RequestId { get; set; } = "";

            public string Message { get; set; } = "";
        }

        private sealed class DiscoveredPeer
        {
            public required string Code { get; init; }
            public required string Name { get; init; }
            public required IPAddress Address { get; init; }
            public required int ControlPort { get; init; }
            public long LastSeenTick { get; set; }
        }

        private sealed class PendingFriendRequest
        {
            public required FriendRequest Request { get; init; }
            public required TaskCompletionSource<bool> Decision { get; init; }
        }

        private sealed class PendingDirectSessionRequest
        {
            public required DirectSessionRequest Request { get; init; }
            public required TaskCompletionSource<bool> Decision { get; init; }
        }

        private sealed class PendingSenderRecovery
        {
            public required int SessionGeneration { get; init; }
            public required long StreamGeneration { get; init; }
            public required TaskCompletionSource<bool> Completion { get; init; }
        }

        private sealed class PairingAttemptState
        {
            public long WindowStartedAt { get; set; }
            public int Failures { get; set; }
            public long BlockedUntil { get; set; }
        }

        private static readonly Lazy<RemoteSessionService>
            LazyInstance =
                new(() => new RemoteSessionService());

        public static RemoteSessionService Instance =>
            LazyInstance.Value;

        public const int ProtocolVersion = 20;
        public const int ControlPort = 47820;
        public const int VideoPort = 47821;
        public const int DiscoveryPort = 47822;

        private const long DiscoveryPeerExpiryMs = 5000;
        private const int DiscoveryBroadcastIntervalMs = 1000;
        private const int KeepAliveIntervalMs = 1000;
        // Jogos podem bloquear momentaneamente scheduling durante loading/alt-tab.
        // 30 s evita desconexão falsa sem esconder uma queda real por minutos.
        private const long KeepAliveTimeoutMs = 30000;
        private const int MaxControlLineChars = 16 * 1024;
        private const int CriticalControlQueueCapacity = 128;
        private const int PairingFailureLimit = 5;
        private const long PairingFailureWindowMs = 60_000;
        private const long PairingBlockMs = 30_000;

        private readonly object _sync = new();
        private readonly object _discoverySync = new();
        private readonly object _pairingSync = new();
        private readonly Dictionary<string, PairingAttemptState> _pairingAttempts = new(StringComparer.Ordinal);

        private readonly Dictionary<string, DiscoveredPeer>
            _discoveredPeers =
                new(StringComparer.Ordinal);

        private readonly object _friendSync = new();
        private readonly Dictionary<string, PendingFriendRequest>
            _pendingFriendRequests = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PendingDirectSessionRequest>
            _pendingDirectSessionRequests = new(StringComparer.Ordinal);
        private readonly FriendStoreService _friendStore = FriendStoreService.Instance;
        private readonly MultiverseIdentityService _identity = MultiverseIdentityService.Instance;

        private Channel<ControlEnvelope>? _controlOutbound;
        private Channel<ControlEnvelope>? _telemetryOutbound;
        private Channel<ControlEnvelope>? _gamepadOutbound;
        private Channel<ControlEnvelope>? _cursorOutbound;
        private SemaphoreSlim? _controlOutboundSignal;

        private CancellationTokenSource? _lifetimeCancel;
        private TcpListener? _listener;

        private UdpClient? _discoveryReceiver;
        private UdpClient? _discoverySender;

        private TcpClient? _activeTcp;
        private BoundedLineReader? _activeReader;
        private StreamWriter? _activeWriter;
        private CancellationTokenSource? _sessionCancel;

        private int _sessionGeneration;
        private bool _started;
        private bool _disposed;

        private RemoteSessionRole _role =
            RemoteSessionRole.None;

        private string _remoteCode = "";
        private string _remoteName = "";
        private string _remoteAddress = "";
        private string _remoteFriendDeviceId = "";
        private bool _remoteInputAllowed = true;
        private bool _remoteAudioAllowed = true;

        private IPEndPoint? _remoteVideoEndpoint;

        private RemoteStreamConfig? _streamConfig;
        private long _localVideoStreamGeneration;
        private long _expectedRemoteVideoReadyGeneration;
        private bool _remoteVideoReady;
        private long _videoStartupErrorGeneration;
        private string _videoStartupError = "";
        private string _lastVideoStartupError = "";

        private long _lastControlMessageAt;
        private long _controlReadMessages;
        private long _controlWriteMessages;
        private long _controlReadBytes;
        private long _controlWriteBytes;
        private long _controlLastReadAt;
        private long _controlLastWriteAt;
        private long _controlLastPongAt;
        private long _controlReadStartedAt;
        private long _controlWriteStartedAt;
        private long _controlLastWriteDurationUs;
        private long _controlMaxWriteDurationUs;
        private long _pingSequence;
        private double _rttMs;
        private double _baselineRttMs;
        private double _jitterMs;
        private double _lastRttSampleMs;

        // Telemetria de vídeo recebida do cliente.
        // Os contadores enviados pelo cliente são cumulativos, mas o pacer
        // precisa reagir à perda RECENTE. Guardamos a amostra anterior e
        // calculamos deltas por janela; assim uma perda antiga não deixa o
        // bitrate preso no mínimo para sempre.
        private double _remotePacketLossPercent;
        private double _remoteVideoFps;
        private double _remoteRxMbps;
        private int _remotePendingFrames;
        private long _remoteDecodeDrops;
        private long _remoteDecodeStaleDrops;
        private long _remoteReferenceWaitDrops;
        private long _remotePresenterDrops;
        private int _outboundVideoPacketSequence;
        internal uint NextVideoPacketSequence() => unchecked((uint)Interlocked.Increment(ref _outboundVideoPacketSequence));
        private long _remotePresentedFrames, _remoteCompleteFrames, _remoteDecodedFrames;
        private double _remoteLastPresentAgeMs = -1;
        private readonly Dictionary<long, PendingSenderRecovery> _pendingSenderRecoveries = new();

        private bool _hasRemoteTransportCounters;
        private long _lastRemoteReceivedPackets;
        private long _lastRemoteReceivedBytes;
        private long _lastRemoteSequenceGaps;
        private long _lastRemoteIncompleteFrames;
        private long _lastRemoteStatsTick;

        private bool _remoteCursorVisible;
        private double _remoteCursorX;
        private double _remoteCursorY;

        private bool _remoteGamepadConnected;
        private long _remoteGamepadPackets;
        private long _lastRemoteGamepadAt;

        private string _lastError = "";
        private string _statusText =
            "Aguardando conexão.";

        private readonly string _deviceName;
        private readonly string _deviceCode;
        private readonly TailscaleNetworkService _tailscale =
            TailscaleNetworkService.Instance;
        private readonly MultiverseSettingsService _settings =
            MultiverseSettingsService.Instance;

        private void Tailscale_NetworkChanged(object? sender, EventArgs e)
        {
            // Address changes invalidate the old direct endpoint immediately.
            // GetSnapshot rebuilds it from the latest network generation.
            RaiseSessionChanged();
        }

        public event EventHandler? SessionChanged;
        public event EventHandler? StreamConfigChanged;
        public event EventHandler? StreamStopped;
        public event Action? RemoteIdrRequested;
        public event Action<int, long, long, long>? RemoteSenderRecoveryRequested;
        public event Action<RemoteGamepadPacket>? RemoteGamepadReceived;
        public event Action<FriendRequest>? FriendRequestReceived;
        public event Action<DirectSessionRequest>? DirectSessionRequestReceived;

        private RemoteSessionService()
        {
            _deviceName =
                Environment.MachineName
                    .ToUpperInvariant();

            int code =
                RandomNumberGenerator.GetInt32(
                    0,
                    1_000_000_000);

            string digits =
                code.ToString(
                    "000000000",
                    CultureInfo.InvariantCulture);

            _deviceCode =
                $"{digits[..3]} " +
                $"{digits.Substring(3, 3)} " +
                $"{digits.Substring(6, 3)}";

            _tailscale.NetworkChanged +=
                Tailscale_NetworkChanged;
            _friendStore.FriendsChanged += FriendStore_FriendsChanged;
        }

        public string DeviceName =>
            _deviceName;

        public string DeviceCode =>
            _deviceCode;

        public int LocalVideoPort =>
            VideoPort;

        private void FriendStore_FriendsChanged(object? sender, EventArgs e)
        {
            lock (_sync)
            {
                if (string.IsNullOrWhiteSpace(_remoteFriendDeviceId))
                    return;
                MultiverseFriend? friend = _friendStore.FindByDeviceId(_remoteFriendDeviceId);
                if (friend is null || friend.Blocked)
                {
                    _remoteInputAllowed = false;
                    _remoteAudioAllowed = false;
                }
                else
                {
                    _remoteInputAllowed = friend.AllowRemoteInput;
                    _remoteAudioAllowed = friend.AllowRemoteAudio;
                }
            }
            RaiseSessionChanged();
        }

        public void Start()
        {
            lock (_sync)
            {
                if (_disposed)
                    throw new ObjectDisposedException(
                        nameof(RemoteSessionService));

                if (_started)
                    return;

                _lifetimeCancel =
                    new CancellationTokenSource();

                CancellationToken token =
                    _lifetimeCancel.Token;

                try
                {
                    _listener =
                        new TcpListener(
                            IPAddress.Any,
                            ControlPort);

                    _listener.Start(
                        backlog: 4);

                    _started = true;
                    _lastError = "";
                    _statusText =
                        $"Pronto para receber • TCP {ControlPort} / UDP {VideoPort}";

                    _ = Task.Run(
                        () => AcceptLoopAsync(
                            token));

                    StartDiscoveryLocked(
                        token);
                }
                catch (Exception ex)
                {
                    _started = false;
                    _lastError =
                        ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
                            ? "Porta ocupada. Feche as outras instâncias do Leveling, inclusive na bandeja, e reabra esta versão."
                            : $"Falha ao abrir a conexão: {ex.Message}";
                    _statusText =
                        "Listener indisponível.";

                    try
                    {
                        _listener?.Stop();
                    }
                    catch
                    {
                    }

                    _listener = null;
                }
            }

            RaiseSessionChanged();
        }

        private void StartDiscoveryLocked(
            CancellationToken token)
        {
            try
            {
                _discoveryReceiver =
                    new UdpClient(
                        AddressFamily.InterNetwork);

                _discoveryReceiver.Client.ExclusiveAddressUse =
                    false;

                _discoveryReceiver.Client
                    .SetSocketOption(
                        SocketOptionLevel.Socket,
                        SocketOptionName.ReuseAddress,
                        true);

                _discoveryReceiver.Client.Bind(
                    new IPEndPoint(
                        IPAddress.Any,
                        DiscoveryPort));

                _discoverySender =
                    new UdpClient(
                        AddressFamily.InterNetwork)
                    {
                        EnableBroadcast = true
                    };

                _ = Task.Run(
                    () => DiscoveryReceiveLoopAsync(
                        token));

                _ = Task.Run(
                    () => DiscoveryBroadcastLoopAsync(
                        token));
            }
            catch (Exception ex)
            {
                // Discovery is convenience only. Direct IP still works.
                _lastError =
                    $"Descoberta LAN indisponível: {ex.Message}";

                _discoveryReceiver?.Dispose();
                _discoveryReceiver = null;

                _discoverySender?.Dispose();
                _discoverySender = null;
            }
        }

        private async Task AcceptLoopAsync(
            CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client;

                try
                {
                    client =
                        await _listener!
                            .AcceptTcpClientAsync(
                                token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    lock (_sync)
                    {
                        _lastError =
                            $"Listener: {ex.Message}";
                    }

                    RaiseSessionChanged();

                    try
                    {
                        await Task.Delay(
                            250,
                            token);
                    }
                    catch
                    {
                    }

                    continue;
                }

                _ =
                    HandleIncomingAsync(
                        client,
                        token);
            }
        }

        private async Task HandleIncomingAsync(
            TcpClient client,
            CancellationToken lifetimeToken)
        {
            try
            {
                client.NoDelay = true;
                client.SendBufferSize = 64 * 1024;
                client.ReceiveBufferSize = 64 * 1024;

                using CancellationTokenSource handshakeTimeout =
                    CancellationTokenSource
                        .CreateLinkedTokenSource(
                            lifetimeToken);

                handshakeTimeout.CancelAfter(
                    TimeSpan.FromSeconds(5));

                NetworkStream stream =
                    client.GetStream();

                BoundedLineReader reader =
                    new(
                        new StreamReader(
                            stream,
                            Encoding.UTF8,
                            detectEncodingFromByteOrderMarks: false,
                            bufferSize: 4096,
                            leaveOpen: true),
                        MaxControlLineChars);

                StreamWriter writer =
                    new(
                        stream,
                        new UTF8Encoding(
                            encoderShouldEmitUTF8Identifier: false),
                        bufferSize: 4096,
                        leaveOpen: true)
                    {
                        AutoFlush = true
                    };

                string? line =
                    await reader.ReadLineAsync(
                        handshakeTimeout.Token);

                ControlEnvelope? hello =
                    Deserialize(
                        line);

                if (hello is null || hello.Protocol != ProtocolVersion)
                {
                    await WriteEnvelopeDirectAsync(
                        writer,
                        new ControlEnvelope
                        {
                            Type = "reject",
                            Message = "Handshake Leveling inválido."
                        });
                    client.Dispose();
                    return;
                }

                if (string.Equals(hello.Type, "friend_request", StringComparison.Ordinal))
                {
                    await HandleIncomingFriendRequestAsync(
                        client, reader, writer, hello, lifetimeToken);
                    return;
                }

                if (string.Equals(hello.Type, "friend_hello", StringComparison.Ordinal))
                {
                    await HandleIncomingFriendHelloAsync(
                        client, reader, writer, hello, lifetimeToken);
                    return;
                }

                if (!string.Equals(hello.Type, "hello", StringComparison.Ordinal))
                {
                    await WriteEnvelopeDirectAsync(
                        writer,
                        new ControlEnvelope
                        {
                            Type = "reject",
                            Message = "Tipo de handshake desconhecido."
                        });
                    client.Dispose();
                    return;
                }

                IPEndPoint remoteTcp =
                    (IPEndPoint)client.Client.RemoteEndPoint!;

                if (IsPairingThrottled(remoteTcp.Address))
                {
                    await WriteEnvelopeDirectAsync(
                        writer,
                        new ControlEnvelope
                        {
                            Type = "reject",
                            Message = "Muitas tentativas de pareamento. Aguarde alguns segundos."
                        });
                    client.Dispose();
                    return;
                }

                string targetCode =
                    NormalizeCode(
                        hello.TargetCode);

                if (!string.Equals(
                        targetCode,
                        NormalizeCode(
                            DeviceCode),
                        StringComparison.Ordinal))
                {
                    RegisterPairingFailure(remoteTcp.Address);
                    await WriteEnvelopeDirectAsync(
                        writer,
                        new ControlEnvelope
                        {
                            Type = "reject",
                            Message =
                                "Código de pareamento incorreto."
                        });

                    client.Dispose();
                    return;
                }

                ResetPairingFailures(remoteTcp.Address);

                lock (_sync)
                {
                    if (_activeTcp is not null)
                    {
                        // One private session at a time in Core 08.
                        // We answer below outside the lock.
                    }
                }

                bool busy;

                lock (_sync)
                {
                    busy =
                        _activeTcp is not null;
                }

                if (busy)
                {
                    await WriteEnvelopeDirectAsync(
                        writer,
                        new ControlEnvelope
                        {
                            Type = "reject",
                            Message =
                                "Este computador já está em uma sessão."
                        });

                    client.Dispose();
                    return;
                }

                int remoteUdpPort =
                    IsValidPort(
                        hello.UdpPort)
                        ? hello.UdpPort
                        : VideoPort;

                int generation =
                    ActivateSession(
                        client,
                        reader,
                        writer,
                        RemoteSessionRole.Host,
                        hello.SourceCode,
                        hello.SourceName,
                        new IPEndPoint(
                            remoteTcp.Address,
                            remoteUdpPort));

                SavePeerProfile(hello.SourceCode, hello.SourceName);

                await SendAsync(
                    new ControlEnvelope
                    {
                        Type = "accept",
                        SourceCode = DeviceCode,
                        SourceName = DeviceName,
                        UdpPort = VideoPort,
                        Message =
                            "Sessão Core 08 aceita."
                    });

                CancellationToken sessionToken =
                    _sessionCancel!.Token;

                _ = Task.Run(
                    () => ControlLoopAsync(
                        generation,
                        sessionToken));

                _ = Task.Run(
                    () => KeepAliveLoopAsync(
                        generation,
                        sessionToken));

                RaiseSessionChanged();
            }
            catch (OperationCanceledException)
            {
                client.Dispose();
            }
            catch (Exception ex)
            {
                client.Dispose();

                lock (_sync)
                {
                    _lastError =
                        $"Entrada Core 08: {ex.Message}";
                }

                RaiseSessionChanged();
            }
        }

        public IReadOnlyList<FriendRequest> GetPendingFriendRequests()
        {
            lock (_friendSync)
            {
                return _pendingFriendRequests.Values
                    .Select(p => p.Request)
                    .OrderBy(r => r.ReceivedAtUtc)
                    .ToArray();
            }
        }

        public IReadOnlyList<DirectSessionRequest> GetPendingDirectSessionRequests()
        {
            lock (_friendSync)
            {
                return _pendingDirectSessionRequests.Values
                    .Select(x => x.Request)
                    .OrderBy(x => x.ReceivedAtUtc)
                    .ToArray();
            }
        }

        public bool RespondToDirectSessionRequest(string requestId, bool accept)
        {
            PendingDirectSessionRequest? pending;
            lock (_friendSync)
                _pendingDirectSessionRequests.TryGetValue(requestId ?? "", out pending);
            return pending is not null && pending.Decision.TrySetResult(accept);
        }

        public bool RespondToFriendRequest(string requestId, bool accept)
        {
            PendingFriendRequest? pending;
            lock (_friendSync)
                _pendingFriendRequests.TryGetValue(requestId ?? "", out pending);

            return pending is not null && pending.Decision.TrySetResult(accept);
        }

        private async Task HandleIncomingFriendRequestAsync(
            TcpClient client,
            BoundedLineReader reader,
            StreamWriter writer,
            ControlEnvelope requestEnvelope,
            CancellationToken lifetimeToken)
        {
            string requestId = string.IsNullOrWhiteSpace(requestEnvelope.RequestId)
                ? Guid.NewGuid().ToString("N")
                : requestEnvelope.RequestId.Trim();

            if (!Guid.TryParse(requestEnvelope.DeviceId, out _) ||
                string.IsNullOrWhiteSpace(requestEnvelope.PublicKeySpki))
            {
                await WriteEnvelopeDirectAsync(writer, new ControlEnvelope
                {
                    Type = "friend_decline",
                    RequestId = requestId,
                    Message = "Pedido de amizade inválido."
                });
                client.Dispose();
                return;
            }

            MultiverseFriend? knownFriend =
                _friendStore.FindByDeviceId(requestEnvelope.DeviceId);
            if (knownFriend?.Blocked == true)
            {
                await WriteEnvelopeDirectAsync(writer, new ControlEnvelope
                {
                    Type = "friend_decline",
                    RequestId = requestId,
                    Message = "Este dispositivo está bloqueado."
                });
                client.Dispose();
                return;
            }

            string remoteAddress =
                (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "";

            var request = new FriendRequest
            {
                RequestId = requestId,
                DeviceId = requestEnvelope.DeviceId,
                DisplayName = string.IsNullOrWhiteSpace(requestEnvelope.SourceName)
                    ? "PC Tailscale"
                    : requestEnvelope.SourceName.Trim(),
                PublicKeySpki = requestEnvelope.PublicKeySpki,
                RemoteAddress = remoteAddress,
                ReceivedAtUtc = DateTimeOffset.UtcNow
            };

            var pending = new PendingFriendRequest
            {
                Request = request,
                Decision = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously)
            };

            lock (_friendSync)
                _pendingFriendRequests[requestId] = pending;

            try
            {
                FriendRequestReceived?.Invoke(request);

                using CancellationTokenSource decisionTimeout =
                    CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
                decisionTimeout.CancelAfter(TimeSpan.FromSeconds(60));

                bool accepted;
                try
                {
                    accepted = await pending.Decision.Task.WaitAsync(decisionTimeout.Token);
                }
                catch (OperationCanceledException)
                {
                    accepted = false;
                }

                if (!accepted)
                {
                    await WriteEnvelopeDirectAsync(writer, new ControlEnvelope
                    {
                        Type = "friend_decline",
                        RequestId = requestId,
                        Message = "Pedido recusado ou expirado."
                    });
                    return;
                }

                _friendStore.Upsert(new MultiverseFriend
                {
                    DeviceId = request.DeviceId,
                    DisplayName = request.DisplayName,
                    PublicKeySpki = request.PublicKeySpki,
                    LastKnownIp = remoteAddress,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    LastSeenUtc = DateTimeOffset.UtcNow
                });

                await WriteEnvelopeDirectAsync(writer, new ControlEnvelope
                {
                    Type = "friend_accept",
                    RequestId = requestId,
                    DeviceId = _identity.DeviceId,
                    SourceName = _identity.DisplayName,
                    PublicKeySpki = _identity.PublicKeySpki,
                    Message = "Amizade aceita."
                });
            }
            finally
            {
                lock (_friendSync)
                    _pendingFriendRequests.Remove(requestId);
                client.Dispose();
                FriendRequestReceived?.Invoke(new FriendRequest { RequestId = "" });
            }
        }

        private async Task HandleIncomingFriendHelloAsync(
            TcpClient client,
            BoundedLineReader reader,
            StreamWriter writer,
            ControlEnvelope hello,
            CancellationToken lifetimeToken)
        {
            MultiverseFriend? friend = _friendStore.FindByDeviceId(hello.DeviceId);
            if (friend is null || !IsValidFriendNonce(hello.Nonce))
            {
                await WriteEnvelopeDirectAsync(writer, new ControlEnvelope
                {
                    Type = "reject",
                    Message = "Dispositivo não é um amigo confiável."
                });
                client.Dispose();
                return;
            }

            bool busy;
            lock (_sync)
                busy = _activeTcp is not null;
            if (busy)
            {
                await WriteEnvelopeDirectAsync(writer, new ControlEnvelope
                {
                    Type = "reject",
                    Message = "Este computador já está em uma sessão."
                });
                client.Dispose();
                return;
            }

            string responderNonce = FriendConnectionService.CreateNonce();
            string transcript = FriendConnectionService.BuildTranscript(
                hello.DeviceId,
                _identity.DeviceId,
                hello.Nonce,
                responderNonce);

            await WriteEnvelopeDirectAsync(writer, new ControlEnvelope
            {
                Type = "friend_challenge",
                DeviceId = _identity.DeviceId,
                Nonce = responderNonce,
                Signature = _identity.Sign(transcript),
                SourceName = DeviceName,
                UdpPort = VideoPort
            });

            using CancellationTokenSource proofTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
            proofTimeout.CancelAfter(TimeSpan.FromSeconds(5));

            ControlEnvelope? proof = Deserialize(
                await reader.ReadLineAsync(proofTimeout.Token));

            bool signatureValid = proof is not null &&
                proof.Protocol == ProtocolVersion &&
                string.Equals(proof.Type, "friend_proof", StringComparison.Ordinal) &&
                string.Equals(proof.DeviceId, friend.DeviceId, StringComparison.OrdinalIgnoreCase) &&
                FriendConnectionService.VerifyTranscript(
                    friend.PublicKeySpki,
                    transcript,
                    proof.Signature);

            FriendAuthorizationDecision decision =
                FriendAuthorizationPolicy.EvaluateDirectRequest(friend, signatureValid);

            if (decision == FriendAuthorizationDecision.RejectSignature)
            {
                await WriteEnvelopeDirectAsync(writer, new ControlEnvelope
                {
                    Type = "reject",
                    Message = "Assinatura do amigo não pôde ser validada."
                });
                client.Dispose();
                return;
            }
            if (decision == FriendAuthorizationDecision.RejectBlocked)
            {
                await WriteEnvelopeDirectAsync(writer, new ControlEnvelope
                {
                    Type = "reject",
                    Message = "Este amigo está bloqueado."
                });
                client.Dispose();
                return;
            }
            if (decision == FriendAuthorizationDecision.RejectDirectDisabled)
            {
                await WriteEnvelopeDirectAsync(writer, new ControlEnvelope
                {
                    Type = "reject",
                    Message = "Entrada direta não liberada por este amigo."
                });
                client.Dispose();
                return;
            }
            if (decision == FriendAuthorizationDecision.RejectUnknown)
            {
                await WriteEnvelopeDirectAsync(writer, new ControlEnvelope
                {
                    Type = "reject",
                    Message = "Dispositivo não é um amigo confiável."
                });
                client.Dispose();
                return;
            }

            if (decision == FriendAuthorizationDecision.RequireApproval)
            {
                string requestId = Guid.NewGuid().ToString("N");
                string remoteAddress =
                    (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "";
                var request = new DirectSessionRequest
                {
                    RequestId = requestId,
                    DeviceId = friend.DeviceId,
                    DisplayName = friend.DisplayName,
                    RemoteAddress = remoteAddress,
                    ReceivedAtUtc = DateTimeOffset.UtcNow
                };
                var pending = new PendingDirectSessionRequest
                {
                    Request = request,
                    Decision = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously)
                };
                lock (_friendSync)
                    _pendingDirectSessionRequests[requestId] = pending;

                bool accepted = false;
                try
                {
                    DirectSessionRequestReceived?.Invoke(request);
                    using CancellationTokenSource approvalTimeout =
                        CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
                    approvalTimeout.CancelAfter(TimeSpan.FromSeconds(60));
                    try
                    {
                        accepted = await pending.Decision.Task.WaitAsync(approvalTimeout.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        accepted = false;
                    }
                }
                finally
                {
                    lock (_friendSync)
                        _pendingDirectSessionRequests.Remove(requestId);
                    DirectSessionRequestReceived?.Invoke(new DirectSessionRequest { RequestId = "" });
                }

                if (!accepted)
                {
                    await WriteEnvelopeDirectAsync(writer, new ControlEnvelope
                    {
                        Type = "reject",
                        Message = "Conexão direta recusada ou expirada."
                    });
                    client.Dispose();
                    return;
                }
            }

            lock (_sync)
                busy = _activeTcp is not null;
            if (busy)
            {
                await WriteEnvelopeDirectAsync(writer, new ControlEnvelope
                {
                    Type = "reject",
                    Message = "Este computador já está em uma sessão."
                });
                client.Dispose();
                return;
            }

            IPEndPoint remoteTcp = (IPEndPoint)client.Client.RemoteEndPoint!;
            int remoteUdpPort = IsValidPort(hello.UdpPort) ? hello.UdpPort : VideoPort;

            int generation = ActivateSession(
                client,
                reader,
                writer,
                RemoteSessionRole.Host,
                string.IsNullOrWhiteSpace(hello.SourceCode) ? hello.DeviceId : hello.SourceCode,
                string.IsNullOrWhiteSpace(hello.SourceName) ? friend.DisplayName : hello.SourceName,
                new IPEndPoint(remoteTcp.Address, remoteUdpPort),
                friend.DeviceId,
                friend.AllowRemoteInput,
                friend.AllowRemoteAudio);

            _friendStore.UpdatePresence(
                friend.DeviceId,
                remoteTcp.Address.ToString(),
                friend.TailscaleDnsName,
                friend.StablePeerId);

            await SendAsync(new ControlEnvelope
            {
                Type = "accept",
                SourceCode = DeviceCode,
                SourceName = DeviceName,
                DeviceId = _identity.DeviceId,
                UdpPort = VideoPort,
                Message = "Sessão de amigo autenticada."
            });

            CancellationToken sessionToken = _sessionCancel!.Token;
            _ = Task.Run(() => ControlLoopAsync(generation, sessionToken));
            _ = Task.Run(() => KeepAliveLoopAsync(generation, sessionToken));
            RaiseSessionChanged();
        }


        public async Task<RemoteConnectResult> PairFriendAsync(
            TailscalePeerInfo peer,
            CancellationToken cancellationToken = default)
        {
            if (peer is null || string.IsNullOrWhiteSpace(peer.TailscaleIp))
                return new RemoteConnectResult { Success = false, Message = "Peer Tailscale inválido." };

            IPAddress? address = await _tailscale.ResolvePeerAsync(
                peer.TailscaleIp, ControlPort, cancellationToken);
            if (address is null)
                return new RemoteConnectResult { Success = false, Message = "Peer Tailscale não possui rota válida agora." };

            using TcpClient client = new(AddressFamily.InterNetwork)
            {
                NoDelay = true,
                SendBufferSize = 32 * 1024,
                ReceiveBufferSize = 32 * 1024
            };

            try
            {
                using CancellationTokenSource timeout =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(65));

                await client.ConnectAsync(address, ControlPort, timeout.Token);
                using NetworkStream stream = client.GetStream();
                using BoundedLineReader reader = new(
                    new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true),
                    MaxControlLineChars);
                using StreamWriter writer = new(
                    stream, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

                string requestId = Guid.NewGuid().ToString("N");
                await WriteEnvelopeDirectAsync(writer, new ControlEnvelope
                {
                    Type = "friend_request",
                    RequestId = requestId,
                    DeviceId = _identity.DeviceId,
                    PublicKeySpki = _identity.PublicKeySpki,
                    SourceName = _identity.DisplayName,
                    Message = "Pedido de amizade Leveling"
                });

                ControlEnvelope? response = Deserialize(
                    await reader.ReadLineAsync(timeout.Token));
                if (response is null || response.Protocol != ProtocolVersion)
                    return new RemoteConnectResult { Success = false, Message = "Resposta de amizade inválida." };

                if (string.Equals(response.Type, "friend_decline", StringComparison.Ordinal))
                    return new RemoteConnectResult { Success = false, Message = string.IsNullOrWhiteSpace(response.Message) ? "Pedido recusado." : response.Message };

                if (!string.Equals(response.Type, "friend_accept", StringComparison.Ordinal) ||
                    !Guid.TryParse(response.DeviceId, out _) ||
                    string.IsNullOrWhiteSpace(response.PublicKeySpki))
                {
                    return new RemoteConnectResult { Success = false, Message = "Resposta de amizade inesperada." };
                }

                _friendStore.Upsert(new MultiverseFriend
                {
                    DeviceId = response.DeviceId,
                    DisplayName = string.IsNullOrWhiteSpace(response.SourceName) ? peer.DisplayName : response.SourceName,
                    PublicKeySpki = response.PublicKeySpki,
                    StablePeerId = peer.StablePeerId,
                    TailscaleDnsName = peer.DnsName,
                    LastKnownIp = peer.TailscaleIp,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    LastSeenUtc = DateTimeOffset.UtcNow
                });

                return new RemoteConnectResult
                {
                    Success = true,
                    Message = $"{(string.IsNullOrWhiteSpace(response.SourceName) ? peer.DisplayName : response.SourceName)} agora é seu amigo."
                };
            }
            catch (OperationCanceledException)
            {
                return new RemoteConnectResult { Success = false, Message = "O pedido de amizade expirou." };
            }
            catch (Exception ex)
            {
                return new RemoteConnectResult { Success = false, Message = $"Falha ao adicionar amigo: {ex.Message}" };
            }
        }

        public async Task<RemoteConnectResult> ConnectFriendAsync(
            MultiverseFriend friend,
            string currentTailscaleIp,
            CancellationToken cancellationToken = default)
        {
            Start();
            if (friend is null || string.IsNullOrWhiteSpace(friend.DeviceId) ||
                string.IsNullOrWhiteSpace(friend.PublicKeySpki))
                return new RemoteConnectResult { Success = false, Message = "Amigo inválido." };
            if (friend.Blocked)
                return new RemoteConnectResult { Success = false, Message = "Este amigo está bloqueado." };

            lock (_sync)
            {
                if (_activeTcp is not null)
                    return new RemoteConnectResult { Success = false, Message = "Já existe uma sessão ativa." };
            }

            string host = string.IsNullOrWhiteSpace(currentTailscaleIp)
                ? friend.LastKnownIp
                : currentTailscaleIp;
            IPAddress? address = await _tailscale.ResolvePeerAsync(host, ControlPort, cancellationToken);
            if (address is null)
                return new RemoteConnectResult { Success = false, Message = "Amigo offline ou sem rota Tailscale válida." };

            TcpClient client = new(AddressFamily.InterNetwork)
            {
                NoDelay = true,
                SendBufferSize = 64 * 1024,
                ReceiveBufferSize = 64 * 1024
            };

            try
            {
                using CancellationTokenSource timeout =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                // Direct sessions may wait for the trusted friend to press Aceitar.
                timeout.CancelAfter(TimeSpan.FromSeconds(65));

                await client.ConnectAsync(address, ControlPort, timeout.Token);
                NetworkStream stream = client.GetStream();
                BoundedLineReader reader = new(
                    new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true),
                    MaxControlLineChars);
                StreamWriter writer = new(
                    stream, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

                string initiatorNonce = FriendConnectionService.CreateNonce();
                await WriteEnvelopeDirectAsync(writer, new ControlEnvelope
                {
                    Type = "friend_hello",
                    DeviceId = _identity.DeviceId,
                    Nonce = initiatorNonce,
                    SourceCode = DeviceCode,
                    SourceName = DeviceName,
                    UdpPort = VideoPort
                });

                ControlEnvelope? challenge = Deserialize(
                    await reader.ReadLineAsync(timeout.Token));
                if (challenge is null || challenge.Protocol != ProtocolVersion)
                {
                    client.Dispose();
                    return new RemoteConnectResult { Success = false, Message = "Autenticação de amigo inválida." };
                }
                if (string.Equals(challenge.Type, "reject", StringComparison.Ordinal))
                {
                    client.Dispose();
                    return new RemoteConnectResult { Success = false, Message = challenge.Message };
                }
                if (!string.Equals(challenge.Type, "friend_challenge", StringComparison.Ordinal) ||
                    !string.Equals(challenge.DeviceId, friend.DeviceId, StringComparison.OrdinalIgnoreCase) ||
                    !IsValidFriendNonce(challenge.Nonce))
                {
                    client.Dispose();
                    return new RemoteConnectResult { Success = false, Message = "Desafio de amigo inesperado." };
                }

                string transcript = FriendConnectionService.BuildTranscript(
                    _identity.DeviceId,
                    friend.DeviceId,
                    initiatorNonce,
                    challenge.Nonce);
                if (!FriendConnectionService.VerifyTranscript(
                    friend.PublicKeySpki, transcript, challenge.Signature))
                {
                    client.Dispose();
                    return new RemoteConnectResult { Success = false, Message = "A identidade do amigo não confere." };
                }

                await WriteEnvelopeDirectAsync(writer, new ControlEnvelope
                {
                    Type = "friend_proof",
                    DeviceId = _identity.DeviceId,
                    Signature = _identity.Sign(transcript)
                });

                ControlEnvelope? reply = Deserialize(
                    await reader.ReadLineAsync(timeout.Token));
                if (reply is null || reply.Protocol != ProtocolVersion ||
                    !string.Equals(reply.Type, "accept", StringComparison.Ordinal))
                {
                    client.Dispose();
                    return new RemoteConnectResult
                    {
                        Success = false,
                        Message = reply?.Message ?? "O amigo não aceitou a sessão autenticada."
                    };
                }

                int remoteUdpPort = IsValidPort(reply.UdpPort) ? reply.UdpPort : VideoPort;
                int generation = ActivateSession(
                    client,
                    reader,
                    writer,
                    RemoteSessionRole.Client,
                    reply.SourceCode,
                    string.IsNullOrWhiteSpace(reply.SourceName) ? friend.DisplayName : reply.SourceName,
                    new IPEndPoint(address, remoteUdpPort),
                    friend.DeviceId);

                _friendStore.UpdatePresence(
                    friend.DeviceId,
                    address.ToString(),
                    friend.TailscaleDnsName,
                    friend.StablePeerId);

                CancellationToken sessionToken = _sessionCancel!.Token;
                _ = Task.Run(() => ControlLoopAsync(generation, sessionToken));
                _ = Task.Run(() => KeepAliveLoopAsync(generation, sessionToken));
                RaiseSessionChanged();

                return new RemoteConnectResult { Success = true, Message = $"Conectado a {friend.DisplayName}." };
            }
            catch (OperationCanceledException)
            {
                client.Dispose();
                return new RemoteConnectResult { Success = false, Message = "Tempo limite ao conectar ao amigo." };
            }
            catch (Exception ex)
            {
                client.Dispose();
                return new RemoteConnectResult { Success = false, Message = $"Falha ao conectar ao amigo: {ex.Message}" };
            }
        }

        private static bool IsValidFriendNonce(string nonce)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(nonce) &&
                    Convert.FromBase64String(nonce).Length == 32;
            }
            catch
            {
                return false;
            }
        }

        public async Task<RemoteConnectResult>
            ConnectAsync(
                string target)
        {
            Start();

            lock (_sync)
            {
                if (_activeTcp is not null)
                {
                    return new RemoteConnectResult
                    {
                        Success = false,
                        Message =
                            "Já existe uma sessão ativa."
                    };
                }
            }

            ResolvedTarget resolved;

            try
            {
                resolved =
                    await ResolveTargetAsync(
                        target);
            }
            catch (Exception ex)
            {
                return new RemoteConnectResult
                {
                    Success = false,
                    Message = ex.Message
                };
            }

            TcpClient client =
                new(
                    AddressFamily.InterNetwork)
                {
                    NoDelay = true,
                    SendBufferSize = 64 * 1024,
                    ReceiveBufferSize = 64 * 1024
                };

            try
            {
                using CancellationTokenSource timeout =
                    new(
                        TimeSpan.FromSeconds(6));

                await client.ConnectAsync(
                    resolved.Address,
                    resolved.ControlPort,
                    timeout.Token);

                NetworkStream stream =
                    client.GetStream();

                BoundedLineReader reader =
                    new(
                        new StreamReader(
                            stream,
                            Encoding.UTF8,
                            detectEncodingFromByteOrderMarks: false,
                            bufferSize: 4096,
                            leaveOpen: true),
                        MaxControlLineChars);

                StreamWriter writer =
                    new(
                        stream,
                        new UTF8Encoding(
                            encoderShouldEmitUTF8Identifier: false),
                        bufferSize: 4096,
                        leaveOpen: true)
                    {
                        AutoFlush = true
                    };

                await WriteEnvelopeDirectAsync(
                    writer,
                    new ControlEnvelope
                    {
                        Type = "hello",
                        TargetCode =
                            resolved.TargetCode,
                        SourceCode =
                            DeviceCode,
                        SourceName =
                            DeviceName,
                        UdpPort =
                            VideoPort
                    });

                string? line =
                    await reader.ReadLineAsync(
                        timeout.Token);

                ControlEnvelope? reply =
                    Deserialize(
                        line);

                if (
                    reply is null ||
                    reply.Protocol != ProtocolVersion)
                {
                    client.Dispose();

                    return new RemoteConnectResult
                    {
                        Success = false,
                        Message =
                            "O PC remoto respondeu com um protocolo inválido."
                    };
                }

                if (string.Equals(
                        reply.Type,
                        "reject",
                        StringComparison.Ordinal))
                {
                    client.Dispose();

                    return new RemoteConnectResult
                    {
                        Success = false,
                        Message =
                            string.IsNullOrWhiteSpace(
                                reply.Message)
                                ? "Conexão recusada."
                                : reply.Message
                    };
                }

                if (!string.Equals(
                        reply.Type,
                        "accept",
                        StringComparison.Ordinal))
                {
                    client.Dispose();

                    return new RemoteConnectResult
                    {
                        Success = false,
                        Message =
                            "Resposta de conexão inesperada."
                    };
                }

                int remoteUdpPort =
                    IsValidPort(
                        reply.UdpPort)
                        ? reply.UdpPort
                        : VideoPort;

                int generation =
                    ActivateSession(
                        client,
                        reader,
                        writer,
                        RemoteSessionRole.Client,
                        reply.SourceCode,
                        reply.SourceName,
                        new IPEndPoint(
                            resolved.Address,
                            remoteUdpPort));

                SavePeerProfile(reply.SourceCode, reply.SourceName);

                CancellationToken sessionToken =
                    _sessionCancel!.Token;

                _ = Task.Run(
                    () => ControlLoopAsync(
                        generation,
                        sessionToken));

                _ = Task.Run(
                    () => KeepAliveLoopAsync(
                        generation,
                        sessionToken));

                RaiseSessionChanged();

                return new RemoteConnectResult
                {
                    Success = true,
                    Message =
                        $"Conectado a {_remoteName}."
                };
            }
            catch (OperationCanceledException)
            {
                client.Dispose();

                return new RemoteConnectResult
                {
                    Success = false,
                    Message =
                        "Tempo limite ao conectar."
                };
            }
            catch (SocketException ex)
            {
                client.Dispose();

                return new RemoteConnectResult
                {
                    Success = false,
                    Message =
                        $"Não foi possível alcançar o PC remoto: {ex.Message}"
                };
            }
            catch (Exception ex)
            {
                client.Dispose();

                return new RemoteConnectResult
                {
                    Success = false,
                    Message =
                        $"Falha ao conectar: {ex.Message}"
                };
            }
        }

        private int ActivateSession(
            TcpClient client,
            BoundedLineReader reader,
            StreamWriter writer,
            RemoteSessionRole role,
            string remoteCode,
            string remoteName,
            IPEndPoint remoteVideoEndpoint,
            string remoteFriendDeviceId = "",
            bool remoteInputAllowed = true,
            bool remoteAudioAllowed = true)
        {
            int generation;
            Channel<ControlEnvelope> outbound;
            Channel<ControlEnvelope> telemetryOutbound;
            Channel<ControlEnvelope> gamepadOutbound;
            Channel<ControlEnvelope> cursorOutbound;
            SemaphoreSlim outboundSignal;
            CancellationToken sessionToken;

            lock (_sync)
            {
                CloseActiveSocketLocked();

                _sessionGeneration++;
                generation =
                    _sessionGeneration;

                _activeTcp =
                    client;

                _activeReader =
                    reader;

                _activeWriter =
                    writer;

                _sessionCancel =
                    CancellationTokenSource
                        .CreateLinkedTokenSource(
                            _lifetimeCancel?.Token ??
                            CancellationToken.None);

                sessionToken =
                    _sessionCancel.Token;

                _controlOutbound =
                    Channel.CreateBounded<ControlEnvelope>(
                        new BoundedChannelOptions(CriticalControlQueueCapacity)
                        {
                            SingleReader = true,
                            SingleWriter = false,
                            FullMode = BoundedChannelFullMode.Wait,
                            AllowSynchronousContinuations = false
                        });

                // Gamepad is latency-sensitive and latest-wins. A stalled
                // TCP writer keeps only the newest controller state; ping/IDR/bye
                // still win because the critical queue is drained first.
                // Network telemetry is advisory and latest-wins. A TCP stall
                // must never queue stale netstats ahead of ping/IDR/bye.
                _telemetryOutbound =
                    Channel.CreateBounded<ControlEnvelope>(
                        new BoundedChannelOptions(1)
                        {
                            SingleReader = true,
                            SingleWriter = false,
                            FullMode =
                                BoundedChannelFullMode.DropOldest,
                            AllowSynchronousContinuations = false
                        });

                _gamepadOutbound =
                    Channel.CreateBounded<ControlEnvelope>(
                        new BoundedChannelOptions(1)
                        {
                            SingleReader = true,
                            SingleWriter = false,
                            FullMode =
                                BoundedChannelFullMode.DropOldest,
                            AllowSynchronousContinuations = false
                        });

                // Cursor is latest-wins. Never put 60 cursor updates/second
                // behind ping/IDR/bye on the ordered TCP control channel.
                // A network stall keeps at most the newest cursor sample.
                _cursorOutbound =
                    Channel.CreateBounded<ControlEnvelope>(
                        new BoundedChannelOptions(1)
                        {
                            SingleReader = true,
                            SingleWriter = false,
                            FullMode =
                                BoundedChannelFullMode.DropOldest,
                            AllowSynchronousContinuations = false
                        });

                _controlOutboundSignal =
                    new SemaphoreSlim(
                        0,
                        1);

                outbound =
                    _controlOutbound;

                telemetryOutbound =
                    _telemetryOutbound;

                gamepadOutbound =
                    _gamepadOutbound;

                cursorOutbound =
                    _cursorOutbound;

                outboundSignal =
                    _controlOutboundSignal;

                _role =
                    role;

                _remoteCode =
                    FormatCode(
                        NormalizeCode(
                            remoteCode));

                _remoteName =
                    string.IsNullOrWhiteSpace(
                        remoteName)
                        ? "PC REMOTO"
                        : remoteName.Trim();

                _remoteAddress =
                    client.Client.RemoteEndPoint?
                        .ToString() ??
                    remoteVideoEndpoint.Address
                        .ToString();

                _remoteFriendDeviceId = remoteFriendDeviceId ?? "";
                _remoteInputAllowed = remoteInputAllowed;
                _remoteAudioAllowed = remoteAudioAllowed;

                _remoteVideoEndpoint =
                    remoteVideoEndpoint;

                _streamConfig =
                    null;
                _expectedRemoteVideoReadyGeneration = 0;
                _remoteVideoReady = false;

                _lastControlMessageAt =
                    Environment.TickCount64;
                Interlocked.Exchange(ref _controlLastReadAt, 0);
                Interlocked.Exchange(ref _controlLastWriteAt, 0);
                Interlocked.Exchange(ref _controlLastPongAt, 0);

                _rttMs = 0;
                _baselineRttMs = 0;
                _jitterMs = 0;
                _lastRttSampleMs = 0;

                _remotePacketLossPercent = 0;
                _remoteVideoFps = 0;
                _remoteRxMbps = 0;
                _remotePendingFrames = 0;
                _remoteDecodeDrops = 0;
                _remoteDecodeStaleDrops = 0;
                _remoteReferenceWaitDrops = 0;
                _remotePresenterDrops = 0;
                _remotePresentedFrames = 0;
                _remoteCompleteFrames = _remoteDecodedFrames = 0;
                _remoteLastPresentAgeMs = -1;
                _hasRemoteTransportCounters = false;
                _lastRemoteReceivedPackets = 0;
                _lastRemoteReceivedBytes = 0;
                _lastRemoteSequenceGaps = 0;
                _lastRemoteIncompleteFrames = 0;
                _lastRemoteStatsTick = 0;
                _remoteCursorVisible = false;
                _remoteCursorX = 0;
                _remoteCursorY = 0;
                _remoteGamepadConnected = false;
                _remoteGamepadPackets = 0;
                _lastRemoteGamepadAt = 0;

                _videoStartupErrorGeneration = 0;
                _videoStartupError = "";
                _lastVideoStartupError = "";
                _lastError = "";
                _statusText =
                    role == RemoteSessionRole.Host
                        ? $"Conectado • enviando para {_remoteName}"
                        : $"Conectado • recebendo de {_remoteName}";
            }

            // O writer TCP fica isolado em uma task própria. Nenhum loop de
            // leitura, keepalive ou UI espera por um WriteLine de rede.
            _ = Task.Run(
                () => ControlWriteLoopAsync(
                    generation,
                    writer,
                    outbound.Reader,
                    telemetryOutbound.Reader,
                    gamepadOutbound.Reader,
                    cursorOutbound.Reader,
                    outboundSignal,
                    sessionToken));

            return generation;
        }

        internal ControlDiagnosticsSnapshot GetControlDiagnostics()
        {
            long now = Environment.TickCount64;
            static long Age(long now, long tick) => tick == 0 ? -1 : Math.Max(0, now - tick);
            static int Count(Channel<ControlEnvelope>? channel) =>
                channel is not null && channel.Reader.CanCount ? channel.Reader.Count : -1;
            lock (_sync)
            {
                return new ControlDiagnosticsSnapshot
                {
                    Connected = _activeTcp is not null && _role != RemoteSessionRole.None,
                    SessionGeneration = _sessionGeneration,
                    ReadMessages = Interlocked.Read(ref _controlReadMessages),
                    WriteMessages = Interlocked.Read(ref _controlWriteMessages),
                    ReadBytes = Interlocked.Read(ref _controlReadBytes),
                    WriteBytes = Interlocked.Read(ref _controlWriteBytes),
                    CriticalQueueDepth = Count(_controlOutbound),
                    TelemetryQueueDepth = Count(_telemetryOutbound),
                    GamepadQueueDepth = Count(_gamepadOutbound),
                    CursorQueueDepth = Count(_cursorOutbound),
                    LastReadAgeMs = Age(now, Interlocked.Read(ref _controlLastReadAt)),
                    LastWriteAgeMs = Age(now, Interlocked.Read(ref _controlLastWriteAt)),
                    LastPongAgeMs = Age(now, Interlocked.Read(ref _controlLastPongAt)),
                    ReadInProgressAgeMs = Age(now, Interlocked.Read(ref _controlReadStartedAt)),
                    WriteInProgressAgeMs = Age(now, Interlocked.Read(ref _controlWriteStartedAt)),
                    LastWriteDurationMs = Interlocked.Read(ref _controlLastWriteDurationUs) / 1000.0,
                    MaxWriteDurationMs = Interlocked.Read(ref _controlMaxWriteDurationUs) / 1000.0
                };
            }
        }

        private async Task WriteMeasuredAsync(StreamWriter writer, ControlEnvelope envelope)
        {
            string json = JsonSerializer.Serialize(envelope);
            long start = Stopwatch.GetTimestamp();
            Interlocked.Exchange(ref _controlWriteStartedAt, Environment.TickCount64);
            try
            {
                await writer.WriteLineAsync(json).ConfigureAwait(false);
                Interlocked.Increment(ref _controlWriteMessages);
                Interlocked.Add(ref _controlWriteBytes, Encoding.UTF8.GetByteCount(json) + 1);
                Interlocked.Exchange(ref _controlLastWriteAt, Environment.TickCount64);
            }
            finally
            {
                long durationUs = (long)(Stopwatch.GetElapsedTime(start).TotalMilliseconds * 1000.0);
                Interlocked.Exchange(ref _controlLastWriteDurationUs, durationUs);
                long current;
                while (durationUs > (current = Interlocked.Read(ref _controlMaxWriteDurationUs)) &&
                    Interlocked.CompareExchange(ref _controlMaxWriteDurationUs, durationUs, current) != current) { }
                Interlocked.Exchange(ref _controlWriteStartedAt, 0);
            }
        }

        private async Task ControlWriteLoopAsync(
            int generation,
            StreamWriter writer,
            ChannelReader<ControlEnvelope> criticalReader,
            ChannelReader<ControlEnvelope> telemetryReader,
            ChannelReader<ControlEnvelope> gamepadReader,
            ChannelReader<ControlEnvelope> cursorReader,
            SemaphoreSlim signal,
            CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await signal.WaitAsync(token);

                    // Critical ordered control always wins over cosmetic
                    // cursor telemetry. This prevents a 500 ms network stall
                    // from placing stale cursor JSON ahead of ping/IDR/bye.
                    while (
                        criticalReader.TryRead(
                            out ControlEnvelope? critical))
                    {
                        await WriteMeasuredAsync(writer, critical).ConfigureAwait(false);
                    }

                    // Telemetry is coalesced and can never delay critical
                    // keepalive/IDR/session control after a network stall.
                    if (
                        telemetryReader.TryRead(
                            out ControlEnvelope? telemetry))
                    {
                        await WriteMeasuredAsync(writer, telemetry).ConfigureAwait(false);
                    }

                    // Gamepad channel has capacity 1 / DropOldest. Never
                    // replay stale analog/button samples after a network stall.
                    if (
                        gamepadReader.TryRead(
                            out ControlEnvelope? gamepad))
                    {
                        await WriteMeasuredAsync(writer, gamepad).ConfigureAwait(false);
                    }

                    // Cursor channel has capacity 1 / DropOldest, therefore
                    // this is always the newest known sample.
                    if (
                        cursorReader.TryRead(
                            out ControlEnvelope? cursor))
                    {
                        await WriteMeasuredAsync(writer, cursor).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    _lastError =
                        $"TX controle: {ex.Message}";
                }
            }

            CloseActiveSession(
                generation,
                "Canal de controle foi encerrado.");
        }

        private async Task ControlLoopAsync(
            int generation,
            CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    BoundedLineReader? reader;

                    lock (_sync)
                    {
                        if (
                            generation !=
                                _sessionGeneration ||
                            _activeReader is null)
                        {
                            return;
                        }

                        reader =
                            _activeReader;
                    }

                    Interlocked.Exchange(ref _controlReadStartedAt, Environment.TickCount64);
                    string? line;
                    try { line = await reader.ReadLineAsync(token).ConfigureAwait(false); }
                    finally { Interlocked.Exchange(ref _controlReadStartedAt, 0); }
                    if (line is null)
                        break;
                    Interlocked.Increment(ref _controlReadMessages);
                    Interlocked.Add(ref _controlReadBytes, Encoding.UTF8.GetByteCount(line) + 1);
                    Interlocked.Exchange(ref _controlLastReadAt, Environment.TickCount64);

                    ControlEnvelope? message =
                        Deserialize(
                            line);

                    if (
                        message is null ||
                        message.Protocol !=
                            ProtocolVersion)
                    {
                        continue;
                    }

                    lock (_sync)
                    {
                        _lastControlMessageAt =
                            Environment.TickCount64;
                    }

                    switch (message.Type)
                    {
                        case "ping":
                            await SendAsync(
                                new ControlEnvelope
                                {
                                    Type = "pong",
                                    Sequence =
                                        message.Sequence,
                                    Timestamp =
                                        message.Timestamp
                                });
                            break;

                        case "pong":
                            Interlocked.Exchange(ref _controlLastPongAt, Environment.TickCount64);
                            HandlePong(
                                message);
                            break;

                        case "stream":
                            HandleStreamConfig(
                                message);
                            break;

                        case "streamstop":
                            HandleStreamStopped(message);
                            break;

                        case "videoready":
                            HandleVideoReady(message);
                            break;

                        case "videofailed":
                            HandleVideoFailed(message);
                            break;

                        case "idr":
                            // A delayed request from an earlier stream must not
                            // force an IDR in the newly started stream.
                            bool currentIdr;
                            lock (_sync)
                                currentIdr = _role == RemoteSessionRole.Host &&
                                    message.Sequence > 0 &&
                                    message.Sequence == _streamConfig?.Generation;
                            if (currentIdr)
                                NotifyIdrHandlersSafely();
                            break;

                        case "videorecover":
                            // Only the client on this live TCP connection may
                            // request a sender rotation. Never trust the payload
                            // to select another session or another stream.
                            bool currentRecovery;
                            lock (_sync)
                                currentRecovery = _sessionGeneration == generation &&
                                    _role == RemoteSessionRole.Host &&
                                    _activeTcp is not null &&
                                    _sessionCancel?.IsCancellationRequested == false &&
                                    message.Sequence > 0 &&
                                    message.Sequence == _streamConfig?.Generation &&
                                    message.RecoveryRequestId > 0;
                            if (currentRecovery)
                                NotifySenderRecoveryHandlersSafely(generation,
                                    message.Sequence, message.RecoveryRequestId,
                                    Math.Max(0, message.PresentedFrames));
                            break;

                        case "videorecoverresult":
                            CompleteSenderRecoveryResult(generation, message);
                            break;

                        case "netstats":
                            HandleNetworkStats(message, generation);
                            break;

                        case "cursor":
                            HandleCursor(
                                message);
                            break;

                        case "desktopinput":
                            HandleDesktopInput(message,generation);
                            break;

                        case "gamepad":
                            HandleGamepad(
                                message);
                            break;

                        case "bye":
                            CloseActiveSession(
                                generation,
                                "Sessão encerrada pelo PC remoto.");
                            return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // Connection teardown.
            }
            catch (ObjectDisposedException)
            {
                // Connection teardown.
            }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    _lastError =
                        $"Controle remoto: {ex.Message}";
                }
            }

            CloseActiveSession(
                generation,
                "Conexão com o PC remoto foi encerrada.");
        }

        private async Task KeepAliveLoopAsync(
            int generation,
            CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(
                        KeepAliveIntervalMs,
                        token);

                    long now =
                        Environment.TickCount64;

                    long last;

                    lock (_sync)
                    {
                        if (
                            generation !=
                                _sessionGeneration)
                        {
                            return;
                        }

                        last =
                            _lastControlMessageAt;
                    }

                    if (
                        last != 0 &&
                        now - last >
                            KeepAliveTimeoutMs)
                    {
                        CloseActiveSession(
                            generation,
                            "Timeout de keepalive.");

                        return;
                    }

                    long sequence =
                        Interlocked.Increment(
                            ref _pingSequence);

                    await SendAsync(
                        new ControlEnvelope
                        {
                            Type = "ping",
                            Sequence =
                                sequence,
                            Timestamp =
                                Stopwatch.GetTimestamp()
                        });
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                CloseActiveSession(
                    generation,
                    "Keepalive interrompido.");
            }
        }

        private void HandlePong(
            ControlEnvelope message)
        {
            if (message.Timestamp <= 0)
                return;

            long now =
                Stopwatch.GetTimestamp();

            if (now < message.Timestamp)
                return;

            double rttMs =
                (now - message.Timestamp) *
                1000.0 /
                Stopwatch.Frequency;

            lock (_sync)
            {
                if (_baselineRttMs <= 0 ||
                    rttMs < _baselineRttMs)
                {
                    _baselineRttMs =
                        rttMs;
                }

                if (_rttMs <= 0)
                {
                    _rttMs =
                        rttMs;
                }
                else
                {
                    // Um pouco mais responsivo que antes para o pacer reagir
                    // cedo ao crescimento da fila de rede.
                    _rttMs =
                        _rttMs * 0.72 +
                        rttMs * 0.28;
                }

                if (_lastRttSampleMs > 0)
                {
                    double delta =
                        Math.Abs(
                            rttMs -
                            _lastRttSampleMs);

                    _jitterMs =
                        _jitterMs * 0.85 +
                        delta * 0.15;
                }

                _lastRttSampleMs =
                    rttMs;
            }

            RaiseSessionChanged();
        }

        private void HandleStreamConfig(
            ControlEnvelope message)
        {
            if (message.Sequence <= 0 || message.WireStreamId == 0) return;

            if (
                message.Width == 0 ||
                message.Height == 0 ||
                message.Width > 16384 ||
                message.Height > 16384)
            {
                return;
            }

            RemoteStreamConfig config =
                new()
                {
                    Width =
                        message.Width,

                    Height =
                        message.Height,

                    Fps =
                        Math.Clamp(
                            message.Fps,
                            1u,
                            1000u),

                    BitrateKbps =
                        message.BitrateKbps,

                    Generation =
                        message.Sequence, WireStreamId = message.WireStreamId
                };

            lock (_sync)
            {
                if (_role != RemoteSessionRole.Client) return;
                if (_streamConfig is not null &&
                    message.Sequence <= _streamConfig.Generation) return;
                _streamConfig = config;
                // Requests for the replaced generation can no longer receive a
                // valid result. Release their awaiters before notifying runtime.
                CancelPendingSenderRecoveriesLocked();
                _videoStartupErrorGeneration = config.Generation;
                _videoStartupError = "";
            }

            NotifySessionHandlersSafely(StreamConfigChanged);

            RaiseSessionChanged();
        }

        private void HandleVideoReady(ControlEnvelope message)
        {
            lock (_sync)
            {
                if (_role != RemoteSessionRole.Host ||
                    _expectedRemoteVideoReadyGeneration <= 0 ||
                    _streamConfig?.Generation != _expectedRemoteVideoReadyGeneration ||
                    message.Sequence != _expectedRemoteVideoReadyGeneration)
                {
                    return;
                }

                _remoteVideoReady = true;
                _videoStartupError = "";
                _lastVideoStartupError = "";
            }
        }

        // A failed decoder/UDP bootstrap must be reported to the authenticated
        // peer instead of silently becoming a generic five-second timeout.
        // The optional message is compatible with the existing protocol 18.
        private static string NormalizeVideoStartupError(string error)
        {
            string value = string.IsNullOrWhiteSpace(error)
                ? "Falha desconhecida ao preparar o vídeo."
                : error.Trim().Replace('\r', ' ').Replace('\n', ' ');
            return value.Length <= 512 ? value : value[..512];
        }

        private void HandleVideoFailed(ControlEnvelope message)
        {
            lock (_sync)
            {
                if (_role != RemoteSessionRole.Host ||
                    _expectedRemoteVideoReadyGeneration <= 0 ||
                    _streamConfig?.Generation != _expectedRemoteVideoReadyGeneration ||
                    message.Sequence != _expectedRemoteVideoReadyGeneration)
                    return;
                if (_remoteVideoReady)
                    return;

                _videoStartupErrorGeneration = message.Sequence;
                _videoStartupError = NormalizeVideoStartupError(message.Message);
                _lastVideoStartupError = _videoStartupError;
            }
            RaiseSessionChanged();
        }

        public string GetVideoStartupFailure(long streamGeneration, int sessionGeneration)
        {
            lock (_sync)
            {
                return _sessionGeneration == sessionGeneration &&
                    _videoStartupErrorGeneration == streamGeneration
                    ? _videoStartupError : "";
            }
        }

        public Task ReportClientVideoStartupFailureAsync(
            long streamGeneration, int sessionGeneration, string error)
        {
            if (!IsCurrentVideoStream(streamGeneration, sessionGeneration,
                    RemoteSessionRole.Client))
                return Task.CompletedTask;

            string reason = NormalizeVideoStartupError(error);
            lock (_sync)
            {
                if (_sessionGeneration != sessionGeneration ||
                    _role != RemoteSessionRole.Client ||
                    _streamConfig?.Generation != streamGeneration)
                    return Task.CompletedTask;
                _videoStartupErrorGeneration = streamGeneration;
                _videoStartupError = reason;
                _lastVideoStartupError = reason;
            }
            RaiseSessionChanged();

            // Best effort and non-blocking: a broken control channel cannot
            // turn a video-only preparation failure into another exception.
            TrySendCriticalForSession(new ControlEnvelope
            {
                Type = "videofailed", Sequence = streamGeneration,
                Message = reason
            }, sessionGeneration);
            return Task.CompletedTask;
        }

        private void HandleStreamStopped(ControlEnvelope message)
        {
            lock (_sync)
            {
                if (_role != RemoteSessionRole.Client || _streamConfig is null ||
                    message.Sequence != _streamConfig.Generation) return;
                _streamConfig = null;
                _expectedRemoteVideoReadyGeneration = 0;
                CancelPendingSenderRecoveriesLocked();
                _remoteVideoReady = false;
                _videoStartupErrorGeneration = 0;
                _videoStartupError = "";
                _remotePacketLossPercent = 0;
                _remoteVideoFps = 0;
                _remoteRxMbps = 0;
                _remotePendingFrames = 0;
                _remoteDecodeDrops = 0;
                _remoteDecodeStaleDrops = 0;
                _remoteReferenceWaitDrops = 0;
                _remotePresenterDrops = 0;
                _remotePresentedFrames = 0;
                _remoteCompleteFrames = _remoteDecodedFrames = 0;
                _remoteLastPresentAgeMs = -1;
                _hasRemoteTransportCounters = false;
                _lastRemoteReceivedPackets = 0;
                _lastRemoteReceivedBytes = 0;
                _lastRemoteSequenceGaps = 0;
                _lastRemoteIncompleteFrames = 0;
                _lastRemoteStatsTick = 0;
            }

            NotifySessionHandlersSafely(StreamStopped);
            RaiseSessionChanged();
        }

        private void HandleNetworkStats(
            ControlEnvelope message, int sessionGeneration)
        {
            long received =
                Math.Max(
                    0,
                    message.ReceivedPackets);

            long receivedBytes =
                Math.Max(
                    0,
                    message.ReceivedBytes);

            long gaps =
                Math.Max(
                    0,
                    message.SequenceGaps);

            long incomplete =
                Math.Max(
                    0,
                    message.IncompleteFrames);

            long now =
                Environment.TickCount64;

            lock (_sync)
            {
                // Queued telemetry can outlive a stream transition. Never
                // let an old decoder's counters veto recovery of a new one.
                if (_sessionGeneration != sessionGeneration ||
                    _role != RemoteSessionRole.Host ||
                    message.Sequence <= 0 ||
                    message.Sequence != _streamConfig?.Generation)
                    return;
                double sampleLoss = 0.0;

                bool countersReset =
                    !_hasRemoteTransportCounters ||
                    received <
                        _lastRemoteReceivedPackets ||
                    receivedBytes <
                        _lastRemoteReceivedBytes ||
                    gaps <
                        _lastRemoteSequenceGaps ||
                    incomplete <
                        _lastRemoteIncompleteFrames;

                if (countersReset)
                {
                    _hasRemoteTransportCounters =
                        true;

                    _remotePacketLossPercent =
                        0.0;

                    _remoteRxMbps =
                        0.0;
                }
                else
                {
                    long receivedDelta =
                        Math.Max(
                            0,
                            received -
                            _lastRemoteReceivedPackets);

                    long bytesDelta =
                        Math.Max(
                            0,
                            receivedBytes -
                            _lastRemoteReceivedBytes);

                    long gapDelta =
                        Math.Max(
                            0,
                            gaps -
                            _lastRemoteSequenceGaps);

                    long observedDelta =
                        receivedDelta +
                        gapDelta;

                    if (observedDelta > 0)
                    {
                        sampleLoss =
                            Math.Clamp(
                                gapDelta *
                                    100.0 /
                                    observedDelta,
                                0.0,
                                100.0);
                    }

                    double alpha =
                        sampleLoss >
                            _remotePacketLossPercent
                            ? 0.42
                            : 0.30;

                    _remotePacketLossPercent =
                        _remotePacketLossPercent *
                            (1.0 - alpha) +
                        sampleLoss *
                            alpha;

                    if (sampleLoss <= 0.001)
                    {
                        _remotePacketLossPercent *=
                            0.78;
                    }

                    if (_remotePacketLossPercent <
                        0.02)
                    {
                        _remotePacketLossPercent =
                            0.0;
                    }

                    if (_lastRemoteStatsTick > 0 &&
                        now > _lastRemoteStatsTick)
                    {
                        double seconds =
                            (now - _lastRemoteStatsTick) /
                            1000.0;

                        double sampleRxMbps =
                            bytesDelta *
                            8.0 /
                            Math.Max(0.05, seconds) /
                            1_000_000.0;

                        _remoteRxMbps =
                            _remoteRxMbps <= 0
                                ? sampleRxMbps
                                : _remoteRxMbps * 0.65 +
                                  sampleRxMbps * 0.35;
                    }
                }

                _lastRemoteReceivedPackets =
                    received;

                _lastRemoteReceivedBytes =
                    receivedBytes;

                _lastRemoteSequenceGaps =
                    gaps;

                _lastRemoteIncompleteFrames =
                    incomplete;

                _lastRemoteStatsTick =
                    now;

                _remoteVideoFps =
                    Math.Max(
                        0,
                        message.VideoFps);

                _remotePendingFrames =
                    Math.Max(
                        0,
                        message.PendingFrames);

                _remoteDecodeDrops =
                    Math.Max(
                        0,
                        message.DecodeDrops);
                _remoteDecodeStaleDrops = Math.Max(0, message.DecodeStaleDrops);
                _remoteReferenceWaitDrops = Math.Max(0, message.ReferenceWaitDrops);
                _remotePresenterDrops = Math.Max(0, message.PresenterDrops);
                _remotePresentedFrames = Math.Max(0, message.PresentedFrames);
                _remoteCompleteFrames = Math.Max(0, message.CompleteFrames);
                _remoteDecodedFrames = Math.Max(0, message.DecodedFrames);
                _remoteLastPresentAgeMs = double.IsFinite(message.LastPresentAgeMs) ? message.LastPresentAgeMs : -1;
            }
        }

        private void HandleCursor(
            ControlEnvelope message)
        {
            lock (_sync)
            {
                _remoteCursorVisible =
                    message.CursorVisible;

                _remoteCursorX =
                    Math.Clamp(
                        message.CursorX,
                        0.0,
                        1.0);

                _remoteCursorY =
                    Math.Clamp(
                        message.CursorY,
                        0.0,
                        1.0);
            }
        }

        public async Task<long> PublishStreamConfigAsync(
            uint width, uint height, uint fps, uint bitrateKbps, int expectedSessionGeneration = 0)
        {
            long generation;
            uint wireStreamId = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
            int sessionGeneration;
            lock (_sync)
            {
                if (_activeTcp is null || _role != RemoteSessionRole.Host ||
                    _sessionCancel?.IsCancellationRequested != false)
                    return 0;

                if (expectedSessionGeneration != 0 && expectedSessionGeneration != _sessionGeneration) return 0;
                _remotePresentedFrames = _remoteCompleteFrames = _remoteDecodedFrames = 0;
                _remoteLastPresentAgeMs = -1;
                _lastRemoteStatsTick = 0;
                sessionGeneration = _sessionGeneration;
                generation = ++_localVideoStreamGeneration;
                if (generation <= 0)
                {
                    _localVideoStreamGeneration = 1;
                    generation = 1;
                }

                _expectedRemoteVideoReadyGeneration = generation;
                _remoteVideoReady = false;
                _videoStartupErrorGeneration = generation;
                _videoStartupError = "";
                _streamConfig = new RemoteStreamConfig
                {
                    Width = width, Height = height, Fps = fps,
                    BitrateKbps = bitrateKbps, Generation = generation, WireStreamId = wireStreamId
                };
            }

            // Enqueue only into the control connection that owns this stream.
            // A reconnect between publication and enqueue must not receive old work.
            if (!TrySendCriticalForSession(new ControlEnvelope
            {
                Type = "stream", Sequence = generation, WireStreamId = wireStreamId,
                Width = width, Height = height, Fps = fps,
                BitrateKbps = bitrateKbps
            }, sessionGeneration))
            {
                lock (_sync)
                {
                    if (_sessionGeneration == sessionGeneration &&
                        _streamConfig?.Generation == generation)
                    {
                        _streamConfig = null;
                        _expectedRemoteVideoReadyGeneration = 0;
                        _remoteVideoReady = false;
                    }
                }
                throw new IOException("A configuração de vídeo não pôde ser enviada à sessão atual.");
            }

            RaiseSessionChanged();
            await Task.CompletedTask;
            return generation;
        }

        public Task PublishVideoReadyAsync(long streamGeneration, int sessionGeneration)
        {
            if (!IsCurrentVideoStream(streamGeneration, sessionGeneration,
                    RemoteSessionRole.Client))
                return Task.CompletedTask;

            if (!TrySendCriticalForSession(new ControlEnvelope
            {
                Type = "videoready", Sequence = streamGeneration
            }, sessionGeneration))
                throw new IOException("Não foi possível publicar VIDEO READY na sessão atual.");

            lock (_sync)
            {
                if (_sessionGeneration == sessionGeneration &&
                    _streamConfig?.Generation == streamGeneration &&
                    _videoStartupErrorGeneration == streamGeneration)
                    _videoStartupError = "";
                if (_sessionGeneration == sessionGeneration &&
                    _streamConfig?.Generation == streamGeneration)
                    _lastVideoStartupError = "";
            }
            return Task.CompletedTask;
        }

        internal CancellationToken GetVideoSessionToken(int generation)
        {
            lock (_sync) return generation == _sessionGeneration && _sessionCancel is not null
                ? _sessionCancel.Token : new CancellationToken(true);
        }

        public bool IsCurrentVideoStream(long streamGeneration, int sessionGeneration,
            RemoteSessionRole role)
        {
            lock (_sync)
            {
                return streamGeneration > 0 &&
                    _activeTcp is not null &&
                    _sessionCancel?.IsCancellationRequested == false &&
                    _sessionGeneration == sessionGeneration &&
                    _role == role &&
                    _streamConfig?.Generation == streamGeneration;
            }
        }

        public async Task<bool> WaitForRemoteVideoReadyAsync(
            long streamGeneration, int sessionGeneration, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            long timeoutMs = (long)Math.Clamp(timeout.TotalMilliseconds, 0, 5000);
            long deadline = Environment.TickCount64 + timeoutMs;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    if (_activeTcp is null || _role != RemoteSessionRole.Host ||
                        _sessionCancel?.IsCancellationRequested != false ||
                        _sessionGeneration != sessionGeneration ||
                        _expectedRemoteVideoReadyGeneration != streamGeneration ||
                        _streamConfig?.Generation != streamGeneration ||
                        streamGeneration <= 0)
                        return false;
                    if (!string.IsNullOrEmpty(_videoStartupError) &&
                        _videoStartupErrorGeneration == streamGeneration)
                        return false;
                    if (_remoteVideoReady)
                        return true;
                }
                if (Environment.TickCount64 >= deadline)
                    return false;
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
        }

        public Task PublishStreamStoppedAsync(int expectedSessionGeneration = 0, long expectedStreamGeneration = 0)
        {
            long streamGeneration;
            int sessionGeneration;
            lock (_sync)
            {
                if (_activeTcp is null || _role != RemoteSessionRole.Host ||
                    _sessionCancel?.IsCancellationRequested != false)
                    return Task.CompletedTask;

                sessionGeneration = _sessionGeneration;
                streamGeneration = _streamConfig?.Generation ?? 0;
                if ((expectedSessionGeneration != 0 && expectedSessionGeneration != sessionGeneration) ||
                    (expectedStreamGeneration != 0 && expectedStreamGeneration != streamGeneration))
                    return Task.CompletedTask;
                if (streamGeneration <= 0)
                    return Task.CompletedTask;

                _streamConfig = null;
                _expectedRemoteVideoReadyGeneration = 0;
                _remoteVideoReady = false;
                _videoStartupErrorGeneration = 0;
                _videoStartupError = "";
                _remotePacketLossPercent = 0;
                _remoteVideoFps = 0;
                _remoteRxMbps = 0;
                _remotePendingFrames = 0;
                _remoteDecodeDrops = 0;
                _remoteDecodeStaleDrops = 0;
                _remoteReferenceWaitDrops = 0;
                _remotePresenterDrops = 0;
                _remotePresentedFrames = 0;
                _remoteCompleteFrames = _remoteDecodedFrames = 0;
                _remoteLastPresentAgeMs = -1;
                _hasRemoteTransportCounters = false;
                _lastRemoteReceivedPackets = 0;
                _lastRemoteReceivedBytes = 0;
                _lastRemoteSequenceGaps = 0;
                _lastRemoteIncompleteFrames = 0;
                _lastRemoteStatsTick = 0;
            }

            // A delayed stop must never end a newer stream or a new TCP session.
            if (!TrySendCriticalForSession(new ControlEnvelope
            {
                Type = "streamstop", Sequence = streamGeneration
            }, sessionGeneration))
                throw new IOException("Não foi possível publicar a parada do vídeo.");

            RaiseSessionChanged();
            return Task.CompletedTask;
        }

        private void HandleGamepad(
            ControlEnvelope message)
        {
            lock (_sync)
            {
                if (_role != RemoteSessionRole.Host) return;
                if (!_remoteInputAllowed)
                {
                    _remoteGamepadConnected = false;
                    _lastRemoteGamepadAt = Environment.TickCount64;
                    return;
                }
            }

            RemoteGamepadPacket packet =
                new()
                {
                    Connected = message.GamepadConnected,
                    UserIndex = message.GamepadUserIndex,
                    PacketNumber = message.GamepadPacketNumber,
                    Buttons = message.GamepadButtons,
                    LeftTrigger = message.GamepadLeftTrigger,
                    RightTrigger = message.GamepadRightTrigger,
                    LeftThumbX = message.GamepadLeftThumbX,
                    LeftThumbY = message.GamepadLeftThumbY,
                    RightThumbX = message.GamepadRightThumbX,
                    RightThumbY = message.GamepadRightThumbY,
                    Sequence = message.Sequence
                };

            lock (_sync)
            {
                _remoteGamepadConnected =
                    packet.Connected;
                _remoteGamepadPackets++;
                _lastRemoteGamepadAt =
                    Environment.TickCount64;
            }

            NotifyGamepadHandlersSafely(packet);
        }

        public Task RequestRemoteIdrAsync()
        {
            RemoteSessionSnapshot snapshot = GetSnapshot();
            return RequestRemoteIdrAsync(snapshot.StreamConfig?.Generation ?? 0,
                snapshot.SessionGeneration);
        }

        public Task RequestRemoteIdrAsync(long generation, int sessionGeneration)
        {
            if (!IsCurrentVideoStream(generation, sessionGeneration,
                    RemoteSessionRole.Client))
                return Task.CompletedTask;
            if (!TrySendCriticalForSession(new ControlEnvelope
            {
                Type = "idr", Sequence = generation
            }, sessionGeneration))
                throw new IOException("Não foi possível solicitar IDR na sessão atual.");
            return Task.CompletedTask;
        }

        // Caller owns _sync. Continuations are asynchronous, so cancellation
        // cannot reenter session/runtime handlers while the lock is held.
        private void CancelPendingSenderRecoveriesLocked()
        {
            foreach (PendingSenderRecovery pending in _pendingSenderRecoveries.Values)
                pending.Completion.TrySetCanceled();
            _pendingSenderRecoveries.Clear();
        }

        public async Task<bool> AwaitSenderRecoveryResultAsync(long streamGeneration,
            int sessionGeneration, long requestId, long presentedBaseline,
            TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (requestId <= 0 || timeout <= TimeSpan.Zero ||
                !IsCurrentVideoStream(streamGeneration, sessionGeneration,
                    RemoteSessionRole.Client))
                throw new InvalidOperationException("A solicitação de recuperação não pertence à sessão atual.");

            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                if (_sessionGeneration != sessionGeneration ||
                    _role != RemoteSessionRole.Client ||
                    _streamConfig?.Generation != streamGeneration ||
                    _pendingSenderRecoveries.ContainsKey(requestId))
                    throw new InvalidOperationException("A solicitação de recuperação ficou obsoleta.");
                _pendingSenderRecoveries[requestId] = new PendingSenderRecovery
                {
                    SessionGeneration = sessionGeneration,
                    StreamGeneration = streamGeneration,
                    Completion = completion
                };
            }

            try
            {
                if (!TrySendCriticalForSession(new ControlEnvelope
                {
                    Type = "videorecover", Sequence = streamGeneration,
                    RecoveryRequestId = requestId,
                    PresentedFrames = Math.Max(0, presentedBaseline)
                }, sessionGeneration))
                    throw new IOException("Não foi possível solicitar a recuperação do sender.");

                return await completion.Task.WaitAsync(timeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                lock (_sync)
                {
                    if (_pendingSenderRecoveries.TryGetValue(requestId, out PendingSenderRecovery? pending) &&
                        ReferenceEquals(pending.Completion, completion))
                        _pendingSenderRecoveries.Remove(requestId);
                }
            }
        }

        private void CompleteSenderRecoveryResult(int sessionGeneration, ControlEnvelope message)
        {
            TaskCompletionSource<bool>? completion = null;
            bool accepted = string.Equals(message.Message, "ACCEPTED", StringComparison.Ordinal);
            lock (_sync)
            {
                if (_sessionGeneration != sessionGeneration || _role != RemoteSessionRole.Client ||
                    message.Sequence <= 0 || message.Sequence != _streamConfig?.Generation ||
                    message.RecoveryRequestId <= 0 ||
                    !_pendingSenderRecoveries.TryGetValue(message.RecoveryRequestId,
                        out PendingSenderRecovery? pending) ||
                    pending.SessionGeneration != sessionGeneration ||
                    pending.StreamGeneration != message.Sequence)
                    return;
                _pendingSenderRecoveries.Remove(message.RecoveryRequestId);
                completion = pending.Completion;
            }
            completion.TrySetResult(accepted);
            VideoIncidentRecorder.Instance.RecordEvent("CLIENT_SENDER_RECOVERY_RESULT", new
            {
                session = sessionGeneration, stream = message.Sequence,
                requestId = message.RecoveryRequestId, accepted
            });
        }

        internal Task PublishSenderRecoveryResultAsync(int sessionGeneration,
            long streamGeneration, long requestId, bool accepted)
        {
            if (!IsCurrentVideoStream(streamGeneration, sessionGeneration,
                    RemoteSessionRole.Host)) return Task.CompletedTask;
            if (!TrySendCriticalForSession(new ControlEnvelope
            {
                Type = "videorecoverresult", Sequence = streamGeneration,
                RecoveryRequestId = requestId,
                Message = accepted ? "ACCEPTED" : "REJECTED"
            }, sessionGeneration))
                throw new IOException("Não foi possível publicar o resultado da recuperação.");
            return Task.CompletedTask;
        }

        private void NotifySenderRecoveryHandlersSafely(int sessionGeneration,
            long streamGeneration, long requestId, long presentedBaseline)
        {
            Action<int, long, long, long>? handlers = RemoteSenderRecoveryRequested;
            if (handlers is null) return;
            foreach (Action<int, long, long, long> handler in handlers.GetInvocationList())
            {
                try { handler(sessionGeneration, streamGeneration, requestId, presentedBaseline); }
                catch (Exception ex) { RecordCallbackError("recuperação do sender", ex); }
            }
        }

        public Task PublishTransportTelemetryAsync(
            int sessionGeneration, long streamGeneration,
            long receivedPackets,
            long receivedBytes,
            long sequenceGaps,
            long incompleteFrames,
            int pendingFrames,
            long decodeDrops,
            long decodeStaleDrops,
            long referenceWaitDrops,
            long presenterDrops,
            double videoFps, long presentedFrames = 0, long completeFrames = 0, long decodedFrames = 0, double lastPresentAgeMs = -1)
        {
            if (!IsCurrentVideoStream(streamGeneration, sessionGeneration,
                    RemoteSessionRole.Client))
                return Task.CompletedTask;
            return SendAsync(
                new ControlEnvelope
                {
                    Type = "netstats", Sequence = streamGeneration,
                    ReceivedPackets =
                        receivedPackets,
                    ReceivedBytes =
                        receivedBytes,
                    SequenceGaps =
                        sequenceGaps,
                    IncompleteFrames =
                        incompleteFrames,
                    PendingFrames =
                        pendingFrames,
                    DecodeDrops =
                        decodeDrops,
                    DecodeStaleDrops = Math.Max(0, decodeStaleDrops),
                    ReferenceWaitDrops = Math.Max(0, referenceWaitDrops),
                    PresenterDrops = Math.Max(0, presenterDrops),
                    VideoFps =
                        videoFps,
                    PresentedFrames = Math.Max(0, presentedFrames),
                    CompleteFrames = Math.Max(0, completeFrames), DecodedFrames = Math.Max(0, decodedFrames),
                    LastPresentAgeMs = lastPresentAgeMs
                });
        }

        public Task PublishCursorAsync(
            bool visible,
            double normalizedX,
            double normalizedY)
        {
            return SendAsync(
                new ControlEnvelope
                {
                    Type = "cursor",
                    CursorVisible =
                        visible,
                    CursorX =
                        Math.Clamp(
                            normalizedX,
                            0.0,
                            1.0),
                    CursorY =
                        Math.Clamp(
                            normalizedY,
                            0.0,
                            1.0)
                });
        }

        public Task PublishGamepadAsync(
            RemoteGamepadPacket packet)
        {
            return SendAsync(
                new ControlEnvelope
                {
                    Type = "gamepad",
                    Sequence = packet.Sequence,
                    GamepadConnected = packet.Connected,
                    GamepadUserIndex = packet.UserIndex,
                    GamepadPacketNumber = packet.PacketNumber,
                    GamepadButtons = packet.Buttons,
                    GamepadLeftTrigger = packet.LeftTrigger,
                    GamepadRightTrigger = packet.RightTrigger,
                    GamepadLeftThumbX = packet.LeftThumbX,
                    GamepadLeftThumbY = packet.LeftThumbY,
                    GamepadRightThumbX = packet.RightThumbX,
                    GamepadRightThumbY = packet.RightThumbY
                });
        }

        public IPEndPoint? GetRemoteVideoEndpoint()
        {
            lock (_sync)
            {
                if (_remoteVideoEndpoint is null)
                    return null;

                return new IPEndPoint(
                    _remoteVideoEndpoint.Address,
                    _remoteVideoEndpoint.Port);
            }
        }

        public RemoteStreamConfig? GetStreamConfig()
        {
            lock (_sync)
            {
                if (_streamConfig is null)
                    return null;

                return new RemoteStreamConfig
                {
                    Width =
                        _streamConfig.Width,
                    Height =
                        _streamConfig.Height,
                    Fps =
                        _streamConfig.Fps,
                    BitrateKbps =
                        _streamConfig.BitrateKbps,
                    Generation =
                        _streamConfig.Generation,
                    WireStreamId =
                        _streamConfig.WireStreamId
                };
            }
        }

        public RemoteSessionSnapshot GetSnapshot()
        {
            lock (_sync)
            {
                bool connected =
                    _activeTcp is not null &&
                    _activeTcp.Connected;

                return new RemoteSessionSnapshot
                {
                    Listening =
                        _started,

                    SessionGeneration = _sessionGeneration,

                    Connected =
                        connected,

                    Role =
                        connected
                            ? _role
                            : RemoteSessionRole.None,

                    LocalCode =
                        DeviceCode,

                    LocalName =
                        DeviceName,

                    LocalDirectAddress =
                        BuildLocalDirectAddress(),

                    CurrentTailscaleIp =
                        _tailscale.GetSnapshot().CurrentTailscaleIp,

                    TailscaleInterface =
                        _tailscale.GetSnapshot().InterfaceName,

                    NetworkGeneration =
                        _tailscale.GetSnapshot().Generation,

                    RemoteCode =
                        _remoteCode,

                    RemoteName =
                        _remoteName,

                    RemoteAddress =
                        _remoteAddress,

                    RemoteFriendDeviceId = _remoteFriendDeviceId,
                    RemoteInputAllowed = _remoteInputAllowed,
                    RemoteAudioAllowed = _remoteAudioAllowed,

                    RoundTripMs =
                        _rttMs,

                    BaseRoundTripMs =
                        _baselineRttMs,

                    QueueDelayMs =
                        _baselineRttMs > 0 &&
                        _rttMs > _baselineRttMs
                            ? _rttMs - _baselineRttMs
                            : 0,

                    JitterMs =
                        _jitterMs,

                    RemotePacketLossPercent =
                        _remotePacketLossPercent,

                    RemoteVideoFps =
                        _remoteVideoFps,

                    RemoteRxMbps =
                        _remoteRxMbps,

                    RemoteTelemetryAgeMs =
                        _lastRemoteStatsTick == 0 ? -1 :
                            Math.Max(0, Environment.TickCount64 - _lastRemoteStatsTick),

                    RemotePendingFrames =
                        _remotePendingFrames,

                    RemoteDecodeDrops =
                        _remoteDecodeDrops,
                    RemoteDecodeStaleDrops = _remoteDecodeStaleDrops,
                    RemoteReferenceWaitDrops = _remoteReferenceWaitDrops,
                    RemotePresenterDrops = _remotePresenterDrops,
                    RemotePresentedFrames = _remotePresentedFrames,
                    RemoteCompleteFrames = _remoteCompleteFrames,
                    RemoteDecodedFrames = _remoteDecodedFrames,
                    RemoteLastPresentAgeMs = _remoteLastPresentAgeMs < 0 || _lastRemoteStatsTick == 0 ? -1 :
                        _remoteLastPresentAgeMs + Environment.TickCount64 - _lastRemoteStatsTick,

                    RemoteCursorVisible =
                        _remoteCursorVisible,

                    RemoteCursorX =
                        _remoteCursorX,

                    RemoteCursorY =
                        _remoteCursorY,

                    RemoteGamepadConnected =
                        _remoteGamepadConnected,

                    RemoteGamepadPackets =
                        _remoteGamepadPackets,

                    RemoteGamepadAgeMs =
                        _lastRemoteGamepadAt == 0
                            ? 0
                            : Math.Max(
                                0,
                                Environment.TickCount64 -
                                _lastRemoteGamepadAt),

                    StreamConfig =
                        _streamConfig,

                    LastError =
                        _lastError,

                    VideoStartupError = _lastVideoStartupError,

                    StatusText =
                        connected
                            ? _statusText
                            : _started
                                ? "Aguardando conexão."
                                : "Listener parado."
                };
            }
        }

        public async Task DisconnectAsync()
        {
            int generation;

            lock (_sync)
            {
                generation =
                    _sessionGeneration;
            }

            try
            {
                await SendAsync(
                    new ControlEnvelope
                    {
                        Type = "bye"
                    });
            }
            catch
            {
            }

            CloseActiveSession(
                generation,
                "Sessão desconectada.");
        }

        private bool TrySendCriticalForSession(ControlEnvelope envelope, int sessionGeneration)
        {
            lock (_sync)
            {
                if (_activeTcp is null || _role == RemoteSessionRole.None ||
                    _sessionCancel?.IsCancellationRequested != false ||
                    _sessionGeneration != sessionGeneration ||
                    _controlOutbound is null)
                    return false;

                if (!_controlOutbound.Writer.TryWrite(envelope))
                {
                    _lastError = "Fila crítica de controle saturada.";
                    VideoIncidentRecorder.Instance.RecordEvent(
                        "CONTROL_CRITICAL_QUEUE_SATURATED",
                        new { sessionGeneration, Type = envelope.Type });
                    return false;
                }

                try { _controlOutboundSignal?.Release(); }
                catch (SemaphoreFullException) { }
                catch (ObjectDisposedException) { return false; }
                return true;
            }
        }

        private Task SendAsync(
            ControlEnvelope envelope)
        {
            Channel<ControlEnvelope>? outbound;
            Channel<ControlEnvelope>? telemetryOutbound;
            Channel<ControlEnvelope>? gamepadOutbound;
            Channel<ControlEnvelope>? cursorOutbound;
            SemaphoreSlim? signal;

            lock (_sync)
            {
                outbound =
                    _controlOutbound;

                telemetryOutbound =
                    _telemetryOutbound;

                gamepadOutbound =
                    _gamepadOutbound;

                cursorOutbound =
                    _cursorOutbound;

                signal =
                    _controlOutboundSignal;
            }

            Channel<ControlEnvelope>? target =
                string.Equals(
                    envelope.Type,
                    "netstats",
                    StringComparison.Ordinal)
                    ? telemetryOutbound
                    : string.Equals(
                        envelope.Type,
                        "gamepad",
                        StringComparison.Ordinal)
                        ? gamepadOutbound
                        : string.Equals(
                            envelope.Type,
                            "cursor",
                            StringComparison.Ordinal)
                            ? cursorOutbound
                            : outbound;

            if (target is null)
                return Task.CompletedTask;

            // Never block UI/reader/keepalive on TCP I/O. Critical messages
            // remain ordered; cursor messages are coalesced latest-wins.
            if (!target.Writer.TryWrite(
                    envelope))
            {
                lock (_sync)
                {
                    _lastError =
                        "Fila de controle indisponível.";
                }

                return Task.CompletedTask;
            }

            if (signal is not null)
            {
                try
                {
                    signal.Release();
                }
                catch (SemaphoreFullException)
                {
                    // Event already signaled. Writer will drain all critical
                    // messages and the latest cursor sample.
                }
                catch (ObjectDisposedException)
                {
                }
            }

            return Task.CompletedTask;
        }

        private static Task WriteEnvelopeDirectAsync(
            StreamWriter writer,
            ControlEnvelope envelope)
        {
            return writer.WriteLineAsync(
                JsonSerializer.Serialize(
                    envelope));
        }

        private void CloseActiveSession(
            int generation,
            string status)
        {
            lock (_sync)
            {
                if (
                    generation !=
                    _sessionGeneration)
                {
                    return;
                }

                _statusText =
                    status;

                CloseActiveSocketLocked();
            }

            RaiseSessionChanged();
        }

        private void CloseActiveSocketLocked()
        {
            try
            {
                _sessionCancel?.Cancel();
            }
            catch
            {
            }

            try
            {
                _controlOutbound?
                    .Writer
                    .TryComplete();

                _telemetryOutbound?
                    .Writer
                    .TryComplete();

                _gamepadOutbound?
                    .Writer
                    .TryComplete();

                _cursorOutbound?
                    .Writer
                    .TryComplete();
            }
            catch
            {
            }

            try
            {
                _controlOutboundSignal?
                    .Release();
            }
            catch
            {
            }

            CancelPendingSenderRecoveriesLocked();
            _controlOutbound = null;
            _telemetryOutbound = null;
            _gamepadOutbound = null;
            _cursorOutbound = null;
            _controlOutboundSignal = null;

            _sessionCancel?.Dispose();
            _sessionCancel = null;

            try
            {
                _activeTcp?.Close();
            }
            catch
            {
            }

            _activeReader?.Dispose();
            _activeWriter?.Dispose();
            _activeTcp?.Dispose();

            _activeReader = null;
            _activeWriter = null;
            _activeTcp = null;

            _role =
                RemoteSessionRole.None;

            _remoteCode = "";
            _remoteName = "";
            _remoteAddress = "";
            _remoteFriendDeviceId = "";
            _remoteInputAllowed = true;
            _remoteAudioAllowed = true;
            _remoteVideoEndpoint = null;
            _streamConfig = null;
            _expectedRemoteVideoReadyGeneration = 0;
            _remoteVideoReady = false;
            _videoStartupErrorGeneration = 0;
            _videoStartupError = "";

            _rttMs = 0;
            _baselineRttMs = 0;
            _jitterMs = 0;
            _lastRttSampleMs = 0;

            _remotePacketLossPercent = 0;
            _remoteVideoFps = 0;
            _remoteRxMbps = 0;
            _remotePendingFrames = 0;
            _remoteDecodeDrops = 0;
            _remoteDecodeStaleDrops = 0;
            _remoteReferenceWaitDrops = 0;
            _remotePresenterDrops = 0;
            _hasRemoteTransportCounters = false;
            _lastRemoteReceivedPackets = 0;
            _lastRemoteReceivedBytes = 0;
            _lastRemoteSequenceGaps = 0;
            _lastRemoteIncompleteFrames = 0;
            _lastRemoteStatsTick = 0;
            _remoteCursorVisible = false;
            _remoteCursorX = 0;
            _remoteCursorY = 0;
            _remoteGamepadConnected = false;
            _remoteGamepadPackets = 0;
            _lastRemoteGamepadAt = 0;
        }

        private async Task DiscoveryReceiveLoopAsync(
            CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                UdpReceiveResult result;

                try
                {
                    result =
                        await _discoveryReceiver!
                            .ReceiveAsync(
                                token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch
                {
                    try
                    {
                        await Task.Delay(
                            250,
                            token);
                    }
                    catch
                    {
                    }

                    continue;
                }

                string text =
                    Encoding.UTF8.GetString(
                        result.Buffer);

                string[] parts =
                    text.Split(
                        '|',
                        StringSplitOptions.None);

                if (
                    parts.Length < 5 ||
                    parts[0] != "MV08D" ||
                    parts[1] !=
                        ProtocolVersion
                            .ToString(
                                CultureInfo.InvariantCulture))
                {
                    continue;
                }

                string code =
                    NormalizeCode(
                        parts[2]);

                if (
                    code.Length != 9 ||
                    code ==
                        NormalizeCode(
                            DeviceCode))
                {
                    continue;
                }

                if (!int.TryParse(
                        parts[3],
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int port) ||
                    !IsValidPort(
                        port))
                {
                    continue;
                }

                lock (_discoverySync)
                {
                    _discoveredPeers[
                        code] =
                        new DiscoveredPeer
                        {
                            Code =
                                code,
                            Name =
                                parts[4],
                            Address =
                                result.RemoteEndPoint
                                    .Address,
                            ControlPort =
                                port,
                            LastSeenTick =
                                Environment.TickCount64
                        };

                    CleanupDiscoveryLocked();
                }
            }
        }

        private async Task DiscoveryBroadcastLoopAsync(
            CancellationToken token)
        {
            IPEndPoint broadcast =
                new(
                    IPAddress.Broadcast,
                    DiscoveryPort);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    string payload =
                        $"MV08D|{ProtocolVersion}|" +
                        $"{NormalizeCode(DeviceCode)}|" +
                        $"{ControlPort}|" +
                        $"{DeviceName}";

                    byte[] data =
                        Encoding.UTF8.GetBytes(
                            payload);

                    if (_discoverySender is not null)
                    {
                        await _discoverySender
                            .SendAsync(
                                data,
                                data.Length,
                                broadcast);
                    }
                }
                catch
                {
                    // Direct connect remains available.
                }

                try
                {
                    await Task.Delay(
                        DiscoveryBroadcastIntervalMs,
                        token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private async Task<ResolvedTarget>
            ResolveTargetAsync(
                string target)
        {
            string trimmed =
                (target ?? "")
                    .Trim();

            if (string.IsNullOrWhiteSpace(
                    trimmed))
            {
                throw new InvalidOperationException(
                    "Digite o código ou endereço do PC remoto.");
            }

            string normalizedDigits =
                NormalizeCode(
                    trimmed);

            if (
                normalizedDigits.Length == 9 &&
                normalizedDigits.All(
                    char.IsDigit))
            {
                lock (_discoverySync)
                {
                    CleanupDiscoveryLocked();

                    if (_discoveredPeers.TryGetValue(
                            normalizedDigits,
                            out DiscoveredPeer? peer))
                    {
                        return new ResolvedTarget
                        {
                            Address =
                                peer.Address,
                            ControlPort =
                                peer.ControlPort,
                            TargetCode =
                                normalizedDigits
                        };
                    }
                }

                string savedCode = NormalizeCode(
                    _settings.GetString("Multiverse.Peer.Code", ""));
                string savedName =
                    _settings.GetString("Multiverse.Peer.Name", "");

                if (string.Equals(savedCode, normalizedDigits, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(savedName))
                {
                    IPAddress? savedAddress = await _tailscale.ResolvePeerAsync(
                        savedName,
                        ControlPort);
                    if (savedAddress is not null)
                    {
                        return new ResolvedTarget
                        {
                            Address = savedAddress,
                            ControlPort = ControlPort,
                            TargetCode = normalizedDigits
                        };
                    }
                }

                throw new InvalidOperationException(
                    "Código não encontrado na LAN nem no peer Tailscale salvo. " +
                    "Use nome-do-pc#código uma vez para memorizar o peer.");
            }

            string endpointPart =
                trimmed;

            string codePart =
                "";

            int hash =
                trimmed.LastIndexOf(
                    '#');

            if (hash >= 0)
            {
                endpointPart =
                    trimmed[..hash];

                codePart =
                    NormalizeCode(
                        trimmed[
                            (hash + 1)..]);
            }

            if (
                codePart.Length != 9 ||
                !codePart.All(
                    char.IsDigit))
            {
                throw new InvalidOperationException(
                    "No modo direto use IP:47820#código. " +
                    "Ex.: 192.168.1.20:47820#123456789");
            }

            endpointPart =
                endpointPart.Trim();

            int port =
                ControlPort;

            string host =
                endpointPart;

            int colon =
                endpointPart.LastIndexOf(
                    ':');

            if (
                colon > 0 &&
                endpointPart.IndexOf(':') ==
                    colon)
            {
                string possiblePort =
                    endpointPart[
                        (colon + 1)..];

                if (int.TryParse(
                        possiblePort,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int parsedPort))
                {
                    if (!IsValidPort(
                            parsedPort))
                    {
                        throw new InvalidOperationException(
                            "Porta inválida.");
                    }

                    port =
                        parsedPort;

                    host =
                        endpointPart[..colon];
                }
            }

            host =
                host.Trim();

            if (host.Length == 0)
            {
                throw new InvalidOperationException(
                    "Endereço remoto vazio.");
            }

            IPAddress address;

            if (!IPAddress.TryParse(host, out IPAddress? parsed))
            {
                address =
                    await _tailscale.ResolvePeerAsync(host, port)
                    ?? throw new InvalidOperationException(
                        "O host não possui um IPv4 atualmente roteável.");
            }
            else
            {
                if (parsed.AddressFamily != AddressFamily.InterNetwork)
                {
                    throw new InvalidOperationException(
                        "CORE 11 usa IPv4 no transporte privado atual.");
                }
                address = parsed;
            }

            return new ResolvedTarget
            {
                Address =
                    address,
                ControlPort =
                    port,
                TargetCode =
                    codePart
            };
        }

        private sealed class ResolvedTarget
        {
            public required IPAddress Address { get; init; }
            public required int ControlPort { get; init; }
            public required string TargetCode { get; init; }
        }

        private void CleanupDiscoveryLocked()
        {
            long now =
                Environment.TickCount64;

            List<string>? expired =
                null;

            foreach (var pair in
                     _discoveredPeers)
            {
                if (
                    now -
                        pair.Value.LastSeenTick >
                    DiscoveryPeerExpiryMs)
                {
                    expired ??=
                        new List<string>();

                    expired.Add(
                        pair.Key);
                }
            }

            if (expired is null)
                return;

            foreach (string key in
                     expired)
            {
                _discoveredPeers.Remove(
                    key);
            }
        }

        private string BuildLocalDirectAddress()
        {
            TailscaleNetworkSnapshot tailscale =
                _tailscale.GetSnapshot();

            if (tailscale.Available)
            {
                return $"{tailscale.CurrentTailscaleIp}:{ControlPort}#" +
                       NormalizeCode(DeviceCode);
            }

            IPAddress? local = GetBestLocalIpv4();
            return local is null
                ? $"IP:{ControlPort}#{NormalizeCode(DeviceCode)}"
                : $"{local}:{ControlPort}#{NormalizeCode(DeviceCode)}";
        }

        private void SavePeerProfile(string? code, string? name)
        {
            string normalized = NormalizeCode(code);
            if (normalized.Length == 9)
                _settings.Set("Multiverse.Peer.Code", normalized);
            if (!string.IsNullOrWhiteSpace(name))
                _settings.Set("Multiverse.Peer.Name", name.Trim());
        }

        private static IPAddress? GetBestLocalIpv4()
        {
            IPAddress? best = null;
            int bestScore = int.MinValue;

            foreach (
                NetworkInterface network in
                NetworkInterface.GetAllNetworkInterfaces())
            {
                if (
                    network.OperationalStatus !=
                        OperationalStatus.Up ||
                    network.NetworkInterfaceType ==
                        NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (
                    UnicastIPAddressInformation unicast in
                    network
                        .GetIPProperties()
                        .UnicastAddresses)
                {
                    IPAddress address =
                        unicast.Address;

                    if (
                        address.AddressFamily !=
                            AddressFamily.InterNetwork ||
                        IPAddress.IsLoopback(address))
                    {
                        continue;
                    }

                    byte[] bytes =
                        address.GetAddressBytes();

                    if (bytes[0] == 169 &&
                        bytes[1] == 254)
                    {
                        continue;
                    }

                    int score = 0;

                    // Tailscale usa 100.64.0.0/10. Quando Radmin e Tailscale
                    // coexistem, preferimos explicitamente o caminho Tailscale.
                    bool tailscale =
                        bytes[0] == 100 &&
                        bytes[1] >= 64 &&
                        bytes[1] <= 127;

                    bool privateLan =
                        bytes[0] == 10 ||
                        (bytes[0] == 172 &&
                         bytes[1] >= 16 &&
                         bytes[1] <= 31) ||
                        (bytes[0] == 192 &&
                         bytes[1] == 168);

                    bool radminLike =
                        bytes[0] == 26;

                    if (tailscale)
                        score += 1000;
                    else if (privateLan)
                        score += 700;
                    else if (radminLike)
                        score += 100;
                    else
                        score += 300;

                    if (network.NetworkInterfaceType ==
                            NetworkInterfaceType.Ethernet ||
                        network.NetworkInterfaceType ==
                            NetworkInterfaceType.Wireless80211)
                    {
                        score += 50;
                    }

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = address;
                    }
                }
            }

            return best;
        }

        private static ControlEnvelope? Deserialize(
            string? line)
        {
            if (string.IsNullOrWhiteSpace(
                    line))
            {
                return null;
            }

            try
            {
                return JsonSerializer
                    .Deserialize<ControlEnvelope>(
                        line);
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizeCode(
            string? value)
        {
            if (string.IsNullOrWhiteSpace(
                    value))
            {
                return "";
            }

            StringBuilder builder =
                new(9);

            foreach (char c in value)
            {
                if (char.IsDigit(c))
                    builder.Append(c);
            }

            return builder.ToString();
        }

        private static string FormatCode(
            string digits)
        {
            if (digits.Length != 9)
                return digits;

            return
                $"{digits[..3]} " +
                $"{digits.Substring(3, 3)} " +
                $"{digits.Substring(6, 3)}";
        }

        private static string PairingAddressKey(IPAddress address) =>
            address.MapToIPv6().ToString();

        private bool IsPairingThrottled(IPAddress address)
        {
            string key = PairingAddressKey(address);
            long now = Environment.TickCount64;
            lock (_pairingSync)
            {
                if (!_pairingAttempts.TryGetValue(key, out PairingAttemptState? state))
                    return false;
                if (state.BlockedUntil > now)
                    return true;
                if (state.BlockedUntil != 0 || now - state.WindowStartedAt > PairingFailureWindowMs)
                {
                    _pairingAttempts.Remove(key);
                    return false;
                }
                return false;
            }
        }

        private void RegisterPairingFailure(IPAddress address)
        {
            string key = PairingAddressKey(address);
            long now = Environment.TickCount64;
            lock (_pairingSync)
            {
                if (!_pairingAttempts.TryGetValue(key, out PairingAttemptState? state) ||
                    now - state.WindowStartedAt > PairingFailureWindowMs)
                {
                    state = new PairingAttemptState { WindowStartedAt = now };
                    _pairingAttempts[key] = state;
                }
                state.Failures++;
                if (state.Failures >= PairingFailureLimit)
                    state.BlockedUntil = now + PairingBlockMs;

                // Bound the throttle table itself. Stale entries are safe to drop.
                if (_pairingAttempts.Count > 256)
                {
                    foreach (string stale in _pairingAttempts
                        .Where(pair => pair.Value.BlockedUntil <= now &&
                            now - pair.Value.WindowStartedAt > PairingFailureWindowMs)
                        .Select(pair => pair.Key).Take(64).ToArray())
                        _pairingAttempts.Remove(stale);
                }
            }
        }

        private void ResetPairingFailures(IPAddress address)
        {
            lock (_pairingSync)
                _pairingAttempts.Remove(PairingAddressKey(address));
        }

        private static bool IsValidPort(
            int port) =>
            port is > 0 and <= 65535;

        private void RaiseSessionChanged()
        {
            NotifySessionHandlersSafely(SessionChanged);
        }

        private void RecordCallbackError(string name, Exception ex)
        {
            lock (_sync)
                _lastError = $"Callback {name}: {ex.Message}";
        }

        private void NotifySessionHandlersSafely(EventHandler? handlers)
        {
            if (handlers is null) return;
            foreach (EventHandler handler in handlers.GetInvocationList())
            {
                try { handler(this, EventArgs.Empty); }
                catch (Exception ex) { RecordCallbackError("sessão", ex); }
            }
        }

        private void NotifyIdrHandlersSafely()
        {
            Action? handlers = RemoteIdrRequested;
            if (handlers is null) return;
            foreach (Action handler in handlers.GetInvocationList())
            {
                try { handler(); }
                catch (Exception ex) { RecordCallbackError("IDR", ex); }
            }
        }

        private void NotifyGamepadHandlersSafely(RemoteGamepadPacket packet)
        {
            Action<RemoteGamepadPacket>? handlers = RemoteGamepadReceived;
            if (handlers is null) return;
            foreach (Action<RemoteGamepadPacket> handler in handlers.GetInvocationList())
            {
                try { handler(packet); }
                catch (Exception ex)
                {
                    RecordCallbackError("gamepad", ex);
                    try { RemoteGamepadService.Instance.NeutralizeNow(); } catch { }
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _tailscale.NetworkChanged -= Tailscale_NetworkChanged;
            _friendStore.FriendsChanged -= FriendStore_FriendsChanged;

            try
            {
                _lifetimeCancel?.Cancel();
            }
            catch
            {
            }

            lock (_sync)
            {
                CloseActiveSocketLocked();

                try
                {
                    _listener?.Stop();
                }
                catch
                {
                }

                _listener = null;
                _started = false;
            }

            _discoveryReceiver?.Dispose();
            _discoveryReceiver = null;

            _discoverySender?.Dispose();
            _discoverySender = null;

            _lifetimeCancel?.Dispose();
            _lifetimeCancel = null;

            GC.SuppressFinalize(this);
        }
    }
}
