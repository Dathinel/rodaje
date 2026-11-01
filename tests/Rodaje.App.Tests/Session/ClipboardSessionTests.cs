using Rodaje.App.Services;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Probe;

namespace Rodaje.App.Tests.Session;

/// <summary>Pegar en el cursor y pegar insertando desde la sesión, también en otro proyecto que no tiene los medios.</summary>
public class ClipboardSessionTests
{
    private static Time S(double seconds) => Time.FromSeconds(seconds);

    private static void At(EditorSession session, double seconds) =>
        session.UpdateTransport(PlaybackSnapshot.Empty with { Position = S(seconds) });

    /// <summary>Una sesión con dos videos reales (archivos vacíos en una carpeta temporal) en la línea de tiempo.</summary>
    private static async Task<(EditorSession Session, FakeDialogs Dialogs, string A, string B)> TwoClips(TempDirectory folder)
    {
        string a = folder.File("a.mp4");
        string b = folder.File("b.mp4");
        File.WriteAllBytes(a, []);
        File.WriteAllBytes(b, []);
        var dialogs = new FakeDialogs { Choice = UnsavedChangesChoice.Discard };
        var session = new EditorSession(new FakePlayback(), new FakeProber().Video(a).Video(b), dialogs, new SequentialIdGenerator());
        await session.OpenFilesAsync([a, b]);
        return (session, dialogs, a, b);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PasteDiscardsTheImportIfThePlayheadMovesWhileReading(bool insert) => UiThread.Run(async () =>
    {
        using var folder = new TempDirectory();
        string path = folder.File("copied.mp4");
        File.WriteAllBytes(path, []);
        var prober = new PausedProber(new FakeProber().Video(path));
        var session = new EditorSession(new FakePlayback(), prober,
            new FakeDialogs { Choice = UnsavedChangesChoice.Discard }, new SequentialIdGenerator());
        await session.OpenFilesAsync([path]);
        session.Select([session.MainTrack!.Clips[0].Id]);
        Assert.True(session.CopySelection());
        Assert.True(await session.NewProjectAsync());
        int steps = session.History.Count;
        string[] selection = [.. session.SelectedClipIds];
        var notices = new List<EditorNotification>();
        session.Notified += (_, notice) => notices.Add(notice);
        prober.Pause();

        Task<bool> pending = session.PasteAtPlayheadAsync(insert);
        try
        {
            Assert.False(pending.IsCompleted);
            At(session, 5);
        }
        finally
        {
            prober.Release();
        }

        Assert.False(await pending);
        Assert.Empty(session.Project.Media);
        Assert.Empty(session.MainTrack!.Clips);
        Assert.Equal(steps, session.History.Count);
        Assert.Equal(selection, session.SelectedClipIds);
        Assert.Equal("No se pudo pegar", Assert.Single(notices).Title);
    });

    private sealed class PausedProber(IMediaProber inner) : IMediaProber
    {
        private TaskCompletionSource? _gate;

        public void Pause() => _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate?.TrySetResult();

        public MediaProbeResult Probe(string path)
        {
            if (_gate is { } gate && !gate.Task.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("La prueba no soltó la importación.");
            }

            return inner.Probe(path);
        }
    }

    [Fact]
    public void Pegar_insertando_corre_lo_que_sigue_y_es_un_paso_del_historial() => UiThread.Run(async () =>
    {
        using var folder = new TempDirectory();
        (EditorSession session, _, _, _) = await TwoClips(folder);
        string first = session.MainTrack!.Clips[0].Id;
        session.Select([first]);
        Assert.True(session.CopySelection());
        At(session, 10);

        Assert.True(await session.PasteAtPlayheadAsync(insert: true));

        Assert.Equal([S(0), S(10), S(20)], session.MainTrack!.Clips.Select(c => c.Start));
        Assert.Equal(3, session.Sequence.AudioTracks[0].Clips.Count);
        Assert.Equal("Pegar insertando", session.UndoDescription);
        Assert.Equal(2, session.SelectedClipIds.Count);
        session.Undo();
        Assert.Equal([S(0), S(10)], session.MainTrack!.Clips.Select(c => c.Start));
    });

    [Fact]
    public void Pegar_en_otro_proyecto_importa_el_medio_por_su_archivo() => UiThread.Run(async () =>
    {
        using var folder = new TempDirectory();
        (EditorSession session, _, string a, _) = await TwoClips(folder);
        session.Select([session.MainTrack!.Clips[0].Id]);
        Assert.True(session.CopySelection());
        Assert.True(await session.NewProjectAsync());
        Assert.Empty(session.Project.Media);
        var notices = new List<EditorNotification>();
        session.Notified += (_, n) => notices.Add(n);

        Assert.True(await session.PasteAtPlayheadAsync());

        MediaRef media = Assert.Single(session.Project.Media);
        Assert.Equal(a, media.Path);
        Assert.Equal(S(10), media.Duration);
        Clip pasted = Assert.Single(session.MainTrack!.Clips);
        Assert.Equal(media.Id, pasted.MediaId);
        Assert.Equal(media.Id, Assert.Single(session.Sequence.AudioTracks[0].Clips).MediaId);
        Assert.Equal("Pegar en el cursor", session.UndoDescription);
        Assert.DoesNotContain(notices, n => n.Severity == NotificationSeverity.Warning);

        // Importar fue un paso aparte: al deshacer el pegado, el medio sigue en la biblioteca.
        session.Undo();
        Assert.Empty(session.MainTrack!.Clips);
        Assert.Single(session.Project.Media);
        Assert.Equal("Importar medio", session.UndoDescription);
    });

    [Fact]
    public void Pegar_en_otro_proyecto_que_ya_tiene_el_archivo_usa_ese_medio_sin_repetirlo() => UiThread.Run(async () =>
    {
        using var folder = new TempDirectory();
        (EditorSession session, _, string a, string b) = await TwoClips(folder);
        session.Select([session.MainTrack!.Clips[1].Id]);
        Assert.True(session.CopySelection());
        Assert.True(await session.NewProjectAsync());
        await session.ImportMediaAsync([b]);
        string existing = Assert.Single(session.Project.Media).Id;
        int steps = session.History.Count;

        Assert.True(await session.PasteAtPlayheadAsync());

        Assert.Single(session.Project.Media);
        Assert.Equal(existing, Assert.Single(session.MainTrack!.Clips).MediaId);
        Assert.Equal(steps + 1, session.History.Count);
        Assert.NotEqual(a, session.Project.Media[0].Path);
    });

    [Fact]
    public void Pegar_en_otro_proyecto_avisa_si_el_archivo_ya_no_existe_y_no_pega_nada() => UiThread.Run(async () =>
    {
        using var folder = new TempDirectory();
        (EditorSession session, _, string a, _) = await TwoClips(folder);
        session.Select([session.MainTrack!.Clips[0].Id]);
        Assert.True(session.CopySelection());
        Assert.True(await session.NewProjectAsync());
        File.Delete(a);
        var notices = new List<EditorNotification>();
        session.Notified += (_, n) => notices.Add(n);
        int steps = session.History.Count;

        Assert.False(await session.PasteAtPlayheadAsync());

        EditorNotification notice = Assert.Single(notices);
        Assert.Equal(NotificationSeverity.Warning, notice.Severity);
        Assert.Equal("No se pudo pegar", notice.Title);
        Assert.Contains("a.mp4", notice.Message, StringComparison.Ordinal);
        Assert.Contains("no existe", notice.Message, StringComparison.Ordinal);
        Assert.Empty(session.Project.Media);
        Assert.Empty(session.MainTrack!.Clips);
        Assert.Equal(steps, session.History.Count);
    });
}
