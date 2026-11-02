#include "MultiverseCore.h"
#include "EncoderResolutionLimits.h"
#include "Core10SharedFrameRing.h"
#include "Core10WgcCapture.h"
#include "Core11AdaptivePressureController.h"
#include "FramePacer.h"
#include "RemoteFrameDeadlineScheduler.h"
#include "HighResolutionFrameTimer.h"
#include "SharedEncodedPacketQueue.h"

#include <Windows.h>
#include <avrt.h>
#include <d3d11.h>
#include <d3d11_4.h>
#include <d3dcompiler.h>
#include <dxgi1_6.h>
#include <wrl/client.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <cwchar>
#include <deque>
#include <functional>
#include <memory>
#include <mutex>
#include <new>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#ifdef MULTIVERSE_NVENC
#include "nvEncodeAPI.h"
#endif

using Microsoft::WRL::ComPtr;
using multiverse::EncodedPacket;
using multiverse::FramePacer;
using multiverse::RemoteFrameDeadlineScheduler;
using multiverse::HighResolutionFrameTimer;
using multiverse::Core10SharedFrameRing;
using multiverse::Core10WgcCapture;
using multiverse::Core11AdaptivePressureController;
using multiverse::Core11AdaptivePressureSample;
using multiverse::Core11AdaptivePressureState;
using multiverse::SharedEncodedPacketQueue;
using multiverse::EncodedQueueAdmissionPolicy;

#pragma comment(lib, "Avrt.lib")

namespace
{
    class MmcssThreadScope final
    {
    public:
        MmcssThreadScope(
            const wchar_t* taskName,
            AVRT_PRIORITY priority)
        {
            if (!taskName)
                return;

            DWORD taskIndex = 0;
            m_handle =
                AvSetMmThreadCharacteristicsW(
                    taskName,
                    &taskIndex);

            if (m_handle)
            {
                (void)AvSetMmThreadPriority(
                    m_handle,
                    priority);
            }
        }

        ~MmcssThreadScope()
        {
            if (m_handle)
            {
                (void)AvRevertMmThreadCharacteristics(
                    m_handle);
            }
        }

        MmcssThreadScope(
            const MmcssThreadScope&) = delete;
        MmcssThreadScope& operator=(
            const MmcssThreadScope&) = delete;

    private:
        HANDLE m_handle = nullptr;
    };

    void DisableBackgroundPowerThrottling()
    {
        PROCESS_POWER_THROTTLING_STATE state{};
        state.Version =
            PROCESS_POWER_THROTTLING_CURRENT_VERSION;

        // Explicit HighQoS: do not let Windows infer EcoQoS/LowQoS just
        // because the game owns the foreground window.
        state.ControlMask =
            PROCESS_POWER_THROTTLING_EXECUTION_SPEED;
        state.StateMask = 0;

        (void)SetProcessInformation(
            GetCurrentProcess(),
            ProcessPowerThrottling,
            &state,
            sizeof(state));
    }

    long long SteadyNowUs() noexcept
    {
        return std::chrono::duration_cast<std::chrono::microseconds>(
            std::chrono::steady_clock::now().time_since_epoch()).count();
    }

    void TraceNativeTeardown(const wchar_t* stage, const void* session) noexcept
    {
        wchar_t buffer[320]{};
        (void)swprintf_s(
            buffer,
            L"[MV11K][TEARDOWN] session=%p stage=%ls\n",
            session,
            stage ? stage : L"UNKNOWN");
        OutputDebugStringW(buffer);
    }

    double AgeMsFrom(long long lastUs) noexcept
    {
        if (lastUs <= 0)
            return -1.0;

        return std::max<long long>(0, SteadyNowUs() - lastUs) / 1000.0;
    }

    std::uint32_t ResolveCore11IngressFps(
        std::uint32_t targetFps) noexcept
    {
        if (targetFps == 0u)
            return 0u;

        const std::uint32_t boundedTarget =
            std::clamp<std::uint32_t>(targetFps, 30u, 120u);

        // CORE 11.4: keep a small jitter margin, not the old 2x ingress.
        // At 60 Hz this bounds full-frame shared-ring copies to 75/s
        // instead of 120/s, preserving 15 FPS of sampling headroom while
        // materially reducing capture-side GPU work under heavy games.
        return std::min<std::uint32_t>(135u, boundedTarget + 15u);
    }

    struct AtomicStageResetGuard
    {
        std::atomic<std::uint32_t>* Stage = nullptr;
        std::atomic<long long>* ChangedAtUs = nullptr;

        ~AtomicStageResetGuard() noexcept
        {
            if (Stage)
                Stage->store(0, std::memory_order_release);
            if (ChangedAtUs)
                ChangedAtUs->store(SteadyNowUs(), std::memory_order_release);
        }
    };

    struct GpuPriorityResult
    {
        HRESULT SetStatus = E_NOINTERFACE;
        HRESULT GetStatus = E_NOINTERFACE;
        INT EffectivePriority = 0;
    };

    GpuPriorityResult ConfigureGpuSchedulingPriority(
        ID3D11Device* device,
        INT relativePriority)
    {
        GpuPriorityResult result{};
        if (!device)
            return result;

        ComPtr<IDXGIDevice> dxgiDevice;
        const HRESULT queryHr = device->QueryInterface(
            IID_PPV_ARGS(dxgiDevice.GetAddressOf()));
        if (FAILED(queryHr) || !dxgiDevice)
        {
            result.SetStatus = queryHr;
            result.GetStatus = queryHr;
            return result;
        }

        // CORE 11 deliberately uses a modest relative priority. Microsoft
        // warns that aggressive GPU priority can hurt overall rendering.
        // CORE 11.5 still stays far below the extreme +7 range. The normal
        // profile is Capture +1 / Encode +2; sustained foreground-game
        // pressure may temporarily use Capture +2 / Encode +3.
        const INT requested = std::clamp<INT>(relativePriority, -3, 3);
        result.SetStatus = dxgiDevice->SetGPUThreadPriority(requested);
        if (SUCCEEDED(result.SetStatus))
            result.GetStatus = dxgiDevice->GetGPUThreadPriority(&result.EffectivePriority);
        else
            result.GetStatus = result.SetStatus;
        return result;
    }

    enum class ProcessGpuPriorityClass : int
    {
        Idle = 0,
        BelowNormal = 1,
        Normal = 2,
        AboveNormal = 3,
        High = 4,
        Realtime = 5
    };

    bool EnableIncreaseBasePriorityPrivilege()
    {
        HANDLE token = nullptr;

        if (!OpenProcessToken(
                GetCurrentProcess(),
                TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY,
                &token))
        {
            return false;
        }

        LUID luid{};
        const bool found =
            LookupPrivilegeValueW(
                nullptr,
                SE_INC_BASE_PRIORITY_NAME,
                &luid) != FALSE;

        bool enabled = false;

        if (found)
        {
            TOKEN_PRIVILEGES privileges{};
            privileges.PrivilegeCount = 1;
            privileges.Privileges[0].Luid = luid;
            privileges.Privileges[0].Attributes =
                SE_PRIVILEGE_ENABLED;

            SetLastError(ERROR_SUCCESS);

            enabled =
                AdjustTokenPrivileges(
                    token,
                    FALSE,
                    &privileges,
                    sizeof(privileges),
                    nullptr,
                    nullptr) != FALSE &&
                GetLastError() == ERROR_SUCCESS;
        }

        CloseHandle(token);
        return enabled;
    }

    struct ProcessGpuPriorityResult
    {
        ProcessGpuPriorityClass Requested = ProcessGpuPriorityClass::Normal;
        ProcessGpuPriorityClass Effective = ProcessGpuPriorityClass::Normal;
        HRESULT SetStatus = E_NOINTERFACE;
        HRESULT GetStatus = E_NOINTERFACE;
    };

    using SetProcessGpuPriorityFn =
        LONG (WINAPI*)(HANDLE, int);
    using GetProcessGpuPriorityFn =
        LONG (WINAPI*)(HANDLE, int*);

    HMODULE GetGdi32ForGpuScheduling()
    {
        HMODULE gdi32 = GetModuleHandleW(L"gdi32.dll");
        if (!gdi32)
            gdi32 = LoadLibraryW(L"gdi32.dll");
        return gdi32;
    }

    HRESULT QueryProcessGpuSchedulingPriority(
        ProcessGpuPriorityClass* priority)
    {
        if (!priority)
            return E_POINTER;

        HMODULE gdi32 = GetGdi32ForGpuScheduling();
        if (!gdi32)
            return HRESULT_FROM_WIN32(GetLastError());

        auto getPriority =
            reinterpret_cast<GetProcessGpuPriorityFn>(
                GetProcAddress(
                    gdi32,
                    "D3DKMTGetProcessSchedulingPriorityClass"));
        if (!getPriority)
            return HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND);

        int effective = static_cast<int>(ProcessGpuPriorityClass::Normal);
        const LONG status = getPriority(GetCurrentProcess(), &effective);
        if (status < 0)
            return HRESULT_FROM_NT(status);

        if (effective < static_cast<int>(ProcessGpuPriorityClass::Idle) ||
            effective > static_cast<int>(ProcessGpuPriorityClass::Realtime))
        {
            return E_UNEXPECTED;
        }

        *priority = static_cast<ProcessGpuPriorityClass>(effective);
        return S_OK;
    }

    ProcessGpuPriorityResult ConfigureProcessGpuSchedulingPriority(
        ProcessGpuPriorityClass priority)
    {
        ProcessGpuPriorityResult result{};
        result.Requested = priority;

        // CORE 11.10: WDDM process scheduling class is a separate lever from
        // IDXGIDevice relative priority. The host requests HIGH only while
        // streaming and always restores the class that existed before Start.
        // Never request REALTIME.
        (void)EnableIncreaseBasePriorityPrivilege();

        HMODULE gdi32 = GetGdi32ForGpuScheduling();
        if (!gdi32)
        {
            result.SetStatus = HRESULT_FROM_WIN32(GetLastError());
            result.GetStatus = result.SetStatus;
            return result;
        }

        auto setPriority =
            reinterpret_cast<SetProcessGpuPriorityFn>(
                GetProcAddress(
                    gdi32,
                    "D3DKMTSetProcessSchedulingPriorityClass"));
        if (!setPriority)
        {
            result.SetStatus = HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND);
            result.GetStatus = result.SetStatus;
            return result;
        }

        const LONG status =
            setPriority(
                GetCurrentProcess(),
                static_cast<int>(priority));
        result.SetStatus = status >= 0 ? S_OK : HRESULT_FROM_NT(status);
        if (FAILED(result.SetStatus))
        {
            result.GetStatus = result.SetStatus;
            return result;
        }

        result.GetStatus = QueryProcessGpuSchedulingPriority(&result.Effective);
        return result;
    }

    void ConfigureCaptureFrameLatency(
        ID3D11Device* device)
    {
        if (!device)
            return;

        ComPtr<IDXGIDevice1> dxgiDevice1;

        if (SUCCEEDED(
                device->QueryInterface(
                    IID_PPV_ARGS(
                        dxgiDevice1.GetAddressOf()))))
        {
            (void)dxgiDevice1->SetMaximumFrameLatency(1);
        }
    }

    struct MonitorRecord
    {
        HMONITOR Handle = nullptr;
        MONITORINFOEXW Info{};
    };

    BOOL CALLBACK MonitorEnumProc(
        HMONITOR monitor,
        HDC,
        LPRECT,
        LPARAM userData)
    {
        auto* monitors =
            reinterpret_cast<std::vector<MonitorRecord>*>(userData);

        if (!monitors)
            return FALSE;

        MonitorRecord record{};
        record.Handle = monitor;
        record.Info.cbSize = sizeof(MONITORINFOEXW);

        if (GetMonitorInfoW(
                monitor,
                reinterpret_cast<MONITORINFO*>(&record.Info)))
        {
            monitors->push_back(record);
        }

        return TRUE;
    }

    std::vector<MonitorRecord> EnumerateMonitors()
    {
        std::vector<MonitorRecord> monitors;

        EnumDisplayMonitors(
            nullptr,
            nullptr,
            MonitorEnumProc,
            reinterpret_cast<LPARAM>(&monitors));

        return monitors;
    }

    std::uint32_t GetRefreshRate(
        const wchar_t* deviceName)
    {
        if (!deviceName || deviceName[0] == L'\0')
            return 0;

        DEVMODEW mode{};
        mode.dmSize = sizeof(DEVMODEW);

        if (!EnumDisplaySettingsW(
                deviceName,
                ENUM_CURRENT_SETTINGS,
                &mode))
        {
            return 0;
        }

        return mode.dmDisplayFrequency > 1
            ? static_cast<std::uint32_t>(
                mode.dmDisplayFrequency)
            : 0;
    }

    HRESULT FindOutputAndAdapter(
        const wchar_t* deviceName,
        ComPtr<IDXGIAdapter1>& adapterResult,
        ComPtr<IDXGIOutput>& outputResult)
    {
        if (!deviceName || deviceName[0] == L'\0')
            return E_INVALIDARG;

        ComPtr<IDXGIFactory1> factory;

        HRESULT hr = CreateDXGIFactory1(
            IID_PPV_ARGS(factory.GetAddressOf()));

        if (FAILED(hr))
            return hr;

        for (UINT adapterIndex = 0;; ++adapterIndex)
        {
            ComPtr<IDXGIAdapter1> adapter;

            hr = factory->EnumAdapters1(
                adapterIndex,
                adapter.GetAddressOf());

            if (hr == DXGI_ERROR_NOT_FOUND)
                break;

            if (FAILED(hr))
                return hr;

            for (UINT outputIndex = 0;; ++outputIndex)
            {
                ComPtr<IDXGIOutput> output;

                hr = adapter->EnumOutputs(
                    outputIndex,
                    output.GetAddressOf());

                if (hr == DXGI_ERROR_NOT_FOUND)
                    break;

                if (FAILED(hr))
                    return hr;

                DXGI_OUTPUT_DESC desc{};

                if (FAILED(output->GetDesc(&desc)))
                    continue;

                if (_wcsicmp(
                        desc.DeviceName,
                        deviceName) == 0)
                {
                    adapterResult = adapter;
                    outputResult = output;
                    return S_OK;
                }
            }
        }

        return DXGI_ERROR_NOT_FOUND;
    }

    void CalculatePreviewSize(
        std::uint32_t sourceWidth,
        std::uint32_t sourceHeight,
        std::uint32_t& previewWidth,
        std::uint32_t& previewHeight)
    {
        constexpr double maxWidth = 960.0;
        constexpr double maxHeight = 540.0;

        if (sourceWidth == 0 || sourceHeight == 0)
        {
            previewWidth = 0;
            previewHeight = 0;
            return;
        }

        const double scale =
            std::min(
                {
                    1.0,
                    maxWidth /
                        static_cast<double>(
                            sourceWidth),
                    maxHeight /
                        static_cast<double>(
                            sourceHeight)
                });

        previewWidth =
            std::max<std::uint32_t>(
                1,
                static_cast<std::uint32_t>(
                    std::floor(
                        sourceWidth * scale)));

        previewHeight =
            std::max<std::uint32_t>(
                1,
                static_cast<std::uint32_t>(
                    std::floor(
                        sourceHeight * scale)));
    }

    bool IsRecoverableDuplicationError(
        HRESULT hr) noexcept
    {
        return
            hr == DXGI_ERROR_ACCESS_LOST ||
            hr == DXGI_ERROR_DEVICE_REMOVED ||
            hr == DXGI_ERROR_DEVICE_RESET ||
            hr == DXGI_ERROR_SESSION_DISCONNECTED ||
            hr == DXGI_ERROR_NOT_CURRENTLY_AVAILABLE;
    }

    bool IsH264KeyFrame(
        const std::vector<std::uint8_t>& data)
    {
        for (std::size_t i = 0;
             i + 4 < data.size();
             ++i)
        {
            std::size_t nal = i;

            if (data[i] == 0 &&
                data[i + 1] == 0 &&
                data[i + 2] == 1)
            {
                nal = i + 3;
            }
            else if (
                data[i] == 0 &&
                data[i + 1] == 0 &&
                data[i + 2] == 0 &&
                data[i + 3] == 1)
            {
                nal = i + 4;
            }
            else
            {
                continue;
            }

            if (nal >= data.size())
                continue;

            const std::uint8_t type =
                data[nal] & 0x1F;

            if (type == 5)
                return true;
        }

        return false;
    }

#ifdef MULTIVERSE_NVENC
    HRESULT NvencStatusToHresult(
        NVENCSTATUS status)
    {
        if (status == NV_ENC_SUCCESS)
            return S_OK;

        // HRESULT privado do Multiverse para erros NVENC.
        // O código NVENC continua aparecendo na mensagem.
        return MAKE_HRESULT(
            SEVERITY_ERROR,
            FACILITY_ITF,
            0x500 +
            (static_cast<unsigned int>(
                status) & 0xFF));
    }

    std::wstring NvencStatusText(
        NVENCSTATUS status)
    {
        const wchar_t* name = L"NV_ENC_ERR_UNKNOWN";

        switch (status)
        {
        case NV_ENC_SUCCESS:
            name = L"NV_ENC_SUCCESS";
            break;

        case NV_ENC_ERR_NO_ENCODE_DEVICE:
            name = L"NV_ENC_ERR_NO_ENCODE_DEVICE";
            break;

        case NV_ENC_ERR_UNSUPPORTED_DEVICE:
            name = L"NV_ENC_ERR_UNSUPPORTED_DEVICE";
            break;

        case NV_ENC_ERR_INVALID_ENCODERDEVICE:
            name = L"NV_ENC_ERR_INVALID_ENCODERDEVICE";
            break;

        case NV_ENC_ERR_INVALID_DEVICE:
            name = L"NV_ENC_ERR_INVALID_DEVICE";
            break;

        case NV_ENC_ERR_DEVICE_NOT_EXIST:
            name = L"NV_ENC_ERR_DEVICE_NOT_EXIST";
            break;

        case NV_ENC_ERR_INVALID_PTR:
            name = L"NV_ENC_ERR_INVALID_PTR";
            break;

        case NV_ENC_ERR_INVALID_PARAM:
            name = L"NV_ENC_ERR_INVALID_PARAM";
            break;

        case NV_ENC_ERR_INVALID_CALL:
            name = L"NV_ENC_ERR_INVALID_CALL";
            break;

        case NV_ENC_ERR_OUT_OF_MEMORY:
            name = L"NV_ENC_ERR_OUT_OF_MEMORY";
            break;

        case NV_ENC_ERR_ENCODER_NOT_INITIALIZED:
            name = L"NV_ENC_ERR_ENCODER_NOT_INITIALIZED";
            break;

        case NV_ENC_ERR_UNSUPPORTED_PARAM:
            name = L"NV_ENC_ERR_UNSUPPORTED_PARAM";
            break;

        case NV_ENC_ERR_LOCK_BUSY:
            name = L"NV_ENC_ERR_LOCK_BUSY";
            break;

        case NV_ENC_ERR_NOT_ENOUGH_BUFFER:
            name = L"NV_ENC_ERR_NOT_ENOUGH_BUFFER";
            break;

        case NV_ENC_ERR_INVALID_VERSION:
            name = L"NV_ENC_ERR_INVALID_VERSION";
            break;

        case NV_ENC_ERR_MAP_FAILED:
            name = L"NV_ENC_ERR_MAP_FAILED";
            break;

        case NV_ENC_ERR_NEED_MORE_INPUT:
            name = L"NV_ENC_ERR_NEED_MORE_INPUT";
            break;

        case NV_ENC_ERR_ENCODER_BUSY:
            name = L"NV_ENC_ERR_ENCODER_BUSY";
            break;

        case NV_ENC_ERR_GENERIC:
            name = L"NV_ENC_ERR_GENERIC";
            break;

        case NV_ENC_ERR_RESOURCE_REGISTER_FAILED:
            name = L"NV_ENC_ERR_RESOURCE_REGISTER_FAILED";
            break;

        case NV_ENC_ERR_RESOURCE_NOT_REGISTERED:
            name = L"NV_ENC_ERR_RESOURCE_NOT_REGISTERED";
            break;

        case NV_ENC_ERR_RESOURCE_NOT_MAPPED:
            name = L"NV_ENC_ERR_RESOURCE_NOT_MAPPED";
            break;

        default:
            break;
        }

        std::wstring result(name);
        result += L" (";
        result += std::to_wstring(
            static_cast<int>(status));
        result += L")";

        if (status == NV_ENC_ERR_INVALID_VERSION)
        {
            result +=
                L". O SDK 13.1 exige driver NVIDIA compatível "
                L"(linha 610 ou superior).";
        }

        return result;
    }


    static constexpr std::uint32_t LAB_MODE_AUTO = 0;
    static constexpr std::uint32_t LAB_MODE_NV12 = 1;
    static constexpr std::uint32_t LAB_MODE_ARGB = 2;
    static constexpr std::uint32_t LAB_MODE_SYNTHETIC = 3;
    // Internal NVENC mode used only by CORE 11.13. The shared-ring texture
    // is already stream-sized RGB and is registered directly with NVENC.
    static constexpr std::uint32_t LAB_MODE_EXTERNAL_ARGB = 4;
    // CORE 11.14 internal NVENC path: native-size shared BGRA enters the
    // isolated EncodeDevice, is scaled by a lightweight RGB shader into the
    // private per-slot NVENC texture, then encoded as ARGB. No VideoProcessor.
    static constexpr std::uint32_t LAB_MODE_ARGB_SCALE = 5;
    static constexpr std::uint32_t CORE10_MODE_ISOLATED_BGRA = 4;
    static constexpr std::uint32_t CORE10_MODE_WGC_BGRA = 5;
    static constexpr std::uint32_t CORE11_MODE_WGC_NV12 = 6;
    static constexpr std::uint32_t CORE11_MODE_WGC_DIRECT_RGB = 7;
    static constexpr std::uint32_t CORE11_MODE_WGC_RGB_ENCODE_SCALE = 8;
    // CORE 11.15: native shared ring -> native private ARGB NVENC copy.
    static constexpr std::uint32_t CORE11_MODE_WGC_NATIVE_RGB = 9;
    // CORE 11.17 recommended path: WGC and NVENC share the same D3D11
    // device. FrameArrived only publishes the latest WinRT frame; a dedicated
    // delivery worker performs the single CopyResource into the local ring.
    // NVENC reads that RGB texture directly. No cross-device ring/fence/Flush.
    static constexpr std::uint32_t CORE11_MODE_WGC_SAME_DEVICE_DIRECT_RGB = 10;

    static constexpr bool IsCore10IsolatedMode(std::uint32_t mode) noexcept
    {
        return mode == CORE10_MODE_ISOLATED_BGRA ||
            mode == CORE10_MODE_WGC_BGRA ||
            mode == CORE11_MODE_WGC_NV12 ||
            mode == CORE11_MODE_WGC_DIRECT_RGB ||
            mode == CORE11_MODE_WGC_RGB_ENCODE_SCALE ||
            mode == CORE11_MODE_WGC_NATIVE_RGB;
    }

    class AsyncNvencD3D11
    {
    public:
        static constexpr std::size_t SlotCount = 4;

        AsyncNvencD3D11() = default;

        ~AsyncNvencD3D11()
        {
            Stop();
        }

        AsyncNvencD3D11(
            const AsyncNvencD3D11&) = delete;

        AsyncNvencD3D11& operator=(
            const AsyncNvencD3D11&) = delete;

        const std::wstring& LastStage() const noexcept
        {
            return m_lastStage;
        }

        NVENCSTATUS Start(
            ID3D11Device* device,
            std::uint32_t sourceWidth,
            std::uint32_t sourceHeight,
            std::uint32_t width,
            std::uint32_t height,
            std::uint32_t fps,
            std::uint32_t bitrateKbps,
            std::uint32_t preset,
            bool ultraLowLatency,
            std::uint32_t labInputMode,
            std::shared_ptr<SharedEncodedPacketQueue> packetQueue)
        {
            Stop();

            if (!device ||
                sourceWidth == 0 ||
                sourceHeight == 0 ||
                width == 0 ||
                height == 0)
            {
                return NV_ENC_ERR_INVALID_PTR;
            }

            m_sharedPacketQueue = std::move(packetQueue);
            if (!m_sharedPacketQueue)
            {
                m_sharedPacketQueue =
                    std::make_shared<SharedEncodedPacketQueue>();
            }
            m_sharedPacketQueue->Clear(true);
            m_sharedPacketQueue->ResetCounters();

            m_requestedInputMode =
                labInputMode <= LAB_MODE_ARGB_SCALE
                    ? labInputMode
                    : LAB_MODE_AUTO;
            m_useExternalArgbInput =
                m_requestedInputMode == LAB_MODE_EXTERNAL_ARGB;
            m_useArgbScalePreprocess =
                m_requestedInputMode == LAB_MODE_ARGB_SCALE;

            // Every Start is a new diagnostic epoch. Core 09.3 used to keep
            // lifetime counters across encoder restarts, which made a fresh
            // test difficult to interpret after one failure/recovery.
            m_nextSlot.store(0);
            m_lastStatus.store(S_OK);
            m_submittedFrames.store(0);
            m_encodedFrames.store(0);
            m_encodedBytes.store(0);
            m_pacingDrops.store(0);
            m_needMoreInputAccepted.store(0);
            m_encoderBusyRetries.store(0);
            m_noFreeSlotDrops.store(0);
            m_flushTimeouts.store(0);
            m_queueResets.store(0);
            {
                std::lock_guard<std::mutex> pendingLock(m_pendingOutputMutex);
                m_pendingOutputSlots.clear();
            }
            m_nextSubmitSequence = 0;
            m_retrySlotIndex = SlotCount;
            m_retryNotBefore = {};
            m_lastSubmitUs.store(0);
            m_totalSubmitUs.store(0);
            m_lastOutputLatencyUs.store(0);
            m_totalOutputLatencyUs.store(0);
            m_lastSubmitProgressUs.store(0);
            m_lastOutputProgressUs.store(0);
            m_outputWorkerAlive.store(false);
            m_lastPreprocessUs.store(0);
            m_totalPreprocessUs.store(0);
            m_preprocessSamples.store(0);
            m_lastMapUs.store(0);
            m_totalMapUs.store(0);
            m_mapSamples.store(0);
            m_lastEncodeCallUs.store(0);
            m_totalEncodeCallUs.store(0);
            m_encodeCallSamples.store(0);
            m_lastLockBitstreamUs.store(0);
            m_totalLockBitstreamUs.store(0);
            m_lockBitstreamSamples.store(0);
            m_mapFailures.store(0);
            m_encodeErrors.store(0);
            m_lockBusyRetries.store(0);
            m_inputPathDiagnostic.clear();

            m_lastStage =
                L"LoadLibrary(nvEncodeAPI64.dll)";

            m_module =
                LoadLibraryW(
                    L"nvEncodeAPI64.dll");

            if (!m_module)
                return NV_ENC_ERR_NO_ENCODE_DEVICE;

            using CreateInstanceFn =
                NVENCSTATUS (NVENCAPI*)(
                    NV_ENCODE_API_FUNCTION_LIST*);

            auto createInstance =
                reinterpret_cast<CreateInstanceFn>(
                    GetProcAddress(
                        m_module,
                        "NvEncodeAPICreateInstance"));

            if (!createInstance)
            {
                Stop();
                return NV_ENC_ERR_NO_ENCODE_DEVICE;
            }

            m_api = {};
            m_api.version =
                NV_ENCODE_API_FUNCTION_LIST_VER;

            m_lastStage =
                L"NvEncodeAPICreateInstance";

            NVENCSTATUS status =
                createInstance(&m_api);

            if (status != NV_ENC_SUCCESS)
            {
                Stop();
                return status;
            }

            NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS
                openParams{};

            openParams.version =
                NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER;

            openParams.deviceType =
                NV_ENC_DEVICE_TYPE_DIRECTX;

            openParams.device = device;
            openParams.apiVersion =
                NVENCAPI_VERSION;

            m_lastStage =
                L"nvEncOpenEncodeSessionEx";

            status =
                m_api.nvEncOpenEncodeSessionEx(
                    &openParams,
                    &m_encoder);

            if (status != NV_ENC_SUCCESS)
            {
                Stop();
                return status;
            }

            // CORE 11.12 diagnostic only: non-blocking fence markers are
            // enabled only for the WGC->NV12 laboratory path. Failure leaves
            // the probe disabled and never changes the encode path.
            if (m_requestedInputMode == LAB_MODE_NV12)
                (void)InitializePreprocessGpuProbe(device);

            const std::uint32_t effectivePreset =
                preset == 0
                    ? (fps >= 120 ? 1u : 2u)
                    : std::clamp<std::uint32_t>(
                        preset,
                        1,
                        4);

            GUID presetGuid =
                NV_ENC_PRESET_P2_GUID;

            switch (effectivePreset)
            {
            case 1:
                presetGuid =
                    NV_ENC_PRESET_P1_GUID;
                break;

            case 3:
                presetGuid =
                    NV_ENC_PRESET_P3_GUID;
                break;

            case 4:
                presetGuid =
                    NV_ENC_PRESET_P4_GUID;
                break;

            default:
                presetGuid =
                    NV_ENC_PRESET_P2_GUID;
                break;
            }

            const NV_ENC_TUNING_INFO
                tuningInfo =
                    ultraLowLatency
                        ? NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY
                        : NV_ENC_TUNING_INFO_LOW_LATENCY;

            m_presetGuid =
                presetGuid;

            m_tuningInfo =
                tuningInfo;

            NV_ENC_PRESET_CONFIG
                presetConfig{};

            presetConfig.version =
                NV_ENC_PRESET_CONFIG_VER;

            presetConfig.presetCfg.version =
                NV_ENC_CONFIG_VER;

            m_lastStage =
                L"nvEncGetEncodePresetConfigEx";

            status =
                m_api.nvEncGetEncodePresetConfigEx(
                    m_encoder,
                    NV_ENC_CODEC_H264_GUID,
                    presetGuid,
                    tuningInfo,
                    &presetConfig);

            if (status != NV_ENC_SUCCESS)
            {
                Stop();
                return status;
            }

            m_config =
                presetConfig.presetCfg;

            m_config.version =
                NV_ENC_CONFIG_VER;

            m_config.profileGUID =
                NV_ENC_H264_PROFILE_HIGH_GUID;

            // Uma IDR periódica limita o tempo de recuperação mesmo se um
            // pedido de IDR se perder/atrasar no caminho de controle.
            // Fallback periódico de 2 s. Recuperações reais continuam pedindo
            // IDR imediatamente pelo canal de controle; reduzir IDR periódica
            // evita rajadas grandes todo segundo em cenas complexas.
            const std::uint32_t recoveryGop =
                std::max<std::uint32_t>(
                    60u,
                    fps * 2u);

            m_config.gopLength =
                recoveryGop;

            // Zero B-frames / zero display reordering.
            m_config.frameIntervalP = 1;

            auto& rc =
                m_config.rcParams;

            rc.rateControlMode =
                NV_ENC_PARAMS_RC_CBR;

            const std::uint32_t
                bitrateBps =
                    bitrateKbps * 1000u;

            rc.averageBitRate =
                bitrateBps;

            rc.maxBitRate =
                bitrateBps;

            // Buffer de aproximadamente 1 frame:
            // baixa latência e resposta rápida ao bitrate alvo.
            rc.vbvBufferSize =
                std::max<std::uint32_t>(
                    1,
                    bitrateBps /
                    std::max<std::uint32_t>(
                        fps,
                        1));

            rc.vbvInitialDelay =
                rc.vbvBufferSize;

            rc.enableLookahead = 0;
            rc.lookaheadDepth = 0;
            rc.zeroReorderDelay = 1;

            // Automatic non-reference P frames reduce error propagation on
            // unreliable interactive links. Losing one of these frames does
            // not poison every subsequent reference until the next IDR.
            rc.enableNonRefP = 1;

            rc.enableTemporalAQ = 0;
            rc.multiPass =
                NV_ENC_MULTI_PASS_DISABLED;

            // Streaming interativo prioriza estabilidade sob 95-100% de
            // carga 3D. AQ usa CUDA internamente no NVENC; mantenha-o OFF.
            rc.enableAQ = 0;
            rc.aqStrength = 0;

            auto& h264 =
                m_config.encodeCodecConfig.h264Config;

            h264.idrPeriod =
                recoveryGop;

            h264.repeatSPSPPS = 1;

            // Evita SEIs/AUDs que não ajudam o nosso stream interativo.

            NV_ENC_INITIALIZE_PARAMS
                init{};

            init.version =
                NV_ENC_INITIALIZE_PARAMS_VER;

            init.encodeGUID =
                NV_ENC_CODEC_H264_GUID;

            init.presetGUID =
                presetGuid;

            init.encodeWidth = width;
            init.encodeHeight = height;

            init.darWidth = width;
            init.darHeight = height;

            init.frameRateNum =
                std::max<std::uint32_t>(
                    1,
                    fps);

            init.frameRateDen = 1;
            init.enablePTD = 1;

            // NVIDIA recomenda async para DXGI Desktop Duplication +
            // DirectX, deixando LockBitstream fora da thread de captura.
            init.enableEncodeAsync = 1;
            init.enableOutputInVidmem = 0;

            init.maxEncodeWidth = width;
            init.maxEncodeHeight = height;

            init.tuningInfo =
                tuningInfo;

            init.encodeConfig =
                &m_config;

            m_lastStage =
                L"nvEncInitializeEncoder";

            status =
                m_api.nvEncInitializeEncoder(
                    m_encoder,
                    &init);

            if (status != NV_ENC_SUCCESS)
            {
                Stop();
                return status;
            }

            m_sourceWidth = sourceWidth;
            m_sourceHeight = sourceHeight;
            m_width = width;
            m_height = height;
            m_fps = fps;
            m_bitrateKbps = bitrateKbps;
            m_effectivePreset = effectivePreset;

            // Core 09.3.6 LAB A/B/C: the requested path is explicit so an
            // A/B measurement cannot silently fall back and hide the cause.
            // Mode 0 preserves the normal auto behavior for compatibility.
            m_inputPathDiagnostic.clear();
            m_useNv12Preprocess = false;
            m_useArgbScalePreprocess = false;

            const bool ForceNv12 =
                m_requestedInputMode == LAB_MODE_NV12;
            const bool ForceArgb =
                m_requestedInputMode == LAB_MODE_ARGB ||
                m_requestedInputMode == LAB_MODE_SYNTHETIC;
            const bool ForceExternalArgb =
                m_requestedInputMode == LAB_MODE_EXTERNAL_ARGB;
            const bool ForceArgbScale =
                m_requestedInputMode == LAB_MODE_ARGB_SCALE;
            const bool allowAutomaticFallback =
                m_requestedInputMode == LAB_MODE_AUTO;

            if (!ForceArgb && !ForceExternalArgb && !ForceArgbScale)
            {
                const bool nv12Advertised =
                    EncoderSupportsInputFormat(
                        NV_ENC_BUFFER_FORMAT_NV12);

                if (nv12Advertised)
                {
                    const HRESULT preprocessHr =
                        InitializeNv12Preprocessor(device);

                    if (SUCCEEDED(preprocessHr))
                    {
                        m_useNv12Preprocess = true;
                        const NVENCSTATUS slotStatus =
                            CreateAllSlots(device);

                        if (slotStatus == NV_ENC_SUCCESS)
                        {
                            m_inputPathDiagnostic =
                                ForceNv12
                                    ? L"LAB A: NV12/D3D11 VideoProcessor forçado"
                                    : L"AUTO: NV12/D3D11 VideoProcessor validado";
                        }
                        else
                        {
                            const std::wstring failureStage = m_lastStage;
                            ReleaseSlotsForStartupFallback();
                            ResetVideoPreprocessor();
                            m_useNv12Preprocess = false;

                            if (ForceNv12)
                            {
                                m_inputPathDiagnostic =
                                    L"LAB A falhou em [" + failureStage + L"]: " +
                                    NvencStatusText(slotStatus);
                                Stop();
                                return slotStatus;
                            }
                        }
                    }
                    else
                    {
                        ResetVideoPreprocessor();
                        if (ForceNv12)
                        {
                            m_inputPathDiagnostic =
                                L"LAB A: VideoProcessor NV12 indisponível";
                            Stop();
                            return NV_ENC_ERR_UNSUPPORTED_PARAM;
                        }
                    }
                }
                else if (ForceNv12)
                {
                    m_inputPathDiagnostic =
                        L"LAB A: NVENC não anunciou NV12";
                    Stop();
                    return NV_ENC_ERR_UNSUPPORTED_PARAM;
                }
            }

            if (!m_useNv12Preprocess)
            {
                if (!ForceArgb && !ForceExternalArgb && !ForceArgbScale && !allowAutomaticFallback)
                {
                    Stop();
                    return NV_ENC_ERR_UNSUPPORTED_PARAM;
                }

                if ((ForceExternalArgb || ForceArgbScale) &&
                    !EncoderSupportsInputFormat(NV_ENC_BUFFER_FORMAT_ARGB))
                {
                    m_inputPathDiagnostic = ForceArgbScale
                        ? L"CORE 11.14: NVENC não anunciou ARGB"
                        : L"CORE 11.13: NVENC não anunciou ARGB";
                    Stop();
                    return NV_ENC_ERR_UNSUPPORTED_PARAM;
                }

                if (ForceArgbScale)
                {
                    const HRESULT scaleHr = InitializeArgbScaler(device);
                    if (FAILED(scaleHr))
                    {
                        m_inputPathDiagnostic = L"CORE 11.14: falha ao inicializar RGB scaler no EncodeDevice";
                        Stop();
                        return NV_ENC_ERR_UNSUPPORTED_PARAM;
                    }
                    m_useArgbScalePreprocess = true;
                }

                status = CreateAllSlots(device);
                if (status != NV_ENC_SUCCESS)
                {
                    Stop();
                    return status;
                }

                if (m_requestedInputMode == LAB_MODE_ARGB_SCALE)
                    m_inputPathDiagnostic = L"CORE 11.14: shared-ring BGRA nativo -> RGB shader scaler no EncodeDevice -> NVENC ARGB";
                else if (m_requestedInputMode == LAB_MODE_EXTERNAL_ARGB)
                    m_inputPathDiagnostic = L"CORE 11.13: shared-ring RGB registrado direto no NVENC; zero preprocess no EncodeDevice";
                else if (m_requestedInputMode == LAB_MODE_ARGB)
                    m_inputPathDiagnostic = L"LAB B: ARGB DirectX direto; VideoProcessor bypass";
                else if (m_requestedInputMode == LAB_MODE_SYNTHETIC)
                    m_inputPathDiagnostic = L"LAB C: ARGB sintético; captura + VideoProcessor bypass";
                else
                    m_inputPathDiagnostic = L"AUTO: ARGB fallback";
            }

            status = CreateEosCompletionEvent();
            if (status != NV_ENC_SUCCESS)
            {
                Stop();
                return status;
            }

            // CORE 11 follows NVIDIA's WDDM/DirectX async threading model:
            // the encoder worker only submits NvEncEncodePicture, while a
            // dedicated secondary thread waits on completion events and locks
            // the output bitstream. Capture and encode use different D3D11
            // devices, removing the old same-device lock hazard.
            m_outputFatalStatus.store(NV_ENC_SUCCESS);
            m_outputStop.store(false);
            m_running.store(true);
            m_forceIdr.store(true);
            try
            {
                m_outputWorker = std::thread(
                    &AsyncNvencD3D11::OutputLoop,
                    this);
            }
            catch (...)
            {
                m_running.store(false);
                m_outputStop.store(true);
                Stop();
                return NV_ENC_ERR_OUT_OF_MEMORY;
            }

            m_lastStage =
                L"CORE 11 NVENC async; output worker por completion event";

            return NV_ENC_SUCCESS;
        }

        NVENCSTATUS ReconfigureBitrate(
            std::uint32_t bitrateKbps)
        {
            if (!m_encoder ||
                !m_running.load())
            {
                return NV_ENC_ERR_ENCODER_NOT_INITIALIZED;
            }

            bitrateKbps =
                std::clamp<std::uint32_t>(
                    bitrateKbps,
                    2000u,
                    200000u);

            if (bitrateKbps ==
                m_bitrateKbps)
            {
                return NV_ENC_SUCCESS;
            }

            if (!m_api.nvEncReconfigureEncoder)
            {
                return NV_ENC_ERR_UNSUPPORTED_PARAM;
            }

            const std::uint32_t bitrateBps =
                bitrateKbps * 1000u;

            auto& rc =
                m_config.rcParams;

            rc.averageBitRate =
                bitrateBps;

            rc.maxBitRate =
                bitrateBps;

            rc.vbvBufferSize =
                std::max<std::uint32_t>(
                    1u,
                    bitrateBps /
                    std::max<std::uint32_t>(
                        1u,
                        m_fps));

            rc.vbvInitialDelay =
                rc.vbvBufferSize;

            NV_ENC_INITIALIZE_PARAMS init{};
            init.version =
                NV_ENC_INITIALIZE_PARAMS_VER;
            init.encodeGUID =
                NV_ENC_CODEC_H264_GUID;
            init.presetGUID =
                m_presetGuid;
            init.encodeWidth =
                m_width;
            init.encodeHeight =
                m_height;
            init.darWidth =
                m_width;
            init.darHeight =
                m_height;
            init.frameRateNum =
                std::max<std::uint32_t>(
                    1u,
                    m_fps);
            init.frameRateDen = 1;
            init.enablePTD = 1;
            init.enableEncodeAsync = 1;
            init.enableOutputInVidmem = 0;
            init.maxEncodeWidth =
                m_width;
            init.maxEncodeHeight =
                m_height;
            init.tuningInfo =
                m_tuningInfo;
            init.encodeConfig =
                &m_config;

            NV_ENC_RECONFIGURE_PARAMS params{};
            params.version =
                NV_ENC_RECONFIGURE_PARAMS_VER;
            params.reInitEncodeParams =
                init;
            params.resetEncoder = 0;
            params.forceIDR = 0;

            m_lastStage =
                L"nvEncReconfigureEncoder(bitrate)";

            const NVENCSTATUS status =
                m_api.nvEncReconfigureEncoder(
                    m_encoder,
                    &params);

            if (status ==
                NV_ENC_SUCCESS)
            {
                m_bitrateKbps =
                    bitrateKbps;
                m_lastStatus.store(S_OK);
            }
            else
            {
                m_lastStatus.store(
                    NvencStatusToHresult(
                        status));
            }

            return status;
        }

        void Stop(
            const std::function<void(std::uint32_t)>& reportStage = {})
        {
            auto report =
                [&reportStage](std::uint32_t stage)
                {
                    if (reportStage)
                        reportStage(stage);
                };

            report(1); // stop entered / submissions disabled
            // Stop accepting new submissions, but keep the output worker alive
            // while EOS/pending bitstreams drain. Destroying the worker before
            // the flush was a lifecycle inversion in CORE 10.x.
            m_running.store(false);

            if (m_encoder &&
                static_cast<NVENCSTATUS>(m_outputFatalStatus.load()) ==
                    NV_ENC_SUCCESS)
            {
                report(2); // graceful flush begin
                (void)FlushPendingOutputs(std::chrono::milliseconds(750));
                report(3); // graceful flush returned
            }

            // A fatal completion/lock error means the normal drain path is no
            // longer trustworthy. Skip the 750 ms graceful wait and rebuild
            // immediately; slot/resource cleanup below is the bounded fallback.
            report(4); // output stop signaled
            m_outputStop.store(true);
            m_pendingOutputCv.notify_all();

            if (m_outputWorker.joinable() &&
                m_outputWorker.get_id() != std::this_thread::get_id())
            {
                report(5); // output worker join
                m_outputWorker.join();
            }
            report(6); // output worker joined

            report(7); // resource teardown
            if (m_encoder)
            {
                if (m_eosEvent && m_api.nvEncUnregisterAsyncEvent)
                {
                    NV_ENC_EVENT_PARAMS eosParams{};
                    eosParams.version = NV_ENC_EVENT_PARAMS_VER;
                    eosParams.completionEvent = m_eosEvent;
                    (void)m_api.nvEncUnregisterAsyncEvent(
                        m_encoder,
                        &eosParams);
                }

                if (m_eosEvent)
                {
                    CloseHandle(m_eosEvent);
                    m_eosEvent = nullptr;
                }

                // Hard cleanup is only the bounded-stop fallback. It never
                // participates in the normal running lifecycle.
                for (auto& slot : m_slots)
                {
                    if (slot.MappedInput &&
                        m_api.nvEncUnmapInputResource)
                    {
                        (void)m_api.nvEncUnmapInputResource(
                            m_encoder,
                            slot.MappedInput);
                        slot.MappedInput = nullptr;
                    }

                    CompleteInputOwnership(slot);

                    if (slot.Event &&
                        m_api.nvEncUnregisterAsyncEvent)
                    {
                        NV_ENC_EVENT_PARAMS eventParams{};
                        eventParams.version = NV_ENC_EVENT_PARAMS_VER;
                        eventParams.completionEvent = slot.Event;
                        (void)m_api.nvEncUnregisterAsyncEvent(
                            m_encoder,
                            &eventParams);
                    }

                    if (slot.Bitstream &&
                        m_api.nvEncDestroyBitstreamBuffer)
                    {
                        (void)m_api.nvEncDestroyBitstreamBuffer(
                            m_encoder,
                            slot.Bitstream);
                        slot.Bitstream = nullptr;
                    }

                    if (slot.RegisteredInput &&
                        m_api.nvEncUnregisterResource)
                    {
                        (void)m_api.nvEncUnregisterResource(
                            m_encoder,
                            slot.RegisteredInput);
                        slot.RegisteredInput = nullptr;
                    }

                    if (slot.Event)
                    {
                        CloseHandle(slot.Event);
                        slot.Event = nullptr;
                    }

                    slot.ScaleOutputView.Reset();
                    slot.VideoOutputView.Reset();
                    slot.VideoInputView.Reset();
                    slot.VideoInputTexture.Reset();
                    slot.Texture.Reset();
                    slot.MappedBufferFormat = NV_ENC_BUFFER_FORMAT_UNDEFINED;
                    slot.FrameId = 0;
                    slot.SubmitSequence = 0;
                    slot.AcceptedStatus = NV_ENC_SUCCESS;
                    slot.ForceIdr = false;
                    slot.SubmitCpuUs = 0;
                    slot.SubmitTime = {};
                    slot.State.store(SlotState::Free);
                }

                if (m_api.nvEncUnregisterResource)
                {
                    for (auto& external : m_externalInputs)
                    {
                        if (external.Registered)
                        {
                            (void)m_api.nvEncUnregisterResource(
                                m_encoder,
                                external.Registered);
                            external.Registered = nullptr;
                        }
                        external.Texture.Reset();
                    }
                }
                m_externalInputs.clear();

                if (m_api.nvEncDestroyEncoder)
                    (void)m_api.nvEncDestroyEncoder(m_encoder);

                m_encoder = nullptr;
            }
            else if (m_eosEvent)
            {
                CloseHandle(m_eosEvent);
                m_eosEvent = nullptr;
            }

            {
                std::lock_guard<std::mutex> pendingLock(m_pendingOutputMutex);
                m_pendingOutputSlots.clear();
            }
            m_retrySlotIndex = SlotCount;
            m_retryNotBefore = {};

            if (m_sharedPacketQueue)
                m_sharedPacketQueue->Clear(true);

            m_api = {};

            if (m_module)
            {
                FreeLibrary(m_module);
                m_module = nullptr;
            }

            m_videoProcessor.Reset();
            m_videoEnumerator.Reset();
            m_videoContext.Reset();
            m_videoDevice.Reset();
            ResetPreprocessGpuProbe();
            m_useNv12Preprocess = false;
            m_useExternalArgbInput = false;
            m_useArgbScalePreprocess = false;
            m_externalInputs.clear();
            m_argbScaleSourceViews.clear();
            m_argbScaleSampler.Reset();
            m_argbScalePs.Reset();
            m_argbScaleVs.Reset();

            m_sourceWidth = 0;
            m_sourceHeight = 0;
            m_width = 0;
            m_height = 0;
            m_fps = 0;
            m_bitrateKbps = 0;
            report(8); // complete
        }

        bool IsRunning() const noexcept
        {
            return
                m_running.load() &&
                m_encoder != nullptr &&
                static_cast<NVENCSTATUS>(m_outputFatalStatus.load()) ==
                    NV_ENC_SUCCESS;
        }

        NVENCSTATUS Pump()
        {
            if (!m_encoder)
                return NV_ENC_ERR_ENCODER_NOT_INITIALIZED;

            const NVENCSTATUS outputFatal =
                static_cast<NVENCSTATUS>(m_outputFatalStatus.load());
            if (outputFatal != NV_ENC_SUCCESS)
                return outputFatal;

            if (m_retrySlotIndex < SlotCount)
            {
                const NVENCSTATUS retryStatus = RetryBusySubmission();
                if (retryStatus != NV_ENC_SUCCESS &&
                    retryStatus != NV_ENC_ERR_NEED_MORE_INPUT &&
                    retryStatus != NV_ENC_ERR_ENCODER_BUSY)
                {
                    return retryStatus;
                }
                return retryStatus == NV_ENC_ERR_ENCODER_BUSY
                    ? NV_ENC_SUCCESS
                    : retryStatus;
            }

            return NV_ENC_SUCCESS;
        }

        NVENCSTATUS Submit(
            ID3D11DeviceContext* context,
            ID3D11Texture2D* source,
            std::uint64_t frameId,
            bool forceIdr)
        {
            if (!IsRunning() ||
                !context ||
                !source)
            {
                return NV_ENC_ERR_ENCODER_NOT_INITIALIZED;
            }

            // CORE 11.12: sample previous GPU marker completion without
            // waiting. This reveals queue backlog while preserving behavior.
            RefreshPreprocessGpuProbes();

            // Always make output/retry progress before accepting another
            // source frame. A prepared ENCODER_BUSY slot owns the next retry.
            const NVENCSTATUS pumpStatus = Pump();
            if (pumpStatus != NV_ENC_SUCCESS &&
                pumpStatus != NV_ENC_ERR_LOCK_BUSY)
            {
                return pumpStatus;
            }

            if (m_retrySlotIndex < SlotCount)
                return NV_ENC_ERR_ENCODER_BUSY;

            D3D11_TEXTURE2D_DESC sourceDesc{};
            source->GetDesc(&sourceDesc);

            const std::uint32_t expectedSourceWidth =
                (m_useNv12Preprocess || m_useArgbScalePreprocess)
                    ? m_sourceWidth
                    : m_width;
            const std::uint32_t expectedSourceHeight =
                (m_useNv12Preprocess || m_useArgbScalePreprocess)
                    ? m_sourceHeight
                    : m_height;

            if (sourceDesc.Width != expectedSourceWidth ||
                sourceDesc.Height != expectedSourceHeight ||
                sourceDesc.Format != DXGI_FORMAT_B8G8R8A8_UNORM)
            {
                return NV_ENC_ERR_INVALID_PARAM;
            }

            Slot* slot = nullptr;
            std::size_t slotIndex = 0;

            const std::size_t baseSlot =
                m_nextSlot.fetch_add(1) % SlotCount;

            for (std::size_t offset = 0;
                 offset < SlotCount;
                 ++offset)
            {
                const std::size_t index =
                    (baseSlot + offset) % SlotCount;

                SlotState expected = SlotState::Free;
                if (m_slots[index].State.compare_exchange_strong(
                        expected,
                        SlotState::PreparedForSubmit))
                {
                    slot = &m_slots[index];
                    slotIndex = index;
                    break;
                }
            }

            if (!slot)
            {
                m_noFreeSlotDrops.fetch_add(1);
                return NV_ENC_ERR_ENCODER_BUSY;
            }

            ResetEvent(slot->Event);
            slot->FrameId = frameId;
            slot->ForceIdr = forceIdr;
            slot->SubmitTime = std::chrono::steady_clock::now();
            slot->SubmitCpuUs = 0;
            slot->AcceptedStatus = NV_ENC_SUCCESS;
            slot->SubmitSequence = 0;
            slot->MappedBufferFormat = NV_ENC_BUFFER_FORMAT_UNDEFINED;

            const auto submitStart =
                std::chrono::steady_clock::now();

            const auto preprocessStart =
                std::chrono::steady_clock::now();

            NVENCSTATUS preprocessStatus = NV_ENC_SUCCESS;

            if (m_useNv12Preprocess)
            {
                preprocessStatus =
                    ConvertBgraToNv12(context, source, *slot);
            }
            else if (m_useArgbScalePreprocess)
            {
                preprocessStatus =
                    ScaleBgraToArgb(context, source, *slot);
            }
            else
            {
                context->CopyResource(slot->Texture.Get(), source);
                SignalPreprocessCopyProbe();
            }

            const auto preprocessEnd =
                std::chrono::steady_clock::now();
            const auto preprocessUs =
                std::chrono::duration_cast<std::chrono::microseconds>(
                    preprocessEnd - preprocessStart).count();

            m_lastPreprocessUs.store(preprocessUs);
            m_totalPreprocessUs.fetch_add(
                static_cast<std::uint64_t>(
                    std::max<long long>(0, preprocessUs)));
            m_preprocessSamples.fetch_add(1);

            if (preprocessStatus != NV_ENC_SUCCESS)
            {
                m_encodeErrors.fetch_add(1);
                m_lastStatus.store(NvencStatusToHresult(preprocessStatus));
                slot->State.store(SlotState::Free);
                return preprocessStatus;
            }

            NV_ENC_MAP_INPUT_RESOURCE mapInput{};
            mapInput.version = NV_ENC_MAP_INPUT_RESOURCE_VER;
            mapInput.registeredResource = slot->RegisteredInput;

            const auto mapStart = std::chrono::steady_clock::now();
            NVENCSTATUS status =
                m_api.nvEncMapInputResource(m_encoder, &mapInput);
            const auto mapEnd = std::chrono::steady_clock::now();
            const auto mapUs =
                std::chrono::duration_cast<std::chrono::microseconds>(
                    mapEnd - mapStart).count();

            m_lastMapUs.store(mapUs);
            m_totalMapUs.fetch_add(
                static_cast<std::uint64_t>(std::max<long long>(0, mapUs)));
            m_mapSamples.fetch_add(1);

            if (status != NV_ENC_SUCCESS)
            {
                m_mapFailures.fetch_add(1);
                m_lastStatus.store(NvencStatusToHresult(status));
                slot->State.store(SlotState::Free);
                return status;
            }

            slot->MappedInput = mapInput.mappedResource;
            slot->MappedBufferFormat =
                mapInput.mappedBufferFmt != NV_ENC_BUFFER_FORMAT_UNDEFINED
                    ? mapInput.mappedBufferFmt
                    : (m_useNv12Preprocess
                        ? NV_ENC_BUFFER_FORMAT_NV12
                        : NV_ENC_BUFFER_FORMAT_ARGB);

            const auto preparedAt = std::chrono::steady_clock::now();
            slot->SubmitCpuUs =
                std::chrono::duration_cast<std::chrono::microseconds>(
                    preparedAt - submitStart).count();

            return SubmitPreparedSlot(slotIndex);
        }

        void RequestIdr()
        {
            m_forceIdr.store(true);
        }

        bool ShouldDeferNewFrame()
        {
            if (!m_sharedPacketQueue)
                return false;
            // A prepared ENCODER_BUSY input may be the only in-flight frame.
            // Pump its retry before deciding to defer another source, otherwise
            // recovery could wait forever for a retry that nobody performs.
            // Never hold the packet-queue mutex while calling NVENC.
            const NVENCSTATUS progress = Pump();
            if (progress != NV_ENC_SUCCESS &&
                progress != NV_ENC_ERR_NEED_MORE_INPUT &&
                progress != NV_ENC_ERR_ENCODER_BUSY &&
                progress != NV_ENC_ERR_LOCK_BUSY)
                return false; // Let Submit surface the real encoder failure.
            // Observe queue occupancy AFTER the retry has made progress.
            // An earlier snapshot could defer a frame unnecessarily.
            const auto queue = m_sharedPacketQueue->GetState();
            if (!queue.Active)
                return false;
            std::uint32_t inFlight = 0;
            for (const auto& slot : m_slots)
                if (slot.State.load(std::memory_order_acquire) != SlotState::Free)
                    ++inFlight;
            if (!m_queueAdmissionPolicy.ShouldDefer(queue, inFlight, static_cast<std::uint32_t>(SlotCount)))
                return false;
            // This is an input admission drop, not a coded-reference drop.
            // The output worker continues to drain independently.
            m_pacingDrops.fetch_add(1, std::memory_order_relaxed);
            return true;
        }

        void RestoreForceIdr() noexcept
        {
            m_forceIdr.store(true, std::memory_order_release);
        }

        bool ConsumeForceIdr()
        {
            const bool localRequest =
                m_forceIdr.exchange(false);

            const bool queueRequest =
                m_sharedPacketQueue
                    ? m_sharedPacketQueue->ConsumeIdrRequest()
                    : false;

            return localRequest || queueRequest;
        }

        void SetPacketConsumerActive(
            bool active)
        {
            if (m_sharedPacketQueue)
                m_sharedPacketQueue->SetActive(active);

            if (active)
                m_forceIdr.store(true);
        }

        HRESULT GetStats(
            MV_EncoderStats* stats)
        {
            if (!stats)
                return E_INVALIDARG;

            *stats = {};

            stats->Running =
                IsRunning()
                    ? 1
                    : 0;

            stats->LastStatus =
                m_lastStatus.load();

            stats->Width =
                m_width;

            stats->Height =
                m_height;

            stats->TargetFps =
                m_fps;

            stats->BitrateKbps =
                m_bitrateKbps;

            stats->EncodedFrames =
                m_encodedFrames.load();

            stats->EncodedBytes =
                m_encodedBytes.load();

            stats->PacingDrops =
                m_pacingDrops.load();

            stats->QueueResets =
                m_sharedPacketQueue
                    ? m_sharedPacketQueue->ResetCount()
                    : 0;

            stats->SubmittedFrames =
                m_submittedFrames.load();

            // Compatibility alias: only a genuinely unavailable input
            // slot is a backpressure drop in API v15.
            stats->BackpressureDrops =
                m_noFreeSlotDrops.load();

            stats->InputSlots =
                static_cast<std::uint32_t>(
                    SlotCount);

            std::uint32_t inFlight = 0;

            for (const auto& slot : m_slots)
            {
                if (slot.State.load() != SlotState::Free)
                    ++inFlight;
            }

            stats->InFlightFrames =
                inFlight;

            const auto encoded =
                stats->EncodedFrames;

            const auto submitted =
                stats->SubmittedFrames;

            stats->LastSubmitMs =
                m_lastSubmitUs.load() /
                1000.0;

            stats->AverageSubmitMs =
                submitted != 0
                    ? (m_totalSubmitUs.load() /
                       1000.0) /
                        static_cast<double>(
                            submitted)
                    : 0.0;

            stats->LastOutputLatencyMs =
                m_lastOutputLatencyUs.load() /
                1000.0;

            stats->AverageOutputLatencyMs =
                encoded != 0
                    ? (m_totalOutputLatencyUs.load() /
                       1000.0) /
                        static_cast<double>(
                            encoded)
                    : 0.0;

            // Mantém compatibilidade com a UI antiga:
            // "Tempo encode" passa a significar submit -> bitstream pronto.
            stats->LastEncodeMs =
                stats->LastOutputLatencyMs;

            stats->AverageEncodeMs =
                stats->AverageOutputLatencyMs;

            stats->InputMode =
                m_useNv12Preprocess
                    ? 1
                    : 2;
            stats->RequestedInputMode =
                static_cast<std::int32_t>(m_requestedInputMode);
            stats->SourceMode =
                m_requestedInputMode == LAB_MODE_SYNTHETIC
                    ? 1u
                    : 0u;
            stats->NeedMoreInputAccepted =
                m_needMoreInputAccepted.load();
            stats->EncoderBusyRetries =
                m_encoderBusyRetries.load();
            stats->NoFreeSlotDrops =
                m_noFreeSlotDrops.load();
            stats->FlushTimeouts = m_flushTimeouts.load();
            stats->OldestInFlightMs = 0.0;
            {
                std::lock_guard<std::mutex> pendingLock(m_pendingOutputMutex);
                stats->PendingOutputs =
                    static_cast<std::uint32_t>(m_pendingOutputSlots.size());
                if (!m_pendingOutputSlots.empty())
                {
                    const auto index = m_pendingOutputSlots.front();
                    if (index < SlotCount)
                    {
                        const auto& oldest = m_slots[index];
                        stats->OldestInFlightMs =
                            std::chrono::duration<double, std::milli>(
                                std::chrono::steady_clock::now() - oldest.SubmitTime).count();
                    }
                }
            }
            stats->MapFailures =
                m_mapFailures.load();
            stats->EncodeErrors =
                m_encodeErrors.load();
            stats->LockBusyRetries =
                m_lockBusyRetries.load();
            stats->LastPreprocessMs =
                m_lastPreprocessUs.load() /
                1000.0;

            const auto preprocessSamples =
                m_preprocessSamples.load();

            stats->AveragePreprocessMs =
                preprocessSamples != 0
                    ? (m_totalPreprocessUs.load() /
                       1000.0) /
                        static_cast<double>(
                            preprocessSamples)
                    : 0.0;

            stats->LastMapMs =
                m_lastMapUs.load() / 1000.0;
            const auto mapSamples = m_mapSamples.load();
            stats->AverageMapMs =
                mapSamples != 0
                    ? (m_totalMapUs.load() / 1000.0) /
                        static_cast<double>(mapSamples)
                    : 0.0;

            stats->LastEncodeCallMs =
                m_lastEncodeCallUs.load() / 1000.0;
            const auto encodeCallSamples =
                m_encodeCallSamples.load();
            stats->AverageEncodeCallMs =
                encodeCallSamples != 0
                    ? (m_totalEncodeCallUs.load() / 1000.0) /
                        static_cast<double>(encodeCallSamples)
                    : 0.0;

            stats->LastLockBitstreamMs =
                m_lastLockBitstreamUs.load() / 1000.0;
            const auto lockSamples =
                m_lockBitstreamSamples.load();
            stats->AverageLockBitstreamMs =
                lockSamples != 0
                    ? (m_totalLockBitstreamUs.load() / 1000.0) /
                        static_cast<double>(lockSamples)
                    : 0.0;

            RefreshPreprocessGpuProbes();
            stats->PreprocessCopyFenceSignals = m_preprocessCopyProbeSequence;
            stats->PreprocessCopyFenceCompleted = m_preprocessCopyProbeCompleted;
            stats->PreprocessCopyFencePending = static_cast<std::uint32_t>(
                std::min<std::size_t>(m_preprocessCopyProbePending.size(), 0xFFFFFFFFu));
            stats->PreprocessCopyFenceMaxPending = m_preprocessCopyProbeMaxPending;
            stats->PreprocessCopyFenceOldestAgeMs = ProbeOldestAgeMs(m_preprocessCopyProbePending);
            stats->VideoProcessorFenceSignals = m_videoProcessorProbeSequence;
            stats->VideoProcessorFenceCompleted = m_videoProcessorProbeCompleted;
            stats->VideoProcessorFencePending = static_cast<std::uint32_t>(
                std::min<std::size_t>(m_videoProcessorProbePending.size(), 0xFFFFFFFFu));
            stats->VideoProcessorFenceMaxPending = m_videoProcessorProbeMaxPending;
            stats->VideoProcessorFenceOldestAgeMs = ProbeOldestAgeMs(m_videoProcessorProbePending);

            if (m_sharedPacketQueue)
            {
                stats->QueuedPackets =
                    m_sharedPacketQueue->QueuedPackets();
                stats->QueuedBytes =
                    m_sharedPacketQueue->QueuedBytes();
            }

            stats->OutputWorkerAlive =
                m_outputWorkerAlive.load(std::memory_order_acquire) ? 1u : 0u;
            stats->LastSubmitAgoMs = AgeMsFrom(
                m_lastSubmitProgressUs.load(std::memory_order_relaxed));
            stats->LastOutputAgoMs = AgeMsFrom(
                m_lastOutputProgressUs.load(std::memory_order_relaxed));

            return S_OK;
        }

        HRESULT PeekPacket(
            std::uint32_t* requiredBytes,
            std::uint64_t* frameId,
            std::int32_t* keyFrame)
        {
            if (!requiredBytes || !frameId || !keyFrame)
                return E_INVALIDARG;

            if (!m_sharedPacketQueue)
                return S_FALSE;

            // Consumer ownership is explicit. Reads must never reactivate
            // a queue after its owner has stopped.
            if (!m_sharedPacketQueue->IsActive())
                return S_FALSE;

            bool isKeyFrame = false;
            if (!m_sharedPacketQueue->Peek(
                    *requiredBytes,
                    *frameId,
                    isKeyFrame))
            {
                return S_FALSE;
            }

            *keyFrame = isKeyFrame ? 1 : 0;
            return S_OK;
        }

        HRESULT PopPacket(
            std::uint8_t* destination,
            std::uint32_t destinationBytes,
            std::uint32_t* writtenBytes,
            std::uint64_t* frameId,
            std::int32_t* keyFrame)
        {
            if (!destination || !writtenBytes || !frameId || !keyFrame)
                return E_INVALIDARG;

            if (!m_sharedPacketQueue)
                return S_FALSE;

            // Consumer ownership is explicit. Reads must never reactivate
            // a queue after its owner has stopped.
            if (!m_sharedPacketQueue->IsActive())
                return S_FALSE;

            bool isKeyFrame = false;
            const auto result = m_sharedPacketQueue->Pop(
                destination,
                destinationBytes,
                *writtenBytes,
                *frameId,
                isKeyFrame);

            if (result == SharedEncodedPacketQueue::PopResult::Empty)
                return S_FALSE;

            if (result == SharedEncodedPacketQueue::PopResult::InsufficientBuffer)
            {
                return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
            }

            *keyFrame = isKeyFrame ? 1 : 0;
            return S_OK;
        }

        void AddPacingDrop()
        {
            m_pacingDrops.fetch_add(1);
        }

        std::uint32_t EffectivePreset() const noexcept
        {
            return m_effectivePreset;
        }

        bool UsesNv12Preprocess() const noexcept
        {
            return m_useNv12Preprocess;
        }

        const std::wstring& InputPathDiagnostic() const noexcept
        {
            return m_inputPathDiagnostic;
        }

    private:
        enum class SlotState : std::uint8_t
        {
            Free = 0,
            PreparedForSubmit,
            SubmittedPendingOutput,
            CompletionObserved
        };

        struct Slot
        {
            ComPtr<ID3D11Texture2D>
                Texture;

            // Each NVENC slot owns its own BGRA video-processor input.
            // Core 09.2 shared one BGRA surface between all async slots, which
            // could serialize preprocess work under 99% foreground GPU load.
            ComPtr<ID3D11Texture2D>
                VideoInputTexture;

            ComPtr<ID3D11VideoProcessorInputView>
                VideoInputView;

            ComPtr<ID3D11VideoProcessorOutputView>
                VideoOutputView;

            // CORE 11.14: private ARGB NVENC slot is also the render target
            // of the isolated EncodeDevice RGB scaler.
            ComPtr<ID3D11RenderTargetView>
                ScaleOutputView;

            NV_ENC_REGISTERED_PTR
                RegisteredInput = nullptr;

            NV_ENC_INPUT_PTR
                MappedInput = nullptr;

            NV_ENC_BUFFER_FORMAT
                MappedBufferFormat = NV_ENC_BUFFER_FORMAT_UNDEFINED;

            // CORE 11.13 direct-ring input. Internal slots still own
            // RegisteredInput; direct-ring slots map a cached external
            // registration and hold the ring lease until NVENC completion.
            NV_ENC_REGISTERED_PTR
                ExternalRegisteredInput = nullptr;
            ComPtr<ID3D11Texture2D>
                ExternalTexture;
            std::function<void()>
                OnInputReleased;

            NV_ENC_OUTPUT_PTR
                Bitstream = nullptr;

            HANDLE Event = nullptr;

            std::atomic<SlotState>
                State{ SlotState::Free };

            std::uint64_t
                FrameId = 0;

            std::uint64_t
                SubmitSequence = 0;

            NVENCSTATUS
                AcceptedStatus = NV_ENC_SUCCESS;

            bool ForceIdr = false;
            long long SubmitCpuUs = 0;

            std::chrono::steady_clock::time_point
                SubmitTime{};
        };

        struct ExternalInputRegistration
        {
            ComPtr<ID3D11Texture2D> Texture;
            NV_ENC_REGISTERED_PTR Registered = nullptr;
            std::uint32_t Width = 0;
            std::uint32_t Height = 0;
        };

        struct ScaleSourceView
        {
            ComPtr<ID3D11Texture2D> Texture;
            ComPtr<ID3D11ShaderResourceView> View;
        };


        void AcceptSubmittedSlot(
            std::size_t slotIndex,
            NVENCSTATUS status)
        {
            auto& slot = m_slots[slotIndex];
            slot.AcceptedStatus = status;
            slot.SubmitSequence = m_nextSubmitSequence++;
            slot.State.store(SlotState::SubmittedPendingOutput);
            {
                std::lock_guard<std::mutex> pendingLock(m_pendingOutputMutex);
                m_pendingOutputSlots.push_back(slotIndex);
            }
            m_pendingOutputCv.notify_one();
            m_submittedFrames.fetch_add(1);
            m_lastSubmitProgressUs.store(
                SteadyNowUs(),
                std::memory_order_relaxed);
            m_lastSubmitUs.store(slot.SubmitCpuUs);
            m_totalSubmitUs.fetch_add(
                static_cast<std::uint64_t>(
                    std::max<long long>(0, slot.SubmitCpuUs)));

            if (status == NV_ENC_ERR_NEED_MORE_INPUT)
                m_needMoreInputAccepted.fetch_add(1);

            if (m_retrySlotIndex == slotIndex)
                m_retrySlotIndex = SlotCount;

            m_lastStatus.store(S_OK);
        }

        void CompleteInputOwnership(Slot& slot) noexcept
        {
            slot.ExternalRegisteredInput = nullptr;
            slot.ExternalTexture.Reset();
            if (slot.OnInputReleased)
            {
                auto release = std::move(slot.OnInputReleased);
                try { release(); } catch (...) {}
            }
        }

        void ReleasePreparedSlot(Slot& slot)
        {
            if (slot.MappedInput && m_api.nvEncUnmapInputResource)
            {
                (void)m_api.nvEncUnmapInputResource(
                    m_encoder,
                    slot.MappedInput);
            }

            slot.MappedInput = nullptr;
            slot.MappedBufferFormat = NV_ENC_BUFFER_FORMAT_UNDEFINED;
            CompleteInputOwnership(slot);
            slot.FrameId = 0;
            slot.SubmitSequence = 0;
            slot.AcceptedStatus = NV_ENC_SUCCESS;
            slot.ForceIdr = false;
            slot.SubmitCpuUs = 0;
            slot.SubmitTime = {};
            slot.State.store(SlotState::Free);
        }

        NV_ENC_PIC_PARAMS BuildPictureParams(const Slot& slot) const
        {
            NV_ENC_PIC_PARAMS pic{};
            pic.version = NV_ENC_PIC_PARAMS_VER;
            pic.inputBuffer = slot.MappedInput;
            pic.bufferFmt = slot.MappedBufferFormat;
            pic.inputWidth = m_width;
            pic.inputHeight = m_height;
            pic.outputBitstream = slot.Bitstream;
            pic.completionEvent = slot.Event;
            pic.pictureStruct = NV_ENC_PIC_STRUCT_FRAME;

            if (slot.ForceIdr)
            {
                pic.encodePicFlags =
                    NV_ENC_PIC_FLAG_FORCEIDR |
                    NV_ENC_PIC_FLAG_OUTPUT_SPSPPS;
            }

            return pic;
        }

        NVENCSTATUS SubmitPreparedSlot(std::size_t slotIndex)
        {
            if (!m_encoder || slotIndex >= SlotCount)
                return NV_ENC_ERR_ENCODER_NOT_INITIALIZED;

            auto& slot = m_slots[slotIndex];
            if (slot.State.load() != SlotState::PreparedForSubmit ||
                !slot.MappedInput)
            {
                return NV_ENC_ERR_INVALID_CALL;
            }

            // ENCODER_BUSY retries reuse the same prepared slot, but the
            // completion event must start each API attempt unsignaled.
            ResetEvent(slot.Event);
            NV_ENC_PIC_PARAMS pic = BuildPictureParams(slot);

            const auto encodeCallStart =
                std::chrono::steady_clock::now();
            const NVENCSTATUS status =
                m_api.nvEncEncodePicture(m_encoder, &pic);
            const auto encodeCallEnd =
                std::chrono::steady_clock::now();
            const auto encodeCallUs =
                std::chrono::duration_cast<std::chrono::microseconds>(
                    encodeCallEnd - encodeCallStart).count();

            m_lastEncodeCallUs.store(encodeCallUs);
            m_totalEncodeCallUs.fetch_add(
                static_cast<std::uint64_t>(
                    std::max<long long>(0, encodeCallUs)));
            m_encodeCallSamples.fetch_add(1);
            slot.SubmitCpuUs += encodeCallUs;

            if (status == NV_ENC_SUCCESS ||
                status == NV_ENC_ERR_NEED_MORE_INPUT)
            {
                AcceptSubmittedSlot(slotIndex, status);
                return status;
            }

            if (status == NV_ENC_ERR_ENCODER_BUSY)
            {
                m_encoderBusyRetries.fetch_add(1);
                if (m_useExternalArgbInput)
                {
                    // A direct-ring lease must never remain hidden behind an
                    // encoder retry. Drop this fresh frame and release its ring
                    // slot immediately; latest-frame-wins will supply the next.
                    ReleasePreparedSlot(slot);
                    m_retrySlotIndex = SlotCount;
                    m_lastStatus.store(S_OK);
                    return status;
                }

                // Legacy paths preserve exact-input retry semantics.
                slot.State.store(SlotState::PreparedForSubmit);
                m_retrySlotIndex = slotIndex;
                m_retryNotBefore =
                    std::chrono::steady_clock::now() +
                    std::chrono::milliseconds(1);
                m_lastStatus.store(S_OK);
                return status;
            }

            m_encodeErrors.fetch_add(1);
            m_lastStatus.store(NvencStatusToHresult(status));
            if (m_retrySlotIndex == slotIndex)
                m_retrySlotIndex = SlotCount;
            ReleasePreparedSlot(slot);
            return status;
        }

        NVENCSTATUS RetryBusySubmission()
        {
            if (m_retrySlotIndex >= SlotCount)
                return NV_ENC_SUCCESS;

            if (std::chrono::steady_clock::now() < m_retryNotBefore)
                return NV_ENC_ERR_ENCODER_BUSY;

            const auto slotIndex = m_retrySlotIndex;
            return SubmitPreparedSlot(slotIndex);
        }


        NVENCSTATUS GetOrRegisterExternalInput(
            ID3D11Texture2D* source,
            NV_ENC_REGISTERED_PTR& registered)
        {
            registered = nullptr;
            if (!m_encoder || !source || !m_useExternalArgbInput)
                return NV_ENC_ERR_INVALID_PTR;

            D3D11_TEXTURE2D_DESC desc{};
            source->GetDesc(&desc);
            if (desc.Width != m_width || desc.Height != m_height ||
                desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM)
            {
                return NV_ENC_ERR_INVALID_PARAM;
            }

            for (auto& entry : m_externalInputs)
            {
                if (entry.Texture.Get() == source && entry.Registered)
                {
                    registered = entry.Registered;
                    return NV_ENC_SUCCESS;
                }
            }

            NV_ENC_REGISTER_RESOURCE registerResource{};
            registerResource.version = NV_ENC_REGISTER_RESOURCE_VER;
            registerResource.resourceType = NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX;
            registerResource.width = m_width;
            registerResource.height = m_height;
            registerResource.pitch = 0;
            registerResource.subResourceIndex = 0;
            registerResource.resourceToRegister = source;
            registerResource.bufferFormat = NV_ENC_BUFFER_FORMAT_ARGB;

            m_lastStage = L"nvEncRegisterResource(external)";
            const NVENCSTATUS status =
                m_api.nvEncRegisterResource(m_encoder, &registerResource);
            if (status != NV_ENC_SUCCESS)
                return status;

            ExternalInputRegistration entry{};
            entry.Texture = source;
            entry.Registered = registerResource.registeredResource;
            entry.Width = desc.Width;
            entry.Height = desc.Height;
            m_externalInputs.push_back(std::move(entry));
            registered = registerResource.registeredResource;
            return NV_ENC_SUCCESS;
        }

    public:
        NVENCSTATUS SubmitExternalArgb(
            ID3D11Texture2D* source,
            std::uint64_t frameId,
            bool forceIdr,
            std::function<void()> onInputReleased,
            bool* ownershipTransferred)
        {
            if (ownershipTransferred)
                *ownershipTransferred = false;
            if (!IsRunning() || !source || !m_useExternalArgbInput)
                return NV_ENC_ERR_ENCODER_NOT_INITIALIZED;

            const NVENCSTATUS pumpStatus = Pump();
            if (pumpStatus != NV_ENC_SUCCESS &&
                pumpStatus != NV_ENC_ERR_LOCK_BUSY)
            {
                return pumpStatus;
            }

            if (m_retrySlotIndex < SlotCount)
                return NV_ENC_ERR_ENCODER_BUSY;

            Slot* slot = nullptr;
            std::size_t slotIndex = 0;
            const std::size_t baseSlot = m_nextSlot.fetch_add(1) % SlotCount;
            for (std::size_t offset = 0; offset < SlotCount; ++offset)
            {
                const std::size_t index = (baseSlot + offset) % SlotCount;
                SlotState expected = SlotState::Free;
                if (m_slots[index].State.compare_exchange_strong(
                        expected,
                        SlotState::PreparedForSubmit))
                {
                    slot = &m_slots[index];
                    slotIndex = index;
                    break;
                }
            }

            if (!slot)
            {
                m_noFreeSlotDrops.fetch_add(1);
                return NV_ENC_ERR_ENCODER_BUSY;
            }

            ResetEvent(slot->Event);
            slot->FrameId = frameId;
            slot->ForceIdr = forceIdr;
            slot->SubmitTime = std::chrono::steady_clock::now();
            slot->SubmitCpuUs = 0;
            slot->AcceptedStatus = NV_ENC_SUCCESS;
            slot->SubmitSequence = 0;
            slot->MappedBufferFormat = NV_ENC_BUFFER_FORMAT_UNDEFINED;
            slot->ExternalTexture = source;
            slot->OnInputReleased = std::move(onInputReleased);
            if (ownershipTransferred)
                *ownershipTransferred = true;

            const auto submitStart = std::chrono::steady_clock::now();
            NV_ENC_REGISTERED_PTR registered = nullptr;
            NVENCSTATUS status = GetOrRegisterExternalInput(source, registered);
            if (status != NV_ENC_SUCCESS)
            {
                m_mapFailures.fetch_add(1);
                m_lastStatus.store(NvencStatusToHresult(status));
                ReleasePreparedSlot(*slot);
                return status;
            }
            slot->ExternalRegisteredInput = registered;

            NV_ENC_MAP_INPUT_RESOURCE mapInput{};
            mapInput.version = NV_ENC_MAP_INPUT_RESOURCE_VER;
            mapInput.registeredResource = registered;
            const auto mapStart = std::chrono::steady_clock::now();
            status = m_api.nvEncMapInputResource(m_encoder, &mapInput);
            const auto mapEnd = std::chrono::steady_clock::now();
            const auto mapUs = std::chrono::duration_cast<std::chrono::microseconds>(
                mapEnd - mapStart).count();
            m_lastMapUs.store(mapUs);
            m_totalMapUs.fetch_add(static_cast<std::uint64_t>(std::max<long long>(0, mapUs)));
            m_mapSamples.fetch_add(1);

            if (status != NV_ENC_SUCCESS)
            {
                m_mapFailures.fetch_add(1);
                m_lastStatus.store(NvencStatusToHresult(status));
                ReleasePreparedSlot(*slot);
                return status;
            }

            slot->MappedInput = mapInput.mappedResource;
            slot->MappedBufferFormat =
                mapInput.mappedBufferFmt != NV_ENC_BUFFER_FORMAT_UNDEFINED
                    ? mapInput.mappedBufferFmt
                    : NV_ENC_BUFFER_FORMAT_ARGB;
            const auto preparedAt = std::chrono::steady_clock::now();
            slot->SubmitCpuUs = std::chrono::duration_cast<std::chrono::microseconds>(
                preparedAt - submitStart).count();

            status = SubmitPreparedSlot(slotIndex);
            // Once the callback has been installed, ownershipTransferred stays
            // true even if SubmitPreparedSlot immediately releases it. This tells
            // the caller that ring release was handled exactly once internally.
            return status;
        }

    private:
        NVENCSTATUS CreateEosCompletionEvent()
        {
            if (!m_encoder || !m_api.nvEncRegisterAsyncEvent)
                return NV_ENC_ERR_ENCODER_NOT_INITIALIZED;

            m_eosEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
            if (!m_eosEvent)
                return NV_ENC_ERR_OUT_OF_MEMORY;

            NV_ENC_EVENT_PARAMS eventParams{};
            eventParams.version = NV_ENC_EVENT_PARAMS_VER;
            eventParams.completionEvent = m_eosEvent;

            m_lastStage = L"nvEncRegisterAsyncEvent(EOS)";
            const NVENCSTATUS status =
                m_api.nvEncRegisterAsyncEvent(m_encoder, &eventParams);

            if (status != NV_ENC_SUCCESS)
            {
                CloseHandle(m_eosEvent);
                m_eosEvent = nullptr;
            }

            return status;
        }

        NVENCSTATUS FlushPendingOutputs(
            std::chrono::milliseconds timeout)
        {
            if (!m_encoder)
                return NV_ENC_ERR_ENCODER_NOT_INITIALIZED;

            const auto deadline = std::chrono::steady_clock::now() + timeout;

            while (m_retrySlotIndex < SlotCount &&
                   std::chrono::steady_clock::now() < deadline)
            {
                if (std::chrono::steady_clock::now() >= m_retryNotBefore)
                    (void)RetryBusySubmission();
                std::this_thread::yield();
            }

            if (m_retrySlotIndex < SlotCount)
            {
                m_flushTimeouts.fetch_add(1);
                return NV_ENC_ERR_ENCODER_BUSY;
            }

            if (m_eosEvent)
                ResetEvent(m_eosEvent);

            bool eosAccepted = false;
            while (!eosAccepted && std::chrono::steady_clock::now() < deadline)
            {
                NV_ENC_PIC_PARAMS eos{};
                eos.version = NV_ENC_PIC_PARAMS_VER;
                eos.encodePicFlags = NV_ENC_PIC_FLAG_EOS;
                eos.completionEvent = m_eosEvent;
                const NVENCSTATUS status = m_api.nvEncEncodePicture(m_encoder, &eos);
                if (status == NV_ENC_SUCCESS || status == NV_ENC_ERR_NEED_MORE_INPUT)
                {
                    eosAccepted = true;
                    break;
                }
                if (status != NV_ENC_ERR_ENCODER_BUSY)
                {
                    m_lastStatus.store(NvencStatusToHresult(status));
                    return status;
                }
                m_encoderBusyRetries.fetch_add(1);
                std::this_thread::yield();
            }

            if (!eosAccepted)
            {
                m_flushTimeouts.fetch_add(1);
                return NV_ENC_ERR_ENCODER_BUSY;
            }

            {
                std::unique_lock<std::mutex> pendingLock(m_pendingOutputMutex);
                while (!m_pendingOutputSlots.empty())
                {
                    if (m_pendingOutputCv.wait_until(pendingLock, deadline) ==
                        std::cv_status::timeout)
                    {
                        m_flushTimeouts.fetch_add(1);
                        return NV_ENC_ERR_ENCODER_BUSY;
                    }
                }
            }

            if (m_eosEvent)
            {
                const auto now = std::chrono::steady_clock::now();
                if (now >= deadline)
                {
                    m_flushTimeouts.fetch_add(1);
                    return NV_ENC_ERR_ENCODER_BUSY;
                }
                const auto remaining = std::chrono::duration_cast<std::chrono::milliseconds>(deadline - now);
                const DWORD waitMs = static_cast<DWORD>(
                    std::clamp<long long>(remaining.count(), 1, 0x7fffffff));
                if (WaitForSingleObject(m_eosEvent, waitMs) != WAIT_OBJECT_0)
                {
                    m_flushTimeouts.fetch_add(1);
                    return NV_ENC_ERR_ENCODER_BUSY;
                }
            }

            return NV_ENC_SUCCESS;
        }

        bool EncoderSupportsInputFormat(
            NV_ENC_BUFFER_FORMAT format)
        {
            if (!m_encoder ||
                !m_api.nvEncGetInputFormatCount ||
                !m_api.nvEncGetInputFormats)
            {
                return false;
            }

            std::uint32_t count = 0;

            if (m_api.nvEncGetInputFormatCount(
                    m_encoder,
                    NV_ENC_CODEC_H264_GUID,
                    &count) != NV_ENC_SUCCESS ||
                count == 0)
            {
                return false;
            }

            std::vector<NV_ENC_BUFFER_FORMAT> formats(
                count);

            std::uint32_t written = 0;

            if (m_api.nvEncGetInputFormats(
                    m_encoder,
                    NV_ENC_CODEC_H264_GUID,
                    formats.data(),
                    count,
                    &written) != NV_ENC_SUCCESS)
            {
                return false;
            }

            const std::uint32_t valid =
                std::min<std::uint32_t>(
                    count,
                    written);

            for (std::uint32_t i = 0;
                 i < valid;
                 ++i)
            {
                if (formats[i] == format)
                    return true;
            }

            return false;
        }

        HRESULT InitializeNv12Preprocessor(
            ID3D11Device* device)
        {
            if (!device ||
                m_sourceWidth == 0 ||
                m_sourceHeight == 0 ||
                m_width == 0 ||
                m_height == 0 ||
                (m_width & 1u) != 0 ||
                (m_height & 1u) != 0)
            {
                return E_INVALIDARG;
            }

            ResetVideoPreprocessor();

            ComPtr<ID3D11DeviceContext> context;
            device->GetImmediateContext(
                context.GetAddressOf());

            if (!context)
                return E_FAIL;

            m_lastStage =
                L"D3D11 video device";

            HRESULT hr =
                device->QueryInterface(
                    IID_PPV_ARGS(
                        m_videoDevice.GetAddressOf()));

            if (FAILED(hr))
                return hr;

            hr =
                context->QueryInterface(
                    IID_PPV_ARGS(
                        m_videoContext.GetAddressOf()));

            if (FAILED(hr))
                return hr;

            D3D11_VIDEO_PROCESSOR_CONTENT_DESC content{};
            content.InputFrameFormat =
                D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
            content.InputFrameRate.Numerator =
                std::max<std::uint32_t>(1u, m_fps);
            content.InputFrameRate.Denominator = 1;
            content.InputWidth = m_sourceWidth;
            content.InputHeight = m_sourceHeight;
            content.OutputFrameRate =
                content.InputFrameRate;
            content.OutputWidth = m_width;
            content.OutputHeight = m_height;
            content.Usage =
                D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;

            m_lastStage =
                L"CreateVideoProcessorEnumerator";

            hr =
                m_videoDevice->CreateVideoProcessorEnumerator(
                    &content,
                    m_videoEnumerator.GetAddressOf());

            if (FAILED(hr) || !m_videoEnumerator)
                return FAILED(hr) ? hr : E_FAIL;

            // Validate the exact BGRA -> NV12 path before allocating the four
            // NVENC slots. Merely seeing NV12 in nvEncGetInputFormats is not
            // sufficient: the D3D11 video processor and the NVENC DirectX
            // resource path must both accept the surfaces on this driver.
            UINT inputSupport = 0;
            m_lastStage =
                L"CheckVideoProcessorFormat(BGRA input)";

            hr =
                m_videoEnumerator->CheckVideoProcessorFormat(
                    DXGI_FORMAT_B8G8R8A8_UNORM,
                    &inputSupport);

            if (FAILED(hr) ||
                (inputSupport &
                    D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_INPUT) == 0)
            {
                return FAILED(hr)
                    ? hr
                    : DXGI_ERROR_UNSUPPORTED;
            }

            UINT outputSupport = 0;
            m_lastStage =
                L"CheckVideoProcessorFormat(NV12 output)";

            hr =
                m_videoEnumerator->CheckVideoProcessorFormat(
                    DXGI_FORMAT_NV12,
                    &outputSupport);

            if (FAILED(hr) ||
                (outputSupport &
                    D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_OUTPUT) == 0)
            {
                return FAILED(hr)
                    ? hr
                    : DXGI_ERROR_UNSUPPORTED;
            }

            m_lastStage =
                L"CreateVideoProcessor";

            hr =
                m_videoDevice->CreateVideoProcessor(
                    m_videoEnumerator.Get(),
                    0,
                    m_videoProcessor.GetAddressOf());

            if (FAILED(hr) || !m_videoProcessor)
                return FAILED(hr) ? hr : E_FAIL;

            // Input surfaces themselves are allocated per NVENC slot in
            // CreateSlot(). The VideoProcessor is shared safely because all
            // preprocessing submissions run on the dedicated encoder thread.
            m_videoContext->VideoProcessorSetStreamAutoProcessingMode(
                m_videoProcessor.Get(),
                0,
                FALSE);

            const RECT sourceRect
            {
                0,
                0,
                static_cast<LONG>(m_sourceWidth),
                static_cast<LONG>(m_sourceHeight)
            };
            const RECT outputRect
            {
                0,
                0,
                static_cast<LONG>(m_width),
                static_cast<LONG>(m_height)
            };
            m_videoContext->VideoProcessorSetStreamSourceRect(
                m_videoProcessor.Get(), 0, TRUE, &sourceRect);
            m_videoContext->VideoProcessorSetStreamDestRect(
                m_videoProcessor.Get(), 0, TRUE, &outputRect);
            m_videoContext->VideoProcessorSetOutputTargetRect(
                m_videoProcessor.Get(), TRUE, &outputRect);

            return S_OK;
        }

        void ResetVideoPreprocessor()
        {
            m_videoProcessor.Reset();
            m_videoEnumerator.Reset();
            m_videoContext.Reset();
            m_videoDevice.Reset();
        }

        HRESULT InitializeArgbScaler(ID3D11Device* device)
        {
            if (!device)
                return E_INVALIDARG;
            if (m_argbScaleVs && m_argbScalePs && m_argbScaleSampler)
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
                "MultiverseEncodeScaleVS",
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
                "MultiverseEncodeScalePS",
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

            hr = device->CreateVertexShader(
                vsBlob->GetBufferPointer(),
                vsBlob->GetBufferSize(),
                nullptr,
                m_argbScaleVs.GetAddressOf());
            if (FAILED(hr))
                return hr;

            hr = device->CreatePixelShader(
                psBlob->GetBufferPointer(),
                psBlob->GetBufferSize(),
                nullptr,
                m_argbScalePs.GetAddressOf());
            if (FAILED(hr))
                return hr;

            D3D11_SAMPLER_DESC sampler{};
            sampler.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
            sampler.AddressU = D3D11_TEXTURE_ADDRESS_CLAMP;
            sampler.AddressV = D3D11_TEXTURE_ADDRESS_CLAMP;
            sampler.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
            sampler.MinLOD = 0.0f;
            sampler.MaxLOD = D3D11_FLOAT32_MAX;
            return device->CreateSamplerState(
                &sampler,
                m_argbScaleSampler.GetAddressOf());
        }

        HRESULT GetOrCreateArgbScaleSourceView(
            ID3D11Texture2D* source,
            ID3D11ShaderResourceView** view)
        {
            if (!source || !view)
                return E_INVALIDARG;
            *view = nullptr;

            for (auto& entry : m_argbScaleSourceViews)
            {
                if (entry.Texture.Get() == source && entry.View)
                {
                    *view = entry.View.Get();
                    (*view)->AddRef();
                    return S_OK;
                }
            }

            ComPtr<ID3D11Device> device;
            source->GetDevice(device.GetAddressOf());
            if (!device)
                return E_NOINTERFACE;

            ComPtr<ID3D11ShaderResourceView> sourceView;
            const HRESULT hr = device->CreateShaderResourceView(
                source,
                nullptr,
                sourceView.GetAddressOf());
            if (FAILED(hr))
                return hr;

            ScaleSourceView entry{};
            entry.Texture = source;
            entry.View = sourceView;
            m_argbScaleSourceViews.push_back(std::move(entry));
            *view = sourceView.Detach();
            return S_OK;
        }

        NVENCSTATUS ScaleBgraToArgb(
            ID3D11DeviceContext* context,
            ID3D11Texture2D* source,
            Slot& slot)
        {
            if (!context || !source || !slot.Texture || !slot.ScaleOutputView ||
                !m_argbScaleVs || !m_argbScalePs || !m_argbScaleSampler)
            {
                return NV_ENC_ERR_INVALID_PTR;
            }

            D3D11_TEXTURE2D_DESC sourceDesc{};
            source->GetDesc(&sourceDesc);
            if (sourceDesc.Width != m_sourceWidth ||
                sourceDesc.Height != m_sourceHeight ||
                sourceDesc.Format != DXGI_FORMAT_B8G8R8A8_UNORM)
            {
                return NV_ENC_ERR_INVALID_PARAM;
            }

            ComPtr<ID3D11ShaderResourceView> sourceView;
            HRESULT hr = GetOrCreateArgbScaleSourceView(
                source,
                sourceView.GetAddressOf());
            if (FAILED(hr))
                return NV_ENC_ERR_GENERIC;

            D3D11_VIEWPORT viewport{};
            viewport.Width = static_cast<float>(m_width);
            viewport.Height = static_cast<float>(m_height);
            viewport.MinDepth = 0.0f;
            viewport.MaxDepth = 1.0f;

            ID3D11RenderTargetView* rtv = slot.ScaleOutputView.Get();
            ID3D11ShaderResourceView* srv = sourceView.Get();
            ID3D11SamplerState* sampler = m_argbScaleSampler.Get();
            context->OMSetRenderTargets(1, &rtv, nullptr);
            context->RSSetViewports(1, &viewport);
            context->IASetInputLayout(nullptr);
            context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            context->VSSetShader(m_argbScaleVs.Get(), nullptr, 0);
            context->PSSetShader(m_argbScalePs.Get(), nullptr, 0);
            context->PSSetShaderResources(0, 1, &srv);
            context->PSSetSamplers(0, 1, &sampler);
            context->Draw(3, 0);

            ID3D11ShaderResourceView* nullSrv = nullptr;
            context->PSSetShaderResources(0, 1, &nullSrv);
            context->OMSetRenderTargets(0, nullptr, nullptr);
            return NV_ENC_SUCCESS;
        }

        void ReleaseSlotsForStartupFallback()
        {
            if (!m_encoder)
                return;

            for (auto& slot : m_slots)
            {
                if (slot.MappedInput &&
                    m_api.nvEncUnmapInputResource)
                {
                    m_api.nvEncUnmapInputResource(
                        m_encoder,
                        slot.MappedInput);
                    slot.MappedInput = nullptr;
                }

                if (slot.Event &&
                    m_api.nvEncUnregisterAsyncEvent)
                {
                    NV_ENC_EVENT_PARAMS eventParams{};
                    eventParams.version =
                        NV_ENC_EVENT_PARAMS_VER;
                    eventParams.completionEvent =
                        slot.Event;
                    (void)m_api.nvEncUnregisterAsyncEvent(
                        m_encoder,
                        &eventParams);
                }

                if (slot.Bitstream &&
                    m_api.nvEncDestroyBitstreamBuffer)
                {
                    (void)m_api.nvEncDestroyBitstreamBuffer(
                        m_encoder,
                        slot.Bitstream);
                    slot.Bitstream = nullptr;
                }

                if (slot.RegisteredInput &&
                    m_api.nvEncUnregisterResource)
                {
                    (void)m_api.nvEncUnregisterResource(
                        m_encoder,
                        slot.RegisteredInput);
                    slot.RegisteredInput = nullptr;
                }

                if (slot.Event)
                {
                    CloseHandle(slot.Event);
                    slot.Event = nullptr;
                }

                slot.ScaleOutputView.Reset();
                slot.VideoOutputView.Reset();
                slot.VideoInputView.Reset();
                slot.VideoInputTexture.Reset();
                slot.Texture.Reset();
                slot.MappedBufferFormat = NV_ENC_BUFFER_FORMAT_UNDEFINED;
                slot.FrameId = 0;
                slot.SubmitSequence = 0;
                slot.AcceptedStatus = NV_ENC_SUCCESS;
                slot.ForceIdr = false;
                slot.SubmitCpuUs = 0;
                slot.SubmitTime = {};
                slot.State.store(SlotState::Free);
            }
        }

        using ProbePendingEntry =
            std::pair<std::uint64_t, std::chrono::steady_clock::time_point>;

        static double ProbeOldestAgeMs(
            const std::deque<ProbePendingEntry>& pending) noexcept
        {
            if (pending.empty())
                return 0.0;
            return std::chrono::duration<double, std::milli>(
                std::chrono::steady_clock::now() - pending.front().second).count();
        }

        void ResetPreprocessGpuProbe() noexcept
        {
            m_preprocessProbeContext4.Reset();
            m_preprocessCopyProbeFence.Reset();
            m_videoProcessorProbeFence.Reset();
            m_preprocessCopyProbeSequence = 0;
            m_preprocessCopyProbeCompleted = 0;
            m_preprocessCopyProbeMaxPending = 0;
            m_preprocessCopyProbePending.clear();
            m_videoProcessorProbeSequence = 0;
            m_videoProcessorProbeCompleted = 0;
            m_videoProcessorProbeMaxPending = 0;
            m_videoProcessorProbePending.clear();
        }

        HRESULT InitializePreprocessGpuProbe(ID3D11Device* device)
        {
            ResetPreprocessGpuProbe();
            if (!device)
                return E_INVALIDARG;

            ComPtr<ID3D11Device5> device5;
            HRESULT hr = device->QueryInterface(IID_PPV_ARGS(device5.GetAddressOf()));
            if (FAILED(hr))
                return hr;

            ComPtr<ID3D11DeviceContext> immediate;
            device->GetImmediateContext(immediate.GetAddressOf());
            if (!immediate)
                return E_NOINTERFACE;

            hr = immediate->QueryInterface(
                IID_PPV_ARGS(m_preprocessProbeContext4.GetAddressOf()));
            if (FAILED(hr))
                return hr;

            hr = device5->CreateFence(
                0,
                D3D11_FENCE_FLAG_NONE,
                IID_PPV_ARGS(m_preprocessCopyProbeFence.GetAddressOf()));
            if (FAILED(hr))
            {
                ResetPreprocessGpuProbe();
                return hr;
            }

            hr = device5->CreateFence(
                0,
                D3D11_FENCE_FLAG_NONE,
                IID_PPV_ARGS(m_videoProcessorProbeFence.GetAddressOf()));
            if (FAILED(hr))
            {
                ResetPreprocessGpuProbe();
                return hr;
            }

            return S_OK;
        }

        static void RefreshProbeFence(
            ID3D11Fence* fence,
            std::uint64_t& completed,
            std::deque<ProbePendingEntry>& pending) noexcept
        {
            if (!fence)
                return;
            completed = fence->GetCompletedValue();
            while (!pending.empty() && pending.front().first <= completed)
                pending.pop_front();
        }

        void RefreshPreprocessGpuProbes() noexcept
        {
            RefreshProbeFence(
                m_preprocessCopyProbeFence.Get(),
                m_preprocessCopyProbeCompleted,
                m_preprocessCopyProbePending);
            RefreshProbeFence(
                m_videoProcessorProbeFence.Get(),
                m_videoProcessorProbeCompleted,
                m_videoProcessorProbePending);
        }

        void SignalPreprocessCopyProbe() noexcept
        {
            if (!m_preprocessCopyProbeFence || !m_preprocessProbeContext4)
                return;

            const std::uint64_t value = ++m_preprocessCopyProbeSequence;
            const HRESULT hr = m_preprocessProbeContext4->Signal(m_preprocessCopyProbeFence.Get(), value);
            if (SUCCEEDED(hr))
            {
                m_preprocessCopyProbePending.emplace_back(
                    value, std::chrono::steady_clock::now());
                m_preprocessCopyProbeMaxPending = std::max<std::uint32_t>(
                    m_preprocessCopyProbeMaxPending,
                    static_cast<std::uint32_t>(std::min<std::size_t>(
                        m_preprocessCopyProbePending.size(), 0xFFFFFFFFu)));
            }
        }

        void SignalVideoProcessorProbe() noexcept
        {
            if (!m_videoProcessorProbeFence || !m_preprocessProbeContext4)
                return;

            const std::uint64_t value = ++m_videoProcessorProbeSequence;
            const HRESULT hr = m_preprocessProbeContext4->Signal(m_videoProcessorProbeFence.Get(), value);
            if (SUCCEEDED(hr))
            {
                m_videoProcessorProbePending.emplace_back(
                    value, std::chrono::steady_clock::now());
                m_videoProcessorProbeMaxPending = std::max<std::uint32_t>(
                    m_videoProcessorProbeMaxPending,
                    static_cast<std::uint32_t>(std::min<std::size_t>(
                        m_videoProcessorProbePending.size(), 0xFFFFFFFFu)));
            }
        }

        NVENCSTATUS CreateAllSlots(
            ID3D11Device* device)
        {
            for (std::size_t i = 0;
                 i < SlotCount;
                 ++i)
            {
                const NVENCSTATUS status =
                    CreateSlot(
                        device,
                        m_slots[i]);

                if (status != NV_ENC_SUCCESS)
                    return status;
            }

            return NV_ENC_SUCCESS;
        }

        NVENCSTATUS ConvertBgraToNv12(
            ID3D11DeviceContext* context,
            ID3D11Texture2D* source,
            Slot& slot)
        {
            if (!context ||
                !source ||
                !slot.VideoInputTexture ||
                !slot.VideoInputView ||
                !m_videoContext ||
                !m_videoProcessor ||
                !slot.VideoOutputView)
            {
                return NV_ENC_ERR_INVALID_PTR;
            }

            // One BGRA input per async slot prevents a later submission from
            // overwriting the video-processor source of a frame still in the
            // GPU queue. The copy remains GPU-only.
            context->CopyResource(
                slot.VideoInputTexture.Get(),
                source);
            SignalPreprocessCopyProbe();

            D3D11_VIDEO_PROCESSOR_STREAM stream{};
            stream.Enable = TRUE;
            stream.pInputSurface =
                slot.VideoInputView.Get();

            const HRESULT hr =
                m_videoContext->VideoProcessorBlt(
                    m_videoProcessor.Get(),
                    slot.VideoOutputView.Get(),
                    0,
                    1,
                    &stream);

            if (SUCCEEDED(hr))
                SignalVideoProcessorProbe();

            return SUCCEEDED(hr)
                ? NV_ENC_SUCCESS
                : NV_ENC_ERR_GENERIC;
        }

        NVENCSTATUS CreateSlot(
            ID3D11Device* device,
            Slot& slot)
        {
            NVENCSTATUS status = NV_ENC_SUCCESS;

            if (!m_useExternalArgbInput)
            {
                D3D11_TEXTURE2D_DESC textureDesc{};
                textureDesc.Width = m_width;
                textureDesc.Height = m_height;
                textureDesc.MipLevels = 1;
                textureDesc.ArraySize = 1;
                textureDesc.Format = m_useNv12Preprocess
                    ? DXGI_FORMAT_NV12
                    : DXGI_FORMAT_B8G8R8A8_UNORM;
                textureDesc.SampleDesc.Count = 1;
                textureDesc.Usage = D3D11_USAGE_DEFAULT;
                textureDesc.BindFlags = D3D11_BIND_RENDER_TARGET;

                m_lastStage = L"ID3D11Device::CreateTexture2D";
                HRESULT hr = device->CreateTexture2D(
                    &textureDesc,
                    nullptr,
                    slot.Texture.GetAddressOf());
                if (FAILED(hr))
                    return NV_ENC_ERR_OUT_OF_MEMORY;

                if (m_useArgbScalePreprocess)
                {
                    hr = device->CreateRenderTargetView(
                        slot.Texture.Get(),
                        nullptr,
                        slot.ScaleOutputView.GetAddressOf());
                    if (FAILED(hr))
                        return NV_ENC_ERR_OUT_OF_MEMORY;
                }

                NV_ENC_REGISTER_RESOURCE registerResource{};
                registerResource.version = NV_ENC_REGISTER_RESOURCE_VER;
                registerResource.resourceType = NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX;
                registerResource.width = m_width;
                registerResource.height = m_height;
                registerResource.pitch = 0;
                registerResource.subResourceIndex = 0;
                registerResource.resourceToRegister = slot.Texture.Get();
                registerResource.bufferFormat = m_useNv12Preprocess
                    ? NV_ENC_BUFFER_FORMAT_NV12
                    : NV_ENC_BUFFER_FORMAT_ARGB;

                m_lastStage = L"nvEncRegisterResource";
                status = m_api.nvEncRegisterResource(m_encoder, &registerResource);
                if (status != NV_ENC_SUCCESS)
                    return status;
                slot.RegisteredInput = registerResource.registeredResource;

                if (m_useNv12Preprocess)
                {
                    D3D11_TEXTURE2D_DESC inputDesc{};
                    inputDesc.Width = m_sourceWidth;
                    inputDesc.Height = m_sourceHeight;
                    inputDesc.MipLevels = 1;
                    inputDesc.ArraySize = 1;
                    inputDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
                    inputDesc.SampleDesc.Count = 1;
                    inputDesc.Usage = D3D11_USAGE_DEFAULT;
                    inputDesc.BindFlags = D3D11_BIND_RENDER_TARGET;

                    HRESULT inputHr = device->CreateTexture2D(
                        &inputDesc,
                        nullptr,
                        slot.VideoInputTexture.GetAddressOf());
                    if (FAILED(inputHr))
                        return NV_ENC_ERR_OUT_OF_MEMORY;

                    D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC inputView{};
                    inputView.FourCC = 0;
                    inputView.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
                    inputView.Texture2D.MipSlice = 0;
                    inputView.Texture2D.ArraySlice = 0;
                    inputHr = m_videoDevice->CreateVideoProcessorInputView(
                        slot.VideoInputTexture.Get(),
                        m_videoEnumerator.Get(),
                        &inputView,
                        slot.VideoInputView.GetAddressOf());
                    if (FAILED(inputHr))
                        return NV_ENC_ERR_UNSUPPORTED_PARAM;

                    D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC outputView{};
                    outputView.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
                    outputView.Texture2D.MipSlice = 0;
                    const HRESULT outputHr = m_videoDevice->CreateVideoProcessorOutputView(
                        slot.Texture.Get(),
                        m_videoEnumerator.Get(),
                        &outputView,
                        slot.VideoOutputView.GetAddressOf());
                    if (FAILED(outputHr))
                        return NV_ENC_ERR_UNSUPPORTED_PARAM;
                }
            }

            NV_ENC_CREATE_BITSTREAM_BUFFER
                bitstream{};

            bitstream.version =
                NV_ENC_CREATE_BITSTREAM_BUFFER_VER;

            m_lastStage =
                L"nvEncCreateBitstreamBuffer";

            status =
                m_api.nvEncCreateBitstreamBuffer(
                    m_encoder,
                    &bitstream);

            if (status != NV_ENC_SUCCESS)
                return status;

            slot.Bitstream =
                bitstream.bitstreamBuffer;

            slot.Event =
                CreateEventW(
                    nullptr,
                    FALSE,
                    FALSE,
                    nullptr);

            if (!slot.Event)
                return NV_ENC_ERR_OUT_OF_MEMORY;

            NV_ENC_EVENT_PARAMS
                eventParams{};

            eventParams.version =
                NV_ENC_EVENT_PARAMS_VER;

            eventParams.completionEvent =
                slot.Event;

            m_lastStage =
                L"nvEncRegisterAsyncEvent";

            status =
                m_api.nvEncRegisterAsyncEvent(
                    m_encoder,
                    &eventParams);

            return status;
        }

        NVENCSTATUS DrainCompletedOutputs()
        {
            if (!m_encoder)
                return NV_ENC_ERR_ENCODER_NOT_INITIALIZED;

            constexpr DWORD kOutputWaitMs = 100;

            for (;;)
            {
                std::size_t slotIndex = SlotCount;
                {
                    std::lock_guard<std::mutex> pendingLock(m_pendingOutputMutex);
                    if (m_pendingOutputSlots.empty())
                        return NV_ENC_SUCCESS;
                    slotIndex = m_pendingOutputSlots.front();
                }

                if (slotIndex >= SlotCount)
                    return NV_ENC_ERR_GENERIC;

                auto& slot = m_slots[slotIndex];
                const DWORD waitResult = WaitForSingleObject(slot.Event, kOutputWaitMs);
                if (waitResult == WAIT_TIMEOUT)
                {
                    const auto ageMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                        std::chrono::steady_clock::now() - slot.SubmitTime).count();
                    if (ageMs > 1000)
                    {
                        // A one-second completion stall is fatal for this NVENC
                        // session, but it is not proof of a WDDM priority bug.
                        // Mark the encoder unhealthy so EncoderLoop rebuilds it
                        // on the next frame instead of accumulating a frozen FIFO.
                        m_lastStatus.store(HRESULT_FROM_WIN32(ERROR_TIMEOUT));
                        m_outputFatalStatus.store(NV_ENC_ERR_GENERIC);
                        return NV_ENC_ERR_GENERIC;
                    }
                    return NV_ENC_SUCCESS;
                }
                if (waitResult != WAIT_OBJECT_0)
                {
                    m_lastStatus.store(HRESULT_FROM_WIN32(GetLastError()));
                    return NV_ENC_ERR_GENERIC;
                }

                slot.State.store(SlotState::CompletionObserved);

                NV_ENC_LOCK_BITSTREAM lockParams{};
                lockParams.version = NV_ENC_LOCK_BITSTREAM_VER;
                lockParams.outputBitstream = slot.Bitstream;
                // Event completion already guarantees readiness. NVIDIA's
                // DirectX async guidance recommends a blocking lock here.
                lockParams.doNotWait = 0;

                const auto lockStart = std::chrono::steady_clock::now();
                const NVENCSTATUS status = m_api.nvEncLockBitstream(m_encoder, &lockParams);
                const auto lockEnd = std::chrono::steady_clock::now();
                const auto lockUs = std::chrono::duration_cast<std::chrono::microseconds>(
                    lockEnd - lockStart).count();
                m_lastLockBitstreamUs.store(lockUs);
                m_totalLockBitstreamUs.fetch_add(static_cast<std::uint64_t>(std::max<long long>(0, lockUs)));
                m_lockBitstreamSamples.fetch_add(1);

                if (status != NV_ENC_SUCCESS)
                {
                    if (status == NV_ENC_ERR_LOCK_BUSY)
                        m_lockBusyRetries.fetch_add(1);
                    m_lastStatus.store(NvencStatusToHresult(status));
                    return status;
                }

                const auto outputTime = std::chrono::steady_clock::now();
                const auto latencyUs = std::chrono::duration_cast<std::chrono::microseconds>(
                    outputTime - slot.SubmitTime).count();
                m_lastOutputLatencyUs.store(latencyUs);
                m_totalOutputLatencyUs.fetch_add(static_cast<std::uint64_t>(std::max<long long>(0, latencyUs)));

                const auto bytes = static_cast<std::uint64_t>(lockParams.bitstreamSizeInBytes);
                const bool keyFrame = lockParams.pictureType == NV_ENC_PIC_TYPE_IDR;
                if (m_sharedPacketQueue && m_sharedPacketQueue->IsActive() &&
                    lockParams.bitstreamBufferPtr && lockParams.bitstreamSizeInBytes > 0)
                {
                    const auto* data = static_cast<const std::uint8_t*>(lockParams.bitstreamBufferPtr);
                    QueuePacket(data, lockParams.bitstreamSizeInBytes, slot.FrameId, keyFrame);
                }

                (void)m_api.nvEncUnlockBitstream(m_encoder, slot.Bitstream);
                if (slot.MappedInput)
                {
                    (void)m_api.nvEncUnmapInputResource(m_encoder, slot.MappedInput);
                    slot.MappedInput = nullptr;
                }
                slot.MappedBufferFormat = NV_ENC_BUFFER_FORMAT_UNDEFINED;
                CompleteInputOwnership(slot);
                slot.FrameId = 0;
                slot.AcceptedStatus = NV_ENC_SUCCESS;
                slot.ForceIdr = false;
                slot.SubmitCpuUs = 0;
                slot.SubmitTime = {};
                slot.State.store(SlotState::Free);

                {
                    std::lock_guard<std::mutex> pendingLock(m_pendingOutputMutex);
                    if (!m_pendingOutputSlots.empty() &&
                        m_pendingOutputSlots.front() == slotIndex)
                    {
                        m_pendingOutputSlots.pop_front();
                    }
                }
                m_pendingOutputCv.notify_all();
                m_encodedFrames.fetch_add(1);
                m_encodedBytes.fetch_add(bytes);
                m_lastOutputProgressUs.store(
                    SteadyNowUs(),
                    std::memory_order_relaxed);
                m_lastStatus.store(S_OK);
            }
        }

        void OutputLoop()
        {
            m_outputWorkerAlive.store(true, std::memory_order_release);
            struct OutputWorkerGuard
            {
                std::atomic<bool>& Alive;
                ~OutputWorkerGuard() noexcept
                {
                    Alive.store(false, std::memory_order_release);
                }
            } outputWorkerGuard{ m_outputWorkerAlive };

            SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_ABOVE_NORMAL);

            while (!m_outputStop.load())
            {
                {
                    std::unique_lock<std::mutex> pendingLock(m_pendingOutputMutex);
                    m_pendingOutputCv.wait_for(
                        pendingLock,
                        std::chrono::milliseconds(20),
                        [this]()
                        {
                            return m_outputStop.load() || !m_pendingOutputSlots.empty();
                        });
                }

                if (m_outputStop.load())
                    break;

                const NVENCSTATUS status = DrainCompletedOutputs();
                if (status != NV_ENC_SUCCESS && status != NV_ENC_ERR_LOCK_BUSY)
                {
                    m_outputFatalStatus.store(status);
                    const HRESULT current =
                        static_cast<HRESULT>(m_lastStatus.load());
                    if (SUCCEEDED(current))
                        m_lastStatus.store(NvencStatusToHresult(status));
                    break;
                }
            }

            if (m_encoder && !m_outputStop.load())
                (void)DrainCompletedOutputs();
        }

        void QueuePacket(
            const std::uint8_t* data,
            std::uint32_t size,
            std::uint64_t frameId,
            bool keyFrame)
        {
            if (!data || size == 0 || !m_sharedPacketQueue ||
                !m_sharedPacketQueue->IsActive())
            {
                return;
            }

            EncodedPacket packet{};
            packet.Data.assign(data, data + size);
            packet.FrameId = frameId;
            packet.KeyFrame =
                keyFrame || IsH264KeyFrame(packet.Data);

            if (m_sharedPacketQueue->Push(std::move(packet)))
            {
                m_queueResets.fetch_add(1);
                m_forceIdr.store(true);
            }
        }

    private:
        HMODULE m_module = nullptr;

        NV_ENCODE_API_FUNCTION_LIST
            m_api{};

        void* m_encoder = nullptr;

        NV_ENC_CONFIG
            m_config{};

        std::array<Slot, SlotCount>
            m_slots{};

        std::atomic<std::size_t>
            m_nextSlot{ 0 };

        // Runtime-owned by the encoder worker. Slots enter this FIFO only
        // after NVENC has accepted the input.
        std::deque<std::size_t>
            m_pendingOutputSlots;
        std::mutex m_pendingOutputMutex;
        std::condition_variable m_pendingOutputCv;
        std::atomic<int> m_outputFatalStatus{ NV_ENC_SUCCESS };

        std::uint64_t m_nextSubmitSequence = 0;
        std::size_t m_retrySlotIndex = SlotCount;
        std::chrono::steady_clock::time_point m_retryNotBefore{};
        HANDLE m_eosEvent = nullptr;

        std::atomic<bool>
            m_running{ false };

        std::atomic<bool>
            m_outputStop{ true };

        std::thread m_outputWorker;
        std::atomic<bool> m_outputWorkerAlive{ false };
        std::atomic<long long> m_lastSubmitProgressUs{ 0 };
        std::atomic<long long> m_lastOutputProgressUs{ 0 };

        std::atomic<bool>
            m_forceIdr{ false };

        std::shared_ptr<SharedEncodedPacketQueue>
            m_sharedPacketQueue;

        EncodedQueueAdmissionPolicy m_queueAdmissionPolicy;

        std::atomic<long>
            m_lastStatus{ S_OK };

        std::atomic<std::uint64_t>
            m_submittedFrames{ 0 };

        std::atomic<std::uint64_t>
            m_encodedFrames{ 0 };

        std::atomic<std::uint64_t>
            m_encodedBytes{ 0 };

        std::atomic<std::uint64_t>
            m_pacingDrops{ 0 };

        std::atomic<std::uint64_t>
            m_needMoreInputAccepted{ 0 };

        std::atomic<std::uint64_t>
            m_encoderBusyRetries{ 0 };

        std::atomic<std::uint64_t>
            m_noFreeSlotDrops{ 0 };

        std::atomic<std::uint64_t>
            m_flushTimeouts{ 0 };

        std::atomic<std::uint64_t>
            m_queueResets{ 0 };

        std::atomic<long long>
            m_lastSubmitUs{ 0 };

        std::atomic<std::uint64_t>
            m_totalSubmitUs{ 0 };

        std::atomic<long long>
            m_lastOutputLatencyUs{ 0 };

        std::atomic<std::uint64_t>
            m_totalOutputLatencyUs{ 0 };

        std::atomic<long long>
            m_lastPreprocessUs{ 0 };

        std::atomic<std::uint64_t>
            m_totalPreprocessUs{ 0 };

        std::atomic<std::uint64_t>
            m_preprocessSamples{ 0 };

        std::atomic<long long>
            m_lastMapUs{ 0 };

        std::atomic<std::uint64_t>
            m_totalMapUs{ 0 };

        std::atomic<std::uint64_t>
            m_mapSamples{ 0 };

        std::atomic<long long>
            m_lastEncodeCallUs{ 0 };

        std::atomic<std::uint64_t>
            m_totalEncodeCallUs{ 0 };

        std::atomic<std::uint64_t>
            m_encodeCallSamples{ 0 };

        std::atomic<long long>
            m_lastLockBitstreamUs{ 0 };

        std::atomic<std::uint64_t>
            m_totalLockBitstreamUs{ 0 };

        std::atomic<std::uint64_t>
            m_lockBitstreamSamples{ 0 };

        std::atomic<std::uint64_t>
            m_mapFailures{ 0 };

        std::atomic<std::uint64_t>
            m_encodeErrors{ 0 };

        std::atomic<std::uint64_t>
            m_lockBusyRetries{ 0 };

        // CORE 11.12: non-blocking encode-device GPU completion probes.
        ComPtr<ID3D11DeviceContext4> m_preprocessProbeContext4;
        ComPtr<ID3D11Fence> m_preprocessCopyProbeFence;
        ComPtr<ID3D11Fence> m_videoProcessorProbeFence;
        std::uint64_t m_preprocessCopyProbeSequence = 0;
        std::uint64_t m_preprocessCopyProbeCompleted = 0;
        std::uint32_t m_preprocessCopyProbeMaxPending = 0;
        std::deque<ProbePendingEntry> m_preprocessCopyProbePending;
        std::uint64_t m_videoProcessorProbeSequence = 0;
        std::uint64_t m_videoProcessorProbeCompleted = 0;
        std::uint32_t m_videoProcessorProbeMaxPending = 0;
        std::deque<ProbePendingEntry> m_videoProcessorProbePending;

        bool m_useNv12Preprocess = false;
        bool m_useExternalArgbInput = false;
        bool m_useArgbScalePreprocess = false;
        std::vector<ExternalInputRegistration> m_externalInputs;
        std::vector<ScaleSourceView> m_argbScaleSourceViews;
        ComPtr<ID3D11VertexShader> m_argbScaleVs;
        ComPtr<ID3D11PixelShader> m_argbScalePs;
        ComPtr<ID3D11SamplerState> m_argbScaleSampler;
        std::uint32_t m_requestedInputMode = LAB_MODE_AUTO;
        ComPtr<ID3D11VideoDevice>
            m_videoDevice;
        ComPtr<ID3D11VideoContext>
            m_videoContext;
        ComPtr<ID3D11VideoProcessorEnumerator>
            m_videoEnumerator;
        ComPtr<ID3D11VideoProcessor>
            m_videoProcessor;

        std::uint32_t m_sourceWidth = 0;
        std::uint32_t m_sourceHeight = 0;
        std::uint32_t m_width = 0;
        std::uint32_t m_height = 0;
        std::uint32_t m_fps = 0;
        std::uint32_t m_bitrateKbps = 0;
        std::uint32_t m_effectivePreset = 0;
        GUID m_presetGuid =
            NV_ENC_PRESET_P2_GUID;
        NV_ENC_TUNING_INFO m_tuningInfo =
            NV_ENC_TUNING_INFO_LOW_LATENCY;

        std::wstring m_lastStage =
            L"Não iniciado";

        std::wstring m_inputPathDiagnostic =
            L"Não validado";
    };

#endif

    class DesktopCaptureSession
    {
    public:
        DesktopCaptureSession() = default;

        ~DesktopCaptureSession()
        {
            TraceNativeTeardown(L"DESTROY_ENTER", this);
            Stop();
            TraceNativeTeardown(L"DESTROY_COMPLETE", this);
        }

        HRESULT Initialize(
            const wchar_t* deviceName)
        {
            if (!deviceName ||
                deviceName[0] == L'\0')
            {
                return E_INVALIDARG;
            }

            m_deviceName =
                deviceName;

            HRESULT hr =
                FindOutputAndAdapter(
                    deviceName,
                    m_adapter,
                    m_output);

            if (FAILED(hr))
                return hr;

            hr =
                D3D11CreateDevice(
                    m_adapter.Get(),
                    D3D_DRIVER_TYPE_UNKNOWN,
                    nullptr,
                    D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                    nullptr,
                    0,
                    D3D11_SDK_VERSION,
                    m_device.GetAddressOf(),
                    nullptr,
                    m_context.GetAddressOf());

            if (FAILED(hr))
                return hr;

            // Core 09.3.2: capture and encode run on independent CPU threads.
            // Protect the immediate context so D3D11 serializes command-list
            // access safely instead of relying on foreground scheduling luck.
            {
                ComPtr<ID3D11Multithread> multithread;
                if (SUCCEEDED(
                        m_context->QueryInterface(
                            IID_PPV_ARGS(multithread.GetAddressOf()))))
                {
                    multithread->SetMultithreadProtected(TRUE);
                }
            }

            ConfigureCaptureFrameLatency(
                m_device.Get());

            // CORE 11.17e: inherit the 11.17d A/B context3 cache so the
            // same-device WGC copy path can explicitly submit COPY commands
            // with Flush1 without blocking for completion. If unavailable,
            // streaming remains behaviorally identical to 11.17c.
            (void)m_context.As(&m_context3);

            // Core 09.3.2: keep CPU QoS stable at all times. Process-level GPU
            // scheduling is raised to HIGH only while streaming.
            DisableBackgroundPowerThrottling();

            hr =
                CreateDesktopDuplication(
                    m_output.Get());

            if (FAILED(hr))
                return hr;

            DXGI_OUTDUPL_DESC
                duplicationDesc{};

            m_duplication->GetDesc(
                &duplicationDesc);

            m_sourceWidth =
                duplicationDesc.ModeDesc.Width;

            m_sourceHeight =
                duplicationDesc.ModeDesc.Height;

            if (m_sourceWidth == 0 ||
                m_sourceHeight == 0)
            {
                return E_FAIL;
            }

            CalculatePreviewSize(
                m_sourceWidth,
                m_sourceHeight,
                m_previewWidth,
                m_previewHeight);

            m_previewStride =
                m_previewWidth * 4u;

            const std::uint64_t
                bufferBytes =
                    static_cast<std::uint64_t>(
                        m_previewStride) *
                    static_cast<std::uint64_t>(
                        m_previewHeight);

            if (bufferBytes > UINT32_MAX)
                return E_OUTOFMEMORY;

            m_requiredBufferBytes =
                static_cast<std::uint32_t>(
                    bufferBytes);

            try
            {
                m_previewBuffer.resize(
                    m_requiredBufferBytes);
            }
            catch (...)
            {
                return E_OUTOFMEMORY;
            }

            m_stop.store(false);
            m_lastError.store(S_OK);
            m_frameCount.store(0);
            m_previewFrameId = 0;

            m_lastPreviewGenerated =
                std::chrono::steady_clock::now() -
                std::chrono::milliseconds(100);

            try
            {
                m_worker =
                    std::thread(
                        &DesktopCaptureSession::CaptureLoop,
                        this);
            }
            catch (...)
            {
                return E_FAIL;
            }

            return S_OK;
        }

        void Stop()
        {
            TraceNativeTeardown(L"STOP_ENTER", this);
            m_stop.store(true);

            TraceNativeTeardown(L"STOP_ENCODER_BEGIN", this);
            StopEncoder();
            TraceNativeTeardown(L"STOP_ENCODER_END", this);

            if (m_worker.joinable())
            {
                TraceNativeTeardown(L"CAPTURE_WORKER_JOIN_BEGIN", this);
                m_worker.join();
                TraceNativeTeardown(L"CAPTURE_WORKER_JOIN_END", this);
            }

            // Capture thread is now stopped, so shared resources can be safely
            // destroyed before either D3D11 device is released.
            TraceNativeTeardown(L"CORE10_RING_RESET_BEGIN", this);
            m_core10Ring.Reset();
            TraceNativeTeardown(L"CORE10_RING_RESET_END", this);

            TraceNativeTeardown(L"ENCODE_CONTEXT_RESET_BEGIN", this);
            m_encodeContext.Reset();
            TraceNativeTeardown(L"ENCODE_CONTEXT_RESET_END", this);

            TraceNativeTeardown(L"ENCODE_DEVICE_RESET_BEGIN", this);
            m_encodeDevice.Reset();
            TraceNativeTeardown(L"ENCODE_DEVICE_RESET_END", this);

            TraceNativeTeardown(L"STAGING_RESET_BEGIN", this);
            m_stagingTexture.Reset();
            TraceNativeTeardown(L"STAGING_RESET_END", this);

            TraceNativeTeardown(L"LATEST_RESET_BEGIN", this);
            m_latestTexture.Reset();
            TraceNativeTeardown(L"LATEST_RESET_END", this);

            TraceNativeTeardown(L"CAPTURE_RING_RESET_BEGIN", this);
            {
                std::lock_guard<std::mutex> ringLock(m_captureRingMutex);
                for (auto& slot : m_captureRing)
                    slot.Texture.Reset();
                m_captureRingWidth = 0;
                m_captureRingHeight = 0;
                m_captureRingFormat = DXGI_FORMAT_UNKNOWN;
            }
            TraceNativeTeardown(L"CAPTURE_RING_RESET_END", this);

            TraceNativeTeardown(L"DUPLICATION_RESET_BEGIN", this);
            m_duplication.Reset();
            TraceNativeTeardown(L"DUPLICATION_RESET_END", this);

            // StopEncoder() normally clears these resources already. Repeat the
            // operation here deliberately: teardown must be idempotent and no
            // legacy shader object may outlive the capture D3D11 context/device.
            TraceNativeTeardown(L"LEGACY_SCALER_RESET_BEGIN", this);
            ResetEncoderScaleResources();
            TraceNativeTeardown(L"LEGACY_SCALER_RESET_END", this);

            TraceNativeTeardown(L"D3D_CONTEXT_RESET_BEGIN", this);
            m_context3.Reset();
            m_context.Reset();
            TraceNativeTeardown(L"D3D_CONTEXT_RESET_END", this);

            TraceNativeTeardown(L"D3D_DEVICE_RESET_BEGIN", this);
            m_device.Reset();
            TraceNativeTeardown(L"D3D_DEVICE_RESET_END", this);

            TraceNativeTeardown(L"OUTPUT_RESET_BEGIN", this);
            m_output.Reset();
            TraceNativeTeardown(L"OUTPUT_RESET_END", this);

            TraceNativeTeardown(L"ADAPTER_RESET_BEGIN", this);
            m_adapter.Reset();
            TraceNativeTeardown(L"ADAPTER_RESET_END", this);

            TraceNativeTeardown(L"PREVIEW_CLEAR_BEGIN", this);
            {
                std::lock_guard<std::mutex> lock(
                    m_previewMutex);

                m_previewBuffer.clear();
                m_previewFrameId = 0;
            }
            TraceNativeTeardown(L"PREVIEW_CLEAR_END", this);
            TraceNativeTeardown(L"STOP_COMPLETE", this);
        }

        std::uint32_t SourceWidth() const noexcept
        {
            return m_sourceWidth;
        }

        std::uint32_t SourceHeight() const noexcept
        {
            return m_sourceHeight;
        }

        std::uint32_t PreviewWidth() const noexcept
        {
            return m_previewWidth;
        }

        std::uint32_t PreviewHeight() const noexcept
        {
            return m_previewHeight;
        }

        std::uint32_t PreviewStride() const noexcept
        {
            return m_previewStride;
        }

        std::uint32_t RequiredBufferBytes() const noexcept
        {
            return m_requiredBufferBytes;
        }

        std::uint64_t FrameCount() const noexcept
        {
            return m_frameCount.load();
        }

        HRESULT LastError() const noexcept
        {
            return static_cast<HRESULT>(
                m_lastError.load());
        }

        HRESULT GetCapturePipelineStats(
            MV_CapturePipelineStats* stats) const noexcept
        {
            if (!stats)
                return E_INVALIDARG;

            *stats = {};

            // CORE 11.1D DIAG: normal managed calls serialize Start/Stop, but
            // the shutdown diagnostic intentionally polls while StopEncoder owns
            // the lifecycle mutex. Never wait on that mutex here: if it is busy,
            // return only fields backed by atomics/WGC's atomic Snapshot so the
            // UI can keep displaying the exact blocking checkpoint without
            // racing non-atomic encoder configuration fields.
            std::unique_lock<std::mutex> lifecycleProbe(
                m_encoderLifecycleMutex,
                std::try_to_lock);

            if (!lifecycleProbe.owns_lock())
            {
                const auto wgc = m_wgcCapture.Snapshot();
                stats->CaptureBackend = 1u;
                stats->BackendFramesArrived = wgc.FramesArrived;
                stats->BackendFramesPublished = m_wgcFramesPublished.load();
                stats->BackendPacingDrops = m_wgcPacingDrops.load();
                stats->BackendEmptyCallbacks = wgc.EmptyCallbacks;
                stats->BackendRestarts = wgc.Restarts;
                stats->LastBackendCallbackMs = wgc.LastCallbackMs;
                stats->AverageBackendCallbackMs = wgc.AverageCallbackMs;
                stats->LastBackendFrameAgeMs = wgc.LastFrameAgeMs;
                stats->AverageBackendFrameAgeMs = wgc.AverageFrameAgeMs;
                stats->LastBackendStatus = static_cast<std::int32_t>(wgc.LastStatus);
                stats->ReservedBackend = wgc.StartStage;
                stats->ReservedBackendStatus = static_cast<std::int32_t>(wgc.LastStartStatus);
                stats->BackendRunning = wgc.Running ? 1u : 0u;
                stats->BackendCallbacksInFlight = wgc.CallbacksInFlight;
                stats->BackendStopInProgress = wgc.StopInProgress ? 1u : 0u;
                stats->BackendStopStage = wgc.StopStage;
                stats->BackendLastCallbackAgoMs = wgc.LastCallbackAgoMs;
                stats->BackendLastCallbackCompleteAgoMs = wgc.LastCallbackCompleteAgoMs;
                stats->BackendStopStageAgeMs = wgc.StopStageAgeMs;
                stats->LastPublishAgoMs = AgeMsFrom(
                    m_lastPublishProgressUs.load(std::memory_order_relaxed));
                stats->LastConsumeAgoMs = AgeMsFrom(
                    m_lastConsumeProgressUs.load(std::memory_order_relaxed));
                stats->EncoderWorkerAlive =
                    m_encoderWorkerAlive.load(std::memory_order_acquire) ? 1u : 0u;
                stats->BackendCallbackStage = wgc.CallbackStage;
                stats->BackendCallbackSubstage = wgc.CallbackSubstage;
                stats->BackendCurrentDrainFrames = wgc.CurrentDrainFrames;
                stats->BackendCallbackStageAgeMs = wgc.CallbackStageAgeMs;
                stats->CoreWgcStage = m_coreWgcStage.load(std::memory_order_acquire);
                stats->RingPublishStage = m_core10Ring.DiagnosticStageValue();
                stats->EncoderWorkerStage = m_encoderWorkerStage.load(std::memory_order_acquire);
                stats->CoreWgcStageAgeMs = AgeMsFrom(
                    m_coreWgcStageChangedUs.load(std::memory_order_relaxed));
                stats->RingPublishStageAgeMs = m_core10Ring.DiagnosticStageAgeMs();
                stats->EncoderWorkerStageAgeMs = AgeMsFrom(
                    m_encoderWorkerStageChangedUs.load(std::memory_order_relaxed));
                stats->Reserved0 = static_cast<std::int32_t>(
                    m_core11IngressFps.load(std::memory_order_relaxed));
                stats->ReservedLiveness = static_cast<std::uint32_t>(
                    std::min<std::uint64_t>(
                        m_remoteClockLateTicks.load(std::memory_order_relaxed),
                        0xFFFFFFFFull));
                stats->ReservedPacing = m_core10Ring.InitStageValue();
                stats->ReservedStageDiag = static_cast<std::uint32_t>(
                    m_core10Ring.LastInitStatusValue());
                stats->BackendFramesDelivered = wgc.FramesDelivered;
                stats->BackendPendingFramesReplaced = wgc.ReplacedPendingFrames;
                stats->BackendDeliveryWorkerAlive = wgc.DeliveryWorkerAlive ? 1u : 0u;
                stats->BackendLastDeliveryDelayMs = wgc.LastDeliveryDelayMs;
                stats->AdaptivePressureState =
                    m_adaptivePressureState.load(std::memory_order_relaxed);
                stats->AdaptiveTargetFps =
                    m_adaptivePressureTargetFps.load(std::memory_order_relaxed);
                stats->AdaptivePressureStreak =
                    m_adaptivePressureStreak.load(std::memory_order_relaxed);
                stats->AdaptiveHealthyStreak =
                    m_adaptiveHealthyStreak.load(std::memory_order_relaxed);
                stats->AdaptiveTransitions =
                    m_adaptiveTransitions.load(std::memory_order_relaxed);
                stats->CaptureRingOccupiedFrames =
                    m_adaptiveRingOccupied.load(std::memory_order_relaxed);
                stats->CopyGpuProbeSignals = m_copyGpuProbeSignals.load(std::memory_order_relaxed);
                stats->CopyGpuProbeCompleted = m_copyGpuProbeCompleted.load(std::memory_order_relaxed);
                stats->CopyGpuReadyAtSubmit = m_copyGpuReadyAtSubmit.load(std::memory_order_relaxed);
                stats->CopyGpuNotReadyAtSubmit = m_copyGpuNotReadyAtSubmit.load(std::memory_order_relaxed);
                stats->CopyGpuProbePending = CaptureCopyGpuPendingCount();
                stats->CopyGpuProbeMaxPending = m_copyGpuProbeMaxPending.load(std::memory_order_relaxed);
                stats->CopyGpuLastLatencyMs = m_copyGpuLastLatencyUs.load(std::memory_order_relaxed) / 1000.0;
                const auto copyGpuCompleted = m_copyGpuProbeCompleted.load(std::memory_order_relaxed);
                stats->CopyGpuAverageLatencyMs = copyGpuCompleted != 0
                    ? (m_copyGpuTotalLatencyUs.load(std::memory_order_relaxed) / 1000.0) /
                        static_cast<double>(copyGpuCompleted)
                    : 0.0;
                stats->CopyGpuMaxLatencyMs = m_copyGpuMaxLatencyUs.load(std::memory_order_relaxed) / 1000.0;
                stats->CopyGpuOldestPendingMs = CaptureCopyGpuOldestPendingMs();
                stats->CopyGpuProbeStatus = m_copyGpuProbeStatus.load(std::memory_order_relaxed);
                return S_OK;
            }

            stats->DesktopPresents = m_desktopPresents.load();
            stats->PointerOnlyUpdates = m_pointerOnlyUpdates.load();
            stats->DxgiAccumulatedFrames = m_dxgiAccumulatedFrames.load();
            stats->DxgiMissedFrames = m_dxgiMissedFrames.load();
            stats->PublishedToRing = m_ringPublished.load();
            stats->ReplacedInRing = m_ringReplaced.load();
            stats->RingBusyDrops = m_ringBusyDrops.load();
            stats->ConsumedByEncoder = m_ringConsumed.load();
            stats->PublishPacingDrops = m_ringPacingDrops.load();
            stats->LastAccumulatedFrames = m_lastAccumulatedFrames.load();
            const bool core10Active =
                m_encoderActive.load() &&
                IsCore10IsolatedMode(m_encoderLabInputMode);
            stats->RingReadyFrames = core10Active
                ? m_core10Ring.ReadyCount()
                : RingReadyCount();
            stats->TargetPublishFps = m_capturePublishFps.load();
            stats->LastAcquireMs = m_lastAcquireUs.load() / 1000.0;
            stats->AverageAcquireMs = AverageUs(
                m_totalAcquireUs.load(),
                m_acquireSamples.load());
            stats->LastRingCopyMs = m_lastRingCopyUs.load() / 1000.0;
            stats->AverageRingCopyMs = AverageUs(
                m_totalRingCopyUs.load(),
                m_ringPublished.load());
            stats->AcquireTimeouts = m_acquireTimeouts.load();
            stats->DuplicationResets = m_duplicationResets.load();
            stats->EncoderRestarts = m_encoderRestarts.load();
            stats->LastCaptureStatus = m_lastError.load();
            stats->Reserved0 = static_cast<std::int32_t>(
                m_core11IngressFps.load(std::memory_order_relaxed));

            const auto shared = m_core10Ring.Snapshot();
            stats->SharedCaptureSyncTimeouts = shared.CaptureSyncTimeouts;
            stats->SharedEncodeSyncTimeouts = shared.EncodeSyncTimeouts;
            stats->SharedSyncAbandoned = shared.SyncAbandoned;
            stats->Core10IsolationActive = core10Active ? 1u : 0u;
            stats->SharedReadyFrames = shared.ReadyFrames;
            stats->SharedCaptureWaitMs = shared.LastCaptureWaitMs;
            stats->SharedAverageCaptureWaitMs = shared.AverageCaptureWaitMs;
            stats->SharedEncodeWaitMs = shared.LastEncodeWaitMs;
            stats->SharedAverageEncodeWaitMs = shared.AverageEncodeWaitMs;
            stats->RingAcquireCalls = shared.AcquireCalls;
            stats->RingAcquireNoReadyFrames = shared.AcquireNoReadyFrames;
            stats->RingAcquireReadySelections = shared.AcquireReadySelections;
            stats->RingCaptureFenceReady = shared.AcquireFenceReady;
            stats->RingCaptureFencePending = shared.AcquireFencePending;
            stats->RingPublishNoReusableSlots = shared.PublishNoReusableSlots;
            stats->RingAcquireWaitMs = shared.LastAcquireWaitMs;
            stats->RingAverageAcquireWaitMs = shared.AverageAcquireWaitMs;
            stats->RingLeaseAgeMs = shared.LastLeaseAgeMs;
            stats->RingAverageLeaseAgeMs = shared.AverageLeaseAgeMs;
            stats->RingCaptureFenceLag = shared.LastCaptureFenceLag;
            stats->RingMaxCaptureFenceLag = shared.MaxCaptureFenceLag;
            stats->RingReleaseFencePendingSlots = shared.ReleaseFencePendingSlots;

            const bool wgcActive =
                m_encoderActive.load() &&
                (m_encoderLabInputMode == CORE10_MODE_WGC_BGRA ||
                 m_encoderLabInputMode == CORE11_MODE_WGC_NV12 ||
                 m_encoderLabInputMode == CORE11_MODE_WGC_DIRECT_RGB ||
                 m_encoderLabInputMode == CORE11_MODE_WGC_RGB_ENCODE_SCALE ||
                 m_encoderLabInputMode == CORE11_MODE_WGC_NATIVE_RGB ||
                 m_encoderLabInputMode == CORE11_MODE_WGC_SAME_DEVICE_DIRECT_RGB);
            const auto wgc = m_wgcCapture.Snapshot();
            stats->CaptureBackend = wgcActive ? 1u : 0u;
            stats->BackendFramesArrived = wgcActive
                ? wgc.FramesArrived
                : m_desktopPresents.load();
            stats->BackendFramesPublished = wgcActive
                ? m_wgcFramesPublished.load()
                : m_ringPublished.load();
            stats->BackendPacingDrops = wgcActive
                ? m_wgcPacingDrops.load()
                : m_ringPacingDrops.load();
            stats->BackendEmptyCallbacks = wgcActive
                ? wgc.EmptyCallbacks
                : 0u;
            stats->BackendRestarts = wgcActive
                ? wgc.Restarts
                : m_duplicationResets.load();
            stats->LastBackendCallbackMs = wgcActive
                ? wgc.LastCallbackMs
                : stats->LastAcquireMs;
            stats->AverageBackendCallbackMs = wgcActive
                ? wgc.AverageCallbackMs
                : stats->AverageAcquireMs;
            stats->LastBackendFrameAgeMs = wgcActive
                ? wgc.LastFrameAgeMs
                : 0.0;
            stats->AverageBackendFrameAgeMs = wgcActive
                ? wgc.AverageFrameAgeMs
                : 0.0;
            stats->LastBackendStatus = wgcActive
                ? static_cast<std::int32_t>(wgc.LastStatus)
                : m_lastError.load();
            stats->ReservedBackend = wgc.StartStage;
            stats->ReservedBackendStatus = static_cast<std::int32_t>(wgc.LastStartStatus);

            stats->BackendRunning = wgc.Running ? 1u : 0u;
            stats->BackendCallbacksInFlight = wgc.CallbacksInFlight;
            stats->BackendStopInProgress = wgc.StopInProgress ? 1u : 0u;
            stats->BackendStopStage = wgc.StopStage;
            stats->BackendLastCallbackAgoMs = wgc.LastCallbackAgoMs;
            stats->BackendLastCallbackCompleteAgoMs = wgc.LastCallbackCompleteAgoMs;
            stats->BackendStopStageAgeMs = wgc.StopStageAgeMs;
            stats->LastPublishAgoMs = AgeMsFrom(
                m_lastPublishProgressUs.load(std::memory_order_relaxed));
            stats->LastConsumeAgoMs = AgeMsFrom(
                m_lastConsumeProgressUs.load(std::memory_order_relaxed));
            stats->EncoderWorkerAlive =
                m_encoderWorkerAlive.load(std::memory_order_acquire) ? 1u : 0u;
            stats->ReservedLiveness = static_cast<std::uint32_t>(
                std::min<std::uint64_t>(
                    m_remoteClockLateTicks.load(std::memory_order_relaxed),
                    0xFFFFFFFFull));
            stats->BackendCallbackStage = wgc.CallbackStage;
            stats->BackendCallbackSubstage = wgc.CallbackSubstage;
            stats->BackendCurrentDrainFrames = wgc.CurrentDrainFrames;
            stats->BackendCallbackStageAgeMs = wgc.CallbackStageAgeMs;
            stats->CoreWgcStage = m_coreWgcStage.load(std::memory_order_acquire);
            stats->RingPublishStage = m_core10Ring.DiagnosticStageValue();
            stats->EncoderWorkerStage = m_encoderWorkerStage.load(std::memory_order_acquire);
            stats->CoreWgcStageAgeMs = AgeMsFrom(
                m_coreWgcStageChangedUs.load(std::memory_order_relaxed));
            stats->RingPublishStageAgeMs = m_core10Ring.DiagnosticStageAgeMs();
            stats->EncoderWorkerStageAgeMs = AgeMsFrom(
                m_encoderWorkerStageChangedUs.load(std::memory_order_relaxed));
            stats->ReservedPacing = m_core10Ring.InitStageValue();
            stats->ReservedStageDiag = static_cast<std::uint32_t>(
                m_core10Ring.LastInitStatusValue());
            stats->BackendFramesDelivered = wgcActive ? wgc.FramesDelivered : stats->BackendFramesArrived;
            stats->BackendPendingFramesReplaced = wgcActive ? wgc.ReplacedPendingFrames : 0u;
            stats->BackendDeliveryWorkerAlive = wgc.DeliveryWorkerAlive ? 1u : 0u;
            stats->BackendLastDeliveryDelayMs = wgcActive ? wgc.LastDeliveryDelayMs : 0.0;
            stats->AdaptivePressureState =
                m_adaptivePressureState.load(std::memory_order_relaxed);
            stats->AdaptiveTargetFps =
                m_adaptivePressureTargetFps.load(std::memory_order_relaxed);
            stats->AdaptivePressureStreak =
                m_adaptivePressureStreak.load(std::memory_order_relaxed);
            stats->AdaptiveHealthyStreak =
                m_adaptiveHealthyStreak.load(std::memory_order_relaxed);
            stats->AdaptiveTransitions =
                m_adaptiveTransitions.load(std::memory_order_relaxed);
            stats->CaptureRingOccupiedFrames = core10Active
                ? stats->SharedReadyFrames
                : CaptureRingOccupiedCount();
            stats->CopyGpuProbeSignals = m_copyGpuProbeSignals.load(std::memory_order_relaxed);
            stats->CopyGpuProbeCompleted = m_copyGpuProbeCompleted.load(std::memory_order_relaxed);
            stats->CopyGpuReadyAtSubmit = m_copyGpuReadyAtSubmit.load(std::memory_order_relaxed);
            stats->CopyGpuNotReadyAtSubmit = m_copyGpuNotReadyAtSubmit.load(std::memory_order_relaxed);
            stats->CopyGpuProbePending = CaptureCopyGpuPendingCount();
            stats->CopyGpuProbeMaxPending = m_copyGpuProbeMaxPending.load(std::memory_order_relaxed);
            stats->CopyGpuLastLatencyMs = m_copyGpuLastLatencyUs.load(std::memory_order_relaxed) / 1000.0;
            const auto copyGpuCompleted = m_copyGpuProbeCompleted.load(std::memory_order_relaxed);
            stats->CopyGpuAverageLatencyMs = copyGpuCompleted != 0
                ? (m_copyGpuTotalLatencyUs.load(std::memory_order_relaxed) / 1000.0) /
                    static_cast<double>(copyGpuCompleted)
                : 0.0;
            stats->CopyGpuMaxLatencyMs = m_copyGpuMaxLatencyUs.load(std::memory_order_relaxed) / 1000.0;
            stats->CopyGpuOldestPendingMs = CaptureCopyGpuOldestPendingMs();
            stats->CopyGpuProbeStatus = m_copyGpuProbeStatus.load(std::memory_order_relaxed);
            return S_OK;
        }

        HRESULT CopyLatestPreview(
            std::uint8_t* destination,
            std::uint32_t destinationBytes,
            std::uint32_t* previewWidth,
            std::uint32_t* previewHeight,
            std::uint32_t* previewStride,
            std::uint64_t* frameId)
        {
            if (!destination ||
                !previewWidth ||
                !previewHeight ||
                !previewStride ||
                !frameId)
            {
                return E_INVALIDARG;
            }

            if (destinationBytes <
                m_requiredBufferBytes)
            {
                return HRESULT_FROM_WIN32(
                    ERROR_INSUFFICIENT_BUFFER);
            }

            // Preview é diagnóstico. Só pedimos uma nova cópia GPU->CPU
            // quando a UI realmente tentou ler o preview. Durante streaming
            // isto evita Map/CopyResource periódicos competindo com NVENC.
            m_previewRequested.store(true);

            std::lock_guard<std::mutex> lock(
                m_previewMutex);

            if (m_previewFrameId == 0 ||
                m_previewBuffer.size() <
                    m_requiredBufferBytes)
            {
                return S_FALSE;
            }

            std::memcpy(
                destination,
                m_previewBuffer.data(),
                m_requiredBufferBytes);

            *previewWidth =
                m_previewWidth;

            *previewHeight =
                m_previewHeight;

            *previewStride =
                m_previewStride;

            *frameId =
                m_previewFrameId;

            return S_OK;
        }

        void SetCaptureContextMultithreadProtection(bool enabled) noexcept
        {
            if (!m_context)
                return;

            ComPtr<ID3D11Multithread> multithread;
            if (SUCCEEDED(m_context->QueryInterface(
                    IID_PPV_ARGS(multithread.GetAddressOf()))) &&
                multithread)
            {
                multithread->SetMultithreadProtected(enabled ? TRUE : FALSE);
            }
        }

        HRESULT EnsureCore10EncodeDevice()
        {
            if (m_encodeDevice && m_encodeContext)
                return S_OK;
            if (!m_adapter)
                return E_FAIL;

            m_encodeContext.Reset();
            m_encodeDevice.Reset();

            const HRESULT hr = D3D11CreateDevice(
                m_adapter.Get(),
                D3D_DRIVER_TYPE_UNKNOWN,
                nullptr,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                nullptr,
                0,
                D3D11_SDK_VERSION,
                m_encodeDevice.GetAddressOf(),
                nullptr,
                m_encodeContext.GetAddressOf());

            if (FAILED(hr))
                return hr;

            // The encode immediate context is intentionally single-owner: only
            // EncoderLoop touches it after startup, so multithread protection is
            // unnecessary and would only add serialization overhead.
            ConfigureCaptureFrameLatency(m_encodeDevice.Get());
            return S_OK;
        }

        HRESULT StartEncoder(
            const MV_EncoderConfig& config)
        {
            std::lock_guard<std::mutex> lifecycleLock(
                m_encoderLifecycleMutex);

#ifdef MULTIVERSE_NVENC
            if (m_encoderActive.load())
            {
                return HRESULT_FROM_WIN32(
                    ERROR_ALREADY_EXISTS);
            }

            if (!m_device)
                return E_FAIL;

            // CORE 11.10 keeps CPU priority normal but explicitly tests WDDM
            // process GPU scheduling during streaming. Per-device DXGI priority
            // remains the bounded adaptive hint from CORE 11.5; process HIGH
            // (or ABOVE_NORMAL fallback) is restored on Stop.
            DisableBackgroundPowerThrottling();
            ConfigureCaptureFrameLatency(
                m_device.Get());

            const bool core10Isolated =
                IsCore10IsolatedMode(config.LabInputMode);
            const bool core11WgcNv12 =
                config.LabInputMode == CORE11_MODE_WGC_NV12;
            const bool core11WgcDirectRgb =
                config.LabInputMode == CORE11_MODE_WGC_DIRECT_RGB;
            const bool core11WgcRgbEncodeScale =
                config.LabInputMode == CORE11_MODE_WGC_RGB_ENCODE_SCALE;
            const bool core11WgcNativeRgb =
                config.LabInputMode == CORE11_MODE_WGC_NATIVE_RGB;
            const bool core11WgcSameDeviceDirect =
                config.LabInputMode == CORE11_MODE_WGC_SAME_DEVICE_DIRECT_RGB;
            const bool core10Wgc =
                config.LabInputMode == CORE10_MODE_WGC_BGRA ||
                core11WgcNv12 ||
                core11WgcDirectRgb ||
                core11WgcRgbEncodeScale ||
                core11WgcNativeRgb ||
                core11WgcSameDeviceDirect;
            const bool isolatedSynthetic =
                config.LabInputMode == LAB_MODE_SYNTHETIC;

            if (core10Isolated || isolatedSynthetic)
            {
                const HRESULT encodeDeviceHr = EnsureCore10EncodeDevice();
                if (FAILED(encodeDeviceHr))
                {
                    m_encoderStatus.store(encodeDeviceHr);
                    SetEncoderMessage(L"CORE 10: falha ao criar device D3D11 isolado do encoder.");
                    return encodeDeviceHr;
                }

                // Capture keeps its existing D3D11 multithread guard for safe
                // transition/recovery. The important CORE 10 invariant is that
                // the encoder never executes work on this immediate context.
            }

            const std::uint32_t capturedTextureWidth =
                m_textureWidth.load(std::memory_order_acquire);

            const std::uint32_t capturedTextureHeight =
                m_textureHeight.load(std::memory_order_acquire);

            const std::uint32_t sourceWidth =
                capturedTextureWidth != 0u
                    ? capturedTextureWidth
                    : m_sourceWidth;

            const std::uint32_t sourceHeight =
                capturedTextureHeight != 0u
                    ? capturedTextureHeight
                    : m_sourceHeight;

            const auto remoteLimits = ResolveRemoteEncoderLimits(config.MaxWidth, config.MaxHeight);
            const std::uint32_t remoteMaxWidth = remoteLimits.width;
            const std::uint32_t remoteMaxHeight = remoteLimits.height;

            std::uint32_t width = 0;
            std::uint32_t height = 0;

            ResolveEncoderDimensions(
                sourceWidth,
                sourceHeight,
                remoteMaxWidth,
                remoteMaxHeight,
                width,
                height);

            // CORE 11.15 native-RGB diagnostics stay native. CORE 11.56 keeps
            // mode 10 on the proven same-device/latest-frame topology but now
            // respects MaxWidth/MaxHeight. When the host is above the remote
            // cap, AsyncNvencD3D11 performs the RGB scale only on frames that
            // actually reach the encoder deadline. Legacy isolated BGRA
            // diagnostics also remain native.
            if (core11WgcNativeRgb ||
                (core10Isolated && !core11WgcNv12 && !core11WgcDirectRgb && !core11WgcRgbEncodeScale))
            {
                width = sourceWidth & ~1u;
                height = sourceHeight & ~1u;
            }

            const bool sameDeviceNeedsScale =
                core11WgcSameDeviceDirect &&
                (width != sourceWidth || height != sourceHeight);

            if (width == 0 ||
                height == 0 ||
                (width & 1u) != 0 ||
                (height & 1u) != 0)
            {
                SetEncoderMessage(
                    L"A resolução precisa ser par para H.264.");

                return E_INVALIDARG;
            }

            const std::uint32_t fps =
                std::clamp<std::uint32_t>(
                    config.Fps,
                    30,
                    120);

            const std::uint32_t bitrate =
                std::clamp<std::uint32_t>(
                    config.BitrateKbps,
                    2000,
                    200000);

            // 0 = Auto. O stream remoto é limitado a 120 FPS; P1/P2 continuam selecionáveis.
            const std::uint32_t preset =
                std::min<std::uint32_t>(
                    config.Preset,
                    4);

            auto encoder =
                std::make_unique<AsyncNvencD3D11>();

            ID3D11Device* encoderDevice =
                core11WgcSameDeviceDirect
                    ? m_device.Get()
                    : ((core10Isolated || isolatedSynthetic)
                        ? m_encodeDevice.Get()
                        : m_device.Get());

            const std::uint32_t nvencInputMode =
                core11WgcSameDeviceDirect
                    ? (sameDeviceNeedsScale
                        ? LAB_MODE_ARGB_SCALE
                        : LAB_MODE_EXTERNAL_ARGB)
                    : (core11WgcNativeRgb
                        ? LAB_MODE_ARGB
                        : (core11WgcRgbEncodeScale
                            ? LAB_MODE_ARGB_SCALE
                            : (core11WgcDirectRgb
                                ? LAB_MODE_EXTERNAL_ARGB
                                : (core11WgcNv12
                                    ? LAB_MODE_NV12
                                    : (core10Isolated
                                        ? LAB_MODE_ARGB
                                        : config.LabInputMode)))));

            const std::uint32_t nvencSourceWidth =
                core11WgcDirectRgb ? width : sourceWidth;
            const std::uint32_t nvencSourceHeight =
                core11WgcDirectRgb ? height : sourceHeight;

            const NVENCSTATUS status =
                encoder->Start(
                    encoderDevice,
                    nvencSourceWidth,
                    nvencSourceHeight,
                    width,
                    height,
                    fps,
                    bitrate,
                    preset,
                    config.UltraLowLatency != 0,
                    nvencInputMode,
                    m_packetQueue);

            if (status != NV_ENC_SUCCESS)
            {
                m_encoderStatus.store(
                    NvencStatusToHresult(
                        status));

                SetEncoderMessage(
                    L"Falha ao iniciar NVENC async em [" +
                    encoder->LastStage() +
                    L"]: " +
                    NvencStatusText(status));

                return
                    NvencStatusToHresult(
                        status);
            }

            const auto effectivePreset =
                encoder->EffectivePreset();
            const bool nv12Preprocess =
                encoder->UsesNv12Preprocess();
            const std::wstring inputDiagnostic =
                core11WgcSameDeviceDirect
                    ? (sameDeviceNeedsScale
                        ? L"CORE 11.56 FIXED-60: WGC free-threaded -> latest delivery worker -> same-device native ring -> RGB shader scale at encoder deadline -> NVENC"
                        : L"CORE 11.17e FIXED-60 + COPY-FLUSH1: WGC free-threaded -> latest delivery worker -> same-device ring -> Copy GPU EVENT -> Flush1(COPY) -> NVENC")
                    : (core11WgcNativeRgb
                        ? L"CORE 11.15: shared-ring BGRA nativo -> CopyResource nativo -> private NVENC ARGB"
                        : encoder->InputPathDiagnostic());

            m_encoder =
                std::move(encoder);

            // Start a fresh ring-telemetry epoch so each test can be read
            // without counters inherited from a previous encoder run.
            m_ringPublished.store(0);
            m_ringReplaced.store(0);
            m_ringBusyDrops.store(0);
            m_ringConsumed.store(0);
            m_ringPacingDrops.store(0);
            ResetCaptureCopyGpuDiagnostics();
            m_ringPublishPacer.Reset();
            m_remoteOutputFrameId = 0;
            m_remoteClockLateTicks.store(0, std::memory_order_relaxed);
            m_core11IngressFps.store(0, std::memory_order_relaxed);
            m_lastPublishProgressUs.store(0, std::memory_order_relaxed);
            m_lastConsumeProgressUs.store(0, std::memory_order_relaxed);
            m_encoderWorkerAlive.store(false, std::memory_order_release);
            m_coreWgcStage.store(0, std::memory_order_release);
            m_coreWgcStageChangedUs.store(0, std::memory_order_relaxed);
            m_encoderWorkerStage.store(0, std::memory_order_release);
            m_encoderWorkerStageChangedUs.store(0, std::memory_order_relaxed);
            m_encoderStopInProgress.store(false, std::memory_order_release);
            m_encoderStopStage.store(0, std::memory_order_release);
            m_encoderStopStageChangedUs.store(0, std::memory_order_relaxed);
            m_nvencStopStage.store(0, std::memory_order_release);
            m_nvencStopStageChangedUs.store(0, std::memory_order_relaxed);
            if (core10Isolated)
                m_core10Ring.Reset();

            m_encoderSourceWidth = core11WgcDirectRgb ? width : sourceWidth;
            m_encoderSourceHeight = core11WgcDirectRgb ? height : sourceHeight;
            m_encoderWidth = width;
            m_encoderHeight = height;
            m_encoderFps = fps;
            m_capturePublishFps.store(fps);
            m_adaptivePressureController.Reset(fps);
            m_adaptivePressureState.store(
                static_cast<std::uint32_t>(Core11AdaptivePressureState::Normal),
                std::memory_order_relaxed);
            m_adaptivePressureTargetFps.store(fps, std::memory_order_relaxed);
            m_adaptivePressureStreak.store(0, std::memory_order_relaxed);
            m_adaptiveHealthyStreak.store(0, std::memory_order_relaxed);
            m_adaptiveTransitions.store(0, std::memory_order_relaxed);
            m_adaptiveRingOccupied.store(0, std::memory_order_relaxed);
            const std::uint32_t sameDeviceIngressFps = fps;
            m_core11IngressFps.store(
                core11WgcSameDeviceDirect
                    ? sameDeviceIngressFps
                    : ((core11WgcNv12 || core11WgcDirectRgb || core11WgcRgbEncodeScale || core11WgcNativeRgb)
                        ? ResolveCore11IngressFps(fps)
                        : fps),
                std::memory_order_relaxed);
            m_encoderBitrateKbps = bitrate;
            m_encoderPreset = preset;
            m_encoderUltraLowLatency =
                config.UltraLowLatency != 0;
            m_encoderMaxWidth =
                core11WgcNativeRgb
                    ? (sourceWidth & ~1u)
                    : remoteMaxWidth;
            m_encoderMaxHeight =
                core11WgcNativeRgb
                    ? (sourceHeight & ~1u)
                    : remoteMaxHeight;
            m_encoderLabInputMode =
                config.LabInputMode <= CORE11_MODE_WGC_SAME_DEVICE_DIRECT_RGB
                    ? config.LabInputMode
                    : CORE11_MODE_WGC_SAME_DEVICE_DIRECT_RGB;

            if (m_encoderLabInputMode == LAB_MODE_SYNTHETIC)
            {
                const HRESULT syntheticHr =
                    CreateSyntheticEncoderSource(width, height);
                if (FAILED(syntheticHr))
                {
                    if (m_encoder)
                    {
                        m_encoder->Stop();
                        m_encoder.reset();
                    }
                    m_encoderStatus.store(syntheticHr);
                    SetEncoderMessage(L"LAB C: falha ao criar fonte sintética D3D11.");
                    return syntheticHr;
                }
            }
            else
            {
                ResetSyntheticEncoderSource();
            }

            // Publish all configuration before either capture backend can touch the pacer.
            m_encoderWorkerStop.store(false);
            m_encoderActive.store(true);

            if (core10Wgc)
            {
                const HRESULT wgcHr = StartWgcCapture();
                if (FAILED(wgcHr))
                {
                    m_encoderActive.store(false);
                    m_encoderWorkerStop.store(true);
                    if (m_encoder)
                    {
                        m_encoder->Stop();
                        m_encoder.reset();
                    }
                    m_encoderStatus.store(wgcHr);
                    SetEncoderMessage(
                        L"CORE 10.1 WGC: falha ao iniciar captura free-threaded.");
                    return wgcHr;
                }
            }

            // CORE 11.15c: the native-host path is a fair-scheduling experiment.
            // Keep both D3D11 devices at neutral relative priority so streaming
            // cannot steal foreground-game GPU time through DXGI priority hints.
            // Older CORE 11 fallbacks retain the previous adaptive experiment
            // for A/B comparison only.
            ResetCore11GpuSchedulingControl();
            if (core11WgcNativeRgb || core11WgcSameDeviceDirect)
            {
                m_captureGpuPriority =
                    ConfigureGpuSchedulingPriority(m_device.Get(), 0);
                m_encodeGpuPriority = core11WgcSameDeviceDirect
                    ? m_captureGpuPriority
                    : ConfigureGpuSchedulingPriority(m_encodeDevice.Get(), 0);
                m_gpuSchedulingBoosted.store(false, std::memory_order_release);
            }
            else if (core11WgcNv12 || core11WgcDirectRgb || core11WgcRgbEncodeScale)
            {
                ApplyCore11GpuSchedulingPriorities(false, true);
            }
            else
            {
                m_captureGpuPriority = {};
                m_encodeGpuPriority = {};
            }

            m_nextEncoderRestartAttempt =
                std::chrono::steady_clock::time_point{};

            m_nextEncodeTime =
                std::chrono::steady_clock::now();
            m_remoteFrameDeadlineScheduler.Reset();

            // CORE 11.15c: native-host streaming uses fair WDDM scheduling.
            // The process is explicitly NORMAL for mode 9 and restored to its
            // original class on Stop. Legacy CORE 11 fallbacks keep the prior
            // HIGH/ABOVE_NORMAL experiment for direct A/B comparison.
            ApplyProcessGpuSchedulingPriorityForStreaming();

            m_encoderStatus.store(S_OK);
            m_pendingForceIdr.store(false);
            m_pendingBitrateKbps.store(0u);
            RefreshEncoderStatsSnapshot();

            try
            {
                if (!m_encoderWorker.joinable())
                {
                    m_encoderWorker =
                        std::thread(
                            &DesktopCaptureSession::EncoderLoop,
                            this);
                }
            }
            catch (...)
            {
                m_encoderActive.store(false);
                StopWgcCapture();
                (void)ConfigureGpuSchedulingPriority(m_device.Get(), 0);
                (void)ConfigureGpuSchedulingPriority(m_encodeDevice.Get(), 0);
                RestoreProcessGpuSchedulingPriority();
                m_encoderWorkerStop.store(true);
                m_encoder->Stop();
                m_encoder.reset();
                m_encoderStatus.store(E_OUTOFMEMORY);
                SetEncoderMessage(
                    L"Falha ao criar thread dedicada do encoder.");
                return E_OUTOFMEMORY;
            }

            std::wstring pipelineLabel;
            std::wstring preprocessLabel;
            std::wstring schedulingLabel;
            std::wstring ringLabel;

            if (core11WgcSameDeviceDirect)
            {
                pipelineLabel = L"CORE 11.56 FIXED-60 + WGC LATEST-WORKER + SAME-DEVICE + remote resolution cap: P";
                preprocessLabel = sameDeviceNeedsScale
                    ? L"SAME-DEVICE RGB SCALE: ring nativo -> shader linear somente no frame codificado -> NVENC"
                    : L"SAME-DEVICE DIRECT ARGB: 1x CopyResource total, zero shared-fence; Flush1(COPY) A/B";
                schedulingLabel = L"scheduler DEADLINE-AWARE 11.9; FAIR GPU PROC NORMAL + device 0; WGC/NVENC no mesmo D3D11 device";
                ringLabel = L"ring local RGB 4 slots + 4 slots NVENC";
            }
            else if (core11WgcNativeRgb)
            {
                pipelineLabel = L"CORE 11.15 WGC native host + private ARGB NVENC fallback ativo: P";
                preprocessLabel = L"NATIVE ARGB CopyResource no EncodeDevice (zero scaling/VP)";
                schedulingLabel = L"scheduler DEADLINE-AWARE 11.9; CORE 11.15c FAIR GPU: PROC NORMAL + cap/enc 0/0 + boost adaptativo OFF";
                ringLabel = L"ring GPU 3 por shared fence + 4 slots NVENC";
            }
            else if (core11WgcRgbEncodeScale)
            {
                pipelineLabel = L"CORE 11.14 WGC + encode-side RGB scaler/NVENC fallback ativo: P";
                preprocessLabel = L"RGB SHADER no EncodeDevice (zero VideoProcessor; ring nativo)";
                schedulingLabel = L"scheduler DEADLINE-AWARE 11.9; fallback CORE 11 com scheduling legado";
                ringLabel = L"ring GPU 3 por shared fence + 4 slots NVENC";
            }
            else if (core11WgcDirectRgb)
            {
                pipelineLabel = L"CORE 11.13 WGC + direct-ring RGB/NVENC fallback ativo: P";
                preprocessLabel = L"DIRECT-RING RGB 11.13 fallback";
                schedulingLabel = L"scheduler DEADLINE-AWARE 11.9; fallback CORE 11 com scheduling legado";
                ringLabel = L"ring GPU 3 por shared fence + 4 slots NVENC";
            }
            else if (core11WgcNv12)
            {
                pipelineLabel = L"CORE 11.12 WGC free-threaded + isolated NV12 fallback ativo: P";
                preprocessLabel = nv12Preprocess ? L"NV12/DXVA (sem RGB-CUDA)" : L"ARGB fallback";
                schedulingLabel = L"scheduler DEADLINE-AWARE 11.9; fallback CORE 11 com scheduling legado";
                ringLabel = L"ring GPU 3 por shared fence + 4 slots NVENC";
            }
            else if (core10Wgc)
            {
                pipelineLabel = L"CORE 10.1 WGC free-threaded + isolated BGRA ativo: P";
                preprocessLabel = L"ARGB fallback";
                schedulingLabel = L"scheduler legado";
                ringLabel = L"ring GPU legado";
            }
            else if (core10Isolated)
            {
                pipelineLabel = L"CORE 10.0 DXGI isolated BGRA ativo: P";
                preprocessLabel = L"ARGB fallback";
                schedulingLabel = L"scheduler legado";
                ringLabel = L"ring GPU legado";
            }
            else
            {
                pipelineLabel = L"NVENC diagnóstico legado ativo: P";
                preprocessLabel = L"ARGB fallback";
                schedulingLabel = L"scheduler legado";
                ringLabel = L"ring legado";
            }

            SetEncoderMessage(
                pipelineLabel +
                std::to_wstring(effectivePreset) +
                L", " + ringLabel +
                L", resolução de stream " +
                std::to_wstring(width) + L"x" + std::to_wstring(height) +
                L", CBR, zero B-frames, FEC e preprocess " +
                preprocessLabel +
                L"; diagnóstico input: " + inputDiagnostic +
                L"; " + schedulingLabel +
                L", output NVENC dedicado por completion event.");

            return S_OK;
#else
            (void)config;

            m_encoderStatus.store(
                E_NOTIMPL);

            SetEncoderMessage(
                L"Core compilado sem nvEncodeAPI.h. "
                L"Confira NV_CODEC_SDK e recompile.");

            return E_NOTIMPL;
#endif
        }

        void BlockEncoderSubmissions()
        {
            m_encoderActive.store(false, std::memory_order_release);
            m_encoderWorkerStop.store(true, std::memory_order_release);
            m_captureRingCv.notify_all();
            m_core10Ring.WakeAll();
        }

        void StopEncoder()
        {
            std::lock_guard<std::mutex> lifecycleLock(
                m_encoderLifecycleMutex);

            m_encoderStopInProgress.store(true, std::memory_order_release);
            SetEncoderStopStage(1); // entered

            m_encoderActive.store(false);
            SetEncoderStopStage(2); // producers disabled

            // Request neutral scheduling immediately, but do not mutate the
            // diagnostic result structs until the adaptive worker has joined.
            // CORE 11.5 can update those structs from EncoderLoop, so writing
            // them here as well would create an unnecessary cross-thread race.
            (void)ConfigureGpuSchedulingPriority(m_device.Get(), 0);
            (void)ConfigureGpuSchedulingPriority(m_encodeDevice.Get(), 0);
            SetEncoderStopStage(3); // GPU priority restore requested

            SetEncoderStopStage(4); // entering WGC stop
            StopWgcCapture();
            SetEncoderStopStage(5); // WGC stop returned

            m_encoderWorkerStop.store(true);
            m_captureRingCv.notify_all();
            m_core10Ring.WakeAll();
            SetEncoderStopStage(6); // worker stop signaled

            if (m_encoderWorker.joinable() &&
                m_encoderWorker.get_id() != std::this_thread::get_id())
            {
                SetEncoderStopStage(7); // waiting encoder worker join
                m_encoderWorker.join();
            }
            SetEncoderStopStage(8); // encoder worker joined
            m_remoteOutputFrameId = 0;

            // A worker may have observed pressure just before Stop disabled the
            // encoder. Restore neutral scheduling again after join so teardown
            // can never leave a boosted device behind.
            m_captureGpuPriority =
                ConfigureGpuSchedulingPriority(m_device.Get(), 0);
            m_encodeGpuPriority =
                ConfigureGpuSchedulingPriority(m_encodeDevice.Get(), 0);
            ResetCore11GpuSchedulingControl();
            RestoreProcessGpuSchedulingPriority();

#ifdef MULTIVERSE_NVENC
            if (m_encoder)
            {
                SetEncoderStopStage(9); // entering NVENC/output stop
                m_encoder->Stop(
                    [this](std::uint32_t stage)
                    {
                        m_nvencStopStage.store(stage, std::memory_order_release);
                        m_nvencStopStageChangedUs.store(
                            SteadyNowUs(),
                            std::memory_order_release);
                    });
                SetEncoderStopStage(10); // NVENC/output stop returned
                m_encoder.reset();
            }
#endif

            SetEncoderStopStage(11); // cleanup
            m_packetConsumerActive.store(false);
            if (m_packetQueue)
                m_packetQueue->SetActive(false);
            m_nextEncoderRestartAttempt =
                std::chrono::steady_clock::time_point{};
            m_remoteFrameDeadlineScheduler.Reset();

            {
                std::lock_guard<std::mutex> ringLock(m_captureRingMutex);
                for (auto& slot : m_captureRing)
                {
                    slot.Ready = false;
                    slot.Encoding = false;
                    slot.FrameId = 0;
                    slot.CopyProbeIssued = false;
                    slot.CopyProbePending = false;
                    slot.CopyProbeCheckedAtSubmit = false;
                    slot.CopyProbeFailed = false;
                    slot.CopyEnqueueTime = {};
                }
            }

            m_encoderSourceWidth = 0;
            m_encoderSourceHeight = 0;
            m_encoderWidth = 0;
            m_encoderHeight = 0;
            m_encoderFps = 0;
            m_capturePublishFps.store(0);
            m_adaptivePressureController.Reset(0);
            m_adaptivePressureState.store(
                static_cast<std::uint32_t>(Core11AdaptivePressureState::Normal),
                std::memory_order_relaxed);
            m_adaptivePressureTargetFps.store(0, std::memory_order_relaxed);
            m_adaptivePressureStreak.store(0, std::memory_order_relaxed);
            m_adaptiveHealthyStreak.store(0, std::memory_order_relaxed);
            m_adaptiveTransitions.store(0, std::memory_order_relaxed);
            m_adaptiveRingOccupied.store(0, std::memory_order_relaxed);
            m_core11IngressFps.store(0, std::memory_order_relaxed);
            m_remoteClockLateTicks.store(0, std::memory_order_relaxed);
            m_encoderBitrateKbps = 0;
            m_encoderMaxWidth = 0;
            m_encoderMaxHeight = 0;
            m_encoderLabInputMode = LAB_MODE_AUTO;
            ResetSyntheticEncoderSource();
            ResetEncoderScaleResources();

            m_encoderStatus.store(S_OK);
            m_pendingForceIdr.store(false);
            m_pendingBitrateKbps.store(0u);
            RefreshEncoderStatsSnapshot();

            SetEncoderStopStage(12); // complete
            m_encoderStopInProgress.store(false, std::memory_order_release);
            SetEncoderMessage(L"Encoder parado.");
        }

        HRESULT RequestIdr()
        {
#ifdef MULTIVERSE_NVENC
            if (!m_encoderActive.load())
                return E_FAIL;

            m_pendingForceIdr.store(true);
            m_captureRingCv.notify_one();
            m_core10Ring.WakeAll();
            return S_OK;
#else
            return E_NOTIMPL;
#endif
        }

        HRESULT ReconfigureEncoderBitrate(
            std::uint32_t bitrateKbps)
        {
#ifdef MULTIVERSE_NVENC
            if (!m_encoderActive.load())
                return E_FAIL;

            bitrateKbps =
                std::clamp<std::uint32_t>(
                    bitrateKbps,
                    2000u,
                    200000u);

            // Coalescing mailbox: latest adaptive-bitrate value wins.
            m_pendingBitrateKbps.store(bitrateKbps);
            m_captureRingCv.notify_one();
            m_core10Ring.WakeAll();
            return S_OK;
#else
            (void)bitrateKbps;
            return E_NOTIMPL;
#endif
        }

        HRESULT GetEncoderStats(
            MV_EncoderStats* stats)
        {
            if (!stats)
                return E_INVALIDARG;

            const auto waitStart =
                std::chrono::steady_clock::now();

            {
                std::lock_guard<std::mutex> lock(
                    m_encoderStatsMutex);
                *stats = m_encoderStatsSnapshot;
            }

            const auto waitEnd =
                std::chrono::steady_clock::now();

            stats->StatsReadWaitMs =
                std::chrono::duration_cast<
                    std::chrono::microseconds>(
                        waitEnd - waitStart)
                    .count() / 1000.0;

            stats->PendingCommands =
                (m_pendingForceIdr.load() ? 1u : 0u) +
                (m_pendingBitrateKbps.load() != 0u ? 1u : 0u);
            stats->EncoderWorkerAlive =
                m_encoderWorkerAlive.load(std::memory_order_acquire) ? 1u : 0u;
            stats->StopInProgress =
                m_encoderStopInProgress.load(std::memory_order_acquire) ? 1u : 0u;
            stats->StopStage = m_encoderStopStage.load(std::memory_order_acquire);
            stats->StopStageAgeMs = AgeMsFrom(
                m_encoderStopStageChangedUs.load(std::memory_order_relaxed));
            stats->NvencStopStage =
                m_nvencStopStage.load(std::memory_order_acquire);
            stats->NvencStopStageAgeMs = AgeMsFrom(
                m_nvencStopStageChangedUs.load(std::memory_order_relaxed));

            return S_OK;
        }

        HRESULT GetEncoderMessage(
            wchar_t* destination,
            std::uint32_t destinationChars)
        {
            if (!destination ||
                destinationChars == 0)
            {
                return E_INVALIDARG;
            }

            std::lock_guard<std::mutex> lock(
                m_encoderMessageMutex);

            wcsncpy_s(
                destination,
                destinationChars,
                m_encoderMessage.c_str(),
                _TRUNCATE);

            return S_OK;
        }

        HRESULT PeekPacket(
            std::uint32_t* requiredBytes,
            std::uint64_t* frameId,
            std::int32_t* keyFrame)
        {
            if (!requiredBytes || !frameId || !keyFrame)
                return E_INVALIDARG;

            if (!m_packetQueue)
                return S_FALSE;

            // Only the explicit consumer owner may activate the queue.
            // A stale read after Stop must never restart NVENC packet copying.
            if (!m_packetQueue->IsActive())
                return S_FALSE;

            bool isKeyFrame = false;
            if (!m_packetQueue->Peek(
                    *requiredBytes,
                    *frameId,
                    isKeyFrame))
            {
                return S_FALSE;
            }

            *keyFrame = isKeyFrame ? 1 : 0;
            return S_OK;
        }

        HRESULT PopPacket(
            std::uint8_t* destination,
            std::uint32_t destinationBytes,
            std::uint32_t* writtenBytes,
            std::uint64_t* frameId,
            std::int32_t* keyFrame)
        {
            if (!destination || !writtenBytes || !frameId || !keyFrame)
                return E_INVALIDARG;

            if (!m_packetQueue)
                return S_FALSE;

            // Only the explicit consumer owner may activate the queue.
            // A stale read after Stop must never restart NVENC packet copying.
            if (!m_packetQueue->IsActive())
                return S_FALSE;

            bool isKeyFrame = false;
            const auto result = m_packetQueue->Pop(
                destination,
                destinationBytes,
                *writtenBytes,
                *frameId,
                isKeyFrame);

            if (result == SharedEncodedPacketQueue::PopResult::Empty)
                return S_FALSE;

            if (result == SharedEncodedPacketQueue::PopResult::InsufficientBuffer)
                return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);

            *keyFrame = isKeyFrame ? 1 : 0;
            return S_OK;
        }

        HRESULT SetPacketConsumerActive(
            bool active)
        {
#ifdef MULTIVERSE_NVENC
            m_packetConsumerActive.store(active);

            if (!m_packetQueue)
                return S_FALSE;

            m_packetQueue->SetActive(active);
            if (active)
            {
                m_pendingForceIdr.store(true);
                m_captureRingCv.notify_one();
                m_core10Ring.WakeAll();
            }

            // Transport never touches the live encoder.
            return S_OK;
#else
            (void)active;
            return E_NOTIMPL;
#endif
        }

    private:
#ifdef MULTIVERSE_NVENC
        void ApplyPendingEncoderCommands()
        {
            if (!m_encoder || !m_encoder->IsRunning())
                return;

            if (m_pendingForceIdr.exchange(false))
                m_encoder->RequestIdr();

            const std::uint32_t bitrateKbps =
                m_pendingBitrateKbps.exchange(0u);

            if (bitrateKbps == 0u)
                return;

            const NVENCSTATUS status =
                m_encoder->ReconfigureBitrate(bitrateKbps);

            if (status == NV_ENC_SUCCESS)
            {
                m_encoderBitrateKbps = bitrateKbps;
                m_encoderStatus.store(S_OK);
                SetEncoderMessage(
                    L"NVENC adaptativo aplicado pela worker: " +
                    std::to_wstring(bitrateKbps) +
                    L" Kbps.");
            }
            else
            {
                const HRESULT hr =
                    NvencStatusToHresult(status);
                m_encoderStatus.store(hr);
                SetEncoderMessage(
                    L"Falha ao aplicar bitrate NVENC na worker: " +
                    NvencStatusText(status));
            }
        }
#endif

        void SetCoreWgcStage(std::uint32_t stage) noexcept
        {
            m_coreWgcStage.store(stage, std::memory_order_release);
            m_coreWgcStageChangedUs.store(
                SteadyNowUs(),
                std::memory_order_release);
        }

        void SetEncoderWorkerStage(std::uint32_t stage) noexcept
        {
            m_encoderWorkerStage.store(stage, std::memory_order_release);
            m_encoderWorkerStageChangedUs.store(
                SteadyNowUs(),
                std::memory_order_release);
        }

        void SetEncoderStopStage(std::uint32_t stage) noexcept
        {
            m_encoderStopStage.store(stage, std::memory_order_release);
            m_encoderStopStageChangedUs.store(
                SteadyNowUs(),
                std::memory_order_release);
        }

        void ResetCore11GpuSchedulingControl() noexcept
        {
            m_gpuSchedulingBoosted.store(false, std::memory_order_release);
            m_gpuPressureSamples = 0;
            m_gpuRecoverySamples = 0;
            m_gpuSchedulingLastSample = {};
            m_gpuSampleWgcFrames = 0;
            m_gpuSampleConsumedFrames = 0;
            m_gpuSampleEncodedFrames = 0;
            m_gpuSampleLateTicks = 0;
        }

        HRESULT EffectiveGpuPriorityStatus(
            const GpuPriorityResult& result) const noexcept
        {
            if (FAILED(result.SetStatus))
                return result.SetStatus;
            if (FAILED(result.GetStatus))
                return result.GetStatus;
            return S_OK;
        }

        HRESULT EffectiveProcessGpuPriorityStatus(
            const ProcessGpuPriorityResult& result) const noexcept
        {
            if (FAILED(result.SetStatus))
                return result.SetStatus;
            if (FAILED(result.GetStatus))
                return result.GetStatus;
            return S_OK;
        }

        void ApplyProcessGpuSchedulingPriorityForStreaming()
        {
            const bool fairGpuScheduling =
                m_encoderLabInputMode == CORE11_MODE_WGC_NATIVE_RGB ||
                m_encoderLabInputMode == CORE11_MODE_WGC_SAME_DEVICE_DIRECT_RGB;
            const ProcessGpuPriorityClass requestedClass =
                fairGpuScheduling
                    ? ProcessGpuPriorityClass::Normal
                    : ProcessGpuPriorityClass::High;

            ProcessGpuPriorityClass original = ProcessGpuPriorityClass::Normal;
            const HRESULT originalHr =
                QueryProcessGpuSchedulingPriority(&original);
            if (FAILED(originalHr))
            {
                // Never mutate a process scheduling class that we cannot later
                // restore deterministically. Surface the query failure instead.
                m_processGpuPriorityOriginalCaptured = false;
                m_processGpuPriority = {};
                m_processGpuPriority.Requested = requestedClass;
                m_processGpuPriority.SetStatus = originalHr;
                m_processGpuPriority.GetStatus = originalHr;
                m_processGpuPriorityRestoreStatus = S_FALSE;
                return;
            }

            m_processGpuPriorityOriginal = original;
            m_processGpuPriorityOriginalCaptured = true;
            m_processGpuPriorityRestoreStatus = S_OK;

            auto result =
                ConfigureProcessGpuSchedulingPriority(requestedClass);

            if (fairGpuScheduling)
            {
                // CORE 11.15c must be a clean fairness experiment: no fallback
                // to ABOVE_NORMAL/HIGH. If NORMAL cannot be applied/read back,
                // telemetry exposes the failure and the original class is still
                // restored on Stop.
                if (SUCCEEDED(EffectiveProcessGpuPriorityStatus(result)) &&
                    result.Effective != ProcessGpuPriorityClass::Normal)
                {
                    result.GetStatus = HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
                }
            }
            else if (FAILED(EffectiveProcessGpuPriorityStatus(result)) ||
                     result.Effective != ProcessGpuPriorityClass::High)
            {
                // Legacy CORE 11 experiment: HIGH may be rejected on a
                // non-elevated token, so preserve the ABOVE_NORMAL fallback.
                auto fallback =
                    ConfigureProcessGpuSchedulingPriority(
                        ProcessGpuPriorityClass::AboveNormal);
                fallback.Requested = ProcessGpuPriorityClass::High;
                if (SUCCEEDED(EffectiveProcessGpuPriorityStatus(fallback)) &&
                    fallback.Effective == ProcessGpuPriorityClass::AboveNormal)
                {
                    fallback.SetStatus = S_FALSE;
                }
                result = fallback;
            }

            m_processGpuPriority = result;
        }

        void RestoreProcessGpuSchedulingPriority()
        {
            if (!m_processGpuPriorityOriginalCaptured)
            {
                m_processGpuPriorityRestoreStatus = S_FALSE;
                return;
            }

            const auto restored =
                ConfigureProcessGpuSchedulingPriority(
                    m_processGpuPriorityOriginal);
            m_processGpuPriorityRestoreStatus =
                EffectiveProcessGpuPriorityStatus(restored);
            if (SUCCEEDED(m_processGpuPriorityRestoreStatus) &&
                restored.Effective != m_processGpuPriorityOriginal)
            {
                m_processGpuPriorityRestoreStatus =
                    HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
            }

            // Keep the effective value visible after Stop so a failed restore
            // cannot be hidden by resetting the diagnostic struct.
            m_processGpuPriority.Effective = restored.Effective;
            m_processGpuPriorityOriginalCaptured = false;
        }

        void ApplyCore11GpuSchedulingPriorities(
            bool boosted,
            bool force = false)
        {
            if (!m_device || !m_encodeDevice)
                return;

            if (!force &&
                m_gpuSchedulingBoosted.load(std::memory_order_acquire) == boosted)
            {
                return;
            }

            const INT capturePriority = boosted ? 2 : 1;
            const INT encodePriority = boosted ? 3 : 2;

            auto captureResult =
                ConfigureGpuSchedulingPriority(
                    m_device.Get(),
                    capturePriority);
            auto encodeResult =
                ConfigureGpuSchedulingPriority(
                    m_encodeDevice.Get(),
                    encodePriority);

            // Read-back is part of the contract, not just decoration. If a
            // driver accepts SetGPUThreadPriority but reports a different
            // effective value, surface that as a verification failure.
            if (SUCCEEDED(captureResult.GetStatus) &&
                captureResult.EffectivePriority != capturePriority)
            {
                captureResult.GetStatus = HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
            }
            if (SUCCEEDED(encodeResult.GetStatus) &&
                encodeResult.EffectivePriority != encodePriority)
            {
                encodeResult.GetStatus = HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
            }

            m_captureGpuPriority = captureResult;
            m_encodeGpuPriority = encodeResult;

            // This atomic represents the controller tier that was requested.
            // The UI reports the actual GetGPUThreadPriority values separately,
            // so a rejected/clamped boost can never masquerade as effective.
            // Keeping the requested tier here also guarantees the recovery
            // hysteresis will restore +1/+2 instead of retrying +2/+3 forever.
            m_gpuSchedulingBoosted.store(
                boosted,
                std::memory_order_release);
        }

        void UpdateCore11GpuSchedulingControl()
        {
            if ((m_encoderLabInputMode != CORE11_MODE_WGC_NV12 &&
                 m_encoderLabInputMode != CORE11_MODE_WGC_DIRECT_RGB &&
                 m_encoderLabInputMode != CORE11_MODE_WGC_RGB_ENCODE_SCALE) ||
                !m_encoderActive.load(std::memory_order_acquire) ||
                m_encoderWorkerStop.load(std::memory_order_acquire))
            {
                return;
            }

            const auto now = std::chrono::steady_clock::now();
            const auto wgc = m_wgcCapture.Snapshot();
            const std::uint64_t wgcFrames = wgc.FramesArrived;
            const std::uint64_t consumedFrames = m_ringConsumed.load();
            const std::uint64_t lateTicks =
                m_remoteClockLateTicks.load(std::memory_order_relaxed);

            std::uint64_t encodedFrames = 0;
            {
                std::lock_guard<std::mutex> lock(m_encoderStatsMutex);
                encodedFrames = m_encoderStatsSnapshot.EncodedFrames;
            }

            if (m_gpuSchedulingLastSample ==
                std::chrono::steady_clock::time_point{})
            {
                m_gpuSchedulingLastSample = now;
                m_gpuSampleWgcFrames = wgcFrames;
                m_gpuSampleConsumedFrames = consumedFrames;
                m_gpuSampleEncodedFrames = encodedFrames;
                m_gpuSampleLateTicks = lateTicks;
                return;
            }

            const double elapsedSeconds =
                std::chrono::duration<double>(
                    now - m_gpuSchedulingLastSample).count();

            // Half-second samples react quickly enough to a foreground game,
            // while two pressure samples and four recovery samples prevent
            // priority thrash on scene transitions or ordinary WGC jitter.
            if (elapsedSeconds < 0.50)
                return;

            const auto deltaRate =
                [elapsedSeconds](std::uint64_t current, std::uint64_t previous)
                {
                    return current >= previous
                        ? static_cast<double>(current - previous) / elapsedSeconds
                        : 0.0;
                };

            const double sourceFps =
                deltaRate(wgcFrames, m_gpuSampleWgcFrames);
            const double consumeFps =
                deltaRate(consumedFrames, m_gpuSampleConsumedFrames);
            const double outputFps =
                deltaRate(encodedFrames, m_gpuSampleEncodedFrames);
            const double lateFps =
                deltaRate(lateTicks, m_gpuSampleLateTicks);

            m_gpuSchedulingLastSample = now;
            m_gpuSampleWgcFrames = wgcFrames;
            m_gpuSampleConsumedFrames = consumedFrames;
            m_gpuSampleEncodedFrames = encodedFrames;
            m_gpuSampleLateTicks = lateTicks;

            const double targetFps = static_cast<double>(
                std::clamp<std::uint32_t>(m_encoderFps, 30u, 120u));
            const bool sourceSufficient = sourceFps >= targetFps * 0.85;
            const bool pipelineBelowTarget = outputFps < targetFps * 0.85;
            const bool downstreamPressure =
                consumeFps < targetFps * 0.85 || lateFps >= 1.0;
            const bool pressure =
                sourceSufficient && pipelineBelowTarget && downstreamPressure;
            const bool severePressure =
                sourceSufficient &&
                outputFps < targetFps * 0.65 &&
                consumeFps < targetFps * 0.75;

            const bool boosted =
                m_gpuSchedulingBoosted.load(std::memory_order_acquire);

            if (!boosted)
            {
                m_gpuRecoverySamples = 0;
                m_gpuPressureSamples = pressure
                    ? std::min<std::uint32_t>(m_gpuPressureSamples + 1u, 100u)
                    : 0u;

                if ((severePressure || m_gpuPressureSamples >= 2u) &&
                    m_encoderActive.load(std::memory_order_acquire) &&
                    !m_encoderWorkerStop.load(std::memory_order_acquire))
                {
                    ApplyCore11GpuSchedulingPriorities(true);
                    m_gpuPressureSamples = 0;
                }
                return;
            }

            if (pressure)
            {
                m_gpuRecoverySamples = 0;
                return;
            }

            m_gpuRecoverySamples =
                std::min<std::uint32_t>(m_gpuRecoverySamples + 1u, 100u);

            if (m_gpuRecoverySamples >= 4u &&
                m_encoderActive.load(std::memory_order_acquire) &&
                !m_encoderWorkerStop.load(std::memory_order_acquire))
            {
                ApplyCore11GpuSchedulingPriorities(false);
                m_gpuRecoverySamples = 0;
                m_gpuPressureSamples = 0;
            }
        }

        void RefreshEncoderStatsSnapshot()
        {
            MV_EncoderStats local{};

#ifdef MULTIVERSE_NVENC
            if (m_encoder)
                (void)m_encoder->GetStats(&local);
            else
#endif
            {
                local.Running = 0;
                local.LastStatus = m_encoderStatus.load();
            }

            local.PendingCommands =
                (m_pendingForceIdr.load() ? 1u : 0u) +
                (m_pendingBitrateKbps.load() != 0u ? 1u : 0u);
            local.RequestedInputMode = static_cast<std::int32_t>(m_encoderLabInputMode);
            local.Core10PipelineMode = m_encoderLabInputMode;
            local.CaptureDeviceIsolated =
                IsCore10IsolatedMode(m_encoderLabInputMode) ? 1u : 0u;
            local.Core11GpuPriority =
                (m_encoderLabInputMode == CORE11_MODE_WGC_NV12 ||
                 m_encoderLabInputMode == CORE11_MODE_WGC_DIRECT_RGB ||
                 m_encoderLabInputMode == CORE11_MODE_WGC_RGB_ENCODE_SCALE ||
                 m_encoderLabInputMode == CORE11_MODE_WGC_NATIVE_RGB ||
                 m_encoderLabInputMode == CORE11_MODE_WGC_SAME_DEVICE_DIRECT_RGB) ? 1u : 0u;
            local.CaptureGpuPriority = m_captureGpuPriority.EffectivePriority;
            local.EncodeGpuPriority = m_encodeGpuPriority.EffectivePriority;
            if (m_encoderLabInputMode == CORE11_MODE_WGC_NV12 ||
                m_encoderLabInputMode == CORE11_MODE_WGC_DIRECT_RGB ||
                m_encoderLabInputMode == CORE11_MODE_WGC_RGB_ENCODE_SCALE ||
                m_encoderLabInputMode == CORE11_MODE_WGC_NATIVE_RGB ||
                m_encoderLabInputMode == CORE11_MODE_WGC_SAME_DEVICE_DIRECT_RGB)
            {
                const HRESULT encodePriorityHr =
                    EffectiveGpuPriorityStatus(m_encodeGpuPriority);
                const HRESULT capturePriorityHr =
                    EffectiveGpuPriorityStatus(m_captureGpuPriority);
                local.GpuPrioritySetStatus =
                    static_cast<std::int32_t>(
                        FAILED(encodePriorityHr)
                            ? encodePriorityHr
                            : capturePriorityHr);
            }
            else
            {
                local.GpuPrioritySetStatus = static_cast<std::int32_t>(S_OK);
            }
            local.ProcessGpuPriorityRequested =
                static_cast<std::int32_t>(m_processGpuPriority.Requested);
            local.ProcessGpuPriorityEffective =
                static_cast<std::int32_t>(m_processGpuPriority.Effective);
            local.ProcessGpuPrioritySetStatus =
                static_cast<std::int32_t>(
                    EffectiveProcessGpuPriorityStatus(m_processGpuPriority));
            local.ProcessGpuPriorityRestoreStatus =
                static_cast<std::int32_t>(m_processGpuPriorityRestoreStatus);
            local.EncoderWorkerAlive =
                m_encoderWorkerAlive.load(std::memory_order_acquire) ? 1u : 0u;

            std::lock_guard<std::mutex> lock(
                m_encoderStatsMutex);
            m_encoderStatsSnapshot = local;
        }

        static double AverageUs(
            std::uint64_t totalUs,
            std::uint64_t samples) noexcept
        {
            return samples == 0
                ? 0.0
                : (static_cast<double>(totalUs) /
                   static_cast<double>(samples)) / 1000.0;
        }

        struct CaptureRingSlot
        {
            ComPtr<ID3D11Texture2D> Texture;
            ComPtr<ID3D11Query> CopyCompletionQuery;
            std::uint64_t FrameId = 0;
            std::uint64_t CopyProbeGeneration = 0;
            std::chrono::steady_clock::time_point Timestamp{};
            std::chrono::steady_clock::time_point CopyEnqueueTime{};
            bool Ready = false;
            bool Encoding = false;
            bool CopyProbeIssued = false;
            bool CopyProbePending = false;
            bool CopyProbeCheckedAtSubmit = false;
            bool CopyProbeFailed = false;
        };

        static void AtomicMaxU64(
            std::atomic<std::uint64_t>& target,
            std::uint64_t value) noexcept
        {
            std::uint64_t current = target.load(std::memory_order_relaxed);
            while (current < value &&
                   !target.compare_exchange_weak(
                       current, value,
                       std::memory_order_relaxed,
                       std::memory_order_relaxed))
            {
            }
        }

        void ResetCaptureCopyGpuDiagnostics() noexcept
        {
            m_copyGpuProbeSignals.store(0, std::memory_order_relaxed);
            m_copyGpuProbeCompleted.store(0, std::memory_order_relaxed);
            m_copyGpuReadyAtSubmit.store(0, std::memory_order_relaxed);
            m_copyGpuNotReadyAtSubmit.store(0, std::memory_order_relaxed);
            m_copyGpuProbeMaxPending.store(0, std::memory_order_relaxed);
            m_copyGpuLastLatencyUs.store(0, std::memory_order_relaxed);
            m_copyGpuTotalLatencyUs.store(0, std::memory_order_relaxed);
            m_copyGpuMaxLatencyUs.store(0, std::memory_order_relaxed);
            m_copyGpuProbeStatus.store(S_OK, std::memory_order_relaxed);

            std::lock_guard<std::mutex> lock(m_captureRingMutex);
            for (auto& slot : m_captureRing)
            {
                slot.CopyProbeGeneration = 0;
                slot.CopyEnqueueTime = {};
                slot.CopyProbeIssued = false;
                slot.CopyProbePending = false;
                slot.CopyProbeCheckedAtSubmit = false;
                slot.CopyProbeFailed = false;
            }
        }

        std::uint32_t CaptureCopyGpuPendingCount() const noexcept
        {
            std::lock_guard<std::mutex> lock(m_captureRingMutex);
            std::uint32_t pending = 0;
            for (const auto& slot : m_captureRing)
            {
                if (slot.CopyProbePending)
                    ++pending;
            }
            return pending;
        }

        double CaptureCopyGpuOldestPendingMs() const noexcept
        {
            std::lock_guard<std::mutex> lock(m_captureRingMutex);
            const auto now = std::chrono::steady_clock::now();
            double oldest = 0.0;
            for (const auto& slot : m_captureRing)
            {
                if (!slot.CopyProbePending ||
                    slot.CopyEnqueueTime.time_since_epoch().count() == 0)
                {
                    continue;
                }

                oldest = std::max(
                    oldest,
                    std::chrono::duration<double, std::milli>(
                        now - slot.CopyEnqueueTime).count());
            }
            return oldest;
        }

        bool TryCompleteCaptureCopyGpuProbe(
            std::size_t selected,
            bool countSubmitReadiness) noexcept
        {
            if (selected >= CaptureRingSize || !m_context)
                return false;

            ComPtr<ID3D11Query> query;
            std::chrono::steady_clock::time_point enqueued{};
            std::uint64_t generation = 0;
            bool issued = false;
            bool pending = false;
            bool checkedAtSubmit = false;
            bool failed = false;
            {
                std::lock_guard<std::mutex> lock(m_captureRingMutex);
                const auto& slot = m_captureRing[selected];
                query = slot.CopyCompletionQuery;
                enqueued = slot.CopyEnqueueTime;
                generation = slot.CopyProbeGeneration;
                issued = slot.CopyProbeIssued;
                pending = slot.CopyProbePending;
                checkedAtSubmit = slot.CopyProbeCheckedAtSubmit;
                failed = slot.CopyProbeFailed;
            }

            if (!issued || !query || failed)
                return false;

            HRESULT pollHr = pending
                ? m_context->GetData(
                    query.Get(),
                    nullptr,
                    0,
                    D3D11_ASYNC_GETDATA_DONOTFLUSH)
                : S_OK;

            bool completedNow = false;
            if (pollHr == S_OK && pending)
            {
                const auto now = std::chrono::steady_clock::now();
                const auto latencyUsSigned =
                    std::chrono::duration_cast<std::chrono::microseconds>(
                        now - enqueued).count();
                const std::uint64_t latencyUs = static_cast<std::uint64_t>(
                    std::max<long long>(0, latencyUsSigned));

                std::lock_guard<std::mutex> lock(m_captureRingMutex);
                auto& slot = m_captureRing[selected];
                if (slot.CopyProbeGeneration == generation &&
                    slot.CopyProbePending)
                {
                    slot.CopyProbePending = false;
                    m_copyGpuProbeCompleted.fetch_add(1, std::memory_order_relaxed);
                    m_copyGpuLastLatencyUs.store(latencyUs, std::memory_order_relaxed);
                    m_copyGpuTotalLatencyUs.fetch_add(latencyUs, std::memory_order_relaxed);
                    AtomicMaxU64(m_copyGpuMaxLatencyUs, latencyUs);
                    completedNow = true;
                }
            }
            else if (pollHr != S_FALSE && FAILED(pollHr))
            {
                std::lock_guard<std::mutex> lock(m_captureRingMutex);
                auto& slot = m_captureRing[selected];
                if (slot.CopyProbeGeneration == generation)
                {
                    slot.CopyProbePending = false;
                    slot.CopyProbeFailed = true;
                }
                m_copyGpuProbeStatus.store(pollHr, std::memory_order_relaxed);
            }

            if (countSubmitReadiness && !checkedAtSubmit)
            {
                bool readyAtSubmit = false;
                bool valid = false;
                {
                    std::lock_guard<std::mutex> lock(m_captureRingMutex);
                    auto& slot = m_captureRing[selected];
                    if (slot.CopyProbeGeneration == generation &&
                        slot.CopyProbeIssued &&
                        !slot.CopyProbeCheckedAtSubmit &&
                        !slot.CopyProbeFailed)
                    {
                        readyAtSubmit = !slot.CopyProbePending;
                        slot.CopyProbeCheckedAtSubmit = true;
                        valid = true;
                    }
                }

                if (valid)
                {
                    if (readyAtSubmit)
                        m_copyGpuReadyAtSubmit.fetch_add(1, std::memory_order_relaxed);
                    else
                        m_copyGpuNotReadyAtSubmit.fetch_add(1, std::memory_order_relaxed);
                }
            }

            return completedNow || pollHr == S_OK;
        }

        void RefreshCaptureCopyGpuProbes() noexcept
        {
            for (std::size_t i = 0; i < CaptureRingSize; ++i)
                (void)TryCompleteCaptureCopyGpuProbe(i, false);
        }

        void BeginCaptureCopyGpuProbe(std::size_t selected) noexcept
        {
            if (selected >= CaptureRingSize || !m_context)
                return;

            ComPtr<ID3D11Query> query;
            {
                std::lock_guard<std::mutex> lock(m_captureRingMutex);
                auto& slot = m_captureRing[selected];
                if (slot.CopyProbePending || !slot.CopyCompletionQuery)
                    return;
                query = slot.CopyCompletionQuery;
            }

            m_context->End(query.Get());
            const auto enqueued = std::chrono::steady_clock::now();

            std::uint32_t pendingCount = 0;
            {
                std::lock_guard<std::mutex> lock(m_captureRingMutex);
                auto& slot = m_captureRing[selected];
                ++slot.CopyProbeGeneration;
                slot.CopyEnqueueTime = enqueued;
                slot.CopyProbeIssued = true;
                slot.CopyProbePending = true;
                slot.CopyProbeCheckedAtSubmit = false;
                slot.CopyProbeFailed = false;
                for (const auto& item : m_captureRing)
                {
                    if (item.CopyProbePending)
                        ++pendingCount;
                }
            }

            m_copyGpuProbeSignals.fetch_add(1, std::memory_order_relaxed);
            std::uint32_t current =
                m_copyGpuProbeMaxPending.load(std::memory_order_relaxed);
            while (current < pendingCount &&
                   !m_copyGpuProbeMaxPending.compare_exchange_weak(
                       current, pendingCount,
                       std::memory_order_relaxed,
                       std::memory_order_relaxed))
            {
            }
        }

        std::uint32_t RingReadyCount() const noexcept
        {
            std::lock_guard<std::mutex> lock(m_captureRingMutex);
            std::uint32_t count = 0;
            for (const auto& slot : m_captureRing)
            {
                if (slot.Ready)
                    ++count;
            }
            return count;
        }

        std::uint32_t CaptureRingOccupiedCount() const noexcept
        {
            std::lock_guard<std::mutex> lock(m_captureRingMutex);
            std::uint32_t count = 0;
            for (const auto& slot : m_captureRing)
            {
                if (slot.Ready || slot.Encoding)
                    ++count;
            }
            return count;
        }

        void UpdateCore11AdaptivePressureControl()
        {
            if (m_encoderLabInputMode != CORE11_MODE_WGC_SAME_DEVICE_DIRECT_RGB ||
                !m_encoderActive.load(std::memory_order_acquire) ||
                m_encoderWorkerStop.load(std::memory_order_acquire))
            {
                return;
            }

            MV_EncoderStats encoderStats{};
            {
                std::lock_guard<std::mutex> statsLock(m_encoderStatsMutex);
                encoderStats = m_encoderStatsSnapshot;
            }

            Core11AdaptivePressureSample sample{};
            sample.EncoderRunning = encoderStats.Running != 0;
            sample.LastOutputLatencyMs = encoderStats.LastOutputLatencyMs;
            sample.PendingOutputs = encoderStats.PendingOutputs;
            sample.InputSlots = encoderStats.InputSlots;
            sample.OldestInFlightMs = encoderStats.OldestInFlightMs;
            sample.RingOccupied = CaptureRingOccupiedCount();
            sample.RingSlots = static_cast<std::uint32_t>(CaptureRingSize);
            m_adaptiveRingOccupied.store(sample.RingOccupied, std::memory_order_relaxed);

            const auto nowMs =
                std::chrono::duration_cast<std::chrono::milliseconds>(
                    std::chrono::steady_clock::now().time_since_epoch()).count();
            m_adaptivePressureController.Observe(sample, nowMs);
            const auto snapshot = m_adaptivePressureController.Snapshot();

            if (snapshot.TargetFps != 0)
            {
                m_capturePublishFps.store(snapshot.TargetFps, std::memory_order_release);
                m_core11IngressFps.store(snapshot.TargetFps, std::memory_order_relaxed);
            }
            m_adaptivePressureState.store(
                static_cast<std::uint32_t>(snapshot.State),
                std::memory_order_relaxed);
            m_adaptivePressureTargetFps.store(snapshot.TargetFps, std::memory_order_relaxed);
            m_adaptivePressureStreak.store(snapshot.PressureStreak, std::memory_order_relaxed);
            m_adaptiveHealthyStreak.store(snapshot.HealthyStreak, std::memory_order_relaxed);
            m_adaptiveTransitions.store(snapshot.Transitions, std::memory_order_relaxed);
        }

        HRESULT EnsureCaptureRingResources(
            const D3D11_TEXTURE2D_DESC& sourceDesc)
        {
            std::lock_guard<std::mutex> lock(m_captureRingMutex);

            if (m_captureRingWidth == sourceDesc.Width &&
                m_captureRingHeight == sourceDesc.Height &&
                m_captureRingFormat == sourceDesc.Format &&
                m_captureRing[0].Texture)
            {
                return S_OK;
            }

            for (auto& slot : m_captureRing)
            {
                if (slot.Encoding)
                    return HRESULT_FROM_WIN32(ERROR_BUSY);
            }

            D3D11_TEXTURE2D_DESC desc = sourceDesc;
            desc.MipLevels = 1;
            desc.ArraySize = 1;
            desc.SampleDesc.Count = 1;
            desc.SampleDesc.Quality = 0;
            desc.Usage = D3D11_USAGE_DEFAULT;
            desc.CPUAccessFlags = 0;
            desc.MiscFlags = 0;
            desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;

            std::array<ComPtr<ID3D11Texture2D>, CaptureRingSize> textures;
            std::array<ComPtr<ID3D11Query>, CaptureRingSize> copyQueries;
            D3D11_QUERY_DESC queryDesc{};
            queryDesc.Query = D3D11_QUERY_EVENT;
            queryDesc.MiscFlags = 0;

            for (std::size_t i = 0; i < CaptureRingSize; ++i)
            {
                HRESULT hr = m_device->CreateTexture2D(
                    &desc, nullptr, textures[i].GetAddressOf());
                if (FAILED(hr))
                    return hr;

                hr = m_device->CreateQuery(
                    &queryDesc,
                    copyQueries[i].GetAddressOf());
                if (FAILED(hr))
                {
                    // Diagnostics must never make capture fail. Keep the
                    // original 11.17b streaming path and expose the HRESULT.
                    copyQueries[i].Reset();
                    m_copyGpuProbeStatus.store(hr, std::memory_order_relaxed);
                }
            }

            for (std::size_t i = 0; i < CaptureRingSize; ++i)
            {
                m_captureRing[i].Texture = textures[i];
                m_captureRing[i].CopyCompletionQuery = copyQueries[i];
                m_captureRing[i].FrameId = 0;
                m_captureRing[i].CopyProbeGeneration = 0;
                m_captureRing[i].CopyProbeIssued = false;
                m_captureRing[i].CopyProbePending = false;
                m_captureRing[i].CopyProbeCheckedAtSubmit = false;
                m_captureRing[i].CopyProbeFailed = false;
                m_captureRing[i].Ready = false;
                m_captureRing[i].Encoding = false;
            }

            m_captureRingWidth = sourceDesc.Width;
            m_captureRingHeight = sourceDesc.Height;
            m_captureRingFormat = sourceDesc.Format;
            return S_OK;
        }

        HRESULT PublishFrameToSameDeviceNvencRing(
            ID3D11Texture2D* source,
            const D3D11_TEXTURE2D_DESC& sourceDesc,
            std::uint64_t frameId,
            const std::chrono::steady_clock::time_point& timestamp)
        {
            if (!source || !m_context)
                return E_INVALIDARG;

            HRESULT hr = EnsureCaptureRingResources(sourceDesc);
            if (FAILED(hr))
            {
                m_ringBusyDrops.fetch_add(1);
                return hr;
            }

            std::size_t selected = CaptureRingSize;
            bool replaced = false;

            {
                std::lock_guard<std::mutex> lock(m_captureRingMutex);

                for (std::size_t i = 0; i < CaptureRingSize; ++i)
                {
                    if (!m_captureRing[i].Ready &&
                        !m_captureRing[i].Encoding)
                    {
                        selected = i;
                        break;
                    }
                }

                if (selected == CaptureRingSize)
                {
                    std::uint64_t oldest = UINT64_MAX;
                    for (std::size_t i = 0; i < CaptureRingSize; ++i)
                    {
                        if (m_captureRing[i].Ready &&
                            !m_captureRing[i].Encoding &&
                            m_captureRing[i].FrameId < oldest)
                        {
                            oldest = m_captureRing[i].FrameId;
                            selected = i;
                        }
                    }
                    replaced = selected != CaptureRingSize;
                }

                if (selected == CaptureRingSize)
                {
                    m_ringBusyDrops.fetch_add(1);
                    return S_FALSE;
                }

                m_captureRing[selected].Encoding = true; // producer owns slot during copy
                m_captureRing[selected].Ready = false;
            }

            const auto copyStart = std::chrono::steady_clock::now();
            // CORE 11.17e: preserve the 11.17d EVENT+Flush1 probe, but submit
            // the COPY command buffer immediately after the probe marker.
            // Flush1 is asynchronous: there is still no wait, Sleep, or GetData
            // loop on the producer path. This is the only performance variable
            // changed from the 11.17c baseline.
            (void)TryCompleteCaptureCopyGpuProbe(selected, false);
            m_context->CopyResource(
                m_captureRing[selected].Texture.Get(),
                source);
            BeginCaptureCopyGpuProbe(selected);
            if (m_context3)
            {
                m_context3->Flush1(
                    D3D11_CONTEXT_TYPE_COPY,
                    nullptr);
            }
            const auto copyEnd = std::chrono::steady_clock::now();
            const auto copyUs = std::chrono::duration_cast<std::chrono::microseconds>(
                copyEnd - copyStart).count();

            {
                std::lock_guard<std::mutex> lock(m_captureRingMutex);
                auto& slot = m_captureRing[selected];
                slot.FrameId = frameId;
                slot.Timestamp = timestamp;
                slot.Encoding = false;
                slot.Ready = m_encoderActive.load() &&
                    !m_encoderWorkerStop.load();

                if (!slot.Ready)
                    return S_FALSE;
            }

            m_ringPublished.fetch_add(1);
            m_lastPublishProgressUs.store(
                SteadyNowUs(),
                std::memory_order_relaxed);
            if (replaced)
                m_ringReplaced.fetch_add(1);
            m_lastRingCopyUs.store(copyUs);
            m_totalRingCopyUs.fetch_add(
                static_cast<std::uint64_t>(std::max<long long>(0, copyUs)));
            m_captureRingCv.notify_one();
            return S_OK;
        }

        HRESULT StartWgcCapture()
        {
            if (!m_device || !m_output)
                return E_FAIL;
            if (m_encoderLabInputMode != CORE11_MODE_WGC_SAME_DEVICE_DIRECT_RGB &&
                !m_encodeDevice)
            {
                return E_FAIL;
            }

            DXGI_OUTPUT_DESC outputDesc{};
            const HRESULT descHr = m_output->GetDesc(&outputDesc);
            if (FAILED(descHr) || !outputDesc.Monitor)
                return FAILED(descHr) ? descHr : E_FAIL;

            m_wgcFramesPublished.store(0);
            m_wgcPacingDrops.store(0);
            m_wgcLastStatus.store(S_OK);

            return m_wgcCapture.Start(
                m_device.Get(),
                outputDesc.Monitor,
                [this](
                    ID3D11Texture2D* texture,
                    const D3D11_TEXTURE2D_DESC& desc,
                    const std::chrono::steady_clock::time_point& arrival)
                {
                    OnWgcFrame(texture, desc, arrival);
                }, m_capturePublishFps.load(std::memory_order_relaxed));
        }

        void StopWgcCapture() noexcept
        {
            m_wgcCapture.Stop();
        }

        void OnWgcFrame(
            ID3D11Texture2D* texture,
            const D3D11_TEXTURE2D_DESC& desc,
            const std::chrono::steady_clock::time_point& arrival)
        {
            SetCoreWgcStage(1); // ENTER
            AtomicStageResetGuard stageReset{
                &m_coreWgcStage,
                &m_coreWgcStageChangedUs };

            if (!texture ||
                !m_encoderActive.load(std::memory_order_acquire) ||
                (m_encoderLabInputMode != CORE10_MODE_WGC_BGRA &&
                 m_encoderLabInputMode != CORE11_MODE_WGC_NV12 &&
                 m_encoderLabInputMode != CORE11_MODE_WGC_DIRECT_RGB &&
                 m_encoderLabInputMode != CORE11_MODE_WGC_RGB_ENCODE_SCALE &&
                 m_encoderLabInputMode != CORE11_MODE_WGC_NATIVE_RGB &&
                 m_encoderLabInputMode != CORE11_MODE_WGC_SAME_DEVICE_DIRECT_RGB))
            {
                return;
            }

            SetCoreWgcStage(2); // INGRESS_PACER
            const auto nowUs =
                std::chrono::duration_cast<std::chrono::microseconds>(
                    arrival.time_since_epoch()).count();
            const auto targetFps = m_capturePublishFps.load();

            // CORE 11.4: the remote clock still owns configured output cadence, but
            // capture-side full-frame copies use only a bounded sampling margin.
            // This is deliberately much cheaper than the old 2x/120 Hz policy
            // while retaining enough headroom for WGC/compositor jitter.
            const std::uint32_t ingressFps =
                m_encoderLabInputMode == CORE11_MODE_WGC_SAME_DEVICE_DIRECT_RGB
                    ? targetFps
                    : ((m_encoderLabInputMode == CORE11_MODE_WGC_NV12 ||
                        m_encoderLabInputMode == CORE11_MODE_WGC_DIRECT_RGB ||
                        m_encoderLabInputMode == CORE11_MODE_WGC_RGB_ENCODE_SCALE ||
                        m_encoderLabInputMode == CORE11_MODE_WGC_NATIVE_RGB)
                        ? ResolveCore11IngressFps(targetFps)
                        : targetFps);
            m_core11IngressFps.store(
                ingressFps,
                std::memory_order_relaxed);

            if (!m_ringPublishPacer.ShouldRun(nowUs, ingressFps))
            {
                m_wgcPacingDrops.fetch_add(1);
                m_ringPacingDrops.fetch_add(1);
                return;
            }

            SetCoreWgcStage(3); // FRAME_ID
            const auto frameId = m_frameCount.fetch_add(1) + 1;
            const HRESULT hr =
                m_encoderLabInputMode == CORE11_MODE_WGC_SAME_DEVICE_DIRECT_RGB
                    ? PublishFrameToSameDeviceNvencRing(
                        texture, desc, frameId, arrival)
                    : PublishFrameToCore10Ring(
                        texture, desc, frameId, arrival);
            SetCoreWgcStage(6); // COMMIT
            m_wgcLastStatus.store(hr);
            if (hr == S_OK)
                m_wgcFramesPublished.fetch_add(1);
        }

        HRESULT PublishFrameToCore10Ring(
            ID3D11Texture2D* source,
            const D3D11_TEXTURE2D_DESC& sourceDesc,
            std::uint64_t frameId,
            const std::chrono::steady_clock::time_point& timestamp)
        {
            if (!source || !m_context || !m_device || !m_encodeDevice)
                return E_INVALIDARG;

            SetCoreWgcStage(4); // ENSURE_RING
            const std::uint32_t ringTargetWidth =
                m_encoderLabInputMode == CORE11_MODE_WGC_DIRECT_RGB
                    ? m_encoderWidth
                    : sourceDesc.Width;
            const std::uint32_t ringTargetHeight =
                m_encoderLabInputMode == CORE11_MODE_WGC_DIRECT_RGB
                    ? m_encoderHeight
                    : sourceDesc.Height;

            HRESULT hr = m_core10Ring.EnsureResources(
                m_device.Get(),
                m_context.Get(),
                m_encodeDevice.Get(),
                m_encodeContext.Get(),
                sourceDesc,
                ringTargetWidth,
                ringTargetHeight);
            if (FAILED(hr))
            {
                if (hr == HRESULT_FROM_WIN32(ERROR_BUSY))
                    m_ringBusyDrops.fetch_add(1);
                return hr;
            }

            const auto copyStart = std::chrono::steady_clock::now();
            bool replaced = false;
            SetCoreWgcStage(5); // RING_PUBLISH
            hr = m_core10Ring.Publish(
                m_context.Get(),
                source,
                frameId,
                timestamp,
                &replaced);
            const auto copyEnd = std::chrono::steady_clock::now();
            const auto copyUs = std::chrono::duration_cast<std::chrono::microseconds>(
                copyEnd - copyStart).count();

            if (hr == S_OK)
            {
                m_ringPublished.fetch_add(1);
                m_lastPublishProgressUs.store(
                    SteadyNowUs(),
                    std::memory_order_relaxed);
                if (replaced)
                    m_ringReplaced.fetch_add(1);
                m_lastRingCopyUs.store(copyUs);
                m_totalRingCopyUs.fetch_add(
                    static_cast<std::uint64_t>(std::max<long long>(0, copyUs)));
            }
            else
            {
                if (hr == HRESULT_FROM_WIN32(ERROR_BUSY))
                    m_ringBusyDrops.fetch_add(1);
            }

            return hr;
        }

        HRESULT CreateSyntheticEncoderSource(
            std::uint32_t width,
            std::uint32_t height)
        {
            ResetSyntheticEncoderSource();
            ID3D11Device* syntheticDevice =
                m_encodeDevice ? m_encodeDevice.Get() : m_device.Get();
            if (!syntheticDevice || width == 0 || height == 0)
                return E_INVALIDARG;

            D3D11_TEXTURE2D_DESC desc{};
            desc.Width = width;
            desc.Height = height;
            desc.MipLevels = 1;
            desc.ArraySize = 1;
            desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
            desc.SampleDesc.Count = 1;
            desc.Usage = D3D11_USAGE_DEFAULT;
            desc.BindFlags = D3D11_BIND_RENDER_TARGET;

            HRESULT hr = syntheticDevice->CreateTexture2D(
                &desc, nullptr, m_syntheticTexture.GetAddressOf());
            if (FAILED(hr))
                return hr;

            hr = syntheticDevice->CreateRenderTargetView(
                m_syntheticTexture.Get(), nullptr,
                m_syntheticRtv.GetAddressOf());
            if (FAILED(hr))
            {
                ResetSyntheticEncoderSource();
                return hr;
            }

            m_syntheticFrameId = 0;
            m_nextSyntheticFrameTime = std::chrono::steady_clock::now();
            return S_OK;
        }

        void ResetSyntheticEncoderSource()
        {
            m_syntheticRtv.Reset();
            m_syntheticTexture.Reset();
            m_syntheticFrameId = 0;
            m_nextSyntheticFrameTime = {};
        }

        HRESULT RunSyntheticEncoderTick()
        {
#ifdef MULTIVERSE_NVENC
            if (!m_encoderActive.load() ||
                !m_encoder ||
                !m_encoder->IsRunning() ||
                !m_syntheticTexture ||
                !m_syntheticRtv ||
                !m_encodeContext)
            {
                std::this_thread::yield();
                return S_FALSE;
            }

            const auto frameDuration =
                std::chrono::duration_cast<std::chrono::steady_clock::duration>(
                    std::chrono::duration<double>(
                        1.0 / std::max<std::uint32_t>(1u, m_encoderFps)));

            auto now = std::chrono::steady_clock::now();
            if (m_nextSyntheticFrameTime ==
                std::chrono::steady_clock::time_point{})
            {
                m_nextSyntheticFrameTime = now;
            }

            if (now < m_nextSyntheticFrameTime)
            {
                if (!m_syntheticFrameTimer.WaitUntil(
                        m_nextSyntheticFrameTime))
                {
                    // Rare fallback for an unavailable waitable timer. This is
                    // local to the worker and does not alter system timer
                    // resolution.
                    std::this_thread::sleep_until(
                        m_nextSyntheticFrameTime);
                }
                now = std::chrono::steady_clock::now();
            }

            // Stable cadence: advance from the previous deadline. If GPU/OS
            // work falls more than one complete frame behind, resynchronize
            // instead of emitting a catch-up burst above the configured FPS.
            if (now > m_nextSyntheticFrameTime + frameDuration)
                m_nextSyntheticFrameTime = now + frameDuration;
            else
                m_nextSyntheticFrameTime += frameDuration;

            const std::uint64_t frameId = ++m_syntheticFrameId;
            const float phase =
                static_cast<float>(frameId % 240u) / 239.0f;
            const float color[4] =
            {
                0.04f + phase * 0.20f,
                0.08f + (1.0f - phase) * 0.12f,
                0.16f + phase * 0.18f,
                1.0f
            };

            m_encodeContext->ClearRenderTargetView(
                m_syntheticRtv.Get(), color);

            return SubmitFrame(
                m_syntheticTexture.Get(),
                frameId,
                m_encodeContext.Get());
#else
            return E_NOTIMPL;
#endif
        }

        bool WaitForRemoteFrameDeadline()
        {
            if (m_encoderWorkerStop.load(std::memory_order_acquire) ||
                !m_encoderActive.load(std::memory_order_acquire))
            {
                return false;
            }

            auto now = std::chrono::steady_clock::now();
            const auto deadline =
                m_remoteFrameDeadlineScheduler.GetOrInitializeDeadline(now);

            if (now < deadline)
            {
                if (!m_remoteFrameTimer.WaitUntil(deadline))
                    std::this_thread::sleep_until(deadline);
            }

            return
                !m_encoderWorkerStop.load(std::memory_order_acquire) &&
                m_encoderActive.load(std::memory_order_acquire);
        }

        void CommitRemoteFrameDeadline()
        {
            const std::uint32_t fps =
                std::clamp<std::uint32_t>(
                    m_capturePublishFps.load(std::memory_order_relaxed),
                    30u,
                    120u);
            const auto frameDuration =
                std::chrono::duration_cast<std::chrono::steady_clock::duration>(
                    std::chrono::duration<double>(
                        1.0 / static_cast<double>(fps)));

            const auto now = std::chrono::steady_clock::now();
            const auto deadline =
                m_remoteFrameDeadlineScheduler.GetOrInitializeDeadline(now);

            // Count lateness once per successfully submitted remote frame. A
            // ring miss no longer advances the deadline or inflates this
            // counter on every retry.
            if (now > deadline + std::chrono::milliseconds(2))
            {
                m_remoteClockLateTicks.fetch_add(
                    1,
                    std::memory_order_relaxed);
            }

            m_remoteFrameDeadlineScheduler.CommitSuccessfulFrame(
                now,
                frameDuration);
        }

        void EncoderLoop()
        {
            m_encoderWorkerAlive.store(true, std::memory_order_release);
            struct EncoderWorkerGuard
            {
                std::atomic<bool>& Alive;
                std::atomic<std::uint32_t>& Stage;
                std::atomic<long long>& StageChangedUs;
                ~EncoderWorkerGuard() noexcept
                {
                    Stage.store(0, std::memory_order_release);
                    StageChangedUs.store(SteadyNowUs(), std::memory_order_release);
                    Alive.store(false, std::memory_order_release);
                }
            } encoderWorkerGuard{
                m_encoderWorkerAlive,
                m_encoderWorkerStage,
                m_encoderWorkerStageChangedUs };

            // Keep the encode worker at normal priority. The transport sender
            // is AboveNormal and the UI is normal; the encoder must not starve
            // either one merely because NVENC or the GPU stalls.
            SetThreadPriority(
                GetCurrentThread(),
                THREAD_PRIORITY_NORMAL);

            while (!m_encoderWorkerStop.load())
            {
                SetEncoderWorkerStage(1); // LOOP_TOP
#ifdef MULTIVERSE_NVENC
                // Runtime NVENC ownership belongs to this worker. UI and
                // transport only publish commands/read snapshots.
                SetEncoderWorkerStage(2); // APPLY_COMMANDS
                ApplyPendingEncoderCommands();
                if (m_encoder && m_encoder->IsRunning())
                {
                    SetEncoderWorkerStage(3); // NVENC_PUMP
                    (void)m_encoder->Pump();
                }
                // Observational only: EVENT queries are polled with DONOTFLUSH.
                RefreshCaptureCopyGpuProbes();
                SetEncoderWorkerStage(4); // REFRESH_STATS
                RefreshEncoderStatsSnapshot();
#endif

                if (m_encoderActive.load() &&
                    m_encoderLabInputMode == LAB_MODE_SYNTHETIC)
                {
                    SetEncoderWorkerStage(5); // SYNTHETIC
                    (void)RunSyntheticEncoderTick();
                    SetEncoderWorkerStage(4); // REFRESH_STATS
                    RefreshEncoderStatsSnapshot();
                    continue;
                }

                if (m_encoderActive.load() &&
                    m_encoderLabInputMode == CORE11_MODE_WGC_SAME_DEVICE_DIRECT_RGB)
                {
                    // CORE 11.17e: the remote deadline follows the user-requested
                    // target. Pressure states are telemetry-only and never halve
                    // a requested 60-Hz stream. WGC and NVENC
                    // share one D3D11 device; the delivery worker copies the freshest
                    // frame once into the local ring and NVENC consumes it directly.
                    // No cross-device fence or blocking wait exists; Flush1(COPY)
                    // remains asynchronous from the 11.17d A/B baseline.
                    SetEncoderWorkerStage(15); // REMOTE_FRAME_DEADLINE
                    if (!WaitForRemoteFrameDeadline())
                    {
                        SetEncoderWorkerStage(4); // REFRESH_STATS
                        RefreshEncoderStatsSnapshot();
                        continue;
                    }

                    std::size_t selected = CaptureRingSize;
                    ComPtr<ID3D11Texture2D> source;

                    SetEncoderWorkerStage(16); // SAME_DEVICE_SELECT
                    {
                        std::unique_lock<std::mutex> lock(m_captureRingMutex);
                        m_captureRingCv.wait_for(
                            lock,
                            std::chrono::milliseconds(1),
                            [this]()
                            {
                                if (m_encoderWorkerStop.load())
                                    return true;
                                for (const auto& slot : m_captureRing)
                                {
                                    if (slot.Ready && !slot.Encoding)
                                        return true;
                                }
                                return false;
                            });

                        if (m_encoderWorkerStop.load())
                            break;

                        std::uint64_t newest = 0;
                        for (std::size_t i = 0; i < CaptureRingSize; ++i)
                        {
                            if (m_captureRing[i].Ready &&
                                !m_captureRing[i].Encoding &&
                                m_captureRing[i].FrameId >= newest)
                            {
                                newest = m_captureRing[i].FrameId;
                                selected = i;
                            }
                        }

                        if (selected != CaptureRingSize)
                        {
                            // latest-frame-wins: retire every older ready frame.
                            for (std::size_t i = 0; i < CaptureRingSize; ++i)
                            {
                                if (i != selected &&
                                    m_captureRing[i].Ready &&
                                    !m_captureRing[i].Encoding)
                                {
                                    m_captureRing[i].Ready = false;
                                    m_ringReplaced.fetch_add(1);
                                }
                            }

                            auto& slot = m_captureRing[selected];
                            slot.Ready = false;
                            slot.Encoding = true;
                            source = slot.Texture;
                        }
                    }

                    if (selected == CaptureRingSize || !source)
                    {
                        // Do not commit the expired deadline. The worker retries
                        // as soon as a fresh WGC frame arrives instead of paying
                        // another full 16.67-ms period.
                        SetEncoderWorkerStage(4); // REFRESH_STATS
                        RefreshEncoderStatsSnapshot();
                        UpdateCore11AdaptivePressureControl();
                        continue;
                    }

                    bool releaseHandled = false;
#ifdef MULTIVERSE_NVENC
                    if (m_encoderActive.load())
                    {
                        SetEncoderWorkerStage(17); // SAME_DEVICE_ENSURE
                        const HRESULT matchHr =
                            EnsureEncoderMatchesTexture(source.Get());

                        if (SUCCEEDED(matchHr))
                        {
                            const std::uint64_t submitFrameId =
                                ++m_remoteOutputFrameId;
                            SetEncoderWorkerStage(18); // SAME_DEVICE_SUBMIT
                            // CORE 11.17c DIAG: observe whether the copy EVENT
                            // is already complete at the submit boundary.
                            // DONOTFLUSH guarantees the probe never forces GPU
                            // progress and does not change pacing.
                            (void)TryCompleteCaptureCopyGpuProbe(selected, true);
                            const HRESULT submitHr =
                                SubmitSameDeviceRingFrame(
                                    selected,
                                    source.Get(),
                                    submitFrameId,
                                    &releaseHandled);

                            if (submitHr == S_OK)
                            {
                                CommitRemoteFrameDeadline();
                                m_ringConsumed.fetch_add(1);
                                m_lastConsumeProgressUs.store(
                                    SteadyNowUs(),
                                    std::memory_order_relaxed);
                            }
                        }
                    }
#endif

                    if (!releaseHandled)
                        ReleaseSameDeviceRingSlot(selected);

                    SetEncoderWorkerStage(4); // REFRESH_STATS
                    RefreshEncoderStatsSnapshot();
                    UpdateCore11AdaptivePressureControl();
                    continue;
                }

                if (m_encoderActive.load() &&
                    IsCore10IsolatedMode(m_encoderLabInputMode))
                {
                    const bool remoteClockMode =
                        m_encoderLabInputMode == CORE11_MODE_WGC_NV12 ||
                        m_encoderLabInputMode == CORE11_MODE_WGC_DIRECT_RGB ||
                        m_encoderLabInputMode == CORE11_MODE_WGC_RGB_ENCODE_SCALE ||
                        m_encoderLabInputMode == CORE11_MODE_WGC_NATIVE_RGB;
                    if (remoteClockMode)
                    {
                        SetEncoderWorkerStage(15); // REMOTE_FRAME_DEADLINE
                        if (!WaitForRemoteFrameDeadline())
                        {
                            SetEncoderWorkerStage(4); // REFRESH_STATS
                            RefreshEncoderStatsSnapshot();
                            continue;
                        }
                    }

                    Core10SharedFrameRing::Lease lease;
                    SetEncoderWorkerStage(6); // ACQUIRE_RING
                    const bool haveFreshFrame =
                        m_core10Ring.AcquireLatest(
                            lease,
                            std::chrono::milliseconds(4),
                            0,
                            m_encoderLabInputMode == CORE11_MODE_WGC_DIRECT_RGB);

#ifdef MULTIVERSE_NVENC
                    if (haveFreshFrame &&
                        lease.Texture &&
                        m_encoderActive.load())
                    {
                        SetEncoderWorkerStage(7); // ENSURE_ENCODER
                        const HRESULT matchHr =
                            EnsureEncoderMatchesTexture(lease.Texture.Get());

                        if (SUCCEEDED(matchHr))
                        {
                            const std::uint64_t submitFrameId =
                                remoteClockMode
                                    ? ++m_remoteOutputFrameId
                                    : lease.FrameId;

                            SetEncoderWorkerStage(8); // SUBMIT_FRAME
                            bool ringReleaseHandled = false;
                            const HRESULT submitHr =
                                m_encoderLabInputMode == CORE11_MODE_WGC_DIRECT_RGB
                                    ? SubmitDirectRingFrame(
                                        lease,
                                        submitFrameId,
                                        &ringReleaseHandled)
                                    : SubmitFrame(
                                        lease.Texture.Get(),
                                        submitFrameId,
                                        m_encodeContext.Get());

                            if (submitHr == S_OK)
                            {
                                if (remoteClockMode)
                                    CommitRemoteFrameDeadline();

                                m_ringConsumed.fetch_add(1);
                                m_lastConsumeProgressUs.store(
                                    SteadyNowUs(),
                                    std::memory_order_relaxed);
                            }

                        }
                    }
#endif

                    if (haveFreshFrame)
                    {
                        SetEncoderWorkerStage(9); // RELEASE_RING
                        if (m_encoderLabInputMode == CORE11_MODE_WGC_DIRECT_RGB)
                            m_core10Ring.ReleaseAfterExternalConsumer(lease);
                        else
                            m_core10Ring.Release(lease, m_encodeContext.Get());
                    }
                    SetEncoderWorkerStage(4); // REFRESH_STATS
                    RefreshEncoderStatsSnapshot();
                    UpdateCore11GpuSchedulingControl();
                    continue;
                }

                std::size_t selected = CaptureRingSize;
                std::uint64_t frameId = 0;
                ComPtr<ID3D11Texture2D> source;

                SetEncoderWorkerStage(10); // LEGACY_WAIT
                {
                    std::unique_lock<std::mutex> lock(m_captureRingMutex);
                    m_captureRingCv.wait_for(
                        lock,
                        std::chrono::milliseconds(4),
                        [this]()
                        {
                            if (m_encoderWorkerStop.load())
                                return true;
                            for (const auto& slot : m_captureRing)
                            {
                                if (slot.Ready && !slot.Encoding)
                                    return true;
                            }
                            return false;
                        });

                    if (m_encoderWorkerStop.load())
                        break;

                    SetEncoderWorkerStage(11); // LEGACY_SELECT
                    std::uint64_t newest = 0;
                    for (std::size_t i = 0; i < CaptureRingSize; ++i)
                    {
                        if (m_captureRing[i].Ready &&
                            !m_captureRing[i].Encoding &&
                            m_captureRing[i].FrameId >= newest)
                        {
                            newest = m_captureRing[i].FrameId;
                            selected = i;
                        }
                    }

                    if (selected == CaptureRingSize)
                        continue;

                    // Latest-frame-wins: retire every older ready frame.
                    for (std::size_t i = 0; i < CaptureRingSize; ++i)
                    {
                        if (i != selected &&
                            m_captureRing[i].Ready &&
                            !m_captureRing[i].Encoding)
                        {
                            m_captureRing[i].Ready = false;
                            m_ringReplaced.fetch_add(1);
                        }
                    }

                    auto& slot = m_captureRing[selected];
                    slot.Ready = false;
                    slot.Encoding = true;
                    frameId = slot.FrameId;
                    source = slot.Texture;
                }

#ifdef MULTIVERSE_NVENC
                if (source && m_encoderActive.load())
                {
                    SetEncoderWorkerStage(12); // LEGACY_ENSURE
                    const HRESULT matchHr =
                        EnsureEncoderMatchesTexture(source.Get());

                    if (SUCCEEDED(matchHr))
                    {
                        SetEncoderWorkerStage(13); // LEGACY_SUBMIT
                        const HRESULT submitHr =
                            SubmitFrame(
                                source.Get(),
                                frameId);

                        if (submitHr == S_OK)
                        {
                            m_ringConsumed.fetch_add(1);
                            m_lastConsumeProgressUs.store(
                                SteadyNowUs(),
                                std::memory_order_relaxed);
                        }
                    }
                }
#endif

                SetEncoderWorkerStage(14); // LEGACY_RELEASE
                {
                    std::lock_guard<std::mutex> lock(m_captureRingMutex);
                    if (selected < CaptureRingSize)
                        m_captureRing[selected].Encoding = false;
                }

                SetEncoderWorkerStage(4); // REFRESH_STATS
                RefreshEncoderStatsSnapshot();
            }
        }

        static void ResolveEncoderDimensions(
            std::uint32_t sourceWidth,
            std::uint32_t sourceHeight,
            std::uint32_t maxWidth,
            std::uint32_t maxHeight,
            std::uint32_t& targetWidth,
            std::uint32_t& targetHeight)
        {
            targetWidth = sourceWidth;
            targetHeight = sourceHeight;

            if (sourceWidth == 0 || sourceHeight == 0)
                return;

            if (maxWidth == 0 || maxHeight == 0 ||
                (sourceWidth <= maxWidth && sourceHeight <= maxHeight))
            {
                targetWidth &= ~1u;
                targetHeight &= ~1u;
                return;
            }

            const double scale =
                std::min(
                    static_cast<double>(maxWidth) /
                        static_cast<double>(sourceWidth),
                    static_cast<double>(maxHeight) /
                        static_cast<double>(sourceHeight));

            targetWidth =
                std::max<std::uint32_t>(
                    2u,
                    static_cast<std::uint32_t>(
                        std::floor(sourceWidth * scale))) & ~1u;

            targetHeight =
                std::max<std::uint32_t>(
                    2u,
                    static_cast<std::uint32_t>(
                        std::floor(sourceHeight * scale))) & ~1u;
        }

        void ResetEncoderScaleResources()
        {
            // Legacy capture-device scaler resources must be released explicitly
            // while the capture D3D11 device/context are still alive. In
            // particular, do not leave shader ComPtrs for member destruction
            // after m_context/m_device have already been torn down.
            m_scaleSourceSrv.Reset();
            m_scaleTargetRtv.Reset();
            m_encoderScaleTexture.Reset();
            m_scaleSampler.Reset();
            m_scalePixelShader.Reset();
            m_scaleVertexShader.Reset();
            m_scaleSourceWidth = 0;
            m_scaleSourceHeight = 0;
            m_scaleTargetWidth = 0;
            m_scaleTargetHeight = 0;
            m_scaleFormat = DXGI_FORMAT_UNKNOWN;
            m_scaleSourceIdentity = nullptr;
        }

        HRESULT EnsureScaleShaders()
        {
            if (m_scaleVertexShader &&
                m_scalePixelShader &&
                m_scaleSampler)
            {
                return S_OK;
            }

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
                "MultiverseScaleVS",
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
                "MultiverseScalePS",
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

            hr = m_device->CreateVertexShader(
                vsBlob->GetBufferPointer(),
                vsBlob->GetBufferSize(),
                nullptr,
                m_scaleVertexShader.GetAddressOf());

            if (FAILED(hr))
                return hr;

            hr = m_device->CreatePixelShader(
                psBlob->GetBufferPointer(),
                psBlob->GetBufferSize(),
                nullptr,
                m_scalePixelShader.GetAddressOf());

            if (FAILED(hr))
                return hr;

            D3D11_SAMPLER_DESC sampler{};
            sampler.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
            sampler.AddressU = D3D11_TEXTURE_ADDRESS_CLAMP;
            sampler.AddressV = D3D11_TEXTURE_ADDRESS_CLAMP;
            sampler.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
            sampler.MinLOD = 0.0f;
            sampler.MaxLOD = D3D11_FLOAT32_MAX;

            return m_device->CreateSamplerState(
                &sampler,
                m_scaleSampler.GetAddressOf());
        }

        HRESULT EnsureEncoderScaleResources(
            ID3D11Texture2D* source,
            std::uint32_t targetWidth,
            std::uint32_t targetHeight)
        {
            if (!source || !m_device || !m_context)
                return E_INVALIDARG;

            D3D11_TEXTURE2D_DESC sourceDesc{};
            source->GetDesc(&sourceDesc);

            if (sourceDesc.Width == targetWidth &&
                sourceDesc.Height == targetHeight)
            {
                return S_FALSE;
            }

            if (m_encoderScaleTexture &&
                m_scaleSourceSrv &&
                m_scaleTargetRtv &&
                m_scaleSourceWidth == sourceDesc.Width &&
                m_scaleSourceHeight == sourceDesc.Height &&
                m_scaleTargetWidth == targetWidth &&
                m_scaleTargetHeight == targetHeight &&
                m_scaleFormat == sourceDesc.Format &&
                m_scaleSourceIdentity == source)
            {
                return S_OK;
            }

            ResetEncoderScaleResources();

            HRESULT hr = EnsureScaleShaders();
            if (FAILED(hr))
                return hr;

            hr = m_device->CreateShaderResourceView(
                source,
                nullptr,
                m_scaleSourceSrv.GetAddressOf());

            if (FAILED(hr))
                return hr;

            D3D11_TEXTURE2D_DESC targetDesc{};
            targetDesc.Width = targetWidth;
            targetDesc.Height = targetHeight;
            targetDesc.MipLevels = 1;
            targetDesc.ArraySize = 1;
            targetDesc.Format = sourceDesc.Format;
            targetDesc.SampleDesc.Count = 1;
            targetDesc.Usage = D3D11_USAGE_DEFAULT;
            targetDesc.BindFlags =
                D3D11_BIND_RENDER_TARGET |
                D3D11_BIND_SHADER_RESOURCE;

            hr = m_device->CreateTexture2D(
                &targetDesc,
                nullptr,
                m_encoderScaleTexture.GetAddressOf());

            if (FAILED(hr))
            {
                ResetEncoderScaleResources();
                return hr;
            }

            hr = m_device->CreateRenderTargetView(
                m_encoderScaleTexture.Get(),
                nullptr,
                m_scaleTargetRtv.GetAddressOf());

            if (FAILED(hr))
            {
                ResetEncoderScaleResources();
                return hr;
            }

            m_scaleSourceWidth = sourceDesc.Width;
            m_scaleSourceHeight = sourceDesc.Height;
            m_scaleTargetWidth = targetWidth;
            m_scaleTargetHeight = targetHeight;
            m_scaleFormat = sourceDesc.Format;
            m_scaleSourceIdentity = source;

            return S_OK;
        }

        HRESULT PrepareEncoderSource(
            ID3D11Texture2D* source,
            ID3D11Texture2D** encoderSource)
        {
            if (!source || !encoderSource)
                return E_INVALIDARG;

            *encoderSource = source;

            D3D11_TEXTURE2D_DESC sourceDesc{};
            source->GetDesc(&sourceDesc);

            if (sourceDesc.Width == m_encoderWidth &&
                sourceDesc.Height == m_encoderHeight)
            {
                return S_OK;
            }

            HRESULT hr = EnsureEncoderScaleResources(
                source,
                m_encoderWidth,
                m_encoderHeight);

            if (FAILED(hr))
                return hr;

            D3D11_VIEWPORT viewport{};
            viewport.TopLeftX = 0.0f;
            viewport.TopLeftY = 0.0f;
            viewport.Width = static_cast<float>(m_encoderWidth);
            viewport.Height = static_cast<float>(m_encoderHeight);
            viewport.MinDepth = 0.0f;
            viewport.MaxDepth = 1.0f;

            ID3D11RenderTargetView* rtv =
                m_scaleTargetRtv.Get();

            ID3D11ShaderResourceView* srv =
                m_scaleSourceSrv.Get();

            ID3D11SamplerState* sampler =
                m_scaleSampler.Get();

            m_context->OMSetRenderTargets(1, &rtv, nullptr);
            m_context->RSSetViewports(1, &viewport);
            m_context->IASetInputLayout(nullptr);
            m_context->IASetPrimitiveTopology(
                D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            m_context->VSSetShader(
                m_scaleVertexShader.Get(),
                nullptr,
                0);
            m_context->PSSetShader(
                m_scalePixelShader.Get(),
                nullptr,
                0);
            m_context->PSSetShaderResources(0, 1, &srv);
            m_context->PSSetSamplers(0, 1, &sampler);
            m_context->Draw(3, 0);

            ID3D11ShaderResourceView* nullSrv = nullptr;
            m_context->PSSetShaderResources(0, 1, &nullSrv);
            m_context->OMSetRenderTargets(0, nullptr, nullptr);

            *encoderSource =
                m_encoderScaleTexture.Get();

            return S_OK;
        }

        void SetEncoderMessage(
            const std::wstring& message)
        {
            std::lock_guard<std::mutex> lock(
                m_encoderMessageMutex);
            m_encoderMessage = message;
        }

        HRESULT CreateDesktopDuplication(
            IDXGIOutput* output)
        {
            if (!output || !m_device)
                return E_INVALIDARG;

            m_duplication.Reset();

            // Windows 10+ DuplicateOutput1 avoids some implicit format
            // conversion paths and is the preferred DXGI duplication entry
            // point used by mature Windows streaming hosts. Keep BGRA because
            // the NV12 video preprocessor consumes BGRA directly.
            ComPtr<IDXGIOutput5> output5;

            if (SUCCEEDED(
                    output->QueryInterface(
                        IID_PPV_ARGS(
                            output5.GetAddressOf()))))
            {
                const DXGI_FORMAT formats[] =
                {
                    DXGI_FORMAT_B8G8R8A8_UNORM
                };

                const HRESULT hr =
                    output5->DuplicateOutput1(
                        m_device.Get(),
                        0,
                        1u,
                        formats,
                        m_duplication.GetAddressOf());

                if (SUCCEEDED(hr))
                    return S_OK;
            }

            ComPtr<IDXGIOutput1> output1;
            HRESULT hr =
                output->QueryInterface(
                    IID_PPV_ARGS(
                        output1.GetAddressOf()));

            if (FAILED(hr))
                return hr;

            return output1->DuplicateOutput(
                m_device.Get(),
                m_duplication.GetAddressOf());
        }

        HRESULT RecreateDuplication()
        {
            if (m_deviceName.empty() ||
                !m_device)
            {
                return E_FAIL;
            }

            m_duplication.Reset();

            ComPtr<IDXGIAdapter1> adapter;
            ComPtr<IDXGIOutput> output;

            HRESULT hr =
                FindOutputAndAdapter(
                    m_deviceName.c_str(),
                    adapter,
                    output);

            if (FAILED(hr))
                return hr;

            hr =
                CreateDesktopDuplication(
                    output.Get());

            if (FAILED(hr))
                return hr;

            m_adapter = adapter;
            m_output = output;
            m_lastError.store(S_OK);
            m_duplicationResets.fetch_add(1);

            return S_OK;
        }

#ifdef MULTIVERSE_NVENC
        HRESULT EnsureEncoderMatchesTexture(
            ID3D11Texture2D* source)
        {
            if (!m_encoderActive.load() || !source)
                return S_FALSE;

            D3D11_TEXTURE2D_DESC sourceDesc{};
            source->GetDesc(&sourceDesc);

            if (sourceDesc.Width == 0 || sourceDesc.Height == 0)
                return S_FALSE;

            std::uint32_t desiredWidth = 0;
            std::uint32_t desiredHeight = 0;

            ResolveEncoderDimensions(
                sourceDesc.Width,
                sourceDesc.Height,
                m_encoderMaxWidth,
                m_encoderMaxHeight,
                desiredWidth,
                desiredHeight);

            if (m_encoderLabInputMode == CORE11_MODE_WGC_NATIVE_RGB ||
                (IsCore10IsolatedMode(m_encoderLabInputMode) &&
                 m_encoderLabInputMode != CORE11_MODE_WGC_NV12 &&
                 m_encoderLabInputMode != CORE11_MODE_WGC_DIRECT_RGB &&
                 m_encoderLabInputMode != CORE11_MODE_WGC_RGB_ENCODE_SCALE &&
                 m_encoderLabInputMode != CORE11_MODE_WGC_NATIVE_RGB))
            {
                desiredWidth = sourceDesc.Width & ~1u;
                desiredHeight = sourceDesc.Height & ~1u;
            }

            const bool sameDeviceNeedsScale =
                m_encoderLabInputMode == CORE11_MODE_WGC_SAME_DEVICE_DIRECT_RGB &&
                (desiredWidth != sourceDesc.Width || desiredHeight != sourceDesc.Height);

            if (m_encoder &&
                m_encoder->IsRunning() &&
                m_encoderSourceWidth == sourceDesc.Width &&
                m_encoderSourceHeight == sourceDesc.Height &&
                m_encoderWidth == desiredWidth &&
                m_encoderHeight == desiredHeight)
            {
                return S_OK;
            }

            const auto now =
                std::chrono::steady_clock::now();

            if (m_nextEncoderRestartAttempt !=
                    std::chrono::steady_clock::time_point{} &&
                now < m_nextEncoderRestartAttempt)
            {
                return S_FALSE;
            }

            if (m_encoder)
            {
                m_encoder->Stop();
                m_encoder.reset();
            }

            auto encoder =
                std::make_unique<AsyncNvencD3D11>();

            const bool core10Isolated =
                IsCore10IsolatedMode(m_encoderLabInputMode);
            ID3D11Device* encoderDevice =
                core10Isolated ? m_encodeDevice.Get() : m_device.Get();
            const std::uint32_t nvencInputMode =
                m_encoderLabInputMode == CORE11_MODE_WGC_SAME_DEVICE_DIRECT_RGB
                    ? (sameDeviceNeedsScale
                        ? LAB_MODE_ARGB_SCALE
                        : LAB_MODE_EXTERNAL_ARGB)
                    : (m_encoderLabInputMode == CORE11_MODE_WGC_NATIVE_RGB
                        ? LAB_MODE_ARGB
                        : (m_encoderLabInputMode == CORE11_MODE_WGC_RGB_ENCODE_SCALE
                            ? LAB_MODE_ARGB_SCALE
                            : (m_encoderLabInputMode == CORE11_MODE_WGC_DIRECT_RGB
                                ? LAB_MODE_EXTERNAL_ARGB
                                : (m_encoderLabInputMode == CORE11_MODE_WGC_NV12
                                    ? LAB_MODE_NV12
                                    : (core10Isolated ? LAB_MODE_ARGB : m_encoderLabInputMode)))));

            const NVENCSTATUS status =
                encoder->Start(
                    encoderDevice,
                    sourceDesc.Width,
                    sourceDesc.Height,
                    desiredWidth,
                    desiredHeight,
                    std::clamp<std::uint32_t>(
                        m_encoderFps,
                        30u,
                        120u),
                    std::max<std::uint32_t>(
                        2000u,
                        m_encoderBitrateKbps),
                    m_encoderPreset,
                    m_encoderUltraLowLatency,
                    nvencInputMode,
                    m_packetQueue);

            if (status != NV_ENC_SUCCESS)
            {
                const HRESULT hr =
                    NvencStatusToHresult(
                        status);

                m_encoderStatus.store(hr);
                SetEncoderMessage(
                    L"NVENC aguardando recuperação após troca de modo: " +
                    NvencStatusText(status));
                m_nextEncoderRestartAttempt =
                    now +
                    std::chrono::milliseconds(250);
                return hr;
            }

            if (m_packetConsumerActive.load())
            {
                encoder->SetPacketConsumerActive(
                    true);
            }

            m_encoder =
                std::move(encoder);
            m_encoderSourceWidth =
                sourceDesc.Width;
            m_encoderSourceHeight =
                sourceDesc.Height;
            m_encoderWidth =
                desiredWidth;
            m_encoderHeight =
                desiredHeight;
            m_encoderStatus.store(S_OK);
            m_nextEncodeTime = now;
            m_nextEncoderRestartAttempt =
                std::chrono::steady_clock::time_point{};
            m_encoderRestarts.fetch_add(1);

            SetEncoderMessage(
                L"NVENC recriado automaticamente após troca de resolução/modo.");

            return S_OK;
        }
#endif

        HRESULT EnsureFrameTextures(
            const D3D11_TEXTURE2D_DESC& sourceDesc)
        {
            const std::uint32_t currentTextureWidth =
                m_textureWidth.load(std::memory_order_acquire);

            const std::uint32_t currentTextureHeight =
                m_textureHeight.load(std::memory_order_acquire);

            const DXGI_FORMAT currentTextureFormat =
                m_textureFormat.load(std::memory_order_acquire);

            if (m_latestTexture &&
                m_stagingTexture &&
                currentTextureWidth ==
                    sourceDesc.Width &&
                currentTextureHeight ==
                    sourceDesc.Height &&
                currentTextureFormat ==
                    sourceDesc.Format)
            {
                return S_OK;
            }

            m_latestTexture.Reset();
            m_stagingTexture.Reset();

            D3D11_TEXTURE2D_DESC
                latestDesc{};

            latestDesc.Width =
                sourceDesc.Width;

            latestDesc.Height =
                sourceDesc.Height;

            latestDesc.MipLevels = 1;
            latestDesc.ArraySize = 1;

            latestDesc.Format =
                sourceDesc.Format;

            latestDesc.SampleDesc.Count = 1;

            latestDesc.Usage =
                D3D11_USAGE_DEFAULT;
            latestDesc.BindFlags =
                D3D11_BIND_SHADER_RESOURCE;

            HRESULT hr =
                m_device->CreateTexture2D(
                    &latestDesc,
                    nullptr,
                    m_latestTexture.GetAddressOf());

            if (FAILED(hr))
                return hr;

            D3D11_TEXTURE2D_DESC
                stagingDesc =
                    latestDesc;

            stagingDesc.Usage =
                D3D11_USAGE_STAGING;
            stagingDesc.BindFlags = 0;

            stagingDesc.CPUAccessFlags =
                D3D11_CPU_ACCESS_READ;

            hr =
                m_device->CreateTexture2D(
                    &stagingDesc,
                    nullptr,
                    m_stagingTexture.GetAddressOf());

            if (FAILED(hr))
            {
                m_latestTexture.Reset();
                return hr;
            }

            m_textureWidth.store(
                sourceDesc.Width,
                std::memory_order_release);

            m_textureHeight.store(
                sourceDesc.Height,
                std::memory_order_release);

            m_textureFormat.store(
                sourceDesc.Format,
                std::memory_order_release);

            return S_OK;
        }

        HRESULT GeneratePreview(
            std::uint64_t frameId)
        {
            if (!m_latestTexture ||
                !m_stagingTexture)
            {
                return S_FALSE;
            }

            m_context->CopyResource(
                m_stagingTexture.Get(),
                m_latestTexture.Get());

            D3D11_MAPPED_SUBRESOURCE
                mapped{};

            HRESULT hr =
                m_context->Map(
                    m_stagingTexture.Get(),
                    0,
                    D3D11_MAP_READ,
                    0,
                    &mapped);

            if (FAILED(hr))
                return hr;

            std::vector<std::uint8_t>
                localPreview;

            try
            {
                localPreview.resize(
                    m_requiredBufferBytes);
            }
            catch (...)
            {
                m_context->Unmap(
                    m_stagingTexture.Get(),
                    0);

                return E_OUTOFMEMORY;
            }

            const auto* sourceBase =
                static_cast<const std::uint8_t*>(
                    mapped.pData);

            const std::uint32_t textureWidth =
                m_textureWidth.load(std::memory_order_acquire);

            const std::uint32_t textureHeight =
                m_textureHeight.load(std::memory_order_acquire);

            if (textureWidth == 0u ||
                textureHeight == 0u)
            {
                m_context->Unmap(
                    m_stagingTexture.Get(),
                    0);

                return S_FALSE;
            }

            for (std::uint32_t y = 0;
                 y < m_previewHeight;
                 ++y)
            {
                const std::uint32_t sourceY =
                    std::min<std::uint32_t>(
                        textureHeight - 1,
                        static_cast<std::uint32_t>(
                            (static_cast<std::uint64_t>(y) *
                             textureHeight) /
                            m_previewHeight));

                const auto* sourceRow =
                    sourceBase +
                    static_cast<std::size_t>(
                        sourceY) *
                    mapped.RowPitch;

                auto* destinationRow =
                    localPreview.data() +
                    static_cast<std::size_t>(
                        y) *
                    m_previewStride;

                for (std::uint32_t x = 0;
                     x < m_previewWidth;
                     ++x)
                {
                    const std::uint32_t sourceX =
                        std::min<std::uint32_t>(
                            textureWidth - 1,
                            static_cast<std::uint32_t>(
                                (static_cast<std::uint64_t>(x) *
                                 textureWidth) /
                                m_previewWidth));

                    const auto* sourcePixel =
                        sourceRow +
                        static_cast<std::size_t>(
                            sourceX) *
                        4u;

                    auto* destinationPixel =
                        destinationRow +
                        static_cast<std::size_t>(
                            x) *
                        4u;

                    destinationPixel[0] =
                        sourcePixel[0];

                    destinationPixel[1] =
                        sourcePixel[1];

                    destinationPixel[2] =
                        sourcePixel[2];

                    destinationPixel[3] =
                        0xFF;
                }
            }

            m_context->Unmap(
                m_stagingTexture.Get(),
                0);

            {
                std::lock_guard<std::mutex> lock(
                    m_previewMutex);

                m_previewBuffer.swap(
                    localPreview);

                m_previewFrameId =
                    frameId;
            }

            return S_OK;
        }

        void ReleaseSameDeviceRingSlot(
            std::size_t selected) noexcept
        {
            if (selected >= CaptureRingSize)
                return;

            {
                std::lock_guard<std::mutex> lock(m_captureRingMutex);
                auto& slot = m_captureRing[selected];
                slot.Ready = false;
                slot.Encoding = false;
            }
            m_captureRingCv.notify_one();
        }

#ifdef MULTIVERSE_NVENC
        bool ShouldDeferNewFrame()
        {
            return m_encoder && m_encoder->ShouldDeferNewFrame();
        }

        void PreserveIdrOnFailedSubmit(NVENCSTATUS status, bool forceIdr)
        {
            if (forceIdr && status != NV_ENC_SUCCESS &&
                status != NV_ENC_ERR_NEED_MORE_INPUT && m_encoder)
                m_encoder->RestoreForceIdr();
        }

        HRESULT SubmitDirectRingFrame(
            Core10SharedFrameRing::Lease& lease,
            std::uint64_t frameId,
            bool* ringReleaseHandled)
        {
            if (ringReleaseHandled)
                *ringReleaseHandled = false;
            if (!lease || !lease.Texture || !m_encoder || !m_encoder->IsRunning())
                return S_FALSE;
            if (ShouldDeferNewFrame())
                return S_FALSE;

            auto leaseHolder =
                std::make_shared<Core10SharedFrameRing::Lease>(lease);
            const bool forceIdr = m_encoder->ConsumeForceIdr();
            bool ownershipTransferred = false;
            const NVENCSTATUS status = m_encoder->SubmitExternalArgb(
                lease.Texture.Get(),
                frameId,
                forceIdr,
                [this, leaseHolder]() mutable
                {
                    if (leaseHolder && *leaseHolder)
                        m_core10Ring.ReleaseAfterExternalConsumer(*leaseHolder);
                },
                &ownershipTransferred);

            if (ownershipTransferred)
            {
                // From this point the NVENC slot/callback is the single owner
                // of the ring lease. It releases only after completion/unmap.
                lease = {};
                if (ringReleaseHandled)
                    *ringReleaseHandled = true;
            }

            PreserveIdrOnFailedSubmit(status, forceIdr);

            if (status == NV_ENC_ERR_ENCODER_BUSY)
            {
                m_encoderStatus.store(S_OK);
                return S_FALSE;
            }

            if (status == NV_ENC_ERR_NEED_MORE_INPUT ||
                status == NV_ENC_SUCCESS)
            {
                m_encoderStatus.store(S_OK);
                return S_OK;
            }

            const HRESULT hr = NvencStatusToHresult(status);
            m_encoderStatus.store(hr);
            SetEncoderMessage(
                L"CORE 11.13: erro ao enviar shared-ring RGB direto ao NVENC: " +
                NvencStatusText(status));
            m_encoder->RequestIdr();
            return hr;
        }

        HRESULT SubmitSameDeviceRingFrame(
            std::size_t selected,
            ID3D11Texture2D* source,
            std::uint64_t frameId,
            bool* ringReleaseHandled)
        {
            if (ringReleaseHandled)
                *ringReleaseHandled = false;
            if (selected >= CaptureRingSize || !source ||
                !m_encoder || !m_encoder->IsRunning())
            {
                return S_FALSE;
            }

            if (ShouldDeferNewFrame())
                return S_FALSE;

            const bool scaledSameDevice =
                m_encoderSourceWidth != m_encoderWidth ||
                m_encoderSourceHeight != m_encoderHeight;
            if (scaledSameDevice && !m_context)
                return E_FAIL;
            const bool forceIdr = m_encoder->ConsumeForceIdr();

            NVENCSTATUS status = NV_ENC_SUCCESS;
            if (scaledSameDevice)
            {
                // CORE 11.56: keep the native latest-frame ring and only scale
                // the frame selected by the 60-Hz encoder deadline. The scaler
                // writes into a private NVENC slot, so the ring slot can be
                // released immediately after this call; D3D11 immediate-context
                // ordering preserves the queued read before any later ring copy.
                status = m_encoder->Submit(
                    m_context.Get(),
                    source,
                    frameId,
                    forceIdr);
            }
            else
            {
                bool ownershipTransferred = false;
                status = m_encoder->SubmitExternalArgb(
                    source,
                    frameId,
                    forceIdr,
                    [this, selected]()
                    {
                        // The capture ring slot remains immutable while NVENC owns
                        // the mapped external resource. Completion/unmap releases it.
                        ReleaseSameDeviceRingSlot(selected);
                    },
                    &ownershipTransferred);

                if (ownershipTransferred && ringReleaseHandled)
                    *ringReleaseHandled = true;
            }

            PreserveIdrOnFailedSubmit(status, forceIdr);

            if (status == NV_ENC_ERR_ENCODER_BUSY)
            {
                m_encoderStatus.store(S_OK);
                return S_FALSE;
            }

            if (status == NV_ENC_ERR_NEED_MORE_INPUT ||
                status == NV_ENC_SUCCESS)
            {
                m_encoderStatus.store(S_OK);
                return S_OK;
            }

            const HRESULT hr = NvencStatusToHresult(status);
            m_encoderStatus.store(hr);
            SetEncoderMessage(
                scaledSameDevice
                    ? L"CORE 11.56: erro ao reduzir RGB same-device para o NVENC: " +
                        NvencStatusText(status)
                    : L"CORE 11.56: erro ao enviar RGB same-device direto ao NVENC: " +
                        NvencStatusText(status));
            m_encoder->RequestIdr();
            return hr;
        }

        HRESULT SubmitFrame(
            ID3D11Texture2D* source,
            std::uint64_t frameId,
            ID3D11DeviceContext* submitContext = nullptr)
        {
#ifdef MULTIVERSE_NVENC
            if (!m_encoder ||
                !m_encoder->IsRunning())
            {
                return S_FALSE;
            }

            if (ShouldDeferNewFrame())
                return S_FALSE;

            ID3D11Texture2D* encoderSource =
                source;

            // CORE 11 uses the native shared BGRA source directly. Its isolated
            // EncodeDevice VideoProcessor owns BOTH scaling (for example
            // 2560x1440 -> 1920x1080) and BGRA -> NV12 conversion. Never route
            // CORE11_MODE_WGC_NV12 through the legacy capture-device shader
            // scaler: doing so recreates capture/encode contention and leaves
            // legacy D3D11 shader objects alive during capture-device teardown.
            if (m_encoderLabInputMode == CORE11_MODE_WGC_NV12 ||
                m_encoderLabInputMode == CORE11_MODE_WGC_RGB_ENCODE_SCALE ||
                m_encoderLabInputMode == CORE11_MODE_WGC_NATIVE_RGB)
            {
                // CORE 11.15 keeps the shared-ring texture native and uses the
                // normal private-ARGB path, whose only preprocess work is a
                // same-size CopyResource on the EncodeDevice. CORE 11.14 keeps
                // its RGB shader fallback and CORE 11.12 keeps NV12/VP.
                encoderSource = source;
            }
            else if (IsCore10IsolatedMode(m_encoderLabInputMode))
            {
                // Legacy isolated BGRA diagnostics submit a direct CopyResource,
                // so their source/output dimensions must continue to match.
                D3D11_TEXTURE2D_DESC sourceDesc{};
                source->GetDesc(&sourceDesc);
                if (sourceDesc.Width != m_encoderWidth ||
                    sourceDesc.Height != m_encoderHeight)
                {
                    const HRESULT sizeHr = HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
                    m_encoderStatus.store(sizeHr);
                    SetEncoderMessage(
                        L"CORE 10: resolução da captura mudou; aguardando restart nativo do encoder.");
                    return sizeHr;
                }
            }
            else
            {
                const HRESULT scaleHr =
                    PrepareEncoderSource(
                        source,
                        &encoderSource);

                if (FAILED(scaleHr))
                {
                    m_encoderStatus.store(scaleHr);
                    SetEncoderMessage(
                        L"Falha no downscale GPU legado do stream remoto.");
                    return scaleHr;
                }
            }

            ID3D11DeviceContext* activeSubmitContext =
                submitContext ? submitContext : m_context.Get();
            if (!activeSubmitContext)
                return E_FAIL;

            const bool forceIdr =
                m_encoder->ConsumeForceIdr();

            const NVENCSTATUS status =
                m_encoder->Submit(
                    activeSubmitContext,
                    encoderSource,
                    frameId,
                    forceIdr);

            PreserveIdrOnFailedSubmit(status, forceIdr);

            if (status == NV_ENC_ERR_ENCODER_BUSY)
            {
                m_encoderStatus.store(S_OK);
                return S_FALSE;
            }

            if (status == NV_ENC_ERR_NEED_MORE_INPUT)
            {
                // The input was accepted by NVENC and remains owned by its
                // slot until ordered output drain. It is not a dropped frame.
                m_encoderStatus.store(S_OK);
                return S_OK;
            }

            if (status != NV_ENC_SUCCESS)
            {
                const HRESULT hr =
                    NvencStatusToHresult(
                        status);

                m_encoderStatus.store(hr);

                SetEncoderMessage(
                    L"Erro ao enviar frame ao NVENC async: " +
                    NvencStatusText(status));

                m_encoder->RequestIdr();

                return hr;
            }

            m_encoderStatus.store(S_OK);

            return S_OK;
#else
            (void)source;
            (void)frameId;
            return E_NOTIMPL;
#endif
        }
#endif

        void CaptureLoop()
        {
            // Stable scheduling baseline: capture may be slightly favored,
            // but must never become a CRITICAL/HIGHEST thread that starves the
            // WinUI dispatcher, transport, game or DWM.
            SetThreadPriority(
                GetCurrentThread(),
                THREAD_PRIORITY_ABOVE_NORMAL);

            while (!m_stop.load())
            {
                // LAB C intentionally pauses Desktop Duplication while the
                // synthetic source is running. This isolates the encode path
                // from capture/device synchronization; preview freezes by design.
                if (m_encoderActive.load() &&
                    m_encoderLabInputMode == LAB_MODE_SYNTHETIC)
                {
                    Sleep(4);
                    continue;
                }

                // WGC owns capture delivery on its free-threaded frame-pool worker.
                // Keep Desktop Duplication idle so it cannot compete for the capture
                // immediate context while a foreground game saturates the GPU.
                if (m_encoderActive.load() &&
                    (m_encoderLabInputMode == CORE10_MODE_WGC_BGRA ||
                     m_encoderLabInputMode == CORE11_MODE_WGC_NV12 ||
                     m_encoderLabInputMode == CORE11_MODE_WGC_DIRECT_RGB ||
                     m_encoderLabInputMode == CORE11_MODE_WGC_RGB_ENCODE_SCALE ||
                     m_encoderLabInputMode == CORE11_MODE_WGC_NATIVE_RGB ||
                     m_encoderLabInputMode == CORE11_MODE_WGC_SAME_DEVICE_DIRECT_RGB))
                {
                    Sleep(4);
                    continue;
                }

                DXGI_OUTDUPL_FRAME_INFO
                    frameInfo{};

                ComPtr<IDXGIResource>
                    desktopResource;

                const UINT acquireTimeoutMs =
                    m_encoderActive.load()
                        ? 5u
                        : 50u;

                const auto acquireStart =
                    std::chrono::steady_clock::now();

                HRESULT hr =
                    m_duplication->AcquireNextFrame(
                        acquireTimeoutMs,
                        &frameInfo,
                        desktopResource.GetAddressOf());

                const auto acquireEnd =
                    std::chrono::steady_clock::now();
                const auto acquireUs =
                    std::chrono::duration_cast<std::chrono::microseconds>(
                        acquireEnd - acquireStart).count();
                m_lastAcquireUs.store(acquireUs);
                m_totalAcquireUs.fetch_add(
                    static_cast<std::uint64_t>(
                        std::max<long long>(0, acquireUs)));
                m_acquireSamples.fetch_add(1);

                if (hr == DXGI_ERROR_WAIT_TIMEOUT)
                {
                    m_acquireTimeouts.fetch_add(1);
                    continue;
                }

                if (FAILED(hr))
                {
                    m_lastError.store(hr);

                    if (IsRecoverableDuplicationError(
                            hr))
                    {
                        bool recovered = false;

                        for (int attempt = 0;
                             attempt < 20 &&
                             !m_stop.load();
                             ++attempt)
                        {
                            Sleep(
                                attempt < 4
                                    ? 40
                                    : 100);

                            const HRESULT recoverHr =
                                RecreateDuplication();

                            if (SUCCEEDED(recoverHr))
                            {
                                recovered = true;
                                break;
                            }

                            m_lastError.store(
                                recoverHr);
                        }

                        if (recovered)
                            continue;
                    }

                    break;
                }

                // Desktop Duplication wakes for BOTH desktop-image updates and
                // pointer-only changes. The remote cursor already travels on a
                // dedicated latest-wins control path, so encoding pointer-only
                // wakes wastes NVENC/network work and makes capture FPS look
                // artificially high when the user moves the mouse.
                //
                // Microsoft documents LastPresentTime == 0 when only the pointer
                // changed. In that case release the acquired frame immediately
                // and keep pumping NVENC output, but do not copy/encode the same
                // desktop image again.
                const bool desktopImageUpdated =
                    frameInfo.LastPresentTime.QuadPart != 0;

                if (!desktopImageUpdated)
                {
                    m_pointerOnlyUpdates.fetch_add(1);
                    const HRESULT releasePointerOnlyHr =
                        m_duplication->ReleaseFrame();

                    if (FAILED(releasePointerOnlyHr))
                    {
                        m_lastError.store(
                            releasePointerOnlyHr);

                        if (IsRecoverableDuplicationError(
                                releasePointerOnlyHr) &&
                            SUCCEEDED(RecreateDuplication()))
                        {
                            continue;
                        }

                        break;
                    }

                    continue;
                }

                m_desktopPresents.fetch_add(1);
                m_lastAccumulatedFrames.store(frameInfo.AccumulatedFrames);
                m_dxgiAccumulatedFrames.fetch_add(frameInfo.AccumulatedFrames);
                if (frameInfo.AccumulatedFrames > 1)
                {
                    m_dxgiMissedFrames.fetch_add(
                        static_cast<std::uint64_t>(frameInfo.AccumulatedFrames - 1));
                }

                ComPtr<ID3D11Texture2D>
                    sourceTexture;

                hr =
                    desktopResource.As(
                        &sourceTexture);

                if (SUCCEEDED(hr))
                {
                    D3D11_TEXTURE2D_DESC
                        sourceDesc{};

                    sourceTexture->GetDesc(
                        &sourceDesc);

                    hr =
                        EnsureFrameTextures(
                            sourceDesc);

                    if (SUCCEEDED(hr))
                    {
                        const bool encoderActive =
                            m_encoderActive.load();

                        const auto frameId =
                            m_frameCount.fetch_add(1) + 1;

                        const auto now =
                            std::chrono::steady_clock::now();

#ifdef MULTIVERSE_NVENC
                        if (encoderActive)
                        {
                            // WGC has its own free-threaded callback and owns
                            // the publish pacer in mode 5. A DXGI frame that was
                            // already in flight during backend transition is
                            // simply released below. This avoids both D3D11
                            // context contention and a data race on FramePacer.
                            if ((m_encoderLabInputMode != CORE10_MODE_WGC_BGRA &&
                                 m_encoderLabInputMode != CORE11_MODE_WGC_NV12 &&
                                 m_encoderLabInputMode != CORE11_MODE_WGC_DIRECT_RGB &&
                                 m_encoderLabInputMode != CORE11_MODE_WGC_RGB_ENCODE_SCALE &&
                                 m_encoderLabInputMode != CORE11_MODE_WGC_NATIVE_RGB &&
                                 m_encoderLabInputMode != CORE11_MODE_WGC_SAME_DEVICE_DIRECT_RGB))
                            {
                                // Pace BEFORE the full-frame GPU copy.
                                const auto nowUs =
                                    std::chrono::duration_cast<
                                        std::chrono::microseconds>(
                                            now.time_since_epoch()).count();

                                const auto targetFps =
                                    m_capturePublishFps.load();

                                if (m_ringPublishPacer.ShouldRun(
                                        nowUs,
                                        targetFps))
                                {
                                    if (m_encoderLabInputMode == CORE10_MODE_ISOLATED_BGRA)
                                    {
                                        (void)PublishFrameToCore10Ring(
                                            sourceTexture.Get(),
                                            sourceDesc,
                                            frameId,
                                            now);
                                    }
                                    else
                                    {
                                        (void)PublishFrameToSameDeviceNvencRing(
                                            sourceTexture.Get(),
                                            sourceDesc,
                                            frameId,
                                            now);
                                    }
                                }
                                else
                                {
                                    m_ringPacingDrops.fetch_add(1);
                                }
                            }

                            m_previewRequested.store(false);
                        }
                        else
#endif
                        {
                            // Preview-only mode keeps the last desktop image.
                            m_context->CopyResource(
                                m_latestTexture.Get(),
                                sourceTexture.Get());

                            const auto previewInterval =
                                std::chrono::milliseconds(33);

                            if (m_previewRequested.load() &&
                                now -
                                    m_lastPreviewGenerated >=
                                    previewInterval)
                            {
                                const HRESULT previewHr =
                                    GeneratePreview(
                                        frameId);

                                if (SUCCEEDED(previewHr))
                                {
                                    m_lastPreviewGenerated =
                                        now;
                                    m_previewRequested.store(false);
                                }
                            }
                        }
                    }
                }

                const HRESULT releaseHr =
                    m_duplication->ReleaseFrame();

                if (FAILED(releaseHr) &&
                    SUCCEEDED(hr))
                {
                    hr = releaseHr;
                }

                if (FAILED(hr))
                {
                    m_lastError.store(hr);

                    if (IsRecoverableDuplicationError(
                            hr))
                    {
                        if (SUCCEEDED(
                                RecreateDuplication()))
                        {
                            continue;
                        }
                    }

                    break;
                }
            }
        }

    private:
        std::wstring m_deviceName;

        ComPtr<IDXGIAdapter1> m_adapter;
        ComPtr<IDXGIOutput> m_output;

        // Capture device/context are owned by Desktop Duplication only in CORE 10.
        ComPtr<ID3D11Device> m_device;
        ComPtr<ID3D11DeviceContext> m_context;
        ComPtr<ID3D11DeviceContext3> m_context3;

        // CORE 10: a second device on the same adapter owns NVENC and all
        // encoder-context work. This removes the capture/encode immediate-context
        // contention proven by the 09.3.6 B/C tests.
        ComPtr<ID3D11Device> m_encodeDevice;
        ComPtr<ID3D11DeviceContext> m_encodeContext;
        GpuPriorityResult m_captureGpuPriority{};
        GpuPriorityResult m_encodeGpuPriority{};
        ProcessGpuPriorityResult m_processGpuPriority{};
        ProcessGpuPriorityClass m_processGpuPriorityOriginal = ProcessGpuPriorityClass::Normal;
        bool m_processGpuPriorityOriginalCaptured = false;
        HRESULT m_processGpuPriorityRestoreStatus = S_OK;
        std::atomic<bool> m_gpuSchedulingBoosted{ false };
        std::uint32_t m_gpuPressureSamples = 0;
        std::uint32_t m_gpuRecoverySamples = 0;
        std::chrono::steady_clock::time_point m_gpuSchedulingLastSample{};
        std::uint64_t m_gpuSampleWgcFrames = 0;
        std::uint64_t m_gpuSampleConsumedFrames = 0;
        std::uint64_t m_gpuSampleEncodedFrames = 0;
        std::uint64_t m_gpuSampleLateTicks = 0;
        Core10SharedFrameRing m_core10Ring;
        Core10WgcCapture m_wgcCapture;

        ComPtr<IDXGIOutputDuplication>
            m_duplication;

        ComPtr<ID3D11Texture2D>
            m_latestTexture;

        ComPtr<ID3D11Texture2D>
            m_stagingTexture;

        // Core 08.8: downscale GPU antes do NVENC para equalizar hosts
        // 1440p/4K com hosts 1080p sem cópia pela CPU.
        ComPtr<ID3D11Texture2D>
            m_encoderScaleTexture;
        ComPtr<ID3D11ShaderResourceView>
            m_scaleSourceSrv;
        ComPtr<ID3D11RenderTargetView>
            m_scaleTargetRtv;
        ComPtr<ID3D11VertexShader>
            m_scaleVertexShader;
        ComPtr<ID3D11PixelShader>
            m_scalePixelShader;
        ComPtr<ID3D11SamplerState>
            m_scaleSampler;
        std::uint32_t m_scaleSourceWidth = 0;
        std::uint32_t m_scaleSourceHeight = 0;
        std::uint32_t m_scaleTargetWidth = 0;
        std::uint32_t m_scaleTargetHeight = 0;
        DXGI_FORMAT m_scaleFormat = DXGI_FORMAT_UNKNOWN;
        ID3D11Texture2D* m_scaleSourceIdentity = nullptr;

        std::atomic<bool> m_stop{ false };
        std::thread m_worker;

        std::atomic<std::uint64_t>
            m_frameCount{ 0 };

        std::atomic<bool>
            m_previewRequested{ false };

        std::atomic<long>
            m_lastError{ S_OK };

        std::atomic<std::uint64_t>
            m_duplicationResets{ 0 };

        std::atomic<std::uint64_t>
            m_encoderRestarts{ 0 };

        std::mutex m_previewMutex;

        std::vector<std::uint8_t>
            m_previewBuffer;

        std::uint64_t m_previewFrameId = 0;

        std::chrono::steady_clock::time_point
            m_lastPreviewGenerated{};

        std::uint32_t m_sourceWidth = 0;
        std::uint32_t m_sourceHeight = 0;

        std::uint32_t m_previewWidth = 0;
        std::uint32_t m_previewHeight = 0;

        std::uint32_t m_previewStride = 0;
        std::uint32_t m_requiredBufferBytes = 0;

        std::atomic<std::uint32_t> m_textureWidth{ 0 };
        std::atomic<std::uint32_t> m_textureHeight{ 0 };

        std::atomic<DXGI_FORMAT> m_textureFormat{
            DXGI_FORMAT_UNKNOWN };

        static constexpr std::size_t CaptureRingSize = 4;
        std::array<CaptureRingSlot, CaptureRingSize> m_captureRing{};
        mutable std::mutex m_captureRingMutex;
        std::condition_variable m_captureRingCv;
        std::uint32_t m_captureRingWidth = 0;
        std::uint32_t m_captureRingHeight = 0;
        DXGI_FORMAT m_captureRingFormat = DXGI_FORMAT_UNKNOWN;

        std::atomic<bool> m_encoderWorkerStop{ true };
        std::thread m_encoderWorker;
        std::atomic<bool> m_encoderWorkerAlive{ false };
        // CORE 11.1E DIAG - exact potentially-blocking stages.
        std::atomic<std::uint32_t> m_coreWgcStage{ 0 };
        std::atomic<long long> m_coreWgcStageChangedUs{ 0 };
        std::atomic<std::uint32_t> m_encoderWorkerStage{ 0 };
        std::atomic<long long> m_encoderWorkerStageChangedUs{ 0 };
        std::atomic<long long> m_lastPublishProgressUs{ 0 };
        std::atomic<long long> m_lastConsumeProgressUs{ 0 };
        std::atomic<bool> m_encoderStopInProgress{ false };
        std::atomic<std::uint32_t> m_encoderStopStage{ 0 };
        std::atomic<long long> m_encoderStopStageChangedUs{ 0 };
        std::atomic<std::uint32_t> m_nvencStopStage{ 0 };
        std::atomic<long long> m_nvencStopStageChangedUs{ 0 };

        std::atomic<std::uint64_t> m_desktopPresents{ 0 };
        std::atomic<std::uint64_t> m_pointerOnlyUpdates{ 0 };
        std::atomic<std::uint64_t> m_dxgiAccumulatedFrames{ 0 };
        std::atomic<std::uint64_t> m_dxgiMissedFrames{ 0 };
        std::atomic<std::uint32_t> m_lastAccumulatedFrames{ 0 };
        std::atomic<std::uint64_t> m_ringPublished{ 0 };
        std::atomic<std::uint64_t> m_ringReplaced{ 0 };
        std::atomic<std::uint64_t> m_ringBusyDrops{ 0 };
        std::atomic<std::uint64_t> m_ringConsumed{ 0 };
        std::atomic<std::uint64_t> m_ringPacingDrops{ 0 };
        std::atomic<std::uint32_t> m_capturePublishFps{ 0 };
        std::atomic<std::uint32_t> m_core11IngressFps{ 0 };
        std::atomic<std::uint64_t> m_remoteClockLateTicks{ 0 };

        // CORE 11.17c DIAG: real GPU queue observation for the single
        // same-device CopyResource. EVENT queries are polled DONOTFLUSH only.
        std::atomic<std::uint64_t> m_copyGpuProbeSignals{ 0 };
        std::atomic<std::uint64_t> m_copyGpuProbeCompleted{ 0 };
        std::atomic<std::uint64_t> m_copyGpuReadyAtSubmit{ 0 };
        std::atomic<std::uint64_t> m_copyGpuNotReadyAtSubmit{ 0 };
        std::atomic<std::uint32_t> m_copyGpuProbeMaxPending{ 0 };
        std::atomic<std::uint64_t> m_copyGpuLastLatencyUs{ 0 };
        std::atomic<std::uint64_t> m_copyGpuTotalLatencyUs{ 0 };
        std::atomic<std::uint64_t> m_copyGpuMaxLatencyUs{ 0 };
        std::atomic<HRESULT> m_copyGpuProbeStatus{ S_OK };

        // CORE 11.17e: encoder-worker-owned pressure observer. State transitions
        // remain diagnostic-only; TargetFps mirrors the requested cadence. The
        // mirrors are atomics so UI/native snapshots never race controller state.
        Core11AdaptivePressureController m_adaptivePressureController;
        std::atomic<std::uint32_t> m_adaptivePressureState{ 0 };
        std::atomic<std::uint32_t> m_adaptivePressureTargetFps{ 0 };
        std::atomic<std::uint32_t> m_adaptivePressureStreak{ 0 };
        std::atomic<std::uint32_t> m_adaptiveHealthyStreak{ 0 };
        std::atomic<std::uint32_t> m_adaptiveTransitions{ 0 };
        std::atomic<std::uint32_t> m_adaptiveRingOccupied{ 0 };

        FramePacer m_ringPublishPacer;
        std::atomic<long long> m_lastAcquireUs{ 0 };
        std::atomic<std::uint64_t> m_totalAcquireUs{ 0 };
        std::atomic<std::uint64_t> m_acquireSamples{ 0 };
        std::atomic<std::uint64_t> m_acquireTimeouts{ 0 };
        std::atomic<std::uint64_t> m_wgcFramesPublished{ 0 };
        std::atomic<std::uint64_t> m_wgcPacingDrops{ 0 };
        std::atomic<long> m_wgcLastStatus{ S_OK };
        std::atomic<long long> m_lastRingCopyUs{ 0 };
        std::atomic<std::uint64_t> m_totalRingCopyUs{ 0 };

        // Start/Stop serialization only. Runtime NVENC access belongs to
        // EncoderLoop and never holds this lock.
        mutable std::mutex m_encoderLifecycleMutex;
        std::mutex m_encoderMessageMutex;
        std::mutex m_encoderStatsMutex;
        MV_EncoderStats m_encoderStatsSnapshot{};

        std::atomic<bool> m_pendingForceIdr{ false };
        std::atomic<std::uint32_t> m_pendingBitrateKbps{ 0u };

        std::wstring m_encoderMessage =
            L"Encoder parado.";

        std::atomic<long>
            m_encoderStatus{ S_OK };

#ifdef MULTIVERSE_NVENC
        std::unique_ptr<AsyncNvencD3D11>
            m_encoder;
#endif

        std::atomic<bool>
            m_encoderActive{ false };

        std::shared_ptr<SharedEncodedPacketQueue>
            m_packetQueue =
                std::make_shared<SharedEncodedPacketQueue>();

        std::atomic<bool>
            m_packetConsumerActive{ false };

        std::uint32_t m_encoderSourceWidth = 0;
        std::uint32_t m_encoderSourceHeight = 0;
        std::uint32_t m_encoderWidth = 0;
        std::uint32_t m_encoderHeight = 0;
        std::uint32_t m_encoderFps = 0;
        std::uint32_t m_encoderBitrateKbps = 0;
        std::uint32_t m_encoderPreset = 0;
        bool m_encoderUltraLowLatency = true;
        std::uint32_t m_encoderMaxWidth = 0;
        std::uint32_t m_encoderMaxHeight = 0;
        std::uint32_t m_encoderLabInputMode = LAB_MODE_AUTO;

        ComPtr<ID3D11Texture2D> m_syntheticTexture;
        ComPtr<ID3D11RenderTargetView> m_syntheticRtv;
        std::uint64_t m_syntheticFrameId = 0;
        HighResolutionFrameTimer m_syntheticFrameTimer;
        std::chrono::steady_clock::time_point
            m_nextSyntheticFrameTime{};
        HighResolutionFrameTimer m_remoteFrameTimer;
        RemoteFrameDeadlineScheduler m_remoteFrameDeadlineScheduler;
        std::uint64_t m_remoteOutputFrameId = 0;

        std::chrono::steady_clock::time_point
            m_nextEncodeTime{};

        std::chrono::steady_clock::time_point
            m_nextEncoderRestartAttempt{};
    };
}

// =====================================================
// HARDWARE API
// =====================================================

std::int32_t __cdecl MV_GetApiVersion()
{
    // CORE 11.17c: API v28 appends observational CopyResource GPU EVENT telemetry.
    return 28;
}

std::int32_t __cdecl MV_GetGpuCount()
{
    ComPtr<IDXGIFactory1> factory;

    if (FAILED(CreateDXGIFactory1(
            IID_PPV_ARGS(factory.GetAddressOf()))))
    {
        return 0;
    }

    std::int32_t count = 0;

    for (UINT index = 0;; ++index)
    {
        ComPtr<IDXGIAdapter1> adapter;

        const HRESULT hr =
            factory->EnumAdapters1(
                index,
                adapter.GetAddressOf());

        if (hr == DXGI_ERROR_NOT_FOUND)
            break;

        if (FAILED(hr))
            break;

        ++count;
    }

    return count;
}

std::int32_t __cdecl MV_GetGpuInfo(
    std::int32_t index,
    MV_GpuInfo* info)
{
    if (!info || index < 0)
        return 0;

    *info = {};

    ComPtr<IDXGIFactory1> factory;

    if (FAILED(CreateDXGIFactory1(
            IID_PPV_ARGS(factory.GetAddressOf()))))
    {
        return 0;
    }

    ComPtr<IDXGIAdapter1> adapter;

    if (FAILED(factory->EnumAdapters1(
            static_cast<UINT>(index),
            adapter.GetAddressOf())))
    {
        return 0;
    }

    DXGI_ADAPTER_DESC1 desc{};

    if (FAILED(adapter->GetDesc1(&desc)))
        return 0;

    wcsncpy_s(
        info->Name,
        desc.Description,
        _TRUNCATE);

    info->DedicatedVideoMemory =
        static_cast<std::uint64_t>(
            desc.DedicatedVideoMemory);

    info->VendorId =
        desc.VendorId;

    info->DeviceId =
        desc.DeviceId;

    info->IsSoftware =
        (desc.Flags &
         DXGI_ADAPTER_FLAG_SOFTWARE)
            ? 1
            : 0;

    return 1;
}

std::int32_t __cdecl MV_GetMonitorCount()
{
    return static_cast<std::int32_t>(
        EnumerateMonitors().size());
}

std::int32_t __cdecl MV_GetMonitorInfo(
    std::int32_t index,
    MV_MonitorInfo* info)
{
    if (!info || index < 0)
        return 0;

    *info = {};

    const auto monitors =
        EnumerateMonitors();

    if (static_cast<std::size_t>(
            index) >=
        monitors.size())
    {
        return 0;
    }

    const auto& monitor =
        monitors[
            static_cast<std::size_t>(
                index)];

    wcsncpy_s(
        info->DeviceName,
        monitor.Info.szDevice,
        _TRUNCATE);

    info->Left =
        monitor.Info.rcMonitor.left;

    info->Top =
        monitor.Info.rcMonitor.top;

    info->Right =
        monitor.Info.rcMonitor.right;

    info->Bottom =
        monitor.Info.rcMonitor.bottom;

    info->Width =
        static_cast<std::uint32_t>(
            std::max<LONG>(
                0,
                monitor.Info.rcMonitor.right -
                monitor.Info.rcMonitor.left));

    info->Height =
        static_cast<std::uint32_t>(
            std::max<LONG>(
                0,
                monitor.Info.rcMonitor.bottom -
                monitor.Info.rcMonitor.top));

    info->RefreshRate =
        GetRefreshRate(
            monitor.Info.szDevice);

    info->IsPrimary =
        (monitor.Info.dwFlags &
         MONITORINFOF_PRIMARY)
            ? 1
            : 0;

    return 1;
}

std::int32_t __cdecl MV_TestD3D11Device()
{
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;

    return D3D11CreateDevice(
        nullptr,
        D3D_DRIVER_TYPE_HARDWARE,
        nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        nullptr,
        0,
        D3D11_SDK_VERSION,
        device.GetAddressOf(),
        nullptr,
        context.GetAddressOf());
}

std::int32_t __cdecl MV_TestDesktopDuplication(
    const wchar_t* monitorDeviceName)
{
    ComPtr<IDXGIAdapter1> adapter;
    ComPtr<IDXGIOutput> output;

    HRESULT hr =
        FindOutputAndAdapter(
            monitorDeviceName,
            adapter,
            output);

    if (FAILED(hr))
        return hr;

    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;

    hr =
        D3D11CreateDevice(
            adapter.Get(),
            D3D_DRIVER_TYPE_UNKNOWN,
            nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            nullptr,
            0,
            D3D11_SDK_VERSION,
            device.GetAddressOf(),
            nullptr,
            context.GetAddressOf());

    if (FAILED(hr))
        return hr;

    ComPtr<IDXGIOutput1> output1;

    hr = output.As(&output1);

    if (FAILED(hr))
        return hr;

    ComPtr<IDXGIOutputDuplication>
        duplication;

    return
        output1->DuplicateOutput(
            device.Get(),
            duplication.GetAddressOf());
}

// =====================================================
// CAPTURE API
// =====================================================

std::int32_t __cdecl MV_CaptureCreate(
    const wchar_t* monitorDeviceName,
    void** captureHandle,
    std::uint32_t* sourceWidth,
    std::uint32_t* sourceHeight)
{
    if (!captureHandle ||
        !sourceWidth ||
        !sourceHeight)
    {
        return E_INVALIDARG;
    }

    *captureHandle = nullptr;
    *sourceWidth = 0;
    *sourceHeight = 0;

    auto* session =
        new (std::nothrow)
            DesktopCaptureSession();

    if (!session)
        return E_OUTOFMEMORY;

    const HRESULT hr =
        session->Initialize(
            monitorDeviceName);

    if (FAILED(hr))
    {
        delete session;
        return hr;
    }

    *sourceWidth =
        session->SourceWidth();

    *sourceHeight =
        session->SourceHeight();

    *captureHandle =
        session;

    return S_OK;
}

void __cdecl MV_CaptureDestroy(
    void* captureHandle)
{
    TraceNativeTeardown(L"MV_CAPTURE_DESTROY_ENTER", captureHandle);
    delete
        static_cast<DesktopCaptureSession*>(
            captureHandle);
    TraceNativeTeardown(L"MV_CAPTURE_DESTROY_RETURN", captureHandle);
}

std::int32_t __cdecl MV_CaptureGetPreviewSize(
    void* captureHandle,
    std::uint32_t* previewWidth,
    std::uint32_t* previewHeight,
    std::uint32_t* previewStride,
    std::uint32_t* requiredBufferBytes)
{
    if (!captureHandle ||
        !previewWidth ||
        !previewHeight ||
        !previewStride ||
        !requiredBufferBytes)
    {
        return E_INVALIDARG;
    }

    auto* session =
        static_cast<DesktopCaptureSession*>(
            captureHandle);

    *previewWidth =
        session->PreviewWidth();

    *previewHeight =
        session->PreviewHeight();

    *previewStride =
        session->PreviewStride();

    *requiredBufferBytes =
        session->RequiredBufferBytes();

    return S_OK;
}

std::int32_t __cdecl MV_CaptureGetFrameCount(
    void* captureHandle,
    std::uint64_t* frameCount)
{
    if (!captureHandle ||
        !frameCount)
    {
        return E_INVALIDARG;
    }

    *frameCount =
        static_cast<DesktopCaptureSession*>(
            captureHandle)
            ->FrameCount();

    return S_OK;
}

std::int32_t __cdecl MV_CaptureGetPipelineStats(
    void* captureHandle,
    MV_CapturePipelineStats* stats)
{
    if (!captureHandle || !stats)
        return E_INVALIDARG;

    return static_cast<DesktopCaptureSession*>(captureHandle)
        ->GetCapturePipelineStats(stats);
}


std::int32_t __cdecl MV_CaptureGetLastError(
    void* captureHandle)
{
    if (!captureHandle)
        return E_INVALIDARG;

    return
        static_cast<DesktopCaptureSession*>(
            captureHandle)
            ->LastError();
}

std::int32_t __cdecl MV_CaptureCopyLatestPreview(
    void* captureHandle,
    std::uint8_t* destination,
    std::uint32_t destinationBytes,
    std::uint32_t* previewWidth,
    std::uint32_t* previewHeight,
    std::uint32_t* previewStride,
    std::uint64_t* frameId)
{
    if (!captureHandle)
        return E_INVALIDARG;

    return
        static_cast<DesktopCaptureSession*>(
            captureHandle)
            ->CopyLatestPreview(
                destination,
                destinationBytes,
                previewWidth,
                previewHeight,
                previewStride,
                frameId);
}

// =====================================================
// NVENC API
// =====================================================

std::int32_t __cdecl MV_NvencIsCompiled()
{
#ifdef MULTIVERSE_NVENC
    return 1;
#else
    return 0;
#endif
}

std::int32_t __cdecl
MV_NvencRuntimeLibraryAvailable()
{
    HMODULE module =
        LoadLibraryW(
            L"nvEncodeAPI64.dll");

    if (!module)
        return 0;

    FreeLibrary(module);
    return 1;
}

std::int32_t __cdecl MV_EncoderStart(
    void* captureHandle,
    const MV_EncoderConfig* config)
{
    if (!captureHandle ||
        !config)
    {
        return E_INVALIDARG;
    }

    return
        static_cast<DesktopCaptureSession*>(
            captureHandle)
            ->StartEncoder(
                *config);
}

void __cdecl MV_EncoderBlockSubmissions(void* captureHandle)
{
    if (captureHandle)
        static_cast<DesktopCaptureSession*>(captureHandle)->BlockEncoderSubmissions();
}

void __cdecl MV_EncoderStop(
    void* captureHandle)
{
    if (captureHandle)
    {
        static_cast<DesktopCaptureSession*>(
            captureHandle)
            ->StopEncoder();
    }
}

std::int32_t __cdecl MV_EncoderRequestIdr(
    void* captureHandle)
{
    if (!captureHandle)
        return E_INVALIDARG;

    return
        static_cast<DesktopCaptureSession*>(
            captureHandle)
            ->RequestIdr();
}

std::int32_t __cdecl MV_EncoderReconfigureBitrate(
    void* captureHandle,
    std::uint32_t bitrateKbps)
{
    if (!captureHandle)
        return E_INVALIDARG;

    return
        static_cast<DesktopCaptureSession*>(
            captureHandle)
            ->ReconfigureEncoderBitrate(
                bitrateKbps);
}

std::int32_t __cdecl MV_EncoderSetPacketConsumerActive(
    void* captureHandle,
    std::int32_t active)
{
    if (!captureHandle)
        return E_INVALIDARG;

    return
        static_cast<DesktopCaptureSession*>(
            captureHandle)
            ->SetPacketConsumerActive(
                active != 0);
}

std::int32_t __cdecl MV_EncoderGetStats(
    void* captureHandle,
    MV_EncoderStats* stats)
{
    if (!captureHandle)
        return E_INVALIDARG;

    return
        static_cast<DesktopCaptureSession*>(
            captureHandle)
            ->GetEncoderStats(stats);
}

std::int32_t __cdecl MV_EncoderGetLastMessage(
    void* captureHandle,
    wchar_t* destination,
    std::uint32_t destinationChars)
{
    if (!captureHandle)
        return E_INVALIDARG;

    return
        static_cast<DesktopCaptureSession*>(
            captureHandle)
            ->GetEncoderMessage(
                destination,
                destinationChars);
}

std::int32_t __cdecl MV_EncoderPeekPacket(
    void* captureHandle,
    std::uint32_t* requiredBytes,
    std::uint64_t* frameId,
    std::int32_t* keyFrame)
{
    if (!captureHandle)
        return E_INVALIDARG;

    return
        static_cast<DesktopCaptureSession*>(
            captureHandle)
            ->PeekPacket(
                requiredBytes,
                frameId,
                keyFrame);
}

std::int32_t __cdecl MV_EncoderPopPacket(
    void* captureHandle,
    std::uint8_t* destination,
    std::uint32_t destinationBytes,
    std::uint32_t* writtenBytes,
    std::uint64_t* frameId,
    std::int32_t* keyFrame)
{
    if (!captureHandle)
        return E_INVALIDARG;

    return
        static_cast<DesktopCaptureSession*>(
            captureHandle)
            ->PopPacket(
                destination,
                destinationBytes,
                writtenBytes,
                frameId,
                keyFrame);
}
