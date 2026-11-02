using System;
using System.Threading;
using System.Threading.Tasks;
using Multiverse.Services;

internal static class ConfirmedResourceTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
    }

    internal static void Run()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var worker = new Thread(() => { entered.Set(); release.Wait(); });
        worker.Start();
        Check(entered.Wait(2000), "worker started");
        try
        {
            bool timedOut = false;
            try { ConfirmedThread.JoinOrThrow(worker, 20, "test worker"); }
            catch (TimeoutException) { timedOut = true; }
            Check(timedOut && worker.IsAlive, "timeout cannot certify shutdown");
        }
        finally { release.Set(); }
        ConfirmedThread.JoinOrThrow(worker, 2000, "test worker");
        Check(!worker.IsAlive, "successful join certifies shutdown");
        Check(ConfirmedThread.JoinOrThrow(null, 1, "none") == false, "null worker is idle");
        bool selfRejected = false;
        try { ConfirmedThread.JoinOrThrow(Thread.CurrentThread, 1, "self"); }
        catch (InvalidOperationException) { selfRejected = true; }
        Check(selfRejected, "worker cannot join itself");

        using var taskEntered = new ManualResetEventSlim();
        using var taskRelease = new ManualResetEventSlim();
        Task taskWorker = Task.Run(() => { taskEntered.Set(); taskRelease.Wait(); });
        Check(taskEntered.Wait(2000), "task worker started");
        bool taskTimedOut = false;
        try { ConfirmedTask.WaitOrThrow(taskWorker, 20, "test task"); }
        catch (TimeoutException) { taskTimedOut = true; }
        Check(taskTimedOut && !taskWorker.IsCompleted, "task timeout cannot certify shutdown");
        taskRelease.Set();
        ConfirmedTask.WaitOrThrow(taskWorker, 2000, "test task");
        Check(taskWorker.IsCompleted, "successful task wait certifies shutdown");

        Task faultedWorker = Task.FromException(new InvalidOperationException("worker failed after terminating"));
        bool faultedAccepted = false;
        try { faultedAccepted = ConfirmedTask.WaitOrThrow(faultedWorker, 20, "faulted task"); }
        catch { faultedAccepted = false; }
        Check(faultedAccepted && faultedWorker.IsCompleted,
            "faulted completed task still certifies worker termination");

        var queue = new CodedFrameQueue<int>(4);
        Check(queue.TryEnqueue(1) && queue.TryEnqueue(2), "enqueue");
        Check(queue.TryDequeue(out int first) && first == 1, "FIFO order");
        Check(queue.Count == 1 && queue.Clear() == 1, "clear returns exact count");
        Check(queue.TryEnqueue(3), "reuse after clear");
        Check(queue.Count == 1 && queue.MaxObserved == 2, "coherent depth");
        Check(queue.Clear() == 1, "second clear");
        for (int i = 0; i < 4; i++) Check(queue.TryEnqueue(i), "capacity");
        Check(!queue.TryEnqueue(5), "bounded queue never silently drops a GOP");
        Check(queue.Count == 4 && queue.MaxObserved == 4, "capacity accounted");
        Check(queue.Clear() == 4 && queue.Count == 0, "flush exact");

        var concurrent = new CodedFrameQueue<int>(4);
        var producer = Task.Run(() => {
            for (int i = 0; i < 20000; i++)
            {
                if (!concurrent.TryEnqueue(i)) concurrent.Clear();
            }
        });
        var consumer = Task.Run(() => {
            while (!producer.IsCompleted || concurrent.Count > 0)
                concurrent.TryDequeue(out _);
        });
        Task.WaitAll(producer, consumer);
        Check(concurrent.Count == 0 && concurrent.MaxObserved <= 4,
            "concurrent enqueue/dequeue/flush keeps exact bounded depth");
        var resource = new object();
        object? retained = resource;
        int stopCalls = 0, disposeCalls = 0;
        bool rejectStop = true;
        bool rejected = false;
        try
        {
            ConfirmedResource.StopAndRelease(ref retained,
                _ => { stopCalls++; if (rejectStop) throw new TimeoutException(); },
                _ => disposeCalls++);
        }
        catch (TimeoutException) { rejected = true; }
        Check(rejected && ReferenceEquals(retained, resource) && disposeCalls == 0,
            "failed stop retains ownership and forbids dispose");
        rejectStop = false;
        ConfirmedResource.StopAndRelease(ref retained,
            _ => stopCalls++, _ => disposeCalls++);
        Check(retained is null && stopCalls == 2 && disposeCalls == 1,
            "successful retry releases exactly once");
        Console.WriteLine("confirmed resource and queue contracts: PASS");
    }
}
