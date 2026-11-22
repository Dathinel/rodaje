using System.Diagnostics;

namespace Rodaje.App.Views.Player;

/// <summary>
/// Si el visor rehace su dispositivo tras un fallo, sin WinUI. Solo cuando la GPU se perdió de verdad (TDR, controlador que
/// se actualiza o se cae: <c>GetDeviceRemovedReason</c> falla); cualquier otro error sigue parando el visor. Como mucho
/// <see cref="MaxRecoveries"/> veces por <see cref="Window"/>: una GPU que se cae en bucle no deja al hilo rehaciendo para
/// siempre, y entonces el visor muestra el error.
/// </summary>
public sealed class GpuRecovery
{
    public const string Notice = "La tarjeta gráfica se reinició; seguimos.";
    public const int MaxRecoveries = 3;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly Queue<long> _recent = new();

    /// <summary><c>true</c> si toca rehacer el dispositivo; lo anota como un intento.</summary>
    /// <param name="deviceLost">El dispositivo está perdido.</param>
    /// <param name="timestamp">Ahora, en marcas de <see cref="Stopwatch"/>.</param>
    public bool ShouldRecover(bool deviceLost, long timestamp)
    {
        if (!deviceLost)
        {
            return false;
        }

        long window = (long)(Window.TotalSeconds * Stopwatch.Frequency);
        while (_recent.Count > 0 && timestamp - _recent.Peek() >= window)
        {
            _recent.Dequeue();
        }

        if (_recent.Count >= MaxRecoveries)
        {
            return false;
        }

        _recent.Enqueue(timestamp);
        return true;
    }
}
