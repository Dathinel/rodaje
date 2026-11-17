using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.Playback.Composition;
using Rodaje.Render.Gpu;
using Rodaje.Render.Gpu.D3D11;
using Rodaje.Render.Interpolation;
using Rodaje.Render.Shaders;
using Rodaje.Render.Tests.Support;
using TerraFX.Interop.DirectX;
using static Rodaje.Render.RenderException;

namespace Rodaje.Render.Tests.Interpolation;

/// <summary>La interpolación (mezcla y flujo óptico) en el motor de <see cref="TestGpu.Engine"/>: D3D11 o Vulkan.</summary>
[Collection("Interpolation payload lifetime")]
public sealed class FrameInterpolatorTests
{
    private static readonly ShaderCache TestShaders = new(null);

    [EngineTheory]
    [InlineData(0.5f)]
    [InlineData(0.25f)]
    public void P016_preserves_sixteen_bit_plane_codes(float fraction)
    {
        using InterpolationRig rig = InterpolationRig.Create();
        IDecoderSurface a = rig.Upload(Codes(false), 16, 16, 16, 16, PlaneDepth.P016, VideoColor.Bt709Limited);
        IDecoderSurface b = rig.Upload(Codes(true), 16, 16, 16, 16, PlaneDepth.P016, VideoColor.Bt709Limited);
        IDecoderSurface output = Assert.IsAssignableFrom<IDecoderSurface>(rig.Blend(a, b, fraction));
        Assert.Equal(a.PixelFormat, output.PixelFormat);
        ushort[] first = rig.ReadPlanes(a);
        ushort[] second = rig.ReadPlanes(b);
        ushort[] actual = rig.ReadPlanes(output);
        for (int i = 0; i < actual.Length; i++)
        {
            Assert.InRange(Math.Abs(actual[i] - (((1 - fraction) * first[i]) + (fraction * second[i]))), 0, 1);
        }

        static ushort[] Codes(bool reverse)
        {
            var data = new ushort[16 * 16 * 3 / 2];
            for (int i = 0; i < data.Length; i++) data[i] = (ushort)(reverse ? 65535 - (i * 137) : i * 113);
            return data;
        }
    }

    [EngineTheory]
    [InlineData(false, 0.5f)]
    [InlineData(false, 0.25f)]
    [InlineData(true, 0.5f)]
    [InlineData(true, 0.25f)]
    public void Blend_matches_weighted_codes_in_both_planes(bool tenBit, float fraction)
    {
        using InterpolationRig rig = InterpolationRig.Create();
        YuvImage imageA = Pattern(tenBit, false, 32, 24);
        YuvImage imageB = Pattern(tenBit, true, 32, 24);
        var color = new VideoColor(YuvMatrix.Bt601, true);
        IDecoderSurface a = rig.Upload(imageA.Codes(), imageA.Width, imageA.Height, 32, 24, Depth(tenBit), color);
        IDecoderSurface b = rig.Upload(imageB.Codes(), imageB.Width, imageB.Height, 32, 24, Depth(tenBit), color);
        IDecoderSurface output = Assert.IsAssignableFrom<IDecoderSurface>(rig.Blend(a, b, fraction));
        Assert.Equal(a.PixelFormat, output.PixelFormat);
        Assert.Equal(a.Color, output.Color);
        Assert.Equal(a.Width, output.Width);
        Assert.Equal(a.Height, output.Height);
        ushort[] ca = rig.ReadPlanes(a);
        ushort[] cb = rig.ReadPlanes(b);
        ushort[] actual = rig.ReadPlanes(output);
        for (int i = 0; i < actual.Length; i++)
        {
            double expected = ((1 - fraction) * ca[i]) + (fraction * cb[i]);
            Assert.InRange(Math.Abs(actual[i] - expected), 0, 1);
        }

        Assert.Equal((long)a.CodedWidth * a.CodedHeight * 3 / 2 * (tenBit ? 2 : 1), rig.Pool.Bytes);
        rig.EndFrame();
        Assert.True(output.IsReleased);
    }

    [EngineTheory]
    [InlineData(2, 0)]
    [InlineData(1, 90)]
    public void Identical_pair_preserves_anamorphic_and_rotated_geometry(int sar, int rotation)
    {
        using TestGpu gpu = TestGpu.Create(warp: true);
        using var compositor = gpu.NewCompositor(96, 64, CompositeFormat.Rgba8Unorm, TestShaders);
        using LayerFrame source = Pattern(gpu, false, false, 32, 24);
        var nearest = new LayerFrame(0, "geometría", source.Frame, LayerTransform.Identity, new Rational(sar, 1), rotation);
        using CompositionFrame plain = Layers.Frame(96, 64, nearest);
        Assert.True(source.Frame.TryRetain());
        Assert.True(source.Frame.TryRetain());
        Assert.True(source.Frame.TryRetain());
        using CompositionFrame blended = Layers.Frame(96, 64,
            new LayerFrame(0, "geometría", source.Frame, LayerTransform.Identity, new Rational(sar, 1), rotation)
            { Next = source.Frame, Fraction = 0.5f, Sampling = FrameSampling.Blend });
        Assert.Equal(1, compositor.Render(plain, Geometry).LayersDrawn);
        byte[] expected = compositor.ReadbackRgba();
        CompositeResult result = compositor.Render(blended, Geometry);
        Assert.Equal(1, result.LayersDrawn);
        Assert.Equal(0, result.LayersSkipped);
        AssertBytes(expected, compositor.ReadbackRgba(), 1);
    }

    [EngineFact]
    public void Nearest_and_missing_pair_create_no_interpolation_textures()
    {
        using TestGpu gpu = TestGpu.Create(warp: true);
        using var compositor = gpu.NewCompositor(32, 24, CompositeFormat.Rgba8Unorm, TestShaders);
        using LayerFrame source = Pattern(gpu, false, false, 32, 24);
        using LayerFrame next = Pattern(gpu, false, true, 32, 24);
        using CompositionFrame plain = Layers.Frame(32, 24, source);
        Assert.True(source.Frame.TryRetain());
        Assert.True(source.Frame.TryRetain());
        Assert.True(next.Frame.TryRetain());
        using CompositionFrame nearest = Layers.Frame(32, 24, new LayerFrame(0, "c", source.Frame, LayerTransform.Identity)
        { Next = next.Frame, Fraction = 0.5f, Sampling = FrameSampling.Nearest });
        using CompositionFrame missing = Layers.Frame(32, 24, new LayerFrame(0, "c", source.Frame, LayerTransform.Identity)
        { Fraction = 0.5f, Sampling = FrameSampling.Blend });
        Assert.True(source.Frame.TryRetain());
        compositor.Render(plain);
        byte[] expected = compositor.ReadbackRgba();
        compositor.Render(nearest);
        AssertBytes(expected, compositor.ReadbackRgba(), 0);
        compositor.Render(missing);
        AssertBytes(expected, compositor.ReadbackRgba(), 0);
        Assert.Equal(0, compositor.InterpolationTextureCount);
    }

    [EngineTheory]
    [InlineData("plain")]
    [InlineData("effects")]
    [InlineData("motion")]
    [InlineData("nested")]
    [InlineData("transition")]
    [InlineData("nested-transition")]
    [InlineData("motion-fallback")]
    public async Task Every_video_path_uses_blend_and_optical_flow(string path)
    {
        using TestGpu gpu = TestGpu.Create(warp: true);
        // El tope del camino "motion-fallback" deja sitio para la mezcla pero no para los intermedios del flujo;
        // en Vulkan los planos son R16/Rg16 (P010) y el slot cuesta el doble que el NV12 de D3D11.
        using var compositor = gpu.NewCompositor(32, 24, CompositeFormat.Rgba8Unorm, TestShaders,
            path == "motion-fallback" ? (TestGpu.IsVulkan ? 8192 : 4096) : null);
        await compositor.ShadersReady;
        byte[] nearest = Render(FrameSampling.Nearest);
        byte[] blend = Render(FrameSampling.Blend);
        byte[] optical = Render(FrameSampling.OpticalFlow);
        Assert.False(nearest.AsSpan().SequenceEqual(blend));
        TestGpu.AssertSameImage(blend, optical);

        byte[] Render(FrameSampling mode)
        {
            LayerFrame a = Pattern(gpu, false, false, 32, 24);
            LayerFrame b = Pattern(gpu, false, true, 32, 24);
            LayerFrame sampled = new(0, "c", a.Frame, LayerTransform.Identity)
            {
                Next = b.Frame, Fraction = 0.5f, Sampling = mode,
                MotionSamples = path.StartsWith("motion", StringComparison.Ordinal) ? [LayerTransform.Identity, LayerTransform.Identity] : null,
                Effects = path == "effects" ? [new LayerEffect(EffectRegistry.Find(BuiltInEffects.Color)!.Code, default, null)] : [],
            };
            LayerFrame root = path switch
            {
                "nested" => new LayerFrame(0, "compuesto", new[] { sampled }, LayerTransform.Identity),
                "transition" => Transition(sampled, false),
                "nested-transition" => Transition(sampled, true),
                _ => sampled,
            };
            using CompositionFrame frame = Layers.Frame(32, 24, root);
            CompositeResult result = compositor.Render(frame);
            Assert.Equal(1, result.LayersDrawn);
            Assert.Equal(0, result.LayersSkipped);
            return compositor.ReadbackRgba();
        }

        LayerFrame Transition(LayerFrame incoming, bool nested)
        {
            LayerFrame outgoing = Pattern(gpu, false, false, 32, 24);
            return new LayerFrame(0, "sale", outgoing.Frame, LayerTransform.Identity)
            {
                Transition = new LayerTransition(EffectRegistry.Find(BuiltInTransitions.Crossfade)!.Code, 1, default,
                    nested ? new LayerFrame(0, "compuesto", new[] { incoming }, LayerTransform.Identity) : incoming),
            };
        }
    }

    [EngineFact]
    public void Two_hundred_frames_reuse_the_pool_and_release_all_payloads()
    {
        long before = TestGpu.IsVulkan || !OperatingSystem.IsWindowsVersionAtLeast(7) ? 0 : Interlocked.Read(ref LiveCount(null));
        // Los planos son R16/Rg16 (P010) en Vulkan, el doble de bytes que el NV12 de D3D11 para el mismo slot.
        long expectedSlot = TestGpu.IsVulkan ? 4608 : 2304;
        using (TestGpu gpu = TestGpu.Create(warp: true))
        using (var compositor = gpu.NewCompositor(32, 24, CompositeFormat.Rgba8Unorm, TestShaders))
        {
            for (int i = 0; i < 200; i++)
            {
                LayerFrame a = Pattern(gpu, false, false, 32, 24);
                LayerFrame b = Pattern(gpu, false, true, 32, 24);
                using (CompositionFrame frame = Layers.Frame(32, 24, new LayerFrame(0, "c", a.Frame, LayerTransform.Identity)
                { Next = b.Frame, Fraction = 0.5f, Sampling = FrameSampling.Blend }))
                {
                    CompositeResult result = compositor.Render(frame);
                    Assert.Equal(1, result.LayersDrawn);
                    Assert.Equal(expectedSlot, result.PoolBytes);
                    Assert.Equal(1, compositor.InterpolationTextureCount);
                }

                if (!TestGpu.IsVulkan && OperatingSystem.IsWindowsVersionAtLeast(7))
                {
                    Assert.Equal(before, Interlocked.Read(ref LiveCount(null)));
                }
            }

            compositor.TrimCachedViews();
            Assert.Equal(0, compositor.InterpolationTextureCount);
        }

        if (!TestGpu.IsVulkan && OperatingSystem.IsWindowsVersionAtLeast(7))
        {
            Assert.Equal(before, Interlocked.Read(ref LiveCount(null)));
        }
    }

    [EngineFact]
    public void Mismatched_and_rgb_inputs_fall_back_without_allocating()
    {
        using InterpolationRig rig = InterpolationRig.Create();
        YuvImage imageA = Pattern(false, false, 32, 24);
        YuvImage imageB = Pattern(true, false, 32, 24);
        YuvImage imageC = Pattern(false, false, 16, 24);
        IDecoderSurface a = rig.Upload(imageA.Codes(), imageA.Width, imageA.Height, 32, 24, PlaneDepth.Nv12, VideoColor.Bt709Limited);
        IDecoderSurface b = rig.Upload(imageB.Codes(), imageB.Width, imageB.Height, 32, 24, PlaneDepth.P010, VideoColor.Bt709Limited);
        IDecoderSurface c = rig.Upload(imageC.Codes(), imageC.Width, imageC.Height, 16, 24, PlaneDepth.Nv12, VideoColor.Bt709Limited);
        Assert.Null(rig.Blend(a, b, 0.5f));
        Assert.Null(rig.Blend(a, c, 0.5f));
        Assert.Null(rig.Blend(rig.RgbaSurface(32, 24), rig.RgbaSurface(32, 24), 0.5f));
        Assert.Equal(0, rig.TextureCount);
        Assert.Equal(0, rig.Pool.Bytes);
    }

    [EngineFact]
    public void Budget_is_shared_and_simultaneous_outputs_stay_distinct()
    {
        using InterpolationRig rig = InterpolationRig.Create(4608);
        YuvImage imageA = Pattern(false, false, 32, 24);
        YuvImage imageB = Pattern(false, true, 32, 24);
        IDecoderSurface a = rig.Upload(imageA.Codes(), imageA.Width, imageA.Height, 32, 24, PlaneDepth.Nv12, VideoColor.Bt709Limited);
        IDecoderSurface b = rig.Upload(imageB.Codes(), imageB.Width, imageB.Height, 32, 24, PlaneDepth.Nv12, VideoColor.Bt709Limited);
        IDecoderSurface one = Assert.IsAssignableFrom<IDecoderSurface>(rig.Blend(a, b, 0.25f));
        ushort[] expected = rig.ReadPlanes(one);
        IDecoderSurface two = Assert.IsAssignableFrom<IDecoderSurface>(rig.Blend(a, b, 0.5f));
        Assert.False(InterpolationRig.SameTexture(one, two));
        Assert.Equal(expected, rig.ReadPlanes(one));
        Assert.Null(rig.Blend(a, b, 0.75f));
        Assert.Null(rig.Pool.Rent(16, 16, GpuFormat.Rgba8Unorm, exact: true));
        Assert.Equal(rig.Pool.CapBytes, rig.Pool.Bytes);
        rig.EndFrame();
        rig.Trim();
        Assert.Equal(0, rig.Pool.Bytes);
        Assert.NotNull(rig.Pool.Rent(16, 16, GpuFormat.Rgba8Unorm, exact: true));
    }

    [GpuArrayFact]
    [SupportedOSPlatform("windows7.0")]
    public unsafe void Decoder_array_slices_are_selected_independently()
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var views = new PlaneViewCache((ID3D11Device*)gpu.NativeDevice);
        using var pool = new RenderTargetPool(D3D11Gpu.For(gpu), 1024 * 1024);
        using var interpolator = new FrameInterpolator(gpu, views, pool, TestShaders);
        var first = YuvImage.Solid(48, 32, false, new YuvCode(20, 60, 100));
        var second = YuvImage.Solid(48, 32, false, new YuvCode(200, 160, 120));
        nint texture = YuvImage.CreateTexture(gpu, [first, second]);
        try
        {
            using D3D11TexturePayload a = D3D11TexturePayload.FromTexture(gpu, texture, 1, 32, 24, VideoColor.Bt709Limited);
            using D3D11TexturePayload b = D3D11TexturePayload.FromTexture(gpu, texture, 0, 32, 24, VideoColor.Bt709Limited);
            D3D11TexturePayload output = Assert.IsType<D3D11TexturePayload>(interpolator.Blend(a, b, 0.25f));
            ushort[] actual = ReadPlanes(gpu, output);
            for (int i = 0; i < actual.Length; i++)
            {
                int expected = i < 32 * 24 ? 155 : i % 2 == 0 ? 135 : 115;
                Assert.InRange(Math.Abs(actual[i] - expected), 0, 1);
            }
        }
        finally
        {
            Layers.Release(texture);
        }
    }

    private static PlaneDepth Depth(bool tenBit) => tenBit ? PlaneDepth.P010 : PlaneDepth.Nv12;

    private static SourceGeometry Geometry(LayerFrame layer) => new(layer.SampleAspectRatio, layer.Rotation);

    /// <summary>La imagen de siempre: trama codificada algo mayor que la visible, con el patrón de 2×2 en lo visible.</summary>
    private static YuvImage Pattern(bool tenBit, bool second, int width, int height)
    {
        int scale = tenBit ? 4 : 1;
        var image = new YuvImage(width + 16, height + 8, tenBit);
        image.Fill(0, 0, image.Width, image.Height, new YuvCode(0, 0, 0));
        for (int y = 0; y < height; y += 2)
        {
            for (int x = 0; x < width; x += 2)
            {
                int n = ((x * 7) + (y * 13)) % 180;
                image.Fill(x, y, 2, 2, new YuvCode((second ? 240 - n : 16 + n) * scale,
                    (second ? 220 - n : 20 + n) * scale, (second ? 30 + n : 230 - n) * scale));
            }
        }

        return image;
    }

    private static LayerFrame Pattern(TestGpu gpu, bool tenBit, bool second, int width, int height) =>
        Layers.From(gpu, [Pattern(tenBit, second, width, height)], 0, width, height, new VideoColor(YuvMatrix.Bt601, true),
            LayerTransform.Identity);

    [SupportedOSPlatform("windows7.0")]
    private static unsafe ushort[] ReadPlanes(GpuDevice gpu, D3D11TexturePayload payload)
    {
        var device = (ID3D11Device*)gpu.NativeDevice;
        var context = (ID3D11DeviceContext*)gpu.NativeContext;
        var texture = (ID3D11Texture2D*)payload.Texture;
        D3D11_TEXTURE2D_DESC desc;
        texture->GetDesc(&desc);
        desc.ArraySize = 1;
        desc.BindFlags = 0;
        desc.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
        desc.CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
        ID3D11Texture2D* staging;
        Check(device->CreateTexture2D(&desc, null, &staging), "CreateTexture2D(lectura de planos)");
        try
        {
            context->CopySubresourceRegion((ID3D11Resource*)staging, 0, 0, 0, 0, (ID3D11Resource*)texture, (uint)payload.ArraySlice, null);
            D3D11_MAPPED_SUBRESOURCE mapped;
            Check(context->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped), "Map(lectura de planos)");
            try
            {
                int width = payload.Width;
                int height = payload.Height;
                var codes = new ushort[width * height * 3 / 2];
                bool eight = payload.DxgiFormat == (int)DXGI_FORMAT.DXGI_FORMAT_NV12;
                for (int y = 0; y < height * 3 / 2; y++)
                {
                    int row = y < height ? y : payload.CodedHeight + y - height;
                    byte* data = (byte*)mapped.pData + ((nuint)row * mapped.RowPitch);
                    for (int x = 0; x < width; x++) codes[(y * width) + x] = eight ? data[x] : ((ushort*)data)[x];
                }

                return codes;
            }
            finally
            {
                context->Unmap((ID3D11Resource*)staging, 0);
            }
        }
        finally
        {
            staging->Release();
        }
    }

    private static void AssertBytes(byte[] expected, byte[] actual, int tolerance)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++) Assert.InRange(Math.Abs(expected[i] - actual[i]), 0, tolerance);
    }

    [SupportedOSPlatform("windows7.0")]
    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_live")]
    private static extern ref long LiveCount(D3D11TexturePayload? type);
}

[CollectionDefinition("Interpolation payload lifetime", DisableParallelization = true)]
public sealed class InterpolationPayloadTests;
