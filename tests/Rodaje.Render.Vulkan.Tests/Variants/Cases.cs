
using static Rodaje.Render.Tests.Variants.CaseVariants;

namespace Rodaje.Render.Tests.Variants;

/// <summary>
/// Los casos de comparación: las 77 variantes con constantes genéricas no triviales y, además, una pasada por cada operación de
/// <c>PsFx</c>, cada tipo de transición y de máscara y cada modo de mezcla por shader. Las constantes quedan escritas en
/// casos.json (float32 exactos); las texturas, como fórmula de <see cref="Patterns"/>.
/// </summary>
internal static class Cases
{
    public const int Size = 64;
    private const int QuadFloats = 17 * 4, EffectFloats = 26 * 4;

    // Índices de float4 en cbuffer Effects.
    private const int Grade = 0, Balance = 1, Tones = 2, VignetteRow = 3, BlendInfo = 4, MapA = 5, MapB = 6, Kernel = 7, Mix0 = 8,
        Mix1 = 9, Canvas = 10, Key0 = 11, Key1 = 12, Lift = 13, GammaExp = 14, Gain = 15, Extra = 16, Skin = 17, Vignette2 = 18,
        Qual0 = 19, Qual1 = 20, Qual2 = 21, Qual3 = 22, Fx0 = 23, Fx1 = 24, Fx2 = 25;

    private const int LutSize = 17;
    private const float HalfTexel = 0.5f / Size;

    public static TextureSpec[] TextureSet() =>
    [
        Tex("luma", "r16f", "2d-array", Size, Size, "suave", 1),
        Tex("croma", "rg16f", "2d-array", Size / 2, Size / 2, "suave", 2),
        Tex("croma444", "rg16f", "2d-array", Size, Size, "suave", 2),
        Tex("lumaRgba", "rgba16f", "2d-array", Size, Size, "premultiplicada", 11),
        Tex("fondo", "rgba16f", "2d", Size, Size, "suave", 3),
        Tex("lut", "rgba16f", "3d", LutSize, LutSize, "lut", 0, LutSize),
        Tex("imagenA", "rgba16f", "2d", Size, Size, "premultiplicada", 5),
        Tex("imagenA128", "rgba16f", "2d", 2 * Size, 2 * Size, "premultiplicada", 5),
        Tex("imagenB", "rgba16f", "2d", Size, Size, "premultiplicada", 6),
        Tex("mascara", "rgba16f", "2d", Size, Size, "suave", 7),
        WithHash(new TextureSpec
        {
            Name = "puntos", Format = "rgba32f", Dimension = "2d", Width = 8, Height = 1, Pattern = "literal",
            Data =
            [
                -0.45f, -0.30f, 1.0f, 0, 0.10f, -0.48f, 1.3f, 0, 0.47f, -0.10f, 0.8f, 0, 0.35f, 0.40f, 1.1f, 0,
                -0.05f, 0.46f, 0.9f, 0, -0.42f, 0.20f, 1.2f, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            ],
        }),
        Tex("mate", "r16f", "2d", Size, Size, "suave", 9),
        Tex("curvas", "rgba16f", "2d", 1024, 2, "curvas", 0),
        Tex("gruesa", "r32f", "2d", 8, 8, "distancia", 0),
    ];

    private static TextureSpec Tex(string name, string format, string dim, int w, int h, string pattern, int seed, int depth = 1) =>
        WithHash(new TextureSpec { Name = name, Format = format, Dimension = dim, Width = w, Height = h, Depth = depth, Pattern = pattern, Seed = seed });

    private static TextureSpec WithHash(TextureSpec t) => t with { Hash = Patterns.Hash(Patterns.Bytes(t)) };

    private static Dictionary<string, string> Bindings(params (string Register, string Texture)[] overrides)
    {
        var map = new Dictionary<string, string>
        {
            ["t0"] = "luma", ["t1"] = "croma", ["t3"] = "fondo", ["t4"] = "lut", ["t5"] = "imagenA", ["t6"] = "imagenB",
            ["t7"] = "mascara", ["t8"] = "puntos", ["t9"] = "mate", ["t10"] = "curvas", ["t11"] = "gruesa",
        };
        foreach (var (r, t) in overrides)
        {
            map[r] = t;
        }

        return map;
    }

    /// <summary>cbuffer Quad de una capa que llena el objetivo; <paramref name="local"/> es el tamaño local (píxeles o 1).</summary>
    private static float[] Quad(float local, float opacity = 1f, bool yuv = true, bool exact = true, float angle = 0f, float scale = 1f)
    {
        var q = new float[QuadFloats];
        float c = MathF.Cos(angle) * scale, s = MathF.Sin(angle) * scale;
        (float X, float Y)[] ndc = [(-1, 1), (1, 1), (-1, -1), (1, -1)];
        (float U, float V)[] uv = [(0, 0), (1, 0), (0, 1), (1, 1)];
        for (int k = 0; k < 4; k++)
        {
            Set(q, k, (c * ndc[k].X) - (s * ndc[k].Y), (s * ndc[k].X) + (c * ndc[k].Y), uv[k].U, uv[k].V);
            Set(q, 4 + k, uv[k].U * local, uv[k].V * local, 0, 0);
        }

        Set(q, 8, yuv ? 255f / 219f : -1f, yuv ? -16f / 219f : 0f, 255f / 224f, -128f / 224f);
        Set(q, 9, 1f - HalfTexel, 1f - HalfTexel, 1f - (0.5f / (Size / 2)), 1f - (0.5f / (Size / 2)));
        Set(q, 10, 1.5748f, 0.1873f, 0.4681f, 1.8556f);
        Set(q, 11, opacity, scale * Size / local, local, local);
        if (exact)
        {
            PixelGradients(q);
        }

        return q;
    }

    /// <summary>Como <c>QuadConstants.SetPixelGradients</c> (QuadPipeline.cs) para un paralelogramo.</summary>
    private static void PixelGradients(float[] q)
    {
        (double X, double Y) Pixel(int k) => ((q[k * 4] + 1) * 0.5 * Size, (1 - q[(k * 4) + 1]) * 0.5 * Size);
        (double x0, double y0) = Pixel(0);
        (double x1, double y1) = Pixel(1);
        (double x2, double y2) = Pixel(2);
        double ax = x1 - x0, ay = y1 - y0, bx = x2 - x0, by = y2 - y0;
        double det = (ax * by) - (ay * bx);
        double[] a0 = [q[2], q[3], q[16], q[17]];
        double[] a1 = [q[6], q[7], q[20], q[21]];
        double[] a2 = [q[10], q[11], q[24], q[25]];
        for (int k = 0; k < 4; k++)
        {
            double d1 = a1[k] - a0[k], d2 = a2[k] - a0[k];
            q[(14 * 4) + k] = (float)(((d1 * by) - (d2 * ay)) / det);
            q[(15 * 4) + k] = (float)(((d2 * ax) - (d1 * bx)) / det);
        }

        Set(q, 13, (float)x0, (float)y0, 1, 0);
    }

    private static void Set(float[] a, int row, float x, float y, float z, float w)
    {
        a[row * 4] = x;
        a[(row * 4) + 1] = y;
        a[(row * 4) + 2] = z;
        a[(row * 4) + 3] = w;
    }

    private static float[] Effects() 
    {
        var e = new float[EffectFloats];
        Set(e, MapA, 1, 1, 1 - HalfTexel, 1 - HalfTexel);
        Set(e, MapB, 1, 1, 1 - HalfTexel, 1 - HalfTexel);
        Set(e, Canvas, Size, Size, 0, 0);
        return e;
    }

    /// <summary>Constantes de PsGraded con todo encendido: color, piel, LUT, viñeta, croma, ruedas, curvas y calificador.</summary>
    private static float[] GradedEffects()
    {
        float[] e = Effects();
        Set(e, Grade, 1.15f, 1.1f, 1.2f, 0.7f);
        Set(e, Balance, 1.05f, 1.0f, 0.94f, 0f);
        Set(e, Tones, 0.1f, -0.05f, (LutSize - 1f) / LutSize, 0.5f / LutSize);
        Set(e, VignetteRow, 0.6f, 0.35f, 0.5f, 0f);
        Set(e, BlendInfo, 5, 0, 0, 0);
        Set(e, Key0, -0.1f, -0.15f, 0.2f, 0.15f);
        Set(e, Key1, 0.5f, 0.05f, -0.5547002f, -0.8320503f);
        Set(e, Lift, 0.02f, 0f, -0.01f, 0.01f);
        Set(e, GammaExp, 1.1f, 0.95f, 1.0f, 0f);
        Set(e, Gain, 1.05f, 1f, 0.97f, -0.01f);
        Set(e, Extra, 1, 1, 1, 0);
        Set(e, Skin, 0.4f, 0, 0, 0);
        Set(e, Vignette2, 0.3f, 0.5f, 0.45f, 0);
        Set(e, Qual0, 0.08f, 0.15f, 0.1f, 0f);
        Set(e, Qual1, 0.1f, 0.9f, 0.1f, 0.9f);
        Set(e, Qual2, 0.1f, 0.9553365f, 0.2955202f, 1.2f);
        Set(e, Qual3, 1.1f, 0f, 1f, 1f);
        return e;
    }

    private static CaseSpec Case(string name, int variant, string description, float[] quad, float[] effects, Dictionary<string, string>? textures = null) =>
        new() { Name = name, Variant = variant, Description = description, Quad = quad, Effects = effects, Textures = textures ?? Bindings() };

    private static string Flags(int flags)
    {
        var names = new List<string>();
        foreach ((int bit, string n) in new[] { (Color, "COLOR"), (Lut, "LUT"), (Vignette, "VIGNETTE"), (Blend, "BLEND"), (Mask, "MASK"), (Advanced, "ADVANCED"), (Motion, "MOTION") })
        {
            if ((flags & bit) != 0)
            {
                names.Add(n);
            }
        }

        return names.Count == 0 ? "0" : string.Join('+', names);
    }

    public static CaseSpec[] All()
    {
        var list = new List<CaseSpec>();

        // PsGraded: las 64 variantes con YUV 4:2:0 de rango limitado y la capa en píxeles (opacidad 0,9).
        for (int flags = 0; flags < GradedVariants; flags++)
        {
            list.Add(Case($"graded-{Flags(flags)}", IndexOf(EffectShader.Graded, flags), "YUV 4:2:0 limitado, constantes genéricas", Quad(Size, 0.9f), GradedEffects()));
        }

        int all = Color | Lut | Vignette | Advanced;
        float[] e = GradedEffects();
        Set(e, Extra, 1, 1, 1, 1);
        list.Add(Case("graded-COLOR+LUT-intermedio", IndexOf(EffectShader.Graded, Color | Lut), "origen RGBA premultiplicado de t5 (extra.w = 1)", Quad(Size), e));
        list.Add(Case("graded-COLOR+VIGNETTE-plano-rgba", IndexOf(EffectShader.Graded, Color | Vignette), "plano RGBA (range.x < 0) con alfa de la fuente",
            Quad(Size, yuv: false), GradedEffects(), Bindings(("t0", "lumaRgba"))));
        // ADVANCED por partes, para ver cuál separa a WARP de Vulkan.
        foreach (var (name, x, y, z, qualifier, hueCurves) in new[]
                 {
                     ("solo-croma", 1, 0, 0, 0, 0), ("solo-ruedas", 0, 1, 0, 0, 0), ("solo-curvas", 0, 0, 1, 0, 0),
                     ("solo-curvas-tono", 0, 0, 1, 0, 1), ("solo-calificador", 0, 0, 0, 1, 0),
                 })
        {
            e = GradedEffects();
            Set(e, Extra, x, y, z, 0);
            Set(e, Qual3, 1.1f, 0f, qualifier, hueCurves);
            list.Add(Case($"graded-ADVANCED-{name}", IndexOf(EffectShader.Graded, Advanced), "ADVANCED con una sola parte encendida", Quad(Size, 0.9f), e));
            // Lo mismo desde un plano RGBA leído en los centros de texel (el bilineal no interpola): separa el filtro de la aritmética.
            list.Add(Case($"graded-ADVANCED-{name}-centros", IndexOf(EffectShader.Graded, Advanced), "la misma parte desde un plano RGBA en centros de texel",
                Quad(Size, 0.9f, yuv: false), e, Bindings(("t0", "lumaRgba"))));
        }

        list.Add(Case("graded-ADVANCED-centros", IndexOf(EffectShader.Graded, Advanced), "ADVANCED entero desde un plano RGBA en centros de texel",
            Quad(Size, 0.9f, yuv: false), GradedEffects(), Bindings(("t0", "lumaRgba"))));
        e = GradedEffects();
        Set(e, Extra, 1, 0, 0, 0);
        Set(e, Qual3, 1.1f, 0f, 0f, 0f);
        float[] quad444 = Quad(Size, 0.9f);
        Set(quad444, 9, 1f - HalfTexel, 1f - HalfTexel, 1f - HalfTexel, 1f - HalfTexel);
        list.Add(Case("graded-ADVANCED-solo-croma-444", IndexOf(EffectShader.Graded, Advanced), "croma desde YUV 4:4:4 (la croma también en centros de texel)",
            quad444, e, Bindings(("t1", "croma444"))));

        e = GradedEffects();
        Set(e, Qual3, 1.1f, 1f, 1f, 1f);
        list.Add(Case("graded-ADVANCED-vista-calificador", IndexOf(EffectShader.Graded, all), "qual3.y = 1: muestra la máscara del calificador", Quad(Size), e));
        for (int view = 1; view <= 2; view++)
        {
            e = GradedEffects();
            Set(e, BlendInfo, 5, 0, 0, view);
            list.Add(Case($"graded-COLOR+BLEND+MASK-vista{view}", IndexOf(EffectShader.Graded, Color | Blend | Mask), "vista de la máscara (blendInfo.w)", Quad(Size), e));
        }

        foreach (int flags in new[] { Color | Lut | Vignette, all })
        {
            list.Add(Case($"graded-{Flags(flags)}-girado", IndexOf(EffectShader.Graded, flags), "capa girada 20° y al 70 %: bordes suavizados, uv exactas",
                Quad(Size, 0.9f, angle: 0.3490658f, scale: 0.7f), GradedEffects()));
            list.Add(Case($"graded-{Flags(flags)}-interpolado", IndexOf(EffectShader.Graded, flags), "girada sin pixelOrigin: uv y local interpoladas por el rasterizador",
                Quad(Size, 0.9f, exact: false, angle: 0.3490658f, scale: 0.7f), GradedEffects()));
            float[] hdrQuad = Quad(Size, 0.9f);
            Set(hdrQuad, 16, 0, 0, 0, 1);
            list.Add(Case($"graded-{Flags(flags)}-hdr", IndexOf(EffectShader.Graded, flags), "secuencia HDR (hdr.w = 1): luz lineal BT.2020, LUT (y curvas) en PQ",
                hdrQuad, GradedEffects()));
        }

        // PsImage, con y sin mezcla por shader (todos los modos).
        list.Add(Case("image-0", IndexOf(EffectShader.Image, 0), "intermedio premultiplicado, opacidad 0,85", Quad(Size, 0.85f), Effects()));
        foreach (int mode in new[] { 0, 4, 5, 6, 7, 8, 9, 10, 11, 12 })
        {
            e = Effects();
            Set(e, BlendInfo, mode, 0, 0, 0);
            list.Add(Case($"image-BLEND-modo{mode}", IndexOf(EffectShader.Image, Blend), $"mezcla por shader, modo {mode}", Quad(Size, 0.85f), e));
        }

        // PsTransition: cada tipo a p = 0,45; local de 0 a 1.
        foreach (var (name, kind, set) in Transitions())
        {
            e = Effects();
            Set(e, Mix0, kind, 0.45f, 4f * 0.45f * 0.55f, 0);
            set(e);
            list.Add(Case($"transition-0-{name}", IndexOf(EffectShader.Transition, 0), $"tipo {kind} a p = 0,45", Quad(1), e));
        }

        e = Effects();
        Set(e, Mix0, 1, 0.45f, 0.99f, 0);
        Set(e, BlendInfo, 6, 0, 0, 0);
        list.Add(Case("transition-BLEND-fundido-modo6", IndexOf(EffectShader.Transition, Blend), "fundido con mezcla por shader (luz fuerte)", Quad(1), e));
        e = Effects();
        Set(e, Mix0, 4, 0.45f, 0.99f, 0);
        Set(e, Mix1, 0.5f, 0, 0, 0);
        Set(e, BlendInfo, 9, 0, 0, 0);
        list.Add(Case("transition-BLEND-empuje-modo9", IndexOf(EffectShader.Transition, Blend), "empuje con mezcla por shader (diferencia)", Quad(1), e));
        foreach (var (name, kind, mix1x, mix1y, blend) in new[] { ("fundido", 1, 0f, 0f, 0), ("empuje", 4, 0.5f, 0f, 0), ("barrido", 3, 0.8f, 0.6f, 5) })
        {
            int flags = Motion | (blend != 0 ? Blend : 0);
            e = Effects();
            Set(e, Mix0, kind, 0.45f, 0.99f, 5);
            Set(e, Mix1, mix1x, mix1y, kind == 3 ? 0.1f : 0f, kind == 3 ? 0.02f : 0f);
            Set(e, Key0, 0.9f, 0.6f, 0.3f, 0);
            Set(e, Fx0, 0.3f, 0.5f, 0, 0);
            Set(e, BlendInfo, blend, 0, 0, 0);
            list.Add(Case($"transition-{Flags(flags)}-{name}", IndexOf(EffectShader.Transition, flags), "desenfoque de movimiento: 5 muestras de p entre 0,3 y 0,5", Quad(1), e));
        }

        // Pasadas sobre intermedios (uv de 0 a 1).
        list.Add(Case("downsample", IndexOf(EffectShader.Downsample, 0), "128² → 64²: bilineal justo entre cuatro texeles", Quad(1), DownsampleEffects(),
            Bindings(("t5", "imagenA128"))));

        // Sondas del filtro bilineal: escala 0,5 cae en fracciones 0,25/0,75 (exactas con 8 bits); 0,9 recorre todas.
        foreach (float scale in new[] { 0.5f, 0.9f })
        {
            e = Effects();
            Set(e, MapA, scale, scale, 1 - HalfTexel, 1 - HalfTexel);
            list.Add(Case($"muestreo-escala{scale.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}", IndexOf(EffectShader.Downsample, 0), $"SampleA con uv × {scale.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}: fracciones de texel {(scale == 0.5f ? "exactas" : "arbitrarias")}", Quad(1), e));
        }

        e = Effects();
        Set(e, Kernel, 1f / Size, 0, 2f, 5);
        list.Add(Case("blur-h", IndexOf(EffectShader.Blur, 0), "gaussiano horizontal, σ = 2, 5 muestras por lado", Quad(1), e));
        e = Effects();
        Set(e, Kernel, 0, 1.5f / Size, 1.5f, 3);
        list.Add(Case("blur-v-medio-texel", IndexOf(EffectShader.Blur, 0), "vertical con paso de 1,5 texeles (bilineal entre texeles)", Quad(1), e));
        e = Effects();
        Set(e, Kernel, 0.8f, 0, 0, 0);
        list.Add(Case("sharpen", IndexOf(EffectShader.Sharpen, 0), "cantidad 0,8 sin umbral (B hace de desenfocado)", Quad(1), e));
        e = Effects();
        Set(e, Kernel, 0.8f, 0.02f, 0, 0);
        list.Add(Case("sharpen-umbral", IndexOf(EffectShader.Sharpen, 0), "cantidad 0,8, umbral 0,02", Quad(1), e));
        e = Effects();
        Set(e, Kernel, 6, 0, 0, 0);
        list.Add(Case("pixelate", IndexOf(EffectShader.Pixelate, 0), "bloques de 6 px (el último, recortado)", Quad(1), e));

        foreach (var (name, kind, invert, coarse) in new[] { ("rect", 0, 0, 0), ("rect-invertida", 0, 1, 0), ("elipse", 1, 0, 0), ("poligono", 2, 0, 0),
                     ("mate", 3, 0, 0), ("pluma", 4, 0, 0), ("trazo", 5, 0, 0), ("poligono-gruesa", 2, 0, 2) })
        {
            e = Effects();
            Set(e, Mix0, kind, invert, 0.9f, 3f);
            Set(e, Mix1, 32, 30, 18, 12);
            Set(e, Kernel, 0.9553365f, 0.2955202f, 6, 1f);
            Set(e, Key0, 4, 2, 1, 4);
            Set(e, Key1, -18, -12, 18, 12);
            Set(e, Canvas, Size, Size, coarse, 6);
            Set(e, Tones, 8, 8, 0, 0);
            list.Add(Case($"mask-{name}", IndexOf(EffectShader.Mask, 0), $"forma {kind}" + (coarse > 0 ? ", con la pasada gruesa (t11)" : ""), Quad(1), e));
        }

        for (int view = 0; view <= 2; view++)
        {
            e = Effects();
            Set(e, Mix0, view, 0, 0, 0);
            Set(e, Mix1, 1, 1, 1 - HalfTexel, 1 - HalfTexel);
            list.Add(Case($"maskmix-vista{view}", IndexOf(EffectShader.MaskMix, 0), "antes (t5) y después (t6) por la máscara (t7)", Quad(1), e));
        }

        foreach (var (name, set) in FxOps())
        {
            e = Effects();
            set(e);
            list.Add(Case($"fx-{name}", IndexOf(EffectShader.Fx, 0), $"FxOp {e[Fx0 * 4]}", Quad(1), e));
        }

        return [.. list];
    }

    private static float[] DownsampleEffects()
    {
        float[] e = Effects();
        Set(e, MapA, 1, 1, 1 - (0.5f / (2 * Size)), 1 - (0.5f / (2 * Size)));
        return e;
    }

    private static IEnumerable<(string Name, int Kind, Action<float[]> Set)> Transitions()
    {
        yield return ("corte", 0, _ => { });
        yield return ("fundido", 1, _ => { });
        yield return ("fundido-color", 2, e => Set(e, Mix1, 0.1f, 0.2f, 0.3f, 0));
        yield return ("barrido", 3, e => { Set(e, Mix1, 0.8f, 0.6f, 0.1f, 0.02f); Set(e, Key0, 0.9f, 0.6f, 0.3f, 0); });
        yield return ("empuje", 4, e => Set(e, Mix1, 0.5f, 0, 0, 0));
        yield return ("zoom", 5, e => Set(e, Mix1, 0.5f, 0, 0, 0));
        yield return ("deslizar", 6, e => Set(e, Mix1, 0.3f, 0.2f, 0.5f, 0));
        yield return ("zoom-movido", 7, e => Set(e, Mix1, 0.8f, 0.45f, 0.55f, 0));
        yield return ("giro", 8, e => Set(e, Mix1, 90f, 0.3f, 0, 0));
        yield return ("barrido-luz", 9, e => { Set(e, Mix1, 0.6f, 0.8f, 0.1f, 0.05f); Set(e, Key0, 0.5f, 0.9f, 0.6f, 0.3f); });
        yield return ("glitch", 10, e => Set(e, Mix1, 0.1f, 12f, 0.02f, 3f));
        yield return ("quemado", 11, e => { Set(e, Mix1, 0.3f, 2f, 0, 0); Set(e, Key0, 1f, 0.5f, 0.1f, 0); });
        yield return ("destello", 12, e => { Set(e, Mix1, 1f, 0.4f, 0, 0); Set(e, Key0, 1f, 1f, 0.9f, 0); });
        foreach (int g in new[] { 0, 1, 2, 3, 4, 5 })
        {
            int gradient = g;
            yield return ($"degradado{gradient}", 13, e =>
            {
                Set(e, Mix1, 0.8f, 0.6f, 0.1f, 0.02f);
                Set(e, Key0, 0.2f, 0.4f, 0.9f, 0);
                Set(e, Key1, gradient, gradient == 3 ? 1 : 0, 0, 0.125f);
            });
        }

        // degradado2 (angular) con key1.w = 0,125 pone el corte de frac() justo en la diagonal de centros de píxel; este no.
        yield return ("degradado2-desfasado", 13, e =>
        {
            Set(e, Mix1, 0.8f, 0.6f, 0.1f, 0.02f);
            Set(e, Key0, 0.2f, 0.4f, 0.9f, 0);
            Set(e, Key1, 2, 0, 0, 0.13f);
        });
        yield return ("degradado-mate", 13, e => { Set(e, Mix1, 0, 0, 0.1f, 0); Set(e, Key1, 0, 0, 1, 0); });
        yield return ("rebote", 14, e => Set(e, Mix1, 0.4f, 0.1f, 0.6f, 0));
    }

    private static IEnumerable<(string Name, Action<float[]> Set)> FxOps()
    {
        yield return ("grano", e => { Set(e, Fx0, 1, 0.3f, 2f, 0.5f); Set(e, Fx1, 0.5f, 1.25f, 1f, 0); });
        yield return ("resplandor-brillo", e => Set(e, Fx0, 2, 0.6f, 0, 0));
        yield return ("resplandor-suma", e => Set(e, Fx0, 3, 0.8f, 0, 0));
        yield return ("aberracion-radial", e => Set(e, Fx0, 4, 0, 0.02f, 0));
        yield return ("aberracion-lateral", e => Set(e, Fx0, 4, 1, 0.01f, 0.005f));
        for (int mode = 0; mode <= 5; mode++)
        {
            int m = mode;
            yield return ($"espejo{m}", e => Set(e, Fx0, 5, m, 0.4f, 3));
        }

        yield return ("distorsion", e => Set(e, Fx0, 6, 0.2f, 1.1f, 1f));
        yield return ("linea", e => Set(e, Fx0, 7, 1f / Size, 0.5f / Size, 4));
        yield return ("zoom", e => { Set(e, Fx0, 8, 0.5f, 0.45f, 0.01f); Set(e, Fx1, 6, 0, 0, 0); });
        yield return ("lente", e => { Set(e, Fx0, 9, 3f / Size, 3f / Size, 0); Set(e, Fx1, 16, 0, 0, 0); });
        yield return ("bilateral", e => { Set(e, Fx0, 10, 0.8f, 1f / Size, 1f / Size); Set(e, Fx1, 0.1f, 20f, 0, 0); });
        yield return ("temporal", e => Set(e, Fx0, 11, 0.7f, 0.02f, 0.1f));
        yield return ("a-lineal", e => Set(e, Fx0, 12, 0, 0, 0));
        yield return ("a-gamma", e => Set(e, Fx0, 13, 0, 0, 0));
        yield return ("region", e => { Set(e, Fx0, 14, 0, 0, 0); Set(e, Fx2, 1, 1, 1 - HalfTexel, 1 - HalfTexel); });
        yield return ("copia", e => Set(e, Fx0, 15, 0, 0, 0));
        yield return ("reencuadre360", e => { Set(e, Fx0, 16, 0.8f, 1f, 0); Set(e, Fx1, 0.9553365f, 0, 0.2955202f, 0); Set(e, Fx2, 0, 1, 0, 0); });
        yield return ("reencuadre360-estereo", e => { Set(e, Fx0, 16, 1.2f, 1f, 1); Set(e, Fx1, 0.9553365f, 0, 0.2955202f, 0); Set(e, Fx2, 0, 1, 0, 0); });
        yield return ("titulo-unidad", e => { Set(e, Fx0, 17, 0, 0.9f, 0); Set(e, Fx1, 0.1f, 0.1f, 0.9f, 0.9f); Set(e, Fx2, 0.2f, 0.8f, 1f / Size, 0); });
    }
}

