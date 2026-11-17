using System.Globalization;
using System.Text;
using System.Text.Json;
using Rodaje.Playback.Composition;
using Xunit.Abstractions;

namespace Rodaje.Text.Tests;

/// <summary>
/// La maquetación de HarfBuzz frente a la de DirectWrite en los casos de aceptación de títulos (D-066, fase 5): mismas
/// unidades, líneas y palabras, y cada borde a ±1 px. La referencia la escribe Rodaje.Render.Tests en Windows. Con respaldo
/// por letra la fuente de respaldo es otra en cada sistema: desde la primera letra de respaldo de una línea solo se comparan
/// la línea y la palabra.
/// </summary>
public sealed class TitleLayoutAcceptanceTests(ITestOutputHelper output) : IDisposable
{
    private const float Tolerance = 1f;

    private readonly TitleFontSet _fonts = new();

    public static TheoryData<string> Cases() => [.. TitleLayoutCases.All.Select(c => c.Name)];

    public void Dispose() => _fonts.Dispose();

    [Theory]
    [MemberData(nameof(Cases))]
    public void HarfBuzz_lays_out_the_case_like_DirectWrite(string name)
    {
        TitleLayoutCase @case = TitleLayoutCases.All.Single(c => c.Name == name);
        string root = TitleLayoutCases.RepositoryRoot();
        Assert.Empty(_fonts.Register(TitleLayoutCases.FontFiles.Select(f => Path.Combine(root, f))));
        var content = new GeneratedContent(GeneratorKinds.Text, @case.Text, @case.Style(), @case.BoxWidth, 1920, 1080, 1);

        TitleLayout layout = TitleLayout.Create(content, _fonts);
        LaidOutTitle actual = LaidOutTitle.Rounded(name, "HarfBuzz", TitleUnits.Segment(layout, @case.Unit)
            .Select(u => new LaidOutUnit(u.Start, u.End, u.Line, u.Word, u.Left, u.Right, u.Top, u.Bottom)));
        output.WriteLine(JsonSerializer.Serialize(actual, TitleLayoutCases.Json));

        string golden = TitleLayoutCases.GoldenPath(root, name);
        Assert.True(File.Exists(golden), $"Falta la referencia de DirectWrite {golden}: la escribe Rodaje.Render.Tests en Windows.");
        LaidOutTitle expected = JsonSerializer.Deserialize<LaidOutTitle>(File.ReadAllText(golden), TitleLayoutCases.Json)!;
        ShapingFont primary = _fonts.Resolve(@case.Style().Font).Font;

        Assert.Equal(expected.Units.Count, actual.Units.Count);
        Assert.Equal(expected.Units.Select(u => (u.Start, u.End, u.Line, u.Word)), actual.Units.Select(u => (u.Start, u.End, u.Line, u.Word)));
        var fellBack = new HashSet<int>();
        float worst = 0;
        for (int i = 0; i < expected.Units.Count; i++)
        {
            LaidOutUnit e = expected.Units[i];
            LaidOutUnit a = actual.Units[i];
            if (@case.FallsBackPerGlyph && (fellBack.Contains(e.Line) || !Covered(primary, layout.Drawn, e.Start, e.End)))
            {
                fellBack.Add(e.Line);
                continue;
            }

            worst = Math.Max(worst, Math.Max(Math.Abs(e.Left - a.Left), Math.Abs(e.Right - a.Right)));
            Assert.True(Math.Abs(e.Left - a.Left) <= Tolerance && Math.Abs(e.Right - a.Right) <= Tolerance,
                $"Unidad {i} («{layout.Drawn[e.Start..e.End]}»): DirectWrite {e.Left}..{e.Right}, HarfBuzz {a.Left}..{a.Right}.");
            Assert.True(Math.Abs(e.Top - a.Top) <= 0.5f && Math.Abs(e.Bottom - a.Bottom) <= 0.5f,
                $"Unidad {i}: DirectWrite {e.Top}..{e.Bottom} en vertical, HarfBuzz {a.Top}..{a.Bottom}.");
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{name}: {actual.Units.Count} unidades en {actual.Units.Select(u => u.Line).Distinct().Count()} líneas; mayor diferencia horizontal {worst:0.00} px."));
    }

    private static bool Covered(ShapingFont font, string text, int start, int end)
    {
        foreach (Rune rune in text[start..end].EnumerateRunes())
        {
            if (!font.Has(rune.Value) && !Rune.IsWhiteSpace(rune))
            {
                return false;
            }
        }

        return true;
    }
}
