using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Rodaje.Core.Model;
using Windows.UI;

namespace Rodaje.App.Views.Timeline;

/// <summary>Los pinceles del tema que usa la línea de tiempo (Palette.xaml); los derivados se recalculan al cambiar de tema.</summary>
internal sealed class TimelineBrushes
{
    private Color _frame;
    private Color _frameGap;

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
        TextClip = Get("TextClipBrush");
        TextClipEdge = Get("TextClipEdgeBrush");
        AudioClip = Get("AudioClipBrush");
        AudioClipEdge = Get("AudioClipEdgeBrush");
        Scrim = Get("ScrimBrush");
        Transparent = Get("TransparentBrush");
        Danger = Get("DangerBrush");

        Refresh();
        Theme.ThemeResources.ThemeChanged += (_, _) => Refresh();
    }

    /// <summary>Vuelve a derivar los translúcidos (onda, banda del nombre, región, fantasma…) de los pinceles de la paleta.</summary>
    private void Refresh()
    {
        Color edge = Color(VideoClipEdge);
        Color body = Color(VideoClip);
        _frame = WithAlpha(edge, 0x58);
        _frameGap = WithAlpha(body, 0xFF);
        Waveform.Color = WithAlpha(Color(AudioClipEdge), 0xC0);
        NameBand.Color = WithAlpha(Color(Background), 0x90);
        Region.Color = WithAlpha(Color(Accent), 0x14);
        RegionRuler.Color = WithAlpha(Color(Accent), 0x40);
        MarkerLine.Color = WithAlpha(Color(Keyframe), 0x60);
        TimeRange.Color = WithAlpha(Color(Text), 0x14);
        Ghost.Color = WithAlpha(Color(Accent), 0x50);
        MainLane.Color = WithAlpha(Color(Accent), 0x1A);
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

    public Brush TextClip { get; }

    public Brush TextClipEdge { get; }

    public Brush AudioClip { get; }

    public Brush AudioClipEdge { get; }

    public Brush Scrim { get; }

    public Brush Transparent { get; }

    /// <summary>Rojo de los avisos (desfase de un enlazado).</summary>
    public Brush Danger { get; }

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

    public SolidColorBrush Waveform { get; } = new();

    public SolidColorBrush NameBand { get; } = new();

    public SolidColorBrush Region { get; } = new();

    public SolidColorBrush RegionRuler { get; } = new();

    public SolidColorBrush MarkerLine { get; } = new();

    public SolidColorBrush TimeRange { get; } = new();

    public SolidColorBrush Ghost { get; } = new();

    public SolidColorBrush MainLane { get; } = new();

    public Brush ClipFill(TrackKind kind) => kind == TrackKind.Audio ? AudioClip : VideoClip;

    public Brush ClipEdge(TrackKind kind) => kind == TrackKind.Audio ? AudioClipEdge : VideoClipEdge;

    private readonly Dictionary<string, (Brush Solid, Brush Line, Brush Band, Brush Ruler)> _marked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Los pinceles de un marcador o una región con color propio (<c>#RRGGBB</c>): bandera y nombre, línea, banda de la
    /// región y su franja en la regla. Se crean una vez por color.
    /// </summary>
    public (Brush Solid, Brush Line, Brush Band, Brush Ruler)? Marked(string? color)
    {
        if (color is null || !MarkerStyle.IsColor(color))
        {
            return null;
        }

        if (!_marked.TryGetValue(color, out var brushes))
        {
            Color c = ColorHelper.FromArgb(0xFF, Convert.ToByte(color[1..3], 16), Convert.ToByte(color[3..5], 16), Convert.ToByte(color[5..7], 16));
            brushes = (new SolidColorBrush(c), new SolidColorBrush(WithAlpha(c, 0x60)), new SolidColorBrush(WithAlpha(c, 0x1C)), new SolidColorBrush(WithAlpha(c, 0x60)));
            _marked[color] = brushes;
        }

        return brushes;
    }

    private static Brush Get(string key) => (Brush)Application.Current.Resources[key];

    private static Color Color(Brush brush) => brush is SolidColorBrush solid ? solid.Color : Colors.Gray;

    private static Color WithAlpha(Color color, byte alpha) => ColorHelper.FromArgb(alpha, color.R, color.G, color.B);
}
