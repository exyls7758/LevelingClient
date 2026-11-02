using System.Text.Json; using Multiverse.Services;
var trace=JsonSerializer.Deserialize<EncoderStallEvidence[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"trace.json")))!;
var old=new LegacyDetector();var current=new EncoderStallDetector();long? before=null,after=null;
foreach(var s in trace){if(old.Observe(s))before??=s.Now;if(current.Observe(s))after??=s.Now;}
if(before is null || after is null || after>=before)throw new Exception("Expected earlier confirmed terminal detection in incident trace");
Console.WriteLine($"PASS recorded host evidence: baselineTick={before}, correctedTick={after}, earlierMs={before-after}, samples={trace.Length}");
Console.WriteLine("Policy replay only: not a live recovery or end-to-end latency benchmark.");