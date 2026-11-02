using System;
using System.Runtime.CompilerServices;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Multiverse.Services;

/// <summary>
/// Delayed explanatory tooltip used by Leveling's configurable controls.
/// A one-shot timer exists only while the pointer rests over a control.
/// The explanation is shown in a non-interactive Popup so opening it does
/// not steal pointer hover/focus from the control that owns the hint.
/// </summary>
public sealed class LevelingInfoToolTip : DependencyObject
{
    private const double MaxTextWidth = 360;
    private const double PopupMargin = 8;
    private static readonly ConditionalWeakTable<FrameworkElement, HoverState> States = new();
    private static HoverState? _activeState;
    private static bool _isSuspended;

    public static bool IsSuspended
    {
        get => _isSuspended;
        set
        {
            if (_isSuspended == value)
                return;
            _isSuspended = value;
            if (value)
            {
                _activeState?.CancelAndReleaseFromGlobal();
                _activeState = null;
            }
        }
    }

    private LevelingInfoToolTip()
    {
    }

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text",
        typeof(string),
        typeof(LevelingInfoToolTip),
        new PropertyMetadata(string.Empty, OnTextChanged));

    public static void SetText(DependencyObject element, string value) => element.SetValue(TextProperty, value);

    public static string GetText(DependencyObject element) => (string)element.GetValue(TextProperty);

    private static void OnTextChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not FrameworkElement element)
        {
            return;
        }

        HoverState state = States.GetValue(element, owner => new HoverState(owner));
        state.Text = args.NewValue as string ?? string.Empty;
        state.AttachIfNeeded();
    }

    private sealed class HoverState
    {
        private readonly FrameworkElement _owner;
        private DispatcherQueueTimer? _timer;
        private Popup? _popup;
        private bool _attached;
        private bool _pointerOver;

        public HoverState(FrameworkElement owner)
        {
            _owner = owner;
        }

        public string Text { get; set; } = string.Empty;

        public void AttachIfNeeded()
        {
            if (_attached)
            {
                return;
            }

            _owner.PointerEntered += Owner_PointerEntered;
            _owner.PointerExited += Owner_PointerExited;
            _owner.PointerCanceled += Owner_PointerCanceled;
            _owner.Unloaded += Owner_Unloaded;
            _attached = true;
        }

        private void Owner_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (IsSuspended || string.IsNullOrWhiteSpace(Text))
            {
                return;
            }

            _pointerOver = true;
            ClaimGlobalOwnership();
            Hide();

            _timer ??= CreateTimer();
            _timer.Stop();
            _timer.Start();
        }

        private void Owner_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            // With the non-interactive Popup, PointerExited now means the pointer
            // really left the owner. Cancel immediately so no stale 3-second timer
            // can open a hint after the cursor has moved elsewhere.
            _pointerOver = false;
            StopAndHide();
        }

        private void Owner_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            _pointerOver = false;
            StopAndHide();
        }

        private void Owner_Unloaded(object sender, RoutedEventArgs e)
        {
            _pointerOver = false;
            StopAndHide();
        }

        private DispatcherQueueTimer CreateTimer()
        {
            DispatcherQueueTimer timer = _owner.DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(3);
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (IsSuspended)
                    return;
                if (!ReferenceEquals(_activeState, this))
                {
                    return;
                }

                if (_pointerOver && !string.IsNullOrWhiteSpace(Text))
                {
                    Show();
                }
            };
            return timer;
        }

        private void Show()
        {
            if (IsSuspended)
                return;
            if (!ReferenceEquals(_activeState, this))
            {
                return;
            }

            Hide();

            XamlRoot? xamlRoot = _owner.XamlRoot;
            if (xamlRoot?.Content is not UIElement root)
            {
                return;
            }

            var title = new TextBlock
            {
                Text = "O QUE ESTA OPÇÃO FAZ",
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 239, 43, 86))
            };

            var description = new TextBlock
            {
                Text = Text,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = MaxTextWidth,
                Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 232, 237, 244))
            };

            var content = new StackPanel
            {
                Spacing = 6,
                MaxWidth = MaxTextWidth
            };
            content.Children.Add(title);
            content.Children.Add(description);

            var card = new Border
            {
                Background = new SolidColorBrush(ColorHelper.FromArgb(255, 13, 20, 29)),
                BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 91, 34, 53)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(12, 10, 12, 11),
                Child = content,
                IsHitTestVisible = false
            };

            Point ownerTopLeft = _owner.TransformToVisual(root).TransformPoint(new Point(0, 0));
            double x = ownerTopLeft.X;
            double y = ownerTopLeft.Y + _owner.ActualHeight + 6;

            // Keep the card inside the visible XamlRoot when the owner is near
            // the right or bottom edge. The height is estimated before layout;
            // the popup itself remains non-interactive and does not steal hover.
            double maxX = Math.Max(PopupMargin, xamlRoot.Size.Width - MaxTextWidth - 32);
            x = Math.Clamp(x, PopupMargin, maxX);
            if (y + 120 > xamlRoot.Size.Height)
            {
                y = Math.Max(PopupMargin, ownerTopLeft.Y - 120);
            }

            _popup = new Popup
            {
                XamlRoot = xamlRoot,
                Child = card,
                IsLightDismissEnabled = false,
                HorizontalOffset = x,
                VerticalOffset = y
            };
            _popup.IsOpen = true;
        }

        private void StopAndHide()
        {
            _timer?.Stop();
            Hide();
            ReleaseGlobalOwnership();
        }

        private void ClaimGlobalOwnership()
        {
            if (IsSuspended)
                return;
            if (ReferenceEquals(_activeState, this))
            {
                return;
            }

            _activeState?.CancelFromGlobal();
            _activeState = this;
        }

        private void ReleaseGlobalOwnership()
        {
            if (ReferenceEquals(_activeState, this))
            {
                _activeState = null;
            }
        }

        private void CancelFromGlobal()
        {
            _pointerOver = false;
            _timer?.Stop();
            Hide();
        }

        internal void CancelAndReleaseFromGlobal()
        {
            CancelFromGlobal();
            ReleaseGlobalOwnership();
        }

        private void Hide()
        {
            if (_popup is not null)
            {
                _popup.IsOpen = false;
                _popup.Child = null;
                _popup = null;
            }
        }
    }
}
