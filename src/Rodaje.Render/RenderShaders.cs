namespace Rodaje.Render;

/// <summary>HLSL del compositor y del presentador, compilado una vez por proceso.</summary>
internal static class RenderShaders
{
    // corners[i]: xy = NDC, zw = uv; local[i].xy = punto en el espacio local de la capa; orden TL, TR, BL, BR.
    // range, toRgb y limits: YuvRange, YuvToRgb y tope de uv a medio texel del borde visible.
    // shape: x = opacidad, y = píxeles de destino por píxel local, zw = tamaño local. La cobertura del borde se
    // calcula con la distancia al lado más cercano en píxeles de destino, así un borde girado sale suavizado y uno
    // alineado con la rejilla queda nítido (los centros de sus píxeles están a 0,5 px o más del lado).
    // present: x > 0 pide muestreo con mipmaps (destino más pequeño que la fuente).
    private const string Source = """
        cbuffer Quad : register(b0)
        {
            float4 corners[4];
            float4 local[4];
            float4 range;
            float4 limits;
            float4 toRgb;
            float4 shape;
            float4 present;
        };

        Texture2DArray<float> lumaPlane : register(t0);
        Texture2DArray<float2> chromaPlane : register(t1);
        Texture2D<float4> composite : register(t2);
        SamplerState linearClamp : register(s0);

        struct VsOut
        {
            float4 position : SV_Position;
            float2 uv : TEXCOORD0;
            float2 local : TEXCOORD1;
        };

        VsOut VsQuad(uint id : SV_VertexID)
        {
            VsOut o;
            o.position = float4(corners[id].xy, 0, 1);
            o.uv = corners[id].zw;
            o.local = local[id].xy;
            return o;
        }

        float4 PsLayer(VsOut i) : SV_Target
        {
            float y = lumaPlane.SampleLevel(linearClamp, float3(min(i.uv, limits.xy), 0), 0) * range.x + range.y;
            float2 c = chromaPlane.SampleLevel(linearClamp, float3(min(i.uv, limits.zw), 0), 0) * range.z + range.w;
            float3 rgb = saturate(float3(
                y + toRgb.x * c.y,
                y - toRgb.y * c.x - toRgb.z * c.y,
                y + toRgb.w * c.x));
            float2 inside = min(i.local, shape.zw - i.local);
            float coverage = saturate(min(inside.x, inside.y) * shape.y + 0.5);
            float alpha = shape.x * coverage;
            return float4(rgb * alpha, alpha);
        }

        float4 PsPresent(VsOut i) : SV_Target
        {
            float3 rgb = present.x > 0
                ? composite.Sample(linearClamp, i.uv).rgb
                : composite.SampleLevel(linearClamp, i.uv, 0).rgb;
            return float4(rgb, 1);
        }
        """;

    private static readonly Lazy<byte[]> s_vertex = new(() => Compile("VsQuad", "vs_5_0"));
    private static readonly Lazy<byte[]> s_layer = new(() => Compile("PsLayer", "ps_5_0"));
    private static readonly Lazy<byte[]> s_present = new(() => Compile("PsPresent", "ps_5_0"));

    public static byte[] Vertex => s_vertex.Value;

    public static byte[] Layer => s_layer.Value;

    public static byte[] Present => s_present.Value;

    private static byte[] Compile(string entryPoint, string target) =>
        ShaderCompiler.Compile(Source, entryPoint, target, "rodaje-render.hlsl");
}
