namespace Multiverse.Services
{
    internal sealed class ControlDiagnosticsSnapshot
    {
        public bool Connected { get; init; }
        public int SessionGeneration { get; init; }
        public long ReadMessages { get; init; }
        public long WriteMessages { get; init; }
        public long ReadBytes { get; init; }
        public long WriteBytes { get; init; }
        public int CriticalQueueDepth { get; init; }
        public int TelemetryQueueDepth { get; init; }
        public int GamepadQueueDepth { get; init; }
        public int CursorQueueDepth { get; init; }
        public long LastReadAgeMs { get; init; }
        public long LastWriteAgeMs { get; init; }
        public long LastPongAgeMs { get; init; }
        public long ReadInProgressAgeMs { get; init; }
        public long WriteInProgressAgeMs { get; init; }
        public double LastWriteDurationMs { get; init; }
        public double MaxWriteDurationMs { get; init; }
    }
}
