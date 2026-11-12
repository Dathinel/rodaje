using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rodaje.Packages;

public static class PackageIntegrity
{
    public const string FileName = "integridad.json";
    private const int MaxBytes = 8 << 20;

    public static void Write(string directory)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Files(directory))
        {
            string relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            if (!SafePaths.IsInside(file, directory))
                throw new PackageInstallException($"No se puede registrar la integridad de «{relative}»: ruta no permitida.");
            hashes.Add(relative, PackageInstaller.ComputeSha256(file));
        }

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(hashes, IntegrityJsonContext.Default.DictionaryStringString);
        if (json.Length > MaxBytes) throw new PackageInstallException("El manifiesto de integridad supera el tamaño máximo.");
        using var stream = new FileStream(Path.Combine(directory, FileName), FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(json);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Si falta el manifiesto (paquete instalado por una versión anterior a esta comprobación) toma el estado
    /// actual como base; si existe, lo verifica. Los paquetes ya instalados siguen cargando tras actualizar
    /// la app y quedan protegidos a partir de ese momento: la base queda con lo que hubiera en disco.
    /// </summary>
    public static void Ensure(string directory)
    {
        if (File.Exists(Path.Combine(directory, FileName))) Verify(directory);
        else Write(directory);
    }

    public static void Verify(string directory)
    {
        try
        {
            using var stream = File.OpenRead(Path.Combine(directory, FileName));
            if (stream.Length > MaxBytes) throw new PackageInstallException("El manifiesto de integridad supera el tamaño máximo.");
            byte[] bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            var parsed = JsonSerializer.Deserialize(bytes, IntegrityJsonContext.Default.DictionaryStringString)
                ?? throw new PackageInstallException("El manifiesto de integridad está vacío.");
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in parsed)
            {
                if (!SafePaths.TryNormalizeRelative(pair.Key, out string? relative) || !hashes.TryAdd(relative, pair.Value)
                    || !Download.SignedIndex.IsSha256(pair.Value))
                    throw new PackageInstallException("El manifiesto de integridad contiene una entrada no válida.");
                string path = Path.Combine(directory, relative);
                if (!SafePaths.IsInside(path, directory) || !File.Exists(path)
                    || !string.Equals(PackageInstaller.ComputeSha256(path), pair.Value, StringComparison.Ordinal))
                    throw new PackageInstallException($"Falló la integridad de «{relative}»: falta o su SHA-256 no coincide. Reinstala el paquete.");
            }

            // Los modelos que declara paquete.json llegan después de instalar (descarga aparte) y los comprueba quien los
            // carga contra el SHA-256 de ese manifiesto, que sí está cubierto: no cuentan como archivos sobrantes.
            HashSet<string> models = DeclaredModels(directory);
            foreach (string file in Files(directory))
            {
                string relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
                if (!hashes.ContainsKey(relative) && !models.Contains(relative))
                    throw new PackageInstallException($"El archivo «{relative}» falta del manifiesto de integridad. Reinstala el paquete.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new PackageInstallException("No se pudo verificar integridad.json. Reinstala el paquete: " + ex.Message, ex);
        }
    }

    private static HashSet<string> DeclaredModels(string directory)
    {
        var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string manifest = Path.Combine(directory, PackageManifest.FileName);
        if (File.Exists(manifest))
        {
            foreach (Download.ModuleModel model in Download.ModuleModels.Read(manifest)) models.Add(model.File);
        }

        return models;
    }

    private static IEnumerable<string> Files(string directory) => Directory.EnumerateFiles(directory, "*",
        new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false })
        .Where(file => !string.Equals(Path.GetFullPath(file), Path.GetFullPath(Path.Combine(directory, FileName)), StringComparison.OrdinalIgnoreCase));
}

[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class IntegrityJsonContext : JsonSerializerContext;
