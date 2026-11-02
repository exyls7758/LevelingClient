using System;
using System.Globalization;

namespace Multiverse.Services
{
    internal static class SettingsMigration
    {
        internal static void MigratePipelineMode(ILevelingSettingsStore store)
        {
            const string current = "Multiverse.Pipeline.Mode";
            const string legacy = "Multiverse.Encoder.PipelineMode";
            if (!store.TryGetValue(legacy, out object? raw)) return;
            if (!store.TryGetValue(current, out _))
            {
                if (!uint.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture),
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out uint mode))
                    return; // Preserve an unknown old value rather than destroying it.
                store.SetValue(current, Math.Clamp(mode, 1u, 10u).ToString(CultureInfo.InvariantCulture));
                // Some backends may report a write as successful without persisting it.
                // Never discard the legacy value until the replacement can be read.
                if (!store.TryGetValue(current, out object? saved) || saved is null)
                    return;
            }
            // Only remove the old key after the new value is safely present.
            store.RemoveValue(legacy);
        }
    }
}
