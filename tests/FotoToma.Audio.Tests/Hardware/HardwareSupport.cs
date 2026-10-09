using System.Diagnostics;
using System.Globalization;
using FotoToma.Playback;

namespace FotoToma.Audio.Tests.Hardware;

internal static class AudioHardware
{
    public const string ToneVariable = "FOTOTOMA_PRUEBAS_AUDIO";

    private static readonly Lazy<string?> MissingDevice = new(() =>
        WasapiOutput.ProbeDefaultDevice(out string description)
            ? null
            : $"No hay un dispositivo de salida de audio usable en este equipo: {description}");

    /// <summary>Nulo si hay dispositivo; si no, el motivo para saltar la prueba.</summary>
    public static string? SkipReason => MissingDevice.Value;

    public static string? ToneSkipReason =>
        Environment.GetEnvironmentVariable(ToneVariable) == "1"
            ? SkipReason
            : $"Esta prueba hace sonar un tono de 3 s; se activa con la variable de entorno {ToneVariable}=1.";
}

/// <summary>Prueba que necesita un dispositivo de salida real; no suena (reproduce silencio).</summary>
internal sealed class AudioDeviceFactAttribute : FactAttribute
{
    public AudioDeviceFactAttribute() => Skip = AudioHardware.SkipReason;
}

internal sealed class AudioDeviceTheoryAttribute : TheoryAttribute
{
    public AudioDeviceTheoryAttribute() => Skip = AudioHardware.SkipReason;
}

/// <summary>Prueba que hace ruido: solo corre con <c>FOTOTOMA_PRUEBAS_AUDIO=1</c> y un dispositivo real.</summary>
internal sealed class AudioToneFactAttribute : FactAttribute
{
    public AudioToneFactAttribute() => Skip = AudioHardware.ToneSkipReason;
}

internal sealed class ToneSource(int sampleRate, double frequency, float amplitude) : IAudioSampleSource
{
    private long _frame;

    public long FramesRead => Interlocked.Read(ref _frame);

    public int Read(Span<float> interleaved)
    {
        int frames = interleaved.Length / 2;
        long start = _frame;
        for (int f = 0; f < frames; f++)
        {
            float sample = amplitude * (float)Math.Sin(2 * Math.PI * frequency * (start + f) / sampleRate);
            interleaved[f * 2] = sample;
            interleaved[(f * 2) + 1] = sample;
        }

        Interlocked.Add(ref _frame, frames);
        return frames;
    }
}

/// <summary>Muestrea el reloj contra un Stopwatch y resume deriva, jitter y monotonía.</summary>
internal sealed record ClockMeasurement(
    double StartCallMs,
    double StartupMs,
    double Slope,
    double MaxResidualMs,
    double RmsResidualMs,
    double MaxHoldMs,
    int Samples,
    int Backwards,
    double EndPositionSeconds)
{
    public double DriftPpm => (Slope - 1) * 1e6;

    public static ClockMeasurement Run(IAudioClock clock, TimeSpan duration)
    {
        var wall = new List<double>(16_384);
        var position = new List<double>(16_384);
        long origin = Stopwatch.GetTimestamp();
        clock.Start();
        double startCall = Stopwatch.GetElapsedTime(origin).TotalMilliseconds;
        long next = Stopwatch.GetTimestamp();
        long step = Stopwatch.Frequency / 2_000;
        while (Stopwatch.GetElapsedTime(origin) < duration)
        {
            while (Stopwatch.GetTimestamp() < next)
            {
                Thread.SpinWait(20);
            }

            next += step;
            double p = clock.Position.ToSeconds();
            wall.Add(Stopwatch.GetElapsedTime(origin).TotalSeconds);
            position.Add(p);
        }

        int backwards = 0;
        int first = -1;
        double maxHold = 0;
        double holdStart = 0;
        for (int i = 1; i < position.Count; i++)
        {
            if (position[i] < position[i - 1])
            {
                backwards++;
            }

            if (first < 0 && position[i] > position[0])
            {
                first = i;
                holdStart = wall[i];
            }
            else if (first >= 0 && position[i] > position[i - 1])
            {
                maxHold = Math.Max(maxHold, wall[i] - holdStart);
                holdStart = wall[i];
            }
        }

        if (first < 0)
        {
            return new ClockMeasurement(startCall, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, position.Count, backwards, position[^1]);
        }

        // Ajuste lineal desde 50 ms después del arranque, cuando el dispositivo ya está en régimen.
        int from = first;
        while (from < wall.Count && wall[from] < wall[first] + 0.05)
        {
            from++;
        }

        double n = wall.Count - from;
        double mx = 0, my = 0;
        for (int i = from; i < wall.Count; i++)
        {
            mx += wall[i];
            my += position[i];
        }

        mx /= n;
        my /= n;
        double sxy = 0, sxx = 0;
        for (int i = from; i < wall.Count; i++)
        {
            sxy += (wall[i] - mx) * (position[i] - my);
            sxx += (wall[i] - mx) * (wall[i] - mx);
        }

        double slope = sxy / sxx;
        double maxResidual = 0, sumSquares = 0;
        for (int i = from; i < wall.Count; i++)
        {
            double residual = position[i] - (my + (slope * (wall[i] - mx)));
            maxResidual = Math.Max(maxResidual, Math.Abs(residual));
            sumSquares += residual * residual;
        }

        // El arranque es el instante en que el ajuste cruza la posición inicial: no depende de cuándo cayó una muestra.
        double startup = (mx - ((my - position[0]) / slope)) * 1_000;
        return new ClockMeasurement(
            startCall,
            startup,
            slope,
            maxResidual * 1_000,
            Math.Sqrt(sumSquares / n) * 1_000,
            maxHold * 1_000,
            position.Count,
            backwards,
            position[^1]);
    }

    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"Start() tardó {StartCallMs:0.00} ms; el reloj arrancó a los {StartupMs:0.00} ms; pendiente {Slope:0.000000} " +
        $"(deriva {DriftPpm:+0.0;-0.0} ppm frente a QPC); residuo máx {MaxResidualMs:0.000} ms, RMS {RmsResidualMs:0.000} ms; " +
        $"mayor tramo sin avanzar {MaxHoldMs:0.000} ms; {Samples} muestras, {Backwards} retrocesos; posición final {EndPositionSeconds:0.0000} s.");
}
