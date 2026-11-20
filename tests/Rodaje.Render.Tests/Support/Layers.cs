using System.Runtime.Versioning;
using Rodaje.Core.Timing;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.Playback.Composition;
using Rodaje.Render.Gpu;

namespace Rodaje.Render.Tests.Support;

/// <summary>Capas de prueba hechas con <see cref="YuvImage"/>.</summary>
internal static class Layers
{
    /// <summary>Una capa con la imagen completa como parte visible.</summary>
    public static LayerFrame From(TestGpu gpu, YuvImage image, VideoColor color, LayerTransform? transform = null, int track = 0) =>
        From(gpu, [image], 0, image.Width, image.Height, color, transform ?? LayerTransform.Identity, track);

    /// <summary>El camino D3D11, para las pruebas que ya tienen un <see cref="GpuDevice"/>.</summary>
    [SupportedOSPlatform("windows7.0")]
    public static LayerFrame From(GpuDevice gpu, YuvImage image, VideoColor color, LayerTransform? transform = null, int track = 0) =>
        From(gpu, [image], 0, image.Width, image.Height, color, transform ?? LayerTransform.Identity, track);

    /// <summary>
    /// Con D3D11 sube <paramref name="slices"/> como arreglo de texturas y hace una capa con la capa <paramref name="slice"/>.
    /// Con Vulkan solo vale una imagen (los arreglos son solo D3D11VA): sube sus planos como <see cref="GpuTextureSurface"/>.
    /// </summary>
    public static LayerFrame From(TestGpu gpu, IReadOnlyList<YuvImage> slices, int slice, int visibleWidth, int visibleHeight,
        VideoColor color, LayerTransform transform, int track = 0)
    {
        if (TestGpu.IsVulkan || !OperatingSystem.IsWindowsVersionAtLeast(7))
        {
            if (slices.Count != 1)
            {
                throw new InvalidOperationException("Los arreglos de texturas del decodificador solo existen en D3D11VA.");
            }

            return FromSurface(gpu.Gpu, YuvImage.CreateSurface(gpu.Gpu, slices[0], color, visibleWidth, visibleHeight),
                transform, track);
        }

        return From(gpu.Device, slices, slice, visibleWidth, visibleHeight, color, transform, track);
    }

    /// <summary>El camino D3D11 de arriba, para las pruebas que ya tienen un <see cref="GpuDevice"/>.</summary>
    [SupportedOSPlatform("windows7.0")]
    public static LayerFrame From(GpuDevice gpu, IReadOnlyList<YuvImage> slices, int slice, int visibleWidth, int visibleHeight,
        VideoColor color, LayerTransform transform, int track = 0)
    {
        nint texture = YuvImage.CreateTexture(gpu, slices);
        try
        {
            return FromTexture(gpu, texture, slice, visibleWidth, visibleHeight, color, transform, track);
        }
        finally
        {
            Release(texture);
        }
    }

    /// <summary>Capa sobre una textura D3D11 existente; toma su propia referencia.</summary>
    [SupportedOSPlatform("windows7.0")]
    public static LayerFrame FromTexture(GpuDevice gpu, nint texture, int slice, int visibleWidth, int visibleHeight,
        VideoColor color, LayerTransform transform, int track = 0, LayerLook? look = null, Time? pts = null, Time? duration = null)
    {
        D3D11TexturePayload payload = D3D11TexturePayload.FromTexture(gpu, texture, slice, visibleWidth, visibleHeight, color);
        var frame = new DecodedFrame(0, pts ?? Time.Zero, duration ?? Time.FromTicks(1), payload, 0);
        return new LayerFrame(track, $"clip-{track}", frame, transform)
        {
            Effects = look?.Effects ?? [],
            Blend = look?.Blend ?? BlendMode.Normal,
            Transition = look?.Transition,
        };
    }

    /// <summary>Capa sobre una superficie de un <see cref="IGpu"/>; toma la referencia (la libera el cuadro).</summary>
    public static LayerFrame FromSurface(IGpu gpu, GpuTextureSurface surface, LayerTransform transform, int track = 0,
        LayerLook? look = null, Time? pts = null, Time? duration = null)
    {
        var frame = new DecodedFrame(0, pts ?? Time.Zero, duration ?? Time.FromTicks(1), surface, 0);
        return new LayerFrame(track, $"clip-{track}", frame, transform)
        {
            Effects = look?.Effects ?? [],
            Blend = look?.Blend ?? BlendMode.Normal,
            Transition = look?.Transition,
        };
    }

    /// <summary>Una capa con efectos, mezcla o transición; <paramref name="pts"/> y <paramref name="duration"/> son los del cuadro del origen.</summary>
    public static LayerFrame From(TestGpu gpu, YuvImage image, VideoColor color, LayerLook look, LayerTransform? transform = null, int track = 0,
        Time? pts = null, Time? duration = null)
    {
        if (TestGpu.IsVulkan || !OperatingSystem.IsWindowsVersionAtLeast(7))
        {
            return FromSurface(gpu.Gpu, YuvImage.CreateSurface(gpu.Gpu, image, color, image.Width, image.Height),
                transform ?? LayerTransform.Identity, track, look, pts, duration);
        }

        nint texture = YuvImage.CreateTexture(gpu.Device, [image]);
        try
        {
            return FromTexture(gpu.Device, texture, 0, image.Width, image.Height, color, transform ?? LayerTransform.Identity,
                track, look, pts, duration);
        }
        finally
        {
            Release(texture);
        }
    }

    /// <summary>El camino D3D11 de la sobrecarga con <see cref="LayerLook"/>.</summary>
    [SupportedOSPlatform("windows7.0")]
    public static LayerFrame From(GpuDevice gpu, YuvImage image, VideoColor color, LayerLook look, LayerTransform? transform = null,
        int track = 0, Time? pts = null, Time? duration = null)
    {
        nint texture = YuvImage.CreateTexture(gpu, [image]);
        try
        {
            return FromTexture(gpu, texture, 0, image.Width, image.Height, color, transform ?? LayerTransform.Identity,
                track, look, pts, duration);
        }
        finally
        {
            Release(texture);
        }
    }

    [SupportedOSPlatform("windows7.0")]
    public static unsafe void Release(nint texture) => ((TerraFX.Interop.Windows.IUnknown*)texture)->Release();

    public static CompositionFrame Frame(int width, int height, params LayerFrame[] layers) =>
        new(Time.Zero, width, height, layers);
}

/// <summary>Efectos, mezcla y transición de una capa de prueba.</summary>
internal sealed record LayerLook(IReadOnlyList<LayerEffect>? Effects = null, BlendMode Blend = BlendMode.Normal, LayerTransition? Transition = null);

/// <summary>Píxeles RGBA de 8 bits leídos de la GPU.</summary>
internal sealed class RgbaImage(byte[] pixels, int width, int height)
{
    public byte[] Pixels => pixels;

    public int Width { get; } = width;

    public int Height { get; } = height;

    public (byte R, byte G, byte B, byte A) At(int x, int y)
    {
        int i = ((y * Width) + x) * 4;
        return (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]);
    }

    public void AssertColor(int x, int y, Rgb expected, double tolerance = 2)
    {
        (byte r, byte g, byte b, byte a) = At(x, y);
        bool ok = Math.Abs(r - expected.R) <= tolerance && Math.Abs(g - expected.G) <= tolerance
            && Math.Abs(b - expected.B) <= tolerance && a == 255;
        Assert.True(ok, $"Píxel ({x}, {y}) = ({r}, {g}, {b}, {a}); se esperaba ({expected.R:0.#}, {expected.G:0.#}, {expected.B:0.#}, 255) ±{tolerance}.");
    }

    public void AssertBlack(int x, int y) => AssertColor(x, y, new Rgb(0, 0, 0), 0);

    /// <summary>Luma de rango completo aproximada (canal verde), útil con croma neutra.</summary>
    public byte[] Green(int x, int y, int width, int height)
    {
        var plane = new byte[width * height];
        for (int row = 0; row < height; row++)
        {
            for (int column = 0; column < width; column++)
            {
                plane[(row * width) + column] = At(x + column, y + row).G;
            }
        }

        return plane;
    }
}
