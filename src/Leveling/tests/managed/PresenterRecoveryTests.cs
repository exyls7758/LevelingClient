using Multiverse.Services;
internal static class PresenterRecoveryTests
{
    public static void Run()
    {
        static void Check(bool ok) { if (!ok) throw new Exception("Presenter recovery policy regression"); }
        var p = new PresenterRecoveryPolicy();
        Check(!p.Observe(0, 0, 0, false));
        Check(!p.Observe(5000, 0, 0, false)); // no video is not display failure
        Check(!p.Observe(5100, 100, 0, false)); // grace after first picture
        Check(p.Observe(9000, 200, 0, false));
        Check(!p.Observe(10000, 250, 0, false)); // cooldown
        Check(!p.Observe(20000, 300, 0, true)); // do not join a blocked Present on UI
        Check(p.Observe(21000, 400, 0, false));
        Check(p.Observe(32000, 500, 0, false));
        Check(!p.Observe(40000, 600, 0, false)); // bounded
        p.Reset(); Check(!p.Observe(50000, 600, 0, false));
        for (int i=1;i<=30;i++) Check(!p.Observe(50000+i*1000, (ulong)(600+i), (ulong)i, false));
        Check(p.Observe(84000, 640, 30, false));
        Console.WriteLine("PASS presenter stall, idle, in-flight, cooldown, budget and session reset");
    }
}
