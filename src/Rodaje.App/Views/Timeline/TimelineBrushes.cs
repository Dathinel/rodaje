using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Rodaje.Core.Model;
using Windows.UI;

namespace Rodaje.App.Views.Timeline;

/// <summary>Los pinceles del tema que usa la línea de tiempo (Palette.xaml), leídos una vez.</summary>
internal sealed class TimelineBrushes
{
    private readonly Color _frame;
    private readonly Color _frameGap;

    public TimelineBrushes()
    {
        Background = Get("BackgroundBrush");
        Panel = Get("PanelBrush");
        Bar = Get("BarBrush");
        Control = Get("ControlBrush");
        ControlHover = Get("ControlHoverBrush");
        Stroke = Get("StrokeBrush");
        Text = Get("TextBrush");
        TextMuted = Get("TextMutedBrush");
        TextDisabled = Get("TextDisabledBrush");
        Accent = Get("AccentBrush");
        AccentSubtle = Get("AccentSubtleBrush");
        Keyframe = Get("KeyframeBrush");
        VideoClip = Get("VideoClipBrush");
        VideoClipEdge = Get("VideoClipEdgeBrush");
        AudioClip = Get("AudioClipBrush");
        AudioClipEdge = Get("AudioClipEdgeBrush");
        Scrim = Get("ScrimBrush");
        Transparent = Get("TransparentBrush");

        Color edge = Color(VideoClipEdge);
        Color body = Color(VideoClip);
        _frame = WithAlpha(edge, 0x58);
        _frameGap = WithAlpha(body, 0xFF);
        Waveform = new SolidColorBrush(WithAlpha(Color(AudioClipEdge), 0xC0));
        NameBand = new SolidColorBrush(WithAlpha(Color(Background), 0x90));
        Region = new SolidColorBrush(WithAlpha(Color(Accent), 0x14));
        RegionRuler = new SolidColorBrush(WithAlpha(Color(Accent), 0x40));
        MarkerLine = new SolidColorBrush(WithAlpha(Color(Keyframe), 0x60));
        TimeRange = new SolidColorBrush(WithAlpha(Color(Text), 0x14));
        Ghost = new SolidColorBrush(WithAlpha(Color(Accent), 0x50));
        MainLane = new SolidColorBrush(WithAlpha(Color(Accent), 0x1A));
    }

    public Brush Background { get; }

    public Brush Panel { get; }

    public Brush Bar { get; }

    public Brush Control { get; }

    public Brush ControlHover { get; }

    public Brush Stroke { get; }

    public Brush Text { get; }

    public Brush TextMuted { get; }

    public Brush TextDisabled { get; }

    public Brush Accent { get; }

    public Brush AccentSubtle { get; }

    public Brush Keyframe { get; }

    public Brush VideoClip { get; }

    public Brush VideoClipEdge { get; }

    public Brush AudioClip { get; }

    public Brush AudioClipEdge { get; }

    public Brush Scrim { get; }

    public Brush Transparent { get; }

    /// <summary>
    /// Cuadros de la tira de película mientras no hay miniaturas: un degradado horizontal que se repite (un cuadro y su
    /// separación); cada clip tiene el suyo para alinearlo con su inicio.
    /// </summary>
    public LinearGradientBrush CreateFilmstripBrush(Transform shift) => new()
    {
        MappingMode = BrushMappingMode.Absolute,
        SpreadMethod = GradientSpreadMethod.Repeat,
        StartPoint = new Windows.Foundation.Point(0, 0),
        EndPoint = new Windows.Foundation.Point(64, 0),
        Transform = shift,
        GradientStops =
        {
            new GradientStop { Color = _frameGap, Offset = 0 },
            new GradientStop { Color = _frame, Offset = 0.03 },
            new GradientStop { Color = _frame, Offset = 0.97 },
            new GradientStop { Color = _frameGap, Offset = 1 },
        },
    };

    public Brush Waveform { get; }

    public Brush NameBand { get; }

    public Brush Region { get; }

    public Brush RegionRuler { get; }

    public Brush MarkerLine { get; }

    public Brush TimeRange { get; }

    public Brush Ghost { get; }

    public Brush MainLane { get; }

    public Brush ClipFill(TrackKind kind) => kind == TrackKind.Audio ? AudioClip : VideoClip;

    public Brush ClipEdge(TrackKind kind) => kind == TrackKind.Audio ? AudioClipEdge : VideoClipEdge;

    private static Brush Get(string key) => (Brush)Application.Current.Resources[key];

    private static Color Color(Brush brush) => brush is SolidColorBrush solid ? solid.Color : Colors.Gray;

    private static Color WithAlpha(Color color, byte alpha) => ColorHelper.FromArgb(alpha, color.R, color.G, color.B);
}
