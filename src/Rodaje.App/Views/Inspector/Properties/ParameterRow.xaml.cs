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

    private readonly DispatcherQueueTimer _settle;
    private ParameterSpec? _spec;
    private bool _updating;
    private bool _pointerDown;
    private bool _changing;

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
    }

    /// <summary>Valor del modelo mientras se arrastra o se usa el teclado sobre el deslizador.</summary>
    public event EventHandler<double>? Changing;

    public event EventHandler? ChangeEnded;

    /// <summary>Valor del modelo escrito en el número.</summary>
    public event EventHandler<double>? Committed;

    public event EventHandler? KeyframeToggled;

    public event EventHandler<int>? NavigateRequested;

    public event EventHandler<Interpolation>? InterpolationChosen;

    public ParameterSpec Spec => _spec ?? throw new InvalidOperationException("La fila no está configurada.");

    public bool IsChanging => _changing;

    public void Configure(ParameterSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        _spec = spec;
        _updating = true;
        LabelText.Text = spec.Label;
        UnitText.Text = spec.Unit;
        ValueSlider.Minimum = spec.Minimum;
        ValueSlider.Maximum = spec.Maximum;
        ValueSlider.StepFrequency = spec.Step;
        ValueSlider.SmallChange = spec.Step;
        ValueSlider.LargeChange = spec.Step * 10;
        ValueBox.SmallChange = spec.Step;
        ValueBox.NumberFormatter = Formatter(spec.Decimals);
        Visibility keyframes = spec.Animatable ? Visibility.Visible : Visibility.Collapsed;
        PreviousButton.Visibility = keyframes;
        KeyframeButton.Visibility = keyframes;
        NextButton.Visibility = keyframes;
        AutomationProperties.SetName(ValueSlider, spec.Label);
        AutomationProperties.SetName(ValueBox, spec.Label);
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
        DiamondHere.Visibility = state.KeyframeHere is not null ? Visibility.Visible : Visibility.Collapsed;
        DiamondAnimated.Visibility = state.IsAnimated && state.KeyframeHere is null ? Visibility.Visible : Visibility.Collapsed;
        DiamondEmpty.Visibility = state.IsAnimated ? Visibility.Collapsed : Visibility.Visible;
        KeyframeButton.IsEnabled = playheadInside;
        PreviousButton.IsEnabled = state.Previous is not null;
        NextButton.IsEnabled = state.Next is not null;

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
