using System.Collections.Concurrent;
using Rodaje.Media.Probe;
using Rodaje.Media.Proxies;
using Rodaje.Media.Tests.Support;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Media.Tests.Proxies;

/// <summary>Un almacén y un generador compartidos por las pruebas de la clase, con cada proxy generado una vez.</summary>
public sealed class ProxyGenerationFixture : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<ProxyEntry>>> _proxies = new();

    public ProxyGenerationFixture()
    {
        Store = new ProxyStore(_temp.Combine("proxies con espacio y tildes ñ"));
        if (ProxyTestEnvironment.Executable is { } exe)
        {
            Generator = new ProxyGenerator(Store, exe);
        }
    }

    internal ProxyStore Store { get; }

    internal ProxyGenerator? Generator { get; }

    internal Task<ProxyEntry> ProxyOf(MediaSpec spec) => _proxies.GetOrAdd(spec.Name, _ => new Lazy<Task<ProxyEntry>>(
        () => Generator!.GenerateAsync(ProxyTestMedia.Get(spec).Path))).Value;

    public void Dispose()
    {
        Generator?.Dispose();
        _temp.Dispose();
    }
}

public sealed class ProxyGenerationTests(ProxyGenerationFixture fixture, ITestOutputHelper output) : IClassFixture<ProxyGenerationFixture>
{
    private const int RandomFrames = 300;

    public static TheoryData<string> Specs => ["h264-2997-bframes", "hevc10-desfase", "h264-desfase"];

    [ProxyTheory]
    [MemberData(nameof(Specs))]
    public async Task Proxy_has_the_same_frames_and_frame_n_shows_source_frame_n(string name)
    {
        MediaSpec spec = SpecNamed(name);
        GeneratedMedia source = ProxyTestMedia.Get(spec);
        ProxyEntry entry = await fixture.ProxyOf(spec);
        MediaInfo sourceInfo = MediaProbe.Probe(source.Path);
        MediaInfo proxyInfo = MediaProbe.Probe(entry.ProxyPath);

        Assert.Equal(spec.FrameCount, entry.Metadata.FrameCount);
        Assert.Equal(960, proxyInfo.Video!.DisplayWidth);
        Assert.Equal(540, proxyInfo.Video.DisplayHeight);
        Assert.Equal("h264", proxyInfo.Video.Codec);
        Assert.False(proxyInfo.HasAudio);
        Assert.Equal(sourceInfo.Video!.FrameRate, proxyInfo.Video.FrameRate);
        Assert.Equal(sourceInfo.Video.StartTime, proxyInfo.Video.StartTime);
        Assert.Equal(sourceInfo.Video.StartTime.Ticks, entry.Metadata.SourceStartTicks);
        Assert.Equal(spec.StartOffset.TotalSeconds, proxyInfo.Video.StartTime.ToSeconds(), 3);

        // Decodificación completa en orden con ffmpeg.exe: ni un cuadro de más ni de menos, cada uno en su sitio.
        var misplaced = new List<string>();
        int count = FfmpegTool.Default!.DecodeGrayFrames(entry.ProxyPath, 960, 540, (i, gray) =>
        {
            int? code = FrameCode.Read(gray, 960, 540, 960);
            if (code != i && misplaced.Count < 10)
            {
                misplaced.Add($"{i}→{code?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "ilegible"}");
            }
        });
        Assert.Equal(spec.FrameCount, count);
        Assert.Empty(misplaced);

        // Acceso aleatorio con el decodificador de Rodaje: el cuadro n del proxy es el cuadro n del original.
        using VideoDecoder decoder = VideoDecoder.Open(entry.ProxyPath);
        Assert.Equal(spec.FrameCount, decoder.FrameCount);
        var random = new Random(spec.Name.Length * 7919);
        var wrong = new List<string>();
        for (int k = 0; k < RandomFrames; k++)
        {
            int n = random.Next(spec.FrameCount);
            using DecodedFrame? frame = decoder.GetFrame(n);
            Assert.NotNull(frame);
            LumaImage luma = FrameReadback.ReadLuma(frame);
            int? code = FrameCode.Read(luma.Pixels, luma.Width, luma.Height, luma.Width);
            if (code != n)
            {
                wrong.Add($"{n}→{code?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "ilegible"}");
            }
        }

        Assert.Empty(wrong);
        output.WriteLine($"{spec.Name}: {count} cuadros, {RandomFrames} accesos aleatorios exactos; {entry.Metadata.Encoder}, " +
            $"{entry.Metadata.Speed:0.0}× tiempo real, {entry.Metadata.ProxyBytes / 1e6:0.00} MB (original {new FileInfo(source.Path).Length / 1e6:0.0} MB).");
    }

    [ProxyFact]
    public async Task Proxy_has_a_short_gop_without_b_frames_and_the_policy_sees_the_difference()
    {
        MediaSpec spec = ProxyTestMedia.H264Ntsc30;
        ProxyEntry entry = await fixture.ProxyOf(spec);
        string source = ProxyTestMedia.Get(spec).Path;

        StreamStructure original = StreamStructureProbe.Probe(source);
        StreamStructure proxy = StreamStructureProbe.Probe(entry.ProxyPath);
        IReadOnlyDictionary<char, int>? types = FfmpegTool.Default!.CountPictureTypes(entry.ProxyPath);

        Assert.Equal(spec.GopLength, original.MaxKeyframeInterval);
        Assert.False(original.IntervalIsLowerBound);
        Assert.Equal(ProxySettings.Default.GopLength, proxy.MaxKeyframeInterval);
        Assert.Equal((spec.FrameCount + 14) / 15, proxy.KeyframeCount);
        Assert.NotNull(proxy.Bitrate);
        Assert.NotNull(types);
        Assert.False(types.ContainsKey('B'), "El proxy no debe tener cuadros B.");
        ProxyPolicy policy = new(new ProxyThresholds { MaxKeyframeInterval = 30 });
        Assert.True(policy.Evaluate(ProxyCandidate.From(MediaProbe.Probe(source), original)).Reasons.HasFlag(ProxyReasons.LongGop));
        Assert.False(policy.Evaluate(ProxyCandidate.From(MediaProbe.Probe(entry.ProxyPath), proxy)).IsRecommended);
    }

    [ProxyFact]
    public async Task A_second_request_reuses_the_stored_proxy()
    {
        MediaSpec spec = ProxyTestMedia.H264Offset;
        ProxyEntry first = await fixture.ProxyOf(spec);
        var phases = new List<ProxyJobPhase>();

        ProxyEntry again = await fixture.Generator!.GenerateAsync(ProxyTestMedia.Get(spec).Path, progress: new InlineProgress<ProxyProgress>(p => phases.Add(p.Phase)));

        Assert.Equal(first.Metadata, again.Metadata);
        Assert.DoesNotContain(ProxyJobPhase.Encoding, phases);
        Assert.Equal(ProxyJobPhase.Completed, phases[^1]);
    }

    [ProxyFact]
    public async Task Hevc_10_bit_clip_on_two_layers_is_recommended_and_its_decode_speed_can_be_measured()
    {
        string source = ProxyTestMedia.Get(ProxyTestMedia.Hevc10Offset).Path;
        MediaInfo info = MediaProbe.Probe(source);

        double speed = DecodeSpeedProbe.Measure(source, frames: 60);
        ProxyRecommendation recommendation = new ProxyPolicy().Evaluate(ProxyCandidate.From(info, StreamStructureProbe.Probe(source), speed, layers: 2));

        Assert.True(speed > 0);
        Assert.True(recommendation.Reasons.HasFlag(ProxyReasons.HighBitDepthHevc));
        output.WriteLine($"HEVC 10 bits 1080p por software: {speed:0.0}× tiempo real. {string.Join(" ", recommendation.Explanations)}");
        await Task.CompletedTask;
    }

    private static MediaSpec SpecNamed(string name) => name switch
    {
        "h264-2997-bframes" => ProxyTestMedia.H264Ntsc30,
        "hevc10-desfase" => ProxyTestMedia.Hevc10Offset,
        "h264-desfase" => ProxyTestMedia.H264Offset,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };
}

/// <summary>Cancelación, cola y el resolvedor con un generador real; cada prueba con su propio almacén.</summary>
public sealed class ProxyJobTests(ITestOutputHelper output)
{
    [ProxyFact]
    public async Task Cancelling_during_encoding_kills_ffmpeg_and_leaves_no_files()
    {
        using var temp = new TempDirectory();
        var store = new ProxyStore(temp.Combine("proxies"));
        using var generator = new ProxyGenerator(store, ProxyTestEnvironment.Executable!, new ProxyGeneratorOptions
        {
            EncoderPreference = ["libopenh264"],
            HardwareDecoding = false,
        });
        using var cancellation = new CancellationTokenSource();
        long framesAtCancel = 0;
        var progress = new InlineProgress<ProxyProgress>(p =>
        {
            if (p.Phase == ProxyJobPhase.Encoding && p.FramesDone > 0 && !cancellation.IsCancellationRequested)
            {
                framesAtCancel = p.FramesDone;
                Assert.Single(Directory.EnumerateFiles(store.Directory), f => f.Contains(".tmp", StringComparison.Ordinal));
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => generator.GenerateAsync(ProxyTestMedia.Get(ProxyTestMedia.H264Ntsc30).Path, null, progress, cancellation.Token));

        Assert.InRange(framesAtCancel, 1, ProxyTestMedia.H264Ntsc30.FrameCount - 1);
        Assert.Empty(Directory.EnumerateFiles(store.Directory));
        output.WriteLine($"Cancelado tras {framesAtCancel} cuadros; la carpeta quedó vacía.");
    }

    [ProxyFact]
    public async Task Jobs_beyond_the_limit_wait_in_the_queue_and_can_be_cancelled_there()
    {
        using var temp = new TempDirectory();
        var store = new ProxyStore(temp.Combine("proxies"));
        using var generator = new ProxyGenerator(store, ProxyTestEnvironment.Executable!, new ProxyGeneratorOptions
        {
            MaxConcurrent = 1,
            EncoderPreference = ["libopenh264"],
            HardwareDecoding = false,
        });
        using var stopFirst = new CancellationTokenSource();
        using var stopSecond = new CancellationTokenSource();
        var encoding = new TaskCompletionSource();
        var secondPhases = new ConcurrentQueue<ProxyJobPhase>();

        Task<ProxyEntry> first = generator.GenerateAsync(ProxyTestMedia.Get(ProxyTestMedia.H264Ntsc30).Path, null,
            new InlineProgress<ProxyProgress>(p =>
            {
                if (p.Phase == ProxyJobPhase.Encoding)
                {
                    encoding.TrySetResult();
                }
            }), stopFirst.Token);
        await encoding.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Task<ProxyEntry> second = generator.GenerateAsync(ProxyTestMedia.Get(ProxyTestMedia.Small).Path, null,
            new InlineProgress<ProxyProgress>(p => secondPhases.Enqueue(p.Phase)), stopSecond.Token);
        await Task.Delay(300);
        stopSecond.Cancel();
        stopFirst.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal([ProxyJobPhase.Queued], secondPhases);
        Assert.Empty(Directory.EnumerateFiles(store.Directory));
    }

    [ProxyFact]
    public async Task Requests_with_the_same_fingerprint_share_one_job_and_one_cancellation_does_not_stop_the_other()
    {
        using var temp = new TempDirectory();
        string source = temp.Combine("toma compartida.mp4");
        File.Copy(ProxyTestMedia.Get(ProxyTestMedia.H264Ntsc30).Path, source);
        var store = new ProxyStore(temp.Combine("proxies"));
        using var generator = new ProxyGenerator(store, ProxyTestEnvironment.Executable!, new ProxyGeneratorOptions
        {
            EncoderPreference = ["libopenh264"],
            HardwareDecoding = false,
        });
        using var stopFirst = new CancellationTokenSource();
        var encoding = new TaskCompletionSource();
        var second = new ConcurrentQueue<ProxyProgress>();

        Task<ProxyEntry> firstTask = generator.GenerateAsync(source, null, new InlineProgress<ProxyProgress>(p =>
        {
            if (p.Phase == ProxyJobPhase.Encoding)
            {
                encoding.TrySetResult();
            }
        }), stopFirst.Token);
        await encoding.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Task<ProxyEntry> secondTask = generator.GenerateAsync(source.ToUpperInvariant(), null, new InlineProgress<ProxyProgress>(second.Enqueue));
        await WaitUntil(() => second.Any(p => p.Phase == ProxyJobPhase.Encoding));
        stopFirst.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstTask);
        ProxyEntry entry = await secondTask.WaitAsync(TimeSpan.FromSeconds(120));
        Assert.DoesNotContain(second, p => p.Phase == ProxyJobPhase.Analyzing);
        Assert.All(second, p => Assert.Equal(source.ToUpperInvariant(), p.SourcePath));
        Assert.Equal(entry.Metadata, store.TryGet(SourceFingerprint.Compute(source), ProxySettings.Default)!.Metadata);
        Assert.Equal(2, Directory.EnumerateFiles(store.Directory).Count());
    }

    [ProxyFact]
    public async Task Resolver_generates_once_reports_progress_and_switches_preview_to_the_proxy()
    {
        using var temp = new TempDirectory();
        string source = temp.Combine("medios del proyecto\\toma corta ñ.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.Copy(ProxyTestMedia.Get(ProxyTestMedia.Small).Path, source);
        var store = new ProxyStore(temp.Combine("proxies"));
        using var generator = new ProxyGenerator(store, ProxyTestEnvironment.Executable!);
        using var resolver = new MediaSourceResolver(store, generator);
        var states = new ConcurrentQueue<ProxyState>();
        resolver.StatusChanged += (_, e) => states.Enqueue(e.Status.State);

        Assert.False(resolver.Resolve(source, MediaPurpose.Preview).IsProxy);
        Task<ProxyEntry> request = resolver.RequestProxyAsync(source);
        Assert.Same(request, resolver.RequestProxyAsync(source));
        ProxyEntry entry = await request;

        MediaSourceResolution preview = resolver.Resolve(source, MediaPurpose.Preview);
        MediaSourceResolution export = resolver.Resolve(source, MediaPurpose.Export);
        Assert.True(preview.IsProxy);
        Assert.Equal(entry.ProxyPath, preview.VideoPath);
        Assert.Equal(source, preview.AudioPath);
        Assert.Equal(source, export.VideoPath);
        Assert.Equal(ProxyState.Queued, states.First(s => s != ProxyState.Unknown));
        Assert.Contains(ProxyState.Generating, states);
        Assert.Equal(ProxyState.Ready, states.Last());

        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(1));
        Assert.False(resolver.Resolve(source, MediaPurpose.Preview).IsProxy);
        Assert.Equal(1, store.Cleanup().Stale);
    }

    [ProxyFact]
    public async Task Resolver_generates_automatically_when_recommended_and_can_cancel()
    {
        using var temp = new TempDirectory();
        string source = temp.Combine("toma.mp4");
        File.Copy(ProxyTestMedia.Get(ProxyTestMedia.H264Ntsc30).Path, source);
        var store = new ProxyStore(temp.Combine("proxies"));
        using var generator = new ProxyGenerator(store, ProxyTestEnvironment.Executable!, new ProxyGeneratorOptions
        {
            EncoderPreference = ["libopenh264"],
            HardwareDecoding = false,
        });
        using var resolver = new MediaSourceResolver(store, generator) { GenerateWhenRecommended = true };
        var generating = new TaskCompletionSource();
        resolver.StatusChanged += (_, e) =>
        {
            if (e.Status.State == ProxyState.Generating && e.Status.Progress > 0)
            {
                generating.TrySetResult();
            }
        };

        ProxyRecommendation recommendation = resolver.Assess(MediaProbe.Probe(source) with { Path = source }, decodeSpeed: 0.5, layers: 2);
        await generating.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(recommendation.Reasons.HasFlag(ProxyReasons.SlowDecode));
        Assert.True(resolver.Cancel(source));

        await WaitUntil(() => resolver.GetStatus(source).State == ProxyState.Recommended);
        Assert.Equal("Generación cancelada.", resolver.GetStatus(source).Message);
        await WaitUntil(() => !Directory.EnumerateFiles(store.Directory).Any());
    }

    [NvencFact]
    public async Task Nvenc_generation_speed_for_1080p() => await MeasureSpeed(ProxyTestMedia.H264Ntsc30, verifyFrames: false);

    [NvencFact(LongRunning = true)]
    public async Task Nvenc_generation_speed_for_4k_hevc_10_bit() => await MeasureSpeed(ProxyTestMedia.Uhd, verifyFrames: true);

    private async Task MeasureSpeed(MediaSpec spec, bool verifyFrames)
    {
        GeneratedMedia media = ProxyTestMedia.Get(spec);
        output.WriteLine($"Original {spec.Name}: {spec.Width}×{spec.Height}, {spec.FrameCount} cuadros, {new FileInfo(media.Path).Length / 1e6:0.0} MB ({media.VideoEncoder}).");
        foreach (int height in (int[])[540, 720])
        {
            foreach (bool hardware in (bool[])[true, false])
            {
                using var temp = new TempDirectory();
                var store = new ProxyStore(temp.Path);
                using var generator = new ProxyGenerator(store, ProxyTestEnvironment.Executable!, new ProxyGeneratorOptions
                {
                    EncoderPreference = ["h264_nvenc"],
                    HardwareDecoding = hardware,
                });
                ProxyEntry entry = await generator.GenerateAsync(media.Path, new ProxySettings { TargetHeight = height });

                Assert.Equal("h264_nvenc", entry.Metadata.Encoder);
                Assert.Equal(spec.FrameCount, entry.Metadata.FrameCount);
                Assert.True(entry.Metadata.Speed > 1, $"La generación va a {entry.Metadata.Speed:0.00}× el tiempo real.");
                output.WriteLine($"  {height}p, decodificación {(hardware ? "D3D11VA" : "software")}: {entry.Metadata.Speed:0.0}× tiempo real " +
                    $"({entry.Metadata.GenerationSeconds:0.00} s), {entry.Metadata.Width}×{entry.Metadata.Height}, {entry.Metadata.ProxyBytes / 1e6:0.00} MB.");
                if (verifyFrames && height == 540 && hardware)
                {
                    using VideoDecoder decoder = VideoDecoder.Open(entry.ProxyPath);
                    var random = new Random(4242);
                    for (int k = 0; k < 300; k++)
                    {
                        int n = random.Next(spec.FrameCount);
                        using DecodedFrame? frame = decoder.GetFrame(n);
                        LumaImage luma = FrameReadback.ReadLuma(frame!);
                        Assert.Equal(n, FrameCode.Read(luma.Pixels, luma.Width, luma.Height, luma.Width));
                    }
                }
            }
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(50);
        }

        Assert.True(condition());
    }
}
