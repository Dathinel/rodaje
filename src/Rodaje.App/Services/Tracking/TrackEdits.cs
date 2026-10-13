using System.Numerics;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using O = Rodaje.Core.Model.BuiltInEffects.TrackOptions;

namespace Rodaje.App.Services.Tracking;

/// <summary>Las ediciones de un seguimiento guardado en el clip (<see cref="BuiltInEffects.Track"/>), cada una un comando deshacible.</summary>
public static class TrackEdits
{
    /// <summary>Colores que van tomando los seguimientos nuevos en el visor.</summary>
    public static readonly string[] Palette = ["#FFD54F", "#4DD0E1", "#F06292", "#AED581", "#9575CD"];

    /// <summary>Lado del patrón de fábrica, en fracción del alto de la imagen (unos 49 px en 1080p).</summary>
    public const float PatternSide = 0.045f;

    /// <summary>Cuántas veces el patrón mide la zona de búsqueda de fábrica.</summary>
    public const float SearchFactor = 2.6f;

    /// <summary>Los puntos de partida de un tipo: centrados, con el patrón y la búsqueda de fábrica (cuadrados en la imagen).</summary>
    public static TrackPointSpec[] DefaultPoints(PointTrackKind kind, double aspect)
    {
        float a = (float)Math.Max(0.1, aspect);
        var pattern = new Vector2(PatternSide / a, PatternSide);
        var search = pattern * SearchFactor;
        Vector2[] at = kind switch
        {
            PointTrackKind.Point => [new(0.5f, 0.5f)],
            PointTrackKind.Two => [new(0.4f, 0.5f), new(0.6f, 0.5f)],
            _ => [new(0.38f, 0.32f), new(0.62f, 0.32f), new(0.62f, 0.68f), new(0.38f, 0.68f)],
        };
        return [.. at.Select(p => new TrackPointSpec(p, pattern, search))];
    }

    /// <summary>Un seguimiento nuevo al final de la pila del clip.</summary>
    public static SequenceEditCommand Create(string sequenceId, string clipId, PointTrackKind kind, IReadOnlyList<TrackPointSpec> points, string name,
        string color) =>
        new("Nuevo seguimiento", sequenceId, ctx =>
        {
            string id = EffectEdits.AddEffect(ctx, clipId, BuiltInEffects.Track);
            EffectEdits.SetOption(ctx, clipId, id, O.Kind, PointTrack.KindName(kind));
            EffectEdits.SetOption(ctx, clipId, id, O.Points, PointTrack.FormatPoints(points));
            EffectEdits.SetOption(ctx, clipId, id, O.Name, name);
            EffectEdits.SetOption(ctx, clipId, id, O.Color, color);
            return [id];
        });

    /// <summary>Cambia los puntos de partida (posición, patrón y búsqueda).</summary>
    public static SequenceEditCommand SetPoints(string sequenceId, string clipId, string trackId, IReadOnlyList<TrackPointSpec> points,
        string description, string? mergeKey = null) =>
        new(description, sequenceId, ctx => EffectEdits.SetOption(ctx, clipId, trackId, O.Points, PointTrack.FormatPoints(points)), mergeKey: mergeKey);

    /// <summary>Reemplaza los cuadros seguidos (nulo los borra).</summary>
    public static SequenceEditCommand SetSamples(string sequenceId, string clipId, string trackId, PointTrack? samples, string description,
        string? mergeKey = null) =>
        new(description, sequenceId, ctx =>
        {
            string text = samples?.Format() ?? "";
            if (text.Length > PointTrack.MaxTextLength)
            {
                throw new EditException("El seguimiento es demasiado largo para guardarlo: sigue un tramo más corto.");
            }

            EffectEdits.SetOption(ctx, clipId, trackId, O.Samples, text);
        }, mergeKey: mergeKey);

    /// <summary>Los puntos donde empieza a seguir en <paramref name="source"/>: los del cuadro seguido si lo hay; si no, los de partida.</summary>
    public static TrackPointSpec[] StartAt(EffectInstance track, Time source)
    {
        ArgumentNullException.ThrowIfNull(track);
        TrackPointSpec[] specs = PointTrack.ParsePoints(track.Options.GetValueOrDefault(O.Points));
        if (PointTrack.ParseCached(track.Options.GetValueOrDefault(O.Samples)) is { } samples && samples.PointCount == specs.Length
            && samples.IndexOf(source) is >= 0 and var index)
        {
            IReadOnlyList<TrackSample> frame = samples.FrameAt(index);
            return [.. specs.Select((s, i) => s with { Position = frame[i].Position })];
        }

        return specs;
    }
}
