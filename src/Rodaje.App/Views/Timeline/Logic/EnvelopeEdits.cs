using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Views.Timeline;

/// <summary>
/// La envolvente de volumen de los clips (keyframes de <c>rodaje.audio.gainDb</c> en tiempo del origen) y el volumen de
/// las pistas de audio (<see cref="Track.Effects"/>, keyframes en tiempo de la secuencia).
/// </summary>
public static class EnvelopeEdits
{
    /// <summary>Distancia en píxeles a la que se agarra un punto de la envolvente.</summary>
    public const double PointGrab = 6;

    /// <summary>Margen del bloque del clip y de la envolvente dentro de la fila (el mismo con que se dibujan).</summary>
    private const double ClipInset = 2;
    private const double EnvelopeInset = 4;

    /// <summary>La y del contenido de una ganancia sobre la fila del clip.</summary>
    public static double EnvelopeY(TrackRow row, double gainDb)
    {
        ArgumentNullException.ThrowIfNull(row);
        double top = row.Top + ClipInset + EnvelopeInset;
        double range = row.Height - (2 * (ClipInset + EnvelopeInset));
        return top + ((1 - ClipDecorations.EnvelopeFraction(gainDb)) * range);
    }

    /// <summary>La ganancia que corresponde a una y del contenido sobre la fila del clip.</summary>
    public static double GainDbAtY(TrackRow row, double contentY)
    {
        ArgumentNullException.ThrowIfNull(row);
        double top = row.Top + ClipInset + EnvelopeInset;
        double range = row.Height - (2 * (ClipInset + EnvelopeInset));
        return ClipDecorations.GainDbFromFraction(1 - ((contentY - top) / range));
    }

    /// <summary>El punto de la envolvente bajo el puntero (instante de la línea de tiempo), si hay uno cerca.</summary>
    public static Time? PointAt(Clip clip, TrackRow row, TimelineViewport viewport, double x, double contentY)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(viewport);
        Time? best = null;
        double bestDistance = double.MaxValue;
        foreach ((Time time, double gainDb) in ClipDecorations.GainPoints(clip))
        {
            double dx = Math.Abs(viewport.TimeToX(time) - x);
            double dy = Math.Abs(EnvelopeY(row, gainDb) - contentY);
            if (dx <= PointGrab && dy <= PointGrab && dx + dy < bestDistance)
            {
                bestDistance = dx + dy;
                best = time;
            }
        }

        return best;
    }

    /// <summary>
    /// Pone un punto de la envolvente en <paramref name="to"/> (instante de la línea de tiempo, dentro del clip, sin
    /// pasar a sus vecinos) con <paramref name="gainDb"/>; si <paramref name="from"/> no es nulo, el punto que estaba ahí
    /// se mueve. Crea el efecto de volumen si falta. Se funde con el siguiente movimiento del mismo clip: arrastrar deja un paso.
    /// </summary>
    public static SequenceEditCommand SetPoint(string sequenceId, string clipId, Time? from, Time to, double gainDb, IIdGenerator? ids = null) =>
        new("Envolvente de volumen", sequenceId, ctx =>
        {
            Clip clip = EditableClip(ctx, clipId);
            Parameter gain = GainOf(ctx, clip.Effects, create: true)!;
            ClipTimeMap map = clip.TimeMap;
            (Time first, Time last) = ClipMapping.EffectRange(map);
            // El borde final es el último instante del tramo (el mapeo acota al último tick dentro del clip).
            Time target = to >= map.End ? last : Clamp(map.EffectTime(to), first, last);
            int index = from is { } f ? IndexAt(gain, map, f) : -1;
            if (index >= 0)
            {
                Time low = index > 0 ? gain.Keyframes[index - 1].Time + Time.FromTicks(1) : first;
                Time high = index < gain.Keyframes.Count - 1 ? gain.Keyframes[index + 1].Time - Time.FromTicks(1) : last;
                target = Clamp(target, low, high);
                gain.Keyframes[index] = gain.Keyframes[index] with { Time = target, Value = ClampDb(gainDb) };
            }
            else
            {
                Upsert(gain, target, ClampDb(gainDb));
            }
        }, ids, $"envelope:{clipId}");

    /// <summary>Quita el punto de <paramref name="at"/>; sin puntos, la ganancia queda fija en el valor del que se quitó.</summary>
    public static SequenceEditCommand RemovePoint(string sequenceId, string clipId, Time at, IIdGenerator? ids = null) =>
        new("Quitar punto de la envolvente", sequenceId, ctx =>
        {
            Clip clip = EditableClip(ctx, clipId);
            if (GainOf(ctx, clip.Effects, create: false) is not { } gain)
            {
                return;
            }

            int index = IndexAt(gain, clip.TimeMap, at);
            if (index < 0)
            {
                return;
            }

            double value = gain.Keyframes[index].Value;
            gain.Keyframes.RemoveAt(index);
            if (gain.Keyframes.Count == 0)
            {
                gain.Value = value;
            }
        }, ids);

    /// <summary>El volumen de toda la pista, en dB (sin efecto, 0), en un instante de la secuencia.</summary>
    public static double TrackGainDb(Track track, Time at)
    {
        ArgumentNullException.ThrowIfNull(track);
        EffectInstance? volume = track.Effects.Find(e => e.Type == BuiltInEffects.Audio && e.Enabled);
        return volume is not null && volume.Parameters.TryGetValue(BuiltInEffects.AudioParameters.GainDb, out Parameter? gain)
            ? gain.Evaluate(at)
            : 0;
    }

    /// <summary>
    /// Volumen de la pista como un deslizador: sin keyframes cambia el valor fijo; con keyframes fija el de
    /// <paramref name="at"/> (tiempo de la secuencia). Se funde con el siguiente cambio de la misma pista.
    /// </summary>
    public static SequenceEditCommand SetTrackGain(string sequenceId, string trackId, double gainDb, Time at, IIdGenerator? ids = null) =>
        new("Volumen de la pista", sequenceId, ctx =>
        {
            Track track = ctx.Sequence.FindTrack(trackId) ?? throw new EditException("La pista ya no está en la secuencia.");
            EditContext.EnsureUnlocked(track);
            if (track.Kind != TrackKind.Audio)
            {
                throw new EditException("El volumen de pista es de las pistas de audio.");
            }

            Parameter gain = GainOf(ctx, track.Effects, create: true)!;
            if (gain.Keyframes.Count > 0)
            {
                Upsert(gain, at, ClampDb(gainDb));
            }
            else
            {
                gain.Value = ClampDb(gainDb);
            }
        }, ids, $"track-gain:{trackId}:{at.Ticks}");

    /// <summary>
    /// El keyframe que se dibuja en <paramref name="timeline"/>: se busca por su lugar en la línea de tiempo y no por el
    /// origen de ese instante, porque a otra velocidad el viaje de ida y vuelta no siempre cae en el mismo tick.
    /// </summary>
    private static int IndexAt(Parameter gain, ClipTimeMap map, Time timeline) =>
        gain.Keyframes.FindIndex(k => ClipMapping.TimelineOfEffect(map, k.Time) == timeline);

    private static Clip EditableClip(EditContext ctx, string clipId)
    {
        if (ctx.Sequence.FindClip(clipId) is not { } found)
        {
            throw new EditException("El clip ya no está en la secuencia.");
        }

        EditContext.EnsureUnlocked(found.Track);
        return found.Clip;
    }

    private static Parameter? GainOf(EditContext ctx, List<EffectInstance> effects, bool create)
    {
        EffectInstance? volume = effects.Find(e => e.Type == BuiltInEffects.Audio && e.Enabled);
        if (volume is null)
        {
            if (!create)
            {
                return null;
            }

            volume = new EffectInstance { Id = ctx.Ids.NewId("effect"), Type = BuiltInEffects.Audio };
            effects.Add(volume);
        }

        if (!volume.Parameters.TryGetValue(BuiltInEffects.AudioParameters.GainDb, out Parameter? gain))
        {
            if (!create)
            {
                return null;
            }

            gain = new Parameter();
            volume.Parameters[BuiltInEffects.AudioParameters.GainDb] = gain;
        }

        return gain;
    }

    private static void Upsert(Parameter gain, Time at, double value)
    {
        int index = gain.Keyframes.FindIndex(k => k.Time >= at);
        if (index >= 0 && gain.Keyframes[index].Time == at)
        {
            gain.Keyframes[index] = gain.Keyframes[index] with { Value = value };
        }
        else
        {
            gain.Keyframes.Insert(index < 0 ? gain.Keyframes.Count : index, new Keyframe(at, value));
        }
    }

    private static double ClampDb(double gainDb)
    {
        ParameterDefinition? definition = EffectRegistry.Find(BuiltInEffects.Audio)?.FindParameter(BuiltInEffects.AudioParameters.GainDb);
        return definition?.Clamp(gainDb) ?? gainDb;
    }

    private static Time Clamp(Time value, Time low, Time high) => value < low ? low : value > high ? high : value;
}
