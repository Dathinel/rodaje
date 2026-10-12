using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Rodaje.App.Services.Inspector;
using Rodaje.Core.Model;

namespace Rodaje.App.Views.Inspector.Properties;

/// <summary>
/// Un parámetro del inspector. No edita nada por su cuenta: avisa con eventos y el panel decide.
/// </summary>
/// <remarks>
/// Un arrastre del deslizador manda <see cref="Changing"/> por cada valor y <see cref="ChangeEnded"/> al soltar; con el
/// teclado, el final llega tras una pausa corta. El número escrito llega como <see cref="Committed"/>.
/// </remarks>
internal sealed partial class ParameterRow : UserControl
{
    private static readonly TimeSpan KeyboardSettle = TimeSpan.FromMilliseconds(350);

    /// <summary>Por debajo de este ancho el deslizador baja a su propia línea (le quedarían menos de ~100 px).</summary>
    private const double OneLineMinWidth = 340;

    private readonly DispatcherQueueTimer _settle;
    private ParameterSpec? _spec;
    private bool _updating;
    private bool _pointerDown;
    private bool _changing;
    private bool? _oneLine;

    public ParameterRow()
    {
        InitializeComponent();
        foreach ((string name, _) in KeyframeEase.Options)
        {
            EaseBox.Items.Add(name);
        }

        _settle = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _settle.IsRepeating = false;
        _settle.Tick += (_, _) => FinishChange();

        // El deslizador marca la pulsación como atendida; hace falta verla igual para saber cuándo termina el arrastre.
        ValueSlider.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _pointerDown = true), true);
        ValueSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => EndPointer()), true);
        ValueSlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => EndPointer()), true);
        ValueSlider.LostFocus += (_, _) => FinishChange();
        AttachScrub();
        SizeChanged += (_, e) => ApplyLayout(e.NewSize.Width);
        ApplyLayout(0);
    }

    /// <summary>Valor del modelo mientras se arrastra o se usa el teclado sobre el deslizador.</summary>
    public event EventHandler<double>? Changing;

    public event EventHandler? ChangeEnded;

    /// <summary>Valor del modelo escrito en el número.</summary>
    public event EventHandler<double>? Committed;

    public event EventHandler? KeyframeToggled;

    public event EventHandler<int>? NavigateRequested;

    public event EventHandler<Interpolation>? InterpolationChosen;

    public event EventHandler? ResetRequested;

    public event EventHandler? KeyframesClearRequested;

    public ParameterSpec Spec => _spec ?? throw new InvalidOperationException("La fila no está configurada.");

    public bool IsChanging => _changing;

    /// <param name="spec">El parámetro.</param>
    /// <param name="label">Etiqueta más corta que la del registro (en una sección que ya da el contexto).</param>
    /// <param name="axis">Eje a la derecha de la etiqueta: «X» e «Y» de la posición, en dos filas seguidas.</param>
    public void Configure(ParameterSpec spec, string? label = null, string? axis = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        _spec = spec;
        _updating = true;
        LabelText.Text = label ?? spec.Label;
        AxisText.Text = axis ?? "";
        AxisText.Visibility = axis is null ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(LabelArea, spec.Label + Environment.NewLine + "Arrastra a los lados para cambiar el valor (Mayús: fino; Ctrl: grueso). Doble clic: restablecer.");
        UnitText.Text = spec.Unit;
        ValueSlider.Minimum = spec.Minimum;
        ValueSlider.Maximum = spec.Maximum;
        ValueSlider.StepFrequency = spec.Step;
        ValueSlider.SmallChange = spec.Step;
        ValueSlider.LargeChange = spec.Step * 10;
        ValueBox.SmallChange = spec.Step;
        ValueBox.NumberFormatter = Formatter(spec.Decimals);
        KeyframeButton.Visibility = spec.Animatable ? Visibility.Visible : Visibility.Collapsed;
        string keyframeHelp = "Añadir o quitar keyframe de " + char.ToLowerInvariant(spec.Label[0]) + spec.Label[1..] + " en el cursor";
        ToolTipService.SetToolTip(KeyframeButton, keyframeHelp);
        AutomationProperties.SetName(KeyframeButton, keyframeHelp);
        AutomationProperties.SetName(ValueSlider, spec.Label);
        AutomationProperties.SetName(ValueBox, spec.Label);
        string reset = spec.ResetDescription;
        ToolTipService.SetToolTip(ResetButton, reset);
        AutomationProperties.SetName(ResetButton, reset);
        ClearKeyframesItem.Visibility = spec.Animatable ? Visibility.Visible : Visibility.Collapsed;
        _updating = false;
    }

    /// <param name="state">Estado en el cursor.</param>
    /// <param name="playheadInside">El cursor está dentro del clip: solo ahí se ponen keyframes.</param>
    public void Show(ParameterState state, bool playheadInside)
    {
        ArgumentNullException.ThrowIfNull(state);
        ParameterSpec spec = Spec;
        _updating = true;
        double display = spec.ToDisplay(state.Value);
        if (!_changing)
        {
            ValueSlider.Value = Math.Clamp(display, spec.Minimum, spec.Maximum);
        }

        if (ValueBox.FocusState == FocusState.Unfocused)
        {
            ValueBox.Value = Math.Round(display, spec.Decimals);
        }

        bool editable = !state.IsAnimated || playheadInside;
        ValueSlider.IsEnabled = editable;
        ValueBox.IsEnabled = editable;

        CountText.Text = state.KeyframeCount switch
        {
            0 => "",
            1 => "1 keyframe",
            int n => n.ToString(CultureInfo.InvariantCulture) + " keyframes",
        };
        HereRing.Visibility = state.KeyframeHere is not null ? Visibility.Visible : Visibility.Collapsed;
        DiamondAnimated.Visibility = state.IsAnimated ? Visibility.Visible : Visibility.Collapsed;
        DiamondEmpty.Visibility = state.IsAnimated ? Visibility.Collapsed : Visibility.Visible;
        KeyframeButton.IsEnabled = playheadInside;
        PreviousButton.IsEnabled = state.Previous is not null;
        NextButton.IsEnabled = state.Next is not null;
        ResetButton.IsEnabled = !state.IsDefault(spec.DefaultValue);
        ResetItem.IsEnabled = ResetButton.IsEnabled;
        ClearKeyframesItem.IsEnabled = state.IsAnimated;
        KeyframeStrip.Visibility = state.IsAnimated && spec.Animatable ? Visibility.Visible : Visibility.Collapsed;

        if (state.KeyframeHere is { } here)
        {
            EaseBox.SelectedIndex = KeyframeEase.IndexOf(here.Interpolation);
            EaseBox.Visibility = Visibility.Visible;
        }
        else
        {
            EaseBox.Visibility = Visibility.Collapsed;
        }

        _updating = false;
    }

    /// <summary>Cierra el cambio en curso (por ejemplo, si el clip deja de estar seleccionado).</summary>
    public void FinishChange()
    {
        _settle.Stop();
        if (!_changing)
        {
            return;
        }

        _changing = false;
        ChangeEnded?.Invoke(this, EventArgs.Empty);
    }

    private static Windows.Globalization.NumberFormatting.DecimalFormatter Formatter(int decimals) => new()
    {
        FractionDigits = decimals,
        IntegerDigits = 1,
        IsGrouped = false,
        NumberRounder = new Windows.Globalization.NumberFormatting.IncrementNumberRounder
        {
            Increment = Math.Pow(10, -decimals),
            RoundingAlgorithm = Windows.Globalization.NumberFormatting.RoundingAlgorithm.RoundHalfAwayFromZero,
        },
    };

    // Con el panel ancho todo va en una línea; estrecho, el deslizador ocupa la segunda línea entera.
    private void ApplyLayout(double width)
    {
        bool oneLine = width >= OneLineMinWidth;
        if (_oneLine == oneLine)
        {
            return;
        }

        _oneLine = oneLine;
        Grid.SetColumnSpan(LabelArea, oneLine ? 1 : 2);
        Grid.SetRow(ValueSlider, oneLine ? 0 : 1);
        Grid.SetColumn(ValueSlider, oneLine ? 1 : 0);
        Grid.SetColumnSpan(ValueSlider, oneLine ? 1 : 4);
        ValueSlider.Margin = oneLine ? new Thickness(2, -6, 4, -6) : new Thickness(0, -4, 4, -6);
        Grid.SetRow(KeyframeStrip, oneLine ? 1 : 2);
        Grid.SetColumn(KeyframeStrip, oneLine ? 1 : 0);
        Grid.SetColumnSpan(KeyframeStrip, oneLine ? 5 : 6);
    }

    /// <summary>
    /// Arrastrar la etiqueta a los lados cambia el valor (el rango del deslizador en unos 300 px; con Mayús, diez veces más
    /// fino; con Ctrl, diez veces más grueso). Doble clic en la etiqueta restablece.
    /// </summary>
    private void AttachScrub()
    {
        double startX = 0;
        double startValue = 0;
        bool scrubbing = false;
        LabelArea.PointerEntered += (_, _) => ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeWestEast);
        LabelArea.PointerExited += (_, _) =>
        {
            if (!scrubbing)
            {
                ProtectedCursor = null;
            }
        };
        LabelArea.PointerPressed += (_, e) =>
        {
            if (_spec is null || !ValueSlider.IsEnabled || !e.GetCurrentPoint(LabelArea).Properties.IsLeftButtonPressed)
            {
                return;
            }

            scrubbing = LabelArea.CapturePointer(e.Pointer);
            _pointerDown = scrubbing;
            startX = e.GetCurrentPoint(this).Position.X;
            startValue = ValueSlider.Value;
            e.Handled = true;
        };
        LabelArea.PointerMoved += (_, e) =>
        {
            if (!scrubbing || _spec is null)
            {
                return;
            }

            var modifiers = e.KeyModifiers;
            double factor = modifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift) ? 0.1
                : modifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control) ? 10 : 1;
            double perPixel = (_spec.Maximum - _spec.Minimum) / 300 * factor;
            double value = startValue + ((e.GetCurrentPoint(this).Position.X - startX) * perPixel);
            double step = _spec.Step * (factor < 1 ? 0.1 : 1);
            value = Math.Round(value / step) * step;
            ValueSlider.Value = Math.Clamp(value, _spec.Minimum, _spec.Maximum);
            e.Handled = true;
        };
        void End()
        {
            if (scrubbing)
            {
                scrubbing = false;
                ProtectedCursor = null;
                EndPointer();
            }
        }

        LabelArea.PointerReleased += (_, e) =>
        {
            LabelArea.ReleasePointerCapture(e.Pointer);
            End();
        };
        LabelArea.PointerCaptureLost += (_, _) => End();
        LabelArea.DoubleTapped += (_, e) =>
        {
            if (ResetButton.IsEnabled)
            {
                OnResetClick(this, new RoutedEventArgs());
            }

            e.Handled = true;
        };
    }

    private void EndPointer()
    {
        _pointerDown = false;
        FinishChange();
    }

    private void OnSliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updating || _spec is null)
        {
            return;
        }

        _changing = true;
        Changing?.Invoke(this, _spec.ToModel(e.NewValue));
        ValueBox.Value = Math.Round(e.NewValue, _spec.Decimals);
        if (!_pointerDown)
        {
            _settle.Interval = KeyboardSettle;
            _settle.Start();
        }
    }

    private void OnBoxValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updating || _changing || _spec is null || double.IsNaN(args.NewValue))
        {
            return;
        }

        Committed?.Invoke(this, _spec.ToModel(args.NewValue));
    }

    private void OnKeyframeClick(object sender, RoutedEventArgs e)
    {
        FinishChange();
        KeyframeToggled?.Invoke(this, EventArgs.Empty);
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        FinishChange();
        ResetRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnClearKeyframesClick(object sender, RoutedEventArgs e)
    {
        FinishChange();
        KeyframesClearRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnPreviousClick(object sender, RoutedEventArgs e) => NavigateRequested?.Invoke(this, -1);

    private void OnNextClick(object sender, RoutedEventArgs e) => NavigateRequested?.Invoke(this, 1);

    private void OnEaseChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || EaseBox.SelectedIndex < 0)
        {
            return;
        }

        InterpolationChosen?.Invoke(this, KeyframeEase.Options[EaseBox.SelectedIndex].Value);
    }
}
