using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using TerraFX.Interop.DirectX;
using static Rodaje.Render.RenderException;

namespace Rodaje.Render;

/// <summary>Vistas de una capa NV12/P010/P016 o RGBA; en RGBA solo se muestrea la primera.</summary>
internal readonly unsafe struct PlaneViews(ID3D11ShaderResourceView* luma, ID3D11ShaderResourceView* chroma)
{
    public ID3D11ShaderResourceView* Luma { get; } = luma;

    public ID3D11ShaderResourceView* Chroma { get; } = chroma;

    public void Release()
    {
        Luma->Release();
        Chroma->Release();
    }
}

/// <summary>
/// Crea las vistas de los planos y guarda las de las superficies del decodificador, que se reutilizan cuadro tras
/// cuadro. Cada vista retiene su textura, es decir, todo el pool de su decodificador: las de un decodificador cerrado se
/// sueltan en el siguiente cuadro (o en <see cref="Trim"/>), y las que llevan <see cref="MaxIdle"/> sin usarse, también.
/// </summary>
/// <remarks>Solo se usa desde el hilo que compone, con el candado del contexto tomado.</remarks>
internal sealed unsafe class PlaneViewCache(ID3D11Device* device, TimeProvider? time = null) : IDisposable
{
    public static readonly TimeSpan MaxIdle = TimeSpan.FromSeconds(2);
    private const int MaxEntries = 512;
    private const int AgeCheckFrames = 30;

    private readonly Dictionary<(nint Texture, int Slice), Entry> _entries = [];
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private long _frame;
    private long _closedSeen = DecoderSurfacePool.ClosedCount;

    public int Count => _entries.Count;

    /// <summary>Vistas para <paramref name="payload"/>; <paramref name="owned"/> indica que quien llama debe liberarlas.</summary>
    public PlaneViews Get(D3D11TexturePayload payload, out bool owned)
    {
        if (!payload.IsDecoderSurface && payload.PixelFormat is not (FramePixelFormat.Bgra8 or FramePixelFormat.Rgba16))
        {
            owned = true;
            return Create(payload);
        }

        owned = false;
        var key = (payload.Texture, payload.ArraySlice);
        long now = _time.GetTimestamp();
        if (_entries.TryGetValue(key, out Entry entry))
        {
            _entries[key] = entry with { LastUsed = now };
            return entry.Views;
        }

        if (_entries.Count >= MaxEntries)
        {
            Clear();
        }

        PlaneViews views = Create(payload);
        _entries.Add(key, new Entry(views, now, payload.SurfacePool));
        return views;
    }

    /// <summary>Cierra un cuadro compuesto: suelta las vistas de pools cerrados y, cada tanto, las que llevan mucho sin usarse.</summary>
    public void EndFrame()
    {
        _frame++;
        bool closedChanged = DecoderSurfacePool.ClosedCount != _closedSeen;
        if (closedChanged || _frame % AgeCheckFrames == 0)
        {
            Trim();
        }
    }

    /// <summary>Suelta las vistas de pools cerrados y las que llevan más de <see cref="MaxIdle"/> sin usarse.</summary>
    public void Trim()
    {
        _closedSeen = DecoderSurfacePool.ClosedCount;
        long now = _time.GetTimestamp();
        List<(nint, int)>? stale = null;
        foreach (KeyValuePair<(nint, int), Entry> pair in _entries)
        {
            if (pair.Value.Pool is { IsClosed: true } || _time.GetElapsedTime(pair.Value.LastUsed, now) > MaxIdle)
            {
                (stale ??= []).Add(pair.Key);
            }
        }

        if (stale is null)
        {
            return;
        }

        foreach ((nint, int) key in stale)
        {
            _entries[key].Views.Release();
            _entries.Remove(key);
        }
    }

    public void Clear()
    {
        foreach (Entry entry in _entries.Values)
        {
            entry.Views.Release();
        }

        _entries.Clear();
    }

    public void Dispose() => Clear();
    private static (DXGI_FORMAT Luma, DXGI_FORMAT Chroma) PlaneFormats(FramePixelFormat format) => format switch
    {
        FramePixelFormat.Bgra8 => (DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM),
        FramePixelFormat.Rgba16 => (DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_UNORM, DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_UNORM),
        FramePixelFormat.P010 or FramePixelFormat.P016 => (DXGI_FORMAT.DXGI_FORMAT_R16_UNORM, DXGI_FORMAT.DXGI_FORMAT_R16G16_UNORM),
        _ => (DXGI_FORMAT.DXGI_FORMAT_R8_UNORM, DXGI_FORMAT.DXGI_FORMAT_R8G8_UNORM),
    };

    private PlaneViews Create(D3D11TexturePayload payload)
    {
        (DXGI_FORMAT luma, DXGI_FORMAT chroma) = PlaneFormats(payload.PixelFormat);
        var texture = (ID3D11Resource*)payload.Texture;
        ID3D11ShaderResourceView* lumaView = CreateSliceView(texture, luma, payload.ArraySlice);
        try
        {
            return new PlaneViews(lumaView, CreateSliceView(texture, chroma, payload.ArraySlice));
        }
        catch
        {
            lumaView->Release();
            throw;
        }
    }

    private ID3D11ShaderResourceView* CreateSliceView(ID3D11Resource* texture, DXGI_FORMAT format, int slice)
    {
        var desc = new D3D11_SHADER_RESOURCE_VIEW_DESC
        {
            Format = format,
            ViewDimension = D3D_SRV_DIMENSION.D3D11_SRV_DIMENSION_TEXTURE2DARRAY,
        };
        desc.Texture2DArray.MostDetailedMip = 0;
        desc.Texture2DArray.MipLevels = 1;
        desc.Texture2DArray.FirstArraySlice = (uint)slice;
        desc.Texture2DArray.ArraySize = 1;
        ID3D11ShaderResourceView* view;
        Check(device->CreateShaderResourceView(texture, &desc, &view), "CreateShaderResourceView");
        return view;
    }

    private readonly record struct Entry(PlaneViews Views, long LastUsed, DecoderSurfacePool? Pool);
}
