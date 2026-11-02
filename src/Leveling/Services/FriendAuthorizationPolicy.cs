using Multiverse.Models;

namespace Multiverse.Services
{
    internal enum FriendAuthorizationDecision
    {
        RejectUnknown,
        RejectBlocked,
        RejectSignature,
        RejectDirectDisabled,
        RequireApproval,
        AutoAccept
    }

    internal static class FriendAuthorizationPolicy
    {
        internal static FriendAuthorizationDecision EvaluateDirectRequest(
            MultiverseFriend? friend,
            bool signatureValid)
        {
            if (friend is null)
                return FriendAuthorizationDecision.RejectUnknown;
            if (friend.Blocked)
                return FriendAuthorizationDecision.RejectBlocked;
            if (!signatureValid)
                return FriendAuthorizationDecision.RejectSignature;
            if (!friend.AllowDirectConnection)
                return FriendAuthorizationDecision.RejectDirectDisabled;
            return friend.AutoAcceptConnection
                ? FriendAuthorizationDecision.AutoAccept
                : FriendAuthorizationDecision.RequireApproval;
        }
    }
}
