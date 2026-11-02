using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Multiverse.Services
{
    internal sealed class TailscaleNetworkSnapshot
    {
        public string CurrentTailscaleIp { get; init; } = "";
        public string InterfaceName { get; init; } = "";
        public bool Available { get; init; }
        public long Generation { get; init; }
    }

    /// <summary>
    /// CORE 11 network identity tracker.
    /// Tailscale IPv4 lives in 100.64.0.0/10. The service never caches an
    /// address across a NetworkAddressChanged event: each change produces a
    /// fresh generation and invalidates the previous UI/direct-endpoint view.
    /// </summary>
    internal sealed class TailscaleNetworkService : IDisposable
    {
        private static readonly Lazy<TailscaleNetworkService> LazyInstance =
            new(() => new TailscaleNetworkService());

        public static TailscaleNetworkService Instance => LazyInstance.Value;

        private readonly object _sync = new();
        private Timer? _refreshTimer;
        private string _currentIp = "";
        private string _interfaceName = "";
        private long _generation;
        private bool _disposed;

        public event EventHandler? NetworkChanged;

        private TailscaleNetworkService()
        {
            RefreshNow();
            NetworkChange.NetworkAddressChanged += NetworkAddressChanged;
            NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
        }

        public TailscaleNetworkSnapshot GetSnapshot()
        {
            lock (_sync)
            {
                return new TailscaleNetworkSnapshot
                {
                    CurrentTailscaleIp = _currentIp,
                    InterfaceName = _interfaceName,
                    Available = _currentIp.Length > 0,
                    Generation = _generation
                };
            }
        }

        private void NetworkAddressChanged(object? sender, EventArgs e) =>
            ScheduleRefresh();

        private void NetworkAvailabilityChanged(
            object? sender,
            NetworkAvailabilityEventArgs e) =>
            ScheduleRefresh();

        private void ScheduleRefresh()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;

                _refreshTimer ??= new Timer(
                    _ => RefreshNow(forceGeneration: true),
                    null,
                    Timeout.Infinite,
                    Timeout.Infinite);

                _refreshTimer.Change(250, Timeout.Infinite);
            }
        }

        public void RefreshNow(bool forceGeneration = false)
        {
            string nextIp = "";
            string nextInterface = "";
            int bestScore = int.MinValue;

            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }

                    int nicScore =
                        nic.Name.Contains("tailscale", StringComparison.OrdinalIgnoreCase) ||
                        nic.Description.Contains("tailscale", StringComparison.OrdinalIgnoreCase)
                            ? 100
                            : 0;

                    foreach (UnicastIPAddressInformation unicast in
                             nic.GetIPProperties().UnicastAddresses)
                    {
                        IPAddress address = unicast.Address;
                        if (address.AddressFamily != AddressFamily.InterNetwork ||
                            !IsTailscaleIpv4(address))
                        {
                            continue;
                        }

                        int score = 1000 + nicScore;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            nextIp = address.ToString();
                            nextInterface = nic.Name;
                        }
                    }
                }
            }
            catch
            {
                // Network changes can race adapter teardown. The next event or
                // periodic UI refresh will try again; stale data is not kept.
            }

            bool changed;
            lock (_sync)
            {
                if (_disposed)
                    return;

                bool identityChanged =
                    !string.Equals(_currentIp, nextIp, StringComparison.Ordinal) ||
                    !string.Equals(_interfaceName, nextInterface, StringComparison.Ordinal);

                // A physical Ethernet/Wi-Fi/VPN route can change while the
                // Tailscale virtual IP remains identical. Treat every debounced
                // NetworkChange notification as a new routing generation so
                // peer resolution never trusts the previous route implicitly.
                changed = forceGeneration || identityChanged;

                if (changed)
                {
                    _currentIp = nextIp;
                    _interfaceName = nextInterface;
                    _generation++;
                }
            }

            if (changed)
                NetworkChanged?.Invoke(this, EventArgs.Empty);
        }

        public async Task<IPAddress?> ResolvePeerAsync(
            string host,
            int port,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(host))
                return null;

            IPAddress[] addresses;
            if (IPAddress.TryParse(host.Trim(), out IPAddress? literal))
            {
                addresses = new[] { literal };
            }
            else
            {
                addresses = await Dns.GetHostAddressesAsync(host.Trim(), cancellationToken);
            }

            List<IPAddress> ipv4 = addresses
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Distinct()
                .OrderByDescending(a => IsTailscaleIpv4(a))
                .ToList();

            foreach (IPAddress candidate in ipv4)
            {
                if (IsRouteable(candidate, port, preferTailscale: IsTailscaleIpv4(candidate)))
                    return candidate;
            }

            // No candidate currently has a valid route. Returning the first
            // DNS answer here would resurrect an invalidated/stale endpoint.
            return null;
        }

        private static bool IsRouteable(
            IPAddress candidate,
            int port,
            bool preferTailscale)
        {
            try
            {
                using Socket socket = new(
                    AddressFamily.InterNetwork,
                    SocketType.Dgram,
                    ProtocolType.Udp);

                socket.Connect(new IPEndPoint(candidate, port));
                if (socket.LocalEndPoint is not IPEndPoint local)
                    return false;

                return !preferTailscale || IsTailscaleIpv4(local.Address);
            }
            catch
            {
                return false;
            }
        }

        public static bool IsTailscaleIpv4(IPAddress address)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork)
                return false;

            byte[] b = address.GetAddressBytes();
            return b.Length == 4 &&
                   b[0] == 100 &&
                   b[1] >= 64 && b[1] <= 127;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;
            }

            NetworkChange.NetworkAddressChanged -= NetworkAddressChanged;
            NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
            _refreshTimer?.Dispose();
            _refreshTimer = null;
        }
    }
}
