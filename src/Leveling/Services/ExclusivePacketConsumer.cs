using System;
using System.Threading;

namespace Multiverse.Services
{
    /// <summary>
    /// Serializes native packet reads and consumer ownership. A superseded
    /// sender cannot read or disable the queue belonging to its replacement.
    /// The native activation callback must not call back into this gate.
    /// </summary>
    internal sealed class ExclusivePacketConsumer<T> where T : class
    {
        private readonly object _sync = new();
        private readonly Action<bool> _setActive;
        private readonly Func<T?> _read;
        private long _generation;
        private bool _active;

        internal ExclusivePacketConsumer(Action<bool> setActive, Func<T?> read)
        {
            _setActive = setActive;
            _read = read;
        }

        internal Lease Acquire()
        {
            lock (_sync)
            {
                unchecked { ++_generation; }
                _active = false;
                _setActive(false);
                try
                {
                    _setActive(true);
                    _active = true;
                    return new Lease(this, _generation);
                }
                catch
                {
                    _setActive(false);
                    throw;
                }
            }
        }

        internal void Invalidate() => RunInactive(() => { });

        internal TResult RunInactive<TResult>(Func<TResult> action)
        {
            lock (_sync)
            {
                unchecked { ++_generation; }
                _active = false;
                _setActive(false);
                return action();
            }
        }

        internal void RunInactive(Action action) => RunInactive(() =>
        {
            action();
            return true;
        });

        private T? Read(long generation)
        {
            lock (_sync)
                return _active && generation == _generation ? _read() : null;
        }

        private bool IsCurrent(long generation)
        {
            lock (_sync)
                return _active && generation == _generation;
        }

        private void Release(long generation)
        {
            lock (_sync)
            {
                if (!_active || generation != _generation)
                    return;
                _active = false;
                unchecked { ++_generation; }
                _setActive(false);
            }
        }

        internal sealed class Lease : IDisposable
        {
            private readonly ExclusivePacketConsumer<T> _owner;
            private readonly long _generation;
            private int _released;

            internal Lease(ExclusivePacketConsumer<T> owner, long generation)
            {
                _owner = owner;
                _generation = generation;
            }

            internal bool IsCurrent =>
                Volatile.Read(ref _released) == 0 && _owner.IsCurrent(_generation);

            internal T? TryRead() =>
                Volatile.Read(ref _released) == 0 ? _owner.Read(_generation) : null;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                    _owner.Release(_generation);
            }
        }
    }
}
