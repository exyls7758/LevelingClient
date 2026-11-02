using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Multiverse.Services;
namespace Multiverse.Views;
public sealed partial class KeyboardMousePage : Page
{
    private readonly DesktopInputService _service=DesktopInputService.Instance;
    private KeyboardMouseProfile _profile=new();
    private bool _loading=true;
    private int _selected=65; private bool _mouse;
    private readonly List<Button> _keys=new();
    public KeyboardMousePage()
    {
        InitializeComponent();BuildKeyboard();Loaded+=OnLoaded;Unloaded+=(_,_)=>_service.Changed-=ServiceChanged;
    }
    private void OnLoaded(object sender,RoutedEventArgs e) {_service.Changed+=ServiceChanged;Refresh();Select(65,false);}
    private void ServiceChanged(object? sender,EventArgs e)=>DispatcherQueue.TryEnqueue(()=>{StatusText.Text=_service.Status;_loading=true;ModeBox.SelectedIndex=(int)_service.Mode;HostToggle.IsOn=_service.HostAllowed;_loading=false;});
    private void Refresh()
    {
        _loading=true;_profile=_service.Profile;ModeBox.SelectedIndex=(int)_service.Mode;HostToggle.IsOn=_service.HostAllowed;
        SensitivitySlider.Value=_profile.Sensitivity;SensitivityText.Text=$"Sensibilidade do mouse · {_profile.Sensitivity:F2}×";InvertBox.IsChecked=_profile.InvertY;
        CaptureKeyBox.Items.Clear();GamepadKeyBox.Items.Clear();for(int k=117;k<=121;k++){CaptureKeyBox.Items.Add(new ComboBoxItem {Content=Label(k),Tag=k});GamepadKeyBox.Items.Add(new ComboBoxItem {Content=Label(k),Tag=k});}
        CaptureKeyBox.SelectedIndex=_profile.CaptureShortcut-117;GamepadKeyBox.SelectedIndex=_profile.GamepadShortcut-117;StatusText.Text=_service.Status;_loading=false;UpdateLabels();
    }
    private static string Label(int k)=>k switch {8=>"Backspace",9=>"Tab",13=>"Enter",20=>"Caps",27=>"Esc",32=>"Espaço",33=>"PgUp",34=>"PgDn",35=>"End",36=>"Home",37=>"←",38=>"↑",39=>"→",40=>"↓",45=>"Insert",46=>"Delete",160=>"Shift E",161=>"Shift D",162=>"Ctrl E",163=>"Ctrl D",164=>"Alt E",165=>"Alt D",186=>"; / Ç",187=>"=",188=>",",189=>"−",190=>".",191=>"/",192=>"`",219=>"[",220=>"\\",221=>"]",222=>"'",226=>"Tecla ISO / ABNT2",19=>"Pause",44=>"PrtSc",91=>"Win",92=>"Win D",93=>"Menu",144=>"NumLock",145=>"Scroll",>=96 and <=105=>"Num "+(k-96),106=>"Num ×",107=>"Num +",108=>"Num sep.",109=>"Num −",110=>"Num .",111=>"Num /",>=112 and <=123=>"F"+(k-111),>=48 and <=90=>((char)k).ToString(),_=>"VK "+k};
    private void BuildKeyboard()
    {
        // Quarter-key units keep every row on the same physical grid.
        var board=new Grid {ColumnSpacing=0,RowSpacing=5};
        for(int i=0;i<74;i++)board.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(1,GridUnitType.Star)});
        for(int i=0;i<6;i++)board.RowDefinitions.Add(new RowDefinition {Height=new GridLength(i==0?42:36)});
        void Key(int vk,int row,int column,int span=4)=>AddDiagramKey(board,vk,row,column,span);
        Key(27,0,0);
        for(int i=0;i<12;i++)Key(112+i,0,8+i*4+(i/4)*2);
        int[] numbers={192,49,50,51,52,53,54,55,56,57,48,189,187};
        for(int i=0;i<numbers.Length;i++)Key(numbers[i],1,i*4);
        Key(8,1,52,8);
        Key(9,2,0,6);int[] top={81,87,69,82,84,89,85,73,79,80,219,221};
        for(int i=0;i<top.Length;i++)Key(top[i],2,6+i*4);
        Key(220,2,54,6);
        Key(20,3,0,7);int[] home={65,83,68,70,71,72,74,75,76,186,222};
        for(int i=0;i<home.Length;i++)Key(home[i],3,7+i*4);
        Key(13,3,51,9);
        Key(160,4,0,9);int[] bottom={90,88,67,86,66,78,77,188,190,191};
        for(int i=0;i<bottom.Length;i++)Key(bottom[i],4,9+i*4);
        Key(161,4,49,11);
        Key(162,5,0,5);Key(91,5,5,5);Key(164,5,10,5);Key(32,5,15,25);
        Key(165,5,40,5);Key(92,5,45,5);Key(93,5,50,5);Key(163,5,55,5);
        int[][] navigation={new[]{44,145,19},new[]{45,36,33},new[]{46,35,34}};
        for(int r=0;r<3;r++)for(int c=0;c<3;c++)Key(navigation[r][c],r,62+c*4);
        Key(38,4,66);Key(37,5,62);Key(40,5,66);Key(39,5,70);
        KeyboardRows.Children.Add(board);
        var numpad=new Grid {Width=232,HorizontalAlignment=HorizontalAlignment.Left,RowSpacing=5};
        for(int i=0;i<4;i++)numpad.ColumnDefinitions.Add(new ColumnDefinition());
        for(int i=0;i<5;i++)numpad.RowDefinitions.Add(new RowDefinition {Height=new GridLength(36)});
        int[][] numrows={new[]{144,111,106,109},new[]{103,104,105},new[]{100,101,102},new[]{97,98,99}};
        for(int r=0;r<numrows.Length;r++)for(int c=0;c<numrows[r].Length;c++)AddDiagramKey(numpad,numrows[r][c],r,c,1);
        AddDiagramKey(numpad,107,1,3,1,2);AddDiagramKey(numpad,13,3,3,1,2);
        AddDiagramKey(numpad,96,4,0,2);AddDiagramKey(numpad,110,4,2,1);
        ExtraKeys.Children.Add(numpad);
        var iso=new Button {Content="Tecla adicional ISO / ABNT2  ·  \\\\ |",Tag=226,HorizontalAlignment=HorizontalAlignment.Left};
        iso.Click+=(_,_)=>Select(226,false);_keys.Add(iso);ExtraKeys.Children.Add(iso);
    }
    private void AddDiagramKey(Grid board,int key,int row,int column,int span,int rowSpan=1)
    {
        string label=key is 160 or 161?"Shift":key is 162 or 163?"Ctrl":key is 164 or 165?"Alt":key is 91 or 92?"Win":key>=96&&key<=105?(key-96).ToString():key==144?"Num":key==107?"+":key==109?"−":key==106?"×":key==111?"/":key==110?".":Label(key);
        var button=new Button {Content=label,Tag=key,HorizontalAlignment=HorizontalAlignment.Stretch,VerticalAlignment=VerticalAlignment.Stretch,Margin=new Thickness(2,0,2,row==0?6:0),Padding=new Thickness(1),FontSize=10,CornerRadius=new CornerRadius(5)};
        button.Click+=(_,_)=>Select(key,false);InterfaceMotion.SetEnabled(button,true);
        Grid.SetRow(button,row);Grid.SetColumn(button,column);Grid.SetColumnSpan(button,span);Grid.SetRowSpan(button,rowSpan);
        _keys.Add(button);board.Children.Add(button);
    }
    private void UpdateLabels()
    {
        foreach(var b in _keys)
        {
            int k=(int)b.Tag;bool selected=!_mouse&&_selected==k;
            b.Background=(Brush)Application.Current.Resources[selected?"DesignAccent":"DesignField"];
            b.Foreground=new SolidColorBrush(selected?Microsoft.UI.Colors.White:Windows.UI.Color.FromArgb(255,196,205,215));
            bool reserved=k==_profile.CaptureShortcut||k==_profile.GamepadShortcut;b.IsEnabled=!reserved;
            ToolTipService.SetToolTip(b,reserved?"Atalho do Leveling • altere em Resposta e atalhos":Label(k)+" → "+Label(_profile.KeyMap.GetValueOrDefault(k,k)));
        }
        foreach(var b in new[]{MouseLeft,MouseRight,MouseMiddle,MouseSide1,MouseSide2})
            b.Background=_mouse&&_selected==int.Parse((string)b.Tag)?(Brush)Application.Current.Resources["DesignAccent"]:new SolidColorBrush(Windows.UI.Color.FromArgb(12,180,190,201));
    }
    private void PageSizeChanged(object sender,SizeChangedEventArgs e)
    {
        if(HostCard is null||MouseDiagram is null||InvertBox is null)return;
        bool narrow=e.NewSize.Width<930;
        Grid.SetRow(HostCard,narrow?1:0);Grid.SetColumn(HostCard,narrow?0:1);Grid.SetColumnSpan(HostCard,narrow?2:1);
        Grid.SetColumnSpan((FrameworkElement)ModeGrid.Children[0],narrow?2:1);
        Grid.SetRow(MouseDiagram,narrow?1:0);Grid.SetColumn(MouseDiagram,narrow?0:1);Grid.SetColumnSpan(MouseDiagram,narrow?2:1);
        Grid.SetColumnSpan((FrameworkElement)DiagramGrid.Children[0],narrow?2:1);
        bool compact=e.NewSize.Width<650;
        Grid.SetRow(RestoreKeyButton,compact?1:0);Grid.SetColumn(RestoreKeyButton,compact?1:2);
        Grid.SetColumnSpan((FrameworkElement)MappingGrid.Children[1],compact?2:1);
        Grid.SetRow(InvertBox,compact?1:0);Grid.SetColumn(InvertBox,compact?0:2);Grid.SetColumnSpan(InvertBox,compact?3:1);
    }
    private void Select(int code,bool mouse)
    {
        _selected=code;_mouse=mouse;_loading=true;DestinationBox.Items.Clear();
        var values=new List<int>();if(mouse){for(int i=1;i<=5;i++)values.Add(i);}else{for(int i=8;i<=254;i++)if(i is 8 or 9 or 13 or 19 or 20 or 27 or 32 or >=33 and <=40 or 44 or 45 or 46 or >=48 and <=57 or >=65 and <=93 or >=96 and <=123 or 144 or 145 or >=160 and <=165 or >=186 and <=192 or >=219 and <=222 or 226)values.Add(i);}
        int target=(mouse?_profile.MouseMap:_profile.KeyMap).GetValueOrDefault(code,code);
        foreach(int key in values){var item=new ComboBoxItem {Content=mouse?MouseLabel(key):Label(key),Tag=key};DestinationBox.Items.Add(item);if(key==target)DestinationBox.SelectedItem=item;}
        SelectedText.Text=(mouse?MouseLabel(code):Label(code))+" → "+(mouse?MouseLabel(target):Label(target));SelectedBadge.Text=mouse?"M"+code:code==226?"ISO":Label(code);_loading=false;UpdateLabels();
    }
    private static string MouseLabel(int b)=>b switch {1=>"Mouse esquerdo",2=>"Mouse direito",3=>"Clique da roda",4=>"Mouse X1",_=>"Mouse X2"};
    private void Save(){try{_service.SaveProfile(_profile);UpdateLabels();}catch(Exception ex){StatusText.Text=ex.Message;}}
    private void ModeChanged(object sender,SelectionChangedEventArgs e){if(!_loading && ModeBox.SelectedIndex>=0)_service.SetMode((DesktopInputMode)ModeBox.SelectedIndex);}
    private void HostChanged(object sender,RoutedEventArgs e){if(!_loading)_service.SetHostAllowed(HostToggle.IsOn);}
    private void ReleaseClicked(object sender,RoutedEventArgs e)=>_service.ReleaseTurn();
    private async void PlayClicked(object sender,RoutedEventArgs e)
    {
        try { await RemoteStreamingRuntime.Instance.PlayWithKeyboardMouseAsync(); }
        catch(Exception ex){StatusText.Text=ex.Message;}
    }
    private void GamepadClicked(object sender,RoutedEventArgs e)=>_service.SetMode(DesktopInputMode.Gamepad);
    private void MouseSelected(object sender,RoutedEventArgs e)=>Select(int.Parse((string)((Button)sender).Tag),true);
    private void DestinationChanged(object sender,SelectionChangedEventArgs e)
    {
        if(_loading||DestinationBox.SelectedItem is not ComboBoxItem item)return;
        var map=_mouse?_profile.MouseMap:_profile.KeyMap;int target=(int)item.Tag;
        if(target==_selected)map.Remove(_selected);else map[_selected]=target;
        Save();
        // Keep the open popup's item collection intact during SelectionChanged.
        // Rebuilding it here destroys WinUI's selected container (0x8000FFFF).
        SelectedText.Text=(_mouse?MouseLabel(_selected):Label(_selected))+" → "+(_mouse?MouseLabel(target):Label(target));
    }
    private void RestoreSelected(object sender,RoutedEventArgs e){(_mouse?_profile.MouseMap:_profile.KeyMap).Remove(_selected);Save();Select(_selected,_mouse);}
    private void SensitivityChanged(object sender,RangeBaseValueChangedEventArgs e){if(_loading)return;_profile.Sensitivity=e.NewValue;SensitivityText.Text=$"Sensibilidade do mouse · {e.NewValue:F2}×";Save();}
    private void InvertChanged(object sender,RoutedEventArgs e){if(!_loading){_profile.InvertY=InvertBox.IsChecked==true;Save();}}
    private void ShortcutChanged(object sender,SelectionChangedEventArgs e)
    {if(_loading||CaptureKeyBox.SelectedItem is not ComboBoxItem a||GamepadKeyBox.SelectedItem is not ComboBoxItem b)return;var candidate=_profile.Clone();candidate.CaptureShortcut=(int)a.Tag;candidate.GamepadShortcut=(int)b.Tag;try{candidate.Validate();_profile=candidate;Save();}catch(Exception ex){StatusText.Text=ex.Message;}}
    private void RestoreAll(object sender,RoutedEventArgs e){_service.SaveProfile(new());Refresh();Select(65,false);}
}
