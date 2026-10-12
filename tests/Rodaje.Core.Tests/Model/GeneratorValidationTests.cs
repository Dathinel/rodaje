using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Model.ModelSamples;

namespace Rodaje.Core.Tests.Model;

/// <summary>Reglas del validador para los clips generadores y los roles de pista (formato 5).</summary>
public class GeneratorValidationTests
{
    /// <summary>El proyecto de siempre con un título en V2 (de 10 a 12 s, con el punto de entrada en −1 s).</summary>
    private static Project WithTitle(out Clip title)
    {
        Project p = ValidProject();
        title = MakeTitle("t1", 10, -1, 1);
        p.Sequences[0].VideoTracks[1].Clips.Add(title);
        return p;
    }

    private static void AssertError(Action<Clip> breakIt, string expected)
    {
        Project p = WithTitle(out Clip title);
        breakIt(title);
        IReadOnlyList<string> errors = ModelValidator.Validate(p);
        Assert.Contains(errors, e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void ValidTitle_WithNegativeSourceIn_Passes()
    {
        Project p = WithTitle(out Clip title);
        title.Generator!.Box = new TextFrame { Width = 1600, WidthFraction = 1 };
        title.Generator.Text = "Línea 1\n\tLínea 2 ‏שלום‎ ‫x‬ 😀";
        p.Sequences[0].VideoTracks[1].Role = TrackRoles.Subtitles;

        Assert.Empty(ModelValidator.Validate(p));
        Assert.Equal(Sec(-1), title.SourceIn);
        Assert.Equal(Sec(2), title.Duration);
    }

    [Fact]
    public void MediaClip_StillRejectsNegativeSourceIn()
    {
        Project p = ValidProject();
        p.Sequences[0].VideoTracks[1].Clips[0].SourceIn = Sec(-1);
        Assert.Contains(ModelValidator.Validate(p), e => e.Contains("punto de entrada negativo", StringComparison.Ordinal));
    }

    [Fact]
    public void MediaAndGenerator_Together_OrNeither_AreRejected()
    {
        AssertError(c => c.MediaId = "m1", "a la vez un medio y un generador");
        AssertError(c => c.Generator = null, "usa el medio «», que no existe");
    }

    [Fact]
    public void Generator_IsNotChecked_AgainstMediaDurationOrStreams()
    {
        Project p = WithTitle(out Clip title);
        title.SourceOut = Sec(10_000);
        Assert.Empty(ModelValidator.Validate(p));
    }

    [Fact]
    public void Generator_OnAudioTrack_IsRejected()
    {
        Project p = ValidProject();
        p.Sequences[0].AudioTracks[0].Clips.Add(MakeTitle("t1", 40, 0, 1));
        Assert.Contains(ModelValidator.Validate(p), e => e.Contains("es un generador y está en una pista de audio", StringComparison.Ordinal));
    }

    [Fact]
    public void Generator_WithSpeed_IsRejected()
    {
        AssertError(
            c =>
            {
                c.Speed = new ClipSpeed();
                c.Duration = c.SourceDuration;
            },
            "es un generador y tiene velocidad");
    }

    [Theory]
    [InlineData("\r")]
    [InlineData("\0")]
    [InlineData("\u001b[31m")]
    [InlineData("\u007f")]
    [InlineData("\u0085")]
    public void Text_RejectsControlCharacters_OtherThanNewlineAndTab(string control) =>
        AssertError(c => c.Generator!.Text = "a" + control + "b", "caracteres de control");

    [Fact]
    public void Text_Limits()
    {
        Project ok = WithTitle(out Clip title);
        title.Generator!.Text = new string('a', ModelValidator.MaxTextLength - (ModelValidator.MaxTextLines - 1)) + new string('\n', ModelValidator.MaxTextLines - 1);
        Assert.Equal(ModelValidator.MaxTextLength, title.Generator.Text.Length);
        Assert.Empty(ModelValidator.Validate(ok));

        AssertError(c => c.Generator!.Text = new string('a', ModelValidator.MaxTextLength + 1), $"más de {ModelValidator.MaxTextLength} caracteres");
        AssertError(c => c.Generator!.Text = new string('\n', ModelValidator.MaxTextLines), $"más de {ModelValidator.MaxTextLines} líneas");
        AssertError(
            c => c.Generator!.Text = new string('\u2028', ModelValidator.MaxTextLines / 2) + new string('\u2029', ModelValidator.MaxTextLines / 2),
            $"más de {ModelValidator.MaxTextLines} líneas");
    }

    [Fact]
    public void ProjectTotalText_IsCapped()
    {
        Project p = ValidProject();
        Track track = p.Sequences[0].VideoTracks[1];
        int count = (ModelValidator.MaxProjectTextLength / ModelValidator.MaxTextLength) + 1;
        for (int i = 0; i < count; i++)
        {
            Clip title = MakeTitle($"t{i}", 100 + i, 0, 1, new string('a', ModelValidator.MaxTextLength));
            track.Clips.Add(title);
        }

        string error = Assert.Single(ModelValidator.Validate(p));
        Assert.Contains($"el máximo es {ModelValidator.MaxProjectTextLength}", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Text")]
    [InlineData("a b")]
    [InlineData("tipo/raro")]
    public void Kind_MustBeAToken(string kind) => AssertError(c => c.Generator!.Kind = kind, "tipo de generador no válido");

    [Fact]
    public void UnknownKind_WithoutStyle_IsKept_ButTextNeedsAStyle()
    {
        Project p = WithTitle(out Clip title);
        title.Generator!.Kind = "rodaje.solid-2";
        title.Generator.Style = null;
        Assert.Empty(ModelValidator.Validate(p));

        AssertError(c => c.Generator!.Style = null, "es un texto sin estilo");
    }

    public static TheoryData<Action<TextStyle>, string> BrokenStyles => new()
    {
        { s => s.Source = "clasico", "un origen" },
        { s => s.Name = "a\nb", "un nombre" },
        { s => s.Name = new string('n', 129), "un nombre" },
        { s => s.Font.Family = " ", "familia" },
        { s => s.Font.Family = "In\tter", "familia" },
        { s => s.Font.Weight = 99, "peso" },
        { s => s.Font.Weight = 901, "peso" },
        { s => s.Font.Source = "inter", "una fuente con un origen" },
        { s => s.Font.Fallback = [.. Enumerable.Repeat("Arial", ModelValidator.MaxFallbackFonts + 1)], "respaldo" },
        { s => s.Font.Fallback = [""], "respaldo" },
        { s => s.Size = 0, "tamaño" },
        { s => s.Size = 1000.5, "tamaño" },
        { s => s.Size = double.NaN, "tamaño" },
        { s => s.Reference.Width = 0, "referencia" },
        { s => s.Reference.Height = 16_385, "referencia" },
        { s => s.LineHeight = 0, "interlineado" },
        { s => s.LineHeight = double.PositiveInfinity, "interlineado" },
        { s => s.LetterSpacing = -1001, "espaciado" },
        { s => s.Align = (TextAlign)7, "alineación" },
        { s => s.Fill.Color = "#FFF", "relleno" },
        { s => s.Fill.Color = "FFFFFF", "relleno" },
        { s => s.Fill.Color = "#GGGGGG", "relleno" },
        { s => s.Stroke!.Width = 48.5, "mitad del tamaño" },
        { s => s.Stroke!.Width = -1, "mitad del tamaño" },
        { s => s.Stroke!.Join = (StrokeJoin)9, "un borde con color" },
        { s => s.Stroke!.Color = "#12345", "un borde con color" },
        { s => s.Shadow!.Blur = 65, "sombra" },
        { s => s.Shadow!.Dx = 1e9, "sombra" },
        { s => s.Shadow!.Color = "", "sombra" },
        { s => s.Background!.Padding = [1], "fondo" },
        { s => s.Background!.Padding = [1, -1], "fondo" },
        { s => s.Background!.Radius = double.NaN, "fondo" },
        { s => s.In!.DurationSeconds = 61, "entrada con una duración" },
        { s => s.Out!.DurationSeconds = -0.1, "salida con una duración" },
        { s => s.In!.Curves["brillo"] = [new StyleKey(0, 1)], "anima «brillo»" },
        { s => s.In!.Curves["x"] = [], "curva «x» vacía" },
        { s => s.In!.Curves["x"] = [new StyleKey(0, 1), new StyleKey(0, 2)], "curva «x»" },
        { s => s.In!.Curves["x"] = [new StyleKey(0.5, 1)], "curva «x»" },
        { s => s.In!.Curves["opacity"] = [new StyleKey(0, 2)], "curva «opacity»" },
        { s => s.In!.Curves["scale"] = [new StyleKey(0, double.NaN)], "curva «scale»" },
        { s => s.In!.Curves["y"] = [new StyleKey(0, 1, (Interpolation)42)], "curva «y»" },
        { s => s.Out!.Curves["x"] = [.. Enumerable.Range(0, ModelValidator.MaxStyleKeys + 1).Select(i => new StyleKey(i * 0.001, 0))], "curva «x»" },
    };

    [Theory]
    [MemberData(nameof(BrokenStyles))]
    public void Style_OutOfRange_IsRejected(Action<TextStyle> breakIt, string expected) =>
        AssertError(c => breakIt(c.Generator!.Style!), expected);

    [Fact]
    public void Style_Boundaries_Pass()
    {
        Project p = WithTitle(out Clip title);
        TextStyle s = title.Generator!.Style!;
        s.Size = 1000;
        s.Stroke!.Width = 500;
        s.Shadow!.Blur = 64;
        s.Font.Weight = 900;
        s.Font.Fallback = [.. Enumerable.Repeat("Arial", ModelValidator.MaxFallbackFonts)];
        s.Fill.Color = "#aBcDeF12";
        s.In!.DurationSeconds = 60;
        s.In.Curves["x"] = [.. Enumerable.Range(0, ModelValidator.MaxStyleKeys).Select(i => new StyleKey(i * 0.5, -1000))];
        s.Out!.DurationSeconds = 0;
        s.Out.Curves.Clear();
        s.Out.Curves["opacity"] = [new StyleKey(0, 0)];

        Assert.Empty(ModelValidator.Validate(p));
    }

    [Theory]
    [InlineData(0d, null)]
    [InlineData(-5d, null)]
    [InlineData(16_385d, null)]
    [InlineData(null, 0d)]
    [InlineData(null, 1.01)]
    [InlineData(null, double.NaN)]
    public void Box_OutOfRange_IsRejected(double? width, double? fraction) =>
        AssertError(c => c.Generator!.Box = new TextFrame { Width = width, WidthFraction = fraction }, "caja de texto");

    [Theory]
    [InlineData("Subtitles")]
    [InlineData("")]
    [InlineData("sub titles")]
    [InlineData("a.b")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Role_MustBeAToken(string role)
    {
        Project p = ValidProject();
        p.Sequences[0].VideoTracks[0].Role = role;
        Assert.Equal(["La pista «V1» tiene un rol no válido."], ModelValidator.Validate(p));
    }

    [Fact]
    public void Role_UnknownIsKept_SubtitlesOnlyOnVideo()
    {
        Project p = ValidProject();
        p.Sequences[0].VideoTracks[0].Role = "karaoke-2";
        Assert.Empty(ModelValidator.Validate(p));

        p.Sequences[0].AudioTracks[0].Role = TrackRoles.Subtitles;
        Assert.Equal(["La pista «A1» tiene el rol de subtítulos y no es de video."], ModelValidator.Validate(p));
    }

    [Fact]
    public void ExtremeNegativeSourceIn_IsReportedNotThrown()
    {
        Project p = WithTitle(out Clip title);
        title.SourceIn = Time.FromTicks(long.MinValue);
        IReadOnlyList<string> errors = ModelValidator.Validate(p);
        Assert.Contains(errors, e => e.Contains("fuera de rango", StringComparison.Ordinal));
    }
}
