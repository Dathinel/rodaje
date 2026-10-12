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
/// adelantar lo que sigue. Los paquetes llegan del hilo de captura; el resto, del que crea el grabador.
/// </remarks>
public sealed class VoiceOverRecorder : IDisposable
{
    private const double QpcUnitsPerSecond = 10_000_000;

    private readonly IAudioCapture _capture;
    private readonly Func<long, Time?> _heardAt;
    private readonly WaveFileWriter _writer;
    private readonly int _rate;
    private readonly float[] _silence = new float[4800];
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
    public VoiceOverTake Finish()
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        _capture.StopCapture();
        _finished = true;
        _writer.Complete();
        return new VoiceOverTake(Path, _frames, _rate, Start);
    }

    public void Dispose()
    {
        if (!_finished)
        {
            _capture.StopCapture();
            _finished = true;
        }

        _writer.Dispose();
        _capture.Dispose();
    }

    private void OnPacket(ReadOnlySpan<float> mono, long qpc, bool discontinuity)
    {
        if (_finished)
        {
            return;
        }

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
