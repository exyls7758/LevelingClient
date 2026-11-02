using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Multiverse.Views;
using System;
namespace Multiverse.Services;
public sealed class StudioQuickLinks : Grid
{
    public StudioQuickLinks()
    {
        ColumnSpacing = 8;
        var items = new (string Title, string Glyph, Type Page)[] {
            ("Captura", "\uE7F4", typeof(DevicePreferencesPage)),
            ("Transmissão", "\uE704", typeof(TransmissionPage)),
            ("Áudio", "\uE7F6", typeof(AudioPage)),
            ("Controle", "\uE7FC", typeof(ControllerPage))
        };
        for (int i = 0; i < items.Length; i++)
        {
            var item = items[i];
            ColumnDefinitions.Add(new ColumnDefinition());
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(new FontIcon { Glyph = item.Glyph, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 17, Foreground = (Brush)Application.Current.Resources["DesignMuted"] });
            content.Children.Add(new TextBlock { Text = item.Title, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            var button = new Button { Content = content, Padding = new Thickness(10,14,10,14), HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(0),
                Background = (Brush)Application.Current.Resources["DesignSurface"] };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, item.Title);
            ToolTipService.SetToolTip(button, item.Title);
            button.Click += (_, _) => {
                for (DependencyObject? parent = this; parent is not null; parent = VisualTreeHelper.GetParent(parent))
                    if (parent is Page page) { page.Frame?.Navigate(item.Page); break; }
            };
            SetColumn(button,i); Children.Add(button);
        }
    }
}