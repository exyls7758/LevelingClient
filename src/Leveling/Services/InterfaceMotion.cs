using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using System;
using System.Numerics;

namespace Multiverse.Services;

// Finite compositor animations: no rendering timer, work on the video queue,
// or delay before an action. Template instances attach only to themselves.
public static class InterfaceMotion
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(InterfaceMotion), new PropertyMetadata(false, Changed));
    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);

    private static void Changed(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    {
        if (obj is not FrameworkElement element) return;
        if ((bool)args.NewValue)
        {
            element.PointerEntered += Enter;
            element.PointerExited += Exit;
            element.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Press), true);
            element.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Release), true);
            element.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(Exit), true);
            element.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(CaptureLost), true);
            element.Loaded += Loaded;
            element.Unloaded += Unloaded;
            element.GotFocus += Focused;
            if (element is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase button) button.Click += Activated;
            element.LostFocus += Blurred;
        }
        else
        {
            element.PointerEntered -= Enter;
            element.PointerExited -= Exit;
            element.RemoveHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Press));
            element.RemoveHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Release));
            element.RemoveHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(Exit));
            element.RemoveHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(CaptureLost));
            element.Loaded -= Loaded;
            element.Unloaded -= Unloaded;
            element.GotFocus -= Focused;
            if (element is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase button) button.Click -= Activated;
            element.LostFocus -= Blurred;
            Reset(element);
        }
    }
    private static void Activated(object sender, RoutedEventArgs args) => ClickResponse(sender);
    private static void Enter(object sender, PointerRoutedEventArgs args) => AnimateOpacity(sender, .90f);
    private static void Exit(object sender, PointerRoutedEventArgs args) => AnimateOpacity(sender, 1f);
    private static void Press(object sender, PointerRoutedEventArgs args)
    {
        AnimateOpacity(sender, .82f);
        if (sender is FrameworkElement element && !ShouldReduceMotion())
        {
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            ElementCompositionPreview.GetElementVisual(element).Properties.InsertVector3("Translation", new Vector3(0, 1, 0));
        }
    }
    private static void Release(object sender, PointerRoutedEventArgs args) => ClickResponse(sender);
    private static void CaptureLost(object sender, PointerRoutedEventArgs args) => ClickResponse(sender);
    private static void Loaded(object sender, RoutedEventArgs args) => Reveal(sender, .68f, 180);
    internal static bool ShouldReduceMotion() =>
        MultiverseSettingsService.Instance.GetAppPreferences().LowMotion ||
        !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;

    private static void ClickResponse(object sender)
    {
        Reveal(sender, .82f, 200);
        if (sender is not FrameworkElement element || ShouldReduceMotion()) return;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        using var motion = visual.Compositor.CreateVector3KeyFrameAnimation();
        using var ease = visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(.2f, .8f), new Vector2(.2f, 1));
        motion.InsertKeyFrame(0, new Vector3(0, 1, 0));
        motion.InsertKeyFrame(1, Vector3.Zero, ease);
        motion.Duration = TimeSpan.FromMilliseconds(180);
        visual.StartAnimation("Translation", motion);
    }

    private static void Reveal(object sender, float from, int duration)
    {
        if (sender is not FrameworkElement element) return;
        if (ShouldReduceMotion() || element is Microsoft.UI.Xaml.Controls.Control { IsEnabled: false })
        { Reset(element); return; }
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Opacity");
        using var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        using var easing = visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(.16f, 1), new Vector2(.3f, 1));
        animation.InsertKeyFrame(0, from);
        animation.InsertKeyFrame(1, 1f, easing);
        animation.Duration = TimeSpan.FromMilliseconds(duration);
        visual.StartAnimation("Opacity", animation);
    }
    private static void Unloaded(object sender, RoutedEventArgs args) => Reset((FrameworkElement)sender);
    private static void Focused(object sender, RoutedEventArgs args)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Control { FocusState: FocusState.Keyboard })
            AnimateOpacity(sender, .96f);
    }
    private static void Blurred(object sender, RoutedEventArgs args) => Reset((FrameworkElement)sender);
    private static void Reset(FrameworkElement element)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Translation");
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
        visual.Opacity = 1;
        visual.Scale = Vector3.One;
    }
    private static void AnimateOpacity(object sender, float value)
    {
        if (sender is not FrameworkElement element) return;
        if (element is Microsoft.UI.Xaml.Controls.Control { IsEnabled: false }) { Reset(element); return; }
        if (ShouldReduceMotion())
        { Reset(element); return; }
        var visual = ElementCompositionPreview.GetElementVisual(element);
        // Opacity feedback preserves native text rasterization and pixel alignment.
        // Scaling the whole control leaves glyphs blurred at fractional hover sizes.
        visual.Scale = Vector3.One;
        using var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1, value);
        animation.Duration = TimeSpan.FromMilliseconds(value < 1 ? 90 : 140);
        visual.StartAnimation("Opacity", animation);
        // Keep geometry pixel-aligned: translating a rounded card by fractional
        // pixels during hover makes its outline and text appear jagged.
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        visual.StopAnimation("Translation");
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
    }
}
