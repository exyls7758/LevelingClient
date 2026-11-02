using System;

namespace Multiverse.Models
{
    internal sealed class DirectSessionRequest
    {
        public string RequestId { get; init; } = "";
        public string DeviceId { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string RemoteAddress { get; init; } = "";
        public DateTimeOffset ReceivedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    }
}
