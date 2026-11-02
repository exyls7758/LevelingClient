using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System;
using System.Numerics;

namespace Multiverse.Services;

// Halo lives at the shell root, outside button template clipping. No rendering loop.
public sealed class SidebarIcon : Grid
{
    private readonly Grid _halo = new() { IsHitTestVisible = false };
    private readonly FontIcon _icon = new() { FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 17 };
    private readonly Border _tile;
    private CompositionRadialGradientBrush? _brush;
    private SpriteVisual? _glow;
    private bool _selected;
    private Grid? _shell;
    public string Glyph { get => _icon.Glyph; set => _icon.Glyph = value; }
    public bool Selected { get => _selected; set { _selected = value; Refresh(); } }

    public SidebarIcon()
    {
        Width = 48; Height = 48;
        _tile = new Border { Width = 30, Height = 30, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(8), Child = _icon };
        Children.Add(_tile);
        SizeChanged += (_, _) => PositionHalo();
        Loaded += (_, _) => Connect();
        Unloaded += (_, _) => Disconnect();
    }

    private void Connect()
    {
        Disconnect();
        if (new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast) { Refresh(); return; }
        try
        {
            _shell = XamlRoot?.Content as Grid;
            if (_shell is null) return;
            Grid.SetRowSpan(_halo, 100); Grid.SetColumnSpan(_halo, 100);
            Canvas.SetZIndex(_halo, 100);
            _shell.Children.Add(_halo);
            _shell.SizeChanged += ShellSizeChanged;
            var compositor = ElementCompositionPreview.GetElementVisual(_halo).Compositor;
            _brush = compositor.CreateRadialGradientBrush();
            _brush.MappingMode = CompositionMappingMode.Relative;
            _brush.EllipseCenter = new Vector2(.5f);
            _brush.EllipseRadius = new Vector2(.5f);
            _brush.ColorStops.Add(compositor.CreateColorGradientStop(0, ColorHelper.FromArgb(48, 255, 63, 91)));
            _brush.ColorStops.Add(compositor.CreateColorGradientStop(.4f, ColorHelper.FromArgb(20, 230, 45, 79)));
            _brush.ColorStops.Add(compositor.CreateColorGradientStop(1, ColorHelper.FromArgb(0, 230, 45, 79)));
            _glow = compositor.CreateSpriteVisual();
            _glow.Size = new Vector2(88); _glow.Offset = Vector3.Zero;
            _glow.Brush = _brush; _glow.Opacity = 0;
            ElementCompositionPreview.SetElementChildVisual(_halo, _glow);
            PositionHalo();
            Refresh();
        }
        catch (Exception ex) { Disconnect(); StartupDiagnostics.RecordException("UI_SIDEBAR_GLOW_FALLBACK", ex); }
    }

    private void ShellSizeChanged(object sender, SizeChangedEventArgs args)
        => DispatcherQueue.TryEnqueue(PositionHalo);

    internal static void RefreshShell(DependencyObject parent)
    {
        if (parent is SidebarIcon icon) { icon.PositionHalo(); return; }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            RefreshShell(VisualTreeHelper.GetChild(parent, i));
    }

    private void PositionHalo()
    {
        if (_shell is null || _glow is null || !IsLoaded) return;
        var point = TransformToVisual(_shell).TransformPoint(new Windows.Foundation.Point(ActualWidth / 2, ActualHeight / 2));
        _glow.Offset = new Vector3((float)point.X - 44, (float)point.Y - 44, 0);
    }

    private void Refresh()
    {
        _tile.Background = new SolidColorBrush(_selected ? ColorHelper.FromArgb(255, 230, 45, 79) : Colors.Transparent);
        _icon.Foreground = new SolidColorBrush(_selected ? Colors.White : ColorHelper.FromArgb(255, 152, 164, 181));
        if (_glow is null) return;
        DispatcherQueue.TryEnqueue(PositionHalo);
        float target = _selected ? 1 : 0;
        if (InterfaceMotion.ShouldReduceMotion()) { _glow.StopAnimation("Opacity"); _glow.Opacity = target; return; }
        using var fade = _glow.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, target);
        fade.Duration = TimeSpan.FromMilliseconds(_selected ? 180 : 120);
        _glow.StartAnimation("Opacity", fade);
    }

    private void Disconnect()
    {
        if (_shell is not null) { _shell.SizeChanged -= ShellSizeChanged; _shell.Children.Remove(_halo); _shell = null; }
        ElementCompositionPreview.SetElementChildVisual(_halo, null);
        _glow?.Dispose(); _glow = null;
        _brush?.Dispose(); _brush = null;
    }
}
