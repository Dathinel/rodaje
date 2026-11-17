using System.Collections.Immutable;
using Rodaje.Render.Effects;
using Rodaje.Render.Export;
using Rodaje.Render.Interpolation;
using Rodaje.Render.Raw;
using Rodaje.Render.Scopes;

namespace Rodaje.Render.Shaders;

/// <summary>Lista completa de variantes de shader que Rodaje compila e identifica de forma estable.</summary>
public static class ShaderManifest
{
    private static readonly ImmutableArray<ShaderVariant> s_all = CreateAll();

    /// <summary>Todas las variantes de shader del motor.</summary>
    public static IReadOnlyList<ShaderVariant> All => s_all;

    /// <summary>Identificador estable de una variante: <c>{SourceName}:{EntryPoint}</c> más <c>+DEF1+DEF2</c> si tiene definiciones.</summary>
    public static string Id(ShaderVariant variant)
    {
        ArgumentNullException.ThrowIfNull(variant);
        return variant.Defines.Length == 0
            ? $"{variant.SourceName}:{variant.EntryPoint}"
            : $"{variant.SourceName}:{variant.EntryPoint}+{string.Join('+', variant.Defines)}";
    }

    private static ImmutableArray<ShaderVariant> CreateAll()
    {
        var list = ImmutableArray.CreateBuilder<ShaderVariant>(100);

        list.Add(RenderShaders.VertexVariant);
        list.Add(RenderShaders.LayerVariant);
        list.Add(RenderShaders.PresentVariant);

        list.AddRange(EffectShaders.All);

        list.Add(ExportShaders.ScaleVariant);
        list.Add(ExportShaders.ConvertVariant);
        list.Add(ExportShaders.ConvertHdrVariant);
        list.Add(ExportShaders.ConvertToneMapVariant);
        list.Add(ExportShaders.ThumbnailVariant);
        list.Add(ExportShaders.ThumbnailToneMapVariant);
        list.Add(ExportShaders.LightVariant);

        list.Add(FlowShaders.Luma);
        list.Add(FlowShaders.Chroma);
        foreach (string entry in FlowShaders.OpticalEntries)
        {
            list.Add(FlowShaders.Optical(entry));
        }

        list.Add(ScopeShaders.Accumulate);
        list.Add(ScopeShaders.Maximum);
        list.Add(ScopeShaders.Draw);

        list.Add(RawShaders.DevelopVariant);

        return list.MoveToImmutable();
    }
}
