using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Multiverse.Services
{
    internal sealed class NativeNvdecSnapshot
    {
        public bool Running { get; init; }
        public bool FormatReady { get; init; }
        // True until an IDR has entered the coded-order queue. This is not
        // proof that the native decoder has presented or decoded that frame.
        public bool AwaitingKeyFrame { get; init; }

        public long NetworkFramesReceived { get; init; }
        public long NativeFramesSubmitted { get; init; }

        public long StaleFramesDropped { get; init; }
        public long WaitingForKeyFrameDrops { get; init; }

        public int QueueDepth { get; init; }
        public int MaxQueueDepth { get; init; }
        public long RecoveryIdrRequests { get; init; }

        public ulong DecodedPictures { get; init; }
        public ulong DisplayedFrames { get; init; }
        public ulong PreviewFrames { get; init; }

        public double LastDecodeLatencyMs { get; init; }
        public double AverageDecodeLatencyMs { get; init; }
        // Receive -> decode-queue dequeue, measured on the client's clock.
        // This is NOT network one-way or input-to-photon latency.
        public int LocalQueueAgeSamples { get; init; }
        public double LocalQueueAgeAverageMs { get; init; }
        public double LocalQueueAgeP95Ms { get; init; }
        public double LocalQueueAgeP99Ms { get; init; }
        public double LocalQueueAgeMaxMs { get; init; }

        public bool D3D11Attached { get; init; }
        public int D3D11LastStatus { get; init; }
        public ulong D3D11SubmittedFrames { get; init; }
        public ulong D3D11PresentedFrames { get; init; }
        public ulong D3D11DroppedPresents { get; init; }
        public ulong D3D11ReplacedPendingFrames { get; init; }
        public ulong D3D11PresentWaitTimeouts { get; init; }

        public ulong D3D11PresenterDequeuedFrames { get; init; }
        public ulong D3D11FrameLatencySignals { get; init; }
        public ulong D3D11FrameLatencyBypasses { get; init; }
        public ulong D3D11FrameLatencyWaitFailures { get; init; }
        public ulong D3D11PresentAttempts { get; init; }
        public ulong D3D11PresentStillDrawing { get; init; }
        public ulong D3D11PresentFailures { get; init; }
        public uint D3D11ForegroundProcessId { get; init; }
        public bool D3D11ForegroundIsCurrentProcess { get; init; }
        public uint D3D11PresenterStage { get; init; }
        public bool D3D11PresentInFlight { get; init; }

        public double D3D11GpuCopyMs { get; init; }
        public double D3D11PresentSubmitMs { get; init; }
        public double D3D11PresentWaitMs { get; init; }
        public double D3D11LastQueueToPresenterMs { get; init; }
        public double D3D11AverageQueueToPresenterMs { get; init; }
        public double D3D11LastStateLockWaitMs { get; init; }
        public double D3D11AverageStateLockWaitMs { get; init; }
        public double D3D11LastPresentCallMs { get; init; }
        public double D3D11AveragePresentCallMs { get; init; }
        public double D3D11LastFrameAgeAtPresentMs { get; init; }
        public double D3D11AverageFrameAgeAtPresentMs { get; init; }
        public double D3D11PresenterActivityAgeMs { get; init; }

        public string LastError { get; init; } = "";
    }

    /// <summary>
    /// Core 06 native NVDEC worker.
    ///
    /// Receiver thread NEVER calls the decoder directly.
    /// It feeds a tiny bounded coded-order FIFO.
    /// Decode runs on a dedicated AboveNormal thread.
    ///
    /// Remote gaming policy:
    /// compressed P-frames are never dropped independently. If the tiny FIFO
    /// overloads, the GOP is flushed and a fresh IDR is requested.
    /// </summary>
    internal sealed class NativeNvdecPlayer :
        IDisposable
    {
        // Coded H.264 must stay in order. A small bounded queue absorbs
        // scheduling jitter without becoming visible latency.
        private const int MaxCompressedQueue =
            4;

        private const long RecoveryIdrCooldownMs =
            750;

        // Remote gaming should not "catch up" old compressed video after a
        // scheduler/GC/network burst. Once a completed access unit has spent
        // this long waiting in the local decode queue, the GOP is already
        // outside the useful latency budget: flush it and recover from one IDR.
        private const double MaxDecodeQueueAgeMs =
            80.0;

        private readonly NativeNvdecSession _native;
        private readonly Action? _requestIdr;
        private readonly bool _headlessOutput;

        private readonly AutoResetEvent _wake =
            new(false);

        private readonly CodedFrameQueue<ReassembledVideoFrame>
            _queue = new(MaxCompressedQueue);
        private readonly object _queueSync = new();

        private readonly object _lifecycle =
            new();

        private Thread? _thread;

        private volatile bool _stop;
        private volatile bool _running;
        private volatile bool _disposed;
        private volatile bool _seenKeyFrame;

        private long _networkFramesReceived;
        private long _nativeFramesSubmitted;

        private readonly VideoLatencyWindow _localQueueAge = new();

        private long _staleFramesDropped;
        private long _waitingForKeyFrameDrops;


        private long _recoveryIdrRequests;
        private long _lastRecoveryIdrAt;

        // Recovery can be requested by the UDP receive thread. The expensive
        // native parser/decoder discontinuity call is executed only by the
        // dedicated decode thread, so RX never waits on NVDEC/CUDA.

        private string _lastError = "";

        public NativeNvdecPlayer(
            uint width,
            uint height,
            Action? requestIdr = null,
            bool headlessOutput = false)
        {
            _requestIdr =
                requestIdr;

            _headlessOutput =
                headlessOutput;

            NvdecOpenResult open =
                NativeCoreService.OpenNvdec(
                    width,
                    height,
                    previewMaxWidth: 480);

            if (!open.Success ||
                open.Session is null)
            {
                var failure = new InvalidOperationException(
                    open.Error.Length > 0
                        ? open.Error
                        : $"Falha no decoder de vídeo ({open.Status}).");
                failure.Data["NativeStatus"] = open.Status;
                throw failure;
            }

            _native =
                open.Session;

            int headlessResult =
                _native.SetHeadlessOutput(
                    _headlessOutput);

            if (headlessResult < 0)
            {
                _native.Dispose();

                throw new InvalidOperationException(
                    $"Falha ao configurar saída NVDEC headless: 0x{headlessResult:X8}");
            }
        }

        public bool HeadlessOutput =>
            _headlessOutput;

        public uint PreviewWidth =>
            _native.PreviewWidth;

        public uint PreviewHeight =>
            _native.PreviewHeight;

        public byte[] PreviewBuffer =>
            _native.PreviewBuffer;

        public bool Running =>
            _running;

        public int AttachRenderer(
            IntPtr panelUnknown,
            uint width,
            uint height) =>
            _native.AttachSwapChainPanel(
                panelUnknown,
                width,
                height);

        public int ResizeRenderer(
            uint width,
            uint height) =>
            _native.ResizeSwapChain(
                width,
                height);

        public int SetPresentationScale(float scaleX, float scaleY) =>
            _native.SetCompositionScale(scaleX, scaleY);

        public void DetachRenderer() =>
            _native.DetachSwapChainPanel();

        public void Start()
        {
            lock (_lifecycle)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(NativeNvdecPlayer));
                if (_thread is not null)
                {
                    if (_running && !_stop) return;
                    throw new InvalidOperationException(
                        "O decoder anterior ainda não teve sua parada confirmada.");
                }

                _stop = false;
                _lastError = "";
                _localQueueAge.Reset();

                _thread =
                    new Thread(DecodeLoop)
                    {
                        IsBackground = true,
                        Name = "Multiverse NVDEC",
                        Priority =
                            ThreadPriority.AboveNormal
                    };

                _running = true;
                _thread.Start();

                // IMPORTANT: the transport is not necessarily listening yet.
                // DevicesPage requests the initial IDR only after RX/TX and
                // FrameReceived are active.
            }
        }

        public void PushFrame(ReassembledVideoFrame frame)
        {
            if (_disposed || frame.Data.Length == 0) return;
            bool requestIdr = false;
            lock (_queueSync)
            {
                if (!_running || _stop) return;
                Interlocked.Increment(ref _networkFramesReceived);
                // A dependent frame may enter only after a clean keyframe.
                if (_queue.Count >= MaxCompressedQueue)
                {
                    RecoverFromBacklogLocked();
                    requestIdr = true;
                }
                if (!_seenKeyFrame && !frame.KeyFrame)
                {
                    Interlocked.Increment(ref _waitingForKeyFrameDrops);
                    // An initial IDR request can race encoder startup or be lost.
                    // Keep requesting through the existing cooldown while deltas
                    // arrive; otherwise a low-motion source can wait indefinitely.
                    requestIdr = true;
                }
                else
                {
                    if (frame.KeyFrame) _seenKeyFrame = true;
                    if (!_queue.TryEnqueue(frame))
                        throw new InvalidOperationException("A fila comprimida excedeu sua capacidade.");
                    _wake.Set();
                }
            }
            if (requestIdr) RequestRecoveryIdr();
        }

        private void RecoverFromBacklogLocked()
        {
            int dropped = _queue.ClearForDiscontinuity();
            if (dropped > 0)
                Interlocked.Add(ref _staleFramesDropped, dropped);
            _seenKeyFrame = false;
        }

        private void RecoverFromBacklog()
        {
            lock (_queueSync) RecoverFromBacklogLocked();
            // No external callback or native call is made under the queue lock.
            RequestRecoveryIdr();
        }

        private void RequestRecoveryIdr()
        {
            long now =
                Environment.TickCount64;

            long previous =
                Interlocked.Read(
                    ref _lastRecoveryIdrAt);

            if (previous != 0 &&
                now - previous <
                    RecoveryIdrCooldownMs)
            {
                return;
            }

            Interlocked.Exchange(
                ref _lastRecoveryIdrAt,
                now);

            Interlocked.Increment(
                ref _recoveryIdrRequests);

            try
            {
                _requestIdr?.Invoke();
            }
            catch (Exception ex)
            {
                // Recovery signaling is advisory. A temporary control-channel
                // failure must never terminate the NVDEC worker that is still
                // capable of decoding the next clean keyframe.
                _lastError = $"Falha ao solicitar keyframe de recuperação: {ex.Message}";
                StartupDiagnostics.RecordException("NVDEC_IDR_CALLBACK_ERROR", ex);
                VideoIncidentRecorder.Instance.RecordEvent(
                    "NVDEC_IDR_CALLBACK_ERROR",
                    new { ex.HResult });
            }
        }

        private void ApplyNativeDiscontinuity(bool pending)
        {
            if (!pending)
            {
                return;
            }

            try
            {
                _native.SignalDiscontinuity();
            }
            catch (Exception ex)
            {
                _lastError =
                    $"NVDEC discontinuity: {ex.Message}";
            }
        }

        private bool TryDequeueFrame([MaybeNullWhen(false)] out ReassembledVideoFrame frame, out bool discontinuity)
        {
            lock (_queueSync) return _queue.TryDequeue(out frame, out discontinuity);
        }

        private void DecodeLoop()
        {
            try
            {
                while (!_stop)
                {
                    _wake.WaitOne(5);

                    while (TryDequeueFrame(out ReassembledVideoFrame? frame, out bool discontinuity) && frame is not null)
                    {
                        ApplyNativeDiscontinuity(discontinuity);

                        _localQueueAge.ObserveTimestamp(
                            frame.ReceiveTimestamp, Stopwatch.GetTimestamp(), Stopwatch.Frequency);

                        if (
                            frame.ReceiveTimestamp > 0 &&
                            Stopwatch.GetElapsedTime(
                                frame.ReceiveTimestamp)
                                .TotalMilliseconds >
                                MaxDecodeQueueAgeMs)
                        {
                            Interlocked.Increment(
                                ref _staleFramesDropped);

                            // Never decode a delayed P-frame and then try to
                            // replay the old GOP. Clear whatever followed it,
                            // invalidate parser/decoder references and request
                            // one fresh IDR. This bounds local catch-up latency.
                            RecoverFromBacklog();
                            break;
                        }

                        var packet =
                            new EncodedPacket
                            {
                                Data = frame.Data,
                                FrameId = frame.FrameId,
                                KeyFrame = frame.KeyFrame
                            };

                        int result =
                            _native.Push(
                                packet);

                        if (result < 0)
                        {
                            _lastError =
                                _native.GetMessage();

                            RecoverFromBacklog();
                            break;
                        }

                        Interlocked.Increment(
                            ref _nativeFramesSubmitted);
                    }
                }
            }
            catch (Exception ex)
            {
                _lastError =
                    ex.Message;
            }
            finally
            {
                _running = false;
            }
        }

        public NvdecPreviewResult ReadLatestPreview() =>
            _native.ReadLatestPreview();

        public NativeNvdecSnapshot GetSnapshot()
        {
            NvdecStats? native =
                _native.GetStats();

            D3D11RendererStats? renderer =
                _native.GetRendererStats();
            VideoLatencySnapshot localQueueAge = _localQueueAge.Snapshot();

            return new NativeNvdecSnapshot
            {
                Running = _running,
                AwaitingKeyFrame = !_seenKeyFrame,

                FormatReady =
                    native?.FormatReady == true,

                NetworkFramesReceived =
                    Interlocked.Read(
                        ref _networkFramesReceived),

                NativeFramesSubmitted =
                    Interlocked.Read(
                        ref _nativeFramesSubmitted),

                StaleFramesDropped =
                    Interlocked.Read(
                        ref _staleFramesDropped),

                WaitingForKeyFrameDrops =
                    Interlocked.Read(
                        ref _waitingForKeyFrameDrops),

                QueueDepth = _queue.Count,
                MaxQueueDepth = _queue.MaxObserved,

                RecoveryIdrRequests =
                    Interlocked.Read(
                        ref _recoveryIdrRequests),

                DecodedPictures =
                    native?.DecodedPictures ?? 0,

                DisplayedFrames =
                    native?.DisplayedFrames ?? 0,

                PreviewFrames =
                    native?.PreviewFrames ?? 0,

                LastDecodeLatencyMs =
                    native?.LastDecodeLatencyMs ?? 0,

                AverageDecodeLatencyMs =
                    native?.AverageDecodeLatencyMs ?? 0,

                LocalQueueAgeSamples = localQueueAge.Samples,
                LocalQueueAgeAverageMs = localQueueAge.AverageMs,
                LocalQueueAgeP95Ms = localQueueAge.P95Ms,
                LocalQueueAgeP99Ms = localQueueAge.P99Ms,
                LocalQueueAgeMaxMs = localQueueAge.MaxMs,

                D3D11Attached =
                    renderer?.Attached == true,
                D3D11LastStatus = renderer?.LastStatus ?? 0,

                D3D11SubmittedFrames =
                    renderer?.SubmittedFrames ?? 0,

                D3D11PresentedFrames =
                    renderer?.PresentedFrames ?? 0,

                D3D11DroppedPresents =
                    renderer?.DroppedPresents ?? 0,

                D3D11ReplacedPendingFrames =
                    renderer?.ReplacedPendingFrames ?? 0,

                D3D11PresentWaitTimeouts =
                    renderer?.PresentWaitTimeouts ?? 0,

                D3D11PresenterDequeuedFrames =
                    renderer?.PresenterDequeuedFrames ?? 0,

                D3D11FrameLatencySignals =
                    renderer?.FrameLatencySignals ?? 0,

                D3D11FrameLatencyBypasses =
                    renderer?.FrameLatencyBypasses ?? 0,

                D3D11FrameLatencyWaitFailures =
                    renderer?.FrameLatencyWaitFailures ?? 0,

                D3D11PresentAttempts =
                    renderer?.PresentAttempts ?? 0,

                D3D11PresentStillDrawing =
                    renderer?.PresentStillDrawing ?? 0,

                D3D11PresentFailures =
                    renderer?.PresentFailures ?? 0,

                D3D11ForegroundProcessId =
                    renderer?.ForegroundProcessId ?? 0,

                D3D11ForegroundIsCurrentProcess =
                    renderer?.ForegroundIsCurrentProcess ?? false,

                D3D11PresenterStage =
                    renderer?.PresenterStage ?? 0,

                D3D11PresentInFlight =
                    renderer?.PresentInFlight ?? false,

                D3D11GpuCopyMs =
                    renderer?.AverageGpuCopyMs ?? 0,

                D3D11PresentSubmitMs =
                    renderer?.AveragePresentSubmitMs ?? 0,

                D3D11PresentWaitMs =
                    renderer?.AveragePresentWaitMs ?? 0,

                D3D11LastQueueToPresenterMs =
                    renderer?.LastQueueToPresenterMs ?? 0,

                D3D11AverageQueueToPresenterMs =
                    renderer?.AverageQueueToPresenterMs ?? 0,

                D3D11LastStateLockWaitMs =
                    renderer?.LastStateLockWaitMs ?? 0,

                D3D11AverageStateLockWaitMs =
                    renderer?.AverageStateLockWaitMs ?? 0,

                D3D11LastPresentCallMs =
                    renderer?.LastPresentCallMs ?? 0,

                D3D11AveragePresentCallMs =
                    renderer?.AveragePresentCallMs ?? 0,

                D3D11LastFrameAgeAtPresentMs =
                    renderer?.LastFrameAgeAtPresentMs ?? 0,

                D3D11AverageFrameAgeAtPresentMs =
                    renderer?.AverageFrameAgeAtPresentMs ?? 0,

                D3D11PresenterActivityAgeMs =
                    renderer?.PresenterActivityAgeMs ?? 0,

                LastError =
                    !string.IsNullOrWhiteSpace(
                        _lastError)
                        ? _lastError
                        : _native.GetMessage()
            };
        }

        public void Stop()
        {
            Thread? thread;
            lock (_lifecycle)
            {
                if (_disposed) return;
                _stop = true;
                _wake.Set();
                thread = _thread;
            }

            // Never release the native decoder or its wait event while a
            // worker can still return from a native Push or use that event.
            ConfirmedThread.JoinOrThrow(thread, 1000, "NVDEC");

            lock (_lifecycle)
            {
                if (!ReferenceEquals(_thread, thread))
                    throw new InvalidOperationException("A geração do decoder mudou durante a parada.");
                _thread = null;
                _running = false;
            }
            lock (_queueSync)
            {
                _queue.Clear();
                _seenKeyFrame = false;
                _queue.ResetMax();
            }
            _localQueueAge.Reset();
        }

        public void Dispose()
        {
            lock (_lifecycle)
            {
                if (_disposed) return;
                Stop();
                _native.Dispose();
                _wake.Dispose();
                _disposed = true;
                GC.SuppressFinalize(this);
            }
        }
    }
}
