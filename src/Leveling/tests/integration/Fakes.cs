using System.Collections.Concurrent;
using System.Net;

namespace Multiverse.Services;

internal static class FakeEnvironment
{
    internal static readonly string MonitorName = @"\\.\DISPLAY1";

    internal static void Reset()
    {
        NativeCoreService.Reset();
        RemoteSessionService.Instance.Reset();
        RemoteStreamingRuntime.Instance.Reset();
        MultiverseSettingsService.Instance.Reset();
        RemoteAudioService.Instance.Reset();
        RemoteHostVideoTransport.Reset();
        VideoIncidentRecorder.Instance.Reset();
        StartupDiagnostics.Reset();
    }

    internal static NativeCaptureSession NewCapture() => new()
    {
        CaptureStats = new CapturePipelineStats
        {
            BackendFramesArrived = 10,
            BackendFramesPublished = 10,
            BackendLastCallbackAgoMs = 0,
            LastPublishAgoMs = 0,
            BackendRunning = true
        },
        EncoderStats = new EncoderStats
        {
            Running = false,
            OutputWorkerAlive = true,
            EncoderWorkerAlive = true,
            InputSlots = 4,
            EncodedFrames = 10,
            Width = 1920,
            Height = 1080,
            TargetFps = 60,
            BitrateKbps = 20_000
        }
    };
}

internal static class NativeCoreService
{
    internal static int OpenCaptureCalls { get; private set; }
    internal static int ActiveEncoders { get; private set; }
    internal static int MaxActiveEncoders { get; private set; }
    internal static int UnsafeDisposeWhileRunning { get; private set; }
    internal static List<NativeCaptureSession> Captures { get; } = new();
    internal static Func<string, CaptureOpenResult> OpenCaptureFactory { get; set; } = DefaultOpen;
    internal static HardwareSnapshot Hardware { get; private set; } = CreateHardware();

    internal static void Reset()
    {
        OpenCaptureCalls = 0;
        ActiveEncoders = 0;
        MaxActiveEncoders = 0;
        UnsafeDisposeWhileRunning = 0;
        Captures.Clear();
        Hardware = CreateHardware();
        OpenCaptureFactory = DefaultOpen;
    }

    internal static HardwareSnapshot ReadHardware() => Hardware;

    internal static CaptureOpenResult OpenCapture(string deviceName)
    {
        OpenCaptureCalls++;
        CaptureOpenResult result = OpenCaptureFactory(deviceName);
        if (result.Session is not null) Captures.Add(result.Session);
        return result;
    }

    private static CaptureOpenResult DefaultOpen(string _) => new()
    {
        HResult = 0,
        Session = FakeEnvironment.NewCapture()
    };

    private static HardwareSnapshot CreateHardware()
    {
        var hardware = new HardwareSnapshot { NativeCoreLoaded = true };
        hardware.Monitors.Add(new MonitorInfo
        {
            DeviceName = FakeEnvironment.MonitorName,
            Width = 1920,
            Height = 1080,
            RefreshRate = 60,
            IsPrimary = true,
            DesktopDuplicationReady = true
        });
        return hardware;
    }

    internal static void EncoderStarted()
    {
        ActiveEncoders++;
        MaxActiveEncoders = Math.Max(MaxActiveEncoders, ActiveEncoders);
    }

    internal static void EncoderStopped() => ActiveEncoders--;
    internal static void UnsafeDispose() => UnsafeDisposeWhileRunning++;
}

internal sealed class NativeCaptureSession : IDisposable
{
    private bool _encoderOwned;
    internal CapturePipelineStats CaptureStats { get; set; } = new();
    internal EncoderStats EncoderStats { get; set; } = new();
    internal Func<EncoderConfig, EncoderStartResult>? StartEncoderOverride { get; set; }
    internal Action? StopEncoderOverride { get; set; }
    internal Action? DisposeOverride { get; set; }
    internal int RequestIdrResult { get; set; }
    internal int StartEncoderCalls { get; private set; }
    internal int StopEncoderCalls { get; private set; }
    internal int RequestIdrCalls { get; private set; }
    internal int BlockSubmissionsCalls { get; private set; }
    internal bool Disposed { get; private set; }
    internal EncoderConfig? LastConfig { get; private set; }
    public uint SourceWidth { get; init; } = 1920;
    public uint SourceHeight { get; init; } = 1080;

    public EncoderStartResult StartEncoder(EncoderConfig config)
    {
        StartEncoderCalls++;
        LastConfig = Copy(config);
        EncoderStartResult result = StartEncoderOverride?.Invoke(config) ?? new EncoderStartResult { HResult = 0 };
        if (result.Success && !EncoderStats.Running)
        {
            EncoderStats.Running = true;
            _encoderOwned = true;
            NativeCoreService.EncoderStarted();
        }
        return result;
    }

    public void StopEncoder()
    {
        StopEncoderCalls++;
        StopEncoderOverride?.Invoke();
        if (_encoderOwned)
        {
            EncoderStats.Running = false;
            _encoderOwned = false;
            NativeCoreService.EncoderStopped();
        }
    }

    public void BlockEncoderSubmissions() => BlockSubmissionsCalls++;
    public int RequestIdr() { RequestIdrCalls++; return RequestIdrResult; }
    public EncoderStats? GetEncoderStats() => EncoderStats;
    public EncoderStats? GetEncoderStatsDiagnostic() => EncoderStats;
    public CapturePipelineStats? GetCapturePipelineStatsDiagnostic() => CaptureStats;

    public void Dispose()
    {
        DisposeOverride?.Invoke();
        if (_encoderOwned) NativeCoreService.UnsafeDispose();
        Disposed = true;
    }

    private static EncoderConfig Copy(EncoderConfig c) => new()
    {
        Fps = c.Fps,
        BitrateKbps = c.BitrateKbps,
        Preset = c.Preset,
        UltraLowLatency = c.UltraLowLatency,
        MaxWidth = c.MaxWidth,
        MaxHeight = c.MaxHeight,
        LabInputMode = c.LabInputMode
    };
}

internal sealed class CapturePipelineStats
{
    public ulong BackendFramesArrived { get; set; }
    public ulong BackendFramesPublished { get; set; }
    public double BackendLastCallbackAgoMs { get; set; }
    public double LastPublishAgoMs { get; set; }
    public bool BackendRunning { get; set; }
    public bool BackendStopInProgress { get; set; }
}

internal sealed class EncoderConfig
{
    public uint BitrateKbps { get; set; } = 50_000;
    public uint Fps { get; set; } = 60;
    public uint Preset { get; set; }
    public bool UltraLowLatency { get; set; } = true;
    public uint MaxWidth { get; set; } = 1920;
    public uint MaxHeight { get; set; } = 1080;
    public uint LabInputMode { get; set; } = 10;
}

internal sealed class EncoderStats
{
    public bool Running { get; set; }
    public int LastStatus { get; set; }
    public uint Width { get; set; }
    public uint Height { get; set; }
    public uint TargetFps { get; set; }
    public uint BitrateKbps { get; set; }
    public ulong EncodedFrames { get; set; }
    public uint InFlightFrames { get; set; }
    public uint InputSlots { get; set; }
    public uint PendingOutputs { get; set; }
    public double OldestInFlightMs { get; set; }
    public bool EncoderWorkerAlive { get; set; }
    public bool OutputWorkerAlive { get; set; }
    public bool StopInProgress { get; set; }
    public uint StopStage { get; set; }
}

internal sealed class EncoderStartResult
{
    public int HResult { get; set; }
    public string Message { get; set; } = "";
    public bool Success => HResult >= 0;
}

internal sealed class CaptureOpenResult
{
    public int HResult { get; set; }
    public string? Error { get; set; }
    public NativeCaptureSession? Session { get; set; }
}

internal sealed class HardwareSnapshot
{
    public bool NativeCoreLoaded { get; set; }
    public string? Error { get; set; }
    public List<MonitorInfo> Monitors { get; } = new();
}

internal sealed class MonitorInfo
{
    public string DeviceName { get; set; } = "";
    public uint Width { get; set; }
    public uint Height { get; set; }
    public uint RefreshRate { get; set; }
    public bool IsPrimary { get; set; }
    public bool DesktopDuplicationReady { get; set; }
}

internal enum RemoteSessionRole { None, Host, Client }

internal sealed class RemoteStreamConfig
{
    public uint Width { get; set; }
    public uint Height { get; set; }
    public uint Fps { get; set; }
    public uint BitrateKbps { get; set; }
    public long Generation { get; set; }
    public uint WireStreamId { get; set; }
}

internal sealed class RemoteSessionSnapshot
{
    public bool Connected { get; set; }
    public int SessionGeneration { get; set; }
    public RemoteSessionRole Role { get; set; }
    public double RoundTripMs { get; set; }
    public double BaseRoundTripMs { get; set; }
    public double QueueDelayMs { get; set; }
    public double JitterMs { get; set; }
    public double RemotePacketLossPercent { get; set; }
    public double RemoteVideoFps { get; set; }
    public double RemoteRxMbps { get; set; }
    public long RemoteTelemetryAgeMs { get; set; } = -1;
    public int RemotePendingFrames { get; set; }
    public long RemoteDecodeDrops { get; set; }
    public long RemotePresentedFrames { get; set; }
    public long RemoteCompleteFrames { get; set; }
    public long RemoteDecodedFrames { get; set; }
    public double RemoteLastPresentAgeMs { get; set; } = -1;
    public RemoteStreamConfig? StreamConfig { get; set; }
}

internal sealed class RemoteSessionService
{
    public static RemoteSessionService Instance { get; } = new();
    private CancellationTokenSource _sessionToken = new();
    private long _nextStreamGeneration = 1;
    internal bool VideoReady { get; set; } = true;
    internal int StreamConfigPublishes { get; private set; }
    internal int StreamStoppedPublishes { get; private set; }
    internal int SenderRecoveryResults { get; private set; }
    internal RemoteSessionSnapshot Snapshot { get; private set; } = new();
    internal IPEndPoint? RemoteEndpoint { get; set; }

    public event EventHandler? SessionChanged;
    public event Action? RemoteIdrRequested { add { } remove { } }
    public event Action<int, long, long, long>? RemoteSenderRecoveryRequested { add { } remove { } }

    internal void Reset()
    {
        _sessionToken.Cancel();
        _sessionToken.Dispose();
        _sessionToken = new CancellationTokenSource();
        _nextStreamGeneration = 1;
        VideoReady = true;
        StreamConfigPublishes = 0;
        StreamStoppedPublishes = 0;
        SenderRecoveryResults = 0;
        RemoteEndpoint = new IPEndPoint(IPAddress.Loopback, 47821);
        Snapshot = new RemoteSessionSnapshot
        {
            Connected = true,
            SessionGeneration = 1,
            Role = RemoteSessionRole.Host,
            StreamConfig = new RemoteStreamConfig
            {
                Width = 1920,
                Height = 1080,
                Fps = 60,
                BitrateKbps = 20_000,
                Generation = 1,
                WireStreamId = 1
            }
        };
    }

    internal void AdvanceSession()
    {
        _sessionToken.Cancel();
        _sessionToken.Dispose();
        _sessionToken = new CancellationTokenSource();
        Snapshot.SessionGeneration++;
        Snapshot.StreamConfig = new RemoteStreamConfig
        {
            Width = 1920,
            Height = 1080,
            Fps = 60,
            BitrateKbps = 20_000,
            Generation = ++_nextStreamGeneration,
            WireStreamId = (uint)_nextStreamGeneration
        };
        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    public RemoteSessionSnapshot GetSnapshot() => Snapshot;
    public IPEndPoint? GetRemoteVideoEndpoint() => RemoteEndpoint;
    public CancellationToken GetVideoSessionToken(int generation) =>
        generation == Snapshot.SessionGeneration ? _sessionToken.Token : new CancellationToken(true);

    public bool IsCurrentVideoStream(long streamGeneration, int sessionGeneration, RemoteSessionRole role) =>
        Snapshot.Connected && Snapshot.Role == role && Snapshot.SessionGeneration == sessionGeneration &&
        Snapshot.StreamConfig?.Generation == streamGeneration;

    public Task<long> PublishStreamConfigAsync(uint width, uint height, uint fps, uint bitrate,
        int expectedSessionGeneration = 0)
    {
        if (expectedSessionGeneration != 0 && Snapshot.SessionGeneration != expectedSessionGeneration)
            return Task.FromResult(0L);
        long generation = ++_nextStreamGeneration;
        Snapshot.StreamConfig = new RemoteStreamConfig
        {
            Width = width,
            Height = height,
            Fps = fps,
            BitrateKbps = bitrate,
            Generation = generation,
            WireStreamId = (uint)generation
        };
        StreamConfigPublishes++;
        return Task.FromResult(generation);
    }

    public Task<bool> WaitForRemoteVideoReadyAsync(long streamGeneration, int sessionGeneration,
        TimeSpan timeout, CancellationToken token) =>
        Task.FromResult(VideoReady && !token.IsCancellationRequested &&
            IsCurrentVideoStream(streamGeneration, sessionGeneration, RemoteSessionRole.Host));

    public string GetVideoStartupFailure(long streamGeneration, int sessionGeneration) => "";
    public Task PublishStreamStoppedAsync(int expectedSessionGeneration = 0, long expectedStreamGeneration = 0)
    {
        StreamStoppedPublishes++;
        return Task.CompletedTask;
    }

    internal Task PublishSenderRecoveryResultAsync(int sessionGeneration, long streamGeneration,
        long requestId, bool accepted)
    {
        SenderRecoveryResults++;
        return Task.CompletedTask;
    }
}

internal sealed class RemoteStreamingState
{
    public NativeCaptureSession? Capture { get; init; }
    public IVideoTransport? Transport { get; init; }
    public bool EncoderRunning { get; init; }
    public MonitorInfo? HostMonitor { get; init; }
}

internal sealed class RemoteStreamingRuntime
{
    public static RemoteStreamingRuntime Instance { get; } = new();
    private NativeCaptureSession? _capture;
    private IVideoTransport? _transport;
    private bool _encoderRunning;
    private MonitorInfo? _monitor;

    internal void Reset()
    {
        _capture = null;
        _transport = null;
        _encoderRunning = false;
        _monitor = null;
    }

    public RemoteStreamingState GetState() => new()
    {
        Capture = _capture,
        Transport = _transport,
        EncoderRunning = _encoderRunning,
        HostMonitor = _monitor
    };

    public void AdoptHost(NativeCaptureSession capture, IVideoTransport? transport,
        bool encoderRunning, MonitorInfo? monitor = null)
    {
        _capture = capture;
        if (transport is not null) _transport = transport;
        _encoderRunning = encoderRunning;
        if (monitor is not null) _monitor = monitor;
    }

    public void UpdateTransport(IVideoTransport? transport) => _transport = transport;

    public void UpdateCapture(NativeCaptureSession? capture, bool encoderRunning)
    {
        _capture = capture;
        _encoderRunning = encoderRunning;
        if (capture is null) _monitor = null;
    }
}

internal sealed class MultiverseStreamProfile
{
    public uint MaxWidth { get; init; } = 1920;
    public uint MaxHeight { get; init; } = 1080;
    public uint Fps { get; init; } = 60;
    public uint BitrateKbps { get; init; } = 20_000;
    public uint Preset { get; init; }
    public bool UltraLowLatency { get; init; } = true;
    public uint PipelineMode { get; init; } = 10;
    public string MonitorDeviceName { get; init; } = FakeEnvironment.MonitorName;
}

internal sealed class MultiverseAppPreferences
{
    public bool AutoStartHostStream { get; init; }
}

internal sealed class MultiverseSettingsService
{
    public static MultiverseSettingsService Instance { get; } = new();
    internal MultiverseStreamProfile Profile { get; set; } = new();
    internal MultiverseAppPreferences Preferences { get; set; } = new();
    internal int SaveMonitorCalls { get; private set; }
    internal void Reset() { Profile = new(); Preferences = new(); SaveMonitorCalls = 0; }
    public MultiverseStreamProfile GetStreamProfile() => Profile;
    public MultiverseAppPreferences GetAppPreferences() => Preferences;
    public void SaveMonitor(string deviceName) => SaveMonitorCalls++;
}

internal sealed class RemoteAudioService
{
    public static RemoteAudioService Instance { get; } = new();
    internal int EnsureCalls { get; private set; }
    internal int StopCalls { get; private set; }
    internal void Reset() { EnsureCalls = 0; StopCalls = 0; }
    public Task<bool> EnsureAutomaticForTransmissionAsync() { EnsureCalls++; return Task.FromResult(true); }
    public void StopHostCaptureOnly() => StopCalls++;
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
    public long SentFrames { get; init; }
}

internal interface IVideoTransport : IDisposable
{
    bool Running { get; }
    event Action<ReassembledVideoFrame>? FrameReceived;
    void Start();
    void Stop();
    TransportSnapshot GetSnapshot();
}

internal sealed class RemoteHostVideoTransport : IVideoTransport
{
    private readonly RemoteSessionService _session;
    private readonly int _sessionGeneration;
    private readonly long _streamGeneration;
    internal bool IsCurrentStream => _session.IsCurrentVideoStream(_streamGeneration, _sessionGeneration, RemoteSessionRole.Host);
    internal static int CreatedCount { get; private set; }
    internal static int StartCount { get; private set; }
    internal static int StopCount { get; private set; }
    internal static int DisposeCount { get; private set; }
    internal static int ActiveSenders { get; private set; }
    internal static int MaxActiveSenders { get; private set; }
    internal static int UnsafeDisposeWhileRunning { get; private set; }
    internal static Func<Exception?> StopFailure { get; set; } = () => null;
    internal IPEndPoint RemoteEndpoint { get; }
    public bool Running { get; private set; }
    private bool _disposed;
    private long _sentFrames;

    internal static void Reset()
    {
        CreatedCount = 0;
        StartCount = 0;
        StopCount = 0;
        DisposeCount = 0;
        ActiveSenders = 0;
        MaxActiveSenders = 0;
        UnsafeDisposeWhileRunning = 0;
        StopFailure = () => null;
    }

    public RemoteHostVideoTransport(NativeCaptureSession capture, IPEndPoint remote,
        RemoteSessionService session)
    {
        ArgumentNullException.ThrowIfNull(capture);
        RemoteEndpoint = remote ?? throw new ArgumentNullException(nameof(remote));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _sessionGeneration = session.Snapshot.SessionGeneration;
        _streamGeneration = session.Snapshot.StreamConfig?.Generation ?? 0;
        CreatedCount++;
    }

    public event Action<ReassembledVideoFrame>? FrameReceived { add { } remove { } }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Running) return;
        Running = true;
        StartCount++;
        ActiveSenders++;
        MaxActiveSenders = Math.Max(MaxActiveSenders, ActiveSenders);
        _sentFrames = 1;
        _session.Snapshot.RemoteCompleteFrames = Math.Max(1, _session.Snapshot.RemoteCompleteFrames + 1);
        _session.Snapshot.RemoteDecodedFrames = Math.Max(1, _session.Snapshot.RemoteDecodedFrames + 1);
        _session.Snapshot.RemotePresentedFrames = Math.Max(1, _session.Snapshot.RemotePresentedFrames + 1);
        _session.Snapshot.RemoteLastPresentAgeMs = 0;
    }

    public void Stop()
    {
        StopCount++;
        Exception? failure = StopFailure();
        if (failure is not null) throw failure;
        if (!Running) return;
        Running = false;
        ActiveSenders--;
    }

    public TransportSnapshot GetSnapshot() => new() { Running = Running, SentFrames = _sentFrames };

    public void Dispose()
    {
        if (Running) UnsafeDisposeWhileRunning++;
        if (!_disposed) DisposeCount++;
        _disposed = true;
    }
}

internal sealed class LoopbackVideoTransport : IVideoTransport
{
    public LoopbackVideoTransport(NativeCaptureSession capture) => ArgumentNullException.ThrowIfNull(capture);
    public bool Running { get; private set; }
    public event Action<ReassembledVideoFrame>? FrameReceived { add { } remove { } }
    public void Start() => Running = true;
    public void Stop() => Running = false;
    public TransportSnapshot GetSnapshot() => new() { Running = Running };
    public void Dispose() => Running = false;
}

internal sealed class VideoIncidentRecorder
{
    public static VideoIncidentRecorder Instance { get; } = new();
    private readonly ConcurrentQueue<string> _events = new();
    internal IReadOnlyCollection<string> Events => _events.ToArray();
    internal void Reset() { while (_events.TryDequeue(out _)) { } }
    internal void RecordEvent(string reason, object? details = null) => _events.Enqueue(reason);
    public void MarkIncident(string reason, long durationMs) => _events.Enqueue(reason);
}

internal static class StartupDiagnostics
{
    private static readonly ConcurrentQueue<string> Errors = new();
    internal static void Reset() { while (Errors.TryDequeue(out _)) { } }
    internal static void RecordException(string category, Exception exception, string? detail = null) =>
        Errors.Enqueue(category + ":" + exception.GetType().Name);
}
