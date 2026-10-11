using System.Diagnostics;
using System.Globalization;
using Rodaje.Acceptance.Tests.Support;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Timeline;
using Rodaje.Playback.Composition;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Acceptance.Tests;

/// <summary>
/// Criterio de la fase 5c: 10 minutos a 1,5×, a 0,5× y con la curva «Montaje» repetida clip tras clip, sin deriva. El
/// video, con un reloj manual a 60 Hz: en cada refresco el cuadro mostrado (por su número dibujado) es el que manda el
/// mapeo. El audio, leyendo la mezcla tal como la leería la salida: cada chirp del origen suena donde el mapeo lo pone,
/// con ≤5 ms de error medio por minuto (≤1 ms con «Montaje») y ≤1 ms de diferencia entre el primer y el último minuto.
/// </summary>
public sealed class SpeedDriftTests(ITestOutputHelper output)
{

    private const int Rate = TimelinePlan.AudioSampleRate;
    private const int Channels = 2;
    private const int MaxFailures = 20;
    private const double MaxMeanErrorMs = 5;
    private const double MaxMontageMeanErrorMs = 1;
    private const double MaxMinuteDriftMs = 1;
    private static readonly Time Length = Time.FromSeconds(600);
    private static readonly Time ControlLength = Time.FromSeconds(60);
    private static readonly Time MontageMaterial = Time.FromSeconds(8);
    private static readonly Time Refresh = Time.FromTicks(Time.TicksPerSecond / 60);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    public static TheoryData<string> Rates => new("1,5x", "0,5x");

    public static TheoryData<string, bool> AudioCases => new()
    {
        { "1,5x", true },
        { "0,5x", true },
        { "1,5x", false },
        { "0,5x", false },
    };

    [FfmpegLibraryTheory(VideoCodec.H264, Audio = AudioCodec.Aac, LongRunning = true)]
    [MemberData(nameof(Rates))]
    public void Video_diez_minutos_sin_deriva(string name) =>
        Video(name, SpeedTimeline.Build(MediaPath, SpeedCase.Named(name), withAudio: true, Length));

    [FfmpegLibraryTheory(VideoCodec.H264, Audio = AudioCodec.Aac, LongRunning = true)]
    [MemberData(nameof(AudioCases))]
    public void Audio_diez_minutos_sin_deriva(string name, bool preservePitch)
    {
        SpeedCase speedCase = SpeedCase.Named(name);
        Audio($"{name}{(preservePitch ? "" : ".sin_tono")}", SpeedTimeline.Build(MediaPath, speedCase, withAudio: true, Length, preservePitch),
            (double)speedCase.Rate.Num / speedCase.Rate.Den, preservePitch);
    }

    /// <summary>«Montaje» en clips seguidos de 8 s de material cada uno durante 10 minutos: cortes y rampas sin parar.</summary>
    [FfmpegLibraryFact(VideoCodec.H264, Audio = AudioCodec.Aac, LongRunning = true)]
    public void Video_montaje_repetida_diez_minutos_sin_deriva() =>
        Video("montaje", SpeedTimeline.BuildRepeated(MediaPath, SpeedPreset.Montage, Length, MontageMaterial));

    [FfmpegLibraryFact(VideoCodec.H264, Audio = AudioCodec.Aac, LongRunning = true)]
    public void Audio_montaje_repetida_diez_minutos_sin_deriva() =>
        Audio("montaje", SpeedTimeline.BuildRepeated(MediaPath, SpeedPreset.Montage, Length, MontageMaterial), 2.5, preservePitch: true,
            MaxMontageMeanErrorMs);

    /// <summary>El recorrido de video sobre un clip sin velocidad: comprueba la medida con el motor de hoy.</summary>
    [FfmpegLibraryFact(VideoCodec.H264, Audio = AudioCodec.Aac, LongRunning = true)]
    public void Control_video_sin_velocidad() =>
        Video("control-1x", SpeedTimeline.BuildPlain(MediaPath, ControlLength, withAudio: true));

    /// <summary>El recorrido de audio sobre un clip sin velocidad.</summary>
    [FfmpegLibraryFact(VideoCodec.H264, Audio = AudioCodec.Aac, LongRunning = true)]
    public void Control_audio_sin_velocidad() =>
        Audio("control-1x", SpeedTimeline.BuildPlain(MediaPath, ControlLength, withAudio: true), 1, preservePitch: true);

    private static string MediaPath => AcceptanceEnvironment.MediaPath(SpeedMediaPresets.Speed720p16Min);

    private void Video(string name, SpeedTimeline timeline)
    {
        using var backend = new ManualClockBackend();
        using TimelineSession session = ManualClockBackend.Open(timeline.Project, backend, audio: true);
        using (CompositionFrame? first = Settle(session, f => !session.IsSeekPending))
        {
            Assert.NotNull(first);
        }

        var failures = new List<string>();
        long refreshes = 0;
        long waits = 0;
        long codeReads = 0;
        long maxFirstTickError = 0;
        long lastIndex = -1;
        var watch = Stopwatch.StartNew();
        session.Play();
        Time end = timeline.End;
        while (failures.Count < MaxFailures && backend.Clock.Position + Refresh < end)
        {
            backend.Clock.Advance(Refresh);
            refreshes++;
            Time due = Time.FromFrames(backend.Clock.Position.ToFrames(SpeedTimeline.SequenceRate), SpeedTimeline.SequenceRate);
            long expected = timeline.ExpectedFrame(due);
            bool firstTick = true;
            using CompositionFrame? shown = Settle(session, f =>
            {
                bool ready = f.Layers.Count == 1 && f.Layers[0].Frame.Index == expected;
                if (firstTick && f.Layers.Count == 1)
                {
                    maxFirstTickError = Math.Max(maxFirstTickError, Math.Abs(f.Layers[0].Frame.Index - expected));
                }

                if (!ready && firstTick)
                {
                    waits++;
                }

                firstTick = false;
                return ready;
            });
            if (shown is null)
            {
                failures.Add($"reloj {backend.Clock.Position}: no llegó el cuadro {expected} en {WaitTimeout.TotalSeconds:0} s");
                continue;
            }

            if (shown.TimelineTime != due)
            {
                failures.Add($"reloj {backend.Clock.Position}: se compuso {shown.TimelineTime} y tocaba {due}");
                continue;
            }

            if (shown.Layers[0].Frame.Index != lastIndex)
            {
                lastIndex = shown.Layers[0].Frame.Index;
                codeReads++;
                int? code = AcceptanceEnvironment.ReadCode(shown.Layers[0].Frame);
                if (code != expected)
                {
                    failures.Add($"reloj {backend.Clock.Position}: el cuadro {expected} lleva dibujado {code?.ToString(CultureInfo.InvariantCulture) ?? "nada legible"}");
                }
            }
        }

        watch.Stop();
        TimelineStats stats = session.Stats;
        output.Report($"velocidad.deriva.{name}.video_cuadros_distintos", $"{failures.Count}");
        output.Report($"velocidad.deriva.{name}.video_refrescos_que_esperaron", $"{waits}/{refreshes}");
        output.Report($"velocidad.deriva.{name}.video_error_max_primer_refresco", $"{maxFirstTickError}");
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{name}: {timeline.Map}; {refreshes} refrescos en {watch.Elapsed.TotalSeconds:0.0} s, {codeReads} cuadros leídos; reloj final {backend.Clock.Position}; " +
            $"mostrados {stats.FramesShown}, saltados {stats.FramesDropped}, tarde {stats.LateRefreshes}, errores {stats.DecodeErrors}."));
        Assert.Equal(0, stats.DecodeErrors);
        Assert.True(refreshes >= (long)(timeline.End.ToSeconds() * 60) - 2, $"Solo hubo {refreshes} refrescos.");
        Assert.True(failures.Count == 0, $"{failures.Count} refrescos con deriva; los primeros: {string.Join(" | ", failures.Take(10))}");
    }

    /// <param name="rate">La velocidad (la mayor, en una curva): da la plantilla sin mantener el tono y lo lejos que se busca.</param>
    private void Audio(string name, SpeedTimeline timeline, double rate, bool preservePitch, double maxMeanErrorMs = MaxMeanErrorMs)
    {
        using var backend = new ManualClockBackend();
        using TimelineSession session = ManualClockBackend.Open(timeline.Project, backend, audio: true);
        float[] mono = ReadMix(session, backend, (int)(timeline.End.ToSeconds() * Rate));
        float[] template = RetimedAudioCode.ChirpTemplate(Rate, rate, preservePitch);
        int near = Rate / 20;
        int period = (int)(Rate / rate);

        var errorsByMinute = new SortedDictionary<int, List<double>>();
        var missing = new List<int>();
        double minCorrelation = 1;
        List<Time> instants = timeline.ChirpInstants();
        for (int index = 0; index < instants.Count; index++)
        {
            Time at = instants[index];
            long expected = (long)((Int128)at.Ticks * Rate / Time.TicksPerSecond);
            if (expected - near < 0 || expected + near + template.Length > mono.Length)
            {
                continue;
            }

            AudioMark? found = RetimedAudioCode.FindChirp(mono, template, (int)expected - near, (int)expected + near)
                ?? RetimedAudioCode.FindChirp(mono, template, (int)expected - (period / 2), (int)expected + (period / 2), 0.5);
            if (found is not { } mark)
            {
                missing.Add(index);
                continue;
            }

            minCorrelation = Math.Min(minCorrelation, mark.Correlation);
            int minute = (int)(at.Ticks / Time.FromSeconds(60).Ticks);
            if (!errorsByMinute.TryGetValue(minute, out List<double>? errors))
            {
                errorsByMinute[minute] = errors = [];
            }

            errors.Add((mark.Sample - expected) * 1000.0 / Rate);
        }

        Assert.True(errorsByMinute.Count > 0, $"{name}: no se encontró ninguna marca.");
        var lines = errorsByMinute.Select(m => (Minute: m.Key, Count: m.Value.Count, Mean: m.Value.Average(), MeanAbs: m.Value.Average(Math.Abs), Max: m.Value.Max(Math.Abs))).ToList();
        double worstMeanAbs = lines.Max(l => l.MeanAbs);
        double minuteDrift = Math.Abs(lines[^1].Mean - lines[0].Mean);
        int marks = lines.Sum(l => l.Count);
        output.Report($"velocidad.deriva.{name}.audio_error_medio_max_ms", $"{worstMeanAbs:0.000}");
        output.Report($"velocidad.deriva.{name}.audio_primer_a_ultimo_minuto_ms", $"{minuteDrift:0.000}");
        output.Report($"velocidad.deriva.{name}.audio_marcas", $"{marks}");
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{name}: {timeline.Map}; {marks} marcas, {missing.Count} sin encontrar, correlación mínima {minCorrelation:0.000}; underruns {session.Stats.Audio.Underruns}."));
        foreach (var line in lines)
        {
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  minuto {line.Minute}: {line.Count} marcas, error medio {line.Mean:+0.000;-0.000} ms, medio absoluto {line.MeanAbs:0.000} ms, máximo {line.Max:0.000} ms"));
        }

        Assert.True(missing.Count == 0, $"{name}: no se encontraron las marcas {string.Join(", ", missing.Take(20))} (de {instants.Count}).");
        Assert.True(worstMeanAbs <= maxMeanErrorMs, $"{name}: un minuto tiene {worstMeanAbs:0.000} ms de error medio (máximo {maxMeanErrorMs} ms).");
        Assert.True(minuteDrift <= MaxMinuteDriftMs, $"{name}: el error medio cambió {minuteDrift:0.000} ms entre el primer y el último minuto (máximo {MaxMinuteDriftMs} ms).");
    }

    /// <summary>
    /// Lee la mezcla desde el inicio como la salida de audio, de 100 ms en 100 ms, esperando a que esté lista: una lectura
    /// sin el audio listo adelanta la posición igual y desplazaría todo lo que sigue.
    /// </summary>
    private static float[] ReadMix(TimelineSession session, ManualClockBackend backend, int frames)
    {
        const int chunk = Rate / 10;
        var mono = new float[frames];
        var block = new float[chunk * Channels];
        int read = 0;
        while (read < frames)
        {
            int want = Math.Min(chunk, frames - read);
            var watch = Stopwatch.StartNew();
            while (session.BufferedAudioFrames < want)
            {
                Assert.True(watch.Elapsed < WaitTimeout, $"La mezcla no tuvo {want} cuadros listos en {WaitTimeout.TotalSeconds:0} s (leídos {read}).");
                Thread.Sleep(1);
            }

            int got = backend.Mix.Read(block.AsSpan(0, want * Channels));
            Assert.Equal(want, got);
            RetimedAudioCode.Downmix(block.AsSpan(0, want * Channels), Channels, mono.AsSpan(read, want));
            read += want;
        }

        return mono;
    }

    /// <summary>Refresca sin avanzar el reloj hasta que <paramref name="ready"/> acepta la composición; null si no llega.</summary>
    private static CompositionFrame? Settle(TimelineSession session, Func<CompositionFrame, bool> ready)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            CompositionFrame frame = session.Tick();
            if (ready(frame))
            {
                return frame;
            }

            frame.Dispose();
            if (watch.Elapsed > WaitTimeout)
            {
                return null;
            }

            Thread.Sleep(1);
        }
    }
}
