using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Caza;

/// <summary>
/// Lo que promete «Por palabra» de <c>docs/guia/titulos-y-subtitulos.md</c>, comprobado en <see cref="SubtitleEdits"/> y sus
/// comandos. Cada prueba cita la frase.
/// </summary>
public sealed class SubtitleWordsContractTests
{
    private static Time S(double seconds) => Time.FromSeconds(seconds);

    /// <summary>Un subtítulo automático en v2 que empieza en <paramref name="start"/>, con palabras de 0,4 s en su origen.</summary>
    private static TrimFixture Subtitle(string text, double start = 2, double trim = 0, string id = "s1")
    {
        TrimFixture f = new TrimFixture().AddTitle("v2", id, 0, 0, 0);
        Clip clip = f.Clip(id);
        string[] parts = text.Split(' ', '\n');
        clip.Start = S(start);
        clip.SourceIn = S(trim);
        clip.SourceOut = S(parts.Length * 0.4);
        clip.Generator!.Text = text;
        clip.Generator.Words = [.. parts.Select((p, i) => new GeneratorWord { Start = S(i * 0.4), End = S((i + 1) * 0.4), Text = p })];
        f.AssertValid();
        return f;
    }

    /// <summary>El instante de la línea de tiempo al que lleva la hora de cada palabra, de todos los clips de v2.</summary>
    private static List<(string Text, Time At)> Instants(TrimFixture f) =>
        [.. f.Track("v2").Clips.OrderBy(c => c.Start)
            .SelectMany(c => c.Generator!.Words!.Select(w => (w.Text, c.Start + (w.Start - c.SourceIn))))];

    // «Cada subtítulo automático guarda sus palabras con su hora.» «Cada palabra es editable: corrígela y pulsa Intro.»
    [Fact]
    public void Corregir_una_palabra_cambia_la_palabra_y_el_texto_en_un_paso()
    {
        TrimFixture f = Subtitle("Hola a todos.");
        var stack = new UndoStack(f.Project);
        stack.Execute(Edits.CorrectSubtitleWord("seq", "s1", 2, "todas."));

        ClipGenerator g = f.Clip("s1").Generator!;
        Assert.Equal("Hola a todas.", g.Text);
        Assert.Equal(["Hola", "a", "todas."], g.Words!.Select(w => w.Text));
        Assert.Equal((S(0.8), S(1.2)), (g.Words![2].Start, g.Words[2].End));

        stack.Undo();
        Assert.Equal("Hola a todos.", f.Clip("s1").Generator!.Text);
        Assert.Equal("todos.", f.Clip("s1").Generator!.Words![2].Text);
        f.AssertValid();
    }

    // «La tijera Dividir aquí parte el subtítulo en esa palabra»
    [Fact]
    public void Dividir_aqui_empieza_el_subtitulo_nuevo_en_esa_palabra()
    {
        TrimFixture f = Subtitle("Hola a todos. Bienvenidos al canal");
        var stack = new UndoStack(f.Project);
        var split = Edits.SplitSubtitleAtWord("seq", "s1", 3);
        stack.Execute(split);

        Clip left = f.Clip("s1"), right = f.Clip(split.CreatedIds[0]);
        Assert.Equal(S(2) + S(1.2), right.Start);
        Assert.Equal(left.End, right.Start);
        Assert.Equal(["Hola", "a", "todos."], left.Generator!.Words!.Select(w => w.Text));
        Assert.Equal(["Bienvenidos", "al", "canal"], right.Generator!.Words!.Select(w => w.Text));
        Assert.StartsWith("Bienvenidos", right.Generator.Text, StringComparison.Ordinal);
        Assert.Equal("Hola a todos.", left.Generator.Text);

        stack.Undo();
        Assert.Single(f.Track("v2").Clips);
        AssertFails(() => SubtitleEdits.SplitAtWord(f.Ctx, "s1", 0), "no sea la primera");
        f.AssertValid();
    }

    // «… Unir con el siguiente los junta.»
    [Fact]
    public void Unir_con_el_siguiente_deshace_la_division()
    {
        TrimFixture f = Subtitle("Hola a todos. Bienvenidos al canal");
        Clip original = f.Clip("s1");
        (Time start, Time end, string text) = (original.Start, original.End, original.Generator!.Text);
        List<(string, Time)> before = Instants(f);

        SubtitleEdits.SplitAtWord(f.Ctx, "s1", 2);
        var stack = new UndoStack(f.Project);
        stack.Execute(Edits.JoinSubtitleWithNext("seq", "s1"));

        Clip joined = Assert.Single(f.Track("v2").Clips);
        Assert.Equal((start, end, text), (joined.Start, joined.End, joined.Generator!.Text));
        Assert.Equal(before, Instants(f));

        stack.Undo();
        Assert.Equal(2, f.Track("v2").Clips.Count);
        f.AssertValid();
    }

    // «… Unir con el siguiente los junta.» Con hueco entre los dos, el primero crece hasta el final del segundo.
    [Fact]
    public void Unir_con_el_siguiente_separado_junta_palabras_y_texto()
    {
        TrimFixture f = Subtitle("Uno dos");
        Clip other = Model.ModelSamples.MakeTitle("s2", 5, 1, 2, "tres cuatro");
        other.Generator!.Words =
        [
            new GeneratorWord { Start = S(1), End = S(1.5), Text = "tres" },
            new GeneratorWord { Start = S(1.5), End = S(2), Text = "cuatro" },
        ];
        f.Track("v2").Clips.Add(other);
        f.AssertValid();
        List<(string, Time)> before = Instants(f);

        SubtitleEdits.JoinWithNext(f.Ctx, "s1");

        Clip clip = Assert.Single(f.Track("v2").Clips);
        Assert.Equal((S(2), S(6)), (clip.Start, clip.End));
        Assert.Equal("Uno dos tres cuatro", clip.Generator!.Text);
        Assert.Equal(before, Instants(f));
        f.AssertValid();
    }

    // «La hora de cada palabra lleva el cursor a ese instante.» Dividir no mueve ninguna palabra en la línea de tiempo,
    // tampoco en un subtítulo recortado por la izquierda.
    [Theory]
    [InlineData(0.0, 2)]
    [InlineData(0.4, 3)]
    public void Dividir_deja_cada_palabra_en_su_instante(double trim, int index)
    {
        TrimFixture f = Subtitle("uno dos tres cuatro cinco", trim: trim);
        List<(string Text, Time At)> before = Instants(f);
        string right = SubtitleEdits.SplitAtWord(f.Ctx, "s1", index);

        Assert.Equal(before, Instants(f));
        Assert.Equal(before.Single(w => w.Text == f.Clip(right).Generator!.Words![0].Text).At, f.Clip(right).Start);
        f.AssertValid();
    }

    // «La hora de cada palabra lleva el cursor a ese instante.» Corregir una palabra no mueve su hora.
    [Fact]
    public void Corregir_deja_la_hora_de_la_palabra()
    {
        TrimFixture f = Subtitle("Hola a todos");
        List<(string Text, Time At)> before = Instants(f);
        SubtitleEdits.CorrectWord(f.Ctx, "s1", 1, "a ustedes");

        List<(string Text, Time At)> after = Instants(f);
        Assert.Equal(before[1].At, after[1].At);
        Assert.Equal(before[2].At, after[3].At);
    }
}
