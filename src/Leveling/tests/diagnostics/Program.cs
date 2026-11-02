using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Multiverse.Services;

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
}

try { RunRegression(); return 0; }
catch (Exception ex) { Console.Error.WriteLine($"Startup/settings regression: FAIL ({ex.GetType().Name}): {ex.Message}"); return 1; }

static void RunRegression()
{
var packaged = new MemoryStore();
var portable = new MemoryStore();
Check(ReferenceEquals(SettingsStoreSelector.Select(true, () => packaged, () => portable), packaged), "package identity keeps packaged settings");
Check(ReferenceEquals(SettingsStoreSelector.Select(false, () => packaged, () => portable), portable), "unpackaged app uses portable settings");
packaged.SetValue("original", "keep");
portable.SetValue("original", "separate");
Check(packaged.TryGetValue("original", out var first) && Equals(first, "keep") &&
      portable.TryGetValue("original", out var second) && Equals(second, "separate"), "backends remain isolated");
portable.SetValue("Multiverse.Encoder.PipelineMode", "7");
SettingsMigration.MigratePipelineMode(portable);
Check(portable.TryGetValue("Multiverse.Pipeline.Mode", out var migrated) && Equals(migrated, "7"), "legacy pipeline migrates semantically");
Check(!portable.TryGetValue("Multiverse.Encoder.PipelineMode", out _), "successful migration removes old key");
portable.SetValue("Multiverse.Encoder.PipelineMode", "2");
portable.SetValue("Multiverse.Pipeline.Mode", "10");
SettingsMigration.MigratePipelineMode(portable);
Check(portable.TryGetValue("Multiverse.Pipeline.Mode", out var currentMode) && Equals(currentMode, "10"), "existing semantic setting wins");
var failing = new MemoryStore { FailWrites = true };
failing.Data["Multiverse.Encoder.PipelineMode"] = "8";
try { SettingsMigration.MigratePipelineMode(failing); } catch (IOException) { }
Check(failing.TryGetValue("Multiverse.Encoder.PipelineMode", out _), "failed migration never deletes original");
var silentFailure = new MemoryStore { IgnoreWrites = true };
silentFailure.Data["Multiverse.Encoder.PipelineMode"] = "9";
SettingsMigration.MigratePipelineMode(silentFailure);
Check(silentFailure.TryGetValue("Multiverse.Encoder.PipelineMode", out _), "unconfirmed migration never deletes original");

var root = Path.Combine(Path.GetTempPath(), "LevelingStartupRegression_" + Guid.NewGuid().ToString("N"));
try
{
    StartupDiagnostics.Initialize("TEST", root);
    StartupDiagnostics.Stage("TEST_STAGE");
    StartupDiagnostics.RecordException("TEST_FAILURE", new TypeInitializationException("Example", new InvalidOperationException("inner-marker")));
    StartupDiagnostics.Ready();
    var text = File.ReadAllText(StartupDiagnostics.LogPath);
    Check(text.Contains("TEST_STAGE") && text.Contains("TEST_FAILURE"), "startup stages are persisted");
    Check(text.Contains("TypeInitializationException") && text.Contains("inner-marker"), "nested exception is preserved");
    Check(!StartupDiagnostics.FirstChanceEnabled, "first-chance collection stops after ready");
    for (int i = 0; i < 260; i++)
        StartupDiagnostics.RecordException("TEST_BOUNDED", new InvalidOperationException(new string('x', 12000)));
    Check(new DirectoryInfo(root).GetFiles("startup_*.log").All(f => f.Length <= 2L * 1024 * 1024),
        "diagnostic files obey the size limit");
    StartupDiagnostics.RecordException("TEST_SECRET", new InvalidOperationException("token=private-value"));
    Check(!File.ReadAllText(StartupDiagnostics.LogPath).Contains("private-value"), "sensitive key-value text is redacted");
    Check(StartupDiagnostics.LogPath.StartsWith(root, StringComparison.OrdinalIgnoreCase), "test log stays in explicit directory");
}
finally { StartupDiagnostics.Shutdown(); try { Directory.Delete(root, true); } catch { } }

if (OperatingSystem.IsWindows())
{
    string key = @"Software\Leveling\Regression_" + Guid.NewGuid().ToString("N");
    try
    {
        var store = new RegistrySettingsStore(key);
        store.SetValue("text", "áudio e português");
        store.SetValue("bool", true);
        store.SetValue("number", 60);
        var reopened = new RegistrySettingsStore(key);
        Check(reopened.TryGetValue("text", out var text) && Equals(text, "áudio e português") &&
              reopened.TryGetValue("bool", out var boolean) && Equals(boolean, "True") &&
              reopened.TryGetValue("number", out var number) && Equals(number, "60"), "registry values persist across instances");
        reopened.RemoveValue("text");
        Check(!store.TryGetValue("text", out _), "registry deletion is explicit");
    }
    finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(key, false); }
}
Console.WriteLine("Startup/settings regression: PASS");
}

sealed class MemoryStore : ILevelingSettingsStore
{
    internal readonly Dictionary<string, object?> Data = new(StringComparer.Ordinal);
    internal bool FailWrites;
    internal bool IgnoreWrites;
    public string Description => "memory";
    public bool TryGetValue(string key, out object? value) => Data.TryGetValue(key, out value);
    public void SetValue(string key, object value) { if (FailWrites) throw new IOException("test write failure"); if (!IgnoreWrites) Data[key] = value; }
    public void RemoveValue(string key) => Data.Remove(key);
}
