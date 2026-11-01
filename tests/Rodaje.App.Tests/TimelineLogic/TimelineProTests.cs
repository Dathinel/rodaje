using Rodaje.App.Services;
using Rodaje.App.Services.Titles;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Timeline;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.TimelineLogic;

/// <summary>Grupos al arrastrar, recorte del borde elegido con el teclado, empujones, rango, subclips soltados y avisos de desfase.</summary>
public sealed class TimelineProTests
{
    private static Time S(double seconds) => Time.FromSeconds(seconds);

    /// <summary>Pista principal: a [0,10), b [10,20), c [20,25), con su audio enlazado en «Audio 1».</summary>
    private static async Task<EditorSession> ThreeClips()
    {
        var prober = new FakeProber().Video("a.mp4", 10).Video("b.mp4", 10).Video("c.mp4", 5).Video("muda.mp4", 8, audio: false);
        var session = new EditorSession(new FakePlayback(), prober, new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4"), Path.GetFullPath("b.mp4"), Path.GetFullPath("c.mp4")]);
        await session.ImportMediaAsync([Path.GetFullPath("muda.mp4")]);
        return session;
    }

    private static string Layout(EditorSession session, Track track) =>
        string.Join(" ", track.Clips.Select(c => FormattableString.Invariant($"{session.Project.GetMedia(c.MediaId).Name}@{c.Start.ToSeconds():0.##}-{c.End.ToSeconds():0.##}")));

    private static string Muted(EditorSession session) => session.Project.Media.First(m => m.Name == "muda").Id;

    [Fact]
    public void Un_clip_agrupado_arrastra_a_su_grupo_en_un_paso() => UiThread.Run(async () =>
    {
        EditorSession session = await ThreeClips();
        if (session.Sequence.VideoTracks.Count < 2)
        {
            Assert.True(session.Execute(Edits.AddTrack(session.SequenceId, TrackKind.Video, 1, "Video 2")));
        }

        Track v2 = session.Sequence.VideoTracks[1];
        string muted = Muted(session);
        Assert.True(session.Execute(Edits.Overwrite(session.SequenceId, muted, Time.Zero, S(2), v2.Id, S(1))));
        Assert.True(session.Execute(Edits.Overwrite(session.SequenceId, muted, S(2), S(4), v2.Id, S(6))));
        session.Select(v2.Clips.Select(c => c.Id));
        Assert.True(session.GroupSelection());
        int steps = session.History.Count;

        var options = new DragOptions { PixelsPerSecond = 100, MagneticTrackId = session.MainTrack!.Id };
        var drag = new ClipDragSession(session, new ClipDrag(session.Project, session.SequenceId, v2.Clips[0].Id, DragOperation.Move, S(1.5), options, null));
        drag.Begin();
        drag.Move(S(2.5), null);
        drag.Move(S(4.5), null);
        drag.Complete();

        Assert.Equal("muda@4-6 muda@9-11", Layout(session, v2));
        Assert.Equal(steps + 1, session.History.Count);
        session.Undo();
        Assert.Equal("muda@1-3 muda@6-8", Layout(session, v2));
    });

    [Fact]
    public void El_borde_elegido_se_recorta_un_cuadro_y_el_empujon_corre_la_seleccion() => UiThread.Run(async () =>
    {
        EditorSession session = await ThreeClips();
        session.Timeline.MagneticMainTrack = false;
        Clip b = session.MainTrack!.Clips[1];
        Time frame = Time.FromFrames(1, session.Sequence.FrameRate);

        session.SelectedEdge = new SelectedEdge(b.Id, TrimEdge.Out, Ripple: false);
        Assert.True(session.TrimSelectedEdge(-1));
        Time end = session.Sequence.GetClip(b.Id).Clip.End;
        Assert.InRange(end.Ticks, (S(20) - frame - frame).Ticks, S(20).Ticks - 1);

        session.SelectedEdge = new SelectedEdge(b.Id, TrimEdge.Out, Ripple: true);
        Assert.True(session.TrimSelectedEdge(-2));
        // El recorte simple dejó un cuadro de hueco; el ripple corre «c» lo mismo que se acorta «b», así que el hueco sigue.
        Clip c = session.MainTrack.Clips[2];
        Time gap = c.Start - session.Sequence.GetClip(b.Id).Clip.End;
        Assert.Equal(S(20) - end, gap);

        Time before = c.Start;
        session.Select([c.Id]);
        Assert.True(session.NudgeSelection(3));
        Assert.Equal(before + Time.FromFrames(3, session.Sequence.FrameRate), session.MainTrack.Clips[2].Start);
        Assert.Equal(session.MainTrack.Clips[2].Start, session.Sequence.AudioTracks[0].Clips[2].Start);
    });

    [Fact]
    public void Cerrar_los_huecos_y_marcar_un_rango() => UiThread.Run(async () =>
    {
        EditorSession session = await ThreeClips();
        session.Timeline.MagneticMainTrack = false;
        Assert.True(session.Execute(Edits.Lift(session.SequenceId, [session.MainTrack!.Clips[1].Id])));
        Assert.Equal("a@0-10 c@20-25", Layout(session, session.MainTrack));

        Assert.True(session.CloseAllGaps());
        Assert.Equal("a@0-10 c@10-15", Layout(session, session.MainTrack));
        Assert.Equal("a@0-10 c@10-15", Layout(session, session.Sequence.AudioTracks[0]));

        session.SetSequenceRange(S(2), S(5));
        Assert.Equal((S(2), S(5)), (session.SequenceIn!.Value, session.SequenceOut!.Value));
        session.SetSequenceRange(S(1), S(1));
        Assert.Null(session.SequenceIn);
    });

    [Fact]
    public void Un_subclip_soltado_entra_con_su_tramo()
    {
        string text = MediaDrop.Format("m1", (S(2), S(5)));
        Assert.Equal(new MediaDrop("m1", (S(2), S(5))), MediaDrop.Parse(text));
        Assert.Equal(new MediaDrop("m1", null), MediaDrop.Parse("m1"));
        Assert.Equal(new MediaDrop("m1", null), MediaDrop.Parse("m1|9|3"));
        Assert.Null(MediaDrop.Parse(""));
    }

    [Theory]
    [InlineData(3.0, "+3")]
    [InlineData(-12.4, "−12")]
    [InlineData(0.5, "+0,5")]
    [InlineData(-0.04, "±0,0")]
    public void El_desfase_se_escribe_en_cuadros(double frames, string expected) => Assert.Equal(expected, ClipLabels.SyncFrames(frames));

    [Fact]
    public void Una_pista_con_alto_propio_lo_usa_en_la_disposicion() => UiThread.Run(async () =>
    {
        EditorSession session = await ThreeClips();
        Track audio = session.Sequence.AudioTracks[0];
        Assert.True(session.Execute(Edits.SetTrackHeight(session.SequenceId, audio.Id, 120)));

        TrackRow row = TrackLayout.Build(session.Sequence, session.MainTrack).Find(audio.Id)!;
        Assert.Equal(120, row.Height);
    });

    [Fact]
    public void Con_ignorar_grupos_el_empujon_mueve_solo_el_seleccionado() => UiThread.Run(async () =>
    {
        EditorSession session = await ThreeClips();
        session.Timeline.IgnoreGroups = true;
        Clip audio = session.Sequence.AudioTracks[0].Clips[2];
        session.Select([audio.Id]);

        Assert.True(session.NudgeSelection(3));

        Assert.Equal(S(20) + Time.FromFrames(3, session.Sequence.FrameRate), session.Sequence.AudioTracks[0].Clips[2].Start);
        Assert.Equal(S(20), session.MainTrack!.Clips[2].Start);
    });

    [Fact]
    public void Con_ignorar_grupos_Supr_borra_solo_el_seleccionado_y_la_pareja_sigue_enlazada() => UiThread.Run(async () =>
    {
        EditorSession session = await ThreeClips();
        session.Timeline.IgnoreGroups = true;
        Clip video = session.MainTrack!.Clips[1];
        session.Select([session.Sequence.AudioTracks[0].Clips[1].Id]);

        Assert.True(session.DeleteSelection());

        Assert.Equal("a@0-10 b@10-20 c@20-25", Layout(session, session.MainTrack));
        Assert.Equal("a@0-10 c@20-25", Layout(session, session.Sequence.AudioTracks[0]));
        Assert.NotNull(session.Sequence.GetClip(video.Id).Clip.LinkId);
        session.Undo();
        Assert.Equal("a@0-10 b@10-20 c@20-25", Layout(session, session.Sequence.AudioTracks[0]));
    });
}
