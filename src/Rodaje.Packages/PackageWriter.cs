using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Rodaje.Packages;

/// <summary>Lo que lleva el <c>.paquete</c> de un solo elemento que escribe <see cref="PackageWriter"/>.</summary>
public sealed record SingleItemPackage(string Id, PackageVersion Version, string Name, string Author, string License,
    string AppId, string ItemId, ItemKind ItemKind, string ItemName, string File);

/// <summary>
/// Escribe un <c>.paquete</c> de un elemento con el orden de entradas y la fecha fijos: las mismas fuentes dan los mismos
/// bytes («Exportar…» de una plantilla propia).
/// </summary>
public static class PackageWriter
{
    private static readonly DateTimeOffset FixedDate = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <exception cref="ArgumentException">Un id o la ruta del archivo no valen para un paquete.</exception>
    public static void Write(Stream output, SingleItemPackage package, ReadOnlySpan<byte> content)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(package);
        if (!Identifiers.IsValid(package.Id) || !Identifiers.IsValid(package.ItemId) || !Identifiers.IsValid(package.AppId)
            || package.Id == PackageLibrary.OwnPackageId)
        {
            throw new ArgumentException("El id del paquete, del elemento o del programa no es válido.", nameof(package));
        }

        if (!SafePaths.TryNormalizeRelative(package.File, out string? file)
            || file.Equals(PackageManifest.FileName, StringComparison.OrdinalIgnoreCase) || file.Equals(PackageIntegrity.FileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"«{package.File}» no es una ruta relativa dentro del paquete.", nameof(package));
        }

        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
        Add(zip, PackageManifest.FileName, Manifest(package, file));
        Add(zip, file, content);
    }

    public static byte[] Write(SingleItemPackage package, ReadOnlySpan<byte> content)
    {
        using var buffer = new MemoryStream();
        Write(buffer, package, content);
        return buffer.ToArray();
    }

    private static void Add(ZipArchive zip, string name, ReadOnlySpan<byte> bytes)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = FixedDate;
        using Stream stream = entry.Open();
        stream.Write(bytes);
    }

    private static byte[] Manifest(SingleItemPackage package, string file)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            json.WriteNumber("formatVersion", 1);
            json.WriteString("id", package.Id);
            json.WriteString("version", package.Version.ToString());
            json.WriteString("kind", "content");
            json.WriteString("name", package.Name);
            json.WriteString("author", package.Author);
            json.WriteString("license", package.License);
            json.WriteStartArray("apps");
            json.WriteStringValue(package.AppId);
            json.WriteEndArray();
            json.WriteStartArray("items");
            json.WriteStartObject();
            json.WriteString("id", package.ItemId);
            json.WriteString("kind", ManifestReader.FormatItemKind(package.ItemKind));
            json.WriteString("name", package.ItemName);
            json.WriteString("file", file);
            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteEndObject();
        }

        return buffer.ToArray();
    }
}
