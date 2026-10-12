using Rodaje.Packages;
using Rodaje.Packages.Content;

namespace Rodaje.App.Services.Effects;

/// <summary>
/// Lee el contenido de los paquetes instalados por su referencia <c>paquete@versión/elemento</c>. Devuelve <c>null</c> si
/// el paquete o el elemento no están; si el archivo está dañado lanza <see cref="PackageContentException"/>. Se llama
/// fuera del hilo de la interfaz y del de render.
/// </summary>
public interface IContentLoader
{
    LutTable? LoadLut(string reference);

    /// <summary>Un <c>.cube</c> suelto («Cargar .cube…»).</summary>
    /// <exception cref="PackageContentException">No se pudo leer.</exception>
    LutTable LoadLutFile(string path);

    TransitionPreset? LoadTransition(string reference);

    FilterPreset? LoadFilter(string reference);

    EffectPreset? LoadEffect(string reference);

    /// <summary>Un estilo de título; se copia al clip al aplicarlo (D-029), el motor nunca lo busca.</summary>
    TextStylePreset? LoadTextStyle(string reference);
}

/// <summary>
/// <see cref="IContentLoader"/> sobre lo instalado (<see cref="PackageScan"/>) y los lectores de <c>Rodaje.Packages</c>.
/// El paquete se busca por id y el elemento por id y tipo; una versión instalada distinta de la referida también sirve.
/// </summary>
public sealed class InstalledContentLoader(Func<PackageScan> installed) : IContentLoader
{
    private readonly Func<PackageScan> _installed = installed ?? throw new ArgumentNullException(nameof(installed));

    public LutTable? LoadLut(string reference) => Read(reference, ItemKind.Lut, ContentReaders.ReadCube);

    public LutTable LoadLutFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            using FileStream stream = OpenLimited(path);
            return ContentReaders.ReadCube(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PackageContentException($"No se pudo abrir «{Path.GetFileName(path)}»: {ex.Message}", ex);
        }
    }

    public TransitionPreset? LoadTransition(string reference) => Read(reference, ItemKind.Transition, ContentReaders.ReadTransition);

    /// <summary>Un filtro, con sus opciones <c>./elemento</c> (la LUT de su paquete) ya como referencias completas.</summary>
    public FilterPreset? LoadFilter(string reference) =>
        Read(reference, ItemKind.Filter, ContentReaders.ReadFilter) is { } filter && Owner(reference) is { } owner
            ? filter with { Effects = PackageContentStore.ResolveLocalReferences(filter.Effects, owner) }
            : null;

    /// <summary>Un efecto animado, con sus opciones <c>./elemento</c> ya como referencias completas.</summary>
    public EffectPreset? LoadEffect(string reference) =>
        Read(reference, ItemKind.Effect, ContentReaders.ReadEffect) is { } effect && Owner(reference) is { } owner
            ? effect with { Effects = PackageContentStore.ResolveLocalReferences(effect.Effects, owner) }
            : null;

    public TextStylePreset? LoadTextStyle(string reference) => Read(reference, ItemKind.TextStyle, ContentReaders.ReadTextStyle);

    /// <summary>El archivo de un elemento instalado de ese tipo, o <c>null</c> si falta.</summary>
    public string? FindFile(string reference, ItemKind kind)
    {
        if (!PackageReference.TryParse(reference, out PackageReference? parsed)
            || _installed().Find(parsed.PackageId) is not { } package
            || package.Manifest.Items.FirstOrDefault(i => i.Id == parsed.ItemId && i.Kind == kind) is not { } item)
        {
            return null;
        }

        return new PackageItemEntry(package, item).FilePath;
    }

    private PackageManifest? Owner(string reference) =>
        PackageReference.TryParse(reference, out PackageReference? parsed) ? _installed().Find(parsed.PackageId)?.Manifest : null;

    private T? Read<T>(string reference, ItemKind kind, Func<Stream, T> reader)
        where T : class
    {
        if (FindFile(reference, kind) is not { } path || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using FileStream stream = OpenLimited(path);
            return reader(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PackageContentException($"No se pudo abrir «{reference}»: {ex.Message}", ex);
        }
    }

    private static FileStream OpenLimited(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        if (stream.Length > LutTable.MaxFileBytes)
        {
            stream.Dispose();
            throw new PackageContentException($"«{Path.GetFileName(path)}» pasa de {LutTable.MaxFileBytes / (1024 * 1024)} MB.");
        }

        return stream;
    }
}
