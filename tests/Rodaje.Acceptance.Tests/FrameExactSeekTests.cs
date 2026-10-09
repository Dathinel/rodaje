using System.Collections.Concurrent;
using System.Diagnostics;
using Rodaje.Acceptance.Tests.Support;
using Rodaje.Core.Timing;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Acceptance.Tests;

/// <summary>
/// Criterio 1 de la fase 2: el seek es exacto al cuadro en H.264 y HEVC. Cada cuadro entregado lleva su
/// número dibujado en la luma, así que se compara lo que se ve con lo que se pidió, sin referencia externa.
/// </summary>
public sealed class FrameExactSeekTests(ITestOutputHelper output)
{
    // En la suite normal, una muestra; con RODAJE_PRUEBAS_LARGAS=1 (ABRIR.bat aceptacion), la medida completa.
    private static readonly int RandomRequests = AcceptanceEnvironment.LongRuns ? 500 : 100;
    private static readonly int SchedulerRandomRequests = AcceptanceEnvironment.LongRuns ? 150 : 30;
    private static readonly TimeSpan SeekTimeout = TimeSpan.FromSeconds(20);

    public static TheoryData<string> Presets => new("h264", "hevc");

    [AcceptanceTheory(hardware: true)]
    [MemberData(nameof(Presets))]
    public void GetFrame_es_exacto_por_hardware(string preset) => DirectSeek(preset, DecodePath.Hardware);

    [AcceptanceTheory]
    [MemberData(nameof(Presets))]
    public void GetFrame_es_exacto_por_software(string preset) => DirectSeek(preset, DecodePath.Software);

    [AcceptanceTheory(hardware: true)]
    [MemberData(nameof(Presets))]
    public void SeekToFrame_del_programador_es_exacto_por_hardware(string preset) => SchedulerSeek(preset, DecodePath.Hardware);

    [AcceptanceTheory]
    [MemberData(nameof(Presets))]
    public void SeekToFrame_del_programador_es_exacto_por_software(string preset) => SchedulerSeek(preset, DecodePath.Software);

    internal static MediaSpec Spec(string preset) => preset switch
    {
        "h264" => TestMediaPresets.H264Ntsc30BFrames,
        "hevc" => TestMediaPresets.Hevc25Mkv,
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Preset desconocido."),
    };

    /// <summary>Primero, último, keyframes con sus vecinos y <paramref name="random"/> índices al azar, todo mezclado.</summary>
    internal static List<int> Requests(int frames, IReadOnlyList<int> keys, int random, int seed)
    {
        var rng = new Random(seed);
        var requests = new List<int> { 0, 1, frames - 2, frames - 1 };
        foreach (int k in keys)
        {
            requests.AddRange(new[] { k - 1, k, k + 1 }.Where(i => i >= 0 && i < frames));
        }

        for (int i = 0; i < random; i++)
        {
            requests.Add(rng.Next(frames));
        }

        rng.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(requests));
        return requests;
    }

    private void DirectSeek(string preset, DecodePath path)
    {
        MediaSpec spec = Spec(preset);
        string file = AcceptanceEnvironment.MediaPath(spec);
        IReadOnlyList<int> keys = AcceptanceEnvironment.KeyFrames(file, spec.GopLength, spec.FrameCount);
        Assert.True(keys.Count >= 3, $"El archivo debería tener varios keyframes y tiene {keys.Count}.");
        List<int> requests = Requests(spec.FrameCount, keys, RandomRequests, 20261009);

        using VideoDecoder decoder = VideoDecoder.Open(file, AcceptanceEnvironment.Options(path));
        Assert.Equal(spec.FrameCount, decoder.FrameCount);
        Assert.Equal(spec.FrameRate, decoder.FrameRate);

        var failures = new List<string>();
        var watch = Stopwatch.StartNew();
        foreach (int n in requests)
        {
            using DecodedFrame? frame = decoder.GetFrame(n);
            if (frame is null)
            {
                failures.Add($"{n}: no se entregó");
                continue;
            }

            Assert.Equal(n, frame.Index);
            Assert.Equal(Time.FromFrames(n, spec.FrameRate), frame.Pts);
            int? code = AcceptanceEnvironment.ReadCode(frame);
            if (code != n)
            {
                failures.Add($"{n}: se vio {(code is { } c ? c.ToString(System.Globalization.CultureInfo.InvariantCulture) : "ilegible")}");
            }
        }

        watch.Stop();
        Assert.Equal(path == DecodePath.Hardware, decoder.UsesHardwareDecoding);
        int exact = requests.Count - failures.Count;
        string key = $"seek.{preset}.{path.ToString().ToLowerInvariant()}.getframe";
        output.Report(key, $"{exact}/{requests.Count}");
        output.WriteLine($"{spec.Name} [{path}]: {requests.Count} pedidos ({keys.Count} keyframes) en {watch.ElapsedMilliseconds} ms; {decoder.Statistics}.");
        Assert.True(failures.Count == 0,
            $"{failures.Count} de {requests.Count} cuadros no son el pedido ({spec.Name}, {path}): {string.Join("; ", failures.Take(20))}");
    }

    private void SchedulerSeek(string preset, DecodePath path)
    {
        MediaSpec spec = Spec(preset);
        string file = AcceptanceEnvironment.MediaPath(spec);
        IReadOnlyList<int> keys = AcceptanceEnvironment.KeyFrames(file, spec.GopLength, spec.FrameCount);
        List<int> requests = Requests(spec.FrameCount, keys, SchedulerRandomRequests, 777);

        var options = new PlaybackOptions();
        var timing = new ClipTiming(Time.Zero, Time.Zero, Time.FromFrames(spec.FrameCount, spec.FrameRate), spec.FrameRate);
        var clock = new ManualClock();
        using var ready = new BlockingCollection<long>();
        var failed = new ConcurrentQueue<string>();

        using VideoDecoder decoder = VideoDecoder.Open(file, AcceptanceEnvironment.Options(path));
        using FrameCache cache = AcceptanceEnvironment.CacheFor(decoder, options);
        using var executor = new DecodeThread("Rodaje aceptación seek");
        using var scheduler = new PlaybackScheduler(timing, clock, decoder, cache, executor, options);
        scheduler.ExactFrameReady += (_, e) => ready.Add(e.FrameIndex);
        scheduler.DecodeFailed += (_, e) => failed.Enqueue($"{e.FrameIndex}: {e.Error.Message}");

        var failures = new List<string>();
        var watch = Stopwatch.StartNew();
        foreach (int n in requests)
        {
            scheduler.SeekToFrame(n);
            if (!WaitFor(ready, n))
            {
                failures.Add($"{n}: ExactFrameReady no llegó en {SeekTimeout.TotalSeconds:0} s");
                continue;
            }

            FramePresentation shown = scheduler.Tick();
            if (!shown.IsExact || shown.Frame is null || shown.TargetFrame != n)
            {
                failures.Add($"{n}: el refresco mostró {shown.Frame?.Index} (debido {shown.TargetFrame})");
                continue;
            }

            int? code = AcceptanceEnvironment.ReadCode(shown.Frame);
            if (code != n)
            {
                failures.Add($"{n}: se vio {(code is { } c ? c.ToString(System.Globalization.CultureInfo.InvariantCulture) : "ilegible")}");
            }
        }

        watch.Stop();
        int exact = requests.Count - failures.Count;
        string key = $"seek.{preset}.{path.ToString().ToLowerInvariant()}.programador";
        output.Report(key, $"{exact}/{requests.Count}");
        output.WriteLine($"{spec.Name} [{path}] con PlaybackScheduler: {requests.Count} seeks en {watch.ElapsedMilliseconds} ms; {decoder.Statistics}.");
        Assert.Empty(failed);
        Assert.True(failures.Count == 0,
            $"{failures.Count} de {requests.Count} seeks no mostraron el cuadro pedido ({spec.Name}, {path}): {string.Join("; ", failures.Take(20))}");
    }

    private static bool WaitFor(BlockingCollection<long> ready, long frame)
    {
        DateTime limit = DateTime.UtcNow + SeekTimeout;
        while (true)
        {
            TimeSpan left = limit - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || !ready.TryTake(out long index, left))
            {
                return false;
            }

            if (index == frame)
            {
                return true;
            }
        }
    }
}
