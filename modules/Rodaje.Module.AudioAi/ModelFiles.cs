using System.Security.Cryptography;

namespace Rodaje.Module.AudioAi;

/// <summary>Un modelo con su huella fijada (la misma de <c>paquete.json</c> y de <c>herramientas\preparar-modelos.ps1</c>).</summary>
internal sealed record ModelFile(string FileName, string Sha256, long Bytes);

/// <summary>
/// Dónde están los modelos. Se buscan, en orden: <c>RODAJE_MODELOS</c>, <c>modelos\</c> junto al exe, el almacén compartido
/// <c>%LOCALAPPDATA%\Rodaje\complementos\modelos</c> (por nombre o por huella), <c>RODAJE_ENTORNO\modelos</c> y, al correr
/// desde el repositorio, el primer <c>.entorno\modelos</c> hacia arriba. Cada archivo se comprueba con SHA-256 una vez:
/// un modelo cambiado o cortado no llega a ONNX Runtime.
/// </summary>
internal sealed class ModelFiles
{
    public static readonly ModelFile SileroVad = new("silero_vad.onnx", "1a153a22f4509e292a94e67d6f9b85e8deb25b4988682b7e174c65279d8788e3", 2_327_524);
    public static readonly ModelFile DfnEncoder = new("deepfilternet3-enc.onnx", "7c5399d3da8a50ebef1c1a0ae421b33376aa5e45d0e92df16da7e83c9c131916", 1_954_042);
    public static readonly ModelFile DfnErbDecoder = new("deepfilternet3-erb_dec.onnx", "ab669a1d10afe20911728b33053a452071042317a90581092b325da7b2f9d895", 3_292_397);
    public static readonly ModelFile DfnDfDecoder = new("deepfilternet3-df_dec.onnx", "23114ce3b0f6464b763ee62f7bb8aab6b2a129a21eabd5bcfe59413db05f278a", 3_340_803);

    private readonly IReadOnlyList<string> _folders;
    private readonly Dictionary<string, string> _verified = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public ModelFiles(IReadOnlyList<string>? folders = null) => _folders = folders ?? DefaultFolders();

    public IReadOnlyList<string> Folders => _folders;

    /// <summary>La ruta del modelo, comprobada.</summary>
    /// <exception cref="ModelMissingException">No está en ninguna carpeta o su huella no coincide.</exception>
    public string Path(ModelFile model)
    {
        ArgumentNullException.ThrowIfNull(model);
        lock (_gate)
        {
            if (_verified.TryGetValue(model.FileName, out string? known))
            {
                return known;
            }

            string? mismatch = null;
            foreach (string folder in _folders)
            {
                foreach (string candidate in new[] { System.IO.Path.Combine(folder, model.FileName), System.IO.Path.Combine(folder, model.Sha256) })
                {
                    var info = new FileInfo(candidate);
                    if (!info.Exists || info.Length != model.Bytes)
                    {
                        continue;
                    }

                    if (Hash(candidate) == model.Sha256)
                    {
                        _verified[model.FileName] = candidate;
                        return candidate;
                    }

                    mismatch = candidate;
                }
            }

            throw new ModelMissingException(mismatch is null
                ? $"Falta el modelo {model.FileName}. Reinstala el módulo o ejecuta herramientas\\preparar-modelos.ps1."
                : $"El modelo {mismatch} está dañado (su huella no coincide). Reinstala el módulo.");
        }
    }

    public static IReadOnlyList<string> DefaultFolders()
    {
        var folders = new List<string>();
        if (Environment.GetEnvironmentVariable("RODAJE_MODELOS") is { Length: > 0 } custom)
        {
            folders.Add(custom);
        }

        folders.Add(System.IO.Path.Combine(AppContext.BaseDirectory, "modelos"));
        folders.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rodaje", "complementos", "modelos"));
        if (Environment.GetEnvironmentVariable("RODAJE_ENTORNO") is { Length: > 0 } entorno)
        {
            folders.Add(System.IO.Path.Combine(entorno, "modelos"));
        }

        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = System.IO.Path.Combine(dir.FullName, ".entorno", "modelos");
            if (Directory.Exists(candidate))
            {
                folders.Add(candidate);
                break;
            }
        }

        return folders;
    }

    private static string Hash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}

/// <summary>Falta un modelo o está dañado; el módulo lo informa como error de la llamada.</summary>
internal sealed class ModelMissingException(string message) : Exception(message);
