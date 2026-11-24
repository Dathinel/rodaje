using Rodaje.App.Services;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Timeline;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Session;

/// <summary>«Fundido de entrada hasta el cursor» y «Fundido de salida desde el cursor» sobre la selección.</summary>
public class FadeToPlayheadTests
{
    private static Time S(double seconds) => Time.FromSeconds(seconds);

    /// <summary>Un video de 10 s con su audio enlazado en la primera pista de audio.</summary>
    private static async Task<EditorSession> OneClip()
    {
        var prober = new FakeProber().Video("a.mp4", 10);
        var session = new EditorSession(new FakePlayback(), prober, new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4")]);
        return session;
    }

    private static void At(EditorSession session, double seconds) =>
        session.UpdateTransport(PlaybackSnapshot.Empty with { Position = S(seconds), Duration = session.Sequence.Duration });

    [Fact]
    public void ConElVideoElegido_ElAudioEnlazadoFundeHastaElCursor() => UiThread.Run(async () =>
    {
        EditorSession session = await OneClip();
        Clip video = session.MainTrack!.Clips[0];
        Clip audio = session.Sequence.AudioTracks[0].Clips[0];
        session.Select([video.Id]);
        At(session, 1.5);

        Assert.True(session.FadeToPlayhead(FadeHandle.In));
        Assert.Equal((1.5, 0.0), FadeHandles.Seconds(audio));

        At(session, 8);
        Assert.True(session.FadeToPlayhead(FadeHandle.Out));
        Assert.Equal((1.5, 2.0), FadeHandles.Seconds(audio));
        Assert.Equal("Fundido de salida desde el cursor", session.UndoDescription);

        session.Undo();
        // Deshacer restaura la secuencia desde una instantánea: el clip se relee.
        Assert.Equal((1.5, 0.0), FadeHandles.Seconds(session.Sequence.AudioTracks[0].Clips[0]));
    });

    [Fact]
    public void ConElCursorFueraDelClipOSinSeleccion_AvisaYNoCambiaNada() => UiThread.Run(async () =>
    {
        EditorSession session = await OneClip();
        var notices = new List<EditorNotification>();
        session.Notified += (_, n) => notices.Add(n);
        Clip audio = session.Sequence.AudioTracks[0].Clips[0];
        // Abrir el video ya dejó pasos en el historial: lo que se comprueba es que no se añada ninguno.
        int steps = session.HistoryEntries.Count;

        At(session, 3);
        Assert.False(session.FadeToPlayhead(FadeHandle.In));
        Assert.Contains(notices, n => n.Message.Contains("Selecciona", StringComparison.Ordinal));

        session.Select([audio.Id]);
        At(session, 10);
        Assert.False(session.FadeToPlayhead(FadeHandle.Out));
        Assert.Equal((0.0, 0.0), FadeHandles.Seconds(audio));
        Assert.Equal(steps, session.HistoryEntries.Count);
    });
}
