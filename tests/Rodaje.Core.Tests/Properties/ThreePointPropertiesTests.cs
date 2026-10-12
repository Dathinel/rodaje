using CsCheck;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using Xunit.Abstractions;
using static Rodaje.Core.Tests.Properties.V1Support;

namespace Rodaje.Core.Tests.Properties;

/// <summary>
/// Edición de tres y cuatro puntos sobre proyectos aleatorios: el plan cumple las reglas clásicas, insertar corre lo que
/// sigue exactamente la duración, sobrescribir solo cambia el tramo, las demás pistas no se tocan y deshacer vuelve byte a byte.
/// </summary>
/// <remarks>Para reproducir un fallo, pasar la semilla que reporta CsCheck en <c>seed</c> o en <c>CsCheck_Seed</c>.</remarks>
public class ThreePointPropertiesTests(ITestOutputHelper output)
{
    private const string Seq = BuiltProject.SequenceId;

    /// <summary>Las marcas en cuadros; las negativas no se ponen.</summary>
    internal sealed record MarksSpec(int Media, int SourceIn, int SourceOut, int SequenceIn, int SequenceOut, int Playhead, int Video, int Audio,
        bool Insert, int Fit, int Scope)
    {
        public override string ToString() => Inv(
            $"medio {Media}, origen [{SourceIn}, {SourceOut}), secuencia [{SequenceIn}, {SequenceOut}), cursor {Playhead}, pistas {Video}/{Audio}, {(Insert ? "insertar" : "sobrescribir")}, ajuste {Fit}, alcance {Scope}");
    }

    private static readonly Gen<int> GenMark = Gen.Frequency((1, Gen.Const(-1)), (2, Gen.Int[0, 1000]));

    private static readonly Gen<MarksSpec> GenMarks = Gen.Select(
        Gen.Select(Gen.Int[0, 9], GenMark, GenMark, GenMark, GenMark, Gen.Int[0, 700], (m, a, b, c, d, p) => (m, a, b, c, d, p)),
        Gen.Int[-1, 3], Gen.Int[-1, 3], Gen.Bool, Gen.Int[0, 2], Gen.Int[0, 2],
        (x, v, a, insert, fit, scope) => new MarksSpec(x.m, x.a, x.b, x.c, x.d, x.p, v, a, insert, fit, scope));

    private static readonly Gen<(ProjectSpec Project, MarksSpec Marks)> GenScenario =
        Gen.Select(RandomTimeline.GenProject, GenMarks, (p, m) => (p with { LockBits = p.LockBits | 0x0554 }, m));

    /// <summary>El plan respeta las marcas puestas y calcula la que falta como dicen las reglas clásicas.</summary>
    [Fact]
    public void Plan_CumpleLasReglasClasicas()
    {
        int planned = 0, total = 0;
        GenScenario.Sample(
            s =>
            {
                total++;
                BuiltProject built = RandomTimeline.Build(s.Project);
                ThreePointRequest request = Request(built, s.Marks);
                ThreePointPlan plan;
                try
                {
                    plan = TimelineEdits.PlanThreePoint(built.Project, request);
                }
                catch (EditException)
                {
                    return;
                }

                planned++;
                CheckPlan(built.Project.GetMedia(request.MediaId), request, plan);
            },
            writeLine: output.WriteLine,
            iter: 5000,
            print: s => $"{s.Project}\n{s.Marks}");
        output.WriteLine($"{planned} de {total} planes válidos");
        Assert.True(planned * 3 >= total, $"Pocos planes válidos: {planned} de {total}");
    }

    private static void CheckPlan(MediaRef media, ThreePointRequest r, ThreePointPlan plan)
    {
        Assert.True(plan.SourceIn >= Time.Zero && plan.SourceIn < plan.SourceOut && plan.SourceOut <= media.Duration, "Tramo del origen fuera del medio");
        Assert.True(plan.At >= Time.Zero);
        Assert.True(plan.Duration > Time.Zero);
        bool four = r.SequenceIn is not null && r.SequenceOut is not null;
        if (four)
        {
            Time span = r.SequenceOut!.Value - r.SequenceIn!.Value;
            Assert.Equal(r.SequenceIn, plan.At);
            bool differs = r.SourceIn is { } x && r.SourceOut is { } y && y - x != span;
            Assert.Equal(differs && r.Fit == FourPointFit.Ask, plan.NeedsDecision);
            Assert.Equal(differs && r.Fit == FourPointFit.FitToFill, plan.Rate is not null);
            if (!plan.NeedsDecision)
            {
                Assert.Equal(span, plan.Duration);
            }

            if (r.SourceIn is { } sIn)
            {
                Assert.Equal(sIn, plan.SourceIn);
            }
            else if (r.SourceOut is { } sOut)
            {
                Assert.Equal(sOut, plan.SourceOut);
            }

            if (differs && r.Fit != FourPointFit.Trim)
            {
                Assert.Equal(r.SourceOut, plan.SourceOut);
            }
        }
        else
        {
            Assert.Null(plan.Rate);
            Assert.False(plan.NeedsDecision);
            Assert.Equal(r.SourceIn ?? Time.Zero, plan.SourceIn);
            Assert.Equal(r.SourceOut ?? media.Duration, plan.SourceOut);
            Time expectedAt = r.SequenceIn ?? (r.SequenceOut is { } end ? end - plan.Duration : r.Playhead);
            Assert.Equal(expectedAt, plan.At);
        }

        if (plan.Rate is null)
        {
            Assert.Equal(plan.SourceOut - plan.SourceIn, plan.Duration);
        }
    }

    /// <summary>
    /// Aplicada: en cada pista destino lo anterior a la entrada no cambia, lo posterior se corre justo la duración (insertar)
    /// o queda igual fuera del tramo (sobrescribir), y el tramo muestra el origen pedido; las pistas que no se corren no
    /// cambian; ajustar a la velocidad llena el tramo de la secuencia; deshacer vuelve byte a byte.
    /// </summary>
    [Fact]
    public void Edicion_CorreOSobrescribeSoloLoQueToca()
    {
        int applied = 0, total = 0;
        GenScenario.Sample(
            s =>
            {
                total++;
                if (RunEdit(s.Project, s.Marks))
                {
                    applied++;
                }
            },
            writeLine: output.WriteLine,
            iter: 5000,
            print: s => $"{s.Project}\n{s.Marks}");
        output.WriteLine($"{applied} de {total} ediciones aplicadas");
        Assert.True(applied * 5 >= total, $"Pocas ediciones aplicadas: {applied} de {total}");
    }

    private static bool RunEdit(ProjectSpec spec, MarksSpec marks)
    {
        BuiltProject built = RandomTimeline.Build(spec);
        Project project = built.Project;
        Sequence sequence = built.Sequence;
        ThreePointRequest request = Request(built, marks);
        string before = ProjectSerializer.Serialize(project);
        Sequence old = sequence.Clone();
        var stack = new UndoStack(project);
        SequenceEditCommand command = Edits.ThreePointEdit(Seq, request, built.Ids);
        try
        {
            stack.Execute(command);
        }
        catch (EditException)
        {
            Assert.Equal(before, ProjectSerializer.Serialize(project));
            return false;
        }

        Assert.Empty(ModelValidator.Validate(project));
        ThreePointPlan plan = TimelineEdits.PlanThreePoint(Project(old, project), request);
        Clip first = sequence.GetClip(command.CreatedIds[0]).Clip;
        Time at = first.Start;
        Time duration = first.Duration;
        Assert.Equal(plan.At, at);
        Assert.All(command.CreatedIds, id =>
        {
            Clip c = sequence.GetClip(id).Clip;
            Assert.Equal((at, duration, request.MediaId), (c.Start, c.Duration, c.MediaId));
        });
        if (command.CreatedIds.Count > 1)
        {
            Assert.NotNull(first.LinkId);
            Assert.Single(command.CreatedIds.Select(id => sequence.GetClip(id).Clip.LinkId).Distinct());
        }

        if (plan.Rate is null)
        {
            Assert.Equal(plan.Duration, duration);
        }
        else if ((plan.SourceOut - plan.SourceIn).Ticks * 10 >= plan.Duration.Ticks && (plan.SourceOut - plan.SourceIn).Ticks <= plan.Duration.Ticks * 100)
        {
            Assert.True(Math.Abs((duration - plan.Duration).Ticks) <= Time.FromFrames(1, sequence.FrameRate).Ticks,
                Inv($"Ajustar a la velocidad dura {duration.Ticks} y el tramo {plan.Duration.Ticks}"));
        }

        var targets = command.CreatedIds.Select(id => sequence.GetClip(id).Track.Id).ToHashSet(StringComparer.Ordinal);
        bool insert = request.Mode == PlacementMode.Insert;
        Time step = Time.FromFrames(1, sequence.FrameRate);
        Time horizon = Time.Max(old.Duration, at) + duration + step + step;
        foreach (Track track in sequence.AllTracks)
        {
            Track? previous = old.FindTrack(track.Id);
            Assert.NotNull(previous);
            bool target = targets.Contains(track.Id);
            bool shifted = insert && (target || (request.Scope.IncludesAllTracks() && !previous.Locked));
            for (Time t = Time.Zero; t < horizon; t += Time.FromTicks(step.Ticks * 7))
            {
                var now = Probe(track, t);
                if (target && t >= at && t < at + duration)
                {
                    Assert.NotNull(now);
                    Assert.Equal("media:" + request.MediaId, now.Value.Source);
                    if (plan.Rate is null)
                    {
                        Assert.Equal(plan.SourceIn + (t - at), now.Value.Origin);
                    }

                    continue;
                }

                Time from = shifted && t >= at ? t - duration : t;
                if (shifted && t >= at && t < at + duration)
                {
                    // Hueco que abrió la inserción en una pista que no recibe clip.
                    Assert.Null(now);
                    continue;
                }

                Assert.True(Probe(previous, from) == now, Inv($"Pista {track.Id} en {t.Ticks}: antes {Probe(previous, from)}, ahora {now}"));
            }
        }

        if (request.Scope == RippleScope.AllTracksAndMarkers && insert)
        {
            Assert.Equal(old.Markers.Select(m => m.Time >= at ? m.Time + duration : m.Time), sequence.Markers.Select(m => m.Time));
        }
        else
        {
            Assert.Equal(old.Markers.Select(m => m.Time), sequence.Markers.Select(m => m.Time));
        }

        stack.Undo();
        Assert.Equal(before, ProjectSerializer.Serialize(project));
        return true;
    }

    private static Project Project(Sequence old, Project current) => new() { Id = current.Id, Media = current.Media, Sequences = [old] };

    private static ThreePointRequest Request(BuiltProject built, MarksSpec m)
    {
        Rational rate = built.Rate;
        MediaRef media = built.Project.Media[m.Media % built.Project.Media.Count];
        long length = media.Duration.ToFrames(rate);
        // Las del origen en milésimas del medio (a veces un cuadro más allá); las de la secuencia, en cuadros de 0 a 1000.
        Time? Source(int permille) => permille < 0 ? null : Time.FromFrames((permille * (length + 1)) / 1000, rate);
        Time? Mark(int frames) => frames < 0 ? null : Time.FromFrames(frames * 7 / 10, rate);
        Sequence s = built.Sequence;
        return new ThreePointRequest(
            media.Id,
            Source(m.SourceIn),
            Source(m.SourceOut),
            Mark(m.SequenceIn),
            Mark(m.SequenceOut),
            Time.FromFrames(m.Playhead, rate),
            m.Video < 0 ? null : s.VideoTracks[m.Video % s.VideoTracks.Count].Id,
            m.Audio < 0 ? null : s.AudioTracks[m.Audio % s.AudioTracks.Count].Id,
            m.Insert ? PlacementMode.Insert : PlacementMode.Overwrite,
            (FourPointFit)m.Fit,
            (RippleScope)m.Scope);
    }
}
