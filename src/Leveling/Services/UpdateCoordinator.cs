using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
namespace Multiverse.Services;
public record ReleaseOffer(string Version, string Notes, long Size);
public interface IUpdateBackend {
    bool Available { get; }
    Task<ReleaseOffer?> CheckAsync();
    Task DownloadAsync(Action<int> progress, CancellationToken token);
    void Apply();
}
public sealed class UpdateCoordinator {
    readonly IUpdateBackend backend;
    readonly Func<bool> sessionBusy;
    readonly SemaphoreSlim gate = new(1,1);
    public event Action? Changed;
    public ReleaseOffer? Offer { get; private set; }
    public string Status { get; private set; } = "Verifique se há uma nova versão.";
    public bool Busy { get; private set; }
    public bool Ready { get; private set; }
    public bool Installing { get; private set; }
    public int Percent { get; private set; }
    public double BytesPerSecond { get; private set; }
    public bool Deferred { get; private set; }
    public UpdateCoordinator(IUpdateBackend backend, Func<bool> sessionBusy) { this.backend=backend; this.sessionBusy=sessionBusy; }
    void Notify() => Changed?.Invoke();
    public void Later() { Deferred=true; Notify(); }
    public async Task CheckAsync() {
        if (!await gate.WaitAsync(0)) return;
        try {
            Busy=true; Status="Verificando atualizações…"; Notify();
            if (!backend.Available) { Status="Esta cópia não tem o instalador de atualizações. Use a distribuição Setup ou Portable oficial."; return; }
            if (Ready) { Status="Atualização baixada. Pronta para instalar."; return; }
            var next=await backend.CheckAsync();
            if (next?.Version != Offer?.Version) Deferred=false;
            Offer=next;
            Status=next is null ? "Você está na versão mais recente." : "Nova atualização disponível!";
        } catch (Exception) { Status="Não foi possível verificar agora. Confira a conexão e tente novamente."; }
        finally { Busy=false; gate.Release(); Notify(); }
    }
    public async Task UpdateAsync(CancellationToken token=default) {
        if (!await gate.WaitAsync(0)) return;
        try {
            if (Offer is null) return;
            if (sessionBusy()) { Status="Encerre a sessão e a transmissão antes de atualizar. Nada foi interrompido."; return; }
            Busy=true; Deferred=false; Notify();
            if (!Ready) {
                Percent=0; Status="Baixando atualização…"; var elapsed=Stopwatch.StartNew();
                await backend.DownloadAsync(p => {
                    Percent=Math.Clamp(p,0,100);
                    BytesPerSecond=elapsed.Elapsed.TotalSeconds>0 ? Offer.Size*(Percent/100.0)/elapsed.Elapsed.TotalSeconds : 0;
                    Notify();
                },token);
                Ready=true; Percent=100;
            }
            // A session may have started while the package was downloading.
            if (sessionBusy()) { Status="Download concluído. Encerre a sessão e clique em Atualizar agora para instalar."; return; }
            Installing=true; Status="Instalando e reiniciando…"; Notify();
            backend.Apply();
        } catch (OperationCanceledException) { Status="Download cancelado. Você pode tentar novamente."; }
          catch (Exception) { Status="Não foi possível concluir a atualização. A versão atual foi mantida; tente novamente."; }
        finally { Installing=false; Busy=false; gate.Release(); Notify(); }
    }
}
