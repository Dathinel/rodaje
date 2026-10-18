using Rodaje.Core.Timing;

namespace Rodaje.Core.Model;

/// <summary>Referencia a un archivo de origen y sus metadatos; el proyecto nunca copia el medio.</summary>
public sealed class MediaRef
{
    public string Id { get; set; } = "";

    /// <summary>Ruta del archivo, preferiblemente relativa a la carpeta del proyecto.</summary>
    public string Path { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Duración total del archivo; cero en imágenes fijas, cuyo origen virtual no tiene final.</summary>
    public Time Duration { get; set; }

    /// <summary>Imagen sin duración propia: sus clips usan un origen virtual y pueden alargarse.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsStillImage { get; set; }

    public const double DefaultStillImageDurationSeconds = 5;
    public const double MinStillImageDurationSeconds = 1;
    public const double MaxStillImageDurationSeconds = 60;

    /// <summary>Duración por defecto de una foto (1–60 s); configurable desde Preferencias.</summary>
    public static Time DefaultStillImageDuration { get; set; } = Time.FromSeconds(DefaultStillImageDurationSeconds);

    [System.Text.Json.Serialization.JsonIgnore]
    public Time InitialClipDuration => IsStillImage ? DefaultStillImageDuration : Duration;

    public bool HasVideo { get; set; }

    public bool HasAudio { get; set; }

    /// <summary>Tasa de cuadros del video, si tiene; una imagen fija no tiene.</summary>
    public Rational? FrameRate { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public int SampleRate { get; set; }

    public int AudioChannels { get; set; }

    /// <summary>Devuelve la ruta absoluta, resolviendo las relativas contra la carpeta del proyecto.</summary>
    /// <remarks>
    /// <c>\Videos\a.mp4</c> y <c>C:a.mp4</c> tienen raíz pero no son absolutas: se resuelven contra la
    /// carpeta del proyecto, nunca contra el directorio actual del proceso.
    /// Una ruta que, resuelta, pasa de lo que admite Windows (32 767 caracteres) no apunta a ningún archivo: se deja
    /// como está y el medio queda sin archivo, en vez de no abrir el proyecto.
    /// </remarks>
    public string ResolvePath(string projectDirectory)
    {
        if (System.IO.Path.IsPathFullyQualified(Path))
        {
            return Path;
        }

        try
        {
            return System.IO.Path.GetFullPath(Path, System.IO.Path.GetFullPath(projectDirectory));
        }
        catch (System.IO.PathTooLongException)
        {
            return Path;
        }
    }

    public MediaRef Clone() => (MediaRef)MemberwiseClone();
}
