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
    // esquinas): local[i].z es el w de cada esquina, que VsQuad pone en el vértice para que uv y local se interpolen con perspectiva.
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

        float3 HdrToSdr(float3 rgb)
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
            if (hdr.z > 0)
            {
                light = mul(float3x3(
                    1.660491, -0.587641, -0.072850,
                    -0.124550, 1.132900, -0.008350,
                    -0.018151, -0.100579, 1.118730), light);
            }
            light = max(light, 0);
            // Una sola ganancia conserva las proporciones RGB; el blanco elegido llega a 1.
            float peak = max(max(light.r, light.g), light.b);
            float mapped = Hable(peak / 100) / Hable(hdr.y / 100);
            light = saturate(light * (mapped / max(peak, 1e-6)));
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

            [branch] if (hdr.x > 0)
            {
                rgb = HdrToSdr(rgb);
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
