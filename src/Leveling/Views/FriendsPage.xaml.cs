using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Multiverse.Models;
using Multiverse.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Multiverse.Views
{
    public sealed partial class FriendsPage : Page
    {
        private readonly FriendConnectionService _friends = FriendConnectionService.Instance;
        private readonly FriendStoreService _store = FriendStoreService.Instance;
        private readonly FriendPresenceService _presence = FriendPresenceService.Instance;
        private readonly RemoteSessionService _session = RemoteSessionService.Instance;
        private RemoteStreamingRuntime Stream => RemoteStreamingRuntime.Instance;
        private bool _refreshing;
        private bool _friendMenuOpen;
        private bool _presenceRenderPending;
        private bool _loaded;

        public FriendsPage()
        {
            InitializeComponent();
            _session.Start();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            _loaded = true;
            FriendRequestPromptCoordinator.Instance.Attach(this);
            _store.FriendsChanged += Store_FriendsChanged;
            _presence.PresenceChanged += Presence_PresenceChanged;
            _session.FriendRequestReceived += Session_FriendRequestReceived;
            _presence.AddConsumer(FriendPresenceRefreshMode.Friends);
            LocalIdentityTextBlock.Text = $"ID • {_friends.LocalDeviceId[..Math.Min(8, _friends.LocalDeviceId.Length)]}";
            await RefreshAllAsync(silent: false);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _loaded = false;
            _friendMenuOpen = false;
            _presenceRenderPending = false;
            FriendRequestPromptCoordinator.Instance.Detach(this);
            _store.FriendsChanged -= Store_FriendsChanged;
            _presence.PresenceChanged -= Presence_PresenceChanged;
            _session.FriendRequestReceived -= Session_FriendRequestReceived;
            _presence.RemoveConsumer(FriendPresenceRefreshMode.Friends);
        }

        private void Store_FriendsChanged(object? sender, EventArgs e) =>
            DispatcherQueue.TryEnqueue(RenderCurrentPresence);

        private void Presence_PresenceChanged(object? sender, EventArgs e) =>
            DispatcherQueue.TryEnqueue(RenderCurrentPresence);

        private void Session_FriendRequestReceived(FriendRequest request) =>
            DispatcherQueue.TryEnqueue(RenderCurrentPresence);

        private async void RefreshFriendsButton_Click(object sender, RoutedEventArgs e) =>
            await RefreshAllAsync(silent: false);

        private async Task RefreshAllAsync(bool silent)
        {
            if (_refreshing) return;
            _refreshing = true;
            RefreshFriendsButton.IsEnabled = false;
            if (!silent) FriendsStatusTextBlock.Text = "Consultando sua tailnet...";
            try
            {
                await _presence.RefreshAsync();
                RenderCurrentPresence();
            }
            catch (Exception ex)
            {
                FriendsStatusTextBlock.Text = $"Tailscale indisponível: {ex.Message}";
            }
            finally
            {
                _refreshing = false;
                RefreshFriendsButton.IsEnabled = true;
            }
        }

        private void FriendMenu_Opening(object sender, object e) => _friendMenuOpen = true;

        private void FriendMenu_Closed(object sender, object e)
        {
            _friendMenuOpen = false;
            if (!_presenceRenderPending || !_loaded) return;
            _presenceRenderPending = false;
            // Let the menu command finish using its original DataContext first.
            DispatcherQueue.TryEnqueue(RenderCurrentPresence);
        }

        private void RenderCurrentPresence()
        {
            if (!_loaded) return;
            // Replacing ItemsSource destroys the button that owns the open flyout.
            // Coalesce presence updates until the user finishes the menu interaction.
            if (_friendMenuOpen) { _presenceRenderPending = true; return; }
            FriendPresenceSnapshot snapshot = _presence.GetSnapshot();
            FriendsStatusTextBlock.Text = snapshot.Status;
            var pending = _session.GetPendingFriendRequests().ToArray();
            PendingRequestsList.ItemsSource = pending;
            PendingCountText.Text = pending.Length.ToString();
            PendingEmptyCard.Visibility = pending.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

            List<MultiverseFriend> trusted = snapshot.Friends.ToList();
            TrustedFriendsList.ItemsSource = trusted;
            TrustedCountText.Text = trusted.Count.ToString();
            NoFriendsTextBlock.Visibility = trusted.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var visible = snapshot.Peers
                .Where(peer => !trusted.Any(friend => FriendPresenceService.PeerMatches(friend, peer)))
                .OrderByDescending(p => p.Online)
                .ThenBy(p => p.DisplayName)
                .ToArray();
            VisiblePeersList.ItemsSource = visible;
            PeersCountText.Text = visible.Length.ToString();
            PeersEmptyCard.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void AddFriendButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.DataContext is not TailscalePeerInfo peer) return;
            button.IsEnabled = false;
            FriendsStatusTextBlock.Text = $"Pedido enviado para {peer.DisplayName}. Aguardando aceite...";
            try
            {
                RemoteConnectResult result = await _friends.AddFriendAsync(peer);
                FriendsStatusTextBlock.Text = result.Message;
                await RefreshAllAsync(silent: true);
            }
            catch (Exception ex)
            {
                FriendsStatusTextBlock.Text = $"Não foi possível enviar o pedido: {ex.Message}";
            }
            finally { button.IsEnabled = true; }
        }

        private void AcceptFriendButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is FriendRequest request)
                _session.RespondToFriendRequest(request.RequestId, accept: true);
        }

        private void DeclineFriendButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is FriendRequest request)
                _session.RespondToFriendRequest(request.RequestId, accept: false);
        }

        private void RemoveFriendButton_Click(object sender, RoutedEventArgs e)
        {
            object? context = sender switch
            {
                Button button => button.DataContext,
                MenuFlyoutItem item => item.DataContext,
                _ => null
            };
            if (context is MultiverseFriend friend)
                _friends.RemoveFriend(friend.DeviceId);
        }

        private void DirectPermission_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleMenuFlyoutItem item || item.DataContext is not MultiverseFriend friend) return;
            _store.UpdatePermissions(friend.DeviceId, f =>
            {
                f.AllowDirectConnection = item.IsChecked;
                if (!item.IsChecked) f.AutoAcceptConnection = false;
            });
        }

        private void AutoAcceptPermission_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleMenuFlyoutItem item || item.DataContext is not MultiverseFriend friend) return;
            _store.UpdatePermissions(friend.DeviceId, f =>
                f.AutoAcceptConnection = f.AllowDirectConnection && item.IsChecked);
        }

        private void InputPermission_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleMenuFlyoutItem item || item.DataContext is not MultiverseFriend friend) return;
            _store.UpdatePermissions(friend.DeviceId, f => f.AllowRemoteInput = item.IsChecked);
        }

        private void AudioPermission_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleMenuFlyoutItem item || item.DataContext is not MultiverseFriend friend) return;
            _store.UpdatePermissions(friend.DeviceId, f => f.AllowRemoteAudio = item.IsChecked);
        }

        private void BlockPermission_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleMenuFlyoutItem item || item.DataContext is not MultiverseFriend friend) return;
            _store.UpdatePermissions(friend.DeviceId, f =>
            {
                f.Blocked = item.IsChecked;
                if (item.IsChecked)
                {
                    f.AllowDirectConnection = false;
                    f.AutoAcceptConnection = false;
                }
            });
        }

        private async void ConnectFriendButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.DataContext is not MultiverseFriend friend) return;
            button.IsEnabled = false;
            FriendsStatusTextBlock.Text = $"Conectando a {friend.DisplayName}...";
            try
            {
                if (friend.Blocked)
                {
                    FriendsStatusTextBlock.Text = "Este amigo está bloqueado.";
                    return;
                }
                if (!friend.Online)
                {
                    FriendsStatusTextBlock.Text = $"{friend.DisplayName} está offline.";
                    return;
                }

                RemoteConnectResult result = await _friends.ConnectAsync(friend, friend.LastKnownIp);
                FriendsStatusTextBlock.Text = result.Message;
                if (result.Success)
                {
                    RemoteSessionSnapshot session = _session.GetSnapshot();
                    if (session.Role == RemoteSessionRole.Client && await Stream.EnsureClientStreamingAsync())
                        Stream.ShowSessionWindow();
                }
            }
            catch (Exception ex)
            {
                FriendsStatusTextBlock.Text = $"Não foi possível conectar ao amigo: {ex.Message}";
            }
            finally { button.IsEnabled = true; }
        }
    }
}
