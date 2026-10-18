using Rodaje.App.Services;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using P = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.App.Tests.Inspector;

/// <summary>Con varios clips seleccionados, el inspector cambia a todos en un paso y muestra «—» si difieren.</summary>
public class MultiClipPropertyTests
{
    private const string Transform = BuiltInEffects.Transform;

    private static async Task<(EditorSession Session, ClipPropertyEditor Editor, Clip A, Clip B)> TwoClips()
    {
        var session = new EditorSession(new FakePlayback(), new FakeProber().Video("a.mp4", 10).Video("b.mp4", 10), new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4"), Path.GetFullPath("b.mp4")]);
        Clip a = session.MainTrack!.Clips[0];
        Clip b = session.MainTrack!.Clips[1];
        session.UpdateTransport(PlaybackSnapshot.Empty with { Position = Time.FromSeconds(2), Duration = session.Sequence.Duration });
        return (session, new ClipPropertyEditor(session), a, b);
    }

    private static double Opacity(ClipPropertyEditor editor, EditorSession session, Clip clip) =>
        editor.Read(session.Sequence.GetClip(clip.Id).Clip, Transform, P.Opacity, P.DefaultValue(P.Opacity)).Value;

    [Fact]
    public void CambiarLaOpacidad_VaATodosEnUnPaso_YElValorMixtoSeVe() => UiThread.Run(async () =>
    {
        (EditorSession session, ClipPropertyEditor editor, Clip a, Clip b) = await TwoClips();
        session.Select([a.Id]);
        Assert.True(editor.SetValue(a, Transform, P.Opacity, 0.5, "Cambiar opacidad"));
        session.Select([a.Id, b.Id]);
        (IReadOnlyList<Clip> videos, _) = editor.Targets();
        Assert.Equal(2, videos.Count);
        Assert.True(editor.IsMixed(videos, Transform, P.Opacity, P.DefaultValue(P.Opacity)));
        Assert.False(editor.IsMixed(videos, Transform, P.Scale, P.DefaultValue(P.Scale)));
        int steps = session.History.Count;

        editor.BeginChange(videos, Transform, P.Opacity, "Cambiar opacidad");
        Assert.True(editor.Change(0.7));
        Assert.True(editor.Change(0.8));
        editor.EndChange();

        Assert.Equal(steps + 1, session.History.Count);
        Assert.Equal(0.8, Opacity(editor, session, a), 6);
        Assert.Equal(0.8, Opacity(editor, session, b), 6);
        (videos, _) = editor.Targets();
        Assert.False(editor.IsMixed(videos, Transform, P.Opacity, P.DefaultValue(P.Opacity)));

        session.Undo();
        Assert.Equal(0.5, Opacity(editor, session, a), 6);
        Assert.Equal(1, Opacity(editor, session, b), 6);
    });

    [Fact]
    public void ConTiposMezclados_SoloQuedaLoComun() => UiThread.Run(async () =>
    {
        (EditorSession session, ClipPropertyEditor editor, Clip a, _) = await TwoClips();
        Clip audioB = session.Sequence.AudioTracks[0].Clips[1];
        session.Select([a.Id, audioB.Id]);

        (IReadOnlyList<Clip> videos, IReadOnlyList<Clip> audios) = editor.Targets();

        Assert.Empty(videos);
        Assert.Equal(2, audios.Count);
        Assert.Contains(audioB, audios);
        Assert.True(editor.SetValue(audios, BuiltInEffects.Audio, BuiltInEffects.AudioParameters.GainDb, -6, "Cambiar volumen"));
        Assert.All(audios, c => Assert.Equal(-6,
            editor.Read(session.Sequence.GetClip(c.Id).Clip, BuiltInEffects.Audio, BuiltInEffects.AudioParameters.GainDb, 0).Value, 6));
    });
}
