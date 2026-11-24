using Rodaje.Render.Shaders;

namespace Rodaje.Render.Tests.Variants;

internal enum EffectShader
{
    Graded, Image, Transition, Downsample, Blur, Sharpen, Mask, MaskMix, Pixelate, Fx,
}

internal static class CaseVariants
{
    public const int Color = 1, Lut = 2, Vignette = 4, Blend = 8, Mask = 16, Advanced = 32, Motion = 128;
    public const int GradedVariants = 64;
    public static ShaderVariant Vertex { get; } = ShaderManifest.All.Single(v => v.EntryPoint == "VsQuad");
    public static ShaderVariant[] All { get; } = ShaderManifest.All.Where(v => v.SourceName == "rodaje-efectos.hlsl").ToArray();

    public static int IndexOf(EffectShader shader, int flags) => shader switch
    {
        EffectShader.Graded => flags,
        EffectShader.Image => GradedVariants + (flags & Blend) / Blend,
        EffectShader.Transition when (flags & Motion) == 0 => GradedVariants + 2 + (flags & Blend) / Blend,
        EffectShader.Downsample => GradedVariants + 4,
        EffectShader.Blur => GradedVariants + 5,
        EffectShader.Sharpen => GradedVariants + 6,
        EffectShader.Mask => GradedVariants + 7,
        EffectShader.MaskMix => GradedVariants + 8,
        EffectShader.Pixelate => GradedVariants + 9,
        EffectShader.Fx => GradedVariants + 10,
        EffectShader.Transition => GradedVariants + 11 + (flags & Blend) / Blend,
        _ => throw new ArgumentOutOfRangeException(nameof(shader)),
    };
}

