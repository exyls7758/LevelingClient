using System;

namespace Multiverse.Services
{
    internal readonly struct VideoSenderNetworkFeedback
    {
        public bool Connected { get; init; }
        public double RoundTripMs { get; init; }
        public double BaseRoundTripMs { get; init; }
        public double JitterMs { get; init; }
        public double PacketLossPercent { get; init; }
        public double RemoteRxMbps { get; init; }
        public long FeedbackAgeMs { get; init; }
        public double RemoteVideoFps { get; init; }
        public int RemotePendingFrames { get; init; }
        public long RemoteDecodeDrops { get; init; }
    }

    // Receiver feedback is measured by the session owner, not inferred from
    // TCP connectivity. Unknown/expired samples must never authorize probing.
    internal static class VideoFeedbackFreshness
    {
        internal const long MaxAgeMs = 2000;

        internal static bool IsFresh(VideoSenderNetworkFeedback feedback) =>
            feedback.Connected && feedback.FeedbackAgeMs >= 0 &&
            feedback.FeedbackAgeMs <= MaxAgeMs &&
            double.IsFinite(feedback.RoundTripMs) &&
            feedback.RoundTripMs > 0;
    }

}
