using System;
namespace Multiverse;
internal static class Program {
    internal static bool Updated { get; private set; }
    [STAThread]
    static void Main(string[] args) {
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).OnRestarted(_ => Updated = true).Run();
        XamlGeneratedProgram.XamlGeneratedMain();
    }
}
