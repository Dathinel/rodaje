using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Rodaje.Core.Timing;
using Rodaje.Media.Gpu;
using Rodaje.Media.Proxies;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.Playback.Composition;
using Rodaje.Render;
using Rodaje.TestMedia;
using TerraFX.Interop.DirectX;
using Xunit.Abstractions;
using static TerraFX.Interop.DirectX.D3D11;

namespace Rodaje.Export.Tests.Prototype;

/// <summary>
/// Prototipo del paso 0 de la fase 6 (no es producto): fija las cifras de la tubería a <c>ffmpeg.exe</c> y del canal sin
/// codificar en este equipo. Solo informa; los criterios los mide el agente F. Cifras anotadas en <c>docs/fase-6.md</c>.
/// </summary>
public sealed class ExportThroughputPrototype(ITestOutputHelper output)
{
    [PrototypeTheory]
    [InlineData(1920, 1080, "null")]
    [InlineData(3840, 2160, "null")]
    [InlineData(1920, 1080, "h264_nvenc")]
    [InlineData(3840, 2160, "h264_nvenc")]
    [InlineData(3840, 2160, "hevc_nvenc")]
    [InlineData(1920, 1080, "libsvtav1")]
    public void Pipe_throughput_to_ffmpeg(int width, int height, string encoder)
    {
        FfmpegExecutable ffmpeg = Skip.ProxyExecutable!;
        if (encoder != "null" && ffmpeg.ProbeEncoder(encoder) is { } missing)
        {
            output.WriteLine(missing);
            return;
        }

        int frameBytes = width * height * 3 / 2;
        int frames = encoder == "libsvtav1" ? 150 : width > 1920 ? 300 : 600;
        byte[][] pattern = Frames(width, height, 30);
        var info = new ProcessStartInfo(ffmpeg.Path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        string[] head = ["-hide_banner", "-nostdin", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "nv12",
            "-s", $"{width}x{height}", "-r", "30", "-i", "pipe:0"];
        string[] tail = encoder switch
        {
            "null" => ["-f", "null", "-"],
            "libsvtav1" => ["-c:v", "libsvtav1", "-preset", "8", "-crf", "35", "-pix_fmt", "yuv420p", "-f", "null", "-"],
            _ => ["-c:v", encoder, "-preset", "p4", "-tune", "hq", "-rc", "vbr", "-cq", "23", "-f", "null", "-"],
        };
        foreach (string a in head.Concat(tail))
        {
            info.ArgumentList.Add(a);
        }

        using var process = Process.Start(info)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Stream pipe = process.StandardInput.BaseStream;
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < frames; i++)
        {
            pipe.Write(pattern[i % pattern.Length], 0, frameBytes);
        }

        TimeSpan written = clock.Elapsed;
        pipe.Close();
        Assert.True(process.WaitForExit(120_000), "ffmpeg.exe no terminó.");
        clock.Stop();
        _ = stdout.GetAwaiter().GetResult();
        Assert.True(process.ExitCode == 0, error.GetAwaiter().GetResult());

        double seconds = clock.Elapsed.TotalSeconds;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"TUBERIA {width}x{height} NV12 -> {encoder}: {frames} cuadros en {seconds:0.00} s = {frames / seconds:0.0} fps, " +
            $"{(double)frameBytes * frames / seconds / 1e6:0} MB/s (escritura sola {written.TotalSeconds:0.00} s)"));
    }

    [HardwareFact(VideoCodec.H264, VideoCodec.Hevc, LongRunning = true)]
    public void Uncoded_channel_decodes_composes_and_downloads_three_1080p_layers()
    {
        TestMediaGenerator generator = TestMediaGenerator.CreateDefault()!;
        string[] paths = [.. new[] { TestMediaPresets.H264Ntsc30BFrames, TestMediaPresets.Hevc25Mkv, TestMediaPresets.H264Ntsc24Aac }
            .Select(s => generator.GetOrCreate(s).Path)];
        const int Frames = 480;
        using GpuDevice gpu = GpuDevice.Create();
        var decoders = paths.Select(p => VideoDecoder.Open(p, new VideoDecoderOptions { Device = gpu })).ToArray();
        try
        {
            foreach (CompositeFormat format in new[] { CompositeFormat.Rgba16Float, CompositeFormat.Rgba8Unorm })
            {
                Run(gpu, decoders, Frames, format);
            }
        }
        finally
        {
            foreach (VideoDecoder decoder in decoders)
            {
                decoder.Dispose();
            }
        }
    }

    private unsafe void Run(GpuDevice gpu, VideoDecoder[] decoders, int frames, CompositeFormat format)
    {
        using var compositor = new Compositor(gpu, 1920, 1080, format);
        var queues = decoders.Select(_ => new BlockingCollection<DecodedFrame>(boundedCapacity: 3)).ToArray();
        using var cancel = new CancellationTokenSource();
        Task[] producers = [.. decoders.Select((d, k) => Task.Factory.StartNew(() =>
        {
            for (int i = 0; i < frames && !cancel.IsCancellationRequested; i++)
            {
                DecodedFrame frame = d.GetFrame(i + (k * 7), cancel.Token) ?? throw new InvalidOperationException($"Sin cuadro {i}.");
                queues[k].Add(frame, cancel.Token);
            }

            queues[k].CompleteAdding();
        }, TaskCreationOptions.LongRunning))];

        // Sustituto de ExportFrameConverter: un píxel de la composición a Staging (obliga a la GPU a terminarla) y una textura
        // R8 del tamaño de un NV12 1080p (1920×1620) copiada a Staging y mapeada cada cuadro (el ancho de banda de la bajada).
        var device = (ID3D11Device*)gpu.NativeDevice;
        var context = (ID3D11DeviceContext*)gpu.NativeContext;
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = 1920, Height = 1620, MipLevels = 1, ArraySize = 1, Format = DXGI_FORMAT.DXGI_FORMAT_R8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC(1, 0), Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET,
        };
        ID3D11Texture2D* nv12;
        Assert.True(device->CreateTexture2D(&desc, null, &nv12).SUCCEEDED);
        desc.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
        desc.BindFlags = 0;
        desc.CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
        ID3D11Texture2D* staging;
        Assert.True(device->CreateTexture2D(&desc, null, &staging).SUCCEEDED);
        D3D11_TEXTURE2D_DESC compositeDesc;
        ((ID3D11Texture2D*)compositor.Texture)->GetDesc(&compositeDesc);
        compositeDesc.Width = 1;
        compositeDesc.Height = 1;
        compositeDesc.MipLevels = 1;
        compositeDesc.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
        compositeDesc.BindFlags = 0;
        compositeDesc.MiscFlags = 0;
        compositeDesc.CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
        ID3D11Texture2D* probe;
        Assert.True(device->CreateTexture2D(&compositeDesc, null, &probe).SUCCEEDED);
        var sink = new byte[1920 * 1620];
        double waitMs = 0, composeMs = 0, downloadMs = 0;
        var total = Stopwatch.StartNew();
        try
        {
            for (int i = 0; i < frames; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                var layers = new LayerFrame[3];
                for (int k = 0; k < 3; k++)
                {
                    LayerTransform transform = k == 0 ? LayerTransform.Identity : new LayerTransform(k * 300, k * 150, 0.5, 0, 0.8);
                    layers[k] = new LayerFrame(k, $"c{k}", queues[k].Take(), transform);
                }

                long t1 = Stopwatch.GetTimestamp();
                using (var composition = new CompositionFrame(Time.Zero, 1920, 1080, layers))
                {
                    Assert.Equal(3, compositor.Render(composition).LayersDrawn);
                }

                long t2 = Stopwatch.GetTimestamp();
                gpu.LockContext();
                try
                {
                    var box = new D3D11_BOX(0, 0, 0, 1, 1, 1);
                    context->CopySubresourceRegion((ID3D11Resource*)probe, 0, 0, 0, 0, (ID3D11Resource*)compositor.Texture, 0, &box);
                    D3D11_MAPPED_SUBRESOURCE mapped;
                    Assert.True(context->Map((ID3D11Resource*)probe, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped).SUCCEEDED);
                    context->Unmap((ID3D11Resource*)probe, 0);
                    context->CopyResource((ID3D11Resource*)staging, (ID3D11Resource*)nv12);
                    Assert.True(context->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped).SUCCEEDED);
                    for (int y = 0; y < 1620; y++)
                    {
                        new ReadOnlySpan<byte>((byte*)mapped.pData + ((nuint)y * mapped.RowPitch), 1920).CopyTo(sink.AsSpan(y * 1920));
                    }

                    context->Unmap((ID3D11Resource*)staging, 0);
                }
                finally
                {
                    gpu.UnlockContext();
                }

                long t3 = Stopwatch.GetTimestamp();
                waitMs += Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds;
                composeMs += Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds;
                downloadMs += Stopwatch.GetElapsedTime(t2, t3).TotalMilliseconds;
            }
        }
        finally
        {
            cancel.Cancel();
            foreach (BlockingCollection<DecodedFrame> queue in queues)
            {
                while (queue.TryTake(out DecodedFrame? left))
                {
                    left.Dispose();
                }
            }

            Task.WaitAll(producers.Select(p => p.ContinueWith(_ => { }, TaskScheduler.Default)).ToArray());
            probe->Release();
            staging->Release();
            nv12->Release();
        }

        double seconds = total.Elapsed.TotalSeconds;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"SIN CODIFICAR 3 capas 1080p ({format}): {frames} cuadros en {seconds:0.00} s = {frames / seconds:0.0} fps; " +
            $"por cuadro: espera de decodificación {waitMs / frames:0.00} ms, composición {composeMs / frames:0.00} ms, " +
            $"copia y bajada NV12 {downloadMs / frames:0.00} ms"));
    }

    private static byte[][] Frames(int width, int height, int count)
    {
        int bytes = width * height * 3 / 2;
        var frames = new byte[count][];
        uint state = 2463534242;
        for (int f = 0; f < count; f++)
        {
            var frame = new byte[bytes];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    state ^= state << 13;
                    state ^= state >> 17;
                    state ^= state << 5;
                    frame[(y * width) + x] = (byte)(((x + y + (f * 8)) & 0xFF) ^ (state & 0x0F));
                }
            }

            frame.AsSpan(width * height).Fill(128);
            frames[f] = frame;
        }

        return frames;
    }
}

/// <summary>Prueba larga que necesita ffmpeg.exe (el de los proxies).</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PrototypeTheoryAttribute : TheoryAttribute
{
    public PrototypeTheoryAttribute() => Skip = Rodaje.TestMedia.Skip.First(Rodaje.TestMedia.Skip.LongRun, Rodaje.TestMedia.Skip.Proxies);
}
