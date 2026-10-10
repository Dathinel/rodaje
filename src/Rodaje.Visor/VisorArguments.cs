using System.Globalization;
using Rodaje.Engine.Timeline;

namespace Rodaje.Visor;

/// <summary>Línea de órdenes: <c>[archivo...] [--prueba segundos] [--registro ruta] [--sin-audio] [--cpu] [--proxies [siempre]]</c>.</summary>
/// <remarks>Los archivos son videos (el primero de fondo, los demás como imagen en imagen) o un solo proyecto <c>.rodaje</c>.</remarks>
internal sealed record VisorArguments
{
    private const string Usage = "Uso: Rodaje.Visor [video...|proyecto.rodaje] [--prueba segundos] [--registro ruta] [--sin-audio] [--cpu] [--proxies [siempre]]";

    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>Recorrido automático de esta duración que termina imprimiendo las estadísticas.</summary>
    public TimeSpan? TestDuration { get; init; }

    public string? LogPath { get; init; }

    public bool DisableAudio { get; init; }

    public bool ForceSoftwareDecoding { get; init; }

    /// <summary>Con <c>--proxies</c> la vista previa usa (y genera) proxies donde la política los recomienda; con <c>siempre</c>, todos los listos.</summary>
    public ProxyUsage Proxies { get; init; } = ProxyUsage.Never;

    public static VisorArguments Parse(IReadOnlyList<string> args)
    {
        var result = new VisorArguments();
        var files = new List<string>();
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
                case "--proxies":
                    bool always = i + 1 < args.Count && args[i + 1].Equals("siempre", StringComparison.OrdinalIgnoreCase);
                    i += always ? 1 : 0;
                    result = result with { Proxies = always ? ProxyUsage.Always : ProxyUsage.Auto };
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Opción desconocida: {arg}. {Usage}");
                    }

                    if (!string.IsNullOrWhiteSpace(arg))
                    {
                        files.Add(arg);
                    }

                    break;
            }
        }

        if (result.TestDuration is not null && files.Count == 0)
        {
            throw new ArgumentException("--prueba necesita al menos un video o un proyecto.");
        }

        return result with { Files = files };
    }
}
