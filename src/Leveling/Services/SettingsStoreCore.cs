using System;

namespace Multiverse.Services
{
    internal interface ILevelingSettingsStore
    {
        string Description { get; }
        bool TryGetValue(string key, out object? value);
        void SetValue(string key, object value);
        void RemoveValue(string key);
    }

    internal static class SettingsStoreSelector
    {
        internal static ILevelingSettingsStore Select(
            bool hasPackageIdentity,
            Func<ILevelingSettingsStore> packaged,
            Func<ILevelingSettingsStore> portable) =>
            hasPackageIdentity ? packaged() : portable();
    }
}
