using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Rodaje.Core.Collab;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Commands;
using Rodaje.Core.Tests.Properties;
using Xunit.Sdk;

namespace Rodaje.Core.Tests.Collab;

/// <summary>
/// Prueba de propiedad de la colaboración (colaboracion.md §8, fase 1a): secuencias aleatorias de ediciones (las de
/// <see cref="UndoExactnessPropertiesTests"/>: línea de tiempo, keyframes, marcadores, regiones, mezclador, compuestos, grupos de
/// gesto cerrados y cancelados, deshacer y rehacer, órdenes que fallan) aplicadas en A; cada paso del historial sale como un
/// parche (comparando el estado compartido antes y después, como hará <c>CollabSession</c>), pasa por el JSON de red y se aplica
/// en B. Al final, y en cada paso en parte de las semillas, la huella del estado compartido de A y de B es la misma. Semillas
/// fijas: un fallo se reproduce con su número.
/// </summary>
public sealed class PatchConvergenceTests
{
    private static int SwapsDone;
    private static int PatchesMade;
    private static readonly ConcurrentDictionary<ElementKind, int> KindsSeen = new();
    private static readonly ConcurrentDictionary<bool, int> Deletes = new();

    [Fact]
    public void Diez_mil_secuencias_aleatorias_convergen_en_dos_replicas()
    {
        SwapsDone = 0;
        PatchesMade = 0;
        KindsSeen.Clear();
        RunSeeds(1, 10_000, steps: 14, limitFor: seed => seed % 7 == 0 ? 1 + seed % 6 : 500, everyStepEvery: 25);

        // Si nunca se llegó a reordenar nada, la mitad de la propiedad no se probó.
        Assert.True(SwapsDone > 500, $"Solo se reordenaron {SwapsDone} pares de parches independientes.");
        Assert.True(Deletes.ContainsKey(true) && Deletes.ContainsKey(false), "Faltan borrados o altas.");
        Assert.True(PatchesMade > 20_000, $"Solo salieron {PatchesMade} parches.");
        foreach (ElementKind kind in new[] { ElementKind.Clip, ElementKind.Track, ElementKind.Marker, ElementKind.Region })
        {
            Assert.True(KindsSeen.ContainsKey(kind), $"Ningún parche tocó elementos de la clase {kind}.");
        }
    }

    [Theory]
    [InlineData(5025, 14)]
    [InlineData(777, 80)]
    [InlineData(4242, 80)]
    public void Secuencias_largas_convergen_paso_a_paso(int seed, int steps) =>
        Run(seed, steps, limit: 500, checkEveryStep: true);

    [Fact]
    public void Un_parche_repetido_se_rechaza_por_version()
    {
        Project a = SharedStateTests.Rich();
        Project b = a.Clone();
        SharedState before = SharedState.Capture(a);
        a.Sequences[0].Markers.Add(new Marker { Id = "mk-rep", Name = "uno" });
        Patch patch = PatchDiff.Create(before, SharedState.Capture(a), new ElementVersions(), new PatchHeader("p1", 0, "k:a", "Marcador"))!;
        var versions = new ElementVersions();

        Assert.True(PatchApply.TryApply(b, versions, patch).IsAccepted);
        PatchResult again = PatchApply.TryApply(b, versions, patch);

        Assert.Equal(PatchRejectionReason.VersionConflict, again.Rejection?.Reason);
        Assert.Equal(SharedState.Capture(a).Hash, SharedState.Capture(b).Hash);
    }

    [Fact]
    public void Un_parche_que_deja_el_proyecto_invalido_se_rechaza_sin_cambiar_nada()
    {
        Project a = SharedStateTests.Rich();
        Project b = a.Clone();
        string hash = SharedState.Capture(b).Hash;
        var versions = new ElementVersions();
        ElementState clip = SharedState.Capture(a).Elements[new ElementKey(ElementKind.Clip, "clip-c")];
        var copy = new PatchOp(ElementKind.Clip, "clip-c-2", clip.Parent, 0, clip.Content.Replace("\"id\":\"clip-c\"", "\"id\":\"clip-c-2\"", StringComparison.Ordinal));
        var overlapping = new Patch("p2", 0, "k:a", "Mal", [copy], [], []);

        PatchResult result = PatchApply.TryApply(b, versions, overlapping);

        Assert.Equal(PatchRejectionReason.Invalid, result.Rejection?.Reason);
        Assert.Equal(hash, SharedState.Capture(b).Hash);
        Assert.Equal(0, versions.Count);
    }

    private static void RunSeeds(int first, int count, int steps, Func<int, int> limitFor, int everyStepEvery)
    {
        var failures = new ConcurrentQueue<string>();
        Parallel.For(first, first + count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, 4)) }, seed =>
        {
            if (!failures.IsEmpty)
            {
                return;
            }

            try
            {
                Run(seed, steps, limitFor(seed), checkEveryStep: seed % everyStepEvery == 0);
            }
            catch (XunitException ex)
            {
                failures.Enqueue(ex.Message);
            }
            catch (Exception ex)
            {
                failures.Enqueue($"[semilla {seed}] excepción sin capturar: {ex}");
            }
        });

        if (failures.TryDequeue(out string? first1))
        {
            throw new XunitException(first1);
        }
    }

    private static void Run(int seed, int steps, int limit, bool checkEveryStep)
    {
        var random = new Random(seed);
        BuiltProject built = RandomTimeline.Build(UndoExactnessPropertiesTests.RandomSpec(random));
        Project a = built.Project;
        Project b = a.Clone();
        b.Name = "copia de B";
        var replicaB = new Replica(b);
        Project c = a.Clone();
        var stack = new UndoStack(a, limit);
        var log = new StringBuilder();
        var versionsA = new ElementVersions();
        SharedState baseline = SharedState.Capture(a);
        var patches = new List<Patch>();
        var states = new List<SharedState>();
        var befores = new List<SharedState>();

        void Fail(string message) =>
            throw new XunitException($"[semilla {seed}, límite {limit}] {message}\nPasos:\n{log}");

        stack.Changed += (_, _) =>
        {
            if (stack.IsInGroup)
            {
                return;
            }

            SharedState after = SharedState.Capture(a);
            Patch? patch = PatchDiff.Create(baseline, after, versionsA, new PatchHeader($"{seed}-{patches.Count}", patches.Count, "k:a", "paso"));
            if (patch is not null)
            {
                Interlocked.Increment(ref PatchesMade);
                foreach (PatchOp op in patch.Ops)
                {
                    KindsSeen.TryAdd(op.Kind, 1);
                    Deletes.TryAdd(op.IsDelete, 1);
                }

                patches.Add(patch);
                befores.Add(baseline);
                states.Add(after);
                versionsA.Advance(patch);
            }

            baseline = after;
        };

        Func<Random, (string Text, ICommand Command)>? drag = null;
        for (int step = 0; step < steps; step++)
        {
            int roll = random.Next(100);
            if (roll < 12)
            {
                if (!stack.IsInGroup)
                {
                    bool undo = random.Next(2) == 0;
                    bool done = undo ? stack.Undo() : stack.Redo();
                    log.Append(CultureInfo.InvariantCulture, $"  {(undo ? "deshacer" : "rehacer")} → {(done ? "hecho" : "nada")}\n");
                }
            }
            else if (roll < 16)
            {
                if (!stack.IsInGroup)
                {
                    stack.MarkClean();
                    log.Append("  guardar\n");
                }
            }
            else if (roll < 20)
            {
                stack.BreakMerge();
            }
            else if (roll < 26)
            {
                if (!stack.IsInGroup)
                {
                    stack.BeginGroup("Gesto");
                    log.Append("  abrir grupo\n");
                }
                else if (random.Next(3) == 0)
                {
                    stack.CancelGroup();
                    log.Append("  cancelar grupo\n");
                }
                else
                {
                    stack.EndGroup();
                    log.Append("  cerrar grupo\n");
                }
            }
            else
            {
                Func<Random, (string Text, ICommand Command)>? nextDrag = null;
                (string text, ICommand command) = roll < 45 && drag is not null ? drag(random) : UndoExactnessPropertiesTests.NextCommand(random, built, out nextDrag);
                drag = nextDrag ?? drag;
                log.Append("  ").Append(text);
                try
                {
                    stack.Execute(command);
                    log.Append(" → ok\n");
                }
                catch (EditException)
                {
                    log.Append(" → rechazada\n");
                }
                catch (UndoExactnessPropertiesTests.InjectedFailure)
                {
                    log.Append(" → falló a medias\n");
                }
            }

            if (checkEveryStep && !stack.IsInGroup)
            {
                Replay(patches, replicaB, states, seed, log, everyStep: true);
            }
        }

        if (stack.IsInGroup)
        {
            stack.EndGroup();
        }

        Replay(patches, replicaB, states, seed, log, everyStep: checkEveryStep);

        string finalA = SharedState.Capture(a).Hash;
        string finalB = SharedState.Capture(b).Hash;
        if (finalA != finalB)
        {
            Fail($"A y B terminaron distintos tras {patches.Count} parches.\n{Describe(SharedState.Capture(a), SharedState.Capture(b))}");
        }

        // Los mismos parches con los pares independientes en el otro orden (cambios de clips de pistas distintas).
        List<Patch> swapped = Swap(patches, befores);
        if (swapped.Count > 0)
        {
            var versionsC = new ElementVersions();
            foreach (Patch patch in swapped)
            {
                PatchResult result = PatchApply.TryApply(c, versionsC, Reparsed(patch, Fail));
                if (!result.IsAccepted)
                {
                    Fail($"Con los parches independientes reordenados, C rechazó «{patch.Id}»: {result.Rejection!.Message}");
                }
            }

            if (SharedState.Capture(c).Hash != finalA)
            {
                Fail($"C (parches independientes reordenados) terminó distinto de A tras {patches.Count} parches.");
            }
        }
    }

    private static string Describe(SharedState a, SharedState b)
    {
        var text = new StringBuilder();
        foreach (ElementKey key in a.Elements.Keys.Union(b.Elements.Keys).OrderBy(k => k.ToString(), StringComparer.Ordinal))
        {
            a.Elements.TryGetValue(key, out ElementState? left);
            b.Elements.TryGetValue(key, out ElementState? right);
            if (left?.Content != right?.Content || left?.Parent != right?.Parent)
            {
                text.Append("  ").Append(key).Append("\n    A: ").Append(left?.Parent).Append(' ').Append(left?.Content).Append("\n    B: ").Append(right?.Parent).Append(' ').Append(right?.Content).Append('\n');
            }
        }

        foreach (string container in a.Orders.Keys.Union(b.Orders.Keys).Order(StringComparer.Ordinal))
        {
            string left = string.Join(",", a.Orders.GetValueOrDefault(container) ?? []);
            string right = string.Join(",", b.Orders.GetValueOrDefault(container) ?? []);
            if (left != right)
            {
                text.Append("  orden ").Append(container).Append("\n    A: ").Append(left).Append("\n    B: ").Append(right).Append('\n');
            }
        }

        return text.ToString();
    }

    private static Patch Reparsed(Patch patch, Action<string> fail)
    {
        string json = PatchWire.ToJson(patch);
        PatchParseResult parsed = PatchWire.TryParse(json);
        if (!parsed.IsValid)
        {
            fail($"El parche «{patch.Id}» no pasó el saneador: {parsed.Rejection!.Message}");
        }

        return parsed.Patch!;
    }

    private sealed class Replica(Project project)
    {
        public Project Project { get; } = project;

        public ElementVersions Versions { get; } = new();

        public int Applied { get; set; }
    }

    private static void Replay(List<Patch> patches, Replica replica, List<SharedState> states, int seed, StringBuilder log, bool everyStep)
    {
        while (replica.Applied < patches.Count)
        {
            int index = replica.Applied;
            PatchParseResult parsed = PatchWire.TryParse(PatchWire.ToJson(patches[index]));
            if (!parsed.IsValid)
            {
                throw new XunitException($"[semilla {seed}] El parche {index} no pasó el saneador: {parsed.Rejection!.Message}\nPasos:\n{log}");
            }

            PatchResult result = PatchApply.TryApply(replica.Project, replica.Versions, parsed.Patch!);
            if (!result.IsAccepted)
            {
                throw new XunitException($"[semilla {seed}] B rechazó el parche {index} ({patches[index].Description}): {result.Rejection!.Reason} {result.Rejection.Message} {result.Rejection.Element}\nPasos:\n{log}");
            }

            if (everyStep && SharedState.Capture(replica.Project).Hash != states[index].Hash)
            {
                throw new XunitException($"[semilla {seed}] Tras el parche {index} B es distinto de A.\nPasos:\n{log}");
            }

            replica.Applied++;
        }
    }

    /// <summary>
    /// Intercambia pares contiguos de parches que solo tocan clips de una pista cada uno, de pistas distintas y sin moverlos de
    /// pista: no dependen uno del otro, así que el orden da igual.
    /// </summary>
    private static List<Patch> Swap(List<Patch> patches, List<SharedState> befores)
    {
        string?[] track = new string?[patches.Count];
        for (int i = 0; i < patches.Count; i++)
        {
            track[i] = LocalTrack(patches[i], befores[i]);
        }

        var result = new List<Patch>(patches);
        int swaps = 0;
        for (int i = 0; i + 1 < result.Count; i++)
        {
            if (track[i] is not null && track[i + 1] is not null && track[i] != track[i + 1])
            {
                (result[i], result[i + 1]) = (result[i + 1], result[i]);
                (track[i], track[i + 1]) = (track[i + 1], track[i]);
                swaps++;
                i++;
            }
        }

        Interlocked.Add(ref SwapsDone, swaps);
        return swaps == 0 ? [] : result;
    }

    private static string? LocalTrack(Patch patch, SharedState before)
    {
        if (patch.Orders.Count > 0 || patch.Ops.Count == 0 || patch.Ops.Any(o => o.Kind != ElementKind.Clip))
        {
            return null;
        }

        string? parent = patch.Ops[0].Parent;
        foreach (PatchOp op in patch.Ops)
        {
            bool existed = before.Elements.TryGetValue(op.Key, out ElementState? was);
            if (op.Parent != parent || (existed && was!.Parent != parent))
            {
                return null;
            }
        }

        return parent;
    }
}
