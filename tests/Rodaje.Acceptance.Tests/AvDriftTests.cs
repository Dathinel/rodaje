using System.Diagnostics;
using System.Globalization;
using Rodaje.Acceptance.Tests.Support;
using Rodaje.Audio;
using Rodaje.Core.Timing;
using Rodaje.Media.Audio;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Acceptance.Tests;

/// <summary>
/// Criterio 2 de la fase 2: la deriva entre audio y video queda por debajo de un cuadro en 10 minutos.
/// El video se mide por el número dibujado en cada cuadro mostrado y el audio por la marca de cada segundo.
/// </summary>
public sealed class AvDriftTests(ITestOutputHelper output)
{
    private const int MaxFailures = 200;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Reloj manual que avanza en refrescos de 60 Hz (a la tasa nominal, +0,1 % o −0,1 %) tan rápido como
    /// decodifica el hilo real. Cuando el audio leído por delante trae la marca del segundo s, el reloj cae
    /// justo en ella y el cuadro mostrado debe ser el que contiene el instante s.
    /// </summary>
    [AcceptanceTheory(longRunning: true)]
    [InlineData(0.0)]
    [InlineData(0.001)]
    [InlineData(-0.001)]
    public void Diez_minutos_con_reloj_manual(double clockDrift)
    {
        MediaSpec spec = TestMediaPresets.Drift720p10Min;
        string file = AcceptanceEnvironment.MediaPath(spec);
        DecodePath path = AcceptanceEnvironment.HasHardwareDecoding ? DecodePath.Hardware : DecodePath.Software;
        string label = clockDrift switch
        {
            > 0 => "mas",
            < 0 => "menos",
            _ => "nominal",
        };

        using VideoDecoder decoder = VideoDecoder.Open(file, AcceptanceEnvironment.Options(path));
        using AudioDecoder audio = AudioDecoder.Open(file);
        Rational rate = decoder.FrameRate;
        Assert.Equal(spec.FrameRate, rate);
        Assert.Equal(spec.FrameCount, decoder.FrameCount);
        Time frame = Time.FromFrames(1, rate);
        var marks = new AudioMarkReader(audio);

        var options = new PlaybackOptions();
        var timing = new ClipTiming(Time.Zero, Time.Zero, Time.FromFrames(decoder.FrameCount, rate), rate);
        var clock = new ManualClock();
        var decodeFailures = new List<string>();
        using FrameCache cache = AcceptanceEnvironment.CacheFor(decoder, options);
        using var executor = new QuiescentExecutor();
        using var scheduler = new PlaybackScheduler(timing, clock, decoder, cache, executor, options);
        scheduler.DecodeFailed += (_, e) =>
        {
            lock (decodeFailures)
            {
                decodeFailures.Add($"{e.FrameIndex}: {e.Error.Message}");
            }
        };

        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Caché de {cache.BudgetBytes / 1048576.0:0.0} MB para {options.PrefetchFrames} cuadros adelantados."));
        Time refresh = Time.FromTicks(Time.TicksPerSecond / 60);
        Time clockStep = Time.FromTicks((long)Math.Round(refresh.Ticks * (1 + clockDrift)));
        var failures = new List<string>();
        var missingMarks = new List<int>();
        long ticks = 0;
        long markChecks = 0;
        long matchingTicks = 0;
        long maxFrameError = 0;
        long codeReads = 0;
        Time maxOffset = Time.Zero;
        Time maxMarkOffset = Time.Zero;
        double maxAudioAlignmentMs = 0;
        DecodedFrame? lastFrame = null;
        int lastCode = -1;
        Time anchorClock = Time.Zero;
        Time anchorContent = Time.Zero;
        var watch = Stopwatch.StartNew();

        bool hasMark = NextMark(marks, missingMarks, out SecondMark mark);
        executor.WaitIdle(IdleTimeout);
        scheduler.Play();
        while (failures.Count < MaxFailures)
        {
            Time position = clock.Position;
            Time step = clockStep;
            bool atMark = false;
            if (hasMark)
            {
                Time markClock = Time.FromSamples(mark.Sample!.Value, AudioDecoder.OutputSampleRate);
                if (position + step >= markClock)
                {
                    step = Time.Max(Time.Zero, markClock - position);
                    atMark = true;
                }
            }

            if (position + step >= timing.End)
            {
                break;
            }

            clock.Advance(step);
            executor.WaitIdle(IdleTimeout);
            FramePresentation shown = scheduler.Tick();
            ticks++;
            if (shown.Frame is null || !shown.IsExact)
            {
                Fail(failures, $"Reloj en {shown.SourceTime}: debía verse {shown.TargetFrame} y se vio {shown.Frame?.Index}.");
                continue;
            }

            if (!ReferenceEquals(shown.Frame, lastFrame))
            {
                lastFrame = shown.Frame;
                lastCode = AcceptanceEnvironment.ReadCode(shown.Frame) ?? -1;
                codeReads++;
                if (lastCode != shown.Frame.Index)
                {
                    Fail(failures, $"El cuadro {shown.Frame.Index} lleva dibujado el {lastCode}.");
                }
            }

            if (atMark)
            {
                anchorClock = shown.SourceTime;
                anchorContent = Time.FromSeconds(mark.Second);
                maxAudioAlignmentMs = Math.Max(maxAudioAlignmentMs, Math.Abs((anchorClock - anchorContent).ToSeconds() * 1000));
            }

            // Lo que suena es el contenido del audio en la posición del reloj; lo que se ve, el cuadro con su número.
            Time audioContent = anchorContent + (shown.SourceTime - anchorClock);
            Time videoContent = Time.FromFrames(lastCode, rate);
            Time offset = audioContent - videoContent;
            Time magnitude = offset < Time.Zero ? -offset : offset;
            maxOffset = Time.Max(maxOffset, magnitude);
            long frameError = Math.Abs(lastCode - audioContent.ToFrames(rate));
            maxFrameError = Math.Max(maxFrameError, frameError);
            if (frameError == 0)
            {
                matchingTicks++;
            }
            if (offset < Time.Zero || offset >= frame)
            {
                Fail(failures, $"Reloj en {shown.SourceTime}: suena {audioContent} y se ve el cuadro {lastCode} ({videoContent}).");
            }

            if (atMark)
            {
                markChecks++;
                maxMarkOffset = Time.Max(maxMarkOffset, magnitude);
                long expected = Time.FromSeconds(mark.Second).ToFrames(rate);
                if (lastCode != expected)
                {
                    Fail(failures, $"Marca del segundo {mark.Second}: se veía el cuadro {lastCode} y correspondía el {expected}.");
                }

                hasMark = NextMark(marks, missingMarks, out mark);
            }
        }

        watch.Stop();
        PlaybackStats stats = scheduler.Stats;
        double frameMs = frame.ToSeconds() * 1000;
        string key = "deriva.manual." + label;
        output.Report(key + ".desfase_max_ms", $"{maxOffset.ToSeconds() * 1000:0.000}");
        output.Report(key + ".desfase_max_cuadros", $"{maxOffset.ToSeconds() * 1000 / frameMs:0.000}");
        output.Report(key + ".marcas", $"{markChecks}");
        output.Report(key + ".refrescos_con_el_cuadro_del_audio", $"{matchingTicks}/{ticks}");
        output.Report(key + ".error_max_cuadros", $"{maxFrameError}");
        output.Report(key + ".alineacion_audio_ms", $"{maxAudioAlignmentMs:0.000}");
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{spec.Name} [{path}], reloj {1 + clockDrift:0.000}×: {ticks} refrescos y {markChecks} marcas en {watch.Elapsed.TotalSeconds:0.0} s; " +
            $"posición final {clock.Position}; desfase máximo {maxOffset.ToSeconds() * 1000:0.000} ms ({maxOffset.ToSeconds() * 1000 / frameMs:0.000} cuadros, " +
            $"un cuadro dura {frameMs:0.000} ms); en las marcas {maxMarkOffset.ToSeconds() * 1000:0.000} ms; marcas de audio corridas hasta " +
            $"{maxAudioAlignmentMs:0.000} ms de su segundo; {codeReads} cuadros leídos; {stats}; {decoder.Statistics}."));

        Assert.Empty(decodeFailures);
        Assert.True(missingMarks.Count == 0, $"No se encontraron las marcas de los segundos {string.Join(", ", missingMarks.Take(20))}.");
        Assert.True(failures.Count == 0, $"{failures.Count} fallos; los primeros: {string.Join(" | ", failures.Take(10))}");
        Assert.True(markChecks >= 599, $"Solo se comprobaron {markChecks} marcas de audio.");
        Assert.True(maxOffset < frame, $"Desfase máximo {maxOffset}, un cuadro dura {frame}.");
        Assert.Equal(0, maxFrameError);
        Assert.Equal(0, stats.LateRefreshes);
        Assert.Equal(0, stats.FramesDropped);
        Assert.Equal(0, stats.DecodeErrors);
        Assert.True(stats.MaxAvOffset < frame);
    }

    /// <summary>
    /// 30 s en tiempo real con la salida de audio de verdad (volumen 0) como reloj maestro y un bucle de
    /// render a 60 Hz: se mide la distancia entre el reloj y el cuadro que se ve.
    /// </summary>
    [AcceptanceFact(longRunning: true)]
    public void Treinta_segundos_en_tiempo_real_con_el_reloj_del_dispositivo()
    {
        MediaSpec spec = TestMediaPresets.Drift720p10Min;
        string file = AcceptanceEnvironment.MediaPath(spec);
        DecodePath path = AcceptanceEnvironment.HasHardwareDecoding ? DecodePath.Hardware : DecodePath.Software;
        TimeSpan duration = TimeSpan.FromSeconds(30);

        using VideoDecoder decoder = VideoDecoder.Open(file, AcceptanceEnvironment.Options(path));
        using AudioDecoder audioDecoder = AudioDecoder.Open(file);
        using IAudioOutput audio = AudioOutput.Open(new DecoderSource(audioDecoder), new AudioOutputOptions { Volume = 0f });
        Rational rate = decoder.FrameRate;
        Time frame = Time.FromFrames(1, rate);

        var options = new PlaybackOptions();
        var timing = new ClipTiming(Time.Zero, Time.Zero, Time.FromFrames(decoder.FrameCount, rate), rate);
        using FrameCache cache = AcceptanceEnvironment.CacheFor(decoder, options);
        using var executor = new DecodeThread("Rodaje aceptación tiempo real");
        using var scheduler = new PlaybackScheduler(timing, audio.Clock, decoder, cache, executor, options);

        var startWait = Stopwatch.StartNew();
        while (!scheduler.Tick().IsExact)
        {
            Assert.True(startWait.Elapsed < TimeSpan.FromSeconds(10), "El primer cuadro no llegó en 10 s.");
            Thread.Sleep(2);
        }

        long ticks = 0;
        long inSync = 0;
        long codeErrors = 0;
        Time maxOffset = Time.Zero;
        long distinct = 0;
        long codeChecks = 0;
        DecodedFrame? lastFrame = null;
        long period = Stopwatch.Frequency / 60;
        scheduler.Play();
        long origin = Stopwatch.GetTimestamp();
        long next = origin;
        while (Stopwatch.GetTimestamp() - origin < duration.TotalSeconds * Stopwatch.Frequency)
        {
            next += period;
            while (Stopwatch.GetTimestamp() < next)
            {
                Thread.SpinWait(50);
            }

            FramePresentation shown = scheduler.Tick();
            ticks++;
            if (shown.Frame is null)
            {
                continue;
            }

            // Leer la luma detiene al decodificador (copia y espera a la GPU con el contexto tomado), así que en
            // tiempo real solo se comprueba el número dibujado de un cuadro por segundo; el resto lo cubren las
            // pruebas con reloj manual, que lo leen en todos.
            if (!ReferenceEquals(shown.Frame, lastFrame))
            {
                lastFrame = shown.Frame;
                if (distinct++ % 30 == 0)
                {
                    codeChecks++;
                    if (AcceptanceEnvironment.ReadCode(shown.Frame) != shown.Frame.Index)
                    {
                        codeErrors++;
                    }
                }
            }

            Time offset = shown.SourceTime - shown.Frame.Pts;
            maxOffset = Time.Max(maxOffset, offset < Time.Zero ? -offset : offset);
            if (offset >= Time.Zero && offset < frame)
            {
                inSync++;
            }
        }

        scheduler.Pause();
        PlaybackStats stats = scheduler.Stats;
        AudioOutputStats audioStats = audio.Stats;
        double frameMs = frame.ToSeconds() * 1000;
        double inSyncPercent = 100.0 * inSync / Math.Max(1, ticks);
        output.Report("tiempo_real.dispositivo", $"{(audio.HasDevice ? audio.Device!.Name : "sin dispositivo: " + audio.FallbackReason)}");
        output.Report("tiempo_real.desfase_max_ms", $"{maxOffset.ToSeconds() * 1000:0.000}");
        output.Report("tiempo_real.desfase_max_cuadros", $"{maxOffset.ToSeconds() * 1000 / frameMs:0.000}");
        output.Report("tiempo_real.refrescos_en_sincronia", $"{inSyncPercent:0.00}");
        output.Report("tiempo_real.refrescos_tarde", $"{stats.LateRefreshes}/{ticks}");
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{spec.Name} [{path}], {duration.TotalSeconds:0} s en tiempo real: {ticks} refrescos, {inSyncPercent:0.00} % con el cuadro debido; {codeChecks} cuadros con el número comprobado; " +
            $"desfase máximo {maxOffset.ToSeconds() * 1000:0.000} ms ({maxOffset.ToSeconds() * 1000 / frameMs:0.000} cuadros); reloj final {audio.Clock.Position}; " +
            $"{stats}; audio: {audioStats}."));

        Assert.True(codeChecks >= 20, $"Solo se comprobó el número de {codeChecks} cuadros.");
        Assert.Equal(0, codeErrors);
        Assert.Equal(0, stats.DecodeErrors);
        Assert.InRange(audio.Clock.Position.ToSeconds(), duration.TotalSeconds - 0.5, duration.TotalSeconds + 0.5);
        Assert.True(inSyncPercent >= 99.5, $"Solo {inSyncPercent:0.00} % de los refrescos mostraron el cuadro debido.");
        Assert.True(maxOffset < frame + frame, $"Desfase máximo {maxOffset}, un cuadro dura {frame}.");
    }

    private static bool NextMark(AudioMarkReader marks, List<int> missing, out SecondMark mark)
    {
        while (marks.TryNext(out mark))
        {
            if (mark.Sample is not null)
            {
                return true;
            }

            missing.Add(mark.Second);
        }

        return false;
    }

    private static void Fail(List<string> failures, string message)
    {
        if (failures.Count < MaxFailures)
        {
            failures.Add(message);
        }
    }

    /// <summary>El audio del archivo como fuente de la salida; la salida lo llama siempre desde su hilo.</summary>
    private sealed class DecoderSource(AudioDecoder decoder) : IAudioSampleSource
    {
        public int Read(Span<float> interleaved) => decoder.Read(interleaved);

        public void Seek(Time position) => decoder.Seek(position);
    }
}
