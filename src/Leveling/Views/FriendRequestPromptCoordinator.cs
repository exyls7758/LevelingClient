using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Multiverse.Models;
using Multiverse.Services;
using System;
using System.Threading;

namespace Multiverse.Views
{
    /// <summary>
    /// UI-only prompt bridge. It never starts a session; it only answers a
    /// pending trust request already held by RemoteSessionService.
    /// </summary>
    internal sealed class FriendRequestPromptCoordinator
    {
        private static readonly Lazy<FriendRequestPromptCoordinator> LazyInstance =
            new(() => new FriendRequestPromptCoordinator());
        public static FriendRequestPromptCoordinator Instance => LazyInstance.Value;

        private readonly RemoteSessionService _session = RemoteSessionService.Instance;
        private readonly SemaphoreSlim _dialogGate = new(1, 1);
        private readonly object _sync = new();
        private WeakReference<FrameworkElement>? _activeOwner;

        private FriendRequestPromptCoordinator()
        {
            _session.FriendRequestReceived += OnFriendRequestReceived;
            _session.DirectSessionRequestReceived += OnDirectSessionRequestReceived;
        }

        public void Attach(FrameworkElement owner)
        {
            if (owner is null) return;
            lock (_sync) _activeOwner = new WeakReference<FrameworkElement>(owner);
            TryShowPendingRequest(owner);
            TryShowPendingDirectRequest(owner);
        }

        private void TryShowPendingRequest(FrameworkElement owner)
        {
            foreach (FriendRequest pending in _session.GetPendingFriendRequests())
            {
                if (string.IsNullOrWhiteSpace(pending.RequestId)) continue;
                OnFriendRequestReceived(pending);
                break;
            }
        }

        private void TryShowPendingDirectRequest(FrameworkElement owner)
        {
            foreach (DirectSessionRequest pending in _session.GetPendingDirectSessionRequests())
            {
                if (string.IsNullOrWhiteSpace(pending.RequestId)) continue;
                OnDirectSessionRequestReceived(pending);
                break;
            }
        }

        public void Detach(FrameworkElement owner)
        {
            lock (_sync)
            {
                if (_activeOwner is not null &&
                    _activeOwner.TryGetTarget(out FrameworkElement? current) &&
                    ReferenceEquals(current, owner))
                {
                    _activeOwner = null;
                }
            }
        }

        private void OnFriendRequestReceived(FriendRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.RequestId)) return;
            FrameworkElement? owner = null;
            lock (_sync) _activeOwner?.TryGetTarget(out owner);
            if (owner is null) return;

            owner.DispatcherQueue.TryEnqueue(async () =>
            {
                await _dialogGate.WaitAsync();
                try
                {
                    // The request may have expired or been answered on Friends.
                    bool stillPending = false;
                    foreach (FriendRequest pending in _session.GetPendingFriendRequests())
                    {
                        if (string.Equals(pending.RequestId, request.RequestId, StringComparison.Ordinal))
                        {
                            stillPending = true;
                            break;
                        }
                    }
                    if (!stillPending || owner.XamlRoot is null) return;

                    var dialog = new ContentDialog
                    {
                        XamlRoot = owner.XamlRoot,
                        Title = "Pedido de amizade Leveling",
                        Content = $"{request.DisplayName} quer adicionar este PC como amigo.\n{request.RemoteAddress}\n\nAceitar cria confiança para conexões futuras em 1 clique. Nenhuma transmissão começa agora.",
                        PrimaryButtonText = "Aceitar",
                        CloseButtonText = "Recusar",
                        DefaultButton = ContentDialogButton.Primary
                    };
                    ContentDialogResult result = await dialog.ShowAsync();
                    _session.RespondToFriendRequest(
                        request.RequestId,
                        result == ContentDialogResult.Primary);
                }
                catch
                {
                    // Safe failure: request remains pending/timeout; stream is untouched.
                }
                finally
                {
                    _dialogGate.Release();
                }
            });
        }

        private void OnDirectSessionRequestReceived(DirectSessionRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.RequestId)) return;
            FrameworkElement? owner = null;
            lock (_sync) _activeOwner?.TryGetTarget(out owner);
            if (owner is null) return;

            owner.DispatcherQueue.TryEnqueue(async () =>
            {
                await _dialogGate.WaitAsync();
                try
                {
                    bool stillPending = false;
                    foreach (DirectSessionRequest pending in _session.GetPendingDirectSessionRequests())
                    {
                        if (string.Equals(pending.RequestId, request.RequestId, StringComparison.Ordinal))
                        {
                            stillPending = true;
                            break;
                        }
                    }
                    if (!stillPending || owner.XamlRoot is null) return;

                    var dialog = new ContentDialog
                    {
                        XamlRoot = owner.XamlRoot,
                        Title = "Conexão direta Leveling",
                        Content = $"{request.DisplayName} quer conectar ao seu PC.\n{request.RemoteAddress}",
                        PrimaryButtonText = "Aceitar",
                        CloseButtonText = "Recusar",
                        DefaultButton = ContentDialogButton.Primary
                    };
                    ContentDialogResult result = await dialog.ShowAsync();
                    _session.RespondToDirectSessionRequest(
                        request.RequestId,
                        result == ContentDialogResult.Primary);
                }
                catch
                {
                    // Timeout/rejection remains owned by RemoteSessionService.
                }
                finally
                {
                    _dialogGate.Release();
                }
            });
        }
    }
}
