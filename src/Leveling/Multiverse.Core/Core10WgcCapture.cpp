#include "Core10WgcCapture.h"
#include "CaptureCadence.h"

#include <dxgi.h>
#include <wrl/client.h>
#include <windows.graphics.capture.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>

#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Metadata.h>
#include <winrt/Windows.Graphics.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>

#include <algorithm>
#include <atomic>
#include <condition_variable>
#include <mutex>
#include <thread>
#include <utility>
#include <vector>
#include <string>

using namespace winrt;
using Microsoft::WRL::ComPtr;

namespace
{
    // GraphicsCaptureItem is agile and may back multiple capture sessions.
    // Recreating it per Start/Stop leaked two ALPC handles per cycle on the
    // tested Windows build. Retain only monitor identities, never sessions,
    // frame pools, textures, callbacks, or encoder resources.
    struct MonitorCaptureIdentity
    {
        HMONITOR Monitor{};
        std::wstring DeviceName;
        winrt::Windows::Graphics::Capture::GraphicsCaptureItem Item{ nullptr };
        winrt::event_token ClosedToken{};
        std::shared_ptr<std::atomic<bool>> Closed = std::make_shared<std::atomic<bool>>(false);
        ~MonitorCaptureIdentity() noexcept
        {
            try { if (Item && ClosedToken.value) Item.Closed(ClosedToken); } catch (...) {}
        }
    };

    winrt::Windows::Graphics::Capture::GraphicsCaptureItem GetMonitorCaptureIdentity(HMONITOR monitor)
    {
        static std::mutex cacheMutex;
        static std::vector<std::unique_ptr<MonitorCaptureIdentity>> cache;
        std::lock_guard<std::mutex> lock(cacheMutex);
        MONITORINFOEXW info{}; info.cbSize = sizeof(info);
        if (!GetMonitorInfoW(monitor, &info)) winrt::throw_last_error();
        // Retire removed monitors and identities closed by Windows. A recycled
        // HMONITOR is not sufficient identity when its device name changed.
        for (auto it = cache.begin(); it != cache.end();)
        {
            MONITORINFOEXW current{}; current.cbSize = sizeof(current);
            if ((*it)->Closed->load(std::memory_order_acquire) ||
                !GetMonitorInfoW((*it)->Monitor, &current) || (*it)->DeviceName != current.szDevice)
                it = cache.erase(it);
            else ++it;
        }
        for (const auto& entry : cache)
            if (entry->Monitor == monitor && entry->DeviceName == info.szDevice)
                return entry->Item;
        auto entry = std::make_unique<MonitorCaptureIdentity>();
        entry->Monitor = monitor;
        entry->DeviceName = info.szDevice;
        auto factory = winrt::get_activation_factory<
            winrt::Windows::Graphics::Capture::GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        winrt::check_hresult(factory->CreateForMonitor(monitor,
            winrt::guid_of<ABI::Windows::Graphics::Capture::IGraphicsCaptureItem>(),
            reinterpret_cast<void**>(winrt::put_abi(entry->Item))));
        const std::weak_ptr<std::atomic<bool>> closed = entry->Closed;
        entry->ClosedToken = entry->Item.Closed([closed](auto const&, auto const&) noexcept {
            if (auto flag = closed.lock()) flag->store(true, std::memory_order_release);
        });
        auto item = entry->Item;
        // Explicit bound even on virtual-display systems with frequent churn.
        constexpr std::size_t MaxCachedMonitors = 16;
        if (cache.size() >= MaxCachedMonitors) cache.erase(cache.begin());
        cache.push_back(std::move(entry));
        return item;
    }

    template <typename T>
    winrt::com_ptr<T> GetDxgiInterface(
        const winrt::Windows::Foundation::IInspectable& object)
    {
        auto access = object.as<
            ::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();

        winrt::com_ptr<T> result;
        winrt::check_hresult(
            access->GetInterface(
                winrt::guid_of<T>(),
                result.put_void()));
        return result;
    }

    double FrameAgeMs(
        const winrt::Windows::Foundation::TimeSpan& timestamp) noexcept
    {
        LARGE_INTEGER qpc{};
        LARGE_INTEGER frequency{};
        if (!QueryPerformanceCounter(&qpc) ||
            !QueryPerformanceFrequency(&frequency) ||
            frequency.QuadPart <= 0)
        {
            return 0.0;
        }

        const long double now100ns =
            static_cast<long double>(qpc.QuadPart) * 10000000.0L /
            static_cast<long double>(frequency.QuadPart);
        const long double frame100ns =
            static_cast<long double>(timestamp.count());
        const long double delta100ns =
            std::max<long double>(0.0L, now100ns - frame100ns);
        return static_cast<double>(delta100ns / 10000.0L);
    }

    long long SteadyNowUs() noexcept
    {
        return std::chrono::duration_cast<std::chrono::microseconds>(
            std::chrono::steady_clock::now().time_since_epoch()).count();
    }

    double AgeMsFrom(long long lastUs) noexcept
    {
        if (lastUs <= 0)
            return -1.0;

        const auto nowUs = SteadyNowUs();
        return std::max<long long>(0, nowUs - lastUs) / 1000.0;
    }

    void TraceWgcStart(const wchar_t* stage, HRESULT hr = S_OK) noexcept
    {
        wchar_t buffer[320]{};
        (void)swprintf_s(
            buffer,
            L"[MV11K][WGC_START] stage=%ls hr=0x%08X\n",
            stage ? stage : L"UNKNOWN",
            static_cast<unsigned int>(hr));
        OutputDebugStringW(buffer);
    }

    void TraceStartFailure(const wchar_t* stage, HRESULT hr) noexcept
    {
        TraceWgcStart(stage, hr);
    }
}

namespace multiverse
{
    struct Core10WgcCapture::Impl
    {
        FrameCallback Callback;
        std::mutex CallbackMutex;
        std::atomic<std::uint32_t> CallbacksInFlight{ 0 };
        std::atomic<long long> LastCallbackSeenAtUs{ 0 };
        std::atomic<long long> LastCallbackCompletedAtUs{ 0 };
        std::mutex CallbackDrainMutex;
        std::condition_variable CallbackDrainCv;

        // CORE 11.17 - FrameArrived is now a minimal producer. The newest WGC
        // frame is handed to this worker, which owns all surface extraction and
        // downstream D3D11 work. This prevents CopyResource/NVENC pressure from
        // blocking WGC's free-threaded FrameArrived callback.
        std::mutex DeliveryMutex;
        std::condition_variable DeliveryCv;
        std::thread DeliveryWorker;
        std::atomic<bool> DeliveryWorkerStop{ true };
        std::atomic<bool> DeliveryWorkerAlive{ false };
        winrt::Windows::Graphics::Capture::Direct3D11CaptureFrame PendingFrame{ nullptr };
        std::chrono::steady_clock::time_point PendingArrival{};
        std::atomic<std::uint64_t> ReplacedPendingFrames{ 0 };
        std::atomic<std::uint64_t> FramesDelivered{ 0 };
        std::atomic<long long> LastDeliveryDelayUs{ 0 };

        struct CallbackGuard
        {
            Impl* Owner = nullptr;
            ~CallbackGuard() noexcept
            {
                if (!Owner)
                    return;
                Owner->LastCallbackCompletedAtUs.store(
                    SteadyNowUs(),
                    std::memory_order_relaxed);
                Owner->CallbackSubstage.store(0, std::memory_order_release);
                Owner->CurrentDrainFrames.store(0, std::memory_order_release);
                Owner->SetCallbackStage(0); // IDLE
                if (Owner->CallbacksInFlight.fetch_sub(1, std::memory_order_acq_rel) == 1)
                    Owner->CallbackDrainCv.notify_all();
            }
        };

        winrt::Windows::Graphics::Capture::GraphicsCaptureItem Item{ nullptr };
        winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DDevice WinrtDevice{ nullptr };
        winrt::Windows::Graphics::Capture::Direct3D11CaptureFramePool FramePool{ nullptr };
        winrt::Windows::Graphics::Capture::GraphicsCaptureSession Session{ nullptr };
        winrt::event_token FrameArrivedToken{};
        winrt::event_token ClosedToken{};

        std::atomic<bool> Running{ false };
        std::atomic<std::uint64_t> FramesArrived{ 0 };
        std::atomic<std::uint64_t> EmptyCallbacks{ 0 };
        std::atomic<std::uint64_t> Restarts{ 0 };
        std::atomic<long long> LastCallbackUs{ 0 };
        std::atomic<std::uint64_t> TotalCallbackUs{ 0 };
        std::atomic<std::uint64_t> CallbackSamples{ 0 };
        std::atomic<long long> LastFrameAgeUs{ 0 };
        std::atomic<std::uint64_t> TotalFrameAgeUs{ 0 };
        std::atomic<std::uint64_t> FrameAgeSamples{ 0 };
        std::atomic<long> LastStatus{ S_OK };
        std::atomic<std::uint32_t> StartStage{ 0 };
        std::atomic<long> LastStartStatus{ S_OK };
        std::atomic<long long> StartStageChangedAtUs{ 0 };
        std::atomic<bool> StopInProgress{ false };
        std::atomic<std::uint32_t> StopStage{ 0 };
        std::atomic<long long> StopStageChangedAtUs{ 0 };

        // CORE 11.1E DIAG - lock-free callback stage telemetry.
        std::atomic<std::uint32_t> CallbackStage{ 0 };
        std::atomic<std::uint32_t> CallbackSubstage{ 0 };
        std::atomic<std::uint64_t> CurrentDrainFrames{ 0 };
        std::atomic<long long> CallbackStageChangedAtUs{ 0 };

        void SetCallbackStage(std::uint32_t stage) noexcept
        {
            CallbackStage.store(stage, std::memory_order_release);
            CallbackStageChangedAtUs.store(
                SteadyNowUs(),
                std::memory_order_release);
        }

        void SetStopStage(std::uint32_t stage) noexcept
        {
            StopStage.store(stage, std::memory_order_release);
            StopStageChangedAtUs.store(
                SteadyNowUs(),
                std::memory_order_release);
        }

        void SetStartStage(std::uint32_t stage, const wchar_t* label) noexcept
        {
            StartStage.store(stage, std::memory_order_release);
            StartStageChangedAtUs.store(
                SteadyNowUs(),
                std::memory_order_release);
            TraceWgcStart(label, static_cast<HRESULT>(LastStartStatus.load(std::memory_order_relaxed)));
        }

        void SetStartFailure(std::uint32_t stage, const wchar_t* label, HRESULT hr) noexcept
        {
            StartStage.store(stage, std::memory_order_release);
            StartStageChangedAtUs.store(
                SteadyNowUs(),
                std::memory_order_release);
            LastStartStatus.store(hr, std::memory_order_release);
            TraceStartFailure(label, hr);
        }

        void OnClosed(
            const winrt::Windows::Graphics::Capture::GraphicsCaptureItem&,
            const winrt::Windows::Foundation::IInspectable&) noexcept
        {
            Running.store(false, std::memory_order_release);
            LastStatus.store(
                HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED),
                std::memory_order_release);
        }

        void DeliveryLoop() noexcept
        {
            DeliveryWorkerAlive.store(true, std::memory_order_release);
            struct DeliveryWorkerGuard
            {
                Impl* Owner = nullptr;
                ~DeliveryWorkerGuard() noexcept
                {
                    if (Owner)
                        Owner->DeliveryWorkerAlive.store(false, std::memory_order_release);
                }
            } deliveryGuard{ this };

            try
            {
                winrt::init_apartment(winrt::apartment_type::multi_threaded);
            }
            catch (...)
            {
                LastStatus.store(E_FAIL, std::memory_order_release);
                return;
            }

            // Balance the successful RoInitialize on this worker, after its
            // per-frame WinRT references have gone out of scope. Repeated
            // workers otherwise retain COM/RPC resources across Start/Stop.
            struct ApartmentGuard
            {
                ~ApartmentGuard() noexcept { winrt::uninit_apartment(); }
            } apartmentGuard;

            for (;;)
            {
                winrt::Windows::Graphics::Capture::Direct3D11CaptureFrame frame{ nullptr };
                std::chrono::steady_clock::time_point arrival{};
                {
                    std::unique_lock<std::mutex> lock(DeliveryMutex);
                    DeliveryCv.wait(
                        lock,
                        [this]()
                        {
                            return DeliveryWorkerStop.load(std::memory_order_acquire) ||
                                static_cast<bool>(PendingFrame);
                        });

                    if (DeliveryWorkerStop.load(std::memory_order_acquire) && !PendingFrame)
                        break;

                    frame = std::move(PendingFrame);
                    PendingFrame = nullptr;
                    arrival = PendingArrival;
                    PendingArrival = {};
                }

                if (!frame)
                    continue;

                if (!Running.load(std::memory_order_acquire))
                {
                    try { frame.Close(); } catch (...) {}
                    continue;
                }

                try
                {
                    const auto deliveryStart = std::chrono::steady_clock::now();
                    const auto delayUs = std::chrono::duration_cast<std::chrono::microseconds>(
                        deliveryStart - arrival).count();
                    LastDeliveryDelayUs.store(
                        std::max<long long>(0, delayUs),
                        std::memory_order_relaxed);

                    auto texture =
                        GetDxgiInterface<ID3D11Texture2D>(frame.Surface());
                    if (!texture)
                    {
                        LastStatus.store(E_NOINTERFACE, std::memory_order_release);
                        try { frame.Close(); } catch (...) {}
                        continue;
                    }

                    D3D11_TEXTURE2D_DESC desc{};
                    texture->GetDesc(&desc);

                    const double ageMs = FrameAgeMs(frame.SystemRelativeTime());
                    const auto ageUs = static_cast<long long>(
                        std::max(0.0, ageMs) * 1000.0);
                    LastFrameAgeUs.store(ageUs, std::memory_order_relaxed);
                    TotalFrameAgeUs.fetch_add(
                        static_cast<std::uint64_t>(std::max<long long>(0, ageUs)),
                        std::memory_order_relaxed);
                    FrameAgeSamples.fetch_add(1, std::memory_order_relaxed);

                    FrameCallback callback;
                    {
                        std::lock_guard<std::mutex> lock(CallbackMutex);
                        callback = Callback;
                    }

                    if (callback && Running.load(std::memory_order_acquire))
                    {
                        callback(
                            texture.get(),
                            desc,
                            arrival);
                        FramesDelivered.fetch_add(1, std::memory_order_relaxed);
                    }

                    LastStatus.store(S_OK, std::memory_order_release);
                }
                catch (const winrt::hresult_error& error)
                {
                    LastStatus.store(error.code().value, std::memory_order_release);
                }
                catch (...)
                {
                    LastStatus.store(E_FAIL, std::memory_order_release);
                }

                try { frame.Close(); } catch (...) {}
            }
        }

        void OnFrameArrived(
            const winrt::Windows::Graphics::Capture::Direct3D11CaptureFramePool& sender,
            const winrt::Windows::Foundation::IInspectable&) noexcept
        {
            LastCallbackSeenAtUs.store(
                SteadyNowUs(),
                std::memory_order_relaxed);
            CallbacksInFlight.fetch_add(1, std::memory_order_acq_rel);
            CurrentDrainFrames.store(0, std::memory_order_release);
            CallbackSubstage.store(0, std::memory_order_release);
            SetCallbackStage(1); // ENTER
            CallbackGuard callbackGuard{ this };

            if (!Running.load(std::memory_order_acquire))
                return;

            const auto callbackStart = std::chrono::steady_clock::now();
            struct CallbackTimingGuard
            {
                Impl* Owner = nullptr;
                std::chrono::steady_clock::time_point Started{};
                ~CallbackTimingGuard() noexcept
                {
                    if (!Owner)
                        return;
                    const auto callbackUs =
                        std::chrono::duration_cast<std::chrono::microseconds>(
                            std::chrono::steady_clock::now() - Started).count();
                    Owner->LastCallbackUs.store(callbackUs, std::memory_order_relaxed);
                    Owner->TotalCallbackUs.fetch_add(
                        static_cast<std::uint64_t>(std::max<long long>(0, callbackUs)),
                        std::memory_order_relaxed);
                    Owner->CallbackSamples.fetch_add(1, std::memory_order_relaxed);
                }
            } callbackTiming{ this, callbackStart };

            try
            {
                winrt::Windows::Graphics::Capture::Direct3D11CaptureFrame latest{ nullptr };
                std::uint64_t drained = 0;

                // CORE 11.17: bounded latest-frame-wins drain. The pool has only
                // three buffers, so the callback can never spin indefinitely.
                // Crucially, no D3D11 surface work or CopyResource happens here.
                constexpr std::uint64_t kMaxFramesPerCallback = 3;
                SetCallbackStage(2); // DRAIN_POOL
                while (drained < kMaxFramesPerCallback)
                {
                    CallbackSubstage.store(1, std::memory_order_release); // TRY_GET_NEXT
                    auto next = sender.TryGetNextFrame();
                    if (!next)
                        break;
                    CallbackSubstage.store(2, std::memory_order_release); // GOT_FRAME
                    if (latest)
                    {
                        try { latest.Close(); } catch (...) {}
                    }
                    latest = std::move(next);
                    ++drained;
                    CurrentDrainFrames.store(drained, std::memory_order_release);
                }
                CallbackSubstage.store(0, std::memory_order_release);

                if (!latest)
                {
                    EmptyCallbacks.fetch_add(1, std::memory_order_relaxed);
                    return;
                }

                FramesArrived.fetch_add(
                    std::max<std::uint64_t>(1, drained),
                    std::memory_order_relaxed);

                SetCallbackStage(3); // PUBLISH_LATEST
                winrt::Windows::Graphics::Capture::Direct3D11CaptureFrame replaced{ nullptr };
                {
                    std::lock_guard<std::mutex> lock(DeliveryMutex);
                    if (DeliveryWorkerStop.load(std::memory_order_acquire))
                    {
                        try { latest.Close(); } catch (...) {}
                        return;
                    }

                    if (PendingFrame)
                    {
                        replaced = std::move(PendingFrame);
                        PendingFrame = nullptr;
                        ReplacedPendingFrames.fetch_add(1, std::memory_order_relaxed);
                    }

                    PendingFrame = std::move(latest);
                    PendingArrival = std::chrono::steady_clock::now();
                }

                if (replaced)
                {
                    try { replaced.Close(); } catch (...) {}
                }
                DeliveryCv.notify_one();
                LastStatus.store(S_OK, std::memory_order_release);
            }
            catch (const winrt::hresult_error& error)
            {
                LastStatus.store(error.code().value, std::memory_order_release);
            }
            catch (...)
            {
                LastStatus.store(E_FAIL, std::memory_order_release);
            }
        }

    };

    Core10WgcCapture::Core10WgcCapture()
        : m_impl(std::make_shared<Impl>())
    {
    }

    Core10WgcCapture::~Core10WgcCapture()
    {
        Stop();
    }

    HRESULT Core10WgcCapture::Start(
        ID3D11Device* device,
        HMONITOR monitor,
        FrameCallback callback,
        std::uint32_t targetFps)
    {
        if (!device || !monitor || !callback)
            return E_INVALIDARG;

        m_impl->LastStartStatus.store(S_OK, std::memory_order_relaxed);
        m_impl->SetStartStage(1, L"ENTER");
        m_impl->SetStartStage(2, L"STOP_PREVIOUS");
        Stop();

        m_impl->CallbacksInFlight.store(0, std::memory_order_relaxed);
        m_impl->FramesArrived.store(0, std::memory_order_relaxed);
        m_impl->EmptyCallbacks.store(0, std::memory_order_relaxed);
        m_impl->Restarts.store(0, std::memory_order_relaxed);
        m_impl->LastCallbackUs.store(0, std::memory_order_relaxed);
        m_impl->TotalCallbackUs.store(0, std::memory_order_relaxed);
        m_impl->CallbackSamples.store(0, std::memory_order_relaxed);
        m_impl->LastFrameAgeUs.store(0, std::memory_order_relaxed);
        m_impl->TotalFrameAgeUs.store(0, std::memory_order_relaxed);
        m_impl->FrameAgeSamples.store(0, std::memory_order_relaxed);
        m_impl->LastStatus.store(S_OK, std::memory_order_relaxed);
        m_impl->LastCallbackSeenAtUs.store(0, std::memory_order_relaxed);
        m_impl->LastCallbackCompletedAtUs.store(0, std::memory_order_relaxed);
        m_impl->ReplacedPendingFrames.store(0, std::memory_order_relaxed);
        m_impl->FramesDelivered.store(0, std::memory_order_relaxed);
        m_impl->LastDeliveryDelayUs.store(0, std::memory_order_relaxed);
        m_impl->DeliveryWorkerAlive.store(false, std::memory_order_relaxed);
        m_impl->DeliveryWorkerStop.store(true, std::memory_order_release);
        {
            std::lock_guard<std::mutex> deliveryLock(m_impl->DeliveryMutex);
            if (m_impl->PendingFrame)
            {
                try { m_impl->PendingFrame.Close(); } catch (...) {}
                m_impl->PendingFrame = nullptr;
            }
            m_impl->PendingArrival = {};
        }
        m_impl->StopInProgress.store(false, std::memory_order_release);
        m_impl->StopStage.store(0, std::memory_order_release);
        m_impl->StopStageChangedAtUs.store(0, std::memory_order_relaxed);

        try
        {
            m_impl->SetStartStage(3, L"IS_SUPPORTED");
            if (!winrt::Windows::Graphics::Capture::GraphicsCaptureSession::IsSupported())
            {
                m_impl->LastStatus.store(E_NOTIMPL, std::memory_order_release);
                m_impl->SetStartFailure(3, L"IS_SUPPORTED", E_NOTIMPL);
                return E_NOTIMPL;
            }

            m_impl->SetStartStage(4, L"QUERY_DXGI_DEVICE");
            ComPtr<IDXGIDevice> dxgiDevice;
            HRESULT hr = device->QueryInterface(
                IID_PPV_ARGS(dxgiDevice.GetAddressOf()));
            if (FAILED(hr))
            {
                m_impl->LastStatus.store(hr, std::memory_order_release);
                m_impl->SetStartFailure(4, L"QUERY_DXGI_DEVICE", hr);
                return hr;
            }

            m_impl->SetStartStage(5, L"CREATE_WINRT_DEVICE");
            winrt::com_ptr<IInspectable> inspectable;
            hr = CreateDirect3D11DeviceFromDXGIDevice(
                dxgiDevice.Get(),
                inspectable.put());
            if (FAILED(hr))
            {
                m_impl->LastStatus.store(hr, std::memory_order_release);
                m_impl->SetStartFailure(5, L"CREATE_WINRT_DEVICE", hr);
                return hr;
            }

            m_impl->WinrtDevice =
                inspectable.as<
                    winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DDevice>();

            m_impl->SetStartStage(6, L"CREATE_ITEM");
            auto item = GetMonitorCaptureIdentity(monitor);

            const auto size = item.Size();
            if (size.Width <= 0 || size.Height <= 0)
            {
                m_impl->LastStatus.store(E_FAIL, std::memory_order_release);
                m_impl->SetStartFailure(6, L"CREATE_ITEM", E_FAIL);
                return E_FAIL;
            }

            m_impl->SetStartStage(7, L"CREATE_FRAMEPOOL");
            // CORE 11.17 keeps three WGC buffers deliberately: one may be
            // owned by the delivery worker, one may be the replaceable latest
            // pending frame, and one remains available to the compositor.
            // Latest-frame-wins prevents this extra slack from adding a queue.
            constexpr int kFramePoolBufferCount = 3;
            auto framePool =
                winrt::Windows::Graphics::Capture::Direct3D11CaptureFramePool::CreateFreeThreaded(
                    m_impl->WinrtDevice,
                    winrt::Windows::Graphics::DirectX::DirectXPixelFormat::B8G8R8A8UIntNormalized,
                    kFramePoolBufferCount,
                    size);
            m_impl->SetStartStage(8, L"CREATE_SESSION");
            auto session = framePool.CreateCaptureSession(item);

            m_impl->SetStartStage(9, L"CONFIGURE_SESSION");
            if (winrt::Windows::Foundation::Metadata::ApiInformation::IsPropertyPresent(
                    L"Windows.Graphics.Capture.GraphicsCaptureSession",
                    L"IsCursorCaptureEnabled"))
            {
                session.IsCursorCaptureEnabled(false);
            }

            // RC20.4: the old fixed 14-ms eligibility suppressed high-rate
            // capture before our pacer. Use a fraction of the requested period
            // to accommodate compositor refresh quantization. The existing
            // ingress and encoder pacers still bound copies and output FPS.
            try
            {
                if (winrt::Windows::Foundation::Metadata::ApiInformation::IsPropertyPresent(
                        L"Windows.Graphics.Capture.GraphicsCaptureSession",
                        L"MinUpdateInterval"))
                {
                    session.MinUpdateInterval(WgcUpdateInterval(targetFps));
                }
            }
            catch (const winrt::hresult_error&)
            {
                // Optional tuning only. Capture must remain functional if the
                // installed Windows build exposes the metadata but rejects it.
            }
            catch (...)
            {
                // Preserve the existing WGC fallback behavior on any unexpected
                // runtime failure while configuring this optional property.
            }

            {
                std::lock_guard<std::mutex> lock(m_impl->CallbackMutex);
                m_impl->Callback = std::move(callback);
            }

            m_impl->DeliveryWorkerStop.store(false, std::memory_order_release);
            m_impl->DeliveryWorker = std::thread(
                [impl = m_impl]()
                {
                    impl->DeliveryLoop();
                });

            m_impl->SetStartStage(10, L"REGISTER_CALLBACKS");
            m_impl->Item = item;
            m_impl->FramePool = framePool;
            m_impl->Session = session;
            const std::weak_ptr<Impl> weakImpl = m_impl;
            m_impl->FrameArrivedToken =
                m_impl->FramePool.FrameArrived(
                    [weakImpl](
                        const winrt::Windows::Graphics::Capture::Direct3D11CaptureFramePool& sender,
                        const winrt::Windows::Foundation::IInspectable& args) noexcept
                    {
                        if (auto impl = weakImpl.lock())
                            impl->OnFrameArrived(sender, args);
                    });
            m_impl->ClosedToken =
                m_impl->Item.Closed(
                    [weakImpl](
                        const winrt::Windows::Graphics::Capture::GraphicsCaptureItem& item,
                        const winrt::Windows::Foundation::IInspectable& args) noexcept
                    {
                        if (auto impl = weakImpl.lock())
                            impl->OnClosed(item, args);
                    });

            m_impl->Running.store(true, std::memory_order_release);
            m_impl->Restarts.fetch_add(1, std::memory_order_relaxed);
            m_impl->LastStatus.store(S_OK, std::memory_order_release);
            m_impl->LastStartStatus.store(S_OK, std::memory_order_release);
            m_impl->SetStartStage(11, L"START_CAPTURE");
            m_impl->Session.StartCapture();
            m_impl->SetStartStage(12, L"COMPLETE");
            return S_OK;
        }
        catch (const winrt::hresult_error& error)
        {
            const HRESULT hr = error.code().value;
            m_impl->LastStatus.store(hr, std::memory_order_release);
            const auto failedStage = m_impl->StartStage.load(std::memory_order_acquire);
            m_impl->LastStartStatus.store(hr, std::memory_order_release);
            TraceStartFailure(L"EXCEPTION", hr);
            m_impl->StartStage.store(failedStage, std::memory_order_release);
            Stop();
            m_impl->LastStatus.store(hr, std::memory_order_release);
            return hr;
        }
        catch (...)
        {
            m_impl->LastStatus.store(E_FAIL, std::memory_order_release);
            const auto failedStage = m_impl->StartStage.load(std::memory_order_acquire);
            m_impl->LastStartStatus.store(E_FAIL, std::memory_order_release);
            TraceStartFailure(L"UNKNOWN_EXCEPTION", E_FAIL);
            m_impl->StartStage.store(failedStage, std::memory_order_release);
            Stop();
            m_impl->LastStatus.store(E_FAIL, std::memory_order_release);
            return E_FAIL;
        }
    }

    void Core10WgcCapture::Stop() noexcept
    {
        if (!m_impl)
            return;

        m_impl->StopInProgress.store(true, std::memory_order_release);
        m_impl->SetStopStage(1); // entered / producer disabled
        m_impl->Running.store(false, std::memory_order_release);

        try
        {
            m_impl->SetStopStage(2); // revoke FrameArrived
            if (m_impl->FramePool && m_impl->FrameArrivedToken.value != 0)
                m_impl->FramePool.FrameArrived(m_impl->FrameArrivedToken);

            m_impl->SetStopStage(3); // revoke item Closed
            if (m_impl->Item && m_impl->ClosedToken.value != 0)
                m_impl->Item.Closed(m_impl->ClosedToken);

            m_impl->SetStopStage(4); // GraphicsCaptureSession.Close
            if (m_impl->Session)
                m_impl->Session.Close();

            m_impl->SetStopStage(5); // Direct3D11CaptureFramePool.Close
            if (m_impl->FramePool)
                m_impl->FramePool.Close();
        }
        catch (...)
        {
        }

        // A free-threaded FrameArrived callback can already be executing when
        // the event is revoked. Native resources remain owned until callbacks
        // actually finish. The controller bounds its wait and retains the
        // native stop task on timeout; a timeout cannot authorize release.
        m_impl->SetStopStage(6); // callback drain
        {
            std::unique_lock<std::mutex> drainLock(m_impl->CallbackDrainMutex);
            // Notification updates an atomic predicate outside this mutex.
            // Repeat a bounded predicate wait to tolerate a missed wake; the
            // expiration NEVER authorizes resource release.
            while (!m_impl->CallbackDrainCv.wait_for(
                drainLock,
                std::chrono::milliseconds(250),
                [impl = m_impl]()
                {
                    return impl->CallbacksInFlight.load(std::memory_order_acquire) == 0;
                })) {}
        }

        m_impl->SetStopStage(7); // stop delivery worker
        m_impl->DeliveryWorkerStop.store(true, std::memory_order_release);
        m_impl->DeliveryCv.notify_all();
        if (m_impl->DeliveryWorker.joinable())
        {
            m_impl->DeliveryWorker.join();
        }
        {
            winrt::Windows::Graphics::Capture::Direct3D11CaptureFrame pending{ nullptr };
            {
                std::lock_guard<std::mutex> deliveryLock(m_impl->DeliveryMutex);
                pending = std::move(m_impl->PendingFrame);
                m_impl->PendingFrame = nullptr;
                m_impl->PendingArrival = {};
            }
            if (pending)
            {
                try { pending.Close(); } catch (...) {}
            }
        }

        m_impl->SetStopStage(8); // release WinRT/D3D references
        m_impl->FrameArrivedToken = {};
        m_impl->ClosedToken = {};
        m_impl->Session = nullptr;
        m_impl->FramePool = nullptr;
        m_impl->Item = nullptr;
        m_impl->WinrtDevice = nullptr;
        {
            std::lock_guard<std::mutex> lock(m_impl->CallbackMutex);
            m_impl->Callback = {};
        }

        m_impl->SetStopStage(9); // complete
        m_impl->StopInProgress.store(false, std::memory_order_release);
    }

    bool Core10WgcCapture::IsRunning() const noexcept
    {
        return m_impl && m_impl->Running.load(std::memory_order_acquire);
    }

    Core10WgcCapture::Stats Core10WgcCapture::Snapshot() const noexcept
    {
        Stats stats{};
        if (!m_impl)
            return stats;

        stats.FramesArrived = m_impl->FramesArrived.load(std::memory_order_relaxed);
        stats.EmptyCallbacks = m_impl->EmptyCallbacks.load(std::memory_order_relaxed);
        stats.Restarts = m_impl->Restarts.load(std::memory_order_relaxed);
        stats.LastCallbackMs = m_impl->LastCallbackUs.load(std::memory_order_relaxed) / 1000.0;
        const auto callbackSamples = m_impl->CallbackSamples.load(std::memory_order_relaxed);
        stats.AverageCallbackMs = callbackSamples == 0
            ? 0.0
            : (static_cast<double>(m_impl->TotalCallbackUs.load(std::memory_order_relaxed)) /
               static_cast<double>(callbackSamples)) / 1000.0;
        stats.LastFrameAgeMs = m_impl->LastFrameAgeUs.load(std::memory_order_relaxed) / 1000.0;
        const auto ageSamples = m_impl->FrameAgeSamples.load(std::memory_order_relaxed);
        stats.AverageFrameAgeMs = ageSamples == 0
            ? 0.0
            : (static_cast<double>(m_impl->TotalFrameAgeUs.load(std::memory_order_relaxed)) /
               static_cast<double>(ageSamples)) / 1000.0;
        stats.FramesDelivered = m_impl->FramesDelivered.load(std::memory_order_relaxed);
        stats.ReplacedPendingFrames = m_impl->ReplacedPendingFrames.load(std::memory_order_relaxed);
        stats.DeliveryWorkerAlive = m_impl->DeliveryWorkerAlive.load(std::memory_order_relaxed);
        stats.LastDeliveryDelayMs =
            m_impl->LastDeliveryDelayUs.load(std::memory_order_relaxed) / 1000.0;
        stats.LastStatus = static_cast<HRESULT>(m_impl->LastStatus.load(std::memory_order_relaxed));
        stats.CallbacksInFlight = m_impl->CallbacksInFlight.load(std::memory_order_relaxed);
        stats.Running = m_impl->Running.load(std::memory_order_relaxed);
        stats.LastCallbackAgoMs = AgeMsFrom(
            m_impl->LastCallbackSeenAtUs.load(std::memory_order_relaxed));
        stats.LastCallbackCompleteAgoMs = AgeMsFrom(
            m_impl->LastCallbackCompletedAtUs.load(std::memory_order_relaxed));
        stats.StopInProgress = m_impl->StopInProgress.load(std::memory_order_relaxed);
        stats.StopStage = m_impl->StopStage.load(std::memory_order_relaxed);
        stats.StopStageAgeMs = AgeMsFrom(
            m_impl->StopStageChangedAtUs.load(std::memory_order_relaxed));
        stats.CallbackStage = m_impl->CallbackStage.load(std::memory_order_relaxed);
        stats.CallbackSubstage = m_impl->CallbackSubstage.load(std::memory_order_relaxed);
        stats.CurrentDrainFrames = m_impl->CurrentDrainFrames.load(std::memory_order_relaxed);
        stats.CallbackStageAgeMs = AgeMsFrom(
            m_impl->CallbackStageChangedAtUs.load(std::memory_order_relaxed));
        stats.StartStage = m_impl->StartStage.load(std::memory_order_relaxed);
        stats.LastStartStatus = static_cast<HRESULT>(m_impl->LastStartStatus.load(std::memory_order_relaxed));
        stats.StartStageAgeMs = AgeMsFrom(
            m_impl->StartStageChangedAtUs.load(std::memory_order_relaxed));
        return stats;
    }
}
