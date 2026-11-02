using System;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Multiverse.Services;

// One compositor backdrop for the shell. Cards tint that already blurred layer;
// they do not each sample/blur the desktop or run a render timer.
internal sealed class LevelingDesktopBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;
    internal Action? Failed { get; init; }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        try
        {
            var controller = new DesktopAcrylicController
            {
                Kind = DesktopAcrylicKind.Thin,
                TintColor = ColorHelper.FromArgb(255, 8, 12, 17),
                TintOpacity = 0.10f,
                LuminosityOpacity = 0.18f,
                FallbackColor = ColorHelper.FromArgb(255, 8, 12, 17)
            };
            _controller = controller;
            // Default configuration tracks activation, theme and accessibility.
            controller.SetSystemBackdropConfiguration(GetDefaultSystemBackdropConfiguration(target, root));
            controller.AddSystemBackdropTarget(target);
            StartupDiagnostics.Stage("UI.DesktopAcrylic.Connected");
        }
        catch (Exception ex)
        {
            _controller?.Dispose();
            _controller = null;
            StartupDiagnostics.RecordException("UI_DESKTOP_ACRYLIC_FALLBACK", ex);
            Failed?.Invoke();
        }
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        try
        {
            _controller?.RemoveSystemBackdropTarget(target);
        }
        finally
        {
            _controller?.Dispose();
            _controller = null;
            base.OnTargetDisconnected(target);
        }
    }
}
