using System;
using System.Threading.Tasks;

namespace Multiverse.Services
{
    // Keeps the watchdog responsive while an existing native lifecycle call is
    // still running. A timeout is observational: it never abandons ownership
    // or starts a second recovery on a potentially blocked GPU resource.
    internal sealed class HostPipelineRecoveryCoordinator
    {
        private readonly object _sync = new();
        private Task<bool>? _task;
        private long _startedAt;
        private bool _timeoutReported;
        internal bool Running
        {
            get { lock (_sync) return _task is { IsCompleted: false }; }
        }

        internal bool TryStart(Func<Task<bool>> operation, long now)
        {
            if (operation is null) throw new ArgumentNullException(nameof(operation));
            lock (_sync)
            {
                if (_task is not null) return false;
                // Task.Run prevents synchronous native work from blocking the
                // sampler even if the operation itself starts synchronously.
                _task = Task.Run(operation);
                _startedAt = now;
                _timeoutReported = false;
                return true;
            }
        }

        internal bool TryTakeCompleted(out Task<bool>? completed)
        {
            lock (_sync)
            {
                completed = _task;
                if (completed is null || !completed.IsCompleted)
                {
                    completed = null;
                    return false;
                }
                _task = null;
                return true;
            }
        }

        internal bool TryReportTimeout(long now, long timeoutMs = 12000)
        {
            lock (_sync)
            {
                if (_task is null || _task.IsCompleted || _timeoutReported ||
                    now - _startedAt < timeoutMs) return false;
                _timeoutReported = true;
                return true;
            }
        }
    }
}
