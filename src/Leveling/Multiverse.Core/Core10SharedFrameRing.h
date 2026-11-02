#pragma once

#include <Windows.h>
#include <d3d11_4.h>
#include <d3dcompiler.h>
#include <dxgi1_2.h>
#include <wrl/client.h>

#include <array>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <mutex>

namespace multiverse
{
    class Core10SharedFrameRing final
    {
    public:
        static constexpr std::size_t SlotCount = 3;

        struct Lease
        {
            std::size_t SlotIndex = SlotCount;
            Microsoft::WRL::ComPtr<ID3D11Texture2D> Texture;
            std::uint64_t FrameId = 0;
            std::chrono::steady_clock::time_point Timestamp{};
            bool Reserved = false;

            explicit operator bool() const noexcept
            {
                return SlotIndex < SlotCount && Texture && Reserved;
            }
        };

        // Keep the public/stat ABI stable. The legacy "SyncTimeout" fields are
        // now fence-operation failure counters; normal fence synchronization is
        // GPU-queued and therefore has no CPU wait time.
        struct Stats
        {
            std::uint64_t Published = 0;
            std::uint64_t Replaced = 0;
            std::uint64_t CaptureSyncTimeouts = 0;
            std::uint64_t EncodeSyncTimeouts = 0;
            std::uint64_t SyncAbandoned = 0;
            std::uint32_t ReadyFrames = 0;
            double LastCaptureWaitMs = 0.0;
            double AverageCaptureWaitMs = 0.0;
            double LastEncodeWaitMs = 0.0;
            double AverageEncodeWaitMs = 0.0;

            // CORE 11.12 - observational ring/fence probe. These counters do
            // not change synchronization; they only classify where a frame is
            // unavailable when the encoder worker asks for the latest lease.
            std::uint64_t AcquireCalls = 0;
            std::uint64_t AcquireNoReadyFrames = 0;
            std::uint64_t AcquireReadySelections = 0;
            std::uint64_t AcquireFenceReady = 0;
            std::uint64_t AcquireFencePending = 0;
            std::uint64_t PublishNoReusableSlots = 0;
            double LastAcquireWaitMs = 0.0;
            double AverageAcquireWaitMs = 0.0;
            double LastLeaseAgeMs = 0.0;
            double AverageLeaseAgeMs = 0.0;
            std::uint64_t LastCaptureFenceLag = 0;
            std::uint64_t MaxCaptureFenceLag = 0;
            std::uint32_t ReleaseFencePendingSlots = 0;
        };

        Core10SharedFrameRing() = default;
        ~Core10SharedFrameRing();

        // CORE 11.2 fence-ring diagnostics. Values: 0=IDLE,
        // 1=SELECT_SLOT, 2=REFRESH_RELEASE_FENCE, 3=RESERVE_SLOT,
        // 4=COPY_RESOURCE, 5=SIGNAL_CAPTURE_FENCE, 6=FLUSH_CAPTURE,
        // 7=COMMIT.
        std::uint32_t DiagnosticStageValue() const noexcept;
        double DiagnosticStageAgeMs() const noexcept;

        // CORE 11.2A - lock-free init diagnostics. These intentionally reuse
        // reserved public pipeline-stat fields so ABI v24 stays unchanged.
        std::uint32_t InitStageValue() const noexcept;
        std::int32_t LastInitStatusValue() const noexcept;

        Core10SharedFrameRing(const Core10SharedFrameRing&) = delete;
        Core10SharedFrameRing& operator=(const Core10SharedFrameRing&) = delete;

        HRESULT EnsureResources(
            ID3D11Device* captureDevice,
            ID3D11DeviceContext* captureContext,
            ID3D11Device* encodeDevice,
            ID3D11DeviceContext* encodeContext,
            const D3D11_TEXTURE2D_DESC& sourceDesc,
            std::uint32_t targetWidth = 0,
            std::uint32_t targetHeight = 0);

        HRESULT Publish(
            ID3D11DeviceContext* captureContext,
            ID3D11Texture2D* source,
            std::uint64_t frameId,
            std::chrono::steady_clock::time_point timestamp,
            bool* replaced);

        // Kept source-compatible with the previous ring API. syncTimeoutMs is
        // intentionally ignored: fence waits are queued on the encode GPU and
        // never block the encoder CPU worker.
        bool AcquireLatest(
            Lease& lease,
            std::chrono::milliseconds waitForFrame,
            DWORD syncTimeoutMs = 0,
            bool requireCaptureFenceReady = false);

        void Release(
            Lease& lease,
            ID3D11DeviceContext* encodeContext) noexcept;

        // CORE 11.13 direct NVENC path: after the NVENC completion event has
        // fired and nvEncUnmapInputResource returned, no D3D11 encode-context
        // work references the shared texture. Release it immediately without
        // queuing a graphics release fence, avoiding another foreground-GPU
        // scheduling dependency.
        void ReleaseAfterExternalConsumer(Lease& lease) noexcept;

        void WakeAll() noexcept;
        void Reset() noexcept;
        std::uint32_t ReadyCount() const noexcept;
        Stats Snapshot() const noexcept;

    private:
        struct Slot
        {
            Microsoft::WRL::ComPtr<ID3D11Texture2D> CaptureTexture;
            Microsoft::WRL::ComPtr<ID3D11Texture2D> EncodeTexture;
            Microsoft::WRL::ComPtr<ID3D11RenderTargetView> CaptureRtv;
            std::uint64_t FrameId = 0;
            std::uint64_t ReleaseFenceValue = 0;
            std::chrono::steady_clock::time_point Timestamp{};
            bool Ready = false;
            bool Encoding = false;
            bool ProducerOwned = false;
        };

        HRESULT CreateResourcesLocked(
            ID3D11Device* captureDevice,
            ID3D11DeviceContext* captureContext,
            ID3D11Device* encodeDevice,
            ID3D11DeviceContext* encodeContext,
            const D3D11_TEXTURE2D_DESC& sourceDesc,
            std::uint32_t targetWidth,
            std::uint32_t targetHeight);

        HRESULT EnsureScalePipelineLocked(ID3D11Device* captureDevice);
        HRESULT ScaleIntoSlot(
            ID3D11DeviceContext* captureContext,
            ID3D11Texture2D* source,
            ID3D11RenderTargetView* targetRtv);

        void RefreshReleasedSlotsLocked() noexcept;
        bool IsSlotReusableLocked(const Slot& slot) const noexcept;

        enum class DiagnosticStage : std::uint32_t
        {
            Idle = 0,
            SelectSlot = 1,
            RefreshReleaseFence = 2,
            ReserveSlot = 3,
            CopyResource = 4,
            SignalCaptureFence = 5,
            FlushCapture = 6,
            Commit = 7
        };

        enum class InitStage : std::uint32_t
        {
            Idle = 0,
            QueryCaptureDevice5 = 1,
            QueryEncodeDevice5 = 2,
            QueryCaptureContext4 = 3,
            QueryEncodeContext4 = 4,
            CreateCaptureFence = 5,
            ShareCaptureFence = 6,
            OpenCaptureFenceOnEncode = 7,
            CreateReleaseFence = 8,
            ShareReleaseFence = 9,
            OpenReleaseFenceOnCapture = 10,
            CreateSharedTexture = 11,
            GetSharedTextureHandle = 12,
            OpenSharedTextureOnEncode = 13,
            Complete = 14
        };

        struct DiagnosticScope
        {
            Core10SharedFrameRing* Owner = nullptr;
            ~DiagnosticScope() noexcept;
        };

        void SetDiagnosticStage(DiagnosticStage stage) noexcept;
        void SetInitDiagnostic(InitStage stage, HRESULT status) noexcept;
        static double AverageMs(std::uint64_t totalUs, std::uint64_t samples) noexcept;

        std::atomic<std::uint32_t> m_diagnosticStage{ 0 };
        std::atomic<std::uint32_t> m_initStage{ 0 };
        std::atomic<std::int32_t> m_lastInitStatus{ static_cast<std::int32_t>(S_OK) };
        std::atomic<long long> m_diagnosticStageChangedAtUs{ 0 };
        mutable std::mutex m_mutex;
        std::condition_variable m_cv;
        std::array<Slot, SlotCount> m_slots{};
        std::uint32_t m_width = 0;
        std::uint32_t m_height = 0;
        std::uint32_t m_sourceWidth = 0;
        std::uint32_t m_sourceHeight = 0;
        DXGI_FORMAT m_format = DXGI_FORMAT_UNKNOWN;

        // CORE 11.13: capture-device RGB scaler. It writes the WGC frame
        // directly into the shared ring at stream resolution, so the isolated
        // EncodeDevice no longer needs CopyResource + VideoProcessor.
        Microsoft::WRL::ComPtr<ID3D11VertexShader> m_scaleVs;
        Microsoft::WRL::ComPtr<ID3D11PixelShader> m_scalePs;
        Microsoft::WRL::ComPtr<ID3D11SamplerState> m_scaleSampler;
        Microsoft::WRL::ComPtr<ID3D11Texture2D> m_scaleFallbackTexture;
        Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> m_scaleFallbackSrv;
        std::uint32_t m_scaleFallbackWidth = 0;
        std::uint32_t m_scaleFallbackHeight = 0;

        Microsoft::WRL::ComPtr<ID3D11DeviceContext4> m_captureContext4;
        Microsoft::WRL::ComPtr<ID3D11DeviceContext4> m_encodeContext4;
        Microsoft::WRL::ComPtr<ID3D11Fence> m_captureFenceCapture;
        Microsoft::WRL::ComPtr<ID3D11Fence> m_captureFenceEncode;
        Microsoft::WRL::ComPtr<ID3D11Fence> m_releaseFenceEncode;
        Microsoft::WRL::ComPtr<ID3D11Fence> m_releaseFenceCapture;
        std::uint64_t m_releaseFenceSequence = 0;

        std::uint64_t m_published = 0;
        std::uint64_t m_replaced = 0;
        std::uint64_t m_captureSyncTimeouts = 0;
        std::uint64_t m_encodeSyncTimeouts = 0;
        std::uint64_t m_syncAbandoned = 0;
        std::uint64_t m_captureWaitSamples = 0;
        std::uint64_t m_encodeWaitSamples = 0;
        std::uint64_t m_totalCaptureWaitUs = 0;
        std::uint64_t m_totalEncodeWaitUs = 0;
        long long m_lastCaptureWaitUs = 0;
        long long m_lastEncodeWaitUs = 0;

        // CORE 11.12 probe counters (all protected by m_mutex).
        std::uint64_t m_acquireCalls = 0;
        std::uint64_t m_acquireNoReadyFrames = 0;
        std::uint64_t m_acquireReadySelections = 0;
        std::uint64_t m_acquireFenceReady = 0;
        std::uint64_t m_acquireFencePending = 0;
        std::uint64_t m_publishNoReusableSlots = 0;
        std::uint64_t m_acquireWaitSamples = 0;
        std::uint64_t m_totalAcquireWaitUs = 0;
        long long m_lastAcquireWaitUs = 0;
        std::uint64_t m_leaseAgeSamples = 0;
        std::uint64_t m_totalLeaseAgeUs = 0;
        long long m_lastLeaseAgeUs = 0;
        std::uint64_t m_lastCaptureFenceLag = 0;
        std::uint64_t m_maxCaptureFenceLag = 0;
    };
}
