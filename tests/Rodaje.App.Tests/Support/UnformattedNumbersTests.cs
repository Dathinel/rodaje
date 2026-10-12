using System.Text.RegularExpressions;

namespace Rodaje.App.Tests.Support;

/// <summary>
/// La app corre con <c>InvariantGlobalization</c>: un número con decimales formateado sin proveedor sale con punto
/// («0.5»), igual que con <c>CultureInfo.CurrentCulture</c>. En <c>Views/</c> y <c>Services/</c> toda interpolación con
/// formato decimal (<c>:0.</c>, <c>:F</c>, <c>:P</c>, <c>:N</c>, grupos con <c>#,</c>) y todo <c>ToString("…")</c> numérico
/// tiene que llevar <c>SpanishNumbers.Format</c> o, si va a un archivo o a un registro, <c>InvariantCulture</c>.
/// </summary>
public class UnformattedNumbersTests
{
    // Diagnósticos y medidas: el texto va a un informe para comparar cifras, no al usuario, y lleva punto a propósito.
    private static readonly string[] DiagnosticFiles =
        ["TimelineBenchmark.cs", "LibraryScrollMeasurement.cs", "MeasurementRun.cs", "Metrics.cs"];

    // Pendientes conocidos fuera de los caminos de la tarea 60, línea por línea. Hoy no queda ninguno.
    private static readonly (string File, string Line)[] KnownPending = [];

    // Proveedores que dejan bien el número, ayudantes que los aplican y salidas que no ve el usuario.
    private static readonly string[] Providers =
    [
        "SpanishNumbers.Format", "InvariantCulture", "NumberFormatInfo.InvariantInfo",
        "Trace.Write", "Debug.Write", "Debug.Assert",
    ];

    // Ayudantes de un archivo que reciben un FormattableString y lo formatean con SpanishNumbers.Format.
    private static readonly (string File, string Call)[] Helpers = [("VoiceDialog.cs", "Fmt($\"")];

    // Interpolación con formato decimal o de miles: {v:0.0}, {v:#,##0}, {v:F1}, {v:P0}, {v:N2}.
    private static readonly Regex NumericHole = new(@"\{[^{}]*?:(?:[0#]+[.,]|[FfPpNn]\d{0,2}\})", RegexOptions.CultureInvariant);

    // ToString con un formato numérico y sin proveedor: ToString("0.0"), ToString("F2"), ToString("P0"), ToString("#,##0").
    private static readonly Regex NumericToString =
        new(@"\.ToString\(""(?:[0#][^""]*|[CcEeFfGgNnPp]\d{0,2})""\)", RegexOptions.CultureInvariant);

    private static readonly string[] ScannedFolders = ["Views", "Services"];

    private const int MaxStatementLines = 6;

    [Fact]
    public void LasVistasYLosServiciosNoFormateanDecimalesSinProveedor()
    {
        string source = Path.Combine(RepositoryRoot(), "src", "Rodaje.App");
        var found = new List<string>();
        foreach (string folder in ScannedFolders)
        {
            foreach (string file in Directory.EnumerateFiles(Path.Combine(source, folder), "*.cs", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file);
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (IsOffending(name, lines, i))
                    {
                        found.Add($"{Path.GetRelativePath(source, file)}:{i + 1}: {lines[i].Trim()}");
                    }
                }
            }
        }

        Assert.Empty(found);
    }

    [Theory]
    [InlineData("status.Text = $\"{v:0.0} s\";")]
    [InlineData("status.Text = $\"{v:0.##} s\";")]
    [InlineData("status.Text = $\"{v:#,##0} cuadros\";")]
    [InlineData("status.Text = $\"{v:F1} s\";")]
    [InlineData("status.Text = $\"{v:f2} s\";")]
    [InlineData("status.Text = $\"{v:P0} menos\";")]
    [InlineData("status.Text = $\"{v:N} cuadros\";")]
    [InlineData("status.Text = $\"{v:N2} cuadros\";")]
    [InlineData("status.Text = string.Format(\"{0:0.0} s\", v);")]
    [InlineData("status.Text = v.ToString(\"0.0\");")]
    [InlineData("status.Text = v.ToString(\"#,##0\");")]
    [InlineData("status.Text = v.ToString(\"F2\");")]
    [InlineData("status.Text = v.ToString(\"P0\");")]
    [InlineData("status.Text = v.ToString(\"N1\");")]
    [InlineData("status.Text = v.ToString(\"G\");")]
    public void LaVigilanciaDetectaCadaPatron(string line)
    {
        Assert.True(IsOffending("X.cs", [line], 0));
    }

    [Theory]
    [InlineData("status.Text = string.Create(SpanishNumbers.Format, $\"{v:0.0} s\");")]
    [InlineData("status.Text = v.ToString(\"0.0\", SpanishNumbers.Format);")]
    [InlineData("string json = string.Create(CultureInfo.InvariantCulture, $\"{v:0.###}\");")]
    [InlineData("string s = v.ToString(\"F2\", NumberFormatInfo.InvariantInfo);")]
    [InlineData("Trace.WriteLine($\"Rodaje: no respondió en {t.TotalSeconds:0.#} s\");")]
    [InlineData("string path = $\"ruido-{Guid.NewGuid():N}.wav\";")]
    [InlineData("string id = Guid.NewGuid().ToString(\"N\");")]
    [InlineData("status.Text = $\"{count} cuadros, {db:0} dB\";")]
    [InlineData("status.Text = $\"{(a ? b : 0)} s\";")]
    [InlineData("string t = when.ToString(\"HH:mm\");")]
    [InlineData("// $\"{v:0.0}\" daría «0.5»")]
    [InlineData("/// <c>v.ToString(\"0.0\")</c>")]
    public void LaVigilanciaDejaPasarLoCorrecto(string line)
    {
        Assert.False(IsOffending("X.cs", [line], 0));
    }

    [Fact]
    public void LaVigilanciaMiraLaSentenciaEntera()
    {
        // El proveedor suele ir en la línea anterior: string.Create(SpanishNumbers.Format,\n    $"…{v:0.0}…");
        string[] good =
        [
            "status.Text = string.Create(SpanishNumbers.Format,",
            "    $\"Temblor: {a:0.0} → {b:0.00} px \" +",
            "    $\"({c:P0} menos).\");",
        ];
        Assert.False(IsOffending("X.cs", good, 1));
        Assert.False(IsOffending("X.cs", good, 2));

        // ...o en la siguiente: ((FormattableString)$"…{v:0.0}…")\n    .ToString(SpanishNumbers.Format);
        string[] after = ["string s = ((FormattableString)$\"{v:0.0}×\")", "    .ToString(SpanishNumbers.Format);"];
        Assert.False(IsOffending("X.cs", after, 0));

        // Un proveedor en la sentencia anterior no tapa la siguiente.
        string[] bad = ["a.Text = string.Create(SpanishNumbers.Format, $\"{v:0.0} s\");", "b.Text = $\"{w:0.0} s\";"];
        Assert.False(IsOffending("X.cs", bad, 0));
        Assert.True(IsOffending("X.cs", bad, 1));
    }

    [Fact]
    public void LaVigilanciaRespetaLasListasExplicitas()
    {
        string[] line = ["report.AppendLine($\"p95 {P(0.95):0.0} ms\");"];
        Assert.True(IsOffending("X.cs", line, 0));
        foreach (string file in DiagnosticFiles)
        {
            Assert.False(IsOffending(file, line, 0));
        }

        // El ayudante solo vale en su archivo.
        string[] fmt = ["status.Text = Fmt($\"Listo: {v.Seconds:0.0} s de audio.\");"];
        Assert.False(IsOffending("VoiceDialog.cs", fmt, 0));
        Assert.True(IsOffending("OtroDialog.cs", fmt, 0));
    }

    [Fact]
    public void LaVigilanciaLeeLasVistasYLosServicios()
    {
        // Si la ruta cambiara y no leyera nada, la vigilancia pasaría siempre: el diálogo de estabilizar tiene P0 y 0.0.
        string stabilize = Path.Combine(RepositoryRoot(), "src", "Rodaje.App", "Views", "Analysis", "StabilizeDialog.cs");
        string[] lines = File.ReadAllLines(stabilize);
        Assert.Contains(lines, l => NumericHole.IsMatch(l));
        Assert.True(Directory.Exists(Path.Combine(RepositoryRoot(), "src", "Rodaje.App", "Services")));
    }

    /// <summary>
    /// ¿La línea <paramref name="index"/> formatea un número con decimales sin proveedor? Mira la sentencia entera (hasta
    /// <see cref="MaxStatementLines"/> líneas hacia atrás y hacia delante), porque el proveedor suele ir en otra línea.
    /// </summary>
    private static bool IsOffending(string fileName, string[] lines, int index)
    {
        string line = lines[index].Trim();
        if (line.StartsWith("//", StringComparison.Ordinal) || DiagnosticFiles.Contains(fileName))
        {
            return false;
        }

        if (!NumericHole.IsMatch(line) && !NumericToString.IsMatch(line))
        {
            return false;
        }

        string statement = Statement(lines, index);
        if (statement.Contains("Guid", StringComparison.Ordinal)
            || Providers.Any(p => statement.Contains(p, StringComparison.Ordinal))
            || Helpers.Any(h => h.File == fileName && statement.Contains(h.Call, StringComparison.Ordinal)))
        {
            return false;
        }

        return !KnownPending.Any(p => p.File == fileName && line.Contains(p.Line, StringComparison.Ordinal));
    }

    // La sentencia que contiene la línea: hacia atrás hasta una línea que cierre otra sentencia o abra un bloque, y hacia
    // delante hasta el «;».
    private static string Statement(string[] lines, int index)
    {
        int start = index;
        while (start > 0 && index - start < MaxStatementLines && !EndsStatement(lines[start - 1]))
        {
            start--;
        }

        int end = index;
        while (end < lines.Length - 1 && end - index < MaxStatementLines && !EndsStatement(lines[end]))
        {
            end++;
        }

        return string.Join('\n', lines.Skip(start).Take(end - start + 1));
    }

    private static bool EndsStatement(string line)
    {
        string t = line.TrimEnd();
        return t.Length == 0 || t.EndsWith(';') || t.EndsWith('{') || t.EndsWith('}');
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
