using System.Diagnostics;
using System.Globalization;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Export.Still;
using Rodaje.Export.Tests.Running;
using Rodaje.Media.Export;
using Rodaje.TestMedia;

namespace Rodaje.Export.Tests.Still;

/// <summary>
/// La codificación del fotograma con el <c>ffmpeg.exe</c> LGPL, sin GPU: un cuadro sintético entra por el sink y se
/// decodifica de vuelta. Corre en la CI de Windows (tiene <c>ffmpeg.exe</c> pero no GPU).
/// </summary>
public sealed class StillEncoderTests : IDisposable
{
    private const int Width = 97;
    private const int Height = 41;

    public void Dispose() => ExportEndToEndTests.DeleteFolders();

    [StillEncoderFact]
    public void Png_es_exacto_y_deja_solo_el_archivo()
    {
        string folder = ExportEndToEndTests.Folder();
        string path = Path.Combine(folder, "cuadro ñ.png");
        byte[] rgb = Pattern(3);
        long bytes = Encode(new StillSettings(), path, rgb, 3);

        Assert.Equal(new FileInfo(path).Length, bytes);
        Assert.Equal([path], Directory.GetFileSystemEntries(folder));
        Assert.Equal(rgb, Decode(path, "rgb24"));
        Assert.Contains("codec_name=png", Probe(path), StringComparison.Ordinal);
    }

    [StillEncoderFact]
    public void Png_con_alfa_guarda_el_alfa_recto()
    {
        string path = Path.Combine(ExportEndToEndTests.Folder(), "alfa.png");
        byte[] rgba = Pattern(4);
        Encode(new StillSettings { Alpha = true }, path, rgba, 4);

        Assert.Equal(rgba, Decode(path, "rgba"));
        Assert.Contains("pix_fmt=rgba", Probe(path), StringComparison.Ordinal);
    }

    [StillEncoderFact]
    public void Jpeg_es_bt601_de_rango_completo_y_cerca_del_original()
    {
        string path = Path.Combine(ExportEndToEndTests.Folder(), "cuadro.jpg");
        byte[] rgb = Pattern(3);
        Encode(new StillSettings { Format = StillFormat.Jpeg, JpegQuality = 100 }, path, rgb, 3);

        string probe = Probe(path);
        Assert.Contains("codec_name=mjpeg", probe, StringComparison.Ordinal);
        Assert.Contains("color_range=pc", probe, StringComparison.Ordinal);
        Assert.Contains("444p", probe, StringComparison.Ordinal);
        byte[] decoded = Decode(path, "rgb24");
        double psnr = Psnr(rgb, decoded);
        Assert.True(psnr > 38, string.Create(CultureInfo.InvariantCulture, $"PSNR {psnr:0.0} dB."));
        // Un gris medio sigue siendo el mismo gris: rango completo, no limitado (que lo aclararía o oscurecería).
        int gray = (((Height / 2) * Width) + 2) * 3;
        Assert.InRange(decoded[gray], 125, 131);
    }

    [StillEncoderFact]
    public void Menos_calidad_jpeg_da_un_archivo_mas_chico()
    {
        string folder = ExportEndToEndTests.Folder();
        byte[] rgb = Pattern(3);
        long best = Encode(new StillSettings { Format = StillFormat.Jpeg, JpegQuality = 100 }, Path.Combine(folder, "alta.jpg"), rgb, 3);
        long worst = Encode(new StillSettings { Format = StillFormat.Jpeg, JpegQuality = 1 }, Path.Combine(folder, "baja.jpg"), rgb, 3);
        Assert.True(worst < best, $"Calidad 1: {worst} B; calidad 100: {best} B.");
    }

    [StillEncoderFact]
    public void Reemplaza_un_archivo_que_ya_existe()
    {
        string path = Path.Combine(ExportEndToEndTests.Folder(), "existe.png");
        File.WriteAllText(path, "viejo");
        Encode(new StillSettings(), path, Pattern(3), 3);
        Assert.Equal(Pattern(3), Decode(path, "rgb24"));
    }

    [StillEncoderFact]
    public void No_pisa_un_medio_del_proyecto()
    {
        string path = Path.Combine(ExportEndToEndTests.Folder(), "medio.png");
        File.WriteAllText(path, "medio");
        StillPlan plan = Plan(new StillSettings(), path);
        ExportException error = Assert.Throws<ExportException>(() => new StillEncoder(Skip.ProxyExecutable!, plan, path, [path]));
        Assert.Equal(ExportErrorCode.InvalidSettings, error.Code);
        Assert.Equal("medio", File.ReadAllText(path));
    }

    private static StillPlan Plan(StillSettings settings, string path)
    {
        var project = new Project
        {
            Id = "p", Name = "p",
            Sequences = [new Sequence { Id = "sec", Name = "s", Width = Width, Height = Height, FrameRate = Rational.Fps30 }],
        };
        return StillPlan.Create(new StillRequest(project, "sec", Time.Zero, path, settings));
    }

    private static long Encode(StillSettings settings, string path, byte[] pixels, int channels)
    {
        StillPlan plan = Plan(settings, path);
        using var encoder = new StillEncoder(Skip.ProxyExecutable!, plan, path);
        // Con relleno al final de cada fila, como la memoria de una textura Staging.
        int pitch = (Width * channels) + 64;
        byte[] padded = new byte[pitch * Height];
        for (int y = 0; y < Height; y++)
        {
            pixels.AsSpan(y * Width * channels, Width * channels).CopyTo(padded.AsSpan(y * pitch));
        }

        var frame = new ExportVideoFrame(0, plan.Output, padded, pitch);
        return encoder.Write(in frame, CancellationToken.None);
    }

    /// <summary>Degradados suaves (lo que JPEG guarda bien), un gris medio en la fila central y alfa variable.</summary>
    private static byte[] Pattern(int channels)
    {
        byte[] data = new byte[Width * Height * channels];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int i = ((y * Width) + x) * channels;
                bool gray = y == Height / 2 || Math.Abs(y - (Height / 2)) <= 4;
                data[i] = gray ? (byte)128 : (byte)(x * 255 / (Width - 1));
                data[i + 1] = gray ? (byte)128 : (byte)(y * 255 / (Height - 1));
                data[i + 2] = gray ? (byte)128 : (byte)(255 - (x * 255 / (Width - 1)));
                if (channels == 4)
                {
                    data[i + 3] = (byte)((x + y) * 255 / (Width + Height - 2));
                }
            }
        }

        return data;
    }

    private static double Psnr(byte[] a, byte[] b)
    {
        Assert.Equal(a.Length, b.Length);
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double d = a[i] - b[i];
            sum += d * d;
        }

        double mse = sum / a.Length;
        return mse == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    internal static byte[] Decode(string path, string pixelFormat) =>
        Run(Skip.ProxyExecutable!.Path, ["-hide_banner", "-loglevel", "error", "-i", path, "-f", "rawvideo", "-pix_fmt", pixelFormat, "pipe:1"]);

    internal static string Probe(string path)
    {
        string ffprobe = Path.Combine(Path.GetDirectoryName(Skip.ProxyExecutable!.Path)!, "ffprobe.exe");
        return System.Text.Encoding.UTF8.GetString(Run(ffprobe, ["-v", "error", "-show_streams", path]));
    }

    private static byte[] Run(string exe, string[] arguments)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in arguments)
        {
            info.ArgumentList.Add(a);
        }

        using Process process = Process.Start(info)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        using var memory = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(memory);
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error.Result);
        return memory.ToArray();
    }
}

/// <summary>Necesita el <c>ffmpeg.exe</c> LGPL verificado (y <c>ffprobe.exe</c>); no la GPU.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class StillEncoderFactAttribute : FactAttribute
{
    public StillEncoderFactAttribute() => Skip = Rodaje.TestMedia.Skip.Proxies;
}
