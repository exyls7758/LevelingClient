#pragma once

#include <Windows.h>
#include <d3d11.h>

#include <chrono>
#include <cstdint>
#include <functional>
#include <memory>

namespace multiverse
{
    class Core10WgcCapture final
    {
    public:
        struct Stats
        {
            std::uint64_t FramesArrived = 0;
            std::uint64_t EmptyCallbacks = 0;
            std::uint64_t Restarts = 0;
            double LastCallbackMs = 0.0;
            double AverageCallbackMs = 0.0;
            double LastFrameAgeMs = 0.0;
            double AverageFrameAgeMs = 0.0;

            // CORE 11.17 - free-threaded producer -> latest-frame worker.
            std::uint64_t FramesDelivered = 0;
            std::uint64_t ReplacedPendingFrames = 0;
            bool DeliveryWorkerAlive = false;
            double LastDeliveryDelayMs = 0.0;

            HRESULT LastStatus = S_OK;
            std::uint32_t CallbacksInFlight = 0;
            bool Running = false;
            double LastCallbackAgoMs = -1.0;
            double LastCallbackCompleteAgoMs = -1.0;
            bool StopInProgress = false;
            std::uint32_t StopStage = 0;
            double StopStageAgeMs = 0.0;

            // CORE 11.17 - exact stage of the minimal WGC producer callback.
            // 0=IDLE, 1=ENTER, 2=DRAIN_POOL, 3=PUBLISH_LATEST.
            // Surface extraction / D3D11 work now runs on DeliveryWorker.
            std::uint32_t CallbackStage = 0;
            // Drain substage: 0=NONE, 1=TRY_GET_NEXT, 2=GOT_FRAME.
            std::uint32_t CallbackSubstage = 0;
            std::uint64_t CurrentDrainFrames = 0;
            double CallbackStageAgeMs = 0.0;

            // CORE 11.1K DIAG - exact restart/start checkpoint and HRESULT.
            // 0=IDLE, 1=ENTER, 2=STOP_PREVIOUS, 3=IS_SUPPORTED,
            // 4=QUERY_DXGI_DEVICE, 5=CREATE_WINRT_DEVICE, 6=CREATE_ITEM,
            // 7=CREATE_FRAMEPOOL, 8=CREATE_SESSION, 9=CONFIGURE_SESSION,
            // 10=REGISTER_CALLBACKS, 11=START_CAPTURE, 12=COMPLETE.
            std::uint32_t StartStage = 0;
            HRESULT LastStartStatus = S_OK;
            double StartStageAgeMs = 0.0;
        };

        using FrameCallback = std::function<void(
            ID3D11Texture2D* texture,
            const D3D11_TEXTURE2D_DESC& desc,
            const std::chrono::steady_clock::time_point& arrival)>;

        Core10WgcCapture();
        ~Core10WgcCapture();

        Core10WgcCapture(const Core10WgcCapture&) = delete;
        Core10WgcCapture& operator=(const Core10WgcCapture&) = delete;

        HRESULT Start(
            ID3D11Device* device,
            HMONITOR monitor,
            FrameCallback callback,
            std::uint32_t targetFps = 60);

        void Stop() noexcept;
        bool IsRunning() const noexcept;
        Stats Snapshot() const noexcept;

    private:
        struct Impl;
        std::shared_ptr<Impl> m_impl;
    };
}
