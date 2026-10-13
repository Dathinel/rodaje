using System.Numerics;
using Rodaje.Core.Effects;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Playback.Composition;
using Rodaje.Render.Tests.Support;
using Rodaje.TestMedia;

namespace Rodaje.Render.Tests;

/// <summary>
/// Anclaje de esquinas: la capa va a un cuadrilátero con perspectiva. Contra la referencia de la CPU (la homografía
/// inversa de cada píxel): la cobertura del borde, el área y el centro del cuadrilátero, y que la imagen siga la
/// perspectiva por dentro (no una interpolación afín por triángulo). En la GPU de verdad y en WARP.
/// </summary>
public sealed class CornerPinTests
{
    private const int Width = 256;
    private const int Height = 128;
    private static readonly VideoColor Bt709 = VideoColor.Bt709Limited;

    /// <summary>Una pantalla vista de lado: el borde derecho más chico que el izquierdo.</summary>
    private static readonly LayerCornerPin Screen = new(30, 12, -40, 30, -46, -24, 22, -6);

    public static TheoryData<bool> Devices => new() { false, true };

    [GpuTheory]
    [MemberData(nameof(Devices))]
    public void Coverage_area_and_center_match_the_quadrilateral(bool warp)
    {
        using GpuDevice gpu = warp ? GpuDevice.CreateWarp() : GpuDevice.Create();
        using Compositor compositor = TransitionTests.Ready(gpu);
        byte[] opaque = Render(compositor, Layers.From(gpu, Solid(), Bt709));
        int red = opaque[0];
        byte[] actual = Render(compositor, Layers.From(gpu, Solid(), Bt709, LayerTransform.Identity with { Pin = Screen }));

        LayerPlacement placement = LayerLayout.Place(Width, Height, 64, 32, LayerTransform.Identity with { Pin = Screen })!.Value;
        Assert.NotNull(placement.Projective);
        int worst = 0;
        double area = 0, cx = 0, cy = 0;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                double reference = ReferenceCoverage(placement, x + 0.5f, y + 0.5f);
                double measured = actual[((y * Width) + x) * 4] / (double)red;
                worst = Math.Max(worst, Math.Abs(actual[((y * Width) + x) * 4] - (int)Math.Round(red * reference)));
                area += measured;
                cx += measured * (x + 0.5);
                cy += measured * (y + 0.5);
            }
        }

        Vector2[] quad = [placement.TopLeft, placement.TopRight, placement.BottomRight, placement.BottomLeft];
        (double trueArea, Vector2 trueCenter) = Polygon(quad);
        double centerError = Vector2.Distance(new Vector2((float)(cx / area), (float)(cy / area)), trueCenter);
        Assert.True(worst <= 3, $"Cobertura: diferencia máxima {worst} códigos ({gpu.AdapterName}).");
        Assert.InRange(Math.Abs(area - trueArea) / trueArea, 0, 0.004);
        Assert.InRange(centerError, 0, 0.05);
    }

    [GpuTheory]
    [MemberData(nameof(Devices))]
    public void The_image_follows_the_perspective_inside(bool warp)
    {
        using GpuDevice gpu = warp ? GpuDevice.CreateWarp() : GpuDevice.Create();
        using Compositor compositor = TransitionTests.Ready(gpu);
        byte[] flat = Render(compositor, Layers.From(gpu, Bands(), Bt709));
        byte[] pinned = Render(compositor, Layers.From(gpu, Bands(), Bt709, LayerTransform.Identity with { Pin = Screen }));
        LayerPlacement placement = LayerLayout.Place(Width, Height, Width, Height, LayerTransform.Identity with { Pin = Screen })!.Value;

        // Cada límite entre bandas (x = 32·k) cae en la recta que da la homografía: a 2 px de cada lado, el color de su banda.
        int worst = 0;
        double worstShift = 0;
        for (int k = 1; k < 8; k++)
        {
            foreach (float v in new[] { 20f, 44f, 84f, 108f })
            {
                Vector2 on = placement.Map(32 * k, v);
                Vector2 along = Vector2.Normalize(placement.Map(32 * k, v + 1) - on);
                var across = new Vector2(along.Y, -along.X);
                if (Vector2.Dot(placement.Map((32 * k) + 1, v) - on, across) < 0)
                {
                    across = -across;
                }

                worst = Math.Max(worst, Distance(pinned, Pixel(on - (2.5f * across)), flat, ((Height / 2 > v ? 10 : Height - 10) * Width) + (32 * k) - 16));
                worst = Math.Max(worst, Distance(pinned, Pixel(on + (2.5f * across)), flat, ((Height / 2 > v ? 10 : Height - 10) * Width) + (32 * k) + 16));

                // Lo que daría una interpolación afín por triángulo, para saber que la prueba distingue.
                worstShift = Math.Max(worstShift, Vector2.Distance(on, Affine(placement, 32 * k, v)));
            }
        }

        Assert.True(worstShift > 4, $"La perspectiva es muy suave para probar algo ({worstShift:0.0} px).");
        Assert.True(worst <= 3, $"Diferencia máxima {worst} códigos junto a los límites de banda ({gpu.AdapterName}).");
    }

    [GpuFact]
    public void Preview_and_export_compose_the_same_pinned_layer()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using Compositor compositor = TransitionTests.Ready(gpu);
        var transform = LayerTransform.Identity with { Pin = Screen, Scale = 0.8, RotationDegrees = 7 };
        byte[] preview;
        using (CompositionFrame frame = Layers.Frame(Width, Height, Layers.From(gpu, Bands(), Bt709, new LayerLook(Blend: BlendMode.Screen), transform)))
        {
            Assert.Equal(0, compositor.Render(frame).EffectsSkipped);
            preview = compositor.ReadbackRgba();
        }

        using (CompositionFrame frame = Layers.Frame(Width, Height, Layers.From(gpu, Bands(), Bt709, new LayerLook(Blend: BlendMode.Screen), transform)))
        {
            compositor.RenderStrict(frame);
        }

        Assert.Equal(preview, compositor.ReadbackRgba());
    }

    [GpuFact]
    public void A_crossed_quadrilateral_is_ignored()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using Compositor compositor = TransitionTests.Ready(gpu);
        byte[] plain = Render(compositor, Layers.From(gpu, Bands(), Bt709));
        var crossed = new LayerCornerPin(Width, 0, -Width, 0, 0, 0, 0, 0);
        Assert.Null(LayerLayout.Place(Width, Height, Width, Height, LayerTransform.Identity with { Pin = crossed })!.Value.Projective);
        Assert.Equal(plain, Render(compositor, Layers.From(gpu, Bands(), Bt709, LayerTransform.Identity with { Pin = crossed })));
    }

    // La regla del shader: distancia al lado en píxeles de destino (con la escala local de la perspectiva) más ½.
    private static double ReferenceCoverage(LayerPlacement placement, float x, float y)
    {
        if (placement.Unmap(new Vector2(x, y)) is not { } q)
        {
            return 0;
        }

        Vector2 dx = placement.Unmap(new Vector2(x + 0.5f, y))!.Value - placement.Unmap(new Vector2(x - 0.5f, y))!.Value;
        Vector2 dy = placement.Unmap(new Vector2(x, y + 0.5f))!.Value - placement.Unmap(new Vector2(x, y - 0.5f))!.Value;
        double sx = 1 / Math.Max(1e-6, Math.Sqrt((dx.X * dx.X) + (dy.X * dy.X)));
        double sy = 1 / Math.Max(1e-6, Math.Sqrt((dx.Y * dx.Y) + (dy.Y * dy.Y)));
        double inside = Math.Min(Math.Min(q.X, placement.LocalWidth - q.X) * sx, Math.Min(q.Y, placement.LocalHeight - q.Y) * sy);
        return Math.Clamp(inside + 0.5, 0, 1);
    }

    private static (double Area, Vector2 Center) Polygon(Vector2[] p)
    {
        double a = 0, cx = 0, cy = 0;
        for (int i = 0; i < p.Length; i++)
        {
            Vector2 u = p[i], v = p[(i + 1) % p.Length];
            double cross = ((double)u.X * v.Y) - ((double)v.X * u.Y);
            a += cross;
            cx += (u.X + v.X) * cross;
            cy += (u.Y + v.Y) * cross;
        }

        a /= 2;
        return (Math.Abs(a), new Vector2((float)(cx / (6 * a)), (float)(cy / (6 * a))));
    }

    // El mismo punto con la interpolación afín del triángulo que lo contiene (lo que haría la GPU sin la w por esquina).
    private static Vector2 Affine(LayerPlacement placement, float x, float y)
    {
        float u = x / placement.LocalWidth, v = y / placement.LocalHeight;
        return u + v <= 1
            ? placement.TopLeft + (u * (placement.TopRight - placement.TopLeft)) + (v * (placement.BottomLeft - placement.TopLeft))
            : placement.BottomRight + ((1 - u) * (placement.BottomLeft - placement.BottomRight)) + ((1 - v) * (placement.TopRight - placement.BottomRight));
    }

    private static int Pixel(Vector2 p) => ((int)p.Y * Width) + (int)p.X;

    private static int Distance(byte[] a, int i, byte[] b, int j) =>
        Math.Max(Math.Abs(a[i * 4] - b[j * 4]), Math.Max(Math.Abs(a[(i * 4) + 1] - b[(j * 4) + 1]), Math.Abs(a[(i * 4) + 2] - b[(j * 4) + 2])));

    private static YuvImage Solid() => YuvImage.Solid(64, 32, false, ReferenceColor.Encode(220, 120, 40, Bt709, false));

    private static YuvImage Bands()
    {
        var image = new YuvImage(Width, Height, false);
        for (int band = 0; band < 8; band++)
        {
            image.Fill(band * 32, 0, 32, Height / 2, ReferenceColor.Encode(30 + (band * 28), 200 - (band * 20), 60, Bt709, false));
            image.Fill(band * 32, Height / 2, 32, Height / 2, ReferenceColor.Encode(220 - (band * 20), 40 + (band * 25), 160, Bt709, false));
        }

        return image;
    }

    private static byte[] Render(Compositor compositor, params LayerFrame[] layers)
    {
        using CompositionFrame frame = Layers.Frame(Width, Height, layers);
        Assert.Equal(0, compositor.Render(frame).EffectsSkipped);
        return compositor.ReadbackRgba();
    }
}
