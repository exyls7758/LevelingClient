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
