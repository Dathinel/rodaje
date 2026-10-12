using System.Text;
using System.Text.Json;
using Rodaje.Packages.Download;

namespace Rodaje.Packages;

/// <summary>Resultado de leer un <c>paquete.json</c>: el manifiesto si es válido, y los errores y avisos en español.</summary>
public sealed record ManifestResult(PackageManifest? Manifest, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool IsValid => Manifest is not null;
}

/// <summary>
/// Lee y valida el manifiesto. Los campos desconocidos se ignoran; un elemento de un tipo que este programa no conoce se
/// omite con un aviso (un paquete más nuevo sigue sirviendo en parte).
/// </summary>
public static class ManifestReader
{
    public const int MaxManifestBytes = 1 << 20;
    public const int MaxItems = 4096;

    /// <summary>Largo máximo de los textos que no tienen uno propio (id, versión, tipo, rutas, funciones).</summary>
    public const int MaxShortText = 256;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 32,
    };

    public static ManifestResult ReadFile(string path, bool checkFiles = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return Fail($"Falta {PackageManifest.FileName}.");
        }

        if (info.Length > MaxManifestBytes)
        {
            return Fail($"{PackageManifest.FileName} pesa más de 1 MB.");
        }

        string text;
        try
        {
            text = SafeText.ReadAllTextStrict(path);
        }
        catch (DecoderFallbackException)
        {
            return Fail($"{PackageManifest.FileName} tiene un texto que no es Unicode válido (UTF-8 dañado o un carácter suelto escapado).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail($"No se pudo leer {PackageManifest.FileName}: {ex.Message}");
        }

        return Read(text, checkFiles ? Path.GetDirectoryName(Path.GetFullPath(path)) : null);
    }

    /// <param name="json">Contenido del manifiesto.</param>
    /// <param name="packageDirectory">Si se da, se comprueba que los archivos de los elementos existan dentro de la carpeta.</param>
    public static ManifestResult Read(string json, string? packageDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, DocumentOptions);
        }
        catch (JsonException ex)
        {
            return Fail($"{PackageManifest.FileName} no es JSON válido: {ex.Message}");
        }
        catch (ArgumentException)
        {
            // Una cadena de .NET con un sustituto suelto no se puede pasar a UTF-8 (desde un archivo no llega: se lee estricto).
            return Fail($"{PackageManifest.FileName} tiene un texto que no es Unicode válido (UTF-8 dañado o un carácter suelto escapado).");
        }

        using (document)
        {
            if (SafeText.HasUnreadableString(document.RootElement))
            {
                return Fail($"{PackageManifest.FileName} tiene un texto que no es Unicode válido (UTF-8 dañado o un carácter suelto escapado).");
            }

            var errors = new List<string>();
            var warnings = new List<string>();
            PackageManifest? manifest = document.RootElement.ValueKind == JsonValueKind.Object
                ? Build(document.RootElement, packageDirectory, errors, warnings)
                : null;
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{PackageManifest.FileName} debe ser un objeto JSON.");
            }

            return new ManifestResult(errors.Count == 0 ? manifest : null, errors, warnings);
        }
    }

    private static ManifestResult Fail(string error) => new(null, [error], []);

    private static PackageManifest? Build(JsonElement root, string? directory, List<string> errors, List<string> warnings)
    {
        int? formatValue = Integer(root, "formatVersion", errors);
        if (formatValue > PackageManifest.CurrentFormatVersion)
        {
            errors.Add($"El paquete usa el formato {formatValue} y este programa entiende hasta el {PackageManifest.CurrentFormatVersion}: actualiza el programa.");
            return null;
        }

        if (formatValue < 1)
        {
            errors.Add("«formatVersion» debe ser 1 o más.");
        }

        int format = formatValue ?? 0;

        string? id = Text(root, "id", errors);
        if (id is not null && !Identifiers.IsValid(id))
        {
            errors.Add($"El id «{id}» no es válido: solo minúsculas, dígitos y guiones o puntos sueltos, hasta {Identifiers.MaxLength} caracteres.");
        }

        string? versionText = Text(root, "version", errors);
        PackageVersion? version = null;
        if (versionText is not null && !PackageVersion.TryParse(versionText, out version))
        {
            errors.Add($"La versión «{versionText}» no es mayor.menor.parche (por ejemplo 1.0.0).");
        }

        PackageKind kind = PackageKind.Content;
        if (OptionalText(root, "kind", errors) is { } kindText)
        {
            switch (kindText)
            {
                case "content":
                    break;
                case "module":
                    kind = PackageKind.Module;
                    break;
                default:
                    errors.Add($"kind «{kindText}» no se conoce: usa «content» o «module».");
                    break;
            }
        }

        string? name = Text(root, "name", errors, SafeText.MaxName);
        string? author = Text(root, "author", errors, SafeText.MaxName);
        string? license = Text(root, "license", errors, SafeText.MaxLicense);
        string? description = OptionalText(root, "description", errors, SafeText.MaxDescription, multiline: true);
        Uri? repository = null;
        if (OptionalText(root, "repository", errors, SafeText.MaxUri) is { } repositoryText && !SafeText.TryParseHttpsUri(repositoryText, out repository))
        {
            errors.Add($"repository debe ser una dirección https con el dominio en ASCII y sin usuario (es «{repositoryText}»).");
        }

        List<string>? appsValue = Strings(root, "apps", errors);
        List<string> apps = appsValue ?? [];
        if (appsValue is { Count: 0 })
        {
            errors.Add("«apps» debe nombrar al menos un programa (por ejemplo «rodaje»).");
        }

        foreach (string app in apps.Where(a => !Identifiers.IsValid(a)))
        {
            errors.Add($"El programa «{app}» de apps no es un identificador válido.");
        }

        List<PackageItem> items = Items(root, directory, errors, warnings);
        ModuleInfo? module = null;
        if (kind == PackageKind.Module)
        {
            module = Module(root, directory, errors);
        }
        else if (root.TryGetProperty("module", out _))
        {
            warnings.Add("Un paquete de contenido no lleva «module»; se ignora.");
        }

        if (kind == PackageKind.Content && items.Count == 0 && errors.Count == 0)
        {
            warnings.Add("El paquete no trae ningún elemento que este programa entienda.");
        }

        if (errors.Count > 0)
        {
            return null;
        }

        return new PackageManifest
        {
            FormatVersion = format,
            Id = id!,
            Version = version!,
            Kind = kind,
            Name = name!,
            Description = description,
            Author = author!,
            License = license!,
            Repository = repository,
            Apps = apps,
            Items = items,
            Module = module,
        };
    }

    private static List<PackageItem> Items(JsonElement root, string? directory, List<string> errors, List<string> warnings)
    {
        var items = new List<PackageItem>();
        if (!root.TryGetProperty("items", out JsonElement array))
        {
            return items;
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            errors.Add("items debe ser una lista.");
            return items;
        }

        if (array.GetArrayLength() > MaxItems)
        {
            errors.Add($"El paquete tiene más de {MaxItems} elementos.");
            return items;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        int index = 0;
        foreach (JsonElement element in array.EnumerateArray())
        {
            index++;
            if (element.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"El elemento {index} de items no es un objeto.");
                continue;
            }

            var local = new List<string>();
            string? id = Text(element, "id", local);
            string where = id is null ? $"El elemento {index}" : $"El elemento «{id}»";
            if (id is not null && !Identifiers.IsValid(id))
            {
                local.Add($"el id «{id}» no es válido");
            }

            string? kindText = Text(element, "kind", local);
            string? name = Text(element, "name", local, SafeText.MaxName);
            string? file = OptionalText(element, "file", local);
            string? preview = OptionalText(element, "preview", local);
            foreach (string? relative in new[] { file, preview })
            {
                if (relative is not null && CheckRelativeFile(relative, directory) is { } problem)
                {
                    local.Add(problem);
                }
            }

            ItemKind? kind = kindText is null ? null : ParseItemKind(kindText);
            if (kindText is not null && kind is null)
            {
                if (kindText == "module")
                {
                    local.Add("«module» es el kind de un paquete, no de un elemento");
                }
                else
                {
                    warnings.Add($"{where} es de tipo «{kindText}», que este programa no conoce; se omite.");
                    continue;
                }
            }

            List<string>? fonts = kind == ItemKind.Font ? FontFiles(element, file, directory, local) : null;
            if (local.Count > 0)
            {
                errors.Add($"{where}: {string.Join("; ", local.Select(Uncapitalize))}.");
                continue;
            }

            if (!ids.Add(id!))
            {
                errors.Add($"El id de elemento «{id}» está repetido.");
                continue;
            }

            items.Add(new PackageItem(id!, kind!.Value, name!, file, OptionalText(element, "description", errors, SafeText.MaxDescription, multiline: true),
                OptionalText(element, "license", errors, SafeText.MaxLicense), preview, fonts));
        }

        return items;
    }

    /// <summary>
    /// Los archivos de una fuente: <c>files</c> (o <c>file</c> si es uno), de 1 a <see cref="PackageItem.MaxFontFiles"/>,
    /// con extensión de fuente, cada uno de hasta 20 MB y la familia de hasta 100 MB; y su <c>license</c>, obligatoria
    /// porque una fuente de terceros no tiene la licencia del paquete.
    /// </summary>
    private static List<string>? FontFiles(JsonElement element, string? file, string? directory, List<string> local)
    {
        if (!element.TryGetProperty("license", out JsonElement license) || license.ValueKind != JsonValueKind.String)
        {
            local.Add("una fuente necesita «license» con la licencia de la familia (por ejemplo OFL-1.1)");
        }

        List<string>? files = element.TryGetProperty("files", out _) ? Strings(element, "files", local, MaxShortText) : file is null ? null : [file];
        if (files is null)
        {
            if (file is null && !element.TryGetProperty("files", out _))
            {
                local.Add("una fuente necesita «files» con sus archivos .ttf u .otf");
            }

            return null;
        }

        if (files.Count is 0 || files.Count > PackageItem.MaxFontFiles)
        {
            local.Add($"una fuente lleva de 1 a {PackageItem.MaxFontFiles} archivos");
            return null;
        }

        long total = 0;
        foreach (string relative in files)
        {
            if (!PackageItem.FontExtensions.Contains(Path.GetExtension(relative), StringComparer.OrdinalIgnoreCase))
            {
                local.Add($"«{relative}» no es un archivo de fuente (.ttf, .otf, .ttc u .otc)");
            }
            else if (CheckRelativeFile(relative, directory) is { } problem)
            {
                local.Add(problem);
            }
            else if (directory is not null)
            {
                SafePaths.TryNormalizeRelative(relative, out string? normalized);
                long length = new FileInfo(Path.Combine(directory, normalized!)).Length;
                if (length > PackageItem.MaxFontFileBytes)
                {
                    local.Add($"«{relative}» pasa de 20 MB");
                }

                total += length;
            }
        }

        if (total > PackageItem.MaxFontFamilyBytes)
        {
            local.Add("la familia pasa de 100 MB");
        }

        return files;
    }

    private static ModuleInfo? Module(JsonElement root, string? directory, List<string> errors)
    {
        if (!root.TryGetProperty("module", out JsonElement module) || module.ValueKind != JsonValueKind.Object)
        {
            errors.Add("Un módulo necesita «module» con entry, protocol y functions.");
            return null;
        }

        string? entry = Text(module, "entry", errors);
        if (entry is not null && CheckRelativeFile(entry, directory) is { } problem)
        {
            errors.Add("module.entry: " + problem + ".");
        }

        int protocol = Integer(module, "protocol", errors) ?? 0;
        List<string> functions = Strings(module, "functions", errors, MaxShortText) ?? [];
        Models(module, errors);
        return entry is null ? null : new ModuleInfo(entry, protocol, functions);
    }

    /// <summary>
    /// <c>module.models</c>: cada uno con <c>id</c>, <c>file</c> (relativa; el archivo no viaja en el paquete, no se
    /// comprueba que exista), <c>sha256</c> en minúsculas, <c>size</c> en bytes mayor que 0, <c>license</c> y una
    /// <c>url</c> fijada (ver <see cref="IsPinnedModelUrl"/>). Sin tamaño ni dirección fijada, el gestor de complementos
    /// no puede bajarlo ni la distribución completa comprobarlo. <c>ModuleModels.Read</c> omite en silencio lo que no sirve;
    /// aquí es un error del manifiesto, para que quien publica el módulo lo vea al validarlo o instalarlo.
    /// </summary>
    private static void Models(JsonElement module, List<string> errors)
    {
        if (!module.TryGetProperty("models", out JsonElement array) || array.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            errors.Add("module.models debe ser una lista.");
            return;
        }

        if (array.GetArrayLength() > ModuleModels.MaxModels)
        {
            errors.Add($"module.models tiene más de {ModuleModels.MaxModels} modelos.");
            return;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int index = 0;
        foreach (JsonElement element in array.EnumerateArray())
        {
            index++;
            if (element.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"El modelo {index} de module.models no es un objeto.");
                continue;
            }

            var local = new List<string>();
            string? id = Text(element, "id", local);
            string where = id is null ? $"El modelo {index}" : $"El modelo «{id}»";
            if (id is not null && !Identifiers.IsValid(id))
            {
                local.Add($"el id «{id}» no es válido");
            }

            string? file = Text(element, "file", local);
            if (file is not null && CheckRelativeFile(file, null) is { } problem)
            {
                local.Add(problem);
            }

            string? sha256 = Text(element, "sha256", local);
            if (sha256 is not null && !SignedIndex.IsSha256(sha256))
            {
                local.Add("«sha256» debe tener 64 cifras hexadecimales en minúsculas");
            }

            if (!element.TryGetProperty("size", out JsonElement sizeElement))
            {
                local.Add("falta «size» (el tamaño exacto en bytes)");
            }
            else if (sizeElement.ValueKind != JsonValueKind.Number || !sizeElement.TryGetInt64(out long bytes) || bytes <= 0 || bytes > SignedIndex.MaxEntrySize)
            {
                local.Add("«size» debe ser el tamaño en bytes, un entero mayor que 0");
            }

            Text(element, "license", local, SafeText.MaxLicense);
            string? urlText = Text(element, "url", local, SafeText.MaxUri);
            if (urlText is not null && !IsPinnedModelUrl(urlText, out _))
            {
                local.Add($"la url «{urlText}» no está fijada: usa Hugging Face con …/resolve/<commit de 40 caracteres>/…, "
                    + "GitHub con el commit en la ruta (…/raw/<commit>/… o raw.githubusercontent.com/…/<commit>/…) o un asset de una release de GitHub");
            }

            if (local.Count > 0)
            {
                errors.Add($"{where} de module.models: {string.Join("; ", local.Select(Uncapitalize))}.");
                continue;
            }

            if (!ids.Add(id!))
            {
                errors.Add($"El id de modelo «{id}» está repetido.");
            }
            else if (!files.Add(file!))
            {
                errors.Add($"El modelo «{id}» repite el archivo «{file}».");
            }
        }
    }

    /// <summary>
    /// Si la dirección de un modelo apunta siempre al mismo contenido: https, sin consulta ni fragmento, y además
    /// Hugging Face fijado por commit (lo mismo que acepta <see cref="DownloadUrlPolicy"/>), un asset de una release de
    /// GitHub, o un archivo de GitHub con el commit de 40 caracteres en la ruta (<c>github.com/&lt;dueño&gt;/&lt;repo&gt;/raw/&lt;commit&gt;/…</c>
    /// o <c>raw.githubusercontent.com/&lt;dueño&gt;/&lt;repo&gt;/&lt;commit&gt;/…</c>). <c>resolve/main</c> o una rama no valen.
    /// </summary>
    /// <remarks>
    /// Una dirección http o https de este equipo (127.0.0.1, localhost) también se acepta: es la del servidor de pruebas,
    /// que solo se baja con <see cref="DownloadUrlPolicy.WithLoopback"/>; la política real la sigue rechazando.
    /// </remarks>
    public static bool IsPinnedModelUrl(string text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Uri? url)
    {
        url = null;
        if (Uri.TryCreate(text, UriKind.Absolute, out Uri? local) && local.IsLoopback
            && (local.Scheme == Uri.UriSchemeHttp || local.Scheme == Uri.UriSchemeHttps))
        {
            url = local;
            return true;
        }

        if (!SafeText.TryParseHttpsUri(text, out Uri? parsed) || parsed.Query.Length > 0 || parsed.Fragment.Length > 0)
        {
            return false;
        }

        string[] segments = parsed.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        bool pinned = parsed.Host.ToLowerInvariant() switch
        {
            "huggingface.co" or "github.com" when segments.Length > 0
                && DownloadUrlPolicy.ForOwners(segments[0]).IsAllowed(parsed, out _) => true,
            "github.com" => segments.Length >= 5 && segments[2] == "raw" && IsCommit(segments[3]),
            "raw.githubusercontent.com" => segments.Length >= 4 && IsCommit(segments[2]),
            _ => false,
        };
        if (pinned)
        {
            url = parsed;
        }

        return pinned;
    }

    private static bool IsCommit(string text) => text.Length == 40 && text.All(char.IsAsciiHexDigitLower);

    /// <summary>Tipo de elemento según su nombre en el manifiesto, o <c>null</c> si no se conoce.</summary>
    public static ItemKind? ParseItemKind(string text) => text switch
    {
        "transition" => ItemKind.Transition,
        "text-style" => ItemKind.TextStyle,
        "effect" => ItemKind.Effect,
        "filter" => ItemKind.Filter,
        "lut" => ItemKind.Lut,
        "template" => ItemKind.Template,
        "sound" => ItemKind.Sound,
        "font" => ItemKind.Font,
        "safe-zones" => ItemKind.SafeZones,
        _ => null,
    };

    public static string FormatItemKind(ItemKind kind) => kind switch
    {
        ItemKind.Transition => "transition",
        ItemKind.TextStyle => "text-style",
        ItemKind.Effect => "effect",
        ItemKind.Filter => "filter",
        ItemKind.Lut => "lut",
        ItemKind.Template => "template",
        ItemKind.Sound => "sound",
        ItemKind.Font => "font",
        ItemKind.SafeZones => "safe-zones",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Una ruta de archivo del manifiesto: relativa, con <c>/</c>, sin salir de la carpeta y, si se da la carpeta, existente.</summary>
    internal static string? CheckRelativeFile(string relative, string? directory)
    {
        if (!SafePaths.TryNormalizeRelative(relative, out string? normalized))
        {
            return $"la ruta «{relative}» no es relativa a la carpeta del paquete";
        }

        if (directory is not null)
        {
            string full = Path.GetFullPath(Path.Combine(directory, normalized));
            if (!SafePaths.IsInside(full, directory) || !File.Exists(full))
            {
                return $"falta el archivo «{relative}»";
            }
        }

        return null;
    }

    private static string MissingMessage(string property) => $"Falta «{property}».";

    private static string Uncapitalize(string s) => s.Length > 0 && char.IsUpper(s[0]) ? char.ToLowerInvariant(s[0]) + s[1..] : s;

    private static string? Text(JsonElement element, string property, List<string> errors, int maxLength = MaxShortText)
    {
        if (!element.TryGetProperty(property, out JsonElement value))
        {
            errors.Add(MissingMessage(property));
            return null;
        }

        string? text = value.ValueKind == JsonValueKind.String ? Limited(value.GetString()!, property, maxLength, multiline: false, errors) : null;
        if (value.ValueKind != JsonValueKind.String || text?.Length == 0)
        {
            errors.Add($"«{property}» debe ser un texto no vacío.");
            return null;
        }

        return text;
    }

    private static string? OptionalText(JsonElement element, string property, List<string> errors, int maxLength = MaxShortText, bool multiline = false)
    {
        if (!element.TryGetProperty(property, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            errors.Add($"«{property}» debe ser un texto.");
            return null;
        }

        string? text = Limited(value.GetString()!, property, maxLength, multiline, errors);
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>El texto sin controles ni marcas bidi, o <c>null</c> con un error si pasa del largo máximo.</summary>
    private static string? Limited(string raw, string property, int maxLength, bool multiline, List<string> errors)
    {
        string text = SafeText.Clean(raw, multiline);
        if (text.Length > maxLength)
        {
            errors.Add($"«{property}» pasa de {maxLength} caracteres.");
            return null;
        }

        return text;
    }

    private static int? Integer(JsonElement element, string property, List<string> errors)
    {
        if (!element.TryGetProperty(property, out JsonElement value))
        {
            errors.Add(MissingMessage(property));
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int number))
        {
            errors.Add($"«{property}» debe ser un número entero.");
            return null;
        }

        return number;
    }

    private static List<string>? Strings(JsonElement element, string property, List<string> errors, int maxLength = Identifiers.MaxLength)
    {
        if (!element.TryGetProperty(property, out JsonElement value))
        {
            errors.Add(MissingMessage(property));
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String))
        {
            errors.Add($"«{property}» debe ser una lista de textos.");
            return null;
        }

        List<string> texts = [.. value.EnumerateArray().Select(e => SafeText.Clean(e.GetString()!)).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal)];
        if (texts.Any(t => t.Length > maxLength))
        {
            errors.Add($"Un texto de «{property}» pasa de {maxLength} caracteres.");
            return null;
        }

        return texts;
    }
}
