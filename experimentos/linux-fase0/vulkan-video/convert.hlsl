[[vk::binding(0, 0)]]
Texture2D<float> planeY;

[[vk::binding(1, 0)]]
Texture2D<float2> planeUV;

[[vk::binding(2, 0)]]
SamplerState smp;

[[vk::binding(3, 0)]]
RWTexture2D<float4> outImage;

struct PushConsts
{
    uint outWidth;
    uint outHeight;
    uint is10Bit;
    uint isFullRange;
    float maxU;
    float maxV;
};

[[vk::push_constant]]
PushConsts push;

[numthreads(16, 16, 1)]
void main(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    if (dispatchThreadId.x >= push.outWidth || dispatchThreadId.y >= push.outHeight)
        return;

    float2 normCoord = (float2(dispatchThreadId.xy) + 0.5f) / float2(push.outWidth, push.outHeight);
    float2 uv = normCoord * float2(push.maxU, push.maxV);

    float y = planeY.SampleLevel(smp, uv, 0);
    float2 chroma = planeUV.SampleLevel(smp, uv, 0);

    float y_norm, cb_norm, cr_norm;

    if (push.is10Bit != 0)
    {
        // 10-bit P010 in 16-bit UNORM (val * (65535/64))
        float y10 = y * (65535.0f / 64.0f);
        float cb10 = chroma.x * (65535.0f / 64.0f);
        float cr10 = chroma.y * (65535.0f / 64.0f);

        if (push.isFullRange != 0)
        {
            y_norm = y10 / 1023.0f;
            cb_norm = (cb10 - 512.0f) / 1024.0f;
            cr_norm = (cr10 - 512.0f) / 1024.0f;
        }
        else
        {
            // Rec.709 studio swing: Y in [64, 940], Cb/Cr in [64, 960] centered at 512
            y_norm = (y10 - 64.0f) / (940.0f - 64.0f);
            cb_norm = (cb10 - 512.0f) / (960.0f - 64.0f);
            cr_norm = (cr10 - 512.0f) / (960.0f - 64.0f);
        }
    }
    else
    {
        float y8 = y * 255.0f;
        float cb8 = chroma.x * 255.0f;
        float cr8 = chroma.y * 255.0f;

        if (push.isFullRange != 0)
        {
            y_norm = y;
            cb_norm = chroma.x - 0.5f;
            cr_norm = chroma.y - 0.5f;
        }
        else
        {
            // Rec.709 studio swing: Y in [16, 235], Cb/Cr in [16, 240] centered at 128
            y_norm = (y8 - 16.0f) / (235.0f - 16.0f);
            cb_norm = (cb8 - 128.0f) / (240.0f - 16.0f);
            cr_norm = (cr8 - 128.0f) / (240.0f - 16.0f);
        }
    }

    // ITU-R BT.709 color matrix
    float r = y_norm + 1.5748f * cr_norm;
    float g = y_norm - 0.1873f * cb_norm - 0.4681f * cr_norm;
    float b = y_norm + 1.8556f * cb_norm;

    outImage[dispatchThreadId.xy] = float4(saturate(r), saturate(g), saturate(b), 1.0f);
}
