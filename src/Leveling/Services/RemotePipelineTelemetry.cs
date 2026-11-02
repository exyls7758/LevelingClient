using System;
using System.Globalization;

namespace Multiverse.Services
{
    internal sealed class RemotePipelineTelemetrySnapshot
    {
        public string Text { get; init; } = "Aguardando amostra.";
        public double? ReceiveFps { get; init; }
        public double? PresentFps { get; init; }
        public double? ReceiveMbps { get; init; }
        public bool PresenterAttached { get; init; }
        public double? PacketLossPercent { get; init; }
    }

    /// <summary>
    /// Read-only sampler of the process-wide runtime. Does not create any
    /// capture, encoder, transport or preview. A measured zero stays zero.
    /// Every counter is reset when its owning resource changes.
    /// </summary>
    internal sealed class RemotePipelineTelemetry
    {
        public static RemotePipelineTelemetry Instance { get; } = new();
        private readonly object _sync = new();
        private NativeCaptureSession? _capture;
        private IVideoTransport? _transport;
        private NativeNvdecPlayer? _decoder;
        private long _lastTick;
        private double _arrived, _published, _encoded, _sent, _received, _bytes, _presented;
        private long _incomplete, _gaps;
        private RemotePipelineTelemetrySnapshot _cached = new();
        private RemotePipelineTelemetry() { }

        public RemotePipelineTelemetrySnapshot GetSnapshot()
        {
            lock (_sync)
            {
                long now = Environment.TickCount64;
                if (_lastTick != 0 && now - _lastTick < 400)
                    return _cached;
                try
                {
                    RemoteStreamingState state = RemoteStreamingRuntime.Instance.GetState();
                    RemoteSessionSnapshot session = RemoteSessionService.Instance.GetSnapshot();
                    CapturePipelineStats? c = state.Capture?.GetCapturePipelineStatsDiagnostic();
                    EncoderStats? e = state.Capture?.GetEncoderStatsDiagnostic();
                    TransportSnapshot? t = state.Transport?.GetSnapshot();
                    NativeNvdecSnapshot? d = state.Decoder?.GetSnapshot();
                    bool reset = !ReferenceEquals(_capture, state.Capture) ||
                        !ReferenceEquals(_transport, state.Transport) ||
                        !ReferenceEquals(_decoder, state.Decoder);
                    double seconds = !reset && _lastTick > 0
                        ? Math.Max(0.001, (now - _lastTick) / 1000.0) : 0;
                    _capture = state.Capture;
                    _transport = state.Transport;
                    _decoder = state.Decoder;
                    _lastTick = now;

                    double? arrived = Rate(c?.BackendFramesArrived, ref _arrived, seconds);
                    double? published = Rate(c?.BackendFramesPublished, ref _published, seconds);
                    double? encoded = Rate(e?.EncodedFrames, ref _encoded, seconds);
                    double? sent = Rate(t?.SentFrames, ref _sent, seconds);
                    double? received = Rate(t?.ReceivedFrames, ref _received, seconds);
                    double? bytes = Rate(t?.ReceivedBytes, ref _bytes, seconds);
                    double? presented = Rate(d?.D3D11PresentedFrames, ref _presented, seconds);
                    double? rxMbps = bytes * 8.0 / 1_000_000.0;
                    bool attached = d?.D3D11Attached == true;
                    long incomplete = t?.IncompleteFramesDropped ?? 0;
                    long gaps = t?.SequenceGaps ?? 0;
                    long incompleteDelta = !reset && incomplete >= _incomplete ? incomplete - _incomplete : 0;
                    long gapDelta = !reset && gaps >= _gaps ? gaps - _gaps : 0;
                    _incomplete = incomplete;
                    _gaps = gaps;
                    string F(double? value, string format = "0.0") =>
                        value.HasValue && double.IsFinite(value.Value)
                            ? value.Value.ToString(format, CultureInfo.InvariantCulture) : "--";

                    string text =
                        $"SESSÃO REAL • {session.Role} • {(session.Connected ? "conectada" : "desconectada")} • geração {session.SessionGeneration}\n" +
                        $"REDE • RTT {session.RoundTripMs:0.0} ms • base {session.BaseRoundTripMs:0.0} • fila {session.QueueDelayMs:0.0} ms • jitter {session.JitterMs:0.0} ms • loss {session.RemotePacketLossPercent:0.00}% • feedback {(session.RemoteTelemetryAgeMs < 0 ? "--" : session.RemoteTelemetryAgeMs.ToString())} ms\n" +
                        $"WGC • chegada {F(arrived)} FPS • publicação {F(published)} FPS • último callback {F(c?.BackendLastCallbackAgoMs)} ms • publish {F(c?.LastPublishAgoMs)} ms\n" +
                        $"NVENC • {(e?.Running == true ? "ativo" : "inativo")} • {F(encoded)} FPS • saída há {F(e?.LastOutputAgoMs)} ms • submit {F(e?.LastSubmitAgoMs)} ms • in-flight {e?.InFlightFrames ?? 0}/{e?.InputSlots ?? 0} • fila {e?.QueuedPackets ?? 0} • oldest {F(e?.OldestInFlightMs)} ms\n" +
                        $"UDP • {(t?.Running == true ? "ativo" : "inativo")} • TX {F(sent)} FPS / {F(t?.ActualSendMbps)} Mbps • RX {F(received)} FPS / {F(rxMbps)} Mbps • fila {t?.PendingFrames ?? 0}\n" +
                        $"PACING • {t?.CongestionState ?? "--"} • atual {F(t?.CurrentPacingMbps)} / alvo {F(t?.TargetPacingMbps)} Mbps • encoder {F(t?.EncoderTargetMbps)} Mbps • backlog {F(t?.PacerBacklogMs)} ms\n" +
                        $"PERDAS • incompletos +{incompleteDelta} • gaps +{gapDelta} (janela) • total {incomplete}/{gaps} • FEC {t?.FecRecoveredFragments ?? 0} • origem-estranha {t?.ForeignPacketsDropped ?? 0} • drops antes TX {t?.FramesDroppedBeforeSend ?? 0}\n" +
                        $"NVDEC • submit {d?.NativeFramesSubmitted ?? 0} • fila {d?.QueueDepth ?? 0} • waiting-keyframe {d?.WaitingForKeyFrameDrops ?? 0} • IDR recovery {d?.RecoveryIdrRequests ?? 0}\n" +
                        $"ATRASO LOCAL • RX→NVDEC fila média {F(d?.LocalQueueAgeAverageMs)} ms • p95 {F(d?.LocalQueueAgeP95Ms)} • p99 {F(d?.LocalQueueAgeP99Ms)} • não é input-to-photon\n" +
                        $"PRESENT • {(attached ? "anexado" : "inativo")} • {F(presented)} FPS • age {F(d?.D3D11PresenterActivityAgeMs)} ms • frame age {F(d?.D3D11AverageFrameAgeAtPresentMs)} ms • failures {d?.D3D11PresentFailures ?? 0} • timeouts {d?.D3D11PresentWaitTimeouts ?? 0}\n" +
                        $"ERROS • vídeo {(string.IsNullOrWhiteSpace(session.VideoStartupError) ? "--" : session.VideoStartupError)} • transporte {t?.LastError ?? "--"}";
                    _cached = new RemotePipelineTelemetrySnapshot
                    {
                        Text = text, ReceiveFps = received, PresentFps = presented,
                        ReceiveMbps = rxMbps, PresenterAttached = attached,
                        PacketLossPercent = session.Connected && t is { Running: true, ReceivedPackets: > 0 } ? t.EstimatedPacketLossPercent : null
                    };
                }
                catch (Exception ex)
                {
                    // A disposed/recovering native object must not break the UI.
                    _lastTick = 0;
                    _cached = new RemotePipelineTelemetrySnapshot
                    {
                        Text = "Telemetria aguardando recuperação: " + ex.Message
                    };
                }
                return _cached;
            }
        }

        private static double? Rate(double? value, ref double previous, double seconds)
        {
            if (!value.HasValue)
            {
                previous = 0;
                return null;
            }
            double current = value.Value;
            double? result = seconds > 0 && current >= previous
                ? (current - previous) / seconds : null;
            previous = current;
            return result;
        }

        public string GetDisplayText() => GetSnapshot().Text;
    }
}
