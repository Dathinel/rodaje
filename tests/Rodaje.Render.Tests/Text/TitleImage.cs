using Rodaje.Core.Model;
using Rodaje.Media.Gpu;
using Rodaje.Playback.Composition;
using Rodaje.Render.Text;
using TerraFX.Interop.DirectX;

namespace Rodaje.Render.Tests.Text;

/// <summary>Un título rasterizado leído a la CPU (BGRA premultiplicado), con medidas para comparar contra lo esperado.</summary>
internal sealed unsafe class TitleImage
{
    private TitleImage(byte[] bgra, int width, int height)
    {
        Bgra = bgra;
        Width = width;
        Height = height;
    }

    public byte[] Bgra { get; }

    public int Width { get; }

    public int Height { get; }

    public (byte B, byte G, byte R, byte A) this[int x, int y]
    {
        get
        {
            int i = ((y * Width) + x) * 4;
            return (Bgra[i], Bgra[i + 1], Bgra[i + 2], Bgra[i + 3]);
        }
    }

    /// <summary>Un título como lo arma el motor, en una secuencia de 1920×1080 con la referencia del estilo igual.</summary>
    public static GeneratedContent Content(string text, TextStyle? style = null, double boxWidth = 0, double scale = 1, int width = 1920,
        int height = 1080) =>
        new(GeneratorKinds.Text, text, style ?? new TextStyle(), boxWidth, width, height, scale) { ClipId = "titulo" };

    public static TitleImage Read(GpuDevice gpu, TitleTexture title)
    {
        var device = (ID3D11Device*)gpu.NativeDevice;
        var context = (ID3D11DeviceContext*)gpu.NativeContext;
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)title.Width,
            Height = (uint)title.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
            Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
        };
        ID3D11Texture2D* staging;
        Assert.True(device->CreateTexture2D(&desc, null, &staging).SUCCEEDED);
        var pixels = new byte[title.Width * title.Height * 4];
        gpu.LockContext();
        try
        {
            context->CopyResource((ID3D11Resource*)staging, (ID3D11Resource*)title.Texture);
            D3D11_MAPPED_SUBRESOURCE mapped;
            Assert.True(context->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped).SUCCEEDED);
            for (int y = 0; y < title.Height; y++)
            {
                new ReadOnlySpan<byte>((byte*)mapped.pData + ((nuint)y * mapped.RowPitch), title.Width * 4)
                    .CopyTo(pixels.AsSpan(y * title.Width * 4));
            }

            context->Unmap((ID3D11Resource*)staging, 0);
        }
        finally
        {
            gpu.UnlockContext();
            staging->Release();
        }

        return new TitleImage(pixels, title.Width, title.Height);
    }

    /// <summary>Píxeles que cumplen <paramref name="predicate"/>.</summary>
    public int Count(Func<(byte B, byte G, byte R, byte A), bool> predicate)
    {
        int count = 0;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                count += predicate(this[x, y]) ? 1 : 0;
            }
        }

        return count;
    }

    /// <summary>Centro de masa del alfa: dónde cae la tinta.</summary>
    public (double X, double Y) InkCenter()
    {
        double sum = 0;
        double sx = 0;
        double sy = 0;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                double a = this[x, y].A;
                sum += a;
                sx += a * x;
                sy += a * y;
            }
        }

        return sum == 0 ? (double.NaN, double.NaN) : (sx / sum, sy / sum);
    }

    /// <summary>Filas con algo de tinta: la altura del texto dibujado.</summary>
    public (int Top, int Bottom) InkRows()
    {
        int top = -1;
        int bottom = -1;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                if (this[x, y].A > 0)
                {
                    top = top < 0 ? y : top;
                    bottom = y;
                    break;
                }
            }
        }

        return (top, bottom);
    }
}
