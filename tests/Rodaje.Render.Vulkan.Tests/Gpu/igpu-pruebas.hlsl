cbuffer Draw : register(b0) { float4 color; float4 depth; uint4 id; };
struct VsOut { float4 position : SV_Position; float4 color : COLOR; };

VsOut VsFull(uint v : SV_VertexID)
{
    VsOut o;
    float2 uv = float2(v & 1, v >> 1);
    o.position = float4(uv.x * 2 - 1, 1 - uv.y * 2, depth.x, 1);
    o.color = color;
    return o;
}

// Profundidad que crece de izquierda a derecha (para el sesgo por pendiente).
VsOut VsSlope(uint v : SV_VertexID)
{
    VsOut o;
    float2 uv = float2(v & 1, v >> 1);
    o.position = float4(uv.x * 2 - 1, 1 - uv.y * 2, 0.2 + 0.6 * uv.x, 1);
    o.color = color;
    return o;
}

VsOut VsMesh(float3 position : POSITION, float4 vertexColor : COLOR)
{
    VsOut o;
    o.position = float4(position, 1);
    o.color = vertexColor;
    return o;
}

float4 PsColor(VsOut i) : SV_Target { return i.color; }

uint PsId(VsOut i) : SV_Target { return id.x; }

Texture2DArray<float> luma : register(t0);
Texture2DArray<float2> chroma : register(t1);

float4 PsPlanes(VsOut i) : SV_Target { return float4(luma.Load(int4(0, 0, 0, 0)), chroma.Load(int4(0, 0, 0, 0)), 1); }

// Texel a texel del mip id.z: los niveles generados no se copian a una lectura.
Texture2D<float4> tex : register(t2);

float4 PsMip(VsOut i) : SV_Target { return tex.Load(int3(int2(i.position.xy), int(id.z))); }

RWTexture2D<float4> image : register(u0);
RWByteAddressBuffer bytes : register(u1);

[numthreads(8, 8, 1)]
void CsFill(uint3 t : SV_DispatchThreadID)
{
    image[t.xy] = float4(t.x / 255.0, t.y / 255.0, color.z, 1);
    if (t.y == 0) bytes.Store(t.x * 4, t.x * 3 + id.x);
}
