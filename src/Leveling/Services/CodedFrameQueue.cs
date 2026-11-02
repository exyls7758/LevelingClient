using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Multiverse.Services
{
    // The queue and its depth share one lock. No producer can publish an
    // item before its depth is counted, and flush cannot race a decrement.
    internal sealed class CodedFrameQueue<T>
    {
        private readonly object _sync = new();
        private readonly Queue<T> _items = new();
        private readonly int _capacity;
        private int _maxObserved;

        internal CodedFrameQueue(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
        }
        internal int Count { get { lock (_sync) return _items.Count; } }
        internal int MaxObserved { get { lock (_sync) return _maxObserved; } }
        internal bool TryEnqueue(T item)
        {
            lock (_sync)
            {
                if (_items.Count >= _capacity) return false;
                _items.Enqueue(item);
                _maxObserved = Math.Max(_maxObserved, _items.Count);
                return true;
            }
        }
        internal bool TryDequeue([MaybeNullWhen(false)] out T item)
        {
            lock (_sync)
            {
                if (_items.Count == 0) { item = default!; return false; }
                item = _items.Dequeue();
                return true;
            }
        }
        internal int Clear()
        {
            lock (_sync)
            {
                int count = _items.Count;
                _items.Clear();
                return count;
            }
        }
        internal void ResetMax()
        {
            lock (_sync) _maxObserved = _items.Count;
        }
    }
}
