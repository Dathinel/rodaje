using System.Runtime.InteropServices;
using Rodaje.Media.Gpu;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace Rodaje.Render.Tests.Support;

/// <summary>Imagen 4:2:0 en la CPU con códigos de 8 o 10 bits, lista para subir como una capa de textura NV12 o P010.</summary>
internal sealed class YuvImage
{
    private readonly ushort[] _luma;
    private readonly ushort[] _chroma;

    /// <param name="codedWidth">Ancho de la textura (par); la parte visible la decide quien crea la capa.</param>
    public YuvImage(int codedWidth, int codedHeight, bool tenBit)
    {
        if (codedWidth % 2 != 0 || codedHeight % 2 != 0)
        {
            throw new ArgumentException("NV12 y P010 necesitan dimensiones pares.");
        }

        Width = codedWidth;
        Height = codedHeight;
        TenBit = tenBit;
        _luma = new ushort[codedWidth * codedHeight];
        _chroma = new ushort[codedWidth * codedHeight / 2];
    }

    public int Width { get; }

    public int Height { get; }

    public bool TenBit { get; }

    public static YuvImage Solid(int width, int height, bool tenBit, YuvCode code)
    {
        var image = new YuvImage(width, height, tenBit);
        image.Fill(0, 0, width, height, code);
        return image;
    }

    /// <summary>Pinta un rectángulo de coordenadas pares, para que la croma quede alineada.</summary>
    public YuvImage Fill(int x, int y, int width, int height, YuvCode code)
    {
        for (int row = y; row < y + height; row++)
        {
            _luma.AsSpan((row * Width) + x, width).Fill((ushort)code.Y);
        }

        for (int row = y / 2; row < (y + height + 1) / 2; row++)
        {
            for (int column = x / 2; column < (x + width + 1) / 2; column++)
            {
                int i = (row * Width) + (2 * column);
                _chroma[i] = (ushort)code.Cb;
                _chroma[i + 1] = (ushort)code.Cr;
            }
        }

        return this;
    }

    /// <summary>Crea un arreglo de texturas con una capa por imagen. Devuelve una referencia que hay que liberar.</summary>
    public static unsafe nint CreateTexture(GpuDevice gpu, IReadOnlyList<YuvImage> slices)
    {
        YuvImage first = slices[0];
        bool tenBit = first.TenBit;
        int bytesPerSample = tenBit ? 2 : 1;
        int pitch = first.Width * bytesPerSample;
        int sliceBytes = pitch * first.Height * 3 / 2;
        byte* memory = (byte*)NativeMemory.Alloc((nuint)(sliceBytes * slices.Count));
        var initial = new D3D11_SUBRESOURCE_DATA[slices.Count];
        try
        {
            for (int s = 0; s < slices.Count; s++)
            {
                YuvImage image = slices[s];
                if (image.Width != first.Width || image.Height != first.Height || image.TenBit != tenBit)
                {
                    throw new ArgumentException("Todas las capas deben tener el mismo tamaño y profundidad.", nameof(slices));
                }

                byte* data = memory + ((long)s * sliceBytes);
                image.Pack(data);
                initial[s] = new D3D11_SUBRESOURCE_DATA { pSysMem = data, SysMemPitch = (uint)pitch };
            }

            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)first.Width,
                Height = (uint)first.Height,
                MipLevels = 1,
                ArraySize = (uint)slices.Count,
                Format = tenBit ? DXGI_FORMAT.DXGI_FORMAT_P010 : DXGI_FORMAT.DXGI_FORMAT_NV12,
                SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
            };
            var device = (ID3D11Device*)gpu.NativeDevice;
            ID3D11Texture2D* texture;
            if (slices.Count == 1)
            {
                fixed (D3D11_SUBRESOURCE_DATA* init = initial)
                {
                    Check(device->CreateTexture2D(&desc, init, &texture));
                }

                return (nint)texture;
            }

            // Un arreglo NV12/P010 solo existe como pool de decodificador (D3D11_BIND_DECODER), sin contenido inicial:
            // cada capa se sube sola y se copia, igual que el pool que llena D3D11VA.
            D3D11_TEXTURE2D_DESC arrayDesc = desc;
            arrayDesc.BindFlags |= (uint)D3D11_BIND_FLAG.D3D11_BIND_DECODER;
            Check(device->CreateTexture2D(&arrayDesc, null, &texture));
            var context = (ID3D11DeviceContext*)gpu.NativeContext;
            desc.ArraySize = 1;
            try
            {
                for (int s = 0; s < slices.Count; s++)
                {
                    ID3D11Texture2D* single;
                    D3D11_SUBRESOURCE_DATA init = initial[s];
                    Check(device->CreateTexture2D(&desc, &init, &single));
                    gpu.LockContext();
                    try
                    {
                        context->CopySubresourceRegion((ID3D11Resource*)texture, (uint)s, 0, 0, 0, (ID3D11Resource*)single, 0, null);
                    }
                    finally
                    {
                        gpu.UnlockContext();
                        single->Release();
                    }
                }
            }
            catch
            {
                texture->Release();
                throw;
            }

            return (nint)texture;
        }
        finally
        {
            NativeMemory.Free(memory);
        }
    }

    private static void Check(HRESULT hr)
    {
        if (hr.FAILED)
        {
            throw new InvalidOperationException($"Direct3D falló con 0x{hr.Value:X8}.");
        }
    }

    private unsafe void Pack(byte* destination)
    {
        int samples = Width * Height;
        if (TenBit)
        {
            var words = (ushort*)destination;
            for (int i = 0; i < samples; i++)
            {
                words[i] = (ushort)(_luma[i] << 6);
            }

            for (int i = 0; i < _chroma.Length; i++)
            {
                words[samples + i] = (ushort)(_chroma[i] << 6);
            }

            return;
        }

        for (int i = 0; i < samples; i++)
        {
            destination[i] = (byte)_luma[i];
        }

        for (int i = 0; i < _chroma.Length; i++)
        {
            destination[samples + i] = (byte)_chroma[i];
        }
    }
}
