using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Storage;

namespace Multiverse.Services
{
    internal sealed class PackagedSettingsStore : ILevelingSettingsStore
    {
        private readonly ApplicationDataContainer _container =
            ApplicationData.Current.LocalSettings;
        public string Description => "Windows.ApplicationData.LocalSettings (pacote atual)";
        public bool TryGetValue(string key, out object? value) => _container.Values.TryGetValue(key, out value);
        public void SetValue(string key, object value) => _container.Values[key] = value;
        public void RemoveValue(string key) => _container.Values.Remove(key);
    }

    internal static class LevelingSettingsStoreFactory
    {
        private const int AppModelErrorNoPackage = 15700;
        private const int ErrorInsufficientBuffer = 122;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int GetCurrentPackageFullName(ref uint length, StringBuilder? name);

        internal static bool HasPackageIdentity()
        {
            uint length = 0;
            int result = GetCurrentPackageFullName(ref length, null);
            if (result == AppModelErrorNoPackage) return false;
            if (result == 0 || result == ErrorInsufficientBuffer) return true;
            throw new Win32Exception(result, "Não foi possível determinar a identidade do pacote.");
        }

        internal static ILevelingSettingsStore Create() => SettingsStoreSelector.Select(
            HasPackageIdentity(),
            () => new PackagedSettingsStore(),
            () => new RegistrySettingsStore());
    }
}
