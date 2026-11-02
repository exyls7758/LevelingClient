using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;

namespace Multiverse.Services
{
    internal sealed class RemoteHostVideoTransport :
        IVideoTransport
    {
        private readonly NativeCaptureSession _capture;
        private readonly IPEndPoint _remote;
        private readonly RemoteSessionService _session;
        private readonly int _sessionGeneration;
        private readonly long _streamGeneration;

        private VideoUdpSender? _sender;
        private bool _disposed;

        public RemoteHostVideoTransport(
            NativeCaptureSession capture,
            IPEndPoint remote,
            RemoteSessionService session)
        {
            _capture =
                capture ??
                throw new ArgumentNullException(
                    nameof(capture));

            _remote =
                remote ??
                throw new ArgumentNullException(
                    nameof(remote));

            _session =
                session ??
                throw new ArgumentNullException(
                    nameof(session));
            var identity = session.GetSnapshot();
            _sessionGeneration = identity.SessionGeneration;
            _streamGeneration = identity.StreamConfig?.Generation ?? 0;
        }

        public bool Running =>
            _sender?.Running == true;

        internal IPEndPoint RemoteEndpoint => _remote;
        internal bool IsCurrentStream => _session.IsCurrentVideoStream(
            _streamGeneration, _sessionGeneration, RemoteSessionRole.Host);

        public event Action<ReassembledVideoFrame>? FrameReceived
        {
            add { }
            remove { }
        }

        public void Start()
        {
            if (_disposed)
                throw new ObjectDisposedException(
                    nameof(RemoteHostVideoTransport));

            if (Running)
                return;

            Stop();

            RemoteStreamConfig? config =
                _session.GetStreamConfig();

            if (config is null || config.WireStreamId == 0) throw new InvalidOperationException("No negotiated UDP stream identity");
            if (config.Generation != _streamGeneration ||
                !_session.IsCurrentVideoStream(_streamGeneration, _sessionGeneration, RemoteSessionRole.Host) ||
                !_remote.Equals(_session.GetRemoteVideoEndpoint()))
                throw new InvalidOperationException("UDP sender belongs to a retired session or stream");
            uint bitrateKbps = config.BitrateKbps;

            uint fps = config.Fps;

            var sender =
                new VideoUdpSender(
                    _capture,
                    _remote,
                    bitrateKbps,
                    fps,
                    () =>
                    {
                        RemoteSessionSnapshot snapshot =
                            _session.GetSnapshot();

                        return new VideoSenderNetworkFeedback
                        {
                            Connected =
                                snapshot.Connected,

                            RoundTripMs =
                                snapshot.RoundTripMs,

                            BaseRoundTripMs =
                                snapshot.BaseRoundTripMs,

                            JitterMs =
                                snapshot.JitterMs,

                            PacketLossPercent =
                                snapshot.RemotePacketLossPercent,

                            RemoteRxMbps =
                                snapshot.RemoteRxMbps,

                            FeedbackAgeMs =
                                snapshot.RemoteTelemetryAgeMs,

                            RemoteVideoFps =
                                snapshot.RemoteVideoFps,

                            RemotePendingFrames =
                                snapshot.RemotePendingFrames,

                            RemoteDecodeDrops =
                                snapshot.RemoteDecodeDrops
                        };
                    }, config.WireStreamId, _session.NextVideoPacketSequence);

            _sender = sender;
            try { sender.Start(); }
            catch
            {
                sender.Dispose();
                if (ReferenceEquals(_sender, sender)) _sender = null;
                throw;
            }
        }

        public void Stop()
        {
            _sender?.Stop();
            _sender?.Dispose();
            _sender = null;
        }

        public TransportSnapshot GetSnapshot()
        {
            VideoUdpSenderSnapshot tx =
                _sender?.GetSnapshot()
                ?? new VideoUdpSenderSnapshot();

            RemoteSessionSnapshot session =
                _session.GetSnapshot();

            return new TransportSnapshot
            {
                Running =
                    Running,

                Mode =
                    "REMOTE_HOST",

                RemoteEndpoint =
                    _remote.ToString(),

                SentFrames =
                    tx.SentFrames,

                SentPackets =
                    tx.SentPackets,

                SentBytes =
                    tx.SentBytes,
                LastPacketAgeMs = tx.PacketIntervals.LastAgeMs,
                LastFrameAgeMs = tx.FrameIntervals.LastAgeMs,
                LastPacketGapMs = tx.PacketIntervals.LastGapMs,
                MaxPacketGapMs = tx.PacketIntervals.MaxGapMs,
                LastFrameGapMs = tx.FrameIntervals.LastGapMs,
                MaxFrameGapMs = tx.FrameIntervals.MaxGapMs,
                FrameGapsOver100Ms = tx.FrameIntervals.GapsOver100Ms,
                PacketGapsOver100Ms = tx.PacketIntervals.GapsOver100Ms,

                RoundTripMs =
                    session.RoundTripMs,

                JitterMs =
                    session.JitterMs,

                EstimatedPacketLossPercent =
                    session.RemotePacketLossPercent,

                CurrentPacingMbps =
                    tx.CurrentPacingMbps,

                TargetPacingMbps =
                    tx.TargetPacingMbps,

                ActualSendMbps =
                    tx.ActualSendMbps,

                EncoderTargetMbps =
                    tx.EncoderTargetMbps,

                PacerBacklogMs =
                    tx.PacerBacklogMs,

                PacerLatenessMs =
                    tx.PacerLatenessMs,

                CongestionState =
                    tx.CongestionState,

                FramesDroppedBeforeSend =
                    tx.FramesDroppedBeforeSend,

                LastError =
                    tx.LastError
            };
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            // Keep Dispose retryable when a worker has not stopped yet.
            Stop();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    internal sealed class RemoteClientVideoTransport :
        IVideoTransport
    {
        private const long IdrRequestCooldownMs = 750;

        private readonly int _localPort;
        private readonly RemoteSessionService _session;

        private VideoUdpReceiver? _receiver;

        // Core 08.8:
        // There used to be a second managed dispatch queue here (3 frames)
        // before NativeNvdecPlayer's own compressed queue. Under a short
        // scheduler/GC stall, that extra queue could overflow, flush the GOP
        // and request an IDR even though NVDEC itself was still healthy.
        // VideoUdpReceiver already invokes FrameCompleted on its worker and
        // NativeNvdecPlayer.PushFrame is intentionally O(1): it only enqueues
        // the completed access unit and wakes the decode thread. Therefore the
        // redundant dispatch queue is removed.
        private volatile bool _awaitingKeyFrame;

        private long _lastIdrRequestTick;
        private int _idrRetryScheduled;
        private int _idrLifecycle;
        private int _retrySessionGeneration;
        private long _retryStreamGeneration;
        private long _decodeDispatchDrops;
        private string _dispatchError = "";
        private bool _disposed;

        public RemoteClientVideoTransport(
            int localPort,
            RemoteSessionService session)
        {
            _localPort =
                localPort;

            _session =
                session ??
                throw new ArgumentNullException(
                    nameof(session));
        }

        public bool Running =>
            _receiver?.Running == true;

        public event Action<ReassembledVideoFrame>? FrameReceived;

        public void Start()
        {
            if (_disposed)
                throw new ObjectDisposedException(
                    nameof(RemoteClientVideoTransport));

            if (Running)
                return;

            Stop();

            RemoteSessionSnapshot identity = _session.GetSnapshot();
            IPEndPoint? expectedRemote = _session.GetRemoteVideoEndpoint();
            if (identity.StreamConfig is null || identity.StreamConfig.WireStreamId == 0 || expectedRemote is null)
                throw new InvalidOperationException("No authenticated UDP stream identity");
            var streamConfig = identity.StreamConfig!;
            _retrySessionGeneration = identity.SessionGeneration;
            _retryStreamGeneration = identity.StreamConfig?.Generation ?? 0;
            Interlocked.Increment(ref _idrLifecycle);
            Interlocked.Exchange(ref _idrRetryScheduled, 0);
            Interlocked.Exchange(ref _lastIdrRequestTick, 0);
            _awaitingKeyFrame = true;
            _decodeDispatchDrops = 0;
            _dispatchError = "";

            _receiver =
                new VideoUdpReceiver(
                    IPAddress.Any,
                    _localPort,
                    measureOneWayLatency: false,
                    expectedRemoteAddress: expectedRemote.Address,
                    expectedStreamId: streamConfig.WireStreamId);

            _receiver.FrameCompleted +=
                OnFrameCompleted;

            _receiver.ReferenceLost +=
                OnReferenceLost;

            _receiver.Start();
        }

        private void OnReferenceLost()
        {
            // CORE 11.71: after an incomplete encoded frame, predictive H.264
            // references are no longer trustworthy. Gate the decode hand-off
            // until a fresh IDR arrives instead of showing corrupted P-frames.
            _awaitingKeyFrame = true;
            RequestIdrDebounced();
        }

        private void OnFrameCompleted(
            ReassembledVideoFrame frame)
        {
            if (!Running)
                return;

            if (_awaitingKeyFrame)
            {
                if (!frame.KeyFrame)
                    return;

                _awaitingKeyFrame = false;
            }

            try
            {
                // O(1) hand-off into NativeNvdecPlayer's single coded-order
                // queue. This removes one full managed queue + one thread from
                // the critical receive path.
                FrameReceived?
                    .Invoke(
                        frame);
                if (frame.KeyFrame)
                    VideoTransportDiagnostics.Record("CLIENT_KEYFRAME_DISPATCHED",
                        new { frame.FrameId, SessionGeneration = _retrySessionGeneration,
                            StreamGeneration = _retryStreamGeneration });
            }
            catch (Exception ex)
            {
                Interlocked.Increment(
                    ref _decodeDispatchDrops);

                _dispatchError =
                    $"Decode hand-off: {ex.Message}";

                RequestIdrDebounced();
            }
        }

        private bool IsCurrentRecovery(int lifecycle, long streamGeneration,
            int sessionGeneration) =>
            !_disposed && Running && streamGeneration > 0 &&
            Volatile.Read(ref _idrLifecycle) == lifecycle &&
            _retryStreamGeneration == streamGeneration &&
            _retrySessionGeneration == sessionGeneration &&
            _session.IsCurrentVideoStream(streamGeneration, sessionGeneration,
                RemoteSessionRole.Client);

        private void RequestIdrDebounced()
        {
            int lifecycle = Volatile.Read(ref _idrLifecycle);
            long stream = _retryStreamGeneration;
            int session = _retrySessionGeneration;
            if (!IsCurrentRecovery(lifecycle, stream, session)) return;
            long now = Environment.TickCount64;
            long previous = Interlocked.Read(ref _lastIdrRequestTick);
            if (previous != 0 && now - previous < IdrRequestCooldownMs)
            {
                ScheduleIdrRecoveryRetry(IdrRequestCooldownMs - Math.Max(0, now - previous),
                    lifecycle, stream, session);
                return;
            }
            Interlocked.Exchange(ref _lastIdrRequestTick, now);
            _ = RequestCurrentIdrAsync(lifecycle, stream, session);
        }

        private async Task RequestCurrentIdrAsync(int lifecycle, long streamGeneration,
            int sessionGeneration)
        {
            if (!IsCurrentRecovery(lifecycle, streamGeneration, sessionGeneration)) return;
            try
            {
                await _session.RequestRemoteIdrAsync(streamGeneration, sessionGeneration)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                VideoIncidentRecorder.Instance.RecordEvent("CLIENT_IDR_ERROR", new { ex.HResult });
            }
        }

        private void ScheduleIdrRecoveryRetry(long delayMs, int lifecycle,
            long streamGeneration, int sessionGeneration)
        {
            if (!IsCurrentRecovery(lifecycle, streamGeneration, sessionGeneration) ||
                Interlocked.CompareExchange(ref _idrRetryScheduled, lifecycle, 0) != 0)
                return;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay((int)Math.Clamp(delayMs, 1, IdrRequestCooldownMs))
                        .ConfigureAwait(false);
                    if (!IsCurrentRecovery(lifecycle, streamGeneration, sessionGeneration) ||
                        !_awaitingKeyFrame) return;
                    Interlocked.Exchange(ref _lastIdrRequestTick, Environment.TickCount64);
                    await RequestCurrentIdrAsync(lifecycle, streamGeneration, sessionGeneration)
                        .ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.CompareExchange(ref _idrRetryScheduled, 0, lifecycle);
                }
            });
        }

        public void Stop()
        {
            Interlocked.Increment(ref _idrLifecycle);
            Interlocked.Exchange(ref _idrRetryScheduled, 0);
            VideoUdpReceiver? receiver =
                _receiver;

            if (receiver is not null)
            {
                receiver.FrameCompleted -=
                    OnFrameCompleted;

                receiver.ReferenceLost -=
                    OnReferenceLost;

                receiver.Stop();
                receiver.Dispose();
                _receiver = null;
            }

            _awaitingKeyFrame = true;
        }

        public TransportSnapshot GetSnapshot()
        {
            VideoUdpReceiverSnapshot rx =
                _receiver?.GetSnapshot()
                ?? new VideoUdpReceiverSnapshot();

            RemoteSessionSnapshot session =
                _session.GetSnapshot();

            long totalObserved =
                Math.Max(
                    0,
                    rx.ReceivedPackets) +
                Math.Max(
                    0,
                    rx.SequenceGaps);

            double localLoss =
                totalObserved > 0
                    ? Math.Clamp(
                        rx.SequenceGaps *
                            100.0 /
                            totalObserved,
                        0.0,
                        100.0)
                    : 0.0;

            string error =
                !string.IsNullOrWhiteSpace(
                    _dispatchError)
                    ? _dispatchError
                    : rx.LastError;

            return new TransportSnapshot
            {
                Running =
                    Running,

                Mode =
                    "REMOTE_CLIENT",

                RemoteEndpoint =
                    session.RemoteAddress,

                ReceivedFrames =
                    rx.ReceivedFrames,

                ReceivedPackets =
                    rx.ReceivedPackets,

                ReceivedBytes =
                    rx.ReceivedBytes,
                LastPacketAgeMs = rx.PacketIntervals.LastAgeMs,
                LastFrameAgeMs = rx.FrameIntervals.LastAgeMs,
                LastPacketGapMs = rx.PacketIntervals.LastGapMs,
                MaxPacketGapMs = rx.PacketIntervals.MaxGapMs,
                LastFrameGapMs = rx.FrameIntervals.LastGapMs,
                MaxFrameGapMs = rx.FrameIntervals.MaxGapMs,
                FrameGapsOver100Ms = rx.FrameIntervals.GapsOver100Ms,
                PacketGapsOver100Ms = rx.PacketIntervals.GapsOver100Ms,

                KeyFrames =
                    rx.KeyFrames,
                AwaitingKeyFrame = _awaitingKeyFrame,

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

                DecodeDispatchDrops =
                    Interlocked.Read(
                        ref _decodeDispatchDrops),

                RoundTripMs =
                    session.RoundTripMs,

                JitterMs =
                    session.JitterMs,

                EstimatedPacketLossPercent =
                    localLoss,

                LastError =
                    error
            };
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            // Keep Dispose retryable when a worker has not stopped yet.
            Stop();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

}
