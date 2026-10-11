using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Timeline;
using Rodaje.Media.Export;

namespace Rodaje.Engine.Offline;

/// <summary>
/// La mezcla de la secuencia sin dispositivo ni margen de recarga: el <c>MixRenderer</c> de la vista previa leído de
/// corrido (con su WSOLA y su cinta para los clips con velocidad), para escribir el WAV temporal de la exportación antes
/// del video (D-028).
/// </summary>
/// <remarks>
/// <para>
/// Se usa desde un solo hilo. Estéreo float a <see cref="TimelinePlan.AudioSampleRate"/>; la muestra <c>s</c> es el
/// instante <c>s / 48 000</c> de la secuencia, igual que en la vista previa.
/// </para>
/// <para>
/// Exacto a la muestra con el video: el rango <c>[start, end)</c> a la frecuencia <c>R</c> de la salida son las muestras
/// <c>start.ToSamples(R)</c> hasta <c>end.ToSamples(R)</c> (sin incluir); con <c>end</c> en el borde de un cuadro, la
/// suma de los tramos de una exportación por partes es la del todo y no se acumula deriva. A otra frecuencia que 48 kHz,
/// la muestra <c>n</c> es el instante <c>n / R</c> remuestreado de la mezcla (<see cref="RateConverter"/>).
/// </para>
/// </remarks>
public sealed class OfflineAudio : IDisposable
{
    public const int Channels = 2;

    private const int Block = 4096;

    private readonly MixRenderer _mixer;
    private readonly TimelinePlan _plan;
    private readonly IClipBackend _backend;
    private readonly float[] _mix = new float[Block * Channels];
    private bool _disposed;

    internal OfflineAudio(Project project, string sequenceId, string projectDirectory, IClipBackend backend)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(sequenceId);
        ArgumentNullException.ThrowIfNull(backend);
        _plan = TimelinePlan.Build(project, sequenceId, projectDirectory);
        _backend = backend;
        Duration = _plan.Duration;
        _mixer = new MixRenderer(_plan.Audio, backend.OpenAudio, TimeSpan.FromSeconds(5), TimeProvider.System);
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
        CheckSources(start, end);
        var meter = new LoudnessMeter();
        long first = start.ToSamples(SampleRate);
        long total = end.ToSamples(SampleRate) - first;
        _mixer.Seek(Time.FromSamples(first, SampleRate));
        long errors = _mixer.Errors;
        var report = new ProgressReporter(progress, total);
        for (long done = 0; done < total;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int frames = (int)Math.Min(Block, total - done);
            Span<float> block = _mix.AsSpan(0, frames * Channels);
            _mixer.Read(block);
            CheckErrors(errors);
            meter.Process(block);
            done += frames;
            report.Report(done);
        }

        return meter.Result();
    }

    /// <summary>
    /// Escribe la mezcla de <c>[start, end)</c> en un WAV (RIFF, o RF64 si pasa de 4 GB) con el formato pedido, de
    /// forma atómica. Devuelve las muestras por canal escritas: <c>end.ToSamples − start.ToSamples</c> a la tasa de
    /// <paramref name="format"/>.
    /// </summary>
    /// <param name="format">Frecuencia, canales (mono baja la mezcla; 5.1 pone el estéreo en L/R) y muestras.</param>
    /// <param name="gainDb">Ganancia de normalización (de <see cref="LoudnessMeasurement.GainTo"/>); 0 sin normalizar.</param>
    /// <param name="progress">Fracción hecha, de 0 a 1.</param>
    /// <exception cref="ExportException">
    /// Un medio falló (<see cref="ExportErrorCode.MediaMissing"/>), el disco se llenó (<see cref="ExportErrorCode.DiskFull"/>)
    /// o no se puede escribir (<see cref="ExportErrorCode.OutputNotWritable"/>).
    /// </exception>
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
        CheckSources(start, end);
        float gain = (float)Math.Pow(10, gainDb / 20);
        long first = start.ToSamples(format.SampleRate);
        long total = end.ToSamples(format.SampleRate) - first;
        WaveWriter? writer = null;
        try
        {
            writer = new WaveWriter(path, format);
            var report = new ProgressReporter(progress, total);
            var output = new float[Block * format.Channels];
            long errors = _mixer.Errors;
            if (format.SampleRate == SampleRate)
            {
                _mixer.Seek(Time.FromSamples(first, SampleRate));
                for (long done = 0; done < total;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int frames = (int)Math.Min(Block, total - done);
                    Span<float> block = _mix.AsSpan(0, frames * Channels);
                    _mixer.Read(block);
                    CheckErrors(errors);
                    Layout(block, block, output.AsSpan(0, frames * format.Channels), format.Channels, gain, interleaved: true);
                    writer.Write(output.AsSpan(0, frames * format.Channels));
                    done += frames;
                    report.Report(done);
                }
            }
            else
            {
                var converter = new RateConverter(SampleRate, format.SampleRate);
                var source = new MixWindow(_mixer, converter.FirstInput(first));
                var left = new float[Block];
                var right = new float[Block];
                for (long done = 0; done < total;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int frames = (int)Math.Min(Block, total - done);
                    long at = first + done;
                    source.Fill(converter.FirstInput(at), converter.LastInput(at + frames - 1));
                    CheckErrors(errors);
                    converter.Convert(at, left.AsSpan(0, frames), right.AsSpan(0, frames), source.Left, source.Right, source.Base);
                    Layout(left.AsSpan(0, frames), right.AsSpan(0, frames), output.AsSpan(0, frames * format.Channels), format.Channels, gain,
                        interleaved: false);
                    writer.Write(output.AsSpan(0, frames * format.Channels));
                    done += frames;
                    report.Report(done);
                }
            }

            long written = writer.Frames;
            writer.Complete();
            return written;
        }
        catch (IOException ex) when (IsDiskFull(ex))
        {
            throw new ExportException(ExportErrorCode.DiskFull, $"No cabe el audio en el disco de «{path}».", ex) { Culprit = path };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ExportException(ExportErrorCode.OutputNotWritable, $"No se pudo escribir el audio en «{path}»: {ex.Message}", ex)
            {
                Culprit = path,
            };
        }
        finally
        {
            writer?.Dispose();
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

    private static void CheckRange(Time start, Time end)
    {
        if (end <= start || start < Time.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(end), "El rango del audio está vacío o empieza antes de 0.");
        }
    }

    /// <summary>
    /// Lleva la mezcla estéreo a los canales de la salida con la ganancia: mono es el promedio, estéreo tal cual y más
    /// canales llevan el estéreo en L/R y silencio en los demás.
    /// </summary>
    private static void Layout(ReadOnlySpan<float> left, ReadOnlySpan<float> right, Span<float> output, int channels, float gain, bool interleaved)
    {
        int frames = output.Length / channels;
        for (int i = 0; i < frames; i++)
        {
            float l = interleaved ? left[2 * i] : left[i];
            float r = interleaved ? right[(2 * i) + 1] : right[i];
            Span<float> frame = output.Slice(i * channels, channels);
            if (channels == 1)
            {
                frame[0] = 0.5f * (l + r) * gain;
                continue;
            }

            frame[0] = l * gain;
            frame[1] = r * gain;
            frame[2..].Clear();
        }
    }

    private static bool IsDiskFull(IOException ex) => (ex.HResult & 0xFFFF) is 0x70 or 0x27;

    /// <summary>Antes de mezclar: cada medio de audio del rango tiene que abrir (en la vista previa uno roto sonaría como silencio).</summary>
    private void CheckSources(Time start, Time end)
    {
        long from = start.ToSamples(SampleRate);
        long to = end.ToSamples(SampleRate);
        var seen = new HashSet<SourceKey>();
        foreach (AudioTrackPlan track in _plan.Audio.Tracks)
        {
            foreach (AudioClipPlan clip in track.Clips)
            {
                if (clip.EndSample <= from || clip.StartSample >= to || !seen.Add(clip.Source))
                {
                    continue;
                }

                try
                {
                    _backend.OpenAudio(clip.Source.Path).Dispose();
                }
#pragma warning disable CA1031 // Cualquier fallo al abrir es un medio que falta para la exportación.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    throw new ExportException(ExportErrorCode.MediaMissing,
                        $"No se pudo abrir el audio de «{Path.GetFileName(clip.Source.Path)}»: {ex.Message}", ex)
                    {
                        Culprit = clip.Source.Path,
                    };
                }
            }
        }
    }

    private void CheckErrors(long before)
    {
        if (_mixer.Errors != before)
        {
            throw new ExportException(ExportErrorCode.MediaMissing, "Un medio de audio dejó de leerse a mitad de la mezcla.");
        }
    }

    /// <summary>Ventana de la mezcla a 48 kHz que se lee de corrido; lo anterior a la muestra 0 es silencio.</summary>
    private sealed class MixWindow
    {
        private readonly MixRenderer _mixer;
        private readonly float[] _interleaved = new float[Block * Channels];
        private float[] _left = new float[Block * 4];
        private float[] _right = new float[Block * 4];
        private int _count;

        public MixWindow(MixRenderer mixer, long first)
        {
            _mixer = mixer;
            Base = first;
            _mixer.Seek(Time.FromSamples(Math.Max(0, first), SampleRate));
            if (first < 0)
            {
                _count = (int)-first;
                Grow(_count);
                _left.AsSpan(0, _count).Clear();
                _right.AsSpan(0, _count).Clear();
            }
        }

        /// <summary>Muestra de la mezcla que está en la posición 0 de <see cref="Left"/> y <see cref="Right"/>.</summary>
        public long Base { get; private set; }

        public ReadOnlySpan<float> Left => _left.AsSpan(0, _count);

        public ReadOnlySpan<float> Right => _right.AsSpan(0, _count);

        /// <summary>Deja en la ventana las muestras <c>[from, to]</c>; lo anterior a <paramref name="from"/> se descarta.</summary>
        public void Fill(long from, long to)
        {
            int drop = (int)Math.Clamp(from - Base, 0, _count);
            if (drop > 0)
            {
                _left.AsSpan(drop, _count - drop).CopyTo(_left);
                _right.AsSpan(drop, _count - drop).CopyTo(_right);
                _count -= drop;
                Base += drop;
            }

            long need = to + 1 - (Base + _count);
            while (need > 0)
            {
                int frames = (int)Math.Min(Block, need);
                Span<float> block = _interleaved.AsSpan(0, frames * Channels);
                _mixer.Read(block);
                Grow(_count + frames);
                for (int i = 0; i < frames; i++)
                {
                    _left[_count + i] = block[2 * i];
                    _right[_count + i] = block[(2 * i) + 1];
                }

                _count += frames;
                need -= frames;
            }
        }

        private void Grow(int size)
        {
            if (_left.Length < size)
            {
                Array.Resize(ref _left, Math.Max(size, _left.Length * 2));
                Array.Resize(ref _right, _left.Length);
            }
        }
    }

    /// <summary>Avisa el avance cuando cambia al menos un 0,5 % (y siempre al terminar).</summary>
    private sealed class ProgressReporter(IProgress<double>? progress, long total)
    {
        private double _last = -1;

        public void Report(long done)
        {
            if (progress is null)
            {
                return;
            }

            double fraction = total <= 0 ? 1 : (double)done / total;
            if (fraction - _last >= 0.005 || done >= total)
            {
                _last = fraction;
                progress.Report(fraction);
            }
        }
    }
}
