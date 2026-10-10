namespace Rodaje.Media.Gpu;

/// <summary>
/// El arreglo de texturas de un decodificador D3D11VA, visto desde fuera: cuánto ocupa y si su decodificador ya se
/// cerró. Quien guarde recursos atados a esas texturas (por ejemplo, vistas de sombreado) los suelta al cerrarse.
/// </summary>
public sealed class DecoderSurfacePool
{
    private static long s_closedCount;
    private int _closed;

    internal DecoderSurfacePool(int surfaces, long surfaceBytes)
    {
        Surfaces = surfaces;
        SurfaceBytes = surfaceBytes;
    }

    /// <summary>Cuántos pools se cerraron en todo el proceso; si no cambió, no hay nada nuevo que soltar.</summary>
    public static long ClosedCount => Interlocked.Read(ref s_closedCount);

    public int Surfaces { get; }

    /// <summary>Bytes de una superficie, con el relleno que pide el decodificador.</summary>
    public long SurfaceBytes { get; }

    public long TotalBytes => Surfaces * SurfaceBytes;

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    internal void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            Interlocked.Increment(ref s_closedCount);
        }
    }
}
