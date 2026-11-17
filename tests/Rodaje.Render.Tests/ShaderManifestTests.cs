using Rodaje.Render.Shaders;

namespace Rodaje.Render.Tests;

/// <summary>Pruebas del manifiesto de variantes de shader.</summary>
public sealed class ShaderManifestTests
{
    [Fact]
    public void Manifest_contains_exactly_one_hundred_variants()
    {
        // 3 de RenderShaders (VsQuad vs_5_0, PsLayer ps_5_0, PsPresent ps_5_0)
        // + 77 de EffectShaders.All
        // + 7 de ExportShaders (*Variant)
        // + 2 de FlowShaders (Luma, Chroma)
        // + 7 de FlowShaders.Optical (Downsample, Search, Densify, Histogram, Residual, Decide, Warp)
        // + 3 de ScopeShaders (Accumulate, Maximum, Draw)
        // + 1 de RawShaders (DevelopVariant)
        // = 100 variantes en total.
        Assert.Equal(100, ShaderManifest.All.Count);
    }

    [Fact]
    public void All_variant_ids_are_unique()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (ShaderVariant variant in ShaderManifest.All)
        {
            string id = ShaderManifest.Id(variant);
            Assert.True(ids.Add(id), $"El identificador de shader '{id}' está duplicado.");
        }

        Assert.Equal(ShaderManifest.All.Count, ids.Count);
    }

    public static TheoryData<int> AllVariantIndices()
    {
        var data = new TheoryData<int>();
        for (int i = 0; i < ShaderManifest.All.Count; i++)
        {
            data.Add(i);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllVariantIndices))]
    public void Every_manifest_variant_compiles_with_shader_compiler(int index)
    {
        ShaderVariant variant = ShaderManifest.All[index];
        byte[] code = ShaderCompiler.Compile(variant.FullSource(), variant.EntryPoint, variant.Profile, variant.SourceName);
        Assert.NotEmpty(code);
    }
}
