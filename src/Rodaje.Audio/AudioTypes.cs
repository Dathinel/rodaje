using Rodaje.Core.Timing;
using Rodaje.Playback;

namespace Rodaje.Audio;

/// <summary>Proveedor del audio que suena: float32 estéreo intercalado a la frecuencia de la salida.</summary>
/// <remarks>
/// Se llama siempre desde el hilo de audio, de a una llamada, así que no necesita candados propios para
/// <see cref="Read"/> y <see cref="Seek"/>. No debe bloquear: lo que no esté listo se devuelve corto y la
/// salida lo completa con silencio.
/// </remarks>
public interface IAudioSampleSource
{
    /// <summary>Llena <paramref name="interleaved"/> (L, R, L, R…) y devuelve cuántos cuadros escribió.</summary>
    int Read(Span<float> interleaved);

    /// <summary>El reloj saltó a <paramref name="position"/>; lo siguiente que se lea debe empezar ahí.</summary>
    /// <remarks>Se llama en el hilo de audio, después de vaciar el dispositivo y antes de volver a llenarlo.</remarks>
    void Seek(Time position)
    {
    }
}

/// <summary>Salida de audio. Se controla con su <see cref="Clock"/>: Start, Pause y Seek del reloj mueven el dispositivo.</summary>
public interface IAudioOutput : IDisposable
{
    /// <summary>Reloj maestro de la reproducción.</summary>
    IAudioClock Clock { get; }

    /// <summary>Falso cuando no hay dispositivo y el reloj es el del sistema.</summary>
    bool HasDevice { get; }

    /// <summary>Por qué no hay sonido, en lenguaje simple; nulo si hay dispositivo.</summary>
    string? FallbackReason { get; }

    /// <summary>Dispositivo abierto ahora mismo; nulo si no hay ninguno.</summary>
    AudioDeviceInfo? Device { get; }

    /// <summary>Ganancia lineal entre 0 y 1; se aplica con una rampa corta para no producir chasquidos.</summary>
    float Volume { get; set; }

    AudioOutputStats Stats { get; }

    /// <summary>Se cambió o se perdió el dispositivo. Llega desde un hilo del grupo, nunca del hilo de audio.</summary>
    event EventHandler<AudioDeviceChangedEventArgs>? DeviceChanged;
}

public sealed record AudioOutputOptions
{
    public int SampleRate { get; init; } = 48_000;

    /// <summary>Buffer del dispositivo cuando no se puede usar el modo de baja latencia de IAudioClient3.</summary>
    public TimeSpan BufferDuration { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>Usar IAudioClient3 con un periodo menor que el del motor cuando el controlador lo permite.</summary>
    public bool PreferLowLatency { get; init; } = true;

    /// <summary>Periodo buscado en baja latencia; se redondea al múltiplo que admite el controlador.</summary>
    public TimeSpan LowLatencyPeriod { get; init; } = TimeSpan.FromMilliseconds(5);

    /// <summary>Retardo que Windows no conoce (por ejemplo Bluetooth o un procesador externo) y que el reloj debe descontar.</summary>
    public TimeSpan ExtraLatency { get; init; } = TimeSpan.Zero;

    public float Volume { get; init; } = 1f;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(SampleRate, 8_000, nameof(SampleRate));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(SampleRate, 384_000, nameof(SampleRate));
        ArgumentOutOfRangeException.ThrowIfLessThan(BufferDuration, TimeSpan.FromMilliseconds(3), nameof(BufferDuration));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(BufferDuration, TimeSpan.FromMilliseconds(500), nameof(BufferDuration));
        ArgumentOutOfRangeException.ThrowIfLessThan(LowLatencyPeriod, TimeSpan.FromMilliseconds(1), nameof(LowLatencyPeriod));
        ArgumentOutOfRangeException.ThrowIfLessThan(ExtraLatency, TimeSpan.Zero, nameof(ExtraLatency));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ExtraLatency, TimeSpan.FromSeconds(2), nameof(ExtraLatency));
    }
}

/// <param name="Id">Identificador de Windows del dispositivo; sirve para saber si cambió el predeterminado.</param>
/// <param name="Name">Nombre que muestra Windows, por ejemplo "Altavoces (Realtek(R) Audio)".</param>
/// <param name="Channels">Canales del dispositivo; el estéreo de la fuente se adapta a ellos.</param>
/// <param name="MixSampleRate">Frecuencia del mezclador de Windows; si difiere de la salida, Windows remuestrea.</param>
/// <param name="BufferDuration">Tamaño del buffer del dispositivo: lo máximo que puede haber en cola.</param>
/// <param name="Period">Cada cuánto despierta el hilo de audio.</param>
/// <param name="LowLatency">Se abrió con IAudioClient3 y un periodo menor que el del motor.</param>
/// <param name="StreamLatency">Lo que informa <c>GetStreamLatency</c>; en modo compartido suele ser 0.</param>
public sealed record AudioDeviceInfo(
    string Id,
    string Name,
    int Channels,
    int MixSampleRate,
    TimeSpan BufferDuration,
    TimeSpan Period,
    bool LowLatency,
    TimeSpan StreamLatency);

/// <param name="FramesRendered">Cuadros entregados al dispositivo (incluye silencio de relleno).</param>
/// <param name="Underruns">Veces que la fuente entregó menos de lo pedido.</param>
/// <param name="UnderrunFrames">Cuadros de silencio que se pusieron en lugar de audio.</param>
/// <param name="SourceErrors">Excepciones lanzadas por la fuente; cada una cuenta también como silencio.</param>
/// <param name="Starvations">Veces que el dispositivo vació su buffer porque el hilo de audio llegó tarde.</param>
/// <param name="DeviceRestarts">Reaperturas por pérdida o cambio del dispositivo predeterminado.</param>
/// <param name="ProAudioPriority">El hilo de audio obtuvo la prioridad "Pro Audio" de MMCSS.</param>
/// <param name="ControlTimeouts">Órdenes de transporte que el hilo de audio no confirmó a tiempo.</param>
public readonly record struct AudioOutputStats(
    long FramesRendered,
    long Underruns,
    long UnderrunFrames,
    long SourceErrors,
    long Starvations,
    int DeviceRestarts,
    bool ProAudioPriority,
    int ControlTimeouts);

public sealed class AudioDeviceChangedEventArgs(AudioDeviceInfo? device, string message) : EventArgs
{
    /// <summary>Dispositivo nuevo; nulo si no quedó ninguno y el reloj sigue solo.</summary>
    public AudioDeviceInfo? Device { get; } = device;

    /// <summary>Explicación para el usuario.</summary>
    public string Message { get; } = message;
}

/// <summary>No se pudo abrir ningún dispositivo de salida; <see cref="Exception.Message"/> explica por qué.</summary>
public sealed class AudioDeviceUnavailableException : Exception
{
    public AudioDeviceUnavailableException()
    {
    }

    public AudioDeviceUnavailableException(string message)
        : base(message)
    {
    }

    public AudioDeviceUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public AudioDeviceUnavailableException(string message, int hresult)
        : base(message) => HResult = hresult;
}
