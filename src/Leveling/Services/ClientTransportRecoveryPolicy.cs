using System;

namespace Multiverse.Services
{
    internal enum ClientTransportRecoveryAction
    {
        None, ReopenReceiver, RotateSender, Recovered, Exhausted
    }

    internal sealed class ClientTransportRecoverySample
    {
        public long NowMs { get; init; }
        public int SessionGeneration { get; init; }
        public long StreamGeneration { get; init; }
        public bool Connected { get; init; }
        public bool ReceiverRunning { get; init; }
        public bool DecoderRunning { get; init; }
        public bool PresenterAttached { get; init; }
        public bool WaitingForReference { get; init; }
        public long Packets { get; init; }
        public long Frames { get; init; }
        public long Presented { get; init; }
    }

    internal sealed class ClientTransportRecoveryDecision
    {
        public ClientTransportRecoveryAction Action { get; init; }
        public int RemoteAttempts { get; init; }
        public long NoFrameMs { get; init; }
        public long NoPresentMs { get; init; }
        public long StartupMs { get; init; }
        public bool FirstPresented { get; init; }
        public string Reason { get; init; } = "";
    }

    // Deterministic policy; no sockets, native handles or wall-clock time.
    // A successful Start/IDR is only an attempted recovery. Progress at the
    // presenter is the confirmation. Receiver recreation is never repeated
    // after a failed shutdown, and remote rotation has a finite budget.
    internal sealed class ClientTransportRecoveryPolicy
    {
        private enum Stage { Idle, LocalPending, LocalWaiting, RemotePending, RemoteWaiting, Exhausted }
        private Stage _stage;
        private int _session = -1;
        private long _stream = -1;
        private long _lastNow;
        private long _startedAt;
        private long _lastFrameAt;
        private long _lastPresentAt;
        private long _frames;
        private long _presented;
        private long _packets;
        private long _deadline;
        private int _remoteAttempts;
        private bool _firstPresented;
        private bool _exhaustionReported;
        private string _failure = "";

        public void Reset()
        {
            _session = -1;
            _stream = -1;
            _stage = Stage.Idle;
            _remoteAttempts = 0;
            _firstPresented = false;
            _exhaustionReported = false;
            _failure = "";
        }

        public ClientTransportRecoveryDecision Observe(ClientTransportRecoverySample s)
        {
            if (!s.Connected || s.SessionGeneration <= 0 || s.StreamGeneration <= 0 ||
                !s.DecoderRunning || !s.PresenterAttached)
            {
                Reset();
                return new();
            }
            if (_session != s.SessionGeneration || _stream != s.StreamGeneration ||
                s.NowMs < _lastNow || s.Presented < _presented)
            {
                Reset();
                _session = s.SessionGeneration;
                _stream = s.StreamGeneration;
                _startedAt = _lastFrameAt = _lastPresentAt = s.NowMs;
                _frames = s.Frames;
                _presented = s.Presented;
                _packets = s.Packets;
                _lastNow = s.NowMs;
                return new();
            }
            _lastNow = s.NowMs;
            _packets = s.Packets;
            // Receiver recreation resets its counters. That must not erase
            // the recovery budget or count the reset itself as progress.
            bool newFrames = s.Frames > _frames;
            bool newPresent = s.Presented > _presented;
            if (newFrames) _lastFrameAt = s.NowMs;
            if (newPresent) _lastPresentAt = s.NowMs;
            _frames = s.Frames;
            _presented = s.Presented;
            bool first = newPresent && !_firstPresented;
            if (first) _firstPresented = true;
            long startupMs = first ? Math.Max(0, s.NowMs - _startedAt) : 0;
            long frameAge = Math.Max(0, s.NowMs - _lastFrameAt);
            long presentAge = Math.Max(0, s.NowMs - _lastPresentAt);
            var action = ClientTransportRecoveryAction.None;
            string reason = "";
            bool recovering = _stage != Stage.Idle && _stage != Stage.Exhausted;
            if (newPresent)
            {
                if (recovering || _stage == Stage.Exhausted)
                    action = ClientTransportRecoveryAction.Recovered;
                _stage = Stage.Idle;
                _remoteAttempts = 0;
                _failure = "";
                _exhaustionReported = false;
            }
            else if (_stage == Stage.Exhausted)
            {
                if (!_exhaustionReported)
                {
                    action = ClientTransportRecoveryAction.Exhausted;
                    reason = _failure;
                    _exhaustionReported = true;
                }
            }
            else if (_stage == Stage.LocalPending || _stage == Stage.RemotePending)
            {
                // The owner has not completed its operation yet.
            }
            else if (presentAge < 3500 ||
                (s.ReceiverRunning && frameAge < 3500 && !s.WaitingForReference))
            {
                // A new complete frame means a decoder/presenter problem,
                // not evidence that the UDP path needs to be replaced.
            }
            else if (_stage == Stage.Idle)
            {
                _stage = Stage.LocalPending;
                action = ClientTransportRecoveryAction.ReopenReceiver;
                reason = "CLIENT_FRAME_PROGRESS_STALL";
            }
            else if (_stage == Stage.LocalWaiting && s.NowMs >= _deadline)
            {
                _stage = Stage.RemotePending;
                action = ClientTransportRecoveryAction.RotateSender;
                reason = "CLIENT_LOCAL_RECOVERY_NO_PROGRESS";
            }
            else if (_stage == Stage.RemoteWaiting && s.NowMs >= _deadline)
            {
                if (_remoteAttempts < 2)
                {
                    _stage = Stage.RemotePending;
                    action = ClientTransportRecoveryAction.RotateSender;
                    reason = "CLIENT_REMOTE_RECOVERY_RETRY";
                }
                else
                {
                    _stage = Stage.Exhausted;
                    _failure = "CLIENT_RECOVERY_BUDGET_EXHAUSTED";
                    _exhaustionReported = true;
                    action = ClientTransportRecoveryAction.Exhausted;
                    reason = _failure;
                }
            }
            return new()
            {
                Action = action, Reason = reason, RemoteAttempts = _remoteAttempts,
                NoFrameMs = frameAge, NoPresentMs = presentAge,
                FirstPresented = first, StartupMs = startupMs
            };
        }

        public void Complete(ClientTransportRecoveryAction action, bool succeeded, long now,
            int sessionGeneration, long streamGeneration)
        {
            if (_session != sessionGeneration || _stream != streamGeneration)
                return;
            if (action == ClientTransportRecoveryAction.ReopenReceiver && _stage == Stage.LocalPending)
            {
                if (succeeded)
                {
                    _stage = Stage.LocalWaiting;
                    _deadline = now + 2500;
                }
                else
                {
                    _stage = Stage.Exhausted;
                    _failure = "CLIENT_RECEIVER_SHUTDOWN_OR_START_FAILED";
                    _exhaustionReported = false;
                }
            }
            else if (action == ClientTransportRecoveryAction.RotateSender && _stage == Stage.RemotePending)
            {
                _remoteAttempts++;
                _stage = Stage.RemoteWaiting;
                // An explicit REJECTED/failed request is actionable much sooner
                // than an accepted rotation that still needs time to deliver an
                // IDR. Keep a small backoff so a control failure cannot spin.
                _deadline = now + (succeeded ? 8000 : 1500);
                if (!succeeded) _failure = "CLIENT_REMOTE_RECOVERY_REQUEST_FAILED";
            }
        }
    }
}
