using Rodaje.Core.Editing;

namespace Rodaje.App.Services;

/// <summary>Herramienta activa de la línea de tiempo (barra estilo Vegas).</summary>
public enum TimelineTool
{
    Normal,
    Envelope,
    Selection,
    Zoom,
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

/// <summary>Opciones de la línea de tiempo que no son parte del proyecto: herramienta, interruptores y zoom.</summary>
public sealed partial class TimelineState : ObservableObject
{
    public const double MinPixelsPerSecond = 2;
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

    public RippleScope RippleScope => RippleMode == RippleMode.AffectedTracks ? RippleScope.Track : RippleScope.AllTracks;

    public void ZoomIn() => PixelsPerSecond *= ZoomStep;

    public void ZoomOut() => PixelsPerSecond /= ZoomStep;
}
