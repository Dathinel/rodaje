using Rodaje.Render.Shaders;

namespace Rodaje.Render.Interpolation;

internal static class FlowShaders
{
    private const string Blend = """
        cbuffer Constants : register(b0)
        {
            uint width;
            uint height;
            float fraction;
            uint padding;
        };
        #ifdef CHROMA
        Texture2DArray<float2> first : register(t0);
        Texture2DArray<float2> second : register(t1);
        RWTexture2D<float2> output : register(u0);
        #else
        Texture2DArray<float> first : register(t0);
        Texture2DArray<float> second : register(t1);
        RWTexture2D<float> output : register(u0);
        #endif

        [numthreads(8, 8, 1)]
        void CsBlend(uint3 id : SV_DispatchThreadID)
        {
            if (id.x >= width || id.y >= height) return;
            output[id.xy] = lerp(first.Load(int4(id.xy, 0, 0)), second.Load(int4(id.xy, 0, 0)), fraction);
        }
        """;

    public static ShaderVariant Luma { get; } = new(Blend, "CsBlend", "cs_5_0", "rodaje-flow.hlsl");
    public static ShaderVariant Chroma { get; } = new(Blend, "CsBlend", "cs_5_0", "rodaje-flow.hlsl", "CHROMA");
}
