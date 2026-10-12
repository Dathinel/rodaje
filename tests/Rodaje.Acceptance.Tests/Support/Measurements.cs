using System.Globalization;
using Rodaje.Playback;
using Xunit.Abstractions;

namespace Rodaje.Acceptance.Tests.Support;

/// <summary>Cifras con un formato fijo que build\aceptacion.ps1 lee del .trx: <c>MEDIDA clave = valor</c>.</summary>
internal static class Measurements
{
    public static void Report(this ITestOutputHelper output, string key, FormattableString value) =>
        output.WriteLine("MEDIDA " + key + " = " + value.ToString(CultureInfo.InvariantCulture));
}

/// <summary>
/// <see cref="DecodeThread"/> que sabe cuándo no le queda trabajo. Permite avanzar el reloj tan rápido como
/// decodifica el hilo real, sin depender del reloj de pared.
/// </summary>
internal sealed class QuiescentExecutor : IDecodeExecutor, IDisposable
{
    private readonly DecodeThread _thread = new("Rodaje aceptación");
    private readonly object _gate = new();
    private int _pending;

    public void Post(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            _pending++;
        }

        _thread.Post(() =>
        {
            try
            {
                work();
            }
            finally
            {
                lock (_gate)
                {
                    _pending--;
                    Monitor.PulseAll(_gate);
                }
            }
        });
    }

    /// <summary>Espera a que el hilo termine todo lo encolado, incluido lo que encole mientras trabaja.</summary>
    public void WaitIdle(TimeSpan timeout)
    {
        DateTime limit = DateTime.UtcNow + timeout;
        lock (_gate)
        {
            while (_pending > 0)
            {
                TimeSpan left = limit - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !Monitor.Wait(_gate, left))
                {
                    throw new TimeoutException(string.Create(CultureInfo.InvariantCulture,
                        $"El hilo de decodificación sigue ocupado después de {timeout.TotalSeconds:0} s."));
                }
            }
        }
    }

    public void Dispose() => _thread.Dispose();
}
