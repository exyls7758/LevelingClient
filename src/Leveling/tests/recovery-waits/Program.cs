int failures=0,passed=0;
void Check(bool ok,string message){if(!ok)throw new Exception(message);}
async Task Cancelled(Task<bool> task) {
    try {await task.WaitAsync(TimeSpan.FromMilliseconds(120)); throw new Exception("obsolete wait reported success/rejection");}
    catch(OperationCanceledException){}
}
async Task Test(string name,Func<Task> test){try{await test();passed++;Console.WriteLine("PASS "+name);}catch(Exception e){failures++;Console.WriteLine("FAIL "+name+": "+e.GetType().Name+" "+e.Message);}}
await Test("new generation cancels obsolete wait immediately",async()=>{var s=new Harness();var t=s.Begin();s.Config(2);await Cancelled(t);Check(s.Pending==0,"leak");});
await Test("streamstop cancels wait immediately",async()=>{var s=new Harness();var t=s.Begin();s.Stop(1);await Cancelled(t);Check(s.Pending==0,"leak");});
await Test("disconnect cancels and reconnect isolates request id",async()=>{var s=new Harness();var t=s.Begin();s.Disconnect();await Cancelled(t);s.Reconnect();var next=s.Begin();s.Result(1,1,1);Check(!next.IsCompleted,"old session accepted");s.Result(1,1,2);Check(await next,"new result rejected");});
await Test("duplicate/older config and stale stop preserve active wait",async()=>{var s=new Harness();var t=s.Begin();s.Config(1);s.Config(0);s.Stop(2);Check(!t.IsCompleted,"active wait cancelled");s.Result(1,1);Check(await t,"result missing");});
await Test("late old result cannot complete new generation",async()=>{var s=new Harness();var old=s.Begin();s.Config(2);await Cancelled(old);var next=s.Begin(2);s.Result(1,1);s.Result(2,1);Check(!next.IsCompleted,"stale result accepted");s.Result(2,2);Check(await next,"new result rejected");});
await Test("explicit rejection stays rejection",async()=>{var s=new Harness();var t=s.Begin();s.Result(1,1,accepted:false);Check(!await t,"forced acceptance");});
await Test("real timeout remains timeout and releases entry",async()=>{var s=new Harness();try{await s.Begin();throw new Exception("missing timeout");}catch(TimeoutException){}Check(s.Pending==0,"leak");});
await Test("100 config/result races terminate without leaked waits",async()=>{for(int i=0;i<100;i++){var s=new Harness();var t=s.Begin();await Task.WhenAll(Task.Run(()=>s.Config(2)),Task.Run(()=>s.Result(1,1)));try{await t.WaitAsync(TimeSpan.FromMilliseconds(120));}catch(OperationCanceledException){}Check(s.Pending==0,"leak");}});
Console.WriteLine($"RESULT {passed} passed, {failures} failed");return failures==0?0:1;
