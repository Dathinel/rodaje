namespace Rodaje.Render.Tests.Support;

/// <summary>
/// Shader de conversión de la exportación anterior a la fase C del HDR, congelado: la variante SDR de hoy debe compilar al
/// mismo bytecode, así un video SDR no cambia ni un bit (<c>ExportHdrEncodeTests</c>).
/// </summary>
internal static class LegacyExportShaders
{
    public const string Convert = """
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

        float4 Pixel(int x, int y)
        {
            float4 p = picture.Load(int3(clamp(x, 0, int(size.x) - 1), clamp(y, 0, int(size.y) - 1), 0));
            float a = saturate(p.a);
            float3 rgb = p.rgb;
            if ((mode.w & 2) != 0) rgb = a > 0 ? rgb / a : 0;
            return float4(saturate(rgb), a);
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
}
