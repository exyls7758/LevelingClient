#nullable enable
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
 public void Disconnect(){lock(_sync){ CancelPendingSenderRecoveriesLocked(); _streamConfig=null; _sessionGeneration++;}}
 public void Reconnect(){lock(_sync){_streamConfig=new(){Generation=1};}}
private sealed class PendingSenderRecovery
        {
            public required int SessionGeneration { get; init; }
            public required long StreamGeneration { get; init; }
            public required TaskCompletionSource<bool> Completion { get; init; }
        }
private void HandleStreamConfig(
            ControlEnvelope message)
        {
            if (message.Sequence <= 0 || message.WireStreamId == 0) return;

            if (
                message.Width == 0 ||
                message.Height == 0 ||
                message.Width > 16384 ||
                message.Height > 16384)
            {
                return;
            }

            RemoteStreamConfig config =
                new()
                {
                    Width =
                        message.Width,

                    Height =
                        message.Height,

                    Fps =
                        Math.Clamp(
                            message.Fps,
                            1u,
                            1000u),

                    BitrateKbps =
                        message.BitrateKbps,

                    Generation =
                        message.Sequence, WireStreamId = message.WireStreamId
                };

            lock (_sync)
            {
                if (_role != RemoteSessionRole.Client) return;
                if (_streamConfig is not null &&
                    message.Sequence <= _streamConfig.Generation) return;
                _streamConfig = config;
                // Requests for the replaced generation can no longer receive a
                // valid result. Release their awaiters before notifying runtime.
                CancelPendingSenderRecoveriesLocked();
                _videoStartupErrorGeneration = config.Generation;
                _videoStartupError = "";
            }

            NotifySessionHandlersSafely(StreamConfigChanged);

            RaiseSessionChanged();
        }
private void HandleStreamStopped(ControlEnvelope message)
        {
            lock (_sync)
            {
                if (_role != RemoteSessionRole.Client || _streamConfig is null ||
                    message.Sequence != _streamConfig.Generation) return;
                _streamConfig = null;
                _expectedRemoteVideoReadyGeneration = 0;
                CancelPendingSenderRecoveriesLocked();
                _remoteVideoReady = false;
                _videoStartupErrorGeneration = 0;
                _videoStartupError = "";
                _remotePacketLossPercent = 0;
                _remoteVideoFps = 0;
                _remoteRxMbps = 0;
                _remotePendingFrames = 0;
                _remoteDecodeDrops = 0;
                _remoteDecodeStaleDrops = 0;
                _remoteReferenceWaitDrops = 0;
                _remotePresenterDrops = 0;
                _remotePresentedFrames = 0;
                _remoteCompleteFrames = _remoteDecodedFrames = 0;
                _remoteLastPresentAgeMs = -1;
                _hasRemoteTransportCounters = false;
                _lastRemoteReceivedPackets = 0;
                _lastRemoteReceivedBytes = 0;
                _lastRemoteSequenceGaps = 0;
                _lastRemoteIncompleteFrames = 0;
                _lastRemoteStatsTick = 0;
            }

            NotifySessionHandlersSafely(StreamStopped);
            RaiseSessionChanged();
        }
public async Task<bool> AwaitSenderRecoveryResultAsync(long streamGeneration,
            int sessionGeneration, long requestId, long presentedBaseline,
            TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (requestId <= 0 || timeout <= TimeSpan.Zero ||
                !IsCurrentVideoStream(streamGeneration, sessionGeneration,
                    RemoteSessionRole.Client))
                throw new InvalidOperationException("A solicitação de recuperação não pertence à sessão atual.");

            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                if (_sessionGeneration != sessionGeneration ||
                    _role != RemoteSessionRole.Client ||
                    _streamConfig?.Generation != streamGeneration ||
                    _pendingSenderRecoveries.ContainsKey(requestId))
                    throw new InvalidOperationException("A solicitação de recuperação ficou obsoleta.");
                _pendingSenderRecoveries[requestId] = new PendingSenderRecovery
                {
                    SessionGeneration = sessionGeneration,
                    StreamGeneration = streamGeneration,
                    Completion = completion
                };
            }

            try
            {
                if (!TrySendCriticalForSession(new ControlEnvelope
                {
                    Type = "videorecover", Sequence = streamGeneration,
                    RecoveryRequestId = requestId,
                    PresentedFrames = Math.Max(0, presentedBaseline)
                }, sessionGeneration))
                    throw new IOException("Não foi possível solicitar a recuperação do sender.");

                return await completion.Task.WaitAsync(timeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                lock (_sync)
                {
                    if (_pendingSenderRecoveries.TryGetValue(requestId, out PendingSenderRecovery? pending) &&
                        ReferenceEquals(pending.Completion, completion))
                        _pendingSenderRecoveries.Remove(requestId);
                }
            }
        }
private void CompleteSenderRecoveryResult(int sessionGeneration, ControlEnvelope message)
        {
            TaskCompletionSource<bool>? completion = null;
            bool accepted = string.Equals(message.Message, "ACCEPTED", StringComparison.Ordinal);
            lock (_sync)
            {
                if (_sessionGeneration != sessionGeneration || _role != RemoteSessionRole.Client ||
                    message.Sequence <= 0 || message.Sequence != _streamConfig?.Generation ||
                    message.RecoveryRequestId <= 0 ||
                    !_pendingSenderRecoveries.TryGetValue(message.RecoveryRequestId,
                        out PendingSenderRecovery? pending) ||
                    pending.SessionGeneration != sessionGeneration ||
                    pending.StreamGeneration != message.Sequence)
                    return;
                _pendingSenderRecoveries.Remove(message.RecoveryRequestId);
                completion = pending.Completion;
            }
            completion.TrySetResult(accepted);
            VideoIncidentRecorder.Instance.RecordEvent("CLIENT_SENDER_RECOVERY_RESULT", new
            {
                session = sessionGeneration, stream = message.Sequence,
                requestId = message.RecoveryRequestId, accepted
            });
        }
private void CancelPendingSenderRecoveriesLocked()
        {
            foreach (PendingSenderRecovery pending in _pendingSenderRecoveries.Values)
                pending.Completion.TrySetCanceled();
            _pendingSenderRecoveries.Clear();
        }
}