using System.Globalization;
using System.Text;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Tests.Properties;
using Rodaje.Core.Timing;
using Xunit.Sdk;

namespace Rodaje.Core.Tests.Commands;

/// <summary>
/// Deshacer y rehacer exactos, comparando el proyecto por su archivo (<see cref="ProjectSerializer.Serialize"/>) en cada
/// paso: secuencias aleatorias de ediciones de línea de tiempo (las de <see cref="RandomTimeline"/>: cortar, recortar,
/// ripple, mover entre pistas, agrupar, efectos, velocidades…) mezcladas con keyframes, marcadores, regiones, mezclador,
/// compuestos, comandos de proyecto, grupos de gesto (cerrados y cancelados), fusiones, puntos limpios, comandos que
/// fallan a medias y un límite de pila pequeño. Semillas fijas: un fallo se reproduce con su número.
/// </summary>
public sealed class UndoExactnessPropertiesTests
{
    private const string SequenceId = BuiltProject.SequenceId;

    [Fact]
    public void Secuencias_aleatorias_deshacen_y_rehacen_exactamente()
    {
        for (int seed = 1; seed <= 200; seed++)
        {
            Run(seed, steps: 60, limit: 500);
        }
    }

    [Fact]
    public void Con_limite_pequeno_la_pila_descarta_lo_antiguo_y_sigue_exacta()
    {
        for (int seed = 1001; seed <= 1100; seed++)
        {
            Run(seed, steps: 60, limit: 1 + (seed % 6));
        }
    }

    /// <summary>
    /// Semillas que destaparon defectos. 111 y 1019: quitar una secuencia que un clip compuesto muestra dejaba el proyecto
    /// inválido (Load ya no lo abría); ahora <see cref="RemoveSequenceCommand"/> lo rechaza sin cambiar nada.
    /// </summary>
    [Theory]
    [InlineData(111, 500)]
    [InlineData(1019, 6)]
    public void Semillas_de_defectos(int seed, int limit) => Run(seed, steps: 60, limit);

    private static void Run(int seed, int steps, int limit)
    {
        var random = new Random(seed);
        BuiltProject built = RandomTimeline.Build(RandomSpec(random));
        Project project = built.Project;
        var stack = new UndoStack(project, limit);
        var log = new StringBuilder();
        string initial = ProjectSerializer.Serialize(project);
        int initialProblems = ModelValidator.Validate(project).Count;

        // states[i]: el archivo esperado con i pasos de deshacer; los de más allá de Count son los de rehacer.
        var states = new List<string> { initial };
        bool trimmed = false;
        string? clean = initial;
        string? groupStart = null;
        int groupCommands = 0;
        ICommand? lastBeforeGroup = null;

        // Un arrastre en curso: el siguiente paso del mismo gesto, que se funde con el anterior.
        Func<Random, (string Text, ICommand Command)>? drag = null;

        // Solo se puede fundir con el paso anterior si lo último fue ejecutar un comando fuera de un grupo: deshacer,
        // rehacer, guardar, el fin de un gesto o un grupo cortan la fusión.
        bool mergeable = false;

        void Fail(string message) =>
            throw new XunitException($"[semilla {seed}, límite {limit}] {message}\nPasos:\n{log}");

        void CheckClean()
        {
            if (stack.IsClean && Serialize(project) != clean)
            {
                Fail("La pila dice que el proyecto está como lo guardado, pero no lo está.");
            }
        }

        for (int step = 0; step < steps; step++)
        {
            int roll = random.Next(100);
            string current = Serialize(project);

            if (roll < 8)
            {
                if (stack.IsInGroup)
                {
                    continue;
                }

                bool undo = random.Next(2) == 0;
                bool done = undo ? stack.Undo() : stack.Redo();
                log.Append(CultureInfo.InvariantCulture, $"  {(undo ? "deshacer" : "rehacer")} → {(done ? "hecho" : "nada")}\n");
                mergeable &= !done;
                if (done && Serialize(project) != states[stack.Count])
                {
                    Fail($"Tras {(undo ? "deshacer" : "rehacer")} el proyecto no es el del paso {stack.Count}.");
                }

                if (!done && Serialize(project) != current)
                {
                    Fail("Deshacer o rehacer sin pasos cambió el proyecto.");
                }

                CheckClean();
                continue;
            }

            if (roll < 11)
            {
                if (!stack.IsInGroup)
                {
                    stack.MarkClean();
                    clean = current;
                    mergeable = false;
                    log.Append("  guardar\n");
                }

                continue;
            }

            if (roll < 16)
            {
                stack.BreakMerge();
                mergeable = false;
                log.Append("  fin de gesto\n");
                continue;
            }

            if (roll < 20)
            {
                mergeable = false;
                if (!stack.IsInGroup)
                {
                    groupStart = current;
                    groupCommands = 0;
                    lastBeforeGroup = stack.UndoCommands.Count > 0 ? stack.UndoCommands[^1] : null;
                    stack.BeginGroup("Gesto");
                    log.Append("  abrir grupo\n");
                }
                else if (random.Next(3) == 0)
                {
                    stack.CancelGroup();
                    log.Append("  cancelar grupo\n");
                    if (Serialize(project) != groupStart)
                    {
                        Fail("Cancelar el grupo no devolvió el proyecto al inicio del gesto.");
                    }

                    groupStart = null;
                }
                else
                {
                    int before = stack.Count;
                    stack.EndGroup();
                    log.Append("  cerrar grupo\n");
                    Record(before, lastBeforeGroup, groupCommands > 0, canMerge: false);
                    groupStart = null;
                }

                CheckClean();
                continue;
            }

            // El arrastre sigue vivo entre otros comandos: así también se prueba que no se funda con un paso que no es el anterior.
            Func<Random, (string Text, ICommand Command)>? nextDrag = null;
            (string text, ICommand command) = roll < 45 && drag is not null ? drag(random) : NextCommand(random, built, out nextDrag);
            drag = nextDrag ?? drag;
            log.Append("  ").Append(text);
            int countBefore = stack.Count;
            int redoBefore = stack.RedoCount;
            bool cleanBefore = stack.IsClean;
            string? undoBefore = stack.UndoDescription;
            ICommand? lastBefore = stack.UndoCommands.Count > 0 ? stack.UndoCommands[^1] : null;
            try
            {
                stack.Execute(command);
            }
            catch (EditException ex)
            {
                log.Append(" → rechazada: ").Append(ex.Message).Append('\n');
                if (Serialize(project) != current)
                {
                    Fail($"«{text}» se rechazó pero cambió el proyecto.");
                }

                if (stack.Count != countBefore || stack.RedoCount != redoBefore || stack.IsClean != cleanBefore
                    || stack.UndoDescription != undoBefore)
                {
                    Fail($"«{text}» se rechazó pero cambió la pila.");
                }

                continue;
            }
            catch (InjectedFailure)
            {
                log.Append(" → falló a medias\n");
                if (Serialize(project) != current)
                {
                    Fail($"«{text}» falló a medias y dejó el proyecto cambiado.");
                }

                if (stack.Count != countBefore || stack.RedoCount != redoBefore || stack.IsClean != cleanBefore)
                {
                    Fail($"«{text}» falló a medias y cambió la pila.");
                }

                continue;
            }

            log.Append(" → ok\n");
            if (ModelValidator.Validate(project).Count > initialProblems)
            {
                Fail($"«{text}» dejó el proyecto inválido: {string.Join(" | ", ModelValidator.Validate(project))}");
            }

            if (stack.IsInGroup)
            {
                groupCommands++;
            }
            else
            {
                Record(countBefore, lastBefore, changed: true, canMerge: mergeable);
                mergeable = true;
            }

            CheckClean();
        }

        if (stack.IsInGroup)
        {
            int before = stack.Count;
            stack.EndGroup();
            Record(before, lastBeforeGroup, groupCommands > 0, canMerge: false);
        }

        while (stack.Redo())
        {
        }

        string final = Serialize(project);
        if (final != states[^1])
        {
            Fail("Rehacer todo no lleva al último estado.");
        }

        while (stack.Undo())
        {
            if (Serialize(project) != states[stack.Count])
            {
                Fail($"Deshacer hasta el paso {stack.Count} no restaura su estado.");
            }
        }

        if (!trimmed && Serialize(project) != initial)
        {
            Fail("Deshacer todo no vuelve al proyecto inicial byte a byte.");
        }

        while (stack.Redo())
        {
            if (Serialize(project) != states[stack.Count])
            {
                Fail($"Rehacer hasta el paso {stack.Count} no restaura su estado.");
            }
        }

        if (Serialize(project) != final)
        {
            Fail("Rehacer todo después de deshacer todo no vuelve al estado final.");
        }

        // Tras ejecutar (no en grupo) o cerrar un grupo: un paso nuevo, una fusión con el último o un paso nuevo que
        // empuja fuera el más antiguo por el límite.
        void Record(int countBefore, ICommand? lastBefore, bool changed, bool canMerge)
        {
            string now = Serialize(project);
            if (!changed)
            {
                if (stack.Count != countBefore)
                {
                    Fail("Un grupo vacío cambió la pila.");
                }

                return;
            }

            states.RemoveRange(countBefore + 1, states.Count - countBefore - 1);

            if (stack.Count == countBefore + 1)
            {
                states.Add(now);
            }
            else if (stack.Count == countBefore && ReferenceEquals(stack.UndoCommands[^1], lastBefore))
            {
                if (!canMerge)
                {
                    Fail("El paso se fundió con uno que no era el inmediatamente anterior.");
                }

                states[^1] = now;
            }
            else if (stack.Count == countBefore && countBefore == stack.Limit)
            {
                states.RemoveAt(0);
                states.Add(now);
                trimmed = true;
            }
            else
            {
                Fail($"La pila pasó de {countBefore} a {stack.Count} pasos de una forma imposible.");
            }

            if (states.Count != stack.Count + 1)
            {
                Fail($"Los estados quedaron desalineados con la pila ({states.Count} estados, {stack.Count} pasos).");
            }
        }
    }

    private static string Serialize(Project project) => ProjectSerializer.Serialize(project);

    private static ProjectSpec RandomSpec(Random random)
    {
        MediaSpec[] media = [.. Enumerable.Range(0, random.Next(2, 6)).Select(_ =>
        {
            int k = random.Next(10);
            return new MediaSpec(k < 6 ? 0 : k < 8 ? 1 : 2, random.Next(1, 601), random.Next(4));
        })];
        PlacementSpec[] placements = [.. Enumerable.Range(0, random.Next(1, 9)).Select(_ => new PlacementSpec(
            random.Next(100), random.Next(301), random.Next(1, 151), random.Next(100), random.Next(4), random.Next(601), random.Next(2) == 0))];
        int titles = random.Next(65536);
        return new ProjectSpec(
            random.Next(RandomTimeline.Rates.Length),
            random.Next(1, 4),
            random.Next(1, 4),
            media,
            placements,
            random.Next(4096),
            random.Next(65536),
            random.Next(65536),
            Titles: titles % 4 == 0 ? titles : 0);
    }

    private static (string Text, ICommand Command) NextCommand(Random random, BuiltProject built, out Func<Random, (string Text, ICommand Command)>? drag)
    {
        drag = null;
        IIdGenerator ids = built.Ids;
        Sequence sequence = built.Sequence;
        List<Clip> clips = [.. sequence.AllTracks.SelectMany(t => t.Clips)];
        Clip? clip = clips.Count == 0 ? null : clips[random.Next(clips.Count)];
        List<Clip> withEffects = clips.FindAll(c => c.Effects.Count > 0);
        Clip? fxClip = withEffects.Count == 0 ? null : withEffects[random.Next(withEffects.Count)];
        Time at = Time.FromFrames(random.Next(0, 400), built.Rate);
        int kind = random.Next(22);

        switch (kind)
        {
            case < 9:
                return TimelineOp();

            case 9 when fxClip is not null:
            {
                clip = fxClip;
                // Arrastre de un deslizador: varios cambios del mismo parámetro en el mismo instante se funden.
                EffectInstance effect = clip.Effects[random.Next(clip.Effects.Count)];
                string? name = effect.Parameters.Keys.Order(StringComparer.Ordinal).ElementAtOrDefault(random.Next(Math.Max(1, effect.Parameters.Count)));
                name ??= BuiltInEffects.TransformParameters.Opacity;
                Time source = clip.SourceIn + Time.FromTicks(random.NextInt64(Math.Max(1, clip.SourceDuration.Ticks)));
                string clipId = clip.Id;
                drag = r =>
                {
                    double value = Math.Round(r.NextDouble(), 3);
                    return ($"parámetro {clipId}/{effect.Id}/{name}={value} en {source.Ticks}",
                        Edits.SetParameter(SequenceId, clipId, effect.Id, name, value, source, ids: ids));
                };
                return drag(random);
            }

            case 10 when fxClip is not null:
            {
                clip = fxClip;
                EffectInstance effect = clip.Effects[random.Next(clip.Effects.Count)];
                (string name, Parameter? parameter) = effect.Parameters.Count == 0
                    ? (BuiltInEffects.TransformParameters.X, null)
                    : effect.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, (Parameter?)p.Value)).ElementAt(random.Next(effect.Parameters.Count));
                Keyframe? existing = parameter is { Keyframes.Count: > 0 } ? parameter.Keyframes[random.Next(parameter.Keyframes.Count)] : null;
                Time source = clip.SourceIn + Time.FromTicks(random.NextInt64(Math.Max(1, clip.SourceDuration.Ticks)));
                return (random.Next(6), existing) switch
                {
                    (0, _) or (_, null) => ($"keyframe {clip.Id}/{effect.Id}/{name} en {source.Ticks}",
                        Edits.SetKeyframe(SequenceId, clip.Id, effect.Id, name, source, Math.Round(random.NextDouble(), 3), (Interpolation)random.Next(4), ids)),
                    (1, { } k) => ($"mover keyframe {clip.Id}/{name} {k.Time.Ticks}→{source.Ticks}",
                        Edits.MoveKeyframe(SequenceId, clip.Id, effect.Id, name, k.Time, source, $"kf:{clip.Id}:{name}", ids)),
                    (2, { } k) => ($"quitar keyframe {clip.Id}/{name} {k.Time.Ticks}",
                        Edits.RemoveKeyframe(SequenceId, clip.Id, effect.Id, name, k.Time, ids)),
                    (3, { } k) => ($"interpolación {clip.Id}/{name} {k.Time.Ticks}",
                        Edits.SetKeyframeInterpolation(SequenceId, clip.Id, effect.Id, name, k.Time, (Interpolation)random.Next(4), ids)),
                    (_, { } k) => ($"curva {clip.Id}/{name} {k.Time.Ticks}",
                        Edits.SetKeyframeEase(SequenceId, clip.Id, effect.Id, name, k.Time,
                            random.Next(3) == 0 ? null : new BezierEase(0.25, Math.Round(random.NextDouble(), 2), 0.75, 1), ids)),
                };
            }

            case 11:
            {
                if (sequence.Markers.Count == 0 || random.Next(3) == 0)
                {
                    return ($"marcador en {at.Ticks}", Edits.AddMarker(SequenceId, at, "M" + random.Next(10), ids));
                }

                Marker marker = sequence.Markers[random.Next(sequence.Markers.Count)];
                if (random.Next(3) == 0)
                {
                    drag = r =>
                    {
                        Time to = Time.FromFrames(r.Next(0, 400), built.Rate);
                        return ($"mover marcador {marker.Id} a {to.Ticks}", Edits.MoveMarker(SequenceId, marker.Id, to, ids));
                    };
                    return drag(random);
                }

                return random.Next(2) switch
                {
                    1 => ($"editar marcador {marker.Id}", Edits.EditMarker(SequenceId, marker.Id, "N" + random.Next(10),
                        random.Next(2) == 0 ? null : "#12AB34", random.Next(2) == 0 ? null : "nota " + random.Next(10), ids)),
                    _ => ($"quitar marcador {marker.Id}", Edits.RemoveMarker(SequenceId, marker.Id, ids)),
                };
            }

            case 12:
            {
                Time end = at + Time.FromFrames(random.Next(-5, 60), built.Rate);
                if (sequence.Regions.Count == 0 || random.Next(3) == 0)
                {
                    return ($"región {at.Ticks}-{end.Ticks}", Edits.AddRegion(SequenceId, at, end, "R", ids));
                }

                Region region = sequence.Regions[random.Next(sequence.Regions.Count)];
                return random.Next(2) == 0
                    ? ($"editar región {region.Id}", Edits.EditRegion(SequenceId, region.Id, "R" + random.Next(10),
                        random.Next(2) == 0 ? null : "#ABCDEF", random.Next(2) == 0 ? null : "nota", at, end, ids))
                    : ($"quitar región {region.Id}", Edits.RemoveRegion(SequenceId, region.Id, ids));
            }

            case 13:
            {
                List<Track> audio = sequence.AudioTracks;
                if (sequence.AudioBuses.Count == 0 || random.Next(4) == 0)
                {
                    return ("añadir bus", Edits.AddBus(SequenceId, "Bus " + random.Next(10), ids: ids));
                }

                AudioBus bus = sequence.AudioBuses[random.Next(sequence.AudioBuses.Count)];
                Track? track = audio.Count == 0 ? null : audio[random.Next(audio.Count)];
                int mix = random.Next(6);
                if (mix == 0 && track is not null)
                {
                    drag = r => ($"envío {track.Id}→{bus.Id}", Edits.SetSend(SequenceId, track.Id, bus.Id, gainDb: Math.Round(r.NextDouble() * -20, 1), ids: ids));
                    return drag(random);
                }

                if (mix == 2 && track is not null)
                {
                    drag = r => ($"fader {track.Id}", Edits.SetFader(SequenceId, track.Id, BuiltInEffects.AudioParameters.Pan, r.Next(-100, 101), Time.Zero, ids));
                    return drag(random);
                }

                return (mix, track) switch
                {
                    (1, { } t) => ($"salida {t.Id}→{bus.Id}", Edits.SetTrackOutput(SequenceId, t.Id, random.Next(3) == 0 ? null : bus.Id, ids)),
                    (3, _) => ($"estado bus {bus.Id}", Edits.SetBusState(SequenceId, bus.Id, muted: random.Next(2) == 0, ids: ids)),
                    (4, _) => ($"quitar bus {bus.Id}", Edits.RemoveBus(SequenceId, bus.Id, ids)),
                    _ => ("ganancia maestra", Edits.SetMasterGain(SequenceId, Math.Round(random.NextDouble() * 20 - 10, 2), "Normalizar", ids)),
                };
            }

            case 14 when clip is not null:
            {
                Clip? compound = clips.Find(c => c.IsCompound);
                if (compound is not null && random.Next(2) == 0)
                {
                    return ($"descomponer {compound.Id}", Edits.DecomposeCompound(SequenceId, compound.Id, ids));
                }

                string[] chosen = [.. clips.Where(_ => random.Next(3) == 0).Select(c => c.Id).Append(clip.Id).Distinct()];
                return ($"compuesto [{string.Join(",", chosen)}]", Edits.CreateCompound(SequenceId, chosen, "Compuesto", ids));
            }

            case 15:
                return random.Next(5) switch
                {
                    3 when built.Project.Sequences.Count > 1 => ($"quitar secuencia {built.Project.Sequences[^1].Id}",
                        new RemoveSequenceCommand(built.Project.Sequences[^1].Id)),
                    4 => ("nueva secuencia", new AddSequenceCommand(new Sequence
                    {
                        Id = ids.NewId("sequence"),
                        Name = "Otra",
                        VideoTracks = [new Track { Id = ids.NewId("track"), Kind = TrackKind.Video, Name = "V" }],
                    })),
                    0 => ("renombrar proyecto", new RenameProjectCommand("Proyecto " + random.Next(10))),
                    1 => ("importar medio", new AddMediaCommand(new MediaRef
                    {
                        Id = ids.NewId("media"),
                        Name = "extra.wav",
                        Path = "extra-" + random.Next(1000) + ".wav",
                        Duration = Time.FromSeconds(3),
                        HasAudio = true,
                        SampleRate = 48000,
                        AudioChannels = 2,
                    })),
                    2 => ("renombrar proyecto vacío", new RenameProjectCommand(" ")),
                    _ => ("renombrar proyecto", new RenameProjectCommand("Proyecto")),
                };

            case 16 when clip is not null:
            {
                // Falla a medias: el primer paso cambia el proyecto y el segundo lanza algo que no es EditException.
                SequenceEditCommand first = Edits.SetClipEnabled(SequenceId, clip.Id, !clip.Enabled, ids);
                return random.Next(2) == 0
                    ? ($"compuesto que falla tras desactivar {clip.Id}", new CompositeCommand("Falla", [first, new FailingCommand()]))
                    : ($"edición que falla tras mover {clip.Id}", new SequenceEditCommand("Falla", SequenceId, ctx =>
                    {
                        Clip target = ctx.Sequence.GetClip(clip.Id).Clip;
                        target.Start += Time.FromSeconds(1);
                        target.Color = "#FF0000";
                        ctx.Sequence.Markers.Add(new Marker { Id = "intruso", Time = Time.Zero });
                        ctx.Sequence.MasterGainDb = 3;
                        throw new InjectedFailure();
                    }, ids));
            }

            case 18 when sequence.AllTracks.Any(t => t.Transitions.Count > 0):
            {
                TransitionInstance transition = sequence.AllTracks.SelectMany(t => t.Transitions).ElementAt(
                    random.Next(sequence.AllTracks.Sum(t => t.Transitions.Count)));
                return random.Next(2) == 0
                    ? ($"alinear transición {transition.Id}", Edits.SetTransitionAlignment(SequenceId, transition.Id, (TransitionAlignment)random.Next(3), ids))
                    : ($"curva de transición {transition.Id}", Edits.SetTransitionCurve(SequenceId, transition.Id,
                        random.Next(3) == 0 ? null : (Interpolation)random.Next(4), random.Next(2) == 0 ? null : new BezierEase(0.3, 0, 0.7, 1), ids));
            }

            case 19 when fxClip is not null:
            {
                EffectInstance effect = fxClip.Effects[random.Next(fxClip.Effects.Count)];
                return random.Next(3) switch
                {
                    0 => ($"mover efecto {fxClip.Id}/{effect.Id}", Edits.MoveEffect(SequenceId, fxClip.Id, effect.Id, random.Next(fxClip.Effects.Count + 1), ids)),
                    1 => ($"activar efecto {fxClip.Id}/{effect.Id}", Edits.SetEffectEnabled(SequenceId, fxClip.Id, effect.Id, random.Next(2) == 0, ids)),
                    _ => ($"quitar efecto {fxClip.Id}/{effect.Id}", Edits.RemoveEffect(SequenceId, fxClip.Id, effect.Id, ids)),
                };
            }

            case 20 when clip is not null:
            {
                Clip? title = clips.Find(c => c.Generator is not null);
                int what = random.Next(3);
                if (what == 0 && title is not null)
                {
                    drag = r => ($"texto {title.Id}", Edits.SetTitleText(SequenceId, title.Id, "Hola " + r.Next(5), ids));
                    return drag(random);
                }

                return what switch
                {
                    1 => ($"cortar todo en {at.Ticks}", Edits.SplitAllTracks(SequenceId, at, ids)),
                    _ => ($"separar audio {clip.Id}", Edits.DetachAudio(SequenceId, clip.Id, ids: ids)),
                };
            }

            case 21 when clip is not null:
            {
                // Arrastres de la línea de tiempo: recortar un borde o empujar el clip, paso a paso.
                string clipId = clip.Id;
                TrimEdge edge = (TrimEdge)random.Next(2);
                bool nudge = random.Next(2) == 0;
                drag = r =>
                {
                    Time delta = Time.FromFrames(r.Next(-6, 7), built.Rate);
                    return nudge
                        ? ($"empujar {clipId} {delta.Ticks}", Edits.MoveClips(SequenceId, [clipId], delta, ids))
                        : ($"recortar {clipId} {edge} {delta.Ticks}", Edits.Trim(SequenceId, clipId, edge, delta, ids));
                };
                return drag(random);
            }

            case 17 when clip is not null:
                return ($"color {clip.Id}", Edits.SetClipColor(SequenceId, [clip.Id], random.Next(3) == 0 ? null : "#00FF00", ids));

            default:
                return TimelineOp();
        }

        (string Text, ICommand Command) TimelineOp()
        {
            ResolvedOp op;
            do
            {
                op = RandomTimeline.Resolve(built, new OpSpec(
                    random.Next(RandomTimeline.KindCount), random.Next(1001), random.Next(1001), random.Next(1001), random.Next(1001), random.Next(-40, 41)));
            }
            while (op.Command is null);

            return (op.Text, op.Command);
        }
    }

    private sealed class InjectedFailure : Exception
    {
        public InjectedFailure()
            : base("Fallo provocado por la prueba.")
        {
        }
    }

    private sealed class FailingCommand : ICommand
    {
        public string Description => "Falla";

        public void Apply(Project project) => throw new InjectedFailure();

        public void Revert(Project project)
        {
        }

        public bool TryMerge(ICommand following) => false;
    }
}
