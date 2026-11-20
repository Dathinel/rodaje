using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Editing.Transcript;

/// <summary>Borrar texto y los subtítulos de las palabras que se van (F1 del repaso del 19-nov).</summary>
public sealed class TranscriptSubtitlesTests
{
    // Voz de m2 con palabras w{i} en [10i, 10i + 6) y tres subtítulos en v2: w0-w4 en [0, 46), w5-w9 en [50, 96), w10-w14 en [100, 146).
    private static TrimFixture WithSubtitles()
    {
        TrimFixture f = new TrimFixture()
            .Add("v1", "v", "m2", 0, 200, 0, link: "l")
            .Add("a1", "a", "m2", 0, 200, 0, link: "l")
            .WithTranscript();
        f.Track("v2").Role = TrackRoles.Subtitles;
        for (int k = 0; k < 3; k++)
        {
            long first = k * 5, start = first * 10;
            f.AddTitle("v2", "s" + k, start, start + 46, start);
            ClipGenerator generator = f.Clip("s" + k).Generator!;
            generator.Text = string.Join(' ', Enumerable.Range((int)first, 5).Select(i => "w" + i));
            generator.Words = [.. Enumerable.Range((int)first, 5).Select(i => new GeneratorWord { Start = F(i * 10), End = F(i * 10 + 6), Text = "w" + i })];
        }

        return f;
    }

    [Fact]
    public void Deleting_a_whole_phrase_removes_its_subtitle_and_closes_the_line()
    {
        TrimFixture f = WithSubtitles();

        TranscriptEdits.DeleteWords(f.Ctx, f.Doc(), [5, 6, 7, 8, 9], ripple: true, RippleScope.Track);

        Assert.Equal(["s0", "s2"], f.Track("v2").Clips.Select(c => c.Id));
        f.AssertClip("s0", 0, 46, 0);
        f.AssertClip("s2", 100, 146, 50);
        f.AssertValid();
    }

    [Fact]
    public void Deleting_part_of_a_phrase_keeps_the_rest_of_its_subtitle_in_sync()
    {
        TrimFixture f = WithSubtitles();

        TranscriptEdits.DeleteWords(f.Ctx, f.Doc(), [3, 4, 5, 6], ripple: true, RippleScope.Track);

        // Se quitan [28, 68): el primer subtítulo pierde w3 y w4; el segundo, w5 y w6.
        Clip first = f.Clip("s0"), second = f.Clip("s1");
        Assert.Equal("w0 w1 w2", first.Generator!.Text);
        Assert.Equal(F(28), first.End);
        Assert.Equal("w7 w8 w9", second.Generator!.Text);
        Assert.Equal((F(28), F(56)), (second.Start, second.End));
        Assert.Equal(F(60), f.Clip("s2").Start);
        Assert.Equal(["w7", "w8", "w9"], second.Generator.Words!.Select(w => w.Text));
        f.AssertValid();
    }

    [Fact]
    public void Without_ripple_the_subtitles_of_the_deleted_words_go_and_the_rest_stay_where_they_are()
    {
        TrimFixture f = WithSubtitles();

        TranscriptEdits.DeleteWords(f.Ctx, f.Doc(), [5, 6, 7, 8, 9], ripple: false, RippleScope.Track);

        Assert.Equal(["s0", "s2"], f.Track("v2").Clips.Select(c => c.Id));
        f.AssertClip("s2", 100, 146, 100);
    }

    [Fact]
    public void A_locked_subtitle_track_is_left_alone()
    {
        TrimFixture f = WithSubtitles().Lock("v2");

        TranscriptEdits.DeleteWords(f.Ctx, f.Doc(), [5, 6, 7, 8, 9], ripple: true, RippleScope.Track);

        Assert.Equal(["s0", "s1", "s2"], f.Track("v2").Clips.Select(c => c.Id));
    }

    [Fact]
    public void With_all_tracks_the_ripple_shifts_the_subtitles_once()
    {
        TrimFixture f = new TrimFixture()
            .Add("v1", "v", "m2", 0, 200, 0, link: "l")
            .Add("a1", "a", "m2", 0, 200, 0, link: "l")
            .WithTranscript();
        f.Track("v2").Role = TrackRoles.Subtitles;
        f.AddTitle("v2", "s", 150, 196, 150);

        IReadOnlyList<(Time Start, Time End)> removed = TranscriptEdits.DeleteWords(f.Ctx, f.Doc(), [0, 1, 2, 3, 4], ripple: true, RippleScope.AllTracks);

        Assert.Equal(F(150) - removed.Aggregate(Time.Zero, (sum, s) => sum + (s.End - s.Start)), f.Clip("s").Start);
        f.AssertValid();
    }

    [Fact]
    public void Removing_pauses_shifts_the_subtitles_after_them()
    {
        TrimFixture f = WithSubtitles();
        Time before = f.Clip("s2").Start;

        IReadOnlyList<(Time Start, Time End)> removed = TranscriptEdits.RemovePauses(f.Ctx, f.Doc(), minimum: F(3), keep: F(1), ripple: true, RippleScope.Track);

        Assert.NotEmpty(removed);
        Assert.True(f.Clip("s2").Start < before);
        f.AssertValid();
    }
}
