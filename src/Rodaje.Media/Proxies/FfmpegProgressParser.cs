using System.Globalization;

namespace Rodaje.Media.Proxies;

/// <summary>Un bloque de <c>-progress</c> de ffmpeg.</summary>
/// <param name="Frame">Cuadros escritos hasta ahora.</param>
/// <param name="OutTimeMicroseconds">Marca de salida en microsegundos (con <c>-copyts</c> incluye el desplazamiento inicial).</param>
/// <param name="Ended">Es el último bloque (<c>progress=end</c>).</param>
public readonly record struct FfmpegProgressSnapshot(long Frame, long? OutTimeMicroseconds, bool Ended);

/// <summary>Lee la salida de <c>ffmpeg -progress pipe:1</c>: pares <c>clave=valor</c> que cierra <c>progress=continue|end</c>.</summary>
public sealed class FfmpegProgressParser
{
    private long _frame;
    private long? _outTime;

    /// <summary>Último bloque completo.</summary>
    public FfmpegProgressSnapshot Last { get; private set; }

    /// <summary>Procesa una línea; devuelve <c>true</c> cuando cierra un bloque, que queda en <paramref name="snapshot"/>.</summary>
    public bool Feed(string? line, out FfmpegProgressSnapshot snapshot)
    {
        snapshot = Last;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        int equals = line.IndexOf('=', StringComparison.Ordinal);
        if (equals <= 0)
        {
            return false;
        }

        string key = line[..equals].Trim();
        string value = line[(equals + 1)..].Trim();
        switch (key)
        {
            case "frame" when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long frame):
                _frame = frame;
                break;
            case "out_time_us" when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long us):
                _outTime = us;
                break;
            case "progress":
                Last = snapshot = new FfmpegProgressSnapshot(_frame, _outTime, value == "end");
                return true;
        }

        return false;
    }
}
