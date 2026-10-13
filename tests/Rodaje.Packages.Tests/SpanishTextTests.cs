using System.Globalization;
using System.Text;

namespace Rodaje.Packages.Tests;

/// <summary>
/// Tildes y orden alfabético sin ICU (la app publica con <c>InvariantGlobalization</c>, donde <c>Normalize</c> no separa
/// las tildes y la cultura actual ordena por código). Aquí sí hay ICU: sirve de referencia para la tabla.
/// </summary>
public class SpanishTextTests
{
    [Fact]
    public void Fold_CoincideConLaDescomposicionCanonica_EnLatin1YLatinExtendidoA()
    {
        var wrong = new List<string>();
        for (char c = 'À'; c <= 'ſ'; c++)
        {
            string decomposed = c.ToString().Normalize(NormalizationForm.FormD);
            bool letterWithMarks = decomposed.Length > 1 && char.IsAsciiLetter(decomposed[0])
                && decomposed.Skip(1).All(m => CharUnicodeInfo.GetUnicodeCategory(m) == UnicodeCategory.NonSpacingMark);
            char expected = letterWithMarks ? decomposed[0] : c;
            if (letterWithMarks && SpanishText.Fold(c) != expected)
            {
                wrong.Add($"U+{(int)c:X4} {c} da {SpanishText.Fold(c)}, se esperaba {expected}");
            }
            else if (!letterWithMarks && SpanishText.Fold(c) != c && !char.IsAsciiLetter(SpanishText.Fold(c)))
            {
                wrong.Add($"U+{(int)c:X4} {c} da {SpanishText.Fold(c)}");
            }
        }

        Assert.Empty(wrong);
    }

    [Theory]
    [InlineData("Música épica", "Musica epica")]
    [InlineData("ÑANDÚ, pingüino y cigüeña", "NANDU, pinguino y ciguena")]
    [InlineData("Øresund Łódź", "Oresund Lodz")]
    [InlineData("日本語 😀 ﬁ", "日本語 😀 ﬁ")]
    public void Fold_QuitaLasTildesSinCambiarElLargo(string text, string expected)
    {
        Assert.Equal(expected, SpanishText.Fold(text));
    }

    [Fact]
    public void Comparer_OrdenaComoUnDiccionarioEspanol()
    {
        string[] names = ["Zeta", "ñu", "Árbol", "oso", "nube", "Éxito", "abeja", "Ñandú", "Ola", "estrella", "nz"];

        string[] sorted = [.. names.Order(SpanishText.Comparer)];

        Assert.Equal(["abeja", "Árbol", "estrella", "Éxito", "nube", "nz", "Ñandú", "ñu", "Ola", "oso", "Zeta"], sorted);
    }

    [Fact]
    public void Comparer_DesempataPorTildesYMayusculas()
    {
        Assert.True(SpanishText.Comparer.Compare("papa", "papá") < 0);
        Assert.True(SpanishText.Comparer.Compare("papá", "Papa") > 0);
        Assert.True(SpanishText.Comparer.Compare("papa", "Papa") < 0);
        Assert.Equal(0, SpanishText.Comparer.Compare("Ñu", "Ñu"));
        Assert.True(SpanishText.Comparer.Compare(null, "a") < 0);
    }

    [Fact]
    public void Scan_OrdenaLosPaquetesAlfabeticamenteEnEspanol()
    {
        using var folder = new TempFolder();
        var locations = PackageLocations.Under(folder.Combine("datos"));
        string[] names = ["Zeta", "Ñandú", "nube", "Árbol"];
        for (int i = 0; i < names.Length; i++)
        {
            string directory = Path.Combine(locations.Packages, $"p{i}");
            Directory.CreateDirectory(Path.Combine(directory, "transiciones"));
            File.WriteAllText(Path.Combine(directory, "transiciones", "deslizar.json"), "{}");
            File.WriteAllText(Path.Combine(directory, "paquete.json"), Samples.Manifest($"p{i}").Replace("Transiciones de prueba", names[i], StringComparison.Ordinal));
        }

        PackageScan scan = PackageLibrary.Scan(locations, "rodaje");

        Assert.Empty(scan.Problems);
        Assert.Equal(["Árbol", "nube", "Ñandú", "Zeta"], scan.Packages.Select(p => p.Manifest.Name));
    }
}
