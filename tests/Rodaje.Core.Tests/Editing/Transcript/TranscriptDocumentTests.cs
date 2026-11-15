using System.Diagnostics;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Editing.Transcript;

/// <summary>El documento de la transcripción (D-061): proyección por clip, párrafos, palabra activa y búsqueda.</summary>
public sealed class TranscriptDocumentTests
{
    private static TrimFixture Whole() => new TrimFixture().Add("a1", "a", "m2", 0, 200, 0).WithTranscript();

    [Fact]
    public void A_whole_clip_shows_every_word_at_its_source_time()
    {
        TranscriptDocument doc = Whole().Doc();

        Assert.Equal("a1", doc.TrackId);
        Assert.Equal(TranscriptFixture.WordCount, doc.Words.Count);
        Assert.All(doc.Words, (w, i) => Assert.Equal((F(10 * i), F(10 * i + 6), i), (w.SeqStart, w.SeqEnd, w.WordIndex)));
    }

    [Fact]
    public void A_word_belongs_to_the_clip_that_holds_its_center()
    {
        // Centros en 10i + 3: el 33 queda fuera de [35, 105) y el 103 dentro, aunque termine después de la salida.
        TranscriptDocument doc = new TrimFixture().Add("a1", "a", "m2", 35, 105, 100).WithTranscript().Doc();

        Assert.Equal(["w4", "w5", "w6", "w7", "w8", "w9", "w10"], doc.Texts());
        Assert.Equal(F(105), doc.Words[0].SeqStart);
        Assert.Equal(F(170), doc.Words[^1].SeqEnd);
    }

    [Theory]
    [InlineData(1, 2, false)]
    [InlineData(2, 1, false)]
    [InlineData(1, 1, true)]
    [InlineData(2, 1, true)]
    public void With_speed_or_reverse_each_word_maps_back_to_its_source(int num, int den, bool reverse)
    {
        TrimFixture f = Whole();
        Clip clip = f.Retime("a", new ClipSpeed { Rate = new Rational(num, den), Reverse = reverse });
        MediaTranscript transcript = f.Project.Transcripts[0];

        TranscriptDocument doc = f.Doc();

        Assert.Equal(TranscriptFixture.WordCount, doc.Words.Count);
        AssertMapsBack(clip, transcript, doc, reverse, tolerance: (num + den - 1) / den);
    }

    [Fact]
    public void With_a_speed_curve_each_word_maps_back_to_its_source()
    {
        TrimFixture f = Whole();
        var curve = new SpeedCurve { Span = F(200), Points = [new SpeedPoint(0, 50), new SpeedPoint(SpeedCurve.AtScale, 300)] };
        Clip clip = f.Retime("a", new ClipSpeed { Curve = curve });

        TranscriptDocument doc = f.Doc();

        Assert.NotEmpty(doc.Words);
        AssertMapsBack(clip, f.Project.Transcripts[0], doc, reverse: false, tolerance: 3);
    }

    private static void AssertMapsBack(Clip clip, MediaTranscript transcript, TranscriptDocument doc, bool reverse, long tolerance)
    {
        for (int i = 0; i < doc.Words.Count; i++)
        {
            DocWord word = doc.Words[i];
            Assert.True(word.SeqStart <= word.SeqEnd);
            Assert.True(i == 0 || doc.Words[i - 1].SeqStart <= word.SeqStart, "fuera de orden");
            TranscriptWord source = transcript.Words[word.WordIndex];
            Time expected = reverse ? source.End : source.Start;
            Time back = clip.ToSourceTime(word.SeqStart);
            if (back >= clip.SourceIn && back < clip.SourceOut)
            {
                Assert.InRange(Math.Abs((back - expected).Ticks), 0, tolerance);
            }
        }

        if (reverse)
        {
            Assert.Equal("w19", doc.Words[0].Text);
        }
    }

    [Theory]
    [InlineData(new long[] { 50 })]
    [InlineData(new long[] { 13, 47, 120 })]
    [InlineData(new long[] { 3, 4, 5, 6, 7, 8 })]
    public void Cutting_a_clip_does_not_change_the_text(long[] cuts)
    {
        TrimFixture f = Whole();
        string[] before = f.Doc().Texts();

        AnalysisEdits.SplitAt(f.Ctx, "a", cuts.Select(F));
        TranscriptDocument after = f.Doc();

        Assert.Equal(before, after.Texts());
        Assert.Equal(cuts.Length + 1, f.Track("a1").Clips.Count);
    }

    [Fact]
    public void Paragraphs_break_on_clip_speaker_marked_start_and_long_pause()
    {
        TrimFixture f = new TrimFixture()
            .Add("a1", "a", "m2", 0, 100, 0)
            .Add("a1", "b", "m2", 100, 200, 300)
            .WithTranscript();
        List<TranscriptWord> words = f.Project.Transcripts[0].Words;
        f.Project.Transcripts[0].Speakers.Add(new TranscriptSpeaker { Id = "s1", Name = "Ana" });
        words[3].SpeakerId = "s1";
        words[4].SpeakerId = "s1";
        words[6].ParagraphStart = true;
        words.RemoveRange(11, 4); // de w10 (termina en 106) a w15 (empieza en 150) hay 1,76 s

        TranscriptDocument doc = f.Doc();

        Assert.Equal(
            [(0, 3, "a", null), (3, 2, "a", "s1"), (5, 1, "a", null), (6, 4, "a", null), (10, 1, "b", null), (11, 5, "b", null)],
            doc.Paragraphs.Select(p => (p.FirstWord, p.Count, p.ClipId, p.SpeakerId)));
    }

    [Fact]
    public void A_very_long_paragraph_is_split()
    {
        var f = new TrimFixture().Add("a1", "a", "m2", 0, 200, 0).WithTranscript(words: 1000, step: 0, length: 0);

        TranscriptDocument doc = f.Doc();

        Assert.Equal([400, 400, 200], doc.Paragraphs.Select(p => p.Count));
    }

    [Fact]
    public void Active_word_is_the_last_one_that_started()
    {
        TranscriptDocument doc = Whole().Doc();

        Assert.Equal(-1, doc.ActiveWord(-Time.FromTicks(1)));
        Assert.Equal(0, doc.ActiveWord(Time.Zero));
        Assert.Equal(0, doc.ActiveWord(F(9)));
        Assert.Equal(1, doc.ActiveWord(F(10)));
        Assert.Equal(19, doc.ActiveWord(F(1000)));
        Assert.Equal(-1, TranscriptDocument.Empty.ActiveWord(Time.Zero));
    }

    [Fact]
    public void Find_ignores_case_accents_and_punctuation()
    {
        TrimFixture f = Whole();
        List<TranscriptWord> words = f.Project.Transcripts[0].Words;
        words[2].Text = "Camión,";
        words[3].Text = "rojo.";
        words[7].Text = "CAMION";
        words[8].Text = "¿Rojo?";

        TranscriptDocument doc = f.Doc();

        Assert.Equal([(2, 2), (7, 2)], doc.Find("camión  ROJO"));
        Assert.Equal([(2, 1), (7, 1)], doc.Find("amio"));
        Assert.Equal([(5, 1)], doc.Find("W5"));
        Assert.Empty(doc.Find("  ¿? "));
        Assert.Empty(doc.Find("verde"));
    }

    [Fact]
    public void Freezes_stills_titles_and_media_without_transcript_show_nothing()
    {
        TrimFixture f = new TrimFixture()
            .Add("a1", "plain", "m1", 0, 100, 0)
            .Add("a2", "frozen", "m2", 0, 50, 0)
            .WithTranscript();
        f.Clip("frozen").Speed = new ClipSpeed { Freeze = true };

        Assert.Same(TranscriptDocument.Empty, f.Doc());
        Assert.Empty(f.Doc("a2").Words);
        Assert.Same(TranscriptDocument.Empty, new TrimFixture().Doc());
    }

    [Fact]
    public void The_default_guide_is_the_first_audio_track_with_transcribed_clips()
    {
        TrimFixture f = new TrimFixture()
            .Add("v1", "video", "m2", 0, 200, 0, link: "l")
            .Add("a1", "other", "m1", 0, 100, 0)
            .Add("a2", "audio", "m2", 0, 200, 0, link: "l")
            .WithTranscript();

        Assert.Equal("a2", TranscriptDocument.DefaultGuideTrack(f.Project, f.Sequence));
        Assert.Equal("v1", f.Doc("v1").TrackId);
        Assert.Same(TranscriptDocument.Empty, f.Doc("no-existe"));
    }

    [Fact]
    [Trait("Categoria", "Rendimiento")]
    public void One_hour_in_200_clips_builds_finds_and_seeks_within_budget()
    {
        // Una hora a 25 fps (90 000 cuadros), 9 000 palabras de 6 cuadros cada 10, en 200 clips seguidos.
        var f = new TrimFixture();
        f.Project.Media.Add(new MediaRef { Id = "hora", Name = "hora", Duration = F(90_000), HasAudio = true });
        for (int i = 0; i < 200; i++)
        {
            f.Add("a1", "c" + i, "hora", i * 450, (i + 1) * 450, i * 450);
        }

        f.WithTranscript("hora", words: 9_000);

        // El mejor de varios intentos: la app va con AOT, sin el código sin optimizar de las primeras llamadas del JIT.
        TranscriptDocument doc = TranscriptDocument.Empty;
        double buildMs = double.MaxValue, findMs = double.MaxValue;
        int found = 0;
        var clock = new Stopwatch();
        for (int run = 0; run < 8; run++)
        {
            clock.Restart();
            doc = f.Doc();
            buildMs = Math.Min(buildMs, clock.Elapsed.TotalMilliseconds);
            clock.Restart();
            found = doc.Find("w8999").Count + doc.Find("w4500").Count;
            findMs = Math.Min(findMs, clock.Elapsed.TotalMilliseconds / 2);
        }

        Time[] probes = [.. Enumerable.Range(0, 100_000).Select(i => F(i % 90_000))];
        clock.Restart();
        int total = 0;
        foreach (Time probe in probes)
        {
            total += doc.ActiveWord(probe);
        }

        double activeUs = clock.Elapsed.TotalMilliseconds * 1000 / 100_000;

        Assert.Equal(9_000, doc.Words.Count);
        Assert.Equal(2, found);
        Assert.True(total > 0);
        // Las cifras de D-061 son de la app (Release con AOT); en Debug, con la cobertura instrumentando cada línea, el triple.
        bool unoptimized = typeof(TranscriptDocument).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false)
            .OfType<DebuggableAttribute>().Any(a => a.IsJITOptimizerDisabled);
        double slack = unoptimized ? 3 : 1;
        Assert.True(buildMs <= 15 * slack, $"Build tardó {buildMs:F1} ms");
        Assert.True(activeUs <= 2 * slack, $"ActiveWord tardó {activeUs:F3} µs");
        Assert.True(findMs <= 5 * slack, $"Find tardó {findMs:F1} ms");
    }
}
