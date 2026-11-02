#include "Core10SharedFrameRing.h"

#include <algorithm>
#include <cstring>
#include <limits>
#include <utility>

using Microsoft::WRL::ComPtr;

namespace
{
    long long RingSteadyNowUs() noexcept
    {
        return std::chrono::duration_cast<std::chrono::microseconds>(
            std::chrono::steady_clock::now().time_since_epoch()).count();
    }

    struct UniqueHandle final
    {
        HANDLE Value = nullptr;
        ~UniqueHandle() noexcept
        {
            if (Value)
                CloseHandle(Value);
        }
    };
}

namespace multiverse
{
    Core10SharedFrameRing::DiagnosticScope::~DiagnosticScope() noexcept
    {
        if (Owner)
            Owner->SetDiagnosticStage(DiagnosticStage::Idle);
    }

    void Core10SharedFrameRing::SetDiagnosticStage(
        DiagnosticStage stage) noexcept
    {
        m_diagnosticStage.store(
            static_cast<std::uint32_t>(stage),
            std::memory_order_release);
        m_diagnosticStageChangedAtUs.store(
            RingSteadyNowUs(),
            std::memory_order_release);
    }

    std::uint32_t Core10SharedFrameRing::DiagnosticStageValue() const noexcept
    {
        return m_diagnosticStage.load(std::memory_order_acquire);
    }

    double Core10SharedFrameRing::DiagnosticStageAgeMs() const noexcept
    {
        const auto changed =
            m_diagnosticStageChangedAtUs.load(std::memory_order_relaxed);
        if (changed <= 0)
            return -1.0;
        return std::max<long long>(0, RingSteadyNowUs() - changed) / 1000.0;
    }

    std::uint32_t Core10SharedFrameRing::InitStageValue() const noexcept
    {
        return m_initStage.load(std::memory_order_acquire);
    }

    std::int32_t Core10SharedFrameRing::LastInitStatusValue() const noexcept
    {
        return m_lastInitStatus.load(std::memory_order_acquire);
    }

    void Core10SharedFrameRing::SetInitDiagnostic(
        InitStage stage, HRESULT status) noexcept
    {
        m_initStage.store(
            static_cast<std::uint32_t>(stage),
            std::memory_order_release);
        m_lastInitStatus.store(
            static_cast<std::int32_t>(status),
            std::memory_order_release);
    }

    Core10SharedFrameRing::~Core10SharedFrameRing()
    {
        Reset();
    }

    double Core10SharedFrameRing::AverageMs(
        std::uint64_t totalUs,
        std::uint64_t samples) noexcept
    {
        return samples == 0
            ? 0.0
            : (static_cast<double>(totalUs) / static_cast<double>(samples)) / 1000.0;
    }

    void Core10SharedFrameRing::RefreshReleasedSlotsLocked() noexcept
    {
        if (!m_releaseFenceCapture)
            return;

        const std::uint64_t completed =
            m_releaseFenceCapture->GetCompletedValue();

        for (auto& slot : m_slots)
        {
            if (slot.ReleaseFenceValue != 0 &&
                !slot.Ready &&
                !slot.Encoding &&
                !slot.ProducerOwned &&
                completed >= slot.ReleaseFenceValue)
            {
                slot.ReleaseFenceValue = 0;
            }
        }
    }

    bool Core10SharedFrameRing::IsSlotReusableLocked(
        const Slot& slot) const noexcept
    {
        return !slot.Ready &&
            !slot.Encoding &&
            !slot.ProducerOwned &&
            slot.ReleaseFenceValue == 0;
    }

    HRESULT Core10SharedFrameRing::EnsureResources(
        ID3D11Device* captureDevice,
        ID3D11DeviceContext* captureContext,
        ID3D11Device* encodeDevice,
        ID3D11DeviceContext* encodeContext,
        const D3D11_TEXTURE2D_DESC& sourceDesc,
        std::uint32_t targetWidth,
        std::uint32_t targetHeight)
    {
        if (!captureDevice || !captureContext ||
            !encodeDevice || !encodeContext ||
            sourceDesc.Width == 0 || sourceDesc.Height == 0)
        {
            return E_INVALIDARG;
        }

        targetWidth = targetWidth == 0 ? sourceDesc.Width : targetWidth;
        targetHeight = targetHeight == 0 ? sourceDesc.Height : targetHeight;

        if (targetWidth == 0 || targetHeight == 0)
            return E_INVALIDARG;

        std::lock_guard<std::mutex> lock(m_mutex);

        const bool scalePipelineReady =
            (targetWidth == sourceDesc.Width && targetHeight == sourceDesc.Height) ||
            (m_scaleVs && m_scalePs && m_scaleSampler);

        if (m_width == targetWidth &&
            m_height == targetHeight &&
            m_sourceWidth == sourceDesc.Width &&
            m_sourceHeight == sourceDesc.Height &&
            m_format == sourceDesc.Format &&
            scalePipelineReady &&
            m_slots[0].CaptureTexture &&
            m_slots[0].EncodeTexture &&
            m_captureFenceCapture &&
            m_captureFenceEncode &&
            m_releaseFenceEncode &&
            m_releaseFenceCapture &&
            m_captureContext4 &&
            m_encodeContext4)
        {
            return S_OK;
        }

        RefreshReleasedSlotsLocked();

        const std::uint64_t captureCompleted =
            m_captureFenceCapture
                ? m_captureFenceCapture->GetCompletedValue()
                : std::numeric_limits<std::uint64_t>::max();

        for (const auto& slot : m_slots)
        {
            if (slot.Encoding || slot.ProducerOwned ||
                slot.ReleaseFenceValue != 0)
            {
                return HRESULT_FROM_WIN32(ERROR_BUSY);
            }

            // A Ready frame can be discarded during a resize only after the
            // capture GPU has completed the copy that produced it.
            if (slot.Ready &&
                m_captureFenceCapture &&
                captureCompleted < slot.FrameId)
            {
                return HRESULT_FROM_WIN32(ERROR_BUSY);
            }
        }

        return CreateResourcesLocked(
            captureDevice,
            captureContext,
            encodeDevice,
            encodeContext,
            sourceDesc,
            targetWidth,
            targetHeight);
    }

    HRESULT Core10SharedFrameRing::CreateResourcesLocked(
        ID3D11Device* captureDevice,
        ID3D11DeviceContext* captureContext,
        ID3D11Device* encodeDevice,
        ID3D11DeviceContext* encodeContext,
        const D3D11_TEXTURE2D_DESC& sourceDesc,
        std::uint32_t targetWidth,
        std::uint32_t targetHeight)
    {
        auto failInit = [this](InitStage stage, HRESULT hr) -> HRESULT
        {
            SetInitDiagnostic(stage, hr);
            return hr;
        };

        ComPtr<ID3D11Device5> captureDevice5;
        ComPtr<ID3D11Device5> encodeDevice5;
        ComPtr<ID3D11DeviceContext4> captureContext4;
        ComPtr<ID3D11DeviceContext4> encodeContext4;

        SetInitDiagnostic(InitStage::QueryCaptureDevice5, S_FALSE);
        HRESULT hr = captureDevice->QueryInterface(
            IID_PPV_ARGS(captureDevice5.GetAddressOf()));
        if (FAILED(hr) || !captureDevice5)
            return failInit(
                InitStage::QueryCaptureDevice5,
                FAILED(hr) ? hr : E_NOINTERFACE);

        SetInitDiagnostic(InitStage::QueryEncodeDevice5, S_FALSE);
        hr = encodeDevice->QueryInterface(
            IID_PPV_ARGS(encodeDevice5.GetAddressOf()));
        if (FAILED(hr) || !encodeDevice5)
            return failInit(
                InitStage::QueryEncodeDevice5,
                FAILED(hr) ? hr : E_NOINTERFACE);

        SetInitDiagnostic(InitStage::QueryCaptureContext4, S_FALSE);
        hr = captureContext->QueryInterface(
            IID_PPV_ARGS(captureContext4.GetAddressOf()));
        if (FAILED(hr) || !captureContext4)
            return failInit(
                InitStage::QueryCaptureContext4,
                FAILED(hr) ? hr : E_NOINTERFACE);

        SetInitDiagnostic(InitStage::QueryEncodeContext4, S_FALSE);
        hr = encodeContext->QueryInterface(
            IID_PPV_ARGS(encodeContext4.GetAddressOf()));
        if (FAILED(hr) || !encodeContext4)
            return failInit(
                InitStage::QueryEncodeContext4,
                FAILED(hr) ? hr : E_NOINTERFACE);

        ComPtr<ID3D11Fence> captureFenceCapture;
        ComPtr<ID3D11Fence> captureFenceEncode;
        ComPtr<ID3D11Fence> releaseFenceEncode;
        ComPtr<ID3D11Fence> releaseFenceCapture;

        SetInitDiagnostic(InitStage::CreateCaptureFence, S_FALSE);
        hr = captureDevice5->CreateFence(
            0,
            D3D11_FENCE_FLAG_SHARED,
            IID_PPV_ARGS(captureFenceCapture.GetAddressOf()));
        if (FAILED(hr))
            return failInit(InitStage::CreateCaptureFence, hr);

        UniqueHandle captureFenceHandle{};
        SetInitDiagnostic(InitStage::ShareCaptureFence, S_FALSE);
        hr = captureFenceCapture->CreateSharedHandle(
            nullptr,
            GENERIC_ALL,
            nullptr,
            &captureFenceHandle.Value);
        if (FAILED(hr) || !captureFenceHandle.Value)
            return failInit(
                InitStage::ShareCaptureFence,
                FAILED(hr) ? hr : E_FAIL);

        SetInitDiagnostic(InitStage::OpenCaptureFenceOnEncode, S_FALSE);
        hr = encodeDevice5->OpenSharedFence(
            captureFenceHandle.Value,
            IID_PPV_ARGS(captureFenceEncode.GetAddressOf()));
        if (FAILED(hr))
            return failInit(InitStage::OpenCaptureFenceOnEncode, hr);

        SetInitDiagnostic(InitStage::CreateReleaseFence, S_FALSE);
        hr = encodeDevice5->CreateFence(
            0,
            D3D11_FENCE_FLAG_SHARED,
            IID_PPV_ARGS(releaseFenceEncode.GetAddressOf()));
        if (FAILED(hr))
            return failInit(InitStage::CreateReleaseFence, hr);

        UniqueHandle releaseFenceHandle{};
        SetInitDiagnostic(InitStage::ShareReleaseFence, S_FALSE);
        hr = releaseFenceEncode->CreateSharedHandle(
            nullptr,
            GENERIC_ALL,
            nullptr,
            &releaseFenceHandle.Value);
        if (FAILED(hr) || !releaseFenceHandle.Value)
            return failInit(
                InitStage::ShareReleaseFence,
                FAILED(hr) ? hr : E_FAIL);

        SetInitDiagnostic(InitStage::OpenReleaseFenceOnCapture, S_FALSE);
        hr = captureDevice5->OpenSharedFence(
            releaseFenceHandle.Value,
            IID_PPV_ARGS(releaseFenceCapture.GetAddressOf()));
        if (FAILED(hr))
            return failInit(InitStage::OpenReleaseFenceOnCapture, hr);

        std::array<Slot, SlotCount> fresh{};

        D3D11_TEXTURE2D_DESC desc = sourceDesc;
        desc.Width = targetWidth;
        desc.Height = targetHeight;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.SampleDesc.Count = 1;
        desc.SampleDesc.Quality = 0;
        desc.Usage = D3D11_USAGE_DEFAULT;
        desc.CPUAccessFlags = 0;
        desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;

        // The texture and the fence are two separate sharing mechanisms.
        // We only need to share this Texture2D between two D3D11 devices in
        // the same process/adapter, so use the classic shared-resource handle.
        // Fence objects themselves continue to use NT handles. This avoids the
        // SHARED_NTHANDLE + SHARED_KEYEDMUTEX contract of
        // IDXGIResource1::CreateSharedHandle while keeping keyed mutexes out of
        // the hot path entirely.
        desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED;

        for (auto& slot : fresh)
        {
            SetInitDiagnostic(InitStage::CreateSharedTexture, S_FALSE);
            hr = captureDevice->CreateTexture2D(
                &desc,
                nullptr,
                slot.CaptureTexture.GetAddressOf());
            if (FAILED(hr))
                return failInit(InitStage::CreateSharedTexture, hr);

            hr = captureDevice->CreateRenderTargetView(
                slot.CaptureTexture.Get(),
                nullptr,
                slot.CaptureRtv.GetAddressOf());
            if (FAILED(hr))
                return failInit(InitStage::CreateSharedTexture, hr);

            ComPtr<IDXGIResource> sharedResource;
            hr = slot.CaptureTexture->QueryInterface(
                IID_PPV_ARGS(sharedResource.GetAddressOf()));
            if (FAILED(hr) || !sharedResource)
                return failInit(
                    InitStage::GetSharedTextureHandle,
                    FAILED(hr) ? hr : E_NOINTERFACE);

            HANDLE sharedTextureHandle = nullptr;
            SetInitDiagnostic(InitStage::GetSharedTextureHandle, S_FALSE);
            hr = sharedResource->GetSharedHandle(&sharedTextureHandle);
            if (FAILED(hr) || !sharedTextureHandle)
                return failInit(
                    InitStage::GetSharedTextureHandle,
                    FAILED(hr) ? hr : E_FAIL);

            SetInitDiagnostic(InitStage::OpenSharedTextureOnEncode, S_FALSE);
            hr = encodeDevice->OpenSharedResource(
                sharedTextureHandle,
                __uuidof(ID3D11Texture2D),
                reinterpret_cast<void**>(slot.EncodeTexture.GetAddressOf()));
            if (FAILED(hr))
                return failInit(InitStage::OpenSharedTextureOnEncode, hr);
        }

        m_slots = std::move(fresh);
        m_captureContext4 = std::move(captureContext4);
        m_encodeContext4 = std::move(encodeContext4);
        m_captureFenceCapture = std::move(captureFenceCapture);
        m_captureFenceEncode = std::move(captureFenceEncode);
        m_releaseFenceEncode = std::move(releaseFenceEncode);
        m_releaseFenceCapture = std::move(releaseFenceCapture);
        m_width = targetWidth;
        m_height = targetHeight;
        m_sourceWidth = sourceDesc.Width;
        m_sourceHeight = sourceDesc.Height;
        m_format = sourceDesc.Format;

        if (targetWidth != sourceDesc.Width || targetHeight != sourceDesc.Height)
        {
            hr = EnsureScalePipelineLocked(captureDevice);
            if (FAILED(hr))
                return failInit(InitStage::CreateSharedTexture, hr);
        }

        SetInitDiagnostic(InitStage::Complete, S_OK);
        return S_OK;
    }

    HRESULT Core10SharedFrameRing::EnsureScalePipelineLocked(
        ID3D11Device* captureDevice)
    {
        if (!captureDevice)
            return E_INVALIDARG;
        if (m_scaleVs && m_scalePs && m_scaleSampler)
            return S_OK;

        static constexpr const char* vertexShaderSource = R"(
struct VSOut { float4 Position : SV_POSITION; float2 UV : TEXCOORD0; };
VSOut main(uint id : SV_VertexID)
{
    VSOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.UV = uv;
    o.Position = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return o;
})";

        static constexpr const char* pixelShaderSource = R"(
Texture2D SourceTexture : register(t0);
SamplerState LinearSampler : register(s0);
float4 main(float4 position : SV_POSITION, float2 uv : TEXCOORD0) : SV_TARGET
{
    return SourceTexture.Sample(LinearSampler, uv);
})";

        ComPtr<ID3DBlob> vsBlob;
        ComPtr<ID3DBlob> psBlob;
        ComPtr<ID3DBlob> errors;
        HRESULT hr = D3DCompile(
            vertexShaderSource,
            std::strlen(vertexShaderSource),
            "MultiverseRingScaleVS",
            nullptr,
            nullptr,
            "main",
            "vs_5_0",
            D3DCOMPILE_OPTIMIZATION_LEVEL3,
            0,
            vsBlob.GetAddressOf(),
            errors.GetAddressOf());
        if (FAILED(hr))
            return hr;

        errors.Reset();
        hr = D3DCompile(
            pixelShaderSource,
            std::strlen(pixelShaderSource),
            "MultiverseRingScalePS",
            nullptr,
            nullptr,
            "main",
            "ps_5_0",
            D3DCOMPILE_OPTIMIZATION_LEVEL3,
            0,
            psBlob.GetAddressOf(),
            errors.GetAddressOf());
        if (FAILED(hr))
            return hr;

        hr = captureDevice->CreateVertexShader(
            vsBlob->GetBufferPointer(),
            vsBlob->GetBufferSize(),
            nullptr,
            m_scaleVs.GetAddressOf());
        if (FAILED(hr))
            return hr;

        hr = captureDevice->CreatePixelShader(
            psBlob->GetBufferPointer(),
            psBlob->GetBufferSize(),
            nullptr,
            m_scalePs.GetAddressOf());
        if (FAILED(hr))
            return hr;

        D3D11_SAMPLER_DESC sampler{};
        sampler.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        sampler.AddressU = D3D11_TEXTURE_ADDRESS_CLAMP;
        sampler.AddressV = D3D11_TEXTURE_ADDRESS_CLAMP;
        sampler.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        sampler.MinLOD = 0.0f;
        sampler.MaxLOD = D3D11_FLOAT32_MAX;
        return captureDevice->CreateSamplerState(
            &sampler,
            m_scaleSampler.GetAddressOf());
    }

    HRESULT Core10SharedFrameRing::ScaleIntoSlot(
        ID3D11DeviceContext* captureContext,
        ID3D11Texture2D* source,
        ID3D11RenderTargetView* targetRtv)
    {
        if (!captureContext || !source || !targetRtv ||
            !m_scaleVs || !m_scalePs || !m_scaleSampler)
        {
            return E_INVALIDARG;
        }

        ComPtr<ID3D11Device> sourceDevice;
        source->GetDevice(sourceDevice.GetAddressOf());
        if (!sourceDevice)
            return E_NOINTERFACE;

        ComPtr<ID3D11ShaderResourceView> sourceSrv;
        HRESULT hr = sourceDevice->CreateShaderResourceView(
            source,
            nullptr,
            sourceSrv.GetAddressOf());
        if (FAILED(hr))
        {
            // Some WGC driver paths expose a texture without SRV bind flags.
            // Keep a compatibility fallback that performs one capture-device
            // CopyResource into an SRV-capable texture; the normal path above
            // remains zero-copy before the scale draw.
            D3D11_TEXTURE2D_DESC sourceDesc{};
            source->GetDesc(&sourceDesc);
            if (!m_scaleFallbackTexture || !m_scaleFallbackSrv ||
                m_scaleFallbackWidth != sourceDesc.Width ||
                m_scaleFallbackHeight != sourceDesc.Height)
            {
                m_scaleFallbackSrv.Reset();
                m_scaleFallbackTexture.Reset();
                D3D11_TEXTURE2D_DESC fallbackDesc = sourceDesc;
                fallbackDesc.MipLevels = 1;
                fallbackDesc.ArraySize = 1;
                fallbackDesc.SampleDesc.Count = 1;
                fallbackDesc.SampleDesc.Quality = 0;
                fallbackDesc.Usage = D3D11_USAGE_DEFAULT;
                fallbackDesc.CPUAccessFlags = 0;
                fallbackDesc.MiscFlags = 0;
                fallbackDesc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
                hr = sourceDevice->CreateTexture2D(
                    &fallbackDesc,
                    nullptr,
                    m_scaleFallbackTexture.GetAddressOf());
                if (FAILED(hr))
                    return hr;
                hr = sourceDevice->CreateShaderResourceView(
                    m_scaleFallbackTexture.Get(),
                    nullptr,
                    m_scaleFallbackSrv.GetAddressOf());
                if (FAILED(hr))
                    return hr;
                m_scaleFallbackWidth = sourceDesc.Width;
                m_scaleFallbackHeight = sourceDesc.Height;
            }
            captureContext->CopyResource(m_scaleFallbackTexture.Get(), source);
            sourceSrv = m_scaleFallbackSrv;
        }

        D3D11_VIEWPORT viewport{};
        viewport.Width = static_cast<float>(m_width);
        viewport.Height = static_cast<float>(m_height);
        viewport.MinDepth = 0.0f;
        viewport.MaxDepth = 1.0f;

        ID3D11RenderTargetView* rtv = targetRtv;
        ID3D11ShaderResourceView* srv = sourceSrv.Get();
        ID3D11SamplerState* sampler = m_scaleSampler.Get();
        captureContext->OMSetRenderTargets(1, &rtv, nullptr);
        captureContext->RSSetViewports(1, &viewport);
        captureContext->IASetInputLayout(nullptr);
        captureContext->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        captureContext->VSSetShader(m_scaleVs.Get(), nullptr, 0);
        captureContext->PSSetShader(m_scalePs.Get(), nullptr, 0);
        captureContext->PSSetShaderResources(0, 1, &srv);
        captureContext->PSSetSamplers(0, 1, &sampler);
        captureContext->Draw(3, 0);

        ID3D11ShaderResourceView* nullSrv = nullptr;
        captureContext->PSSetShaderResources(0, 1, &nullSrv);
        captureContext->OMSetRenderTargets(0, nullptr, nullptr);
        return S_OK;
    }

    HRESULT Core10SharedFrameRing::Publish(
        ID3D11DeviceContext* captureContext,
        ID3D11Texture2D* source,
        std::uint64_t frameId,
        std::chrono::steady_clock::time_point timestamp,
        bool* replaced)
    {
        if (replaced)
            *replaced = false;
        if (!captureContext || !source)
            return E_INVALIDARG;

        DiagnosticScope diagnosticScope{ this };
        SetDiagnosticStage(DiagnosticStage::SelectSlot);

        std::size_t selected = SlotCount;
        bool replacedReady = false;
        ComPtr<ID3D11Texture2D> captureTexture;
        ComPtr<ID3D11RenderTargetView> captureRtv;

        {
            std::lock_guard<std::mutex> lock(m_mutex);
            SetDiagnosticStage(DiagnosticStage::RefreshReleaseFence);
            RefreshReleasedSlotsLocked();
            SetDiagnosticStage(DiagnosticStage::SelectSlot);

            // Prefer a genuinely free slot.
            for (std::size_t i = 0; i < SlotCount; ++i)
            {
                if (IsSlotReusableLocked(m_slots[i]))
                {
                    selected = i;
                    break;
                }
            }

            // Latest-frame-wins: a Ready slot has never been reserved by the
            // encode worker, so it may be replaced without cross-device
            // synchronization. Choose the oldest Ready frame to preserve the
            // newest candidates for the consumer.
            if (selected == SlotCount)
            {
                std::uint64_t oldestFrameId =
                    std::numeric_limits<std::uint64_t>::max();
                for (std::size_t i = 0; i < SlotCount; ++i)
                {
                    const auto& slot = m_slots[i];
                    if (slot.Ready &&
                        !slot.Encoding &&
                        !slot.ProducerOwned &&
                        slot.ReleaseFenceValue == 0 &&
                        slot.FrameId < oldestFrameId)
                    {
                        oldestFrameId = slot.FrameId;
                        selected = i;
                    }
                }

                replacedReady = selected < SlotCount;
            }

            if (selected == SlotCount)
            {
                ++m_publishNoReusableSlots;
                return HRESULT_FROM_WIN32(ERROR_BUSY);
            }

            SetDiagnosticStage(DiagnosticStage::ReserveSlot);
            auto& slot = m_slots[selected];
            slot.ProducerOwned = true;
            slot.Ready = false;
            captureTexture = slot.CaptureTexture;
            captureRtv = slot.CaptureRtv;
        }

        if (!captureTexture || !m_captureContext4 || !m_captureFenceCapture)
        {
            std::lock_guard<std::mutex> lock(m_mutex);
            m_slots[selected].ProducerOwned = false;
            m_cv.notify_all();
            return E_NOINTERFACE;
        }

        SetDiagnosticStage(DiagnosticStage::CopyResource);
        D3D11_TEXTURE2D_DESC sourceDesc{};
        source->GetDesc(&sourceDesc);
        HRESULT copyHr = S_OK;
        if (sourceDesc.Width == m_width && sourceDesc.Height == m_height)
        {
            captureContext->CopyResource(captureTexture.Get(), source);
        }
        else
        {
            copyHr = ScaleIntoSlot(captureContext, source, captureRtv.Get());
        }

        if (FAILED(copyHr))
        {
            std::lock_guard<std::mutex> lock(m_mutex);
            auto& slot = m_slots[selected];
            slot.ProducerOwned = false;
            slot.Ready = false;
            slot.Encoding = false;
            slot.ReleaseFenceValue = 0;
            m_cv.notify_all();
            return copyHr;
        }

        SetDiagnosticStage(DiagnosticStage::SignalCaptureFence);
        const HRESULT signalHr =
            m_captureContext4->Signal(m_captureFenceCapture.Get(), frameId);

        SetDiagnosticStage(DiagnosticStage::FlushCapture);
        captureContext->Flush();

        if (FAILED(signalHr))
        {
            std::lock_guard<std::mutex> lock(m_mutex);
            auto& slot = m_slots[selected];
            slot.ProducerOwned = false;
            slot.Ready = false;
            slot.Encoding = false;
            slot.ReleaseFenceValue = 0;
            ++m_captureSyncTimeouts;
            m_cv.notify_all();
            return signalHr;
        }

        SetDiagnosticStage(DiagnosticStage::Commit);
        {
            std::lock_guard<std::mutex> lock(m_mutex);
            auto& slot = m_slots[selected];
            slot.ProducerOwned = false;
            slot.FrameId = frameId;
            slot.Timestamp = timestamp;
            slot.ReleaseFenceValue = 0;
            slot.Ready = true;
            ++m_published;
            if (replacedReady)
                ++m_replaced;
        }

        if (replaced)
            *replaced = replacedReady;
        m_cv.notify_one();
        return S_OK;
    }

    bool Core10SharedFrameRing::AcquireLatest(
        Lease& lease,
        std::chrono::milliseconds waitForFrame,
        DWORD syncTimeoutMs,
        bool requireCaptureFenceReady)
    {
        lease = {};
        (void)syncTimeoutMs;

        const auto acquireStart = std::chrono::steady_clock::now();
        std::size_t selected = SlotCount;
        std::uint64_t frameId = 0;
        std::chrono::steady_clock::time_point timestamp{};
        ComPtr<ID3D11Texture2D> texture;

        {
            std::unique_lock<std::mutex> lock(m_mutex);
            ++m_acquireCalls;

            m_cv.wait_for(lock, waitForFrame, [this, requireCaptureFenceReady]()
            {
                const std::uint64_t completed =
                    (requireCaptureFenceReady && m_captureFenceEncode)
                        ? m_captureFenceEncode->GetCompletedValue()
                        : std::numeric_limits<std::uint64_t>::max();
                for (const auto& slot : m_slots)
                {
                    if (slot.Ready && !slot.Encoding && !slot.ProducerOwned &&
                        (!requireCaptureFenceReady || slot.FrameId <= completed))
                    {
                        return true;
                    }
                }
                return false;
            });

            const auto afterWait = std::chrono::steady_clock::now();
            const auto acquireWaitUs =
                std::chrono::duration_cast<std::chrono::microseconds>(
                    afterWait - acquireStart).count();
            m_lastAcquireWaitUs = std::max<long long>(0, acquireWaitUs);
            m_totalAcquireWaitUs += static_cast<std::uint64_t>(m_lastAcquireWaitUs);
            ++m_acquireWaitSamples;

            const std::uint64_t completedForSelection =
                (requireCaptureFenceReady && m_captureFenceEncode)
                    ? m_captureFenceEncode->GetCompletedValue()
                    : std::numeric_limits<std::uint64_t>::max();

            for (std::size_t i = 0; i < SlotCount; ++i)
            {
                const auto& slot = m_slots[i];
                if (slot.Ready &&
                    !slot.Encoding &&
                    !slot.ProducerOwned &&
                    (!requireCaptureFenceReady || slot.FrameId <= completedForSelection) &&
                    (selected == SlotCount || slot.FrameId > frameId))
                {
                    selected = i;
                    frameId = slot.FrameId;
                }
            }

            if (selected == SlotCount)
            {
                ++m_acquireNoReadyFrames;
                if (requireCaptureFenceReady && m_captureFenceEncode)
                {
                    const std::uint64_t completed = m_captureFenceEncode->GetCompletedValue();
                    for (const auto& pending : m_slots)
                    {
                        if (pending.Ready && !pending.Encoding &&
                            !pending.ProducerOwned && pending.FrameId > completed)
                        {
                            ++m_acquireFencePending;
                            m_lastCaptureFenceLag = pending.FrameId - completed;
                            m_maxCaptureFenceLag = std::max(
                                m_maxCaptureFenceLag,
                                m_lastCaptureFenceLag);
                            break;
                        }
                    }
                }
                return false;
            }

            ++m_acquireReadySelections;

            // Latest-frame-wins is enforced at consumption time too: once
            // the newest Ready frame is reserved, older unreserved Ready
            // frames are obsolete and must never be encoded afterwards. They
            // can be returned to capture immediately because the encode GPU
            // has never referenced them. Capture-context command ordering
            // keeps a subsequent overwrite safe even if an old copy is still
            // finishing on the capture GPU.
            for (std::size_t i = 0; i < SlotCount; ++i)
            {
                if (i == selected)
                    continue;
                auto& stale = m_slots[i];
                if (stale.Ready && !stale.Encoding && !stale.ProducerOwned &&
                    (!requireCaptureFenceReady || stale.FrameId < frameId))
                {
                    stale.Ready = false;
                    ++m_replaced;
                }
            }

            auto& slot = m_slots[selected];
            slot.Ready = false;
            slot.Encoding = true;
            texture = slot.EncodeTexture;
            timestamp = slot.Timestamp;

            const auto leaseAgeUs =
                std::chrono::duration_cast<std::chrono::microseconds>(
                    afterWait - timestamp).count();
            m_lastLeaseAgeUs = std::max<long long>(0, leaseAgeUs);
            m_totalLeaseAgeUs += static_cast<std::uint64_t>(m_lastLeaseAgeUs);
            ++m_leaseAgeSamples;
        }

        if (!texture || !m_encodeContext4 || !m_captureFenceEncode)
        {
            std::lock_guard<std::mutex> lock(m_mutex);
            auto& slot = m_slots[selected];
            if (slot.Encoding && slot.FrameId == frameId)
            {
                slot.Encoding = false;
                slot.Ready = true;
            }
            ++m_encodeSyncTimeouts;
            m_cv.notify_all();
            return false;
        }

        // CORE 11.12 probe: inspect the capture fence before queuing the GPU
        // dependency. GetCompletedValue is non-blocking; the synchronization
        // behavior remains the same as CORE 11.11.
        const std::uint64_t completedCaptureFence =
            m_captureFenceEncode->GetCompletedValue();
        {
            std::lock_guard<std::mutex> lock(m_mutex);
            if (completedCaptureFence >= frameId)
            {
                ++m_acquireFenceReady;
                m_lastCaptureFenceLag = 0;
            }
            else
            {
                ++m_acquireFencePending;
                const std::uint64_t lag = frameId - completedCaptureFence;
                m_lastCaptureFenceLag = lag;
                m_maxCaptureFenceLag = std::max(m_maxCaptureFenceLag, lag);
            }
        }

        // Queue the dependency on the encode GPU. This call does not block the
        // CPU worker; future encode-context work begins only after capture has
        // signaled that this shared texture copy is complete.
        const HRESULT waitHr = requireCaptureFenceReady
            ? S_OK
            : m_encodeContext4->Wait(m_captureFenceEncode.Get(), frameId);
        if (FAILED(waitHr))
        {
            std::lock_guard<std::mutex> lock(m_mutex);
            auto& slot = m_slots[selected];
            if (slot.Encoding && slot.FrameId == frameId)
            {
                slot.Encoding = false;
                slot.Ready = true;
            }
            ++m_encodeSyncTimeouts;
            m_cv.notify_all();
            return false;
        }

        lease.SlotIndex = selected;
        lease.Texture = std::move(texture);
        lease.FrameId = frameId;
        lease.Timestamp = timestamp;
        lease.Reserved = true;
        return true;
    }

    void Core10SharedFrameRing::Release(
        Lease& lease,
        ID3D11DeviceContext* encodeContext) noexcept
    {
        if (!lease)
            return;

        const std::size_t slotIndex = lease.SlotIndex;
        const std::uint64_t frameId = lease.FrameId;

        std::uint64_t releaseFenceValue = 0;
        {
            std::lock_guard<std::mutex> lock(m_mutex);
            releaseFenceValue = ++m_releaseFenceSequence;
        }

        HRESULT signalHr = E_NOINTERFACE;
        if (m_encodeContext4 && m_releaseFenceEncode)
        {
            // Submit all reads of the shared BGRA texture before signaling the
            // release fence. Use a dedicated monotonically increasing release
            // sequence rather than FrameId so the fence protocol remains valid
            // even if latest-frame-wins skips source frame ids.
            signalHr = m_encodeContext4->Signal(
                m_releaseFenceEncode.Get(),
                releaseFenceValue);
            if (encodeContext)
                encodeContext->Flush();
        }

        {
            std::lock_guard<std::mutex> lock(m_mutex);
            if (slotIndex < SlotCount)
            {
                auto& slot = m_slots[slotIndex];
                if (slot.Encoding && slot.FrameId == frameId)
                {
                    if (SUCCEEDED(signalHr))
                    {
                        slot.Encoding = false;
                        slot.ReleaseFenceValue = releaseFenceValue;
                    }
                    else
                    {
                        // Fail closed: never let capture overwrite a texture
                        // whose encode-GPU release could not be fenced.
                        ++m_syncAbandoned;
                    }
                }
            }
        }

        lease = {};
        m_cv.notify_all();
    }

    void Core10SharedFrameRing::ReleaseAfterExternalConsumer(
        Lease& lease) noexcept
    {
        if (!lease)
            return;

        const std::size_t slotIndex = lease.SlotIndex;
        const std::uint64_t frameId = lease.FrameId;
        {
            std::lock_guard<std::mutex> lock(m_mutex);
            if (slotIndex < SlotCount)
            {
                auto& slot = m_slots[slotIndex];
                if (slot.Encoding && slot.FrameId == frameId)
                {
                    slot.Encoding = false;
                    slot.ReleaseFenceValue = 0;
                }
            }
        }

        lease = {};
        m_cv.notify_all();
    }

    void Core10SharedFrameRing::WakeAll() noexcept
    {
        m_cv.notify_all();
    }

    void Core10SharedFrameRing::Reset() noexcept
    {
        std::unique_lock<std::mutex> lock(m_mutex);
        (void)m_cv.wait_for(
            lock,
            std::chrono::milliseconds(100),
            [this]()
            {
                for (const auto& slot : m_slots)
                {
                    if (slot.Encoding || slot.ProducerOwned)
                        return false;
                }
                return true;
            });

        for (auto& slot : m_slots)
            slot = {};

        m_captureFenceCapture.Reset();
        m_captureFenceEncode.Reset();
        m_releaseFenceEncode.Reset();
        m_releaseFenceCapture.Reset();
        m_captureContext4.Reset();
        m_encodeContext4.Reset();
        m_releaseFenceSequence = 0;

        m_width = 0;
        m_height = 0;
        m_sourceWidth = 0;
        m_sourceHeight = 0;
        m_format = DXGI_FORMAT_UNKNOWN;
        m_scaleFallbackSrv.Reset();
        m_scaleFallbackTexture.Reset();
        m_scaleFallbackWidth = 0;
        m_scaleFallbackHeight = 0;
        m_scaleSampler.Reset();
        m_scalePs.Reset();
        m_scaleVs.Reset();
        m_published = 0;
        m_replaced = 0;
        m_captureSyncTimeouts = 0;
        m_encodeSyncTimeouts = 0;
        m_syncAbandoned = 0;
        m_captureWaitSamples = 0;
        m_encodeWaitSamples = 0;
        m_totalCaptureWaitUs = 0;
        m_totalEncodeWaitUs = 0;
        m_lastCaptureWaitUs = 0;
        m_lastEncodeWaitUs = 0;
        m_acquireCalls = 0;
        m_acquireNoReadyFrames = 0;
        m_acquireReadySelections = 0;
        m_acquireFenceReady = 0;
        m_acquireFencePending = 0;
        m_publishNoReusableSlots = 0;
        m_acquireWaitSamples = 0;
        m_totalAcquireWaitUs = 0;
        m_lastAcquireWaitUs = 0;
        m_leaseAgeSamples = 0;
        m_totalLeaseAgeUs = 0;
        m_lastLeaseAgeUs = 0;
        m_lastCaptureFenceLag = 0;
        m_maxCaptureFenceLag = 0;
        m_diagnosticStage.store(0, std::memory_order_release);
        m_diagnosticStageChangedAtUs.store(0, std::memory_order_relaxed);
        m_initStage.store(0, std::memory_order_release);
        m_lastInitStatus.store(static_cast<std::int32_t>(S_OK), std::memory_order_release);
    }

    std::uint32_t Core10SharedFrameRing::ReadyCount() const noexcept
    {
        std::lock_guard<std::mutex> lock(m_mutex);
        std::uint32_t ready = 0;
        for (const auto& slot : m_slots)
        {
            if (slot.Ready)
                ++ready;
        }
        return ready;
    }

    Core10SharedFrameRing::Stats Core10SharedFrameRing::Snapshot() const noexcept
    {
        std::lock_guard<std::mutex> lock(m_mutex);
        Stats stats{};
        stats.Published = m_published;
        stats.Replaced = m_replaced;
        stats.CaptureSyncTimeouts = m_captureSyncTimeouts;
        stats.EncodeSyncTimeouts = m_encodeSyncTimeouts;
        stats.SyncAbandoned = m_syncAbandoned;
        for (const auto& slot : m_slots)
        {
            if (slot.Ready)
                ++stats.ReadyFrames;
        }
        stats.LastCaptureWaitMs = static_cast<double>(m_lastCaptureWaitUs) / 1000.0;
        stats.AverageCaptureWaitMs = AverageMs(m_totalCaptureWaitUs, m_captureWaitSamples);
        stats.LastEncodeWaitMs = static_cast<double>(m_lastEncodeWaitUs) / 1000.0;
        stats.AverageEncodeWaitMs = AverageMs(m_totalEncodeWaitUs, m_encodeWaitSamples);
        stats.AcquireCalls = m_acquireCalls;
        stats.AcquireNoReadyFrames = m_acquireNoReadyFrames;
        stats.AcquireReadySelections = m_acquireReadySelections;
        stats.AcquireFenceReady = m_acquireFenceReady;
        stats.AcquireFencePending = m_acquireFencePending;
        stats.PublishNoReusableSlots = m_publishNoReusableSlots;
        stats.LastAcquireWaitMs = static_cast<double>(m_lastAcquireWaitUs) / 1000.0;
        stats.AverageAcquireWaitMs = AverageMs(m_totalAcquireWaitUs, m_acquireWaitSamples);
        stats.LastLeaseAgeMs = static_cast<double>(m_lastLeaseAgeUs) / 1000.0;
        stats.AverageLeaseAgeMs = AverageMs(m_totalLeaseAgeUs, m_leaseAgeSamples);
        stats.LastCaptureFenceLag = m_lastCaptureFenceLag;
        stats.MaxCaptureFenceLag = m_maxCaptureFenceLag;

        const std::uint64_t releaseCompleted =
            m_releaseFenceCapture ? m_releaseFenceCapture->GetCompletedValue() : 0;
        for (const auto& slot : m_slots)
        {
            if (slot.ReleaseFenceValue != 0 &&
                slot.ReleaseFenceValue > releaseCompleted)
            {
                ++stats.ReleaseFencePendingSlots;
            }
        }
        return stats;
    }
}
