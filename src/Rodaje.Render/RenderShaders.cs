namespace Rodaje.Render;

/// <summary>HLSL del compositor y del presentador, compilado una vez por proceso.</summary>
internal static class RenderShaders
{
    // corners[i]: xy = NDC, zw = uv; local[i].xy = punto en el espacio local de la capa; orden TL, TR, BL, BR.
    // range, toRgb y limits: YuvRange, YuvToRgb y tope de uv a medio texel del borde visible.
    // shape: x = opacidad, y = píxeles de destino por píxel local (hacia los lados verticales), zw = tamaño local;
    // present.y, si no es 0, la misma escala hacia los lados horizontales (capas estiradas o inclinadas). La cobertura del borde se
    // calcula con la distancia al lado más cercano en píxeles de destino, así un borde girado sale suavizado y uno
    // alineado con la rejilla queda nítido (los centros de sus píxeles están a 0,5 px o más del lado).
    // present: x > 0 pide muestreo con mipmaps (destino más pequeño que la fuente); z > 0, capa con perspectiva (anclaje de
    // esquinas): local[i].z es el w de cada esquina, que VsQuad pone en el vértice para que uv y local se interpolen con perspectiva;
    // w, en el presentador, cómo va la composición a la pantalla (PresentHdr).
    // hdr: x = curva de la capa (HdrTransfer), y = blanco o pico de referencia en nits (en una capa SDR, el pico de «Expandir a
    // HDR» o 0), z = primarios BT.2020, w = 1 si la secuencia es HDR (la composición es luz lineal BT.2020 con 1,0 = 203 nits;
    // QuadPipeline.HdrMode).
    /// <summary>Constantes, planos, vértices y conversión de color comunes a todos los shaders.</summary>
    public const string Prelude = """
        cbuffer Quad : register(b0)
        {
            float4 corners[4];
            float4 local[4];
            float4 range;
            float4 limits;
            float4 toRgb;
            float4 shape;
            float4 present;
            float4 pixelOrigin;
            float4 perPixelX;
            float4 perPixelY;
            float4 hdr;
        };

        Texture2DArray<float4> lumaPlane : register(t0);
        Texture2DArray<float2> chromaPlane : register(t1);
        SamplerState linearClamp : register(s0);

        struct VsOut
        {
            float4 position : SV_Position;
            float2 uv : TEXCOORD0;
            float2 local : TEXCOORD1;
        };

        float Hable(float x)
        {
            return ((x * (0.15 * x + 0.05) + 0.004) / (x * (0.15 * x + 0.5) + 0.06)) - (0.02 / 0.3);
        }

        float HlgLinear(float x)
        {
            return x <= 0.5 ? x * x / 3 : (exp((x - 0.55991073) / 0.17883277) + 0.28466892) / 12;
        }

        float Bt709(float x)
        {
            return x < 0.018 ? 4.5 * x : 1.099 * pow(x, 0.45) - 0.099;
        }

        float Bt709Linear(float x)
        {
            return x < 0.081 ? x / 4.5 : pow(max(x + 0.099, 0) / 1.099, 1.0 / 0.45);
        }

        static const float3x3 Bt2020ToBt709 = float3x3(
            1.660491, -0.587641, -0.072850,
            -0.124550, 1.132900, -0.008350,
            -0.018151, -0.100579, 1.118730);

        static const float3x3 Bt709ToBt2020 = float3x3(
            0.627404, 0.329283, 0.043313,
            0.069097, 0.919540, 0.011362,
            0.016391, 0.088013, 0.895595);

        // Señal PQ o HLG (hdr.x) → luz en nits, en los primarios de la capa.
        float3 HdrLight(float3 rgb)
        {
            float3 light;
            if (hdr.x == 1)
            {
                float3 p = pow(saturate(rgb), 1.0 / 78.84375);
                light = 10000 * pow(max(p - 0.8359375, 0) / max(18.8515625 - 18.6875 * p, 1e-6), 1.0 / 0.1593017578125);
            }
            else
            {
                light = float3(HlgLinear(rgb.r), HlgLinear(rgb.g), HlgLinear(rgb.b));
                float y = dot(light, float3(0.2627, 0.6780, 0.0593));
                // BT.2100: OOTF de luminancia, no una potencia independiente de cada canal.
                float gamma = max(1.0, 1.2 + 0.42 * log10(hdr.y / 1000));
                light *= hdr.y * pow(max(y, 1e-6), gamma - 1);
            }
            return light;
        }

        float3 HdrToSdr(float3 rgb)
        {
            float3 light = HdrLight(rgb);
            if (hdr.z > 0)
            {
                light = mul(Bt2020ToBt709, light);
            }
            light = max(light, 0);
            // Una sola ganancia conserva las proporciones RGB; el blanco elegido llega a 1.
            float peak = max(max(light.r, light.g), light.b);
            float mapped = Hable(peak / 100) / Hable(hdr.y / 100);
            light = saturate(light * (mapped / max(peak, 1e-6)));
            return float3(Bt709(light.r), Bt709(light.g), Bt709(light.b));
        }

        // Luz lineal (1,0 = 203 nits) → PQ y vuelta, para que la LUT y las curvas trabajen en una señal acotada en HDR.
        float3 LinearToPq(float3 light)
        {
            float3 y = pow(max(light, 0) * (203.0 / 10000), 0.1593017578125);
            return pow((0.8359375 + 18.8515625 * y) / (1 + 18.6875 * y), 78.84375);
        }

        float3 PqToLinear(float3 pq)
        {
            float3 p = pow(saturate(pq), 1.0 / 78.84375);
            return (10000.0 / 203) * pow(max(p - 0.8359375, 0) / max(18.8515625 - 18.6875 * p, 1e-6), 1.0 / 0.1593017578125);
        }

        // «Expandir a HDR» de una capa SDR (hdr.y = pico en nits, 203–1000): la curva inversa de BT.2446-1 método A normalizada
        // al pico y anclada en el gris del 18 %, como SdrExpansion en Rodaje.Core (la referencia). ρ del SDR de 203 nits = 7,3085.
        float ExpandNormalized(float y, float rhoHdr)
        {
            float yc = log(1 + 6.3085 * pow(y, 1.0 / 2.4)) / 1.98904;
            float yp = yc <= 0.79688 ? yc / 1.0770
                : yc < 0.99545 ? (2.7811 - sqrt(max(2.7811 * 2.7811 - 4 * 1.1510 * (0.6302 + yc), 0))) / (2 * 1.1510)
                : 2 * yc - 1;
            return pow(saturate((pow(rhoHdr, yp) - 1) / (rhoHdr - 1)), 2.4);
        }

        float3 ExpandSdr(float3 light)
        {
            float y = saturate(dot(light, float3(0.2126, 0.7152, 0.0722)));
            float rhoHdr = 1 + 32 * pow(hdr.y / 10000, 1.0 / 2.4);
            float anchor = ExpandNormalized(0.18, rhoHdr);
            float s = saturate((ExpandNormalized(y, rhoHdr) - anchor) / (1 - anchor));
            return light * (1 + (hdr.y / 203 - 1) * s);
        }

        // Secuencia HDR (hdr.w > 0): luz lineal BT.2020 con 1,0 = 203 nits (BT.2408) y sin recortar por arriba. PQ y HLG
        // por su curva, sin Hable; una capa SDR entra con el blanco en 203 nits y, solo con «Expandir a HDR», sube hasta hdr.y.
        float3 ToLinear2020(float3 rgb)
        {
            float3 light;
            [branch] if (hdr.x > 0)
            {
                light = HdrLight(rgb) * (1.0 / 203);
                light = hdr.z > 0 ? light : mul(Bt709ToBt2020, light);
            }
            else
            {
                light = float3(Bt709Linear(rgb.r), Bt709Linear(rgb.g), Bt709Linear(rgb.b));
                [branch] if (hdr.y > 0)
                {
                    light = ExpandSdr(light);
                }
                light = mul(Bt709ToBt2020, light);
            }

            return max(light, 0);
        }

        // La composición lineal BT.2020 hacia la pantalla (present.w): 1, SDR BT.709 recortando lo que pasa de 203 nits;
        // 2, scRGB (BT.709 lineal, 1,0 = 80 nits). Con 3 la composición es SDR en gamma y la pantalla scRGB.
        float3 PresentHdr(float3 rgb)
        {
            if (present.w > 2.5)
            {
                return float3(Bt709Linear(rgb.r), Bt709Linear(rgb.g), Bt709Linear(rgb.b)) * (203.0 / 80);
            }

            float3 light = mul(Bt2020ToBt709, rgb);
            if (present.w > 1.5)
            {
                return light * (203.0 / 80);
            }

            light = saturate(light);
            return float3(Bt709(light.r), Bt709(light.g), Bt709(light.b));
        }

        // WARP redondea hacia abajo la fracción del bilineal: una coordenada un pelo antes del centro de un texel toma
        // 1/256 del vecino. Se corre 1/1024 de texel (limits.xy − limits.zw es medio texel de luma), que el hardware
        // redondea a 0.
        float3 SampleRgb(float2 uv)
        {
            uv += (limits.xy - limits.zw) * (1.0 / 512.0);
            float4 pixel = lumaPlane.SampleLevel(linearClamp, float3(min(uv, limits.xy), 0), 0);
            float3 rgb = pixel.a > 0 ? pixel.rgb / pixel.a : 0;
            [branch] if (range.x >= 0)
            {
                float y = pixel.r * range.x + range.y;
                float2 c = chromaPlane.SampleLevel(linearClamp, float3(min(uv, limits.zw), 0), 0) * range.z + range.w;
                rgb = saturate(float3(
                    y + toRgb.x * c.y,
                    y - toRgb.y * c.x - toRgb.z * c.y,
                    y + toRgb.w * c.x));
            }

            [branch] if (hdr.w > 0)
            {
                rgb = ToLinear2020(rgb);
            }
            else
            {
                [branch] if (hdr.x > 0)
                {
                    rgb = HdrToSdr(rgb);
                }
            }
            return rgb;
        }

        float SourceAlpha(float2 uv)
        {
            float alpha = 1;
            [branch] if (range.x < 0)
            {
                uv += (limits.xy - limits.zw) * (1.0 / 512.0);
                alpha = lumaPlane.SampleLevel(linearClamp, float3(min(uv, limits.xy), 0), 0).a;
            }

            return alpha;
        }

        // uv y local desde SV_Position y no interpoladas: WARP recorta en el viewport el cuadrilátero que se sale y los
        // vértices nuevos llevan atributos desviados (QuadConstants.SetPixelGradients).
        VsOut Exact(VsOut i)
        {
            [branch] if (pixelOrigin.z > 0)
            {
                float2 d = i.position.xy - pixelOrigin.xy;
                float4 a = float4(corners[0].zw, local[0].xy) + perPixelX * d.x + perPixelY * d.y;
                i.uv = a.xy;
                i.local = a.zw;
            }

            return i;
        }

        float EdgeCoverage(float2 p)
        {
            float2 scale = float2(shape.y, present.y > 0 ? present.y : shape.y);
            if (present.z > 0)
            {
                // Con perspectiva la escala cambia en cada píxel: la da la derivada de la coordenada local en pantalla.
                scale = 1 / max(float2(length(float2(ddx(p.x), ddy(p.x))), length(float2(ddx(p.y), ddy(p.y)))), 1e-6);
            }

            float2 inside = min(p, shape.zw - p) * scale;
            return saturate(min(inside.x, inside.y) + 0.5);
        }

        """;

    private const string Source = Prelude + """
        Texture2D<float4> composite : register(t2);

        VsOut VsQuad(uint id : SV_VertexID)
        {
            VsOut o;
            float q = local[id].z > 0 ? local[id].z : 1;
            o.position = float4(corners[id].xy * q, 0, q);
            o.uv = corners[id].zw;
            o.local = local[id].xy;
            return o;
        }

        float4 PsLayer(VsOut i) : SV_Target
        {
            i = Exact(i);
            float3 rgb = SampleRgb(i.uv);
            float alpha = shape.x * EdgeCoverage(i.local) * SourceAlpha(i.uv);
            return float4(rgb * alpha, alpha);
        }

        float4 PsPresent(VsOut i) : SV_Target
        {
            i = Exact(i);
            float3 rgb = present.x > 0
                ? composite.Sample(linearClamp, i.uv).rgb
                : composite.SampleLevel(linearClamp, i.uv, 0).rgb;
            [branch] if (present.w > 0)
            {
                rgb = PresentHdr(rgb);
            }
            return float4(rgb, 1);
        }
        """;

    private static readonly Lazy<byte[]> s_vertex = new(() => Compile("VsQuad", "vs_5_0"));
    private static readonly Lazy<byte[]> s_layer = new(() => Compile("PsLayer", "ps_5_0"));
    private static readonly Lazy<byte[]> s_present = new(() => Compile("PsPresent", "ps_5_0"));

    public static byte[] Vertex => s_vertex.Value;

    public static byte[] Layer => s_layer.Value;

    public static byte[] Present => s_present.Value;

    private static byte[] Compile(string entryPoint, string target) =>
        ShaderCompiler.Compile(Source, entryPoint, target, "rodaje-render.hlsl");
}
