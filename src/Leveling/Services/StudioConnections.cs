using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Multiverse.Models;
using System;
using System.Linq;

namespace Multiverse.Services;

// Uses the existing presence cache and connection workflow. No extra polling
// timer, account system, invented friends, or synthetic online indicators.
public sealed class StudioConnections : UserControl, IDisposable
{
    private readonly StackPanel _list = new() { Spacing=10 };
    private readonly TextBox _search = new() { PlaceholderText="Buscar computadores…", FontSize=12 };
    private readonly TextBlock _count = new() { FontSize=12, Text="CONEXÕES" };
    private readonly TextBlock _feedback = new() { FontSize=12, TextWrapping=TextWrapping.Wrap };
    private readonly TextBlock _local = new() { FontSize=15, FontWeight=Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming=TextTrimming.CharacterEllipsis };
    private bool _loaded, _busy, _onlineOnly;
    public event Action? ManageRequested;
    public StudioConnections()
    {
        var panel=new StackPanel { Spacing=16, Margin=new Thickness(16,18,16,20) };
        var heading=new Grid(); heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
        heading.Children.Add(new TextBlock { Text="Conexões", FontSize=16, FontWeight=Microsoft.UI.Text.FontWeights.SemiBold });
        var manage=new Button { Content="+", Padding=new Thickness(9,3,9,3), MinWidth=32 };
        ToolTipService.SetToolTip(manage,"Gerenciar amigos e pedidos");
        manage.Click += (_,_)=>ManageRequested?.Invoke(); Grid.SetColumn(manage,1); heading.Children.Add(manage); panel.Children.Add(heading);
        _search.TextChanged += (_,_)=>Render(); panel.Children.Add(_search);
        var filter=new ToggleSwitch { OffContent="Todos os autorizados", OnContent="Somente online", FontSize=12 };
        filter.Toggled += (_,_)=>{_onlineOnly=filter.IsOn; Render();}; panel.Children.Add(filter);
        _count.Foreground=Muted; panel.Children.Add(_count); panel.Children.Add(_list);
        _feedback.Foreground=Muted; panel.Children.Add(_feedback);
        var all=new Button { Content="Amigos e permissões  →", HorizontalAlignment=HorizontalAlignment.Stretch };
        all.Click+=(_,_)=>ManageRequested?.Invoke(); panel.Children.Add(all);
        var note=new TextBlock { Text="Acesso autorizado · Tailscale", FontSize=11, Foreground=Muted, TextWrapping=TextWrapping.Wrap };
        ToolTipService.SetToolTip(note,"A disponibilidade vem do Tailscale. Somente computadores autorizados podem se conectar.");
        panel.Children.Add(note);
        Content=new ScrollViewer { Content=panel, HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled, VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
        Loaded+=OnLoaded; Unloaded+=OnUnloaded;
    }
    private static Brush Muted => (Brush)Application.Current.Resources["DesignMuted"];
    private void OnLoaded(object sender,RoutedEventArgs e)
    {
        if(_loaded)return; _loaded=true;
        FriendPresenceService.Instance.PresenceChanged+=Changed;
        FriendPresenceService.Instance.AddConsumer(FriendPresenceRefreshMode.Home);
        Render();
    }
    private void OnUnloaded(object sender,RoutedEventArgs e)=>Detach();
    private void Detach()
    {
        if(!_loaded)return; _loaded=false;
        FriendPresenceService.Instance.PresenceChanged-=Changed;
        FriendPresenceService.Instance.RemoveConsumer(FriendPresenceRefreshMode.Home);
    }
    private void Changed(object? sender,EventArgs e)=>DispatcherQueue.TryEnqueue(()=> { if(_loaded)Render(); });
    private void Render()
    {
        _local.Text=RemoteSessionService.Instance.GetSnapshot().LocalName;
        var friends=FriendPresenceService.Instance.GetSnapshot().Friends
            .Where(f=>!f.Blocked && (!_onlineOnly || f.Online) && f.DisplayName.Contains(_search.Text.Trim(),StringComparison.CurrentCultureIgnoreCase)).ToArray();
        _count.Text=$"AUTORIZADOS · {friends.Length}";
        _list.Children.Clear();
        foreach(var friend in friends)
        {
            var row=new Grid { ColumnSpacing=9 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(40) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            row.Children.Add(new Border { CornerRadius=new CornerRadius(9), Height=42, Background=(Brush)Application.Current.Resources["DesignSurface"],
                Child=new FontIcon { Glyph="\uE7F4", FontFamily=new FontFamily("Segoe Fluent Icons"), FontSize=20 } });
            var text=new StackPanel { Spacing=5, VerticalAlignment=VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text=friend.DisplayName, FontSize=12, FontWeight=Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming=TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text=friend.Online?"● Online":"● Offline", FontSize=11,
                Foreground=friend.Online?new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255,32,228,154)):Muted });
            SetColumn(text,1); row.Children.Add(text);
            var connect=new Button { Content="Conectar", FontSize=11, Padding=new Thickness(8,6,8,6), IsEnabled=friend.Online&&!_busy, VerticalAlignment=VerticalAlignment.Center };
            connect.Click+=async(_,_)=>await ConnectAsync(friend); SetColumn(connect,2); row.Children.Add(connect); _list.Children.Add(row);
        }
        if(friends.Length==0) _list.Children.Add(new TextBlock { Text="Nenhum computador neste filtro. Gerencie seus acessos em Amigos.", FontSize=13, TextWrapping=TextWrapping.Wrap, Foreground=Muted, Margin=new Thickness(0,14,0,14) });
    }
    private async System.Threading.Tasks.Task ConnectAsync(MultiverseFriend friend)
    {
        if(_busy)return; _busy=true; Render(); _feedback.Text="Conectando…";
        try
        {
            var result=await FriendConnectionService.Instance.ConnectAsync(friend,friend.LastKnownIp);
            if(_loaded)_feedback.Text=result.Message;
            if(result.Success && await RemoteStreamingRuntime.Instance.EnsureClientStreamingAsync() &&
                MultiverseSettingsService.Instance.GetAppPreferences().AutoOpenClientWindow)
                RemoteStreamingRuntime.Instance.ShowSessionWindow();
        }
        catch(Exception ex) { if(_loaded)_feedback.Text="Não foi possível conectar: "+ex.Message; }
        finally { _busy=false; if(_loaded)Render(); }
    }
    private static void SetColumn(FrameworkElement element,int column)=>Grid.SetColumn(element,column);
    public void Dispose() { Detach(); Loaded-=OnLoaded; Unloaded-=OnUnloaded; }
}
