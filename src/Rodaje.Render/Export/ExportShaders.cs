using Rodaje.Render.Shaders;

namespace Rodaje.Render.Export;

/// <summary>Compute shaders de la exportación: escala y forma, Y'CbCr por planos, miniatura y medida de luz (MaxCLL/MaxFALL).</summary>
internal static class ExportShaders
{
    private const string SourceName = "rodaje-export.hlsl";

    // rect: origen y tamaño de la composición en píxeles de salida. source: tamaño del nivel que se lee y cuánto se
    // ensancha el núcleo en cada eje. options: núcleo (ScaleKernel), mezcla de dos composiciones, tamaño de salida.
    // window: origen y tamaño de la región que se exporta (recorte), en texels del nivel leído.
    // Las coordenadas s están en texels del nivel leído, con el centro del texel en el entero.
    private const string Scale = """
        cbuffer Scale : register(b0)
        {
            float4 rect;
            float4 source;
            float4 bars;
            uint4 options;
            float4 blend;
            float4 window;
        };

        Texture2D<float4> first : register(t0);
        Texture2D<float4> second : register(t1);
        SamplerState linearClamp : register(s0);
        RWTexture2D<float4> scaled : register(u0);

        float Weight(float t)
        {
            t = abs(t);
            if (options.x == 2)
            {
                if (t < 1) return ((1.5 * t - 2.5) * t) * t + 1;
                if (t < 2) return ((-0.5 * t + 2.5) * t - 4) * t + 2;
                return 0;
            }

            if (t < 1e-5) return 1;
            if (t >= 3) return 0;
            float pt = 3.14159265 * t;
            return 3 * sin(pt) * sin(pt / 3) / (pt * pt);
        }

        float4 Filtered(Texture2D<float4> tex, float2 s)
        {
            int2 last = int2(source.xy) - 1;
            float4 sum = 0;
            float total = 1;
            if (options.x == 0)
            {
                sum = tex.Load(int3(clamp(int2(round(s)), 0, last), 0));
            }
            else if (options.x == 1)
            {
                sum = tex.SampleLevel(linearClamp, (s + 0.5) / source.xy, 0);
            }
            else
            {
                float support = options.x == 2 ? 2 : 3;
                float2 reach = support * source.zw;
                int2 lo = int2(floor(s - reach)) + 1;
                int2 hi = int2(floor(s + reach));
                total = 0;
                [loop] for (int y = lo.y; y <= hi.y; y++)
                {
                    float wy = Weight((y - s.y) / source.w);
                    [loop] for (int x = lo.x; x <= hi.x; x++)
                    {
                        float w = wy * Weight((x - s.x) / source.z);
                        sum += w * tex.Load(int3(clamp(int2(x, y), 0, last), 0));
                        total += w;
                    }
                }
            }

            return sum / total;
        }

        [numthreads(8, 8, 1)]
        void CsScale(uint3 id : SV_DispatchThreadID)
        {
            if (id.x >= options.z || id.y >= options.w) return;
            float2 p = float2(id.xy) + 0.5 - rect.xy;
            float4 c = bars;
            if (all(p >= 0) && all(p < rect.zw))
            {
                float2 s = window.xy + p / rect.zw * window.zw - 0.5;
                c = Filtered(first, s);
                if (options.y != 0) c = lerp(c, Filtered(second, s), blend.x);
            }

            scaled[id.xy] = c;
        }
        """;

    // plane: offset, pitch, unidades por fila, filas. mode: PlaneKind, código máximo, desplazamiento (P010: 6),
    // banderas (1 tramado, 2 alfa recto, 4 croma a medio ancho, 8 croma a media altura; con HDR, 16 PQ y 32 HLG, 64 fuente SDR en gamma). coef: Kr, Kb y los
    // divisores de Cb y Cr. range: escala y desplazamiento de luma y de croma, en códigos. size: tamaño del cuadro.
    // La variante HDR codifica la composición a BT.2020 con PQ o HLG, con el blanco en 203 nits (BT.2408): lineal BT.2020 de una
    // secuencia HDR (D-055) o, con la bandera 64, SDR BT.709 en gamma. La variante TONEMAP lleva la composición lineal de una
    // secuencia HDR a SDR BT.709 recortando lo que pasa de 203 nits (como el visor sin pantalla HDR). Sin ninguna definida la
    // fuente es la de siempre, así el bytecode SDR no cambia (lo comprueba ExportHdrEncodeTests).
    private const string Convert = """
        cbuffer Convert : register(b0)
        {
            uint4 plane;
            uint4 mode;
            float4 coef;
            float4 range;
            uint4 size;
        };

        Texture2D<float4> picture : register(t0);
        RWByteAddressBuffer frame : register(u0);

        static const float bayer[16] = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };

        #ifdef HDR
        float Bt709Linear(float x)
        {
            return x < 0.081 ? x / 4.5 : pow((x + 0.099) / 1.099, 1.0 / 0.45);
        }

        float HlgOetf(float x)
        {
            return x <= 1.0 / 12 ? sqrt(3 * x) : 0.17883277 * log(12 * x - 0.28466892) + 0.55991073;
        }

        // Entra luz lineal BT.2020 con 1,0 = 203 nits (o, con la bandera 64, R'G'B' BT.709 en [0, 1]); sale R'G'B' BT.2020 en PQ
        // (203 nits ≈ 0,58) o en HLG (203 nits = 75 %, pico 1000).
        float3 EncodeHdr(float3 rgb)
        {
            float3 light = rgb;
            if ((mode.w & 64) != 0)
            {
                light = mul(float3x3(
                    0.627404, 0.329283, 0.043313,
                    0.069097, 0.919540, 0.011362,
                    0.016391, 0.088013, 0.895595), float3(Bt709Linear(saturate(rgb.r)), Bt709Linear(saturate(rgb.g)), Bt709Linear(saturate(rgb.b))));
            }
            light = max(light, 0);
            if ((mode.w & 16) != 0)
            {
                float3 y = pow(light * (203.0 / 10000), 0.1593017578125);
                return pow((0.8359375 + 18.8515625 * y) / (1 + 18.6875 * y), 78.84375);
            }

            // BT.2100: OOTF inversa de luminancia (gamma 1,2 a 1000 nits) y después la OETF por canal.
            float3 display = light * (203.0 / 1000);
            float luminance = max(dot(display, float3(0.2627, 0.6780, 0.0593)), 1e-6);
            float3 scene = display * pow(luminance, 1.0 / 1.2 - 1);
            return float3(HlgOetf(scene.r), HlgOetf(scene.g), HlgOetf(scene.b));
        }
        #endif

        #ifdef TONEMAP
        float Bt709(float x)
        {
            return x < 0.018 ? 4.5 * x : 1.099 * pow(x, 0.45) - 0.099;
        }

        // Lineal BT.2020 (1,0 = 203 nits) → R'G'B' BT.709: lo que pasa de 203 nits se recorta, como PresentHdr en el visor.
        float3 ToSdr(float3 rgb)
        {
            float3 light = saturate(mul(float3x3(
                1.660491, -0.587641, -0.072850,
                -0.124550, 1.132900, -0.008350,
                -0.018151, -0.100579, 1.118730), rgb));
            return float3(Bt709(light.r), Bt709(light.g), Bt709(light.b));
        }
        #endif

        float4 Pixel(int x, int y)
        {
            float4 p = picture.Load(int3(clamp(x, 0, int(size.x) - 1), clamp(y, 0, int(size.y) - 1), 0));
            float a = saturate(p.a);
            float3 rgb = p.rgb;
            if ((mode.w & 2) != 0) rgb = a > 0 ? rgb / a : 0;
        #ifdef HDR
            return float4(EncodeHdr(rgb), a);
        #elif defined(TONEMAP)
            return float4(ToSdr(rgb), a);
        #else
            return float4(saturate(rgb), a);
        #endif
        }

        float Luma(float3 rgb)
        {
            return coef.x * rgb.r + (1 - coef.x - coef.y) * rgb.g + coef.y * rgb.b;
        }

        float2 CbCr(float3 rgb)
        {
            float y = Luma(rgb);
            return float2((rgb.b - y) * coef.z, (rgb.r - y) * coef.w);
        }

        // Croma «left»: en horizontal la muestra cae sobre la columna par y se filtra [1 2 1]/4; en vertical, entre las dos filas.
        float2 ChromaRow(int x, int y)
        {
            float2 c = CbCr(Pixel(x, y).rgb);
            if ((mode.w & 4) != 0) c = 0.25 * (CbCr(Pixel(x - 1, y).rgb) + 2 * c + CbCr(Pixel(x + 1, y).rgb));
            return c;
        }

        float2 Chroma(uint cx, uint cy)
        {
            int x = int((mode.w & 4) != 0 ? cx * 2 : cx);
            int y = int((mode.w & 8) != 0 ? cy * 2 : cy);
            float2 c = ChromaRow(x, y);
            if ((mode.w & 8) != 0) c = 0.5 * (c + ChromaRow(x, y + 1));
            return c;
        }

        float Dither(uint x, uint y)
        {
            return (mode.w & 1) != 0 ? (bayer[(y & 3) * 4 + (x & 3)] + 0.5) / 16 - 0.5 : 0;
        }

        uint Quantize(float code, float dither)
        {
            return uint(clamp(floor(code + 0.5 + dither), 0, float(mode.y)));
        }

        uint Sample(uint x, uint y)
        {
            uint kind = mode.x;
            float code;
            float dither = Dither(x, y);
            if (kind == 0)
            {
                code = Luma(Pixel(int(x), int(y)).rgb) * range.x + range.y;
            }
            else if (kind == 4)
            {
                code = Pixel(int(x), int(y)).a * float(mode.y);
                dither = 0;
            }
            else
            {
                float2 c = Chroma(x, y);
                code = (kind == 2 ? c.x : c.y) * range.z + range.w;
            }

            return Quantize(code, dither);
        }

        uint4 Rgba(uint x, uint y)
        {
            float4 p = Pixel(int(x), int(y));
            float d = Dither(x, y);
            float m = float(mode.y);
            return uint4(Quantize(p.r * m, d), Quantize(p.g * m, d), Quantize(p.b * m, d), Quantize(p.a * m, 0));
        }

        [numthreads(8, 8, 1)]
        void CsConvert(uint3 id : SV_DispatchThreadID)
        {
            if (id.x >= plane.z || id.y >= plane.w) return;
            uint kind = mode.x;
            uint row = plane.x + id.y * plane.y;
            bool wide = mode.y > 255;
            uint bits = wide ? 16 : 8;
            if (kind == 5)
            {
                uint bytes[12];
                [unroll] for (uint i = 0; i < 4; i++)
                {
                    uint4 c = Rgba(id.x * 4 + i, id.y);
                    bytes[i * 3] = c.r;
                    bytes[i * 3 + 1] = c.g;
                    bytes[i * 3 + 2] = c.b;
                }

                frame.Store3(row + id.x * 12, uint3(
                    bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24),
                    bytes[4] | (bytes[5] << 8) | (bytes[6] << 16) | (bytes[7] << 24),
                    bytes[8] | (bytes[9] << 8) | (bytes[10] << 16) | (bytes[11] << 24)));
                return;
            }

            if (kind == 6)
            {
                uint4 c = Rgba(id.x, id.y);
                frame.Store(row + id.x * 4, c.r | (c.g << 8) | (c.b << 16) | (c.a << 24));
                return;
            }

            if (kind == 7)
            {
                uint4 c = Rgba(id.x, id.y);
                frame.Store2(row + id.x * 8, uint2(c.r | (c.g << 16), c.b | (c.a << 16)));
                return;
            }

            uint word = 0;
            if (kind == 1)
            {
                uint pairs = wide ? 1 : 2;
                for (uint j = 0; j < pairs; j++)
                {
                    uint cx = id.x * pairs + j;
                    float2 c = Chroma(cx, id.y);
                    float d = Dither(cx, id.y);
                    uint cb = Quantize(c.x * range.z + range.w, d) << mode.z;
                    uint cr = Quantize(c.y * range.z + range.w, d) << mode.z;
                    word |= (cb << (j * 2 * bits)) | (cr << ((j * 2 + 1) * bits));
                }
            }
            else
            {
                uint count = wide ? 2 : 4;
                for (uint i = 0; i < count; i++)
                {
                    word |= (Sample(id.x * count + i, id.y) << mode.z) << (i * bits);
                }
            }

            frame.Store(row + id.x * 4, word);
        }
        """;

    // thumb: tamaño de la miniatura y del cuadro. Cada píxel promedia 4×4 muestras de su caja. La variante TONEMAP lee la
    // composición lineal de una secuencia HDR y la recorta a SDR BT.709 como PresentHdr (D-055); sin ella la miniatura es la de siempre.
    private const string Thumbnail = """
        cbuffer Thumb : register(b0)
        {
            uint4 thumb;
        };

        Texture2D<float4> picture : register(t0);
        RWByteAddressBuffer pixels : register(u0);

        #ifdef TONEMAP
        float Bt709(float x)
        {
            return x < 0.018 ? 4.5 * x : 1.099 * pow(x, 0.45) - 0.099;
        }

        float3 ToSdr(float3 rgb)
        {
            float3 light = saturate(mul(float3x3(
                1.660491, -0.587641, -0.072850,
                -0.124550, 1.132900, -0.008350,
                -0.018151, -0.100579, 1.118730), rgb));
            return float3(Bt709(light.r), Bt709(light.g), Bt709(light.b));
        }
        #endif

        float3 Sample(uint2 p)
        {
            float3 rgb = picture.Load(int3(p, 0)).rgb;
        #ifdef TONEMAP
            return ToSdr(rgb);
        #else
            return rgb;
        #endif
        }

        [numthreads(8, 8, 1)]
        void CsThumbnail(uint3 id : SV_DispatchThreadID)
        {
            if (id.x >= thumb.x || id.y >= thumb.y) return;
            float2 box = float2(thumb.zw) / float2(thumb.xy);
            float3 sum = 0;
            [unroll] for (int j = 0; j < 4; j++)
            {
                [unroll] for (int i = 0; i < 4; i++)
                {
                    float2 p = (float2(id.xy) + (float2(i, j) + 0.5) / 4) * box;
                    sum += Sample(min(uint2(p), thumb.zw - 1));
                }
            }

            uint3 c = uint3(saturate(sum / 16) * 255 + 0.5);
            pixels.Store((id.y * thumb.x + id.x) * 4, c.b | (c.g << 8) | (c.r << 16) | 0xFF000000u);
        }
        """;

    // light: ancho y alto de la composición y teselas por fila. Cada grupo reduce su tesela de 16×16 a dos floats (máximo y
    // suma de max(R, G, B) en luz lineal, 1,0 = 203 nits); la CPU junta las teselas (ContentLightMeter).
    private const string Light = """
        cbuffer Light : register(b0)
        {
            uint4 light;
        };

        Texture2D<float4> picture : register(t0);
        RWByteAddressBuffer tiles : register(u0);

        groupshared float peaks[256];
        groupshared float sums[256];

        [numthreads(16, 16, 1)]
        void CsLight(uint3 id : SV_DispatchThreadID, uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
        {
            float peak = 0;
            if (id.x < light.x && id.y < light.y)
            {
                float3 rgb = picture.Load(int3(id.xy, 0)).rgb;
                peak = max(max(max(rgb.r, rgb.g), rgb.b), 0);
            }

            peaks[lane] = peak;
            sums[lane] = peak;
            GroupMemoryBarrierWithGroupSync();
            for (uint step = 128; step > 0; step >>= 1)
            {
                if (lane < step)
                {
                    peaks[lane] = max(peaks[lane], peaks[lane + step]);
                    sums[lane] += sums[lane + step];
                }

                GroupMemoryBarrierWithGroupSync();
            }

            if (lane == 0)
            {
                tiles.Store2((group.y * light.z + group.x) * 8, uint2(asuint(peaks[0]), asuint(sums[0])));
            }
        }
        """;

    public static ShaderVariant ScaleVariant { get; } = new(Scale, "CsScale", "cs_5_0", SourceName);

    public static ShaderVariant ConvertVariant { get; } = new(Convert, "CsConvert", "cs_5_0", SourceName);

    public static ShaderVariant ConvertHdrVariant { get; } = new(Convert, "CsConvert", "cs_5_0", SourceName, "HDR");

    public static ShaderVariant ConvertToneMapVariant { get; } = new(Convert, "CsConvert", "cs_5_0", SourceName, "TONEMAP");

    public static ShaderVariant ThumbnailVariant { get; } = new(Thumbnail, "CsThumbnail", "cs_5_0", SourceName);

    public static ShaderVariant ThumbnailToneMapVariant { get; } = new(Thumbnail, "CsThumbnail", "cs_5_0", SourceName, "TONEMAP");

    public static ShaderVariant LightVariant { get; } = new(Light, "CsLight", "cs_5_0", SourceName);
}
