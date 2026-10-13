using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Rodaje.App.Services.Inspector;
using Windows.UI;

namespace Rodaje.App.Views.Inspector.Transition;

/// <summary>
/// Miniatura animada de una transición: los cuadros que compuso la GPU (<see cref="TransitionPreviews"/>) en bucle, con
/// una pausa en cada clip. Quieta muestra la mitad de la transición. La mueve <see cref="Advance"/>, que llama un único reloj
/// de quien la contiene.
/// </summary>
internal sealed partial class TransitionThumb : Grid
{
    private const double HoldStart = 0.35;
    private const double HoldEnd = 0.55;
    private readonly Image _image = new() { Stretch = Stretch.UniformToFill };
    private TransitionAnimation? _animation;
    private WriteableBitmap? _bitmap;
    private int _shown = -1;
    private double _time;

    public TransitionThumb()
    {
        CornerRadius = new CornerRadius(3);
        Background = new SolidColorBrush(Color.FromArgb(255, 0x14, 0x14, 0x14));
        Children.Add(_image);
        IsHitTestVisible = false;
    }

    /// <summary>Avance de la transición en el cuadro que se ve (de 0 a 1, sin curva), para la gráfica de la curva.</summary>
    public event EventHandler<double>? ProgressChanged;

    public bool HasAnimation => _animation is not null;

    public void Show(TransitionAnimation? animation)
    {
        if (ReferenceEquals(animation, _animation))
        {
            return;
        }

        _animation = animation;
        _shown = -1;
        if (animation is null)
        {
            _image.Source = null;
            _bitmap = null;
            return;
        }

        if (_bitmap is null || _bitmap.PixelWidth != animation.Width || _bitmap.PixelHeight != animation.Height)
        {
            _bitmap = new WriteableBitmap(animation.Width, animation.Height);
            _image.Source = _bitmap;
        }

        ShowFrame(animation.Bgra.Count / 2);
    }

    /// <summary>Vuelve al principio del bucle (al empezar a reproducir).</summary>
    public void Restart() => _time = 0;

    /// <summary>Quieta en la mitad.</summary>
    public void Rest()
    {
        if (_animation is { } animation)
        {
            ShowFrame(animation.Bgra.Count / 2);
        }
    }

    /// <summary>Adelanta el bucle: espera en el clip que sale, la transición y otra espera en el que entra.</summary>
    public void Advance(double seconds)
    {
        if (_animation is not { } animation)
        {
            return;
        }

        double cycle = HoldStart + animation.Seconds + HoldEnd;
        _time = (_time + seconds) % cycle;
        double linear = Math.Clamp((_time - HoldStart) / animation.Seconds, 0, 1);
        ShowFrame((int)Math.Round(linear * (animation.Bgra.Count - 1)));
        ProgressChanged?.Invoke(this, linear);
    }

    private void ShowFrame(int index)
    {
        if (_animation is not { } animation || _bitmap is null || index == _shown || index < 0 || index >= animation.Bgra.Count)
        {
            return;
        }

        _shown = index;
        animation.Bgra[index].CopyTo(_bitmap.PixelBuffer);
        _bitmap.Invalidate();
    }
}
