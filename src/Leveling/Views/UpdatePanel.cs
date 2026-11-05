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
    readonly Button previous=new(){Content="Versões anteriores"};
    readonly ComboBox previousVersions=new(){PlaceholderText="Escolher versão anterior",DisplayMemberPath="Version",MinWidth=220};
    readonly Button restore=new(){Content="Restaurar versão"};
    bool confirming;
    public UpdatePanel() {
        var stack=new StackPanel{Spacing=12};
        stack.Children.Add(new TextBlock{Text="Atualizações",FontSize=20,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
        if (Program.Updated) stack.Children.Add(new TextBlock{Text="Atualização instalada com sucesso. O Leveling foi reiniciado.",TextWrapping=TextWrapping.Wrap});
        foreach(var e in new UIElement[]{versions,status,notes,progress,transfer}) stack.Children.Add(e);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};
        foreach(var b in new[]{check,update,later}) buttons.Children.Add(b);
        stack.Children.Add(buttons);Content=stack;
        stack.Children.Add(new TextBlock{Text="Se uma atualização apresentar problemas, reinstale uma versão anterior publicada. O aplicativo será reiniciado. Correções mais recentes também serão removidas.",TextWrapping=TextWrapping.Wrap});
        var recovery=new StackPanel{Spacing=8};
        recovery.Children.Add(previous);recovery.Children.Add(previousVersions);recovery.Children.Add(restore);
        stack.Children.Add(recovery);
        check.Click+=async(_,_)=>await service.CheckAsync();
        update.Click+=async(_,_)=>{if(service.IsRollback)await ConfirmRestoreAsync();else await service.UpdateAsync();};
        later.Click+=(_,_)=>service.Later();
        previous.Click+=async(_,_)=>await service.LoadPreviousAsync();
        previousVersions.SelectionChanged+=(_,_)=>restore.IsEnabled=!service.Busy && previousVersions.SelectedItem is ReleaseOffer;
        restore.Click+=async(_,_)=>{
            if(confirming)return;
            if(previousVersions.SelectedItem is ReleaseOffer selected && await service.SelectPreviousAsync(selected.Version))
                await ConfirmRestoreAsync();
        };
        Loaded+=(_,_)=>{service.Changed+=Changed;Render();};
        Unloaded+=(_,_)=>service.Changed-=Changed;
    }
    void Changed()=>DispatcherQueue.TryEnqueue(Render);
    async System.Threading.Tasks.Task ConfirmRestoreAsync() {
        var offer=service.Offer;
        if(offer is null || service.Busy || confirming)return;
        confirming=true;
        try {
        var dialog=new ContentDialog{XamlRoot=XamlRoot,Title="Restaurar "+offer.Version+"?",
            Content="O Leveling vai baixar esta versão, verificar o pacote e reiniciar. Preferências e amizades permanecem no PC. A versão anterior pode ter limitações que já foram corrigidas.\n\n"+offer.Notes,
            PrimaryButtonText="Restaurar e reiniciar",CloseButtonText="Cancelar",DefaultButton=ContentDialogButton.Close};
        if(await dialog.ShowAsync()==ContentDialogResult.Primary && service.Offer==offer && service.IsRollback)
            await service.UpdateAsync();
        } finally {confirming=false;}
    }
    void Render() {
        versions.Text="Atual: "+BuildInfo.DisplayVersion+(service.Offer is null?"":"  →  "+(service.IsRollback?"Restaurar: ":"Nova: ")+service.Offer.Version);
        status.Text=service.Status;notes.Text=service.Offer?.Notes ?? "";
        notes.Visibility=string.IsNullOrWhiteSpace(notes.Text)?Visibility.Collapsed:Visibility.Visible;
        transfer.Visibility=service.Offer is null?Visibility.Collapsed:Visibility.Visible;
        progress.Value=service.Percent;
        progress.Visibility=service.Busy && service.Offer is not null?Visibility.Visible:Visibility.Collapsed;
        transfer.Text=service.Offer is null?"":service.Busy ? $"{service.Percent}% • ~{service.BytesPerSecond/1048576:0.0} MB/s • {service.Offer.Size/1048576.0:0.0} MB" : $"Download: {service.Offer.Size/1048576.0:0.0} MB";
        check.IsEnabled=!service.Busy;update.IsEnabled=!service.Busy && service.Offer is not null;
        later.IsEnabled=!service.Busy && service.Offer is not null;
        update.Content=service.IsRollback?"Restaurar e reiniciar":"Atualizar agora";
        previous.IsEnabled=!service.Busy;previousVersions.IsEnabled=!service.Busy;
        if(!ReferenceEquals(previousVersions.ItemsSource,service.Previous))previousVersions.ItemsSource=service.Previous;
        previousVersions.Visibility=service.Previous.Count==0?Visibility.Collapsed:Visibility.Visible;
        restore.Visibility=previousVersions.Visibility;
        restore.IsEnabled=!service.Busy && previousVersions.SelectedItem is ReleaseOffer;
    }
}
