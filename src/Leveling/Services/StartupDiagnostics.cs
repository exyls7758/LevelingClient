using System;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Multiverse.Services
{
    /// <summary>
    /// Small, independent startup/error journal. No WinUI, networking or media dependencies.
    /// Never handles fatal exceptions or records configuration values by design.
    /// </summary>
    internal static class StartupDiagnostics
    {
        private static readonly object Sync = new();
        [ThreadStatic] private static bool _insideLogger;
        private static bool _initialized;
        private static bool _firstChanceEnabled;
        private static int _firstChanceCount;
        private static int _criticalFirstChanceCount;
        private static string _stage = "BeforeApp";
        private static string _path = "";
        private static string _logPrefix = "";
        private static int _logPart;
        private static string _lastError = "";
        private static readonly Regex Secret = new(@"(?i)\b([\w.-]*(?:token|password|passwd|secret|api[_-]?key|authorization|credential|pairing[_-]?code)[\w.-]*)\s*[:=]\s*(?:""[^""]*""|'[^']*'|[^\s,;]+)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
        private static readonly Regex Bearer = new(@"(?i)\bBearer\s+\S+", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
        private static readonly Regex UserPath = new(@"(?i)\b[A-Z]:\\Users\\[^\\\s]+", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
        private static readonly Regex Url = new(@"(?i)https?://[^\s""'<>]+", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
        private const long MaxLogBytes = 2L * 1024 * 1024;

        internal static string LogPath { get { lock (Sync) return _path; } }
        internal static string LastError { get { lock (Sync) return _lastError; } }
        internal static bool FirstChanceEnabled { get { lock (Sync) return _firstChanceEnabled; } }
        internal static string CurrentStage { get { lock (Sync) return _stage; } }

        internal static void Initialize(string build, string? directory = null)
        {
            lock (Sync)
            {
                if (_initialized) return;
                _initialized = true;
                _firstChanceEnabled = true;
                _firstChanceCount = 0;
                _criticalFirstChanceCount = 0;
                _lastError = "";
                _stage = "BeforeApp";
                _logPart = 0;
                string root = directory ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Leveling", "Diagnostics", "Startup");
                try
                {
                    Directory.CreateDirectory(root);
                    _logPrefix = "startup_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") +
                        "_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..8];
                    _path = Path.Combine(root, _logPrefix + ".log");
                    Prune(root);
                }
                catch (Exception ex)
                {
                    _lastError = ex.GetType().Name + ":0x" + ex.HResult.ToString("X8");
                    _path = "";
                }
                AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
                AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
                TaskScheduler.UnobservedTaskException += OnUnobserved;
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            }
            Write("START", "build=" + build + " runtime=" + RuntimeInformation.FrameworkDescription +
                " arch=" + RuntimeInformation.ProcessArchitecture + " os=" + Environment.OSVersion.Version);
        }

        private static void Prune(string root)
        {
            try
            {
                var files = new DirectoryInfo(root).GetFiles("startup_*.log")
                    .OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
                long bytes = 0;
                for (int i = 0; i < files.Length; i++)
                {
                    bytes += files[i].Length;
                    if (i < 20 && bytes <= 20L * 1024 * 1024) continue;
                    try { files[i].Delete(); } catch { }
                }
            }
            catch { }
        }

        private static string Redact(string text)
        {
            try
            {
                text = Secret.Replace(text, "$1=<redacted>");
                text = Bearer.Replace(text, "Bearer <redacted>");
                text = UserPath.Replace(text, @"C:\Users\<user>");
                return Url.Replace(text, "<url>");
            }
            catch (RegexMatchTimeoutException) { return "<diagnostic text omitted: redaction timeout>"; }
        }

        private static void Write(string category, string text)
        {
            if (_insideLogger) return;
            _insideLogger = true;
            try
            {
                lock (Sync)
                {
                    if (string.IsNullOrEmpty(_path)) return;
                    string line = DateTimeOffset.UtcNow.ToString("O") + " [" + category + "] " +
                        "stage=" + _stage + " " + Redact(text) + Environment.NewLine;
                    if (line.Length > 16000) line = line[..16000] + " [truncated]" + Environment.NewLine;
                    if (File.Exists(_path) && new FileInfo(_path).Length + Encoding.UTF8.GetByteCount(line) > MaxLogBytes)
                    {
                        _logPart++;
                        _path = Path.Combine(Path.GetDirectoryName(_path)!, _logPrefix + "_part" + _logPart + ".log");
                        Prune(Path.GetDirectoryName(_path)!);
                    }
                    File.AppendAllText(_path, line, new UTF8Encoding(false));
                }
            }
            catch (Exception ex)
            {
                // Diagnostics must never replace the original application error.
                _lastError = ex.GetType().Name + ":0x" + ex.HResult.ToString("X8");
            }
            finally { _insideLogger = false; }
        }

        internal static void Stage(string stage, string? detail = null)
        {
            lock (Sync) _stage = stage;
            Write("STAGE", detail ?? "");
        }

        internal static void Ready()
        {
            lock (Sync) _firstChanceEnabled = false;
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
            Write("READY", "Janela ativada; o teste visual ainda é necessário.");
        }

        internal static void RecordException(string category, Exception exception, string? detail = null)
        {
            ProblemJournal.Instance.Record(category, exception);
            var text = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(detail)) text.AppendLine(detail);
            int depth = 0;
            for (Exception? current = exception; current is not null && depth < 8; current = current.InnerException, depth++)
            {
                text.Append("exception[").Append(depth).Append("]=").Append(current.GetType().FullName)
                    .Append(" hresult=0x").Append(current.HResult.ToString("X8"))
                    .Append(" message=").AppendLine(current.Message);
                if (!string.IsNullOrWhiteSpace(current.StackTrace)) text.AppendLine(current.StackTrace);
            }
            Write(category, text.ToString());
        }

        private static void OnFirstChance(object? sender, FirstChanceExceptionEventArgs e)
        {
            if (!FirstChanceEnabled || _insideLogger) return;
            Exception ex = e.Exception;
            bool relevant = ex is TypeInitializationException or DllNotFoundException or
                BadImageFormatException or FileNotFoundException or FileLoadException or EntryPointNotFoundException or
                System.Runtime.InteropServices.COMException ||
                ex.GetType().FullName == "Microsoft.UI.Xaml.Markup.XamlParseException";
            if (!relevant) return;
            int count = Interlocked.Increment(ref _firstChanceCount);
            if (count > 32)
            {
                // COM exceptions can be frequent during normal XAML probing.
                // Reserve a second, bounded budget for the likely root cause.
                bool critical = ex is TypeInitializationException or DllNotFoundException or
                    BadImageFormatException or FileNotFoundException or FileLoadException or EntryPointNotFoundException ||
                    ex.GetType().FullName == "Microsoft.UI.Xaml.Markup.XamlParseException";
                if (!critical || Interlocked.Increment(ref _criticalFirstChanceCount) > 16) return;
            }
            if (count == 32) Write("FIRST_CHANCE_LIMIT", "Limite geral atingido; mais 16 exceções críticas podem ser registradas.");
            RecordException("FIRST_CHANCE", ex);
        }

        private static void OnUnhandled(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex) RecordException("UNHANDLED", ex);
            else Write("UNHANDLED", "Exceção não gerenciada/objeto desconhecido.");
        }

        private static void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e) =>
            RecordException("UNOBSERVED_TASK", e.Exception);

        private static void OnProcessExit(object? sender, EventArgs e) => Shutdown();

        internal static void Shutdown()
        {
            lock (Sync)
            {
                if (!_initialized) return;
                _firstChanceEnabled = false;
                AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
                AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
                TaskScheduler.UnobservedTaskException -= OnUnobserved;
                AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
                _initialized = false;
            }
            Write("SHUTDOWN", "Logger encerrado.");
        }
    }
}
