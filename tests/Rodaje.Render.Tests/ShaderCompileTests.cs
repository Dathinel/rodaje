using Rodaje.Render.Effects;
using Rodaje.Render.Shaders;

namespace Rodaje.Render.Tests;

public sealed class ShaderCompileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rodaje-shaders-" + Guid.NewGuid().ToString("N"));

    public static TheoryData<int> Variants()
    {
        var data = new TheoryData<int>();
        for (int i = 0; i < EffectShaders.All.Length; i++)
        {
            data.Add(i);
        }

        return data;
    }

    /// <summary>Cada variante compila con las advertencias como errores (las opciones de <see cref="ShaderCompiler"/>).</summary>
    [Theory]
    [MemberData(nameof(Variants))]
    public void Every_effect_variant_compiles_without_warnings(int index)
    {
        ShaderVariant variant = EffectShaders.All[index];
        byte[] code = ShaderCompiler.Compile(variant.FullSource(), variant.EntryPoint, variant.Profile, variant.SourceName);
        Assert.NotEmpty(code);
    }

    [Fact]
    public void Variant_indices_are_unique_and_cover_the_list()
    {
        var indices = new List<int>();
        for (int flags = 0; flags < EffectShaders.GradedVariants; flags++)
        {
            indices.Add(EffectShaders.IndexOf(EffectShader.Graded, flags));
        }

        foreach (EffectShader shader in new[] { EffectShader.Image, EffectShader.Transition })
        {
            indices.Add(EffectShaders.IndexOf(shader, 0));
            indices.Add(EffectShaders.IndexOf(shader, EffectShaders.Blend));
        }

        indices.Add(EffectShaders.IndexOf(EffectShader.Downsample, 0));
        indices.Add(EffectShaders.IndexOf(EffectShader.Blur, 0));
        indices.Add(EffectShaders.IndexOf(EffectShader.Sharpen, 0));
        indices.Add(EffectShaders.IndexOf(EffectShader.Mask, 0));
        indices.Add(EffectShaders.IndexOf(EffectShader.MaskMix, 0));
        indices.Add(EffectShaders.IndexOf(EffectShader.Pixelate, 0));
        indices.Add(EffectShaders.IndexOf(EffectShader.Fx, 0));
        Assert.Equal(Enumerable.Range(0, EffectShaders.All.Length), indices.Order());
    }

    [Fact]
    public void Disk_cache_writes_once_and_reads_back_in_a_new_session()
    {
        ShaderVariant variant = EffectShaders.All[EffectShaders.IndexOf(EffectShader.Blur, 0)];
        var first = new ShaderCache(_directory);
        byte[] compiled = first.GetOrCompile(variant);
        Assert.True(first.TryGet(variant, out _));
        Assert.Equal(1, first.CompiledCount);
        Assert.True(File.Exists(Path.Combine(_directory, ShaderCache.Key(variant) + ".cso")));

        var second = new ShaderCache(_directory);
        Assert.False(second.TryGet(variant, out _));
        Assert.Equal(compiled, second.GetOrCompile(variant));
        Assert.Equal(0, second.CompiledCount);
        Assert.Equal(1, second.LoadedFromDiskCount);
    }

    [Fact]
    public void Defines_change_the_key()
    {
        Assert.Equal(EffectShaders.All.Length, EffectShaders.All.Select(ShaderCache.Key).Distinct().Count());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
