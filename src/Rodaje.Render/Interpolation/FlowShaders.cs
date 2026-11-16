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

    public static ShaderVariant Optical(string entry) => new(OpticalSource, entry, "cs_5_0", "rodaje-optical-flow.hlsl");

    private const string OpticalSource = """
        cbuffer Constants : register(b0)
        {
            uint2 size; uint2 fullSize;
            uint2 previousSize; uint2 patchCount;
            float fraction; float maxCode; uint initial; uint partialCount;
            float deltaThreshold; float regularization; float alpha; float beta;
            float cutHistogram; float cutResidual; uint iterations; uint scale;
        };
        Texture2DArray<float> planeA : register(t0);
        Texture2DArray<float> planeB : register(t1);
        Texture2D<float> imageA : register(t2);
        Texture2D<float> imageB : register(t3);
        Texture2D<float2> forwardFlow : register(t4);
        Texture2D<float2> backwardFlow : register(t5);
        Texture2D<float2> patches : register(t6);
        Texture2DArray<float2> chromaA : register(t7);
        Texture2DArray<float2> chromaB : register(t8);
        StructuredBuffer<uint> histogram : register(t9);
        StructuredBuffer<float> residuals : register(t10);
        StructuredBuffer<float4> decision : register(t11);
        RWTexture2D<float> imageOut : register(u0);
        RWTexture2D<float2> flowOut : register(u1);
        RWTexture2D<float> lumaOut : register(u2);
        RWTexture2D<float2> chromaOut : register(u3);
        RWStructuredBuffer<uint> histogramOut : register(u4);
        RWStructuredBuffer<float> residualOut : register(u5);
        RWStructuredBuffer<float4> decisionOut : register(u6);

        float SampleImage(Texture2D<float> img, float2 p, uint2 extent)
        {
            p = clamp(p, 0, extent - 1);
            int2 q = (int2)p, r = min(q + 1, (int2)extent - 1);
            float2 f = p - q;
            precise float top = img[q] + f.x * (img[int2(r.x,q.y)] - img[q]);
            precise float bottom = img[int2(q.x,r.y)] + f.x * (img[r] - img[int2(q.x,r.y)]);
            return top + f.y * (bottom - top);
        }
        float2 SampleFlow(Texture2D<float2> img, float2 p, uint2 extent)
        {
            p = clamp(p, 0, extent - 1);
            int2 q = (int2)p, r = min(q + 1, (int2)extent - 1);
            float2 f = p - q;
            precise float2 top = img[q] + f.x * (img[int2(r.x,q.y)] - img[q]);
            precise float2 bottom = img[int2(q.x,r.y)] + f.x * (img[r] - img[int2(q.x,r.y)]);
            return top + f.y * (bottom - top);
        }
        [numthreads(8,8,1)]
        void Downsample(uint3 id : SV_DispatchThreadID)
        {
            if (any(id.xy >= size)) return;
            int2 p = 2 * id.xy;
            precise float sum;
            if (initial != 0)
                sum = planeA.Load(int4(p,0,0)) + planeA.Load(int4(p+int2(1,0),0,0))
                    + planeA.Load(int4(p+int2(0,1),0,0)) + planeA.Load(int4(p+1,0,0));
            else sum = imageA[p] + imageA[p+int2(1,0)] + imageA[p+int2(0,1)] + imageA[p+1];
            imageOut[id.xy] = 0.25 * sum;
        }
        float2 Gradient(int2 p)
        {
            int2 lo = max(p-1,0), hi = min(p+1,(int2)size-1);
            return float2((imageA[int2(hi.x,p.y)]-imageA[int2(lo.x,p.y)]) / (hi.x-lo.x),
                (imageA[int2(p.x,hi.y)]-imageA[int2(p.x,lo.y)]) / (hi.y-lo.y));
        }
        float PatchSsd(int2 p, float2 flow)
        {
            float e[64]; precise float mean = 0;
            [loop] for (int k=0;k<64;k++)
            {
                int2 q=p+int2(k & 7,k >> 3);
                e[k]=SampleImage(imageB,q+flow,size)-imageA[q]; mean+=e[k];
            }
            mean *= 1.0/64;
            precise float ssd=0;
            [loop] for (int j=0;j<64;j++) { float d=e[j]-mean; ssd+=d*d; }
            return ssd;
        }
        [numthreads(4,4,1)]
        void Search(uint3 id : SV_DispatchThreadID)
        {
            if (any(id.xy >= patchCount)) return;
            int2 p=min(4*id.xy,size-8);
            float2 start=initial != 0 ? 0 : 2*SampleFlow(forwardFlow,(p+3.5)*0.5,previousSize);
            float2 gradients[64]; float values[64]; float errors[64];
            precise float h00=0, h01=0, h11=0;
            [loop] for(int k=0;k<64;k++)
            {
                int2 q=p+int2(k & 7,k >> 3); float2 g=Gradient(q);
                gradients[k]=g; values[k]=imageA[q];
                h00+=g.x*g.x; h01+=g.x*g.y; h11+=g.y*g.y;
            }
            h00+=regularization; h11+=regularization;
            precise float invDet=1/(h00*h11-h01*h01);
            float3 invH=float3(h11,-h01,h00)*invDet;
            precise float2 flow=start;
            float ssdStart=PatchSsd(p,start);
            [loop] for(uint it=0;it<iterations;it++)
            {
                precise float mean=0;
                [loop] for(int j=0;j<64;j++)
                {
                    errors[j]=SampleImage(imageB,p+int2(j & 7,j >> 3)+flow,size)-values[j]; mean+=errors[j];
                }
                mean*=1.0/64;
                precise float2 b=0;
                [loop] for(int n=0;n<64;n++) b+=gradients[n]*(errors[n]-mean);
                precise float2 d=float2(invH.x*b.x+invH.y*b.y,invH.y*b.x+invH.z*b.y);
                flow-=d;
                if(dot(d,d)<deltaThreshold*deltaThreshold) break;
            }
            if(PatchSsd(p,flow)>ssdStart) flow=start;
            flowOut[id.xy]=flow;
        }
        [numthreads(8,8,1)]
        void Densify(uint3 id : SV_DispatchThreadID)
        {
            if(any(id.xy>=size)) return;
            int2 low=(max(0,(int2)id.xy-7)>>2), high=min((int2)patchCount-1,((int2)id.xy>>2)+1);
            precise float sum=0; precise float2 weighted=0;
            [loop] for(int y=low.y;y<=high.y;y++)
            [loop] for(int x=low.x;x<=high.x;x++)
            {
                int2 p=min(4*int2(x,y),(int2)size-8);
                if(any(p>(int2)id.xy)||any((int2)id.xy>=p+8)) continue;
                float2 f=patches[int2(x,y)];
                float w=1/max(0.01,abs(SampleImage(imageB,id.xy+f,size)-imageA[id.xy]));
                sum+=w; weighted+=w*f;
            }
            flowOut[id.xy]=weighted*(1/sum);
        }
        groupshared uint bins[128];
        [numthreads(256,1,1)]
        void Histogram(uint3 id : SV_DispatchThreadID, uint lane : SV_GroupIndex)
        {
            if(lane<128) bins[lane]=0;
            GroupMemoryBarrierWithGroupSync();
            if(id.x<fullSize.x*fullSize.y)
            {
                int2 p=int2(id.x%fullSize.x,id.x/fullSize.x);
                uint a=min(63,(uint)(planeA.Load(int4(p,0,0))*64));
                uint b=min(63,(uint)(planeB.Load(int4(p,0,0))*64));
                InterlockedAdd(bins[a],1); InterlockedAdd(bins[64+b],1);
            }
            GroupMemoryBarrierWithGroupSync();
            if(lane<128) InterlockedAdd(histogramOut[lane],bins[lane]);
        }
        groupshared float sums[256];
        [numthreads(256,1,1)]
        void Residual(uint3 id : SV_DispatchThreadID, uint lane : SV_GroupIndex, uint3 group : SV_GroupID)
        {
            float value=0;
            if(id.x<size.x*size.y)
            {
                uint2 p=uint2(id.x%size.x,id.x/size.x);
                value=abs(SampleImage(imageB,p+forwardFlow[p],size)-imageA[p]);
            }
            sums[lane]=value; GroupMemoryBarrierWithGroupSync();
            [unroll] for(uint step=128;step>0;step/=2)
            {
                if(lane<step) sums[lane]+=sums[lane+step]; GroupMemoryBarrierWithGroupSync();
            }
            if(lane==0) residualOut[group.x]=sums[0];
        }
        [numthreads(1,1,1)]
        void Decide(uint3 id : SV_DispatchThreadID)
        {
            float distance=0, residual=0;
            for(uint k=0;k<64;k++) distance+=abs((float)histogram[k]-(float)histogram[k+64]);
            distance*=0.5/(fullSize.x*fullSize.y);
            for(uint j=0;j<partialCount;j++) residual+=residuals[j];
            residual/=size.x*size.y;
            decisionOut[0]=float4(distance>cutHistogram && residual>cutResidual, distance,residual,0);
        }
        float2 FullAt(Texture2D<float2> field, float2 p) { return scale*SampleFlow(field,p/scale,size); }
        float2 FullSample(Texture2D<float2> field,float2 p)
        {
            p=clamp(p,0,fullSize-1);
            int2 q=(int2)p, r=min(q+1,(int2)fullSize-1); float2 f=p-q;
            precise float2 top=FullAt(field,q)+f.x*(FullAt(field,int2(r.x,q.y))-FullAt(field,q));
            precise float2 bottom=FullAt(field,int2(q.x,r.y))+f.x*(FullAt(field,r)-FullAt(field,int2(q.x,r.y)));
            return top+f.y*(bottom-top);
        }
        bool Outside(float2 p) { return any(p<0.0)||any(p>(float2)fullSize-1.0); }
        bool Occluded(Texture2D<float2> first,Texture2D<float2> second,float2 p)
        {
            float2 f=FullAt(first,p), target=p+f;
            if(Outside(target)) return true;
            float2 b=FullSample(second,target);
            return dot(f+b,f+b)>alpha*(dot(f,f)+dot(b,b))+beta;
        }
        float4 TimeFlow(float2 p)
        {
            float2 f=FullAt(forwardFlow,p), b=FullAt(backwardFlow,p);
            precise float4 result=float4(-(1-fraction)*fraction*f+fraction*fraction*b,
                (1-fraction)*(1-fraction)*f-fraction*(1-fraction)*b);
            return result;
        }
        float2 Weights(float2 p,float4 f)
        {
            float2 a=p+f.xy, b=p+f.zw;
            bool e0=Occluded(forwardFlow,backwardFlow,clamp(round(a),0,fullSize-1));
            bool e1=Occluded(backwardFlow,forwardFlow,clamp(round(b),0,fullSize-1));
            bool v0=!Outside(a)&&!(e1&&!e0), v1=!Outside(b)&&!(e0&&!e1);
            return v0==v1 ? float2(1-fraction,fraction) : (v0 ? float2(1,0) : float2(0,1));
        }
        float SamplePlane(Texture2DArray<float> img,float2 p)
        {
            p=clamp(p,0,fullSize-1); int2 q=(int2)p, r=min(q+1,(int2)fullSize-1); float2 f=p-q;
            precise float top=img.Load(int4(q,0,0))+f.x*(img.Load(int4(r.x,q.y,0,0))-img.Load(int4(q,0,0)));
            precise float bottom=img.Load(int4(q.x,r.y,0,0))+f.x*(img.Load(int4(r,0,0))-img.Load(int4(q.x,r.y,0,0)));
            return top+f.y*(bottom-top);
        }
        float2 SampleChroma(Texture2DArray<float2> img,float2 p)
        {
            p=clamp(p,0,fullSize/2-1); int2 q=(int2)p, r=min(q+1,(int2)(fullSize/2)-1); float2 f=p-q;
            precise float2 top=img.Load(int4(q,0,0))+f.x*(img.Load(int4(r.x,q.y,0,0))-img.Load(int4(q,0,0)));
            precise float2 bottom=img.Load(int4(q.x,r.y,0,0))+f.x*(img.Load(int4(r,0,0))-img.Load(int4(q.x,r.y,0,0)));
            return top+f.y*(bottom-top);
        }
        [numthreads(8,8,1)]
        void Warp(uint3 id : SV_DispatchThreadID)
        {
            if(any(id.xy>=fullSize/2)) return;
            int2 p=2*id.xy;
            if(decision[0].x!=0)
            {
                [unroll] for(int k=0;k<4;k++)
                {
                    int2 q=p+int2(k & 1,k >> 1);
                    lumaOut[q]=fraction<0.5 ? planeA.Load(int4(q,0,0)) : planeB.Load(int4(q,0,0));
                }
                chromaOut[id.xy]=fraction<0.5 ? chromaA.Load(int4(id.xy,0,0)) : chromaB.Load(int4(id.xy,0,0));
                return;
            }
            precise float4 average=0; float2 chromaWeights=0;
            [unroll] for(int j=0;j<4;j++)
            {
                int2 q=p+int2(j & 1,j >> 1); float4 f=TimeFlow(q); average+=f;
                float2 w=Weights(q,f); if(j==0) chromaWeights=w;
                precise float value=w.x*SamplePlane(planeA,q+f.xy)+w.y*SamplePlane(planeB,q+f.zw);
                lumaOut[q]=round(saturate(value)*maxCode)/maxCode;
            }
            average*=0.125;
            precise float2 uv=chromaWeights.x*SampleChroma(chromaA,id.xy+average.xy)
                +chromaWeights.y*SampleChroma(chromaB,id.xy+average.zw);
            chromaOut[id.xy]=round(saturate(uv)*maxCode)/maxCode;
        }
        """;
}
