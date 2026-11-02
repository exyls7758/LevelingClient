using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace Multiverse.Services;

internal sealed class WindowsDesktopInputSink : IDesktopInputSink
{
    [StructLayout(LayoutKind.Explicit, Size=40)]
    internal struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public int Dx;
        [FieldOffset(12)] public int Dy;
        [FieldOffset(16)] public uint MouseData;
        [FieldOffset(20)] public uint MouseFlags;
        [FieldOffset(8)] public ushort Vk;
        [FieldOffset(10)] public ushort Scan;
        [FieldOffset(12)] public uint KeyFlags;
    }
    public void Key(int vk,bool down)
    {
        uint scan=MapVirtualKey((uint)vk,4);
        bool extended=(scan & 0xff00)!=0;
        var input=new Input {Type=1, Scan=(ushort)(scan&0xff), Vk=scan==0?(ushort)vk:(ushort)0,
            KeyFlags=(scan==0?0u:8u)|(extended?1u:0u)|(down?0u:2u)};
        Send(input);
    }
    public void Button(int code,bool down) => Send(new Input {Type=0,
        MouseFlags=code switch {1=>down?2u:4u,2=>down?8u:16u,3=>down?32u:64u,_=>down?128u:256u},
        MouseData=code>=4?(uint)(code-3):0});
    public void Move(int dx,int dy) => Send(new Input {Type=0,Dx=dx,Dy=dy,MouseFlags=1|0x2000});
    public void Wheel(int delta) => Send(new Input {Type=0,MouseFlags=0x800,MouseData=unchecked((uint)delta)});
    private static void Send(Input input)
    {
        if (SendInput(1,new[]{input},Marshal.SizeOf<Input>())!=1)
            throw new Win32Exception(Marshal.GetLastWin32Error(),"O Windows não aceitou a entrada remota. Verifique o nível de permissão do jogo e do Leveling.");
    }
    [DllImport("user32.dll",SetLastError=true)] private static extern uint SendInput(uint count,Input[] inputs,int size);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code,uint mapType);
}
