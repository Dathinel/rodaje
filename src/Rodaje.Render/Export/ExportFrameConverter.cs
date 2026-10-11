using Rodaje.Media.Export;
using Rodaje.Media.Gpu;

namespace Rodaje.Render.Export;

/// <summary>Recibe un cuadro convertido, prestado mientras dura la llamada.</summary>
public delegate void ExportFrameHandler(in ExportVideoFrame frame);

/// <summary>
/// De la composición R'G'B' a los cuadros de la salida, en la GPU (D-028): escala, forma (barras, recorte, estirado),
/// Y'CbCr con la matriz y el rango de <see cref="ExportVideoFormat.Color"/>, croma «left», tramado al bajar a 8 bits, y un
/// anillo de texturas <c>Staging</c> para que la copia a la CPU no detenga a la GPU.
/// </summary>
/// <remarks>
/// <para>
/// Uso, desde el hilo de la exportación: <see cref="Submit"/> encola la conversión y la copia de un cuadro (no espera);
/// <see cref="TryRead"/> mapea el más antiguo, se lo presta al manejador (normalmente <c>IExportSink.WriteVideo</c>) y lo
/// suelta. Con el anillo lleno, <see cref="Submit"/> lanza: primero se lee.
/// </para>
/// <para>
/// Con un formato con alfa la composición debe venir con fondo transparente (opción del compositor que añade el agente
/// B); el alfa sale recto, no premultiplicado. Contrato del paso 0 de la fase 6; la implementación es del agente B.
/// </para>
/// </remarks>
public sealed class ExportFrameConverter : IDisposable
{
    public const int DefaultRingSize = 3;

    private readonly GpuDevice _gpu;
    private bool _disposed;

    /// <param name="gpu">El dispositivo de la exportación, el mismo del compositor.</param>
    /// <param name="sourceWidth">Ancho de la composición (la secuencia a resolución completa).</param>
    /// <param name="sourceHeight">Alto de la composición.</param>
    /// <param name="output">Tamaño, formato de píxel y color de la salida.</param>
    /// <param name="fit">Cómo cabe la composición si la forma no coincide.</param>
    /// <param name="filter">Filtro de escala.</param>
    /// <param name="ringSize">Texturas <c>Staging</c> en vuelo (2 a 8).</param>
    public ExportFrameConverter(GpuDevice gpu, int sourceWidth, int sourceHeight, ExportVideoFormat output,
        ExportFit fit = ExportFit.Bars, ExportScaleFilter filter = ExportScaleFilter.Auto, int ringSize = DefaultRingSize)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceHeight);
        ArgumentOutOfRangeException.ThrowIfLessThan(ringSize, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ringSize, 8);
        _gpu = gpu;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        Output = output;
        Fit = fit;
        Filter = filter;
        Capacity = ringSize;
    }

    public GpuDevice Device => _gpu;

    public int SourceWidth { get; }

    public int SourceHeight { get; }

    public ExportVideoFormat Output { get; }

    public ExportFit Fit { get; }

    public ExportScaleFilter Filter { get; }

    /// <summary>Cuadros que caben en vuelo.</summary>
    public int Capacity { get; }

    /// <summary>Cuadros encolados que todavía no se leyeron.</summary>
    public int Pending { get; private set; }

    /// <summary>Convierte la composición <paramref name="compositeTexture"/> (<c>ID3D11Texture2D*</c> de <c>Compositor.Texture</c>) y encola su copia.</summary>
    /// <param name="index">Número del cuadro en la salida; llega igual en <see cref="ExportVideoFrame.Index"/>.</param>
    /// <exception cref="InvalidOperationException">El anillo está lleno.</exception>
    public void Submit(nint compositeTexture, long index)
    {
        ThrowIfUnusable(compositeTexture, index);
        throw new NotImplementedException("ExportFrameConverter.Submit es del agente B de la fase 6.");
    }

    /// <summary>
    /// Como <see cref="Submit"/>, pero mezcla dos composiciones (conversión de tasa con mezcla):
    /// <c>(1 − weight)·first + weight·second</c> en R'G'B' antes de convertir.
    /// </summary>
    public void SubmitBlend(nint first, nint second, float weight, long index)
    {
        ThrowIfUnusable(first, index);
        ThrowIfUnusable(second, index);
        ArgumentOutOfRangeException.ThrowIfLessThan(weight, 0f);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(weight, 1f);
        throw new NotImplementedException("ExportFrameConverter.SubmitBlend es del agente B de la fase 6.");
    }

    /// <summary>Si hay un cuadro encolado, espera a que la GPU lo termine, se lo presta a <paramref name="handler"/> y lo suelta.</summary>
    /// <returns><c>false</c> si no había ninguno.</returns>
    public bool TryRead(ExportFrameHandler handler)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(handler);
        if (Pending == 0)
        {
            return false;
        }

        throw new NotImplementedException("ExportFrameConverter.TryRead es del agente B de la fase 6.");
    }

    public void Dispose() => _disposed = true;

    private void ThrowIfUnusable(nint texture, long index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfZero(texture);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (Pending >= Capacity)
        {
            throw new InvalidOperationException("El anillo de cuadros está lleno: hay que leer antes de encolar otro.");
        }
    }
}
