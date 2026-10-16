using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Text;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Model.ModelSamples;

namespace Rodaje.Core.Tests.Text;

/// <summary>Contrato de la sección «Karaoke» de <c>docs/guia/titulos-y-subtitulos.md</c>, en lo que decide Core.</summary>
public class KaraokeContractTests
{
    private static Time S(double seconds) => Time.FromSeconds(seconds);

    private static List<GeneratorWord> Words(params string[] parts) =>
        [.. parts.Select((p, i) => new GeneratorWord { Start = S(i * 0.5), End = S((i + 1) * 0.5), Text = p })];

    // «Resaltar la palabra que suena pinta cada palabra en el color de «Resaltado» mientras suena.»
    [Fact]
    public void Each_word_is_the_active_one_while_it_sounds()
    {
        List<GeneratorWord> words = Words("Hola", "a", "todos");
        for (int i = 0; i < words.Count; i++)
        {
            Time start = words[i].Start;
            Time end = words[i].End;
            Assert.Equal(i, SubtitleEdits.ActiveWord(words, start));
            Assert.Equal(i, SubtitleEdits.ActiveWord(words, Time.FromTicks((start.Ticks + end.Ticks) / 2)));
            Assert.Equal(i, SubtitleEdits.ActiveWord(words, Time.FromTicks(end.Ticks - 1)));
        }
    }

    // «Resaltar la palabra que suena pinta cada palabra en el color de «Resaltado» mientras suena.»
    [Fact]
    public void Each_word_has_its_own_stretch_of_text_to_paint()
    {
        const string text = "¿Hola, a\ntodos?";
        List<GeneratorWord> words = Words("¿Hola,", "a", "todos?");
        IReadOnlyList<WordRange> ranges = SubtitleEdits.WordRanges(text, words);

        Assert.Equal(words.Count, ranges.Count);
        for (int i = 0; i < words.Count; i++)
        {
            Assert.True(ranges[i].Found);
            Assert.Equal(words[i].Text, text.Substring(ranges[i].Start, ranges[i].Length));
        }
    }

    // «Resaltar la palabra que suena pinta cada palabra en el color de «Resaltado» mientras suena.»
    [Theory]
    [InlineData(TextCase.Upper)]
    [InlineData(TextCase.Lower)]
    [InlineData(TextCase.SmallCaps)]
    public void The_word_to_paint_stays_in_place_with_drawn_capitals(TextCase textCase)
    {
        const string text = "Ñandú straße über";
        IReadOnlyList<WordRange> ranges = SubtitleEdits.WordRanges(text, Words("Ñandú", "straße", "über"));
        string drawn = TextCasing.Apply(text, textCase);

        Assert.Equal(text.Length, drawn.Length);
        foreach (WordRange range in ranges)
        {
            string word = text.Substring(range.Start, range.Length);
            Assert.Equal(TextCasing.Apply(word, textCase), drawn.Substring(range.Start, range.Length));
        }
    }

    // «pinta cada palabra en el color de «Resaltado»»
    [Fact]
    public void The_default_highlight_color_is_a_valid_color()
    {
        var highlight = new StyleHighlight();
        Assert.Equal(HighlightMode.Word, highlight.Mode);
        Assert.Empty(ModelValidator.Validate(Project(highlight)));
    }

    // «Con Modo › Barrido de color, el color avanza dentro de la palabra al ritmo de la voz y lo dicho queda pintado, como en un karaoke.»
    [Fact]
    public void The_color_sweep_mode_is_kept_when_saved_and_rejected_when_unknown()
    {
        var highlight = new StyleHighlight { Mode = HighlightMode.Sweep };
        Project project = Project(highlight);
        Assert.Empty(ModelValidator.Validate(project));

        Project back = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(project));
        Assert.Equal(HighlightMode.Sweep, back.Sequences[0].VideoTracks[1].Clips.Single(c => c.Id == "t1").Generator!.Style!.Highlight!.Mode);

        highlight.Mode = (HighlightMode)7;
        Assert.Contains(ModelValidator.Validate(project), e => e.Contains("palabra resaltada", StringComparison.Ordinal));
    }

    private static Project Project(StyleHighlight highlight)
    {
        Project p = ValidProject();
        Clip title = MakeTitle("t1", 10, -1, 1);
        title.Generator!.Text = "Hola a todos";
        title.Generator.Words = Words("Hola", "a", "todos");
        title.Generator.Style = new TextStyle { Highlight = highlight };
        p.Sequences[0].VideoTracks[1].Clips.Add(title);
        return p;
    }
}
