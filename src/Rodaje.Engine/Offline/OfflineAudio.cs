using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Timeline;
using Rodaje.Media.Export;

namespace Rodaje.Engine.Offline;

/// <summary>
/// La mezcla de la secuencia sin dispositivo ni margen de recarga: el <c>MixRenderer</c> de la vista previa leído de
/// corrido, para escribir el WAV temporal de la exportación antes del video (D-028).
/// </summary>
/// <remarks>
/// Se usa desde un solo hilo. Estéreo float a <see cref="TimelinePlan.AudioSampleRate"/>; la muestra <c>s</c> es el
/// instante <c>s / 48 000</c> de la secuencia, igual que en la vista previa. Contrato del paso 0 de la fase 6; la
/// escritura del WAV y los errores de medios los completa el agente A.
/// </remarks>
public sealed class OfflineAudio : IDisposable
{
    public const int Channels = 2;

    private readonly MixRenderer _mixer;
    private bool _disposed;

    internal OfflineAudio(Project project, string sequenceId, string projectDirectory, IClipBackend backend)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(sequenceId);
        ArgumentNullException.ThrowIfNull(backend);
        TimelinePlan plan = TimelinePlan.Build(project, sequenceId, projectDirectory);
        Duration = plan.Duration;
        _mixer = new MixRenderer(plan.Audio, backend.OpenAudio, TimeSpan.FromSeconds(5), TimeProvider.System);
    }

    /// <exception cref="KeyNotFoundException">La secuencia no existe en el proyecto.</exception>
    public static OfflineAudio Open(Project project, string sequenceId, string? projectDirectory = null) =>
        new(project, sequenceId, projectDirectory ?? Environment.CurrentDirectory, MediaClipBackend.Instance);

    public static int SampleRate => TimelinePlan.AudioSampleRate;

    public Time Duration { get; }

    /// <summary>Muestra de la secuencia que dará el próximo <see cref="Read"/>.</summary>
    public long Position => _mixer.Position;

    /// <summary>Medios que fallaron al abrirse o decodificarse (en la exportación es un error, no silencio).</summary>
    public long Errors => _mixer.Errors;

    /// <summary>Llena todo <paramref name="interleaved"/> (estéreo intercalado); tras los clips, silencio.</summary>
    public int Read(Span<float> interleaved)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _mixer.Read(interleaved);
    }

    public void Seek(Time position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _mixer.Seek(position);
    }

    /// <summary>Mide la sonoridad de la mezcla de <c>[start, end)</c> según EBU R128 / ITU-R BS.1770 (pasada previa a normalizar).</summary>
    /// <exception cref="ExportException">Un medio falló.</exception>
    public LoudnessMeasurement MeasureLoudness(Time start, Time end, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckRange(start, end);
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotImplementedException("OfflineAudio.MeasureLoudness es del agente A de la fase 6.");
    }

    /// <summary>
    /// Escribe la mezcla de <c>[start, end)</c> en un WAV (RIFF, o RF64 si pasa de 4 GB) con el formato pedido, de
    /// forma atómica. Devuelve las muestras por canal escritas: <c>end.ToSamples − start.ToSamples</c> a la tasa de
    /// <paramref name="format"/>.
    /// </summary>
    /// <param name="format">Frecuencia, canales (mono baja la mezcla; 5.1 pone el estéreo en L/R) y muestras.</param>
    /// <param name="gainDb">Ganancia de normalización (de <see cref="LoudnessMeasurement.GainTo"/>); 0 sin normalizar.</param>
    /// <param name="progress">Fracción hecha, de 0 a 1.</param>
    /// <exception cref="ExportException">Un medio falló (<see cref="ExportErrorCode.MediaMissing"/>) o el disco se llenó.</exception>
    public long WriteWave(string path, Time start, Time end, ExportAudioFormat format, double gainDb, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(format);
        CheckRange(start, end);
        if (!double.IsFinite(gainDb))
        {
            throw new ArgumentOutOfRangeException(nameof(gainDb), gainDb, "La ganancia debe ser un número finito.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new NotImplementedException("OfflineAudio.WriteWave es del agente A de la fase 6.");
    }

    private static void CheckRange(Time start, Time end)
    {
        if (end <= start || start < Time.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(end), "El rango del audio está vacío o empieza antes de 0.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _mixer.Dispose();
    }
}
