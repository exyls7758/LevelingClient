using System;
using System.Text.Json;
using System.Threading;
namespace Multiverse.Services;

internal sealed class DesktopInputService
{
    public static DesktopInputService Instance {get;}=new();
    private readonly object _gate=new();
    private readonly RemoteSessionService _session=RemoteSessionService.Instance;
    private readonly MultiverseSettingsService _settings=MultiverseSettingsService.Instance;
    private DesktopInputReceiver _receiver=new(new WindowsDesktopInputSink());
    private readonly Timer _timer;
    private KeyboardMouseProfile _profile=new();
    private DesktopInputMapper _mapper=new(new());
    private DesktopInputMode _mode=DesktopInputMode.Gamepad;
    private bool _hostAllowed=true,_capturing,_pending;
    private int _generation;
    private long _epoch,_hostEpoch,_lastHostEpoch,_sequence,_x,_y,_wheel,_lastMotionX,_lastMotionY,_lastWheel,_lastHeartbeat,_requestAt,_leaseAt;
    private string _status="Controle selecionado • F8 para teclado e mouse na janela remota";
    public event EventHandler? Changed;
    public DesktopInputMode Mode {get {lock(_gate)return _mode;}}
    public bool HostAllowed {get {lock(_gate)return _hostAllowed;}}
    public bool IsCapturing {get {lock(_gate)return _capturing;}}
    public string Status {get {lock(_gate)return _status;}}
    public KeyboardMouseProfile Profile {get {lock(_gate)return _profile.Clone();}}
    public (int Capture,int Gamepad) Shortcuts {get {lock(_gate)return (_profile.CaptureShortcut,_profile.GamepadShortcut);}}
    private DesktopInputService()
    {
        try { _profile=JsonSerializer.Deserialize<KeyboardMouseProfile>(_settings.GetString("Leveling.DesktopInput.Profile","{}"))??new();_profile.Validate(); }
        catch { _profile=new(); }
        _hostAllowed=_settings.GetBool("Leveling.DesktopInput.HostAllowed",true);
        _session.DesktopInputReceived+=Receive;
        _session.SessionChanged+=(_,_)=>SessionChanged();
        _timer=new Timer(_=>Tick(),null,100,100);
    }
    private void Notify() { Changed?.Invoke(this,EventArgs.Empty); }
    public void SaveProfile(KeyboardMouseProfile profile)
    {
        profile.Validate(); Release();
        lock(_gate) { _profile=profile.Clone();_settings.Set("Leveling.DesktopInput.Profile",JsonSerializer.Serialize(_profile)); }
        Notify();
    }
    public void SetHostAllowed(bool value)
    {
        lock(_gate) { _hostAllowed=value;_settings.Set("Leveling.DesktopInput.HostAllowed",value);if(!value)ResetReceiver(); }
        Notify();
    }
    public void SetMode(DesktopInputMode mode)
    {
        Release();
        lock(_gate) { _mode=mode;_status=mode switch {DesktopInputMode.KeyboardMouse=>"Teclado e mouse • pressione F8 na janela remota",DesktopInputMode.Gamepad=>"Controle selecionado",_=>"Entrada remota pausada"}; }
        Notify();
    }
    public void CaptureUnavailable(string detail)
    {
        Release();
        lock(_gate){_status="Teclado e mouse indisponíveis: "+detail;}
        Notify();
    }
    public void BeginCapture()
    {
        lock(_gate)
        {
            var s=_session.GetSnapshot();
            if(_capturing||_pending)return;
            if(!s.Connected||s.Role!=RemoteSessionRole.Client||!s.RemoteInputAllowed)
            { _status="Conecte-se a um host com permissão de input."; }
            else
            {
                _generation=s.SessionGeneration;_mode=DesktopInputMode.KeyboardMouse;
                _epoch=Math.Max(_epoch+1,DateTime.UtcNow.Ticks);_sequence=0;
                _x=_y=_wheel=_lastMotionX=_lastMotionY=_lastWheel=0;_mapper=new(_profile);
                _pending=true;_requestAt=Environment.TickCount64;_status="Confirmando teclado e mouse com o host…";
                if(!Send("start")) StopCore("Fila de controle indisponível. Tente novamente.");
            }
        }
        Notify();
    }
    public void Release()
    {
        bool changed=false;
        lock(_gate) { if(_capturing||_pending) { Send("reset");StopCore("Teclado e mouse liberados • F8 para retomar");changed=true; } }
        if(changed)Notify();
    }
    public void EmergencyStop()
    {
        Release();SetMode(DesktopInputMode.Disabled);
        lock(_gate) {ResetReceiver();_hostAllowed=false;_status="Entrada interrompida. Reative a permissão na aba Teclado e mouse.";}
        Notify();
    }
    public void ReleaseTurn()
    {
        Release();
        lock(_gate)
        {
            if(_hostEpoch!=0)Reply("released",_hostEpoch,_generation);
            ResetReceiver();
            _status="Entrada liberada. A permissão foi mantida para a próxima vez.";
        }
        Notify();
    }
    public void Key(int code,bool down)
    {
        lock(_gate)
        {
            if(!_capturing)return;
            var edge=_mapper.Key(code,down);
            if(edge is {} e && !Send("key",e.Code,e.Down)) StopCore("Fila cheia. Entrada liberada; pressione F8 para retomar.");
        }
    }
    public void Button(int code,bool down)
    {
        lock(_gate)
        {
            if(!_capturing)return;
            var edge=_mapper.Button(code,down);
            if(edge is {} e && !Send("button",e.Code,e.Down)) StopCore("Fila cheia. Entrada liberada; pressione F8 para retomar.");
        }
    }
    public void Move(int dx,int dy,int wheel)
    {
        lock(_gate) {if(!_capturing)return;var d=_mapper.Move(dx,dy);_x+=d.X;_y+=d.Y;_wheel+=wheel;}
    }
    private bool Send(string kind,int code=0,bool down=false) => _session.TryPublishDesktopInput(new DesktopInputPacket {Kind=kind,Epoch=_epoch,Sequence=++_sequence,Code=code,Down=down,X=_x,Y=_y,Wheel=_wheel},_generation);
    private void Reply(string kind,long epoch,int generation,string? reason=null) => _session.TryPublishDesktopInput(new DesktopInputPacket {Kind=kind,Epoch=epoch,Sequence=1,Reason=reason},generation);
    private static string Refusal(string? reason) => reason switch
    {
        "keyboard_permission"=>"No PC host, ative a permissão na aba Teclado e mouse. Depois tente novamente.",
        "master_permission"=>"No PC host, ative Aceitar input remoto na aba Controle. Depois tente novamente.",
        "stale_epoch"=>"Solicitação de entrada antiga. Libere e ative novamente na janela do vídeo.",
        "release_failed"=>"O Windows do host não conseguiu liberar a entrada anterior. Verifique as permissões do Leveling e do jogo.",
        "injection_failed"=>"O Windows do host recusou a entrada. Verifique o nível de permissão do Leveling e do jogo.",
        _=>"O host recusou a entrada. Confira as permissões de Controle e Teclado e mouse no PC host."
    };
    private void StopCore(string status) { _capturing=_pending=false;_status=status;_timer.Change(100,100); }
    private bool ResetReceiver()
    {
        _hostEpoch=0;
        try{_receiver.Reset();return true;}catch(Exception ex){_status=ex.Message;return false;}
    }
    private void SessionChanged()
    {
        lock(_gate)
        {
            var s=_session.GetSnapshot();
            if(!s.Connected||s.SessionGeneration!=_generation)
            {
                StopCore("Sessão alterada • entrada liberada");ResetReceiver();
                try{_receiver.ResetSession();}catch(Exception ex){_status=ex.Message;}
                _lastHostEpoch=0;_generation=s.SessionGeneration;
            }
            if(!s.RemoteInputAllowed) {StopCore("Input não autorizado nesta sessão");ResetReceiver();}
        }
        Notify();
    }
    private void Receive(DesktopInputPacket p,int generation)
    {
        bool notify=false;
        lock(_gate)
        {
            var s=_session.GetSnapshot();
            if(!s.Connected||s.SessionGeneration!=generation||!s.RemoteInputAllowed)return;
            _generation=generation;
            try
            {
                if(s.Role==RemoteSessionRole.Client)
                {
                    if(p.Epoch!=_epoch)return;
                    if(p.Kind=="accepted" && _pending && Environment.TickCount64-_requestAt<=1200)
                    {
                        _pending=false;_capturing=true;_lastHeartbeat=0;_leaseAt=Environment.TickCount64;
                        _status="Teclado e mouse ativos • Ctrl+Alt+Backspace libera";_timer.Change(8,8);notify=true;
                    }
                    else if(p.Kind=="alive" && _capturing) _leaseAt=Environment.TickCount64;
                    else if(p.Kind=="rejected") {StopCore(Refusal(p.Reason));notify=true;}
                    else if(p.Kind=="expired") {StopCore("Entrada liberada por silêncio de rede no host. Ative novamente na janela do vídeo.");notify=true;}
                    else if(p.Kind=="released") {StopCore("O host encerrou sua vez. Clique em Jogar com teclado e mouse para retomar.");notify=true;}
                }
                else if(s.Role==RemoteSessionRole.Host)
                {
                    if(p.Kind=="start")
                    {
                        string? refusal=!_hostAllowed?"keyboard_permission":p.Epoch<=_lastHostEpoch||p.Sequence<=0?"stale_epoch":null;
                        if(refusal is not null) {Reply("rejected",p.Epoch,generation,refusal);return;}
                        if(!ResetReceiver()){Reply("rejected",p.Epoch,generation,"release_failed");return;}
                        _hostEpoch=_lastHostEpoch=p.Epoch;
                        if(!_receiver.Apply(new DesktopInputPacket {Kind="heartbeat",Epoch=p.Epoch,Sequence=p.Sequence},Environment.TickCount64))
                        { _hostEpoch=0;Reply("rejected",p.Epoch,generation);return; }
                        Reply("accepted",p.Epoch,generation);_status="Teclado e mouse do cliente autorizados";notify=true;
                    }
                    else if(_hostAllowed && p.Epoch==_hostEpoch)
                    {
                        bool accepted=_receiver.Apply(p,Environment.TickCount64);
                        if(accepted && p.Kind=="heartbeat")Reply("alive",p.Epoch,generation);
                        if(!_receiver.Active) {Reply("expired",p.Epoch,generation);_hostEpoch=0;notify=true;}
                    }
                }
            }
            catch(Exception ex) {ResetReceiver();_status=ex.Message;Reply("rejected",p.Epoch,generation,"injection_failed");notify=true;}
        }
        if(notify)Notify();
    }
    private void Tick()
    {
        bool notify=false;
        try
        {
            lock(_gate)
            {
                var snapshot=_session.GetSnapshot();
                long now=Environment.TickCount64;
                if(!snapshot.Connected || snapshot.SessionGeneration!=_generation || !snapshot.RemoteInputAllowed)
                {if(_capturing||_pending){StopCore("Sessão encerrada • entrada liberada");notify=true;}ResetReceiver();}
                _receiver.Tick(now);
                if(_hostEpoch!=0 && !_receiver.Active) {Reply("expired",_hostEpoch,_generation);_hostEpoch=0;_status="Input remoto liberado por silêncio de rede";notify=true;}
                if(_pending && now-_requestAt>1200) {Send("reset");StopCore("Host sem confirmação. Use RC20 nos dois computadores e tente F8 novamente.");notify=true;}
                if(_capturing && now-_leaseAt>700) {Send("reset");StopCore("Confirmação do host expirou • input liberado");notify=true;}
                if(_capturing)
                {
                    bool ok=true;
                    if(_x!=_lastMotionX||_y!=_lastMotionY||_wheel!=_lastWheel)
                    {ok=Send("move");_lastMotionX=_x;_lastMotionY=_y;_lastWheel=_wheel;}
                    if(now-_lastHeartbeat>=100){ok &= Send("heartbeat");_lastHeartbeat=now;}
                    if(!ok){StopCore("Fila indisponível • input liberado");notify=true;}
                }
            }
        }
        catch(Exception ex) {lock(_gate){StopCore("Input interrompido: "+ex.Message);ResetReceiver();}notify=true;}
        if(notify)Notify();
    }
}
