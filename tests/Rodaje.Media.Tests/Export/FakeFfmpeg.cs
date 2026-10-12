using Rodaje.Media.Export;

namespace Rodaje.Media.Tests.Export;

/// <summary>
/// El ffmpeg falso de <c>testing/Rodaje.FakeFfmpeg</c>, que queda junto a las pruebas por la referencia del proyecto.
/// Lee la entrada estándar hasta el final y sale con el código pedido, o se queda dormido hasta que lo maten.
/// </summary>
internal static class FakeFfmpeg
{
    /// <summary>Opción del falso: al terminar la entrada estándar no sale (un ffmpeg.exe que tarda en cerrar el contenedor).</summary>
    public const string Hang = "-falso-colgar";

    /// <summary>Opción del falso: el código con el que sale al terminar la entrada estándar.</summary>
    public const string ExitCode = "-falso-salida";

    private static readonly Lazy<string> Executable = new(Locate);

    public static string Path => Executable.Value;

    /// <summary>Un sink que lanza el falso sin la verificación de <see cref="Proxies.FfmpegExecutable"/>.</summary>
    public static FfmpegPipeSink Sink(FfmpegPipeSinkOptions? options = null) => new(Path, options);

    private static string Locate()
    {
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Rodaje.FakeFfmpeg.exe" : "Rodaje.FakeFfmpeg");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Falta el ffmpeg falso junto a las pruebas: {path}.", path);
        }

        // El lanzador del falso busca el runtime de .NET en DOTNET_ROOT; si no está (dotnet instalado fuera del lugar
        // por omisión), se le da el mismo runtime que corre las pruebas. El proceso hijo hereda la variable.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_ROOT")))
        {
            // .../shared/Microsoft.NETCore.App/<versión>/System.Private.CoreLib.dll
            string? root = System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location);
            for (int i = 0; i < 3 && root is not null; i++)
            {
                root = System.IO.Path.GetDirectoryName(root);
            }

            if (root is not null)
            {
                Environment.SetEnvironmentVariable("DOTNET_ROOT", root);
            }
        }

        return path;
    }
}
