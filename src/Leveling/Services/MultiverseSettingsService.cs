using Multiverse.Models;
using System;
using System.Globalization;

namespace Multiverse.Services
{
    internal sealed class MultiverseStreamProfile
    {
        public uint Fps { get; init; } = 60;
        public uint MaxWidth { get; init; } = 1920;
        public uint MaxHeight { get; init; } = 1080;
        public uint BitrateKbps { get; init; } = 20_000;
        public uint Preset { get; init; }
        public bool UltraLowLatency { get; init; } = true;
        public uint PipelineMode { get; init; } = 10;
        public string MonitorDeviceName { get; init; } = "";
    }

    internal sealed class MultiverseAppPreferences
    {
        public bool LowMotion { get; init; } = false;
        public bool AutoOpenClientWindow { get; init; } = true;
        public bool AutoStartHostStream { get; init; }
        public string DiagnosticsLevel { get; init; } = "Basic";
    }

    /// <summary>
    /// Central persistent settings store for Multiverse pages.
    /// Values are semantic (fps=60, bitrate=20000, preset=1, monitor=\\.\DISPLAY1)
    /// instead of UI indexes, so rearranging a ComboBox cannot silently change a profile.
    /// </summary>
    internal sealed class MultiverseSettingsService
    {
        public const string StreamFpsKey =
            "Multiverse.Encoder.Fps";
        public const string StreamResolutionKey = "Leveling.Stream.Resolution";
        public const string StreamBitrateKbpsKey =
            "Multiverse.Encoder.BitrateKbps";
        public const string StreamPresetKey =
            "Multiverse.Encoder.Preset";
        public const string StreamUltraLowLatencyKey =
            "Multiverse.Encoder.Tuning";
        public const string StreamPipelineModeKey =
            "Multiverse.Pipeline.Mode";
        private const string LegacyStreamPipelineModeKey =
            "Multiverse.Encoder.PipelineMode";
        public const string MonitorDeviceNameKey =
            "Multiverse.Capture.Monitor";

        public const string LowMotionKey =
            "Multiverse.Ui.LowMotion";
        public const string AutoOpenClientWindowKey =
            "Multiverse.Client.AutoOpenWindow";
        public const string AutoStartHostStreamKey =
            "Multiverse.Host.AutoStartStream";
        public const string DiagnosticsLevelKey =
            "Multiverse.Diagnostics.Level";

        public const string LastRemoteTargetKey =
            "Multiverse.Recent.LastRemoteTarget";
        public const string LastRemoteNameKey =
            "Multiverse.Recent.LastRemoteName";

        public const string AudioOutputDeviceIdKey =
            "Multiverse.Audio.OutputDeviceId";
        public const string AudioOutputDeviceNameKey =
            "Multiverse.Audio.OutputDeviceName";
        public const string AudioClientVolumePercentKey =
            "Multiverse.Audio.ClientVolumePercent";
        public const string AudioCaptureModeKey =
            "Multiverse.Audio.CaptureMode";
        public const string AudioExclusionsKey =
            "Multiverse.Audio.Exclusions";
        public const string AudioManualProcessNameKey =
            "Multiverse.Audio.ManualProcessName";
        public const string TransmissionHudVisibleKey =
            "Multiverse.Client.TransmissionHudVisible";
        public const string HostTransmissionHudVisibleKey =
            "Multiverse.Host.TransmissionHudVisible";
        public const string MinimizeToTrayOnCloseKey =
            "Multiverse.Ui.MinimizeToTrayOnClose";
        public const string ControllerVisualSkinKey =
            "Multiverse.Gamepad.VisualSkin";

        public static MultiverseSettingsService Instance { get; } = new();

        public event EventHandler? TransmissionHudVisibilityChanged;
        public event EventHandler? HostTransmissionHudVisibilityChanged;

        private readonly ILevelingSettingsStore _local;
        public string StorageDescription => _local.Description;
        public string LastPersistenceError { get; private set; } = "";

        private MultiverseSettingsService()
        {
            StartupDiagnostics.Stage("Settings.SelectBackend");
            _local = LevelingSettingsStoreFactory.Create();
            StartupDiagnostics.Stage("Settings.BackendReady", _local.Description);
            try { SettingsMigration.MigratePipelineMode(_local); }
            catch (Exception ex) { ReportPersistenceError("migration", ex); }
        }

        private void ReportPersistenceError(string operation, Exception ex)
        {
            string error = operation + ": " + ex.GetType().Name +
                " (0x" + ex.HResult.ToString("X8") + ")";
            if (error == LastPersistenceError) return;
            LastPersistenceError = error;
            StartupDiagnostics.RecordException("SETTINGS_" + operation, ex);
        }

        internal bool TryGetRaw(string key, out object? value)
        {
            try { return _local.TryGetValue(key, out value); }
            catch (Exception ex)
            {
                ReportPersistenceError("READ", ex);
                value = null;
                return false;
            }
        }

        internal void Remove(string key)
        {
            try { _local.RemoveValue(key); }
            catch (Exception ex) { ReportPersistenceError("REMOVE", ex); }
        }

        public string GetString(string key, string fallback = "")
        {
            try
            {
                return TryGetRaw(key, out object? raw) && raw is not null
                    ? raw.ToString() ?? fallback
                    : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        public int GetInt(string key, int fallback)
        {
            string text = GetString(key, "");
            return int.TryParse(text, out int value) ? value : fallback;
        }

        public uint GetUInt(string key, uint fallback)
        {
            string text = GetString(key, "");
            return uint.TryParse(text, out uint value) ? value : fallback;
        }

        public bool GetBool(string key, bool fallback)
        {
            try
            {
                if (!TryGetRaw(key, out object? raw) || raw is null)
                    return fallback;

                if (raw is bool value)
                    return value;

                if (bool.TryParse(raw.ToString(), out bool parsed))
                    return parsed;

                if (int.TryParse(raw.ToString(), out int numeric))
                    return numeric != 0;
            }
            catch
            {
            }

            return fallback;
        }

        public void Set(string key, object? value)
        {
            try
            {
                if (value is null)
                    _local.RemoveValue(key);
                else
                    _local.SetValue(key, value);
            }
            catch (Exception ex)
            {
                // Keep the live session running, but make persistence failures visible.
                ReportPersistenceError("WRITE", ex);
            }
        }

        public bool GetTransmissionHudVisible() =>
            GetBool(TransmissionHudVisibleKey, true);

        public void SetTransmissionHudVisible(bool visible)
        {
            bool previous = GetTransmissionHudVisible();
            Set(TransmissionHudVisibleKey, visible);
            if (previous != visible)
                TransmissionHudVisibilityChanged?.Invoke(this, EventArgs.Empty);
        }

        public bool GetHostTransmissionHudVisible() =>
            GetBool(HostTransmissionHudVisibleKey, true);

        public void SetHostTransmissionHudVisible(bool visible)
        {
            bool previous = GetHostTransmissionHudVisible();
            Set(HostTransmissionHudVisibleKey, visible);
            if (previous != visible)
                HostTransmissionHudVisibilityChanged?.Invoke(this, EventArgs.Empty);
        }


        public ControllerVisualSkin GetControllerVisualSkin() =>
            string.Equals(
                GetString(ControllerVisualSkinKey, "Xbox"),
                "PlayStation5",
                StringComparison.OrdinalIgnoreCase)
                ? ControllerVisualSkin.PlayStation5
                : ControllerVisualSkin.Xbox;

        public void SetControllerVisualSkin(ControllerVisualSkin skin) =>
            Set(
                ControllerVisualSkinKey,
                skin == ControllerVisualSkin.PlayStation5
                    ? "PlayStation5"
                    : "Xbox");

        public bool GetMinimizeToTrayOnClose() =>
            GetBool(MinimizeToTrayOnCloseKey, true);

        public void SetMinimizeToTrayOnClose(bool enabled) =>
            Set(MinimizeToTrayOnCloseKey, enabled);

        public MultiverseStreamProfile GetStreamProfile()
        {
            uint fps = GetUInt(StreamFpsKey, 60);
            fps = fps is 30 or 60 or 80 or 90 or 120 ? fps : 60u;

            uint bitrateKbps = Math.Clamp(
                GetUInt(StreamBitrateKbpsKey, 20_000),
                5_000u,
                150_000u);

            uint preset = Math.Min(GetUInt(StreamPresetKey, 0), 7u);
            bool ultraLowLatency = GetBool(StreamUltraLowLatencyKey, true);
            uint pipelineMode = Math.Clamp(
                GetUInt(StreamPipelineModeKey, 10),
                1u,
                10u);

            return new MultiverseStreamProfile
            {
                Fps = fps,
                MaxWidth = GetString(StreamResolutionKey, "1080p") == "1440p" ? 2560u :
                    GetString(StreamResolutionKey, "1080p") == "720p" ? 1280u : 1920u,
                MaxHeight = GetString(StreamResolutionKey, "1080p") == "1440p" ? 1440u :
                    GetString(StreamResolutionKey, "1080p") == "720p" ? 720u : 1080u,
                BitrateKbps = bitrateKbps,
                Preset = preset,
                UltraLowLatency = ultraLowLatency,
                PipelineMode = pipelineMode,
                MonitorDeviceName = GetString(MonitorDeviceNameKey, "")
            };
        }

        public void SaveStreamResolution(string resolution) =>
            Set(StreamResolutionKey, resolution is "720p" or "1440p" ? resolution : "1080p");

        public void SaveStreamProfile(
            uint fps,
            uint bitrateKbps,
            uint preset,
            bool ultraLowLatency,
            uint? pipelineMode = null)
        {
            Set(StreamFpsKey, (fps is 30 or 60 or 80 or 90 or 120 ? fps : 60u).ToString());
            Set(
                StreamBitrateKbpsKey,
                Math.Clamp(bitrateKbps, 5_000u, 150_000u).ToString());
            Set(StreamPresetKey, Math.Min(preset, 7u).ToString());
            Set(StreamUltraLowLatencyKey, ultraLowLatency ? "1" : "0");

            if (pipelineMode.HasValue)
            {
                Set(
                    StreamPipelineModeKey,
                    Math.Clamp(pipelineMode.Value, 1u, 10u).ToString());
            }
        }

        public void SaveEncoderProfile(
            uint fps,
            uint bitrateKbps,
            uint preset,
            bool ultraLowLatency)
        {
            SaveStreamProfile(
                fps,
                bitrateKbps,
                preset,
                ultraLowLatency);
        }

        public MultiverseAppPreferences GetAppPreferences()
        {
            string diagnosticsLevel = GetString(DiagnosticsLevelKey, "Basic");
            diagnosticsLevel = diagnosticsLevel switch
            {
                "Advanced" => "Advanced",
                "Off" => "Off",
                _ => "Basic"
            };

            return new MultiverseAppPreferences
            {
                LowMotion = GetBool(LowMotionKey, false),
                AutoOpenClientWindow = GetBool(AutoOpenClientWindowKey, true),
                AutoStartHostStream = GetBool(AutoStartHostStreamKey, false),
                DiagnosticsLevel = diagnosticsLevel
            };
        }

        public void SaveAppPreferences(
            bool lowMotion,
            bool autoOpenClientWindow,
            bool autoStartHostStream,
            string diagnosticsLevel)
        {
            string normalizedDiagnostics = diagnosticsLevel switch
            {
                "Advanced" => "Advanced",
                "Off" => "Off",
                _ => "Basic"
            };

            Set(LowMotionKey, lowMotion);
            Set(AutoOpenClientWindowKey, autoOpenClientWindow);
            Set(AutoStartHostStreamKey, autoStartHostStream);
            Set(DiagnosticsLevelKey, normalizedDiagnostics);
        }

        /// <summary>
        /// Resets user-facing preferences only. Pairing/session security state is not touched.
        /// </summary>
        public void ResetUserPreferences()
        {
            string[] keys =
            {
                StreamFpsKey,
                  StreamResolutionKey,
                StreamBitrateKbpsKey,
                StreamPresetKey,
                StreamUltraLowLatencyKey,
                StreamPipelineModeKey,
                LegacyStreamPipelineModeKey,
                MonitorDeviceNameKey,
                LowMotionKey,
                AutoOpenClientWindowKey,
                AutoStartHostStreamKey,
                DiagnosticsLevelKey,
                AudioOutputDeviceIdKey,
                AudioOutputDeviceNameKey,
                AudioClientVolumePercentKey,
                AudioCaptureModeKey,
                AudioExclusionsKey,
                AudioManualProcessNameKey,
                TransmissionHudVisibleKey,
                HostTransmissionHudVisibleKey,
                MinimizeToTrayOnCloseKey
            };

            foreach (string key in keys)
                Set(key, null);
            TransmissionHudVisibilityChanged?.Invoke(this, EventArgs.Empty);
            HostTransmissionHudVisibilityChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SaveMonitor(string deviceName)
        {
            if (!string.IsNullOrWhiteSpace(deviceName))
                Set(MonitorDeviceNameKey, deviceName);
        }

        public void SaveRecentSession(string target, string remoteName)
        {
            if (!string.IsNullOrWhiteSpace(target))
                Set(LastRemoteTargetKey, target.Trim());
            if (!string.IsNullOrWhiteSpace(remoteName))
                Set(LastRemoteNameKey, remoteName.Trim());
        }

        public void SaveAudioClientPlayback(
            string deviceId,
            string deviceName,
            int volumePercent)
        {
            Set(AudioOutputDeviceIdKey, deviceId ?? "");
            Set(AudioOutputDeviceNameKey, deviceName ?? "Padrão do Windows");
            Set(
                AudioClientVolumePercentKey,
                Math.Clamp(volumePercent, 0, 200).ToString());
        }
    }
}
