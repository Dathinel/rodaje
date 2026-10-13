using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Playback.Composition;
using Rodaje.Render.Tests.Support;
using Rodaje.TestMedia;
using Xunit.Abstractions;
using T = Rodaje.Core.Model.BuiltInTransitions;
using TP = Rodaje.Core.Model.BuiltInTransitions.CommonParameters;

namespace Rodaje.Render.Tests;

/// <summary>
/// Las transiciones de calidad contra su referencia en la CPU (<see cref="TransitionReference"/>), en la GPU y en WARP, con
/// las coordenadas exactas de <c>SV_Position</c>: cada lado es su imagen compuesta sola y cada píxel queda a ≤2/255.
/// </summary>
public sealed class TransitionMixTests(ITestOutputHelper output)
{
    private const int Width = 256;
    private const int Height = 128;
    private static readonly VideoColor Bt709 = VideoColor.Bt709Limited;
    private static readonly Dictionary<string, double> NoValues = [];
    private static readonly Dictionary<string, string> NoOptions = [];

    public static TheoryData<bool> Devices => new() { false, true };

    public static TheoryData<string, double, bool> Cases()
    {
        var data = new TheoryData<string, double, bool>();
        foreach (string type in new[] { T.Slide, T.ZoomBlur, T.Spin, T.LightWipe, T.Glitch, T.FilmBurn, T.Flash, T.Luma, T.Bounce })
        {
            foreach (double p in new[] { 0.25, 0.5, 0.8 })
            {
                data.Add(type, p, false);
            }

            data.Add(type, 0.5, true);
        }

        return data;
    }

    /// <summary>Las nuevas, como las de siempre: en 0 exactamente el clip que sale y en 1 el que entra.</summary>
    [GpuTheory]
    [InlineData(T.Slide)]
    [InlineData(T.ZoomBlur)]
    [InlineData(T.Spin)]
    [InlineData(T.LightWipe)]
    [InlineData(T.Glitch)]
    [InlineData(T.FilmBurn)]
    [InlineData(T.Flash)]
    [InlineData(T.Luma)]
    [InlineData(T.Bounce)]
    public void Progress_0_and_1_are_exactly_each_clip(string type)
    {
        using GpuDevice gpu = GpuDevice.Create();
        using Compositor compositor = TransitionTests.Ready(gpu);
        byte[] a = Render(compositor, Outgoing(gpu));
        byte[] b = Render(compositor, Incoming(gpu));

        Assert.Equal(a, Render(compositor, WithTransition(gpu, type, 0, NoValues, NoOptions)));
        Assert.Equal(b, Render(compositor, WithTransition(gpu, type, 1, NoValues, NoOptions)));
    }

    [GpuTheory]
    [MemberData(nameof(Cases))]
    public void Each_new_transition_matches_its_cpu_reference_on_the_gpu(string type, double progress, bool shutter) =>
        AssertMatches(false, type, progress, NoValues, NoOptions, shutter ? (progress - 0.06, progress + 0.06) : null);

    [GpuTheory]
    [MemberData(nameof(Devices))]
    public void Warp_draws_them_like_the_gpu(bool warp)
    {
        foreach (string type in new[] { T.Slide, T.Spin, T.LightWipe, T.Glitch, T.FilmBurn, T.Luma, T.Bounce })
        {
            AssertMatches(warp, type, 0.5, NoValues, NoOptions, null);
        }
    }

    [GpuTheory]
    [InlineData("linear", "off")]
    [InlineData("radial", "off")]
    [InlineData("angular", "on")]
    [InlineData("diamond", "off")]
    [InlineData("noise", "on")]
    [InlineData("bars", "off")]
    public void Luma_gradients_match_the_reference(string gradient, string invert) =>
        AssertMatches(false, T.Luma, 0.45, new Dictionary<string, double> { [TP.Angle] = 30, [TP.Softness] = 0.15 },
            new Dictionary<string, string> { [T.LumaOptions.Gradient] = gradient, [T.LumaOptions.Invert] = invert }, null);

    [GpuFact]
    public void A_wipe_and_a_luma_dissolve_draw_a_colored_border_ahead_of_the_change()
    {
        var border = new Dictionary<string, double>
        {
            [TP.Angle] = 20, [TP.Softness] = 0.02, [TP.BorderWidth] = 0.05, [TP.BorderRed] = 1, [TP.BorderGreen] = 0.8, [TP.BorderBlue] = 0,
        };
        AssertMatches(false, T.Wipe, 0.5, border, NoOptions, null);
        AssertMatches(false, T.Luma, 0.5, border, new Dictionary<string, string> { [T.LumaOptions.Gradient] = "radial" }, null);
    }

    [GpuFact]
    public void A_luma_image_drives_the_dissolve_and_a_missing_one_is_counted()
    {
        var resources = new MatteResources();
        AssertMatches(false, T.Luma, 0.5, new Dictionary<string, double> { [TP.Softness] = 0.1 }, NoOptions, null, resources,
            (u, _) => Math.Clamp((u * 256) - 0.5, 0, 255) / 255);

        using GpuDevice gpu = GpuDevice.Create();
        using Compositor compositor = TransitionTests.Ready(gpu);
        compositor.Resources = new MatteResources(missing: true);
        LayerFrame layer = WithTransition(gpu, T.Luma, 0.5, NoValues, NoOptions, resource: new ResourceKey("file:falta.png"));
        using CompositionFrame frame = Layers.Frame(Width, Height, layer);
        Assert.Equal(1, compositor.Render(frame).MissingContent);
    }

    [GpuFact]
    public void A_push_with_motion_blur_is_the_average_of_the_mix_along_the_shutter() =>
        AssertMatches(false, T.Push, 0.5, new Dictionary<string, double> { [TP.MotionBlur] = 0.5 },
            new Dictionary<string, string> { [T.PushOptions.Direction] = "up" }, (0.42, 0.58));

    [GpuTheory]
    [InlineData(T.Bounce, "right")]
    [InlineData(T.Bounce, "down")]
    public void A_bounce_overshoots_and_fills_the_gap_with_the_mirrored_incoming_clip(string type, string direction)
    {
        // A 0,9 el empuje ya pasó el final (q > 1): el hueco que deja muestra al que entra reflejado, no negro.
        AssertMatches(false, type, 0.9, NoValues, new Dictionary<string, string> { [T.PushOptions.Direction] = direction }, null);
    }

    private void AssertMatches(bool warp, string type, double progress, IReadOnlyDictionary<string, double> values,
        IReadOnlyDictionary<string, string> options, (double From, double To)? shutter, IEffectResources? resources = null,
        Func<double, double, double>? luma = null)
    {
        using GpuDevice gpu = warp ? GpuDevice.CreateWarp() : GpuDevice.Create();
        using Compositor compositor = TransitionTests.Ready(gpu);
        compositor.Resources = resources;
        FloatImage a = Opaque(Render(compositor, Outgoing(gpu)));
        FloatImage b = Opaque(Render(compositor, Incoming(gpu)));
        var reference = new TransitionReference(a, b) { LumaImage = luma };
        byte[] image = Render(compositor, WithTransition(gpu, type, progress, values, options, shutter,
            luma is null ? null : new ResourceKey(MatteResources.Gradient)));

        double total = 0;
        int outside = 0;
        double worst = 0;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                C4 expected = reference.At(type, values, options, x, y, progress, shutter);
                int i = ((y * Width) + x) * 4;
                double error = Math.Max(Math.Abs(image[i] - (Math.Clamp(expected.R, 0, 1) * 255)),
                    Math.Max(Math.Abs(image[i + 1] - (Math.Clamp(expected.G, 0, 1) * 255)), Math.Abs(image[i + 2] - (Math.Clamp(expected.B, 0, 1) * 255))));
                total += error;
                worst = Math.Max(worst, error);
                if (error > 3 && outside < 12)
                {
                    output.WriteLine($"  ({x}, {y}): {image[i]},{image[i + 1]},{image[i + 2]} frente a {expected.R * 255:F0},{expected.G * 255:F0},{expected.B * 255:F0}");
                }

                outside += error > 3 ? 1 : 0;
            }
        }

        double mean = total / (Width * Height);
        double fraction = (double)outside / (Width * Height);
        output.WriteLine($"MEDIDA {type}@{progress}{(shutter is null ? "" : "+obturador")}{(warp ? " WARP" : "")}: error medio {mean:F3}/255, máx {worst:F0}, fuera de ±3: {fraction:P2}");
        Assert.True(mean <= 0.6, $"{type}: error medio {mean:F2}/255.");
        Assert.True(worst <= 2, $"{type}: {fraction:P2} de los píxeles a más de 3/255 (máx {worst:F0}).");
    }

    private static byte[] Render(Compositor compositor, LayerFrame layer)
    {
        using CompositionFrame frame = Layers.Frame(Width, Height, layer);
        compositor.Render(frame);
        return compositor.ReadbackRgba();
    }

    private static FloatImage Opaque(byte[] rgba) => FloatImage.FromColors(Width, Height, (x, y) =>
    {
        int i = ((y * Width) + x) * 4;
        return new C3(rgba[i] / 255.0, rgba[i + 1] / 255.0, rgba[i + 2] / 255.0);
    });

    /// <summary>Saliente: celdas de 16 px de colores distintos, para que se note cualquier corrimiento.</summary>
    private static LayerFrame Outgoing(GpuDevice gpu, LayerTransition? transition = null)
    {
        var image = new YuvImage(Width, Height, false);
        for (int cy = 0; cy < Height / 16; cy++)
        {
            for (int cx = 0; cx < Width / 16; cx++)
            {
                image.Fill(cx * 16, cy * 16, 16, 16, ReferenceColor.Encode(40 + ((cx * 37) % 170), 50 + ((cy * 53) % 160), 210 - ((cx + cy) * 11 % 150), Bt709, false));
            }
        }

        return Layers.From(gpu, image, Bt709, new LayerLook(Transition: transition));
    }

    /// <summary>Entrante: franjas de 32 × 16 px con otra paleta.</summary>
    private static LayerFrame Incoming(GpuDevice gpu)
    {
        var image = new YuvImage(Width, Height, false);
        for (int cy = 0; cy < Height / 16; cy++)
        {
            for (int cx = 0; cx < Width / 32; cx++)
            {
                image.Fill(cx * 32, cy * 16, 32, 16, ReferenceColor.Encode(220 - ((cy * 31) % 160), 30 + ((cx * 47) % 180), 90 + ((cx + cy) * 19 % 140), Bt709, false));
            }
        }

        return Layers.From(gpu, image, Bt709, track: 1);
    }

    private static LayerFrame WithTransition(GpuDevice gpu, string type, double progress, IReadOnlyDictionary<string, double> values,
        IReadOnlyDictionary<string, string> options, (double From, double To)? shutter = null, ResourceKey? resource = null) =>
        Outgoing(gpu, new LayerTransition(EffectRegistry.Find(type)!.Code, (float)progress, TransitionValues.Of(type, values, options), Incoming(gpu))
        {
            Shutter = shutter is { } open ? ((float)open.From, (float)open.To) : null,
            Resource = resource,
        });

    /// <summary>Un degradado horizontal de 256 niveles como imagen gris de la disolución por luma.</summary>
    private sealed class MatteResources(bool missing = false) : IEffectResources
    {
        public const string Gradient = "file:degradado.png";

        private static readonly MatteFrame Frame = new(256, 4, [.. Enumerable.Range(0, 1024).Select(i => (byte)(i % 256))], 1);

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public bool TryGetLut(ResourceKey key, out LutData? lut)
        {
            lut = null;
            return false;
        }

        public bool TryGetMatte(ResourceKey key, double seconds, out MatteFrame? frame)
        {
            frame = !missing && key.Reference == Gradient ? Frame : null;
            return frame is not null;
        }
    }
}
