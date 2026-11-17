using System.Numerics;
using System.Runtime.InteropServices;
using Rodaje.Core.Effects;
using Rodaje.Playback.Composition;
using Rodaje.Render.Gpu;

namespace Rodaje.Render.Effects;

/// <summary>
/// Lo que lee <c>PsMask</c> además de sus constantes: la polilínea de un contorno de vértices (una fila RGBA32F: x, y y
/// peso) y el cuadro del video gris de una máscara de imagen (R8). Las formas leídas se guardan por texto.
/// </summary>
/// <remarks>Solo se usa desde el hilo que compone, con el candado del contexto tomado.</remarks>
internal sealed class MaskTextures(IGpu gpu) : IDisposable
{
    private const int MaxCachedShapes = 64;
    private readonly Dictionary<string, MaskShapeTrack?> _shapes = new(StringComparer.Ordinal);
    private readonly MaskVertex[] _vertices = new MaskVertex[MaskPath.MaxVertices];
    private readonly Vector3[] _polyline = new Vector3[MaskPath.MaxPolyline];
    private readonly Vector4[] _upload = new Vector4[MaskPath.MaxPolyline];
    private GpuTexture? _points;
    private GpuTexture? _matte;
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

    /// <summary>Sube la polilínea y devuelve la textura para <c>t8</c>.</summary>
    public GpuTexture UploadPolyline(ReadOnlySpan<Vector3> points)
    {
        _points ??= gpu.CreateTexture(new GpuTextureDesc(MaskPath.MaxPolyline, 1, GpuFormat.Rgba32Float, GpuTextureUsage.Sampled));
        int n = Math.Min(points.Length, MaskPath.MaxPolyline);
        for (int i = 0; i < n; i++)
        {
            _upload[i] = new Vector4(points[i], 0);
        }

        using (IGpuFrame frame = gpu.BeginFrame())
        {
            frame.Write(_points, MemoryMarshal.AsBytes(_upload.AsSpan()),
                MaskPath.MaxPolyline * GpuFormats.BytesPerPixel(GpuFormat.Rgba32Float));
        }

        return _points;
    }

    /// <summary>Sube el cuadro de la máscara de imagen si cambió y devuelve la textura para <c>t9</c>.</summary>
    public GpuTexture? UploadMatte(MatteFrame frame)
    {
        if (ReferenceEquals(frame, _uploaded) || (_uploaded is not null && _uploaded.Stamp == frame.Stamp && _matteWidth == frame.Width
            && _matteHeight == frame.Height))
        {
            return _matte;
        }

        if (_matte is null || _matteWidth != frame.Width || _matteHeight != frame.Height)
        {
            _matte?.Dispose();
            _matte = gpu.CreateTexture(new GpuTextureDesc(frame.Width, frame.Height, GpuFormat.R8Unorm, GpuTextureUsage.Sampled));
            _matteWidth = frame.Width;
            _matteHeight = frame.Height;
        }

        using (IGpuFrame upload = gpu.BeginFrame())
        {
            upload.Write(_matte, frame.Gray.AsSpan(0, frame.Width * frame.Height), frame.Width);
        }

        _uploaded = frame;
        return _matte;
    }

    public void Dispose()
    {
        _points?.Dispose();
        _points = null;
        _matte?.Dispose();
        _matte = null;
        _uploaded = null;
    }
}
