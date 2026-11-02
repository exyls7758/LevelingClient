#include <Windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl/client.h>
#include <winrt/base.h>
#include <atomic>
#include <iostream>
#include <thread>
#include "Multiverse.Core/Core10WgcCapture.h"
#pragma comment(lib,"d3d11.lib")
#pragma comment(lib,"windowsapp.lib")
int main(int argc, char**) {
 try {
  winrt::init_apartment(winrt::apartment_type::multi_threaded);
  { Microsoft::WRL::ComPtr<ID3D11Device> device;
    winrt::check_hresult(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,nullptr));
    Microsoft::WRL::ComPtr<IDXGIOutputDuplication> duplication;
    if (argc > 1) {
      Microsoft::WRL::ComPtr<IDXGIDevice> dxgi; winrt::check_hresult(device.As(&dxgi));
      Microsoft::WRL::ComPtr<IDXGIAdapter> adapter; winrt::check_hresult(dxgi->GetAdapter(&adapter));
      Microsoft::WRL::ComPtr<IDXGIOutput> output; winrt::check_hresult(adapter->EnumOutputs(0,&output));
      Microsoft::WRL::ComPtr<IDXGIOutput1> output1; winrt::check_hresult(output.As(&output1));
      winrt::check_hresult(output1->DuplicateOutput(device.Get(), &duplication));
    }
    auto capture = std::make_unique<multiverse::Core10WgcCapture>(); DWORD warm=0,last=0;
    for(int i=0;i<20;i++) {
      if (argc > 3) capture = std::make_unique<multiverse::Core10WgcCapture>();
      std::atomic<unsigned> frames{0};
      if (argc > 2) duplication.Reset();
      winrt::check_hresult(capture->Start(device.Get(),MonitorFromPoint(POINT{0,0},MONITOR_DEFAULTTOPRIMARY),[&](auto*,auto&,auto&){++frames;}));
      for(int t=0;t<100 && frames.load()==0;t++) std::this_thread::sleep_for(std::chrono::milliseconds(20));
      capture->Stop(); std::this_thread::sleep_for(std::chrono::milliseconds(250));
      GetProcessHandleCount(GetCurrentProcess(),&last); if(i==0)warm=last;
      auto stats=capture->Snapshot();
      std::cout<<"cycle="<<i+1<<" frames="<<frames<<" handles="<<last<<" running="<<stats.Running<<" worker="<<stats.DeliveryWorkerAlive<<std::endl;
      if(!frames || stats.Running || stats.DeliveryWorkerAlive)return 2;
    }
    std::cout<<"capture_only_handle_growth="<<long(last)-long(warm)<<std::endl;
    if(last>warm+4)return 3;
  }
  winrt::uninit_apartment(); return 0;
 } catch(const winrt::hresult_error& e) {std::cerr<<"HRESULT="<<std::hex<<e.code().value<<std::endl;return 1;}
}
