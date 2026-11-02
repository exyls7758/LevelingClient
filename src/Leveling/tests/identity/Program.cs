
var session = new Session();
if (session.GetStreamConfig() != null) throw new Exception("empty session");
for (uint n=1; n<=20; n++) {
 var current = new RemoteStreamConfig { Width=2560,Height=1440,Fps=60,BitrateKbps=20000,Generation=n,WireStreamId=9000+n };
 session.Set(current);
 var copy=session.GetStreamConfig()!;
 foreach(var property in typeof(RemoteStreamConfig).GetProperties())
  if (!Equals(property.GetValue(current),property.GetValue(copy))) throw new Exception("Lost negotiated property: "+property.Name);
 session.Set(null);
 if(session.GetStreamConfig()!=null) throw new Exception("stale session");
 if(copy.WireStreamId!=9000+n) throw new Exception("snapshot mutated");
}
Console.WriteLine("PASS exact production GetStreamConfig: all properties preserved, 20 generations, cleared session and stable snapshots");
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
class Session { readonly object _sync=new(); RemoteStreamConfig? _streamConfig; public void Set(RemoteStreamConfig? c) {lock(_sync) _streamConfig=c;}
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
        }
}