using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Multiverse.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Multiverse.Views
{
    public sealed partial class ControllerPage
    {
        private enum ControllerDiagnosticStep
        {
            DetectController,
            StableRest,
            LeftStick,
            RightStick,
            LeftTrigger,
            RightTrigger,
            Result
        }

        private sealed class ControllerDisconnectedDuringDiagnosticException : Exception
        {
            public ControllerDisconnectedDuringDiagnosticException()
                : base("O controle foi desconectado durante o diagnóstico.")
            {
            }
        }

        private const int DiagnosticSampleDelayMs = 8;
        private const int StableRestRequiredMs = 2500;
        private const int DirectionTimeoutMs = 15000;
        private const int TriggerTimeoutMs = 15000;
        private const double StickDirectionThreshold = 0.50;
        private const double StickCenterGuard = 0.55;
        private const double StableDeltaThreshold = 0.035;
        private const double TriggerPressThreshold = 0.82;
        private const double TriggerReleaseThreshold = 0.08;

        private CancellationTokenSource? _diagnosticCts;
        private ControllerDiagnosticReport? _diagnosticReport;
        private GamepadTuningSnapshot? _diagnosticPreviousTuning;
        private bool _diagnosticRunning;
        private bool _diagnosticApplied;
        private bool _diagnosticSourceReliable = true;
        private bool _diagnosticUsedRawFallback;
        private string _diagnosticControllerIdentity = "";
        private LocalRawGamepadSnapshot? _diagnosticLatestRaw;
        private RawHidDiagnosticMapper? _diagnosticRawMapper;
        private readonly List<LocalRawGamepadSnapshot> _diagnosticRawRestSamples = new();
        private readonly List<LocalRawGamepadSnapshot> _diagnosticRawLeftSweepSamples = new();
        private readonly List<LocalRawGamepadSnapshot> _diagnosticRawRightSweepSamples = new();
        private readonly List<LocalRawGamepadSnapshot> _diagnosticRawTriggerSamples = new();
        private ControllerDiagnosticStep _diagnosticStep;

        private async void DiagnosticControllerButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_diagnosticRunning && _diagnosticApplied && _diagnosticReport is not null && _diagnosticPreviousTuning is not null)
            {
                LevelingInfoToolTip.IsSuspended = true;
                DiagnosticPanel.Visibility = Visibility.Visible;
                ShowDiagnosticReport(_diagnosticReport);
                ApplyDiagnosticButton.IsEnabled = false;
                RevertDiagnosticButton.IsEnabled = true;
                DiagnosticStatusTextBlock.Text = "Os ajustes medidos estão aplicados. Você pode REVERTER ou executar o assistente novamente.";
                return;
            }

            await RunControllerDiagnosticAsync();
        }

        private async void RetryDiagnosticButton_Click(object sender, RoutedEventArgs e)
        {
            if (_diagnosticApplied && _diagnosticPreviousTuning is not null)
            {
                _tuning.RestoreClientTuningSnapshot(_diagnosticPreviousTuning);
                _diagnosticApplied = false;
                _diagnosticPreviousTuning = null;
                DiagnosticControllerButton.Content = "DIAGNOSTICAR CONTROLE";
                LoadProfileToControls();
            }

            await RunControllerDiagnosticAsync();
        }

        private void ApplyDiagnosticButton_Click(object sender, RoutedEventArgs e)
        {
            if (_diagnosticRunning || _diagnosticReport is null || !CanUseClientDiagnosticRole())
                return;
            if (!_diagnosticSourceReliable)
            {
                DiagnosticStatusTextBlock.Text = "Aplicação automática bloqueada porque a fonte do diagnóstico não foi validada. Use REFAZER para identificar novamente o controle.";
                return;
            }
            if (!_diagnosticReport.HasActionableRecommendation)
            {
                DiagnosticStatusTextBlock.Text = "Nenhum componente teve medição suficiente para aplicação automática. Use REFAZER.";
                return;
            }

            try
            {
                _diagnosticPreviousTuning = _tuning.CaptureClientTuningSnapshot();
                _tuning.ApplyDiagnosticRecommendation(_diagnosticReport.Recommendation);
                _diagnosticApplied = true;
                DiagnosticControllerButton.Content = "VER RESULTADO";
                RevertDiagnosticButton.IsEnabled = true;
                ApplyDiagnosticButton.IsEnabled = false;
                DiagnosticStatusTextBlock.Text = "Somente os componentes validados foram alterados. Use REVERTER para restaurar exatamente o ajuste anterior.";
                LoadProfileToControls();
                SafeUpdateUi();
            }
            catch (Exception ex)
            {
                DiagnosticStatusTextBlock.Text = $"Não foi possível aplicar os ajustes: {ex.Message}";
            }
        }

        private void RevertDiagnosticButton_Click(object sender, RoutedEventArgs e)
        {
            if (_diagnosticRunning || !_diagnosticApplied || _diagnosticPreviousTuning is null || !CanUseClientDiagnosticRole())
                return;

            try
            {
                _tuning.RestoreClientTuningSnapshot(_diagnosticPreviousTuning);
                _diagnosticApplied = false;
                _diagnosticPreviousTuning = null;
                DiagnosticControllerButton.Content = "DIAGNOSTICAR CONTROLE";
                RevertDiagnosticButton.IsEnabled = false;
                ApplyDiagnosticButton.IsEnabled = _diagnosticReport is not null && _diagnosticReport.HasActionableRecommendation && _diagnosticSourceReliable;
                DiagnosticStatusTextBlock.Text = "Ajuste anterior restaurado exatamente como estava antes do diagnóstico.";
                LoadProfileToControls();
                SafeUpdateUi();
            }
            catch (Exception ex)
            {
                DiagnosticStatusTextBlock.Text = $"Não foi possível reverter: {ex.Message}";
            }
        }

        private void CancelDiagnosticButton_Click(object sender, RoutedEventArgs e) => CancelControllerDiagnostic();

        private void CancelControllerDiagnostic()
        {
            try { _diagnosticCts?.Cancel(); } catch { }
            _diagnosticRunning = false;
            _diagnosticLatestRaw = null;
            DiagnosticPanel.Visibility = Visibility.Collapsed;
            LevelingInfoToolTip.IsSuspended = false;
        }

        private bool CanUseClientDiagnosticRole()
        {
            RemoteSessionSnapshot session = _session.GetSnapshot();
            return !session.Connected || session.Role == RemoteSessionRole.Client;
        }

        private void ResetDiagnosticAttemptState()
        {
            _diagnosticReport = null;
            _diagnosticControllerIdentity = "";
            _diagnosticLatestRaw = null;
            _diagnosticRawMapper = null;
            _diagnosticRawRestSamples.Clear();
            _diagnosticRawLeftSweepSamples.Clear();
            _diagnosticRawRightSweepSamples.Clear();
            _diagnosticRawTriggerSamples.Clear();
            _diagnosticSourceReliable = true;
            _diagnosticUsedRawFallback = false;
            _gamepad.ResetLocalDiagnosticDeviceState();

            DiagnosticResultPanel.Visibility = Visibility.Collapsed;
            DiagnosticWarningsTextBlock.Visibility = Visibility.Collapsed;
            DiagnosticProgressBar.Value = 0;
            DiagnosticCountdownTextBlock.Text = "AO VIVO";
            ApplyDiagnosticButton.IsEnabled = false;
            RetryDiagnosticButton.IsEnabled = false;
            UpdateDiagnosticStepList(ControllerDiagnosticStep.DetectController, false);
        }

        private async Task RunControllerDiagnosticAsync()
        {
            if (_diagnosticRunning)
                return;

            if (!CanUseClientDiagnosticRole())
            {
                ControllerStatusTextBlock.Text = "O diagnóstico deve ser executado no CLIENTE que está com o controle físico.";
                return;
            }

            _diagnosticRunning = true;
            ResetDiagnosticAttemptState();
            _diagnosticCts?.Dispose();
            _diagnosticCts = new CancellationTokenSource();
            CancellationToken token = _diagnosticCts.Token;

            LevelingInfoToolTip.IsSuspended = true;
            DiagnosticPanel.Visibility = Visibility.Visible;
            DiagnosticResultPanel.Visibility = Visibility.Collapsed;
            DiagnosticWarningsTextBlock.Visibility = Visibility.Collapsed;
            DiagnosticProgressBar.Value = 0;
            DiagnosticCountdownTextBlock.Text = "AO VIVO";
            DiagnosticStageTitleTextBlock.Text = "ASSISTENTE DE DIAGNÓSTICO";
            DiagnosticInstructionTextBlock.Text = "Não há cronômetro cego: a próxima instrução só aparece quando o Leveling reconhecer a ação atual.";
            DiagnosticStatusTextBlock.Text = "Procurando o controle físico selecionado...";
            ApplyDiagnosticButton.IsEnabled = false;
            RevertDiagnosticButton.IsEnabled = _diagnosticApplied && _diagnosticPreviousTuning is not null;
            RetryDiagnosticButton.IsEnabled = false;
            DiagnosticControllerButton.IsEnabled = false;
            UpdateDiagnosticStepList(ControllerDiagnosticStep.DetectController, false);

            try
            {
                using IDisposable diagnosticCapture = _gamepad.BeginDiagnosticCapture();

                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    ResetDiagnosticAttemptState();
                    var analyzer = new ControllerDiagnosticAnalyzer();

                    try
                    {
                        LocalRawGamepadSnapshot first = await WaitForControllerAsync(token);
                        _diagnosticUsedRawFallback = first.Backend.Contains("RawInteractive", StringComparison.OrdinalIgnoreCase);
                        UpdateDiagnosticStepList(ControllerDiagnosticStep.DetectController, true);

                        if (_diagnosticUsedRawFallback)
                        {
                            _diagnosticRawMapper = new RawHidDiagnosticMapper(first.RawAxisCount);
                            _diagnosticSourceReliable = true;
                            DiagnosticStatusTextBlock.Text =
                                $"{first.ControllerName} • Sony {first.HardwareVendorId:X4}:{first.HardwareProductId:X4} • HID RAW • {first.RawAxisCount} eixos • {first.RawButtonCount} botões. O Leveling vai aprender cada eixo com seus movimentos.";
                            await ConfigureRawHidInteractivelyAsync(analyzer, token);
                        }
                        else
                        {
                            _diagnosticSourceReliable = true;
                            DiagnosticStatusTextBlock.Text = $"{first.ControllerName} • {first.Backend} • eixos nomeados pelo Windows.";

                            _diagnosticStep = ControllerDiagnosticStep.StableRest;
                            SetDiagnosticAction(
                                "SOLTE O CONTROLE",
                                "Não toque nos analógicos, gatilhos ou botões. Quando a leitura ficar estável por alguns segundos, o Leveling avança sozinho.");
                            await WaitForStableRestAsync(analyzer, token);
                            UpdateDiagnosticStepList(ControllerDiagnosticStep.StableRest, true);

                            _diagnosticStep = ControllerDiagnosticStep.LeftStick;
                            await RunStickGuideAsync(analyzer, leftStick: true, token);
                            UpdateDiagnosticStepList(ControllerDiagnosticStep.LeftStick, true);

                            _diagnosticStep = ControllerDiagnosticStep.RightStick;
                            await RunStickGuideAsync(analyzer, leftStick: false, token);
                            UpdateDiagnosticStepList(ControllerDiagnosticStep.RightStick, true);

                            _diagnosticStep = ControllerDiagnosticStep.LeftTrigger;
                            await RunTriggerGuideAsync(analyzer, leftTrigger: true, token);
                            UpdateDiagnosticStepList(ControllerDiagnosticStep.LeftTrigger, true);

                            _diagnosticStep = ControllerDiagnosticStep.RightTrigger;
                            await RunTriggerGuideAsync(analyzer, leftTrigger: false, token);
                            UpdateDiagnosticStepList(ControllerDiagnosticStep.RightTrigger, true);
                        }

                        token.ThrowIfCancellationRequested();
                        _diagnosticReport = analyzer.BuildReport();
                        _diagnosticStep = ControllerDiagnosticStep.Result;
                        UpdateDiagnosticStepList(ControllerDiagnosticStep.Result, true);
                        ShowDiagnosticReport(_diagnosticReport);
                        break;
                    }
                    catch (ControllerDisconnectedDuringDiagnosticException)
                    {
                        ResetDiagnosticAttemptState();
                        DiagnosticStageTitleTextBlock.Text = "CONTROLE DESCONECTADO";
                        SetDiagnosticAction(
                            "CONECTE SEU CONTROLE",
                            "O controle foi removido. O Leveling descartou todas as amostras e o mapeamento HID anteriores. Reconecte por USB/Bluetooth; o assistente recomeça automaticamente do zero.");
                        DiagnosticStatusTextBlock.Text = _gamepad.GetLocalDiagnosticDiscoverySummary() +
                            "\nAguardando uma nova instância do controle. Nenhuma leitura antiga será reutilizada.";

                        // WaitForControllerAsync performs a short consecutive-presence
                        // confirmation before the next attempt. The returned sample is
                        // intentionally discarded here so the new attempt starts clean.
                        await WaitForControllerAsync(token);
                        ResetDiagnosticAttemptState();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                DiagnosticStatusTextBlock.Text = "Diagnóstico cancelado. Nenhum ajuste foi aplicado.";
            }
            catch (Exception ex)
            {
                DiagnosticStageTitleTextBlock.Text = "DIAGNÓSTICO INTERROMPIDO";
                SetDiagnosticAction("NÃO FOI POSSÍVEL CONTINUAR", ex.Message);
                DiagnosticStatusTextBlock.Text = "Você pode fechar o assistente, reconectar o controle e tentar novamente.";
                DiagnosticResultPanel.Visibility = Visibility.Collapsed;
            }
            finally
            {
                _diagnosticRunning = false;
                _diagnosticLatestRaw = null;
                RetryDiagnosticButton.IsEnabled = _diagnosticReport is not null;
                DiagnosticControllerButton.IsEnabled = CanUseClientDiagnosticRole();
                _diagnosticCts?.Dispose();
                _diagnosticCts = null;
                SafeUpdateUi();
            }
        }

        private async Task<LocalRawGamepadSnapshot> WaitForControllerAsync(CancellationToken token)
        {
            _diagnosticStep = ControllerDiagnosticStep.DetectController;
            SetDiagnosticAction(
                "CONECTE SEU CONTROLE",
                "O assistente fica aguardando até reconhecer a fonte. Você pode reconectar por USB/Bluetooth ou cancelar quando quiser.");

            string candidateIdentity = "";
            int consecutivePresence = 0;

            while (true)
            {
                token.ThrowIfCancellationRequested();
                EnsureDiagnosticRole();
                if (_gamepad.TryGetLocalDiagnosticSnapshot(out LocalRawGamepadSnapshot sample) && sample.Connected)
                {
                    string identity = string.IsNullOrWhiteSpace(sample.ControllerIdentity)
                        ? $"{sample.Backend}:{sample.UserIndex}:{sample.ControllerName}"
                        : sample.ControllerIdentity;

                    if (string.Equals(candidateIdentity, identity, StringComparison.Ordinal))
                        consecutivePresence++;
                    else
                    {
                        candidateIdentity = identity;
                        consecutivePresence = 1;
                    }

                    DiagnosticStatusTextBlock.Text = sample.Backend.Contains("RawInteractive", StringComparison.OrdinalIgnoreCase)
                        ? $"DualSense encontrado via Sony HID RAW • VID {sample.HardwareVendorId:X4} • PID {sample.HardwareProductId:X4} • confirmando conexão {consecutivePresence}/3..."
                        : $"{sample.ControllerName} detectado via {sample.Backend} • confirmando conexão {consecutivePresence}/3...";

                    if (consecutivePresence >= 3)
                    {
                        RegisterDiagnosticSample(sample);
                        if (sample.Backend.Contains("RawInteractive", StringComparison.OrdinalIgnoreCase))
                        {
                            DiagnosticStatusTextBlock.Text =
                                $"DualSense encontrado via Sony HID RAW • VID {sample.HardwareVendorId:X4} • PID {sample.HardwareProductId:X4} • {sample.RawAxisCount} eixos • {sample.RawButtonCount} botões.";
                        }
                        else
                        {
                            DiagnosticStatusTextBlock.Text = $"{sample.ControllerName} detectado via {sample.Backend}.";
                        }
                        return sample;
                    }
                }
                else
                {
                    candidateIdentity = "";
                    consecutivePresence = 0;
                    DiagnosticStatusTextBlock.Text = _gamepad.GetLocalDiagnosticDiscoverySummary() +
                        "\nCONECTE SEU CONTROLE • aguardando uma fonte realmente presente no Windows.";
                }

                await Task.Delay(120, token);
            }
        }

        private async Task ConfigureRawHidInteractivelyAsync(
            ControllerDiagnosticAnalyzer analyzer,
            CancellationToken token)
        {
            RawHidDiagnosticMapper mapper = _diagnosticRawMapper ??
                throw new InvalidOperationException("O mapeador HID não foi inicializado.");

            _diagnosticStep = ControllerDiagnosticStep.StableRest;
            SetDiagnosticAction(
                "SOLTE O CONTROLE",
                "Primeiro vamos aprender o repouso dos eixos HID. Não toque nos analógicos nem gatilhos. Se houver um movimento grande, a contagem reinicia.");
            await WaitForRawStableBaselineAsync(mapper, token);
            UpdateDiagnosticStepList(ControllerDiagnosticStep.StableRest, true);

            _diagnosticStep = ControllerDiagnosticStep.LeftStick;
            await RunRawStickGuideAsync(mapper, analyzer, leftStick: true, token);
            UpdateDiagnosticStepList(ControllerDiagnosticStep.LeftStick, true);

            _diagnosticStep = ControllerDiagnosticStep.RightStick;
            await RunRawStickGuideAsync(mapper, analyzer, leftStick: false, token);
            UpdateDiagnosticStepList(ControllerDiagnosticStep.RightStick, true);

            _diagnosticStep = ControllerDiagnosticStep.LeftTrigger;
            await RunRawTriggerGuideAsync(mapper, analyzer, leftTrigger: true, token);
            UpdateDiagnosticStepList(ControllerDiagnosticStep.LeftTrigger, true);

            _diagnosticStep = ControllerDiagnosticStep.RightTrigger;
            await RunRawTriggerGuideAsync(mapper, analyzer, leftTrigger: false, token);
            UpdateDiagnosticStepList(ControllerDiagnosticStep.RightTrigger, true);

            foreach (LocalRawGamepadSnapshot raw in _diagnosticRawRestSamples)
                analyzer.AddRestSample(mapper.MapSnapshot(raw));

            DiagnosticStatusTextBlock.Text =
                "Mapeamento HID concluído • " + mapper.MappingSummary + ". Somente componentes reconhecidos serão considerados no resultado.";
        }

        private async Task WaitForRawStableBaselineAsync(RawHidDiagnosticMapper mapper, CancellationToken token)
        {
            var stableSamples = new List<LocalRawGamepadSnapshot>();
            LocalRawGamepadSnapshot? previous = null;
            long stableSince = 0;

            while (true)
            {
                token.ThrowIfCancellationRequested();
                LocalRawGamepadSnapshot sample = ReadDiagnosticSample();
                if (sample.RawAxes.Length != mapper.AxisCount || sample.RawAxes.Length == 0)
                    throw new InvalidOperationException("O DualSense mudou a quantidade de eixos HID durante a calibração. Reconecte o controle e use REFAZER.");

                UpdateRawDiagnosticLiveVisual(sample, "Repouso RAW");
                bool changed = previous is not null && RawAxesDelta(previous, sample) > 0.18;
                if (changed)
                {
                    stableSamples.Clear();
                    stableSince = 0;
                    DiagnosticStatusTextBlock.Text = "Movimento grande detectado nos eixos HID — solte o controle; reiniciando a leitura de repouso.";
                }
                else
                {
                    stableSince = stableSince == 0 ? Environment.TickCount64 : stableSince;
                    stableSamples.Add(sample);
                    long stableMs = Environment.TickCount64 - stableSince;
                    double pct = Math.Clamp(stableMs / (double)StableRestRequiredMs, 0.0, 1.0);
                    DiagnosticProgressBar.Value = 8 + pct * 12;
                    DiagnosticStatusTextBlock.Text =
                        $"Repouso HID • {Math.Min(StableRestRequiredMs, stableMs) / 1000.0:0.0}s / {StableRestRequiredMs / 1000.0:0.0}s • {sample.RawAxisCount} eixos monitorados";
                    if (stableMs >= StableRestRequiredMs && stableSamples.Count >= 80)
                    {
                        _diagnosticRawRestSamples.Clear();
                        _diagnosticRawRestSamples.AddRange(stableSamples);
                        mapper.SetBaseline(stableSamples);
                        return;
                    }
                }

                previous = sample;
                await Task.Delay(DiagnosticSampleDelayMs, token);
            }
        }

        private async Task RunRawStickGuideAsync(
            RawHidDiagnosticMapper mapper,
            ControllerDiagnosticAnalyzer analyzer,
            bool leftStick,
            CancellationToken token)
        {
            string stickName = leftStick ? "ANALÓGICO ESQUERDO" : "ANALÓGICO DIREITO";
            string[] labels = { "← ESQUERDA", "→ DIREITA", "↑ CIMA", "↓ BAIXO" };
            RawHidDirection[] directions =
            {
                RawHidDirection.Left, RawHidDirection.Right,
                RawHidDirection.Up, RawHidDirection.Down
            };
            List<LocalRawGamepadSnapshot> sweep = leftStick
                ? _diagnosticRawLeftSweepSamples
                : _diagnosticRawRightSweepSamples;
            sweep.Clear();
            int recognized = 0;

            for (int i = 0; i < directions.Length; i++)
            {
                SetDiagnosticAction(
                    $"{stickName}  {labels[i]}",
                    "Empurre somente o analógico indicado, em linha reta, até o limite e segure. O Leveling observa quais eixos HID realmente mudaram e aprende o mapeamento.");

                bool ok = await WaitForRawStickDirectionAsync(
                    mapper, leftStick, directions[i], sweep, token);
                if (ok)
                {
                    recognized++;
                    DiagnosticStatusTextBlock.Text = $"{labels[i]} reconhecido ✓ • {mapper.MappingSummary}";
                    await Task.Delay(220, token);
                }
                else
                {
                    DiagnosticStatusTextBlock.Text =
                        $"{labels[i]} não foi identificado com confiança. O diagnóstico continuará e não aplicará correção nesse componente se o par de eixos ficar incompleto.";
                    await Task.Delay(700, token);
                }
            }

            if (mapper.IsStickMapped(leftStick))
            {
                foreach (LocalRawGamepadSnapshot raw in sweep)
                {
                    LocalRawGamepadSnapshot mapped = mapper.MapSnapshot(raw);
                    if (leftStick) analyzer.AddLeftSweepSample(mapped);
                    else analyzer.AddRightSweepSample(mapped);
                }
                DiagnosticStatusTextBlock.Text = $"{stickName}: X/Y identificados e validados ✓";
            }
            else
            {
                DiagnosticStatusTextBlock.Text = $"{stickName}: mapeamento incompleto. Esse stick ficará sem ajuste automático.";
            }
        }

        private async Task<bool> WaitForRawStickDirectionAsync(
            RawHidDiagnosticMapper mapper,
            bool leftStick,
            RawHidDirection direction,
            List<LocalRawGamepadSnapshot> sweep,
            CancellationToken token)
        {
            long started = Environment.TickCount64;
            int consecutive = 0;
            int candidateAxis = -1;
            RawHidObservation best = default;

            while (Environment.TickCount64 - started < DirectionTimeoutMs)
            {
                token.ThrowIfCancellationRequested();
                LocalRawGamepadSnapshot sample = ReadDiagnosticSample();
                sweep.Add(sample);
                UpdateRawDiagnosticLiveVisual(sample, "Identificando eixo HID");

                if (mapper.TryObserveStickDirection(leftStick, direction, sample, out RawHidObservation observation))
                {
                    if (candidateAxis != observation.AxisIndex)
                    {
                        candidateAxis = observation.AxisIndex;
                        consecutive = 0;
                        best = observation;
                    }
                    consecutive++;
                    if (observation.Strength > best.Strength)
                        best = observation;
                    DiagnosticStatusTextBlock.Text =
                        $"Movimento detectado no eixo HID {observation.AxisIndex} • força {observation.Strength * 100.0:0}% • segure mais um instante...";

                    if (consecutive >= 18)
                    {
                        if (mapper.CommitStickDirection(leftStick, direction, best, out string message))
                        {
                            DiagnosticStatusTextBlock.Text = message + " ✓";
                            return true;
                        }
                        DiagnosticStatusTextBlock.Text = message;
                        consecutive = 0;
                        candidateAxis = -1;
                    }
                }
                else
                {
                    consecutive = 0;
                    candidateAxis = -1;
                    DiagnosticStatusTextBlock.Text =
                        "Aguardando um movimento claro e reto. Mova apenas o analógico pedido; os demais controles devem ficar soltos.";
                }

                double elapsed = (Environment.TickCount64 - started) / (double)DirectionTimeoutMs;
                DiagnosticProgressBar.Value = ProgressForCurrentStep(Math.Clamp(elapsed, 0.0, 1.0) * 0.85);
                await Task.Delay(DiagnosticSampleDelayMs, token);
            }
            return false;
        }

        private async Task RunRawTriggerGuideAsync(
            RawHidDiagnosticMapper mapper,
            ControllerDiagnosticAnalyzer analyzer,
            bool leftTrigger,
            CancellationToken token)
        {
            string name = leftTrigger ? "L2 / LT" : "R2 / RT";
            var localSamples = new List<LocalRawGamepadSnapshot>();
            SetDiagnosticAction(
                $"APERTE {name} ATÉ O FUNDO",
                "Aperte apenas o gatilho indicado até o limite e segure. O Leveling descobrirá qual eixo HID pertence a ele.");

            bool pressed = await WaitForRawTriggerPressAsync(mapper, leftTrigger, localSamples, token);
            if (!pressed)
            {
                DiagnosticStatusTextBlock.Text = $"{name}: não foi possível identificar um eixo exclusivo. Esse gatilho não será alterado.";
                await Task.Delay(650, token);
                return;
            }

            SetDiagnosticAction(
                $"SOLTE {name}",
                "Solte completamente o gatilho. O Leveling precisa confirmar que o mesmo eixo voltou ao repouso.");
            bool released = await WaitForRawTriggerReleaseAsync(mapper, leftTrigger, localSamples, token);
            if (!released)
            {
                DiagnosticStatusTextBlock.Text = $"{name}: o retorno ao repouso não foi confirmado. Esse gatilho ficará sem ajuste automático.";
                await Task.Delay(650, token);
                return;
            }

            _diagnosticRawTriggerSamples.AddRange(localSamples);
            foreach (LocalRawGamepadSnapshot raw in localSamples)
                analyzer.AddTriggerSample(mapper.MapSnapshot(raw));
            DiagnosticStatusTextBlock.Text = $"{name}: eixo identificado e retorno ao repouso confirmado ✓";
            await Task.Delay(300, token);
        }

        private async Task<bool> WaitForRawTriggerPressAsync(
            RawHidDiagnosticMapper mapper,
            bool leftTrigger,
            List<LocalRawGamepadSnapshot> samples,
            CancellationToken token)
        {
            long started = Environment.TickCount64;
            int consecutive = 0;
            int candidateAxis = -1;
            RawHidObservation best = default;

            while (Environment.TickCount64 - started < TriggerTimeoutMs)
            {
                token.ThrowIfCancellationRequested();
                LocalRawGamepadSnapshot sample = ReadDiagnosticSample();
                samples.Add(sample);
                UpdateRawDiagnosticLiveVisual(sample, "Identificando gatilho HID");

                if (mapper.TryObserveTriggerPress(leftTrigger, sample, out RawHidObservation observation))
                {
                    if (candidateAxis != observation.AxisIndex)
                    {
                        candidateAxis = observation.AxisIndex;
                        consecutive = 0;
                        best = observation;
                    }
                    consecutive++;
                    if (observation.Strength > best.Strength)
                        best = observation;
                    DiagnosticStatusTextBlock.Text =
                        $"Possível eixo HID {observation.AxisIndex} • variação {observation.Strength * 100.0:0}% • segure...";
                    if (consecutive >= 15)
                    {
                        if (mapper.CommitTrigger(leftTrigger, best, out string message))
                        {
                            DiagnosticStatusTextBlock.Text = message + " ✓";
                            return true;
                        }
                        DiagnosticStatusTextBlock.Text = message;
                        consecutive = 0;
                        candidateAxis = -1;
                    }
                }
                else
                {
                    consecutive = 0;
                    candidateAxis = -1;
                    DiagnosticStatusTextBlock.Text = "Aguardando apenas o gatilho indicado ser pressionado.";
                }
                await Task.Delay(DiagnosticSampleDelayMs, token);
            }
            return false;
        }

        private async Task<bool> WaitForRawTriggerReleaseAsync(
            RawHidDiagnosticMapper mapper,
            bool leftTrigger,
            List<LocalRawGamepadSnapshot> samples,
            CancellationToken token)
        {
            long started = Environment.TickCount64;
            int consecutive = 0;
            while (Environment.TickCount64 - started < TriggerTimeoutMs)
            {
                token.ThrowIfCancellationRequested();
                LocalRawGamepadSnapshot sample = ReadDiagnosticSample();
                samples.Add(sample);
                LocalRawGamepadSnapshot mapped = mapper.MapSnapshot(sample);
                UpdateDiagnosticLiveVisual(mapped);
                bool released = mapper.IsTriggerReleased(leftTrigger, sample, out double value);
                consecutive = released ? consecutive + 1 : 0;
                DiagnosticStatusTextBlock.Text = $"{(leftTrigger ? "L2 / LT" : "R2 / RT")} • leitura aprendida {value * 100.0:0}% • aguardando repouso";
                if (consecutive >= 20)
                    return true;
                await Task.Delay(DiagnosticSampleDelayMs, token);
            }
            return false;
        }

        private LocalRawGamepadSnapshot MapDiagnosticSample(LocalRawGamepadSnapshot sample) =>
            _diagnosticRawMapper is null ? sample : _diagnosticRawMapper.MapSnapshot(sample);

        private void UpdateRawDiagnosticLiveVisual(LocalRawGamepadSnapshot sample, string phase)
        {
            if (_diagnosticRawMapper is not null &&
                (_diagnosticRawMapper.IsStickMapped(true) || _diagnosticRawMapper.IsStickMapped(false)))
            {
                UpdateDiagnosticLiveVisual(_diagnosticRawMapper.MapSnapshot(sample));
                return;
            }

            string axes = sample.RawAxes.Length == 0
                ? "sem eixos"
                : string.Join("  ", sample.RawAxes.Take(Math.Min(8, sample.RawAxes.Length)).Select((v, i) => $"A{i}:{v:0.00}"));
            DiagnosticLeftTriggerBar.Value = 0;
            DiagnosticRightTriggerBar.Value = 0;
            DiagnosticLeftTriggerValueTextBlock.Text = "--";
            DiagnosticRightTriggerValueTextBlock.Text = "--";
            DiagnosticLiveValuesTextBlock.Text =
                $"{phase}\n{axes}\n{sample.ControllerName} • Sony {sample.HardwareVendorId:X4}:{sample.HardwareProductId:X4}";
        }

        private static double RawAxesDelta(LocalRawGamepadSnapshot a, LocalRawGamepadSnapshot b)
        {
            if (a.RawAxes.Length == 0 || a.RawAxes.Length != b.RawAxes.Length)
                return 1.0;
            double max = 0;
            for (int i = 0; i < a.RawAxes.Length; i++)
                max = Math.Max(max, Math.Abs(a.RawAxes[i] - b.RawAxes[i]));
            return max;
        }

        private async Task WaitForStableRestAsync(ControllerDiagnosticAnalyzer analyzer, CancellationToken token)
        {
            var stableSamples = new List<LocalRawGamepadSnapshot>();
            LocalRawGamepadSnapshot? previous = null;
            long stableSince = 0;

            while (true)
            {
                token.ThrowIfCancellationRequested();
                LocalRawGamepadSnapshot sample = ReadDiagnosticSample();
                UpdateDiagnosticLiveVisual(sample);

                bool obviouslyHeld = StickMagnitude(sample.LeftThumbX, sample.LeftThumbY) > StickCenterGuard ||
                                     StickMagnitude(sample.RightThumbX, sample.RightThumbY) > StickCenterGuard ||
                                     sample.LeftTrigger / 255.0 > 0.50 ||
                                     sample.RightTrigger / 255.0 > 0.50;
                bool changed = previous is not null && SampleDelta(previous, sample) > StableDeltaThreshold;

                if (obviouslyHeld || changed)
                {
                    stableSamples.Clear();
                    stableSince = 0;
                    DiagnosticStatusTextBlock.Text = obviouslyHeld
                        ? "Um eixo ainda está muito acionado. Solte os analógicos e gatilhos; botões presos podem permanecer para serem detectados."
                        : "Movimento detectado — reiniciando a medição de repouso.";
                }
                else
                {
                    stableSince = stableSince == 0 ? Environment.TickCount64 : stableSince;
                    stableSamples.Add(sample);
                    long stableMs = Environment.TickCount64 - stableSince;
                    double pct = Math.Clamp(stableMs / (double)StableRestRequiredMs, 0.0, 1.0);
                    DiagnosticProgressBar.Value = 8 + pct * 12;
                    DiagnosticStatusTextBlock.Text = $"Repouso estável detectado • {Math.Min(StableRestRequiredMs, stableMs) / 1000.0:0.0}s / {StableRestRequiredMs / 1000.0:0.0}s";
                    if (stableMs >= StableRestRequiredMs && stableSamples.Count >= 80)
                    {
                        foreach (LocalRawGamepadSnapshot stable in stableSamples)
                            analyzer.AddRestSample(stable);
                        return;
                    }
                }

                previous = sample;
                await Task.Delay(DiagnosticSampleDelayMs, token);
            }
        }

        private async Task RunStickGuideAsync(ControllerDiagnosticAnalyzer analyzer, bool leftStick, CancellationToken token)
        {
            string stickName = leftStick ? "ANALÓGICO ESQUERDO" : "ANALÓGICO DIREITO";
            string[] labels = { "← ESQUERDA", "→ DIREITA", "↑ CIMA", "↓ BAIXO" };
            StickDirection[] directions = { StickDirection.Left, StickDirection.Right, StickDirection.Up, StickDirection.Down };
            int recognized = 0;

            for (int i = 0; i < directions.Length; i++)
            {
                SetDiagnosticAction(
                    $"{stickName}  {labels[i]}",
                    $"Empurre o {stickName.ToLowerInvariant()} até o limite em {labels[i].Split(' ')[1].ToLowerInvariant()} e segure por um instante. O Leveling avança automaticamente quando reconhecer.");

                bool ok = await WaitForStickDirectionAsync(analyzer, leftStick, directions[i], token);
                if (ok)
                {
                    recognized++;
                    DiagnosticStatusTextBlock.Text = $"{labels[i]} reconhecido ✓";
                    await Task.Delay(180, token);
                }
                else
                {
                    DiagnosticStatusTextBlock.Text = $"{labels[i]} não foi reconhecido dentro do tempo. Essa direção ficará marcada como não validada, mas o diagnóstico continuará.";
                    await Task.Delay(650, token);
                }
            }

            DiagnosticStatusTextBlock.Text = recognized == 4
                ? $"{stickName}: quatro direções reconhecidas ✓"
                : $"{stickName}: {recognized}/4 direções reconhecidas. O resultado indicará o que é seguro aplicar.";
        }

        private enum StickDirection { Left, Right, Up, Down }

        private async Task<bool> WaitForStickDirectionAsync(
            ControllerDiagnosticAnalyzer analyzer,
            bool leftStick,
            StickDirection direction,
            CancellationToken token)
        {
            long started = Environment.TickCount64;
            int consecutive = 0;

            while (Environment.TickCount64 - started < DirectionTimeoutMs)
            {
                token.ThrowIfCancellationRequested();
                LocalRawGamepadSnapshot sample = ReadDiagnosticSample();
                UpdateDiagnosticLiveVisual(sample);
                if (leftStick)
                    analyzer.AddLeftSweepSample(sample);
                else
                    analyzer.AddRightSweepSample(sample);

                double x = NormalizeAxis(leftStick ? sample.LeftThumbX : sample.RightThumbX);
                double y = NormalizeAxis(leftStick ? sample.LeftThumbY : sample.RightThumbY);
                double primary = direction switch
                {
                    StickDirection.Left => -x,
                    StickDirection.Right => x,
                    StickDirection.Up => y,
                    _ => -y
                };
                double secondary = direction is StickDirection.Left or StickDirection.Right ? Math.Abs(y) : Math.Abs(x);
                bool matches = primary >= StickDirectionThreshold && primary >= secondary * 1.15;
                consecutive = matches ? consecutive + 1 : 0;

                double elapsed = (Environment.TickCount64 - started) / (double)DirectionTimeoutMs;
                DiagnosticProgressBar.Value = ProgressForCurrentStep(Math.Clamp(elapsed, 0.0, 1.0) * 0.8);
                DiagnosticStatusTextBlock.Text = matches
                    ? "Direção reconhecida — segure só mais um instante..."
                    : "Aguardando a direção indicada acima. Observe o ponto ao vivo para confirmar o que o Leveling está lendo.";

                if (consecutive >= 20)
                    return true;

                await Task.Delay(DiagnosticSampleDelayMs, token);
            }

            return false;
        }

        private async Task RunTriggerGuideAsync(ControllerDiagnosticAnalyzer analyzer, bool leftTrigger, CancellationToken token)
        {
            string name = leftTrigger ? "L2 / LT" : "R2 / RT";
            SetDiagnosticAction($"APERTE {name} ATÉ O FUNDO", "A barra ao vivo deve subir. Segure por um instante; o Leveling reconhecerá automaticamente.");
            bool pressed = await WaitForTriggerPressAsync(analyzer, leftTrigger, token);
            if (!pressed)
            {
                DiagnosticStatusTextBlock.Text = $"{name} não atingiu o nível mínimo esperado. O diagnóstico continuará sem alterar esse gatilho.";
                await Task.Delay(650, token);
                return;
            }

            SetDiagnosticAction($"SOLTE {name}", "Solte completamente o gatilho. A barra deve voltar para perto de 0%.");
            bool released = await WaitForTriggerReleaseAsync(analyzer, leftTrigger, token);
            DiagnosticStatusTextBlock.Text = released
                ? $"{name}: pressão e repouso reconhecidos ✓"
                : $"{name}: a soltura não foi confirmada. Esse gatilho ficará não validado.";
            await Task.Delay(350, token);
        }

        private async Task<bool> WaitForTriggerPressAsync(ControllerDiagnosticAnalyzer analyzer, bool leftTrigger, CancellationToken token)
        {
            long started = Environment.TickCount64;
            int consecutive = 0;
            while (Environment.TickCount64 - started < TriggerTimeoutMs)
            {
                token.ThrowIfCancellationRequested();
                LocalRawGamepadSnapshot sample = ReadDiagnosticSample();
                analyzer.AddTriggerSample(sample);
                UpdateDiagnosticLiveVisual(sample);
                double value = (leftTrigger ? sample.LeftTrigger : sample.RightTrigger) / 255.0;
                consecutive = value >= TriggerPressThreshold ? consecutive + 1 : 0;
                DiagnosticStatusTextBlock.Text = $"Leitura atual: {value * 100.0:0}% • alvo: pelo menos {TriggerPressThreshold * 100.0:0}%";
                if (consecutive >= 12)
                    return true;
                await Task.Delay(DiagnosticSampleDelayMs, token);
            }
            return false;
        }

        private async Task<bool> WaitForTriggerReleaseAsync(ControllerDiagnosticAnalyzer analyzer, bool leftTrigger, CancellationToken token)
        {
            long started = Environment.TickCount64;
            int consecutive = 0;
            while (Environment.TickCount64 - started < TriggerTimeoutMs)
            {
                token.ThrowIfCancellationRequested();
                LocalRawGamepadSnapshot sample = ReadDiagnosticSample();
                analyzer.AddTriggerSample(sample);
                UpdateDiagnosticLiveVisual(sample);
                double value = (leftTrigger ? sample.LeftTrigger : sample.RightTrigger) / 255.0;
                consecutive = value <= TriggerReleaseThreshold ? consecutive + 1 : 0;
                DiagnosticStatusTextBlock.Text = $"Leitura atual: {value * 100.0:0}% • aguardando repouso";
                if (consecutive >= 20)
                    return true;
                await Task.Delay(DiagnosticSampleDelayMs, token);
            }
            return false;
        }

        private LocalRawGamepadSnapshot ReadDiagnosticSample()
        {
            EnsureDiagnosticRole();
            if (!_gamepad.TryGetLocalDiagnosticSnapshot(out LocalRawGamepadSnapshot sample) || !sample.Connected)
                throw new ControllerDisconnectedDuringDiagnosticException();
            RegisterDiagnosticSample(sample);
            return sample;
        }

        private void RegisterDiagnosticSample(LocalRawGamepadSnapshot sample)
        {
            string identity = string.IsNullOrWhiteSpace(sample.ControllerIdentity)
                ? $"{sample.Backend}:{sample.UserIndex}:{sample.ControllerName}"
                : sample.ControllerIdentity;
            if (string.IsNullOrWhiteSpace(_diagnosticControllerIdentity))
                _diagnosticControllerIdentity = identity;
            else if (!string.Equals(_diagnosticControllerIdentity, identity, StringComparison.Ordinal))
                throw new InvalidOperationException("O controle ativo mudou durante o diagnóstico. O Leveling interrompeu a coleta para não misturar dois dispositivos.");

            _diagnosticLatestRaw = sample;
            if (sample.Backend.Contains("RawInteractive", StringComparison.OrdinalIgnoreCase))
                _diagnosticUsedRawFallback = true;
        }

        private void EnsureDiagnosticRole()
        {
            if (!CanUseClientDiagnosticRole())
                throw new InvalidOperationException("A sessão mudou para HOST. Execute o diagnóstico no PC que está usando o controle físico.");
        }

        private void UpdateDiagnosticLiveVisual(LocalRawGamepadSnapshot sample)
        {
            MoveDiagnosticDot(DiagnosticLeftStickDot, sample.LeftThumbX, sample.LeftThumbY);
            MoveDiagnosticDot(DiagnosticRightStickDot, sample.RightThumbX, sample.RightThumbY);
            double lt = sample.LeftTrigger * 100.0 / 255.0;
            double rt = sample.RightTrigger * 100.0 / 255.0;
            DiagnosticLeftTriggerBar.Value = lt;
            DiagnosticRightTriggerBar.Value = rt;
            DiagnosticLeftTriggerValueTextBlock.Text = $"{lt:0}%";
            DiagnosticRightTriggerValueTextBlock.Text = $"{rt:0}%";
            DiagnosticLiveValuesTextBlock.Text =
                $"L {NormalizeAxis(sample.LeftThumbX):0.00}, {NormalizeAxis(sample.LeftThumbY):0.00}  •  " +
                $"R {NormalizeAxis(sample.RightThumbX):0.00}, {NormalizeAxis(sample.RightThumbY):0.00}\n{sample.ControllerName} • {sample.Backend}";
        }

        private static void MoveDiagnosticDot(UIElement dot, short x, short y)
        {
            const double center = 58.5;
            const double travel = 51.0;
            Canvas.SetLeft(dot, center + NormalizeAxis(x) * travel);
            Canvas.SetTop(dot, center - NormalizeAxis(y) * travel);
        }

        private static double SampleDelta(LocalRawGamepadSnapshot a, LocalRawGamepadSnapshot b)
        {
            double[] delta =
            {
                Math.Abs(NormalizeAxis(a.LeftThumbX) - NormalizeAxis(b.LeftThumbX)),
                Math.Abs(NormalizeAxis(a.LeftThumbY) - NormalizeAxis(b.LeftThumbY)),
                Math.Abs(NormalizeAxis(a.RightThumbX) - NormalizeAxis(b.RightThumbX)),
                Math.Abs(NormalizeAxis(a.RightThumbY) - NormalizeAxis(b.RightThumbY))
            };
            return delta.Max();
        }

        private static double StickMagnitude(short x, short y)
        {
            double nx = NormalizeAxis(x);
            double ny = NormalizeAxis(y);
            return Math.Min(1.0, Math.Sqrt(nx * nx + ny * ny));
        }

        private static double NormalizeAxis(short value) =>
            value == short.MinValue ? -1.0 : Math.Clamp(value / 32767.0, -1.0, 1.0);

        private void SetDiagnosticAction(string title, string hint)
        {
            DiagnosticActionTitleTextBlock.Text = title;
            DiagnosticActionHintTextBlock.Text = hint;
        }

        private double ProgressForCurrentStep(double localProgress)
        {
            double start = _diagnosticStep switch
            {
                ControllerDiagnosticStep.DetectController => 0,
                ControllerDiagnosticStep.StableRest => 8,
                ControllerDiagnosticStep.LeftStick => 20,
                ControllerDiagnosticStep.RightStick => 43,
                ControllerDiagnosticStep.LeftTrigger => 66,
                ControllerDiagnosticStep.RightTrigger => 80,
                _ => 100
            };
            double span = _diagnosticStep switch
            {
                ControllerDiagnosticStep.DetectController => 8,
                ControllerDiagnosticStep.StableRest => 12,
                ControllerDiagnosticStep.LeftStick => 23,
                ControllerDiagnosticStep.RightStick => 23,
                ControllerDiagnosticStep.LeftTrigger => 14,
                ControllerDiagnosticStep.RightTrigger => 14,
                _ => 0
            };
            return Math.Clamp(start + span * Math.Clamp(localProgress, 0.0, 1.0), 0.0, 100.0);
        }

        private void UpdateDiagnosticStepList(ControllerDiagnosticStep step, bool completed)
        {
            int current = (int)step;
            string Mark(ControllerDiagnosticStep item)
            {
                int index = (int)item;
                if (index < current || (item == step && completed)) return "✓";
                if (item == step) return "●";
                return "○";
            }

            DiagnosticStepListTextBlock.Text =
                $"{Mark(ControllerDiagnosticStep.DetectController)} Detectar controle\n" +
                $"{Mark(ControllerDiagnosticStep.StableRest)} Repouso estável\n" +
                $"{Mark(ControllerDiagnosticStep.LeftStick)} Analógico esquerdo\n" +
                $"{Mark(ControllerDiagnosticStep.RightStick)} Analógico direito\n" +
                $"{Mark(ControllerDiagnosticStep.LeftTrigger)} L2 / LT\n" +
                $"{Mark(ControllerDiagnosticStep.RightTrigger)} R2 / RT\n" +
                $"{Mark(ControllerDiagnosticStep.Result)} Resultado";

            DiagnosticProgressBar.Value = completed ? ProgressForCurrentStep(1.0) : ProgressForCurrentStep(0.0);
        }

        private void ShowDiagnosticReport(ControllerDiagnosticReport report)
        {
            DiagnosticProgressBar.Value = 100;
            DiagnosticCountdownTextBlock.Text = "CONCLUÍDO";
            DiagnosticStageTitleTextBlock.Text = "RESULTADO DO DIAGNÓSTICO";
            SetDiagnosticAction(
                report.HasActionableRecommendation ? "MEDIÇÃO FINALIZADA" : "MEDIÇÃO INSUFICIENTE",
                _diagnosticSourceReliable
                    ? "Confira cada componente abaixo. Somente medições validadas podem ser aplicadas."
                    : "A fonte não pôde ser validada com segurança; nenhum ajuste automático será aplicado.");
            DiagnosticInstructionTextBlock.Text = $"{report.ControllerName} • análise local antes de deadzone, curva e remapeamento.";
            DiagnosticHealthTextBlock.Text = $"Saúde estimada: {report.HealthScore}/100 • {report.HealthLabel} • confiança {report.ConfidenceLabel}";

            string leftState = report.LeftStickValid ? "VALIDADO" : "não validado";
            string rightState = report.RightStickValid ? "VALIDADO" : "não validado";
            string ltState = report.LeftTriggerValid ? "VALIDADO" : "não validado";
            string rtState = report.RightTriggerValid ? "VALIDADO" : "não validado";
            DiagnosticMeasurementsTextBlock.Text =
                $"Stick L [{leftState}]: centro/drift {report.LeftRestDriftPercent:0.0}% • jitter {report.LeftJitterPercent:0.0}% • alcance {report.LeftDirectionalCoveragePercent:0.0}% • assimetria {report.LeftDirectionalAsymmetryPercent:0.0}%\n" +
                $"Stick R [{rightState}]: centro/drift {report.RightRestDriftPercent:0.0}% • jitter {report.RightJitterPercent:0.0}% • alcance {report.RightDirectionalCoveragePercent:0.0}% • assimetria {report.RightDirectionalAsymmetryPercent:0.0}%\n" +
                $"L2/LT [{ltState}]: repouso {report.LeftTriggerRestPercent:0.0}% • máximo {report.LeftTriggerMaxPercent:0.0}%\n" +
                $"R2/RT [{rtState}]: repouso {report.RightTriggerRestPercent:0.0}% • máximo {report.RightTriggerMaxPercent:0.0}%";

            ControllerTuningRecommendation r = report.Recommendation;
            var recommendationLines = new List<string>();
            if (r.ApplyLeftStick)
                recommendationLines.Add($"Stick L → Deadzone {r.LeftStickDeadzonePercent}% • Outer {r.LeftStickOuterDeadzonePercent}% • Anti 0%");
            if (r.ApplyRightStick)
                recommendationLines.Add($"Stick R → Deadzone {r.RightStickDeadzonePercent}% • Outer {r.RightStickOuterDeadzonePercent}% • Anti 0%");
            if (r.ApplyLeftTrigger)
                recommendationLines.Add($"L2/LT → Deadzone {r.LeftTriggerDeadzonePercent}%");
            if (r.ApplyRightTrigger)
                recommendationLines.Add($"R2/RT → Deadzone {r.RightTriggerDeadzonePercent}%");
            if (r.ApplyStickResponseCurve)
                recommendationLines.Add("Curva → Linear");

            DiagnosticRecommendationTextBlock.Text = recommendationLines.Count == 0
                ? "Nenhum ajuste automático seguro foi calculado nesta coleta."
                : "AJUSTES MEDIDOS\n" + string.Join("\n", recommendationLines);

            var warnings = report.Warnings.ToList();
            if (_diagnosticUsedRawFallback)
                warnings.Insert(0, "DualSense lido por HID RAW com eixos identificados pelo assistente interativo. O Leveling não assumiu índices fixos: cada stick e gatilho foi associado apenas após reconhecer seus movimentos.");

            if (warnings.Count > 0)
            {
                DiagnosticWarningsTextBlock.Text = "ATENÇÃO\n• " + string.Join("\n• ", warnings);
                DiagnosticWarningsTextBlock.Visibility = Visibility.Visible;
            }
            else
            {
                DiagnosticWarningsTextBlock.Text = "Nenhum problema adicional detectado.";
                DiagnosticWarningsTextBlock.Visibility = Visibility.Collapsed;
            }

            DiagnosticResultPanel.Visibility = Visibility.Visible;
            ApplyDiagnosticButton.IsEnabled = report.HasActionableRecommendation && _diagnosticSourceReliable;
            RevertDiagnosticButton.IsEnabled = _diagnosticApplied && _diagnosticPreviousTuning is not null;
            RetryDiagnosticButton.IsEnabled = true;

            bool correctionNeeded =
                r.LeftStickDeadzonePercent > 0 || r.RightStickDeadzonePercent > 0 ||
                r.LeftStickOuterDeadzonePercent > 0 || r.RightStickOuterDeadzonePercent > 0 ||
                r.LeftTriggerDeadzonePercent > 0 || r.RightTriggerDeadzonePercent > 0;

            DiagnosticStatusTextBlock.Text = !_diagnosticSourceReliable
                ? "Resultado somente informativo: a fonte não foi validada com segurança para aplicação automática."
                : !report.HasActionableRecommendation
                    ? "Nenhum componente foi validado o bastante para aplicação automática. Use REFAZER."
                    : correctionNeeded
                        ? "Há correções calculadas. Nada muda até você clicar em APLICAR AJUSTES MEDIDOS."
                        : "Os componentes medidos parecem saudáveis. A recomendação calculada é manter deadzone/outer medidos em 0%.";
        }
    }
}
