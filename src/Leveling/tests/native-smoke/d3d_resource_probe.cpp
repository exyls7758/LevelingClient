#include <Windows.h>
#include <d3d11.h>
#include <iostream>
#include <string>
#include <thread>
#include <chrono>
#pragma comment(lib, "d3d11.lib")

// Independent diagnostic: no Leveling DLL, WGC, NVENC, or video session.
// Release the immediate context before the device, and record remaining refs.
int main(int argc, char** argv)
{
    const bool warp = argc > 1 && std::string(argv[1]) == "--warp";
    DWORD warm = 0, last = 0;
    for (int cycle = 1; cycle <= 20; ++cycle)
    {
        ID3D11Device* device = nullptr;
        ID3D11DeviceContext* context = nullptr;
        const HRESULT hr = D3D11CreateDevice(nullptr,
            warp ? D3D_DRIVER_TYPE_WARP : D3D_DRIVER_TYPE_HARDWARE,
            nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0,
            D3D11_SDK_VERSION, &device, nullptr, &context);
        if (FAILED(hr)) { std::cout << "FAIL create hr=" << std::hex << hr << std::endl; return 1; }
        context->ClearState();
        context->Flush();
        context->Release();
        const auto refs = device->Release();
        std::this_thread::sleep_for(std::chrono::milliseconds(250));
        if (!GetProcessHandleCount(GetCurrentProcess(), &last)) return 2;
        if (cycle == 1) warm = last;
        std::cout << "cycle=" << cycle << " handles=" << last << " remaining_device_refs=" << refs << std::endl;
    }
    std::cout << "mode=" << (warp ? "warp" : "hardware")
              << " handle_delta=" << static_cast<long>(last) - static_cast<long>(warm) << std::endl;
    return last > warm + 4 ? 3 : 0;
}
