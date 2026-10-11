using System.Globalization;

namespace Rodaje.Media.Export;

/// <summary>
/// Lo que se comprueba y hace alrededor del archivo de salida: el temporal <c>.rodaje-tmp</c>, que la salida no pise un
/// medio de la secuencia, el espacio libre y el renombrado final.
/// </summary>
public static class ExportOutputGuard
{
    /// <summary>Marca de todo temporal de exportación; la limpieza tras un apagón solo borra rutas que la llevan.</summary>
    public const string TemporaryMarker = ".rodaje-tmp";

    /// <summary>Temporal junto a la salida: <c>video.mp4.rodaje-tmp</c> (o la carpeta <c>fotos.rodaje-tmp</c>).</summary>
    public static string TemporaryPath(string outputPath) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputPath)) + TemporaryMarker;

    /// <summary>
    /// Falla si la salida es uno de los medios de la secuencia, si la carpeta no se puede escribir o si el archivo de
    /// destino está abierto por otro programa.
    /// </summary>
    /// <exception cref="ExportException"><see cref="ExportErrorCode.InvalidSettings"/> u <see cref="ExportErrorCode.OutputNotWritable"/>.</exception>
    public static void CheckOutput(string outputPath, ExportContainer container, IEnumerable<string> sourcePaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(sourcePaths);
        string output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputPath));
        foreach (string source in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
            if (string.Equals(full, output, StringComparison.OrdinalIgnoreCase)
                || (container == ExportContainer.ImageSequence && full.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ExportException(ExportErrorCode.InvalidSettings,
                    $"La salida no puede ser un medio del proyecto: {full}. Elige otro nombre.") { Culprit = full };
            }
        }

        string? directory = Path.GetDirectoryName(output);
        if (directory is null)
        {
            throw new ExportException(ExportErrorCode.OutputNotWritable, $"No se puede escribir en la raíz de la unidad: {output}.");
        }

        try
        {
            Directory.CreateDirectory(directory);
            string probe = Path.Combine(directory, $"{Path.GetFileName(output)}{TemporaryMarker}.prueba");
            using (new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ExportException(ExportErrorCode.OutputNotWritable, $"No se puede escribir en {directory}: {ex.Message}", ex);
        }

        if (container == ExportContainer.ImageSequence)
        {
            if (File.Exists(output))
            {
                throw new ExportException(ExportErrorCode.OutputNotWritable, $"{output} es un archivo; la secuencia de imágenes necesita una carpeta.");
            }

            if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            {
                throw new ExportException(ExportErrorCode.OutputNotWritable, $"La carpeta {output} ya tiene archivos; elige una vacía.");
            }

            return;
        }

        if (Directory.Exists(output))
        {
            throw new ExportException(ExportErrorCode.OutputNotWritable, $"{output} es una carpeta.");
        }

        if (File.Exists(output))
        {
            try
            {
                using (new FileStream(output, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ExportException(ExportErrorCode.OutputNotWritable,
                    $"{output} está abierto en otro programa o es de solo lectura; ciérralo o elige otro nombre.", ex) { Culprit = output };
            }
        }
    }

    /// <summary>Falla si en el disco de <paramref name="path"/> no caben <paramref name="neededBytes"/> más la reserva.</summary>
    /// <exception cref="ExportException"><see cref="ExportErrorCode.DiskFull"/>.</exception>
    public static void CheckDiskSpace(string path, long neededBytes, long reserveBytes, Func<string, long>? freeSpace = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(neededBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(reserveBytes);
        string full = Path.GetFullPath(path);
        long free;
        try
        {
            free = freeSpace?.Invoke(full) ?? new DriveInfo(Path.GetPathRoot(full)!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return;
        }

        if (free < neededBytes + reserveBytes)
        {
            throw new ExportException(ExportErrorCode.DiskFull, string.Create(CultureInfo.GetCultureInfo("es-ES"),
                $"No cabe en el disco {Path.GetPathRoot(full)}: hay {Megabytes(free)} libres y hacen falta {Megabytes(neededBytes + reserveBytes)}. Libera espacio o elige otro disco."))
            {
                Culprit = full,
            };
        }
    }

    /// <summary>Renombra el temporal a la salida (reemplazándola) y devuelve los bytes escritos.</summary>
    /// <exception cref="ExportException"><see cref="ExportErrorCode.OutputNotWritable"/> si no se pudo renombrar.</exception>
    public static long Commit(string temporaryPath, string outputPath, ExportContainer container)
    {
        string output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputPath));
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (container == ExportContainer.ImageSequence)
                {
                    if (Directory.Exists(output))
                    {
                        Directory.Delete(output);
                    }

                    Directory.Move(temporaryPath, output);
                    return new DirectoryInfo(output).EnumerateFiles().Sum(f => f.Length);
                }

                File.Move(temporaryPath, output, overwrite: true);
                return new FileInfo(output).Length;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 20)
            {
                Thread.Sleep(50);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ExportException(ExportErrorCode.OutputNotWritable,
                    $"El archivo quedó en {temporaryPath} pero no se pudo renombrar a {output}: {ex.Message}", ex) { Culprit = output };
            }
        }
    }

    /// <summary>
    /// Borra un temporal (archivo o carpeta) y los que empiezan por su nombre seguido de «-» (registros de dos pasadas).
    /// Solo actúa sobre rutas con <see cref="TemporaryMarker"/>. Devuelve si quedó algo sin borrar.
    /// </summary>
    public static bool DeleteTemporary(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.Contains(TemporaryMarker, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        bool left = !TryDelete(path);
        string? directory = Path.GetDirectoryName(path);
        string name = Path.GetFileName(path);
        if (directory is not null && Directory.Exists(directory))
        {
            foreach (string sibling in Directory.EnumerateFiles(directory, name + "-*"))
            {
                left |= !TryDelete(sibling);
            }
        }

        return left;
    }

    private static bool TryDelete(string path)
    {
        for (int attempt = 0; attempt < 15; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else
                {
                    File.Delete(path);
                }

                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(20);
            }
        }

        return false;
    }

    private static string Megabytes(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1L << 20):0} MB";
}
