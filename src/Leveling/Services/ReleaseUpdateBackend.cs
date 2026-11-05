using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Multiverse.Services;

// Full packages only. UpdateManager verifies integrity and owns replacement/restart.
public sealed class ReleaseUpdateBackend : IUpdateBackend
{
    private sealed class ReleaseManager : UpdateManager
    {
        public ReleaseManager(IUpdateSource source) : base(source) { }
        public Task<VelopackAssetFeed> ReadFeedAsync() => Source.GetReleaseFeed(Log, AppId, Channel);
    }
    private readonly ReleaseManager manager;
    private UpdateInfo? pending;
    private readonly Dictionary<string, VelopackAsset> previous = new(StringComparer.Ordinal);
    public ReleaseUpdateBackend(IUpdateSource source) => manager = new ReleaseManager(source);
    public bool Available => manager.IsInstalled;
    private static ReleaseOffer Describe(VelopackAsset asset) =>
        new(asset.Version.ToString(), asset.NotesMarkdown ?? "Melhorias e correções do Leveling.", asset.Size);
    private static void Validate(VelopackAsset asset)
    {
        if (asset.Type != VelopackAssetType.Full || asset.Size <= 0 ||
            asset.SHA256?.Length != 64 || !asset.SHA256.All(Uri.IsHexDigit))
            throw new InvalidOperationException("Invalid full-package integrity metadata");
    }
    public async Task<ReleaseOffer?> CheckAsync()
    {
        pending = null;
        var update = await manager.CheckForUpdatesAsync();
        if (update is null) return null;
        Validate(update.TargetFullRelease);
        pending = new UpdateInfo(update.TargetFullRelease, false, null, Array.Empty<VelopackAsset>());
        return Describe(pending.TargetFullRelease);
    }
    public async Task<IReadOnlyList<ReleaseOffer>> GetPreviousAsync()
    {
        previous.Clear();
        if (!Available || manager.CurrentVersion is null) return Array.Empty<ReleaseOffer>();
        var feed = await manager.ReadFeedAsync();
        var current = manager.CurrentVersion;
        var assets = feed.Assets.Where(a => a.Type == VelopackAssetType.Full &&
            a.PackageId == manager.AppId && a.Version < current &&
            a.Version.Major == current.Major && a.Version.Minor == current.Minor)
            .OrderByDescending(a => a.Version).GroupBy(a => a.Version.ToString())
            .Select(g => g.First()).Take(5).ToArray();
        foreach (var asset in assets)
        {
            Validate(asset);
            previous.Add(asset.Version.ToString(), asset);
        }
        return assets.Select(Describe).ToArray();
    }
    public async Task<ReleaseOffer> SelectPreviousAsync(string version)
    {
        pending = null;
        // Refresh at selection so withdrawn releases cannot be restored from stale UI.
        await GetPreviousAsync();
        if (!previous.TryGetValue(version, out var asset))
            throw new InvalidOperationException("Previous release no longer available");
        pending = new UpdateInfo(asset, true, null, Array.Empty<VelopackAsset>());
        return Describe(asset);
    }
    public Task DownloadAsync(Action<int> progress, CancellationToken token) =>
        manager.DownloadUpdatesAsync(pending ?? throw new InvalidOperationException("No selected release"), progress, token);
    public void Apply() => manager.ApplyUpdatesAndRestart(
        (pending ?? throw new InvalidOperationException("No selected release")).TargetFullRelease);
}
