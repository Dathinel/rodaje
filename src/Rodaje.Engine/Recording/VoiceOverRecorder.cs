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

/// <summary>Qué hacer con la toma al cerrar el diálogo de la voz en off.</summary>
public enum VoiceOverOutcome
{
    /// <summary>Se canceló en la cuenta atrás: se descarta sin avisar, aunque haya fallado la escritura.</summary>
    Discard,

    /// <summary>Falló la escritura (por ejemplo, el disco se llenó): se avisa del error.</summary>
    ReportFailure,

    /// <summary>No llegó a grabarse nada: se descarta y se avisa.</summary>
    Empty,

    /// <summary>La toma entra en la línea de tiempo.</summary>
    Insert,
}

/// <summary>Escribe un bloque de muestras mono en la toma.</summary>
internal delegate void VoiceOverSampleWriter(ReadOnlySpan<float> mono);

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
/// al escribir (disco lleno) no tumba el hilo de captura: se guarda, se ve enseguida en <see cref="Failure"/>
/// y lo lanza <see cref="Finish"/>.
/// </remarks>
public sealed class VoiceOverRecorder : IDisposable
{
    private const double QpcUnitsPerSecond = 10_000_000;

    private readonly IAudioCapture _capture;
    private readonly Func<long, Time?> _heardAt;
    private readonly WaveFileWriter _writer;
    private readonly VoiceOverSampleWriter _write;
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
        : this(capture, path, start, heardAt, null)
    {
    }

    /// <param name="write">Sustituye la escritura en el WAV (para simular un disco lleno en las pruebas); nulo, la normal.</param>
    internal VoiceOverRecorder(IAudioCapture capture, string path, Time start, Func<long, Time?> heardAt, VoiceOverSampleWriter? write)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(heardAt);
        _capture = capture;
        _heardAt = heardAt;
        _rate = capture.SampleRate;
        Start = start;
        Path = System.IO.Path.GetFullPath(path);
        _writer = new WaveFileWriter(Path, new ExportAudioFormat(_rate, 1, ExportSampleFormat.PcmFloat));
        _write = write ?? _writer.Write;
    }

    public string Path { get; }

    public Time Start { get; }

    /// <summary>Lo grabado hasta ahora (incluido el silencio de alineación).</summary>
    public Time Recorded => Time.FromSamples(Interlocked.Read(ref _frames), _rate);

    /// <summary>Si la primera muestra ya quedó alineada (empezó a sonar la línea de tiempo).</summary>
    public bool IsRecording => Volatile.Read(ref _started);

    /// <summary>
    /// El error de escritura que detuvo la toma (por ejemplo, el disco se llenó), o nulo. Se puede leer desde cualquier
    /// hilo mientras se graba, para avisar en el momento en vez de esperar a <see cref="Finish"/>.
    /// </summary>
    public Exception? Failure
    {
        get
        {
            lock (_gate)
            {
                return _failure;
            }
        }
    }

    /// <summary>Qué hacer con la toma: cancelar en la cuenta atrás descarta sin avisar, aunque haya fallado la escritura.</summary>
    public static VoiceOverOutcome Decide(bool cancelled, bool failed, long frames) =>
        cancelled ? VoiceOverOutcome.Discard
        : failed ? VoiceOverOutcome.ReportFailure
        : frames == 0 ? VoiceOverOutcome.Empty
        : VoiceOverOutcome.Insert;

    /// <summary>Pico desde la última lectura, de 0 a 1: para el medidor del diálogo.</summary>
    public float TakePeak() => BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _peakBits, 0));

    public void Begin() => _capture.StartCapture(OnPacket);

    /// <summary>
    /// Para las pruebas: se llama al entrar en <see cref="Finish"/>, antes de su primer candado. Con un paquete dentro del
    /// candado, lo siguiente que hace <see cref="Finish"/> es quedarse esperándolo; sin este aviso, la prueba no puede saber
    /// si su hilo ya llegó ahí o sigue arrancando.
    /// </summary>
    internal Action? BeforeFinishGate { get; set; }

    /// <summary>Detiene la captura y cierra el WAV.</summary>
    /// <exception cref="IOException">No se pudo escribir la toma (por ejemplo, el disco se llenó mientras se grababa).</exception>
    public VoiceOverTake Finish()
    {
        BeforeFinishGate?.Invoke();
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
        try
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
        }
        finally
        {
            // Si algo lanza al cerrar, el WAV y la captura se sueltan igual.
            try
            {
                _writer.Dispose();
            }
            finally
            {
                _capture.Dispose();
            }
        }
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

        _write(mono);
        Interlocked.Add(ref _frames, mono.Length);
        _expectedQpc = qpc + (long)Math.Round(mono.Length * QpcUnitsPerSecond / _rate);
    }

    private void WriteSilence(long frames)
    {
        while (frames > 0)
        {
            int n = (int)Math.Min(frames, _silence.Length);
            _write(_silence.AsSpan(0, n));
            Interlocked.Add(ref _frames, n);
            frames -= n;
        }
    }
}
