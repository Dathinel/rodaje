using System.Text;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;

namespace Rodaje.Render;

/// <summary>Compila HLSL en ejecución con <c>d3dcompiler_47.dll</c>, que viene con Windows (decisión D-015).</summary>
public static unsafe class ShaderCompiler
{
    private const uint D3DCOMPILE_OPTIMIZATION_LEVEL3 = 1 << 15;
    private const uint D3DCOMPILE_WARNINGS_ARE_ERRORS = 1 << 18;

    /// <summary>Devuelve el bytecode de <paramref name="entryPoint"/> para el perfil <paramref name="target"/> (por ejemplo <c>ps_5_0</c>).</summary>
    /// <exception cref="RenderException">El código no compila.</exception>
    public static byte[] Compile(string source, string entryPoint, string target, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(entryPoint);
        ArgumentException.ThrowIfNullOrEmpty(target);
        ArgumentException.ThrowIfNullOrEmpty(sourceName);
        byte[] text = Encoding.ASCII.GetBytes(source);
        byte[] entry = Encoding.ASCII.GetBytes(entryPoint + "\0");
        byte[] profile = Encoding.ASCII.GetBytes(target + "\0");
        byte[] name = Encoding.ASCII.GetBytes(sourceName + "\0");
        ID3DBlob* code = null;
        ID3DBlob* errors = null;
        HRESULT hr;
        fixed (byte* s = text, e = entry, p = profile, n = name)
        {
            hr = D3DCompile(s, (nuint)text.Length, (sbyte*)n, null, null, (sbyte*)e, (sbyte*)p,
                D3DCOMPILE_OPTIMIZATION_LEVEL3 | D3DCOMPILE_WARNINGS_ARE_ERRORS, 0, &code, &errors);
        }

        try
        {
            if (hr.FAILED)
            {
                string detail = errors != null
                    ? Encoding.ASCII.GetString((byte*)errors->GetBufferPointer(), (int)errors->GetBufferSize()).TrimEnd('\0', '\n')
                    : $"HRESULT 0x{hr.Value:X8}";
                throw new RenderException($"No se pudo compilar el shader {entryPoint} con d3dcompiler_47.dll: {detail}", hr.Value);
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
