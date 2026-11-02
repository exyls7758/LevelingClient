using System;

namespace Multiverse.Models
{
    internal sealed class MultiverseFriend
    {
        public string DeviceId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string PublicKeySpki { get; set; } = "";
        public string StablePeerId { get; set; } = "";
        public string TailscaleDnsName { get; set; } = "";
        public string LastKnownIp { get; set; } = "";
        public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;

        public bool AllowDirectConnection { get; set; }
        public bool AutoAcceptConnection { get; set; }
        public bool AllowRemoteInput { get; set; } = true;
        public bool AllowRemoteAudio { get; set; } = true;
        public bool Blocked { get; set; }

        public string PresenceText { get; set; } = "Offline";
        public bool Online { get; set; }
        public string RouteText { get; set; } = "Tailscale";
    }
}
