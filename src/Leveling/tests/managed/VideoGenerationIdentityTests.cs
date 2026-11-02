using Multiverse.Services;
internal static class VideoGenerationIdentityTests
{
    internal static void Run()
    {
        static void Check(bool v) { if (!v) throw new Exception("generation identity violation"); }
        Check(!VideoGenerationIdentity.MatchesTelemetry(1, 2, 1, 1));
        Check(!VideoGenerationIdentity.MatchesTelemetry(2, 1, 1, 1));
        Check(VideoGenerationIdentity.MatchesTelemetry(2, 3, 2, 3));
        Check(!VideoGenerationIdentity.AcceptWireStream(45, 44));
        Check(!VideoGenerationIdentity.AcceptWireStream(45, 0));
        Check(VideoGenerationIdentity.AcceptWireStream(45, 45));
        Check(VideoGenerationIdentity.AcceptWireStream(0, 77)); // diagnostic loopback
        Console.WriteLine("feedback and UDP generation binding: PASS");
    }
}
