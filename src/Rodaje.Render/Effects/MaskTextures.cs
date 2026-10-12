using System.Numerics;
using Rodaje.Core.Effects;
using Rodaje.Playback.Composition;
using TerraFX.Interop.DirectX;
using static Rodaje.Render.RenderException;

namespace Rodaje.Render.Effects;

/// <summary>
/// Lo que lee <c>PsMask</c> además de sus constantes: la polilínea de un contorno de vértices (una fila RGBA32F: x, y y
/// peso) y el cuadro del video gris de una máscara de imagen (R8). Las formas leídas se guardan por texto.
/// </summary>
/// <remarks>Solo se usa desde el hilo que compone, con el candado del contexto tomado.</remarks>
internal sealed unsafe class MaskTextures(ID3D11Device* device, ID3D11DeviceContext* context) : IDisposable
{
    private const int MaxCachedShapes = 64;
    private readonly Dictionary<string, MaskShapeTrack?> _shapes = new(StringComparer.Ordinal);
    private readonly MaskVertex[] _vertices = new MaskVertex[MaskPath.MaxVertices];
    private readonly Vector3[] _polyline = new Vector3[MaskPath.MaxPolyline];
    private ID3D11Texture2D* _points;
    private ID3D11ShaderResourceView* _pointsView;
    private ID3D11Texture2D* _matte;
    private ID3D11ShaderResourceView* _matteView;
    private int _matteWidth;
    private int _matteHeight;
    private MatteFrame? _uploaded;

    /// <summary>
    /// El contorno de <paramref name="text"/> (vértices fijos, forma animada o trazo de antes) a los <paramref name="seconds"/>
    /// del origen, aplanado para una caja de <paramref name="boxWidth"/> × <paramref name="boxHeight"/> píxeles de la imagen.
    /// Vacío si el texto no sirve.
    /// </summary>
    public ReadOnlySpan<Vector3> Polyline(string text, double seconds, MaskShape shape, float boxWidth, float boxHeight, float featherPixels)
    {
        if (!_shapes.TryGetValue(text, out MaskShapeTrack? track))
        {
            if (_shapes.Count >= MaxCachedShapes)
            {
                _shapes.Clear();
            }

            track = shape == MaskShape.Path && MaskGeometry.ParsePath(text).Length < 3 ? null : MaskShapeTrack.Parse(text);
            _shapes[text] = track;
        }

        if (track is null)
        {
            return [];
        }

        int vertices = track.Evaluate(seconds, _vertices);
        int count = MaskPath.Flatten(_vertices.AsSpan(0, vertices), shape != MaskShape.Stroke, boxWidth, boxHeight, _polyline,
            MaskPath.ToleranceFor(featherPixels));
        return _polyline.AsSpan(0, count);
    }

    /// <summary>Sube la polilínea y devuelve la vista para <c>t8</c>.</summary>
    public ID3D11ShaderResourceView* UploadPolyline(ReadOnlySpan<Vector3> points)
    {
        if (_points == null)
        {
            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = MaskPath.MaxPolyline,
                Height = 1,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_FLOAT,
                SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
                Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
                BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
                CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
            };
            ID3D11Texture2D* texture;
            Check(device->CreateTexture2D(&desc, null, &texture), "CreateTexture2D(contorno)");
            _points = texture;
            ID3D11ShaderResourceView* view;
            Check(device->CreateShaderResourceView((ID3D11Resource*)texture, null, &view), "CreateShaderResourceView(contorno)");
            _pointsView = view;
        }

        D3D11_MAPPED_SUBRESOURCE mapped;
        Check(context->Map((ID3D11Resource*)_points, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped), "Map(contorno)");
        var output = new Span<Vector4>(mapped.pData, MaskPath.MaxPolyline);
        int n = Math.Min(points.Length, MaskPath.MaxPolyline);
        for (int i = 0; i < n; i++)
        {
            output[i] = new Vector4(points[i], 0);
        }

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
