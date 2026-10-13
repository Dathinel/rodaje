using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Services.Titles;
using Rodaje.Core.Model;
using Rodaje.Core.Text;
using Windows.UI;

namespace Rodaje.App.Views.Inspector.Text;

/// <summary>
/// Miniatura animada de un título: unas letras con la fuente y el color del estilo que repiten la entrada (o la salida) en
/// bucle, con las mismas curvas que el motor (<see cref="TextAnimator"/>). El desenfoque se insinúa con la opacidad. La
/// mueve <see cref="Advance"/>, que llama un único reloj del panel mientras se ve.
/// </summary>
internal sealed partial class TextAnimationThumb : Grid
{
    private const double Hold = 0.5;
    private readonly Canvas _canvas = new();
    private readonly List<TextBlock> _letters = [];
    private readonly List<CompositeTransform> _transforms = [];
    private TextAnimator? _animator;
    private double[] _delays = [];
    private int[] _groups = [];
    private double _time;
    private double _scale = 0.25;

    public TextAnimationThumb(double width, double height)
    {
        Width = width;
        Height = height;
        CornerRadius = new CornerRadius(4);
        Background = new SolidColorBrush(Color.FromArgb(255, 0x10, 0x10, 0x10));
        Children.Add(_canvas);
        IsHitTestVisible = false;
    }

    /// <summary>Lo que se muestra: <paramref name="text"/> con el aspecto de <paramref name="style"/> haciendo <paramref name="animation"/>.</summary>
    public void Show(string text, TextStyle style, StyleAnimation? animation)
    {
        _canvas.Children.Clear();
        _letters.Clear();
        _transforms.Clear();
        double size = Math.Clamp(Height * 0.42, 10, 40);
        _scale = size / Math.Max(1, style.Size);
        Color fill = StyleColors.TryParse(style.Fill.Gradient?.Stops.FirstOrDefault()?.Color ?? style.Fill.Color, out (byte A, byte R, byte G, byte B) c)
            ? Color.FromArgb(Math.Max(c.A, (byte)210), c.R, c.G, c.B)
            : Color.FromArgb(255, 255, 255, 255);
        // El fondo de la muestra contrasta con el color del estilo: los oscuros se ven sobre claro y los claros sobre oscuro.
        double luminance = ((0.2126 * fill.R) + (0.7152 * fill.G) + (0.0722 * fill.B)) / 255;
        Background = new SolidColorBrush(luminance < 0.5 ? Color.FromArgb(255, 0xE4, 0xE4, 0xE4) : Color.FromArgb(255, 0x10, 0x10, 0x10));
        var family = new FontFamily(style.Font.Family);
        var brush = new SolidColorBrush(fill);
        string shown = TextCasing.Apply(text, style.Typography?.Case ?? TextCase.None);
        double x = 0;
        var widths = new List<double>();
        var words = new List<int>();
        int word = 0;
        foreach (char ch in shown)
        {
            if (char.IsWhiteSpace(ch))
            {
                x += size * 0.3;
                word++;
                continue;
            }

            var letter = new TextBlock
            {
                Text = ch.ToString(),
                FontFamily = family,
                FontSize = size,
                FontWeight = new Windows.UI.Text.FontWeight((ushort)Math.Clamp(style.Font.Weight, 100, 900)),
                FontStyle = style.Font.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
                Foreground = brush,
            };
            letter.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            double w = letter.DesiredSize.Width;
            var transform = new CompositeTransform { CenterX = w / 2, CenterY = letter.DesiredSize.Height / 2 };
            letter.RenderTransform = transform;
            Canvas.SetLeft(letter, x);
            Canvas.SetTop(letter, (Height - letter.DesiredSize.Height) / 2);
            x += w + (style.LetterSpacing * _scale);
            _canvas.Children.Add(letter);
            _letters.Add(letter);
            _transforms.Add(transform);
            widths.Add(w);
            words.Add(word);
        }

        // Centrado en la miniatura.
        double offset = (Width - x) / 2;
        foreach (TextBlock letter in _letters)
        {
            Canvas.SetLeft(letter, Canvas.GetLeft(letter) + offset);
        }

        _animator = TextAnimator.From(animation is { PerUnit: false } block ? AsUnits(block) : animation);
        _groups = _animator?.Unit switch
        {
            TextUnit.Letter => [.. Enumerable.Range(0, _letters.Count)],
            TextUnit.Word => [.. words.Select(w => words.Distinct().ToList().IndexOf(w))],
            _ => new int[_letters.Count],
        };
        int groups = _groups.Length == 0 ? 0 : _groups.Max() + 1;
        _delays = new double[groups];
        if (_animator is not null)
        {
            TextAnimator.Delays(_animator.Order, _delays);
        }

        // Empieza con el texto ya entrado: la muestra se lee aunque el reloj no haya corrido.
        _time = Hold + (_animator?.Duration ?? 0);
        Advance(0);
    }

    /// <summary>Adelanta el bucle: espera, la animación y otra espera.</summary>
    public void Advance(double seconds)
    {
        if (_animator is not { } animator)
        {
            return;
        }

        double cycle = animator.Duration + (2 * Hold);
        _time = (_time + seconds) % cycle;
        double progress = Math.Clamp((_time - Hold) / animator.Duration, 0, 1);
        for (int i = 0; i < _letters.Count; i++)
        {
            UnitPose pose = animator.Evaluate(progress, _delays.Length > 0 ? _delays[_groups[i]] : 0);
            CompositeTransform t = _transforms[i];
            t.TranslateX = pose.X * _scale;
            t.TranslateY = pose.Y * _scale;
            t.ScaleX = pose.Scale;
            t.ScaleY = pose.Scale;
            t.Rotation = pose.Rotation;
            _letters[i].Opacity = pose.Opacity / (1 + (pose.Blur / 8));
        }
    }

    /// <summary>Una animación del bloque se ve como un solo grupo que hace lo mismo.</summary>
    private static StyleAnimation AsUnits(StyleAnimation block)
    {
        StyleAnimation copy = block.Clone();
        copy.Curves[StyleAnimation.Blur] = [new StyleKey(0, 0)];
        return copy;
    }
}
