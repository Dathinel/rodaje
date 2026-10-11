using System.Text;
using CsCheck;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Tests.Commands;
using Rodaje.Core.Timing;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Rodaje.Core.Tests.Properties;

/// <remarks>Para reproducir un fallo, pasar la semilla que reporta CsCheck en <c>seed</c> o en <c>CsCheck_Seed</c>.</remarks>
public class EditPropertiesTests(ITestOutputHelper output)
{
    private const string Seq = BuiltProject.SequenceId;

    private static readonly Gen<(ProjectSpec Project, OpSpec[] Ops)> GenScenario =
        Gen.Select(RandomTimeline.GenProject, RandomTimeline.GenOp.Array[1, 40]);

    /// <summary>
    /// Propiedades 1 a 4, 6 y 7 sobre proyectos con efectos y transiciones y hasta 40 operaciones con deshacer y rehacer
    /// intercalados; deshacer todo devuelve el archivo idéntico byte a byte.
    /// </summary>
    [Fact]
    public void OperacionesAleatorias_CumplenTodasLasReglas() =>
        GenScenario.Sample(
            s => RunScenario(s.Project, s.Ops),
            writeLine: output.WriteLine,
            iter: 10000,
            print: s => $"{s.Project}\nOperaciones: {string.Join(" ", s.Ops.Select(o => o.ToString()))}");

    /// <summary>Propiedad 5: un arrastre (muchos comandos con la misma clave) queda como un solo paso.</summary>
    [Fact]
    public void Arrastres_SeFundenEnUnPasoYDeshacerVuelveAlInicio() =>
        Gen.Select(RandomTimeline.GenProject, Gen.Int[0, 7], Gen.Int[0, 1000], Gen.Int[-20, 20].Array[2, 15], Gen.Bool)
            .Sample(
                s => RunDrag(s.Item1, s.Item2, s.Item3, s.Item4, s.Item5),
                writeLine: output.WriteLine,
                iter: 5000,
                print: s => $"{s.Item1}\nArrastre {s.Item2} del clip {s.Item3}: [{string.Join(", ", s.Item4)}] ripple-todas={s.Item5}");

    private static void RunScenario(ProjectSpec spec, OpSpec[] ops)
    {
        BuiltProject built = RandomTimeline.Build(spec);
        Project project = built.Project;
        var log = new StringBuilder();
        void Fail(string message) =>
            throw new XunitException($"{message}\nOperaciones ejecutadas:\n{log}\nEstado actual:\n{ProjectDump.Of(project)}");

        if (ModelValidator.Validate(project).Count > 0)
        {
            Fail("El proyecto inicial no es válido: " + string.Join(" | ", ModelValidator.Validate(project)));
        }

        var stack = new UndoStack(project);
        string initialFile = ProjectSerializer.Serialize(project);

        // dumps[i] es el estado esperado cuando la pila tiene i pasos de deshacer.
        var dumps = new List<string> { ProjectDump.Of(project) };

        foreach (OpSpec spec1 in ops)
        {
            ResolvedOp op = RandomTimeline.Resolve(built, spec1);
            log.Append("  ").Append(op.Text);

            if (op.Kind is OpKind.Undo or OpKind.Redo)
            {
                bool done = op.Kind == OpKind.Undo ? stack.Undo() : stack.Redo();
                log.Append(done ? " → hecho\n" : " → nada\n");
                if (done && ProjectDump.Of(project) != dumps[stack.Count])
                {
                    Fail($"Tras {op.Kind} el estado no coincide con el del paso {stack.Count}.");
                }

                continue;
            }

            string before = ProjectDump.Of(project);
            var trackBefore = built.Sequence.AllTracks.ToDictionary(t => t.Id, t => t.Clone(), StringComparer.Ordinal);
            List<GroupMember> group = GroupMember.Capture(built.Sequence, op.ClipId);

            try
            {
                stack.Execute(op.Command!);
            }
            catch (EditException ex)
            {
                log.Append(" → rechazada: ").Append(ex.Message).Append('\n');

                // Propiedad 1: un rechazo deja el proyecto byte a byte como estaba.
                if (ProjectDump.Of(project) != before)
                {
                    Fail($"«{op.Text}» se rechazó pero cambió el proyecto.\nAntes:\n{before}");
                }

                continue;
            }
            catch (Exception ex)
            {
                Fail($"«{op.Text}» lanzó {ex.GetType().Name} en vez de EditException: {ex}");
            }

            log.Append(" → ok\n");

            // Propiedad 2: el modelo sigue siendo válido.
            IReadOnlyList<string> errors = ModelValidator.Validate(project);
            if (errors.Count > 0)
            {
                Fail($"«{op.Text}» dejó el proyecto inválido: {string.Join(" | ", errors)}");
            }

            // Propiedad 3: las pistas bloqueadas antes de la operación conservan sus clips.
            foreach (Track locked in trackBefore.Values.Where(t => t.Locked))
            {
                Track? now = built.Sequence.FindTrack(locked.Id);
                if (now is null || !SameClips(locked.Clips, now.Clips))
                {
                    Fail($"«{op.Text}» cambió la pista bloqueada «{locked.Id}».");
                }
            }

            // Propiedad 6: conservación en slip, roll, slide y cortes.
            string? conservation = CheckConservation(op, group, trackBefore, built.Sequence);
            if (conservation is not null)
            {
                Fail($"«{op.Text}»: {conservation}");
            }

            // Propiedad 7: cada transición sigue en un corte entre clips contiguos, sin pasar del más corto.
            string? transitions = CheckTransitions(op, trackBefore, built.Sequence);
            if (transitions is not null)
            {
                Fail($"«{op.Text}»: {transitions}");
            }

            // Propiedad 4: el historial guarda el estado de cada paso.
            dumps.RemoveRange(stack.Count, dumps.Count - stack.Count);
            dumps.Add(ProjectDump.Of(project));
            if (dumps.Count != stack.Count + 1)
            {
                Fail($"La pista de estados quedó desalineada con la pila ({dumps.Count} estados, {stack.Count} pasos).");
            }
        }

        // Propiedad 4: deshacer todo vuelve al inicio y rehacer todo al final alcanzado.
        while (stack.Redo())
        {
        }

        string final = ProjectDump.Of(project);
        if (final != dumps[^1])
        {
            Fail("Rehacer todo no lleva al último estado.");
        }

        while (stack.Undo())
        {
            if (ProjectDump.Of(project) != dumps[stack.Count])
            {
                Fail($"Deshacer hasta el paso {stack.Count} no restaura su estado.");
            }
        }

        if (ProjectDump.Of(project) != dumps[0] || ProjectSerializer.Serialize(project) != initialFile)
        {
            Fail("Deshacer todo no vuelve al proyecto inicial byte a byte.");
        }

        while (stack.Redo())
        {
            if (ProjectDump.Of(project) != dumps[stack.Count])
            {
                Fail($"Rehacer hasta el paso {stack.Count} no restaura su estado.");
            }
        }

        if (ProjectDump.Of(project) != final)
        {
            Fail("Rehacer todo después de deshacer todo no vuelve al estado final.");
        }
    }

    private static void RunDrag(ProjectSpec spec, int dragKind, int clipIndex, int[] steps, bool allTracks)
    {
        BuiltProject built = RandomTimeline.Build(spec);
        Project project = built.Project;
        List<Clip> clips = [.. built.Sequence.AllTracks.SelectMany(t => t.Clips)];
        if (clips.Count == 0)
        {
            return;
        }

        string clipId = clips[clipIndex % clips.Count].Id;
        string initial = ProjectDump.Of(project);
        var stack = new UndoStack(project);
        RippleScope scope = allTracks ? RippleScope.AllTracks : RippleScope.Track;
        int succeeded = 0;
        string? key = null;
        var log = new StringBuilder();

        foreach (int step in steps)
        {
            Time delta = Time.FromFrames(step, built.Rate);
            SequenceEditCommand command = dragKind switch
            {
                0 => Edits.Move(Seq, clipId, MoveTarget(built, clipId, delta), ids: built.Ids),
                1 => Edits.Trim(Seq, clipId, TrimEdge.In, delta, built.Ids),
                2 => Edits.Trim(Seq, clipId, TrimEdge.Out, delta, built.Ids),
                3 => Edits.RippleTrim(Seq, clipId, TrimEdge.In, delta, scope, built.Ids),
                4 => Edits.RippleTrim(Seq, clipId, TrimEdge.Out, delta, scope, built.Ids),
                5 => Edits.Roll(Seq, clipId, delta, built.Ids),
                6 => Edits.Slip(Seq, clipId, delta, built.Ids),
                _ => Edits.Slide(Seq, clipId, delta, built.Ids),
            };
            key ??= command.MergeKey;
            Assert.Equal(key, command.MergeKey);

            string before = ProjectDump.Of(project);
            try
            {
                stack.Execute(command);
                succeeded++;
                log.Append(command.Description).Append(' ').Append(delta.Ticks).Append(" ok\n");
                Assert.Empty(ModelValidator.Validate(project));
            }
            catch (EditException ex)
            {
                log.Append(command.Description).Append(' ').Append(delta.Ticks).Append(" rechazado: ").Append(ex.Message).Append('\n');
                Assert.Equal(before, ProjectDump.Of(project));
            }
        }

        Assert.NotNull(key);
        if (stack.Count != (succeeded > 0 ? 1 : 0))
        {
            throw new XunitException($"El arrastre ({key}) dejó {stack.Count} pasos con {succeeded} comandos aplicados.\n{log}");
        }

        string final = ProjectDump.Of(project);
        stack.Undo();
        Assert.Equal(initial, ProjectDump.Of(project));
        stack.Redo();
        Assert.Equal(final, ProjectDump.Of(project));
    }

    /// <summary>Un arrastre de mover da posiciones absolutas: la actual del clip más el paso.</summary>
    private static Time MoveTarget(BuiltProject built, string clipId, Time delta)
    {
        Time target = built.Sequence.GetClip(clipId).Clip.Start + delta;
        return target < Time.Zero ? Time.Zero : target;
    }

    /// <summary>
    /// Toda transición une dos clips contiguos y no dura más que el más corto. Cortar, slip, roll y slide no quitan
    /// ninguna, porque no hacen desaparecer cortes.
    /// </summary>
    private static string? CheckTransitions(ResolvedOp op, Dictionary<string, Track> before, Sequence after)
    {
        foreach (Track track in after.AllTracks)
        {
            foreach (TransitionInstance transition in track.Transitions)
            {
                int from = track.Clips.FindIndex(c => c.Id == transition.From);
                if (from < 0 || from + 1 >= track.Clips.Count || track.Clips[from + 1].Id != transition.To
                    || track.Clips[from].End != track.Clips[from + 1].Start)
                {
                    return $"la transición «{transition.Id}» quedó sin sus clips contiguos.";
                }

                if (transition.Duration <= Time.Zero || transition.Duration > TransitionOps.MaxDuration(track.Clips[from], track.Clips[from + 1]))
                {
                    return $"la transición «{transition.Id}» dura {transition.Duration.Ticks} ticks, fuera de lo que permiten sus clips.";
                }
            }

            if (op.Kind is OpKind.Split or OpKind.Slip or OpKind.Roll or OpKind.Slide
                && before.TryGetValue(track.Id, out Track? old)
                && old.Transitions.Count != track.Transitions.Count)
            {
                return $"{op.Kind} cambió las transiciones de «{track.Id}» de {old.Transitions.Count} a {track.Transitions.Count}.";
            }
        }

        return null;
    }

    private static bool SameClips(List<Clip> a, List<Clip> b) =>
        a.Count == b.Count && a.Zip(b).All(p => SequenceSnapshot.ClipStateEquals(p.First, p.Second));

    private static long SumDurations(Track track) => track.Clips.Sum(c => c.Duration.Ticks);

    /// <summary>Revisa que slip, roll, slide y cortar conserven lo que deben; devuelve el problema o <c>null</c>.</summary>
    private static string? CheckConservation(ResolvedOp op, List<GroupMember> group, Dictionary<string, Track> before, Sequence after)
    {
        if (op.Kind is not (OpKind.Slip or OpKind.Roll or OpKind.Slide or OpKind.Split))
        {
            return null;
        }

        foreach ((string trackId, Track old) in before)
        {
            Track now = after.GetTrack(trackId);
            GroupMember? member = group.Find(m => m.TrackId == trackId);
            long expectedChange = 0;
            if (member is not null)
            {
                expectedChange = op.Kind switch
                {
                    OpKind.Roll => member.HasNext ? 0 : op.Delta.Ticks,
                    OpKind.Slide => (member.HasPrevious ? op.Delta.Ticks : 0) - (member.HasNext ? op.Delta.Ticks : 0),
                    _ => 0,
                };
            }
            else if (!SameClips(old.Clips, now.Clips))
            {
                return $"cambió la pista «{trackId}», que no tiene clips del grupo editado.";
            }

            long change = SumDurations(now) - SumDurations(old);
            if (change != expectedChange)
            {
                return $"la duración ocupada de «{trackId}» cambió {change} ticks y se esperaba {expectedChange}.";
            }

            if (member is null)
            {
                continue;
            }

            Clip clip = now.Clips.Find(c => c.Id == member.ClipId)!;
            switch (op.Kind)
            {
                case OpKind.Slip:
                    if (clip.Start != member.Start || clip.Duration != member.Duration)
                    {
                        return $"slip movió o cambió la duración de «{clip.Id}».";
                    }

                    if (!old.Clips.Zip(now.Clips).All(p => p.First.Start == p.Second.Start && p.First.End == p.Second.End))
                    {
                        return $"slip cambió la ocupación de la pista «{trackId}».";
                    }

                    break;
                case OpKind.Roll when member.HasNext:
                    if (clip.Start != member.Start || now.End != old.End || now.Clips.Count != old.Clips.Count)
                    {
                        return $"roll cambió el inicio de «{clip.Id}» o el final de la pista «{trackId}».";
                    }

                    break;
                case OpKind.Slide:
                    if (clip.SourceIn != member.SourceIn || clip.SourceOut != member.SourceOut || clip.Start != member.Start + op.Delta)
                    {
                        return $"slide cambió el contenido de «{clip.Id}» o no lo movió {op.Delta.Ticks} ticks.";
                    }

                    break;
                case OpKind.Split:
                    string? mapping = CheckSameContent(old, now, op.At);
                    if (mapping is not null)
                    {
                        return mapping;
                    }

                    break;
                default:
                    break;
            }
        }

        return null;
    }

    /// <summary>En cada instante de prueba, antes y después de cortar, se ve u oye el mismo instante del mismo medio.</summary>
    private static string? CheckSameContent(Track old, Track now, Time at)
    {
        var samples = new HashSet<long> { at.Ticks, at.Ticks - 1, at.Ticks + 1 };
        foreach (Clip c in old.Clips.Concat(now.Clips))
        {
            samples.Add(c.Start.Ticks);
            samples.Add(c.End.Ticks - 1);
            samples.Add(c.Start.Ticks + (c.Duration.Ticks / 2));
        }

        foreach (long ticks in samples.Where(t => t >= 0))
        {
            var t = Time.FromTicks(ticks);
            Clip? a = old.ClipAt(t);
            Clip? b = now.ClipAt(t);
            bool same = (a is null && b is null)
                || (a is not null && b is not null && a.MediaId == b.MediaId && a.ToSourceTime(t) == b.ToSourceTime(t) && a.Enabled == b.Enabled);
            if (!same)
            {
                return $"el corte cambió el contenido de la pista «{old.Id}» en {ticks} ticks.";
            }
        }

        return null;
    }

    /// <summary>Un miembro del grupo enlazado del clip editado, tal como estaba antes de la operación.</summary>
    private sealed record GroupMember(string TrackId, string ClipId, Time Start, Time Duration, Time SourceIn, Time SourceOut, bool HasPrevious, bool HasNext)
    {
        public static List<GroupMember> Capture(Sequence sequence, string? clipId)
        {
            if (clipId is null || sequence.FindClip(clipId) is null)
            {
                return [];
            }

            return sequence.GetLinkGroup(clipId).ConvertAll(m => new GroupMember(
                m.Track.Id,
                m.Clip.Id,
                m.Clip.Start,
                m.Clip.Duration,
                m.Clip.SourceIn,
                m.Clip.SourceOut,
                m.Track.Clips.Exists(c => c != m.Clip && c.End == m.Clip.Start),
                m.Track.Clips.Exists(c => c != m.Clip && c.Start == m.Clip.End)));
        }
    }
}
