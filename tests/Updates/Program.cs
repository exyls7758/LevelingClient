using Multiverse.Services;
static void Assert(bool value,string label){if(!value)throw new Exception(label);Console.WriteLine("PASS "+label);}
var backend=new Fake();bool active=false;var c=new UpdateCoordinator(backend,()=>active);
await c.CheckAsync();Assert(c.Offer?.Version=="11.75.1-rc.2","offer and RC version retained");
c.Later();Assert(c.Deferred,"later hides notification");await c.CheckAsync();Assert(c.Deferred,"same release remains deferred");
active=true;await c.UpdateAsync();Assert(backend.Downloads==0 && backend.Applies==0,"active session blocks download and restart");
active=false;backend.DuringDownload=()=>active=true;await c.UpdateAsync();Assert(c.Ready && backend.Applies==0,"session starting during download prevents install");
active=false;await c.UpdateAsync();Assert(backend.Downloads==1 && backend.Applies==1,"ready package installs without redownload");
var failed=new Fake{Fail=true};var failure=new UpdateCoordinator(failed,()=>false);await failure.CheckAsync();await failure.UpdateAsync();Assert(!failure.Ready && failed.Applies==0 && !failure.Busy,"download failure leaves current version running");
var absent=new UpdateCoordinator(new Fake{Available=false},()=>false);await absent.CheckAsync();Assert(absent.Offer is null,"unmanaged copy never applies updates");
var slow=new Fake{Hold=new TaskCompletionSource()};var parallel=new UpdateCoordinator(slow,()=>false);await parallel.CheckAsync();var first=parallel.UpdateAsync();await parallel.UpdateAsync();slow.Hold.SetResult();await first;Assert(slow.Downloads==1 && slow.Applies==1,"double clicks cannot start concurrent updates");
class Fake:IUpdateBackend {
 public bool Available{get;set;}=true; public bool Fail; public int Downloads,Applies; public Action? DuringDownload;public TaskCompletionSource? Hold;
 public Task<ReleaseOffer?> CheckAsync()=>Task.FromResult<ReleaseOffer?>(new("11.75.1-rc.2","notes",100));
 public async Task DownloadAsync(Action<int> p,CancellationToken token){Downloads++;p(50);if(Hold is not null)await Hold.Task;if(Fail)throw new IOException();DuringDownload?.Invoke();p(100);}
 public void Apply(){Applies++;}
}
