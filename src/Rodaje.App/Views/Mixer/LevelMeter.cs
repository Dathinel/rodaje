using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Rodaje.App.Theme;
using Rodaje.Audio.Metering;

namespace Rodaje.App.Views.Mixer;

/// <summary>
/// Medidor L/R de una tira: barra de pico (verde, amarillo y rojo, escala IEC), franja del RMS, pico retenido y aviso de
/// saturación. Son <see cref="SpriteVisual"/> de composición que se mueven sin pasar por el diseño de XAML; quien lo usa
/// llama a <see cref="Update"/> en cada cuadro.
/// </summary>
internal sealed partial class LevelMeter : Grid
{
    private const float Gap = 2;
    private const float HoldThickness = 2;
    private const double MidFromDb = -18;
    private const double HighFromDb = -6;

    private readonly MeterBallistics[] _ballistics = [new(), new()];
    private readonly Bar[] _bars = new Bar[2];
    private readonly Border _clip = new() { Height = 4, Margin = new Thickness(0, 0, 0, 2) };
    private readonly Grid _host = new();
    private ContainerVisual? _root;
    private CompositionColorBrush? _low, _mid, _high;
    private Vector2 _size;
    private bool _clipShown;

    public LevelMeter()
    {
        Width = 14;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _clip.Background = ThemeResources.Brush("ControlBrush");
        _clip.CornerRadius = new CornerRadius(1);
        _clip.Tapped += (_, _) => ResetClip();
        ToolTipService.SetToolTip(_clip, "Saturación: se enciende si pasa de 0 dB. Clic para apagarla.");
        Children.Add(_clip);
        SetRow(_host, 1);
        Children.Add(_host);
        _host.Loaded += (_, _) => Create();
        _host.SizeChanged += (_, _) => Layout();
    }

    /// <summary>El mayor pico desde el último <see cref="ResetClip"/>, en dB.</summary>
    public double MaxPeak => Math.Max(_ballistics[0].MaxPeak, _ballistics[1].MaxPeak);

    public bool IsIdle => _ballistics[0].IsIdle && _ballistics[1].IsIdle;

    /// <summary>Avanza <paramref name="elapsedSeconds"/> con los niveles medidos (nulos en pausa) y redibuja.</summary>
    public void Update(AudioLevels? levels, double elapsedSeconds, bool truePeak = false)
    {
        for (int c = 0; c < 2; c++)
        {
            ChannelLevel channel = levels is { } l ? (c == 0 ? l.Left : l.Right) : default;
            double peak = levels is { Frames: > 0 } ? (truePeak ? channel.TruePeakDecibels : channel.PeakDecibels) : double.NegativeInfinity;
            _ballistics[c].Update(peak, elapsedSeconds, levels is { Frames: > 0 } && (channel.TruePeak >= 1f || (levels.Value.Clipped && truePeak)));
            _bars[c].RmsDb = levels is { Frames: > 0 } ? channel.RmsDecibels : double.NegativeInfinity;
        }

        Draw();
        bool clipped = _ballistics[0].Clipped || _ballistics[1].Clipped;
        if (clipped != _clipShown)
        {
            _clipShown = clipped;
            _clip.Background = ThemeResources.Brush(clipped ? "MeterHighBrush" : "ControlBrush");
        }
    }

    public void ResetClip()
    {
        foreach (MeterBallistics b in _ballistics)
        {
            b.ResetClip();
        }

        _clipShown = false;
        _clip.Background = ThemeResources.Brush("ControlBrush");
    }

    public void Clear()
    {
        foreach (MeterBallistics b in _ballistics)
        {
            b.Clear();
        }

        Draw();
    }

    private void Create()
    {
        if (_root is not null)
        {
            return;
        }

        Compositor compositor = ElementCompositionPreview.GetElementVisual(_host).Compositor;
        _root = compositor.CreateContainerVisual();
        _low = compositor.CreateColorBrush(ThemeResources.Color("MeterLowColor"));
        _mid = compositor.CreateColorBrush(ThemeResources.Color("MeterMidColor"));
        _high = compositor.CreateColorBrush(ThemeResources.Color("MeterHighColor"));
        CompositionColorBrush cover = compositor.CreateColorBrush(ThemeResources.Color("ControlColor"));
        Windows.UI.Color text = ThemeResources.Color("TextColor");
        CompositionColorBrush rms = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0x80, text.R, text.G, text.B));
        for (int c = 0; c < 2; c++)
        {
            var bar = new Bar
            {
                Low = Sprite(compositor, _low),
                Mid = Sprite(compositor, _mid),
                High = Sprite(compositor, _high),
                Cover = Sprite(compositor, cover),
                Rms = Sprite(compositor, rms),
                Hold = Sprite(compositor, _low),
            };
            foreach (SpriteVisual sprite in new[] { bar.Low, bar.Mid, bar.High, bar.Cover, bar.Rms, bar.Hold })
            {
                _root.Children.InsertAtTop(sprite);
            }

            _bars[c] = bar;
        }

        ElementCompositionPreview.SetElementChildVisual(_host, _root);
        Layout();
    }

    private void Layout()
    {
        if (_root is null)
        {
            return;
        }

        _size = new Vector2((float)_host.ActualWidth, (float)_host.ActualHeight);
        float width = MathF.Max(1, (_size.X - Gap) / 2);
        float midTop = Y(MidFromDb), highTop = Y(HighFromDb);
        for (int c = 0; c < 2; c++)
        {
            Bar bar = _bars[c];
            bar.X = c * (width + Gap);
            bar.Width = width;
            Place(bar.High, bar.X, 0, width, highTop);
            Place(bar.Mid, bar.X, highTop, width, midTop - highTop);
            Place(bar.Low, bar.X, midTop, width, _size.Y - midTop);
            bar.LastCover = bar.LastRms = bar.LastHold = float.NaN;
        }

        Draw();
    }

    private void Draw()
    {
        if (_root is null || _size.Y <= 0)
        {
            return;
        }

        for (int c = 0; c < 2; c++)
        {
            Bar bar = _bars[c];
            MeterBallistics b = _ballistics[c];
            float cover = MathF.Round(Y(b.Level));
            if (cover != bar.LastCover)
            {
                bar.LastCover = cover;
                Place(bar.Cover, bar.X, 0, bar.Width, cover);
            }

            float rms = MathF.Round(Y(bar.RmsDb));
            if (rms != bar.LastRms)
            {
                bar.LastRms = rms;
                Place(bar.Rms, bar.X, rms, bar.Width, MathF.Min(2, _size.Y - rms));
            }

            bool visible = b.PeakHold > MeterBallistics.FloorDecibels;
            float hold = visible ? MathF.Round(MathF.Min(Y(b.PeakHold), _size.Y - HoldThickness)) : -1;
            if (hold != bar.LastHold)
            {
                bar.LastHold = hold;
                bar.Hold.IsVisible = visible;
                bar.Hold.Brush = b.PeakHold >= HighFromDb ? _high : b.PeakHold >= MidFromDb ? _mid : _low;
                Place(bar.Hold, bar.X, hold, bar.Width, HoldThickness);
            }
        }
    }

    private float Y(double db) => (float)(_size.Y * (1 - MeterScale.Deflection(db)));

    private static SpriteVisual Sprite(Compositor compositor, CompositionBrush brush)
    {
        SpriteVisual sprite = compositor.CreateSpriteVisual();
        sprite.Brush = brush;
        return sprite;
    }

    private static void Place(SpriteVisual sprite, float x, float y, float width, float height)
    {
        sprite.Offset = new Vector3(x, y, 0);
        sprite.Size = new Vector2(width, MathF.Max(0, height));
    }

    private sealed class Bar
    {
        public SpriteVisual Low { get; init; } = null!;

        public SpriteVisual Mid { get; init; } = null!;

        public SpriteVisual High { get; init; } = null!;

        public SpriteVisual Cover { get; init; } = null!;

        public SpriteVisual Rms { get; init; } = null!;

        public SpriteVisual Hold { get; init; } = null!;

        public float X { get; set; }

        public float Width { get; set; }

        public double RmsDb { get; set; } = double.NegativeInfinity;

        public float LastCover { get; set; } = float.NaN;

        public float LastRms { get; set; } = float.NaN;

        public float LastHold { get; set; } = float.NaN;
    }
}
