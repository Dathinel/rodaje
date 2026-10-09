using System.Text;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static Rodaje.Visor.Win32;

namespace Rodaje.Visor;

/// <summary>Shaders del visor, compilados al arrancar con <c>d3dcompiler_47.dll</c> (parte de Windows).</summary>
/// <remarks>
/// Compilar en ejecución evita depender de fxc.exe (Windows SDK) para construir; cuesta unos milisegundos
/// al abrir la ventana. Ver la decisión D-015 en docs/decisiones.md.
/// </remarks>
internal static unsafe class Shaders
{
    // corners[i]: xy = posición en NDC, zw = coordenada de textura; orden TL, TR, BL, BR (tira de triángulos).
    // range: escala y desplazamiento de luma y croma (YuvRange). limits: tope de uv para luma (xy) y croma (zw),
    // a medio texel del borde visible, para que el filtro no mezcle el relleno del decodificador.
    // BT.709 en rango limitado; BT.2020 y PQ/HLG quedan fuera de la fase 2.
    private const string Source = """
        cbuffer Frame : register(b0)
        {
            float4 corners[4];
            float4 range;
            float4 limits;
            float4 color;
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
                y + 1.5748 * c.y,
                y - 0.187324 * c.x - 0.468124 * c.y,
                y + 1.8556 * c.x);
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

    private static byte[] Compile(string entryPoint, string target)
    {
        byte[] source = Encoding.ASCII.GetBytes(Source);
        byte[] entry = Encoding.ASCII.GetBytes(entryPoint + "\0");
        byte[] profile = Encoding.ASCII.GetBytes(target + "\0");
        byte[] name = "visor.hlsl\0"u8.ToArray();
        ID3DBlob* code = null;
        ID3DBlob* errors = null;
        HRESULT hr;
        fixed (byte* s = source, e = entry, p = profile, n = name)
        {
            hr = D3DCompile(s, (nuint)source.Length, (sbyte*)n, null, null, (sbyte*)e, (sbyte*)p,
                D3DCOMPILE_OPTIMIZATION_LEVEL3 | D3DCOMPILE_WARNINGS_ARE_ERRORS, 0, &code, &errors);
        }

        try
        {
            if (hr.FAILED)
            {
                string detail = errors != null
                    ? Encoding.ASCII.GetString((byte*)errors->GetBufferPointer(), (int)errors->GetBufferSize()).TrimEnd('\0', '\n')
                    : $"HRESULT 0x{hr.Value:X8}";
                throw new InvalidOperationException($"No se pudo compilar el shader {entryPoint} con d3dcompiler_47.dll: {detail}");
            }

            return new ReadOnlySpan<byte>(code->GetBufferPointer(), (int)code->GetBufferSize()).ToArray();
        }
        finally
        {
            if (code != null)
            {
                code->Release();
            }

            if (errors != null)
            {
                errors->Release();
            }
        }
    }
}
