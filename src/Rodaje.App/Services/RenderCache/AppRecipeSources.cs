using System.Collections.Concurrent;
using Rodaje.App.Services.Effects;
using Rodaje.Engine.RenderCache;
using Rodaje.Engine.Timeline;
using Rodaje.Media.Probe;
using Rodaje.Media.RenderCache;
using Rodaje.Playback.Composition;

namespace Rodaje.App.Services.RenderCache;

/// <summary>
/// Lo que la receta lee de fuera en la app: la huella de contenido y el formato de cada medio, la identidad y el estado de
/// cada recurso y si están las fuentes de un título. Se puede usar desde varios hilos (el cálculo de fondo y la exportación).
/// </summary>
internal sealed class AppRecipeSources(IEffectContent? content, Func<GeneratedContent, bool> fontMissing, string chain)
{
    private readonly ConcurrentDictionary<string, (long Length, DateTime Written, RecipeMedia? Media)> _media = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (long Length, DateTime Written, string? Hash)> _files = new(StringComparer.OrdinalIgnoreCase);

    public string Chain => chain;

    public RecipeBuilderOptions Options(string? projectDirectory) => new()
    {
        ProjectDirectory = projectDirectory,
        Content = content,
        Chain = chain,
        Media = Media,
        Resources = key => Resource(key, projectDirectory),
        Fonts = c => fontMissing(c) ? null : c.Style.Font.Family + "\0" + c.Style.Font.Source,
    };

    private RecipeMedia? Media(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            return null;
        }

        if (_media.TryGetValue(path, out var known) && known.Length == file.Length && known.Written == file.LastWriteTimeUtc)
        {
            return known.Media;
        }

        RecipeMedia? media = null;
        if (ContentFingerprint.Of(path) is { } hash && Probe(path) is { } video)
        {
            string format = $"{video.PixelFormat}:{video.BitDepth}:{video.Color.Matrix}:{video.Color.FullRange}:{video.Color.Bt2020Primaries}:{video.Color.Transfer}:{video.HdrTransfer}:{video.Width}x{video.Height}";
            media = new RecipeMedia(new RecipeSource(hash, format), video.SampleAspectRatio, video.Rotation);
        }

        _media[path] = (file.Length, file.LastWriteTimeUtc, media);
        return media;
    }

    private static VideoStreamInfo? Probe(string path)
    {
        try
        {
            return MediaProbe.Probe(path).Video;
        }
#pragma warning disable CA1031 // Un medio que no se lee no tiene receta: el cuadro se queda sin caché.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    /// <summary>
    /// Un archivo (<c>file:</c> más su ruta: LUT suelta, máscara FFV1, imagen) entra por su huella de contenido; un elemento
    /// de paquete (<c>paquete@versión/elemento</c>), por su referencia si ya está cargado; el texto de una forma o de unas
    /// curvas es su propia identidad. Lo que falta o aún carga no tiene identidad: el cuadro se queda sin caché.
    /// </summary>
    internal string? Resource(ResourceKey key, string? projectDirectory)
    {
        string reference = key.Reference;
        if (LutReference.IsFile(reference))
        {
            return LutReference.ResolveFile(reference, projectDirectory) is { } file ? FileHash(file) : null;
        }

        if (global::Rodaje.Packages.PackageReference.TryParse(reference, out _))
        {
            return content is not null && content.TryGetLut(key, out LutData? lut) && lut is not null ? reference : null;
        }

        if (reference.IndexOfAny(Path.GetInvalidPathChars()) < 0 && LooksLikeFile(reference))
        {
            try
            {
                return FileHash(Path.IsPathRooted(reference) ? reference : Path.Combine(projectDirectory ?? Environment.CurrentDirectory, reference));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }

        return reference;
    }

    private string? FileHash(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            return null;
        }

        if (_files.TryGetValue(path, out var known) && known.Length == file.Length && known.Written == file.LastWriteTimeUtc)
        {
            return known.Hash;
        }

        string? hash = ContentFingerprint.Of(path);
        _files[path] = (file.Length, file.LastWriteTimeUtc, hash);
        return hash;
    }

    // Los vértices de una forma («0.1,0.2 0.3,0.4») también tienen «extensión»: solo cuentan las de archivos de verdad.
    private static bool LooksLikeFile(string reference) => FileExtensions.Contains(Path.GetExtension(reference));

    private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".nut", ".mov", ".mp4", ".avi", ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".webp", ".exr", ".cube", ".3dl",
    };
}
