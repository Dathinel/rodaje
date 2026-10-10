using System.Text;

namespace Rodaje.App.Services;

/// <summary>Dónde guarda Rodaje lo de la persona (ajustes, atajos) y cómo lo escribe sin dejar archivos a medias.</summary>
public static class AppDataPaths
{
    /// <summary>Variable que cambia la carpeta de datos (para pruebas o para llevar Rodaje en una memoria USB).</summary>
    public const string OverrideVariable = Rodaje.Media.EnvironmentVariables.Datos;

    /// <summary><c>%LOCALAPPDATA%\Rodaje</c>, o la carpeta de <see cref="OverrideVariable"/>.</summary>
    public static string Root =>
        Rodaje.Media.EnvironmentVariables.Read(OverrideVariable) is { } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rodaje");

    /// <summary>
    /// Escribe a un temporal, lo lleva al disco y lo pone en su lugar: un apagón deja el archivo anterior o el nuevo,
    /// nunca uno cortado.
    /// </summary>
    public static void WriteAllTextAtomic(string path, string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(text);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(text);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Aparta un archivo que no se pudo leer (con el sufijo dado) para no pisarlo al guardar; devuelve dónde quedó.</summary>
    public static string? SetAside(string path, string suffix)
    {
        string aside = Path.ChangeExtension(path, null) + suffix + Path.GetExtension(path);
        try
        {
            File.Move(path, aside, overwrite: true);
            return aside;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
