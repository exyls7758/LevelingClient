using System;
using System.Collections.Generic;
namespace Multiverse.Services;

public sealed class DesktopInputReceiver
{
    private readonly IDesktopInputSink _sink;
    private readonly HashSet<int> _keys = new(), _buttons = new();
    private long _epoch, _edgeSequence, _moveSequence, _lastAt, _x, _y, _wheel;
    private bool _closed = true;
    public DesktopInputReceiver(IDesktopInputSink sink) => _sink = sink;
    public bool Active => !_closed;
    public bool Apply(DesktopInputPacket p, long nowMs)
    {
        Tick(nowMs);
        if (p.Epoch <= 0 || p.Sequence <= 0 || p.Epoch < _epoch || (p.Epoch == _epoch && _closed)) return false;
        if (p.Kind is not ("key" or "button" or "move" or "heartbeat" or "reset")) return false;
        if (p.Kind == "key" && (p.Code < 8 || p.Code > 254)) return false;
        if (p.Kind == "button" && (p.Code < 1 || p.Code > 5)) return false;
        // Cumulative totals remain wide enough for long sessions, but each delivered
        // movement is bounded. Malformed values cannot overflow subtraction.
        const long limit = 1L << 45;
        if (p.Kind == "move" && (p.X < -limit || p.X > limit || p.Y < -limit || p.Y > limit || p.Wheel < -limit || p.Wheel > limit)) return false;
        long oldX = p.Epoch > _epoch ? 0 : _x, oldY = p.Epoch > _epoch ? 0 : _y, oldW = p.Epoch > _epoch ? 0 : _wheel;
        if (p.Kind == "move" && (Math.Abs(p.X-oldX)>1000000 || Math.Abs(p.Y-oldY)>1000000 || Math.Abs(p.Wheel-oldW)>120000)) return false;
        if (p.Epoch > _epoch)
        {
            Reset(); _epoch = p.Epoch; _closed = false;
            _edgeSequence = _moveSequence = _x = _y = _wheel = 0;
        }
        if (p.Kind == "move")
        {
            if (p.Sequence <= _moveSequence) return false;
            int dx=(int)(p.X-_x), dy=(int)(p.Y-_y), dw=(int)(p.Wheel-_wheel);
            if (dx != 0 || dy != 0) _sink.Move(dx,dy);
            if (dw != 0) _sink.Wheel(dw);
            _x=p.X; _y=p.Y; _wheel=p.Wheel; _moveSequence=p.Sequence;
        }
        else
        {
            if (p.Sequence <= _edgeSequence) return false;
            _edgeSequence=p.Sequence;
            if (p.Kind == "reset") Reset();
            else if (p.Kind is "key" or "button")
            {
                var held=p.Kind=="key" ? _keys : _buttons;
                if (p.Down ? held.Add(p.Code) : held.Contains(p.Code))
                {
                    if (p.Kind=="key") _sink.Key(p.Code,p.Down); else _sink.Button(p.Code,p.Down);
                    if (!p.Down) held.Remove(p.Code);
                }
            }
        }
        _lastAt=nowMs;
        return true;
    }
    public void Tick(long nowMs)
    {
        if ((_closed && (_keys.Count!=0 || _buttons.Count!=0)) ||
            (!_closed && (nowMs < _lastAt || nowMs-_lastAt>=500))) Reset();
    }
    public void ResetSession()
    {
        // Preserve failed release ownership; never replace the receiver while
        // the OS may still hold input from the previous session.
        Reset(); _epoch=0; _edgeSequence=_moveSequence=_x=_y=_wheel=0;
    }
    public void Reset()
    {
        _closed=true;
        // Continue releasing the rest even if one native call fails. Retain failed
        // ownership so another Reset can retry rather than forgetting held keys.
        Exception? failure=null;
        foreach(int k in new List<int>(_keys)) try { _sink.Key(k,false); _keys.Remove(k); } catch(Exception ex) { failure=ex; }
        foreach(int b in new List<int>(_buttons)) try { _sink.Button(b,false); _buttons.Remove(b); } catch(Exception ex) { failure=ex; }
        if (failure is not null) throw failure;
    }
}
