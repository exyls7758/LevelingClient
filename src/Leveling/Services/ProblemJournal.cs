using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Multiverse.Services;

// Small, local, durable exception metadata. Never persist Message, Data,
// source filenames, arguments, input content or arbitrary caller details.
internal sealed class ProblemJournal(string directory, long maxBytes = 1024 * 1024)
{
    internal static ProblemJournal Instance { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Leveling", "Diagnostics", "Problems"));
    private readonly object _sync = new();
    private string Current => Path.Combine(directory, "errors.jsonl");
    private string Previous => Path.Combine(directory, "errors.previous.jsonl");
    private string _lastKey = "";
    private long _lastTick;

    internal void Record(string category, Exception error)
    {
        try
        {
            lock (_sync)
            {
                string safeCategory = new(category.Take(80).Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').ToArray());
                string key = safeCategory + error.GetType().FullName + error.HResult;
                long now = Environment.TickCount64;
                if (key == _lastKey && now - _lastTick < 2000) return;
                _lastKey = key; _lastTick = now;
                var chain = new List<object>();
                for (Exception? current = error; current is not null && chain.Count < 8; current = current.InnerException)
                {
                    string[] methods = (new StackTrace(current, false).GetFrames() ?? Array.Empty<StackFrame>())
                        .Select(f => f.GetMethod()).Where(m => m?.DeclaringType?.Namespace?.StartsWith("Multiverse", StringComparison.Ordinal) == true)
                        .Take(16).Select(m => m!.DeclaringType!.FullName + "." + m.Name).ToArray();
                    chain.Add(new { type = current.GetType().FullName, current.HResult, methods });
                }
                Directory.CreateDirectory(directory);
                if (File.Exists(Current) && new FileInfo(Current).Length >= maxBytes)
                    File.Move(Current, Previous, true);
                File.AppendAllText(Current, JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, category = safeCategory, errors = chain }) + "\n");
            }
        }
        catch { /* Diagnostics must never replace the original failure. */ }
    }

    internal string Read()
    {
        lock (_sync)
        {
            var result = new System.Text.StringBuilder();
            foreach (string path in new[] { Previous, Current })
            {
                try { if (File.Exists(path) && new FileInfo(path).Length <= maxBytes + 65536) result.Append(File.ReadAllText(path)); }
                catch { }
            }
            return result.ToString();
        }
    }
}
