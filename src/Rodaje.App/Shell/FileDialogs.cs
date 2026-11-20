using Rodaje.App.Services.Media;

namespace Rodaje.App.Shell;

/// <summary>Diálogos de abrir y guardar archivos; cada sistema los muestra en su archivo (<c>FileDialogs.Windows.cs</c>, <c>FileDialogs.Desktop.cs</c>).</summary>
internal static partial class FileDialogs
{
    public static readonly (string Name, string Spec) ProjectFilter = ("Proyectos de Rodaje", "*.rodaje");

    public static readonly (string Name, string Spec) MediaFilter =
        ("Videos, audios e imágenes", string.Join(';', WatchedFolderScanner.MediaExtensions.Select(e => "*" + e)));

    public static readonly (string Name, string Spec) AllFilter = ("Todos los archivos", "*.*");
}