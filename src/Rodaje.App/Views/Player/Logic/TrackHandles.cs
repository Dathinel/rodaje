using System.Numerics;
using Rodaje.App.Services.Tracking;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Render;
using O = Rodaje.Core.Model.BuiltInEffects.TrackOptions;

namespace Rodaje.App.Views.Player;

/// <summary>Qué parte de un punto de seguimiento queda bajo el puntero.</summary>
public enum TrackGrip
{
    None,

    /// <summary>El centro: mueve el punto (en un cuadro seguido, lo corrige a mano).</summary>
    Center,

    /// <summary>Una esquina del patrón: lo agranda o achica.</summary>
    Pattern,

    /// <summary>Una esquina de la zona de búsqueda.</summary>
    Search,
}

/// <summary>Un punto en el visor: dónde está, su patrón y su zona en la imagen (0–1) y la confianza del cuadro (−1 sin seguir).</summary>
public readonly record struct TrackPointBox(Vector2 Position, Vector2 Pattern, Vector2 Search, float Confidence);

/// <summary>Un seguimiento en el cursor: dónde está la imagen de la capa y sus puntos.</summary>
public sealed record TrackView(string ClipId, string TrackId, LayerPlacement Layer, Time Source, TrackPointBox[] Points, string Color,
    PointTrackKind Kind, float Threshold);

/// <summary>
/// Los puntos de un seguimiento en el visor, sin WinUI: la misma colocación que el render (<see cref="TrackApply.PlacementAt"/>),
/// el acierto del puntero y lo que hace un arrastre.
/// </summary>
public static class TrackHandles
{
    public const double GripRadius = 7;

    /// <summary>El seguimiento <paramref name="track"/> en <paramref name="timeline"/>; nulo si el clip no se ve ahí.</summary>
    public static TrackView? Find(Project project, Sequence sequence, Clip clip, EffectInstance track, Time timeline)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(track);
        if (timeline < clip.Start || timeline >= clip.End)
        {
            return null;
        }

        Time source = clip.ToEffectTime(timeline);
        if (TrackApply.PlacementAt(project, sequence, clip, source) is not { } layer)
        {
            return null;
        }

        TrackPointSpec[] specs = PointTrack.ParsePoints(track.Options.GetValueOrDefault(O.Points));
        PointTrack? samples = PointTrack.ParseCached(track.Options.GetValueOrDefault(O.Samples));
        float[] confidence = [.. specs.Select(_ => -1f)];
        Vector2[] positions = [.. specs.Select(s => s.Position)];
        if (samples is not null && samples.PointCount == specs.Length && source >= samples.First && source <= samples.Last)
        {
            positions = samples.PositionsAt(source);
            int index = samples.IndexOf(source);
            if (index >= 0)
            {
                confidence = [.. samples.FrameAt(index).Select(s => s.Confidence)];
            }
        }

        double threshold = track.Parameters.TryGetValue(BuiltInEffects.TrackParameters.Threshold, out Parameter? p) ? p.Value : 0.7;
        return new TrackView(clip.Id, track.Id, layer, source,
            [.. specs.Select((s, i) => new TrackPointBox(positions[i], s.Pattern, s.Search, confidence[i]))],
            track.Options.GetValueOrDefault(O.Color) ?? TrackEdits.Palette[0], PointTrack.ParseKind(track.Options.GetValueOrDefault(O.Kind)),
            (float)threshold);
    }

    /// <summary>Un punto de la imagen (0–1) en el panel.</summary>
    public static Vector2 ToPanel(TrackView view, ImageFrame frame, Vector2 image)
    {
        ArgumentNullException.ThrowIfNull(view);
        return frame.ToPanel(view.Layer.Map(image.X * view.Layer.LocalWidth, image.Y * view.Layer.LocalHeight));
    }

    /// <summary>Un punto del panel en la imagen (0–1); nulo si no se puede.</summary>
    public static Vector2? ToImage(TrackView view, ImageFrame frame, Vector2 panel)
    {
        ArgumentNullException.ThrowIfNull(view);
        return view.Layer.Unmap(frame.ToSequence(panel.X, panel.Y)) is { } local
            ? local / new Vector2(view.Layer.LocalWidth, view.Layer.LocalHeight)
            : null;
    }

    /// <summary>Las cuatro esquinas de una caja de <paramref name="size"/> (fracción de la imagen) centrada en el punto, en el panel.</summary>
    public static Vector2[] Box(TrackView view, ImageFrame frame, Vector2 center, Vector2 size) =>
    [
        ToPanel(view, frame, center + (new Vector2(-0.5f, -0.5f) * size)), ToPanel(view, frame, center + (new Vector2(0.5f, -0.5f) * size)),
        ToPanel(view, frame, center + (new Vector2(0.5f, 0.5f) * size)), ToPanel(view, frame, center + (new Vector2(-0.5f, 0.5f) * size)),
    ];

    /// <summary>El punto y la parte bajo <paramref name="panel"/>: primero los centros, después las esquinas del patrón y las de la búsqueda.</summary>
    public static (int Point, TrackGrip Grip) Hit(TrackView view, ImageFrame frame, Vector2 panel)
    {
        ArgumentNullException.ThrowIfNull(view);
        for (int i = 0; i < view.Points.Length; i++)
        {
            if (Vector2.Distance(ToPanel(view, frame, view.Points[i].Position), panel) <= GripRadius)
            {
                return (i, TrackGrip.Center);
            }
        }

        for (int i = 0; i < view.Points.Length; i++)
        {
            TrackPointBox p = view.Points[i];
            if (Box(view, frame, p.Position, p.Pattern).Any(c => Vector2.Distance(c, panel) <= GripRadius))
            {
                return (i, TrackGrip.Pattern);
            }

            if (Box(view, frame, p.Position, p.Search).Any(c => Vector2.Distance(c, panel) <= GripRadius))
            {
                return (i, TrackGrip.Search);
            }
        }

        return (-1, TrackGrip.None);
    }

    /// <summary>
    /// El tamaño nuevo de una caja al llevar su esquina a <paramref name="image"/>: simétrico alrededor del punto, al menos de
    /// <paramref name="minimum"/>; la búsqueda nunca más chica que el patrón.
    /// </summary>
    public static Vector2 Resized(Vector2 center, Vector2 image, Vector2 minimum) => Vector2.Max(2 * Vector2.Abs(image - center), minimum);
}
