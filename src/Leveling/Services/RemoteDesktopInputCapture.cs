using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
namespace Multiverse.Services;

// Scoped to the visible remote window; no global keyboard hooks or input logging.
internal sealed class RemoteDesktopInputCapture : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly DesktopInputService _input=DesktopInputService.Instance;
    private readonly SubclassProc _proc;
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _focusTimer;
    private readonly HashSet<int> _down=new();
    private bool _disposed,_clipped;
    public bool Capturing => _input.IsCapturing;
    public RemoteDesktopInputCapture(IntPtr hwnd)
    {
        _hwnd=hwnd;_dispatcher=DispatcherQueue.GetForCurrentThread();_proc=WindowProc;
        if(!SetWindowSubclass(hwnd,_proc,0x4c56494e,UIntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error());
        var devices=new[]{new Device {Page=1,Usage=2,Target=hwnd},new Device {Page=1,Usage=6,Target=hwnd}};
        if(!RegisterRawInputDevices(devices,2,(uint)Marshal.SizeOf<Device>())) {RemoveWindowSubclass(hwnd,_proc,0x4c56494e);throw new Win32Exception(Marshal.GetLastWin32Error());}
        _input.Changed+=Changed;
        _focusTimer=_dispatcher.CreateTimer();_focusTimer.Interval=TimeSpan.FromMilliseconds(100);_focusTimer.Tick+=FocusTick;_focusTimer.Start();
    }
    private void Changed(object? sender,EventArgs e) => _dispatcher.TryEnqueue(UpdateCapture);
    private void FocusTick(DispatcherQueueTimer sender,object args) => UpdateCapture();
    private void UpdateCapture()
    {
        if(_disposed)return;
        if(GetForegroundWindow()!=_hwnd){_input.Release();_down.Clear();}
        bool capture=_input.IsCapturing && GetForegroundWindow()==_hwnd;
        if(capture)
        {
            if(GetClientRect(_hwnd,out Rect rect))
            {
                var a=new Point {X=rect.Left,Y=rect.Top};var b=new Point {X=rect.Right,Y=rect.Bottom};
                if(!ClientToScreen(_hwnd,ref a)||!ClientToScreen(_hwnd,ref b))return;
                rect=new Rect {Left=a.X,Top=a.Y,Right=b.X,Bottom=b.Y};
                if(!ClipCursor(ref rect)){_input.CaptureUnavailable("não foi possível capturar o cursor");return;}
                _clipped=true;SetCursor(IntPtr.Zero);
            }
        }
        else if(_clipped){ClipCursor(IntPtr.Zero);_clipped=false;SetCursor(LoadCursor(IntPtr.Zero,(IntPtr)32512));}
    }
    private IntPtr WindowProc(IntPtr hwnd,uint msg,UIntPtr wparam,IntPtr lparam,UIntPtr id,UIntPtr data)
    {
        try
        {
            // WinUI can move focus to a child HWND without leaving the video window.
            // Only release when the top-level window is actually deactivated.
            if((msg==0x0008 && GetForegroundWindow()!=_hwnd) || (msg==0x0006 && (wparam.ToUInt64()&0xffff)==0)) {_input.Release();_down.Clear();UpdateCapture();}
            if(msg==0x0020 && _input.IsCapturing){SetCursor(IntPtr.Zero);return (IntPtr)1;}
            if(msg==0x00ff && GetForegroundWindow()==_hwnd) ReadRaw(lparam);
        }
        catch(Exception ex){_input.Release();StartupDiagnostics.RecordException("DESKTOP_INPUT_CAPTURE_ERROR",ex);}
        return DefSubclassProc(hwnd,msg,wparam,lparam);
    }
    private void ReadRaw(IntPtr handle)
    {
        uint size=0;uint header=(uint)(8+2*IntPtr.Size);
        if(GetRawInputData(handle,0x10000003,IntPtr.Zero,ref size,header)==uint.MaxValue || size<header || size>1024)return;
        IntPtr mem=Marshal.AllocHGlobal((int)size);
        try
        {
            if(GetRawInputData(handle,0x10000003,mem,ref size,header)!=size)return;
            int type=Marshal.ReadInt32(mem);IntPtr body=IntPtr.Add(mem,(int)header);
            if(type==1)
            {
                int vk=(ushort)Marshal.ReadInt16(body,6),flags=(ushort)Marshal.ReadInt16(body,2),scan=(ushort)Marshal.ReadInt16(body);
                if(vk==255)return;
                if(vk==16)vk=(int)MapVirtualKey((uint)scan,3);
                if(vk==17)vk=(flags&2)!=0?163:162;
                if(vk==18)vk=(flags&2)!=0?165:164;
                bool down=(flags&1)==0;bool first=down?_down.Add(vk):_down.Remove(vk);
                var shortcuts=_input.Shortcuts;
                bool ctrl=_down.Contains(162)||_down.Contains(163),alt=_down.Contains(164)||_down.Contains(165);
                if(down && first && vk==8 && ctrl && alt){_input.Release();return;}
                if(vk==shortcuts.Capture)
                {
                    if(down&&first){if(_input.IsCapturing)_input.Release();else _input.BeginCapture();}
                    return;
                }
                if(vk==shortcuts.Gamepad){if(down&&first)_input.SetMode(DesktopInputMode.Gamepad);return;}
                if(_input.IsCapturing) _input.Key(vk,down);
            }
            else if(type==0 && _input.IsCapturing)
            {
                int flags=(ushort)Marshal.ReadInt16(body,4);int wheel=(flags&0x400)!=0?(short)Marshal.ReadInt16(body,6):0;
                // RAWMOUSE absolute devices are not interpreted as relative game input.
                if((Marshal.ReadInt16(body)&1)==0)_input.Move(Marshal.ReadInt32(body,12),Marshal.ReadInt32(body,16),wheel);
                else if(wheel!=0)_input.Move(0,0,wheel);
                for(int button=1;button<=5;button++)
                {int bit=1<<((button-1)*2);if((flags&bit)!=0)_input.Button(button,true);if((flags&(bit<<1))!=0)_input.Button(button,false);}
            }
        }
        finally {Marshal.FreeHGlobal(mem);}
    }
    public void Dispose()
    {
        if(_disposed)return;_input.Release();_disposed=true;_focusTimer.Stop();_input.Changed-=Changed;
        if(_clipped){ClipCursor(IntPtr.Zero);SetCursor(LoadCursor(IntPtr.Zero,(IntPtr)32512));}
        RegisterRawInputDevices(new[]{new Device {Page=1,Usage=2,Flags=1},new Device {Page=1,Usage=6,Flags=1}},2,(uint)Marshal.SizeOf<Device>());
        RemoveWindowSubclass(_hwnd,_proc,0x4c56494e);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Device {public ushort Page,Usage;public uint Flags;public IntPtr Target;}
    [StructLayout(LayoutKind.Sequential)] private struct Rect {public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)] private struct Point {public int X,Y;}
    private delegate IntPtr SubclassProc(IntPtr hwnd,uint msg,UIntPtr wparam,IntPtr lparam,UIntPtr id,UIntPtr data);
    [DllImport("comctl32.dll",SetLastError=true)] private static extern bool SetWindowSubclass(IntPtr hwnd,SubclassProc callback,UIntPtr id,UIntPtr data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr hwnd,SubclassProc callback,UIntPtr id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd,uint msg,UIntPtr wparam,IntPtr lparam);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool RegisterRawInputDevices(Device[] devices,uint count,uint size);
    [DllImport("user32.dll",SetLastError=true)] private static extern uint GetRawInputData(IntPtr input,uint command,IntPtr data,ref uint size,uint header);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd,out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd,ref Point point);
    [DllImport("user32.dll")] private static extern bool ClipCursor(ref Rect rect);
    [DllImport("user32.dll")] private static extern bool ClipCursor(IntPtr rect);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr instance,IntPtr name);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code,uint mapType);
}
