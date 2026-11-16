using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Titles;
using Rodaje.App.Theme;
using Rodaje.Packages.Content;
using Windows.UI;

namespace Rodaje.App.Views.Inspector.Template;

/// <summary>Una fila por parámetro, según su tipo: texto, color, fuente, número, casilla u opciones.</summary>
internal sealed partial class TemplatePanel
{
    private const double LabelWidth = 76;
    private const double NumberWidth = 56;

    private void BuildRows()
    {
        Rows.Children.Clear();
        _refreshers.Clear();
        foreach (TemplateParameter parameter in _document?.Parameters ?? [])
        {
            Rows.Children.Add(parameter.Type switch
            {
                TemplateParameterType.Text => TextRow(parameter),
                TemplateParameterType.Color => ColorRow(parameter),
                TemplateParameterType.Font => FontRow(parameter),
                TemplateParameterType.Number => NumberRow(parameter),
                TemplateParameterType.Bool => BoolRow(parameter),
                _ => ChoiceRow(parameter),
            });
        }

        EmptyText.Visibility = _refreshers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Grid TextRow(TemplateParameter parameter)
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 28,
            MaxHeight = 160,
            MaxLength = TemplateLimits.MaxTextLength,
            FontSize = ThemeResources.Size("BodyFontSize"),
        };
        AutomationProperties.SetName(box, parameter.Label);
        bool typing = false;
        box.TextChanged += (_, _) =>
        {
            typing = true;
            Commit(parameter, JsonValue.Create(TitleEditor.FromTextBox(box.Text))!, single: false);
            typing = false;
        };
        box.LostFocus += (_, _) => EndChange();
        _refreshers.Add(value =>
        {
            // El refresco de la propia tecla no pisa el campo (el cursor saltaría); deshacer con el foco dentro sí lo actualiza.
            string text = Text(value) ?? "";
            if (!typing && TitleEditor.FromTextBox(box.Text) != text)
            {
                box.Text = text;
            }
        });
        return Row(parameter.Label, box);
    }

    private Grid ColorRow(TemplateParameter parameter)
    {
        var swatch = new Border { Width = 18, Height = 18, CornerRadius = ThemeResources.Corner("ControlCornerRadius"), BorderThickness = new Thickness(1), BorderBrush = ThemeResources.Brush("ControlBorderBrush") };
        var picker = new ColorPicker { IsAlphaEnabled = true, IsMoreButtonVisible = false, IsColorChannelTextInputVisible = false, IsHexInputVisible = false };
        var flyout = new Flyout { Content = picker };
        var button = new Button { Content = swatch, Width = 28, Height = 28, MinHeight = 28, Padding = new Thickness(0), Flyout = flyout };
        AutomationProperties.SetName(button, $"Elegir {parameter.Label}");
        var hex = new TextBox { Height = 28, MinHeight = 28, FontSize = ThemeResources.Size("BodyFontSize"), MinWidth = 0 };
        AutomationProperties.SetName(hex, parameter.Label);
        bool fromCode = false;
        string shownHex = "";
        picker.ColorChanged += (_, e) =>
        {
            if (!fromCode)
            {
                Commit(parameter, JsonValue.Create(StyleColors.Format(e.NewColor.A, e.NewColor.R, e.NewColor.G, e.NewColor.B))!, single: false);
            }
        };
        flyout.Closed += (_, _) => EndChange();
        void CommitHex()
        {
            if (StyleColors.Normalize(hex.Text) is { } value)
            {
                Commit(parameter, JsonValue.Create(value)!, single: true);
            }
            else
            {
                hex.Text = shownHex;
            }
        }

        hex.LostFocus += (_, _) => CommitHex();
        hex.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                CommitHex();
                e.Handled = true;
            }
        };
        _refreshers.Add(value =>
        {
            string text = Text(value) ?? "#FFFFFF";
            if (hex.FocusState == FocusState.Unfocused || text != shownHex)
            {
                hex.Text = text;
            }

            shownHex = text;

            if (StyleColors.TryParse(text, out (byte A, byte R, byte G, byte B) c))
            {
                Color color = Color.FromArgb(c.A, c.R, c.G, c.B);
                swatch.Background = new SolidColorBrush(color);
                fromCode = true;
                picker.Color = color;
                fromCode = false;
            }
        });
        return Row(parameter.Label, hex, button);
    }

    private Grid FontRow(TemplateParameter parameter)
    {
        var box = new ComboBox { IsEditable = true, HorizontalAlignment = HorizontalAlignment.Stretch, Height = 28, MinHeight = 28, FontSize = ThemeResources.Size("BodyFontSize") };
        AutomationProperties.SetName(box, parameter.Label);
        foreach (string family in TitleEditor.CommonFonts)
        {
            box.Items.Add(family);
        }

        JsonObject current = [];
        void Choose(string family)
        {
            family = family.Trim();
            if (family.Length == 0 || family == Text(current["family"]))
            {
                return;
            }

            // El resto del objeto (peso, cursiva, respaldos) se conserva; el archivo del paquete era de la familia anterior.
            var font = (JsonObject)current.DeepClone();
            font["family"] = family;
            font.Remove("source");
            Commit(parameter, font, single: true);
        }

        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is string family)
            {
                Choose(family);
            }
        };
        box.TextSubmitted += (_, e) => Choose(e.Text);
        _refreshers.Add(value =>
        {
            current = value as JsonObject is { } font ? (JsonObject)font.DeepClone() : [];
            string family = Text(current["family"]) ?? "";
            box.SelectedItem = box.Items.Contains(family) ? family : null;
            box.Text = family;
        });
        return Row(parameter.Label, box);
    }

    private Grid NumberRow(TemplateParameter parameter)
    {
        double min = parameter.Min ?? 0;
        double max = parameter.Max ?? 100;
        double step = parameter.Step ?? (max - min <= 10 ? 0.01 : 1);
        bool fractional = step < 1 || step != Math.Floor(step);
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            StepFrequency = step,
            SmallChange = step,
            LargeChange = step * 10,
            IsThumbToolTipEnabled = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(slider, parameter.Label);
        var number = new NumberBox
        {
            Minimum = min,
            Maximum = max,
            SmallChange = step,
            Width = NumberWidth,
            Height = 28,
            MinHeight = 28,
            FontSize = ThemeResources.Size("BodyFontSize"),
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
            NumberFormatter = new Windows.Globalization.NumberFormatting.DecimalFormatter
            {
                FractionDigits = fractional ? 2 : 0,
                IsDecimalPointAlwaysDisplayed = false,
                NumberRounder = new Windows.Globalization.NumberFormatting.IncrementNumberRounder { Increment = fractional ? 0.01 : 1 },
            },
        };
        AutomationProperties.SetName(number, parameter.Label);
        NumberBoxCommit.CommitBeforeFocusLeaves(number);
        bool fromCode = false;
        slider.ValueChanged += (_, e) =>
        {
            if (!fromCode)
            {
                Commit(parameter, Number(e.NewValue, fractional), single: false);
            }
        };
        slider.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => EndChange()), true);
        slider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => EndChange()), true);
        slider.LostFocus += (_, _) => EndChange();
        number.ValueChanged += (_, e) =>
        {
            if (!fromCode && double.IsFinite(e.NewValue))
            {
                Commit(parameter, Number(Math.Clamp(e.NewValue, min, max), fractional), single: true);
            }
        };
        _refreshers.Add(value =>
        {
            double shown = value?.GetValueKind() == JsonValueKind.Number && double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? n : min;
            fromCode = true;
            slider.Value = Math.Clamp(shown, min, max);
            number.Value = shown;
            fromCode = false;
        });
        return Row(parameter.Label, slider, number);
    }

    private CheckBox BoolRow(TemplateParameter parameter)
    {
        var box = new CheckBox { Content = parameter.Label, FontSize = ThemeResources.Size("BodyFontSize"), Margin = new Thickness(12, 2, 6, 2) };
        box.Click += (_, _) => Commit(parameter, JsonValue.Create(box.IsChecked == true), single: true);
        _refreshers.Add(value => box.IsChecked = value?.GetValueKind() == JsonValueKind.True);
        return box;
    }

    private Grid ChoiceRow(TemplateParameter parameter)
    {
        var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Height = 28, MinHeight = 28, FontSize = ThemeResources.Size("BodyFontSize") };
        AutomationProperties.SetName(box, parameter.Label);
        foreach (TemplateOption option in parameter.Options)
        {
            box.Items.Add(option.Label);
        }

        bool fromCode = false;
        box.SelectionChanged += (_, _) =>
        {
            if (!fromCode && box.SelectedIndex >= 0)
            {
                Commit(parameter, parameter.Options[box.SelectedIndex].Value, single: true);
            }
        };
        _refreshers.Add(value =>
        {
            fromCode = true;
            box.SelectedIndex = -1;
            for (int i = 0; i < parameter.Options.Count; i++)
            {
                if (JsonNode.DeepEquals(parameter.Options[i].Value, value))
                {
                    box.SelectedIndex = i;
                    break;
                }
            }

            fromCode = false;
        });
        return Row(parameter.Label, box);
    }

    private static JsonValue Number(double value, bool fractional) =>
        fractional ? JsonValue.Create(Math.Round(value, 4)) : JsonValue.Create((long)Math.Round(value));

    private static string? Text(JsonNode? node) => node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;

    /// <summary>Etiqueta de ancho fijo, el control y, opcional, un tercer elemento a la derecha: como el resto del inspector.</summary>
    private static Grid Row(string label, FrameworkElement control, FrameworkElement? trailing = null)
    {
        var grid = new Grid { Padding = new Thickness(12, 3, 6, 3), ColumnSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = trailing is null ? new GridLength(0) : GridLength.Auto });
        var text = new TextBlock { Text = label, Style = ThemeResources.Style("LabelTextStyle"), VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(text, label);
        grid.Children.Add(text);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        if (trailing is not null)
        {
            Grid.SetColumn(trailing, 2);
            grid.Children.Add(trailing);
        }

        return grid;
    }
}
