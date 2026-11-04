#pragma once

#include <cstdint>
#include <string>

#if defined(MULTIVERSE_NVDEC)

#include <Windows.h>
#include <d3d11.h>
#include <dxgi1_3.h>
#include <cuda.h>

struct MV_D3D11RendererStatsNative
{
    std::int32_t Attached = 0;
    std::int32_t LastStatus = 0;

    std::uint32_t TargetWidth = 0;
    std::uint32_t TargetHeight = 0;
    std::uint32_t SourceWidth = 0;
    std::uint32_t SourceHeight = 0;

    std::uint64_t SubmittedFrames = 0;
    std::uint64_t PresentedFrames = 0;
    std::uint64_t DroppedPresents = 0;
    std::uint64_t ReplacedPendingFrames = 0;
    std::uint64_t PresentWaitTimeouts = 0;

    // CORE 11.7 NonBlockingPresenter. The original probe counters remain
    // visible so signal/bypass/STILL_DRAWING behavior can be verified live.
    std::uint64_t PresenterDequeuedFrames = 0;
    std::uint64_t FrameLatencySignals = 0;
    std::uint64_t FrameLatencyBypasses = 0;
    std::uint64_t FrameLatencyWaitFailures = 0;
    std::uint64_t PresentAttempts = 0;
    std::uint64_t PresentStillDrawing = 0;
    std::uint64_t PresentFailures = 0;

    std::uint32_t ForegroundProcessId = 0;
    std::int32_t ForegroundIsCurrentProcess = 0;
    std::uint32_t PresenterStage = 0;
    std::int32_t PresentInFlight = 0;

    double LastGpuCopyMs = 0.0;
    double AverageGpuCopyMs = 0.0;
    double LastPresentSubmitMs = 0.0;
    double AveragePresentSubmitMs = 0.0;
    double LastPresentWaitMs = 0.0;
    double AveragePresentWaitMs = 0.0;

    double LastQueueToPresenterMs = 0.0;
    double AverageQueueToPresenterMs = 0.0;
    double LastStateLockWaitMs = 0.0;
    double AverageStateLockWaitMs = 0.0;
    double LastPresentCallMs = 0.0;
    double AveragePresentCallMs = 0.0;
    double LastFrameAgeAtPresentMs = 0.0;
    double AverageFrameAgeAtPresentMs = 0.0;
    double PresenterActivityAgeMs = 0.0;
};

class D3D11NvdecRenderer final
{
public:
    D3D11NvdecRenderer();
    ~D3D11NvdecRenderer();

    D3D11NvdecRenderer(
        const D3D11NvdecRenderer&) = delete;

    D3D11NvdecRenderer& operator=(
        const D3D11NvdecRenderer&) = delete;

    std::int32_t AttachPanel(
        IUnknown* panelUnknown,
        CUdevice cudaDevice,
        std::uint32_t width,
        std::uint32_t height);

    std::int32_t Resize(
        std::uint32_t width,
        std::uint32_t height);

    // Physical buffer pixels per WinUI effective pixel. No presenter restart.
    std::int32_t SetCompositionScale(float scaleX, float scaleY);

    void DetachPanel();
    bool TryStopPresenter();

    void Shutdown();

    bool IsAttached() const;

    std::int32_t PresentNv12(
        CUdeviceptr sourceNv12,
        unsigned int sourcePitch,
        std::uint32_t sourceWidth,
        std::uint32_t sourceHeight,
        std::uint32_t sourceSurfaceHeight);

    void GetStats(
        MV_D3D11RendererStatsNative& stats) const;

    std::wstring GetLastMessage() const;

private:
    struct Impl;
    Impl* m_impl = nullptr;
};

#endif
