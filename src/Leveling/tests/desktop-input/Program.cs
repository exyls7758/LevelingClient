using Multiverse.Services;

var failures = 0;
Run("ordered key transitions and duplicate suppression", () => {
    var s = new FakeSink(); var r = new DesktopInputReceiver(s);
    Yes(r.Apply(P("key", 1, 1, 65, true), 0));
    No(r.Apply(P("key", 1, 1, 65, true), 1));
    Yes(r.Apply(P("key", 1, 2, 65, true), 2));
    Yes(r.Apply(P("key", 1, 3, 65, false), 3));
    No(r.Apply(P("key", 1, 2, 65, true), 4));
    Eq("K65:True,K65:False", s.Text);
});
Run("invalid packets cannot poison epoch or sequence", () => {
    var s = new FakeSink(); var r = new DesktopInputReceiver(s);
    foreach (var p in new[] { P("key", 99, 99, 0, true), P("key", 99, 99, 256, true), P("key", 99, 99, 1, true), P("button", 99, 99, 6, true), P("button", 99, 99, 0, true), P("unknown", 99, 99), P("key", 0, 1, 65, true), P("key", 1, -1, 65, true) }) No(r.Apply(p, 0));
    Yes(r.Apply(P("key", 1, 1, 65, true), 0)); Eq("K65:True", s.Text);
});
Run("movement coalesces independently of ordered edges", () => {
    var s = new FakeSink(); var r = new DesktopInputReceiver(s);
    Yes(r.Apply(P("move", 1, 10, x: 12, y: -4, wheel: -120), 0));
    Yes(r.Apply(P("key", 1, 2, 65, true), 1));
    Yes(r.Apply(P("move", 1, 20, x: 18, y: 2, wheel: -240), 2));
    No(r.Apply(P("move", 1, 15, x: 15), 3));
    Yes(r.Apply(P("move", 1, 21, x: 18, y: 2, wheel: -240), 4));
    Eq("M12:-4,W-120,K65:True,M6:6,W-120", s.Text);
});
Run("reset only releases owned input and closes epoch", () => {
    var s = new FakeSink(); var r = new DesktopInputReceiver(s);
    r.Apply(P("key", 1, 1, 66, false), 0);
    r.Apply(P("key", 1, 2, 65, true), 1);
    r.Apply(P("button", 1, 3, 5, true), 2);
    Yes(r.Apply(P("reset", 1, 4), 3)); r.Reset();
    No(r.Apply(P("key", 1, 100, 65, true), 4));
    Eq("K65:True,B5:True,K65:False,B5:False", s.Text);
});
Run("watchdog times out at 500ms and old traffic cannot rearm", () => {
    var s = new FakeSink(); var r = new DesktopInputReceiver(s);
    r.Apply(P("key", 5, 1, 65, true), 100);
    r.Tick(599); Eq("K65:True", s.Text);
    r.Tick(600); Eq("K65:True,K65:False", s.Text);
    No(r.Apply(P("heartbeat", 5, 2), 601));
    No(r.Apply(P("key", 5, 3, 65, true), 602));
    No(r.Apply(P("key", 4, 99, 65, true), 603));
    Yes(r.Apply(P("key", 6, 1, 66, true), 604));
    Eq("K65:True,K65:False,K66:True", s.Text);
});
Run("receive path enforces timeout even without timer tick", () => {
    var s = new FakeSink(); var r = new DesktopInputReceiver(s);
    r.Apply(P("key", 1, 1, 65, true), 0);
    No(r.Apply(P("key", 1, 2, 66, true), 500));
    Eq("K65:True,K65:False", s.Text);
});
Run("heartbeat refreshes lease but duplicate and invalid do not", () => {
    var s = new FakeSink(); var r = new DesktopInputReceiver(s);
    r.Apply(P("key", 1, 1, 65, true), 0);
    Yes(r.Apply(P("heartbeat", 1, 2), 400));
    No(r.Apply(P("heartbeat", 1, 2), 800));
    No(r.Apply(P("key", 1, 3, 0, true), 850));
    r.Tick(899); Eq("K65:True", s.Text);
    r.Tick(900); Eq("K65:True,K65:False", s.Text);
});
Run("reconnect releases previous ownership and resets totals", () => {
    var s = new FakeSink(); var r = new DesktopInputReceiver(s);
    r.Apply(P("key", 1, 1, 65, true), 0);
    r.Apply(P("move", 1, 10, x: 100), 1);
    Yes(r.Apply(P("move", 2, 1, x: 3), 2));
    No(r.Apply(P("move", 1, 999, x: 200), 3));
    Eq("K65:True,M100:0,K65:False,M3:0", s.Text);
});
Run("out of range movement rejected without consuming totals", () => {
    var s = new FakeSink(); var r = new DesktopInputReceiver(s);
    No(r.Apply(P("move", 1, 1, x: long.MaxValue), 0));
    Yes(r.Apply(P("move", 1, 2, x: -2), 1)); Eq("M-2:0", s.Text);
});
Run("disconnect reset prevents replay", () => {
    var s = new FakeSink(); var r = new DesktopInputReceiver(s);
    r.Apply(P("button", 1, 1, 1, true), 0); r.Reset(); r.Reset();
    No(r.Apply(P("button", 1, 2, 1, true), 1));
    Yes(r.Apply(P("button", 2, 1, 1, true), 2));
    Eq("B1:True,B1:False,B1:True", s.Text);
});
Run("failed key-up remains owned for reset retry", () => {
    var s=new FailingSink();var r=new DesktopInputReceiver(s);
    r.Apply(P("key",1,1,65,true),0);s.FailRelease=true;
    try{r.Apply(P("key",1,2,65,false),1);}catch(InvalidOperationException){}
    s.FailRelease=false;r.Reset();Eq("True,False",string.Join(',',s.Successful));
});
Run("failed watchdog release retries on following tick", () => {
    var s=new FailingSink();var r=new DesktopInputReceiver(s);
    r.Apply(P("key",1,1,65,true),0);s.FailRelease=true;
    try{r.Tick(500);}catch(InvalidOperationException){}
    s.FailRelease=false;r.Tick(600);Eq("True,False",string.Join(',',s.Successful));
});
Console.WriteLine($"Desktop input: {failures} failed");
return failures == 0 ? 0 : 1;

void Run(string name, Action test) { try { test(); Console.WriteLine("PASS " + name); } catch(Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); } }
static void Yes(bool value) { if (!value) throw new Exception("expected acceptance"); }
static void No(bool value) { if (value) throw new Exception("expected rejection"); }
static void Eq(string expected, string actual) { if (expected != actual) throw new Exception($"expected [{expected}], actual [{actual}]"); }
static DesktopInputPacket P(string kind, long epoch, long seq, int code = 0, bool down = false, long x = 0, long y = 0, long wheel = 0) => new() { Kind=kind, Epoch=epoch, Sequence=seq, Code=code, Down=down, X=x, Y=y, Wheel=wheel };
sealed class FakeSink : IDesktopInputSink {
    private readonly List<string> events = new();
    public string Text => string.Join(',', events);
    public void Key(int key, bool down) => events.Add($"K{key}:{down}");
    public void Button(int code, bool down) => events.Add($"B{code}:{down}");
    public void Move(int dx, int dy) => events.Add($"M{dx}:{dy}");
    public void Wheel(int delta) => events.Add($"W{delta}");
}
sealed class FailingSink:IDesktopInputSink {
 public bool FailRelease;public List<bool> Successful=new();
 public void Key(int code,bool down){if(!down&&FailRelease)throw new InvalidOperationException("simulated OS failure");Successful.Add(down);}
 public void Button(int code,bool down)=>Key(code,down);
 public void Move(int x,int y){}public void Wheel(int d){}
}
