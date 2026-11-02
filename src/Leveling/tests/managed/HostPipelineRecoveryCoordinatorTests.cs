using Multiverse.Services;

internal static class HostPipelineRecoveryCoordinatorTests
{
    internal static async Task RunAsync()
    {
        static void Check(bool value, string message)
        {
            if (!value) throw new Exception("FAIL: " + message);
        }
        var coordinator = new HostPipelineRecoveryCoordinator();
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Check(coordinator.TryStart(() => pending.Task, 0), "first attempt owns lifecycle");
        Check(!coordinator.TryStart(() => Task.FromResult(true), 100), "no overlapping restart");
        Check(!coordinator.TryReportTimeout(11999), "timeout grace");
        Check(coordinator.TryReportTimeout(12000), "timeout reported once");
        Check(!coordinator.TryReportTimeout(13000), "no repeated timeout noise");
        Check(!coordinator.TryTakeCompleted(out _), "cannot abandon running work");
        pending.SetResult(true);
        Task<bool>? completed;
        while (!coordinator.TryTakeCompleted(out completed))
            await Task.Delay(1);
        Check(completed is not null && await completed, "completion observed");
        Check(coordinator.TryStart(() => Task.FromResult(false), 14000), "new attempt after completion");
        while (!coordinator.TryTakeCompleted(out _)) await Task.Delay(1);
        Console.WriteLine("host recovery coordinator: PASS");
    }
}
