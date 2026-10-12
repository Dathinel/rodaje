namespace Rodaje.Media.Export;

/// <summary>Todo lo que el sink necesita para empezar a escribir un archivo (o una pasada de uno).</summary>
/// <param name="OutputPath">
/// Ruta final. El sink escribe en un temporal junto a ella y la renombra al terminar. En
/// <see cref="ExportContainer.ImageSequence"/> es la carpeta; los archivos se llaman <c>nombre_000000.ext</c>.
/// </param>
/// <param name="Container">Contenedor de la salida.</param>
/// <param name="Video">Formato de los cuadros que llegarán; <c>null</c> si es solo audio.</param>
/// <param name="VideoEncoder">Codificador de video; <c>null</c> si es solo audio.</param>
/// <param name="VideoArguments">
/// Argumentos que salen de los ajustes (control de tasa, perfil, nivel, GOP, cuadros B, preset, tune), tras
/// <see cref="EncoderSpec.BaseArguments"/>. Los arma <c>Rodaje.Export</c>; el sink no interpreta ajustes.
/// </param>
/// <param name="Audio">Formato del WAV de la mezcla; <c>null</c> si no hay audio.</param>
/// <param name="AudioPath">WAV temporal con la mezcla del rango, ya escrito (y normalizado si se pidió) por <c>OfflineAudio</c>.</param>
/// <param name="AudioEncoder">Codificador de audio; <c>null</c> si no hay audio.</param>
/// <param name="AudioArguments">Argumentos de audio (tasa, calidad, perfil, frecuencia y canales de salida).</param>
/// <param name="FrameCount">Cuadros que llegarán, para el avance y para comprobar que no falta ninguno.</param>
public sealed record ExportSinkRequest(
    string OutputPath,
    ExportContainer Container,
    ExportVideoFormat? Video,
    EncoderSpec? VideoEncoder,
    IReadOnlyList<string> VideoArguments,
    ExportAudioFormat? Audio,
    string? AudioPath,
    EncoderSpec? AudioEncoder,
    IReadOnlyList<string> AudioArguments,
    long FrameCount)
{
    public ExportMetadata Metadata { get; init; } = ExportMetadata.Empty;

    /// <summary>Pasada actual, desde 1. En la primera de dos no se escribe archivo, solo el registro de la pasada.</summary>
    public int Pass { get; init; } = 1;

    public int PassCount { get; init; } = 1;

    /// <summary>Prefijo del registro de dos pasadas (<c>-passlogfile</c>), en la carpeta temporal de la exportación.</summary>
    public string? PassLogPath { get; init; }

    /// <summary>Filtros de <c>ffmpeg.exe</c> que no se hacen en la GPU (la paleta del GIF); vacío casi siempre.</summary>
    public IReadOnlyList<string> VideoFilters { get; init; } = [];

    /// <summary>
    /// Copia directa: el video sale del archivo original sin recodificar (<c>-c:v copy</c>) y no llegan cuadros por la
    /// tubería; el audio sigue igual y <see cref="VideoArguments"/> solo lleva los del contenedor. <c>null</c> en una
    /// exportación normal.
    /// </summary>
    public ExportStreamCopy? VideoCopy { get; init; }

    public bool HasVideo => Video is not null && VideoEncoder is not null;

    public bool HasAudio => Audio is not null && AudioPath is not null && AudioEncoder is not null;

    public bool IsAnalysisPass => PassCount > 1 && Pass < PassCount;
}

/// <summary>Tramo del original que se copia tal cual.</summary>
/// <param name="SourcePath">Archivo de origen.</param>
/// <param name="StreamIndex">Flujo de video del original.</param>
/// <param name="SeekSeconds"><c>-ss</c> antes de <c>-i</c> (<see cref="StreamCopySource"/>).</param>
/// <param name="BitstreamFilters">Filtros de flujo para etiquetar el color sin recodificar (<c>h264_metadata</c>…); vacío si no hacen falta.</param>
public sealed record ExportStreamCopy(string SourcePath, int StreamIndex, double SeekSeconds, IReadOnlyList<string> BitstreamFilters);

/// <summary>Lo que dejó un sink al terminar bien.</summary>
/// <param name="OutputPath">Archivo (o carpeta) final, ya renombrado; en una pasada de análisis, el registro.</param>
/// <param name="Bytes">Tamaño de lo escrito.</param>
/// <param name="FramesWritten">Cuadros que recibió el codificador.</param>
public sealed record ExportSinkResult(string OutputPath, long Bytes, long FramesWritten);

/// <summary>El final de la tubería: recibe los cuadros ya convertidos y los deja en un archivo.</summary>
/// <remarks>
/// <para>
/// Orden de llamadas, todas desde el hilo de la exportación: <see cref="Start"/>, <see cref="WriteVideo"/> por cada
/// cuadro en orden (0, 1, 2…), y <see cref="Complete"/>; con dos pasadas, el ciclo se repite con la pasada 2.
/// <see cref="Abort"/> se puede llamar desde cualquier hilo y en cualquier momento; deja sin archivo ni proceso vivo
/// antes de volver (≤1 s). <see cref="IDisposable.Dispose"/> sin <see cref="Complete"/> equivale a <see cref="Abort"/>.
/// </para>
/// <para>Los errores salen como <see cref="ExportException"/> con su código; la cancelación, como <see cref="OperationCanceledException"/>.</para>
/// <para>
/// La v1 tiene una implementación, <c>FfmpegPipeSink</c> (tubería a <c>ffmpeg.exe</c>); un complemento GPL usa la misma
/// con otro <c>ffmpeg.exe</c>. <c>NvencDirectSink</c> sería otra si las cifras lo piden.
/// </para>
/// </remarks>
public interface IExportSink : IDisposable
{
    void Start(ExportSinkRequest request, CancellationToken cancellationToken);

    /// <summary>Escribe un cuadro; puede bloquear mientras el codificador va por detrás (contrapresión).</summary>
    void WriteVideo(in ExportVideoFrame frame, CancellationToken cancellationToken);

    /// <summary>Cierra la entrada, espera al codificador y renombra el temporal al destino.</summary>
    ExportSinkResult Complete(CancellationToken cancellationToken);

    /// <summary>Detiene el codificador y borra el temporal.</summary>
    void Abort();

    /// <summary>Cuadros que el codificador ya consumió según su <c>-progress</c>; puede ir por detrás de los escritos.</summary>
    long FramesEncoded { get; }
}
