using Rodaje.Media.Export;

namespace Rodaje.App.Views.Export;

/// <summary>Qué puede hacer el usuario ante un error de exportación (el botón del aviso).</summary>
public enum ExportErrorAction
{
    None,

    /// <summary>Repetir con los mismos ajustes.</summary>
    Retry,

    /// <summary>Repetir sin el contenido que falta (LUT, transición o paquete).</summary>
    ContinueWithout,

    /// <summary>Volver al diálogo para elegir otra carpeta o nombre.</summary>
    ChooseOutput,

    /// <summary>Volver al diálogo para elegir otro formato o codificador.</summary>
    ChangeSettings,

    /// <summary>Abrir el gestor de complementos (contenido que falta).</summary>
    OpenPlugins,
}

/// <summary>Un error explicado: título, qué pasó, qué hacer y el botón.</summary>
public sealed record ExportErrorView(string Title, string Message, ExportErrorAction Action, string? ActionText);

/// <summary>Textos en español por <see cref="ExportErrorCode"/>; el mensaje del trabajo se añade si aporta.</summary>
public static class ExportErrorText
{
    /// <summary>Así empieza <see cref="ExportException.Culprit"/> cuando falta la fuente de un título.</summary>
    private const string FontCulprit = "fuente «";

    public static ExportErrorView For(ExportException error)
    {
        ArgumentNullException.ThrowIfNull(error);
        string culprit = error.Culprit is { Length: > 0 } c ? $" ({CulpritName(c)})" : "";
        string frame = error.Frame is { } f ? $" en el cuadro {f}" : "";
        string mediaFrame = error.MediaFrame is { } m ? $"el cuadro {m}" : "un cuadro";
        string medium = error.Culprit is { Length: > 0 } named ? $"«{CulpritName(named)}»" : "un medio";
        string exported = error.Frame is { } e ? $" (cuadro {e} de la exportación)" : "";
        (string title, string message, ExportErrorAction action) = error.Code switch
        {
            ExportErrorCode.MediaMissing => ("Falta un medio", $"Un video o audio de la secuencia no se encuentra o no se puede abrir{culprit}. Vuelve a enlazarlo o quítalo y exporta otra vez.", ExportErrorAction.Retry),
            ExportErrorCode.DecodeFailed => ("No se pudo leer un cuadro", $"No se pudo decodificar {mediaFrame} de {medium}{exported}. Puede estar dañado o usar un formato que este equipo no decodifica: prueba a generar su proxy o a convertirlo, y exporta de nuevo.", ExportErrorAction.Retry),
            ExportErrorCode.ContentMissing => ("Falta contenido", $"La secuencia usa una LUT, transición o paquete que no está instalado{culprit}. Puedes instalarlo o exportar sin él.", ExportErrorAction.ContinueWithout),
            ExportErrorCode.EffectFailed => ("Un efecto falló", $"Un efecto no se pudo aplicar{culprit}{frame}. Puedes exportar sin él o quitarlo del clip.", ExportErrorAction.ContinueWithout),
            ExportErrorCode.GpuFailed => ("La tarjeta gráfica se quedó sin memoria", "Cierra otros programas que usen la GPU o elige «Bajo rendimiento» y vuelve a intentarlo.", ExportErrorAction.Retry),
            ExportErrorCode.EncoderMissing => ("Falta el codificador", "No está el ffmpeg.exe LGPL de Rodaje. Reinstala Rodaje o ejecuta «preparar-entorno».", ExportErrorAction.None),
            ExportErrorCode.EncoderRejected => ("El codificador no aceptó los ajustes", "Elige otro codificador (por ejemplo, el de CPU) o vuelve a un preajuste.", ExportErrorAction.ChangeSettings),
            ExportErrorCode.EncoderFailed => ("El codificador se detuvo", "Prueba otra vez; si se repite, elige otro codificador en Avanzado.", ExportErrorAction.Retry),
            ExportErrorCode.DiskFull => ("No hay espacio en el disco", "Libera espacio o guarda en otro disco.", ExportErrorAction.ChooseOutput),
            ExportErrorCode.OutputNotWritable => ("No se puede escribir ahí", "La carpeta es de solo lectura o el archivo está abierto en otro programa. Ciérralo o elige otra carpeta.", ExportErrorAction.ChooseOutput),
            ExportErrorCode.InvalidSettings => ("Los ajustes no son válidos", "Revisa el formato en Avanzado o vuelve a un preajuste.", ExportErrorAction.ChangeSettings),
            ExportErrorCode.Canceled => ("Exportación cancelada", "No se guardó ningún archivo.", ExportErrorAction.None),
            _ => ("La exportación falló", "Algo inesperado la detuvo. Vuelve a intentarlo; los detalles quedan en rodaje-error.txt.", ExportErrorAction.Retry),
        };

        if (error.Code == ExportErrorCode.ContentMissing && error.Culprit?.StartsWith(FontCulprit, StringComparison.Ordinal) == true)
        {
            (title, message) = ("Falta una fuente", $"Un título usa la {error.Culprit}, que no está instalada. Instala su paquete o exporta con la fuente de respaldo.");
        }

        if (error.Code == ExportErrorCode.ContentMissing && !error.CanContinueWithout)
        {
            action = ExportErrorAction.OpenPlugins;
        }

        return new(title, message, action, ActionText(action));
    }

    /// <summary>El nombre del archivo de una ruta, o la referencia de paquete tal cual.</summary>
    private static string CulpritName(string culprit) =>
        Path.GetFileName(culprit.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : culprit;

    public static string? ActionText(ExportErrorAction action) => action switch
    {
        ExportErrorAction.Retry => "Reintentar",
        ExportErrorAction.ContinueWithout => "Exportar sin él",
        ExportErrorAction.ChooseOutput => "Elegir otra carpeta",
        ExportErrorAction.ChangeSettings => "Cambiar ajustes",
        ExportErrorAction.OpenPlugins => "Complementos",
        _ => null,
    };
}
