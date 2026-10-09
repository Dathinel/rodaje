using Rodaje.Audio;
using Rodaje.Media.Video;
using Rodaje.Playback;

namespace Rodaje.Engine;

public enum DecodeMode
{
    /// <summary>D3D11VA: el cuadro nace en la GPU y no la abandona.</summary>
    Gpu,

    /// <summary>Software, con el cuadro subido a una textura para pintarlo.</summary>
    CpuUploaded,

    /// <summary>Software sin dispositivo: el cuadro queda en memoria del sistema.</summary>
    Cpu,
}

/// <summary>Estado medible de un <see cref="ClipPlayer"/>.</summary>
/// <param name="Playback">
/// Contadores de la reproducción sin los refrescos de espera que siguen a cada seek: hasta que aparece el
/// cuadro pedido se muestra el anterior, y eso no es un retraso de sincronía sino latencia del seek.
/// </param>
/// <param name="Seeks">Seeks pedidos (incluidos los pasos de cuadro).</param>
/// <param name="MaxSeekLatency">Lo más que tardó en aparecer el cuadro pedido por un seek.</param>
/// <param name="AverageSeekLatency">Promedio de lo mismo.</param>
/// <param name="AudioStatus">Nombre del dispositivo de audio, o por qué no suena.</param>
/// <param name="AudioUnderruns">Veces que el audio no estaba decodificado a tiempo (fuera de los seeks).</param>
public sealed record ClipPlayerStats(
    PlaybackStats Playback,
    DecodeMode DecodeMode,
    VideoDecoderStatistics Decoder,
    long Seeks,
    TimeSpan MaxSeekLatency,
    TimeSpan AverageSeekLatency,
    bool HasAudioDevice,
    string AudioStatus,
    AudioOutputStats Audio,
    long AudioUnderruns)
{
    public string DecodeModeText => DecodeMode switch
    {
        DecodeMode.Gpu => "GPU (D3D11VA)",
        DecodeMode.CpuUploaded => "CPU (subido a la GPU)",
        _ => "CPU",
    };
}
