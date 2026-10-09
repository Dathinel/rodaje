using FotoToma.Core.Timing;

namespace FotoToma.Core.Model;

/// <summary>
/// Referencia a un archivo de origen. El proyecto nunca guarda video ni audio: solo dónde está el
/// archivo y lo que se sabe de él, de modo que editar no toca el original.
/// </summary>
public sealed class MediaRef
{
    public string Id { get; set; } = "";

    /// <summary>Ruta del archivo, preferiblemente relativa a la carpeta del proyecto.</summary>
    public string Path { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Duración total del archivo. Los puntos de entrada y salida de los clips no pueden pasarla.</summary>
    public Time Duration { get; set; }

    public bool HasVideo { get; set; }

    public bool HasAudio { get; set; }

    /// <summary>Tasa de cuadros del video, si tiene; una imagen fija no tiene.</summary>
    public Rational? FrameRate { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public int SampleRate { get; set; }

    public int AudioChannels { get; set; }

    /// <summary>Devuelve la ruta absoluta, resolviendo las relativas contra la carpeta del proyecto.</summary>
    public string ResolvePath(string projectDirectory) =>
        System.IO.Path.IsPathRooted(Path) ? Path : System.IO.Path.GetFullPath(System.IO.Path.Combine(projectDirectory, Path));

    public MediaRef Clone() => (MediaRef)MemberwiseClone();
}
