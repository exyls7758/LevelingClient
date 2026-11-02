namespace Multiverse.Services;

internal static class VideoGenerationIdentity
{
    internal static bool MatchesTelemetry(int session, long stream, int decoderSession, long decoderStream) =>
        session > 0 && stream > 0 && session == decoderSession && stream == decoderStream;
    // Zero is only used by local diagnostic loopback. Remote protocol 20
    // requires a nonzero identity negotiated over the authenticated channel.
    internal static bool AcceptWireStream(uint expected, uint received) => expected == 0 || expected == received;
}
