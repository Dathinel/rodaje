using System.Diagnostics;
using Rodaje.Analysis.Video.Interpolation;
using Xunit.Abstractions;

namespace Rodaje.Analysis.Tests.Video.Interpolation;

[Collection(VideoTestGroup.Name)]
public sealed class FlowReferenceTests(ITestOutputHelper output)
{
    private const int Width = 320;
    private const int Height = 180;

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void Translation_reconstructs_with_high_psnr(int bitDepth)
    {
        (PlanarFrame a, PlanarFrame b, PlanarFrame truth) = CreateTranslationScene(bitDepth);

        var stopwatch = Stopwatch.StartNew();
        FlowOutcome flowOutcome = FlowReference.Interpolate(a, b, 0.5f, InterpolationMode.OpticalFlow);
        stopwatch.Stop();

        FlowOutcome blendOutcome = FlowReference.Interpolate(a, b, 0.5f, InterpolationMode.Blend);

        double psnrY = ComputePsnr(flowOutcome.Frame.Y, truth.Y, Width, Height, bitDepth, margin: 16);
        double psnrBlendY = ComputePsnr(blendOutcome.Frame.Y, truth.Y, Width, Height, bitDepth, margin: 16);
        double psnrUv = ComputePsnrUv(flowOutcome.Frame.UV, truth.UV, Width / 2, Height / 2, bitDepth, margin: 8);

        output.WriteLine($"[Traslación {bitDepth} bits] Tiempo: {stopwatch.ElapsedMilliseconds} ms, PSNR Y: {psnrY:F2} dB (Blend: {psnrBlendY:F2} dB, diff: {psnrY - psnrBlendY:F2} dB), PSNR UV: {psnrUv:F2} dB");

        Assert.False(flowOutcome.SceneCut);
        Assert.True(psnrY >= 32.0, $"PSNR Y ({psnrY:F2} dB) debe ser >= 32 dB.");
        Assert.True(psnrY >= psnrBlendY + 10.0, $"PSNR Y ({psnrY:F2} dB) debe superar a Blend ({psnrBlendY:F2} dB) por al menos 10 dB.");
        Assert.True(psnrUv >= 30.0, $"PSNR UV ({psnrUv:F2} dB) debe ser >= 30 dB.");
    }

    [Fact]
    public void Occlusion_handles_disoccluded_regions_within_threshold()
    {
        (PlanarFrame a, PlanarFrame b, PlanarFrame truth) = CreateOcclusionScene();

        FlowOutcome outcome = FlowReference.Interpolate(a, b, 0.5f, InterpolationMode.OpticalFlow);

        int errorPixels = 0;
        int totalPixels = Width * Height;
        for (int i = 0; i < totalPixels; i++)
        {
            if (Math.Abs(outcome.Frame.Y[i] - truth.Y[i]) > 10)
            {
                errorPixels++;
            }
        }

        double errorFraction = (double)errorPixels / totalPixels;
        output.WriteLine($"[Oclusión 8 bits] Píxeles con error > 10: {errorPixels} / {totalPixels} ({errorFraction * 100:F2} %)");

        Assert.False(outcome.SceneCut);
        Assert.True(errorFraction < 0.02, $"La fracción de error ({errorFraction * 100:F2} %) debe ser < 2 %.");
    }

    [Fact]
    public void Scene_cut_detects_unrelated_frames_and_returns_nearest()
    {
        (PlanarFrame a, PlanarFrame b) = CreateSceneCutPair();

        FlowOutcome outcomeA = FlowReference.Interpolate(a, b, 0.3f, InterpolationMode.OpticalFlow);
        FlowOutcome outcomeB = FlowReference.Interpolate(a, b, 0.7f, InterpolationMode.OpticalFlow);

        output.WriteLine($"[Corte de escena] Distancia histograma: {outcomeA.HistogramDistance:F3}, Residuo: {outcomeA.Residual:F3}");

        Assert.True(outcomeA.SceneCut);
        Assert.True(outcomeB.SceneCut);
        Assert.True(outcomeA.HistogramDistance > 0.5f);
        Assert.True(outcomeA.Residual > 0.08f);

        Assert.Equal(a.Y, outcomeA.Frame.Y);
        Assert.Equal(a.UV, outcomeA.Frame.UV);
        Assert.Equal(b.Y, outcomeB.Frame.Y);
        Assert.Equal(b.UV, outcomeB.Frame.UV);
    }

    [Fact]
    public void Blend_nearest_and_determinism_work_correctly()
    {
        (PlanarFrame a, PlanarFrame b, _) = CreateTranslationScene(8);

        FlowOutcome blend = FlowReference.Interpolate(a, b, 0.5f, InterpolationMode.Blend);
        for (int i = 0; i < a.Y.Length; i++)
        {
            ushort expectedY = (ushort)MathF.Round((a.Y[i] + b.Y[i]) * 0.5f);
            Assert.Equal(expectedY, blend.Frame.Y[i]);
        }

        for (int i = 0; i < a.UV.Length; i++)
        {
            ushort expectedUv = (ushort)MathF.Round((a.UV[i] + b.UV[i]) * 0.5f);
            Assert.Equal(expectedUv, blend.Frame.UV[i]);
        }

        FlowOutcome nearestA = FlowReference.Interpolate(a, b, 0.3f, InterpolationMode.Nearest);
        Assert.Equal(a.Y, nearestA.Frame.Y);
        Assert.Equal(a.UV, nearestA.Frame.UV);

        FlowOutcome nearestB = FlowReference.Interpolate(a, b, 0.7f, InterpolationMode.Nearest);
        Assert.Equal(b.Y, nearestB.Frame.Y);
        Assert.Equal(b.UV, nearestB.Frame.UV);

        FlowOutcome run1 = FlowReference.Interpolate(a, b, 0.5f, InterpolationMode.OpticalFlow);
        FlowOutcome run2 = FlowReference.Interpolate(a, b, 0.5f, InterpolationMode.OpticalFlow);
        Assert.Equal(run1.Frame.Y, run2.Frame.Y);
        Assert.Equal(run1.Frame.UV, run2.Frame.UV);
        Assert.Equal(run1.HistogramDistance, run2.HistogramDistance);
        Assert.Equal(run1.Residual, run2.Residual);
        Assert.Equal(run1.SceneCut, run2.SceneCut);
    }

    [Fact]
    public void Flow_computes_dense_displacement_field()
    {
        (PlanarFrame a, PlanarFrame b, _) = CreateTranslationScene(8);
        float[] i0 = Array.ConvertAll(a.Y, y => y / 255f);
        float[] i1 = Array.ConvertAll(b.Y, y => y / 255f);

        var p = new FlowParameters();
        (float[] u, float[] v) = FlowReference.Flow(i0, i1, Width, Height, p);

        Assert.Equal(Width * Height, u.Length);
        Assert.Equal(Width * Height, v.Length);

        // En la zona interior, el desplazamiento horizontal debe ser cercano a 8 px y el vertical a 0 px.
        int interiorCount = 0;
        double sumU = 0;
        double sumV = 0;
        for (int y = 16; y < Height - 16; y++)
        {
            int row = y * Width;
            for (int x = 16; x < Width - 16; x++)
            {
                sumU += u[row + x];
                sumV += v[row + x];
                interiorCount++;
            }
        }

        double meanU = sumU / interiorCount;
        double meanV = sumV / interiorCount;
        output.WriteLine($"[Flujo denso] Media interior U: {meanU:F2} px (esperado ~8), V: {meanV:F2} px (esperado ~0)");

        Assert.InRange(meanU, 7.5, 8.5);
        Assert.InRange(meanV, -0.5, 0.5);
    }

    [Fact]
    public void PlanarFrame_validates_dimensions_and_buffers()
    {
        ushort[] validY = new ushort[320 * 180];
        ushort[] validUV = new ushort[160 * 90 * 2];

        Assert.Throws<ArgumentOutOfRangeException>(() => new PlanarFrame(0, 180, 8, validY, validUV));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlanarFrame(321, 180, 8, validY, validUV));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlanarFrame(320, 181, 8, validY, validUV));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlanarFrame(320, 180, 10, validY, validUV));
        Assert.Throws<ArgumentNullException>(() => new PlanarFrame(320, 180, 8, null!, validUV));
        Assert.Throws<ArgumentNullException>(() => new PlanarFrame(320, 180, 8, validY, null!));
        Assert.Throws<ArgumentException>(() => new PlanarFrame(320, 180, 8, new ushort[10], validUV));
        Assert.Throws<ArgumentException>(() => new PlanarFrame(320, 180, 8, validY, new ushort[10]));

        var frame = new PlanarFrame(320, 180, 8, validY, validUV);
        Assert.Equal(320, frame.Width);
        Assert.Equal(180, frame.Height);
        Assert.Equal(8, frame.BitDepth);
        Assert.Same(validY, frame.Y);
        Assert.Same(validUV, frame.UV);
    }

    private static (PlanarFrame A, PlanarFrame B, PlanarFrame Truth) CreateTranslationScene(int bitDepth)
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
                truthY[row + x] = (ushort)Math.Clamp(MathF.Round(SampleLumaTexture(x - 4, y) * maxCode), 0f, maxCode);
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
                (float uT, float vT) = SampleChromaTexture(x - 2, y);

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
}
