using Multiverse.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Multiverse.Services
{
    internal sealed class TailscalePeerDiscoveryResult
    {
        public bool Available { get; init; }
        public string Status { get; init; } = "";
        public IReadOnlyList<TailscalePeerInfo> Peers { get; init; } = Array.Empty<TailscalePeerInfo>();
    }

    internal sealed class TailscalePeerDiscoveryService
    {
        private static readonly Lazy<TailscalePeerDiscoveryService> LazyInstance =
            new(() => new TailscalePeerDiscoveryService());

        public static TailscalePeerDiscoveryService Instance => LazyInstance.Value;

        private TailscalePeerDiscoveryService() { }

        public async Task<TailscalePeerDiscoveryResult> RefreshAsync(
            CancellationToken cancellationToken = default)
        {
            string executable = ResolveExecutable();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = "status --json",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using Process process = new() { StartInfo = psi };
                if (!process.Start())
                    return Unavailable("Tailscale CLI não pôde ser iniciado.");

                Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                Task<string> errorTask = process.StandardError.ReadToEndAsync();
                using CancellationTokenSource timeout =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));

                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    try
                    {
                        if (!process.HasExited)
                            process.Kill(entireProcessTree: true);
                    }
                    catch { }
                    if (cancellationToken.IsCancellationRequested)
                        throw;
                    return Unavailable("Tailscale demorou demais para responder.");
                }

                string json = await outputTask;
                string stderr = await errorTask;
                if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
                    return Unavailable(string.IsNullOrWhiteSpace(stderr)
                        ? "Tailscale não retornou o status da tailnet."
                        : stderr.Trim());

                return Parse(json);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Unavailable($"Tailscale indisponível: {ex.Message}");
            }
        }

        internal static TailscalePeerDiscoveryResult Parse(string json)
        {
            var peers = new List<TailscalePeerInfo>();
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                string backend = GetString(root, "BackendState", "Unknown");
                if (backend != "Running")
                    return Unavailable(backend == "NeedsLogin"
                        ? "Entre no Tailscale para conectar computadores pela internet."
                        : $"Tailscale não está conectado ({backend}).");
                if (root.TryGetProperty("Peer", out JsonElement peerRoot) &&
                    peerRoot.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty property in peerRoot.EnumerateObject())
                    {
                        JsonElement peer = property.Value;
                        string ip = FirstTailscaleIpv4(peer);
                        if (string.IsNullOrWhiteSpace(ip))
                            continue;

                        peers.Add(new TailscalePeerInfo
                        {
                            StablePeerId = GetString(peer, "ID", property.Name),
                            HostName = GetString(peer, "HostName", ""),
                            DnsName = GetString(peer, "DNSName", ""),
                            TailscaleIp = ip,
                            Online = GetBool(peer, "Online"),
                            Active = GetBool(peer, "Active"),
                            CurrentEndpoint = GetString(peer, "CurAddr", ""),
                            Relay = GetString(peer, "Relay", "")
                        });
                    }
                }

                return new TailscalePeerDiscoveryResult
                {
                    Available = true,
                    Status = peers.Count == 0
                        ? "Tailscale online • nenhum peer visível"
                        : $"Tailscale online • {peers.Count} peer(s) visível(is)",
                    Peers = peers
                        .OrderByDescending(p => p.Online)
                        .ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                        .ToArray()
                };
            }
            catch (Exception ex)
            {
                return Unavailable($"Status Tailscale inválido: {ex.Message}");
            }
        }

        private static string ResolveExecutable()
        {
            string common = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Tailscale",
                "tailscale.exe");
            return File.Exists(common) ? common : "tailscale.exe";
        }

        private static string FirstTailscaleIpv4(JsonElement peer)
        {
            if (!peer.TryGetProperty("TailscaleIPs", out JsonElement ips) ||
                ips.ValueKind != JsonValueKind.Array)
                return "";

            foreach (JsonElement item in ips.EnumerateArray())
            {
                string value = item.GetString() ?? "";
                if (IPAddress.TryParse(value, out IPAddress? address) &&
                    address.AddressFamily == AddressFamily.InterNetwork &&
                    address.GetAddressBytes() is var bytes && bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
                    return value;
            }
            return "";
        }

        private static string GetString(JsonElement obj, string name, string fallback)
        {
            if (obj.TryGetProperty(name, out JsonElement value) &&
                value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? fallback;
            return fallback;
        }

        private static bool GetBool(JsonElement obj, string name)
        {
            if (obj.TryGetProperty(name, out JsonElement value) &&
                (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False))
                return value.GetBoolean();
            return false;
        }

        private static TailscalePeerDiscoveryResult Unavailable(string message) =>
            new()
            {
                Available = false,
                Status = message,
                Peers = Array.Empty<TailscalePeerInfo>()
            };
    }
}
