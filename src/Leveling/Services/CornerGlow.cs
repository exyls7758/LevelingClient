using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using System;
using System.Numerics;

namespace Multiverse.Services;

// Static compositor wash. No timer, shadows, bitmap blur or frame callbacks.
public sealed class CornerGlow : Grid
{
    private SpriteVisual? _visual;
    private CompositionRadialGradientBrush? _brush;
    public bool BottomLeft { get; set; }
    public bool AroundCard { get; set; }

    public CornerGlow()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => Connect();
        Unloaded += (_, _) => Disconnect();
    }

    private void Connect()
    {
        Disconnect();
        if (new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast) return;
        try
        {
            var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
            _brush = compositor.CreateRadialGradientBrush();
            _brush.MappingMode = CompositionMappingMode.Relative;
            _brush.EllipseCenter = AroundCard ? new Vector2(.5f) : BottomLeft ? new Vector2(0, 1) : new Vector2(1, 0);
            _brush.EllipseRadius = AroundCard ? new Vector2(.5f) : Vector2.One;
            _brush.ColorStops.Add(compositor.CreateColorGradientStop(0, ColorHelper.FromArgb((byte)(AroundCard ? 64 : 38), 230, 45, 79)));
            _brush.ColorStops.Add(compositor.CreateColorGradientStop(.45f, ColorHelper.FromArgb(16, 230, 45, 79)));
            _brush.ColorStops.Add(compositor.CreateColorGradientStop(1, ColorHelper.FromArgb(0, 230, 45, 79)));
            _visual = compositor.CreateSpriteVisual();
            _visual.RelativeSizeAdjustment = Vector2.One;
            _visual.Brush = _brush;
            ElementCompositionPreview.SetElementChildVisual(this, _visual);
        }
        catch (Exception ex)
        {
            Disconnect();
            StartupDiagnostics.RecordException("UI_CORNER_GLOW_FALLBACK", ex);
        }
    }

    private void Disconnect()
    {
        ElementCompositionPreview.SetElementChildVisual(this, null);
        _visual?.Dispose(); _visual = null;
        _brush?.Dispose(); _brush = null;
    }
}
