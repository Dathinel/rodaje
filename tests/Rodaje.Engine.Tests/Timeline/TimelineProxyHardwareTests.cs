using System.Diagnostics;
using Rodaje.Audio;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Media.Gpu;
using Rodaje.Media.Probe;
using Rodaje.Media.Proxies;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.Playback.Composition;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Engine.Tests.Timeline;

/// <summary>
/// De punta a punta con FFmpeg, D3D11VA y NVENC: la vista previa de un 4K arranca en el original, el proxy se genera
/// solo en segundo plano y la reproducción pasa a él sin perder el cuadro exacto. Prueba larga.
/// </summary>
public sealed class TimelineProxyHardwareTests(ITestOutputHelper output) : IDisposable
{
    private static readonly MediaSpec Uhd = new("proxy-vista-hevc-2160p-30", 3840, 2160, Rational.Fps30, TimeSpan.FromSeconds(6))
    {
        Codec = VideoCodec.Hevc,
        GopLength = 30,
        BFrames = 2,
    };

    private readonly string _store = Path.Combine(Path.GetTempPath(), "rodaje-pruebas", "proxies-vista-" + Guid.NewGuid().ToString("N"));

    [GpuLoadFact]
    public void A_4k_clip_switches_to_its_generated_proxy_while_playing_and_every_frame_stays_exact()
    {
        using PreviewProxies proxies = PreviewProxies.Create(_store);
        Assert.True(proxies.CanGenerate, proxies.UnavailableReason);
        string path = TestMediaGenerator.CreateDefault()!.GetOrCreate(Uhd).Path;
        var media = new MediaRef { Id = "m", Path = path };
        MediaProbe.Probe(path).ApplyTo(media);
        var sequence = new Sequence { Id = "sec", Name = "Proxy", FrameRate = Rational.Fps30, Width = 3840, Height = 2160 };
        sequence.VideoTracks.Add(new Track { Id = "v0", Kind = TrackKind.Video, Name = "Video 1" });
        sequence.VideoTracks[0].Clips.Add(new Clip { Id = "c", MediaId = "m", Start = Time.Zero, SourceIn = Time.Zero, SourceOut = media.Duration });
        var project = new Project { Id = "p", Name = "Proxy", Sequences = [sequence], Media = [media] };

        using GpuDevice gpu = GpuDevice.Create();
        using var backend = new RealBackend();
        using var session = new TimelineSession(project, "sec", gpu,
            new TimelineSessionOptions { ProxyResolver = proxies.Resolver, UseProxies = ProxyUsage.Auto }, backend);
        Time refresh = Time.FromFrames(1, Rational.Fps60);
        long wrong = 0;
        long checkedFrames = 0;
        long fromProxy = 0;
        var watch = Stopwatch.StartNew();
        session.Play();
        while (watch.Elapsed < TimeSpan.FromSeconds(90) && fromProxy < 120)
        {
            if (!session.IsPlaying)
            {
                session.Play();
            }

            backend.Clock.Advance(refresh);
            using (CompositionFrame frame = session.Tick())
            {
                if (frame.Layers is [LayerFrame layer] && !session.IsSeekPending)
                {
                    LumaImage luma = FrameReadback.ReadLuma(layer.Frame);
                    int? code = FrameCode.Read(luma.Pixels, luma.Width, luma.Height, luma.Width);
                    wrong += code == layer.Frame.Index ? 0 : 1;
                    checkedFrames++;
                    if (layer.IsProxy)
                    {
                        fromProxy++;
                        Assert.Equal((3840, 2160), (layer.DisplayWidth, layer.DisplayHeight));
                        Assert.Equal((960, 540), (((D3D11TexturePayload)layer.Frame.Payload!).Width, ((D3D11TexturePayload)layer.Frame.Payload!).Height));
                    }
                }
            }

            Thread.Sleep(16);
        }

        TimelineStats stats = session.Stats;
        output.WriteLine($"{checkedFrames} cuadros comprobados por su código, {wrong} distintos; {fromProxy} del proxy tras {watch.Elapsed.TotalSeconds:0.0} s; " +
            $"tarde {stats.LateRefreshes}, perdidos {stats.FramesDropped}, decodificadores {stats.DecodersOpen} (proxy {stats.ProxyDecoders}), cerrados {stats.DecodersClosed}.");
        Assert.True(fromProxy >= 120, "La reproducción no pasó al proxy.");
        Assert.Equal(0, wrong);
        Assert.Equal(0, stats.DecodeErrors);
        Assert.Equal(1, stats.ProxyDecoders);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_store, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class RealBackend : IClipBackend, IDisposable
    {
        private readonly DisposalLog _log = new();
        private FakeOutput? _output;

        public ManualClock Clock => _output!.ManualClock;

        public IClipVideo OpenVideo(string path, VideoDecoderOptions options) => MediaClipBackend.Instance.OpenVideo(path, options);

        public IPcmSource OpenAudio(string path) => MediaClipBackend.Instance.OpenAudio(path);

        public IAudioOutput OpenOutput(IAudioSampleSource source, AudioOutputOptions options) => _output = new FakeOutput(source, hasDevice: true, _log);

        public IDecodeExecutor CreateExecutor() => MediaClipBackend.Instance.CreateExecutor();

        public void Dispose() => _output?.Dispose();
    }
}
