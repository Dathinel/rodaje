using System.Runtime.CompilerServices;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.Playback.Composition;
using Rodaje.Render.Interpolation;
using Rodaje.Render.Shaders;
using Rodaje.Render.Tests.Support;
using TerraFX.Interop.DirectX;
using static Rodaje.Render.RenderException;
using Rodaje.Render.Gpu;
using Rodaje.Render.Gpu.D3D11;

namespace Rodaje.Render.Tests.Interpolation;

[Collection("Interpolation payload lifetime")]
public sealed class FrameInterpolatorTests
{
    private static readonly ShaderCache TestShaders = new(null);

    [Theory]
    [InlineData(0.5f)]
    [InlineData(0.25f)]
    public unsafe void P016_preserves_sixteen_bit_plane_codes(float fraction)
    {
        using GpuDevice gpu = GpuDevice.CreateWarp();
        using var views = new PlaneViewCache((ID3D11Device*)gpu.NativeDevice);
        using var pool = new RenderTargetPool(D3D11Gpu.For(gpu), 1024 * 1024);
        using var interpolator = new FrameInterpolator(gpu, views, pool, TestShaders);
        using D3D11TexturePayload a = Create(false);
        using D3D11TexturePayload b = Create(true);
        D3D11TexturePayload output = Assert.IsType<D3D11TexturePayload>(interpolator.Blend(a, b, fraction));
        Assert.Equal(a.DxgiFormat, output.DxgiFormat);
        ushort[] first = ReadPlanes(gpu, a);
        ushort[] second = ReadPlanes(gpu, b);
        ushort[] actual = ReadPlanes(gpu, output);
        for (int i = 0; i < actual.Length; i++)
        {
            Assert.InRange(Math.Abs(actual[i] - (((1 - fraction) * first[i]) + (fraction * second[i]))), 0, 1);
        }

        D3D11TexturePayload Create(bool reverse)
        {
            var data = new ushort[16 * 16 * 3 / 2];
            for (int i = 0; i < data.Length; i++) data[i] = (ushort)(reverse ? 65535 - (i * 137) : i * 113);
            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = 16, Height = 16, MipLevels = 1, ArraySize = 1, Format = DXGI_FORMAT.DXGI_FORMAT_P016,
                SampleDesc = new DXGI_SAMPLE_DESC(1, 0), Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
            };
            ID3D11Texture2D* texture;
            fixed (ushort* codes = data)
            {
                var initial = new D3D11_SUBRESOURCE_DATA { pSysMem = codes, SysMemPitch = 32 };
                Check(((ID3D11Device*)gpu.NativeDevice)->CreateTexture2D(&desc, &initial, &texture), "CreateTexture2D(P016)");
            }

            try
            {
                return D3D11TexturePayload.FromTexture(gpu, (nint)texture, 0, 16, 16, VideoColor.Bt709Limited);
            }
            finally
            {
                texture->Release();
            }
        }
    }

    [Theory]
    [InlineData(false, 0.5f)]
    [InlineData(false, 0.25f)]
    [InlineData(true, 0.5f)]
    [InlineData(true, 0.25f)]
    public unsafe void Blend_matches_weighted_codes_in_both_planes(bool tenBit, float fraction)
    {
        using GpuDevice gpu = GpuDevice.CreateWarp();
        using var views = new PlaneViewCache((ID3D11Device*)gpu.NativeDevice);
        using var pool = new RenderTargetPool(D3D11Gpu.For(gpu), 1024 * 1024);
        using var interpolator = new FrameInterpolator(gpu, views, pool, TestShaders);
        using LayerFrame first = Pattern(gpu, tenBit, false, 32, 24);
        using LayerFrame second = Pattern(gpu, tenBit, true, 32, 24);
        var a = (D3D11TexturePayload)first.Frame.Payload!;
        var b = (D3D11TexturePayload)second.Frame.Payload!;
        D3D11TexturePayload output = Assert.IsType<D3D11TexturePayload>(interpolator.Blend(a, b, fraction));
        Assert.Equal(a.DxgiFormat, output.DxgiFormat);
        Assert.Equal(a.Color, output.Color);
        Assert.Equal(a.Width, output.Width);
        Assert.Equal(a.Height, output.Height);
        ushort[] ca = ReadPlanes(gpu, a);
        ushort[] cb = ReadPlanes(gpu, b);
        ushort[] actual = ReadPlanes(gpu, output);
        for (int i = 0; i < actual.Length; i++)
        {
            double expected = ((1 - fraction) * ca[i]) + (fraction * cb[i]);
            Assert.InRange(Math.Abs(actual[i] - expected), 0, 1);
        }

        Assert.Equal((long)a.CodedWidth * a.CodedHeight * 3 / 2 * (tenBit ? 2 : 1), pool.Bytes);
        interpolator.EndFrame();
        Assert.True(output.IsReleased);
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(1, 90)]
    public void Identical_pair_preserves_anamorphic_and_rotated_geometry(int sar, int rotation)
    {
        using GpuDevice gpu = GpuDevice.CreateWarp();
        using var compositor = new Compositor(gpu, 96, 64, CompositeFormat.Rgba8Unorm, TestShaders);
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

    [Fact]
    public void Nearest_and_missing_pair_create_no_interpolation_textures()
    {
        using GpuDevice gpu = GpuDevice.CreateWarp();
        using var compositor = new Compositor(gpu, 32, 24, CompositeFormat.Rgba8Unorm, TestShaders);
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

    [Theory]
    [InlineData("plain")]
    [InlineData("effects")]
    [InlineData("motion")]
    [InlineData("nested")]
    [InlineData("transition")]
    [InlineData("nested-transition")]
    [InlineData("motion-fallback")]
    public async Task Every_video_path_uses_blend_and_optical_flow(string path)
    {
        using GpuDevice gpu = GpuDevice.CreateWarp();
        using var compositor = new Compositor(gpu, 32, 24, CompositeFormat.Rgba8Unorm, TestShaders,
            path == "motion-fallback" ? 4096 : null);
        await compositor.ShadersReady;
        byte[] nearest = Render(FrameSampling.Nearest);
        byte[] blend = Render(FrameSampling.Blend);
        byte[] optical = Render(FrameSampling.OpticalFlow);
        Assert.False(nearest.AsSpan().SequenceEqual(blend));
        AssertBytes(blend, optical, 0);

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

    [Fact]
    public void Two_hundred_frames_reuse_the_pool_and_release_all_payloads()
    {
        long before = Interlocked.Read(ref LiveCount(null));
        using (GpuDevice gpu = GpuDevice.CreateWarp())
        using (var compositor = new Compositor(gpu, 32, 24, CompositeFormat.Rgba8Unorm, TestShaders))
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
                    Assert.Equal(2304, result.PoolBytes);
                    Assert.Equal(1, compositor.InterpolationTextureCount);
                }

                Assert.Equal(before, Interlocked.Read(ref LiveCount(null)));
            }

            compositor.TrimCachedViews();
            Assert.Equal(0, compositor.InterpolationTextureCount);
        }

        Assert.Equal(before, Interlocked.Read(ref LiveCount(null)));
    }

    [Fact]
    public unsafe void Mismatched_and_rgb_inputs_fall_back_without_allocating()
    {
        using GpuDevice gpu = GpuDevice.CreateWarp();
        using var views = new PlaneViewCache((ID3D11Device*)gpu.NativeDevice);
        using var pool = new RenderTargetPool(D3D11Gpu.For(gpu), 1024 * 1024);
        using var interpolator = new FrameInterpolator(gpu, views, pool, TestShaders);
        using LayerFrame a = Pattern(gpu, false, false, 32, 24);
        using LayerFrame b = Pattern(gpu, true, false, 32, 24);
        using LayerFrame c = Pattern(gpu, false, false, 16, 24);
        Assert.Null(interpolator.Blend((D3D11TexturePayload)a.Frame.Payload!, (D3D11TexturePayload)b.Frame.Payload!, 0.5f));
        Assert.Null(interpolator.Blend((D3D11TexturePayload)a.Frame.Payload!, (D3D11TexturePayload)c.Frame.Payload!, 0.5f));
        using RenderTarget rgb = RenderTarget.Create(D3D11Gpu.For(gpu), 32, 24, GpuFormat.Bgra8Unorm);
        using D3D11TexturePayload rgba = D3D11TexturePayload.FromTexture(gpu, (nint)rgb.Texture, 0, 32, 24, VideoColor.Bt709Limited);
        Assert.Null(interpolator.Blend(rgba, rgba, 0.5f));
        Assert.Equal(0, interpolator.TextureCount);
        Assert.Equal(0, pool.Bytes);
    }

    [Fact]
    public unsafe void Budget_is_shared_and_simultaneous_outputs_stay_distinct()
    {
        using GpuDevice gpu = GpuDevice.CreateWarp();
        using var views = new PlaneViewCache((ID3D11Device*)gpu.NativeDevice);
        using var pool = new RenderTargetPool(D3D11Gpu.For(gpu), 4608);
        using var interpolator = new FrameInterpolator(gpu, views, pool, TestShaders);
        using LayerFrame first = Pattern(gpu, false, false, 32, 24);
        using LayerFrame second = Pattern(gpu, false, true, 32, 24);
        var a = (D3D11TexturePayload)first.Frame.Payload!;
        var b = (D3D11TexturePayload)second.Frame.Payload!;
        D3D11TexturePayload one = Assert.IsType<D3D11TexturePayload>(interpolator.Blend(a, b, 0.25f));
        ushort[] expected = ReadPlanes(gpu, one);
        D3D11TexturePayload two = Assert.IsType<D3D11TexturePayload>(interpolator.Blend(a, b, 0.5f));
        Assert.NotEqual(one.Texture, two.Texture);
        Assert.Equal(expected, ReadPlanes(gpu, one));
        Assert.Null(interpolator.Blend(a, b, 0.75f));
        Assert.Null(pool.Rent(16, 16, GpuFormat.Rgba8Unorm, exact: true));
        Assert.Equal(pool.CapBytes, pool.Bytes);
        interpolator.EndFrame();
        interpolator.Trim();
        Assert.Equal(0, pool.Bytes);
        Assert.NotNull(pool.Rent(16, 16, GpuFormat.Rgba8Unorm, exact: true));
    }

    [GpuArrayFact]
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

    private static SourceGeometry Geometry(LayerFrame layer) => new(layer.SampleAspectRatio, layer.Rotation);

    private static LayerFrame Pattern(GpuDevice gpu, bool tenBit, bool second, int width, int height)
    {
        int scale = tenBit ? 4 : 1;
        var image = YuvImage.Solid(width + 16, height + 8, tenBit, new YuvCode(0, 0, 0));
        for (int y = 0; y < height; y += 2)
        {
            for (int x = 0; x < width; x += 2)
            {
                int n = ((x * 7) + (y * 13)) % 180;
                image.Fill(x, y, 2, 2, new YuvCode((second ? 240 - n : 16 + n) * scale,
                    (second ? 220 - n : 20 + n) * scale, (second ? 30 + n : 230 - n) * scale));
            }
        }

        return Layers.From(gpu, [image], 0, width, height, new VideoColor(YuvMatrix.Bt601, true), LayerTransform.Identity);
    }

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

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_live")]
    private static extern ref long LiveCount(D3D11TexturePayload? type);
}

[CollectionDefinition("Interpolation payload lifetime", DisableParallelization = true)]
public sealed class InterpolationPayloadTests;
