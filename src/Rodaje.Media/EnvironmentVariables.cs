namespace Rodaje.Media;

/// <summary>
/// Las variables de entorno <c>RODAJE_*</c> que lee el editor. Las que solo usan las pruebas y los scripts están
/// en <c>testing\Rodaje.TestMedia\Skip.cs</c> y en <c>herramientas\</c>; la tabla completa, en
/// <c>docs\variables-entorno.md</c>.
/// </summary>
public static class EnvironmentVariables
{
    /// <summary>Carpeta con las DLL de FFmpeg (y ffmpeg.exe); se ignora si junto al ejecutable hay <c>ffmpeg\</c>.</summary>
    public const string Ffmpeg = "RODAJE_FFMPEG";

    /// <summary>Otro <c>.entorno</c> (SDK, FFmpeg), por ejemplo el del checkout principal desde un worktree.</summary>
    public const string Entorno = "RODAJE_ENTORNO";

    /// <summary>Carpeta de los proxies; sin ella, <c>%LOCALAPPDATA%\Rodaje\proxies</c>.</summary>
    public const string Proxies = "RODAJE_PROXIES";

    /// <summary>Carpeta de los ajustes y atajos de la app; sin ella, <c>%LOCALAPPDATA%\Rodaje</c>.</summary>
    public const string Datos = "RODAJE_DATOS";

    /// <summary>Ruta de un informe: la línea de tiempo arma un proyecto de 200 clips, mide desplazamiento, zoom y arrastre, lo escribe y cierra.</summary>
    public const string MedirLinea = "RODAJE_MEDIR_LINEA";

    /// <summary>El valor de la variable, o <c>null</c> si no está definida o está en blanco.</summary>
    public static string? Read(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
