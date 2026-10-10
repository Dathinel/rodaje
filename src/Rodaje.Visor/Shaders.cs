using Rodaje.Render;

namespace Rodaje.Visor;

/// <summary>Shaders de la barra de progreso, compilados al arrancar con <c>d3dcompiler_47.dll</c> (parte de Windows).</summary>
/// <remarks>El video lo dibujan <c>Compositor</c> y <c>Presenter</c> de Rodaje.Render. Ver la decisión D-015.</remarks>
internal static class Shaders
{
    // corners[i]: xy = posición en NDC; orden TL, TR, BL, BR (tira de triángulos).
    private const string Source = """
        cbuffer Frame : register(b0)
        {
            float4 corners[4];
            float4 color;
        };

        float4 VsMain(uint id : SV_VertexID) : SV_Position
        {
            return float4(corners[id].xy, 0, 1);
        }

        float4 PsSolid(float4 position : SV_Position) : SV_Target
        {
            return color;
        }
        """;

    public static byte[] VertexShader() => Compile("VsMain", "vs_5_0");

    public static byte[] SolidPixelShader() => Compile("PsSolid", "ps_5_0");

    private static byte[] Compile(string entryPoint, string target) =>
        ShaderCompiler.Compile(Source, entryPoint, target, "visor.hlsl");
}
