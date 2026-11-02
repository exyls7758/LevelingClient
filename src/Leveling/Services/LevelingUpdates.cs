using System;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;
namespace Multiverse.Services;
internal sealed class GithubUpdateBackend : IUpdateBackend {
    readonly UpdateManager manager = new(new GithubSource("https://github.com/exyls7758/LevelingClient", null, true));
    UpdateInfo? pending;
    public bool Available => manager.IsInstalled;
    public async Task<ReleaseOffer?> CheckAsync() {
        pending=await manager.CheckForUpdatesAsync();
        if(pending is null) return null;
        // Full packages keep download size/progress unambiguous; deltas can be enabled later.
        pending=new UpdateInfo(pending.TargetFullRelease, false, null, Array.Empty<VelopackAsset>());
        var a=pending.TargetFullRelease;
        if(string.IsNullOrWhiteSpace(a.SHA256)) throw new InvalidOperationException("Missing package checksum");
        return new(a.Version.ToString(),a.NotesMarkdown ?? "Melhorias e correÃ§Ãµes do Leveling.",a.Size);
    }
    public Task DownloadAsync(Action<int> progress,CancellationToken token) => manager.DownloadUpdatesAsync(pending!,progress,token);
    public void Apply() => manager.ApplyUpdatesAndRestart(pending!.TargetFullRelease);
}
internal static class LevelingUpdates {
    internal static UpdateCoordinator Instance { get; } = new(new GithubUpdateBackend(),
        () => HostTransmissionService.Instance.GetSnapshot().CanStop || RemoteSessionService.Instance.GetSnapshot().Connected);
}
