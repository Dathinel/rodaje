using Rodaje.Core.Timing;

namespace Rodaje.Core.Model;

/// <summary>Comprueba las invariantes de un proyecto.</summary>
public static class ModelValidator
{
    /// <summary>Devuelve la lista de problemas encontrados, en español; vacía si el proyecto es válido.</summary>
    public static IReadOnlyList<string> Validate(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        void CheckId(string id, string what)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                errors.Add($"Hay {what} sin identificador.");
            }
            else if (!ids.Add(id))
            {
                errors.Add($"El identificador «{id}» está repetido ({what}).");
            }
        }

        if (project.SchemaVersion != Project.CurrentSchemaVersion)
        {
            errors.Add($"La versión de formato es {project.SchemaVersion} y se esperaba {Project.CurrentSchemaVersion}.");
        }

        CheckId(project.Id, "un proyecto");

        foreach (MediaRef media in project.Media)
        {
            CheckId(media.Id, "un medio");
            if (media.Duration <= Time.Zero)
            {
                errors.Add($"El medio «{media.Id}» tiene una duración no positiva.");
            }

            if (!IsUsablePath(media.Path))
            {
                errors.Add($"El medio «{media.Id}» tiene una ruta con caracteres que no puede tener una ruta de archivo.");
            }
        }

        foreach (Sequence sequence in project.Sequences)
        {
            CheckId(sequence.Id, "una secuencia");
            ValidateSequence(project, sequence, errors, CheckId);
        }

        return errors;
    }

    public static void ThrowIfInvalid(Project project)
    {
        IReadOnlyList<string> errors = Validate(project);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("El proyecto no es válido:\n- " + string.Join("\n- ", errors));
        }
    }

    /// <summary>Problemas de una sola secuencia.</summary>
    public static IReadOnlyList<string> ValidateSequence(Project project, Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);
        var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        ValidateSequence(project, sequence, errors, (id, what) =>
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                errors.Add($"Hay {what} sin identificador.");
            }
            else if (!ids.Add(id))
            {
                errors.Add($"El identificador «{id}» está repetido ({what}).");
            }
        });
        return errors;
    }

    private static void ValidateSequence(Project project, Sequence sequence, List<string> errors, Action<string, string> checkId)
    {
        if (sequence.Width <= 0 || sequence.Height <= 0 || sequence.SampleRate <= 0)
        {
            errors.Add($"La secuencia «{sequence.Id}» tiene dimensiones o frecuencia de muestreo no positivas.");
        }

        // default(Rational) es 0/0: el constructor lo impide, pero puede llegar desde un archivo
        if (sequence.FrameRate.Num <= 0 || sequence.FrameRate.Den <= 0)
        {
            errors.Add($"La secuencia «{sequence.Id}» tiene una tasa de cuadros no válida.");
        }

        CheckTrackKinds(sequence.VideoTracks, TrackKind.Video, sequence, errors);
        CheckTrackKinds(sequence.AudioTracks, TrackKind.Audio, sequence, errors);

        var linkTracks = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (Track track in sequence.AllTracks)
        {
            checkId(track.Id, "una pista");
            Clip? previous = null;
            Int128 previousEnd = 0;
            foreach (Clip clip in track.Clips)
            {
                checkId(clip.Id, "un clip");
                string where = $"El clip «{clip.Id}» de la pista «{track.Name}»";

                MediaRef? media = project.FindMedia(clip.MediaId);
                if (media is null)
                {
                    errors.Add($"{where} usa el medio «{clip.MediaId}», que no existe.");
                }
                else
                {
                    if (clip.SourceOut > media.Duration)
                    {
                        errors.Add($"{where} termina en {clip.SourceOut} y el medio solo dura {media.Duration}.");
                    }

                    if (track.Kind == TrackKind.Video && !media.HasVideo)
                    {
                        errors.Add($"{where} está en una pista de video pero su medio no tiene imagen.");
                    }

                    if (track.Kind == TrackKind.Audio && !media.HasAudio)
                    {
                        errors.Add($"{where} está en una pista de audio pero su medio no tiene sonido.");
                    }
                }

                if (clip.SourceIn < Time.Zero)
                {
                    errors.Add($"{where} tiene un punto de entrada negativo.");
                }

                if (clip.SourceOut <= clip.SourceIn)
                {
                    errors.Add($"{where} tiene duración cero o negativa.");
                }

                if (clip.Start < Time.Zero)
                {
                    errors.Add($"{where} empieza antes del inicio de la línea de tiempo.");
                }

                // 128 bits: un valor extremo leído de un archivo se informa como problema, no como OverflowException.
                Int128 duration = (Int128)clip.SourceOut.Ticks - clip.SourceIn.Ticks;
                Int128 end = clip.Start.Ticks + duration;
                if (!FitsInLong(duration) || !FitsInLong(end))
                {
                    errors.Add($"{where} tiene tiempos fuera de rango.");
                }

                if (previous is not null && clip.Start.Ticks < previousEnd)
                {
                    errors.Add($"{where} se solapa con «{previous.Id}» o está fuera de orden.");
                }

                foreach (EffectInstance effect in clip.Effects)
                {
                    checkId(effect.Id, "un efecto");
                    foreach ((string name, Parameter parameter) in effect.Parameters)
                    {
                        for (int i = 1; i < parameter.Keyframes.Count; i++)
                        {
                            if (parameter.Keyframes[i].Time <= parameter.Keyframes[i - 1].Time)
                            {
                                errors.Add($"Los keyframes de «{name}» en el efecto «{effect.Id}» no están en orden estricto.");
                                break;
                            }
                        }
                    }
                }

                if (clip.LinkId is not null)
                {
                    if (!linkTracks.TryGetValue(clip.LinkId, out HashSet<string>? tracks))
                    {
                        linkTracks[clip.LinkId] = tracks = new HashSet<string>(StringComparer.Ordinal);
                    }

                    if (!tracks.Add(track.Id))
                    {
                        errors.Add($"El enlace «{clip.LinkId}» tiene dos clips en la misma pista «{track.Name}».");
                    }
                }

                previous = clip;
                previousEnd = end;
            }
        }

        foreach (Marker marker in sequence.Markers)
        {
            checkId(marker.Id, "un marcador");
            if (marker.Time < Time.Zero)
            {
                errors.Add($"El marcador «{marker.Id}» está antes del inicio de la línea de tiempo.");
            }
        }

        foreach (Region region in sequence.Regions)
        {
            checkId(region.Id, "una región");
            if (region.Start < Time.Zero)
            {
                errors.Add($"La región «{region.Id}» empieza antes del inicio de la línea de tiempo.");
            }

            if (region.End <= region.Start)
            {
                errors.Add($"La región «{region.Id}» tiene duración cero o negativa.");
            }
        }
    }

    private static bool FitsInLong(Int128 value) => value >= long.MinValue && value <= long.MaxValue;

    // Un archivo editado a mano puede traer caracteres que Windows no admite en una ruta; resolverla lanzaría al abrir.
    private static bool IsUsablePath(string? path) =>
        path is not null && !path.Any(c => c < ' ' || c is '"' or '<' or '>' or '|');

    private static void CheckTrackKinds(List<Track> tracks, TrackKind expected, Sequence sequence, List<string> errors)
    {
        foreach (Track track in tracks)
        {
            if (track.Kind == expected)
            {
                continue;
            }

            errors.Add($"La pista «{track.Name}» de la secuencia «{sequence.Id}» está en la lista equivocada (es de tipo {track.Kind}).");
        }
    }
}
