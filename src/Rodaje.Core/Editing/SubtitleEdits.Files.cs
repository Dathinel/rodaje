using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

/// <summary>Subtítulos que vienen de un archivo o van a uno: los tiempos se respetan, no se vuelven a partir.</summary>
public static partial class SubtitleEdits
{
    /// <summary>
    /// Pone <paramref name="cues"/> en una pista de subtítulos nueva, encima de todas, como copias de
    /// <paramref name="template"/>. Los tiempos van al cuadro; si dos se pisan, el primero termina donde empieza el
    /// segundo, y uno que se queda sin cuadros dura uno si cabe o se descarta.
    /// </summary>
    /// <returns>Los ids de los clips en orden y, al final, el de la pista.</returns>
    public static IReadOnlyList<string> ImportCues(EditContext ctx, IReadOnlyList<SubtitleCue> cues, ClipGenerator template, string trackName)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(cues);
        ArgumentNullException.ThrowIfNull(template);
        IReadOnlyList<SubtitleCue> placed = FitToFrames(cues, ctx.Sequence.FrameRate);
        if (placed.Count == 0)
        {
            throw new EditException("El archivo no trae subtítulos.");
        }

        string trackId = TitleEdits.AddSubtitleTrack(ctx, trackName);
        Track track = ctx.Sequence.GetTrack(trackId);
        List<string> ids = new(placed.Count + 1);
        foreach (SubtitleCue cue in placed)
        {
            ClipGenerator generator = template.Clone();
            generator.Text = cue.Text;
            var clip = new Clip
            {
                Id = ctx.Ids.NewId("clip"),
                Generator = generator,
                SourceIn = Time.Zero,
                SourceOut = cue.End - cue.Start,
                Start = cue.Start,
            };
            TrackOps.AddClipSorted(track, clip);
            ids.Add(clip.Id);
        }

        ids.Add(trackId);
        ctx.EnsureValid();
        return ids;
    }

    /// <summary>Ordena, lleva al cuadro y quita los solapes (ver <see cref="ImportCues"/>).</summary>
    public static IReadOnlyList<SubtitleCue> FitToFrames(IReadOnlyList<SubtitleCue> cues, Rational frameRate)
    {
        ArgumentNullException.ThrowIfNull(cues);
        Time frame = Time.FromFrames(1, frameRate);
        SubtitleCue[] sorted = [.. cues.Where(c => c.Text.Trim().Length > 0 && c.End > c.Start).OrderBy(c => c.Start)];
        var result = new List<SubtitleCue>(sorted.Length);
        for (int i = 0; i < sorted.Length; i++)
        {
            Time start = Time.Max(Time.Zero, sorted[i].Start.SnapToFrame(frameRate));
            if (result.Count > 0 && start < result[^1].End)
            {
                SubtitleCue previous = result[^1];
                if (start <= previous.Start)
                {
                    start = previous.Start + frame;
                }

                result[^1] = previous with { End = start };
            }

            Time end = Time.Max(sorted[i].End.SnapToFrame(frameRate), start + frame);
            if (i + 1 < sorted.Length)
            {
                Time next = Time.Max(Time.Zero, sorted[i + 1].Start.SnapToFrame(frameRate));
                if (next > start)
                {
                    end = Time.Min(end, Time.Max(next, start + frame));
                }
            }

            result.Add(new SubtitleCue(start, end, sorted[i].Text.Trim()));
        }

        return result;
    }

    /// <summary>Los clips de texto de una pista como subtítulos, en orden; los títulos vacíos no cuentan.</summary>
    public static IReadOnlyList<SubtitleCue> CuesOf(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return [.. track.Clips
            .Where(c => c.Generator is { Kind: GeneratorKinds.Text } g && g.Text.Trim().Length > 0)
            .OrderBy(c => c.Start)
            .Select(c => new SubtitleCue(c.Start, c.Start + c.Duration, c.Generator!.Text.Trim()))];
    }
}
