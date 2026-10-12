using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Rodaje.App.Services;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Views.Inspector.Properties;
using Rodaje.Core.Commands;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Render.Effects;
using C = Rodaje.Core.Model.BuiltInEffects.ColorParameters;
using Q = Rodaje.Core.Model.BuiltInEffects.HslQualifierParameters;

namespace Rodaje.App.Views.Inspector.Filters;

/// <summary>
/// Lo que va entre la pila y los parámetros: las opciones de elección de cualquier filtro (tipo de desenfoque, modo, zona
/// respecto de las máscaras), las herramientas de «Color y LUT» (cuentagotas de blanco y exposición en tercios de paso), las
/// del calificador (cuentagotas y ver la máscara), el A/B por efecto y el costo de cada uno.
/// </summary>
internal sealed partial class FiltersPanel
{
    /// <summary>Las opciones que tienen su propia fila (o están en Propiedades) y no van como elección genérica.</summary>
    private static readonly HashSet<(string Type, string Option)> OwnRows =
    [
        (BuiltInEffects.Mask, BuiltInEffects.MaskOptions.Shape),
        (BuiltInEffects.Mask, BuiltInEffects.MaskOptions.Invert),
        (BuiltInEffects.Crop, BuiltInEffects.CropOptions.Aspect),
        (BuiltInEffects.Blend, BuiltInEffects.BlendOptions.Mode),
    ];

    private readonly List<(OptionDefinition Option, ComboBox Box)> _choices = [];

    private EffectCostEstimate? Cost(EffectInstance effect)
    {
        if (_services is null || EffectRegistry.Find(effect.Type) is not { Stage: EffectStage.PerPixel or EffectStage.Spatial } definition)
        {
            return null;
        }

        Sequence sequence = _services.Session.Sequence;
        EffectCostEstimate estimate = EffectCost.Estimate(effect.Type, option => effect.Options.GetValueOrDefault(option), sequence.Width, sequence.Height);
        double intermediate = definition.Stage == EffectStage.Spatial
            ? EffectCost.IntermediateMs1080 * sequence.Width * sequence.Height / (1920.0 * 1080)
            : 0;
        return estimate with { Milliseconds = estimate.Milliseconds + intermediate };
    }

    private void BuildChoices(string type)
    {
        ChoiceRows.Children.Clear();
        _choices.Clear();
        if (EffectRegistry.Find(type) is not { } definition)
        {
            return;
        }

        foreach (OptionDefinition option in definition.Options)
        {
            if (option.Kind != OptionKind.Choice || OwnRows.Contains((type, option.Name)))
            {
                continue;
            }

            var row = new Grid { Padding = new Thickness(12, 3, 6, 3), ColumnSpacing = 4 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            string label = EffectUi.OptionLabel(option.Name);
            var text = new TextBlock { Text = label, Style = (Style)Application.Current.Resources["LabelTextStyle"] };
            var box = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinWidth = 0,
                Height = 28,
                FontSize = (double)Application.Current.Resources["BodyFontSize"],
                Tag = option.Name,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, label);
            if (option.Name == BuiltInEffects.RegionOptions.Region)
            {
                ToolTipService.SetToolTip(box, "Con una máscara en uso «Limitar efectos» (pestaña Máscaras), el efecto va solo dentro o solo fuera de ella");
            }

            foreach (string choice in option.Choices)
            {
                box.Items.Add(EffectUi.ChoiceName(option.Name, choice));
            }

            box.SelectionChanged += OnChoiceSelected;
            Grid.SetColumn(box, 1);
            row.Children.Add(text);
            row.Children.Add(box);
            ChoiceRows.Children.Add(row);
            _choices.Add((option, box));
        }
    }

    private void ShowChoices(EffectInstance effect)
    {
        _updating = true;
        foreach ((OptionDefinition option, ComboBox box) in _choices)
        {
            int index = option.Choices.IndexOf(effect.Options.GetValueOrDefault(option.Name) ?? option.DefaultValue);
            box.SelectedIndex = Math.Max(0, index);
        }

        _updating = false;
    }

    private void OnChoiceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || sender is not ComboBox { Tag: string name, SelectedIndex: >= 0 } box || _effectId is not { } effectId
            || _clipId is null || _services is null || _choices.Find(c => ReferenceEquals(c.Box, box)).Option is not { } option)
        {
            return;
        }

        _services.Session.Execute(Edits.SetEffectOption(_services.Session.SequenceId, _clipId, effectId, name, option.Choices[box.SelectedIndex]));
    }

    /// <summary>Los parámetros que no hacen nada con la opción elegida se esconden (el ángulo solo en el desenfoque direccional…).</summary>
    private void ShowRowVisibility(EffectInstance effect)
    {
        string? Option(string name) => effect.Options.GetValueOrDefault(name);
        foreach (ParameterRow row in _rows)
        {
            bool visible = (effect.Type, row.Spec.Name) switch
            {
                (BuiltInEffects.Blur, BuiltInEffects.BlurParameters.Angle) => Option(BuiltInEffects.BlurOptions.Kind) == "directional",
                (BuiltInEffects.Blur, BuiltInEffects.BlurParameters.CenterX or BuiltInEffects.BlurParameters.CenterY)
                    => Option(BuiltInEffects.BlurOptions.Kind) == "radial",
                (BuiltInEffects.ChromaticAberration, BuiltInEffects.ChromaticAberrationParameters.Angle)
                    => Option(BuiltInEffects.ChromaticAberrationOptions.Mode) == "lateral",
                (BuiltInEffects.Mirror, BuiltInEffects.MirrorParameters.Tiles) => Option(BuiltInEffects.MirrorOptions.Mode) == "tiles",
                (BuiltInEffects.Mirror, BuiltInEffects.MirrorParameters.Axis) => Option(BuiltInEffects.MirrorOptions.Mode) != "tiles",
                _ => true,
            };
            row.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ShowColorTools(Clip clip, EffectInstance effect)
    {
        ColorToolsRow.Visibility = effect.Type == BuiltInEffects.Color ? Visibility.Visible : Visibility.Collapsed;
        bool qualifier = effect.Type == BuiltInEffects.HslQualifier;
        QualifierRow.Visibility = qualifier ? Visibility.Visible : Visibility.Collapsed;
        if (!qualifier)
        {
            SetMaskView(false);
            return;
        }

        Time at = clip.ToEffectTime(_services!.Session.Position);
        double hue = effect.Parameters.TryGetValue(Q.HueCenter, out Parameter? p) ? p.Evaluate(at) : EffectRegistry.DefaultValue(effect.Type, Q.HueCenter);
        HueSwatch.Background = new SolidColorBrush(HueColor(hue));
    }

    /// <summary>El color puro de un tono en grados, para la muestra del calificador.</summary>
    private static Windows.UI.Color HueColor(double degrees)
    {
        double h = ((degrees % 360) + 360) % 360 / 60;
        double x = 1 - Math.Abs((h % 2) - 1);
        (double r, double g, double b) = (int)h switch
        {
            0 => (1.0, x, 0.0),
            1 => (x, 1.0, 0.0),
            2 => (0.0, 1.0, x),
            3 => (0.0, x, 1.0),
            4 => (x, 0.0, 1.0),
            _ => (1.0, 0.0, x),
        };
        static byte B(double v) => (byte)Math.Round(40 + (v * 190));
        return ColorHelper.FromArgb(255, B(r), B(g), B(b));
    }

    /// <summary>Al dejar el clip: vuelven todos los efectos al visor y se apaga la vista de la máscara.</summary>
    private void LeaveClip()
    {
        _services?.Session.ClearPreviewOff();
        SetMaskView(false);
    }

    private void SetMaskView(bool show)
    {
        MaskViewButton.IsChecked = show;
        if (_maskViewOn != show)
        {
            _maskViewOn = show;
            _services?.Player.SetQualifierMaskView(show);
        }
    }

    private bool _maskViewOn;

    private void OnMaskViewClick(object sender, RoutedEventArgs e) => SetMaskView(MaskViewButton.IsChecked == true);

    private void OnCompareClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string effectId } toggle && _services is not null)
        {
            _services.Session.SetPreviewOff(effectId, toggle.IsChecked == true);
        }
    }

    private void OnPickWhiteClick(object sender, RoutedEventArgs e)
    {
        if (_services is not null && _clipId is not null && _effectId is not null)
        {
            _services.Session.RequestColorPick(_clipId, _effectId, ColorPick.WhiteBalance);
        }
    }

    private void OnPickQualifierClick(object sender, RoutedEventArgs e)
    {
        if (_services is not null && _clipId is not null && _effectId is not null)
        {
            _services.Session.RequestColorPick(_clipId, _effectId, ColorPick.Qualifier);
        }
    }

    /// <summary>Exposición al tercio de paso más cercano, más o menos un tercio (un paso del historial).</summary>
    private void OnExposureStepClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || _editor is not { } editor || Editing() is not { } target
            || _services is null)
        {
            return;
        }

        int direction = tag == "-1" ? -1 : 1;
        Time at = target.Clip.ToEffectTime(_services.Session.Position);
        EffectInstance? effect = target.Clip.Effects.Find(x => x.Id == target.EffectId);
        double current = effect?.Parameters.TryGetValue(C.Exposure, out Parameter? p) == true ? p!.Evaluate(at) : 0;
        double value = Math.Clamp((Math.Round(current * 3) + direction) / 3, -4, 4);
        editor.SetValue(target.Clip, target.Type, C.Exposure, Math.Round(value, 4),
            direction > 0 ? "Subir un tercio de paso" : "Bajar un tercio de paso", target.EffectId);
    }
}
