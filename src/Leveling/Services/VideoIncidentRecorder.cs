using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Multiverse.Services
{
    internal sealed class VideoIncidentRecorder : IDisposable
    {
        private static readonly Lazy<VideoIncidentRecorder> LazyInstance = new(() => new VideoIncidentRecorder());
        public static VideoIncidentRecorder Instance => LazyInstance.Value;
        internal static bool IsValueCreated => LazyInstance.IsValueCreated;
        private readonly VideoFlightRecorder _flight;
        private readonly string _instanceId = Guid.NewGuid().ToString("N");
        private readonly byte[] _endpointSalt = RandomNumberGenerator.GetBytes(32);
        private readonly Task<(string Managed, string Native)> _binaryHashes;
        private long _sampleSequence;
        private TimeSpan _lastCpu;
        private long _lastCpuTick;
        private readonly Dictionary<string, long> _lastCounters = new();
        private string _lastCounterIdentity = "";
        private readonly object _sampleSync = new();

        private VideoIncidentRecorder()
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _flight = new VideoFlightRecorder(Path.Combine(root, "Leveling", "Diagnostics", "VideoIncidents"));
            _binaryHashes = Task.Run(() => (
                FileHash(typeof(VideoIncidentRecorder).Assembly.Location),
                FileHash(Path.Combine(AppContext.BaseDirectory, "Multiverse.Core.dll"))));
        }
        public string DirectoryPath => _flight.DirectoryPath;
        public string LastReason => _flight.LastReason;
        public string LastWriteError => _flight.LastWriteError;
        public long DroppedEntries => _flight.DroppedEntries;

        private static string FileHash(string path)
        {
            try { return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))); }
            catch { return "unavailable"; }
        }

        private string EndpointHash(string? endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) return "none";
            byte[] data = Encoding.UTF8.GetBytes(endpoint);
            return Convert.ToHexString(HMACSHA256.HashData(_endpointSalt, data))[..16];
        }

        // Only numeric, boolean and enum properties are admitted. No names,
        // addresses, pairing codes, input, audio, frames or message payloads.
        private static Dictionary<string, object?> Numbers(object? value)
        {
            var result = new Dictionary<string, object?>();
            if (value is null) return result;
            // Never serialize raw input/packet payload objects, even if a
            // future diagnostic caller accidentally supplies one.
            if (value.GetType().Name.EndsWith("Packet", StringComparison.Ordinal)) return result;
            foreach (PropertyInfo property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length != 0 ||
                    property.Name is "RemoteCursorX" or "RemoteCursorY" or
                        "GamepadButtons" or "Buttons" or "LeftTrigger" or "RightTrigger" or
                        "GamepadLeftTrigger" or "GamepadRightTrigger" or
                        "LeftThumbX" or "LeftThumbY" or "RightThumbX" or "RightThumbY" or
                        "GamepadLeftThumbX" or "GamepadLeftThumbY" or "GamepadRightThumbX" or "GamepadRightThumbY") continue;
                Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                bool numeric = type == typeof(bool) || type == typeof(byte) || type == typeof(sbyte) ||
                    type == typeof(short) || type == typeof(ushort) || type == typeof(int) || type == typeof(uint) ||
                    type == typeof(long) || type == typeof(ulong) || type == typeof(float) ||
                    type == typeof(double) || type == typeof(decimal) || type.IsEnum;
                if (!numeric) continue;
                try
                {
                    object? item = property.GetValue(value);
                    if (item is float f && !float.IsFinite(f)) item = null;
                    if (item is double d && !double.IsFinite(d)) item = null;
                    result[property.Name] = item;
                }
                catch { result[property.Name] = null; }
            }
            return result;
        }

        private static string ErrorKind(string? error)
        {
            if (string.IsNullOrWhiteSpace(error)) return "none";
            // Do not persist arbitrary exception messages containing paths/IPs.
            if (error.Contains("0x8007007E", StringComparison.OrdinalIgnoreCase)) return "DLL_NOT_FOUND_8007007E";
            if (error.Contains("10054", StringComparison.Ordinal)) return "SOCKET_10054";
            if (error.Contains("10051", StringComparison.Ordinal)) return "SOCKET_10051";
            if (error.Contains("10050", StringComparison.Ordinal)) return "SOCKET_10050";
            if (error.Contains("NVDEC", StringComparison.OrdinalIgnoreCase)) return "NVDEC_ERROR";
            if (error.Contains("NVENC", StringComparison.OrdinalIgnoreCase)) return "NVENC_ERROR";
            if (error.Contains("UDP", StringComparison.OrdinalIgnoreCase)) return "UDP_ERROR";
            return "OTHER_ERROR";
        }

        internal void RecordEvent(string reason, object? details = null)
        {
            _flight.Collect(JsonSerializer.Serialize(new {
                utc = DateTimeOffset.UtcNow, tick = Environment.TickCount64,
                kind = "event", reason = VideoFlightRecorder.NormalizeReason(reason), details = Numbers(details)
            }));
        }

        public void Sample(RemoteSessionSnapshot session, TransportSnapshot? transport,
            CapturePipelineStats? capture, EncoderStats? encoder, NativeNvdecSnapshot? decoder,
            VideoProgressDecision? progress = null, object? control = null,
            long runtimeGeneration = 0)
        {
            lock (_sampleSync)
            {
                long now = Environment.TickCount64;
                string identity = session.SessionGeneration.ToString(CultureInfo.InvariantCulture) + ":" +
                    (session.StreamConfig?.Generation ?? 0).ToString(CultureInfo.InvariantCulture) + ":" +
                    runtimeGeneration.ToString(CultureInfo.InvariantCulture) + ":" + session.Role;
                if (_lastCounterIdentity != identity)
                {
                    _lastCounters.Clear();
                    _lastCounterIdentity = identity;
                    _lastCpuTick = 0;
                }
                var values = new Dictionary<string, object?>
                {
                    ["session.runtimeGeneration"] = runtimeGeneration
                };
                void Add(string prefix, object? source)
                {
                    foreach (var pair in Numbers(source)) values[prefix + pair.Key] = pair.Value;
                }
                Add("session.", session);
                Add("transport.", transport);
                Add("capture.", capture);
                Add("encoder.", encoder);
                Add("decoder.", decoder);
                Add("progress.", progress);
                Add("control.", control);
                // Snapshots only: never start audio/controller hardware for diagnostics.
                if (RemoteAudioService.IsValueCreated) Add("audio.", RemoteAudioService.Instance.GetSnapshot());
                if (RemoteGamepadService.IsValueCreated) Add("gamepad.", RemoteGamepadService.Instance.GetSnapshot());
                values["transport.endpointHash"] = EndpointHash(transport?.RemoteEndpoint);
                values["transport.mode"] = VideoFlightRecorder.NormalizeReason(transport?.Mode ?? "UNKNOWN");
                values["session.connectionPath"] = session.ConnectionPath;
                values["transport.congestion"] = VideoFlightRecorder.NormalizeReason(transport?.CongestionState ?? "UNKNOWN");
                values["transport.errorKind"] = ErrorKind(transport?.LastError);
                values["decoder.errorKind"] = ErrorKind(decoder?.LastError);
                values["session.errorKind"] = ErrorKind(session.LastError);
                // Deltas are per actual sample interval; a counter reset is not loss.
                var deltas = new Dictionary<string, long>();
                foreach (var pair in values)
                {
                    if (pair.Value is not long and not int and not ulong and not uint) continue;
                    if (!pair.Key.Contains("Frames", StringComparison.Ordinal) &&
                        !pair.Key.Contains("Packets", StringComparison.Ordinal) &&
                        !pair.Key.Contains("Bytes", StringComparison.Ordinal) &&
                        !pair.Key.Contains("Drops", StringComparison.Ordinal) &&
                        !pair.Key.Contains("Gaps", StringComparison.Ordinal) &&
                        !pair.Key.Contains("Resets", StringComparison.Ordinal) &&
                        !pair.Key.Contains("Failures", StringComparison.Ordinal) &&
                        !pair.Key.Contains("Timeouts", StringComparison.Ordinal)) continue;
                    long current;
                    try { current = Convert.ToInt64(pair.Value, CultureInfo.InvariantCulture); }
                    catch { continue; }
                    if (_lastCounters.TryGetValue(pair.Key, out long previous))
                        deltas[pair.Key] = current >= previous ? current - previous : -1;
                    _lastCounters[pair.Key] = current;
                }
                try
                {
                    using Process process = Process.GetCurrentProcess();
                    process.Refresh();
                    long interval = _lastCpuTick == 0 ? 0 : now - _lastCpuTick;
                    TimeSpan cpu = process.TotalProcessorTime;
                    values["process.cpuPercent"] = interval > 0 ?
                        Math.Max(0, (cpu - _lastCpu).TotalMilliseconds / interval * 100.0 / Environment.ProcessorCount) : 0;
                    _lastCpu = cpu; _lastCpuTick = now;
                    values["process.workingSetBytes"] = process.WorkingSet64;
                    values["process.privateBytes"] = process.PrivateMemorySize64;
                    values["process.threads"] = process.Threads.Count;
                    values["process.gcHeapBytes"] = GC.GetTotalMemory(false);
                    values["process.gcGen0"] = GC.CollectionCount(0);
                    values["process.gcGen1"] = GC.CollectionCount(1);
                    values["process.gcGen2"] = GC.CollectionCount(2);
                    var gc = GC.GetGCMemoryInfo();
                    values["process.gcPauseMs"] = gc.PauseTimePercentage;
                    values["process.gcCommittedBytes"] = gc.TotalCommittedBytes;
                    values["process.gcFragmentedBytes"] = gc.FragmentedBytes;
                }
                catch { }
                _flight.Collect(JsonSerializer.Serialize(new {
                    utc = DateTimeOffset.UtcNow, tick = now,
                    seq = ++_sampleSequence, instance = _instanceId, kind = "sample",
                    values, deltas
                }));
            }
        }

        public void MarkIncident(string reason, long durationMs) => _flight.MarkIncident(reason, durationMs);

        public string LastReportSummary { get; private set; } = "";

        public async Task<string> ExportAsync(int category = 0)
        {
            var hashes = await _binaryHashes.ConfigureAwait(false);
            string[] categories = { "Geral", "Vídeo", "Áudio", "Teclado, mouse e controle", "Conexão", "Travamento ou encerramento" };
            string selected = categories[Math.Clamp(category, 0, categories.Length - 1)];
            string reportId = Guid.NewGuid().ToString("N")[..12];
            var utc = DateTimeOffset.UtcNow;
            // File I/O and compression stay off the UI thread, even when hashes are cached.
            return await Task.Run(async () =>
            {
            var session = RemoteSessionService.Instance.GetSnapshot();
            var audio = RemoteAudioService.IsValueCreated ? RemoteAudioService.Instance.GetSnapshot() : null;
            var gamepad = RemoteGamepadService.IsValueCreated ? RemoteGamepadService.Instance.GetSnapshot() : null;
            string errors = ProblemJournal.Instance.Read();
            int errorCount = errors.Count(c => c == '\n');
            string summary = $"Leveling — relatório {reportId}\nVersão: {BuildInfo.DisplayVersion}\nUTC: {utc:O}\nÁrea: {selected}\n" +
                $"Sessão conectada: {session.Connected}\nÚltimo incidente: {VideoFlightRecorder.NormalizeReason(LastReason)}\n" +
                $"Erros registrados (histórico limitado): {errorCount}\n" +
                "Inclui histórico disponível de vídeo/rede, estados de áudio/controle e metadados de exceções.\n" +
                "Não inclui mídia, teclas, mensagens de exceção, caminhos pessoais ou códigos de conexão.\n" +
                "A categoria foi escolhida pelo usuário; este relatório não determina sozinho a causa.\n" +
                "Para comparar os dois PCs, gere e envie um ZIP em cada computador logo após o problema.";
            var attachments = new Dictionary<string, string> {
                ["summary.txt"] = summary,
                ["errors.jsonl"] = errors,
                ["report.json"] = JsonSerializer.Serialize(new {
                    reportId, utc, category = selected, version = BuildInfo.DisplayVersion,
                    processorCount = Environment.ProcessorCount,
                    session = Numbers(session), control = Numbers(RemoteSessionService.Instance.GetControlDiagnostics()),
                    audio = Numbers(audio), audioError = ErrorKind(audio?.LastError),
                    gamepad = Numbers(gamepad), gamepadError = ErrorKind(gamepad?.LastError)
                }),
                ["README.txt"] = "Pacote local. Nenhum envio automático. UTC permite comparar os dois computadores. errors.jsonl inclui execuções anteriores dentro da retenção. Ausência de erro não comprova ausência de falha. Falhas de energia, kernel ou driver podem impedir o registro final. dropped no manifesto inclui descarte por retenção; não equivale a perda de rede."
            };
            string path = await _flight.ExportAsync(JsonSerializer.Serialize(new {
            format = "leveling-diagnostics-v3", instance = _instanceId, reportId, version = BuildInfo.DisplayVersion,
            utc = DateTimeOffset.UtcNow, managedSha256 = hashes.Managed, nativeSha256 = hashes.Native,
            runtime = RuntimeInformation.FrameworkDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            os = Environment.OSVersion.VersionString, dropped = DroppedEntries,
            lastReason = LastReason, lastWriteError = ErrorKind(LastWriteError),
            privacy = "numeric/state telemetry only; endpoints keyed-hashed; no media, inputs or pairing codes"
        }), attachments).ConfigureAwait(false);
            LastReportSummary = summary;
            return path;
            }).ConfigureAwait(false);
        }
        public void Dispose() => _flight.Dispose();
    }
}
