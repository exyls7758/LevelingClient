#include "D3D11NvdecRenderer.h"
#include "CompositionScaleMath.h"
#include "UnlockedPresentation.h"
#include "PresenterJoin.h"

#if defined(MULTIVERSE_NVDEC)

#include <cudaD3D11.h>
#include <avrt.h>

#include <d3dcompiler.h>
#include <wrl/client.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>

using Microsoft::WRL::ComPtr;

#pragma comment(lib, "Avrt.lib")

namespace
{
    class PresenterMmcssScope final
    {
    public:
        PresenterMmcssScope()
        {
            DWORD taskIndex = 0;
            m_handle =
                AvSetMmThreadCharacteristicsW(
                    L"Playback",
                    &taskIndex);

            if (m_handle)
            {
                (void)AvSetMmThreadPriority(
                    m_handle,
                    AVRT_PRIORITY_CRITICAL);
            }
        }

        ~PresenterMmcssScope()
        {
            if (m_handle)
            {
                (void)AvRevertMmThreadCharacteristics(
                    m_handle);
            }
        }

    private:
        HANDLE m_handle = nullptr;
    };

    using Clock =
        std::chrono::steady_clock;

    std::uint64_t SteadyNowUs()
    {
        const auto now =
            Clock::now().time_since_epoch();

        const auto us =
            std::chrono::duration_cast<
                std::chrono::microseconds>(
                    now).count();

        return us > 0
            ? static_cast<std::uint64_t>(us)
            : 0;
    }

    // WinUI 3 uses a different IID from the legacy
    // Windows.UI.Xaml ISwapChainPanelNative interface.
    MIDL_INTERFACE("63aad0b8-7c24-40ff-85a8-640d944cc325")
    IMultiverseSwapChainPanelNative : public IUnknown
    {
    public:
        virtual HRESULT STDMETHODCALLTYPE SetSwapChain(
            IDXGISwapChain* swapChain) = 0;
    };

    std::int32_t HrStatus(
        HRESULT hr)
    {
        return SUCCEEDED(hr)
            ? 0
            : static_cast<std::int32_t>(hr);
    }

    std::int32_t CuStatus(
        CUresult result)
    {
        return result == CUDA_SUCCESS
            ? 0
            : -30000 -
                static_cast<std::int32_t>(
                    result);
    }

    std::wstring HrText(
        const wchar_t* stage,
        HRESULT hr)
    {
        wchar_t buffer[256]{};

        swprintf_s(
            buffer,
            L"%s falhou: HRESULT 0x%08X",
            stage,
            static_cast<unsigned int>(hr));

        return buffer;
    }

    std::wstring CuText(
        const wchar_t* stage,
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

        std::wstring message =
            std::wstring(stage) +
            L" falhou: CUDA " +
            std::to_wstring(
                static_cast<int>(result));

        auto appendUtf8 =
            [&message](
                const char* value)
            {
                if (!value)
                    return;

                int chars =
                    MultiByteToWideChar(
                        CP_UTF8,
                        0,
                        value,
                        -1,
                        nullptr,
                        0);

                if (chars <= 1)
                    return;

                std::wstring wide(
                    static_cast<size_t>(
                        chars - 1),
                    L'\0');

                MultiByteToWideChar(
                    CP_UTF8,
                    0,
                    value,
                    -1,
                    wide.data(),
                    chars);

                message +=
                    L" • " + wide;
            };

        appendUtf8(name);
        appendUtf8(text);

        return message;
    }

    const char* FullscreenVsHlsl =
        R"(
struct VSOut
{
    float4 position : SV_Position;
    float2 uv       : TEXCOORD0;
};

VSOut main(uint id : SV_VertexID)
{
    float2 positions[3] =
    {
        float2(-1.0, -1.0),
        float2(-1.0,  3.0),
        float2( 3.0, -1.0)
    };

    VSOut o;
    float2 p = positions[id];

    o.position = float4(p, 0.0, 1.0);
    o.uv = float2(
        (p.x + 1.0) * 0.5,
        1.0 - ((p.y + 1.0) * 0.5));

    return o;
}
)";

    const char* Nv12PsHlsl =
        R"(
Texture2D YPlane  : register(t0);
Texture2D UVPlane : register(t1);
SamplerState LinearClamp : register(s0);

float4 main(float4 position : SV_Position, float2 uv : TEXCOORD0)
    : SV_Target
{
    float y = YPlane.Sample(LinearClamp, uv).r;
    float2 c = UVPlane.Sample(LinearClamp, uv).rg;

    // BT.709 limited-range NV12 -> RGB.
    float Y = max(
        0.0,
        (y - (16.0 / 255.0)) *
        1.16438356);

    float U =
        c.x - 0.5;

    float V =
        c.y - 0.5;

    float3 rgb;

    rgb.r =
        Y + 1.79274107 * V;

    rgb.g =
        Y -
        0.21324861 * U -
        0.53290933 * V;

    rgb.b =
        Y + 2.11240179 * U;

    return float4(
        saturate(rgb),
        1.0);
}
)";
}

struct D3D11NvdecRenderer::Impl
{
    struct PlaneSlot
    {
        ComPtr<ID3D11Texture2D> Y;
        ComPtr<ID3D11Texture2D> UV;

        ComPtr<ID3D11ShaderResourceView> YSrv;
        ComPtr<ID3D11ShaderResourceView> UVSrv;

        CUgraphicsResource CudaY = nullptr;
        CUgraphicsResource CudaUV = nullptr;

        // Timestamp taken only after the NV12 copy completed and the slot was
        // committed as pending. Presenter telemetry uses it to separate
        // queue/scheduler delay from DXGI/DWM delay.
        std::uint64_t ReadyAtUs = 0;
    };

    // StateMutex protects swap-chain/device lifecycle and the immediate D3D11
    // context. The presenter NEVER waits for frame latency while holding it.
    // This is the key Core 09 change: NVDEC submission can continue copying the
    // newest frame while the display/compositor is busy presenting the prior one.
    mutable std::mutex StateMutex;

    std::mutex QueueMutex;
    std::condition_variable QueueCv;
    std::thread PresenterThread;
    std::atomic<bool> PresenterStop{false};
    std::atomic<bool> PresenterRunning{false};

    int PendingSlot = -1;
    int PresentingSlot = -1;
    int CopyingSlot = -1;
    std::uint32_t NextSubmitSlot = 0;

    HANDLE FrameLatencyWaitable = nullptr;

    CUdevice CudaDevice = 0;

    ComPtr<IDXGIAdapter1> Adapter;
    ComPtr<IDXGIFactory2> Factory;

    ComPtr<ID3D11Device> Device;
    ComPtr<ID3D11DeviceContext> Context;

    ComPtr<ID3D11VertexShader> VertexShader;
    ComPtr<ID3D11PixelShader> PixelShader;
    ComPtr<ID3D11SamplerState> Sampler;

    ComPtr<IMultiverseSwapChainPanelNative>
        PanelNative;

    ComPtr<IDXGISwapChain1>
        SwapChain;

    ComPtr<ID3D11RenderTargetView>
        BackBufferRtv;

    // Three plane slots are enough for remote gaming:
    // one can be on screen, one can be copied by CUDA and one can hold the
    // newest pending frame. Pending frames are latest-wins, never FIFO.
    std::array<PlaneSlot, 3>
        Slots{};

    std::uint32_t SourceWidth = 0;
    std::uint32_t SourceHeight = 0;

    std::uint32_t TargetWidth = 0;
    std::uint32_t TargetHeight = 0;
    float CompositionScaleX = 1.0f;
    float CompositionScaleY = 1.0f;

    // GetStats must not wait behind a stalled Present() that owns StateMutex.
    // These are only telemetry shadows for the rarely-changing dimensions.
    mutable std::atomic<std::uint32_t> StatsSourceWidth{0};
    mutable std::atomic<std::uint32_t> StatsSourceHeight{0};
    mutable std::atomic<std::uint32_t> StatsTargetWidth{0};
    mutable std::atomic<std::uint32_t> StatsTargetHeight{0};

    std::atomic<bool> Attached{false};
    std::atomic<std::int32_t> LastStatus{0};

    std::atomic<std::uint64_t> SubmittedFrames{0};
    std::atomic<std::uint64_t> PresentedFrames{0};
    std::atomic<std::uint64_t> DroppedPresents{0};
    std::atomic<std::uint64_t> ReplacedPendingFrames{0};
    std::atomic<std::uint64_t> PresentWaitTimeouts{0};

    // CORE 11.17f WaitablePresenter. Keep the existing probe counters; the
    // former "bypass" counter now records frames skipped because display
    // capacity did not become available inside the bounded wait. Frames are
    // never submitted with DXGI_PRESENT_DO_NOT_WAIT in this path.
    std::atomic<std::uint64_t> PresenterDequeuedFrames{0};
    std::atomic<std::uint64_t> FrameLatencySignals{0};
    std::atomic<std::uint64_t> FrameLatencyBypasses{0};
    std::atomic<std::uint64_t> FrameLatencyWaitFailures{0};
    std::atomic<std::uint64_t> PresentAttempts{0};
    std::atomic<std::uint64_t> PresentStillDrawing{0};
    std::atomic<std::uint64_t> PresentFailures{0};

    // NOTE (fix): the 20 ms wait above is sound on its own, but as written
    // a timeout only ever led to skipping Present and looping back to wait
    // again — nothing ever escalated. If FrameLatencyWaitable stops being
    // signaled for a stretch (a background/non-focused composed swap chain
    // can go quiet under DWM for reasons outside this process's control),
    // every single cycle takes the timeout branch forever and the presenter
    // produces zero output indefinitely — matching "D3D11 present FPS goes
    // to 0". This counter tracks consecutive timeouts so a bounded number of
    // them can force one blocking present as a safety valve (see
    // kMaxConsecutiveFrameLatencyTimeouts below) instead of waiting forever.
    // This is not a return to presenting with DXGI_PRESENT_DO_NOT_WAIT every
    // frame (that tradeoff was already tried and moved away from); it only
    // engages after sustained, repeated failure to acquire the wait.
    std::atomic<std::uint32_t> ConsecutiveFrameLatencyTimeouts{0};
    std::atomic<std::uint64_t> FrameLatencyForcedPresents{0};

    std::atomic<std::uint32_t> ForegroundProcessId{0};
    std::atomic<std::int32_t> ForegroundIsCurrentProcess{0};

    // 0=stopped, 1=queue, 2=DXGI waitable, 3=StateMutex,
    // 4=draw, 5=IDXGISwapChain::Present.
    std::atomic<std::uint32_t> PresenterStage{0};
    std::atomic<std::int32_t> PresentInFlight{0};

    std::atomic<std::uint64_t> LastPresenterActivityUs{0};

    std::atomic<std::uint64_t> LastQueueToPresenterUs{0};
    std::atomic<std::uint64_t> TotalQueueToPresenterUs{0};
    std::atomic<std::uint64_t> QueueToPresenterSamples{0};

    std::atomic<std::uint64_t> LastStateLockWaitUs{0};
    std::atomic<std::uint64_t> TotalStateLockWaitUs{0};
    std::atomic<std::uint64_t> StateLockWaitSamples{0};

    std::atomic<std::uint64_t> LastPresentCallUs{0};
    std::atomic<std::uint64_t> TotalPresentCallUs{0};
    std::atomic<std::uint64_t> PresentCallSamples{0};

    std::atomic<std::uint64_t> LastFrameAgeAtPresentUs{0};
    std::atomic<std::uint64_t> TotalFrameAgeAtPresentUs{0};
    std::atomic<std::uint64_t> FrameAgeAtPresentSamples{0};

    std::atomic<std::uint64_t> LastCopyUs{0};
    std::atomic<std::uint64_t> TotalCopyUs{0};
    std::atomic<std::uint64_t> CopySamples{0};

    std::atomic<std::uint64_t> LastPresentUs{0};
    std::atomic<std::uint64_t> TotalPresentUs{0};
    std::atomic<std::uint64_t> PresentSamples{0};

    std::atomic<std::uint64_t> LastPresentWaitUs{0};
    std::atomic<std::uint64_t> TotalPresentWaitUs{0};
    std::atomic<std::uint64_t> PresentWaitSamples{0};

    mutable std::mutex MessageMutex;
    std::wstring LastMessage =
        L"D3D11 renderer não iniciado.";

    void SetMessage(
        const std::wstring& message)
    {
        std::lock_guard lock(
            MessageMutex);

        LastMessage =
            message;
    }

    std::wstring Message() const
    {
        std::lock_guard lock(
            MessageMutex);

        return LastMessage;
    }

    void DestroyPlaneResources()
    {
        for (auto& slot :
             Slots)
        {
            if (slot.CudaY)
            {
                cuGraphicsUnregisterResource(
                    slot.CudaY);

                slot.CudaY =
                    nullptr;
            }

            if (slot.CudaUV)
            {
                cuGraphicsUnregisterResource(
                    slot.CudaUV);

                slot.CudaUV =
                    nullptr;
            }

            slot.YSrv.Reset();
            slot.UVSrv.Reset();
            slot.Y.Reset();
            slot.UV.Reset();
            slot.ReadyAtUs = 0;
        }

        SourceWidth = 0;
        SourceHeight = 0;
        NextSubmitSlot = 0;
        {
            std::lock_guard queueLock(QueueMutex);
            PendingSlot = -1;
            PresentingSlot = -1;
            CopyingSlot = -1;
        }
    }

    HRESULT CompileShaders()
    {
        if (VertexShader &&
            PixelShader &&
            Sampler)
        {
            return S_OK;
        }

        UINT flags =
            D3DCOMPILE_ENABLE_STRICTNESS;

#if defined(_DEBUG)
        flags |=
            D3DCOMPILE_DEBUG |
            D3DCOMPILE_SKIP_OPTIMIZATION;
#else
        flags |=
            D3DCOMPILE_OPTIMIZATION_LEVEL3;
#endif

        ComPtr<ID3DBlob> vs;
        ComPtr<ID3DBlob> ps;
        ComPtr<ID3DBlob> errors;

        HRESULT hr =
            D3DCompile(
                FullscreenVsHlsl,
                std::strlen(
                    FullscreenVsHlsl),
                "MultiverseFullscreenVS",
                nullptr,
                nullptr,
                "main",
                "vs_5_0",
                flags,
                0,
                &vs,
                &errors);

        if (FAILED(hr))
        {
            SetMessage(
                HrText(
                    L"D3DCompile VS",
                    hr));

            return hr;
        }

        errors.Reset();

        hr =
            D3DCompile(
                Nv12PsHlsl,
                std::strlen(
                    Nv12PsHlsl),
                "MultiverseNv12PS",
                nullptr,
                nullptr,
                "main",
                "ps_5_0",
                flags,
                0,
                &ps,
                &errors);

        if (FAILED(hr))
        {
            SetMessage(
                HrText(
                    L"D3DCompile PS",
                    hr));

            return hr;
        }

        hr =
            Device->CreateVertexShader(
                vs->GetBufferPointer(),
                vs->GetBufferSize(),
                nullptr,
                &VertexShader);

        if (FAILED(hr))
            return hr;

        hr =
            Device->CreatePixelShader(
                ps->GetBufferPointer(),
                ps->GetBufferSize(),
                nullptr,
                &PixelShader);

        if (FAILED(hr))
            return hr;

        D3D11_SAMPLER_DESC sampler{};

        sampler.Filter =
            D3D11_FILTER_MIN_MAG_LINEAR_MIP_POINT;

        sampler.AddressU =
            D3D11_TEXTURE_ADDRESS_CLAMP;

        sampler.AddressV =
            D3D11_TEXTURE_ADDRESS_CLAMP;

        sampler.AddressW =
            D3D11_TEXTURE_ADDRESS_CLAMP;

        sampler.MaxLOD =
            D3D11_FLOAT32_MAX;

        return Device->CreateSamplerState(
            &sampler,
            &Sampler);
    }

    HRESULT CreateDeviceForCuda(
        CUdevice cudaDevice)
    {
        if (Device)
            return S_OK;

        ComPtr<IDXGIFactory1> factory1;

        HRESULT hr =
            CreateDXGIFactory1(
                IID_PPV_ARGS(
                    &factory1));

        if (FAILED(hr))
            return hr;

        for (UINT index = 0;
             ;
             ++index)
        {
            ComPtr<IDXGIAdapter1> adapter;

            hr =
                factory1->EnumAdapters1(
                    index,
                    &adapter);

            if (hr ==
                DXGI_ERROR_NOT_FOUND)
            {
                break;
            }

            if (FAILED(hr))
                continue;

            CUdevice adapterCuda = 0;

            CUresult cu =
                cuD3D11GetDevice(
                    &adapterCuda,
                    adapter.Get());

            if (cu != CUDA_SUCCESS ||
                adapterCuda != cudaDevice)
            {
                continue;
            }

            Adapter =
                adapter;

            break;
        }

        if (!Adapter)
        {
            SetMessage(
                L"Não encontrei o adaptador D3D11 correspondente à GPU CUDA/NVDEC.");

            return DXGI_ERROR_NOT_FOUND;
        }

        const D3D_FEATURE_LEVEL levels[] =
        {
            D3D_FEATURE_LEVEL_11_1,
            D3D_FEATURE_LEVEL_11_0
        };

        D3D_FEATURE_LEVEL level{};

        UINT createFlags =
            D3D11_CREATE_DEVICE_BGRA_SUPPORT;

        hr =
            D3D11CreateDevice(
                Adapter.Get(),
                D3D_DRIVER_TYPE_UNKNOWN,
                nullptr,
                createFlags,
                levels,
                ARRAYSIZE(levels),
                D3D11_SDK_VERSION,
                &Device,
                &level,
                &Context);

        if (FAILED(hr))
        {
            SetMessage(
                HrText(
                    L"D3D11CreateDevice",
                    hr));

            return hr;
        }

        // Presenter also gets relative GPU priority. This matters especially
        // in loopback stress tests where the same GPU renders the game AND
        // decodes/presents the received stream.
        {
            ComPtr<IDXGIDevice> dxgiDevice;
            if (SUCCEEDED(
                    Device->QueryInterface(
                        IID_PPV_ARGS(
                            dxgiDevice.GetAddressOf()))))
            {
                (void)dxgiDevice->SetGPUThreadPriority(4);
            }
        }

        // Core 09 uses a frame-latency waitable swap chain. Latency is
        // configured on IDXGISwapChain2, not globally on IDXGIDevice1.

        hr =
            Adapter->GetParent(
                IID_PPV_ARGS(
                    &Factory));

        if (FAILED(hr))
            return hr;

        return CompileShaders();
    }

    HRESULT ApplyCompositionScale()
    {
        if (!SwapChain)
            return S_OK;
        ComPtr<IDXGISwapChain2> swapChain2;
        HRESULT hr = SwapChain.As(&swapChain2);
        if (FAILED(hr))
            return hr;
        const auto matrix = CompositionScaleMath::MakeMatrix(
            CompositionScaleX, CompositionScaleY);
        const DXGI_MATRIX_3X2_F dxgiMatrix{
            matrix.m11, matrix.m12, matrix.m21,
            matrix.m22, matrix.m31, matrix.m32};
        return swapChain2->SetMatrixTransform(&dxgiMatrix);
    }

    HRESULT CreateBackBuffer()
    {
        BackBufferRtv.Reset();

        if (!SwapChain)
            return E_POINTER;

        ComPtr<ID3D11Texture2D>
            backBuffer;

        HRESULT hr =
            SwapChain->GetBuffer(
                0,
                IID_PPV_ARGS(
                    &backBuffer));

        if (FAILED(hr))
            return hr;

        return Device->
            CreateRenderTargetView(
                backBuffer.Get(),
                nullptr,
                &BackBufferRtv);
    }

    HRESULT CreateSwapChain(
        IUnknown* panelUnknown,
        std::uint32_t width,
        std::uint32_t height)
    {
        if (!panelUnknown ||
            !Factory ||
            !Device)
        {
            return E_POINTER;
        }

        width =
            std::max<std::uint32_t>(
                width,
                2u);

        height =
            std::max<std::uint32_t>(
                height,
                2u);

        ComPtr<
            IMultiverseSwapChainPanelNative>
            panelNative;

        HRESULT hr =
            panelUnknown->QueryInterface(
                __uuidof(
                    IMultiverseSwapChainPanelNative),
                reinterpret_cast<void**>(
                    panelNative.GetAddressOf()));

        if (FAILED(hr))
        {
            SetMessage(
                HrText(
                    L"QueryInterface(ISwapChainPanelNative WinUI3)",
                    hr));

            return hr;
        }

        DXGI_SWAP_CHAIN_DESC1 desc{};

        desc.Width =
            width;

        desc.Height =
            height;

        desc.Format =
            DXGI_FORMAT_B8G8R8A8_UNORM;

        desc.Stereo =
            FALSE;

        desc.SampleDesc.Count =
            1;

        desc.BufferUsage =
            DXGI_USAGE_RENDER_TARGET_OUTPUT;

        desc.BufferCount =
            2;

        desc.Scaling =
            DXGI_SCALING_STRETCH;

        desc.SwapEffect =
            DXGI_SWAP_EFFECT_FLIP_DISCARD;

        desc.AlphaMode =
            DXGI_ALPHA_MODE_IGNORE;

        desc.Flags =
            DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;

        ComPtr<IDXGISwapChain1>
            swapChain;

        hr =
            Factory->
                CreateSwapChainForComposition(
                    Device.Get(),
                    &desc,
                    nullptr,
                    &swapChain);

        if (FAILED(hr))
        {
            SetMessage(
                HrText(
                    L"CreateSwapChainForComposition",
                    hr));

            return hr;
        }

        ComPtr<IDXGISwapChain2>
            swapChain2;

        if (SUCCEEDED(
                swapChain.As(
                    &swapChain2)))
        {
            hr = swapChain2->
                SetMaximumFrameLatency(
                    1);

            if (FAILED(hr))
                return hr;

            FrameLatencyWaitable =
                swapChain2->
                    GetFrameLatencyWaitableObject();

            if (!FrameLatencyWaitable)
            {
                SetMessage(
                    L"DXGI não forneceu o FrameLatencyWaitableObject.");
                return E_FAIL;
            }
        }
        else
        {
            SetMessage(
                L"IDXGISwapChain2 indisponível para presenter de baixa latência.");
            return E_NOINTERFACE;
        }

        // Must run on the XAML UI thread. The managed caller guarantees it.
        hr =
            panelNative->
                SetSwapChain(
                    swapChain.Get());

        if (FAILED(hr))
        {
            SetMessage(
                HrText(
                    L"ISwapChainPanelNative::SetSwapChain",
                    hr));

            CloseFrameLatencyHandle();
            return hr;
        }

        PanelNative =
            panelNative;

        SwapChain =
            swapChain;

        // Apply the inverse composition scale before the first Present.
        // A physical-size buffer otherwise appears enlarged on a DPI-scaled
        // SwapChainPanel and can be clipped to a tiny portion of the source.
        hr = ApplyCompositionScale();
        if (FAILED(hr))
        {
            panelNative->SetSwapChain(nullptr);
            PanelNative.Reset();
            SwapChain.Reset();
            CloseFrameLatencyHandle();
            SetMessage(HrText(L"SetMatrixTransform", hr));
            return hr;
        }

        TargetWidth =
            width;

        TargetHeight =
            height;

        hr =
            CreateBackBuffer();

        if (FAILED(hr))
        {
            panelNative->SetSwapChain(nullptr);
            PanelNative.Reset();
            SwapChain.Reset();
            CloseFrameLatencyHandle();
            return hr;
        }

        Attached.store(
            true);

        SetMessage(
            L"D3D11 SwapChainPanel conectado.");

        return S_OK;
    }

    HRESULT ResizeSwapChain(
        std::uint32_t width,
        std::uint32_t height)
    {
        if (!SwapChain)
            return S_FALSE;

        width =
            std::max<std::uint32_t>(
                width,
                2u);

        height =
            std::max<std::uint32_t>(
                height,
                2u);

        if (width == TargetWidth &&
            height == TargetHeight)
        {
            return S_OK;
        }

        // Flip-model swap chains require all indirect references to the
        // old back buffer to be released before ResizeBuffers.
        Context->OMSetRenderTargets(
            0,
            nullptr,
            nullptr);

        Context->Flush();

        BackBufferRtv.Reset();

        HRESULT hr =
            SwapChain->ResizeBuffers(
                2,
                width,
                height,
                DXGI_FORMAT_B8G8R8A8_UNORM,
                DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT);

        if (FAILED(hr))
        {
            // Resize failure must not leave the renderer without an RTV.
            // The old swap-chain buffers remain valid when ResizeBuffers
            // fails, so try to recreate the old RTV immediately.
            HRESULT restoreHr =
                CreateBackBuffer();

            SetMessage(
                HrText(
                    restoreHr == S_OK
                        ? L"IDXGISwapChain::ResizeBuffers"
                        : L"ResizeBuffers + restore back buffer",
                    FAILED(restoreHr)
                        ? restoreHr
                        : hr));

            return hr;
        }

        TargetWidth =
            width;

        TargetHeight =
            height;

        hr =
            CreateBackBuffer();

        if (SUCCEEDED(hr))
            hr = ApplyCompositionScale();

        if (FAILED(hr))
        {
            SetMessage(
                HrText(
                    L"CreateBackBuffer/SetMatrixTransform após ResizeBuffers",
                    hr));
        }

        return hr;
    }

    HRESULT CreatePlaneSlot(
        PlaneSlot& slot,
        std::uint32_t width,
        std::uint32_t height)
    {
        D3D11_TEXTURE2D_DESC yDesc{};

        yDesc.Width =
            width;

        yDesc.Height =
            height;

        yDesc.MipLevels =
            1;

        yDesc.ArraySize =
            1;

        yDesc.Format =
            DXGI_FORMAT_R8_UNORM;

        yDesc.SampleDesc.Count =
            1;

        yDesc.Usage =
            D3D11_USAGE_DEFAULT;

        yDesc.BindFlags =
            D3D11_BIND_SHADER_RESOURCE;

        HRESULT hr =
            Device->CreateTexture2D(
                &yDesc,
                nullptr,
                &slot.Y);

        if (FAILED(hr))
            return hr;

        D3D11_TEXTURE2D_DESC uvDesc =
            yDesc;

        uvDesc.Width =
            width / 2u;

        uvDesc.Height =
            height / 2u;

        uvDesc.Format =
            DXGI_FORMAT_R8G8_UNORM;

        hr =
            Device->CreateTexture2D(
                &uvDesc,
                nullptr,
                &slot.UV);

        if (FAILED(hr))
            return hr;

        hr =
            Device->CreateShaderResourceView(
                slot.Y.Get(),
                nullptr,
                &slot.YSrv);

        if (FAILED(hr))
            return hr;

        hr =
            Device->CreateShaderResourceView(
                slot.UV.Get(),
                nullptr,
                &slot.UVSrv);

        if (FAILED(hr))
            return hr;

        CUresult cu =
            cuGraphicsD3D11RegisterResource(
                &slot.CudaY,
                slot.Y.Get(),
                CU_GRAPHICS_REGISTER_FLAGS_NONE);

        if (cu != CUDA_SUCCESS)
        {
            SetMessage(
                CuText(
                    L"cuGraphicsD3D11RegisterResource(Y)",
                    cu));

            return E_FAIL;
        }

        cu =
            cuGraphicsD3D11RegisterResource(
                &slot.CudaUV,
                slot.UV.Get(),
                CU_GRAPHICS_REGISTER_FLAGS_NONE);

        if (cu != CUDA_SUCCESS)
        {
            SetMessage(
                CuText(
                    L"cuGraphicsD3D11RegisterResource(UV)",
                    cu));

            return E_FAIL;
        }

        return S_OK;
    }

    HRESULT EnsurePlaneResources(
        std::uint32_t width,
        std::uint32_t height)
    {
        width &=
            ~1u;

        height &=
            ~1u;

        if (width == 0 ||
            height == 0)
        {
            return E_INVALIDARG;
        }

        if (SourceWidth == width &&
            SourceHeight == height &&
            Slots[0].CudaY &&
            Slots[1].CudaY &&
            Slots[2].CudaY)
        {
            return S_OK;
        }

        DestroyPlaneResources();

        for (auto& slot :
             Slots)
        {
            HRESULT hr =
                CreatePlaneSlot(
                    slot,
                    width,
                    height);

            if (FAILED(hr))
            {
                DestroyPlaneResources();
                return hr;
            }
        }

        SourceWidth =
            width;

        SourceHeight =
            height;

        SetMessage(
            L"CUDA↔D3D11 interop ativo: Y R8 + UV R8G8 • 3 slots latest-frame-wins.");

        return S_OK;
    }

    std::int32_t CopyNv12ToSlot(
        PlaneSlot& slot,
        CUdeviceptr sourceNv12,
        unsigned int sourcePitch,
        std::uint32_t width,
        std::uint32_t height,
        std::uint32_t surfaceHeight)
    {
        CUgraphicsResource resources[] =
        {
            slot.CudaY,
            slot.CudaUV
        };

        CUresult cu =
            cuGraphicsMapResources(
                2,
                resources,
                0);

        if (cu != CUDA_SUCCESS)
        {
            SetMessage(
                CuText(
                    L"cuGraphicsMapResources",
                    cu));

            return CuStatus(cu);
        }

        CUarray yArray = nullptr;
        CUarray uvArray = nullptr;

        cu =
            cuGraphicsSubResourceGetMappedArray(
                &yArray,
                slot.CudaY,
                0,
                0);

        if (cu == CUDA_SUCCESS)
        {
            cu =
                cuGraphicsSubResourceGetMappedArray(
                    &uvArray,
                    slot.CudaUV,
                    0,
                    0);
        }

        if (cu != CUDA_SUCCESS)
        {
            cuGraphicsUnmapResources(
                2,
                resources,
                0);

            SetMessage(
                CuText(
                    L"cuGraphicsSubResourceGetMappedArray",
                    cu));

            return CuStatus(cu);
        }

        CUDA_MEMCPY2D yCopy{};

        yCopy.srcMemoryType =
            CU_MEMORYTYPE_DEVICE;

        yCopy.srcDevice =
            sourceNv12;

        yCopy.srcPitch =
            sourcePitch;

        yCopy.dstMemoryType =
            CU_MEMORYTYPE_ARRAY;

        yCopy.dstArray =
            yArray;

        yCopy.WidthInBytes =
            width;

        yCopy.Height =
            height;

        cu =
            cuMemcpy2D(
                &yCopy);

        if (cu == CUDA_SUCCESS)
        {
            CUDA_MEMCPY2D uvCopy{};

            uvCopy.srcMemoryType =
                CU_MEMORYTYPE_DEVICE;

            uvCopy.srcDevice =
                sourceNv12 +
                static_cast<CUdeviceptr>(
                    sourcePitch) *
                surfaceHeight;

            uvCopy.srcPitch =
                sourcePitch;

            uvCopy.dstMemoryType =
                CU_MEMORYTYPE_ARRAY;

            uvCopy.dstArray =
                uvArray;

            // R8G8 = two bytes per UV pixel. The texture is W/2 pixels,
            // therefore the row remains exactly W bytes.
            uvCopy.WidthInBytes =
                width;

            uvCopy.Height =
                height / 2u;

            cu =
                cuMemcpy2D(
                    &uvCopy);
        }

        CUresult unmap =
            cuGraphicsUnmapResources(
                2,
                resources,
                0);

        if (cu != CUDA_SUCCESS)
        {
            SetMessage(
                CuText(
                    L"cuMemcpy2D NV12→D3D11",
                    cu));

            return CuStatus(cu);
        }

        if (unmap != CUDA_SUCCESS)
        {
            SetMessage(
                CuText(
                    L"cuGraphicsUnmapResources",
                    unmap));

            return CuStatus(unmap);
        }

        return 0;
    }

    HRESULT DrawSlot(
        PlaneSlot& slot, std::unique_lock<std::mutex>& stateLock, UINT presentFlags)
    {
        if (!SwapChain ||
            !BackBufferRtv ||
            TargetWidth == 0 ||
            TargetHeight == 0)
        {
            return S_FALSE;
        }

        ID3D11RenderTargetView* rtv =
            BackBufferRtv.Get();

        Context->OMSetRenderTargets(
            1,
            &rtv,
            nullptr);

        const float black[] =
        {
            0.0f,
            0.0f,
            0.0f,
            1.0f
        };

        Context->ClearRenderTargetView(
            BackBufferRtv.Get(),
            black);

        float sourceAspect =
            SourceHeight > 0
                ? static_cast<float>(
                    SourceWidth) /
                    static_cast<float>(
                        SourceHeight)
                : 1.0f;

        float targetAspect =
            static_cast<float>(
                TargetWidth) /
            static_cast<float>(
                TargetHeight);

        D3D11_VIEWPORT viewport{};

        if (targetAspect >
            sourceAspect)
        {
            viewport.Height =
                static_cast<float>(
                    TargetHeight);

            viewport.Width =
                viewport.Height *
                sourceAspect;

            viewport.TopLeftX =
                (
                    static_cast<float>(
                        TargetWidth) -
                    viewport.Width
                ) *
                0.5f;

            viewport.TopLeftY =
                0.0f;
        }
        else
        {
            viewport.Width =
                static_cast<float>(
                    TargetWidth);

            viewport.Height =
                viewport.Width /
                sourceAspect;

            viewport.TopLeftX =
                0.0f;

            viewport.TopLeftY =
                (
                    static_cast<float>(
                        TargetHeight) -
                    viewport.Height
                ) *
                0.5f;
        }

        viewport.MinDepth =
            0.0f;

        viewport.MaxDepth =
            1.0f;

        Context->RSSetViewports(
            1,
            &viewport);

        Context->IASetInputLayout(
            nullptr);

        Context->IASetPrimitiveTopology(
            D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);

        Context->VSSetShader(
            VertexShader.Get(),
            nullptr,
            0);

        Context->PSSetShader(
            PixelShader.Get(),
            nullptr,
            0);

        ID3D11ShaderResourceView*
            srvs[] =
        {
            slot.YSrv.Get(),
            slot.UVSrv.Get()
        };

        Context->PSSetShaderResources(
            0,
            2,
            srvs);

        ID3D11SamplerState* sampler =
            Sampler.Get();

        Context->PSSetSamplers(
            0,
            1,
            &sampler);

        Context->Draw(
            3,
            0);

        ID3D11ShaderResourceView*
            nullSrvs[] =
        {
            nullptr,
            nullptr
        };

        Context->PSSetShaderResources(
            0,
            2,
            nullSrvs);

        // CORE 11.7 TELEMETRY: sample foreground ownership at the exact point
        // where the swap-chain is about to be submitted. This lets the test
        // correlate Mortal Shell foreground focus with presenter starvation
        // without naming or special-casing any game process.
        DWORD foregroundPid = 0;
        HWND foregroundWindow =
            GetForegroundWindow();

        if (foregroundWindow)
        {
            (void)GetWindowThreadProcessId(
                foregroundWindow,
                &foregroundPid);
        }

        ForegroundProcessId.store(
            static_cast<std::uint32_t>(
                foregroundPid));
        ForegroundIsCurrentProcess.store(
            foregroundPid != 0 &&
            foregroundPid == GetCurrentProcessId()
                ? 1
                : 0);

        const std::uint64_t beforePresentUs =
            SteadyNowUs();

        if (slot.ReadyAtUs != 0 &&
            beforePresentUs >= slot.ReadyAtUs)
        {
            const std::uint64_t frameAgeUs =
                beforePresentUs -
                slot.ReadyAtUs;

            LastFrameAgeAtPresentUs.store(
                frameAgeUs);
            TotalFrameAgeAtPresentUs.fetch_add(
                frameAgeUs);
            FrameAgeAtPresentSamples.fetch_add(1);
        }

        PresentAttempts.fetch_add(1);
        LastPresenterActivityUs.store(
            beforePresentUs);
        PresenterStage.store(5);
        PresentInFlight.store(1);

        const auto presentCallStart =
            Clock::now();

        // CORE 11.17f CLIENT PRESENTER: the frame-latency waitable was
        // signaled before DrawSlot reached this point, so submit normally.
        // Do not use DXGI_PRESENT_DO_NOT_WAIT here: that flag intentionally
        // converts compositor back-pressure into WAS_STILL_DRAWING frame loss.
        // Resize/detach stop and join this presenter before replacing resources.
        // Keep the COM object alive and the slot reserved, but let NVDEC copy
        // other slots while DXGI waits for the compositor.
        auto presentingChain = SwapChain;
        HRESULT hr = multiverse::InvokePresentation(stateLock, [&] {
            return presentingChain->Present(0, presentFlags);
        });

        const auto presentCallEnd =
            Clock::now();
        const auto presentCallUs =
            std::chrono::duration_cast<
                std::chrono::microseconds>(
                presentCallEnd - presentCallStart)
                .count();

        if (presentCallUs >= 0)
        {
            LastPresentCallUs.store(
                static_cast<std::uint64_t>(
                    presentCallUs));
            TotalPresentCallUs.fetch_add(
                static_cast<std::uint64_t>(
                    presentCallUs));
            PresentCallSamples.fetch_add(1);
        }

        PresentInFlight.store(0);
        PresenterStage.store(4);
        LastPresenterActivityUs.store(
            SteadyNowUs());

        if (hr ==
            DXGI_ERROR_WAS_STILL_DRAWING)
        {
            PresentStillDrawing.fetch_add(1);
            DroppedPresents.fetch_add(
                1);

            return S_FALSE;
        }

        if (FAILED(hr))
        {
            PresentFailures.fetch_add(1);
            if (
                hr == DXGI_ERROR_DEVICE_REMOVED ||
                hr == DXGI_ERROR_DEVICE_RESET)
            {
                HRESULT removed =
                    Device
                        ? Device->GetDeviceRemovedReason()
                        : hr;

                SetMessage(
                    HrText(
                        L"D3D11 device removido/resetado",
                        removed));
            }
            else
            {
                SetMessage(
                    HrText(
                        L"IDXGISwapChain::Present",
                        hr));
            }

            return hr;
        }

        PresentedFrames.fetch_add(
            1);

        return S_OK;
    }

    int ReserveSlotForCopy()
    {
        std::lock_guard queueLock(
            QueueMutex);

        // If a frame is already pending, overwrite that same slot. It has not
        // reached the presenter yet, so replacing it is the lowest-latency
        // possible policy and never creates a backlog.
        if (PendingSlot >= 0 &&
            PendingSlot != PresentingSlot)
        {
            const int slot = PendingSlot;
            PendingSlot = -1;
            CopyingSlot = slot;
            ReplacedPendingFrames.fetch_add(1);
            return slot;
        }

        for (std::size_t attempt = 0;
             attempt < Slots.size();
             ++attempt)
        {
            const int slot =
                static_cast<int>(
                    (NextSubmitSlot + attempt) %
                    Slots.size());

            if (slot == PresentingSlot ||
                slot == CopyingSlot)
            {
                continue;
            }

            CopyingSlot = slot;
            NextSubmitSlot =
                static_cast<std::uint32_t>(
                    (slot + 1) % Slots.size());
            return slot;
        }

        return -1;
    }

    void CancelSlotCopy(
        int slot)
    {
        std::lock_guard queueLock(
            QueueMutex);

        if (CopyingSlot == slot)
            CopyingSlot = -1;
    }

    void RecordPresenterDequeuedSlot(
        int slot)
    {
        const std::uint64_t nowUs =
            SteadyNowUs();

        LastPresenterActivityUs.store(
            nowUs);
        PresenterDequeuedFrames.fetch_add(1);

        if (slot < 0 ||
            static_cast<std::size_t>(slot) >=
                Slots.size())
        {
            return;
        }

        const std::uint64_t readyUs =
            Slots[static_cast<std::size_t>(slot)]
                .ReadyAtUs;

        if (readyUs == 0 ||
            nowUs < readyUs)
        {
            return;
        }

        const std::uint64_t delayUs =
            nowUs - readyUs;

        LastQueueToPresenterUs.store(
            delayUs);
        TotalQueueToPresenterUs.fetch_add(
            delayUs);
        QueueToPresenterSamples.fetch_add(1);
    }

    void CommitSlotCopy(
        int slot)
    {
        {
            std::lock_guard queueLock(
                QueueMutex);

            if (CopyingSlot == slot)
                CopyingSlot = -1;

            if (PendingSlot >= 0 &&
                PendingSlot != slot)
            {
                ReplacedPendingFrames.fetch_add(1);
            }

            if (slot >= 0 &&
                static_cast<std::size_t>(slot) <
                    Slots.size())
            {
                Slots[static_cast<std::size_t>(slot)]
                    .ReadyAtUs = SteadyNowUs();
            }

            PendingSlot = slot;
        }

        QueueCv.notify_one();
    }

    // While DXGI is waiting for display capacity, newer decoded pictures may
    // arrive. Never present the frame that was newest *before* a long GPU/DWM
    // wait if an even newer frame is already ready. This is the second half of
    // latest-frame-wins: not only the pending queue, but also a frame selected
    // by the presenter may be superseded before it reaches the screen.
    void PromoteNewestPendingIfAny(
        int& slot)
    {
        std::lock_guard queueLock(
            QueueMutex);

        if (PendingSlot < 0 ||
            PendingSlot == slot)
        {
            return;
        }

        const int newest =
            PendingSlot;

        PendingSlot = -1;

        if (PresentingSlot == slot)
            PresentingSlot = -1;

        slot = newest;
        PresentingSlot = newest;
        RecordPresenterDequeuedSlot(
            newest);

        // The previously selected frame is intentionally skipped, not lost.
        // It is older than a fully copied frame and displaying it would only
        // add latency to remote play.
        ReplacedPendingFrames.fetch_add(1);
    }

    void PresenterLoop()
    {
        PresenterRunning.store(true);
        PresenterStage.store(1);
        PresentInFlight.store(0);
        LastPresenterActivityUs.store(
            SteadyNowUs());

        PresenterMmcssScope mmcss;

        SetThreadPriority(
            GetCurrentThread(),
            THREAD_PRIORITY_HIGHEST);

        while (!PresenterStop.load())
        {
            PresenterStage.store(1);
            int slot = -1;

            {
                std::unique_lock queueLock(
                    QueueMutex);

                QueueCv.wait_for(
                    queueLock,
                    std::chrono::milliseconds(8),
                    [this]()
                    {
                        return PresenterStop.load() ||
                            PendingSlot >= 0;
                    });

                // Heartbeat every time the presenter actually resumes after
                // the queue wait. If activity-age grows while stage=QUEUE,
                // the thread itself is not being scheduled promptly.
                LastPresenterActivityUs.store(
                    SteadyNowUs());

                if (PresenterStop.load())
                    break;

                if (PendingSlot < 0)
                    continue;

                slot = PendingSlot;
                PendingSlot = -1;
                PresentingSlot = slot;
                RecordPresenterDequeuedSlot(
                    slot);
            }

            PresenterStage.store(2);

            // CORE 11.17f CLIENT PRESENTER: a waitable swap chain is a gate,
            // not an advisory probe. DXGI documents that the frame-latency
            // handle should be waited before rendering the next frame. The old
            // zero-timeout + DO_NOT_WAIT path could repeatedly reject frames
            // with WAS_STILL_DRAWING when a foreground game owned compositor
            // time. Wait at most 20 ms (one 60 Hz frame + scheduling jitter);
            // the presenter is a dedicated thread and latest-frame-wins keeps
            // NVDEC/UDP unblocked while we wait.
            const auto waitStart = Clock::now();
            HANDLE waitable =
                FrameLatencyWaitable;
            bool frameLatencyReady = true;
            UINT presentFlags = 0;

            if (waitable)
            {
                const DWORD waitResult =
                    WaitForSingleObjectEx(
                        waitable,
                        20,
                        FALSE);

                if (waitResult == WAIT_OBJECT_0)
                {
                    FrameLatencySignals.fetch_add(1);
                    ConsecutiveFrameLatencyTimeouts.store(0);
                }
                else if (waitResult == WAIT_TIMEOUT)
                {
                    PresentWaitTimeouts.fetch_add(1);
                    FrameLatencyBypasses.fetch_add(1);
                    frameLatencyReady = false;
                }
                else
                {
                    FrameLatencyWaitFailures.fetch_add(1);
                    FrameLatencyBypasses.fetch_add(1);
                    frameLatencyReady = false;

                    const DWORD waitError =
                        waitResult == WAIT_FAILED
                            ? GetLastError()
                            : ERROR_GEN_FAILURE;

                    LastStatus.store(
                        static_cast<std::int32_t>(
                            HRESULT_FROM_WIN32(
                                waitError)));
                }
            }
            else
            {
                // CreateSwapChain normally requires a valid waitable handle.
                // Preserve a safe fallback if a future platform does not expose
                // one: present normally rather than disabling the client.
                FrameLatencyBypasses.fetch_add(1);
            }

            // NOTE (fix): a genuinely stuck/non-signaling waitable must not be
            // able to silence the presenter forever. Three consecutive misses
            // (roughly 60 ms, well past a single dropped 60 Hz frame) forces
            // one blocking present attempt as a safety valve, then the wait
            // gate resumes normally on the next cycle. This only overrides
            // frameLatencyReady after sustained failure — an isolated
            // timeout still skips exactly as CORE 11.17f intended.
            constexpr std::uint32_t kMaxConsecutiveFrameLatencyTimeouts = 3;
            if (!frameLatencyReady)
            {
                const auto misses =
                    ConsecutiveFrameLatencyTimeouts.fetch_add(1) + 1;
                if (misses >= kMaxConsecutiveFrameLatencyTimeouts)
                {
                    ConsecutiveFrameLatencyTimeouts.store(0);
                    frameLatencyReady = true;
                    presentFlags = DXGI_PRESENT_DO_NOT_WAIT;
                    FrameLatencyForcedPresents.fetch_add(1);
                }
            }


            LastPresenterActivityUs.store(
                SteadyNowUs());

            const auto waitEnd = Clock::now();
            const auto waitUs =
                std::chrono::duration_cast<
                    std::chrono::microseconds>(
                    waitEnd - waitStart)
                    .count();

            if (waitUs >= 0)
            {
                LastPresentWaitUs.store(
                    static_cast<std::uint64_t>(waitUs));
                TotalPresentWaitUs.fetch_add(
                    static_cast<std::uint64_t>(waitUs));
                PresentWaitSamples.fetch_add(1);
            }

            if (!frameLatencyReady)
            {
                // No display capacity became available inside one 60 Hz
                // budget. Do not call Present on an unavailable swap chain.
                // Release the selected slot; any newer decoded picture remains
                // pending and will be selected on the next iteration.
                std::lock_guard queueLock(
                    QueueMutex);

                if (PresentingSlot == slot)
                    PresentingSlot = -1;

                continue;
            }

            if (!PresenterStop.load())
            {
                // A newer decoded picture may have arrived between dequeue and
                // the readiness probe. Submit only the freshest completed slot.
                PromoteNewestPendingIfAny(
                    slot);

                const auto presentStart =
                    Clock::now();

                HRESULT hr = S_FALSE;

                {
                    PresenterStage.store(3);

                    const auto stateLockStart =
                        Clock::now();

                    std::unique_lock stateLock(
                        StateMutex);

                    const auto stateLockEnd =
                        Clock::now();
                    const auto stateLockUs =
                        std::chrono::duration_cast<
                            std::chrono::microseconds>(
                            stateLockEnd - stateLockStart)
                            .count();

                    if (stateLockUs >= 0)
                    {
                        LastStateLockWaitUs.store(
                            static_cast<std::uint64_t>(
                                stateLockUs));
                        TotalStateLockWaitUs.fetch_add(
                            static_cast<std::uint64_t>(
                                stateLockUs));
                        StateLockWaitSamples.fetch_add(1);
                    }

                    LastPresenterActivityUs.store(
                        SteadyNowUs());
                    PresenterStage.store(4);

                    if (Attached.load() &&
                        slot >= 0 &&
                        static_cast<std::size_t>(slot) <
                            Slots.size())
                    {
                        hr = DrawSlot(
                            Slots[static_cast<std::size_t>(slot)], stateLock, presentFlags);
                    }
                }

                const auto presentEnd =
                    Clock::now();
                const auto presentUs =
                    std::chrono::duration_cast<
                        std::chrono::microseconds>(
                        presentEnd - presentStart)
                        .count();

                if (presentUs >= 0)
                {
                    LastPresentUs.store(
                        static_cast<std::uint64_t>(presentUs));
                    TotalPresentUs.fetch_add(
                        static_cast<std::uint64_t>(presentUs));
                    PresentSamples.fetch_add(1);
                }

                LastStatus.store(
                    HrStatus(hr));
            }

            {
                std::lock_guard queueLock(
                    QueueMutex);

                if (PresentingSlot == slot)
                    PresentingSlot = -1;
            }
        }

        {
            std::lock_guard queueLock(
                QueueMutex);
            PendingSlot = -1;
            PresentingSlot = -1;
            CopyingSlot = -1;
        }

        PresentInFlight.store(0);
        PresenterStage.store(0);
        PresenterRunning.store(false);
    }

    void StartPresenter()
    {
        if (PresenterThread.joinable() ||
            !Attached.load())
        {
            return;
        }

        PresenterStop.store(false);
        PresenterThread =
            std::thread(
                [this]()
                {
                    PresenterLoop();
                });
    }

    bool StopPresenter(DWORD timeoutMs = INFINITE)
    {
        PresenterStop.store(true);
        QueueCv.notify_all();

        if (!multiverse::JoinPresenter(PresenterThread, timeoutMs))
            return false;

        PresenterRunning.store(false);

        std::lock_guard queueLock(
            QueueMutex);
        PendingSlot = -1;
        PresentingSlot = -1;
        CopyingSlot = -1;
        return true;
    }

    void CloseFrameLatencyHandle()
    {
        if (FrameLatencyWaitable)
        {
            CloseHandle(
                FrameLatencyWaitable);
            FrameLatencyWaitable = nullptr;
        }
    }
};

D3D11NvdecRenderer::D3D11NvdecRenderer()
    :
    m_impl(
        new Impl())
{
}

D3D11NvdecRenderer::~D3D11NvdecRenderer()
{
    Shutdown();

    delete m_impl;
    m_impl = nullptr;
}

std::int32_t
D3D11NvdecRenderer::AttachPanel(
    IUnknown* panelUnknown,
    CUdevice cudaDevice,
    std::uint32_t width,
    std::uint32_t height)
{
    if (!m_impl ||
        !panelUnknown)
    {
        return E_POINTER;
    }

    if (!m_impl->StopPresenter(100))
        return HRESULT_FROM_WIN32(WAIT_TIMEOUT);

    HRESULT hr = S_OK;

    {
        std::lock_guard lock(
            m_impl->StateMutex);

        m_impl->Attached.store(false);
        m_impl->CudaDevice = cudaDevice;

        // A removed device cannot be repaired by resizing/reusing its swap chain.
        // Attach runs with the decoder's CUDA context current, after the presenter
        // joined; release CUDA registrations before releasing their D3D resources.
        if (m_impl->Device && FAILED(m_impl->Device->GetDeviceRemovedReason()))
        {
            if (m_impl->PanelNative) m_impl->PanelNative->SetSwapChain(nullptr);
            m_impl->CloseFrameLatencyHandle();
            m_impl->PanelNative.Reset();
            m_impl->BackBufferRtv.Reset();
            m_impl->SwapChain.Reset();
            m_impl->DestroyPlaneResources();
            m_impl->Sampler.Reset();
            m_impl->PixelShader.Reset();
            m_impl->VertexShader.Reset();
            m_impl->Context.Reset();
            m_impl->Device.Reset();
            m_impl->Factory.Reset();
            m_impl->Adapter.Reset();
        }

        hr =
            m_impl->CreateDeviceForCuda(
                cudaDevice);

        if (FAILED(hr))
        {
            m_impl->LastStatus.store(
                HrStatus(hr));
            return HrStatus(hr);
        }

        if (m_impl->PanelNative)
        {
            m_impl->PanelNative->
                SetSwapChain(
                    nullptr);
        }

        m_impl->CloseFrameLatencyHandle();
        m_impl->PanelNative.Reset();
        m_impl->BackBufferRtv.Reset();
        m_impl->SwapChain.Reset();

        hr =
            m_impl->CreateSwapChain(
                panelUnknown,
                width,
                height);

        m_impl->LastStatus.store(
            HrStatus(hr));
    }

    if (SUCCEEDED(hr))
    {
        m_impl->StartPresenter();
        m_impl->SetMessage(
            L"Core 09.3.4 presenter ativo • waitable swap-chain • MMCSS/GPU priority • latest-frame-wins.");
    }

    return HrStatus(hr);
}

std::int32_t
D3D11NvdecRenderer::Resize(
    std::uint32_t width,
    std::uint32_t height)
{
    if (!m_impl)
        return E_POINTER;

    // Do not stop/restart the presenter when only a redundant XAML size
    // notification arrived. The state lock also protects the size snapshot.
    {
        std::unique_lock lock(m_impl->StateMutex, std::try_to_lock);
        if (lock.owns_lock() && !m_impl->PresenterStop.load() &&
            m_impl->Attached.load() && m_impl->PresenterRunning.load() &&
            std::max<std::uint32_t>(width, 2u) == m_impl->TargetWidth &&
            std::max<std::uint32_t>(height, 2u) == m_impl->TargetHeight)
            return 0;
    }

    if (!m_impl->StopPresenter(100))
        return HRESULT_FROM_WIN32(WAIT_TIMEOUT);

    HRESULT hr = S_FALSE;
    bool restart = false;

    {
        std::lock_guard lock(
            m_impl->StateMutex);

        restart =
            m_impl->Attached.load();

        hr =
            m_impl->ResizeSwapChain(
                width,
                height);

        m_impl->LastStatus.store(
            HrStatus(hr));
    }

    // A recoverable ResizeBuffers/matrix failure must not strand the
    // presenter stopped when a valid old or new render target still exists.
    if (restart && m_impl->SwapChain && m_impl->BackBufferRtv)
        m_impl->StartPresenter();

    return HrStatus(hr);
}

std::int32_t
D3D11NvdecRenderer::SetCompositionScale(float scaleX, float scaleY)
{
    if (!m_impl)
        return E_POINTER;
    if (!CompositionScaleMath::Valid(scaleX, scaleY))
        return E_INVALIDARG;

    std::unique_lock lock(m_impl->StateMutex, std::try_to_lock);
    if (!lock.owns_lock()) return HRESULT_FROM_WIN32(WAIT_TIMEOUT);
    const float previousX = m_impl->CompositionScaleX;
    const float previousY = m_impl->CompositionScaleY;
    m_impl->CompositionScaleX = scaleX;
    m_impl->CompositionScaleY = scaleY;
    HRESULT hr = m_impl->ApplyCompositionScale();
    if (FAILED(hr))
    {
        m_impl->CompositionScaleX = previousX;
        m_impl->CompositionScaleY = previousY;
        (void)m_impl->ApplyCompositionScale();
        m_impl->SetMessage(HrText(L"SetMatrixTransform", hr));
    }
    return HrStatus(hr);
}

bool D3D11NvdecRenderer::TryStopPresenter()
{
    return !m_impl || m_impl->StopPresenter(100);
}

void
D3D11NvdecRenderer::DetachPanel()
{
    if (!m_impl)
        return;

    m_impl->StopPresenter();

    std::lock_guard lock(
        m_impl->StateMutex);

    m_impl->Attached.store(false);

    if (m_impl->Context)
    {
        m_impl->Context->
            OMSetRenderTargets(
                0,
                nullptr,
                nullptr);

        // Flush only on teardown/resize; never on the hot presentation path.
        m_impl->Context->Flush();
    }

    if (m_impl->PanelNative)
    {
        // The managed caller invokes this from the owning XAML UI thread.
        m_impl->PanelNative->
            SetSwapChain(
                nullptr);
    }

    m_impl->CloseFrameLatencyHandle();
    m_impl->PanelNative.Reset();
    m_impl->BackBufferRtv.Reset();
    m_impl->SwapChain.Reset();

    m_impl->SetMessage(
        L"D3D11 SwapChainPanel desconectado.");
}

void
D3D11NvdecRenderer::Shutdown()
{
    if (!m_impl)
        return;

    m_impl->StopPresenter();

    std::lock_guard lock(
        m_impl->StateMutex);

    m_impl->Attached.store(false);

    if (m_impl->Context)
    {
        m_impl->Context->
            OMSetRenderTargets(
                0,
                nullptr,
                nullptr);

        m_impl->Context->Flush();
    }

    // Don't call SetSwapChain(nullptr) here because destruction may occur
    // away from the XAML UI thread. DevicesPage explicitly detaches first.
    m_impl->CloseFrameLatencyHandle();
    m_impl->PanelNative.Reset();
    m_impl->BackBufferRtv.Reset();
    m_impl->SwapChain.Reset();

    m_impl->DestroyPlaneResources();

    m_impl->Sampler.Reset();
    m_impl->PixelShader.Reset();
    m_impl->VertexShader.Reset();

    m_impl->Context.Reset();
    m_impl->Device.Reset();
    m_impl->Factory.Reset();
    m_impl->Adapter.Reset();
}

bool
D3D11NvdecRenderer::IsAttached() const
{
    return m_impl &&
        m_impl->Attached.load();
}

std::int32_t
D3D11NvdecRenderer::PresentNv12(
    CUdeviceptr sourceNv12,
    unsigned int sourcePitch,
    std::uint32_t sourceWidth,
    std::uint32_t sourceHeight,
    std::uint32_t sourceSurfaceHeight)
{
    if (!m_impl ||
        !sourceNv12)
    {
        return E_POINTER;
    }

    if (!m_impl->Attached.load())
        return 1;

    // A decoder format change can arrive without a XAML resize (for example
    // after a remote resolution switch/IDR). Recreating CUDA↔D3D resources
    // while the presenter still owns one of the old slots would be unsafe.
    // Stop only for the rare format transition, rebuild atomically and resume.
    const std::uint32_t evenSourceWidth =
        sourceWidth & ~1u;
    const std::uint32_t evenSourceHeight =
        sourceHeight & ~1u;

    bool sourceResourcesReady = false;

    {
        std::lock_guard stateLock(
            m_impl->StateMutex);

        sourceResourcesReady =
            m_impl->SourceWidth == evenSourceWidth &&
            m_impl->SourceHeight == evenSourceHeight &&
            m_impl->Slots[0].CudaY &&
            m_impl->Slots[1].CudaY &&
            m_impl->Slots[2].CudaY;
    }

    if (!sourceResourcesReady)
    {
        if (!m_impl->StopPresenter(100))
            return HRESULT_FROM_WIN32(WAIT_TIMEOUT);

        HRESULT resourceHr = S_OK;
        bool restartPresenter = false;

        {
            std::lock_guard stateLock(
                m_impl->StateMutex);

            restartPresenter =
                m_impl->Attached.load();

            if (restartPresenter)
            {
                resourceHr =
                    m_impl->EnsurePlaneResources(
                        sourceWidth,
                        sourceHeight);
            }
            else
            {
                resourceHr = E_ABORT;
            }
        }

        if (restartPresenter &&
            SUCCEEDED(resourceHr))
        {
            m_impl->StartPresenter();
        }

        if (FAILED(resourceHr))
        {
            m_impl->LastStatus.store(
                HrStatus(resourceHr));
            return HrStatus(resourceHr);
        }
    }

    m_impl->SubmittedFrames.fetch_add(1);

    const int slotIndex =
        m_impl->ReserveSlotForCopy();

    if (slotIndex < 0)
    {
        // This should be exceptionally rare with three slots. Never block the
        // NVDEC parser waiting for presentation: newest video is more valuable
        // than a delayed frame in a remote-gaming pipeline.
        m_impl->ReplacedPendingFrames.fetch_add(1);
        return 1;
    }

    std::int32_t copyStatus = 0;
    const auto copyStart = Clock::now();

    {
        // DrawSlot also uses StateMutex, but its wait on the DXGI frame-latency
        // object happens before this lock. The only serialization left here is
        // the short CUDA/D3D resource operation itself.
        std::lock_guard stateLock(
            m_impl->StateMutex);

        if (!m_impl->Attached.load())
        {
            m_impl->CancelSlotCopy(
                slotIndex);
            return 1;
        }

        HRESULT hr =
            m_impl->EnsurePlaneResources(
                sourceWidth,
                sourceHeight);

        if (FAILED(hr))
        {
            m_impl->CancelSlotCopy(
                slotIndex);
            m_impl->LastStatus.store(
                HrStatus(hr));
            return HrStatus(hr);
        }

        copyStatus =
            m_impl->CopyNv12ToSlot(
                m_impl->Slots[
                    static_cast<std::size_t>(slotIndex)],
                sourceNv12,
                sourcePitch,
                sourceWidth,
                sourceHeight,
                sourceSurfaceHeight);
    }

    const auto copyEnd = Clock::now();
    const auto copyUs =
        std::chrono::duration_cast<
            std::chrono::microseconds>(
            copyEnd - copyStart)
            .count();

    if (copyUs >= 0)
    {
        m_impl->LastCopyUs.store(
            static_cast<std::uint64_t>(copyUs));
        m_impl->TotalCopyUs.fetch_add(
            static_cast<std::uint64_t>(copyUs));
        m_impl->CopySamples.fetch_add(1);
    }

    if (copyStatus < 0)
    {
        m_impl->CancelSlotCopy(
            slotIndex);
        m_impl->LastStatus.store(
            copyStatus);
        return copyStatus;
    }

    m_impl->CommitSlotCopy(
        slotIndex);

    // Presentation is intentionally asynchronous from NVDEC. The dedicated
    // presenter consumes only the newest pending frame and never forces the
    // parser/decode thread to wait for DWM/game GPU scheduling.
    m_impl->LastStatus.store(0);
    return 0;
}

void
D3D11NvdecRenderer::GetStats(
    MV_D3D11RendererStatsNative& stats) const
{
    stats = {};

    if (!m_impl)
        return;

    stats.Attached =
        m_impl->Attached.load()
            ? 1
            : 0;

    stats.LastStatus =
        m_impl->LastStatus.load();

    // Probe telemetry must stay readable while the presenter is blocked in
    // StateMutex/Present(). Use the last dimension snapshot if the lifecycle
    // lock is busy instead of making the UI diagnostics wait behind it.
    stats.TargetWidth =
        m_impl->StatsTargetWidth.load();
    stats.TargetHeight =
        m_impl->StatsTargetHeight.load();
    stats.SourceWidth =
        m_impl->StatsSourceWidth.load();
    stats.SourceHeight =
        m_impl->StatsSourceHeight.load();

    {
        std::unique_lock stateLock(
            m_impl->StateMutex,
            std::try_to_lock);

        if (stateLock.owns_lock())
        {
            stats.TargetWidth =
                m_impl->TargetWidth;

            stats.TargetHeight =
                m_impl->TargetHeight;

            stats.SourceWidth =
                m_impl->SourceWidth;

            stats.SourceHeight =
                m_impl->SourceHeight;

            m_impl->StatsTargetWidth.store(
                stats.TargetWidth);
            m_impl->StatsTargetHeight.store(
                stats.TargetHeight);
            m_impl->StatsSourceWidth.store(
                stats.SourceWidth);
            m_impl->StatsSourceHeight.store(
                stats.SourceHeight);
        }
    }

    stats.SubmittedFrames =
        m_impl->SubmittedFrames.load();

    stats.PresentedFrames =
        m_impl->PresentedFrames.load();

    stats.DroppedPresents =
        m_impl->DroppedPresents.load();

    stats.ReplacedPendingFrames =
        m_impl->ReplacedPendingFrames.load();

    stats.PresentWaitTimeouts =
        m_impl->PresentWaitTimeouts.load();

    stats.PresenterDequeuedFrames =
        m_impl->PresenterDequeuedFrames.load();

    stats.FrameLatencySignals =
        m_impl->FrameLatencySignals.load();

    stats.FrameLatencyBypasses =
        m_impl->FrameLatencyBypasses.load();

    stats.FrameLatencyWaitFailures =
        m_impl->FrameLatencyWaitFailures.load();

    stats.PresentAttempts =
        m_impl->PresentAttempts.load();

    stats.PresentStillDrawing =
        m_impl->PresentStillDrawing.load();

    stats.PresentFailures =
        m_impl->PresentFailures.load();

    stats.ForegroundProcessId =
        m_impl->ForegroundProcessId.load();

    stats.ForegroundIsCurrentProcess =
        m_impl->ForegroundIsCurrentProcess.load();

    stats.PresenterStage =
        m_impl->PresenterStage.load();

    stats.PresentInFlight =
        m_impl->PresentInFlight.load();

    stats.LastGpuCopyMs =
        m_impl->LastCopyUs.load() /
        1000.0;

    std::uint64_t copySamples =
        m_impl->CopySamples.load();

    stats.AverageGpuCopyMs =
        copySamples > 0
            ? m_impl->TotalCopyUs.load() /
                1000.0 /
                static_cast<double>(
                    copySamples)
            : 0.0;

    stats.LastPresentSubmitMs =
        m_impl->LastPresentUs.load() /
        1000.0;

    std::uint64_t presentSamples =
        m_impl->PresentSamples.load();

    stats.AveragePresentSubmitMs =
        presentSamples > 0
            ? m_impl->TotalPresentUs.load() /
                1000.0 /
                static_cast<double>(
                    presentSamples)
            : 0.0;

    stats.LastPresentWaitMs =
        m_impl->LastPresentWaitUs.load() /
        1000.0;

    const std::uint64_t waitSamples =
        m_impl->PresentWaitSamples.load();

    stats.AveragePresentWaitMs =
        waitSamples > 0
            ? m_impl->TotalPresentWaitUs.load() /
                1000.0 /
                static_cast<double>(waitSamples)
            : 0.0;

    stats.LastQueueToPresenterMs =
        m_impl->LastQueueToPresenterUs.load() /
        1000.0;

    const std::uint64_t queueSamples =
        m_impl->QueueToPresenterSamples.load();

    stats.AverageQueueToPresenterMs =
        queueSamples > 0
            ? m_impl->TotalQueueToPresenterUs.load() /
                1000.0 /
                static_cast<double>(queueSamples)
            : 0.0;

    stats.LastStateLockWaitMs =
        m_impl->LastStateLockWaitUs.load() /
        1000.0;

    const std::uint64_t stateLockSamples =
        m_impl->StateLockWaitSamples.load();

    stats.AverageStateLockWaitMs =
        stateLockSamples > 0
            ? m_impl->TotalStateLockWaitUs.load() /
                1000.0 /
                static_cast<double>(stateLockSamples)
            : 0.0;

    stats.LastPresentCallMs =
        m_impl->LastPresentCallUs.load() /
        1000.0;

    const std::uint64_t presentCallSamples =
        m_impl->PresentCallSamples.load();

    stats.AveragePresentCallMs =
        presentCallSamples > 0
            ? m_impl->TotalPresentCallUs.load() /
                1000.0 /
                static_cast<double>(presentCallSamples)
            : 0.0;

    stats.LastFrameAgeAtPresentMs =
        m_impl->LastFrameAgeAtPresentUs.load() /
        1000.0;

    const std::uint64_t frameAgeSamples =
        m_impl->FrameAgeAtPresentSamples.load();

    stats.AverageFrameAgeAtPresentMs =
        frameAgeSamples > 0
            ? m_impl->TotalFrameAgeAtPresentUs.load() /
                1000.0 /
                static_cast<double>(frameAgeSamples)
            : 0.0;

    const std::uint64_t activityUs =
        m_impl->LastPresenterActivityUs.load();
    const std::uint64_t nowUs =
        SteadyNowUs();

    stats.PresenterActivityAgeMs =
        activityUs > 0 &&
        nowUs >= activityUs
            ? static_cast<double>(
                nowUs - activityUs) /
                1000.0
            : 0.0;
}

std::wstring
D3D11NvdecRenderer::GetLastMessage() const
{
    return m_impl
        ? m_impl->Message()
        : L"D3D11 renderer indisponível.";
}

#endif
