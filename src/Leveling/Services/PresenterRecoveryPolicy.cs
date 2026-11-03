namespace Multiverse.Services;

// Local surface recovery only: never restart the sender for a client display failure.
internal sealed class PresenterRecoveryPolicy
{
    private bool _initialized;
    private ulong _decoded, _presented;
    private long _lastDecode, _lastPresent, _lastAttempt;
    private int _attempts, _healthySamples;
    public void Reset() { _initialized = false; _attempts = _healthySamples = 0; }
    public bool Observe(long now, ulong decoded, ulong presented, bool presentInFlight)
    {
        if (!_initialized || decoded < _decoded || presented < _presented)
        {
            _initialized = true; _decoded = decoded; _presented = presented;
            _lastDecode = _lastPresent = now; _lastAttempt = now - 10000;
            return false;
        }
        if (_decoded == 0 && decoded > 0) _lastPresent = now; // first picture gets startup grace
        if (decoded > _decoded) _lastDecode = now;
        if (presented > _presented)
        {
            _lastPresent = now;
            if (++_healthySamples >= 20) _attempts = 0;
        }
        else _healthySamples = 0;
        _decoded = decoded; _presented = presented;
        if (decoded == 0 || now - _lastDecode > 1500 || now - _lastPresent < 3000 ||
            presentInFlight || now - _lastAttempt < 10000 || _attempts >= 3) return false;
        _lastAttempt = now; _attempts++; return true;
    }
}
