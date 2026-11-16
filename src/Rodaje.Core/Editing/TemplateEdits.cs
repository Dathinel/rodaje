using System.Text.Json;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

/// <summary>
/// Ediciones de los clips de plantilla de movimiento (D-064). Insertar, mover, dividir, copiar y recortar usan el código de
/// los generadores; aquí van cambiar los valores (la app instancia y entrega las capas) y «Convertir en títulos».
/// </summary>
public static class TemplateEdits
{
    /// <summary>
    /// Lo más corto que puede quedar el clip al recortarlo: un cuadro o, en una plantilla de movimiento, su
    /// <see cref="GeneratorTemplate.MinSeconds"/> (D-064). Si ya era más corto (salió de dividir), no se alarga.
    /// </summary>
    public static Time MinDuration(Clip clip, Time frame)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return clip.Generator?.Template is { } template
            ? Time.Max(frame, Time.Min(clip.Duration, Time.FromSeconds(template.MinSeconds)))
            : frame;
    }

    /// <summary>Cambia los valores y las capas ya instanciadas: un paso del historial.</summary>
    public static void SetValues(EditContext ctx, string clipId, Dictionary<string, JsonElement> values, List<TemplateLayer> layers)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(layers);
        GeneratorTemplate template = Template(ctx, clipId).Template;
        template.Values = new Dictionary<string, JsonElement>(values);
        template.Layers = layers.ConvertAll(l => l.Clone());
        ctx.EnsureValid();
    }

    /// <summary>
    /// Cambia el clip por un título por capa con lo mismo que se veía: la capa de abajo en la pista del clip y las demás en
    /// pistas de video libres por encima (o nuevas). Cada título lleva los efectos del clip, con la posición de la capa
    /// sumada a su transformación, y su ventana dentro del clip.
    /// </summary>
    /// <returns>Los ids de los títulos, de abajo arriba, y los de las pistas creadas.</returns>
    public static IReadOnlyList<string> ConvertToTitles(EditContext ctx, string clipId)
    {
        (Track track, Clip clip, GeneratorTemplate template) = Template(ctx, clipId);
        GeneratorEdges edges = clip.Generator!.Edges;
        int trackIndex = ctx.Sequence.VideoTracks.IndexOf(track);
        track.Clips.Remove(clip);
        var created = new List<string>();
        var tracks = new List<string>();
        int above = trackIndex;
        foreach (TemplateLayer layer in template.Layers)
        {
            Time before = Time.FromSeconds(layer.Start) - edges.Before;
            Time after = Time.FromSeconds(layer.End) - edges.After;
            Time skip = Time.Max(Time.Zero, before);
            Time cut = Time.Max(Time.Zero, after);
            if (clip.Duration - skip - cut <= Time.Zero)
            {
                continue;
            }

            var title = new Clip
            {
                Id = ctx.Ids.NewId("clip"),
                Start = clip.Start + skip,
                SourceIn = clip.SourceIn + skip,
                SourceOut = clip.SourceOut - cut,
                Enabled = clip.Enabled,
                Color = clip.Color,
                Generator = new ClipGenerator
                {
                    Text = layer.Text,
                    Style = layer.Style.Clone(),
                    Box = layer.Box?.Clone(),
                    Edges = new GeneratorEdges { In = edges.In, Out = edges.Out, Before = Time.Max(Time.Zero, -before), After = Time.Max(Time.Zero, -after) },
                },
                Effects = CopyEffects(ctx, clip.Effects),
            };
            Offset(ctx, title, layer);

            Track target = created.Count == 0 ? track : FreeTrackAbove(ctx, ref above, title.Start, title.End, tracks);
            TrackOps.AddClipSorted(target, title);
            created.Add(title.Id);
        }

        ctx.EnsureValid();
        return [.. created, .. tracks];
    }

    private static (Track Track, Clip Clip, GeneratorTemplate Template) Template(EditContext ctx, string clipId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        (Track track, Clip clip) = ctx.Sequence.FindClip(clipId)
            ?? throw new EditException($"El clip «{clipId}» ya no está en la secuencia.");
        EditContext.EnsureUnlocked(track);
        return clip.Generator?.Template is { } template
            ? (track, clip, template)
            : throw new EditException($"El clip «{clipId}» no es una plantilla de movimiento.");
    }

    /// <summary>La primera pista de video por encima de la anterior que tiene libre el tramo; si no hay, una nueva.</summary>
    private static Track FreeTrackAbove(EditContext ctx, ref int above, Time start, Time end, List<string> createdTracks)
    {
        List<Track> video = ctx.Sequence.VideoTracks;
        for (int i = above + 1; i < video.Count; i++)
        {
            if (!video[i].Locked && video[i].Role is null && TrackOps.FindOverlap(video[i], start, end) is null)
            {
                above = i;
                return video[i];
            }
        }

        string id = TimelineEdits.AddTrack(ctx, TrackKind.Video, video.Count, "Texto");
        createdTracks.Add(id);
        above = video.Count - 1;
        return ctx.Sequence.GetTrack(id);
    }

    private static List<EffectInstance> CopyEffects(EditContext ctx, List<EffectInstance> effects)
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        List<EffectInstance> copies = effects.ConvertAll(e =>
        {
            EffectInstance copy = e.Clone();
            copy.Id = ctx.Ids.NewId("effect");
            ids[e.Id] = copy.Id;
            return copy;
        });
        foreach (EffectInstance copy in copies)
        {
            if (copy.Type == BuiltInEffects.Mask && copy.Options.GetValueOrDefault(BuiltInEffects.MaskOptions.Target) is { Length: > 0 } target)
            {
                copy.Options[BuiltInEffects.MaskOptions.Target] = ids.GetValueOrDefault(target, target);
            }
        }

        return copies;
    }

    /// <summary>Suma la posición de la capa (píxeles de la referencia de su estilo) a la transformación del título.</summary>
    private static void Offset(EditContext ctx, Clip title, TemplateLayer layer)
    {
        Sequence sequence = ctx.Sequence;
        double unit = Math.Min((double)sequence.Width / layer.Style.Reference.Width, (double)sequence.Height / layer.Style.Reference.Height);
        double dx = layer.X * unit;
        double dy = layer.Y * unit;
        if (dx == 0 && dy == 0)
        {
            return;
        }

        EffectInstance? transform = title.Effects.Find(e => e.Type == BuiltInEffects.Transform);
        if (transform is null)
        {
            transform = new EffectInstance { Id = ctx.Ids.NewId("effect"), Type = BuiltInEffects.Transform };
            title.Effects.Insert(0, transform);
        }

        Shift(transform, BuiltInEffects.TransformParameters.X, dx);
        Shift(transform, BuiltInEffects.TransformParameters.Y, dy);
    }

    private static void Shift(EffectInstance transform, string name, double by)
    {
        if (!transform.Parameters.TryGetValue(name, out Parameter? parameter))
        {
            parameter = new Parameter { Value = 0 };
            transform.Parameters[name] = parameter;
        }

        parameter.Value += by;
        parameter.Keyframes = parameter.Keyframes.ConvertAll(k => k with { Value = k.Value + by });
    }
}
