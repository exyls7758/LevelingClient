using System.Net;
using Multiverse.Services;
static void Allowed(ConnectionPath mode, string value, bool expected)
{
    bool actual;
    try { ConnectionPathPolicy.Validate(mode, IPAddress.Parse(value)); actual=true; }
    catch (InvalidOperationException) { actual=false; }
    if(actual!=expected)throw new Exception($"Route policy failed {mode} {value}");
    Console.WriteLine($"PASS {mode} {value} allowed={expected}");
}
foreach(var ip in new[]{"100.64.0.1","100.127.255.254"}) {
    Allowed(ConnectionPath.Tailscale,ip,true); Allowed(ConnectionPath.LocalDirect,ip,false);
}
foreach(var ip in new[]{"10.0.0.1","172.16.0.1","172.31.255.254","192.168.1.20"}) {
    Allowed(ConnectionPath.LocalDirect,ip,true); Allowed(ConnectionPath.Tailscale,ip,false);
}
foreach(var ip in new[]{"100.63.255.254","100.128.0.1","172.32.0.1","192.169.1.1","8.8.8.8","127.0.0.1","224.0.0.1","::1","fd00::1"}) {
    Allowed(ConnectionPath.LocalDirect,ip,false); Allowed(ConnectionPath.Tailscale,ip,false);
}
if(!ConnectionPathPolicy.Describe(IPAddress.Parse("100.64.1.1")).Contains("a verificar"))
    throw new Exception("Overlay address must not claim a proven direct route");
Console.WriteLine("PASS overlay address does not imply direct WAN or verified UDP");
