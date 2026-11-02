using Velopack;
using Velopack.Sources;
bool restarted=false;
VelopackApp.Build().SetAutoApplyOnStartup(false).OnRestarted(_=>restarted=true).Run();
var root=Environment.GetEnvironmentVariable("LEVELING_UPDATE_TEST_ROOT") ?? throw new Exception("Test root required");
var manager=new UpdateManager(new SimpleFileSource(new DirectoryInfo(Path.Combine(root,"feed"))));
if(restarted){File.WriteAllText(Path.Combine(root,"restarted.txt"),manager.CurrentVersion+"|"+typeof(Program).Assembly.GetName().Version);return;}
try {
 if(!manager.IsInstalled)throw new Exception("Portable package not recognized");
 var update=await manager.CheckForUpdatesAsync() ?? throw new Exception("No update found");
 await manager.DownloadUpdatesAsync(update);
 File.WriteAllText(Path.Combine(root,"downloaded.txt"),update.TargetFullRelease.Version.ToString());
 manager.ApplyUpdatesAndRestart(update);
} catch(Exception e){File.WriteAllText(Path.Combine(root,"failed.txt"),e.ToString());Environment.ExitCode=1;}
