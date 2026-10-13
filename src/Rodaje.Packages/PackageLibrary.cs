using System.Text;
using System.Text.Json;

namespace Rodaje.Packages;

/// <summary>De dónde salió un paquete instalado.</summary>
public enum PackageOrigin
{
    /// <summary>Instalado por la persona (se puede desinstalar).</summary>
    Installed,

    /// <summary>Viene junto al ejecutable, en la distribución completa (solo lectura).</summary>
    Bundled,

    /// <summary>Lo que la persona hizo en el programa, por ejemplo una transición guardada desde el inspector.</summary>
    Own,
}

/// <summary>Carpetas de paquetes de un programa.</summary>
/// <param name="Packages">Paquetes de contenido instalados (escribible).</param>
/// <param name="Modules">Módulos de IA instalados (escribible).</param>
/// <param name="Own">Elementos propios de la persona (escribible por el programa, no por el instalador).</param>
/// <param name="Bundled">Carpetas de solo lectura que trae la distribución completa, con paquetes o módulos dentro.</param>
public sealed record PackageLocations(string Packages, string Modules, string Own, IReadOnlyList<string> Bundled)
{
    /// <summary>Las de siempre bajo una carpeta de datos (por ejemplo <c>%LOCALAPPDATA%\Rodaje\complementos</c>).</summary>
    public static PackageLocations Under(string root, IReadOnlyList<string>? bundled = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        return new PackageLocations(Path.Combine(root, "paquetes"), Path.Combine(root, "modulos"), Path.Combine(root, "propios"), bundled ?? []);
    }

    /// <summary>Carpeta donde se instala un paquete de ese tipo.</summary>
    public string InstallRoot(PackageKind kind) => kind == PackageKind.Module ? Modules : Packages;
}

/// <summary>Un paquete encontrado en disco.</summary>
public sealed record InstalledPackage(PackageManifest Manifest, string Directory, PackageOrigin Origin)
{
    public string Id => Manifest.Id;

    public bool CanUninstall => Origin == PackageOrigin.Installed;
}

/// <summary>Un elemento listo para mostrarse en la biblioteca, con su paquete y su archivo en disco.</summary>
public sealed record PackageItemEntry(InstalledPackage Package, PackageItem Item)
{
    public PackageReference Reference => Package.Manifest.ReferenceTo(Item);

    public string? FilePath => Item.File is null ? null : Path.GetFullPath(Path.Combine(Package.Directory, Item.File));
}

/// <summary>Un paquete que no se pudo cargar, y por qué.</summary>
public sealed record PackageProblem(string Directory, string Message);

/// <summary>Lo que hay instalado.</summary>
public sealed record PackageScan(IReadOnlyList<InstalledPackage> Packages, IReadOnlyList<PackageProblem> Problems)
{
    public static readonly PackageScan Empty = new([], []);

    public InstalledPackage? Find(string id) => Packages.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

    public IEnumerable<PackageItemEntry> Items(ItemKind kind) =>
        Packages.Where(p => p.Manifest.Kind == PackageKind.Content)
            .SelectMany(p => p.Manifest.Items.Where(i => i.Kind == kind).Select(i => new PackageItemEntry(p, i)));
}

/// <summary>
/// Busca paquetes en sus carpetas: cada subcarpeta con un <c>paquete.json</c> válido para el programa es un paquete. Si un
/// mismo id aparece en varias, gana la versión mayor (a igual versión, el instalado sobre el incluido).
/// </summary>
public static class PackageLibrary
{
    public const string OwnPackageId = "propios";

    public static PackageScan Scan(PackageLocations locations, string appId)
    {
        ArgumentNullException.ThrowIfNull(locations);
        ArgumentException.ThrowIfNullOrEmpty(appId);
        var found = new Dictionary<string, InstalledPackage>(StringComparer.Ordinal);
        var problems = new List<PackageProblem>();
        foreach (string bundled in locations.Bundled)
        {
            ScanRoot(bundled, PackageOrigin.Bundled, appId, found, problems);
        }

        ScanRoot(locations.Packages, PackageOrigin.Installed, appId, found, problems);
        ScanRoot(locations.Modules, PackageOrigin.Installed, appId, found, problems);
        if (ScanOwn(locations.Own, appId, problems) is { } own)
        {
            found[own.Id] = own;
        }

        List<InstalledPackage> packages = [.. found.Values.OrderBy(p => p.Origin == PackageOrigin.Own).ThenBy(p => p.Manifest.Name, SpanishText.Comparer)];
        return new PackageScan(packages, problems);
    }

    private static void ScanRoot(string root, PackageOrigin origin, string appId, Dictionary<string, InstalledPackage> found, List<PackageProblem> problems)
    {
        IEnumerable<string> directories;
        try
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            directories = Directory.EnumerateDirectories(root).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add(new PackageProblem(root, ex.Message));
            return;
        }

        foreach (string directory in directories)
        {
            string name = Path.GetFileName(directory);
            if (name.StartsWith('.'))
            {
                // Instalaciones a medias (.instalando-*, .desinstalando-*): el instalador las limpia.
                continue;
            }

            ManifestResult result = ManifestReader.ReadFile(Path.Combine(directory, PackageManifest.FileName));
            if (result.Manifest is not { } manifest)
            {
                problems.Add(new PackageProblem(directory, string.Join(" ", result.Errors)));
                continue;
            }

            if (!manifest.WorksIn(appId))
            {
                problems.Add(new PackageProblem(directory, $"«{manifest.Name}» es para {string.Join(", ", manifest.Apps)}, no para {appId}."));
                continue;
            }

            var package = new InstalledPackage(manifest, Path.GetFullPath(directory), origin);
            if (!found.TryGetValue(manifest.Id, out InstalledPackage? existing) || manifest.Version >= existing.Manifest.Version)
            {
                found[manifest.Id] = package;
            }
        }
    }

    /// <summary>
    /// Los elementos propios: un <c>paquete.json</c> en la carpeta, o archivos <c>*.json</c> sueltos (uno por elemento, en
    /// cualquier subcarpeta) con <c>kind</c> y <c>name</c>, y opcionalmente <c>id</c> y <c>description</c>.
    /// </summary>
    private static InstalledPackage? ScanOwn(string root, string appId, List<PackageProblem> problems)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        string manifestPath = Path.Combine(root, PackageManifest.FileName);
        if (File.Exists(manifestPath))
        {
            ManifestResult result = ManifestReader.ReadFile(manifestPath);
            if (result.Manifest is { } manifest)
            {
                return new InstalledPackage(manifest with { Id = OwnPackageId }, Path.GetFullPath(root), PackageOrigin.Own);
            }

            problems.Add(new PackageProblem(root, string.Join(" ", result.Errors)));
        }

        var items = new List<PackageItem>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        List<string> files;
        try
        {
            files = [.. Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
                .Where(f => !string.Equals(Path.GetFileName(f), PackageManifest.FileName, StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add(new PackageProblem(root, ex.Message));
            return null;
        }

        foreach (string file in files)
        {
            if (ReadOwnItem(file, root) is { } item && ids.Add(item.Id))
            {
                items.Add(item);
            }
        }

        if (items.Count == 0)
        {
            return null;
        }

        var own = new PackageManifest
        {
            FormatVersion = PackageManifest.CurrentFormatVersion,
            Id = OwnPackageId,
            Version = PackageVersion.Parse("0.0.0"),
            Kind = PackageKind.Content,
            Name = "Mis elementos",
            Author = Environment.UserName,
            License = "LicenseRef-Propio",
            Apps = [appId],
            Items = items,
        };
        return new InstalledPackage(own, Path.GetFullPath(root), PackageOrigin.Own);
    }

    private static PackageItem? ReadOwnItem(string file, string root)
    {
        try
        {
            if (new FileInfo(file).Length > ManifestReader.MaxManifestBytes)
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(SafeText.ReadAllTextStrict(file), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            JsonElement e = document.RootElement;
            if (SafeText.HasUnreadableString(e) || e.ValueKind != JsonValueKind.Object
                || !e.TryGetProperty("kind", out JsonElement kindElement) || kindElement.ValueKind != JsonValueKind.String
                || ManifestReader.ParseItemKind(kindElement.GetString()!) is not { } kind)
            {
                return null;
            }

            string stem = Path.GetFileNameWithoutExtension(file);
            // El inspector escribe "propios.<nombre>": la referencia queda propios@0.0.0/<nombre>.
            string? explicitId = StringOf(e, "id") is { } given && given.StartsWith(OwnPackageId + ".", StringComparison.Ordinal)
                ? given[(OwnPackageId.Length + 1)..]
                : StringOf(e, "id");
            string id = explicitId is not null && Identifiers.IsValid(explicitId) ? explicitId : ToIdentifier(stem);
            string name = SafeText.Truncate(SafeText.Clean(StringOf(e, "name") ?? stem), SafeText.MaxName);
            string? description = StringOf(e, "description") is { } text ? SafeText.Truncate(SafeText.Clean(text, multiline: true), SafeText.MaxDescription) : null;
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            return new PackageItem(id, kind, name.Length > 0 ? name : ToIdentifier(stem), relative, description);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException)
        {
            return null;
        }
    }

    private static string? StringOf(JsonElement e, string property) =>
        e.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    /// <summary>Un nombre de archivo cualquiera convertido en identificador válido (minúsculas, sin tildes, con guiones).</summary>
    public static string ToIdentifier(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (char c in SpanishText.Fold(text))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            char lower = char.ToLowerInvariant(c);
            if (char.IsAsciiLetterLower(lower) || char.IsAsciiDigit(lower))
            {
                builder.Append(lower);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        string id = builder.ToString().Trim('-');
        if (id.Length > Identifiers.MaxLength)
        {
            id = id[..Identifiers.MaxLength].TrimEnd('-');
        }

        return id.Length == 0 ? "elemento" : SafePaths.IsReservedName(id) ? id + "-1" : id;
    }
}
