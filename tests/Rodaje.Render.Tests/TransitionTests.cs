using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Playback.Composition;
using Rodaje.Render.Tests.Support;
using Rodaje.TestMedia;
using T = Rodaje.Core.Model.BuiltInTransitions;

namespace Rodaje.Render.Tests;

public sealed class TransitionTests
{
    private const int Width = 256;
    private const int Height = 128;
    private static readonly VideoColor Bt709 = VideoColor.Bt709Limited;

    public static TheoryData<string> Kinds() =>
        [T.Crossfade, T.Dip, T.Wipe, T.Push, T.ZoomDissolve, T.BlurDissolve, T.Preset];

    /// <summary>En 0 se ve exactamente el clip que sale y en 1 exactamente el que entra, byte a byte.</summary>
    [GpuTheory]
    [MemberData(nameof(Kinds))]
    public void Progress_0_is_the_outgoing_clip_and_1_the_incoming_one(string type)
    {
        using GpuDevice gpu = GpuDevice.Create();
        using Compositor compositor = Ready(gpu);
        byte[] outgoingAlone = Render(compositor, Outgoing(gpu));
        byte[] incomingAlone = Render(compositor, Incoming(gpu));

        Assert.Equal(outgoingAlone, Render(compositor, WithTransition(gpu, type, 0)));
        Assert.Equal(incomingAlone, Render(compositor, WithTransition(gpu, type, 1)));
    }

    [GpuFact]
    public void Crossfade_at_half_is_the_average_within_one_code()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using Compositor compositor = Ready(gpu);
        byte[] a = Render(compositor, Outgoing(gpu));
        byte[] b = Render(compositor, Incoming(gpu));
        byte[] mixed = Render(compositor, WithTransition(gpu, T.Crossfade, 0.5f));

        for (int i = 0; i < mixed.Length; i++)
        {
            Assert.InRange(mixed[i], ((a[i] + b[i]) / 2.0) - 1, ((a[i] + b[i]) / 2.0) + 1);
        }
    }

    [GpuFact]
    public void Dip_passes_through_the_color_at_the_middle()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using Compositor compositor = Ready(gpu);
        EffectValues values = Fx.Transition(T.Dip, (T.DipParameters.Red, 1), (T.DipParameters.Green, 0.5), (T.DipParameters.Blue, 0));
        var image = new RgbaImage(Render(compositor, WithTransition(gpu, T.Dip, 0.5f, values)), Width, Height);

        image.AssertColor(10, 10, new Rgb(255, 127.5, 0));
        image.AssertColor(200, 100, new Rgb(255, 127.5, 0));
    }

    [GpuFact]
    public void Wipe_shows_the_incoming_clip_on_the_left_first()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using Compositor compositor = Ready(gpu);
        var a = new RgbaImage(Render(compositor, Outgoing(gpu)), Width, Height);
        var b = new RgbaImage(Render(compositor, Incoming(gpu)), Width, Height);
        EffectValues hard = Fx.Transition(T.Wipe, (T.WipeParameters.Softness, 0));
        var image = new RgbaImage(Render(compositor, WithTransition(gpu, T.Wipe, 0.5f, hard)), Width, Height);

        Assert.Equal(b.At(40, 64), image.At(40, 64));
        Assert.Equal(a.At(220, 64), image.At(220, 64));
    }

    [GpuTheory]
    [InlineData(0, 40, 220)]
    [InlineData(1, 220, 40)]
    public void Push_moves_the_outgoing_clip_toward_its_direction(int direction, int outgoingX, int incomingX)
    {
        using GpuDevice gpu = GpuDevice.Create();
        using Compositor compositor = Ready(gpu);
        var a = new RgbaImage(Render(compositor, Outgoing(gpu)), Width, Height);
        var b = new RgbaImage(Render(compositor, Incoming(gpu)), Width, Height);
        var image = new RgbaImage(Render(compositor, WithTransition(gpu, T.Push, 0.5f, PushValues(direction))), Width, Height);

        // A la mitad, el saliente se corrió medio cuadro: su píxel 128 + x quedó en x (izquierda) o en x − 128 (derecha).
        int shift = direction == 0 ? 128 : -128;
        Assert.Equal(a.At(outgoingX + shift, 64), image.At(outgoingX, 64));
        Assert.Equal(b.At(incomingX - shift, 64), image.At(incomingX, 64));
    }

    /// <summary>
    /// Arriba (2) y abajo (3), en la columna 128, donde el entrante tiene franjas horizontales: a la mitad, el entrante
    /// asoma medio cuadro (64 filas) por el lado contrario al que sale el saliente.
    /// </summary>
    [GpuTheory]
    [InlineData(2, 20, 100)]
    [InlineData(3, 100, 20)]
    public void Push_up_and_down_move_the_clips_vertically(int direction, int outgoingY, int incomingY)
    {
        using GpuDevice gpu = GpuDevice.Create();
        using Compositor compositor = Ready(gpu);
        var a = new RgbaImage(Render(compositor, Outgoing(gpu)), Width, Height);
        var b = new RgbaImage(Render(compositor, Incoming(gpu)), Width, Height);
        var image = new RgbaImage(Render(compositor, WithTransition(gpu, T.Push, 0.5f, PushValues(direction))), Width, Height);

        int shift = direction == 2 ? 64 : -64;
        Assert.Equal(a.At(128, outgoingY + shift), image.At(128, outgoingY));
        Assert.Equal(b.At(128, incomingY - shift), image.At(128, incomingY));
        Assert.NotEqual(b.At(128, incomingY), image.At(128, incomingY));
    }

    /// <summary>
    /// A la mitad, frente a la fórmula en la CPU aplicada a cada lado compuesto solo (sobre negro, o sea premultiplicado,
    /// con la croma ya interpolada): el zoom achica el muestreo hacia el centro y el desenfoque es la misma pirámide.
    /// </summary>
    [GpuTheory]
    [InlineData(T.ZoomDissolve)]
    [InlineData(T.BlurDissolve)]
    public void Zoom_and_blur_dissolve_at_half_match_the_cpu_reference(string type)
    {
        using GpuDevice gpu = GpuDevice.Create();
        using Compositor compositor = Ready(gpu);
        var image = new RgbaImage(Render(compositor, WithTransition(gpu, type, 0.5f)), Width, Height);

        double amount = EffectRegistry.Find(type)!.Parameters[0].DefaultValue;
        FloatImage a = Premultiplied(Render(compositor, Outgoing(gpu)));
        FloatImage b = Premultiplied(Render(compositor, Incoming(gpu)));
        double scale = 1;
        if (type == T.ZoomDissolve)
        {
            scale = 1 + amount;
        }
        else
        {
            a = a.Blur(amount / 3);
            b = b.Blur(amount / 3);
        }

        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                double u = 0.5 + ((((x + 0.5) / Width) - 0.5) / scale);
                double v = 0.5 + ((((y + 0.5) / Height) - 0.5) / scale);
                (C3 outgoing, _) = a.Sample(u, v);
                (C3 incoming, _) = b.Sample(u, v);
                image.AssertColor(x, y, C3.Mix(outgoing, incoming, 0.5).Map(EffectReference.Saturate).To255());
            }
        }
    }

    [GpuFact]
    public void A_preset_that_arrives_unresolved_falls_back_to_crossfade_and_is_counted()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using Compositor compositor = Ready(gpu);
        byte[] crossfade = Render(compositor, WithTransition(gpu, T.Crossfade, 0.3f));
        using CompositionFrame frame = Layers.Frame(Width, Height, WithTransition(gpu, T.Preset, 0.3f));
        CompositeResult result = compositor.Render(frame);

        Assert.Equal(crossfade, compositor.ReadbackRgba());
        Assert.Equal(1, result.MissingContent);
        Assert.Equal(1, result.EffectsApplied);
    }

    [GpuFact]
    public void Each_side_keeps_its_own_effects()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using Compositor compositor = Ready(gpu);
        LayerEffect[] sepia = [Fx.Color((BuiltInEffects.ColorParameters.Saturation, -1))];
        byte[] outgoingAlone = Render(compositor, Outgoing(gpu, sepia));
        LayerFrame incoming = Incoming(gpu);
        LayerFrame outgoing = Outgoing(gpu, sepia, new LayerTransition(EffectRegistry.Find(T.Crossfade)!.Code, 0, default, incoming));
        using CompositionFrame frame = Layers.Frame(Width, Height, outgoing);
        CompositeResult result = compositor.Render(frame);

        Assert.Equal(outgoingAlone, compositor.ReadbackRgba());
        Assert.Equal(2, result.EffectsApplied);
    }

    internal static Compositor Ready(GpuDevice gpu)
    {
        var compositor = new Compositor(gpu, Width, Height);
        Assert.True(compositor.ShadersReady.Wait(TimeSpan.FromSeconds(60)));
        return compositor;
    }

    private static byte[] Render(Compositor compositor, LayerFrame layer)
    {
        using CompositionFrame frame = Layers.Frame(Width, Height, layer);
        compositor.Render(frame);
        return compositor.ReadbackRgba();
    }

    /// <summary>La dirección va después de los parámetros del empuje (su desenfoque de movimiento).</summary>
    private static EffectValues PushValues(int direction)
    {
        EffectValues values = default;
        values[EffectRegistry.Find(T.Push)!.Parameters.Length] = direction;
        return values;
    }

    private static FloatImage Premultiplied(byte[] rgba) => FloatImage.FromColors(Width, Height, (x, y) =>
    {
        int i = ((y * Width) + x) * 4;
        return new C3(rgba[i] / 255.0, rgba[i + 1] / 255.0, rgba[i + 2] / 255.0);
    });

    /// <summary>Saliente: degradado horizontal de 8 franjas a cuadro completo.</summary>
    private static LayerFrame Outgoing(GpuDevice gpu, IReadOnlyList<LayerEffect>? effects = null, LayerTransition? transition = null)
    {
        var image = new YuvImage(Width, Height, false);
        for (int band = 0; band < 8; band++)
        {
            image.Fill(band * 32, 0, 32, Height, ReferenceColor.Encode(30 + (band * 28), 200 - (band * 20), 60, Bt709, false));
        }

        return Layers.From(gpu, image, Bt709, new LayerLook(effects, Transition: transition));
    }

    /// <summary>Entrante: una imagen más chica (deja bordes transparentes) con franjas horizontales.</summary>
    private static LayerFrame Incoming(GpuDevice gpu)
    {
        var image = new YuvImage(128, 128, false);
        for (int band = 0; band < 4; band++)
        {
            image.Fill(0, band * 32, 128, 32, ReferenceColor.Encode(220 - (band * 40), 40, 90 + (band * 40), Bt709, false));
        }

        return Layers.From(gpu, image, Bt709, track: 1);
    }

    private static LayerFrame WithTransition(GpuDevice gpu, string type, float progress, EffectValues? values = null) =>
        Outgoing(gpu, null, new LayerTransition(EffectRegistry.Find(type)!.Code, progress, values ?? Fx.Transition(type), Incoming(gpu)));
}
