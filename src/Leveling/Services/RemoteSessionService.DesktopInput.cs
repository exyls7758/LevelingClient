using System;
using System.Threading;
namespace Multiverse.Services;

internal sealed partial class RemoteSessionService
{
    internal event Action<DesktopInputPacket,int>? DesktopInputReceived;
    // Every message belongs to the existing authenticated session. Old binaries
    // ignore these optional message types; the caller requires a start ACK.
    internal bool TryPublishDesktopInput(DesktopInputPacket packet,int generation)
    {
        lock(_sync)
        {
            if (_sessionGeneration!=generation || _activeWriter is null || _sessionCancel?.IsCancellationRequested!=false) return false;
            var channel=packet.Kind=="move" ? _cursorOutbound : _controlOutbound;
            if (channel is null || !channel.Writer.TryWrite(new ControlEnvelope {Type="desktopinput",DesktopInput=packet})) return false;
            try { _controlOutboundSignal?.Release(); } catch(SemaphoreFullException){} catch(ObjectDisposedException){return false;}
            return true;
        }
    }
    private void HandleDesktopInput(ControlEnvelope message,int generation)
    {
        if(message.DesktopInput is null)return;
        lock(_sync) { if(generation!=_sessionGeneration || !_remoteInputAllowed)return; }
        DesktopInputReceived?.Invoke(message.DesktopInput,generation);
    }
}
