using System.Globalization;
using System.Runtime.InteropServices;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.Playback.Composition;
using Rodaje.Render.Gpu;
using Rodaje.Render.Gpu.D3D11;
using Rodaje.Render.Tests.Gpu;
using Rodaje.Render.Tests.Support;
using Xunit.Abstractions;

namespace Rodaje.Render.Tests.Composition;

/// <summary>
/// El compositor entero sobre el motor Vulkan frente al mismo compositor sobre D3D11 WARP: capas NV12 subidas como texturas propias
/// (<see cref="GpuTextureSurface"/>), transformaciones, opacidad, mezclas por estado y por shader, efectos y transiciones. En Windows se
/// compara en el mismo proceso; en Linux, con las salidas de WARP que deja el trabajo de Windows de la CI
/// (<c>RODAJE_REFERENCIAS_WARP</c>). Criterio (ii) de <c>docs/mac-y-linux-fase0.md</c>: error medio ≤ 2e-4 y p99,9 ≤ 4e-3.
/// </summary>
public sealed class CompositorEngineTests(ITestOutputHelper output)
{
    private const int Width = 96;
    private const int Height = 64;
    private const double MeanLimit = 2e-4;
    private const double P999Limit = 4e-3;
    private static readonly VideoColor Bt709 = VideoColor.Bt709Limited;

    public static TheoryData<string> Cases => [.. s_cases.Keys];

    private static readonly Dictionary<string, Func<IGpu, LayerFrame[]>> s_cases = new(StringComparer.Ordinal)
    {
        ["una-capa"] = gpu => [Layer(gpu, Pattern(Width, Height, 1), LayerTransform.Identity)],
        ["escalada-y-movida"] = gpu =>
        [
            Layer(gpu, Pattern(Width, Height, 1), LayerTransform.Identity),
            Layer(gpu, Pattern(32, 32, 2), new LayerTransform(12, -6, 0.5, 0, 0.7), track: 1),
        ],
        ["girada"] = gpu => [Layer(gpu, Pattern(Width, Height, 3), new LayerTransform(0, 0, 0.8, 17, 1))],
        ["multiplicar"] = gpu =>
        [
            Layer(gpu, Pattern(Width, Height, 1), LayerTransform.Identity),
            Layer(gpu, Pattern(Width, Height, 4), LayerTransform.Identity, track: 1, blend: BlendMode.Multiply),
        ],
        ["superponer"] = gpu =>
        [
            Layer(gpu, Pattern(Width, Height, 1), LayerTransform.Identity),
            Layer(gpu, Pattern(Width, Height, 5), LayerTransform.Identity, track: 1, blend: BlendMode.Overlay),
        ],
        ["color-y-vineta"] = gpu =>
        [
            Layer(gpu, Pattern(Width, Height, 6), LayerTransform.Identity,
                effects: [Fx.Color((BuiltInEffects.ColorParameters.Exposure, 0.4)), Fx.Vignette(0.5, 0.5, 0.5)]),
        ],
        ["desenfoque"] = gpu => [Layer(gpu, Pattern(Width, Height, 7), LayerTransform.Identity, effects: [Fx.Blur(6)])],
        ["nitidez"] = gpu => [Layer(gpu, Pattern(Width, Height, 8), LayerTransform.Identity, effects: [Fx.Sharpen(1, 2)])],
        ["fundido-cruzado"] = gpu =>
        [
            Layer(gpu, Pattern(Width, Height, 9), LayerTransform.Identity, transition: new LayerTransition(
                EffectRegistry.Find(BuiltInTransitions.Crossfade)!.Code, 0.4f, Fx.Transition(BuiltInTransitions.Crossfade),
                Layer(gpu, Pattern(Width, Height, 10), LayerTransform.Identity, track: 1))),
        ],
    };

    [VulkanTheory]
    [MemberData(nameof(Cases))]
    public void Vulkan_composes_like_d3d11_warp(string name)
    {
        float[] vulkan = Compose(VulkanTestGpu.Gpu, name);
        float[]? reference = Reference(name, out string? skip);
        if (reference is null)
        {
            output.WriteLine(skip);
            return;
        }

        Assert.Equal(reference.Length, vulkan.Length);
        (double max, double mean, double p999) = Errors(reference, vulkan);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name}: máximo {max:0.###e+0}, medio {mean:0.###e+0}, p99,9 {p999:0.###e+0}"));
        Assert.True(mean <= MeanLimit, $"{name}: error medio {mean} > {MeanLimit}");
        Assert.True(p999 <= P999Limit, $"{name}: p99,9 {p999} > {P999Limit}");
    }

    /// <summary>Con <c>RODAJE_REFERENCIAS_WARP</c> en Windows, deja la composición de WARP de cada caso para el trabajo de Linux.</summary>
    [Fact]
    public void Generate_warp_references()
    {
        string? folder = Environment.GetEnvironmentVariable(ReferenceVariable);
        if (folder is null || !OperatingSystem.IsWindowsVersionAtLeast(7))
        {
            return;
        }

        Directory.CreateDirectory(folder);
        using D3D11Gpu warp = D3D11Gpu.Create(preferSoftware: true);
        foreach (string name in s_cases.Keys)
        {
            File.WriteAllBytes(Path.Combine(folder, $"compositor-{name}.bin"), MemoryMarshal.AsBytes(Compose(warp, name).AsSpan()).ToArray());
        }
    }

    private const string ReferenceVariable = "RODAJE_REFERENCIAS_WARP";

    private static float[]? Reference(string name, out string? skip)
    {
        skip = null;
        if (OperatingSystem.IsWindowsVersionAtLeast(7))
        {
            using D3D11Gpu warp = D3D11Gpu.Create(preferSoftware: true);
            return Compose(warp, name);
        }

        string? folder = Environment.GetEnvironmentVariable(ReferenceVariable);
        string? file = folder is null ? null : Path.Combine(folder, $"compositor-{name}.bin");
        if (file is null || !File.Exists(file))
        {
            skip = $"Sin referencia de WARP para {name} ({ReferenceVariable}): solo se comprobó que compone.";
            return null;
        }

        return MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(file)).ToArray();
    }

    private static float[] Compose(IGpu gpu, string name)
    {
        LayerFrame[] layers = s_cases[name](gpu);
        try
        {
            using var compositor = new Compositor(gpu, Width, Height, CompositeFormat.Rgba16Float, new Shaders.ShaderCache(null));
            CompositeResult result = compositor.RenderStrict(new CompositionFrame(Time.Zero, Width, Height, layers));
            Assert.Equal(layers.Length, result.LayersDrawn);
            Assert.Equal(0, result.EffectsSkipped);
            return compositor.ReadbackRgbaFloat();
        }
        finally
        {
            foreach (LayerFrame layer in layers)
            {
                layer.Dispose();
            }
        }
    }

    private static LayerFrame Layer(IGpu gpu, (byte[] Luma, byte[] Chroma, int Width, int Height) image, LayerTransform transform, int track = 0,
        BlendMode blend = BlendMode.Normal, IReadOnlyList<LayerEffect>? effects = null, LayerTransition? transition = null)
    {
        GpuTexture luma = gpu.CreateTexture(new GpuTextureDesc(image.Width, image.Height, GpuFormat.R8Unorm, GpuTextureUsage.Sampled), image.Luma);
        GpuTexture chroma = gpu.CreateTexture(new GpuTextureDesc(image.Width / 2, image.Height / 2, GpuFormat.Rg8Unorm, GpuTextureUsage.Sampled),
            image.Chroma);
        var surface = new GpuTextureSurface(luma, chroma, FramePixelFormat.Nv12, Bt709);
        var frame = new DecodedFrame(0, Time.Zero, Time.FromTicks(1), surface, 0);
        return new LayerFrame(track, $"clip-{track}", frame, transform) { Effects = effects ?? [], Blend = blend, Transition = transition };
    }

    /// <summary>NV12 de rango limitado por fórmula (sin aleatorios): degradados y bandas que cambian con <paramref name="seed"/>.</summary>
    private static (byte[] Luma, byte[] Chroma, int Width, int Height) Pattern(int width, int height, int seed)
    {
        byte[] luma = new byte[width * height];
        byte[] chroma = new byte[width * height / 2];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                luma[(y * width) + x] = (byte)(16 + (((x * 7) + (y * 3 * seed) + (seed * 29)) % 220));
            }
        }

        for (int y = 0; y < height / 2; y++)
        {
            for (int x = 0; x < width / 2; x++)
            {
                int i = ((y * width / 2) + x) * 2;
                chroma[i] = (byte)(16 + (((x * 9) + (seed * 37)) % 225));
                chroma[i + 1] = (byte)(16 + (((y * 11) + (seed * 53)) % 225));
            }
        }

        return (luma, chroma, width, height);
    }

    private static (double Max, double Mean, double P999) Errors(float[] a, float[] b)
    {
        var errors = new double[a.Length];
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            errors[i] = Math.Abs((double)a[i] - b[i]);
            sum += errors[i];
        }

        Array.Sort(errors);
        return (errors[^1], sum / errors.Length, errors[(int)Math.Min(errors.Length - 1, Math.Ceiling(errors.Length * 0.999) - 1)]);
    }
}
