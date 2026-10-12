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

    /// <summary>Una pasada de los efectos espaciales nuevos (<see cref="FxOp"/>): grano, resplandor, aberración, espejo…</summary>
    Fx,
}

/// <summary>Qué hace <c>PsFx</c> (<c>fx0.x</c>).</summary>
internal enum FxOp
{
    Grain = 1,
    GlowBright = 2,
    GlowCombine = 3,
    Aberration = 4,
    Mirror = 5,
    Distortion = 6,
    LineBlur = 7,
    ZoomBlur = 8,
    LensDisc = 9,
    DenoiseSpatial = 10,
    DenoiseTemporal = 11,
    ToLinear = 12,
    ToGamma = 13,
    RegionMix = 14,
    Copy = 15,
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
    // skin.x: protección de piel (0 apagada). vignette2: x = redondez, yz = centro.
    // qual0: tono central y medio ancho (en vueltas), suavidad del tono, invertir. qual1: saturación baja y alta, luma baja
    // y alta. qual2: suavidad de saturación y luma, coseno y seno del giro de tono, ganancia de saturación. qual3: x =
    // 2^exposición, y = ver la máscara (solo vista previa), z = calificador encendido, w = curvas de tono y saturación.
    // fx0, fx1, fx2: PsFx (FxOp en fx0.x); en RegionMix, fx2 es el mapa de la máscara de efectos (t7).
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
            float4 skin;
            float4 vignette2;
            float4 qual0;
            float4 qual1;
            float4 qual2;
            float4 qual3;
            float4 fx0;
            float4 fx1;
            float4 fx2;
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

        float2 ToCbCr(float3 rgb, float y)
        {
            return float2((rgb.b - y) / 1.8556, (rgb.r - y) / 1.5748);
        }

        float3 FromYcc(float y, float2 c)
        {
            float r = y + 1.5748 * c.y;
            float b = y + 1.8556 * c.x;
            return float3(r, (y - 0.2126 * r - 0.0722 * b) / 0.7152, b);
        }

        // Tono de HSV de 0 a 1 (0 rojo, 1/3 verde, 2/3 azul); 0 en los grises.
        float Hue(float3 c)
        {
            float high = max(c.r, max(c.g, c.b));
            float d = high - min(c.r, min(c.g, c.b));
            if (d < 1e-5)
            {
                return 0.0;
            }

            float h = high == c.r ? (c.g - c.b) / d : (high == c.g ? 2.0 + (c.b - c.r) / d : 4.0 + (c.r - c.g) / d);
            return frac(h / 6.0 + 1.0);
        }

        float3 GradeWith(float3 rgb, float saturation, float3 gains)
        {
            rgb *= grade.x;
            rgb = (rgb - 0.5) * grade.y + 0.5;
            float luma = dot(rgb, LumaWeights);
            rgb = luma + (rgb - luma) * saturation;
            rgb *= gains;
            luma = dot(rgb, LumaWeights);
            float high = smoothstep(0.5, 1.0, luma);
            float low = 1.0 - smoothstep(0.0, 0.5, luma);
            rgb += 0.25 * (tones.x * high + tones.y * low);
            return saturate(rgb);
        }

        // Cerca de la línea de piel del vectorscopio (123° en CbCr), con algo de croma y sin llegar a lo muy saturado.
        float SkinWeight(float3 rgb)
        {
            float y = dot(rgb, LumaWeights);
            float2 c = ToCbCr(rgb, y);
            float r = length(c);
            float angle = degrees(atan2(c.y, c.x));
            return (1.0 - smoothstep(12.0, 28.0, abs(angle - 123.0))) * smoothstep(0.015, 0.05, r) * (1.0 - smoothstep(0.3, 0.45, r));
        }

        float3 Grade(float3 rgb)
        {
            float3 graded = GradeWith(rgb, grade.z, balance.xyz);
            [branch] if (skin.x > 0.0)
            {
                graded = lerp(graded, GradeWith(rgb, 1.0, float3(1.0, 1.0, 1.0)), skin.x * SkinWeight(rgb));
            }

            return graded;
        }

        // Calificador HSL: máscara por tono (HSV), saturación (HSL) y luma (BT.709) con bordes suaves; corrige en Y'CbCr.
        float QualifierMask(float3 rgb)
        {
            float high = max(rgb.r, max(rgb.g, rgb.b));
            float low = min(rgb.r, min(rgb.g, rgb.b));
            float l = 0.5 * (high + low);
            float s = saturate((high - low) / max(1.0 - abs(2.0 * l - 1.0), 1e-4));
            float y = dot(rgb, LumaWeights);
            float dh = abs(frac(Hue(rgb) - qual0.x + 0.5) - 0.5);
            float hueTerm = 1.0 - smoothstep(qual0.y, qual0.y + max(qual0.z, 1e-4), dh);
            float soft = max(qual2.x, 1e-4);
            float satTerm = smoothstep(qual1.x - soft, qual1.x, s) * (1.0 - smoothstep(qual1.y, qual1.y + soft, s));
            float lumaTerm = smoothstep(qual1.z - soft, qual1.z, y) * (1.0 - smoothstep(qual1.w, qual1.w + soft, y));
            float m = hueTerm * satTerm * lumaTerm;
            return qual0.w > 0.5 ? 1.0 - m : m;
        }

        float3 Qualify(float3 rgb, float m)
        {
            float y = dot(rgb, LumaWeights);
            float2 c = ToCbCr(rgb, y);
            c = float2(c.x * qual2.y - c.y * qual2.z, c.x * qual2.z + c.y * qual2.y) * qual2.w;
            return lerp(rgb, saturate(FromYcc(y, c) * qual3.x), m);
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

        // Fila 0 (v = 0,25): curvas de tono por canal. Fila 1 (v = 0,75): tono contra tono (r), tono contra saturación (g)
        // y luma contra saturación (b), con 0,5 sin cambio.
        float3 Curves(float3 rgb)
        {
            float3 u = saturate(rgb) * ((CurveSize - 1.0) / CurveSize) + 0.5 / CurveSize;
            rgb = float3(
                curveTable.SampleLevel(linearClamp, float2(u.r, 0.25), 0).r,
                curveTable.SampleLevel(linearClamp, float2(u.g, 0.25), 0).g,
                curveTable.SampleLevel(linearClamp, float2(u.b, 0.25), 0).b);
            [branch] if (qual3.w > 0.5)
            {
                float y = dot(rgb, LumaWeights);
                float uh = Hue(rgb) * ((CurveSize - 1.0) / CurveSize) + 0.5 / CurveSize;
                float uy = saturate(y) * ((CurveSize - 1.0) / CurveSize) + 0.5 / CurveSize;
                float2 byHue = curveTable.SampleLevel(linearClamp, float2(uh, 0.75), 0).rg;
                float byLuma = curveTable.SampleLevel(linearClamp, float2(uy, 0.75), 0).b;
                float shift = (byHue.x - 0.5) * 6.28318531;
                float2 c = ToCbCr(rgb, y);
                c = float2(c.x * cos(shift) - c.y * sin(shift), c.x * sin(shift) + c.y * cos(shift)) * (4.0 * byHue.y * byLuma);
                rgb = saturate(FromYcc(y, c));
            }

            return rgb;
        }

        // Con redondez 0 y el centro en el medio es la elipse del cuadro (d = 1 en las esquinas); con 1, un círculo en píxeles.
        float VignetteGain(float2 uv)
        {
            float sx = lerp(1.0, shape.z / shape.w, vignette2.x);
            float2 p = (uv - vignette2.yz) * 2.0 * float2(sx, 1.0);
            float d = length(p) / length(float2(sx, 1.0));
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

            float qualified = 0.0;
            [branch] if (qual3.z > 0.5)
            {
                qualified = QualifierMask(rgb);
                rgb = Qualify(rgb, qualified);
            }
        #endif
        #if LUT
            rgb = ApplyLut(rgb);
        #endif
        #if VIGNETTE
            rgb *= VignetteGain(i.local / shape.zw);
        #endif
        #if ADVANCED
            [branch] if (qual3.y > 0.5)
            {
                rgb = qualified;
            }
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

        // kernel.y: umbral; las diferencias de luma más chicas no se realzan (rampa suave hasta el doble del umbral).
        float4 PsSharpen(VsOut i) : SV_Target
        {
            float4 original = SampleA(i.uv);
            float4 blurred = SampleB(i.uv);
            float4 detail = original - blurred;
            float k = kernel.y > 0.0 ? smoothstep(kernel.y, 2.0 * kernel.y, abs(dot(detail.rgb, LumaWeights))) : 1.0;
            return saturate(original + kernel.x * k * detail);
        }

        float4 Unpremultiply(float4 c)
        {
            return float4(c.a > 0.0 ? c.rgb / c.a : 0.0, c.a);
        }

        // Luz lineal aproximada con gamma 2,2 (lo óptico: resplandor y desenfoque de lente).
        float3 ToLinear(float3 c)
        {
            return pow(max(c, 0.0), 2.2);
        }

        float3 ToGamma(float3 c)
        {
            return pow(max(c, 0.0), 1.0 / 2.2);
        }

        uint Hash(uint x)
        {
            x ^= x >> 16;
            x *= 0x7feb352du;
            x ^= x >> 15;
            x *= 0x846ca68bu;
            x ^= x >> 16;
            return x;
        }

        float Noise(int2 cell, uint seed, uint channel)
        {
            uint h = Hash((asuint(cell.x) * 0x9e3779b9u) ^ Hash(asuint(cell.y) + 0x85ebca6bu * (seed + channel * 0x632be5abu)));
            return (h >> 8) * (1.0 / 16777216.0);
        }

        // Ruido de valor interpolado (media 0) en celdas del tamaño del grano.
        float ValueNoise(float2 p, uint seed, uint channel)
        {
            float2 f = frac(p);
            int2 c = int2(floor(p));
            f = f * f * (3.0 - 2.0 * f);
            float a = Noise(c, seed, channel);
            float b = Noise(c + int2(1, 0), seed, channel);
            float d = Noise(c + int2(0, 1), seed, channel);
            float e = Noise(c + int2(1, 1), seed, channel);
            return lerp(lerp(a, b, f.x), lerp(d, e, f.x), f.y) - 0.5;
        }

        // fx0: cantidad, tamaño en píxeles de la secuencia, color; fx1: respuesta, segundos del origen, píxeles del
        // intermedio por píxel de la secuencia.
        float4 Grain(float2 uv, float2 pixel)
        {
            float4 c = Unpremultiply(SampleA(uv));
            float2 p = pixel / fx1.z / fx0.z;
            uint seed = Hash(asuint(fx1.y));
            float mono = ValueNoise(p, seed, 0u);
            float3 colored = float3(ValueNoise(p, seed, 1u), ValueNoise(p, seed, 2u), ValueNoise(p, seed, 3u));
            float y = dot(c.rgb, LumaWeights);
            float weight = lerp(1.0, 4.0 * y * (1.0 - y), fx1.x);
            float3 n = lerp(mono.xxx, colored, fx0.w);
            return float4(saturate(c.rgb + fx0.y * 0.7 * weight * n) * c.a, c.a);
        }

        // Bilateral de 5 × 5: fx0.y = fuerza, fx0.zw = paso de un píxel en uv, fx1.x = 1/(2σ²) espacial, fx1.y = 1/(2σ²) de luma.
        float4 Bilateral(float2 uv)
        {
            float4 center = SampleA(uv);
            float yc = dot(Unpremultiply(center).rgb, LumaWeights);
            float4 sum = 0;
            float total = 0;
            [unroll]
            for (int dy = -2; dy <= 2; dy++)
            {
                [unroll]
                for (int dx = -2; dx <= 2; dx++)
                {
                    float4 s = SampleA(uv + float2(dx, dy) * fx0.zw);
                    float dl = dot(Unpremultiply(s).rgb, LumaWeights) - yc;
                    float w = exp(-(dx * dx + dy * dy) * fx1.x - dl * dl * fx1.y);
                    sum += w * s;
                    total += w;
                }
            }

            return lerp(center, sum / total, fx0.y);
        }

        // Pasadas de los efectos espaciales sobre intermedios premultiplicados: t5 = A, t6 = B, t7 = máscara de efectos.
        float4 PsFx(VsOut i) : SV_Target
        {
            float2 uv = i.uv;
            switch ((int)fx0.x)
            {
                case 1:
                    return Grain(uv, i.position.xy);
                case 2:
                {
                    // Lo que pasa del umbral, en luz lineal, con una rampa cuadrática hasta el blanco.
                    float4 c = Unpremultiply(SampleA(uv));
                    float bright = max(c.r, max(c.g, c.b));
                    float k = saturate((bright - fx0.y) / max(1.0 - fx0.y, 1e-3));
                    return float4(ToLinear(c.rgb) * k * k * c.a, c.a);
                }
                case 3:
                {
                    float4 c = Unpremultiply(SampleA(uv));
                    float3 halo = SampleB(uv).rgb;
                    return float4(saturate(ToGamma(ToLinear(c.rgb) + fx0.y * halo)) * c.a, c.a);
                }
                case 4:
                {
                    // fx0.y: 0 radial (fx0.z = separación en la esquina, relativa), 1 lateral (fx0.zw = desplazamiento en uv).
                    float2 d = uv - 0.5;
                    float2 red = fx0.y > 0.5 ? uv + fx0.zw : 0.5 + d * (1.0 + fx0.z);
                    float2 blue = fx0.y > 0.5 ? uv - fx0.zw : 0.5 + d * (1.0 - fx0.z);
                    float4 g = SampleA(uv);
                    return float4(SampleA(red).r, g.g, SampleA(blue).b, g.a);
                }
                case 5:
                {
                    // fx0.y = modo (EffectRegistry.MirrorModes), fx0.z = eje, fx0.w = copias por lado.
                    float2 m = uv;
                    int mode = (int)fx0.y;
                    float axis = fx0.z;
                    if ((mode == 0 || mode == 4) && m.x > axis) m.x = 2.0 * axis - m.x;
                    if (mode == 1 && m.x < axis) m.x = 2.0 * axis - m.x;
                    if ((mode == 2 || mode == 4) && m.y > axis) m.y = 2.0 * axis - m.y;
                    if (mode == 3 && m.y < axis) m.y = 2.0 * axis - m.y;
                    if (mode == 5) m = frac(uv * fx0.w);
                    return SampleA(saturate(m));
                }
                case 6:
                {
                    // fx0.y = k (barril positivo), fx0.z = acercamiento, fx0.w = ancho/alto. r² vale 1 en la esquina.
                    float2 d = (uv - 0.5) * float2(fx0.w, 1.0);
                    float r2 = dot(d, d) * 4.0 / (fx0.w * fx0.w + 1.0);
                    float2 source = 0.5 + d * (1.0 + fx0.y * r2) / fx0.z / float2(fx0.w, 1.0);
                    return any(source < 0.0) || any(source > 1.0) ? 0.0 : SampleA(source);
                }
                case 7:
                {
                    // Caja a lo largo de fx0.yz (paso por muestra), fx0.w muestras a cada lado.
                    float4 sum = 0;
                    int taps = (int)fx0.w;
                    [loop]
                    for (int t = -taps; t <= taps; t++)
                    {
                        sum += SampleA(uv + fx0.yz * t);
                    }

                    return sum / (2 * taps + 1);
                }
                case 8:
                {
                    // Zoom desde el centro fx0.yz: cada muestra escala la distancia en fx0.w; fx1.x muestras a cada lado.
                    float4 sum = 0;
                    int taps = (int)fx1.x;
                    [loop]
                    for (int t = -taps; t <= taps; t++)
                    {
                        sum += SampleA(fx0.yz + (uv - fx0.yz) * (1.0 + fx0.w * t));
                    }

                    return sum / (2 * taps + 1);
                }
                case 9:
                {
                    // Disco de radio fx0.yz (en uv) con la espiral de Vogel: fx1.x muestras repartidas por igual en el área.
                    float4 sum = 0;
                    int taps = (int)fx1.x;
                    [loop]
                    for (int t = 0; t < taps; t++)
                    {
                        float r = sqrt((t + 0.5) / taps);
                        float a = t * 2.39996323;
                        sum += SampleA(uv + float2(cos(a), sin(a)) * r * fx0.yz);
                    }

                    return sum / taps;
                }
                case 10:
                    return Bilateral(uv);
                case 11:
                {
                    // Con el cuadro anterior (B) donde la luma casi no cambió: fx0.y = mezcla, fx0.zw = rampa de la diferencia.
                    float4 now = SampleA(uv);
                    float4 before = SampleB(uv);
                    float diff = abs(dot(Unpremultiply(now).rgb - Unpremultiply(before).rgb, LumaWeights));
                    return lerp(now, before, fx0.y * (1.0 - smoothstep(fx0.z, fx0.w, diff)));
                }
                case 12:
                {
                    float4 c = Unpremultiply(SampleA(uv));
                    return float4(ToLinear(c.rgb) * c.a, c.a);
                }
                case 13:
                {
                    float4 c = Unpremultiply(SampleA(uv));
                    return float4(ToGamma(c.rgb) * c.a, c.a);
                }
                case 14:
                {
                    // Antes (A) y después (B) según la máscara de efectos; fx0.y = 1 aplica fuera de ella.
                    float m = maskImage.SampleLevel(linearClamp, min(uv * fx2.xy, fx2.zw), 0).r;
                    return lerp(SampleA(uv), SampleB(uv), fx0.y > 0.5 ? 1.0 - m : m);
                }
                default:
                    return SampleA(uv);
            }
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
        EffectShader.Fx => GradedVariants + 8,
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
        all.Add(Variant("PsFx", 0));
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
