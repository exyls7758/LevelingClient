from pathlib import Path
import re
here=Path(__file__).resolve().parent
source=(here.parents[1]/'Services/RemoteSessionService.cs').read_text(encoding='utf-8-sig')
def extract(marker):
    start=source.index(marker); brace=source.index('{',start); depth=1; end=brace+1
    while depth:
        depth += (source[end]=='{')-(source[end]=='}'); end+=1
    return source[start:end]
markers=['private sealed class PendingSenderRecovery','private void HandleStreamConfig(',
         'private void HandleStreamStopped(', 'public async Task<bool> AwaitSenderRecoveryResultAsync(',
         'private void CompleteSenderRecoveryResult(']
if 'private void CancelPendingSenderRecoveriesLocked(' in source:
    markers+=['private void CancelPendingSenderRecoveriesLocked(']
methods='\n'.join(extract(m) for m in markers)
# Execute exactly the production disconnect cancellation block, without sockets.
close=extract('private void CloseActiveSocketLocked(')
if 'CancelPendingSenderRecoveriesLocked();' in close: disconnect='CancelPendingSenderRecoveriesLocked();'
else: disconnect=close[close.index('foreach (PendingSenderRecovery'):close.index('_pendingSenderRecoveries.Clear();')+len('_pendingSenderRecoveries.Clear();')]
stub='''#nullable enable
using System; using System.IO; using System.Threading; using System.Threading.Tasks; using System.Collections.Generic;
enum RemoteSessionRole { Client, Host }
class RemoteStreamConfig { public uint Width,Height,Fps,BitrateKbps,WireStreamId; public long Generation; }
class ControlEnvelope { public string Type="",Message=""; public uint Width=1920,Height=1080,Fps=60,BitrateKbps=20000,WireStreamId=1; public long Sequence,RecoveryRequestId,PresentedFrames; }
class VideoIncidentRecorder { public static VideoIncidentRecorder Instance=new(); public void RecordEvent(string name,object data){} }
partial class Harness {
 readonly object _sync=new(); int _sessionGeneration=1; RemoteSessionRole _role=RemoteSessionRole.Client;
 RemoteStreamConfig? _streamConfig=new(){Generation=1}; readonly Dictionary<long,PendingSenderRecovery> _pendingSenderRecoveries=new();
 long _videoStartupErrorGeneration,_expectedRemoteVideoReadyGeneration,_lastRemoteReceivedPackets,_lastRemoteReceivedBytes,_lastRemoteSequenceGaps,_lastRemoteIncompleteFrames,_lastRemoteStatsTick;
 double _remotePacketLossPercent,_remoteVideoFps,_remoteRxMbps,_remotePendingFrames,_remoteDecodeDrops,_remoteDecodeStaleDrops,_remoteReferenceWaitDrops,_remotePresenterDrops,_remotePresentedFrames,_remoteCompleteFrames,_remoteDecodedFrames,_remoteLastPresentAgeMs;
 bool _remoteVideoReady,_hasRemoteTransportCounters; string _videoStartupError="";
 event EventHandler? StreamConfigChanged,StreamStopped;
 void NotifySessionHandlersSafely(EventHandler? handlers)=>handlers?.Invoke(this,EventArgs.Empty);
 void RaiseSessionChanged(){}
 bool IsCurrentVideoStream(long g,int s,RemoteSessionRole role){lock(_sync)return s==_sessionGeneration && role==_role && g==_streamConfig?.Generation;}
 bool TrySendCriticalForSession(ControlEnvelope e,int s)=>true;
 public Task<bool> Begin(long id=1)=>AwaitSenderRecoveryResultAsync(_streamConfig!.Generation,_sessionGeneration,id,0,TimeSpan.FromMilliseconds(250));
 public void Config(long g)=>HandleStreamConfig(new(){Sequence=g});
 public void Stop(long g)=>HandleStreamStopped(new(){Sequence=g});
 public void Result(long id,long g,int s=1,bool accepted=true)=>CompleteSenderRecoveryResult(s,new(){Sequence=g,RecoveryRequestId=id,Message=accepted?"ACCEPTED":"REJECTED"});
 public int Pending {get {lock(_sync)return _pendingSenderRecoveries.Count;}}
 public void Disconnect(){lock(_sync){ DISCONNECT _streamConfig=null; _sessionGeneration++;}}
 public void Reconnect(){lock(_sync){_streamConfig=new(){Generation=1};}}
'''.replace('DISCONNECT',disconnect)
(here/'Production.generated.cs').write_text(stub+methods+'\n}',encoding='utf-8')
(here/'RecoveryWaits.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><NoWarn>CS0169;CS0414</NoWarn></PropertyGroup></Project>')
print('Extracted current production recovery methods and disconnect cancellation block')
