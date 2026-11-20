using System.Diagnostics;
using System.Globalization;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using Rodaje.Engine.Offline;
using Rodaje.Export.Encoders;
using Rodaje.Export.Running;
using Rodaje.Export.Tests.Acceptance;
using Rodaje.Media.Export;
using Rodaje.Media.Gpu;
using Rodaje.Media.Probe;
using Rodaje.Playback.Composition;
using Rodaje.Render;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Export.Tests.Running;

/// <summary>
/// Lo común de los recorridos de edición de punta a punta: medio sintético con código de cuadro y marcas por
/// segundo, proyecto guardado y reabierto desde disco, exportación y medidas sobre el archivo de salida.
/// </summary>
internal static class EditJourney
{
    /// <summary>640×360 a 30 con AAC: los 15 s dejan mangos de sobra para recortes, velocidades y transiciones.</summary>
    public static MediaSpec Media { get; } = new("recorrido-640x360-30-aac", 640, 360, Rational.Fps30, TimeSpan.FromSeconds(15))
    {
        Codec = VideoCodec.H264,
        GopLength = 30,
        BFrames = 2,
        Audio = AudioCodec.Aac,
        Container = ContainerFormat.Mp4,
    };

    /// <summary>Segundo contenido (320×180) para la pista de arriba del recorrido de varias pistas.</summary>
    public static MediaSpec MediaSmall { get; } = new("recorrido-320x180-30", 320, 180, Rational.Fps30, TimeSpan.FromSeconds(15))
    {
        Codec = VideoCodec.H264,
        GopLength = 30,
        BFrames = 2,
        Container = ContainerFormat.Mp4,
    };

    public static Time S(double seconds) => Time.FromSeconds(seconds);

    /// <summary>Secuencia «sec» a 30 fps con la pista de video «v0» (y la de audio «a0» si se pide).</summary>
    public static Project NewProject(int width, int height, bool audioTrack = false)
    {
        var sequence = new Sequence { Id = "sec", Name = "Recorrido", FrameRate = Rational.Fps30, Width = width, Height = height };
        sequence.VideoTracks.Add(new Track { Id = "v0", Kind = TrackKind.Video, Name = "Video 1" });
        if (audioTrack)
        {
            sequence.AudioTracks.Add(new Track { Id = "a0", Kind = TrackKind.Audio, Name = "Audio 1" });
        }

        return new Project { Id = "p", Name = "Recorrido", Sequences = [sequence] };
    }

    /// <summary>Genera el medio (una sola vez por especificación) y lo da de alta en el proyecto.</summary>
    public static MediaRef Import(Project project, MediaSpec spec, string id)
    {
        var reference = new MediaRef { Id = id, Path = TestMediaGenerator.CreateDefault()!.GetOrCreate(spec).Path };
        MediaProbe.Probe(reference.Path).ApplyTo(reference);
        project.Media.Add(reference);
        return reference;
    }

    /// <summary>Escribe el .rodaje, lo lee del disco y exige una lectura limpia (nada acotado al cargar).</summary>
    public static Project SaveAndReload(Project project, string folder)
    {
        string path = Path.Combine(folder, "proyecto.rodaje");
        ProjectSerializer.Save(project, path);
        Assert.True(File.Exists(path));
        Project loaded = ProjectSerializer.Load(path, out int clamped);
        Assert.Equal(0, clamped);
        return loaded;
    }

    /// <summary>H.264 NVENC a calidad constante; el audio solo en los recorridos que lo escuchan.</summary>
    public static ExportSettings Settings(bool audio) => new()
    {
        Video = new VideoExportSettings
        {
            Codec = ExportCodec.H264,
            EncoderId = "h264_nvenc",
            RateControl = RateControl.ConstantQuality(16),
            Profile = "high",
        },
        Audio = audio ? new AudioExportSettings { Codec = ExportCodec.Aac } : null,
    };

    /// <summary>Exporta el proyecto tal cual llega (ya reabierto si el recorrido lo pide) y exige que termine bien.</summary>
    public static async Task<(ExportJob Job, string Path)> ExportAsync(ITestOutputHelper output, Project project, string path, bool audio = false)
    {
        ExportJob job = await AcceptanceSupport.ExportAsync(output, project, path, Settings(audio));
        Assert.True(File.Exists(path));
        return (job, path);
    }

    /// <summary>Plano Y (8 bits) del cuadro <paramref name="frame"/> del archivo.</summary>
    public static byte[] Luma(string path, long frame, int width, int height) =>
        AcceptanceSupport.DecodeYuv420(path, frame, width, height)[..(width * height)];

    /// <summary>Número de cuadro dibujado por <see cref="SyntheticVideo"/> en el cuadro exportado, o null si no se lee.</summary>
    public static int? Code(string path, long frame, int width, int height) =>
        FrameCode.Read(Luma(path, frame, width, height), width, height, width);

    /// <summary>El código leído solo en la región (x, y, w, h) de un plano de luma completo.</summary>
    public static int? RegionCode(byte[] luma, int width, int x, int y, int w, int h) =>
        FrameCode.Read(luma.AsSpan((y * width) + x), w, h, width);

    /// <summary>Distancia media del croma al neutro 128 en un cuadro yuv420p: ~0 en blanco y negro.</summary>
    public static double ChromaDeviation(byte[] yuv420, int width, int height)
    {
        int luma = width * height;
        int chroma = luma / 4;
        long sum = 0;
        for (int i = 0; i < chroma; i++)
        {
            sum += Math.Abs(yuv420[luma + i] - 128) + Math.Abs(yuv420[luma + chroma + i] - 128);
        }

        return (double)sum / (2 * chroma);
    }

    /// <summary>Brillo medio de un plano de luma.</summary>
    public static double MeanLuma(ReadOnlySpan<byte> luma)
    {
        long sum = 0;
        foreach (byte v in luma)
        {
            sum += v;
        }

        return (double)sum / luma.Length;
    }

    /// <summary>Diferencia media absoluta píxel a píxel entre dos planos iguales.</summary>
    public static double MeanAbsDiff(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        Assert.Equal(a.Length, b.Length);
        long sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            sum += Math.Abs(a[i] - b[i]);
        }

        return (double)sum / a.Length;
    }

    /// <summary>Diferencia media absoluta solo en la región (x, y, w, h) de dos planos.</summary>
    public static double RegionDiff(byte[] a, byte[] b, int width, int x, int y, int w, int h)
    {
        long sum = 0;
        for (int row = y; row < y + h; row++)
        {
            int offset = (row * width) + x;
            for (int i = 0; i < w; i++)
            {
                sum += Math.Abs(a[offset + i] - b[offset + i]);
            }
        }

        return (double)sum / (w * h);
    }

    /// <summary>Nivel RMS del audio mono de 48 kHz entre dos instantes.</summary>
    public static double Rms(float[] mono, double fromSeconds, double toSeconds)
    {
        int from = Math.Max(0, (int)(fromSeconds * 48000));
        int to = Math.Min(mono.Length, (int)(toSeconds * 48000));
        double sum = 0;
        for (int i = from; i < to; i++)
        {
            sum += (double)mono[i] * mono[i];
        }

        return Math.Sqrt(sum / Math.Max(1, to - from));
    }

    /// <summary>El cuadro exportado en RGB de 8 bits con la matriz 709, como hacen las pruebas de exportación.</summary>
    public static byte[] DecodeRgb(string path, long frame, int width, int height)
    {
        using Process process = AcceptanceSupport.Start(AcceptanceSupport.Ffmpeg.Path, "-hide_banner", "-loglevel", "error", "-nostdin",
            "-i", path, "-vf", string.Create(CultureInfo.InvariantCulture,
                $"select=eq(n\\,{frame}),scale=in_color_matrix=bt709:in_range=tv:out_range=pc:flags=bicubic+accurate_rnd+full_chroma_int,format=rgb24"),
            "-frames:v", "1", "-f", "rawvideo", "-");
        Task<string> error = process.StandardError.ReadToEndAsync();
        using var memory = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(memory);
        process.WaitForExit();
        Assert.Equal(width * height * 3, (int)memory.Length);
        return memory.ToArray();
    }

    /// <summary>
    /// Píxeles magenta del relleno del título (rojo y azul altos, verde bajo) en todo el cuadro: ese color no sale
    /// en el medio sintético (su caja móvil es anaranjada), así que cada uno es un píxel del título.
    /// </summary>
    public static int TitlePixels(byte[] rgb)
    {
        int count = 0;
        for (int i = 0; i + 2 < rgb.Length; i += 3)
        {
            count += rgb[i] > 200 && rgb[i + 2] > 170 && rgb[i + 1] < 90 ? 1 : 0;
        }

        return count;
    }

    /// <summary>
    /// El peor PSNR de los cuadros dados entre el archivo exportado y la composición que hace el propio editor
    /// (OfflineTimeline, la misma que la vista previa): si la edición llega a la exportación, ambos coinciden.
    /// </summary>
    public static double PsnrAgainstPreview(Project project, string path, params long[] frames)
    {
        using GpuDevice gpu = GpuDevice.Create();
        using OfflineTimeline timeline = OfflineTimeline.Open(project, "sec", gpu);
        using var compositor = new Compositor(gpu, timeline.Width, timeline.Height);
        double worst = double.PositiveInfinity;
        foreach (long f in frames)
        {
            using (CompositionFrame composed = timeline.Render(f, CancellationToken.None))
            {
                compositor.RenderStrict(composed, l => new SourceGeometry(l.SampleAspectRatio, l.Rotation));
            }

            byte[] reference = compositor.ReadbackRgba();
            byte[] decoded = DecodeRgb(path, f, timeline.Width, timeline.Height);
            double sum = 0;
            for (int i = 0, j = 0; j < decoded.Length; i += 4, j += 3)
            {
                for (int c = 0; c < 3; c++)
                {
                    double d = reference[i + c] - decoded[j + c];
                    sum += d * d;
                }
            }

            double mse = sum / decoded.Length;
            worst = Math.Min(worst, mse <= 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse));
        }

        return worst;
    }

    /// <summary>
    /// Dos archivos, cuadro a cuadro en luma: el peor promedio de diferencia por cuadro y cuántos cuadros se
    /// compararon (el conteo tiene que coincidir).
    /// </summary>
    public static (long Frames, double WorstDiff) CompareLuma(string a, string b, int width, int height)
    {
        using Process pa = GrayProcess(a);
        using Process pb = GrayProcess(b);
        Task<string> ea = pa.StandardError.ReadToEndAsync();
        Task<string> eb = pb.StandardError.ReadToEndAsync();
        var fa = new byte[width * height];
        var fb = new byte[width * height];
        long frames = 0;
        double worst = 0;
        while (true)
        {
            int ra = pa.StandardOutput.BaseStream.ReadAtLeast(fa, fa.Length, throwOnEndOfStream: false);
            int rb = pb.StandardOutput.BaseStream.ReadAtLeast(fb, fb.Length, throwOnEndOfStream: false);
            if (ra != fa.Length || rb != fb.Length)
            {
                Assert.True(ra == rb, $"Los dos exportados no tienen los mismos cuadros ({ra} frente a {rb} en el cuadro {frames}).");
                break;
            }

            worst = Math.Max(worst, MeanAbsDiff(fa, fb));
            frames++;
        }

        pa.WaitForExit();
        pb.WaitForExit();
        Assert.True(pa.ExitCode == 0, ea.Result);
        Assert.True(pb.ExitCode == 0, eb.Result);
        return (frames, worst);
    }

    private static Process GrayProcess(string path) => AcceptanceSupport.Start(AcceptanceSupport.Ffmpeg.Path,
        "-hide_banner", "-loglevel", "error", "-nostdin", "-i", path, "-map", "0:v:0", "-fps_mode", "passthrough",
        "-f", "rawvideo", "-pix_fmt", "gray", "-");
}
