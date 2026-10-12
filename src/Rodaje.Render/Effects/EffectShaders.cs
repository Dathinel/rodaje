using System.Collections.Immutable;
using Rodaje.Render.Shaders;

namespace Rodaje.Render.Effects;

/// <summary>Pixel shaders de los efectos.</summary>
internal enum EffectShader
{
    /// <summary>YUV → RGB con color, LUT y viñeta según las banderas, y mezcla por shader si lleva <see cref="EffectShaders.Blend"/>.</summary>
    Graded,

    /// <summary>Dibuja un intermedio RGBA premultiplicado con la transformación, la opacidad y la mezcla de la capa.</summary>
    Image,

    /// <summary>Reduce a la mitad con un promedio de 2×2.</summary>
    Downsample,

    /// <summary>Una pasada del gaussiano separable.</summary>
    Blur,

    /// <summary>Máscara de enfoque: original + cantidad · (original − desenfocado).</summary>
    Sharpen,

    /// <summary>Mezcla los dos intermedios de una transición.</summary>
    Transition,

    /// <summary>Una máscara (rectángulo, elipse, trazo o imagen) multiplicada sobre la textura de máscaras de la capa.</summary>
    Mask,
}

/// <summary>Fuente HLSL de los efectos y la lista de variantes que se compilan por adelantado.</summary>
/// <remarks>
/// Todo en gamma, como la mezcla de capas (D-016). Las fórmulas son las mismas que las referencias de la CPU de las
/// pruebas; si cambia una, cambia la otra.
/// </remarks>
internal static class EffectShaders
{
    public const int Color = 1;
    public const int Lut = 2;
    public const int Vignette = 4;
    public const int Blend = 8;
    public const int Mask = 16;

    /// <summary>Croma, ruedas y curvas: cada uno se enciende con <c>extra</c> (rama uniforme) para no multiplicar las variantes.</summary>
    public const int Advanced = 32;
    public const int GradedVariants = 64;

    // grade: x = 2^exposición, y = 1 + contraste, z = 1 + saturación, w = intensidad de la LUT.
    // balance: ganancia por canal de temperatura y tinte. tones: x = altas, y = sombras, zw = escala y desplazamiento
    // de la LUT de N³ (centros de los texeles). vignette: x = cantidad, y = tamaño, z = suavidad.
    // blendInfo: x = modo (BlendMode), yz = origen en píxeles de la copia del fondo.
    // mapA, mapB: de uv de la imagen (0–1) a uv de la textura (xy) y tope de uv (zw), porque el pool redondea tamaños.
    // kernel: xy = paso entre muestras en uv de la imagen, z = sigma, w = muestras a cada lado; en nitidez, x = cantidad.
    // mix0: x = tipo de transición, y = progreso, z = peso de la mitad 4p(1 − p). mix1: según el tipo. canvas: tamaño.
    // En PsMask (MaskGeometry es la referencia): mix0 = forma, invertir, opacidad y borde suave en píxeles de la imagen;
    // mix1 = centro y medio tamaño en píxeles de la imagen; kernel = coseno y seno del giro y puntos del trazo; canvas =
    // tamaño de la imagen. En PsGraded con MASK, mapB lleva a la textura de máscaras (t7).
    // extra.w = 1: el origen no son los planos Y'CbCr sino el intermedio RGBA premultiplicado de t5 (una capa compuesta).
    // Con ADVANCED: extra = croma, ruedas y curvas encendidos (0 o 1). key0 = Cb y Cr de la clave, tolerancia, suavidad;
    // key1 = derrame, recorte del borde (0–0,45), dirección unitaria de la clave en CbCr. lift, gain: por canal (ya sumado
    // el general); gammaExp: exponente 2^−gamma por canal. Curvas: tabla de 1024 × 1 en t10 (canal y luego general).
    private const string Source = RenderShaders.Prelude + """
        cbuffer Effects : register(b1)
        {
            float4 grade;
            float4 balance;
            float4 tones;
            float4 vignette;
            float4 blendInfo;
            float4 mapA;
            float4 mapB;
            float4 kernel;
            float4 mix0;
            float4 mix1;
            float4 canvas;
            float4 key0;
            float4 key1;
            float4 lift;
            float4 gammaExp;
            float4 gain;
            float4 extra;
        };

        Texture2D<float4> background : register(t3);
        Texture3D<float4> lut : register(t4);
        Texture2D<float4> imageA : register(t5);
        Texture2D<float4> imageB : register(t6);
        Texture2D<float4> maskImage : register(t7);
        Texture2D<float2> pathPoints : register(t8);
        Texture2D<float> matteImage : register(t9);
        Texture2D<float4> curveTable : register(t10);

        static const float3 LumaWeights = float3(0.2126, 0.7152, 0.0722);

        float3 Grade(float3 rgb)
        {
            rgb *= grade.x;
            rgb = (rgb - 0.5) * grade.y + 0.5;
            float luma = dot(rgb, LumaWeights);
            rgb = luma + (rgb - luma) * grade.z;
            rgb *= balance.xyz;
            luma = dot(rgb, LumaWeights);
            float high = smoothstep(0.5, 1.0, luma);
            float low = 1.0 - smoothstep(0.0, 0.5, luma);
            rgb += 0.25 * (tones.x * high + tones.y * low);
            return saturate(rgb);
        }

        float3 ApplyLut(float3 rgb)
        {
            float3 graded = lut.SampleLevel(linearClamp, rgb * tones.z + tones.w, 0).rgb;
            return saturate(lerp(rgb, graded, grade.w));
        }

        static const float CurveSize = 1024.0;

        // Croma en Y'CbCr BT.709: alfa por la distancia de croma a la clave y derrame quitado a lo largo de su dirección,
        // conservando la luma. Devuelve el color corregido; el alfa sale en keyAlpha.
        float3 ChromaKey(float3 rgb, out float keyAlpha)
        {
            float y = dot(rgb, LumaWeights);
            float2 c = float2((rgb.b - y) / 1.8556, (rgb.r - y) / 1.5748);
            float d = 2.0 * length(c - key0.xy);
            float t = saturate((d - key0.z) / max(key0.w, 1e-3));
            float a = t * t * (3.0 - 2.0 * t);
            keyAlpha = saturate((a - key1.y) / (1.0 - 2.0 * key1.y));
            float along = dot(c, key1.zw);
            c -= key1.zw * (key1.x * max(along, 0.0));
            float r = y + 1.5748 * c.y;
            float b = y + 1.8556 * c.x;
            float g = (y - 0.2126 * r - 0.0722 * b) / 0.7152;
            return saturate(float3(r, g, b));
        }

        float3 Wheels(float3 rgb)
        {
            rgb = rgb + lift.rgb * (1.0 - rgb);
            rgb = rgb * gain.rgb;
            return saturate(pow(max(rgb, 0.0), gammaExp.rgb));
        }

        float3 Curves(float3 rgb)
        {
            float3 u = saturate(rgb) * ((CurveSize - 1.0) / CurveSize) + 0.5 / CurveSize;
            return float3(
                curveTable.SampleLevel(linearClamp, float2(u.r, 0.5), 0).r,
                curveTable.SampleLevel(linearClamp, float2(u.g, 0.5), 0).g,
                curveTable.SampleLevel(linearClamp, float2(u.b, 0.5), 0).b);
        }

        float VignetteGain(float2 uv)
        {
            float d = length((uv - 0.5) * 2.0) * 0.70710678;
            float t = saturate((d - vignette.y) / vignette.z);
            return 1.0 - vignette.x * t * t * (3.0 - 2.0 * t);
        }

        float3 SoftLightD(float3 cb)
        {
            return cb <= 0.25 ? ((16.0 * cb - 12.0) * cb + 4.0) * cb : sqrt(cb);
        }

        float3 HardLight(float3 cb, float3 cs)
        {
            return cs <= 0.5 ? cb * 2.0 * cs : 1.0 - (1.0 - cb) * (1.0 - (2.0 * cs - 1.0));
        }

        float3 BlendColor(float3 cb, float3 cs)
        {
            switch ((int)blendInfo.x)
            {
                case 4: return HardLight(cs, cb);
                case 5: return cs <= 0.5 ? cb - (1.0 - 2.0 * cs) * cb * (1.0 - cb) : cb + (2.0 * cs - 1.0) * (SoftLightD(cb) - cb);
                case 6: return HardLight(cb, cs);
                case 7: return min(cb, cs);
                case 8: return max(cb, cs);
                case 9: return abs(cb - cs);
                case 10: return cb + cs - 2.0 * cb * cs;
                case 11: return cb <= 0.0 ? 0.0 : (cs >= 1.0 ? 1.0 : min(1.0, cb / (1.0 - cs)));
                case 12: return cb >= 1.0 ? 1.0 : (cs <= 0.0 ? 0.0 : 1.0 - min(1.0, (1.0 - cb) / cs));
                default: return cs;
            }
        }

        // Color sin premultiplicar y alfa de la capa → valor premultiplicado para la mezcla "over".
        float4 Composite(float4 position, float3 cs, float alpha)
        {
        #if BLEND
            float3 cb = saturate(background.Load(int3(int2(position.xy) - int2(blendInfo.yz), 0)).rgb);
            return float4(BlendColor(cb, saturate(cs)) * alpha, alpha);
        #else
            return float4(cs * alpha, alpha);
        #endif
        }

        float4 SampleA(float2 uv)
        {
            return imageA.SampleLevel(linearClamp, min(uv * mapA.xy, mapA.zw), 0);
        }

        float4 SampleB(float2 uv)
        {
            return imageB.SampleLevel(linearClamp, min(uv * mapB.xy, mapB.zw), 0);
        }

        float4 PsGraded(VsOut i) : SV_Target
        {
            float3 rgb;
            float keyAlpha = 1.0;
            float sourceAlpha = 1.0;
            [branch] if (extra.w > 0.5)
            {
                float4 c = SampleA(i.uv);
                rgb = c.a > 0 ? c.rgb / c.a : 0;
                sourceAlpha = c.a;
            }
            else
            {
                rgb = SampleRgb(i.uv);
            }
        #if ADVANCED
            [branch] if (extra.x > 0.5)
            {
                rgb = ChromaKey(rgb, keyAlpha);
            }
        #endif
        #if COLOR
            rgb = Grade(rgb);
        #endif
        #if ADVANCED
            [branch] if (extra.y > 0.5)
            {
                rgb = Wheels(rgb);
            }

            [branch] if (extra.z > 0.5)
            {
                rgb = Curves(rgb);
            }
        #endif
        #if LUT
            rgb = ApplyLut(rgb);
        #endif
        #if VIGNETTE
            rgb *= VignetteGain(i.local / shape.zw);
        #endif
            float alpha = shape.x * EdgeCoverage(i.local) * keyAlpha * sourceAlpha;
        #if MASK
            alpha *= maskImage.SampleLevel(linearClamp, min(i.local / shape.zw * mapB.xy, mapB.zw), 0).r;
        #endif
            return Composite(i.position, rgb, alpha);
        }

        float PolygonDistance(float2 p, int count, float2 size)
        {
            float2 v0 = pathPoints.Load(int3(0, 0, 0)) * size;
            float best = dot(p - v0, p - v0);
            float s = 1.0;
            int j = count - 1;
            [loop]
            for (int k = 0; k < count; k++)
            {
                float2 vi = pathPoints.Load(int3(k, 0, 0)) * size;
                float2 vj = pathPoints.Load(int3(j, 0, 0)) * size;
                float2 e = vj - vi;
                float2 w = p - vi;
                float2 b = w - e * saturate(dot(w, e) / max(dot(e, e), 1e-12));
                best = min(best, dot(b, b));
                bool c1 = p.y >= vi.y;
                bool c2 = p.y < vj.y;
                bool c3 = e.x * w.y > e.y * w.x;
                if ((c1 && c2 && c3) || (!c1 && !c2 && !c3))
                {
                    s = -s;
                }

                j = k;
            }

            return s * sqrt(best);
        }

        float4 PsMask(VsOut i) : SV_Target
        {
            int kind = (int)mix0.x;
            float coverage;
            if (kind == 3)
            {
                coverage = matteImage.SampleLevel(linearClamp, i.local, 0);
            }
            else
            {
                float2 d = i.local * canvas.xy - mix1.xy;
                float2 l = float2(kernel.x * d.x + kernel.y * d.y, -kernel.y * d.x + kernel.x * d.y);
                float2 h = max(mix1.zw, 1e-6);
                float dist;
                if (kind == 1)
                {
                    float k1 = length(l / h);
                    float k2 = length(l / (h * h));
                    dist = k2 < 1e-30 ? -min(h.x, h.y) : k1 * (k1 - 1.0) / k2;
                }
                else if (kind == 2)
                {
                    dist = PolygonDistance(l, (int)kernel.z, 2.0 * h);
                }
                else
                {
                    float2 q = abs(l) - h;
                    dist = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0);
                }

                coverage = saturate(0.5 - dist / mix0.w);
            }

            float f = mix0.z * (mix0.y > 0.5 ? 1.0 - coverage : coverage);
            return float4(f, f, f, f);
        }

        float4 PsImage(VsOut i) : SV_Target
        {
            float4 c = SampleA(i.uv);
            float alpha = c.a * shape.x * EdgeCoverage(i.local);
            return Composite(i.position, c.a > 0 ? c.rgb / c.a : 0, alpha);
        }

        float4 PsDownsample(VsOut i) : SV_Target
        {
            return SampleA(i.uv);
        }

        float4 PsBlur(VsOut i) : SV_Target
        {
            float k = -0.5 / (kernel.z * kernel.z);
            float4 sum = SampleA(i.uv);
            float total = 1.0;
            int taps = (int)kernel.w;
            [loop]
            for (int t = 1; t <= taps; t++)
            {
                float w = exp(k * t * t);
                sum += w * (SampleA(i.uv + kernel.xy * t) + SampleA(i.uv - kernel.xy * t));
                total += 2.0 * w;
            }

            return sum / total;
        }

        float4 PsSharpen(VsOut i) : SV_Target
        {
            float4 original = SampleA(i.uv);
            float4 blurred = SampleB(i.uv);
            return saturate(original + kernel.x * (original - blurred));
        }

        float4 Side(bool b, float2 uv)
        {
            if (any(uv < 0.0) || any(uv > 1.0))
            {
                return 0;
            }

            return b ? SampleB(uv) : SampleA(uv);
        }

        float4 PsTransition(VsOut i) : SV_Target
        {
            float2 uv = i.local;
            float p = mix0.y;
            float4 m;
            switch ((int)mix0.x)
            {
                case 2:
                {
                    float4 dip = float4(mix1.rgb, 1.0);
                    m = p < 0.5
                        ? Side(false, uv) * (1.0 - 2.0 * p) + dip * (2.0 * p)
                        : dip * (1.0 - (2.0 * p - 1.0)) + Side(true, uv) * (2.0 * p - 1.0);
                    break;
                }
                case 3:
                {
                    float2 position = (uv - 0.5) * canvas.xy;
                    float extent = 0.5 * (abs(mix1.x) * canvas.x + abs(mix1.y) * canvas.y);
                    float f = (dot(position, mix1.xy) + extent) / (2.0 * extent);
                    float t = saturate((p * (1.0 + mix1.z) - f) / mix1.z);
                    m = Side(false, uv) * (1.0 - t) + Side(true, uv) * t;
                    break;
                }
                case 4:
                {
                    float4 a = Side(false, uv + mix1.xy * p);
                    float4 b = Side(true, uv - mix1.xy * (1.0 - p));
                    m = b + a * (1.0 - b.a);
                    break;
                }
                case 5:
                {
                    float2 zoomed = mix0.z > 0.0 ? 0.5 + (uv - 0.5) / (1.0 + mix1.x * mix0.z) : uv;
                    m = Side(false, zoomed) * (1.0 - p) + Side(true, zoomed) * p;
                    break;
                }
                default:
                    m = Side(false, uv) * (1.0 - p) + Side(true, uv) * p;
                    break;
            }

            return Composite(i.position, m.a > 0 ? m.rgb / m.a : 0, m.a);
        }
        """;

    private const string SourceName = "rodaje-efectos.hlsl";

    /// <summary>Todas las variantes, en el orden de <see cref="IndexOf"/>.</summary>
    public static ImmutableArray<ShaderVariant> All { get; } = CreateAll();

    public static int IndexOf(EffectShader shader, int flags) => shader switch
    {
        EffectShader.Graded => flags,
        EffectShader.Image => GradedVariants + (flags & Blend) / Blend,
        EffectShader.Transition => GradedVariants + 2 + (flags & Blend) / Blend,
        EffectShader.Downsample => GradedVariants + 4,
        EffectShader.Blur => GradedVariants + 5,
        EffectShader.Sharpen => GradedVariants + 6,
        EffectShader.Mask => GradedVariants + 7,
        _ => throw new ArgumentOutOfRangeException(nameof(shader)),
    };

    private static ImmutableArray<ShaderVariant> CreateAll()
    {
        var all = ImmutableArray.CreateBuilder<ShaderVariant>();
        for (int flags = 0; flags < GradedVariants; flags++)
        {
            all.Add(Variant("PsGraded", flags));
        }

        all.Add(Variant("PsImage", 0));
        all.Add(Variant("PsImage", Blend));
        all.Add(Variant("PsTransition", 0));
        all.Add(Variant("PsTransition", Blend));
        all.Add(Variant("PsDownsample", 0));
        all.Add(Variant("PsBlur", 0));
        all.Add(Variant("PsSharpen", 0));
        all.Add(Variant("PsMask", 0));
        return all.ToImmutable();
    }

    private static ShaderVariant Variant(string entryPoint, int flags)
    {
        var defines = new List<string>(4);
        if ((flags & Color) != 0)
        {
            defines.Add("COLOR");
        }

        if ((flags & Lut) != 0)
        {
            defines.Add("LUT");
        }

        if ((flags & Vignette) != 0)
        {
            defines.Add("VIGNETTE");
        }

        if ((flags & Blend) != 0)
        {
            defines.Add("BLEND");
        }

        if ((flags & Mask) != 0)
        {
            defines.Add("MASK");
        }

        if ((flags & Advanced) != 0)
        {
            defines.Add("ADVANCED");
        }

        return new ShaderVariant(Source, entryPoint, "ps_5_0", SourceName, [.. defines]);
    }
}
