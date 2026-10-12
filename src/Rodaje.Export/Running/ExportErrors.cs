using System.Runtime.InteropServices;
using Rodaje.Media;
using Rodaje.Media.Export;
using Rodaje.Render;

namespace Rodaje.Export.Running;

/// <summary>Cualquier excepción de la exportación como <see cref="ExportException"/> con su código estable.</summary>
public static class ExportErrors
{
    // ERROR_DISK_FULL y ERROR_HANDLE_DISK_FULL como HRESULT.
    private const int DiskFull = unchecked((int)0x80070070);
    private const int HandleDiskFull = unchecked((int)0x80070027);

    // DXGI_ERROR_DEVICE_REMOVED, _HUNG, _RESET, E_OUTOFMEMORY.
    private static readonly int[] GpuLost = [unchecked((int)0x887A0005), unchecked((int)0x887A0006), unchecked((int)0x887A0007), unchecked((int)0x8007000E)];

    /// <param name="frame">Cuadro de la salida en el que pasó, si se sabe.</param>
    public static ExportException Map(Exception error, long? frame = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error switch
        {
            ExportException { Frame: null } export when frame is not null =>
                new ExportException(export.Code, export.Message, export.InnerException) { Frame = frame, MediaFrame = export.MediaFrame, Culprit = export.Culprit },
            ExportException export => export,
            AggregateException { InnerExceptions.Count: 1 } aggregate => Map(aggregate.InnerExceptions[0], frame),
            OperationCanceledException => new ExportException(ExportErrorCode.Canceled, "Exportación cancelada.", error),
            RenderException => new ExportException(ExportErrorCode.GpuFailed,
                $"La GPU falló al componer: {error.Message}", error) { Frame = frame },
            COMException com when GpuLost.Contains(com.HResult) => new ExportException(ExportErrorCode.GpuFailed,
                $"Se perdió la GPU o se quedó sin memoria de video: {error.Message}", error) { Frame = frame },
            FfmpegNotFoundException => new ExportException(ExportErrorCode.EncoderMissing, error.Message, error),
            IOException io when io.HResult is DiskFull or HandleDiskFull => new ExportException(ExportErrorCode.DiskFull,
                "No queda espacio en el disco.", error),
            IOException or UnauthorizedAccessException => new ExportException(ExportErrorCode.OutputNotWritable,
                $"No se puede escribir: {error.Message}", error),
            OutOfMemoryException => new ExportException(ExportErrorCode.GpuFailed, "Falta memoria para exportar.", error) { Frame = frame },
            _ => new ExportException(ExportErrorCode.Unknown, $"Error inesperado al exportar: {error.Message}", error) { Frame = frame },
        };
    }
}
