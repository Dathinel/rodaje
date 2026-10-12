using System.Numerics;
using Rodaje.Core.Effects;
using Rodaje.Playback.Composition;
using TerraFX.Interop.DirectX;
using static Rodaje.Render.RenderException;

namespace Rodaje.Render.Effects;

/// <summary>
/// Lo que lee <c>PsMask</c> además de sus constantes: los puntos de un trazo libre (una fila RG32F) y el cuadro del video
/// gris de una máscara de imagen (R8). Los trazos leídos se guardan por texto.
/// </summary>
/// <remarks>Solo se usa desde el hilo que compone, con el candado del contexto tomado.</remarks>
internal sealed unsafe class MaskTextures(ID3D11Device* device, ID3D11DeviceContext* context) : IDisposable
{
    private const int MaxCachedPaths = 64;
    private readonly Dictionary<string, Vector2[]> _paths = new(StringComparer.Ordinal);
    private ID3D11Texture2D* _points;
    private ID3D11ShaderResourceView* _pointsView;
    private ID3D11Texture2D* _matte;
    private ID3D11ShaderResourceView* _matteView;
    private int _matteWidth;
    private int _matteHeight;
    private MatteFrame? _uploaded;

    /// <summary>Los puntos del trazo; vacío si el texto no sirve.</summary>
    public Vector2[] Path(string text)
    {
        if (!_paths.TryGetValue(text, out Vector2[]? points))
        {
            if (_paths.Count >= MaxCachedPaths)
            {
                _paths.Clear();
            }

            points = MaskGeometry.ParsePath(text);
            _paths[text] = points;
        }

        return points;
    }

    /// <summary>Sube los puntos y devuelve la vista para <c>t8</c>.</summary>
    public ID3D11ShaderResourceView* UploadPath(ReadOnlySpan<Vector2> points)
    {
        if (_points == null)
        {
            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = MaskGeometry.MaxPathPoints,
                Height = 1,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI_FORMAT.DXGI_FORMAT_R32G32_FLOAT,
                SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
                Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
                BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
                CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
            };
            ID3D11Texture2D* texture;
            Check(device->CreateTexture2D(&desc, null, &texture), "CreateTexture2D(trazo)");
            _points = texture;
            ID3D11ShaderResourceView* view;
            Check(device->CreateShaderResourceView((ID3D11Resource*)texture, null, &view), "CreateShaderResourceView(trazo)");
            _pointsView = view;
        }

        D3D11_MAPPED_SUBRESOURCE mapped;
        Check(context->Map((ID3D11Resource*)_points, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map(trazo)");
        points[..Math.Min(points.Length, MaskGeometry.MaxPathPoints)].CopyTo(new Span<Vector2>(mapped.pData, MaskGeometry.MaxPathPoints));
        context->Unmap((ID3D11Resource*)_points, 0);
        return _pointsView;
    }

    /// <summary>Sube el cuadro de la máscara de imagen si cambió y devuelve la vista para <c>t9</c>.</summary>
    public ID3D11ShaderResourceView* UploadMatte(MatteFrame frame)
    {
        if (ReferenceEquals(frame, _uploaded) || (_uploaded is not null && _uploaded.Stamp == frame.Stamp && _matteWidth == frame.Width
            && _matteHeight == frame.Height))
        {
            return _matteView;
        }

        if (_matte == null || _matteWidth != frame.Width || _matteHeight != frame.Height)
        {
            Com.Release(ref _matteView);
            Com.Release(ref _matte);
            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)frame.Width,
                Height = (uint)frame.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI_FORMAT.DXGI_FORMAT_R8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
                Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
                BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
                CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
            };
            ID3D11Texture2D* texture;
            Check(device->CreateTexture2D(&desc, null, &texture), "CreateTexture2D(máscara)");
            _matte = texture;
            ID3D11ShaderResourceView* view;
            Check(device->CreateShaderResourceView((ID3D11Resource*)texture, null, &view), "CreateShaderResourceView(máscara)");
            _matteView = view;
            _matteWidth = frame.Width;
            _matteHeight = frame.Height;
        }

        D3D11_MAPPED_SUBRESOURCE mapped;
        Check(context->Map((ID3D11Resource*)_matte, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map(máscara)");
        for (int y = 0; y < frame.Height; y++)
        {
            frame.Gray.AsSpan(y * frame.Width, frame.Width).CopyTo(new Span<byte>((byte*)mapped.pData + ((nint)y * mapped.RowPitch), frame.Width));
        }

        context->Unmap((ID3D11Resource*)_matte, 0);
        _uploaded = frame;
        return _matteView;
    }

    public void Dispose()
    {
        Com.Release(ref _pointsView);
        Com.Release(ref _points);
        Com.Release(ref _matteView);
        Com.Release(ref _matte);
        _uploaded = null;
    }
}
