using Rodaje.Core.Editing;

namespace Rodaje.App.Services;

/// <summary>Herramienta activa de la línea de tiempo.</summary>
public enum TimelineTool
{
    Normal,
    Envelope,
    Selection,
    Zoom,

    /// <summary>Arrastrar un corte lo mueve sin cambiar la duración total (roll).</summary>
    Roll,

    /// <summary>Arrastrar un clip lo corre entre sus vecinos, que se recortan (slide).</summary>
    Slide,

    /// <summary>Cuchilla: un clic corta el clip (y sus enlazados) en ese punto; con Mayús, todas las pistas.</summary>
    Blade,

    /// <summary>Ripple: arrastrar un borde recorta y corre lo que sigue, con el ripple automático apagado o no.</summary>
    Ripple,

    /// <summary>Slip: arrastrar un clip muestra otra parte de su material sin moverlo.</summary>
    Slip,

    /// <summary>Rango: arrastrar marca un tramo de la secuencia (entrada y salida) sin tocar los clips.</summary>
    Range,
}

/// <summary>Qué se corre con el ripple automático.</summary>
public enum RippleMode
{
    /// <summary>Solo las pistas de lo que se edita.</summary>
    AffectedTracks,

    /// <summary>Todas las pistas.</summary>
    AllTracks,

    /// <summary>Todas las pistas, y también los marcadores y las regiones.</summary>
    AllTracksAndMarkers,
}

/// <summary>Qué hace la edición cuando quitar o mover clips abre un hueco.</summary>
public enum TimelineGapBehavior
{
    LeaveGaps,
    MagneticMainTrack,
    GlobalRipple,
}

/// <summary>Opciones de la línea de tiempo que no son parte del proyecto: herramienta, interruptores y zoom.</summary>
public sealed partial class TimelineState : ObservableObject
{
    // Así «Ajustar a la secuencia» deja ver entera una de varias horas (con 2 px/s no cabían ni 10 min).
    public const double MinPixelsPerSecond = 0.05;
    public const double MaxPixelsPerSecond = 2400;
    public const double DefaultPixelsPerSecond = 60;
    public const double ZoomStep = 1.25;

    private TimelineTool _tool;
    private bool _magneticMainTrack = true;
    private bool _snapping = true;
    private bool _autoRipple;
    private RippleMode _rippleMode = RippleMode.AffectedTracks;
    private bool _autoCrossfade;
    private bool _lockEnvelopes;
    private bool _ignoreGroups;
    private double _pixelsPerSecond = DefaultPixelsPerSecond;
    private double _scrollX;

    /// <summary>Origen horizontal compartido por la línea de tiempo y la gráfica del inspector.</summary>
    public double ScrollX
    {
        get => _scrollX;
        set => SetProperty(ref _scrollX, double.IsFinite(value) ? Math.Max(0, value) : 0);
    }

    public TimelineTool Tool
    {
        get => _tool;
        set => SetProperty(ref _tool, value);
    }

    /// <summary>La pista principal no deja huecos: borrar o recortar en ella corre lo que sigue.</summary>
    public bool MagneticMainTrack
    {
        get => _magneticMainTrack;
        set => SetProperty(ref _magneticMainTrack, value);
    }

    public bool Snapping
    {
        get => _snapping;
        set => SetProperty(ref _snapping, value);
    }

    public bool AutoRipple
    {
        get => _autoRipple;
        set => SetProperty(ref _autoRipple, value);
    }

    public RippleMode RippleMode
    {
        get => _rippleMode;
        set => SetProperty(ref _rippleMode, value);
    }

    /// <summary>El único modo de control de huecos visible en la línea de tiempo.</summary>
    public TimelineGapBehavior GapBehavior
    {
        get => AutoRipple ? TimelineGapBehavior.GlobalRipple
            : MagneticMainTrack ? TimelineGapBehavior.MagneticMainTrack
            : TimelineGapBehavior.LeaveGaps;
        set
        {
            switch (value)
            {
                case TimelineGapBehavior.LeaveGaps:
                    MagneticMainTrack = false;
                    AutoRipple = false;
                    break;
                case TimelineGapBehavior.MagneticMainTrack:
                    MagneticMainTrack = true;
                    AutoRipple = false;
                    break;
                case TimelineGapBehavior.GlobalRipple:
                    MagneticMainTrack = false;
                    AutoRipple = true;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(value));
            }
        }
    }

    public bool AutoCrossfade
    {
        get => _autoCrossfade;
        set => SetProperty(ref _autoCrossfade, value);
    }

    public bool LockEnvelopes
    {
        get => _lockEnvelopes;
        set => SetProperty(ref _lockEnvelopes, value);
    }

    public bool IgnoreGroups
    {
        get => _ignoreGroups;
        set => SetProperty(ref _ignoreGroups, value);
    }

    /// <summary>Zoom horizontal, en píxeles (independientes del DPI) por segundo de secuencia.</summary>
    public double PixelsPerSecond
    {
        get => _pixelsPerSecond;
        set => SetProperty(ref _pixelsPerSecond, Math.Clamp(double.IsFinite(value) ? value : DefaultPixelsPerSecond, MinPixelsPerSecond, MaxPixelsPerSecond));
    }

    public RippleScope RippleScope => RippleMode switch
    {
        RippleMode.AllTracks => RippleScope.AllTracks,
        RippleMode.AllTracksAndMarkers => RippleScope.AllTracksAndMarkers,
        _ => RippleScope.Track,
    };

    public void ZoomIn() => PixelsPerSecond *= ZoomStep;

    public void ZoomOut() => PixelsPerSecond /= ZoomStep;
}
