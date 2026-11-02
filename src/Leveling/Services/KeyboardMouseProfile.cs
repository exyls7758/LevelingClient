using System;
using System.Collections.Generic;
using System.Linq;
namespace Multiverse.Services;
public enum DesktopInputMode { Disabled, Gamepad, KeyboardMouse }
public sealed class KeyboardMouseProfile
{
    public Dictionary<int,int> KeyMap {get;set;} = new();
    public Dictionary<int,int> MouseMap {get;set;} = new();
    public double Sensitivity {get;set;}=1;
    public bool InvertY {get;set;}
    public int CaptureShortcut {get;set;}=119;
    public int GamepadShortcut {get;set;}=120;
    public KeyboardMouseProfile Clone() => new() {KeyMap=new(KeyMap),MouseMap=new(MouseMap),Sensitivity=Sensitivity,InvertY=InvertY,CaptureShortcut=CaptureShortcut,GamepadShortcut=GamepadShortcut};
    public void Validate()
    {
        if (!double.IsFinite(Sensitivity) || Sensitivity<.1 || Sensitivity>3) throw new ArgumentException("Sensibilidade deve ficar entre 0,1 e 3.");
        if (CaptureShortcut<117 || CaptureShortcut>121 || GamepadShortcut<117 || GamepadShortcut>121 || CaptureShortcut==GamepadShortcut) throw new ArgumentException("Escolha dois atalhos diferentes entre F6 e F10.");
        if (KeyMap is null || MouseMap is null || KeyMap.Count>247 || MouseMap.Count>5 || KeyMap.Any(x=>x.Key<8||x.Key>254||x.Value<8||x.Value>254) || MouseMap.Any(x=>x.Key<1||x.Key>5||x.Value<1||x.Value>5)) throw new ArgumentException("Mapeamento inválido.");
    }
}
// Tracks physical sources separately: mapping A and B to W must keep W pressed
// until both sources are released. Profile changes only take effect after reset.
public sealed class DesktopInputMapper
{
    private readonly Dictionary<int,int> _keys=new(), _buttons=new();
    private readonly KeyboardMouseProfile _profile;
    private double _fractionX,_fractionY;
    public DesktopInputMapper(KeyboardMouseProfile profile) { profile.Validate(); _profile=profile.Clone(); }
    public (int Code,bool Down)? Key(int source,bool down) => Edge(_keys,_profile.KeyMap,source,down);
    public (int Code,bool Down)? Button(int source,bool down) => Edge(_buttons,_profile.MouseMap,source,down);
    private static (int,bool)? Edge(Dictionary<int,int> held,Dictionary<int,int> map,int source,bool down)
    {
        if (down)
        {
            if (held.ContainsKey(source)) return null;
            int target=map.TryGetValue(source,out int v)?v:source;
            bool already=held.ContainsValue(target); held[source]=target;
            return already?null:(target,true);
        }
        if (!held.Remove(source,out int old) || held.ContainsValue(old)) return null;
        return (old,false);
    }
    public (int X,int Y) Move(int dx,int dy)
    {
        _fractionX+=dx*_profile.Sensitivity; _fractionY+=dy*_profile.Sensitivity*(_profile.InvertY?-1:1);
        int x=(int)_fractionX,y=(int)_fractionY;_fractionX-=x;_fractionY-=y;return(x,y);
    }
}
