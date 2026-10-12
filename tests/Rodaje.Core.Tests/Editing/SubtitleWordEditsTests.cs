using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Editing;

/// <summary>Edición por palabras de un subtítulo automático: emparejar con el texto, corregir, dividir, unir y karaoke.</summary>
public class SubtitleWordEditsTests
{
    private static Time S(double seconds) => Time.FromSeconds(seconds);

    /// <summary>Un subtítulo en v2 desde el segundo 2, con sus palabras de 0,4 s (10 cuadros) en el origen (que empieza en 0 en su borde).</summary>
    private static TrimFixture Subtitle(string text, double start = 2, string id = "s1")
    {
        TrimFixture f = new TrimFixture().AddTitle("v2", id, 0, 0, 0);
        Clip clip = f.Clip(id);
        string[] parts = text.Split(' ', '\n');
        clip.Start = S(start);
        clip.SourceOut = S(parts.Length * 0.4);
        clip.Generator!.Text = text;
        clip.Generator.Words = [.. parts.Select((p, i) => new GeneratorWord { Start = S(i * 0.4), End = S((i + 1) * 0.4), Text = p })];
        f.AssertValid();
        return f;
    }

    private static List<GeneratorWord> Words(string text) =>
        [.. text.Split(' ').Select((p, i) => new GeneratorWord { Start = S(i), End = S(i + 1), Text = p })];

    [Fact]
    public void Ranges_follow_the_text_and_survive_a_hand_edit()
    {
        IReadOnlyList<WordRange> same = SubtitleEdits.WordRanges("Hola a\ntodos.", Words("Hola a todos"));
        Assert.Equal([new WordRange(0, 4), new WordRange(5, 1), new WordRange(7, 6)], same);

        // Se agregó una palabra a mano: el resto se encuentra por su texto y la que falta queda sin rango.
        IReadOnlyList<WordRange> edited = SubtitleEdits.WordRanges("Pues hola a todos", Words("hola gente todos"));
        Assert.Equal(new WordRange(5, 4), edited[0]);
        Assert.False(edited[1].Found);
        Assert.Equal(new WordRange(12, 5), edited[2]);
    }

    [Fact]
    public void The_active_word_is_the_last_that_started()
    {
        List<GeneratorWord> words = Words("uno dos tres");
        Assert.Equal(-1, SubtitleEdits.ActiveWord(words, S(-0.1)));
        Assert.Equal(0, SubtitleEdits.ActiveWord(words, S(0)));
        Assert.Equal(1, SubtitleEdits.ActiveWord(words, S(1.99)));
        Assert.Equal(2, SubtitleEdits.ActiveWord(words, S(9)));
    }

    [Fact]
    public void Correcting_a_word_changes_word_and_text_keeping_the_line_breaks()
    {
        TrimFixture f = Subtitle("Ola a\ntodos");
        SubtitleEdits.CorrectWord(f.Ctx, "s1", 0, "Hola");
        ClipGenerator g = f.Clip("s1").Generator!;
        Assert.Equal("Hola a\ntodos", g.Text);
        Assert.Equal("Hola", g.Words![0].Text);
        Assert.Equal(S(0.4), g.Words[0].End);

        // Con espacio se vuelve dos palabras que se reparten el tiempo; vacía, se borra.
        SubtitleEdits.CorrectWord(f.Ctx, "s1", 2, "todas ustedes");
        Assert.Equal("Hola a\ntodas ustedes", g.Text);
        Assert.Equal(["Hola", "a", "todas", "ustedes"], g.Words!.Select(w => w.Text));
        Assert.Equal(S(1.2), g.Words[3].End);
        Assert.True(g.Words[2].End > S(0.8) && g.Words[2].End < S(1.2));

        SubtitleEdits.CorrectWord(f.Ctx, "s1", 1, " ");
        Assert.Equal("Hola\ntodas ustedes", g.Text);
        Assert.Equal(3, g.Words!.Count);
        f.AssertValid();
    }

    [Fact]
    public void Splitting_at_a_word_leaves_two_subtitles_with_their_words()
    {
        TrimFixture f = Subtitle("Hola a todos. Bienvenidos al canal");
        string right = SubtitleEdits.SplitAtWord(f.Ctx, "s1", 3);

        Clip left = f.Clip("s1"), second = f.Clip(right);
        Assert.Equal(S(2), left.Start);
        Assert.Equal(S(3.2), left.End);
        Assert.Equal(S(3.2), second.Start);
        Assert.Equal(S(4.4), second.End);
        Assert.Equal("Hola a todos.", left.Generator!.Text);
        Assert.Equal("Bienvenidos al canal", second.Generator!.Text);
        Assert.Equal(["Bienvenidos", "al", "canal"], second.Generator.Words!.Select(w => w.Text));

        // Las palabras siguen en el origen del clip: la primera del trozo derecho empieza justo en su borde.
        Assert.Equal(second.SourceIn, second.Generator.Words![0].Start);
        Assert.True(left.Generator.Edges.Out && second.Generator.Edges.In);
        Assert.Equal(Time.Zero, second.Generator.Edges.Before);
        AssertFails(() => SubtitleEdits.SplitAtWord(f.Ctx, "s1", 0), "no sea la primera");
        f.AssertValid();
    }

    [Fact]
    public void Joining_brings_back_one_subtitle_with_all_words_in_its_origin()
    {
        TrimFixture f = Subtitle("Hola a todos. Bienvenidos al canal");
        string right = SubtitleEdits.SplitAtWord(f.Ctx, "s1", 3);
        SubtitleEdits.CorrectWord(f.Ctx, right, 2, "canal!");
        SubtitleEdits.JoinWithNext(f.Ctx, "s1");

        Clip clip = f.Clip("s1");
        Assert.Single(f.Track("v2").Clips);
        Assert.Equal(S(4.4), clip.End);
        Assert.Equal("Hola a todos. Bienvenidos al canal!", clip.Generator!.Text);
        Assert.Equal(S(1.2), clip.Generator.Words![3].Start);
        Assert.Equal("canal!", clip.Generator.Words[5].Text);
        AssertFails(() => SubtitleEdits.JoinWithNext(f.Ctx, "s1"), "No hay otro subtítulo");
        f.AssertValid();
    }

    [Fact]
    public void Joining_two_separate_subtitles_shifts_the_second_words()
    {
        TrimFixture f = Subtitle("Uno dos");
        Clip other = Model.ModelSamples.MakeTitle("s2", 4, 0, 1, "tres");
        other.Generator!.Words = [new GeneratorWord { Start = S(0.2), End = S(0.8), Text = "tres" }];
        f.Track("v2").Clips.Add(other);
        SubtitleEdits.JoinWithNext(f.Ctx, "s1");

        Clip clip = f.Clip("s1");
        Assert.Equal(S(5), clip.End);
        Assert.Equal(S(2.2), clip.Generator!.Words![2].Start);
        Assert.Equal("Uno dos tres", clip.Generator.Text);
    }

    [Fact]
    public void Word_edits_are_single_undo_steps_and_need_words()
    {
        TrimFixture f = Subtitle("Hola a todos");
        var stack = new UndoStack(f.Project);
        SequenceEditCommand split = Edits.SplitSubtitleAtWord("seq", "s1", 2);
        stack.Execute(split);
        Assert.Equal(2, f.Track("v2").Clips.Count);
        stack.Undo();
        Assert.Single(f.Track("v2").Clips);
        Assert.Equal("Hola a todos", f.Clip("s1").Generator!.Text);

        f.Clip("s1").Generator!.Words = null;
        AssertFails(() => SubtitleEdits.CorrectWord(f.Ctx, "s1", 0, "x"), "palabras transcritas");
    }

    [Fact]
    public void The_highlight_color_is_validated_and_round_trips()
    {
        TrimFixture f = Subtitle("Hola");
        TextStyle style = f.Clip("s1").Generator!.Style!;
        style.Highlight = new StyleHighlight { Color = "#FFD400" };
        f.AssertValid();
        string json = ProjectSerializer.Serialize(f.Project);
        Assert.Contains("\"highlight\"", json, StringComparison.Ordinal);
        Assert.Equal("#FFD400", ProjectSerializer.Deserialize(json).Sequences[0].GetClip("s1").Clip.Generator!.Style!.Highlight!.Color);
        Assert.True(style.ContentEquals(style.Clone()));

        style.Highlight.Color = "amarillo";
        Assert.Contains(ModelValidator.Validate(f.Project), e => e.Contains("palabra resaltada", StringComparison.Ordinal));
    }
}
