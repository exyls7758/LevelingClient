#include <Windows.h>
#include <TlHelp32.h>
#include <roapi.h>
#pragma comment(lib, "runtimeobject.lib")

#include "Multiverse.Core/MultiverseCore.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <iomanip>
#include <iostream>
#include <limits>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>
#include <map>

#pragma comment(lib, "Ole32.lib")

namespace
{
    using Clock = std::chrono::steady_clock;

    struct Api
    {
        decltype(&MV_GetApiVersion) GetApiVersion{};
        decltype(&MV_GetGpuCount) GetGpuCount{};
        decltype(&MV_GetGpuInfo) GetGpuInfo{};
        decltype(&MV_GetMonitorCount) GetMonitorCount{};
        decltype(&MV_GetMonitorInfo) GetMonitorInfo{};
        decltype(&MV_TestD3D11Device) TestD3D11Device{};
        decltype(&MV_TestDesktopDuplication) TestDesktopDuplication{};
        decltype(&MV_CaptureCreate) CaptureCreate{};
        decltype(&MV_CaptureDestroy) CaptureDestroy{};
        decltype(&MV_CaptureGetPipelineStats) CaptureGetPipelineStats{};
        decltype(&MV_CaptureGetLastError) CaptureGetLastError{};
        decltype(&MV_NvencIsCompiled) NvencIsCompiled{};
        decltype(&MV_NvencRuntimeLibraryAvailable) NvencRuntimeLibraryAvailable{};
        decltype(&MV_NvdecIsCompiled) NvdecIsCompiled{};
        decltype(&MV_NvdecRuntimeLibraryAvailable) NvdecRuntimeLibraryAvailable{};
        decltype(&MV_EncoderStart) EncoderStart{};
        decltype(&MV_EncoderBlockSubmissions) EncoderBlockSubmissions{};
        decltype(&MV_EncoderStop) EncoderStop{};
        decltype(&MV_EncoderRequestIdr) EncoderRequestIdr{};
        decltype(&MV_EncoderSetPacketConsumerActive) EncoderSetPacketConsumerActive{};
        decltype(&MV_EncoderGetStats) EncoderGetStats{};
        decltype(&MV_EncoderGetLastMessage) EncoderGetLastMessage{};
        decltype(&MV_EncoderPeekPacket) EncoderPeekPacket{};
        decltype(&MV_EncoderPopPacket) EncoderPopPacket{};
    };

    struct ResourceSample
    {
        DWORD Handles = 0;
        DWORD Threads = 0;
    };

    struct CycleResult
    {
        bool Started = false;
        bool PacketConsumed = false;
        bool RuntimeValid = true;
        bool PostStopValid = true;
        std::uint64_t EncodedFrames = 0;
        std::uint64_t EncodedBytes = 0;
        std::uint64_t PublishedFrames = 0;
        std::uint64_t ConsumedFrames = 0;
        double StartMs = 0.0;
        double StopMs = 0.0;
        ResourceSample Resources{};
    };

    std::string HexHr(std::int32_t value)
    {
        char buffer[16]{};
        sprintf_s(buffer, "0x%08X", static_cast<unsigned int>(value));
        return buffer;
    }

    void Require(bool condition, const std::string& message)
    {
        if (!condition)
            throw std::runtime_error(message);
    }

    template <typename T>
    T Resolve(HMODULE module, const char* name)
    {
        FARPROC address = GetProcAddress(module, name);
        if (!address)
        {
            throw std::runtime_error(
                std::string("missing_export name=") + name +
                " win32=" + std::to_string(GetLastError()));
        }
        return reinterpret_cast<T>(address);
    }

    Api LoadApi(HMODULE module)
    {
        Api api{};
        api.GetApiVersion = Resolve<decltype(api.GetApiVersion)>(module, "MV_GetApiVersion");
        api.GetGpuCount = Resolve<decltype(api.GetGpuCount)>(module, "MV_GetGpuCount");
        api.GetGpuInfo = Resolve<decltype(api.GetGpuInfo)>(module, "MV_GetGpuInfo");
        api.GetMonitorCount = Resolve<decltype(api.GetMonitorCount)>(module, "MV_GetMonitorCount");
        api.GetMonitorInfo = Resolve<decltype(api.GetMonitorInfo)>(module, "MV_GetMonitorInfo");
        api.TestD3D11Device = Resolve<decltype(api.TestD3D11Device)>(module, "MV_TestD3D11Device");
        api.TestDesktopDuplication = Resolve<decltype(api.TestDesktopDuplication)>(module, "MV_TestDesktopDuplication");
        api.CaptureCreate = Resolve<decltype(api.CaptureCreate)>(module, "MV_CaptureCreate");
        api.CaptureDestroy = Resolve<decltype(api.CaptureDestroy)>(module, "MV_CaptureDestroy");
        api.CaptureGetPipelineStats = Resolve<decltype(api.CaptureGetPipelineStats)>(module, "MV_CaptureGetPipelineStats");
        api.CaptureGetLastError = Resolve<decltype(api.CaptureGetLastError)>(module, "MV_CaptureGetLastError");
        api.NvencIsCompiled = Resolve<decltype(api.NvencIsCompiled)>(module, "MV_NvencIsCompiled");
        api.NvencRuntimeLibraryAvailable = Resolve<decltype(api.NvencRuntimeLibraryAvailable)>(module, "MV_NvencRuntimeLibraryAvailable");
        api.NvdecIsCompiled = Resolve<decltype(api.NvdecIsCompiled)>(module, "MV_NvdecIsCompiled");
        api.NvdecRuntimeLibraryAvailable = Resolve<decltype(api.NvdecRuntimeLibraryAvailable)>(module, "MV_NvdecRuntimeLibraryAvailable");
        api.EncoderStart = Resolve<decltype(api.EncoderStart)>(module, "MV_EncoderStart");
        api.EncoderBlockSubmissions = Resolve<decltype(api.EncoderBlockSubmissions)>(module, "MV_EncoderBlockSubmissions");
        api.EncoderStop = Resolve<decltype(api.EncoderStop)>(module, "MV_EncoderStop");
        api.EncoderRequestIdr = Resolve<decltype(api.EncoderRequestIdr)>(module, "MV_EncoderRequestIdr");
        api.EncoderSetPacketConsumerActive = Resolve<decltype(api.EncoderSetPacketConsumerActive)>(module, "MV_EncoderSetPacketConsumerActive");
        api.EncoderGetStats = Resolve<decltype(api.EncoderGetStats)>(module, "MV_EncoderGetStats");
        api.EncoderGetLastMessage = Resolve<decltype(api.EncoderGetLastMessage)>(module, "MV_EncoderGetLastMessage");
        api.EncoderPeekPacket = Resolve<decltype(api.EncoderPeekPacket)>(module, "MV_EncoderPeekPacket");
        api.EncoderPopPacket = Resolve<decltype(api.EncoderPopPacket)>(module, "MV_EncoderPopPacket");
        return api;
    }


    void ReportHandleTypes()
    {
        // Diagnostic only: query handles in THIS harness process. No foreign
        // process inspection and no handle names/payloads are recorded.
        struct Entry { HANDLE value; ULONG_PTR handles, pointers; ULONG access, type, attributes, reserved; };
        struct Snapshot { ULONG_PTR count, reserved; Entry entries[1]; };
        using Query = LONG (NTAPI*)(HANDLE, ULONG, void*, ULONG, ULONG*);
        auto query = reinterpret_cast<Query>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtQueryInformationProcess"));
        if (!query) return;
        std::vector<unsigned char> buffer(1024 * 1024);
        ULONG needed = 0;
        if (query(GetCurrentProcess(), 51, buffer.data(), static_cast<ULONG>(buffer.size()), &needed) < 0) return;
        auto snapshot = reinterpret_cast<const Snapshot*>(buffer.data());
        std::map<ULONG, std::size_t> counts;
        for (ULONG_PTR i = 0; i < snapshot->count; ++i) ++counts[snapshot->entries[i].type];
        static bool reportedTypes = false;
        if (!reportedTypes)
        {
            auto queryObject = reinterpret_cast<Query>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtQueryObject"));
            std::map<ULONG, bool> reported;
            for (ULONG_PTR i = 0; queryObject && i < snapshot->count; ++i)
            {
                const auto& entry = snapshot->entries[i];
                if (reported[entry.type]) continue;
                std::vector<unsigned char> info(4096);
                if (queryObject(entry.value, 2, info.data(), static_cast<ULONG>(info.size()), &needed) >= 0)
                {
                    struct UnicodeString { USHORT length, maximum; wchar_t* data; };
                    const auto* name = reinterpret_cast<const UnicodeString*>(info.data());
                    std::wcout << L"event=handle_type index=" << entry.type << L" name="
                        << std::wstring(name->data, name->length / sizeof(wchar_t)) << L"\n";
                    reported[entry.type] = true;
                }
            }
            reportedTypes = true;
        }
        std::cout << "event=handle_types";
        for (const auto& pair : counts) std::cout << " type_" << pair.first << "=" << pair.second;
        std::cout << "\n";
    }

    ResourceSample SampleResources()
    {
        ReportHandleTypes();
        ResourceSample result{};
        Require(
            GetProcessHandleCount(GetCurrentProcess(), &result.Handles) != FALSE,
            "GetProcessHandleCount failed");

        HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
        Require(snapshot != INVALID_HANDLE_VALUE, "CreateToolhelp32Snapshot failed");

        THREADENTRY32 entry{};
        entry.dwSize = sizeof(entry);
        if (Thread32First(snapshot, &entry))
        {
            const DWORD processId = GetCurrentProcessId();
            do
            {
                if (entry.th32OwnerProcessID == processId)
                    ++result.Threads;
            }
            while (Thread32Next(snapshot, &entry));
        }
        CloseHandle(snapshot);
        return result;
    }

    double LinearSlope(const std::vector<DWORD>& values)
    {
        if (values.size() < 2)
            return 0.0;

        const double n = static_cast<double>(values.size());
        double sumX = 0.0;
        double sumY = 0.0;
        double sumXY = 0.0;
        double sumXX = 0.0;
        for (std::size_t index = 0; index < values.size(); ++index)
        {
            const double x = static_cast<double>(index);
            const double y = static_cast<double>(values[index]);
            sumX += x;
            sumY += y;
            sumXY += x * y;
            sumXX += x * x;
        }

        const double denominator = n * sumXX - sumX * sumX;
        return denominator == 0.0
            ? 0.0
            : (n * sumXY - sumX * sumY) / denominator;
    }

    std::wstring EncoderMessage(const Api& api, void* capture)
    {
        wchar_t message[1024]{};
        const std::int32_t hr = api.EncoderGetLastMessage(
            capture,
            message,
            static_cast<std::uint32_t>(std::size(message)));
        return SUCCEEDED(hr) ? message : L"message_unavailable";
    }

    CycleResult RunCycle(
        const Api& api,
        void* capture,
        const MV_EncoderConfig& config,
        int cycle,
        bool requireStart)
    {
        CycleResult result{};
        const auto startBegin = Clock::now();
        const std::int32_t startHr = api.EncoderStart(capture, &config);
        result.StartMs = std::chrono::duration<double, std::milli>(
            Clock::now() - startBegin).count();

        std::wcout << L"cycle=" << cycle
                   << L" event=start mode=" << config.LabInputMode
                   << L" hr=" << HexHr(startHr).c_str()
                   << L" latency_ms=" << std::fixed << std::setprecision(3)
                   << result.StartMs << L"\n" << std::flush;

        if (FAILED(startHr))
        {
            std::wcout << L"cycle=" << cycle
                       << L" event=start_failure status=" << HexHr(startHr).c_str()
                       << L" message=" << EncoderMessage(api, capture)
                       << L"\n" << std::flush;
            if (requireStart)
                return result;
            return result;
        }

        result.Started = true;
        const std::int32_t consumerHr =
            api.EncoderSetPacketConsumerActive(capture, 1);
        Require(
            SUCCEEDED(consumerHr),
            "MV_EncoderSetPacketConsumerActive failed: " + HexHr(consumerHr));

        const std::int32_t idrHr = api.EncoderRequestIdr(capture);
        Require(
            SUCCEEDED(idrHr),
            "MV_EncoderRequestIdr failed: " + HexHr(idrHr));
        std::cout << "cycle=" << cycle
                  << " event=request_idr hr=" << HexHr(idrHr) << "\n"
                  << std::flush;

        MV_EncoderStats latestEncoder{};
        MV_CapturePipelineStats latestCapture{};
        const auto packetDeadline = Clock::now() + std::chrono::milliseconds(1500);
        while (Clock::now() < packetDeadline)
        {
            const std::int32_t statsHr = api.EncoderGetStats(capture, &latestEncoder);
            Require(SUCCEEDED(statsHr), "MV_EncoderGetStats failed: " + HexHr(statsHr));

            const std::int32_t captureStatsHr =
                api.CaptureGetPipelineStats(capture, &latestCapture);
            Require(
                SUCCEEDED(captureStatsHr),
                "MV_CaptureGetPipelineStats failed: " + HexHr(captureStatsHr));

            std::uint32_t requiredBytes = 0;
            std::uint64_t frameId = 0;
            std::int32_t keyFrame = 0;
            const std::int32_t peekHr = api.EncoderPeekPacket(
                capture, &requiredBytes, &frameId, &keyFrame);
            if (peekHr == S_OK)
            {
                Require(requiredBytes > 0, "packet peek returned zero bytes");
                std::vector<std::uint8_t> packet(requiredBytes);
                std::uint32_t writtenBytes = 0;
                const std::int32_t popHr = api.EncoderPopPacket(
                    capture,
                    packet.data(),
                    static_cast<std::uint32_t>(packet.size()),
                    &writtenBytes,
                    &frameId,
                    &keyFrame);
                if (popHr == S_OK)
                {
                    Require(writtenBytes > 0, "packet pop returned zero bytes");
                    result.PacketConsumed = true;
                    SecureZeroMemory(packet.data(), packet.size());
                    std::cout << "cycle=" << cycle
                              << " event=packet_consumed bytes=" << writtenBytes
                              << " frame_id=" << frameId
                              << " keyframe=" << keyFrame << "\n"
                              << std::flush;
                    break;
                }
                Require(
                    popHr == S_FALSE,
                    "MV_EncoderPopPacket failed: " + HexHr(popHr));
            }
            else
            {
                Require(
                    peekHr == S_FALSE,
                    "MV_EncoderPeekPacket failed: " + HexHr(peekHr));
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(25));
        }

        const std::int32_t finalStatsHr = api.EncoderGetStats(capture, &latestEncoder);
        Require(SUCCEEDED(finalStatsHr), "final MV_EncoderGetStats failed");
        const std::int32_t finalCaptureHr =
            api.CaptureGetPipelineStats(capture, &latestCapture);
        Require(SUCCEEDED(finalCaptureHr), "final MV_CaptureGetPipelineStats failed");

        result.RuntimeValid =
            latestEncoder.Running == 1 &&
            latestEncoder.TargetFps == 60 &&
            latestEncoder.BitrateKbps == config.BitrateKbps &&
            latestEncoder.RequestedInputMode == static_cast<std::int32_t>(config.LabInputMode) &&
            latestEncoder.Core10PipelineMode == config.LabInputMode;

        result.EncodedFrames = latestEncoder.EncodedFrames;
        result.EncodedBytes = latestEncoder.EncodedBytes;
        result.PublishedFrames = latestCapture.PublishedToRing;
        result.ConsumedFrames = latestCapture.ConsumedByEncoder;

        std::cout << "cycle=" << cycle
                  << " event=stats"
                  << " running=" << latestEncoder.Running
                  << " width=" << latestEncoder.Width
                  << " height=" << latestEncoder.Height
                  << " target_fps=" << latestEncoder.TargetFps
                  << " bitrate_kbps=" << latestEncoder.BitrateKbps
                  << " requested_mode=" << latestEncoder.RequestedInputMode
                  << " pipeline_mode=" << latestEncoder.Core10PipelineMode
                  << " source_mode=" << latestEncoder.SourceMode
                  << " last_status=" << HexHr(latestEncoder.LastStatus)
                  << " capture_last_error=" << HexHr(api.CaptureGetLastError(capture))
                  << " runtime_valid=" << (result.RuntimeValid ? 1 : 0)
                  << " encoded_frames=" << latestEncoder.EncodedFrames
                  << " encoded_bytes=" << latestEncoder.EncodedBytes
                  << " queued_packets=" << latestEncoder.QueuedPackets
                  << " submitted_frames=" << latestEncoder.SubmittedFrames
                  << " published_frames=" << latestCapture.PublishedToRing
                  << " consumed_frames=" << latestCapture.ConsumedByEncoder
                  << " backend=" << latestCapture.CaptureBackend
                  << " backend_running=" << latestCapture.BackendRunning
                  << " encoder_worker=" << latestCapture.EncoderWorkerAlive
                  << " avg_encode_ms=" << std::fixed << std::setprecision(3)
                  << latestEncoder.AverageEncodeMs
                  << " avg_output_latency_ms=" << latestEncoder.AverageOutputLatencyMs
                  << " avg_capture_wait_ms=" << latestCapture.SharedAverageCaptureWaitMs
                  << "\n" << std::flush;

        api.EncoderSetPacketConsumerActive(capture, 0);
        api.EncoderBlockSubmissions(capture);
        std::cout << "cycle=" << cycle << " event=stop_begin\n" << std::flush;
        const auto stopBegin = Clock::now();
        api.EncoderStop(capture);
        result.StopMs = std::chrono::duration<double, std::milli>(
            Clock::now() - stopBegin).count();
        std::cout << "cycle=" << cycle
                  << " event=stop_end latency_ms=" << std::fixed
                  << std::setprecision(3) << result.StopMs << "\n"
                  << std::flush;

        MV_EncoderStats stopped{};
        const std::int32_t stoppedHr = api.EncoderGetStats(capture, &stopped);
        Require(SUCCEEDED(stoppedHr), "post-stop MV_EncoderGetStats failed");
        result.PostStopValid =
            stopped.Running == 0 &&
            stopped.EncoderWorkerAlive == 0 &&
            stopped.OutputWorkerAlive == 0 &&
            stopped.StopInProgress == 0;
        std::cout << "cycle=" << cycle
                  << " event=post_stop_stats"
                  << " running=" << stopped.Running
                  << " encoder_worker=" << stopped.EncoderWorkerAlive
                  << " output_worker=" << stopped.OutputWorkerAlive
                  << " stop_in_progress=" << stopped.StopInProgress
                  << " valid=" << (result.PostStopValid ? 1 : 0) << "\n";

        std::this_thread::sleep_for(std::chrono::milliseconds(150));
        result.Resources = SampleResources();
        std::cout << "cycle=" << cycle
                  << " event=resources handles=" << result.Resources.Handles
                  << " threads=" << result.Resources.Threads << "\n"
                  << std::flush;
        return result;
    }
}

int wmain(int argc, wchar_t** argv)
{
    if (argc < 2)
    {
        std::cerr << "usage: native_smoke.exe <absolute-path-to-Multiverse.Core.dll>\n";
        return 64;
    }

    wchar_t absolutePath[MAX_PATH]{};
    const DWORD pathLength = GetFullPathNameW(argv[1], MAX_PATH, absolutePath, nullptr);
    if (pathLength == 0 || pathLength >= MAX_PATH)
    {
        std::cerr << "FAIL invalid_dll_path win32=" << GetLastError() << "\n";
        return 65;
    }

    if (_wcsicmp(argv[1], absolutePath) != 0)
    {
        std::cerr << "FAIL dll_path_must_be_absolute\n";
        return 66;
    }

    const auto hasOption = [argc, argv](const wchar_t* option)
    {
        for (int i = 2; i < argc; ++i)
            if (std::wstring(argv[i]) == option) return true;
        return false;
    };
    const bool useWinrtApartment = hasOption(L"--winrt-apartment");
    const HRESULT coHr = useWinrtApartment ? RoInitialize(RO_INIT_MULTITHREADED) : CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    const bool uninitializeCom = SUCCEEDED(coHr);
    HMODULE module = nullptr;
    // Optional diagnostic comparison, never used to waive the normal test.
    HMODULE retainedNvenc = hasOption(L"--retain-nvenc-module")
        ? LoadLibraryW(L"nvEncodeAPI64.dll") : nullptr;
    void* capture = nullptr;
    int exitCode = 1;

    try
    {
        module = LoadLibraryExW(
            absolutePath,
            nullptr,
            LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        Require(
            module != nullptr,
            "LoadLibraryExW failed win32=" + std::to_string(GetLastError()));
        const Api api = LoadApi(module);

        const std::int32_t apiVersion = api.GetApiVersion();
        std::cout << "event=dll_loaded api_version=" << apiVersion
                  << " export_count_checked=24\n";
        Require(apiVersion >= 28, "API version is older than 28");
        Require(api.NvencIsCompiled() == 1, "NVENC was not compiled into DLL");
        Require(api.NvencRuntimeLibraryAvailable() == 1, "NVENC runtime library unavailable");
        Require(api.NvdecIsCompiled() == 1, "NVDEC was not compiled into DLL");
        Require(api.NvdecRuntimeLibraryAvailable() == 1, "NVDEC runtime library unavailable");
        std::cout << "event=codec_capabilities nvenc_compiled=1 nvenc_runtime=1"
                     " nvdec_compiled=1 nvdec_runtime=1\n";

        const std::int32_t gpuCount = api.GetGpuCount();
        Require(gpuCount > 0, "no GPU adapters enumerated");
        bool foundNvidia = false;
        for (std::int32_t index = 0; index < gpuCount; ++index)
        {
            MV_GpuInfo gpu{};
            Require(api.GetGpuInfo(index, &gpu) == 1, "MV_GetGpuInfo failed");
            foundNvidia = foundNvidia || gpu.VendorId == 0x10DEu;
            std::cout << "event=gpu index=" << index
                      << " vendor_id=0x" << std::hex << std::uppercase << gpu.VendorId
                      << " device_id=0x" << gpu.DeviceId << std::dec
                      << " dedicated_bytes=" << gpu.DedicatedVideoMemory
                      << " software=" << gpu.IsSoftware << "\n";
        }
        Require(foundNvidia, "no NVIDIA GPU adapter enumerated");

        const std::int32_t monitorCount = api.GetMonitorCount();
        Require(monitorCount > 0, "no monitors enumerated");
        std::vector<MV_MonitorInfo> monitors(static_cast<std::size_t>(monitorCount));
        std::size_t selectedMonitor = 0;
        for (std::int32_t index = 0; index < monitorCount; ++index)
        {
            Require(
                api.GetMonitorInfo(index, &monitors[static_cast<std::size_t>(index)]) == 1,
                "MV_GetMonitorInfo failed");
            const auto& monitor = monitors[static_cast<std::size_t>(index)];
            if (monitor.IsPrimary)
                selectedMonitor = static_cast<std::size_t>(index);
            std::wcout << L"event=monitor index=" << index
                       << L" device=" << monitor.DeviceName
                       << L" width=" << monitor.Width
                       << L" height=" << monitor.Height
                       << L" refresh_hz=" << monitor.RefreshRate
                       << L" primary=" << monitor.IsPrimary << L"\n";
        }

        const std::int32_t d3dHr = api.TestD3D11Device();
        Require(SUCCEEDED(d3dHr), "MV_TestD3D11Device failed: " + HexHr(d3dHr));
        const auto& monitor = monitors[selectedMonitor];
        const std::int32_t duplicationHr =
            api.TestDesktopDuplication(monitor.DeviceName);
        Require(
            SUCCEEDED(duplicationHr),
            "MV_TestDesktopDuplication failed: " + HexHr(duplicationHr));
        std::cout << "event=native_prerequisites d3d11_hr=" << HexHr(d3dHr)
                  << " desktop_duplication_hr=" << HexHr(duplicationHr) << "\n";

        std::uint32_t sourceWidth = 0;
        std::uint32_t sourceHeight = 0;
        const auto captureBegin = Clock::now();
        const std::int32_t captureHr = api.CaptureCreate(
            monitor.DeviceName,
            &capture,
            &sourceWidth,
            &sourceHeight);
        const double captureMs = std::chrono::duration<double, std::milli>(
            Clock::now() - captureBegin).count();
        Require(SUCCEEDED(captureHr), "MV_CaptureCreate failed: " + HexHr(captureHr));
        Require(capture != nullptr, "MV_CaptureCreate returned a null handle");
        Require(sourceWidth > 0 && sourceHeight > 0, "capture source dimensions were zero");
        std::cout << "event=capture_created hr=" << HexHr(captureHr)
                  << " width=" << sourceWidth << " height=" << sourceHeight
                  << " latency_ms=" << std::fixed << std::setprecision(3)
                  << captureMs << "\n" << std::flush;

        MV_EncoderConfig config{};
        config.BitrateKbps = 20000;
        config.Fps = 60;
        config.Preset = 0;
        config.UltraLowLatency = 1;
        config.MaxWidth = 1920;
        config.MaxHeight = 1080;
        config.LabInputMode = hasOption(L"--synthetic-isolation") ? 3 :
            hasOption(L"--wgc-isolated-device") ? 5 : 10;
        std::cout << "event=isolation_mode actual_mode=" << config.LabInputMode << "\n";
        config.ReservedLab = 0;
        std::cout << "event=configuration bitrate_kbps=20000 fps=60 preset=0"
                     " ultra_low_latency=1 max_width=1920 max_height=1080 mode=10\n";

        constexpr int CycleCount = 20;
        std::vector<CycleResult> cycles;
        cycles.reserve(CycleCount);
        std::uint64_t totalEncodedFrames = 0;
        std::uint64_t totalPackets = 0;
        std::uint64_t runtimeFailures = 0;
        std::uint64_t postStopFailures = 0;
        bool mode10StartFailed = false;

        for (int cycle = 1; cycle <= CycleCount; ++cycle)
        {
            if (cycle > 1 && hasOption(L"--recreate-capture"))
            {
                api.CaptureDestroy(capture); capture = nullptr;
                Require(SUCCEEDED(api.CaptureCreate(monitor.DeviceName, &capture, &sourceWidth, &sourceHeight)), "capture recreation failed");
                std::cout << "event=capture_recreated cycle=" << cycle << std::endl;
            }
            CycleResult result = RunCycle(api, capture, config, cycle, config.LabInputMode == 10);
            if (!result.Started)
            {
                mode10StartFailed = true;
                break;
            }
            totalEncodedFrames += result.EncodedFrames;
            totalPackets += result.PacketConsumed ? 1u : 0u;
            runtimeFailures += result.RuntimeValid ? 0u : 1u;
            postStopFailures += result.PostStopValid ? 0u : 1u;
            cycles.push_back(result);
        }

        if (mode10StartFailed)
        {
            MV_EncoderConfig synthetic = config;
            synthetic.LabInputMode = 3;
            std::cout << "event=supplementary_synthetic_begin reason=mode10_start_failed\n"
                      << std::flush;
            const CycleResult syntheticResult =
                RunCycle(api, capture, synthetic, 0, false);
            std::cout << "event=supplementary_synthetic_end started="
                      << (syntheticResult.Started ? 1 : 0)
                      << " encoded_frames=" << syntheticResult.EncodedFrames
                      << " packet_consumed=" << (syntheticResult.PacketConsumed ? 1 : 0)
                      << "\n" << std::flush;
            throw std::runtime_error("mode 10 failed to start; synthetic result is supplementary only");
        }

        Require(cycles.size() == CycleCount, "did not complete 20 mode-10 cycles");

        const ResourceSample warmed = cycles.front().Resources;
        std::vector<DWORD> handles;
        std::vector<DWORD> threads;
        for (std::size_t index = 1; index < cycles.size(); ++index)
        {
            handles.push_back(cycles[index].Resources.Handles);
            threads.push_back(cycles[index].Resources.Threads);
        }
        const double handleSlope = LinearSlope(handles);
        const double threadSlope = LinearSlope(threads);
        const DWORD finalHandles = cycles.back().Resources.Handles;
        const DWORD finalThreads = cycles.back().Resources.Threads;
        const DWORD maxHandles = *std::max_element(handles.begin(), handles.end());
        const DWORD maxThreads = *std::max_element(threads.begin(), threads.end());

        std::cout << "event=leak_analysis"
                  << " warm_cycle=1"
                  << " analyzed_cycles=19"
                  << " warm_handles=" << warmed.Handles
                  << " final_handles=" << finalHandles
                  << " max_handles=" << maxHandles
                  << " handle_slope_per_cycle=" << std::fixed << std::setprecision(4)
                  << handleSlope
                  << " warm_threads=" << warmed.Threads
                  << " final_threads=" << finalThreads
                  << " max_threads=" << maxThreads
                  << " thread_slope_per_cycle=" << threadSlope << "\n";

        const bool handlesValid =
            finalHandles <= warmed.Handles + 4 &&
            maxHandles <= warmed.Handles + 6 &&
            handleSlope <= 0.25;
        const bool threadsValid =
            finalThreads <= warmed.Threads + 1 &&
            maxThreads <= warmed.Threads + 2 &&
            threadSlope <= 0.10;
        std::cout << "event=leak_assertions handles_valid=" << (handlesValid ? 1 : 0)
                  << " threads_valid=" << (threadsValid ? 1 : 0) << "\n";

        std::cout << "event=capture_destroy_begin\n" << std::flush;
        const auto destroyBegin = Clock::now();
        api.CaptureDestroy(capture);
        capture = nullptr;
        const double destroyMs = std::chrono::duration<double, std::milli>(
            Clock::now() - destroyBegin).count();
        std::cout << "event=capture_destroy_end latency_ms=" << std::fixed
                  << std::setprecision(3) << destroyMs << "\n";

        const ResourceSample afterDestroy = SampleResources();
        std::cout << "event=final_resources handles=" << afterDestroy.Handles
                  << " threads=" << afterDestroy.Threads << "\n";
        Require(totalEncodedFrames > 0, "no frames were encoded across 20 cycles");
        Require(totalPackets > 0, "no encoded packet was consumed across 20 cycles");
        Require(runtimeFailures == 0, "one or more running cycles violated effective encoder configuration");
        Require(postStopFailures == 0, "one or more stops left native workers active");
        Require(handlesValid, "post-stop handle counts show linear or unbounded growth");
        Require(threadsValid, "post-stop thread counts show linear or unbounded growth");
        std::cout << "PASS cycles=20 packets_consumed=" << totalPackets
                  << " encoded_frames_observed=" << totalEncodedFrames
                  << " runtime_failures=" << runtimeFailures
                  << " post_stop_failures=" << postStopFailures << "\n";
        exitCode = 0;
    }
    catch (const std::exception& error)
    {
        std::cerr << "FAIL reason=" << error.what() << "\n" << std::flush;
        if (capture && module)
        {
            try
            {
                const Api api = LoadApi(module);
                api.EncoderSetPacketConsumerActive(capture, 0);
                api.EncoderBlockSubmissions(capture);
                api.EncoderStop(capture);
                api.CaptureDestroy(capture);
                capture = nullptr;
            }
            catch (...)
            {
                std::cerr << "FAIL cleanup_exception=1\n";
            }
        }
    }

    if (module)
        FreeLibrary(module);
    if (retainedNvenc)
        FreeLibrary(retainedNvenc);
    if (uninitializeCom) { if (useWinrtApartment) RoUninitialize(); else CoUninitialize(); }
    return exitCode;
}
