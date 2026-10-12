using System.Diagnostics;
using Rodaje.Media.Export;
using Rodaje.Media.Proxies;

namespace Rodaje.Export.Still;

/// <summary>
/// Escribe un cuadro ya bajado de la GPU como PNG o JPEG con el <c>ffmpeg.exe</c> LGPL verificado, por el mismo
/// <see cref="FfmpegPipeSink"/> de la exportación (proceso en el <c>ChildProcessJob</c>, temporal <c>.rodaje-tmp</c>,
/// registro para limpiar tras un apagón, comprobación de la salida y del disco).
/// </summary>
/// <remarks>
/// El sink escribe imágenes como <see cref="ExportContainer.ImageSequence"/> (una carpeta con <c>nombre_000000.ext</c>):
/// aquí la carpeta es un temporal junto al destino, y la única imagen se renombra al archivo final, en el mismo disco.
/// </remarks>
public sealed class StillEncoder : IDisposable
{
    private readonly FfmpegPipeSink _sink;
    private readonly StillPlan _plan;
    private readonly string _output;
    private readonly string _work;
    private readonly ExportJournal? _journal;
    private readonly string _journalId = "fotograma-" + Guid.NewGuid().ToString("N");
    private bool _started;
    private bool _done;

    /// <param name="ffmpeg">El <c>ffmpeg.exe</c> verificado.</param>
    /// <param name="plan">Lo decidido para el fotograma.</param>
    /// <param name="outputPath">El archivo final.</param>
    /// <param name="sourcePaths">Los medios del proyecto, que la salida no puede pisar.</param>
    /// <param name="journal">Registro de temporales; <c>null</c> no registra.</param>
    public StillEncoder(FfmpegExecutable ffmpeg, StillPlan plan, string outputPath, IEnumerable<string>? sourcePaths = null, ExportJournal? journal = null)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        _plan = plan;
        _output = Path.GetFullPath(outputPath);
        _work = _output + ".fotograma" + ExportOutputGuard.TemporaryMarker;
        _journal = journal;
        List<string> sources = [.. sourcePaths ?? []];
        // Cualquier contenedor de un solo archivo sirve para comprobar el destino: que no sea un medio, una carpeta ni esté abierto.
        ExportOutputGuard.CheckOutput(_output, ExportContainer.Mp4, sources);
        _sink = new FfmpegPipeSink(ffmpeg, new FfmpegPipeSinkOptions
        {
            Priority = ProcessPriorityClass.Normal,
            Journal = journal,
            SourcePaths = sources,
            EstimatedBytes = plan.Output.FrameBytes,
        });
    }

    /// <summary>La línea de <c>ffmpeg.exe</c> (tras <see cref="Write"/>; para el registro y las pruebas).</summary>
    public IReadOnlyList<string> Arguments => _sink.Arguments;

    /// <summary>La petición que recibe el sink.</summary>
    public ExportSinkRequest Request => new(_work, ExportContainer.ImageSequence, _plan.Output, _plan.Encoder, _plan.Arguments,
        null, null, null, [], 1)
    {
        VideoFilters = _plan.Filters,
    };

    /// <summary>Codifica <paramref name="frame"/> (formato <see cref="StillPlan.Output"/>, índice 0) y deja el archivo final.</summary>
    /// <returns>Bytes del archivo.</returns>
    /// <exception cref="ExportException">Con su código; la cancelación, como <see cref="OperationCanceledException"/>.</exception>
    public long Write(in ExportVideoFrame frame, CancellationToken cancellationToken)
    {
        if (_started)
        {
            throw new InvalidOperationException("Un StillEncoder escribe una sola imagen.");
        }

        _started = true;
        _journal?.Register(_journalId, _output, [_work]);
        try
        {
            _sink.Start(Request, cancellationToken);
            _sink.WriteVideo(in frame, cancellationToken);
            _sink.Complete(cancellationToken);
            string image = Directory.EnumerateFiles(_work).SingleOrDefault()
                ?? throw new ExportException(ExportErrorCode.EncoderFailed, "ffmpeg.exe terminó sin escribir la imagen.") { Culprit = _output };
            long bytes = ExportOutputGuard.Commit(image, _output, ExportContainer.Mp4);
            _done = true;
            return bytes;
        }
        finally
        {
            ExportOutputGuard.DeleteTemporary(_work);
            try
            {
                _journal?.Remove(_journalId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public void Dispose()
    {
        _sink.Dispose();
        if (!_done)
        {
            ExportOutputGuard.DeleteTemporary(_work);
        }
    }
}
