using System;
using System.Collections.Generic;

namespace Multiverse.Services
{
    // Guarded by HostTransmissionService's ownership gate. Request identity is
    // recorded before any health veto, while cooldown/budget are consumed only
    // when a sender rotation will actually be attempted.
    internal sealed class HostSenderRecoveryPolicy
    {
        private int _session = -1;
        private long _stream = -1;
        private long _lastRequestId;
        private long _lastAttemptAt = long.MinValue;
        private readonly Queue<long> _attempts = new();

        public bool TryRegisterRequest(int session, long stream, long requestId, out string reason)
        {
            reason = "";
            if (session <= 0 || stream <= 0 || requestId <= 0)
            {
                reason = "INVALID_REQUEST";
                return false;
            }
            if (_session != session || _stream != stream)
            {
                _session = session;
                _stream = stream;
                _lastRequestId = 0;
                _lastAttemptAt = long.MinValue;
                _attempts.Clear();
            }
            if (requestId <= _lastRequestId)
            {
                reason = "REPLAY";
                return false;
            }
            _lastRequestId = requestId;
            return true;
        }

        public bool TryConsumeAttempt(long now, out string reason)
        {
            reason = "";
            if (_lastAttemptAt != long.MinValue && now - _lastAttemptAt < 6000)
            {
                reason = "COOLDOWN";
                return false;
            }
            while (_attempts.Count > 0 && now - _attempts.Peek() >= 60000)
                _attempts.Dequeue();
            if (_attempts.Count >= 2)
            {
                reason = "BUDGET";
                return false;
            }
            _lastAttemptAt = now;
            _attempts.Enqueue(now);
            return true;
        }
    }
}
