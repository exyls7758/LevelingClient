#pragma once
#include <algorithm>
#include <chrono>
#include <cstdint>
namespace multiverse {
// WGC eligibility must be shorter than one output period. Otherwise compositor
// quantization skips refreshes before the application pacer can see them.
// Keep expensive GPU copies bounded by the existing downstream ingress pacer.
inline std::chrono::microseconds WgcUpdateInterval(std::uint32_t fps) noexcept {
    fps = std::clamp<std::uint32_t>(fps, 30u, 120u);
    return std::chrono::microseconds(std::min<std::uint32_t>(14000u, 1000000u / (fps * 2u)));
}
}
