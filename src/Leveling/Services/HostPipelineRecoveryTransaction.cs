using System;
using System.Threading;
using System.Threading.Tasks;

namespace Multiverse.Services;

internal enum HostPipelineState { STOPPED, STARTING, RUNNING, RECOVERING_TRANSPORT, RECOVERING_ENCODER, STOPPING, FAULTED }
internal readonly record struct PipelineIdentity(int SessionGeneration, long StreamGeneration, CancellationToken Token);
internal sealed class PipelineBootstrapException : Exception
{
    internal PipelineIdentity Identity { get; }
    internal PipelineBootstrapException(PipelineIdentity identity, Exception cause) : base(cause.Message, cause) { Identity = identity; }
}
internal readonly record struct PipelineProgress(int SessionGeneration, long StreamGeneration,
    long Published, long Encoded, long Sent, long Complete, long Decoded, long Presented, double LastPresentAgeMs);

internal interface IHostPipelineRecoveryDriver
{
    bool IsCurrent(PipelineIdentity id);
    bool NeedsRecovery(PipelineIdentity id, bool encoder);
    PipelineProgress ReadProgress();
    void BlockSubmissions();
    Task StopSenderAsync();
    Task StopEncoderAsync();
    Task StartEncoderAsync();
    Task<PipelineIdentity> StartSenderAsync(PipelineIdentity id, bool newGeneration);
    int RequestIdr();
    Task FaultAsync(PipelineIdentity id, string reason);
    void Record(string name, HostPipelineState previous, HostPipelineState next, PipelineIdentity id, long attempt, string reason, int status);
}

internal sealed class HostPipelineRecoveryTransaction
{
    internal const int ConfirmationTimeoutMs = 10000;
    // Sender stop + bootstrap + full frame confirmation must fit within the
    // client's acknowledgement window. A successful response still returns early.
    internal const int SenderResponseTimeoutMs = 20000;
    internal SemaphoreSlim Gate { get; }
    internal HostPipelineState State { get; private set; } = HostPipelineState.STOPPED;
    internal long Attempts { get; private set; }
    private readonly IHostPipelineRecoveryDriver _driver;
    private readonly Func<long> _clock;
    private readonly Func<CancellationToken, Task> _delay;
    private readonly long _confirmTimeoutMs;
    public HostPipelineRecoveryTransaction(IHostPipelineRecoveryDriver driver, SemaphoreSlim gate,
        Func<long>? clock = null, Func<CancellationToken, Task>? delay = null, long confirmTimeoutMs = ConfirmationTimeoutMs)
    {
        _driver = driver; Gate = gate; _clock = clock ?? (() => Environment.TickCount64);
        _delay = delay ?? (token => Task.Delay(50, token)); _confirmTimeoutMs = confirmTimeoutMs;
    }

    // All callers share the controller's lifecycle gate. Fault is sticky until
    // the owner has explicitly confirmed teardown; observation never clears it.
    internal void SetState(HostPipelineState state, PipelineIdentity id, string reason)
    {
        var previous = State; State = state;
        _driver.Record("PIPELINE_TRANSITION", previous, state, id, Attempts, reason, 0);
    }

    internal async Task<bool> RecoverAsync(PipelineIdentity id, bool encoder)
    {
        try { await Gate.WaitAsync(id.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
        try { return await RecoverWhileGateHeldAsync(id, encoder).ConfigureAwait(false); }
        finally { Gate.Release(); }
    }

    internal async Task<bool> RecoverWhileGateHeldAsync(PipelineIdentity id, bool encoder)
    {
        string prefix = encoder ? "ENCODER" : "TRANSPORT";
        try
        {
            if (State == HostPipelineState.FAULTED || !_driver.IsCurrent(id) ||
                !_driver.NeedsRecovery(id, encoder)) return false;
            var baseline = _driver.ReadProgress();
            Attempts++;
            SetState(encoder ? HostPipelineState.RECOVERING_ENCODER : HostPipelineState.RECOVERING_TRANSPORT, id, prefix + "_RECOVERY_BEGIN");
            Record(prefix + "_RECOVERY_BEGIN", id);
            if (encoder) _driver.BlockSubmissions();
            await _driver.StopSenderAsync().ConfigureAwait(false);
            if (!_driver.IsCurrent(id)) return false;
            if (encoder)
            {
                await _driver.StopEncoderAsync().ConfigureAwait(false);
                Record("ENCODER_STOP_CONFIRMED", id);
                if (!_driver.IsCurrent(id)) return false;
                await _driver.StartEncoderAsync().ConfigureAwait(false);
                Record("ENCODER_RESTARTED", id);
            }
            if (!_driver.IsCurrent(id)) return false;
            id = await _driver.StartSenderAsync(id, encoder).ConfigureAwait(false);
            if (!_driver.IsCurrent(id)) return false;
            int hr = _driver.RequestIdr();
            Record(hr >= 0 ? "ENCODER_IDR_ACCEPTED" : "ENCODER_IDR_REJECTED", id, hr);
            if (hr < 0) throw new InvalidOperationException("RequestIdr rejected: 0x" + hr.ToString("X8"));
            long started = _clock();
            bool published = false, encoded = false, sent = false, complete = false, decoded = false, presented = false;
            while (_driver.IsCurrent(id))
            {
                var progress = _driver.ReadProgress();
                if (progress.SessionGeneration == id.SessionGeneration && progress.StreamGeneration == id.StreamGeneration)
                {
                    void Mark(ref bool flag, bool observed, string name) { if (!flag && observed) { flag = true; Record(name, id); } }
                    Mark(ref published, progress.Published > (encoder ? 0 : baseline.Published), "FIRST_CAPTURE_PUBLISHED");
                    Mark(ref encoded, progress.Encoded > (encoder ? 0 : baseline.Encoded), "FIRST_FRAME_ENCODED_AFTER_RECOVERY");
                    // Every new sender has local counters starting at zero.
                    Mark(ref sent, progress.Sent > 0, "FIRST_FRAME_SENT_AFTER_RECOVERY");
                    Mark(ref complete, progress.Complete > (encoder ? 0 : baseline.Complete), "CLIENT_FIRST_COMPLETE");
                    Mark(ref decoded, progress.Decoded > (encoder ? 0 : baseline.Decoded), "CLIENT_FIRST_DECODED");
                    Mark(ref presented, progress.Presented > (encoder ? 0 : baseline.Presented) &&
                        progress.LastPresentAgeMs >= 0 && progress.LastPresentAgeMs < 2000, "CLIENT_FIRST_FRAME_PRESENTED_AFTER_RECOVERY");
                    if ((!encoder || (published && encoded)) && sent && complete && decoded && presented)
                    {
                        SetState(HostPipelineState.RUNNING, id, prefix + "_RECOVERY_SUCCESS");
                        Record(prefix + "_RECOVERY_SUCCESS", id); return true;
                    }
                }
                if (_clock() - started >= _confirmTimeoutMs) throw new TimeoutException("Frame chain not confirmed");
                await _delay(id.Token).ConfigureAwait(false);
            }
            return false;
        }
        catch (Exception ex)
        {
            if (ex is PipelineBootstrapException bootstrap && bootstrap.Identity.SessionGeneration == id.SessionGeneration)
                id = bootstrap.Identity;
            // The old attempt may not fault/stop a new session. Resource
            // ownership is retained by the driver until its real stop returns.
            if (_driver.IsCurrent(id))
            {
                SetState(HostPipelineState.FAULTED, id, ex.Message);
                Record(prefix + "_RECOVERY_FAILED", id, ex.HResult);
                await _driver.FaultAsync(id, ex.Message).ConfigureAwait(false);
            }
            return false;
        }
    }
    private void Record(string name, PipelineIdentity id, int status = 0) =>
        _driver.Record(name, State, State, id, Attempts, name, status);
}
