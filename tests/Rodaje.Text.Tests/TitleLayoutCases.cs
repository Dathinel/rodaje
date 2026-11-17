using System.Text.Json;
using System.Text.Json.Serialization;
using Rodaje.Core.Model;
using Rodaje.Core.Text;

namespace Rodaje.Text.Tests;

// Compartido con Rodaje.Render.Tests (enlazado): los mismos casos los maqueta DirectWrite en Windows, que escribe la
// referencia en Maquetacion/, y HarfBuzz en los tres sistemas, que la compara (D-066, fase 5).

/// <summary>Un caso de aceptación de títulos: texto, estilo, caja y la unidad por la que se parte.</summary>
public sealed record TitleLayoutCase(string Name, string Text, double Size, string Family, TextUnit Unit, double BoxWidth = 0,
    TextAlign Align = TextAlign.Center, double LetterSpacing = 0, double LineHeight = 1.2, bool Kerning = true, TextCase Case = TextCase.None,
    string[]? Fallback = null, bool FallsBackPerGlyph = false)
{
    public TextStyle Style()
    {
        var style = new TextStyle
        {
            Size = Size,
            Font = new StyleFont { Family = Family, Weight = 400, Fallback = [.. Fallback ?? []] },
            Align = Align,
            LetterSpacing = LetterSpacing,
            LineHeight = LineHeight,
            In = TextAnimationPresets.Find("subir")!.In(Unit, 0.5),
        };
        if (!Kerning || Case != TextCase.None)
        {
            style.Typography = new StyleTypography { Kerning = Kerning, Case = Case };
        }

        return style;
    }

    public override string ToString() => Name;
}

public static class TitleLayoutCases
{
    public const string Anton = "Anton";
    public const string Mono = "JetBrains Mono";

    /// <summary>Los archivos de fuente de los casos (OFL), relativos a la raíz del repositorio.</summary>
    public static IReadOnlyList<string> FontFiles { get; } =
        ["tests/Rodaje.Text.Tests/Fuentes/Anton-Regular.ttf", "tests/Rodaje.Text.Tests/Fuentes/JetBrainsMono.ttf"];

    public static IReadOnlyList<TitleLayoutCase> All { get; } =
    [
        new("una-linea", "Hola mundo", 64, Anton, TextUnit.Letter),
        new("salto-por-ancho", "La maquetación tiene que romper las líneas en el mismo lugar que DirectWrite.", 48, Anton, TextUnit.Letter,
            BoxWidth: 520, Align: TextAlign.Left),
        new("saltos-forzados", "Primera\nSegunda línea\n\nCuarta", 56, Anton, TextUnit.Letter),
        new("derecha", "Alineado a la derecha dentro de su caja", 40, Anton, TextUnit.Letter, BoxWidth: 300, Align: TextAlign.Right),
        new("espaciado", "ESPACIADO", 60, Anton, TextUnit.Letter, LetterSpacing: 12),
        new("palabra-larga", "Electroencefalografistas", 60, Anton, TextUnit.Letter, BoxWidth: 250),
        new("con-kerning", "AVATAR Tyre", 72, Anton, TextUnit.Letter),
        new("sin-kerning", "AVATAR Tyre", 72, Anton, TextUnit.Letter, Kerning: false),
        new("mayusculas", "texto en mayúsculas", 50, Anton, TextUnit.Letter, Case: TextCase.Upper),
        new("acentos", "Ñandú pingüino açaí", 56, Anton, TextUnit.Letter),
        new("interlineado", "Uno\nDos\nTres", 40, Anton, TextUnit.Letter, LineHeight: 1.6),
        new("monoespaciada", "fi fl 0O -> => !=", 40, Mono, TextUnit.Letter),
        new("respaldo-de-familia", "Fuente que falta", 44, "Fuente Que No Existe", TextUnit.Letter, Fallback: [Mono]),
        new("respaldo-por-letra", "Alfa Ωμέγα beta", 56, Anton, TextUnit.Letter, FallsBackPerGlyph: true),
        new("por-palabras", "La maquetación tiene que romper las líneas en el mismo lugar que DirectWrite.", 48, Anton, TextUnit.Word,
            BoxWidth: 520, Align: TextAlign.Left),
        new("por-lineas", "Primera línea\nSegunda", 56, Anton, TextUnit.Line),
    ];

    public static JsonSerializerOptions Json { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>La raíz del repositorio: la primera carpeta hacia arriba con Rodaje.slnx.</summary>
    public static string RepositoryRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Rodaje.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("No se encontró Rodaje.slnx hacia arriba de " + AppContext.BaseDirectory);
    }

    public static string GoldenPath(string root, string name) => Path.Combine(root, "tests", "Rodaje.Text.Tests", "Maquetacion", name + ".json");
}

/// <summary>Una unidad maquetada (letra, palabra o línea), en píxeles de la secuencia con el origen en el centro de la caja.</summary>
public sealed record LaidOutUnit(int Start, int End, int Line, int Word, float Left, float Right, float Top, float Bottom);

/// <summary>La maquetación de un caso, como se guarda en Maquetacion/&lt;caso&gt;.json.</summary>
public sealed record LaidOutTitle(string Case, string Engine, IReadOnlyList<LaidOutUnit> Units)
{
    public static LaidOutTitle Rounded(string name, string engine, IEnumerable<LaidOutUnit> units) =>
        new(name, engine, [.. units.Select(u => u with
        {
            Left = MathF.Round(u.Left, 2),
            Right = MathF.Round(u.Right, 2),
            Top = MathF.Round(u.Top, 2),
            Bottom = MathF.Round(u.Bottom, 2),
        })]);
}
