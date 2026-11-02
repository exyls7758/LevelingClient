using System;

namespace Multiverse.Services
{
    // Pure, deterministic policy. It never opens sockets or calls NVDEC.
    // All times are monotonic milliseconds supplied by the caller.
    internal sealed class VideoProgressSample
    {
        public long NowMs { get; init; }
        public int SessionGeneration { get; init; }
        public long StreamGeneration { get; init; }
        public long TransportId { get; init; }
        public bool Connected { get; init; }
        public bool Host { get; init; }
        public bool Running { get; init; }
        public bool DecoderRunning { get; init; }
        public bool PresenterAttached { get; init; }
        public uint ExpectedFps { get; init; }
        public long Packets { get; init; }
        public long Frames { get; init; }
        public long Decoded { get; init; }
        public long Presented { get; init; }
        public long Captured { get; init; }
        public long Encoded { get; init; }
        public long Gaps { get; init; }
        public long Incomplete { get; init; }
        public long WaitingForKeyFrame { get; init; }
        public bool WaitingForReference { get; init; }
        public long QueueResets { get; init; }
    }

    internal sealed class VideoProgressDecision
    {
        public string Reason { get; init; } = "";
        public long PacketAgeMs { get; init; }
        public long FrameAgeMs { get; init; }
        public long DecodeAgeMs { get; init; }
        public long PresentAgeMs { get; init; }
        public long CaptureAgeMs { get; init; }
        public long EncodeAgeMs { get; init; }
        // Only the stalled stage has a meaningful clock. A client's encoder
        // never advances and must not inflate a receive incident to session age.
        public long IncidentDurationMs => Reason switch
        {
            "HOST_CAPTURE_GAP" or "HOST_CAPTURE_STALL" => CaptureAgeMs,
            "HOST_ENCODE_GAP" or "HOST_ENCODE_STALL" => EncodeAgeMs,
            "HOST_TX_GAP" or "CLIENT_UDP_SILENCE" or "CLIENT_RX_GAP" => PacketAgeMs,
            "CLIENT_REASSEMBLY_STALL" or "CLIENT_FRAME_GAP" => FrameAgeMs,
            "CLIENT_REFERENCE_WAIT" or "CLIENT_DECODE_STALL" => DecodeAgeMs,
            "CLIENT_PRESENT_GAP" or "CLIENT_PRESENTER_STALL" => PresentAgeMs,
            _ => 0
        };
        public bool RequestIdr { get; init; }
        public bool RecoverReceiver { get; init; }
    }

    internal sealed class VideoProgressMonitor
    {
        private sealed class CounterClock
        {
            internal long Count = -1;
            internal long At;
            internal long Observe(long count, long now)
            {
                if (Count < 0 || count != Count)
                {
                    Count = count;
                    At = now;
                }
                return Math.Max(0, now - At);
            }
        }

        private readonly CounterClock _packets = new();
        private readonly CounterClock _frames = new();
        private readonly CounterClock _decoded = new();
        private readonly CounterClock _presented = new();
        private readonly CounterClock _captured = new();
        private readonly CounterClock _encoded = new();
        private int _sessionGeneration = -1;
        private long _streamGeneration = -1;
        private long _transportId = -1;
        private bool _host;
        private long _lastNow;
        private long _lastIdrAt = -100000;
        private bool _wasWaitingForReference;
        private long _lastEventAt = -100000;
        private string _lastReason = "";
        private bool _initialized;

        public void Reset()
        {
            _initialized = false;
            _packets.Count = _frames.Count = _decoded.Count = -1;
            _presented.Count = _captured.Count = _encoded.Count = -1;
            _wasWaitingForReference = false;
        }

        public VideoProgressDecision Observe(VideoProgressSample s)
        {
            long now = s.NowMs;
            bool reset = !_initialized || now < _lastNow ||
                s.SessionGeneration != _sessionGeneration ||
                s.StreamGeneration != _streamGeneration ||
                s.TransportId != _transportId || s.Host != _host ||
                s.Packets < _packets.Count || s.Frames < _frames.Count ||
                // Only counters produced by this role can invalidate its
                // progress history. Client capture/encode and host decode/
                // present counters are optional and may legitimately be zero.
                (s.Host
                    ? (s.Captured < _captured.Count || s.Encoded < _encoded.Count)
                    : (s.Decoded < _decoded.Count || s.Presented < _presented.Count));
            if (reset)
            {
                Reset();
                _initialized = true;
                _sessionGeneration = s.SessionGeneration;
                _streamGeneration = s.StreamGeneration;
                _transportId = s.TransportId;
                _host = s.Host;
                _lastIdrAt = -100000;
                _lastEventAt = -100000;
                _lastReason = "";
            }
            _lastNow = now;
            long packetAge = _packets.Observe(s.Packets, now);
            long frameAge = _frames.Observe(s.Frames, now);
            long decodeAge = _decoded.Observe(s.Decoded, now);
            long presentAge = _presented.Observe(s.Presented, now);
            long captureAge = _captured.Observe(s.Captured, now);
            long encodeAge = _encoded.Observe(s.Encoded, now);
            string reason = "";
            bool idr = false, rebind = false;
            // The first explicit reference-wait notification is actionable even
            // when decode/receive counters have advanced recently. A stream
            // generation change or Reset() starts a new, independent episode.
            bool referenceWait = s.Connected && s.Running && !s.Host &&
                s.DecoderRunning && s.WaitingForReference;
            bool referenceWaitStarted = referenceWait && !_wasWaitingForReference;
            _wasWaitingForReference = referenceWait;
            if (!reset && s.Connected && s.Running)
            {
                long microMs = Math.Max(100, 6000 / Math.Max(1, (int)s.ExpectedFps));
                if (s.Host)
                {
                    if (encodeAge >= microMs && encodeAge < 1200)
                        reason = captureAge >= microMs ? "HOST_CAPTURE_GAP" : "HOST_ENCODE_GAP";
                    else if (encodeAge >= 1200)
                        reason = captureAge >= 1200 ? "HOST_CAPTURE_STALL" : "HOST_ENCODE_STALL";
                    else if (packetAge >= microMs && s.Encoded > 0)
                        reason = "HOST_TX_GAP";
                }
                else
                {
                    // A packet is not a decoded frame. Keep these clocks separate.
                    if (packetAge >= 3500)
                    {
                        reason = "CLIENT_UDP_SILENCE";
                        rebind = true; // The runtime owns retry/backoff and revalidation.
                    }
                    else if (referenceWait && (referenceWaitStarted || decodeAge >= 1200))
                        reason = "CLIENT_REFERENCE_WAIT";
                    else if (frameAge >= 1200)
                        reason = "CLIENT_REASSEMBLY_STALL";
                    else if (s.DecoderRunning && decodeAge >= 1200 && s.Frames > 0)
                        reason = "CLIENT_DECODE_STALL";
                    else if (s.PresenterAttached && presentAge >= 1200 && s.Decoded > 0)
                        reason = "CLIENT_PRESENTER_STALL";
                    else if (frameAge >= microMs && s.Packets > 0)
                        reason = packetAge >= microMs ? "CLIENT_RX_GAP" : "CLIENT_FRAME_GAP";
                    else if (s.PresenterAttached && presentAge >= microMs && s.Decoded > 0)
                        reason = "CLIENT_PRESENT_GAP";

                    // Only reference/transport stalls request IDR. A busy decoder
                    // or presenter is diagnosed rather than repeatedly flushed.
                    if ((frameAge >= 1200 || packetAge >= 1200 ||
                         referenceWait) &&
                        now - _lastIdrAt >= 1000)
                    {
                        _lastIdrAt = now;
                        idr = true;
                    }
                }
            }
            if (reason.Length > 0)
            {
                if (reason == _lastReason && now - _lastEventAt < 2000)
                    reason = "";
                else
                {
                    _lastEventAt = now;
                    _lastReason = reason;
                }
            }
            else if (!idr && !rebind)
            {
                _lastReason = "";
            }
            return new VideoProgressDecision
            {
                Reason = reason,
                PacketAgeMs = packetAge,
                FrameAgeMs = frameAge,
                DecodeAgeMs = decodeAge,
                PresentAgeMs = presentAge,
                CaptureAgeMs = captureAge,
                EncodeAgeMs = encodeAge,
                RequestIdr = idr,
                RecoverReceiver = rebind
            };
        }
    }
}
