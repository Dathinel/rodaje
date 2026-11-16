using System.Runtime.CompilerServices;
using Rodaje.Analysis.Video.Interpolation;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Render.Interpolation;
using Rodaje.Render.Shaders;
using Rodaje.Render.Tests.Support;
using TerraFX.Interop.DirectX;
using Xunit.Abstractions;
using static Rodaje.Render.RenderException;

namespace Rodaje.Render.Tests.Interpolation;

[Collection("Interpolation payload lifetime")]
public sealed class FlowInterpolatorTests(ITestOutputHelper output)
{
    private const int Width = 320;
    private const int Height = 180;
    private static readonly ShaderCache TestShaders = new(null);

    [Fact]
    public void Defaults_match_analysis_without_a_production_dependency()
    {
        var p = new FlowParameters();
        Assert.Equal(FlowDefaults.FlowLevel, p.FlowLevel);
        Assert.Equal(FlowDefaults.PatchSize, p.PatchSize);
        Assert.Equal(FlowDefaults.PatchStride, p.PatchStride);
        Assert.Equal(FlowDefaults.Iterations, p.Iterations);
        Assert.Equal(FlowDefaults.DeltaThreshold, p.DeltaThreshold);
        Assert.Equal(FlowDefaults.DiagonalRegularization, p.DiagonalRegularization);
        Assert.Equal(FlowDefaults.CutHistogram, p.CutHistogram);
        Assert.Equal(FlowDefaults.CutResidual, p.CutResidual);
        Assert.Equal(FlowDefaults.Alpha, p.Alpha);
        Assert.Equal(FlowDefaults.Beta, p.Beta);
    }

    [Fact]
    public void All_compute_passes_compile_without_warnings()
    {
        foreach (string entry in new[] { "Downsample", "Search", "Densify", "Histogram", "Residual", "Decide", "Warp" })
            Assert.NotEmpty(TestShaders.GetOrCompile(FlowShaders.Optical(entry)));
    }

    [Theory]
    [InlineData(8, 0.25f)]
    [InlineData(8, 0.5f)]
    [InlineData(8, 0.75f)]
    [InlineData(16, 0.25f)]
    [InlineData(16, 0.5f)]
    [InlineData(16, 0.75f)]
    public void Translation_and_moving_color_match_reference_psnr(int bitDepth, float fraction)
    {
        (PlanarFrame a, PlanarFrame b, PlanarFrame truth) = CreateTranslationScene(bitDepth, fraction);
        Compare(a, b, truth, fraction, "Traslación y color", 16, true);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void Occlusion_matches_reference_psnr(int bitDepth)
    {
        (PlanarFrame a, PlanarFrame b, PlanarFrame truth) = CreateOcclusionScene();
        Compare(ConvertDepth(a, bitDepth), ConvertDepth(b, bitDepth), ConvertDepth(truth, bitDepth), 0.5f, "Oclusión", 0, false);
    }

    [Theory]
    [InlineData(8, 0.25f)]
    [InlineData(8, 0.5f)]
    [InlineData(8, 0.75f)]
    [InlineData(16, 0.25f)]
    [InlineData(16, 0.5f)]
    [InlineData(16, 0.75f)]
    public void Scene_cut_is_decided_on_gpu_and_returns_nearest_exactly(int bitDepth, float fraction)
    {
        (PlanarFrame a, PlanarFrame b) = CreateSceneCutPair();
        a = ConvertDepth(a, bitDepth); b = ConvertDepth(b, bitDepth);
        Compare(a, b, fraction < 0.5f ? a : b, fraction, "Corte", 0, false);
    }

    private unsafe void Compare(PlanarFrame a, PlanarFrame b, PlanarFrame truth, float fraction, string scene, int margin, bool checkChroma)
    {
        using GpuDevice gpu = GpuDevice.CreateWarp();
        using var views = new PlaneViewCache((ID3D11Device*)gpu.NativeDevice);
        using var pool = new RenderTargetPool((ID3D11Device*)gpu.NativeDevice, (ID3D11DeviceContext*)gpu.NativeContext, 64 * 1024 * 1024);
        using var interpolator = new FrameInterpolator(gpu, views, pool, TestShaders);
        using D3D11TexturePayload first = Upload(gpu, a);
        using D3D11TexturePayload second = Upload(gpu, b);
        D3D11TexturePayload result = Assert.IsType<D3D11TexturePayload>(interpolator.Flow(first, second, fraction));
        Assert.Equal(1, interpolator.FlowFrames);
        Assert.Equal(first.DxgiFormat, result.DxgiFormat);
        Assert.Equal(first.Color, result.Color);
        Assert.Equal(first.Width, result.Width);
        Assert.Equal(first.Height, result.Height);
        ushort[] codes = ReadPlanes(gpu, result);
        ushort[] y = codes[..(Width * Height)], uv = codes[(Width * Height)..];
        FlowOutcome reference = FlowReference.Interpolate(a, b, fraction, InterpolationMode.OpticalFlow);
        (bool cut, float histogram, float residual) = ReadDecision(gpu, interpolator.FlowDecisionBuffer);
        Assert.Equal(reference.SceneCut, cut);
        double gpuY = ComputePsnr(y, truth.Y, Width, Height, a.BitDepth, margin);
        double cpuY = ComputePsnr(reference.Frame.Y, truth.Y, Width, Height, a.BitDepth, margin);
        double gpuUv = ComputePsnrUv(uv, truth.UV, Width / 2, Height / 2, a.BitDepth, margin / 2);
        double cpuUv = ComputePsnrUv(reference.Frame.UV, truth.UV, Width / 2, Height / 2, a.BitDepth, margin / 2);
        output.WriteLine($"{scene}, {a.BitDepth} bits, t={fraction}: Y GPU={gpuY:F5}, CPU={cpuY:F5} dB; UV GPU={gpuUv:F5}, CPU={cpuUv:F5} dB; corte={cut}; histograma={histogram:F6}/{reference.HistogramDistance:F6}; residuo={residual:F6}/{reference.Residual:F6}");
        Assert.True(Math.Abs(gpuY - cpuY) < 0.5, $"PSNR Y: GPU {gpuY}, referencia {cpuY}.");
        if (checkChroma) Assert.True(Math.Abs(gpuUv - cpuUv) < 0.5, $"PSNR UV: GPU {gpuUv}, referencia {cpuUv}.");
        if (cut)
        {
            Assert.Equal(truth.Y, y); Assert.Equal(truth.UV, uv);
        }
    }

    [Fact]
    public unsafe void Two_hundred_frames_reuse_textures_and_release_payloads_and_budget()
    {
        long baseline = LiveCount(null);
        using GpuDevice gpu = GpuDevice.CreateWarp();
        using var views = new PlaneViewCache((ID3D11Device*)gpu.NativeDevice);
        using var pool = new RenderTargetPool((ID3D11Device*)gpu.NativeDevice, (ID3D11DeviceContext*)gpu.NativeContext, 64 * 1024 * 1024);
        using var interpolator = new FrameInterpolator(gpu, views, pool, TestShaders);
        {
            (PlanarFrame a, PlanarFrame b, _) = CreateTranslationScene(8);
            using D3D11TexturePayload first = Upload(gpu, a);
            using D3D11TexturePayload second = Upload(gpu, b);
            int textures = 0; long bytes = 0;
            for (int i = 0; i < 200; i++)
            {
                D3D11TexturePayload result = Assert.IsType<D3D11TexturePayload>(interpolator.Flow(first, second, (i % 3 + 1) * 0.25f));
                Assert.Equal(i + 1, interpolator.FlowFrames);
                if (i == 0) { textures = interpolator.TextureCount; bytes = pool.Bytes; }
                Assert.Equal(textures, interpolator.TextureCount); Assert.Equal(bytes, pool.Bytes);
                _ = ReadPlanes(gpu, result);
                interpolator.EndFrame();
                Assert.True(result.IsReleased);
                Assert.Equal(baseline + 2, LiveCount(null));
            }
            interpolator.Trim();
            Assert.Equal(0, pool.Bytes); Assert.Equal(0, interpolator.TextureCount);
        }
        Assert.Equal(baseline, LiveCount(null));
    }

    [Theory]
    [InlineData(1920, 1080, 64)]
    [InlineData(3840, 2160, 200)]
    public unsafe void Intermediate_and_output_vram_stay_within_budget(int width, int height, int megabytes)
    {
        using GpuDevice gpu = GpuDevice.CreateWarp();
        using var views = new PlaneViewCache((ID3D11Device*)gpu.NativeDevice);
        using var pool = new RenderTargetPool((ID3D11Device*)gpu.NativeDevice, (ID3D11DeviceContext*)gpu.NativeContext, megabytes * 1024L * 1024);
        using var interpolator = new FrameInterpolator(gpu, views, pool, TestShaders);
        using D3D11TexturePayload first = Upload(gpu, LargePattern(width, height, 0));
        using D3D11TexturePayload second = Upload(gpu, LargePattern(width, height, 8));
        D3D11TexturePayload result = Assert.IsType<D3D11TexturePayload>(interpolator.Flow(first, second, 0.5f));
        _ = ReadPlanes(gpu, result);
        Assert.Equal(1, interpolator.FlowFrames);
        output.WriteLine($"{width}×{height}: {pool.Bytes} bytes, {pool.Bytes / (1024.0 * 1024):F3} MiB (planos P010 incluidos).");
        Assert.InRange(pool.Bytes, 1, megabytes * 1024L * 1024);
        interpolator.EndFrame(); interpolator.Trim(); Assert.Equal(0, pool.Bytes);
    }

    [Fact]
    public unsafe void Insufficient_intermediate_budget_falls_back_to_unchanged_blend()
    {
        using GpuDevice gpu = GpuDevice.CreateWarp();
        using var views = new PlaneViewCache((ID3D11Device*)gpu.NativeDevice);
        using var pool = new RenderTargetPool((ID3D11Device*)gpu.NativeDevice, (ID3D11DeviceContext*)gpu.NativeContext, Width * Height * 3);
        using var interpolator = new FrameInterpolator(gpu, views, pool, TestShaders);
        (PlanarFrame a, PlanarFrame b, _) = CreateTranslationScene(8);
        using D3D11TexturePayload first = Upload(gpu, a);
        using D3D11TexturePayload second = Upload(gpu, b);
        ushort[] expected = ReadPlanes(gpu, Assert.IsType<D3D11TexturePayload>(interpolator.Blend(first, second, 0.5f)));
        ushort[] actual = ReadPlanes(gpu, Assert.IsType<D3D11TexturePayload>(interpolator.Flow(first, second, 0.5f)));
        Assert.Equal(0, interpolator.FlowFrames); Assert.Equal(expected, actual);
        interpolator.EndFrame(); interpolator.Trim(); Assert.Equal(0, pool.Bytes);
    }

    [GpuArrayTheory]
    [Trait("Categoria", "Rendimiento")]
    [InlineData(1920, 1080, 4)]
    [InlineData(3840, 2160, 12)]
    public unsafe void Hardware_reports_gpu_milliseconds_per_frame(int width, int height, double limit)
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var views = new PlaneViewCache((ID3D11Device*)gpu.NativeDevice);
        using var pool = new RenderTargetPool((ID3D11Device*)gpu.NativeDevice, (ID3D11DeviceContext*)gpu.NativeContext, 200 * 1024L * 1024);
        using var interpolator = new FrameInterpolator(gpu, views, pool, TestShaders);
        using D3D11TexturePayload first = Upload(gpu, LargePattern(width, height, 0));
        using D3D11TexturePayload second = Upload(gpu, LargePattern(width, height, 8));
        using var timer = new GpuTimer(gpu);
        var samples = new List<double>();
        for (int i = 0; i < 40; i++)
        {
            timer.Begin();
            Assert.NotNull(interpolator.Flow(first, second, 0.5f));
            timer.End();
            double? ms = timer.WaitMilliseconds(TimeSpan.FromSeconds(10));
            Assert.NotNull(ms);
            if (i >= 10) samples.Add(ms.Value);
            interpolator.EndFrame();
        }
        Assert.Equal(40, interpolator.FlowFrames);
        samples.Sort(); double median = samples[samples.Count / 2];
        output.WriteLine($"{gpu.AdapterName}; {width}×{height}: mediana {median:F3} ms/cuadro, VRAM {pool.Bytes / (1024.0 * 1024):F3} MiB.");
        Assert.True(median <= limit, $"{width}×{height}: {median:F3} ms supera {limit} ms.");
    }

    [Fact]
    public unsafe void Failed_compute_pass_releases_intermediates_and_falls_back_to_blend()
    {
        var failingShaders = new ShaderCache(null)
        {
            Compile = variant => variant.EntryPoint == "Search"
                ? throw new RenderException("Fallo de compute inyectado.") : TestShaders.GetOrCompile(variant),
        };
        using GpuDevice gpu = GpuDevice.CreateWarp();
        using var views = new PlaneViewCache((ID3D11Device*)gpu.NativeDevice);
        using var pool = new RenderTargetPool((ID3D11Device*)gpu.NativeDevice, (ID3D11DeviceContext*)gpu.NativeContext, 64 * 1024 * 1024);
        using var interpolator = new FrameInterpolator(gpu, views, pool, failingShaders);
        (PlanarFrame a, PlanarFrame b, _) = CreateTranslationScene(8);
        using D3D11TexturePayload first = Upload(gpu, a);
        using D3D11TexturePayload second = Upload(gpu, b);
        ushort[] expected = ReadPlanes(gpu, Assert.IsType<D3D11TexturePayload>(interpolator.Blend(first, second, 0.5f)));
        interpolator.EndFrame();
        ushort[] actual = ReadPlanes(gpu, Assert.IsType<D3D11TexturePayload>(interpolator.Flow(first, second, 0.5f)));
        Assert.Equal(expected, actual);
        Assert.Equal(0, interpolator.FlowFrames);
        Assert.Equal(1, interpolator.TextureCount);
        Assert.Equal(Width * Height * 3 / 2, pool.Bytes);
        interpolator.EndFrame(); interpolator.Trim(); Assert.Equal(0, pool.Bytes);
    }

    [Theory]
    [InlineData(DXGI_FORMAT.DXGI_FORMAT_NV12)]
    [InlineData(DXGI_FORMAT.DXGI_FORMAT_P010)]
    [InlineData(DXGI_FORMAT.DXGI_FORMAT_P016)]
    public unsafe void Visible_planes_ignore_coded_padding_and_preserve_format(DXGI_FORMAT format)
    {
        int depth = format == DXGI_FORMAT.DXGI_FORMAT_NV12 ? 8 : 16;
        (PlanarFrame a, PlanarFrame b, _) = CreateTranslationScene(depth);
        using GpuDevice gpu = GpuDevice.CreateWarp();
        using var views = new PlaneViewCache((ID3D11Device*)gpu.NativeDevice);
        using var pool = new RenderTargetPool((ID3D11Device*)gpu.NativeDevice, (ID3D11DeviceContext*)gpu.NativeContext, 64 * 1024 * 1024);
        using var interpolator = new FrameInterpolator(gpu, views, pool, TestShaders);
        using D3D11TexturePayload first = Upload(gpu, a, format, 16);
        using D3D11TexturePayload second = Upload(gpu, b, format, 32);
        D3D11TexturePayload result = Assert.IsType<D3D11TexturePayload>(interpolator.Flow(first, second, 0.5f));
        Assert.Equal(1, interpolator.FlowFrames);
        Assert.Equal((int)format, result.DxgiFormat);
        Assert.Equal(a.Width, result.Width); Assert.Equal(a.Height, result.Height);
        Assert.Equal(first.CodedWidth, result.CodedWidth); Assert.Equal(first.CodedHeight, result.CodedHeight);
        Assert.Equal(first.Color, result.Color);
        ushort[] actual = ReadPlanes(gpu, result);
        PlanarFrame reference = FlowReference.Interpolate(a, b, 0.5f, InterpolationMode.OpticalFlow).Frame;
        ushort[] expected = [.. reference.Y, .. reference.UV];
        double error = actual.Select((c, i) => Math.Abs((double)c - expected[i])).Average();
        Assert.True(error < 1, $"Error medio de planos visibles: {error} códigos.");
    }

    private static PlanarFrame LargePattern(int width, int height, int shift)
    {
        var y = new ushort[width * height]; var uv = new ushort[width * height / 2];
        for (int j = 0; j < height; j++)
            for (int i = 0; i < width; i++) y[j * width + i] = (ushort)((int)MathF.Round(SampleLumaTexture(i - shift, j) * 1023) << 6);
        Array.Fill(uv, (ushort)32768);
        return new PlanarFrame(width, height, 16, y, uv);
    }

    private static PlanarFrame ConvertDepth(PlanarFrame frame, int depth) => depth == 8 ? frame : new PlanarFrame(frame.Width, frame.Height, 16,
        Array.ConvertAll(frame.Y, c => (ushort)(c * 256)), Array.ConvertAll(frame.UV, c => (ushort)(c * 256)));

    private static unsafe D3D11TexturePayload Upload(GpuDevice gpu, PlanarFrame frame, DXGI_FORMAT? format = null, int padding = 0)
    {
        bool eight = frame.BitDepth == 8;
        var codes = new ushort[frame.Y.Length + frame.UV.Length];
        frame.Y.CopyTo(codes, 0); frame.UV.CopyTo(codes, frame.Y.Length);
        byte[] data = new byte[codes.Length * (eight ? 1 : 2)];
        if (eight) { for (int i = 0; i < codes.Length; i++) data[i] = (byte)codes[i]; }
        else System.Buffer.BlockCopy(codes, 0, data, 0, data.Length);
        int codedWidth = frame.Width + padding, codedHeight = frame.Height + padding;
        if (padding != 0)
        {
            int pixelBytes = eight ? 1 : 2;
            byte[] padded = new byte[codedWidth * codedHeight * 3 / 2 * pixelBytes];
            Array.Fill(padded, (byte)255);
            for (int row = 0; row < frame.Height * 3 / 2; row++)
            {
                int destination = row < frame.Height ? row : codedHeight + row - frame.Height;
                data.AsSpan(row * frame.Width * pixelBytes, frame.Width * pixelBytes).CopyTo(padded.AsSpan(destination * codedWidth * pixelBytes));
            }
            data = padded;
        }
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)codedWidth, Height = (uint)codedHeight, MipLevels = 1, ArraySize = 1,
            Format = format ?? (eight ? DXGI_FORMAT.DXGI_FORMAT_NV12 : DXGI_FORMAT.DXGI_FORMAT_P010),
            SampleDesc = new DXGI_SAMPLE_DESC(1, 0), Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
        };
        ID3D11Texture2D* texture;
        fixed (byte* source = data)
        {
            var initial = new D3D11_SUBRESOURCE_DATA { pSysMem = source, SysMemPitch = (uint)(codedWidth * (eight ? 1 : 2)) };
            Check(((ID3D11Device*)gpu.NativeDevice)->CreateTexture2D(&desc, &initial, &texture), "CreateTexture2D(prueba de flujo)");
        }
        try { return D3D11TexturePayload.FromTexture(gpu, (nint)texture, 0, frame.Width, frame.Height, VideoColor.Bt709Limited); }
        finally { texture->Release(); }
    }

    private static unsafe (bool Cut, float Histogram, float Residual) ReadDecision(GpuDevice gpu, nint buffer)
    {
        var desc = new D3D11_BUFFER_DESC { ByteWidth = 16, Usage = D3D11_USAGE.D3D11_USAGE_STAGING, CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ };
        ID3D11Buffer* staging;
        Check(((ID3D11Device*)gpu.NativeDevice)->CreateBuffer(&desc, null, &staging), "CreateBuffer(lectura del corte)");
        var context = (ID3D11DeviceContext*)gpu.NativeContext;
        try
        {
            context->CopyResource((ID3D11Resource*)staging, (ID3D11Resource*)buffer);
            D3D11_MAPPED_SUBRESOURCE mapped;
            Check(context->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped), "Map(lectura del corte)");
            try { float* values = (float*)mapped.pData; return (values[0] != 0, values[1], values[2]); }
            finally { context->Unmap((ID3D11Resource*)staging, 0); }
        }
        finally { staging->Release(); }
    }

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_live")]
    private static extern ref long LiveCount(D3D11TexturePayload? type);
    private static (PlanarFrame A, PlanarFrame B, PlanarFrame Truth) CreateTranslationScene(int bitDepth, float fraction = 0.5f)
    {
        float maxCode = bitDepth == 8 ? 255f : 65535f;
        ushort[] aY = new ushort[Width * Height];
        ushort[] bY = new ushort[Width * Height];
        ushort[] truthY = new ushort[Width * Height];

        for (int y = 0; y < Height; y++)
        {
            int row = y * Width;
            for (int x = 0; x < Width; x++)
            {
                aY[row + x] = (ushort)Math.Clamp(MathF.Round(SampleLumaTexture(x, y) * maxCode), 0f, maxCode);
                bY[row + x] = (ushort)Math.Clamp(MathF.Round(SampleLumaTexture(x - 8, y) * maxCode), 0f, maxCode);
                truthY[row + x] = (ushort)Math.Clamp(MathF.Round(SampleLumaTexture(x - 8 * fraction, y) * maxCode), 0f, maxCode);
            }
        }

        int uvW = Width / 2;
        int uvH = Height / 2;
        ushort[] aUV = new ushort[uvW * uvH * 2];
        ushort[] bUV = new ushort[uvW * uvH * 2];
        ushort[] truthUV = new ushort[uvW * uvH * 2];

        for (int y = 0; y < uvH; y++)
        {
            int row = y * uvW * 2;
            for (int x = 0; x < uvW; x++)
            {
                (float uA, float vA) = SampleChromaTexture(x, y);
                (float uB, float vB) = SampleChromaTexture(x - 4, y);
                (float uT, float vT) = SampleChromaTexture(x - 4 * fraction, y);

                int idx = row + (x * 2);
                aUV[idx] = (ushort)Math.Clamp(MathF.Round(uA * maxCode), 0f, maxCode);
                aUV[idx + 1] = (ushort)Math.Clamp(MathF.Round(vA * maxCode), 0f, maxCode);

                bUV[idx] = (ushort)Math.Clamp(MathF.Round(uB * maxCode), 0f, maxCode);
                bUV[idx + 1] = (ushort)Math.Clamp(MathF.Round(vB * maxCode), 0f, maxCode);

                truthUV[idx] = (ushort)Math.Clamp(MathF.Round(uT * maxCode), 0f, maxCode);
                truthUV[idx + 1] = (ushort)Math.Clamp(MathF.Round(vT * maxCode), 0f, maxCode);
            }
        }

        return (
            new PlanarFrame(Width, Height, bitDepth, aY, aUV),
            new PlanarFrame(Width, Height, bitDepth, bY, bUV),
            new PlanarFrame(Width, Height, bitDepth, truthY, truthUV));
    }

    private static (PlanarFrame A, PlanarFrame B, PlanarFrame Truth) CreateOcclusionScene()
    {
        ushort[] aY = new ushort[Width * Height];
        ushort[] bY = new ushort[Width * Height];
        ushort[] truthY = new ushort[Width * Height];

        const int sqSize = 48;
        const int sqY = 66;
        const int sqAx = 120;
        const int sqBx = 128;
        const int sqTx = 124;

        for (int y = 0; y < Height; y++)
        {
            int row = y * Width;
            for (int x = 0; x < Width; x++)
            {
                float bg = 0.35f + (0.12f * MathF.Sin(x * 0.18f) * MathF.Cos(y * 0.22f));

                float valA = (x >= sqAx && x < sqAx + sqSize && y >= sqY && y < sqY + sqSize)
                    ? 0.78f + (0.10f * MathF.Sin((x - sqAx) * 0.4f) * MathF.Cos((y - sqY) * 0.35f))
                    : bg;

                float valB = (x >= sqBx && x < sqBx + sqSize && y >= sqY && y < sqY + sqSize)
                    ? 0.78f + (0.10f * MathF.Sin((x - sqBx) * 0.4f) * MathF.Cos((y - sqY) * 0.35f))
                    : bg;

                float valT = (x >= sqTx && x < sqTx + sqSize && y >= sqY && y < sqY + sqSize)
                    ? 0.78f + (0.10f * MathF.Sin((x - sqTx) * 0.4f) * MathF.Cos((y - sqY) * 0.35f))
                    : bg;

                aY[row + x] = (ushort)Math.Clamp(MathF.Round(valA * 255f), 0f, 255f);
                bY[row + x] = (ushort)Math.Clamp(MathF.Round(valB * 255f), 0f, 255f);
                truthY[row + x] = (ushort)Math.Clamp(MathF.Round(valT * 255f), 0f, 255f);
            }
        }

        int uvW = Width / 2;
        int uvH = Height / 2;
        ushort[] dummyUvA = new ushort[uvW * uvH * 2];
        ushort[] dummyUvB = new ushort[uvW * uvH * 2];
        ushort[] dummyUvT = new ushort[uvW * uvH * 2];
        Array.Fill(dummyUvA, (ushort)128);
        Array.Fill(dummyUvB, (ushort)128);
        Array.Fill(dummyUvT, (ushort)128);

        return (
            new PlanarFrame(Width, Height, 8, aY, dummyUvA),
            new PlanarFrame(Width, Height, 8, bY, dummyUvB),
            new PlanarFrame(Width, Height, 8, truthY, dummyUvT));
    }

    private static (PlanarFrame A, PlanarFrame B) CreateSceneCutPair()
    {
        ushort[] aY = new ushort[Width * Height];
        ushort[] bY = new ushort[Width * Height];
        int uvW = Width / 2;
        int uvH = Height / 2;
        ushort[] aUV = new ushort[uvW * uvH * 2];
        ushort[] bUV = new ushort[uvW * uvH * 2];

        for (int y = 0; y < Height; y++)
        {
            int row = y * Width;
            for (int x = 0; x < Width; x++)
            {
                float valA = 0.15f + (0.05f * MathF.Sin(x * 0.1f) * MathF.Cos(y * 0.1f));
                float valB = 0.82f + (0.05f * MathF.Cos(x * 0.15f) * MathF.Sin(y * 0.12f));
                aY[row + x] = (ushort)Math.Clamp(MathF.Round(valA * 255f), 0f, 255f);
                bY[row + x] = (ushort)Math.Clamp(MathF.Round(valB * 255f), 0f, 255f);
            }
        }

        Array.Fill(aUV, (ushort)64);
        Array.Fill(bUV, (ushort)200);

        return (new PlanarFrame(Width, Height, 8, aY, aUV), new PlanarFrame(Width, Height, 8, bY, bUV));
    }

    private static float SampleLumaTexture(float x, float y)
    {
        float v = 0.5f
            + (0.16f * MathF.Sin((x * 0.12f) + 0.3f) * MathF.Cos((y * 0.15f) + 0.5f))
            + (0.12f * MathF.Cos((x * 0.08f) - 0.2f))
            + (0.10f * MathF.Sin(((x + y) * 0.07f) + 1.1f))
            + (0.06f * MathF.Cos(((2f * x - y) * 0.06f) + 0.4f));
        return Math.Clamp(v, 0.05f, 0.95f);
    }

    private static (float U, float V) SampleChromaTexture(float x, float y)
    {
        float u = 0.5f
            + (0.15f * MathF.Sin((x * 0.14f) + 0.4f) * MathF.Cos((y * 0.11f) + 0.8f))
            + (0.10f * MathF.Cos((x * 0.09f) - 0.3f));
        float v = 0.5f
            + (0.15f * MathF.Cos((x * 0.12f) + 0.7f) * MathF.Sin((y * 0.13f) + 0.2f))
            + (0.10f * MathF.Sin(((x - y) * 0.08f) + 0.5f));
        return (Math.Clamp(u, 0.05f, 0.95f), Math.Clamp(v, 0.05f, 0.95f));
    }

    private static double ComputePsnr(ushort[] actual, ushort[] expected, int width, int height, int bitDepth, int margin)
    {
        double sumSq = 0;
        int count = 0;
        for (int y = margin; y < height - margin; y++)
        {
            int row = y * width;
            for (int x = margin; x < width - margin; x++)
            {
                double diff = actual[row + x] - expected[row + x];
                sumSq += diff * diff;
                count++;
            }
        }

        double mse = sumSq / count;
        if (mse <= 1e-12)
        {
            return 99.0;
        }

        double maxVal = bitDepth == 8 ? 255.0 : 65535.0;
        return 10.0 * Math.Log10((maxVal * maxVal) / mse);
    }

    private static double ComputePsnrUv(ushort[] actual, ushort[] expected, int width, int height, int bitDepth, int margin)
    {
        double sumSq = 0;
        int count = 0;
        for (int y = margin; y < height - margin; y++)
        {
            int row = y * width * 2;
            for (int x = margin; x < width - margin; x++)
            {
                int idx = row + (x * 2);
                double diffU = actual[idx] - expected[idx];
                double diffV = actual[idx + 1] - expected[idx + 1];
                sumSq += (diffU * diffU) + (diffV * diffV);
                count += 2;
            }
        }

        double mse = sumSq / count;
        if (mse <= 1e-12)
        {
            return 99.0;
        }

        double maxVal = bitDepth == 8 ? 255.0 : 65535.0;
        return 10.0 * Math.Log10((maxVal * maxVal) / mse);
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

}
