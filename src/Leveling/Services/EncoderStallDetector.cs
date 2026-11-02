namespace Multiverse.Services;

internal readonly record struct EncoderStallEvidence(long Now, int Session, long Stream, bool Active,
    long Arrived, long Published, double CallbackAgeMs, double PublishAgeMs, bool EncoderRunning,
    bool OutputWorkerAlive, uint InFlight, uint Capacity, uint PendingOutputs, double OldestAgeMs,
    int Status, bool Stopping);

internal sealed class EncoderStallDetector
{
    private EncoderStallEvidence? _last;
    private long _suspect = -1, _lastArrivalAt = -1;
    internal bool Observe(EncoderStallEvidence s)
    {
        var previous = _last; _last = s;
        if (previous is not { } p || p.Session != s.Session || p.Stream != s.Stream || s.Now < p.Now)
        { _suspect = -1; _lastArrivalAt = -1; return false; }
        if (s.Arrived > p.Arrived) _lastArrivalAt = s.Now;
        if (!s.Active || s.Stopping || s.Published != p.Published ||
            s.Arrived < p.Arrived || _lastArrivalAt < 0 || s.Now - _lastArrivalAt > 1000 ||
            s.CallbackAgeMs < 0 || s.CallbackAgeMs > 1000 ||
            s.PublishAgeMs < RequiredPublishSilenceMs(s) ||
            !HasEncoderFault(s)) { _suspect = -1; return false; }
        if (_suspect < 0) _suspect = s.Now;
        return s.Now - _suspect >= 1000;
    }
    // Native DrainCompletedOutputs has already terminated on ERROR_TIMEOUT.
    // Busy but live workers retain the conservative five-second stall window.
    internal static bool IsTerminalOutputTimeout(bool running, bool outputAlive, int status,
        uint capacity, uint inFlight, uint pending, double oldestMs) =>
        !running && !outputAlive && status == unchecked((int)0x800705B4) &&
        capacity > 0 && inFlight >= capacity && pending >= capacity && oldestMs >= 1000;

    internal static double RequiredPublishSilenceMs(EncoderStallEvidence s) =>
        IsTerminalOutputTimeout(s.EncoderRunning, s.OutputWorkerAlive, s.Status,
            s.Capacity, s.InFlight, s.PendingOutputs, s.OldestAgeMs) ? 1000 : 5000;

    internal static bool HasEncoderFault(EncoderStallEvidence s) => !s.OutputWorkerAlive ||
        (s.Capacity > 0 && (s.InFlight >= s.Capacity || s.PendingOutputs >= s.Capacity)) ||
        s.Status == unchecked((int)0x800705B4) || s.OldestAgeMs >= 5000;
}
