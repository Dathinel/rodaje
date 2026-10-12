using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Video;

namespace Rodaje.Media.Probe;

/// <summary>Lo que Rodaje necesita saber de un archivo de medio para usarlo en un proyecto.</summary>
public sealed record MediaInfo
{
    public required string Path { get; init; }

    /// <summary>Nombre del contenedor según FFmpeg, por ejemplo <c>mov,mp4,m4a,3gp,3g2,mj2</c>.</summary>
    public required string ContainerFormat { get; init; }

    /// <summary>Duración contada desde el origen del medio (el primer cuadro de video, o la primera muestra de audio).</summary>
    public required Time Duration { get; init; }

    /// <summary><c>start_time</c> del contenedor; la reproducción lo normaliza a cero.</summary>
    public required Time ContainerStartTime { get; init; }

    public VideoStreamInfo? Video { get; init; }

    public AudioStreamInfo? Audio { get; init; }

    public bool HasVideo => Video is not null;

    public bool HasAudio => Audio is not null;

    /// <summary>Instante del contenedor que corresponde al tiempo 0 del medio.</summary>
    public Time Origin => Video?.StartTime ?? Audio?.StartTime ?? ContainerStartTime;

    /// <summary>Copia los metadatos al <see cref="MediaRef"/>; el ancho y alto son los de pantalla, ya rotados.</summary>
    public void ApplyTo(MediaRef media)
    {
        ArgumentNullException.ThrowIfNull(media);
        media.Duration = Duration;
        media.HasVideo = HasVideo;
        media.HasAudio = HasAudio;
        media.FrameRate = Video?.FrameRate;
        media.Width = Video?.DisplayWidth ?? 0;
        media.Height = Video?.DisplayHeight ?? 0;
        media.SampleRate = Audio?.SampleRate ?? 0;
        media.AudioChannels = Audio?.Channels ?? 0;
        if (string.IsNullOrEmpty(media.Name))
        {
            media.Name = System.IO.Path.GetFileNameWithoutExtension(Path);
        }
    }
}

/// <summary>Flujo de video elegido (el mejor según FFmpeg).</summary>
public sealed record VideoStreamInfo
{
    public required int StreamIndex { get; init; }

    public required string Codec { get; init; }

    public required string PixelFormat { get; init; }

    public required int BitDepth { get; init; }

    /// <summary>Matriz y rango que declara el flujo, o los habituales para su tamaño si no los declara.</summary>
    public VideoColor Color { get; init; } = VideoColor.Bt709Limited;

    /// <summary>«PQ» o «HLG» si el flujo es HDR. Rodaje no hace mapeo de tonos: lo trata como SDR (se ve apagado).</summary>
    public string? HdrTransfer { get; init; }

    /// <summary>Tamaño del cuadro decodificado, antes de aplicar rotación o relación de aspecto del píxel.</summary>
    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>Relación de aspecto del píxel (SAR); 1:1 si el archivo no la declara.</summary>
    public required Rational SampleAspectRatio { get; init; }

    /// <summary>Rotación de pantalla en grados en sentido horario: 0, 90, 180 o 270.</summary>
    public required int Rotation { get; init; }

    /// <summary>Ancho en pantalla: aplica el SAR al ancho y después la rotación.</summary>
    public required int DisplayWidth { get; init; }

    public required int DisplayHeight { get; init; }

    /// <summary>Tasa con la que se numeran los cuadros (ver <see cref="IsVariableFrameRate"/>).</summary>
    public required Rational FrameRate { get; init; }

    public Rational? AverageFrameRate { get; init; }

    public Rational? RealFrameRate { get; init; }

    /// <summary>
    /// Las marcas de tiempo no siguen una tasa fija. La fase 2 numera los cuadros con <see cref="FrameRate"/> y
    /// cada índice muestra el cuadro decodificado más cercano, así que puede repetir o saltar cuadros.
    /// </summary>
    public required bool IsVariableFrameRate { get; init; }

    public required long FrameCount { get; init; }

    public required Time Duration { get; init; }

    /// <summary><c>start_time</c> del flujo, en tiempo del contenedor. El cuadro 0 empieza aquí.</summary>
    public required Time StartTime { get; init; }
}

/// <summary>Flujo de audio elegido.</summary>
public sealed record AudioStreamInfo
{
    public required int StreamIndex { get; init; }

    public required string Codec { get; init; }

    public required int SampleRate { get; init; }

    public required int Channels { get; init; }

    public required Time Duration { get; init; }

    /// <summary><c>start_time</c> del flujo, en tiempo del contenedor.</summary>
    public required Time StartTime { get; init; }
}
