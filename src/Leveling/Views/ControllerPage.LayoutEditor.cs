using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Multiverse.Models;
using Multiverse.Services;
using System;
using System.Collections.Generic;
using Windows.Foundation;
using Windows.System;

namespace Multiverse.Views
{
    public sealed partial class ControllerPage
    {
        private enum ControllerLayoutEditorMode
        {
            None,
            AnalogAlignment,
            FullLayout
        }

        private sealed class LayoutElementDescriptor
        {
            public required string Key { get; init; }
            public required string DisplayName { get; init; }
            public required FrameworkElement Element { get; init; }
            public bool UsesTranslation { get; init; }
            public bool CanResize { get; init; }
            public bool IsAnalog { get; init; }
        }

        private readonly ControllerLayoutService _layoutService = ControllerLayoutService.Instance;
        private readonly MultiverseSettingsService _controllerSettings = MultiverseSettingsService.Instance;
        private readonly Dictionary<string, LayoutElementDescriptor> _layoutElements = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _analogLayoutKeys = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ControllerVisualSkin, ControllerVisualLayout> _defaultVisualLayouts = new();
        private ControllerVisualSkin _controllerVisualSkin = ControllerVisualSkin.Xbox;
        private bool _controllerSkinUiLoading;
        private ControllerVisualLayout? _preEditVisualLayout;
        private LayoutElementDescriptor? _selectedLayoutElement;
        private ControllerLayoutEditorMode _layoutEditorMode = ControllerLayoutEditorMode.None;
        private bool _hasCustomVisualLayout;
        private bool _hasFullCustomLayout;
        private bool _preEditCustomRendering;
        private bool _layoutEditorUiLoading;
        private const string ControllerFeedbackVisibilityKey = "Multiverse.Gamepad.VisualFeedback";
        private bool _controllerFeedbackVisible;
        private bool _controllerFeedbackUiLoading;
        private bool _analogAlignmentGuidesRequested;
        private bool _layoutDragActive;
        private Point _layoutDragStartPoint;
        private double _layoutDragStartX;
        private double _layoutDragStartY;

        private bool _layoutEditMode => _layoutEditorMode != ControllerLayoutEditorMode.None;

        private void InitializeControllerLayoutEditor()
        {
            RegisterAnalogLayoutElement("LEFT_STICK", "Analógico esquerdo", LeftStickSurface);
            RegisterAnalogLayoutElement("RIGHT_STICK", "Analógico direito", RightStickSurface);

            RegisterFullLayoutElement("DPAD", "D-pad", DpadContainer, false, false);
            RegisterFullLayoutElement("BACK", "View / Back", BackButtonOrb, false, true);
            RegisterFullLayoutElement("START", "Menu / Start", StartButtonOrb, false, true);
            RegisterFullLayoutElement("A", "Botão A", AButtonOrb, false, true);
            RegisterFullLayoutElement("B", "Botão B", BButtonOrb, false, true);
            RegisterFullLayoutElement("X", "Botão X", XButtonOrb, false, true);
            RegisterFullLayoutElement("Y", "Botão Y", YButtonOrb, false, true);
            RegisterFullLayoutElement("LT", "Gatilho LT", LtSurface, true, false);
            RegisterFullLayoutElement("LB", "Bumper LB", LbOrb, true, false);
            RegisterFullLayoutElement("RB", "Bumper RB", RbOrb, true, false);
            RegisterFullLayoutElement("RT", "Gatilho RT", RtSurface, true, false);
        }

        private void ControllerSkinComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_controllerSkinUiLoading || _layoutEditMode)
                return;

            ControllerVisualSkin selected = ControllerSkinComboBox.SelectedIndex == 1
                ? ControllerVisualSkin.PlayStation5
                : ControllerVisualSkin.Xbox;
            if (selected == _controllerVisualSkin && _defaultVisualLayouts.Count > 0)
                return;

            ApplyControllerVisualSkin(selected, loadSavedLayout: true, persistSelection: true);
        }

        private void ApplyControllerVisualSkin(
            ControllerVisualSkin skin,
            bool loadSavedLayout,
            bool persistSelection)
        {
            if (!_defaultVisualLayouts.TryGetValue(skin, out ControllerVisualLayout? defaultLayout))
                return;

            _controllerVisualSkin = skin;
            if (persistSelection)
                _controllerSettings.SetControllerVisualSkin(skin);

            _controllerSkinUiLoading = true;
            try
            {
                ControllerSkinComboBox.SelectedIndex =
                    skin == ControllerVisualSkin.PlayStation5 ? 1 : 0;
            }
            finally
            {
                _controllerSkinUiLoading = false;
            }

            ApplyVisualLayout(defaultLayout);
            ControllerVisualLayout? saved = loadSavedLayout
                ? _layoutService.Load(skin)
                : null;
            if (saved is not null)
            {
                ApplyVisualLayout(saved);
                _hasCustomVisualLayout = true;
                // Old layouts retain their coordinates, but the legacy shell
                // must never replace the approved original controller artwork.
                _hasFullCustomLayout = false;
            }
            else
            {
                _hasCustomVisualLayout = false;
                _hasFullCustomLayout = false;
            }

            SetCustomControllerRendering(_hasFullCustomLayout);
            ApplyControllerSkinLabels();
            _selectedControlKey = "";
            _selectedDigitalSource = null;
            InitializeMappingTargets();
            UpdateSelectedPartVisual("");
            UpdateMappingPanel();
            UpdateEditorButtons();
            UpdateLayoutEditorFields();
        }

        private void ApplyControllerSkinLabels()
        {
            bool ps5 = _controllerVisualSkin == ControllerVisualSkin.PlayStation5;
            ControllerVisualModeTitleTextBlock.Text = ps5
                ? "PS5 / DUALSENSE • INPUT LIVE"
                : "XBOX 360 • INPUT LIVE";
            LtLabelTextBlock.Text = ps5 ? "L2" : "LT";
            LbLabelTextBlock.Text = ps5 ? "L1" : "LB";
            RbLabelTextBlock.Text = ps5 ? "R1" : "RB";
            RtLabelTextBlock.Text = ps5 ? "R2" : "RT";

            ToolTipService.SetToolTip(AButtonOrb, ps5 ? "× / A • clique para configurar o mapeamento" : "A • clique para configurar o mapeamento");
            ToolTipService.SetToolTip(BButtonOrb, ps5 ? "○ / B • clique para configurar o mapeamento" : "B • clique para configurar o mapeamento");
            ToolTipService.SetToolTip(XButtonOrb, ps5 ? "□ / X • clique para configurar o mapeamento" : "X • clique para configurar o mapeamento");
            ToolTipService.SetToolTip(YButtonOrb, ps5 ? "△ / Y • clique para configurar o mapeamento" : "Y • clique para configurar o mapeamento");
            ToolTipService.SetToolTip(BackButtonOrb, ps5 ? "Create / Back • clique para remapear" : "View / Back • clique para remapear");
            ToolTipService.SetToolTip(StartButtonOrb, ps5 ? "Options / Start • clique para remapear" : "Menu / Start • clique para remapear");
            ToolTipService.SetToolTip(LtSurface, ps5 ? "L2 • deadzone e leitura em tempo real" : "LT • deadzone e leitura em tempo real");
            ToolTipService.SetToolTip(RtSurface, ps5 ? "R2 • deadzone e leitura em tempo real" : "RT • deadzone e leitura em tempo real");
            ToolTipService.SetToolTip(LbOrb, ps5 ? "L1 • clique para remapear" : "LB • clique para remapear");
            ToolTipService.SetToolTip(RbOrb, ps5 ? "R1 • clique para remapear" : "RB • clique para remapear");
        }

        private static ControllerVisualLayout CreatePlayStation5DefaultLayout(ControllerVisualLayout xboxDefault)
        {
            ControllerVisualLayout layout = CloneVisualLayout(xboxDefault);
            layout.UseCustomControllerRendering = false;

            SetLayout(layout, "LEFT_STICK", 532.75, 549.51, 304.702, 304.893);
            SetLayout(layout, "RIGHT_STICK", 1206.54, 549.41, 304.702, 304.893);
            SetLayout(layout, "DPAD", 196.883, 240.649, 349.659, 340.637);
            SetLayout(layout, "BACK", 487.524, 138.820, 89.912, 87.592);
            SetLayout(layout, "START", 1496.164, 138.820, 89.912, 87.592);
            SetLayout(layout, "Y", 1605.907, 192.952, 144.859, 141.121);
            SetLayout(layout, "X", 1452.307, 348.600, 144.859, 141.121);
            SetLayout(layout, "B", 1757.459, 346.551, 144.859, 141.121);
            SetLayout(layout, "A", 1605.907, 502.200, 144.859, 141.121);
            return layout;
        }

        private static ControllerVisualLayout CloneVisualLayout(ControllerVisualLayout source)
        {
            ControllerVisualLayout clone = new()
            {
                Version = source.Version,
                UseCustomControllerRendering = source.UseCustomControllerRendering
            };
            foreach ((string key, ControllerVisualElementLayout value) in source.Elements)
            {
                clone.Elements[key] = new ControllerVisualElementLayout
                {
                    X = value.X,
                    Y = value.Y,
                    Width = value.Width,
                    Height = value.Height,
                    UsesTranslation = value.UsesTranslation
                };
            }
            return clone;
        }

        private static void SetLayout(
            ControllerVisualLayout layout,
            string key,
            double x,
            double y,
            double width,
            double height)
        {
            if (!layout.Elements.TryGetValue(key, out ControllerVisualElementLayout? entry))
                return;

            entry.X = x;
            entry.Y = y;
            entry.Width = width;
            entry.Height = height;
        }

        private void RegisterAnalogLayoutElement(string key, string displayName, FrameworkElement element)
        {
            _analogLayoutKeys.Add(key);
            RegisterLayoutElement(new LayoutElementDescriptor
            {
                Key = key,
                DisplayName = displayName,
                Element = element,
                UsesTranslation = false,
                CanResize = false,
                IsAnalog = true
            });
        }

        private void RegisterFullLayoutElement(string key, string displayName, FrameworkElement element, bool usesTranslation, bool canResize)
        {
            RegisterLayoutElement(new LayoutElementDescriptor
            {
                Key = key,
                DisplayName = displayName,
                Element = element,
                UsesTranslation = usesTranslation,
                CanResize = canResize,
                IsAnalog = false
            });
        }

        private void RegisterLayoutElement(LayoutElementDescriptor descriptor)
        {
            _layoutElements[descriptor.Key] = descriptor;
            descriptor.Element.PointerPressed += LayoutElement_PointerPressed;
            descriptor.Element.PointerMoved += LayoutElement_PointerMoved;
            descriptor.Element.PointerReleased += LayoutElement_PointerReleased;
            descriptor.Element.PointerCanceled += LayoutElement_PointerReleased;
        }

        private void LoadControllerLayoutEditor()
        {
            if (!_defaultVisualLayouts.ContainsKey(ControllerVisualSkin.Xbox))
            {
                ControllerVisualLayout xboxDefault = CaptureVisualLayout(false);
                _defaultVisualLayouts[ControllerVisualSkin.Xbox] = xboxDefault;
                _defaultVisualLayouts[ControllerVisualSkin.PlayStation5] =
                    CreatePlayStation5DefaultLayout(xboxDefault);
            }

            ControllerVisualSkin savedSkin = _controllerSettings.GetControllerVisualSkin();
            ApplyControllerVisualSkin(savedSkin, loadSavedLayout: true, persistSelection: false);
            SetAlignmentGuidesVisible(false);
            UpdateEditorButtons();
            UpdateLayoutEditorFields();
        }

        private void EditControllerLayoutButton_Click(object sender, RoutedEventArgs e) =>
            BeginLayoutEditor(ControllerLayoutEditorMode.AnalogAlignment);

        private void EditFullControllerLayoutButton_Click(object sender, RoutedEventArgs e) =>
            BeginLayoutEditor(ControllerLayoutEditorMode.FullLayout);

        private void BeginLayoutEditor(ControllerLayoutEditorMode mode)
        {
            if (_layoutEditMode)
                return;

            _preEditVisualLayout = CaptureVisualLayout(false);
            _preEditCustomRendering = false;
            _layoutEditorMode = mode;
            _selectedLayoutElement = null;
            ControllerLayoutEditorBar.Visibility = Visibility.Visible;

            if (mode == ControllerLayoutEditorMode.AnalogAlignment)
            {
                LayoutEditorModeTitle.Text = "AJUSTE DA HITBOX DOS ANALÓGICOS";
                LayoutSelectedElementText.Text = "Clique em um analógico";
                LayoutEditorInstructionText.Text = "Arraste o anel ou use as setas para mover 1 px.";
                AnalogEditorActionPanel.Visibility = Visibility.Visible;
                FullEditorActionPanel.Visibility = Visibility.Collapsed;
                LayoutSizeControls.Visibility = Visibility.Collapsed;
                SetAlignmentGuidesVisible(true);
                SetCustomControllerRendering(_hasFullCustomLayout);
                SelectedPartHintTextBlock.Text = "Ajuste de analógico: mova apenas a hitbox/camada do stick. Todo o restante do controle permanece fixo.";
            }
            else
            {
                bool ps5 = _controllerVisualSkin == ControllerVisualSkin.PlayStation5;
                LayoutEditorModeTitle.Text = ps5 ? "EDITOR DE HITBOXES PS5" : "EDITOR VISUAL DO CONTROLE";
                LayoutSelectedElementText.Text = "Selecione um elemento";
                LayoutEditorInstructionText.Text = ps5
                    ? "Arraste a hitbox sobre a arte PS5 • setas movem 1 px • o input continua XInput internamente."
                    : "Arraste o elemento • setas movem 1 px • botões permitem ajuste fino.";
                AnalogEditorActionPanel.Visibility = Visibility.Collapsed;
                FullEditorActionPanel.Visibility = Visibility.Visible;
                LayoutSizeControls.Visibility = Visibility.Visible;
                SetAlignmentGuidesVisible(false);
                SetCustomControllerRendering(false);
                SelectedPartHintTextBlock.Text = ps5
                    ? "PS5: ajuste apenas as hitboxes sobre a arte original. O host continua recebendo Xbox 360 virtual."
                    : "Editor visual: arraste botões, D-pad, gatilhos e analógicos. A lógica real do input não é alterada.";
            }

            UpdateEditorButtons();
            UpdateLayoutEditorFields();
            Focus(FocusState.Programmatic);
        }

        private void SaveControllerLayoutButton_Click(object sender, RoutedEventArgs e)
        {
            // Full layout editing changes hitboxes, not the original artwork.
            bool fullRendering = false;
            ControllerVisualLayout layout = CaptureVisualLayout(false);
            if (!_layoutService.Save(_controllerVisualSkin, layout))
            {
                LayoutCoordinatesText.Text = _layoutEditorMode == ControllerLayoutEditorMode.AnalogAlignment
                    ? "Não foi possível salvar o alinhamento."
                    : "Não foi possível salvar o layout.";
                return;
            }

            _hasCustomVisualLayout = true;
            _hasFullCustomLayout = fullRendering;
            ControllerLayoutEditorMode completedMode = _layoutEditorMode;
            _layoutEditorMode = ControllerLayoutEditorMode.None;
            _preEditVisualLayout = null;
            _selectedLayoutElement = null;
            ControllerLayoutEditorBar.Visibility = Visibility.Collapsed;
            AnalogEditorActionPanel.Visibility = Visibility.Collapsed;
            FullEditorActionPanel.Visibility = Visibility.Collapsed;
            SetAlignmentGuidesVisible(false);
            SetCustomControllerRendering(_hasFullCustomLayout);
            UpdateEditorButtons();
            UpdateLayoutEditorFields();
            SelectedPartHintTextBlock.Text = completedMode == ControllerLayoutEditorMode.AnalogAlignment
                ? "Alinhamento dos analógicos salvo • o layout visual e o input continuam independentes."
                : (_controllerVisualSkin == ControllerVisualSkin.PlayStation5
                    ? "Hitboxes PS5 salvas • a arte permanece original e o input continua XInput internamente."
                    : "Layout visual salvo • o input real continua independente da posição dos elementos.");
        }

        private void CancelControllerLayoutButton_Click(object sender, RoutedEventArgs e)
        {
            if (_preEditVisualLayout is not null)
                ApplyVisualLayout(_preEditVisualLayout);

            _layoutEditorMode = ControllerLayoutEditorMode.None;
            _preEditVisualLayout = null;
            _selectedLayoutElement = null;
            ControllerLayoutEditorBar.Visibility = Visibility.Collapsed;
            AnalogEditorActionPanel.Visibility = Visibility.Collapsed;
            FullEditorActionPanel.Visibility = Visibility.Collapsed;
            SetAlignmentGuidesVisible(false);
            SetCustomControllerRendering(_preEditCustomRendering);
            _hasFullCustomLayout = false;
            UpdateEditorButtons();
            UpdateLayoutEditorFields();
            SelectedPartHintTextBlock.Text = _hasCustomVisualLayout
                ? "Edição cancelada • configuração visual salva preservada."
                : "Edição cancelada • layout padrão preservado.";
        }

        private void ResetControllerLayoutButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_defaultVisualLayouts.TryGetValue(_controllerVisualSkin, out ControllerVisualLayout? defaultLayout))
                return;

            if (_layoutEditorMode == ControllerLayoutEditorMode.AnalogAlignment)
            {
                ApplyDefaultAnalogPositions();
                ControllerVisualLayout current = CaptureVisualLayout(_hasFullCustomLayout);
                bool preserveOtherCustomElements =
                    _hasFullCustomLayout ||
                    (_controllerVisualSkin == ControllerVisualSkin.PlayStation5 && _hasCustomVisualLayout);
                if (preserveOtherCustomElements)
                {
                    _layoutService.Save(_controllerVisualSkin, current);
                    _hasCustomVisualLayout = true;
                }
                else
                {
                    _layoutService.Reset(_controllerVisualSkin);
                    _hasCustomVisualLayout = false;
                }

                _layoutEditorMode = ControllerLayoutEditorMode.None;
                _preEditVisualLayout = null;
                _selectedLayoutElement = null;
                ControllerLayoutEditorBar.Visibility = Visibility.Collapsed;
                AnalogEditorActionPanel.Visibility = Visibility.Collapsed;
                FullEditorActionPanel.Visibility = Visibility.Collapsed;
                SetAlignmentGuidesVisible(false);
                SetCustomControllerRendering(_hasFullCustomLayout);
                UpdateEditorButtons();
                UpdateLayoutEditorFields();
                SelectedPartHintTextBlock.Text = "Posição padrão dos analógicos restaurada.";
                return;
            }

            ApplyVisualLayout(defaultLayout);
            _layoutService.Reset(_controllerVisualSkin);
            _hasCustomVisualLayout = false;
            _hasFullCustomLayout = false;
            _layoutEditorMode = ControllerLayoutEditorMode.None;
            _preEditVisualLayout = null;
            _selectedLayoutElement = null;
            ControllerLayoutEditorBar.Visibility = Visibility.Collapsed;
            AnalogEditorActionPanel.Visibility = Visibility.Collapsed;
            FullEditorActionPanel.Visibility = Visibility.Collapsed;
            SetAlignmentGuidesVisible(false);
            SetCustomControllerRendering(false);
            UpdateEditorButtons();
            UpdateLayoutEditorFields();
            SelectedPartHintTextBlock.Text = "Layout padrão completo restaurado.";
        }

        private void ApplyDefaultAnalogPositions()
        {
            if (!_defaultVisualLayouts.TryGetValue(_controllerVisualSkin, out ControllerVisualLayout? defaultLayout))
                return;

            foreach (string key in _analogLayoutKeys)
            {
                if (!_layoutElements.TryGetValue(key, out LayoutElementDescriptor? descriptor) ||
                    !defaultLayout.Elements.TryGetValue(key, out ControllerVisualElementLayout? entry))
                    continue;

                SetElementPosition(descriptor, entry.X, entry.Y);
            }
        }

        private void UpdateEditorButtons()
        {
            bool enabled = !_layoutEditMode;
            EditControllerLayoutButton.IsEnabled = enabled;
            EditFullControllerLayoutButton.IsEnabled = enabled;
            ControllerSkinComboBox.IsEnabled = enabled;
            EditControllerLayoutButton.Content = "AJUSTAR ANALÓGICOS";
            EditFullControllerLayoutButton.Content = _controllerVisualSkin == ControllerVisualSkin.PlayStation5
                ? "AJUSTAR HITBOXES"
                : (_hasFullCustomLayout ? "EDITAR LAYOUT" : "EDITAR CONTROLE");
        }

        private void LayoutElement_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (!_layoutEditMode)
                return;

            LayoutElementDescriptor? descriptor = ResolveLayoutDescriptor(sender as FrameworkElement);
            if (descriptor is null)
                return;

            _selectedLayoutElement = descriptor;
            SelectControllerPart(descriptor.Key is "DPAD" ? "DPAD_UP" : descriptor.Key);
            UIElement coordinateSpace = descriptor.UsesTranslation ? ShoulderStrip : ControllerVectorStage;
            _layoutDragStartPoint = e.GetCurrentPoint(coordinateSpace).Position;
            ReadElementPosition(descriptor, out _layoutDragStartX, out _layoutDragStartY);
            _layoutDragActive = true;
            descriptor.Element.CapturePointer(e.Pointer);
            UpdateLayoutEditorFields();
            e.Handled = true;
        }

        private void LayoutElement_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_layoutEditMode || !_layoutDragActive || _selectedLayoutElement is null)
                return;

            LayoutElementDescriptor descriptor = _selectedLayoutElement;
            UIElement coordinateSpace = descriptor.UsesTranslation ? ShoulderStrip : ControllerVectorStage;
            Point point = e.GetCurrentPoint(coordinateSpace).Position;
            double x = _layoutDragStartX + (point.X - _layoutDragStartPoint.X);
            double y = _layoutDragStartY + (point.Y - _layoutDragStartPoint.Y);
            SetElementPosition(descriptor, x, y);
            UpdateLayoutEditorFields();
            e.Handled = true;
        }

        private void LayoutElement_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_layoutEditMode)
                return;

            _layoutDragActive = false;
            if (sender is UIElement element)
                element.ReleasePointerCapture(e.Pointer);
            UpdateLayoutEditorFields();
            e.Handled = true;
        }

        private LayoutElementDescriptor? ResolveLayoutDescriptor(FrameworkElement? element)
        {
            if (element is null)
                return null;

            LayoutElementDescriptor? descriptor = null;
            foreach (LayoutElementDescriptor candidate in _layoutElements.Values)
            {
                if (ReferenceEquals(candidate.Element, element))
                {
                    descriptor = candidate;
                    break;
                }
            }

            if (descriptor is null)
            {
                string? tag = element.Tag as string;
                if (tag is "DPAD_UP" or "DPAD_DOWN" or "DPAD_LEFT" or "DPAD_RIGHT")
                    _layoutElements.TryGetValue("DPAD", out descriptor);
                else if (tag == "L3")
                    _layoutElements.TryGetValue("LEFT_STICK", out descriptor);
                else if (tag == "R3")
                    _layoutElements.TryGetValue("RIGHT_STICK", out descriptor);
                else if (tag is not null)
                    _layoutElements.TryGetValue(tag, out descriptor);
            }

            if (descriptor is null)
                return null;

            if (_layoutEditorMode == ControllerLayoutEditorMode.AnalogAlignment && !descriptor.IsAnalog)
                return null;

            return descriptor;
        }

        private void SelectLayoutElementFromControllerPart(FrameworkElement element)
        {
            if (!_layoutEditMode)
                return;

            LayoutElementDescriptor? descriptor = ResolveLayoutDescriptor(element);
            if (descriptor is null)
                return;

            _selectedLayoutElement = descriptor;
            UpdateLayoutEditorFields();
        }

        private void LayoutNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (!_layoutEditMode || _layoutEditorUiLoading || _selectedLayoutElement is null)
                return;

            LayoutElementDescriptor descriptor = _selectedLayoutElement;
            double x = double.IsNaN(LayoutXNumberBox.Value) ? 0 : LayoutXNumberBox.Value;
            double y = double.IsNaN(LayoutYNumberBox.Value) ? 0 : LayoutYNumberBox.Value;
            SetElementPosition(descriptor, x, y);

            if (_layoutEditorMode == ControllerLayoutEditorMode.FullLayout && descriptor.CanResize)
            {
                double width = Math.Max(20, double.IsNaN(LayoutWidthNumberBox.Value) ? ResolveElementWidth(descriptor.Element) : LayoutWidthNumberBox.Value);
                double height = Math.Max(20, double.IsNaN(LayoutHeightNumberBox.Value) ? ResolveElementHeight(descriptor.Element) : LayoutHeightNumberBox.Value);
                descriptor.Element.Width = width;
                descriptor.Element.Height = height;
            }

            UpdateLayoutEditorFields();
        }

        private void ControllerPage_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (!_layoutEditMode || _selectedLayoutElement is null)
                return;

            const double step = 1;
            double dx = 0;
            double dy = 0;
            switch (e.Key)
            {
                case VirtualKey.Left: dx = -step; break;
                case VirtualKey.Right: dx = step; break;
                case VirtualKey.Up: dy = -step; break;
                case VirtualKey.Down: dy = step; break;
                default: return;
            }

            ReadElementPosition(_selectedLayoutElement, out double x, out double y);
            SetElementPosition(_selectedLayoutElement, x + dx, y + dy);
            UpdateLayoutEditorFields();
            e.Handled = true;
        }

        private void UpdateLayoutEditorFields()
        {
            _layoutEditorUiLoading = true;
            try
            {
                if (_selectedLayoutElement is null)
                {
                    LayoutSelectedElementText.Text = _layoutEditorMode == ControllerLayoutEditorMode.AnalogAlignment
                        ? "Clique em um analógico"
                        : "Selecione um elemento";
                    LayoutCoordinatesText.Text = _layoutEditorMode == ControllerLayoutEditorMode.AnalogAlignment
                        ? "X -- • Y -- • Centro X -- • Centro Y --"
                        : "X -- • Y -- • W -- • H --";
                    LayoutXNumberBox.Value = double.NaN;
                    LayoutYNumberBox.Value = double.NaN;
                    LayoutWidthNumberBox.Value = double.NaN;
                    LayoutHeightNumberBox.Value = double.NaN;
                    LayoutWidthNumberBox.IsEnabled = false;
                    LayoutHeightNumberBox.IsEnabled = false;
                    return;
                }

                LayoutElementDescriptor descriptor = _selectedLayoutElement;
                ReadElementPosition(descriptor, out double x, out double y);
                double width = ResolveElementWidth(descriptor.Element);
                double height = ResolveElementHeight(descriptor.Element);
                LayoutSelectedElementText.Text = descriptor.DisplayName;
                LayoutXNumberBox.Value = Math.Round(x, 2);
                LayoutYNumberBox.Value = Math.Round(y, 2);
                LayoutWidthNumberBox.Value = Math.Round(width, 2);
                LayoutHeightNumberBox.Value = Math.Round(height, 2);
                LayoutWidthNumberBox.IsEnabled = _layoutEditorMode == ControllerLayoutEditorMode.FullLayout && descriptor.CanResize;
                LayoutHeightNumberBox.IsEnabled = _layoutEditorMode == ControllerLayoutEditorMode.FullLayout && descriptor.CanResize;

                if (descriptor.IsAnalog)
                {
                    double centerX = x + width / 2.0;
                    double centerY = y + height / 2.0;
                    LayoutCoordinatesText.Text = $"X {x:0.00} • Y {y:0.00} • Centro X {centerX:0.00} • Centro Y {centerY:0.00}";
                }
                else if (descriptor.UsesTranslation)
                {
                    LayoutCoordinatesText.Text = $"Offset X {x:0.00} • Y {y:0.00} • W {width:0.0} • H {height:0.0}";
                }
                else
                {
                    LayoutCoordinatesText.Text = $"X {x:0.00} • Y {y:0.00} • W {width:0.0} • H {height:0.0}";
                }
            }
            finally
            {
                _layoutEditorUiLoading = false;
            }
        }

        private ControllerVisualLayout CaptureVisualLayout(bool useCustomControllerRendering)
        {
            ControllerVisualLayout layout = new() { UseCustomControllerRendering = useCustomControllerRendering };
            foreach (LayoutElementDescriptor descriptor in _layoutElements.Values)
            {
                ReadElementPosition(descriptor, out double x, out double y);
                layout.Elements[descriptor.Key] = new ControllerVisualElementLayout
                {
                    X = x,
                    Y = y,
                    Width = ResolveElementWidth(descriptor.Element),
                    Height = ResolveElementHeight(descriptor.Element),
                    UsesTranslation = descriptor.UsesTranslation
                };
            }
            return layout;
        }

        private void ApplyVisualLayout(ControllerVisualLayout layout)
        {
            foreach (LayoutElementDescriptor descriptor in _layoutElements.Values)
            {
                if (!layout.Elements.TryGetValue(descriptor.Key, out ControllerVisualElementLayout? entry))
                    continue;

                SetElementPosition(descriptor, entry.X, entry.Y);
                if (descriptor.CanResize && entry.Width >= 20 && entry.Height >= 20)
                {
                    descriptor.Element.Width = entry.Width;
                    descriptor.Element.Height = entry.Height;
                }
            }
            UpdateLayoutEditorFields();
        }

        private void SetElementPosition(LayoutElementDescriptor descriptor, double x, double y)
        {
            if (descriptor.UsesTranslation)
            {
                double limitedX = Math.Clamp(x, -600, 600);
                double limitedY = Math.Clamp(y, -180, 500);
                if (descriptor.Element.RenderTransform is not TranslateTransform transform)
                {
                    transform = new TranslateTransform();
                    descriptor.Element.RenderTransform = transform;
                }
                transform.X = limitedX;
                transform.Y = limitedY;
                return;
            }

            double width = ResolveElementWidth(descriptor.Element);
            double height = ResolveElementHeight(descriptor.Element);
            double maxX = Math.Max(0, ControllerVectorStage.Width - width);
            double maxY = Math.Max(0, ControllerVectorStage.Height - height);
            Canvas.SetLeft(descriptor.Element, Math.Clamp(x, 0, maxX));
            Canvas.SetTop(descriptor.Element, Math.Clamp(y, 0, maxY));
        }

        private static void ReadElementPosition(LayoutElementDescriptor descriptor, out double x, out double y)
        {
            if (descriptor.UsesTranslation)
            {
                if (descriptor.Element.RenderTransform is TranslateTransform transform)
                {
                    x = transform.X;
                    y = transform.Y;
                }
                else
                {
                    x = 0;
                    y = 0;
                }
                return;
            }

            x = Canvas.GetLeft(descriptor.Element);
            y = Canvas.GetTop(descriptor.Element);
            if (double.IsNaN(x)) x = 0;
            if (double.IsNaN(y)) y = 0;
        }

        private static double ResolveElementWidth(FrameworkElement element) =>
            !double.IsNaN(element.Width) && element.Width > 0 ? element.Width : Math.Max(1, element.ActualWidth);

        private static double ResolveElementHeight(FrameworkElement element) =>
            !double.IsNaN(element.Height) && element.Height > 0 ? element.Height : Math.Max(1, element.ActualHeight);

        private void LoadControllerFeedbackPreference()
        {
            // New profiles show the supplied artwork without overlays. The choice
            // is a visual preference only and never changes controller input.
            bool visible = _controllerSettings.GetBool(ControllerFeedbackVisibilityKey, false);
            _controllerFeedbackUiLoading = true;
            try
            {
                _controllerFeedbackVisible = visible;
                ControllerFeedbackToggle.IsOn = visible;
            }
            finally
            {
                _controllerFeedbackUiLoading = false;
            }
            ApplyAnalogFeedbackVisibility();
        }

        private void ControllerFeedbackToggle_Toggled(object sender, RoutedEventArgs e)
        {
            // XAML can raise Toggled before InitializeComponent has completed.
            if (!_uiInitialized || _controllerFeedbackUiLoading)
                return;

            _controllerFeedbackVisible = ControllerFeedbackToggle.IsOn;
            _controllerSettings.Set(ControllerFeedbackVisibilityKey, _controllerFeedbackVisible);
            ApplyAnalogFeedbackVisibility();
        }

        private static void SetOverlayVisibility(UIElement element, Visibility state)
        {
            if (element.Visibility != state)
                element.Visibility = state;
        }

        private void ApplyAnalogFeedbackVisibility()
        {
            // Keep the real SVG as the sole static artwork. All feedback is an
            // optional, non-hit-testable layer. Editing and diagnostics can show
            // the guides without changing the saved user preference.
            bool showFeedback = _controllerFeedbackVisible || _layoutEditMode || _diagnosticRunning;
            Visibility state = showFeedback ? Visibility.Visible : Visibility.Collapsed;
            SetOverlayVisibility(LeftStickPhysicalGuide, state);
            SetOverlayVisibility(RightStickPhysicalGuide, state);
            SetOverlayVisibility(LeftStickOuterRing, state);
            SetOverlayVisibility(RightStickOuterRing, state);
            SetOverlayVisibility(LeftStickLiveDot, state);
            SetOverlayVisibility(RightStickLiveDot, state);
            SetOverlayVisibility(LeftStickDeadzoneRing,
                showFeedback && _tuning.LeftStickDeadzonePercent > 0 ? Visibility.Visible : Visibility.Collapsed);
            SetOverlayVisibility(RightStickDeadzoneRing,
                showFeedback && _tuning.RightStickDeadzonePercent > 0 ? Visibility.Visible : Visibility.Collapsed);
            SetOverlayVisibility(LeftStickAntiRing,
                showFeedback && _tuning.LeftStickAntiDeadzonePercent > 0 ? Visibility.Visible : Visibility.Collapsed);
            SetOverlayVisibility(RightStickAntiRing,
                showFeedback && _tuning.RightStickAntiDeadzonePercent > 0 ? Visibility.Visible : Visibility.Collapsed);
            Visibility alignmentState = _analogAlignmentGuidesRequested &&
                _layoutEditorMode == ControllerLayoutEditorMode.AnalogAlignment
                ? Visibility.Visible : Visibility.Collapsed;
            SetOverlayVisibility(LeftStickAlignmentGuide, alignmentState);
            SetOverlayVisibility(RightStickAlignmentGuide, alignmentState);
        }

        private void SetAlignmentGuidesVisible(bool visible)
        {
            _analogAlignmentGuidesRequested = visible;
            ApplyAnalogFeedbackVisibility();
        }

        private void SetCustomControllerRendering(bool enabled)
        {
            // CORE 11.73a: restore the supplied Xbox/PS5 SVGs at all times.
            // This is intentionally visual-only: saved element coordinates,
            // tap handlers, live stick rings and gamepad routing are unchanged.
            ControllerBaseImage.Visibility =
                _controllerVisualSkin == ControllerVisualSkin.Xbox
                    ? Visibility.Visible : Visibility.Collapsed;
            ControllerPs5Image.Visibility =
                _controllerVisualSkin == ControllerVisualSkin.PlayStation5
                    ? Visibility.Visible : Visibility.Collapsed;
            ControllerShellImage.Visibility = Visibility.Collapsed;

            LeftStickCustomBody.Visibility = Visibility.Collapsed;
            RightStickCustomBody.Visibility = Visibility.Collapsed;
            DpadCustomVisual.Visibility = Visibility.Collapsed;
            AButtonCustomVisual.Visibility = Visibility.Collapsed;
            BButtonCustomVisual.Visibility = Visibility.Collapsed;
            XButtonCustomVisual.Visibility = Visibility.Collapsed;
            YButtonCustomVisual.Visibility = Visibility.Collapsed;
            BackButtonCustomVisual.Visibility = Visibility.Collapsed;
            StartButtonCustomVisual.Visibility = Visibility.Collapsed;
            ApplyAnalogFeedbackVisibility();
        }
    }
}
