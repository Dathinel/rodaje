using System.Text;
using Rodaje.Media.Export;

namespace Rodaje.Export.Running;

/// <summary>
/// La «línea de ffmpeg» de una exportación: los mismos argumentos que <see cref="ExportJob"/> le pasa a <c>ffmpeg.exe</c>,
/// armados sin lanzar nada, para copiarlos y leerlos (qué codificador, qué control de tasa, qué perfil, qué muestreo).
/// Rodaje compone la imagen en la GPU y se la pasa cruda por la entrada estándar (<c>-f rawvideo … -i pipe:0</c>) y la
/// mezcla por un WAV temporal: por eso la línea no se puede ejecutar tal cual contra los medios originales, pero sí muestra
/// cada opción de codificación. No lleva los capítulos ni los subtítulos como pista aparte (van en archivos temporales que
/// solo existen al exportar) y no contempla la copia directa.
/// </summary>
public static class ExportCommandLine
{
    /// <summary>Nombre que ocupa el lugar del WAV temporal de la mezcla.</summary>
    public const string AudioPlaceholder = "mezcla.wav";

    /// <summary>Nombre del ejecutable en la línea copiada.</summary>
    public const string Executable = "ffmpeg";

    /// <summary>Los argumentos de la pasada final de <paramref name="plan"/>.</summary>
    public static IReadOnlyList<string> Arguments(ExportPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string? wave = plan.Audio is null ? null : Path.Combine(Path.GetDirectoryName(plan.Request.OutputPath) ?? "", AudioPlaceholder);
        var request = new ExportSinkRequest(plan.Request.OutputPath, plan.Settings.Container, plan.Video, plan.VideoEncoder, plan.Arguments.Video,
            plan.Audio, wave, plan.AudioEncoder, plan.Arguments.Audio, plan.Video is null ? 0 : plan.FrameCount)
        {
            // Capítulos y subtítulos van en archivos que solo existen al exportar: la línea los omite.
            Metadata = plan.Metadata with { Chapters = [], Subtitles = [], ThumbnailPath = null },
            PassCount = plan.Arguments.PassCount,
            Pass = plan.Arguments.PassCount,
            PassLogPath = Path.Combine(Path.GetDirectoryName(plan.Request.OutputPath) ?? "", "pasadas"),
            VideoFilters = plan.VideoFilters,
            EncodedFrameCount = plan.Interlaced ? plan.EncodedFrameCount : null,
        };
        return FfmpegPipeSink.CommandArguments(request);
    }

    /// <summary>La línea entera, con <see cref="Executable"/> delante y cada argumento entrecomillado si hace falta.</summary>
    public static string Text(ExportPlan plan) => Format(Executable, Arguments(plan));

    /// <summary>
    /// Junta el ejecutable y los argumentos en una línea para la consola de Windows: un argumento con espacios, comillas o
    /// caracteres que el intérprete trataría aparte va entre comillas dobles, con las comillas de dentro escapadas como
    /// las entiende <c>CommandLineToArgvW</c> (las barras invertidas justo antes de una comilla se doblan).
    /// </summary>
    public static string Format(string executable, IEnumerable<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        var text = new StringBuilder(Quote(executable));
        foreach (string argument in arguments)
        {
            text.Append(' ').Append(Quote(argument));
        }

        return text.ToString();
    }

    /// <summary>El argumento tal como se escribe en la línea.</summary>
    public static string Quote(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c is '"' or '&' or '|' or '<' or '>' or '^' or '(' or ')' or ';' or ','))
        {
            return argument;
        }

        var quoted = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                quoted.Append('\\', (backslashes * 2) + 1).Append('"');
            }
            else
            {
                quoted.Append('\\', backslashes).Append(c);
            }

            backslashes = 0;
        }

        // Las barras del final se doblan: si no, la última escaparía la comilla de cierre.
        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
