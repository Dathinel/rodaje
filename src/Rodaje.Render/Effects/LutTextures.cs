using Rodaje.Playback.Composition;
using TerraFX.Interop.DirectX;
using static Rodaje.Render.RenderException;

namespace Rodaje.Render.Effects;

/// <summary>
/// LUT 3D subidas a la GPU como <c>Texture3D</c> RGBA16F, por referencia. Si el proveedor entrega otro
/// <see cref="LutData"/> para la misma referencia, se vuelve a subir.
/// </summary>
/// <remarks>Solo se usa desde el hilo que compone, con el candado del contexto tomado.</remarks>
internal sealed unsafe class LutTextures(ID3D11Device* device) : IDisposable
{
    private const int MaxEntries = 8;
    private const int MinTextureSize = 17;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private long _uses;

    public int Count => _entries.Count;

    public ID3D11ShaderResourceView* Get(ResourceKey key, LutData lut)
    {
        string reference = key.Reference ?? "";
        if (_entries.TryGetValue(reference, out Entry? entry) && ReferenceEquals(entry.Data, lut))
        {
            entry.LastUse = ++_uses;
            return entry.View;
        }

        if (entry is not null)
        {
            entry.Dispose();
            _entries.Remove(reference);
        }
        else if (_entries.Count >= MaxEntries)
        {
            string oldest = _entries.MinBy(e => e.Value.LastUse).Key;
            _entries[oldest].Dispose();
            _entries.Remove(oldest);
        }

        var created = new Entry(lut, Upload(lut)) { LastUse = ++_uses };
        _entries.Add(reference, created);
        return created.View;
    }

    public void Dispose()
    {
        foreach (Entry entry in _entries.Values)
        {
            entry.Dispose();
        }

        _entries.Clear();
    }

    /// <summary>Lado de la textura: una LUT chica se subdivide para que el filtro del hardware no pierda precisión.</summary>
    /// <remarks>
    /// El filtro trilineal pondera con unos 8 bits de fracción: con 2³ eso son 1,5 códigos de error. Subdividir cada celda
    /// en k partes iguales no cambia el resultado, porque la interpolación trilineal reproduce exactamente una función
    /// trilineal dentro de la celda.
    /// </remarks>
    public static int TextureSize(int size) => size >= MinTextureSize ? size : ((size - 1) * (int)Math.Ceiling((MinTextureSize - 1.0) / (size - 1))) + 1;

    private ID3D11ShaderResourceView* Upload(LutData lut)
    {
        int n = TextureSize(lut.Size);
        Half[] texels = Texels(lut, n);

        var desc = new D3D11_TEXTURE3D_DESC
        {
            Width = (uint)n,
            Height = (uint)n,
            Depth = (uint)n,
            MipLevels = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT,
            Usage = D3D11_USAGE.D3D11_USAGE_IMMUTABLE,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
        };
        ID3D11Texture3D* texture;
        fixed (Half* data = texels)
        {
            var initial = new D3D11_SUBRESOURCE_DATA { pSysMem = data, SysMemPitch = (uint)(n * 8), SysMemSlicePitch = (uint)(n * n * 8) };
            Check(device->CreateTexture3D(&desc, &initial, &texture), "CreateTexture3D(LUT)");
        }

        try
        {
            ID3D11ShaderResourceView* view;
            Check(device->CreateShaderResourceView((ID3D11Resource*)texture, null, &view), "CreateShaderResourceView(LUT)");
            return view;
        }
        finally
        {
            texture->Release();
        }
    }

    private static Half[] Texels(LutData lut, int n)
    {
        int size = lut.Size;
        ReadOnlySpan<float> rgb = lut.Rgb.Span;
        var texels = new Half[n * n * n * 4];
        int k = (n - 1) / (size - 1);
        int j = 0;
        for (int b = 0; b < n; b++)
        {
            for (int g = 0; g < n; g++)
            {
                for (int r = 0; r < n; r++, j += 4)
                {
                    for (int channel = 0; channel < 3; channel++)
                    {
                        texels[j + channel] = (Half)Trilinear(rgb, size, (float)r / k, (float)g / k, (float)b / k, channel);
                    }

                    texels[j + 3] = Half.One;
                }
            }
        }

        return texels;
    }

    private static float Trilinear(ReadOnlySpan<float> rgb, int size, float x, float y, float z, int channel)
    {
        int x0 = Math.Min((int)x, size - 2);
        int y0 = Math.Min((int)y, size - 2);
        int z0 = Math.Min((int)z, size - 2);
        float fx = x - x0;
        float fy = y - y0;
        float fz = z - z0;
        float At(ReadOnlySpan<float> data, int i, int j, int l) => data[(3 * (i + (size * (j + (size * l))))) + channel];
        float Row(ReadOnlySpan<float> data, int j, int l) => At(data, x0, j, l) + ((At(data, x0 + 1, j, l) - At(data, x0, j, l)) * fx);
        float Plane(ReadOnlySpan<float> data, int l) => Row(data, y0, l) + ((Row(data, y0 + 1, l) - Row(data, y0, l)) * fy);
        return Plane(rgb, z0) + ((Plane(rgb, z0 + 1) - Plane(rgb, z0)) * fz);
    }

    private sealed class Entry(LutData data, ID3D11ShaderResourceView* view) : IDisposable
    {
        private ID3D11ShaderResourceView* _view = view;

        public LutData Data { get; } = data;

        public ID3D11ShaderResourceView* View => _view;

        public long LastUse { get; set; }

        public void Dispose() => Com.Release(ref _view);
    }
}
