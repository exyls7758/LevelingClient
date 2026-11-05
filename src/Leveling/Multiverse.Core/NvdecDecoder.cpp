#include "MultiverseCore.h"
#include "MfVideoDecoder.h"
#include "D3D11NvdecRenderer.h"

#include <Windows.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cwchar>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#if defined(MULTIVERSE_NVDEC)
#include <cuda.h>
#include "nvcuvid.h"
#endif

namespace
{
#if defined(MULTIVERSE_NVDEC)

    static unsigned long GetDecodeSurfaceCount(
        cudaVideoCodec codec,
        unsigned int width,
        unsigned int height)
    {
        (void)width;
        (void)height;

        // NVIDIA's current NvDecoder reference implementation uses
        // a worst-case DPB size of 20 decode surfaces for H.264.
        // Returning this value from the sequence callback also overrides
        // CUVIDPARSERPARAMS::ulMaxNumDecodeSurfaces.
        if (codec == cudaVideoCodec_H264 ||
            codec == cudaVideoCodec_H264_SVC ||
            codec == cudaVideoCodec_H264_MVC)
        {
            return 20ul;
        }

        return 8ul;
    }

    using Clock =
        std::chrono::steady_clock;

    std::int32_t NvdecResultToStatus(
        CUresult result)
    {
        if (result == CUDA_SUCCESS)
            return 0;

        return -20000 -
            static_cast<std::int32_t>(result);
    }

    std::wstring CudaResultText(
        CUresult result)
    {
        const char* name = nullptr;
        const char* text = nullptr;

        cuGetErrorName(
            result,
            &name);

        cuGetErrorString(
            result,
            &text);

        std::wstring resultText =
            L"CUDA/NVDEC erro " +
            std::to_wstring(
                static_cast<int>(result));

        if (name)
        {
            int chars =
                MultiByteToWideChar(
                    CP_UTF8,
                    0,
                    name,
                    -1,
                    nullptr,
                    0);

            if (chars > 1)
            {
                std::wstring wide(
                    static_cast<size_t>(chars - 1),
                    L'\0');

                MultiByteToWideChar(
                    CP_UTF8,
                    0,
                    name,
                    -1,
                    wide.data(),
                    chars);

                resultText +=
                    L" (" + wide + L")";
            }
        }

        if (text)
        {
            int chars =
                MultiByteToWideChar(
                    CP_UTF8,
                    0,
                    text,
                    -1,
                    nullptr,
                    0);

            if (chars > 1)
            {
                std::wstring wide(
                    static_cast<size_t>(chars - 1),
                    L'\0');

                MultiByteToWideChar(
                    CP_UTF8,
                    0,
                    text,
                    -1,
                    wide.data(),
                    chars);

                resultText +=
                    L": " + wide;
            }
        }

        return resultText;
    }

    std::uint8_t Clamp8(
        int value)
    {
        return static_cast<std::uint8_t>(
            std::clamp(
                value,
                0,
                255));
    }

    std::uint32_t ReadBe32(
        const std::uint8_t* p)
    {
        return
            (static_cast<std::uint32_t>(p[0]) << 24) |
            (static_cast<std::uint32_t>(p[1]) << 16) |
            (static_cast<std::uint32_t>(p[2]) << 8) |
            static_cast<std::uint32_t>(p[3]);
    }

    bool HasAnnexBStartCode(
        const std::uint8_t* data,
        std::uint32_t bytes)
    {
        if (!data || bytes < 4)
            return false;

        const std::uint32_t limit =
            std::min<std::uint32_t>(
                bytes,
                64u);

        for (std::uint32_t i = 0;
             i + 3 < limit;
             ++i)
        {
            if (data[i] == 0 &&
                data[i + 1] == 0 &&
                (
                    data[i + 2] == 1 ||
                    (
                        i + 3 < limit &&
                        data[i + 2] == 0 &&
                        data[i + 3] == 1
                    )
                ))
            {
                return true;
            }
        }

        return false;
    }

    struct SubmitStamp
    {
        std::uint64_t FrameId = 0;
        CUvideotimestamp Timestamp = 0;
        Clock::time_point Time{};
    };

    class NativeNvdecDecoder final
    {
    public:
        NativeNvdecDecoder(
            std::uint32_t expectedWidth,
            std::uint32_t expectedHeight,
            std::uint32_t previewMaxWidth)
            :
            m_expectedWidth(expectedWidth),
            m_expectedHeight(expectedHeight),
            m_previewMaxWidth(
                std::clamp<std::uint32_t>(
                    previewMaxWidth == 0
                        ? 960u
                        : previewMaxWidth,
                    320u,
                    1920u))
        {
        }

        ~NativeNvdecDecoder()
        {
            Shutdown();
        }

        NativeNvdecDecoder(
            const NativeNvdecDecoder&) = delete;

        NativeNvdecDecoder& operator=(
            const NativeNvdecDecoder&) = delete;

        std::int32_t Initialize()
        {
            SetMessage(
                L"Inicializando CUDA/NVDEC...");

            CUresult result =
                cuInit(0);

            if (result != CUDA_SUCCESS)
                return Fail(
                    result,
                    L"cuInit");

            int deviceCount = 0;

            result =
                cuDeviceGetCount(
                    &deviceCount);

            if (result != CUDA_SUCCESS ||
                deviceCount <= 0)
            {
                if (result == CUDA_SUCCESS)
                {
                    SetMessage(
                        L"Nenhuma GPU CUDA foi encontrada.");

                    m_lastStatus.store(
                        -20001);

                    return -20001;
                }

                return Fail(
                    result,
                    L"cuDeviceGetCount");
            }

            // Core 06 usa a primeira GPU CUDA/NVIDIA.
            // A seleção por LUID do adapter da captura entra no Core 07.
            result =
                cuDeviceGet(
                    &m_device,
                    0);

            if (result != CUDA_SUCCESS)
                return Fail(
                    result,
                    L"cuDeviceGet");

            // CUDA 13.x: cuCtxCreate maps to cuCtxCreate_v4 and now
            // receives CUctxCreateParams* as the second argument.
            // nullptr creates a regular CUDA context.
            result =
                cuCtxCreate(
                    &m_context,
                    nullptr,
                    CU_CTX_SCHED_YIELD,
                    m_device);

            if (result != CUDA_SUCCESS)
                return Fail(
                    result,
                    L"cuCtxCreate");

            // NVIDIA's reference decoder creates a CUVID context lock and
            // supplies it to CUVIDDECODECREATEINFO::vidLock. This keeps
            // parser/decode/map access serialized correctly around the CUDA
            // context and is important once decode/render become threaded.
            result =
                cuvidCtxLockCreate(
                    &m_ctxLock,
                    m_context);

            if (result != CUDA_SUCCESS)
                return Fail(
                    result,
                    L"cuvidCtxLockCreate");

            m_timestampOrigin =
                Clock::now();

            m_renderer =
                std::make_unique<
                    D3D11NvdecRenderer>();

            CUVIDPARSERPARAMS parser{};

            parser.CodecType =
                cudaVideoCodec_H264;

            parser.ulMaxNumDecodeSurfaces =
                1;

            // Sem B-frames no nosso encoder:
            // zero display delay é o caminho correto.
            parser.ulClockRate =
                10'000'000;

            // Remote-gaming policy: always let the decode callback see a
            // picture even if the parser reports bitstream damage. Hardware
            // concealment + a forced IDR is preferable to silently starving
            // the decoder forever after one bad access unit.
            parser.ulErrorThreshold =
                100;

            parser.ulMaxDisplayDelay =
                0;

            parser.pUserData =
                this;

            parser.pfnSequenceCallback =
                &SequenceCallback;

            parser.pfnDecodePicture =
                &DecodeCallback;

            parser.pfnDisplayPicture =
                &DisplayCallback;

            result =
                cuvidCreateVideoParser(
                    &m_parser,
                    &parser);

            if (result != CUDA_SUCCESS)
                return Fail(
                    result,
                    L"cuvidCreateVideoParser");

            m_running.store(
                true);

            m_lastStatus.store(
                0);

            SetMessage(
                L"NVDEC pronto. Aguardando SPS/PPS + IDR.");

            return 0;
        }

        void Shutdown()
        {
            bool wasRunning =
                m_running.exchange(
                    false);

            if (!wasRunning &&
                !m_parser &&
                !m_decoder &&
                !m_context)
            {
                return;
            }

            if (m_context)
            {
                CUcontext old = nullptr;

                if (cuCtxPushCurrent(
                        m_context) == CUDA_SUCCESS)
                {
                    if (m_renderer)
                    {
                        m_renderer->Shutdown();
                    }

                    if (m_parser)
                    {
                        CUVIDSOURCEDATAPACKET eos{};
                        eos.flags =
                            CUVID_PKT_ENDOFSTREAM;

                        cuvidParseVideoData(
                            m_parser,
                            &eos);
                    }

                    if (m_decoder)
                    {
                        cuvidDestroyDecoder(
                            m_decoder);

                        m_decoder =
                            nullptr;
                    }

                    if (m_parser)
                    {
                        cuvidDestroyVideoParser(
                            m_parser);

                        m_parser =
                            nullptr;
                    }

                    cuCtxPopCurrent(
                        &old);
                }

                if (m_ctxLock)
                {
                    cuvidCtxLockDestroy(
                        m_ctxLock);

                    m_ctxLock =
                        nullptr;
                }

                cuCtxDestroy(
                    m_context);

                m_context =
                    nullptr;
            }

            SetMessage(
                L"NVDEC parado.");
        }

        std::int32_t Push(
            const std::uint8_t* data,
            std::uint32_t bytes,
            std::uint64_t frameId,
            bool keyFrame)
        {
            if (!m_running.load() ||
                !m_parser ||
                !m_context)
            {
                return -20002;
            }

            if (!data ||
                bytes == 0)
            {
                return -20003;
            }

            const std::uint8_t* parserData =
                data;

            std::uint32_t parserBytes =
                bytes;

            std::vector<std::uint8_t>
                annexBConverted;

            bool annexB =
                HasAnnexBStartCode(
                    data,
                    bytes);

            if (!annexB && bytes >= 5)
            {
                // Defensive AVCC fallback. NVENC normally emits Annex-B,
                // but this makes the decoder tolerant if the producer is
                // ever changed to length-prefixed NAL units.
                std::uint32_t offset = 0;
                bool validAvcc = true;

                while (offset + 4u <= bytes)
                {
                    std::uint32_t nalBytes =
                        ReadBe32(
                            data + offset);

                    offset += 4u;

                    if (nalBytes == 0 ||
                        nalBytes > bytes - offset)
                    {
                        validAvcc = false;
                        break;
                    }

                    annexBConverted.insert(
                        annexBConverted.end(),
                        {0, 0, 0, 1});

                    annexBConverted.insert(
                        annexBConverted.end(),
                        data + offset,
                        data + offset + nalBytes);

                    offset += nalBytes;
                }

                if (validAvcc &&
                    offset == bytes &&
                    !annexBConverted.empty())
                {
                    parserData =
                        annexBConverted.data();

                    parserBytes =
                        static_cast<std::uint32_t>(
                            annexBConverted.size());

                    annexB = true;
                    m_avccConverted.fetch_add(1);
                }
            }

            if (annexB)
            {
                InspectAnnexB(
                    parserData,
                    parserBytes);
            }
            else
            {
                m_packetsWithoutStartCode.fetch_add(1);
            }

            const auto submitNow =
                Clock::now();

            // CUVIDSOURCEDATAPACKET.timestamp is a PTS in a 10 MHz timebase
            // by default. Using FrameId (1, 2, 3...) as a timestamp makes
            // hundreds of frames appear only microseconds apart and newer
            // drivers use this value for display ordering.
            CUvideotimestamp timestamp =
                static_cast<CUvideotimestamp>(
                    std::chrono::duration_cast<
                        std::chrono::nanoseconds>(
                        submitNow -
                        m_timestampOrigin)
                        .count() /
                    100);

            CUvideotimestamp previous =
                m_lastSubmittedTimestamp.load();

            if (timestamp <= previous)
                timestamp = previous + 1;

            m_lastSubmittedTimestamp.store(
                timestamp);

            const std::uint64_t submitIndex =
                m_submittedFrames.fetch_add(
                    1);

            SubmitStamp& stamp =
                m_submitStamps[
                    submitIndex %
                    m_submitStamps.size()];

            stamp.FrameId =
                frameId;

            stamp.Timestamp =
                timestamp;

            stamp.Time =
                submitNow;

            CUVIDSOURCEDATAPACKET packet{};

            packet.payload =
                parserData;

            packet.payload_size =
                parserBytes;

            packet.flags =
                CUVID_PKT_TIMESTAMP |
                CUVID_PKT_ENDOFPICTURE;

            if (keyFrame &&
                m_discontinuityRequested.exchange(
                    false))
            {
                packet.flags |=
                    CUVID_PKT_DISCONTINUITY;
            }

            packet.timestamp =
                timestamp;

            CUcontext old = nullptr;

            CUresult result =
                cuCtxPushCurrent(
                    m_context);

            if (result != CUDA_SUCCESS)
                return Fail(
                    result,
                    L"cuCtxPushCurrent");

            result =
                cuvidParseVideoData(
                    m_parser,
                    &packet);

            CUresult popResult =
                cuCtxPopCurrent(
                    &old);

            if (result != CUDA_SUCCESS)
                return Fail(
                    result,
                    L"cuvidParseVideoData");

            if (popResult != CUDA_SUCCESS)
                return Fail(
                    popResult,
                    L"cuCtxPopCurrent");

            return 0;
        }

        void RequestDiscontinuity()
        {
            m_discontinuityRequested.store(
                true);
        }

        void SignalDiscontinuity()
        {
            m_discontinuityRequested.store(
                true);

            SetMessage(
                L"Descontinuidade sinalizada. Aguardando novo IDR.");
        }

        void SetHeadlessOutput(
            bool enabled)
        {
            m_headlessOutput.store(
                enabled);

            SetMessage(
                enabled
                    ? L"CORE 11.8 HEADLESS: NVDEC ativo sem preview/D3D11/Present."
                    : L"CORE 11.8 VISUAL: saída NVDEC normal habilitada.");
        }

        std::int32_t AttachSwapChainPanel(
            IUnknown* panelUnknown,
            std::uint32_t width,
            std::uint32_t height)
        {
            if (!m_renderer ||
                !m_context ||
                !panelUnknown)
            {
                return -20100;
            }

            CUcontext old = nullptr;

            CUresult cu =
                cuCtxPushCurrent(
                    m_context);

            if (cu != CUDA_SUCCESS)
            {
                return Fail(
                    cu,
                    L"cuCtxPushCurrent(AttachSwapChain)");
            }

            std::int32_t result =
                m_renderer->AttachPanel(
                    panelUnknown,
                    m_device,
                    width,
                    height);

            cuCtxPopCurrent(
                &old);

            if (result < 0)
            {
                SetMessage(
                    m_renderer->GetLastMessage());
            }
            else
            {
                SetMessage(
                    L"Core 07 D3D11 conectado ao SwapChainPanel.");
            }

            return result;
        }

        std::int32_t ResizeSwapChain(
            std::uint32_t width,
            std::uint32_t height)
        {
            if (!m_renderer)
                return -20100;

            std::int32_t result =
                m_renderer->Resize(
                    width,
                    height);

            if (result < 0)
            {
                SetMessage(
                    m_renderer->GetLastMessage());
            }

            return result;
        }

        std::int32_t SetCompositionScale(float scaleX, float scaleY)
        {
            return m_renderer
                ? m_renderer->SetCompositionScale(scaleX, scaleY)
                : -20100;
        }

        void DetachSwapChainPanel()
        {
            if (m_renderer) m_renderer->DetachPanel();
        }

        std::int32_t TryDetachPanel()
        {
            if (m_renderer && !m_renderer->TryStopPresenter())
                return HRESULT_FROM_WIN32(WAIT_TIMEOUT);
            DetachSwapChainPanel();
            return 0;
        }

        void FillRendererStats(
            MV_D3D11RendererStats& stats) const
        {
            stats = {};

            if (!m_renderer)
                return;

            MV_D3D11RendererStatsNative native{};
            m_renderer->GetStats(native);

            stats.Attached =
                native.Attached;

            stats.LastStatus =
                native.LastStatus;

            stats.TargetWidth =
                native.TargetWidth;

            stats.TargetHeight =
                native.TargetHeight;

            stats.SourceWidth =
                native.SourceWidth;

            stats.SourceHeight =
                native.SourceHeight;

            stats.SubmittedFrames =
                native.SubmittedFrames;

            stats.PresentedFrames =
                native.PresentedFrames;

            stats.DroppedPresents =
                native.DroppedPresents;

            stats.ReplacedPendingFrames =
                native.ReplacedPendingFrames;

            stats.PresentWaitTimeouts =
                native.PresentWaitTimeouts;

            stats.PresenterDequeuedFrames =
                native.PresenterDequeuedFrames;

            stats.FrameLatencySignals =
                native.FrameLatencySignals;

            stats.FrameLatencyBypasses =
                native.FrameLatencyBypasses;

            stats.FrameLatencyWaitFailures =
                native.FrameLatencyWaitFailures;

            stats.PresentAttempts =
                native.PresentAttempts;

            stats.PresentStillDrawing =
                native.PresentStillDrawing;

            stats.PresentFailures =
                native.PresentFailures;

            stats.ForegroundProcessId =
                native.ForegroundProcessId;

            stats.ForegroundIsCurrentProcess =
                native.ForegroundIsCurrentProcess;

            stats.PresenterStage =
                native.PresenterStage;

            stats.PresentInFlight =
                native.PresentInFlight;

            stats.LastGpuCopyMs =
                native.LastGpuCopyMs;

            stats.AverageGpuCopyMs =
                native.AverageGpuCopyMs;

            stats.LastPresentSubmitMs =
                native.LastPresentSubmitMs;

            stats.AveragePresentSubmitMs =
                native.AveragePresentSubmitMs;

            stats.LastPresentWaitMs =
                native.LastPresentWaitMs;

            stats.AveragePresentWaitMs =
                native.AveragePresentWaitMs;

            stats.LastQueueToPresenterMs =
                native.LastQueueToPresenterMs;

            stats.AverageQueueToPresenterMs =
                native.AverageQueueToPresenterMs;

            stats.LastStateLockWaitMs =
                native.LastStateLockWaitMs;

            stats.AverageStateLockWaitMs =
                native.AverageStateLockWaitMs;

            stats.LastPresentCallMs =
                native.LastPresentCallMs;

            stats.AveragePresentCallMs =
                native.AveragePresentCallMs;

            stats.LastFrameAgeAtPresentMs =
                native.LastFrameAgeAtPresentMs;

            stats.AverageFrameAgeAtPresentMs =
                native.AverageFrameAgeAtPresentMs;

            stats.PresenterActivityAgeMs =
                native.PresenterActivityAgeMs;
        }

        void FillStats(
            MV_DecoderStats& stats) const
        {
            stats = {};

            stats.Running =
                m_running.load()
                    ? 1
                    : 0;

            stats.FormatReady =
                m_formatReady.load()
                    ? 1
                    : 0;

            stats.LastStatus =
                m_lastStatus.load();

            stats.Width =
                m_width.load();

            stats.Height =
                m_height.load();

            stats.PreviewWidth =
                m_previewWidth.load();

            stats.PreviewHeight =
                m_previewHeight.load();

            stats.SubmittedFrames =
                m_submittedFrames.load();

            stats.DecodedPictures =
                m_decodedPictures.load();

            stats.DisplayedFrames =
                m_displayedFrames.load();

            stats.PreviewFrames =
                m_previewFrames.load();

            stats.LastDecodeLatencyMs =
                m_lastLatencyUs.load() /
                1000.0;

            std::uint64_t samples =
                m_latencySamples.load();

            stats.AverageDecodeLatencyMs =
                samples > 0
                    ? m_totalLatencyUs.load() /
                        1000.0 /
                        static_cast<double>(samples)
                    : 0.0;
        }

        void GetPreviewInfo(
            std::uint32_t& width,
            std::uint32_t& height,
            std::uint32_t& stride,
            std::uint32_t& bytes) const
        {
            width =
                m_previewWidth.load();

            height =
                m_previewHeight.load();

            stride =
                width * 4u;

            bytes =
                stride * height;
        }

        std::int32_t CopyPreview(
            std::uint8_t* destination,
            std::uint32_t destinationBytes,
            std::uint64_t& frameId)
        {
            if (!destination)
                return -20004;

            std::lock_guard lock(
                m_previewMutex);

            if (m_previewBgra.empty() ||
                m_previewGeneration ==
                    m_lastCopiedGeneration)
            {
                return 1;
            }

            if (destinationBytes <
                m_previewBgra.size())
            {
                return -20005;
            }

            std::copy(
                m_previewBgra.begin(),
                m_previewBgra.end(),
                destination);

            frameId =
                m_previewFrameId;

            m_lastCopiedGeneration =
                m_previewGeneration;

            return 0;
        }

        void CopyMessage(
            wchar_t* destination,
            std::uint32_t chars) const
        {
            if (!destination ||
                chars == 0)
            {
                return;
            }

            std::lock_guard lock(
                m_messageMutex);

            wcsncpy_s(
                destination,
                chars,
                m_lastMessage.c_str(),
                _TRUNCATE);
        }

    private:
        static int CUDAAPI SequenceCallback(
            void* userData,
            CUVIDEOFORMAT* format)
        {
            auto* self =
                static_cast<NativeNvdecDecoder*>(
                    userData);

            return self
                ? self->OnSequence(
                    format)
                : 0;
        }

        static int CUDAAPI DecodeCallback(
            void* userData,
            CUVIDPICPARAMS* picture)
        {
            auto* self =
                static_cast<NativeNvdecDecoder*>(
                    userData);

            return self
                ? self->OnDecode(
                    picture)
                : 0;
        }

        static int CUDAAPI DisplayCallback(
            void* userData,
            CUVIDPARSERDISPINFO* display)
        {
            auto* self =
                static_cast<NativeNvdecDecoder*>(
                    userData);

            return self
                ? self->OnDisplay(
                    display)
                : 0;
        }

        int OnSequence(
            CUVIDEOFORMAT* format)
        {
            if (!format)
                return 0;

            if (format->codec !=
                cudaVideoCodec_H264)
            {
                SetMessage(
                    L"NVDEC recebeu codec diferente de H.264.");

                m_lastStatus.store(
                    -20006);

                return 0;
            }

            const unsigned long decodeSurfaces =
                GetDecodeSurfaceCount(
                    format->codec,
                    format->coded_width,
                    format->coded_height);

            CUVIDDECODECAPS caps{};

            caps.eCodecType =
                format->codec;

            caps.eChromaFormat =
                format->chroma_format;

            caps.nBitDepthMinus8 =
                format->bit_depth_luma_minus8;

            CUresult result =
                cuvidGetDecoderCaps(
                    &caps);

            if (result != CUDA_SUCCESS)
            {
                Fail(
                    result,
                    L"cuvidGetDecoderCaps");

                return 0;
            }

            if (!caps.bIsSupported)
            {
                SetMessage(
                    L"O NVDEC desta GPU não suporta o formato H.264 recebido.");

                m_lastStatus.store(
                    -20007);

                return 0;
            }

            if (format->coded_width >
                    caps.nMaxWidth ||
                format->coded_height >
                    caps.nMaxHeight)
            {
                SetMessage(
                    L"Resolução acima do limite suportado pelo NVDEC.");

                m_lastStatus.store(
                    -20008);

                return 0;
            }

            std::uint32_t width =
                format->display_area.right >
                        format->display_area.left
                    ? static_cast<std::uint32_t>(
                        format->display_area.right -
                        format->display_area.left)
                    : format->coded_width;

            std::uint32_t height =
                format->display_area.bottom >
                        format->display_area.top
                    ? static_cast<std::uint32_t>(
                        format->display_area.bottom -
                        format->display_area.top)
                    : format->coded_height;

            width =
                std::max<std::uint32_t>(
                    width,
                    2u);

            height =
                std::max<std::uint32_t>(
                    height,
                    2u);

            width &= ~1u;
            height &= ~1u;

            if (m_decoder)
            {
                // O nosso stream não muda de resolução durante a sessão.
                // Se mudar, recriamos o decoder de modo determinístico.
                cuvidDestroyDecoder(
                    m_decoder);

                m_decoder =
                    nullptr;
            }

            CUVIDDECODECREATEINFO create{};

            create.ulWidth =
                format->coded_width;

            create.ulHeight =
                format->coded_height;

            create.ulMaxWidth =
                std::max(
                    format->coded_width,
                    m_expectedWidth);

            create.ulMaxHeight =
                std::max(
                    format->coded_height,
                    m_expectedHeight);

            create.ulNumDecodeSurfaces =
                decodeSurfaces;

            create.CodecType =
                format->codec;

            create.ChromaFormat =
                format->chroma_format;

            create.bitDepthMinus8 =
                format->bit_depth_luma_minus8;

            create.OutputFormat =
                format->bit_depth_luma_minus8 == 0
                    ? cudaVideoSurfaceFormat_NV12
                    : cudaVideoSurfaceFormat_P016;

            if (create.OutputFormat !=
                cudaVideoSurfaceFormat_NV12)
            {
                SetMessage(
                    L"Core 06 aceita H.264 8-bit NV12. Stream 10-bit ainda não está habilitado.");

                m_lastStatus.store(
                    -20009);

                return 0;
            }

            create.DeinterlaceMode =
                cudaVideoDeinterlaceMode_Weave;

            create.ulCreationFlags =
                cudaVideoCreate_PreferCUVID;

            create.ulNumOutputSurfaces =
                2;

            create.vidLock =
                m_ctxLock;

            create.ulTargetWidth =
                width;

            create.ulTargetHeight =
                height;

            create.display_area.left =
                static_cast<short>(
                    std::clamp(
                        format->display_area.left,
                        -32768,
                        32767));

            create.display_area.top =
                static_cast<short>(
                    std::clamp(
                        format->display_area.top,
                        -32768,
                        32767));

            create.display_area.right =
                static_cast<short>(
                    std::clamp(
                        format->display_area.right,
                        -32768,
                        32767));

            create.display_area.bottom =
                static_cast<short>(
                    std::clamp(
                        format->display_area.bottom,
                        -32768,
                        32767));

            create.target_rect.left =
                0;

            create.target_rect.top =
                0;

            create.target_rect.right =
                static_cast<short>(
                    std::min<std::uint32_t>(
                        width,
                        32766u));

            create.target_rect.bottom =
                static_cast<short>(
                    std::min<std::uint32_t>(
                        height,
                        32766u));

            result =
                cuvidCreateDecoder(
                    &m_decoder,
                    &create);

            if (result != CUDA_SUCCESS)
            {
                Fail(
                    result,
                    L"cuvidCreateDecoder");

                return 0;
            }

            m_surfaceHeight =
                height;

            m_width.store(
                width);

            m_height.store(
                height);

            BuildPreviewGeometry(
                width,
                height);

            m_formatReady.store(
                true);

            m_lastStatus.store(
                0);

            SetMessage(
                L"NVDEC H.264 ativo • Core 06.5 stability • preview fora do hot path • display delay 0.");

            // Returning > 1 tells the CUVID parser to override its
            // decode-surface count as well. This must match the decoder DPB.
            return static_cast<int>(
                decodeSurfaces);
        }

        int OnDecode(
            CUVIDPICPARAMS* picture)
        {
            if (!m_decoder ||
                !picture)
            {
                return 0;
            }

            CUresult result =
                cuvidDecodePicture(
                    m_decoder,
                    picture);

            if (result != CUDA_SUCCESS)
            {
                Fail(
                    result,
                    L"cuvidDecodePicture");

                return 0;
            }

            m_decodedPictures.fetch_add(
                1);

            return 1;
        }

        int OnDisplay(
            CUVIDPARSERDISPINFO* display)
        {
            if (!m_decoder ||
                !display)
            {
                return 0;
            }

            const auto now =
                Clock::now();

            std::uint64_t frameId = 0;

            for (const SubmitStamp& stamp :
                 m_submitStamps)
            {
                if (stamp.Timestamp ==
                    display->timestamp)
                {
                    frameId =
                        stamp.FrameId;

                    auto latency =
                        std::chrono::duration_cast<
                            std::chrono::microseconds>(
                            now - stamp.Time)
                            .count();

                    if (latency >= 0)
                    {
                        m_lastLatencyUs.store(
                            static_cast<std::uint64_t>(
                                latency));

                        m_totalLatencyUs.fetch_add(
                            static_cast<std::uint64_t>(
                                latency));

                        m_latencySamples.fetch_add(
                            1);
                    }

                    break;
                }
            }

            m_displayedFrames.fetch_add(
                1);

            // CORE 11.8 Host/Client Isolation. In diagnostic headless mode
            // the parser + NVDEC callbacks continue at full rate, but we stop
            // before cuvidMapVideoFrame. This removes preview CUDA copies,
            // D3D11 rendering, DXGI Present and DWM composition from the local
            // loopback workload, so host capture/NVENC/UDP can be measured
            // without the artificial same-GPU client presenter.
            if (m_headlessOutput.load())
            {
                return 1;
            }

            const bool directD3D =
                m_renderer &&
                m_renderer->IsAttached();

            const bool makeDiagnosticPreview =
                !directD3D &&
                now - m_lastPreviewAt >=
                    std::chrono::milliseconds(250);

            if (!directD3D &&
                !makeDiagnosticPreview)
            {
                return 1;
            }

            CUVIDPROCPARAMS process{};

            process.progressive_frame =
                display->progressive_frame;

            process.second_field =
                0;

            process.top_field_first =
                display->top_field_first;

            process.unpaired_field =
                display->repeat_first_field < 0
                    ? 1
                    : 0;

            CUdeviceptr deviceFrame = 0;
            unsigned int sourcePitch = 0;

            CUresult result =
                cuvidMapVideoFrame(
                    m_decoder,
                    display->picture_index,
                    &deviceFrame,
                    &sourcePitch,
                    &process);

            if (result != CUDA_SUCCESS)
            {
                Fail(
                    result,
                    L"cuvidMapVideoFrame");

                return 1;
            }

            if (directD3D)
            {
                std::int32_t renderResult =
                    m_renderer->PresentNv12(
                        deviceFrame,
                        sourcePitch,
                        m_width.load(),
                        m_height.load(),
                        m_surfaceHeight);

                if (renderResult < 0)
                {
                    SetMessage(
                        m_renderer->
                            GetLastMessage());
                }
            }
            else
            {
                BuildPreviewFromNv12(
                    deviceFrame,
                    sourcePitch,
                    frameId);

                m_lastPreviewAt =
                    now;
            }

            result =
                cuvidUnmapVideoFrame(
                    m_decoder,
                    deviceFrame);

            if (result != CUDA_SUCCESS)
            {
                Fail(
                    result,
                    L"cuvidUnmapVideoFrame");
            }

            // Never starve the parser because presentation failed. Decode is
            // still valid and UI/render errors are reported independently.
            return 1;
        }

        void InspectAnnexB(
            const std::uint8_t* data,
            std::uint32_t bytes)
        {
            std::uint32_t i = 0;

            while (i + 4u < bytes)
            {
                std::uint32_t nal = 0;

                if (data[i] == 0 &&
                    data[i + 1] == 0 &&
                    data[i + 2] == 1)
                {
                    nal = i + 3u;
                }
                else if (
                    i + 4u < bytes &&
                    data[i] == 0 &&
                    data[i + 1] == 0 &&
                    data[i + 2] == 0 &&
                    data[i + 3] == 1)
                {
                    nal = i + 4u;
                }
                else
                {
                    ++i;
                    continue;
                }

                if (nal >= bytes)
                    break;

                const std::uint8_t type =
                    data[nal] & 0x1Fu;

                switch (type)
                {
                case 1:
                    m_nonIdrSlices.fetch_add(1);
                    break;
                case 5:
                    m_idrSlices.fetch_add(1);
                    break;
                case 7:
                    m_spsCount.fetch_add(1);
                    break;
                case 8:
                    m_ppsCount.fetch_add(1);
                    break;
                default:
                    break;
                }

                i = nal + 1u;
            }

            const std::uint64_t submitted =
                m_submittedFrames.load();

            // Keep the UI informative while we are still validating the
            // parser. This message is cheap and only refreshed occasionally.
            if (submitted > 0 &&
                submitted % 240u == 0 &&
                m_decodedPictures.load() == 0)
            {
                SetMessage(
                    L"Parser recebeu Annex-B: SPS " +
                    std::to_wstring(m_spsCount.load()) +
                    L" • PPS " +
                    std::to_wstring(m_ppsCount.load()) +
                    L" • IDR " +
                    std::to_wstring(m_idrSlices.load()) +
                    L" • P " +
                    std::to_wstring(m_nonIdrSlices.load()) +
                    L" • sem start-code " +
                    std::to_wstring(m_packetsWithoutStartCode.load()) +
                    L" • AVCC convertido " +
                    std::to_wstring(m_avccConverted.load()));
            }
        }

        void BuildPreviewGeometry(
            std::uint32_t width,
            std::uint32_t height)
        {
            std::uint32_t previewWidth =
                std::min(
                    width,
                    m_previewMaxWidth);

            previewWidth =
                std::max<std::uint32_t>(
                    2u,
                    previewWidth & ~1u);

            double scale =
                static_cast<double>(
                    previewWidth) /
                static_cast<double>(
                    width);

            std::uint32_t previewHeight =
                static_cast<std::uint32_t>(
                    std::lround(
                        height * scale));

            previewHeight =
                std::max<std::uint32_t>(
                    2u,
                    previewHeight & ~1u);

            m_previewWidth.store(
                previewWidth);

            m_previewHeight.store(
                previewHeight);

            std::lock_guard lock(
                m_previewMutex);

            m_previewBgra.assign(
                static_cast<size_t>(
                    previewWidth) *
                    previewHeight *
                    4u,
                0);

            m_previewGeneration++;
            m_lastCopiedGeneration =
                m_previewGeneration;
        }

        void BuildPreviewFromNv12(
            CUdeviceptr deviceFrame,
            unsigned int sourcePitch,
            std::uint64_t frameId)
        {
            const std::uint32_t width =
                m_width.load();

            const std::uint32_t height =
                m_height.load();

            const std::uint32_t previewWidth =
                m_previewWidth.load();

            const std::uint32_t previewHeight =
                m_previewHeight.load();

            if (width == 0 ||
                height == 0 ||
                previewWidth == 0 ||
                previewHeight == 0)
            {
                return;
            }

            size_t yBytes =
                static_cast<size_t>(
                    width) *
                height;

            size_t uvBytes =
                yBytes / 2u;

            if (m_nv12Host.size() !=
                yBytes + uvBytes)
            {
                m_nv12Host.resize(
                    yBytes + uvBytes);
            }

            CUDA_MEMCPY2D copyY{};

            copyY.srcMemoryType =
                CU_MEMORYTYPE_DEVICE;

            copyY.srcDevice =
                deviceFrame;

            copyY.srcPitch =
                sourcePitch;

            copyY.dstMemoryType =
                CU_MEMORYTYPE_HOST;

            copyY.dstHost =
                m_nv12Host.data();

            copyY.dstPitch =
                width;

            copyY.WidthInBytes =
                width;

            copyY.Height =
                height;

            CUresult result =
                cuMemcpy2D(
                    &copyY);

            if (result != CUDA_SUCCESS)
            {
                Fail(
                    result,
                    L"cuMemcpy2D(Y)");

                return;
            }

            CUDA_MEMCPY2D copyUv{};

            copyUv.srcMemoryType =
                CU_MEMORYTYPE_DEVICE;

            copyUv.srcDevice =
                deviceFrame +
                static_cast<CUdeviceptr>(
                    sourcePitch) *
                m_surfaceHeight;

            copyUv.srcPitch =
                sourcePitch;

            copyUv.dstMemoryType =
                CU_MEMORYTYPE_HOST;

            copyUv.dstHost =
                m_nv12Host.data() +
                yBytes;

            copyUv.dstPitch =
                width;

            copyUv.WidthInBytes =
                width;

            copyUv.Height =
                height / 2u;

            result =
                cuMemcpy2D(
                    &copyUv);

            if (result != CUDA_SUCCESS)
            {
                Fail(
                    result,
                    L"cuMemcpy2D(UV)");

                return;
            }

            std::vector<std::uint8_t>
                converted(
                    static_cast<size_t>(
                        previewWidth) *
                    previewHeight *
                    4u);

            const std::uint8_t* yPlane =
                m_nv12Host.data();

            const std::uint8_t* uvPlane =
                m_nv12Host.data() +
                yBytes;

            for (std::uint32_t py = 0;
                 py < previewHeight;
                 ++py)
            {
                std::uint32_t sy =
                    static_cast<std::uint32_t>(
                        (
                            static_cast<std::uint64_t>(
                                py) *
                            height
                        ) /
                        previewHeight);

                sy =
                    std::min(
                        sy,
                        height - 1u);

                for (std::uint32_t px = 0;
                     px < previewWidth;
                     ++px)
                {
                    std::uint32_t sx =
                        static_cast<std::uint32_t>(
                            (
                                static_cast<std::uint64_t>(
                                    px) *
                                width
                            ) /
                            previewWidth);

                    sx =
                        std::min(
                            sx,
                            width - 1u);

                    const int y =
                        yPlane[
                            static_cast<size_t>(
                                sy) *
                            width +
                            sx];

                    const size_t uvIndex =
                        static_cast<size_t>(
                            sy / 2u) *
                            width +
                        (sx & ~1u);

                    const int u =
                        uvPlane[
                            uvIndex];

                    const int v =
                        uvPlane[
                            uvIndex + 1u];

                    // BT.709 limited-range -> BGRA.
                    int c =
                        std::max(
                            0,
                            y - 16);

                    int d =
                        u - 128;

                    int e =
                        v - 128;

                    int r =
                        (298 * c +
                         459 * e +
                         128) >> 8;

                    int g =
                        (298 * c -
                         55 * d -
                         136 * e +
                         128) >> 8;

                    int b =
                        (298 * c +
                         541 * d +
                         128) >> 8;

                    size_t outIndex =
                        (
                            static_cast<size_t>(
                                py) *
                            previewWidth +
                            px
                        ) *
                        4u;

                    converted[
                        outIndex + 0u] =
                        Clamp8(b);

                    converted[
                        outIndex + 1u] =
                        Clamp8(g);

                    converted[
                        outIndex + 2u] =
                        Clamp8(r);

                    converted[
                        outIndex + 3u] =
                        255u;
                }
            }

            {
                std::lock_guard lock(
                    m_previewMutex);

                m_previewBgra.swap(
                    converted);

                m_previewFrameId =
                    frameId;

                m_previewGeneration++;
            }

            m_previewFrames.fetch_add(
                1);
        }

        std::int32_t Fail(
            CUresult result,
            const wchar_t* stage)
        {
            std::int32_t status =
                NvdecResultToStatus(
                    result);

            m_lastStatus.store(
                status);

            SetMessage(
                std::wstring(
                    L"Falha NVDEC em [") +
                stage +
                L"]: " +
                CudaResultText(
                    result));

            return status;
        }

        void SetMessage(
            const std::wstring& message) const
        {
            std::lock_guard lock(
                m_messageMutex);

            m_lastMessage =
                message;
        }

    private:
        const std::uint32_t m_expectedWidth;
        const std::uint32_t m_expectedHeight;
        const std::uint32_t m_previewMaxWidth;

        CUdevice m_device = 0;
        CUcontext m_context = nullptr;
        CUvideoctxlock m_ctxLock = nullptr;
        CUvideoparser m_parser = nullptr;
        CUvideodecoder m_decoder = nullptr;

        std::unique_ptr<
            D3D11NvdecRenderer>
            m_renderer;

        Clock::time_point m_timestampOrigin{};
        std::atomic<CUvideotimestamp>
            m_lastSubmittedTimestamp{0};

        std::uint32_t m_surfaceHeight = 0;

        std::atomic<bool> m_running{false};
        std::atomic<bool> m_formatReady{false};
        std::atomic<bool> m_discontinuityRequested{false};
        std::atomic<bool> m_headlessOutput{false};

        std::atomic<std::int32_t> m_lastStatus{0};

        std::atomic<std::uint32_t> m_width{0};
        std::atomic<std::uint32_t> m_height{0};
        std::atomic<std::uint32_t> m_previewWidth{0};
        std::atomic<std::uint32_t> m_previewHeight{0};

        std::atomic<std::uint64_t> m_submittedFrames{0};
        std::atomic<std::uint64_t> m_decodedPictures{0};
        std::atomic<std::uint64_t> m_displayedFrames{0};
        std::atomic<std::uint64_t> m_previewFrames{0};

        std::atomic<std::uint64_t> m_spsCount{0};
        std::atomic<std::uint64_t> m_ppsCount{0};
        std::atomic<std::uint64_t> m_idrSlices{0};
        std::atomic<std::uint64_t> m_nonIdrSlices{0};
        std::atomic<std::uint64_t> m_packetsWithoutStartCode{0};
        std::atomic<std::uint64_t> m_avccConverted{0};

        std::atomic<std::uint64_t> m_lastLatencyUs{0};
        std::atomic<std::uint64_t> m_totalLatencyUs{0};
        std::atomic<std::uint64_t> m_latencySamples{0};

        std::array<SubmitStamp, 512>
            m_submitStamps{};

        Clock::time_point m_lastPreviewAt{};

        std::vector<std::uint8_t>
            m_nv12Host;

        mutable std::mutex
            m_previewMutex;

        std::vector<std::uint8_t>
            m_previewBgra;

        std::uint64_t m_previewFrameId = 0;
        std::uint64_t m_previewGeneration = 0;
        std::uint64_t m_lastCopiedGeneration = 0;

        mutable std::mutex
            m_messageMutex;

        mutable std::wstring
            m_lastMessage =
                L"NVDEC não iniciado.";
    };

#endif
}

std::int32_t __cdecl MV_NvdecIsCompiled()
{
#if defined(MULTIVERSE_NVDEC)
    return 1;
#else
    return 0;
#endif
}

std::int32_t __cdecl
MV_NvdecRuntimeLibraryAvailable()
{
#if defined(MULTIVERSE_NVDEC)
    HMODULE cuda =
        LoadLibraryW(
            L"nvcuda.dll");

    HMODULE cuvid =
        LoadLibraryW(
            L"nvcuvid.dll");

    if (cuda)
        FreeLibrary(cuda);

    if (cuvid)
        FreeLibrary(cuvid);

    return cuda && cuvid
        ? 1
        : 0;
#else
    return 0;
#endif
}

namespace {
struct DecoderHandle {
#if defined(MULTIVERSE_NVDEC)
    std::unique_ptr<NativeNvdecDecoder> nvdec;
#endif
    std::unique_ptr<MfVideoDecoder> windows;
};
template<class Action> int32_t WithDecoder(void* opaque, Action action) {
    auto* handle=static_cast<DecoderHandle*>(opaque);
    if(!handle)return -20023;
    try {
#if defined(MULTIVERSE_NVDEC)
        if(handle->nvdec)return action(*handle->nvdec);
#endif
        if(handle->windows)return action(*handle->windows);
    } catch(...) { return -20021; }
    return -20023;
}
}
int32_t __cdecl MV_DecoderCreate(uint32_t width,uint32_t height,uint32_t preview,void** output) {
    if(!output)return -20020;
    *output=nullptr;
    try {
        auto handle=std::make_unique<DecoderHandle>();
        wchar_t mode[32]{};
        GetEnvironmentVariableW(L"LEVELING_DECODER",mode,32);
        bool forceMf=_wcsicmp(mode,L"mf")==0||_wcsicmp(mode,L"mf-software")==0;
#if defined(MULTIVERSE_NVDEC)
        // Preserve the established NVIDIA path. Windows decoding is a fallback,
        // or explicitly selected by the diagnostic harness.
        if(!forceMf&&MV_NvdecRuntimeLibraryAvailable()) {
            auto candidate=std::make_unique<NativeNvdecDecoder>(width,height,preview);
            if(candidate->Initialize()>=0)handle->nvdec=std::move(candidate);
        }
        if(!handle->nvdec)
#endif
        {
            handle->windows=std::make_unique<MfVideoDecoder>(width,height,_wcsicmp(mode,L"mf-software")==0);
            auto result=handle->windows->Initialize();
            if(result<0)return result;
        }
        *output=handle.release();return 0;
    } catch(...) {return -20021;}
}
void __cdecl MV_DecoderDestroy(void* h){delete static_cast<DecoderHandle*>(h);}
int32_t __cdecl MV_DecoderPushAnnexB(void* h,const uint8_t* data,uint32_t bytes,uint64_t id,int32_t key){return WithDecoder(h,[&](auto& d){return d.Push(data,bytes,id,key!=0);});}
int32_t __cdecl MV_DecoderGetStats(void* h,MV_DecoderStats* stats){if(!stats)return -20024;return WithDecoder(h,[&](auto& d){d.FillStats(*stats);return 0;});}
int32_t __cdecl MV_DecoderSignalDiscontinuity(void* h){return WithDecoder(h,[](auto& d){d.SignalDiscontinuity();return 0;});}
int32_t __cdecl MV_DecoderSetHeadlessOutput(void* h,int32_t enabled){return WithDecoder(h,[&](auto& d){d.SetHeadlessOutput(enabled!=0);return 0;});}
int32_t __cdecl MV_DecoderAttachSwapChainPanel(void* h,void* panel,uint32_t w,uint32_t height){if(!panel)return -20100;return WithDecoder(h,[&](auto& d){return d.AttachSwapChainPanel(static_cast<IUnknown*>(panel),w,height);});}
int32_t __cdecl MV_DecoderResizeSwapChain(void* h,uint32_t w,uint32_t height){return WithDecoder(h,[&](auto& d){return d.ResizeSwapChain(w,height);});}
int32_t __cdecl MV_DecoderSetCompositionScale(void* h,float x,float y){return WithDecoder(h,[&](auto& d){return d.SetCompositionScale(x,y);});}
int32_t __cdecl MV_DecoderTryDetachPanel(void* h){if(!h)return 0;return WithDecoder(h,[](auto& d){return d.TryDetachPanel();});}
void __cdecl MV_DecoderDetachSwapChainPanel(void* h){WithDecoder(h,[](auto& d){d.DetachSwapChainPanel();return 0;});}
int32_t __cdecl MV_DecoderGetD3D11RendererStats(void* h,MV_D3D11RendererStats* stats){if(!stats)return -20101;return WithDecoder(h,[&](auto& d){d.FillRendererStats(*stats);return 0;});}
int32_t __cdecl MV_DecoderGetPreviewSize(void* h,uint32_t* w,uint32_t* height,uint32_t* stride,uint32_t* bytes){if(!w||!height||!stride||!bytes)return -20024;return WithDecoder(h,[&](auto& d){d.GetPreviewInfo(*w,*height,*stride,*bytes);return 0;});}
int32_t __cdecl MV_DecoderCopyLatestPreview(void* h,uint8_t* data,uint32_t bytes,uint64_t* id){if(!id)return -20024;return WithDecoder(h,[&](auto& d){return d.CopyPreview(data,bytes,*id);});}
int32_t __cdecl MV_DecoderGetLastMessage(void* h,wchar_t* destination,uint32_t chars){if(!destination||!chars)return -20024;return WithDecoder(h,[&](auto& d){d.CopyMessage(destination,chars);return 0;});}