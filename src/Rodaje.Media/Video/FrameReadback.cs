using Rodaje.Media.Gpu;
using Rodaje.Playback;
using ID3D11Texture2D = TerraFX.Interop.DirectX.ID3D11Texture2D;

namespace Rodaje.Media.Video;

/// <summary>Plano de luma en 8 bits, fila tras fila sin relleno.</summary>
public sealed record LumaImage(int Width, int Height, byte[] Pixels);

/// <summary>Copia cuadros decodificados a la CPU, para miniaturas, análisis y pruebas.</summary>
/// <remarks>Lenta a propósito (copia a una textura de staging y espera a la GPU): no es para la reproducción.</remarks>
public static unsafe class FrameReadback
{
    /// <summary>Plano Y del cuadro en 8 bits. Los de 10 bits se reducen descartando los 2 bits bajos.</summary>
    public static LumaImage ReadLuma(DecodedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!frame.TryRetain())
        {
            throw new ObjectDisposedException(nameof(DecodedFrame), "El cuadro ya se liberó.");
        }

        try
        {
            return frame.Payload switch
            {
                D3D11TexturePayload texture => ReadTexture(texture),
                CpuFramePayload cpu => ReadCpu(cpu),
                _ => throw new NotSupportedException("El cuadro no tiene un contenido que Rodaje.Media sepa leer."),
            };
        }
        finally
        {
            frame.Dispose();
        }
    }

    private static LumaImage ReadTexture(D3D11TexturePayload texture)
    {
        if (!texture.AddRef())
        {
            throw new ObjectDisposedException(nameof(D3D11TexturePayload), "La textura del cuadro ya se liberó.");
        }

        try
        {
            ObjectDisposedException.ThrowIf(texture.Device.IsDisposed, texture.Device);
            byte[] pixels = texture.Device.ReadLuma((ID3D11Texture2D*)texture.Texture, (uint)texture.ArraySlice, texture.Width, texture.Height);
            return new LumaImage(texture.Width, texture.Height, pixels);
        }
        finally
        {
            texture.Release();
        }
    }

    private static LumaImage ReadCpu(CpuFramePayload cpu)
    {
        int width = cpu.Width;
        int height = cpu.Height;
        var pixels = new byte[width * height];
        ReadOnlySpan<byte> plane = cpu.GetPlane(0, out int stride);
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = plane.Slice(y * stride);
            Span<byte> target = pixels.AsSpan(y * width, width);
            switch (cpu.PixelFormat)
            {
                case FramePixelFormat.Yuv420P or FramePixelFormat.Nv12:
                    row[..width].CopyTo(target);
                    break;
                case FramePixelFormat.Yuv420P10:
                    for (int x = 0; x < width; x++)
                    {
                        target[x] = (byte)((row[2 * x] | (row[(2 * x) + 1] << 8)) >> 2);
                    }

                    break;
                case FramePixelFormat.P010:
                    for (int x = 0; x < width; x++)
                    {
                        target[x] = row[(2 * x) + 1];
                    }

                    break;
                default:
                    throw new NotSupportedException($"La lectura de luma no admite el formato {cpu.FfmpegPixelFormat}.");
            }
        }

        return new LumaImage(width, height, pixels);
    }
}
