#include "../../Multiverse.Core/EncoderResolutionLimits.h"
#include <cassert>
int main() {
    auto defaults = ResolveRemoteEncoderLimits(0, 0);
    assert(defaults.width == 1920 && defaults.height == 1080);
    auto qhd = ResolveRemoteEncoderLimits(2560, 1440);
    assert(qhd.width == 2560 && qhd.height == 1440);
    auto bounded = ResolveRemoteEncoderLimits(0xffffffff, 0xffffffff);
    assert(bounded.width == 2560 && bounded.height == 1440);
    auto small = ResolveRemoteEncoderLimits(1280, 720);
    assert(small.width == 1280 && small.height == 720);
    auto invalid = ResolveRemoteEncoderLimits(1, 1);
    assert(invalid.width >= 2 && invalid.height >= 2);
}
