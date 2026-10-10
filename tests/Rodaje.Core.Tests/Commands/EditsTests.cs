using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Commands.CommandFixtures;

namespace Rodaje.Core.Tests.Commands;

/// <summary>Cada fábrica de <see cref="Edits"/> equivale a su operación de <see cref="TimelineEdits"/>.</summary>
public class EditsTests
{
    /// <summary>Compara el comando con la operación directa en un proyecto idéntico, con deshacer y rehacer.</summary>
    private static SequenceEditCommand Check(
        Func<IIdGenerator, SequenceEditCommand> factory,
        Func<EditContext, IReadOnlyList<string>> direct,
        string description,
        string? mergeKey,
        int createdCount = 0)
    {
        Project project = Create();
        string before = ProjectDump.Of(project);
        SequenceEditCommand command = factory(new FreshIds());
        Assert.Equal(description, command.Description);
        Assert.Equal(mergeKey, command.MergeKey);
        Assert.Equal(SequenceId, command.SequenceId);
        Assert.False(command.HasRun);

        // Crear el comando no toca el proyecto.
        Assert.Equal(before, ProjectDump.Of(project));

        var stack = new UndoStack(project);
        stack.Execute(command);
        string after = ProjectDump.Of(project);
        Assert.NotEqual(before, after);
        Assert.Empty(ModelValidator.Validate(project));
        Assert.Equal(description, stack.UndoDescription);

        Project reference = Create();
        IReadOnlyList<string> created = direct(new EditContext(reference, Seq(reference), new FreshIds()));
        Assert.Equal(ProjectDump.Of(reference), after);
        Assert.Equal(created, command.CreatedIds);
        Assert.Equal(createdCount, command.CreatedIds.Count);

        Assert.True(stack.Undo());
        Assert.Equal(before, ProjectDump.Of(project));
        Assert.True(stack.Redo());
        Assert.Equal(after, ProjectDump.Of(project));
        Assert.True(stack.Undo());
        Assert.Equal(before, ProjectDump.Of(project));
        return command;
    }

    private static IReadOnlyList<string> None(Action action)
    {
        action();
        return [];
    }

    [Fact]
    public void Split_CortaElClipYSuEnlazado()
    {
        SequenceEditCommand command = Check(
            ids => Edits.Split(SequenceId, "clip-1", S(2), ids),
            ctx => TimelineEdits.Split(ctx, "clip-1", S(2)),
            "Cortar clip",
            null,
            createdCount: 2);
        Assert.All(command.CreatedIds, id => Assert.StartsWith("clip-", id, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(TrimEdge.In, 1.0, "Recortar inicio", "trim:clip-2:in")]
    [InlineData(TrimEdge.Out, -1.0, "Recortar final", "trim:clip-2:out")]
    public void Trim_DescripcionYClavePorBorde(TrimEdge edge, double delta, string description, string key) =>
        Check(
            ids => Edits.Trim(SequenceId, "clip-2", edge, S(delta), ids),
            ctx => None(() => TimelineEdits.Trim(ctx, "clip-2", edge, S(delta))),
            description,
            key);

    [Theory]
    [InlineData(TrimEdge.In, RippleScope.Track, "rippletrim:clip-1:in")]
    [InlineData(TrimEdge.Out, RippleScope.AllTracks, "rippletrim:clip-1:out")]
    public void RippleTrim_DescripcionYClave(TrimEdge edge, RippleScope scope, string key) =>
        Check(
            ids => Edits.RippleTrim(SequenceId, "clip-1", edge, S(1), scope, ids),
            ctx => None(() => TimelineEdits.RippleTrim(ctx, "clip-1", edge, S(1), scope)),
            "Recorte ripple",
            key);

    [Fact]
    public void Roll_DelegaYUsaClaveDelClip() =>
        Check(
            ids => Edits.Roll(SequenceId, "clip-1", S(1), ids),
            ctx => None(() => TimelineEdits.Roll(ctx, "clip-1", S(1))),
            "Roll",
            "roll:clip-1");

    [Fact]
    public void Slip_DelegaYUsaClaveDelClip() =>
        Check(
            ids => Edits.Slip(SequenceId, "clip-2", S(1), ids),
            ctx => None(() => TimelineEdits.Slip(ctx, "clip-2", S(1))),
            "Slip",
            "slip:clip-2");

    [Fact]
    public void Slide_DelegaYUsaClaveDelClip() =>
        Check(
            ids => Edits.Slide(SequenceId, "clip-2", S(1), ids),
            ctx => None(() => TimelineEdits.Slide(ctx, "clip-2", S(1))),
            "Slide",
            "slide:clip-2");

    [Fact]
    public void Move_DelegaYUsaClaveDelClip() =>
        Check(
            ids => Edits.Move(SequenceId, "clip-2", S(20), ids: ids),
            ctx => None(() => TimelineEdits.Move(ctx, "clip-2", S(20), null, PlacementMode.Strict)),
            "Mover clip",
            "move:clip-2");

    [Theory]
    [InlineData(PlacementMode.Overwrite)]
    [InlineData(PlacementMode.Insert)]
    public void Move_ConModoQueTocaOtrosClips_DelegaYNoLlevaClaveDeFusion(PlacementMode mode) =>
        Check(
            ids => Edits.Move(SequenceId, "clip-2", S(3), null, mode, ids),
            ctx => None(() => TimelineEdits.Move(ctx, "clip-2", S(3), null, mode)),
            "Mover clip",
            null);

    [Fact]
    public void ArrastreEnModoSobrescribir_NoSeFundeYNoDejaRecortadoLoQueSoloSePaso()
    {
        // Fundido, pasar por encima de clip-1 lo dejaría recortado aunque el destino final esté libre.
        Project project = Create();
        string before = ProjectDump.Of(project);
        var stack = new UndoStack(project);

        stack.Execute(Edits.Move(SequenceId, "clip-2", S(3), mode: PlacementMode.Overwrite));
        stack.Execute(Edits.Move(SequenceId, "clip-2", S(30), mode: PlacementMode.Overwrite));
        Assert.Equal(2, stack.Count);

        Assert.True(stack.Undo());
        Assert.True(stack.Undo());
        Assert.Equal(before, ProjectDump.Of(project));
    }

    [Fact]
    public void SetTrackState_DesbloquearYRenombrarALaVez_FuncionaYDeshacerRestaura()
    {
        Project project = Create();
        string before = ProjectDump.Of(project);
        var stack = new UndoStack(project);

        stack.Execute(Edits.SetTrackState(SequenceId, "track-v2", locked: false, name: "Fondo"));
        Track v2 = Seq(project).GetTrack("track-v2");
        Assert.False(v2.Locked);
        Assert.Equal("Fondo", v2.Name);

        Assert.True(stack.Undo());
        Assert.Equal(before, ProjectDump.Of(project));
        Assert.True(Seq(project).GetTrack("track-v2").Locked);
        Assert.Equal("Video 2", Seq(project).GetTrack("track-v2").Name);

        Assert.True(stack.Redo());
        Assert.False(Seq(project).GetTrack("track-v2").Locked);
        Assert.Equal("Fondo", Seq(project).GetTrack("track-v2").Name);
    }

    [Fact]
    public void PistaQueSigueBloqueada_SusClipsYSuNombreSiguenProtegidos()
    {
        Project project = Create();
        string before = ProjectDump.Of(project);
        var stack = new UndoStack(project);

        Assert.Throws<EditException>(() => stack.Execute(Edits.Move(SequenceId, "clip-3", S(40))));
        Assert.Throws<EditException>(() => stack.Execute(Edits.SetClipEnabled(SequenceId, "clip-3", false)));
        Assert.Throws<EditException>(() => stack.Execute(Edits.SetTrackState(SequenceId, "track-v2", name: "Otra")));
        Assert.Throws<EditException>(() => stack.Execute(Edits.SetTrackState(SequenceId, "track-v2", locked: true, name: "Otra")));

        Assert.Equal(0, stack.Count);
        Assert.Equal(before, ProjectDump.Of(project));
    }

    [Fact]
    public void PistaBloqueada_SePuedeOcultarYSilenciarSinDesbloquear()
    {
        Project project = Create();
        var stack = new UndoStack(project);

        stack.Execute(Edits.SetTrackState(SequenceId, "track-v2", hidden: true, muted: true));

        Track track = Seq(project).GetTrack("track-v2");
        Assert.True(track.Locked);
        Assert.True(track.Hidden);
        Assert.True(track.Muted);

        Assert.True(stack.Undo());
        Assert.False(Seq(project).GetTrack("track-v2").Hidden);
        Assert.False(Seq(project).GetTrack("track-v2").Muted);
    }

    [Theory]
    [InlineData(PlacementMode.Insert, "Insertar")]
    [InlineData(PlacementMode.Overwrite, "Sobrescribir")]
    [InlineData(PlacementMode.Strict, "Colocar clip")]
    public void InsertMedia_DescripcionSegunModoEIdsCreados(PlacementMode mode, string description)
    {
        // En modo estricto el lugar tiene que estar libre: se coloca después de todo.
        double at = mode == PlacementMode.Strict ? 30 : 12;
        SequenceEditCommand command = Check(
            ids => Edits.InsertMedia(SequenceId, "media-1", S(40), S(42), "track-v1", S(at), mode, "track-a1", ids),
            ctx => TimelineEdits.InsertMedia(ctx, "media-1", S(40), S(42), "track-v1", S(at), mode, "track-a1"),
            description,
            null,
            createdCount: 2);
        Sequence seq = Seq(Create());
        Assert.Null(seq.FindClip(command.CreatedIds[0]));
    }

    [Fact]
    public void InsertYOverwrite_SonAtajosDelModo()
    {
        Check(
            ids => Edits.Insert(SequenceId, "media-1", S(40), S(42), "track-v1", S(12), ids: ids),
            ctx => TimelineEdits.InsertMedia(ctx, "media-1", S(40), S(42), "track-v1", S(12), PlacementMode.Insert),
            "Insertar",
            null,
            createdCount: 1);
        Check(
            ids => Edits.Overwrite(SequenceId, "media-1", S(40), S(42), "track-v1", S(12), ids: ids),
            ctx => TimelineEdits.InsertMedia(ctx, "media-1", S(40), S(42), "track-v1", S(12), PlacementMode.Overwrite),
            "Sobrescribir",
            null,
            createdCount: 1);
    }

    [Fact]
    public void AppendMedia_DelegaYDevuelveLosClipsCreados()
    {
        SequenceEditCommand command = Check(
            ids => Edits.AppendMedia(SequenceId, "media-1", S(40), S(42), "track-v1", "track-a1", ids),
            ctx => TimelineEdits.AppendMedia(ctx, "media-1", S(40), S(42), "track-v1", "track-a1"),
            "Añadir al final",
            null,
            createdCount: 2);

        var project = Create();
        new UndoStack(project).Execute(Edits.AppendMedia(SequenceId, "media-1", S(40), S(42), "track-v1", "track-a1", new FreshIds()));
        (Track track, Clip clip) = Seq(project).GetClip(command.CreatedIds[0]);
        Assert.Equal("track-v1", track.Id);
        Assert.Equal(S(15), clip.Start);
    }

    [Fact]
    public void Lift_Delega() =>
        Check(
            ids => Edits.Lift(SequenceId, ["clip-2"], ids),
            ctx => None(() => TimelineEdits.Lift(ctx, ["clip-2"])),
            "Levantar",
            null);

    [Theory]
    [InlineData(RippleScope.Track)]
    [InlineData(RippleScope.AllTracks)]
    public void RippleDelete_Delega(RippleScope scope) =>
        Check(
            ids => Edits.RippleDelete(SequenceId, ["clip-2"], scope, ids),
            ctx => None(() => TimelineEdits.RippleDelete(ctx, ["clip-2"], scope)),
            "Borrado ripple",
            null);

    [Fact]
    public void CloseGap_Delega() =>
        Check(
            ids => Edits.CloseGap(SequenceId, "track-v1", S(7), RippleScope.Track, ids),
            ctx => None(() => TimelineEdits.CloseGap(ctx, "track-v1", S(7), RippleScope.Track)),
            "Cerrar hueco",
            null);

    [Fact]
    public void AddTrack_DevuelveElIdDeLaPistaNueva()
    {
        SequenceEditCommand command = Check(
            ids => Edits.AddTrack(SequenceId, TrackKind.Audio, 1, "Audio 2", ids),
            ctx => [TimelineEdits.AddTrack(ctx, TrackKind.Audio, 1, "Audio 2")],
            "Añadir pista",
            null,
            createdCount: 1);
        Assert.Equal("track-n1", command.CreatedIds[0]);
    }

    [Fact]
    public void RemoveTrack_Delega() =>
        Check(
            ids => Edits.RemoveTrack(SequenceId, "track-a1", ids),
            ctx => None(() => TimelineEdits.RemoveTrack(ctx, "track-a1")),
            "Quitar pista",
            null);

    [Fact]
    public void MoveTrack_Delega() =>
        Check(
            ids => Edits.MoveTrack(SequenceId, "track-v1", 1, ids),
            ctx => None(() => TimelineEdits.MoveTrack(ctx, "track-v1", 1)),
            "Mover pista",
            null);

    [Fact]
    public void SetTrackState_Delega() =>
        Check(
            ids => Edits.SetTrackState(SequenceId, "track-v2", muted: true, locked: false, ids: ids),
            ctx => None(() => TimelineEdits.SetTrackState(ctx, "track-v2", muted: true, locked: false)),
            "Cambiar pista",
            null);

    [Fact]
    public void SetClipEnabled_DescripcionSegunElValor()
    {
        Check(
            ids => Edits.SetClipEnabled(SequenceId, "clip-1", false, ids),
            ctx => None(() => TimelineEdits.SetClipEnabled(ctx, "clip-1", false)),
            "Desactivar clip",
            null);
        Assert.Equal("Activar clip", Edits.SetClipEnabled(SequenceId, "clip-1", true).Description);
    }

    [Fact]
    public void LinkClips_DevuelveElIdDelEnlace()
    {
        SequenceEditCommand command = Check(
            ids => Edits.LinkClips(SequenceId, ["clip-2", "clip-4"], ids),
            ctx => [TimelineEdits.LinkClips(ctx, ["clip-2", "clip-4"])],
            "Enlazar",
            null,
            createdCount: 1);
        Assert.StartsWith("link-", command.CreatedIds[0], StringComparison.Ordinal);
    }

    [Fact]
    public void UnlinkClips_Delega() =>
        Check(
            ids => Edits.UnlinkClips(SequenceId, "clip-1", ids),
            ctx => None(() => TimelineEdits.UnlinkClips(ctx, "clip-1")),
            "Desenlazar",
            null);

    [Fact]
    public void LaSeleccionSeCopiaAlCrearElComando()
    {
        Project project = Create();
        var selection = new List<string> { "clip-2" };
        SequenceEditCommand command = Edits.Lift(SequenceId, selection);
        selection.Clear();
        selection.Add("clip-1");

        new UndoStack(project).Execute(command);
        Assert.Null(Seq(project).FindClip("clip-2"));
        Assert.NotNull(Seq(project).FindClip("clip-1"));
    }

    [Fact]
    public void SinGenerador_UsaGuids()
    {
        Project project = Create();
        SequenceEditCommand command = Edits.Split(SequenceId, "clip-2", S(12));
        new UndoStack(project).Execute(command);
        string id = Assert.Single(command.CreatedIds);
        Assert.Matches("^clip-[0-9a-f]{32}$", id);
    }

    [Fact]
    public void UnaEdicionImposible_LanzaEditExceptionYNoQuedaEnLaPila()
    {
        Project project = Create();
        string before = ProjectDump.Of(project);
        var stack = new UndoStack(project);

        Assert.Throws<EditException>(() => stack.Execute(Edits.Move(SequenceId, "clip-2", S(1))));
        Assert.Throws<EditException>(() => stack.Execute(Edits.Split(SequenceId, "clip-3", S(4))));
        Assert.Throws<EditException>(() => stack.Execute(Edits.Trim(SequenceId, "no-existe", TrimEdge.In, S(1))));

        Assert.Equal(0, stack.Count);
        Assert.Equal(before, ProjectDump.Of(project));
    }

    [Fact]
    public void ArrastreDeMovimientos_SeFundeEnUnPasoYDeshacerVuelveAlOrigen()
    {
        Project project = Create();
        string before = ProjectDump.Of(project);
        var stack = new UndoStack(project);

        foreach (double start in new[] { 11.0, 13.5, 18.0, 25.0, 22.0 })
        {
            stack.Execute(Edits.Move(SequenceId, "clip-2", S(start)));
        }

        Assert.Equal(1, stack.Count);
        Assert.Equal("Mover clip", stack.UndoDescription);
        Assert.Equal(S(22), Seq(project).GetClip("clip-2").Clip.Start);
        string after = ProjectDump.Of(project);

        Assert.True(stack.Undo());
        Assert.Equal(before, ProjectDump.Of(project));
        Assert.False(stack.CanUndo);
        Assert.True(stack.Redo());
        Assert.Equal(after, ProjectDump.Of(project));
    }

    [Fact]
    public void ArrastreDeRecorte_SeFundePorBordeYNoConOtroBorde()
    {
        Project project = Create();
        var stack = new UndoStack(project);

        stack.Execute(Edits.Trim(SequenceId, "clip-2", TrimEdge.Out, S(-1)));
        stack.Execute(Edits.Trim(SequenceId, "clip-2", TrimEdge.Out, S(-1)));
        Assert.Equal(1, stack.Count);

        stack.Execute(Edits.Trim(SequenceId, "clip-2", TrimEdge.In, S(1)));
        Assert.Equal(2, stack.Count);

        stack.Execute(Edits.Move(SequenceId, "clip-2", S(30)));
        stack.Execute(Edits.Move(SequenceId, "clip-1", S(6)));
        Assert.Equal(4, stack.Count);

        Clip clip = Seq(project).GetClip("clip-2").Clip;
        Assert.Equal(S(11), clip.SourceIn);
        Assert.Equal(S(13), clip.SourceOut);

        stack.Undo();
        stack.Undo();
        stack.Undo();
        clip = Seq(project).GetClip("clip-2").Clip;
        Assert.Equal(S(10), clip.SourceIn);
        Assert.Equal(S(13), clip.SourceOut);
        stack.Undo();
        Assert.Equal(ProjectDump.Of(Create()), ProjectDump.Of(project));
    }

    [Fact]
    public void OperacionesSinArrastre_NoSeFunden()
    {
        Project project = Create();
        var stack = new UndoStack(project);
        stack.Execute(Edits.SetClipEnabled(SequenceId, "clip-2", false));
        stack.Execute(Edits.SetClipEnabled(SequenceId, "clip-2", true));
        Assert.Equal(2, stack.Count);
    }

    [Fact]
    public void LasFabricasValidanLosArgumentosNulos()
    {
        Assert.Throws<ArgumentNullException>(() => Edits.Split(null!, "clip-1", Time.Zero));
        Assert.Throws<ArgumentNullException>(() => Edits.Lift(SequenceId, null!));
        Assert.Throws<ArgumentNullException>(() => Edits.LinkClips(SequenceId, null!));
    }
}

/// <summary>Identificadores <c>clip-n1</c>, <c>clip-n2</c>… que no chocan con los del proyecto de prueba.</summary>
internal sealed class FreshIds : IIdGenerator
{
    private int _next;

    public string NewId(string prefix) =>
        $"{prefix}-n{(++_next).ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}
