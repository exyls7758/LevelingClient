using Multiverse.Services;

int failures = 0;
void Test(string name, Action body) {
    try { body(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + ex.Message); }
}
void Check(bool condition) { if (!condition) throw new Exception("assertion failed"); }
Test("logged out is unavailable", () => Check(!TailscalePeerDiscoveryService.Parse("{\"BackendState\":\"NeedsLogin\"}").Available));
Test("stopped is unavailable", () => Check(!TailscalePeerDiscoveryService.Parse("{\"BackendState\":\"Stopped\"}").Available));
Test("missing backend is not proof of connectivity", () => Check(!TailscalePeerDiscoveryService.Parse("{}").Available));
Test("running empty tailnet is available", () => Check(TailscalePeerDiscoveryService.Parse("{\"BackendState\":\"Running\"}").Available));
Test("malformed JSON fails safely", () => Check(!TailscalePeerDiscoveryService.Parse("{").Available));
Test("100 prefix outside CGNAT is rejected", () => Check(TailscalePeerDiscoveryService.Parse("""
{"BackendState":"Running","Peer":{"a":{"TailscaleIPs":["100.1.2.3"],"Online":true}}}
""").Peers.Count == 0));
Test("real CGNAT peer accepted", () => Check(TailscalePeerDiscoveryService.Parse("""
{"BackendState":"Running","Peer":{"a":{"TailscaleIPs":["100.64.0.1"],"Online":true}}}
""").Peers.Count == 1));
Test("direct endpoint outranks available relay", () => Check(TailscalePeerDiscoveryService.Parse("""
{"BackendState":"Running","Peer":{"a":{"TailscaleIPs":["100.64.0.1"],"Online":true,"Active":true,"CurAddr":"192.0.2.1:41641","Relay":"gru"}}}
""").Peers.Single().RouteText.Contains("direta")));
Test("inactive peer does not claim active direct route", () => Check(TailscalePeerDiscoveryService.Parse("""
{"BackendState":"Running","Peer":{"a":{"TailscaleIPs":["100.64.0.1"],"Online":true,"Active":false,"CurAddr":"192.0.2.1:41641","Relay":"gru"}}}
""").Peers.Single().RouteText.Contains("confirmada")));
Console.WriteLine($"Refinement failures: {failures}");
return failures == 0 ? 0 : 1;
