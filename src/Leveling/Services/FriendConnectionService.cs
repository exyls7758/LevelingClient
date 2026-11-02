using Multiverse.Models;
using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Multiverse.Services
{
    /// <summary>
    /// Friend trust/orchestration only. Video/audio/input remain owned by their
    /// existing subsystems. Signed authentication is performed before the
    /// existing RemoteSessionService session is activated.
    /// </summary>
    internal sealed class FriendConnectionService
    {
        private static readonly Lazy<FriendConnectionService> LazyInstance =
            new(() => new FriendConnectionService());

        public static FriendConnectionService Instance => LazyInstance.Value;

        private readonly MultiverseIdentityService _identity = MultiverseIdentityService.Instance;
        private readonly FriendStoreService _store = FriendStoreService.Instance;
        private readonly TailscalePeerDiscoveryService _discovery = TailscalePeerDiscoveryService.Instance;
        private readonly RemoteSessionService _session = RemoteSessionService.Instance;

        private FriendConnectionService() { }

        public string LocalDeviceId => _identity.DeviceId;
        public string LocalDisplayName => _identity.DisplayName;
        public string LocalPublicKey => _identity.PublicKeySpki;

        public Task<TailscalePeerDiscoveryResult> RefreshPeersAsync(
            CancellationToken cancellationToken = default) =>
            _discovery.RefreshAsync(cancellationToken);

        public Task<RemoteConnectResult> AddFriendAsync(
            TailscalePeerInfo peer,
            CancellationToken cancellationToken = default) =>
            _session.PairFriendAsync(peer, cancellationToken);

        public Task<RemoteConnectResult> ConnectAsync(
            MultiverseFriend friend,
            string currentTailscaleIp,
            CancellationToken cancellationToken = default) =>
            _session.ConnectFriendAsync(friend, currentTailscaleIp, cancellationToken);

        public void RemoveFriend(string deviceId) => _store.Remove(deviceId);

        public static string CreateNonce()
        {
            byte[] bytes = RandomNumberGenerator.GetBytes(32);
            return Convert.ToBase64String(bytes);
        }

        public static string BuildTranscript(
            string initiatorDeviceId,
            string responderDeviceId,
            string initiatorNonce,
            string responderNonce)
        {
            static string Clean(string value) => (value ?? "").Trim();
            return string.Join(
                "|",
                "MULTIVERSE-FRIEND-AUTH-v1",
                Clean(initiatorDeviceId),
                Clean(responderDeviceId),
                Clean(initiatorNonce),
                Clean(responderNonce));
        }

        public string SignTranscript(string transcript) => _identity.Sign(transcript);

        public static bool VerifyTranscript(
            string publicKeySpki,
            string transcript,
            string signature) =>
            MultiverseIdentityService.Verify(publicKeySpki, transcript, signature);
    }
}
