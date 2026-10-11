using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Rodaje.Render.Shaders;

/// <summary>Un shader concreto: fuente, punto de entrada, perfil y las definiciones que eligen la variante.</summary>
/// <param name="Defines">Nombres que se definen a 1 antes de la fuente (por ejemplo <c>COLOR</c>).</param>
public sealed record ShaderVariant(string Source, string EntryPoint, string Profile, string SourceName, params string[] Defines)
{
    /// <summary>Fuente completa con las definiciones delante.</summary>
    public string FullSource()
    {
        var text = new StringBuilder();
        foreach (string define in Defines)
        {
            text.Append("#define ").Append(define).Append(" 1\n");
        }

        return text.Append(Source).ToString();
    }

    public override string ToString() => Defines.Length == 0 ? EntryPoint : $"{EntryPoint}[{string.Join(',', Defines)}]";
}

/// <summary>
/// Bytecode de shaders compilados: en memoria, en disco (<c>&lt;carpeta&gt;\&lt;sha256&gt;.cso</c>) y, si falta, con
/// <see cref="ShaderCompiler"/>. La clave es el hash de la fuente, la variante y las opciones del compilador, así un
/// cambio de código nunca usa un archivo viejo.
/// </summary>
public sealed class ShaderCache
{
    private const string KeyVersion = "rodaje-shaders-1;O3;WX";
    private static readonly Lazy<ShaderCache> s_default = new(() => new ShaderCache(DefaultDirectory));
    private readonly ConcurrentDictionary<string, byte[]> _memory = new(StringComparer.Ordinal);
    private int _compiled;
    private int _loaded;

    /// <param name="directory">Carpeta de los <c>.cso</c>; <c>null</c> solo guarda en memoria.</param>
    public ShaderCache(string? directory)
    {
        Directory = directory;
    }

    /// <summary>Caché del proceso en <see cref="DefaultDirectory"/>.</summary>
    public static ShaderCache Default => s_default.Value;

    /// <summary><c>%LOCALAPPDATA%\Rodaje\cache\shaders</c>, o <c>RODAJE_DATOS\cache\shaders</c> si esa variable existe.</summary>
    public static string DefaultDirectory
    {
        get
        {
            string? data = Environment.GetEnvironmentVariable("RODAJE_DATOS");
            string root = string.IsNullOrWhiteSpace(data)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rodaje")
                : data;
            return Path.Combine(root, "cache", "shaders");
        }
    }

    public string? Directory { get; }

    /// <summary>Variantes compiladas en esta sesión (para diagnóstico y pruebas).</summary>
    public int CompiledCount => _compiled;

    /// <summary>Variantes leídas del disco en esta sesión.</summary>
    public int LoadedFromDiskCount => _loaded;

    /// <summary>Para las pruebas: corre antes de cada compilación (por ejemplo, para retenerla).</summary>
    internal Action? BeforeCompile { get; init; }

    public static string Key(ShaderVariant variant)
    {
        ArgumentNullException.ThrowIfNull(variant);
        string text = string.Join('\n', KeyVersion, variant.EntryPoint, variant.Profile, variant.FullSource());
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>El bytecode si ya está en memoria. Nunca compila ni lee el disco: es lo que usa un cuadro.</summary>
    public bool TryGet(ShaderVariant variant, out byte[]? code) => _memory.TryGetValue(Key(variant), out code);

    /// <summary>Devuelve el bytecode; lo lee del disco o lo compila si hace falta (puede tardar decenas de ms).</summary>
    /// <exception cref="RenderException">El shader no compila.</exception>
    public byte[] GetOrCompile(ShaderVariant variant)
    {
        string key = Key(variant);
        if (_memory.TryGetValue(key, out byte[]? code))
        {
            return code;
        }

        string? path = Directory is null ? null : Path.Combine(Directory, key + ".cso");
        if (path is not null && TryRead(path, out code))
        {
            Interlocked.Increment(ref _loaded);
            return _memory.GetOrAdd(key, code!);
        }

        BeforeCompile?.Invoke();
        code = ShaderCompiler.Compile(variant.FullSource(), variant.EntryPoint, variant.Profile, variant.SourceName);
        Interlocked.Increment(ref _compiled);
        if (path is not null)
        {
            TryWrite(path, code);
        }

        return _memory.GetOrAdd(key, code);
    }

    private static bool TryRead(string path, out byte[]? code)
    {
        try
        {
            code = File.Exists(path) ? File.ReadAllBytes(path) : null;
            return code is { Length: > 0 };
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        code = null;
        return false;
    }

    // Un fallo al escribir solo cuesta compilar otra vez la próxima sesión.
    private static void TryWrite(string path, byte[] code)
    {
        string temporary = $"{path}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.tmp";
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(temporary, code);
            File.Move(temporary, path, overwrite: true);
        }
        catch (IOException)
        {
            TryDelete(temporary);
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(temporary);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
