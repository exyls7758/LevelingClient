namespace Multiverse.Services;
internal sealed class LegacyDetector
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
            s.CallbackAgeMs < 0 || s.CallbackAgeMs > 1000 || s.PublishAgeMs < 5000 ||
            !HasEncoderFault(s)) { _suspect = -1; return false; }
        if (_suspect < 0) _suspect = s.Now;
        return s.Now - _suspect >= 1000;
    }
    internal static bool HasEncoderFault(EncoderStallEvidence s) => !s.OutputWorkerAlive ||
        (s.Capacity > 0 && (s.InFlight >= s.Capacity || s.PendingOutputs >= s.Capacity)) ||
        s.Status == unchecked((int)0x800705B4) || s.OldestAgeMs >= 5000;
}
