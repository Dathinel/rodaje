namespace Rodaje.Render.Tests.Support;

/// <summary>
/// Un pixel shader vacío, compilado una vez: lo que devuelven las cachés de las pruebas que retienen la compilación, para
/// no pagar en frío las variantes de los efectos (unas 77 compilaciones O3 por caché).
/// </summary>
internal static class TrivialShader
{
    public static byte[] Code { get; } = ShaderCompiler.Compile("float4 main() : SV_Target { return float4(0, 0, 0, 0); }", "main", "ps_5_0", "trivial.hlsl");
}
