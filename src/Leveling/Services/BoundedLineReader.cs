using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Multiverse.Services
{
    /// <summary>
    /// Line reader for the TCP control plane that caps memory consumed by a
    /// peer before a line terminator is received. It keeps StreamReader's
    /// buffered read-ahead semantics while refusing oversized control frames.
    /// </summary>
    internal sealed class BoundedLineReader : IDisposable
    {
        private readonly StreamReader _reader;
        private readonly int _maxChars;
        private readonly char[] _readBuffer;
        private readonly StringBuilder _pending = new();
        private int _scanIndex;
        private bool _eof;
        private bool _disposed;

        internal BoundedLineReader(StreamReader reader, int maxChars, int readBufferChars = 1024)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
            if (maxChars <= 0) throw new ArgumentOutOfRangeException(nameof(maxChars));
            if (readBufferChars <= 0) throw new ArgumentOutOfRangeException(nameof(readBufferChars));
            _maxChars = maxChars;
            _readBuffer = new char[Math.Min(readBufferChars, maxChars + 2)];
        }

        internal async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            while (true)
            {
                if (TryTakeLine(out string? line))
                    return line;

                if (_eof)
                {
                    if (_pending.Length == 0)
                        return null;
                    if (_pending.Length > _maxChars)
                        throw OversizedLine();
                    string final = _pending.ToString();
                    _pending.Clear();
                    _scanIndex = 0;
                    return final;
                }

                if (_pending.Length > _maxChars + 1)
                    throw OversizedLine();

                int read = await _reader.ReadAsync(
                        _readBuffer.AsMemory(0, _readBuffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    _eof = true;
                    continue;
                }
                _pending.Append(_readBuffer, 0, read);
            }
        }

        private bool TryTakeLine(out string? line)
        {
            for (; _scanIndex < _pending.Length; _scanIndex++)
            {
                char current = _pending[_scanIndex];
                if (current != '\r' && current != '\n')
                    continue;

                // A trailing CR may be the first half of CRLF. Wait for one
                // more read unless EOF has already been observed.
                if (current == '\r' && _scanIndex + 1 >= _pending.Length && !_eof)
                    break;

                int lineChars = _scanIndex;
                if (lineChars > _maxChars)
                    throw OversizedLine();

                line = _pending.ToString(0, lineChars);
                int terminatorChars = current == '\r' &&
                    _scanIndex + 1 < _pending.Length &&
                    _pending[_scanIndex + 1] == '\n' ? 2 : 1;
                _pending.Remove(0, _scanIndex + terminatorChars);
                _scanIndex = 0;
                return true;
            }

            // No complete terminator yet. A pending CR is allowed to occupy
            // one extra character beyond the payload limit.
            int payloadChars = _pending.Length;
            if (payloadChars > 0 && _pending[payloadChars - 1] == '\r')
                payloadChars--;
            if (payloadChars > _maxChars)
                throw OversizedLine();

            line = null;
            return false;
        }

        private InvalidDataException OversizedLine() =>
            new($"Mensagem de controle excede o limite de {_maxChars} caracteres.");

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _reader.Dispose();
            _pending.Clear();
        }
    }
}
