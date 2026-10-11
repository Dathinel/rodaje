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
    public const int GradedVariants = 16;

    // grade: x = 2^exposición, y = 1 + contraste, z = 1 + saturación, w = intensidad de la LUT.
    // balance: ganancia por canal de temperatura y tinte. tones: x = altas, y = sombras, zw = escala y desplazamiento
    // de la LUT de N³ (centros de los texeles). vignette: x = cantidad, y = tamaño, z = suavidad.
    // blendInfo: x = modo (BlendMode), yz = origen en píxeles de la copia del fondo.
    // mapA, mapB: de uv de la imagen (0–1) a uv de la textura (xy) y tope de uv (zw), porque el pool redondea tamaños.
    // kernel: xy = paso entre muestras en uv de la imagen, z = sigma, w = muestras a cada lado; en nitidez, x = cantidad.
    // mix0: x = tipo de transición, y = progreso, z = peso de la mitad 4p(1 − p). mix1: según el tipo. canvas: tamaño.
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
        };

        Texture2D<float4> background : register(t3);
        Texture3D<float4> lut : register(t4);
        Texture2D<float4> imageA : register(t5);
        Texture2D<float4> imageB : register(t6);

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
            float3 rgb = SampleRgb(i.uv);
        #if COLOR
            rgb = Grade(rgb);
        #endif
        #if LUT
            rgb = ApplyLut(rgb);
        #endif
        #if VIGNETTE
            rgb *= VignetteGain(i.local / shape.zw);
        #endif
            return Composite(i.position, rgb, shape.x * EdgeCoverage(i.local));
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

        return new ShaderVariant(Source, entryPoint, "ps_5_0", SourceName, [.. defines]);
    }
}
