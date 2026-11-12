namespace Rodaje.App.Views.Timeline.Logic;

/// <summary>
/// Lógica de desbordamiento de la barra de herramientas de la línea de tiempo.
/// A 1366 px (pantalla portátil), pliega los grupos menos usados (etiquetas/marcadores y modificadores secundarios)
/// en un desplegable «…» y compacta los selectores para garantizar que Deshacer y Rehacer nunca desaparezcan.
/// </summary>
public static class TimelineToolbarOverflow
{
    public const double CompactThreshold = 1440;
    public const double ExtraCompactThreshold = 1370;

    public const double DefaultGapBoxWidth = 184;
    public const double CompactGapBoxWidth = 130;

    public const double DefaultRippleBoxWidth = 196;
    public const double CompactRippleBoxWidth = 140;

    public static ToolbarLayoutState Calculate(double availableWidth)
    {
        bool compact = availableWidth > 0 && availableWidth < CompactThreshold;
        bool extraCompact = availableWidth > 0 && availableWidth < ExtraCompactThreshold;

        return new ToolbarLayoutState(
            ShowDirectMarkers: !compact,
            ShowMoreButton: compact,
            ShowDirectSecondaryToggles: !extraCompact,
            ShowFlyoutSecondaryToggles: extraCompact,
            GapBoxWidth: compact ? CompactGapBoxWidth : DefaultGapBoxWidth,
            RippleBoxWidth: compact ? CompactRippleBoxWidth : DefaultRippleBoxWidth
        );
    }
}

public sealed record ToolbarLayoutState(
    bool ShowDirectMarkers,
    bool ShowMoreButton,
    bool ShowDirectSecondaryToggles,
    bool ShowFlyoutSecondaryToggles,
    double GapBoxWidth,
    double RippleBoxWidth
);
