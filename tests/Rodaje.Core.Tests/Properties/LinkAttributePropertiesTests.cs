using System.Text;
using CsCheck;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Commands;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Rodaje.Core.Tests.Properties;

/// <summary>
/// Enlazar, desenlazar, separar el audio y pegar atributos sobre proyectos aleatorios (con efectos, velocidades, curvas,
/// congelados y pistas bloqueadas): un rechazo no cambia nada, lo hecho deja el proyecto válido, las pistas bloqueadas
/// quedan intactas y deshacer vuelve a cada estado anterior.
/// </summary>
/// <remarks>Para reproducir un fallo, pasar la semilla que reporta CsCheck en <c>seed</c> o en <c>CsCheck_Seed</c>.</remarks>
public class LinkAttributePropertiesTests(ITestOutputHelper output)
{
    private const string Seq = BuiltProject.SequenceId;

    /// <summary>Clase (0 enlazar, 1 desenlazar, 2 separar audio, 3 pegar atributos, 4 deshacer) y números para elegir.</summary>
    private sealed record LinkOp(int Kind, int A, int B, int C, int Kinds);

    private static readonly Gen<LinkOp> GenLinkOp =
        Gen.Select(Gen.Int[0, 4], Gen.Int[0, 1000], Gen.Int[0, 1000], Gen.Int[0, 1000], Gen.Int[0, 15], (k, a, b, c, m) => new LinkOp(k, a, b, c, m));

    private static readonly Gen<(ProjectSpec Project, LinkOp[] Ops)> GenScenario =
        Gen.Select(RandomTimeline.GenProject, GenLinkOp.Array[1, 25]);

    [Fact]
    public void EnlacesYAtributosAleatorios_CumplenLasReglas() =>
        GenScenario.Sample(
            s => Run(s.Project, s.Ops),
            writeLine: output.WriteLine,
            iter: 3000,
            print: s => $"{s.Project}\nOperaciones: {string.Join(" ", s.Ops.Select(o => o.ToString()))}");

    private static void Run(ProjectSpec spec, LinkOp[] ops)
    {
        BuiltProject built = RandomTimeline.Build(spec);
        Project project = built.Project;
        var stack = new UndoStack(project);
        var dumps = new List<string> { ProjectDump.Of(project) };
        var log = new StringBuilder();
        void Fail(string message) =>
            throw new XunitException($"{message}\nOperaciones:\n{log}\nEstado:\n{ProjectDump.Of(project)}");

        foreach (LinkOp op in ops)
        {
            List<Clip> clips = [.. built.Sequence.AllTracks.SelectMany(t => t.Clips)];
            if (op.Kind == 4)
            {
                if (stack.Undo())
                {
                    dumps.RemoveAt(dumps.Count - 1);
                    log.Append("  deshacer\n");
                    if (ProjectDump.Of(project) != dumps[^1])
                    {
                        Fail("Deshacer no volvió al estado anterior.");
                    }
                }

                continue;
            }

            if (clips.Count == 0)
            {
                continue;
            }

            string Pick(int n) => clips[n % clips.Count].Id;
            SequenceEditCommand command;
            switch (op.Kind)
            {
                case 0:
                    command = Edits.LinkClips(Seq, [Pick(op.A), Pick(op.B)], built.Ids);
                    break;
                case 1:
                    command = Edits.UnlinkClips(Seq, Pick(op.A), built.Ids);
                    break;
                case 2:
                    command = Edits.DetachAudio(Seq, Pick(op.A), ids: built.Ids);
                    break;
                default:
                    ClipAttributes copied = ClipAttributes.Capture(built.Sequence, Pick(op.A));
                    command = Edits.PasteAttributes(
                        Seq, [Pick(op.B), Pick(op.C)], copied, (ClipAttributeKinds)op.Kinds, (RippleScope)(op.C % 3), built.Ids);
                    break;
            }

            log.Append("  ").Append(op).Append(' ').Append(command.Description);
            string before = ProjectDump.Of(project);
            var locked = built.Sequence.AllTracks.Where(t => t.Locked).ToDictionary(t => t.Id, t => TrackDump(t), StringComparer.Ordinal);
            try
            {
                stack.Execute(command);
            }
            catch (EditException ex)
            {
                log.Append(" → rechazada: ").Append(ex.Message).Append('\n');
                if (ProjectDump.Of(project) != before)
                {
                    Fail("Una edición rechazada cambió el proyecto.");
                }

                continue;
            }
            catch (Exception ex)
            {
                Fail($"Lanzó {ex.GetType().Name} en vez de EditException: {ex}");
            }

            log.Append(" → ok\n");
            IReadOnlyList<string> errors = ModelValidator.Validate(project);
            if (errors.Count > 0)
            {
                Fail("El proyecto quedó inválido: " + string.Join(" | ", errors));
            }

            foreach ((string id, string dump) in locked)
            {
                if (built.Sequence.FindTrack(id) is not { } track || TrackDump(track) != dump)
                {
                    Fail($"La pista bloqueada «{id}» cambió.");
                }
            }

            dumps.Add(ProjectDump.Of(project));
        }
    }

    private static string TrackDump(Track track) => string.Join(
        "\n",
        track.Clips.Select(c =>
            $"{c.Id} {c.MediaId} {c.SourceIn.Ticks} {c.SourceOut.Ticks} {c.Start.Ticks} {c.Duration.Ticks} {c.LinkId} {c.Enabled} "
            + $"{c.Speed?.Rate} {c.Speed?.Reverse} {c.Speed?.PreservePitch} "
            + string.Join(",", c.Effects.Select(e => $"{e.Id}:{e.Type}:{e.Parameters.Sum(p => p.Value.Keyframes.Count)}"))));
}
