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

public sealed class CollabHistoryTests
{
    private static int UndosDone;
    private static int RedosDone;

    private static PatchHeader Header(string id) => new(id, 0, "k:a", "deshacer");

    private static void Toggle(Project project)
    {
        Clip clip = project.Sequences[0].GetClip("clip-c").Clip;
        clip.Enabled = !clip.Enabled;
    }

    [Fact]
    public void Undo_restores_my_change_and_keeps_what_others_did_meanwhile()
    {
        Project a = SharedStateTests.Rich();
        var history = new CollabHistory();
        var versions = new ElementVersions();
        SharedState before = SharedState.Capture(a);
        Toggle(a);
        history.Record(before, SharedState.Capture(a), "Activar o desactivar");

        a.Sequences[0].Markers.Add(new Marker { Id = "mk-otro", Name = "de otra persona" });
        HistoryOutcome outcome = history.PlanUndo(SharedState.Capture(a), versions, Header("u1"));

        Assert.NotNull(outcome.Plan);
        Assert.True(PatchApply.TryApply(a, versions, outcome.Plan.Patch).IsAccepted);
        history.CommitUndo();
        Assert.True(a.Sequences[0].GetClip("clip-c").Clip.Enabled == SharedStateTests.Rich().Sequences[0].GetClip("clip-c").Clip.Enabled);
        Assert.Contains(a.Sequences[0].Markers, m => m.Id == "mk-otro");
        Assert.True(history.CanRedo);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void Undo_is_refused_when_somebody_else_changed_the_same_element()
    {
        Project a = SharedStateTests.Rich();
        var history = new CollabHistory();
        SharedState before = SharedState.Capture(a);
        a.Sequences[0].GetClip("clip-c").Clip.Color = "#112233";
        history.Record(before, SharedState.Capture(a), "Color");

        a.Sequences[0].GetClip("clip-c").Clip.Color = "#445566";
        HistoryOutcome outcome = history.PlanUndo(SharedState.Capture(a), new ElementVersions(), Header("u1"));

        Assert.Null(outcome.Plan);
        Assert.Equal(HistoryFailure.ChangedByOthers, outcome.Failure);
        Assert.Equal(new ElementKey(ElementKind.Clip, "clip-c"), outcome.Element);
        Assert.True(history.CanUndo);

        history.DiscardUndo();
        Assert.False(history.CanUndo);
        Assert.Equal(HistoryFailure.Nothing, history.PlanUndo(SharedState.Capture(a), new ElementVersions(), Header("u2")).Failure);
    }

    [Fact]
    public void Redo_after_undo_gives_back_the_step_and_a_new_step_forgets_the_redo()
    {
        Project a = SharedStateTests.Rich();
        var history = new CollabHistory();
        var versions = new ElementVersions();
        SharedState before = SharedState.Capture(a);
        Toggle(a);
        SharedState after = SharedState.Capture(a);
        history.Record(before, after, "Paso");

        Assert.True(PatchApply.TryApply(a, versions, history.PlanUndo(after, versions, Header("u")).Plan!.Patch).IsAccepted);
        history.CommitUndo();
        Assert.Equal(before.Hash, SharedState.Capture(a).Hash);

        Assert.True(PatchApply.TryApply(a, versions, history.PlanRedo(SharedState.Capture(a), versions, Header("r")).Plan!.Patch).IsAccepted);
        history.CommitRedo();
        Assert.Equal(after.Hash, SharedState.Capture(a).Hash);

        history.PlanUndo(after, versions, Header("u2"));
        history.CommitUndo();
        history.Record(after, after, "Nada");
        Assert.True(history.CanRedo);
        SharedState other = SharedState.Capture(a);
        a.Sequences[0].Markers.Add(new Marker { Id = "mk-nuevo", Name = "n" });
        history.Record(other, SharedState.Capture(a), "Marcador");
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void The_limit_drops_the_oldest_steps()
    {
        Project a = SharedStateTests.Rich();
        var history = new CollabHistory(limit: 2);
        for (int i = 0; i < 5; i++)
        {
            SharedState before = SharedState.Capture(a);
            Toggle(a);
            history.Record(before, SharedState.Capture(a), "Paso " + i);
        }

        Assert.Equal("Paso 4", history.UndoDescription);
        history.CommitUndo();
        history.CommitUndo();
        history.CommitUndo();
        Assert.False(history.CanUndo);
        Assert.Equal(2, new[] { history.CanRedo, history.RedoDescription is not null }.Count(x => x) );
    }

    [Fact]
    public void Mil_secuencias_de_pasos_deshacen_y_rehacen_como_el_historial_de_una_persona()
    {
        UndosDone = 0;
        RedosDone = 0;
        var failures = new ConcurrentQueue<string>();
        Parallel.For(1, 1_001, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, 4)) }, seed =>
        {
            if (!failures.IsEmpty)
            {
                return;
            }

            try
            {
                Run(seed, steps: 24);
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

        if (failures.TryDequeue(out string? first))
        {
            throw new XunitException(first);
        }

        Assert.True(UndosDone > 1_000 && RedosDone > 200, $"Solo hubo {UndosDone} deshacer y {RedosDone} rehacer.");
    }

    private static void Run(int seed, int steps)
    {
        var random = new Random(seed);
        BuiltProject built = RandomTimeline.Build(UndoExactnessPropertiesTests.RandomSpec(random));
        Project a = built.Project;
        Project b = a.Clone();
        var history = new CollabHistory();
        var versionsA = new ElementVersions();
        var versionsB = new ElementVersions();
        var log = new StringBuilder();
        var patches = new List<Patch>();
        var expected = new List<string> { SharedState.Capture(a).Hash };
        int at = 0;
        SharedState baseline = SharedState.Capture(a);
        int counter = 0;

        void Fail(string message) =>
            throw new XunitException($"[semilla {seed}] {message}\nPasos:\n{log}");

        for (int step = 0; step < steps; step++)
        {
            int roll = random.Next(100);
            if (roll < 30 && (history.CanUndo || history.CanRedo))
            {
                bool undo = history.CanUndo && (roll < 20 || !history.CanRedo);
                HistoryOutcome outcome = undo
                    ? history.PlanUndo(baseline, versionsA, new PatchHeader($"h{seed}-{counter++}", 0, "k:a", "deshacer"))
                    : history.PlanRedo(baseline, versionsA, new PatchHeader($"h{seed}-{counter++}", 0, "k:a", "rehacer"));
                log.Append(undo ? "  deshacer\n" : "  rehacer\n");
                if (outcome.Plan is null)
                {
                    Fail($"No se pudo {(undo ? "deshacer" : "rehacer")} con una sola persona: {outcome.Failure} {outcome.Element}");
                    return;
                }

                PatchResult result = PatchApply.TryApply(a, versionsA, outcome.Plan.Patch);
                if (!result.IsAccepted)
                {
                    Fail($"El parche de {(undo ? "deshacer" : "rehacer")} se rechazó: {result.Rejection!.Message}");
                }

                if (undo)
                {
                    Interlocked.Increment(ref UndosDone);
                    history.CommitUndo();
                    at--;
                }
                else
                {
                    Interlocked.Increment(ref RedosDone);
                    history.CommitRedo();
                    at++;
                }

                patches.Add(outcome.Plan.Patch);
                baseline = SharedState.Capture(a);
                if (baseline.Hash != expected[at])
                {
                    Fail($"Tras {(undo ? "deshacer" : "rehacer")} el estado no es el del paso {at}.");
                }

                continue;
            }

            var stack = new UndoStack(a);
            stack.BeginGroup("Paso");
            int commands = random.Next(1, 4);
            Func<Random, (string Text, ICommand Command)>? drag = null;
            for (int i = 0; i < commands; i++)
            {
                Func<Random, (string Text, ICommand Command)>? next = null;
                (string text, ICommand command) = i > 0 && drag is not null && random.Next(2) == 0 ? drag(random) : UndoExactnessPropertiesTests.NextCommand(random, built, out next);
                drag = next ?? drag;
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

            if (random.Next(5) == 0)
            {
                stack.CancelGroup();
                log.Append("  cancelar paso\n");
            }
            else
            {
                stack.EndGroup();
            }

            SharedState after = SharedState.Capture(a);
            Patch? patch = PatchDiff.Create(baseline, after, versionsA, new PatchHeader($"s{seed}-{counter++}", 0, "k:a", "paso"));
            if (patch is not null)
            {
                history.Record(baseline, after, "paso");
                versionsA.Advance(patch);
                patches.Add(patch);
                expected.RemoveRange(at + 1, expected.Count - at - 1);
                expected.Add(after.Hash);
                at++;
            }

            baseline = after;
        }

        foreach (Patch patch in patches)
        {
            PatchParseResult parsed = PatchWire.TryParse(PatchWire.ToJson(patch));
            if (!parsed.IsValid)
            {
                Fail($"El parche {patch.Id} no pasó el saneador: {parsed.Rejection!.Message}");
            }

            PatchResult result = PatchApply.TryApply(b, versionsB, parsed.Patch!);
            if (!result.IsAccepted)
            {
                Fail($"B rechazó el parche {patch.Id}: {result.Rejection!.Reason} {result.Rejection.Message}");
            }
        }

        if (SharedState.Capture(b).Hash != SharedState.Capture(a).Hash)
        {
            Fail(string.Create(CultureInfo.InvariantCulture, $"A y B terminaron distintos tras {patches.Count} parches."));
        }
    }
}
