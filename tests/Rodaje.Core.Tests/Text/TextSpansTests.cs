using Rodaje.Core.Model;
using Rodaje.Core.Text;

namespace Rodaje.Core.Tests.Text;

/// <summary>Tramos con formato: aplicar, unir, mover con el texto al escribir o borrar, y mayúsculas sin cambiar el largo.</summary>
public class TextSpansTests
{
    private static TextSpan Bold(int start, int length) => new() { Start = start, Length = length, Weight = 700 };

    [Fact]
    public void Applying_a_format_splits_and_merges_neighbours()
    {
        // «Hola mundo»: «mundo» en negrita, luego «Hola » también: queda un solo tramo.
        List<TextSpan>? spans = TextSpans.Apply(null, 10, 5, 5, s => s with { Weight = 700 });
        Assert.Equal([Bold(5, 5)], spans!);
        spans = TextSpans.Apply(spans, 10, 0, 5, s => s with { Weight = 700 });
        Assert.Equal([Bold(0, 10)], spans!);

        // Color en «la m»: parte la negrita en tres trozos con el mismo peso.
        spans = TextSpans.Apply(spans, 10, 2, 4, s => s with { Color = "#FF0000" });
        Assert.Equal(3, spans!.Count);
        Assert.Equal([0, 2, 6], spans.Select(s => s.Start));
        Assert.All(spans, s => Assert.Equal(700, s.Weight));
        Assert.Equal("#FF0000", spans[1].Color);

        // Quitar todo el formato deja la lista nula.
        Assert.Null(TextSpans.Apply(spans, 10, 0, 10, _ => new TextSpan()));
    }

    [Fact]
    public void Typing_at_the_end_of_a_span_continues_it_but_not_just_before_it()
    {
        List<TextSpan> spans = [Bold(5, 5)];
        Assert.Equal([Bold(5, 6)], TextSpans.Remap("Hola mundo", "Hola mundos", spans)!);
        Assert.Equal([Bold(6, 5)], TextSpans.Remap("Hola mundo", "Hola  mundo", spans)!);
        Assert.Equal([Bold(5, 7)], TextSpans.Remap("Hola mundo", "Hola munXXdo", spans)!);
        Assert.Equal([Bold(3, 5)], TextSpans.Remap("Hola mundo", "Hoamundo", spans)!);

    }

    [Fact]
    public void Deleting_the_text_of_a_span_removes_it()
    {
        List<TextSpan> spans = [Bold(0, 4), Bold(5, 5) with { Color = "#00FF00" }];
        List<TextSpan>? left = TextSpans.Remap("Hola mundo", "Hola ", spans);
        Assert.Equal([Bold(0, 4)], left!);
        Assert.Null(TextSpans.Remap("Hola mundo", "", spans));
    }

    [Fact]
    public void Replacing_a_selection_keeps_the_format_of_its_first_character()
    {
        List<TextSpan> spans = [Bold(5, 5)];
        Assert.Equal([Bold(5, 3)], TextSpans.Remap("Hola mundo", "Hola Ana", spans)!);
    }

    [Fact]
    public void Normalize_clamps_drops_plain_and_trims_overlaps()
    {
        List<TextSpan>? spans = TextSpans.Normalize(
            [Bold(8, 10), new TextSpan { Start = 0, Length = 3 }, Bold(2, 4), Bold(4, 4) with { Scale = 50 }], 12);
        Assert.Equal(3, spans!.Count);
        Assert.Equal((2, 2), (spans[0].Start, spans[0].Length));
        Assert.Equal((4, 4, 10.0), (spans[1].Start, spans[1].Length, spans[1].Scale!.Value));
        Assert.Equal((8, 4), (spans[2].Start, spans[2].Length));
    }

    [Fact]
    public void Slice_returns_the_spans_of_a_piece_relative_to_it()
    {
        List<TextSpan> spans = [Bold(2, 6)];
        Assert.Equal([Bold(0, 3)], TextSpans.Slice(spans, 5, 10)!);
        Assert.Null(TextSpans.Slice(spans, 8, 10));
    }

    [Theory]
    [InlineData("Ñandú ágil ß", TextCase.Upper, "ÑANDÚ ÁGIL ß")]
    [InlineData("ÑANDÚ Ágil", TextCase.Lower, "ñandú ágil")]
    [InlineData("Hola 😀", TextCase.Upper, "HOLA 😀")]
    public void Casing_keeps_the_length_so_positions_stay_valid(string text, TextCase textCase, string expected)
    {
        string drawn = TextCasing.Apply(text, textCase);
        Assert.Equal(expected, drawn);
        Assert.Equal(text.Length, drawn.Length);
    }

    [Fact]
    public void Small_caps_marks_the_lowercase_letters_it_raised()
    {
        bool[] lowered = new bool[6];
        Assert.Equal("HOLA Á", TextCasing.Apply("Hola á", TextCase.SmallCaps, lowered));
        Assert.Equal([false, true, true, true, false, true], lowered);
    }
}
