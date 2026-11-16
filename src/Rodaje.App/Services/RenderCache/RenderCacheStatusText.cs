using Rodaje.App.Services.Format;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services.RenderCache;

/// <summary>Lo que dice la fila del render en caché en Trabajos.</summary>
public static class RenderCacheStatusText
{
    public static string Describe(RenderCacheStatus status, Rational rate)
    {
        ArgumentNullException.ThrowIfNull(status);
        string now = status.State switch
        {
            RenderCacheState.Working when status.Current is { } work =>
                string.Create(SpanishNumbers.Format, $"Calculando desde {Time.FromFrames(work.FirstFrame, rate).ToSeconds():0.#} s ({work.Count} cuadros)"),
            RenderCacheState.Paused => "En pausa mientras se reproduce o se mueve el cursor",
            RenderCacheState.Waiting => "Espera 3 s sin editar ni reproducir",
            RenderCacheState.Blocked => "Parado: el tope del caché o el disco libre no dejan calcular más",
            _ => "Nada pendiente",
        };
        var parts = new List<string> { now };
        if (status.Pending > 0)
        {
            parts.Add(status.Pending == 1 ? "1 trozo en cola" : $"{status.Pending} trozos en cola");
        }

        if (status.Completed > 0)
        {
            parts.Add(status.Completed == 1 ? "1 calculado" : $"{status.Completed} calculados");
        }

        if (status.Failed > 0)
        {
            parts.Add(status.Failed == 1 ? "1 falló" : $"{status.Failed} fallaron");
        }

        return string.Join(" · ", parts);
    }
}
