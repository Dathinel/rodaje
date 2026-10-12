using System.Text;
using Rodaje.App.Services.Inspector;
using Rodaje.Packages;
using Rodaje.Packages.Content;

namespace Rodaje.App.Services.SafeZones;

/// <summary>De dónde viene un juego de zonas: de fábrica, de un paquete instalado o hecho por la persona.</summary>
public enum SafeZoneOrigin
{
    BuiltIn,
    Package,
    Own,
}

/// <summary>Un juego de zonas para elegir en el visor, con la referencia que se guarda en los ajustes.</summary>
public sealed record SafeZoneChoice(string Reference, SafeZonePreset Preset, SafeZoneOrigin Origin)
{
    public string Name => Preset.Name;
}

/// <summary>
/// Las zonas seguras del visor (<c>rodaje.safezones</c>), sin WinUI: las de fábrica (JSON incluidos en el programa, con el
/// mismo formato que un paquete), las de los paquetes instalados y las propias de <c>complementos\propios</c>.
/// </summary>
public static class SafeZoneCatalog
{
    /// <summary>Prefijo de las referencias de fábrica: <c>fabrica/tiktok</c>.</summary>
    public const string BuiltInPrefix = "fabrica/";

    /// <summary>Los márgenes de acción y de títulos de siempre.</summary>
    public const string Classic = BuiltInPrefix + "clasicos";

    /// <summary>Tipo de elemento con el que se guarda un juego propio, para que la carpeta <c>propios</c> lo reconozca.</summary>
    public const string ItemKindText = "safe-zones";

    private const string ResourcePrefix = "Rodaje.App.zonas.";

    // Orden del menú: los clásicos y luego las redes, de la más usada en vertical a la horizontal.
    private static readonly string[] BuiltInOrder =
        ["clasicos", "tiktok", "instagram-reels", "youtube-shorts", "facebook-reels", "instagram-historias", "youtube"];

    private static readonly Lazy<IReadOnlyList<SafeZoneChoice>> BuiltInSets = new(LoadBuiltIn);

    public static IReadOnlyList<SafeZoneChoice> BuiltIn => BuiltInSets.Value;

    /// <summary>Lo de fábrica, lo de los paquetes y lo propio, en ese orden; lo que no se pueda leer queda en <paramref name="problems"/>.</summary>
    public static IReadOnlyList<SafeZoneChoice> All(PackageScan installed, List<string>? problems = null)
    {
        ArgumentNullException.ThrowIfNull(installed);
        var all = new List<SafeZoneChoice>(BuiltIn);
        foreach (PackageItemEntry entry in installed.Items(ItemKind.SafeZones)
            .OrderBy(e => e.Package.Origin == PackageOrigin.Own).ThenBy(e => e.Item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            try
            {
                using FileStream stream = File.OpenRead(entry.FilePath ?? throw new FileNotFoundException(entry.Item.Name));
                SafeZonePreset preset = ContentReaders.ReadSafeZones(stream) with { Name = entry.Item.Name };
                all.Add(new SafeZoneChoice(entry.Reference.ToString(), preset,
                    entry.Package.Origin == PackageOrigin.Own ? SafeZoneOrigin.Own : SafeZoneOrigin.Package));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PackageContentException)
            {
                problems?.Add($"{entry.Item.Name}: {ex.Message}");
            }
        }

        return all;
    }

    /// <summary>El juego de esa referencia; si ya no existe (se desinstaló), los clásicos.</summary>
    public static SafeZoneChoice Find(IReadOnlyList<SafeZoneChoice> all, string? reference)
    {
        ArgumentNullException.ThrowIfNull(all);
        return all.FirstOrDefault(c => c.Reference == reference)
            ?? all.FirstOrDefault(c => c.Reference == Classic)
            ?? BuiltIn[0];
    }

    /// <summary>
    /// Guarda un juego propio en <paramref name="folder"/> como elemento suelto (<c>&lt;nombre&gt;.json</c> con
    /// <c>kind: safe-zones</c>); devuelve su referencia <c>propios@0.0.0/&lt;id&gt;</c>. Con <paramref name="replace"/>
    /// reescribe ese archivo en lugar de crear otro.
    /// </summary>
    public static string SaveOwn(string folder, SafeZonePreset preset, string? replace = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(preset);
        Directory.CreateDirectory(folder);
        string slug = AnimationPreset.Slug(preset.Name);
        string path = replace ?? Path.Combine(folder, slug + ".json");
        for (int n = 2; replace is null && File.Exists(path); n++)
        {
            path = Path.Combine(folder, $"{slug}-{n}.json");
        }

        string id = Path.GetFileNameWithoutExtension(path);
        byte[] json = ContentReaders.WriteSafeZones(preset with { Id = PackageLibrary.OwnPackageId + "." + id }, ItemKindText);
        AppDataPaths.WriteAllTextAtomic(path, Encoding.UTF8.GetString(json) + "\n");
        return $"{PackageLibrary.OwnPackageId}@0.0.0/{id}";
    }

    /// <summary>El archivo de un juego propio, para reescribirlo al editarlo; nulo si no es propio.</summary>
    public static string? OwnFile(PackageScan installed, string reference)
    {
        ArgumentNullException.ThrowIfNull(installed);
        return installed.Items(ItemKind.SafeZones)
            .FirstOrDefault(e => e.Package.Origin == PackageOrigin.Own && e.Reference.ToString() == reference)?.FilePath;
    }

    private static List<SafeZoneChoice> LoadBuiltIn()
    {
        var sets = new List<SafeZoneChoice>();
        foreach (string id in BuiltInOrder)
        {
            using Stream stream = typeof(SafeZoneCatalog).Assembly.GetManifestResourceStream(ResourcePrefix + id + ".json")
                ?? throw new InvalidOperationException($"Falta el juego de zonas de fábrica «{id}».");
            sets.Add(new SafeZoneChoice(BuiltInPrefix + id, ContentReaders.ReadSafeZones(stream), SafeZoneOrigin.BuiltIn));
        }

        return sets;
    }
}
