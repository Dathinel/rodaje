using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.Effects;
using Rodaje.App.Services.Format;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Packages;
using Rodaje.App.Services.Titles;
using Rodaje.App.Theme;
using Rodaje.Core.Model;
using Rodaje.Core.Text;
using Rodaje.Packages;
using Windows.UI;

namespace Rodaje.App.Views.Inspector.Text;

/// <summary>
/// Lo profesional de la pestaña Texto: formato por tramos, tipografía y caja, degradado, bordes y sombras adicionales,
/// arco, animación por unidades con galería animada, barrido de karaoke y la galería de estilos con su animación.
/// </summary>
internal sealed partial class TextPanel
{
    private const double ThumbWidth = 84;
    private const double ThumbHeight = 44;

    private static readonly (string Tag, string Name, string Help)[] FeatureChoices =
    [
        ("-liga", "Sin ligaduras", "Separa «fi», «fl» y las demás uniones de la fuente"),
        ("tnum", "Números tabulares", "Todos los números del mismo ancho: sirven para contadores y tablas"),
        ("onum", "Números antiguos", "Números con ascendentes y descendentes, para texto corrido"),
        ("smcp", "Versalitas de la fuente", "Las versalitas que trae la fuente (si no trae, no cambia nada)"),
        ("zero", "Cero con barra", "Distingue el cero de la O"),
        ("ss01", "Variante estilística 1", "La primera variante de letras que proponga la fuente"),
    ];

    private readonly List<Action<ClipGenerator>> _generatorRefreshers = [];
    private readonly List<TextAnimationThumb> _thumbs = [];
    private readonly List<(TextStyle Style, LibraryElement Element)> _galleryStyles = [];
    private DispatcherTimer? _thumbClock;
    private long _thumbLast;
    private GridView? _styleGallery;
    private PackageScan? _galleryScan;

    private void BuildProRows()
    {
        BuildFormatBar();
        BuildTypographyRows();
        BuildFillRows();
        StrokeRows.Children.Add(LayerList("Bordes adicionales", "Añadir borde", "extra-strokes", ModelValidator.MaxExtraStrokes,
            s => s.ExtraStrokes?.Count ?? 0,
            s => (s.ExtraStrokes ??= []).Add(new StyleStroke { Color = "#FFFFFF", Width = Math.Min(12, s.Size / 2), Join = StrokeJoin.Round }),
            (s, i) => { s.ExtraStrokes!.RemoveAt(i); s.ExtraStrokes = s.ExtraStrokes.Count > 0 ? s.ExtraStrokes : null; },
            [
                ("Grosor", 0, 100, (s, i) => s.ExtraStrokes![i].Width, (s, i, v) => s.ExtraStrokes![i].Width = Math.Min(v, s.Size / 2)),
            ],
            (s, i) => s.ExtraStrokes![i].Color, (s, i, c) => s.ExtraStrokes![i].Color = c));
        ShadowRows.Children.Add(LayerList("Sombras adicionales", "Añadir sombra", "extra-shadows", ModelValidator.MaxExtraShadows,
            s => s.ExtraShadows?.Count ?? 0,
            s => (s.ExtraShadows ??= []).Add(new StyleShadow { Color = "#FF00FF80", Blur = 24 }),
            (s, i) => { s.ExtraShadows!.RemoveAt(i); s.ExtraShadows = s.ExtraShadows.Count > 0 ? s.ExtraShadows : null; },
            [
                ("X", -100, 100, (s, i) => s.ExtraShadows![i].Dx, (s, i, v) => s.ExtraShadows![i].Dx = v),
                ("Y", -100, 100, (s, i) => s.ExtraShadows![i].Dy, (s, i, v) => s.ExtraShadows![i].Dy = v),
                ("Desenfoque", 0, 64, (s, i) => s.ExtraShadows![i].Blur, (s, i, v) => s.ExtraShadows![i].Blur = v),
            ],
            (s, i) => s.ExtraShadows![i].Color, (s, i, c) => s.ExtraShadows![i].Color = c));
        PathRows.Children.Add(Toggle("Texto en arco", "path-on", s => s.Path is not null,
            (s, on) => s.Path = on ? new StylePath { Kind = TextPathKind.Arc, Bend = 90 } : null));
        PathRows.Children.Add(SliderRow("Curvatura", -360, 360, 1, "path-bend", s => s.Path?.Bend, (s, v) => (s.Path ??= new StylePath()).Bend = v));
        PathRows.Children.Add(Caption("Grados que abarca la línea más larga: positivo sube por el centro, negativo baja. Cada letra gira con el arco."));
        BuildAnimationRows(entry: true);
        BuildAnimationRows(entry: false);
        BuildKaraokeRow();
        BuildStyleGallery();
        StartThumbClock();
    }

    private void RefreshPro(ClipGenerator generator, TextStyle style)
    {
        foreach (Action<ClipGenerator> refresh in _generatorRefreshers)
        {
            refresh(generator);
        }

        TypographySection.Summary = style.Typography is { } t
            ? string.Join(" · ", new[]
            {
                t.Case switch { TextCase.Upper => "Mayúsculas", TextCase.Lower => "Minúsculas", TextCase.SmallCaps => "Versalitas", _ => null },
                t.Kerning ? null : "Sin kerning",
                t.Features.Count > 0 ? string.Join(", ", t.Features) : null,
                t.Fit switch { TextFit.Shrink => "Achica a la caja", TextFit.Fill => "Llena la caja", _ => null },
            }.OfType<string>().DefaultIfEmpty("Como la fuente"))
            : "Como la fuente";
        FillSection.Summary = style.Fill.Gradient is { } g ? (g.Kind == GradientKind.Radial ? "Radial" : string.Create(SpanishNumbers.Format, $"Lineal {g.Angle:0}°")) : "Color liso";
        PathSection.Summary = style.Path is { } path ? string.Create(SpanishNumbers.Format, $"{path.Bend:0}°") : "En línea recta";
        int strokes = style.AllStrokes().Count();
        int shadows = style.AllShadows().Count();
        StrokeSection.Summary = strokes == 0 ? "Sin borde" : strokes == 1 ? style.AllStrokes().First().Color : $"{strokes} bordes";
        ShadowSection.Summary = shadows == 0 ? "Sin sombra" : shadows == 1 ? style.AllShadows().First().Color : $"{shadows} sombras";
        AnimationSection.Summary = Describe(style.In, "Entra") + " · " + Describe(style.Out, "Sale");
        RefreshGallery();
    }

    private static string Describe(StyleAnimation? animation, string verb) =>
        animation is null ? $"{verb} sin animación"
            : $"{verb} {(TextAnimationPresets.Match(animation, verb == "Entra")?.Name ?? "propia").ToLower(CultureInfo.CurrentCulture)}{UnitName(animation.Unit)}";

    private static string UnitName(TextUnit unit) => unit switch
    {
        TextUnit.Line => " por línea",
        TextUnit.Word => " por palabra",
        TextUnit.Letter => " por letra",
        _ => "",
    };

    // ---- Formato por tramos ----

    private void BuildFormatBar()
    {
        FormatBar.Children.Add(FormatButton("B", "Negrita (Ctrl+B en el visor)", FontWeights.Bold, null,
            (start, length) => _editor?.Toggle(start, length, s => s.Weight >= 600, (s, on) => s with { Weight = on ? 700 : null }, "Negrita")));
        FormatBar.Children.Add(FormatButton("I", "Cursiva", FontWeights.Normal, Windows.UI.Text.FontStyle.Italic,
            (start, length) => _editor?.Toggle(start, length, s => s.Italic == true, (s, on) => s with { Italic = on ? true : null }, "Cursiva")));
        FormatBar.Children.Add(FormatButton("U", "Subrayado", FontWeights.Normal, null,
            (start, length) => _editor?.Toggle(start, length, s => s.Underline == true, (s, on) => s with { Underline = on ? true : null }, "Subrayado")));
        FormatBar.Children.Add(FormatButton("A−", "Más chico", FontWeights.Normal, null,
            (start, length) => _editor?.FormatText(start, length, s => s with { Scale = Step(s.Scale, 1 / 1.25) }, "Achicar texto")));
        FormatBar.Children.Add(FormatButton("A+", "Más grande", FontWeights.Normal, null,
            (start, length) => _editor?.FormatText(start, length, s => s with { Scale = Step(s.Scale, 1.25) }, "Agrandar texto")));

        var picker = new ColorPicker { IsAlphaEnabled = true, IsMoreButtonVisible = false, IsColorChannelTextInputVisible = false, IsHexInputVisible = false };
        var flyout = new Flyout { Content = picker };
        var swatch = new Border { Width = 14, Height = 14, CornerRadius = ThemeResources.Corner("ControlCornerRadius"), Background = new SolidColorBrush(Color.FromArgb(255, 255, 212, 0)) };
        var color = new Button { Content = swatch, Width = 32, Height = 28, MinHeight = 28, Padding = new Thickness(0), Flyout = flyout };
        ToolTipService.SetToolTip(color, "Color de lo elegido");
        AutomationProperties.SetName(color, "Color de lo elegido");
        (int Start, int Length) chosen = default;
        flyout.Opening += (_, _) => chosen = Selection();
        flyout.Closed += (_, _) =>
        {
            Color c = picker.Color;
            string value = StyleColors.Format(c.A, c.R, c.G, c.B);
            swatch.Background = new SolidColorBrush(c);
            _editor?.FormatText(chosen.Start, chosen.Length, s => s with { Color = value }, "Color del texto");
        };
        FormatBar.Children.Add(color);
        FormatBar.Children.Add(FormatButton("✕", "Quitar el formato propio de lo elegido", FontWeights.Normal, null,
            (start, length) => _editor?.FormatText(start, length, _ => new TextSpan(), "Quitar formato")));
    }

    private static double? Step(double? scale, double factor)
    {
        double next = Math.Clamp(Math.Round((scale ?? 1) * factor, 3), TextSpans.MinScale, TextSpans.MaxScale);
        return Math.Abs(next - 1) < 0.01 ? null : next;
    }

    private Button FormatButton(string text, string help, Windows.UI.Text.FontWeight weight, Windows.UI.Text.FontStyle? style, Func<int, int, bool?> apply)
    {
        var label = new TextBlock { Text = text, FontWeight = weight, FontSize = Resource<double>("BodyFontSize"), HorizontalAlignment = HorizontalAlignment.Center };
        if (style is { } s)
        {
            label.FontStyle = s;
        }

        if (text == "U")
        {
            label.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
        }

        var button = new Button { Content = label, Width = 32, Height = 28, MinHeight = 28, Padding = new Thickness(0) };
        ToolTipService.SetToolTip(button, help);
        AutomationProperties.SetName(button, help);
        button.Click += (_, _) =>
        {
            (int start, int length) = Selection();
            apply(start, length);
        };
        return button;
    }

    /// <summary>Lo elegido en el campo; sin selección, la palabra donde está el cursor.</summary>
    private (int Start, int Length) Selection()
    {
        string text = ContentBox.Text;
        int start = Math.Clamp(ContentBox.SelectionStart, 0, text.Length);
        int length = Math.Clamp(ContentBox.SelectionLength, 0, text.Length - start);
        if (length > 0)
        {
            return (start, length);
        }

        int from = start;
        while (from > 0 && !char.IsWhiteSpace(text[from - 1]))
        {
            from--;
        }

        int to = start;
        while (to < text.Length && !char.IsWhiteSpace(text[to]))
        {
            to++;
        }

        return (from, to - from);
    }

    // ---- Tipografía y caja ----

    private void BuildTypographyRows()
    {
        TypographyRows.Children.Add(Toggle("Kerning (ajuste entre pares de letras)", "kerning", s => s.Typography?.Kerning ?? true,
            (s, on) => Typography(s, t => t.Kerning = on)));
        TypographyRows.Children.Add(ChoiceRow("Mayúsculas", "case", ["Como se escribe", "MAYÚSCULAS", "minúsculas", "Versalitas"],
            s => (int)(s.Typography?.Case ?? TextCase.None), (s, i) => Typography(s, t => t.Case = (TextCase)i)));
        foreach ((string tag, string name, string help) in FeatureChoices)
        {
            CheckBox box = Toggle(name, "feature-" + tag, s => s.Typography?.Features.Contains(tag) == true,
                (s, on) => Typography(s, t =>
                {
                    t.Features.Remove(tag);
                    if (on)
                    {
                        t.Features.Add(tag);
                    }
                }));
            ToolTipService.SetToolTip(box, help);
            TypographyRows.Children.Add(box);
        }

        TypographyRows.Children.Add(ChoiceRow("Ajuste", "fit", ["Sin ajuste", "Achicar si no cabe", "Llenar la caja"],
            s => (int)(s.Typography?.Fit ?? TextFit.None), (s, i) => Typography(s, t => t.Fit = (TextFit)i)));
        TypographyRows.Children.Add(BoxRow("Ancho de caja", 10, 100, g => g.Box?.WidthFraction, (b, v) => b.WidthFraction = v));
        TypographyRows.Children.Add(BoxRow("Alto de caja", 5, 100, g => g.Box?.HeightFraction, (b, v) => b.HeightFraction = v));
        TypographyRows.Children.Add(Caption("Con caja, el texto parte sus líneas a ese ancho; el ajuste cambia el tamaño para que quepa en el ancho y el alto."));
    }

    /// <summary>Cambia la tipografía; si queda como la de la fuente, el estilo vuelve a no llevarla.</summary>
    private static void Typography(TextStyle style, Action<StyleTypography> change)
    {
        StyleTypography t = style.Typography ?? new StyleTypography();
        change(t);
        style.Typography = t is { Kerning: true, Case: TextCase.None, Fit: TextFit.None, Features.Count: 0 } ? null : t;
    }

    /// <summary>Un deslizador del ancho o el alto de la caja en porcentaje del cuadro, con una casilla para quitarlo.</summary>
    private Grid BoxRow(string label, double min, double max, Func<ClipGenerator, double?> get, Action<TextFrame, double?> set)
    {
        var on = new CheckBox { MinWidth = 0, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(on, $"Usar {label.ToLower(CultureInfo.CurrentCulture)}");
        var slider = new Slider { Minimum = min, Maximum = max, StepFrequency = 1, IsThumbToolTipEnabled = false, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(slider, label);
        var value = new TextBlock { Width = NumberWidth, Style = Resource<Style>("LabelTextStyle"), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        void Commit(double? fraction)
        {
            if (_updating || _editor is null)
            {
                return;
            }

            _editor.SetBox(current =>
            {
                TextFrame box = current?.Clone() ?? new TextFrame();
                set(box, fraction);
                return box.Width is null && box.WidthFraction is null && box.HeightFraction is null ? null : box;
            });
        }

        on.Click += (_, _) => Commit(on.IsChecked == true ? slider.Value / 100 : null);
        slider.AddHandler(PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => Commit(slider.Value / 100)), true);
        slider.ValueChanged += (_, e) => value.Text = string.Create(SpanishNumbers.Format, $"{e.NewValue:0} %");
        var body = new Grid { ColumnSpacing = 4 };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(on);
        Grid.SetColumn(slider, 1);
        body.Children.Add(slider);
        Grid row = Row(label, body, value);
        _generatorRefreshers.Add(g =>
        {
            double? fraction = get(g);
            on.IsChecked = fraction is not null;
            slider.IsEnabled = fraction is not null;
            slider.Value = Math.Clamp((fraction ?? (max / 100)) * 100, min, max);
            value.Text = fraction is null ? "—" : string.Create(SpanishNumbers.Format, $"{fraction * 100:0} %");
        });
        return row;
    }

    // ---- Degradado ----

    private void BuildFillRows()
    {
        FillRows.Children.Add(Toggle("Relleno con degradado", "gradient-on", s => s.Fill.Gradient is not null,
            (s, on) => s.Fill.Gradient = on
                ? new StyleGradient { Kind = GradientKind.Linear, Angle = 90, Stops = [new ColorStop(0, s.Fill.Color), new ColorStop(1, "#FF6A00")] }
                : null));
        FillRows.Children.Add(ChoiceRow("Tipo", "gradient-kind", ["Lineal", "Radial"], s => (int)(s.Fill.Gradient?.Kind ?? GradientKind.Linear),
            (s, i) => { if (s.Fill.Gradient is { } g) { g.Kind = (GradientKind)i; } }));
        FillRows.Children.Add(SliderRow("Ángulo", 0, 360, 1, "gradient-angle", s => s.Fill.Gradient?.Angle,
            (s, v) => { if (s.Fill.Gradient is { } g) { g.Angle = v; } }));
        FillRows.Children.Add(ColorRow("Desde", "gradient-from", s => s.Fill.Gradient?.Stops.FirstOrDefault()?.Color,
            (s, c) => { if (s.Fill.Gradient is { Stops.Count: > 0 } g) { g.Stops[0] = g.Stops[0] with { Color = c }; } }));
        FillRows.Children.Add(ColorRow("Hasta", "gradient-to", s => s.Fill.Gradient?.Stops.LastOrDefault()?.Color,
            (s, c) => { if (s.Fill.Gradient is { Stops.Count: > 0 } g) { g.Stops[^1] = g.Stops[^1] with { Color = c }; } }));
        FillRows.Children.Add(Caption("Cubre el bloque entero: 90° va de arriba abajo. Un color propio en una palabra manda sobre el degradado."));
    }

    // ---- Bordes y sombras adicionales ----

    /// <summary>
    /// Una lista de capas (bordes o sombras adicionales) con su color, sus números y quitar, más «añadir». Se rehace solo
    /// si cambia la cantidad, para no quitarle el foco a un número a medio escribir.
    /// </summary>
    private StackPanel LayerList(string title, string add, string field, int max, Func<TextStyle, int> count, Action<TextStyle> create,
        Action<TextStyle, int> remove, (string Name, double Min, double Max, Func<TextStyle, int, double> Get, Action<TextStyle, int, double> Set)[] numbers,
        Func<TextStyle, int, string> getColor, Action<TextStyle, int, string> setColor)
    {
        var panel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 6, 0, 0) };
        var header = new TextBlock { Text = title, Style = Resource<Style>("LabelTextStyle"), Margin = new Thickness(12, 2, 6, 2) };
        var list = new StackPanel { Spacing = 2 };
        var addButton = new Button { Content = add, FontSize = Resource<double>("BodyFontSize"), Margin = new Thickness(12, 2, 6, 4) };
        addButton.Click += (_, _) => _editor?.CommitStyle(field, create);
        panel.Children.Add(header);
        panel.Children.Add(list);
        panel.Children.Add(addButton);
        int shown = -1;
        var updates = new List<Action<TextStyle>>();
        _refreshers.Add(style =>
        {
            int n = count(style);
            addButton.IsEnabled = n < max;
            header.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (n != shown)
            {
                shown = n;
                list.Children.Clear();
                updates.Clear();
                for (int i = 0; i < n; i++)
                {
                    list.Children.Add(LayerRow(i, field, remove, numbers, getColor, setColor, updates));
                }
            }

            foreach (Action<TextStyle> update in updates)
            {
                update(style);
            }
        });
        return panel;
    }

    private Grid LayerRow(int index, string field, Action<TextStyle, int> remove,
        (string Name, double Min, double Max, Func<TextStyle, int, double> Get, Action<TextStyle, int, double> Set)[] numbers,
        Func<TextStyle, int, string> getColor, Action<TextStyle, int, string> setColor, List<Action<TextStyle>> updates)
    {
        var row = new Grid { Padding = new Thickness(12, 2, 6, 2), ColumnSpacing = 4 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var picker = new ColorPicker { IsAlphaEnabled = true, IsMoreButtonVisible = false, IsColorChannelTextInputVisible = false, IsHexInputVisible = false };
        var flyout = new Flyout { Content = picker };
        var swatch = new Border { Width = 18, Height = 18, CornerRadius = ThemeResources.Corner("ControlCornerRadius"), BorderThickness = new Thickness(1), BorderBrush = Resource<Brush>("ControlBorderBrush") };
        var color = new Button { Content = swatch, Width = 28, Height = 28, MinHeight = 28, Padding = new Thickness(0), Flyout = flyout };
        AutomationProperties.SetName(color, "Color");
        bool fromCode = false;
        picker.ColorChanged += (_, e) =>
        {
            if (!_updating && !fromCode)
            {
                string value = StyleColors.Format(e.NewColor.A, e.NewColor.R, e.NewColor.G, e.NewColor.B);
                _editor?.ChangeStyle(field + "-color", s => setColor(s, index, value));
            }
        };
        flyout.Closed += (_, _) => _editor?.EndChange();
        row.Children.Add(color);
        int column = 1;
        foreach ((string name, double min, double max, Func<TextStyle, int, double> get, Action<TextStyle, int, double> set) in numbers)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var number = new NumberBox
            {
                Header = null,
                PlaceholderText = name,
                Minimum = min,
                Maximum = max,
                Height = 28,
                MinHeight = 28,
                MinWidth = 0,
                FontSize = Resource<double>("BodyFontSize"),
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            };
            AutomationProperties.SetName(number, name);
            ToolTipService.SetToolTip(number, name);
            number.ValueChanged += (_, e) =>
            {
                if (!_updating && double.IsFinite(e.NewValue))
                {
                    double v = Math.Clamp(e.NewValue, min, max);
                    _editor?.CommitStyle(field + "-" + name, s => set(s, index, v));
                }
            };
            Grid.SetColumn(number, column++);
            row.Children.Add(number);
            updates.Add(s =>
            {
                if (number.FocusState == FocusState.Unfocused && index < numbers.Length + 100)
                {
                    try
                    {
                        number.Value = get(s, index);
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                    }
                }
            });
        }

        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var delete = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = ThemeResources.Size("IconFontSize") },
            Width = 28,
            Height = 28,
            MinHeight = 28,
            Padding = new Thickness(0),
        };
        ToolTipService.SetToolTip(delete, "Quitar");
        AutomationProperties.SetName(delete, "Quitar");
        delete.Click += (_, _) => _editor?.CommitStyle(field + "-remove", s => remove(s, index));
        Grid.SetColumn(delete, column);
        row.Children.Add(delete);
        updates.Add(s =>
        {
            try
            {
                if (StyleColors.TryParse(getColor(s, index), out (byte A, byte R, byte G, byte B) c))
                {
                    Color value = Color.FromArgb(c.A, c.R, c.G, c.B);
                    swatch.Background = new SolidColorBrush(value);
                    fromCode = true;
                    picker.Color = value;
                    fromCode = false;
                }
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        });
        return row;
    }

    // ---- Animación ----

    private void BuildAnimationRows(bool entry)
    {
        string field = entry ? "in" : "out";
        Func<TextStyle, StyleAnimation?> get = s => entry ? s.In : s.Out;
        void Set(TextStyle s, StyleAnimation? a)
        {
            if (entry)
            {
                s.In = a;
            }
            else
            {
                s.Out = a;
            }
        }

        AnimationRows.Children.Add(new TextBlock { Text = entry ? "Entrada" : "Salida", Style = Resource<Style>("LabelTextStyle"), Margin = new Thickness(12, 4, 6, 0) });
        var gallery = new GridView
        {
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true,
            Padding = new Thickness(8, 0, 4, 0),
            MaxHeight = 220,
        };
        AutomationProperties.SetName(gallery, entry ? "Animaciones de entrada" : "Animaciones de salida");
        var tiles = new List<(TextAnimationPreset? Preset, FrameworkElement Tile, TextAnimationThumb Thumb)>();
        foreach (TextAnimationPreset? preset in new TextAnimationPreset?[] { null }.Concat(TextAnimationPresets.All))
        {
            var thumb = new TextAnimationThumb(ThumbWidth, ThumbHeight);
            _thumbs.Add(thumb);
            var tile = new StackPanel { Width = ThumbWidth, Spacing = 2, Tag = preset };
            tile.Children.Add(thumb);
            tile.Children.Add(new TextBlock
            {
                Text = preset?.Name ?? "Ninguna",
                Style = Resource<Style>("CaptionTextStyle"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            ToolTipService.SetToolTip(tile, preset?.Description ?? (entry ? "Aparece de golpe" : "Se va de golpe"));
            gallery.Items.Add(tile);
            tiles.Add((preset, tile, thumb));
        }

        gallery.ItemClick += (_, e) =>
        {
            if (_updating || e.ClickedItem is not FrameworkElement { Tag: var tag } || _editor is not { } editor)
            {
                return;
            }

            TextAnimationPreset? preset = tag as TextAnimationPreset;
            editor.ChooseAnimation(field, s =>
            {
                StyleAnimation? current = get(s);
                Set(s, preset is null ? null
                    : entry ? preset.In(seconds: current?.DurationSeconds, order: current?.Order ?? UnitOrder.Forward)
                    : preset.Out(seconds: current?.DurationSeconds, order: current?.Order ?? UnitOrder.Forward));
            }, entry, animated: preset is not null);
        };
        AnimationRows.Children.Add(gallery);
        AnimationRows.Children.Add(ChoiceRow("Por", field + "-unit", ["Bloque", "Línea", "Palabra", "Letra"], s => (int)(get(s)?.Unit ?? TextUnit.Block),
            (s, i) => { if (get(s) is { } a) { a.Unit = (TextUnit)i; } }));
        AnimationRows.Children.Add(SliderRow("Desfase %", 0, 100, 1, field + "-stagger", s => get(s) is { } a ? a.Stagger * 100 : null,
            (s, v) => { if (get(s) is { } a) { a.Stagger = v / 100; } }));
        AnimationRows.Children.Add(ChoiceRow("Orden", field + "-order", ["De la primera a la última", "De la última a la primera", "Desde el centro", "Desde los extremos", "Mezclado"],
            s => (int)(get(s)?.Order ?? UnitOrder.Forward), (s, i) => { if (get(s) is { } a) { a.Order = (UnitOrder)i; } }));
        AnimationRows.Children.Add(SliderRow("Duración s", 0.1, 5, 0.05, field + "-duration", s => get(s)?.DurationSeconds,
            (s, v) => { if (get(s) is { } a) { Retime(a, v); } }));
        _refreshers.Add(style =>
        {
            StyleAnimation? animation = get(style);
            TextAnimationPreset? match = TextAnimationPresets.Match(animation, entry);
            int index = animation is null ? 0 : match is null ? -1 : tiles.FindIndex(t => t.Preset == match);
            gallery.SelectedIndex = index;
            string sample = TitleSample();
            foreach ((TextAnimationPreset? preset, _, TextAnimationThumb thumb) in tiles)
            {
                StyleAnimation? shown = preset is null ? null
                    : entry ? preset.In(animation?.Unit, animation?.Stagger, animation?.DurationSeconds, animation?.Order ?? UnitOrder.Forward)
                    : preset.Out(animation?.Unit, animation?.Stagger, animation?.DurationSeconds, animation?.Order ?? UnitOrder.Forward);
                thumb.Show(sample, style, shown ?? (preset is null ? null : preset.In()));
            }
        });
    }

    /// <summary>Estira las curvas a una duración nueva, conservando su forma.</summary>
    private static void Retime(StyleAnimation animation, double seconds)
    {
        double factor = animation.DurationSeconds > 0 ? seconds / animation.DurationSeconds : 1;
        animation.DurationSeconds = seconds;
        foreach (string name in animation.Curves.Keys.ToList())
        {
            animation.Curves[name] = [.. animation.Curves[name].Select(k => k with { T = Math.Min(seconds, k.T * factor) })];
        }
    }

    /// <summary>Unas letras del título para las miniaturas (la primera palabra, o «Título»).</summary>
    private string TitleSample()
    {
        string text = _editor?.Target()?.Generator?.Text ?? "";
        string first = text.Split((char[])[' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return first.Length is >= 2 and <= 7 ? first : "Título";
    }

    private void BuildKaraokeRow()
    {
        HighlightRows.Children.Add(ChoiceRow("Modo", "highlight-mode", ["Solo la palabra que suena", "Barrido de color"],
            s => (int)(s.Highlight?.Mode ?? HighlightMode.Word), (s, i) => (s.Highlight ??= new StyleHighlight()).Mode = (HighlightMode)i));
    }

    // ---- Galería de estilos ----

    private void BuildStyleGallery()
    {
        _styleGallery = new GridView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true, Padding = new Thickness(8, 0, 4, 4), MaxHeight = 240 };
        AutomationProperties.SetName(_styleGallery, "Galería de estilos");
        _styleGallery.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is FrameworkElement { Tag: TextStyle style } && _editor is { } editor)
            {
                editor.ApplyStyle(style);
            }
        };
        StyleRows.Children.Insert(Math.Min(3, StyleRows.Children.Count), _styleGallery);
    }

    /// <summary>Lee los estilos instalados (son JSON chicos) y arma una miniatura animada por cada uno, solo si cambió lo instalado.</summary>
    private void RefreshGallery() => UiTask.Run(RefreshGalleryAsync());

    private async Task RefreshGalleryAsync()
    {
        if (_styleGallery is null || _services is not { } services || ReferenceEquals(services.Packages.Installed, _galleryScan))
        {
            return;
        }

        PackageScan scan = services.Packages.Installed;
        _galleryScan = scan;
        var loader = new InstalledContentLoader(() => scan);
        List<(TextStyle, LibraryElement)> loaded = await Task.Run(() =>
        {
            var result = new List<(TextStyle, LibraryElement)>();
            foreach (LibraryElement element in LibraryElements.For(LibraryCategory.Text, scan))
            {
                try
                {
                    TextStyle? style = element.IsBuiltIn ? TitleStyles.Default()
                        : loader.LoadTextStyle(element.Reference) is { } preset ? TitleStyles.FromPreset(preset, element.Reference) : null;
                    if (style is not null)
                    {
                        result.Add((style, element));
                    }
                }
                catch (Exception ex) when (ex is Rodaje.Packages.Content.PackageContentException or IOException or UnauthorizedAccessException)
                {
                    Debug.WriteLine($"Estilo sin miniatura: {element.Reference}: {ex.Message}");
                }
            }

            return result;
        });
        if (!ReferenceEquals(scan, _galleryScan))
        {
            return;
        }

        _galleryStyles.Clear();
        _galleryStyles.AddRange(loaded);
        foreach (object item in _styleGallery.Items)
        {
            if (item is FrameworkElement { Tag: TextStyle } tile && tile is StackPanel { Children: [TextAnimationThumb thumb, ..] })
            {
                _thumbs.Remove(thumb);
            }
        }

        _styleGallery.Items.Clear();
        foreach ((TextStyle style, LibraryElement element) in loaded)
        {
            var thumb = new TextAnimationThumb(ThumbWidth, ThumbHeight);
            thumb.Show(style.Name is { Length: > 0 and <= 7 } name ? name : "Título", style, style.In);
            _thumbs.Add(thumb);
            var tile = new StackPanel { Width = ThumbWidth, Spacing = 2, Tag = style };
            tile.Children.Add(thumb);
            tile.Children.Add(new TextBlock
            {
                Text = element.Name,
                Style = Resource<Style>("CaptionTextStyle"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            ToolTipService.SetToolTip(tile, element.IsBuiltIn ? element.Name : $"{element.Name} · {element.Source}");
            _styleGallery.Items.Add(tile);
        }
    }

    // ---- Reloj de las miniaturas ----

    private void StartThumbClock()
    {
        _thumbClock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _thumbClock.Tick += (_, _) =>
        {
            long now = Stopwatch.GetTimestamp();
            double seconds = _thumbLast == 0 ? 0 : Math.Min(0.1, Stopwatch.GetElapsedTime(_thumbLast, now).TotalSeconds);
            _thumbLast = now;
            if (Body.Visibility != Visibility.Visible || XamlRoot is null)
            {
                return;
            }

            try
            {
                foreach (TextAnimationThumb thumb in _thumbs)
                {
                    if (thumb.ActualWidth > 0 && thumb.XamlRoot is not null)
                    {
                        thumb.Advance(seconds);
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // Una miniatura que se desarma a mitad de un tic (al cerrar la ventana) no debe tumbar la app.
                _thumbClock?.Stop();
                Debug.WriteLine($"Miniaturas detenidas: {ex.Message}");
            }
        };
        Loaded += (_, _) => _thumbClock.Start();
        Unloaded += (_, _) => _thumbClock.Stop();
        if (IsLoaded)
        {
            _thumbClock.Start();
        }
    }

    // ---- Filas sueltas ----

    private Grid ChoiceRow(string label, string field, string[] choices, Func<TextStyle, int> get, Action<TextStyle, int> set)
    {
        ComboBox box = Combo(label);
        foreach (string choice in choices)
        {
            box.Items.Add(choice);
        }

        box.SelectionChanged += (_, _) =>
        {
            if (!_updating && box.SelectedIndex >= 0)
            {
                int index = box.SelectedIndex;
                _editor?.CommitStyle(field, s => set(s, index));
            }
        };
        _refreshers.Add(s => box.SelectedIndex = Math.Clamp(get(s), -1, choices.Length - 1));
        return Row(label, box);
    }

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        Style = Resource<Style>("CaptionTextStyle"),
        TextWrapping = TextWrapping.Wrap,
        TextTrimming = TextTrimming.None,
        Margin = new Thickness(12, 2, 12, 6),
    };
}
