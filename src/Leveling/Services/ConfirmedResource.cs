using System;

namespace Multiverse.Services
{
    // Ownership remains with the caller until every required shutdown step
    // has returned. A thrown Stop must never be interpreted as a release.
    internal static class ConfirmedResource
    {
        internal static void StopAndRelease<T>(ref T? owner, Action<T> stop, Action<T> dispose)
            where T : class
        {
            T? resource = owner;
            if (resource is null) return;
            stop(resource);
            dispose(resource);
            owner = null;
        }
    }
}
