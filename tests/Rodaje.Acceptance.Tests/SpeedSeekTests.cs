using System.Diagnostics;
using System.Globalization;
using Rodaje.Acceptance.Tests.Support;
using Rodaje.Core.Timing;
using Rodaje.Engine.Timeline;
using Rodaje.Playback.Composition;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Acceptance.Tests;

/// <summary>
/// Criterio de la fase 5c: con velocidad, el seek muestra el cuadro esperado en el 100 % de 2000 instantes al azar por
/// caso, también con la curva «Bala». Lo que se ve se lee del número dibujado en el cuadro; lo esperado sale de la aritmética entera de
/// <see cref="SpeedTimeline"/>, que además debe coincidir con <see cref="ClipTimeMap"/>.
/// </summary>
public sealed class SpeedSeekTests(ITestOutputHelper output)
{
    private const int RandomSeeks = 2000;
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(20);

    public static TheoryData<string> Cases => new("0,25x", "0,5x", "1,37x", "2x", "4x", "1001/1000x", "100x", "congelado", "bala", "reversa", "reversa-1,37x");

    [FfmpegLibraryTheory(VideoCodec.H264, Audio = AudioCodec.Aac, LongRunning = true)]
    [MemberData(nameof(Cases))]
    public void Seek_con_velocidad_muestra_el_cuadro_esperado(string name) =>
        Run(name, SpeedTimeline.Build(MediaPath, SpeedCase.Named(name), withAudio: false));

    /// <summary>El mismo recorrido sobre un clip sin velocidad: comprueba la medida con el motor de hoy.</summary>
    [FfmpegLibraryFact(VideoCodec.H264, Audio = AudioCodec.Aac, LongRunning = true)]
    public void Control_sin_velocidad() =>
        Run("control-1x", SpeedTimeline.BuildPlain(MediaPath, Time.FromSeconds(120), withAudio: false));

    /// <summary>Sin motor: la referencia entera y <see cref="ClipTimeMap"/> dan el mismo cuadro en todos los casos.</summary>
    [FfmpegLibraryFact(VideoCodec.H264, Audio = AudioCodec.Aac, LongRunning = true)]
    public void La_referencia_entera_coincide_con_el_contrato()
    {
        foreach (SpeedCase speedCase in SpeedCase.All)
        {
            SpeedTimeline timeline = SpeedTimeline.Build(MediaPath, speedCase, withAudio: true);
            foreach (long frame in Requests(timeline.FrameCount, 1))
            {
                Time at = Time.FromFrames(frame, SpeedTimeline.SequenceRate);
                Assert.True(timeline.ContractFrame(at) == timeline.ExpectedFrame(at),
                    $"{speedCase.Name}, cuadro {frame}: contrato {timeline.ContractFrame(at)}, referencia {timeline.ExpectedFrame(at)}.");
            }

            output.WriteLine($"{speedCase.Name}: {timeline.Map}");
        }
    }

    private static string MediaPath => AcceptanceEnvironment.MediaPath(SpeedMediaPresets.Speed720p16Min);

    /// <summary>Primeros y últimos cuadros del clip y <see cref="RandomSeeks"/> al azar, mezclados.</summary>
    private static List<long> Requests(long frames, int seed)
    {
        var rng = new Random(seed);
        var requests = new List<long> { 0, 1, frames - 2, frames - 1 };
        for (int i = 0; i < RandomSeeks; i++)
        {
            requests.Add(rng.NextInt64(frames));
        }

        rng.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(requests));
        return requests.Where(f => f >= 0 && f < frames).ToList();
    }

    private void Run(string name, SpeedTimeline timeline)
    {
        using var backend = new ManualClockBackend();
        using TimelineSession session = ManualClockBackend.Open(timeline.Project, backend, audio: false);
        List<long> requests = Requests(timeline.FrameCount, name.GetHashCode(StringComparison.Ordinal));

        var failures = new List<string>();
        int contractMismatches = 0;
        var watch = Stopwatch.StartNew();
        foreach (long frame in requests)
        {
            session.SeekToFrame(frame);
            using CompositionFrame? shown = TickUntilSettled(session);
            if (shown is null)
            {
                failures.Add($"{frame}: la capa no llegó en {SettleTimeout.TotalSeconds:0} s");
                continue;
            }

            long expected = timeline.ExpectedFrame(shown.TimelineTime);
            if (timeline.ContractFrame(shown.TimelineTime) != expected)
            {
                contractMismatches++;
                failures.Add($"{frame}: ClipTimeMap da {timeline.ContractFrame(shown.TimelineTime)} y la referencia entera {expected}");
                continue;
            }

            if (shown.TimelineTime != Time.FromFrames(frame, SpeedTimeline.SequenceRate) || shown.Layers.Count != 1)
            {
                failures.Add($"{frame}: se compuso {shown.TimelineTime} con {shown.Layers.Count} capas");
                continue;
            }

            LayerFrame layer = shown.Layers[0];
            int? code = AcceptanceEnvironment.ReadCode(layer.Frame);
            if (layer.Frame.Index != expected || code != expected)
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{frame}: índice {layer.Frame.Index}, código {(code is { } c ? c.ToString(CultureInfo.InvariantCulture) : "ilegible")}, esperado {expected}"));
            }
        }

        watch.Stop();
        int exact = requests.Count - failures.Count;
        output.Report($"velocidad.seek.{name}", $"{exact}/{requests.Count}");
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{name}: {timeline.Map}, {timeline.FrameCount} cuadros de secuencia; {requests.Count} seeks en {watch.Elapsed.TotalSeconds:0.0} s; " +
            $"{contractMismatches} diferencias con el contrato; {session.Stats.DecoderSeeks} seeks de keyframe; errores {session.Stats.DecodeErrors}."));
        Assert.Equal(0, session.Stats.DecodeErrors);
        Assert.True(failures.Count == 0,
            $"{failures.Count} de {requests.Count} seeks no mostraron el cuadro esperado ({name}): {string.Join("; ", failures.Take(20))}");
    }

    private static CompositionFrame? TickUntilSettled(TimelineSession session)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            CompositionFrame frame = session.Tick();
            if (!session.IsSeekPending)
            {
                return frame;
            }

            frame.Dispose();
            if (watch.Elapsed > SettleTimeout)
            {
                return null;
            }

            Thread.Sleep(1);
        }
    }
}
