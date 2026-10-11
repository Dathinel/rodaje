using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Titles;
using Rodaje.Core.Model;
using Windows.UI;

namespace Rodaje.App.Views.Inspector.Text;

/// <summary>Las filas de estilo, armadas en código: etiqueta de ancho fijo, control y, si hace falta, el número.</summary>
internal sealed partial class TextPanel
{
    private const double LabelWidth = 76;
    private const double NumberWidth = 56;

    private void BuildRows()
    {
        _styleBox = Combo("Estilo de la biblioteca");
        _styleBox.SelectionChanged += OnStyleChosen;
        StyleRows.Children.Add(Row("Estilo", _styleBox));
        _sourceText = new TextBlock { Style = Resource<Style>("CaptionTextStyle"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(12, 0, 12, 4) };
        StyleRows.Children.Add(_sourceText);

        StyleRows.Children.Add(FontRow());
        StyleRows.Children.Add(WeightRow());
        StyleRows.Children.Add(SliderRow("Tamaño", 8, 400, 1, "font-size", s => s.Size, (s, v) =>
        {
            s.Size = v;
            if (s.Stroke is { } stroke)
            {
                stroke.Width = Math.Min(stroke.Width, v / 2);
            }
        }));
        StyleRows.Children.Add(ColorRow("Color", "fill", s => s.Fill.Color, (s, c) => s.Fill.Color = c));
        StyleRows.Children.Add(AlignRow());
        StyleRows.Children.Add(SliderRow("Interlineado", 0.5, 3, 0.05, "line-height", s => s.LineHeight, (s, v) => s.LineHeight = v));
        StyleRows.Children.Add(SliderRow("Espaciado", -20, 100, 1, "letter-spacing", s => s.LetterSpacing, (s, v) => s.LetterSpacing = v));

        StrokeRows.Children.Add(Toggle("Con borde", "stroke-on", s => s.Stroke is not null, (s, on) => s.Stroke = on ? new StyleStroke { Color = "#000000", Width = Math.Min(6, s.Size / 2) } : null));
        StrokeRows.Children.Add(ColorRow("Color", "stroke-color", s => s.Stroke?.Color, (s, c) => (s.Stroke ??= new StyleStroke()).Color = c));
        StrokeRows.Children.Add(SliderRow("Grosor", 0, 50, 0.5, "stroke-width", s => s.Stroke?.Width, (s, v) => (s.Stroke ??= new StyleStroke()).Width = Math.Min(v, s.Size / 2)));

        ShadowRows.Children.Add(Toggle("Con sombra", "shadow-on", s => s.Shadow is not null, (s, on) => s.Shadow = on ? new StyleShadow { Color = "#00000099", Dy = 4, Blur = 8 } : null));
        ShadowRows.Children.Add(ColorRow("Color", "shadow-color", s => s.Shadow?.Color, (s, c) => (s.Shadow ??= new StyleShadow()).Color = c));
        ShadowRows.Children.Add(SliderRow("Distancia X", -100, 100, 1, "shadow-dx", s => s.Shadow?.Dx, (s, v) => (s.Shadow ??= new StyleShadow()).Dx = v));
        ShadowRows.Children.Add(SliderRow("Distancia Y", -100, 100, 1, "shadow-dy", s => s.Shadow?.Dy, (s, v) => (s.Shadow ??= new StyleShadow()).Dy = v));
        ShadowRows.Children.Add(SliderRow("Desenfoque", 0, 64, 1, "shadow-blur", s => s.Shadow?.Blur, (s, v) => (s.Shadow ??= new StyleShadow()).Blur = v));

        BackgroundRows.Children.Add(Toggle("Con fondo", "background-on", s => s.Background is not null, (s, on) => s.Background = on ? new StyleBackground { Color = "#00000080", Padding = [24, 12], Radius = 8 } : null));
        BackgroundRows.Children.Add(ColorRow("Color", "background-color", s => s.Background?.Color, (s, c) => (s.Background ??= new StyleBackground()).Color = c));
        BackgroundRows.Children.Add(SliderRow("Margen", 0, 200, 1, "background-padding", s => s.Background?.Padding[0], (s, v) =>
        {
            StyleBackground background = s.Background ??= new StyleBackground();
            background.Padding = [v, Math.Round(v / 2)];
        }));
        BackgroundRows.Children.Add(SliderRow("Esquinas", 0, 100, 1, "background-radius", s => s.Background?.Radius, (s, v) => (s.Background ??= new StyleBackground()).Radius = v));
    }

    private Grid FontRow()
    {
        ComboBox box = Combo("Fuente");
        box.IsEditable = true;
        foreach (string family in TitleEditor.CommonFonts)
        {
            box.Items.Add(family);
        }

        void Commit(string family)
        {
            family = family.Trim();
            if (!_updating && family.Length > 0)
            {
                _editor?.CommitStyle("font-family", s =>
                {
                    s.Font.Family = family;
                    s.Font.Source = null;
                });
            }
        }

        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is string family)
            {
                Commit(family);
            }
        };
        box.TextSubmitted += (_, e) => Commit(e.Text);
        _refreshers.Add(s =>
        {
            box.SelectedItem = box.Items.Contains(s.Font.Family) ? s.Font.Family : null;
            box.Text = s.Font.Family;
        });
        return Row("Fuente", box);
    }

    private Grid WeightRow()
    {
        ComboBox box = Combo("Grosor de la letra");
        foreach ((_, string name) in TitleEditor.Weights)
        {
            box.Items.Add(name);
        }

        box.SelectionChanged += (_, _) =>
        {
            if (!_updating && box.SelectedIndex >= 0)
            {
                int weight = TitleEditor.Weights[box.SelectedIndex].Weight;
                _editor?.CommitStyle("font-weight", s => s.Font.Weight = weight);
            }
        };

        var italic = new ToggleButton
        {
            Content = "I",
            FontStyle = Windows.UI.Text.FontStyle.Italic,
            Width = NumberWidth,
            Height = 28,
            MinHeight = 28,
            Padding = new Thickness(0),
        };
        AutomationProperties.SetName(italic, "Cursiva");
        ToolTipService.SetToolTip(italic, "Cursiva");
        italic.Click += (_, _) =>
        {
            bool on = italic.IsChecked == true;
            if (!_updating)
            {
                _editor?.CommitStyle("font-italic", s => s.Font.Italic = on);
            }
        };
        _refreshers.Add(s =>
        {
            box.SelectedIndex = TitleEditor.Weights.ToList().FindIndex(w => w.Weight == s.Font.Weight);
            box.PlaceholderText = TitleEditor.WeightName(s.Font.Weight);
            italic.IsChecked = s.Font.Italic;
        });
        return Row("Grosor", box, italic);
    }

    private Grid AlignRow()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var buttons = new List<(TextAlign Align, ToggleButton Button)>();
        foreach ((TextAlign align, string glyph, string name) in (ReadOnlySpan<(TextAlign, string, string)>)
                 [(TextAlign.Left, "", "A la izquierda"), (TextAlign.Center, "", "Centrado"), (TextAlign.Right, "", "A la derecha")])
        {
            var button = new ToggleButton
            {
                Content = new FontIcon { Glyph = glyph, FontSize = 13 },
                Width = 36,
                Height = 28,
                MinHeight = 28,
                Padding = new Thickness(0),
            };
            AutomationProperties.SetName(button, name);
            ToolTipService.SetToolTip(button, name);
            button.Click += (_, _) =>
            {
                if (!_updating)
                {
                    _editor?.CommitStyle("align", s => s.Align = align);
                }

                Refresh();
            };
            buttons.Add((align, button));
            panel.Children.Add(button);
        }

        _refreshers.Add(s =>
        {
            foreach ((TextAlign align, ToggleButton button) in buttons)
            {
                button.IsChecked = s.Align == align;
            }
        });
        return Row("Alineación", panel);
    }

    private CheckBox Toggle(string text, string field, Func<TextStyle, bool> get, Action<TextStyle, bool> set)
    {
        var box = new CheckBox { Content = text, FontSize = Resource<double>("BodyFontSize"), Margin = new Thickness(12, 2, 6, 2) };
        box.Click += (_, _) =>
        {
            bool on = box.IsChecked == true;
            if (!_updating)
            {
                _editor?.CommitStyle(field, s => set(s, on));
            }
        };
        _refreshers.Add(s => box.IsChecked = get(s));
        return box;
    }

    /// <summary>Deslizador y número; arrastrar es un paso del historial hasta soltar.</summary>
    private Grid SliderRow(string label, double min, double max, double step, string field, Func<TextStyle, double?> get, Action<TextStyle, double> set)
    {
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
        AutomationProperties.SetName(slider, label);
        var number = new NumberBox
        {
            Minimum = min,
            Maximum = max,
            SmallChange = step,
            Width = NumberWidth,
            Height = 28,
            MinHeight = 28,
            FontSize = Resource<double>("BodyFontSize"),
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
            NumberFormatter = new Windows.Globalization.NumberFormatting.DecimalFormatter
            {
                FractionDigits = step < 1 ? 2 : 0,
                IsDecimalPointAlwaysDisplayed = false,
                NumberRounder = new Windows.Globalization.NumberFormatting.IncrementNumberRounder { Increment = step < 1 ? 0.01 : 1 },
            },
        };
        AutomationProperties.SetName(number, label);

        slider.ValueChanged += (_, e) =>
        {
            if (!_updating)
            {
                double value = e.NewValue;
                _editor?.ChangeStyle(field, s => set(s, value));
            }
        };
        slider.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => _editor?.EndChange()), true);
        slider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => _editor?.EndChange()), true);
        slider.LostFocus += (_, _) => _editor?.EndChange();
        number.ValueChanged += (_, e) =>
        {
            if (!_updating && double.IsFinite(e.NewValue))
            {
                double value = Math.Clamp(e.NewValue, min, max);
                _editor?.CommitStyle(field, s => set(s, value));
            }
        };

        Grid row = Row(label, slider, number);
        _refreshers.Add(s =>
        {
            double? value = get(s);
            row.Opacity = value is null ? 0.5 : 1;
            slider.IsEnabled = value is not null;
            number.IsEnabled = value is not null;
            slider.Value = Math.Clamp(value ?? min, min, max);
            number.Value = value ?? double.NaN;
        });
        return row;
    }

    /// <summary>Muestra del color con su selector (con transparencia) y el código <c>#RRGGBB[AA]</c> escrito.</summary>
    private Grid ColorRow(string label, string field, Func<TextStyle, string?> get, Action<TextStyle, string> set)
    {
        var swatch = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(2), BorderThickness = new Thickness(1), BorderBrush = Resource<Brush>("ControlBorderBrush") };
        var picker = new ColorPicker
        {
            IsAlphaEnabled = true,
            IsMoreButtonVisible = false,
            IsColorChannelTextInputVisible = false,
            IsHexInputVisible = false,
        };
        var flyout = new Flyout { Content = picker };
        var button = new Button { Content = swatch, Width = 28, Height = 28, MinHeight = 28, Padding = new Thickness(0), Flyout = flyout };
        AutomationProperties.SetName(button, $"Elegir {label.ToLower(CultureInfo.CurrentCulture)}");
        var hex = new TextBox { Height = 28, MinHeight = 28, FontSize = Resource<double>("BodyFontSize"), MinWidth = 0 };
        AutomationProperties.SetName(hex, label);

        bool fromCode = false;
        picker.ColorChanged += (_, e) =>
        {
            if (!_updating && !fromCode)
            {
                string value = StyleColors.Format(e.NewColor.A, e.NewColor.R, e.NewColor.G, e.NewColor.B);
                _editor?.ChangeStyle(field, s => set(s, value));
            }
        };
        flyout.Closed += (_, _) => _editor?.EndChange();
        void CommitHex()
        {
            if (!_updating && StyleColors.Normalize(hex.Text) is { } value)
            {
                _editor?.CommitStyle(field, s => set(s, value));
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

        var body = new Grid { ColumnSpacing = 4 };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(button);
        Grid.SetColumn(hex, 1);
        body.Children.Add(hex);

        Grid row = Row(label, body);
        _refreshers.Add(s =>
        {
            string? value = get(s);
            row.Opacity = value is null ? 0.5 : 1;
            button.IsEnabled = value is not null;
            hex.IsEnabled = value is not null;
            if (hex.FocusState == FocusState.Unfocused)
            {
                hex.Text = value ?? "";
            }

            if (StyleColors.TryParse(value, out (byte A, byte R, byte G, byte B) c))
            {
                Color color = Color.FromArgb(c.A, c.R, c.G, c.B);
                swatch.Background = new SolidColorBrush(color);
                fromCode = true;
                picker.Color = color;
                fromCode = false;
            }
        });
        return row;
    }

    private static ComboBox Combo(string name)
    {
        var box = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 0,
            Height = 28,
            MinHeight = 28,
            FontSize = Resource<double>("BodyFontSize"),
        };
        AutomationProperties.SetName(box, name);
        return box;
    }

    /// <summary>Etiqueta de ancho fijo con puntos suspensivos, el control y, opcional, un tercer elemento a la derecha.</summary>
    private static Grid Row(string label, FrameworkElement control, FrameworkElement? trailing = null)
    {
        var grid = new Grid { Padding = new Thickness(12, 3, 6, 3), ColumnSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = trailing is null ? new GridLength(0) : GridLength.Auto });
        var text = new TextBlock { Text = label, Style = Resource<Style>("LabelTextStyle"), VerticalAlignment = VerticalAlignment.Center };
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

    private static T Resource<T>(string key) => (T)Application.Current.Resources[key];
}
