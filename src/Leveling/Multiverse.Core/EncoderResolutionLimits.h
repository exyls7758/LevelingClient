#pragma once
#include <algorithm>
#include <cstdint>

struct RemoteEncoderLimits { std::uint32_t width; std::uint32_t height; };
// Preserve the established default. QHD is explicit and bounded; source
// aspect ratio/no-upscale/even dimensions remain resolved by the encoder.
inline RemoteEncoderLimits ResolveRemoteEncoderLimits(std::uint32_t width, std::uint32_t height)
{
    return { width == 0 ? 1920u : std::clamp(width, 2u, 2560u),
             height == 0 ? 1080u : std::clamp(height, 2u, 1440u) };
}
