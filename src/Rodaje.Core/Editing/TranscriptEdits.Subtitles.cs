using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

public static partial class TranscriptEdits
{
    // Los subtítulos no van enlazados a la voz: al quitar tramos de la pista guía (en tiempo de la secuencia de antes), el
    // subtítulo de una frase que se va se va con ella (los que solo pierden algunas palabras, también esas) y, con ripple, los
    // demás corren lo que se cerró para seguir diciendo lo que suena. Las pistas de subtítulos bloqueadas y los clips
    // enlazados no se tocan.
    private static void FollowInSubtitles(EditContext ctx, IReadOnlyList<(Time Start, Time End)> removed, bool ripple)
    {
        List<(Time Start, Time End)> spans = TrackOps.MergeSpans(removed);
        if (spans.Count == 0)
        {
            return;
        }

        Time Map(Time t)
        {
            if (!ripple)
            {
                return t;
            }

            Time shift = Time.Zero;
            foreach ((Time start, Time end) in spans)
            {
                shift += Time.Max(Time.Zero, Time.Min(t, end) - start);
            }

            return t - shift;
        }

        Rational rate = ctx.Sequence.FrameRate;
        bool changed = false;
        foreach (Track track in ctx.Sequence.AllTracks.Where(t => t.Role == TrackRoles.Subtitles && !t.Locked))
        {
            foreach (Clip clip in track.Clips.ToList())
            {
                if (clip.LinkId is not null || clip.Generator is not { Kind: GeneratorKinds.Text } generator)
                {
                    continue;
                }

                Time from = clip.Start, to = clip.End;
                if (!spans.Exists(s => s.Start < to && s.End > from))
                {
                    if (ripple && Map(from) != from)
                    {
                        clip.Start = Map(from);
                        changed = true;
                    }

                    continue;
                }

                changed = true;
                bool[]? gone = generator.Words is { Count: > 0 } words
                    ? words.Select(w =>
                    {
                        Time center = from + (w.Start + Time.FromTicks((w.End - w.Start).Ticks / 2) - clip.SourceIn);
                        return spans.Exists(s => center >= s.Start && center < s.End);
                    }).ToArray()
                    : null;
                Time newStart = Map(from), newEnd = Map(to);
                bool inside = spans.Exists(s => s.Start <= from && s.End >= to);
                if (gone is not null ? gone.All(g => g) : inside)
                {
                    track.Clips.Remove(clip);
                    continue;
                }

                if (newEnd - newStart < Time.FromFrames(1, rate))
                {
                    track.Clips.Remove(clip);
                    continue;
                }

                if (gone is not null && generator.Words is { } all)
                {
                    List<string> tokens = SubtitleEdits.TokenTexts(generator.Text, all);
                    var kept = new List<GeneratorWord>();
                    var keptTokens = new List<string>();
                    for (int i = 0; i < all.Count; i++)
                    {
                        if (gone[i])
                        {
                            continue;
                        }

                        GeneratorWord word = all[i].Clone();
                        Time at = from + (word.Start - clip.SourceIn);
                        Time length = word.End - word.Start;
                        word.Start = Time.Max(Time.Zero, clip.SourceIn + (Map(at) - newStart));
                        word.End = word.Start + length;
                        kept.Add(word);
                        keptTokens.Add(tokens[i]);
                    }

                    if (kept.Count < all.Count)
                    {
                        generator.Text = SubtitleEdits.Rewrap(keptTokens);
                        generator.Spans = null;
                    }

                    generator.Words = kept;
                }

                clip.Start = newStart;
                clip.SourceOut = clip.SourceIn + (newEnd - newStart);
            }

            track.SortClips();
        }

        if (changed)
        {
            ctx.EnsureValid();
        }
    }
}
