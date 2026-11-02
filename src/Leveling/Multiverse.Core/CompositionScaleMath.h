#pragma once
#include <cmath>

namespace CompositionScaleMath
{
    struct Matrix
    {
        float m11, m12, m21, m22, m31, m32;
    };

    inline bool Valid(float sx, float sy)
    {
        return std::isfinite(sx) && std::isfinite(sy) &&
            sx >= 0.125f && sy >= 0.125f && sx <= 8.0f && sy <= 8.0f;
    }

    // The back buffer uses physical pixels while WinUI arranges the panel
    // in effective pixels. DXGI's composition transform is the inverse.
    inline Matrix MakeMatrix(float sx, float sy)
    {
        return {1.0f / sx, 0.0f, 0.0f, 1.0f / sy, 0.0f, 0.0f};
    }
}
