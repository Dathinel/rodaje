using Rodaje.Render.Shaders;

namespace Rodaje.Render.Raw;

/// <summary>Compute shader del revelado de Bayer (docs/hdr.md, fase D): la misma receta que <c>MosaicDeveloper</c> en la CPU.</summary>
internal static class RawShaders
{
    private const string SourceName = "rodaje-raw.hlsl";

    // size: ancho, alto, curva (0 lineal, 1 Rec.709, 2 sRGB). pattern: canal (0 R, 1 G, 2 B) en (0,0), (1,0), (0,1) y (1,1)
    // del bloque de 2×2. scale: multiplicador de cada canal dividido por el blanco (el menor multiplicador es 1) y la ganancia
    // 2^EV. row0..row2: la matriz de la cámara (tras el balance) a RGB lineal de salida, por filas.
    private const string Develop = """
        cbuffer Develop : register(b0)
        {
            uint4 size;
            uint4 pattern;
            float4 scale;
            float4 row0;
            float4 row1;
            float4 row2;
        };

        Texture2D<uint> mosaic : register(t0);
        RWTexture2D<float4> developed : register(u0);

        uint Channel(int x, int y)
        {
            uint i = ((y & 1) << 1) | (x & 1);
            return i == 0 ? pattern.x : i == 1 ? pattern.y : i == 2 ? pattern.z : pattern.w;
        }

        // Reflejado en el borde sin repetir el píxel del borde (conserva la paridad del patrón), como MosaicDeveloper.Mirror.
        int Mirror(int i, int n)
        {
            if (i < 0) i = -i;
            if (i >= n) i = 2 * n - 2 - i;
            return clamp(i, 0, n - 1);
        }

        float Scaled(int x, int y)
        {
            x = Mirror(x, (int)size.x);
            y = Mirror(y, (int)size.y);
            uint c = Channel(x, y);
            float k = c == 0 ? scale.x : c == 1 ? scale.y : scale.z;
            return min(mosaic.Load(int3(x, y, 0)) * k, 1.0);
        }

        float Encode(float v)
        {
            v = max(v, 0);
            if (size.z == 1) return v < 0.018 ? 4.5 * v : 1.099 * pow(v, 0.45) - 0.099;
            if (size.z == 2) return v <= 0.0031308 ? 12.92 * v : 1.055 * pow(v, 1.0 / 2.4) - 0.055;
            return v;
        }

        [numthreads(16, 16, 1)]
        void CsDevelop(uint3 id : SV_DispatchThreadID)
        {
            if (id.x >= size.x || id.y >= size.y) return;
            int x = (int)id.x, y = (int)id.y;
            float c = Scaled(x, y);
            float hn = Scaled(x - 1, y) + Scaled(x + 1, y);
            float vn = Scaled(x, y - 1) + Scaled(x, y + 1);
            float diag = Scaled(x - 1, y - 1) + Scaled(x + 1, y - 1) + Scaled(x - 1, y + 1) + Scaled(x + 1, y + 1);
            float h2 = Scaled(x - 2, y) + Scaled(x + 2, y);
            float v2 = Scaled(x, y - 2) + Scaled(x, y + 2);
            uint channel = Channel(x, y);
            float3 rgb;
            if (channel == 1)
            {
                float horizontal = (5 * c + 4 * hn - diag + 0.5 * v2 - h2) / 8;
                float vertical = (5 * c + 4 * vn - diag + 0.5 * h2 - v2) / 8;
                rgb = Channel(x + 1, y) == 0 ? float3(horizontal, c, vertical) : float3(vertical, c, horizontal);
            }
            else
            {
                float g = (4 * c + 2 * (hn + vn) - h2 - v2) / 8;
                float other = (6 * c + 2 * diag - 1.5 * (h2 + v2)) / 8;
                rgb = channel == 0 ? float3(c, g, other) : float3(other, g, c);
            }

            rgb = saturate(rgb);
            float3 result = saturate(float3(dot(row0.xyz, rgb), dot(row1.xyz, rgb), dot(row2.xyz, rgb))) * scale.w;
            developed[id.xy] = float4(Encode(result.r), Encode(result.g), Encode(result.b), 1);
        }
        """;

    public static ShaderVariant DevelopVariant { get; } = new(Develop, "CsDevelop", "cs_5_0", SourceName);
}
