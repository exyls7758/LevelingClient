using Velopack;
using Velopack.Sources;
namespace Multiverse.Services;
internal static class LevelingUpdates {
    internal static UpdateCoordinator Instance { get; } = new(
        new ReleaseUpdateBackend(new GithubSource("https://github.com/exyls7758/LevelingClient", null, true)),
        () => HostTransmissionService.Instance.GetSnapshot().CanStop || RemoteSessionService.Instance.GetSnapshot().Connected);
}
