namespace Multiverse.Models
{
    internal sealed class TailscalePeerInfo
    {
        public string StablePeerId { get; init; } = "";
        public string HostName { get; init; } = "";
        public string DnsName { get; init; } = "";
        public string TailscaleIp { get; init; } = "";
        public bool Online { get; init; }
        public bool Active { get; init; }
        public string Relay { get; init; } = "";
        public string CurrentEndpoint { get; init; } = "";

        public string DisplayName =>
            string.IsNullOrWhiteSpace(HostName)
                ? (string.IsNullOrWhiteSpace(DnsName) ? TailscaleIp : DnsName.TrimEnd('.'))
                : HostName;

        public string RouteText =>
            !Online ? "Offline" : !Active ? "Rota ainda não confirmada"
                : !string.IsNullOrWhiteSpace(CurrentEndpoint) ? "Conexão direta • P2P"
                : !string.IsNullOrWhiteSpace(Relay) ? $"Relay • {Relay} • conexão intermediada"
                : "Rota ainda não confirmada";
    }
}
