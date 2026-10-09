using FotoToma.Core.Commands;
using FotoToma.Core.Editing;
using FotoToma.Core.Model;
using static FotoToma.Core.Tests.Commands.CommandFixtures;

namespace FotoToma.Core.Tests.Commands;

public class SequenceEditCommandTests
{
    private static SequenceEditCommand MoveClip(string clipId, double start, string? mergeKey = null) =>
        new("Mover clip", SequenceId, ctx =>
        {
            (Track track, Clip clip) = ctx.Sequence.GetClip(clipId);
            clip.Start = S(start);
            track.SortClips();
        }, mergeKey: mergeKey);

    private static SequenceEditCommand AddClip(IIdGenerator ids, double start) =>
        new("Agregar clip", SequenceId, ctx =>
        {
            string id = ctx.Ids.NewId("nuevo");
            Track track = ctx.Sequence.GetTrack("track-v1");
            track.Clips.Add(NewClip(id, start));
            track.SortClips();
            return [id];
        }, ids);

    [Fact]
    public void Apply_CorreLaOperacionYRevertDejaTodoIgual()
    {
        Project project = Create();
        string before = ProjectDump.Of(project);
        SequenceEditCommand command = MoveClip("clip-1", 6);
        Assert.False(command.HasRun);
        Assert.Empty(command.CreatedIds);

        command.Apply(project);
        string after = ProjectDump.Of(project);
        Assert.True(command.HasRun);
        Assert.Equal(S(6), Seq(project).GetClip("clip-1").Clip.Start);
        Assert.NotEqual(before, after);

        command.Revert(project);
        Assert.Equal(before, ProjectDump.Of(project));

        command.Apply(project);
        Assert.Equal(after, ProjectDump.Of(project));
    }

    [Fact]
    public void DeshacerYRehacer_ConservanLosObjetosDePista()
    {
        Project project = Create();
        Sequence seq = Seq(project);
        Track v1 = seq.VideoTracks[0];
        Track v2 = seq.VideoTracks[1];
        Track a1 = seq.AudioTracks[0];
        var command = new SequenceEditCommand("Quitar pista", SequenceId, ctx => ctx.Sequence.AudioTracks.Clear());

        command.Apply(project);
        Assert.Empty(seq.AudioTracks);
        command.Revert(project);
        Assert.Same(a1, Assert.Single(seq.AudioTracks));
        command.Apply(project);
        Assert.Empty(seq.AudioTracks);
        command.Revert(project);

        Assert.Same(v1, seq.VideoTracks[0]);
        Assert.Same(v2, seq.VideoTracks[1]);
        Assert.Same(a1, seq.AudioTracks[0]);
    }

    [Fact]
    public void Rehacer_NoVuelveACorrerLaOperacionYLosIdsSeMantienen()
    {
        Project project = Create();
        var ids = new SequentialIdGenerator();
        int runs = 0;
        var command = new SequenceEditCommand("Agregar clip", SequenceId, ctx =>
        {
            runs++;
            string id = ctx.Ids.NewId("nuevo");
            ctx.Sequence.GetTrack("track-v1").Clips.Add(NewClip(id, 20));
            return [id];
        }, ids);

        command.Apply(project);
        Assert.Equal(["nuevo-1"], command.CreatedIds);
        command.Revert(project);
        Assert.Null(Seq(project).FindClip("nuevo-1"));
        command.Apply(project);
        command.Revert(project);
        command.Apply(project);

        Assert.Equal(1, runs);
        Assert.Equal(["nuevo-1"], command.CreatedIds);
        Assert.NotNull(Seq(project).FindClip("nuevo-1"));
    }

    [Fact]
    public void Apply_UsaGuidSiNoSeDaGenerador()
    {
        Project project = Create();
        var command = new SequenceEditCommand("Agregar", SequenceId, ctx => [ctx.Ids.NewId("clip")]);

        command.Apply(project);

        Assert.StartsWith("clip-", Assert.Single(command.CreatedIds), StringComparison.Ordinal);
        Assert.Equal(37, command.CreatedIds[0].Length);
    }

    [Fact]
    public void Apply_SiLaOperacionFallaAMitadRestauraYRelanzaLaMismaExcepcion()
    {
        Project project = Create();
        Sequence seq = Seq(project);
        Track v1 = seq.VideoTracks[0];
        string before = ProjectDump.Of(project);
        var boom = new InvalidOperationException("a mitad");
        var command = new SequenceEditCommand("Romper", SequenceId, ctx =>
        {
            ctx.Sequence.Name = "cambiada";
            ctx.Sequence.VideoTracks[0].Clips.Clear();
            ctx.Sequence.VideoTracks.RemoveAt(0);
            throw boom;
        });

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() => command.Apply(project));

        Assert.Same(boom, thrown);
        Assert.Equal(before, ProjectDump.Of(project));
        Assert.Same(v1, seq.VideoTracks[0]);
        Assert.False(command.HasRun);
        Assert.Throws<InvalidOperationException>(() => command.Revert(project));
    }

    [Fact]
    public void Apply_SiLaOperacionLanzaEditExceptionTambienRestaura()
    {
        Project project = Create();
        string before = ProjectDump.Of(project);
        var command = new SequenceEditCommand("Validar", SequenceId, ctx =>
        {
            // Pone clip-2 encima de clip-1: la validación detecta el solape.
            ctx.Sequence.GetClip("clip-2").Clip.Start = S(1);
            ctx.Sequence.GetTrack("track-v1").SortClips();
            ctx.EnsureValid();
        });

        Assert.Throws<EditException>(() => command.Apply(project));
        Assert.Equal(before, ProjectDump.Of(project));
    }

    [Fact]
    public void Apply_RechazaCambiosEnUnaPistaBloqueada()
    {
        Project project = Create();
        string before = ProjectDump.Of(project);
        var command = new SequenceEditCommand("Mover", SequenceId, ctx =>
        {
            ctx.Sequence.GetClip("clip-1").Clip.Start = S(30);
            ctx.Sequence.GetClip("clip-3").Clip.Start = S(40);
        });

        EditException error = Assert.Throws<EditException>(() => command.Apply(project));

        Assert.Contains("«Video 2» está bloqueada", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, ProjectDump.Of(project));
        Assert.False(command.HasRun);
    }

    [Fact]
    public void Apply_RechazaQuitarUnaPistaBloqueada()
    {
        Project project = Create();
        string before = ProjectDump.Of(project);
        var command = new SequenceEditCommand("Quitar", SequenceId, ctx => ctx.Sequence.VideoTracks.RemoveAt(1));

        Assert.Throws<EditException>(() => command.Apply(project));
        Assert.Equal(before, ProjectDump.Of(project));
    }

    [Fact]
    public void Apply_RechazaCambiosEnUnaPistaDeAudioBloqueada()
    {
        Project project = Create();
        Seq(project).AudioTracks[0].Locked = true;
        var command = new SequenceEditCommand("Silenciar clip", SequenceId, ctx => ctx.Sequence.GetClip("clip-4").Clip.Enabled = false);

        Assert.Throws<EditException>(() => command.Apply(project));
        Assert.True(Seq(project).GetClip("clip-4").Clip.Enabled);
    }

    [Fact]
    public void Apply_PermiteCambiarMarcasYOrdenDeUnaPistaBloqueada()
    {
        Project project = Create();
        Sequence seq = Seq(project);
        Track locked = seq.VideoTracks[1];
        var command = new SequenceEditCommand("Desbloquear y subir", SequenceId, ctx =>
        {
            Track track = ctx.Sequence.GetTrack("track-v2");
            track.Locked = false;
            track.Muted = true;
            track.Hidden = true;
            ctx.Sequence.VideoTracks.Reverse();
        });

        command.Apply(project);
        Assert.Same(locked, seq.VideoTracks[0]);
        Assert.False(locked.Locked);

        command.Revert(project);
        Assert.Same(locked, seq.VideoTracks[1]);
        Assert.True(locked.Locked);
        Assert.False(locked.Muted);
    }

    [Fact]
    public void Apply_PermiteEditarUnaPistaQueSoloSeBloqueaDuranteLaOperacion()
    {
        Project project = Create();
        var command = new SequenceEditCommand("Editar y bloquear", SequenceId, ctx =>
        {
            Track track = ctx.Sequence.GetTrack("track-a1");
            track.Clips[0].Start = S(1);
            track.Locked = true;
        });

        command.Apply(project);

        Assert.Equal(S(1), Seq(project).GetClip("clip-4").Clip.Start);
    }

    [Fact]
    public void Apply_SiLaOperacionDesbloqueaLaPista_PermiteCualquierCambioEnLaMismaLlamada()
    {
        Project project = Create();
        string before = ProjectDump.Of(project);
        var command = new SequenceEditCommand("Desbloquear y editar", SequenceId, ctx =>
        {
            Track track = ctx.Sequence.GetTrack("track-v2");
            track.Locked = false;
            track.Name = "Fondo";
            track.Clips[0].Start = S(30);
        });

        command.Apply(project);
        Track v2 = Seq(project).GetTrack("track-v2");
        Assert.Equal("Fondo", v2.Name);
        Assert.Equal(S(30), v2.Clips[0].Start);

        command.Revert(project);
        Assert.Equal(before, ProjectDump.Of(project));
    }

    [Fact]
    public void Apply_SiLaOperacionDesbloqueaLaPista_PermiteQuitarla()
    {
        Project project = Create();
        var command = new SequenceEditCommand("Desbloquear y quitar", SequenceId, ctx =>
        {
            Track track = ctx.Sequence.GetTrack("track-v2");
            track.Locked = false;
            ctx.Sequence.VideoTracks.Remove(track);
        });

        command.Apply(project);
        Assert.Null(Seq(project).FindTrack("track-v2"));
    }

    [Fact]
    public void Apply_ElNombreNoEsContenidoVigilado_PeroLosClipsDeUnaPistaQueSigueBloqueadaSi()
    {
        // El nombre de una pista que sigue bloqueada lo protege la operación (SetTrackState), no esta red.
        Project project = Create();
        var rename = new SequenceEditCommand("Renombrar", SequenceId, ctx => ctx.Sequence.GetTrack("track-v2").Name = "Otra");
        rename.Apply(project);
        Assert.Equal("Otra", Seq(project).GetTrack("track-v2").Name);

        var edit = new SequenceEditCommand("Editar", SequenceId, ctx =>
        {
            Track track = ctx.Sequence.GetTrack("track-v2");
            track.Name = "Tercera";
            track.Clips[0].Enabled = false;
        });
        Assert.Throws<EditException>(() => edit.Apply(project));
        Assert.Equal("Otra", Seq(project).GetTrack("track-v2").Name);
    }

    [Fact]
    public void ApplyYRevert_FallanSiLaSecuenciaNoExiste()
    {
        Project project = Create();
        var command = new SequenceEditCommand("x", "no-existe", _ => { });
        EditException error = Assert.Throws<EditException>(() => command.Apply(project));
        Assert.Contains("no-existe", error.Message, StringComparison.Ordinal);

        SequenceEditCommand applied = MoveClip("clip-1", 6);
        applied.Apply(project);
        project.Sequences.Clear();
        Assert.Throws<EditException>(() => applied.Revert(project));
        Assert.Throws<EditException>(() => applied.Apply(project));
    }

    [Fact]
    public void Constructores_RechazanNulos()
    {
        Action<EditContext> action = _ => { };
        Func<EditContext, IReadOnlyList<string>> func = _ => [];
        Assert.Throws<ArgumentNullException>(() => new SequenceEditCommand(null!, SequenceId, action));
        Assert.Throws<ArgumentNullException>(() => new SequenceEditCommand("x", null!, action));
        Assert.Throws<ArgumentNullException>(() => new SequenceEditCommand("x", SequenceId, (Action<EditContext>)null!));
        Assert.Throws<ArgumentNullException>(() => new SequenceEditCommand("x", SequenceId, (Func<EditContext, IReadOnlyList<string>>)null!));
        Assert.Throws<ArgumentNullException>(() => new SequenceEditCommand("x", SequenceId, func).Apply(null!));
        Assert.Throws<ArgumentNullException>(() => new SequenceEditCommand("x", SequenceId, func).Revert(null!));
    }

    [Fact]
    public void Propiedades_ReflejanElConstructor()
    {
        var command = new SequenceEditCommand("Cortar", SequenceId, _ => { }, mergeKey: "k");
        Assert.Equal("Cortar", command.Description);
        Assert.Equal(SequenceId, command.SequenceId);
        Assert.Equal("k", command.MergeKey);
    }

    [Fact]
    public void Apply_ToleraOperacionQueDevuelveNulo()
    {
        Project project = Create();
        var command = new SequenceEditCommand("x", SequenceId, _ => (IReadOnlyList<string>)null!);
        command.Apply(project);
        Assert.Empty(command.CreatedIds);
    }

    [Fact]
    public void TryMerge_FundeArrastresConLaMismaClave()
    {
        Project project = Create();
        string before = ProjectDump.Of(project);
        SequenceEditCommand first = MoveClip("clip-1", 6, "arrastre");
        SequenceEditCommand second = MoveClip("clip-1", 7, "arrastre");
        first.Apply(project);
        second.Apply(project);
        string after = ProjectDump.Of(project);

        Assert.True(first.TryMerge(second));

        first.Revert(project);
        Assert.Equal(before, ProjectDump.Of(project));
        first.Apply(project);
        Assert.Equal(after, ProjectDump.Of(project));
    }

    [Fact]
    public void TryMerge_JuntaLosIdsCreados()
    {
        Project project = Create();
        var ids = new SequentialIdGenerator();
        Func<EditContext, IReadOnlyList<string>> add = ctx =>
        {
            string id = ctx.Ids.NewId("c");
            ctx.Sequence.GetTrack("track-v1").Clips.Add(NewClip(id, 30));
            return [id];
        };
        var first = new SequenceEditCommand("Agregar", SequenceId, add, ids, "k");
        var second = new SequenceEditCommand("Agregar", SequenceId, add, ids, "k");
        first.Apply(project);
        second.Apply(project);

        Assert.True(first.TryMerge(second));
        Assert.Equal(["c-1", "c-2"], first.CreatedIds);
    }

    [Fact]
    public void TryMerge_RechazaLosCasosQueNoCorresponden()
    {
        Project project = Create();
        project.Sequences.Add(new Sequence { Id = "seq-2" });

        SequenceEditCommand applied(string? key, string seq = SequenceId)
        {
            var c = new SequenceEditCommand("x", seq, _ => { }, mergeKey: key);
            c.Apply(project);
            return c;
        }

        SequenceEditCommand withKey = applied("k");
        Assert.False(withKey.TryMerge(new FakeCommand("otro")));
        Assert.False(withKey.TryMerge(withKey));
        Assert.False(applied(null).TryMerge(applied(null)));
        Assert.False(withKey.TryMerge(applied("otra")));
        Assert.False(withKey.TryMerge(applied("k", "seq-2")));
        Assert.False(withKey.TryMerge(new SequenceEditCommand("x", SequenceId, _ => { }, mergeKey: "k")));
        Assert.False(new SequenceEditCommand("x", SequenceId, _ => { }, mergeKey: "k").TryMerge(applied("k")));
        Assert.True(withKey.TryMerge(applied("k")));
    }

    [Fact]
    public void AddClip_ConGeneradorSecuencialDaIdsPredecibles()
    {
        Project project = Create();
        var ids = new SequentialIdGenerator();
        SequenceEditCommand a = AddClip(ids, 20);
        SequenceEditCommand b = AddClip(ids, 25);
        a.Apply(project);
        b.Apply(project);

        Assert.Equal(["nuevo-1"], a.CreatedIds);
        Assert.Equal(["nuevo-2"], b.CreatedIds);
    }
}
