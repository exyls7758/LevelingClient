#pragma once
#include "MultiverseCore.h"
#include <Windows.h>
#include <unknwn.h>
#include <memory>

// Windows H.264 decoder and D3D11 presentation. No CUDA headers or imports.
class MfVideoDecoder final {
public:
    MfVideoDecoder(uint32_t width, uint32_t height, bool software = false);
    ~MfVideoDecoder();
    int32_t Initialize();
    int32_t Push(const uint8_t* data, uint32_t bytes, uint64_t frameId, bool keyFrame);
    void SignalDiscontinuity();
    void SetHeadlessOutput(bool enabled);
    int32_t AttachSwapChainPanel(IUnknown* panel, uint32_t width, uint32_t height);
    int32_t ResizeSwapChain(uint32_t width, uint32_t height);
    int32_t SetCompositionScale(float x, float y);
    int32_t TryDetachPanel();
    void DetachSwapChainPanel();
    void FillStats(MV_DecoderStats& stats) const;
    void FillRendererStats(MV_D3D11RendererStats& stats) const;
    void GetPreviewInfo(uint32_t& w,uint32_t& h,uint32_t& stride,uint32_t& bytes) const;
    int32_t CopyPreview(uint8_t*,uint32_t,uint64_t&);
    void CopyMessage(wchar_t* destination,uint32_t chars) const;
private:
    struct Impl;
    std::unique_ptr<Impl> impl;
};
