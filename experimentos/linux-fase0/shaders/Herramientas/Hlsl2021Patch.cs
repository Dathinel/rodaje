namespace Fase0.Shaders;

/// <summary>
/// El parche mínimo para que el HLSL de los efectos compile con el HLSL 2021 de DXC (sin <c>-HV 2018</c>) y siga compilando con
/// fxc: <c>select</c> y <c>and</c> donde la condición es un vector, y dos macros para fxc, que no los conoce. Solo se aplica sobre una
/// copia en memoria; <c>src/</c> no cambia.
/// </summary>
internal static class Hlsl2021Patch
{
    public const string Header = """
        #if !defined(__HLSL_VERSION) || __HLSL_VERSION < 2021
        #define select(c, a, b) ((c) ? (a) : (b))
        #define and(a, b) ((a) && (b))
        #endif

        """;

    public static readonly (string From, string To)[] Replacements =
    [
        ("return cb <= 0.25 ? ((16.0 * cb - 12.0) * cb + 4.0) * cb : sqrt(cb);",
         "return select(cb <= 0.25, ((16.0 * cb - 12.0) * cb + 4.0) * cb, sqrt(cb));"),
        ("return cs <= 0.5 ? cb * 2.0 * cs : 1.0 - (1.0 - cb) * (1.0 - (2.0 * cs - 1.0));",
         "return select(cs <= 0.5, cb * 2.0 * cs, 1.0 - (1.0 - cb) * (1.0 - (2.0 * cs - 1.0)));"),
        ("case 5: return cs <= 0.5 ? cb - (1.0 - 2.0 * cs) * cb * (1.0 - cb) : cb + (2.0 * cs - 1.0) * (SoftLightD(cb) - cb);",
         "case 5: return select(cs <= 0.5, cb - (1.0 - 2.0 * cs) * cb * (1.0 - cb), cb + (2.0 * cs - 1.0) * (SoftLightD(cb) - cb));"),
        ("case 11: return cb <= 0.0 ? 0.0 : (cs >= 1.0 ? 1.0 : min(1.0, cb / (1.0 - cs)));",
         "case 11: return select(cb <= 0.0, (float3)0.0, select(cs >= 1.0, (float3)1.0, min(1.0, cb / (1.0 - cs))));"),
        ("case 12: return cb >= 1.0 ? 1.0 : (cs <= 0.0 ? 0.0 : 1.0 - min(1.0, (1.0 - cb) / cs));",
         "case 12: return select(cb >= 1.0, (float3)1.0, select(cs <= 0.0, (float3)0.0, 1.0 - min(1.0, (1.0 - cb) / cs)));"),
        ("all(l >= key1.xy - key0.w && l <= key1.zw + key0.w)",
         "all(and(l >= key1.xy - key0.w, l <= key1.zw + key0.w))"),
        ("m = m > 1.0 ? 2.0 - m : m;",
         "m = select(m > 1.0, 2.0 - m, m);"),
    ];

    public static string Apply(string source)
    {
        foreach (var (from, to) in Replacements)
        {
            int at = source.IndexOf(from, StringComparison.Ordinal);
            if (at < 0 || source.IndexOf(from, at + from.Length, StringComparison.Ordinal) >= 0)
            {
                throw new InvalidDataException($"El parche no encaja (no está o está repetido): {from}");
            }

            source = source.Replace(from, to, StringComparison.Ordinal);
        }

        return Header + source;
    }
}
