using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Packages;
using Rodaje.Packages.Content;

namespace Rodaje.App.Services.Packages;

/// <summary>Un sonido de un paquete instalado: su WAV, lo que dice su cabecera y con qué nombre se copia al proyecto.</summary>
/// <param name="CopyName">Nombre del archivo copiado: <c>&lt;paquete&gt;-&lt;elemento&gt;.wav</c>, igual entre versiones del paquete.</param>
public sealed record PackageSound(string Name, string FilePath, SoundInfo Info, string CopyName)
{
    public Time Duration => Time.FromSamples(Info.Frames, Info.SampleRate);
}

/// <summary>
/// Arrastrar un sonido (<c>sound</c>) de la biblioteca a la línea de tiempo: el WAV se copia a la carpeta de medios del
/// proyecto (así el proyecto no depende del paquete instalado) y entra como clip de audio donde cae.
/// </summary>
public static class SoundDrag
{
    /// <summary>Formato del arrastre; el texto es la referencia <c>paquete@versión/elemento</c>.</summary>
    public const string DragFormat = "rodaje/sonido";

    /// <summary>Subcarpeta de la carpeta de medios generados donde van las copias.</summary>
    public const string Folder = "sonidos";

    /// <summary>El sonido de esa referencia en lo instalado, con su cabecera ya leída; <c>null</c> si falta o no es un WAV válido.</summary>
    public static PackageSound? Resolve(PackageScan installed, string reference, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(installed);
        problem = null;
        if (!PackageReference.TryParse(reference, out PackageReference? parsed) || parsed is null)
        {
            problem = "La referencia del sonido no es válida.";
            return null;
        }

        PackageItemEntry? entry = installed.Find(parsed.PackageId) is { Manifest.Kind: PackageKind.Content } package
            && package.Manifest.FindItem(parsed.ItemId, ItemKind.Sound) is { } item
            ? new PackageItemEntry(package, item)
            : null;
        if (entry?.FilePath is not { } path)
        {
            problem = $"Falta el paquete {parsed.PackageId}.";
            return null;
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            SoundInfo info = ContentReaders.ReadSoundInfo(stream);
            return new PackageSound(entry.Item.Name, path, info, $"{entry.Package.Id}-{entry.Item.Id}.wav");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PackageContentException)
        {
            problem = $"{entry.Item.Name}: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// La pista bajo el puntero si es de audio, sin bloquear y libre en <c>[at, at + duración)</c>; si no, <c>null</c>: el
    /// sonido va a la primera pista de audio libre o a una nueva.
    /// </summary>
    public static Track? TargetTrack(Sequence sequence, Track? row, Time at, Time duration)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        Time end = at + duration;
        bool Free(Track t) => t is { Kind: TrackKind.Audio, Locked: false } && !t.Clips.Exists(c => c.Start < end && c.End > at);
        return row is not null && Free(row) ? row : sequence.AudioTracks.FirstOrDefault(Free);
    }

    /// <summary>
    /// Copia el WAV a <paramref name="folder"/>\<see cref="Folder"/> si no está ya (mismo nombre y tamaño) y devuelve la ruta
    /// de la copia.
    /// </summary>
    public static string CopyInto(PackageSound sound, string folder)
    {
        ArgumentNullException.ThrowIfNull(sound);
        string target = Path.Combine(folder, Folder, sound.CopyName);
        var existing = new FileInfo(target);
        if (existing.Exists && existing.Length == new FileInfo(sound.FilePath).Length)
        {
            return target;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string temporary = target + ".parcial";
        File.Copy(sound.FilePath, temporary, overwrite: true);
        File.Move(temporary, target, overwrite: true);
        return target;
    }
}
