using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Rodaje.App.Services.Audio;
using Rodaje.App.Services.Crashes;
using Rodaje.App.Services.Settings;
using Rodaje.App.Shell;
using Rodaje.App.Theme;
using Rodaje.App.Views.Player;
using Rodaje.Audio.Metering;
using Windows.UI;

namespace Rodaje.App.Views.Meters;

/// <summary>Medidores de pico de audio L/R, con los niveles que suenan según el reloj del dispositivo.</summary>
/// <remarks>
/// En cada cuadro de composición (<see cref="CompositionTarget.Rendering"/>) toma los niveles del tramo que acaba de
/// sonar (<see cref="PlayerEngine.TakeAudioLevels"/>), les aplica la balística (<see cref="MeterBallistics"/>) y mueve
/// unos pocos <see cref="SpriteVisual"/>: la tapa que oculta la parte apagada de cada barra, la franja del RMS y la raya
/// del pico retenido. No hay diseño de XAML ni reservas de memoria por cuadro; la lectura numérica cambia a lo sumo diez
/// veces por segundo y sus textos están precalculados. Sin nada que animar se desuscribe del cuadro.
/// </remarks>
internal sealed partial class MetersView : UserControl, IEditorView
{
    private const float BarGap = 2;
    private const float HoldThickness = 2;
    private const double MidFromDb = -18;
    private const double HighFromDb = -6;
    private static readonly TimeSpan ReadoutInterval = TimeSpan.FromMilliseconds(100);

    private static readonly string[] Readouts = BuildReadouts();

    private readonly MeterBallistics[] _ballistics = [new(), new()];
    private readonly Channel[] _channels = new Channel[2];
    private readonly List<TextBlock> _scaleLabels = [];
    private readonly LoudnessIntegrator _integrated = new();
    private PlayerEngine? _engine;
    private EditorServices? _services;
    private (double Momentary, double ShortTerm, double Integrated) _loudnessShown = (double.NaN, double.NaN, double.NaN);
    private SettingsStore? _settings;
    private ContainerVisual? _root;
    private CompositionColorBrush? _lowBrush;
    private CompositionColorBrush? _midBrush;
    private CompositionColorBrush? _highBrush;
    private Brush? _clipOnBrush;
    private Brush? _clipOffBrush;
    private Vector2 _size;
    private bool _animating;
    private long _lastFrame;
    private long _lastReadout;
    private int _readoutIndex = -1;
    private bool _clipShown;

    public MetersView()
    {
        InitializeComponent();
    }

    /// <summary>Caída de las barras y del pico retenido, en dB por segundo.</summary>
    public double DecayDecibelsPerSecond
    {
        get => _ballistics[0].DecayDecibelsPerSecond;
        set
        {
            foreach (MeterBallistics channel in _ballistics)
            {
                channel.DecayDecibelsPerSecond = value;
            }

            UpdateDecayItems();
        }
    }

    public void Attach(EditorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _engine = services.Player;
        _services = services;
        _settings = services.Settings;
        BuildNormalizeMenu();
        services.Session.ProjectEdited += (_, _) => UpdateMasterItem();
        services.Session.ProjectOpened += (_, _) => UpdateMasterItem();
        UpdateMasterItem();
        DecayDecibelsPerSecond = services.Settings.Current.MeterDecayDbPerSecond;
        _clipOnBrush = (Brush)Application.Current.Resources["MeterHighBrush"];
        _clipOffBrush = (Brush)Application.Current.Resources["ControlBrush"];
        CreateVisuals();
        CreateScale();
        BarsHost.SizeChanged += (_, _) => Layout();
        ScaleCanvas.SizeChanged += (_, _) => LayoutScale();
        services.Session.TransportChanged += (_, _) =>
        {
            if (services.Session.IsPlaying)
            {
                StartAnimating();
            }
        };
        Unloaded += (_, _) => StopAnimating();
        Loaded += (_, _) => StartAnimating();
        StartAnimating();
    }

    /// <summary>Apaga el aviso de saturación y vuelve a empezar la lectura del mayor pico.</summary>
    public void ResetPeaks()
    {
        foreach (MeterBallistics channel in _ballistics)
        {
            channel.ResetClip();
        }

        _readoutIndex = -1;
        _integrated.Reset();
        UpdateReadout(force: true);
        UpdateLoudness(null);
        UpdateClip();
    }

    private void CreateVisuals()
    {
        Compositor compositor = ElementCompositionPreview.GetElementVisual(BarsHost).Compositor;
        _root = compositor.CreateContainerVisual();
        _lowBrush = compositor.CreateColorBrush(ThemeColor("MeterLowColor"));
        _midBrush = compositor.CreateColorBrush(ThemeColor("MeterMidColor"));
        _highBrush = compositor.CreateColorBrush(ThemeColor("MeterHighColor"));
        CompositionColorBrush cover = compositor.CreateColorBrush(ThemeColor("ControlColor"));
        CompositionColorBrush separator = compositor.CreateColorBrush(ThemeColor("BarColor"));
        Color text = ThemeColor("TextColor");
        CompositionColorBrush rms = compositor.CreateColorBrush(Color.FromArgb(0x70, text.R, text.G, text.B));

        for (int i = 0; i < _channels.Length; i++)
        {
            var channel = new Channel
            {
                Low = Sprite(compositor, _lowBrush),
                Mid = Sprite(compositor, _midBrush),
                High = Sprite(compositor, _highBrush),
                Cover = Sprite(compositor, cover),
                Rms = Sprite(compositor, rms),
                Hold = Sprite(compositor, _lowBrush),
            };
            channel.Ticks = new SpriteVisual[MeterScale.Ticks.Count];
            for (int t = 0; t < channel.Ticks.Length; t++)
            {
                channel.Ticks[t] = Sprite(compositor, separator);
            }

            _root.Children.InsertAtTop(channel.Low);
            _root.Children.InsertAtTop(channel.Mid);
            _root.Children.InsertAtTop(channel.High);
            _root.Children.InsertAtTop(channel.Cover);
            _root.Children.InsertAtTop(channel.Rms);
            foreach (SpriteVisual tick in channel.Ticks)
            {
                _root.Children.InsertAtTop(tick);
            }

            _root.Children.InsertAtTop(channel.Hold);
            _channels[i] = channel;
        }

        ElementCompositionPreview.SetElementChildVisual(BarsHost, _root);
        Layout();
    }

    private void CreateScale()
    {
        var style = (Style)Application.Current.Resources["CaptionTextStyle"];
        foreach (int tick in MeterScale.Ticks)
        {
            _scaleLabels.Add(ScaleLabel(style, tick == 0 ? "0" : tick.ToString(CultureInfo.InvariantCulture).Replace('-', '−')));
        }

        _scaleLabels.Add(ScaleLabel(style, "−∞"));
    }

    private TextBlock ScaleLabel(Style style, string text)
    {
        var label = new TextBlock { Text = text, Style = style, FontSize = ThemeResources.Size("MicroFontSize"), Width = 20, TextAlignment = TextAlignment.Right };
        ScaleCanvas.Children.Add(label);
        return label;
    }

    /// <summary>Coloca lo que no se mueve: zonas de color y separadores de la escala. Se llama al cambiar de tamaño.</summary>
    private void Layout()
    {
        if (_root is null)
        {
            return;
        }

        _size = new Vector2((float)BarsHost.ActualWidth, (float)BarsHost.ActualHeight);
        float width = MathF.Max(1, (_size.X - BarGap) / 2);
        float height = _size.Y;
        float midTop = Y(MidFromDb);
        float highTop = Y(HighFromDb);
        for (int i = 0; i < _channels.Length; i++)
        {
            Channel channel = _channels[i];
            float x = i * (width + BarGap);
            Place(channel.High, x, 0, width, highTop);
            Place(channel.Mid, x, highTop, width, midTop - highTop);
            Place(channel.Low, x, midTop, width, height - midTop);
            for (int t = 0; t < channel.Ticks.Length; t++)
            {
                Place(channel.Ticks[t], x, MathF.Round(Y(MeterScale.Ticks[t])), width, 1);
            }

            channel.X = x;
            channel.Width = width;
            channel.LastCover = channel.LastRms = channel.LastHold = float.NaN;
        }

        Draw();
    }

    private void LayoutScale()
    {
        if (_scaleLabels.Count == 0)
        {
            return;
        }

        double height = ScaleCanvas.ActualHeight;
        for (int t = 0; t < MeterScale.Ticks.Count; t++)
        {
            Canvas.SetTop(_scaleLabels[t], (height * (1 - MeterScale.Deflection(MeterScale.Ticks[t]))) - 7);
        }

        Canvas.SetTop(_scaleLabels[^1], height - 10);
    }

    private void StartAnimating()
    {
        if (_animating || _engine is null)
        {
            return;
        }

        _animating = true;
        _lastFrame = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopAnimating()
    {
        if (_animating)
        {
            _animating = false;
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    private void OnRendering(object? sender, object e)
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = Stopwatch.GetElapsedTime(_lastFrame, now).TotalSeconds;
        _lastFrame = now;
        AudioLevels levels = _engine!.TakeAudioLevels();
        bool sounded = levels.Frames > 0;
        bool clipped = levels.Clipped || levels.Left.TruePeak >= 1f || levels.Right.TruePeak >= 1f;
        _ballistics[0].Update(sounded ? levels.Left.TruePeakDecibels : double.NegativeInfinity, elapsed, clipped && levels.Left.Peak >= levels.Right.Peak);
        _ballistics[1].Update(sounded ? levels.Right.TruePeakDecibels : double.NegativeInfinity, elapsed, clipped && levels.Right.Peak >= levels.Left.Peak);
        _channels[0].RmsDecibels = levels.Left.RmsDecibels;
        _channels[1].RmsDecibels = levels.Right.RmsDecibels;
        Draw();
        UpdateClip();
        if (Stopwatch.GetElapsedTime(_lastReadout, now) >= ReadoutInterval)
        {
            _lastReadout = now;
            UpdateReadout(force: false);
            UpdateLoudness(_engine.TakeLoudness());
        }

        if (!sounded && _ballistics[0].IsIdle && _ballistics[1].IsIdle && _engine.Snapshot is { IsPlaying: false, ShuttleRate: 0 })
        {
            StopAnimating();
        }
    }

    /// <summary>Mueve la tapa, el RMS y el pico retenido de cada barra; solo toca lo que cambió.</summary>
    private void Draw()
    {
        if (_root is null || _size.Y <= 0)
        {
            return;
        }

        for (int i = 0; i < _channels.Length; i++)
        {
            Channel channel = _channels[i];
            MeterBallistics ballistics = _ballistics[i];
            float coverHeight = MathF.Round(Y(ballistics.Level));
            if (coverHeight != channel.LastCover)
            {
                channel.LastCover = coverHeight;
                Place(channel.Cover, channel.X, 0, channel.Width, coverHeight);
            }

            float rmsTop = MathF.Round(Y(channel.RmsDecibels));
            if (rmsTop != channel.LastRms)
            {
                channel.LastRms = rmsTop;
                float rmsWidth = MathF.Max(2, MathF.Round(channel.Width / 3));
                Place(channel.Rms, channel.X + ((channel.Width - rmsWidth) / 2), rmsTop, rmsWidth, _size.Y - rmsTop);
            }

            bool holdVisible = ballistics.PeakHold > MeterBallistics.FloorDecibels;
            float holdTop = holdVisible ? MathF.Round(MathF.Min(Y(ballistics.PeakHold), _size.Y - HoldThickness)) : -1;
            if (holdTop != channel.LastHold)
            {
                channel.LastHold = holdTop;
                channel.Hold.IsVisible = holdVisible;
                channel.Hold.Brush = ballistics.PeakHold >= HighFromDb ? _highBrush : ballistics.PeakHold >= MidFromDb ? _midBrush : _lowBrush;
                Place(channel.Hold, channel.X, holdTop, channel.Width, HoldThickness);
            }
        }
    }

    private void UpdateClip()
    {
        bool clipped = _ballistics[0].Clipped || _ballistics[1].Clipped;
        if (clipped == _clipShown && ClipLeft.Background is not null)
        {
            return;
        }

        _clipShown = clipped;
        ClipLeft.Background = _ballistics[0].Clipped ? _clipOnBrush : _clipOffBrush;
        ClipRight.Background = _ballistics[1].Clipped ? _clipOnBrush : _clipOffBrush;
    }

    private void UpdateReadout(bool force)
    {
        double peak = Math.Max(_ballistics[0].MaxPeak, _ballistics[1].MaxPeak);
        int index = ReadoutIndex(peak);
        if (index != _readoutIndex || force)
        {
            _readoutIndex = index;
            PeakText.Text = Readouts[index];
        }
    }

    /// <summary>
    /// Sonoridad (EBU R128) a lo sumo diez veces por segundo: la corta (3 s) arriba y la integrada desde el último clic
    /// abajo, que suma un bloque de 400 ms por cada lectura mientras suena.
    /// </summary>
    private void UpdateLoudness((double Momentary, double ShortTerm)? powers)
    {
        if (powers is { } p)
        {
            _integrated.Add(p.Momentary);
        }

        double momentary = powers is { } m ? Loudness.ToLufs(m.Momentary) : double.NegativeInfinity;
        double shortTerm = powers is { } s ? Loudness.ToLufs(s.ShortTerm) : double.NegativeInfinity;
        double integrated = _integrated.IntegratedLufs;
        (double, double, double) shown = (Math.Round(momentary, 1), Math.Round(shortTerm, 1), Math.Round(integrated, 1));
        if (shown.Equals(_loudnessShown))
        {
            return;
        }

        _loudnessShown = shown;
        ShortTermText.Text = Readouts[ReadoutIndex(shortTerm)];
        IntegratedText.Text = "I " + Readouts[ReadoutIndex(integrated)];
        double master = _services?.Session.Sequence.MasterGainDb ?? 0;
        ToolTipService.SetToolTip(LoudnessPanel,
            $"Sonoridad (LUFS, EBU R128)\nMomentánea (400 ms): {Readouts[ReadoutIndex(momentary)]}\nCorta (3 s): {Readouts[ReadoutIndex(shortTerm)]}\n" +
            $"Integrada desde el último clic: {Readouts[ReadoutIndex(integrated)]}\nGanancia maestra: {LoudnessNormalizer.Signed(master)} dB\n" +
            "Clic para volver a empezar.");
    }

    private void BuildNormalizeMenu()
    {
        foreach ((double lufs, string name) in LoudnessNormalizer.Targets)
        {
            var item = new MenuFlyoutItem { Text = name };
            item.Click += (_, _) => UiTask.Run(NormalizeMixAsync(lufs));
            NormalizeMixMenu.Items.Add(item);
        }

        NormalizeMixMenu.Items.Add(new MenuFlyoutSeparator());
        var custom = new MenuFlyoutItem { Text = "Otro objetivo…" };
        custom.Click += (_, _) => UiTask.Run(async () =>
        {
            if (await Inspector.Properties.PropertiesPanel.AskTargetAsync(XamlRoot, "Normalizar la mezcla") is { } lufs)
            {
                await NormalizeMixAsync(lufs);
            }
        });
        NormalizeMixMenu.Items.Add(custom);
    }

    private async Task NormalizeMixAsync(double lufs)
    {
        if (_services is not { } services)
        {
            return;
        }

        NormalizeMixMenu.IsEnabled = false;
        try
        {
            await LoudnessNormalizer.NormalizeMixAsync(services.Session, lufs);
        }
        finally
        {
            NormalizeMixMenu.IsEnabled = true;
        }
    }

    private void UpdateMasterItem()
    {
        double master = _services?.Session.Sequence.MasterGainDb ?? 0;
        ClearMasterItem.IsEnabled = master != 0;
        ClearMasterItem.Text = master == 0 ? "Sin ganancia maestra" : $"Quitar la ganancia maestra ({LoudnessNormalizer.Signed(master)} dB)";
    }

    private void OnClearMasterClick(object sender, RoutedEventArgs e)
    {
        if (_services is { } services && services.Session.Sequence.MasterGainDb != 0)
        {
            services.Session.Execute(Rodaje.Core.Commands.Edits.SetMasterGain(services.Session.SequenceId, 0, "Quitar la ganancia maestra"));
        }
    }

    /// <summary>Altura desde arriba hasta el nivel <paramref name="decibels"/>, en píxeles del área de barras.</summary>
    private float Y(double decibels) => (float)(_size.Y * (1 - MeterScale.Deflection(decibels)));

    private static void Place(SpriteVisual visual, float x, float y, float width, float height)
    {
        visual.Offset = new Vector3(x, y, 0);
        visual.Size = new Vector2(width, MathF.Max(0, height));
    }

    private static SpriteVisual Sprite(Compositor compositor, CompositionBrush brush)
    {
        SpriteVisual sprite = compositor.CreateSpriteVisual();
        sprite.Brush = brush;
        return sprite;
    }

    private static Color ThemeColor(string key) => (Color)Application.Current.Resources[key];

    /// <summary>Índice en <see cref="Readouts"/>: décimas de dB de 0 (+3,0) a 730 (−70,0); 731 es −∞.</summary>
    private static int ReadoutIndex(double decibels) =>
        decibels <= MeterBallistics.FloorDecibels || double.IsNaN(decibels) ? Readouts.Length - 1 : (int)Math.Round((3 - Math.Min(decibels, 3)) * 10);

    private static string[] BuildReadouts()
    {
        // La app corre con globalización invariante: coma decimal y signo menos tipográfico a mano.
        var texts = new string[732];
        for (int i = 0; i < 731; i++)
        {
            double value = Math.Round(3 - (i / 10.0), 1);
            string number = value.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',').Replace('-', '−');
            texts[i] = value > 0 ? "+" + number : number;
        }

        texts[^1] = "−∞";
        return texts;
    }

    private void UpdateDecayItems()
    {
        double decay = DecayDecibelsPerSecond;
        SlowDecayItem.IsChecked = decay < 16;
        NormalDecayItem.IsChecked = decay is >= 16 and < 30;
        FastDecayItem.IsChecked = decay >= 30;
    }

    private void OnDecayClick(object sender, RoutedEventArgs e)
    {
        double decay = ReferenceEquals(sender, SlowDecayItem) ? 12 : ReferenceEquals(sender, FastDecayItem) ? 40 : 20;
        DecayDecibelsPerSecond = decay;
        if (_settings is not null)
        {
            _settings.Current.MeterDecayDbPerSecond = decay;
        }
    }

    private void OnResetClick(object sender, RoutedEventArgs e) => ResetPeaks();

    private void OnResetTapped(object sender, TappedRoutedEventArgs e) => ResetPeaks();

    private sealed class Channel
    {
        public required SpriteVisual Low { get; init; }

        public required SpriteVisual Mid { get; init; }

        public required SpriteVisual High { get; init; }

        public required SpriteVisual Cover { get; init; }

        public required SpriteVisual Rms { get; init; }

        public required SpriteVisual Hold { get; init; }

        public SpriteVisual[] Ticks { get; set; } = [];

        public float X { get; set; }

        public float Width { get; set; }

        public double RmsDecibels { get; set; } = double.NegativeInfinity;

        public float LastCover { get; set; } = float.NaN;

        public float LastRms { get; set; } = float.NaN;

        public float LastHold { get; set; } = float.NaN;
    }

    private void OnMixCompressorClick(object sender, RoutedEventArgs e)
    {
        if (_services is { } services)
        {
            services.Session.ShowDynamics(Services.Audio.DynamicsOwner.Mix(services.Session.Sequence));
        }
    }
}
