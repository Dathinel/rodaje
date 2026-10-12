using Rodaje.Core.Effects;
using TerraFX.Interop.DirectX;
using static Rodaje.Render.RenderException;

namespace Rodaje.Render.Effects;

/// <summary>
/// La tabla de <c>rodaje.curves</c> para <c>t10</c>: una fila RGBA16F de <see cref="ToneCurve.TableSize"/> que se vuelve a
/// subir solo si cambian las curvas. Las tablas armadas se guardan por clave.
/// </summary>
/// <remarks>Solo desde el hilo que compone, con el candado del contexto tomado.</remarks>
internal sealed unsafe class CurveTextures(ID3D11Device* device, ID3D11DeviceContext* context) : IDisposable
{
    private const int MaxCached = 32;
    private readonly Dictionary<string, Half[]> _tables = new(StringComparer.Ordinal);
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

            float[] rgb = ToneCurve.Table(key);
            table = new Half[ToneCurve.TableSize * 4];
            for (int i = 0; i < ToneCurve.TableSize; i++)
            {
                table[(i * 4) + 0] = (Half)rgb[(i * 3) + 0];
                table[(i * 4) + 1] = (Half)rgb[(i * 3) + 1];
                table[(i * 4) + 2] = (Half)rgb[(i * 3) + 2];
                table[(i * 4) + 3] = (Half)1f;
            }

            _tables[key] = table;
        }

        if (_texture == null)
        {
            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = ToneCurve.TableSize,
                Height = 1,
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
        table.AsSpan().CopyTo(new Span<Half>(mapped.pData, table.Length));
        context->Unmap((ID3D11Resource*)_texture, 0);
        _uploaded = key;
        return _view;
    }

    public void Dispose()
    {
        Com.Release(ref _view);
        Com.Release(ref _texture);
        _uploaded = null;
    }
}
