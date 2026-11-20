using System.Security.Cryptography;
using Rodaje.Core.Timing;
using Rodaje.Media.Audio;
using Rodaje.Media.Probe;
using Rodaje.Media.Proxies;
using Rodaje.Media.Tests.Support;
using Rodaje.Media.Video;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Media.Tests.Proxies;

/// <summary>El proxy de la sala (B14): el mismo generador, pero con AAC estéreo y almacén por la huella entera del archivo.</summary>
public sealed class RoomProxyGenerationTests(ITestOutputHelper output)
{
    /// <summary>Clip corto con video y AAC estéreo, generado una vez por el caché de <see cref="ProxyTestMedia"/>.</summary>
    private static MediaSpec WithAudio { get; } = new("h264-360p-25-aac-sala", 640, 360, Rational.Fps25, TimeSpan.FromSeconds(4))
    {
        Codec = VideoCodec.H264,
        GopLength = 25,
        Audio = AudioCodec.Aac,
        Container = ContainerFormat.Mp4,
    };

    [ProxyFact]
    public async Task Room_proxy_plays_video_and_audio()
    {
        using var temp = new TempDirectory();
        var store = new ProxyStore(temp.Combine("proxies"));
        using var generator = new ProxyGenerator(store, ProxyTestEnvironment.Executable!, new ProxyGeneratorOptions
        {
            EncoderPreference = ["libopenh264"],
            HardwareDecoding = false,
        });
        GeneratedMedia source = ProxyTestMedia.Get(WithAudio);

        ProxyEntry entry = await generator.GenerateAsync(source.Path, ProxySettings.Room);
        MediaInfo info = MediaProbe.Probe(entry.ProxyPath);

        Assert.Equal("h264", info.Video!.Codec);
        Assert.Equal(640, info.Video.DisplayWidth);
        Assert.Equal(360, info.Video.DisplayHeight);
        Assert.True(info.HasAudio);
        Assert.Equal("aac", info.Audio!.Codec);
        Assert.Equal(2, info.Audio.Channels);
        Assert.Equal("h540-g15-a128-v1", entry.Metadata.SettingsTag);

        // El camino de siempre abre el archivo entero: el medio «solo proxy» del invitado se ve y se oye.
        using VideoDecoder video = VideoDecoder.Open(entry.ProxyPath);
        Assert.Equal(info.Video.FrameCount, video.FrameCount);
        using AudioDecoder audio = AudioDecoder.Open(entry.ProxyPath);
        output.WriteLine($"{entry.ProxyPath}: {info.Video.FrameCount} cuadros y {info.Audio.Duration.ToSeconds():0.0} s de AAC estéreo.");
    }

    [ProxyFact]
    public async Task Generation_keyed_by_the_full_sha256_names_one_file_per_content()
    {
        using var temp = new TempDirectory();
        var store = new ProxyStore(temp.Combine("proxies"));
        using var generator = new ProxyGenerator(store, ProxyTestEnvironment.Executable!, new ProxyGeneratorOptions
        {
            EncoderPreference = ["libopenh264"],
            HardwareDecoding = false,
        });
        string source = ProxyTestMedia.Get(WithAudio).Path;
        string sha;
        await using (FileStream stream = File.OpenRead(source))
        {
            sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
        }

        SourceFingerprint fingerprint = SourceFingerprint.Compute(source) with { Key = sha };
        ProxyEntry first = await generator.GenerateAsync(fingerprint, ProxySettings.Room);
        ProxyEntry second = await generator.GenerateAsync(fingerprint, ProxySettings.Room);

        Assert.Equal($"{sha}-h540-g15-a128-v1.mp4", Path.GetFileName(first.ProxyPath));
        Assert.Equal(first.Metadata, second.Metadata);
        Assert.Equal(2, Directory.EnumerateFiles(store.Directory).Count());
        // La limpieza del almacén de la vista previa no ve esta carpeta: el proxy de la sala es de la sala.
        Assert.Equal(0, new ProxyStore(temp.Combine("proxies de vista previa")).Cleanup().Stale);
    }

    [ProxyFact]
    public async Task Two_concurrent_requests_for_the_same_hash_share_one_job()
    {
        using var temp = new TempDirectory();
        var store = new ProxyStore(temp.Combine("proxies"));
        using var generator = new ProxyGenerator(store, ProxyTestEnvironment.Executable!, new ProxyGeneratorOptions
        {
            EncoderPreference = ["libopenh264"],
            HardwareDecoding = false,
        });
        string source = ProxyTestMedia.Get(WithAudio).Path;
        string sha;
        await using (FileStream stream = File.OpenRead(source))
        {
            sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
        }

        SourceFingerprint fingerprint = SourceFingerprint.Compute(source) with { Key = sha };
        Task<ProxyEntry> first = generator.GenerateAsync(fingerprint, ProxySettings.Room);
        Task<ProxyEntry> second = generator.GenerateAsync(fingerprint, ProxySettings.Room);

        Assert.Equal((await first).ProxyPath, (await second).ProxyPath);
        Assert.Single(Directory.EnumerateFiles(store.Directory, "*.mp4"));
    }

    [ProxyFact]
    public async Task Room_generation_reports_progress_and_cancels_cleanly()
    {
        using var temp = new TempDirectory();
        var store = new ProxyStore(temp.Combine("proxies"));
        using var generator = new ProxyGenerator(store, ProxyTestEnvironment.Executable!, new ProxyGeneratorOptions
        {
            EncoderPreference = ["libopenh264"],
            HardwareDecoding = false,
        });
        string source = ProxyTestMedia.Get(TestMediaPresets.H264Ntsc24Aac).Path;
        string sha;
        await using (FileStream stream = File.OpenRead(source))
        {
            sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
        }

        using var cancellation = new CancellationTokenSource();
        var fractions = new List<double>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            generator.GenerateAsync(SourceFingerprint.Compute(source) with { Key = sha }, ProxySettings.Room,
                new InlineProgress<ProxyProgress>(p =>
                {
                    fractions.Add(p.Fraction);
                    if (p.Phase == ProxyJobPhase.Encoding)
                    {
                        cancellation.Cancel();
                    }
                }), cancellation.Token));

        Assert.Empty(Directory.EnumerateFiles(store.Directory));
    }
}
