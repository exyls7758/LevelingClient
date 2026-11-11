# Leveling 11.75.1-rc.9

- Uma espera temporária pelo decoder durante ajuste da janela não interrompe imediatamente o transmissor. O cliente preserva a superfície e tenta novamente por até três segundos.
- Falhas de apresentação só são comunicadas como definitivas ao host após as tentativas locais limitadas de recuperação.
- Liberação da superfície tenta novamente de forma assíncrona quando o decoder ainda está ocupado, sem liberar recursos ou trocar de sessão prematuramente.
- Runtime C++ incorporado ao núcleo para evitar a dependência externa das DLLs de runtime na instalação portátil.
- Diagnósticos de inicialização passam a registrar a etapa e o status nativo numericamente.

Não altera perfis, bitrate, FEC, pacing ou a prioridade NVDEC/NVENC em NVIDIA. Os testes locais não equivalem à validação entre dois computadores ou no notebook Intel.
