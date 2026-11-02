using System.Text.Json;
namespace Multiverse.Services;
// Only OS injection, settings and the authenticated-session boundary are faked.
// The service, lease receiver, mapping and diagnostic policy are production files.
internal enum RemoteSessionRole { None,Host,Client }
internal sealed class Snapshot
{
 public bool Connected=true,RemoteInputAllowed=true;
 public int SessionGeneration;
 public RemoteSessionRole Role;
}
internal sealed class RemoteSessionService
{
 public static RemoteSessionService Instance {get;}=new();
 public Snapshot Snapshot=new();
 public bool Writable=true;
 public readonly List<DesktopInputPacket> Sent=new();
 public event Action<DesktopInputPacket,int>? DesktopInputReceived;
 public event EventHandler? SessionChanged;
 public Snapshot GetSnapshot()=>Snapshot;
 public bool TryPublishDesktopInput(DesktopInputPacket p,int gen)
 {
  if(!Writable||!Snapshot.Connected||gen!=Snapshot.SessionGeneration)return false;
  Sent.Add(JsonSerializer.Deserialize<DesktopInputPacket>(JsonSerializer.Serialize(p))!);return true;
 }
 public void Change()=>SessionChanged?.Invoke(this,EventArgs.Empty);
 public void Deliver(DesktopInputPacket p,int? gen=null)=>DesktopInputReceived?.Invoke(JsonSerializer.Deserialize<DesktopInputPacket>(JsonSerializer.Serialize(p))!,gen??Snapshot.SessionGeneration);
}
internal sealed class MultiverseSettingsService
{
 public static MultiverseSettingsService Instance {get;}=new();
 public string GetString(string k,string d)=>d;
 public bool GetBool(string k,bool d)=>d;
 public void Set(string k,object v){}
}
internal sealed class GamepadTuningService
{
 public static GamepadTuningService Instance {get;}=new();
 public bool RemoteInputEnabled=true;
}
internal sealed class WindowsDesktopInputSink:IDesktopInputSink
{
 public static readonly List<string> Events=new();
 public void Key(int k,bool d)=>Events.Add($"K{k}:{d}");
 public void Button(int k,bool d)=>Events.Add($"B{k}:{d}");
 public void Move(int x,int y)=>Events.Add($"M{x}:{y}");
 public void Wheel(int d)=>Events.Add($"W{d}");
}
