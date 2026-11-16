namespace Rodaje.Render.Gpu;

/// <summary>Un recurso de un <see cref="IGpu"/>. Lo libera quien lo crea; no se usa con otro <see cref="IGpu"/>.</summary>
public abstract class GpuResource : IDisposable
{
    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        Release();
        GC.SuppressFinalize(this);
    }

    /// <summary>Suelta lo nativo. El motor no destruye nada que una valla pendiente aÃºn use (regla 4 de <c>docs/igpu.md</c>).</summary>
    protected abstract void Release();
}

public abstract class GpuTexture(GpuTextureDesc desc) : GpuResource
{
    public GpuTextureDesc Desc { get; } = desc;

    public int Width => Desc.Width;

    public int Height => Desc.Height;

    public GpuFormat Format => Desc.Format;
}

public abstract class GpuBuffer(GpuBufferDesc desc) : GpuResource
{
    public GpuBufferDesc Desc { get; } = desc;

    public int Size => Desc.Size;
}

public abstract class GpuSampler : GpuResource;

/// <summary>Un sombreador compilado para el motor (bytecode de D3D11 o SPIR-V).</summary>
public abstract class GpuShader : GpuResource
{
    /// <summary>Id estable de la variante, como en el manifiesto.</summary>
    public required string Id { get; init; }
}

public abstract class GpuPipeline(GpuPipelineDesc desc) : GpuResource
{
    public GpuPipelineDesc Desc { get; } = desc;

    public bool IsCompute => Desc.Compute is not null;
}

/// <summary>Marca de fin de un envÃ­o.</summary>
public abstract class GpuFence
{
    public abstract bool IsComplete { get; }

    /// <summary>Espera a que termine; falso si se agotÃ³ el tiempo.</summary>
    public abstract bool Wait(TimeSpan timeout);
}

/// <summary>Tiempo de GPU entre <see cref="IGpuFrame.BeginTimer"/> y <see cref="IGpuFrame.EndTimer"/>.</summary>
public abstract class GpuTimerQuery : GpuResource
{
    /// <summary>Falso si todavÃ­a no terminÃ³. <paramref name="milliseconds"/> es <c>null</c> si el intervalo no dio medida (reloj disjunto).</summary>
    public abstract bool TryGetMilliseconds(out double? milliseconds);

    /// <summary>Espera el resultado; <c>null</c> si no dio medida o no llegÃ³ a tiempo.</summary>
    public double? WaitMilliseconds(TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (true)
        {
            if (TryGetMilliseconds(out double? milliseconds))
            {
                return milliseconds;
            }

            if (Environment.TickCount64 > deadline)
            {
                return null;
            }

            Thread.Yield();
        }
    }
}

/// <summary>
/// Memoria visible desde la CPU para leer una textura o un bÃºfer, reutilizable de un cuadro a otro. Los bytes valen cuando el
/// cuadro que grabÃ³ la copia terminÃ³: <see cref="TryMap"/> no espera, <see cref="Map"/> sÃ­.
/// </summary>
public abstract class GpuReadback : GpuResource
{
    protected GpuReadback(int width, int height, GpuFormat format, int bytes)
    {
        Width = width;
        Height = height;
        Format = format;
        Bytes = bytes;
    }

    /// <summary>En pÃ­xeles; un bÃºfer tiene <see cref="Height"/> 1 y <see cref="Format"/> <see cref="GpuFormat.Unknown"/>.</summary>
    public int Width { get; }

    public int Height { get; }

    public GpuFormat Format { get; }

    /// <summary>Bytes Ãºtiles de una fila (o del bÃºfer).</summary>
    public int Bytes { get; }

    /// <summary>Falso si la copia no terminÃ³ todavÃ­a.</summary>
    public abstract bool TryMap(out GpuMappedRows rows);

    public abstract GpuMappedRows Map();

    /// <summary>Copia las filas, sin relleno, a <paramref name="destination"/> (<see cref="Bytes"/> Ã— <see cref="Height"/>).</summary>
    public void Read(Span<byte> destination)
    {
        using GpuMappedRows rows = Map();
        rows.CopyTo(destination);
    }

    protected internal abstract void Unmap();
}

/// <summary>Filas leÃ­das, de arriba abajo; valen hasta <see cref="Dispose"/>.</summary>
public readonly unsafe ref struct GpuMappedRows
{
    private readonly GpuReadback _owner;
    private readonly byte* _data;

    internal GpuMappedRows(GpuReadback owner, byte* data, int rowPitch)
    {
        _owner = owner;
        _data = data;
        RowPitch = rowPitch;
    }

    /// <summary>Distancia en bytes entre filas (puede llevar relleno).</summary>
    public int RowPitch { get; }

    public int Height => _owner.Height;

    public ReadOnlySpan<byte> Row(int y)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, _owner.Height);
        return new ReadOnlySpan<byte>(_data + ((nint)y * RowPitch), _owner.Bytes);
    }

    public void CopyTo(Span<byte> destination)
    {
        int bytes = _owner.Bytes;
        for (int y = 0; y < _owner.Height; y++)
        {
            Row(y).CopyTo(destination.Slice(y * bytes, bytes));
        }
    }

    public void Dispose() => _owner?.Unmap();
}
