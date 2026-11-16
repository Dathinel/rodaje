namespace Rodaje.Analysis.Video.Interpolation;

/// <summary>
/// Referencia determinista en CPU del interpolador de cuadros y flujo óptico DIS.
/// </summary>
public static class FlowReference
{
    public static FlowOutcome Interpolate(PlanarFrame a, PlanarFrame b, float t, InterpolationMode mode, FlowParameters? p = null)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.Width != b.Width || a.Height != b.Height || a.BitDepth != b.BitDepth)
        {
            throw new ArgumentException("Los cuadros deben tener las mismas dimensiones y profundidad de bits.");
        }

        p ??= new FlowParameters();

        if (mode == InterpolationMode.Nearest)
        {
            PlanarFrame chosen = t < 0.5f ? a : b;
            return new FlowOutcome(new PlanarFrame(a.Width, a.Height, a.BitDepth, chosen.Y.ToArray(), chosen.UV.ToArray()), false, 0f, 0f);
        }

        if (mode == InterpolationMode.Blend)
        {
            ushort[] blendedY = BlendPlane(a.Y, b.Y, t, a.BitDepth);
            ushort[] blendedUv = BlendPlane(a.UV, b.UV, t, a.BitDepth);
            return new FlowOutcome(new PlanarFrame(a.Width, a.Height, a.BitDepth, blendedY, blendedUv), false, 0f, 0f);
        }

        int width = a.Width;
        int height = a.Height;
        int n = width * height;
        float maxCode = a.BitDepth == 8 ? 255f : 65535f;
        float invMax = 1f / maxCode;

        float[] i0 = new float[n];
        float[] i1 = new float[n];
        for (int i = 0; i < n; i++)
        {
            i0[i] = a.Y[i] * invMax;
            i1[i] = b.Y[i] * invMax;
        }

        float histogramDistance = ComputeHistogramDistance(i0, i1);

        (float[] f01U, float[] f01V, float residual, _, _) = ComputeDenseFlow(i0, i1, width, height, p);

        bool sceneCut = histogramDistance > p.CutHistogram && residual > p.CutResidual;
        if (sceneCut)
        {
            PlanarFrame chosen = t < 0.5f ? a : b;
            return new FlowOutcome(new PlanarFrame(width, height, a.BitDepth, chosen.Y.ToArray(), chosen.UV.ToArray()), true, histogramDistance, residual);
        }

        (float[] f10U, float[] f10V, _, _, _) = ComputeDenseFlow(i1, i0, width, height, p);

        bool[] occ0 = ComputeOcclusion(f01U, f01V, f10U, f10V, width, height, p.Alpha, p.Beta);
        bool[] occ1 = ComputeOcclusion(f10U, f10V, f01U, f01V, width, height, p.Alpha, p.Beta);

        float ft0W01 = -(1f - t) * t;
        float ft0W10 = t * t;
        float ft1W01 = (1f - t) * (1f - t);
        float ft1W10 = -t * (1f - t);

        float[] ft0U = new float[n];
        float[] ft0V = new float[n];
        float[] ft1U = new float[n];
        float[] ft1V = new float[n];

        for (int i = 0; i < n; i++)
        {
            ft0U[i] = (ft0W01 * f01U[i]) + (ft0W10 * f10U[i]);
            ft0V[i] = (ft0W01 * f01V[i]) + (ft0W10 * f10V[i]);
            ft1U[i] = (ft1W01 * f01U[i]) + (ft1W10 * f10U[i]);
            ft1V[i] = (ft1W01 * f01V[i]) + (ft1W10 * f10V[i]);
        }

        ushort[] outY = new ushort[n];
        bool[] valid0 = new bool[n];
        bool[] valid1 = new bool[n];
        WarpAndBlendLuma(a.Y, b.Y, outY, ft0U, ft0V, ft1U, ft1V, occ0, occ1, valid0, valid1, width, height, t, maxCode);

        int uvW = width / 2;
        int uvH = height / 2;
        ushort[] outUV = new ushort[uvW * uvH * 2];
        WarpAndBlendChroma(a.UV, b.UV, outUV, ft0U, ft0V, ft1U, ft1V, valid0, valid1, width, height, uvW, uvH, t, maxCode);

        return new FlowOutcome(new PlanarFrame(width, height, a.BitDepth, outY, outUV), false, histogramDistance, residual);
    }

    public static (float[] U, float[] V) Flow(float[] i0, float[] i1, int width, int height, FlowParameters p)
    {
        ArgumentNullException.ThrowIfNull(i0);
        ArgumentNullException.ThrowIfNull(i1);
        ArgumentNullException.ThrowIfNull(p);

        (float[] u, float[] v, _, _, _) = ComputeDenseFlow(i0, i1, width, height, p);
        return (u, v);
    }

    private static ushort[] BlendPlane(ushort[] a, ushort[] b, float t, int bitDepth)
    {
        float maxCode = bitDepth == 8 ? 255f : 65535f;
        ushort[] output = new ushort[a.Length];
        float w0 = 1f - t;
        float w1 = t;
        for (int i = 0; i < a.Length; i++)
        {
            output[i] = (ushort)Math.Clamp(MathF.Round((w0 * a[i]) + (w1 * b[i])), 0f, maxCode);
        }

        return output;
    }

    private static float ComputeHistogramDistance(float[] i0, float[] i1)
    {
        float[] hA = new float[64];
        float[] hB = new float[64];
        float invN = 1f / i0.Length;

        for (int i = 0; i < i0.Length; i++)
        {
            int binA = Math.Clamp((int)(i0[i] * 64f), 0, 63);
            int binB = Math.Clamp((int)(i1[i] * 64f), 0, 63);
            hA[binA] += invN;
            hB[binB] += invN;
        }

        float sumDiff = 0f;
        for (int k = 0; k < 64; k++)
        {
            sumDiff += MathF.Abs(hA[k] - hB[k]);
        }

        return 0.5f * sumDiff;
    }

    private static (float[] FullU, float[] FullV, float Residual, float[] LevelU, float[] LevelV) ComputeDenseFlow(
        float[] i0, float[] i1, int width, int height, FlowParameters p)
    {
        List<int> widths = [width];
        List<int> heights = [height];
        int cw = width;
        int ch = height;
        while (true)
        {
            int nw = cw / 2;
            int nh = ch / 2;
            if (widths.Count > p.FlowLevel && Math.Min(nw, nh) < 16)
            {
                break;
            }

            if (nw == 0 || nh == 0)
            {
                break;
            }

            widths.Add(nw);
            heights.Add(nh);
            cw = nw;
            ch = nh;
        }

        int coarsestLevel = Math.Max(p.FlowLevel, widths.Count - 1);

        float[][] pyr0 = new float[coarsestLevel + 1][];
        float[][] pyr1 = new float[coarsestLevel + 1][];
        pyr0[0] = i0;
        pyr1[0] = i1;

        for (int l = 1; l <= coarsestLevel; l++)
        {
            pyr0[l] = Downsample2x2(pyr0[l - 1], widths[l - 1], heights[l - 1], widths[l], heights[l]);
            pyr1[l] = Downsample2x2(pyr1[l - 1], widths[l - 1], heights[l - 1], widths[l], heights[l]);
        }

        float[]? prevU = null;
        float[]? prevV = null;
        int prevW = 0;
        int prevH = 0;

        float[] currU = [];
        float[] currV = [];
        int lw = 0;
        int lh = 0;

        for (int l = coarsestLevel; l >= p.FlowLevel; l--)
        {
            lw = widths[l];
            lh = heights[l];
            float[] levelI0 = pyr0[l];
            float[] levelI1 = pyr1[l];

            ComputeGradients(levelI0, lw, lh, out float[] gx, out float[] gy);

            int[] patchX = GeneratePatchCoords(lw, p.PatchSize, p.PatchStride);
            int[] patchY = GeneratePatchCoords(lh, p.PatchSize, p.PatchStride);

            float[] patchFlowU = new float[patchX.Length * patchY.Length];
            float[] patchFlowV = new float[patchX.Length * patchY.Length];

            Parallel.For(0, patchY.Length, pyIdx =>
            {
                int py = patchY[pyIdx];
                int pRow = pyIdx * patchX.Length;
                Span<float> e = stackalloc float[64];

                for (int pxIdx = 0; pxIdx < patchX.Length; pxIdx++)
                {
                    int px = patchX[pxIdx];
                    float uInit = 0f;
                    float vInit = 0f;

                    if (prevU is not null && prevV is not null)
                    {
                        float cx = (px + 3.5f) * 0.5f;
                        float cy = (py + 3.5f) * 0.5f;
                        uInit = 2f * SampleBilinearClamped(prevU, prevW, prevH, cx, cy);
                        vInit = 2f * SampleBilinearClamped(prevV, prevW, prevH, cx, cy);
                    }

                    float h00 = 0f;
                    float h01 = 0f;
                    float h11 = 0f;
                    for (int dy = 0; dy < 8; dy++)
                    {
                        int gRow = ((py + dy) * lw) + px;
                        for (int dx = 0; dx < 8; dx++)
                        {
                            float ix = gx[gRow + dx];
                            float iy = gy[gRow + dx];
                            h00 += ix * ix;
                            h01 += ix * iy;
                            h11 += iy * iy;
                        }
                    }

                    h00 += p.DiagonalRegularization;
                    h11 += p.DiagonalRegularization;
                    float det = (h00 * h11) - (h01 * h01);
                    float invDet = 1f / det;
                    float invH00 = h11 * invDet;
                    float invH01 = -h01 * invDet;
                    float invH11 = h00 * invDet;

                    float u = uInit;
                    float v = vInit;
                    float ssdInit = ComputePatchSsd(levelI0, levelI1, lw, lh, px, py, u, v);

                    for (int it = 0; it < p.Iterations; it++)
                    {
                        float meanE = 0f;
                        int k = 0;
                        for (int dy = 0; dy < 8; dy++)
                        {
                            int y0 = py + dy;
                            int row0 = (y0 * lw) + px;
                            float sampleY = y0 + v;
                            for (int dx = 0; dx < 8; dx++)
                            {
                                int x0 = px + dx;
                                float sampleX = x0 + u;
                                float err = SampleBilinearClamped(levelI1, lw, lh, sampleX, sampleY) - levelI0[row0 + dx];
                                e[k++] = err;
                                meanE += err;
                            }
                        }

                        meanE *= 1f / 64f;

                        float bx = 0f;
                        float by = 0f;
                        k = 0;
                        for (int dy = 0; dy < 8; dy++)
                        {
                            int row0 = ((py + dy) * lw) + px;
                            for (int dx = 0; dx < 8; dx++)
                            {
                                float diff = e[k++] - meanE;
                                bx += gx[row0 + dx] * diff;
                                by += gy[row0 + dx] * diff;
                            }
                        }

                        float du = (invH00 * bx) + (invH01 * by);
                        float dv = (invH01 * bx) + (invH11 * by);

                        u -= du;
                        v -= dv;

                        if ((du * du) + (dv * dv) < p.DeltaThreshold * p.DeltaThreshold)
                        {
                            break;
                        }
                    }

                    float ssdFinal = ComputePatchSsd(levelI0, levelI1, lw, lh, px, py, u, v);
                    if (ssdFinal > ssdInit)
                    {
                        u = uInit;
                        v = vInit;
                    }

                    patchFlowU[pRow + pxIdx] = u;
                    patchFlowV[pRow + pxIdx] = v;
                }
            });

            int[][] patchesForX = PrecomputeCoveringPatches(lw, patchX);
            int[][] patchesForY = PrecomputeCoveringPatches(lh, patchY);

            currU = new float[lw * lh];
            currV = new float[lw * lh];

            Parallel.For(0, lh, y =>
            {
                int[] pyIndices = patchesForY[y];
                int row = y * lw;
                for (int x = 0; x < lw; x++)
                {
                    int[] pxIndices = patchesForX[x];
                    float sumW = 0f;
                    float sumU = 0f;
                    float sumV = 0f;
                    float i0Val = levelI0[row + x];

                    for (int ji = 0; ji < pyIndices.Length; ji++)
                    {
                        int pyIdx = pyIndices[ji];
                        int pRow = pyIdx * patchX.Length;
                        for (int ii = 0; ii < pxIndices.Length; ii++)
                        {
                            int pxIdx = pxIndices[ii];
                            int pIdx = pRow + pxIdx;
                            float pu = patchFlowU[pIdx];
                            float pv = patchFlowV[pIdx];
                            float s = SampleBilinearClamped(levelI1, lw, lh, x + pu, y + pv);
                            float diff = MathF.Abs(s - i0Val);
                            float w = 1f / MathF.Max(0.01f, diff);
                            sumW += w;
                            sumU += w * pu;
                            sumV += w * pv;
                        }
                    }

                    float invW = 1f / sumW;
                    currU[row + x] = sumU * invW;
                    currV[row + x] = sumV * invW;
                }
            });

            prevU = currU;
            prevV = currV;
            prevW = lw;
            prevH = lh;
        }

        float[] flowLevelI0 = pyr0[p.FlowLevel];
        float[] flowLevelI1 = pyr1[p.FlowLevel];
        float sumRes = 0f;
        int nLevel = lw * lh;
        for (int y = 0; y < lh; y++)
        {
            int row = y * lw;
            for (int x = 0; x < lw; x++)
            {
                float u = currU[row + x];
                float v = currV[row + x];
                float s = SampleBilinearClamped(flowLevelI1, lw, lh, x + u, y + v);
                sumRes += MathF.Abs(s - flowLevelI0[row + x]);
            }
        }

        float residual = sumRes / nLevel;

        float scale = 1 << p.FlowLevel;
        float invScale = 1f / scale;
        float[] fullU = new float[width * height];
        float[] fullV = new float[width * height];

        Parallel.For(0, height, y =>
        {
            int row = y * width;
            float cy = y * invScale;
            for (int x = 0; x < width; x++)
            {
                float cx = x * invScale;
                fullU[row + x] = SampleBilinearClamped(currU, lw, lh, cx, cy) * scale;
                fullV[row + x] = SampleBilinearClamped(currV, lw, lh, cx, cy) * scale;
            }
        });

        return (fullU, fullV, residual, currU, currV);
    }

    private static float[] Downsample2x2(float[] src, int srcW, int srcH, int dstW, int dstH)
    {
        float[] dst = new float[dstW * dstH];
        for (int y = 0; y < dstH; y++)
        {
            int srcY = 2 * y;
            int r0 = srcY * srcW;
            int r1 = (srcY + 1) * srcW;
            int dstRow = y * dstW;
            for (int x = 0; x < dstW; x++)
            {
                int srcX = 2 * x;
                dst[dstRow + x] = 0.25f * (src[r0 + srcX] + src[r0 + srcX + 1] + src[r1 + srcX] + src[r1 + srcX + 1]);
            }
        }

        return dst;
    }

    private static void ComputeGradients(float[] img, int width, int height, out float[] gx, out float[] gy)
    {
        gx = new float[width * height];
        gy = new float[width * height];

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            int rowAbove = (y > 0 ? y - 1 : 0) * width;
            int rowBelow = (y < height - 1 ? y + 1 : height - 1) * width;

            for (int x = 0; x < width; x++)
            {
                int idx = row + x;
                if (width == 1)
                {
                    gx[idx] = 0f;
                }
                else if (x == 0)
                {
                    gx[idx] = img[row + 1] - img[row];
                }
                else if (x == width - 1)
                {
                    gx[idx] = img[row + width - 1] - img[row + width - 2];
                }
                else
                {
                    gx[idx] = 0.5f * (img[row + x + 1] - img[row + x - 1]);
                }

                if (height == 1)
                {
                    gy[idx] = 0f;
                }
                else if (y == 0)
                {
                    gy[idx] = img[rowBelow + x] - img[row + x];
                }
                else if (y == height - 1)
                {
                    gy[idx] = img[row + x] - img[rowAbove + x];
                }
                else
                {
                    gy[idx] = 0.5f * (img[rowBelow + x] - img[rowAbove + x]);
                }
            }
        }
    }

    private static int[] GeneratePatchCoords(int length, int size, int stride)
    {
        List<int> coords = [];
        for (int c = 0; c + size <= length; c += stride)
        {
            coords.Add(c);
        }

        if (coords.Count == 0 || coords[^1] != length - size)
        {
            coords.Add(Math.Max(0, length - size));
        }

        return coords.ToArray();
    }

    private static int[][] PrecomputeCoveringPatches(int length, int[] patchCoords)
    {
        int[][] result = new int[length][];
        List<int> buffer = [];
        for (int c = 0; c < length; c++)
        {
            buffer.Clear();
            for (int pIdx = 0; pIdx < patchCoords.Length; pIdx++)
            {
                int px = patchCoords[pIdx];
                if (px <= c && c < px + 8)
                {
                    buffer.Add(pIdx);
                }
            }

            result[c] = buffer.ToArray();
        }

        return result;
    }

    private static float ComputePatchSsd(float[] i0, float[] i1, int width, int height, int px, int py, float u, float v)
    {
        Span<float> e = stackalloc float[64];
        float meanE = 0f;
        int k = 0;
        for (int dy = 0; dy < 8; dy++)
        {
            int y0 = py + dy;
            int row0 = (y0 * width) + px;
            float sampleY = y0 + v;
            for (int dx = 0; dx < 8; dx++)
            {
                int x0 = px + dx;
                float sampleX = x0 + u;
                float err = SampleBilinearClamped(i1, width, height, sampleX, sampleY) - i0[row0 + dx];
                e[k++] = err;
                meanE += err;
            }
        }

        meanE *= 1f / 64f;
        float ssd = 0f;
        for (k = 0; k < 64; k++)
        {
            float diff = e[k] - meanE;
            ssd += diff * diff;
        }

        return ssd;
    }

    private static bool[] ComputeOcclusion(float[] f01U, float[] f01V, float[] f10U, float[] f10V, int width, int height, float alpha, float beta)
    {
        bool[] occ = new bool[width * height];
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int idx = row + x;
                float u = f01U[idx];
                float v = f01V[idx];
                float tx = x + u;
                float ty = y + v;

                if (tx < 0f || tx > width - 1f || ty < 0f || ty > height - 1f)
                {
                    occ[idx] = true;
                    continue;
                }

                float wu = SampleBilinearClamped(f10U, width, height, tx, ty);
                float wv = SampleBilinearClamped(f10V, width, height, tx, ty);

                float sumU = u + wu;
                float sumV = v + wv;
                float sumSq = (sumU * sumU) + (sumV * sumV);
                float f01Sq = (u * u) + (v * v);
                float wSq = (wu * wu) + (wv * wv);

                occ[idx] = sumSq > (alpha * (f01Sq + wSq)) + beta;
            }
        }

        return occ;
    }

    private static void WarpAndBlendLuma(
        ushort[] a, ushort[] b, ushort[] outY,
        float[] ft0U, float[] ft0V, float[] ft1U, float[] ft1V,
        bool[] occ0, bool[] occ1,
        bool[] valid0, bool[] valid1,
        int width, int height, float t, float maxCode)
    {
        Parallel.For(0, height, y =>
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int idx = row + x;
                float p0x = x + ft0U[idx];
                float p0y = y + ft0V[idx];
                float p1x = x + ft1U[idx];
                float p1y = y + ft1V[idx];

                bool out0 = p0x < 0f || p0x > width - 1f || p0y < 0f || p0y > height - 1f;
                bool out1 = p1x < 0f || p1x > width - 1f || p1y < 0f || p1y > height - 1f;

                int nx0 = Math.Clamp((int)MathF.Round(p0x), 0, width - 1);
                int ny0 = Math.Clamp((int)MathF.Round(p0y), 0, height - 1);
                bool e0 = occ0[(ny0 * width) + nx0];

                int nx1 = Math.Clamp((int)MathF.Round(p1x), 0, width - 1);
                int ny1 = Math.Clamp((int)MathF.Round(p1y), 0, height - 1);
                bool e1 = occ1[(ny1 * width) + nx1];

                bool v0 = !out0 && !(e1 && !e0);
                bool v1 = !out1 && !(e0 && !e1);
                valid0[idx] = v0;
                valid1[idx] = v1;

                float s0 = SampleBilinearClamped(a, width, height, p0x, p0y);
                float s1 = SampleBilinearClamped(b, width, height, p1x, p1y);

                float w0, w1;
                if ((v0 && v1) || (!v0 && !v1))
                {
                    w0 = 1f - t;
                    w1 = t;
                }
                else if (v0)
                {
                    w0 = 1f;
                    w1 = 0f;
                }
                else
                {
                    w0 = 0f;
                    w1 = 1f;
                }

                outY[idx] = (ushort)Math.Clamp(MathF.Round((w0 * s0) + (w1 * s1)), 0f, maxCode);
            }
        });
    }

    private static void WarpAndBlendChroma(
        ushort[] aUV, ushort[] bUV, ushort[] outUV,
        float[] ft0U, float[] ft0V, float[] ft1U, float[] ft1V,
        bool[] lumaValid0, bool[] lumaValid1,
        int lumaW, int lumaH, int uvW, int uvH, float t, float maxCode)
    {
        Parallel.For(0, uvH, uvY =>
        {
            int uvRow = uvY * uvW;
            int ly0 = 2 * uvY;
            int ly1 = Math.Min(ly0 + 1, lumaH - 1);
            int lr0 = ly0 * lumaW;
            int lr1 = ly1 * lumaW;

            for (int uvX = 0; uvX < uvW; uvX++)
            {
                int lx0 = 2 * uvX;
                int lx1 = Math.Min(lx0 + 1, lumaW - 1);

                float avgFt0U = 0.25f * (ft0U[lr0 + lx0] + ft0U[lr0 + lx1] + ft0U[lr1 + lx0] + ft0U[lr1 + lx1]) * 0.5f;
                float avgFt0V = 0.25f * (ft0V[lr0 + lx0] + ft0V[lr0 + lx1] + ft0V[lr1 + lx0] + ft0V[lr1 + lx1]) * 0.5f;
                float avgFt1U = 0.25f * (ft1U[lr0 + lx0] + ft1U[lr0 + lx1] + ft1U[lr1 + lx0] + ft1U[lr1 + lx1]) * 0.5f;
                float avgFt1V = 0.25f * (ft1V[lr0 + lx0] + ft1V[lr0 + lx1] + ft1V[lr1 + lx0] + ft1V[lr1 + lx1]) * 0.5f;

                int lumaIdx = lr0 + lx0;
                bool v0 = lumaValid0[lumaIdx];
                bool v1 = lumaValid1[lumaIdx];

                float w0, w1;
                if ((v0 && v1) || (!v0 && !v1))
                {
                    w0 = 1f - t;
                    w1 = t;
                }
                else if (v0)
                {
                    w0 = 1f;
                    w1 = 0f;
                }
                else
                {
                    w0 = 0f;
                    w1 = 1f;
                }

                float p0x = uvX + avgFt0U;
                float p0y = uvY + avgFt0V;
                float p1x = uvX + avgFt1U;
                float p1y = uvY + avgFt1V;

                (float u0, float v0Samp) = SampleBilinearUVClamped(aUV, uvW, uvH, p0x, p0y);
                (float u1, float v1Samp) = SampleBilinearUVClamped(bUV, uvW, uvH, p1x, p1y);

                int outIdx = (uvRow + uvX) * 2;
                outUV[outIdx] = (ushort)Math.Clamp(MathF.Round((w0 * u0) + (w1 * u1)), 0f, maxCode);
                outUV[outIdx + 1] = (ushort)Math.Clamp(MathF.Round((w0 * v0Samp) + (w1 * v1Samp)), 0f, maxCode);
            }
        });
    }

    private static float SampleBilinearClamped(float[] image, int width, int height, float x, float y)
    {
        float cx = Math.Clamp(x, 0f, width - 1f);
        float cy = Math.Clamp(y, 0f, height - 1f);
        int x0 = (int)cx;
        int y0 = (int)cy;
        int x1 = Math.Min(x0 + 1, width - 1);
        int y1 = Math.Min(y0 + 1, height - 1);
        float fx = cx - x0;
        float fy = cy - y0;

        int r0 = y0 * width;
        int r1 = y1 * width;
        float v00 = image[r0 + x0];
        float v10 = image[r0 + x1];
        float v01 = image[r1 + x0];
        float v11 = image[r1 + x1];

        float top = v00 + (fx * (v10 - v00));
        float bottom = v01 + (fx * (v11 - v01));
        return top + (fy * (bottom - top));
    }

    private static float SampleBilinearClamped(ushort[] image, int width, int height, float x, float y)
    {
        float cx = Math.Clamp(x, 0f, width - 1f);
        float cy = Math.Clamp(y, 0f, height - 1f);
        int x0 = (int)cx;
        int y0 = (int)cy;
        int x1 = Math.Min(x0 + 1, width - 1);
        int y1 = Math.Min(y0 + 1, height - 1);
        float fx = cx - x0;
        float fy = cy - y0;

        int r0 = y0 * width;
        int r1 = y1 * width;
        float v00 = image[r0 + x0];
        float v10 = image[r0 + x1];
        float v01 = image[r1 + x0];
        float v11 = image[r1 + x1];

        float top = v00 + (fx * (v10 - v00));
        float bottom = v01 + (fx * (v11 - v01));
        return top + (fy * (bottom - top));
    }

    private static (float U, float V) SampleBilinearUVClamped(ushort[] uv, int width, int height, float x, float y)
    {
        float cx = Math.Clamp(x, 0f, width - 1f);
        float cy = Math.Clamp(y, 0f, height - 1f);
        int x0 = (int)cx;
        int y0 = (int)cy;
        int x1 = Math.Min(x0 + 1, width - 1);
        int y1 = Math.Min(y0 + 1, height - 1);
        float fx = cx - x0;
        float fy = cy - y0;

        int i00 = ((y0 * width) + x0) * 2;
        int i10 = ((y0 * width) + x1) * 2;
        int i01 = ((y1 * width) + x0) * 2;
        int i11 = ((y1 * width) + x1) * 2;

        float u00 = uv[i00];
        float u10 = uv[i10];
        float u01 = uv[i01];
        float u11 = uv[i11];
        float topU = u00 + (fx * (u10 - u00));
        float botU = u01 + (fx * (u11 - u01));
        float u = topU + (fy * (botU - topU));

        float v00 = uv[i00 + 1];
        float v10 = uv[i10 + 1];
        float v01 = uv[i01 + 1];
        float v11 = uv[i11 + 1];
        float topV = v00 + (fx * (v10 - v00));
        float botV = v01 + (fx * (v11 - v01));
        float v = topV + (fy * (botV - topV));

        return (u, v);
    }
}
