using System.Diagnostics;
using System.Runtime.InteropServices;
using Rodaje.Media.Gpu;
using Rodaje.Render.Gpu;
using Rodaje.Render.Gpu.D3D11;
using Rodaje.Render.Shaders;
using TerraFX.Interop.DirectX;

namespace Rodaje.Render.Scopes;

/// <summary>Qué monitor de imagen se dibuja.</summary>
public enum ScopeKind
{
    /// <summary>Forma de onda de la luma (BT.709), por columna de la imagen.</summary>
    Waveform,

    /// <summary>Forma de onda de R, G y B superpuestas.</summary>
    WaveformRgb,

    /// <summary>Desfile: R, G y B uno al lado del otro.</summary>
    Parade,

    /// <summary>Vectorscopio: Cb a la derecha, Cr hacia arriba (256 × 256 a la izquierda de la imagen).</summary>
    Vectorscope,

    /// <summary>Histograma de R, G, B y luma.</summary>
    Histogram,
}

/// <summary>Una imagen de monitor lista para mostrar: BGRA de 8 bits, fila tras fila.</summary>
public sealed record ScopeImage(byte[] Bgra, int Width, int Height, ScopeKind Kind, long Sequence);

/// <summary>Las cuentas crudas de un cuadro, para las pruebas (y para quien quiera dibujar sus propios monitores).</summary>
public sealed class ScopeCounts(uint[] raw, int width, int height, int step)
{
    /// <summary>Ancho y alto de la imagen medida y cada cuántos píxeles se tomó uno.</summary>
    public int Width { get; } = width;

    public int Height { get; } = height;

    public int Step { get; } = step;

    /// <summary>Píxeles en la columna <paramref name="column"/> (de <see cref="ScopeRenderer.Columns"/>) con ese nivel (0–255) en el canal 0 R, 1 G, 2 B o 3 luma.</summary>
    public uint Wave(int channel, int level, int column) => raw[(((channel * 256) + level) * ScopeRenderer.Columns) + column];

    /// <summary>Píxeles en la celda (x, y) del vectorscopio de 256 × 256.</summary>
    public uint Vector(int x, int y) => raw[ScopeRenderer.VectorBase + (y * 256) + x];

    /// <summary>Píxeles con ese nivel en el canal 0 R, 1 G, 2 B o 3 luma.</summary>
    public uint Histogram(int channel, int level) => raw[ScopeRenderer.HistogramBase + (channel * 256) + level];

    /// <summary>El mayor nivel del histograma sin los extremos (0 y 255), con el que se escala su dibujo.</summary>
    public uint HistogramPeak => raw[ScopeRenderer.MaxSlot];
}

/// <summary>
/// Los monitores de imagen en la GPU (compute shaders): cuenta los píxeles de la composición por columna y nivel, por
/// celda de croma y por nivel, y dibuja el monitor elegido en una imagen de 512 × 256 que baja por un anillo de
/// lecturas sin esperar a la GPU. Se usa desde el hilo que compone, después de <see cref="Compositor.Render"/>.
/// </summary>
public sealed unsafe class ScopeRenderer : IDisposable
{
    /// <summary>Columnas de la forma de onda y del desfile.</summary>
    public const int Columns = 512;

    public const int ImageWidth = 512;

    public const int ImageHeight = 256;

    /// <summary>Celdas del vectorscopio por unidad de croma (Cb o Cr): el 100 % de saturación queda dentro del círculo de 128.</summary>
    public const double VectorScale = 236;

    /// <summary>Ancho máximo que se mide sin submuestrear; por encima se toma un píxel de cada 2 (o 3…) en cada eje.</summary>
    public const int MaxMeasuredWidth = 1920;

    internal const int VectorBase = 4 * 256 * Columns;
    internal const int HistogramBase = VectorBase + (256 * 256);
    internal const int MaxSlot = HistogramBase + 1024;
    private const int CountSlots = MaxSlot + 1;
    private const int Ring = 3;

    private readonly D3D11Gpu _engine;
    private readonly Task _ready;
    private readonly GpuReadback?[] _staging = new GpuReadback?[Ring];
    private readonly (long Sequence, ScopeKind Kind)[] _pending = new (long, ScopeKind)[Ring];
    private GpuShader[]? _compiled;
    private GpuPipeline? _accumulate;
    private GpuPipeline? _maximum;
    private GpuPipeline? _draw;
    private GpuBuffer? _constants;
    private GpuBuffer? _counts;
    private GpuTexture? _image;
    private GpuReadback? _countsRead;
    private GpuTexture? _source;
    private nint _sourceTexture;
    private long _submitted;
    private long _read;
    private bool _disposed;

    public ScopeRenderer(GpuDevice gpu, ShaderCache? shaders = null)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        _engine = D3D11Gpu.For(gpu);
        ShaderCache cache = shaders ?? ShaderCache.Default;
        _ready = Task.Run(() =>
        {
            GpuShader? accumulate = null;
            GpuShader? maximum = null;
            try
            {
                accumulate = _engine.CreateShader(ScopeShaders.Accumulate, cache);
                maximum = _engine.CreateShader(ScopeShaders.Maximum, cache);
                return (GpuShader[])[accumulate, maximum, _engine.CreateShader(ScopeShaders.Draw, cache)];
            }
            catch
            {
                accumulate?.Dispose();
                maximum?.Dispose();
                throw;
            }
        }).ContinueWith(t => _compiled = t.Result, TaskScheduler.Default);
    }

    /// <summary>Termina cuando los shaders están compilados (o falló la compilación: la excepción queda aquí).</summary>
    public Task Ready => _ready;

    /// <summary>Ganancia del trazo: con 1, un nivel con el promedio de píxeles de su columna se ve al 63 %.</summary>
    public float Gain { get; set; } = 1.5f;

    /// <summary>Tiempo de CPU de la última medición encolada, en ms (sin esperar a la GPU).</summary>
    public double LastSubmitMilliseconds { get; private set; }

    /// <summary>
    /// Encola la medición de la última composición de <paramref name="compositor"/> y el dibujo de <paramref name="kind"/>.
    /// No espera: si los shaders no están, no hace nada y devuelve <c>false</c>.
    /// </summary>
    public bool Submit(Compositor compositor, ScopeKind kind)
    {
        ArgumentNullException.ThrowIfNull(compositor);
        if (_compiled is null || compositor.ShaderResourceView == 0)
        {
            return false;
        }

        long start = Stopwatch.GetTimestamp();
        using (IGpuFrame frame = _engine.BeginFrame())
        {
            EnsureResources();
            int slot = (int)(_submitted % Ring);
            if (_submitted - _read >= Ring)
            {
                // El anillo está lleno: la más vieja se pierde (la interfaz solo quiere la última).
                _read = _submitted - Ring + 1;
            }

            Measure(frame, compositor, kind);
            frame.Copy(_image!, new GpuBox(0, 0, ImageWidth, ImageHeight), _staging[slot]!);
            frame.Submit();
            _pending[slot] = (++_submitted, kind);
        }

        LastSubmitMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        return true;
    }

    /// <summary>Hay alguna medición encolada que <see cref="TryRead"/> todavía no recogió.</summary>
    public bool HasPending => _read < _submitted;

    /// <summary>La imagen más nueva que la GPU ya terminó, o <c>null</c> si no hay ninguna nueva. Nunca espera.</summary>
    public ScopeImage? TryRead()
    {
        if (_read >= _submitted || _staging[0] is null)
        {
            return null;
        }

        ScopeImage? latest = null;
        while (_read < _submitted)
        {
            int slot = (int)(_read % Ring);
            if (!_staging[slot]!.TryMap(out GpuMappedRows rows))
            {
                break;
            }

            var pixels = new byte[ImageWidth * ImageHeight * 4];
            using (rows)
            {
                rows.CopyTo(pixels);
            }

            PixelFingerprint.Record("monitor", pixels.AsSpan());
            (long sequence, ScopeKind kind) = _pending[slot];
            latest = new ScopeImage(pixels, ImageWidth, ImageHeight, kind, sequence);
            _read++;
        }

        return latest;
    }

    /// <summary>Mide y espera: las cuentas crudas de la última composición (para las pruebas).</summary>
    public ScopeCounts MeasureNow(Compositor compositor, ScopeKind kind = ScopeKind.Waveform)
    {
        ArgumentNullException.ThrowIfNull(compositor);
        Ready.GetAwaiter().GetResult();
        int width;
        int height;
        int step;
        using (IGpuFrame frame = _engine.BeginFrame())
        {
            EnsureResources();
            (width, height, step) = Measure(frame, compositor, kind);
            frame.Copy(_counts!, 0, CountSlots * 4, _countsRead!);
        }

        var raw = new uint[CountSlots];
        using (GpuMappedRows rows = _countsRead!.Map())
        {
            MemoryMarshal.Cast<byte, uint>(rows.Row(0)).CopyTo(raw);
        }

        return new ScopeCounts(raw, width, height, step);
    }

    /// <summary>Tres pasadas: limpiar y contar, el máximo del histograma y el dibujo. Dentro de un cuadro grabado.</summary>
    private (int Width, int Height, int Step) Measure(IGpuFrame frame, Compositor compositor, ScopeKind kind)
    {
        int width = compositor.Width;
        int height = compositor.Height;
        int step = Math.Max(1, (width + MaxMeasuredWidth - 1) / MaxMeasuredWidth);
        int measuredWidth = (width + step - 1) / step;
        int measuredHeight = (height + step - 1) / step;
        var constants = new ScopeConstants
        {
            SourceWidth = (uint)width,
            SourceHeight = (uint)height,
            Step = (uint)step,
            Columns = Columns,
            Kind = (float)kind,
            Gain = Gain,
            PixelsPerColumn = (float)measuredWidth * measuredHeight / Math.Min(Columns, measuredWidth),
            TotalPixels = (float)measuredWidth * measuredHeight,
            Hdr = compositor.ColorSpace != Rodaje.Core.Model.SequenceColorSpace.Sdr709 ? 1 : 0,
        };
        frame.Write(_constants!, MemoryMarshal.CreateReadOnlySpan(ref constants, 1));
        frame.ClearUint(_counts!, 0);
        var bindings = default(GpuBindings);
        bindings.SetConstants(0, _constants);
        bindings.SetResource(0, Source(compositor));
        bindings.SetStorage(0, _counts);
        bindings.SetStorage(1, _image);
        frame.Dispatch(_accumulate!, bindings, (measuredWidth + 15) / 16, (measuredHeight + 15) / 16);
        frame.Dispatch(_maximum!, bindings, 1, 1);
        frame.Dispatch(_draw!, bindings, ImageWidth / 8, ImageHeight / 8);
        return (measuredWidth, measuredHeight, step);
    }

    /// <summary>La composición envuelta para el motor; se envuelve otra vez solo si cambia el puntero.</summary>
    private GpuTexture Source(Compositor compositor)
    {
        if (_source is not null && _sourceTexture == compositor.Texture)
        {
            return _source;
        }

        _source?.Dispose();
        var desc = new GpuTextureDesc(compositor.Width, compositor.Height,
            compositor.Format == CompositeFormat.Rgba16Float ? GpuFormat.Rgba16Float : GpuFormat.Rgba8Unorm,
            GpuTextureUsage.Sampled, MipLevels: compositor.MipLevels);
        _source = _engine.Wrap((ID3D11Texture2D*)compositor.Texture, desc,
            (ID3D11ShaderResourceView*)compositor.ShaderResourceView);
        _sourceTexture = compositor.Texture;
        return _source;
    }

    private void EnsureResources()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_accumulate is not null)
        {
            return;
        }

        GpuShader[] shaders = _compiled!;
        _accumulate = _engine.CreatePipeline(new GpuPipelineDesc { Compute = shaders[0] });
        _maximum = _engine.CreatePipeline(new GpuPipelineDesc { Compute = shaders[1] });
        _draw = _engine.CreatePipeline(new GpuPipelineDesc { Compute = shaders[2] });
        _constants = _engine.CreateBuffer(new GpuBufferDesc(sizeof(ScopeConstants), GpuBufferKind.Constant));
        _counts = _engine.CreateBuffer(new GpuBufferDesc(CountSlots * 4, GpuBufferKind.Raw, Storage: true));
        _image = _engine.CreateTexture(new GpuTextureDesc(ImageWidth, ImageHeight, GpuFormat.Rgba8Unorm, GpuTextureUsage.Storage));
        _countsRead = _engine.CreateReadback(CountSlots * 4);
        for (int i = 0; i < Ring; i++)
        {
            _staging[i] = _engine.CreateReadback(ImageWidth, ImageHeight, GpuFormat.Rgba8Unorm);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _source?.Dispose();
        _accumulate?.Dispose();
        _maximum?.Dispose();
        _draw?.Dispose();
        _constants?.Dispose();
        _counts?.Dispose();
        _countsRead?.Dispose();
        _image?.Dispose();
        foreach (GpuReadback? staging in _staging)
        {
            staging?.Dispose();
        }

        if (_compiled is { } shaders)
        {
            foreach (GpuShader shader in shaders)
            {
                shader.Dispose();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScopeConstants
    {
        public uint SourceWidth;
        public uint SourceHeight;
        public uint Step;
        public uint Columns;
        public float Kind;
        public float Gain;
        public float PixelsPerColumn;
        public float TotalPixels;
        public float Hdr;
        public float Unused0;
        public float Unused1;
        public float Unused2;
    }
}
