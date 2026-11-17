using System.Buffers.Binary;
using System.Text;
using HarfBuzzSharp;
using Rodaje.Core.Model;

namespace Rodaje.Text;

/// <summary>Una cara de un archivo de fuente lista para HarfBuzz, en unidades de diseño (escala = unidades por eme).</summary>
public sealed class ShapingFont : IDisposable
{
    private readonly Blob _blob;
    private readonly Face _face;

    private ShapingFont(string path, int index, Blob blob, Face face, string family, int weight, bool italic)
    {
        Path = path;
        Index = index;
        _blob = blob;
        _face = face;
        Family = family;
        Weight = weight;
        Italic = italic;
        UnitsPerEm = Math.Max(1, face.UnitsPerEm);
        Font = new HarfBuzzSharp.Font(face);
        Font.SetFunctionsOpenType();
        Font.SetScale(UnitsPerEm, UnitsPerEm);
    }

    public string Path { get; }

    public int Index { get; }

    public string Family { get; }

    public int Weight { get; }

    public bool Italic { get; }

    public int UnitsPerEm { get; }

    internal HarfBuzzSharp.Font Font { get; }

    public bool Has(int codepoint) => Font.TryGetNominalGlyph(codepoint, out uint _);

    /// <summary>Todas las caras de un archivo; vacío si no es una fuente que HarfBuzz entienda o no tiene nombre de familia.</summary>
    public static List<ShapingFont> Load(string path)
    {
        var fonts = new List<ShapingFont>();
        Blob blob;
        try
        {
            blob = Blob.FromFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return fonts;
        }

        int faces = Math.Max(0, blob.FaceCount);
        for (int i = 0; i < faces; i++)
        {
            var face = new Face(blob, i);
            if (face.GlyphCount > 0 && FontNames.Read(face) is { } names)
            {
                fonts.Add(new ShapingFont(path, i, blob, face, names.Family, names.Weight, names.Italic));
            }
            else
            {
                face.Dispose();
            }
        }

        if (fonts.Count == 0)
        {
            blob.Dispose();
        }

        return fonts;
    }

    public void Dispose()
    {
        Font.Dispose();
        _face.Dispose();
        // Las caras de un .ttc comparten el blob: HarfBuzz cuenta las referencias.
        _blob.Dispose();
    }
}

/// <summary>
/// Las fuentes de los títulos fuera de Windows: las de los paquetes registradas aquí (nunca instaladas en el sistema) y las
/// del sistema como respaldo, igual que <c>TitleFonts</c> con DirectWrite. También da la fuente de respaldo por letra.
/// </summary>
/// <remarks>
/// Shortcut: las del sistema se buscan recorriendo sus carpetas de fuentes, sin fontconfig ni CoreText, y el respaldo por
/// letra prueba primero unas familias conocidas y después las demás por nombre. Las fuentes variables usan su instancia por
/// omisión. Cuando la interfaz de Linux dibuje títulos (fase 8) se cambia por fontconfig y CoreText y se aplican los ejes con
/// hb_font_set_variations.
/// </remarks>
public sealed class TitleFontSet : IDisposable
{
    /// <summary>Tope de un archivo de fuente (D-029: 20 MB por archivo), el mismo que en Windows.</summary>
    public const long MaxFileBytes = 20L * 1024 * 1024;

    private static readonly string[] PreferredSystemFamilies =
        ["Segoe UI", "Helvetica Neue", "Helvetica", "DejaVu Sans", "Noto Sans", "Liberation Sans", "Arial"];

    private readonly Lock _gate = new();
    private readonly List<ShapingFont> _registered = [];
    private readonly HashSet<string> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<int, ShapingFont?> _perGlyph = [];
    private List<ShapingFont>? _system;
    private bool _disposed;

    /// <summary>Agrega archivos <c>.ttf</c>/<c>.otf</c>/<c>.ttc</c>; devuelve los que no se pudieron leer o pasan del tope.</summary>
    public IReadOnlyList<string> Register(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var rejected = new List<string>();
            foreach (string path in paths)
            {
                if (_files.Contains(path))
                {
                    continue;
                }

                var info = new FileInfo(path);
                List<ShapingFont> faces = info.Exists && info.Length <= MaxFileBytes ? ShapingFont.Load(path) : [];
                if (faces.Count == 0)
                {
                    rejected.Add(path);
                    continue;
                }

                _files.Add(path);
                _registered.AddRange(faces);
            }

            _perGlyph.Clear();
            return rejected;
        }
    }

    /// <summary>La primera familia de <c>[pedida, respaldos…]</c> que haya; si ninguna, una del sistema. <c>Missing</c> si faltaba la pedida.</summary>
    public (ShapingFont Font, bool Missing) Resolve(StyleFont font)
    {
        ArgumentNullException.ThrowIfNull(font);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Find(font.Family, font.Weight, font.Italic) is { } exact)
            {
                return (exact, false);
            }

            foreach (string family in font.Fallback)
            {
                if (Find(family, font.Weight, font.Italic) is { } fallback)
                {
                    return (fallback, true);
                }
            }

            ShapingFont? lastResort = PreferredSystemFamilies.Select(f => Find(f, font.Weight, font.Italic)).FirstOrDefault(f => f is not null)
                ?? SystemFonts().FirstOrDefault() ?? _registered.FirstOrDefault();
            return (lastResort ?? throw new InvalidOperationException("No hay ninguna fuente: ni registrada ni del sistema."), true);
        }
    }

    /// <summary>La fuente que dibuja <paramref name="codepoint"/> cuando la del título no lo tiene: las registradas primero y después el sistema.</summary>
    public ShapingFont? FallbackFor(int codepoint)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_perGlyph.TryGetValue(codepoint, out ShapingFont? found))
            {
                found = _registered.FirstOrDefault(f => f.Has(codepoint)) ?? SystemFonts().FirstOrDefault(f => f.Has(codepoint));
                _perGlyph[codepoint] = found;
            }

            return found;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (ShapingFont font in _registered.Concat(_system ?? []))
            {
                font.Dispose();
            }

            _registered.Clear();
            _system?.Clear();
            _perGlyph.Clear();
        }
    }

    private ShapingFont? Find(string family, int weight, bool italic)
    {
        if (string.IsNullOrWhiteSpace(family))
        {
            return null;
        }

        return Best(_registered, family, weight, italic) ?? Best(SystemFonts(), family, weight, italic);
    }

    private static ShapingFont? Best(IEnumerable<ShapingFont> fonts, string family, int weight, bool italic) => fonts
        .Where(f => string.Equals(f.Family, family, StringComparison.OrdinalIgnoreCase))
        .OrderBy(f => f.Italic != italic)
        .ThenBy(f => Math.Abs(f.Weight - weight))
        .FirstOrDefault();

    /// <summary>Las fuentes del sistema, leídas una vez: primero las familias de <see cref="PreferredSystemFamilies"/> y después por nombre.</summary>
    private List<ShapingFont> SystemFonts()
    {
        if (_system is not null)
        {
            return _system;
        }

        var found = new List<ShapingFont>();
        foreach (string directory in SystemFontDirectories().Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true });
            }
            catch (IOException)
            {
                continue;
            }

            foreach (string file in files.Where(f => Path.GetExtension(f).ToUpperInvariant() is ".TTF" or ".OTF" or ".TTC" or ".OTC"))
            {
                found.AddRange(ShapingFont.Load(file));
            }
        }

        _system = [.. found
            .OrderBy(f => Array.FindIndex(PreferredSystemFamilies, p => string.Equals(p, f.Family, StringComparison.OrdinalIgnoreCase)) is var i && i >= 0 ? i : int.MaxValue)
            .ThenBy(f => f.Family, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Italic)
            .ThenBy(f => Math.Abs(f.Weight - 400))
            .ThenBy(f => f.Path, StringComparer.Ordinal)];
        return _system;
    }

    private static IEnumerable<string> SystemFontDirectories()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            yield return Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/System/Library/Fonts";
            yield return "/Library/Fonts";
            yield return Path.Combine(home, "Library", "Fonts");
        }
        else
        {
            yield return "/usr/share/fonts";
            yield return "/usr/local/share/fonts";
            yield return Path.Combine(home, ".local", "share", "fonts");
            yield return Path.Combine(home, ".fonts");
        }
    }
}

/// <summary>Familia, peso y cursiva de una cara, leídos de sus tablas <c>name</c> y <c>OS/2</c>.</summary>
internal static class FontNames
{
    public static (string Family, int Weight, bool Italic)? Read(Face face)
    {
        string? family;
        using (Blob name = face.ReferenceTable(new Tag('n', 'a', 'm', 'e')))
        {
            ReadOnlySpan<byte> table = name.AsSpan();
            // La familia tipográfica (16) si la hay, como DirectWrite; si no, la de siempre (1).
            family = NameRecord(table, 16) ?? NameRecord(table, 1);
        }

        if (string.IsNullOrWhiteSpace(family))
        {
            return null;
        }

        int weight = 400;
        bool italic = false;
        using (Blob os2 = face.ReferenceTable(new Tag('O', 'S', '/', '2')))
        {
            ReadOnlySpan<byte> table = os2.AsSpan();
            if (table.Length >= 64)
            {
                weight = BinaryPrimitives.ReadUInt16BigEndian(table[4..]);
                italic = (BinaryPrimitives.ReadUInt16BigEndian(table[62..]) & 1) != 0;
            }
        }

        return (family, weight, italic);
    }

    /// <summary>El registro <paramref name="nameId"/> en Unicode (Windows o Unicode, UTF-16 big endian), en inglés si hay varios.</summary>
    private static string? NameRecord(ReadOnlySpan<byte> table, int nameId)
    {
        if (table.Length < 6)
        {
            return null;
        }

        int count = BinaryPrimitives.ReadUInt16BigEndian(table[2..]);
        int storage = BinaryPrimitives.ReadUInt16BigEndian(table[4..]);
        string? any = null;
        for (int r = 0; r < count; r++)
        {
            int at = 6 + (r * 12);
            if (at + 12 > table.Length)
            {
                break;
            }

            int platform = BinaryPrimitives.ReadUInt16BigEndian(table[at..]);
            int language = BinaryPrimitives.ReadUInt16BigEndian(table[(at + 4)..]);
            int id = BinaryPrimitives.ReadUInt16BigEndian(table[(at + 6)..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(table[(at + 8)..]);
            int offset = BinaryPrimitives.ReadUInt16BigEndian(table[(at + 10)..]);
            if (id != nameId || platform is not (0 or 3) || storage + offset + length > table.Length)
            {
                continue;
            }

            string text = Encoding.BigEndianUnicode.GetString(table.Slice(storage + offset, length));
            if (platform == 0 || language == 0x0409)
            {
                return text;
            }

            any ??= text;
        }

        return any;
    }
}
