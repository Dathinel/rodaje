using Rodaje.Render.Shaders;

namespace Rodaje.Render.Scopes;

/// <summary>Compute shaders de los monitores: cuentas por columna, vector y nivel, y la imagen de cada monitor.</summary>
/// <remarks>
/// Cuentas en un búfer crudo de uint: forma de onda (4 canales R, G, B y luma × 256 niveles × <c>Columns</c>), vectorscopio
/// (256 × 256, Cb a la derecha y Cr hacia arriba) e histograma (4 × 256), más el máximo del histograma sin los extremos.
/// El histograma suma primero en memoria del grupo, porque muchos píxeles caen en el mismo nivel.
/// </remarks>
internal static class ScopeShaders
{
    private const string SourceName = "rodaje-monitores.hlsl";

    // source: ancho y alto de la imagen que se lee, paso del submuestreo y columnas de la forma de onda.
    // look: x = monitor (ScopeKind), y = ganancia del trazo, z = píxeles por columna de la forma de onda, w = píxeles en total.
    private const string Source = """
        cbuffer Scope : register(b0)
        {
            uint4 source;
            float4 look;
        };

        static const uint Columns = 512;
        static const uint WaveBase = 0;
        static const uint VectorBase = 4 * 256 * Columns;
        static const uint HistogramBase = VectorBase + 256 * 256;
        static const uint MaxSlot = HistogramBase + 1024;
        static const float VectorScale = 236.0;
        static const float3 LumaWeights = float3(0.2126, 0.7152, 0.0722);

        Texture2D<float4> image : register(t0);
        RWByteAddressBuffer counts : register(u0);
        RWTexture2D<unorm float4> output : register(u1);

        groupshared uint histogram[1024];
        groupshared uint largest[256];

        void Add(uint index, uint value)
        {
            counts.InterlockedAdd(index * 4, value);
        }

        uint Count(uint index)
        {
            return counts.Load(index * 4);
        }

        [numthreads(16, 16, 1)]
        void CsAccumulate(uint3 id : SV_DispatchThreadID, uint lane : SV_GroupIndex)
        {
            for (uint k = lane; k < 1024; k += 256)
            {
                histogram[k] = 0;
            }

            GroupMemoryBarrierWithGroupSync();
            uint2 pixel = id.xy * source.z;
            if (pixel.x < source.x && pixel.y < source.y)
            {
                float3 rgb = saturate(image.Load(int3(pixel, 0)).rgb);
                float y = dot(rgb, LumaWeights);
                uint4 level = (uint4)round(saturate(float4(rgb, y)) * 255.0);
                uint column = min(pixel.x * Columns / source.x, Columns - 1);
                Add(WaveBase + (0 * 256 + level.r) * Columns + column, 1);
                Add(WaveBase + (1 * 256 + level.g) * Columns + column, 1);
                Add(WaveBase + (2 * 256 + level.b) * Columns + column, 1);
                Add(WaveBase + (3 * 256 + level.a) * Columns + column, 1);
                float2 cbcr = float2((rgb.b - y) / 1.8556, (rgb.r - y) / 1.5748);
                int2 v = clamp((int2)round(float2(128.0 + cbcr.x * VectorScale, 128.0 - cbcr.y * VectorScale)), 0, 255);
                Add(VectorBase + v.y * 256 + v.x, 1);
                InterlockedAdd(histogram[level.r], 1);
                InterlockedAdd(histogram[256 + level.g], 1);
                InterlockedAdd(histogram[512 + level.b], 1);
                InterlockedAdd(histogram[768 + level.a], 1);
            }

            GroupMemoryBarrierWithGroupSync();
            for (uint j = lane; j < 1024; j += 256)
            {
                if (histogram[j] != 0)
                {
                    Add(HistogramBase + j, histogram[j]);
                }
            }
        }

        // El mayor nivel del histograma sin el 0 ni el 255 (los recortados no aplastan el resto).
        [numthreads(256, 1, 1)]
        void CsMaximum(uint lane : SV_GroupIndex)
        {
            uint best = 0;
            if (lane > 0 && lane < 255)
            {
                best = max(max(Count(HistogramBase + lane), Count(HistogramBase + 256 + lane)),
                    max(Count(HistogramBase + 512 + lane), Count(HistogramBase + 768 + lane)));
            }

            largest[lane] = best;
            GroupMemoryBarrierWithGroupSync();
            for (uint step = 128; step > 0; step >>= 1)
            {
                if (lane < step)
                {
                    largest[lane] = max(largest[lane], largest[lane + step]);
                }

                GroupMemoryBarrierWithGroupSync();
            }

            if (lane == 0)
            {
                counts.Store(MaxSlot * 4, max(largest[0], 1));
            }
        }

        float Glow(uint count, float norm)
        {
            return 1.0 - exp(-(float)count * look.y / max(norm, 1.0));
        }

        float WaveAt(uint channel, uint level, uint column)
        {
            return Glow(Count(WaveBase + (channel * 256 + level) * Columns + column), look.z / 256.0);
        }

        static const float3 Background = float3(0.035, 0.035, 0.035);
        static const float3 Red = float3(1.0, 0.25, 0.22);
        static const float3 Green = float3(0.3, 1.0, 0.35);
        static const float3 Blue = float3(0.3, 0.5, 1.0);

        // Imagen de 512 × 256 en BGRA (el vectorscopio ocupa los 256 × 256 de la izquierda).
        [numthreads(8, 8, 1)]
        void CsDraw(uint3 id : SV_DispatchThreadID)
        {
            uint x = id.x;
            uint row = id.y;
            if (x >= 512 || row >= 256)
            {
                return;
            }

            uint kind = (uint)look.x;
            uint level = 255 - row;
            float3 color = Background;
            if (kind == 0)
            {
                color += WaveAt(3, level, x) * float3(0.8, 1.0, 0.85);
            }
            else if (kind == 1)
            {
                color += WaveAt(0, level, x) * Red + WaveAt(1, level, x) * Green + WaveAt(2, level, x) * Blue;
            }
            else if (kind == 2)
            {
                uint panel = min(x * 3 / 512, 2);
                uint start = (panel * 512 + 2) / 3;
                uint end = ((panel + 1) * 512 + 2) / 3;
                if (x > start + 1 && x + 2 < end)
                {
                    uint column = (x - start) * Columns / (end - start);
                    float3 tint = panel == 0 ? Red : (panel == 1 ? Green : Blue);
                    color += WaveAt(panel, level, column) * tint;
                }
            }
            else if (kind == 3)
            {
                if (x < 256)
                {
                    float i = Glow(Count(VectorBase + row * 256 + x), look.w / 6000.0);
                    float2 cbcr = float2(x - 128.0, 128.0 - row) / VectorScale;
                    float3 hue = saturate(float3(0.5 + 1.5748 * cbcr.y, 0.5 - 0.1873 * cbcr.x - 0.4681 * cbcr.y, 0.5 + 1.8556 * cbcr.x));
                    color += i * lerp(float3(0.85, 0.85, 0.85), hue, 0.75);
                }
                else
                {
                    color = 0.0;
                }
            }
            else
            {
                uint bin = x / 2;
                float h = (float)level / 255.0;
                float top = (float)Count(MaxSlot);
                float r = Count(HistogramBase + bin) / top;
                float g = Count(HistogramBase + 256 + bin) / top;
                float b = Count(HistogramBase + 512 + bin) / top;
                float l = Count(HistogramBase + 768 + bin) / top;
                color += 0.55 * ((r >= h ? Red : 0.0) + (g >= h ? Green : 0.0) + (b >= h ? Blue : 0.0));
                color = abs(l - h) < 1.5 / 255.0 ? float3(0.92, 0.92, 0.92) : color;
            }

            color = saturate(color);
            output[uint2(x, row)] = float4(color.b, color.g, color.r, 1.0);
        }
        """;

    public static ShaderVariant Accumulate { get; } = new(Source, "CsAccumulate", "cs_5_0", SourceName);

    public static ShaderVariant Maximum { get; } = new(Source, "CsMaximum", "cs_5_0", SourceName);

    public static ShaderVariant Draw { get; } = new(Source, "CsDraw", "cs_5_0", SourceName);
}
