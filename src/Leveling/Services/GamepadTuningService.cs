using System;
using System.Collections.Generic;

namespace Multiverse.Services
{

    internal enum GamepadDigitalButton : ushort
    {
        None = 0x0000,
        DpadUp = 0x0001,
        DpadDown = 0x0002,
        DpadLeft = 0x0004,
        DpadRight = 0x0008,
        Start = 0x0010,
        Back = 0x0020,
        L3 = 0x0040,
        R3 = 0x0080,
        LB = 0x0100,
        RB = 0x0200,
        A = 0x1000,
        B = 0x2000,
        X = 0x4000,
        Y = 0x8000
    }

    internal enum GamepadStickResponseCurve
    {
        Linear = 0,
        Precision = 1,
        Fast = 2
    }


    internal sealed class GamepadTuningSnapshot
    {
        public int LeftStickDeadzonePercent { get; init; }
        public int RightStickDeadzonePercent { get; init; }
        public int LeftStickOuterDeadzonePercent { get; init; }
        public int RightStickOuterDeadzonePercent { get; init; }
        public int LeftStickAntiDeadzonePercent { get; init; }
        public int RightStickAntiDeadzonePercent { get; init; }
        public int LeftTriggerDeadzonePercent { get; init; }
        public int RightTriggerDeadzonePercent { get; init; }
        public GamepadStickResponseCurve StickResponseCurve { get; init; }
        public bool InvertLeftY { get; init; }
        public bool InvertRightY { get; init; }
    }

    /// <summary>
    /// Persistent controller routing and client-side XInput shaping.
    /// Defaults preserve the proven input path while routing the remote client to
    /// P2 by default; Auto and strict P1-P4 remain user-selectable.
    /// </summary>
    internal sealed class GamepadTuningService
    {
        private static readonly Lazy<GamepadTuningService> LazyInstance =
            new(() => new GamepadTuningService());

        public static GamepadTuningService Instance => LazyInstance.Value;

        private const string ClientSourceKey = "Multiverse.Gamepad.ClientControllerSource";
        private const string HostSlotKey = "Multiverse.Gamepad.HostPlayerSlot";
        private const string RemoteEnabledKey = "Multiverse.Gamepad.RemoteInputEnabled";

        private const string LeftKey = "Multiverse.Gamepad.LeftStickDeadzonePercent";
        private const string RightKey = "Multiverse.Gamepad.RightStickDeadzonePercent";
        private const string LegacyTriggerKey = "Multiverse.Gamepad.TriggerDeadzonePercent";
        private const string LeftOuterKey = "Multiverse.Gamepad.LeftStickOuterDeadzonePercent";
        private const string RightOuterKey = "Multiverse.Gamepad.RightStickOuterDeadzonePercent";
        private const string LeftAntiKey = "Multiverse.Gamepad.LeftStickAntiDeadzonePercent";
        private const string RightAntiKey = "Multiverse.Gamepad.RightStickAntiDeadzonePercent";
        private const string LeftTriggerKey = "Multiverse.Gamepad.LeftTriggerDeadzonePercent";
        private const string RightTriggerKey = "Multiverse.Gamepad.RightTriggerDeadzonePercent";
        private const string CurveKey = "Multiverse.Gamepad.StickResponseCurve";
        private const string InvertLeftYKey = "Multiverse.Gamepad.InvertLeftY";
        private const string InvertRightYKey = "Multiverse.Gamepad.InvertRightY";
        private const string ButtonMappingKeyPrefix = "Multiverse.Gamepad.ButtonMap.";

        private static readonly GamepadDigitalButton[] DigitalButtons =
        {
            GamepadDigitalButton.DpadUp,
            GamepadDigitalButton.DpadDown,
            GamepadDigitalButton.DpadLeft,
            GamepadDigitalButton.DpadRight,
            GamepadDigitalButton.Start,
            GamepadDigitalButton.Back,
            GamepadDigitalButton.L3,
            GamepadDigitalButton.R3,
            GamepadDigitalButton.LB,
            GamepadDigitalButton.RB,
            GamepadDigitalButton.A,
            GamepadDigitalButton.B,
            GamepadDigitalButton.X,
            GamepadDigitalButton.Y
        };

        private readonly MultiverseSettingsService _settings = MultiverseSettingsService.Instance;
        private readonly object _sync = new();

        // -2 = DualSense/HID, -1 = Auto, 0..3 = XInput 1..4.
        private int _clientSource;
        // 0 = Auto, 1..4 = strict host player slot.
        private int _hostSlot;
        private bool _remoteEnabled;

        private int _left;
        private int _right;
        private int _leftOuter;
        private int _rightOuter;
        private int _leftAnti;
        private int _rightAnti;
        private int _leftTrigger;
        private int _rightTrigger;
        private GamepadStickResponseCurve _curve;
        private bool _invertLeftY;
        private bool _invertRightY;
        private readonly Dictionary<GamepadDigitalButton, GamepadDigitalButton> _buttonMappings = new();

        public event EventHandler? Changed;

        private GamepadTuningService()
        {
            _clientSource = NormalizeClientSource(_settings.GetInt(ClientSourceKey, -1));
            _hostSlot = NormalizeHostSlot(_settings.GetInt(HostSlotKey, 2));
            _remoteEnabled = _settings.GetBool(RemoteEnabledKey, true);

            _left = Math.Clamp(_settings.GetInt(LeftKey, 0), 0, 40);
            _right = Math.Clamp(_settings.GetInt(RightKey, 0), 0, 40);
            _leftOuter = Math.Clamp(_settings.GetInt(LeftOuterKey, 0), 0, 20);
            _rightOuter = Math.Clamp(_settings.GetInt(RightOuterKey, 0), 0, 20);
            _leftAnti = Math.Clamp(_settings.GetInt(LeftAntiKey, 0), 0, 30);
            _rightAnti = Math.Clamp(_settings.GetInt(RightAntiKey, 0), 0, 30);

            int legacyTrigger = Math.Clamp(_settings.GetInt(LegacyTriggerKey, 0), 0, 40);
            _leftTrigger = Math.Clamp(_settings.GetInt(LeftTriggerKey, legacyTrigger), 0, 40);
            _rightTrigger = Math.Clamp(_settings.GetInt(RightTriggerKey, legacyTrigger), 0, 40);

            _curve = NormalizeCurve(_settings.GetString(CurveKey, "Linear"));
            _invertLeftY = _settings.GetBool(InvertLeftYKey, false);
            _invertRightY = _settings.GetBool(InvertRightYKey, false);

            foreach (GamepadDigitalButton source in DigitalButtons)
            {
                string stored = _settings.GetString(ButtonMappingKeyPrefix + source, "");
                if (Enum.TryParse(stored, true, out GamepadDigitalButton target) &&
                    Enum.IsDefined(typeof(GamepadDigitalButton), target))
                {
                    _buttonMappings[source] = target;
                }
                else
                {
                    _buttonMappings[source] = source;
                }
            }
        }

        public int ClientControllerSource { get { lock (_sync) return _clientSource; } }
        public int HostPlayerSlot { get { lock (_sync) return _hostSlot; } }
        public bool RemoteInputEnabled { get { lock (_sync) return _remoteEnabled; } }

        public int LeftStickDeadzonePercent { get { lock (_sync) return _left; } }
        public int RightStickDeadzonePercent { get { lock (_sync) return _right; } }
        public int LeftStickOuterDeadzonePercent { get { lock (_sync) return _leftOuter; } }
        public int RightStickOuterDeadzonePercent { get { lock (_sync) return _rightOuter; } }
        public int LeftStickAntiDeadzonePercent { get { lock (_sync) return _leftAnti; } }
        public int RightStickAntiDeadzonePercent { get { lock (_sync) return _rightAnti; } }
        public int LeftTriggerDeadzonePercent { get { lock (_sync) return _leftTrigger; } }
        public int RightTriggerDeadzonePercent { get { lock (_sync) return _rightTrigger; } }
        public GamepadStickResponseCurve StickResponseCurve { get { lock (_sync) return _curve; } }
        public bool InvertLeftY { get { lock (_sync) return _invertLeftY; } }
        public bool InvertRightY { get { lock (_sync) return _invertRightY; } }
        public bool HasCustomButtonMappings
        {
            get
            {
                lock (_sync)
                {
                    foreach (GamepadDigitalButton source in DigitalButtons)
                    {
                        if (_buttonMappings[source] != source)
                            return true;
                    }
                    return false;
                }
            }
        }

        // Compatibility alias for older UI/code paths.
        public int TriggerDeadzonePercent => Math.Max(LeftTriggerDeadzonePercent, RightTriggerDeadzonePercent);

        public GamepadDigitalButton GetButtonMapping(GamepadDigitalButton source)
        {
            if (source == GamepadDigitalButton.None || !Enum.IsDefined(typeof(GamepadDigitalButton), source))
                return source;

            lock (_sync)
            {
                return _buttonMappings.TryGetValue(source, out GamepadDigitalButton target)
                    ? target
                    : source;
            }
        }

        public void SetButtonMapping(GamepadDigitalButton source, GamepadDigitalButton target)
        {
            if (source == GamepadDigitalButton.None || !Enum.IsDefined(typeof(GamepadDigitalButton), source))
                return;
            if (!Enum.IsDefined(typeof(GamepadDigitalButton), target))
                target = source;

            bool changed;
            lock (_sync)
            {
                GamepadDigitalButton current = _buttonMappings.TryGetValue(source, out GamepadDigitalButton saved)
                    ? saved
                    : source;
                changed = current != target;
                _buttonMappings[source] = target;
            }

            if (!changed)
                return;

            if (target == source)
                _settings.Set(ButtonMappingKeyPrefix + source, null);
            else
                _settings.Set(ButtonMappingKeyPrefix + source, target.ToString());

            Changed?.Invoke(this, EventArgs.Empty);
        }

        public ushort MapButtons(ushort rawButtons)
        {
            ushort mapped = 0;
            lock (_sync)
            {
                foreach (GamepadDigitalButton source in DigitalButtons)
                {
                    ushort sourceMask = (ushort)source;
                    if ((rawButtons & sourceMask) == 0)
                        continue;

                    GamepadDigitalButton target = _buttonMappings.TryGetValue(source, out GamepadDigitalButton saved)
                        ? saved
                        : source;
                    mapped |= (ushort)target;
                }
            }
            return mapped;
        }

        public void ResetButtonMappings()
        {
            bool changed = false;
            lock (_sync)
            {
                foreach (GamepadDigitalButton source in DigitalButtons)
                {
                    if (_buttonMappings.TryGetValue(source, out GamepadDigitalButton target) && target != source)
                        changed = true;
                    _buttonMappings[source] = source;
                }
            }

            foreach (GamepadDigitalButton source in DigitalButtons)
                _settings.Set(ButtonMappingKeyPrefix + source, null);

            if (changed)
                Changed?.Invoke(this, EventArgs.Empty);
        }

        public void SetClientControllerSource(int value) =>
            SetInt(ref _clientSource, ClientSourceKey, NormalizeClientSource(value));

        public void SetHostPlayerSlot(int value) =>
            SetInt(ref _hostSlot, HostSlotKey, NormalizeHostSlot(value));

        public void SetRemoteInputEnabled(bool value) =>
            SetBool(ref _remoteEnabled, RemoteEnabledKey, value);

        public void SetLeftStickDeadzonePercent(int value) =>
            SetInt(ref _left, LeftKey, Math.Clamp(value, 0, 40));

        public void SetRightStickDeadzonePercent(int value) =>
            SetInt(ref _right, RightKey, Math.Clamp(value, 0, 40));

        public void SetLeftStickOuterDeadzonePercent(int value) =>
            SetInt(ref _leftOuter, LeftOuterKey, Math.Clamp(value, 0, 20));

        public void SetRightStickOuterDeadzonePercent(int value) =>
            SetInt(ref _rightOuter, RightOuterKey, Math.Clamp(value, 0, 20));

        public void SetLeftStickAntiDeadzonePercent(int value) =>
            SetInt(ref _leftAnti, LeftAntiKey, Math.Clamp(value, 0, 30));

        public void SetRightStickAntiDeadzonePercent(int value) =>
            SetInt(ref _rightAnti, RightAntiKey, Math.Clamp(value, 0, 30));

        public void SetLeftTriggerDeadzonePercent(int value) =>
            SetInt(ref _leftTrigger, LeftTriggerKey, Math.Clamp(value, 0, 40));

        public void SetRightTriggerDeadzonePercent(int value) =>
            SetInt(ref _rightTrigger, RightTriggerKey, Math.Clamp(value, 0, 40));

        public void SetTriggerDeadzonePercent(int value)
        {
            SetLeftTriggerDeadzonePercent(value);
            SetRightTriggerDeadzonePercent(value);
        }

        public void SetStickResponseCurve(GamepadStickResponseCurve value)
        {
            GamepadStickResponseCurve normalized = Enum.IsDefined(typeof(GamepadStickResponseCurve), value)
                ? value
                : GamepadStickResponseCurve.Linear;
            bool changed;
            lock (_sync)
            {
                changed = _curve != normalized;
                _curve = normalized;
            }
            if (!changed) return;
            _settings.Set(CurveKey, normalized.ToString());
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void SetInvertLeftY(bool value) =>
            SetBool(ref _invertLeftY, InvertLeftYKey, value);

        public void SetInvertRightY(bool value) =>
            SetBool(ref _invertRightY, InvertRightYKey, value);

        public GamepadTuningSnapshot CaptureClientTuningSnapshot()
        {
            lock (_sync)
            {
                return new GamepadTuningSnapshot
                {
                    LeftStickDeadzonePercent = _left,
                    RightStickDeadzonePercent = _right,
                    LeftStickOuterDeadzonePercent = _leftOuter,
                    RightStickOuterDeadzonePercent = _rightOuter,
                    LeftStickAntiDeadzonePercent = _leftAnti,
                    RightStickAntiDeadzonePercent = _rightAnti,
                    LeftTriggerDeadzonePercent = _leftTrigger,
                    RightTriggerDeadzonePercent = _rightTrigger,
                    StickResponseCurve = _curve,
                    InvertLeftY = _invertLeftY,
                    InvertRightY = _invertRightY
                };
            }
        }

        public void ApplyDiagnosticRecommendation(ControllerTuningRecommendation recommendation)
        {
            if (recommendation is null)
                return;

            // A guided diagnostic may validate only part of the controller. Never
            // overwrite an unmeasured component with a guessed/default value.
            if (recommendation.ApplyLeftStick)
            {
                SetLeftStickDeadzonePercent(recommendation.LeftStickDeadzonePercent);
                SetLeftStickOuterDeadzonePercent(recommendation.LeftStickOuterDeadzonePercent);
                SetLeftStickAntiDeadzonePercent(recommendation.LeftStickAntiDeadzonePercent);
            }

            if (recommendation.ApplyRightStick)
            {
                SetRightStickDeadzonePercent(recommendation.RightStickDeadzonePercent);
                SetRightStickOuterDeadzonePercent(recommendation.RightStickOuterDeadzonePercent);
                SetRightStickAntiDeadzonePercent(recommendation.RightStickAntiDeadzonePercent);
            }

            if (recommendation.ApplyLeftTrigger)
                SetLeftTriggerDeadzonePercent(recommendation.LeftTriggerDeadzonePercent);
            if (recommendation.ApplyRightTrigger)
                SetRightTriggerDeadzonePercent(recommendation.RightTriggerDeadzonePercent);
            if (recommendation.ApplyStickResponseCurve)
                SetStickResponseCurve(recommendation.StickResponseCurve);
        }

        public void RestoreClientTuningSnapshot(GamepadTuningSnapshot snapshot)
        {
            if (snapshot is null)
                return;

            SetLeftStickDeadzonePercent(snapshot.LeftStickDeadzonePercent);
            SetRightStickDeadzonePercent(snapshot.RightStickDeadzonePercent);
            SetLeftStickOuterDeadzonePercent(snapshot.LeftStickOuterDeadzonePercent);
            SetRightStickOuterDeadzonePercent(snapshot.RightStickOuterDeadzonePercent);
            SetLeftStickAntiDeadzonePercent(snapshot.LeftStickAntiDeadzonePercent);
            SetRightStickAntiDeadzonePercent(snapshot.RightStickAntiDeadzonePercent);
            SetLeftTriggerDeadzonePercent(snapshot.LeftTriggerDeadzonePercent);
            SetRightTriggerDeadzonePercent(snapshot.RightTriggerDeadzonePercent);
            SetStickResponseCurve(snapshot.StickResponseCurve);
            SetInvertLeftY(snapshot.InvertLeftY);
            SetInvertRightY(snapshot.InvertRightY);
        }

        public void Reset()
        {
            SetClientControllerSource(-1);
            SetHostPlayerSlot(2);
            SetRemoteInputEnabled(true);
            SetLeftStickDeadzonePercent(0);
            SetRightStickDeadzonePercent(0);
            SetLeftStickOuterDeadzonePercent(0);
            SetRightStickOuterDeadzonePercent(0);
            SetLeftStickAntiDeadzonePercent(0);
            SetRightStickAntiDeadzonePercent(0);
            SetLeftTriggerDeadzonePercent(0);
            SetRightTriggerDeadzonePercent(0);
            SetStickResponseCurve(GamepadStickResponseCurve.Linear);
            SetInvertLeftY(false);
            SetInvertRightY(false);
            ResetButtonMappings();
            _settings.Set(LegacyTriggerKey, null);
        }

        private void SetInt(ref int target, string key, int value)
        {
            bool changed;
            lock (_sync)
            {
                changed = target != value;
                target = value;
            }
            if (!changed) return;
            _settings.Set(key, value.ToString());
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void SetBool(ref bool target, string key, bool value)
        {
            bool changed;
            lock (_sync)
            {
                changed = target != value;
                target = value;
            }
            if (!changed) return;
            _settings.Set(key, value);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private static int NormalizeClientSource(int value) =>
            value is >= -2 and <= 3 ? value : -1;

        private static int NormalizeHostSlot(int value) =>
            value is >= 1 and <= 4 ? value : 0;

        private static GamepadStickResponseCurve NormalizeCurve(string value) =>
            Enum.TryParse(value, true, out GamepadStickResponseCurve parsed) &&
            Enum.IsDefined(typeof(GamepadStickResponseCurve), parsed)
                ? parsed
                : GamepadStickResponseCurve.Linear;
    }
}
