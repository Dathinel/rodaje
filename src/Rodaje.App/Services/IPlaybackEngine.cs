using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Timeline;

namespace Rodaje.App.Services;

/// <summary>Estado del reproductor que publica el hilo de render; se reemplaza entero, así que se lee sin candado.</summary>
public sealed record PlaybackSnapshot(Time Position, Time Duration, bool IsPlaying, long Frame, long FrameCount, string Status)
{
    public static readonly PlaybackSnapshot Empty = new(Time.Zero, Time.Zero, false, 0, 0, "Preparando el reproductor...");

    /// <summary>Refrescos en los que alguna capa no tenía su cuadro a tiempo, desde que se abrió la secuencia.</summary>
    public long FramesDropped { get; init; }
}

/// <summary>
/// El reproductor visto desde la interfaz. Todas las llamadas encolan una orden y vuelven enseguida: nunca esperan al
/// hilo de render.
/// </summary>
public interface IPlaybackEngine
{
    PlaybackSnapshot Snapshot { get; }

    /// <summary>Reproduce otra secuencia desde el principio. <paramref name="project"/> pasa a ser del motor: no se vuelve a tocar.</summary>
    void Load(Project project, string sequenceId);

    /// <summary>El proyecto cambió: el motor toma esta copia sin parar ni mover la posición. Tampoco se vuelve a tocar.</summary>
    void Reload(Project project);

    void Play();

    void Pause();

    void TogglePlayPause();

    void Seek(Time position);

    void StepFrames(int frames);

    /// <summary>Cuándo decodifica la vista previa el proxy de un medio en vez del original.</summary>
    void SetProxyUsage(ProxyUsage usage);
}
