using Rodaje.Media.Export;

namespace Rodaje.Media.Tests.Export;

/// <summary>
/// El ffmpeg falso de <c>tests/Rodaje.FakeFfmpeg</c>, que queda junto a las pruebas por la referencia del proyecto.
/// Lee la entrada estándar hasta el final y sale con el código pedido, o se queda dormido hasta que lo maten.
/// </summary>
internal static class FakeFfmpeg
{
    /// <summary>
    /// Opción del falso: al terminar la entrada estándar no sale (un ffmpeg.exe que tarda en cerrar el contenedor).
    /// Sale solo con el código <see cref="HangExitCode"/> a los dos minutos o cuando muere el proceso de las pruebas
    /// (<see cref="ParentVariable"/>), para no quedar huérfano si una prueba falla antes de matarlo.
    /// </summary>
    public const string Hang = "-falso-colgar";

    /// <summary>Opción del falso: el código con el que sale al terminar la entrada estándar.</summary>
    public const string ExitCode = "-falso-salida";

    /// <summary>Código con el que sale el falso colgado cuando deja de esperar sin que lo maten.</summary>
    public const int HangExitCode = 124;

    /// <summary>Variable de entorno con el id del proceso de las pruebas; el falso la hereda y sale si ese proceso muere.</summary>
    public const string ParentVariable = "RODAJE_FALSO_PADRE";

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

        Environment.SetEnvironmentVariable(ParentVariable, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

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
