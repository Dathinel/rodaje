using System.Security.Cryptography;
using System.Text;
using Rodaje.Render.Effects;
using Rodaje.Render.Export;
using Rodaje.Render.Raw;

namespace Rodaje.Render;

/// <summary>Huella de los shaders que deciden los píxeles de un cuadro exportado (render en caché, D-060).</summary>
public static class RenderChain
{
    /// <summary>SHA-256 de los fuentes HLSL del compositor, los efectos, la exportación, el revelado RAW y la interpolación.</summary>
    public static string ShaderFingerprint { get; } = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0',
        RenderShaders.Source, EffectShaders.Source, ExportShaders.Scale, ExportShaders.Convert, ExportShaders.Thumbnail,
        ExportShaders.Light, RawShaders.Develop, Interpolation.FlowShaders.Source))));
}
