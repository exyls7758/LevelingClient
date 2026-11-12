using Multiverse.Services;
using System.Text.Json;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
}

static void LeaseTests()
{
    bool active = false;
    int disabled = 0;
    var queue = new Queue<string>();
    var gate = new ExclusivePacketConsumer<string>(v => { active = v; if (!v) disabled++; },
        () => queue.Count > 0 ? queue.Dequeue() : null);
    var first = gate.Acquire();
    queue.Enqueue("old");
    Check(first.TryRead() == "old", "first owner reads");
    var second = gate.Acquire();
    int before = disabled;
    first.Dispose();
    Check(active && disabled == before, "stale release must not disable replacement");
    Check(first.TryRead() is null, "stale owner cannot read");
    queue.Enqueue("new");
    Check(second.TryRead() == "new", "replacement reads");
    gate.Invalidate();
    Check(!active && second.TryRead() is null, "invalidate revokes current owner");
    second.Dispose();
    for (int i = 0; i < 1000; i++)
    {
        var a = gate.Acquire();
        var b = gate.Acquire();
        Parallel.Invoke(a.Dispose, () => { _ = b.TryRead(); });
        Check(b.IsCurrent, "concurrent old release cannot revoke new owner");
        b.Dispose();
    }
    bool held = false;
    using var entered = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    var owner = gate.Acquire();
    var stop = Task.Run(() => gate.RunInactive(() => { entered.Set(); release.Wait(); }));
    entered.Wait();
    var acquire = Task.Run(() => { using var lease = gate.Acquire(); held = true; });
    Check(!acquire.Wait(25), "new owner must wait for native stop");
    release.Set();
    Task.WaitAll(stop, acquire);
    Check(held, "new owner acquired after stop");
    owner.Dispose();
}

static VideoProgressSample S(long now, long packets, long frames, long decoded = 0, long presented = 0,
    bool host = false, long transportId = 1) => new()
{
    NowMs = now, SessionGeneration = 1, StreamGeneration = 1, TransportId = transportId,
    Connected = true, Host = host, Running = true, DecoderRunning = !host,
    PresenterAttached = !host, ExpectedFps = 60, Packets = packets, Frames = frames,
    Decoded = decoded, Presented = presented,
    Captured = host ? frames : 0, Encoded = host ? frames : 0
};

static void ProgressTests()
{
    var m = new VideoProgressMonitor();
    Check(m.Observe(S(0, 0, 0)).Reason == "", "startup grace");
    Check(m.Observe(S(250, 100, 10, 10, 10)).Reason == "", "healthy stream");
    var gap = m.Observe(S(500, 200, 10, 10, 10));
    Check(gap.Reason == "CLIENT_FRAME_GAP" && !gap.RecoverReceiver, "packets without frames");
    var stalled = m.Observe(S(1500, 500, 10, 10, 10));
    Check(stalled.RequestIdr && !stalled.RecoverReceiver, "reassembly stall requests IDR, not rebind");
    Check(m.Observe(S(3000, 700, 10, 10, 10)).RequestIdr, "IDR retry persists");
    var silence = m.Observe(S(7000, 700, 10, 10, 10));
    Check(silence.RecoverReceiver, "sustained UDP silence requests recovery");
    Check(m.Observe(S(7250, 700, 10, 10, 10, transportId: 2)).Reason == "", "new receiver resets clocks");
    Check(!m.Observe(S(7500, 800, 20, 20, 20, transportId: 2)).RecoverReceiver, "new progress healthy");
    m.Reset();
    m.Observe(S(0, 10, 10, 10, 10));
    m.Observe(S(250, 20, 20, 10, 10));
    var decode = m.Observe(S(1500, 100, 100, 10, 10));
    Check(decode.Reason == "CLIENT_DECODE_STALL" && !decode.RecoverReceiver && !decode.RequestIdr,
        "decoder stall must not rebind healthy UDP");
    m.Reset();
    m.Observe(S(0, 10, 10, 10, 10));
    m.Observe(S(250, 20, 20, 20, 10));
    var present = m.Observe(S(1500, 100, 100, 100, 10));
    Check(present.Reason == "CLIENT_PRESENTER_STALL" && !present.RecoverReceiver,
        "presenter stall is not a network failure");
    m.Reset();
    m.Observe(S(0, 10, 10, host: true));
    Check(m.Observe(S(250, 20, 10, host: true)).Reason == "HOST_CAPTURE_GAP", "host production gap");
    Check(m.Observe(S(500, 30, 30, host: true)).Reason == "", "host recovers");
    // Receiving P-frames does not prove that a decoder has a valid reference.
    m.Reset();
    m.Observe(S(0, 0, 0));
    m.Observe(S(250, 100, 10, 0, 0));
    var referenceWait = new VideoProgressSample
    {
        NowMs = 1500, SessionGeneration = 1, StreamGeneration = 1, TransportId = 1,
        Connected = true, Running = true, DecoderRunning = true, PresenterAttached = true,
        ExpectedFps = 60, Packets = 500, Frames = 100, WaitingForReference = true
    };
    Check(m.Observe(referenceWait).RequestIdr, "missing reference requests IDR despite incoming P-frames");
    Check(m.Observe(new VideoProgressSample
    {
        NowMs = 2500, SessionGeneration = 1, StreamGeneration = 1, TransportId = 1,
        Connected = true, Running = true, DecoderRunning = true, PresenterAttached = true,
        ExpectedFps = 60, Packets = 700, Frames = 200, WaitingForReference = true
    }).RequestIdr, "reference wait retries until a valid reference is accepted");
    Check(!m.Observe(S(2750, 800, 250, 250, 250)).RequestIdr,
        "healthy reference stops the retry");
    // A decoder can lose its reference immediately after decoding a frame.
    // An explicit transition into reference-wait must not wait for a separate
    // 1.2-second decode stall, and must not be reset by incoming P-frames.
    m.Reset();
    m.Observe(S(0, 0, 0, 0, 0));
    m.Observe(S(250, 100, 10, 10, 10));
    var enteredWait = new VideoProgressSample
    {
        NowMs = 500, SessionGeneration = 1, StreamGeneration = 1, TransportId = 1,
        Connected = true, Running = true, DecoderRunning = true, PresenterAttached = true,
        ExpectedFps = 60, Packets = 200, Frames = 20, Decoded = 10, Presented = 10,
        WaitingForReference = true
    };
    Check(m.Observe(enteredWait).RequestIdr,
        "reference-wait transition requests IDR even after recent decode progress");
    Check(!m.Observe(new VideoProgressSample
    {
        NowMs = 750, SessionGeneration = 1, StreamGeneration = 1, TransportId = 1,
        Connected = true, Running = true, DecoderRunning = true, PresenterAttached = true,
        ExpectedFps = 60, Packets = 300, Frames = 30, Decoded = 10, Presented = 10,
        WaitingForReference = true
    }).RequestIdr, "reference-wait retry respects cooldown");
    Check(m.Observe(new VideoProgressSample
    {
        NowMs = 1500, SessionGeneration = 1, StreamGeneration = 1, TransportId = 1,
        Connected = true, Running = true, DecoderRunning = true, PresenterAttached = true,
        ExpectedFps = 60, Packets = 400, Frames = 40, Decoded = 10, Presented = 10,
        WaitingForReference = true
    }).RequestIdr, "reference-wait retry continues despite incoming frames");
    Check(!m.Observe(S(1750, 500, 50, 50, 50)).RequestIdr,
        "valid reference ends retry");
    Check(!m.Observe(S(2000, 600, 60, 60, 60)).RequestIdr,
        "healthy frames do not request IDR");
    m.Reset();
    m.Observe(S(0, 0, 0));
    Check(m.Observe(new VideoProgressSample
    {
        NowMs = 250, SessionGeneration = 1, StreamGeneration = 1, TransportId = 1,
        Connected = true, Running = true, DecoderRunning = true, PresenterAttached = true,
        ExpectedFps = 60, Packets = 100, Frames = 10, WaitingForReference = true
    }).RequestIdr, "initial reference wait requests IDR");
    Check(!m.Observe(new VideoProgressSample
    {
        NowMs = 500, SessionGeneration = 1, StreamGeneration = 1, TransportId = 1,
        Connected = true, Running = true, DecoderRunning = true, PresenterAttached = true,
        ExpectedFps = 60, Packets = 200, Frames = 20, WaitingForReference = true
    }).RequestIdr, "initial wait is debounced");
    Check(m.Observe(new VideoProgressSample
    {
        NowMs = 750, SessionGeneration = 2, StreamGeneration = 2, TransportId = 2,
        Connected = true, Running = true, DecoderRunning = true, PresenterAttached = true,
        ExpectedFps = 60, Packets = 1, Frames = 0, WaitingForReference = true
    }).RequestIdr == false, "new session starts a fresh grace sample");
    Check(m.Observe(new VideoProgressSample
    {
        NowMs = 1000, SessionGeneration = 2, StreamGeneration = 2, TransportId = 2,
        Connected = true, Running = true, DecoderRunning = true, PresenterAttached = true,
        ExpectedFps = 60, Packets = 100, Frames = 10, WaitingForReference = true
    }).RequestIdr, "new session can request IDR without old cooldown");
    // Regression: the previous helper supplied host-only counters on the
    // client. Their disappearance incorrectly reset the reference recovery.
    m.Reset();
    m.Observe(S(0, 0, 0));
    m.Observe(new VideoProgressSample
    {
        NowMs = 250, SessionGeneration = 1, StreamGeneration = 1, TransportId = 1,
        Connected = true, Running = true, DecoderRunning = true, PresenterAttached = true,
        ExpectedFps = 60, Packets = 100, Frames = 10, Decoded = 10, Presented = 10,
        Captured = 10, Encoded = 10
    });
    Check(m.Observe(new VideoProgressSample
    {
        NowMs = 500, SessionGeneration = 1, StreamGeneration = 1, TransportId = 1,
        Connected = true, Running = true, DecoderRunning = true, PresenterAttached = true,
        ExpectedFps = 60, Packets = 200, Frames = 20, Decoded = 10, Presented = 10,
        WaitingForReference = true
    }).RequestIdr, "irrelevant client counters cannot reset reference recovery");
    Check(!m.Observe(new VideoProgressSample
    {
        NowMs = 750, SessionGeneration = 1, StreamGeneration = 1, TransportId = 1,
        Connected = true, Running = true, DecoderRunning = true, PresenterAttached = true,
        ExpectedFps = 60, Packets = 300, Frames = 30, Decoded = 10, Presented = 10,
        WaitingForReference = true
    }).RequestIdr, "irrelevant counter changes do not bypass IDR cooldown");
    Check(m.Observe(new VideoProgressSample
    {
        NowMs = 1500, SessionGeneration = 1, StreamGeneration = 1, TransportId = 1,
        Connected = true, Running = true, DecoderRunning = true, PresenterAttached = true,
        ExpectedFps = 60, Packets = 400, Frames = 40, Decoded = 10, Presented = 10,
        WaitingForReference = true
    }).RequestIdr, "reference retry survives incoming P-frames");
    Check(!m.Observe(S(1750, 500, 50, 50, 50)).RequestIdr,
        "accepted reference ends the retry");
    // Host-only counters are likewise independent of the local decoder.
    m.Reset();
    m.Observe(S(0, 0, 0, host: true));
    m.Observe(new VideoProgressSample
    {
        NowMs = 250, SessionGeneration = 1, StreamGeneration = 1, TransportId = 1,
        Connected = true, Host = true, Running = true, ExpectedFps = 60,
        Packets = 100, Frames = 10, Captured = 10, Encoded = 10,
        Decoded = 10, Presented = 10
    });
    Check(m.Observe(S(500, 200, 20, host: true)).Reason == "",
        "host decoder counters cannot reset or fabricate a capture gap");
    Check(m.Observe(S(750, 1, 0, host: true)).Reason == "",
        "host counter reset begins a new episode");
    Check(m.Observe(S(1000, 100, 10)).Reason == "",
        "role change begins a new episode");
    m.Reset();
    m.Observe(S(0, 100, 100));
    Check(m.Observe(S(250, 0, 0)).Reason == "", "counter reset is not packet loss");
}

static async Task RecorderTests()
{
    string directory = Path.Combine(Path.GetTempPath(), "leveling-regression-" + Guid.NewGuid().ToString("N"));
    long tick = 100000;
    try
    {
        using var recorder = new VideoFlightRecorder(directory, () => Interlocked.Read(ref tick), startWorker: false);
        for (int i = 0; i < 1000; i++) recorder.Collect(JsonSerializer.Serialize(new { kind = "sample", i }));
        recorder.MarkIncident("CLIENT_FRAME_GAP", 200);
        Interlocked.Add(ref tick, 5000);
        recorder.Collect("{\"kind\":\"after\"}");
        string active = await recorder.ExportAsync("{\"test\":true}", new Dictionary<string, string> {
            ["summary.txt"] = "relatório UTF-8: áudio", ["errors.jsonl"] = "{\"HResult\":-1}\n"
        });
        using (var zip = System.IO.Compression.ZipFile.OpenRead(active))
        {
            Check(zip.GetEntry("incident_in_progress.jsonl") is not null, "export includes active incident");
            Check(zip.GetEntry("recent.jsonl") is not null, "export includes recent history");
            using var summary = new StreamReader(zip.GetEntry("summary.txt")!.Open());
            Check(summary.ReadToEnd() == "relatório UTF-8: áudio", "unified ZIP preserves technical summary and accents");
            Check(zip.GetEntry("errors.jsonl") is not null, "unified ZIP includes durable exception history");
        }
        Interlocked.Add(ref tick, 20000);
        await recorder.FlushAsync();
        string[] files = Directory.GetFiles(directory, "video_incident_*.jsonl");
        Check(files.Length == 1, "post-window incident saved; write status: " + recorder.LastWriteError);
        string text = File.ReadAllText(files[0]);
        Check(text.Contains("CLIENT_FRAME_GAP") && text.Contains("after"), "pre/post history retained");
        foreach (string line in File.ReadAllLines(files[0]))
            using (JsonDocument.Parse(line)) { }
        Parallel.For(0, 8, worker =>
        {
            for (int i = 0; i < 500; i++) recorder.Collect(JsonSerializer.Serialize(new { worker, i }));
        });
        Check(recorder.DroppedEntries >= 0, "concurrent collection completes");
        for (int i = 0; i < 25; i++)
        {
            Interlocked.Add(ref tick, 100000);
            recorder.MarkIncident("TEST_" + i, 0);
            await recorder.FlushAsync(true);
        }
        files = Directory.GetFiles(directory, "video_incident_*.jsonl");
        Check(files.Length <= 20 && files.Sum(f => new FileInfo(f).Length) <= 50L * 1024 * 1024,
            "disk retention bounded");
        // A long incident must retain its triggering history and its ending.
        string longDir = Path.Combine(directory, "long-incident");
        using (var longRecorder = new VideoFlightRecorder(longDir, () => Interlocked.Read(ref tick), startWorker: false))
        {
            longRecorder.Collect("{\"kind\":\"first\"}");
            longRecorder.MarkIncident("CLIENT_UDP_SILENCE", 0);
            string payload = new string('x', 4096);
            for (int i = 0; i < 2000; i++)
            {
                Interlocked.Add(ref tick, 5);
                longRecorder.Collect(JsonSerializer.Serialize(new { kind = "sample", payload }));
            }
            longRecorder.Collect("{\"kind\":\"last\"}");
            await longRecorder.FlushAsync(true);
            string incidentText = File.ReadAllText(Directory.GetFiles(longDir, "video_incident_*.jsonl").Single());
            Check(incidentText.Contains("\"kind\":\"first\"") && incidentText.Contains("\"kind\":\"last\""),
                "bounded incident retains its first and last samples");
            Check(longRecorder.DroppedEntries > 0 && longRecorder.PendingBytes <= 16L * 1024 * 1024,
                "large incident is bounded without losing the end");
        }
        // A failed write must retain the incident for retry/export.
        string blocked = Path.Combine(directory, "blocked");
        File.WriteAllText(blocked, "file, not directory");
        using var retry = new VideoFlightRecorder(blocked, () => Interlocked.Read(ref tick), startWorker: false);
        retry.Collect("{\"kind\":\"before\"}");
        retry.MarkIncident("WRITE_RETRY", 0);
        await retry.FlushAsync(true);
        Check(retry.LastWriteError.Length > 0, "write failure visible");
        File.Delete(blocked);
        await retry.FlushAsync(true);
        Check(Directory.GetFiles(blocked, "video_incident_*.jsonl").Length == 1, "failed incident retried");
    }
    finally { try { Directory.Delete(directory, true); } catch { } }
}

static void IntervalTests()
{
    var meter = new VideoFrameIntervalMeter();
    Check(meter.GetSnapshot().LastAgeMs < 0, "no frame recorded is unavailable, not zero");
    meter.Mark();
    Thread.Sleep(120);
    meter.Mark();
    var gap = meter.GetSnapshot();
    Check(gap.GapsOver100Ms == 1 && gap.MaxGapMs >= 100 && gap.LastGapMs >= 100,
        "long frame interval recorded");
    meter.Reset();
    Check(meter.GetSnapshot().GapsOver100Ms == 0 && meter.GetSnapshot().LastAgeMs < 0,
        "new session clears interval history");
}

try
{
LeaseTests();
PresenterRecoveryTests.Run();
var recoveryQueue = new CodedFrameQueue<int>(4);
recoveryQueue.TryEnqueue(1);
Check(recoveryQueue.ClearForDiscontinuity() == 1, "recovery clears old references");
Check(!recoveryQueue.TryDequeue(out _, out _), "empty poll cannot consume recovery barrier");
recoveryQueue.TryEnqueue(100);
recoveryQueue.TryEnqueue(101);
Check(recoveryQueue.TryDequeue(out var idr, out var reset) && idr == 100 && reset, "first post-flush frame carries decoder reset");
Check(recoveryQueue.TryDequeue(out var delta, out reset) && delta == 101 && !reset, "reset cannot move after the new keyframe");
recoveryQueue.ClearForDiscontinuity();
recoveryQueue.Clear();
recoveryQueue.TryEnqueue(200);
Check(recoveryQueue.TryDequeue(out _, out reset) && !reset, "new session clears obsolete recovery state");
ProgressTests();
IntervalTests();
await RecorderTests();
Console.WriteLine("Leveling managed regression: PASS");

HostPipelineRecoveryTests.Run();

await HostPipelineRecoveryCoordinatorTests.RunAsync();

VideoLatencyWindowTests.Run();

ConfirmedResourceTests.Run();

await ControlHardeningTests.RunAsync();

TransportRecoveryTests.Run();
await ConfirmedPipelineTests.Run();
EncoderStallDetectorTests.Run();

VideoGenerationIdentityTests.Run();
return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
