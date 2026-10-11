using TerraFX.Interop.DirectX;
using static Rodaje.Render.RenderException;

namespace Rodaje.Render;

/// <summary>Una textura con vista de destino y de lectura. <see cref="Width"/> y <see cref="Height"/> son los de la textura.</summary>
internal sealed unsafe class RenderTarget : IDisposable
{
    private ID3D11Texture2D* _texture;
    private ID3D11RenderTargetView* _rtv;
    private ID3D11ShaderResourceView* _srv;

    public RenderTarget(ID3D11Texture2D* texture, ID3D11RenderTargetView* rtv, ID3D11ShaderResourceView* srv,
        int width, int height, DXGI_FORMAT format, long bytes)
    {
        _texture = texture;
        _rtv = rtv;
        _srv = srv;
        Width = width;
        Height = height;
        Format = format;
        Bytes = bytes;
    }

    public ID3D11Texture2D* Texture => _texture;

    public ID3D11RenderTargetView* Rtv => _rtv;

    public ID3D11ShaderResourceView* Srv => _srv;

    public int Width { get; }

    public int Height { get; }

    public DXGI_FORMAT Format { get; }

    public long Bytes { get; }

    internal bool InUse { get; set; }

    internal long LastUsedFrame { get; set; }

    public static RenderTarget Create(ID3D11Device* device, int width, int height, DXGI_FORMAT format)
    {
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE),
        };
        ID3D11Texture2D* texture;
        Check(device->CreateTexture2D(&desc, null, &texture), "CreateTexture2D(intermedio)");
        ID3D11RenderTargetView* rtv = null;
        ID3D11ShaderResourceView* srv = null;
        try
        {
            Check(device->CreateRenderTargetView((ID3D11Resource*)texture, null, &rtv), "CreateRenderTargetView(intermedio)");
            Check(device->CreateShaderResourceView((ID3D11Resource*)texture, null, &srv), "CreateShaderResourceView(intermedio)");
        }
        catch
        {
            Com.Release(ref rtv);
            Com.Release(ref texture);
            throw;
        }

        return new RenderTarget(texture, rtv, srv, width, height, format, BytesFor(width, height, format));
    }

    public static long BytesFor(int width, int height, DXGI_FORMAT format) =>
        (long)width * height * (format == DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT ? 8 : 4);

    public void Dispose()
    {
        Com.Release(ref _srv);
        Com.Release(ref _rtv);
        Com.Release(ref _texture);
    }
}

/// <summary>
/// Intermedios de los efectos, reutilizados cuadro tras cuadro, con un tope de memoria que no entra en el presupuesto
/// de los decodificadores. Si una textura no cabe, <see cref="Rent"/> devuelve <c>null</c> y el efecto se salta.
/// </summary>
/// <remarks>
/// Los tamaños se redondean a 64 px para que una capa que cambia de escala no cree una textura por cuadro; quien la usa
/// dibuja en la parte que pidió. Solo se usa desde el hilo que compone, con el candado del contexto tomado.
/// </remarks>
internal sealed unsafe class RenderTargetPool : IDisposable
{
    public const long MaxCapBytes = 300L * 1024 * 1024;
    public const long MinCapBytes = 96L * 1024 * 1024;
    private const int Granularity = 64;
    private const int IdleFrames = 240;

    private readonly ID3D11Device* _device;
    private readonly List<RenderTarget> _targets = [];
    private long _frame;

    public RenderTargetPool(ID3D11Device* device, long capBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capBytes);
        _device = device;
        CapBytes = capBytes;
    }

    public long CapBytes { get; }

    /// <summary>Memoria de todas las texturas del pool, en uso o libres.</summary>
    public long Bytes { get; private set; }

    public int Count => _targets.Count;

    /// <summary>Tope según la VRAM dedicada: 7,5 %, entre 96 y 300 MB (300 MB con 4 GB, 150 MB con 2 GB).</summary>
    public static long CapFor(long dedicatedVideoMemory) =>
        Math.Clamp((long)(dedicatedVideoMemory * 0.075), MinCapBytes, MaxCapBytes);

    /// <summary>Una textura de al menos <paramref name="width"/>×<paramref name="height"/>; <c>null</c> si no cabe en el tope.</summary>
    /// <param name="exact">Sin redondear el tamaño (los lienzos de transición, que siempre miden lo mismo).</param>
    public RenderTarget? Rent(int width, int height, DXGI_FORMAT format, bool exact = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        int w = exact ? width : RoundUp(width);
        int h = exact ? height : RoundUp(height);
        foreach (RenderTarget target in _targets)
        {
            if (!target.InUse && target.Width == w && target.Height == h && target.Format == format)
            {
                target.InUse = true;
                return target;
            }
        }

        long bytes = RenderTarget.BytesFor(w, h, format);
        while (Bytes + bytes > CapBytes && EvictOldestFree())
        {
        }

        if (Bytes + bytes > CapBytes)
        {
            return null;
        }

        RenderTarget created = RenderTarget.Create(_device, w, h, format);
        created.InUse = true;
        _targets.Add(created);
        Bytes += created.Bytes;
        return created;
    }

    public void Return(RenderTarget? target)
    {
        if (target is null)
        {
            return;
        }

        target.InUse = false;
        target.LastUsedFrame = _frame;
    }

    /// <summary>Cierra un cuadro: suelta las texturas libres que llevan muchos cuadros sin usarse.</summary>
    public void EndFrame()
    {
        _frame++;
        for (int i = _targets.Count - 1; i >= 0; i--)
        {
            RenderTarget target = _targets[i];
            if (!target.InUse && _frame - target.LastUsedFrame > IdleFrames)
            {
                Remove(i);
            }
        }
    }

    /// <summary>Suelta todas las texturas libres (en pausa).</summary>
    public void Trim()
    {
        for (int i = _targets.Count - 1; i >= 0; i--)
        {
            if (!_targets[i].InUse)
            {
                Remove(i);
            }
        }
    }

    public void Dispose()
    {
        foreach (RenderTarget target in _targets)
        {
            target.Dispose();
        }

        _targets.Clear();
        Bytes = 0;
    }

    private static int RoundUp(int size) => (size + Granularity - 1) / Granularity * Granularity;

    private bool EvictOldestFree()
    {
        int oldest = -1;
        for (int i = 0; i < _targets.Count; i++)
        {
            if (!_targets[i].InUse && (oldest < 0 || _targets[i].LastUsedFrame < _targets[oldest].LastUsedFrame))
            {
                oldest = i;
            }
        }

        if (oldest < 0)
        {
            return false;
        }

        Remove(oldest);
        return true;
    }

    private void Remove(int index)
    {
        RenderTarget target = _targets[index];
        _targets.RemoveAt(index);
        Bytes -= target.Bytes;
        target.Dispose();
    }
}
