using System;

namespace Multiverse.Services
{
    internal sealed class VideoDiagnosticProbe
    {
        internal long CollectedAt { get; init; }
        internal long RuntimeGeneration { get; init; }
        internal RemoteSessionSnapshot Session { get; init; } = null!;
        internal IVideoTransport? Transport { get; init; }
        internal NativeNvdecPlayer? Decoder { get; init; }
        internal TransportSnapshot? TransportStats { get; init; }
        internal CapturePipelineStats? CaptureStats { get; init; }
        internal EncoderStats? EncoderStats { get; init; }
        internal NativeNvdecSnapshot? DecoderStats { get; init; }
        internal VideoProgressDecision? Progress { get; init; }
    }
}
