using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Multiverse.Services
{
    /// <summary>
    /// Automatic render-audio collector. It never opens a microphone/capture endpoint;
    /// each source is one PROCESS_LOOPBACK tree and the resulting PCM frames are mixed
    /// before entering the existing Opus sender queue.
    /// </summary>
    internal sealed class AutomaticProcessAudioMixer : IDisposable
    {
        private const int SampleRate = 48000;
        private const int Channels = 2;
        private const int BytesPerSample = 2;
        private const int FrameSamplesPerChannel = 960;
        private const int FrameSamples = FrameSamplesPerChannel * Channels;
        private const int FrameBytes = FrameSamples * BytesPerSample;
        private const int MaxSources = 8;
        private const int SourceBufferBytes = FrameBytes * 6;

        private sealed class SourceState : IDisposable
        {
            private readonly object _sync = new();
            private readonly byte[] _ring = new byte[SourceBufferBytes];
            private int _read;
            private int _write;
            private int _count;

            internal SourceState(AudioApplicationDescriptor descriptor)
            {
                Descriptor = descriptor;
                Capture = new ProcessLoopbackAudioCapture(descriptor.RootProcessId);
                Capture.PcmAvailable += Capture_PcmAvailable;
            }

            internal AudioApplicationDescriptor Descriptor { get; }
            internal ProcessLoopbackAudioCapture Capture { get; }

            internal event Action<SourceState, Exception>? Failed;

            internal async Task StartAsync()
            {
                Capture.CaptureFailed += Capture_CaptureFailed;
                await Capture.StartAsync().ConfigureAwait(false);
            }

            private void Capture_CaptureFailed(Exception ex) => Failed?.Invoke(this, ex);

            private void Capture_PcmAvailable(ReadOnlyMemory<byte> pcm)
            {
                ReadOnlySpan<byte> span = pcm.Span;
                lock (_sync)
                {
                    // Keep a bounded latest-audio window. Overflow drops oldest bytes,
                    // never blocks the capture thread and therefore never video workers.
                    for (int i = 0; i < span.Length; i++)
                    {
                        if (_count == _ring.Length)
                        {
                            _read = (_read + 1) % _ring.Length;
                            _count--;
                        }
                        _ring[_write] = span[i];
                        _write = (_write + 1) % _ring.Length;
                        _count++;
                    }
                }
            }

            internal bool TryReadFrame(Span<byte> destination)
            {
                if (destination.Length < FrameBytes)
                    throw new ArgumentException("Frame buffer too small.", nameof(destination));

                lock (_sync)
                {
                    if (_count < FrameBytes)
                        return false;

                    for (int i = 0; i < FrameBytes; i++)
                    {
                        destination[i] = _ring[_read];
                        _read = (_read + 1) % _ring.Length;
                    }
                    _count -= FrameBytes;
                    return true;
                }
            }

            public void Dispose()
            {
                try { Capture.PcmAvailable -= Capture_PcmAvailable; } catch { }
                try { Capture.CaptureFailed -= Capture_CaptureFailed; } catch { }
                try { Capture.StopAudioOnly(); } catch { }
                try { Capture.Dispose(); } catch { }
            }
        }

        private readonly object _sync = new();
        private readonly AudioApplicationDiscoveryService _discovery;
        private readonly MicrophoneUsageDetector _microphoneUsageDetector = new();
        private readonly Func<IReadOnlyCollection<string>> _exclusionsProvider;
        private readonly Dictionary<int, SourceState> _sources = new();
        private CancellationTokenSource? _cancel;
        private Task? _reconcileTask;
        private Task? _mixTask;
        private bool _voicePrivacyVerified;
        private bool _voicePrivacyFailClosed;
        private int _voicePrivacyBlockedSources;
        private string _voicePrivacyStatus = "PRIVACIDADE DE VOZ • verificando...";
        private string _voicePrivacyError = "";
        private bool _disposed;

        internal AutomaticProcessAudioMixer(
            AudioApplicationDiscoveryService discovery,
            Func<IReadOnlyCollection<string>> exclusionsProvider)
        {
            _discovery = discovery;
            _exclusionsProvider = exclusionsProvider;
        }

        internal event Action<ReadOnlyMemory<byte>>? PcmAvailable;
        internal event Action<int, Exception>? SourceFailed;
        internal event EventHandler? SourcesChanged;

        internal bool Running => _cancel is { IsCancellationRequested: false };

        internal int ActiveSourceCount
        {
            get { lock (_sync) return _sources.Count; }
        }

        internal bool VoicePrivacyVerified
        {
            get { lock (_sync) return _voicePrivacyVerified; }
        }

        internal bool VoicePrivacyFailClosed
        {
            get { lock (_sync) return _voicePrivacyFailClosed; }
        }

        internal int VoicePrivacyBlockedSources
        {
            get { lock (_sync) return _voicePrivacyBlockedSources; }
        }

        internal string VoicePrivacyStatus
        {
            get { lock (_sync) return _voicePrivacyStatus; }
        }

        internal string VoicePrivacyError
        {
            get { lock (_sync) return _voicePrivacyError; }
        }

        internal async Task StartAsync()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(AutomaticProcessAudioMixer));
            if (Running)
                return;

            CancellationTokenSource cts = new();
            lock (_sync)
                _cancel = cts;

            await ReconcileSourcesAsync(cts.Token).ConfigureAwait(false);
            _reconcileTask = Task.Run(() => ReconcileLoopAsync(cts.Token));
            _mixTask = Task.Run(() => MixLoopAsync(cts.Token));
        }

        private async Task ReconcileLoopAsync(CancellationToken token)
        {
            using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(500));
            try
            {
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                    await ReconcileSourcesAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }

        private async Task ReconcileSourcesAsync(CancellationToken token)
        {
            IReadOnlyCollection<string> exclusions = _exclusionsProvider();
            MicrophoneUsageSnapshot microphone = _microphoneUsageDetector.Scan();

            AudioApplicationDescriptor[] discovered = _discovery
                .EnumerateActiveApplications()
                .Where(x => x.RootProcessId != Environment.ProcessId)
                .ToArray();

            AudioApplicationDescriptor[] allowedByCommunicationPolicy = discovered
                .Where(x => !IsCommunicationExcluded(x, exclusions))
                .ToArray();

            int communicationBlocked = discovered.Length - allowedByCommunicationPolicy.Length;
            int microphoneBlocked = 0;
            AudioApplicationDescriptor[] eligible;

            if (!microphone.Verified)
            {
                eligible = Array.Empty<AudioApplicationDescriptor>();
            }
            else
            {
                AudioApplicationDescriptor[] notUsingMicrophone = allowedByCommunicationPolicy
                    .Where(x => !IsUsingMicrophone(x, microphone.ProcessIds))
                    .ToArray();
                microphoneBlocked = allowedByCommunicationPolicy.Length - notUsingMicrophone.Length;
                eligible = notUsingMicrophone.Take(MaxSources).ToArray();
            }

            bool privacyChanged;
            lock (_sync)
            {
                bool verified = microphone.Verified;
                bool failClosed = !verified;
                int blocked = communicationBlocked + microphoneBlocked;
                string status = failClosed
                    ? "ÁUDIO PAUSADO • não foi possível verificar o uso do microfone"
                    : blocked > 0
                        ? $"PRIVACIDADE DE VOZ • PROTEGIDA • {blocked} fonte(s) bloqueada(s)"
                        : "PRIVACIDADE DE VOZ • PROTEGIDA";
                string privacyError = failClosed
                    ? (string.IsNullOrWhiteSpace(microphone.Error)
                        ? "O Windows não confirmou as sessões de entrada."
                        : microphone.Error)
                    : "";

                privacyChanged =
                    _voicePrivacyVerified != verified ||
                    _voicePrivacyFailClosed != failClosed ||
                    _voicePrivacyBlockedSources != blocked ||
                    !string.Equals(_voicePrivacyStatus, status, StringComparison.Ordinal) ||
                    !string.Equals(_voicePrivacyError, privacyError, StringComparison.Ordinal);

                _voicePrivacyVerified = verified;
                _voicePrivacyFailClosed = failClosed;
                _voicePrivacyBlockedSources = blocked;
                _voicePrivacyStatus = status;
                _voicePrivacyError = privacyError;
            }

            int[] wanted = eligible.Select(x => x.RootProcessId).ToArray();
            SourceState[] remove;
            lock (_sync)
            {
                remove = _sources
                    .Where(pair => !wanted.Contains(pair.Key))
                    .Select(pair => pair.Value)
                    .ToArray();
                foreach (SourceState source in remove)
                    _sources.Remove(source.Descriptor.RootProcessId);
            }
            foreach (SourceState source in remove)
                source.Dispose();

            bool sourceChanged = remove.Length > 0;
            foreach (AudioApplicationDescriptor descriptor in eligible)
            {
                token.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    if (_sources.ContainsKey(descriptor.RootProcessId))
                        continue;
                }

                SourceState state = new(descriptor);
                state.Failed += Source_Failed;
                try
                {
                    await state.StartAsync().ConfigureAwait(false);
                    lock (_sync)
                    {
                        if (_cancel?.IsCancellationRequested == true ||
                            _sources.ContainsKey(descriptor.RootProcessId))
                        {
                            state.Dispose();
                        }
                        else
                        {
                            _sources[descriptor.RootProcessId] = state;
                            sourceChanged = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    state.Dispose();
                    try { SourceFailed?.Invoke(descriptor.RootProcessId, ex); } catch { }
                }
            }

            if (sourceChanged || privacyChanged)
            {
                try { SourcesChanged?.Invoke(this, EventArgs.Empty); } catch { }
            }
        }

        private static bool IsCommunicationExcluded(
            AudioApplicationDescriptor descriptor,
            IReadOnlyCollection<string> exclusions)
        {
            if (AudioApplicationPolicy.IsExcluded(descriptor.ProcessName, exclusions))
                return true;

            foreach (string processName in descriptor.SessionProcessNames)
            {
                if (AudioApplicationPolicy.IsExcluded(processName, exclusions))
                    return true;
            }

            return false;
        }

        private static bool IsUsingMicrophone(
            AudioApplicationDescriptor descriptor,
            IReadOnlySet<int> activeMicrophoneProcessIds)
        {
            if (activeMicrophoneProcessIds.Count == 0)
                return false;

            if (activeMicrophoneProcessIds.Contains(descriptor.RootProcessId))
                return true;

            foreach (int pid in descriptor.SessionProcessIds)
            {
                if (activeMicrophoneProcessIds.Contains(pid))
                    return true;
            }

            return false;
        }

        private void Source_Failed(SourceState state, Exception ex)
        {
            bool removed;
            lock (_sync)
                removed = _sources.Remove(state.Descriptor.RootProcessId);
            if (removed)
            {
                // CaptureFailed can originate on the capture thread itself. Dispose
                // off-thread so COM teardown never races that callback/finally block.
                _ = Task.Run(state.Dispose);
            }
            try { SourceFailed?.Invoke(state.Descriptor.RootProcessId, ex); } catch { }
            try { SourcesChanged?.Invoke(this, EventArgs.Empty); } catch { }
        }

        private async Task MixLoopAsync(CancellationToken token)
        {
            using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(20));
            byte[] frameBytes = ArrayPool<byte>.Shared.Rent(FrameBytes);
            short[] mixed = ArrayPool<short>.Shared.Rent(FrameSamples);
            var sourceSamples = new List<short[]>(MaxSources);
            var rentedSourceSamples = new List<short[]>(MaxSources);

            try
            {
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    SourceState[] sources;
                    lock (_sync)
                        sources = _sources.Values.ToArray();
                    if (sources.Length == 0)
                        continue;

                    sourceSamples.Clear();
                    rentedSourceSamples.Clear();
                    foreach (SourceState source in sources)
                    {
                        if (!source.TryReadFrame(frameBytes.AsSpan(0, FrameBytes)))
                            continue;

                        short[] samples = ArrayPool<short>.Shared.Rent(FrameSamples);
                        Buffer.BlockCopy(frameBytes, 0, samples, 0, FrameBytes);
                        sourceSamples.Add(samples);
                        rentedSourceSamples.Add(samples);
                    }

                    if (sourceSamples.Count == 0)
                        continue;

                    Pcm16Mixer.Mix(sourceSamples, mixed.AsSpan(0, FrameSamples));
                    byte[] output = new byte[FrameBytes];
                    Buffer.BlockCopy(mixed, 0, output, 0, FrameBytes);
                    try { PcmAvailable?.Invoke(output); } catch { }

                    foreach (short[] rented in rentedSourceSamples)
                        ArrayPool<short>.Shared.Return(rented, clearArray: false);
                    rentedSourceSamples.Clear();
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                foreach (short[] rented in rentedSourceSamples)
                    ArrayPool<short>.Shared.Return(rented, clearArray: false);
                ArrayPool<byte>.Shared.Return(frameBytes, clearArray: false);
                ArrayPool<short>.Shared.Return(mixed, clearArray: false);
            }
        }

        internal void Stop()
        {
            CancellationTokenSource? cts;
            Task? reconcile;
            Task? mix;
            SourceState[] sources;
            lock (_sync)
            {
                cts = _cancel;
                reconcile = _reconcileTask;
                mix = _mixTask;
                _cancel = null;
                _reconcileTask = null;
                _mixTask = null;
                sources = _sources.Values.ToArray();
                _sources.Clear();
            }

            try { cts?.Cancel(); } catch { }
            foreach (SourceState source in sources)
                source.Dispose();
            try { Task.WaitAll(new[] { reconcile, mix }.Where(x => x is not null).Cast<Task>().ToArray(), 300); } catch { }
            try { cts?.Dispose(); } catch { }
            try { SourcesChanged?.Invoke(this, EventArgs.Empty); } catch { }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Stop();
        }
    }
}
