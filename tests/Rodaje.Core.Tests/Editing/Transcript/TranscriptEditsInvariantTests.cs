using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Timing;
using Xunit.Sdk;

namespace Rodaje.Core.Tests.Editing.Transcript;

/// <summary>
/// Invariantes de <see cref="TranscriptEdits"/> y de sus comandos con montajes aleatorios reproducibles (D-061): el
/// medio m2 (200 cuadros) partido en 1-4 clips seguidos en a1 con su video enlazado en v1, con huecos o sin ellos, y
/// una transcripción de paso y largo aleatorios. Unas 50 secuencias por semilla.
/// </summary>
public sealed class TranscriptEditsInvariantTests
{
    private const int Cases = 50;

    // Copiado de TranscriptEditsTests: cada enlace conserva el mismo desfase y largo en todos sus clips.
    private static void AssertLinksInSync(Sequence sequence)
    {
        foreach (IGrouping<string, Clip> group in sequence.AllTracks.SelectMany(t => t.Clips).Where(c => c.LinkId is not null).GroupBy(c => c.LinkId!))
        {
            Assert.Single(group.Select(c => (c.Start - c.SourceIn, c.Duration)).Distinct());
        }
    }

    [Theory]
    [InlineData(71001)]
    [InlineData(71002)]
    [InlineData(71003)]
    [InlineData(71004)]
    public void Deleting_random_words_leaves_only_their_text_gone_and_the_sequence_shorter_by_what_was_removed(int seed)
    {
        var random = new Random(seed);
        for (int i = 0; i < Cases; i++)
        {
            TrimFixture f = Interview(random, gaps: random.Next(2) == 0, out string describe);
            TranscriptDocument doc = f.Doc();
            int[] words = Pick(random, doc);
            string[] expected = [.. doc.Texts().Where((_, w) => !words.Contains(w))];
            var clips = TrackSpans(f);
            try
            {
                IReadOnlyList<(Time Start, Time End)> removed =
                    TranscriptEdits.DeleteWords(f.Ctx, doc, words, ripple: true, RippleScope.Track);

                Assert.Equal(expected, f.Doc().Texts());
                Assert.Equal(ExpectedEnd(clips, removed, ripple: true), f.Sequence.Duration);
                AssertLinksInSync(f.Sequence);
                f.AssertValid();
            }
            catch (Exception error)
            {
                throw new XunitException($"Semilla {seed}, caso {i} ({describe}), palabras [{string.Join(',', words)}]: {error}");
            }
        }
    }

    [Theory]
    [InlineData(71011)]
    [InlineData(71012)]
    [InlineData(71013)]
    [InlineData(71014)]
    public void Without_ripple_the_text_loses_only_the_selection_and_the_track_keeps_its_end(int seed)
    {
        var random = new Random(seed);
        for (int i = 0; i < Cases; i++)
        {
            TrimFixture f = Interview(random, gaps: random.Next(2) == 0, out string describe);
            TranscriptDocument doc = f.Doc();
            int[] words = Pick(random, doc);
            string[] expected = [.. doc.Texts().Where((_, w) => !words.Contains(w))];
            var clips = TrackSpans(f);
            try
            {
                IReadOnlyList<(Time Start, Time End)> removed =
                    TranscriptEdits.DeleteWords(f.Ctx, doc, words, ripple: false, RippleScope.Track);

                Assert.Equal(expected, f.Doc().Texts());
                Assert.Equal(ExpectedEnd(clips, removed, ripple: false), f.Sequence.Duration);
                AssertLinksInSync(f.Sequence);
                f.AssertValid();
            }
            catch (Exception error)
            {
                throw new XunitException($"Semilla {seed}, caso {i} ({describe}), palabras [{string.Join(',', words)}]: {error}");
            }
        }
    }

    [Theory]
    [InlineData(71021)]
    [InlineData(71022)]
    [InlineData(71023)]
    [InlineData(71024)]
    public void Undo_restores_the_identical_project_after_delete_and_move(int seed)
    {
        var random = new Random(seed);
        for (int i = 0; i < Cases; i++)
        {
            TrimFixture f = Interview(random, gaps: random.Next(2) == 0, out string describe);
            TranscriptDocument doc = f.Doc();
            int[] words = Pick(random, doc);
            (int first, int count, int before) = PickPhrase(random, doc.Words.Count);
            string saved = ProjectSerializer.Serialize(f.Project);
            var undo = new UndoStack(f.Project);
            try
            {
                undo.Execute(Edits.DeleteWords("seq", null, words, ripple: true, ids: new SequentialIdGenerator()));
                Assert.NotEqual(saved, ProjectSerializer.Serialize(f.Project));
                undo.Undo();
                Assert.Equal(saved, ProjectSerializer.Serialize(f.Project));

                try
                {
                    undo.Execute(Edits.MoveWords("seq", null, first, count, before, ids: new SequentialIdGenerator()));
                }
                catch (EditException refused) when (refused.Message.Contains("hueco", StringComparison.Ordinal))
                {
                    // Una frase que cruza un hueco entre clips no se mueve, y no deja nada a medias.
                    Assert.Equal(saved, ProjectSerializer.Serialize(f.Project));
                    continue;
                }

                Assert.NotEqual(saved, ProjectSerializer.Serialize(f.Project));
                undo.Undo();
                Assert.Equal(saved, ProjectSerializer.Serialize(f.Project));
            }
            catch (Exception error)
            {
                throw new XunitException($"Semilla {seed}, caso {i} ({describe}), palabras [{string.Join(',', words)}], frase [{first}, {first + count}) antes de {before}: {error}");
            }
        }
    }

    [Theory]
    [InlineData(71031)]
    [InlineData(71032)]
    [InlineData(71033)]
    [InlineData(71034)]
    public void Moving_a_phrase_and_back_restores_text_and_duration(int seed)
    {
        var random = new Random(seed);
        for (int i = 0; i < Cases; i++)
        {
            // Sin huecos entre clips la duración de la secuencia no cambia al mover: lo que se quita con ripple vuelve
            // entero al pegar insertando, y los tramos movidos de una frase seguida son contiguos.
            TrimFixture f = Interview(random, gaps: false, out string describe);
            string[] original = f.Doc().Texts();
            Time duration = f.Sequence.Duration;
            (int first, int count, int before) = PickPhrase(random, original.Length);
            string[] phrase = [.. original.Skip(first).Take(count)];
            string[] rest = [.. original.Where((_, w) => w < first || w >= first + count)];
            int at = before > first ? before - count : before;
            int back = before > first ? first : first + count;
            try
            {
                TranscriptEdits.MoveWords(f.Ctx, f.Doc(), first, count, before, RippleScope.Track);

                Assert.Equal([.. rest.Take(at), .. phrase, .. rest.Skip(at)], f.Doc().Texts());
                Assert.Equal(duration, f.Sequence.Duration);
                AssertLinksInSync(f.Sequence);
                f.AssertValid();

                TranscriptEdits.MoveWords(f.Ctx, f.Doc(), at, count, back, RippleScope.Track);

                Assert.Equal(original, f.Doc().Texts());
                Assert.Equal(duration, f.Sequence.Duration);
                AssertLinksInSync(f.Sequence);
                f.AssertValid();
            }
            catch (Exception error)
            {
                throw new XunitException($"Semilla {seed}, caso {i} ({describe}), frase [{first}, {first + count}) antes de {before}: {error}");
            }
        }
    }

    /// <summary>
    /// m2 en <c>clips</c> piezas seguidas del origen, con su pareja enlazada en v1 y huecos opcionales de pista entre
    /// unas y otras. Cada pieza mide al menos <c>2·step</c> para que enseñe palabras.
    /// </summary>
    private static TrimFixture Interview(Random random, bool gaps, out string describe)
    {
        int step = random.Next(4, 13);
        int length = random.Next(1, step);
        int words = random.Next(8, (200 - length) / step + 2);
        int clips = random.Next(1, 5);
        describe = $"clips={clips}, step={step}, length={length}, palabras={words}, huecos={(gaps ? "sí" : "no")}";

        TrimFixture f = new TrimFixture().WithTranscript("m2", words, step, length);
        long source = 0, start = 0;
        foreach (long cut in Cuts(random, clips - 1, 2L * step).Append(200L))
        {
            string link = $"l{source}";
            f.Add("v1", $"v{source}", "m2", source, cut, start, link)
             .Add("a1", $"a{source}", "m2", source, cut, start, link);
            start += cut - source;
            if (gaps)
            {
                start += random.Next(3) == 0 ? 0 : random.Next(1, 16);
            }

            source = cut;
        }

        return f;
    }

    /// <summary>Puntos de corte de [0, 200) en piezas de al menos <paramref name="minPiece"/> cuadros.</summary>
    private static long[] Cuts(Random random, int count, long minPiece)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            long[] cuts = [.. Enumerable.Range(0, count).Select(_ => (long)random.Next(1, 200)).Distinct().Order()];
            if (cuts.Length == count
                && (cuts.Length == 0 || (cuts[0] >= minPiece
                    && 200 - cuts[^1] >= minPiece
                    && cuts.Zip(cuts.Skip(1), (a, b) => b - a).All(d => d >= minPiece))))
            {
                return cuts;
            }
        }

        return [.. Enumerable.Range(1, count).Select(i => i * 200L / (count + 1))];
    }

    /// <summary>Una selección de palabras: todo el documento, un clip entero, sueltas o una a tres frases seguidas.</summary>
    private static int[] Pick(Random random, TranscriptDocument doc)
    {
        int count = doc.Words.Count;
        switch (random.Next(8))
        {
            case 0:
                return [.. Enumerable.Range(0, count)];
            case 1:
                string clipId = doc.Words[random.Next(count)].ClipId;
                return [.. Enumerable.Range(0, count).Where(i => doc.Words[i].ClipId == clipId)];
            case 2:
                int[] loose = [.. Enumerable.Range(0, count).Where(_ => random.Next(3) == 0)];
                return loose.Length > 0 ? loose : [random.Next(count)];
            default:
                var picked = new SortedSet<int>();
                for (int runs = random.Next(1, 4); runs > 0; runs--)
                {
                    for (int i = random.Next(count), last = Math.Min(count, i + random.Next(1, 7)); i < last; i++)
                    {
                        picked.Add(i);
                    }
                }

                return [.. picked];
        }
    }

    /// <summary>Una frase <c>[first, first + count)</c> y un destino fuera de ella (<c>before == count</c> total: al final).</summary>
    private static (int First, int Count, int Before) PickPhrase(Random random, int words)
    {
        int count = random.Next(1, words);
        int first = random.Next(words - count + 1);
        int before;
        do
        {
            before = random.Next(words + 1);
        }
        while (before >= first && before <= first + count);

        return (first, count, before);
    }

    private static List<List<(Time Start, Time End)>> TrackSpans(TrimFixture f) =>
        [.. f.Sequence.AllTracks.Select(t => t.Clips.Select(c => (c.Start, c.End)).ToList())];

    /// <summary>El final de la secuencia tras quitar <paramref name="removed"/> de cada pista.</summary>
    private static Time ExpectedEnd(List<List<(Time Start, Time End)>> tracks, IReadOnlyList<(Time Start, Time End)> removed, bool ripple) =>
        tracks.Select(clips => EndAfter(clips, removed, ripple)).DefaultIfEmpty(Time.Zero).Max();

    /// <summary>
    /// El final de una pista tras quitarle los tramos de <paramref name="removed"/> (que están dentro de sus clips):
    /// con ripple cada pieza que queda se corre a la izquierda la suma de lo quitado delante de ella, así que un hueco
    /// de pista por delante del último clip también deja de contar; sin ripple las piezas no se mueven.
    /// </summary>
    private static Time EndAfter(IReadOnlyList<(Time Start, Time End)> clips, IReadOnlyList<(Time Start, Time End)> removed, bool ripple)
    {
        Time end = Time.Zero;
        foreach ((Time clipStart, Time clipEnd) in clips)
        {
            Time shift = ripple
                ? removed.Where(r => r.End <= clipStart).Aggregate(Time.Zero, (sum, r) => sum + (r.End - r.Start))
                : Time.Zero;
            Time segmentStart = clipStart;
            foreach ((Time rs, Time re) in removed)
            {
                if (re <= clipStart || rs >= clipEnd)
                {
                    continue;
                }

                Time pieceEnd = Time.Min(rs, clipEnd);
                if (pieceEnd > segmentStart)
                {
                    end = Time.Max(end, pieceEnd - shift);
                }

                segmentStart = Time.Max(segmentStart, Time.Min(re, clipEnd));
                if (ripple)
                {
                    shift += Time.Min(re, clipEnd) - Time.Max(rs, clipStart);
                }
            }

            if (segmentStart < clipEnd)
            {
                end = Time.Max(end, clipEnd - shift);
            }
        }

        return end;
    }
}
