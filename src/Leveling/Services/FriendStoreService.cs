using Multiverse.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiverse.Services
{
    internal sealed class FriendStoreService
    {
        private static readonly Lazy<FriendStoreService> LazyInstance =
            new(() => new FriendStoreService());

        public static FriendStoreService Instance => LazyInstance.Value;

        private const string StoreV1Key = "Multiverse.Friends.v1";
        private const string StoreV2Key = "Multiverse.Friends.v2";
        private readonly object _sync = new();
        private readonly MultiverseSettingsService _settings = MultiverseSettingsService.Instance;
        private readonly List<MultiverseFriend> _friends;

        public event EventHandler? FriendsChanged;

        private FriendStoreService()
        {
            _friends = Load();
        }

        public IReadOnlyList<MultiverseFriend> GetFriends()
        {
            lock (_sync)
            {
                return _friends
                    .Select(Clone)
                    .OrderBy(f => f.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();
            }
        }

        public MultiverseFriend? FindByDeviceId(string deviceId)
        {
            lock (_sync)
            {
                MultiverseFriend? found = _friends.FirstOrDefault(
                    f => string.Equals(f.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
                return found is null ? null : Clone(found);
            }
        }

        public void Upsert(MultiverseFriend friend)
        {
            if (friend is null || string.IsNullOrWhiteSpace(friend.DeviceId) ||
                string.IsNullOrWhiteSpace(friend.PublicKeySpki))
            {
                return;
            }

            lock (_sync)
            {
                int index = _friends.FindIndex(
                    f => string.Equals(f.DeviceId, friend.DeviceId, StringComparison.OrdinalIgnoreCase));
                MultiverseFriend clean = Clone(friend);
                if (clean.CreatedAtUtc == default)
                    clean.CreatedAtUtc = DateTimeOffset.UtcNow;
                clean.LastSeenUtc = DateTimeOffset.UtcNow;
                if (index >= 0)
                {
                    MultiverseFriend existing = _friends[index];
                    clean.CreatedAtUtc = existing.CreatedAtUtc;
                    // Pairing/presence updates must never silently change permissions.
                    clean.AllowDirectConnection = existing.AllowDirectConnection;
                    clean.AutoAcceptConnection = existing.AutoAcceptConnection;
                    clean.AllowRemoteInput = existing.AllowRemoteInput;
                    clean.AllowRemoteAudio = existing.AllowRemoteAudio;
                    clean.Blocked = existing.Blocked;
                    _friends[index] = clean;
                }
                else
                {
                    clean.AllowDirectConnection = false;
                    clean.AutoAcceptConnection = false;
                    clean.AllowRemoteInput = true;
                    clean.AllowRemoteAudio = true;
                    clean.Blocked = false;
                    _friends.Add(clean);
                }
                SaveLocked();
            }

            FriendsChanged?.Invoke(this, EventArgs.Empty);
        }

        public bool UpdatePermissions(string deviceId, Action<MultiverseFriend> update)
        {
            if (string.IsNullOrWhiteSpace(deviceId) || update is null)
                return false;

            bool changed = false;
            lock (_sync)
            {
                MultiverseFriend? friend = _friends.FirstOrDefault(
                    f => string.Equals(f.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
                if (friend is null)
                    return false;

                string before = FriendStoreCodec.Encode(new[] { friend });
                update(friend);
                if (!friend.AllowDirectConnection)
                    friend.AutoAcceptConnection = false;
                if (friend.Blocked)
                {
                    friend.AllowDirectConnection = false;
                    friend.AutoAcceptConnection = false;
                }
                string after = FriendStoreCodec.Encode(new[] { friend });
                changed = !string.Equals(before, after, StringComparison.Ordinal);
                if (changed)
                    SaveLocked();
            }

            if (changed)
                FriendsChanged?.Invoke(this, EventArgs.Empty);
            return changed;
        }

        public void Remove(string deviceId)
        {
            bool changed;
            lock (_sync)
            {
                changed = _friends.RemoveAll(
                    f => string.Equals(f.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)) > 0;
                if (changed)
                    SaveLocked();
            }
            if (changed)
                FriendsChanged?.Invoke(this, EventArgs.Empty);
        }

        public void UpdatePresence(
            string deviceId,
            string ip,
            string dnsName,
            string stablePeerId)
        {
            lock (_sync)
            {
                MultiverseFriend? friend = _friends.FirstOrDefault(
                    f => string.Equals(f.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
                if (friend is null)
                    return;

                bool changed = false;
                if (!string.IsNullOrWhiteSpace(ip) && !string.Equals(friend.LastKnownIp, ip, StringComparison.OrdinalIgnoreCase))
                { friend.LastKnownIp = ip; changed = true; }
                if (!string.IsNullOrWhiteSpace(dnsName) && !string.Equals(friend.TailscaleDnsName, dnsName, StringComparison.OrdinalIgnoreCase))
                { friend.TailscaleDnsName = dnsName; changed = true; }
                if (!string.IsNullOrWhiteSpace(stablePeerId) && !string.Equals(friend.StablePeerId, stablePeerId, StringComparison.OrdinalIgnoreCase))
                { friend.StablePeerId = stablePeerId; changed = true; }

                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (now - friend.LastSeenUtc >= TimeSpan.FromMinutes(1))
                { friend.LastSeenUtc = now; changed = true; }

                if (changed)
                    SaveLocked();
            }
        }

        private List<MultiverseFriend> Load()
        {
            try
            {
                string v2 = _settings.GetString(StoreV2Key, "");
                if (!string.IsNullOrWhiteSpace(v2))
                    return FriendStoreCodec.Decode(v2).Select(Clone).ToList();

                string v1 = _settings.GetString(StoreV1Key, "");
                if (string.IsNullOrWhiteSpace(v1))
                    return new List<MultiverseFriend>();

                List<MultiverseFriend> migrated = FriendStoreCodec.Decode(v1).Select(Clone).ToList();
                if (migrated.Count > 0)
                {
                    // Write v2 first. The old blob remains untouched as a rollback copy.
                    _settings.Set(StoreV2Key, FriendStoreCodec.Encode(migrated));
                }
                return migrated;
            }
            catch
            {
                // Trust persistence must never break streaming.
                return new List<MultiverseFriend>();
            }
        }

        private void SaveLocked()
        {
            try
            {
                _settings.Set(StoreV2Key, FriendStoreCodec.Encode(_friends.Select(Clone)));
            }
            catch
            {
                // Trust persistence must never be allowed to break a session.
            }
        }

        internal static MultiverseFriend Clone(MultiverseFriend source) =>
            new()
            {
                DeviceId = source.DeviceId,
                DisplayName = source.DisplayName,
                PublicKeySpki = source.PublicKeySpki,
                StablePeerId = source.StablePeerId,
                TailscaleDnsName = source.TailscaleDnsName,
                LastKnownIp = source.LastKnownIp,
                CreatedAtUtc = source.CreatedAtUtc,
                LastSeenUtc = source.LastSeenUtc,
                AllowDirectConnection = source.AllowDirectConnection,
                AutoAcceptConnection = source.AutoAcceptConnection,
                AllowRemoteInput = source.AllowRemoteInput,
                AllowRemoteAudio = source.AllowRemoteAudio,
                Blocked = source.Blocked,
                PresenceText = source.PresenceText,
                Online = source.Online,
                RouteText = source.RouteText
            };
    }
}
