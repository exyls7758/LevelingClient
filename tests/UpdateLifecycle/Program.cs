using Velopack;
using Velopack.Sources;
using Multiverse.Services;
bool restarted=false;
VelopackApp.Build().SetAutoApplyOnStartup(false).OnRestarted(_=>restarted=true).Run();
var root=Environment.GetEnvironmentVariable("LEVELING_UPDATE_TEST_ROOT") ?? throw new Exception("Test root required");
var manager=new UpdateManager(new SimpleFileSource(new DirectoryInfo(Path.Combine(root,"feed"))));
if(restarted){File.WriteAllText(Path.Combine(root,"restarted.txt"),manager.CurrentVersion+"|"+typeof(Program).Assembly.GetName().Version);return;}
try {
 if(!manager.IsInstalled)throw new Exception("Portable package not recognized");
 if(Environment.GetEnvironmentVariable("LEVELING_UPDATE_TEST_ROLLBACK")=="1") {
  var backend=new ReleaseUpdateBackend(new SimpleFileSource(new DirectoryInfo(Path.Combine(root,"feed"))));
  var releases=await backend.GetPreviousAsync();
  if(releases.Count!=1 || releases[0].Version!="1.0.0")throw new Exception("Invalid rollback selection");
  await backend.SelectPreviousAsync("1.0.0");
  await backend.DownloadAsync(_=>{},default);
  File.WriteAllText(Path.Combine(root,"downloaded.txt"),"1.0.0");
  backend.Apply();return;
 }
 var update=await manager.CheckForUpdatesAsync() ?? throw new Exception("No update found");
 await manager.DownloadUpdatesAsync(update);
 File.WriteAllText(Path.Combine(root,"downloaded.txt"),update.TargetFullRelease.Version.ToString());
 manager.ApplyUpdatesAndRestart(update);
} catch(Exception e){File.WriteAllText(Path.Combine(root,"failed.txt"),e.ToString());Environment.ExitCode=1;}
