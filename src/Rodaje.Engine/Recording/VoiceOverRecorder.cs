using Rodaje.Audio.Capture;
using Rodaje.Core.Timing;
using Rodaje.Media.Export;

namespace Rodaje.Engine.Recording;

/// <summary>Una toma terminada: el WAV (mono float) y dónde empieza en la línea de tiempo.</summary>
/// <param name="Frames">Muestras escritas; 0 si no llegó a sonar nada.</param>
public sealed record VoiceOverTake(string Path, long Frames, int SampleRate, Time Start)
{
    public Time Duration => Time.FromSamples(Frames, SampleRate);
}

/// <summary>
/// Graba la voz en off mientras suena la línea de tiempo y la deja alineada con lo que se oía: cada paquete del micrófono
/// trae el instante QPC en que el dispositivo lo capturó, y <c>heardAt</c> da qué posición de la línea de tiempo sonaba
/// en ese instante (el reloj del dispositivo de salida, que ya descuenta su latencia). Así se compensan las latencias de
/// entrada y de salida.
/// </summary>
/// <remarks>
/// La toma empieza exactamente en <c>start</c>: lo capturado antes se descarta y, si la primera muestra llega más tarde,
/// el hueco se rellena con silencio. Una pérdida de muestras de Windows (discontinuidad) también se rellena, para no
/// adelantar lo que sigue. Los paquetes llegan del hilo de captura; el resto, del que crea el grabador. Un candado
/// separa cada paquete del cierre: aunque la captura tarde en detenerse, nada se escribe en el WAV ya cerrado, y un error
/// al escribir (disco lleno) no tumba el hilo de captura: se guarda y lo lanza <see cref="Finish"/>.
/// </remarks>
public sealed class VoiceOverRecorder : IDisposable
{
    private const double QpcUnitsPerSecond = 10_000_000;

    private readonly IAudioCapture _capture;
    private readonly Func<long, Time?> _heardAt;
    private readonly WaveFileWriter _writer;
    private readonly int _rate;
    private readonly float[] _silence = new float[4800];
    private readonly Lock _gate = new();
    private Exception? _failure;
    private bool _started;
    private long _expectedQpc;
    private long _frames;
    private int _peakBits;
    private bool _finished;

    /// <param name="heardAt">Posición de la línea de tiempo que suena en un instante QPC (100 ns); nula si todavía no suena.</param>
    public VoiceOverRecorder(IAudioCapture capture, string path, Time start, Func<long, Time?> heardAt)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(heardAt);
        _capture = capture;
        _heardAt = heardAt;
        _rate = capture.SampleRate;
        Start = start;
        Path = System.IO.Path.GetFullPath(path);
        _writer = new WaveFileWriter(Path, new ExportAudioFormat(_rate, 1, ExportSampleFormat.PcmFloat));
    }

    public string Path { get; }

    public Time Start { get; }

    /// <summary>Lo grabado hasta ahora (incluido el silencio de alineación).</summary>
    public Time Recorded => Time.FromSamples(Interlocked.Read(ref _frames), _rate);

    /// <summary>Si la primera muestra ya quedó alineada (empezó a sonar la línea de tiempo).</summary>
    public bool IsRecording => Volatile.Read(ref _started);

    /// <summary>Pico desde la última lectura, de 0 a 1: para el medidor del diálogo.</summary>
    public float TakePeak() => BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _peakBits, 0));

    public void Begin() => _capture.StartCapture(OnPacket);

    /// <summary>Detiene la captura y cierra el WAV.</summary>
    /// <exception cref="IOException">No se pudo escribir la toma (por ejemplo, el disco se llenó mientras se grababa).</exception>
    public VoiceOverTake Finish()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_finished, this);
        }

        // Fuera del candado: detener la captura espera a su hilo, que puede estar esperando el candado con un paquete.
        _capture.StopCapture();
        Exception? failure;
        lock (_gate)
        {
            _finished = true;
            failure = _failure;
        }

        try
        {
            if (failure is null)
            {
                _writer.Complete();
            }
        }
        catch (ExportException ex)
        {
            failure = ex;
        }

        if (failure is not null)
        {
            throw new IOException("No se pudo escribir la voz en off: " + failure.Message, failure);
        }

        return new VoiceOverTake(Path, Interlocked.Read(ref _frames), _rate, Start);
    }

    public void Dispose()
    {
        bool stop;
        lock (_gate)
        {
            stop = !_finished;
        }

        if (stop)
        {
            _capture.StopCapture();
            lock (_gate)
            {
                _finished = true;
            }
        }

        _writer.Dispose();
        _capture.Dispose();
    }

    private void OnPacket(ReadOnlySpan<float> mono, long qpc, bool discontinuity)
    {
        lock (_gate)
        {
            if (_finished || _failure is not null)
            {
                return;
            }

            try
            {
                Append(mono, qpc, discontinuity);
            }
            catch (Exception ex) when (ex is IOException or ExportException or UnauthorizedAccessException)
            {
                // En el hilo de captura no hay a quién lanzarlo: se guarda para Finish y se deja de escribir.
                _failure = ex;
            }
        }
    }

    private void Append(ReadOnlySpan<float> mono, long qpc, bool discontinuity)
    {
        if (!_started)
        {
            if (_heardAt(qpc) is not { } heard)
            {
                return;
            }

            long offset = (long)Math.Round((heard - Start).ToSeconds() * _rate);
            if (offset < 0)
            {
                if (-offset >= mono.Length)
                {
                    return;
                }

                mono = mono[(int)-offset..];
                qpc += (long)Math.Round(-offset * QpcUnitsPerSecond / _rate);
            }
            else
            {
                WriteSilence(offset);
            }

            Volatile.Write(ref _started, true);
        }
        else if (discontinuity)
        {
            long gap = (long)Math.Round((qpc - _expectedQpc) * _rate / QpcUnitsPerSecond);
            WriteSilence(Math.Max(0, gap));
        }

        float peak = 0;
        foreach (float s in mono)
        {
            peak = MathF.Max(peak, MathF.Abs(s));
        }

        if (peak > BitConverter.Int32BitsToSingle(Volatile.Read(ref _peakBits)))
        {
            Interlocked.Exchange(ref _peakBits, BitConverter.SingleToInt32Bits(peak));
        }

        _writer.Write(mono);
        Interlocked.Add(ref _frames, mono.Length);
        _expectedQpc = qpc + (long)Math.Round(mono.Length * QpcUnitsPerSecond / _rate);
    }

    private void WriteSilence(long frames)
    {
        while (frames > 0)
        {
            int n = (int)Math.Min(frames, _silence.Length);
            _writer.Write(_silence.AsSpan(0, n));
            Interlocked.Add(ref _frames, n);
            frames -= n;
        }
    }
}
