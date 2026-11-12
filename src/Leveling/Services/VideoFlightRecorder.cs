using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Multiverse.Services
{
    /// <summary>
    /// Bounded, single-writer flight recorder. The producer only copies small
    /// telemetry strings under a lock; disk and ZIP work never run on RX/TX.
    /// </summary>
    internal sealed class VideoFlightRecorder : IDisposable
    {
        private sealed class Entry
        {
            internal long Tick;
            internal string Json = "";
            internal int Bytes;
        }
        private sealed class Incident
        {
            internal string Id = Guid.NewGuid().ToString("N");
            internal string Reason = "";
            internal long Started;
            internal long Ends;
            internal readonly List<Entry> Entries = new();
            internal int Bytes;
            internal long Dropped;
            internal Entry? _incidentFirst;
            internal Entry? _incidentLast;
        }
        private const int MaxRingBytes = 8 * 1024 * 1024;
        private const int MaxIncidentBytes = 4 * 1024 * 1024;
        private const int MaxPending = 4;
        private const int MaxPendingBytes = 16 * 1024 * 1024;
        private readonly object _sync = new();
        private readonly SemaphoreSlim _fileGate = new(1, 1);
        private readonly Queue<Entry> _ring = new();
        private readonly Queue<Incident> _pending = new();
        private int _pendingBytes;
        private readonly Func<long> _clock;
        private readonly string _directory;
        private readonly CancellationTokenSource _cancel = new();
        private readonly Task _worker;
        private Incident? _active;
        private int _ringBytes;
        private long _dropped;
        private long _lastIncidentAt = -100000;
        private string _lastReason = "";
        private string _lastWriteError = "";
        private bool _disposed;

        internal VideoFlightRecorder(string directory, Func<long>? clock = null, bool startWorker = true)
        {
            _directory = directory;
            _clock = clock ?? (() => Environment.TickCount64);
            _worker = startWorker ? Task.Run(() => WorkerAsync(_cancel.Token)) : Task.CompletedTask;
        }
        internal string DirectoryPath => _directory;
        internal string LastReason { get { lock (_sync) return _lastReason; } }
        internal string LastWriteError { get { lock (_sync) return _lastWriteError; } }
        internal long DroppedEntries { get { lock (_sync) return _dropped; } }
        internal long PendingBytes { get { lock (_sync) return _pendingBytes; } }

        internal void Collect(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            // Never allow an unexpected error string to exhaust the recorder.
            if (Encoding.UTF8.GetByteCount(json) > 65536)
                json = JsonSerializer.Serialize(new { kind = "oversized_sample", originalChars = json.Length });
            var entry = new Entry { Tick = _clock(), Json = json, Bytes = Encoding.UTF8.GetByteCount(json) + 1 };
            lock (_sync)
            {
                if (_disposed) return;
                _ring.Enqueue(entry);
                _ringBytes += entry.Bytes;
                while (_ringBytes > MaxRingBytes || _ring.Count > 8000)
                {
                    Entry old = _ring.Dequeue();
                    _ringBytes -= old.Bytes;
                    _dropped++;
                }
                if (_active is not null)
                    AddToIncident(_active, entry);
            }
        }

        private void AddToIncident(Incident incident, Entry entry)
        {
            incident.Entries.Add(entry);
            incident.Bytes += entry.Bytes;
            incident._incidentFirst ??= entry;
            incident._incidentLast = entry;
            while (incident.Bytes > MaxIncidentBytes && incident.Entries.Count > 1)
            {
                int index = incident.Entries.Count > 2 ? 1 : 0;
                incident.Bytes -= incident.Entries[index].Bytes;
                incident.Entries.RemoveAt(index);
                incident.Dropped++;
                _dropped++;
            }
        }

        internal static string NormalizeReason(string value)
        {
            if (value.Length is < 1 or > 96) return "UNCLASSIFIED";
            foreach (char c in value)
                if (!(c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_'))
                    return "UNCLASSIFIED";
            return value;
        }

        internal void MarkIncident(string reason, long durationMs)
        {
            if (string.IsNullOrEmpty(reason)) return;
            reason = NormalizeReason(reason);
            long now = _clock();
            lock (_sync)
            {
                if (_disposed) return;
                if (_active is null)
                {
                    if (now - _lastIncidentAt < 1000) return;
                    _active = new Incident { Reason = reason, Started = now, Ends = now + 20000 };
                    foreach (Entry entry in _ring)
                        if (now - entry.Tick <= 60000)
                            AddToIncident(_active, entry);
                }
                else
                {
                    _active.Ends = Math.Min(_active.Started + 60000, Math.Max(_active.Ends, now + 20000));
                }
                _lastIncidentAt = now;
                _lastReason = reason;
            }
            Collect(JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, kind = "incident", reason, durationMs }));
        }

        private void FinalizeIncidentLocked(bool force)
        {
            if (_active is null || (!force && _clock() < _active.Ends)) return;
            while (_pending.Count >= MaxPending ||
                _pendingBytes + _active.Bytes > MaxPendingBytes)
            {
                if (_pending.Count == 0) break;
                _pendingBytes -= _pending.Dequeue().Bytes;
                _dropped++;
            }
            _pending.Enqueue(_active);
            _pendingBytes += _active.Bytes;
            _active = null;
        }

        private async Task WorkerAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(250, token).ConfigureAwait(false);
                    await FlushAsync(false).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            finally { await FlushAsync(true).ConfigureAwait(false); }
        }

        internal async Task FlushAsync(bool force = false)
        {
            await _fileGate.WaitAsync().ConfigureAwait(false);
            try
            {
                while (true)
                {
                    Incident incident;
                    lock (_sync)
                    {
                        FinalizeIncidentLocked(force);
                        if (_pending.Count == 0) break;
                        incident = _pending.Peek();
                    }
                    try
                    {
                        Directory.CreateDirectory(_directory);
                        string path = Path.Combine(_directory, "video_incident_" + incident.Id + ".jsonl");
                        string temporary = path + ".tmp";
                        using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(false)))
                        {
                            await writer.WriteLineAsync(JsonSerializer.Serialize(new {
                                format = "leveling-video-incident-v2", incident.Id, incident.Reason,
                                incident.Started, incident.Dropped, incident.Bytes,
                                utc = DateTimeOffset.UtcNow
                            })).ConfigureAwait(false);
                            foreach (Entry entry in incident.Entries)
                                await writer.WriteLineAsync(entry.Json).ConfigureAwait(false);
                        }
                        File.Move(temporary, path, true);
                        lock (_sync)
                        {
                            if (_pending.Count > 0 && ReferenceEquals(_pending.Peek(), incident))
                            {
                                _pending.Dequeue();
                                _pendingBytes -= incident.Bytes;
                            }
                            _lastWriteError = "";
                        }
                        PruneFiles();
                    }
                    catch (Exception ex)
                    {
                        lock (_sync) _lastWriteError = ex.GetType().Name + ":0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture);
                        break; // Keep the bounded pending incident for the next attempt.
                    }
                }
            }
            finally { _fileGate.Release(); }
        }

        private void PruneFiles()
        {
            var files = new DirectoryInfo(_directory).GetFiles("video_incident_*.jsonl")
                .OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
            long bytes = 0;
            for (int i = 0; i < files.Length; i++)
            {
                bytes += files[i].Length;
                if (i < 20 && bytes <= 50L * 1024 * 1024) continue;
                try { files[i].Delete(); } catch { }
            }
        }

        internal async Task<string> ExportAsync(string? manifestJson = null, IReadOnlyDictionary<string, string>? attachments = null)
        {
            await _fileGate.WaitAsync().ConfigureAwait(false);
            try
            {
                string[] recent;
                string[] current;
                Incident[] pending;
                lock (_sync)
                {
                    recent = _ring.Select(e => e.Json).ToArray();
                    current = _active?.Entries.Select(e => e.Json).ToArray() ?? Array.Empty<string>();
                    pending = _pending.ToArray();
                }
                Directory.CreateDirectory(_directory);
                string path = Path.Combine(_directory, "Leveling_Diagnostics_" +
                    DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" +
                    Guid.NewGuid().ToString("N")[..8] + ".zip");
                using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
                {
                    WriteEntry(archive, "manifest.json", manifestJson ?? "{}");
                    if (attachments is not null)
                        foreach (var item in attachments)
                        {
                            if (Path.GetFileName(item.Key) != item.Key || item.Key.Contains('\\') || item.Key.Contains('/'))
                                throw new ArgumentException("Invalid diagnostic attachment name");
                            WriteEntry(archive, item.Key, item.Value);
                        }
                    WriteEntry(archive, "recent.jsonl", string.Join("\n", recent));
                    if (current.Length > 0)
                        WriteEntry(archive, "incident_in_progress.jsonl", string.Join("\n", current));
                    foreach (Incident incident in pending)
                        WriteEntry(archive, "pending_" + incident.Id + ".jsonl",
                            string.Join("\n", incident.Entries.Select(e => e.Json)));
                    foreach (string file in Directory.GetFiles(_directory, "video_incident_*.jsonl"))
                    {
                        try { archive.CreateEntryFromFile(file, Path.GetFileName(file), CompressionLevel.Fastest); }
                        catch (IOException) { }
                    }
                }
                PruneExports(path);
                return path;
            }
            finally { _fileGate.Release(); }
        }

        private void PruneExports(string keep)
        {
            var files = new DirectoryInfo(_directory).GetFiles("Leveling_Diagnostics_*.zip")
                .OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
            long bytes = 0;
            int retained = 0;
            foreach (FileInfo file in files)
            {
                if (string.Equals(file.FullName, keep, StringComparison.OrdinalIgnoreCase))
                {
                    bytes += file.Length;
                    retained++;
                    continue;
                }
                if (retained < 10 && bytes + file.Length <= 100L * 1024 * 1024)
                {
                    bytes += file.Length;
                    retained++;
                    continue;
                }
                try { file.Delete(); } catch { }
            }
        }

        private static void WriteEntry(ZipArchive archive, string name, string content)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name, CompressionLevel.Fastest).Open(), new UTF8Encoding(false));
            writer.Write(content);
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
            }
            _cancel.Cancel();
            try { _worker.Wait(TimeSpan.FromSeconds(5)); } catch { }
            // Do not dispose the synchronization primitives while a writer may
            // still be completing. The process-wide recorder lives until exit.
        }
    }
}
