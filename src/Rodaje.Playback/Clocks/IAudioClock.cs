using Rodaje.Core.Timing;

namespace Rodaje.Playback;

/// <summary>Reloj maestro de la reproducción. El video lo sigue; nunca al revés.</summary>
/// <remarks>
/// <see cref="Position"/> es el instante de la línea de tiempo cuya muestra de audio se está oyendo.
/// Una implementación con dispositivo la calcula como el destino del último <see cref="Seek"/>
/// más las muestras reproducidas desde entonces, y descuenta ella misma la latencia de salida
/// (ver <see cref="SampleCountingClock"/>). Todos los miembros deben ser seguros entre hilos.
/// </remarks>
public interface IAudioClock
{
    /// <summary>Posición actual. No decrece mientras corre, salvo por un <see cref="Seek"/>.</summary>
    Time Position { get; }

    bool IsRunning { get; }

    void Start();

    void Pause();

    /// <summary>Lleva el reloj a <paramref name="position"/> sin cambiar si corre o está en pausa.</summary>
    void Seek(Time position);
}
