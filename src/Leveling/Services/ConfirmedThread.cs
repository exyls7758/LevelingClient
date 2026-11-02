using System;
using System.Threading;

namespace Multiverse.Services
{
    // A timeout is not proof that the worker has stopped. Callers retain
    // their thread, socket, event and native handles until this succeeds.
    internal static class ConfirmedThread
    {
        internal static bool JoinOrThrow(Thread? thread, int timeoutMs, string name)
        {
            if (thread is null) return false;
            if (thread == Thread.CurrentThread)
                throw new InvalidOperationException(name + ": o worker não pode encerrar a si próprio.");
            if (timeoutMs < 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
            try
            {
                if (!thread.Join(timeoutMs))
                    throw new TimeoutException(name + ": a thread não encerrou no prazo. Recursos preservados.");
            }
            catch (ThreadStateException)
            {
                // A thread created but never started has no code executing.
                if (thread.ThreadState != ThreadState.Unstarted) throw;
            }
            return true;
        }
    }
}
