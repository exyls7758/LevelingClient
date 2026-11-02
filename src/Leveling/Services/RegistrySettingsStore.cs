using Microsoft.Win32;
using System;
using System.Globalization;
using System.IO;

namespace Multiverse.Services
{
    /// <summary>Per-user settings for the unpackaged desktop application.</summary>
    internal sealed class RegistrySettingsStore : ILevelingSettingsStore
    {
        internal const string DefaultKey = @"Software\Leveling\Settings";
        private readonly string _key;
        internal RegistrySettingsStore(string key = DefaultKey) => _key = key;
        public string Description => @"HKCU\" + _key;

        public bool TryGetValue(string key, out object? value)
        {
            using RegistryKey? store = Registry.CurrentUser.OpenSubKey(_key, false);
            value = store?.GetValue(key);
            return value is not null;
        }

        public void SetValue(string key, object value)
        {
            string text = value switch
            {
                string s => s,
                bool b => b ? "True" : "False",
                int or uint or long or ulong or short or ushort or byte or sbyte =>
                    Convert.ToString(value, CultureInfo.InvariantCulture)!,
                _ => throw new ArgumentException("Tipo de configuração não suportado.", nameof(value))
            };
            using RegistryKey store = Registry.CurrentUser.CreateSubKey(_key, true)
                ?? throw new IOException("Não foi possível abrir o armazenamento de configurações.");
            store.SetValue(key, text, RegistryValueKind.String);
        }

        public void RemoveValue(string key)
        {
            using RegistryKey? store = Registry.CurrentUser.OpenSubKey(_key, true);
            store?.DeleteValue(key, false);
        }
    }
}
