#pragma once
#include <cstdint>

#if defined(MULTIVERSECORE_EXPORTS)
#define MV_API extern "C" __declspec(dllexport)
#else
#define MV_API extern "C" __declspec(dllimport)
#endif

struct MV_GpuInfo
{
    wchar_t Name[128];
    std::uint64_t DedicatedVideoMemory;
    std::uint32_t VendorId;
    std::uint32_t DeviceId;
    std::int32_t IsSoftware;
};

struct MV_MonitorInfo
{
    wchar_t DeviceName[32];
    std::int32_t Left;
    std::int32_t Top;
    std::int32_t Right;
    std::int32_t Bottom;
    std::uint32_t Width;
    std::uint32_t Height;
    std::uint32_t RefreshRate;
    std::int32_t IsPrimary;
};

struct MV_EncoderConfig
{
    std::uint32_t BitrateKbps;
    std::uint32_t Fps;

    // 0 = Auto (P1 em 120+ FPS, P2 abaixo)
    // 1=P1, 2=P2, 3=P3, 4=P4
    std::uint32_t Preset;

    // 1=Ultra Low Latency, 0=Low Latency
    std::uint32_t UltraLowLatency;

    // Limite do stream remoto. 0 = sem limite naquele eixo.
    // Limite opcional do stream. Core 09.3.2 prefere resolução nativa do host
    // quando possível para evitar scaling 3D no host sob jogo em foreground.
    std::uint32_t MaxWidth;
    std::uint32_t MaxHeight;

    // CORE 11.15: 9=WGC native shared ring -> native private ARGB NVENC copy
    // (default/recommended). No host scaling, no VideoProcessor and no CPU
    // capture-fence wait; the client renderer scales to its target viewport.
    // Fallbacks: 8=encode-device RGB scaler, 7=direct-ring RGB, 6=WGC NV12.
    // Diagnostics: 5=WGC isolated BGRA, 4=DXGI isolated BGRA,
    // 1=legacy NV12 same-device, 2=ARGB same-device, 3=synthetic ARGB.
    std::uint32_t LabInputMode;
    std::uint32_t ReservedLab;
};

struct MV_CapturePipelineStats
{
    std::uint64_t DesktopPresents;
    std::uint64_t PointerOnlyUpdates;
    std::uint64_t DxgiAccumulatedFrames;
    std::uint64_t DxgiMissedFrames;
    std::uint64_t PublishedToRing;
    std::uint64_t ReplacedInRing;
    std::uint64_t RingBusyDrops;
    std::uint64_t ConsumedByEncoder;
    std::uint64_t PublishPacingDrops;

    std::uint32_t LastAccumulatedFrames;
    std::uint32_t RingReadyFrames;
    std::uint32_t TargetPublishFps;
    std::uint32_t ReservedPacing;

    double LastAcquireMs;
    double AverageAcquireMs;
    double LastRingCopyMs;
    double AverageRingCopyMs;

    // Core 09.3.2 - diagnóstico de starvation/recovery do Desktop Duplication.
    std::uint64_t AcquireTimeouts;
    std::uint64_t DuplicationResets;
    std::uint64_t EncoderRestarts;
    std::int32_t LastCaptureStatus;
    std::int32_t Reserved0;

    // CORE 10.1 / API v21 - cross-device shared-ring diagnostics.
    std::uint64_t SharedCaptureSyncTimeouts;
    std::uint64_t SharedEncodeSyncTimeouts;
    std::uint64_t SharedSyncAbandoned;
    std::uint32_t Core10IsolationActive;
    std::uint32_t SharedReadyFrames;
    double SharedCaptureWaitMs;
    double SharedAverageCaptureWaitMs;
    double SharedEncodeWaitMs;
    double SharedAverageEncodeWaitMs;

    // CORE 10.1 / API v21 - active capture-backend diagnostics.
    std::uint32_t CaptureBackend; // 0=DXGI, 1=WGC free-threaded
    std::uint32_t ReservedBackend;
    std::uint64_t BackendFramesArrived;
    std::uint64_t BackendFramesPublished;
    std::uint64_t BackendPacingDrops;
    std::uint64_t BackendEmptyCallbacks;
    std::uint64_t BackendRestarts;
    double LastBackendCallbackMs;
    double AverageBackendCallbackMs;
    double LastBackendFrameAgeMs;
    double AverageBackendFrameAgeMs;
    std::int32_t LastBackendStatus;
    std::int32_t ReservedBackendStatus;

    // CORE 11.1D / API v23 - liveness + WGC shutdown diagnostics.
    std::uint32_t BackendRunning;
    std::uint32_t BackendCallbacksInFlight;
    std::uint32_t BackendStopInProgress;
    std::uint32_t BackendStopStage;
    double BackendLastCallbackAgoMs;
    double BackendLastCallbackCompleteAgoMs;
    double BackendStopStageAgeMs;
    double LastPublishAgoMs;
    double LastConsumeAgoMs;
    std::uint32_t EncoderWorkerAlive;
    std::uint32_t ReservedLiveness;

    // CORE 11.1E / API v24 - exact in-flight callback/ring/worker stages.
    std::uint32_t BackendCallbackStage;
    std::uint32_t BackendCallbackSubstage;
    std::uint64_t BackendCurrentDrainFrames;
    double BackendCallbackStageAgeMs;
    std::uint32_t CoreWgcStage;
    std::uint32_t RingPublishStage;
    std::uint32_t EncoderWorkerStage;
    std::uint32_t ReservedStageDiag;
    double CoreWgcStageAgeMs;
    double RingPublishStageAgeMs;
    double EncoderWorkerStageAgeMs;

    // CORE 11.12 / API v26 - exact SharedRing acquire/fence probe.
    std::uint64_t RingAcquireCalls;
    std::uint64_t RingAcquireNoReadyFrames;
    std::uint64_t RingAcquireReadySelections;
    std::uint64_t RingCaptureFenceReady;
    std::uint64_t RingCaptureFencePending;
    std::uint64_t RingPublishNoReusableSlots;
    double RingAcquireWaitMs;
    double RingAverageAcquireWaitMs;
    double RingLeaseAgeMs;
    double RingAverageLeaseAgeMs;
    std::uint64_t RingCaptureFenceLag;
    std::uint64_t RingMaxCaptureFenceLag;
    std::uint32_t RingReleaseFencePendingSlots;
    std::uint32_t RingProbeReserved0;

    // CORE 11.17 / API v27 - free-threaded WGC delivery worker + adaptive
    // host pacing. Appended to preserve the existing field order.
    std::uint64_t BackendFramesDelivered;
    std::uint64_t BackendPendingFramesReplaced;
    std::uint32_t BackendDeliveryWorkerAlive;
    std::uint32_t AdaptivePressureState; // 0=NORMAL, 1=PRESSURE (legacy RELIEF state), 2=RECOVERING
    std::uint32_t AdaptiveTargetFps;
    std::uint32_t AdaptivePressureStreak;
    std::uint32_t AdaptiveHealthyStreak;
    std::uint32_t AdaptiveTransitions;
    double BackendLastDeliveryDelayMs;
    std::uint32_t CaptureRingOccupiedFrames;
    std::uint32_t Core1117Reserved0;

    // CORE 11.17c / API v28 - observational D3D11_QUERY_EVENT probe placed
    // immediately after the same-device CopyResource. No Flush/wait is used.
    std::uint64_t CopyGpuProbeSignals;
    std::uint64_t CopyGpuProbeCompleted;
    std::uint64_t CopyGpuReadyAtSubmit;
    std::uint64_t CopyGpuNotReadyAtSubmit;
    std::uint32_t CopyGpuProbePending;
    std::uint32_t CopyGpuProbeMaxPending;
    double CopyGpuLastLatencyMs;
    double CopyGpuAverageLatencyMs;
    double CopyGpuMaxLatencyMs;
    double CopyGpuOldestPendingMs;
    std::int32_t CopyGpuProbeStatus;
    std::uint32_t Core1117cReserved0;
};

struct MV_EncoderStats
{
    std::int32_t Running;
    std::int32_t LastStatus;

    std::uint32_t Width;
    std::uint32_t Height;
    std::uint32_t TargetFps;
    std::uint32_t BitrateKbps;

    // Frames cujo bitstream ficou pronto.
    std::uint64_t EncodedFrames;
    std::uint64_t EncodedBytes;

    // Frames descartados pelo limitador de FPS.
    std::uint64_t PacingDrops;

    // Resets da fila de pacotes para consumidor de rede/decoder.
    std::uint64_t QueueResets;

    std::uint32_t QueuedPackets;
    std::uint32_t QueuedBytes;

    // Compatibilidade: agora representa submit -> bitstream pronto.
    double LastEncodeMs;
    double AverageEncodeMs;

    // Core API v4 - métricas de pipeline.
    std::uint64_t SubmittedFrames;
    std::uint64_t BackpressureDrops;

    std::uint32_t InFlightFrames;
    std::uint32_t InputSlots;

    // Tempo de CPU gasto para CopyResource + Map + NvEncEncodePicture.
    double LastSubmitMs;
    double AverageSubmitMs;

    // Latência real do frame: submit -> evento/bitstream pronto.
    double LastOutputLatencyMs;
    double AverageOutputLatencyMs;

    // Core 09.3.2 - diagnóstico fino do caminho de entrada/submit.
    // 0=desconhecido, 1=NV12/D3D11 VideoProcessor, 2=ARGB fallback.
    std::int32_t InputMode;
    std::int32_t RequestedInputMode;
    std::uint64_t MapFailures;
    std::uint64_t EncodeErrors;
    std::uint64_t LockBusyRetries;
    double LastPreprocessMs;
    double AveragePreprocessMs;

    // Core 09.3.2 - divide o submit para localizar sincronização no driver.
    double LastMapMs;
    double AverageMapMs;
    double LastEncodeCallMs;
    double AverageEncodeCallMs;
    double LastLockBitstreamMs;
    double AverageLockBitstreamMs;

    // Core 09.3.4 - custo da leitura do snapshot, nunca do NVENC ao vivo.
    double StatsReadWaitMs;
    std::uint32_t PendingCommands;
    // 0=captura real, 1=fonte sintética do laboratório.
    std::uint32_t SourceMode;

    // Core 09.3.6 / API v15 - lifecycle NVENC explícito.
    std::uint64_t NeedMoreInputAccepted;
    std::uint64_t EncoderBusyRetries;
    std::uint64_t NoFreeSlotDrops;
    std::uint32_t PendingOutputs;
    std::uint64_t FlushTimeouts;
    double OldestInFlightMs;

    // CORE 11 / API v22. 6=WGC/NV12, 5=WGC/BGRA, 4=DXGI/BGRA.
    std::uint32_t Core10PipelineMode;
    std::uint32_t CaptureDeviceIsolated;

    // CORE 11 / API v22. Verified per-device GPU scheduling telemetry.
    std::uint32_t Core11GpuPriority;
    std::int32_t CaptureGpuPriority;
    std::int32_t EncodeGpuPriority;
    std::int32_t GpuPrioritySetStatus;

    // CORE 11.10 / API v25 - WDDM process scheduling class.
    // 0 idle, 1 below-normal, 2 normal, 3 above-normal, 4 high, 5 realtime.
    std::int32_t ProcessGpuPriorityRequested;
    std::int32_t ProcessGpuPriorityEffective;
    std::int32_t ProcessGpuPrioritySetStatus;
    std::int32_t ProcessGpuPriorityRestoreStatus;

    // CORE 11.1D / API v23 - encoder/output liveness + shutdown checkpoint.
    std::uint32_t EncoderWorkerAlive;
    std::uint32_t OutputWorkerAlive;
    std::uint32_t StopInProgress;
    std::uint32_t StopStage;
    double LastSubmitAgoMs;
    double LastOutputAgoMs;
    double StopStageAgeMs;
    std::uint32_t NvencStopStage;
    std::uint32_t ReservedNvencStop;
    double NvencStopStageAgeMs;

    // CORE 11.12 / API v26 - non-blocking GPU completion probes around the
    // encode-device BGRA copy and VideoProcessor. Values are fence sequence
    // counters; no CPU wait is introduced by these diagnostics.
    std::uint64_t PreprocessCopyFenceSignals;
    std::uint64_t PreprocessCopyFenceCompleted;
    std::uint32_t PreprocessCopyFencePending;
    std::uint32_t PreprocessCopyFenceMaxPending;
    double PreprocessCopyFenceOldestAgeMs;
    std::uint64_t VideoProcessorFenceSignals;
    std::uint64_t VideoProcessorFenceCompleted;
    std::uint32_t VideoProcessorFencePending;
    std::uint32_t VideoProcessorFenceMaxPending;
    double VideoProcessorFenceOldestAgeMs;
};

// Hardware / captura
MV_API std::int32_t __cdecl MV_GetApiVersion();

MV_API std::int32_t __cdecl MV_GetGpuCount();
MV_API std::int32_t __cdecl MV_GetGpuInfo(
    std::int32_t index,
    MV_GpuInfo* info);

MV_API std::int32_t __cdecl MV_GetMonitorCount();
MV_API std::int32_t __cdecl MV_GetMonitorInfo(
    std::int32_t index,
    MV_MonitorInfo* info);

MV_API std::int32_t __cdecl MV_TestD3D11Device();

MV_API std::int32_t __cdecl MV_TestDesktopDuplication(
    const wchar_t* monitorDeviceName);

MV_API std::int32_t __cdecl MV_CaptureCreate(
    const wchar_t* monitorDeviceName,
    void** captureHandle,
    std::uint32_t* sourceWidth,
    std::uint32_t* sourceHeight);

MV_API void __cdecl MV_CaptureDestroy(
    void* captureHandle);

MV_API std::int32_t __cdecl MV_CaptureGetPreviewSize(
    void* captureHandle,
    std::uint32_t* previewWidth,
    std::uint32_t* previewHeight,
    std::uint32_t* previewStride,
    std::uint32_t* requiredBufferBytes);

MV_API std::int32_t __cdecl MV_CaptureGetFrameCount(
    void* captureHandle,
    std::uint64_t* frameCount);

MV_API std::int32_t __cdecl MV_CaptureGetPipelineStats(
    void* captureHandle,
    MV_CapturePipelineStats* stats);

MV_API std::int32_t __cdecl MV_CaptureGetLastError(
    void* captureHandle);

MV_API std::int32_t __cdecl MV_CaptureCopyLatestPreview(
    void* captureHandle,
    std::uint8_t* destination,
    std::uint32_t destinationBytes,
    std::uint32_t* previewWidth,
    std::uint32_t* previewHeight,
    std::uint32_t* previewStride,
    std::uint64_t* frameId);

// NVENC / H.264
MV_API std::int32_t __cdecl MV_NvencIsCompiled();

MV_API std::int32_t __cdecl
MV_NvencRuntimeLibraryAvailable();

MV_API std::int32_t __cdecl MV_EncoderStart(
    void* captureHandle,
    const MV_EncoderConfig* config);

MV_API void __cdecl MV_EncoderBlockSubmissions(void* captureHandle);

MV_API void __cdecl MV_EncoderStop(
    void* captureHandle);

MV_API std::int32_t __cdecl MV_EncoderRequestIdr(
    void* captureHandle);

MV_API std::int32_t __cdecl MV_EncoderReconfigureBitrate(
    void* captureHandle,
    std::uint32_t bitrateKbps);

MV_API std::int32_t __cdecl
MV_EncoderSetPacketConsumerActive(
    void* captureHandle,
    std::int32_t active);

MV_API std::int32_t __cdecl MV_EncoderGetStats(
    void* captureHandle,
    MV_EncoderStats* stats);

MV_API std::int32_t __cdecl
MV_EncoderGetLastMessage(
    void* captureHandle,
    wchar_t* destination,
    std::uint32_t destinationChars);

// Fila H.264 Annex-B para Core 04/05.
// Por padrão o encoder NÃO copia bitstream para esta fila.
// O primeiro Peek/Pop ativa o consumidor e força um IDR.
MV_API std::int32_t __cdecl MV_EncoderPeekPacket(
    void* captureHandle,
    std::uint32_t* requiredBytes,
    std::uint64_t* frameId,
    std::int32_t* keyFrame);

MV_API std::int32_t __cdecl MV_EncoderPopPacket(
    void* captureHandle,
    std::uint8_t* destination,
    std::uint32_t destinationBytes,
    std::uint32_t* writtenBytes,
    std::uint64_t* frameId,
    std::int32_t* keyFrame);

// NVDEC / H.264 - Core 06
struct MV_DecoderStats
{
    std::int32_t Running;
    std::int32_t FormatReady;
    std::int32_t LastStatus;
    std::int32_t Reserved0;

    std::uint32_t Width;
    std::uint32_t Height;
    std::uint32_t PreviewWidth;
    std::uint32_t PreviewHeight;

    std::uint64_t SubmittedFrames;
    std::uint64_t DecodedPictures;
    std::uint64_t DisplayedFrames;
    std::uint64_t PreviewFrames;

    double LastDecodeLatencyMs;
    double AverageDecodeLatencyMs;
};

MV_API std::int32_t __cdecl MV_NvdecIsCompiled();

MV_API std::int32_t __cdecl
MV_NvdecRuntimeLibraryAvailable();

MV_API std::int32_t __cdecl MV_DecoderCreate(
    std::uint32_t expectedWidth,
    std::uint32_t expectedHeight,
    std::uint32_t previewMaxWidth,
    void** decoderHandle);

MV_API void __cdecl MV_DecoderDestroy(
    void* decoderHandle);

MV_API std::int32_t __cdecl MV_DecoderPushAnnexB(
    void* decoderHandle,
    const std::uint8_t* data,
    std::uint32_t bytes,
    std::uint64_t frameId,
    std::int32_t keyFrame);

MV_API std::int32_t __cdecl MV_DecoderGetStats(
    void* decoderHandle,
    MV_DecoderStats* stats);


MV_API std::int32_t __cdecl
MV_DecoderSignalDiscontinuity(
    void* decoderHandle);

// CORE 11.8 diagnostic isolation: when enabled the NVDEC display callback
// counts decoded/displayed pictures but does not map them for preview or D3D11.
MV_API std::int32_t __cdecl
MV_DecoderSetHeadlessOutput(
    void* decoderHandle,
    std::int32_t enabled);

MV_API std::int32_t __cdecl
MV_DecoderGetPreviewSize(
    void* decoderHandle,
    std::uint32_t* width,
    std::uint32_t* height,
    std::uint32_t* stride,
    std::uint32_t* requiredBytes);

MV_API std::int32_t __cdecl
MV_DecoderCopyLatestPreview(
    void* decoderHandle,
    std::uint8_t* destination,
    std::uint32_t destinationBytes,
    std::uint64_t* previewFrameId);

MV_API std::int32_t __cdecl
MV_DecoderGetLastMessage(
    void* decoderHandle,
    wchar_t* destination,
    std::uint32_t destinationChars);

// D3D11 direct presentation - Core 07
struct MV_D3D11RendererStats
{
    std::int32_t Attached;
    std::int32_t LastStatus;

    std::uint32_t TargetWidth;
    std::uint32_t TargetHeight;
    std::uint32_t SourceWidth;
    std::uint32_t SourceHeight;

    std::uint64_t SubmittedFrames;
    std::uint64_t PresentedFrames;
    std::uint64_t DroppedPresents;
    std::uint64_t ReplacedPendingFrames;
    std::uint64_t PresentWaitTimeouts;

    std::uint64_t PresenterDequeuedFrames;
    std::uint64_t FrameLatencySignals;
    std::uint64_t FrameLatencyBypasses;
    std::uint64_t FrameLatencyWaitFailures;
    std::uint64_t PresentAttempts;
    std::uint64_t PresentStillDrawing;
    std::uint64_t PresentFailures;

    std::uint32_t ForegroundProcessId;
    std::int32_t ForegroundIsCurrentProcess;
    std::uint32_t PresenterStage;
    std::int32_t PresentInFlight;

    double LastGpuCopyMs;
    double AverageGpuCopyMs;
    double LastPresentSubmitMs;
    double AveragePresentSubmitMs;
    double LastPresentWaitMs;
    double AveragePresentWaitMs;

    double LastQueueToPresenterMs;
    double AverageQueueToPresenterMs;
    double LastStateLockWaitMs;
    double AverageStateLockWaitMs;
    double LastPresentCallMs;
    double AveragePresentCallMs;
    double LastFrameAgeAtPresentMs;
    double AverageFrameAgeAtPresentMs;
    double PresenterActivityAgeMs;
};

MV_API std::int32_t __cdecl
MV_DecoderAttachSwapChainPanel(
    void* decoderHandle,
    void* panelUnknown,
    std::uint32_t width,
    std::uint32_t height);

MV_API std::int32_t __cdecl
MV_DecoderResizeSwapChain(
    void* decoderHandle,
    std::uint32_t width,
    std::uint32_t height);

MV_API std::int32_t __cdecl
MV_DecoderSetCompositionScale(
    void* decoderHandle,
    float scaleX,
    float scaleY);

MV_API void __cdecl
MV_DecoderDetachSwapChainPanel(
    void* decoderHandle);

MV_API std::int32_t __cdecl
MV_DecoderGetD3D11RendererStats(
    void* decoderHandle,
    MV_D3D11RendererStats* stats);
