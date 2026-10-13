using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

/// <summary>
/// Un ángulo de una multicámara: un medio y dónde cae su instante 0 en el reloj común de todos los ángulos (lo que da la
/// sincronización por forma de onda).
/// </summary>
public sealed record MulticamAngle(string MediaId, Time Offset);

/// <summary>Cortar entre ángulos sincronizados: el clip de la pista pasa a mostrar otro ángulo desde un instante.</summary>
public static class MulticamEdits
{
    /// <summary>
    /// Desde <paramref name="at"/> hasta el siguiente corte, el clip de video de <paramref name="trackId"/> muestra el
    /// ángulo <paramref name="angle"/> en el mismo instante del reloj común: se corta en <paramref name="at"/> (si no es
    /// su inicio) y a la parte derecha se le cambia el medio. El sonido enlazado no cambia; los efectos y sus keyframes
    /// siguen al material, como al reemplazar un medio.
    /// </summary>
    /// <returns>El clip que muestra el ángulo nuevo.</returns>
    public static string SwitchAngle(EditContext ctx, IReadOnlyList<MulticamAngle> angles, string trackId, Time at, int angle)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(angles);
        if (angle < 0 || angle >= angles.Count)
        {
            throw new EditException($"La multicámara tiene {angles.Count} ángulos: no hay un ángulo {angle + 1}.");
        }

        Track track = TrimmingSupport.ResolveTrack(ctx, trackId);
        if (track.Kind != TrackKind.Video)
        {
            throw new EditException("Los ángulos se cortan en una pista de video.");
        }

        EditContext.EnsureUnlocked(track);
        Clip clip = track.ClipAt(at) ?? throw new EditException("No hay un clip bajo el cursor en la pista de la multicámara.");
        int current = IndexOf(angles, clip.MediaId);
        if (clip.Generator is not null || clip.IsCompound || current < 0)
        {
            throw new EditException($"El clip «{clip.Id}» no es un ángulo de esta multicámara.");
        }

        if (clip.Speed is not null)
        {
            throw new EditException("Un ángulo con velocidad no se puede cambiar: déjalo a 1×.");
        }

        if (current == angle)
        {
            throw new EditException($"Ese tramo ya muestra el ángulo {angle + 1}.");
        }

        MulticamAngle target = angles[angle];
        MediaRef media = ctx.Project.FindMedia(target.MediaId)
            ?? throw new EditException($"El ángulo {angle + 1} usa un medio que ya no está en el proyecto.");
        if (!media.HasVideo)
        {
            throw new EditException($"El ángulo {angle + 1} («{media.Name}») no tiene imagen.");
        }

        // El mismo instante del reloj común: origen + offset del ángulo actual − offset del nuevo.
        Time delta = angles[current].Offset - target.Offset;
        Time end = clip.End;
        if (clip.SourceIn + (at - clip.Start) + delta < Time.Zero || clip.SourceOut + delta > media.Duration)
        {
            throw new EditException($"El ángulo {angle + 1} («{media.Name}») no tiene material de {at} a {end}.");
        }

        Clip right = clip;
        if (at > clip.Start)
        {
            IReadOnlyList<string> pieces = TimelineEdits.Split(ctx, clip.Id, at);
            right = track.Clips.Find(c => c.Id == pieces[0])!;
        }

        foreach (EffectInstance effect in right.Effects)
        {
            foreach (Parameter parameter in effect.Parameters.Values)
            {
                parameter.Keyframes = parameter.Keyframes.ConvertAll(k => k with { Time = k.Time + delta });
            }
        }

        right.MediaId = target.MediaId;
        right.SourceIn += delta;
        right.SourceOut += delta;
        ctx.EnsureValid();
        return right.Id;
    }

    /// <summary>El ángulo que muestra el clip, o −1.</summary>
    public static int IndexOf(IReadOnlyList<MulticamAngle> angles, string mediaId)
    {
        ArgumentNullException.ThrowIfNull(angles);
        for (int i = 0; i < angles.Count; i++)
        {
            if (angles[i].MediaId == mediaId)
            {
                return i;
            }
        }

        return -1;
    }
}
