using System;
using System.Threading.Tasks;

namespace Multiverse.Services
{
    // A timeout is not proof that an async worker has stopped. A completed
    // task (including faulted/cancelled) *is* proof that its worker no longer
    // executes, so shutdown ownership may be released without rethrowing the
    // worker's historical exception here. The worker is responsible for
    // recording its own failure at the execution boundary.
    internal static class ConfirmedTask
    {
        internal static bool WaitOrThrow(Task? task, int timeoutMs, string name)
        {
            if (task is null) return false;
            if (timeoutMs < 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));

            if (!task.IsCompleted)
            {
                Task completed = Task.WhenAny(task, Task.Delay(timeoutMs)).GetAwaiter().GetResult();
                if (!ReferenceEquals(completed, task))
                    throw new TimeoutException(name + ": o worker não encerrou no prazo. Recursos preservados.");
            }

            // Faulted/cancelled is still a terminal state and therefore certifies
            // worker shutdown. Observe a fault without rethrowing it here so a
            // historical worker error cannot surface later as an unobserved task
            // exception during GC. The worker logs the original failure itself.
            if (task.IsFaulted)
                _ = task.Exception;
            return true;
        }
    }
}
