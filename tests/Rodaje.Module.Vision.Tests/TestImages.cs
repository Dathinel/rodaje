using System.Diagnostics;
using System.Globalization;

namespace Rodaje.Module.Vision.Tests;

/// <summary>Una imagen BGRA con alfa (del recorte) y su tamaño.</summary>
internal sealed record Cutout(byte[] Bgra, int Width, int Height);

/// <summary>
/// Datos de prueba fuera del repo, en <c>.entorno</c>: los modelos (<c>build\preparar-modelos.ps1</c>) y dos recortes de
/// persona de dominio público con alfa real (<c>preparar-modelos.ps1 -Corpus</c>, en <c>corpus\vision</c>). Las imágenes
/// de prueba se componen sobre fondos sintéticos: la verdad es la alfa del recorte.
/// </summary>
internal static class TestImages
{
    public static string? Environment { get; } = FindEnvironment();

    public static ModelFiles Models { get; } = new(Environment is null ? null : [Path.Combine(Environment, "modelos")]);

    public static bool HasModels =>
        Environment is not null && new[] { ModelFiles.Modnet, ModelFiles.BiRefNetLite, ModelFiles.YuNet, ModelFiles.RtDetr }
            .All(m => File.Exists(Path.Combine(Environment, "modelos", m.FileName)));

    public static bool LongTests => System.Environment.GetEnvironmentVariable("RODAJE_PRUEBAS_LARGAS") == "1";

    public static string Missing =>
        "Omitida: faltan los modelos de visión o los recortes de prueba (build\\preparar-modelos.ps1 -Corpus).";

    /// <summary>Retrato de medio cuerpo, en color (Lewis M. Blanton, foto oficial; dominio público).</summary>
    public static Cutout? Portrait => Load("retrato.png");

    /// <summary>Cuerpo entero, en blanco y negro (Alfred Hitchcock, 1960; dominio público).</summary>
    public static Cutout? FullBody => Load("cuerpo.png");

    public static bool Ready => HasModels && Portrait is not null && FullBody is not null;

    private static readonly Dictionary<string, Cutout?> Cache = [];

    private static Cutout? Load(string name)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out Cutout? known))
            {
                return known;
            }

            Cutout? result = null;
            if (Environment is { } env && File.Exists(Path.Combine(env, "corpus", "vision", name)) && File.Exists(Path.Combine(env, "ffmpeg", "bin", "ffmpeg.exe")))
            {
                string png = Path.Combine(env, "corpus", "vision", name);
                using var probe = Process.Start(new ProcessStartInfo(Path.Combine(env, "ffmpeg", "bin", "ffprobe.exe"),
                    ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=p=0", png])
                { RedirectStandardOutput = true })!;
                string[] size = probe.StandardOutput.ReadToEnd().Trim().Split(',');
                probe.WaitForExit();
                int w = int.Parse(size[0], CultureInfo.InvariantCulture), h = int.Parse(size[1], CultureInfo.InvariantCulture);
                using var ffmpeg = Process.Start(new ProcessStartInfo(Path.Combine(env, "ffmpeg", "bin", "ffmpeg.exe"),
                    ["-v", "error", "-i", png, "-f", "rawvideo", "-pix_fmt", "bgra", "-"])
                { RedirectStandardOutput = true })!;
                using var memory = new MemoryStream();
                ffmpeg.StandardOutput.BaseStream.CopyTo(memory);
                ffmpeg.WaitForExit();
                if (memory.Length == (long)w * h * 4)
                {
                    result = new Cutout(memory.ToArray(), w, h);
                }
            }

            Cache[name] = result;
            return result;
        }
    }

    private static string? FindEnvironment()
    {
        if (System.Environment.GetEnvironmentVariable("RODAJE_ENTORNO") is { Length: > 0 } env && Directory.Exists(env))
        {
            return env;
        }

        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, ".entorno");
            if (Directory.Exists(Path.Combine(candidate, "modelos")))
            {
                return candidate;
            }
        }

        return null;
    }
}

/// <summary>Fondos sintéticos y composición del recorte con su alfa como verdad.</summary>
internal static class Scenes
{
    public enum Background { Gradient, Checker, Noise, Solid, Stripes, Blobs }

    public static byte[] MakeBackground(Background kind, int width, int height, int seed)
    {
        var random = new Random(seed);
        var pixels = new byte[width * height * 4];
        (byte r, byte g, byte b) solid = ((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
        var blobs = Enumerable.Range(0, 12).Select(_ => (x: random.NextDouble() * width, y: random.NextDouble() * height,
            r: (0.05 + (random.NextDouble() * 0.2)) * width, c: (random.Next(256), random.Next(256), random.Next(256)))).ToArray();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                (int r, int g, int b) = kind switch
                {
                    Background.Gradient => (x * 255 / width, y * 255 / height, 255 - (x * 255 / width)),
                    Background.Checker => ((x / 40) + (y / 40)) % 2 == 0 ? (230, 230, 210) : (60, 80, 120),
                    Background.Noise => (random.Next(256), random.Next(256), random.Next(256)),
                    Background.Solid => (solid.r, solid.g, solid.b),
                    Background.Stripes => (x / 25) % 2 == 0 ? (200, 40, 40) : (40, 160, 70),
                    _ => Blob(blobs, x, y),
                };
                int i = ((y * width) + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = ((byte)b, (byte)g, (byte)r, 255);
            }
        }

        return pixels;
    }

    private static (int, int, int) Blob((double x, double y, double r, (int, int, int) c)[] blobs, int x, int y)
    {
        foreach (var blob in blobs)
        {
            if (Math.Pow(x - blob.x, 2) + Math.Pow(y - blob.y, 2) < blob.r * blob.r)
            {
                return blob.c;
            }
        }

        return (120, 120, 120);
    }

    /// <summary>
    /// Pega el recorte escalado (vecino más cercano en alfa, bilineal sería igual de válido) con su esquina en
    /// (<paramref name="left"/>, <paramref name="top"/>) y altura <paramref name="cutoutHeight"/>. Devuelve el cuadro y la
    /// alfa verdadera (0..255) del tamaño del cuadro.
    /// </summary>
    public static (byte[] Frame, byte[] Truth) Compose(byte[] background, int width, int height, Cutout cutout, int left, int top, int cutoutHeight)
    {
        byte[] frame = (byte[])background.Clone();
        var truth = new byte[width * height];
        int cutoutWidth = cutoutHeight * cutout.Width / cutout.Height;
        for (int y = Math.Max(0, top); y < Math.Min(height, top + cutoutHeight); y++)
        {
            int sy = (y - top) * cutout.Height / cutoutHeight;
            for (int x = Math.Max(0, left); x < Math.Min(width, left + cutoutWidth); x++)
            {
                int sx = (x - left) * cutout.Width / cutoutWidth;
                int s = ((sy * cutout.Width) + sx) * 4, d = ((y * width) + x) * 4;
                int a = cutout.Bgra[s + 3];
                for (int c = 0; c < 3; c++)
                {
                    frame[d + c] = (byte)(((cutout.Bgra[s + c] * a) + (frame[d + c] * (255 - a)) + 127) / 255);
                }

                truth[(y * width) + x] = (byte)a;
            }
        }

        return (frame, truth);
    }

    /// <summary>IoU de las máscaras binarizadas en 128.</summary>
    public static double Iou(ReadOnlySpan<byte> predicted, ReadOnlySpan<byte> truth)
    {
        long inter = 0, union = 0;
        for (int i = 0; i < truth.Length; i++)
        {
            bool p = predicted[i] >= 128, t = truth[i] >= 128;
            inter += p && t ? 1 : 0;
            union += p || t ? 1 : 0;
        }

        return union == 0 ? 1 : (double)inter / union;
    }

    public static void AddNoise(byte[] frame, int amplitude, int seed)
    {
        var random = new Random(seed);
        for (int i = 0; i < frame.Length; i++)
        {
            if (i % 4 != 3)
            {
                frame[i] = (byte)Math.Clamp(frame[i] + random.Next(-amplitude, amplitude + 1), 0, 255);
            }
        }
    }
}
