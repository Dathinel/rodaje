using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

/// <summary>Qué le pasa a un clip si se reemplaza su medio, para avisar antes de hacerlo.</summary>
/// <param name="Shortened">El medio nuevo es más corto que lo que usa el clip y el clip se acorta.</param>
/// <param name="OldDuration">Duración del clip pedido antes de reemplazar.</param>
/// <param name="NewDuration">Duración del clip pedido después de reemplazar.</param>
/// <param name="ClipCount">Clips que cambian de medio: el pedido y sus enlazados con el mismo medio.</param>
public sealed record ReplaceMediaPreview(bool Shortened, Time OldDuration, Time NewDuration, int ClipCount);

public static partial class TimelineEdits
{
    /// <summary>
    /// Cambia el medio del clip, y el de sus enlazados que usaban el mismo medio, por <paramref name="mediaId"/>. Se
    /// conservan la posición, la duración, la velocidad, los efectos y sus keyframes.
    /// </summary>
    /// <remarks>
    /// Si el tramo del origen <c>[SourceIn, SourceOut)</c> cabe en el medio nuevo se usa el mismo; si no, el mismo largo
    /// desde el inicio del medio nuevo, y los keyframes (que están en tiempo del origen) se corren lo mismo para seguir en
    /// su sitio dentro del clip. Si el medio nuevo es más corto que ese largo, el clip se acorta por el final hasta lo que
    /// dura el medio (<see cref="PreviewReplaceMedia"/> lo anuncia); un clip con velocidad no se acorta solo y falla.
    /// </remarks>
    /// <returns>Lo que se hizo, igual que lo habría anunciado <see cref="PreviewReplaceMedia"/>.</returns>
    public static ReplaceMediaPreview ReplaceMedia(EditContext ctx, string clipId, string mediaId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        (List<ReplacePlan> plans, ReplaceMediaPreview preview) = PlanReplaceMedia(ctx.Project, ctx.Sequence, clipId, mediaId);
        foreach (ReplacePlan plan in plans)
        {
            Clip clip = plan.Clip;
            Time delta = plan.SourceIn - clip.SourceIn;
            if (delta != Time.Zero)
            {
                foreach (EffectInstance effect in clip.Effects)
                {
                    foreach (Parameter parameter in effect.Parameters.Values)
                    {
                        parameter.Keyframes = parameter.Keyframes.ConvertAll(k => k with { Time = k.Time + delta });
                    }
                }
            }

            clip.MediaId = mediaId;
            clip.SourceIn = plan.SourceIn;
            clip.SourceOut = plan.SourceOut;
        }

        ctx.EnsureValid();
        return preview;
    }

    /// <summary>Lo que haría <see cref="ReplaceMedia"/> sin tocar nada; lanza <see cref="EditException"/> si no se puede.</summary>
    public static ReplaceMediaPreview PreviewReplaceMedia(Project project, Sequence sequence, string clipId, string mediaId) =>
        PlanReplaceMedia(project, sequence, clipId, mediaId).Preview;

    private readonly record struct ReplacePlan(Clip Clip, Time SourceIn, Time SourceOut);

    private static (List<ReplacePlan> Plans, ReplaceMediaPreview Preview) PlanReplaceMedia(
        Project project, Sequence sequence, string clipId, string mediaId)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(mediaId);
        (Track track, Clip clip) = sequence.FindClip(clipId)
            ?? throw new EditException($"No existe el clip «{clipId}» en la secuencia.");
        if (clip.Generator is not null)
        {
            throw new EditException($"El clip «{clip.Id}» es un título: no tiene un medio que reemplazar.");
        }

        MediaRef media = project.FindMedia(mediaId)
            ?? throw new EditException($"No existe el medio «{mediaId}» en el proyecto.");
        if (clip.MediaId == mediaId)
        {
            throw new EditException($"El clip «{clip.Id}» ya usa «{media.Name}».");
        }

        if (media.Duration <= Time.Zero)
        {
            throw new EditException($"«{media.Name}» no tiene duración: no puede reemplazar el medio de un clip.");
        }

        string oldMedia = clip.MediaId;
        List<(Track Track, Clip Clip)> group = [(track, clip)];
        group.AddRange(sequence.GetLinkGroup(clip.Id).Skip(1).Where(m => m.Clip.MediaId == oldMedia && m.Clip.Generator is null));

        var plans = new List<ReplacePlan>(group.Count);
        bool shortened = false;
        Time newDuration = clip.Duration;
        foreach ((Track t, Clip c) in group)
        {
            EditContext.EnsureUnlocked(t);
            if (t.Kind == TrackKind.Video && !media.HasVideo)
            {
                throw new EditException($"«{media.Name}» no tiene imagen y el clip «{c.Id}» está en la pista de video «{t.Name}».");
            }

            if (t.Kind == TrackKind.Audio && !media.HasAudio)
            {
                throw new EditException($"«{media.Name}» no tiene sonido y el clip «{c.Id}» está en la pista de audio «{t.Name}». Desenlázalo o quítalo antes.");
            }

            Time span = c.SourceDuration;
            if (c.SourceOut <= media.Duration)
            {
                plans.Add(new(c, c.SourceIn, c.SourceOut));
            }
            else if (span <= media.Duration)
            {
                plans.Add(new(c, Time.Zero, span));
            }
            else if (c.Speed is not null)
            {
                throw new EditException(
                    $"«{media.Name}» dura {media.Duration.ToSeconds():0.##} s y el clip «{c.Id}», que tiene velocidad, usa {span.ToSeconds():0.##} s del origen. Recórtalo antes de reemplazar el medio.");
            }
            else
            {
                plans.Add(new(c, Time.Zero, media.Duration));
                shortened = true;
                if (c == clip)
                {
                    newDuration = media.Duration;
                }
            }
        }

        return (plans, new ReplaceMediaPreview(shortened, clip.Duration, newDuration, plans.Count));
    }
}
