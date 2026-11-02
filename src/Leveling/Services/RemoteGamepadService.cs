using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Windows.Gaming.Input;

namespace Multiverse.Services
{
    internal sealed class RemoteGamepadServiceSnapshot
    {
        public bool LocalControllerConnected { get; init; }
        public int LocalControllerIndex { get; init; } = -1;
        public string LocalControllerName { get; init; } = "";
        public long TransmittedPackets { get; init; }
        public long ReceivedPackets { get; init; }
        public bool VirtualControllerReady { get; init; }
        public bool RemoteControllerConnected { get; init; }
        public bool StaleNeutralActive { get; init; }
        public double RemotePacketAgeMs { get; init; }
        public ushort Buttons { get; init; }
        public byte LeftTrigger { get; init; }
        public byte RightTrigger { get; init; }
        public short LeftThumbX { get; init; }
        public short LeftThumbY { get; init; }
        public short RightThumbX { get; init; }
        public short RightThumbY { get; init; }
        public bool RemoteInputEnabled { get; init; } = true;
        public int RequestedHostSlot { get; init; }
        public int ActualHostSlot { get; init; } = -1;
        public bool HostSlotConflict { get; init; }
        public int ReservationCount { get; init; }
        public string Status { get; init; } = "Aguardando sessão.";
        public string LastError { get; init; } = "";
    }

    internal sealed class LocalRawGamepadSnapshot
    {
        public bool Connected { get; init; }
        public int UserIndex { get; init; } = -1;
        public string ControllerName { get; init; } = "";
        public string ControllerIdentity { get; init; } = "";
        public string Backend { get; init; } = "";
        public ushort Buttons { get; init; }
        public byte LeftTrigger { get; init; }
        public byte RightTrigger { get; init; }
        public short LeftThumbX { get; init; }
        public short LeftThumbY { get; init; }
        public short RightThumbX { get; init; }
        public short RightThumbY { get; init; }
        public double[] RawAxes { get; init; } = Array.Empty<double>();
        public int RawAxisCount { get; init; }
        public int RawButtonCount { get; init; }
        public int RawSwitchCount { get; init; }
        public ushort HardwareVendorId { get; init; }
        public ushort HardwareProductId { get; init; }
    }

    /// <summary>
    /// CORE 11.23 INPUT.
    /// Client polling stays isolated at 250 Hz. Host materializes latest input through
    /// ViGEm and can reserve lower XInput slots so a remote pad lands strictly on P1-P4.
    /// The native video pipeline is intentionally not referenced here.
    /// </summary>
    internal sealed class RemoteGamepadService : IDisposable
    {
        private static readonly Lazy<RemoteGamepadService> LazyInstance =
            new(() => new RemoteGamepadService());

        public static RemoteGamepadService Instance => LazyInstance.Value;
        internal static bool IsValueCreated => LazyInstance.IsValueCreated;

        private const uint ErrorSuccess = 0;
        private const int StaleInputTimeoutMs = 250;
        private const int ConnectedHeartbeatMs = 100;
        private const int ViGEmRetryMs = 2000;
        private const int SlotEnumerationWaitMs = 220;

        private const ushort XInputDpadUp = 0x0001;
        private const ushort XInputDpadDown = 0x0002;
        private const ushort XInputDpadLeft = 0x0004;
        private const ushort XInputDpadRight = 0x0008;
        private const ushort XInputStart = 0x0010;
        private const ushort XInputBack = 0x0020;
        private const ushort XInputLeftThumb = 0x0040;
        private const ushort XInputRightThumb = 0x0080;
        private const ushort XInputLeftShoulder = 0x0100;
        private const ushort XInputRightShoulder = 0x0200;
        private const ushort XInputA = 0x1000;
        private const ushort XInputB = 0x2000;
        private const ushort XInputX = 0x4000;
        private const ushort XInputY = 0x8000;

        private const ushort SonyVendorId = 0x054C;
        private const ushort DualSenseProductId = 0x0CE6;
        private const ushort DualSenseEdgeProductId = 0x0DF2;

        private readonly object _sync = new();
        private readonly object _virtualSync = new();
        private readonly object _inputReadSync = new();
        private readonly RemoteSessionService _session =
            RemoteSessionService.Instance;
        private readonly GamepadTuningService _tuning =
            GamepadTuningService.Instance;
        private readonly CancellationTokenSource _cancel = new();
        private readonly Channel<RemoteGamepadPacket> _remoteInbound =
            Channel.CreateBounded<RemoteGamepadPacket>(
                new BoundedChannelOptions(1)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.DropOldest,
                    AllowSynchronousContinuations = false
                });

        private readonly Task _worker;

        private int _localControllerIndex = -1;
        private bool _localControllerConnected;
        private string _localControllerName = "";
        private LocalControllerBackend _localControllerBackend;
        private uint _lastXInputPacketNumber;
        private Gamepad? _windowsGamepad;
        private XInputGamepad _lastWindowsGamepadState;
        private bool _hasLastWindowsGamepadState;
        private uint _windowsGamepadPacketNumber;
        private RawGameController? _dualSenseRawController;
        private string _dualSenseRawControllerId = "";
        private string _dualSenseRawDisplayName = "DualSense";
        private bool[]? _dualSenseRawButtons;
        private GameControllerSwitchPosition[]? _dualSenseRawSwitches;
        private double[]? _dualSenseRawAxes;
        // Diagnostic RAW reader is intentionally separate from the normal streaming
        // conversion. It accepts the Sony device without assuming axis indexes and
        // exposes the unnamed normalized axes to the guided calibration wizard.
        private RawGameController? _diagnosticRawController;
        private string _diagnosticRawControllerId = "";
        private bool[]? _diagnosticRawButtons;
        private GameControllerSwitchPosition[]? _diagnosticRawSwitches;
        private double[]? _diagnosticRawAxes;
        private XInputGamepad _lastDualSenseRawState;
        private bool _hasLastDualSenseRawState;
        private uint _dualSenseRawPacketNumber;
        private long _lastClientSendAt;
        private long _sequence;
        private long _txPackets;

        private long _rxPackets;
        private long _lastRemotePacketAt;
        private bool _remoteControllerConnected;
        private bool _staleNeutralActive;
        private ushort _lastButtons;
        private byte _lastLeftTrigger;
        private byte _lastRightTrigger;
        private short _lastLeftThumbX;
        private short _lastLeftThumbY;
        private short _lastRightThumbX;
        private short _lastRightThumbY;
        private string _status = "Aguardando sessão.";
        private string _lastError = "";

        private int _requestedHostSlot;
        private int _actualHostSlot = -1;
        private bool _hostSlotConflict;
        private int _reservationCount;
        private bool _rebindRequested;
        private bool _neutralizeRequested;
        private int _diagnosticCaptureDepth;
        private long _diagnosticNeutralUntil;
        private long _lastDiagnosticNeutralSendAt;

        private ViGEmClient? _vigemClient;
        private IXbox360Controller? _virtualController;
        private readonly List<IXbox360Controller> _slotReservations = new();
        private int _boundHostSlotPreference = -1;
        private long _nextVigemRetryAt;
        private bool _disposed;

        private RemoteGamepadService()
        {
            _session.RemoteGamepadReceived += Session_RemoteGamepadReceived;
            _worker = Task.Run(() => WorkerLoopAsync(_cancel.Token));
        }

        public RemoteGamepadServiceSnapshot GetSnapshot()
        {
            lock (_sync)
            {
                long now = Environment.TickCount64;
                return new RemoteGamepadServiceSnapshot
                {
                    LocalControllerConnected = _localControllerConnected,
                    LocalControllerIndex = _localControllerIndex,
                    LocalControllerName = _localControllerName,
                    TransmittedPackets = _txPackets,
                    ReceivedPackets = _rxPackets,
                    VirtualControllerReady = _actualHostSlot > 0 || _virtualController is not null,
                    RemoteControllerConnected = _remoteControllerConnected,
                    StaleNeutralActive = _staleNeutralActive,
                    RemotePacketAgeMs = _lastRemotePacketAt == 0
                        ? 0
                        : Math.Max(0, now - _lastRemotePacketAt),
                    Buttons = _lastButtons,
                    LeftTrigger = _lastLeftTrigger,
                    RightTrigger = _lastRightTrigger,
                    LeftThumbX = _lastLeftThumbX,
                    LeftThumbY = _lastLeftThumbY,
                    RightThumbX = _lastRightThumbX,
                    RightThumbY = _lastRightThumbY,
                    RemoteInputEnabled = _tuning.RemoteInputEnabled,
                    RequestedHostSlot = _requestedHostSlot,
                    ActualHostSlot = _actualHostSlot,
                    HostSlotConflict = _hostSlotConflict,
                    ReservationCount = _reservationCount,
                    Status = _status,
                    LastError = _lastError
                };
            }
        }

        private void Session_RemoteGamepadReceived(RemoteGamepadPacket packet)
        {
            // Never run ViGEm calls on the TCP control-reader thread.
            _remoteInbound.Writer.TryWrite(packet);
        }

        private async Task WorkerLoopAsync(CancellationToken token)
        {
            using PeriodicTimer timer =
                new(TimeSpan.FromMilliseconds(4));

            try
            {
                while (await timer.WaitForNextTickAsync(token))
                {
                    RemoteSessionSnapshot session = _session.GetSnapshot();

                    if (ConsumeNeutralizeRequest())
                    {
                        lock (_virtualSync)
                        {
                            ApplyNeutralReportLocked();
                        }
                        lock (_sync)
                        {
                            ClearDisplayedStateLocked();
                            _staleNeutralActive = true;
                            _status = "Controle neutralizado manualmente.";
                        }
                    }

                    if (!session.Connected)
                    {
                        if (!IsDiagnosticCaptureActive)
                            ResetClientState();
                        DisconnectVirtualTarget();
                        SetStatus(
                            IsDiagnosticCaptureActive
                                ? "DIAGNÓSTICO • leitura RAW local • offline"
                                : "Aguardando sessão.",
                            "");
                        DrainRemoteQueue();
                        continue;
                    }

                    if (session.StreamConfig is null)
                    {
                        if (!IsDiagnosticCaptureActive)
                            ResetClientState();
                        DisconnectVirtualTarget();
                        SetStatus(
                            IsDiagnosticCaptureActive
                                ? "DIAGNÓSTICO • leitura RAW local • sessão sem stream"
                                : "Aguardando transmissão.",
                            "");
                        DrainRemoteQueue();
                        continue;
                    }

                    if (session.Role == RemoteSessionRole.Client)
                    {
                        if (DesktopInputService.Instance.Mode != DesktopInputMode.Gamepad)
                            PublishKeyboardMouseHeartbeat();
                        else if (IsDiagnosticCaptureActive)
                            PublishDiagnosticNeutralHeartbeat();
                        else
                            PollClientController();
                        DisconnectVirtualTarget();
                        DrainRemoteQueue();
                        continue;
                    }

                    ResetClientState();

                    if (session.Role == RemoteSessionRole.Host)
                    {
                        bool rebindRequested = ConsumeRebindRequest();
                        if (rebindRequested ||
                            (_virtualController is not null &&
                             _boundHostSlotPreference != _tuning.HostPlayerSlot))
                        {
                            DisconnectVirtualTarget();
                            if (rebindRequested)
                                _nextVigemRetryAt = 0;
                        }

                        if (!_tuning.RemoteInputEnabled)
                        {
                            DisconnectVirtualTarget();
                            SetStatus("HOST • input remoto desativado", "");
                            DrainRemoteQueue();
                            continue;
                        }

                        ProcessNewestRemotePacket();
                        EnforceHostWatchdog();
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                DisconnectVirtualTarget();
            }
        }

        private void PollClientController()
        {
            if (!_tuning.RemoteInputEnabled)
            {
                PublishClientDisconnectedIfNeeded("CLIENTE • input remoto desativado");
                return;
            }

            long now = Environment.TickCount64;
            lock (_inputReadSync)
            {
                if (!TryReadConfiguredRawController(out RawControllerRead read, out string missingStatus))
                {
                    PublishClientDisconnectedIfNeeded(missingStatus);
                    return;
                }

                switch (read.Backend)
                {
                    case LocalControllerBackend.XInput:
                        PublishXInputClientState(
                            now,
                            read.UserIndex,
                            new XInputState { PacketNumber = read.PacketNumber, Gamepad = read.Gamepad });
                        break;
                    case LocalControllerBackend.WindowsGamepad:
                        PublishWindowsGamepadClientState(now, read.Gamepad, read.DeviceChanged);
                        break;
                    case LocalControllerBackend.DualSenseRaw:
                        PublishDualSenseRawClientState(now, read.Gamepad, read.DeviceChanged);
                        break;
                }
            }
        }

        public bool IsDiagnosticCaptureActive =>
            Volatile.Read(ref _diagnosticCaptureDepth) > 0 ||
            Environment.TickCount64 < Volatile.Read(ref _diagnosticNeutralUntil);

        public IDisposable BeginDiagnosticCapture()
        {
            Interlocked.Exchange(ref _diagnosticNeutralUntil, 0);
            Interlocked.Increment(ref _diagnosticCaptureDepth);
            Interlocked.Exchange(ref _lastDiagnosticNeutralSendAt, 0);
            lock (_sync)
            {
                _status = "CLIENTE • diagnóstico local • saída remota neutralizada";
                ClearDisplayedStateLocked();
            }
            return new DiagnosticCaptureScope(this);
        }

        public void ResetLocalDiagnosticDeviceState()
        {
            lock (_inputReadSync)
            {
                ClearDiagnosticRawState();
            }
        }

        public bool TryGetLocalRawSnapshot(out LocalRawGamepadSnapshot snapshot)
        {
            lock (_inputReadSync)
            {
                if (!TryReadConfiguredRawController(out RawControllerRead read, out _))
                {
                    snapshot = new LocalRawGamepadSnapshot { Connected = false };
                    return false;
                }

                snapshot = new LocalRawGamepadSnapshot
                {
                    Connected = true,
                    UserIndex = read.UserIndex,
                    ControllerName = read.ControllerName,
                    ControllerIdentity = read.Backend switch
                    {
                        LocalControllerBackend.XInput => $"XInput:{read.UserIndex}",
                        LocalControllerBackend.DualSenseRaw => string.IsNullOrWhiteSpace(_dualSenseRawControllerId) ? "DualSenseRaw" : _dualSenseRawControllerId,
                        LocalControllerBackend.WindowsGamepad => GetWindowsGamepadIdentity(),
                        _ => read.ControllerName
                    },
                    Backend = read.Backend.ToString(),
                    Buttons = read.Gamepad.Buttons,
                    LeftTrigger = read.Gamepad.LeftTrigger,
                    RightTrigger = read.Gamepad.RightTrigger,
                    LeftThumbX = read.Gamepad.LeftThumbX,
                    LeftThumbY = read.Gamepad.LeftThumbY,
                    RightThumbX = read.Gamepad.RightThumbX,
                    RightThumbY = read.Gamepad.RightThumbY
                };
                return true;
            }
        }

        public bool TryGetLocalDiagnosticSnapshot(out LocalRawGamepadSnapshot snapshot)
        {
            lock (_inputReadSync)
            {
                int configuredSource = _tuning.ClientControllerSource;

                // Explicit DualSense diagnosis: prefer the named Gamepad contract,
                // then fall back to interactive RAW discovery without assuming axes.
                if (configuredSource == -2)
                {
                    if (TryGetWindowsDualSenseGamepadState(out XInputGamepad namedDualSense, out bool namedChanged))
                    {
                        var read = new RawControllerRead(
                            LocalControllerBackend.WindowsGamepad, -1, namedDualSense,
                            _windowsGamepadPacketNumber, namedChanged, "DualSense • Gamepad tipado");
                        snapshot = CreateRawSnapshot(read);
                        return true;
                    }

                    if (TryGetDualSenseRawDiagnosticSnapshot(out snapshot))
                        return true;

                    snapshot = new LocalRawGamepadSnapshot { Connected = false };
                    return false;
                }

                // Explicit XInput must remain explicit. Do not silently switch devices.
                if (configuredSource >= 0)
                {
                    if (!TryReadConfiguredRawController(out RawControllerRead explicitRead, out _))
                    {
                        snapshot = new LocalRawGamepadSnapshot { Connected = false };
                        return false;
                    }
                    snapshot = CreateRawSnapshot(explicitRead);
                    return true;
                }

                // AUTO keeps its existing priority. If the normal path can name the
                // controller safely, use it. If Sony only surfaces as RAW, hand the
                // unnamed axes to the interactive diagnostic mapper instead.
                if (TryReadConfiguredRawController(out RawControllerRead configuredRead, out _))
                {
                    if (configuredRead.Backend != LocalControllerBackend.DualSenseRaw)
                    {
                        snapshot = CreateRawSnapshot(configuredRead);
                        return true;
                    }

                    if (TryGetDualSenseRawDiagnosticSnapshot(out snapshot))
                        return true;
                }
                else if (TryGetDualSenseRawDiagnosticSnapshot(out snapshot))
                {
                    return true;
                }

                snapshot = new LocalRawGamepadSnapshot { Connected = false };
                return false;
            }
        }

        public string GetLocalDiagnosticDiscoverySummary()
        {
            lock (_inputReadSync)
            {
                int xinput = 0;
                for (uint index = 0; index < 4; index++)
                {
                    if (TryGetXInputState(index, out _))
                        xinput++;
                }

                int gamepads = 0;
                int sonyRaw = 0;
                try { gamepads = Gamepad.Gamepads.Count; } catch { }
                try
                {
                    foreach (RawGameController candidate in RawGameController.RawGameControllers)
                    {
                        if (IsDiagnosticDualSenseCandidate(candidate))
                            sonyRaw++;
                    }
                }
                catch { }

                return $"Detectados pelo Windows: XInput {xinput} • Gamepad {gamepads} • Sony HID {sonyRaw}";
            }
        }

        private LocalRawGamepadSnapshot CreateRawSnapshot(RawControllerRead read)
        {
            return new LocalRawGamepadSnapshot
            {
                Connected = true,
                UserIndex = read.UserIndex,
                ControllerName = read.ControllerName,
                ControllerIdentity = read.Backend switch
                {
                    LocalControllerBackend.XInput => $"XInput:{read.UserIndex}",
                    LocalControllerBackend.DualSenseRaw => string.IsNullOrWhiteSpace(_dualSenseRawControllerId) ? "DualSenseRaw" : _dualSenseRawControllerId,
                    LocalControllerBackend.WindowsGamepad => GetWindowsGamepadIdentity(),
                    _ => read.ControllerName
                },
                Backend = read.Backend == LocalControllerBackend.DualSenseRaw
                    ? "DualSenseRawFallback"
                    : read.Backend.ToString(),
                Buttons = read.Gamepad.Buttons,
                LeftTrigger = read.Gamepad.LeftTrigger,
                RightTrigger = read.Gamepad.RightTrigger,
                LeftThumbX = read.Gamepad.LeftThumbX,
                LeftThumbY = read.Gamepad.LeftThumbY,
                RightThumbX = read.Gamepad.RightThumbX,
                RightThumbY = read.Gamepad.RightThumbY
            };
        }

        private bool TryReadConfiguredRawController(out RawControllerRead read, out string missingStatus)
        {
            read = default;
            missingStatus = "CLIENTE • nenhum XInput ou DualSense compatível detectado";
            int configuredSource = _tuning.ClientControllerSource;

            if (configuredSource == -2)
            {
                if (TryGetDualSenseRawState(out XInputGamepad rawDualSense, out bool rawChanged))
                {
                    read = new RawControllerRead(
                        LocalControllerBackend.DualSenseRaw, -1, rawDualSense,
                        _dualSenseRawPacketNumber, rawChanged,
                        string.IsNullOrWhiteSpace(_dualSenseRawDisplayName) ? "DualSense" : _dualSenseRawDisplayName);
                    return true;
                }

                if (TryGetWindowsDualSenseGamepadState(out XInputGamepad windowsDualSense, out bool windowsChanged))
                {
                    read = new RawControllerRead(
                        LocalControllerBackend.WindowsGamepad, -1, windowsDualSense,
                        _windowsGamepadPacketNumber, windowsChanged, "DualSense/HID");
                    return true;
                }

                missingStatus = "CLIENTE • DualSense/HID Sony não detectado";
                return false;
            }

            if (configuredSource >= 0)
            {
                if (!TryGetXInputState((uint)configuredSource, out XInputState configuredState))
                {
                    missingStatus = $"CLIENTE • XInput #{configuredSource + 1} não detectado";
                    return false;
                }

                read = new RawControllerRead(
                    LocalControllerBackend.XInput, configuredSource, configuredState.Gamepad,
                    configuredState.PacketNumber, false, $"XInput {configuredSource + 1}");
                return true;
            }

            if (_localControllerBackend == LocalControllerBackend.WindowsGamepad &&
                TryGetWindowsGamepadState(out XInputGamepad currentWindows, out bool currentWindowsChanged))
            {
                read = new RawControllerRead(
                    LocalControllerBackend.WindowsGamepad, -1, currentWindows,
                    _windowsGamepadPacketNumber, currentWindowsChanged, "DualSense/HID");
                return true;
            }

            if (_localControllerBackend == LocalControllerBackend.DualSenseRaw &&
                TryGetDualSenseRawState(out XInputGamepad currentDualSense, out bool currentDualSenseChanged))
            {
                read = new RawControllerRead(
                    LocalControllerBackend.DualSenseRaw, -1, currentDualSense,
                    _dualSenseRawPacketNumber, currentDualSenseChanged,
                    string.IsNullOrWhiteSpace(_dualSenseRawDisplayName) ? "DualSense" : _dualSenseRawDisplayName);
                return true;
            }

            int selectedIndex = _localControllerBackend == LocalControllerBackend.XInput
                ? _localControllerIndex
                : -1;
            XInputState state = default;
            bool connected = selectedIndex >= 0 && TryGetXInputState((uint)selectedIndex, out state);

            if (!connected)
            {
                selectedIndex = -1;
                for (uint index = 0; index < 4; ++index)
                {
                    if (!TryGetXInputState(index, out state))
                        continue;
                    selectedIndex = (int)index;
                    connected = true;
                    break;
                }
            }

            if (connected)
            {
                read = new RawControllerRead(
                    LocalControllerBackend.XInput, selectedIndex, state.Gamepad,
                    state.PacketNumber, false, $"XInput {selectedIndex + 1}");
                return true;
            }

            if (TryGetWindowsGamepadState(out XInputGamepad windowsState, out bool deviceChanged))
            {
                read = new RawControllerRead(
                    LocalControllerBackend.WindowsGamepad, -1, windowsState,
                    _windowsGamepadPacketNumber, deviceChanged, "DualSense/HID");
                return true;
            }

            if (TryGetDualSenseRawState(out XInputGamepad dualSenseState, out bool dualSenseChanged))
            {
                read = new RawControllerRead(
                    LocalControllerBackend.DualSenseRaw, -1, dualSenseState,
                    _dualSenseRawPacketNumber, dualSenseChanged,
                    string.IsNullOrWhiteSpace(_dualSenseRawDisplayName) ? "DualSense" : _dualSenseRawDisplayName);
                return true;
            }

            return false;
        }

        private void PublishDiagnosticNeutralHeartbeat()
        {
            long now = Environment.TickCount64;
            long lastNeutralSendAt = Volatile.Read(ref _lastDiagnosticNeutralSendAt);
            if (lastNeutralSendAt != 0 &&
                now - lastNeutralSendAt < ConnectedHeartbeatMs)
                return;

            Interlocked.Exchange(ref _lastDiagnosticNeutralSendAt, now);
            lock (_sync)
            {
                ClearDisplayedStateLocked();
                _status = "CLIENTE • diagnóstico local • saída remota neutralizada";
                _lastError = "";
            }

            if (!_tuning.RemoteInputEnabled)
                return;

            PublishClientState(new RemoteGamepadPacket
            {
                Connected = true,
                UserIndex = _localControllerIndex,
                PacketNumber = 0,
                Buttons = 0,
                LeftTrigger = 0,
                RightTrigger = 0,
                LeftThumbX = 0,
                LeftThumbY = 0,
                RightThumbX = 0,
                RightThumbY = 0,
                Sequence = Interlocked.Increment(ref _sequence)
            });
        }

        private void PublishKeyboardMouseHeartbeat()
        {
            long now=Environment.TickCount64;
            if(now-Volatile.Read(ref _lastDiagnosticNeutralSendAt)<ConnectedHeartbeatMs)return;
            Interlocked.Exchange(ref _lastDiagnosticNeutralSendAt,now);
            lock(_sync)
            {
                _localControllerConnected=false;
                ClearDisplayedStateLocked();
                _status="CLIENTE • teclado e mouse • controle remoto desconectado";
            }
            // A neutral connected pad still exists in the game. Remove the remote
            // target while using keyboard/mouse, and force a fresh pad on return.
            PublishClientState(new RemoteGamepadPacket {Connected=false,UserIndex=-1,
                Sequence=Interlocked.Increment(ref _sequence)});
        }

        private void EndDiagnosticCapture()
        {
            int remaining = Interlocked.Decrement(ref _diagnosticCaptureDepth);
            if (remaining < 0)
            {
                Interlocked.Exchange(ref _diagnosticCaptureDepth, 0);
                remaining = 0;
            }
            if (remaining == 0)
            {
                Interlocked.Exchange(ref _lastDiagnosticNeutralSendAt, 0);
                Interlocked.Exchange(ref _diagnosticNeutralUntil, Environment.TickCount64 + 750);
            }
        }

        private sealed class DiagnosticCaptureScope : IDisposable
        {
            private RemoteGamepadService? _owner;

            public DiagnosticCaptureScope(RemoteGamepadService owner) => _owner = owner;

            public void Dispose()
            {
                RemoteGamepadService? owner = Interlocked.Exchange(ref _owner, null);
                owner?.EndDiagnosticCapture();
            }
        }

        private readonly struct RawControllerRead
        {
            public RawControllerRead(
                LocalControllerBackend backend, int userIndex, XInputGamepad gamepad,
                uint packetNumber, bool deviceChanged, string controllerName)
            {
                Backend = backend;
                UserIndex = userIndex;
                Gamepad = gamepad;
                PacketNumber = packetNumber;
                DeviceChanged = deviceChanged;
                ControllerName = controllerName;
            }

            public LocalControllerBackend Backend { get; }
            public int UserIndex { get; }
            public XInputGamepad Gamepad { get; }
            public uint PacketNumber { get; }
            public bool DeviceChanged { get; }
            public string ControllerName { get; }
        }

        private void PublishXInputClientState(long now, int selectedIndex, XInputState state)
        {
            bool connectionChanged =
                !_localControllerConnected ||
                _localControllerBackend != LocalControllerBackend.XInput ||
                selectedIndex != _localControllerIndex;
            bool changed =
                connectionChanged ||
                state.PacketNumber != _lastXInputPacketNumber;
            bool heartbeatDue =
                _lastClientSendAt == 0 ||
                now - _lastClientSendAt >= ConnectedHeartbeatMs;

            lock (_sync)
            {
                _localControllerConnected = true;
                _localControllerIndex = selectedIndex;
                _localControllerName = $"XInput {selectedIndex + 1}";
                _localControllerBackend = LocalControllerBackend.XInput;
                _status = $"CLIENTE • XInput #{selectedIndex + 1} ativo • 250 Hz";
                _lastError = "";
            }

            _windowsGamepad = null;
            _hasLastWindowsGamepadState = false;
            _windowsGamepadPacketNumber = 0;
            ClearDualSenseRawState();

            if (!changed && !heartbeatDue)
                return;

            _lastXInputPacketNumber = state.PacketNumber;
            _lastClientSendAt = now;

            XInputGamepad effective = ApplyTuning(state.Gamepad);
            PublishEffectiveClientGamepad(
                effective,
                selectedIndex,
                state.PacketNumber);
        }

        private void PublishWindowsGamepadClientState(
            long now,
            XInputGamepad rawState,
            bool deviceChanged)
        {
            bool connectionChanged =
                !_localControllerConnected ||
                _localControllerBackend != LocalControllerBackend.WindowsGamepad ||
                deviceChanged;
            bool changed =
                connectionChanged ||
                !_hasLastWindowsGamepadState ||
                !GamepadStateEquals(rawState, _lastWindowsGamepadState);
            bool heartbeatDue =
                _lastClientSendAt == 0 ||
                now - _lastClientSendAt >= ConnectedHeartbeatMs;

            lock (_sync)
            {
                _localControllerConnected = true;
                _localControllerIndex = -1;
                _localControllerName = "DualSense/HID";
                _localControllerBackend = LocalControllerBackend.WindowsGamepad;
                _status = "CLIENTE • DualSense/HID via Windows Gamepad • 250 Hz";
                _lastError = "";
            }

            _lastXInputPacketNumber = 0;
            if (!changed && !heartbeatDue)
                return;

            if (changed)
                _windowsGamepadPacketNumber++;
            _lastWindowsGamepadState = rawState;
            _hasLastWindowsGamepadState = true;
            _lastClientSendAt = now;

            XInputGamepad effective = ApplyTuning(rawState);
            PublishEffectiveClientGamepad(
                effective,
                -1,
                _windowsGamepadPacketNumber);
        }

        private void PublishDualSenseRawClientState(
            long now,
            XInputGamepad rawState,
            bool deviceChanged)
        {
            bool connectionChanged =
                !_localControllerConnected ||
                _localControllerBackend != LocalControllerBackend.DualSenseRaw ||
                deviceChanged;
            bool changed =
                connectionChanged ||
                !_hasLastDualSenseRawState ||
                !GamepadStateEquals(rawState, _lastDualSenseRawState);
            bool heartbeatDue =
                _lastClientSendAt == 0 ||
                now - _lastClientSendAt >= ConnectedHeartbeatMs;

            string displayName = string.IsNullOrWhiteSpace(_dualSenseRawDisplayName)
                ? "DualSense"
                : _dualSenseRawDisplayName;

            lock (_sync)
            {
                _localControllerConnected = true;
                _localControllerIndex = -1;
                _localControllerName = "DualSense";
                _localControllerBackend = LocalControllerBackend.DualSenseRaw;
                _status = $"CLIENTE • {displayName} direto (HID/WGI) • 250 Hz";
                _lastError = "";
            }

            _lastXInputPacketNumber = 0;
            _windowsGamepad = null;
            _hasLastWindowsGamepadState = false;
            _windowsGamepadPacketNumber = 0;

            if (!changed && !heartbeatDue)
                return;

            if (changed)
                _dualSenseRawPacketNumber++;
            _lastDualSenseRawState = rawState;
            _hasLastDualSenseRawState = true;
            _lastClientSendAt = now;

            XInputGamepad effective = ApplyTuning(rawState);
            PublishEffectiveClientGamepad(
                effective,
                -1,
                _dualSenseRawPacketNumber);
        }

        private void PublishEffectiveClientGamepad(
            XInputGamepad effective,
            int userIndex,
            uint packetNumber)
        {
            lock (_sync)
            {
                _lastButtons = effective.Buttons;
                _lastLeftTrigger = effective.LeftTrigger;
                _lastRightTrigger = effective.RightTrigger;
                _lastLeftThumbX = effective.LeftThumbX;
                _lastLeftThumbY = effective.LeftThumbY;
                _lastRightThumbX = effective.RightThumbX;
                _lastRightThumbY = effective.RightThumbY;
            }

            PublishClientState(
                new RemoteGamepadPacket
                {
                    Connected = true,
                    UserIndex = userIndex,
                    PacketNumber = packetNumber,
                    Buttons = effective.Buttons,
                    LeftTrigger = effective.LeftTrigger,
                    RightTrigger = effective.RightTrigger,
                    LeftThumbX = effective.LeftThumbX,
                    LeftThumbY = effective.LeftThumbY,
                    RightThumbX = effective.RightThumbX,
                    RightThumbY = effective.RightThumbY,
                    Sequence = Interlocked.Increment(ref _sequence)
                });
        }

        private void PublishClientDisconnectedIfNeeded(string status)
        {
            bool wasConnected;
            lock (_sync)
            {
                wasConnected = _localControllerConnected;
                _localControllerConnected = false;
                _localControllerIndex = -1;
                _localControllerName = "";
                _localControllerBackend = LocalControllerBackend.None;
                _lastXInputPacketNumber = 0;
                _windowsGamepad = null;
                _hasLastWindowsGamepadState = false;
                _windowsGamepadPacketNumber = 0;
                _dualSenseRawController = null;
                _dualSenseRawControllerId = "";
                _dualSenseRawDisplayName = "DualSense";
                _dualSenseRawButtons = null;
                _dualSenseRawSwitches = null;
                _dualSenseRawAxes = null;
                _hasLastDualSenseRawState = false;
                _dualSenseRawPacketNumber = 0;
                _lastClientSendAt = 0;
                _status = status;
                _lastError = "";
                ClearDisplayedStateLocked();
            }

            if (!wasConnected)
                return;

            PublishClientState(
                new RemoteGamepadPacket
                {
                    Connected = false,
                    UserIndex = -1,
                    Sequence = Interlocked.Increment(ref _sequence)
                });
        }

        private void PublishClientState(RemoteGamepadPacket packet)
        {
            _ = _session.PublishGamepadAsync(packet);
            lock (_sync)
            {
                _txPackets++;
            }
        }

        private void ProcessNewestRemotePacket()
        {
            RemoteGamepadPacket? newest = null;
            while (_remoteInbound.Reader.TryRead(out RemoteGamepadPacket? packet))
            {
                newest = packet;
            }

            if (newest is null)
                return;

            long now = Environment.TickCount64;
            lock (_sync)
            {
                _rxPackets++;
                _lastRemotePacketAt = now;
                _remoteControllerConnected = newest.Connected;
                _staleNeutralActive = false;
                _lastButtons = newest.Buttons;
                _lastLeftTrigger = newest.LeftTrigger;
                _lastRightTrigger = newest.RightTrigger;
                _lastLeftThumbX = newest.LeftThumbX;
                _lastLeftThumbY = newest.LeftThumbY;
                _lastRightThumbX = newest.RightThumbX;
                _lastRightThumbY = newest.RightThumbY;
                _requestedHostSlot = _tuning.HostPlayerSlot;
            }

            if (!newest.Connected)
            {
                DisconnectVirtualTarget();
                SetStatus("HOST • controle remoto desconectado", "");
                return;
            }

            if (!EnsureVirtualTarget(now))
                return;

            lock (_virtualSync)
            {
                IXbox360Controller? controller = _virtualController;
                if (controller is null)
                    return;

                try
                {
                    controller.ResetReport();
                    controller.SetButtonsFull(newest.Buttons);
                    controller.SetSliderValue(Xbox360Slider.LeftTrigger, newest.LeftTrigger);
                    controller.SetSliderValue(Xbox360Slider.RightTrigger, newest.RightTrigger);
                    controller.SetAxisValue(Xbox360Axis.LeftThumbX, newest.LeftThumbX);
                    controller.SetAxisValue(Xbox360Axis.LeftThumbY, newest.LeftThumbY);
                    controller.SetAxisValue(Xbox360Axis.RightThumbX, newest.RightThumbX);
                    controller.SetAxisValue(Xbox360Axis.RightThumbY, newest.RightThumbY);
                    controller.SubmitReport();

                    int actual;
                    lock (_sync) actual = _actualHostSlot;
                    string slot = actual > 0 ? $"P{actual}" : "slot automático";
                    SetStatus($"HOST • Xbox 360 virtual ativo em {slot} • latest-wins", "");
                }
                catch (Exception ex)
                {
                    SetStatus("HOST • falha ao atualizar Xbox virtual", ex.Message);
                    DisconnectVirtualTargetLocked();
                }
            }
        }

        private bool EnsureVirtualTarget(long now)
        {
            lock (_virtualSync)
            {
                int requestedSlot = _tuning.HostPlayerSlot;
                if (_virtualController is not null &&
                    _boundHostSlotPreference == requestedSlot)
                {
                    return true;
                }

                if (_virtualController is not null || _slotReservations.Count > 0)
                    DisconnectVirtualTargetLocked();

                if (now < _nextVigemRetryAt)
                    return false;

                _nextVigemRetryAt = now + ViGEmRetryMs;

                try
                {
                    _vigemClient = new ViGEmClient();
                    bool success = requestedSlot == 0
                        ? BuildAutomaticVirtualTargetLocked()
                        : BuildVirtualTargetForSlotLocked(requestedSlot);

                    if (!success)
                        return false;

                    _boundHostSlotPreference = requestedSlot;
                    _nextVigemRetryAt = 0;
                    return true;
                }
                catch (Exception ex)
                {
                    ReleaseVirtualTargetsLocked();
                    lock (_sync)
                    {
                        _hostSlotConflict = false;
                        _actualHostSlot = -1;
                    }
                    SetStatus(
                        "HOST • ViGEmBus indisponível",
                        "Instale o Nefarius ViGEmBus no PC host. " + ex.Message);
                    return false;
                }
            }
        }

        private bool BuildAutomaticVirtualTargetLocked()
        {
            bool[] before = ScanXInputOccupancy();
            IXbox360Controller controller = CreateNeutralControllerLocked();
            _virtualController = controller;
            int actual = DetectNewOccupiedSlot(before);

            lock (_sync)
            {
                _requestedHostSlot = 0;
                _actualHostSlot = actual;
                _hostSlotConflict = false;
                _reservationCount = 0;
            }

            SetStatus(
                actual > 0
                    ? $"HOST • Xbox 360 virtual pronto em P{actual}"
                    : "HOST • Xbox 360 virtual pronto • slot automático",
                "");
            return true;
        }

        private bool BuildVirtualTargetForSlotLocked(int requestedSlot)
        {
            int requestedIndex = Math.Clamp(requestedSlot, 1, 4) - 1;
            bool[] occupied = ScanXInputOccupancy();

            // Strict manual mode: an occupied requested slot is a hard conflict.
            if (occupied[requestedIndex])
            {
                ReleaseVirtualTargetsLocked();
                lock (_sync)
                {
                    _requestedHostSlot = requestedSlot;
                    _actualHostSlot = -1;
                    _hostSlotConflict = true;
                    _reservationCount = 0;
                }
                SetStatus(
                    $"HOST • P{requestedSlot} já está ocupado • controle remoto neutralizado",
                    $"The requested slot P{requestedSlot} is already occupied by another XInput device.");
                return false;
            }

            for (int index = 0; index < requestedIndex; index++)
            {
                occupied = ScanXInputOccupancy();
                if (occupied[index])
                    continue;

                IXbox360Controller reservation = CreateNeutralControllerLocked();
                _slotReservations.Add(reservation);
                lock (_sync) _reservationCount = _slotReservations.Count;

                if (!VerifyRequestedSlotOccupied(index))
                {
                    ReleaseVirtualTargetsLocked();
                    lock (_sync)
                    {
                        _requestedHostSlot = requestedSlot;
                        _actualHostSlot = -1;
                        _hostSlotConflict = true;
                    }
                    SetStatus(
                        $"HOST • não foi possível reservar P{index + 1} para alcançar P{requestedSlot}",
                        "O Windows não enumerou o controle virtual de reserva no slot esperado.");
                    return false;
                }
            }

            bool[] beforeActive = ScanXInputOccupancy();
            if (beforeActive[requestedIndex])
            {
                ReleaseVirtualTargetsLocked();
                lock (_sync)
                {
                    _requestedHostSlot = requestedSlot;
                    _actualHostSlot = -1;
                    _hostSlotConflict = true;
                }
                SetStatus(
                    $"HOST • P{requestedSlot} ficou ocupado durante a preparação",
                    "Outro dispositivo XInput ocupou o slot antes do controle remoto.");
                return false;
            }

            _virtualController = CreateNeutralControllerLocked();
            if (!VerifyRequestedSlotOccupied(requestedIndex))
            {
                ReleaseVirtualTargetsLocked();
                lock (_sync)
                {
                    _requestedHostSlot = requestedSlot;
                    _actualHostSlot = -1;
                    _hostSlotConflict = true;
                }
                SetStatus(
                    $"HOST • não foi possível confirmar P{requestedSlot}",
                    "O Xbox virtual foi criado, mas o slot XInput solicitado não apareceu.");
                return false;
            }

            lock (_sync)
            {
                _requestedHostSlot = requestedSlot;
                _actualHostSlot = requestedSlot;
                _hostSlotConflict = false;
                _reservationCount = _slotReservations.Count;
            }

            SetStatus(
                $"HOST • Xbox 360 virtual pronto em P{requestedSlot} • {_slotReservations.Count} reserva(s)",
                "");
            return true;
        }

        private IXbox360Controller CreateNeutralControllerLocked()
        {
            ViGEmClient client = _vigemClient ?? throw new InvalidOperationException("ViGEm client não inicializado.");
            IXbox360Controller controller = client.CreateXbox360Controller();
            controller.AutoSubmitReport = false;
            controller.Connect();
            controller.ResetReport();
            controller.SubmitReport();
            return controller;
        }

        private bool[] ScanXInputOccupancy()
        {
            bool[] occupied = new bool[4];
            for (uint i = 0; i < 4; i++)
                occupied[i] = TryGetXInputState(i, out _);
            return occupied;
        }

        private bool VerifyRequestedSlotOccupied(int zeroBasedSlot)
        {
            long deadline = Environment.TickCount64 + SlotEnumerationWaitMs;
            do
            {
                bool[] occupied = ScanXInputOccupancy();
                if (zeroBasedSlot >= 0 &&
                    zeroBasedSlot < occupied.Length &&
                    occupied[zeroBasedSlot])
                {
                    return true;
                }
                Thread.Sleep(10);
            }
            while (Environment.TickCount64 < deadline);

            return false;
        }

        private int DetectNewOccupiedSlot(bool[] before)
        {
            long deadline = Environment.TickCount64 + SlotEnumerationWaitMs;
            do
            {
                bool[] after = ScanXInputOccupancy();
                for (int i = 0; i < Math.Min(before.Length, after.Length); i++)
                {
                    if (!before[i] && after[i])
                        return i + 1;
                }
                Thread.Sleep(10);
            }
            while (Environment.TickCount64 < deadline);

            return -1;
        }

        private void EnforceHostWatchdog()
        {
            long now = Environment.TickCount64;
            long last;
            bool remoteConnected;
            bool alreadyNeutral;

            lock (_sync)
            {
                last = _lastRemotePacketAt;
                remoteConnected = _remoteControllerConnected;
                alreadyNeutral = _staleNeutralActive;
            }

            bool stale =
                last != 0 &&
                now - last >= StaleInputTimeoutMs;

            if (!remoteConnected || !stale || alreadyNeutral)
                return;

            lock (_virtualSync)
            {
                ApplyNeutralReportLocked();
            }

            lock (_sync)
            {
                _staleNeutralActive = true;
                _status = "HOST • input sem pacote >250 ms • controle neutralizado";
            }
        }

        private void ApplyNeutralReport()
        {
            lock (_virtualSync)
            {
                ApplyNeutralReportLocked();
            }
        }

        private void ApplyNeutralReportLocked()
        {
            IXbox360Controller? controller = _virtualController;
            if (controller is null)
                return;

            TryNeutralizeController(controller);
        }

        private static void TryNeutralizeController(IXbox360Controller controller)
        {
            try
            {
                controller.ResetReport();
                controller.SubmitReport();
            }
            catch
            {
            }
        }

        private void DisconnectVirtualTarget()
        {
            lock (_virtualSync)
            {
                DisconnectVirtualTargetLocked();
            }
        }

        private void DisconnectVirtualTargetLocked()
        {
            ReleaseVirtualTargetsLocked();
            lock (_sync)
            {
                _remoteControllerConnected = false;
                _staleNeutralActive = false;
                _hostSlotConflict = false;
                _requestedHostSlot = _tuning.HostPlayerSlot;
            }
        }

        private void ReleaseVirtualTargetsLocked()
        {
            if (_virtualController is not null)
            {
                TryNeutralizeController(_virtualController);
                try { _virtualController.Disconnect(); } catch { }
                _virtualController = null;
            }

            for (int i = _slotReservations.Count - 1; i >= 0; i--)
            {
                IXbox360Controller reservation = _slotReservations[i];
                TryNeutralizeController(reservation);
                try { reservation.Disconnect(); } catch { }
            }
            _slotReservations.Clear();

            try { _vigemClient?.Dispose(); } catch { }
            _vigemClient = null;
            _boundHostSlotPreference = -1;

            lock (_sync)
            {
                _actualHostSlot = -1;
                _reservationCount = 0;
            }
        }

        private void ResetClientState()
        {
            lock (_inputReadSync)
            {
                lock (_sync)
                {
                    _localControllerConnected = false;
                    _localControllerIndex = -1;
                    _localControllerName = "";
                    _localControllerBackend = LocalControllerBackend.None;
                    _lastXInputPacketNumber = 0;
                    _windowsGamepad = null;
                    _hasLastWindowsGamepadState = false;
                    _windowsGamepadPacketNumber = 0;
                    _dualSenseRawController = null;
                    _dualSenseRawControllerId = "";
                    _dualSenseRawDisplayName = "DualSense";
                    _dualSenseRawButtons = null;
                    _dualSenseRawSwitches = null;
                    _dualSenseRawAxes = null;
                    _hasLastDualSenseRawState = false;
                    _dualSenseRawPacketNumber = 0;
                    _lastClientSendAt = 0;
                    ClearDisplayedStateLocked();
                }
            }
        }

        private void DrainRemoteQueue()
        {
            while (_remoteInbound.Reader.TryRead(out _))
            {
            }
        }

        private void SetStatus(string status, string error)
        {
            lock (_sync)
            {
                _status = status;
                _lastError = error;
            }
        }

        public void NeutralizeNow()
        {
            lock (_sync)
            {
                _neutralizeRequested = true;
                _status = "Neutralização solicitada...";
            }
        }

        private bool ConsumeNeutralizeRequest()
        {
            lock (_sync)
            {
                if (!_neutralizeRequested)
                    return false;
                _neutralizeRequested = false;
                return true;
            }
        }

        public void RebindVirtualController()
        {
            lock (_sync)
            {
                _rebindRequested = true;
                _status = "Recriando controle virtual com segurança...";
            }
        }

        public void SetHostSlotAndRebind(int slot)
        {
            _tuning.SetHostPlayerSlot(slot);
            RebindVirtualController();
        }

        private bool ConsumeRebindRequest()
        {
            lock (_sync)
            {
                if (!_rebindRequested)
                    return false;
                _rebindRequested = false;
                return true;
            }
        }

        private XInputGamepad ApplyTuning(XInputGamepad raw)
        {
            raw.Buttons = _tuning.MapButtons(raw.Buttons);

            int leftInner = _tuning.LeftStickDeadzonePercent;
            int rightInner = _tuning.RightStickDeadzonePercent;
            int leftOuter = _tuning.LeftStickOuterDeadzonePercent;
            int rightOuter = _tuning.RightStickOuterDeadzonePercent;
            int leftAnti = _tuning.LeftStickAntiDeadzonePercent;
            int rightAnti = _tuning.RightStickAntiDeadzonePercent;
            int leftTrigger = _tuning.LeftTriggerDeadzonePercent;
            int rightTrigger = _tuning.RightTriggerDeadzonePercent;
            GamepadStickResponseCurve curve = _tuning.StickResponseCurve;
            bool invertLeftY = _tuning.InvertLeftY;
            bool invertRightY = _tuning.InvertRightY;

            if (leftInner == 0 && rightInner == 0 &&
                leftOuter == 0 && rightOuter == 0 &&
                leftAnti == 0 && rightAnti == 0 &&
                leftTrigger == 0 && rightTrigger == 0 &&
                curve == GamepadStickResponseCurve.Linear &&
                !invertLeftY && !invertRightY)
            {
                return raw;
            }

            ApplyStickTuning(
                raw.LeftThumbX, raw.LeftThumbY,
                leftInner, leftOuter, leftAnti,
                curve, invertLeftY,
                out raw.LeftThumbX, out raw.LeftThumbY);

            ApplyStickTuning(
                raw.RightThumbX, raw.RightThumbY,
                rightInner, rightOuter, rightAnti,
                curve, invertRightY,
                out raw.RightThumbX, out raw.RightThumbY);

            raw.LeftTrigger = ApplyTriggerTuning(raw.LeftTrigger, leftTrigger);
            raw.RightTrigger = ApplyTriggerTuning(raw.RightTrigger, rightTrigger);
            return raw;
        }

        private static void ApplyStickTuning(
            short rawX,
            short rawY,
            int innerPercent,
            int outerPercent,
            int antiPercent,
            GamepadStickResponseCurve curve,
            bool invertY,
            out short tunedX,
            out short tunedY)
        {
            if (innerPercent == 0 &&
                outerPercent == 0 &&
                antiPercent == 0 &&
                curve == GamepadStickResponseCurve.Linear)
            {
                tunedX = rawX;
                tunedY = invertY ? InvertAxis(rawY) : rawY;
                return;
            }

            double x = Math.Clamp(rawX / 32767.0, -1.0, 1.0);
            double y = Math.Clamp(rawY / 32767.0, -1.0, 1.0);
            if (invertY)
                y = -y;

            double magnitude = Math.Sqrt(x * x + y * y);
            if (magnitude <= double.Epsilon)
            {
                tunedX = 0;
                tunedY = 0;
                return;
            }

            double inner = Math.Clamp(innerPercent, 0, 40) / 100.0;
            double outer = Math.Clamp(outerPercent, 0, 20) / 100.0;
            double anti = Math.Clamp(antiPercent, 0, 30) / 100.0;
            double usableEnd = Math.Max(inner + 0.01, 1.0 - outer);

            if (magnitude <= inner)
            {
                tunedX = 0;
                tunedY = 0;
                return;
            }

            double normalized = Math.Clamp((magnitude - inner) / (usableEnd - inner), 0.0, 1.0);
            normalized = curve switch
            {
                GamepadStickResponseCurve.Precision => Math.Pow(normalized, 1.45),
                GamepadStickResponseCurve.Fast => Math.Pow(normalized, 0.72),
                _ => normalized
            };

            if (anti > 0 && normalized > 0)
                normalized = anti + (1.0 - anti) * normalized;

            normalized = Math.Clamp(normalized, 0.0, 1.0);
            double directionX = x / magnitude;
            double directionY = y / magnitude;
            tunedX = ToStickShort(directionX * normalized);
            tunedY = ToStickShort(directionY * normalized);
        }


        private static short InvertAxis(short value)
        {
            return value == short.MinValue ? short.MaxValue : (short)-value;
        }

        private static byte ApplyTriggerTuning(byte value, int percent)
        {
            if (percent <= 0)
                return value;

            int threshold = (int)Math.Round(byte.MaxValue * Math.Clamp(percent, 0, 40) / 100.0);
            if (value <= threshold)
                return 0;

            double normalized = (value - threshold) / (double)(byte.MaxValue - threshold);
            return (byte)Math.Round(Math.Clamp(normalized, 0.0, 1.0) * byte.MaxValue);
        }

        private static byte ToTriggerByte(double value) =>
            (byte)Math.Round(Math.Clamp(value, 0.0, 1.0) * byte.MaxValue);

        private static short ToStickShort(double value)
        {
            double clamped = Math.Clamp(value, -1.0, 1.0);
            if (clamped <= -1.0)
                return short.MinValue;
            return (short)Math.Round(clamped * short.MaxValue);
        }

        private void ClearDisplayedStateLocked()
        {
            _lastButtons = 0;
            _lastLeftTrigger = 0;
            _lastRightTrigger = 0;
            _lastLeftThumbX = 0;
            _lastLeftThumbY = 0;
            _lastRightThumbX = 0;
            _lastRightThumbY = 0;
        }

        private bool TryGetDualSenseRawDiagnosticSnapshot(out LocalRawGamepadSnapshot snapshot)
        {
            snapshot = new LocalRawGamepadSnapshot { Connected = false };
            try
            {
                IReadOnlyList<RawGameController> controllers = RawGameController.RawGameControllers;
                RawGameController? selected = null;

                // A WinRT controller object can remain callable after the physical pad
                // was removed and may keep returning its last reading. Never read the
                // cached object directly. Resolve it back to an object that exists in
                // the current Windows enumeration on every diagnostic poll. Matching by
                // NonRoamableId also avoids false disconnects if WinRT materializes a new
                // wrapper for the same still-connected device.
                if (_diagnosticRawController is not null)
                {
                    selected = ResolveCurrentDiagnosticRawController(
                        _diagnosticRawController,
                        _diagnosticRawControllerId,
                        controllers);
                    if (selected is null)
                    {
                        ClearDiagnosticRawState();
                        return false;
                    }
                }

                if (selected is null)
                {
                    foreach (RawGameController candidate in controllers)
                    {
                        if (!IsDiagnosticDualSenseCandidate(candidate))
                            continue;
                        selected = candidate;
                        break;
                    }
                }

                if (selected is null)
                {
                    ClearDiagnosticRawState();
                    return false;
                }

                string id = selected.NonRoamableId ?? "";
                bool changed = _diagnosticRawController is null ||
                    !ReferenceEquals(_diagnosticRawController, selected) ||
                    !string.Equals(_diagnosticRawControllerId, id, StringComparison.Ordinal) ||
                    _diagnosticRawButtons is null || _diagnosticRawButtons.Length != selected.ButtonCount ||
                    _diagnosticRawSwitches is null || _diagnosticRawSwitches.Length != selected.SwitchCount ||
                    _diagnosticRawAxes is null || _diagnosticRawAxes.Length != selected.AxisCount;

                if (changed)
                {
                    _diagnosticRawController = selected;
                    _diagnosticRawControllerId = id;
                    _diagnosticRawButtons = new bool[selected.ButtonCount];
                    _diagnosticRawSwitches = new GameControllerSwitchPosition[selected.SwitchCount];
                    _diagnosticRawAxes = new double[selected.AxisCount];
                }

                selected.GetCurrentReading(
                    _diagnosticRawButtons!,
                    _diagnosticRawSwitches!,
                    _diagnosticRawAxes!);

                string display = string.IsNullOrWhiteSpace(selected.DisplayName)
                    ? "DualSense Wireless Controller"
                    : selected.DisplayName;
                snapshot = new LocalRawGamepadSnapshot
                {
                    Connected = true,
                    UserIndex = -1,
                    ControllerName = display,
                    ControllerIdentity = string.IsNullOrWhiteSpace(id)
                        ? $"SonyRaw:{selected.HardwareVendorId:X4}:{selected.HardwareProductId:X4}"
                        : id,
                    Backend = "DualSenseRawInteractive",
                    Buttons = ConvertDualSenseRawButtons(_diagnosticRawButtons!, _diagnosticRawSwitches!),
                    RawAxes = (double[])_diagnosticRawAxes!.Clone(),
                    RawAxisCount = selected.AxisCount,
                    RawButtonCount = selected.ButtonCount,
                    RawSwitchCount = selected.SwitchCount,
                    HardwareVendorId = selected.HardwareVendorId,
                    HardwareProductId = selected.HardwareProductId
                };
                return true;
            }
            catch
            {
                ClearDiagnosticRawState();
                return false;
            }
        }

        private static RawGameController? ResolveCurrentDiagnosticRawController(
            RawGameController cached,
            string cachedId,
            IReadOnlyList<RawGameController> currentControllers)
        {
            for (int index = 0; index < currentControllers.Count; index++)
            {
                RawGameController candidate = currentControllers[index];
                if (!IsDiagnosticDualSenseCandidate(candidate))
                    continue;
                if (ReferenceEquals(cached, candidate))
                    return candidate;

                string candidateId = candidate.NonRoamableId ?? "";
                if (!string.IsNullOrWhiteSpace(cachedId) &&
                    string.Equals(cachedId, candidateId, StringComparison.Ordinal))
                    return candidate;
            }
            return null;
        }

        private static bool IsDiagnosticDualSenseCandidate(RawGameController controller)
        {
            if (controller.HardwareVendorId != SonyVendorId)
                return false;
            if (controller.HardwareProductId == DualSenseProductId ||
                controller.HardwareProductId == DualSenseEdgeProductId)
                return true;
            string name = controller.DisplayName ?? "";
            return name.Contains("DualSense", StringComparison.OrdinalIgnoreCase);
        }

        private static ushort ConvertDualSenseRawButtons(
            bool[] buttons,
            GameControllerSwitchPosition[] switches)
        {
            ushort targetButtons = 0;
            if (buttons.Length > 0 && buttons[0]) targetButtons |= XInputX;
            if (buttons.Length > 1 && buttons[1]) targetButtons |= XInputA;
            if (buttons.Length > 2 && buttons[2]) targetButtons |= XInputB;
            if (buttons.Length > 3 && buttons[3]) targetButtons |= XInputY;
            if (buttons.Length > 4 && buttons[4]) targetButtons |= XInputLeftShoulder;
            if (buttons.Length > 5 && buttons[5]) targetButtons |= XInputRightShoulder;
            if (buttons.Length > 8 && buttons[8]) targetButtons |= XInputBack;
            if (buttons.Length > 9 && buttons[9]) targetButtons |= XInputStart;
            if (buttons.Length > 10 && buttons[10]) targetButtons |= XInputLeftThumb;
            if (buttons.Length > 11 && buttons[11]) targetButtons |= XInputRightThumb;
            if (switches.Length > 0)
                targetButtons |= ConvertDpadSwitch(switches[0]);
            return targetButtons;
        }

        private void ClearDiagnosticRawState()
        {
            _diagnosticRawController = null;
            _diagnosticRawControllerId = "";
            _diagnosticRawButtons = null;
            _diagnosticRawSwitches = null;
            _diagnosticRawAxes = null;
        }

        private bool TryGetDualSenseRawState(
            out XInputGamepad state,
            out bool deviceChanged)
        {
            state = default;
            deviceChanged = false;

            try
            {
                RawGameController? selected = _dualSenseRawController;
                if (selected is null)
                {
                    IReadOnlyList<RawGameController> controllers =
                        RawGameController.RawGameControllers;
                    for (int index = 0; index < controllers.Count; index++)
                    {
                        RawGameController candidate = controllers[index];
                        if (!IsSupportedDualSense(candidate))
                            continue;

                        selected = candidate;
                        break;
                    }
                }

                if (selected is null)
                {
                    ClearDualSenseRawState();
                    return false;
                }

                string selectedId = selected.NonRoamableId ?? "";
                bool selectionChanged =
                    _dualSenseRawController is null ||
                    !string.Equals(
                        _dualSenseRawControllerId,
                        selectedId,
                        StringComparison.Ordinal);

                if (selectionChanged ||
                    _dualSenseRawButtons is null ||
                    _dualSenseRawButtons.Length != selected.ButtonCount ||
                    _dualSenseRawSwitches is null ||
                    _dualSenseRawSwitches.Length != selected.SwitchCount ||
                    _dualSenseRawAxes is null ||
                    _dualSenseRawAxes.Length != selected.AxisCount)
                {
                    if (selected.AxisCount < 6 || selected.ButtonCount < 12)
                    {
                        ClearDualSenseRawState();
                        return false;
                    }

                    _dualSenseRawController = selected;
                    _dualSenseRawControllerId = selectedId;
                    _dualSenseRawDisplayName = string.IsNullOrWhiteSpace(selected.DisplayName)
                        ? "DualSense"
                        : selected.DisplayName;
                    _dualSenseRawButtons = new bool[selected.ButtonCount];
                    _dualSenseRawSwitches = new GameControllerSwitchPosition[selected.SwitchCount];
                    _dualSenseRawAxes = new double[selected.AxisCount];
                    _hasLastDualSenseRawState = false;
                    deviceChanged = true;
                }

                selected.GetCurrentReading(
                    _dualSenseRawButtons!,
                    _dualSenseRawSwitches!,
                    _dualSenseRawAxes!);
                state = ConvertDualSenseRawReading(
                    _dualSenseRawButtons!,
                    _dualSenseRawSwitches!,
                    _dualSenseRawAxes!);
                return true;
            }
            catch
            {
                ClearDualSenseRawState();
                return false;
            }
        }

        private static bool IsSupportedDualSense(RawGameController controller) =>
            controller.HardwareVendorId == SonyVendorId &&
            (controller.HardwareProductId == DualSenseProductId ||
             controller.HardwareProductId == DualSenseEdgeProductId);

        private void ClearDualSenseRawState()
        {
            _dualSenseRawController = null;
            _dualSenseRawControllerId = "";
            _dualSenseRawDisplayName = "DualSense";
            _dualSenseRawButtons = null;
            _dualSenseRawSwitches = null;
            _dualSenseRawAxes = null;
            _hasLastDualSenseRawState = false;
            _dualSenseRawPacketNumber = 0;
        }

        private static XInputGamepad ConvertDualSenseRawReading(
            bool[] buttons,
            GameControllerSwitchPosition[] switches,
            double[] axes)
        {
            ushort targetButtons = 0;

            // DualSense HID button order is stable for Sony VID 054C / PID 0CE6
            // (and the Edge keeps the base controller layout for these inputs).
            if (buttons.Length > 0 && buttons[0]) targetButtons |= XInputX; // Square
            if (buttons.Length > 1 && buttons[1]) targetButtons |= XInputA; // Cross
            if (buttons.Length > 2 && buttons[2]) targetButtons |= XInputB; // Circle
            if (buttons.Length > 3 && buttons[3]) targetButtons |= XInputY; // Triangle
            if (buttons.Length > 4 && buttons[4]) targetButtons |= XInputLeftShoulder;
            if (buttons.Length > 5 && buttons[5]) targetButtons |= XInputRightShoulder;
            if (buttons.Length > 8 && buttons[8]) targetButtons |= XInputBack; // Create
            if (buttons.Length > 9 && buttons[9]) targetButtons |= XInputStart; // Options
            if (buttons.Length > 10 && buttons[10]) targetButtons |= XInputLeftThumb;
            if (buttons.Length > 11 && buttons[11]) targetButtons |= XInputRightThumb;

            if (switches.Length > 0)
                targetButtons |= ConvertDpadSwitch(switches[0]);

            return new XInputGamepad
            {
                Buttons = targetButtons,
                LeftTrigger = ToTriggerByte(axes[4]),
                RightTrigger = ToTriggerByte(axes[5]),
                LeftThumbX = ToUnsignedStickShort(axes[0], invert: false),
                LeftThumbY = ToUnsignedStickShort(axes[1], invert: true),
                RightThumbX = ToUnsignedStickShort(axes[2], invert: false),
                RightThumbY = ToUnsignedStickShort(axes[3], invert: true)
            };
        }

        private static ushort ConvertDpadSwitch(GameControllerSwitchPosition position)
        {
            return position switch
            {
                GameControllerSwitchPosition.Up => XInputDpadUp,
                GameControllerSwitchPosition.UpRight => XInputDpadUp | XInputDpadRight,
                GameControllerSwitchPosition.Right => XInputDpadRight,
                GameControllerSwitchPosition.DownRight => XInputDpadDown | XInputDpadRight,
                GameControllerSwitchPosition.Down => XInputDpadDown,
                GameControllerSwitchPosition.DownLeft => XInputDpadDown | XInputDpadLeft,
                GameControllerSwitchPosition.Left => XInputDpadLeft,
                GameControllerSwitchPosition.UpLeft => XInputDpadUp | XInputDpadLeft,
                _ => 0
            };
        }

        private static short ToUnsignedStickShort(double value, bool invert)
        {
            double normalized = (Math.Clamp(value, 0.0, 1.0) * 2.0) - 1.0;
            if (invert)
                normalized = -normalized;
            return ToStickShort(normalized);
        }

        private string GetWindowsGamepadIdentity()
        {
            try
            {
                if (_windowsGamepad is null)
                    return "WindowsGamepad";
                RawGameController? raw = RawGameController.FromGameController(_windowsGamepad);
                string id = raw?.NonRoamableId ?? "";
                return string.IsNullOrWhiteSpace(id) ? "WindowsGamepad" : id;
            }
            catch
            {
                return "WindowsGamepad";
            }
        }

        private bool TryGetWindowsDualSenseGamepadState(
            out XInputGamepad state,
            out bool deviceChanged)
        {
            state = default;
            deviceChanged = false;

            try
            {
                IReadOnlyList<Gamepad> gamepads = Gamepad.Gamepads;
                Gamepad? selected = null;

                foreach (Gamepad candidate in gamepads)
                {
                    RawGameController? raw =
                        RawGameController.FromGameController(candidate);
                    if (raw is null || !IsSupportedDualSense(raw))
                        continue;

                    selected = candidate;
                    break;
                }

                if (selected is null)
                {
                    _windowsGamepad = null;
                    return false;
                }

                deviceChanged =
                    _windowsGamepad is null ||
                    !ReferenceEquals(_windowsGamepad, selected);
                _windowsGamepad = selected;

                GamepadReading reading = selected.GetCurrentReading();
                state = ConvertWindowsGamepadReading(reading);
                return true;
            }
            catch
            {
                _windowsGamepad = null;
                return false;
            }
        }

        private bool TryGetWindowsGamepadState(
            out XInputGamepad state,
            out bool deviceChanged)
        {
            state = default;
            deviceChanged = false;

            try
            {
                IReadOnlyList<Gamepad> gamepads = Gamepad.Gamepads;
                if (gamepads.Count == 0)
                {
                    _windowsGamepad = null;
                    return false;
                }

                Gamepad selected = gamepads[0];
                deviceChanged = _windowsGamepad is null;
                _windowsGamepad = selected;

                GamepadReading reading = selected.GetCurrentReading();
                state = ConvertWindowsGamepadReading(reading);
                return true;
            }
            catch
            {
                _windowsGamepad = null;
                return false;
            }
        }

        private static XInputGamepad ConvertWindowsGamepadReading(GamepadReading reading)
        {
            ushort buttons = 0;
            GamepadButtons source = reading.Buttons;
            if ((source & GamepadButtons.DPadUp) != 0) buttons |= XInputDpadUp;
            if ((source & GamepadButtons.DPadDown) != 0) buttons |= XInputDpadDown;
            if ((source & GamepadButtons.DPadLeft) != 0) buttons |= XInputDpadLeft;
            if ((source & GamepadButtons.DPadRight) != 0) buttons |= XInputDpadRight;
            if ((source & GamepadButtons.Menu) != 0) buttons |= XInputStart;
            if ((source & GamepadButtons.View) != 0) buttons |= XInputBack;
            if ((source & GamepadButtons.LeftThumbstick) != 0) buttons |= XInputLeftThumb;
            if ((source & GamepadButtons.RightThumbstick) != 0) buttons |= XInputRightThumb;
            if ((source & GamepadButtons.LeftShoulder) != 0) buttons |= XInputLeftShoulder;
            if ((source & GamepadButtons.RightShoulder) != 0) buttons |= XInputRightShoulder;
            if ((source & GamepadButtons.A) != 0) buttons |= XInputA;
            if ((source & GamepadButtons.B) != 0) buttons |= XInputB;
            if ((source & GamepadButtons.X) != 0) buttons |= XInputX;
            if ((source & GamepadButtons.Y) != 0) buttons |= XInputY;

            return new XInputGamepad
            {
                Buttons = buttons,
                LeftTrigger = ToTriggerByte(reading.LeftTrigger),
                RightTrigger = ToTriggerByte(reading.RightTrigger),
                LeftThumbX = ToStickShort(reading.LeftThumbstickX),
                LeftThumbY = ToStickShort(reading.LeftThumbstickY),
                RightThumbX = ToStickShort(reading.RightThumbstickX),
                RightThumbY = ToStickShort(reading.RightThumbstickY)
            };
        }

        private static bool GamepadStateEquals(XInputGamepad left, XInputGamepad right) =>
            left.Buttons == right.Buttons &&
            left.LeftTrigger == right.LeftTrigger &&
            left.RightTrigger == right.RightTrigger &&
            left.LeftThumbX == right.LeftThumbX &&
            left.LeftThumbY == right.LeftThumbY &&
            left.RightThumbX == right.RightThumbX &&
            left.RightThumbY == right.RightThumbY;

        private static bool TryGetXInputState(uint userIndex, out XInputState state)
        {
            try
            {
                return XInput14.XInputGetState(userIndex, out state) == ErrorSuccess;
            }
            catch (DllNotFoundException)
            {
                return XInput910.XInputGetState(userIndex, out state) == ErrorSuccess;
            }
            catch (EntryPointNotFoundException)
            {
                return XInput910.XInputGetState(userIndex, out state) == ErrorSuccess;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _session.RemoteGamepadReceived -= Session_RemoteGamepadReceived;
            _remoteInbound.Writer.TryComplete();
            _cancel.Cancel();

            try
            {
                _worker.Wait(500);
            }
            catch
            {
            }

            DisconnectVirtualTarget();
            _cancel.Dispose();
        }

        private enum LocalControllerBackend
        {
            None = 0,
            XInput = 1,
            WindowsGamepad = 2,
            DualSenseRaw = 3
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputGamepad
        {
            public ushort Buttons;
            public byte LeftTrigger;
            public byte RightTrigger;
            public short LeftThumbX;
            public short LeftThumbY;
            public short RightThumbX;
            public short RightThumbY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputState
        {
            public uint PacketNumber;
            public XInputGamepad Gamepad;
        }

        private static class XInput14
        {
            [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
            internal static extern uint XInputGetState(
                uint userIndex,
                out XInputState state);
        }

        private static class XInput910
        {
            [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
            internal static extern uint XInputGetState(
                uint userIndex,
                out XInputState state);
        }
    }
}
