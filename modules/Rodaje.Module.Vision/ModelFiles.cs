using System.Security.Cryptography;

namespace Rodaje.Module.Vision;

/// <summary>Un modelo con su huella fijada (la misma de <c>paquete.json</c> y de <c>build\preparar-modelos.ps1</c>).</summary>
/// <param name="FreeDimensions">Nombres simbólicos de las dimensiones de la entrada que se fijan por sesión («» = ya fija).</param>
internal sealed record ModelFile(string FileName, string Sha256, long Bytes, bool NoGraphFusion = false, string[]? FreeDimensions = null);

/// <summary>
/// Dónde están los modelos. Se buscan, en orden: <c>RODAJE_MODELOS</c>, <c>modelos\</c> junto al exe, el almacén compartido
/// <c>%LOCALAPPDATA%\Rodaje\complementos\modelos</c> (por nombre o por huella), <c>RODAJE_ENTORNO\modelos</c> y, al correr
/// desde el repositorio, el primer <c>.entorno\modelos</c> hacia arriba. Cada archivo se comprueba con SHA-256 una vez.
/// </summary>
internal sealed class ModelFiles
{
    public static readonly ModelFile Modnet = new("modnet.onnx", "07c308cf0fc7e6e8b2065a12ed7fc07e1de8febb7dc7839d7b7f15dd66584df9", 25_888_640,
        FreeDimensions: ["batch_size", "", "height", "width"]);
    public static readonly ModelFile BiRefNetLite = new("birefnet_lite_fp16.onnx", "d39b897ceb16ae654c1731f3dba0cf9b368d9cae74b5a57459b455cc8bfec402", 114_538_221, NoGraphFusion: true);
    public static readonly ModelFile YuNet = new("face_detection_yunet_2023mar.onnx", "8f2383e4dd3cfbb4553ea8718107fc0423210dc964f9f4280604804ed2552fa4", 232_589);
    public static readonly ModelFile RtDetr = new("rtdetr_v2_r18vd.onnx", "583a236ac21c95a7fd94f284fc21485e42355bfef82c27011ba78fbc09ee87e2", 81_057_510,
        FreeDimensions: ["batch_size", "", "height", "width"]);

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
                ? $"Falta el modelo {model.FileName}. Reinstala el módulo o ejecuta build\\preparar-modelos.ps1."
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
