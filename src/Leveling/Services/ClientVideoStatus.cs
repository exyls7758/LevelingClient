namespace Multiverse.Services;

// Presentation status only. Does not request recovery or touch video resources.
internal sealed class ClientVideoStatus
{
    private int _session = -1;
    private long _stream = -1, _presented, _lastPresent;
    private long _packets, _lastPacket, _frames, _lastFrame;
    internal string Observe(long now, int session, long stream, long packets,
        long frames, long decoded, long presented, bool waitingForReference)
    {
        if (_session != session || _stream != stream || presented < _presented || now < _lastPresent)
        {
            _session = session; _stream = stream; _presented = 0; _lastPresent = now;
            _packets = packets; _frames = frames; _lastPacket = _lastFrame = now;
        }
        if (packets != _packets) { _packets = packets; _lastPacket = now; }
        if (frames != _frames) { _frames = frames; _lastFrame = now; }
        if (presented > _presented) { _presented = presented; _lastPresent = now; return ""; }
        if (presented > 0 && now - _lastPresent < 2000) return "";
        if (stream <= 0) return "Aguardando o host iniciar a transmissão.";
        if (packets == 0) return "Conectado. Aguardando pacotes de vídeo do host…\nSe não iniciar, consulte o diagnóstico de rede nos dois PCs.";
        if (now - _lastPacket >= 2000) return "Vídeo interrompido. Aguardando novos pacotes do host…";
        if (frames == 0) return "Recebendo pacotes. Aguardando um quadro completo…";
        if (waitingForReference) return "Recuperando a imagem. Aguardando um quadro-chave do host…";
        if (now - _lastFrame >= 2000) return "Recebendo pacotes. Aguardando um quadro completo…";
        if (decoded == 0) return "Vídeo recebido. Preparando a decodificação…";
        return "Vídeo recebido. Aguardando a exibição de novos quadros…";
    }
}
