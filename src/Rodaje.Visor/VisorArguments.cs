using System.Globalization;

namespace Rodaje.Visor;

/// <summary>Línea de órdenes: <c>[archivo] [--prueba segundos] [--registro ruta] [--sin-audio] [--cpu]</c>.</summary>
internal sealed record VisorArguments
{
    public string? File { get; init; }

    /// <summary>Recorrido automático de esta duración que termina imprimiendo las estadísticas.</summary>
    public TimeSpan? TestDuration { get; init; }

    public string? LogPath { get; init; }

    public bool DisableAudio { get; init; }

    public bool ForceSoftwareDecoding { get; init; }

    public static VisorArguments Parse(IReadOnlyList<string> args)
    {
        var result = new VisorArguments();
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--prueba":
                    double seconds = 10;
                    if (i + 1 < args.Count && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                    {
                        seconds = parsed;
                        i++;
                    }

                    if (seconds is < 2 or > 3600)
                    {
                        throw new ArgumentException($"--prueba necesita entre 2 y 3600 segundos; se recibió {seconds}.");
                    }

                    result = result with { TestDuration = TimeSpan.FromSeconds(seconds) };
                    break;
                case "--registro":
                    if (i + 1 >= args.Count)
                    {
                        throw new ArgumentException("--registro necesita la ruta del archivo de registro.");
                    }

                    result = result with { LogPath = args[++i] };
                    break;
                case "--sin-audio":
                    result = result with { DisableAudio = true };
                    break;
                case "--cpu":
                    result = result with { ForceSoftwareDecoding = true };
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Opción desconocida: {arg}. Uso: Rodaje.Visor [archivo] [--prueba segundos] [--registro ruta] [--sin-audio] [--cpu]");
                    }

                    result = result with { File = arg };
                    break;
            }
        }

        if (result.TestDuration is not null && result.File is null)
        {
            throw new ArgumentException("--prueba necesita un archivo de video.");
        }

        return result;
    }
}
