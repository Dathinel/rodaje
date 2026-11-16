using System.Text;

namespace Rodaje.Render.Shaders;

/// <summary>
/// Lo mínimo de <c>ShaderVariant</c> (src/Rodaje.Render/Shaders/ShaderCache.cs) que usa <c>EffectShaders</c>: enlazar ese
/// archivo arrastraría la caché de disco. Misma firma y misma <see cref="FullSource"/>.
/// </summary>
public sealed record ShaderVariant(string Source, string EntryPoint, string Profile, string SourceName, params string[] Defines)
{
    public string FullSource()
    {
        var text = new StringBuilder();
        foreach (string define in Defines)
        {
            text.Append("#define ").Append(define).Append(" 1\n");
        }

        return text.Append(Source).ToString();
    }
}
