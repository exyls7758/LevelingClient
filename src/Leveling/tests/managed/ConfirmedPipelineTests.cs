using Multiverse.Services;

internal static class ConfirmedPipelineTests
{
    static void Check(bool value, string name) { if (!value) throw new Exception(name); }
    internal static async Task Run()
    {
        int passed = 0;
        Check(HostPipelineRecoveryTransaction.SenderResponseTimeoutMs >=
            HostPipelineRecoveryTransaction.ConfirmationTimeoutMs + 5000,
            "client acknowledgement budget must include sender stop/bootstrap and host confirmation");
        async Task Test(string name, Func<Task> test) { await test(); Console.WriteLine("PASS " + name); passed++; }
        await Test("1 dead output worker with capture progress", async () => {
            var f = new Fake(); var p = f.Pipeline();
            Check(await p.RecoverAsync(f.Identity, true), "recovery must succeed after full chain");
            Check(f.Starts == 1 && f.MaxEncoders == 1, "one encoder");
        });
        foreach (string name in new[] { "2 high RTT", "3 sender congestion" })
            await Test(name, async () => { var f = new Fake { Stalled = false }; var p = f.Pipeline();
                Check(!await p.RecoverAsync(f.Identity, true) && f.Starts == 0 && p.Attempts == 0, "healthy encoder preserved"); });
        await Test("4 encoder stop timeout retains ownership", async () => { var f = new Fake { EncoderStopTimeout = true }; var p = f.Pipeline();
            Check(!await p.RecoverAsync(f.Identity, true) && f.Starts == 0 && f.Encoders == 1 && p.State == HostPipelineState.FAULTED, "unsafe replacement");
            Check(!await p.RecoverAsync(f.Identity, true) && f.Starts == 0, "fault blocks retry"); });
        await Test("5 sender stop timeout retains ownership", async () => { var f = new Fake { SenderStopTimeout = true }; var p = f.Pipeline();
            Check(!await p.RecoverAsync(f.Identity, true) && f.Senders == 1 && f.EncoderStops == 0, "sender ownership lost"); });
        await Test("6 session changes while awaiting gate", async () => { var f = new Fake(); var p = f.Pipeline(); var id = f.Identity;
            await p.Gate.WaitAsync(); var task = p.RecoverAsync(id, true); f.Session++; p.Gate.Release();
            Check(!await task && f.EncoderStops == 0 && p.Attempts == 0, "stale recovery touched new session"); });
        await Test("7 spontaneous progress before gate costs no budget", async () => { var f = new Fake(); var p = f.Pipeline();
            await p.Gate.WaitAsync(); var task = p.RecoverAsync(f.Identity, true); f.Stalled = false; p.Gate.Release();
            Check(!await task && p.Attempts == 0, "consumed obsolete attempt"); });
        await Test("8 failed encoder start creates no sender", async () => { var f = new Fake { StartFailure = true }; var p = f.Pipeline();
            Check(!await p.RecoverAsync(f.Identity, true) && f.SenderStarts == 0 && p.State == HostPipelineState.FAULTED, "start failure ignored"); });
        await Test("9 rejected IDR never success", async () => { var f = new Fake { IdrResult = -1 }; var p = f.Pipeline();
            Check(!await p.RecoverAsync(f.Identity, true) && p.State == HostPipelineState.FAULTED, "IDR failure ignored"); });
        await Test("10 encoded/sent without presented never success", async () => { var f = new Fake { Present = false }; var p = f.Pipeline();
            Check(!await p.RecoverAsync(f.Identity, true) && p.State == HostPipelineState.FAULTED, "partial success"); });
        await Test("11 old generation presented ignored", async () => { var f = new Fake { OldFeedback = true }; var p = f.Pipeline();
            Check(!await p.RecoverAsync(f.Identity, true), "stale frame accepted"); });
        await Test("12 old session feedback ignored", async () => { var f = new Fake { OldSessionFeedback = true }; var p = f.Pipeline();
            Check(!await p.RecoverAsync(f.Identity, true), "stale session accepted"); });
        await Test("13 simultaneous triggers execute once", async () => { var f = new Fake(); var p = f.Pipeline(); var id = f.Identity;
            var results = await Task.WhenAll(p.RecoverAsync(id, true), p.RecoverAsync(id, true));
            Check(results.Count(x => x) == 1 && f.Starts == 1, "overlapping recoveries"); });
        await Test("14 twenty recovery/start-stop cycles release resources", async () => { var f = new Fake(); var p = f.Pipeline();
            for (int i = 0; i < 20; i++) { f.Stalled = true; Check(await p.RecoverAsync(f.Identity, true), "cycle " + i); }
            await f.StopSenderAsync(); await f.StopEncoderAsync(); Check(f.Encoders == 0 && f.Senders == 0 && f.MaxEncoders == 1, "pending resources"); });
        await Test("15 receiver/decoder timeout retains resource", () => { object? receiver = new(); var old = receiver; int replacements = 0;
            try { ConfirmedResource.StopAndRelease(ref receiver, _ => throw new TimeoutException(), _ => replacements++); } catch (TimeoutException) { }
            Check(ReferenceEquals(receiver, old) && replacements == 0, "receiver released while alive"); return Task.CompletedTask; });
        await Test("transport recovery does not restart encoder", async () => { var f = new Fake(); var p = f.Pipeline();
            Check(await p.RecoverAsync(f.Identity, false) && f.EncoderStops == 0 && f.Starts == 0 && f.SenderStarts == 1, "transport touched encoder"); });
        await Test("cancellation after stop never touches newer session", async () => { var f = new Fake { ChangeSessionOnStop = true }; var p = f.Pipeline();
            Check(!await p.RecoverAsync(f.Identity, true) && f.Starts == 0 && f.FaultStops == 0, "old recovery stopped new session"); });
        await Test("published generation survives failed sender bootstrap", async () => { var f = new Fake { BootstrapFailure = true }; var p = f.Pipeline();
            Check(!await p.RecoverAsync(f.Identity, true) && p.State == HostPipelineState.FAULTED && f.FaultStops == 1, "new generation escaped failure handling"); });
        Console.WriteLine($"confirmed pipeline: {passed} tests PASS");
    }

    sealed class Fake : IHostPipelineRecoveryDriver
    {
        public int Session = 1; public long Stream = 1;
        public bool Stalled = true, EncoderStopTimeout, SenderStopTimeout, StartFailure, OldFeedback, OldSessionFeedback, ChangeSessionOnStop;
        public bool Present = true, BootstrapFailure; public int IdrResult;
        public int Encoders = 1, Senders = 1, MaxEncoders = 1, Starts, EncoderStops, SenderStarts, FaultStops;
        public long Tick;
        public PipelineIdentity Identity => new(Session, Stream, CancellationToken.None);
        public HostPipelineRecoveryTransaction Pipeline() => new(this, new SemaphoreSlim(1, 1), () => Tick, _ => { Tick += 1000; return Task.CompletedTask; }, 3000);
        public bool IsCurrent(PipelineIdentity id) => id.SessionGeneration == Session && id.StreamGeneration == Stream && !id.Token.IsCancellationRequested;
        public bool NeedsRecovery(PipelineIdentity id, bool encoder) => Stalled;
        public PipelineProgress ReadProgress() => new(Session, OldFeedback ? Stream - 1 : Stream, 1, 1, 1,
            SenderStarts, SenderStarts, Present ? SenderStarts : 0, 0) { SessionGeneration = OldSessionFeedback ? Session - 1 : Session };
        public void BlockSubmissions() { }
        public Task StopSenderAsync() { if (SenderStopTimeout) throw new TimeoutException("sender"); Senders = 0; return Task.CompletedTask; }
        public Task StopEncoderAsync() { EncoderStops++; if (EncoderStopTimeout) throw new TimeoutException("encoder"); Encoders = 0; if (ChangeSessionOnStop) Session++; return Task.CompletedTask; }
        public Task StartEncoderAsync() { Starts++; if (StartFailure) throw new InvalidOperationException("start"); Encoders++; MaxEncoders = Math.Max(MaxEncoders, Encoders); return Task.CompletedTask; }
        public Task<PipelineIdentity> StartSenderAsync(PipelineIdentity id, bool newGeneration) { if (newGeneration) Stream++; if (BootstrapFailure) throw new PipelineBootstrapException(Identity, new TimeoutException("ready")); Senders++; SenderStarts++; return Task.FromResult(Identity); }
        public int RequestIdr() => IdrResult;
        public Task FaultAsync(PipelineIdentity id, string reason) { FaultStops++; return Task.CompletedTask; }
        public void Record(string name, HostPipelineState previous, HostPipelineState next, PipelineIdentity id, long attempt, string reason, int status) { }
    }
}
