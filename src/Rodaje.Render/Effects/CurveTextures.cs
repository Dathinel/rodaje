using Rodaje.Core.Effects;
using TerraFX.Interop.DirectX;
using static Rodaje.Render.RenderException;

namespace Rodaje.Render.Effects;

/// <summary>
/// La tabla de <c>rodaje.curves</c> para <c>t10</c>: dos filas RGBA16F de <see cref="ToneCurve.TableSize"/> (las curvas de
/// tono por canal y, debajo, tono contra tono, tono contra saturación y luma contra saturación) que se vuelven a subir solo
/// si cambian las curvas. Las tablas armadas se guardan por clave.
/// </summary>
/// <remarks>Solo desde el hilo que compone, con el candado del contexto tomado.</remarks>
internal sealed unsafe class CurveTextures(ID3D11Device* device, ID3D11DeviceContext* context) : IDisposable
{
    private const int MaxCached = 32;
    private const int Rows = 2;
    private readonly Dictionary<string, Half[]> _tables = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _hue = new(StringComparer.Ordinal);
    private ID3D11Texture2D* _texture;
    private ID3D11ShaderResourceView* _view;
    private string? _uploaded;

    public ID3D11ShaderResourceView* Upload(string key)
    {
        if (_view != null && _uploaded == key)
        {
            return _view;
        }

        if (!_tables.TryGetValue(key, out Half[]? table))
        {
            if (_tables.Count >= MaxCached)
            {
                _tables.Clear();
            }

            table = Build(key);
            _tables[key] = table;
        }

        if (_texture == null)
        {
            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = ToneCurve.TableSize,
                Height = Rows,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT,
                SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
                Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
                BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
                CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
            };
            ID3D11Texture2D* texture;
            Check(device->CreateTexture2D(&desc, null, &texture), "CreateTexture2D(curvas)");
            _texture = texture;
            ID3D11ShaderResourceView* view;
            Check(device->CreateShaderResourceView((ID3D11Resource*)texture, null, &view), "CreateShaderResourceView(curvas)");
            _view = view;
        }

        D3D11_MAPPED_SUBRESOURCE mapped;
        Check(context->Map((ID3D11Resource*)_texture, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map(curvas)");
        int row = ToneCurve.TableSize * 4;
        for (int r = 0; r < Rows; r++)
        {
            table.AsSpan(r * row, row).CopyTo(new Span<Half>((byte*)mapped.pData + (r * mapped.RowPitch), row));
        }

        context->Unmap((ID3D11Resource*)_texture, 0);
        _uploaded = key;
        return _view;
    }

    /// <summary>Si la clave trae curvas de tono o saturación: el shader las aplica solo entonces (<c>qual3.w</c>).</summary>
    public bool HasHueCurves(string key)
    {
        if (!_hue.TryGetValue(key, out bool has))
        {
            if (_hue.Count >= MaxCached)
            {
                _hue.Clear();
            }

            has = ToneCurve.HasHueCurves(key);
            _hue[key] = has;
        }

        return has;
    }

    private static Half[] Build(string key)
    {
        float[] rgb = ToneCurve.Table(key);
        float[] hue = ToneCurve.HueTable(key);
        int n = ToneCurve.TableSize;
        var table = new Half[n * 4 * Rows];
        for (int i = 0; i < n; i++)
        {
            for (int c = 0; c < 3; c++)
            {
                table[(i * 4) + c] = (Half)rgb[(i * 3) + c];
                table[(n * 4) + (i * 4) + c] = (Half)hue[(i * 3) + c];
            }

            table[(i * 4) + 3] = (Half)1f;
            table[(n * 4) + (i * 4) + 3] = (Half)1f;
        }

        return table;
    }

    public void Dispose()
    {
        Com.Release(ref _view);
        Com.Release(ref _texture);
        _uploaded = null;
    }
}
