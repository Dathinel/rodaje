using Rodaje.App.Services.Modules;
using Rodaje.Core.Timing;
using Rodaje.Media.Video;
using Rodaje.Modules.Protocol;
using Rodaje.TestMedia;

namespace Rodaje.App.Tests.Modules;

/// <summary>El editor decodifica y el módulo recibe audio y cuadros por memoria compartida.</summary>
public sealed class ModuleMediaTests : IDisposable
{
    private static readonly MediaSpec Spec = new("modulos-320-30-aac", 320, 180, Rational.Fps30, TimeSpan.FromSeconds(4)) { Audio = AudioCodec.Aac };
    private readonly EchoModules _modules = new EchoModules().Add("eco");

    public void Dispose() => _modules.Dispose();

    [MediaFact]
    public async Task Audio_a_16_kHz_mono_y_un_cuadro_BGRA_llegan_al_modulo_y_vuelven()
    {
        string path = TestMediaGenerator.CreateDefault()!.GetOrCreate(Spec).Path;
        await using ModuleManager manager = _modules.Manager();
        (AudioLayout audio, float peak, VideoLayout video, bool same) = await manager.RunAsync("eco", "Analizar", async call =>
        {
            SharedBufferRef input = await ModuleMedia.WriteAudioAsync(call, path, Time.FromSeconds(1), Time.FromSeconds(3));
            float[] samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(call.Map(input)).ToArray();

            SharedMemoryRegion frames = call.CreateRegion(2 * ModuleMedia.FrameBytes(256));
            using ThumbnailDecoder decoder = ThumbnailDecoder.Open(path);
            SharedBufferRef frame = ModuleMedia.WriteFrame(decoder, frames, 0, Time.FromSeconds(2), 256)!;
            SharedBufferRef copy = frames.Describe(ModuleMedia.FrameBytes(256), frame.Length, frame.Video);
            ModuleResult result = await call.InvokeAsync("copiar", null, [frame, copy]);
            bool same = frames.GetSpan(frame).SequenceEqual(frames.GetSpan(Assert.Single(result.Buffers)));
            return (input.Audio!, samples.Max(Math.Abs), frame.Video!, same);
        });

        Assert.Equal(16_000, audio.SampleRate);
        Assert.Equal(1, audio.Channels);
        Assert.Equal(32_000, audio.Frames);
        Assert.True(peak > 0.01f, $"pico {peak}");
        Assert.Equal(256, video.Width);
        Assert.Equal(144, video.Height);
        Assert.Equal(PixelFormats.Bgra8, video.PixelFormat);
        Assert.True(same);
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class MediaFactAttribute : FactAttribute
{
    public MediaFactAttribute() => Skip = Rodaje.TestMedia.Skip.First(Rodaje.TestMedia.Skip.FfmpegLibraries, Rodaje.TestMedia.Skip.Encoders([VideoCodec.H264], AudioCodec.Aac));
}
