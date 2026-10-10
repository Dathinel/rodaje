using Rodaje.Render;

namespace Rodaje.Visor;

/// <summary>Shaders del visor, compilados al arrancar con <c>d3dcompiler_47.dll</c> (parte de Windows).</summary>
/// <remarks>
/// Compilar en ejecución evita depender de fxc.exe (Windows SDK) para construir; cuesta unos milisegundos
/// al abrir la ventana. Ver la decisión D-015 en docs/decisiones.md.
/// </remarks>
internal static class Shaders
{
    // corners[i]: xy = posición en NDC, zw = coordenada de textura; orden TL, TR, BL, BR (tira de triángulos).
    // range: escala y desplazamiento de luma y croma (YuvRange). limits: tope de uv para luma (xy) y croma (zw),
    // a medio texel del borde visible, para que el filtro no mezcle el relleno del decodificador.
    // toRgb: coeficientes de la matriz del cuadro (YuvToRgb: BT.601, BT.709 o BT.2020). Las curvas PQ/HLG y la
    // conversión de primarios quedan fuera de la fase 2.
    private const string Source = """
        cbuffer Frame : register(b0)
        {
            float4 corners[4];
            float4 range;
            float4 limits;
            float4 color;
            float4 toRgb;
        };

        Texture2DArray<float> lumaPlane : register(t0);
        Texture2DArray<float2> chromaPlane : register(t1);
        SamplerState linearClamp : register(s0);

        struct VsOut
        {
            float4 position : SV_Position;
            float2 uv : TEXCOORD0;
        };

        VsOut VsMain(uint id : SV_VertexID)
        {
            float4 c = corners[id];
            VsOut o;
            o.position = float4(c.xy, 0, 1);
            o.uv = c.zw;
            return o;
        }

        float4 PsVideo(VsOut i) : SV_Target
        {
            float y = lumaPlane.SampleLevel(linearClamp, float3(min(i.uv, limits.xy), 0), 0) * range.x + range.y;
            float2 c = chromaPlane.SampleLevel(linearClamp, float3(min(i.uv, limits.zw), 0), 0) * range.z + range.w;
            float3 rgb = float3(
                y + toRgb.x * c.y,
                y - toRgb.y * c.x - toRgb.z * c.y,
                y + toRgb.w * c.x);
            return float4(saturate(rgb), 1);
        }

        float4 PsSolid(VsOut i) : SV_Target
        {
            return color;
        }
        """;

    public static byte[] VertexShader() => Compile("VsMain", "vs_5_0");

    public static byte[] VideoPixelShader() => Compile("PsVideo", "ps_5_0");

    public static byte[] SolidPixelShader() => Compile("PsSolid", "ps_5_0");

    private static byte[] Compile(string entryPoint, string target) =>
        ShaderCompiler.Compile(Source, entryPoint, target, "visor.hlsl");
}
