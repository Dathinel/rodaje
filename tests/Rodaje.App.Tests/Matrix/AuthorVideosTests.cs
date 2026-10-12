using System.Diagnostics;
using System.Globalization;
using Rodaje.App.Services;
using Rodaje.App.Services.Media;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Audio;
using Rodaje.Media.Gpu;
using Rodaje.Media.Probe;
using Rodaje.Media.Video;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.App.Tests.Matrix;

/// <summary>
/// Los videos de teléfono del autor (<c>RODAJE_VIDEOS_AUTOR</c>, o <c>%USERPROFILE%\Videos\capcut</c>; solo se leen):
/// HEVC 10 bits vertical con AAC a 44,1 kHz. Sin número dibujado, el seek se compara con el cuadro que da ffmpeg.exe.
/// </summary>
[Collection(MatrixGroup.Name)]
[Trait("Categoria", "Matriz")]
public sealed class AuthorVideosTests(ITestOutputHelper output)
{
    private static string Folder => Environment.GetEnvironmentVariable("RODAJE_VIDEOS_AUTOR") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos", "capcut");

    public static TheoryData<string> Files()
    {
        var data = new TheoryData<string>();
        string[] files = Directory.Exists(Folder) ? Directory.GetFiles(Folder, "*.mp4").Order(StringComparer.OrdinalIgnoreCase).ToArray() : [];
        // El primero y el último (el corto cuyo audio dura más que el video): los demás son iguales.
        foreach (string file in files.Length > 2 ? [files[0], files[^1]] : files)
        {
            data.Add(Path.GetFileName(file));
        }

        if (files.Length == 0)
        {
            data.Add("(no hay videos del autor)");
        }

        return data;
    }

    [MatrixTheory]
    [MemberData(nameof(Files))]
    public async Task An_author_video_goes_end_to_end(string name)
    {
        string path = Path.Combine(Folder, name);
        if (!File.Exists(path))
        {
            output.WriteLine($"Sin videos en {Folder}: se omite.");
            return;
        }

        MediaProbeResult probe = new FfmpegMediaProber().Probe(path);
        Assert.True(probe.IsUsable, probe.Problem);
        MediaInfo info = probe.Info!;
        var c = new MatrixCase(name, "MP4", "video del autor", "audio del autor", ".mp4")
        {
            Seconds = info.Duration.ToSeconds(), Rate = info.Video!.FrameRate, FrameCodes = false,
            Video = ["autor"], Audio = ["autor"], Width = info.Video.DisplayWidth, Height = info.Video.DisplayHeight,
        };
        var row = new MatrixRow(c);
        var media = new MediaRef { Id = "m", Path = path };
        info.ApplyTo(media);
        row.Set(MatrixStep.Import, FormatMatrixTests.Describe(info));
        FormatMatrixTests.Thumbnails(c, path, info, row);
        Seek(path, info, row);
        Waveform(path, row);
        await FormatMatrixTests.ExportAsync(c, media, info, row);

        string table = Path.Combine(MatrixTable.Folder, "autor.md");
        Directory.CreateDirectory(MatrixTable.Folder);
        File.AppendAllText(table, row.ToMarkdown() + Environment.NewLine);
        output.WriteLine(row.ToMarkdown());
        Assert.True(row.Failures.Count == 0, string.Join(Environment.NewLine, row.Failures));
    }

    /// <summary>90 cuadros en orden y 6 seeks por la GPU contra los mismos cuadros decodificados por ffmpeg.exe, en gris.</summary>
    private static void Seek(string path, MediaInfo info, MatrixRow row)
    {
        using GpuDevice gpu = GpuDevice.Create();
        using VideoDecoder decoder = VideoDecoder.Open(path, new VideoDecoderOptions { Device = gpu });
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 90; i++)
        {
            using Rodaje.Playback.DecodedFrame? frame = decoder.GetFrame(i);
            if (frame?.Index != i || frame.Pts != Time.FromFrames(i, decoder.FrameRate))
            {
                row.Fail(MatrixStep.Play, Invariant($"el cuadro {i} llegó como {frame?.Index}"));
                return;
            }
        }

        row.Set(MatrixStep.Play, Invariant($"90/90 {(decoder.UsesHardwareDecoding ? "GPU" : "CPU")}; {90_000 / watch.Elapsed.TotalMilliseconds:0} fps"));
        long last = info.Video!.FrameCount - 1;
        long[] requests = [last, 0, last / 2, 37, last - 45, 211 % (last + 1)];
        int good = 0;
        double worst = 0;
        foreach (long n in requests)
        {
            using Rodaje.Playback.DecodedFrame frame = decoder.GetFrame(n)!;
            LumaImage luma = FrameReadback.ReadLuma(frame);
            byte[] reference = ReferenceLuma(path, n, luma.Width, luma.Height);
            double diff = luma.Pixels.Zip(reference, (a, b) => Math.Abs(a - b)).Average();
            worst = Math.Max(worst, diff);
            good += diff < 2 ? 1 : 0;
        }

        if (good != requests.Length)
        {
            row.Fail(MatrixStep.Seek, Invariant($"{requests.Length - good} seeks distintos del cuadro de ffmpeg.exe (diferencia {worst:0.0})"));
        }
        else
        {
            row.Set(MatrixStep.Seek, Invariant($"{good}/{requests.Length}, diferencia media ≤ {worst:0.00}"));
        }
    }

    private static void Waveform(string path, MatrixRow row)
    {
        using AudioDecoder decoder = AudioDecoder.Open(path);
        var peaks = new WaveformPeaks(AudioDecoder.OutputSampleRate);
        var buffer = new float[8192 * 2];
        int read;
        while ((read = decoder.Read(buffer)) > 0)
        {
            peaks.Append(buffer.AsSpan(0, read * 2), 2);
        }

        peaks.Complete();
        (_, float[] max) = peaks.Query(0, peaks.Samples, 100);
        double seconds = peaks.Samples / 48_000.0;
        if (max.Max() < 0.01 || decoder.DecodeErrors > 0)
        {
            row.Fail(MatrixStep.Waveform, Invariant($"pico {max.Max():0.000}, {decoder.DecodeErrors} errores"));
        }
        else
        {
            row.Set(MatrixStep.Waveform, Invariant($"{seconds:0.00} s, pico {max.Max():0.00}"));
        }
    }

    private static byte[] ReferenceLuma(string path, long frame, int width, int height)
    {
        byte[]? luma = null;
        FfmpegTool.Default!.Run(
            ["-hide_banner", "-loglevel", "error", "-nostdin", "-noautorotate", "-i", path, "-map", "0:v:0", "-vf", Invariant($"select=eq(n\\,{frame})"),
                "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "yuv420p", "-"],
            readOutput: s =>
            {
                using var memory = new MemoryStream();
                s.CopyTo(memory);
                // Solo el plano Y, en su rango limitado como el de la GPU («gray» lo estiraría a rango completo).
                luma = memory.ToArray()[..(width * height)];
            });
        Assert.True(luma is not null, $"ffmpeg no dio el cuadro {frame}.");
        return luma!;
    }

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
}
