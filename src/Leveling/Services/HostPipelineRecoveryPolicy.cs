using System;

namespace Multiverse.Services
{
    internal sealed class HostPipelineRecoverySample
    {
        public long NowMs { get; init; }
        public int SessionGeneration { get; init; }
        public long StreamGeneration { get; init; }
        public long RuntimeGeneration { get; init; }
        public bool Connected { get; init; }
        public bool Running { get; init; }
        public bool EncoderRunning { get; init; }
        public bool CaptureRunning { get; init; }
        public bool EncoderWorkerAlive { get; init; }
        public bool StopInProgress { get; init; }
        public double CaptureCallbackAgeMs { get; init; } = -1;
        public double CapturePublishAgeMs { get; init; } = -1;
        public double EncoderOutputAgeMs { get; init; } = -1;
        public double OldestInFlightMs { get; init; }
        public uint InFlightFrames { get; init; }
        public uint InputSlots { get; init; }
        public uint QueuedPackets { get; init; }
        public uint QueuedBytes { get; init; }
        public long EncodedFrames { get; init; }
    }

    // Pure, deterministic decision policy. Native teardown and session ownership
    // belong exclusively to HostTransmissionService, never to this class.
    internal sealed class HostPipelineRecoveryPolicy
    {
        private const long ConfirmMs = 1000;
        private const long CooldownMs = 60000;
        private const long HealthyMs = 2000;
        private int _session = -1;
        private long _stream = -1;
        private long _runtime = -1;
        private long _lastNow = -1;
        private long _firstSeenAt = -1;
        private long _suspectSince = -1;
        private long _healthySince = -1;
        private long _lastAttempt = -1;
        private bool _attempted;
        private bool _healthyAfterAttempt;
        private bool _active;
        private string _reason = "";
        private long _lastEncoded = -1;

        internal bool AttemptActive => _active;

        internal void Reset()
        {
            _session = -1;
            _stream = -1;
            _runtime = -1;
            _lastNow = -1;
            _firstSeenAt = -1;
            _suspectSince = -1;
            _healthySince = -1;
            _lastAttempt = -1;
            _attempted = false;
            _healthyAfterAttempt = false;
            _active = false;
            _reason = "";
            _lastEncoded = -1;
        }

        internal string Observe(HostPipelineRecoverySample s)
        {
            long now = s.NowMs;
            bool newSession = s.SessionGeneration != _session ||
                s.StreamGeneration != _stream || now < _lastNow;
            if (newSession)
            {
                if (!_active)
                {
                    _lastAttempt = -1;
                    _attempted = false;
                    _healthyAfterAttempt = false;
                }
                _session = s.SessionGeneration;
                _stream = s.StreamGeneration;
                _runtime = s.RuntimeGeneration;
                _lastEncoded = -1;
                _firstSeenAt = now;
                _suspectSince = -1;
                _healthySince = -1;
                _reason = "";
            }
            else if (s.RuntimeGeneration != _runtime)
            {
                _runtime = s.RuntimeGeneration;
                _lastEncoded = -1;
                _firstSeenAt = now;
                _suspectSince = -1;
                _healthySince = -1;
                _reason = "";
            }
            _lastNow = now;
            if (_active || !s.Connected || !s.Running || !s.EncoderRunning ||
                !s.CaptureRunning || !s.EncoderWorkerAlive || s.StopInProgress)
            {
                _suspectSince = -1;
                _healthySince = -1;
                _reason = "";
                return "";
            }

            bool progressing = _lastEncoded >= 0 && s.EncodedFrames > _lastEncoded;
            _lastEncoded = s.EncodedFrames;
            if (progressing)
            {
                _suspectSince = -1;
                _reason = "";
                if (_healthySince < 0) _healthySince = now;
                if (_attempted && now - _healthySince >= HealthyMs)
                    _healthyAfterAttempt = true;
                return "";
            }
            _healthySince = -1;
            // A full coded queue is transport pressure, not a GPU hang.
            // Do not destroy a valid GOP just because the network is slow.
            if (s.QueuedPackets >= 3 || s.QueuedBytes >= 4u * 1024u * 1024u)
            {
                _suspectSince = -1;
                _reason = "";
                return "";
            }
            bool oldOutput = s.EncoderOutputAgeMs >= 5000 ||
                (s.EncodedFrames == 0 && s.EncoderOutputAgeMs < 0 &&
                 _firstSeenAt >= 0 && now - _firstSeenAt >= 5000);
            bool sourceActive = s.CaptureCallbackAgeMs >= 0 &&
                s.CaptureCallbackAgeMs <= 1000;
            bool allSlotsStuck = s.InputSlots > 0 &&
                s.InFlightFrames >= s.InputSlots && s.OldestInFlightMs >= 5000;
            string reason = "";
            if (oldOutput && allSlotsStuck)
                reason = "HOST_NVENC_SLOTS_STALL";
            else if (oldOutput && sourceActive && s.CapturePublishAgeMs >= 5000)
                reason = "HOST_CAPTURE_STALL";
            else if (oldOutput && sourceActive)
                reason = "HOST_ENCODE_STALL";
            if (reason.Length == 0)
            {
                _suspectSince = -1;
                _reason = "";
                return "";
            }
            if (_reason != reason || _suspectSince < 0)
            {
                _reason = reason;
                _suspectSince = now;
            }
            if (_attempted && _healthyAfterAttempt && _lastAttempt >= 0 &&
                now - _lastAttempt >= CooldownMs)
                _attempted = false;
            if (now - _suspectSince < ConfirmMs || _attempted ||
                (_lastAttempt >= 0 && now - _lastAttempt < CooldownMs))
                return "";
            _active = true;
            _attempted = true;
            _healthyAfterAttempt = false;
            _lastAttempt = now;
            return reason;
        }

        internal void Complete(long now)
        {
            _active = false;
            _healthyAfterAttempt = false;
            _lastAttempt = now;
            _suspectSince = -1;
            _healthySince = -1;
            _lastEncoded = -1;
            _reason = "";
        }
    }
}
