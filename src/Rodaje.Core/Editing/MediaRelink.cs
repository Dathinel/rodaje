using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

/// <summary>
/// Lo que se sabe de un archivo al leerlo (los mismos datos que guarda un <see cref="MediaRef"/>), para compararlo con el
/// medio que falta o para actualizar el medio al volver a enlazarlo.
/// </summary>
public sealed record RelinkProbe(
    Time Duration, bool HasVideo, bool HasAudio, Rational? FrameRate, int Width, int Height, int SampleRate, int AudioChannels, bool IsStillImage = false)
{
    /// <summary>Los datos que el proyecto guarda de <paramref name="media"/>.</summary>
    public static RelinkProbe Of(MediaRef media)
    {
        ArgumentNullException.ThrowIfNull(media);
        return new(media.Duration, media.HasVideo, media.HasAudio, media.FrameRate, media.Width, media.Height, media.SampleRate, media.AudioChannels, media.IsStillImage);
    }

    /// <summary>Copia estos datos en el medio; la ruta, el nombre y el identificador no cambian.</summary>
    public void ApplyTo(MediaRef media)
    {
        ArgumentNullException.ThrowIfNull(media);
        media.Duration = Duration;
        media.IsStillImage = IsStillImage;
        media.HasVideo = HasVideo;
        media.HasAudio = HasAudio;
        media.FrameRate = FrameRate;
        media.Width = Width;
        media.Height = Height;
        media.SampleRate = SampleRate;
        media.AudioChannels = AudioChannels;
    }

    /// <summary>
    /// Parece el mismo archivo: las mismas pistas, el mismo tamaño de imagen y la misma duración con
    /// <paramref name="durationTolerance"/> de margen (otra versión de FFmpeg puede medir unos milisegundos distinto).
    /// </summary>
    public bool Matches(RelinkProbe expected, Time durationTolerance)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (IsStillImage != expected.IsStillImage || HasVideo != expected.HasVideo || HasAudio != expected.HasAudio)
        {
            return false;
        }

        if (expected.HasVideo && (Width != expected.Width || Height != expected.Height))
        {
            return false;
        }

        Time difference = Duration - expected.Duration;
        return (difference < Time.Zero ? -difference : difference) <= durationTolerance;
    }
}

/// <summary>Un medio que pasa a apuntar a <paramref name="Path"/>; con <paramref name="Probe"/>, también cambian sus datos.</summary>
/// <param name="MediaId">El medio de la biblioteca.</param>
/// <param name="Path">La ruta nueva, absoluta.</param>
/// <param name="Probe">
/// Los datos del archivo nuevo si son distintos de los del medio (un archivo elegido a mano que no es idéntico); <c>null</c>
/// para cambiar solo la ruta.
/// </param>
public sealed record MediaRelinkTarget(string MediaId, string Path, RelinkProbe? Probe = null);

/// <summary>Volver a enlazar medios movidos o renombrados.</summary>
public static class MediaRelinkEdits
{
    /// <summary>Margen de duración con el que un archivo se da por el mismo medio.</summary>
    public static readonly Time DefaultDurationTolerance = Time.FromSeconds(0.05);

    /// <summary>
    /// Los medios de la biblioteca cuyo archivo no está, en el orden de la biblioteca. <paramref name="exists"/> recibe la
    /// ruta tal como está en memoria (absoluta en la app).
    /// </summary>
    public static IReadOnlyList<MediaRef> FindMissing(Project project, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(exists);
        return [.. project.Media.Where(m => m.Path.Length == 0 || !exists(m.Path))];
    }

    /// <summary>Cuántos clips de todas las secuencias usan el medio (para la lista del diálogo).</summary>
    public static int CountClips(Project project, string mediaId)
    {
        ArgumentNullException.ThrowIfNull(project);
        return project.Sequences.Sum(s => s.AllTracks.Sum(t => t.Clips.Count(c => c.Generator is null && c.MediaId == mediaId)));
    }

    /// <summary>
    /// Por qué <paramref name="probe"/> no puede ocupar el lugar de <paramref name="media"/>: le falta la imagen o el
    /// sonido que usan sus clips, o es más corto que lo que usan. <c>null</c> si puede.
    /// </summary>
    public static string? CheckReplacement(Project project, MediaRef media, RelinkProbe probe)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(probe);
        if (!probe.IsStillImage && probe.Duration <= Time.Zero)
        {
            return "El archivo no tiene duración.";
        }

        Time used = Time.Zero;
        foreach (Sequence sequence in project.Sequences)
        {
            foreach (Track track in sequence.AllTracks)
            {
                foreach (Clip clip in track.Clips.Where(c => c.Generator is null && c.MediaId == media.Id))
                {
                    if (track.Kind == TrackKind.Video && !probe.HasVideo)
                    {
                        return $"El archivo no tiene imagen y «{media.Name}» se usa en la pista de video «{track.Name}».";
                    }

                    if (track.Kind == TrackKind.Audio && !probe.HasAudio)
                    {
                        return $"El archivo no tiene sonido y «{media.Name}» se usa en la pista de audio «{track.Name}».";
                    }

                    used = clip.SourceOut > used ? clip.SourceOut : used;
                }
            }
        }

        return !probe.IsStillImage && probe.Duration < used
            ? $"El archivo dura {probe.Duration.ToSeconds():0.##} s y los clips de «{media.Name}» usan hasta el segundo {used.ToSeconds():0.##}."
            : null;
    }

    /// <summary>
    /// La ruta completa y sin rodeos (<c>.</c>, <c>..</c>, <c>/</c> en vez de <c>\</c> en Windows) para comparar dos rutas
    /// que nombran el mismo archivo; si no se puede (una ruta vacía o con caracteres que no valen), la misma ruta.
    /// </summary>
    public static string NormalizePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return path;
        }
    }

    /// <summary>¿Nombran <paramref name="a"/> y <paramref name="b"/> el mismo archivo? Sin distinguir mayúsculas, como en Windows.</summary>
    public static bool SamePath(string a, string b) =>
        string.Equals(NormalizePath(a), NormalizePath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Por qué <paramref name="path"/> no puede ser el archivo de <paramref name="media"/> porque ya es el de otro medio del
    /// proyecto o el que se eligió para otro en <paramref name="pending"/> (cambios aún sin aplicar). Las rutas se comparan
    /// sin distinguir mayúsculas, como en Windows, y después de <see cref="NormalizePath"/>. <c>null</c> si está libre.
    /// </summary>
    public static string? CheckTaken(Project project, MediaRef media, string path, IEnumerable<MediaRelinkTarget>? pending = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(path);
        List<MediaRelinkTarget> others = [.. pending ?? []];
        foreach (MediaRelinkTarget target in others)
        {
            if (target.MediaId != media.Id && SamePath(target.Path, path))
            {
                string name = project.FindMedia(target.MediaId)?.Name ?? target.MediaId;
                return $"Ese archivo ya está elegido para «{name}»; un archivo no puede ser el de dos medios.";
            }
        }

        // Un medio que se va a enlazar con otro archivo deja libre su ruta de antes.
        var moving = others.Select(t => t.MediaId).ToHashSet(StringComparer.Ordinal);
        MediaRef? owner = project.Media.FirstOrDefault(m =>
            m.Id != media.Id && !moving.Contains(m.Id) && SamePath(m.Path, path));
        return owner is null ? null : $"Ese archivo ya es el de «{owner.Name}»; un archivo no puede ser el de dos medios.";
    }

    /// <summary>
    /// El cambio para enlazar <paramref name="media"/> con un archivo elegido a mano: solo la ruta si el archivo coincide con
    /// el medio, o también sus datos si no. Lanza <see cref="EditException"/> si el archivo no puede ocupar su lugar o ya es
    /// el de otro medio del proyecto o de <paramref name="pending"/> (ver <see cref="CheckTaken"/>).
    /// </summary>
    public static MediaRelinkTarget TargetFor(
        Project project, MediaRef media, string path, RelinkProbe probe, Time? durationTolerance = null, IEnumerable<MediaRelinkTarget>? pending = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (CheckTaken(project, media, path, pending) is { } taken)
        {
            throw new EditException(taken);
        }

        if (CheckReplacement(project, media, probe) is { } problem)
        {
            throw new EditException(problem);
        }

        bool same = probe.Matches(RelinkProbe.Of(media), durationTolerance ?? DefaultDurationTolerance);
        return new MediaRelinkTarget(media.Id, path, same ? null : probe);
    }

    /// <summary>
    /// Cambia la ruta (y, si se dan, los datos) de cada medio. Los clips, los efectos y los keyframes no se tocan: siguen
    /// apuntando al mismo identificador de medio. Lanza <see cref="EditException"/> sin cambiar nada si algún medio no
    /// existe, se repite, una ruta está vacía o un archivo quedaría como el de dos medios: dos destinos iguales en el lote, o
    /// un destino igual a la ruta de un medio que no se reenlaza en el lote (sin distinguir mayúsculas y después de
    /// <see cref="NormalizePath"/>). Intercambiar las rutas de dos medios en el mismo lote sí vale.
    /// </summary>
    public static void Relink(Project project, IReadOnlyList<MediaRelinkTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(targets);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var media = new List<MediaRef>(targets.Count);
        foreach (MediaRelinkTarget target in targets)
        {
            ArgumentNullException.ThrowIfNull(target);
            if (!seen.Add(target.MediaId))
            {
                throw new EditException($"El medio «{target.MediaId}» se volvió a enlazar dos veces en el mismo paso.");
            }

            if (string.IsNullOrWhiteSpace(target.Path))
            {
                throw new EditException($"Falta la ruta nueva del medio «{target.MediaId}».");
            }

            media.Add(project.FindMedia(target.MediaId) ?? throw new EditException($"No existe el medio «{target.MediaId}» en el proyecto."));
        }

        // Un archivo no puede quedar como el de dos medios. Se mira antes de cambiar nada.
        var destinations = new Dictionary<string, MediaRef>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < targets.Count; i++)
        {
            if (!destinations.TryAdd(NormalizePath(targets[i].Path), media[i]))
            {
                MediaRef first = destinations[NormalizePath(targets[i].Path)];
                throw new EditException(
                    $"«{first.Name}» y «{media[i].Name}» se volverían a enlazar con el mismo archivo; un archivo no puede ser el de dos medios.");
            }
        }

        foreach (MediaRef other in project.Media.Where(m => !seen.Contains(m.Id) && m.Path.Length > 0))
        {
            if (destinations.TryGetValue(NormalizePath(other.Path), out MediaRef? relinked))
            {
                throw new EditException(
                    $"El archivo nuevo de «{relinked.Name}» ya es el de «{other.Name}»; un archivo no puede ser el de dos medios.");
            }
        }

        for (int i = 0; i < targets.Count; i++)
        {
            media[i].Path = targets[i].Path;
            targets[i].Probe?.ApplyTo(media[i]);
        }
    }
}
