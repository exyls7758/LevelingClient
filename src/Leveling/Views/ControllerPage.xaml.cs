using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Multiverse.Models;
using Multiverse.Services;
using System;

namespace Multiverse.Views
{
    public sealed partial class ControllerPage : Page
    {
        private const ushort DpadUpMask = 0x0001;
        private const ushort DpadDownMask = 0x0002;
        private const ushort DpadLeftMask = 0x0004;
        private const ushort DpadRightMask = 0x0008;
        private const ushort StartMask = 0x0010;
        private const ushort BackMask = 0x0020;
        private const ushort L3Mask = 0x0040;
        private const ushort R3Mask = 0x0080;
        private const ushort LbMask = 0x0100;
        private const ushort RbMask = 0x0200;
        private const ushort AMask = 0x1000;
        private const ushort BMask = 0x2000;
        private const ushort XMask = 0x4000;
        private const ushort YMask = 0x8000;
        private const double TriggerVisualWidth = 118.0;
        private const double StickSurfaceWidth = 304.702;
        private const double StickSurfaceHeight = 304.893;
        private const double StickOuterGuideDiameter = 235.02;
        private const double StickDeadzoneGuideDiameter = 169.11;
        private const double StickAntiGuideDiameter = 169.11;
        private const double StickDeadzoneMaxPercent = 40.0;
        private const double StickOuterMaxPercent = 20.0;
        private const double StickAntiDeadzoneMaxPercent = 30.0;

        private static readonly GamepadDigitalButton[] MappingTargets =
        {
            GamepadDigitalButton.None,
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

        private readonly RemoteGamepadService _gamepad = RemoteGamepadService.Instance;
        private readonly RemoteSessionService _session = RemoteSessionService.Instance;
        private readonly GamepadTuningService _tuning = GamepadTuningService.Instance;
        private readonly DispatcherTimer _timer = new();
        private bool _loading = true;
        private bool _uiInitialized;
        private bool _mappingUiLoading;
        private string _selectedControlKey = "";
        private GamepadDigitalButton? _selectedDigitalSource;
        private Border? _selectedVisualBorder;
        private Brush? _selectedVisualOriginalBrush;
        private Thickness _selectedVisualOriginalThickness = new(1);
        private bool _compactLayout;

        public ControllerPage()
        {
            InitializeComponent();
            InitializeControllerLayoutEditor();
            InitializeMappingTargets();
            _session.Start();
            _timer.Interval = TimeSpan.FromMilliseconds(50);
            _timer.Tick += (_, _) => SafeUpdateUi();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            _uiInitialized = true;
            _loading = false;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            FriendRequestPromptCoordinator.Instance.Attach(this);
            LoadProfileToControls();
            UpdateResponsiveLayout(ActualWidth);
            LoadControllerLayoutEditor();
            LoadControllerFeedbackPreference();
            UpdateControllerConfigurationVisuals();
            SafeUpdateUi();
            _timer.Start();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _timer.Stop();
            CancelControllerDiagnostic();
            FriendRequestPromptCoordinator.Instance.Detach(this);
        }

        private void InitializeMappingTargets()
        {
            bool previousLoading = _mappingUiLoading;
            _mappingUiLoading = true;
            try
            {
                MappingTargetComboBox.Items.Clear();
                foreach (GamepadDigitalButton target in MappingTargets)
                    MappingTargetComboBox.Items.Add(GetButtonDisplayName(target));
            }
            finally
            {
                _mappingUiLoading = previousLoading;
            }
        }

        private void LoadProfileToControls()
        {
            _loading = true;
            try
            {
                ClientSourceComboBox.SelectedIndex = ClientSourceToSelectedIndex(_tuning.ClientControllerSource);
                HostSlotComboBox.SelectedIndex = Math.Clamp(_tuning.HostPlayerSlot, 0, 4);
                RemoteInputToggle.IsOn = _tuning.RemoteInputEnabled;

                LeftDeadzoneSlider.Value = _tuning.LeftStickDeadzonePercent;
                RightDeadzoneSlider.Value = _tuning.RightStickDeadzonePercent;
                LeftOuterDeadzoneSlider.Value = _tuning.LeftStickOuterDeadzonePercent;
                RightOuterDeadzoneSlider.Value = _tuning.RightStickOuterDeadzonePercent;
                LeftAntiDeadzoneSlider.Value = _tuning.LeftStickAntiDeadzonePercent;
                RightAntiDeadzoneSlider.Value = _tuning.RightStickAntiDeadzonePercent;
                LeftTriggerDeadzoneSlider.Value = _tuning.LeftTriggerDeadzonePercent;
                RightTriggerDeadzoneSlider.Value = _tuning.RightTriggerDeadzonePercent;
                ResponseCurveComboBox.SelectedIndex = (int)_tuning.StickResponseCurve;
                InvertLeftYToggle.IsOn = _tuning.InvertLeftY;
                InvertRightYToggle.IsOn = _tuning.InvertRightY;
            }
            finally
            {
                _loading = false;
            }

            UpdateTuningLabels();
            UpdateControllerConfigurationVisuals();
            UpdateMappingPanel();
        }

        private static int ClientSourceToSelectedIndex(int source) => source switch
        {
            -1 => 0, // Automático
            -2 => 1, // DualSense/HID explícito
            0 => 2,
            1 => 3,
            2 => 4,
            3 => 5,
            _ => 0
        };

        private static int SelectedIndexToClientSource(int selectedIndex) => selectedIndex switch
        {
            0 => -1,
            1 => -2,
            2 => 0,
            3 => 1,
            4 => 2,
            5 => 3,
            _ => -1
        };

        private bool CanEditClientTuning()
        {
            return CanUseClientDiagnosticRole() && !_diagnosticRunning;
        }

        private bool CanConfigureHostRouting()
        {
            RemoteSessionSnapshot session = _session.GetSnapshot();
            return !session.Connected || session.Role == RemoteSessionRole.Host;
        }

        private bool CanUseHostActions()
        {
            RemoteSessionSnapshot session = _session.GetSnapshot();
            return session.Connected && session.Role == RemoteSessionRole.Host;
        }

        private void ClientSourceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || !CanEditClientTuning()) return;
            _tuning.SetClientControllerSource(SelectedIndexToClientSource(ClientSourceComboBox.SelectedIndex));
            SafeUpdateUi();
        }

        private void HostSlotComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || !CanConfigureHostRouting()) return;
            int slot = Math.Clamp(HostSlotComboBox.SelectedIndex, 0, 4);
            if (CanUseHostActions())
                _gamepad.SetHostSlotAndRebind(slot);
            else
                _tuning.SetHostPlayerSlot(slot);
            SafeUpdateUi();
        }

        private void RemoteInputToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _tuning.SetRemoteInputEnabled(RemoteInputToggle.IsOn);
            if (CanUseHostActions())
                _gamepad.RebindVirtualController();
            SafeUpdateUi();
        }

        private void NextPlayerButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CanUseHostActions()) return;
            int current = ResolveCurrentHostSlotForQuickSwitch();
            ApplyQuickHostSlot(current >= 4 ? 1 : current + 1);
        }

        private void PreviousPlayerButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CanUseHostActions()) return;
            int current = ResolveCurrentHostSlotForQuickSwitch();
            ApplyQuickHostSlot(current <= 1 ? 4 : current - 1);
        }

        private void ClearSlotButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CanUseHostActions()) return;
            ApplyQuickHostSlot(0);
        }

        private int ResolveCurrentHostSlotForQuickSwitch()
        {
            int configured = _tuning.HostPlayerSlot;
            if (configured is >= 1 and <= 4)
                return configured;

            int actual = _gamepad.GetSnapshot().ActualHostSlot;
            return actual is >= 1 and <= 4 ? actual : 1;
        }

        private void ApplyQuickHostSlot(int slot)
        {
            _gamepad.SetHostSlotAndRebind(slot);
            _loading = true;
            try
            {
                HostSlotComboBox.SelectedIndex = slot;
            }
            finally
            {
                _loading = false;
            }
            SafeUpdateUi();
        }

        private async void ControllerPart_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not string key)
                return;

            if (_layoutEditMode)
            {
                SelectLayoutElementFromControllerPart(element);
                e.Handled = true;
                return;
            }

            SelectControllerPart(key);
            e.Handled = true;

            if (_selectedDigitalSource is GamepadDigitalButton source)
            {
                if (!CanEditClientTuning())
                {
                    SelectedPartHintTextBlock.Text =
                        "Remapeamento é um ajuste do CLIENTE. Faça esta alteração no PC que está usando o controle físico.";
                    return;
                }

                await ShowMappingDialogAsync(source);
            }
        }

        private async System.Threading.Tasks.Task ShowMappingDialogAsync(GamepadDigitalButton source)
        {
            if (XamlRoot is null)
                return;

            try
            {
                ComboBox targetCombo = new()
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    MinWidth = 260
                };
                foreach (GamepadDigitalButton target in MappingTargets)
                    targetCombo.Items.Add(GetButtonDisplayName(target));

                GamepadDigitalButton current = _tuning.GetButtonMapping(source);
                int currentIndex = Array.IndexOf(MappingTargets, current);
                targetCombo.SelectedIndex = currentIndex >= 0 ? currentIndex : 0;

                StackPanel content = new() { Spacing = 9 };
                content.Children.Add(new TextBlock
                {
                    Text = $"{GetButtonDisplayName(source)} físico controla qual saída virtual?",
                    Foreground = Brush(220, 229, 225),
                    TextWrapping = TextWrapping.Wrap
                });
                content.Children.Add(targetCombo);
                content.Children.Add(new TextBlock
                {
                    Text = "O desenho do controle mostra a saída efetiva em tempo real. Se Y for mapeado como X, pressionar Y acende X no preview.",
                    Foreground = Brush(116, 134, 126),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap
                });

                ContentDialog dialog = new()
                {
                    XamlRoot = XamlRoot,
                    Title = $"Mapear {GetButtonDisplayName(source)}",
                    Content = content,
                    PrimaryButtonText = "Salvar",
                    SecondaryButtonText = "Restaurar padrão",
                    CloseButtonText = "Cancelar",
                    DefaultButton = ContentDialogButton.Primary
                };

                ContentDialogResult result = await dialog.ShowAsync();
                if (!CanEditClientTuning())
                {
                    SelectedPartHintTextBlock.Text =
                        "A sessão mudou de papel durante o mapeamento. Nenhuma alteração foi aplicada.";
                    return;
                }

                if (result == ContentDialogResult.Primary)
                {
                    int index = targetCombo.SelectedIndex;
                    if (index >= 0 && index < MappingTargets.Length)
                        _tuning.SetButtonMapping(source, MappingTargets[index]);
                }
                else if (result == ContentDialogResult.Secondary)
                {
                    _tuning.SetButtonMapping(source, source);
                }

                UpdateMappingPanel();
                SafeUpdateUi();
            }
            catch (Exception ex)
            {
                ControllerStatusTextBlock.Text = $"Não foi possível abrir o mapeamento: {ex.Message}";
            }
        }

        private void SelectControllerPart(string key)
        {
            _selectedControlKey = key ?? "";
            _selectedDigitalSource = TryGetDigitalButton(_selectedControlKey, out GamepadDigitalButton digital)
                ? digital
                : null;
            UpdateSelectedPartVisual(_selectedControlKey);
            UpdateMappingPanel();
        }

        private void UpdateSelectedPartVisual(string key)
        {
            if (_selectedVisualBorder is not null)
            {
                _selectedVisualBorder.BorderBrush = _selectedVisualOriginalBrush;
                _selectedVisualBorder.BorderThickness = _selectedVisualOriginalThickness;
            }

            Border? next = ResolvePartBorder(key);
            _selectedVisualBorder = next;
            _selectedVisualOriginalBrush = next?.BorderBrush;
            _selectedVisualOriginalThickness = next?.BorderThickness ?? new Thickness(1);
            if (next is not null)
            {
                next.BorderBrush = Brush(239, 43, 86);
                next.BorderThickness = new Thickness(2.2);
            }
        }

        private Border? ResolvePartBorder(string key) => key switch
        {
            "A" => AButtonOrb,
            "B" => BButtonOrb,
            "X" => XButtonOrb,
            "Y" => YButtonOrb,
            "LB" => LbOrb,
            "RB" => RbOrb,
            "DPAD_UP" => DpadUp,
            "DPAD_DOWN" => DpadDown,
            "DPAD_LEFT" => DpadLeft,
            "DPAD_RIGHT" => DpadRight,
            "BACK" => BackButtonOrb,
            "START" => StartButtonOrb,
            "L3" => LeftStickClickTarget,
            "R3" => RightStickClickTarget,
            "LEFT_STICK" => LeftStickSurface,
            "RIGHT_STICK" => RightStickSurface,
            "LT" => LtSurface,
            "RT" => RtSurface,
            _ => null
        };

        private void UpdateMappingPanel()
        {
            string display = GetControlDisplayName(_selectedControlKey);
            if (string.IsNullOrEmpty(_selectedControlKey))
            {
                SelectedControlNameTextBlock.Text = "Selecione uma parte do controle";
                SelectedControlHintTextBlock.Text = "Clique em um botão para remapear. Sticks e gatilhos destacam os ajustes relacionados.";
                SelectedPartHintTextBlock.Text = "Clique em qualquer botão para remapear • sticks e gatilhos mostram o efeito das configurações em tempo real.";
                MappingTargetComboBox.IsEnabled = false;
                ResetSelectedMappingButton.IsEnabled = false;
                return;
            }

            SelectedControlNameTextBlock.Text = display;
            if (_selectedDigitalSource is GamepadDigitalButton source)
            {
                GamepadDigitalButton target = _tuning.GetButtonMapping(source);
                string mapping = $"{GetButtonDisplayName(source)} físico → {GetButtonDisplayName(target)} virtual";
                bool canEditMapping = CanEditClientTuning();
                SelectedControlHintTextBlock.Text = mapping;
                SelectedPartHintTextBlock.Text = canEditMapping
                    ? $"{mapping} • clique novamente no botão para alterar o mapeamento."
                    : $"{mapping} • remapeamento deve ser alterado no CLIENTE.";
                MappingTargetComboBox.IsEnabled = canEditMapping;
                ResetSelectedMappingButton.IsEnabled = canEditMapping && target != source;

                _mappingUiLoading = true;
                try
                {
                    int index = Array.IndexOf(MappingTargets, target);
                    MappingTargetComboBox.SelectedIndex = index >= 0 ? index : 0;
                }
                finally
                {
                    _mappingUiLoading = false;
                }
                return;
            }

            MappingTargetComboBox.IsEnabled = false;
            ResetSelectedMappingButton.IsEnabled = false;
            _mappingUiLoading = true;
            try
            {
                MappingTargetComboBox.SelectedIndex = -1;
            }
            finally
            {
                _mappingUiLoading = false;
            }

            string hint = _selectedControlKey switch
            {
                "LEFT_STICK" => $"Analógico esquerdo • Deadzone {_tuning.LeftStickDeadzonePercent}% • Outer {_tuning.LeftStickOuterDeadzonePercent}% • Anti {_tuning.LeftStickAntiDeadzonePercent}% • os anéis mudam ao vivo.",
                "RIGHT_STICK" => $"Analógico direito • Deadzone {_tuning.RightStickDeadzonePercent}% • Outer {_tuning.RightStickOuterDeadzonePercent}% • Anti {_tuning.RightStickAntiDeadzonePercent}% • os anéis mudam ao vivo.",
                "LT" => $"{(_controllerVisualSkin == ControllerVisualSkin.PlayStation5 ? "L2" : "LT")} • Deadzone {_tuning.LeftTriggerDeadzonePercent}% • a faixa âmbar é a zona morta e o preenchimento verde é a leitura efetiva.",
                "RT" => $"{(_controllerVisualSkin == ControllerVisualSkin.PlayStation5 ? "R2" : "RT")} • Deadzone {_tuning.RightTriggerDeadzonePercent}% • a faixa âmbar é a zona morta e o preenchimento roxo é a leitura efetiva.",
                _ => "Este controle usa ajuste analógico dedicado."
            };
            SelectedControlHintTextBlock.Text = hint;
            SelectedPartHintTextBlock.Text = hint;
        }

        private void MappingTargetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_mappingUiLoading || !CanEditClientTuning() ||
                _selectedDigitalSource is not GamepadDigitalButton source)
                return;

            int index = MappingTargetComboBox.SelectedIndex;
            if (index < 0 || index >= MappingTargets.Length)
                return;

            _tuning.SetButtonMapping(source, MappingTargets[index]);
            UpdateMappingPanel();
            SafeUpdateUi();
        }

        private void ResetSelectedMappingButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CanEditClientTuning() ||
                _selectedDigitalSource is not GamepadDigitalButton source)
                return;

            _tuning.SetButtonMapping(source, source);
            UpdateMappingPanel();
            SafeUpdateUi();
        }

        private void LeftDeadzoneSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_loading || !CanEditClientTuning()) return;
            _tuning.SetLeftStickDeadzonePercent((int)Math.Round(e.NewValue));
            UpdateTuningLabels();
            UpdateControllerConfigurationVisuals();
            SelectControllerPart("LEFT_STICK");
        }

        private void RightDeadzoneSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_loading || !CanEditClientTuning()) return;
            _tuning.SetRightStickDeadzonePercent((int)Math.Round(e.NewValue));
            UpdateTuningLabels();
            UpdateControllerConfigurationVisuals();
            SelectControllerPart("RIGHT_STICK");
        }

        private void LeftOuterDeadzoneSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_loading || !CanEditClientTuning()) return;
            _tuning.SetLeftStickOuterDeadzonePercent((int)Math.Round(e.NewValue));
            UpdateTuningLabels();
            UpdateControllerConfigurationVisuals();
            SelectControllerPart("LEFT_STICK");
        }

        private void RightOuterDeadzoneSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_loading || !CanEditClientTuning()) return;
            _tuning.SetRightStickOuterDeadzonePercent((int)Math.Round(e.NewValue));
            UpdateTuningLabels();
            UpdateControllerConfigurationVisuals();
            SelectControllerPart("RIGHT_STICK");
        }

        private void LeftAntiDeadzoneSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_loading || !CanEditClientTuning()) return;
            _tuning.SetLeftStickAntiDeadzonePercent((int)Math.Round(e.NewValue));
            UpdateTuningLabels();
            UpdateControllerConfigurationVisuals();
            SelectControllerPart("LEFT_STICK");
        }

        private void RightAntiDeadzoneSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_loading || !CanEditClientTuning()) return;
            _tuning.SetRightStickAntiDeadzonePercent((int)Math.Round(e.NewValue));
            UpdateTuningLabels();
            UpdateControllerConfigurationVisuals();
            SelectControllerPart("RIGHT_STICK");
        }

        private void LeftTriggerDeadzoneSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_loading || !CanEditClientTuning()) return;
            _tuning.SetLeftTriggerDeadzonePercent((int)Math.Round(e.NewValue));
            UpdateTuningLabels();
            UpdateControllerConfigurationVisuals();
            SelectControllerPart("LT");
        }

        private void RightTriggerDeadzoneSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_loading || !CanEditClientTuning()) return;
            _tuning.SetRightTriggerDeadzonePercent((int)Math.Round(e.NewValue));
            UpdateTuningLabels();
            UpdateControllerConfigurationVisuals();
            SelectControllerPart("RT");
        }

        private void ResponseCurveComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || !CanEditClientTuning()) return;
            int index = Math.Clamp(ResponseCurveComboBox.SelectedIndex, 0, 2);
            _tuning.SetStickResponseCurve((GamepadStickResponseCurve)index);
            UpdateControllerConfigurationVisuals();
            SelectedPartHintTextBlock.Text = "Curva dos analógicos atualizada • o movimento dos dois sticks usa a nova resposta imediatamente.";
        }

        private void InvertLeftYToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading || !CanEditClientTuning()) return;
            _tuning.SetInvertLeftY(InvertLeftYToggle.IsOn);
            UpdateControllerConfigurationVisuals();
            SelectControllerPart("LEFT_STICK");
        }

        private void InvertRightYToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading || !CanEditClientTuning()) return;
            _tuning.SetInvertRightY(InvertRightYToggle.IsOn);
            UpdateControllerConfigurationVisuals();
            SelectControllerPart("RIGHT_STICK");
        }

        private void ResetTuningButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CanEditClientTuning())
                return;

            try
            {
                _tuning.Reset();
                LoadProfileToControls();
                SafeUpdateUi();
            }
            catch (Exception ex)
            {
                ControllerStatusTextBlock.Text = $"Não foi possível restaurar o perfil do controle: {ex.Message}";
            }
        }

        private void NeutralizeButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CanUseHostActions())
                return;

            try
            {
                _gamepad.NeutralizeNow();
                SafeUpdateUi();
            }
            catch (Exception ex)
            {
                ControllerStatusTextBlock.Text = $"Não foi possível neutralizar o controle virtual: {ex.Message}";
            }
        }

        private void RebindButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CanUseHostActions())
                return;

            try
            {
                _gamepad.RebindVirtualController();
                SafeUpdateUi();
            }
            catch (Exception ex)
            {
                ControllerStatusTextBlock.Text = $"Não foi possível recriar o controle virtual: {ex.Message}";
            }
        }

        private void UpdateTuningLabels()
        {
            LeftDeadzoneValue.Text = $"{_tuning.LeftStickDeadzonePercent}%";
            RightDeadzoneValue.Text = $"{_tuning.RightStickDeadzonePercent}%";
            LeftOuterDeadzoneValue.Text = $"{_tuning.LeftStickOuterDeadzonePercent}%";
            RightOuterDeadzoneValue.Text = $"{_tuning.RightStickOuterDeadzonePercent}%";
            LeftAntiDeadzoneValue.Text = $"{_tuning.LeftStickAntiDeadzonePercent}%";
            RightAntiDeadzoneValue.Text = $"{_tuning.RightStickAntiDeadzonePercent}%";
            LeftTriggerDeadzoneValue.Text = $"{_tuning.LeftTriggerDeadzonePercent}%";
            RightTriggerDeadzoneValue.Text = $"{_tuning.RightTriggerDeadzonePercent}%";
        }

        private void UpdateControllerConfigurationVisuals()
        {
            UpdateStickConfigurationVisual(
                LeftStickDeadzoneRing,
                LeftStickOuterRing,
                LeftStickAntiRing,
                _tuning.LeftStickDeadzonePercent,
                _tuning.LeftStickOuterDeadzonePercent,
                _tuning.LeftStickAntiDeadzonePercent);

            UpdateStickConfigurationVisual(
                RightStickDeadzoneRing,
                RightStickOuterRing,
                RightStickAntiRing,
                _tuning.RightStickDeadzonePercent,
                _tuning.RightStickOuterDeadzonePercent,
                _tuning.RightStickAntiDeadzonePercent);

            LtDeadzoneOverlay.Width = TriggerVisualWidth * _tuning.LeftTriggerDeadzonePercent / 100.0;
            RtDeadzoneOverlay.Width = TriggerVisualWidth * _tuning.RightTriggerDeadzonePercent / 100.0;

            ApplyAnalogFeedbackVisibility();

            if (!string.IsNullOrEmpty(_selectedControlKey))
                UpdateMappingPanel();
        }

        private static void UpdateStickConfigurationVisual(
            Ellipse deadzoneRing,
            Ellipse outerRing,
            Ellipse antiRing,
            int deadzonePercent,
            int outerPercent,
            int antiPercent)
        {
            double centerX = StickSurfaceWidth / 2.0;
            double centerY = StickSurfaceHeight / 2.0;

            // Guide mapping requested by the user:
            // - Outer L/R reaches the largest guide circle (third circle).
            // - Deadzone L/R reaches the smaller guide circle (second circle, not the center dot).
            // Anti-deadzone remains an inner assist ring and uses the same maximum guide as deadzone.
            double deadzoneRatio = Math.Clamp(deadzonePercent, 0.0, StickDeadzoneMaxPercent) / StickDeadzoneMaxPercent;
            double deadzoneDiameter = deadzonePercent > 0
                ? Math.Max(18.0, StickDeadzoneGuideDiameter * Math.Sqrt(deadzoneRatio))
                : 0.0;
            SetCenteredRing(deadzoneRing, centerX, centerY, deadzoneDiameter, deadzonePercent > 0);

            double outerRatio = 1.0 - Math.Clamp(outerPercent, 0.0, StickOuterMaxPercent) / 100.0;
            double outerDiameter = Math.Max(2.0, StickOuterGuideDiameter * outerRatio);
            SetCenteredRing(outerRing, centerX, centerY, outerDiameter, true);

            double antiRatio = Math.Clamp(antiPercent, 0.0, StickAntiDeadzoneMaxPercent) / StickAntiDeadzoneMaxPercent;
            double antiDiameter = antiPercent > 0
                ? Math.Max(16.0, StickAntiGuideDiameter * Math.Sqrt(antiRatio))
                : 0.0;
            SetCenteredRing(antiRing, centerX, centerY, antiDiameter, antiPercent > 0);
        }

        private static void SetCenteredRing(Ellipse ring, double centerX, double centerY, double diameter, bool visible)
        {
            double safeWidth = Math.Max(1.0, diameter);
            double safeHeight = safeWidth * (StickSurfaceHeight / StickSurfaceWidth);
            ring.Width = safeWidth;
            ring.Height = safeHeight;
            Canvas.SetLeft(ring, centerX - safeWidth / 2.0);
            Canvas.SetTop(ring, centerY - safeHeight / 2.0);
            ring.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ControllerPage_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (MainLayoutGrid is not null)
                MainLayoutGrid.MinHeight = Math.Max(620.0, e.NewSize.Height - 112.0);

            if (ControllerStageGrid is not null)
            {
                double stageHeight = e.NewSize.Width < 1000
                    ? Math.Clamp(e.NewSize.Width * 0.62, 430.0, 650.0)
                    : Math.Clamp(e.NewSize.Height - 390.0, 520.0, 740.0);
                ControllerStageGrid.Height = stageHeight;
            }

            UpdateResponsiveLayout(e.NewSize.Width);
        }

        private void UpdateResponsiveLayout(double width)
        {
            if (MainLayoutGrid is null || ControllerVisualCard is null || ControllerSettingsCard is null)
                return;

            bool compact = width > 0 && width < 1000;
            if (_compactLayout == compact && width > 0)
                return;

            _compactLayout = compact;
            if (compact)
            {
                MainLayoutGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
                MainLayoutGrid.ColumnDefinitions[1].Width = new GridLength(0);
                MainLayoutGrid.RowDefinitions[0].Height = GridLength.Auto;
                MainLayoutGrid.RowDefinitions[1].Height = GridLength.Auto;
                Grid.SetColumn(ControllerVisualCard, 0);
                Grid.SetRow(ControllerVisualCard, 0);
                Grid.SetColumn(ControllerSettingsCard, 0);
                Grid.SetRow(ControllerSettingsCard, 1);
            }
            else
            {
                MainLayoutGrid.ColumnDefinitions[0].Width = new GridLength(1.28, GridUnitType.Star);
                MainLayoutGrid.ColumnDefinitions[1].Width = new GridLength(1.0, GridUnitType.Star);
                MainLayoutGrid.RowDefinitions[0].Height = GridLength.Auto;
                MainLayoutGrid.RowDefinitions[1].Height = new GridLength(0);
                Grid.SetColumn(ControllerVisualCard, 0);
                Grid.SetRow(ControllerVisualCard, 0);
                Grid.SetColumn(ControllerSettingsCard, 1);
                Grid.SetRow(ControllerSettingsCard, 0);
            }
        }

        private void UpdateRoleAwareControls(RemoteSessionSnapshot session)
        {
            bool disconnected = !session.Connected;
            bool client = session.Connected && session.Role == RemoteSessionRole.Client;
            bool host = session.Connected && session.Role == RemoteSessionRole.Host;
            bool clientRoleAvailable = disconnected || client;
            bool clientSettingsEnabled = clientRoleAvailable && !_diagnosticRunning;
            bool hostRoutingEnabled = (disconnected || host) && !_diagnosticRunning;
            bool hostActionsEnabled = host && !_diagnosticRunning;

            DiagnosticControllerButton.IsEnabled = clientRoleAvailable && !_diagnosticRunning;
            RemoteInputToggle.IsEnabled = !_diagnosticRunning;
            ClientSourceComboBox.IsEnabled = clientSettingsEnabled;
            LeftDeadzoneSlider.IsEnabled = clientSettingsEnabled;
            RightDeadzoneSlider.IsEnabled = clientSettingsEnabled;
            LeftOuterDeadzoneSlider.IsEnabled = clientSettingsEnabled;
            RightOuterDeadzoneSlider.IsEnabled = clientSettingsEnabled;
            LeftAntiDeadzoneSlider.IsEnabled = clientSettingsEnabled;
            RightAntiDeadzoneSlider.IsEnabled = clientSettingsEnabled;
            LeftTriggerDeadzoneSlider.IsEnabled = clientSettingsEnabled;
            RightTriggerDeadzoneSlider.IsEnabled = clientSettingsEnabled;
            ResponseCurveComboBox.IsEnabled = clientSettingsEnabled;
            InvertLeftYToggle.IsEnabled = clientSettingsEnabled;
            InvertRightYToggle.IsEnabled = clientSettingsEnabled;
            ResetTuningButton.IsEnabled = clientSettingsEnabled;

            HostSlotComboBox.IsEnabled = hostRoutingEnabled;
            NextPlayerButton.IsEnabled = hostActionsEnabled;
            PreviousPlayerButton.IsEnabled = hostActionsEnabled;
            ClearSlotButton.IsEnabled = hostActionsEnabled;
            RebindButton.IsEnabled = hostActionsEnabled;
            NeutralizeButton.IsEnabled = hostActionsEnabled;

            if (_diagnosticRunning)
            {
                RoleScopeHintTextBlock.Text =
                    "DIAGNÓSTICO • leitura RAW local ativa. Ajustes manuais ficam bloqueados temporariamente e o input enviado ao host permanece neutro.";
            }
            else if (disconnected)
            {
                RoleScopeHintTextBlock.Text =
                    "SEM SESSÃO • prepare os ajustes do cliente e o slot do host antes de conectar. Ações ViGEm ficam disponíveis durante uma sessão HOST.";
            }
            else if (client)
            {
                RoleScopeHintTextBlock.Text =
                    "CLIENTE • fonte, remapeamento, deadzones, curvas e inversão são aplicados aqui antes do envio. Slot e ações ViGEm pertencem ao HOST.";
            }
            else if (host)
            {
                RoleScopeHintTextBlock.Text =
                    "HOST • slot, troca rápida, neutralização e recriação ViGEm são locais. Ajustes do controle físico devem ser feitos no CLIENTE.";
            }
            else
            {
                RoleScopeHintTextBlock.Text = "Sessão sem papel de controle definido.";
            }

            bool permissionBlocked = host && !session.RemoteInputAllowed;
            RemoteInputPermissionTextBlock.Visibility =
                permissionBlocked ? Visibility.Visible : Visibility.Collapsed;
            RemoteInputPermissionTextBlock.Text = permissionBlocked
                ? "Permissão do amigo bloqueia controle remoto. Ative “Permitir controle remoto” na aba Amigos para este dispositivo."
                : string.Empty;

            if (_selectedDigitalSource is GamepadDigitalButton selectedSource)
            {
                GamepadDigitalButton target = _tuning.GetButtonMapping(selectedSource);
                MappingTargetComboBox.IsEnabled = clientSettingsEnabled;
                ResetSelectedMappingButton.IsEnabled =
                    clientSettingsEnabled && target != selectedSource;

                if (!clientSettingsEnabled)
                {
                    string mapping =
                        $"{GetButtonDisplayName(selectedSource)} físico → {GetButtonDisplayName(target)} virtual";
                    SelectedPartHintTextBlock.Text =
                        $"{mapping} • remapeamento deve ser alterado no CLIENTE.";
                }
            }
        }

        private void SafeUpdateUi()
        {
            // XAML can raise Toggled/SelectionChanged before all named controls exist.
            // Do not run the full UI update until construction and profile loading finish.
            if (!_uiInitialized || _loading)
                return;

            try
            {
                UpdateUi();
            }
            catch (Exception ex)
            {
                ControllerStatusTextBlock.Text = "A tela de controle encontrou um problema, mas a sessão continua ativa.";
                PacketCardValueTextBlock.Text = ex.Message;
            }
        }

        private void UpdateUi()
        {
            RemoteGamepadServiceSnapshot pad = _gamepad.GetSnapshot();
            RemoteSessionSnapshot session = _session.GetSnapshot();
            UpdateRoleAwareControls(session);

            ControllerStatusTextBlock.Text = pad.Status;
            PacketCardValueTextBlock.Text = $"TX {pad.TransmittedPackets}  •  RX {pad.ReceivedPackets}";
            SafetyCardValueTextBlock.Text = pad.StaleNeutralActive ? "SAFE-NEUTRAL ativo" : "Watchdog 250 ms";
            SafetyCardValueTextBlock.Foreground = Brush(
                pad.StaleNeutralActive ? 255 : 96,
                pad.StaleNeutralActive ? 159 : 230,
                pad.StaleNeutralActive ? 67 : 168);

            if (!session.Connected)
            {
                LatencyCardValueTextBlock.Text = "-- ms";
                ControllerRateTextBlock.Text = "Xbox 360 virtual";
            }
            else if (session.Role == RemoteSessionRole.Client)
            {
                LatencyCardValueTextBlock.Text = "local";
                ControllerRateTextBlock.Text = !string.IsNullOrWhiteSpace(pad.LocalControllerName)
                    ? $"{pad.LocalControllerName} • 250 Hz"
                    : "Controle • 250 Hz";
            }
            else
            {
                LatencyCardValueTextBlock.Text = $"{pad.RemotePacketAgeMs:0} ms";
                ControllerRateTextBlock.Text = pad.ActualHostSlot > 0
                    ? $"P{pad.ActualHostSlot} • Xbox 360 virtual"
                    : "Xbox 360 virtual";
            }

            string requested = pad.RequestedHostSlot > 0 ? $"P{pad.RequestedHostSlot}" : "AUTO";
            string actual = pad.ActualHostSlot > 0 ? $"P{pad.ActualHostSlot}" : "--";
            ActualSlotTextBlock.Text = $"Solicitado {requested} • Real {actual}";
            ReservationTextBlock.Text = pad.ReservationCount > 0
                ? $"{pad.ReservationCount} controle(s) neutro(s) reservando slots anteriores"
                : "Sem reservas de slot";

            bool conflict = pad.HostSlotConflict || !string.IsNullOrWhiteSpace(pad.LastError);
            SlotConflictTextBlock.Visibility = conflict ? Visibility.Visible : Visibility.Collapsed;
            SlotConflictTextBlock.Text = pad.HostSlotConflict
                ? $"Conflito: {pad.Status}"
                : pad.LastError;

            LocalRawGamepadSnapshot? diagnosticRaw = _diagnosticRunning ? _diagnosticLatestRaw : null;
            ushort buttons = diagnosticRaw?.Buttons ?? pad.Buttons;
            bool ps5Visual = _controllerVisualSkin == ControllerVisualSkin.PlayStation5;
            SetButton(AButtonOrb, (buttons & AMask) != 0, ps5Visual ? (byte)55 : (byte)84, ps5Visual ? (byte)154 : (byte)214, ps5Visual ? (byte)235 : (byte)110);
            SetButton(BButtonOrb, (buttons & BMask) != 0, ps5Visual ? (byte)55 : (byte)240, ps5Visual ? (byte)154 : (byte)88, ps5Visual ? (byte)235 : (byte)88);
            SetButton(XButtonOrb, (buttons & XMask) != 0, ps5Visual ? (byte)55 : (byte)67, ps5Visual ? (byte)154 : (byte)189, ps5Visual ? (byte)235 : (byte)240);
            SetButton(YButtonOrb, (buttons & YMask) != 0, ps5Visual ? (byte)55 : (byte)244, ps5Visual ? (byte)154 : (byte)201, ps5Visual ? (byte)235 : (byte)76);
            SetButton(DpadUp, (buttons & DpadUpMask) != 0, 239, 43, 86);
            SetButton(DpadDown, (buttons & DpadDownMask) != 0, 239, 43, 86);
            SetButton(DpadLeft, (buttons & DpadLeftMask) != 0, 239, 43, 86);
            SetButton(DpadRight, (buttons & DpadRightMask) != 0, 239, 43, 86);
            SetButton(BackButtonOrb, (buttons & BackMask) != 0, 239, 43, 86);
            SetButton(StartButtonOrb, (buttons & StartMask) != 0, 239, 43, 86);
            SetButton(LeftStickClickTarget, (buttons & L3Mask) != 0, 239, 43, 86);
            SetButton(RightStickClickTarget, (buttons & R3Mask) != 0, 112, 101, 245);
            SetButton(LbOrb, (buttons & LbMask) != 0, 239, 43, 86);
            SetButton(RbOrb, (buttons & RbMask) != 0, 112, 101, 245);

            MoveStick(LeftStickLiveDot, diagnosticRaw?.LeftThumbX ?? pad.LeftThumbX, diagnosticRaw?.LeftThumbY ?? pad.LeftThumbY);
            MoveStick(RightStickLiveDot, diagnosticRaw?.RightThumbX ?? pad.RightThumbX, diagnosticRaw?.RightThumbY ?? pad.RightThumbY);
            LtFill.Width = TriggerVisualWidth * (diagnosticRaw?.LeftTrigger ?? pad.LeftTrigger) / byte.MaxValue;
            RtFill.Width = TriggerVisualWidth * (diagnosticRaw?.RightTrigger ?? pad.RightTrigger) / byte.MaxValue;
            ApplyAnalogFeedbackVisibility();
        }

        private static void MoveStick(UIElement marker, short x, short y)
        {
            const double travelX = 94.91;
            const double travelY = 94.91;
            double markerWidth = marker is FrameworkElement element && !double.IsNaN(element.Width) ? element.Width : 74.93;
            double markerHeight = marker is FrameworkElement heightElement && !double.IsNaN(heightElement.Height) ? heightElement.Height : 74.97;
            double baseX = (StickSurfaceWidth - markerWidth) / 2.0;
            double baseY = (StickSurfaceHeight - markerHeight) / 2.0;
            double nx = Math.Clamp(x / 32767.0, -1.0, 1.0);
            double ny = Math.Clamp(y / 32767.0, -1.0, 1.0);
            Canvas.SetLeft(marker, baseX + nx * travelX);
            Canvas.SetTop(marker, baseY - ny * travelY);
        }

        private static void SetButton(Border border, bool active, byte r, byte g, byte b)
        {
            // Keep the static/selected border intact; live input is shown as a translucent overlay.
            border.Background = active ? Brush(104, r, g, b) : Brush(0, 0, 0, 0);
            border.Opacity = 1.0;
        }

        private static bool TryGetDigitalButton(string key, out GamepadDigitalButton button)
        {
            button = key switch
            {
                "DPAD_UP" => GamepadDigitalButton.DpadUp,
                "DPAD_DOWN" => GamepadDigitalButton.DpadDown,
                "DPAD_LEFT" => GamepadDigitalButton.DpadLeft,
                "DPAD_RIGHT" => GamepadDigitalButton.DpadRight,
                "START" => GamepadDigitalButton.Start,
                "BACK" => GamepadDigitalButton.Back,
                "L3" => GamepadDigitalButton.L3,
                "R3" => GamepadDigitalButton.R3,
                "LB" => GamepadDigitalButton.LB,
                "RB" => GamepadDigitalButton.RB,
                "A" => GamepadDigitalButton.A,
                "B" => GamepadDigitalButton.B,
                "X" => GamepadDigitalButton.X,
                "Y" => GamepadDigitalButton.Y,
                _ => GamepadDigitalButton.None
            };
            return button != GamepadDigitalButton.None;
        }

        private string GetControlDisplayName(string key)
        {
            bool ps5 = _controllerVisualSkin == ControllerVisualSkin.PlayStation5;
            return key switch
            {
                "A" => ps5 ? "Botão × (A)" : "Botão A",
                "B" => ps5 ? "Botão ○ (B)" : "Botão B",
                "X" => ps5 ? "Botão □ (X)" : "Botão X",
                "Y" => ps5 ? "Botão △ (Y)" : "Botão Y",
                "LB" => ps5 ? "Bumper L1" : "Bumper LB",
                "RB" => ps5 ? "Bumper R1" : "Bumper RB",
                "DPAD_UP" => "D-pad ↑",
                "DPAD_DOWN" => "D-pad ↓",
                "DPAD_LEFT" => "D-pad ←",
                "DPAD_RIGHT" => "D-pad →",
                "BACK" => ps5 ? "Create / Back" : "View / Back",
                "START" => ps5 ? "Options / Start" : "Menu / Start",
                "L3" => "Clique do analógico L3",
                "R3" => "Clique do analógico R3",
                "LEFT_STICK" => "Analógico esquerdo",
                "RIGHT_STICK" => "Analógico direito",
                "LT" => ps5 ? "Gatilho L2" : "Gatilho LT",
                "RT" => ps5 ? "Gatilho R2" : "Gatilho RT",
                _ => "Controle"
            };
        }

        private string GetButtonDisplayName(GamepadDigitalButton button)
        {
            bool ps5 = _controllerVisualSkin == ControllerVisualSkin.PlayStation5;
            return button switch
            {
                GamepadDigitalButton.None => "sem saída",
                GamepadDigitalButton.DpadUp => "D-pad ↑",
                GamepadDigitalButton.DpadDown => "D-pad ↓",
                GamepadDigitalButton.DpadLeft => "D-pad ←",
                GamepadDigitalButton.DpadRight => "D-pad →",
                GamepadDigitalButton.Start => ps5 ? "Options / Start" : "Menu / Start",
                GamepadDigitalButton.Back => ps5 ? "Create / Back" : "View / Back",
                GamepadDigitalButton.L3 => "L3",
                GamepadDigitalButton.R3 => "R3",
                GamepadDigitalButton.LB => ps5 ? "L1 (LB)" : "LB",
                GamepadDigitalButton.RB => ps5 ? "R1 (RB)" : "RB",
                GamepadDigitalButton.A => ps5 ? "× (A)" : "A",
                GamepadDigitalButton.B => ps5 ? "○ (B)" : "B",
                GamepadDigitalButton.X => ps5 ? "□ (X)" : "X",
                GamepadDigitalButton.Y => ps5 ? "△ (Y)" : "Y",
                _ => button.ToString()
            };
        }

        private static SolidColorBrush Brush(int r, int g, int b) =>
            new(ColorHelper.FromArgb(255, (byte)r, (byte)g, (byte)b));

        private static SolidColorBrush Brush(int a, int r, int g, int b) =>
            new(ColorHelper.FromArgb((byte)a, (byte)r, (byte)g, (byte)b));
    }
}
