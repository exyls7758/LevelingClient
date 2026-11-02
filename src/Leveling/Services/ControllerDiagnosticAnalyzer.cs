using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiverse.Services
{
    internal sealed class ControllerTuningRecommendation
    {
        public int LeftStickDeadzonePercent { get; init; }
        public int RightStickDeadzonePercent { get; init; }
        public int LeftStickOuterDeadzonePercent { get; init; }
        public int RightStickOuterDeadzonePercent { get; init; }
        public int LeftStickAntiDeadzonePercent { get; init; } = 0;
        public int RightStickAntiDeadzonePercent { get; init; } = 0;
        public int LeftTriggerDeadzonePercent { get; init; }
        public int RightTriggerDeadzonePercent { get; init; }
        public GamepadStickResponseCurve StickResponseCurve { get; init; } = GamepadStickResponseCurve.Linear;
        public bool ApplyLeftStick { get; init; }
        public bool ApplyRightStick { get; init; }
        public bool ApplyLeftTrigger { get; init; }
        public bool ApplyRightTrigger { get; init; }
        public bool ApplyStickResponseCurve { get; init; }
    }

    internal sealed class ControllerDiagnosticReport
    {
        public string ControllerName { get; init; } = "Controle";
        public double LeftRestDriftPercent { get; init; }
        public double RightRestDriftPercent { get; init; }
        public double LeftJitterPercent { get; init; }
        public double RightJitterPercent { get; init; }
        public double LeftDirectionalCoveragePercent { get; init; }
        public double RightDirectionalCoveragePercent { get; init; }
        public double LeftDirectionalAsymmetryPercent { get; init; }
        public double RightDirectionalAsymmetryPercent { get; init; }
        public double LeftTriggerRestPercent { get; init; }
        public double RightTriggerRestPercent { get; init; }
        public double LeftTriggerMaxPercent { get; init; }
        public double RightTriggerMaxPercent { get; init; }
        public bool LeftSweepComplete { get; init; }
        public bool RightSweepComplete { get; init; }
        public bool ButtonsHeldAtRest { get; init; }
        public bool LeftStickValid { get; init; }
        public bool RightStickValid { get; init; }
        public bool LeftTriggerValid { get; init; }
        public bool RightTriggerValid { get; init; }
        public bool HasActionableRecommendation { get; init; }
        public IReadOnlyList<string> StuckButtons { get; init; } = Array.Empty<string>();
        public int HealthScore { get; init; }
        public string HealthLabel { get; init; } = "--";
        public string ConfidenceLabel { get; init; } = "Inconclusivo";
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
        public ControllerTuningRecommendation Recommendation { get; init; } = new();
    }

    /// <summary>
    /// Pure diagnostic math. It only consumes raw, pre-tuning controller samples and
    /// never reads settings, sends input, or touches ViGEm/video/network state.
    /// </summary>
    internal sealed class ControllerDiagnosticAnalyzer
    {
        private const double MinimumSweepEvidence = 0.55;
        private const double CompleteSweepCoverage = 0.80;
        private const double MaximumOuterAsymmetry = 0.12;
        private static readonly GamepadDigitalButton[] DiagnosticButtons =
        {
            GamepadDigitalButton.DpadUp, GamepadDigitalButton.DpadDown,
            GamepadDigitalButton.DpadLeft, GamepadDigitalButton.DpadRight,
            GamepadDigitalButton.Start, GamepadDigitalButton.Back,
            GamepadDigitalButton.L3, GamepadDigitalButton.R3,
            GamepadDigitalButton.LB, GamepadDigitalButton.RB,
            GamepadDigitalButton.A, GamepadDigitalButton.B,
            GamepadDigitalButton.X, GamepadDigitalButton.Y
        };

        private readonly List<(double X, double Y)> _leftRest = new();
        private readonly List<(double X, double Y)> _rightRest = new();
        private readonly List<double> _leftTriggerRest = new();
        private readonly List<double> _rightTriggerRest = new();
        private readonly List<(double X, double Y)> _leftSweep = new();
        private readonly List<(double X, double Y)> _rightSweep = new();
        private readonly List<double> _leftTriggerSweep = new();
        private readonly List<double> _rightTriggerSweep = new();
        private int _restSamples;
        private int _restSamplesWithButtons;
        private readonly Dictionary<GamepadDigitalButton, int> _restButtonCounts = new();
        private string _controllerName = "Controle";

        public void AddRestSample(LocalRawGamepadSnapshot sample)
        {
            CaptureControllerName(sample);
            _leftRest.Add((NormalizeAxis(sample.LeftThumbX), NormalizeAxis(sample.LeftThumbY)));
            _rightRest.Add((NormalizeAxis(sample.RightThumbX), NormalizeAxis(sample.RightThumbY)));
            _leftTriggerRest.Add(NormalizeTrigger(sample.LeftTrigger));
            _rightTriggerRest.Add(NormalizeTrigger(sample.RightTrigger));
            _restSamples++;
            if (sample.Buttons != 0)
                _restSamplesWithButtons++;
            foreach (GamepadDigitalButton button in DiagnosticButtons)
            {
                if ((sample.Buttons & (ushort)button) == 0)
                    continue;
                _restButtonCounts[button] = _restButtonCounts.TryGetValue(button, out int count)
                    ? count + 1
                    : 1;
            }
        }

        public void AddLeftSweepSample(LocalRawGamepadSnapshot sample)
        {
            CaptureControllerName(sample);
            _leftSweep.Add((NormalizeAxis(sample.LeftThumbX), NormalizeAxis(sample.LeftThumbY)));
        }

        public void AddRightSweepSample(LocalRawGamepadSnapshot sample)
        {
            CaptureControllerName(sample);
            _rightSweep.Add((NormalizeAxis(sample.RightThumbX), NormalizeAxis(sample.RightThumbY)));
        }

        public void AddTriggerSample(LocalRawGamepadSnapshot sample)
        {
            CaptureControllerName(sample);
            _leftTriggerSweep.Add(NormalizeTrigger(sample.LeftTrigger));
            _rightTriggerSweep.Add(NormalizeTrigger(sample.RightTrigger));
        }

        public ControllerDiagnosticReport BuildReport()
        {
            double leftDrift = RestDrift(_leftRest);
            double rightDrift = RestDrift(_rightRest);
            double leftJitter = RestJitter(_leftRest);
            double rightJitter = RestJitter(_rightRest);
            double leftCoverage = DirectionalCoverage(_leftSweep);
            double rightCoverage = DirectionalCoverage(_rightSweep);
            double leftAsymmetry = DirectionalAsymmetry(_leftSweep);
            double rightAsymmetry = DirectionalAsymmetry(_rightSweep);
            bool leftSweepComplete = SweepComplete(leftCoverage);
            bool rightSweepComplete = SweepComplete(rightCoverage);
            bool leftOuterReliable = leftSweepComplete && leftAsymmetry <= MaximumOuterAsymmetry;
            bool rightOuterReliable = rightSweepComplete && rightAsymmetry <= MaximumOuterAsymmetry;
            double leftTriggerRest = Percentile(_leftTriggerRest, 0.99);
            double rightTriggerRest = Percentile(_rightTriggerRest, 0.99);
            double leftTriggerMax = _leftTriggerSweep.Count == 0 ? 0 : _leftTriggerSweep.Max();
            double rightTriggerMax = _rightTriggerSweep.Count == 0 ? 0 : _rightTriggerSweep.Max();
            bool leftStickValid = _leftRest.Count >= 40 && _leftSweep.Count >= 20 && leftCoverage >= 0.45;
            bool rightStickValid = _rightRest.Count >= 40 && _rightSweep.Count >= 20 && rightCoverage >= 0.45;
            bool leftTriggerValid = _leftTriggerRest.Count >= 40 && _leftTriggerSweep.Count >= 10 && leftTriggerMax >= 0.50;
            bool rightTriggerValid = _rightTriggerRest.Count >= 40 && _rightTriggerSweep.Count >= 10 && rightTriggerMax >= 0.50;
            bool allComponentsValid = leftStickValid && rightStickValid && leftTriggerValid && rightTriggerValid;
            bool anyComponentValid = leftStickValid || rightStickValid || leftTriggerValid || rightTriggerValid;
            string confidence = allComponentsValid ? "Alta" : anyComponentValid ? "Parcial" : "Inconclusivo";
            bool buttonsHeldAtRest = _restSamples > 0 &&
                _restSamplesWithButtons / (double)_restSamples >= 0.80;
            var stuckButtons = new List<string>();
            if (_restSamples > 0)
            {
                foreach (GamepadDigitalButton button in DiagnosticButtons)
                {
                    int count = _restButtonCounts.TryGetValue(button, out int seen) ? seen : 0;
                    if (count / (double)_restSamples >= 0.80)
                        stuckButtons.Add(DisplayButton(button));
                }
            }

            var recommendation = new ControllerTuningRecommendation
            {
                LeftStickDeadzonePercent = leftStickValid ? RecommendInnerDeadzone(leftDrift + leftJitter) : 0,
                RightStickDeadzonePercent = rightStickValid ? RecommendInnerDeadzone(rightDrift + rightJitter) : 0,
                LeftStickOuterDeadzonePercent = leftStickValid && leftOuterReliable ? RecommendOuterDeadzone(leftCoverage) : 0,
                RightStickOuterDeadzonePercent = rightStickValid && rightOuterReliable ? RecommendOuterDeadzone(rightCoverage) : 0,
                LeftStickAntiDeadzonePercent = 0,
                RightStickAntiDeadzonePercent = 0,
                LeftTriggerDeadzonePercent = leftTriggerValid ? RecommendTriggerDeadzone(leftTriggerRest) : 0,
                RightTriggerDeadzonePercent = rightTriggerValid ? RecommendTriggerDeadzone(rightTriggerRest) : 0,
                StickResponseCurve = GamepadStickResponseCurve.Linear,
                ApplyLeftStick = leftStickValid,
                ApplyRightStick = rightStickValid,
                ApplyLeftTrigger = leftTriggerValid,
                ApplyRightTrigger = rightTriggerValid,
                ApplyStickResponseCurve = leftStickValid && rightStickValid
            };

            var warnings = new List<string>();
            if (!leftStickValid)
                warnings.Add("Analógico esquerdo não foi validado completamente. Ele não será alterado ao aplicar a recomendação.");
            else if (!leftSweepComplete)
                warnings.Add("O analógico esquerdo foi reconhecido, mas não atingiu alcance suficiente para calcular Outer L com segurança; somente a deadzone interna poderá ser corrigida.");
            if (!rightStickValid)
                warnings.Add("Analógico direito não foi validado completamente. Ele não será alterado ao aplicar a recomendação.");
            else if (!rightSweepComplete)
                warnings.Add("O analógico direito foi reconhecido, mas não atingiu alcance suficiente para calcular Outer R com segurança; somente a deadzone interna poderá ser corrigida.");
            if (leftDrift >= 0.25)
                warnings.Add("Drift severo no analógico esquerdo; software pode amenizar, mas o controle pode precisar de reparo.");
            if (rightDrift >= 0.25)
                warnings.Add("Drift severo no analógico direito; software pode amenizar, mas o controle pode precisar de reparo.");
            if (leftSweepComplete && !leftOuterReliable)
                warnings.Add($"Alcance assimétrico no analógico esquerdo ({leftAsymmetry * 100.0:0.0}% entre direções); por segurança, Outer L ficou em 0%. A correção global poderia piorar as direções saudáveis.");
            if (rightSweepComplete && !rightOuterReliable)
                warnings.Add($"Alcance assimétrico no analógico direito ({rightAsymmetry * 100.0:0.0}% entre direções); por segurança, Outer R ficou em 0%. A correção global poderia piorar as direções saudáveis.");
            if (!leftTriggerValid)
                warnings.Add("L2/LT não foi validado. O Leveling não alterará a deadzone desse gatilho.");
            else if (leftTriggerMax < 0.90)
                warnings.Add("L2/LT apresentou alcance baixo mesmo após a ação guiada; isso pode indicar desgaste físico.");
            if (!rightTriggerValid)
                warnings.Add("R2/RT não foi validado. O Leveling não alterará a deadzone desse gatilho.");
            else if (rightTriggerMax < 0.90)
                warnings.Add("R2/RT apresentou alcance baixo mesmo após a ação guiada; isso pode indicar desgaste físico.");
            if (buttonsHeldAtRest)
                warnings.Add(stuckButtons.Count > 0
                    ? $"Botão(ões) aparentemente preso(s) no repouso: {string.Join(", ", stuckButtons)}."
                    : "Um ou mais botões permaneceram pressionados durante a etapa de repouso.");

            if (!anyComponentValid)
                warnings.Add("Nenhum componente teve evidência suficiente para ajuste automático. Refazer o diagnóstico é recomendado.");

            int health = ComputeHealth(
                leftDrift, rightDrift,
                leftSweepComplete ? leftCoverage : 1.0,
                rightSweepComplete ? rightCoverage : 1.0,
                leftTriggerRest, rightTriggerRest,
                leftTriggerMax, rightTriggerMax,
                buttonsHeldAtRest);

            return new ControllerDiagnosticReport
            {
                ControllerName = _controllerName,
                LeftRestDriftPercent = leftDrift * 100.0,
                RightRestDriftPercent = rightDrift * 100.0,
                LeftJitterPercent = leftJitter * 100.0,
                RightJitterPercent = rightJitter * 100.0,
                LeftDirectionalCoveragePercent = leftCoverage * 100.0,
                RightDirectionalCoveragePercent = rightCoverage * 100.0,
                LeftDirectionalAsymmetryPercent = leftAsymmetry * 100.0,
                RightDirectionalAsymmetryPercent = rightAsymmetry * 100.0,
                LeftTriggerRestPercent = leftTriggerRest * 100.0,
                RightTriggerRestPercent = rightTriggerRest * 100.0,
                LeftTriggerMaxPercent = leftTriggerMax * 100.0,
                RightTriggerMaxPercent = rightTriggerMax * 100.0,
                LeftSweepComplete = leftSweepComplete,
                RightSweepComplete = rightSweepComplete,
                ButtonsHeldAtRest = buttonsHeldAtRest,
                LeftStickValid = leftStickValid,
                RightStickValid = rightStickValid,
                LeftTriggerValid = leftTriggerValid,
                RightTriggerValid = rightTriggerValid,
                HasActionableRecommendation = anyComponentValid,
                StuckButtons = stuckButtons,
                HealthScore = health,
                HealthLabel = HealthLabelFor(health),
                ConfidenceLabel = confidence,
                Warnings = warnings,
                Recommendation = recommendation
            };
        }

        private void CaptureControllerName(LocalRawGamepadSnapshot sample)
        {
            if (!string.IsNullOrWhiteSpace(sample.ControllerName))
                _controllerName = sample.ControllerName;
        }

        private static double RestDrift(List<(double X, double Y)> samples)
        {
            if (samples.Count == 0)
                return 0;
            double centerX = Median(samples.Select(v => v.X));
            double centerY = Median(samples.Select(v => v.Y));
            return Math.Min(1.0, Math.Sqrt(centerX * centerX + centerY * centerY));
        }

        private static double RestJitter(List<(double X, double Y)> samples)
        {
            if (samples.Count < 2)
                return 0;
            double centerX = Median(samples.Select(v => v.X));
            double centerY = Median(samples.Select(v => v.Y));
            var deviations = new List<double>(samples.Count);
            foreach ((double x, double y) in samples)
            {
                double dx = x - centerX;
                double dy = y - centerY;
                deviations.Add(Math.Min(1.0, Math.Sqrt(dx * dx + dy * dy)));
            }
            return Percentile(deviations, 0.95);
        }

        private static double DirectionalCoverage(List<(double X, double Y)> samples)
        {
            if (samples.Count == 0)
                return 0;
            double positiveX = Math.Max(0, samples.Max(v => v.X));
            double negativeX = Math.Max(0, -samples.Min(v => v.X));
            double positiveY = Math.Max(0, samples.Max(v => v.Y));
            double negativeY = Math.Max(0, -samples.Min(v => v.Y));
            return Math.Clamp(Math.Min(Math.Min(positiveX, negativeX), Math.Min(positiveY, negativeY)), 0.0, 1.0);
        }

        private static double DirectionalAsymmetry(List<(double X, double Y)> samples)
        {
            if (samples.Count == 0)
                return 0;
            double[] reaches =
            {
                Math.Max(0, samples.Max(v => v.X)),
                Math.Max(0, -samples.Min(v => v.X)),
                Math.Max(0, samples.Max(v => v.Y)),
                Math.Max(0, -samples.Min(v => v.Y))
            };
            return Math.Clamp(reaches.Max() - reaches.Min(), 0.0, 1.0);
        }

        private static bool SweepComplete(double directionalCoverage) => directionalCoverage >= CompleteSweepCoverage;

        private static int RecommendInnerDeadzone(double centerPlusJitter)
        {
            // Around 0-2% of raw center noise is common enough that an automatic
            // correction would often be more intrusive than useful. Above that,
            // cover the measured center displacement + jitter with ~1% margin.
            if (centerPlusJitter <= 0.020)
                return 0;
            return Math.Clamp((int)Math.Ceiling((centerPlusJitter * 100.0) + 1.0), 0, 40);
        }

        private static int RecommendOuterDeadzone(double coverage)
        {
            if (coverage >= 0.97)
                return 0;
            return Math.Clamp((int)Math.Ceiling(((1.0 - coverage) * 100.0) + 1.0), 0, 20);
        }

        private static int RecommendTriggerDeadzone(double rest)
        {
            if (rest <= 0.01)
                return 0;
            return Math.Clamp((int)Math.Ceiling((rest * 100.0) + 1.0), 0, 40);
        }

        private static int ComputeHealth(
            double leftDrift, double rightDrift,
            double leftCoverage, double rightCoverage,
            double leftTriggerRest, double rightTriggerRest,
            double leftTriggerMax, double rightTriggerMax,
            bool buttonsHeldAtRest)
        {
            double penalty = 0;
            penalty += Math.Min(22, leftDrift * 100.0 * 1.35);
            penalty += Math.Min(22, rightDrift * 100.0 * 1.35);
            penalty += Math.Min(12, Math.Max(0, 1.0 - leftCoverage) * 100.0 * 0.8);
            penalty += Math.Min(12, Math.Max(0, 1.0 - rightCoverage) * 100.0 * 0.8);
            penalty += Math.Min(8, leftTriggerRest * 100.0 * 0.8);
            penalty += Math.Min(8, rightTriggerRest * 100.0 * 0.8);
            if (leftTriggerMax > 0)
                penalty += Math.Min(5, Math.Max(0, 0.95 - leftTriggerMax) * 100.0 * 0.5);
            if (rightTriggerMax > 0)
                penalty += Math.Min(5, Math.Max(0, 0.95 - rightTriggerMax) * 100.0 * 0.5);
            if (buttonsHeldAtRest)
                penalty += 15;
            return Math.Clamp((int)Math.Round(100.0 - penalty), 0, 100);
        }

        private static string DisplayButton(GamepadDigitalButton button) => button switch
        {
            GamepadDigitalButton.DpadUp => "D-pad ↑",
            GamepadDigitalButton.DpadDown => "D-pad ↓",
            GamepadDigitalButton.DpadLeft => "D-pad ←",
            GamepadDigitalButton.DpadRight => "D-pad →",
            GamepadDigitalButton.Start => "Start/Menu",
            GamepadDigitalButton.Back => "Back/View",
            _ => button.ToString()
        };

        private static string HealthLabelFor(int health) => health switch
        {
            >= 90 => "Excelente",
            >= 75 => "Bom",
            >= 55 => "Atenção",
            _ => "Desgastado"
        };

        private static double Median(IEnumerable<double> values)
        {
            double[] sorted = values.OrderBy(v => v).ToArray();
            if (sorted.Length == 0)
                return 0;
            int middle = sorted.Length / 2;
            return sorted.Length % 2 == 0
                ? (sorted[middle - 1] + sorted[middle]) / 2.0
                : sorted[middle];
        }

        private static double Percentile(List<double> values, double percentile)
        {
            if (values.Count == 0)
                return 0;
            double[] sorted = values.OrderBy(v => v).ToArray();
            double rank = Math.Clamp(percentile, 0.0, 1.0) * (sorted.Length - 1);
            int lower = (int)Math.Floor(rank);
            int upper = (int)Math.Ceiling(rank);
            if (lower == upper)
                return sorted[lower];
            double fraction = rank - lower;
            return sorted[lower] + ((sorted[upper] - sorted[lower]) * fraction);
        }

        private static double NormalizeAxis(short value) =>
            value == short.MinValue ? -1.0 : Math.Clamp(value / 32767.0, -1.0, 1.0);

        private static double NormalizeTrigger(byte value) => value / 255.0;
    }
}
