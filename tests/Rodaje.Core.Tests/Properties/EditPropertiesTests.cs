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
    /// Propiedades 1 a 4 y 6 a 9 sobre proyectos con efectos, transiciones, velocidades, curvas, reversas y congelados y
    /// hasta 40 operaciones con deshacer y rehacer intercalados; deshacer todo devuelve el archivo idéntico byte a byte.
    /// </summary>
    [Fact]
    public void OperacionesAleatorias_CumplenTodasLasReglas() =>
        GenScenario.Sample(
            s => RunScenario(s.Project, s.Ops),
            writeLine: output.WriteLine,
            iter: 10000,
            print: s => $"{s.Project}\nOperaciones: {string.Join(" ", s.Ops.Select(o => o.ToString()))}");

    private static readonly Gen<(ProjectSpec Project, OpSpec[] Ops)> GenCurveScenario =
        Gen.Select(RandomTimeline.GenCurveProject, RandomTimeline.GenCurveOp.Array[1, 40]);

    /// <summary>
    /// Las mismas propiedades sobre 10 000 proyectos con curvas y preajustes en casi todos los clips, cortando, recortando
    /// y estirando: dividir y recortar no cambian el origen de ningún instante y deshacer todo vuelve byte a byte.
    /// </summary>
    [Fact]
    public void Curvas_CortarRecortarYEstirar_CumplenTodasLasReglas() =>
        GenCurveScenario.Sample(
            s => RunScenario(s.Project, s.Ops),
            writeLine: output.WriteLine,
            iter: 10000,
            print: s => $"{s.Project}\nOperaciones: {string.Join(" ", s.Ops.Select(o => o.ToString()))}");

    private static readonly Gen<(ProjectSpec Project, OpSpec[] Ops)> GenTitleScenario =
        Gen.Select(RandomTimeline.GenTitleProject, RandomTimeline.GenOp.Array[1, 40]);

    /// <summary>Las mismas propiedades sobre proyectos con títulos en casi todas las pistas de video (propiedad 10).</summary>
    [Fact]
    public void Titulos_CumplenTodasLasReglas() =>
        GenTitleScenario.Sample(
            s => RunScenario(s.Project, s.Ops),
            writeLine: output.WriteLine,
            iter: 5000,
            print: s => $"{s.Project}\nOperaciones: {string.Join(" ", s.Ops.Select(o => o.ToString()))}");

    /// <summary>El sorteo con títulos de verdad los corta, recorta y mueve, y rechaza en ellos el slip y la velocidad.</summary>
    [Fact]
    public void ElSorteoConTitulos_LosEdita()
    {
        var applied = new int[Enum.GetValues<OpKind>().Length];
        var rejected = new int[Enum.GetValues<OpKind>().Length];
        GenTitleScenario.Sample(
            s =>
            {
                BuiltProject built = RandomTimeline.Build(s.Project);
                foreach (OpSpec spec in s.Ops)
                {
                    ResolvedOp op = RandomTimeline.Resolve(built, spec);
                    bool title = op.ClipId is { } id && built.Sequence.FindClip(id)?.Clip.Generator is not null;
                    if (op.Command is null || !title)
                    {
                        continue;
                    }

                    try
                    {
                        op.Command.Apply(built.Project);
                        applied[(int)op.Kind]++;
                    }
                    catch (EditException)
                    {
                        rejected[(int)op.Kind]++;
                    }
                }
            },
            iter: 2000,
            threads: 1);

        output.WriteLine($"aplicadas/rechazadas sobre títulos: {string.Join(", ", Enum.GetValues<OpKind>().Select(k => $"{k} {applied[(int)k]}/{rejected[(int)k]}"))}");
        foreach (OpKind kind in (OpKind[])[OpKind.Split, OpKind.Trim, OpKind.RippleTrim, OpKind.Roll, OpKind.Slide, OpKind.Move])
        {
            Assert.True(applied[(int)kind] > 200, $"{kind} solo se aplicó {applied[(int)kind]} veces sobre un título");
        }

        foreach (OpKind kind in MaterialEdits)
        {
            Assert.Equal(0, applied[(int)kind]);
            Assert.True(rejected[(int)kind] > 20, $"{kind} solo se intentó {rejected[(int)kind]} veces sobre un título");
        }
    }

    /// <summary>El sorteo con curvas de verdad corta, recorta y estira clips con curva.</summary>
    [Fact]
    public void ElSorteoConCurvas_CortaRecortaYEstiraCurvas()
    {
        var applied = new int[Enum.GetValues<OpKind>().Length];
        int projectsWithCurves = 0;
        GenCurveScenario.Sample(
            s =>
            {
                BuiltProject built = RandomTimeline.Build(s.Project);
                if (built.Sequence.AllTracks.SelectMany(t => t.Clips).Any(c => c.Speed?.Curve is not null))
                {
                    projectsWithCurves++;
                }

                foreach (OpSpec spec in s.Ops)
                {
                    ResolvedOp op = RandomTimeline.Resolve(built, spec);
                    bool curved = op.ClipId is { } id && built.Sequence.FindClip(id)?.Clip.Speed?.Curve is not null;
                    if (op.Command is null || !curved)
                    {
                        continue;
                    }

                    try
                    {
                        op.Command.Apply(built.Project);
                        applied[(int)op.Kind]++;
                    }
                    catch (EditException)
                    {
                    }
                }
            },
            iter: 2000,
            threads: 1);

        output.WriteLine($"proyectos con curvas: {projectsWithCurves}; aplicadas sobre curvas: {string.Join(", ", Enum.GetValues<OpKind>().Select(k => $"{k} {applied[(int)k]}"))}");
        Assert.True(projectsWithCurves > 1500, $"solo {projectsWithCurves} proyectos con curvas");
        foreach (OpKind kind in (OpKind[])[OpKind.Split, OpKind.Trim, OpKind.RippleTrim, OpKind.RateStretch, OpKind.ApplySpeedPreset])
        {
            Assert.True(applied[(int)kind] > 300, $"{kind} solo se aplicó {applied[(int)kind]} veces sobre una curva");
        }
    }

    /// <summary>Los proyectos y las operaciones de velocidad del sorteo de verdad llegan a aplicarse, no solo a rechazarse.</summary>
    [Fact]
    public void ElSorteo_EjercitaLasVelocidades()
    {
        int projectsWithSpeed = 0;
        var applied = new int[Enum.GetValues<OpKind>().Length];
        GenScenario.Sample(
            s =>
            {
                BuiltProject built = RandomTimeline.Build(s.Project);
                if (built.Sequence.AllTracks.SelectMany(t => t.Clips).Any(c => c.Speed is not null))
                {
                    Interlocked.Increment(ref projectsWithSpeed);
                }

                foreach (OpSpec spec in s.Ops)
                {
                    ResolvedOp op = RandomTimeline.Resolve(built, spec);
                    if (op.Command is null)
                    {
                        continue;
                    }

                    try
                    {
                        op.Command.Apply(built.Project);
                        Interlocked.Increment(ref applied[(int)op.Kind]);
                    }
                    catch (EditException)
                    {
                    }
                }
            },
            iter: 2000,
            threads: 1);

        output.WriteLine($"proyectos con velocidad: {projectsWithSpeed}; aplicadas: {string.Join(", ", Enum.GetValues<OpKind>().Select(k => $"{k} {applied[(int)k]}"))}");
        Assert.True(projectsWithSpeed > 400, $"solo {projectsWithSpeed} proyectos con velocidad");
        foreach (OpKind kind in (OpKind[])[OpKind.SetSpeed, OpKind.RateStretch, OpKind.FreezeFrame, OpKind.SetCurve, OpKind.SetReverse, OpKind.SetPreservePitch, OpKind.ApplySpeedPreset])
        {
            // El sorteo no tiene semilla fija: FreezeFrame queda entre 100 y 135 de 2 000 (100 justos en una corrida de la CI
            // de #109). Lo que se comprueba es que cada operación se aplica de verdad, no una frecuencia: 40 deja margen.
            Assert.True(applied[(int)kind] > 40, $"{kind} solo se aplicó {applied[(int)kind]} veces");
        }
    }

    /// <summary>Propiedad 5: un arrastre (muchos comandos con la misma clave) queda como un solo paso.</summary>
    [Fact]
    public void Arrastres_SeFundenEnUnPasoYDeshacerVuelveAlInicio() =>
        Gen.Select(RandomTimeline.GenProject, Gen.Int[0, 9], Gen.Int[0, 1000], Gen.Int[-20, 20].Array[2, 15], Gen.Bool)
            .Sample(
                s => RunDrag(s.Item1, s.Item2, s.Item3, s.Item4, s.Item5),
                writeLine: output.WriteLine,
                iter: 5000,
                print: s => $"{s.Item1}\nArrastre {s.Item2} del clip {s.Item3}: [{string.Join(", ", s.Item4)}] ripple-todas={s.Item5}");

    private static readonly HashSet<OpKind> SyncKeepingOps =
    [
        OpKind.Split, OpKind.Move, OpKind.InsertMedia, OpKind.Insert, OpKind.Overwrite, OpKind.AppendMedia, OpKind.Lift,
        OpKind.MoveClips, OpKind.GroupClips, OpKind.UngroupClips, OpKind.SetClipColor, OpKind.SetTrackHeight, OpKind.CloseAllGaps,
        OpKind.Resync, OpKind.SourceEdit,
    ];

    /// <summary>
    /// Los desfases entre cada par de enlazados del mismo medio en pistas sin bloquear, no solo contra la referencia:
    /// cortar un enlace (un Insert que parte un clip y lleva lo de después con la mitad derecha) cambia cuál es la
    /// referencia de cada mitad, y un par que ya estaba desfasado pasa a medirse sin que nada se haya corrido.
    /// </summary>
    private static HashSet<Time> SyncOffsets(Sequence sequence) => [.. SyncPairs(sequence).Select(o => Abs(o.Offset))];

    /// <summary>
    /// El primer par desfasado después de la edición cuyo desfase no estaba antes. Se mide con los mismos pares que
    /// <see cref="SyncOffsets"/>: contra la referencia de <see cref="LinkSync.Find"/> un clip que se corre hasta un
    /// desfase que ya tenía otro par (B y C a +5 de A; C pasa a −5) quedaba oculto, aunque B y C pasan de 0 a 10.
    /// </summary>
    private static SyncOffset SyncBreaks(Sequence sequence, HashSet<Time> before) =>
        SyncPairs(sequence).FirstOrDefault(o => !before.Contains(Abs(o.Offset)));

    // Cada par desfasado una sola vez; el signo depende de cuál va primero en el orden de las pistas.
    private static IEnumerable<SyncOffset> SyncPairs(Sequence sequence)
    {
        foreach (IGrouping<(string, string), Clip> same in sequence.AllTracks.Where(t => !t.Locked)
            .SelectMany(t => t.Clips)
            .Where(c => c.LinkId is not null && c.Generator is null && !c.IsCompound)
            .GroupBy(c => (c.LinkId!, c.MediaId)))
        {
            Clip[] members = [.. same];
            for (int i = 0; i < members.Length; i++)
            {
                for (int j = i + 1; j < members.Length; j++)
                {
                    if (LinkSync.OffsetBetween(members[i], members[j]) is { } offset && offset != Time.Zero)
                    {
                        yield return new SyncOffset(members[j].Id, members[i].Id, offset);
                    }
                }
            }
        }
    }

    private static Time Abs(Time t) => t < Time.Zero ? -t : t;

    /// <summary>
    /// La propiedad 11 sobre todas las ediciones (salvo enlazar a mano y las de velocidad, que recalculan cada clip desde
    /// su inicio): 10 000 escenarios al azar.
    /// </summary>
    [Fact]
    public void TodasLasEdiciones_ConservanLaSincronia() =>
        GenScenario.Sample(s => RunScenario(s.Project, s.Ops, allOpsKeepSync: true), writeLine: output.WriteLine, iter: 10000);

    /// <summary>
    /// Las semillas con las que la propiedad 11 encontró desfases: el recorte ripple con enlazados de distinto largo (pista
    /// y todas las pistas) y recortar la entrada de un clip con curva.
    /// </summary>
    [Theory]
    [InlineData("2hcHFAoqg5U8", 2)]
    [InlineData("8fBHh0xmkjni", 0)]
    [InlineData("2_rZhAfEy9if", 1)]
    [InlineData("7Ph8XxheITw1", 0)]
    public void LasSemillasDeDesfase_ConservanLaSincronia(string seed, int generator) =>
        (generator switch { 1 => GenCurveScenario, 2 => GenTitleScenario, _ => GenScenario })
            .Sample(s => RunScenario(s.Project, s.Ops, allOpsKeepSync: true), seed: seed, iter: 1, threads: 1);

    /// <summary>
    /// Regresión (CI de e86c6b09): un recorte ripple y quitar la reversa dejaron dos enlazados con velocidad 1× y distinto
    /// Offset; <see cref="LinkSync.OffsetBetween"/> los comparaba por contenido y no medía su desfase, y al dividirlos las dos
    /// mitades quedaban con el mismo Offset y el desfase de siempre aparecía como nuevo.
    /// </summary>
    [Fact]
    public void LaSemillaDeVelocidadNeutra_ConservaLaSincronia() =>
        GenScenario.Sample(s => RunScenario(s.Project, s.Ops), seed: "4mihSLRcQtT6", iter: 1, threads: 1);

    /// <summary>Una velocidad a 1× sin curva, reversa ni congelado se mide como un clip sin velocidad, sea cual sea su Offset.</summary>
    [Fact]
    public void LaVelocidadNeutra_SeMideComoSinVelocidad()
    {
        var video = new Clip
        {
            Id = "v", MediaId = "m", SourceIn = Time.FromTicks(6_556_200_000), SourceOut = Time.FromTicks(8_212_362_965),
            Start = Time.FromTicks(9_016_037_035), Speed = new ClipSpeed { Offset = Time.FromTicks(49_037_035) },
        };
        var audio = new Clip
        {
            Id = "a", MediaId = "m", SourceIn = Time.FromTicks(6_556_200_000), SourceOut = Time.FromTicks(9_172_800_000),
            Start = Time.FromTicks(8_055_600_000), Speed = new ClipSpeed { Offset = Time.FromTicks(-911_400_000) },
        };
        var plain = new Clip { Id = "p", MediaId = "m", SourceIn = audio.SourceIn, SourceOut = audio.SourceOut, Start = audio.Start };

        Assert.Equal(Time.FromTicks(960_437_035), LinkSync.OffsetBetween(video, audio));
        Assert.Equal(Time.FromTicks(960_437_035), LinkSync.OffsetBetween(video, plain));
    }

    internal static void RunScenario(ProjectSpec spec, OpSpec[] ops, bool allOpsKeepSync = false)
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
            var syncBefore = SyncOffsets(built.Sequence);

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

            // Propiedad 9: dividir y recortar no cambian el cuadro de origen de ningún instante que siga en el mismo clip.
            string? frames = CheckSameFrames(op, group, trackBefore, built.Sequence);
            if (frames is not null)
            {
                Fail($"«{op.Text}»: {frames}");
            }

            // Propiedad 8: la curva de los efectos de pista sigue a los clips que el ripple corre.
            string? curves = CheckTrackCurves(op, group, trackBefore, built.Sequence);
            if (curves is not null)
            {
                Fail($"«{op.Text}»: {curves}");
            }

            // Propiedad 7: cada transición sigue en un corte entre clips contiguos, sin pasar del más corto.
            string? transitions = CheckTransitions(op, trackBefore, built.Sequence);
            if (transitions is not null)
            {
                Fail($"«{op.Text}»: {transitions}");
            }

            // Propiedad 10: los títulos conservan su contenido y solo pierden bordes cuando algo los parte.
            string? titles = CheckGenerators(op, group, trackBefore, built.Sequence);
            if (titles is not null)
            {
                Fail($"«{op.Text}»: {titles}");
            }

            // Propiedad 11: estas ediciones nunca desincronizan clips enlazados del mismo medio en pistas sin bloquear (una
            // pista bloqueada no se corre aunque tenga enlazados: para eso está el aviso de desfase). Las demás se miden
            // aparte (TodasLasEdiciones_ConservanLaSincronia, con los defectos que encontró).
            // Desbloquear una pista (SetTrackState) no mueve nada: solo deja ver un desfase que su bloqueo ya había dejado.
            if ((allOpsKeepSync ? op.Kind is not (OpKind.LinkClips or OpKind.SetTrackState or OpKind.SetSpeed or OpKind.RateStretch or OpKind.FreezeFrame
                    or OpKind.SetCurve or OpKind.SetReverse or OpKind.ApplySpeedPreset) : SyncKeepingOps.Contains(op.Kind))
                && SyncBreaks(built.Sequence, syncBefore) is { ClipId: not null } broken)
            {
                Fail($"«{op.Text}» desincronizó «{broken.ClipId}» de «{broken.PartnerId}» en {broken.Offset.Ticks} ticks.\nAntes:\n{before}");
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
                7 => Edits.Slide(Seq, clipId, delta, built.Ids),
                8 => Edits.RateStretch(Seq, clipId, TrimEdge.Out, delta, scope, built.Ids),
                _ => Edits.RateStretch(Seq, clipId, TrimEdge.In, delta, scope, built.Ids),
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
    /// Toda transición une dos clips contiguos y cabe en ellos con su alineación, sin pisar a sus vecinas. Cortar, slip, roll y slide no quitan
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

                if (transition.Duration <= Time.Zero || transition.Duration > TransitionOps.MaxDuration(track, transition))
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

    /// <summary>
    /// En cada clip que sigue en su pista con el mismo material, la curva de cada efecto de pista vale lo mismo en su
    /// inicio, su mitad y su último tick que antes en los mismos puntos del clip: el ripple corre los keyframes con él.
    /// </summary>
    /// <remarks>
    /// Se salta el recorte con ripple (un clip de otra pista puede quedar a caballo del punto que se corre), los
    /// keyframes puestos a mano y el clip que se mueve o desliza sin ripple, que no se lleva la curva de la pista.
    /// También el clip que cambia de duración: lo recortaron (por ejemplo, al sobrescribir encima) y sus puntos ya no
    /// son los mismos. Un cuadro congelado recortado conserva su origen, así que el origen no basta para notarlo.
    /// </remarks>
    private static string? CheckTrackCurves(ResolvedOp op, List<GroupMember> group, Dictionary<string, Track> before, Sequence after)
    {
        if (op.Kind is OpKind.RippleTrim or OpKind.SetKeyframe or OpKind.SetSpeed or OpKind.RateStretch or OpKind.SetCurve or OpKind.FreezeFrame
            or OpKind.ApplySpeedPreset)
        {
            return null;
        }

        HashSet<string> moved = op.Kind is OpKind.Move or OpKind.Slide ? [.. group.Select(m => m.ClipId)] : [];
        foreach (Track now in after.AllTracks)
        {
            if (!before.TryGetValue(now.Id, out Track? old))
            {
                continue;
            }

            foreach (EffectInstance effect in now.Effects)
            {
                if (old.Effects.Find(e => e.Id == effect.Id) is not { } oldEffect)
                {
                    continue;
                }

                foreach ((string name, Parameter parameter) in effect.Parameters)
                {
                    if (!oldEffect.Parameters.TryGetValue(name, out Parameter? oldParameter))
                    {
                        continue;
                    }

                    for (int i = 1; i < parameter.Keyframes.Count; i++)
                    {
                        if (parameter.Keyframes[i].Time <= parameter.Keyframes[i - 1].Time)
                        {
                            return $"los keyframes de «{name}» en la pista «{now.Id}» quedaron fuera de orden.";
                        }
                    }

                    foreach (Clip clip in now.Clips)
                    {
                        Clip? was = old.Clips.Find(c => c.Id == clip.Id);
                        if (was is null || moved.Contains(clip.Id) || (op.Kind is OpKind.MoveClips or OpKind.Resync && was.Start != clip.Start)
                            || was.SourceIn != clip.SourceIn || was.SourceOut != clip.SourceOut
                            || was.Duration != clip.Duration)
                        {
                            continue;
                        }

                        foreach (long offset in new[] { 0, clip.Duration.Ticks / 2, clip.Duration.Ticks - 1 })
                        {
                            double expected = oldParameter.Evaluate(was.Start + Time.FromTicks(offset));
                            double actual = parameter.Evaluate(clip.Start + Time.FromTicks(offset));
                            if (Math.Abs(expected - actual) > 1e-6)
                            {
                                return $"«{name}» de la pista «{now.Id}» vale {actual} en el clip «{clip.Id}» (+{offset}) y valía {expected}.";
                            }
                        }
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Tras dividir, recortar, hacer roll o slide, o recortar con ripple el final, cada instante que sigue cubierto por el
    /// mismo clip (o, al dividir, por una de sus mitades) muestra el mismo instante del origen. Se comparan solo los clips
    /// que no se mueven.
    /// </summary>
    private static string? CheckSameFrames(ResolvedOp op, List<GroupMember> group, Dictionary<string, Track> before, Sequence after)
    {
        HashSet<string> members = [.. group.Select(m => m.ClipId)];
        Func<string, bool>? compare = op.Kind switch
        {
            OpKind.Trim or OpKind.Roll or OpKind.Split => _ => true,
            OpKind.Slide => id => !members.Contains(id),
            OpKind.RippleTrim when op.Text.Contains("Out", StringComparison.Ordinal) => members.Contains,
            _ => null,
        };
        if (compare is null)
        {
            return null;
        }

        foreach ((string trackId, Track old) in before)
        {
            Track now = after.GetTrack(trackId);
            var samples = new HashSet<long>();
            foreach (Clip c in old.Clips.Concat(now.Clips))
            {
                for (int k = 0; k < 8; k++)
                {
                    samples.Add(c.Start.Ticks + (c.Duration.Ticks / 8 * k) + (k * 7));
                }

                samples.Add(c.End.Ticks - 1);
            }

            foreach (long ticks in samples)
            {
                var t = Time.FromTicks(ticks);
                if (old.ClipAt(t) is { } a && now.ClipAt(t) is { } b && (a.Id == b.Id || op.Kind == OpKind.Split) && compare(a.Id)
                    && a.ToSourceTime(t) != b.ToSourceTime(t))
                {
                    return $"el clip «{a.Id}» mostraba {a.ToSourceTime(t).Ticks} en {ticks} ticks y ahora {b.ToSourceTime(t).Ticks}.";
                }
            }
        }

        return null;
    }

    private static readonly OpKind[] MaterialEdits =
    [
        OpKind.Slip, OpKind.SetSpeed, OpKind.RateStretch, OpKind.FreezeFrame, OpKind.SetCurve, OpKind.SetReverse,
        OpKind.SetPreservePitch, OpKind.ApplySpeedPreset,
    ];

    /// <summary>
    /// Un grupo con un título no acepta slip ni velocidad; ningún título cambia de contenido ni enciende un borde, y solo
    /// lo que parte clips (cortar, insertar, sobrescribir o mover insertando) apaga alguno. Al cortar un título, cada mitad
    /// apaga su borde interno o guarda cuánto del título queda al otro lado. Todo título nuevo sale de uno que ya estaba.
    /// </summary>
    private static string? CheckGenerators(ResolvedOp op, List<GroupMember> group, Dictionary<string, Track> before, Sequence after)
    {
        if (group.Exists(m => m.Generator) && Array.IndexOf(MaterialEdits, op.Kind) >= 0)
        {
            return $"{op.Kind} se aplicó a un grupo con un título.";
        }

        Dictionary<string, ClipGenerator> old = before.Values.SelectMany(t => t.Clips).Where(c => c.Generator is not null)
            .ToDictionary(c => c.Id, c => c.Generator!, StringComparer.Ordinal);
        bool splits = op.Kind is OpKind.Split or OpKind.Insert or OpKind.Overwrite or OpKind.InsertMedia or OpKind.Move or OpKind.SourceEdit;
        foreach (Track track in after.AllTracks)
        {
            foreach (Clip clip in track.Clips)
            {
                if (clip.Generator is not { } g)
                {
                    if (old.ContainsKey(clip.Id))
                    {
                        return $"el título «{clip.Id}» perdió su generador.";
                    }

                    continue;
                }

                if (!old.TryGetValue(clip.Id, out ClipGenerator? was))
                {
                    if (!old.Values.Any(o => o.SameContent(g)))
                    {
                        return $"apareció el título «{clip.Id}», que no sale de ninguno de antes.";
                    }

                    continue;
                }

                if (!g.SameContent(was))
                {
                    return $"el título «{clip.Id}» cambió de contenido.";
                }

                if ((g.Edges.In && !was.Edges.In) || (g.Edges.Out && !was.Edges.Out))
                {
                    return $"el título «{clip.Id}» volvió a encender un borde.";
                }

                if (!splits && !g.ContentEquals(was))
                {
                    return $"{op.Kind} apagó un borde del título «{clip.Id}».";
                }
            }
        }

        if (op.Kind == OpKind.Split)
        {
            foreach (GroupMember member in group.Where(m => m.Generator && m.Start < op.At && op.At < m.Start + m.Duration))
            {
                Track track = after.GetTrack(member.TrackId);
                Clip left = track.Clips.Find(c => c.Id == member.ClipId)!;
                Clip? right = track.Clips.Find(c => c.Start == op.At);
                if (right?.Generator is not { } r || !old.TryGetValue(member.ClipId, out ClipGenerator? was))
                {
                    return $"cortar el título «{member.ClipId}» no dejó la mitad derecha.";
                }

                GeneratorEdges l = left.Generator!.Edges;
                if ((l.Out && l.After != was.Edges.After + right.Duration) || (r.Edges.In && r.Edges.Before != was.Edges.Before + left.Duration))
                {
                    return $"cortar el título «{member.ClipId}» dejó un borde interno encendido sin el resto del título.";
                }
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
                || (a is not null && b is not null && a.MediaId == b.MediaId && a.ToSourceTime(t) == b.ToSourceTime(t) && a.Enabled == b.Enabled
                    && (a.Generator is null ? b.Generator is null : b.Generator is not null && a.Generator.SameContent(b.Generator)));
            if (!same)
            {
                return $"el corte cambió el contenido de la pista «{old.Id}» en {ticks} ticks.";
            }
        }

        return null;
    }

    /// <summary>Un miembro del grupo enlazado del clip editado, tal como estaba antes de la operación.</summary>
    private sealed record GroupMember(string TrackId, string ClipId, Time Start, Time Duration, Time SourceIn, Time SourceOut, bool HasPrevious, bool HasNext, bool Generator)
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
                m.Track.Clips.Exists(c => c != m.Clip && c.Start == m.Clip.End),
                m.Clip.Generator is not null));
        }
    }
}
