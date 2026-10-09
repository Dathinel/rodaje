using Rodaje.Core.Timing;
using Rodaje.Playback;

namespace Rodaje.Audio;

/// <summary>Lo que el reloj necesita de una salida: cuadros oídos y transporte.</summary>
internal interface IRenderTransport
{
    int SampleRate { get; }

    /// <summary>Contador continuo de cuadros oídos (global, monótono, congelado en pausa).</summary>
    long ReadFramesHeard();

    void Start();

    void Pause();

    /// <summary>Descarta lo que hay en cola, lleva la fuente a <paramref name="position"/> y, si sonaba, sigue sonando.</summary>
    void Flush(Time position);
}

/// <summary>Reloj maestro guiado por el dispositivo de audio: la posición es la muestra que está sonando.</summary>
/// <remarks>
/// Hereda de <see cref="SampleCountingClock"/>, que ya resuelve el origen del último seek, la pausa y la
/// seguridad entre hilos; aquí solo se conecta el contador de la salida, que interpola entre lecturas de
/// <c>IAudioClock::GetPosition</c> con su marca QPC y sigue continuo al cambiar de dispositivo.
/// </remarks>
public sealed class DeviceAudioClock : SampleCountingClock
{
    private readonly IRenderTransport _transport;

    internal DeviceAudioClock(IRenderTransport transport)
        : base(transport.SampleRate)
    {
        _transport = transport;
    }

    protected override long ReadSamplesPlayed() => _transport.ReadFramesHeard();

    protected override void OnStart() => _transport.Start();

    protected override void OnPause() => _transport.Pause();

    protected override void OnSeek(Time position) => _transport.Flush(position);
}
