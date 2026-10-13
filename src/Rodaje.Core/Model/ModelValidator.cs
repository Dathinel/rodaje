using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Model;

/// <summary>Comprueba las invariantes de un proyecto.</summary>
public static partial class ModelValidator
{
    /// <summary>Devuelve la lista de problemas encontrados, en español; vacía si el proyecto es válido.</summary>
    public static IReadOnlyList<string> Validate(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var errors = new List<string>();
        Action<string, string> checkId = IdChecker(errors);

        if (project.SchemaVersion != Project.CurrentSchemaVersion)
        {
            errors.Add($"La versión de formato es {project.SchemaVersion} y se esperaba {Project.CurrentSchemaVersion}.");
        }

        checkId(project.Id, "un proyecto");

        foreach (MediaRef media in project.Media)
        {
            checkId(media.Id, "un medio");
            if (media.Duration <= Time.Zero)
            {
                errors.Add($"El medio «{media.Id}» tiene una duración no positiva.");
            }

            // Una ruta más larga de lo que admite Windows no se rechaza (la v1.0.0 la guardaba): el medio queda sin archivo.
            if (!IsUsablePath(media.Path))
            {
                errors.Add($"El medio «{media.Id}» tiene una ruta con caracteres que no puede tener una ruta de archivo.");
            }
        }

        foreach (Sequence sequence in project.Sequences)
        {
            checkId(sequence.Id, "una secuencia");
            ValidateSequence(project, sequence, errors, checkId);
        }

        long text = project.Sequences.SelectMany(s => s.AllTracks).SelectMany(t => t.Clips).Sum(c => (long)(c.Generator?.Text.Length ?? 0) + WordsLength(c));
        if (text > MaxProjectTextLength)
        {
            errors.Add($"Los textos del proyecto suman {text} caracteres y el máximo es {MaxProjectTextLength}.");
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
        ValidateSequence(project, sequence, errors, IdChecker(errors));
        return errors;
    }

    /// <summary>Identificadores no vacíos, únicos y sin caracteres de control (un salto de línea rompería los mensajes y la interfaz).</summary>
    private static Action<string, string> IdChecker(List<string> errors)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        return (id, what) =>
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                errors.Add($"Hay {what} sin identificador.");
            }
            else if (id.Any(char.IsControl))
            {
                errors.Add($"Hay {what} con un identificador que tiene caracteres de control.");
            }
            else if (!ids.Add(id))
            {
                errors.Add($"El identificador «{id}» está repetido ({what}).");
            }
        };
    }

    private static void ValidateSequence(Project project, Sequence sequence, List<string> errors, Action<string, string> checkId)
    {
        if (sequence.Width <= 0 || sequence.Height <= 0 || sequence.SampleRate <= 0)
        {
            errors.Add($"La secuencia «{sequence.Id}» tiene dimensiones o frecuencia de muestreo no positivas.");
        }

        if (!double.IsFinite(sequence.MasterGainDb) || sequence.MasterGainDb < Sequence.MinMasterGainDb || sequence.MasterGainDb > Sequence.MaxMasterGainDb)
        {
            errors.Add($"La secuencia «{sequence.Id}» tiene una ganancia maestra fuera de rango ({Sequence.MinMasterGainDb} a +{Sequence.MaxMasterGainDb} dB).");
        }

        // default(Rational) es 0/0: el constructor lo impide, pero puede llegar desde un archivo
        if (sequence.FrameRate.Num <= 0 || sequence.FrameRate.Den <= 0)
        {
            errors.Add($"La secuencia «{sequence.Id}» tiene una tasa de cuadros no válida.");
        }
        else if (!Sequence.IsSupportedFrameRate(sequence.FrameRate))
        {
            // Una tasa extrema (1/2^62 o 2^62 fps) es una fracción válida, pero un cuadro de más de 400 años o de menos de
            // un tick hacía desbordar empujar, recortar o cortar en el cursor. Al abrir se acota (TimelineLimits).
            errors.Add($"La secuencia «{sequence.Id}» tiene una tasa de {sequence.FrameRate} cuadros por segundo; va de {Sequence.MinFrameRate} a {Sequence.MaxFrameRate}.");
        }

        CheckTrackKinds(sequence.VideoTracks, TrackKind.Video, sequence, errors);
        CheckTrackKinds(sequence.AudioTracks, TrackKind.Audio, sequence, errors);
        ValidateEffects(sequence.MasterEffects, new Track { Kind = TrackKind.Audio, Name = "mezcla" }, errors, checkId);
        ValidateMixer(sequence, errors, checkId);

        var linkTracks = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var linkSpeeds = new Dictionary<string, ClipSpeed?>(StringComparer.Ordinal);

        foreach (Track track in sequence.AllTracks)
        {
            checkId(track.Id, "una pista");
            ValidateRole(track, errors);
            if (track.Height is { } height && (height < Track.MinHeight || height > Track.MaxHeight))
            {
                errors.Add($"La pista «{track.Name}» tiene un alto de {height} px; va de {Track.MinHeight} a {Track.MaxHeight}.");
            }

            Clip? previous = null;
            Int128 previousEnd = 0;
            foreach (Clip clip in track.Clips)
            {
                checkId(clip.Id, "un clip");
                string where = $"El clip «{clip.Id}» de la pista «{track.Name}»";

                MediaRef? media = clip.Generator is null && !clip.IsCompound ? project.FindMedia(clip.MediaId) : null;
                if (clip.IsCompound)
                {
                    ValidateCompound(project, sequence, clip, where, errors);
                }
                else if (clip.Generator is not null)
                {
                    if (clip.MediaId.Length > 0)
                    {
                        errors.Add($"{where} tiene a la vez un medio y un generador.");
                    }
                }
                else if (media is null)
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

                // Un generador tiene un origen virtual sin límites: recortar su inicio puede dejar el punto de entrada negativo.
                if (clip.SourceIn < Time.Zero && clip.Generator is null)
                {
                    errors.Add($"{where} tiene un punto de entrada negativo.");
                }

                if (clip.SourceOut <= clip.SourceIn || (clip.Speed is not null && clip.Duration <= Time.Zero))
                {
                    errors.Add($"{where} tiene duración cero o negativa.");
                }

                if (clip.Start < Time.Zero)
                {
                    errors.Add($"{where} empieza antes del inicio de la línea de tiempo.");
                }

                // 128 bits: un valor extremo leído de un archivo se informa como problema, no como OverflowException.
                Int128 duration = TimelineDuration(clip);
                Int128 end = clip.Start.Ticks + duration;
                if (!FitsInLong((Int128)clip.SourceOut.Ticks - clip.SourceIn.Ticks) || !FitsInLong(end))
                {
                    errors.Add($"{where} tiene tiempos fuera de rango.");
                }
                else if (end > Sequence.MaxTime.Ticks)
                {
                    errors.Add($"{where} termina después del final de la línea de tiempo ({Sequence.MaxTime.Ticks} ticks).");
                }

                if (previous is not null && clip.Start.Ticks < previousEnd)
                {
                    errors.Add($"{where} se solapa con «{previous.Id}» o está fuera de orden.");
                }

                if (clip.Speed is { } speed)
                {
                    ValidateSpeed(clip, speed, track, where, errors);
                }

                if (clip.Generator is { } generator)
                {
                    ValidateGenerator(clip, generator, track, where, errors);
                }

                ValidateEffects(clip.Effects, null, errors, checkId);

                if (clip.Color is not null && !MarkerStyle.IsColor(clip.Color))
                {
                    errors.Add($"{where} tiene un color que no es #RRGGBB: «{clip.Color}».");
                }

                if (clip.GroupId is { } groupId && (groupId.Length == 0 || groupId.Any(char.IsControl)))
                {
                    errors.Add($"{where} tiene un grupo vacío o con caracteres de control.");
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

                    if (!linkSpeeds.TryAdd(clip.LinkId, clip.Speed) && !SameTiming(linkSpeeds[clip.LinkId], clip.Speed))
                    {
                        errors.Add($"Los clips del enlace «{clip.LinkId}» no tienen la misma velocidad.");
                    }
                }

                previous = clip;
                previousEnd = end;
            }

            ValidateEffects(track.Effects, track, errors, checkId);
            ValidateTransitions(track, errors, checkId);
        }

        foreach (Marker marker in sequence.Markers)
        {
            checkId(marker.Id, "un marcador");
            if (marker.Time < Time.Zero)
            {
                errors.Add($"El marcador «{marker.Id}» está antes del inicio de la línea de tiempo.");
            }

            ValidateMarkerStyle("El marcador", marker.Id, marker.Color, marker.Note, errors);
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

            ValidateMarkerStyle("La región", region.Id, region.Color, region.Note, errors);
        }
    }

    /// <summary>Buses con nombre y solo efectos de audio; salidas y envíos de pistas de audio a buses que existen, uno por bus.</summary>
    private static void ValidateMixer(Sequence sequence, List<string> errors, Action<string, string> checkId)
    {
        var buses = new HashSet<string>(StringComparer.Ordinal);
        foreach (AudioBus bus in sequence.AudioBuses)
        {
            checkId(bus.Id, "un bus");
            buses.Add(bus.Id);
            if (string.IsNullOrWhiteSpace(bus.Name))
            {
                errors.Add($"El bus «{bus.Id}» no tiene nombre.");
            }

            ValidateEffects(bus.Effects, new Track { Kind = TrackKind.Audio, Name = bus.Name }, errors, checkId);
        }

        foreach (Track track in sequence.AllTracks)
        {
            if (track.Kind != TrackKind.Audio && (track.Output is not null || track.Sends.Count > 0 || track.Solo || track.Armed))
            {
                errors.Add($"La pista de video «{track.Name}» tiene salida, envíos, solo o grabación, que son de las de audio.");
                continue;
            }

            if (track.Output is { } output && !buses.Contains(output))
            {
                errors.Add($"La pista «{track.Name}» sale a un bus que no existe («{output}»).");
            }

            var sent = new HashSet<string>(StringComparer.Ordinal);
            foreach (AudioSend send in track.Sends)
            {
                if (send is null || !buses.Contains(send.BusId))
                {
                    errors.Add($"La pista «{track.Name}» tiene un envío a un bus que no existe.");
                }
                else if (!sent.Add(send.BusId))
                {
                    errors.Add($"La pista «{track.Name}» tiene dos envíos al bus «{send.BusId}».");
                }
                else if (!double.IsFinite(send.GainDb) || send.GainDb < AudioSend.MinGainDb || send.GainDb > AudioSend.MaxGainDb)
                {
                    errors.Add($"El envío de la pista «{track.Name}» al bus «{send.BusId}» está fuera de rango ({AudioSend.MinGainDb} a +{AudioSend.MaxGainDb} dB).");
                }
            }
        }
    }

    private static void ValidateMarkerStyle(string what, string id, string? color, string? note, List<string> errors)
    {
        if (color is not null && !MarkerStyle.IsColor(color))
        {
            errors.Add($"{what} «{id}» tiene un color que no es #RRGGBB: «{color}».");
        }

        if (note is { Length: > MarkerStyle.MaxNoteLength })
        {
            errors.Add($"{what} «{id}» tiene una nota de más de {MarkerStyle.MaxNoteLength} caracteres.");
        }
    }

    /// <summary>
    /// Keyframes en orden y, en los tipos registrados, opciones aceptadas por el registro. En una pista
    /// (<paramref name="track"/> no nulo) solo van efectos de su tipo: de audio en las de audio y de video en las de video.
    /// Un tipo desconocido se conserva sin mirar sus opciones.
    /// </summary>
    private static void ValidateEffects(List<EffectInstance> effects, Track? track, List<string> errors, Action<string, string> checkId)
    {
        foreach (EffectInstance effect in effects)
        {
            checkId(effect.Id, "un efecto");
            if (EffectRegistry.Find(effect.Type) is { } definition)
            {
                if (definition.IsTransition)
                {
                    errors.Add($"El efecto «{effect.Id}» es de tipo «{effect.Type}», que es una transición.");
                }
                else if (track is not null && (definition.Target & TargetOf(track.Kind)) == 0)
                {
                    errors.Add($"El efecto «{effect.Id}» ({effect.Type}) no se puede aplicar a la pista de {TrackOps.KindName(track.Kind)} «{track.Name}».");
                }

                if (definition.Hidden)
                {
                    errors.Add($"El efecto «{effect.Id}» es de tipo «{effect.Type}», que está reservado y no se usa.");
                }

                ValidateOptions(definition, effect.Options, $"El efecto «{effect.Id}»", errors);
                foreach ((string name, Parameter parameter) in effect.Parameters)
                {
                    if (definition.FindParameter(name) is { } p && parameter is not null
                        && (!InRange(p, parameter.Value) || parameter.Keyframes.Exists(k => k is not null && !InRange(p, k.Value))))
                    {
                        errors.Add($"El parámetro «{name}» del efecto «{effect.Id}» está fuera de su rango ({p.Minimum} a {p.Maximum}).");
                    }
                }
            }

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

                foreach (Keyframe key in parameter.Keyframes)
                {
                    if (key is null)
                    {
                        continue;
                    }

                    if (key.Ease is { IsValid: false })
                    {
                        errors.Add($"La curva Bézier de un keyframe de «{name}» en el efecto «{effect.Id}» está fuera de rango (x de 0 a 1, y de {BezierEase.MinY} a {BezierEase.MaxY}).");
                    }

                    if ((key.SpatialIn is { } spatialIn && !double.IsFinite(spatialIn)) || (key.SpatialOut is { } spatialOut && !double.IsFinite(spatialOut)))
                    {
                        errors.Add($"Una tangente de trayectoria de «{name}» en el efecto «{effect.Id}» no es un número.");
                    }
                }
            }
        }
    }

    /// <summary>Cada transición une dos clips contiguos de su pista, en un solo corte y con duración positiva.</summary>
    private static void ValidateTransitions(Track track, List<string> errors, Action<string, string> checkId)
    {
        var cuts = new HashSet<string>(StringComparer.Ordinal);
        foreach (TransitionInstance transition in track.Transitions)
        {
            checkId(transition.Id, "una transición");
            string where = $"La transición «{transition.Id}» de la pista «{track.Name}»";
            int from = track.Clips.FindIndex(c => c.Id == transition.From);
            if (from < 0 || from + 1 >= track.Clips.Count || track.Clips[from + 1].Id != transition.To
                || !Touches(track.Clips[from], track.Clips[from + 1]))
            {
                errors.Add($"{where} no une dos clips contiguos de la pista («{transition.From}» y «{transition.To}»).");
            }
            else if (!cuts.Add(transition.From))
            {
                errors.Add($"{where} está en un corte que ya tiene otra transición.");
            }
            else if (transition.Alignment == TransitionAlignment.Center
                ? transition.Duration.Ticks > Int128.Min(TimelineDuration(track.Clips[from]), TimelineDuration(track.Clips[from + 1]))
                : transition.Duration.Ticks > TimelineDuration(track.Clips[transition.Alignment == TransitionAlignment.Before ? from : from + 1]))
            {
                errors.Add($"{where} dura más que uno de sus clips.");
            }
            else if (track.TransitionTo(transition.From) is { } entering && entering != transition
                && (Int128)entering.After.Ticks + transition.Before.Ticks > TimelineDuration(track.Clips[from]))
            {
                errors.Add($"{where} se pisa con «{entering.Id}» en el clip «{transition.From}».");
            }

            if (transition.Duration <= Time.Zero)
            {
                errors.Add($"{where} tiene duración cero o negativa.");
            }

            if (!Enum.IsDefined(transition.Alignment))
            {
                errors.Add($"{where} tiene una alineación desconocida.");
            }

            if (transition.Curve is { } curve && !Enum.IsDefined(curve))
            {
                errors.Add($"{where} tiene una curva desconocida.");
            }

            if (transition.Ease is { IsValid: false })
            {
                errors.Add($"{where} tiene puntos de la curva Bézier fuera de sus límites.");
            }

            if (EffectRegistry.Find(transition.Type) is { } definition)
            {
                if (!definition.IsTransition)
                {
                    errors.Add($"{where} es de tipo «{transition.Type}», que no es una transición.");
                }
                else if (BuiltInTransitions.IsAudio(transition.Type) && track.Kind != TrackKind.Audio)
                {
                    errors.Add($"{where} es de audio y la pista no.");
                }

                ValidateOptions(definition, transition.Options, where, errors);
                foreach ((string name, double value) in transition.Parameters)
                {
                    if (definition.FindParameter(name) is { } p && !InRange(p, value))
                    {
                        errors.Add($"{where} tiene «{name}» fuera de su rango ({p.Minimum} a {p.Maximum}).");
                    }
                }
            }
        }
    }

    /// <summary>Las opciones que el tipo define deben tener un valor aceptado; las demás se conservan sin mirar.</summary>
    private static void ValidateOptions(EffectDefinition definition, Dictionary<string, string> options, string where, List<string> errors)
    {
        foreach ((string name, string value) in options)
        {
            bool accepted = value is not null && (definition.FindOption(name) is { } option
                ? option.Accepts(value)
                : name != BuiltInEffects.CommonOptions.Source || OptionDefinition.IsWellFormedReference(value));
            if (!accepted)
            {
                errors.Add($"{where} tiene «{value}» en la opción «{name}», que no es un valor válido.");
            }
        }
    }

    /// <summary>Las reglas de <see cref="Clip.Speed"/>: tasa y curva en rango, cotas del mapeo y tramo del origen coherente.</summary>
    private static void ValidateSpeed(Clip clip, ClipSpeed speed, Track track, string where, List<string> errors)
    {
        int before = errors.Count;
        bool unitRate = speed.Rate == new Rational(1, 1);
        if (!RetimeMath.IsValidRate(speed.Rate))
        {
            errors.Add($"{where} tiene una velocidad fuera de 0,1× a 100×.");
        }

        if (speed.Curve is { } curve)
        {
            if (RetimeMath.CurveProblem(curve) is { } problem)
            {
                errors.Add($"{where}: {problem}");
            }

            if (!unitRate || speed.Freeze)
            {
                errors.Add($"{where} tiene una curva de velocidad junto con una velocidad constante o un congelado.");
            }
        }

        if (speed.Freeze)
        {
            if (speed.Reverse || !unitRate)
            {
                errors.Add($"{where} está congelado y a la vez invertido o con otra velocidad.");
            }

            if (track.Kind != TrackKind.Video)
            {
                errors.Add($"{where} está congelado en una pista de audio.");
            }
        }

        if (!Enum.IsDefined(speed.FrameSampling))
        {
            errors.Add($"{where} tiene un muestreo de cuadros desconocido.");
        }

        if (Int128.Abs(speed.Offset.Ticks) + clip.Duration.Ticks > RetimeMath.MaxExtent)
        {
            errors.Add($"{where} pasa del largo máximo de un clip con velocidad.");
        }

        if (errors.Count > before || clip.Duration <= Time.Zero || clip.SourceOut <= clip.SourceIn)
        {
            return;
        }

        ClipTimeMap map;
        try
        {
            map = ClipTimeMap.Of(clip);
        }
        catch (OverflowException)
        {
            errors.Add($"{where} tiene tiempos fuera de rango.");
            return;
        }

        if (map.SourceIn != clip.SourceIn || map.SourceOut != clip.SourceOut)
        {
            errors.Add($"{where} tiene un tramo del origen que no corresponde a su velocidad y duración (sería [{map.SourceIn}, {map.SourceOut})).");
        }
    }

    private static bool SameTiming(ClipSpeed? a, ClipSpeed? b) =>
        a is not null && b is not null ? a.SameTiming(b) : (a ?? b) is not { } one || one.IsIdentity;

    private static bool InRange(ParameterDefinition definition, double value) =>
        double.IsFinite(value) && value >= definition.Minimum && value <= definition.Maximum;

    private static EffectTarget TargetOf(TrackKind kind) => kind == TrackKind.Video ? EffectTarget.Video : EffectTarget.Audio;

    // En 128 bits por la misma razón que arriba: tiempos extremos de un archivo no deben lanzar.
    private static bool Touches(Clip left, Clip right) =>
        left.Start.Ticks + TimelineDuration(left) == right.Start.Ticks;

    private static Int128 TimelineDuration(Clip clip) =>
        clip.Speed is null ? (Int128)clip.SourceOut.Ticks - clip.SourceIn.Ticks : clip.Duration.Ticks;

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
