using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Multiverse.Services;
namespace Multiverse.Views;
public sealed class UpdatePanel : UserControl {
    readonly UpdateCoordinator service=LevelingUpdates.Instance;
    readonly TextBlock status=new(){TextWrapping=TextWrapping.Wrap};
    readonly TextBlock versions=new(){TextWrapping=TextWrapping.Wrap};
    readonly TextBlock notes=new(){TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true};
    readonly TextBlock transfer=new(){TextWrapping=TextWrapping.Wrap};
    readonly ProgressBar progress=new(){Minimum=0,Maximum=100};
    readonly Button check=new(){Content="Verificar atualizações"};
    readonly Button update=new(){Content="Atualizar agora"};
    readonly Button later=new(){Content="Mais tarde"};
    public UpdatePanel() {
        var stack=new StackPanel{Spacing=12};
        stack.Children.Add(new TextBlock{Text="Atualizações",FontSize=20,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
        if (Program.Updated) stack.Children.Add(new TextBlock{Text="Atualização instalada com sucesso. O Leveling foi reiniciado.",TextWrapping=TextWrapping.Wrap});
        foreach(var e in new UIElement[]{versions,status,notes,progress,transfer}) stack.Children.Add(e);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};
        foreach(var b in new[]{check,update,later}) buttons.Children.Add(b);
        stack.Children.Add(buttons);Content=stack;
        check.Click+=async(_,_)=>await service.CheckAsync();
        update.Click+=async(_,_)=>await service.UpdateAsync();
        later.Click+=(_,_)=>service.Later();
        Loaded+=(_,_)=>{service.Changed+=Changed;Render();};
        Unloaded+=(_,_)=>service.Changed-=Changed;
    }
    void Changed()=>DispatcherQueue.TryEnqueue(Render);
    void Render() {
        versions.Text="Atual: "+BuildInfo.DisplayVersion+(service.Offer is null?"":"  →  Nova: "+service.Offer.Version);
        status.Text=service.Status;notes.Text=service.Offer?.Notes ?? "";
        notes.Visibility=string.IsNullOrWhiteSpace(notes.Text)?Visibility.Collapsed:Visibility.Visible;
        transfer.Visibility=service.Offer is null?Visibility.Collapsed:Visibility.Visible;
        progress.Value=service.Percent;
        progress.Visibility=service.Busy && service.Offer is not null?Visibility.Visible:Visibility.Collapsed;
        transfer.Text=service.Offer is null?"":service.Busy ? $"{service.Percent}% • ~{service.BytesPerSecond/1048576:0.0} MB/s • {service.Offer.Size/1048576.0:0.0} MB" : $"Download: {service.Offer.Size/1048576.0:0.0} MB";
        check.IsEnabled=!service.Busy;update.IsEnabled=!service.Busy && service.Offer is not null;
        later.IsEnabled=!service.Busy && service.Offer is not null;
    }
}
