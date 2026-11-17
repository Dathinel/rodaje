using Rodaje.Render.Gpu;
using Rodaje.Render.Gpu.D3D11;
using TerraFX.Interop.DirectX;

namespace Rodaje.Render;

/// <summary>Una textura de destino y de lectura. <see cref="Width"/> y <see cref="Height"/> son los de la textura.</summary>
internal sealed unsafe class RenderTarget(GpuTexture texture, long bytes) : IDisposable
{
    public GpuTexture Gpu { get; } = texture;

    /// <summary>Para lo que aún no pasó a <see cref="IGpu"/>.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
    public ID3D11Texture2D* Texture => ((D3D11Texture)Gpu).Texture2D;

    [System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
    public ID3D11RenderTargetView* Rtv => ((D3D11Texture)Gpu).Rtv;

    [System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
    public ID3D11ShaderResourceView* Srv => ((D3D11Texture)Gpu).Srv;

    public int Width => Gpu.Width;

    public int Height => Gpu.Height;

    public GpuFormat Format => Gpu.Format;

    public long Bytes { get; } = bytes;

    internal bool InUse { get; set; }

    internal long LastUsedFrame { get; set; }

    public static RenderTarget Create(IGpu gpu, int width, int height, GpuFormat format) =>
        new(gpu.CreateTexture(new GpuTextureDesc(width, height, format, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled)),
            BytesFor(width, height, format));

    public static long BytesFor(int width, int height, GpuFormat format) =>
        (long)width * height * (format == GpuFormat.Rgba16Float ? 8 : 4);

    public void Dispose() => Gpu.Dispose();
}

/// <summary>
/// Intermedios de los efectos, reutilizados cuadro tras cuadro, con un tope de memoria que no entra en el presupuesto
/// de los decodificadores. Si una textura no cabe, <see cref="Rent"/> devuelve <c>null</c> y el efecto se salta.
/// </summary>
/// <remarks>
/// Los tamaños se redondean a 64 px para que una capa que cambia de escala no cree una textura por cuadro; quien la usa
/// dibuja en la parte que pidió. Solo se usa desde el hilo que compone, con el candado del contexto tomado.
/// </remarks>
internal sealed class RenderTargetPool : IDisposable
{
    public const long MaxCapBytes = 300L * 1024 * 1024;
    public const long MinCapBytes = 96L * 1024 * 1024;
    private const int Granularity = 64;
    private const int IdleFrames = 240;

    private readonly IGpu _engine;
    private readonly List<RenderTarget> _targets = [];
    private long _frame;

    public RenderTargetPool(IGpu engine, long capBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capBytes);
        _engine = engine;
        CapBytes = capBytes;
    }

    public long CapBytes { get; }

    /// <summary>Memoria de todas las texturas del pool, en uso o libres.</summary>
    public long Bytes { get; private set; }

    public int Count => _targets.Count;

    internal bool Reserve(long bytes)
    {
        while (Bytes + bytes > CapBytes && EvictOldestFree())
        {
        }

        if (Bytes + bytes > CapBytes) return false;
        Bytes += bytes;
        return true;
    }

    internal void ReleaseReservation(long bytes) => Bytes -= bytes;

    /// <summary>Tope según la VRAM dedicada: 7,5 %, entre 96 y 300 MB (300 MB con 4 GB, 150 MB con 2 GB).</summary>
    public static long CapFor(long dedicatedVideoMemory) =>
        Math.Clamp((long)(dedicatedVideoMemory * 0.075), MinCapBytes, MaxCapBytes);

    /// <summary>Una textura de al menos <paramref name="width"/>×<paramref name="height"/>; <c>null</c> si no cabe en el tope.</summary>
    /// <param name="exact">Sin redondear el tamaño (los lienzos de transición, que siempre miden lo mismo).</param>
    public RenderTarget? Rent(int width, int height, GpuFormat format, bool exact = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        int w = exact ? width : RoundUp(width);
        int h = exact ? height : RoundUp(height);
        foreach (RenderTarget target in _targets)
        {
            if (!target.InUse && target.Width == w && target.Height == h && target.Format == format)
            {
                target.InUse = true;
                return target;
            }
        }

        long bytes = RenderTarget.BytesFor(w, h, format);
        while (Bytes + bytes > CapBytes && EvictOldestFree())
        {
        }

        if (Bytes + bytes > CapBytes)
        {
            return null;
        }

        RenderTarget created = RenderTarget.Create(_engine, w, h, format);

        // El filtro bilineal lee el texel vecino del borde de lo pedido aunque pese 0, y 0 × NaN es NaN: el margen del
        // redondeo no puede quedar con lo que hubiera en esa memoria (en WARP, a veces medias precisiones NaN).
        using (IGpuFrame frame = _engine.BeginFrame())
        {
            frame.Clear(created.Gpu, GpuColor.TransparentBlack);
        }

        created.InUse = true;
        _targets.Add(created);
        Bytes += created.Bytes;
        return created;
    }

    public void Return(RenderTarget? target)
    {
        if (target is null)
        {
            return;
        }

        target.InUse = false;
        target.LastUsedFrame = _frame;
    }

    /// <summary>Cierra un cuadro: suelta las texturas libres que llevan muchos cuadros sin usarse.</summary>
    public void EndFrame()
    {
        _frame++;
        for (int i = _targets.Count - 1; i >= 0; i--)
        {
            RenderTarget target = _targets[i];
            if (!target.InUse && _frame - target.LastUsedFrame > IdleFrames)
            {
                Remove(i);
            }
        }
    }

    /// <summary>Suelta todas las texturas libres (en pausa).</summary>
    public void Trim()
    {
        for (int i = _targets.Count - 1; i >= 0; i--)
        {
            if (!_targets[i].InUse)
            {
                Remove(i);
            }
        }
    }

    public void Dispose()
    {
        foreach (RenderTarget target in _targets)
        {
            target.Dispose();
        }

        _targets.Clear();
        Bytes = 0;
    }

    private static int RoundUp(int size) => (size + Granularity - 1) / Granularity * Granularity;

    private bool EvictOldestFree()
    {
        int oldest = -1;
        for (int i = 0; i < _targets.Count; i++)
        {
            if (!_targets[i].InUse && (oldest < 0 || _targets[i].LastUsedFrame < _targets[oldest].LastUsedFrame))
            {
                oldest = i;
            }
        }

        if (oldest < 0)
        {
            return false;
        }

        Remove(oldest);
        return true;
    }

    private void Remove(int index)
    {
        RenderTarget target = _targets[index];
        _targets.RemoveAt(index);
        Bytes -= target.Bytes;
        target.Dispose();
    }
}
