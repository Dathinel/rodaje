using Rodaje.Core.Editing;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Editing.Transcript;

/// <summary>
/// Borrar palabras nunca quita más que el hueco entre las palabras vecinas que se quedan (F2 del repaso del 19-nov: con la
/// voz partida en dos, Supr se llevó el clip entero). Selecciones al azar, con ripple y sin él.
/// </summary>
public sealed class TranscriptDeleteBoundsTests
{
    // Palabras w{i} en [10i, 10i + 6) del medio m2 (200 cuadros): el corte en 98 cae en una pausa.
    private static TrimFixture Scenario(int index) => index switch
    {
        // La voz partida en dos, con video enlazado.
        0 => new TrimFixture()
            .Add("v1", "va", "m2", 0, 98, 0, link: "l1").Add("a1", "aa", "m2", 0, 98, 0, link: "l1")
            .Add("v1", "vb", "m2", 98, 200, 98, link: "l2").Add("a1", "ab", "m2", 98, 200, 98, link: "l2")
            .WithTranscript(),
        // El mismo tramo del medio dos veces, una detrás de otra.
        1 => new TrimFixture()
            .Add("v1", "va", "m2", 0, 98, 0, link: "l1").Add("a1", "aa", "m2", 0, 98, 0, link: "l1")
            .Add("v1", "vb", "m2", 0, 98, 98, link: "l2").Add("a1", "ab", "m2", 0, 98, 98, link: "l2")
            .WithTranscript(),
        // Tres trozos, con un hueco entre el segundo y el tercero.
        _ => new TrimFixture()
            .Add("v1", "va", "m2", 0, 58, 0, link: "l1").Add("a1", "aa", "m2", 0, 58, 0, link: "l1")
            .Add("v1", "vb", "m2", 58, 138, 58, link: "l2").Add("a1", "ab", "m2", 58, 138, 58, link: "l2")
            .Add("v1", "vc", "m2", 138, 200, 150, link: "l3").Add("a1", "ac", "m2", 138, 200, 150, link: "l3")
            .WithTranscript(),
    };

    private static Time AudioLength(TrimFixture f) => f.Track("a1").Clips.Aggregate(Time.Zero, (sum, c) => sum + c.Duration);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Deleting_words_never_removes_more_than_the_gap_between_the_words_that_stay(int scenario)
    {
        var random = new Random(97 + scenario);
        for (int round = 0; round < 80; round++)
        {
            TrimFixture f = Scenario(scenario);
            TranscriptDocument doc = f.Doc();
            string[] texts = doc.Texts();
            var picked = new bool[doc.Words.Count];
            for (int runs = random.Next(1, 4); runs > 0; runs--)
            {
                int first = random.Next(picked.Length), length = random.Next(1, 9);
                for (int i = first; i < Math.Min(picked.Length, first + length); i++)
                {
                    picked[i] = true;
                }
            }

            if (picked.All(p => p))
            {
                picked[random.Next(picked.Length)] = false;
            }

            // Lo que cubren las palabras: de donde acaba la palabra que se queda antes a donde empieza la que se queda después.
            Time end = f.Track("a1").Clips.Max(c => c.End), bound = Time.Zero;
            for (int i = 0; i < picked.Length; i++)
            {
                if (!picked[i] || (i > 0 && picked[i - 1]))
                {
                    continue;
                }

                int last = i;
                while (last + 1 < picked.Length && picked[last + 1])
                {
                    last++;
                }

                bound += (last + 1 == picked.Length ? end : doc.Words[last + 1].SeqStart) - (i == 0 ? Time.Zero : doc.Words[i - 1].SeqEnd);
            }

            Time before = AudioLength(f);
            int[] words = [.. Enumerable.Range(0, picked.Length).Where(i => picked[i])];
            bool ripple = round % 2 == 0;

            TranscriptEdits.DeleteWords(f.Ctx, doc, words, ripple, RippleScope.Track);

            string context = $"escenario {scenario}, ronda {round}, palabras [{string.Join(',', words)}], ripple {ripple}";
            Assert.True(texts.Where((_, i) => !picked[i]).SequenceEqual(f.Doc().Texts()), "Quedan otras palabras que las no elegidas: " + context);
            Time removed = before - AudioLength(f);
            Assert.True(removed > Time.Zero && removed <= bound, $"Se quitaron {removed.ToSeconds():0.00} s y las palabras cubren {bound.ToSeconds():0.00} s: {context}");
            TranscriptEditsTests.AssertLinksInSync(f.Sequence);
            f.AssertValid();
        }
    }
}
