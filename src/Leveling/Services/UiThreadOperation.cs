using System;
using System.Threading.Tasks;

namespace Multiverse.Services;

internal sealed class UiThreadOperation(Func<bool> hasAccess, Func<Action, bool> enqueue)
{
    public Task RunAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Invoke()
        {
            try { action(); completion.TrySetResult(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }
        if (hasAccess()) Invoke();
        else if (!enqueue(Invoke)) completion.TrySetException(new InvalidOperationException("A fila da interface está encerrada; reabra o Leveling."));
        return completion.Task;
    }
}

// Retry only surface attachment, never NVENC, network or the whole session.
internal sealed class PresentationRetry
{
    private int _failures;
    public bool TrySchedule() => ++_failures <= 3;
    public void Reset() => _failures = 0;
}

// Keep the surface during temporary decoder gate contention; retries are bounded.
internal sealed class PresentationBusyRetry
{
    private long? _started;
    public bool TrySchedule(int status, long nowMilliseconds)
    {
        if (status != unchecked((int)0x80070102)) return false;
        _started ??= nowMilliseconds;
        return nowMilliseconds - _started.Value < 3000;
    }
    public void Reset() => _started = null;
}

internal static class SurfaceDetachRetry
{
    public static async Task RunAsync(Func<Task<bool>> attempt, Func<Task> delay)
    {
        for (int i = 0; i < 10; i++)
        {
            if (await attempt()) return;
            if (i < 9) await delay();
        }
        throw new TimeoutException("A superfície ainda está em uso após as tentativas de liberação.");
    }
}
