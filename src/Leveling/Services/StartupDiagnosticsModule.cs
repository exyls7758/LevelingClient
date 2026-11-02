using System.Reflection;
using System.Runtime.CompilerServices;

namespace Multiverse.Services
{
    internal static class StartupDiagnosticsModule
    {
        [ModuleInitializer]
        internal static void Initialize()
        {
            // Use the same assembly identity as the visible version footer.
            // The logger must not depend on WinUI or a manually edited label.
            var assembly = typeof(StartupDiagnosticsModule).Assembly;
            string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "unknown";
            StartupDiagnostics.Initialize(version);
        }
    }
}
