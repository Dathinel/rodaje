using System.Runtime.InteropServices;
using Rodaje.Core.Effects;
using Rodaje.Render.Gpu;

namespace Rodaje.Render.Effects;

/// <summary>
/// La tabla de <c>rodaje.curves</c> para <c>t10</c>: dos filas RGBA16F de <see cref="ToneCurve.TableSize"/> (las curvas de
/// tono por canal y, debajo, tono contra tono, tono contra saturación y luma contra saturación) que se vuelven a subir solo
/// si cambian las curvas. Las tablas armadas se guardan por clave.
/// </summary>
/// <remarks>Solo desde el hilo que compone, con el candado del contexto tomado.</remarks>
internal sealed class CurveTextures(IGpu gpu) : IDisposable
{
    private const int MaxCached = 32;
    private const int Rows = 2;
    private readonly Dictionary<string, Half[]> _tables = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _hue = new(StringComparer.Ordinal);
    private GpuTexture? _texture;
    private string? _uploaded;

    public GpuTexture Upload(string key)
    {
        if (_texture is not null && _uploaded == key)
        {
            return _texture;
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

        _texture ??= gpu.CreateTexture(new GpuTextureDesc(ToneCurve.TableSize, Rows, GpuFormat.Rgba16Float, GpuTextureUsage.Sampled));
        using (IGpuFrame frame = gpu.BeginFrame())
        {
            frame.Write(_texture, MemoryMarshal.AsBytes(table.AsSpan()), ToneCurve.TableSize * GpuFormats.BytesPerPixel(GpuFormat.Rgba16Float));
        }

        _uploaded = key;
        return _texture;
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
        _texture?.Dispose();
        _texture = null;
        _uploaded = null;
    }
}
