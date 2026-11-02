using System.Reflection;

namespace Multiverse.Services
{
    internal static class BuildInfo
    {
        internal static string DisplayVersion { get; } = ResolveVersion();
        internal static string ApplicationLabel => "Leveling • " + DisplayVersion;

        private static string ResolveVersion()
        {
            Assembly assembly = typeof(BuildInfo).Assembly;
            string? informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
                return informational.Trim();
            return assembly.GetName().Version?.ToString() ?? "desconhecida";
        }
    }
}
