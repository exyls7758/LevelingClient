#include "../../src/Leveling/Multiverse.Core/MfVideoDecoder.h"
#include <mfapi.h>
#include <mfidl.h>
#include <mftransform.h>
#include <mferror.h>
#include <wmcodecdsp.h>
#include <wrl/client.h>
#include <vector>
#include <algorithm>
#include <iostream>
#include <stdexcept>
#include <cstring>
using Microsoft::WRL::ComPtr;
void Check(HRESULT hr) { if(FAILED(hr)) { std::cerr<<"HRESULT "<<std::hex<<hr<<"\n"; throw std::runtime_error("MF failure"); } }
std::vector<std::vector<uint8_t>> Encode() {
    ComPtr<IMFTransform> encoder;
    Check(CoCreateInstance(CLSID_CMSH264EncoderMFT,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&encoder)));
    ComPtr<IMFMediaType> out,in; Check(MFCreateMediaType(&out));
    Check(out->SetGUID(MF_MT_MAJOR_TYPE,MFMediaType_Video)); Check(out->SetGUID(MF_MT_SUBTYPE,MFVideoFormat_H264));
    Check(out->SetUINT32(MF_MT_AVG_BITRATE,1000000)); Check(out->SetUINT32(MF_MT_INTERLACE_MODE,MFVideoInterlace_Progressive));
    Check(MFSetAttributeSize(out.Get(),MF_MT_FRAME_SIZE,64,64)); Check(MFSetAttributeRatio(out.Get(),MF_MT_FRAME_RATE,60,1));
    Check(MFSetAttributeRatio(out.Get(),MF_MT_PIXEL_ASPECT_RATIO,1,1)); Check(encoder->SetOutputType(0,out.Get(),0));
    Check(MFCreateMediaType(&in)); Check(out->CopyAllItems(in.Get())); Check(in->SetGUID(MF_MT_SUBTYPE,MFVideoFormat_NV12));
    Check(encoder->SetInputType(0,in.Get(),0)); Check(encoder->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING,0));
    Check(encoder->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM,0));
    std::vector<std::vector<uint8_t>> packets;
    auto drain=[&] {
        for(int limit=0;limit<100;limit++) {
            MFT_OUTPUT_STREAM_INFO info{}; Check(encoder->GetOutputStreamInfo(0,&info));
            ComPtr<IMFSample> sample; ComPtr<IMFMediaBuffer> buffer;
            Check(MFCreateSample(&sample)); Check(MFCreateMemoryBuffer(std::max<DWORD>(info.cbSize,65536),&buffer)); Check(sample->AddBuffer(buffer.Get()));
            MFT_OUTPUT_DATA_BUFFER data{}; data.pSample=sample.Get(); DWORD status=0;
            auto hr=encoder->ProcessOutput(0,1,&data,&status); if(data.pEvents)data.pEvents->Release();
            if(hr==MF_E_TRANSFORM_NEED_MORE_INPUT)return; Check(hr);
            ComPtr<IMFMediaBuffer> bytes; Check(sample->ConvertToContiguousBuffer(&bytes)); BYTE* p; DWORD length;
            Check(bytes->Lock(&p,nullptr,&length)); packets.emplace_back(p,p+length); Check(bytes->Unlock());
        }
        throw std::runtime_error("encoder unbounded output");
    };
    for(int i=0;i<30;i++) {
        ComPtr<IMFSample> sample; ComPtr<IMFMediaBuffer> buffer; Check(MFCreateSample(&sample)); Check(MFCreateMemoryBuffer(6144,&buffer));
        BYTE* p; Check(buffer->Lock(&p,nullptr,nullptr)); memset(p,32+i*5,4096); memset(p+4096,128,2048); Check(buffer->Unlock());
        Check(buffer->SetCurrentLength(6144)); Check(sample->AddBuffer(buffer.Get())); Check(sample->SetSampleTime(i*166667LL)); Check(sample->SetSampleDuration(166667));
        Check(encoder->ProcessInput(0,sample.Get(),0)); drain();
    }
    Check(encoder->ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN,0)); drain(); return packets;
}
int main() {
    Check(CoInitializeEx(nullptr,COINIT_MULTITHREADED)); Check(MFStartup(MF_VERSION));
    try {
        MfVideoDecoder invalid(0,64); if(invalid.Initialize()>=0)throw std::runtime_error("invalid dimensions accepted");
        auto packets=Encode(); if(packets.size()<20)throw std::runtime_error("missing encoded frames");
        for(bool software: {true,false}) {
            MfVideoDecoder decoder(64,64,software); Check(decoder.Initialize()); decoder.SetHeadlessOutput(true);
            if(decoder.Push(nullptr,0,0,false)>=0)throw std::runtime_error("empty access unit accepted");
            for(int cycle=0;cycle<3;cycle++) {
                if(cycle)decoder.SignalDiscontinuity();
                uint64_t id=0; for(auto& p:packets) { ++id; Check(decoder.Push(p.data(),(uint32_t)p.size(),id,id==1)); }
                MV_DecoderStats stats{}; decoder.FillStats(stats);
                if(stats.DecodedPictures<20u*(cycle+1)||!stats.FormatReady)throw std::runtime_error("decoder made no progress");
                if(software&&stats.Reserved0!=1)throw std::runtime_error("software path did not produce CPU output");
                if(!software&&stats.Reserved0!=2)throw std::runtime_error("DXVA path did not produce GPU textures");
                std::cout<<"PASS software="<<software<<" cycle="<<cycle<<" decoded="<<stats.DecodedPictures<<" output="<<stats.Reserved0<<"\n";
            }
        }
    } catch(std::exception& e) { std::cerr<<e.what()<<"\n"; return 1; }
    MFShutdown(); CoUninitialize(); return 0;
}
