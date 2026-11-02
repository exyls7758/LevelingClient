using Multiverse.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Multiverse.Services
{
    internal enum FriendPresenceRefreshMode
    {
        Home,
        Friends
    }

    internal sealed class FriendPresenceSnapshot
    {
        public string Status { get; init; } = "Tailscale não consultado";
        public IReadOnlyList<MultiverseFriend> Friends { get; init; } = Array.Empty<MultiverseFriend>();
        public IReadOnlyList<TailscalePeerInfo> Peers { get; init; } = Array.Empty<TailscalePeerInfo>();
    }

    internal sealed class FriendPresenceService : IDisposable
    {
        private static readonly Lazy<FriendPresenceService> LazyInstance =
            new(() => new FriendPresenceService());
        public static FriendPresenceService Instance => LazyInstance.Value;

        private readonly object _sync = new();
        private readonly TailscalePeerDiscoveryService _discovery = TailscalePeerDiscoveryService.Instance;
        private readonly FriendStoreService _store = FriendStoreService.Instance;
        private FriendPresenceSnapshot _snapshot = new();
        private CancellationTokenSource? _loopCancel;
        private Task? _loopTask;
        private int _homeConsumers;
        private int _friendsConsumers;
        private bool _refreshing;
        private bool _disposed;

        private FriendPresenceService()
        {
            _store.FriendsChanged += Store_FriendsChanged;
        }

        public event EventHandler? PresenceChanged;

        public FriendPresenceSnapshot GetSnapshot()
        {
            lock (_sync)
            {
                return new FriendPresenceSnapshot
                {
                    Status = _snapshot.Status,
                    Friends = _snapshot.Friends.Select(FriendStoreService.Clone).ToArray(),
                    Peers = _snapshot.Peers.ToArray()
                };
            }
        }

        private static readonly TimeSpan FriendsRefreshInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan HomeRefreshInterval = TimeSpan.FromSeconds(15);

        public void AddConsumer(FriendPresenceRefreshMode mode)
        {
            lock (_sync)
            {
                if (mode == FriendPresenceRefreshMode.Friends)
                    _friendsConsumers++;
                else
                    _homeConsumers++;

                if (_loopCancel is not null)
                    return;

                _loopCancel = new CancellationTokenSource();
                _loopTask = Task.Run(() => LoopAsync(_loopCancel.Token));
            }
        }

        public void RemoveConsumer(FriendPresenceRefreshMode mode)
        {
            CancellationTokenSource? cancel = null;
            lock (_sync)
            {
                if (mode == FriendPresenceRefreshMode.Friends)
                    _friendsConsumers = Math.Max(0, _friendsConsumers - 1);
                else
                    _homeConsumers = Math.Max(0, _homeConsumers - 1);

                if (_homeConsumers == 0 && _friendsConsumers == 0)
                {
                    cancel = _loopCancel;
                    _loopCancel = null;
                    _loopTask = null;
                }
            }
            try { cancel?.Cancel(); cancel?.Dispose(); } catch { }
        }

        private bool HasFriendsConsumer()
        {
            lock (_sync)
                return _friendsConsumers > 0;
        }

        private async Task LoopAsync(CancellationToken token)
        {
            try
            {
                int homeTicks = 0;
                int homeRefreshEveryTicks = Math.Max(1,
                    (int)(HomeRefreshInterval.TotalSeconds / FriendsRefreshInterval.TotalSeconds));
                using PeriodicTimer timer = new(FriendsRefreshInterval);
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    if (HasFriendsConsumer())
                    {
                        homeTicks = 0;
                        await RefreshAsync(token).ConfigureAwait(false);
                        continue;
                    }

                    homeTicks++;
                    if (homeTicks < homeRefreshEveryTicks)
                        continue;

                    homeTicks = 0;
                    await RefreshAsync(token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        }

        public async Task<FriendPresenceSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                if (_refreshing)
                    return GetSnapshot();
                _refreshing = true;
            }

            try
            {
                TailscalePeerDiscoveryResult result = await _discovery.RefreshAsync(cancellationToken)
                    .ConfigureAwait(false);
                List<MultiverseFriend> trusted = _store.GetFriends().ToList();
                foreach (MultiverseFriend friend in trusted)
                {
                    TailscalePeerInfo? peer = result.Peers.FirstOrDefault(p => PeerMatches(friend, p));
                    friend.Online = peer?.Online == true;
                    friend.PresenceText = peer is null
                        ? (string.IsNullOrWhiteSpace(friend.LastKnownIp) ? "Offline" : $"Offline • {friend.LastKnownIp}")
                        : $"{(peer.Online ? "Online" : "Offline")} • {peer.TailscaleIp}";
                    friend.RouteText = peer?.RouteText ?? "Tailscale • rota desconhecida";
                    if (peer is not null)
                    {
                        friend.LastKnownIp = peer.TailscaleIp;
                        friend.StablePeerId = peer.StablePeerId;
                        friend.TailscaleDnsName = peer.DnsName;
                        _store.UpdatePresence(friend.DeviceId, peer.TailscaleIp, peer.DnsName, peer.StablePeerId);
                    }
                }

                var snapshot = new FriendPresenceSnapshot
                {
                    Status = result.Status,
                    Friends = trusted
                        .OrderByDescending(x => x.Online)
                        .ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                        .ToArray(),
                    Peers = result.Peers.ToArray()
                };
                lock (_sync)
                    _snapshot = snapshot;
                RaiseChanged();
                return GetSnapshot();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    _snapshot = new FriendPresenceSnapshot
                    {
                        Status = $"Tailscale indisponível: {ex.Message}",
                        Friends = _store.GetFriends(),
                        Peers = _snapshot.Peers
                    };
                }
                RaiseChanged();
                return GetSnapshot();
            }
            finally
            {
                lock (_sync) _refreshing = false;
            }
        }

        internal static bool PeerMatches(MultiverseFriend friend, TailscalePeerInfo peer) =>
            (!string.IsNullOrWhiteSpace(friend.StablePeerId) &&
             string.Equals(friend.StablePeerId, peer.StablePeerId, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(friend.TailscaleDnsName) &&
             string.Equals(friend.TailscaleDnsName.TrimEnd('.'), peer.DnsName.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(friend.LastKnownIp) &&
             string.Equals(friend.LastKnownIp, peer.TailscaleIp, StringComparison.OrdinalIgnoreCase));

        private void Store_FriendsChanged(object? sender, EventArgs e)
        {
            _ = Task.Run(async () =>
            {
                try { await RefreshAsync().ConfigureAwait(false); } catch { }
            });
        }

        private void RaiseChanged()
        {
            try { PresenceChanged?.Invoke(this, EventArgs.Empty); } catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _store.FriendsChanged -= Store_FriendsChanged;
            CancellationTokenSource? cancel;
            lock (_sync)
            {
                cancel = _loopCancel;
                _loopCancel = null;
                _homeConsumers = 0;
                _friendsConsumers = 0;
            }
            try { cancel?.Cancel(); cancel?.Dispose(); } catch { }
        }
    }
}
