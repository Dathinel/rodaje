using System.Text.RegularExpressions;

namespace Rodaje.Core.Tests.Architecture;

/// <summary>
/// Todo <c>Parse</c> o <c>TryParse</c> de números, tiempos y fechas en <c>src</c> y <c>modules</c> lleva proveedor
/// (<c>CultureInfo.InvariantCulture</c>, <c>NumberFormatInfo</c>…). Las apps publican con <c>InvariantGlobalization</c>,
/// pero las bibliotecas también las usan las pruebas, Subtitular y Rastrear con la cultura de Windows: en es-CO,
/// <c>double.Parse("29.97")</c> da 2997 y un <c>.rodaje</c> o un argumento de FFmpeg leídos así cambian de valor.
/// </summary>
public sealed partial class CultureParsingTests
{
    [GeneratedRegex(@"\b(?:double|float|decimal|Half|int|long|short|uint|ulong|ushort|byte|sbyte|TimeSpan|DateTime|DateTimeOffset)\.(?:Try)?Parse(?:Exact)?\(")]
    private static partial Regex NumericParse();

    [GeneratedRegex(@"Culture|NumberFormatInfo|DateTimeFormatInfo|provider|SpanishNumbers\.Format")]
    private static partial Regex Provider();

    [Fact]
    public void Los_parse_de_numeros_y_fechas_llevan_cultura()
    {
        string root = RepositoryRoot();
        var found = new List<string>();
        int scanned = 0;
        foreach (string file in new[] { "src", "modules" }.SelectMany(f => Directory.EnumerateFiles(Path.Combine(root, f), "*.cs", SearchOption.AllDirectories)))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            scanned++;
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (IsOffending(lines, i))
                {
                    found.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(scanned > 500, "la vigilancia no lee los fuentes");
        Assert.Empty(found);
    }

    /// <summary>
    /// Lo que hace que la región de Windows (coma decimal, la i turca, cifras arábigas) no cambie nada en la app ni en los
    /// módulos: sin esto, cualquier ToString o ToLower sin cultura del código pasaría a depender del país.
    /// </summary>
    [Fact]
    public void Cada_ejecutable_publica_con_InvariantGlobalization()
    {
        string root = RepositoryRoot();
        List<string> exes = [.. new[] { "src", "modules" }
            .SelectMany(f => Directory.EnumerateFiles(Path.Combine(root, f), "*.csproj", SearchOption.AllDirectories))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), "<OutputType>(Win)?Exe</OutputType>"))];
        Assert.True(exes.Count >= 9, "la vigilancia no encuentra la app, el lanzador y los módulos");
        Assert.Empty(exes.Where(f => !File.ReadAllText(f).Contains("<InvariantGlobalization>true</InvariantGlobalization>", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f)));
    }

    [Fact]
    public void La_vigilancia_detecta_los_parse_sin_cultura()
    {
        Assert.True(IsOffending(["double v = double.Parse(text);"], 0));
        Assert.True(IsOffending(["if (int.TryParse(s, out int n))"], 0));
        Assert.True(IsOffending(["var t = TimeSpan.ParseExact(s, \"c\", null);"], 0));
        Assert.False(IsOffending(["double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)"], 0));
        Assert.False(IsOffending(["double start = double.TryParse(x,", "    NumberStyles.Float, CultureInfo.InvariantCulture, out double s);"], 0));
        Assert.False(IsOffending(["// double.Parse(text) daría 2997 en es-CO"], 0));
        Assert.False(IsOffending(["Enum.TryParse(text, out Kind k)"], 0));
    }

    // La llamada y hasta dos líneas más, para las que se parten en varias.
    private static bool IsOffending(string[] lines, int index)
    {
        string line = lines[index];
        int comment = line.IndexOf("//", StringComparison.Ordinal);
        string code = comment >= 0 ? line[..comment] : line;
        if (!NumericParse().IsMatch(code))
        {
            return false;
        }

        string statement = string.Join(' ', lines.Skip(index).Take(3));
        int end = statement.IndexOf(';', StringComparison.Ordinal);
        return !Provider().IsMatch(end >= 0 ? statement[..end] : statement);
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Rodaje.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("No encuentro Rodaje.slnx subiendo desde las pruebas.");
    }
}
