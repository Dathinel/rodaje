namespace Rodaje.Media.Export;

/// <summary>Por qué falló una exportación. Los valores son estables: la interfaz elige el texto y la acción por código.</summary>
public enum ExportErrorCode
{
    Unknown = 0,

    /// <summary>Un medio de la secuencia falta o no se puede abrir.</summary>
    MediaMissing = 1,

    /// <summary>Un cuadro no se pudo decodificar exacto.</summary>
    DecodeFailed = 2,

    /// <summary>Una LUT, transición o paquete que usa la secuencia no está; se puede «continuar sin».</summary>
    ContentMissing = 3,

    /// <summary>Un efecto no se pudo aplicar (shader sin compilar o pool lleno).</summary>
    EffectFailed = 4,

    /// <summary>La GPU se perdió o se quedó sin memoria de video.</summary>
    GpuFailed = 5,

    /// <summary>Falta <c>ffmpeg.exe</c> o no es la compilación LGPL verificada.</summary>
    EncoderMissing = 6,

    /// <summary>El codificador no arrancó o rechazó los ajustes.</summary>
    EncoderRejected = 7,

    /// <summary><c>ffmpeg.exe</c> terminó con error o cerró la tubería a mitad.</summary>
    EncoderFailed = 8,

    /// <summary>No cabe en el disco de destino.</summary>
    DiskFull = 9,

    /// <summary>No se puede escribir en la carpeta o el archivo está en uso.</summary>
    OutputNotWritable = 10,

    /// <summary>Ajustes imposibles (tamaño impar, códec que no admite el contenedor, rango vacío).</summary>
    InvalidSettings = 11,

    /// <summary>El usuario la canceló.</summary>
    Canceled = 12,
}

/// <summary>Error de exportación, con su código y un mensaje en español para el usuario.</summary>
public sealed class ExportException : Exception
{
    public ExportException()
    {
    }

    public ExportException(string message)
        : base(message)
    {
    }

    public ExportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ExportException(ExportErrorCode code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public ExportErrorCode Code { get; }

    /// <summary>Cuadro de la salida en el que falló, si se sabe.</summary>
    public long? Frame { get; init; }

    /// <summary>Medio o recurso causante (ruta o referencia de paquete), si se sabe.</summary>
    public string? Culprit { get; init; }

    /// <summary>Si la interfaz puede ofrecer «continuar sin» y repetir la exportación.</summary>
    public bool CanContinueWithout => Code is ExportErrorCode.ContentMissing or ExportErrorCode.EffectFailed;
}
