using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiverse.Services
{
    internal enum RawHidDirection
    {
        Left,
        Right,
        Up,
        Down
    }

    internal readonly struct RawHidObservation
    {
        public RawHidObservation(int axisIndex, double rawValue, double strength)
        {
            AxisIndex = axisIndex;
            RawValue = rawValue;
            Strength = strength;
        }

        public int AxisIndex { get; }
        public double RawValue { get; }
        public double Strength { get; }
    }

    /// <summary>
    /// Learns the semantic meaning of unnamed RawGameController axes from guided
    /// user actions. RawGameController reports normalized 0..1 axes but does not
    /// guarantee which index represents a particular physical control.
    /// </summary>
    internal sealed class RawHidDiagnosticMapper
    {
        private const double MinimumAxisMovement = 0.22;
        private const double DominanceMargin = 0.08;
        private const double ReleasedTriggerThreshold = 0.14;

        private sealed class StickAxisCalibration
        {
            public int AxisIndex { get; init; }
            public int FirstDeltaSign { get; init; }
            public bool PositiveIncreasing { get; set; }
            public bool HasNegative { get; set; }
            public bool HasPositive { get; set; }
        }

        private sealed class TriggerCalibration
        {
            public int AxisIndex { get; init; }
            public bool PressedIncreasing { get; init; }
        }

        private readonly int _axisCount;
        private double[] _baseline;
        private StickAxisCalibration? _leftX;
        private StickAxisCalibration? _leftY;
        private StickAxisCalibration? _rightX;
        private StickAxisCalibration? _rightY;
        private TriggerCalibration? _leftTrigger;
        private TriggerCalibration? _rightTrigger;

        public RawHidDiagnosticMapper(int axisCount)
        {
            _axisCount = Math.Max(0, axisCount);
            _baseline = new double[_axisCount];
        }

        public bool BaselineReady { get; private set; }
        public int AxisCount => _axisCount;
        public bool IsStickMapped(bool left) => left
            ? IsComplete(_leftX) && IsComplete(_leftY)
            : IsComplete(_rightX) && IsComplete(_rightY);
        public bool IsTriggerMapped(bool left) => left ? _leftTrigger is not null : _rightTrigger is not null;

        public string MappingSummary
        {
            get
            {
                string Axis(StickAxisCalibration? a) => a is null ? "--" : a.AxisIndex.ToString();
                string Trigger(TriggerCalibration? a) => a is null ? "--" : a.AxisIndex.ToString();
                return $"LX eixo {Axis(_leftX)} • LY eixo {Axis(_leftY)} • RX eixo {Axis(_rightX)} • RY eixo {Axis(_rightY)} • L2 eixo {Trigger(_leftTrigger)} • R2 eixo {Trigger(_rightTrigger)}";
            }
        }

        public void SetBaseline(IEnumerable<LocalRawGamepadSnapshot> samples)
        {
            var valid = samples
                .Where(s => s.RawAxes is not null && s.RawAxes.Length == _axisCount)
                .Select(s => s.RawAxes)
                .ToList();
            if (_axisCount == 0 || valid.Count == 0)
                throw new InvalidOperationException("Não há eixos HID suficientes para estabelecer o repouso do controle.");

            for (int axis = 0; axis < _axisCount; axis++)
            {
                double[] values = valid.Select(v => Math.Clamp(v[axis], 0.0, 1.0)).OrderBy(v => v).ToArray();
                int middle = values.Length / 2;
                _baseline[axis] = values.Length % 2 == 0
                    ? (values[middle - 1] + values[middle]) / 2.0
                    : values[middle];
            }
            BaselineReady = true;
        }

        public bool TryObserveStickDirection(
            bool left,
            RawHidDirection direction,
            LocalRawGamepadSnapshot sample,
            out RawHidObservation observation)
        {
            observation = default;
            if (!BaselineReady || sample.RawAxes.Length != _axisCount)
                return false;

            StickAxisCalibration? existing = GetAxisCalibration(left, direction);
            if (existing is not null)
            {
                double delta = sample.RawAxes[existing.AxisIndex] - _baseline[existing.AxisIndex];
                int sign = Math.Sign(delta);
                bool directionAlreadyKnown = DirectionHasEndpoint(existing, direction);
                bool signMatches = directionAlreadyKnown
                    ? sign == ExpectedSign(existing, direction)
                    : sign != 0 && sign != existing.FirstDeltaSign;
                if (!signMatches || Math.Abs(delta) < MinimumAxisMovement)
                    return false;

                observation = new RawHidObservation(existing.AxisIndex, sample.RawAxes[existing.AxisIndex], Math.Abs(delta));
                return true;
            }

            HashSet<int> excluded = ReservedAxisIndexes();
            int bestAxis = -1;
            double best = 0;
            double second = 0;
            for (int axis = 0; axis < _axisCount; axis++)
            {
                if (excluded.Contains(axis))
                    continue;
                double delta = Math.Abs(sample.RawAxes[axis] - _baseline[axis]);
                if (delta > best)
                {
                    second = best;
                    best = delta;
                    bestAxis = axis;
                }
                else if (delta > second)
                {
                    second = delta;
                }
            }

            if (bestAxis < 0 || best < MinimumAxisMovement || best - second < DominanceMargin)
                return false;

            observation = new RawHidObservation(bestAxis, sample.RawAxes[bestAxis], best);
            return true;
        }

        public bool CommitStickDirection(
            bool left,
            RawHidDirection direction,
            RawHidObservation observation,
            out string message)
        {
            message = string.Empty;
            if (!BaselineReady || observation.AxisIndex < 0 || observation.AxisIndex >= _axisCount)
            {
                message = "A leitura HID não é válida para esta direção.";
                return false;
            }

            bool horizontal = direction is RawHidDirection.Left or RawHidDirection.Right;
            StickAxisCalibration? calibration = horizontal
                ? (left ? _leftX : _rightX)
                : (left ? _leftY : _rightY);
            double delta = observation.RawValue - _baseline[observation.AxisIndex];
            int sign = Math.Sign(delta);
            if (sign == 0)
            {
                message = "O eixo não se afastou do repouso o suficiente.";
                return false;
            }

            if (calibration is null)
            {
                calibration = new StickAxisCalibration
                {
                    AxisIndex = observation.AxisIndex,
                    FirstDeltaSign = sign,
                    PositiveIncreasing = IsPositiveSemantic(direction) ? sign > 0 : sign < 0
                };
                if (horizontal)
                {
                    if (left) _leftX = calibration; else _rightX = calibration;
                }
                else
                {
                    if (left) _leftY = calibration; else _rightY = calibration;
                }
            }
            else if (calibration.AxisIndex != observation.AxisIndex)
            {
                message = $"A direção respondeu no eixo {observation.AxisIndex}, mas o par já estava associado ao eixo {calibration.AxisIndex}. Repita o movimento de forma reta.";
                return false;
            }
            else if (!DirectionHasEndpoint(calibration, direction) && sign == calibration.FirstDeltaSign)
            {
                message = "As duas direções responderam para o mesmo lado do eixo. Solte o analógico e repita a direção indicada.";
                return false;
            }

            if (IsPositiveSemantic(direction))
            {
                calibration.HasPositive = true;
                calibration.PositiveIncreasing = sign > 0;
            }
            else
            {
                calibration.HasNegative = true;
                if (!calibration.HasPositive)
                    calibration.PositiveIncreasing = sign < 0;
            }

            message = $"Eixo HID {observation.AxisIndex} identificado para {DisplayDirection(direction)}.";
            return true;
        }

        public bool TryObserveTriggerPress(bool left, LocalRawGamepadSnapshot sample, out RawHidObservation observation)
        {
            observation = default;
            if (!BaselineReady || sample.RawAxes.Length != _axisCount)
                return false;

            TriggerCalibration? existing = left ? _leftTrigger : _rightTrigger;
            if (existing is not null)
            {
                double delta = sample.RawAxes[existing.AxisIndex] - _baseline[existing.AxisIndex];
                if (Math.Abs(delta) < MinimumAxisMovement)
                    return false;
                observation = new RawHidObservation(existing.AxisIndex, sample.RawAxes[existing.AxisIndex], Math.Abs(delta));
                return true;
            }

            HashSet<int> excluded = ReservedAxisIndexes();
            int bestAxis = -1;
            double best = 0;
            double second = 0;
            for (int axis = 0; axis < _axisCount; axis++)
            {
                if (excluded.Contains(axis))
                    continue;
                double delta = Math.Abs(sample.RawAxes[axis] - _baseline[axis]);
                if (delta > best)
                {
                    second = best;
                    best = delta;
                    bestAxis = axis;
                }
                else if (delta > second)
                {
                    second = delta;
                }
            }

            if (bestAxis < 0 || best < MinimumAxisMovement || best - second < DominanceMargin)
                return false;

            observation = new RawHidObservation(bestAxis, sample.RawAxes[bestAxis], best);
            return true;
        }

        public bool CommitTrigger(bool left, RawHidObservation observation, out string message)
        {
            message = string.Empty;
            if (!BaselineReady || observation.AxisIndex < 0 || observation.AxisIndex >= _axisCount)
            {
                message = "A leitura HID do gatilho não é válida.";
                return false;
            }

            double delta = observation.RawValue - _baseline[observation.AxisIndex];
            if (Math.Abs(delta) < MinimumAxisMovement)
            {
                message = "O gatilho não se afastou do repouso o suficiente.";
                return false;
            }

            var calibration = new TriggerCalibration
            {
                AxisIndex = observation.AxisIndex,
                PressedIncreasing = delta > 0
            };
            if (left) _leftTrigger = calibration; else _rightTrigger = calibration;
            message = $"Eixo HID {observation.AxisIndex} identificado para {(left ? "L2 / LT" : "R2 / RT")}.";
            return true;
        }

        public bool IsTriggerReleased(bool left, LocalRawGamepadSnapshot sample, out double normalizedValue)
        {
            TriggerCalibration? calibration = left ? _leftTrigger : _rightTrigger;
            if (calibration is null || sample.RawAxes.Length != _axisCount)
            {
                normalizedValue = 0;
                return false;
            }

            normalizedValue = MapTrigger(calibration, sample.RawAxes[calibration.AxisIndex]);
            return normalizedValue <= ReleasedTriggerThreshold;
        }

        public LocalRawGamepadSnapshot MapSnapshot(LocalRawGamepadSnapshot raw)
        {
            if (raw.RawAxes.Length != _axisCount)
                return raw;

            (short lx, short ly) = MapStick(_leftX, _leftY, raw.RawAxes);
            (short rx, short ry) = MapStick(_rightX, _rightY, raw.RawAxes);
            byte lt = _leftTrigger is null ? (byte)0 : ToTriggerByte(MapTrigger(_leftTrigger, raw.RawAxes[_leftTrigger.AxisIndex]));
            byte rt = _rightTrigger is null ? (byte)0 : ToTriggerByte(MapTrigger(_rightTrigger, raw.RawAxes[_rightTrigger.AxisIndex]));

            return new LocalRawGamepadSnapshot
            {
                Connected = raw.Connected,
                UserIndex = raw.UserIndex,
                ControllerName = raw.ControllerName,
                ControllerIdentity = raw.ControllerIdentity,
                Backend = raw.Backend + " • eixos aprendidos",
                Buttons = raw.Buttons,
                LeftTrigger = lt,
                RightTrigger = rt,
                LeftThumbX = lx,
                LeftThumbY = ly,
                RightThumbX = rx,
                RightThumbY = ry,
                RawAxes = raw.RawAxes,
                RawAxisCount = raw.RawAxisCount,
                RawButtonCount = raw.RawButtonCount,
                RawSwitchCount = raw.RawSwitchCount,
                HardwareVendorId = raw.HardwareVendorId,
                HardwareProductId = raw.HardwareProductId
            };
        }

        private StickAxisCalibration? GetAxisCalibration(bool left, RawHidDirection direction)
        {
            bool horizontal = direction is RawHidDirection.Left or RawHidDirection.Right;
            return horizontal ? (left ? _leftX : _rightX) : (left ? _leftY : _rightY);
        }

        private HashSet<int> ReservedAxisIndexes()
        {
            var result = new HashSet<int>();
            void Add(StickAxisCalibration? a) { if (a is not null) result.Add(a.AxisIndex); }
            Add(_leftX); Add(_leftY); Add(_rightX); Add(_rightY);
            if (_leftTrigger is not null) result.Add(_leftTrigger.AxisIndex);
            if (_rightTrigger is not null) result.Add(_rightTrigger.AxisIndex);
            return result;
        }

        private static bool IsComplete(StickAxisCalibration? axis) =>
            axis is not null && axis.HasNegative && axis.HasPositive;

        private static bool IsPositiveSemantic(RawHidDirection direction) =>
            direction is RawHidDirection.Right or RawHidDirection.Up;

        private static bool DirectionHasEndpoint(StickAxisCalibration axis, RawHidDirection direction) =>
            IsPositiveSemantic(direction) ? axis.HasPositive : axis.HasNegative;

        private static int ExpectedSign(StickAxisCalibration axis, RawHidDirection direction)
        {
            bool semanticPositive = IsPositiveSemantic(direction);
            bool increasing = semanticPositive ? axis.PositiveIncreasing : !axis.PositiveIncreasing;
            return increasing ? 1 : -1;
        }

        private static string DisplayDirection(RawHidDirection direction) => direction switch
        {
            RawHidDirection.Left => "esquerda",
            RawHidDirection.Right => "direita",
            RawHidDirection.Up => "cima",
            _ => "baixo"
        };

        private static (short X, short Y) MapStick(
            StickAxisCalibration? xAxis,
            StickAxisCalibration? yAxis,
            double[] axes)
        {
            if (!IsComplete(xAxis) || !IsComplete(yAxis))
                return (0, 0);
            double x = MapCenteredAxis(xAxis!, axes[xAxis!.AxisIndex]);
            double y = MapCenteredAxis(yAxis!, axes[yAxis!.AxisIndex]);
            return (ToStickShort(x), ToStickShort(y));
        }

        private static double MapCenteredAxis(StickAxisCalibration calibration, double raw)
        {
            // RawGameController axes are normalized to 0..1. 0.5 is the nominal
            // hardware centre; keeping that centre (instead of zeroing on the sampled
            // rest value) is what lets the diagnostic measure real centre drift.
            double normalized = (Math.Clamp(raw, 0.0, 1.0) - 0.5) * 2.0;
            if (!calibration.PositiveIncreasing)
                normalized = -normalized;
            return Math.Clamp(normalized, -1.0, 1.0);
        }

        private static double MapTrigger(TriggerCalibration calibration, double raw)
        {
            double value = Math.Clamp(raw, 0.0, 1.0);
            return calibration.PressedIncreasing ? value : 1.0 - value;
        }

        private static short ToStickShort(double value)
        {
            double clamped = Math.Clamp(value, -1.0, 1.0);
            if (clamped <= -1.0)
                return short.MinValue;
            return (short)Math.Round(clamped * short.MaxValue);
        }

        private static byte ToTriggerByte(double value) =>
            (byte)Math.Round(Math.Clamp(value, 0.0, 1.0) * byte.MaxValue);
    }
}
