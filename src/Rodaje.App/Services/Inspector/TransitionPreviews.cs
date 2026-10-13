using System.Globalization;
using System.Text;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Timeline;

namespace Rodaje.App.Services.Inspector;

/// <summary>
/// Las dos imágenes de muestra de las miniaturas de transiciones: un atardecer cálido con montañas (el clip que sale) y un
/// mar frío con franjas (el que entra). Distintas en color y en forma, para que se vea qué hace cada transición.
/// </summary>
public static class TransitionSampleImages
{
    /// <summary>RGBA de 8 bits, opacos, de <paramref name="width"/> × <paramref name="height"/>.</summary>
    public static (byte[] Outgoing, byte[] Incoming) Create(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 2);
        var a = new byte[width * height * 4];
        var b = new byte[width * height * 4];
        double sunX = 0.68 * width, sunY = 0.42 * height, sunR = 0.14 * height;
        for (int y = 0; y < height; y++)
        {
            double v = (y + 0.5) / height;
            for (int x = 0; x < width; x++)
            {
                double u = (x + 0.5) / width;
                int i = ((y * width) + x) * 4;

                // Atardecer: cielo en degradado, sol y montañas.
                (double r, double g, double bl) = Mix((255, 176, 92), (122, 42, 86), v);
                double sun = Math.Clamp(sunR + 1 - Math.Sqrt(((x - sunX) * (x - sunX)) + ((y - sunY) * (y - sunY))), 0, 1);
                (r, g, bl) = Mix((r, g, bl), (255, 236, 170), sun);
                double ridge = 0.70 + (0.07 * Math.Sin(u * 7.1)) + (0.035 * Math.Sin((u * 19.3) + 1.7));
                double ground = Math.Clamp(((v - ridge) * height) + 0.5, 0, 1);
                (r, g, bl) = Mix((r, g, bl), (42, 24, 52), ground);
                Write(a, i, r, g, bl);

                // Mar: degradado frío, franjas de luz en diagonal y un círculo claro.
                (double r2, double g2, double b2) = Mix((48, 156, 214), (16, 46, 92), v);
                double band = 0.5 + (0.5 * Math.Sin(((u * 2.6) + (v * 1.4)) * Math.PI * 3));
                (r2, g2, b2) = Mix((r2, g2, b2), (150, 228, 240), 0.35 * band * band * band);
                double ring = Math.Abs(Math.Sqrt(((x - (0.3 * width)) * (x - (0.3 * width))) + ((y - (0.45 * height)) * (y - (0.45 * height)))) - (0.2 * height));
                (r2, g2, b2) = Mix((r2, g2, b2), (240, 250, 255), Math.Clamp((0.025 * height) + 0.5 - ring, 0, 1));
                Write(b, i, r2, g2, b2);
            }
        }

        return (a, b);
    }

    private static (double, double, double) Mix((double R, double G, double B) a, (double R, double G, double B) b, double t) =>
        (a.R + ((b.R - a.R) * t), a.G + ((b.G - a.G) * t), a.B + ((b.B - a.B) * t));

    private static void Write(byte[] pixels, int i, double r, double g, double b)
    {
        pixels[i] = (byte)Math.Clamp(Math.Round(r), 0, 255);
        pixels[i + 1] = (byte)Math.Clamp(Math.Round(g), 0, 255);
        pixels[i + 2] = (byte)Math.Clamp(Math.Round(b), 0, 255);
        pixels[i + 3] = 255;
    }
}

/// <summary>Cuadros ya compuestos de una miniatura animada, en BGRA para un <c>WriteableBitmap</c>.</summary>
public sealed record TransitionAnimation(int Width, int Height, IReadOnlyList<byte[]> Bgra, double Seconds);

/// <summary>
/// Pide a la GPU las miniaturas animadas de las transiciones y las guarda por su firma (tipo, parámetros, opciones,
/// curva y duración): las mismas no se vuelven a componer.
/// </summary>
public sealed class TransitionPreviews(Func<int, int, byte[], byte[], IReadOnlyList<TransitionPreviewFrame>, Task<IReadOnlyList<byte[]>>> render,
    Func<IEffectContent?> content)
{
    /// <summary>Cuadros de cada miniatura, del primero (el clip que sale) al último (el que entra).</summary>
    public const int FrameCount = 24;

    private const int MaxCached = 96;
    private readonly Dictionary<string, Task<TransitionAnimation?>> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly Dictionary<(int, int), (byte[], byte[])> _samples = [];

    /// <summary>La animación de <paramref name="transition"/> a ese tamaño; nula si la GPU no la pudo componer.</summary>
    public Task<TransitionAnimation?> GetAsync(TransitionInstance transition, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(transition);
        string key = Signature(transition, width, height);
        if (_cache.TryGetValue(key, out Task<TransitionAnimation?>? cached))
        {
            return cached;
        }

        Task<TransitionAnimation?> task = RenderAsync(transition.Clone(), width, height);
        _cache[key] = task;
        _order.Enqueue(key);
        while (_order.Count > MaxCached)
        {
            _cache.Remove(_order.Dequeue());
        }

        return task;
    }

    /// <summary>Olvida lo compuesto (cambiaron los paquetes instalados).</summary>
    public void Clear()
    {
        _cache.Clear();
        _order.Clear();
    }

    /// <summary>La firma que identifica una miniatura: todo lo que cambia lo que se ve.</summary>
    public static string Signature(TransitionInstance transition, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(transition);
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{transition.Type}|{width}x{height}|{transition.Duration.Ticks}|{transition.Curve}|{transition.Ease}");
        foreach ((string name, double value) in transition.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            text.Append(CultureInfo.InvariantCulture, $"|{name}={value:R}");
        }

        foreach ((string name, string value) in transition.Options.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            text.Append(CultureInfo.InvariantCulture, $"|{name}:{value}");
        }

        return text.ToString();
    }

    private async Task<TransitionAnimation?> RenderAsync(TransitionInstance transition, int width, int height)
    {
        if (!_samples.TryGetValue((width, height), out (byte[] A, byte[] B) samples))
        {
            samples = await Task.Run(() => TransitionSampleImages.Create(width, height)).ConfigureAwait(true);
            _samples[(width, height)] = samples;
        }

        // Las miniaturas de la galería duran lo de fábrica; la del inspector, lo de la transición (para que se note la curva).
        IReadOnlyList<TransitionPreviewFrame> frames = TransitionPreview.Frames(transition, FrameCount, width, height,
            new Rational(30, 1), content());
        try
        {
            IReadOnlyList<byte[]> rgba = await render(width, height, samples.A, samples.B, frames).WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(true);
            return new TransitionAnimation(width, height, [.. rgba.Select(ToBgra)], Math.Clamp(transition.Duration.ToSeconds(), 0.5, 2.5));
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static byte[] ToBgra(byte[] rgba)
    {
        var bgra = new byte[rgba.Length];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            bgra[i] = rgba[i + 2];
            bgra[i + 1] = rgba[i + 1];
            bgra[i + 2] = rgba[i];
            bgra[i + 3] = 255;
        }

        return bgra;
    }
}
