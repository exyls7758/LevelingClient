using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;

namespace Multiverse.Services
{
    internal sealed class DecoderSnapshot
    {
        public bool Running { get; init; }
        public bool FormatReady { get; init; }
        public bool MediaOpened { get; init; }

        public int SequenceHeaderBytes { get; init; }
        public long BootstrapFailures { get; init; }

        public long NetworkFramesReceived { get; init; }
        public long SamplesSubmitted { get; init; }
        public long SamplesRendered { get; init; }

        public long StaleFramesDropped { get; init; }
        public long WaitingForKeyFrameDrops { get; init; }

        public double LastSampleLagMs { get; init; }
        public double AverageSampleLagMs { get; init; }

        public string LastError { get; init; } = "";
    }

    /// <summary>
    /// Core 05.1:
    /// bootstrap correto de um stream H.264 Annex-B para Media Foundation.
    ///
    /// A diferença crítica para o Core 05 é que o MediaStreamSource NÃO é
    /// criado antes de conhecer SPS/PPS. Primeiro aguardamos o IDR que o
    /// Multiverse força ao ativar o consumidor, extraímos SPS/PPS em Annex-B
    /// e só então construímos o tipo H.264 com SetFormatUserData().
    ///
    /// O hot path continua latest-frame: no máximo 1 frame aguardando o
    /// decoder. Frame antigo nunca vira uma fila de latência.
    /// </summary>
    internal sealed class LowLatencyH264Player :
        IDisposable
    {
        private readonly uint _width;
        private readonly uint _height;
        private readonly uint _fps;
        private readonly uint _bitrateKbps;

        private readonly TimeSpan _frameDuration;

        private readonly SemaphoreSlim _frameSignal =
            new(0, 1);

        private readonly CancellationTokenSource _stop =
            new();

        private readonly object _pipelineSync =
            new();

        private ReassembledVideoFrame? _latestFrame;

        private MediaStreamSource? _streamSource;
        private MediaSource? _mediaSource;

        // O MediaPlayer existe desde o início para poder ser conectado
        // imediatamente ao MediaPlayerElement, mas recebe Source somente
        // depois que SPS/PPS forem conhecidos.
        private readonly MediaPlayer _player;

        private volatile bool _running;
        private volatile bool _formatReady;
        private volatile bool _mediaOpened;

        private int _sequenceHeaderBytes;

        private long _bootstrapFailures;

        private long _networkFramesReceived;
        private long _samplesSubmitted;
        private long _samplesRendered;

        private long _staleFramesDropped;
        private long _waitingForKeyFrameDrops;

        private long _sampleIndex;
        private ulong _lastSubmittedFrameId;

        private long _lastSampleLagUs;
        private long _totalSampleLagUs;
        private long _sampleLagCount;

        private string _lastError = "";

        public LowLatencyH264Player(
            uint width,
            uint height,
            uint fps,
            uint bitrateKbps)
        {
            if (width == 0 ||
                height == 0 ||
                fps == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(width),
                    "Dimensões/FPS inválidos para H.264.");
            }

            _width = width;
            _height = height;
            _fps = fps;
            _bitrateKbps = bitrateKbps;

            _frameDuration =
                TimeSpan.FromTicks(
                    Math.Max(
                        1,
                        TimeSpan.TicksPerSecond /
                        (long)fps));

            _player =
                new MediaPlayer
                {
                    // O Media Foundation reduz políticas próprias de
                    // buffering quando este modo está ativo.
                    RealTimePlayback = true,

                    // Áudio terá canal próprio no Multiverse.
                    IsMuted = true,
                    Volume = 0,

                    // Não precisamos da integração de mídia do Windows
                    // para um cliente de desktop remoto.
                    AutoPlay = false
                };

            _player.CommandManager.IsEnabled =
                false;

            _player.MediaOpened +=
                Player_MediaOpened;

            _player.MediaFailed +=
                Player_MediaFailed;
        }

        public MediaPlayer Player =>
            _player;

        public bool Running =>
            _running;

        public void Start()
        {
            if (_running)
                return;

            _running = true;

            // Não damos Play ainda.
            // O pipeline é inicializado no primeiro IDR que contenha SPS/PPS.
        }

        public void PushFrame(
            ReassembledVideoFrame frame)
        {
            if (!_running ||
                frame.Data.Length == 0)
            {
                return;
            }

            Interlocked.Increment(
                ref _networkFramesReceived);

            if (!_formatReady)
            {
                if (!frame.KeyFrame)
                {
                    Interlocked.Increment(
                        ref _waitingForKeyFrameDrops);

                    return;
                }

                if (!TryExtractSequenceHeader(
                        frame.Data,
                        out byte[] sequenceHeader))
                {
                    Interlocked.Increment(
                        ref _bootstrapFailures);

                    _lastError =
                        "IDR recebido, mas SPS/PPS não foram encontrados. " +
                        "Force outro IDR; o NVENC deve repetir SPS/PPS.";

                    return;
                }

                try
                {
                    EnsurePipelineInitialized(
                        sequenceHeader);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(
                        ref _bootstrapFailures);

                    _lastError =
                        $"Falha no bootstrap H.264: {ex.Message}";

                    return;
                }
            }

            QueueLatestFrame(
                frame);
        }

        private void EnsurePipelineInitialized(
            byte[] sequenceHeader)
        {
            lock (_pipelineSync)
            {
                if (_formatReady)
                    return;

                VideoEncodingProperties properties =
                    VideoEncodingProperties.CreateH264();

                properties.Width =
                    _width;

                properties.Height =
                    _height;

                properties.Bitrate =
                    _bitrateKbps * 1000u;

                properties.FrameRate.Numerator =
                    _fps;

                properties.FrameRate.Denominator =
                    1;

                properties.PixelAspectRatio.Numerator =
                    1;

                properties.PixelAspectRatio.Denominator =
                    1;

                // O host NVENC está configurado como H.264 High.
                properties.ProfileId =
                    H264ProfileIds.High;

                // CRÍTICO:
                // Media Foundation usa estes dados para popular o
                // MF_MT_MPEG_SEQUENCE_HEADER. Para H.264 o blob deve
                // conter SPS + PPS em Annex-B, incluindo start codes.
                properties.SetFormatUserData(
                    sequenceHeader);

                var descriptor =
                    new VideoStreamDescriptor(
                        properties);

                MediaStreamSource streamSource =
                    new MediaStreamSource(
                        descriptor)
                    {
                        CanSeek = false,
                        IsLive = true,

                        // Uma janela próxima de 1 frame; nunca segundos.
                        BufferTime =
                            ClampBufferTime(
                                _frameDuration)
                    };

                streamSource.SampleRequested +=
                    StreamSource_SampleRequested;

                streamSource.SampleRendered +=
                    StreamSource_SampleRendered;

                streamSource.Closed +=
                    StreamSource_Closed;

                MediaSource mediaSource =
                    MediaSource.CreateFromMediaStreamSource(
                        streamSource);

                _streamSource =
                    streamSource;

                _mediaSource =
                    mediaSource;

                _sequenceHeaderBytes =
                    sequenceHeader.Length;

                _formatReady =
                    true;

                _lastError =
                    "";

                // Source só é configurado depois do tipo completo existir.
                _player.Source =
                    mediaSource;

                _player.Play();
            }
        }

        private static TimeSpan ClampBufferTime(
            TimeSpan frameDuration)
        {
            // A documentação do MediaStreamSource diz que BufferTime
            // controla quantas amostras são solicitadas antecipadamente.
            // O default é 3 segundos. Para remote gaming mantemos ~1 frame.
            TimeSpan min =
                TimeSpan.FromMilliseconds(3);

            TimeSpan max =
                TimeSpan.FromMilliseconds(12);

            if (frameDuration < min)
                return min;

            if (frameDuration > max)
                return max;

            return frameDuration;
        }

        private void QueueLatestFrame(
            ReassembledVideoFrame frame)
        {
            ReassembledVideoFrame? old =
                Interlocked.Exchange(
                    ref _latestFrame,
                    frame);

            if (old is not null)
            {
                Interlocked.Increment(
                    ref _staleFramesDropped);
            }

            try
            {
                if (_frameSignal.CurrentCount == 0)
                {
                    _frameSignal.Release();
                }
            }
            catch (SemaphoreFullException)
            {
                // Já existe um wake-up aguardando.
            }
        }

        private async void StreamSource_SampleRequested(
            MediaStreamSource sender,
            MediaStreamSourceSampleRequestedEventArgs args)
        {
            MediaStreamSourceSampleRequest request =
                args.Request;

            MediaStreamSourceSampleRequestDeferral deferral =
                request.GetDeferral();

            try
            {
                ReassembledVideoFrame? frame =
                    await WaitForLatestFrameAsync(
                        _stop.Token)
                    .ConfigureAwait(false);

                if (frame is null)
                {
                    request.Sample = null;
                    return;
                }

                long index =
                    Interlocked.Increment(
                        ref _sampleIndex) - 1;

                TimeSpan timestamp =
                    TimeSpan.FromTicks(
                        index *
                        _frameDuration.Ticks);

                MediaStreamSample sample =
                    MediaStreamSample.CreateFromBuffer(
                        frame.Data.AsBuffer(),
                        timestamp);

                sample.Duration =
                    _frameDuration;

                sample.KeyFrame =
                    frame.KeyFrame;

                if (_lastSubmittedFrameId != 0 &&
                    frame.FrameId >
                        _lastSubmittedFrameId + 1)
                {
                    // Informa ao pipeline que frames intermediários foram
                    // intencionalmente descartados pela política low-latency.
                    sample.Discontinuous =
                        true;
                }

                _lastSubmittedFrameId =
                    frame.FrameId;

                request.Sample =
                    sample;

                Interlocked.Increment(
                    ref _samplesSubmitted);
            }
            catch (OperationCanceledException)
            {
                request.Sample = null;
            }
            catch (Exception ex)
            {
                _lastError =
                    $"Decoder/MSS: {ex.Message}";

                request.Sample = null;
            }
            finally
            {
                deferral.Complete();
            }
        }

        private async Task<ReassembledVideoFrame?>
            WaitForLatestFrameAsync(
                CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                ReassembledVideoFrame? immediate =
                    Interlocked.Exchange(
                        ref _latestFrame,
                        null);

                if (immediate is not null)
                {
                    _frameSignal.Wait(0);
                    return immediate;
                }

                await _frameSignal
                    .WaitAsync(token)
                    .ConfigureAwait(false);

                ReassembledVideoFrame? frame =
                    Interlocked.Exchange(
                        ref _latestFrame,
                        null);

                if (frame is not null)
                    return frame;
            }

            return null;
        }

        private void StreamSource_SampleRendered(
            MediaStreamSource sender,
            MediaStreamSourceSampleRenderedEventArgs args)
        {
            Interlocked.Increment(
                ref _samplesRendered);

            long lagUs =
                (long)(
                    args.SampleLag.TotalMilliseconds *
                    1000.0);

            Interlocked.Exchange(
                ref _lastSampleLagUs,
                lagUs);

            Interlocked.Add(
                ref _totalSampleLagUs,
                lagUs);

            Interlocked.Increment(
                ref _sampleLagCount);
        }

        private void Player_MediaOpened(
            MediaPlayer sender,
            object args)
        {
            _mediaOpened =
                true;

            _lastError =
                "";
        }

        private void StreamSource_Closed(
            MediaStreamSource sender,
            MediaStreamSourceClosedEventArgs args)
        {
            _mediaOpened =
                false;

            if (!_stop.IsCancellationRequested)
            {
                _lastError =
                    $"MediaStreamSource fechado: {args.Request.Reason}";
            }
        }

        private void Player_MediaFailed(
            MediaPlayer sender,
            MediaPlayerFailedEventArgs args)
        {
            _mediaOpened =
                false;

            _lastError =
                $"MediaPlayer: {args.Error} • {args.ErrorMessage}";
        }

        public DecoderSnapshot GetSnapshot()
        {
            long lagCount =
                Interlocked.Read(
                    ref _sampleLagCount);

            double averageLagMs =
                lagCount > 0
                    ? Interlocked.Read(
                        ref _totalSampleLagUs) /
                        1000.0 /
                        lagCount
                    : 0.0;

            return new DecoderSnapshot
            {
                Running =
                    _running,

                FormatReady =
                    _formatReady,

                MediaOpened =
                    _mediaOpened,

                SequenceHeaderBytes =
                    _sequenceHeaderBytes,

                BootstrapFailures =
                    Interlocked.Read(
                        ref _bootstrapFailures),

                NetworkFramesReceived =
                    Interlocked.Read(
                        ref _networkFramesReceived),

                SamplesSubmitted =
                    Interlocked.Read(
                        ref _samplesSubmitted),

                SamplesRendered =
                    Interlocked.Read(
                        ref _samplesRendered),

                StaleFramesDropped =
                    Interlocked.Read(
                        ref _staleFramesDropped),

                WaitingForKeyFrameDrops =
                    Interlocked.Read(
                        ref _waitingForKeyFrameDrops),

                LastSampleLagMs =
                    Interlocked.Read(
                        ref _lastSampleLagUs) /
                    1000.0,

                AverageSampleLagMs =
                    averageLagMs,

                LastError =
                    _lastError
            };
        }

        public void Stop()
        {
            if (!_running &&
                _stop.IsCancellationRequested)
            {
                return;
            }

            _running =
                false;

            try
            {
                _stop.Cancel();
            }
            catch
            {
            }

            Interlocked.Exchange(
                ref _latestFrame,
                null);

            try
            {
                _player.Pause();
                _player.Source = null;
            }
            catch
            {
            }

            lock (_pipelineSync)
            {
                DetachStreamSourceHandlers();
            }
        }

        private void DetachStreamSourceHandlers()
        {
            if (_streamSource is not null)
            {
                _streamSource.SampleRequested -=
                    StreamSource_SampleRequested;

                _streamSource.SampleRendered -=
                    StreamSource_SampleRendered;

                _streamSource.Closed -=
                    StreamSource_Closed;
            }
        }

        public void Dispose()
        {
            Stop();

            _player.MediaOpened -=
                Player_MediaOpened;

            _player.MediaFailed -=
                Player_MediaFailed;

            _player.Dispose();

            _mediaSource?.Dispose();
            _mediaSource = null;

            _streamSource = null;

            _frameSignal.Dispose();
            _stop.Dispose();

            GC.SuppressFinalize(this);
        }

        // ============================================================
        // Annex-B parser
        // ============================================================

        private static bool TryExtractSequenceHeader(
            byte[] accessUnit,
            out byte[] sequenceHeader)
        {
            sequenceHeader =
                Array.Empty<byte>();

            if (accessUnit.Length < 8)
                return false;

            ReadOnlySpan<byte> source =
                accessUnit;

            ReadOnlySpan<byte> sps =
                default;

            ReadOnlySpan<byte> pps =
                default;

            int cursor = 0;

            while (TryFindNextNal(
                source,
                cursor,
                out int nalStart,
                out int nalPayloadStart,
                out int nalEnd))
            {
                if (nalPayloadStart >=
                    nalEnd)
                {
                    cursor =
                        Math.Max(
                            nalEnd,
                            cursor + 1);

                    continue;
                }

                byte nalType =
                    (byte)(
                        source[nalPayloadStart] &
                        0x1F);

                ReadOnlySpan<byte> payload =
                    source.Slice(
                        nalPayloadStart,
                        nalEnd -
                        nalPayloadStart);

                if (nalType == 7 &&
                    sps.IsEmpty)
                {
                    sps = payload;
                }
                else if (
                    nalType == 8 &&
                    pps.IsEmpty)
                {
                    pps = payload;
                }

                if (!sps.IsEmpty &&
                    !pps.IsEmpty)
                {
                    break;
                }

                cursor =
                    Math.Max(
                        nalEnd,
                        cursor + 1);
            }

            if (sps.IsEmpty ||
                pps.IsEmpty)
            {
                return false;
            }

            // A Microsoft documenta MF_MT_MPEG_SEQUENCE_HEADER para H.264
            // como SPS e PPS concatenados em Annex-B com os start codes.
            int bytes =
                4 + sps.Length +
                4 + pps.Length;

            byte[] result =
                new byte[bytes];

            int offset = 0;

            WriteStartCode(
                result,
                ref offset);

            sps.CopyTo(
                result.AsSpan(
                    offset,
                    sps.Length));

            offset +=
                sps.Length;

            WriteStartCode(
                result,
                ref offset);

            pps.CopyTo(
                result.AsSpan(
                    offset,
                    pps.Length));

            sequenceHeader =
                result;

            return true;
        }

        private static void WriteStartCode(
            byte[] destination,
            ref int offset)
        {
            destination[offset++] = 0;
            destination[offset++] = 0;
            destination[offset++] = 0;
            destination[offset++] = 1;
        }

        private static bool TryFindNextNal(
            ReadOnlySpan<byte> source,
            int searchFrom,
            out int startCodeStart,
            out int payloadStart,
            out int nalEnd)
        {
            startCodeStart = -1;
            payloadStart = -1;
            nalEnd = -1;

            if (!TryFindStartCode(
                    source,
                    searchFrom,
                    out int start,
                    out int startCodeBytes))
            {
                return false;
            }

            int payload =
                start +
                startCodeBytes;

            if (payload >=
                source.Length)
            {
                return false;
            }

            int nextSearch =
                payload + 1;

            if (TryFindStartCode(
                    source,
                    nextSearch,
                    out int nextStart,
                    out _))
            {
                nalEnd =
                    nextStart;
            }
            else
            {
                nalEnd =
                    source.Length;
            }

            startCodeStart =
                start;

            payloadStart =
                payload;

            return true;
        }

        private static bool TryFindStartCode(
            ReadOnlySpan<byte> source,
            int searchFrom,
            out int index,
            out int bytes)
        {
            index = -1;
            bytes = 0;

            int start =
                Math.Max(
                    0,
                    searchFrom);

            for (int i = start;
                 i + 3 < source.Length;
                 i++)
            {
                if (source[i] != 0 ||
                    source[i + 1] != 0)
                {
                    continue;
                }

                if (source[i + 2] == 1)
                {
                    index = i;
                    bytes = 3;
                    return true;
                }

                if (i + 3 <
                        source.Length &&
                    source[i + 2] == 0 &&
                    source[i + 3] == 1)
                {
                    index = i;
                    bytes = 4;
                    return true;
                }
            }

            return false;
        }
    }
}
