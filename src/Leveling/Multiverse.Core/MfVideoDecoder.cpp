#include "MfVideoDecoder.h"
#include <mfapi.h>
#include <mfidl.h>
#include <mftransform.h>
#include <mferror.h>
#include <wmcodecdsp.h>
#include <codecapi.h>
#include <d3d11_4.h>
#include <d3d10.h>
#include <dxgi1_3.h>
#include <wrl/client.h>
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstring>
#include <string>
#include <vector>

#pragma comment(lib,"mfplat.lib")
#pragma comment(lib,"mfuuid.lib")
#pragma comment(lib,"wmcodecdspuuid.lib")
#pragma comment(lib,"ole32.lib")
#pragma comment(lib,"oleaut32.lib")
#pragma comment(lib,"d3d11.lib")
#pragma comment(lib,"dxgi.lib")
using Microsoft::WRL::ComPtr;
namespace {
MIDL_INTERFACE("63aad0b8-7c24-40ff-85a8-640d944cc325")
IMfSwapChainPanel : public IUnknown { virtual HRESULT STDMETHODCALLTYPE SetSwapChain(IDXGISwapChain*) = 0; };
struct MfLifetime {
    HRESULT status=MFStartup(MF_VERSION,MFSTARTUP_FULL);
    ~MfLifetime(){if(SUCCEEDED(status))MFShutdown();}
};
// COM apartment initialization is scoped to each calling thread. Objects are
// accessed serially by NativeNvdecSession, including renderer attachment.
struct ComScope {
    HRESULT status=CoInitializeEx(nullptr,COINIT_MULTITHREADED);
    ~ComScope(){if(SUCCEEDED(status))CoUninitialize();}
};
}
#define MF_WIDE_INNER(text) L##text
#define MF_WIDE(text) MF_WIDE_INNER(text)
#define MF_TRY(call) do { HRESULT result=(call); if(FAILED(result))return Fail(result,MF_WIDE(#call)); } while(false)

struct MfVideoDecoder::Impl {
    MfLifetime lifetime;
    uint32_t width,height; bool forceSoftware,headless=false,formatReady=false;
    bool usingDxva=false; uint64_t timestamp=0; float scaleX=1,scaleY=1;
    ComPtr<IMFTransform> decoder; ComPtr<IMFDXGIDeviceManager> manager;
    ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11VideoDevice> videoDevice; ComPtr<ID3D11VideoContext> videoContext;
    ComPtr<ID3D11VideoProcessorEnumerator> processorEnum; ComPtr<ID3D11VideoProcessor> processor;
    ComPtr<IDXGISwapChain2> chain; ComPtr<IMfSwapChainPanel> panel;
    ComPtr<ID3D11Texture2D> upload; HANDLE latencyHandle=nullptr;
    std::vector<uint8_t> cpuUpload;
    uint32_t processorWidth=0,processorHeight=0;
    MV_DecoderStats stats{}; MV_D3D11RendererStats render{};
    std::wstring message=L"Inicializando decoder do Windows";
    explicit Impl(uint32_t w,uint32_t h,bool software):width(w),height(h),forceSoftware(software){}
    ~Impl(){ if(decoder)decoder->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH,0); if(latencyHandle)CloseHandle(latencyHandle); }
    HRESULT Fail(HRESULT hr,const wchar_t* stage) {
        stats.LastStatus=hr; message=std::wstring(stage)+L" ("+std::to_wstring(static_cast<int32_t>(hr))+L")"; return hr;
    }
    HRESULT CreateTransform(bool dxva) {
        decoder.Reset(); manager.Reset(); usingDxva=false;
        MF_TRY(CoCreateInstance(CLSID_CMSH264DecoderMFT,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&decoder)));
        ComPtr<IMFAttributes> attributes;
        if(SUCCEEDED(decoder->GetAttributes(&attributes))) {
            attributes->SetUINT32(MF_LOW_LATENCY,TRUE);
            if(dxva) {
                UINT32 aware=0;
                if(FAILED(attributes->GetUINT32(MF_SA_D3D11_AWARE,&aware))||!aware)return E_NOINTERFACE;
                UINT reset=0; MF_TRY(MFCreateDXGIDeviceManager(&reset,&manager));
                MF_TRY(manager->ResetDevice(device.Get(),reset));
                MF_TRY(decoder->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER,reinterpret_cast<ULONG_PTR>(manager.Get())));
                usingDxva=true;
            }
        } else if(dxva) return E_NOINTERFACE;
        ComPtr<ICodecAPI> codec;
        if(SUCCEEDED(decoder.As(&codec))) { VARIANT v{}; v.vt=VT_BOOL; v.boolVal=VARIANT_TRUE; codec->SetValue(&CODECAPI_AVLowLatencyMode,&v); }
        ComPtr<IMFMediaType> input; MF_TRY(MFCreateMediaType(&input));
        MF_TRY(input->SetGUID(MF_MT_MAJOR_TYPE,MFMediaType_Video)); MF_TRY(input->SetGUID(MF_MT_SUBTYPE,MFVideoFormat_H264));
        MF_TRY(MFSetAttributeSize(input.Get(),MF_MT_FRAME_SIZE,width,height));
        MF_TRY(MFSetAttributeRatio(input.Get(),MF_MT_FRAME_RATE,60,1));
        MF_TRY(input->SetUINT32(MF_MT_INTERLACE_MODE,MFVideoInterlace_Progressive));
        MF_TRY(decoder->SetInputType(0,input.Get(),0));
        MF_TRY(SelectOutput());
        MF_TRY(decoder->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING,0));
        MF_TRY(decoder->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM,0));
        return S_OK;
    }
    HRESULT Initialize() {
        MF_TRY(lifetime.status);
        if(width==0||height==0||width>8192||height>8192)return Fail(E_INVALIDARG,L"Dimensões inválidas");
        D3D_FEATURE_LEVEL level;
        HRESULT hr=D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT|D3D11_CREATE_DEVICE_VIDEO_SUPPORT,nullptr,0,D3D11_SDK_VERSION,&device,&level,&context);
        if(FAILED(hr))return Fail(hr,L"Dispositivo D3D11 indisponível");
        ComPtr<ID3D10Multithread> threaded;
        if(SUCCEEDED(context.As(&threaded)))threaded->SetMultithreadProtected(TRUE);
        MF_TRY(device.As(&videoDevice)); MF_TRY(context.As(&videoContext));
        hr=forceSoftware?E_FAIL:CreateTransform(true);
        if(FAILED(hr))hr=CreateTransform(false);
        if(FAILED(hr))return hr;
        stats.Running=1; stats.LastStatus=0;
        message=usingDxva?L"Windows H.264 / D3D11 DXVA":L"Windows H.264 / CPU (compatibilidade)";
        return S_OK;
    }
    HRESULT SelectOutput() {
        for(DWORD i=0;i<32;i++) {
            ComPtr<IMFMediaType> output; HRESULT hr=decoder->GetOutputAvailableType(0,i,&output);
            if(hr==MF_E_NO_MORE_TYPES)break; if(FAILED(hr))return Fail(hr,L"Formato de saída");
            GUID subtype{}; if(FAILED(output->GetGUID(MF_MT_SUBTYPE,&subtype))||subtype!=MFVideoFormat_NV12)continue;
            MF_TRY(decoder->SetOutputType(0,output.Get(),0));
            UINT32 w=0,h=0; MF_TRY(MFGetAttributeSize(output.Get(),MF_MT_FRAME_SIZE,&w,&h));
            if(!w||!h||w>8192||h>8192)return Fail(E_INVALIDARG,L"Dimensões do vídeo");
            width=w;height=h; formatReady=true; processor.Reset();processorEnum.Reset(); upload.Reset();
            return S_OK;
        }
        return Fail(MF_E_INVALIDMEDIATYPE,L"Saída NV12 indisponível");
    }
    HRESULT Scale() {
        if(!chain)return S_OK;
        DXGI_MATRIX_3X2_F matrix{1.0f/scaleX,0,0,1.0f/scaleY,0,0}; return chain->SetMatrixTransform(&matrix);
    }
    HRESULT Attach(IUnknown* unknown,uint32_t w,uint32_t h) {
        if(!unknown||!w||!h||w>16384||h>16384)return E_INVALIDARG;
        if(panel) { auto hr=panel->SetSwapChain(nullptr);if(FAILED(hr))return hr; }
        panel.Reset();chain.Reset();if(latencyHandle){CloseHandle(latencyHandle);latencyHandle=nullptr;}
        if(device && FAILED(device->GetDeviceRemovedReason())) {
            // A swapchain alone cannot recover a removed decoder device.
            // Calls are serialized by the managed session; retire all resources
            // tied to the old device before negotiating a fresh decoder.
            decoder.Reset();manager.Reset();upload.Reset();processor.Reset();processorEnum.Reset();
            videoContext.Reset();videoDevice.Reset();context.Reset();device.Reset();
            formatReady=false;
            MF_TRY(Initialize());
        }
        MF_TRY(unknown->QueryInterface(IID_PPV_ARGS(&panel)));
        ComPtr<IDXGIDevice> dxgi;ComPtr<IDXGIAdapter> adapter;ComPtr<IDXGIFactory2> factory;
        MF_TRY(device.As(&dxgi)); MF_TRY(dxgi->GetAdapter(&adapter)); MF_TRY(adapter->GetParent(IID_PPV_ARGS(&factory)));
        DXGI_SWAP_CHAIN_DESC1 desc{};desc.Width=w;desc.Height=h;desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.SampleDesc.Count=1;desc.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT;desc.BufferCount=2;
        desc.Scaling=DXGI_SCALING_STRETCH;desc.SwapEffect=DXGI_SWAP_EFFECT_FLIP_DISCARD;desc.AlphaMode=DXGI_ALPHA_MODE_IGNORE;
        desc.Flags=DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;
        ComPtr<IDXGISwapChain1> created; MF_TRY(factory->CreateSwapChainForComposition(device.Get(),&desc,nullptr,&created));
        MF_TRY(created.As(&chain)); MF_TRY(chain->SetMaximumFrameLatency(1)); latencyHandle=chain->GetFrameLatencyWaitableObject();
        MF_TRY(Scale()); MF_TRY(panel->SetSwapChain(chain.Get()));
        render.Attached=1;render.TargetWidth=w;render.TargetHeight=h;render.LastStatus=0; return S_OK;
    }
    HRESULT Resize(uint32_t w,uint32_t h) {
        if(!chain)return E_UNEXPECTED;
        if(!w||!h||w>16384||h>16384)return E_INVALIDARG;
        if(w==render.TargetWidth&&h==render.TargetHeight)return S_OK;
        HRESULT hr=chain->ResizeBuffers(2,w,h,DXGI_FORMAT_B8G8R8A8_UNORM,DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT);
        if(FAILED(hr)){render.LastStatus=hr;return hr;}
        render.TargetWidth=w;render.TargetHeight=h;
        processor.Reset();processorEnum.Reset();
        return Scale();
    }
    HRESULT Texture(IMFSample* sample,ComPtr<ID3D11Texture2D>& texture,UINT& slice) {
        ComPtr<IMFMediaBuffer> buffer;MF_TRY(sample->GetBufferByIndex(0,&buffer));
        ComPtr<IMFDXGIBuffer> dxgi;
        if(SUCCEEDED(buffer.As(&dxgi))) {
            MF_TRY(dxgi->GetResource(IID_PPV_ARGS(&texture)));MF_TRY(dxgi->GetSubresourceIndex(&slice));return S_OK;
        }
        // Software decoding still uses the GPU for scaling/color conversion.
        if(!upload) {
            D3D11_TEXTURE2D_DESC desc{}; desc.Width=width;desc.Height=height;desc.MipLevels=1;desc.ArraySize=1;
            // A shader-resource-only texture is not a valid video-processor
            // input. BindFlags=0 is supported for this upload-only resource.
            desc.Format=DXGI_FORMAT_NV12;desc.SampleDesc.Count=1;desc.Usage=D3D11_USAGE_DEFAULT;desc.BindFlags=0;
            MF_TRY(device->CreateTexture2D(&desc,nullptr,&upload));
        }
        ComPtr<IMF2DBuffer> twoD;
        if(SUCCEEDED(buffer.As(&twoD))) {
            // Normalize decoder-specific pitch/plane padding before upload.
            // This copy is restricted to the software fallback; DXVA stays GPU-only.
            DWORD length=0;MF_TRY(twoD->GetContiguousLength(&length));
            if(length!=static_cast<uint64_t>(width)*height*3/2)return Fail(E_INVALIDARG,L"Layout NV12 inesperado");
            cpuUpload.resize(length);MF_TRY(twoD->ContiguousCopyTo(cpuUpload.data(),length));
            context->UpdateSubresource(upload.Get(),0,nullptr,cpuUpload.data(),width,0);
        } else {
            ComPtr<IMFMediaBuffer> contiguous;MF_TRY(sample->ConvertToContiguousBuffer(&contiguous));
            BYTE* bytes=nullptr;DWORD length=0;MF_TRY(contiguous->Lock(&bytes,nullptr,&length));
            if(length<static_cast<uint64_t>(width)*height*3/2){contiguous->Unlock();return Fail(E_INVALIDARG,L"NV12 incompleto");}
            context->UpdateSubresource(upload.Get(),0,nullptr,bytes,width,0);contiguous->Unlock();
        }
        texture=upload;slice=0;return S_OK;
    }
    HRESULT Present(IMFSample* sample) {
        if(headless||!chain)return S_OK;
        ++render.SubmittedFrames;
        // Present(DO_NOT_WAIT) supplies backpressure after GPU work is queued.
        // Polling the latency event before conversion discards frames that would
        // become presentable while the video processor is working.
        ComPtr<ID3D11Texture2D> texture;UINT slice=0;
        HRESULT textureResult=Texture(sample,texture,slice);if(FAILED(textureResult))return textureResult;
        D3D11_TEXTURE2D_DESC textureDesc{};texture->GetDesc(&textureDesc);
        if(!processor||processorWidth!=textureDesc.Width||processorHeight!=textureDesc.Height) {
            processor.Reset();processorEnum.Reset();
            D3D11_VIDEO_PROCESSOR_CONTENT_DESC desc{};desc.InputFrameFormat=D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
            desc.InputWidth=textureDesc.Width;desc.InputHeight=textureDesc.Height;desc.OutputWidth=render.TargetWidth;desc.OutputHeight=render.TargetHeight;
            desc.InputFrameRate={60,1};desc.OutputFrameRate={60,1};desc.Usage=D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
            MF_TRY(videoDevice->CreateVideoProcessorEnumerator(&desc,&processorEnum));MF_TRY(videoDevice->CreateVideoProcessor(processorEnum.Get(),0,&processor));
            processorWidth=textureDesc.Width;processorHeight=textureDesc.Height;
            videoContext->VideoProcessorSetStreamAutoProcessingMode(processor.Get(),0,FALSE);
            D3D11_VIDEO_PROCESSOR_COLOR_SPACE input{};input.YCbCr_Matrix=1;input.Nominal_Range=1;
            videoContext->VideoProcessorSetStreamColorSpace(processor.Get(),0,&input);
            D3D11_VIDEO_PROCESSOR_COLOR_SPACE output{};output.RGB_Range=0;videoContext->VideoProcessorSetOutputColorSpace(processor.Get(),&output);
        }
        ComPtr<ID3D11VideoProcessorInputView> input;D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC in{};
        in.ViewDimension=D3D11_VPIV_DIMENSION_TEXTURE2D;in.Texture2D.ArraySlice=slice/textureDesc.MipLevels;in.Texture2D.MipSlice=slice%textureDesc.MipLevels;
        MF_TRY(videoDevice->CreateVideoProcessorInputView(texture.Get(),processorEnum.Get(),&in,&input));
        ComPtr<ID3D11Texture2D> back;MF_TRY(chain->GetBuffer(0,IID_PPV_ARGS(&back)));
        ComPtr<ID3D11VideoProcessorOutputView> output;D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC out{};out.ViewDimension=D3D11_VPOV_DIMENSION_TEXTURE2D;
        MF_TRY(videoDevice->CreateVideoProcessorOutputView(back.Get(),processorEnum.Get(),&out,&output));
        RECT source{0,0,(LONG)width,(LONG)height},target{0,0,(LONG)render.TargetWidth,(LONG)render.TargetHeight};
        videoContext->VideoProcessorSetStreamSourceRect(processor.Get(),0,TRUE,&source);
        videoContext->VideoProcessorSetStreamDestRect(processor.Get(),0,TRUE,&target);
        videoContext->VideoProcessorSetOutputTargetRect(processor.Get(),TRUE,&target);
        D3D11_VIDEO_PROCESSOR_STREAM stream{};stream.Enable=TRUE;stream.pInputSurface=input.Get();
        MF_TRY(videoContext->VideoProcessorBlt(processor.Get(),output.Get(),0,1,&stream));
        ++render.PresentAttempts;render.PresentInFlight=1;
        auto begin=std::chrono::steady_clock::now();auto hr=chain->Present(0,DXGI_PRESENT_DO_NOT_WAIT);
        render.LastPresentCallMs=std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-begin).count();render.PresentInFlight=0;
        if(hr==DXGI_ERROR_WAS_STILL_DRAWING){++render.DroppedPresents;++render.PresentStillDrawing;return S_OK;}
        if(FAILED(hr)){++render.PresentFailures;render.LastStatus=hr;return Fail(hr,L"Apresentação D3D11");}
        ++render.PresentedFrames;render.LastStatus=0;return S_OK;
    }
    HRESULT Drain() {
        for(int attempt=0;attempt<32;attempt++) {
            MFT_OUTPUT_STREAM_INFO info{};MF_TRY(decoder->GetOutputStreamInfo(0,&info));
            ComPtr<IMFSample> supplied;
            if(!(info.dwFlags&MFT_OUTPUT_STREAM_PROVIDES_SAMPLES)) {
                ComPtr<IMFMediaBuffer> buffer;MF_TRY(MFCreateSample(&supplied));
                MF_TRY(MFCreateMemoryBuffer(std::max<DWORD>(info.cbSize,width*height*3/2),&buffer));MF_TRY(supplied->AddBuffer(buffer.Get()));
            }
            MFT_OUTPUT_DATA_BUFFER data{};data.pSample=supplied.Get();DWORD flags=0;
            HRESULT hr=decoder->ProcessOutput(0,1,&data,&flags);
            if(data.pEvents)data.pEvents->Release();
            ComPtr<IMFSample> received;
            if(data.pSample&&data.pSample!=supplied.Get())received.Attach(data.pSample);else received=supplied;
            if(hr==MF_E_TRANSFORM_NEED_MORE_INPUT)return S_OK;
            if(hr==MF_E_TRANSFORM_STREAM_CHANGE){MF_TRY(SelectOutput());continue;}
            if(FAILED(hr))return Fail(hr,L"Decodificação H.264");
            if(received) {
                ComPtr<IMFMediaBuffer> buffer;ComPtr<IMFDXGIBuffer> gpu;
                // Reserved0 records the actual output memory, not merely a
                // requested acceleration flag: 2 GPU texture, 1 CPU buffer.
                stats.Reserved0=SUCCEEDED(received->GetBufferByIndex(0,&buffer))&&SUCCEEDED(buffer.As(&gpu))?2:1;
                ++stats.DecodedPictures;++stats.DisplayedFrames;
                HRESULT presented=Present(received.Get());if(FAILED(presented))return presented;
            }
        }
        return Fail(E_UNEXPECTED,L"Decoder excedeu o limite de saída");
    }
    HRESULT Push(const uint8_t* data,uint32_t bytes,bool key) {
        if(!decoder||!data||!bytes||bytes>32*1024*1024)return E_INVALIDARG;
        auto begin=std::chrono::steady_clock::now();
        ComPtr<IMFSample> sample;ComPtr<IMFMediaBuffer> buffer;MF_TRY(MFCreateSample(&sample));MF_TRY(MFCreateMemoryBuffer(bytes,&buffer));
        BYTE* destination=nullptr;MF_TRY(buffer->Lock(&destination,nullptr,nullptr));memcpy(destination,data,bytes);buffer->Unlock();
        MF_TRY(buffer->SetCurrentLength(bytes));MF_TRY(sample->AddBuffer(buffer.Get()));
        MF_TRY(sample->SetSampleTime(timestamp++*166667LL));MF_TRY(sample->SetSampleDuration(166667));
        if(key)sample->SetUINT32(MFSampleExtension_CleanPoint,TRUE);
        HRESULT hr=decoder->ProcessInput(0,sample.Get(),0);
        if(hr==MF_E_NOTACCEPTING){HRESULT drained=Drain();if(FAILED(drained))return drained;hr=decoder->ProcessInput(0,sample.Get(),0);}
        if(FAILED(hr))return Fail(hr,L"Entrada H.264");
        ++stats.SubmittedFrames;HRESULT drained=Drain();if(FAILED(drained))return drained;stats.LastStatus=0;
        stats.LastDecodeLatencyMs=std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-begin).count();
        stats.AverageDecodeLatencyMs+=(stats.LastDecodeLatencyMs-stats.AverageDecodeLatencyMs)/stats.SubmittedFrames;
        return S_OK;
    }
};
MfVideoDecoder::MfVideoDecoder(uint32_t w,uint32_t h,bool software):impl(std::make_unique<Impl>(w,h,software)){}
MfVideoDecoder::~MfVideoDecoder()=default;
int32_t MfVideoDecoder::Initialize(){ComScope com;return impl->Initialize();}
int32_t MfVideoDecoder::Push(const uint8_t* data,uint32_t bytes,uint64_t,bool key){ComScope com;return impl->Push(data,bytes,key);}
void MfVideoDecoder::SignalDiscontinuity(){if(impl->decoder){impl->decoder->ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH,0);impl->decoder->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM,0);}}
void MfVideoDecoder::SetHeadlessOutput(bool enabled){impl->headless=enabled;}
int32_t MfVideoDecoder::AttachSwapChainPanel(IUnknown* panel,uint32_t w,uint32_t h){return impl->Attach(panel,w,h);}
int32_t MfVideoDecoder::ResizeSwapChain(uint32_t w,uint32_t h){return impl->Resize(w,h);}
int32_t MfVideoDecoder::SetCompositionScale(float x,float y){if(!std::isfinite(x)||!std::isfinite(y)||x<=0||y<=0)return E_INVALIDARG;impl->scaleX=x;impl->scaleY=y;return impl->Scale();}
int32_t MfVideoDecoder::TryDetachPanel(){if(impl->panel){auto hr=impl->panel->SetSwapChain(nullptr);if(FAILED(hr))return hr;}impl->panel.Reset();impl->chain.Reset();if(impl->latencyHandle){CloseHandle(impl->latencyHandle);impl->latencyHandle=nullptr;}impl->render.Attached=0;return S_OK;}
void MfVideoDecoder::DetachSwapChainPanel(){TryDetachPanel();}
void MfVideoDecoder::FillStats(MV_DecoderStats& stats)const{stats=impl->stats;stats.Width=impl->width;stats.Height=impl->height;stats.FormatReady=impl->formatReady;}
void MfVideoDecoder::FillRendererStats(MV_D3D11RendererStats& stats)const{stats=impl->render;stats.SourceWidth=impl->width;stats.SourceHeight=impl->height;}
void MfVideoDecoder::GetPreviewInfo(uint32_t& w,uint32_t& h,uint32_t& stride,uint32_t& bytes)const{w=h=stride=bytes=0;}
int32_t MfVideoDecoder::CopyPreview(uint8_t*,uint32_t,uint64_t& id){id=0;return 1;}
void MfVideoDecoder::CopyMessage(wchar_t* destination,uint32_t chars)const{if(destination&&chars)wcsncpy_s(destination,chars,impl->message.c_str(),_TRUNCATE);}
