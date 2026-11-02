using System.Net;
var session=new RemoteSessionService();
for(uint n=1;n<=20;n++) {
 session.Config=new(){Generation=n,WireStreamId=9000+n,BitrateKbps=20000,Fps=60};
 var tx=new RemoteHostVideoTransport(new(),session.Endpoint,session);
 tx.Start();
 if(VideoUdpSender.LastWire!=9000+n) throw new Exception("wrong wire identity");
 tx.Stop();
 session.Session++;
 try {tx.Start();throw new Exception("stale sender accepted");} catch(InvalidOperationException){}
}
session.Config=new(){Generation=21,WireStreamId=9021};
var old=new RemoteHostVideoTransport(new(),session.Endpoint,session);
session.Config=new(){Generation=22,WireStreamId=9022};
try {old.Start();throw new Exception("old stream accepted");} catch(InvalidOperationException){}
var wrongEndpoint=new RemoteHostVideoTransport(new(),new IPEndPoint(IPAddress.Loopback,40001),session);
try {wrongEndpoint.Start();throw new Exception("wrong endpoint accepted");} catch(InvalidOperationException){}
session.Config=null;
var empty=new RemoteHostVideoTransport(new(),session.Endpoint,session);
try {empty.Start();throw new Exception("unnegotiated sender accepted");} catch(InvalidOperationException){}
Console.WriteLine("PASS production constructor + Start + GetStreamConfig: 20 identities; stale session, stale stream, endpoint mismatch and missing identity rejected");
internal sealed class RemoteStreamConfig
    {
        public uint Width { get; init; }
        public uint Height { get; init; }
        public uint Fps { get; init; }
        public uint BitrateKbps { get; init; }
        public long Generation { get; init; }
        public uint WireStreamId { get; init; }

        public override string ToString() =>
            $"{Width}x{Height} @ {Fps} FPS • {BitrateKbps / 1000.0:0.#} Mbps";
    }
enum RemoteSessionRole { Host }
class RemoteSessionService {
 readonly object _sync=new(); RemoteStreamConfig? _streamConfig;
 public RemoteStreamConfig? Config {get=>_streamConfig;set=>_streamConfig=value;}
 public int Session=1; public IPEndPoint Endpoint=new(IPAddress.Loopback,40000);
 public RemoteSessionSnapshot GetSnapshot()=>new(){SessionGeneration=Session,StreamConfig=Config};
 public IPEndPoint GetRemoteVideoEndpoint()=>Endpoint;
 public bool IsCurrentVideoStream(long stream,int session,RemoteSessionRole role)=>session==Session && stream>0 && stream==Config?.Generation;
 public uint NextVideoPacketSequence()=>1;
public RemoteStreamConfig? GetStreamConfig()
        {
            lock (_sync)
            {
                if (_streamConfig is null)
                    return null;

                return new RemoteStreamConfig
                {
                    Width =
                        _streamConfig.Width,
                    Height =
                        _streamConfig.Height,
                    Fps =
                        _streamConfig.Fps,
                    BitrateKbps =
                        _streamConfig.BitrateKbps,
                    Generation =
                        _streamConfig.Generation,
                    WireStreamId =
                        _streamConfig.WireStreamId
                };
            }
        }}
class RemoteSessionSnapshot {
 public int SessionGeneration; public RemoteStreamConfig? StreamConfig;
 public bool Connected=>true;
 public double RoundTripMs,BaseRoundTripMs,JitterMs,RemotePacketLossPercent,RemoteRxMbps,RemoteTelemetryAgeMs,RemoteVideoFps,RemotePendingFrames,RemoteDecodeDrops;
}
class NativeCaptureSession {}
class VideoSenderNetworkFeedback { public bool Connected; public double RoundTripMs,BaseRoundTripMs,JitterMs,PacketLossPercent,RemoteRxMbps,FeedbackAgeMs,RemoteVideoFps,RemotePendingFrames,RemoteDecodeDrops; }
class VideoUdpSender {
 public static uint LastWire; public bool Running;
 public VideoUdpSender(NativeCaptureSession c,IPEndPoint p,uint bitrate,uint fps,Func<VideoSenderNetworkFeedback> feedback,uint wire,Func<uint> next){LastWire=wire;}
 public void Start(){Running=true;} public void Stop(){Running=false;} public void Dispose(){}
}
class RemoteHostVideoTransport {
 readonly NativeCaptureSession _capture; readonly IPEndPoint _remote; readonly RemoteSessionService _session;
 readonly int _sessionGeneration; readonly long _streamGeneration;
 VideoUdpSender? _sender; bool _disposed=false;
 public bool Running=>_sender?.Running==true;
public RemoteHostVideoTransport(
            NativeCaptureSession capture,
            IPEndPoint remote,
            RemoteSessionService session)
        {
            _capture =
                capture ??
                throw new ArgumentNullException(
                    nameof(capture));

            _remote =
                remote ??
                throw new ArgumentNullException(
                    nameof(remote));

            _session =
                session ??
                throw new ArgumentNullException(
                    nameof(session));
            var identity = session.GetSnapshot();
            _sessionGeneration = identity.SessionGeneration;
            _streamGeneration = identity.StreamConfig?.Generation ?? 0;
        }
public void Start()
        {
            if (_disposed)
                throw new ObjectDisposedException(
                    nameof(RemoteHostVideoTransport));

            if (Running)
                return;

            Stop();

            RemoteStreamConfig? config =
                _session.GetStreamConfig();

            if (config is null || config.WireStreamId == 0) throw new InvalidOperationException("No negotiated UDP stream identity");
            if (config.Generation != _streamGeneration ||
                !_session.IsCurrentVideoStream(_streamGeneration, _sessionGeneration, RemoteSessionRole.Host) ||
                !_remote.Equals(_session.GetRemoteVideoEndpoint()))
                throw new InvalidOperationException("UDP sender belongs to a retired session or stream");
            uint bitrateKbps = config.BitrateKbps;

            uint fps = config.Fps;

            var sender =
                new VideoUdpSender(
                    _capture,
                    _remote,
                    bitrateKbps,
                    fps,
                    () =>
                    {
                        RemoteSessionSnapshot snapshot =
                            _session.GetSnapshot();

                        return new VideoSenderNetworkFeedback
                        {
                            Connected =
                                snapshot.Connected,

                            RoundTripMs =
                                snapshot.RoundTripMs,

                            BaseRoundTripMs =
                                snapshot.BaseRoundTripMs,

                            JitterMs =
                                snapshot.JitterMs,

                            PacketLossPercent =
                                snapshot.RemotePacketLossPercent,

                            RemoteRxMbps =
                                snapshot.RemoteRxMbps,

                            FeedbackAgeMs =
                                snapshot.RemoteTelemetryAgeMs,

                            RemoteVideoFps =
                                snapshot.RemoteVideoFps,

                            RemotePendingFrames =
                                snapshot.RemotePendingFrames,

                            RemoteDecodeDrops =
                                snapshot.RemoteDecodeDrops
                        };
                    }, config.WireStreamId, _session.NextVideoPacketSequence);

            _sender = sender;
            try { sender.Start(); }
            catch
            {
                sender.Dispose();
                if (ReferenceEquals(_sender, sender)) _sender = null;
                throw;
            }
        }
public void Stop()
        {
            _sender?.Stop();
            _sender?.Dispose();
            _sender = null;
        }
}