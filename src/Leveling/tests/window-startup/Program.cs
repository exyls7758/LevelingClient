using Multiverse.Services;

static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
Action? queued = null;
bool invoked = false;
var worker = new UiThreadOperation(() => false, action => { queued = action; return true; });
var pending = await Task.Run(() => Task.FromResult(worker.RunAsync(() => invoked = true)));
Check(!invoked && !pending.IsCompleted, "worker waits for actual UI execution");
queued!(); await pending;
Check(invoked, "queued window operation completes after UI callback");
var failed = worker.RunAsync(() => throw new InvalidOperationException("attach failed"));
queued!();
try { await failed; throw new Exception("lost UI exception"); } catch (InvalidOperationException ex) { Check(ex.Message == "attach failed", "UI exceptions reach caller"); }
var rejected = new UiThreadOperation(() => false, _ => false);
try { await rejected.RunAsync(() => throw new Exception("must not run")); throw new Exception("queue rejection lost"); }
catch (InvalidOperationException) { Check(true, "closed dispatcher fails explicitly"); }
var ui = new UiThreadOperation(() => true, _ => throw new Exception("must not enqueue"));
await ui.RunAsync(() => invoked = false); Check(!invoked, "UI caller executes without requeue");
var retry = new PresentationRetry();
Check(retry.TrySchedule() && retry.TrySchedule() && retry.TrySchedule() && !retry.TrySchedule() && !retry.TrySchedule(), "attachment retries bounded to three");
retry.Reset(); Check(retry.TrySchedule(), "new decoder gets fresh retry budget");
var status = new ClientVideoStatus();
Check(status.Observe(0,1,1,0,0,0,0,true).Contains("pacotes"),"startup explains absent UDP");
Check(status.Observe(2000,1,1,5,0,0,0,true).Contains("quadro"),"fragments are not confused with decoded video");
Check(status.Observe(2500,1,1,10,1,0,0,true).Contains("quadro-chave"),"reference wait explained");
Check(status.Observe(2600,1,1,10,1,1,1,false)=="","status disappears only after a real presentation");
Check(status.Observe(3200,1,1,10,1,1,1,false)=="","short jitter does not flash warning");
Check(status.Observe(5000,1,1,10,1,1,1,false).Length>0,"stalled presentation becomes visible");
Check(status.Observe(5100,1,1,15,2,2,2,false)=="","progress clears stall warning");
Check(status.Observe(5200,2,2,0,0,0,0,true).Contains("pacotes"),"new session resets presentation history");
status.Observe(5300,2,2,10,1,1,1,false);
Check(status.Observe(7600,2,2,10,1,1,1,false).Contains("pacotes"),"old nonzero counters do not misdiagnose a network interruption as rendering");
Check(status.Observe(7800,2,2,20,2,2,1,false).Contains("exibição"),"presentation stall with fresh decoded data is distinguished");
