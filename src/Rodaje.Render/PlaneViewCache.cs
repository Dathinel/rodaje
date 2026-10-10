using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using TerraFX.Interop.DirectX;
using static Rodaje.Render.RenderException;

namespace Rodaje.Render;

/// <summary>Vistas Y y UV de una capa de textura NV12 o P010.</summary>
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
/// cuadro. Cada vista retiene su textura, así que una entrada que no se usa en <see cref="MaxIdleFrames"/> cuadros se
/// suelta para no sostener el pool de un decodificador ya cerrado.
/// </summary>
internal sealed unsafe class PlaneViewCache(ID3D11Device* device) : IDisposable
{
    public const int MaxIdleFrames = 120;
    private const int MaxEntries = 512;

    private readonly Dictionary<(nint Texture, int Slice), Entry> _entries = [];
    private long _frame;

    public int Count => _entries.Count;

    /// <summary>Vistas para <paramref name="payload"/>; <paramref name="owned"/> indica que quien llama debe liberarlas.</summary>
    public PlaneViews Get(D3D11TexturePayload payload, out bool owned)
    {
        if (!payload.IsDecoderSurface)
        {
            owned = true;
            return Create(payload);
        }

        owned = false;
        var key = (payload.Texture, payload.ArraySlice);
        if (_entries.TryGetValue(key, out Entry entry))
        {
            _entries[key] = entry with { LastUsed = _frame };
            return entry.Views;
        }

        if (_entries.Count >= MaxEntries)
        {
            Clear();
        }

        PlaneViews views = Create(payload);
        _entries.Add(key, new Entry(views, _frame));
        return views;
    }

    /// <summary>Cierra un cuadro compuesto y suelta las vistas que llevan demasiado sin usarse.</summary>
    public void EndFrame()
    {
        _frame++;
        if (_frame % 30 != 0)
        {
            return;
        }

        List<(nint, int)>? stale = null;
        foreach (KeyValuePair<(nint, int), Entry> pair in _entries)
        {
            if (_frame - pair.Value.LastUsed > MaxIdleFrames)
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
        FramePixelFormat.P010 => (DXGI_FORMAT.DXGI_FORMAT_R16_UNORM, DXGI_FORMAT.DXGI_FORMAT_R16G16_UNORM),
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

    private readonly record struct Entry(PlaneViews Views, long LastUsed);
}
