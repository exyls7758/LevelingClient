using Concentus;
using Concentus.Enums;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Multiverse.Services
{
    internal sealed class AudioProcessOption
    {
        public int ProcessId { get; init; }
        public string ProcessName { get; init; } = "";
        public string DisplayName { get; init; } = "";

        public override string ToString() => DisplayName;
    }

    internal sealed class AudioOutputDeviceOption
    {
        public string DeviceId { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public bool IsDefault { get; init; }

        public override string ToString() => DisplayName;
    }

    internal sealed class RemoteAudioSnapshot
    {
        public bool HostCaptureRunning { get; init; }
        public bool ClientPlaybackRunning { get; init; }
        public int? SelectedProcessId { get; init; }
        public string SelectedProcessName { get; init; } = "";
        public string SelectedOutputDeviceId { get; init; } = "";
        public string SelectedOutputDeviceName { get; init; } = "Padrão do Windows";
        public int ClientVolumePercent { get; init; } = 100;
        public AudioCaptureMode CaptureMode { get; init; } = AudioCaptureMode.SpecificApplication;
        public int ActiveAutomaticSources { get; init; }
        public bool VoicePrivacyVerified { get; init; }
        public bool VoicePrivacyFailClosed { get; init; }
        public int VoicePrivacyBlockedSources { get; init; }
        public string VoicePrivacyStatus { get; init; } = "";
        public string VoicePrivacyError { get; init; } = "";
        public long SentPackets { get; init; }
        public long ReceivedPackets { get; init; }
        public long DroppedPackets { get; init; }
        public double BufferedMs { get; init; }
        public string StatusText { get; init; } = "";
        public string LastError { get; init; } = "";
    }

    /// <summary>
    /// Independent process-audio pipeline. Nothing in this service owns the
    /// video/session lifecycle: audio can stop/restart without touching WGC,
    /// NVENC, UDP video, the remote presenter or the gamepad subsystem.
    /// </summary>
    internal sealed class RemoteAudioService : IDisposable
    {
        private static readonly Lazy<RemoteAudioService> LazyInstance =
            new(() => new RemoteAudioService());

        public static RemoteAudioService Instance => LazyInstance.Value;
        internal static bool IsValueCreated => LazyInstance.IsValueCreated;

        private const int SampleRate = 48000;
        private const int Channels = 2;
        private const int BitsPerSample = 16;
        private const int FrameSamplesPerChannel = 960; // 20 ms @ 48 kHz
        private const int Bitrate = 128000;
        private const int BytesPerSample = BitsPerSample / 8;
        private const int FrameBytes =
            FrameSamplesPerChannel * Channels * BytesPerSample;
        private const int MaxPcmQueueChunks = 6;
        private const int PlaybackLatencyMs = 60;
        private const int MaxPlcPacketsPerGap = 2;

        private readonly object _sync = new();
        private readonly SemaphoreSlim _playbackGate = new(1, 1);
        private int _playbackGeneration;
        private readonly RemoteSessionService _session = RemoteSessionService.Instance;
        private readonly MultiverseSettingsService _settings = MultiverseSettingsService.Instance;
        private readonly AudioApplicationDiscoveryService _discovery = new();

        private AudioCaptureMode _captureMode;
        private int? _selectedProcessId;
        private string _selectedProcessName = "";
        private ProcessLoopbackAudioCapture? _capture;
        private AutomaticProcessAudioMixer? _automaticMixer;
        private RemoteAudioSender? _sender;
        private Channel<byte[]>? _pcmQueue;
        private CancellationTokenSource? _encodeCancel;
        private Task? _encodeWorker;

        private RemoteAudioReceiver? _receiver;
        private IOpusDecoder? _decoder;
        private BufferedWaveProvider? _playbackBuffer;
        private WasapiOut? _playback;
        private MMDevice? _playbackDevice;
        private string _selectedOutputDeviceId = "";
        private string _selectedOutputDeviceName = "Padrão do Windows";
        private int _clientVolumePercent = 100;

        private uint _txSequence;
        private uint _lastRxSequence;
        private bool _haveRxSequence;
        private long _sentPackets;
        private long _receivedPackets;
        private long _droppedPackets;
        private string _lastError = "";
        private string _statusText = "Áudio parado";
        private bool _disposed;

        public event EventHandler? AudioChanged;

        private RemoteAudioService()
        {
            _selectedOutputDeviceId = _settings.GetString(
                MultiverseSettingsService.AudioOutputDeviceIdKey,
                "");
            _selectedOutputDeviceName = _settings.GetString(
                MultiverseSettingsService.AudioOutputDeviceNameKey,
                string.IsNullOrWhiteSpace(_selectedOutputDeviceId)
                    ? "Padrão do Windows"
                    : "Dispositivo salvo");
            _clientVolumePercent = Math.Clamp(
                _settings.GetInt(
                    MultiverseSettingsService.AudioClientVolumePercentKey,
                    100),
                0,
                200);

            _captureMode = ParseCaptureMode(_settings.GetString(
                MultiverseSettingsService.AudioCaptureModeKey,
                AudioCaptureMode.SpecificApplication.ToString()));
            _selectedProcessName = AudioApplicationPolicy.NormalizeExecutable(
                _settings.GetString(MultiverseSettingsService.AudioManualProcessNameKey, ""));

            _session.SessionChanged += Session_SessionChanged;
            ApplySessionState();
        }

        public RemoteAudioSnapshot GetSnapshot()
        {
            lock (_sync)
            {
                double bufferedMs = 0;
                if (_playbackBuffer is not null)
                {
                    try
                    {
                        int bytesPerSecond = SampleRate * Channels * BytesPerSample;
                        bufferedMs = bytesPerSecond > 0
                            ? _playbackBuffer.BufferedBytes * 1000.0 / bytesPerSecond
                            : 0;
                    }
                    catch { }
                }

                return new RemoteAudioSnapshot
                {
                    HostCaptureRunning = ((_capture?.Running == true) || (_automaticMixer?.Running == true)) && _sender is not null,
                    ClientPlaybackRunning = _receiver is not null && _playback is not null,
                    SelectedProcessId = _selectedProcessId,
                    SelectedProcessName = _selectedProcessName,
                    SelectedOutputDeviceId = _selectedOutputDeviceId,
                    SelectedOutputDeviceName = _selectedOutputDeviceName,
                    ClientVolumePercent = _clientVolumePercent,
                    CaptureMode = _captureMode,
                    ActiveAutomaticSources = _automaticMixer?.ActiveSourceCount ?? 0,
                    VoicePrivacyVerified = _automaticMixer?.VoicePrivacyVerified ?? false,
                    VoicePrivacyFailClosed = _automaticMixer?.VoicePrivacyFailClosed ?? false,
                    VoicePrivacyBlockedSources = _automaticMixer?.VoicePrivacyBlockedSources ?? 0,
                    VoicePrivacyStatus = _automaticMixer?.VoicePrivacyStatus ?? "Privacidade de voz automática inativa.",
                    VoicePrivacyError = _automaticMixer?.VoicePrivacyError ?? "",
                    SentPackets = _sentPackets,
                    ReceivedPackets = _receivedPackets,
                    DroppedPackets = _droppedPackets,
                    BufferedMs = bufferedMs,
                    StatusText = _statusText,
                    LastError = _lastError
                };
            }
        }

        public IReadOnlyList<AudioProcessOption> EnumerateAudioProcesses()
        {
            try
            {
                return _discovery.EnumerateActiveApplications()
                    .Select(app => new AudioProcessOption
                    {
                        ProcessId = app.RootProcessId,
                        ProcessName = app.ProcessName,
                        DisplayName = app.DisplayName
                    })
                    .ToArray();
            }
            catch (Exception ex)
            {
                SetError("Não foi possível listar os aplicativos com áudio.", ex);
                return Array.Empty<AudioProcessOption>();
            }
        }


        public AudioCaptureMode CaptureMode
        {
            get { lock (_sync) return _captureMode; }
        }

        public void SetCaptureMode(AudioCaptureMode mode)
        {
            bool changed;
            lock (_sync)
            {
                changed = _captureMode != mode;
                _captureMode = mode;
                _lastError = "";
                if (_sender is null)
                {
                    _statusText = mode == AudioCaptureMode.AutomaticGamesAndMedia
                        ? "Automático pronto • jogos e mídia, comunicação excluída."
                        : (_selectedProcessId is null
                            ? "Selecione manualmente o aplicativo de áudio."
                            : $"Selecionado: {_selectedProcessName}");
                }
            }
            _settings.Set(MultiverseSettingsService.AudioCaptureModeKey, mode.ToString());
            if (changed)
            {
                StopHostCaptureOnly();
                if (mode == AudioCaptureMode.AutomaticGamesAndMedia)
                    _ = EnsureAutomaticForTransmissionAsync();
                else
                    _ = EnsureSpecificForTransmissionAsync();
            }
            RaiseAudioChanged();
        }

        public IReadOnlyList<string> GetAutomaticExclusions()
        {
            try
            {
                string raw = _settings.GetString(MultiverseSettingsService.AudioExclusionsKey, "");
                if (string.IsNullOrWhiteSpace(raw))
                    return AudioApplicationPolicy.MergeWithDefaultCommunicationExclusions(
                        Array.Empty<string>());

                string[]? parsed = JsonSerializer.Deserialize<string[]>(raw);
                if (parsed is null)
                    throw new JsonException("Lista vazia.");
                return AudioApplicationPolicy.MergeWithDefaultCommunicationExclusions(parsed);
            }
            catch
            {
                return AudioApplicationPolicy.MergeWithDefaultCommunicationExclusions(
                    Array.Empty<string>());
            }
        }

        public void SetAutomaticExclusions(IEnumerable<string> exclusions)
        {
            IReadOnlyList<string> normalized = AudioApplicationPolicy.MergeWithDefaultCommunicationExclusions(exclusions);
            _settings.Set(
                MultiverseSettingsService.AudioExclusionsKey,
                JsonSerializer.Serialize(normalized));
            RaiseAudioChanged();
        }

        private static AudioCaptureMode ParseCaptureMode(string? value) =>
            Enum.TryParse(value, true, out AudioCaptureMode mode)
                ? mode
                : AudioCaptureMode.SpecificApplication;

        public IReadOnlyList<AudioOutputDeviceOption> EnumerateOutputDevices()
        {
            var result = new List<AudioOutputDeviceOption>();
            try
            {
                using MMDeviceEnumerator enumerator = new();
                string defaultId = "";
                try
                {
                    using MMDevice defaultDevice = enumerator.GetDefaultAudioEndpoint(
                        DataFlow.Render,
                        Role.Multimedia);
                    defaultId = defaultDevice.ID;
                }
                catch { }

                foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(
                    DataFlow.Render,
                    DeviceState.Active))
                {
                    try
                    {
                        result.Add(new AudioOutputDeviceOption
                        {
                            DeviceId = device.ID,
                            DisplayName = device.FriendlyName,
                            IsDefault = string.Equals(
                                device.ID,
                                defaultId,
                                StringComparison.OrdinalIgnoreCase)
                        });
                    }
                    finally
                    {
                        device.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                SetError("Não foi possível listar os dispositivos de saída de áudio.", ex);
            }

            AudioOutputDeviceOption[] endpoints = result
                .OrderByDescending(item => item.IsDefault)
                .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            return new[]
                {
                    new AudioOutputDeviceOption
                    {
                        DeviceId = "",
                        DisplayName = "Padrão do Windows (automático)",
                        IsDefault = true
                    }
                }
                .Concat(endpoints)
                .ToArray();
        }

        public void SelectClientOutputDevice(string? deviceId, string? displayName = null)
        {
            string normalizedId = deviceId ?? "";
            string normalizedName = string.IsNullOrWhiteSpace(displayName)
                ? "Padrão do Windows"
                : displayName.Trim();
            bool changed;
            bool restartPlayback;

            lock (_sync)
            {
                changed = !string.Equals(
                    _selectedOutputDeviceId,
                    normalizedId,
                    StringComparison.Ordinal);
                _selectedOutputDeviceId = normalizedId;
                _selectedOutputDeviceName = normalizedName;
                restartPlayback = changed && _receiver is not null;
            }

            _settings.SaveAudioClientPlayback(
                normalizedId,
                normalizedName,
                ClientVolumePercent);

            if (restartPlayback)
                _ = SwitchClientOutputAsync();

            RaiseAudioChanged();
        }

        public void SetClientVolumePercent(int volumePercent)
        {
            int clamped = Math.Clamp(volumePercent, 0, 200);
            string deviceId;
            string deviceName;
            bool changed;
            lock (_sync)
            {
                changed = _clientVolumePercent != clamped;
                _clientVolumePercent = clamped;
                deviceId = _selectedOutputDeviceId;
                deviceName = _selectedOutputDeviceName;
            }

            _settings.SaveAudioClientPlayback(deviceId, deviceName, clamped);
            if (changed)
                RaiseAudioChanged();
        }

        private int ClientVolumePercent
        {
            get
            {
                lock (_sync)
                    return _clientVolumePercent;
            }
        }

        public void SelectProcess(int? processId, string? processName = null)
        {
            bool changed;
            lock (_sync)
            {
                changed = _selectedProcessId != processId;
                _selectedProcessId = processId;
                _selectedProcessName = processId is null
                    ? ""
                    : (processName ?? $"PID {processId.Value}");
                _lastError = "";
                if (_capture is null)
                    _statusText = processId is null
                        ? "Selecione o aplicativo que deseja transmitir."
                        : $"Selecionado: {_selectedProcessName}";
            }

            _settings.Set(
                MultiverseSettingsService.AudioManualProcessNameKey,
                _selectedProcessName);

            if (changed)
            {
                StopHostCaptureOnly();
                if (processId is not null && CaptureMode == AudioCaptureMode.SpecificApplication)
                    _ = EnsureSpecificForTransmissionAsync();
            }
            RaiseAudioChanged();
        }

        public async Task<bool> EnsureSpecificForTransmissionAsync()
        {
            if (CaptureMode != AudioCaptureMode.SpecificApplication)
                return false;

            try
            {
                RemoteSessionSnapshot session = _session.GetSnapshot();
                IPEndPoint? remote = _session.GetRemoteVideoEndpoint();
                if (!session.Connected ||
                    session.Role != RemoteSessionRole.Host ||
                    session.StreamConfig is null ||
                    remote is null ||
                    !session.RemoteAudioAllowed)
                {
                    return false;
                }

                lock (_sync)
                {
                    if (_selectedProcessId is null)
                        return false;
                    if (_capture is not null && _sender is not null)
                        return true;
                }

                return await StartManualAsync(remote).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SetError("O áudio do aplicativo selecionado não conseguiu acompanhar a transmissão.", ex);
                return false;
            }
        }

        public async Task<bool> EnsureAutomaticForTransmissionAsync()
        {
            if (CaptureMode != AudioCaptureMode.AutomaticGamesAndMedia)
                return false;

            try
            {
                RemoteSessionSnapshot session = _session.GetSnapshot();
                IPEndPoint? remote = _session.GetRemoteVideoEndpoint();
                if (!session.Connected ||
                    session.Role != RemoteSessionRole.Host ||
                    session.StreamConfig is null ||
                    remote is null ||
                    !session.RemoteAudioAllowed)
                {
                    return false;
                }

                lock (_sync)
                {
                    if (_automaticMixer?.Running == true && _sender is not null)
                        return true;
                }

                return await StartAutomaticAsync(remote).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SetError("O áudio automático não conseguiu acompanhar a transmissão.", ex);
                return false;
            }
        }

        public async Task<bool> StartSelectedAsync()
        {
            RemoteSessionSnapshot session = _session.GetSnapshot();
            IPEndPoint? remote = _session.GetRemoteVideoEndpoint();
            if (!session.Connected || session.Role != RemoteSessionRole.Host || remote is null)
            {
                SetError("Conecte uma sessão como host antes de transmitir áudio.");
                return false;
            }

            if (!session.RemoteAudioAllowed)
            {
                SetError("Este amigo não tem permissão para receber áudio remoto.");
                return false;
            }

            return CaptureMode == AudioCaptureMode.AutomaticGamesAndMedia
                ? await StartAutomaticAsync(remote).ConfigureAwait(false)
                : await StartManualAsync(remote).ConfigureAwait(false);
        }

        private async Task<bool> StartManualAsync(IPEndPoint remote)
        {
            int pid;
            string selectedName;
            lock (_sync)
            {
                if (_selectedProcessId is null)
                {
                    _lastError = "Selecione um aplicativo antes de iniciar o áudio.";
                    _statusText = _lastError;
                    RaiseAudioChangedDeferred();
                    return false;
                }
                pid = _selectedProcessId.Value;
                selectedName = _selectedProcessName;
            }

            StopHostCaptureOnly();
            if (HasHostAudioResources())
            {
                SetError("A captura de áudio anterior ainda está encerrando. Tente novamente em instantes.");
                return false;
            }
            PrepareHostEncodePipeline(remote, out RemoteAudioSender sender, out Channel<byte[]> pcmQueue, out CancellationTokenSource encodeCancel, out Task encodeWorker);
            var capture = new ProcessLoopbackAudioCapture(pid);
            capture.PcmAvailable += pcm => QueuePcm(pcmQueue, pcm);
            capture.CaptureFailed += ex =>
            {
                SetError("A captura de áudio do aplicativo foi interrompida.", ex);
                _ = Task.Run(StopHostCaptureOnly);
            };

            lock (_sync)
            {
                _sender = sender;
                _pcmQueue = pcmQueue;
                _encodeCancel = encodeCancel;
                _encodeWorker = encodeWorker;
                _capture = capture;
                _automaticMixer = null;
                _lastError = "";
                _statusText = $"Abrindo áudio de {selectedName}...";
            }
            RaiseAudioChanged();

            try
            {
                await capture.StartAsync().ConfigureAwait(false);
                lock (_sync)
                    _statusText = $"Transmitindo áudio: {selectedName}";
                RaiseAudioChanged();
                return true;
            }
            catch (Exception ex)
            {
                SetError("Não foi possível capturar o áudio do aplicativo selecionado.", ex);
                StopHostCaptureOnly();
                return false;
            }
        }

        private async Task<bool> StartAutomaticAsync(IPEndPoint remote)
        {
            StopHostCaptureOnly();
            if (HasHostAudioResources())
            {
                SetError("A captura de áudio anterior ainda está encerrando. Tente novamente em instantes.");
                return false;
            }
            PrepareHostEncodePipeline(remote, out RemoteAudioSender sender, out Channel<byte[]> pcmQueue, out CancellationTokenSource encodeCancel, out Task encodeWorker);
            var mixer = new AutomaticProcessAudioMixer(_discovery, GetAutomaticExclusions);
            mixer.PcmAvailable += pcm => QueuePcm(pcmQueue, pcm);
            mixer.SourceFailed += (_, ex) =>
            {
                lock (_sync)
                    _lastError = $"Uma fonte automática de áudio falhou: {ex.Message}";
                RaiseAudioChanged();
            };
            mixer.SourcesChanged += (_, _) =>
            {
                lock (_sync)
                {
                    if (_automaticMixer == mixer)
                        _statusText = BuildAutomaticStatus(mixer);
                }
                RaiseAudioChanged();
            };

            lock (_sync)
            {
                _sender = sender;
                _pcmQueue = pcmQueue;
                _encodeCancel = encodeCancel;
                _encodeWorker = encodeWorker;
                _capture = null;
                _automaticMixer = mixer;
                _lastError = "";
                _statusText = "Iniciando captura automática de jogos e mídia...";
            }
            RaiseAudioChanged();

            try
            {
                await mixer.StartAsync().ConfigureAwait(false);
                lock (_sync)
                    _statusText = BuildAutomaticStatus(mixer);
                RaiseAudioChanged();
                return true;
            }
            catch (Exception ex)
            {
                SetError("Não foi possível iniciar a captura automática de áudio.", ex);
                StopHostCaptureOnly();
                return false;
            }
        }

        private static string BuildAutomaticStatus(AutomaticProcessAudioMixer mixer)
        {
            if (mixer.VoicePrivacyFailClosed)
                return "Áudio automático pausado pela proteção de voz.";

            return mixer.ActiveSourceCount > 0
                ? $"Áudio automático • {mixer.ActiveSourceCount} fonte(s) ativa(s)"
                : "Áudio automático • aguardando jogos ou mídia...";
        }

        private void PrepareHostEncodePipeline(
            IPEndPoint remote,
            out RemoteAudioSender sender,
            out Channel<byte[]> pcmQueue,
            out CancellationTokenSource encodeCancel,
            out Task encodeWorker)
        {
            sender = new RemoteAudioSender(remote.Address);
            pcmQueue = Channel.CreateBounded<byte[]>(
                new BoundedChannelOptions(MaxPcmQueueChunks)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.DropOldest,
                    AllowSynchronousContinuations = false
                });
            encodeCancel = new CancellationTokenSource();

            // C# does not allow out/ref parameters to be captured by a lambda.
            // Copy the initialized pipeline objects to locals before Task.Run.
            RemoteAudioSender encodeSender = sender;
            Channel<byte[]> encodeQueue = pcmQueue;
            CancellationToken encodeToken = encodeCancel.Token;
            encodeWorker = Task.Run(
                () => EncodeLoopAsync(encodeQueue.Reader, encodeSender, encodeToken));
        }

        private void QueuePcm(Channel<byte[]> queue, ReadOnlyMemory<byte> pcm)
        {
            if (pcm.Length == 0)
                return;

            byte[] chunk;
            if (MemoryMarshal.TryGetArray(pcm, out ArraySegment<byte> segment) &&
                segment.Array is not null &&
                segment.Offset == 0 &&
                segment.Count == segment.Array.Length)
            {
                // Both ProcessLoopbackAudioCapture and the automatic mixer hand
                // off a freshly owned byte[] and never reuse it after the event.
                // Queue that buffer directly instead of allocating/copying it.
                chunk = segment.Array;
            }
            else
            {
                chunk = pcm.ToArray();
            }

            if (!queue.Writer.TryWrite(chunk))
                Interlocked.Increment(ref _droppedPackets);
        }


        private async Task EncodeLoopAsync(
            ChannelReader<byte[]> reader,
            RemoteAudioSender sender,
            CancellationToken token)
        {
            try
            {
                IOpusEncoder encoder = OpusCodecFactory.CreateEncoder(
                    SampleRate,
                    Channels,
                    OpusApplication.OPUS_APPLICATION_AUDIO);
                encoder.Bitrate = Bitrate;

                byte[] frame = new byte[FrameBytes];
                short[] pcm = new short[FrameSamplesPerChannel * Channels];
                byte[] encodedBuffer = new byte[RemoteAudioSender.MaxPayloadBytes];
                int filled = 0;

                while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
                {
                    while (reader.TryRead(out byte[]? chunk))
                    {
                        int offset = 0;
                        while (offset < chunk.Length)
                        {
                            int copy = Math.Min(FrameBytes - filled, chunk.Length - offset);
                            Buffer.BlockCopy(chunk, offset, frame, filled, copy);
                            offset += copy;
                            filled += copy;

                            if (filled != FrameBytes)
                                continue;

                            Buffer.BlockCopy(frame, 0, pcm, 0, FrameBytes);
                            int encoded = encoder.Encode(
                                pcm,
                                FrameSamplesPerChannel,
                                encodedBuffer,
                                encodedBuffer.Length);
                            if (encoded > 0 && encoded <= RemoteAudioSender.MaxPayloadBytes)
                            {
                                byte[] payload = new byte[encoded];
                                Buffer.BlockCopy(encodedBuffer, 0, payload, 0, encoded);
                                uint sequence;
                                lock (_sync)
                                    sequence = ++_txSequence;

                                bool queued = sender.TryQueue(new RemoteAudioDatagram
                                {
                                    Sequence = sequence,
                                    TimestampUs = Stopwatch.GetTimestamp() * 1_000_000L /
                                        Stopwatch.Frequency,
                                    SamplesPerChannel = FrameSamplesPerChannel,
                                    Payload = payload
                                });
                                if (queued)
                                    Interlocked.Increment(ref _sentPackets);
                                else
                                    Interlocked.Increment(ref _droppedPackets);
                            }
                            filled = 0;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                SetError("O encoder Opus de áudio foi interrompido.", ex);
                _ = Task.Run(StopHostCaptureOnly);
            }
        }

        private void StartClientPlaybackOnly() =>
            _ = StartClientPlaybackAsync();

        private async Task StartClientPlaybackAsync()
        {
            await _playbackGate.WaitAsync();
            try
            {
                RemoteSessionSnapshot session = _session.GetSnapshot();
                IPEndPoint? remote = _session.GetRemoteVideoEndpoint();
                if (!session.Connected || session.Role != RemoteSessionRole.Client || remote is null)
                    return;

                lock (_sync)
                {
                    if (_receiver is not null)
                        return;
                }

                RemoteAudioReceiver? receiver = null;
                WasapiOut? playback = null;
                MMDevice? device = null;
                try
                {
                    IOpusDecoder decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels);
                    var buffer = new BufferedWaveProvider(
                        new WaveFormat(SampleRate, BitsPerSample, Channels))
                    {
                        BufferDuration = TimeSpan.FromMilliseconds(100),
                        DiscardOnBufferOverflow = true,
                        ReadFully = true
                    };

                    device = OpenSelectedOutputDevice();
                    playback = new WasapiOut(
                        device,
                        AudioClientShareMode.Shared,
                        true,
                        PlaybackLatencyMs);
                    playback.Init(buffer);
                    playback.Play();

                    receiver = new RemoteAudioReceiver(remote.Address);
                    receiver.PacketReceived += Receiver_PacketReceived;

                    // A disconnect may happen while WASAPI is being initialized.
                    // Never install newly-created playback objects into a dead session.
                    session = _session.GetSnapshot();
                    if (!session.Connected || session.Role != RemoteSessionRole.Client)
                    {
                        receiver.PacketReceived -= Receiver_PacketReceived;
                        return;
                    }

                    lock (_sync)
                    {
                        Interlocked.Increment(ref _playbackGeneration);
                        _decoder = decoder;
                        _playbackBuffer = buffer;
                        _playback = playback;
                        _playbackDevice = device;
                        _receiver = receiver;
                        _lastRxSequence = 0;
                        _haveRxSequence = false;
                        _lastError = "";
                        _statusText = "Cliente de áudio pronto • aguardando o host.";
                    }

                    receiver.Start();
                    RaiseAudioChanged();
                    receiver = null;
                    playback = null;
                    device = null;
                }
                catch (Exception ex)
                {
                    SetError("Não foi possível iniciar a reprodução do áudio remoto.", ex);
                    StopClientPlaybackOnly();
                }
                finally
                {
                    if (receiver is not null)
                    {
                        try { receiver.PacketReceived -= Receiver_PacketReceived; } catch { }
                        try { receiver.Dispose(); } catch { }
                    }
                    try { playback?.Stop(); playback?.Dispose(); } catch { }
                    try { device?.Dispose(); } catch { }
                }
            }
            finally
            {
                _playbackGate.Release();
            }
        }

        private void Receiver_PacketReceived(RemoteAudioDatagram datagram)
        {
            IOpusDecoder? decoder;
            BufferedWaveProvider? buffer;
            uint previous;
            bool havePrevious;
            int generation;
            lock (_sync)
            {
                decoder = _decoder;
                buffer = _playbackBuffer;
                previous = _lastRxSequence;
                havePrevious = _haveRxSequence;
                generation = _playbackGeneration;

                if (havePrevious && !IsNewerSequence(datagram.Sequence, previous))
                {
                    _droppedPackets++;
                    return;
                }

                _lastRxSequence = datagram.Sequence;
                _haveRxSequence = true;
            }

            if (decoder is null || buffer is null)
                return;

            if (generation != _playbackGeneration)
                return;

            short[] pcm = ArrayPool<short>.Shared.Rent(
                FrameSamplesPerChannel * Channels);
            try
            {
                if (havePrevious)
                {
                    uint missing = datagram.Sequence - previous - 1;
                    int plcPackets = (int)Math.Min(missing, (uint)MaxPlcPacketsPerGap);
                    for (int i = 0; i < plcPackets; ++i)
                    {
                        if (generation != _playbackGeneration)
                            return;

                        int plcSamples = decoder.Decode(
                            ReadOnlySpan<byte>.Empty,
                            pcm,
                            FrameSamplesPerChannel,
                            false);

                        if (generation != _playbackGeneration)
                            return;

                        AddDecodedSamples(buffer, pcm, plcSamples);
                    }
                    if (missing > 0)
                        Interlocked.Add(ref _droppedPackets, missing);
                }

                if (generation != _playbackGeneration)
                    return;

                int samples = decoder.Decode(
                    datagram.Payload,
                    pcm,
                    datagram.SamplesPerChannel == 0
                        ? FrameSamplesPerChannel
                        : datagram.SamplesPerChannel,
                    false);

                if (generation != _playbackGeneration)
                    return;

                AddDecodedSamples(buffer, pcm, samples);
                Interlocked.Increment(ref _receivedPackets);

                lock (_sync)
                {
                    if (generation == _playbackGeneration)
                        _statusText = $"Recebendo áudio • buffer {GetBufferedMs(buffer):0} ms";
                }
            }
            catch (Exception ex)
            {
                // A packet that belonged to the previous WASAPI generation is
                // expected to be abandoned during a device switch/stop.
                if (generation != _playbackGeneration)
                    return;

                SetError("Um pacote de áudio remoto não pôde ser reproduzido.", ex);
            }
            finally
            {
                ArrayPool<short>.Shared.Return(pcm, clearArray: false);
            }
        }

        private static bool IsNewerSequence(uint candidate, uint previous) =>
            unchecked((int)(candidate - previous)) > 0;

        private void AddDecodedSamples(
            BufferedWaveProvider buffer,
            short[] pcm,
            int samplesPerChannel)
        {
            if (samplesPerChannel <= 0)
                return;

            int sampleCount = checked(samplesPerChannel * Channels);
            ApplyClientGainInPlace(pcm, sampleCount, ClientVolumePercent);
            int byteCount = checked(sampleCount * BytesPerSample);
            byte[] bytes = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                Buffer.BlockCopy(pcm, 0, bytes, 0, byteCount);

                // A bounded playout buffer is more important than perfect delivery
                // for remote gaming. If the client drifts behind, discard old sound
                // rather than allowing audio latency to accumulate indefinitely.
                if (GetBufferedMs(buffer) > 80)
                    buffer.ClearBuffer();

                // BufferedWaveProvider copies the samples into its own bounded
                // circular buffer, so this temporary conversion array can be
                // returned immediately after AddSamples completes.
                buffer.AddSamples(bytes, 0, byteCount);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bytes, clearArray: false);
            }
        }

        internal static void ApplyClientGainInPlace(
            short[] samples,
            int sampleCount,
            int volumePercent)
        {
            if (samples is null || sampleCount <= 0)
                return;

            int count = Math.Min(sampleCount, samples.Length);
            int clampedVolume = Math.Clamp(volumePercent, 0, 200);
            if (clampedVolume == 100)
                return;
            if (clampedVolume == 0)
            {
                Array.Clear(samples, 0, count);
                return;
            }

            float gain = clampedVolume / 100.0f;
            for (int i = 0; i < count; ++i)
            {
                int amplified = (int)MathF.Round(samples[i] * gain);
                samples[i] = (short)Math.Clamp(
                    amplified,
                    short.MinValue,
                    short.MaxValue);
            }
        }

        private MMDevice OpenSelectedOutputDevice()
        {
            using MMDeviceEnumerator enumerator = new();
            string selectedId;
            lock (_sync)
                selectedId = _selectedOutputDeviceId;

            if (!string.IsNullOrWhiteSpace(selectedId))
            {
                try
                {
                    return enumerator.GetDevice(selectedId);
                }
                catch
                {
                    // Device may have been unplugged. Fall back to the current
                    // Windows multimedia default without breaking the session.
                }
            }

            return enumerator.GetDefaultAudioEndpoint(
                DataFlow.Render,
                Role.Multimedia);
        }

        private async Task SwitchClientOutputAsync()
        {
            await _playbackGate.WaitAsync();
            try
            {
                RemoteSessionSnapshot session = _session.GetSnapshot();
                if (!session.Connected || session.Role != RemoteSessionRole.Client)
                    return;

                lock (_sync)
                {
                    if (_receiver is null)
                        return;
                }

                MMDevice? newDevice = null;
                WasapiOut? newPlayback = null;
                try
                {
                    newDevice = OpenSelectedOutputDevice();
                    var newBuffer = new BufferedWaveProvider(
                        new WaveFormat(SampleRate, BitsPerSample, Channels))
                    {
                        BufferDuration = TimeSpan.FromMilliseconds(100),
                        DiscardOnBufferOverflow = true,
                        ReadFully = true
                    };
                    newPlayback = new WasapiOut(
                        newDevice,
                        AudioClientShareMode.Shared,
                        true,
                        PlaybackLatencyMs);
                    newPlayback.Init(newBuffer);
                    newPlayback.Play();

                    // Session may have disappeared while COM/WASAPI was opening.
                    session = _session.GetSnapshot();
                    if (!session.Connected || session.Role != RemoteSessionRole.Client)
                        return;

                    WasapiOut? oldPlayback;
                    MMDevice? oldDevice;
                    lock (_sync)
                    {
                        if (_receiver is null)
                            return;

                        oldPlayback = _playback;
                        oldDevice = _playbackDevice;

                        // Invalidate any packet callback that captured the old buffer
                        // before publishing the new playback generation.
                        Interlocked.Increment(ref _playbackGeneration);
                        _playback = newPlayback;
                        _playbackDevice = newDevice;
                        _playbackBuffer = newBuffer;
                        _lastError = "";
                        _statusText = $"Saída: {_selectedOutputDeviceName}";
                    }

                    newPlayback = null;
                    newDevice = null;
                    try { oldPlayback?.Stop(); oldPlayback?.Dispose(); } catch { }
                    try { oldDevice?.Dispose(); } catch { }
                    RaiseAudioChanged();
                }
                catch (Exception ex)
                {
                    SetError("Não foi possível trocar o dispositivo de saída do áudio remoto.", ex);
                }
                finally
                {
                    try { newPlayback?.Stop(); newPlayback?.Dispose(); } catch { }
                    try { newDevice?.Dispose(); } catch { }
                }
            }
            finally
            {
                _playbackGate.Release();
            }
        }

        private static double GetBufferedMs(BufferedWaveProvider buffer)
        {
            int bytesPerSecond = SampleRate * Channels * BytesPerSample;
            return bytesPerSecond > 0
                ? buffer.BufferedBytes * 1000.0 / bytesPerSecond
                : 0;
        }

        private void Session_SessionChanged(object? sender, EventArgs e) =>
            ApplySessionState();

        private void ApplySessionState()
        {
            RemoteSessionSnapshot session = _session.GetSnapshot();
            if (!session.Connected)
            {
                StopAudioOnly();
                return;
            }

            if (session.Role == RemoteSessionRole.Client)
            {
                StopHostCaptureOnly();
                if (session.StreamConfig is null)
                {
                    StopClientPlaybackOnly();
                    lock (_sync)
                        _statusText = "Aguardando transmissão do host.";
                    RaiseAudioChanged();
                }
                else
                {
                    StartClientPlaybackOnly();
                }
            }
            else
            {
                StopClientPlaybackOnly();
                if (!session.RemoteAudioAllowed)
                {
                    StopHostCaptureOnly();
                    lock (_sync)
                    {
                        _lastError = "Este amigo não tem permissão para receber áudio remoto.";
                        _statusText = _lastError;
                    }
                    RaiseAudioChanged();
                    return;
                }
                lock (_sync)
                {
                    if (_capture is null && _automaticMixer is null)
                        _statusText = _captureMode == AudioCaptureMode.AutomaticGamesAndMedia
                            ? "Automático pronto • jogos e mídia, comunicação excluída."
                            : (_selectedProcessId is null
                                ? "Selecione manualmente o aplicativo de áudio."
                                : $"Selecionado: {_selectedProcessName}");
                }
                RaiseAudioChanged();
            }
        }

        private bool HasHostAudioResources()
        {
            lock (_sync)
                return _capture is not null || _automaticMixer is not null ||
                    _sender is not null || _pcmQueue is not null ||
                    _encodeCancel is not null || _encodeWorker is not null;
        }

        public void StopHostCaptureOnly()
        {
            ProcessLoopbackAudioCapture? capture;
            AutomaticProcessAudioMixer? mixer;
            RemoteAudioSender? sender;
            Channel<byte[]>? queue;
            CancellationTokenSource? cancel;
            Task? worker;

            lock (_sync)
            {
                capture = _capture;
                mixer = _automaticMixer;
                sender = _sender;
                queue = _pcmQueue;
                cancel = _encodeCancel;
                worker = _encodeWorker;
            }

            if (capture is null && mixer is null && sender is null &&
                queue is null && cancel is null && worker is null)
                return;

            try { queue?.Writer.TryComplete(); } catch { }
            try { cancel?.Cancel(); } catch { }

            try
            {
                capture?.StopAudioOnly();
                capture?.Dispose();
                mixer?.Stop();
                mixer?.Dispose();

                try
                {
                    ConfirmedTask.WaitOrThrow(worker, 1000, "Audio encode");
                }
                catch (Exception ex)
                {
                    StartupDiagnostics.RecordException("AUDIO_ENCODE_STOP_TIMEOUT", ex);
                    throw;
                }

                sender?.Dispose();
            }
            catch (Exception ex)
            {
                // Do not drop service ownership after an unconfirmed stop. A
                // later Stop can retry the same resources; a new audio pipeline
                // will not be published over them.
                StartupDiagnostics.RecordException("AUDIO_HOST_STOP_INCOMPLETE", ex);
                lock (_sync)
                {
                    _lastError = "Parada de áudio incompleta: " + ex.Message;
                    _statusText = _lastError;
                }
                RaiseAudioChanged();
                return;
            }

            lock (_sync)
            {
                if (ReferenceEquals(_capture, capture)) _capture = null;
                if (ReferenceEquals(_automaticMixer, mixer)) _automaticMixer = null;
                if (ReferenceEquals(_sender, sender)) _sender = null;
                if (ReferenceEquals(_pcmQueue, queue)) _pcmQueue = null;
                if (ReferenceEquals(_encodeCancel, cancel)) _encodeCancel = null;
                if (ReferenceEquals(_encodeWorker, worker)) _encodeWorker = null;
                if (string.IsNullOrWhiteSpace(_lastError) ||
                    _lastError.StartsWith("Parada de áudio incompleta", StringComparison.Ordinal))
                {
                    _lastError = "";
                    _statusText = _captureMode == AudioCaptureMode.AutomaticGamesAndMedia
                        ? "Áudio automático parado"
                        : (_selectedProcessId is null ? "Áudio parado" : $"Selecionado: {_selectedProcessName}");
                }
            }
            try { cancel?.Dispose(); } catch { }
            RaiseAudioChanged();
        }

        private void StopClientPlaybackOnly()
        {
            RemoteAudioReceiver? receiver;
            WasapiOut? playback;
            MMDevice? playbackDevice;
            lock (_sync)
            {
                Interlocked.Increment(ref _playbackGeneration);
                receiver = _receiver;
                playback = _playback;
                playbackDevice = _playbackDevice;
            }

            if (receiver is not null)
                receiver.PacketReceived -= Receiver_PacketReceived;

            try
            {
                receiver?.Dispose();
                playback?.Stop();
                playback?.Dispose();
                playbackDevice?.Dispose();
            }
            catch (Exception ex)
            {
                StartupDiagnostics.RecordException("AUDIO_CLIENT_STOP_INCOMPLETE", ex);
                lock (_sync)
                {
                    _lastError = "Parada do áudio remoto incompleta: " + ex.Message;
                    _statusText = _lastError;
                }
                RaiseAudioChanged();
                return;
            }

            lock (_sync)
            {
                if (ReferenceEquals(_receiver, receiver)) _receiver = null;
                if (ReferenceEquals(_playback, playback)) _playback = null;
                if (ReferenceEquals(_playbackDevice, playbackDevice)) _playbackDevice = null;
                _decoder = null;
                _playbackBuffer = null;
                _haveRxSequence = false;
                if (string.IsNullOrWhiteSpace(_lastError) ||
                    _lastError.StartsWith("Parada do áudio remoto incompleta", StringComparison.Ordinal))
                {
                    _lastError = "";
                    _statusText = "Áudio parado";
                }
            }
            RaiseAudioChanged();
        }

        public void StopAudioOnly()
        {
            StopHostCaptureOnly();
            StopClientPlaybackOnly();
        }

        private void SetError(string message, Exception? ex = null)
        {
            lock (_sync)
            {
                _lastError = ex is null
                    ? message
                    : $"{message} {ex.Message}";
                _statusText = _lastError;
            }
            RaiseAudioChanged();
        }

        private void RaiseAudioChanged() =>
            InvokeAudioChangedSafely();

        private void InvokeAudioChangedSafely()
        {
            EventHandler? handlers = AudioChanged;
            if (handlers is null)
                return;

            foreach (EventHandler handler in handlers.GetInvocationList())
            {
                try { handler(this, EventArgs.Empty); } catch { }
            }
        }

        private void RaiseAudioChangedDeferred() =>
            _ = Task.Run(InvokeAudioChangedSafely);

        // This property is kept private so tests and UI can reason about the
        // exact selected PID without exposing mutable state.
        private int? SelectedProcessId
        {
            get
            {
                lock (_sync)
                    return _selectedProcessId;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _session.SessionChanged -= Session_SessionChanged;
            StopAudioOnly();
            lock (_sync)
            {
                if (_capture is not null || _automaticMixer is not null || _sender is not null ||
                    _encodeWorker is not null || _receiver is not null)
                {
                    _session.SessionChanged += Session_SessionChanged;
                    return;
                }
            }
            _playbackGate.Dispose();
            _disposed = true;
        }
    }

}
